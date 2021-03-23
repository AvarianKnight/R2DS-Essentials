using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using BepInEx.Configuration;
using RoR2;
using Facepunch.Steamworks;
using RoR2.Networking;
using UnityEngine;
using UnityEngine.Networking;
using Console = RoR2.Console;

namespace R2DSEssentials.Modules
{
    [Module(ModuleName, ModuleDescription, DefaultEnabled)]
    public sealed class RetrieveUsername : R2DSEModule
    {
        public const string ModuleName = nameof(RetrieveUsername);
        public const string ModuleDescription = "Retrieve player usernames through third party website. Can optionally turn on the usage of the steam API when steamApiKey is set.";
        public const bool DefaultEnabled = true;

        private ConfigEntry<bool> _enableBlackListRichNames;
        private ConfigEntry<bool> _addConnectionIdToName;
        private ConfigEntry<string> _steamApiKey;
        private ConfigEntry<string> _blackListRichNames;

        public static event Action OnUsernameUpdated;

        internal static readonly Dictionary<ulong, string> UsernamesCache = new Dictionary<ulong, string>();
        private static readonly List<ulong> RequestCache = new List<ulong>();

        public RetrieveUsername(string name, string description, bool defaultEnabled) : base(name, description, defaultEnabled)
        {
        }


        protected override void Hook()
        {
            On.RoR2.NetworkPlayerName.GetResolvedName += OnGetResolvedName;
            Run.onServerGameOver += EmptyCachesOnGameOver;
            On.RoR2.Networking.GameNetworkManager.OnServerDisconnect += RemoveCacheOnPlayerDisconnect;
            On.RoR2.Networking.GameNetworkManager.OnServerConnect += OnPlayerServerConnect;
            On.RoR2.Networking.GameNetworkManager.OnServerAddPlayerInternal += AddInternal;
        }


        protected override void UnHook()
        {
            On.RoR2.Networking.GameNetworkManager.OnServerConnect += OnPlayerServerConnect;
            On.RoR2.NetworkPlayerName.GetResolvedName -= OnGetResolvedName;
            Run.onServerGameOver -= EmptyCachesOnGameOver;
            On.RoR2.Networking.GameNetworkManager.OnServerDisconnect -= RemoveCacheOnPlayerDisconnect;
        }

        protected override void MakeConfig()
        {
            _enableBlackListRichNames = AddConfig("Enable Auto-kick Rich Tag", true,
                "Should the auto-kicker be enabled for people with rich name like oversized names / names with annoying tag");

            _addConnectionIdToName = AddConfig("Enable Connection id Prefix", true,
                "Prefixs every players name with their connection id, can be useful for resources that kick players.");

            _blackListRichNames = AddConfig("Rich Tag Blacklist", "size, style",
                "Blacklist thats used for banning specific tags, only input the tag name in this. Example : size, style, color");

            _steamApiKey = AddConfig("Use steam API key (change this to your api key)", "",
                "Instead of using a website to verify a players name, use the actual steam api.");
        }

        private void AddInternal(On.RoR2.Networking.GameNetworkManager.orig_OnServerAddPlayerInternal orig, global::RoR2.Networking.GameNetworkManager self, NetworkConnection conn, short playerControllerId, NetworkReader extraMessageReader)
        {
            Logger.LogWarning("Add Player Internal");
        }

        private void OnPlayerServerConnect(On.RoR2.Networking.GameNetworkManager.orig_OnServerConnect orig, global::RoR2.Networking.GameNetworkManager self, NetworkConnection conn)
        {
            Logger.LogWarning($"{DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond} orig_OnServerConnect");
        }

        private string OnGetResolvedName(On.RoR2.NetworkPlayerName.orig_GetResolvedName orig, ref NetworkPlayerName self)
        {
            if (Server.Instance != null)
            {
                return UsernamesCache.TryGetValue(self.steamId.value, out var name) ? name : GetPersonaNameWebAPI(self.steamId.value);
            }

            return orig(ref self);
        }

        private static void EmptyCachesOnGameOver(Run _, GameEndingDef __)
        {
            UsernamesCache.Clear();
            RequestCache.Clear();
        }

        private static void RemoveCacheOnPlayerDisconnect(On.RoR2.Networking.GameNetworkManager.orig_OnServerDisconnect orig, GameNetworkManager self, NetworkConnection conn)
        {
            var nu = Util.Networking.FindNetworkUserForConnectionServer(conn);

            if (nu != null)
            {
                var steamId = nu.GetNetworkPlayerName().steamId.value;

                if (steamId != 0)
                {
                    UsernamesCache.Remove(steamId);
                    RequestCache.Remove(steamId);
                }
            }

            orig(self, conn);
        }

        // ReSharper disable once InconsistentNaming
        private string GetPersonaNameWebAPI(ulong steamId)
        {
            const string unkString = "???";

            if (steamId.ToString().Length != 17)
                return unkString;

            if (!RequestCache.Contains(steamId))
            {
                RequestCache.Add(steamId);
                PluginEntry.Instance.StartCoroutine(WebRequestCoroutine(steamId));
            }

            return unkString;
        }

        private bool CheckBlackListRichNames(string name, ulong steamId)
        {
            if (_enableBlackListRichNames.Value)
            {
                var blackList = _blackListRichNames.Value.Split(',');

                foreach (var tag in blackList)
                {
                    var bannedTag = "&lt;" + tag + "=";
                    if (name.Contains(bannedTag))
                    {
                        var userToKick = Util.Networking.GetNetworkUserFromSteamId(steamId);
                        var playerId = Util.Networking.GetPlayerIndexFromNetworkUser(userToKick);

                        Console.instance.SubmitCmd(null, $"kick {playerId}");
                        return true;
                    }
                }
                return false;
            }
            else
            {
                return false;
            }
        }

        public void UpdateName(string name, ulong steamId)
        {
            var networkUser = Util.Networking.GetNetworkUserFromSteamId(steamId);

            if (networkUser != null)
            {
                Logger.LogInfo($"New player : {name} connected. (STEAM:{steamId})");
                networkUser.userName = name;

                // Sync with other players by forcing dirty syncVar ?
                SyncNetworkUserVarTest(networkUser, _addConnectionIdToName.Value);

                OnUsernameUpdated?.Invoke();
            }
        }
        // called when web request fails
        public void UpdateNameOnFail(ulong steamId)
        {
            // we still want to update the players name even if it fails.
            var networkUser = Util.Networking.GetNetworkUserFromSteamId(steamId);
            if (networkUser != null)
            {
                SyncNetworkUserVarTest(networkUser, _addConnectionIdToName.Value);

                OnUsernameUpdated?.Invoke();
            }
        }

        private IEnumerator WebRequestCoroutine(ulong steamId)
        {
            Logger.LogWarning($"{DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond} Player has joined.");

            if (_steamApiKey.Value != "")
            {
                var ioUrlRequest = $"http://api.steampowered.com/ISteamUser/GetPlayerSummaries/v2/?key={_steamApiKey}&steam={steamId}";

                var webRequest = UnityWebRequest.Get(ioUrlRequest);
                yield return webRequest.SendWebRequest();
                if (!webRequest.isNetworkError)
                {
                    Logger.LogWarning(webRequest);
                    Logger.LogWarning(webRequest.downloadHandler.text);
                    Logger.LogWarning(SimpleJSON.JSON.Parse(webRequest.downloadHandler.text));
                }
                else
                {
                    UpdateNameOnFail(steamId);
                }
            }
            else
            {
                const string regexForLookUp = "<br>name \\s*<code>(.*)<\\/code>";
                const string regexForPersonaName = "\"personaname\":\"(.*?)\"";

                var ioUrlRequest = "https://steamidfinder.com/lookup/" + steamId;

                var webRequest = UnityWebRequest.Get(ioUrlRequest);
                yield return webRequest.SendWebRequest();

                if (!webRequest.isNetworkError)
                {
                    var rx = new Regex(regexForLookUp,
                        RegexOptions.Compiled | RegexOptions.IgnoreCase);

                    var steamProfileUrl = rx.Match(webRequest.downloadHandler.text).Groups[1].ToString();

                    webRequest = UnityWebRequest.Get(steamProfileUrl);

                    yield return webRequest.SendWebRequest();

                    if (!webRequest.isNetworkError)
                    {
                        rx = new Regex(regexForPersonaName,
                            RegexOptions.Compiled | RegexOptions.IgnoreCase);

                        var nameFromRegex = rx.Match(webRequest.downloadHandler.text).Groups[1].ToString();

                        if (!nameFromRegex.Equals(""))
                        {
                            var gotBlackListed = CheckBlackListRichNames(nameFromRegex, steamId);

                            if (!UsernamesCache.ContainsKey(steamId) && !gotBlackListed)
                            {
                                UsernamesCache.Add(steamId, nameFromRegex);

                                UpdateName(nameFromRegex, steamId);
                            }
                        }
                    }
                }
                else
                {
                    UpdateNameOnFail(steamId);
                }
                webRequest.Dispose();
            }
        }

        private static void SyncNetworkUserVarTest(NetworkUser currentNetworkUser, bool shouldPrefixId)
        {
            if (shouldPrefixId)
            {
                var networkIndex = Util.Networking.GetPlayerIndexFromNetworkUser(currentNetworkUser);
                currentNetworkUser.userName = $"[{networkIndex}] {currentNetworkUser.userName}";
            }
            var tmp = currentNetworkUser.Network_id;
            var nid = NetworkUserId.FromIp("000.000.000.1", 255);
            currentNetworkUser.Network_id = nid;
            currentNetworkUser.SetDirtyBit(1u);
            //UpdateUsername(currentNetworkUser, tmp);
            PluginEntry.Instance.StartCoroutine(UpdateUsername(currentNetworkUser, tmp));
        }

        private static IEnumerator UpdateUsername(NetworkUser userToUpdate, NetworkUserId realId)
        {
            yield return new WaitForSeconds(1);

            userToUpdate.Network_id = realId;
            userToUpdate.SetDirtyBit(1u);
        }
    }
}
