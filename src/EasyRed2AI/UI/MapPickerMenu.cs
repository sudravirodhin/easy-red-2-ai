using System;
using System.Collections.Generic;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace EasyRed2HumanAI
{
    public static partial class VisionPatches
    {
        private struct PickerBattleEntry
        {
            public string DisplayName;
            public string UniqueId;
            public string BattleId;
            public string PreviousBattleId;
            public string SaveDataId;
            public string PreviousSaveDataId;
            public string InvaderTag;
            public string DefenderTag;
            public bool IsVanilla;
            public bool InitialIsUnlocked;
            public bool MetadataResolved;
            public bool UnitCountsResolved;
            public bool FactionsResolved;
            public bool IsFinale;
            public int DefaultMaxAllies;
            public int DefaultMaxAxis;
            public ICampaignTreeBattleData BattleRef;
        }

        private struct PickerCampaignEntry
        {
            public string CampaignName;
            public bool TopologySorted;
            public bool AllMetadataResolved;
            public List<PickerBattleEntry> Battles;
        }

        private struct QueuedPickerEntry
        {
            public PickerBattleEntry Battle;
            public string VisibleName;
        }

        private const int MapPickerRowsPerPage = 15;
        private static readonly List<PickerCampaignEntry> _mapPickerCampaigns = new List<PickerCampaignEntry>(24);
        private static readonly List<QueuedPickerEntry> _mapPickerPlaylist = new List<QueuedPickerEntry>(16);
        private static float _mapPickerBotScale = 1.0f;
        private static bool _mapPickerBotScaleDirty;
        private static bool _mapPickerInitialized;
        private static bool _mapPickerUserPickedBattle;
        private static int _lastCustomCampaignCount = -1;
        private static int _bgPreloadCampaignIdx;
        private static int _selectedCampaignIdx;
        private static int _selectedBattleIdx;
        private static int _campaignPage;
        private static int _battlePage;
        private static int _mpSwitchStage;
        private static float _mpSwitchStageDeadline;
        private static string _mpSwitchDisplayName;
        private static bool _mpSwitchRetriedConnect;

        // MP-only end-of-match anthem cap (~16s + 2s smooth fadeout) so custom long anthems never trap clients on black screen.
        private static IntPtr _mpAnthemOrigSourcePtr;
        private static float _mpAnthemOrigVolume = 1.0f;

        // Persistent Multiplayer transition banner state (survives BattleManager destruction between scenes).
        internal static bool _mpTransitionBannerActive;
        private static float _mpTransitionBannerStartUnscaled;
        private static float _mpTransitionBannerWaitSeconds;
        private static bool _mpTransitionBannerIsClient;
        private static string _mpTransitionBannerMapName = "";
        private static IntPtr _mpTransitionBannerOldBmPtr;
        private static int _mpTransitionBannerLastSec = -1;
        private static string _mpTransitionBannerCachedText = "";

        private static string ResolveNextTransitionMapName(string fallback = null)
        {
            try
            {
                if (!string.IsNullOrEmpty(fallback))
                    return fallback;
                MatchData md = MatchData.data;
                IBattle lb = md != null ? md.GetLoadedBattle(true) : null;
                if (lb != null)
                {
                    string bn = lb.GetBattleName();
                    if (!string.IsNullOrEmpty(bn))
                        return bn;
                }
                MatchBattle cb = md != null ? md.CurrentBattle : null;
                if (cb != null && cb.battle_reference != null)
                {
                    string uid = cb.battle_reference.GetUniqueId();
                    if (!string.IsNullOrEmpty(uid))
                    {
                        int slash = uid.LastIndexOf('/');
                        if (slash >= 0 && slash + 1 < uid.Length)
                            uid = uid.Substring(slash + 1);
                        return uid.Replace('_', ' ');
                    }
                }
            }
            catch { }
            return "next mission";
        }

        internal static void ActivateMpTransitionBanner(bool isClient, string mapName, float waitSeconds = 0f)
        {
            _mpTransitionBannerActive = true;
            _mpTransitionBannerIsClient = isClient;
            _mpTransitionBannerStartUnscaled = Time.unscaledTime;
            _mpTransitionBannerWaitSeconds = Mathf.Max(0f, waitSeconds);
            _mpTransitionBannerMapName = ResolveNextTransitionMapName(mapName);
            BattleManager bm = BattleManager.instance;
            _mpTransitionBannerOldBmPtr = bm != null ? bm.Pointer : IntPtr.Zero;
            _mpTransitionBannerLastSec = -1;
            _mpTransitionBannerCachedText = "";
        }

        [HarmonyPatch(typeof(EndBattleGUI._WaitAnthemEnd_d__46), nameof(EndBattleGUI._WaitAnthemEnd_d__46.MoveNext))]
        public static class EndBattleGUI_WaitAnthemEnd_Patch
        {
            private const float MpAnthemMaxSeconds = 16.0f;
            private const float MpAnthemFadeSeconds = 2.0f;

            public static bool Prefix(EndBattleGUI._WaitAnthemEnd_d__46 __instance, ref bool __result)
            {
                try
                {
                    if (__instance == null || __instance.__1__state != 1)
                        return true;

                    AudioSource music = __instance._music_5__2;
                    if (music == null || music.Pointer == IntPtr.Zero || music.clip == null || music.clip.Pointer == IntPtr.Zero)
                    {
                        __instance.__1__state = -1;
                        __result = false;
                        return false;
                    }

                    if (!VisionConfig.EnableMod.Value || !Photon.Pun.PhotonNetwork.InRoom)
                        return true;

                    if (!music.isPlaying)
                        return true;

                    IntPtr srcPtr = music.Pointer;
                    if (srcPtr != _mpAnthemOrigSourcePtr)
                    {
                        _mpAnthemOrigSourcePtr = srcPtr;
                        _mpAnthemOrigVolume = Mathf.Max(0.05f, music.volume);
                    }

                    float elapsed = music.time;
                    float clipLen = music.clip.length;
                    if (clipLen <= MpAnthemMaxSeconds + 0.25f)
                        return true;

                    if (elapsed >= MpAnthemMaxSeconds)
                    {
                        music.Stop();
                        music.volume = _mpAnthemOrigVolume;
                        _mpAnthemOrigSourcePtr = IntPtr.Zero;
                        __instance.__1__state = -1;
                        __result = false;
                        return false;
                    }

                    float fadeStart = MpAnthemMaxSeconds - MpAnthemFadeSeconds;
                    if (elapsed > fadeStart)
                    {
                        float t = Mathf.Clamp01((MpAnthemMaxSeconds - elapsed) / MpAnthemFadeSeconds);
                        music.volume = _mpAnthemOrigVolume * t;
                    }
                }
                catch (Exception ex)
                {
                    AiCore.ReportPatchException("EndBattle.Anthem", ex);
                }
                return true;
            }
        }

        public sealed class MpTransitionBannerBehaviour : MonoBehaviour
        {
            public MpTransitionBannerBehaviour(IntPtr ptr) : base(ptr) { }

            public void OnGUI()
            {
                if (!_mpTransitionBannerActive)
                    return;

                try
                {
                    float now = Time.unscaledTime;
                    float elapsed = now - _mpTransitionBannerStartUnscaled;
                    BattleManager bm = BattleManager.instance;
                    IntPtr curBmPtr = bm != null ? bm.Pointer : IntPtr.Zero;
                    bool newBattleStarted = curBmPtr != IntPtr.Zero &&
                                            curBmPtr != _mpTransitionBannerOldBmPtr &&
                                            bm.spawnsInitialized &&
                                            elapsed > 2.0f;
                    bool returnedToMenu = MainMenu.instance != null &&
                                          !Photon.Pun.PhotonNetwork.InRoom &&
                                          _mpSwitchStage == 0 &&
                                          elapsed > 5.0f &&
                                          BattleLoader.instance == null;

                    if (newBattleStarted || returnedToMenu || elapsed > 75.0f)
                    {
                        _mpTransitionBannerActive = false;
                        return;
                    }

                    int sec = Mathf.Max(0, Mathf.FloorToInt(elapsed));
                    if (sec != _mpTransitionBannerLastSec || string.IsNullOrEmpty(_mpTransitionBannerCachedText))
                    {
                        _mpTransitionBannerLastSec = sec;
                        string map = string.IsNullOrEmpty(_mpTransitionBannerMapName) ? "next mission" : _mpTransitionBannerMapName;
                        _mpTransitionBannerCachedText = _mpTransitionBannerIsClient
                            ? $"Waiting for Host to load '{map}'... ({sec}s)"
                            : $"Switching Multiplayer Room to '{map}'... ({sec}s)";
                    }

                    int oldDepth = GUI.depth;
                    Color oldColor = GUI.color;
                    GUI.depth = -999;

                    float boxW = Mathf.Min(Screen.width - 40f, 680f);
                    float boxH = 44f;
                    float boxX = (Screen.width - boxW) * 0.5f;
                    float boxY = Screen.height - 86f;

                    GUI.color = new Color(0.04f, 0.05f, 0.08f, 0.88f);
                    GUI.Box(new Rect(boxX, boxY, boxW, boxH), "");

                    GUIStyle lbl = GUI.skin != null ? GUI.skin.label : null;
                    int oldSize = 0;
                    TextAnchor oldAlign = TextAnchor.UpperLeft;
                    FontStyle oldStyle = FontStyle.Normal;
                    bool oldWrap = false;
                    if (lbl != null)
                    {
                        oldSize = lbl.fontSize;
                        oldAlign = lbl.alignment;
                        oldStyle = lbl.fontStyle;
                        oldWrap = lbl.wordWrap;
                        lbl.fontSize = 18;
                        lbl.alignment = TextAnchor.MiddleCenter;
                        lbl.fontStyle = FontStyle.Bold;
                        lbl.wordWrap = false;
                    }

                    GUI.color = new Color(0.95f, 0.90f, 0.65f, 1.0f);
                    GUI.Label(new Rect(boxX + 10f, boxY + 2f, boxW - 20f, boxH - 4f), _mpTransitionBannerCachedText);

                    if (lbl != null)
                    {
                        lbl.fontSize = oldSize;
                        lbl.alignment = oldAlign;
                        lbl.fontStyle = oldStyle;
                        lbl.wordWrap = oldWrap;
                    }
                    GUI.color = oldColor;
                    GUI.depth = oldDepth;
                }
                catch { }
            }
        }

        // Vanilla BattleLoader.ConnectToNextMatch (0x180A6E2CD) accidentally sets `this.waitBeforeJoining = (float)this.waitTime`
        // (reading the int retry counter `this.waitTime` = 0 instead of the `float waitTime` RPC parameter), causing Clients
        // to race into JoinRoom before the Host has finished leaving the old room and creating the new room.
        [HarmonyPatch(typeof(BattleLoader), nameof(BattleLoader.ConnectToNextMatch))]
        public static class BattleLoader_ConnectToNextMatch_Patch
        {
            public static void Postfix(BattleLoader __instance, float __1)
            {
                try
                {
                    if (__instance != null && __1 > __instance.waitBeforeJoining)
                    {
                        __instance.waitBeforeJoining = __1;
                    }
                    if (VisionConfig.EnableMod.Value)
                    {
                        ActivateMpTransitionBanner(isClient: true, mapName: null, waitSeconds: __1);
                    }
                }
                catch { }
            }
        }

        private static void BeginHostLeaveRoomForMapSwitch(BattleLoader bl)
        {
            if (bl == null)
                bl = BattleLoader.instance;

            // Unregister the dying scene's BattleLoader from Photon callbacks (OnDisable -> RemoveCallbackTarget)
            // so BattleLoader.OnConnectedToMaster does not fire ConnectUsingSettings() / ConnectToNextMatchCR on the Host.
            if (bl != null)
                bl.enabled = false;

            MatchData.actorType = MatchData.RoomActorType.master;
            Photon.Pun.PhotonNetwork.LeaveRoom();
            Photon.Pun.PhotonNetwork.SendAllOutgoingCommands();
            _mpSwitchStage = 2;
            _mpSwitchRetriedConnect = false;
            _mpSwitchStageDeadline = Time.unscaledTime + 3.5f;
        }

        private static void UpdatePendingMpMapSwitch()
        {
            if (_mpSwitchStage == 0)
                return;

            MatchData md = MatchData.data;
            if (md == null)
            {
                _mpSwitchStage = 0;
                return;
            }

            float now = Time.unscaledTime;
            if (_mpSwitchStage == 1)
            {
                Photon.Pun.PhotonNetwork.SendAllOutgoingCommands();
                bool allClientsLeft = !Photon.Pun.PhotonNetwork.InRoom ||
                                      Photon.Pun.PhotonNetwork.CurrentRoom == null ||
                                      Photon.Pun.PhotonNetwork.CurrentRoom.PlayerCount <= 1;
                if (allClientsLeft || now >= _mpSwitchStageDeadline)
                {
                    if (Photon.Pun.PhotonNetwork.InRoom)
                    {
                        BeginHostLeaveRoomForMapSwitch(BattleLoader.instance);
                    }
                    else
                    {
                        _mpSwitchStage = 2;
                        _mpSwitchRetriedConnect = false;
                        _mpSwitchStageDeadline = now + 3.5f;
                    }
                }
                return;
            }

            if (_mpSwitchStage == 2)
            {
                var netState = Photon.Pun.PhotonNetwork.NetworkClientState;
                if (!_mpSwitchRetriedConnect &&
                    netState == Photon.Realtime.ClientState.Disconnected &&
                    now < _mpSwitchStageDeadline)
                {
                    _mpSwitchRetriedConnect = true;
                    Photon.Pun.PhotonNetwork.ConnectUsingSettings();
                    return;
                }

                bool readyOnMaster = !Photon.Pun.PhotonNetwork.InRoom &&
                                     netState == Photon.Realtime.ClientState.ConnectedToMasterServer;
                if (readyOnMaster || now >= _mpSwitchStageDeadline)
                {
                    _mpSwitchStage = 0;
                    MatchData.actorType = MatchData.RoomActorType.master;
                    BattleManager.SetPlayerFaction("");
                    md.StartBattleLoading();
                }
            }
        }

        public static partial class BattleManager_OnGUI_Patch
        {
            private static void AppendCampaignTreeEntry(CampaignTreeData camp, string prefix, int fallbackIdx)
            {
                if (camp == null || camp.battles == null || camp.battles.Count == 0)
                    return;

                string campName = camp.GetName();
                if (string.IsNullOrEmpty(campName))
                    campName = camp.campaing_id ?? $"Campaign #{fallbackIdx + 1}";
                if (!string.IsNullOrEmpty(prefix))
                    campName = $"{prefix} {campName}";

                bool isVanillaCamp = string.IsNullOrEmpty(prefix);
                List<PickerBattleEntry> bList = new List<PickerBattleEntry>(camp.battles.Count);
                for (int j = 0; j < camp.battles.Count; j++)
                {
                    ICampaignTreeBattleData bRef = camp.battles[j];
                    if (bRef == null)
                        continue;

                    string uid = bRef.GetUniqueId() ?? $"Mission_{j + 1}";
                    bList.Add(new PickerBattleEntry
                    {
                        DisplayName = null, // Lazily/background resolved to avoid blocking hitches
                        UniqueId = uid,
                        SaveDataId = isVanillaCamp ? uid : null,
                        PreviousSaveDataId = null,
                        InvaderTag = "ATK",
                        DefenderTag = "DEF",
                        IsVanilla = isVanillaCamp,
                        InitialIsUnlocked = !isVanillaCamp,
                        MetadataResolved = false,
                        FactionsResolved = false,
                        BattleRef = bRef
                    });
                }

                if (bList.Count > 0)
                {
                    _mapPickerCampaigns.Add(new PickerCampaignEntry
                    {
                        CampaignName = campName,
                        TopologySorted = false,
                        AllMetadataResolved = false,
                        Battles = bList
                    });
                }
            }

            private static string GetShortFactionTag(string rawFaction, string fallback)
            {
                if (string.IsNullOrEmpty(rawFaction))
                    return fallback;

                string clean = rawFaction;
                if (clean.EndsWith("_allies", StringComparison.OrdinalIgnoreCase))
                    clean = clean.Substring(0, clean.Length - "_allies".Length);
                else if (clean.EndsWith("_axis", StringComparison.OrdinalIgnoreCase))
                    clean = clean.Substring(0, clean.Length - "_axis".Length);

                if (string.Equals(clean, "Unknown", StringComparison.OrdinalIgnoreCase))
                    return fallback;
                if (clean.IndexOf("UnitedStates", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    string.Equals(clean, "USA", StringComparison.OrdinalIgnoreCase))
                    return "USA";
                if (clean.IndexOf("Germany", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    string.Equals(clean, "GER", StringComparison.OrdinalIgnoreCase))
                    return "GER";
                if (clean.IndexOf("England", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    clean.IndexOf("UnitedKingdom", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    clean.IndexOf("Britain", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    string.Equals(clean, "UK", StringComparison.OrdinalIgnoreCase))
                    return "UK";
                if (clean.IndexOf("Ussr", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    clean.IndexOf("Soviet", StringComparison.OrdinalIgnoreCase) >= 0)
                    return "USSR";
                if (clean.IndexOf("Japan", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    string.Equals(clean, "JPN", StringComparison.OrdinalIgnoreCase))
                    return "JPN";
                if (clean.IndexOf("Italy", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    string.Equals(clean, "ITA", StringComparison.OrdinalIgnoreCase))
                    return "ITA";
                if (clean.IndexOf("France", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    string.Equals(clean, "FRA", StringComparison.OrdinalIgnoreCase))
                    return "FRA";
                if (clean.IndexOf("China", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    string.Equals(clean, "ROC", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(clean, "CHN", StringComparison.OrdinalIgnoreCase))
                    return "CHN";
                if (clean.IndexOf("Poland", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    string.Equals(clean, "POL", StringComparison.OrdinalIgnoreCase))
                    return "POL";
                if (clean.IndexOf("Australia", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    string.Equals(clean, "AUS", StringComparison.OrdinalIgnoreCase))
                    return "AUS";
                if (clean.IndexOf("Canada", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    string.Equals(clean, "CAN", StringComparison.OrdinalIgnoreCase))
                    return "CAN";
                if (clean.IndexOf("Finland", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    string.Equals(clean, "FIN", StringComparison.OrdinalIgnoreCase))
                    return "FIN";
                if (clean.IndexOf("Hungary", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    string.Equals(clean, "HUN", StringComparison.OrdinalIgnoreCase))
                    return "HUN";
                if (clean.IndexOf("Romania", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    string.Equals(clean, "ROM", StringComparison.OrdinalIgnoreCase))
                    return "ROM";
                if (clean.IndexOf("Greece", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    string.Equals(clean, "GRE", StringComparison.OrdinalIgnoreCase))
                    return "GRE";
                if (clean.IndexOf("Civilian", StringComparison.OrdinalIgnoreCase) >= 0)
                    return "CIV";

                string upper = clean.ToUpperInvariant();
                return upper.Length <= 4 ? upper : upper.Substring(0, 3);
            }

            internal static void EnsureMapPickerCatalog()
            {
                try
                {
                    if (!SavableData.IsDataLoaded())
                        SavableData.LoadDataIfNotYet();
                }
                catch { }

                int curCustomCount = 0;
                try
                {
                    var loc = CustomCampaignsList.local_campaign_data;
                    var ws = CustomCampaignsList.workshop_campaign_data;
                    if (loc != null) curCustomCount += loc.Count;
                    if (ws != null) curCustomCount += ws.Count;
                }
                catch { }

                if (_mapPickerInitialized && _mapPickerCampaigns.Count > 0 && curCustomCount == _lastCustomCampaignCount)
                    return;

                _lastCustomCampaignCount = curCustomCount;
                _mapPickerCampaigns.Clear();
                _resolvedUnitCountsCache.Clear();
                try
                {
                    var rawCampaigns = VanillaCampaignsList.vanilla_campaign_data;
                    if (rawCampaigns != null)
                    {
                        for (int i = 0; i < rawCampaigns.Length; i++)
                        {
                            AppendCampaignTreeEntry(rawCampaigns[i], null, i);
                        }
                    }

                    var localCampaigns = CustomCampaignsList.local_campaign_data;
                    if (localCampaigns != null)
                    {
                        for (int i = 0; i < localCampaigns.Count; i++)
                        {
                            AppendCampaignTreeEntry(localCampaigns[i], "[Local]", i);
                        }
                    }

                    var workshopCampaigns = CustomCampaignsList.workshop_campaign_data;
                    if (workshopCampaigns != null)
                    {
                        for (int i = 0; i < workshopCampaigns.Count; i++)
                        {
                            AppendCampaignTreeEntry(workshopCampaigns[i], "[Workshop]", i);
                        }
                    }
                }
                catch { }

                _mapPickerInitialized = true;
                _bgPreloadCampaignIdx = 0;
                _allCampaignsPreloaded = false;
                _selectedCampaignIdx = Mathf.Clamp(_selectedCampaignIdx, 0, Mathf.Max(0, _mapPickerCampaigns.Count - 1));
                _selectedBattleIdx = 0;
            }

            private static PickerBattleEntry GetResolvedBattleEntry(List<PickerBattleEntry> list, int index)
            {
                PickerBattleEntry entry = list[index];
                if (!string.IsNullOrEmpty(entry.DisplayName))
                    return entry;

                string bName = null;
                string uid = entry.UniqueId ?? $"Mission_{index + 1}";
                try
                {
                    string contentPath = entry.BattleRef != null ? entry.BattleRef.GetContentPath() : null;
                    if (!string.IsNullOrEmpty(contentPath))
                    {
                        // Streaming / Mission Editor V2 (sp_ME_...) battles store localized names in <contentPath>/localization.xml
                        bName = Language.GetText("battle_name", contentPath, "localization.xml");
                    }
                    else if (!uid.StartsWith("sp_ME_", StringComparison.OrdinalIgnoreCase))
                    {
                        var dict = Language.languages;
                        if (dict != null)
                        {
                            if (dict.ContainsKey(uid))
                                bName = dict[uid];
                        }
                        else
                        {
                            // Populate Language.languages once if not yet parsed
                            bName = Language.GetBattleName(uid);
                        }
                    }
                }
                catch { }

                if (string.IsNullOrEmpty(bName))
                    bName = uid;

                entry.DisplayName = bName;
                list[index] = entry;
                return entry;
            }

            private struct ActivePlayingBattleInfo
            {
                public string UniqueId;
                public string ContentPath;
                public string BattleFolder;
                public string BattleId;
                public string SaveDataId;
                public string BattleName;

                public bool HasAny =>
                    !string.IsNullOrEmpty(UniqueId) ||
                    !string.IsNullOrEmpty(ContentPath) ||
                    !string.IsNullOrEmpty(BattleFolder) ||
                    !string.IsNullOrEmpty(BattleId) ||
                    !string.IsNullOrEmpty(SaveDataId) ||
                    !string.IsNullOrEmpty(BattleName);
            }

            private static bool PathsShareSameLeafOrEqual(string pathA, string pathB)
            {
                if (string.IsNullOrEmpty(pathA) || string.IsNullOrEmpty(pathB))
                    return false;

                string trimA = pathA.TrimEnd('/', '\\');
                string trimB = pathB.TrimEnd('/', '\\');
                if (string.Equals(trimA, trimB, StringComparison.OrdinalIgnoreCase))
                    return true;

                int sepA = Math.Max(trimA.LastIndexOf('/'), trimA.LastIndexOf('\\'));
                int sepB = Math.Max(trimB.LastIndexOf('/'), trimB.LastIndexOf('\\'));
                string leafA = sepA >= 0 ? trimA.Substring(sepA + 1) : trimA;
                string leafB = sepB >= 0 ? trimB.Substring(sepB + 1) : trimB;
                return !string.IsNullOrEmpty(leafA) && string.Equals(leafA, leafB, StringComparison.OrdinalIgnoreCase);
            }

            private static void GetActivePlayingBattleIdentifiers(out ActivePlayingBattleInfo info)
            {
                info = default;
                try
                {
                    MatchData md = MatchData.data;
                    if (md != null)
                    {
                        try
                        {
                            MatchBattle curMb = md.CurrentBattle;
                            ICampaignTreeBattleData curRef = curMb != null ? curMb.battle_reference : null;
                            if (curRef != null)
                            {
                                info.UniqueId = curRef.GetUniqueId();
                                info.ContentPath = curRef.GetContentPath();
                            }
                        }
                        catch { }

                        IBattle lb = md.loadedBattle;
                        if (lb != null)
                        {
                            info.BattleName = lb.GetBattleName();
                            BattleData lbd = lb.GetBattleData();
                            if (lbd != null)
                            {
                                info.BattleId = lbd.battle_id;
                                info.SaveDataId = lbd.SaveDataId;
                                info.BattleFolder = lbd.battle_folder;
                            }
                        }
                    }

                    BattleData curBd = BattleManager.GetCurrentBattleData();
                    if (curBd != null)
                    {
                        if (string.IsNullOrEmpty(info.BattleId))
                            info.BattleId = curBd.battle_id;
                        if (string.IsNullOrEmpty(info.SaveDataId))
                            info.SaveDataId = curBd.SaveDataId;
                        if (string.IsNullOrEmpty(info.BattleFolder))
                            info.BattleFolder = curBd.battle_folder;
                    }
                }
                catch { }
            }

            private static bool IsBattleEntryActive(in PickerBattleEntry entry, in ActivePlayingBattleInfo active)
            {
                if (!string.IsNullOrEmpty(active.UniqueId) &&
                    !string.IsNullOrEmpty(entry.UniqueId) &&
                    string.Equals(entry.UniqueId, active.UniqueId, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (!string.IsNullOrEmpty(active.SaveDataId))
                {
                    if ((!string.IsNullOrEmpty(entry.SaveDataId) && string.Equals(entry.SaveDataId, active.SaveDataId, StringComparison.OrdinalIgnoreCase)) ||
                        (!string.IsNullOrEmpty(entry.UniqueId) && string.Equals(entry.UniqueId, active.SaveDataId, StringComparison.OrdinalIgnoreCase)))
                    {
                        return true;
                    }
                }

                if (!string.IsNullOrEmpty(active.BattleId))
                {
                    if ((!string.IsNullOrEmpty(entry.BattleId) && string.Equals(entry.BattleId, active.BattleId, StringComparison.OrdinalIgnoreCase)) ||
                        (!string.IsNullOrEmpty(entry.UniqueId) && string.Equals(entry.UniqueId, active.BattleId, StringComparison.OrdinalIgnoreCase)) ||
                        (!string.IsNullOrEmpty(entry.SaveDataId) && string.Equals(entry.SaveDataId, active.BattleId, StringComparison.OrdinalIgnoreCase)))
                    {
                        return true;
                    }
                }

                if ((!string.IsNullOrEmpty(active.ContentPath) || !string.IsNullOrEmpty(active.BattleFolder)) && entry.BattleRef != null)
                {
                    string entryPath = null;
                    try { entryPath = entry.BattleRef.GetContentPath(); } catch { }
                    if (!string.IsNullOrEmpty(entryPath) &&
                        (PathsShareSameLeafOrEqual(entryPath, active.ContentPath) ||
                         PathsShareSameLeafOrEqual(entryPath, active.BattleFolder)))
                    {
                        return true;
                    }
                }

                if (!string.IsNullOrEmpty(active.BattleName) &&
                    !string.IsNullOrEmpty(entry.DisplayName) &&
                    string.Equals(entry.DisplayName, active.BattleName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                return false;
            }

            internal static void TryAutoSelectActiveMissionInPicker()
            {
                _mapPickerUserPickedBattle = false;
                if (_mapPickerCampaigns.Count == 0)
                    return;

                GetActivePlayingBattleIdentifiers(out ActivePlayingBattleInfo active);
                if (!active.HasAny)
                    return;

                const int rowsPerPage = MapPickerRowsPerPage;
                for (int c = 0; c < _mapPickerCampaigns.Count; c++)
                {
                    List<PickerBattleEntry> list = _mapPickerCampaigns[c].Battles;
                    if (list == null)
                        continue;

                    for (int b = 0; b < list.Count; b++)
                    {
                        PickerBattleEntry entry = list[b];
                        if (IsBattleEntryActive(in entry, in active))
                        {
                            _selectedCampaignIdx = c;
                            _selectedBattleIdx = b;
                            _campaignPage = c / rowsPerPage;
                            _battlePage = b / rowsPerPage;
                            return;
                        }
                    }
                }

                // Pass 2: fallback leaf match against BattleId / SaveDataId on unresolved sp_ME_ content folders
                if (!string.IsNullOrEmpty(active.BattleId) || !string.IsNullOrEmpty(active.SaveDataId))
                {
                    for (int c = 0; c < _mapPickerCampaigns.Count; c++)
                    {
                        List<PickerBattleEntry> list = _mapPickerCampaigns[c].Battles;
                        if (list == null)
                            continue;

                        for (int b = 0; b < list.Count; b++)
                        {
                            PickerBattleEntry entry = list[b];
                            if (entry.BattleRef == null)
                                continue;

                            string contentPath = null;
                            try { contentPath = entry.BattleRef.GetContentPath(); } catch { }
                            if (PathsShareSameLeafOrEqual(contentPath, active.BattleId) ||
                                PathsShareSameLeafOrEqual(contentPath, active.SaveDataId))
                            {
                                _selectedCampaignIdx = c;
                                _selectedBattleIdx = b;
                                _campaignPage = c / rowsPerPage;
                                _battlePage = b / rowsPerPage;
                                return;
                            }
                        }
                    }
                }
            }

            private static void SortCampaignBattlesTopologically(ref PickerCampaignEntry camp)
            {
                List<PickerBattleEntry> list = camp.Battles;
                if (list == null)
                {
                    camp.TopologySorted = true;
                    return;
                }

                int n = list.Count;
                if (n <= 1)
                {
                    if (n == 1)
                    {
                        PickerBattleEntry only = list[0];
                        only.IsFinale = false;
                        list[0] = only;
                    }
                    camp.TopologySorted = true;
                    return;
                }

                string prevSelUid = (_selectedBattleIdx >= 0 && _selectedBattleIdx < n) ? list[_selectedBattleIdx].UniqueId : null;

                var battleIdToIdx = new Dictionary<string, int>(n * 2, StringComparer.OrdinalIgnoreCase);
                var saveIdToIdx = new Dictionary<string, int>(n, StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < n; i++)
                {
                    PickerBattleEntry e = list[i];
                    if (!string.IsNullOrEmpty(e.BattleId) && !battleIdToIdx.ContainsKey(e.BattleId))
                        battleIdToIdx[e.BattleId] = i;
                    if (!string.IsNullOrEmpty(e.SaveDataId) && !saveIdToIdx.ContainsKey(e.SaveDataId))
                        saveIdToIdx[e.SaveDataId] = i;
                }
                for (int i = 0; i < n; i++)
                {
                    PickerBattleEntry e = list[i];
                    if (!string.IsNullOrEmpty(e.UniqueId) && !battleIdToIdx.ContainsKey(e.UniqueId))
                        battleIdToIdx[e.UniqueId] = i;
                }

                var nextNodes = new List<int>[n];
                var hasParent = new bool[n];
                var outDeg = new int[n];
                int edgeCount = 0;

                for (int v = 0; v < n; v++)
                {
                    PickerBattleEntry e = list[v];
                    int u = -1;
                    if (!string.IsNullOrEmpty(e.PreviousBattleId))
                    {
                        if ((battleIdToIdx.TryGetValue(e.PreviousBattleId, out int tmpIdx) ||
                             saveIdToIdx.TryGetValue(e.PreviousBattleId, out tmpIdx)) &&
                            tmpIdx != v)
                        {
                            u = tmpIdx;
                        }
                    }

                    if (u < 0 && !string.IsNullOrEmpty(e.PreviousSaveDataId))
                    {
                        if ((saveIdToIdx.TryGetValue(e.PreviousSaveDataId, out int tmpIdx) ||
                             battleIdToIdx.TryGetValue(e.PreviousSaveDataId, out tmpIdx)) &&
                            tmpIdx != v)
                        {
                            u = tmpIdx;
                        }
                    }

                    if (u >= 0 && u != v)
                    {
                        hasParent[v] = true;
                        if (nextNodes[u] == null)
                            nextNodes[u] = new List<int>(2);
                        if (!nextNodes[u].Contains(v))
                        {
                            nextNodes[u].Add(v);
                            outDeg[u]++;
                            edgeCount++;
                        }
                    }
                }

                // Match vanilla CampaignsTree.RefreshDisplayCampaignCr + ExploreBranch (0x180A920B0):
                // collect explicit roots in list order, prepend orphan roots, then traverse depth-first
                // down each main trunk and its side branches so campaigns never zig-zag across branches.
                var rootIndices = new List<int>(4);
                for (int i = 0; i < n; i++)
                {
                    if (string.IsNullOrEmpty(list[i].PreviousBattleId) && string.IsNullOrEmpty(list[i].PreviousSaveDataId))
                        rootIndices.Add(i);
                }
                for (int i = 0; i < n; i++)
                {
                    if (!hasParent[i] && (!string.IsNullOrEmpty(list[i].PreviousBattleId) || !string.IsNullOrEmpty(list[i].PreviousSaveDataId)))
                        rootIndices.Insert(0, i);
                }

                var visited = new bool[n];
                var order = new List<int>(n);
                void ExploreBranchDfs(int nodeIdx)
                {
                    if (visited[nodeIdx])
                        return;
                    visited[nodeIdx] = true;
                    order.Add(nodeIdx);
                    List<int> children = nextNodes[nodeIdx];
                    if (children != null)
                    {
                        for (int k = 0; k < children.Count; k++)
                            ExploreBranchDfs(children[k]);
                    }
                }

                for (int r = 0; r < rootIndices.Count; r++)
                    ExploreBranchDfs(rootIndices[r]);

                for (int i = 0; i < n; i++)
                {
                    if (!visited[i])
                        ExploreBranchDfs(i);
                }

                bool anyVanilla = false;
                for (int i = 0; i < n; i++)
                {
                    if (list[i].IsVanilla)
                    {
                        anyVanilla = true;
                        break;
                    }
                }

                var sorted = new List<PickerBattleEntry>(n);
                for (int idxPos = 0; idxPos < order.Count; idxPos++)
                {
                    int origIdx = order[idxPos];
                    PickerBattleEntry item = list[origIdx];
                    item.IsFinale = edgeCount > 0 ? outDeg[origIdx] == 0 : (anyVanilla && idxPos == order.Count - 1);
                    sorted.Add(item);
                }

                camp.Battles = sorted;
                camp.TopologySorted = true;

                const int rowsPerPage = MapPickerRowsPerPage;
                if (!_mapPickerUserPickedBattle)
                {
                    GetActivePlayingBattleIdentifiers(out ActivePlayingBattleInfo active);
                    for (int i = 0; i < sorted.Count; i++)
                    {
                        PickerBattleEntry item = sorted[i];
                        if (IsBattleEntryActive(in item, in active))
                        {
                            _selectedBattleIdx = i;
                            _battlePage = i / rowsPerPage;
                            return;
                        }
                    }
                }

                if (!string.IsNullOrEmpty(prevSelUid))
                {
                    for (int i = 0; i < sorted.Count; i++)
                    {
                        if (string.Equals(sorted[i].UniqueId, prevSelUid, StringComparison.OrdinalIgnoreCase))
                        {
                            _selectedBattleIdx = i;
                            _battlePage = i / rowsPerPage;
                            break;
                        }
                    }
                }
            }

            private static bool _allCampaignsPreloaded;

            private static int StepResolveCampaignBattlesAtIndex(
                int campIdx,
                int startBattleOffset,
                int maxPerFrame,
                long budgetTicks,
                bool updateSelection)
            {
                if (campIdx < 0 || campIdx >= _mapPickerCampaigns.Count)
                    return 0;

                PickerCampaignEntry camp = _mapPickerCampaigns[campIdx];
                List<PickerBattleEntry> list = camp.Battles;
                if (list == null || list.Count == 0)
                {
                    camp.AllMetadataResolved = true;
                    camp.TopologySorted = true;
                    _mapPickerCampaigns[campIdx] = camp;
                    return 0;
                }

                GetActivePlayingBattleIdentifiers(out ActivePlayingBattleInfo active);
                int count = list.Count;
                int bStart = Mathf.Clamp(startBattleOffset, 0, count - 1);
                int resolvedThisFrame = 0;
                long startTicks = System.Diagnostics.Stopwatch.GetTimestamp();

                for (int offset = 0; offset < count; offset++)
                {
                    int idx = (bStart + offset) % count;
                    PickerBattleEntry entry = list[idx];
                    bool needFullMeta = !entry.MetadataResolved || !entry.FactionsResolved;
                    bool needCounts = !entry.UnitCountsResolved && (entry.DefaultMaxAllies <= 0 || entry.DefaultMaxAxis <= 0);
                    if (!needFullMeta && !needCounts)
                        continue;

                    entry = GetResolvedBattleEntry(list, idx);
                    try
                    {
                        if (entry.BattleRef != null)
                        {
                            IBattle ib = entry.BattleRef.LoadBattle();
                            if (ib != null)
                            {
                                if (needFullMeta && (string.IsNullOrEmpty(entry.DisplayName) || entry.DisplayName == entry.UniqueId))
                                {
                                    string loadedName = ib.GetBattleName();
                                    if (!string.IsNullOrEmpty(loadedName))
                                        entry.DisplayName = loadedName;
                                }

                                BattleData bd = ib.GetBattleData();
                                if (bd != null)
                                {
                                    if (needFullMeta)
                                    {
                                        entry.BattleId = bd.battle_id;
                                        entry.PreviousBattleId = bd.previousMission_id;
                                        entry.IsVanilla = bd.IsVanilla();
                                        entry.InitialIsUnlocked = bd.IsUnlocked();
                                        entry.SaveDataId = !string.IsNullOrEmpty(bd.SaveDataId) ? bd.SaveDataId : entry.UniqueId;
                                        entry.PreviousSaveDataId = !string.IsNullOrEmpty(bd.previousMission_id) ? bd.PreviousMission_SaveDataId : null;
                                        try
                                        {
                                            string invFac = bd.GetInvadersFaction();
                                            string defFac = bd.GetDefendersFaction();
                                            entry.InvaderTag = GetShortFactionTag(invFac, "ATK");
                                            entry.DefenderTag = GetShortFactionTag(defFac, "DEF");
                                            entry.FactionsResolved = !string.IsNullOrEmpty(invFac) || !string.IsNullOrEmpty(defFac);
                                        }
                                        catch { }
                                    }
                                    PopulateEntryUnitCounts(ref entry, bd);
                                }
                            }
                        }
                    }
                    catch { }

                    if (string.IsNullOrEmpty(entry.SaveDataId))
                        entry.SaveDataId = entry.UniqueId;
                    if (string.IsNullOrEmpty(entry.InvaderTag))
                        entry.InvaderTag = "ATK";
                    if (string.IsNullOrEmpty(entry.DefenderTag))
                        entry.DefenderTag = "DEF";

                    entry.MetadataResolved = true;
                    entry.FactionsResolved = true;
                    entry.UnitCountsResolved = true;
                    list[idx] = entry;

                    if (updateSelection && !_mapPickerUserPickedBattle && IsBattleEntryActive(in entry, in active))
                    {
                        _selectedBattleIdx = idx;
                        _battlePage = idx / MapPickerRowsPerPage;
                    }

                    if (++resolvedThisFrame >= maxPerFrame || (System.Diagnostics.Stopwatch.GetTimestamp() - startTicks) >= budgetTicks)
                        break;
                }

                bool allResolved = true;
                for (int k = 0; k < list.Count; k++)
                {
                    if (!list[k].MetadataResolved || !list[k].FactionsResolved)
                    {
                        allResolved = false;
                        break;
                    }
                }

                if (allResolved)
                {
                    camp.AllMetadataResolved = true;
                    if (!camp.TopologySorted)
                        SortCampaignBattlesTopologically(ref camp);
                    _mapPickerCampaigns[campIdx] = camp;
                }

                return resolvedThisFrame;
            }

            // Amortizes IBattle / BattleData metadata loading (battle_id, previousMission_id, SaveDataId, factions)
            // lazily ONLY while the F5 Campaign & Map Picker menu is open (zero mid-match LoadBattle() hitches).
            internal static void StepBackgroundPreloadCatalog()
            {
                if (!_showMapPicker)
                    return;

                if (!_mapPickerInitialized)
                {
                    if (Time.realtimeSinceStartup < 3.5f)
                        return;

                    try
                    {
                        var rawCampaigns = VanillaCampaignsList.vanilla_campaign_data;
                        if (rawCampaigns == null || rawCampaigns.Length == 0)
                            return;
                    }
                    catch
                    {
                        return;
                    }

                    EnsureMapPickerCatalog();
                    _allCampaignsPreloaded = false;
                }

                if (_mapPickerCampaigns.Count == 0)
                    return;

                StepResolveCampaignBattlesAtIndex(
                    _selectedCampaignIdx,
                    _battlePage * MapPickerRowsPerPage,
                    maxPerFrame: 3,
                    budgetTicks: System.Diagnostics.Stopwatch.Frequency / 666, // 1.5ms budget
                    updateSelection: true);

                if (_allCampaignsPreloaded)
                    return;

                int checkedCamps = 0;
                int totalCamps = _mapPickerCampaigns.Count;
                long bgBudget = System.Diagnostics.Stopwatch.Frequency / 2000; // 0.5ms background cap
                while (checkedCamps < totalCamps)
                {
                    int idx = _bgPreloadCampaignIdx % totalCamps;
                    _bgPreloadCampaignIdx = (idx + 1) % totalCamps;
                    checkedCamps++;

                    if (idx == _selectedCampaignIdx)
                        continue;

                    PickerCampaignEntry camp = _mapPickerCampaigns[idx];
                    if (camp.AllMetadataResolved && camp.TopologySorted)
                        continue;

                    StepResolveCampaignBattlesAtIndex(
                        idx,
                        startBattleOffset: 0,
                        maxPerFrame: 1,
                        budgetTicks: bgBudget,
                        updateSelection: false);
                    return;
                }

                _allCampaignsPreloaded = true;
                for (int i = 0; i < totalCamps; i++)
                {
                    if (!_mapPickerCampaigns[i].AllMetadataResolved || !_mapPickerCampaigns[i].TopologySorted)
                    {
                        _allCampaignsPreloaded = false;
                        break;
                    }
                }
            }

            private static void EvaluateBattleUnlockAndStars(
                PickerBattleEntry bEntry,
                List<PickerBattleEntry> campaignBattles,
                GameProgresses prog,
                bool isCurrentlyPlaying,
                out bool isCompleted,
                out bool wonBothFactions,
                out bool wonAsInvader,
                out bool wonAsDefender,
                out bool isUnlocked)
            {
                isCompleted = false;
                wonBothFactions = false;
                wonAsInvader = false;
                wonAsDefender = false;

                if (prog != null && !string.IsNullOrEmpty(bEntry.SaveDataId))
                {
                    try
                    {
                        var comp = prog.GetCompletedLevelData(bEntry.SaveDataId);
                        if (comp != null && comp.IsCompleted())
                        {
                            isCompleted = true;
                            wonAsInvader = comp.wonAsInvader;
                            wonAsDefender = comp.wonAsDefender;
                            wonBothFactions = comp.WonOnBothFactions;
                        }
                    }
                    catch { }
                }

                if (!bEntry.IsVanilla)
                {
                    isUnlocked = true;
                }
                else if (!bEntry.MetadataResolved)
                {
                    // Mask unresolved vanilla rows during the amortized scan window unless completed or actively playing
                    isUnlocked = isCompleted || isCurrentlyPlaying;
                }
                else
                {
                    bool hasPrereq = !string.IsNullOrEmpty(bEntry.PreviousBattleId) || !string.IsNullOrEmpty(bEntry.PreviousSaveDataId);
                    bool prevDone = false;
                    if (hasPrereq && prog != null)
                    {
                        if (!string.IsNullOrEmpty(bEntry.PreviousSaveDataId))
                        {
                            try { prevDone = prog.IsLevelCompleted(bEntry.PreviousSaveDataId); } catch { }
                        }

                        if (!prevDone && !string.IsNullOrEmpty(bEntry.PreviousBattleId) && campaignBattles != null)
                        {
                            for (int k = 0; k < campaignBattles.Count; k++)
                            {
                                PickerBattleEntry prevEntry = campaignBattles[k];
                                if ((!string.IsNullOrEmpty(prevEntry.BattleId) && string.Equals(prevEntry.BattleId, bEntry.PreviousBattleId, StringComparison.OrdinalIgnoreCase)) ||
                                    (!string.IsNullOrEmpty(prevEntry.UniqueId) && string.Equals(prevEntry.UniqueId, bEntry.PreviousBattleId, StringComparison.OrdinalIgnoreCase)))
                                {
                                    if (!string.IsNullOrEmpty(prevEntry.SaveDataId))
                                    {
                                        try { prevDone = prog.IsLevelCompleted(prevEntry.SaveDataId); } catch { }
                                    }
                                    break;
                                }
                            }
                        }
                    }

                    isUnlocked = isCompleted ||
                                 isCurrentlyPlaying ||
                                 bEntry.InitialIsUnlocked ||
                                 !hasPrereq ||
                                 prevDone;
                }
            }

            private static readonly Dictionary<IntPtr, (bool isMp, int allies, int axis)> _resolvedUnitCountsCache
                = new Dictionary<IntPtr, (bool isMp, int allies, int axis)>(64);

            private static void PopulateEntryUnitCounts(ref PickerBattleEntry entry, BattleData bd)
            {
                if (bd == null)
                    return;

                bool isMp = MatchData.data != null && MatchData.data.isMultiplayer;
                int allies;
                int axis;
                try
                {
                    bd.GetMaxUnitsForThisSystemProportional(isMp, out short sysAllies, out short sysAxis);
                    allies = sysAllies > 0 ? sysAllies : Mathf.Max(1, bd.maxAllies);
                    axis = sysAxis > 0 ? sysAxis : Mathf.Max(1, bd.maxAxis);
                }
                catch
                {
                    allies = Mathf.Max(1, bd.maxAllies);
                    axis = Mathf.Max(1, bd.maxAxis);
                }

                entry.DefaultMaxAllies = allies;
                entry.DefaultMaxAxis = axis;
                if (entry.BattleRef != null && entry.BattleRef.Pointer != IntPtr.Zero)
                    _resolvedUnitCountsCache[entry.BattleRef.Pointer] = (isMp, allies, axis);
            }

            private static void EnsureBattleUnitCountsResolved(
                ref PickerBattleEntry entry,
                List<PickerBattleEntry> parentList = null,
                int parentIdx = -1)
            {
                if (entry.UnitCountsResolved || (entry.DefaultMaxAllies > 0 && entry.DefaultMaxAxis > 0))
                    return;

                if (entry.BattleRef == null)
                    return;

                bool isMp = MatchData.data != null && MatchData.data.isMultiplayer;
                IntPtr refPtr = entry.BattleRef.Pointer;
                if (refPtr != IntPtr.Zero &&
                    _resolvedUnitCountsCache.TryGetValue(refPtr, out var cached) &&
                    cached.isMp == isMp &&
                    cached.allies > 0 &&
                    cached.axis > 0)
                {
                    entry.DefaultMaxAllies = cached.allies;
                    entry.DefaultMaxAxis = cached.axis;
                    entry.UnitCountsResolved = true;
                    if (parentList != null && parentIdx >= 0 && parentIdx < parentList.Count)
                        parentList[parentIdx] = entry;
                    return;
                }

                try
                {
                    IBattle ib = entry.BattleRef.LoadBattle();
                    if (ib != null)
                    {
                        BattleData bd = ib.GetBattleData();
                        if (bd != null)
                        {
                            if (string.IsNullOrEmpty(entry.BattleId))
                                entry.BattleId = bd.battle_id;
                            if (string.IsNullOrEmpty(entry.SaveDataId))
                                entry.SaveDataId = !string.IsNullOrEmpty(bd.SaveDataId) ? bd.SaveDataId : entry.UniqueId;
                            PopulateEntryUnitCounts(ref entry, bd);
                        }
                    }
                }
                catch { }

                entry.UnitCountsResolved = true;
                if (parentList != null && parentIdx >= 0 && parentIdx < parentList.Count)
                    parentList[parentIdx] = entry;
            }

            private static void EnsureBattleRefUnitCountsResolved(
                ICampaignTreeBattleData battleRef,
                MatchBattle fallbackMb,
                out int baseAllies,
                out int baseAxis)
            {
                bool isMp = MatchData.data != null && MatchData.data.isMultiplayer;
                if (battleRef != null && battleRef.Pointer != IntPtr.Zero)
                {
                    if (_resolvedUnitCountsCache.TryGetValue(battleRef.Pointer, out var cached) &&
                        cached.isMp == isMp &&
                        cached.allies > 0 &&
                        cached.axis > 0)
                    {
                        baseAllies = cached.allies;
                        baseAxis = cached.axis;
                        return;
                    }

                    try
                    {
                        IBattle ib = battleRef.LoadBattle();
                        if (ib != null)
                        {
                            BattleData bd = ib.GetBattleData();
                            if (bd != null)
                            {
                                PickerBattleEntry tmp = new PickerBattleEntry { BattleRef = battleRef };
                                PopulateEntryUnitCounts(ref tmp, bd);
                                if (tmp.DefaultMaxAllies > 0 && tmp.DefaultMaxAxis > 0)
                                {
                                    baseAllies = tmp.DefaultMaxAllies;
                                    baseAxis = tmp.DefaultMaxAxis;
                                    return;
                                }
                            }
                        }
                    }
                    catch { }
                }

                if (fallbackMb != null)
                {
                    if (fallbackMb.proportionalAlliesRefValue > 0 && fallbackMb.proportionalAxisRefValue > 0)
                    {
                        baseAllies = fallbackMb.proportionalAlliesRefValue;
                        baseAxis = fallbackMb.proportionalAxisRefValue;
                        return;
                    }
                    if (fallbackMb.maxAllies > 0 && fallbackMb.maxAxis > 0)
                    {
                        baseAllies = fallbackMb.maxAllies;
                        baseAxis = fallbackMb.maxAxis;
                        return;
                    }
                }

                baseAllies = 32;
                baseAxis = 32;
            }

            private static void ComputeScaledUnitCounts(
                int baseAllies,
                int baseAxis,
                float botScale,
                out short scaledAllies,
                out short scaledAxis)
            {
                float clampedScale = Mathf.Clamp(botScale, 0.25f, 3.00f);
                float bias = Mathf.Clamp(_mapPickerAlliesBias, -0.40f, 0.40f);
                float alliesMult = clampedScale * (1.0f + bias);
                float axisMult = clampedScale * (1.0f - bias);
                int maxPerSide = VeteranAndTacticsPatches.GetOnlineMaxUnitsPerFaction();
                scaledAllies = (short)Mathf.Clamp(Mathf.RoundToInt(Mathf.Max(1, baseAllies) * alliesMult), 1, maxPerSide);
                scaledAxis = (short)Mathf.Clamp(Mathf.RoundToInt(Mathf.Max(1, baseAxis) * axisMult), 1, maxPerSide);
            }

            private static MatchBattle CreateConfiguredMatchBattle(
                ref PickerBattleEntry entry,
                MatchBattle curMb,
                float botScale)
            {
                EnsureBattleUnitCountsResolved(ref entry);
                int baseAllies = entry.DefaultMaxAllies > 0
                    ? entry.DefaultMaxAllies
                    : (curMb != null && curMb.maxAllies > 0 ? curMb.maxAllies : 32);
                int baseAxis = entry.DefaultMaxAxis > 0
                    ? entry.DefaultMaxAxis
                    : (curMb != null && curMb.maxAxis > 0 ? curMb.maxAxis : 32);

                ComputeScaledUnitCounts(baseAllies, baseAxis, botScale, out short scaledAllies, out short scaledAxis);

                MatchBattle newMb = new MatchBattle(entry.BattleRef);
                newMb.maxAllies = scaledAllies;
                newMb.maxAxis = scaledAxis;
                newMb.proportionalAlliesRefValue = (short)Mathf.Clamp(baseAllies, 1, 500);
                newMb.proportionalAxisRefValue = (short)Mathf.Clamp(baseAxis, 1, 500);
                newMb.proportionalSliderRefValue = Mathf.Clamp(botScale, 0.25f, 3.00f);
                newMb.ticketsMultiplier = Mathf.Clamp(_mapPickerTicketsMult, 0.25f, 3.00f);
                if (curMb != null)
                {
                    newMb.allowAchievements = curMb.allowAchievements;
                }
                return newMb;
            }

            private static int FindPlaylistIndex(in PickerBattleEntry entry)
            {
                IntPtr entryPtr = entry.BattleRef != null ? entry.BattleRef.Pointer : IntPtr.Zero;
                for (int i = 0; i < _mapPickerPlaylist.Count; i++)
                {
                    PickerBattleEntry existing = _mapPickerPlaylist[i].Battle;
                    if (!string.IsNullOrEmpty(entry.UniqueId) &&
                        string.Equals(existing.UniqueId, entry.UniqueId, StringComparison.OrdinalIgnoreCase))
                        return i;
                    if (entryPtr != IntPtr.Zero && existing.BattleRef != null && existing.BattleRef.Pointer == entryPtr)
                        return i;
                }
                return -1;
            }

            private static bool TogglePlaylistEntry(PickerBattleEntry entry, string visibleName)
            {
                int idx = FindPlaylistIndex(in entry);
                if (idx >= 0)
                {
                    _mapPickerPlaylist.RemoveAt(idx);
                    return false;
                }

                EnsureBattleUnitCountsResolved(ref entry);
                _mapPickerPlaylist.Add(new QueuedPickerEntry
                {
                    Battle = entry,
                    VisibleName = string.IsNullOrEmpty(visibleName) ? entry.DisplayName : visibleName
                });
                return true;
            }

            private static List<QueuedPickerEntry> BuildCampaignFromIndexItems(
                List<PickerBattleEntry> curBattles,
                int startIdx,
                GameProgresses prog,
                in ActivePlayingBattleInfo activeInfo)
            {
                var items = new List<QueuedPickerEntry>();
                if (curBattles == null || startIdx < 0 || startIdx >= curBattles.Count)
                    return items;

                for (int i = startIdx; i < curBattles.Count; i++)
                {
                    PickerBattleEntry entry = GetResolvedBattleEntry(curBattles, i);
                    if (entry.BattleRef == null)
                        continue;

                    bool active = IsBattleEntryActive(in entry, in activeInfo);
                    EvaluateBattleUnlockAndStars(entry, curBattles, prog, active, out _, out _, out _, out _, out bool unlocked);
                    bool masked = !unlocked && !_revealMapPickerSpoilers;
                    string visName = masked ? $"Mission #{i + 1} (Locked)" : entry.DisplayName;
                    items.Add(new QueuedPickerEntry
                    {
                        Battle = entry,
                        VisibleName = visName
                    });
                }

                return items;
            }

            private static int ApplyBotScaleToQueuedBattles(float botScale, out short nextAllies, out short nextAxis)
            {
                nextAllies = 0;
                nextAxis = 0;
                MatchData md = MatchData.data;
                if (md == null || md.battles == null)
                    return 0;

                int startIdx = Mathf.Max(0, md.currentBattleId + 1);
                if (startIdx >= md.battles.Count)
                    return 0;

                int updated = 0;
                float clampedScale = Mathf.Clamp(botScale, 0.25f, 3.00f);
                for (int i = startIdx; i < md.battles.Count; i++)
                {
                    MatchBattle queuedMb = md.battles[i];
                    if (queuedMb == null)
                        continue;

                    EnsureBattleRefUnitCountsResolved(queuedMb.battle_reference, queuedMb, out int baseAllies, out int baseAxis);
                    ComputeScaledUnitCounts(baseAllies, baseAxis, clampedScale, out short scaledAllies, out short scaledAxis);
                    queuedMb.maxAllies = scaledAllies;
                    queuedMb.maxAxis = scaledAxis;
                    queuedMb.proportionalAlliesRefValue = (short)Mathf.Clamp(baseAllies, 1, 500);
                    queuedMb.proportionalAxisRefValue = (short)Mathf.Clamp(baseAxis, 1, 500);
                    queuedMb.proportionalSliderRefValue = clampedScale;
                    queuedMb.ticketsMultiplier = Mathf.Clamp(_mapPickerTicketsMult, 0.25f, 3.00f);
                    if (updated == 0)
                    {
                        nextAllies = scaledAllies;
                        nextAxis = scaledAxis;
                    }
                    updated++;
                }

                if (updated > 0 && Photon.Pun.PhotonNetwork.InRoom && Photon.Pun.PhotonNetwork.IsMasterClient)
                {
                    try { md.UpdateNetworkMatchInfo(); } catch { }
                }

                _mapPickerBotScaleDirty = false;
                return updated;
            }

            private static int _cachedSliderBarPct = -1;
            private static string _cachedSliderBarInnerText = "100%  (drag 25-300%)";
            private static string _cachedSliderRowLabelText = "Bot Scale: 100%";
            private static readonly float[] _botScalePresets = { 0.5f, 1.0f, 1.5f, 2.0f, 3.0f };
            private static readonly string[] _botScalePresetLabels = { "50%", "100%", "150%", "200%", "300%" };
            private static KeyCode _cachedPickerHeaderKey = (KeyCode)(-999);
            private static string _cachedPickerHeaderText = "Easy Red 2: Battle Hardened  |  Campaign, Playlist & Mission Picker (F5)";

            private static bool DrawBotScaleSliderBar(Rect trackRect, ref float scale)
            {
                bool changed = false;
                GUI.color = new Color(0.14f, 0.16f, 0.21f, 1.0f);
                GUI.Box(trackRect, "");

                float t = Mathf.InverseLerp(0.25f, 3.00f, scale);
                float fillW = Mathf.Max(6f, trackRect.width * t);
                GUI.color = Mathf.Abs(scale - 1.0f) < 0.01f
                    ? new Color(0.45f, 0.90f, 0.52f, 0.92f)
                    : new Color(1.0f, 0.80f, 0.30f, 0.95f);
                GUI.Box(new Rect(trackRect.x + 1f, trackRect.y + 2f, Mathf.Max(4f, fillW - 2f), trackRect.height - 4f), "");

                int pct = Mathf.RoundToInt(scale * 100f);
                if (pct != _cachedSliderBarPct)
                {
                    _cachedSliderBarPct = pct;
                    _cachedSliderBarInnerText = $"{pct}%  (drag 25\u2013300%)";
                    _cachedSliderRowLabelText = $"Bot Scale: {pct}%";
                }

                GUIStyle lblStyle = GUI.skin != null ? GUI.skin.label : null;
                bool oldWrap = false;
                TextAnchor oldAlign = TextAnchor.UpperLeft;
                if (lblStyle != null)
                {
                    oldWrap = lblStyle.wordWrap;
                    oldAlign = lblStyle.alignment;
                    lblStyle.wordWrap = false;
                    lblStyle.alignment = TextAnchor.MiddleCenter;
                }

                GUI.color = Color.white;
                GUI.Label(new Rect(trackRect.x + 4f, trackRect.y, trackRect.width - 8f, trackRect.height), _cachedSliderBarInnerText);

                if (lblStyle != null)
                {
                    lblStyle.wordWrap = oldWrap;
                    lblStyle.alignment = oldAlign;
                }

                Event ev = Event.current;
                if (ev != null && (ev.type == EventType.MouseDown || ev.type == EventType.MouseDrag) && ev.button == 0)
                {
                    if (trackRect.Contains(ev.mousePosition))
                    {
                        float norm = Mathf.Clamp01((ev.mousePosition.x - trackRect.x) / Mathf.Max(1f, trackRect.width));
                        float rawScale = Mathf.Lerp(0.25f, 3.00f, norm);
                        float snapped = Mathf.Clamp(Mathf.Round(rawScale * 20f) / 20f, 0.25f, 3.00f);
                        if (!Mathf.Approximately(snapped, scale))
                        {
                            scale = snapped;
                            changed = true;
                        }
                        ev.Use();
                    }
                }

                return changed;
            }

            private static int _mapPickerActiveTab;
            private static float _mapPickerAlliesBias;
            private static float _mapPickerTicketsMult = 1.0f;
            private static bool _matchSettingsDraftInitialized;
            private static bool _matchSettingsDraftDirty;
            private static IntPtr _lastSyncedDiffPtr;
            private static string _draftDiffPresetName = "Current";
            private static float _draftAiAcc = 1.0f;
            private static float _draftAiAccStatic = 0.63f;
            private static float _draftAiAirAggro = 3.0f;
            private static float _draftPlayerDmg = 1.0f;
            private static float _draftSuppShake = 0.15f;
            private static float _draftExpRadius = 1.0f;
            private static float _draftStamina = 3.0f;
            private static float _draftBarrelOverheat = 1.0f;
            private static float _draftNightLight = 1.0f;
            private static bool _draftFriendlyInd;
            private static bool _draftSuppDir = true;
            private static bool _draftAutoSurrender;
            private static bool _draftSimpleVehUi = true;
            private static bool _draftDmgAnim = true;

            private static float ReadSafeProtectedFloat(ProtectedFloat pf, float fallback)
            {
                float decoded = BitConverter.Int32BitsToSingle(pf.hiddenValue ^ pf.cryptoKey);
                if (!float.IsNaN(decoded) && !float.IsInfinity(decoded) && Mathf.Approximately(decoded, pf.fakeValue))
                    return decoded;
                if (!float.IsNaN(pf.fakeValue) && !float.IsInfinity(pf.fakeValue))
                    return pf.fakeValue;
                return fallback;
            }

            private static bool ReadSafeProtectedBool(ProtectedBool pb)
            {
                bool decoded = ((pb.hiddenValue ^ pb.cryptoKey) & 1) != 0;
                return decoded == pb.fakeValue ? decoded : pb.fakeValue;
            }

            private static ProtectedFloat MakeSafeProtectedFloat(float value)
            {
                if (float.IsNaN(value) || float.IsInfinity(value))
                    value = 1.0f;

                ProtectedFloat pf = value;
                int bits = BitConverter.SingleToInt32Bits(value);
                if (pf.fakeValue != value || (pf.hiddenValue ^ pf.cryptoKey) != bits)
                {
                    int key = unchecked((int)(0x5A3C96E1u ^ (uint)Environment.TickCount));
                    if (key == 0)
                        key = 0x13579BDF;
                    pf.cryptoKey = key;
                    pf.fakeValue = value;
                    pf.hiddenValue = bits ^ key;
                }
                return pf;
            }

            private static ProtectedBool MakeSafeProtectedBool(bool value)
            {
                ProtectedBool pb = value;
                int expected = value ? 1 : 0;
                if (pb.fakeValue != value || ((pb.hiddenValue ^ pb.cryptoKey) & 1) != expected)
                {
                    int key = unchecked((int)(0x4B2D85F0u ^ (uint)Environment.TickCount)) & 0x7FFFFFFF;
                    if (key == 0)
                        key = 0x2468ACE1;
                    pb.cryptoKey = key;
                    pb.fakeValue = value;
                    pb.hiddenValue = expected ^ key;
                }
                return pb;
            }

            private static void LoadDraftFromDifficultySettings(MatchDifficultySettings settings)
            {
                _draftAiAcc = Mathf.Clamp(ReadSafeProtectedFloat(settings.aiAccuracyMultiplier, 1.0f), 0.25f, 2.50f);
                _draftAiAccStatic = Mathf.Clamp(ReadSafeProtectedFloat(settings.aiAccuracyMultiplierStatic, 0.63f), 0.25f, 2.50f);
                _draftAiAirAggro = Mathf.Clamp(ReadSafeProtectedFloat(settings.aiAircraftAggressivnessMultiplier, 3.0f), 0.25f, 3.00f);
                _draftPlayerDmg = Mathf.Clamp(ReadSafeProtectedFloat(settings.playerDamageMultiplier, 1.0f), 0.25f, 2.50f);
                _draftSuppShake = Mathf.Clamp(ReadSafeProtectedFloat(settings.suppressionShakeMultiplier, 0.15f), 0.00f, 2.50f);
                _draftExpRadius = Mathf.Clamp(ReadSafeProtectedFloat(settings.explosionRadiusMultiplier, 1.0f), 0.50f, 2.00f);
                _draftStamina = Mathf.Clamp(ReadSafeProtectedFloat(settings.staminaMultiplier, 3.0f), 0.25f, 3.00f);
                _draftBarrelOverheat = Mathf.Clamp(settings.barrellOverheatMultiplier, 0.00f, 2.50f);
                _draftNightLight = Mathf.Clamp(settings.nighLightMultiplier, 0.25f, 2.00f);
                _draftFriendlyInd = ReadSafeProtectedBool(settings.showFriendlyUnitsIndicator);
                _draftSuppDir = ReadSafeProtectedBool(settings.suppressionDirectionIndicatorOn);
                _draftAutoSurrender = ReadSafeProtectedBool(settings.allowPlayerAutoSurrender);
                _draftSimpleVehUi = ReadSafeProtectedBool(settings.simplifiedVehicleDamageUi);
                _draftDmgAnim = ReadSafeProtectedBool(settings.playerDamageAnimationOn);
            }

            private static void SyncMatchSettingsDraftFromMatchData(bool force = false)
            {
                try
                {
                    MatchData md = MatchData.data;
                    MatchDifficultySettings diff = MatchData.Difficulty;
                    if (diff == null)
                        return;

                    IntPtr diffPtr = diff.Pointer;
                    if (!force && _matchSettingsDraftInitialized && _matchSettingsDraftDirty)
                        return;
                    if (!force && _matchSettingsDraftInitialized && diffPtr == _lastSyncedDiffPtr)
                        return;

                    _lastSyncedDiffPtr = diffPtr;
                    _matchSettingsDraftInitialized = true;
                    _matchSettingsDraftDirty = false;
                    _draftDiffPresetName = "Synced";

                    LoadDraftFromDifficultySettings(diff);

                    MatchBattle cb = md != null ? md.CurrentBattle : null;
                    if (cb != null && cb.ticketsMultiplier > 0.05f)
                        _mapPickerTicketsMult = Mathf.Clamp(cb.ticketsMultiplier, 0.25f, 3.00f);
                }
                catch { }
            }

            private static void ApplyPresetToMatchDraft(DifficultyValue preset, string label)
            {
                try
                {
                    MatchDifficultySettings temp = new MatchDifficultySettings();
                    temp.SetDifficultyPresed(preset);
                    LoadDraftFromDifficultySettings(temp);
                    _draftDiffPresetName = label;
                    _matchSettingsDraftInitialized = true;
                    _matchSettingsDraftDirty = true;
                }
                catch { }
            }

            private static void ApplyDraftSettingsToMatchData(bool updateUpcomingQueue)
            {
                MatchData md = MatchData.data;
                if (md == null)
                    return;

                _draftAiAcc = Mathf.Clamp(_draftAiAcc, 0.25f, 2.50f);
                _draftAiAccStatic = Mathf.Clamp(_draftAiAccStatic, 0.25f, 2.50f);
                _draftAiAirAggro = Mathf.Clamp(_draftAiAirAggro, 0.25f, 3.00f);
                _draftPlayerDmg = Mathf.Clamp(_draftPlayerDmg, 0.25f, 2.50f);
                _draftSuppShake = Mathf.Clamp(_draftSuppShake, 0.00f, 2.50f);
                _draftExpRadius = Mathf.Clamp(_draftExpRadius, 0.50f, 2.00f);
                _draftStamina = Mathf.Clamp(_draftStamina, 0.25f, 3.00f);
                _draftBarrelOverheat = Mathf.Clamp(_draftBarrelOverheat, 0.00f, 2.50f);
                _draftNightLight = Mathf.Clamp(_draftNightLight, 0.25f, 2.00f);

                MatchDifficultySettings diff = MatchData.Difficulty != null
                    ? MatchData.Difficulty.ShallowClone()
                    : new MatchDifficultySettings();
                diff.aiAccuracyMultiplier = MakeSafeProtectedFloat(_draftAiAcc);
                diff.aiAccuracyMultiplierStatic = MakeSafeProtectedFloat(_draftAiAccStatic);
                diff.aiAircraftAggressivnessMultiplier = MakeSafeProtectedFloat(_draftAiAirAggro);
                diff.playerDamageMultiplier = MakeSafeProtectedFloat(_draftPlayerDmg);
                diff.suppressionShakeMultiplier = MakeSafeProtectedFloat(_draftSuppShake);
                diff.explosionRadiusMultiplier = MakeSafeProtectedFloat(_draftExpRadius);
                diff.staminaMultiplier = MakeSafeProtectedFloat(_draftStamina);
                diff.barrellOverheatMultiplier = _draftBarrelOverheat;
                diff.nighLightMultiplier = _draftNightLight;
                diff.showFriendlyUnitsIndicator = MakeSafeProtectedBool(_draftFriendlyInd);
                diff.suppressionDirectionIndicatorOn = MakeSafeProtectedBool(_draftSuppDir);
                diff.allowPlayerAutoSurrender = MakeSafeProtectedBool(_draftAutoSurrender);
                diff.simplifiedVehicleDamageUi = MakeSafeProtectedBool(_draftSimpleVehUi);
                diff.playerDamageAnimationOn = MakeSafeProtectedBool(_draftDmgAnim);
                MatchData.Difficulty = diff;
                _lastSyncedDiffPtr = MatchData.Difficulty != null ? MatchData.Difficulty.Pointer : IntPtr.Zero;

                if (md.CurrentBattle != null)
                    md.CurrentBattle.ticketsMultiplier = Mathf.Clamp(_mapPickerTicketsMult, 0.25f, 3.00f);

                int updatedQueue = 0;
                if (updateUpcomingQueue)
                    updatedQueue = ApplyBotScaleToQueuedBattles(_mapPickerBotScale, out _, out _);

                if (Photon.Pun.PhotonNetwork.InRoom && Photon.Pun.PhotonNetwork.IsMasterClient && updatedQueue == 0)
                {
                    try { md.UpdateNetworkMatchInfo(); } catch { }
                }

                _matchSettingsDraftDirty = false;
                string mpNote = Photon.Pun.PhotonNetwork.InRoom
                    ? " Synced to lobby (AI settings live on Host; full player/ticket/balance rules apply next map & to new joiners)."
                    : " Active immediately (tickets & force balance apply on next map).";
                ShowToast($"[Battle Hardened] Applied Match & Difficulty settings ({_draftDiffPresetName}, queued maps updated: {updatedQueue}).{mpNote}");
            }

            private static bool DrawFloatSettingRow(
                Rect rowRect,
                string label,
                ref float value,
                float min,
                float max,
                float step,
                string valText,
                bool canEdit,
                bool accentAmber = false)
            {
                bool changed = false;
                float labelW = 164f;
                float btnW = 28f;
                float barX = rowRect.x + labelW + 4f;
                float barW = Mathf.Max(60f, rowRect.width - labelW - btnW * 2f - 18f);
                Rect barRect = new Rect(barX, rowRect.y + 3f, barW, rowRect.height - 6f);

                GUI.color = canEdit ? new Color(0.86f, 0.92f, 0.98f, 1.0f) : new Color(0.66f, 0.70f, 0.76f, 0.85f);
                GUI.Label(new Rect(rowRect.x, rowRect.y + 3f, labelW, rowRect.height - 4f), label);

                GUI.color = new Color(0.14f, 0.16f, 0.21f, 1.0f);
                GUI.Box(barRect, "");
                float t = Mathf.InverseLerp(min, max, value);
                float fillW = Mathf.Max(4f, barRect.width * t);
                GUI.color = !canEdit
                    ? new Color(0.45f, 0.48f, 0.54f, 0.70f)
                    : (accentAmber ? new Color(1.0f, 0.78f, 0.30f, 0.90f) : new Color(0.42f, 0.85f, 0.62f, 0.90f));
                GUI.Box(new Rect(barRect.x + 1f, barRect.y + 2f, Mathf.Max(2f, fillW - 2f), barRect.height - 4f), "");

                GUIStyle lblStyle = GUI.skin != null ? GUI.skin.label : null;
                TextAnchor oldAlign = TextAnchor.UpperLeft;
                if (lblStyle != null)
                {
                    oldAlign = lblStyle.alignment;
                    lblStyle.alignment = TextAnchor.MiddleCenter;
                }
                GUI.color = Color.white;
                GUI.Label(barRect, valText);
                if (lblStyle != null)
                    lblStyle.alignment = oldAlign;

                if (canEdit)
                {
                    Event ev = Event.current;
                    if (ev != null && (ev.type == EventType.MouseDown || ev.type == EventType.MouseDrag) && ev.button == 0 && barRect.Contains(ev.mousePosition))
                    {
                        float norm = Mathf.Clamp01((ev.mousePosition.x - barRect.x) / Mathf.Max(1f, barRect.width));
                        float raw = Mathf.Lerp(min, max, norm);
                        float snapped = Mathf.Clamp(Mathf.Round(raw / step) * step, min, max);
                        if (!Mathf.Approximately(snapped, value))
                        {
                            value = snapped;
                            changed = true;
                        }
                        ev.Use();
                    }

                    GUI.color = new Color(0.82f, 0.88f, 0.96f, 1.0f);
                    if (GUI.Button(new Rect(barX + barW + 4f, rowRect.y + 2f, btnW, rowRect.height - 4f), "-"))
                    {
                        float next = Mathf.Clamp(Mathf.Round((value - step) / step) * step, min, max);
                        if (!Mathf.Approximately(next, value))
                        {
                            value = next;
                            changed = true;
                        }
                    }
                    if (GUI.Button(new Rect(barX + barW + btnW + 8f, rowRect.y + 2f, btnW, rowRect.height - 4f), "+"))
                    {
                        float next = Mathf.Clamp(Mathf.Round((value + step) / step) * step, min, max);
                        if (!Mathf.Approximately(next, value))
                        {
                            value = next;
                            changed = true;
                        }
                    }
                }
                return changed;
            }

            private static void DrawMatchAndDifficultyTab(float panelX, float panelY, float panelW, bool canControlMatch, KeyCode mpKey)
            {
                SyncMatchSettingsDraftFromMatchData();

                float topY = panelY + 86f;
                GUI.color = new Color(0.65f, 0.88f, 1.0f, 1.0f);
                GUI.Label(new Rect(panelX + 20f, topY + 3f, 115f, 22f), "Vanilla Presets:");

                float px = panelX + 136f;
                if (GUI.Button(new Rect(px, topY, 90f, 24f), "Easy") && canControlMatch)
                    ApplyPresetToMatchDraft(DifficultyValue.Easy, "Easy");
                px += 96f;
                if (GUI.Button(new Rect(px, topY, 90f, 24f), "Normal") && canControlMatch)
                    ApplyPresetToMatchDraft(DifficultyValue.Normal, "Normal");
                px += 96f;
                if (GUI.Button(new Rect(px, topY, 96f, 24f), "Realistic") && canControlMatch)
                    ApplyPresetToMatchDraft(DifficultyValue.Realistic, "Realistic");

                GUI.color = new Color(0.84f, 0.90f, 0.98f, 1.0f);
                if (GUI.Button(new Rect(panelX + panelW - 140f, topY, 120f, 24f), "Reload Active"))
                    SyncMatchSettingsDraftFromMatchData(force: true);

                float gridY = panelY + 122f;
                float rowH = 34f;
                float colW = 398f;
                float col1X = panelX + 20f;
                float col2X = panelX + 442f;
                bool d = false;

                d |= DrawFloatSettingRow(new Rect(col1X, gridY, colW, rowH), "AI Infantry Accuracy", ref _draftAiAcc, 0.25f, 2.50f, 0.05f, $"{Mathf.RoundToInt(_draftAiAcc * 100f)}%", canControlMatch);
                d |= DrawFloatSettingRow(new Rect(col1X, gridY + rowH, colW, rowH), "AI Turret / Static Acc.", ref _draftAiAccStatic, 0.25f, 2.50f, 0.05f, $"{Mathf.RoundToInt(_draftAiAccStatic * 100f)}%", canControlMatch);
                d |= DrawFloatSettingRow(new Rect(col1X, gridY + rowH * 2f, colW, rowH), "AI Aircraft Aggression", ref _draftAiAirAggro, 0.25f, 3.00f, 0.10f, $"{_draftAiAirAggro:F2}x", canControlMatch);
                d |= DrawFloatSettingRow(new Rect(col1X, gridY + rowH * 3f, colW, rowH), "Player Damage Taken", ref _draftPlayerDmg, 0.25f, 2.50f, 0.05f, $"{Mathf.RoundToInt(_draftPlayerDmg * 100f)}%", canControlMatch, accentAmber: _draftPlayerDmg > 1.05f);
                d |= DrawFloatSettingRow(new Rect(col1X, gridY + rowH * 4f, colW, rowH), "Suppression Shake", ref _draftSuppShake, 0.00f, 2.50f, 0.10f, $"{Mathf.RoundToInt(_draftSuppShake * 100f)}%", canControlMatch);

                d |= DrawFloatSettingRow(new Rect(col2X, gridY, colW, rowH), "Explosion Radius", ref _draftExpRadius, 0.50f, 2.00f, 0.05f, $"{Mathf.RoundToInt(_draftExpRadius * 100f)}%", canControlMatch);
                d |= DrawFloatSettingRow(new Rect(col2X, gridY + rowH, colW, rowH), "Stamina Pool (300%=Inf)", ref _draftStamina, 0.25f, 3.00f, 0.25f, _draftStamina >= 2.99f ? "Infinite (300%)" : $"{Mathf.RoundToInt(_draftStamina * 100f)}%", canControlMatch);
                d |= DrawFloatSettingRow(new Rect(col2X, gridY + rowH * 2f, colW, rowH), "Barrel Overheat Rate", ref _draftBarrelOverheat, 0.00f, 2.50f, 0.10f, _draftBarrelOverheat <= 0.01f ? "Off (0%)" : $"{Mathf.RoundToInt(_draftBarrelOverheat * 100f)}%", canControlMatch);
                d |= DrawFloatSettingRow(new Rect(col2X, gridY + rowH * 3f, colW, rowH), "Night Brightness", ref _draftNightLight, 0.25f, 2.00f, 0.05f, $"{Mathf.RoundToInt(_draftNightLight * 100f)}%", canControlMatch);

                // Toggles row
                float togY = panelY + 306f;
                GUI.color = new Color(0.65f, 0.88f, 1.0f, 1.0f);
                GUI.Label(new Rect(panelX + 20f, togY, 400f, 20f), "=== Battlefield HUD & Player Realism Toggles ===");
                float tbY = togY + 24f;
                float tbW = 158f;
                float tbGap = 7f;
                float tx = panelX + 20f;

                GUI.color = _draftFriendlyInd ? new Color(0.45f, 0.92f, 0.56f, 1.0f) : new Color(0.75f, 0.78f, 0.84f, 0.88f);
                if (GUI.Button(new Rect(tx, tbY, tbW, 28f), $"Friendly Icons: {(_draftFriendlyInd ? "ON" : "OFF")}") && canControlMatch) { _draftFriendlyInd = !_draftFriendlyInd; d = true; }
                tx += tbW + tbGap;

                GUI.color = _draftSuppDir ? new Color(0.45f, 0.92f, 0.56f, 1.0f) : new Color(0.75f, 0.78f, 0.84f, 0.88f);
                if (GUI.Button(new Rect(tx, tbY, tbW, 28f), $"Supp Arrows: {(_draftSuppDir ? "ON" : "OFF")}") && canControlMatch) { _draftSuppDir = !_draftSuppDir; d = true; }
                tx += tbW + tbGap;

                GUI.color = _draftAutoSurrender ? new Color(1.0f, 0.78f, 0.35f, 1.0f) : new Color(0.75f, 0.78f, 0.84f, 0.88f);
                if (GUI.Button(new Rect(tx, tbY, tbW, 28f), $"Player Surr: {(_draftAutoSurrender ? "ON" : "OFF")}") && canControlMatch) { _draftAutoSurrender = !_draftAutoSurrender; d = true; }
                tx += tbW + tbGap;

                GUI.color = _draftSimpleVehUi ? new Color(0.45f, 0.92f, 0.56f, 1.0f) : new Color(0.75f, 0.78f, 0.84f, 0.88f);
                if (GUI.Button(new Rect(tx, tbY, tbW, 28f), $"Simple Veh UI: {(_draftSimpleVehUi ? "ON" : "OFF")}") && canControlMatch) { _draftSimpleVehUi = !_draftSimpleVehUi; d = true; }
                tx += tbW + tbGap;

                GUI.color = _draftDmgAnim ? new Color(0.45f, 0.92f, 0.56f, 1.0f) : new Color(0.75f, 0.78f, 0.84f, 0.88f);
                if (GUI.Button(new Rect(tx, tbY, tbW, 28f), $"Dmg Flinch: {(_draftDmgAnim ? "ON" : "OFF")}") && canControlMatch) { _draftDmgAnim = !_draftDmgAnim; d = true; }

                // Next-Map & Queue Balance Section
                float balY = panelY + 378f;
                GUI.color = new Color(0.65f, 0.88f, 1.0f, 1.0f);
                GUI.Label(new Rect(panelX + 20f, balY, 600f, 20f), "=== Force Ratio & Tickets (Applies to Next Map / Queued Missions) ===");

                if (DrawFloatSettingRow(new Rect(panelX + 20f, balY + 26f, 440f, 34f), "Tickets Multiplier", ref _mapPickerTicketsMult, 0.25f, 3.00f, 0.25f, $"{_mapPickerTicketsMult:F2}x", canControlMatch))
                    d = true;
                float tpx = panelX + 472f;
                float[] tPresets = { 0.5f, 1.0f, 1.5f, 2.0f, 3.0f };
                for (int i = 0; i < tPresets.Length; i++)
                {
                    float tp = tPresets[i];
                    GUI.color = Mathf.Abs(_mapPickerTicketsMult - tp) < 0.05f ? new Color(0.50f, 0.94f, 0.60f, 1.0f) : new Color(0.80f, 0.86f, 0.94f, 1.0f);
                    if (GUI.Button(new Rect(tpx, balY + 28f, 54f, 26f), $"{tp:0.#}x") && canControlMatch)
                    {
                        _mapPickerTicketsMult = tp;
                        d = true;
                    }
                    tpx += 58f;
                }

                if (DrawFloatSettingRow(new Rect(panelX + 20f, balY + 66f, 440f, 34f), "Bot Scale (Total Units)", ref _mapPickerBotScale, 0.25f, 3.00f, 0.05f, $"{Mathf.RoundToInt(_mapPickerBotScale * 100f)}%", canControlMatch))
                {
                    _mapPickerBotScaleDirty = true;
                    d = true;
                }

                int biasPct = Mathf.RoundToInt(_mapPickerAlliesBias * 100f);
                string biasText = biasPct == 0 ? "Even (0%)" : (biasPct > 0 ? $"Allies +{biasPct}%" : $"Axis +{-biasPct}%");
                if (DrawFloatSettingRow(new Rect(panelX + 20f, balY + 106f, 440f, 34f), "Side Balance Bias", ref _mapPickerAlliesBias, -0.40f, 0.40f, 0.05f, biasText, canControlMatch, accentAmber: biasPct != 0))
                {
                    _mapPickerBotScaleDirty = true;
                    d = true;
                }
                GUI.color = new Color(0.82f, 0.88f, 0.96f, 1.0f);
                if (GUI.Button(new Rect(panelX + 472f, balY + 108f, 108f, 26f), "Reset Even (0%)") && canControlMatch)
                {
                    _mapPickerAlliesBias = 0f;
                    _mapPickerBotScaleDirty = true;
                    d = true;
                }

                if (d)
                {
                    _draftDiffPresetName = "Custom";
                    _matchSettingsDraftDirty = true;
                }

                MatchData curMd = MatchData.data;
                MatchBattle curMb = curMd != null ? curMd.CurrentBattle : null;
                int upcomingCount = (curMd != null && curMd.battles != null)
                    ? Mathf.Max(0, curMd.battles.Count - (curMd.currentBattleId + 1))
                    : 0;
                int curAllies = curMb != null ? curMb.maxAllies : 32;
                int curAxis = curMb != null ? curMb.maxAxis : 32;
                ComputeScaledUnitCounts(
                    curMb != null && curMb.proportionalAlliesRefValue > 0 ? curMb.proportionalAlliesRefValue : curAllies,
                    curMb != null && curMb.proportionalAxisRefValue > 0 ? curMb.proportionalAxisRefValue : curAxis,
                    _mapPickerBotScale,
                    out short pAllies,
                    out short pAxis);

                bool isMpCapActive = !Photon.Pun.PhotonNetwork.OfflineMode &&
                                     (Photon.Pun.PhotonNetwork.InRoom || (curMd != null && curMd.isMultiplayer));
                string mpCapNote = isMpCapActive
                    ? $"  [MP Cap: {VeteranAndTacticsPatches.GetOnlineMaxUnitsPerFaction(curMd)}/side]"
                    : "";
                GUI.color = new Color(0.80f, 0.90f, 0.98f, 0.96f);
                GUI.Label(new Rect(panelX + 20f, panelY + 532f, panelW - 40f, 20f),
                    $"Current Match Caps: {curAllies} Allies / {curAxis} Axis (Tickets {(curMb != null ? curMb.ticketsMultiplier : 1f):F2}x)  ->  Next Preview: {pAllies} Allies / {pAxis} Axis (Tickets {_mapPickerTicketsMult:F2}x){mpCapNote}");

                GUI.color = canControlMatch
                    ? (_matchSettingsDraftDirty || _mapPickerBotScaleDirty ? new Color(0.45f, 0.95f, 0.52f, 1.0f) : new Color(0.52f, 0.86f, 0.96f, 1.0f))
                    : new Color(0.55f, 0.55f, 0.55f, 0.65f);
                string applyBtnText = _matchSettingsDraftDirty || _mapPickerBotScaleDirty
                    ? $"Apply Difficulty & Sync Lobby ({upcomingCount} queued)*"
                    : $"Apply Difficulty & Sync Lobby ({upcomingCount} queued)";
                if (GUI.Button(new Rect(panelX + 20f, panelY + 576f, 440f, 34f), applyBtnText) && canControlMatch)
                {
                    ApplyDraftSettingsToMatchData(updateUpcomingQueue: upcomingCount > 0);
                }

                GUI.color = new Color(0.75f, 0.90f, 1.0f, 1.0f);
                if (GUI.Button(new Rect(panelX + 476f, panelY + 576f, 198f, 34f), "Go to Missions & Playlist"))
                {
                    _mapPickerActiveTab = 0;
                }

                GUI.color = new Color(1.0f, 0.80f, 0.80f, 1.0f);
                if (GUI.Button(new Rect(panelX + 686f, panelY + 576f, 154f, 34f), $"Close ({mpKey})"))
                {
                    _showMapPicker = false;
                }
                GUI.color = Color.white;
            }

            private static void DrawMapPickerWindow(float virtualW, float virtualH)
            {
                EnsureMapPickerCatalog();

                GameProgresses prog = null;
                try { prog = SavableData.Progresses; } catch { }

                float panelW = 860f;
                float panelH = 668f;
                float panelX = (virtualW - panelW) * 0.5f;
                float panelY = (virtualH - panelH) * 0.5f;

                GUI.color = new Color(0.05f, 0.06f, 0.08f, 0.95f);
                GUI.Box(new Rect(panelX, panelY, panelW, panelH), "");

                KeyCode mpKey = VisionConfig.MapPickerHotkey != null ? VisionConfig.MapPickerHotkey.Value : KeyCode.F5;
                if (mpKey != _cachedPickerHeaderKey)
                {
                    _cachedPickerHeaderKey = mpKey;
                    _cachedPickerHeaderText = $"Easy Red 2: Battle Hardened  |  Campaign, Playlist & Match Setup ({mpKey})";
                }
                GUI.color = new Color(1.0f, 0.88f, 0.35f, 1.0f);
                GUI.Label(new Rect(panelX + 20f, panelY + 10f, panelW - 360f, 22f), _cachedPickerHeaderText);

                if (_mapPickerActiveTab == 0)
                {
                    GUI.color = _revealMapPickerSpoilers
                        ? new Color(1.0f, 0.86f, 0.36f, 1.0f)
                        : new Color(0.68f, 0.78f, 0.90f, 0.95f);
                    if (GUI.Button(new Rect(panelX + panelW - 332f, panelY + 8f, 114f, 24f), _revealMapPickerSpoilers ? "Hide ??? [ON]" : "Show ???"))
                    {
                        _revealMapPickerSpoilers = !_revealMapPickerSpoilers;
                    }
                }

                GUI.color = new Color(0.75f, 0.90f, 1.0f, 1.0f);
                if (GUI.Button(new Rect(panelX + panelW - 210f, panelY + 8f, 190f, 24f), "Back to Feature Menu (F6)"))
                {
                    _showMapPicker = false;
                    _showConfigMenu = true;
                }

                // 2-Tab Navigation Header
                float tabY = panelY + 36f;
                GUI.color = _mapPickerActiveTab == 0 ? new Color(0.45f, 0.92f, 0.58f, 1.0f) : new Color(0.76f, 0.82f, 0.90f, 0.85f);
                if (GUI.Button(new Rect(panelX + 20f, tabY, 220f, 24f), "1. Missions & Playlist"))
                    _mapPickerActiveTab = 0;

                GUI.color = _mapPickerActiveTab == 1 ? new Color(0.45f, 0.92f, 0.58f, 1.0f) : new Color(0.76f, 0.82f, 0.90f, 0.85f);
                if (GUI.Button(new Rect(panelX + 248f, tabY, 220f, 24f), "2. Match & Difficulty"))
                {
                    _mapPickerActiveTab = 1;
                    SyncMatchSettingsDraftFromMatchData();
                }

                bool canControlMatch = !Photon.Pun.PhotonNetwork.InRoom || Photon.Pun.PhotonNetwork.IsMasterClient;
                GUI.color = canControlMatch
                    ? new Color(0.75f, 0.88f, 0.78f, 1.0f)
                    : new Color(1.0f, 0.65f, 0.55f, 1.0f);
                string roleNote = canControlMatch
                    ? (_mapPickerActiveTab == 0
                        ? "Select missions, [+] for multi-map playlist, 'Campaign From Here', or switch to Match & Difficulty:"
                        : "Configure vanilla Match Difficulty, Realism toggles, Tickets, and Allies/Axis Balance Bias:")
                    : "Note: In Multiplayer Co-op, only the Room Host (MasterClient) can change maps or match settings.";
                GUI.Label(new Rect(panelX + 20f, panelY + 64f, panelW - 40f, 20f), roleNote);

                if (_mapPickerActiveTab == 1)
                {
                    DrawMatchAndDifficultyTab(panelX, panelY, panelW, canControlMatch, mpKey);
                    return;
                }

                float colCampX = panelX + 20f;
                float colCampW = 304f;
                float colBatX = panelX + 338f;
                float colBatW = 502f;
                float listTopY = panelY + 86f;
                int rowsPerPage = MapPickerRowsPerPage;
                float rowH = 24f;

                // Left Column: Campaigns
                int campCount = _mapPickerCampaigns.Count;
                int totalCampPages = Mathf.Max(1, (campCount + rowsPerPage - 1) / rowsPerPage);
                _campaignPage = Mathf.Clamp(_campaignPage, 0, totalCampPages - 1);

                GUI.color = new Color(0.55f, 0.82f, 1.0f, 1.0f);
                GUI.Label(new Rect(colCampX, listTopY, colCampW - 110f, 20f), $"=== Campaigns ({campCount}) ===");
                if (totalCampPages > 1)
                {
                    GUI.color = new Color(0.85f, 0.90f, 1.0f, 1.0f);
                    if (GUI.Button(new Rect(colCampX + colCampW - 104f, listTopY - 1f, 48f, 20f), "< Prev"))
                        _campaignPage = (_campaignPage - 1 + totalCampPages) % totalCampPages;
                    if (GUI.Button(new Rect(colCampX + colCampW - 52f, listTopY - 1f, 52f, 20f), "Next >"))
                        _campaignPage = (_campaignPage + 1) % totalCampPages;
                }

                float yC = listTopY + 22f;
                int cStart = _campaignPage * rowsPerPage;
                int cEnd = Mathf.Min(campCount, cStart + rowsPerPage);
                for (int i = cStart; i < cEnd; i++)
                {
                    bool selected = i == _selectedCampaignIdx;
                    GUI.color = selected
                        ? new Color(1.0f, 0.86f, 0.32f, 1.0f)
                        : new Color(0.82f, 0.86f, 0.92f, 0.92f);

                    string prefix = selected ? ">> " : "   ";
                    PickerCampaignEntry cEntry = _mapPickerCampaigns[i];
                    int doneCount = 0;
                    bool hasAnySaveId = false;
                    if (prog != null && cEntry.Battles != null)
                    {
                        for (int k = 0; k < cEntry.Battles.Count; k++)
                        {
                            string sid = cEntry.Battles[k].SaveDataId;
                            if (string.IsNullOrEmpty(sid))
                                continue;
                            hasAnySaveId = true;
                            try
                            {
                                if (prog.IsLevelCompleted(sid))
                                    doneCount++;
                            }
                            catch { }
                        }
                    }
                    int campTotal = cEntry.Battles != null ? cEntry.Battles.Count : 0;
                    string countText;
                    if (prog != null && hasAnySaveId && campTotal > 0)
                    {
                        countText = doneCount >= campTotal
                            ? $"[{doneCount}/{campTotal} \u2605]"
                            : $"[{doneCount}/{campTotal}]";
                    }
                    else
                    {
                        countText = $"({campTotal})";
                    }

                    if (GUI.Button(new Rect(colCampX, yC, colCampW, rowH - 2f), $"{prefix}{cEntry.CampaignName} {countText}"))
                    {
                        _selectedCampaignIdx = i;
                        _selectedBattleIdx = 0;
                        _battlePage = 0;
                        _mapPickerUserPickedBattle = false;
                    }
                    yC += rowH;
                }

                // Right Column: Missions in Selected Campaign
                PickerCampaignEntry selectedCamp = (_selectedCampaignIdx >= 0 && _selectedCampaignIdx < campCount)
                    ? _mapPickerCampaigns[_selectedCampaignIdx]
                    : default;
                List<PickerBattleEntry> curBattles = selectedCamp.Battles;
                int batCount = curBattles != null ? curBattles.Count : 0;
                int totalBatPages = Mathf.Max(1, (batCount + rowsPerPage - 1) / rowsPerPage);
                _battlePage = Mathf.Clamp(_battlePage, 0, totalBatPages - 1);
                _selectedBattleIdx = Mathf.Clamp(_selectedBattleIdx, 0, Mathf.Max(0, batCount - 1));

                GUI.color = new Color(0.55f, 0.82f, 1.0f, 1.0f);
                GUI.Label(new Rect(colBatX, listTopY, colBatW - 110f, 20f), $"=== Missions ({batCount})  [[★Atk|☆Def] | [+] Playlist] ===");
                if (totalBatPages > 1)
                {
                    GUI.color = new Color(0.85f, 0.90f, 1.0f, 1.0f);
                    if (GUI.Button(new Rect(colBatX + colBatW - 104f, listTopY - 1f, 48f, 20f), "< Prev"))
                        _battlePage = (_battlePage - 1 + totalBatPages) % totalBatPages;
                    if (GUI.Button(new Rect(colBatX + colBatW - 52f, listTopY - 1f, 52f, 20f), "Next >"))
                        _battlePage = (_battlePage + 1) % totalBatPages;
                }

                GetActivePlayingBattleIdentifiers(out ActivePlayingBattleInfo active);
                int firstNextUpIdx = -1;
                if (curBattles != null && selectedCamp.TopologySorted)
                {
                    for (int k = 0; k < curBattles.Count; k++)
                    {
                        PickerBattleEntry ke = curBattles[k];
                        bool kActive = IsBattleEntryActive(in ke, in active);
                        EvaluateBattleUnlockAndStars(ke, curBattles, prog, kActive, out bool kDone, out _, out _, out _, out bool kUnlocked);
                        if (kUnlocked && !kDone)
                        {
                            firstNextUpIdx = k;
                            break;
                        }
                    }
                }

                float yB = listTopY + 22f;
                float batBtnW = colBatW - 46f;
                float addBtnX = colBatX + batBtnW + 4f;
                float addBtnW = 42f;
                if (curBattles != null)
                {
                    int bStart = _battlePage * rowsPerPage;
                    int bEnd = Mathf.Min(batCount, bStart + rowsPerPage);
                    for (int j = bStart; j < bEnd; j++)
                    {
                        bool selected = j == _selectedBattleIdx;
                        PickerBattleEntry bEntry = GetResolvedBattleEntry(curBattles, j);
                        bool isNowPlaying = IsBattleEntryActive(in bEntry, in active);
                        EvaluateBattleUnlockAndStars(bEntry, curBattles, prog, isNowPlaying, out bool isCompleted, out bool wonBoth, out bool wonInv, out bool wonDef, out bool isUnlocked);

                        bool maskedAsUnknown = !isUnlocked && !_revealMapPickerSpoilers;
                        if (selected)
                        {
                            GUI.color = maskedAsUnknown
                                ? new Color(0.78f, 0.72f, 0.52f, 0.96f)
                                : new Color(0.45f, 0.95f, 0.52f, 1.0f);
                        }
                        else if (isNowPlaying)
                        {
                            GUI.color = new Color(0.42f, 0.92f, 1.0f, 1.0f);
                        }
                        else if (j == firstNextUpIdx && !maskedAsUnknown)
                        {
                            GUI.color = new Color(0.66f, 0.96f, 0.70f, 1.0f);
                        }
                        else if (isCompleted)
                        {
                            GUI.color = new Color(0.98f, 0.90f, 0.58f, 0.96f);
                        }
                        else if (isUnlocked)
                        {
                            GUI.color = new Color(0.86f, 0.92f, 0.98f, 0.96f);
                        }
                        else if (_revealMapPickerSpoilers)
                        {
                            GUI.color = new Color(0.78f, 0.72f, 0.64f, 0.85f);
                        }
                        else
                        {
                            GUI.color = new Color(0.55f, 0.59f, 0.65f, 0.78f);
                        }

                        string prefix = selected ? "[SELECTED] " : $"#{j + 1}. ";
                        string badge = isCompleted ? (wonBoth ? "★★ " : "★ ") : (!isUnlocked && _revealMapPickerSpoilers ? "[Locked] " : "");
                        string sideBadge = "";
                        if (!maskedAsUnknown)
                        {
                            string invMark = wonInv ? "\u2605" : "\u2606";
                            string defMark = wonDef ? "\u2605" : "\u2606";
                            if (bEntry.FactionsResolved)
                                sideBadge = $"[{invMark}{bEntry.InvaderTag}|{defMark}{bEntry.DefenderTag}] ";
                            else if (isCompleted)
                                sideBadge = $"[{invMark}|{defMark}] ";
                        }
                        string statusTag = "";
                        if (!maskedAsUnknown)
                        {
                            if (bEntry.IsFinale) statusTag += " [FINALE]";
                            if (isNowPlaying) statusTag += " [NOW PLAYING]";
                            else if (j == firstNextUpIdx) statusTag += " [NEXT UP]";
                        }
                        string labelTitle = maskedAsUnknown ? "??? (Locked)" : $"{badge}{sideBadge}{bEntry.DisplayName}{statusTag}";

                        if (GUI.Button(new Rect(colBatX, yB, batBtnW, rowH - 2f), $"{prefix}{labelTitle}"))
                        {
                            _selectedBattleIdx = j;
                            _mapPickerUserPickedBattle = true;
                        }

                        int plOrder = FindPlaylistIndex(in bEntry);
                        GUI.color = plOrder >= 0
                            ? new Color(0.48f, 0.95f, 0.58f, 1.0f)
                            : new Color(0.72f, 0.82f, 0.96f, 0.92f);
                        string plBtnLabel = plOrder >= 0 ? $"#{plOrder + 1}" : "+";
                        if (GUI.Button(new Rect(addBtnX, yB, addBtnW, rowH - 2f), plBtnLabel) && bEntry.BattleRef != null)
                        {
                            string plVisibleName = maskedAsUnknown ? $"Mission #{j + 1} (Locked)" : bEntry.DisplayName;
                            TogglePlaylistEntry(bEntry, plVisibleName);
                        }

                        yB += rowH;
                    }
                }

                // Bottom Selection Summary, Playlist Strip, Bot Scale Slider & Launch/Queue Actions
                float summaryY = panelY + 474f;
                string selSummary = "No mission selected.";
                string chosenVisibleName = null;
                PickerBattleEntry chosenBattle = default;
                bool hasChosen = false;
                if (curBattles != null && _selectedBattleIdx >= 0 && _selectedBattleIdx < curBattles.Count)
                {
                    chosenBattle = GetResolvedBattleEntry(curBattles, _selectedBattleIdx);
                    EnsureBattleUnitCountsResolved(ref chosenBattle, curBattles, _selectedBattleIdx);
                    hasChosen = chosenBattle.BattleRef != null;
                    bool selNowPlaying = IsBattleEntryActive(in chosenBattle, in active);
                    EvaluateBattleUnlockAndStars(chosenBattle, curBattles, prog, selNowPlaying, out bool selDone, out bool selBoth, out bool selInvWon, out bool selDefWon, out bool selUnlocked);
                    bool selMasked = !selUnlocked && !_revealMapPickerSpoilers;
                    string selBadge = selDone ? (selBoth ? "★★ " : "★ ") : "";
                    string selStatusTag = "";
                    if (!selMasked)
                    {
                        if (chosenBattle.IsFinale) selStatusTag += " [FINALE]";
                        if (selNowPlaying) selStatusTag += " [NOW PLAYING]";
                        else if (_selectedBattleIdx == firstNextUpIdx) selStatusTag += " [NEXT UP]";
                    }
                    string selCompText = "";
                    if (!selMasked)
                    {
                        string invT = !string.IsNullOrEmpty(chosenBattle.InvaderTag) ? chosenBattle.InvaderTag : "ATK";
                        string defT = !string.IsNullOrEmpty(chosenBattle.DefenderTag) ? chosenBattle.DefenderTag : "DEF";
                        selCompText = $"  |  {invT}:{(selInvWon ? "\u2605" : "\u2606")} {defT}:{(selDefWon ? "\u2605" : "\u2606")}";
                    }
                    chosenVisibleName = selMasked ? "??? (Locked)" : chosenBattle.DisplayName;
                    string campTitle = _mapPickerCampaigns[_selectedCampaignIdx].CampaignName;
                    MatchData activeMd = MatchData.data;
                    int playlistCount = activeMd != null && activeMd.battles != null ? activeMd.battles.Count : 0;
                    int curIdx = activeMd != null ? activeMd.currentBattleId + 1 : 1;
                    string modeLabel = activeMd != null ? activeMd.playType.ToString() : "Single";
                    selSummary = $"Selected: [{campTitle}]  {selBadge}{chosenVisibleName}{selStatusTag}{selCompText}  |  Queue: {curIdx}/{playlistCount} ({modeLabel})";
                }

                GUI.color = new Color(0.95f, 0.92f, 0.78f, 1.0f);
                GUI.Label(new Rect(panelX + 20f, summaryY, panelW - 40f, 20f), selSummary);

                // Playlist Preview Strip
                float playlistY = panelY + 497f;
                StringBuilder plSb = new StringBuilder(128);
                plSb.Append($"Multi-Map Playlist ({_mapPickerPlaylist.Count}): ");
                if (_mapPickerPlaylist.Count == 0)
                {
                    plSb.Append("(click [+] on missions above to build an ordered multi-map list)");
                }
                else
                {
                    int showMax = Math.Min(_mapPickerPlaylist.Count, 5);
                    for (int k = 0; k < showMax; k++)
                    {
                        if (k > 0) plSb.Append(" -> ");
                        plSb.Append($"{k + 1}.{_mapPickerPlaylist[k].VisibleName}");
                    }
                    if (_mapPickerPlaylist.Count > showMax)
                        plSb.Append($" ... (+{_mapPickerPlaylist.Count - showMax} more)");
                }
                GUI.color = _mapPickerPlaylist.Count > 0
                    ? new Color(0.65f, 0.96f, 0.74f, 1.0f)
                    : new Color(0.72f, 0.76f, 0.84f, 0.92f);
                GUI.Label(new Rect(panelX + 20f, playlistY + 1f, panelW - 144f, 20f), plSb.ToString());

                GUI.color = _mapPickerPlaylist.Count > 0
                    ? new Color(1.0f, 0.76f, 0.68f, 1.0f)
                    : new Color(0.55f, 0.55f, 0.55f, 0.65f);
                if (GUI.Button(new Rect(panelX + panelW - 116f, playlistY, 96f, 22f), "Clear List") && _mapPickerPlaylist.Count > 0)
                {
                    _mapPickerPlaylist.Clear();
                }

                // Next-Map Bot Scale Slider Bar (fits cleanly inside 820px inner width with wordWrap=false)
                float sliderY = panelY + 524f;
                int botPct = Mathf.RoundToInt(_mapPickerBotScale * 100f);
                float sx = panelX + 132f;
                if (DrawBotScaleSliderBar(new Rect(sx, sliderY, 206f, 23f), ref _mapPickerBotScale))
                {
                    _mapPickerBotScaleDirty = true;
                    botPct = Mathf.RoundToInt(_mapPickerBotScale * 100f);
                }
                GUI.color = new Color(0.85f, 0.94f, 1.0f, 1.0f);
                GUI.Label(new Rect(panelX + 20f, sliderY + 2f, 110f, 20f), _cachedSliderRowLabelText);
                sx += 212f;

                GUI.color = new Color(0.82f, 0.90f, 1.0f, 1.0f);
                if (GUI.Button(new Rect(sx, sliderY, 44f, 23f), "-10%"))
                {
                    _mapPickerBotScale = Mathf.Clamp(Mathf.Round((_mapPickerBotScale - 0.10f) * 20f) / 20f, 0.25f, 3.00f);
                    _mapPickerBotScaleDirty = true;
                }
                sx += 48f;
                if (GUI.Button(new Rect(sx, sliderY, 44f, 23f), "+10%"))
                {
                    _mapPickerBotScale = Mathf.Clamp(Mathf.Round((_mapPickerBotScale + 0.10f) * 20f) / 20f, 0.25f, 3.00f);
                    _mapPickerBotScaleDirty = true;
                }
                sx += 50f;

                for (int p = 0; p < _botScalePresets.Length; p++)
                {
                    float pv = _botScalePresets[p];
                    float bw = p == 0 ? 42f : 48f;
                    bool isCur = Mathf.Abs(_mapPickerBotScale - pv) < 0.025f;
                    GUI.color = isCur ? new Color(0.55f, 0.95f, 0.62f, 1.0f) : new Color(0.80f, 0.86f, 0.94f, 1.0f);
                    if (GUI.Button(new Rect(sx, sliderY, bw, 23f), _botScalePresetLabels[p]))
                    {
                        _mapPickerBotScale = pv;
                        _mapPickerBotScaleDirty = true;
                    }
                    sx += bw + 4f;
                }

                MatchData curMd = MatchData.data;
                int upcomingCount = (curMd != null && curMd.battles != null)
                    ? Mathf.Max(0, curMd.battles.Count - (curMd.currentBattleId + 1))
                    : 0;
                GUI.color = (upcomingCount > 0 && canControlMatch)
                    ? (_mapPickerBotScaleDirty ? new Color(1.0f, 0.84f, 0.34f, 1.0f) : new Color(0.62f, 0.90f, 1.0f, 1.0f))
                    : new Color(0.55f, 0.55f, 0.55f, 0.65f);
                string applyQueueText = upcomingCount > 0
                    ? (_mapPickerBotScaleDirty ? $"Apply Queue ({upcomingCount})*" : $"Apply Queue ({upcomingCount})")
                    : "Apply Queue (0)";
                if (GUI.Button(new Rect(panelX + panelW - 144f, sliderY, 124f, 23f), applyQueueText) && upcomingCount > 0 && canControlMatch)
                {
                    int updated = ApplyBotScaleToQueuedBattles(_mapPickerBotScale, out short nextA, out short nextX);
                    ShowToast($"[Battle Hardened] Applied {Mathf.RoundToInt(_mapPickerBotScale * 100f)}% Bot Scale to {updated} queued mission(s) (Next: {nextA} Allies vs {nextX} Axis).");
                }

                // Preview line showing selected mission & upcoming queued mission scaled unit counts
                float previewY = panelY + 553f;
                string unitPreviewText;
                if (hasChosen && chosenBattle.DefaultMaxAllies > 0 && chosenBattle.DefaultMaxAxis > 0)
                {
                    ComputeScaledUnitCounts(chosenBattle.DefaultMaxAllies, chosenBattle.DefaultMaxAxis, _mapPickerBotScale, out short selScaledA, out short selScaledX);
                    unitPreviewText = $"Selected Unit Cap: {chosenBattle.DefaultMaxAllies} Allies / {chosenBattle.DefaultMaxAxis} Axis  ->  {selScaledA} Allies / {selScaledX} Axis";
                }
                else
                {
                    unitPreviewText = $"Bot Scale Multiplier: {botPct}% (applies per mission's default Allies/Axis unit budget)";
                }
                if (upcomingCount > 0 && curMd != null && curMd.battles != null)
                {
                    MatchBattle nextMb = curMd.battles[curMd.currentBattleId + 1];
                    if (nextMb != null)
                    {
                        EnsureBattleRefUnitCountsResolved(nextMb.battle_reference, nextMb, out int nBaseA, out int nBaseX);
                        ComputeScaledUnitCounts(nBaseA, nBaseX, _mapPickerBotScale, out short nScaledA, out short nScaledX);
                        unitPreviewText += $"   |   Next Map Queue: currently {nextMb.maxAllies}/{nextMb.maxAxis} (preview {nScaledA}/{nScaledX})";
                    }
                }
                if (!Photon.Pun.PhotonNetwork.OfflineMode &&
                    (Photon.Pun.PhotonNetwork.InRoom || (curMd != null && curMd.isMultiplayer)))
                {
                    unitPreviewText += $"  [MP Cap: {VeteranAndTacticsPatches.GetOnlineMaxUnitsPerFaction(curMd)}/side]";
                }
                GUI.color = new Color(0.76f, 0.88f, 0.96f, 0.95f);
                GUI.Label(new Rect(panelX + 20f, previewY, panelW - 40f, 20f), unitPreviewText);

                int campFromHereCount = (curBattles != null && _selectedBattleIdx >= 0 && _selectedBattleIdx < curBattles.Count)
                    ? (curBattles.Count - _selectedBattleIdx)
                    : 0;
                bool hasCampFromHere = hasChosen && campFromHereCount > 0;
                bool hasPlaylist = _mapPickerPlaylist.Count > 0;

                // Bottom Row 1: Start / Replace Active Playlist Now
                float row1Y = panelY + 579f;
                GUI.color = (hasChosen && canControlMatch)
                    ? new Color(0.45f, 0.95f, 0.52f, 1.0f)
                    : new Color(0.55f, 0.55f, 0.55f, 0.70f);
                if (GUI.Button(new Rect(panelX + 20f, row1Y, 202f, 28f), "Play Selected Now") && hasChosen && canControlMatch)
                {
                    var single = new List<QueuedPickerEntry>(1)
                    {
                        new QueuedPickerEntry
                        {
                            Battle = chosenBattle,
                            VisibleName = chosenVisibleName ?? chosenBattle.DisplayName
                        }
                    };
                    ExecuteMapPickerLaunchOrQueue(single, switchNow: true, isCampaignFromHere: false, single[0].VisibleName);
                }

                GUI.color = (hasCampFromHere && canControlMatch)
                    ? new Color(0.48f, 0.92f, 0.68f, 1.0f)
                    : new Color(0.55f, 0.55f, 0.55f, 0.70f);
                if (GUI.Button(new Rect(panelX + 230f, row1Y, 220f, 28f), $"Campaign From Here ({campFromHereCount})") && hasCampFromHere && canControlMatch)
                {
                    List<QueuedPickerEntry> campSeq = BuildCampaignFromIndexItems(
                        curBattles, _selectedBattleIdx, prog, in active);
                    if (campSeq.Count > 0)
                    {
                        string campTitle = _mapPickerCampaigns[_selectedCampaignIdx].CampaignName;
                        ExecuteMapPickerLaunchOrQueue(campSeq, switchNow: true, isCampaignFromHere: true, $"{campTitle}: {campSeq[0].VisibleName} (+{campSeq.Count - 1})");
                    }
                }

                GUI.color = (hasPlaylist && canControlMatch)
                    ? new Color(0.48f, 0.88f, 0.98f, 1.0f)
                    : new Color(0.55f, 0.55f, 0.55f, 0.70f);
                if (GUI.Button(new Rect(panelX + 458f, row1Y, 220f, 28f), $"Play Playlist Now ({_mapPickerPlaylist.Count})") && hasPlaylist && canControlMatch)
                {
                    var copy = new List<QueuedPickerEntry>(_mapPickerPlaylist);
                    string summary = copy.Count == 1 ? copy[0].VisibleName : $"{copy[0].VisibleName} (+{copy.Count - 1} maps)";
                    ExecuteMapPickerLaunchOrQueue(copy, switchNow: true, isCampaignFromHere: false, summary);
                }

                GUI.color = new Color(1.0f, 0.80f, 0.80f, 1.0f);
                if (GUI.Button(new Rect(panelX + 686f, row1Y, 154f, 28f), $"Close Picker ({mpKey})"))
                {
                    _showMapPicker = false;
                }

                // Bottom Row 2: Queue After Current Mission
                float row2Y = panelY + 614f;
                GUI.color = (hasChosen && canControlMatch)
                    ? new Color(0.58f, 0.84f, 1.0f, 1.0f)
                    : new Color(0.55f, 0.55f, 0.55f, 0.70f);
                if (GUI.Button(new Rect(panelX + 20f, row2Y, 202f, 28f), "Queue Selected After Current") && hasChosen && canControlMatch)
                {
                    var single = new List<QueuedPickerEntry>(1)
                    {
                        new QueuedPickerEntry
                        {
                            Battle = chosenBattle,
                            VisibleName = chosenVisibleName ?? chosenBattle.DisplayName
                        }
                    };
                    ExecuteMapPickerLaunchOrQueue(single, switchNow: false, isCampaignFromHere: false, single[0].VisibleName);
                }

                GUI.color = (hasCampFromHere && canControlMatch)
                    ? new Color(0.66f, 0.88f, 0.98f, 1.0f)
                    : new Color(0.55f, 0.55f, 0.55f, 0.70f);
                if (GUI.Button(new Rect(panelX + 230f, row2Y, 220f, 28f), $"Queue Campaign From Here ({campFromHereCount})") && hasCampFromHere && canControlMatch)
                {
                    List<QueuedPickerEntry> campSeq = BuildCampaignFromIndexItems(
                        curBattles, _selectedBattleIdx, prog, in active);
                    if (campSeq.Count > 0)
                    {
                        string campTitle = _mapPickerCampaigns[_selectedCampaignIdx].CampaignName;
                        ExecuteMapPickerLaunchOrQueue(campSeq, switchNow: false, isCampaignFromHere: true, $"{campTitle}: {campSeq.Count} missions");
                    }
                }

                GUI.color = (hasPlaylist && canControlMatch)
                    ? new Color(0.72f, 0.86f, 1.0f, 1.0f)
                    : new Color(0.55f, 0.55f, 0.55f, 0.70f);
                if (GUI.Button(new Rect(panelX + 458f, row2Y, 220f, 28f), $"Queue Playlist ({_mapPickerPlaylist.Count})") && hasPlaylist && canControlMatch)
                {
                    var copy = new List<QueuedPickerEntry>(_mapPickerPlaylist);
                    ExecuteMapPickerLaunchOrQueue(copy, switchNow: false, isCampaignFromHere: false, $"{copy.Count} playlist missions");
                }

                GUI.color = (upcomingCount > 0 && canControlMatch)
                    ? new Color(1.0f, 0.74f, 0.66f, 1.0f)
                    : new Color(0.55f, 0.55f, 0.55f, 0.65f);
                if (GUI.Button(new Rect(panelX + 686f, row2Y, 154f, 28f), $"Clear Upcoming ({upcomingCount})") && upcomingCount > 0 && canControlMatch && curMd != null && curMd.battles != null)
                {
                    int removeStart = curMd.currentBattleId + 1;
                    int removeCount = curMd.battles.Count - removeStart;
                    if (removeStart >= 0 && removeCount > 0)
                    {
                        curMd.battles.RemoveRange(removeStart, removeCount);
                        if (Photon.Pun.PhotonNetwork.InRoom && Photon.Pun.PhotonNetwork.IsMasterClient)
                        {
                            try { curMd.UpdateNetworkMatchInfo(); } catch { }
                        }
                        ShowToast($"[Battle Hardened] Cleared {removeCount} upcoming mission(s) from match queue.");
                    }
                }

                GUI.color = Color.white;
            }

            private static void ExecuteMapPickerLaunchOrQueue(
                List<QueuedPickerEntry> items,
                bool switchNow,
                bool isCampaignFromHere,
                string labelSummary)
            {
                if (items == null || items.Count == 0)
                    return;

                try
                {
                    if (switchNow && _mpSwitchStage != 0)
                    {
                        ShowToast($"[Battle Hardened] Already switching Multiplayer Room to '{_mpSwitchDisplayName}'...");
                        return;
                    }

                    MatchData md = MatchData.data;
                    if (md == null)
                    {
                        ShowToast("[Battle Hardened] MatchData is not active yet.");
                        return;
                    }

                    if (Photon.Pun.PhotonNetwork.InRoom && !Photon.Pun.PhotonNetwork.IsMasterClient)
                    {
                        ShowToast("[Battle Hardened] Only the Multiplayer Host can change or queue missions.");
                        return;
                    }

                    MatchBattle curMb = md.CurrentBattle;
                    if (md.battles == null)
                        md.ResetSelectedBattles();

                    var configured = new List<MatchBattle>(items.Count);
                    for (int i = 0; i < items.Count; i++)
                    {
                        QueuedPickerEntry item = items[i];
                        if (item.Battle.BattleRef == null)
                            continue;
                        PickerBattleEntry copyEntry = item.Battle;
                        configured.Add(CreateConfiguredMatchBattle(ref copyEntry, curMb, _mapPickerBotScale));
                    }

                    if (configured.Count == 0)
                        return;

                    if (!switchNow)
                    {
                        int firstNewSlot = md.battles.Count + 1;
                        for (int i = 0; i < configured.Count; i++)
                            md.battles.Add(configured[i]);

                        if (isCampaignFromHere)
                            md.playType = MatchData.MissionsPlayType.FullCampaign;
                        else if (md.battles.Count > 1 && md.playType == MatchData.MissionsPlayType.Single)
                            md.playType = MatchData.MissionsPlayType.MultiSelectOrdered;

                        if (Photon.Pun.PhotonNetwork.InRoom && Photon.Pun.PhotonNetwork.IsMasterClient)
                        {
                            md.UpdateNetworkMatchInfo();
                        }

                        MatchBattle firstAdded = configured[0];
                        int pct = Mathf.RoundToInt(_mapPickerBotScale * 100f);
                        ShowToast($"[Battle Hardened] Queued {configured.Count} mission(s) ({labelSummary}) at slot #{firstNewSlot} (Bot Scale {pct}%: {firstAdded.maxAllies} Allies vs {firstAdded.maxAxis} Axis).");
                        return;
                    }

                    md.battles.Clear();
                    for (int i = 0; i < configured.Count; i++)
                        md.battles.Add(configured[i]);

                    if (isCampaignFromHere)
                        md.playType = MatchData.MissionsPlayType.FullCampaign;
                    else if (configured.Count > 1)
                        md.playType = MatchData.MissionsPlayType.MultiSelectOrdered;
                    else
                        md.playType = MatchData.MissionsPlayType.Single;

                    _showMapPicker = false;
                    _showConfigMenu = false;
                    md.currentBattleId = 0;
                    md.loadedBattle = null;
                    BattleManager.SetPlayerFaction("");

                    if (Photon.Pun.PhotonNetwork.InRoom)
                    {
                        VeteranAndTacticsPatches.ClampMatchBattlesToOnlineEnvelope(md);
                        BattleLoader bl = BattleLoader.instance;
                        if (bl != null)
                        {
                            try { bl.StopVoting(); } catch { }
                        }

                        bool hasRemoteClients = Photon.Pun.PhotonNetwork.CurrentRoom != null &&
                                                Photon.Pun.PhotonNetwork.CurrentRoom.PlayerCount > 1;
                        if (bl != null && bl.photonView != null && hasRemoteClients)
                        {
                            var rawBytes = ResourcesManager.SerializeToByteArray(md.Cast<Il2CppSystem.Object>());
                            var waitArr = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<float>(1);
                            waitArr[0] = 3.0f;
                            var rpcArgs = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Il2CppSystem.Object>(2);
                            rpcArgs[0] = rawBytes.Cast<Il2CppSystem.Object>();
                            rpcArgs[1] = waitArr.Cast<Il2CppSystem.Array>().GetValue(0);
                            bl.photonView.RPC("ConnectToNextMatch", Photon.Pun.RpcTarget.Others, rpcArgs);
                            Photon.Pun.PhotonNetwork.SendAllOutgoingCommands();
                        }

                        // Replicate vanilla EndBattleGUI.VictoryAnimation (0x180499286):
                        // NEVER call bl.LoadNextMpMatch() on the Host (which sets MatchData.actorType = client!).
                        // SetMultiplayerRoomSettings() keeps actorType = master (so TryConnect() calls CreateRoom instead of JoinRoom)
                        // and refreshes RoomOptions custom properties for the new battle.
                        md.SetMultiplayerRoomSettings();
                        MatchData.actorType = MatchData.RoomActorType.master;
                        _mpSwitchDisplayName = labelSummary;
                        ActivateMpTransitionBanner(isClient: false, mapName: labelSummary);

                        if (hasRemoteClients)
                        {
                            _mpSwitchStage = 1;
                            _mpSwitchStageDeadline = Time.unscaledTime + 1.0f;
                        }
                        else
                        {
                            BeginHostLeaveRoomForMapSwitch(bl);
                        }

                        ShowToast($"[Battle Hardened] Switching Multiplayer Room to '{labelSummary}' ({configured.Count} map(s), {Mathf.RoundToInt(_mapPickerBotScale * 100f)}% bots)...");
                        return;
                    }

                    md.StartBattleLoading();
                    ShowToast($"[Battle Hardened] Loading '{labelSummary}' ({configured.Count} map(s), {Mathf.RoundToInt(_mapPickerBotScale * 100f)}% bots)...");
                }
                catch (Exception ex)
                {
                    _mpSwitchStage = 0;
                    ShowToast($"[Battle Hardened] Map Picker error: {ex.Message}");
                }
            }
        }
    }
}
