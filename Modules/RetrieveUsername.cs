using BepInEx.Configuration;
using Facepunch.Steamworks;
using RoR2;
using RoR2.Networking;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Networking;
using Console = RoR2.Console;

namespace R2DSEssentials.Modules
{
    [Module(ModuleName, ModuleDescription, DefaultEnabled)]
    public sealed class RetrieveUsername : R2DSEModule
    {
        public const string ModuleName = nameof(RetrieveUsername);
        public const string ModuleDescription = "Retrieve player usernames through third party website. Don't need a steam api key.";
        public const bool DefaultEnabled = true;

        private ConfigEntry<bool> _enableBlackListRichNames;
        private ConfigEntry<string> _blackListRichNames;
        private ConfigEntry<string> _steamApiKey;
        private ConfigEntry<bool> _prefixUniqueId;

        public static event Action OnUsernameUpdated;

        private static int uniqueId = 0;

        public static readonly Dictionary<ulong, int> UniqueIdCache = new Dictionary<ulong, int>();
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
        }

        protected override void UnHook()
        {
            On.RoR2.NetworkPlayerName.GetResolvedName -= OnGetResolvedName;
            Run.onServerGameOver -= EmptyCachesOnGameOver;
            On.RoR2.Networking.GameNetworkManager.OnServerDisconnect -= RemoveCacheOnPlayerDisconnect;
        }

        protected override void MakeConfig()
        {
            _enableBlackListRichNames = AddConfig("Enable Auto-kick Rich Tag", true,
                "Should the auto-kicker be enabled for people with rich name like oversized names / names with annoying tag");

            _blackListRichNames = AddConfig("Rich Tag Blacklist", "size, style",
                "Blacklist thats used for banning specific tags, only input the tag name in this. Example : size, style, color");

            _prefixUniqueId = AddConfig("Prefix Unique Id", true,
                "Prefixs a Unique Id to the players name, useful for kick systems.");

            _steamApiKey = AddConfig("Steam API Key", "",
                "Set this to your steam API key to use the steam api instead of a website.");

        }

        private string OnGetResolvedName(On.RoR2.NetworkPlayerName.orig_GetResolvedName orig, ref NetworkPlayerName self)
        {
            if (Server.Instance != null)
            {
                return UsernamesCache.TryGetValue(self.steamId.value, out var name) ? name : GetPersonaNameWebAPI(self.steamId.value, ref self);
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
        private string GetPersonaNameWebAPI(ulong steamId, ref NetworkPlayerName self)
        {
            const string unkString = "???";

            if (steamId.ToString().Length != 17)
                return unkString;

            if (!RequestCache.Contains(steamId))
            {
                RequestCache.Add(steamId);
                PluginEntry.Instance.StartCoroutine(WebRequestCoroutine(steamId, self));
            }

            return unkString;
        }

        private IEnumerator WebRequestCoroutine(ulong steamId, NetworkPlayerName self)
        {
            string apiKey = _steamApiKey.Value;
            if (apiKey != "")
            {
                var ioUrlRequest = $"http://api.steampowered.com/ISteamUser/GetPlayerSummaries/v2/?key={apiKey}&steamids={steamId}";

                var webRequest = UnityWebRequest.Get(ioUrlRequest);
                yield return webRequest.SendWebRequest();
                if (!webRequest.isNetworkError)
                {
                    var userNameJson = SimpleJSON.JSONObject.Parse(webRequest.downloadHandler.text);
                    var userName = (string)userNameJson["response"]["players"][0]["personaname"];
                    SyncPlayerName(steamId, self, userName);
                }
                else
                {
                    SyncPlayerName(steamId, self, "???");
                }
                webRequest.Dispose();
            }
            else
            {
                const string regexForLookUp = "<dt class=\"key\">name<\\/dt>\\s*<dd class=\"value\">(.*)<\\/dd>";

                var ioUrlRequest = "https://steamid.io/lookup/" + steamId;

                var webRequest = UnityWebRequest.Get(ioUrlRequest);
                yield return webRequest.SendWebRequest();

                if (!webRequest.isNetworkError)
                {
                    var rx = new Regex(regexForLookUp,
                        RegexOptions.Compiled | RegexOptions.IgnoreCase);

                    var nameFromRegex = rx.Match(webRequest.downloadHandler.text).Groups[1].ToString();

                    if (!nameFromRegex.Equals(""))
                    {
                        var gotBlackListed = false;

                        if (_enableBlackListRichNames.Value)
                        {
                            var blackList = _blackListRichNames.Value.Split(',');

                            foreach (var tag in blackList)
                            {
                                var bannedTag = "&lt;" + tag + "=";
                                if (nameFromRegex.Contains(bannedTag))
                                {
                                    var userToKick = Util.Networking.GetNetworkUserFromSteamId(steamId);
                                    var playerId = Util.Networking.GetPlayerIndexFromNetworkUser(userToKick);

                                    Console.instance.SubmitCmd(null, $"kick {playerId}");
                                    gotBlackListed = true;
                                }
                            }
                        }

                        if (!UsernamesCache.ContainsKey(steamId) && !gotBlackListed)
                        {
                            UsernamesCache.Add(steamId, nameFromRegex);

                            SyncPlayerName(steamId, self, nameFromRegex);
                        }
                    }
                } else
                {
                    SyncPlayerName(steamId, self, "???"); ;
                }

                webRequest.Dispose();
            }
        }

        private void SyncPlayerName(ulong steamId, NetworkPlayerName self, string username)
        {
            NetworkUser networkUser = Util.Networking.GetNetworkUserFromSteamId(steamId);
            if (networkUser != null)
            {
                uniqueId++;
                networkUser.userName = $"[{uniqueId}] {username}";
                self.nameOverride = $"[{uniqueId}] {username}";

                UniqueIdCache.Add(steamId, uniqueId);

                var netId = networkUser.Network_id;
                var tempId = NetworkUserId.FromIp("000.000.000.1", 255);
                networkUser.Network_id = tempId;
                networkUser.SetDirtyBit(1u);

                Logger.LogWarning($"New player : {networkUser.userName} connected. (STEAM:{steamId})");
                OnUsernameUpdated?.Invoke();
                PluginEntry.Instance.StartCoroutine(UpdateUserName(networkUser, netId));
            }
        }
        private static IEnumerator UpdateUserName(NetworkUser networkUser, NetworkUserId netId)
        {
            yield return new WaitForSeconds(0.001f);
            networkUser.Network_id = netId;
            networkUser.SetDirtyBit(1u);
        }
    }
}