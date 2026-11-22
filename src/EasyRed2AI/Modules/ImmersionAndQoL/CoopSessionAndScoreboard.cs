using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace EasyRed2HumanAI
{
    public static partial class VeteranAndTacticsPatches
    {
        // =====================================================================
        // 1. Extended Multiplayer Room Name Limit (8 -> 64 Characters)
        // =====================================================================
        public static void ApplyExtendedRoomNameLimits()
        {
            if (!VisionConfig.EnableMod.Value || !VisionConfig.EnableExtendedRoomNames.Value)
                return;

            try
            {
                ServerListMenu.maxServerNameLength = 64;
                ServerListMenu slm = ServerListMenu.instance;
                if (slm != null)
                {
                    if (slm.find_by_name_field != null && slm.find_by_name_field.characterLimit > 0 && slm.find_by_name_field.characterLimit < 64)
                    {
                        slm.find_by_name_field.characterLimit = 64;
                    }

                    var inputs = slm.GetComponentsInChildren<InputField>(true);
                    if (inputs != null)
                    {
                        for (int i = 0; i < inputs.Length; i++)
                        {
                            InputField inp = inputs[i];
                            if (inp != null && inp.characterLimit > 0 && inp.characterLimit <= 16)
                            {
                                inp.characterLimit = 64;
                            }
                        }
                    }

                    if (slm.campaignMenu != null)
                    {
                        var campInputs = slm.campaignMenu.GetComponentsInChildren<InputField>(true);
                        if (campInputs != null)
                        {
                            for (int i = 0; i < campInputs.Length; i++)
                            {
                                InputField inp = campInputs[i];
                                if (inp != null && inp.characterLimit > 0 && inp.characterLimit <= 16)
                                {
                                    inp.characterLimit = 64;
                                }
                            }
                        }
                    }
                }
            }
            catch { }
        }

        [HarmonyPatch(typeof(ServerListMenu), nameof(ServerListMenu.Awake))]
        public static class ServerListMenu_Awake_Patch
        {
            public static void Postfix()
            {
                ApplyExtendedRoomNameLimits();
            }
        }

        [HarmonyPatch(typeof(ServerListMenu), nameof(ServerListMenu.Start))]
        public static class ServerListMenu_Start_Patch
        {
            public static void Postfix()
            {
                ApplyExtendedRoomNameLimits();
            }
        }

        [HarmonyPatch(typeof(ServerListMenu), nameof(ServerListMenu.OnCreateMatchPressed))]
        public static class ServerListMenu_OnCreateMatchPressed_Patch
        {
            public static void Postfix()
            {
                ApplyExtendedRoomNameLimits();
            }
        }

        public static int GetOnlineMaxUnitsPerFaction()
        {
            if (!Photon.Pun.PhotonNetwork.InRoom || Photon.Pun.PhotonNetwork.OfflineMode)
                return 500;

            int onlineCap = 70;
            try
            {
                int nativeCap = ServerListMenu.MaxFactionUnitsOnCurrentSystemOnline;
                if (nativeCap > 0)
                    onlineCap = nativeCap;
            }
            catch { }

            return Mathf.Clamp(onlineCap, 1, 500);
        }

        public static bool ClampMatchBattlesToOnlineEnvelope(MatchData md)
        {
            if (!Photon.Pun.PhotonNetwork.InRoom || Photon.Pun.PhotonNetwork.OfflineMode || md == null || md.battles == null)
                return false;

            short maxPerSide = (short)GetOnlineMaxUnitsPerFaction();
            bool clamped = false;
            for (int i = 0; i < md.battles.Count; i++)
            {
                MatchBattle mb = md.battles[i];
                if (mb == null)
                    continue;

                if (mb.maxAllies > maxPerSide)
                {
                    mb.maxAllies = maxPerSide;
                    clamped = true;
                }
                if (mb.maxAxis > maxPerSide)
                {
                    mb.maxAxis = maxPerSide;
                    clamped = true;
                }
            }
            return clamped;
        }

        [HarmonyPatch(typeof(MatchData), nameof(MatchData.SetMultiplayerRoomSettings))]
        public static class MatchData_SetMultiplayerRoomSettings_Patch
        {
            public static void Prefix(MatchData __instance)
            {
                if (!VisionConfig.EnableMod.Value)
                    return;

                if (VisionConfig.EnableExtendedRoomNames.Value)
                {
                    ServerListMenu.maxServerNameLength = 64;
                }

                try
                {
                    ClampMatchBattlesToOnlineEnvelope(__instance);
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(MatchData), nameof(MatchData.UpdateNetworkMatchInfo))]
        public static class MatchData_UpdateNetworkMatchInfo_Patch
        {
            public static void Prefix(MatchData __instance)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value ||
                        !Photon.Pun.PhotonNetwork.InRoom ||
                        Photon.Pun.PhotonNetwork.OfflineMode ||
                        !Photon.Pun.PhotonNetwork.IsMasterClient)
                        return;

                    ClampMatchBattlesToOnlineEnvelope(__instance);
                }
                catch { }
            }
        }

        // ============================================================================
        // MULTIPLAYER CASUALTIES, PRISONERS & LOST VEHICLES SYNC FIX
        // Native Root Causes (GameAssembly.dll):
        //  1) BattleManager.AddBattleStat (0x18058B940) reads CountBattleStat -> Lua_Globals.get
        //     from Photon Room Custom Properties (which only update AFTER the server round-trip!)
        //     and calls Lua_Globals.set -> SetCustomProperties. Any 2+ casualties, surrenders, or
        //     vehicle losses within one RTT overwrite each other to baseline + 1!
        //  2) BattleManager.OnWin sends the EndBattle RPC on PhotonView immediately while async
        //     Room Custom Properties (and post-victory ragdoll/burning-wreck casualties) arrive
        //     later, while EndBattleGUI.EndBattle (0x180488C10) formats its 6 Text labels once!
        // Fix:
        //  - Maintain an authoritative synchronous short[2, 4] ledger on MasterClient, coalescing
        //    Photon Lua_Globals.set updates at 4 Hz (zero SetCustomProperties spam) and flushing
        //    synchronously on BattleManager.OnWin.
        //  - Refresh EndBattleGUI's 6 casualty/prisoner/vehicle labels at 4 Hz on both Host and
        //    Clients whenever any synced stat changes while the end-of-battle table is open!
        // ============================================================================
        // Native BattleManager.AddBattleStat (0x18058B9C6) & CountBattleStat (0x18058C58E) box
        // (int)side (0=Invaders, 1=Defenders) and (int)stat (1=Deceased, 2=Prisoners, 3=LostVehicles)
        // with Int32_TypeInfo into "BS.{0}.{1}" -> "BS.0.1" .. "BS.1.3".
        private static readonly string[,] _battleStatIds = new string[2, 4]
        {
            { null, "BS.0.1", "BS.0.2", "BS.0.3" },
            { null, "BS.1.1", "BS.1.2", "BS.1.3" }
        };

        private static readonly short[,] _authBattleStats = new short[2, 4];
        private static readonly short[] _lastDisplayedEndStats = new short[6] { -1, -1, -1, -1, -1, -1 };
        private static IntPtr _seededBattleManagerPtr;
        private static bool _wasMasterClientWhenSeeded;
        private static int _dirtyBattleStatsMask;
        private static float _nextBattleStatFlushUnscaledTime;
        private static float _nextEndBattleUiRefreshUnscaledTime;

        public static void ResetMultiplayerBattleStatLedger()
        {
            Array.Clear(_authBattleStats, 0, _authBattleStats.Length);
            for (int i = 0; i < _lastDisplayedEndStats.Length; i++)
                _lastDisplayedEndStats[i] = -1;
            _seededBattleManagerPtr = IntPtr.Zero;
            _wasMasterClientWhenSeeded = false;
            _dirtyBattleStatsMask = 0;
            _nextBattleStatFlushUnscaledTime = 0f;
            _nextEndBattleUiRefreshUnscaledTime = 0f;
            ResetLocalPlayerScoreboardLedger();
        }

        private static bool IsOnlineMasterForStatSync()
        {
            return VisionConfig.EnableMod.Value &&
                   VisionConfig.EnableMultiplayerBattleStatSyncFix.Value &&
                   Photon.Pun.PhotonNetwork.InRoom &&
                   !Photon.Pun.PhotonNetwork.OfflineMode &&
                   Photon.Pun.PhotonNetwork.IsMasterClient;
        }

        private static bool TryMapBattleStatIndices(
            BattleManager.BattleSide side,
            BattleManager.BattleStatistic stat,
            out int sideIdx,
            out int statIdx,
            out string statId)
        {
            sideIdx = -1;
            statIdx = -1;
            statId = null;

            if (side == BattleManager.BattleSide.Invaders)
                sideIdx = 0;
            else if (side == BattleManager.BattleSide.Defenders)
                sideIdx = 1;
            else
                return false;

            if (stat == BattleManager.BattleStatistic.Deceased)
                statIdx = 1;
            else if (stat == BattleManager.BattleStatistic.Prisoners)
                statIdx = 2;
            else if (stat == BattleManager.BattleStatistic.LostVehicles)
                statIdx = 3;
            else
                return false;

            statId = _battleStatIds[sideIdx, statIdx];
            return statId != null;
        }

        private static void EnsureMasterBattleStatLedgerSeeded()
        {
            BattleManager bm = BattleManager.instance;
            IntPtr bmPtr = bm != null ? bm.Pointer : IntPtr.Zero;
            if (bmPtr != IntPtr.Zero && _seededBattleManagerPtr == bmPtr && _wasMasterClientWhenSeeded)
                return;

            for (int s = 0; s <= 1; s++)
            {
                for (int st = 1; st <= 3; st++)
                {
                    short val = 0;
                    string key = _battleStatIds[s, st];
                    try
                    {
                        if (!string.IsNullOrEmpty(key) && Lua_Globals.has(key))
                        {
                            var dv = Lua_Globals.get(key);
                            if (dv != null)
                            {
                                int n = (int)Math.Round(dv.Number);
                                if (n > 0)
                                    val = (short)Mathf.Clamp(n, 0, short.MaxValue);
                            }
                        }
                    }
                    catch { }
                    _authBattleStats[s, st] = val;
                }
            }

            _seededBattleManagerPtr = bmPtr;
            _wasMasterClientWhenSeeded = true;
            _dirtyBattleStatsMask = 0;
        }

        private static void FlushDirtyMasterBattleStats(bool sendImmediate)
        {
            if (!IsOnlineMasterForStatSync())
                return;

            EnsureMasterBattleStatLedgerSeeded();
            if (_dirtyBattleStatsMask == 0)
                return;

            int mask = _dirtyBattleStatsMask;
            _dirtyBattleStatsMask = 0;
            ScoreboardCoalescedFlushesCount++;
            HitchProfiler.NotifyNetPropFlush();

            for (int s = 0; s <= 1; s++)
            {
                for (int st = 1; st <= 3; st++)
                {
                    int bit = 1 << (s * 4 + st);
                    if ((mask & bit) == 0)
                        continue;

                    string key = _battleStatIds[s, st];
                    short val = _authBattleStats[s, st];
                    try
                    {
                        Lua_Globals.set(MoonSharp.Interpreter.DynValue.NewNumber(val), key);
                    }
                    catch { }
                }
            }

            _nextBattleStatFlushUnscaledTime = Time.unscaledTime + 2.0f;
            if (sendImmediate)
            {
                try { Photon.Pun.PhotonNetwork.SendAllOutgoingCommands(); } catch { }
            }
        }

        private static void RefreshEndBattleGuiLabels(EndBattleGUI gui, bool forceUpdate)
        {
            if (gui == null || gui.Pointer == IntPtr.Zero)
                return;

            short atkCas = BattleManager.CountBattleStat(BattleManager.BattleSide.Invaders, BattleManager.BattleStatistic.Deceased, out _);
            short atkPris = BattleManager.CountBattleStat(BattleManager.BattleSide.Invaders, BattleManager.BattleStatistic.Prisoners, out _);
            short atkVeh = BattleManager.CountBattleStat(BattleManager.BattleSide.Invaders, BattleManager.BattleStatistic.LostVehicles, out _);
            short defCas = BattleManager.CountBattleStat(BattleManager.BattleSide.Defenders, BattleManager.BattleStatistic.Deceased, out _);
            short defPris = BattleManager.CountBattleStat(BattleManager.BattleSide.Defenders, BattleManager.BattleStatistic.Prisoners, out _);
            short defVeh = BattleManager.CountBattleStat(BattleManager.BattleSide.Defenders, BattleManager.BattleStatistic.LostVehicles, out _);

            if (forceUpdate || _lastDisplayedEndStats[0] != atkCas)
            {
                _lastDisplayedEndStats[0] = atkCas;
                if (gui.attackers_casualties != null)
                    gui.attackers_casualties.text = atkCas.ToString();
            }
            if (forceUpdate || _lastDisplayedEndStats[1] != atkPris)
            {
                _lastDisplayedEndStats[1] = atkPris;
                if (gui.attackers_prisoners != null)
                    gui.attackers_prisoners.text = atkPris.ToString();
            }
            if (forceUpdate || _lastDisplayedEndStats[2] != atkVeh)
            {
                _lastDisplayedEndStats[2] = atkVeh;
                if (gui.attackers_vehiclesLost != null)
                    gui.attackers_vehiclesLost.text = atkVeh.ToString();
            }
            if (forceUpdate || _lastDisplayedEndStats[3] != defCas)
            {
                _lastDisplayedEndStats[3] = defCas;
                if (gui.defenders_casualties != null)
                    gui.defenders_casualties.text = defCas.ToString();
            }
            if (forceUpdate || _lastDisplayedEndStats[4] != defPris)
            {
                _lastDisplayedEndStats[4] = defPris;
                if (gui.defenders_prisoners != null)
                    gui.defenders_prisoners.text = defPris.ToString();
            }
            if (forceUpdate || _lastDisplayedEndStats[5] != defVeh)
            {
                _lastDisplayedEndStats[5] = defVeh;
                if (gui.defenders_vehiclesLost != null)
                    gui.defenders_vehiclesLost.text = defVeh.ToString();
            }
        }

        public static void UpdateMultiplayerBattleStatSync()
        {
            if (!VisionConfig.EnableMod.Value || !VisionConfig.EnableMultiplayerBattleStatSyncFix.Value)
                return;

            if (!Photon.Pun.PhotonNetwork.InRoom || Photon.Pun.PhotonNetwork.OfflineMode)
            {
                if (_wasMasterClientWhenSeeded)
                {
                    _wasMasterClientWhenSeeded = false;
                    _dirtyBattleStatsMask = 0;
                }
                if (_localScoreboardSeeded || _localScoreboardDirty)
                {
                    ResetLocalPlayerScoreboardLedger();
                }
                return;
            }

            if (!Photon.Pun.PhotonNetwork.IsMasterClient)
            {
                _wasMasterClientWhenSeeded = false;
                _dirtyBattleStatsMask = 0;
            }
            else
            {
                EnsureMasterBattleStatLedgerSeeded();
                float now = Time.unscaledTime;
                if (_dirtyBattleStatsMask != 0 &&
                    (now >= _nextBattleStatFlushUnscaledTime || _nextBattleStatFlushUnscaledTime - now > 5.0f))
                {
                    FlushDirtyMasterBattleStats(sendImmediate: false);
                }
            }

            EnsureLocalPlayerScoreboardSeeded();
            float nowUnscaled = Time.unscaledTime;
            if (nowUnscaled >= _nextPtfoRoomAndObjPollTime || _nextPtfoRoomAndObjPollTime - nowUnscaled > 5.0f)
            {
                _nextPtfoRoomAndObjPollTime = nowUnscaled + 1.0f;
                Soldier curLocal = Soldier.CurrentControlledSoldierOrNull();
                if (curLocal != null && curLocal.Pointer != IntPtr.Zero)
                {
                    string fac = AiCore.GetCachedFaction(curLocal);
                    if (!string.IsNullOrEmpty(fac))
                        _lastKnownLocalPlayerFaction = fac;
                }

                bool isMaster = Photon.Pun.PhotonNetwork.IsMasterClient;
                IntPtr roomPtr = Photon.Pun.PhotonNetwork.CurrentRoom != null ? Photon.Pun.PhotonNetwork.CurrentRoom.Pointer : IntPtr.Zero;
                bool desiredPtfo = VisionConfig.EnableMod.Value &&
                                   VisionConfig.EnableMultiplayerBattleStatSyncFix.Value &&
                                   VisionConfig.EnableObjectiveTeamplayScoring.Value;
                if (!isMaster || !_wasMasterForPtfoSync || roomPtr != _lastSyncedPtfoRoomPtr || desiredPtfo != _lastPublishedPtfoValue)
                {
                    SyncPtfoRoomPropertyIfMaster();
                }

                if (IsOnlinePtfoScoringActive())
                {
                    PollLocalPlayerPtfoObjectives(Time.time);
                }
            }

            if (_localScoreboardDirty &&
                (nowUnscaled >= _nextLocalScoreboardFlushUnscaledTime || _nextLocalScoreboardFlushUnscaledTime - nowUnscaled > 5.0f))
            {
                FlushDirtyLocalPlayerScoreboardStats(sendImmediate: false);
                if (MiniMapGUI.ShowingPlayerList())
                    _pendingOpenPlayerListRefresh = true;
            }

            if (_pendingOpenPlayerListRefresh &&
                (nowUnscaled >= _nextOpenPlayerListRefreshUnscaledTime || _nextOpenPlayerListRefreshUnscaledTime - nowUnscaled > 5.0f))
            {
                _pendingOpenPlayerListRefresh = false;
                _nextOpenPlayerListRefreshUnscaledTime = nowUnscaled + 0.40f;
                if (MiniMapGUI.ShowingPlayerList())
                {
                    try { MiniMapGUI.RefreshPlayerList(); } catch { }
                }
            }

            EndBattleGUI endGui = BattleManager.endBattleGUI;
            if (endGui != null && endGui.animationStarted)
            {
                float now = Time.unscaledTime;
                if (now >= _nextEndBattleUiRefreshUnscaledTime || _nextEndBattleUiRefreshUnscaledTime - now > 5.0f)
                {
                    _nextEndBattleUiRefreshUnscaledTime = now + 0.25f;
                    RefreshEndBattleGuiLabels(endGui, forceUpdate: false);
                }
            }
        }

        [HarmonyPatch(typeof(BattleManager), nameof(BattleManager.AddBattleStat))]
        public static class BattleManager_AddBattleStat_Patch
        {
            public static bool Prefix(
                BattleManager.BattleSide __0,
                BattleManager.BattleStatistic __1,
                short __2)
            {
                try
                {
                    if (!IsOnlineMasterForStatSync() ||
                        !TryMapBattleStatIndices(__0, __1, out int sIdx, out int stIdx, out _))
                    {
                        return true;
                    }

                    if (__2 == 0)
                        return false;

                    EnsureMasterBattleStatLedgerSeeded();
                    int updated = Mathf.Clamp(_authBattleStats[sIdx, stIdx] + __2, 0, short.MaxValue);
                    _authBattleStats[sIdx, stIdx] = (short)updated;
                    _dirtyBattleStatsMask |= (1 << (sIdx * 4 + stIdx));
                    ScoreboardVanillaPacketsSuppressedCount++;
                    return false;
                }
                catch { }
                return true;
            }
        }

        [HarmonyPatch(typeof(BattleManager), nameof(BattleManager.CountBattleStat))]
        public static class BattleManager_CountBattleStat_Patch
        {
            public static bool Prefix(
                BattleManager.BattleSide __0,
                BattleManager.BattleStatistic __1,
                ref string __2,
                ref short __result)
            {
                try
                {
                    if (!IsOnlineMasterForStatSync() ||
                        !TryMapBattleStatIndices(__0, __1, out int sIdx, out int stIdx, out string statId))
                    {
                        return true;
                    }

                    EnsureMasterBattleStatLedgerSeeded();
                    __2 = statId;
                    __result = _authBattleStats[sIdx, stIdx];
                    return false;
                }
                catch { }
                return true;
            }
        }

        [HarmonyPatch(typeof(BattleManager), nameof(BattleManager.OnWin))]
        public static class BattleManager_OnWin_Patch
        {
            public static void Prefix(string __0)
            {
                try
                {
                    // Pre-surrender surviving losing-side AI soldiers BEFORE native BattleManager.OnWin sends its
                    // SyncWin RPC so final prisoner counts are captured in our pre-OnWin Lua_Globals.set flush and
                    // arrive on remote clients in the exact same Photon outgoing batch as SyncWin!
                    if (IsOnlineMasterForStatSync() && !string.IsNullOrEmpty(__0))
                    {
                        var aliveList = Creature.aliveCreatures;
                        if (aliveList != null)
                        {
                            for (int i = aliveList.Count - 1; i >= 0; i--)
                            {
                                Creature c = aliveList[i];
                                if (c == null || c.Pointer == IntPtr.Zero || !c.IsAlive || c.IsDead)
                                    continue;

                                Soldier s = c.TryCast<Soldier>();
                                if (s == null ||
                                    s.Pointer == IntPtr.Zero ||
                                    s.IsPlayer() ||
                                    s.HasSurrended() ||
                                    AiCore.AreSameSideFactions(s.faction, __0))
                                {
                                    continue;
                                }

                                try { s.SurrenderSynched(); } catch { }
                            }
                        }
                    }

                    FlushDirtyMasterBattleStats(sendImmediate: true);
                    FlushDirtyLocalPlayerScoreboardStats(sendImmediate: true);
                }
                catch { }
            }

            public static void Postfix()
            {
                try
                {
                    FlushDirtyMasterBattleStats(sendImmediate: true);
                    FlushDirtyLocalPlayerScoreboardStats(sendImmediate: true);
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(EndBattleGUI), nameof(EndBattleGUI.EndBattle))]
        public static class EndBattleGUI_EndBattle_Patch
        {
            public static void Postfix(EndBattleGUI __instance)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value)
                        return;

                    AiCore.EmitMatchEndHealthSummary();

                    if (!VisionConfig.EnableMultiplayerBattleStatSyncFix.Value || __instance == null)
                        return;

                    FlushDirtyMasterBattleStats(sendImmediate: true);
                    FlushDirtyLocalPlayerScoreboardStats(sendImmediate: true);
                    RefreshEndBattleGuiLabels(__instance, forceUpdate: true);
                }
                catch (Exception ex)
                {
                    AiCore.ReportPatchException("EndBattle", ex);
                }
            }
        }

        // ============================================================================
        // MULTIPLAYER SCOREBOARD KILLS, DEATHS, SCORE & SCORE-DESCENDING SORT FIX
        // Native Root Causes (GameAssembly.dll):
        //  1) DamageFeedback.OnSoldierHit (0x1808866B5) skips CustomProperties["kills"]
        //     whenever !target.GetSyncher().isPlayer, and skips BOTH "kills" and "score"
        //     at 0x180886665 whenever !target.IsAlive is false (every bleed-out / burning
        //     finish, vehicle explosion crew kill, and roadkill awards 0 kills & 0 score!).
        //  2) Soldier.Kill (0x180751974) gates CustomProperties["deaths"] behind
        //     gameObject.activeInHierarchy — skipping every death inside an enclosed
        //     tank/armored car/plane (!seat.showUnitSet), as well as abandoning a
        //     mortally downed (isBleeding) soldier via squad-switch or Respawn.
        //  3) ResourcesManager.SetPlayerCustomProperty read-modifies Photon LocalPlayer
        //     CustomProperties using async RTT values and triggers back-to-back RPC/prop
        //     writes. Native MiniMapGUI.ShowPlayerList (0x1804A49A8) iterates raw
        //     PhotonNetwork.PlayerList without sorting.
        // Fix:
        //  - Authoritative synchronous short ledger for LocalPlayer ("kills", "deaths",
        //    "score" boxed as System.Int16), coalesced at 4 Hz (immediate on Tab/Win).
        //  - Suppresses vanilla's stale 1-key "kills"/"deaths"/"score" property packets.
        //  - Sorts each MiniMapGUI player list container by Score DESC (then Kills DESC,
        //    Deaths ASC, ActorNumber ASC) while preserving native slot Y spacing.
        // ============================================================================
        private struct LocalPlayerVictimTag
        {
            public float Timestamp;
            public bool IsEnemy;
            public bool IsHumanPlayer;
            public bool WasMortalOrFireOrRoadkill;
            public bool WasObjectiveZone;
        }

        private struct PtfoObjectiveState
        {
            public MissionObjective ObjRef;
            public IntPtr PhasePtr;
            public bool Initialized;
            public bool WasHeldByMySide;
            public bool WasSecured;
            public bool CanAttractMySide;
            public float LastProgress01;
            public float LastRawComplete;
            public float LastCaptureAwardTime;
            public bool LocalWasInsideLastPoll;
        }

        private struct PlayerScoreboardRowSortEntry
        {
            public RectTransform Rect;
            public short Score;
            public short Kills;
            public short Deaths;
            public int ActorNumber;
        }

        public static int ScoreboardCoalescedFlushesCount { get; private set; }
        public static int ScoreboardVanillaPacketsSuppressedCount { get; private set; }
        public static int PtfoTotalPointsEarned { get; private set; }
        public static int PtfoObjKillBonusesCount { get; private set; }
        public static int PtfoActiveTicksCount { get; private set; }
        public static int PtfoCapturesCount { get; private set; }
        public static int PtfoRevivesCount { get; private set; }
        public static int PtfoVehicleDisablesCount { get; private set; }

        private static short _authLocalKills;
        private static short _authLocalDeaths;
        private static short _authLocalScore;
        private static bool _localScoreboardSeeded;
        private static bool _localScoreboardDirty;
        private static bool _isInternalScoreboardFlush;
        private static bool _inBattleLoaderOnJoinedRoom;
        private static bool _pendingOpenPlayerListRefresh;
        private static IntPtr _seededLocalScoreboardRoomPtr;
        private static float _nextLocalScoreboardFlushUnscaledTime;
        private static float _nextOpenPlayerListRefreshUnscaledTime;

        private static IntPtr _lastSyncedPtfoRoomPtr;
        private static bool _wasMasterForPtfoSync;
        private static bool _lastPublishedPtfoValue;
        private static bool _remoteHostPtfoKnown;
        private static bool _remoteHostPtfoEnabled;
        private static float _nextPtfoRoomAndObjPollTime;
        private static float _ptfoActiveObjectiveAccumSec;
        private static float _lastPtfoPollTime;
        private static IntPtr _lastPtfoPhasePtr;
        private static string _lastKnownLocalPlayerFaction;
        private static IntPtr _pendingLocalFriendlySyringePatientPtr;
        private static float _pendingLocalFriendlySyringeExpireTime;

        private static Il2CppSystem.Object _propKeyKillsObj;
        private static Il2CppSystem.Object _propKeyDeathsObj;
        private static Il2CppSystem.Object _propKeyScoreObj;
        private static Il2CppSystem.Object _propKeyRoomPtfoObj;
        private static Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<short> _shortBoxBuffer;
        private static Il2CppSystem.Array _shortBoxArrayView;

        private static readonly Dictionary<IntPtr, LocalPlayerVictimTag> _localPlayerDamagedVictims = new Dictionary<IntPtr, LocalPlayerVictimTag>(64);
        private static readonly Dictionary<IntPtr, float> _localPlayerDamagedVehicles = new Dictionary<IntPtr, float>(16);
        private static readonly HashSet<IntPtr> _creditedKillVictimPtrs = new HashSet<IntPtr>();
        private static readonly HashSet<IntPtr> _creditedLocalDeathSoldierPtrs = new HashSet<IntPtr>();
        private static readonly HashSet<IntPtr> _abandonedBleedingLocalSoldiers = new HashSet<IntPtr>();
        private static readonly List<IntPtr> _reusableScoreboardPruneKeys = new List<IntPtr>(32);

        private static readonly List<MissionObjective> _ptfoScratchObjectives = new List<MissionObjective>(16);
        private static readonly Dictionary<IntPtr, PtfoObjectiveState> _ptfoObjectiveStates = new Dictionary<IntPtr, PtfoObjectiveState>(16);
        private static readonly HashSet<IntPtr> _creditedPtfoDisabledVehiclePtrs = new HashSet<IntPtr>();
        private static readonly Dictionary<IntPtr, float> _recentPtfoRevivedPatients = new Dictionary<IntPtr, float>(16);

        private static readonly List<Transform> _reusableRowParents = new List<Transform>(4);
        private static readonly List<List<PlayerScoreboardRowSortEntry>> _reusableParentGroups = new List<List<PlayerScoreboardRowSortEntry>>(4);
        private static readonly List<Vector2> _reusableSlotPositions = new List<Vector2>(32);

        private static void EnsureScoreboardPropertyKeys()
        {
            if (_propKeyKillsObj != null)
                return;

            _propKeyKillsObj = ((Il2CppSystem.String)"kills").Cast<Il2CppSystem.Object>();
            _propKeyDeathsObj = ((Il2CppSystem.String)"deaths").Cast<Il2CppSystem.Object>();
            _propKeyScoreObj = ((Il2CppSystem.String)"score").Cast<Il2CppSystem.Object>();
            _propKeyRoomPtfoObj = ((Il2CppSystem.String)"bh_ptfo").Cast<Il2CppSystem.Object>();
        }

        private static Il2CppSystem.Object BoxIl2CppInt16(short val)
        {
            if (_shortBoxBuffer == null)
            {
                _shortBoxBuffer = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<short>(1);
                _shortBoxArrayView = _shortBoxBuffer.Cast<Il2CppSystem.Array>();
            }
            _shortBoxBuffer[0] = val;
            return _shortBoxArrayView.GetValue(0);
        }

        private static bool TryReadPlayerShortProp(ExitGames.Client.Photon.Hashtable props, Il2CppSystem.Object key, out short value)
        {
            value = 0;
            if (props == null || key == null || !props.ContainsKey(key))
                return false;

            try
            {
                Il2CppSystem.Object raw = props[key];
                if (raw == null)
                    return false;

                try
                {
                    value = raw.Unbox<short>();
                    return true;
                }
                catch
                {
                    if (short.TryParse(raw.ToString(), out value))
                        return true;
                }
            }
            catch { }

            return false;
        }

        private static bool IsOnlineScoreboardFixActive()
        {
            return VisionConfig.EnableMod.Value &&
                   VisionConfig.EnableMultiplayerBattleStatSyncFix.Value &&
                   Photon.Pun.PhotonNetwork.InRoom &&
                   !Photon.Pun.PhotonNetwork.OfflineMode;
        }

        public static void SyncPtfoRoomPropertyIfMaster()
        {
            try
            {
                if (!Photon.Pun.PhotonNetwork.InRoom ||
                    Photon.Pun.PhotonNetwork.OfflineMode ||
                    Photon.Pun.PhotonNetwork.CurrentRoom == null)
                {
                    return;
                }

                EnsureScoreboardPropertyKeys();
                Photon.Realtime.Room room = Photon.Pun.PhotonNetwork.CurrentRoom;
                if (Photon.Pun.PhotonNetwork.IsMasterClient)
                {
                    bool desired = VisionConfig.EnableMod.Value &&
                                   VisionConfig.EnableMultiplayerBattleStatSyncFix.Value &&
                                   VisionConfig.EnableObjectiveTeamplayScoring.Value;
                    var roomProps = new ExitGames.Client.Photon.Hashtable();
                    roomProps[_propKeyRoomPtfoObj] = BoxIl2CppInt16((short)(desired ? 1 : 0));
                    room.SetCustomProperties(roomProps);
                    HitchProfiler.NotifyNetPropFlush();

                    _lastSyncedPtfoRoomPtr = room.Pointer;
                    _wasMasterForPtfoSync = true;
                    _lastPublishedPtfoValue = desired;
                }
                else
                {
                    _wasMasterForPtfoSync = false;
                    bool found = TryReadPlayerShortProp(room.CustomProperties, _propKeyRoomPtfoObj, out short hostVal);
                    _remoteHostPtfoKnown = found;
                    _remoteHostPtfoEnabled = found && hostVal != 0;
                }
            }
            catch { }
        }

        public static bool IsOnlinePtfoScoringActive()
        {
            if (!IsOnlineScoreboardFixActive())
                return false;

            if (Photon.Pun.PhotonNetwork.IsMasterClient)
                return VisionConfig.EnableObjectiveTeamplayScoring.Value;

            return _remoteHostPtfoEnabled;
        }

        public static bool IsPtfoControlledByRemoteHost(out bool hostEnabled, out bool hostKnown)
        {
            hostEnabled = _remoteHostPtfoEnabled;
            hostKnown = _remoteHostPtfoKnown;
            return Photon.Pun.PhotonNetwork.InRoom &&
                   !Photon.Pun.PhotonNetwork.OfflineMode &&
                   !Photon.Pun.PhotonNetwork.IsMasterClient;
        }

        public static bool TryGetLiveScoreboardSnapshot(
            out short score,
            out short kills,
            out short deaths,
            out bool ptfoActive)
        {
            score = _authLocalScore;
            kills = _authLocalKills;
            deaths = _authLocalDeaths;
            ptfoActive = IsOnlinePtfoScoringActive();
            return _localScoreboardSeeded && IsOnlineScoreboardFixActive();
        }

        private static void ResetLocalPlayerScoreboardLedger()
        {
            _authLocalKills = 0;
            _authLocalDeaths = 0;
            _authLocalScore = 0;
            _localScoreboardSeeded = false;
            _localScoreboardDirty = false;
            _isInternalScoreboardFlush = false;
            _inBattleLoaderOnJoinedRoom = false;
            _pendingOpenPlayerListRefresh = false;
            _seededLocalScoreboardRoomPtr = IntPtr.Zero;
            _nextLocalScoreboardFlushUnscaledTime = 0f;
            _nextOpenPlayerListRefreshUnscaledTime = 0f;
            _activeDamageAttackerPtr = IntPtr.Zero;

            _lastSyncedPtfoRoomPtr = IntPtr.Zero;
            _wasMasterForPtfoSync = false;
            _lastPublishedPtfoValue = false;
            _remoteHostPtfoKnown = false;
            _remoteHostPtfoEnabled = false;
            _nextPtfoRoomAndObjPollTime = 0f;
            _ptfoActiveObjectiveAccumSec = 0f;
            _lastPtfoPollTime = 0f;
            _lastPtfoPhasePtr = IntPtr.Zero;
            _pendingLocalFriendlySyringePatientPtr = IntPtr.Zero;
            _pendingLocalFriendlySyringeExpireTime = 0f;

            _localPlayerDamagedVictims.Clear();
            _localPlayerDamagedVehicles.Clear();
            _creditedKillVictimPtrs.Clear();
            _creditedLocalDeathSoldierPtrs.Clear();
            _abandonedBleedingLocalSoldiers.Clear();
            _ptfoObjectiveStates.Clear();
            _creditedPtfoDisabledVehiclePtrs.Clear();
            _recentPtfoRevivedPatients.Clear();
        }

        private static void EnsureLocalPlayerScoreboardSeeded()
        {
            if (!IsOnlineScoreboardFixActive())
                return;

            Photon.Realtime.Room room = Photon.Pun.PhotonNetwork.CurrentRoom;
            Photon.Realtime.Player local = Photon.Pun.PhotonNetwork.LocalPlayer;
            IntPtr roomPtr = room != null ? room.Pointer : IntPtr.Zero;
            if (roomPtr == IntPtr.Zero || local == null)
                return;

            if (_localScoreboardSeeded && _seededLocalScoreboardRoomPtr == roomPtr)
                return;

            EnsureScoreboardPropertyKeys();
            ExitGames.Client.Photon.Hashtable props = local.CustomProperties;
            _authLocalKills = TryReadPlayerShortProp(props, _propKeyKillsObj, out short k) ? k : (short)0;
            _authLocalDeaths = TryReadPlayerShortProp(props, _propKeyDeathsObj, out short d) ? d : (short)0;
            _authLocalScore = TryReadPlayerShortProp(props, _propKeyScoreObj, out short sc) ? sc : (short)0;

            _seededLocalScoreboardRoomPtr = roomPtr;
            _localScoreboardSeeded = true;
            _localScoreboardDirty = false;
        }

        private static void SyncAndFlushLocalScoreboardBeforeListRender()
        {
            if (!IsOnlineScoreboardFixActive())
                return;

            EnsureLocalPlayerScoreboardSeeded();
            Photon.Realtime.Player local = Photon.Pun.PhotonNetwork.LocalPlayer;
            if (local == null)
                return;

            EnsureScoreboardPropertyKeys();
            ExitGames.Client.Photon.Hashtable props = local.CustomProperties;
            if (!TryReadPlayerShortProp(props, _propKeyKillsObj, out short curK) || curK != _authLocalKills ||
                !TryReadPlayerShortProp(props, _propKeyDeathsObj, out short curD) || curD != _authLocalDeaths ||
                !TryReadPlayerShortProp(props, _propKeyScoreObj, out short curS) || curS != _authLocalScore)
            {
                _localScoreboardDirty = true;
            }

            FlushDirtyLocalPlayerScoreboardStats(sendImmediate: false);
        }

        private static void FlushDirtyLocalPlayerScoreboardStats(bool sendImmediate)
        {
            if (!IsOnlineScoreboardFixActive())
                return;

            EnsureLocalPlayerScoreboardSeeded();
            if (!_localScoreboardDirty)
                return;

            Photon.Realtime.Player local = Photon.Pun.PhotonNetwork.LocalPlayer;
            if (local == null)
                return;

            EnsureScoreboardPropertyKeys();
            _localScoreboardDirty = false;
            ScoreboardCoalescedFlushesCount++;
            HitchProfiler.NotifyNetPropFlush();

            try
            {
                var props = new ExitGames.Client.Photon.Hashtable();
                props[_propKeyKillsObj] = BoxIl2CppInt16(_authLocalKills);
                props[_propKeyDeathsObj] = BoxIl2CppInt16(_authLocalDeaths);
                props[_propKeyScoreObj] = BoxIl2CppInt16(_authLocalScore);

                _isInternalScoreboardFlush = true;
                try
                {
                    local.SetCustomProperties(props);
                }
                finally
                {
                    _isInternalScoreboardFlush = false;
                }
            }
            catch { }

            _nextLocalScoreboardFlushUnscaledTime = Time.unscaledTime + 0.25f;
            if (sendImmediate)
            {
                try { Photon.Pun.PhotonNetwork.SendAllOutgoingCommands(); } catch { }
            }
        }

        private static bool IsHumanControlledSoldier(Soldier s)
        {
            if (s == null || s.Pointer == IntPtr.Zero)
                return false;

            if (IsPlayerCharacter(s))
                return true;

            SyncSoldier syncher = s.soldierSyncher;
            return syncher != null && syncher.controlled_by_player;
        }

        private static void PruneExpiredVictimAndVehicleTags(float now)
        {
            if (_localPlayerDamagedVictims.Count >= 48)
            {
                _reusableScoreboardPruneKeys.Clear();
                foreach (var kv in _localPlayerDamagedVictims)
                {
                    if ((now - kv.Value.Timestamp) > 90.0f || now < kv.Value.Timestamp)
                        _reusableScoreboardPruneKeys.Add(kv.Key);
                }
                for (int i = 0; i < _reusableScoreboardPruneKeys.Count; i++)
                    _localPlayerDamagedVictims.Remove(_reusableScoreboardPruneKeys[i]);
            }

            if (_localPlayerDamagedVehicles.Count >= 24)
            {
                _reusableScoreboardPruneKeys.Clear();
                foreach (var kv in _localPlayerDamagedVehicles)
                {
                    if ((now - kv.Value) > 30.0f || now < kv.Value)
                        _reusableScoreboardPruneKeys.Add(kv.Key);
                }
                for (int i = 0; i < _reusableScoreboardPruneKeys.Count; i++)
                    _localPlayerDamagedVehicles.Remove(_reusableScoreboardPruneKeys[i]);
            }
        }

        private static void RecordLocalPlayerDamagedVehicle(IntPtr vehiclePtr)
        {
            if (vehiclePtr == IntPtr.Zero || !IsOnlineScoreboardFixActive())
                return;

            float now = Time.time;
            PruneExpiredVictimAndVehicleTags(now);
            _localPlayerDamagedVehicles[vehiclePtr] = now;
        }

        private static void AwardLocalPtfoScore(short points, string reason)
        {
            if (points <= 0 || !IsOnlinePtfoScoringActive())
                return;

            EnsureLocalPlayerScoreboardSeeded();
            _authLocalScore = (short)Mathf.Clamp(_authLocalScore + points, short.MinValue, short.MaxValue);
            _localScoreboardDirty = true;
            PtfoTotalPointsEarned += points;
            AiCore.LogDebug("PTFO", $"+{points} Score ({reason}) -> TotalScore:{_authLocalScore}");
        }

        private static bool TryGetSoldierWorldPosition(Soldier s, out Vector3 pos)
        {
            pos = Vector3.zero;
            if (s == null || s.Pointer == IntPtr.Zero)
                return false;

            if (s.IsOnVehicle())
            {
                Vehicle v = s.GetCurrentVehicle();
                if (v != null && v.Pointer != IntPtr.Zero && v.transform != null)
                {
                    pos = v.transform.position;
                    return true;
                }
            }

            if (s.transform != null)
            {
                pos = s.transform.position;
                return true;
            }
            return false;
        }

        private static bool IsEitherSoldierInActiveObjective(Soldier localAttacker, Soldier victim)
        {
            if (localAttacker == null || victim == null || localAttacker.Pointer == IntPtr.Zero || victim.Pointer == IntPtr.Zero)
                return false;

            BattleManager bm = BattleManager.instance;
            if (bm == null || bm.Pointer == IntPtr.Zero || BattleManager.isBattleEnded || bm.isPreview)
                return false;

            string fac = AiCore.GetCachedFaction(localAttacker);
            if (string.IsNullOrEmpty(fac))
                return false;

            if (!TryGetSoldierWorldPosition(localAttacker, out Vector3 posA) ||
                !TryGetSoldierWorldPosition(victim, out Vector3 posB))
            {
                return false;
            }

            BattlePhase phase = AiCore.TryGetCurrentBattlePhase(bm);
            AiCore.CollectActiveMissionObjectives(phase, _ptfoScratchObjectives);
            if (_ptfoScratchObjectives.Count == 0)
                return false;

            BattleData bd = null;
            try { bd = BattleManager.GetCurrentBattleData(); } catch { }

            for (int i = 0; i < _ptfoScratchObjectives.Count; i++)
            {
                MissionObjective obj = _ptfoScratchObjectives[i];
                if (obj == null || obj.Pointer == IntPtr.Zero)
                    continue;

                if (!AiCore.ClassifyMissionObjectiveForFaction(obj, fac, bd, out bool isHeld, out bool canAttract, out _))
                    continue;

                if (!isHeld && !canAttract)
                    continue;

                if (obj.IsInsideObjectiveArea(posA, 1.15f) || obj.IsInsideObjectiveArea(posB, 1.15f))
                    return true;
            }
            return false;
        }

        private static bool HasLiveEnemyInObjective(MissionObjective obj, string playerFac, float areaScale)
        {
            if (obj == null || obj.Pointer == IntPtr.Zero || string.IsNullOrEmpty(playerFac))
                return false;

            AiCore.EnsureWorldSnapshot();
            int count = AiCore.WorldSoldierCount;
            var soldiers = AiCore.WorldSoldiers;
            for (int i = 0; i < count; i++)
            {
                ref AiCore.SoldierSnapshot sol = ref soldiers[i];
                if (sol.Ptr == IntPtr.Zero ||
                    sol.Ref == null ||
                    sol.IsInjured ||
                    sol.HasSurrendered ||
                    string.IsNullOrEmpty(sol.Faction) ||
                    AiCore.AreSameSideFactions(playerFac, sol.Faction))
                {
                    continue;
                }

                if (sol.IsOnVehicle)
                {
                    Vehicle sv = sol.Ref.GetCurrentVehicle();
                    if (sv != null && sv.IsAirVehicle())
                        continue;
                }

                if (obj.IsInsideObjectiveArea(sol.Pos, areaScale))
                    return true;
            }
            return false;
        }

        private static void PollLocalPlayerPtfoObjectives(float now)
        {
            BattleManager bm = BattleManager.instance;
            if (bm == null || bm.Pointer == IntPtr.Zero || BattleManager.isBattleEnded || bm.isPreview)
                return;

            Soldier curLocal = Soldier.CurrentControlledSoldierOrNull();
            bool localValid = curLocal != null && curLocal.Pointer != IntPtr.Zero && curLocal.IsAlive && !curLocal.IsDead;
            bool localEligible = localValid && !curLocal.isBleeding;
            if (localEligible && curLocal.IsOnVehicle())
            {
                Vehicle v = curLocal.GetCurrentVehicle();
                if (v != null && v.IsAirVehicle())
                    localEligible = false;
            }

            Vector3 playerPos = Vector3.zero;
            if (localEligible && !TryGetSoldierWorldPosition(curLocal, out playerPos))
                localEligible = false;

            if (!localValid)
                _ptfoActiveObjectiveAccumSec = 0f;

            string playerFac = localValid ? AiCore.GetCachedFaction(curLocal) : _lastKnownLocalPlayerFaction;
            if (string.IsNullOrEmpty(playerFac))
                return;

            BattlePhase phase = AiCore.TryGetCurrentBattlePhase(bm);
            IntPtr curPhasePtr = phase != null ? phase.Pointer : IntPtr.Zero;

            // Check if a phase transition just secured an objective the local player was actively capturing
            if (_lastPtfoPhasePtr != IntPtr.Zero && curPhasePtr != _lastPtfoPhasePtr && _ptfoObjectiveStates.Count > 0)
            {
                _reusableScoreboardPruneKeys.Clear();
                foreach (var kv in _ptfoObjectiveStates)
                {
                    PtfoObjectiveState st = kv.Value;
                    if (st.PhasePtr == _lastPtfoPhasePtr)
                    {
                        _reusableScoreboardPruneKeys.Add(kv.Key);
                        if (localEligible &&
                            st.Initialized &&
                            st.LocalWasInsideLastPoll &&
                            !st.WasHeldByMySide &&
                            !st.WasSecured &&
                            st.CanAttractMySide &&
                            st.ObjRef != null &&
                            (now - st.LastCaptureAwardTime) >= 20.0f)
                        {
                            bool secNow = false;
                            try { secNow = st.ObjRef.IsSecured(); } catch { }
                            if (secNow)
                            {
                                st.LastCaptureAwardTime = now;
                                PtfoCapturesCount++;
                                AwardLocalPtfoScore(5, "Objective Captured (Phase)");
                            }
                        }
                    }
                }
                for (int i = 0; i < _reusableScoreboardPruneKeys.Count; i++)
                    _ptfoObjectiveStates.Remove(_reusableScoreboardPruneKeys[i]);
            }
            _lastPtfoPhasePtr = curPhasePtr;

            AiCore.CollectActiveMissionObjectives(phase, _ptfoScratchObjectives);
            if (_ptfoScratchObjectives.Count == 0)
                return;

            BattleData bd = null;
            try { bd = BattleManager.GetCurrentBattleData(); } catch { }

            float dt = _lastPtfoPollTime > 0f && now > _lastPtfoPollTime
                ? Mathf.Clamp(now - _lastPtfoPollTime, 0.5f, 2.0f)
                : 1.0f;
            _lastPtfoPollTime = now;

            bool activeTickThisPoll = false;
            for (int i = 0; i < _ptfoScratchObjectives.Count; i++)
            {
                MissionObjective obj = _ptfoScratchObjectives[i];
                if (obj == null || obj.Pointer == IntPtr.Zero)
                    continue;

                IntPtr objPtr = obj.Pointer;
                bool classified = AiCore.ClassifyMissionObjectiveForFaction(
                    obj,
                    playerFac,
                    bd,
                    out bool isHeld,
                    out bool canAttract,
                    out float contestOrProgress01);

                bool nowSecured = false;
                try { nowSecured = obj.IsSecured(); } catch { }

                float rawComplete = 0f;
                try
                {
                    if (obj.missionTask != null)
                        rawComplete = obj.missionTask.GetTaskCompletationPercentage();
                }
                catch { }

                _ptfoObjectiveStates.TryGetValue(objPtr, out PtfoObjectiveState st);
                st.ObjRef = obj;
                st.PhasePtr = curPhasePtr;

                bool insideCap = localEligible && obj.IsInsideObjectiveArea(playerPos, 1.08f);
                bool insideTick = localEligible && obj.IsInsideObjectiveArea(playerPos, 1.0f);

                if (st.Initialized &&
                    localEligible &&
                    !st.WasHeldByMySide &&
                    !st.WasSecured &&
                    (isHeld || (nowSecured && st.CanAttractMySide)) &&
                    (insideCap || st.LocalWasInsideLastPoll) &&
                    (now - st.LastCaptureAwardTime) >= 20.0f)
                {
                    st.LastCaptureAwardTime = now;
                    PtfoCapturesCount++;
                    AwardLocalPtfoScore(5, "Objective Captured");
                }

                if (classified && insideTick)
                {
                    bool barMoving = st.Initialized &&
                                     (Mathf.Abs(contestOrProgress01 - st.LastProgress01) > 0.0005f ||
                                      Mathf.Abs(rawComplete - st.LastRawComplete) > 0.0005f);
                    if (!isHeld && canAttract && !nowSecured)
                    {
                        if (barMoving || HasLiveEnemyInObjective(obj, playerFac, 1.20f))
                            activeTickThisPoll = true;
                    }
                    else if (isHeld)
                    {
                        if ((contestOrProgress01 > 0.01f && barMoving) ||
                            HasLiveEnemyInObjective(obj, playerFac, 1.25f))
                        {
                            activeTickThisPoll = true;
                        }
                    }
                }

                st.Initialized = true;
                st.WasHeldByMySide = isHeld;
                st.WasSecured = nowSecured;
                st.CanAttractMySide = canAttract;
                st.LastProgress01 = contestOrProgress01;
                st.LastRawComplete = rawComplete;
                st.LocalWasInsideLastPoll = insideCap;
                _ptfoObjectiveStates[objPtr] = st;
            }

            if (activeTickThisPoll)
            {
                _ptfoActiveObjectiveAccumSec += dt;
                if (_ptfoActiveObjectiveAccumSec >= 10.0f)
                {
                    _ptfoActiveObjectiveAccumSec -= 10.0f;
                    PtfoActiveTicksCount++;
                    AwardLocalPtfoScore(1, "Active Objective Contest/Cap");
                }
            }
        }

        public static void NotifyVehicleRepairedForPtfo(IntPtr vehPtr)
        {
            if (vehPtr != IntPtr.Zero)
                _creditedPtfoDisabledVehiclePtrs.Remove(vehPtr);
        }

        public static void TryCreditEnemyVehicleDisableToLocalPlayer(Vehicle veh, bool directPlayerHit)
        {
            try
            {
                if (!IsOnlinePtfoScoringActive() || veh == null || veh.Pointer == IntPtr.Zero)
                    return;

                IntPtr vehPtr = veh.Pointer;
                if (!directPlayerHit)
                {
                    if (!_localPlayerDamagedVehicles.TryGetValue(vehPtr, out float vHitTime) ||
                        (Time.time - vHitTime) > 20.0f)
                    {
                        return;
                    }
                }

                AiCore.VehicleFastHandles vh = AiCore.GetOrCreateVehicleHandles(veh);
                if (vh != null && vh.IsParachute)
                    return;

                string vehFac = veh.GetVehicleFaction();
                if (string.IsNullOrEmpty(vehFac) || vehFac == "Civilian")
                    _lastKnownVehicleFaction.TryGetValue(vehPtr, out vehFac);

                Soldier curLocal = Soldier.CurrentControlledSoldierOrNull();
                string localFac = curLocal != null && curLocal.Pointer != IntPtr.Zero
                    ? AiCore.GetCachedFaction(curLocal)
                    : null;
                if (string.IsNullOrEmpty(localFac))
                    localFac = _lastKnownLocalPlayerFaction;

                if (string.IsNullOrEmpty(vehFac) ||
                    vehFac == "Civilian" ||
                    string.IsNullOrEmpty(localFac) ||
                    AiCore.AreSameSideFactions(localFac, vehFac))
                {
                    return;
                }

                if (!_creditedPtfoDisabledVehiclePtrs.Add(vehPtr))
                    return;

                PtfoVehicleDisablesCount++;
                bool isStatic = veh.IsStatic();
                bool isAir = veh.IsAirVehicle();
                bool isArmor = !isStatic && !isAir &&
                               (veh.TryCast<VehicleTank>() != null || GetVehicleMaxHullArmorMm(veh) >= 20.0f);
                short vehScore = isStatic ? (short)2 : ((isAir || isArmor) ? (short)5 : (short)3);
                string tierLabel = isStatic
                    ? "Enemy Emplacement Disabled"
                    : (isAir ? "Enemy Aircraft Destroyed" : (isArmor ? "Enemy Armor Destroyed" : "Enemy Vehicle Disabled"));
                AwardLocalPtfoScore(vehScore, $"{tierLabel} ({veh.name})");
            }
            catch { }
        }

        private static void TagVictimDamagedByLocalPlayer(Soldier attacker, Soldier victim, bool mortalOrFireOrRoadkill)
        {
            if (!IsOnlineScoreboardFixActive() ||
                attacker == null ||
                victim == null ||
                attacker.Pointer == IntPtr.Zero ||
                victim.Pointer == IntPtr.Zero ||
                attacker.Pointer == victim.Pointer ||
                !IsPlayerCharacter(attacker))
            {
                return;
            }

            IntPtr victimPtr = victim.Pointer;
            if (_creditedKillVictimPtrs.Contains(victimPtr) || victim.HasSurrended())
                return;

            float now = Time.time;
            PruneExpiredVictimAndVehicleTags(now);

            string atkFac = AiCore.GetCachedFaction(attacker);
            if (!string.IsNullOrEmpty(atkFac))
                _lastKnownLocalPlayerFaction = atkFac;

            string vicFac = AiCore.GetCachedFaction(victim);
            bool isEnemy = string.IsNullOrEmpty(atkFac) ||
                           string.IsNullOrEmpty(vicFac) ||
                           !AiCore.AreSameSideFactions(atkFac, vicFac);
            bool isHumanVictim = IsHumanControlledSoldier(victim);

            bool hasPrev = _localPlayerDamagedVictims.TryGetValue(victimPtr, out LocalPlayerVictimTag existing) &&
                           (now - existing.Timestamp) <= 90.0f;

            bool wasMortal = mortalOrFireOrRoadkill || victim.isBleeding || victim.IsOnFire || !victim.IsAlive || victim.IsDead;
            if (hasPrev && existing.WasMortalOrFireOrRoadkill)
                wasMortal = true;

            bool wasObjZone = hasPrev && existing.WasObjectiveZone;
            if (!wasObjZone && isEnemy && IsOnlinePtfoScoringActive() && IsEitherSoldierInActiveObjective(attacker, victim))
                wasObjZone = true;

            _localPlayerDamagedVictims[victimPtr] = new LocalPlayerVictimTag
            {
                Timestamp = now,
                IsEnemy = isEnemy,
                IsHumanPlayer = isHumanVictim,
                WasMortalOrFireOrRoadkill = wasMortal,
                WasObjectiveZone = wasObjZone
            };
        }

        internal static bool WasVictimKilledByLocalPlayer(IntPtr victimPtr)
        {
            return victimPtr != IntPtr.Zero && _creditedKillVictimPtrs.Contains(victimPtr);
        }

        private static bool IsLocalKillTrackingActive()
        {
            return IsOnlineScoreboardFixActive() ||
                   (VisionConfig.EnableMod.Value && VisionConfig.EnableDogTagArchive != null && VisionConfig.EnableDogTagArchive.Value);
        }

        private static void TryCreditVictimToLocalPlayer(IntPtr victimPtr, LocalPlayerVictimTag tag)
        {
            if (victimPtr == IntPtr.Zero || !IsLocalKillTrackingActive())
                return;

            if (!_creditedKillVictimPtrs.Add(victimPtr))
                return;

            _localPlayerDamagedVictims.Remove(victimPtr);
            if (!IsOnlineScoreboardFixActive())
                return;

            EnsureLocalPlayerScoreboardSeeded();

            short deltaScore = (short)(tag.IsHumanPlayer ? 5 : 1);
            if (tag.IsEnemy)
            {
                bool ptfoObjKill = tag.WasObjectiveZone && IsOnlinePtfoScoringActive();
                if (ptfoObjKill)
                {
                    deltaScore += 1;
                    PtfoObjKillBonusesCount++;
                    PtfoTotalPointsEarned++;
                }
                _authLocalKills = (short)Mathf.Clamp(_authLocalKills + 1, 0, short.MaxValue);
                _authLocalScore = (short)Mathf.Clamp(_authLocalScore + deltaScore, short.MinValue, short.MaxValue);
                if (ptfoObjKill)
                    AiCore.LogDebug("PTFO", $"+1 Obj Attack/Defense Kill Bonus -> TotalScore:{_authLocalScore}");
            }
            else
            {
                // Preserve vanilla friendly-fire score penalty without incrementing Kills
                _authLocalScore = (short)Mathf.Clamp(_authLocalScore - deltaScore, short.MinValue, short.MaxValue);
            }
            _localScoreboardDirty = true;
        }

        private static void TryRecordLocalDeath(Soldier deadSoldier, bool isAbandonedAliveBleeder)
        {
            if (deadSoldier == null || deadSoldier.Pointer == IntPtr.Zero || deadSoldier.HasSurrended())
                return;

            IntPtr ptr = deadSoldier.Pointer;
            if (!_creditedLocalDeathSoldierPtrs.Add(ptr))
                return;

            EnsureLocalPlayerScoreboardSeeded();
            _authLocalDeaths = (short)Mathf.Clamp(_authLocalDeaths + 1, 0, short.MaxValue);
            _localScoreboardDirty = true;

            if (isAbandonedAliveBleeder)
                _abandonedBleedingLocalSoldiers.Add(ptr);
        }

        public static void NotifyVehicleRoadkillHit(Vehicle veh, Soldier victim)
        {
            try
            {
                if (!IsOnlineScoreboardFixActive() ||
                    veh == null ||
                    victim == null ||
                    veh.Pointer == IntPtr.Zero ||
                    victim.Pointer == IntPtr.Zero ||
                    !veh.PlayerIsDriving())
                {
                    return;
                }

                Soldier localDriver = Soldier.CurrentControlledSoldierOrNull();
                if (localDriver == null ||
                    localDriver.Pointer == IntPtr.Zero ||
                    !localDriver.IsOnVehicle() ||
                    localDriver.GetCurrentVehicle() == null ||
                    localDriver.GetCurrentVehicle().Pointer != veh.Pointer)
                {
                    return;
                }

                TagVictimDamagedByLocalPlayer(localDriver, victim, mortalOrFireOrRoadkill: true);
            }
            catch { }
        }

        [HarmonyPatch(typeof(Photon.Realtime.Player), nameof(Photon.Realtime.Player.SetCustomProperties))]
        public static class Player_SetCustomProperties_Patch
        {
            public static bool Prefix(Photon.Realtime.Player __instance, ExitGames.Client.Photon.Hashtable __0)
            {
                try
                {
                    if (!IsOnlineScoreboardFixActive() ||
                        _isInternalScoreboardFlush ||
                        _inBattleLoaderOnJoinedRoom ||
                        __instance == null ||
                        !__instance.IsLocal ||
                        __0 == null)
                    {
                        return true;
                    }

                    EnsureScoreboardPropertyKeys();
                    bool stripped = false;
                    if (__0.ContainsKey(_propKeyKillsObj))
                    {
                        __0.Remove(_propKeyKillsObj);
                        stripped = true;
                    }
                    if (__0.ContainsKey(_propKeyDeathsObj))
                    {
                        __0.Remove(_propKeyDeathsObj);
                        stripped = true;
                    }
                    if (__0.ContainsKey(_propKeyScoreObj))
                    {
                        __0.Remove(_propKeyScoreObj);
                        stripped = true;
                    }

                    // Suppress empty stale scoreboard packets so vanilla DamageFeedback/Soldier.Kill never clobbers our ledger
                    if (stripped && __0.Count == 0)
                    {
                        ScoreboardVanillaPacketsSuppressedCount++;
                        return false;
                    }
                    return __0.Count > 0;
                }
                catch { }
                return true;
            }
        }

        [HarmonyPatch(typeof(BattleLoader), nameof(BattleLoader.OnJoinedRoom))]
        public static class BattleLoader_OnJoinedRoom_Patch
        {
            public static void Prefix()
            {
                ResetLocalPlayerScoreboardLedger();
                _pendingJoinMotdQueue.Clear();
                ResetBattleAtmosphereBroadcastState();
                _inBattleLoaderOnJoinedRoom = true;
            }

            public static void Postfix()
            {
                _inBattleLoaderOnJoinedRoom = false;
                try
                {
                    if (VisionConfig.EnableMod.Value)
                    {
                        AiCore.EnsureLocalModVersionPublished();
                    }

                    if (IsOnlineScoreboardFixActive())
                    {
                        EnsureLocalPlayerScoreboardSeeded();
                        SyncPtfoRoomPropertyIfMaster();
                    }
                }
                catch { }
            }
        }

        private struct PendingJoinMotdEntry
        {
            public IntPtr RoomPtr;
            public Photon.Realtime.Player PlayerRef;
            public int ActorNumber;
            public string DedupKey;
            public float EnqueuedAtUnscaledTime;
            public float ReadyAtUnscaledTime;
            public float SendAtUnscaledTime;
        }

        private static readonly Il2CppSystem.Object _propKeyFactionObj = (Il2CppSystem.String)"faction";
        private static readonly List<PendingJoinMotdEntry> _pendingJoinMotdQueue = new List<PendingJoinMotdEntry>(8);
        private static readonly HashSet<string> _welcomedMotdPlayerKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static IntPtr _lastAtmosphereBroadcastBattlePtr;
        private static float _atmosphereBroadcastReadyAtUnscaled;
        private static bool _atmosphereBroadcastSentForBattle;

        internal static void ResetBattleAtmosphereBroadcastState()
        {
            _lastAtmosphereBroadcastBattlePtr = IntPtr.Zero;
            _atmosphereBroadcastReadyAtUnscaled = 0f;
            _atmosphereBroadcastSentForBattle = false;
        }

        public static void UpdateBattleAtmosphereBroadcast()
        {
            if (!VisionConfig.EnableMod.Value ||
                (VisionConfig.EnableBattleAtmosphereDirectives != null && !VisionConfig.EnableBattleAtmosphereDirectives.Value) ||
                (VisionConfig.BroadcastBattleAtmosphereChat != null && !VisionConfig.BroadcastBattleAtmosphereChat.Value))
            {
                return;
            }

            if (BattleManager.instance == null || PlayerGUI.instance == null)
                return;

            AiCore.BattleTheaterSnapshot theater = AiCore.GetBattleTheaterSnapshot();
            if (theater.BattleDataPtr == IntPtr.Zero || theater.Atmosphere == AiCore.BattleAtmosphereArchetype.None)
                return;

            if (theater.BattleDataPtr != _lastAtmosphereBroadcastBattlePtr)
            {
                _lastAtmosphereBroadcastBattlePtr = theater.BattleDataPtr;
                _atmosphereBroadcastSentForBattle = false;
                _atmosphereBroadcastReadyAtUnscaled = Time.unscaledTime + 4.0f;
                return;
            }

            if (_atmosphereBroadcastSentForBattle || Time.unscaledTime < _atmosphereBroadcastReadyAtUnscaled)
                return;

            _atmosphereBroadcastSentForBattle = true;
            string msg = AiCore.GetBattleAtmosphereDispatchText(theater.Atmosphere);
            if (string.IsNullOrEmpty(msg))
                return;

            bool inMpRoom = AiCore.IsInMultiplayerRoom();
            if (inMpRoom && !AiCore.IsMasterClient())
                return;

            Plugin.Log?.LogInfo($"[Atmosphere] Active directive: {theater.Atmosphere} ({theater.Map})");
            try
            {
                new ChatMessage($"<color=#D4B46A>{msg}</color>");
            }
            catch { }

            if (!inMpRoom)
                return;

            try
            {
                var room = Photon.Pun.PhotonNetwork.CurrentRoom;
                var playersMap = room != null ? room.Players : null;
                if (playersMap == null)
                    return;

                foreach (var kv in playersMap)
                {
                    Photon.Realtime.Player p = kv.Value;
                    if (p == null || p.IsLocal)
                        continue;
                    AiCore.SendTargetedVanillaChatMessage(p, msg, minPlayerInterval: 0f, bypassRpcBudget: true);
                }
            }
            catch { }
        }

        private static bool IsValidPlayableFactionString(string s)
        {
            if (string.IsNullOrWhiteSpace(s))
                return false;

            string t = s.Trim();
            return !t.Equals("Unknown", StringComparison.OrdinalIgnoreCase) &&
                   !t.Equals("Civilian", StringComparison.OrdinalIgnoreCase) &&
                   !t.Equals("None", StringComparison.OrdinalIgnoreCase) &&
                   !t.Equals("Spectator", StringComparison.OrdinalIgnoreCase) &&
                   !t.Equals("Neutral", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryGetPhotonPlayerFaction(Photon.Realtime.Player p, out string faction)
        {
            faction = null;
            if (p == null)
                return false;

            try
            {
                ExitGames.Client.Photon.Hashtable props = p.CustomProperties;
                if (props != null && props.ContainsKey(_propKeyFactionObj))
                {
                    Il2CppSystem.Object raw = props[_propKeyFactionObj];
                    string s = raw != null ? raw.ToString() : null;
                    if (IsValidPlayableFactionString(s))
                    {
                        faction = s.Trim();
                        return true;
                    }
                }
            }
            catch { }

            try
            {
                AiCore.EnsureWorldSnapshot();
                int count = AiCore.WorldSoldierCount;
                var soldiers = AiCore.WorldSoldiers;
                int targetActor = p.ActorNumber;
                for (int i = 0; i < count; i++)
                {
                    ref AiCore.SoldierSnapshot ss = ref soldiers[i];
                    if (!ss.IsPlayer || ss.Ref == null || !IsValidPlayableFactionString(ss.Faction))
                        continue;

                    SyncSoldier syn = ss.Ref.soldierSyncher;
                    if (syn == null)
                        continue;

                    Photon.Realtime.Player owner = syn.Controller ?? syn.Owner;
                    if (owner != null && owner.ActorNumber == targetActor)
                    {
                        faction = ss.Faction;
                        return true;
                    }
                }
            }
            catch { }

            return false;
        }

        private static string GetFactionMotdGreeting(string faction)
        {
            if (string.IsNullOrWhiteSpace(faction))
                return "Welcome to the front, soldier!";

            string f = faction.Trim().ToLowerInvariant();
            if (f.Contains("germany") || f.Contains("ger") || f.Contains("wehrmacht") || f.Contains("_ss"))
                return "Willkommen an der Front, Kamerad!";
            if (f.Contains("italy") || f.Contains("ita"))
                return "Benvenuto al fronte, commilitone!";
            if (f.Contains("france") || f.StartsWith("fr") || f.Contains("_fr"))
                return "Bienvenue au front, camarade!";
            if (f.Contains("poland") || f.Contains("pol"))
                return "Witamy na froncie, towarzyszu!";
            if (f.Contains("ussr") || f.Contains("soviet") || f.Contains("rus"))
                return "Dobro pozhalovat na front, tovarishch!";
            if (f.Contains("japan") || f.Contains("jap") || f.Contains("ijn") || f.Contains("ija"))
                return "Sensen e yokoso, senyu!";
            if (f.Contains("china") || f.Contains("chn"))
                return "Huan ying lai dao qian xian, zhan you!";
            if (f.Contains("hungary") || f.Contains("hun"))
                return "Udvozoljuk a fronton, bajtars!";
            if (f.Contains("romania") || f.Contains("rom"))
                return "Bine ai venit pe front, camarade!";
            if (f.Contains("uk") || f.Contains("brit") || f.Contains("eng") || f.Contains("aus") || f.Contains("can") || f.Contains("ind") || f.Contains("commonwealth"))
                return "Welcome to the front, mate!";

            return "Welcome to the front, soldier!";
        }

        private static string GetJoinerMotdDedupKey(Photon.Realtime.Player p)
        {
            if (p == null)
                return string.Empty;
            try
            {
                string uid = p.UserId;
                if (!string.IsNullOrWhiteSpace(uid))
                    return "uid:" + uid.Trim();
            }
            catch { }
            try
            {
                string nick = p.NickName;
                if (!string.IsNullOrWhiteSpace(nick))
                    return "nick:" + nick.Trim();
            }
            catch { }
            try
            {
                IntPtr roomPtr = Photon.Pun.PhotonNetwork.CurrentRoom != null ? Photon.Pun.PhotonNetwork.CurrentRoom.Pointer : IntPtr.Zero;
                return $"act:{roomPtr}:{p.ActorNumber}";
            }
            catch
            {
                return string.Empty;
            }
        }

        [HarmonyPatch(typeof(BattleLoader), nameof(BattleLoader.OnPlayerEnteredRoom))]
        public static class BattleLoader_OnPlayerEnteredRoom_Patch
        {
            public static void Postfix(Photon.Realtime.Player __0)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value ||
                        (VisionConfig.EnableLobbyJoinMotd != null && !VisionConfig.EnableLobbyJoinMotd.Value) ||
                        __0 == null ||
                        __0.IsLocal ||
                        !AiCore.IsInMultiplayerRoom() ||
                        !AiCore.IsMasterClient())
                    {
                        return;
                    }

                    IntPtr curRoomPtr = Photon.Pun.PhotonNetwork.CurrentRoom != null
                        ? Photon.Pun.PhotonNetwork.CurrentRoom.Pointer
                        : IntPtr.Zero;
                    string dedupKey = GetJoinerMotdDedupKey(__0);
                    if (!string.IsNullOrEmpty(dedupKey) && _welcomedMotdPlayerKeys.Contains(dedupKey))
                        return;

                    int actorNum = __0.ActorNumber;
                    for (int i = _pendingJoinMotdQueue.Count - 1; i >= 0; i--)
                    {
                        PendingJoinMotdEntry existing = _pendingJoinMotdQueue[i];
                        if (existing.RoomPtr != curRoomPtr)
                        {
                            _pendingJoinMotdQueue.RemoveAt(i);
                            continue;
                        }

                        if (existing.ActorNumber == actorNum)
                        {
                            if (string.Equals(existing.DedupKey, dedupKey, StringComparison.OrdinalIgnoreCase))
                                return;

                            _pendingJoinMotdQueue.RemoveAt(i);
                        }
                    }

                    if (_pendingJoinMotdQueue.Count < 16)
                    {
                        float nowUnscaled = Time.unscaledTime;
                        _pendingJoinMotdQueue.Add(new PendingJoinMotdEntry
                        {
                            RoomPtr = curRoomPtr,
                            PlayerRef = __0,
                            ActorNumber = actorNum,
                            DedupKey = dedupKey,
                            EnqueuedAtUnscaledTime = nowUnscaled,
                            ReadyAtUnscaledTime = 0f,
                            SendAtUnscaledTime = nowUnscaled + 1.5f
                        });
                    }
                }
                catch { }
            }
        }

        public static void UpdateLobbyJoinMotdQueue()
        {
            if (_pendingJoinMotdQueue.Count == 0)
                return;

            if (!VisionConfig.EnableMod.Value ||
                (VisionConfig.EnableLobbyJoinMotd != null && !VisionConfig.EnableLobbyJoinMotd.Value) ||
                !AiCore.IsInMultiplayerRoom() ||
                !AiCore.IsMasterClient())
            {
                _pendingJoinMotdQueue.Clear();
                return;
            }

            var room = Photon.Pun.PhotonNetwork.CurrentRoom;
            if (room == null || room.Players == null)
            {
                _pendingJoinMotdQueue.Clear();
                return;
            }

            IntPtr curRoomPtr = room.Pointer;
            float now = Time.unscaledTime;
            for (int i = _pendingJoinMotdQueue.Count - 1; i >= 0; i--)
            {
                PendingJoinMotdEntry entry = _pendingJoinMotdQueue[i];
                if (entry.RoomPtr != curRoomPtr)
                {
                    _pendingJoinMotdQueue.RemoveAt(i);
                    continue;
                }

                if (now < entry.SendAtUnscaledTime)
                    continue;

                Photon.Realtime.Player target = null;
                try
                {
                    if (room.Players.ContainsKey(entry.ActorNumber))
                        target = room.Players[entry.ActorNumber];
                }
                catch { }

                if (target == null || target.IsLocal)
                {
                    _pendingJoinMotdQueue.RemoveAt(i);
                    continue;
                }

                string liveDedup = GetJoinerMotdDedupKey(target);
                if (!string.IsNullOrEmpty(entry.DedupKey) &&
                    !string.IsNullOrEmpty(liveDedup) &&
                    !string.Equals(entry.DedupKey, liveDedup, StringComparison.OrdinalIgnoreCase))
                {
                    _pendingJoinMotdQueue.RemoveAt(i);
                    continue;
                }

                if ((!string.IsNullOrEmpty(entry.DedupKey) && _welcomedMotdPlayerKeys.Contains(entry.DedupKey)) ||
                    (!string.IsNullOrEmpty(liveDedup) && _welcomedMotdPlayerKeys.Contains(liveDedup)))
                {
                    _pendingJoinMotdQueue.RemoveAt(i);
                    continue;
                }

                bool hasFaction = TryGetPhotonPlayerFaction(target, out string joinerFaction);
                if (entry.ReadyAtUnscaledTime <= 0f)
                {
                    if (hasFaction)
                    {
                        entry.ReadyAtUnscaledTime = now + 1.5f;
                        _pendingJoinMotdQueue[i] = entry;
                        continue;
                    }
                    if (now - entry.EnqueuedAtUnscaledTime < 75.0f)
                        continue;
                    entry.ReadyAtUnscaledTime = now;
                }

                if (now < entry.ReadyAtUnscaledTime)
                    continue;

                _pendingJoinMotdQueue.RemoveAt(i);

                AiCore.InvalidatePlayerModdedCache(entry.ActorNumber);
                bool isModdedJoiner = AiCore.IsPhotonPlayerConfirmedModded(target);
                string greetingLine = GetFactionMotdGreeting(joinerFaction);
                string[] motdLines = isModdedJoiner
                    ? new[]
                    {
                        $"{greetingLine} [Battle Hardened v{Plugin.PluginVersion} Lobby]",
                        "Squad Leaders: use [Radial] or [Map] to Accept or Balk HQ radio orders."
                    }
                    : new[]
                    {
                        $"{greetingLine} [Battle Hardened AI Host - No Download Needed]",
                        "AI uses human FOV (flanks, smoke, scavenging); rifles pierce thin wood.",
                        "Keep your Radioman alive for HQ orders & armor/flank support. Good luck!"
                    };

                bool anySent = false;
                for (int lineIdx = 0; lineIdx < motdLines.Length; lineIdx++)
                {
                    if (AiCore.SendTargetedVanillaChatMessage(target, motdLines[lineIdx], minPlayerInterval: 0f, bypassRpcBudget: true))
                        anySent = true;
                }

                if (anySent)
                {
                    string joinerNick = !string.IsNullOrEmpty(target.NickName) ? target.NickName : $"Player#{entry.ActorNumber}";
                    try
                    {
                        if (PlayerGUI.instance != null)
                        {
                            for (int lineIdx = 0; lineIdx < motdLines.Length; lineIdx++)
                            {
                                string prefix = lineIdx == 0 ? $"<color=#9AA7B0>[MOTD -> {joinerNick}]</color> " : "<color=#9AA7B0>[MOTD]</color> ";
                                new ChatMessage(prefix + motdLines[lineIdx]);
                            }
                        }
                    }
                    catch { }

                    if (VisionConfig.DebugLogging.Value)
                    {
                        AiCore.LogDebug("MOTD", $"Sent welcome MOTD to '{joinerNick}' (faction={joinerFaction ?? "none"}, modded={isModdedJoiner}): {greetingLine}", 0f);
                    }

                    if (!string.IsNullOrEmpty(entry.DedupKey))
                    {
                        if (_welcomedMotdPlayerKeys.Count > 128)
                            _welcomedMotdPlayerKeys.Clear();
                        _welcomedMotdPlayerKeys.Add(entry.DedupKey);
                    }
                    if (!string.IsNullOrEmpty(liveDedup))
                    {
                        if (_welcomedMotdPlayerKeys.Count > 128)
                            _welcomedMotdPlayerKeys.Clear();
                        _welcomedMotdPlayerKeys.Add(liveDedup);
                    }
                }
            }
        }

        [HarmonyPatch(typeof(BattleLoader), nameof(BattleLoader.OnPlayerPropertiesUpdate))]
        public static class BattleLoader_OnPlayerPropertiesUpdate_Patch
        {
            public static void Postfix(Photon.Realtime.Player __0, ExitGames.Client.Photon.Hashtable __1)
            {
                try
                {
                    if (__1 == null)
                        return;

                    if (__0 != null && __1.ContainsKey((Il2CppSystem.String)"bh_ver"))
                    {
                        AiCore.InvalidatePlayerModdedCache(__0.ActorNumber);
                    }

                    if (!IsOnlineScoreboardFixActive())
                        return;

                    EnsureScoreboardPropertyKeys();
                    if (__1.ContainsKey(_propKeyScoreObj) ||
                        __1.ContainsKey(_propKeyKillsObj) ||
                        __1.ContainsKey(_propKeyDeathsObj))
                    {
                        if (MiniMapGUI.ShowingPlayerList())
                            _pendingOpenPlayerListRefresh = true;
                    }
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(BattleLoader), nameof(BattleLoader.RequestOwnershipAndSpawnOn))]
        public static class BattleLoader_RequestOwnershipAndSpawnOn_Patch
        {
            public static void Prefix(Photon.Realtime.Player __0, int __1, int __2, int __3)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value || !AiCore.IsInMultiplayerRoom())
                        return;

                    Photon.Pun.PhotonView pv = Photon.Pun.PhotonView.Find(__1);
                    Soldier s = pv != null ? pv.GetComponent<Soldier>() : null;
                    if (s == null || s.Pointer == IntPtr.Zero)
                    {
                        if (VisionConfig.DebugLogging.Value)
                            AiCore.LogDebug("CoopSpawn", $"RequestOwnershipAndSpawnOn from '{__0?.NickName ?? "?"}' rejected: viewID={__1} not found.");
                        return;
                    }

                    SyncSoldier syn = s.soldierSyncher;
                    if (syn != null && syn.OwnSyncher && s.IsAlive && !s.IsInjured && !s.HasSurrended() && !s.IsPlayer() && syn.controlled_by_player)
                    {
                        syn.UnsetAsControlledByPlayer();
                        if (VisionConfig.DebugLogging.Value)
                            AiCore.LogDebug("CoopSpawn", $"Auto-cleared stale controlled_by_player on '{s.name_surname}' (viewID={__1}) for '{__0?.NickName ?? "?"}'.");
                    }
                    else if (syn != null && syn.OwnSyncher && s.HasSurrended())
                    {
                        AiCore.SoldierFastHandles sh = AiCore.GetOrCreateSoldierHandles(s);
                        float now = Time.time;
                        if (sh == null || now >= sh.LastCoopSpawnHealTime + 2.5f)
                        {
                            if (sh != null)
                                sh.LastCoopSpawnHealTime = now;

                            if (sh != null && sh.IsRetreatDespawning)
                            {
                                if (s.joinedSquad != null)
                                    s.joinedSquad.Leave(s, false);
                            }
                            else if (s.IsAlive && !s.IsInjured && !s.IsDead)
                            {
                                s.SurrenderSynched();
                            }
                        }
                    }

                    if (VisionConfig.DebugLogging.Value)
                    {
                        bool seatOk = true;
                        if (__2 > 0)
                        {
                            Photon.Pun.PhotonView vpv = Photon.Pun.PhotonView.Find(__2);
                            Vehicle reqVeh = vpv != null ? vpv.GetComponent<Vehicle>() : null;
                            Seats curSeat = s.GetCurrentVehicleSeat();
                            seatOk = reqVeh != null &&
                                     __3 >= 0 &&
                                     reqVeh.seats != null &&
                                     __3 < reqVeh.seats.Length &&
                                     reqVeh.seats[__3] != null &&
                                     reqVeh.seats[__3].unitSet != null &&
                                     reqVeh.seats[__3].unitSet.Pointer == s.Pointer &&
                                     curSeat != null &&
                                     curSeat.Pointer == reqVeh.seats[__3].Pointer;
                        }

                        if (!seatOk ||
                            !s.IsAlive ||
                            s.IsInjured ||
                            s.HasSurrended() ||
                            syn == null ||
                            !syn.OwnSyncher ||
                            s.IsPlayer() ||
                            syn.controlled_by_player)
                        {
                            AiCore.LogDebug(
                                "CoopSpawn",
                                $"RequestOwnershipAndSpawnOn from '{__0?.NickName ?? "?"}' will be rejected for '{s.name_surname}' (viewID={__1}): alive={s.IsAlive}, inj={s.IsInjured}, surr={s.HasSurrended()}, own={syn != null && syn.OwnSyncher}, isLocalPlayer={s.IsPlayer()}, ctrlByPlayer={syn != null && syn.controlled_by_player}, reqVeh={__2}, reqSeat={__3}, seatOk={seatOk}.");
                        }
                    }
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(SquadSoldierLabel), nameof(SquadSoldierLabel.BeginSpawnOnUnit))]
        public static class SquadSoldierLabel_BeginSpawnOnUnit_Patch
        {
            public static void Prefix(Soldier __0)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value || __0 == null || __0.Pointer == IntPtr.Zero || !__0.IsReloading)
                        return;

                    bool clearReload = __0.IsOnVehicle();
                    if (!clearReload && !AiCore.IsLocallyOwnedSoldier(__0))
                    {
                        GenericGun held = __0.GetHeldGun();
                        clearReload = held == null || !held.IsReloadingOrUsingBolt();
                    }

                    if (clearReload)
                    {
                        __0.IsReloading = false;
                    }
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(SyncSoldier), nameof(SyncSoldier.SetControlledByPlayer))]
        public static class SyncSoldier_SetControlledByPlayer_Patch
        {
            public static void Postfix(SyncSoldier __instance)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value || __instance == null || __instance.Pointer == IntPtr.Zero)
                        return;

                    if (PlayerGUI.IsSelectingSquad() && PlayerGUI.instance != null && PlayerGUI.GUISquad != null)
                    {
                        Soldier s = __instance.soldier;
                        if (s != null && s.joinedSquad != null && s.joinedSquad.Pointer == PlayerGUI.GUISquad.Pointer)
                        {
                            PlayerGUI.instance.respawnEntryNextRefresh = 0f;
                        }
                    }
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(DamageFeedback), nameof(DamageFeedback.OnSoldierHit))]
        public static class DamageFeedback_OnSoldierHit_Patch
        {
            public static void Postfix(Soldier __0, Soldier __1)
            {
                try
                {
                    if (!IsLocalKillTrackingActive() || __1 == null || __1.Pointer == IntPtr.Zero)
                        return;

                    IntPtr targetPtr = __1.Pointer;
                    bool targetDead = !__1.IsAlive || __1.IsDead;

                    if (__0 != null && __0.Pointer != IntPtr.Zero && __0.Pointer != targetPtr)
                    {
                        if (IsPlayerCharacter(__0))
                        {
                            bool mortalNow = targetDead || __1.isBleeding || __1.IsOnFire;
                            TagVictimDamagedByLocalPlayer(__0, __1, mortalNow);
                            if (targetDead &&
                                !__1.HasSurrended() &&
                                _localPlayerDamagedVictims.TryGetValue(targetPtr, out LocalPlayerVictimTag tag))
                            {
                                TryCreditVictimToLocalPlayer(targetPtr, tag);
                            }
                        }
                        else if (_localPlayerDamagedVictims.TryGetValue(targetPtr, out LocalPlayerVictimTag oldTag) &&
                                 (targetDead || !oldTag.WasMortalOrFireOrRoadkill))
                        {
                            // Another soldier finished off or took primary damage precedence on this unit
                            _localPlayerDamagedVictims.Remove(targetPtr);
                        }
                    }
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(DamageFeedback), nameof(DamageFeedback.OnVehicleHit))]
        public static class DamageFeedback_OnVehicleHit_Patch
        {
            public static void Postfix(Soldier __0, Vehicle __1, bool __5)
            {
                try
                {
                    if (IsLocalKillTrackingActive() &&
                        __0 != null &&
                        __0.Pointer != IntPtr.Zero &&
                        __1 != null &&
                        __1.Pointer != IntPtr.Zero &&
                        IsPlayerCharacter(__0))
                    {
                        string atkFac = AiCore.GetCachedFaction(__0);
                        if (!string.IsNullOrEmpty(atkFac))
                            _lastKnownLocalPlayerFaction = atkFac;

                        RecordLocalPlayerDamagedVehicle(__1.Pointer);
                        if (IsOnlineScoreboardFixActive() && __5 && (!__1.IsActive() || __1.IsDisabled()))
                        {
                            TryCreditEnemyVehicleDisableToLocalPlayer(__1, directPlayerHit: true);
                        }
                    }
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(Soldier), nameof(Soldier.UseSyryngeCR))]
        public static class Soldier_UseSyryngeCR_Patch
        {
            public static void Prefix(Soldier __instance, Creature __1)
            {
                try
                {
                    if (__instance == null || __instance.Pointer == IntPtr.Zero)
                        return;

                    AiCore.SoldierFastHandles docH = AiCore.GetOrCreateSoldierHandles(__instance);
                    if (docH != null)
                    {
                        docH.Goap.ActiveSyringeHealUntil = Time.time + 5.2f;
                        docH.Goap.MedicCloseApproachSince = 0f;
                        docH.Goap.MedicHealStartTime = 0f;
                        docH.Goap.MedicHealTargetPtr = IntPtr.Zero;
                    }

                    Soldier patSol = __1 != null ? __1.TryCast<Soldier>() : null;
                    if (VisionConfig.EnableMod.Value &&
                        patSol != null &&
                        patSol.Pointer != IntPtr.Zero &&
                        !patSol.IsDead)
                    {
                        string docFacEarly = AiCore.GetCachedFaction(__instance);
                        string patFacEarly = AiCore.GetCachedFaction(patSol);
                        if (!string.IsNullOrEmpty(docFacEarly) &&
                            !string.IsNullOrEmpty(patFacEarly) &&
                            AiCore.AreEnemyFactions(docFacEarly, patFacEarly))
                        {
                            // Mark this patient as actively undergoing an enemy syringe capture so Soldier.Surrender
                            // revives them (SetIncapacitated(false) -> IsAlive = true, LayerWeight(1) = 1f) BEFORE
                            // Surrender() sets m_Animator.SetBool("Surrender", true)!
                            AiCore.SoldierFastHandles patH = AiCore.GetOrCreateSoldierHandles(patSol);
                            if (patH != null)
                                patH.EnemySyringeCaptureUntil = Time.time + 10.0f;
                            ClearAllFriendlyTargetLocksOnSoldier(patSol);
                        }
                    }

                    if (!IsOnlinePtfoScoringActive() ||
                        !IsPlayerCharacter(__instance))
                    {
                        return;
                    }

                    _pendingLocalFriendlySyringePatientPtr = IntPtr.Zero;
                    if (patSol == null ||
                        patSol.Pointer == IntPtr.Zero ||
                        patSol.Pointer == __instance.Pointer ||
                        patSol.IsDead ||
                        (!patSol.IsAlive && !patSol.IsInjured) ||
                        patSol.HasSurrended())
                    {
                        return;
                    }

                    string docFac = AiCore.GetCachedFaction(__instance);
                    string patFac = AiCore.GetCachedFaction(patSol);
                    if (string.IsNullOrEmpty(docFac) ||
                        string.IsNullOrEmpty(patFac) ||
                        !AiCore.AreSameSideFactions(docFac, patFac))
                    {
                        return;
                    }

                    bool needsHeal = patSol.IsInjured ||
                                     patSol.isBleeding ||
                                     patSol.GetBleedValue() > 0.01f ||
                                     patSol.life_total.Value < 95;
                    if (!needsHeal)
                        return;

                    float now = Time.time;
                    if (_recentPtfoRevivedPatients.TryGetValue(patSol.Pointer, out float lastRevTime) &&
                        now >= lastRevTime &&
                        (now - lastRevTime) <= 15.0f)
                    {
                        return;
                    }

                    _pendingLocalFriendlySyringePatientPtr = patSol.Pointer;
                    _pendingLocalFriendlySyringeExpireTime = now + 12.0f;
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(GameStatistics), nameof(GameStatistics.OnAlliedRevived))]
        public static class GameStatistics_OnAlliedRevived_Patch
        {
            public static void Postfix()
            {
                try
                {
                    if (!IsOnlinePtfoScoringActive() || _pendingLocalFriendlySyringePatientPtr == IntPtr.Zero)
                        return;

                    float now = Time.time;
                    if (now <= _pendingLocalFriendlySyringeExpireTime)
                    {
                        if (_recentPtfoRevivedPatients.Count >= 32)
                        {
                            _reusableScoreboardPruneKeys.Clear();
                            foreach (var kv in _recentPtfoRevivedPatients)
                            {
                                if ((now - kv.Value) > 30.0f || now < kv.Value)
                                    _reusableScoreboardPruneKeys.Add(kv.Key);
                            }
                            for (int i = 0; i < _reusableScoreboardPruneKeys.Count; i++)
                                _recentPtfoRevivedPatients.Remove(_reusableScoreboardPruneKeys[i]);
                        }

                        _recentPtfoRevivedPatients[_pendingLocalFriendlySyringePatientPtr] = now;
                        PtfoRevivesCount++;
                        AwardLocalPtfoScore(2, "Friendly Syringe Revive");
                    }

                    _pendingLocalFriendlySyringePatientPtr = IntPtr.Zero;
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(Soldier), nameof(Soldier.SetBleeding))]
        public static class Soldier_SetBleeding_Patch
        {
            public static void Postfix(Soldier __instance, bool __0)
            {
                try
                {
                    if (!IsLocalKillTrackingActive() || __instance == null || __instance.Pointer == IntPtr.Zero)
                        return;

                    IntPtr ptr = __instance.Pointer;
                    if (__0)
                    {
                        if (_activeDamageAttackerPtr != IntPtr.Zero && _activeDamageAttackerPtr != new IntPtr(-1))
                        {
                            Soldier curLocal = Soldier.CurrentControlledSoldierOrNull();
                            if (curLocal != null && curLocal.Pointer == _activeDamageAttackerPtr && curLocal.Pointer != ptr)
                            {
                                TagVictimDamagedByLocalPlayer(curLocal, __instance, mortalOrFireOrRoadkill: true);
                            }
                            else
                            {
                                _localPlayerDamagedVictims.Remove(ptr);
                            }
                        }
                        else if (_localPlayerDamagedVictims.TryGetValue(ptr, out LocalPlayerVictimTag tag) &&
                                 (Time.time - tag.Timestamp) <= 1.5f)
                        {
                            tag.WasMortalOrFireOrRoadkill = true;
                            tag.Timestamp = Time.time;
                            _localPlayerDamagedVictims[ptr] = tag;
                        }
                    }
                    else if (__instance.IsAlive && !__instance.IsDead)
                    {
                        // Medic stopped bleeding on a live soldier: cancel pending bleed-out kill tag and refund abandoned-bleed death
                        _localPlayerDamagedVictims.Remove(ptr);

                        if (_abandonedBleedingLocalSoldiers.Remove(ptr))
                        {
                            _creditedLocalDeathSoldierPtrs.Remove(ptr);
                            EnsureLocalPlayerScoreboardSeeded();
                            if (_authLocalDeaths > 0)
                            {
                                _authLocalDeaths--;
                                _localScoreboardDirty = true;
                            }
                        }
                    }
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(Soldier), nameof(Soldier.SetOnFire))]
        public static class Soldier_SetOnFire_Patch
        {
            public static void Postfix(Soldier __instance)
            {
                try
                {
                    if (!IsLocalKillTrackingActive() || __instance == null || __instance.Pointer == IntPtr.Zero)
                        return;

                    Soldier curLocal = Soldier.CurrentControlledSoldierOrNull();
                    if (curLocal == null || curLocal.Pointer == IntPtr.Zero || curLocal.Pointer == __instance.Pointer)
                        return;

                    bool attackerIsLocal = _activeDamageAttackerPtr == curLocal.Pointer ||
                        (_localPlayerDamagedVictims.TryGetValue(__instance.Pointer, out LocalPlayerVictimTag fTag) &&
                         (Time.time - fTag.Timestamp) <= 2.0f);
                    if (attackerIsLocal)
                    {
                        TagVictimDamagedByLocalPlayer(curLocal, __instance, mortalOrFireOrRoadkill: true);
                    }
                }
                catch { }
            }
        }

        internal static bool TryComputeFrontlineSpawnDirection(Soldier s, Squad sq, Vector3 spawnPos, out Vector3 outDir)
        {
            outDir = Vector3.zero;
            if (s == null || s.Pointer == IntPtr.Zero)
                return false;

            string fac = AiCore.GetCachedFaction(s);
            if (sq != null && sq.Pointer != IntPtr.Zero)
            {
                try
                {
                    SquadTask task = sq.currentTask;
                    if (task != null && task.Pointer != IntPtr.Zero)
                    {
                        Vector3 dest = task.GetTaskDestination();
                        Vector3 toDest = dest - spawnPos;
                        toDest.y = 0f;
                        if (toDest.sqrMagnitude > 9.0f)
                        {
                            outDir = toDest.normalized;
                            return true;
                        }
                    }
                }
                catch { }
            }

            if (!string.IsNullOrEmpty(fac))
            {
                if (AiCore.TryGetNearestEnemyOrContestedObjectiveDirection(fac, spawnPos, out Vector3 objDir))
                {
                    objDir.y = 0f;
                    if (objDir.sqrMagnitude > 0.001f)
                    {
                        outDir = objDir.normalized;
                        return true;
                    }
                }

                if (VisionPatches.TryGetDominantEnemyGunfireDirection(spawnPos, fac, 320.0f, 12.0f, out Vector3 gfDir, out _))
                {
                    gfDir.y = 0f;
                    if (gfDir.sqrMagnitude > 0.001f)
                    {
                        outDir = gfDir.normalized;
                        return true;
                    }
                }

                if (AiCore.TryGetEnemySpawnDirection(fac, spawnPos, out Vector3 spDir))
                {
                    spDir.y = 0f;
                    if (spDir.sqrMagnitude > 0.001f)
                    {
                        outDir = spDir.normalized;
                        return true;
                    }
                }
            }

            return false;
        }

        internal static void OrientNewlySpawnedSoldierTowardFrontline(
            Soldier s,
            Squad sq,
            float aiJitterDegrees,
            bool isPreSetPlayerHook)
        {
            if (!VisionConfig.EnableMod.Value ||
                s == null ||
                s.Pointer == IntPtr.Zero ||
                !s.IsAlive ||
                s.IsOnVehicle() ||
                s.transform == null)
            {
                return;
            }

            if (AiCore.IsInMultiplayerRoom() && !AiCore.IsLocallyOwnedSoldier(s))
                return;

            AiCore.SoldierFastHandles h = AiCore.GetOrCreateSoldierHandles(s);
            float now = Time.time;
            if (h != null)
            {
                // When SetPlayer switches to a veteran squadmate mid-battle (> 2.5s after spawn), NEVER snap their aim!
                if (isPreSetPlayerHook && h.CreatedAtTime > 0f && (now - h.CreatedAtTime) > 2.5f)
                    return;

                if (!isPreSetPlayerHook && h.LastSpawnOrientTime > 0f && (now - h.LastSpawnOrientTime) < 2.5f)
                    return;

                h.LastSpawnOrientTime = now;
            }

            Vector3 spawnPos = s.transform.position;
            if (!TryComputeFrontlineSpawnDirection(s, sq ?? s.joinedSquad, spawnPos, out Vector3 dir))
                return;

            float yaw = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
            bool isHuman = isPreSetPlayerHook || s.IsPlayer();
            if (!isHuman && aiJitterDegrees > 0.05f)
            {
                float u = ((AiCore.GetDeterministicHash(s.Pointer, 441) & 1023) / 511.5f) - 1.0f;
                yaw += u * aiJitterDegrees;
            }

            s.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            if (isHuman)
            {
                try
                {
                    LookParameters lp = s.lookRotation;
                    if (lp != null)
                        lp.leftRight = 0f;
                    LookParameters camLp = PlayerController.camLookRotation;
                    if (camLp != null)
                        camLp.leftRight = yaw;
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(Squad), nameof(Squad.SetFullySpawned))]
        public static class Squad_SetFullySpawned_Patch
        {
            public static void Postfix(Squad __instance)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value || __instance == null || __instance.Pointer == IntPtr.Zero)
                        return;

                    var units = __instance.units;
                    if (units == null)
                        return;

                    int count = units.Count;
                    for (int i = 0; i < count; i++)
                    {
                        Soldier u = units[i];
                        if (u != null && u.IsAlive && !u.IsOnVehicle())
                        {
                            OrientNewlySpawnedSoldierTowardFrontline(u, __instance, aiJitterDegrees: 10.0f, isPreSetPlayerHook: false);
                        }
                    }
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(PlayerController), nameof(PlayerController.SetPlayer))]
        public static class PlayerController_SetPlayer_Patch
        {
            public static void Prefix(PlayerController __instance, Soldier __0)
            {
                try
                {
                    // Orient freshly spawned on-foot player soldier BEFORE Soldier.OnSetPlayer -> RespawnCameraTransition
                    // so the 1.3s respawn camera swoop lands cleanly facing the objective/frontline!
                    if (__0 != null && __0.Pointer != IntPtr.Zero)
                    {
                        OrientNewlySpawnedSoldierTowardFrontline(__0, __0.joinedSquad, aiJitterDegrees: 0f, isPreSetPlayerHook: true);
                    }

                    if (!IsOnlineScoreboardFixActive() || __instance == null)
                        return;

                    Soldier prev = __instance.ControlledCharacter;
                    if (prev == null || prev.Pointer == IntPtr.Zero)
                        return;

                    if (__0 != null && __0.Pointer == prev.Pointer)
                        return;

                    // Count abandoning a mortally downed (isBleeding) or dead soldier on squad switch / Respawn
                    if (!prev.IsAlive || prev.IsDead || prev.isBleeding)
                    {
                        bool isAbandonedAliveBleeder = prev.IsAlive && !prev.IsDead && prev.isBleeding;
                        TryRecordLocalDeath(prev, isAbandonedAliveBleeder);
                    }
                }
                catch { }
            }

            public static void Postfix()
            {
                try
                {
                    if (VisionConfig.EnableMod.Value)
                        RefreshBottomLeftSquadRosterVisuals();
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(Soldier), nameof(Soldier.Kill))]
        public static class Soldier_Kill_Patch
        {
            public static void Prefix(Soldier __instance)
            {
                try
                {
                    if (__instance != null && __instance.Pointer != IntPtr.Zero && __instance.IsCarryingBody())
                    {
                        __instance.CarryBody(null);
                    }

                    if (VisionConfig.EnableEmergentDecisionEngine != null && VisionConfig.EnableEmergentDecisionEngine.Value)
                    {
                        EmergentRuntimeBridge.NotifySoldierKilled(__instance, Vector3.zero, false);
                    }

                    if (!IsLocalKillTrackingActive() || __instance == null || __instance.Pointer == IntPtr.Zero)
                        return;

                    IntPtr victimPtr = __instance.Pointer;
                    _abandonedBleedingLocalSoldiers.Remove(victimPtr);

                    // 1. Local player death (runs before Soldier.Kill detaches ControlledCharacter and regardless of vehicle hide state!)
                    if (IsPlayerCharacter(__instance))
                    {
                        if (IsOnlineScoreboardFixActive())
                            TryRecordLocalDeath(__instance, isAbandonedAliveBleeder: false);
                        return;
                    }

                    // 2. Local player kill / friendly-fire attribution
                    if (_creditedKillVictimPtrs.Contains(victimPtr) || __instance.HasSurrended())
                        return;

                    Soldier curLocal = Soldier.CurrentControlledSoldierOrNull();
                    IntPtr localPtr = curLocal != null ? curLocal.Pointer : IntPtr.Zero;

                    // 2a. Synchronous attacker context
                    if (_activeDamageAttackerPtr != IntPtr.Zero)
                    {
                        if (_activeDamageAttackerPtr == localPtr && localPtr != IntPtr.Zero)
                        {
                            TagVictimDamagedByLocalPlayer(curLocal, __instance, mortalOrFireOrRoadkill: true);
                            if (_localPlayerDamagedVictims.TryGetValue(victimPtr, out LocalPlayerVictimTag syncTag))
                            {
                                TryCreditVictimToLocalPlayer(victimPtr, syncTag);
                            }
                            return;
                        }

                        // Another specific non-local shooter is landing the synchronous kill
                        if (_activeDamageAttackerPtr != new IntPtr(-1))
                        {
                            _localPlayerDamagedVictims.Remove(victimPtr);
                            return;
                        }
                    }

                    // 2b. Delayed bleed-out, fire, melee, roadkill, or SyncDeath RPC from prior local player hit
                    if (_localPlayerDamagedVictims.TryGetValue(victimPtr, out LocalPlayerVictimTag delayedTag))
                    {
                        float age = Time.time - delayedTag.Timestamp;
                        bool validDelayed = (delayedTag.WasMortalOrFireOrRoadkill && age <= 90.0f) ||
                                            (_activeDamageAttackerPtr == IntPtr.Zero && age <= 6.0f);
                        if (validDelayed)
                        {
                            if (!delayedTag.WasObjectiveZone &&
                                delayedTag.IsEnemy &&
                                IsOnlinePtfoScoringActive() &&
                                curLocal != null &&
                                IsEitherSoldierInActiveObjective(curLocal, __instance))
                            {
                                delayedTag.WasObjectiveZone = true;
                            }
                            TryCreditVictimToLocalPlayer(victimPtr, delayedTag);
                            return;
                        }
                    }

                    // 2c. Enemy crew/pilot killed inside a vehicle/aircraft recently crippled by local player
                    if (curLocal != null && localPtr != IntPtr.Zero && __instance.IsOnVehicle())
                    {
                        Vehicle crewVeh = __instance.GetCurrentVehicle();
                        if (crewVeh != null &&
                            crewVeh.Pointer != IntPtr.Zero &&
                            _localPlayerDamagedVehicles.TryGetValue(crewVeh.Pointer, out float vHitTime) &&
                            (Time.time - vHitTime) <= 20.0f &&
                            !AiCore.AreSameSideFactions(AiCore.GetCachedFaction(curLocal), AiCore.GetCachedFaction(__instance)))
                        {
                            TryCreditVictimToLocalPlayer(victimPtr, new LocalPlayerVictimTag
                            {
                                Timestamp = Time.time,
                                IsEnemy = true,
                                IsHumanPlayer = IsHumanControlledSoldier(__instance),
                                WasMortalOrFireOrRoadkill = true,
                                WasObjectiveZone = IsOnlinePtfoScoringActive() && IsEitherSoldierInActiveObjective(curLocal, __instance)
                            });
                            return;
                        }
                    }

                    // 2d. Remote-client driver roadkill fallback (Host-swept OBB roadkill SyncDeath RPC while client is driving)
                    if (_activeDamageAttackerPtr == IntPtr.Zero &&
                        curLocal != null &&
                        localPtr != IntPtr.Zero &&
                        curLocal.IsOnVehicle() &&
                        __instance.transform != null)
                    {
                        Vehicle drvVeh = curLocal.GetCurrentVehicle();
                        if (drvVeh != null &&
                            drvVeh.Pointer != IntPtr.Zero &&
                            !drvVeh.IsAirVehicle() &&
                            drvVeh.PlayerIsDriving() &&
                            drvVeh.transform != null &&
                            !AiCore.AreSameSideFactions(AiCore.GetCachedFaction(curLocal), AiCore.GetCachedFaction(__instance)))
                        {
                            MovableVehicle mv = drvVeh.TryCast<MovableVehicle>();
                            Rigidbody rb = mv != null ? mv.rb : null;
                            if (rb != null && !rb.isKinematic)
                            {
                                Vector3 vel = rb.velocity;
                                if ((vel.x * vel.x + vel.z * vel.z) > 0.75f * 0.75f)
                                {
                                    Vector4 obb = GetCachedVehicleLocalHullObb(drvVeh);
                                    Vector3 d = __instance.transform.position - drvVeh.transform.position;
                                    float maxHoriz = obb.x + obb.y + 4.5f;
                                    if (Mathf.Abs(d.y) <= 3.2f && (d.x * d.x + d.z * d.z) <= maxHoriz * maxHoriz)
                                    {
                                        TryCreditVictimToLocalPlayer(victimPtr, new LocalPlayerVictimTag
                                        {
                                            Timestamp = Time.time,
                                            IsEnemy = true,
                                            IsHumanPlayer = IsHumanControlledSoldier(__instance),
                                            WasMortalOrFireOrRoadkill = true,
                                            WasObjectiveZone = IsOnlinePtfoScoringActive() && IsEitherSoldierInActiveObjective(curLocal, __instance)
                                        });
                                    }
                                }
                            }
                        }
                    }
                }
                catch { }
            }
        }

        private static void GetPlayerScoreboardSortStats(
            Photon.Realtime.Player p,
            out short score,
            out short kills,
            out short deaths,
            out int actorNumber)
        {
            score = 0;
            kills = 0;
            deaths = 0;
            actorNumber = int.MaxValue;
            if (p == null)
                return;

            actorNumber = p.ActorNumber;
            if (p.IsLocal && _localScoreboardSeeded)
            {
                score = _authLocalScore;
                kills = _authLocalKills;
                deaths = _authLocalDeaths;
                return;
            }

            EnsureScoreboardPropertyKeys();
            ExitGames.Client.Photon.Hashtable props = p.CustomProperties;
            TryReadPlayerShortProp(props, _propKeyScoreObj, out score);
            TryReadPlayerShortProp(props, _propKeyKillsObj, out kills);
            TryReadPlayerShortProp(props, _propKeyDeathsObj, out deaths);
        }

        private static void ClearReusableScoreboardGroups()
        {
            for (int i = 0; i < _reusableParentGroups.Count; i++)
                _reusableParentGroups[i].Clear();
            _reusableRowParents.Clear();
            _reusableSlotPositions.Clear();
        }

        [HarmonyPatch(typeof(MiniMapGUI), nameof(MiniMapGUI.ShowPlayerList))]
        public static class MiniMapGUI_ShowPlayerList_Patch
        {
            public static void Prefix(bool __0, out bool __state)
            {
                __state = false;
                try
                {
                    if (!VisionConfig.EnableMod.Value ||
                        !VisionConfig.EnableMultiplayerBattleStatSyncFix.Value ||
                        !__0)
                    {
                        return;
                    }

                    MiniMapGUI inst = MiniMapGUI.Instance;
                    if (inst == null || inst.playerList == null || inst.playerList.activeSelf)
                        return;

                    __state = true;
                    SyncAndFlushLocalScoreboardBeforeListRender();

                    // Deactivate old pending-Destroy PlayerListLine rows before native Object.Destroy runs
                    // (Unity defers Destroy until end-of-frame, so includeInactive:true + inactive guard prevents stale rows in Postfix).
                    var oldLines = inst.playerList.GetComponentsInChildren<PlayerListLine>(true);
                    if (oldLines != null)
                    {
                        for (int i = 0; i < oldLines.Length; i++)
                        {
                            PlayerListLine old = oldLines[i];
                            if (old != null && old.gameObject != null)
                                old.gameObject.SetActive(false);
                        }
                    }
                }
                catch { }
            }

            public static void Postfix(bool __0, bool __state)
            {
                try
                {
                    if (!__state || !__0)
                        return;

                    MiniMapGUI inst = MiniMapGUI.Instance;
                    if (inst == null || inst.playerList == null)
                        return;

                    var activeLines = inst.playerList.GetComponentsInChildren<PlayerListLine>(false);
                    if (activeLines == null || activeLines.Length < 2)
                        return;

                    ClearReusableScoreboardGroups();
                    for (int i = 0; i < activeLines.Length; i++)
                    {
                        PlayerListLine line = activeLines[i];
                        if (line == null || line.transform == null)
                            continue;

                        Transform parent = line.transform.parent;
                        RectTransform rect = line.transform.TryCast<RectTransform>() ?? line.GetComponent<RectTransform>();
                        if (parent == null || rect == null)
                            continue;

                        int groupIdx = -1;
                        for (int p = 0; p < _reusableRowParents.Count; p++)
                        {
                            if (_reusableRowParents[p].Pointer == parent.Pointer)
                            {
                                groupIdx = p;
                                break;
                            }
                        }
                        if (groupIdx < 0)
                        {
                            groupIdx = _reusableRowParents.Count;
                            _reusableRowParents.Add(parent);
                            while (_reusableParentGroups.Count <= groupIdx)
                                _reusableParentGroups.Add(new List<PlayerScoreboardRowSortEntry>(16));
                        }

                        GetPlayerScoreboardSortStats(
                            line.assignedPlayer,
                            out short sc,
                            out short kl,
                            out short dt,
                            out int actorNum);

                        _reusableParentGroups[groupIdx].Add(new PlayerScoreboardRowSortEntry
                        {
                            Rect = rect,
                            Score = sc,
                            Kills = kl,
                            Deaths = dt,
                            ActorNumber = actorNum
                        });
                    }

                    for (int g = 0; g < _reusableRowParents.Count; g++)
                    {
                        List<PlayerScoreboardRowSortEntry> group = _reusableParentGroups[g];
                        int count = group.Count;
                        if (count < 2)
                            continue;

                        _reusableSlotPositions.Clear();
                        for (int i = 0; i < count; i++)
                            _reusableSlotPositions.Add(group[i].Rect.anchoredPosition);

                        group.Sort((a, b) =>
                        {
                            int cmp = b.Score.CompareTo(a.Score);
                            if (cmp != 0)
                                return cmp;
                            cmp = b.Kills.CompareTo(a.Kills);
                            if (cmp != 0)
                                return cmp;
                            cmp = a.Deaths.CompareTo(b.Deaths);
                            if (cmp != 0)
                                return cmp;
                            return a.ActorNumber.CompareTo(b.ActorNumber);
                        });

                        for (int i = 0; i < count; i++)
                        {
                            RectTransform r = group[i].Rect;
                            if (r != null)
                            {
                                r.SetAsLastSibling();
                                r.anchoredPosition = _reusableSlotPositions[i];
                            }
                        }
                    }

                    ClearReusableScoreboardGroups();
                }
                catch { }
            }
        }
    }
}
