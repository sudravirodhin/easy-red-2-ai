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
        private static float _nextAllowedBotRpcTime;
        private static float _nextRoomCheckTime;
        private static int _cachedRoomPlayerCount = 1;
        private static IntPtr _lastPublishedRoomPtr;
        private static int _lastHeavyWorkFrame = -1;
        private static readonly List<IntPtr> _sharedPruneBuffer = new List<IntPtr>(256);
        private static readonly Dictionary<IntPtr, Vehicle> _turretVehicleCache = new Dictionary<IntPtr, Vehicle>(64);
        private static readonly Dictionary<long, bool> _ceilingCache = new Dictionary<long, bool>(512);
        private static readonly Dictionary<long, bool> _bridgeCellCache = new Dictionary<long, bool>(512);
        private static readonly Dictionary<string, float> _debugLogCooldowns = new Dictionary<string, float>(32);
        private static readonly Dictionary<int, (bool HasMod, float ExpireTime)> _playerModdedCache = new Dictionary<int, (bool HasMod, float ExpireTime)>(16);
        private static readonly List<Vector3> _cachedRemotePlayerPositions = new List<Vector3>(12);
        private static float _nextRemotePlayerPosScanTime;

        public static int TotalPingGovernorSkippedCycles;

        [InterpolatedStringHandler]
        public ref struct DebugLogInterpolatedStringHandler
        {
            private StringBuilder _builder;

            public DebugLogInterpolatedStringHandler(int literalLength, int formattedCount, out bool shouldAppend)
            {
                if (!VisionConfig.DebugLogging.Value || Plugin.Log == null)
                {
                    _builder = null;
                    shouldAppend = false;
                    return;
                }
                _builder = new StringBuilder(literalLength + formattedCount * 12);
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

            internal string GetFormattedText() => _builder != null ? _builder.ToString() : string.Empty;
        }

        public static void LogDebug(string category, DebugLogInterpolatedStringHandler message, float minInterval = 4.0f)
        {
            if (!VisionConfig.DebugLogging.Value || Plugin.Log == null)
                return;

            float now = Time.unscaledTime;
            if (_debugLogCooldowns.TryGetValue(category, out float nextAllowed) && now < nextAllowed)
                return;

            _debugLogCooldowns[category] = now + minInterval;
            Plugin.Log.LogInfo($"[Diag:{category}] {message.GetFormattedText()}");
        }

        public static void LogDebug(string category, string message, float minInterval = 4.0f)
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
            if (now >= _lastWorldSnapshotTime && (now - _lastWorldSnapshotTime) < maxAge)
                return;

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
