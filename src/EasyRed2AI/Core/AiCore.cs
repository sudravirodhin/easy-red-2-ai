using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;

namespace EasyRed2HumanAI
{
    /// <summary>
    /// Shared core dependency layer for EasyRed2HumanAI.
    /// Centralizes Photon Co-op ownership checks, global bot RPC rate-limiting,
    /// solid-environment raycasts (ResourcesManager.groundMask), overhead ceiling caching,
    /// deterministic hashing, and dictionary/hashset memory hygiene.
    /// </summary>
    public static class AiCore
    {
        private static int _cachedSolidEnvMask;
        private static int _cachedSmokeOcclusionMask;
        private static IntPtr _cachedWaterInstancePtr;
        private static Renderer _cachedWaterRenderer;
        private static float _nextAllowedBotRpcTime;
        private static float _nextRoomCheckTime;
        private static int _cachedRoomPlayerCount = 1;
        private static IntPtr _lastPublishedRoomPtr;
        private static int _lastHeavyWorkFrame = -1;
        private static int _lastDirectorWorkFrame = -1;
        private static int _lastDebugLogWriteFrame = -1;
        [ThreadStatic]
        private static StringBuilder _sharedLogBuilder;
        private static readonly List<IntPtr> _sharedPruneBuffer = new List<IntPtr>(256);
        private static readonly Dictionary<IntPtr, Vehicle> _turretVehicleCache = new Dictionary<IntPtr, Vehicle>(64);
        private static readonly Dictionary<long, bool> _ceilingCache = new Dictionary<long, bool>(512);
        private static readonly Dictionary<long, bool> _bridgeCellCache = new Dictionary<long, bool>(512);
        private static readonly Dictionary<string, float> _debugLogCooldowns = new Dictionary<string, float>(32);
        private static readonly Dictionary<int, (bool HasMod, float ExpireTime)> _playerModdedCache = new Dictionary<int, (bool HasMod, float ExpireTime)>(16);
        private static readonly List<Vector3> _cachedRemotePlayerPositions = new List<Vector3>(12);
        private static float _nextRemotePlayerPosScanTime;

        // Rolling 15s Frame-Time & Hitch Telemetry
        private static float _currentWindowMaxFrameMs;
        private static float _previousWindowMaxFrameMs;
        private static float _nextFrameWindowRolloverTime;
        public static float RollingMaxFrameMs => Mathf.Max(_currentWindowMaxFrameMs, _previousWindowMaxFrameMs);
        public static int TotalHitchesOver28ms;

        public static int TotalPingGovernorSkippedCycles;

        public static void RecordFrameTiming()
        {
            float now = Time.unscaledTime;
            if (now >= _nextFrameWindowRolloverTime)
            {
                _previousWindowMaxFrameMs = _currentWindowMaxFrameMs;
                _currentWindowMaxFrameMs = 0f;
                _nextFrameWindowRolloverTime = now + 15.0f;
            }

            // Ignore scene load / alt-tab / pause frames so telemetry reflects true gameplay frame pacing
            if (Time.timeSinceLevelLoad < 3.0f || Time.timeScale <= 0.01f)
                return;

            float dt = Time.unscaledDeltaTime;
            if (dt <= 0f || dt > 0.50f)
                return;

            float dtMs = dt * 1000.0f;
            if (dtMs > _currentWindowMaxFrameMs)
                _currentWindowMaxFrameMs = dtMs;
            if (dtMs > 28.0f)
                TotalHitchesOver28ms++;
        }

        public static bool CanEmitDebugLog(string category, float minInterval = 4.0f)
        {
            if (!VisionConfig.DebugLogging.Value || Plugin.Log == null)
                return false;

            if (_lastDebugLogWriteFrame == Time.frameCount && category != "Heartbeat")
                return false;

            float now = Time.unscaledTime;
            if (_debugLogCooldowns.TryGetValue(category, out float nextAllowed) && now < nextAllowed)
                return false;

            return true;
        }

        [InterpolatedStringHandler]
        public ref struct DebugLogInterpolatedStringHandler
        {
            private StringBuilder _builder;

            public DebugLogInterpolatedStringHandler(
                int literalLength,
                int formattedCount,
                string category,
                out bool shouldAppend)
            {
                if (!CanEmitDebugLog(category))
                {
                    _builder = null;
                    shouldAppend = false;
                    return;
                }
                StringBuilder sb = _sharedLogBuilder;
                if (sb == null)
                {
                    sb = new StringBuilder(Math.Max(256, literalLength + formattedCount * 12));
                    _sharedLogBuilder = sb;
                }
                else
                {
                    sb.Clear();
                }
                _builder = sb;
                shouldAppend = true;
            }

            public void AppendLiteral(string s) => _builder?.Append(s);
            public void AppendFormatted<T>(T value) => _builder?.Append(value);
            public void AppendFormatted<T>(T value, string format)
            {
                if (_builder == null)
                    return;
                if (value is IFormattable formattable)
                    _builder.Append(formattable.ToString(format, null));
                else
                    _builder.Append(value);
            }

            internal bool IsActive => _builder != null;
            internal string GetFormattedText() => _builder != null ? _builder.ToString() : string.Empty;
        }

        public static void LogDebug(
            string category,
            [InterpolatedStringHandlerArgument("category")] DebugLogInterpolatedStringHandler message,
            float minInterval = 4.0f)
        {
            if (!message.IsActive || !CanEmitDebugLog(category, minInterval))
                return;

            _debugLogCooldowns[category] = Time.unscaledTime + minInterval;
            _lastDebugLogWriteFrame = Time.frameCount;
            Plugin.Log.LogInfo($"[Diag:{category}] {message.GetFormattedText()}");
        }

        public static void LogDebug(string category, string message, float minInterval = 4.0f)
        {
            if (!CanEmitDebugLog(category, minInterval))
                return;

            _debugLogCooldowns[category] = Time.unscaledTime + minInterval;
            _lastDebugLogWriteFrame = Time.frameCount;
            Plugin.Log.LogInfo($"[Diag:{category}] {message}");
        }

        public static int GetConnectedPlayerCount()
        {
            float now = Time.unscaledTime;
            if (now < _nextRoomCheckTime)
                return _cachedRoomPlayerCount;

            _nextRoomCheckTime = now + 1.0f;
            try
            {
                if (PhotonNetwork.InRoom && PhotonNetwork.CurrentRoom != null)
                {
                    _cachedRoomPlayerCount = Mathf.Max(1, (int)PhotonNetwork.CurrentRoom.PlayerCount);

                    IntPtr roomPtr = PhotonNetwork.CurrentRoom.Pointer;
                    if (roomPtr != IntPtr.Zero && roomPtr != _lastPublishedRoomPtr && PhotonNetwork.LocalPlayer != null)
                    {
                        _lastPublishedRoomPtr = roomPtr;
                        _playerModdedCache.Clear();
                        var props = new ExitGames.Client.Photon.Hashtable();
                        props[(Il2CppSystem.String)"bh_ver"] = (Il2CppSystem.String)Plugin.PluginVersion;
                        PhotonNetwork.LocalPlayer.SetCustomProperties(props);
                    }
                }
                else
                {
                    _cachedRoomPlayerCount = 1;
                    if (_lastPublishedRoomPtr != IntPtr.Zero)
                    {
                        _lastPublishedRoomPtr = IntPtr.Zero;
                        _playerModdedCache.Clear();
                    }
                }
            }
            catch
            {
                _cachedRoomPlayerCount = 1;
            }
            return _cachedRoomPlayerCount;
        }

        public static bool IsMultiplayerWithRemotePlayers()
        {
            return GetConnectedPlayerCount() > 1;
        }

        /// <summary>
        /// Returns true when the current frame time is elevated and bot brain cycles should
        /// yield/coast for a cycle so human player ping and Photon packet delivery never spike.
        /// </summary>
        public static bool ShouldSkipBotCycleForNetworkPing()
        {
            float dt = Time.unscaledDeltaTime;
            int players = GetConnectedPlayerCount();
            // Keep thresholds safely above normal 60Hz VSync jitter (16.67ms-17.4ms) so locked 60 FPS lobbies
            // never false-trigger PingSkip or starve global squad/macro directors.
            float threshold = players >= 6 ? 0.0208f : (players > 1 ? 0.0222f : 0.0265f);
            if (dt > threshold)
            {
                TotalPingGovernorSkippedCycles++;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Marks the current frame as having executed heavy spatial/octree/snapshot work
        /// (e.g., vanilla <c>Squad.SquadLeaderRoutine</c> or a full world snapshot rebuild)
        /// so secondary octree sweeps shift to subsequent frames.
        /// </summary>
        public static void NotifyHeavyWorkThisFrame()
        {
            _lastHeavyWorkFrame = Time.frameCount;
        }

        public static void NotifyDirectorWorkThisFrame()
        {
            int frame = Time.frameCount;
            _lastHeavyWorkFrame = frame;
            _lastDirectorWorkFrame = frame;
        }

        public static bool IsHeavyWorkFrame()
        {
            return _lastHeavyWorkFrame == Time.frameCount;
        }

        /// <summary>
        /// Ensures at most ONE global mod director pass (Macro Director, TeamNet GOAP Coordinator,
        /// Snapshot Rebuild, or Dictionary Pruning) executes on any single frame, without allowing
        /// frequent vanilla <c>Squad.SquadLeaderRoutine</c> coroutines (80+/s across 20 squads) to starve directors.
        /// </summary>
        public static bool TryClaimHeavyFrameWorkSlot(bool allowOnModerateFrame = false)
        {
            if (!allowOnModerateFrame && ShouldSkipBotCycleForNetworkPing())
                return false;

            int frame = Time.frameCount;
            if (_lastDirectorWorkFrame == frame)
                return false;

            _lastDirectorWorkFrame = frame;
            _lastHeavyWorkFrame = frame;
            return true;
        }

        public static bool CanBotMoveAutonomously(Soldier s)
        {
            if (s == null || s.Pointer == IntPtr.Zero)
                return false;

            AiParams p = s.Params;
            return p == null || p.canMove;
        }

        public static bool CanBotChangePoseAutonomously(Soldier s)
        {
            if (s == null || s.Pointer == IntPtr.Zero)
                return false;

            AiParams p = s.Params;
            return p == null || (p.changePoseAutomatically && !p.forcedPose.HasValue);
        }

        /// <summary>
        /// Universal game-mode objective classifier supporting Classic Invasion (<c>MissionTaskConquerArea</c>,
        /// <c>MissionTaskDestroyVehicles</c>, <c>MissionTaskClearArea</c>), <c>MissionTaskRetreat</c>,
        /// <c>MissionTaskPushFrontline</c>, Dynamic Conquest (<c>gamemode_conquest.lua</c>), Wave Defense
        /// (<c>gamemode_defend.lua</c>), Retreat Finales (<c>phase_4.lua</c>), and Multi-Phase Lua Operations.
        /// </summary>
        public static bool ClassifyMissionObjectiveForFaction(
            MissionObjective obj,
            string faction,
            BattleData bd,
            out bool isHeldByFaction,
            out bool canAttractFaction,
            out float contestOrProgress01)
        {
            isHeldByFaction = false;
            canAttractFaction = false;
            contestOrProgress01 = 0f;

            if (obj == null || obj.Pointer == IntPtr.Zero || string.IsNullOrEmpty(faction))
                return false;

            MissionTask task = obj.missionTask;
            if (task == null || task.Pointer == IntPtr.Zero || !task.IsTaskEnabled())
                return false;

            MissionObjectiveScripted sObj = obj.TryCast<MissionObjectiveScripted>();
            if (sObj != null)
            {
                if (!sObj.IsSettedUp() || sObj.IsSecured())
                    return false;

                // Dormant / staged / completed Lua objectives set both attractors false; ignore completely
                if (!sObj.canAttractAllies && !sObj.canAttractAxis)
                    return false;

                bool isInvader = bd != null && bd.IsInvaderFaction(faction);
                bool canAttract = sObj.CanAttractFaction(faction);
                MissionTaskScripted mTask = task.TryCast<MissionTaskScripted>();
                float rawPct = Mathf.Clamp01(task.GetTaskCompletationPercentage());

                // MissionTaskScripted stores icon/icon_defenders as (luaIcon + 1): 1 = Attack (#attack/#chase), 2 = Defend (#defend/#retreat)
                byte effIcon = mTask != null
                    ? ((!isInvader && mTask.icon_defenders > 0) ? mTask.icon_defenders : mTask.icon)
                    : (byte)0;

                // Retreat Finales (SHARED/phase_4.lua): Defenders get icon_defenders = 2 with "#retreat" text and canAttract = true.
                // Treat as an active evacuation rally objective (not a rear garrison) so all Defender squads withdraw into the zone!
                if (!isInvader && canAttract && mTask != null && mTask.customTextDefenders == "#retreat")
                {
                    canAttractFaction = true;
                    return true;
                }

                string sSec = sObj.securedFaction;
                bool ownedByFaction = (effIcon == 2) ||
                                      (!canAttract && !string.IsNullOrEmpty(sSec) && string.Equals(sSec, faction, StringComparison.OrdinalIgnoreCase));

                if (ownedByFaction)
                {
                    isHeldByFaction = true;
                    canAttractFaction = canAttract;
                    // Conquest & Invader-captured Wave Defense flags set canAttract = false for the owner and rest at 0.99f when uncontested,
                    // draining toward 0.0f when enemies capture! Defender-held Wave Defense / Lua Phase flags keep canAttract = true and rest at 0.0f.
                    if (!canAttract)
                        contestOrProgress01 = (rawPct > 0.01f && rawPct < 0.95f) ? Mathf.Clamp01(1.0f - rawPct) : 0f;
                    else
                        contestOrProgress01 = rawPct > 0.05f ? rawPct : 0f;
                    return true;
                }

                if (canAttract)
                {
                    canAttractFaction = true;
                    // On enemy-owned Conquest flags, rawPct starts at 0.99f under enemy ownership before neutralizing through 0.0f;
                    // only report positive friendly capture momentum once neutralized or under friendly securedFaction!
                    if (!string.IsNullOrEmpty(sSec) && !string.Equals(sSec, faction, StringComparison.OrdinalIgnoreCase))
                        contestOrProgress01 = rawPct < 0.65f ? (1.0f - rawPct) * 0.45f : 0f;
                    else if (mTask != null && mTask.icon == 2 && !isInvader && rawPct >= 0.95f)
                        contestOrProgress01 = 0f; // Lost Wave Defense flag resting at 0.99f before counter-capture begins
                    else
                        contestOrProgress01 = rawPct;
                    return true;
                }

                return false;
            }

            float capPct = Mathf.Clamp01(task.GetTaskCompletationPercentage());
            if (task.TryCast<MissionTaskPushFrontline>() != null)
            {
                string secFaction = obj.GetSecuredFaction();
                if (!string.IsNullOrEmpty(secFaction) && secFaction == faction)
                {
                    isHeldByFaction = true;
                    contestOrProgress01 = capPct > 0.05f ? capPct : 0f;
                    return true;
                }
                if (obj.CanAttractFaction(faction))
                {
                    canAttractFaction = true;
                    contestOrProgress01 = capPct;
                    return true;
                }
                return false;
            }

            if (obj.IsSecured())
                return false;

            if (task.TryCast<MissionTaskRetreat>() != null)
            {
                if (obj.CanAttractFaction(faction))
                {
                    canAttractFaction = true;
                    return true;
                }
                return false;
            }

            string invFaction = obj.GetSecuredFaction();
            if (!string.IsNullOrEmpty(invFaction) && invFaction != faction)
            {
                isHeldByFaction = true;
                canAttractFaction = obj.CanAttractFaction(faction);
                contestOrProgress01 = capPct > 0.05f ? capPct : 0f;
                return true;
            }

            if (obj.CanAttractFaction(faction))
            {
                canAttractFaction = true;
                contestOrProgress01 = capPct;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Evaluates a squad's active <see cref="SquadTask"/> across all vanilla and scripted/dynamic game modes
        /// so GOAP never misinterprets resting <c>0.99f</c> Conquest/WaveDefense bars as enemy back-caps.
        /// </summary>
        public static void EvaluateSquadTaskForFaction(
            SquadTask curTask,
            string faction,
            BattleData bd,
            bool isGarrisonAssigned,
            out bool isDefending,
            out float progressOrContest01)
        {
            isDefending = isGarrisonAssigned;
            progressOrContest01 = 0f;
            if (curTask == null || curTask.Pointer == IntPtr.Zero || string.IsNullOrEmpty(faction))
                return;

            var scriptedList = MissionObjectiveScripted.scriptedMissionObjectives;
            if (scriptedList != null)
            {
                IntPtr taskPtr = curTask.Pointer;
                int sCount = scriptedList.Count;
                for (int i = 0; i < sCount; i++)
                {
                    MissionObjectiveScripted sObj = scriptedList[i];
                    if (sObj != null && sObj.missionTask != null && sObj.missionTask.Pointer == taskPtr)
                    {
                        if (ClassifyMissionObjectiveForFaction(sObj, faction, bd, out bool held, out _, out float prog))
                        {
                            isDefending = isGarrisonAssigned || held;
                            progressOrContest01 = prog;
                        }
                        return;
                    }
                }
            }

            float rawPct = Mathf.Clamp01(curTask.GetTaskCompletationPercentage());
            if (curTask.TryCast<MissionTaskPushFrontline>() != null)
            {
                isDefending = isGarrisonAssigned;
                progressOrContest01 = rawPct;
                return;
            }

            if (curTask.TryCast<MissionTaskRetreat>() != null)
            {
                isDefending = false;
                progressOrContest01 = 1.0f;
                return;
            }

            bool isInvader = bd != null && bd.IsInvaderFaction(faction);
            isDefending = isGarrisonAssigned || !isInvader;
            progressOrContest01 = rawPct;
        }

        /// <summary>
        /// Validates that an IL2CPP <see cref="Spottable"/> interface wrapper still points to an
        /// alive, non-destroyed Unity <see cref="Component"/> with a valid native transform.
        /// Prevents native NullReferenceExceptions in <c>Soldier.GetCenterOfUnit</c> / <c>TurretAI.CanSee</c>.
        /// </summary>
        public static bool IsValidAliveSpottable(Spottable s)
        {
            if (s == null || s.Pointer == IntPtr.Zero)
                return false;

            try
            {
                Component comp = s.TryCast<Component>();
                if (comp == null || comp.transform == null)
                    return false;

                Creature c = s.TryCast<Creature>();
                if (c != null && !c.IsAlive)
                    return false;

                return true;
            }
            catch
            {
                return false;
            }
        }

        public static bool TryGetSpottableCenter(Spottable s, out Vector3 center)
        {
            if (!IsValidAliveSpottable(s))
            {
                center = Vector3.zero;
                return false;
            }

            try
            {
                center = s.GetCenterOfUnit();
                return true;
            }
            catch
            {
                center = Vector3.zero;
                return false;
            }
        }

        public static bool IsMasterClient()
        {
            try
            {
                return !PhotonNetwork.InRoom || PhotonNetwork.IsMasterClient;
            }
            catch
            {
                return true;
            }
        }

        public static bool IsLocallyOwnedBot(Soldier s)
        {
            if (s == null || s.Pointer == IntPtr.Zero || !s.IsAlive || s.IsPlayer())
                return false;

            SyncSoldier syncher = s.soldierSyncher;
            if (syncher != null && (!syncher.OwnSyncher || syncher.controlled_by_player))
                return false;

            return true;
        }

        public static bool IsLocallyOwnedSoldier(Soldier s)
        {
            if (s == null || s.Pointer == IntPtr.Zero || !s.IsAlive)
                return false;

            SyncSoldier syncher = s.soldierSyncher;
            return syncher == null || syncher.OwnSyncher;
        }

        public static bool IsLocallyOwnedVehicleSyncher(Vehicle v)
        {
            if (v == null || v.Pointer == IntPtr.Zero)
                return false;

            SyncVehicle syncher = v.vehicleSyncher;
            return syncher == null || syncher.OwnSyncher;
        }

        public static bool IsLocallyOwnedVehicle(Vehicle v)
        {
            if (v == null || v.Pointer == IntPtr.Zero)
                return false;

            if (v.HasDriverAlive)
                return v.OwnDriverOrVehicle();

            SyncVehicle syncher = v.vehicleSyncher;
            return syncher == null || syncher.OwnSyncher;
        }

        /// <summary>
        /// Safely returns the active <see cref="BattlePhase"/> without throwing when
        /// <c>BattleManager.phases</c> is null/empty (e.g., <c>sp_ME_...</c> Mission Editor V2 / reworked streaming battles).
        /// </summary>
        public static BattlePhase TryGetCurrentBattlePhase(BattleManager bm)
        {
            if (bm == null || bm.Pointer == IntPtr.Zero)
                return null;

            var phases = bm.phases;
            if (phases == null)
                return null;

            int idx = bm.currentPhaseNum;
            if ((uint)idx >= (uint)phases.Length)
                return null;

            return phases[idx];
        }

        /// <summary>
        /// Resolves the horizontal direction toward enemy spawn territory on both classic <see cref="BattlePhase"/>
        /// maps and <c>sp_ME_...</c> Mission Editor V2 streaming maps (falling back to <c>SpawnManager.activeSpawns</c>).
        /// </summary>
        public static bool TryGetEnemySpawnDirection(string myFaction, Vector3 fromPos, out Vector3 dir)
        {
            dir = Vector3.zero;
            if (string.IsNullOrEmpty(myFaction))
                return false;

            try
            {
                BattlePhase phase = TryGetCurrentBattlePhase(BattleManager.instance);
                if (phase != null && phase.EnemyFactionHasSpawn(myFaction))
                {
                    Vector3 toEnemy = phase.GetOppositeFactionTerritoryPosition(myFaction) - fromPos;
                    toEnemy.y = 0f;
                    if (toEnemy.sqrMagnitude > 1.0f)
                    {
                        dir = toEnemy.normalized;
                        return true;
                    }
                }

                var spawns = SpawnManager.activeSpawns;
                if (spawns != null && spawns.Count > 0)
                {
                    Vector3 sum = Vector3.zero;
                    int count = 0;
                    for (int i = 0; i < spawns.Count; i++)
                    {
                        SpawnManager sp = spawns[i];
                        if (sp == null || sp.Pointer == IntPtr.Zero || sp.gameObject == null || !sp.gameObject.activeInHierarchy)
                            continue;

                        string spFact = sp.GetFaction();
                        if (string.IsNullOrEmpty(spFact) || spFact == myFaction || spFact == "Civilian")
                            continue;

                        sum += sp.GetSpawnPosition();
                        count++;
                    }

                    if (count > 0)
                    {
                        Vector3 toEnemy = (sum / count) - fromPos;
                        toEnemy.y = 0f;
                        if (toEnemy.sqrMagnitude > 1.0f)
                        {
                            dir = toEnemy.normalized;
                            return true;
                        }
                    }
                }
            }
            catch { }

            return false;
        }

        /// <summary>
        /// Resolves the nearest friendly spawn position on both classic <see cref="BattlePhase"/> maps
        /// and <c>sp_ME_...</c> Mission Editor V2 streaming maps (via <c>SpawnManager.activeSpawns</c>).
        /// </summary>
        public static bool TryGetNearestFriendlySpawnPosition(string myFaction, Vector3 fromPos, out Vector3 spawnPos)
        {
            spawnPos = Vector3.zero;
            if (string.IsNullOrEmpty(myFaction))
                return false;

            try
            {
                BattlePhase phase = TryGetCurrentBattlePhase(BattleManager.instance);
                if (phase != null)
                {
                    SpawnManager nearest = phase.GetNearestSpawn(myFaction, fromPos);
                    if (nearest != null && nearest.Pointer != IntPtr.Zero)
                    {
                        spawnPos = nearest.GetSpawnPosition();
                        return true;
                    }
                }

                var spawns = SpawnManager.activeSpawns;
                if (spawns != null && spawns.Count > 0)
                {
                    float bestSq = float.MaxValue;
                    bool found = false;
                    for (int i = 0; i < spawns.Count; i++)
                    {
                        SpawnManager sp = spawns[i];
                        if (sp == null || sp.Pointer == IntPtr.Zero || sp.gameObject == null || !sp.gameObject.activeInHierarchy)
                            continue;

                        if (sp.GetFaction() != myFaction)
                            continue;

                        Vector3 cand = sp.GetSpawnPosition();
                        float dSq = (cand - fromPos).sqrMagnitude;
                        if (dSq < bestSq)
                        {
                            bestSq = dSq;
                            spawnPos = cand;
                            found = true;
                        }
                    }
                    return found;
                }
            }
            catch { }

            return false;
        }

        /// <summary>
        /// Checks whether the human player controlling <paramref name="s"/> has published the
        /// <c>"bh_ver"</c> Photon custom property (cached for 5s per ActorNumber).
        /// Returns true for local players/bots or on error so Host-assist only activates for confirmed unmodded remote players.
        /// </summary>
        public static bool IsPlayerRunningBattleHardened(Soldier s)
        {
            if (s == null || s.Pointer == IntPtr.Zero)
                return true;

            try
            {
                SyncSoldier syncher = s.soldierSyncher;
                if (syncher == null || syncher.OwnSyncher)
                    return true;

                Photon.Realtime.Player controller = syncher.Controller ?? syncher.Owner;
                if (controller == null)
                    return true;

                int actorNum = controller.ActorNumber;
                float now = Time.unscaledTime;
                if (_playerModdedCache.TryGetValue(actorNum, out var cached) && now < cached.ExpireTime)
                    return cached.HasMod;

                bool hasMod = false;
                var props = controller.CustomProperties;
                if (props != null && props.ContainsKey((Il2CppSystem.String)"bh_ver"))
                    hasMod = true;

                if (_playerModdedCache.Count > 32)
                    _playerModdedCache.Clear();
                _playerModdedCache[actorNum] = (hasMod, now + 5.0f);
                return hasMod;
            }
            catch
            {
                return true;
            }
        }

        public static bool TryConsumeBotRpcBudget(float minInterval = 0.35f)
        {
            if (ShouldSkipBotCycleForNetworkPing())
                return false;

            float now = Time.time;
            if (_nextAllowedBotRpcTime > now + 2.5f)
            {
                _nextAllowedBotRpcTime = 0f;
            }

            if (now < _nextAllowedBotRpcTime)
                return false;

            int players = GetConnectedPlayerCount();
            if (players > 1)
            {
                minInterval *= (1.0f + Mathf.Min(players - 1, 12) * 0.25f);
            }

            _nextAllowedBotRpcTime = now + Mathf.Clamp(minInterval, 0.05f, 3.0f);
            return true;
        }

        public static Vehicle GetCachedTurretVehicle(Turret turret)
        {
            if (turret == null || turret.Pointer == IntPtr.Zero)
                return null;

            IntPtr ptr = turret.Pointer;
            if (_turretVehicleCache.TryGetValue(ptr, out Vehicle cachedVeh))
            {
                if (cachedVeh != null && cachedVeh.Pointer != IntPtr.Zero)
                    return cachedVeh;
            }

            Vehicle resolved = turret.GetComponentInParent<Vehicle>();
            _turretVehicleCache[ptr] = resolved;
            return resolved;
        }

        public static int GetSolidEnvironmentMask()
        {
            if (_cachedSolidEnvMask != 0)
                return _cachedSolidEnvMask;

            try
            {
                int gm = ResourcesManager.groundMask.value;
                if (gm != 0)
                {
                    _cachedSolidEnvMask = gm;
                    return gm;
                }
            }
            catch { }

            int terrainLayer = LayerMask.NameToLayer("Terrain");
            _cachedSolidEnvMask = terrainLayer >= 0 ? ((1 << 0) | (1 << terrainLayer)) : (1 << 0);
            return _cachedSolidEnvMask;
        }

        public static int GetSmokeOcclusionMask()
        {
            if (_cachedSmokeOcclusionMask != 0)
                return _cachedSmokeOcclusionMask;

            try
            {
                int vm = ResourcesManager.visionMask.value;
                int bm = ResourcesManager.bulletMask.value;
                int diff = vm & ~bm;
                if (diff != 0)
                {
                    _cachedSmokeOcclusionMask = diff;
                    return diff;
                }
            }
            catch { }

            int tfxLayer = LayerMask.NameToLayer("TransparentFX");
            _cachedSmokeOcclusionMask = tfxLayer >= 0 ? (1 << tfxLayer) : (1 << 1);
            return _cachedSmokeOcclusionMask;
        }

        public static bool TryGetActiveCameraPosition(out Vector3 camPos)
        {
            try
            {
                Camera cam = ResourcesManager.mainCamera;
                if (cam == null || cam.Pointer == IntPtr.Zero || !cam.isActiveAndEnabled)
                    cam = Camera.main;

                if (cam != null && cam.Pointer != IntPtr.Zero && cam.transform != null)
                {
                    camPos = cam.transform.position;
                    return true;
                }
            }
            catch { }

            camPos = Vector3.zero;
            return false;
        }

        /// <summary>
        /// Checks whether an impact point is submerged below or across an active <see cref="FollowingWater"/>
        /// or Layer 4 (<c>Water</c>) surface, or occluded from <paramref name="camPos"/> by a tactical smoke volume
        /// (<c>visionMask &amp; ~bulletMask</c>).
        /// </summary>
        public static bool IsPointOccludedByWaterOrSmoke(Vector3 camPos, Vector3 impactPos)
        {
            try
            {
                FollowingWater water = FollowingWater.instance;
                if (water != null && water.Pointer != IntPtr.Zero && water.isActiveAndEnabled)
                {
                    if (water.Pointer != _cachedWaterInstancePtr)
                    {
                        _cachedWaterInstancePtr = water.Pointer;
                        _cachedWaterRenderer = FollowingWater.GetRenderer();
                    }

                    if (_cachedWaterRenderer != null &&
                        _cachedWaterRenderer.Pointer != IntPtr.Zero &&
                        _cachedWaterRenderer.enabled)
                    {
                        float waterY = FollowingWater.WaterLevel;
                        // Submerged flesh hit or sightline crossing the rendered water plane
                        if (impactPos.y < waterY - 0.03f || (camPos.y - waterY) * (impactPos.y - waterY) < 0f)
                            return true;
                    }
                }
            }
            catch { }

            int smokeMask = GetSmokeOcclusionMask();
            int lineMask = smokeMask | (1 << 4); // Smoke volumes (TransparentFX) + local Water colliders (Layer 4)
            float distSq = (impactPos - camPos).sqrMagnitude;
            if (distSq > 0.25f && Physics.Linecast(camPos, impactPos, lineMask))
                return true;

            // PhysX Linecast starting inside a SphereCollider skips its own shell; CheckSphere catches inside-smoke hits
            if (distSq > 4.0f && Physics.CheckSphere(impactPos, 0.20f, smokeMask))
                return true;

            if (distSq > 6.25f && Physics.CheckSphere(camPos, 0.15f, smokeMask))
                return true;

            return false;
        }

        public static bool RaycastEnvironment(Vector3 origin, Vector3 dir, float maxDist, Transform ignoreRoot, out RaycastHit envHit)
        {
            // ResourcesManager.groundMask (Default | Terrain) hits only solid environment geometry (walls, buildings,
            // sandbags, terrain) and ignores soldier BodyPart colliders, ragdolls, items, foliage, and triggers.
            // This eliminates multi-hop raycast loops and IL2CPP GetComponentInParent<Creature>() hierarchy walks!
            return Physics.Raycast(origin, dir, out envHit, maxDist, GetSolidEnvironmentMask(), QueryTriggerInteraction.Ignore);
        }

        public static bool IsDirectionBlockedByEnvironment(Vector3 origin, Vector3 dir, float dist, Transform ignoreRoot)
        {
            return Physics.Raycast(origin, dir, dist, GetSolidEnvironmentMask(), QueryTriggerInteraction.Ignore);
        }

        public static bool HasLowCeilingOverhead(Vector3 feetPos, float maxHeight = 2.6f)
        {
            // Quantize to 2m x 1m x 2m spatial grid cell so indoor ceiling checks are O(1) after first probe
            int gx = Mathf.RoundToInt(feetPos.x * 0.5f) & 0xFFFF;
            int gy = Mathf.RoundToInt(feetPos.y) & 0xFFFF;
            int gz = Mathf.RoundToInt(feetPos.z * 0.5f) & 0xFFFF;
            long cellKey = ((long)gx << 32) | ((long)gy << 16) | (uint)gz;

            if (_ceilingCache.TryGetValue(cellKey, out bool cachedLowCeiling))
                return cachedLowCeiling;

            Vector3 origin = feetPos + Vector3.up * 0.65f;
            bool hasCeiling = Physics.Raycast(origin, Vector3.up, maxHeight, GetSolidEnvironmentMask(), QueryTriggerInteraction.Ignore);
            if (_ceilingCache.Count > 2048)
                _ceilingCache.Clear();
            _ceilingCache[cellKey] = hasCeiling;
            return hasCeiling;
        }

        public static bool IsOnBridgeOrNarrowCrossing(Vector3 feetPos)
        {
            // Quantize to 2m x 1m x 2m spatial grid cell so bridge/crossing checks are O(1) after first probe
            int gx = Mathf.RoundToInt(feetPos.x * 0.5f) & 0xFFFF;
            int gy = Mathf.RoundToInt(feetPos.y) & 0xFFFF;
            int gz = Mathf.RoundToInt(feetPos.z * 0.5f) & 0xFFFF;
            long cellKey = ((long)gx << 32) | ((long)gy << 16) | (uint)gz;

            if (_bridgeCellCache.TryGetValue(cellKey, out bool cachedBridge))
                return cachedBridge;

            bool isBridge = false;
            if (!HasLowCeilingOverhead(feetPos, 3.8f))
            {
                int mask = GetSolidEnvironmentMask();
                Vector3 baseOrigin = feetPos + Vector3.up * 0.85f;
                // A 2.65m downward probe from +0.85m means any ground > 1.80m below feetPos.y is a drop-off
                const float maxDropProbe = 2.65f;

                bool dropPosX = !Physics.Raycast(baseOrigin + new Vector3(3.8f, 0f, 0f), Vector3.down, maxDropProbe, mask, QueryTriggerInteraction.Ignore) ||
                                !Physics.Raycast(baseOrigin + new Vector3(6.2f, 0f, 0f), Vector3.down, maxDropProbe, mask, QueryTriggerInteraction.Ignore);
                bool dropNegX = false;
                if (dropPosX)
                {
                    dropNegX = !Physics.Raycast(baseOrigin + new Vector3(-3.8f, 0f, 0f), Vector3.down, maxDropProbe, mask, QueryTriggerInteraction.Ignore) ||
                               !Physics.Raycast(baseOrigin + new Vector3(-6.2f, 0f, 0f), Vector3.down, maxDropProbe, mask, QueryTriggerInteraction.Ignore);
                }

                if (dropPosX && dropNegX)
                {
                    isBridge = true;
                }
                else
                {
                    bool dropPosZ = !Physics.Raycast(baseOrigin + new Vector3(0f, 0f, 3.8f), Vector3.down, maxDropProbe, mask, QueryTriggerInteraction.Ignore) ||
                                    !Physics.Raycast(baseOrigin + new Vector3(0f, 0f, 6.2f), Vector3.down, maxDropProbe, mask, QueryTriggerInteraction.Ignore);
                    if (dropPosZ)
                    {
                        bool dropNegZ = !Physics.Raycast(baseOrigin + new Vector3(0f, 0f, -3.8f), Vector3.down, maxDropProbe, mask, QueryTriggerInteraction.Ignore) ||
                                        !Physics.Raycast(baseOrigin + new Vector3(0f, 0f, -6.2f), Vector3.down, maxDropProbe, mask, QueryTriggerInteraction.Ignore);
                        if (dropNegZ)
                            isBridge = true;
                    }
                }
            }

            if (_bridgeCellCache.Count > 2048)
                _bridgeCellCache.Clear();
            _bridgeCellCache[cellKey] = isBridge;
            return isBridge;
        }

        public static float GetBarrelHeightForPose(SoldierPose pose)
        {
            switch (pose)
            {
                case SoldierPose.Prone:
                    return 0.25f;
                case SoldierPose.Crouch:
                    return 0.68f;
                default:
                    return 1.24f;
            }
        }

        /// <summary>
        /// Extends a soldier's alertness window (<see cref="Soldier.SetAlertedFor"/>) without forcing
        /// <c>pointGunEndTime = Time.time + 3.0f</c> when the soldier does not currently have an
        /// engageable visible target (<c>HasEnemyAndCanFireAtHim</c>). This prevents stationary
        /// defenders behind cover/walls/windows from raising their rifle into the shooting posture
        /// every 250ms and oscillating against <c>CheckTowardWallTps(1.0f)</c>.
        /// </summary>
        public static void RefreshAlertnessWithoutRaisingGun(Soldier soldier, float duration)
        {
            if (soldier == null)
                return;

            float now = Time.time;
            float prevPointGunEnd = soldier.pointGunEndTime;
            soldier.SetAlertedFor(duration);

            SoldierAI ai = soldier.aiController;
            bool canFireAtVisibleEnemy = ai != null && ai.HasEnemyAndCanFireAtHim;
            if (!canFireAtVisibleEnemy)
            {
                soldier.pointGunEndTime = soldier.isTowardWallTps
                    ? Mathf.Min(prevPointGunEnd, now)
                    : prevPointGunEnd;
            }
        }

        /// <summary>
        /// Rotates a soldier's body horizontally toward <paramref name="flatDir"/> via
        /// <see cref="Soldier.RotateToward"/> while preventing native <c>RotateToward</c>
        /// (<c>0x18076060C</c>) from forcing <c>pointGunEndTime = Time.time + 1.5f</c> (which raises
        /// the rifle into the shooting posture) or twisting <c>lookRotation.leftRight</c> against
        /// <c>LookForward</c> when the soldier is not actively aiming at an enemy.
        /// </summary>
        public static void RotateBodyWithoutRaisingGun(Soldier soldier, Vector3 flatDir, float deltaTime)
        {
            if (soldier == null || deltaTime <= 0f)
                return;

            flatDir.y = 0f;
            if (flatDir.sqrMagnitude < 0.001f)
                return;

            float now = Time.time;
            float prevPointGunEnd = soldier.pointGunEndTime;
            SoldierAI ai = soldier.aiController;
            bool isActivelyAiming = now < prevPointGunEnd &&
                                    !soldier.isTowardWallTps &&
                                    ai != null &&
                                    ai.HasEnemyAndCanFireAtHim;

            var lookRot = soldier.lookRotation;
            float prevLeftRight = lookRot != null ? lookRot.leftRight : 0f;
            float prevUpDown = lookRot != null ? lookRot.upDown : 0f;

            soldier.RotateToward(flatDir.normalized, deltaTime);

            if (!isActivelyAiming)
            {
                soldier.pointGunEndTime = soldier.isTowardWallTps
                    ? Mathf.Min(prevPointGunEnd, now)
                    : prevPointGunEnd;

                if (lookRot != null)
                {
                    lookRot.leftRight = prevLeftRight;
                    lookRot.upDown = prevUpDown;
                }
            }
        }

        /// <summary>
        /// Moves an AI bot smoothly toward <paramref name="targetPos"/> while stopping at
        /// <paramref name="standoffDist"/> meters so bots never body-hug squadmates/corpses.
        /// Uses multi-node <c>CPathFinding</c> (<see cref="SoldierAI.MoveDestination"/>) for long
        /// distances (&gt; 16m) or obstructed lines of walk, and uses an overlap-safe duration on
        /// <see cref="SoldierAI.MoveDirectlyToward"/> for short unobstructed hops so
        /// <c>moveDirectlyToward_end</c> never expires mid-stride into vanilla's 1-node
        /// <c>MoveFPSOptimized(0.5f)</c> slow-walk fallback.
        /// </summary>
        public static void MoveBotTowardPosition(
            SoldierAI ai,
            Soldier soldier,
            Vector3 targetPos,
            float standoffDist = 0f,
            float minDirectDuration = 2.8f)
        {
            if (ai == null || soldier == null || soldier.transform == null)
                return;

            Vector3 myPos = soldier.transform.position;
            Vector3 flatDelta = targetPos - myPos;
            flatDelta.y = 0f;
            float flatDistSq = flatDelta.sqrMagnitude;
            float stopThreshold = Mathf.Max(0f, standoffDist) + 0.25f;
            if (flatDistSq <= stopThreshold * stopThreshold)
                return;

            float flatDist = Mathf.Sqrt(flatDistSq);
            Vector3 dir = flatDelta / flatDist;
            Vector3 dest = standoffDist > 0.05f ? (targetPos - dir * standoffDist) : targetPos;
            dest.y = targetPos.y;
            float moveDist = Mathf.Max(0.25f, flatDist - standoffDist);

            // Long runs (> 16m) or wall-obstructed paths must use multi-node CPathFinding (NodesCount >= 2)
            // rather than a 1-node straight-line MoveDirectlyToward segment.
            if (moveDist > 16.0f ||
                IsDirectionBlockedByEnvironment(myPos + Vector3.up * 0.85f, dir, Mathf.Min(moveDist, 7.5f), soldier.transform.root))
            {
                if (ai.moveDirectlyToward_end > Time.time)
                    ai.moveDirectlyToward_end = 0f;

                if ((ai.MoveDestination - dest).sqrMagnitude > 0.64f)
                    ai.MoveDestination = dest;

                return;
            }

            float duration = Mathf.Clamp(moveDist / 2.6f + 1.45f, minDirectDuration, 5.5f);
            ai.MoveDirectlyToward(dest, duration);
        }

        /// <summary>
        /// Computes true horizontal (XZ-plane) unsigned angle in degrees between <paramref name="soldier"/>'s
        /// forward vector and <paramref name="direction"/>. Replaces vanilla <c>Soldier.AngleToDirection</c>
        /// (<c>0x18073D4E0</c>), which accidentally casts <see cref="Vector3"/> to <see cref="Vector2"/>
        /// and drops the North/South (<c>Z</c>) component completely!
        /// </summary>
        public static float HorizontalAngleToDirection(Soldier soldier, Vector3 direction)
        {
            if (soldier == null || soldier.Pointer == IntPtr.Zero || soldier.transform == null)
                return 180f;

            Vector3 fwd = soldier.transform.forward;
            float fx = fwd.x, fz = fwd.z;
            float dx = direction.x, dz = direction.z;
            float lenSq = (fx * fx + fz * fz) * (dx * dx + dz * dz);
            if (lenSq < 1e-8f)
                return 0f;

            float cos = Mathf.Clamp((fx * dx + fz * dz) / Mathf.Sqrt(lenSq), -1f, 1f);
            return Mathf.Acos(cos) * Mathf.Rad2Deg;
        }

        public static float HorizontalSignedAngleToDirection(Soldier soldier, Vector3 direction)
        {
            if (soldier == null || soldier.Pointer == IntPtr.Zero || soldier.transform == null)
                return 0f;

            Vector3 fwd = soldier.transform.forward;
            float fx = fwd.x, fz = fwd.z;
            float dx = direction.x, dz = direction.z;
            float lenSq = (fx * fx + fz * fz) * (dx * dx + dz * dz);
            if (lenSq < 1e-8f)
                return 0f;

            float cos = Mathf.Clamp((fx * dx + fz * dz) / Mathf.Sqrt(lenSq), -1f, 1f);
            float angle = Mathf.Acos(cos) * Mathf.Rad2Deg;
            // Match vanilla SignedAngleToDirection's intended 2D (x, z) cross sign: fx*dz - dx*fz
            return (fx * dz - dx * fz) < 0f ? -angle : angle;
        }

        /// <summary>
        /// Returns true if any remote multiplayer human client's controlled soldier is within
        /// <paramref name="maxRadius"/> of <paramref name="worldPos"/> (cached 2 Hz scan, O(1) per call).
        /// Used so <c>Soldier.SyncSay</c> Photon RPCs are only sent when a remote player can actually hear the bot.
        /// </summary>
        public static bool IsNearAnyRemotePlayer(Vector3 worldPos, float maxRadius)
        {
            if (!IsMultiplayerWithRemotePlayers())
                return false;

            float now = Time.unscaledTime;
            if (now >= _nextRemotePlayerPosScanTime)
            {
                _nextRemotePlayerPosScanTime = now + 0.50f;
                _cachedRemotePlayerPositions.Clear();
                try
                {
                    var list = Creature.allCreatures;
                    if (list != null)
                    {
                        int count = list.Count;
                        for (int i = 0; i < count; i++)
                        {
                            Creature c = list[i];
                            if (c == null || !c.IsAlive)
                                continue;

                            Soldier s = c.TryCast<Soldier>();
                            if (s == null || s.transform == null)
                                continue;

                            SyncSoldier syncher = s.soldierSyncher;
                            if (syncher != null && !syncher.OwnSyncher && (syncher.controlled_by_player || s.IsPlayer()))
                            {
                                _cachedRemotePlayerPositions.Add(s.transform.position);
                            }
                        }
                    }
                }
                catch { }
            }

            float maxSq = maxRadius * maxRadius;
            for (int i = 0; i < _cachedRemotePlayerPositions.Count; i++)
            {
                if ((_cachedRemotePlayerPositions[i] - worldPos).sqrMagnitude <= maxSq)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Zero-allocation danger-close blast + dispersion safety check for handheld knee mortars and static/on-map
        /// indirect-fire artillery/mortars. Checks the local player, remote human players, and all friendly squads.
        /// </summary>
        public static bool IsIndirectFireImpactDangerClose(
            string myFaction,
            Vector3 impactPos,
            IntPtr shooterPtr = default,
            float friendlySafeRadius = 29.0f,
            float playerSafeRadius = 35.0f)
        {
            if (string.IsNullOrEmpty(myFaction))
                return false;

            float playerSafeSq = playerSafeRadius * playerSafeRadius;
            float friendlySafeSq = friendlySafeRadius * friendlySafeRadius;

            Soldier localPlayer = Soldier.CurrentControlledSoldierOrNull();
            if (localPlayer != null &&
                localPlayer.IsAlive &&
                localPlayer.Pointer != shooterPtr &&
                localPlayer.transform != null &&
                !string.IsNullOrEmpty(localPlayer.faction) &&
                (localPlayer.faction == myFaction || ResourcesManager.IsSameFaction(myFaction, localPlayer.faction)))
            {
                Vehicle pVeh = localPlayer.IsOnVehicle() ? localPlayer.GetCurrentVehicle() : null;
                if (pVeh == null || !pVeh.IsAirVehicle())
                {
                    Vector3 dP = localPlayer.transform.position - impactPos;
                    if (dP.x * dP.x + dP.z * dP.z <= playerSafeSq && Mathf.Abs(dP.y) <= 22.0f)
                        return true;
                }
            }

            EnsureWorldSnapshot();
            int sqCount = WorldSquadCount;
            if (sqCount <= 0)
                return false;

            float maxSquadCheckRadius = Mathf.Max(friendlySafeRadius, playerSafeRadius) + 48.0f;
            float maxSquadCheckSq = maxSquadCheckRadius * maxSquadCheckRadius;
            var squads = WorldSquads;
            var soldiers = WorldSoldiers;

            for (int s = 0; s < sqCount; s++)
            {
                ref SquadSnapshot sqSnap = ref squads[s];
                if (sqSnap.UnitCount == 0 || sqSnap.RefOnPlane || string.IsNullOrEmpty(sqSnap.Faction))
                    continue;

                if (sqSnap.Faction != myFaction && !ResourcesManager.IsSameFaction(myFaction, sqSnap.Faction))
                    continue;

                Vector3 sqDelta = sqSnap.RefPos - impactPos;
                if (sqDelta.x * sqDelta.x + sqDelta.z * sqDelta.z > maxSquadCheckSq)
                    continue;

                int end = sqSnap.UnitStartIndex + sqSnap.UnitCount;
                for (int i = sqSnap.UnitStartIndex; i < end; i++)
                {
                    ref SoldierSnapshot solSnap = ref soldiers[i];
                    if (solSnap.Ptr == shooterPtr || solSnap.Ref == null)
                        continue;

                    Vector3 d = solSnap.Pos - impactPos;
                    float rSq = solSnap.IsPlayer ? playerSafeSq : friendlySafeSq;
                    if (d.x * d.x + d.z * d.z <= rSq && Mathf.Abs(d.y) <= 22.0f)
                        return true;
                }
            }

            return false;
        }

        [HarmonyPatch(typeof(Soldier), nameof(Soldier.AngleToDirection))]
        public static class Soldier_AngleToDirection_Patch
        {
            public static bool Prefix(Soldier __instance, Vector3 __0, ref float __result)
            {
                if (!VisionConfig.EnableMod.Value || __instance == null)
                    return true;

                __result = HorizontalAngleToDirection(__instance, __0);
                return false;
            }
        }

        [HarmonyPatch(typeof(Soldier), nameof(Soldier.SignedAngleToDirection))]
        public static class Soldier_SignedAngleToDirection_Patch
        {
            public static bool Prefix(Soldier __instance, Vector3 __0, ref float __result)
            {
                if (!VisionConfig.EnableMod.Value || __instance == null)
                    return true;

                __result = HorizontalSignedAngleToDirection(__instance, __0);
                return false;
            }
        }

        public static int GetDeterministicHash(IntPtr ptr, int seed)
        {
            unchecked
            {
                long val = ptr.ToInt64();
                int h = (int)(val ^ (val >> 32)) ^ (seed * 397);
                h ^= (h >> 16);
                h *= 0x7feb352d;
                h ^= (h >> 15);
                return h & 0x7FFFFFFF;
            }
        }

        public static void PruneDict<T>(Dictionary<IntPtr, T> dict, HashSet<IntPtr> alive)
        {
            if (dict == null || dict.Count == 0)
                return;

            _sharedPruneBuffer.Clear();
            foreach (var k in dict.Keys)
            {
                if (!alive.Contains(k))
                    _sharedPruneBuffer.Add(k);
            }
            for (int i = 0; i < _sharedPruneBuffer.Count; i++)
            {
                dict.Remove(_sharedPruneBuffer[i]);
            }
            _sharedPruneBuffer.Clear();
        }

        public static void PruneSet(HashSet<IntPtr> set, HashSet<IntPtr> alive)
        {
            if (set == null || set.Count == 0)
                return;

            _sharedPruneBuffer.Clear();
            foreach (var k in set)
            {
                if (!alive.Contains(k))
                    _sharedPruneBuffer.Add(k);
            }
            for (int i = 0; i < _sharedPruneBuffer.Count; i++)
            {
                set.Remove(_sharedPruneBuffer[i]);
            }
            _sharedPruneBuffer.Clear();
        }

        public static int TotalAdaptiveAiExtraTicks;

        // =====================================================================
        // Zero-GC Flat Battlefield World Snapshot (Eliminates Per-Frame & O(N^2)/O(N^3) Squad.AllSquads Scans)
        // =====================================================================
        public struct SquadSnapshot
        {
            public IntPtr Ptr;
            public Squad Ref;
            public Soldier Leader;
            public Soldier RefSoldier;
            public Vector3 RefPos;
            public string Faction;
            public int UnitStartIndex;
            public int UnitCount;
            public bool LeaderIsAlive;
            public bool IsVehicleCrew;
            public bool IsPlayerInSquad;
            public bool LeaderOnVehicle;
            public bool LeaderOnGroundVehicle;
            public bool RefOnPlane;
            public bool HasIntrinsicRadio;
            public bool HasRadioOrRelay;
            public bool IsSpottedOrAware;
        }

        public struct SoldierSnapshot
        {
            public IntPtr Ptr;
            public Soldier Ref;
            public IntPtr SquadPtr;
            public Vector3 Pos;
            public string Faction;
            public bool IsPlayer;
            public bool IsOnVehicle;
            public bool IsSquadLeader;
            public bool HasSurrendered;
            public bool IsSpotted;
            public bool IsInjured;
            public bool IsMedic;
            public bool IsAmmoCarrier;
            public bool CanBorrowAmmo;
            public bool HasHeadgear;
        }

        public static SquadSnapshot[] WorldSquads = new SquadSnapshot[96];
        public static int WorldSquadCount;
        public static SoldierSnapshot[] WorldSoldiers = new SoldierSnapshot[384];
        public static int WorldSoldierCount;
        public static SoldierSnapshot[] WorldInjuredSoldiers = new SoldierSnapshot[64];
        public static int WorldInjuredSoldierCount;

        private static readonly Dictionary<IntPtr, int> _worldSquadIndexByPtr = new Dictionary<IntPtr, int>(96);
        private static readonly HashSet<IntPtr> _seenWorldSoldierPtrs = new HashSet<IntPtr>(384);
        private static float _lastWorldSnapshotTime = -999f;

        public static bool TryGetSquadSnapshot(IntPtr sqPtr, out SquadSnapshot snap)
        {
            EnsureWorldSnapshot();
            if (sqPtr != IntPtr.Zero && _worldSquadIndexByPtr.TryGetValue(sqPtr, out int idx) && idx >= 0 && idx < WorldSquadCount)
            {
                snap = WorldSquads[idx];
                return true;
            }
            snap = default;
            return false;
        }

        public static void EnsureWorldSnapshot(float maxAge = 0.35f)
        {
            float now = Time.time;
            if (now >= _lastWorldSnapshotTime)
            {
                float age = now - _lastWorldSnapshotTime;
                if (age < maxAge)
                    return;

                // If heavy spatial/octree work (e.g. SquadLeaderRoutine) already ran on this frame, reuse the slightly older
                // snapshot for 1 more frame so the O(N) snapshot rebuild lands on a clean frame!
                if (IsHeavyWorkFrame() && age < maxAge + 0.30f)
                    return;
            }

            NotifyDirectorWorkThisFrame();
            _lastWorldSnapshotTime = now;
            int prevSqCount = WorldSquadCount;
            int prevSolCount = WorldSoldierCount;
            int prevInjCount = WorldInjuredSoldierCount;
            WorldSquadCount = 0;
            WorldSoldierCount = 0;
            WorldInjuredSoldierCount = 0;
            _worldSquadIndexByPtr.Clear();
            _seenWorldSoldierPtrs.Clear();

            try
            {
                var allSquads = Squad.AllSquads;
                if (allSquads != null && allSquads.Count > 0)
                {
                    bool radioNetEnabled = VisionConfig.EnableRadiomanIntelNet != null && VisionConfig.EnableRadiomanIntelNet.Value;

                    foreach (var kvp in allSquads)
                    {
                        Squad sq = kvp.Value;
                        if (sq == null || sq.Pointer == IntPtr.Zero)
                            continue;

                        var units = sq.units;
                        int uCount = units != null ? units.Count : 0;
                        Soldier leader = sq.Leader;
                        bool leaderAlive = leader != null && leader.IsAlive && leader.transform != null;

                        Soldier refSoldier = leaderAlive ? leader : null;
                        int unitStart = WorldSoldierCount;
                        int addedUnits = 0;
                        bool hasAliveRadiomanNear = false;
                        Vector3 leaderPos = leaderAlive ? leader.transform.position : Vector3.zero;

                        for (int i = 0; i < uCount; i++)
                        {
                            Soldier u = units[i];
                            if (u == null || u.Pointer == IntPtr.Zero || u.transform == null)
                                continue;

                            if (!u.IsAlive)
                            {
                                // In Easy Red 2, incapacitated casualties waiting for a medic have IsAlive == false (life_total == 0)
                                // and IsInjured == true (!IsDead). Track them in WorldInjuredSoldiers while keeping WorldSoldiers strictly IsAlive!
                                if (u.IsInjured && !u.IsDead && _seenWorldSoldierPtrs.Add(u.Pointer))
                                {
                                    if (WorldInjuredSoldierCount >= WorldInjuredSoldiers.Length)
                                        Array.Resize(ref WorldInjuredSoldiers, WorldInjuredSoldiers.Length * 2);

                                    WorldInjuredSoldiers[WorldInjuredSoldierCount++] = new SoldierSnapshot
                                    {
                                        Ptr = u.Pointer,
                                        Ref = u,
                                        SquadPtr = sq.Pointer,
                                        Pos = u.transform.position,
                                        Faction = u.faction,
                                        IsPlayer = u.IsPlayer(),
                                        IsOnVehicle = u.IsOnVehicle(),
                                        IsInjured = true
                                    };
                                }
                                continue;
                            }

                            if (refSoldier == null)
                                refSoldier = u;

                            Vector3 uPos = u.transform.position;
                            bool uSurr = u.HasSurrended();
                            if (leaderAlive && radioNetEnabled && !hasAliveRadiomanNear && u.IsRadioman() && !uSurr)
                            {
                                if ((uPos - leaderPos).sqrMagnitude <= 32f * 32f)
                                    hasAliveRadiomanNear = true;
                            }

                            if (WorldSoldierCount >= WorldSoldiers.Length)
                                Array.Resize(ref WorldSoldiers, WorldSoldiers.Length * 2);

                            _seenWorldSoldierPtrs.Add(u.Pointer);
                            WorldSoldiers[WorldSoldierCount++] = new SoldierSnapshot
                            {
                                Ptr = u.Pointer,
                                Ref = u,
                                SquadPtr = sq.Pointer,
                                Pos = uPos,
                                Faction = u.faction,
                                IsPlayer = u.IsPlayer(),
                                IsOnVehicle = u.IsOnVehicle(),
                                IsSquadLeader = leaderAlive && u.Pointer == leader.Pointer,
                                HasSurrendered = uSurr,
                                IsSpotted = u.IsSpotted,
                                IsInjured = u.IsInjured,
                                IsMedic = u.IsMedic(),
                                IsAmmoCarrier = u.IsAmmoBoxCarrier(),
                                CanBorrowAmmo = u.CanBorrowAmmo,
                                HasHeadgear = u.hasHeadgear
                            };
                            addedUnits++;
                        }

                        if (refSoldier == null)
                            continue;

                        Vector3 refPos = leaderAlive ? leaderPos : refSoldier.transform.position;
                        string faction = refSoldier.faction;
                        bool leaderOnVeh = leaderAlive && leader.IsOnVehicle();
                        bool leaderOnGroundVeh = false;
                        bool leaderHasOnboardRadio = false;
                        if (leaderOnVeh)
                        {
                            Vehicle lv = leader.GetCurrentVehicle();
                            if (lv != null && lv.Pointer != IntPtr.Zero && lv.TryCast<VehicleParachute>() == null)
                            {
                                leaderHasOnboardRadio = true;
                                if (!lv.IsAirVehicle())
                                    leaderOnGroundVeh = true;
                            }
                        }

                        bool refOnPlane = false;
                        if (refSoldier.IsOnVehicle())
                        {
                            Vehicle rv = refSoldier.GetCurrentVehicle();
                            if (rv != null && rv.IsAirVehicle())
                                refOnPlane = true;
                        }

                        bool isPlayerInSq = sq.IsPlayerInSquad();
                        bool hasIntrinsicRadio = !radioNetEnabled ||
                                                 (leaderAlive && (leaderHasOnboardRadio || hasAliveRadiomanNear || sq.RadioRequestActiveAndConfirmed()));

                        bool spottedOrAware = sq.IsEnemyAwareOfThisSquad ||
                                              sq.isMarked ||
                                              sq.isHalfMarked ||
                                              sq.isMarkedPinPoint ||
                                              refSoldier.IsSpotted;

                        if (WorldSquadCount >= WorldSquads.Length)
                            Array.Resize(ref WorldSquads, WorldSquads.Length * 2);

                        int sqIdx = WorldSquadCount++;
                        _worldSquadIndexByPtr[sq.Pointer] = sqIdx;
                        WorldSquads[sqIdx] = new SquadSnapshot
                        {
                            Ptr = sq.Pointer,
                            Ref = sq,
                            Leader = leader,
                            RefSoldier = refSoldier,
                            RefPos = refPos,
                            Faction = faction,
                            UnitStartIndex = unitStart,
                            UnitCount = addedUnits,
                            LeaderIsAlive = leaderAlive,
                            IsVehicleCrew = sq.IsVehicleCrew,
                            IsPlayerInSquad = isPlayerInSq,
                            LeaderOnVehicle = leaderOnVeh,
                            LeaderOnGroundVehicle = leaderOnGroundVeh,
                            RefOnPlane = refOnPlane,
                            HasIntrinsicRadio = hasIntrinsicRadio,
                            HasRadioOrRelay = hasIntrinsicRadio,
                            IsSpottedOrAware = spottedOrAware
                        };
                    }

                    // Pass 2: Pure C# L1-cache Radio Relay propagation (< 2 microseconds, zero IL2CPP boundary crossings)
                    if (radioNetEnabled && WorldSquadCount > 1)
                    {
                        for (int i = 0; i < WorldSquadCount; i++)
                        {
                            if (WorldSquads[i].HasRadioOrRelay || !WorldSquads[i].LeaderIsAlive || string.IsNullOrEmpty(WorldSquads[i].Faction))
                                continue;

                            string myFaction = WorldSquads[i].Faction;
                            float relayR = VeteranAndTacticsPatches.GetFactionCommandProfile(myFaction).RadioRelayRadius;
                            float relayRSq = relayR * relayR;
                            Vector3 myPos = WorldSquads[i].RefPos;

                            for (int j = 0; j < WorldSquadCount; j++)
                            {
                                if (i == j || !WorldSquads[j].LeaderIsAlive || WorldSquads[j].Faction != myFaction)
                                    continue;

                                if (!WorldSquads[j].HasIntrinsicRadio && !WorldSquads[j].LeaderOnGroundVehicle)
                                    continue;

                                if ((WorldSquads[j].RefPos - myPos).sqrMagnitude <= relayRSq)
                                {
                                    WorldSquads[i].HasRadioOrRelay = true;
                                    break;
                                }
                            }
                        }
                    }
                    // Pass 3: Append any live soldiers detached from Squad.units (e.g. surrendered soldiers whose
                    // joinedSquad was cleared by vanilla Soldier.Surrender(), or lone players/pilots)
                    var allCreatures = Creature.allCreatures;
                    if (allCreatures != null)
                    {
                        int cCount = allCreatures.Count;
                        for (int cIdx = 0; cIdx < cCount; cIdx++)
                        {
                            Creature c = allCreatures[cIdx];
                            if (c == null || c.Pointer == IntPtr.Zero || _seenWorldSoldierPtrs.Contains(c.Pointer))
                                continue;

                            if (!c.IsAlive)
                            {
                                if (c.IsInjured && !c.IsDead)
                                {
                                    Soldier inj = c.TryCast<Soldier>();
                                    if (inj != null && inj.Pointer != IntPtr.Zero && inj.transform != null && _seenWorldSoldierPtrs.Add(inj.Pointer))
                                    {
                                        if (WorldInjuredSoldierCount >= WorldInjuredSoldiers.Length)
                                            Array.Resize(ref WorldInjuredSoldiers, WorldInjuredSoldiers.Length * 2);

                                        WorldInjuredSoldiers[WorldInjuredSoldierCount++] = new SoldierSnapshot
                                        {
                                            Ptr = inj.Pointer,
                                            Ref = inj,
                                            SquadPtr = inj.joinedSquad != null ? inj.joinedSquad.Pointer : IntPtr.Zero,
                                            Pos = inj.transform.position,
                                            Faction = inj.faction,
                                            IsPlayer = inj.IsPlayer(),
                                            IsOnVehicle = inj.IsOnVehicle(),
                                            IsInjured = true
                                        };
                                    }
                                }
                                continue;
                            }

                            Soldier u = c.TryCast<Soldier>();
                            if (u == null || !u.IsAlive || u.Pointer == IntPtr.Zero || _seenWorldSoldierPtrs.Contains(u.Pointer) || u.transform == null)
                                continue;

                            if (WorldSoldierCount >= WorldSoldiers.Length)
                                Array.Resize(ref WorldSoldiers, WorldSoldiers.Length * 2);

                            _seenWorldSoldierPtrs.Add(u.Pointer);
                            WorldSoldiers[WorldSoldierCount++] = new SoldierSnapshot
                            {
                                Ptr = u.Pointer,
                                Ref = u,
                                SquadPtr = u.joinedSquad != null ? u.joinedSquad.Pointer : IntPtr.Zero,
                                Pos = u.transform.position,
                                Faction = u.faction,
                                IsPlayer = u.IsPlayer(),
                                IsOnVehicle = u.IsOnVehicle(),
                                IsSquadLeader = false,
                                HasSurrendered = u.HasSurrended(),
                                IsSpotted = u.IsSpotted,
                                IsInjured = u.IsInjured,
                                IsMedic = u.IsMedic(),
                                IsAmmoCarrier = u.IsAmmoBoxCarrier(),
                                CanBorrowAmmo = u.CanBorrowAmmo,
                                HasHeadgear = u.hasHeadgear
                            };
                        }
                    }
                }
            }
            catch
            {
            }

            if (prevSqCount > WorldSquadCount)
                Array.Clear(WorldSquads, WorldSquadCount, prevSqCount - WorldSquadCount);
            if (prevSolCount > WorldSoldierCount)
                Array.Clear(WorldSoldiers, WorldSoldierCount, prevSolCount - WorldSoldierCount);
            if (prevInjCount > WorldInjuredSoldierCount)
                Array.Clear(WorldInjuredSoldiers, WorldInjuredSoldierCount, prevInjCount - WorldInjuredSoldierCount);
        }

        public static void ClearSpatialCaches()
        {
            _turretVehicleCache.Clear();
            _ceilingCache.Clear();
            _bridgeCellCache.Clear();
            _worldSquadIndexByPtr.Clear();
            if (WorldSquadCount > 0)
                Array.Clear(WorldSquads, 0, WorldSquadCount);
            if (WorldSoldierCount > 0)
                Array.Clear(WorldSoldiers, 0, WorldSoldierCount);
            if (WorldInjuredSoldierCount > 0)
                Array.Clear(WorldInjuredSoldiers, 0, WorldInjuredSoldierCount);
            WorldSquadCount = 0;
            WorldSoldierCount = 0;
            WorldInjuredSoldierCount = 0;
            _lastWorldSnapshotTime = -999f;
            _nextAllowedBotRpcTime = 0f;
            TotalAdaptiveAiExtraTicks = 0;
            TotalPingGovernorSkippedCycles = 0;
            TotalHitchesOver28ms = 0;
            _currentWindowMaxFrameMs = 0f;
            _previousWindowMaxFrameMs = 0f;
            _nextFrameWindowRolloverTime = 0f;
        }

        public static void PruneSpatialCachesIfOversized()
        {
            if (_turretVehicleCache.Count > 256)
                _turretVehicleCache.Clear();
            if (_ceilingCache.Count > 4096)
                _ceilingCache.Clear();
            if (_bridgeCellCache.Count > 4096)
                _bridgeCellCache.Clear();
        }

        // =====================================================================
        // Adaptive AI Brain Tick Scheduler (Creature.ProcessSequentialUpdates)
        // =====================================================================
        // Vanilla Creature.ProcessSequentialUpdates (0x180A53760) hardcodes `cmp ebp, 1`,
        // updating only 1 slot in Creature.allCreatures per frame. In a 120-140 bot match,
        // each soldier waits 2.0-2.5+ seconds between SoldierAI.SequentialUpdate brain ticks,
        // and any frame that lands on a Vehicle, dead soldier, or seated passenger wastes
        // the entire frame's AI budget.
        [HarmonyPatch(typeof(Creature), nameof(Creature.ProcessSequentialUpdates))]
        public static class Creature_ProcessSequentialUpdates_Patch
        {
            private static bool _isRunningExtraTicks;

            public static void Postfix()
            {
                if (_isRunningExtraTicks)
                    return;

                if (!VisionConfig.EnableMod.Value || !VisionConfig.EnableAdaptiveAiTickScaling.Value || Time.timeScale <= 0.01f)
                    return;

                // Prioritize multiplayer ping & frame-time stability: never add extra ticks on busy frames
                // or on frames where vanilla SquadLeaderRoutine / world snapshot rebuild already ran!
                if (IsHeavyWorkFrame() || ShouldSkipBotCycleForNetworkPing())
                    return;

                float dt = Time.unscaledDeltaTime;
                if (dt > 0.025f)
                    return;

                try
                {
                    var list = Creature.allCreatures;
                    if (list == null)
                        return;

                    int count = list.Count;
                    if (count <= 1)
                        return;

                    int lastIdx = Creature.sequentialUpdateIndex - 1;
                    if (lastIdx < 0 || lastIdx >= count)
                        lastIdx = count - 1;

                    int activeFootSoldiersTicked = 0;
                    bool squadLeaderTicked = false;
                    Creature lastCreature = list[lastIdx];
                    if (lastCreature != null && lastCreature.IsAlive)
                    {
                        Soldier lastSoldier = lastCreature.TryCast<Soldier>();
                        if (lastSoldier != null && lastSoldier.isActiveAndEnabled && !lastSoldier.IsOnVehicle())
                        {
                            activeFootSoldiersTicked = 1;
                            if (lastSoldier.IsSquadLeader())
                                squadLeaderTicked = true;
                        }
                    }

                    int players = GetConnectedPlayerCount();
                    int targetFootTicks;
                    int maxScanSteps;

                    if (players >= 4)
                    {
                        // Large multiplayer lobby (4-10+ players): strictly 1 active foot tick per frame
                        // so Photon outgoing serialization queues never contend with player ping.
                        targetFootTicks = 1;
                        maxScanSteps = Mathf.Min(count - 1, 3);
                    }
                    else if (players > 1)
                    {
                        // Small Co-op session (2-3 players): at most 2 ticks and only when frame time < 14.5ms
                        targetFootTicks = (count > 60 && dt < 0.0145f) ? 2 : 1;
                        maxScanSteps = Mathf.Min(count - 1, 4);
                    }
                    else
                    {
                        // Singleplayer: cap at 2 active foot soldiers per frame (1 vanilla + at most 1 extra within 0.45ms CPU budget)
                        // so multiple SquadLeaderRoutine octree/pathfinding bursts NEVER stack onto the same frame!
                        targetFootTicks = (count > 55 && dt < 0.0175f) ? 2 : 1;
                        maxScanSteps = Mathf.Min(count - 1, 6);
                    }

                    if (activeFootSoldiersTicked >= targetFootTicks)
                        return;

                    _isRunningExtraTicks = true;
                    int scanned = 0;
                    long startStamp = Stopwatch.GetTimestamp();
                    long budgetTicks = (long)(Stopwatch.Frequency * 0.00045); // Strict 0.45ms ceiling for extra AI ticks

                    while (activeFootSoldiersTicked < targetFootTicks && scanned < maxScanSteps)
                    {
                        if ((Stopwatch.GetTimestamp() - startStamp) >= budgetTicks)
                            break;

                        int idx = Creature.sequentialUpdateIndex;
                        if (idx < 0 || idx >= count)
                            idx = 0;

                        Creature c = list[idx];
                        if (c == null)
                        {
                            Creature.sequentialUpdateIndex = idx + 1;
                            scanned++;
                            continue;
                        }

                        Soldier s = c.TryCast<Soldier>();
                        if (s == null || !s.isActiveAndEnabled)
                        {
                            Creature.sequentialUpdateIndex = idx + 1;
                            scanned++;
                            continue;
                        }

                        // Never run two Squad Leaders (SquadOrdersRoutine -> 8x CoverManager octree searches) in the same frame!
                        bool isLeader = s.IsAlive && !s.IsOnVehicle() && s.IsSquadLeader();
                        if (isLeader && squadLeaderTicked)
                            break;

                        Creature.sequentialUpdateIndex = idx + 1;
                        scanned++;

                        s.SequentialUpdate();

                        SoldierAI ai = s.aiController;
                        if (ai != null && ai.enabled)
                        {
                            ai.SequentialUpdate();
                        }

                        if (s.IsAlive && !s.IsOnVehicle())
                        {
                            activeFootSoldiersTicked++;
                            TotalAdaptiveAiExtraTicks++;
                            if (isLeader)
                                squadLeaderTicked = true;
                        }
                    }
                }
                catch
                {
                }
                finally
                {
                    _isRunningExtraTicks = false;
                }
            }
        }
    }
}
