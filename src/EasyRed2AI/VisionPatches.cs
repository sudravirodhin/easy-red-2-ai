using System;
using System.Collections.Generic;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace EasyRed2HumanAI
{
    public static class VisionPatches
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

        // On-screen status toast
        private static string _toastMessage = "";
        private static float _toastExpireTime;

        public static void ShowToast(string msg, float duration = 4.0f)
        {
            _toastMessage = msg;
            _toastExpireTime = Time.unscaledTime + duration;
            Plugin.Log.LogInfo(msg);
        }

        public static float GetObserverOpticMultiplier()
        {
            return _currentObserverOpticMultiplier > 0.01f ? _currentObserverOpticMultiplier : 1.0f;
        }

        public static void RecordSoldierFired(Soldier s)
        {
            if (s == null || s.Pointer == IntPtr.Zero) return;
            _lastFiredTime[s.Pointer] = Time.time;
        }

        public static float GetFiringVisibilityBonus(Soldier s)
        {
            if (s == null || s.Pointer == IntPtr.Zero) return 0f;
            float duration = VisionConfig.FiringRevealDuration.Value;
            if (duration <= 0f) return 0f;

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
                        return true; // Run vanilla

                    // 1. Compute clothing camouflage multiplier (0.6x to 1.0x, matching vanilla formula)
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

                    // 2. Determine base sight distance from current stance (m_pose)
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

                        // If the enemy is aware of our squad's general area, apply a modest alertness multiplier
                        // instead of vanilla's jump to a flat 300m for all stances
                        if (__0)
                        {
                            baseDist *= VisionConfig.CombatAwareStanceMultiplier.Value;
                        }
                    }
                    else
                    {
                        // Fallback if user disabled PreserveStanceStealthInCombat
                        baseDist = 300.0f;
                    }

                    // 3. Add individual firing reveal bonus if THIS specific soldier fired recently
                    baseDist += GetFiringVisibilityBonus(__instance);

                    // 4. Apply clothing camo reduction and observer optic zoom (if observer has a scope/optic)
                    float finalDist = baseDist * camoMultiplier * GetObserverOpticMultiplier();
                    __result = finalDist;
                    return false; // Skip vanilla
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

                    // Calculate angle between observer's look/fire direction and the target
                    Vector3 lookDir = observer.IsAiming ? observer.GetFireDir() : observer.transform.forward;
                    float angle = Vector3.Angle(toTarget, lookDir);

                    // Check if observer has a magnified scope
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

                    // Enforce Human FOV Cones (even when alerted or suppressed)
                    if (VisionConfig.EnforceHumanFOVWhenAlerted.Value && dist > VisionConfig.CloseOmnidirectionalRange.Value)
                    {
                        float allowedHalfFOV = dist <= VisionConfig.MidRangeMaxDistance.Value
                            ? VisionConfig.MidRangeHalfFOV.Value
                            : VisionConfig.LongRangeHalfFOV.Value;

                        if (angle > allowedHalfFOV)
                        {
                            BlockedByFOV++;
                            __result = false;
                            return false;
                        }
                    }

                    // Apply scope magnification only if target is within the narrower optic cone
                    if (opticZoom > 1.0f && angle <= VisionConfig.OpticLongRangeHalfFOV.Value)
                    {
                        _currentObserverOpticMultiplier = opticZoom;
                        BoostedByScope++;
                    }

                    // If target is a land vehicle/tank, respect VehicleCamoDistance scaled by optic zoom
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

                    // Against infantry on foot, enforce realistic turret FOV cones
                    if (isInfantryTarget && VisionConfig.EnforceHumanFOVWhenAlerted.Value)
                    {
                        if (dist > VisionConfig.CloseOmnidirectionalRange.Value)
                        {
                            float allowedHalfFOV = dist <= VisionConfig.MidRangeMaxDistance.Value
                                ? VisionConfig.MidRangeHalfFOV.Value
                                : VisionConfig.LongRangeHalfFOV.Value;

                            if (angle > allowedHalfFOV)
                            {
                                BlockedByFOV++;
                                __result = false;
                                return false;
                            }
                        }
                        else if (turretAimFov < 42f && !turret.isAA && angle > 95f)
                        {
                            // Buttoned-up magnified tank turret has a blind spot directly behind the turret at close range
                            BlockedByFOV++;
                            __result = false;
                            return false;
                        }
                    }

                    // Scale sight range by turret optics when looking within the optic's forward cone
                    if (VisionConfig.EnableScopeVisionScaling.Value && turretAimFov > 1f && turretAimFov < 45f)
                    {
                        if (angle <= VisionConfig.OpticLongRangeHalfFOV.Value)
                        {
                            _currentObserverOpticMultiplier = Mathf.Clamp(50f / turretAimFov, 1.0f, VisionConfig.MaxOpticZoomMultiplier.Value);
                            BoostedByScope++;
                        }
                    }

                    // If target is a land vehicle/tank, respect VehicleCamoDistance scaled by turret optic zoom
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
        // 5. Aircraft Ground Spotting (AIPlane.GetBestAircraftVisibleEnemy)
        // =====================================================================
        [HarmonyPatch(typeof(AIPlane), nameof(AIPlane.GetBestAircraftVisibleEnemy))]
        public static class AIPlane_GetBestAircraftVisibleEnemy_Patch
        {
            public static void Postfix(AIPlane __instance, ref Spottable __result)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value || !VisionConfig.LimitAircraftInfantrySpotting.Value)
                        return;
                    if (__instance == null || __result == null)
                        return;

                    // Always allow planes to spot enemy aircraft and enemy vehicles/tanks
                    if (__result.IsAirVehicle() || __result.IsWheeledVehicleOrTank())
                        return;

                    Soldier targetSoldier = __result.TryCast<Soldier>();
                    if (targetSoldier == null)
                        return;

                    // If the soldier's squad is Marked (spotted by ground troops/recon), allow air attack
                    Squad sq = targetSoldier.joinedSquad;
                    if (sq != null && (sq.isMarked || sq.isHalfMarked || sq.isMarkedPinPoint))
                        return;

                    // Otherwise, prevent aircraft from X-ray spotting un-marked crouching/prone infantry
                    // or un-marked infantry beyond AircraftUnmarkedInfantryMaxDist
                    float dist = Vector3.Distance(__instance.transform.position, targetSoldier.GetPosition());
                    bool recentlyFired = GetFiringVisibilityBonus(targetSoldier) > 10f;

                    if (dist > VisionConfig.AircraftUnmarkedInfantryMaxDist.Value ||
                        (targetSoldier.m_pose != SoldierPose.Idle && !recentlyFired))
                    {
                        BlockedPlaneSpot++;
                        __result = null;
                    }
                }
                catch { }
            }
        }

        // =====================================================================
        // 6. In-Game Hotkeys (F8 Toggle, F9 Live Config Reload) & HUD Toast
        // =====================================================================
        [HarmonyPatch(typeof(BattleManager), nameof(BattleManager.Update))]
        public static class BattleManager_Update_Patch
        {
            public static void Postfix()
            {
                try
                {
                    // Periodic cleanup of expired firing timestamps
                    if (Time.time > _nextCleanupTime)
                    {
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

                    // Render 3D Veteran Chevron Icons & periodic dictionary cleanup
                    VeteranAndTacticsPatches.RenderVeteran3DMarkersAndCleanup();

                    // Hotkeys via BepInEx UnityInput
                    var input = UnityInput.Current;
                    if (input != null)
                    {
                        if (input.GetKeyDown(KeyCode.F8))
                        {
                            VisionConfig.EnableMod.Value = !VisionConfig.EnableMod.Value;
                            string state = VisionConfig.EnableMod.Value
                                ? $"ENABLED (Stand:{VisionConfig.StandingSightRange.Value}m, Crouch:{VisionConfig.CrouchingSightRange.Value}m, Prone:{VisionConfig.ProneSightRange.Value}m)"
                                : "DISABLED (Vanilla AI)";
                            ShowToast($"[HumanAI v0.2.8] Mod {state}");
                        }
                        else if (input.GetKeyDown(KeyCode.F9))
                        {
                            Plugin.Instance.Config.Reload();
                            ShowToast($"[HumanAI v0.2.8] Reloaded Config! Stand:{VisionConfig.StandingSightRange.Value}m | Crouch:{VisionConfig.CrouchingSightRange.Value}m | Prone:{VisionConfig.ProneSightRange.Value}m | VetTime:{VisionConfig.VeteranFullMaturitySeconds.Value}s");
                        }
                        else if (input.GetKeyDown(KeyCode.F10))
                        {
                            VeteranAndTacticsPatches.CleanupDictionaries();
                            ShowToast(
                                $"[HumanAI Stats] Vets:{VeteranAndTacticsPatches.ActiveVeteransCount} (Prom:{VeteranAndTacticsPatches.FieldPromotionsCount}) | " +
                                $"Drops:{VeteranAndTacticsPatches.SelfPreservationDrops} | Bipods:{VeteranAndTacticsPatches.BipodsDeployed} | WallBlk:{VeteranAndTacticsPatches.WallsBlocked} | " +
                                $"WallSide:{VeteranAndTacticsPatches.WallSideRepositionsCount} | TankBlk:{VeteranAndTacticsPatches.UselessTankFirePrevented} (Crew:{VeteranAndTacticsPatches.ExposedCrewTargeted}) | " +
                                $"FFSaved:{VeteranAndTacticsPatches.FriendlyFirePrevented} | Repairs:{VeteranAndTacticsPatches.ShelteredRepairsCount}",
                                6.5f);
                        }
                    }
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(BattleManager), nameof(BattleManager.OnGUI))]
        public static class BattleManager_OnGUI_Patch
        {
            public static void Postfix()
            {
                try
                {
                    if (Time.unscaledTime < _toastExpireTime && !string.IsNullOrEmpty(_toastMessage))
                    {
                        GUI.color = new Color(0f, 0f, 0f, 0.78f);
                        GUI.Box(new Rect(20f, 20f, 980f, 38f), "");
                        GUI.color = Color.Lerp(Color.yellow, Color.white, 0.35f);
                        GUI.Label(new Rect(30f, 28f, 960f, 26f), _toastMessage);
                        GUI.color = Color.white;
                    }

                    // Render Death Screen Kill Attribution banner (visible over both native DeathPanel and ragdoll_overhaul fade)
                    if (VisionConfig.EnableMod.Value &&
                        VisionConfig.EnableDeathScreenAttribution.Value &&
                        Time.unscaledTime < VeteranAndTacticsPatches.ActiveDeathAttributionExpireTime &&
                        !string.IsNullOrEmpty(VeteranAndTacticsPatches.ActiveDeathAttributionText))
                    {
                        Soldier cur = Soldier.CurrentControlledSoldierOrNull();
                        bool respawnedIntoNewSoldier = cur != null &&
                                                       cur.IsAlive &&
                                                       cur.Pointer != VeteranAndTacticsPatches.ActiveDeathAttributionVictimPtr;

                        // Once the player takes control of a new soldier, dismiss after a brief 4.2s minimum read window
                        if (respawnedIntoNewSoldier && Time.unscaledTime > VeteranAndTacticsPatches.ActiveDeathAttributionStartTime + 4.2f)
                        {
                            VeteranAndTacticsPatches.ActiveDeathAttributionExpireTime = 0f;
                        }
                        else
                        {
                            float boxW = Mathf.Min(Screen.width - 40f, 760f);
                            float boxH = 42f;
                            float boxX = (Screen.width - boxW) * 0.5f;
                            float boxY = Screen.height * 0.79f;

                            GUI.color = new Color(0.05f, 0.05f, 0.05f, 0.86f);
                            GUI.Box(new Rect(boxX, boxY, boxW, boxH), "");

                            if (VeteranAndTacticsPatches.ActiveDeathAttributionIsFF)
                                GUI.color = new Color(1.0f, 0.36f, 0.36f, 1.0f);
                            else if (VeteranAndTacticsPatches.ActiveDeathAttributionIsSelf)
                                GUI.color = new Color(1.0f, 0.72f, 0.30f, 1.0f);
                            else
                                GUI.color = new Color(0.94f, 0.91f, 0.83f, 1.0f);

                            GUI.Label(new Rect(boxX + 18f, boxY + 11f, boxW - 36f, 24f), VeteranAndTacticsPatches.ActiveDeathAttributionText);
                            GUI.color = Color.white;
                        }
                    }
                }
                catch { }
            }
        }
    }
}
