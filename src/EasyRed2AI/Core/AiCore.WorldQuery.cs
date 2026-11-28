using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace EasyRed2HumanAI
{
    /// <summary>
    /// Shared battlefield world-snapshot, geometry/terrain probe, crew-retreat, mine,
    /// human aim-lane, and reusable tactical/weapon helper partial for <see cref="AiCore"/>.
    /// </summary>
    public static partial class AiCore
    {
        public struct ThrowablePrefabInfo
        {
            public bool IsValid;
            public FuseType Fuse;
            public GrenadeType Type;
            public float MaxPenetration;
            public float ExplosionRadius;
            public bool IsPressureMine;
            public bool IsAtMine;
            public bool IsApMine;
            public bool IsSatchelOrHeavyDemo;
        }

        public struct ActiveLandMine
        {
            public IntPtr Ptr;
            public ItemGrenade Ref;
            public Vector3 Pos;
            public bool IsAtMine;
            public float ExpireTime;
        }

        public static ActiveLandMine[] ActiveLandMines = new ActiveLandMine[128];
        public static int ActiveLandMineCount;
        public static int SpecialMunitionsUsedCount { get; internal set; }

        private static readonly Dictionary<string, ThrowablePrefabInfo> _throwablePrefabCache =
            new Dictionary<string, ThrowablePrefabInfo>(64, StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<IntPtr> _nonMineSpawnedItemPtrs = new HashSet<IntPtr>(512);
        private static float _nextActiveMineScanTime;
        private static int _activeMineScanCursor;
        private static float _nextActiveMineChunkTime;

        private static readonly HashSet<IntPtr> _confirmedVehicleCrewSquads = new HashSet<IntPtr>(64);
        private static readonly HashSet<IntPtr> _confirmedPlaneCrewSquads = new HashSet<IntPtr>(32);
        private static readonly HashSet<IntPtr> _confirmedInfantrySquads = new HashSet<IntPtr>(128);

        public static void MarkConfirmedVehicleCrewSquad(Squad sq)
        {
            if (sq == null || sq.Pointer == IntPtr.Zero)
                return;
            _confirmedInfantrySquads.Remove(sq.Pointer);
            _confirmedVehicleCrewSquads.Add(sq.Pointer);
        }

        public static void MarkConfirmedPlaneCrewSquad(Squad sq)
        {
            if (sq == null || sq.Pointer == IntPtr.Zero)
                return;
            _confirmedInfantrySquads.Remove(sq.Pointer);
            _confirmedVehicleCrewSquads.Add(sq.Pointer);
            _confirmedPlaneCrewSquads.Add(sq.Pointer);
        }

        public static bool IsConfirmedPlaneCrewSquad(Squad sq)
        {
            return sq != null && sq.Pointer != IntPtr.Zero && _confirmedPlaneCrewSquads.Contains(sq.Pointer);
        }

        public static void PruneSquadClassificationCaches(HashSet<IntPtr> activeSquadPtrs)
        {
            if (activeSquadPtrs == null)
                return;
            PruneSet(_confirmedVehicleCrewSquads, activeSquadPtrs);
            PruneSet(_confirmedPlaneCrewSquads, activeSquadPtrs);
            PruneSet(_confirmedInfantrySquads, activeSquadPtrs);
        }

        /// <summary>
        /// Native Squad.get_IsVehicleCrew (0x18080D0A0) literally compiles to `!this.isOpenForJoiners`,
        /// which returns true for EVERY closed/scripted foot-infantry squad and Volga/beach landing-boat squad!
        /// This helper classifies genuine Tank, Aircraft (including 6-10 seat Heavy Bombers), and Combat-Vehicle crews
        /// (and remembers them after bailout) without ever misclassifying closed infantry or paratrooper/troop-transport squads.
        /// </summary>
        public static bool IsTrueVehicleCrewSquad(Squad sq)
        {
            if (ReferenceEquals(sq, null) || sq == null || sq.Pointer == IntPtr.Zero)
                return false;

            IntPtr ptr = sq.Pointer;
            if (_confirmedVehicleCrewSquads.Contains(ptr))
                return true;

            if (sq.isOpenForJoiners)
                return false;

            if (_confirmedInfantrySquads.Contains(ptr))
                return false;

            try
            {
                var units = sq.units;
                int uCount = units != null ? units.Count : 0;
                if (uCount <= 0)
                    return false;

                if (sq.isChargeSquad)
                {
                    _confirmedInfantrySquads.Add(ptr);
                    return false;
                }

                // 1. Inspect seated vehicles BEFORE checking uCount > 5 so 6-10 seat Heavy Bomber crews
                //    are recognized as aircrew rather than misclassified as foot infantry, while checking
                //    veh.IsTransportVehicle() FIRST so C-47 / Ju-52 paratroopers & LCVPs stay infantry!
                bool hasInfantrySpecialist = false;
                for (int i = 0; i < uCount; i++)
                {
                    Soldier u = units[i];
                    if (ReferenceEquals(u, null) || u == null || u.Pointer == IntPtr.Zero)
                        continue;

                    Vehicle veh = u.IsOnVehicle() ? u.GetCurrentVehicle() : u.VehicleToOperate();
                    if (veh != null && veh.Pointer != IntPtr.Zero)
                    {
                        VehicleFastHandles vh = GetOrCreateVehicleHandles(veh);
                        if (vh != null && !vh.IsParachute)
                        {
                            if (veh.IsTransportVehicle())
                            {
                                _confirmedInfantrySquads.Add(ptr);
                                return false;
                            }

                            if (vh.IsPlane || vh.IsAir)
                            {
                                MarkConfirmedPlaneCrewSquad(sq);
                                return true;
                            }

                            if (vh.IsTank)
                            {
                                _confirmedVehicleCrewSquads.Add(ptr);
                                return true;
                            }

                            VehicleWithWheels wheeled = veh.TryCast<VehicleWithWheels>();
                            if (wheeled != null)
                            {
                                if (wheeled.IsTransportVehicle())
                                {
                                    _confirmedInfantrySquads.Add(ptr);
                                    return false;
                                }
                                if (wheeled.RetreatCrewOnDestroy())
                                {
                                    _confirmedVehicleCrewSquads.Add(ptr);
                                    return true;
                                }
                            }
                        }
                    }

                    if (u.IsMedic() || u.hasSyringe || u.hasMg || u.isMarksMan || u.IsRadioman() || u.IsAmmoBoxCarrier() || u.isAtUnit)
                    {
                        hasInfantrySpecialist = true;
                    }
                }

                if (uCount > 5 || hasInfantrySpecialist)
                {
                    _confirmedInfantrySquads.Add(ptr);
                    return false;
                }

                Soldier mech = sq.GetMechanic();
                if (mech != null && mech.Pointer != IntPtr.Zero && mech.GetRepairTools() != null)
                {
                    _confirmedVehicleCrewSquads.Add(ptr);
                    return true;
                }

                if (sq.fullySpawned)
                    _confirmedInfantrySquads.Add(ptr);
            }
            catch { }

            return false;
        }


        /// <summary>
        /// Collects currently active classic and scripted <see cref="MissionObjective"/> instances into
        /// <paramref name="activeObjectives"/> (clearing it first and deduplicating by native pointer).
        /// </summary>
        public static void CollectActiveMissionObjectives(BattlePhase phase, List<MissionObjective> activeObjectives)
        {
            activeObjectives.Clear();
            var rawObjectives = phase != null ? phase.objectives : null;
            int rawLen = rawObjectives != null ? rawObjectives.Length : 0;
            for (int i = 0; i < rawLen; i++)
            {
                MissionObjective obj = rawObjectives[i];
                if (obj == null || obj.Pointer == IntPtr.Zero || obj.gameObject == null || !obj.gameObject.activeInHierarchy)
                    continue;
                MissionTask task = obj.missionTask;
                if (task == null || task.Pointer == IntPtr.Zero || !task.IsTaskEnabled())
                    continue;
                activeObjectives.Add(obj);
            }

            var scriptedObjectives = MissionObjectiveScripted.scriptedMissionObjectives;
            if (scriptedObjectives != null)
            {
                int scriptedLen = scriptedObjectives.Count;
                for (int i = 0; i < scriptedLen; i++)
                {
                    MissionObjectiveScripted sObj = scriptedObjectives[i];
                    if (sObj == null || sObj.Pointer == IntPtr.Zero || sObj.gameObject == null || !sObj.gameObject.activeInHierarchy)
                        continue;
                    MissionTask task = sObj.missionTask;
                    if (task == null || task.Pointer == IntPtr.Zero || !task.IsTaskEnabled())
                        continue;
                    bool duplicate = false;
                    for (int j = 0; j < activeObjectives.Count; j++)
                    {
                        if (activeObjectives[j].Pointer == sObj.Pointer)
                        {
                            duplicate = true;
                            break;
                        }
                    }
                    if (!duplicate)
                        activeObjectives.Add(sObj);
                }
            }
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
                        contestOrProgress01 = (rawPct > 0.01f && rawPct < 0.98f) ? Mathf.Clamp01(1.0f - rawPct) : 0f;
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
            return TryResolveSpottableFast(s, out _, out _);
        }

        public static bool TryGetSpottableCenter(Spottable s, out Vector3 center)
        {
            if (!TryResolveSpottableFast(s, out _, out _))
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

            string canonFaction = InternFaction(myFaction);
            float now = Time.time;

            try
            {
                BattlePhase phase = TryGetCurrentBattlePhase(BattleManager.instance);
                IntPtr phasePtr = phase != null ? phase.Pointer : IntPtr.Zero;
                var cacheKey = (phasePtr, canonFaction);

                if (!_enemySpawnDirCache.TryGetValue(cacheKey, out EnemySpawnDirCacheEntry entry) || now >= entry.ExpireTime)
                {
                    entry = new EnemySpawnDirCacheEntry { ExpireTime = now + 5.0f };

                    if (phase != null && phase.EnemyFactionHasSpawn(canonFaction))
                    {
                        entry.HasCentroid = true;
                        entry.Centroid = phase.GetOppositeFactionTerritoryPosition(canonFaction);
                    }
                    else
                    {
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
                                if (string.IsNullOrEmpty(spFact) || spFact == canonFaction || spFact == "Civilian" || AreSameSideFactions(canonFaction, spFact))
                                    continue;

                                sum += sp.GetSpawnPosition();
                                count++;
                            }

                            if (count > 0)
                            {
                                entry.HasCentroid = true;
                                entry.Centroid = sum / count;
                            }
                        }
                    }

                    if (_enemySpawnDirCache.Count > 32)
                        _enemySpawnDirCache.Clear();
                    _enemySpawnDirCache[cacheKey] = entry;
                }

                if (entry.HasCentroid)
                {
                    Vector3 toEnemy = entry.Centroid - fromPos;
                    toEnemy.y = 0f;
                    if (toEnemy.sqrMagnitude > 1.0f)
                    {
                        dir = toEnemy.normalized;
                        return true;
                    }
                }
            }
            catch { }

            return false;
        }

        private static bool TrySampleDryGroundAltitude(Vector3 cand, out float groundY)
        {
            float terrainY = ResourcesManager.GetTerrainAltitude(cand, 0f);
            if (terrainY >= 0.12f)
            {
                groundY = terrainY;
                return true;
            }

            // Outside Unity Terrain bounds (or on mesh islands/custom airfields where GetTerrainAltitude returns 0f),
            // probe solid environment colliders directly so out-of-bounds grounded pilots still resolve dry footing!
            Vector3 probeStart = new Vector3(cand.x, Mathf.Max(cand.y, 0f) + 85.0f, cand.z);
            if (Physics.Raycast(probeStart, Vector3.down, out RaycastHit hit, 180.0f, GetSolidEnvironmentMask(), QueryTriggerInteraction.Ignore) &&
                hit.point.y >= 0.05f &&
                hit.normal.y >= 0.45f)
            {
                groundY = hit.point.y;
                return true;
            }

            groundY = terrainY;
            return false;
        }

        public static bool TryNormalizeFootSpawnPoint(Vector3 rawPos, out Vector3 groundedPos)
        {
            groundedPos = rawPos;
            if (TrySampleDryGroundAltitude(rawPos, out float groundY))
            {
                if (rawPos.y > groundY + 14.0f)
                    return false;

                // Snap Y to ground altitude so 3D distance checks (e.g. MissionTaskRetreat.IsInsideTaskArea) trigger on foot
                groundedPos = new Vector3(rawPos.x, Mathf.Max(groundY, 0.05f), rawPos.z);
                return true;
            }

            return false;
        }

        private static bool TrySampleDryRetreatPoint(Vector3 originPos, Vector3 horizDir, float dist, out Vector3 dryPos)
        {
            dryPos = Vector3.zero;
            horizDir.y = 0f;
            if (horizDir.sqrMagnitude < 0.001f)
                return false;
            horizDir.Normalize();

            for (int attempt = 0; attempt < 5; attempt++)
            {
                float yaw = 0f;
                if (attempt == 1) yaw = 28f;
                else if (attempt == 2) yaw = -28f;
                else if (attempt == 3) yaw = 60f;
                else if (attempt == 4) yaw = -60f;

                Vector3 dir = attempt == 0 ? horizDir : (Quaternion.Euler(0f, yaw, 0f) * horizDir);
                Vector3 cand = originPos + dir * dist;
                if (TrySampleDryGroundAltitude(cand, out float alt))
                {
                    dryPos = new Vector3(cand.x, alt, cand.z);
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Resolves the nearest dry, foot-reachable friendly spawn position on both classic <see cref="BattlePhase"/> maps
        /// and <c>sp_ME_...</c> Mission Editor V2 streaming maps (via <c>SpawnManager.activeSpawns</c>).
        /// Filters out sky aircraft spawns and deep-water boat spawns, and snaps Y to terrain altitude.
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
                    if (nearest != null &&
                        nearest.Pointer != IntPtr.Zero &&
                        TryNormalizeFootSpawnPoint(nearest.GetSpawnPosition(), out spawnPos))
                    {
                        return true;
                    }
                }

                var spawns = SpawnManager.activeSpawns;
                if (spawns != null && spawns.Count > 0)
                {
                    float bestExactSq = float.MaxValue;
                    Vector3 bestExactPos = Vector3.zero;
                    bool foundExact = false;

                    float bestAlliedSq = float.MaxValue;
                    Vector3 bestAlliedPos = Vector3.zero;
                    bool foundAllied = false;

                    for (int i = 0; i < spawns.Count; i++)
                    {
                        SpawnManager sp = spawns[i];
                        if (sp == null || sp.Pointer == IntPtr.Zero || sp.gameObject == null || !sp.gameObject.activeInHierarchy)
                            continue;

                        string spFaction = sp.GetFaction();
                        if (string.IsNullOrEmpty(spFaction) || spFaction == "Civilian")
                            continue;

                        bool exactMatch = spFaction == myFaction;
                        if (!exactMatch && !AreSameSideFactions(myFaction, spFaction))
                            continue;

                        if (!TryNormalizeFootSpawnPoint(sp.GetSpawnPosition(), out Vector3 cand))
                            continue;

                        float dSq = (cand - fromPos).sqrMagnitude;
                        if (exactMatch)
                        {
                            if (dSq < bestExactSq)
                            {
                                bestExactSq = dSq;
                                bestExactPos = cand;
                                foundExact = true;
                            }
                        }
                        else if (dSq < bestAlliedSq)
                        {
                            bestAlliedSq = dSq;
                            bestAlliedPos = cand;
                            foundAllied = true;
                        }
                    }

                    if (foundExact)
                    {
                        spawnPos = bestExactPos;
                        return true;
                    }
                    if (foundAllied)
                    {
                        spawnPos = bestAlliedPos;
                        return true;
                    }
                }
            }
            catch { }

            return false;
        }

        private static bool TryGetAnyFriendlySpawnHorizontalAnchor(string myFaction, Vector3 fromPos, out Vector3 anchorPos)
        {
            anchorPos = Vector3.zero;
            if (string.IsNullOrEmpty(myFaction))
                return false;

            try
            {
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

                        string spFact = sp.GetFaction();
                        if (string.IsNullOrEmpty(spFact) || spFact == "Civilian")
                            continue;

                        if (spFact != myFaction && !AreSameSideFactions(myFaction, spFact))
                            continue;

                        Vector3 raw = sp.GetSpawnPosition();
                        Vector3 d = raw - fromPos;
                        d.y = 0f;
                        float dSq = d.sqrMagnitude;
                        if (dSq < bestSq)
                        {
                            bestSq = dSq;
                            anchorPos = raw;
                            found = true;
                        }
                    }
                    if (found)
                        return true;
                }
            }
            catch { }

            return false;
        }

        /// <summary>
        /// Computes a rear evacuation point (~85m-125m toward friendly lines) for bailed-out tank crews and grounded aircrews
        /// across classic <see cref="BattlePhase"/> maps, <c>sp_ME_...</c> Mission Editor V2 maps (where <c>currentPhase == null</c>),
        /// out-of-bounds mesh terrain, and open ocean/carrier maps (when <paramref name="allowWaterSurfaceFallback"/> is true).
        /// </summary>
        public static bool TryComputeCrewRetreatDestination(
            string myFaction,
            Vector3 originPos,
            out Vector3 retreatPos,
            bool allowWaterSurfaceFallback = false)
        {
            retreatPos = Vector3.zero;
            if (string.IsNullOrEmpty(myFaction))
                return false;

            try
            {
                bool originIsDry = TrySampleDryGroundAltitude(originPos, out _);

                if (TryGetNearestFriendlySpawnPosition(myFaction, originPos, out Vector3 spawnPos))
                {
                    Vector3 toSpawn = spawnPos - originPos;
                    toSpawn.y = 0f;
                    float dist = toSpawn.magnitude;

                    if (dist >= 75.0f && dist <= 135.0f)
                    {
                        retreatPos = spawnPos;
                        return true;
                    }

                    if (dist > 135.0f)
                    {
                        Vector3 dir = toSpawn / dist;
                        if (TrySampleDryRetreatPoint(originPos, dir, 95.0f, out retreatPos) ||
                            TrySampleDryRetreatPoint(originPos, dir, 125.0f, out retreatPos) ||
                            TrySampleDryRetreatPoint(originPos, dir, 78.0f, out retreatPos) ||
                            TrySampleDryRetreatPoint(originPos, dir, 155.0f, out retreatPos))
                        {
                            return true;
                        }

                        // Pilot ditched or parachuted into open sea/lake far from shore: place a reachable 85m water-surface extraction zone!
                        if ((allowWaterSurfaceFallback || !originIsDry) && dist > 175.0f)
                        {
                            retreatPos = new Vector3(originPos.x + dir.x * 85.0f, 0.0f, originPos.z + dir.z * 85.0f);
                            return true;
                        }

                        retreatPos = spawnPos;
                        return true;
                    }

                    // Tank/vehicle was destroyed within 75m of the nearest friendly spawn: step >= 75m rearward so MissionTaskRetreat's 50m completion sphere doesn't trigger immediately
                    Vector3 spawnDir = dist > 1.0f ? (toSpawn / dist) : Vector3.zero;
                    bool hasEnemyDir = TryGetEnemySpawnDirection(myFaction, originPos, out Vector3 enemyDir);
                    Vector3 rearDir = hasEnemyDir
                        ? ((dist > 1.0f && Vector3.Dot(spawnDir, -enemyDir) > 0.25f) ? spawnDir : -enemyDir)
                        : (dist > 1.0f ? spawnDir : Vector3.back);

                    if (TrySampleDryRetreatPoint(originPos, rearDir, 95.0f, out retreatPos) ||
                        TrySampleDryRetreatPoint(originPos, rearDir, 120.0f, out retreatPos) ||
                        TrySampleDryRetreatPoint(originPos, rearDir, 78.0f, out retreatPos))
                    {
                        return true;
                    }

                    if (dist > 1.0f &&
                        (TrySampleDryRetreatPoint(originPos, spawnDir, 95.0f, out retreatPos) ||
                         TrySampleDryRetreatPoint(originPos, spawnDir, 78.0f, out retreatPos)))
                    {
                        return true;
                    }
                }

                // Carrier / Air-Superiority / Boat-Spawn fallback (where friendly spawns exist only in the sky or on ships):
                if (TryGetAnyFriendlySpawnHorizontalAnchor(myFaction, originPos, out Vector3 anySpawnAnchor))
                {
                    Vector3 toAnchor = anySpawnAnchor - originPos;
                    toAnchor.y = 0f;
                    float aDist = toAnchor.magnitude;
                    Vector3 aDir = aDist > 1.0f
                        ? (toAnchor / aDist)
                        : (TryGetEnemySpawnDirection(myFaction, originPos, out Vector3 eDir) ? -eDir : Vector3.back);

                    if (TrySampleDryRetreatPoint(originPos, aDir, 95.0f, out retreatPos) ||
                        TrySampleDryRetreatPoint(originPos, aDir, 78.0f, out retreatPos) ||
                        TrySampleDryRetreatPoint(originPos, aDir, 130.0f, out retreatPos))
                    {
                        return true;
                    }

                    if (allowWaterSurfaceFallback || !originIsDry)
                    {
                        retreatPos = new Vector3(originPos.x + aDir.x * 85.0f, 0.0f, originPos.z + aDir.z * 85.0f);
                        return true;
                    }
                }

                // Pure Lua / SpawnAISquadGlobal fallback when no SpawnManager exists in scene:
                EnsureWorldSnapshot();
                int sqCount = WorldSquadCount;
                var squads = WorldSquads;

                float bestFriendlySq = 75.0f * 75.0f;
                Vector3 bestFriendlyPos = Vector3.zero;
                bool hasFriendly = false;

                float nearestEnemySq = float.MaxValue;
                Vector3 nearestEnemyPos = Vector3.zero;
                bool hasEnemy = false;

                for (int i = 0; i < sqCount; i++)
                {
                    ref SquadSnapshot snap = ref squads[i];
                    if (snap.UnitCount == 0 || snap.RefOnPlane || string.IsNullOrEmpty(snap.Faction))
                        continue;

                    Vector3 delta = snap.RefPos - originPos;
                    delta.y = 0f;
                    float dSq = delta.sqrMagnitude;
                    if (AreSameSideFactions(myFaction, snap.Faction))
                    {
                        if (!snap.IsVehicleCrew && dSq > bestFriendlySq)
                        {
                            if (TrySampleDryGroundAltitude(snap.RefPos, out float alt))
                            {
                                bestFriendlySq = dSq;
                                bestFriendlyPos = new Vector3(snap.RefPos.x, alt, snap.RefPos.z);
                                hasFriendly = true;
                            }
                        }
                    }
                    else if (AreEnemyFactions(myFaction, snap.Faction) && dSq < nearestEnemySq)
                    {
                        nearestEnemySq = dSq;
                        nearestEnemyPos = snap.RefPos;
                        hasEnemy = true;
                    }
                }

                if (hasEnemy)
                {
                    Vector3 away = originPos - nearestEnemyPos;
                    if (TrySampleDryRetreatPoint(originPos, away, 95.0f, out retreatPos) ||
                        TrySampleDryRetreatPoint(originPos, away, 78.0f, out retreatPos) ||
                        TrySampleDryRetreatPoint(originPos, away, 120.0f, out retreatPos))
                    {
                        return true;
                    }

                    if (allowWaterSurfaceFallback || !originIsDry)
                    {
                        away.y = 0f;
                        Vector3 awayDir = away.sqrMagnitude > 0.01f ? away.normalized : Vector3.back;
                        retreatPos = new Vector3(originPos.x + awayDir.x * 85.0f, 0.0f, originPos.z + awayDir.z * 85.0f);
                        return true;
                    }
                }

                if (hasFriendly)
                {
                    retreatPos = bestFriendlyPos;
                    return true;
                }

                if (allowWaterSurfaceFallback || !originIsDry)
                {
                    retreatPos = new Vector3(originPos.x, 0.0f, originPos.z - 85.0f);
                    return true;
                }
            }
            catch { }

            return false;
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

        /// <summary>
        /// Returns <c>ResourcesManager.vaultMask | groundMask</c> (<c>Default | Terrain | InvisibleTrepassableWall</c>),
        /// which includes simplified invisible collision walls placed on sandbags, fences, parapets, and window sills.
        /// Use for physical movement, vaulting, and wall-cover geometry probes (keep <see cref="GetSolidEnvironmentMask"/>
        /// for optical sightlines and bullet LOS where <c>InvisibleTrepassableWall</c> is transparent).
        /// </summary>
        public static int GetMovementObstacleMask()
        {
            if (_cachedMovementObstacleMask != 0)
                return _cachedMovementObstacleMask;

            int mask = GetSolidEnvironmentMask();
            try
            {
                int vm = ResourcesManager.vaultMask.value;
                if (vm != 0)
                    mask |= vm;
            }
            catch { }

            int invWallLayer = LayerMask.NameToLayer("InvisibleTrepassableWall");
            if (invWallLayer >= 0)
                mask |= (1 << invWallLayer);

            _cachedMovementObstacleMask = mask;
            return mask;
        }

        public static bool IsMovementBlockedByObstacle(Vector3 origin, Vector3 dir, float dist)
        {
            if (dist <= 0.05f)
                return false;

            float dirSq = dir.sqrMagnitude;
            if (dirSq < 1e-6f)
                return false;

            Vector3 normDir = Mathf.Abs(dirSq - 1.0f) > 0.01f ? (dir / Mathf.Sqrt(dirSq)) : dir;
            int mask = GetMovementObstacleMask();

            if (Physics.Raycast(origin, normDir, out RaycastHit rayHit, dist, mask, QueryTriggerInteraction.Ignore) &&
                Mathf.Abs(rayHit.normal.y) < 0.68f)
            {
                return true;
            }

            // Catch wall corners, thin pillars, and doorframes within body radius without false-blocking walkable slopes/ramps.
            float sphereDist = Mathf.Max(0.10f, dist - 0.05f);
            if (Physics.SphereCast(origin + Vector3.up * 0.34f, 0.20f, normDir, out RaycastHit sphereHit, sphereDist, mask, QueryTriggerInteraction.Ignore) &&
                Mathf.Abs(sphereHit.normal.y) < 0.68f)
            {
                return true;
            }

            return false;
        }

        private static readonly Collider[] _stepOffCapsuleHits = new Collider[8];

        public static bool IsDirectSegmentBlockedByWall(Vector3 fromFeetPos, Vector3 toFeetPos, out float flatDist, bool ignoreVehicleObstacles = false)
        {
            Vector3 flatDelta = toFeetPos - fromFeetPos;
            flatDelta.y = 0f;
            float flatSq = flatDelta.sqrMagnitude;
            flatDist = flatSq > 1e-8f ? Mathf.Sqrt(flatSq) : 0f;

            // Reject multi-floor / foundation vertical drops regardless of horizontal distance
            // (closes the <= 0.15m pure-vertical drop loophole).
            if (Mathf.Abs(toFeetPos.y - fromFeetPos.y) > 0.85f)
                return true;

            int solidMask = GetSolidEnvironmentMask();
            Vector3 startLift = fromFeetPos + Vector3.up * 0.34f;
            Vector3 endLift = toFeetPos + Vector3.up * 0.34f;
            if ((Physics.Linecast(startLift, endLift, out RaycastHit fwdSlabHit, solidMask, QueryTriggerInteraction.Ignore) &&
                 (!ignoreVehicleObstacles || !IsVehicleCollider(fwdSlabHit.collider)) &&
                 (!IsTerrainCollider(fwdSlabHit.collider) || Mathf.Abs(fwdSlabHit.normal.y) < 0.62f)) ||
                (Physics.Linecast(endLift, startLift, out RaycastHit revSlabHit, solidMask, QueryTriggerInteraction.Ignore) &&
                 (!ignoreVehicleObstacles || !IsVehicleCollider(revSlabHit.collider)) &&
                 (!IsTerrainCollider(revSlabHit.collider) || Mathf.Abs(revSlabHit.normal.y) < 0.62f)))
            {
                return true;
            }

            if (flatSq <= 0.15f * 0.15f)
                return false;

            Vector3 dir = flatDelta / flatDist;
            if (ignoreVehicleObstacles)
            {
                return Physics.Raycast(fromFeetPos + Vector3.up * 0.48f, dir, out RaycastHit obsHit, flatDist, GetMovementObstacleMask(), QueryTriggerInteraction.Ignore) &&
                       Mathf.Abs(obsHit.normal.y) < 0.68f &&
                       !IsVehicleCollider(obsHit.collider);
            }
            return IsMovementBlockedByObstacle(fromFeetPos + Vector3.up * 0.48f, dir, flatDist);
        }

        /// <summary>
        /// Validates and surface-snaps a short direct micro-step for grounded AI bots (which run with
        /// <c>CharacterController</c> disabled and translate straight along <c>ai.path</c>'s 3D vector).
        /// Ensures walkable floor continuity at <paramref name="rawTargetPos"/>, snaps <c>.y</c> onto the
        /// real floor surface, and rejects any floor-slab, stair-riser, or wall penetration in either direction.
        /// </summary>
        public static bool TryValidateAndSnapDirectMicroStep(
            Vector3 fromFeetPos,
            Vector3 rawTargetPos,
            out Vector3 snappedTargetPos,
            out float flatDist,
            float maxFlatDist = 2.8f,
            float maxStepDeltaY = 0.75f,
            float maxStepDownDeltaY = -1f,
            bool ignoreVehicleObstacles = false)
        {
            snappedTargetPos = rawTargetPos;
            Vector3 flatDelta = rawTargetPos - fromFeetPos;
            flatDelta.y = 0f;
            float flatSq = flatDelta.sqrMagnitude;
            flatDist = flatSq > 1e-8f ? Mathf.Sqrt(flatSq) : 0f;

            bool steppingOffVehicleHull = IsFootBotStandingOnVehicleHull(fromFeetPos);
            bool ignoreVeh = steppingOffVehicleHull || ignoreVehicleObstacles;
            float effectiveMaxUpY = steppingOffVehicleHull ? Mathf.Max(maxStepDeltaY, 2.85f) : maxStepDeltaY;
            float requestedDownY = maxStepDownDeltaY > maxStepDeltaY ? maxStepDownDeltaY : maxStepDeltaY;
            float effectiveMaxDownY = steppingOffVehicleHull ? Mathf.Max(requestedDownY, 4.2f) : requestedDownY;
            float maxRawUpY = Mathf.Max(1.05f, effectiveMaxUpY + 0.35f);
            float maxRawDownY = Mathf.Max(1.05f, effectiveMaxDownY + 0.35f);

            float rawDeltaY = rawTargetPos.y - fromFeetPos.y;
            if (flatDist > maxFlatDist || rawDeltaY > maxRawUpY || rawDeltaY < -maxRawDownY)
                return false;

            // Sub-12cm in-place nudge: keep current floor Y so micro-jitter never drifts vertically.
            if (!steppingOffVehicleHull && flatSq <= 0.12f * 0.12f)
            {
                snappedTargetPos = new Vector3(rawTargetPos.x, fromFeetPos.y, rawTargetPos.z);
                return true;
            }

            int solidMask = GetSolidEnvironmentMask();
            float probeTopY = Mathf.Max(fromFeetPos.y, rawTargetPos.y) + 0.75f;
            float probeDist = (probeTopY - fromFeetPos.y) + effectiveMaxDownY + 0.15f;
            if (!Physics.Raycast(
                    new Vector3(rawTargetPos.x, probeTopY, rawTargetPos.z),
                    Vector3.down,
                    out RaycastHit floorHit,
                    probeDist,
                    solidMask,
                    QueryTriggerInteraction.Ignore) ||
                floorHit.normal.y < 0.58f ||
                IsVehicleCollider(floorHit.collider))
            {
                return false;
            }

            float snappedDeltaY = floorHit.point.y - fromFeetPos.y;
            if (snappedDeltaY > effectiveMaxUpY || snappedDeltaY < -effectiveMaxDownY)
                return false;

            snappedTargetPos = new Vector3(rawTargetPos.x, floorHit.point.y, rawTargetPos.z);

            Vector3 dir = flatDist > 1e-4f ? (flatDelta / flatDist) : Vector3.forward;

            // When intentionally stepping down off a >0.45m elevated ledge/trench rim/rubble/wreck/hull onto lower ground,
            // validate horizontal chest-height exit clearance, vertical drop-shaft clearance (blocking upper-floor slab penetration),
            // dry landing surface, and destination landing capsule instead of casting a diagonal +0.34m chord into the ledge lip.
            if (snappedDeltaY < -0.45f && (steppingOffVehicleHull || effectiveMaxDownY > 0.85f))
            {
                if (floorHit.collider.gameObject.layer == 4 ||
                    (TryGetActiveWaterLevel(out float dropWaterY) && floorHit.point.y <= dropWaterY + 0.15f))
                {
                    return false;
                }

                Vector3 chestStart = fromFeetPos + Vector3.up * 0.90f;
                Vector3 chestEnd = new Vector3(snappedTargetPos.x, chestStart.y, snappedTargetPos.z);
                Vector3 dropShaftBottom = snappedTargetPos + Vector3.up * 0.35f;

                if (flatDist > 0.12f)
                {
                    if ((Physics.Linecast(chestStart, chestEnd, out RaycastHit chestFwdHit, solidMask, QueryTriggerInteraction.Ignore) &&
                         (!ignoreVeh || !IsVehicleCollider(chestFwdHit.collider))) ||
                        (Physics.Linecast(chestEnd, chestStart, out RaycastHit chestRevHit, solidMask, QueryTriggerInteraction.Ignore) &&
                         (!ignoreVeh || !IsVehicleCollider(chestRevHit.collider))) ||
                        (!ignoreVeh && IsMovementBlockedByObstacle(chestStart, dir, Mathf.Min(flatDist, 2.2f))))
                    {
                        return false;
                    }
                }

                // Bidirectional vertical shaft check above the landing spot: guarantees bots on a 2nd-floor building slab
                // never step downward through an intermediate floor/ceiling mesh (regardless of mesh winding order).
                if ((Physics.Linecast(chestEnd, dropShaftBottom, out RaycastHit shaftDownHit, solidMask, QueryTriggerInteraction.Ignore) &&
                     (!ignoreVeh || !IsVehicleCollider(shaftDownHit.collider))) ||
                    (Physics.Linecast(dropShaftBottom, chestEnd, out RaycastHit shaftUpHit, solidMask, QueryTriggerInteraction.Ignore) &&
                     (!ignoreVeh || !IsVehicleCollider(shaftUpHit.collider))))
                {
                    return false;
                }

                if (ignoreVeh)
                {
                    int hitCount = Physics.OverlapCapsuleNonAlloc(
                        snappedTargetPos + Vector3.up * 0.54f,
                        snappedTargetPos + Vector3.up * 1.45f,
                        0.24f,
                        _stepOffCapsuleHits,
                        GetMovementObstacleMask(),
                        QueryTriggerInteraction.Ignore);
                    for (int hi = 0; hi < hitCount; hi++)
                    {
                        Collider c = _stepOffCapsuleHits[hi];
                        if (c != null && !IsVehicleCollider(c))
                            return false;
                    }
                }
                else if (Physics.CheckCapsule(
                        snappedTargetPos + Vector3.up * 0.54f,
                        snappedTargetPos + Vector3.up * 1.45f,
                        0.24f,
                        GetMovementObstacleMask(),
                        QueryTriggerInteraction.Ignore))
                {
                    return false;
                }

                return true;
            }

            // Bidirectional +0.34m chord check: catches one-sided Unity MeshCollider floor slabs/ceilings
            // both when stepping downward off/through an upper floor and upward under a stair/ceiling slab,
            // without false-blocking gentle convex TerrainCollider ground.
            Vector3 startLift = fromFeetPos + Vector3.up * 0.34f;
            Vector3 endLift = snappedTargetPos + Vector3.up * 0.34f;
            if ((Physics.Linecast(startLift, endLift, out RaycastHit fwdHit, solidMask, QueryTriggerInteraction.Ignore) &&
                 (!IsTerrainCollider(fwdHit.collider) || Mathf.Abs(fwdHit.normal.y) < 0.62f) &&
                 (!ignoreVeh || !IsVehicleCollider(fwdHit.collider))) ||
                (Physics.Linecast(endLift, startLift, out RaycastHit revHit, solidMask, QueryTriggerInteraction.Ignore) &&
                 (!IsTerrainCollider(revHit.collider) || Mathf.Abs(revHit.normal.y) < 0.62f) &&
                 (!ignoreVeh || !IsVehicleCollider(revHit.collider))))
            {
                return false;
            }

            if (flatDist > 0.12f && !ignoreVeh && IsMovementBlockedByObstacle(fromFeetPos + Vector3.up * 0.48f, dir, flatDist))
                return false;

            return true;
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

        public static bool TryGetActiveWaterLevel(out float waterLevel)
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
                        waterLevel = FollowingWater.WaterLevel;
                        return true;
                    }
                }
            }
            catch { }

            waterLevel = -9999f;
            return false;
        }

        public static bool IsVehicleInWater(Vehicle veh)
        {
            if (veh == null || veh.Pointer == IntPtr.Zero || veh.transform == null)
                return false;
            if (!TryGetActiveWaterLevel(out float waterY))
                return false;
            Vector3 pos = veh.transform.position;
            if (pos.y > waterY + 2.5f)
                return false;
            float terrY = ResourcesManager.GetTerrainAltitude(pos, pos.y);
            return terrY < waterY - 0.25f;
        }

        /// <summary>
        /// Checks whether an impact point is submerged below or across an active <see cref="FollowingWater"/>
        /// or Layer 4 (<c>Water</c>) surface, or occluded from <paramref name="camPos"/> by a tactical smoke volume
        /// (<c>visionMask &amp; ~bulletMask</c>).
        /// </summary>
        public static bool IsPointOccludedByWaterOrSmoke(Vector3 camPos, Vector3 impactPos)
        {
            if (TryGetActiveWaterLevel(out float waterY))
            {
                // Submerged flesh hit or sightline crossing the rendered water plane
                if (impactPos.y < waterY - 0.03f || (camPos.y - waterY) * (impactPos.y - waterY) < 0f)
                    return true;
            }

            int smokeMask = GetSmokeOcclusionMask();
            int lineMask = smokeMask | (1 << 4); // Smoke volumes (TransparentFX) + local Water colliders (Layer 4)
            float distSq = (impactPos - camPos).sqrMagnitude;
            if (distSq > 0.25f)
            {
                if (Physics.Linecast(camPos, impactPos, lineMask, QueryTriggerInteraction.Collide) ||
                    Physics.Linecast(camPos, impactPos + Vector3.up * 0.75f, lineMask, QueryTriggerInteraction.Collide) ||
                    Physics.Linecast(camPos, impactPos + Vector3.up * 1.35f, lineMask, QueryTriggerInteraction.Collide))
                {
                    return true;
                }
            }

            // PhysX Linecast starting inside a SphereCollider skips its own shell; CheckSphere catches inside-smoke hits
            // as well as low-body hits whose 1.5m blood mist cloud blooms into an adjacent smoke volume.
            if (distSq > 2.25f && Physics.CheckSphere(impactPos + Vector3.up * 0.45f, 1.65f, smokeMask, QueryTriggerInteraction.Collide))
                return true;

            if (distSq > 4.0f && Physics.CheckSphere(camPos, 0.35f, smokeMask, QueryTriggerInteraction.Collide))
                return true;

            return false;
        }

        public static bool IsPointInsideSmoke(Vector3 pos)
        {
            int smokeMask = GetSmokeOcclusionMask();
            return smokeMask != 0 &&
                   Physics.CheckSphere(pos + Vector3.up * 1.0f, 0.55f, smokeMask, QueryTriggerInteraction.Collide);
        }

        public static bool IsSegmentCrossingSmoke(Vector3 fromPos, Vector3 toPos)
        {
            int smokeMask = GetSmokeOcclusionMask();
            if (smokeMask == 0 || (toPos - fromPos).sqrMagnitude < 1.0f)
                return false;
            return Physics.Linecast(
                fromPos + Vector3.up * 1.15f,
                toPos + Vector3.up * 1.15f,
                smokeMask,
                QueryTriggerInteraction.Collide);
        }

        public static bool HasFriendlyEyesBeyondSmoke(Vector3 fromPos, Vector3 toPos, string myFaction, IntPtr selfPtr)
        {
            if (string.IsNullOrEmpty(myFaction))
                return false;

            float dx = toPos.x - fromPos.x;
            float dz = toPos.z - fromPos.z;
            float segFlatSq = dx * dx + dz * dz;
            if (segFlatSq < 1.0f)
                return false;

            float segDist = Mathf.Sqrt(segFlatSq);
            float invLen = 1.0f / segDist;
            float dirX = dx * invLen;
            float dirZ = dz * invLen;
            float minProjDist = Mathf.Max(4.0f, segDist * 0.55f);
            const float maxBeyondTargetSq = 26.0f * 26.0f;
            float now = Time.time;

            EnsureWorldSnapshot();
            var soldiers = WorldSoldiers;
            int count = WorldSoldierCount;
            for (int i = 0; i < count; i++)
            {
                ref SoldierSnapshot sSnap = ref soldiers[i];
                if (sSnap.Ptr == IntPtr.Zero ||
                    sSnap.Ptr == selfPtr ||
                    sSnap.IsInjured ||
                    sSnap.HasSurrendered ||
                    sSnap.IsOnVehicle ||
                    !AreSameSideFactions(myFaction, sSnap.Faction))
                {
                    continue;
                }

                if ((sSnap.Pos - toPos).sqrMagnitude > maxBeyondTargetSq)
                    continue;

                float relX = sSnap.Pos.x - fromPos.x;
                float relZ = sSnap.Pos.z - fromPos.z;
                float proj = relX * dirX + relZ * dirZ;
                if (proj < minProjDist)
                    continue;

                SoldierFastHandles fH = sSnap.Handles;
                if (fH != null && (fH.InSmokeCached || now < fH.Vision.UnderFireUntil))
                    continue;

                Soldier fSol = sSnap.Ref;
                if (!ReferenceEquals(fSol, null) && fSol != null && fSol.GetSuppressionValue() >= 30.0f)
                    continue;

                return true;
            }
            return false;
        }

        public static bool EvaluateBlindSmokeTraversalRisk(
            bool crossesSmokeAhead,
            bool alreadyInsideSmoke,
            bool hasFriendlyEyesBeyondSmoke,
            bool isEscapingHazardOrRetreating,
            bool isDesperateOvertimeOrGyokusai,
            float currentKnownHarm,
            out float effectiveTraversalHarm)
        {
            effectiveTraversalHarm = currentKnownHarm;
            if (!crossesSmokeAhead || alreadyInsideSmoke || isEscapingHazardOrRetreating)
                return false;

            if (hasFriendlyEyesBeyondSmoke)
                return false;

            float blindSmokeFloor = isDesperateOvertimeOrGyokusai ? 0.44f : 0.68f;
            if (effectiveTraversalHarm < blindSmokeFloor)
                effectiveTraversalHarm = blindSmokeFloor;

            return !isDesperateOvertimeOrGyokusai;
        }

        public static bool IsBlindSmokePathHighRisk(
            SoldierFastHandles h,
            Vector3 fromPos,
            Vector3 toPos,
            float now,
            bool isEscapingHazardOrRetreating,
            bool isDesperateOvertimeOrGyokusai,
            float currentKnownHarm,
            out float effectiveTraversalHarm)
        {
            effectiveTraversalHarm = currentKnownHarm;
            if (h == null || ReferenceEquals(h.Ref, null))
                return false;

            if (!IsSegmentCrossingSmoke(fromPos, toPos))
                return false;

            bool alreadyInsideSmoke = h.InSmokeCached || IsPointInsideSmoke(fromPos);
            if (alreadyInsideSmoke || isEscapingHazardOrRetreating)
                return false;

            string fac = GetCachedFaction(h, now);
            bool hasFriendlyEyes = !string.IsNullOrEmpty(fac) &&
                                   HasFriendlyEyesBeyondSmoke(fromPos, toPos, fac, h.Ptr);

            return EvaluateBlindSmokeTraversalRisk(
                crossesSmokeAhead: true,
                alreadyInsideSmoke: false,
                hasFriendlyEyesBeyondSmoke: hasFriendlyEyes,
                isEscapingHazardOrRetreating: false,
                isDesperateOvertimeOrGyokusai: isDesperateOvertimeOrGyokusai,
                currentKnownHarm: currentKnownHarm,
                out effectiveTraversalHarm);
        }

        public static bool RaycastEnvironment(
            Vector3 origin,
            Vector3 dir,
            float maxDist,
            Transform ignoreRoot,
            out RaycastHit envHit,
            bool ignoreVehicles = false)
        {
            // ResourcesManager.groundMask (Default | Terrain) hits only solid environment geometry (walls, buildings,
            // sandbags, terrain) and ignores soldier BodyPart colliders, ragdolls, items, foliage, and triggers.
            // Fast path uses a single native Raycast; only allocates from static scratch buffer if the nearest hit
            // belongs to ignoreRoot or (when ignoreVehicles is requested) a Vehicle collider.
            envHit = default;
            if (maxDist <= 0.001f)
                return false;

            int mask = GetSolidEnvironmentMask();
            if (!Physics.Raycast(origin, dir, out envHit, maxDist, mask, QueryTriggerInteraction.Ignore))
                return false;

            IntPtr ignoreRootPtr = (!ReferenceEquals(ignoreRoot, null) && ignoreRoot != null)
                ? ignoreRoot.Pointer
                : IntPtr.Zero;
            if (ignoreRootPtr == IntPtr.Zero && !ignoreVehicles)
                return true;

            if (!ShouldIgnoreEnvColliderHit(in envHit, ignoreRootPtr, ignoreVehicles))
                return true;

            if (_envRayScratch == null)
                _envRayScratch = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<RaycastHit>(16);

            int hitCount = Physics.RaycastNonAlloc(origin, dir, _envRayScratch, maxDist, mask, QueryTriggerInteraction.Ignore);
            float bestDistSq = float.MaxValue;
            bool found = false;
            RaycastHit bestHit = default;
            for (int i = 0; i < hitCount; i++)
            {
                RaycastHit cand = _envRayScratch[i];
                if (ShouldIgnoreEnvColliderHit(in cand, ignoreRootPtr, ignoreVehicles))
                    continue;

                float dSq = (cand.point - origin).sqrMagnitude;
                if (dSq < bestDistSq)
                {
                    bestDistSq = dSq;
                    bestHit = cand;
                    found = true;
                }
            }

            envHit = found ? bestHit : default;
            return found;
        }

        public static bool IsDirectionBlockedByEnvironment(
            Vector3 origin,
            Vector3 dir,
            float dist,
            Transform ignoreRoot,
            bool ignoreVehicles = false)
        {
            return RaycastEnvironment(origin, dir, dist, ignoreRoot, out _, ignoreVehicles);
        }

        public static bool LinecastEnvironment(
            Vector3 start,
            Vector3 end,
            Transform ignoreRoot,
            out RaycastHit envHit,
            bool ignoreVehicles = false)
        {
            Vector3 delta = end - start;
            float len = delta.magnitude;
            if (len <= 0.001f)
            {
                envHit = default;
                return false;
            }
            return RaycastEnvironment(start, delta / len, len, ignoreRoot, out envHit, ignoreVehicles);
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

        private static bool IsBridgeSideOpenDropOff(
            Vector3 feetPos,
            Vector3 horizDir,
            int mask,
            bool requireWater,
            bool hasActiveWater,
            float waterLevel)
        {
            // Check horizontal clearance above stone bridge parapets / steel rails (<= 2.2m) so bridge sides
            // still register as open drop-offs while tall building walls, cliffs, and rising hillsides return false.
            Vector3 horizOrigin = feetPos + Vector3.up * 2.25f;
            for (int k = 0; k < 3; k++)
            {
                float d = k == 0 ? 3.8f : (k == 1 ? 6.5f : 9.2f);
                if (Physics.Raycast(horizOrigin, horizDir, d, mask, QueryTriggerInteraction.Ignore))
                    return false;

                // Cast down 4.35m from +2.50m above feetPos (threshold = -1.85m below feetPos.y)
                Vector3 elevOrigin = new Vector3(feetPos.x + horizDir.x * d, feetPos.y + 2.50f, feetPos.z + horizDir.z * d);
                if (!Physics.Raycast(elevOrigin, Vector3.down, out RaycastHit downHit, 4.35f, mask, QueryTriggerInteraction.Ignore))
                {
                    if (!requireWater || (hasActiveWater && feetPos.y <= waterLevel + 5.5f))
                        return true;
                }
                else if (hasActiveWater && downHit.point.y <= waterLevel - 0.06f)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Safely checks whether <paramref name="col"/> is a Unity <c>TerrainCollider</c> without calling generic
        /// <c>TryCast&lt;TerrainCollider&gt;()</c> or <c>GetComponent&lt;TerrainCollider&gt;()</c>, because
        /// <c>UnityEngine.TerrainPhysicsModule</c> is stripped in ER2's IL2CPP build and generic casts throw
        /// <see cref="ArgumentException"/> ("UnityEngine.TerrainCollider is not an Il2Cpp reference type").
        /// </summary>
        public static bool IsTerrainCollider(Collider col)
        {
            if (ReferenceEquals(col, null) || col == null)
                return false;

            IntPtr ptr = col.Pointer;
            if (ptr == IntPtr.Zero)
                return false;

            if (_terrainColliderCache.TryGetValue(ptr, out bool cached))
                return cached;

            bool isTerrain = false;
            try
            {
                var il2cppType = col.GetIl2CppType();
                if (il2cppType != null)
                    isTerrain = string.Equals(il2cppType.Name, "TerrainCollider", StringComparison.Ordinal);
            }
            catch { }

            if (_terrainColliderCache.Count > 256)
                _terrainColliderCache.Clear();
            _terrainColliderCache[ptr] = isTerrain;
            return isTerrain;
        }

        private static ColliderEnvMeta GetColliderEnvMeta(Collider col)
        {
            if (ReferenceEquals(col, null) || col == null)
                return default;

            IntPtr ptr = col.Pointer;
            if (ptr == IntPtr.Zero)
                return default;

            if (_vehicleColliderCache.TryGetValue(ptr, out ColliderEnvMeta cached))
                return cached;

            ColliderEnvMeta meta = default;
            try
            {
                Transform tr = col.transform;
                Transform root = tr != null ? tr.root : null;
                if (root != null && root.Pointer != IntPtr.Zero)
                    meta.RootPtr = root.Pointer;

                Rigidbody rb = col.attachedRigidbody;
                if (rb != null && rb.GetComponent<Vehicle>() != null)
                {
                    meta.IsVehicle = true;
                }
                else if (tr != null)
                {
                    if (root != null && root.GetComponent<Vehicle>() != null)
                        meta.IsVehicle = true;
                    else if (col.GetComponentInParent<Vehicle>() != null)
                        meta.IsVehicle = true;
                }
            }
            catch { }

            if (_vehicleColliderCache.Count > 1024)
                _vehicleColliderCache.Clear();
            _vehicleColliderCache[ptr] = meta;
            return meta;
        }

        private static ColliderEnvMeta GetColliderEnvMetaFromHit(in RaycastHit hit)
        {
            int colId = hit.colliderInstanceID;
            if (colId != 0 && _vehicleColliderCacheById.TryGetValue(colId, out ColliderEnvMeta cached))
                return cached;

            ColliderEnvMeta meta = GetColliderEnvMeta(hit.collider);
            if (colId != 0)
            {
                if (_vehicleColliderCacheById.Count > 1024)
                    _vehicleColliderCacheById.Clear();
                _vehicleColliderCacheById[colId] = meta;
            }
            return meta;
        }

        private static bool ShouldIgnoreEnvColliderHit(in RaycastHit hit, IntPtr ignoreRootPtr, bool ignoreAllVehicles)
        {
            if (!ignoreAllVehicles && ignoreRootPtr == IntPtr.Zero)
                return false;

            ColliderEnvMeta meta = GetColliderEnvMetaFromHit(in hit);
            return (ignoreAllVehicles && meta.IsVehicle) ||
                   (ignoreRootPtr != IntPtr.Zero && meta.RootPtr == ignoreRootPtr);
        }

        /// <summary>
        /// Non-allocating octree query backed by a cleared reusable IL2CPP list.
        /// Callers must finish reading results before calling this helper again.
        /// </summary>
        public static Il2CppSystem.Collections.Generic.List<AiDestination> GetNearbyCoversNonAlloc(
            PointOctree<AiDestination> octree,
            Vector3 pos,
            float radius)
        {
            if (octree == null)
                return null;

            if (_sharedOctreeCoverResults == null)
                _sharedOctreeCoverResults = new Il2CppSystem.Collections.Generic.List<AiDestination>(64);
            else
                _sharedOctreeCoverResults.Clear();

            octree.GetNearbyNonAlloc(pos, radius, _sharedOctreeCoverResults);
            return _sharedOctreeCoverResults;
        }

        /// <summary>
        /// Pointer-cached check identifying colliders belonging to a <see cref="Vehicle"/> hull, turret,
        /// or chassis so foot bots never vault onto, or snap walkable ground destinations onto, vehicle roofs.
        /// </summary>
        public static bool IsVehicleCollider(Collider col) => GetColliderEnvMeta(col).IsVehicle;

        /// <summary>
        /// Zero-allocation <see cref="RaycastHit.colliderInstanceID"/> overload of <see cref="IsVehicleCollider"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsVehicleColliderHit(in RaycastHit hit) => GetColliderEnvMetaFromHit(in hit).IsVehicle;

        /// <summary>
        /// Returns <c>true</c> if a dismounted foot bot's feet are currently resting on top of a <see cref="Vehicle"/> collider.
        /// </summary>
        public static bool IsFootBotStandingOnVehicleHull(Vector3 feetPos)
        {
            return Physics.Raycast(
                       feetPos + Vector3.up * 0.45f,
                       Vector3.down,
                       out RaycastHit hit,
                       1.15f,
                       GetSolidEnvironmentMask(),
                       QueryTriggerInteraction.Ignore) &&
                   GetColliderEnvMetaFromHit(in hit).IsVehicle;
        }

        private static readonly Vector3 BridgeDiagNE = new Vector3(0.70710678f, 0f, 0.70710678f);
        private static readonly Vector3 BridgeDiagNW = new Vector3(0.70710678f, 0f, -0.70710678f);

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
                if (Physics.Raycast(feetPos + Vector3.up * 0.85f, Vector3.down, out RaycastHit centerHit, 2.0f, mask, QueryTriggerInteraction.Ignore) &&
                    centerHit.collider != null)
                {
                    bool isTerrain = IsTerrainCollider(centerHit.collider);
                    bool hasActiveWater = TryGetActiveWaterLevel(out float waterLevel);

                    // Dry TerrainCollider hillsides (like Kos slopes) are NEVER bridges or narrow crossings.
                    // Only allow TerrainCollider if near active water so true land-causeways still qualify.
                    if (!isTerrain || (hasActiveWater && centerHit.point.y <= waterLevel + 5.5f))
                    {
                        bool isBuildingRoof = false;
                        if (!isTerrain &&
                            Physics.Raycast(centerHit.point + Vector3.down * 0.45f, Vector3.down, out RaycastHit underHit, 5.5f, mask) &&
                            underHit.collider != null &&
                            underHit.distance >= 1.15f &&
                            !IsTerrainCollider(underHit.collider))
                        {
                            isBuildingRoof = true;
                        }

                        if (!isBuildingRoof)
                        {
                            bool dropX = IsBridgeSideOpenDropOff(feetPos, Vector3.right, mask, isTerrain, hasActiveWater, waterLevel) &&
                                         IsBridgeSideOpenDropOff(feetPos, Vector3.left, mask, isTerrain, hasActiveWater, waterLevel);
                            bool dropZ = IsBridgeSideOpenDropOff(feetPos, Vector3.forward, mask, isTerrain, hasActiveWater, waterLevel) &&
                                         IsBridgeSideOpenDropOff(feetPos, Vector3.back, mask, isTerrain, hasActiveWater, waterLevel);
                            if (dropX ^ dropZ)
                            {
                                isBridge = true;
                            }
                            else if (!dropX && !dropZ)
                            {
                                bool dropD1 = IsBridgeSideOpenDropOff(feetPos, BridgeDiagNE, mask, isTerrain, hasActiveWater, waterLevel) &&
                                              IsBridgeSideOpenDropOff(feetPos, -BridgeDiagNE, mask, isTerrain, hasActiveWater, waterLevel);
                                bool dropD2 = IsBridgeSideOpenDropOff(feetPos, BridgeDiagNW, mask, isTerrain, hasActiveWater, waterLevel) &&
                                              IsBridgeSideOpenDropOff(feetPos, -BridgeDiagNW, mask, isTerrain, hasActiveWater, waterLevel);
                                if (dropD1 ^ dropD2)
                                    isBridge = true;
                            }
                        }
                    }
                }
            }

            if (_bridgeCellCache.Count > 2048)
                _bridgeCellCache.Clear();
            _bridgeCellCache[cellKey] = isBridge;
            return isBridge;
        }

        /// <summary>
        /// Checks whether the soldier is currently on, targeting, or approaching within <c>10.5m</c> of a bridge
        /// or narrow crossing along their active path so vanilla's sideways squad-spacing scatter is cancelled
        /// before it can pull bots down embankment ravines adjacent to bridge approaches.
        /// </summary>
        public static bool IsApproachingOrOnBridge(SoldierAI ai, SoldierFastHandles h, Vector3 myPos, Vector3 moveDest)
        {
            if (IsOnBridgeOrNarrowCrossing(myPos) || IsOnBridgeOrNarrowCrossing(moveDest))
                return true;

            Vector3 probeTarget = moveDest;
            if (ai != null && h != null)
            {
                A.d path = GetCachedAiPath(h, ai);
                if (!ReferenceEquals(path, null) && path.Pointer != IntPtr.Zero && path.NodesCount >= 1)
                {
                    Vector3 nodePt = path.CurrentNode.point;
                    if (IsOnBridgeOrNarrowCrossing(nodePt))
                        return true;

                    Vector3 toNode = nodePt - myPos;
                    toNode.y = 0f;
                    if (toNode.sqrMagnitude > 10.5f * 10.5f)
                        probeTarget = nodePt;
                }
            }

            Vector3 toProbe = probeTarget - myPos;
            toProbe.y = 0f;
            float probeDistSq = toProbe.sqrMagnitude;
            if (probeDistSq > 10.5f * 10.5f)
            {
                Vector3 dir = toProbe / Mathf.Sqrt(probeDistSq);
                Vector3 probeXZ = myPos + dir * 10.5f;
                if (Physics.Raycast(
                        new Vector3(probeXZ.x, myPos.y + 4.0f, probeXZ.z),
                        Vector3.down,
                        out RaycastHit aheadHit,
                        9.0f,
                        GetSolidEnvironmentMask(),
                        QueryTriggerInteraction.Ignore) &&
                    IsOnBridgeOrNarrowCrossing(aheadHit.point))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Verifies that a candidate cover/bounding position for a soldier currently on a bridge or narrow crossing
        /// lands squarely on walkable deck geometry at roughly the same elevation (not off an edge, into water, or inside a pillar).
        /// </summary>
        public static bool IsSafeBridgeDeckStep(Vector3 currentFeetPos, Vector3 candidatePos, out Vector3 snappedGroundPos)
        {
            snappedGroundPos = candidatePos;
            int mask = GetSolidEnvironmentMask();
            Vector3 probeOrigin = new Vector3(candidatePos.x, currentFeetPos.y + 1.35f, candidatePos.z);
            if (!Physics.Raycast(probeOrigin, Vector3.down, out RaycastHit hit, 2.50f, mask, QueryTriggerInteraction.Ignore))
                return false;

            if (hit.normal.y < 0.65f || Mathf.Abs(hit.point.y - currentFeetPos.y) > 1.15f)
                return false;

            if (hit.collider == null || hit.collider.gameObject.layer == 4)
                return false;

            if (TryGetActiveWaterLevel(out float waterLevel) && hit.point.y <= waterLevel + 0.15f)
                return false;

            if (Physics.CheckSphere(hit.point + Vector3.up * 0.65f, 0.26f, mask, QueryTriggerInteraction.Ignore))
                return false;

            snappedGroundPos = hit.point;
            return true;
        }

        /// <summary>
        /// Casts downward onto solid environment geometry from <c>(probePos.x, currentFeetPos.y + liftY, probePos.z)</c>
        /// and validates slope walkability, active water clearance, non-terrain rooftop/drop rejection
        /// (<c>|Δy| &gt; maxNonTerrainDeltaY</c> while preserving full <c>TerrainCollider</c> hillside climbs and
        /// probing beneath overhead bridge decks / upper-floor slabs when unobstructed at <c>+0.60m</c>),
        /// and standing capsule clearance against solid + <c>InvisibleTrepassableWall</c> collider interiors.
        /// </summary>
        public static bool TrySnapWalkableGroundProbe(
            Vector3 currentFeetPos,
            Vector3 probePos,
            float liftY,
            float maxRayDist,
            float minNormalY,
            float maxNonTerrainDeltaY,
            bool hasWater,
            float waterLevel,
            out Vector3 snappedGroundPos,
            float maxNonTerrainDownDeltaY = -1f)
        {
            snappedGroundPos = probePos;
            int envMask = GetSolidEnvironmentMask();
            float topY = currentFeetPos.y + liftY;
            if (!Physics.Raycast(
                    new Vector3(probePos.x, topY, probePos.z),
                    Vector3.down,
                    out RaycastHit gHit,
                    maxRayDist,
                    envMask,
                    QueryTriggerInteraction.Ignore))
            {
                return false;
            }

            Collider gCol = gHit.collider;
            int obsMask = 0;
            bool verifiedTerrain = false;

            if (IsVehicleCollider(gCol))
            {
                float vehSubDist = (gHit.point.y - 0.15f) - (topY - maxRayDist);
                if (vehSubDist <= 0.05f ||
                    !Physics.Raycast(
                        new Vector3(probePos.x, gHit.point.y - 0.15f, probePos.z),
                        Vector3.down,
                        out RaycastHit vehSubHit,
                        vehSubDist,
                        envMask,
                        QueryTriggerInteraction.Ignore) ||
                    (gHit.point.y - vehSubHit.point.y) < 1.90f)
                {
                    return false;
                }

                gHit = vehSubHit;
                gCol = vehSubHit.collider;
                if (IsVehicleCollider(gCol))
                    return false;
            }

            // If an elevated ray (lifted high enough for steep >45 deg TerrainCollider climbs) hits an overhead
            // non-terrain roof, 2nd-floor slab, or bridge deck above currentFeetPos.y + maxNonTerrainDeltaY,
            // probe beneath it before checking roof slope so indoor 1st-floor halls and under-bridge/canopy spans
            // still resolve walkable ground while sealed sheds/buildings remain blocked by their exterior walls.
            if (gHit.point.y - currentFeetPos.y > maxNonTerrainDeltaY)
            {
                if (IsTerrainCollider(gCol))
                {
                    verifiedTerrain = true;
                }
                else
                {
                    float overheadY = gHit.point.y;
                    float subTopY = currentFeetPos.y + maxNonTerrainDeltaY;
                    float subMaxDist = subTopY - (topY - maxRayDist);
                    if (subMaxDist <= 0.05f ||
                        !Physics.Raycast(
                            new Vector3(probePos.x, subTopY, probePos.z),
                            Vector3.down,
                            out RaycastHit subHit,
                            subMaxDist,
                            envMask,
                            QueryTriggerInteraction.Ignore))
                    {
                        return false;
                    }

                    if (overheadY - subHit.point.y < 1.90f)
                        return false;

                    obsMask = GetMovementObstacleMask();
                    Vector3 fromLift = currentFeetPos + Vector3.up * 0.60f;
                    Vector3 toLift = subHit.point + Vector3.up * 0.60f;
                    if ((Physics.Linecast(fromLift, toLift, out RaycastHit fwdWallHit, obsMask, QueryTriggerInteraction.Ignore) &&
                         (!IsTerrainCollider(fwdWallHit.collider) || Mathf.Abs(fwdWallHit.normal.y) < 0.62f)) ||
                        (Physics.Linecast(toLift, fromLift, out RaycastHit revWallHit, obsMask, QueryTriggerInteraction.Ignore) &&
                         (!IsTerrainCollider(revWallHit.collider) || Mathf.Abs(revWallHit.normal.y) < 0.62f)))
                    {
                        return false;
                    }

                    gHit = subHit;
                    gCol = subHit.collider;
                    if (IsVehicleCollider(gCol))
                        return false;
                }
            }

            if (gHit.normal.y < minNormalY)
                return false;

            if (hasWater && gHit.point.y <= waterLevel + 0.15f)
                return false;

            // Reject unreachable building/shed/bunker rooftops and deep cellar/canal drops on non-terrain
            // colliders while allowing full TerrainCollider hillside climbs/descents.
            float effectiveDownLimit = maxNonTerrainDownDeltaY > maxNonTerrainDeltaY ? maxNonTerrainDownDeltaY : maxNonTerrainDeltaY;
            float deltaY = gHit.point.y - currentFeetPos.y;
            if (!verifiedTerrain &&
                (deltaY > maxNonTerrainDeltaY || deltaY < -effectiveDownLimit) &&
                !IsTerrainCollider(gCol))
            {
                return false;
            }

            // Reject collider interiors (solid BoxColliders where an elevated ray started inside the volume,
            // crates, boulders, low overhangs, and InvisibleTrepassableWall sandbag/fence boxes).
            // Lower sphere center is at +0.54m (0.54 * 0.50 = 0.27m > 0.24m radius) so steep 55-60 deg
            // TerrainCollider hillsides (minNormalY >= 0.50) never self-intersect the ground plane.
            if (obsMask == 0)
                obsMask = GetMovementObstacleMask();
            if (Physics.CheckCapsule(
                    gHit.point + Vector3.up * 0.54f,
                    gHit.point + Vector3.up * 1.45f,
                    0.24f,
                    obsMask,
                    QueryTriggerInteraction.Ignore))
            {
                return false;
            }

            snappedGroundPos = gHit.point;
            return true;
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


        // =====================================================================
        // Zero-Allocation Human Player Aim-Lane Cache (SP + Co-op Crosshair Yield)
        // Refreshed at 12.5 Hz (0.08s) so friendly bots avoid claiming cover or
        // squad spacing inside a human player's sights and proactively duck/yield.
        // =====================================================================
        public struct PlayerAimLane
        {
            public IntPtr Ptr;
            public Soldier Ref;
            public Transform RootTr;
            public string Faction;
            public Vector3 FeetPos;
            public Vector3 AimOrigin;
            public Vector3 AimDir3D;
            public Vector3 AimDirFlat;
            public Vector3 RightFlat;
            public float MaxClearForward;
            public float HalfCorridorWidth;
            public bool IsAdsOrFiring;
            public bool IsProne;
            public bool IsCrouched;
        }

        public static PlayerAimLane[] ActivePlayerAimLanes = new PlayerAimLane[32];
        public static int ActivePlayerAimLaneCount;
        private static float _nextPlayerAimLaneRefreshTime;

        public struct VehicleDangerCorridor
        {
            public IntPtr VehiclePtr;
            public Vehicle Ref;
            public string Faction;
            public Vector3 HullCenter;
            public Vector3 TravelDirFlat;
            public Vector3 RightFlat;
            public float HalfWidth;
            public float HalfLength;
            public float SpeedMs;
            public float MinFwd;
            public float MaxFwd;
            public float CorridorHalfWidth;
            public bool IsTank;
            public bool IsPlayerDriven;
            public bool IsReversing;
        }

        public static VehicleDangerCorridor[] ActiveVehicleDangerCorridors = new VehicleDangerCorridor[32];
        public static int ActiveVehicleDangerCorridorCount;
        private static float _nextVehicleDangerCorridorRefreshTime;

        public struct AudibleGroundVehicle
        {
            public IntPtr VehiclePtr;
            public string Faction;
            public Vector3 Pos;
            public float MaxSoundDistance;
            public float SpeedMs;
            public bool IsTank;
            public bool IsMoving;
        }

        public static AudibleGroundVehicle[] ActiveAudibleGroundVehicles = new AudibleGroundVehicle[32];
        public static int ActiveAudibleGroundVehicleCount;

        public static void EnsureVehicleDangerCorridorsUpdated(float now)
        {
            if (now < _nextVehicleDangerCorridorRefreshTime && now >= _nextVehicleDangerCorridorRefreshTime - 2.0f)
                return;

            _nextVehicleDangerCorridorRefreshTime = now + 0.10f;
            int prevCount = ActiveVehicleDangerCorridorCount;
            ActiveVehicleDangerCorridorCount = 0;
            int prevAudibleCount = ActiveAudibleGroundVehicleCount;
            ActiveAudibleGroundVehicleCount = 0;

            try
            {
                var allVehicles = Vehicle.allVehicles;
                if (allVehicles != null)
                {
                    int vCount = allVehicles.Count;
                    for (int i = 0; i < vCount; i++)
                    {
                        Vehicle v = allVehicles[i];
                        if (ReferenceEquals(v, null) || v == null || v.Pointer == IntPtr.Zero || !v.CanFight())
                            continue;

                        VehicleFastHandles vh = GetOrCreateVehicleHandles(v);
                        if (vh == null || !vh.IsWheeledOrTank)
                            continue;

                        Transform tr = vh.Tr != null ? vh.Tr : v.transform;
                        if (tr == null)
                            continue;

                        Vector3 vWorldPos = tr.position;
                        if (vh.LastTrackedWorldPosTime > 0f && now > vh.LastTrackedWorldPosTime)
                        {
                            float dt = now - vh.LastTrackedWorldPosTime;
                            if (dt >= 0.04f && dt <= 0.60f)
                            {
                                Vector3 instVel = new Vector3((vWorldPos.x - vh.LastTrackedWorldPos.x) / dt, 0f, (vWorldPos.z - vh.LastTrackedWorldPos.z) / dt);
                                if (instVel.sqrMagnitude > 45.0f * 45.0f)
                                    vh.EstimatedFlatVel = Vector3.zero;
                                else
                                    vh.EstimatedFlatVel = vh.EstimatedFlatVel.sqrMagnitude < 0.04f
                                        ? instVel
                                        : Vector3.Lerp(vh.EstimatedFlatVel, instVel, 0.65f);
                            }
                            else if (dt > 0.60f)
                            {
                                vh.EstimatedFlatVel = Vector3.zero;
                            }
                        }
                        else if (vh.LastTrackedWorldPosTime <= 0f || now < vh.LastTrackedWorldPosTime)
                        {
                            vh.EstimatedFlatVel = Vector3.zero;
                        }
                        vh.LastTrackedWorldPos = vWorldPos;
                        vh.LastTrackedWorldPosTime = now;

                        string vehFaction = VeteranAndTacticsPatches.GetVehicleCrewFaction(v);
                        if (string.IsNullOrEmpty(vehFaction))
                            continue;

                        Vector3 fwd = tr.forward;
                        fwd.y = 0f;
                        fwd = fwd.sqrMagnitude > 0.001f ? fwd.normalized : Vector3.forward;

                        Vector3 right = tr.right;
                        right.y = 0f;
                        right = right.sqrMagnitude > 0.001f ? right.normalized : new Vector3(fwd.z, 0f, -fwd.x);

                        MovableVehicle mv = vh.Movable;
                        float speedMs = mv != null ? Mathf.Abs(mv.GetCurrentSpeed()) : 0f;
                        Rigidbody rb = GetCachedVehicleRb(vh);
                        Vector3 rbVel = rb != null ? rb.velocity : Vector3.zero;
                        float rbFlatSpeed = Mathf.Sqrt(rbVel.x * rbVel.x + rbVel.z * rbVel.z);

                        Vector3 estVel = vh.EstimatedFlatVel;
                        float estFlatSpeed = Mathf.Sqrt(estVel.x * estVel.x + estVel.z * estVel.z);
                        Vector3 bestVel = rbFlatSpeed >= 0.50f ? rbVel : estVel;
                        float bestVelSpeed = rbFlatSpeed >= 0.50f ? rbFlatSpeed : estFlatSpeed;
                        float effSpeed = Mathf.Max(speedMs, bestVelSpeed);

                        bool isPlayerDriven = IsAnyHumanPlayerDriving(v);
                        bool isHumanControlled = isPlayerDriven ||
                            (IsInMultiplayerRoom() && !IsLocallyOwnedVehicle(v) && v.HasDriver() && IsAnyHumanPlayerInsideVehicle(v));
                        AIVehicle aiVeh = vh.AiVeh;
                        bool isReversing = (aiVeh != null && aiVeh.going_in_retro) ||
                                           (bestVelSpeed >= 0.35f && Vector3.Dot(new Vector3(bestVel.x / bestVelSpeed, 0f, bestVel.z / bestVelSpeed), fwd) < -0.35f) ||
                                           (isPlayerDriven && v.movingDir.y < -0.10f);
                        A.d vehPath = aiVeh != null && !aiVeh.placingInUnstuck && !isReversing && !aiVeh.stopToShoot
                            ? GetCachedAiVehiclePath(vh, aiVeh)
                            : null;
                        bool aiMovingIntent = aiVeh != null &&
                                              !aiVeh.placingInUnstuck &&
                                              (isReversing ||
                                               (!aiVeh.stopToShoot &&
                                                vehPath != null &&
                                                vehPath.NodesCount > 0 &&
                                                now >= aiVeh.resumeDriveTime - 0.50f));

                        bool hullPivoting = false;
                        if (vh.IsTank && effSpeed < 0.45f)
                        {
                            VehicleTank tank = vh.Tank;
                            if (tank != null && Mathf.Abs(tank.curRotationSpeed) >= 0.15f)
                                hullPivoting = true;
                        }

                        if (!vh.EngineSoundDistChecked && mv != null)
                        {
                            vh.EngineSoundDistChecked = true;
                            int msd = mv.MaxSoundDistance;
                            vh.CachedEngineMaxSoundDist = msd > 10 ? (float)msd : (vh.IsTank ? 150.0f : 90.0f);
                        }

                        bool isVehMoving = effSpeed >= 0.45f || hullPivoting;
                        bool engineRunning = mv != null && (isVehMoving || (v.HasDriver() && (mv.engineStarted || mv.engineStarting || mv.engine_looping)));
                        if (engineRunning && TryEnsureArrayCapacity(ref ActiveAudibleGroundVehicles, ActiveAudibleGroundVehicleCount + 1, 256))
                        {
                            ActiveAudibleGroundVehicles[ActiveAudibleGroundVehicleCount++] = new AudibleGroundVehicle
                            {
                                VehiclePtr = v.Pointer,
                                Faction = vehFaction,
                                Pos = vWorldPos,
                                MaxSoundDistance = vh.CachedEngineMaxSoundDist > 10f ? vh.CachedEngineMaxSoundDist : (vh.IsTank ? 150f : 90f),
                                SpeedMs = effSpeed,
                                IsTank = vh.IsTank,
                                IsMoving = isVehMoving
                            };
                        }

                        if (effSpeed < 0.45f && !isHumanControlled && !aiMovingIntent && !hullPivoting)
                            continue;

                        Vector3 travelDir = bestVelSpeed >= 0.45f
                            ? new Vector3(bestVel.x / bestVelSpeed, 0f, bestVel.z / bestVelSpeed)
                            : (isReversing ? -fwd : fwd);
                        Vector3 travelRight = new Vector3(travelDir.z, 0f, -travelDir.x);

                        Vector4 obb = VeteranAndTacticsPatches.GetCachedVehicleLocalHullObb(v);
                        float halfWidth = obb.x;
                        float halfLength = obb.y;
                        Vector3 hullCenter = vWorldPos + right * obb.z + fwd * obb.w;

                        bool stationaryHuman = isHumanControlled && effSpeed < 0.35f && Mathf.Abs(v.movingDir.y) < 0.08f && !hullPivoting;
                        bool pivotOnly = !isHumanControlled && effSpeed < 0.45f && !aiMovingIntent && hullPivoting;
                        float minFwd = stationaryHuman ? (-halfLength - 0.8f) : (pivotOnly ? (-halfLength - 1.2f) : (-halfLength * 0.40f));
                        float maxFwd = stationaryHuman
                            ? (halfLength + 4.5f)
                            : (pivotOnly ? (halfLength + 2.6f) : Mathf.Clamp(halfLength + (vh.IsTank ? 6.5f : 5.5f) + effSpeed * 1.15f, 6.5f, 18.5f));
                        float corridorHalfWidth = stationaryHuman
                            ? (halfWidth + 1.15f)
                            : (halfWidth + (vh.IsTank ? 1.85f : 1.45f));

                        if (!TryEnsureArrayCapacity(ref ActiveVehicleDangerCorridors, ActiveVehicleDangerCorridorCount + 1, 256))
                            break;

                        ActiveVehicleDangerCorridors[ActiveVehicleDangerCorridorCount++] = new VehicleDangerCorridor
                        {
                            VehiclePtr = v.Pointer,
                            Ref = v,
                            Faction = vehFaction,
                            HullCenter = hullCenter,
                            TravelDirFlat = travelDir,
                            RightFlat = travelRight,
                            HalfWidth = halfWidth,
                            HalfLength = halfLength,
                            SpeedMs = effSpeed,
                            MinFwd = minFwd,
                            MaxFwd = maxFwd,
                            CorridorHalfWidth = corridorHalfWidth,
                            IsTank = vh.IsTank,
                            IsPlayerDriven = isHumanControlled,
                            IsReversing = isReversing
                        };
                    }
                }
            }
            catch { }

            if (prevCount > ActiveVehicleDangerCorridorCount)
                Array.Clear(ActiveVehicleDangerCorridors, ActiveVehicleDangerCorridorCount, prevCount - ActiveVehicleDangerCorridorCount);
            if (prevAudibleCount > ActiveAudibleGroundVehicleCount)
                Array.Clear(ActiveAudibleGroundVehicles, ActiveAudibleGroundVehicleCount, prevAudibleCount - ActiveAudibleGroundVehicleCount);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsPointInFriendlyVehicleDangerCorridor(Vector3 worldPoint, string faction, float extraWidth = 0f)
        {
            if (string.IsNullOrEmpty(faction))
                return false;

            EnsureVehicleDangerCorridorsUpdated(Time.time);
            int count = ActiveVehicleDangerCorridorCount;
            if (count == 0)
                return false;

            for (int i = 0; i < count; i++)
            {
                ref VehicleDangerCorridor c = ref ActiveVehicleDangerCorridors[i];
                if (!AreSameSideFactions(faction, c.Faction))
                    continue;
                if (Mathf.Abs(worldPoint.y - c.HullCenter.y) > 3.2f)
                    continue;

                float dx = worldPoint.x - c.HullCenter.x;
                float dz = worldPoint.z - c.HullCenter.z;
                float fwd = dx * c.TravelDirFlat.x + dz * c.TravelDirFlat.z;
                if (fwd < c.MinFwd || fwd > c.MaxFwd)
                    continue;

                float lat = Mathf.Abs(dx * c.RightFlat.x + dz * c.RightFlat.z);
                if (lat <= c.CorridorHalfWidth + extraWidth)
                    return true;
            }

            return false;
        }

        public static bool TryGetBlockingFriendlyVehicleDangerCorridor(
            Vector3 worldPoint,
            string faction,
            float extraWidth,
            out VehicleDangerCorridor corridor,
            out float forwardDist,
            out float signedLateral,
            out Vector3 clearStepDir)
        {
            EnsureVehicleDangerCorridorsUpdated(Time.time);
            int count = ActiveVehicleDangerCorridorCount;
            for (int i = 0; i < count; i++)
            {
                ref VehicleDangerCorridor c = ref ActiveVehicleDangerCorridors[i];
                if (!AreSameSideFactions(faction, c.Faction))
                    continue;
                if (Mathf.Abs(worldPoint.y - c.HullCenter.y) > 3.2f)
                    continue;

                float dx = worldPoint.x - c.HullCenter.x;
                float dz = worldPoint.z - c.HullCenter.z;
                float fwd = dx * c.TravelDirFlat.x + dz * c.TravelDirFlat.z;
                if (fwd < c.MinFwd || fwd > c.MaxFwd)
                    continue;

                float latSigned = dx * c.RightFlat.x + dz * c.RightFlat.z;
                if (Mathf.Abs(latSigned) <= c.CorridorHalfWidth + extraWidth)
                {
                    corridor = c;
                    forwardDist = fwd;
                    signedLateral = latSigned;
                    float sideSign = latSigned >= 0f ? 1f : -1f;
                    clearStepDir = c.RightFlat * sideSign;
                    return true;
                }
            }

            corridor = default;
            forwardDist = 0f;
            signedLateral = 0f;
            clearStepDir = Vector3.zero;
            return false;
        }

        public static bool TryGetNearestAudibleEnemyVehicle(
            Vector3 observerPos,
            string observerFaction,
            float maxQueryRadius,
            float now,
            out Vector3 vehiclePos,
            out float vehicleDist,
            out bool isTank,
            out bool isMoving)
        {
            vehiclePos = Vector3.zero;
            vehicleDist = -1f;
            isTank = false;
            isMoving = false;
            if (string.IsNullOrEmpty(observerFaction) || maxQueryRadius <= 0f)
                return false;

            EnsureVehicleDangerCorridorsUpdated(now);
            int count = ActiveAudibleGroundVehicleCount;
            if (count <= 0)
                return false;

            float rain01 = RainIntensity01;
            bool loudCombatNear = VisionPatches.HasAnyLoudEventNear(observerPos, 38.0f, 3.0f, now);
            float bestRankSq = float.MaxValue;
            float bestTrueDistSq = 0f;
            bool found = false;

            for (int i = 0; i < count; i++)
            {
                ref AudibleGroundVehicle av = ref ActiveAudibleGroundVehicles[i];
                if (!AreEnemyFactions(observerFaction, av.Faction))
                    continue;

                float dx = av.Pos.x - observerPos.x;
                float dy = av.Pos.y - observerPos.y;
                float dz = av.Pos.z - observerPos.z;
                float distSq = dx * dx + dy * dy + dz * dz;
                if (distSq <= 0.25f)
                    continue;

                float audibleR = VisionPatches.ComputeVehicleEngineAudibleRadius(av.MaxSoundDistance, av.IsTank, av.IsMoving, rain01, loudCombatNear);
                float effR = Mathf.Min(maxQueryRadius, audibleR);
                if (distSq > effR * effR)
                    continue;

                float rankSq = distSq * (av.IsTank ? (av.IsMoving ? 0.72f : 1.05f) : (av.IsMoving ? 0.95f : 1.25f));
                if (rankSq < bestRankSq)
                {
                    bestRankSq = rankSq;
                    bestTrueDistSq = distSq;
                    vehiclePos = av.Pos;
                    isTank = av.IsTank;
                    isMoving = av.IsMoving;
                    found = true;
                }
            }

            if (!found)
                return false;

            vehicleDist = Mathf.Sqrt(bestTrueDistSq);
            return true;
        }
        private static Camera _cachedMainCamera;
        private static float _nextMainCameraLookupTime;

        private static Camera GetCachedMainCamera(float now)
        {
            if (!ReferenceEquals(_cachedMainCamera, null) && _cachedMainCamera != null && _cachedMainCamera.isActiveAndEnabled)
                return _cachedMainCamera;

            if (now < _nextMainCameraLookupTime)
                return null;

            _nextMainCameraLookupTime = now + 1.0f;
            _cachedMainCamera = Camera.main;
            return (!ReferenceEquals(_cachedMainCamera, null) && _cachedMainCamera != null && _cachedMainCamera.isActiveAndEnabled)
                ? _cachedMainCamera
                : null;
        }

        /// <summary>
        /// Returns true if <paramref name="worldPos"/> is within <paramref name="minPlayerDist"/> of any active human player
        /// (local or co-op) or unobstructed inside the local camera frustum, so off-camera unstick snaps never pop in visibly.
        /// </summary>
        public static bool IsPositionVisibleOnLocalScreen(Vector3 worldPos, float minPlayerDist = 20.0f)
        {
            EnsurePlayerAimLanesUpdated();
            float minDistSq = minPlayerDist * minPlayerDist;
            for (int i = 0; i < ActivePlayerAimLaneCount; i++)
            {
                if ((worldPos - ActivePlayerAimLanes[i].FeetPos).sqrMagnitude < minDistSq)
                    return true;
            }

            Camera cam = GetCachedMainCamera(Time.time);
            if (cam == null || cam.transform == null)
                return false;

            Vector3 camPos = cam.transform.position;
            if ((worldPos - camPos).sqrMagnitude < minDistSq)
                return true;

            Vector3 targetChest = worldPos + Vector3.up * 0.95f;
            Vector3 vp = cam.WorldToViewportPoint(targetChest);
            if (vp.z > 0.0f && vp.x >= -0.06f && vp.x <= 1.06f && vp.y >= -0.06f && vp.y <= 1.06f)
            {
                if (!Physics.Linecast(camPos, targetChest, GetSolidEnvironmentMask(), QueryTriggerInteraction.Ignore))
                    return true;
            }

            return false;
        }

        private static void TryAppendPlayerAimLane(Soldier p, SoldierFastHandles h, bool isLocalPlayer, float now)
        {
            if (h == null ||
                h.Tr == null ||
                p == null ||
                !p.IsAlive ||
                p.HasSurrended())
            {
                return;
            }

            string faction = GetCachedFaction(h, now);
            if (string.IsNullOrEmpty(faction))
                return;

            bool onGroundVeh = false;
            Transform rootTr = h.RootTr;
            if (p.IsOnVehicle())
            {
                VehicleFastHandles vh = GetOrCreateVehicleHandles(p.GetCurrentVehicle());
                if (vh == null || vh.IsAir || vh.IsParachute)
                    return;

                onGroundVeh = true;
                if (vh.RootTr != null)
                    rootTr = vh.RootTr;
            }

            Vector3 feetPos = h.Tr.position;
            Vector3 aimOrigin;
            Vector3 aimDir3D;

            if (onGroundVeh && isLocalPlayer)
            {
                Camera cam = GetCachedMainCamera(now);
                if (cam != null && cam.transform != null)
                {
                    aimOrigin = cam.transform.position;
                    aimDir3D = cam.transform.forward;
                }
                else
                {
                    aimOrigin = p.GunPosition();
                    aimDir3D = p.GetFireDir();
                }
            }
            else
            {
                aimOrigin = p.GunPosition();
                aimDir3D = p.GetFireDir();
            }

            if (aimDir3D.sqrMagnitude < 0.001f)
                aimDir3D = h.Tr.forward;
            else
                aimDir3D.Normalize();

            Vector3 aimFlat = new Vector3(aimDir3D.x, 0f, aimDir3D.z);
            if (aimFlat.sqrMagnitude < 0.001f)
            {
                Vector3 trFwd = h.Tr.forward;
                aimFlat = new Vector3(trFwd.x, 0f, trFwd.z);
            }
            if (aimFlat.sqrMagnitude < 0.001f)
                return;

            aimFlat.Normalize();
            Vector3 rightFlat = new Vector3(aimFlat.z, 0f, -aimFlat.x);

            bool adsOrFiring = onGroundVeh ||
                               p.IsAiming ||
                               p.IsAutoCoverAimActive ||
                               p.pressTriggerRequest ||
                               VisionPatches.HasFiredRecently(p, 2.2f);
            SoldierPose pose = onGroundVeh ? SoldierPose.Crouch : p.m_pose;

            if (!TryEnsureArrayCapacity(ref ActivePlayerAimLanes, ActivePlayerAimLaneCount + 1, 256))
                return;

            ActivePlayerAimLanes[ActivePlayerAimLaneCount++] = new PlayerAimLane
            {
                Ptr = h.Ptr,
                Ref = p,
                RootTr = rootTr,
                Faction = faction,
                FeetPos = feetPos,
                AimOrigin = aimOrigin,
                AimDir3D = aimDir3D,
                AimDirFlat = aimFlat,
                RightFlat = rightFlat,
                MaxClearForward = adsOrFiring ? 16.5f : 9.5f,
                HalfCorridorWidth = adsOrFiring ? 1.35f : 0.95f,
                IsAdsOrFiring = adsOrFiring,
                IsProne = pose == SoldierPose.Prone,
                IsCrouched = pose == SoldierPose.Crouch
            };
        }

        public static void EnsurePlayerAimLanesUpdated()
        {
            float now = Time.time;
            if (now >= _nextPlayerAimLaneRefreshTime || now < _nextPlayerAimLaneRefreshTime - 2.0f)
            {
                _nextPlayerAimLaneRefreshTime = now + 0.08f;
                int prevCount = ActivePlayerAimLaneCount;
                ActivePlayerAimLaneCount = 0;

                try
                {
                    Soldier localPlayer = Soldier.CurrentControlledSoldierOrNull();
                    IntPtr localPtr = IntPtr.Zero;
                    if (!ReferenceEquals(localPlayer, null) && localPlayer != null && localPlayer.IsAlive)
                    {
                        SoldierFastHandles lpH = GetOrCreateSoldierHandles(localPlayer);
                        if (lpH != null)
                        {
                            localPtr = lpH.Ptr;
                            TryAppendPlayerAimLane(localPlayer, lpH, isLocalPlayer: true, now);
                        }
                    }

                    if (IsInMultiplayerRoom())
                    {
                        EnsureWorldSnapshot();
                        int solCount = WorldSoldierCount;
                        var soldiers = WorldSoldiers;
                        for (int i = 0; i < solCount; i++)
                        {
                            ref SoldierSnapshot snap = ref soldiers[i];
                            if (!snap.IsPlayer || snap.Ptr == localPtr || snap.Ref == null)
                                continue;

                            SoldierFastHandles rpH = GetOrCreateSoldierHandles(snap.Ref);
                            TryAppendPlayerAimLane(snap.Ref, rpH, isLocalPlayer: false, now);
                        }
                    }
                }
                catch { }

                if (prevCount > ActivePlayerAimLaneCount)
                    Array.Clear(ActivePlayerAimLanes, ActivePlayerAimLaneCount, prevCount - ActivePlayerAimLaneCount);
            }
        }

        /// <summary>
        /// Zero-allocation, zero-raycast cover filter (~15ns) used in CombatCover.IsCoverAvailable
        /// and local cover searches so friendly bots never claim cover spots inside a human player's sightline.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsCoverInPlayerAimLane(Vector3 coverPos, Vector3 coverShootDirFlat)
        {
            EnsurePlayerAimLanesUpdated();
            int count = ActivePlayerAimLaneCount;
            if (count == 0)
                return false;

            for (int i = 0; i < count; i++)
            {
                ref PlayerAimLane lane = ref ActivePlayerAimLanes[i];
                if (Mathf.Abs(coverPos.y - lane.FeetPos.y) > 2.2f)
                    continue;

                // Only reject cover searches facing the same general sector as the player (friendly cover);
                // opposing enemy cover searches have shootDirection pointing back toward the player (dot < 0).
                if (coverShootDirFlat.sqrMagnitude > 0.001f && Vector3.Dot(lane.AimDirFlat, coverShootDirFlat) < 0.05f)
                    continue;

                float dx = coverPos.x - lane.AimOrigin.x;
                float dz = coverPos.z - lane.AimOrigin.z;
                float fwd = dx * lane.AimDirFlat.x + dz * lane.AimDirFlat.z;
                if (fwd < 0.35f || fwd > lane.MaxClearForward)
                    continue;

                float rayY = lane.AimOrigin.y + lane.AimDir3D.y * fwd;
                if (rayY > coverPos.y + 2.35f || rayY < coverPos.y - 0.55f)
                    continue;

                float lat = Mathf.Abs(dx * lane.RightFlat.x + dz * lane.RightFlat.z);
                if (lat <= lane.HalfCorridorWidth + 0.20f + fwd * 0.03f)
                    return true;
            }

            return false;
        }

        public static bool TryGetBlockingFriendlyPlayerAimLane(
            IntPtr botPtr,
            string botFaction,
            Vector3 worldPoint,
            float extraWidth,
            out PlayerAimLane blockingLane,
            out float forwardDist,
            out float signedLateral,
            out Vector3 clearStepDir)
        {
            EnsurePlayerAimLanesUpdated();
            int count = ActivePlayerAimLaneCount;
            for (int i = 0; i < count; i++)
            {
                ref PlayerAimLane lane = ref ActivePlayerAimLanes[i];
                if (lane.Ptr == botPtr || !AreSameSideFactions(botFaction, lane.Faction))
                    continue;

                if (Mathf.Abs(worldPoint.y - lane.FeetPos.y) > 2.30f)
                    continue;

                float dx = worldPoint.x - lane.AimOrigin.x;
                float dz = worldPoint.z - lane.AimOrigin.z;
                float fwd = dx * lane.AimDirFlat.x + dz * lane.AimDirFlat.z;
                if (fwd < 0.30f || fwd > lane.MaxClearForward)
                    continue;

                // Ignore when the player is aiming steep overhead (e.g. AA fire at aircraft) or steep below
                float rayY = lane.AimOrigin.y + lane.AimDir3D.y * fwd;
                if (rayY > worldPoint.y + 2.35f || rayY < worldPoint.y - 0.55f)
                    continue;

                float sLat = dx * lane.RightFlat.x + dz * lane.RightFlat.z;
                float halfWidth = lane.HalfCorridorWidth + extraWidth + fwd * 0.035f;
                if (Mathf.Abs(sLat) <= halfWidth)
                {
                    blockingLane = lane;
                    forwardDist = fwd;
                    signedLateral = sLat;
                    if (Mathf.Abs(sLat) > 0.08f)
                        clearStepDir = sLat >= 0f ? lane.RightFlat : -lane.RightFlat;
                    else
                        clearStepDir = ((GetDeterministicHash(botPtr, 97) & 1) == 0) ? lane.RightFlat : -lane.RightFlat;
                    return true;
                }
            }

            blockingLane = default;
            forwardDist = 0f;
            signedLateral = 0f;
            clearStepDir = Vector3.zero;
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
                localPlayer.Pointer != shooterPtr)
            {
                SoldierFastHandles lpHandles = GetOrCreateSoldierHandles(localPlayer);
                string lpFaction = GetCachedFaction(lpHandles, Time.time);
                if (lpHandles != null &&
                    lpHandles.Tr != null &&
                    !string.IsNullOrEmpty(lpFaction) &&
                    AreSameSideFactions(myFaction, lpFaction))
                {
                    Vehicle pVeh = localPlayer.IsOnVehicle() ? localPlayer.GetCurrentVehicle() : null;
                    if (pVeh == null || !pVeh.IsAirVehicle())
                    {
                        Vector3 dP = lpHandles.Tr.position - impactPos;
                        if (dP.x * dP.x + dP.z * dP.z <= playerSafeSq && Mathf.Abs(dP.y) <= 22.0f)
                            return true;
                    }
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

                if (!AreSameSideFactions(myFaction, sqSnap.Faction))
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
            public byte ActiveVehicleMarkerKind;
        }

        public struct SoldierSnapshot
        {
            public IntPtr Ptr;
            public Soldier Ref;
            public SoldierFastHandles Handles;
            public IntPtr SquadPtr;
            public Vector3 Pos;
            public string Faction;
            public bool IsPlayer;
            public bool IsOnVehicle;
            public bool IsSquadLeader;
            public bool IsRadioman;
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
        public static readonly int[] WorldAirSquadIndices = new int[32];
        public static int WorldAirSquadCount;

        private static readonly Dictionary<IntPtr, int> _worldSquadIndexByPtr = new Dictionary<IntPtr, int>(96);
        private static readonly HashSet<IntPtr> _seenWorldSoldierPtrs = new HashSet<IntPtr>(384);
        private static float _lastWorldSnapshotTime = -999f;
        private static float _nextAnomalyWatchdogScanTime;

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

        public static bool IsEnemyAircraftOverhead(Vector3 groundPos, string myFaction, float horizRadius = 260f, float minAltitudeDelta = 12f)
        {
            if (WorldAirSquadCount <= 0 || string.IsNullOrEmpty(myFaction))
                return false;

            float rSq = horizRadius * horizRadius;
            for (int i = 0; i < WorldAirSquadCount; i++)
            {
                int sqIdx = WorldAirSquadIndices[i];
                if (sqIdx < 0 || sqIdx >= WorldSquadCount)
                    continue;

                ref SquadSnapshot sqSnap = ref WorldSquads[sqIdx];
                if (!sqSnap.RefOnPlane || sqSnap.UnitCount <= 0 || string.IsNullOrEmpty(sqSnap.Faction))
                    continue;

                if (!AreEnemyFactions(myFaction, sqSnap.Faction))
                    continue;

                float dy = sqSnap.RefPos.y - groundPos.y;
                if (dy < minAltitudeDelta || dy > 550f)
                    continue;

                float dx = sqSnap.RefPos.x - groundPos.x;
                float dz = sqSnap.RefPos.z - groundPos.z;
                if (dx * dx + dz * dz <= rSq)
                    return true;
            }
            return false;
        }

        public static void WarmWorldSnapshotIfDue()
        {
            if (!VisionConfig.EnableMod.Value || Time.timeSinceLevelLoad < 2.0f || IsHeavyWorkFrame() || Time.unscaledDeltaTime > 0.026f)
                return;

            float now = Time.time;
            if (_lastWorldSnapshotTime < 0f || (now - _lastWorldSnapshotTime) >= 0.32f)
            {
                EnsureWorldSnapshot(0.32f);
            }
        }

        public static void EnsureWorldSnapshot(float maxAge = 0.35f)
        {
            float now = Time.time;
            if (now >= _lastWorldSnapshotTime && _lastWorldSnapshotTime > 0f)
            {
                float age = now - _lastWorldSnapshotTime;
                if (age < maxAge)
                    return;

                // If heavy spatial/octree work already ran on this frame OR the previous frame just hitched (> 35ms),
                // reuse the slightly older snapshot so the O(N) snapshot rebuild lands on a clean frame!
                if ((IsHeavyWorkFrame() && age < maxAge + 0.30f) ||
                    (Time.unscaledDeltaTime > 0.035f && age < maxAge + 0.55f))
                {
                    return;
                }
            }

            NotifyDirectorWorkThisFrame();
            long profSnapStart = HitchProfiler.Begin();
            HitchProfiler.RecordWorldSnapshotRebuilt();
            _lastWorldSnapshotTime = now;
            FlushExpiredRetreatDespawningSoldiers(now);
            int prevSqCount = WorldSquadCount;
            int prevSolCount = WorldSoldierCount;
            int prevInjCount = WorldInjuredSoldierCount;
            WorldSquadCount = 0;
            WorldSoldierCount = 0;
            WorldInjuredSoldierCount = 0;
            WorldAirSquadCount = 0;
            CachedLocalPlayerHandles = null;
            CachedLocalPlayerSquadPtr = IntPtr.Zero;
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
                        SoldierFastHandles leaderHandles = (!ReferenceEquals(leader, null) && leader != null) ? GetOrCreateSoldierHandles(leader) : null;
                        bool leaderAlive = leaderHandles != null && leader.IsAlive && leaderHandles.Tr != null;

                        Soldier refSoldier = leaderAlive ? leader : null;
                        SoldierFastHandles refHandles = leaderAlive ? leaderHandles : null;
                        int unitStart = WorldSoldierCount;
                        int addedUnits = 0;
                        bool isPlayerInSq = HasAnyHumanPlayerInSquad(sq);
                        bool hasAliveRadiomanNear = false;
                        float maxRadDistSq = isPlayerInSq ? (95f * 95f) : (60f * 60f);
                        Vector3 leaderPos = leaderAlive ? leaderHandles.Tr.position : Vector3.zero;
                        byte activeVehMarkerKind = VehicleMarkerKindNone;

                        for (int i = 0; i < uCount; i++)
                        {
                            Soldier u = units[i];
                            if (ReferenceEquals(u, null) || u == null || u.Pointer == IntPtr.Zero)
                                continue;

                            SoldierFastHandles uHandles = GetOrCreateSoldierHandles(u);
                            if (uHandles == null || uHandles.Tr == null)
                                continue;

                            if (!u.IsAlive)
                            {
                                // In Easy Red 2, incapacitated casualties waiting for a medic have IsAlive == false (life_total == 0)
                                // and IsInjured == true (!IsDead). Track them in WorldInjuredSoldiers while keeping WorldSoldiers strictly IsAlive!
                                if (u.IsInjured && !u.IsDead && _seenWorldSoldierPtrs.Add(uHandles.Ptr))
                                {
                                    if (WorldInjuredSoldierCount >= WorldInjuredSoldiers.Length)
                                        Array.Resize(ref WorldInjuredSoldiers, WorldInjuredSoldiers.Length * 2);

                                    WorldInjuredSoldiers[WorldInjuredSoldierCount++] = new SoldierSnapshot
                                    {
                                        Ptr = uHandles.Ptr,
                                        Ref = u,
                                        Handles = uHandles,
                                        SquadPtr = sq.Pointer,
                                        Pos = uHandles.Tr.position,
                                        Faction = GetCachedFaction(uHandles, now),
                                        IsPlayer = IsAnyHumanPlayerSoldier(uHandles),
                                        IsOnVehicle = u.IsOnVehicle(),
                                        IsSquadLeader = u.IsSquadLeader(),
                                        IsRadioman = u.IsRadioman(),
                                        IsInjured = true
                                    };
                                }
                                continue;
                            }

                            if (refSoldier == null)
                            {
                                refSoldier = u;
                                refHandles = uHandles;
                            }

                            Vector3 uPos = uHandles.Tr.position;
                            bool isHuman = IsAnyHumanPlayerSoldier(uHandles);
                            bool isOnVeh = u.IsOnVehicle();
                            Vehicle uVeh = isOnVeh ? u.GetCurrentVehicle() : null;
                            VehicleFastHandles uVh = uVeh != null ? GetOrCreateVehicleHandles(uVeh) : null;
                            if (uVeh != null && uVh != null && !uVeh.IsDisabled())
                            {
                                byte vKind = GetVehicleMarkerKind(uVeh, uVh);
                                if (vKind > activeVehMarkerKind)
                                    activeVehMarkerKind = vKind;
                            }
                            if (isHuman)
                            {
                                if (u.IsPlayer())
                                {
                                    CachedLocalPlayerHandles = uHandles;
                                    CachedLocalPlayerSquadPtr = sq.Pointer;
                                }
                                float curYaw = uHandles.Tr.eulerAngles.y;
                                bool vehActive = isOnVeh && (!u.IsPlayer() || !u.IsAI());
                                if (uHandles.LastHumanActiveTime <= 0f ||
                                    now < uHandles.LastHumanActiveTime ||
                                    (uPos - uHandles.LastHumanActivePos).sqrMagnitude > 0.75f * 0.75f ||
                                    Mathf.Abs(Mathf.DeltaAngle(curYaw, uHandles.LastHumanActiveYaw)) > 10.0f ||
                                    u.isInHandsAnimation ||
                                    u.isInBodyAnimation)
                                {
                                    uHandles.LastHumanActivePos = uPos;
                                    uHandles.LastHumanActiveYaw = curYaw;
                                    uHandles.LastHumanActiveTime = now;
                                }
                                if (vehActive && uVh != null)
                                {
                                    uVh.LastHumanOccupant = u;
                                    uVh.LastHumanInsideTime = now;
                                }
                            }
                            bool uSurr = IsTrueSurrenderedPrisoner(u, uHandles);
                            if (radioNetEnabled && !hasAliveRadiomanNear && u.IsRadioman() && !uSurr)
                            {
                                if (!leaderAlive || (uPos - leaderPos).sqrMagnitude <= maxRadDistSq)
                                    hasAliveRadiomanNear = true;
                            }

                            if (WorldSoldierCount >= WorldSoldiers.Length)
                                Array.Resize(ref WorldSoldiers, WorldSoldiers.Length * 2);

                            _seenWorldSoldierPtrs.Add(uHandles.Ptr);
                            WorldSoldiers[WorldSoldierCount++] = new SoldierSnapshot
                            {
                                Ptr = uHandles.Ptr,
                                Ref = u,
                                Handles = uHandles,
                                SquadPtr = sq.Pointer,
                                Pos = uPos,
                                Faction = GetCachedFaction(uHandles, now),
                                IsPlayer = isHuman,
                                IsOnVehicle = isOnVeh,
                                IsSquadLeader = leaderAlive && uHandles.Ptr == leaderHandles.Ptr,
                                IsRadioman = u.IsRadioman(),
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

                        if (refSoldier == null || refHandles == null)
                            continue;

                        Vector3 refPos = leaderAlive ? leaderPos : refHandles.Tr.position;
                        string faction = GetCachedFaction(refHandles, now);
                        bool leaderOnVeh = leaderAlive && leader.IsOnVehicle();
                        bool leaderOnGroundVeh = false;
                        bool leaderHasOnboardRadio = false;
                        if (leaderOnVeh)
                        {
                            VehicleFastHandles lvh = GetOrCreateVehicleHandles(leader.GetCurrentVehicle());
                            if (lvh != null && !lvh.IsParachute)
                            {
                                leaderHasOnboardRadio = true;
                                if (!lvh.IsAir)
                                    leaderOnGroundVeh = true;
                            }
                        }

                        bool refOnPlane = false;
                        if (refSoldier.IsOnVehicle())
                        {
                            VehicleFastHandles rvh = GetOrCreateVehicleHandles(refSoldier.GetCurrentVehicle());
                            if (rvh != null && rvh.IsAir)
                                refOnPlane = true;
                        }

                        bool hasIntrinsicRadio = !radioNetEnabled ||
                                                 leaderHasOnboardRadio ||
                                                 hasAliveRadiomanNear ||
                                                 (leaderAlive && sq.RadioRequestActiveAndConfirmed());

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
                            IsVehicleCrew = IsTrueVehicleCrewSquad(sq),
                            IsPlayerInSquad = isPlayerInSq,
                            LeaderOnVehicle = leaderOnVeh,
                            LeaderOnGroundVehicle = leaderOnGroundVeh,
                            RefOnPlane = refOnPlane,
                            HasIntrinsicRadio = hasIntrinsicRadio,
                            HasRadioOrRelay = hasIntrinsicRadio,
                            IsSpottedOrAware = spottedOrAware,
                            ActiveVehicleMarkerKind = activeVehMarkerKind
                        };

                        if (refOnPlane && WorldAirSquadCount < WorldAirSquadIndices.Length)
                            WorldAirSquadIndices[WorldAirSquadCount++] = sqIdx;
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
                                if (i == j || !WorldSquads[j].LeaderIsAlive || !AreSameSideFactions(myFaction, WorldSquads[j].Faction))
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
                    // joinedSquad was cleared by vanilla Soldier.Surrender(), or lone players/pilots).
                    // Discover detached creatures every 1.05s and reuse cached handles on intermediate 0.35s snapshots
                    // to eliminate 130+ Creature.allCreatures[i] wrapper allocations per snapshot!
                    if (now >= _nextDetachedCreatureScanTime)
                    {
                        _nextDetachedCreatureScanTime = now + 1.05f;
                        _cachedDetachedSoldiers.Clear();
                        var allCreatures = Creature.allCreatures;
                        if (allCreatures != null)
                        {
                            int cCount = allCreatures.Count;
                            for (int cIdx = 0; cIdx < cCount; cIdx++)
                            {
                                Creature c = allCreatures[cIdx];
                                if (ReferenceEquals(c, null) || c == null || c.Pointer == IntPtr.Zero || _seenWorldSoldierPtrs.Contains(c.Pointer))
                                    continue;

                                if (TryGetSoldierHandlesFromCreature(c, out SoldierFastHandles dh) && dh.Tr != null)
                                    _cachedDetachedSoldiers.Add(dh);
                            }
                        }
                    }

                    for (int dIdx = 0; dIdx < _cachedDetachedSoldiers.Count; dIdx++)
                    {
                        SoldierFastHandles dh = _cachedDetachedSoldiers[dIdx];
                        if (dh == null || ReferenceEquals(dh.Ref, null) || dh.Ref == null || dh.Tr == null || _seenWorldSoldierPtrs.Contains(dh.Ptr))
                            continue;

                        Soldier u = dh.Ref;
                        if (!u.IsAlive)
                        {
                            if (u.IsInjured && !u.IsDead && _seenWorldSoldierPtrs.Add(dh.Ptr))
                            {
                                if (WorldInjuredSoldierCount >= WorldInjuredSoldiers.Length)
                                    Array.Resize(ref WorldInjuredSoldiers, WorldInjuredSoldiers.Length * 2);

                                WorldInjuredSoldiers[WorldInjuredSoldierCount++] = new SoldierSnapshot
                                {
                                    Ptr = dh.Ptr,
                                    Ref = u,
                                    Handles = dh,
                                    SquadPtr = u.joinedSquad != null ? u.joinedSquad.Pointer : IntPtr.Zero,
                                    Pos = dh.Tr.position,
                                    Faction = GetCachedFaction(dh, now),
                                    IsPlayer = IsAnyHumanPlayerSoldier(dh),
                                    IsOnVehicle = u.IsOnVehicle(),
                                    IsInjured = true
                                };
                            }
                            continue;
                        }

                        if (!_seenWorldSoldierPtrs.Add(dh.Ptr))
                            continue;

                        if (WorldSoldierCount >= WorldSoldiers.Length)
                            Array.Resize(ref WorldSoldiers, WorldSoldiers.Length * 2);

                        WorldSoldiers[WorldSoldierCount++] = new SoldierSnapshot
                        {
                            Ptr = dh.Ptr,
                            Ref = u,
                            Handles = dh,
                            SquadPtr = u.joinedSquad != null ? u.joinedSquad.Pointer : IntPtr.Zero,
                            Pos = dh.Tr.position,
                            Faction = GetCachedFaction(dh, now),
                            IsPlayer = IsAnyHumanPlayerSoldier(dh),
                            IsOnVehicle = u.IsOnVehicle(),
                            IsSquadLeader = false,
                            HasSurrendered = IsTrueSurrenderedPrisoner(u, dh),
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
            catch
            {
            }

            if (prevSqCount > WorldSquadCount)
                Array.Clear(WorldSquads, WorldSquadCount, prevSqCount - WorldSquadCount);
            if (prevSolCount > WorldSoldierCount)
                Array.Clear(WorldSoldiers, WorldSoldierCount, prevSolCount - WorldSoldierCount);
            if (prevInjCount > WorldInjuredSoldierCount)
                Array.Clear(WorldInjuredSoldiers, WorldInjuredSoldierCount, prevInjCount - WorldInjuredSoldierCount);
            AuditWorldSoldiersForAnomalies(now);
            HitchProfiler.End(HitchBucket.WorldSnapshot, profSnapStart);
        }

        private static float _nextVehicleAnomalyScanTime;

        private static void AuditVehiclesForAnomalies(float now)
        {
            if (now < _nextVehicleAnomalyScanTime && (now + 5.0f) >= _nextVehicleAnomalyScanTime)
                return;

            _nextVehicleAnomalyScanTime = now + 2.0f;
            try
            {
                var allVehicles = Vehicle.allVehicles;
                if (allVehicles == null)
                    return;

                int vCount = allVehicles.Count;
                for (int i = 0; i < vCount; i++)
                {
                    Vehicle v = allVehicles[i];
                    if (ReferenceEquals(v, null) || v == null || v.Pointer == IntPtr.Zero ||
                        v.vehicleDisabled || v.IsDisabled() || v.peopleInside <= 0)
                    {
                        continue;
                    }

                    VehicleFastHandles vh = GetOrCreateVehicleHandles(v);
                    if (vh == null || !vh.IsWheeledOrTank || vh.IsStaticEmplacement ||
                        vh.Tr == null || !IsLocallyOwnedVehicle(v))
                    {
                        continue;
                    }

                    AIVehicle aiVeh = GetOrCacheAIVehicle(v, vh);
                    if (aiVeh == null || aiVeh.hasEnemy || aiVeh.wasBeingRepaired ||
                        aiVeh.stopToShoot || !aiVeh.destinationActive || aiVeh.DestinationReached)
                    {
                        vh.WatchdogStationarySince = 0f;
                        continue;
                    }

                    Soldier driverOrOccupant = null;
                    bool hasHuman = false;
                    var seats = v.seats;
                    if (seats != null)
                    {
                        int sc = seats.Length;
                        for (int sIdx = 0; sIdx < sc; sIdx++)
                        {
                            Seats seat = seats[sIdx];
                            if (seat == null)
                                continue;
                            Soldier occ = seat.unitSet;
                            if (occ == null || occ.Pointer == IntPtr.Zero || !occ.IsAlive)
                                continue;
                            if (occ.IsPlayer())
                            {
                                hasHuman = true;
                                break;
                            }
                            if (driverOrOccupant == null || seat.isDriver)
                                driverOrOccupant = occ;
                        }
                    }

                    if (hasHuman || driverOrOccupant == null)
                    {
                        vh.WatchdogStationarySince = 0f;
                        continue;
                    }

                    Vector3 pos = vh.Tr.position;
                    Vector3 dest = aiVeh.setVehicleDestination;
                    if (dest.sqrMagnitude < 1.0f || (dest - pos).sqrMagnitude <= 30.0f * 30.0f)
                    {
                        vh.WatchdogStationarySince = 0f;
                        continue;
                    }

                    if (vh.WatchdogStationarySince <= 0f ||
                        now < vh.WatchdogStationarySince ||
                        (pos - vh.WatchdogAnchorPos).sqrMagnitude > 1.5f * 1.5f)
                    {
                        vh.WatchdogAnchorPos = pos;
                        vh.WatchdogStationarySince = now;
                    }
                    else if (now - vh.WatchdogStationarySince > 25.0f && now >= vh.NextAnomalyAlertTime)
                    {
                        vh.NextAnomalyAlertTime = now + 20.0f;
                        AnomalyVehicleStuckCount++;
                        float remDist = Vector3.Distance(pos, dest);
                        float stuckDur = now - vh.WatchdogStationarySince;
                        BotActionTrace.RecordEvent(driverOrOccupant, BotTraceEventKind.AnomalyAlert, "vehicle_stuck_25s", remDist, stuckDur);
                        LogDebug("VehWatchdog", $"{v.name} stuck {stuckDur:F0}s ({remDist:F0}m from MoveDestination).", 6.0f);
                    }
                }
            }
            catch (Exception ex)
            {
                ReportPatchException("Watchdog.Veh", ex);
            }
        }

        private static void AuditWorldSoldiersForAnomalies(float now)
        {
            if (now < _nextAnomalyWatchdogScanTime && (now + 5.0f) >= _nextAnomalyWatchdogScanTime)
                return;

            _nextAnomalyWatchdogScanTime = now + 1.05f;
            bool traceEnabled = BotActionTrace.IsEnabled;
            AuditVehiclesForAnomalies(now);

            for (int i = 0; i < WorldSoldierCount; i++)
            {
                ref SoldierSnapshot ws = ref WorldSoldiers[i];
                Soldier u = ws.Ref;
                SoldierFastHandles h = ws.Handles;
                if (ReferenceEquals(u, null) || u == null || u.Pointer == IntPtr.Zero || h == null)
                    continue;

                if (now < h.NextAnomalyAlertTime && (now + 60.0f) < h.NextAnomalyAlertTime)
                    h.NextAnomalyAlertTime = 0f;

                if (ws.HasSurrendered)
                {
                    h.WatchdogStationarySince = 0f;
                    if (h.SurrenderedSinceTime <= 0f || now < h.SurrenderedSinceTime)
                        h.SurrenderedSinceTime = now;

                    // Detector 1: Surrender pose mismatch (+ self-healing pose restoration)
                    if (now - h.SurrenderedSinceTime > 1.5f && !IsSoldierInVehicleOrTransition(u))
                    {
                        Animator anim = u.m_Animator;
                        if (anim != null && (!anim.GetBool("Surrended") || anim.GetLayerWeight(1) < 0.5f))
                        {
                            if (now >= h.NextAnomalyAlertTime)
                            {
                                h.NextAnomalyAlertTime = now + 8.0f;
                                AnomalySurrenderCount++;
                                BotActionTrace.RecordEvent(u, BotTraceEventKind.AnomalyAlert, "surrender_pose_mismatch", now - h.SurrenderedSinceTime);
                            }
                            VeteranAndTacticsPatches.EnsureSurrenderedPrisonerAnimatorAndPose(u);
                        }
                    }

                    // Detector 2: Surrendered prisoner ignored by unengaged nearby friendly foot bots
                    if (h.Goap.FootStuck.SurrenderedFromTrap)
                    {
                        h.UnliberatedWithNearbyAllySince = 0f;
                        continue;
                    }

                    float nearestAllyDistSq = 18.0f * 18.0f;
                    bool hasNearbyUnengagedAlly = false;
                    for (int j = 0; j < WorldSoldierCount; j++)
                    {
                        if (j == i)
                            continue;

                        ref SoldierSnapshot ally = ref WorldSoldiers[j];
                        if (ally.IsPlayer || ally.IsOnVehicle || ally.HasSurrendered || ally.IsInjured ||
                            ally.Ref == null || !AreSameSideFactions(ws.Faction, ally.Faction))
                        {
                            continue;
                        }

                        float dx = ally.Pos.x - ws.Pos.x;
                        float dz = ally.Pos.z - ws.Pos.z;
                        float horizSq = dx * dx + dz * dz;
                        if (horizSq > 18.0f * 18.0f)
                            continue;

                        float horizDist = Mathf.Sqrt(horizSq);
                        if (Mathf.Abs(ally.Pos.y - ws.Pos.y) > 2.6f + horizDist * 0.55f)
                            continue;

                        SoldierAI allyAi = ally.Handles != null && ally.Handles.Ai != null ? ally.Handles.Ai : ally.Ref.aiController;
                        if (ally.Ref.GetSuppressionValue() < 18.0f && (allyAi == null || !allyAi.HasEnemyAndCanFireAtHim))
                        {
                            float dSq = horizSq + (ally.Pos.y - ws.Pos.y) * (ally.Pos.y - ws.Pos.y);
                            if (dSq < nearestAllyDistSq)
                                nearestAllyDistSq = dSq;
                            hasNearbyUnengagedAlly = true;
                        }
                    }

                    if (hasNearbyUnengagedAlly)
                    {
                        if (h.UnliberatedWithNearbyAllySince <= 0f || now < h.UnliberatedWithNearbyAllySince)
                            h.UnliberatedWithNearbyAllySince = now;
                        else if (now - h.UnliberatedWithNearbyAllySince > 12.0f && now >= h.NextAnomalyAlertTime)
                        {
                            h.NextAnomalyAlertTime = now + 10.0f;
                            AnomalyPrisonerCount++;
                            BotActionTrace.RecordEvent(
                                u,
                                BotTraceEventKind.AnomalyAlert,
                                "prisoner_ignored_by_allies",
                                Mathf.Sqrt(nearestAllyDistSq),
                                now - h.UnliberatedWithNearbyAllySince);
                        }
                    }
                    else
                    {
                        h.UnliberatedWithNearbyAllySince = 0f;
                    }
                }
                else
                {
                    h.SurrenderedSinceTime = 0f;
                    h.UnliberatedWithNearbyAllySince = 0f;

                    // Detector 3: Idle-in-open zombie bot while actual endpoint is > 20m away
                    bool eligibleZombieAudit = !ws.IsPlayer &&
                                               !ws.IsOnVehicle &&
                                               !ws.IsInjured &&
                                               !ws.IsSquadLeader &&
                                               IsLocallyOwnedBot(u) &&
                                               (u.Params == null || u.Params.canMove);
                    if (eligibleZombieAudit)
                    {
                        Squad wdSq = GetCachedSquad(h, now);
                        if (wdSq != null && HasAnyHumanPlayerInSquad(wdSq))
                            eligibleZombieAudit = false;
                    }

                    if (eligibleZombieAudit)
                    {
                        if (h.WatchdogStationarySince <= 0f ||
                            now < h.WatchdogStationarySince ||
                            (ws.Pos - h.WatchdogAnchorPos).sqrMagnitude > 0.35f * 0.35f)
                        {
                            h.WatchdogAnchorPos = ws.Pos;
                            h.WatchdogStationarySince = now;
                        }
                        else if (now - h.WatchdogStationarySince > 15.0f &&
                                 now >= h.NextAnomalyAlertTime &&
                                 !u.IsOnCover() &&
                                 !u.UsingBipod() &&
                                 !IsSoldierInVehicleOrTransition(u))
                        {
                            SoldierAI ai = h.Ai != null ? h.Ai : u.aiController;
                            if (ai != null)
                            {
                                if (ai.HasEnemyAndCanFireAtHim || ai.IsMoving())
                                {
                                    h.WatchdogStationarySince = now;
                                    h.Stance.ZombieRecoveries = 0;
                                }
                                else
                                {
                                    Vector3 effDest = VeteranAndTacticsPatches.GetCachedMoveEndpoint(h, ai, u, out _);
                                    float destDistSq = (effDest - ws.Pos).sqrMagnitude;
                                    if (destDistSq > 20.0f * 20.0f)
                                    {
                                        h.NextAnomalyAlertTime = now + 15.0f;
                                        AnomalyZombieCount++;
                                        if (traceEnabled)
                                        {
                                            string whyDetail = $"idle_in_open_zombie:p={(ai.HasPath ? 1 : 0)},r={(ai.HasPathRequest ? 1 : 0)},lk={(ai.PathCalculationLocked ? 1 : 0)},td={(u.HasDestinationAssigned ? 1 : 0)}";
                                            BotActionTrace.RecordEvent(
                                                u,
                                                BotTraceEventKind.AnomalyAlert,
                                                whyDetail,
                                                Mathf.Sqrt(destDistSq),
                                                now - h.WatchdogStationarySince);
                                        }

                                        if (IsLocallyOwnedBot(u) &&
                                            !u.IsOnVehicle() &&
                                            !u.isBleeding &&
                                            !u.isInHandsAnimation &&
                                            !u.isInBodyAnimation)
                                        {
                                            if (!ai.HasPath && !ai.HasPathRequest && !ai.PathCalculationLocked)
                                            {
                                                ai.moveDirectlyToward_end = 0f;
                                                if (h.Stance.ZombieRecoveries >= 1 && u.HasDestinationAssigned)
                                                {
                                                    u.targetDestination = null;
                                                    InvalidateTargetDestinationCache(u);
                                                    h.Stance.ZombieRecoveries = 0;
                                                    if (u.joinedSquad != null && u.joinedSquad.currentTask != null)
                                                    {
                                                        ai.MoveDestination = u.joinedSquad.currentTask.GetTaskDestination();
                                                        ai.UpdatePath();
                                                    }
                                                }
                                                else if (effDest.sqrMagnitude > 1f)
                                                {
                                                    h.Stance.ZombieRecoveries++;
                                                    ai.MoveDestination = effDest;
                                                    ai.UpdatePath();
                                                }
                                            }

                                            if (!u.IsOnCover() && u.m_pose == SoldierPose.Idle && u.GetSuppressionValue() >= 45f)
                                            {
                                                ai.actualPose = (u.GetSuppressionValue() >= 65f && !HasLowCeilingOverhead(ws.Pos, 5.5f))
                                                    ? SoldierPose.Prone
                                                    : SoldierPose.Crouch;
                                            }
                                        }
                                    }
                                    else
                                    {
                                        h.WatchdogStationarySince = now;
                                    }
                                }
                            }
                        }
                    }
                    else
                    {
                        h.WatchdogStationarySince = 0f;
                    }
                }
            }
        }

        private static bool ContainsHeavyDemoKeyword(string itemId)
        {
            if (string.IsNullOrEmpty(itemId))
                return false;

            return itemId.IndexOf("satchel", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   itemId.IndexOf("geballte", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   itemId.IndexOf("hafthohl", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   itemId.IndexOf("panzerwurf", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   itemId.IndexOf("gammon", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   itemId.IndexOf("hawkins", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   itemId.IndexOf("tnt", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   itemId.IndexOf("dynamite", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   itemId.IndexOf("demolition", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   itemId.IndexOf("bangalore", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static bool TryGetThrowablePrefabInfo(VirtualItem vi, out ThrowablePrefabInfo info)
        {
            if (ReferenceEquals(vi, null) || vi == null || vi.Pointer == IntPtr.Zero)
            {
                info = default;
                return false;
            }

            string itemId = vi.item_id ?? string.Empty;
            if (!string.IsNullOrEmpty(itemId) && _throwablePrefabCache.TryGetValue(itemId, out info))
                return info.IsValid || info.IsPressureMine || info.IsSatchelOrHeavyDemo;

            ItemObject prefab = null;
            try { prefab = vi.GetItemPrefab(); } catch { }
            return TryGetThrowablePrefabInfo(itemId, prefab, out info);
        }

        public static bool TryGetThrowablePrefabInfo(string itemId, ItemObject explicitPrefab, out ThrowablePrefabInfo info)
        {
            if (!string.IsNullOrEmpty(itemId) && _throwablePrefabCache.TryGetValue(itemId, out info))
                return info.IsValid || info.IsPressureMine || info.IsSatchelOrHeavyDemo;

            info = default;
            try
            {
                ItemObject prefab = explicitPrefab;
                if ((ReferenceEquals(prefab, null) || prefab == null) && !string.IsNullOrEmpty(itemId))
                    prefab = ItemsDatabase.GetItemObject(itemId);

                ItemGrenade ig = !ReferenceEquals(prefab, null) && prefab != null ? prefab.TryCast<ItemGrenade>() : null;
                if (ig != null)
                {
                    info.IsValid = true;
                    info.Fuse = ig.fuseType;
                    info.Type = ig.grenadeType;
                    info.MaxPenetration = ig.explosionMaxPenetration;
                    info.ExplosionRadius = ig.explosionRadius;
                    info.IsAtMine = info.Fuse == FuseType.VehiclePressionFuse;
                    info.IsApMine = info.Fuse == FuseType.InfantryPressionFuse;
                    info.IsPressureMine = info.IsAtMine || info.IsApMine;
                    info.IsSatchelOrHeavyDemo = !info.IsPressureMine &&
                                                (info.MaxPenetration >= 22.0f ||
                                                 (info.ExplosionRadius >= 6.0f && info.MaxPenetration >= 16.0f) ||
                                                 ContainsHeavyDemoKeyword(itemId));
                }
                else if (ContainsHeavyDemoKeyword(itemId))
                {
                    info.IsSatchelOrHeavyDemo = true;
                }
            }
            catch { }

            if (!string.IsNullOrEmpty(itemId))
            {
                if (_throwablePrefabCache.Count > 256)
                    _throwablePrefabCache.Clear();
                _throwablePrefabCache[itemId] = info;
            }

            return info.IsValid || info.IsPressureMine || info.IsSatchelOrHeavyDemo;
        }

        private static readonly Dictionary<string, (bool HasVehicles, float ExpireTime)> _enemyGroundVehiclesCache =
            new Dictionary<string, (bool HasVehicles, float ExpireTime)>(16, StringComparer.Ordinal);

        public static bool AnyEnemyGroundVehiclesPresent(string myFaction)
        {
            if (string.IsNullOrEmpty(myFaction))
                return false;

            string facKey = InternFaction(myFaction);
            float now = Time.time;
            if (_enemyGroundVehiclesCache.TryGetValue(facKey, out var cached) && now < cached.ExpireTime && cached.ExpireTime - now <= 10.0f)
                return cached.HasVehicles;

            bool found = false;
            try
            {
                var allVeh = Vehicle.allVehicles;
                if (allVeh != null)
                {
                    int vCount = allVeh.Count;
                    for (int i = 0; i < vCount; i++)
                    {
                        Vehicle v = allVeh[i];
                        if (v == null || v.Pointer == IntPtr.Zero || v.IsDisabled() || v.IsAirVehicle())
                            continue;
                        if (v.TryCast<MovableVehicle>() == null || v.TryCast<VehicleParachute>() != null)
                            continue;

                        string vf = v.GetVehicleFaction();
                        if (!string.IsNullOrEmpty(vf) && AreEnemyFactions(facKey, vf))
                        {
                            found = true;
                            break;
                        }
                    }
                }
            }
            catch { }

            if (_enemyGroundVehiclesCache.Count > 32)
                _enemyGroundVehiclesCache.Clear();
            _enemyGroundVehiclesCache[facKey] = (found, now + 2.5f);
            return found;
        }

        public static bool IsPressureMineItem(VirtualItem vi, out bool isAtMine)
        {
            if (TryGetThrowablePrefabInfo(vi, out ThrowablePrefabInfo info) && info.IsPressureMine)
            {
                isAtMine = info.IsAtMine;
                return true;
            }

            isAtMine = false;
            return false;
        }

        public static bool IsSatchelOrHeavyDemoItem(VirtualItem vi)
        {
            return TryGetThrowablePrefabInfo(vi, out ThrowablePrefabInfo info) && info.IsSatchelOrHeavyDemo;
        }

        /// <summary>
        /// Safely searches a soldier's inventory for a THROWABLE AT grenade or standard frag grenade
        /// while strictly excluding AT/AP pressure mines (<see cref="FuseType.VehiclePressionFuse"/> /
        /// <see cref="FuseType.InfantryPressionFuse"/>), smoke grenades, and non-lethal consumables.
        /// </summary>
        public static VirtualItem FindThrowableGrenadeInInventory(Soldier s, bool findAtGrenade, bool findFragGrenade)
        {
            if (ReferenceEquals(s, null) || s == null || s.Pointer == IntPtr.Zero)
                return null;

            SoldierFastHandles sh = GetOrCreateSoldierHandles(s);
            InventoryManager invMgr = sh != null ? GetCachedInventory(sh) : s.inventory;
            if (invMgr == null || invMgr.inventory == null)
                return null;

            var items = invMgr.inventory.items;
            if (items == null)
                return null;

            int count = items.Count;
            VirtualItem fallbackAt = null;
            for (int i = 0; i < count; i++)
            {
                VirtualItem vi = items[i];
                if (ReferenceEquals(vi, null) || vi == null || vi.Pointer == IntPtr.Zero)
                    continue;

                if (vi.TryCast<VirtualSmokeGrenade>() != null)
                    continue;

                bool isAt = vi.TryCast<VirtualATGrenade>() != null;
                bool isGren = isAt || vi.TryCast<VirtualGrenade>() != null;
                if (!isGren)
                    continue;

                if (IsPressureMineItem(vi, out _))
                    continue;

                if (isAt)
                {
                    if (findAtGrenade)
                        return vi;
                    continue;
                }

                if (findFragGrenade)
                    return vi;
                if (findAtGrenade && fallbackAt == null &&
                    TryGetThrowablePrefabInfo(vi, out ThrowablePrefabInfo info) &&
                    (info.Type == GrenadeType.ATGrenade ||
                     info.Type == GrenadeType.phosporusGrenade ||
                     info.MaxPenetration >= 16.0f ||
                     info.IsSatchelOrHeavyDemo))
                {
                    fallbackAt = vi;
                }
            }

            return fallbackAt;
        }

        public static void RegisterPlacedMine(Vector3 worldPos, ItemGrenade grenadeObj, bool isAtMine)
        {
            float now = Time.time;
            IntPtr ptr = (!ReferenceEquals(grenadeObj, null) && grenadeObj != null) ? grenadeObj.Pointer : IntPtr.Zero;

            for (int i = 0; i < ActiveLandMineCount; i++)
            {
                ref ActiveLandMine m = ref ActiveLandMines[i];
                if ((ptr != IntPtr.Zero && m.Ptr == ptr) || (m.Pos - worldPos).sqrMagnitude <= 0.64f)
                {
                    m.Ptr = ptr;
                    m.Ref = grenadeObj;
                    m.Pos = worldPos;
                    m.IsAtMine = isAtMine;
                    m.ExpireTime = now + 240.0f;
                    return;
                }
            }

            if (TryEnsureArrayCapacity(ref ActiveLandMines, ActiveLandMineCount + 1, 512))
            {
                ActiveLandMines[ActiveLandMineCount++] = new ActiveLandMine
                {
                    Ptr = ptr,
                    Ref = grenadeObj,
                    Pos = worldPos,
                    IsAtMine = isAtMine,
                    ExpireTime = now + 240.0f
                };
            }
        }

        public static void RefreshActiveLandMinesIfDue()
        {
            float now = Time.time;
            bool runTrackedRefresh = now >= _nextActiveMineScanTime || now < _nextActiveMineScanTime - 2.0f;
            bool runSpawnedChunk = now >= _nextActiveMineChunkTime || now < _nextActiveMineChunkTime - 2.5f;
            if (!runTrackedRefresh && !runSpawnedChunk)
                return;

            if (runTrackedRefresh)
            {
                _nextActiveMineScanTime = now + 0.50f;
                int prevCount = ActiveLandMineCount;
                int writeIdx = 0;

                // Keep recently registered or still-live tracked mines up front, culling detonated/expired entries
                for (int i = 0; i < prevCount; i++)
                {
                    ActiveLandMine m = ActiveLandMines[i];
                    if (now > m.ExpireTime)
                        continue;

                    if (!ReferenceEquals(m.Ref, null))
                    {
                        if (m.Ref == null || m.Ref.Pointer == IntPtr.Zero || m.Ref.exploded)
                            continue;

                        if (m.Ref.transform != null)
                            m.Pos = m.Ref.transform.position;
                    }

                    ActiveLandMines[writeIdx++] = m;
                }
                if (prevCount > writeIdx)
                    Array.Clear(ActiveLandMines, writeIdx, prevCount - writeIdx);
                ActiveLandMineCount = writeIdx;
            }

            if (!runSpawnedChunk)
                return;

            try
            {
                var spawned = ItemObject.spawnedItems;
                if (spawned == null)
                {
                    _activeMineScanCursor = 0;
                    _nextActiveMineChunkTime = now + 1.0f;
                    return;
                }

                if (_nonMineSpawnedItemPtrs.Count > 4096)
                    _nonMineSpawnedItemPtrs.Clear();

                int count = spawned.Count;
                if (count <= 0)
                {
                    _activeMineScanCursor = 0;
                    _nextActiveMineChunkTime = now + 1.0f;
                    return;
                }

                if (_activeMineScanCursor < 0 || _activeMineScanCursor >= count)
                    _activeMineScanCursor = 0;

                const int MaxItemsPerChunk = 64;
                int start = _activeMineScanCursor;
                int end = Math.Min(count, start + MaxItemsPerChunk);
                for (int i = start; i < end; i++)
                {
                    ItemObject io = spawned[i];
                    if (ReferenceEquals(io, null) || io == null || io.Pointer == IntPtr.Zero)
                        continue;

                    IntPtr ptr = io.Pointer;
                    if (_nonMineSpawnedItemPtrs.Contains(ptr))
                        continue;

                    ItemGrenade ig = io.TryCast<ItemGrenade>();
                    if (ig == null ||
                        (ig.fuseType != FuseType.VehiclePressionFuse && ig.fuseType != FuseType.InfantryPressionFuse))
                    {
                        _nonMineSpawnedItemPtrs.Add(ptr);
                        continue;
                    }

                    if (!ig.activated || ig.exploded || ig.transform == null)
                        continue;

                    RegisterPlacedMine(ig.transform.position, ig, ig.fuseType == FuseType.VehiclePressionFuse);
                }

                if (end >= count)
                {
                    _activeMineScanCursor = 0;
                    _nextActiveMineChunkTime = now + 1.0f;
                }
                else
                {
                    _activeMineScanCursor = end;
                    _nextActiveMineChunkTime = now + 0.12f;
                }
            }
            catch
            {
                _activeMineScanCursor = 0;
                _nextActiveMineChunkTime = now + 1.0f;
            }
        }

        public static bool TryGetNearbyKnownMine(Vector3 pos, float maxRadius, bool vehiclesOnly, out Vector3 minePos)
        {
            minePos = Vector3.zero;
            if (ActiveLandMineCount <= 0)
                return false;

            float bestSq = maxRadius * maxRadius;
            bool found = false;
            for (int i = 0; i < ActiveLandMineCount; i++)
            {
                ref ActiveLandMine m = ref ActiveLandMines[i];
                if (vehiclesOnly && !m.IsAtMine)
                    continue;

                float dSq = (m.Pos - pos).sqrMagnitude;
                if (dSq < bestSq)
                {
                    bestSq = dSq;
                    minePos = m.Pos;
                    found = true;
                }
            }

            return found;
        }

        public static void ClearSpatialCaches()
        {
            _cachedSolidEnvMask = 0;
            _cachedMovementObstacleMask = 0;
            _cachedSmokeOcclusionMask = 0;
            _turretVehicleCache.Clear();
            _turretByAiPtr.Clear();
            _turretTraitsCache.Clear();
            _ceilingCache.Clear();
            _bridgeCellCache.Clear();
            _terrainColliderCache.Clear();
            _vehicleColliderCache.Clear();
            _vehicleColliderCacheById.Clear();
            if (_sharedOctreeCoverResults != null)
                _sharedOctreeCoverResults.Clear();
            CachedLocalPlayerHandles = null;
            CachedLocalPlayerSquadPtr = IntPtr.Zero;
            ClearAliveCreaturesCache();
            _sameSideFactionCache.Clear();
            _enemyFactionCache.Clear();
            _enemySpawnDirCache.Clear();
            _soldierHandlesByPtr.Clear();
            _soldierHandlesByAiPtr.Clear();
            _squadDiagIds.Clear();
            _confirmedVehicleCrewSquads.Clear();
            _confirmedPlaneCrewSquads.Clear();
            _confirmedInfantrySquads.Clear();
            _formattedFactionDisplayCache.Clear();
            _nextSoldierDiagId = 0;
            _nextSquadDiagId = 0;
            _vehicleHandlesByPtr.Clear();
            _nonSoldierCreaturePtrs.Clear();
            _nonUnitSpottablePtrs.Clear();
            _cachedDetachedSoldiers.Clear();
            _nextDetachedCreatureScanTime = 0f;
            _worldSquadIndexByPtr.Clear();
            if (WorldSquadCount > 0)
                Array.Clear(WorldSquads, 0, WorldSquadCount);
            if (WorldSoldierCount > 0)
                Array.Clear(WorldSoldiers, 0, WorldSoldierCount);
            if (WorldInjuredSoldierCount > 0)
                Array.Clear(WorldInjuredSoldiers, 0, WorldInjuredSoldierCount);
            if (ActivePlayerAimLaneCount > 0)
                Array.Clear(ActivePlayerAimLanes, 0, ActivePlayerAimLaneCount);
            ActivePlayerAimLaneCount = 0;
            _nextPlayerAimLaneRefreshTime = 0f;
            if (ActiveVehicleDangerCorridorCount > 0)
                Array.Clear(ActiveVehicleDangerCorridors, 0, ActiveVehicleDangerCorridorCount);
            ActiveVehicleDangerCorridorCount = 0;
            if (ActiveAudibleGroundVehicleCount > 0)
                Array.Clear(ActiveAudibleGroundVehicles, 0, ActiveAudibleGroundVehicleCount);
            ActiveAudibleGroundVehicleCount = 0;
            _nextVehicleDangerCorridorRefreshTime = 0f;
            _cachedMainCamera = null;
            _nextMainCameraLookupTime = 0f;
            _throwablePrefabCache.Clear();
            _nonMineSpawnedItemPtrs.Clear();
            if (ActiveLandMineCount > 0)
                Array.Clear(ActiveLandMines, 0, ActiveLandMineCount);
            ActiveLandMineCount = 0;
            _nextActiveMineScanTime = 0f;
            _activeMineScanCursor = 0;
            _nextActiveMineChunkTime = 0f;
            WorldSquadCount = 0;
            WorldSoldierCount = 0;
            WorldInjuredSoldierCount = 0;
            WorldAirSquadCount = 0;
            _lastWorldSnapshotTime = -999f;
            ResetCoopRpcNetworkState();
            _nextLocalPlayerRadioHintTime = 0f;
            TotalAdaptiveAiExtraTicks = 0;
            TotalPingGovernorSkippedCycles = 0;
            TotalHitchesOver28ms = 0;
            _currentWindowMaxFrameMs = 0f;
            _previousWindowMaxFrameMs = 0f;
            try { GC.Collect(1, GCCollectionMode.Optimized, blocking: false); } catch { }
            RestoreForegroundFpsIfCapped();
            _nextFrameWindowRolloverTime = 0f;
            ClearUrgentUnderFireWakeQueue();
            _nextWatchObjectiveSnapshotTime = 0f;
            _watchObjectiveSnapshotCount = 0;
            _nextVehicleAnomalyScanTime = 0f;
            VeteranAndTacticsPatches.ClearActiveLiveGrenades();
            ResetMatchTelemetryAndExceptions();
        }

        public static void PruneSpatialCachesIfOversized()
        {
            if (_turretVehicleCache.Count > 256)
                _turretVehicleCache.Clear();
            if (_turretByAiPtr.Count > 256)
                _turretByAiPtr.Clear();
            if (_turretTraitsCache.Count > 256)
                _turretTraitsCache.Clear();
            if (_ceilingCache.Count > 4096)
                _ceilingCache.Clear();
            if (_bridgeCellCache.Count > 4096)
                _bridgeCellCache.Clear();
            if (_nonMineSpawnedItemPtrs.Count > 4096)
                _nonMineSpawnedItemPtrs.Clear();
            if (_throwablePrefabCache.Count > 256)
                _throwablePrefabCache.Clear();
        }


        // =====================================================================
        // Shared Tactical, Locomotion & Weapon Inspection Helpers (DRY)
        // =====================================================================
        private struct WatchObjectiveSnapshotEntry
        {
            public Vector3 Pos;
            public bool InvaderHeld;
            public bool DefenderHeld;
            public float Contest01;
        }

        private static readonly List<MissionObjective> _reusableWatchObjectivesList = new List<MissionObjective>(16);
        private static WatchObjectiveSnapshotEntry[] _watchObjectiveSnapshot = new WatchObjectiveSnapshotEntry[16];
        private static int _watchObjectiveSnapshotCount;
        private static float _nextWatchObjectiveSnapshotTime;
        private static string _watchObjectiveInvaderFaction;

        public static bool TryGetNearestEnemyOrContestedObjectiveDirection(
            string observerFaction,
            Vector3 observerPos,
            out Vector3 objDir)
        {
            objDir = Vector3.zero;
            if (string.IsNullOrEmpty(observerFaction))
                return false;

            float now = Time.time;
            if (now >= _nextWatchObjectiveSnapshotTime || _nextWatchObjectiveSnapshotTime - now > 10.0f)
            {
                _nextWatchObjectiveSnapshotTime = now + 1.25f;
                _watchObjectiveSnapshotCount = 0;
                _watchObjectiveInvaderFaction = null;
                try
                {
                    BattleManager bm = BattleManager.instance;
                    if (bm != null && bm.Pointer != IntPtr.Zero && !BattleManager.isBattleEnded)
                    {
                        BattleData bd = BattleManager.GetCurrentBattleData();
                        string invFac = bd != null ? bd.GetInvadersFaction() : null;
                        string defFac = bd != null ? bd.GetDefendersFaction() : null;
                        _watchObjectiveInvaderFaction = invFac;
                        BattlePhase phase = TryGetCurrentBattlePhase(bm);
                        CollectActiveMissionObjectives(phase, _reusableWatchObjectivesList);
                        TryEnsureArrayCapacity(ref _watchObjectiveSnapshot, Math.Min(_reusableWatchObjectivesList.Count, 64), 64);
                        int count = Mathf.Min(_reusableWatchObjectivesList.Count, _watchObjectiveSnapshot.Length);
                        int outIdx = 0;
                        for (int i = 0; i < count; i++)
                        {
                            MissionObjective mo = _reusableWatchObjectivesList[i];
                            if (mo == null) continue;
                            bool invHeld = !string.IsNullOrEmpty(invFac) &&
                                           ClassifyMissionObjectiveForFaction(mo, invFac, bd, out bool ih, out _, out float ip) && ih;
                            bool defHeld = !string.IsNullOrEmpty(defFac) &&
                                           ClassifyMissionObjectiveForFaction(mo, defFac, bd, out bool dh, out _, out float dp) && dh;
                            float contest = 0f;
                            if (!string.IsNullOrEmpty(invFac) && ClassifyMissionObjectiveForFaction(mo, invFac, bd, out _, out _, out float c1))
                                contest = Mathf.Max(contest, c1);
                            if (!string.IsNullOrEmpty(defFac) && ClassifyMissionObjectiveForFaction(mo, defFac, bd, out _, out _, out float c2))
                                contest = Mathf.Max(contest, c2);

                            _watchObjectiveSnapshot[outIdx++] = new WatchObjectiveSnapshotEntry
                            {
                                Pos = mo.GetTaskPosition(),
                                InvaderHeld = invHeld,
                                DefenderHeld = defHeld,
                                Contest01 = contest
                            };
                        }
                        _watchObjectiveSnapshotCount = outIdx;
                        _reusableWatchObjectivesList.Clear();
                    }
                }
                catch
                {
                    _watchObjectiveSnapshotCount = 0;
                    _reusableWatchObjectivesList.Clear();
                }
            }

            if (_watchObjectiveSnapshotCount <= 0)
                return false;

            bool observerIsInvader = !string.IsNullOrEmpty(_watchObjectiveInvaderFaction) &&
                                     AreSameSideFactions(observerFaction, _watchObjectiveInvaderFaction);
            float bestWeightedSq = float.MaxValue;
            Vector3 bestDelta = Vector3.zero;

            for (int i = 0; i < _watchObjectiveSnapshotCount; i++)
            {
                ref WatchObjectiveSnapshotEntry e = ref _watchObjectiveSnapshot[i];
                bool heldByMe = observerIsInvader ? e.InvaderHeld : e.DefenderHeld;
                if (heldByMe && e.Contest01 < 0.08f)
                    continue;

                Vector3 delta = e.Pos - observerPos;
                delta.y = 0f;
                float dSq = delta.sqrMagnitude;
                if (dSq < 18.0f * 18.0f)
                    continue;

                float weight = e.Contest01 >= 0.08f ? 0.55f : 1.0f;
                if (dSq * weight < bestWeightedSq)
                {
                    bestWeightedSq = dSq * weight;
                    bestDelta = delta;
                }
            }

            if (bestDelta.sqrMagnitude > 0.25f)
            {
                objDir = bestDelta.normalized;
                return true;
            }
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ResetBotPathLockout(SoldierAI ai)
        {
            if (ReferenceEquals(ai, null) || ai.Pointer == IntPtr.Zero)
                return;

            ai.mustTeleport = false;
            ai.pathCreationFails = 0;
            ai.UnlockPathCalculation();
        }

        public static bool IsCombatCoverIntact(CombatCover cc)
        {
            return !ReferenceEquals(cc, null) && cc != null && cc.Pointer != IntPtr.Zero && !cc.destroyed && cc.isActiveAndEnabled;
        }

        public static void AssignBotCoverOrDestination(
            SoldierAI ai,
            Soldier soldier,
            AiDestination dest,
            bool requestImmediatePath = true)
        {
            if (ReferenceEquals(soldier, null) || soldier == null || soldier.Pointer == IntPtr.Zero)
                return;

            soldier.CoverPosition(dest);
            InvalidateTargetDestinationCache(soldier);

            if (ReferenceEquals(ai, null) || ai == null || ai.Pointer == IntPtr.Zero)
                return;

            if (ai.moveDirectlyToward_end > Time.time)
                ai.moveDirectlyToward_end = 0f;

            ResetBotPathLockout(ai);

            if (!ReferenceEquals(dest, null) && dest != null && dest.Pointer != IntPtr.Zero)
            {
                Vector3 coverPos = dest.GetCoverPosition();
                if (coverPos.sqrMagnitude > 0.01f)
                    ai.MoveDestination = coverPos;

                if (requestImmediatePath)
                    ai.UpdatePath();
            }
        }

        public static bool SetBotMoveDestinationAndRefreshPath(SoldierAI ai, Vector3 dest, float minDeltaSq = 0.49f)
        {
            if (ReferenceEquals(ai, null) || ai == null)
                return false;

            if ((ai.MoveDestination - dest).sqrMagnitude <= minDeltaSq &&
                !ai.mustTeleport &&
                !ai.PathCalculationLocked)
            {
                return false;
            }

            if (ai.moveDirectlyToward_end > Time.time)
                ai.moveDirectlyToward_end = 0f;

            ResetBotPathLockout(ai);
            ai.MoveDestination = dest;
            ai.UpdatePath();
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector3 HorizontalDirectionOrFallback(Vector3 from, Vector3 to, Vector3 fallback)
        {
            Vector3 delta = to - from;
            delta.y = 0f;
            return delta.sqrMagnitude > 0.001f ? delta.normalized : fallback;
        }

        public static bool HasUsableSmallArmsAmmo(Soldier soldier)
        {
            if (ReferenceEquals(soldier, null) || soldier == null)
                return false;

            GenericGun held = soldier.GetHeldGun();
            if (held != null && !held.IsAtWeapon() && (held.GetCurrentAmmoCount() > 0 || !soldier.IsOutOfAmmosFor(held)))
                return true;

            GenericGun g1 = soldier.GetHeldGun(1);
            if (g1 != null && !g1.IsAtWeapon() && (g1.GetCurrentAmmoCount() > 0 || !soldier.IsOutOfAmmosFor(g1)))
                return true;

            GenericGun g2 = soldier.GetHeldGun(2);
            if (g2 != null && !g2.IsAtWeapon() && (g2.GetCurrentAmmoCount() > 0 || !soldier.IsOutOfAmmosFor(g2)))
                return true;

            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsHeldGunEmptyOrReloading(Soldier soldier, GenericGun heldGun)
        {
            if (ReferenceEquals(soldier, null) || soldier == null)
                return false;

            return soldier.IsReloading ||
                   soldier.MustReload() ||
                   (heldGun != null && heldGun.GetCurrentAmmoCount() <= 0);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsMedicSoldierOrPersonality(Soldier soldier, VeteranAndTacticsPatches.SoldierPersonality pers)
        {
            if (!ReferenceEquals(soldier, null) && soldier != null && soldier.IsMedic())
                return true;

            return pers == VeteranAndTacticsPatches.SoldierPersonality.Medic_Heroic ||
                   pers == VeteranAndTacticsPatches.SoldierPersonality.Medic_Combat ||
                   pers == VeteranAndTacticsPatches.SoldierPersonality.Medic_Triage;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsAtSoldierPersonality(VeteranAndTacticsPatches.SoldierPersonality pers)
        {
            return pers == VeteranAndTacticsPatches.SoldierPersonality.AT_TankHunter ||
                   pers == VeteranAndTacticsPatches.SoldierPersonality.AT_Ambusher ||
                   pers == VeteranAndTacticsPatches.SoldierPersonality.AT_Demolitionist;
        }
    }
}
