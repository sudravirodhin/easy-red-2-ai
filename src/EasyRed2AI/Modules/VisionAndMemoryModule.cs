using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace EasyRed2HumanAI
{
    public static partial class VisionPatches
    {
        // Tracks the last Time.time an individual soldier (by IL2CPP pointer) fired a weapon
        private static readonly Dictionary<IntPtr, float> _lastFiredTime = new Dictionary<IntPtr, float>(256);
        private static float _nextCleanupTime;

        // Thread-local optic zoom multiplier passed from Observer (Creature.CanSee / TurretAI.CanSee)
        // into Target (Soldier.CamoDistance / Soldier.CamoDistanceAwareEnemies)
        [ThreadStatic]
        private static float _currentObserverOpticMultiplier;

        // Live telemetry counters for HUD / debug feedback
        public static int TotalSightChecks;
        public static int BlockedByFOV;
        public static int BlockedByRange;
        public static int BoostedByScope;
        public static int BlockedPlaneSpot;

        public static float GetObserverOpticMultiplier()
        {
            return _currentObserverOpticMultiplier > 0.01f ? _currentObserverOpticMultiplier : 1.0f;
        }

        public static void RecordSoldierFired(Soldier s)
        {
            if (s == null || s.Pointer == IntPtr.Zero) return;
            _lastFiredTime[s.Pointer] = Time.time;
        }

        public static bool HasFiredRecently(Soldier s, float windowSeconds = 4.5f)
        {
            if (s == null || s.Pointer == IntPtr.Zero)
                return false;

            if (s.pressTriggerRequest)
            {
                _lastFiredTime[s.Pointer] = Time.time;
                return true;
            }

            if (_lastFiredTime.TryGetValue(s.Pointer, out float firedAt))
            {
                float elapsed = Time.time - firedAt;
                return elapsed >= 0f && elapsed <= windowSeconds;
            }
            return false;
        }

        public static float GetFiringVisibilityBonus(Soldier s)
        {
            if (s == null || s.Pointer == IntPtr.Zero) return 0f;
            float duration = VisionConfig.FiringRevealDuration.Value;
            if (duration <= 0f) return 0f;

            // Stalker Marksmen have a 35% shorter firing reveal window when shooting from crouch/prone cover
            if (s.m_pose >= SoldierPose.Crouch &&
                VeteranAndTacticsPatches.GetPersonality(s) == VeteranAndTacticsPatches.SoldierPersonality.Marksman_Stalker)
            {
                duration *= 0.65f;
            }

            // Also catch sustained full-auto fire if trigger is currently depressed
            if (s.pressTriggerRequest)
            {
                _lastFiredTime[s.Pointer] = Time.time;
                return VisionConfig.FiringSightRangeBonus.Value;
            }

            if (_lastFiredTime.TryGetValue(s.Pointer, out float firedAt))
            {
                float elapsed = Time.time - firedAt;
                if (elapsed >= 0f && elapsed < duration)
                {
                    float t = 1.0f - (elapsed / duration);
                    return VisionConfig.FiringSightRangeBonus.Value * t;
                }
            }
            return 0f;
        }

        public static void CleanupExpiredFiringRecords()
        {
            if (Time.time <= _nextCleanupTime)
                return;

            _nextCleanupTime = Time.time + 15f;
            float cutoff = Time.time - Mathf.Max(10f, VisionConfig.FiringRevealDuration.Value);
            var toRemove = new List<IntPtr>();
            foreach (var kvp in _lastFiredTime)
            {
                if (kvp.Value < cutoff) toRemove.Add(kvp.Key);
            }
            for (int i = 0; i < toRemove.Count; i++)
            {
                _lastFiredTime.Remove(toRemove[i]);
            }

            if (VisionConfig.DebugLogging.Value && TotalSightChecks > 0)
            {
                Plugin.Log.LogInfo($"[Stats] SightChecks={TotalSightChecks}, BlockedFOV={BlockedByFOV}, BlockedRange={BlockedByRange}, ScopeBoosts={BoostedByScope}, BlockedPlane={BlockedPlaneSpot}");
            }
        }

        // =====================================================================
        // 1. Track Individual Weapon Firing (Muzzle Flash / Gunshot Reveal)
        // =====================================================================
        [HarmonyPatch(typeof(Soldier), nameof(Soldier.StartFire))]
        public static class Soldier_StartFire_Patch
        {
            public static void Postfix(Soldier __instance)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value || __instance == null) return;
                    RecordSoldierFired(__instance);
                    VeteranAndTacticsPatches.AddCombatExperienceBonus(__instance, 0.75f);
                }
                catch { }
            }
        }

        // =====================================================================
        // 2. Infantry Stance, Camouflage & Squad Awareness Sight Calculation
        // =====================================================================
        [HarmonyPatch(typeof(Soldier), nameof(Soldier.CamoDistanceAwareEnemies))]
        public static class Soldier_CamoDistanceAwareEnemies_Patch
        {
            public static bool Prefix(Soldier __instance, bool __0, ref float __result)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value || __instance == null)
                        return true;

                    float camoSum = 0f;
                    if (__instance.hasUniform)
                    {
                        var u = __instance.GetUniform;
                        if (u != null) camoSum += u.CamoPercReduction;
                    }
                    if (__instance.hasVest)
                    {
                        var v = __instance.GetVest;
                        if (v != null) camoSum += v.CamoPercReduction;
                    }
                    if (__instance.hasHeadgear)
                    {
                        var h = __instance.GetHeadgear;
                        if (h != null) camoSum += h.CamoPercReduction;
                    }
                    float camoMultiplier = 1.0f - (camoSum / 3.0f) * 0.4f;

                    SoldierPose pose = __instance.m_pose;
                    float baseDist;
                    if (!__0 || VisionConfig.PreserveStanceStealthInCombat.Value)
                    {
                        switch (pose)
                        {
                            case SoldierPose.Idle:
                                baseDist = VisionConfig.StandingSightRange.Value;
                                break;
                            case SoldierPose.Crouch:
                                baseDist = VisionConfig.CrouchingSightRange.Value;
                                break;
                            case SoldierPose.Prone:
                                baseDist = VisionConfig.ProneSightRange.Value;
                                break;
                            default:
                                baseDist = VisionConfig.StandingSightRange.Value;
                                break;
                        }

                        if (__0)
                        {
                            baseDist *= VisionConfig.CombatAwareStanceMultiplier.Value;
                        }
                    }
                    else
                    {
                        baseDist = 300.0f;
                    }

                    baseDist += GetFiringVisibilityBonus(__instance);
                    float finalDist = baseDist * camoMultiplier * GetObserverOpticMultiplier();
                    __result = finalDist;
                    return false;
                }
                catch
                {
                    return true;
                }
            }
        }

        // =====================================================================
        // 3. Infantry Observer FOV Cones & Scope Magnification (Creature.CanSee)
        // =====================================================================
        [HarmonyPatch(typeof(Creature), nameof(Creature.CanSee))]
        public static class Creature_CanSee_Patch
        {
            public static bool Prefix(Creature __instance, Spottable __0, ref bool __result)
            {
                _currentObserverOpticMultiplier = 1.0f;
                try
                {
                    if (!VisionConfig.EnableMod.Value || __instance == null || __0 == null)
                        return true;

                    TotalSightChecks++;

                    Soldier observer = __instance.TryCast<Soldier>();
                    if (observer == null)
                        return true;

                    Vector3 observerPos = observer.FacePosition();
                    Vector3 targetPos = __0.SpotPosition();
                    Vector3 toTarget = targetPos - observerPos;
                    float dist = toTarget.magnitude;

                    Vector3 lookDir = observer.IsAiming ? observer.GetFireDir() : observer.transform.forward;
                    float angle = Vector3.Angle(toTarget, lookDir);

                    float opticZoom = 1.0f;
                    if (VisionConfig.EnableScopeVisionScaling.Value && observer.HasHeldGun(0))
                    {
                        GenericGun gun = observer.GetHeldGun(0);
                        if (gun != null && (gun.HasScopeWithMagnification() || observer.IsMarksman()))
                        {
                            float fovMult = gun.GetAimingFOVMultiplier();
                            if (fovMult > 0.01f && fovMult < 0.85f)
                            {
                                opticZoom = Mathf.Clamp(0.85f / fovMult, 1.0f, VisionConfig.MaxOpticZoomMultiplier.Value);
                            }
                            else if (observer.IsMarksman())
                            {
                                opticZoom = Mathf.Min(1.75f, VisionConfig.MaxOpticZoomMultiplier.Value);
                            }
                        }
                    }

                    bool isStationaryOverwatch = VisionConfig.EnableThreatSectorConcentration.Value &&
                                                 !observer.IsPlayer() &&
                                                 !observer.IsOnVehicle() &&
                                                 observer.aiController != null &&
                                                 !observer.aiController.IsMoving();
                    bool hasActiveThreatSector = isStationaryOverwatch &&
                                                 VeteranAndTacticsPatches.HasActiveThreatSectorContact(observer);
                    Soldier targetSoldier = __0.TryCast<Soldier>();
                    bool isQuietFlanker = targetSoldier != null &&
                                          !targetSoldier.IsSpotted &&
                                          !HasFiredRecently(targetSoldier, 4.0f);

                    if (VisionConfig.EnforceHumanFOVWhenAlerted.Value && dist > VisionConfig.CloseOmnidirectionalRange.Value)
                    {
                        float allowedHalfFOV = dist <= VisionConfig.MidRangeMaxDistance.Value
                            ? VisionConfig.MidRangeHalfFOV.Value
                            : VisionConfig.LongRangeHalfFOV.Value;

                        if (hasActiveThreatSector && isQuietFlanker)
                        {
                            allowedHalfFOV = Mathf.Min(allowedHalfFOV, 60f);
                        }

                        if (angle > allowedHalfFOV)
                        {
                            BlockedByFOV++;
                            __result = false;
                            return false;
                        }
                    }

                    float allowedOpticHalfFOV = VisionConfig.OpticLongRangeHalfFOV.Value;
                    VeteranAndTacticsPatches.SoldierPersonality observerPers = VeteranAndTacticsPatches.GetPersonality(observer);
                    if (observerPers == VeteranAndTacticsPatches.SoldierPersonality.Marksman_Spotter)
                    {
                        allowedOpticHalfFOV += 5.0f;
                    }

                    if (opticZoom > 1.0f && angle <= allowedOpticHalfFOV)
                    {
                        _currentObserverOpticMultiplier = opticZoom;
                        BoostedByScope++;
                    }

                    if (isStationaryOverwatch)
                    {
                        if (angle <= 28f)
                        {
                            float focusMult = (observerPers == VeteranAndTacticsPatches.SoldierPersonality.Gunner_Overwatch ||
                                               observerPers == VeteranAndTacticsPatches.SoldierPersonality.Marksman_Spotter)
                                ? 1.28f
                                : 1.20f;
                            _currentObserverOpticMultiplier = Mathf.Max(_currentObserverOpticMultiplier, focusMult);
                        }
                        else if (hasActiveThreatSector && isQuietFlanker && angle > 38f)
                        {
                            _currentObserverOpticMultiplier *= 0.72f;
                        }
                    }

                    if (__0.IsWheeledVehicleOrTank() && dist > VisionConfig.VehicleCamoDistance.Value * GetObserverOpticMultiplier())
                    {
                        BlockedByRange++;
                        __result = false;
                        return false;
                    }

                    return true;
                }
                catch
                {
                    _currentObserverOpticMultiplier = 1.0f;
                    return true;
                }
            }

            public static void Postfix(bool __result)
            {
                if (!__result && VisionConfig.EnableMod.Value)
                {
                    BlockedByRange++;
                }
                _currentObserverOpticMultiplier = 1.0f;
            }
        }

        // =====================================================================
        // 4. Vehicle & Tank Turret Vision (TurretAI.CanSee)
        // =====================================================================
        [HarmonyPatch(typeof(TurretAI), nameof(TurretAI.CanSee))]
        public static class TurretAI_CanSee_Patch
        {
            public static bool Prefix(TurretAI __instance, Spottable __0, ref bool __result)
            {
                _currentObserverOpticMultiplier = 1.0f;
                try
                {
                    if (!VisionConfig.EnableMod.Value || __instance == null || __0 == null)
                        return true;

                    Turret turret = __instance.turret;
                    if (turret == null)
                        return true;

                    TotalSightChecks++;

                    Vector3 firePos = turret.GetFirePos();
                    Vector3 targetPos = __0.SpotPosition();
                    Vector3 toTarget = targetPos - firePos;
                    float dist = toTarget.magnitude;
                    Vector3 fireDir = turret.GetFireDir();
                    float angle = Vector3.Angle(toTarget, fireDir);

                    bool isInfantryTarget = !__0.IsWheeledVehicleOrTank() && !__0.IsAirVehicle();
                    float turretAimFov = turret.GetAimingFOV();
                    Vehicle turretVeh = AiCore.GetCachedTurretVehicle(turret);
                    VeteranAndTacticsPatches.SoldierPersonality crewPers = turretVeh != null
                        ? VeteranAndTacticsPatches.GetVehicleCrewPersonality(turretVeh)
                        : VeteranAndTacticsPatches.SoldierPersonality.None;

                    if (isInfantryTarget && VisionConfig.EnforceHumanFOVWhenAlerted.Value)
                    {
                        if (dist > VisionConfig.CloseOmnidirectionalRange.Value)
                        {
                            float allowedHalfFOV = dist <= VisionConfig.MidRangeMaxDistance.Value
                                ? VisionConfig.MidRangeHalfFOV.Value
                                : VisionConfig.LongRangeHalfFOV.Value;

                            if (crewPers == VeteranAndTacticsPatches.SoldierPersonality.LightCrew_FlakWatch)
                                allowedHalfFOV += 12.0f;

                            if (angle > allowedHalfFOV)
                            {
                                BlockedByFOV++;
                                __result = false;
                                return false;
                            }
                        }
                        else if (turretAimFov < 42f && !turret.isAA && angle > 95f)
                        {
                            BlockedByFOV++;
                            __result = false;
                            return false;
                        }
                    }

                    if (VisionConfig.EnableScopeVisionScaling.Value && turretAimFov > 1f && turretAimFov < 45f)
                    {
                        float opticCone = VisionConfig.OpticLongRangeHalfFOV.Value;
                        if (crewPers == VeteranAndTacticsPatches.SoldierPersonality.TankCrew_Sniper)
                            opticCone += 5.0f;

                        if (angle <= opticCone)
                        {
                            _currentObserverOpticMultiplier = Mathf.Clamp(50f / turretAimFov, 1.0f, VisionConfig.MaxOpticZoomMultiplier.Value);
                            BoostedByScope++;
                        }
                    }

                    if (__0.IsWheeledVehicleOrTank() && dist > VisionConfig.VehicleCamoDistance.Value * GetObserverOpticMultiplier())
                    {
                        BlockedByRange++;
                        __result = false;
                        return false;
                    }

                    return true;
                }
                catch
                {
                    _currentObserverOpticMultiplier = 1.0f;
                    return true;
                }
            }

            public static void Postfix()
            {
                _currentObserverOpticMultiplier = 1.0f;
            }
        }

        // =====================================================================
        // 5. Aircraft Ground Spotting & CAS Target Priority (AIPlane.GetBestAircraftVisibleEnemy)
        // =====================================================================
        [HarmonyPatch(typeof(AIPlane), nameof(AIPlane.GetBestAircraftVisibleEnemy))]
        public static class AIPlane_GetBestAircraftVisibleEnemy_Patch
        {
            public static void Postfix(AIPlane __instance, ref Spottable __result)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value || __instance == null)
                        return;

                    if (VisionConfig.LimitAircraftInfantrySpotting.Value && __result != null)
                    {
                        if (!__result.IsAirVehicle() && !__result.IsWheeledVehicleOrTank())
                        {
                            Soldier targetSoldier = __result.TryCast<Soldier>();
                            if (targetSoldier != null)
                            {
                                Squad sq = targetSoldier.joinedSquad;
                                bool isMarkedSquad = sq != null && (sq.isMarked || sq.isHalfMarked || sq.isMarkedPinPoint);
                                if (!isMarkedSquad)
                                {
                                    float dist = Vector3.Distance(__instance.transform.position, targetSoldier.GetPosition());
                                    bool recentlyFired = GetFiringVisibilityBonus(targetSoldier) > 10f;

                                    if (dist > VisionConfig.AircraftUnmarkedInfantryMaxDist.Value ||
                                        (targetSoldier.m_pose != SoldierPose.Idle && !recentlyFired))
                                    {
                                        BlockedPlaneSpot++;
                                        __result = null;
                                    }
                                }
                            }
                        }
                    }

                    if (VisionConfig.EnableCombinedArmsPrioritySupport.Value)
                    {
                        VeteranAndTacticsPatches.TrySelectKnownAircraftTarget(__instance, ref __result);
                    }
                }
                catch { }
            }
        }
    }

    public static partial class VeteranAndTacticsPatches
    {
        private struct WatchWindowCache
        {
            public Vector3 Pos;
            public float BaseAngleDeg;
            public Vector3 ResultDir;
            public float ExpireTime;
        }

        private static readonly Dictionary<IntPtr, WatchWindowCache> _watchWindowCache = new Dictionary<IntPtr, WatchWindowCache>(256);

        private struct ThreatSectorCache
        {
            public float ExpireTime;
            public Vector3 WatchDir;
            public bool HasActiveContact;
        }

        private static readonly Dictionary<IntPtr, ThreatSectorCache> _threatSectorCache = new Dictionary<IntPtr, ThreatSectorCache>(256);
        private static readonly HashSet<IntPtr> _isTurningToWatchSector = new HashSet<IntPtr>();

        internal struct LocalCalloutRecord
        {
            public Vector3 EnemyPos;
            public float ExpireTime;
        }

        internal struct LastKnownTargetInfo
        {
            public Spottable Target;
            public Vector3 LastSeenPos;
            public float LastSeenTime;
            public bool WasInfantry;
        }

        internal static readonly Dictionary<IntPtr, LocalCalloutRecord> _localCalloutCache = new Dictionary<IntPtr, LocalCalloutRecord>(256);
        internal static readonly Dictionary<IntPtr, float> _nextSpotterCalloutTime = new Dictionary<IntPtr, float>(256);
        internal static readonly Dictionary<IntPtr, LastKnownTargetInfo> _lastKnownTargets = new Dictionary<IntPtr, LastKnownTargetInfo>(256);
        internal static readonly Dictionary<IntPtr, float> _nextSuppressiveGrenadeTime = new Dictionary<IntPtr, float>(256);
        internal static readonly Dictionary<IntPtr, float> _nextSuppressiveBurstCountTime = new Dictionary<IntPtr, float>(256);

        // =====================================================================
        // Helper: Local Contact Callouts (15m) & Last-Known Target Memory
        // =====================================================================
        internal static void RecordAndBroadcastVisualContact(Soldier spotter, Spottable visibleEnemy)
        {
            if (spotter == null || spotter.Pointer == IntPtr.Zero || visibleEnemy == null || visibleEnemy.Pointer == IntPtr.Zero)
                return;

            if (visibleEnemy.IsAirVehicle())
                return;

            IntPtr spotterPtr = spotter.Pointer;
            Vector3 enemyPos = visibleEnemy.GetCenterOfUnit();
            float now = Time.time;

            if (VisionConfig.EnableLastKnownSuppression.Value)
            {
                _lastKnownTargets[spotterPtr] = new LastKnownTargetInfo
                {
                    Target = visibleEnemy,
                    LastSeenPos = enemyPos,
                    LastSeenTime = now,
                    WasInfantry = !visibleEnemy.IsWheeledVehicleOrTank()
                };
            }

            if (!VisionConfig.EnableLocalContactCallouts.Value)
                return;

            if (_nextSpotterCalloutTime.TryGetValue(spotterPtr, out float nextCallout) && now < nextCallout)
                return;

            _nextSpotterCalloutTime[spotterPtr] = now + 1.65f;

            Vector3 spotterPos = spotter.transform.position;
            string myFaction = spotter.faction;
            const float calloutRadiusSq = 15.0f * 15.0f;
            int sharedWith = 0;

            Squad mySquad = spotter.joinedSquad;
            if (mySquad != null && mySquad.units != null)
            {
                var units = mySquad.units;
                int c = units.Count;
                for (int i = 0; i < c; i++)
                {
                    Soldier ally = units[i];
                    if (ally == null || !ally.IsAlive || ally.Pointer == spotterPtr || ally.IsPlayer() || ally.IsOnVehicle())
                        continue;

                    if ((ally.transform.position - spotterPos).sqrMagnitude <= calloutRadiusSq)
                    {
                        IntPtr allyPtr = ally.Pointer;
                        _localCalloutCache[allyPtr] = new LocalCalloutRecord
                        {
                            EnemyPos = enemyPos,
                            ExpireTime = now + 5.5f
                        };
                        AiCore.RefreshAlertnessWithoutRaisingGun(ally, 12f);
                        sharedWith++;
                    }
                }
            }

            var allSquads = Squad.AllSquads;
            if (allSquads != null)
            {
                foreach (var kvp in allSquads)
                {
                    Squad sq = kvp.Value;
                    if (sq == null || sq == mySquad || sq.units == null)
                        continue;

                    Soldier leader = sq.Leader;
                    if (leader == null || leader.faction != myFaction)
                        continue;

                    if ((leader.transform.position - spotterPos).sqrMagnitude > 36f * 36f)
                        continue;

                    var units = sq.units;
                    int c = units.Count;
                    for (int i = 0; i < c; i++)
                    {
                        Soldier ally = units[i];
                        if (ally == null || !ally.IsAlive || ally.Pointer == spotterPtr || ally.IsPlayer() || ally.IsOnVehicle())
                            continue;

                        if ((ally.transform.position - spotterPos).sqrMagnitude <= calloutRadiusSq)
                        {
                            IntPtr allyPtr = ally.Pointer;
                            _localCalloutCache[allyPtr] = new LocalCalloutRecord
                            {
                                EnemyPos = enemyPos,
                                ExpireTime = now + 5.5f
                            };
                            AiCore.RefreshAlertnessWithoutRaisingGun(ally, 12f);
                            sharedWith++;
                        }
                    }
                }
            }

            if (sharedWith > 0)
            {
                LocalCalloutsSharedCount += sharedWith;
                if (VisionConfig.DebugLogging.Value)
                {
                    AiCore.LogDebug("LocalCallout", $"{spotter.faction} soldier shared visual contact with {sharedWith} allies within 15m.");
                }
            }
        }

        internal static bool IsSafeToThrowGrenadeAt(Soldier thrower, Vector3 targetPos, float dist, float friendlySafeRadius = 9.5f)
        {
            if (thrower == null || thrower.transform == null)
                return false;

            Vector3 myPos = thrower.transform.position;

            // 1. Ensure throw arc is not blocked by an interior ceiling, window frame, or wall right in front of the thrower
            Vector3 throwOrigin = myPos + Vector3.up * 1.45f;
            Vector3 arcPeakTarget = targetPos + Vector3.up * 1.60f;
            Vector3 toArc = arcPeakTarget - throwOrigin;
            if (toArc.sqrMagnitude > 0.04f)
            {
                if (AiCore.IsDirectionBlockedByEnvironment(throwOrigin, toArc.normalized, Mathf.Min(dist * 0.50f, 6.5f), thrower.transform.root))
                    return false;
            }

            // 2. Ensure no friendly soldiers (or the player) are within the blast radius of the target position
            string myFaction = thrower.faction;
            float safeRadiusSq = friendlySafeRadius * friendlySafeRadius;

            Soldier player = Soldier.CurrentControlledSoldierOrNull();
            if (player != null && player.IsAlive && player.faction == myFaction && player.transform != null &&
                (player.transform.position - targetPos).sqrMagnitude <= safeRadiusSq)
            {
                return false;
            }

            var allSquads = Squad.AllSquads;
            if (allSquads != null)
            {
                foreach (var kvp in allSquads)
                {
                    Squad sq = kvp.Value;
                    if (sq == null || sq.units == null) continue;
                    Soldier leader = sq.Leader;
                    if (leader == null || leader.faction != myFaction || leader.transform == null) continue;
                    if ((leader.transform.position - targetPos).sqrMagnitude > 48f * 48f) continue;

                    var units = sq.units;
                    int c = units.Count;
                    for (int i = 0; i < c; i++)
                    {
                        Soldier f = units[i];
                        if (f != null && f.IsAlive && f.transform != null &&
                            (f.transform.position - targetPos).sqrMagnitude <= safeRadiusSq)
                        {
                            return false;
                        }
                    }
                }
            }

            return true;
        }

        internal static void TryFlushLastKnownEnemyWithGrenade(SoldierAI ai, Soldier soldier)
        {
            if (!VisionConfig.EnableLastKnownSuppression.Value || ai == null || soldier == null || soldier.Pointer == IntPtr.Zero)
                return;

            if (ai.visibleTarget != null ||
                ai.IsMoving() ||
                soldier.IsOnVehicle() ||
                soldier.isInHandsAnimation ||
                soldier.isInBodyAnimation ||
                soldier.IsReloading ||
                soldier.inventory == null)
            {
                return;
            }

            IntPtr ptr = soldier.Pointer;
            float now = Time.time;
            if (now <= soldier.throw_end_time + 6.0f)
                return;

            if (_nextSuppressiveGrenadeTime.TryGetValue(ptr, out float nextGren) && now < nextGren)
                return;

            if (!_lastKnownTargets.TryGetValue(ptr, out LastKnownTargetInfo lastInfo) || !lastInfo.WasInfantry)
                return;

            float elapsed = now - lastInfo.LastSeenTime;
            if (elapsed < 0.35f || elapsed > 4.5f)
                return;

            if (lastInfo.Target == null || lastInfo.Target.Pointer == IntPtr.Zero)
                return;

            Soldier targetSoldier = lastInfo.Target.TryCast<Soldier>();
            if (targetSoldier == null || !targetSoldier.IsAlive)
                return;

            Vector3 myPos = soldier.transform.position;
            float dist = Vector3.Distance(myPos, lastInfo.LastSeenPos);
            if (dist < 10.0f || dist > 30.0f)
                return;

            VirtualItem fragItem = soldier.inventory.FindItemOfType<VirtualGrenade>();
            if (fragItem == null || dist > soldier.GetGrenadeThrowRange(fragItem))
                return;

            if (!IsSafeToThrowGrenadeAt(soldier, lastInfo.LastSeenPos, dist, 9.5f))
                return;

            if (!AiCore.TryConsumeBotRpcBudget(0.45f))
                return;

            _nextSuppressiveGrenadeTime[ptr] = now + 14.0f;
            soldier.Throw(fragItem, lastInfo.Target);
            SuppressiveEventsCount++;
            if (VisionConfig.DebugLogging.Value)
            {
                AiCore.LogDebug("LastKnownGrenade", $"{soldier.faction} soldier threw frag grenade at last-known enemy cover ({dist:F0}m).");
            }
        }

        private static bool TryMaintainSuppressiveFireOnLastKnown(SoldierAI ai, Soldier soldier, float deltaTime)
        {
            if (!VisionConfig.EnableLastKnownSuppression.Value || ai == null || soldier == null || soldier.Pointer == IntPtr.Zero)
                return false;

            IntPtr ptr = soldier.Pointer;
            if (!_lastKnownTargets.TryGetValue(ptr, out LastKnownTargetInfo lastInfo) || !lastInfo.WasInfantry)
                return false;

            float now = Time.time;
            float elapsed = now - lastInfo.LastSeenTime;
            if (elapsed < 0.08f || elapsed > 2.25f)
                return false;

            if (lastInfo.Target == null || lastInfo.Target.Pointer == IntPtr.Zero)
                return false;

            Soldier targetSoldier = lastInfo.Target.TryCast<Soldier>();
            if (targetSoldier == null || !targetSoldier.IsAlive)
                return false;

            if (soldier.GetSuppressionValue() >= 28f || soldier.IsReloading || soldier.MustReload())
                return false;

            GenericGun heldGun = soldier.GetHeldGun();
            if (heldGun == null)
                return false;

            int ammoLeft = heldGun.GetCurrentAmmoCount();
            if (ammoLeft <= 3)
                return false;

            Vector3 aimPos = lastInfo.LastSeenPos + Vector3.up * 0.45f;
            float dist = Vector3.Distance(soldier.transform.position, aimPos);
            if (dist < 8.0f || dist > 135.0f || heldGun.DamageRange() < dist)
                return false;

            SoldierPersonality pers = GetPersonality(soldier);
            bool isDedicatedGunner = soldier.hasMg ||
                                     soldier.IsGunner() ||
                                     pers == SoldierPersonality.Gunner_Anchor ||
                                     pers == SoldierPersonality.Gunner_Overwatch ||
                                     pers == SoldierPersonality.Gunner_AssaultMG;
            bool isCloseAutoSuppressor = !isDedicatedGunner &&
                                         !heldGun.bolt_required &&
                                         ammoLeft >= 12 &&
                                         dist <= 55.0f;

            if (!isDedicatedGunner && !isCloseAutoSuppressor)
                return false;

            // Verify unobstructed sightline up to the enemy's cover edge (dist - 2.5m) so gunners
            // never blind-fire into intermediate hills, walls, or buildings.
            if (soldier.IsTowardWall())
                return false;

            Vector3 gunPos = soldier.GunPosition();
            Vector3 toAim = aimPos - gunPos;
            float aimLen = toAim.magnitude;
            if (aimLen > 3.5f)
            {
                if (AiCore.RaycastEnvironment(gunPos, toAim / aimLen, aimLen - 2.5f, soldier.transform.root, out _))
                    return false;
            }

            Spottable prevVisible = ai.visibleTarget;
            bool prevAiming = ai.aimingEnemy;
            try
            {
                // Native SoldierAI.RotateAndFireToward (0x18055E454) returns early if visibleTarget == null
                // and checks aimingEnemy (0x18055E7FA) before starting a burst.
                ai.visibleTarget = lastInfo.Target;
                ai.distFromTarget = dist;
                ai.targetInWeaponRange = true;
                ai.allowFireAtEnemy = true;
                ai.aimingEnemy = soldier.AngleToDirection(aimPos - soldier.transform.position) < 25f;
                ai.RotateAndFireToward(aimPos, deltaTime);
            }
            finally
            {
                ai.visibleTarget = prevVisible;
                ai.aimingEnemy = prevAiming;
            }

            if (!_nextSuppressiveBurstCountTime.TryGetValue(ptr, out float nextCount) || now >= nextCount)
            {
                _nextSuppressiveBurstCountTime[ptr] = now + 3.0f;
                SuppressiveEventsCount++;
                if (VisionConfig.DebugLogging.Value)
                {
                    AiCore.LogDebug("LastKnownSupp", $"{soldier.faction} gunner firing suppressive burst at last-known cover ({dist:F0}m).");
                }
            }
            return true;
        }

        internal static void InvalidateWatchWindowCache(IntPtr soldierPtr)
        {
            if (soldierPtr != IntPtr.Zero)
            {
                _watchWindowCache.Remove(soldierPtr);
                _threatSectorCache.Remove(soldierPtr);
            }
        }

        // =====================================================================
        // Helper: Threat-Sector Gaze Concentration & Window-Aperture Alignment
        // =====================================================================
        public static bool HasActiveThreatSectorContact(Soldier soldier)
        {
            if (soldier == null || soldier.Pointer == IntPtr.Zero)
                return false;

            // Pure O(1) dictionary lookup — NEVER trigger Squad.AllSquads iteration or 18-angle window raycast
            // sweeps inside Creature.CanSee! ThreatSectorCache is populated by SequentialUpdate & MovementRoutine.
            return _threatSectorCache.TryGetValue(soldier.Pointer, out ThreatSectorCache cached) &&
                   Time.time < cached.ExpireTime &&
                   cached.HasActiveContact;
        }

        private static readonly float[] _watchSweepOffsets = new float[]
        {
            0f, -18f, 18f, -36f, 36f, -54f, 54f, -72f, 72f, -90f, 90f, -112f, 112f, -135f, 135f, -158f, 158f, 180f
        };

        private static Vector3 CenterGazeInWindowAperture(Vector3 upperOrigin, Vector3 dir, Transform ignoreRoot, float fanT, float maxFanDeg)
        {
            float minOpenDeg = 0f;
            for (int step = 1; step <= 4; step++)
            {
                float deg = -8f * step;
                Vector3 probe = Quaternion.Euler(0f, deg, 0f) * dir;
                if (!AiCore.IsDirectionBlockedByEnvironment(upperOrigin, probe, 5.5f, ignoreRoot))
                    minOpenDeg = deg;
                else
                    break;
            }

            float maxOpenDeg = 0f;
            for (int step = 1; step <= 4; step++)
            {
                float deg = 8f * step;
                Vector3 probe = Quaternion.Euler(0f, deg, 0f) * dir;
                if (!AiCore.IsDirectionBlockedByEnvironment(upperOrigin, probe, 5.5f, ignoreRoot))
                    maxOpenDeg = deg;
                else
                    break;
            }

            float centerDeg = 0.5f * (minOpenDeg + maxOpenDeg);
            float halfWidth = 0.5f * (maxOpenDeg - minOpenDeg);
            float safeMargin = Mathf.Max(0f, halfWidth - 6f);
            float safeFan = Mathf.Clamp(fanT * maxFanDeg, -safeMargin, safeMargin);
            return (Quaternion.Euler(0f, centerDeg + safeFan, 0f) * dir).normalized;
        }

        private static Vector3 PickClearWatchDirection(Soldier soldier, Vector3 myPos, Vector3 baseDir, float maxFanDeg)
        {
            baseDir.y = 0f;
            if (baseDir.sqrMagnitude < 0.001f)
                return Vector3.zero;
            baseDir.Normalize();

            IntPtr ptr = soldier.Pointer;
            float baseAngleDeg = Mathf.Atan2(baseDir.x, baseDir.z) * Mathf.Rad2Deg;
            if (_watchWindowCache.TryGetValue(ptr, out WatchWindowCache wCache) &&
                Time.time < wCache.ExpireTime &&
                (myPos - wCache.Pos).sqrMagnitude < 0.25f &&
                Mathf.Abs(Mathf.DeltaAngle(baseAngleDeg, wCache.BaseAngleDeg)) < 10.0f)
            {
                return wCache.ResultDir;
            }

            int h = AiCore.GetDeterministicHash(ptr, 97);
            float t = ((h % 1001) / 500.0f) - 1.0f;
            float fanAngle = t * maxFanDeg;
            Vector3 fannedDir = Quaternion.Euler(0f, fanAngle, 0f) * baseDir;

            Transform root = soldier.transform.root;
            Vector3 upperOrigin = myPos + Vector3.up * 1.28f;
            Vector3 sillOrigin = myPos + Vector3.up * 0.52f;

            bool fannedBlocked = AiCore.RaycastEnvironment(upperOrigin, fannedDir, 4.5f, root, out _);
            bool fannedHasWindowSill = !fannedBlocked &&
                                       !AiCore.RaycastEnvironment(upperOrigin, fannedDir, 7.5f, root, out _) &&
                                       AiCore.RaycastEnvironment(sillOrigin, fannedDir, 2.35f, root, out _);

            Vector3 finalDir;
            if (fannedHasWindowSill)
            {
                finalDir = CenterGazeInWindowAperture(upperOrigin, fannedDir, root, t, maxFanDeg);
                _watchWindowCache[ptr] = new WatchWindowCache
                {
                    Pos = myPos,
                    BaseAngleDeg = baseAngleDeg,
                    ResultDir = finalDir,
                    ExpireTime = Time.time + 8.0f
                };
                return finalDir;
            }

            bool nearbyWindowDetected = false;
            if (!fannedBlocked)
            {
                Vector3 left90 = new Vector3(-fannedDir.z, 0f, fannedDir.x);
                Vector3 right90 = new Vector3(fannedDir.z, 0f, -fannedDir.x);
                if ((AiCore.RaycastEnvironment(sillOrigin, left90, 2.35f, root, out _) && !AiCore.RaycastEnvironment(upperOrigin, left90, 7.5f, root, out _)) ||
                    (AiCore.RaycastEnvironment(sillOrigin, right90, 2.35f, root, out _) && !AiCore.RaycastEnvironment(upperOrigin, right90, 7.5f, root, out _)))
                {
                    nearbyWindowDetected = true;
                }
            }

            if (!fannedBlocked && !nearbyWindowDetected)
            {
                _watchWindowCache[ptr] = new WatchWindowCache
                {
                    Pos = myPos,
                    BaseAngleDeg = baseAngleDeg,
                    ResultDir = fannedDir,
                    ExpireTime = Time.time + 8.0f
                };
                return fannedDir;
            }

            Vector3 bestDir = fannedDir;
            float bestScore = -999f;
            bool bestIsWindow = false;

            for (int i = 0; i < _watchSweepOffsets.Length; i++)
            {
                float angleOffset = _watchSweepOffsets[i];
                Vector3 cand = i == 0 ? baseDir : (Quaternion.Euler(0f, angleOffset, 0f) * baseDir);

                float clearDist = 18.0f;
                if (AiCore.RaycastEnvironment(upperOrigin, cand, 18.0f, root, out RaycastHit upperHit))
                {
                    clearDist = upperHit.distance;
                }

                if (clearDist < 2.0f)
                    continue;

                bool isWindowAperture = clearDist >= 7.0f &&
                                        AiCore.RaycastEnvironment(sillOrigin, cand, 2.35f, root, out RaycastHit sillHit) &&
                                        sillHit.distance < clearDist - 3.0f;

                float alignDot = Vector3.Dot(cand, baseDir);
                float score = clearDist + (alignDot * 5.5f);
                if (isWindowAperture)
                {
                    score += 18.0f;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    bestDir = cand;
                    bestIsWindow = isWindowAperture;
                }
            }

            finalDir = (bestIsWindow || fannedBlocked)
                ? CenterGazeInWindowAperture(upperOrigin, bestDir, root, t, maxFanDeg)
                : bestDir;

            _watchWindowCache[ptr] = new WatchWindowCache
            {
                Pos = myPos,
                BaseAngleDeg = baseAngleDeg,
                ResultDir = finalDir,
                ExpireTime = Time.time + 8.0f
            };
            return finalDir;
        }

        private static void InsertClosestContact(
            Vector3 dir,
            float distSq,
            ref int count,
            ref Vector3 c0, ref float d0,
            ref Vector3 c1, ref float d1,
            ref Vector3 c2, ref float d2,
            ref Vector3 c3, ref float d3)
        {
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.25f)
                return;
            Vector3 norm = dir.normalized;

            if (count > 0 && Vector3.Dot(norm, c0) > 0.95f)
            {
                if (distSq < d0) { c0 = norm; d0 = distSq; }
                return;
            }
            if (count > 1 && Vector3.Dot(norm, c1) > 0.95f)
            {
                if (distSq < d1) { c1 = norm; d1 = distSq; }
                return;
            }
            if (count > 2 && Vector3.Dot(norm, c2) > 0.95f)
            {
                if (distSq < d2) { c2 = norm; d2 = distSq; }
                return;
            }
            if (count > 3 && Vector3.Dot(norm, c3) > 0.95f)
            {
                if (distSq < d3) { c3 = norm; d3 = distSq; }
                return;
            }

            if (count == 0 || distSq < d0)
            {
                c3 = c2; d3 = d2;
                c2 = c1; d2 = d1;
                c1 = c0; d1 = d0;
                c0 = norm; d0 = distSq;
                if (count < 4) count++;
            }
            else if (count == 1 || distSq < d1)
            {
                c3 = c2; d3 = d2;
                c2 = c1; d2 = d1;
                c1 = norm; d1 = distSq;
                if (count < 4) count++;
            }
            else if (count == 2 || distSq < d2)
            {
                c3 = c2; d3 = d2;
                c2 = norm; d2 = distSq;
                if (count < 4) count++;
            }
            else if (count == 3 || distSq < d3)
            {
                c3 = norm; d3 = distSq;
                if (count < 4) count++;
            }
        }

        private static bool TryGetKnownThreatSectorDirection(Soldier soldier, out Vector3 watchDir, out bool hasActiveContact)
        {
            watchDir = Vector3.zero;
            hasActiveContact = false;
            if (soldier == null || soldier.Pointer == IntPtr.Zero || soldier.transform == null)
                return false;

            IntPtr ptr = soldier.Pointer;
            bool hadPrevCache = _threatSectorCache.TryGetValue(ptr, out ThreatSectorCache cached);
            if (hadPrevCache && Time.time < cached.ExpireTime)
            {
                watchDir = cached.WatchDir;
                hasActiveContact = cached.HasActiveContact;
                return watchDir.sqrMagnitude > 0.01f;
            }

            Vector3 myPos = soldier.transform.position;
            string myFaction = soldier.faction;
            Squad mySquad = soldier.joinedSquad;
            MissionObjective myObj = (mySquad != null && mySquad.currentTask != null)
                ? mySquad.currentTask.TryCast<MissionObjective>()
                : null;

            int contactCount = 0;
            Vector3 c0 = Vector3.zero, c1 = Vector3.zero, c2 = Vector3.zero, c3 = Vector3.zero;
            float d0 = float.MaxValue, d1 = float.MaxValue, d2 = float.MaxValue, d3 = float.MaxValue;

            if (VisionConfig.EnableLocalContactCallouts.Value &&
                _localCalloutCache.TryGetValue(ptr, out LocalCalloutRecord callout) &&
                Time.time < callout.ExpireTime)
            {
                Vector3 toCalledEnemy = callout.EnemyPos - myPos;
                toCalledEnemy.y = 0f;
                float callDistSq = toCalledEnemy.sqrMagnitude;
                if (callDistSq > 0.25f)
                {
                    InsertClosestContact(toCalledEnemy, callDistSq * 0.45f, ref contactCount, ref c0, ref d0, ref c1, ref d1, ref c2, ref d2, ref c3, ref d3);
                }
            }

            if (mySquad != null && mySquad.units != null)
            {
                var units = mySquad.units;
                int uCount = units.Count;
                for (int i = 0; i < uCount; i++)
                {
                    Soldier mate = units[i];
                    if (mate == null || !mate.IsAlive || mate.aiController == null) continue;
                    Spottable mateTarget = mate.aiController.visibleTarget;
                    if (mateTarget == null || mateTarget.IsAirVehicle()) continue;

                    Vector3 toEnemy = mateTarget.GetCenterOfUnit() - myPos;
                    toEnemy.y = 0f;
                    float dSq = toEnemy.sqrMagnitude;
                    if (dSq > 0.25f)
                    {
                        InsertClosestContact(toEnemy, dSq * 0.55f, ref contactCount, ref c0, ref d0, ref c1, ref d1, ref c2, ref d2, ref c3, ref d3);
                    }
                }
            }

            var allSquads = Squad.AllSquads;
            if (allSquads != null)
            {
                foreach (var kvp in allSquads)
                {
                    Squad sq = kvp.Value;
                    if (sq == null || sq == mySquad || sq.units == null) continue;

                    Soldier enemyRef = sq.Leader;
                    if (enemyRef == null || !enemyRef.IsAlive)
                    {
                        var units = sq.units;
                        int uCount = units.Count;
                        for (int i = 0; i < uCount; i++)
                        {
                            Soldier u = units[i];
                            if (u != null && u.IsAlive)
                            {
                                enemyRef = u;
                                break;
                            }
                        }
                    }

                    if (enemyRef == null || enemyRef.faction == myFaction)
                        continue;

                    if (enemyRef.IsOnVehicle())
                    {
                        Vehicle v = enemyRef.GetCurrentVehicle();
                        if (v != null && v.TryCast<VehiclePlane>() != null)
                            continue;
                    }

                    Vector3 toSquad = enemyRef.transform.position - myPos;
                    toSquad.y = 0f;
                    float dSq = toSquad.sqrMagnitude;
                    if (dSq > 175f * 175f || dSq < 0.25f)
                        continue;

                    bool isKnownContact = sq.IsEnemyAwareOfThisSquad ||
                                          sq.isMarked ||
                                          sq.isHalfMarked ||
                                          sq.isMarkedPinPoint ||
                                          enemyRef.IsSpotted;

                    Vector3 contactVector = toSquad;

                    if (!isKnownContact && dSq <= 150f * 150f)
                    {
                        var units = sq.units;
                        int uCount = units.Count;
                        for (int i = 0; i < uCount; i++)
                        {
                            Soldier u = units[i];
                            if (u == null || !u.IsAlive) continue;
                            if (u.IsSpotted || VisionPatches.HasFiredRecently(u, 4.5f))
                            {
                                isKnownContact = true;
                                contactVector = u.transform.position - myPos;
                                contactVector.y = 0f;
                                dSq = contactVector.sqrMagnitude;
                                break;
                            }
                        }
                    }

                    if (!isKnownContact && myObj != null && dSq <= 55f * 55f)
                    {
                        if (enemyRef.PositionIsInsideValidConquerZone(enemyRef.transform.position, myObj))
                        {
                            isKnownContact = true;
                        }
                    }

                    if (isKnownContact)
                    {
                        InsertClosestContact(contactVector, dSq, ref contactCount, ref c0, ref d0, ref c1, ref d1, ref c2, ref d2, ref c3, ref d3);
                    }
                }
            }

            Soldier player = Soldier.CurrentControlledSoldierOrNull();
            if (player != null && player.IsAlive && player.faction != myFaction)
            {
                Vector3 toPlayer = player.transform.position - myPos;
                toPlayer.y = 0f;
                float pDistSq = toPlayer.sqrMagnitude;
                if (pDistSq <= 175f * 175f && pDistSq > 0.25f)
                {
                    bool playerIsKnown = player.IsSpotted ||
                                         VisionPatches.HasFiredRecently(player, 4.5f) ||
                                         (player.joinedSquad != null &&
                                          (player.joinedSquad.IsEnemyAwareOfThisSquad ||
                                           player.joinedSquad.isMarked ||
                                           player.joinedSquad.isHalfMarked ||
                                           player.joinedSquad.isMarkedPinPoint));

                    if (playerIsKnown)
                    {
                        InsertClosestContact(toPlayer, pDistSq, ref contactCount, ref c0, ref d0, ref c1, ref d1, ref c2, ref d2, ref c3, ref d3);
                    }
                }
            }

            if (contactCount > 0)
            {
                hasActiveContact = true;
                // Avoid "% contactCount" index shifts when distant 3rd/4th contacts flicker in and out;
                // only split gaze between c0 and c1 when c1 is comparable in distance to c0.
                bool useSecondContact = contactCount >= 2 &&
                                        d1 <= d0 * 1.65f &&
                                        (AiCore.GetDeterministicHash(ptr, 41) & 1) == 1;
                Vector3 chosenBase = useSecondContact ? c1 : c0;
                float maxFan = (soldier.hasMg || soldier.isMarksMan) ? 8f : 14f;
                watchDir = PickClearWatchDirection(soldier, myPos, chosenBase, maxFan);
            }
            else
            {
                hasActiveContact = false;
                Vector3 fallbackBase = soldier.transform.forward;
                BattleManager bm = BattleManager.instance;
                BattlePhase phase = bm != null ? bm.currentPhase : null;
                if (phase != null && !string.IsNullOrEmpty(myFaction) && phase.EnemyFactionHasSpawn(myFaction))
                {
                    Vector3 toEnemySpawn = phase.GetOppositeFactionTerritoryPosition(myFaction) - myPos;
                    toEnemySpawn.y = 0f;
                    if (toEnemySpawn.sqrMagnitude > 1.0f)
                    {
                        fallbackBase = toEnemySpawn;
                    }
                }
                watchDir = PickClearWatchDirection(soldier, myPos, fallbackBase, 24f);
            }

            // Directional hysteresis: keep previous watch direction if the new sector is within ~25 degrees
            if (hadPrevCache &&
                cached.HasActiveContact == hasActiveContact &&
                cached.WatchDir.sqrMagnitude > 0.01f &&
                watchDir.sqrMagnitude > 0.01f &&
                Vector3.Dot(cached.WatchDir, watchDir) >= 0.90f)
            {
                watchDir = cached.WatchDir;
            }

            float sectorJitter = (AiCore.GetDeterministicHash(ptr, 53) % 55) * 0.01f;
            float cacheDuration = hasActiveContact ? 2.80f : 4.50f;
            _threatSectorCache[ptr] = new ThreatSectorCache
            {
                ExpireTime = Time.time + cacheDuration + sectorJitter,
                WatchDir = watchDir,
                HasActiveContact = hasActiveContact
            };

            return watchDir.sqrMagnitude > 0.01f;
        }

        // =====================================================================
        // Stationary Threat-Sector Gaze Rotation (SoldierAI.MovementRoutine)
        // =====================================================================
        [HarmonyPatch(typeof(SoldierAI), nameof(SoldierAI.MovementRoutine))]
        public static class SoldierAI_MovementRoutine_Patch
        {
            public static void Postfix(SoldierAI __instance, float __0)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value || __instance == null)
                        return;

                    Soldier soldier = __instance.character;
                    if (!AiCore.IsLocallyOwnedBot(soldier))
                        return;

                    IntPtr ptr = soldier.Pointer;

                    if (__instance.IsMoving() ||
                        __instance.HasEnemyAndCanFireAtHim ||
                        __instance.HeldMortar != null ||
                        soldier.IsOnVehicle() ||
                        soldier.isInBodyAnimation ||
                        soldier.isInHandsAnimation ||
                        soldier.HasSurrended() ||
                        soldier.IsCarried() ||
                        soldier.IsCarryingBody())
                    {
                        _isTurningToWatchSector.Remove(ptr);
                        return;
                    }

                    // Last-Known Position Suppressive Fire for stationary gunners / automatic weapons
                    if (VisionConfig.EnableLastKnownSuppression.Value &&
                        __instance.visibleTarget == null &&
                        TryMaintainSuppressiveFireOnLastKnown(__instance, soldier, __0))
                    {
                        _isTurningToWatchSector.Remove(ptr);
                        return;
                    }

                    if (!VisionConfig.EnableThreatSectorConcentration.Value ||
                        soldier.UsingBipod() ||
                        soldier.IsBipodTransitioning())
                    {
                        _isTurningToWatchSector.Remove(ptr);
                        return;
                    }

                    if (TryGetKnownThreatSectorDirection(soldier, out Vector3 watchDir, out _) &&
                        watchDir.sqrMagnitude > 0.01f)
                    {
                        float angle = soldier.AngleToDirection(watchDir);
                        if (_isTurningToWatchSector.Contains(ptr))
                        {
                            if (angle <= 4.0f)
                            {
                                _isTurningToWatchSector.Remove(ptr);
                            }
                            else
                            {
                                AiCore.RotateBodyWithoutRaisingGun(soldier, watchDir, __0);
                            }
                        }
                        else if (angle > 14.0f)
                        {
                            _isTurningToWatchSector.Add(ptr);
                            ThreatSectorOrientationsCount++;
                            AiCore.RotateBodyWithoutRaisingGun(soldier, watchDir, __0);
                        }
                    }
                }
                catch { }
            }
        }
    }
}
