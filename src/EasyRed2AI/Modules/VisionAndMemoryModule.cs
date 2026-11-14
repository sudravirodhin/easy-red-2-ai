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

        // Thread-local flag set only when an alive Tank Commander / close infantry escort checks optical LOS
        // to hand off an off-bore threat to an enclosed turret gunner over crew intercom.
        [ThreadStatic]
        internal static bool AllowCommanderIntercomFovBypass;

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

        private struct RecentGunfireEntry
        {
            public IntPtr ShooterPtr;
            public Vector3 Pos;
            public string Faction;
            public float FiredTime;
        }

        private const int GunfireRingSize = 32;
        private const int GunfireRingMask = GunfireRingSize - 1;
        private static readonly RecentGunfireEntry[] _gunfireRing = new RecentGunfireEntry[GunfireRingSize];
        private static int _gunfireRingHead;

        public static void RecordSoldierFired(Soldier s)
        {
            if (s == null || s.Pointer == IntPtr.Zero) return;
            float now = Time.time;
            IntPtr ptr = s.Pointer;
            _lastFiredTime[ptr] = now;

            AiCore.SoldierFastHandles h = AiCore.GetOrCreateSoldierHandles(s);
            if (h == null || h.Tr == null)
                return;

            Vector3 pos = h.Tr.position;
            string fac = AiCore.GetCachedFaction(h, now);

            // Coalesce rapid-fire bursts from the same shooter so one MG42 doesn't evict 31 other shooters
            for (int i = 0; i < GunfireRingSize; i++)
            {
                if (_gunfireRing[i].ShooterPtr == ptr)
                {
                    _gunfireRing[i].Pos = pos;
                    _gunfireRing[i].Faction = fac;
                    _gunfireRing[i].FiredTime = now;
                    return;
                }
            }

            int slot = _gunfireRingHead & GunfireRingMask;
            _gunfireRingHead = (slot + 1) & GunfireRingMask;
            _gunfireRing[slot] = new RecentGunfireEntry
            {
                ShooterPtr = ptr,
                Pos = pos,
                Faction = fac,
                FiredTime = now
            };
        }

        /// <summary>
        /// Pure L1-cache scan over the 32 most recent shooters (zero IL2CPP calls, zero heap allocations).
        /// Used to suppress suicidal melee lunges and prevent bandaging right next to active enemy gunfire.
        /// </summary>
        public static bool TryGetNearestEnemyGunfire(
            Vector3 observerPos,
            string observerFaction,
            float maxRadius,
            float maxAgeSeconds,
            out float nearestDist)
        {
            nearestDist = -1f;
            if (string.IsNullOrEmpty(observerFaction) || maxRadius <= 0f)
                return false;

            float now = Time.time;
            float maxDistSq = maxRadius * maxRadius;
            float bestDistSq = maxDistSq + 1f;
            bool found = false;

            for (int i = 0; i < GunfireRingSize; i++)
            {
                ref RecentGunfireEntry e = ref _gunfireRing[i];
                if (e.ShooterPtr == IntPtr.Zero)
                    continue;

                float age = now - e.FiredTime;
                if (age < 0f || age > maxAgeSeconds)
                    continue;

                float dx = e.Pos.x - observerPos.x;
                float dy = e.Pos.y - observerPos.y;
                float dz = e.Pos.z - observerPos.z;
                float distSq = dx * dx + dy * dy + dz * dz;
                if (distSq >= bestDistSq || distSq > maxDistSq)
                    continue;

                if (!AiCore.AreEnemyFactions(observerFaction, e.Faction))
                    continue;

                bestDistSq = distSq;
                found = true;
            }

            if (found)
            {
                nearestDist = Mathf.Sqrt(bestDistSq);
                return true;
            }
            return false;
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

        private static readonly List<IntPtr> _reusableExpiredFirePtrs = new List<IntPtr>(64);

        public static void ClearVisionCaches()
        {
            _lastFiredTime.Clear();
            Array.Clear(_gunfireRing, 0, GunfireRingSize);
            _gunfireRingHead = 0;
            _nextCleanupTime = 0f;
        }

        public static void CleanupExpiredFiringRecords()
        {
            if (Time.time <= _nextCleanupTime && _nextCleanupTime - Time.time <= 30f)
                return;

            if (AiCore.IsHeavyWorkFrame() && Time.time < _nextCleanupTime + 1.0f)
                return;

            _nextCleanupTime = Time.time + 15f;
            float cutoff = Time.time - Mathf.Max(10f, VisionConfig.FiringRevealDuration.Value);
            _reusableExpiredFirePtrs.Clear();
            foreach (var kvp in _lastFiredTime)
            {
                if (kvp.Value < cutoff) _reusableExpiredFirePtrs.Add(kvp.Key);
            }
            for (int i = 0; i < _reusableExpiredFirePtrs.Count; i++)
            {
                _lastFiredTime.Remove(_reusableExpiredFirePtrs[i]);
            }
            _reusableExpiredFirePtrs.Clear();

            if (VisionConfig.DebugLogging.Value && TotalSightChecks > 0)
            {
                AiCore.LogDebug(
                    "SightStats",
                    $"SightChecks={TotalSightChecks}, BlockedFOV={BlockedByFOV}, BlockedRange={BlockedByRange}, ScopeBoosts={BoostedByScope}, BlockedPlane={BlockedPlaneSpot}",
                    15.0f);
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
                    if (!VisionConfig.EnableMod.Value || ReferenceEquals(__instance, null) || __instance.Pointer == IntPtr.Zero)
                        return true;

                    AiCore.SoldierFastHandles h = AiCore.GetOrCreateSoldierHandles(__instance);
                    if (h == null)
                        return true;

                    float camoMultiplier = AiCore.GetCachedCamoMultiplier(h, Time.time);

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
            public static bool Prefix(Creature __instance, Spottable __0, ref bool __result, out long __state)
            {
                long pStart = HitchProfiler.Begin();
                __state = pStart;
                bool runVanillaLinecast = true;
                _currentObserverOpticMultiplier = 1.0f;
                try
                {
                    if (!VisionConfig.EnableMod.Value || ReferenceEquals(__instance, null) || ReferenceEquals(__0, null))
                        return true;

                    if (!AiCore.TryResolveSpottableFast(__0, out AiCore.SoldierFastHandles targetSolH, out AiCore.VehicleFastHandles targetVehH))
                    {
                        __result = false;
                        runVanillaLinecast = false;
                        return false;
                    }

                    Soldier targetSoldier = targetSolH != null ? targetSolH.Ref : null;
                    if (targetSoldier != null && targetSoldier.IsOnVehicle())
                    {
                        AiCore.VehicleFastHandles tvH = AiCore.GetOrCreateVehicleHandles(targetSoldier.GetCurrentVehicle());
                        if (tvH != null && tvH.IsAir)
                        {
                            // Never allow ground infantry or turrets to target a pilot's Soldier body inside an aircraft cockpit;
                            // they must target the Airplane vehicle itself.
                            __result = false;
                            runVanillaLinecast = false;
                            return false;
                        }
                    }

                    TotalSightChecks++;

                    if (!AiCore.TryGetSoldierHandlesFromCreature(__instance, out AiCore.SoldierFastHandles obsH) || obsH.Tr == null)
                        return true;

                    Soldier observer = obsH.Ref;
                    Vector3 observerPos = observer.FacePosition();
                    Vector3 targetPos = __0.SpotPosition();
                    Vector3 toTarget = targetPos - observerPos;
                    float dist = toTarget.magnitude;

                    Vector3 bodyFwd = obsH.Tr.forward;
                    float angle;
                    if (observer.IsAiming)
                    {
                        Vector3 gunDir = observer.GetFireDir();
                        float gunAngle = Vector3.Angle(toTarget, gunDir);
                        // At close/near combat range (<= 34m), human peripheral vision includes torso/head orientation
                        // even when shouldering a rifle slightly off to one side.
                        angle = dist <= 34.0f
                            ? Mathf.Min(gunAngle, Vector3.Angle(toTarget, bodyFwd))
                            : gunAngle;
                    }
                    else
                    {
                        angle = Vector3.Angle(toTarget, bodyFwd);
                    }

                    SoldierAI obsAi = obsH.Ai;
                    bool isStationaryOverwatch = VisionConfig.EnableThreatSectorConcentration.Value &&
                                                 !observer.IsPlayer() &&
                                                 !observer.IsOnVehicle() &&
                                                 !ReferenceEquals(obsAi, null) &&
                                                 obsAi != null &&
                                                 !obsAi.IsMoving();
                    bool hasActiveThreatSector = isStationaryOverwatch &&
                                                 VeteranAndTacticsPatches.HasActiveThreatSectorContact(observer);
                    bool isQuietFlanker = targetSoldier != null &&
                                          !targetSoldier.IsSpotted &&
                                          !HasFiredRecently(targetSoldier, 4.0f);

                    if (VisionConfig.EnforceHumanFOVWhenAlerted.Value)
                    {
                        float allowedHalfFOV = AiCore.EvaluateHumanSquintHalfFov(
                            dist,
                            VisionConfig.CloseOmnidirectionalRange.Value,
                            VisionConfig.MidRangeMaxDistance.Value,
                            VisionConfig.MidRangeHalfFOV.Value,
                            VisionConfig.LongRangeHalfFOV.Value);

                        // Only narrow peripheral awareness for stationary overwatch beyond 34m so close-quarters
                        // flankers (12m-34m) never hit an artificial 60-degree blind-spot wall!
                        if (hasActiveThreatSector && isQuietFlanker && dist > 34.0f)
                        {
                            float focusCap = Mathf.Lerp(76.0f, 52.0f, Mathf.InverseLerp(34.0f, 95.0f, dist));
                            allowedHalfFOV = Mathf.Min(allowedHalfFOV, focusCap);
                        }

                        if (angle > allowedHalfFOV)
                        {
                            BlockedByFOV++;
                            __result = false;
                            runVanillaLinecast = false;
                            return false;
                        }
                    }

                    float opticZoom = VisionConfig.EnableScopeVisionScaling.Value
                        ? AiCore.GetCachedOpticZoom(obsH, Time.time)
                        : 1.0f;

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

                    bool isWheeledOrTank = targetVehH != null ? targetVehH.IsWheeledOrTank : (targetSolH == null && __0.IsWheeledVehicleOrTank());
                    if (isWheeledOrTank && dist > VisionConfig.VehicleCamoDistance.Value * GetObserverOpticMultiplier())
                    {
                        BlockedByRange++;
                        __result = false;
                        runVanillaLinecast = false;
                        return false;
                    }

                    return true;
                }
                catch
                {
                    _currentObserverOpticMultiplier = 1.0f;
                    return true;
                }
                finally
                {
                    HitchProfiler.End(HitchBucket.CanSeeMod, pStart);
                    if (runVanillaLinecast && __instance != null && __0 != null)
                        HitchProfiler.RecordCanSeeLinecastPassThrough();
                }
            }

            public static void Postfix(bool __result, long __state)
            {
                if (!__result && VisionConfig.EnableMod.Value)
                {
                    BlockedByRange++;
                }
                _currentObserverOpticMultiplier = 1.0f;
                HitchProfiler.End(HitchBucket.CanSeeTotal, __state);
            }
        }

        // =====================================================================
        // 4. Vehicle & Tank Turret Vision (TurretAI.CanSee)
        // =====================================================================
        [HarmonyPatch(typeof(TurretAI), nameof(TurretAI.CanSee))]
        public static class TurretAI_CanSee_Patch
        {
            public static bool Prefix(TurretAI __instance, Spottable __0, ref bool __result, out long __state)
            {
                __state = HitchProfiler.Begin();
                _currentObserverOpticMultiplier = 1.0f;
                try
                {
                    if (!VisionConfig.EnableMod.Value || ReferenceEquals(__instance, null) || ReferenceEquals(__0, null))
                        return true;

                    Turret turret = __instance.turret;
                    if (turret == null || !AiCore.TryResolveSpottableFast(__0, out AiCore.SoldierFastHandles targetSolH, out AiCore.VehicleFastHandles targetVehH))
                    {
                        __result = false;
                        return false;
                    }

                    Soldier targetSoldier = targetSolH != null ? targetSolH.Ref : null;
                    if (targetSoldier != null && targetSoldier.IsOnVehicle())
                    {
                        AiCore.VehicleFastHandles tvH = AiCore.GetOrCreateVehicleHandles(targetSoldier.GetCurrentVehicle());
                        if (tvH != null && tvH.IsAir)
                        {
                            // Never allow ground/vehicle turrets to target the pilot Soldier inside an airplane cockpit;
                            // turrets must target the Airplane vehicle itself (fixes pilot killed by "Machine Gun").
                            __result = false;
                            return false;
                        }
                    }

                    TotalSightChecks++;

                    Vector3 firePos = turret.GetFirePos();
                    Vector3 targetPos = __0.SpotPosition();
                    Vector3 toTarget = targetPos - firePos;
                    float dist = toTarget.magnitude;
                    Vector3 fireDir = turret.GetFireDir();
                    float angle = Vector3.Angle(toTarget, fireDir);

                    bool isWheeledOrTank = targetVehH != null ? targetVehH.IsWheeledOrTank : (targetSolH == null && __0.IsWheeledVehicleOrTank());
                    bool isAir = targetVehH != null ? targetVehH.IsAir : (targetSolH == null && __0.IsAirVehicle());
                    bool isInfantryTarget = !isWheeledOrTank && !isAir;

                    AiCore.TurretFastTraits traits = AiCore.GetCachedTurretTraits(turret);
                    float turretAimFov = traits.AimFov > 0.1f ? traits.AimFov : turret.GetAimingFOV();
                    Vehicle turretVeh = traits.RootVehicle;
                    AiCore.VehicleFastHandles obsVehH = traits.VehHandles;
                    VeteranAndTacticsPatches.SoldierPersonality crewPers = turretVeh != null
                        ? VeteranAndTacticsPatches.GetVehicleCrewPersonality(turretVeh)
                        : VeteranAndTacticsPatches.SoldierPersonality.None;

                    Spottable curTracked = __instance.CurrentVisibleTarget;
                    bool isAlreadyTrackingThis = !ReferenceEquals(curTracked, null) &&
                                                 curTracked != null &&
                                                 curTracked.Pointer == __0.Pointer;

                    if (VisionConfig.EnforceHumanFOVWhenAlerted.Value &&
                        !isAlreadyTrackingThis &&
                        !AllowCommanderIntercomFovBypass)
                    {
                        float allowedTurretHalfFov = 180.0f;

                        if (traits.IsCommanderTurret)
                        {
                            // 1. Tank Commander Cupola / Periscope / Binoculars:
                            //    Wide all-around observation up close (unbuttoned cupola vs periscope blocks), squinting with distance.
                            if (dist <= 13.0f)
                                allowedTurretHalfFov = traits.IsExposedCrewSeat ? 180.0f : 132.0f;
                            else if (dist <= 65.0f)
                                allowedTurretHalfFov = Mathf.Lerp(120.0f, 76.0f, (dist - 13.0f) / 52.0f);
                            else
                                allowedTurretHalfFov = Mathf.Lerp(76.0f, 44.0f, Mathf.Clamp01((dist - 65.0f) / 155.0f));
                        }
                        else if (obsVehH != null && obsVehH.IsAir)
                        {
                            // 2. Aircraft Defensive / Flexible Gunners: wide framed hemisphere, squinting at range.
                            allowedTurretHalfFov = dist <= 120.0f
                                ? 86.0f
                                : Mathf.Lerp(86.0f, 54.0f, Mathf.Clamp01((dist - 120.0f) / 280.0f));
                        }
                        else if (obsVehH != null && obsVehH.IsWheeledOrTank && !traits.IsExposedCrewSeat && !traits.IsAA)
                        {
                            // 3. Enclosed / Buttoned-Up Tank & Armored Vehicle Guns (Main Cannon, Coax, Hull Bow MG):
                            //    NO magical 360-degree bubble! Gunner stares through a narrow telescope/ball-mount slit;
                            //    flankers outside this bore cone require an alive Commander / escort intercom handoff!
                            float closeSlit = turretAimFov < 42.0f ? 28.0f : 34.0f;
                            float midSlit = turretAimFov < 42.0f ? 22.0f : 27.0f;
                            float farSlit = turretAimFov < 42.0f ? 15.0f : 19.0f;
                            if (!isInfantryTarget)
                            {
                                // Large vehicle silhouettes & muzzle flashes are slightly easier to catch at the edge of a vision block
                                closeSlit += 8.0f;
                                midSlit += 6.0f;
                                farSlit += 4.0f;
                            }

                            if (dist <= 10.0f)
                                allowedTurretHalfFov = closeSlit + 6.0f;
                            else if (dist <= 45.0f)
                                allowedTurretHalfFov = Mathf.Lerp(closeSlit, midSlit, (dist - 10.0f) / 35.0f);
                            else
                                allowedTurretHalfFov = Mathf.Lerp(midSlit, farSlit, Mathf.Clamp01((dist - 45.0f) / 165.0f));
                        }
                        else if (traits.IsAA)
                        {
                            // 4A. Open 360-degree Flak / AA Mounts: point-blank ear awareness + wide sky/horizon scan.
                            if (dist <= 9.5f)
                                allowedTurretHalfFov = 180.0f;
                            else if (dist <= 125.0f)
                                allowedTurretHalfFov = 90.0f + (crewPers == VeteranAndTacticsPatches.SoldierPersonality.LightCrew_FlakWatch ? 12.0f : 0f);
                            else
                                allowedTurretHalfFov = Mathf.Lerp(88.0f, 58.0f, Mathf.Clamp01((dist - 125.0f) / 220.0f));
                        }
                        else
                        {
                            // 4B. Static Emplacements (Shielded AT Guns, HMG Tripods/Pillboxes) & Open-Top Pintle MGs:
                            //     Exposed crew hear/sense point-blank threats (<= 8.5m), restricted laterally by gun shield / embrasure.
                            if (traits.IsExposedCrewSeat && dist <= 8.5f)
                                allowedTurretHalfFov = 180.0f;
                            else if (dist <= 35.0f)
                                allowedTurretHalfFov = Mathf.Lerp(traits.IsExposedCrewSeat ? 68.0f : 52.0f, 48.0f, Mathf.Clamp01((dist - 8.5f) / 26.5f));
                            else
                                allowedTurretHalfFov = Mathf.Lerp(48.0f, 30.0f, Mathf.Clamp01((dist - 35.0f) / 150.0f));
                        }

                        if (angle > allowedTurretHalfFov)
                        {
                            BlockedByFOV++;
                            __result = false;
                            return false;
                        }
                    }

                    if (VisionConfig.EnableScopeVisionScaling.Value)
                    {
                        if (traits.IsCommanderTurret && angle <= 26.0f)
                        {
                            _currentObserverOpticMultiplier = Mathf.Min(1.45f, VisionConfig.MaxOpticZoomMultiplier.Value);
                            BoostedByScope++;
                        }
                        else if (turretAimFov > 1f && turretAimFov < 45f)
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
                    }

                    if (isWheeledOrTank && dist > VisionConfig.VehicleCamoDistance.Value * GetObserverOpticMultiplier())
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

            public static void Postfix(long __state)
            {
                _currentObserverOpticMultiplier = 1.0f;
                HitchProfiler.End(HitchBucket.TurretPlaneSee, __state);
            }
        }

        // =====================================================================
        // 5. Aircraft Ground Spotting & CAS Target Priority (AIPlane.GetBestAircraftVisibleEnemy)
        // =====================================================================
        [HarmonyPatch(typeof(AIPlane), nameof(AIPlane.GetBestAircraftVisibleEnemy))]
        public static class AIPlane_GetBestAircraftVisibleEnemy_Patch
        {
            public static void Prefix(out long __state)
            {
                __state = HitchProfiler.Begin();
            }

            public static void Postfix(AIPlane __instance, ref Spottable __result, long __state)
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
                finally
                {
                    HitchProfiler.End(HitchBucket.TurretPlaneSee, __state);
                }
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

        internal struct CornerPieState
        {
            public float NextRefreshTime;
            public bool IsPieing;
            public bool RaiseRifle;
            public bool HasVerifiedCorner;
            public Vector3 PieLookDir;
            public Vector3 CachedNode0;
            public Vector3 CachedNode1;
        }

        internal struct StationaryWatchState
        {
            public float NextEvalTime;
            public bool HasWatchDir;
            public Vector3 WatchDir;
        }

        internal struct CoverEyeBlockState
        {
            public IntPtr CoverPtr;
            public SoldierPose Pose;
            public float ExpireTime;
            public bool Blocked;
        }

        internal struct TrenchCollisionGuardState
        {
            public float NextProbeTime;
            public float ForceUntilTime;
            public Vector3 LastProbePos;
        }

        internal static readonly Dictionary<IntPtr, CornerPieState> _cornerPieCache = new Dictionary<IntPtr, CornerPieState>(256);
        internal static readonly Dictionary<IntPtr, float> _nextCornerPieCountTime = new Dictionary<IntPtr, float>(256);
        internal static readonly Dictionary<IntPtr, StationaryWatchState> _stationaryWatchCache = new Dictionary<IntPtr, StationaryWatchState>(256);
        internal static readonly Dictionary<IntPtr, CoverEyeBlockState> _coverEyeBlockCache = new Dictionary<IntPtr, CoverEyeBlockState>(256);
        internal static readonly Dictionary<IntPtr, TrenchCollisionGuardState> _trenchCollisionGuardCache = new Dictionary<IntPtr, TrenchCollisionGuardState>(256);
        public static int CornerPiesExecutedCount;

        // =====================================================================
        // Helper: Local Contact Callouts (15m) & Last-Known Target Memory
        // =====================================================================
        internal static void RecordAndBroadcastVisualContact(Soldier spotter, Spottable visibleEnemy)
        {
            AiCore.SoldierFastHandles spotterH = AiCore.GetOrCreateSoldierHandles(spotter);
            if (spotterH == null || spotterH.Tr == null ||
                !AiCore.TryResolveSpottableFast(visibleEnemy, out AiCore.SoldierFastHandles enemySolH, out AiCore.VehicleFastHandles enemyVehH))
            {
                return;
            }

            if ((enemyVehH != null && enemyVehH.IsAir) || (enemySolH == null && enemyVehH == null && visibleEnemy.IsAirVehicle()))
                return;

            Soldier enemySoldier = enemySolH != null ? enemySolH.Ref : null;
            if (enemySoldier != null && enemySoldier.IsOnVehicle())
            {
                AiCore.VehicleFastHandles evH = AiCore.GetOrCreateVehicleHandles(enemySoldier.GetCurrentVehicle());
                if (evH != null && evH.IsAir)
                    return;
            }

            IntPtr spotterPtr = spotterH.Ptr;
            Vector3 enemyPos = visibleEnemy.GetCenterOfUnit();
            float now = Time.time;

            if (VisionConfig.EnableLastKnownSuppression.Value)
            {
                bool isWheeledOrTank = enemyVehH != null ? enemyVehH.IsWheeledOrTank : (enemySolH == null && visibleEnemy.IsWheeledVehicleOrTank());
                _lastKnownTargets[spotterPtr] = new LastKnownTargetInfo
                {
                    Target = visibleEnemy,
                    LastSeenPos = enemyPos,
                    LastSeenTime = now,
                    WasInfantry = !isWheeledOrTank
                };
            }

            if (!VisionConfig.EnableLocalContactCallouts.Value)
                return;

            if (_nextSpotterCalloutTime.TryGetValue(spotterPtr, out float nextCallout) && now < nextCallout)
                return;

            bool isMpWithPlayers = AiCore.IsMultiplayerWithRemotePlayers();
            _nextSpotterCalloutTime[spotterPtr] = now + (isMpWithPlayers ? 4.25f : 2.50f);

            Vector3 spotterPos = spotterH.Tr.position;
            string myFaction = AiCore.GetCachedFaction(spotterH, now);
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
                    if (ReferenceEquals(ally, null) || ally == null || !ally.IsAlive || ally.Pointer == spotterPtr || ally.IsPlayer() || ally.IsOnVehicle())
                        continue;

                    AiCore.SoldierFastHandles allyH = AiCore.GetOrCreateSoldierHandles(ally);
                    if (allyH != null && allyH.Tr != null && (allyH.Tr.position - spotterPos).sqrMagnitude <= calloutRadiusSq)
                    {
                        IntPtr allyPtr = allyH.Ptr;
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

            if (!isMpWithPlayers && sharedWith == 0)
            {
                AiCore.EnsureWorldSnapshot();
                int sqCount = AiCore.WorldSquadCount;
                if (sqCount > 0)
                {
                    IntPtr mySqPtr = mySquad != null ? mySquad.Pointer : IntPtr.Zero;
                    var squads = AiCore.WorldSquads;
                    var soldiers = AiCore.WorldSoldiers;

                    for (int s = 0; s < sqCount; s++)
                    {
                        ref AiCore.SquadSnapshot sqSnap = ref squads[s];
                        if (sqSnap.Ptr == mySqPtr || sqSnap.UnitCount == 0 || sqSnap.Faction != myFaction)
                            continue;

                        if ((sqSnap.RefPos - spotterPos).sqrMagnitude > 36f * 36f)
                            continue;

                        int end = sqSnap.UnitStartIndex + sqSnap.UnitCount;
                        for (int i = sqSnap.UnitStartIndex; i < end; i++)
                        {
                            ref AiCore.SoldierSnapshot allySnap = ref soldiers[i];
                            if (allySnap.Ptr == spotterPtr || allySnap.IsPlayer || allySnap.IsOnVehicle)
                                continue;

                            if ((allySnap.Pos - spotterPos).sqrMagnitude <= calloutRadiusSq)
                            {
                                Soldier ally = allySnap.Ref;
                                if (ally == null || !ally.IsAlive)
                                    continue;

                                _localCalloutCache[allySnap.Ptr] = new LocalCalloutRecord
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

        internal static bool IsSafeToThrowGrenadeAt(Soldier thrower, Vector3 targetPos, float dist, float friendlySafeRadius = 11.5f)
        {
            if (thrower == null || thrower.transform == null)
                return false;

            Vector3 myPos = thrower.transform.position;
            if (dist <= 0.01f)
            {
                dist = Vector3.Distance(myPos, targetPos);
            }

            // Never throw lethal grenades at point-blank targets (< 8.5m) where blast radius endangers thrower and squadmates
            if (dist < 8.5f)
            {
                VeteranAndTacticsPatches.FriendlyFirePrevented++;
                return false;
            }

            // Never throw grenades from indoors beneath a low ceiling (high risk of bouncing off window lintel/ceiling)
            if (AiCore.HasLowCeilingOverhead(myPos, 3.2f))
            {
                VeteranAndTacticsPatches.WallsBlocked++;
                return false;
            }

            // 1. Ensure throw arc is not blocked by an interior ceiling, window frame, or wall right in front of the thrower
            Vector3 throwOrigin = myPos + Vector3.up * 1.45f;
            Vector3 arcPeakTarget = targetPos + Vector3.up * 1.85f;
            Vector3 toArc = arcPeakTarget - throwOrigin;
            if (toArc.sqrMagnitude > 0.04f)
            {
                Vector3 arcDir = toArc.normalized;
                float checkLen = Mathf.Min(dist * 0.55f, 7.5f);
                if (AiCore.IsDirectionBlockedByEnvironment(throwOrigin, arcDir, checkLen, thrower.transform.root))
                {
                    VeteranAndTacticsPatches.WallsBlocked++;
                    return false;
                }

                Vector3 highArcDir = (arcDir + Vector3.up * 0.35f).normalized;
                if (AiCore.IsDirectionBlockedByEnvironment(throwOrigin, highArcDir, Mathf.Min(checkLen, 5.0f), thrower.transform.root))
                {
                    VeteranAndTacticsPatches.WallsBlocked++;
                    return false;
                }
            }

            // 2. Ensure no friendly soldiers (or the player) are within the blast radius or throw trajectory corridor
            string myFaction = thrower.faction;
            IntPtr throwerPtr = thrower.Pointer;
            float aiSafeRadius = Mathf.Max(friendlySafeRadius, 11.5f);
            float aiSafeRadiusSq = aiSafeRadius * aiSafeRadius;
            float playerSafeRadius = Mathf.Max(friendlySafeRadius, 15.0f);
            float playerSafeRadiusSq = playerSafeRadius * playerSafeRadius;
            Vector3 throwDir = (targetPos - myPos) / Mathf.Max(dist, 0.1f);

            Soldier player = Soldier.CurrentControlledSoldierOrNull();
            if (player != null && player.IsAlive && player.faction == myFaction && player.transform != null)
            {
                Vector3 playerPos = player.transform.position;
                if ((playerPos - targetPos).sqrMagnitude <= playerSafeRadiusSq)
                {
                    VeteranAndTacticsPatches.FriendlyFirePrevented++;
                    return false;
                }

                float playerProj = Vector3.Dot(playerPos - myPos, throwDir);
                if (playerProj > -1.5f && playerProj < dist + 4.0f)
                {
                    Vector3 closestOnArc = myPos + throwDir * Mathf.Clamp(playerProj, 0f, dist);
                    if ((playerPos - closestOnArc).sqrMagnitude <= 5.5f * 5.5f)
                    {
                        VeteranAndTacticsPatches.FriendlyFirePrevented++;
                        return false;
                    }
                }
            }

            AiCore.EnsureWorldSnapshot();
            int sqCount = AiCore.WorldSquadCount;
            if (sqCount > 0)
            {
                var squads = AiCore.WorldSquads;
                var soldiers = AiCore.WorldSoldiers;
                for (int s = 0; s < sqCount; s++)
                {
                    ref AiCore.SquadSnapshot sqSnap = ref squads[s];
                    if (sqSnap.UnitCount == 0 || sqSnap.Faction != myFaction) continue;
                    if ((sqSnap.RefPos - targetPos).sqrMagnitude > 52f * 52f) continue;

                    int end = sqSnap.UnitStartIndex + sqSnap.UnitCount;
                    for (int i = sqSnap.UnitStartIndex; i < end; i++)
                    {
                        ref AiCore.SoldierSnapshot fSnap = ref soldiers[i];
                        if (fSnap.Ptr == throwerPtr)
                            continue;

                        Vector3 fPos = fSnap.Pos;
                        if ((fPos - targetPos).sqrMagnitude <= aiSafeRadiusSq)
                        {
                            VeteranAndTacticsPatches.FriendlyFirePrevented++;
                            return false;
                        }

                        float fProj = Vector3.Dot(fPos - myPos, throwDir);
                        if (fProj > 1.0f && fProj < dist + 2.0f)
                        {
                            Vector3 closestOnArc = myPos + throwDir * Mathf.Clamp(fProj, 0f, dist);
                            if ((fPos - closestOnArc).sqrMagnitude <= 3.5f * 3.5f)
                            {
                                VeteranAndTacticsPatches.FriendlyFirePrevented++;
                                return false;
                            }
                        }
                    }
                }
            }

            return true;
        }

        internal static bool TryFlushLastKnownEnemyWithGrenade(SoldierAI ai, Soldier soldier)
        {
            if (!VisionConfig.EnableLastKnownSuppression.Value || ai == null || soldier == null || soldier.Pointer == IntPtr.Zero)
                return false;

            if (ai.visibleTarget != null ||
                ai.IsMoving() ||
                soldier.IsOnVehicle() ||
                soldier.isInHandsAnimation ||
                soldier.isInBodyAnimation ||
                soldier.IsReloading ||
                soldier.inventory == null)
            {
                return false;
            }

            IntPtr ptr = soldier.Pointer;
            float now = Time.time;
            if (now <= soldier.throw_end_time + 6.0f)
                return false;

            if (_nextSuppressiveGrenadeTime.TryGetValue(ptr, out float nextGren) && now < nextGren)
                return false;

            if (!_lastKnownTargets.TryGetValue(ptr, out LastKnownTargetInfo lastInfo) || !lastInfo.WasInfantry)
                return false;

            float elapsed = now - lastInfo.LastSeenTime;
            if (elapsed < 0.35f || elapsed > 4.5f)
                return false;

            if (lastInfo.Target == null || lastInfo.Target.Pointer == IntPtr.Zero)
                return false;

            Soldier targetSoldier = lastInfo.Target.TryCast<Soldier>();
            if (targetSoldier == null || !targetSoldier.IsAlive)
                return false;

            Vector3 myPos = soldier.transform.position;
            Vector3 liveTargetPos = targetSoldier.GetCenterOfUnit();
            if ((liveTargetPos - lastInfo.LastSeenPos).sqrMagnitude > 7.0f * 7.0f)
                return false;

            float dist = Vector3.Distance(myPos, lastInfo.LastSeenPos);
            float liveDist = Vector3.Distance(myPos, liveTargetPos);
            if (dist < 10.0f || dist > 30.0f || liveDist < 10.0f || liveDist > 30.0f)
                return false;

            VirtualItem fragItem = soldier.inventory.FindItemOfType<VirtualGrenade>();
            if (fragItem == null || Mathf.Max(dist, liveDist) > soldier.GetGrenadeThrowRange(fragItem))
                return false;

            if (!IsSafeToThrowGrenadeAt(soldier, liveTargetPos, liveDist, 11.5f) ||
                !IsSafeToThrowGrenadeAt(soldier, lastInfo.LastSeenPos, dist, 11.5f))
                return false;

            if (!AiCore.TryConsumeBotRpcBudget(0.45f))
                return false;

            _nextSuppressiveGrenadeTime[ptr] = now + 14.0f;
            soldier.Throw(fragItem, lastInfo.Target);
            SuppressiveEventsCount++;
            VeteranAndTacticsPatches.TrySayTacticalVoice(soldier, soldier.joinedSquad, VoiceManager.VoiceClip.granade, 0.05f);
            if (VisionConfig.DebugLogging.Value)
            {
                AiCore.LogDebug("LastKnownGrenade", $"{soldier.faction} soldier threw frag grenade at last-known enemy cover ({dist:F0}m).");
            }
            return true;
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
            if (elapsed < 0.08f)
                return false;

            if (elapsed > 2.25f)
            {
                if (elapsed < 2.65f)
                {
                    ai.shotFiresEnd = 0f;
                    soldier.SetGunTriggerPressed(false);
                }
                return false;
            }

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
                ai.aimingEnemy = AiCore.HorizontalAngleToDirection(soldier, aimPos - soldier.transform.position) < 25f;
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
            // Probe at the soldier's ACTUAL stance eye height so crouched trench/parapet defenders (0.80m)
            // don't falsely treat a 1.15m dirt trench wall as "open" at 1.28m and stare straight into dirt!
            float eyeH = soldier.m_pose == SoldierPose.Prone ? 0.34f : (soldier.m_pose == SoldierPose.Crouch ? 0.80f : 1.28f);
            Vector3 upperOrigin = myPos + Vector3.up * eyeH;
            Vector3 standOrigin = myPos + Vector3.up * 1.28f;
            Vector3 sillOrigin = myPos + Vector3.up * 0.52f;

            bool fannedBlocked = AiCore.RaycastEnvironment(upperOrigin, fannedDir, 4.5f, root, out _);
            bool fannedHasWindowSill = !fannedBlocked &&
                                       soldier.m_pose != SoldierPose.Prone &&
                                       !AiCore.RaycastEnvironment(standOrigin, fannedDir, 7.5f, root, out _) &&
                                       AiCore.RaycastEnvironment(sillOrigin, fannedDir, 2.35f, root, out _);

            Vector3 finalDir;
            if (fannedHasWindowSill)
            {
                finalDir = CenterGazeInWindowAperture(standOrigin, fannedDir, root, t, maxFanDeg);
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
            bool inNarrowTrenchOrHall = false;
            if (!fannedBlocked)
            {
                Vector3 left90 = new Vector3(-fannedDir.z, 0f, fannedDir.x);
                Vector3 right90 = new Vector3(fannedDir.z, 0f, -fannedDir.x);
                bool leftCloseWall = AiCore.RaycastEnvironment(upperOrigin, left90, 2.35f, root, out _);
                bool rightCloseWall = AiCore.RaycastEnvironment(upperOrigin, right90, 2.35f, root, out _);
                if (leftCloseWall && rightCloseWall)
                {
                    inNarrowTrenchOrHall = true;
                }
                else if (soldier.m_pose != SoldierPose.Prone &&
                         ((AiCore.RaycastEnvironment(sillOrigin, left90, 2.35f, root, out _) && !AiCore.RaycastEnvironment(standOrigin, left90, 7.5f, root, out _)) ||
                          (AiCore.RaycastEnvironment(sillOrigin, right90, 2.35f, root, out _) && !AiCore.RaycastEnvironment(standOrigin, right90, 7.5f, root, out _))))
                {
                    nearbyWindowDetected = true;
                }
            }

            if (!fannedBlocked && !nearbyWindowDetected && !inNarrowTrenchOrHall)
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
            Vector3 secondApproachDir = Vector3.zero;
            float secondApproachScore = -999f;

            for (int i = 0; i < _watchSweepOffsets.Length; i++)
            {
                float angleOffset = _watchSweepOffsets[i];
                Vector3 cand = i == 0 ? baseDir : (Quaternion.Euler(0f, angleOffset, 0f) * baseDir);

                float clearDist = 18.0f;
                if (AiCore.RaycastEnvironment(upperOrigin, cand, 18.0f, root, out RaycastHit upperHit))
                {
                    clearDist = upperHit.distance;
                }

                if (clearDist < 2.1f)
                    continue;

                bool isWindowAperture = soldier.m_pose != SoldierPose.Prone &&
                                        clearDist >= 7.0f &&
                                        AiCore.RaycastEnvironment(sillOrigin, cand, 2.35f, root, out RaycastHit sillHit) &&
                                        sillHit.distance < clearDist - 3.0f;

                float alignDot = Vector3.Dot(cand, baseDir);
                float score = clearDist + (alignDot * 6.5f);
                if (isWindowAperture)
                {
                    score += 18.0f;
                }

                if (score > bestScore)
                {
                    if (bestScore > -900f && Vector3.Dot(cand, bestDir) < -0.25f)
                    {
                        secondApproachDir = bestDir;
                        secondApproachScore = bestScore;
                    }
                    bestScore = score;
                    bestDir = cand;
                    bestIsWindow = isWindowAperture;
                }
                else if (clearDist >= 5.5f && Vector3.Dot(cand, bestDir) < -0.25f && score > secondApproachScore)
                {
                    secondApproachScore = score;
                    secondApproachDir = cand;
                }
            }

            // Trench & Corridor Vigilance: when walled in on both sides (trench/hallway) with two open approaches,
            // briefly check the secondary trench bend/rear approach ~20% of the time so flankers can't walk up unopposed!
            bool checkSecondaryTrenchApproach =
                (fannedBlocked || inNarrowTrenchOrHall) &&
                !bestIsWindow &&
                secondApproachDir.sqrMagnitude > 0.01f &&
                (((int)(Time.time * 0.24f) + (h & 7)) % 5 == 0);

            Vector3 chosenWatch = checkSecondaryTrenchApproach ? secondApproachDir : bestDir;
            finalDir = (!checkSecondaryTrenchApproach && (bestIsWindow || fannedBlocked))
                ? CenterGazeInWindowAperture(upperOrigin, chosenWatch, root, t, maxFanDeg)
                : chosenWatch;

            float cacheDur = (fannedBlocked || inNarrowTrenchOrHall) ? 3.8f : 8.0f;
            _watchWindowCache[ptr] = new WatchWindowCache
            {
                Pos = myPos,
                BaseAngleDeg = baseAngleDeg,
                ResultDir = finalDir,
                ExpireTime = Time.time + cacheDur
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
            AiCore.SoldierFastHandles sh = AiCore.GetOrCreateSoldierHandles(soldier);
            if (sh == null || sh.Tr == null)
                return false;

            IntPtr ptr = sh.Ptr;
            float now = Time.time;
            bool hadPrevCache = _threatSectorCache.TryGetValue(ptr, out ThreatSectorCache cached);
            if (hadPrevCache && now < cached.ExpireTime)
            {
                watchDir = cached.WatchDir;
                hasActiveContact = cached.HasActiveContact;
                return watchDir.sqrMagnitude > 0.01f;
            }

            Vector3 myPos = sh.Tr.position;
            string myFaction = AiCore.GetCachedFaction(sh, now);
            Squad mySquad = soldier.joinedSquad;
            SquadTask myTask = mySquad != null ? mySquad.currentTask : null;

            int contactCount = 0;
            Vector3 c0 = Vector3.zero, c1 = Vector3.zero, c2 = Vector3.zero, c3 = Vector3.zero;
            float d0 = float.MaxValue, d1 = float.MaxValue, d2 = float.MaxValue, d3 = float.MaxValue;

            if (VisionConfig.EnableLocalContactCallouts.Value &&
                _localCalloutCache.TryGetValue(ptr, out LocalCalloutRecord callout) &&
                now < callout.ExpireTime)
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
                    if (ReferenceEquals(mate, null) || mate == null || !mate.IsAlive) continue;
                    AiCore.SoldierFastHandles mateH = AiCore.GetOrCreateSoldierHandles(mate);
                    if (mateH == null || ReferenceEquals(mateH.Ai, null) || mateH.Ai == null) continue;
                    Spottable mateTarget = mateH.Ai.visibleTarget;
                    if (!AiCore.TryGetSpottableCenter(mateTarget, out Vector3 mateEnemyPos) || mateTarget.IsAirVehicle()) continue;

                    Vector3 toEnemy = mateEnemyPos - myPos;
                    toEnemy.y = 0f;
                    float dSq = toEnemy.sqrMagnitude;
                    if (dSq > 0.25f)
                    {
                        InsertClosestContact(toEnemy, dSq * 0.55f, ref contactCount, ref c0, ref d0, ref c1, ref d1, ref c2, ref d2, ref c3, ref d3);
                    }
                }
            }

            AiCore.EnsureWorldSnapshot();
            int sqCount = AiCore.WorldSquadCount;
            if (sqCount > 0)
            {
                IntPtr mySqPtr = mySquad != null ? mySquad.Pointer : IntPtr.Zero;
                var squads = AiCore.WorldSquads;
                var soldiers = AiCore.WorldSoldiers;

                for (int s = 0; s < sqCount; s++)
                {
                    ref AiCore.SquadSnapshot sqSnap = ref squads[s];
                    if (sqSnap.Ptr == mySqPtr || sqSnap.UnitCount == 0 || sqSnap.RefOnPlane || AiCore.AreSameSideFactions(myFaction, sqSnap.Faction))
                        continue;

                    Vector3 toSquad = sqSnap.RefPos - myPos;
                    toSquad.y = 0f;
                    float dSq = toSquad.sqrMagnitude;
                    // Allow stationary defenders to hear distant gunfire/battles up to 320m away so idle troops face the audible battle!
                    if (dSq > 320f * 320f || dSq < 0.25f)
                        continue;

                    bool isKnownContact = dSq <= 195f * 195f && sqSnap.IsSpottedOrAware;
                    Vector3 contactVector = toSquad;

                    if (!isKnownContact)
                    {
                        int end = sqSnap.UnitStartIndex + sqSnap.UnitCount;
                        for (int i = sqSnap.UnitStartIndex; i < end; i++)
                        {
                            ref AiCore.SoldierSnapshot uSnap = ref soldiers[i];
                            if ((dSq <= 180f * 180f && uSnap.IsSpotted) ||
                                (uSnap.Ref != null && VisionPatches.HasFiredRecently(uSnap.Ref, 6.0f)))
                            {
                                isKnownContact = true;
                                contactVector = uSnap.Pos - myPos;
                                contactVector.y = 0f;
                                dSq = contactVector.sqrMagnitude;
                                if (dSq > 165f * 165f)
                                    dSq *= 1.35f; // Prioritize local (< 165m) contacts over distant audible gunfire
                                break;
                            }
                        }
                    }

                    if (!isKnownContact && myTask != null && dSq <= 55f * 55f)
                    {
                        if (myTask.IsInsideTaskArea(sqSnap.RefPos, 1.05f))
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
            if (player != null && player.IsAlive)
            {
                AiCore.SoldierFastHandles pH = AiCore.GetOrCreateSoldierHandles(player);
                if (pH != null && pH.Tr != null && !AiCore.AreSameSideFactions(myFaction, AiCore.GetCachedFaction(pH, now)))
                {
                    Vector3 toPlayer = pH.Tr.position - myPos;
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
                Vector3 fallbackBase = sh.Tr.forward;
                bool foundObjectiveDir = false;

                // Prefer facing the squad's assigned objective when outside its perimeter so idle troops
                // look toward the contested battle area rather than an off-axis rear map edge!
                if (myTask != null && myTask.Pointer != IntPtr.Zero && !myTask.IsInsideTaskArea(myPos, 0.90f))
                {
                    Vector3 toTask = myTask.GetTaskDestination() - myPos;
                    toTask.y = 0f;
                    if (toTask.sqrMagnitude > 16.0f * 16.0f)
                    {
                        fallbackBase = toTask;
                        foundObjectiveDir = true;
                    }
                }

                if (!foundObjectiveDir && AiCore.TryGetEnemySpawnDirection(myFaction, myPos, out Vector3 enemySpawnDir))
                {
                    fallbackBase = enemySpawnDir;
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

            float sectorJitter = (AiCore.GetDeterministicHash(ptr, 53) % 160) * 0.01f;
            float cacheDuration = hasActiveContact ? 2.80f : 4.50f;
            _threatSectorCache[ptr] = new ThreatSectorCache
            {
                ExpireTime = Time.time + cacheDuration + sectorJitter,
                WatchDir = watchDir,
                HasActiveContact = hasActiveContact
            };

            return watchDir.sqrMagnitude > 0.01f;
        }

        private static bool TryGetActiveCornerPieDirection(
            SoldierAI ai,
            AiCore.SoldierFastHandles h,
            out Vector3 pieLookDir,
            out bool raiseRifle)
        {
            pieLookDir = Vector3.zero;
            raiseRifle = false;
            if (h == null || h.Tr == null || h.Ref == null)
                return false;

            Soldier soldier = h.Ref;
            IntPtr ptr = h.Ptr;
            float now = Time.time;

            bool hadCache = _cornerPieCache.TryGetValue(ptr, out CornerPieState cached);
            if (hadCache && now < cached.NextRefreshTime)
            {
                if (!cached.IsPieing || !cached.RaiseRifle || soldier.isTowardWallTps)
                    return false;

                pieLookDir = cached.PieLookDir;
                raiseRifle = cached.RaiseRifle;
                return pieLookDir.sqrMagnitude > 0.01f;
            }

            var path = ai.path;
            if (path == null || path.NodesCount < 2 || soldier.isTowardWallTps)
            {
                _cornerPieCache[ptr] = new CornerPieState { NextRefreshTime = now + 0.32f, IsPieing = false };
                return false;
            }

            Vector3 myPos = h.Tr.position;
            Vector3 node0 = path.CurrentNode.point;
            Vector3 toCorner = node0 - myPos;
            toCorner.y = 0f;
            float distToCornerSq = toCorner.sqrMagnitude;
            if (distToCornerSq < 0.45f * 0.45f || distToCornerSq > 2.55f * 2.55f)
            {
                _cornerPieCache[ptr] = new CornerPieState { NextRefreshTime = now + 0.26f, IsPieing = false };
                return false;
            }

            bool alerted = now < soldier.alertEndTime || soldier.GetSuppressionValue() > 0;
            bool indoor = AiCore.HasLowCeilingOverhead(myPos, 4.2f);
            bool hasThreatContact = HasActiveThreatSectorContact(soldier);
            raiseRifle = indoor || (alerted && hasThreatContact);
            if (!raiseRifle)
            {
                // Never override MovementRoutine with angled MoveFPSOptimized when the rifle is lowered;
                // Easy Red 2's un-aimed TPS walk tree has no lateral strafe blend and causes sideways foot-sliding.
                _cornerPieCache[ptr] = new CornerPieState { NextRefreshTime = now + 0.34f, IsPieing = false };
                return false;
            }

            float distToCorner = Mathf.Sqrt(distToCornerSq);
            Vector3 seg0 = toCorner / distToCorner;
            Vector3 node1;

            // Reuse cached node1 when approaching the exact same corner node0 so path.A(1) (Stack.GetEnumerator)
            // and the blind-corner raycast only run ONCE per corner instead of every 0.16s!
            if (hadCache && cached.HasVerifiedCorner && (node0 - cached.CachedNode0).sqrMagnitude <= 0.09f)
            {
                if (!cached.IsPieing)
                {
                    cached.NextRefreshTime = now + 0.30f;
                    _cornerPieCache[ptr] = cached;
                    return false;
                }
                node1 = cached.CachedNode1;
            }
            else
            {
                node1 = path.A(1);
                Vector3 checkSeg1 = node1 - node0;
                checkSeg1.y = 0f;
                if (checkSeg1.sqrMagnitude < 0.64f)
                {
                    _cornerPieCache[ptr] = new CornerPieState { NextRefreshTime = now + 0.30f, IsPieing = false, HasVerifiedCorner = true, CachedNode0 = node0, CachedNode1 = node1 };
                    return false;
                }

                float bendAngle = Vector3.Angle(seg0, checkSeg1.normalized);
                if (bendAngle < 28f || bendAngle > 158f)
                {
                    _cornerPieCache[ptr] = new CornerPieState { NextRefreshTime = now + 0.30f, IsPieing = false, HasVerifiedCorner = true, CachedNode0 = node0, CachedNode1 = node1 };
                    return false;
                }

                // Confirm an actual solid wall/doorframe/trench corner blocks direct sight to node1 ("blind corner")!
                Vector3 eyePos = myPos + Vector3.up * 0.95f;
                Vector3 toBeyond = (node1 + Vector3.up * 0.95f) - eyePos;
                float beyondDist = toBeyond.magnitude;
                if (beyondDist < 1.2f ||
                    !AiCore.IsDirectionBlockedByEnvironment(eyePos, toBeyond / beyondDist, Mathf.Min(beyondDist - 0.3f, 6.2f), h.RootTr))
                {
                    _cornerPieCache[ptr] = new CornerPieState { NextRefreshTime = now + 0.30f, IsPieing = false, HasVerifiedCorner = true, CachedNode0 = node0, CachedNode1 = node1 };
                    return false;
                }
            }

            Vector3 seg1 = node1 - node0;
            seg1.y = 0f;
            if (seg1.sqrMagnitude < 0.04f)
                return false;
            seg1.Normalize();

            // Smoothly "slice the pie" within <= 38 deg of travel vector seg0 so the aimed 4-way diagonal
            // walk/jog blend tree stays crisp with zero sideways foot-skating!
            float pieT = Mathf.Clamp01(1.0f - (distToCorner - 0.45f) / 2.10f);
            Vector3 desiredPie = Vector3.Slerp(seg0, seg1, 0.28f + 0.44f * pieT).normalized;
            pieLookDir = Vector3.RotateTowards(seg0, desiredPie, 38f * Mathf.Deg2Rad, 0f).normalized;

            _cornerPieCache[ptr] = new CornerPieState
            {
                NextRefreshTime = now + 0.14f,
                IsPieing = true,
                RaiseRifle = true,
                HasVerifiedCorner = true,
                PieLookDir = pieLookDir,
                CachedNode0 = node0,
                CachedNode1 = node1
            };

            if (!_nextCornerPieCountTime.TryGetValue(ptr, out float nextCount) || now >= nextCount)
            {
                _nextCornerPieCountTime[ptr] = now + 4.5f;
                CornerPiesExecutedCount++;
            }
            return true;
        }

        // =====================================================================
        // Corner-Pieing & Stationary Threat-Sector Gaze (SoldierAI.MovementRoutine)
        // =====================================================================
        [HarmonyPatch(typeof(SoldierAI), nameof(SoldierAI.MovementRoutine))]
        public static class SoldierAI_MovementRoutine_Patch
        {
            public struct MovementRoutineState
            {
                public long ProfStart;
                public AiCore.SoldierFastHandles Handles;
                public AiCore.CachedDestKind DestKind;
                public bool PieHandled;
            }

            public static bool Prefix(SoldierAI __instance, float __0, out MovementRoutineState __state)
            {
                __state = new MovementRoutineState { ProfStart = HitchProfiler.Begin() };
                try
                {
                    if (!VisionConfig.EnableMod.Value || ReferenceEquals(__instance, null))
                        return true;

                    AiCore.SoldierFastHandles h = AiCore.GetOrCreateSoldierHandlesByAi(__instance);
                    if (!AiCore.IsLocallyOwnedBot(h))
                        return true;

                    __state.Handles = h;
                    Soldier soldier = h.Ref;
                    AiCore.CachedDestKind destKind = AiCore.ResolveTargetDestination(h);
                    __state.DestKind = destKind;

                    // Tank Crew Repair Priority: while assigned to a VehicleRepairTask, keep allowFireAtEnemy false
                    // (unless an enemy is point-blank <= 6.0m) so the crewman sprints to the sheltered hull side and
                    // repairs immediately without stopping to duel with a pistol/SMG!
                    if (VisionConfig.EnableSmartTankCrewRepair.Value &&
                        destKind == AiCore.CachedDestKind.VehicleRepair)
                    {
                        bool pointBlankSelfDefense = __instance.HasEnemyAndCanFireAtHim &&
                                                     __instance.distFromTarget > 0.1f &&
                                                     __instance.distFromTarget <= 6.0f;
                        if (!pointBlankSelfDefense)
                        {
                            __instance.allowFireAtEnemy = false;
                            __instance.shotFiresEnd = 0f;
                            soldier.SetGunTriggerPressed(false);
                        }
                    }

                    if (soldier.IsCarried() ||
                        soldier.IsOnVehicle() ||
                        __instance.HeldMortar != null ||
                        soldier.HasSurrended())
                    {
                        return true;
                    }

                    float now = Time.time;
                    Vector3 myPos = h.Tr != null ? h.Tr.position : Vector3.zero;

                    if (!__instance.IsMoving())
                    {
                        // Seamless Async Path-Refresh Continuation:
                        // Every 3-6s (pathRefreshTime), vanilla SequentialUpdate (0x18055EFA9) calls Stop(true) + async UpdatePath(),
                        // briefly dropping IsMoving() to false for 1-3 frames while HasPathRequest is true. Keep sprinting along
                        // __instance.dir instead of decelerating into StopMove(Crouch) or snapping a 2-frame mid-sprint shot!
                        if (h.Tr != null && IsAsyncPathRefreshPending(__instance, soldier, myPos, now))
                        {
                            __state.PieHandled = true;
                            soldier.SetGunTriggerPressed(false);
                            __instance.actualPose = soldier.m_pose;
                            if (!soldier.isInBodyAnimation && __instance.dir.sqrMagnitude > 0.04f)
                            {
                                bool refreshSprint = soldier.m_pose != SoldierPose.Prone && !soldier.IsStealth;
                                __instance.MoveOptimized(refreshSprint, __0);
                                return false;
                            }
                            return true;
                        }

                        // Native MovementRoutine (0x18055D4BD) skips RotateAndFireToward even when stationary if moveLookingTarget is false!
                        if (__instance.HasEnemyAndCanFireAtHim && !soldier.HasMovementsSlowed())
                        {
                            __instance.moveLookingTarget = true;
                        }

                        // Frame-1 Kneel on Committed Firing Halt (don't wait ~0.8s for next SequentialUpdate tick)
                        if (IsInCommittedFiringHalt(h.Ptr, now) &&
                            soldier.m_pose == SoldierPose.Idle &&
                            !soldier.isInBodyAnimation &&
                            !soldier.UsingBipod() &&
                            !soldier.IsBipodTransitioning())
                        {
                            SetBotPose(__instance, soldier, SoldierPose.Crouch);
                        }

                        return true;
                    }

                    // --- Moving Branch (__instance.IsMoving() == true) ---
                    if (soldier.isInBodyAnimation ||
                        soldier.UsingBipod() ||
                        soldier.IsBipodTransitioning())
                    {
                        return true;
                    }

                    Vector3 endpoint = GetCachedMoveEndpoint(h, __instance, soldier, out int nodesCount);
                    float remDistSq = h.Tr != null ? (endpoint - myPos).sqrMagnitude : 100f;

                    // Prevent Trench-Wall Walk-Through:
                    // Native Soldier.RefreshControllerEnabled (0x18075E080) disables m_controller on grounded AI bots
                    // whenever moveDirectlyToward_end <= Time.time, causing Soldier.ApplyMove (0x18073DD7C) to execute
                    // raw Transform.Translate with zero horizontal wall collision while CheckGroundStatus snaps Y onto
                    // steep trench walls like ramps! Calling ForceControllerEnabled() sets Soldier's [this+0x158] timer
                    // so CharacterController collision stays active on final segments and near steep trench/wall faces.
                    if (h.Tr != null)
                    {
                        _trenchCollisionGuardCache.TryGetValue(h.Ptr, out TrenchCollisionGuardState tcGuard);
                        if (now >= tcGuard.NextProbeTime || (myPos - tcGuard.LastProbePos).sqrMagnitude > 0.56f)
                        {
                            tcGuard.NextProbeTime = now + 0.24f;
                            tcGuard.LastProbePos = myPos;
                            Vector3 moveDir = __instance.dir;
                            moveDir.y = 0f;
                            if (moveDir.sqrMagnitude > 0.01f)
                            {
                                moveDir.Normalize();
                                Vector3 waistOrigin = new Vector3(myPos.x, myPos.y + 0.52f, myPos.z);
                                if (Physics.Raycast(
                                        waistOrigin,
                                        moveDir,
                                        out RaycastHit steepHit,
                                        1.75f,
                                        AiCore.GetSolidEnvironmentMask(),
                                        QueryTriggerInteraction.Ignore) &&
                                    Mathf.Abs(steepHit.normal.y) < 0.72f)
                                {
                                    tcGuard.ForceUntilTime = now + 0.90f;
                                }
                            }
                            _trenchCollisionGuardCache[h.Ptr] = tcGuard;
                        }

                        if (nodesCount < 2 || now < tcGuard.ForceUntilTime)
                        {
                            soldier.ForceControllerEnabled();
                        }
                    }

                    // Frame-1 Rise to Upright Sprint when starting or resuming a bound (eliminates 0.8s crouch-waddle lag)
                    if (remDistSq > 5.5f * 5.5f &&
                        destKind != AiCore.CachedDestKind.VehicleRepair &&
                        !__instance.knifeCharge &&
                        (!__instance.HasEnemyAndCanFireAtHim || __instance.distFromTarget > 6.2f) &&
                        (soldier.m_pose != SoldierPose.Idle || __instance.actualPose != SoldierPose.Idle) &&
                        !soldier.isInHandsAnimation &&
                        !soldier.IsReloading)
                    {
                        bool stealthCrouch = false;
                        if (soldier.IsStealth)
                        {
                            Squad sq = AiCore.GetCachedSquad(h, now);
                            stealthCrouch = !ReferenceEquals(sq, null) && sq.Leader != null && sq.Leader.m_pose > SoldierPose.Idle;
                        }
                        if (GetDesiredMovingPose(__instance, soldier, remDistSq, soldier.GetSuppressionValue(), stealthCrouch) == SoldierPose.Idle)
                        {
                            SetBotPose(__instance, soldier, SoldierPose.Idle, true);
                        }
                    }

                    if (__instance.moveDirectlyToward_end > now || __instance.knifeCharge)
                    {
                        return true;
                    }

                    // Only permit firing on the move in genuine point-blank CQB (<= 8.5m) or final charge assault (<= 16m)
                    // when the target lies within a natural forward 52-degree arc of travel (never sideways FPS strafing!).
                    float dist = __instance.distFromTarget;
                    if (__instance.HasEnemyAndCanFireAtHim &&
                        dist > 0.1f &&
                        (dist <= 8.5f || (soldier.IsCharging() && dist <= 16.0f)) &&
                        h.Tr != null &&
                        AiCore.TryGetSpottableCenter(__instance.visibleTarget, out Vector3 cqbPos))
                    {
                        Vector3 fwdRef = __instance.dir;
                        fwdRef.y = 0f;
                        if (fwdRef.sqrMagnitude < 0.01f)
                        {
                            fwdRef = h.Tr.forward;
                            fwdRef.y = 0f;
                        }

                        Vector3 toCqb = cqbPos - myPos;
                        toCqb.y = 0f;
                        if (fwdRef.sqrMagnitude > 0.01f && toCqb.sqrMagnitude > 0.01f && Vector3.Angle(fwdRef, toCqb) <= 52f)
                        {
                            __instance.moveLookingTarget = true;
                            return true;
                        }
                    }

                    if (VisionConfig.EnableThreatSectorConcentration.Value &&
                        !__instance.HasEnemyAndCanFireAtHim &&
                        !soldier.IsCharging() &&
                        !soldier.IsCarryingBody() &&
                        !soldier.isTowardWallTps &&
                        soldier.m_pose != SoldierPose.Prone &&
                        TryGetActiveCornerPieDirection(__instance, h, out Vector3 pieDir, out bool raiseRifle) &&
                        raiseRifle)
                    {
                        __state.PieHandled = true;
                        soldier.SetGunTriggerPressed(false);
                        __instance.MoveFPSOptimized(0.88f, __0);
                        soldier.RotateToward(pieDir, __0);
                        return false;
                    }

                    // Committed Forward Bound: bypass vanilla's 2.0s ShouldLookTargetWhileMoving FPS strafe-shoot parity,
                    // vanilla's dist <= 60m sprint=false casual firefight stroll, and vanilla's NodesCount == 1 long slow-walk!
                    if (__instance.HasEnemyAndCanFireAtHim ||
                        (nodesCount >= 0 && nodesCount < 2 && remDistSq > 1.65f * 1.65f))
                    {
                        __state.PieHandled = true;
                        soldier.SetGunTriggerPressed(false);
                        bool shouldSprint = soldier.m_pose != SoldierPose.Prone &&
                                            !soldier.IsStealth &&
                                            remDistSq > 1.65f * 1.65f;
                        __instance.MoveOptimized(shouldSprint, __0);
                        return false;
                    }
                }
                catch { }
                return true;
            }

            public static void Postfix(SoldierAI __instance, float __0, MovementRoutineState __state)
            {
                try
                {
                    AiCore.SoldierFastHandles h = __state.Handles;
                    if (h == null || __state.PieHandled)
                        return;

                    Soldier soldier = h.Ref;
                    IntPtr ptr = h.Ptr;

                    if (__state.DestKind == AiCore.CachedDestKind.VehicleRepair ||
                        __state.DestKind == AiCore.CachedDestKind.MedicHeal ||
                        __instance.IsMoving() ||
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

                    float now = Time.time;
                    Vector3 watchDir;
                    bool hasWatchDir;

                    if (_stationaryWatchCache.TryGetValue(ptr, out StationaryWatchState wState) && now < wState.NextEvalTime)
                    {
                        hasWatchDir = wState.HasWatchDir;
                        watchDir = wState.WatchDir;
                    }
                    else
                    {
                        hasWatchDir = TryGetKnownThreatSectorDirection(soldier, out watchDir, out _) && watchDir.sqrMagnitude > 0.01f;
                        if (hasWatchDir &&
                            __state.DestKind == AiCore.CachedDestKind.CombatCover &&
                            h.CachedCover != null &&
                            soldier.IsOnCover())
                        {
                            CombatCover activeCover = h.CachedCover;
                            if (activeCover.localLookAngleWideness < 145f)
                            {
                                Vector3 coverFwd = activeCover.GetLookRotation() * Vector3.forward;
                                coverFwd.y = 0f;
                                if (coverFwd.sqrMagnitude > 0.01f)
                                {
                                    coverFwd.Normalize();
                                    IntPtr covPtr = activeCover.Pointer;
                                    SoldierPose pose = soldier.m_pose;
                                    bool coverFwdBlockedAtEye;

                                    if (_coverEyeBlockCache.TryGetValue(ptr, out CoverEyeBlockState eyeState) &&
                                        eyeState.CoverPtr == covPtr &&
                                        eyeState.Pose == pose &&
                                        now < eyeState.ExpireTime)
                                    {
                                        coverFwdBlockedAtEye = eyeState.Blocked;
                                    }
                                    else
                                    {
                                        float eyeH = pose == SoldierPose.Prone ? 0.34f : (pose == SoldierPose.Crouch ? 0.80f : 1.28f);
                                        coverFwdBlockedAtEye = h.Tr != null && AiCore.IsDirectionBlockedByEnvironment(
                                            h.Tr.position + Vector3.up * eyeH,
                                            coverFwd,
                                            1.85f,
                                            h.RootTr);
                                        _coverEyeBlockCache[ptr] = new CoverEyeBlockState
                                        {
                                            CoverPtr = covPtr,
                                            Pose = pose,
                                            ExpireTime = now + 2.5f,
                                            Blocked = coverFwdBlockedAtEye
                                        };
                                    }

                                    if (!coverFwdBlockedAtEye)
                                    {
                                        float maxHalfArc = Mathf.Clamp(activeCover.localLookAngleWideness * 0.5f, 22f, 52f);
                                        if (Vector3.Angle(coverFwd, watchDir) > maxHalfArc)
                                        {
                                            watchDir = Vector3.RotateTowards(coverFwd, watchDir, maxHalfArc * Mathf.Deg2Rad, 0f).normalized;
                                        }
                                    }
                                }
                            }
                        }

                        float jitter = (AiCore.GetDeterministicHash(ptr, 89) & 15) * 0.005f;
                        _stationaryWatchCache[ptr] = new StationaryWatchState
                        {
                            NextEvalTime = now + 0.22f + jitter,
                            HasWatchDir = hasWatchDir,
                            WatchDir = watchDir
                        };
                    }

                    if (hasWatchDir)
                    {
                        float angle = AiCore.HorizontalAngleToDirection(soldier, watchDir);
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
                finally
                {
                    HitchProfiler.End(HitchBucket.MovementRoutine, __state.ProfStart);
                }
            }
        }
    }
}
