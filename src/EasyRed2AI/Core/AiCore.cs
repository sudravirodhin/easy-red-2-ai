using System;
using System.Collections.Generic;
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
        private static float _nextAllowedBotRpcTime;
        private static float _nextRoomCheckTime;
        private static int _cachedRoomPlayerCount = 1;
        private static int _lastHeavyWorkFrame = -1;
        private static readonly List<IntPtr> _sharedPruneBuffer = new List<IntPtr>(256);
        private static readonly Dictionary<IntPtr, Vehicle> _turretVehicleCache = new Dictionary<IntPtr, Vehicle>(64);
        private static readonly Dictionary<long, bool> _ceilingCache = new Dictionary<long, bool>(512);
        private static readonly Dictionary<long, bool> _bridgeCellCache = new Dictionary<long, bool>(512);
        private static readonly Dictionary<string, float> _debugLogCooldowns = new Dictionary<string, float>(32);

        public static int TotalPingGovernorSkippedCycles;

        public static void LogDebug(string category, string message, float minInterval = 2.0f)
        {
            if (!VisionConfig.DebugLogging.Value || Plugin.Log == null)
                return;

            float now = Time.unscaledTime;
            if (_debugLogCooldowns.TryGetValue(category, out float nextAllowed) && now < nextAllowed)
                return;

            _debugLogCooldowns[category] = now + minInterval;
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
                }
                else
                {
                    _cachedRoomPlayerCount = 1;
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
            float threshold = players >= 6 ? 0.0168f : (players > 1 ? 0.0178f : 0.0250f);
            if (dt > threshold)
            {
                TotalPingGovernorSkippedCycles++;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Ensures at most ONE global director pass (Macro Director, TeamNet GOAP Coordinator,
        /// or Dictionary Pruning) executes on any single frame, and never on a slow frame.
        /// </summary>
        public static bool TryClaimHeavyFrameWorkSlot()
        {
            if (ShouldSkipBotCycleForNetworkPing())
                return false;

            int frame = Time.frameCount;
            if (_lastHeavyWorkFrame == frame)
                return false;

            _lastHeavyWorkFrame = frame;
            return true;
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

        public static bool IsLocallyOwnedVehicle(Vehicle v)
        {
            if (v == null || v.Pointer == IntPtr.Zero)
                return false;

            SyncVehicle syncher = v.vehicleSyncher;
            return syncher == null || syncher.OwnSyncher;
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

        public static void ClearSpatialCaches()
        {
            _turretVehicleCache.Clear();
            _ceilingCache.Clear();
            _bridgeCellCache.Clear();
            _nextAllowedBotRpcTime = 0f;
            TotalAdaptiveAiExtraTicks = 0;
            TotalPingGovernorSkippedCycles = 0;
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
                if (ShouldSkipBotCycleForNetworkPing())
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
                    Creature lastCreature = list[lastIdx];
                    if (lastCreature != null && lastCreature.IsAlive)
                    {
                        Soldier lastSoldier = lastCreature.TryCast<Soldier>();
                        if (lastSoldier != null && lastSoldier.isActiveAndEnabled && !lastSoldier.IsOnVehicle())
                        {
                            activeFootSoldiersTicked = 1;
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
                        // Singleplayer:
                        //   <= 45 units : 1 active foot soldier / frame (~0.5s full cycle at 60 FPS)
                        //   46..95 units: 2 active foot soldiers / frame (~0.6s full cycle at 60 FPS)
                        //   96+ units   : 3 active foot soldiers / frame (~0.65s full cycle at 60 FPS)
                        targetFootTicks = count > 95 ? 3 : (count > 45 ? 2 : 1);
                        if (dt > 0.019f && targetFootTicks > 2)
                        {
                            targetFootTicks = 2;
                        }
                        maxScanSteps = Mathf.Min(count - 1, 8);
                    }

                    if (activeFootSoldiersTicked >= targetFootTicks)
                        return;

                    _isRunningExtraTicks = true;
                    int scanned = 0;

                    while (activeFootSoldiersTicked < targetFootTicks && scanned < maxScanSteps)
                    {
                        int idx = Creature.sequentialUpdateIndex;
                        if (idx < 0 || idx >= count)
                            idx = 0;

                        Creature.sequentialUpdateIndex = idx + 1;
                        scanned++;

                        Creature c = list[idx];
                        if (c == null)
                            continue;

                        Soldier s = c.TryCast<Soldier>();
                        if (s == null || !s.isActiveAndEnabled)
                            continue;

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
