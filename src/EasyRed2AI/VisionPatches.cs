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

        // On-screen status toast & F7 Interactive Configuration Menu
        private static string _toastMessage = "";
        private static float _toastExpireTime;
        private static bool _showConfigMenu;

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

                        // When a stationary defender is actively concentrated on a known threat sector,
                        // their peripheral FOV narrows against quiet, unspotted flankers!
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

                    // Apply scope magnification only if target is within the narrower optic cone
                    float allowedOpticHalfFOV = VisionConfig.OpticLongRangeHalfFOV.Value;
                    VeteranAndTacticsPatches.SoldierPersonality observerPers = VeteranAndTacticsPatches.GetPersonality(observer);
                    if (observerPers == VeteranAndTacticsPatches.SoldierPersonality.Marksman_Spotter)
                    {
                        allowedOpticHalfFOV += 5.0f; // Counter-Sniper / Spotter has a wider scoped scan cone
                    }

                    if (opticZoom > 1.0f && angle <= allowedOpticHalfFOV)
                    {
                        _currentObserverOpticMultiplier = opticZoom;
                        BoostedByScope++;
                    }

                    // Stationary Threat-Sector Overwatch:
                    // - 1.20x (or 1.28x for Overwatch MG / Spotter) spotting range bonus within central 28-deg watched sector
                    // - 0.72x peripheral spotting range against quiet flankers outside 38-deg cone when focused on an active threat
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
                    Vehicle turretVeh = turret.GetComponentInParent<Vehicle>();
                    VeteranAndTacticsPatches.SoldierPersonality crewPers = turretVeh != null
                        ? VeteranAndTacticsPatches.GetVehicleCrewPersonality(turretVeh)
                        : VeteranAndTacticsPatches.SoldierPersonality.None;

                    // Against infantry on foot, enforce realistic turret FOV cones
                    if (isInfantryTarget && VisionConfig.EnforceHumanFOVWhenAlerted.Value)
                    {
                        if (dist > VisionConfig.CloseOmnidirectionalRange.Value)
                        {
                            float allowedHalfFOV = dist <= VisionConfig.MidRangeMaxDistance.Value
                                ? VisionConfig.MidRangeHalfFOV.Value
                                : VisionConfig.LongRangeHalfFOV.Value;

                            if (crewPers == VeteranAndTacticsPatches.SoldierPersonality.LightCrew_FlakWatch)
                                allowedHalfFOV += 12.0f; // Flak Watch crew scans a wider sector

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
                        float opticCone = VisionConfig.OpticLongRangeHalfFOV.Value;
                        if (crewPers == VeteranAndTacticsPatches.SoldierPersonality.TankCrew_Sniper)
                            opticCone += 5.0f; // Sniper Crew has a wider long-range turret optic scan cone

                        if (angle <= opticCone)
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

                    // QoL: Short-press of the radial Squad Orders keybind (GameInput.OrdersMenu, default Tab) spots in look direction
                    if (VisionConfig.EnableMod.Value && VisionConfig.EnableQuickTapSpot.Value && !_showConfigMenu)
                    {
                        PlayerController pc = PlayerController.currentController;
                        Soldier player = pc != null ? pc.ControlledCharacter : null;
                        if (pc != null &&
                            !pc.lockRotationAndFiring &&
                            player != null &&
                            player.IsAlive &&
                            !player.HasSurrended() &&
                            player.joinedSquad != null)
                        {
                            VirtualGamepad gpad = GamepadsAPI.GetGamepad(0);
                            if (gpad != null && gpad.GetButtonUp(GameInput.OrdersMenu, out float heldDuration, 0))
                            {
                                float holdThreshold = PlayerController.holdButtonDuration > 0.05f ? PlayerController.holdButtonDuration : 0.25f;
                                if (heldDuration > 0f && heldDuration < holdThreshold)
                                {
                                    PlayerController.TrySpotInLookDirection();
                                }
                            }
                        }
                    }

                    // Hotkeys via BepInEx UnityInput
                    var input = UnityInput.Current;
                    if (input != null)
                    {
                        if (input.GetKeyDown(KeyCode.F6))
                        {
                            _showConfigMenu = !_showConfigMenu;
                            if (_showConfigMenu)
                            {
                                Cursor.visible = true;
                                Cursor.lockState = CursorLockMode.None;
                            }
                        }
                        else if (input.GetKeyDown(KeyCode.F8))
                        {
                            VisionConfig.EnableMod.Value = !VisionConfig.EnableMod.Value;
                            Plugin.Instance.Config.Save();
                            string state = VisionConfig.EnableMod.Value
                                ? $"ENABLED (Stand:{VisionConfig.StandingSightRange.Value}m, Crouch:{VisionConfig.CrouchingSightRange.Value}m, Prone:{VisionConfig.ProneSightRange.Value}m)"
                                : "DISABLED (Vanilla AI)";
                            ShowToast($"[HumanAI v{Plugin.PluginVersion}] Mod {state}");
                        }
                        else if (input.GetKeyDown(KeyCode.F9))
                        {
                            Plugin.Instance.Config.Reload();
                            ShowToast($"[HumanAI v{Plugin.PluginVersion}] Reloaded Config! Stand:{VisionConfig.StandingSightRange.Value}m | Crouch:{VisionConfig.CrouchingSightRange.Value}m | Prone:{VisionConfig.ProneSightRange.Value}m | Personalities:{VisionConfig.EnableRolePersonalities.Value}");
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
                            float boxW = Mathf.Min(Screen.width - 40f, 920f);
                            float boxH = 58f;
                            float boxX = (Screen.width - boxW) * 0.5f;
                            float boxY = Screen.height * 0.78f;

                            GUI.color = new Color(0.04f, 0.04f, 0.05f, 0.90f);
                            GUI.Box(new Rect(boxX, boxY, boxW, boxH), "");
                            GUI.Box(new Rect(boxX + 2f, boxY + 2f, boxW - 4f, boxH - 4f), "");

                            GUIStyle labelStyle = GUI.skin != null ? GUI.skin.label : null;
                            int oldFontSize = 0;
                            TextAnchor oldAlign = TextAnchor.UpperLeft;
                            FontStyle oldFontStyle = FontStyle.Normal;
                            bool oldWordWrap = false;

                            if (labelStyle != null)
                            {
                                oldFontSize = labelStyle.fontSize;
                                oldAlign = labelStyle.alignment;
                                oldFontStyle = labelStyle.fontStyle;
                                oldWordWrap = labelStyle.wordWrap;

                                labelStyle.fontSize = 19;
                                labelStyle.alignment = TextAnchor.MiddleCenter;
                                labelStyle.fontStyle = FontStyle.Bold;
                                labelStyle.wordWrap = true;
                            }

                            Rect textRect = new Rect(boxX + 16f, boxY + 4f, boxW - 32f, boxH - 8f);
                            Rect shadowRect = new Rect(textRect.x + 1.5f, textRect.y + 1.5f, textRect.width, textRect.height);

                            // Crisp dark drop-shadow
                            GUI.color = new Color(0f, 0f, 0f, 0.92f);
                            GUI.Label(shadowRect, VeteranAndTacticsPatches.ActiveDeathAttributionText);

                            if (VeteranAndTacticsPatches.ActiveDeathAttributionIsFF)
                                GUI.color = new Color(1.0f, 0.36f, 0.36f, 1.0f);
                            else if (VeteranAndTacticsPatches.ActiveDeathAttributionIsSelf)
                                GUI.color = new Color(1.0f, 0.74f, 0.30f, 1.0f);
                            else
                                GUI.color = new Color(0.96f, 0.93f, 0.85f, 1.0f);

                            GUI.Label(textRect, VeteranAndTacticsPatches.ActiveDeathAttributionText);

                            if (labelStyle != null)
                            {
                                labelStyle.fontSize = oldFontSize;
                                labelStyle.alignment = oldAlign;
                                labelStyle.fontStyle = oldFontStyle;
                                labelStyle.wordWrap = oldWordWrap;
                            }

                            GUI.color = Color.white;
                        }
                    }

                    // Render F6 Interactive Feature Toggle Menu
                    if (_showConfigMenu)
                    {
                        Cursor.visible = true;
                        Cursor.lockState = CursorLockMode.None;
                        DrawConfigMenu();
                    }
                }
                catch { }
            }

            private static void DrawConfigMenu()
            {
                float panelW = 760f;
                float panelH = 592f;
                float panelX = (Screen.width - panelW) * 0.5f;
                float panelY = (Screen.height - panelH) * 0.5f;

                GUI.color = new Color(0.06f, 0.07f, 0.09f, 0.94f);
                GUI.Box(new Rect(panelX, panelY, panelW, panelH), "");

                GUI.color = new Color(1.0f, 0.88f, 0.35f, 1.0f);
                GUI.Label(new Rect(panelX + 20f, panelY + 14f, panelW - 40f, 24f),
                    $"Easy Red 2 — Humanized AI & Veteran Tactics v{Plugin.PluginVersion}  |  Feature Toggles (F6 to Close)");

                GUI.color = new Color(0.75f, 0.78f, 0.82f, 1.0f);
                GUI.Label(new Rect(panelX + 20f, panelY + 36f, panelW - 40f, 20f),
                    "Click any feature below to toggle it ON/OFF live in-game (automatically saves to com.antigravity.easyred2.humanai.cfg):");

                float colLeftX = panelX + 20f;
                float colRightX = panelX + 390f;
                float colW = 350f;
                float rowH = 26f;
                float startY = panelY + 66f;

                // Left Column: Vision, Veteran Progression & Role Personalities
                float yL = startY;
                DrawSectionHeader(colLeftX, ref yL, "Vision, Stealth & FOV");
                DrawToggleButton(colLeftX, ref yL, colW, rowH, "Master Mod Toggle (F8)", VisionConfig.EnableMod);
                DrawToggleButton(colLeftX, ref yL, colW, rowH, "Preserve Stance Stealth in Combat", VisionConfig.PreserveStanceStealthInCombat);
                DrawToggleButton(colLeftX, ref yL, colW, rowH, "Enforce Human FOV Cones", VisionConfig.EnforceHumanFOVWhenAlerted);
                DrawToggleButton(colLeftX, ref yL, colW, rowH, "Threat-Sector Gaze & Flanking", VisionConfig.EnableThreatSectorConcentration);
                DrawToggleButton(colLeftX, ref yL, colW, rowH, "Scope & Turret Optic Scaling", VisionConfig.EnableScopeVisionScaling);
                DrawToggleButton(colLeftX, ref yL, colW, rowH, "Limit Aircraft Infantry Spotting", VisionConfig.LimitAircraftInfantrySpotting);

                yL += 6f;
                DrawSectionHeader(colLeftX, ref yL, "15-Min Veteran System");
                DrawToggleButton(colLeftX, ref yL, colW, rowH, "15-Minute Veteran Progression", VisionConfig.EnableVeteranSystem);
                DrawToggleButton(colLeftX, ref yL, colW, rowH, "Field Rank Promotions & Decals", VisionConfig.PromoteVeteranRanks);
                DrawToggleButton(colLeftX, ref yL, colW, rowH, "3D Overhead Veteran Icons", VisionConfig.ShowVeteran3DIcons);
                DrawToggleButton(colLeftX, ref yL, colW, rowH, "Squad Roster Veteran Tags", VisionConfig.ShowVeteranSquadRosterTags);

                yL += 6f;
                DrawSectionHeader(colLeftX, ref yL, "AI Role Personalities (3 Per Role)");
                DrawToggleButton(colLeftX, ref yL, colW, rowH, "Enable AI Role Personalities (Master)", VisionConfig.EnableRolePersonalities);
                DrawToggleButton(colLeftX, ref yL, colW, rowH, "Infantry Role Personalities (21 Types)", VisionConfig.EnableInfantryPersonalities);
                DrawToggleButton(colLeftX, ref yL, colW, rowH, "Vehicle & Pilot Personalities (9 Types)", VisionConfig.EnableVehicleAndPilotPersonalities);
                DrawToggleButton(colLeftX, ref yL, colW, rowH, "Veterancy Cures Skittishness", VisionConfig.VeterancyCuresSkittishness);
                DrawToggleButton(colLeftX, ref yL, colW, rowH, "Show Personalities in Squad Roster", VisionConfig.ShowPersonalityInSquadRoster);

                // Right Column: Self-Preservation, Cover, Anti-Armor & Death Screen
                float yR = startY;
                DrawSectionHeader(colRightX, ref yR, "Self-Preservation & Cover");
                DrawToggleButton(colRightX, ref yR, colW, rowH, "Self-Preservation Stances", VisionConfig.EnableSelfPreservationStance);
                DrawToggleButton(colRightX, ref yR, colW, rowH, "Enforce Standing Out of Combat", VisionConfig.EnforceStandingWhenOutOfCombat);
                DrawToggleButton(colRightX, ref yR, colW, rowH, "Never Shoot Standing in Open", VisionConfig.NeverShootStandingInOpen);
                DrawToggleButton(colRightX, ref yR, colW, rowH, "Seek Cover Before Bandaging", VisionConfig.BandageOnlyAfterFindingCover);
                DrawToggleButton(colRightX, ref yR, colW, rowH, "Stand BEHIND Walls (Smart Wall Cover)", VisionConfig.EnableSmartWallSideCover);
                DrawToggleButton(colRightX, ref yR, colW, rowH, "Sheltered Tank Crew Repairs", VisionConfig.EnableSmartTankCrewRepair);

                yR += 6f;
                DrawSectionHeader(colRightX, ref yR, "Muzzle Discipline, Anti-Armor & HUD");
                DrawToggleButton(colRightX, ref yR, colW, rowH, "Stop Small-Arms Fire at Tanks / Aim Crew", VisionConfig.PreventSmallArmsFireAtTanks);
                DrawToggleButton(colRightX, ref yR, colW, rowH, "Gunner Bipod Wall & Ground Mounting", VisionConfig.EnableGunnerBipodWallMounting);
                DrawToggleButton(colRightX, ref yR, colW, rowH, "Wall-Shooting Prevention", VisionConfig.EnableWallShootingPrevention);
                DrawToggleButton(colRightX, ref yR, colW, rowH, "Auto-Peek Over Low Cover", VisionConfig.EnableAutoPeekOverCover);
                DrawToggleButton(colRightX, ref yR, colW, rowH, "Friendly Fire Avoidance", VisionConfig.EnableFriendlyFireAvoidance);
                DrawToggleButton(colRightX, ref yR, colW, rowH, "Anti-Stacking Personal Spacing", VisionConfig.EnableAntiStacking);
                DrawToggleButton(colRightX, ref yR, colW, rowH, "Wall & Fence Vaulting", VisionConfig.EnableWallVaulting);
                DrawToggleButton(colRightX, ref yR, colW, rowH, "Surrendered Comrade Liberation", VisionConfig.EnableComradeLiberation);
                DrawToggleButton(colRightX, ref yR, colW, rowH, "Short-Press Orders Key to Spot", VisionConfig.EnableQuickTapSpot);
                DrawToggleButton(colRightX, ref yR, colW, rowH, "Death Screen Kill Attribution", VisionConfig.EnableDeathScreenAttribution);

                // Bottom Action Buttons
                float bottomY = panelY + panelH - 38f;
                GUI.color = new Color(0.75f, 0.90f, 1.0f, 1.0f);
                if (GUI.Button(new Rect(panelX + 20f, bottomY, 220f, 26f), "Reload .cfg from Disk (F9)"))
                {
                    Plugin.Instance.Config.Reload();
                    ShowToast($"[HumanAI v{Plugin.PluginVersion}] Reloaded configuration from disk!");
                }

                GUI.color = new Color(0.85f, 1.0f, 0.85f, 1.0f);
                if (GUI.Button(new Rect(panelX + 260f, bottomY, 220f, 26f), "Save to .cfg File"))
                {
                    Plugin.Instance.Config.Save();
                    ShowToast($"[HumanAI v{Plugin.PluginVersion}] Saved configuration to com.antigravity.easyred2.humanai.cfg!");
                }

                GUI.color = new Color(1.0f, 0.80f, 0.80f, 1.0f);
                if (GUI.Button(new Rect(panelX + 520f, bottomY, 220f, 26f), "Close Menu (F6)"))
                {
                    _showConfigMenu = false;
                }

                GUI.color = Color.white;
            }

            private static void DrawSectionHeader(float x, ref float y, string title)
            {
                GUI.color = new Color(0.55f, 0.82f, 1.0f, 1.0f);
                GUI.Label(new Rect(x, y, 350f, 20f), $"=== {title} ===");
                y += 20f;
            }

            private static void DrawToggleButton(float x, ref float y, float width, float height, string label, BepInEx.Configuration.ConfigEntry<bool> entry)
            {
                if (entry == null) return;
                bool val = entry.Value;
                GUI.color = val
                    ? new Color(0.45f, 0.95f, 0.52f, 1.0f)
                    : new Color(0.95f, 0.45f, 0.45f, 1.0f);

                string status = val ? "[ON]  " : "[OFF] ";
                if (GUI.Button(new Rect(x, y, width, height - 2f), status + label))
                {
                    entry.Value = !val;
                    Plugin.Instance.Config.Save();
                }
                y += height;
            }
        }
    }
}
