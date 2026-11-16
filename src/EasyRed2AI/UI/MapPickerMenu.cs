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
            public bool IsVanilla;
            public bool InitialIsUnlocked;
            public bool MetadataResolved;
            public bool UnitCountsResolved;
            public bool IsFinale;
            public int DefaultMaxAllies;
            public int DefaultMaxAxis;
            public ICampaignTreeBattleData BattleRef;
        }

        private struct PickerCampaignEntry
        {
            public string CampaignName;
            public bool TopologySorted;
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
        private static int _selectedCampaignIdx;
        private static int _selectedBattleIdx;
        private static int _campaignPage;
        private static int _battlePage;
        private static int _mpSwitchStage;
        private static float _mpSwitchStageDeadline;
        private static string _mpSwitchDisplayName;
        private static bool _mpSwitchRetriedConnect;

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
                        DisplayName = null, // Lazily resolved per visible page to avoid upfront XML disk I/O & Missing translation spam
                        UniqueId = uid,
                        SaveDataId = isVanillaCamp ? uid : null,
                        PreviousSaveDataId = null,
                        IsVanilla = isVanillaCamp,
                        InitialIsUnlocked = !isVanillaCamp,
                        MetadataResolved = !isVanillaCamp,
                        BattleRef = bRef
                    });
                }

                if (bList.Count > 0)
                {
                    _mapPickerCampaigns.Add(new PickerCampaignEntry
                    {
                        CampaignName = campName,
                        TopologySorted = !isVanillaCamp,
                        Battles = bList
                    });
                }
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
                var inDeg = new int[n];
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
                        if (nextNodes[u] == null)
                            nextNodes[u] = new List<int>(2);
                        if (!nextNodes[u].Contains(v))
                        {
                            nextNodes[u].Add(v);
                            inDeg[v]++;
                            outDeg[u]++;
                            edgeCount++;
                        }
                    }
                }

                var visited = new bool[n];
                var order = new List<int>(n);
                while (order.Count < n)
                {
                    int pick = -1;
                    for (int i = 0; i < n; i++)
                    {
                        if (!visited[i] && inDeg[i] == 0)
                        {
                            pick = i;
                            break;
                        }
                    }

                    if (pick < 0)
                        break;

                    visited[pick] = true;
                    order.Add(pick);
                    List<int> adj = nextNodes[pick];
                    if (adj != null)
                    {
                        for (int k = 0; k < adj.Count; k++)
                            inDeg[adj[k]]--;
                    }
                }

                for (int i = 0; i < n; i++)
                {
                    if (!visited[i])
                        order.Add(i);
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

            // Amortizes IBattle / BattleData metadata loading (battle_id, previousMission_id, SaveDataId, PreviousMission_SaveDataId)
            // at strictly <= 3 missions per frame while F5 is open so sp_ME_ campaigns resolve exact progression and sort cleanly.
            internal static void StepResolveSelectedCampaignBattles()
            {
                if (!_showMapPicker || !_mapPickerInitialized)
                    return;

                if (_selectedCampaignIdx < 0 || _selectedCampaignIdx >= _mapPickerCampaigns.Count)
                    return;

                PickerCampaignEntry camp = _mapPickerCampaigns[_selectedCampaignIdx];
                List<PickerBattleEntry> list = camp.Battles;
                if (list == null || list.Count == 0)
                    return;

                GetActivePlayingBattleIdentifiers(out ActivePlayingBattleInfo active);
                int count = list.Count;
                int bStart = Mathf.Clamp(_battlePage * MapPickerRowsPerPage, 0, count - 1);
                int resolvedThisFrame = 0;
                long startTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                long budgetTicks = System.Diagnostics.Stopwatch.Frequency / 500; // 2.0ms per-frame budget

                for (int offset = 0; offset < count; offset++)
                {
                    int idx = (bStart + offset) % count;
                    PickerBattleEntry entry = list[idx];
                    if (entry.MetadataResolved && (entry.UnitCountsResolved || (entry.DefaultMaxAllies > 0 && entry.DefaultMaxAxis > 0)))
                        continue;

                    bool needFullMeta = !entry.MetadataResolved;
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
                                    }
                                    PopulateEntryUnitCounts(ref entry, bd);
                                }
                            }
                        }
                    }
                    catch { }

                    if (string.IsNullOrEmpty(entry.SaveDataId))
                        entry.SaveDataId = entry.UniqueId;

                    entry.MetadataResolved = true;
                    entry.UnitCountsResolved = true;
                    list[idx] = entry;

                    if (!_mapPickerUserPickedBattle && IsBattleEntryActive(in entry, in active))
                    {
                        _selectedBattleIdx = idx;
                        _battlePage = idx / MapPickerRowsPerPage;
                    }

                    if (++resolvedThisFrame >= 3 || (System.Diagnostics.Stopwatch.GetTimestamp() - startTicks) >= budgetTicks)
                        break;
                }

                if (!camp.TopologySorted)
                {
                    bool allResolved = true;
                    for (int k = 0; k < list.Count; k++)
                    {
                        if (!list[k].MetadataResolved)
                        {
                            allResolved = false;
                            break;
                        }
                    }

                    if (allResolved)
                    {
                        SortCampaignBattlesTopologically(ref camp);
                        _mapPickerCampaigns[_selectedCampaignIdx] = camp;
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
                out bool isUnlocked)
            {
                isCompleted = false;
                wonBothFactions = false;

                if (prog != null && !string.IsNullOrEmpty(bEntry.SaveDataId))
                {
                    try
                    {
                        var comp = prog.GetCompletedLevelData(bEntry.SaveDataId);
                        if (comp != null && comp.IsCompleted())
                        {
                            isCompleted = true;
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
                scaledAllies = (short)Mathf.Clamp(Mathf.RoundToInt(Mathf.Max(1, baseAllies) * clampedScale), 1, 500);
                scaledAxis = (short)Mathf.Clamp(Mathf.RoundToInt(Mathf.Max(1, baseAxis) * clampedScale), 1, 500);
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
                if (curMb != null)
                {
                    newMb.ticketsMultiplier = curMb.ticketsMultiplier;
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
                    EvaluateBattleUnlockAndStars(entry, curBattles, prog, active, out _, out _, out bool unlocked);
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

            private static void DrawMapPickerWindow(float virtualW, float virtualH)
            {
                EnsureMapPickerCatalog();

                GameProgresses prog = null;
                try { prog = SavableData.Progresses; } catch { }

                float panelW = 860f;
                float panelH = 640f;
                float panelX = (virtualW - panelW) * 0.5f;
                float panelY = (virtualH - panelH) * 0.5f;

                GUI.color = new Color(0.05f, 0.06f, 0.08f, 0.95f);
                GUI.Box(new Rect(panelX, panelY, panelW, panelH), "");

                KeyCode mpKey = VisionConfig.MapPickerHotkey != null ? VisionConfig.MapPickerHotkey.Value : KeyCode.F5;
                if (mpKey != _cachedPickerHeaderKey)
                {
                    _cachedPickerHeaderKey = mpKey;
                    _cachedPickerHeaderText = $"Easy Red 2: Battle Hardened  |  Campaign, Playlist & Mission Picker ({mpKey})";
                }
                GUI.color = new Color(1.0f, 0.88f, 0.35f, 1.0f);
                GUI.Label(new Rect(panelX + 20f, panelY + 10f, panelW - 360f, 22f), _cachedPickerHeaderText);

                GUI.color = _revealMapPickerSpoilers
                    ? new Color(1.0f, 0.86f, 0.36f, 1.0f)
                    : new Color(0.68f, 0.78f, 0.90f, 0.95f);
                if (GUI.Button(new Rect(panelX + panelW - 332f, panelY + 8f, 114f, 24f), _revealMapPickerSpoilers ? "Hide ??? [ON]" : "Show ???"))
                {
                    _revealMapPickerSpoilers = !_revealMapPickerSpoilers;
                }

                GUI.color = new Color(0.75f, 0.90f, 1.0f, 1.0f);
                if (GUI.Button(new Rect(panelX + panelW - 210f, panelY + 8f, 190f, 24f), "Back to Feature Menu (F6)"))
                {
                    _showMapPicker = false;
                    _showConfigMenu = true;
                }

                bool canControlMatch = !Photon.Pun.PhotonNetwork.InRoom || Photon.Pun.PhotonNetwork.IsMasterClient;
                GUI.color = canControlMatch
                    ? new Color(0.75f, 0.88f, 0.78f, 1.0f)
                    : new Color(1.0f, 0.65f, 0.55f, 1.0f);
                string roleNote = canControlMatch
                    ? "Select missions, toggle [+] to build a multi-map playlist, pick 'Campaign From Here', and tune Next Bot Scale:"
                    : "Note: In Multiplayer Co-op, only the Room Host (MasterClient) can switch or queue missions.";
                GUI.Label(new Rect(panelX + 20f, panelY + 34f, panelW - 40f, 20f), roleNote);

                float colCampX = panelX + 20f;
                float colCampW = 304f;
                float colBatX = panelX + 338f;
                float colBatW = 502f;
                float listTopY = panelY + 56f;
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
                GUI.Label(new Rect(colBatX, listTopY, colBatW - 110f, 20f), $"=== Missions ({batCount})  [★ Beat | [+] Add to Playlist] ===");
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
                        EvaluateBattleUnlockAndStars(ke, curBattles, prog, kActive, out bool kDone, out _, out bool kUnlocked);
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
                        EvaluateBattleUnlockAndStars(bEntry, curBattles, prog, isNowPlaying, out bool isCompleted, out bool wonBoth, out bool isUnlocked);

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
                        string statusTag = "";
                        if (!maskedAsUnknown)
                        {
                            if (bEntry.IsFinale) statusTag += " [FINALE]";
                            if (isNowPlaying) statusTag += " [NOW PLAYING]";
                            else if (j == firstNextUpIdx) statusTag += " [NEXT UP]";
                        }
                        string labelTitle = maskedAsUnknown ? "??? (Locked)" : $"{badge}{bEntry.DisplayName}{statusTag}";

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
                float summaryY = panelY + 444f;
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
                    EvaluateBattleUnlockAndStars(chosenBattle, curBattles, prog, selNowPlaying, out bool selDone, out bool selBoth, out bool selUnlocked);
                    bool selMasked = !selUnlocked && !_revealMapPickerSpoilers;
                    string selBadge = selDone ? (selBoth ? "★★ " : "★ ") : "";
                    string selStatusTag = "";
                    if (!selMasked)
                    {
                        if (chosenBattle.IsFinale) selStatusTag += " [FINALE]";
                        if (selNowPlaying) selStatusTag += " [NOW PLAYING]";
                        else if (_selectedBattleIdx == firstNextUpIdx) selStatusTag += " [NEXT UP]";
                    }
                    chosenVisibleName = selMasked ? "??? (Locked)" : chosenBattle.DisplayName;
                    string campTitle = _mapPickerCampaigns[_selectedCampaignIdx].CampaignName;
                    MatchData activeMd = MatchData.data;
                    int playlistCount = activeMd != null && activeMd.battles != null ? activeMd.battles.Count : 0;
                    int curIdx = activeMd != null ? activeMd.currentBattleId + 1 : 1;
                    string modeLabel = activeMd != null ? activeMd.playType.ToString() : "Single";
                    selSummary = $"Selected: [{campTitle}]  {selBadge}{chosenVisibleName}{selStatusTag}   |   Match Queue: {curIdx}/{playlistCount} ({modeLabel})";
                }

                GUI.color = new Color(0.95f, 0.92f, 0.78f, 1.0f);
                GUI.Label(new Rect(panelX + 20f, summaryY, panelW - 40f, 20f), selSummary);

                // Playlist Preview Strip
                float playlistY = panelY + 467f;
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
                float sliderY = panelY + 494f;
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
                float previewY = panelY + 523f;
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
                GUI.color = new Color(0.76f, 0.88f, 0.96f, 0.95f);
                GUI.Label(new Rect(panelX + 20f, previewY, panelW - 40f, 20f), unitPreviewText);

                int campFromHereCount = (curBattles != null && _selectedBattleIdx >= 0 && _selectedBattleIdx < curBattles.Count)
                    ? (curBattles.Count - _selectedBattleIdx)
                    : 0;
                bool hasCampFromHere = hasChosen && campFromHereCount > 0;
                bool hasPlaylist = _mapPickerPlaylist.Count > 0;

                // Bottom Row 1: Start / Replace Active Playlist Now
                float row1Y = panelY + 549f;
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
                float row2Y = panelY + 584f;
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
