using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace EasyRed2HumanAI
{
    public static partial class VeteranAndTacticsPatches
    {
        private struct VehicleAimCache
        {
            public IntPtr VehiclePtr;
            public float ExpireTime;
            public bool CanEngage;
            public bool HasCustomAimPoint;
            public Vector3 CustomAimPoint;
        }

        private static readonly Dictionary<IntPtr, VehicleAimCache> _vehicleAimCache = new Dictionary<IntPtr, VehicleAimCache>(128);
        private static readonly Dictionary<IntPtr, float> _nextTankDisciplineCountTime = new Dictionary<IntPtr, float>(256);
        private static readonly Dictionary<IntPtr, float> _repairTaskStartTime = new Dictionary<IntPtr, float>(64);
        private static readonly HashSet<IntPtr> _countedRepairTasks = new HashSet<IntPtr>();
        private static readonly Dictionary<IntPtr, float> _unreachableEmplacementUntil = new Dictionary<IntPtr, float>(128);
        private static readonly List<RepairStation> _activeRepairStations = new List<RepairStation>(16);
        private static float _nextRepairStationScanTime;
        private static readonly Dictionary<IntPtr, RepairStation> _vehiclesSeekingRepairStation = new Dictionary<IntPtr, RepairStation>(32);
        private static readonly Dictionary<IntPtr, IntPtr> _squadOrderedToVehicle = new Dictionary<IntPtr, IntPtr>(32);
        private static readonly Dictionary<IntPtr, float> _squadTaskCommittedUntil = new Dictionary<IntPtr, float>(64);
        private static readonly Dictionary<IntPtr, float> _nextTurretFFCheckTime = new Dictionary<IntPtr, float>(64);
        private static readonly Dictionary<IntPtr, float> _turretFFBlockedUntil = new Dictionary<IntPtr, float>(64);

        // =====================================================================
        // Helper: Sheltered Tank / Vehicle Repair Position (Leeward Hull Cover)
        // =====================================================================
        private static Vector3 GetShelteredRepairPosition(Vehicle veh, Soldier mechanic, out Vector3 shelterDir)
        {
            shelterDir = Vector3.back;
            if (veh == null || veh.transform == null)
                return Vector3.zero;

            Vector3 vehPos = veh.transform.position;
            Vector3 vehFwd = veh.transform.forward;
            vehFwd.y = 0f;
            vehFwd = vehFwd.sqrMagnitude > 0.001f ? vehFwd.normalized : Vector3.forward;

            Vector3 threatDir = Vector3.zero;

            // 1. Check the mechanic's own visible target first
            if (mechanic != null && mechanic.aiController != null && mechanic.aiController.visibleTarget != null)
            {
                Vector3 toEnemy = mechanic.aiController.visibleTarget.GetCenterOfUnit() - vehPos;
                toEnemy.y = 0f;
                if (toEnemy.sqrMagnitude > 0.25f)
                    threatDir = toEnemy.normalized;
            }

            // 2. Check the vehicle's main turret target
            Turret mainTurret = veh.GetMainTurret(false);
            if (threatDir.sqrMagnitude < 0.01f && mainTurret != null && mainTurret.autoPilot != null)
            {
                Spottable turretTarget = mainTurret.autoPilot.CurrentVisibleTarget;
                if (turretTarget != null)
                {
                    Vector3 toEnemy = turretTarget.GetCenterOfUnit() - vehPos;
                    toEnemy.y = 0f;
                    if (toEnemy.sqrMagnitude > 0.25f)
                        threatDir = toEnemy.normalized;
                }
            }

            // 3. Check secondary seat turrets or seated crew members' targets
            if (threatDir.sqrMagnitude < 0.01f && veh.seats != null)
            {
                var seats = veh.seats;
                int seatCount = seats.Length;
                for (int i = 0; i < seatCount; i++)
                {
                    Seats seat = seats[i];
                    if (seat == null) continue;

                    if (seat.connectedTurret != null && seat.connectedTurret.autoPilot != null)
                    {
                        Spottable t = seat.connectedTurret.autoPilot.CurrentVisibleTarget;
                        if (t != null)
                        {
                            Vector3 toEnemy = t.GetCenterOfUnit() - vehPos;
                            toEnemy.y = 0f;
                            if (toEnemy.sqrMagnitude > 0.25f)
                            {
                                threatDir = toEnemy.normalized;
                                break;
                            }
                        }
                    }

                    if (seat.unitSet != null && seat.unitSet.aiController != null && seat.unitSet.aiController.visibleTarget != null)
                    {
                        Vector3 toEnemy = seat.unitSet.aiController.visibleTarget.GetCenterOfUnit() - vehPos;
                        toEnemy.y = 0f;
                        if (toEnemy.sqrMagnitude > 0.25f)
                        {
                            threatDir = toEnemy.normalized;
                            break;
                        }
                    }
                }
            }

            // 4. Use the main turret's aim / fire direction (points toward the last known threat sector)
            if (threatDir.sqrMagnitude < 0.01f && mainTurret != null)
            {
                Vector3 fireDir = mainTurret.GetFireDir();
                fireDir.y = 0f;
                if (fireDir.sqrMagnitude > 0.01f)
                    threatDir = fireDir.normalized;
            }

            // 5. Fallback to the tank hull's forward vector (enemies are typically ahead of the frontal glacis)
            if (threatDir.sqrMagnitude < 0.01f)
            {
                threatDir = vehFwd;
            }

            // Sheltered side is directly opposite the threat so the tank hull acts as solid armor cover
            shelterDir = -threatDir;

            float absFwdDot = Mathf.Abs(Vector3.Dot(shelterDir, vehFwd));
            float hullOffset = Mathf.Lerp(2.7f, 4.1f, absFwdDot);

            Transform vehRoot = veh.transform.root;
            Vector3 rayOrigin = vehPos + Vector3.up * 1.1f;
            if (AiCore.IsDirectionBlockedByEnvironment(rayOrigin, shelterDir, hullOffset + 0.5f, vehRoot))
            {
                Vector3 altLeft = Quaternion.Euler(0f, 55f, 0f) * shelterDir;
                float altLeftOffset = Mathf.Lerp(2.7f, 4.1f, Mathf.Abs(Vector3.Dot(altLeft, vehFwd)));
                if (!AiCore.IsDirectionBlockedByEnvironment(rayOrigin, altLeft, altLeftOffset + 0.5f, vehRoot))
                {
                    shelterDir = altLeft;
                    hullOffset = altLeftOffset;
                }
                else
                {
                    Vector3 altRight = Quaternion.Euler(0f, -55f, 0f) * shelterDir;
                    float altRightOffset = Mathf.Lerp(2.7f, 4.1f, Mathf.Abs(Vector3.Dot(altRight, vehFwd)));
                    if (!AiCore.IsDirectionBlockedByEnvironment(rayOrigin, altRight, altRightOffset + 0.5f, vehRoot))
                    {
                        shelterDir = altRight;
                        hullOffset = altRightOffset;
                    }
                }
            }

            return vehPos + shelterDir * hullOffset;
        }

        // =====================================================================
        // Smart Anti-Armor Target & Fire Discipline
        // =====================================================================
        private static bool TryEvaluateVehicleTarget(
            Soldier shooter,
            Vehicle veh,
            float dist,
            out Soldier exposedCrewTarget,
            out Vector3 customAimPoint,
            out bool hasCustomAimPoint)
        {
            exposedCrewTarget = null;
            customAimPoint = Vector3.zero;
            hasCustomAimPoint = false;

            if (shooter == null || veh == null)
                return false;

            GenericGun heldGun = shooter.GetHeldGun();
            if (heldGun == null)
                return false;

            float maxShootDist = heldGun.DamageRange();
            if (dist > maxShootDist + 5f)
                return false;

            bool isAtWeapon = heldGun.IsAtWeapon();
            bool isExplosiveOrFlame = heldGun.isFlamethrower || heldGun.IsGrenadeLauncher();
            float bulletPenMm = 0f;

            AmmoItem ammoItem = heldGun.GetAmmoItem();
            if (ammoItem == null)
                ammoItem = heldGun.usedAmmoItem;

            if (ammoItem != null)
            {
                BulletData bd = ammoItem.GetBulletData();
                if (bd != null)
                {
                    if (bd.IsHe() || bd.IsFire() || bd.explosion_penetration > 10f || bd.mmPenetration > 25)
                    {
                        isExplosiveOrFlame = true;
                    }
                    bulletPenMm = bd.CalculateMmPenetrationWithFallof(dist);
                }
            }

            if (bulletPenMm <= 0.01f)
            {
                bulletPenMm = isAtWeapon ? 35f : 6f;
            }

            Vector3 shooterPos = shooter.transform.position;

            // 1. Check for exposed crew members / passengers (Seats.showUnitSet == true)
            var seats = veh.seats;
            if (seats != null)
            {
                float bestCrewDistSq = float.MaxValue;
                int seatCount = seats.Length;
                for (int i = 0; i < seatCount; i++)
                {
                    Seats seat = seats[i];
                    if (seat == null || !seat.showUnitSet)
                        continue;

                    Soldier crew = seat.unitSet;
                    if (crew == null || !crew.IsAlive || crew.faction == shooter.faction)
                        continue;

                    Spottable crewSpottable = crew.Cast<Spottable>();
                    if (shooter.CanSee(crewSpottable))
                    {
                        float dSq = (crew.transform.position - shooterPos).sqrMagnitude;
                        if (dSq < bestCrewDistSq)
                        {
                            bestCrewDistSq = dSq;
                            exposedCrewTarget = crew;
                        }
                    }
                }

                if (exposedCrewTarget != null && !isAtWeapon && !isExplosiveOrFlame)
                {
                    customAimPoint = exposedCrewTarget.GetCenterOfUnit();
                    hasCustomAimPoint = true;
                    return true;
                }
            }

            // 2. Dedicated explosive / HEAT / Flamethrower / heavy AT weapons (>40mm pen) can engage vehicle hulls directly
            if (isExplosiveOrFlame || bulletPenMm >= 40f)
            {
                return true;
            }

            bool isTank = veh.TryCast<VehicleTank>() != null;

            // Standard small arms (<12mm penetration) cannot penetrate tank hulls or tank tracks
            if (isTank && !isAtWeapon && bulletPenMm < 12f)
            {
                if (exposedCrewTarget != null)
                {
                    customAimPoint = exposedCrewTarget.GetCenterOfUnit();
                    hasCustomAimPoint = true;
                    return true;
                }
                return false;
            }

            // 3. Inspect vehicle damageable parts for penetrable hull armor, vulnerable wheels, or breakable tracks
            var parts = veh.damageableParts;
            if (parts != null)
            {
                bool canPenetrateMainHull = false;
                Vector3 bestPartPos = Vector3.zero;
                float bestPartDistSq = float.MaxValue;

                Vector3 bestWheelOrTrackPos = Vector3.zero;
                float bestWheelOrTrackDistSq = float.MaxValue;

                int partCount = parts.Count;
                for (int i = 0; i < partCount; i++)
                {
                    VehicleDamagablePart part = parts[i];
                    if (part == null || part.transform == null)
                        continue;

                    if (part.TryCast<VehicleDamagableDetachablePart>() != null)
                        continue;

                    VehicleDamageableWheelPart wheelPart = part.TryCast<VehicleDamageableWheelPart>();
                    if (wheelPart != null)
                    {
                        if (!isTank && !wheelPart.WheelIsFlat && dist <= 60f && bulletPenMm > wheelPart.armorThickness + 0.5f)
                        {
                            Vector3 wPos = wheelPart.transform.position;
                            float dSq = (wPos - shooterPos).sqrMagnitude;
                            if (dSq < bestWheelOrTrackDistSq)
                            {
                                bestWheelOrTrackDistSq = dSq;
                                bestWheelOrTrackPos = wPos;
                            }
                        }
                        continue;
                    }

                    VehicleDamageableTrackPart trackPart = part.TryCast<VehicleDamageableTrackPart>();
                    if (trackPart != null)
                    {
                        if (!trackPart.TrackIsBroken && bulletPenMm > trackPart.armorThickness + 0.5f)
                        {
                            Vector3 tPos = trackPart.transform.position;
                            float dSq = (tPos - shooterPos).sqrMagnitude;
                            if (dSq < bestWheelOrTrackDistSq)
                            {
                                bestWheelOrTrackDistSq = dSq;
                                bestWheelOrTrackPos = tPos;
                            }
                        }
                        continue;
                    }

                    float effectiveArmor = part.considerAngledArmor ? (part.armorThickness * 1.15f) : part.armorThickness;
                    if (bulletPenMm > effectiveArmor + 0.5f)
                    {
                        canPenetrateMainHull = true;
                        Vector3 pPos = part.transform.position;
                        float dSq = (pPos - shooterPos).sqrMagnitude;
                        if (dSq < bestPartDistSq)
                        {
                            bestPartDistSq = dSq;
                            bestPartPos = pPos;
                        }
                    }
                }

                if (canPenetrateMainHull)
                {
                    if (isTank && bestPartDistSq < float.MaxValue)
                    {
                        customAimPoint = bestPartPos;
                        hasCustomAimPoint = true;
                    }
                    return true;
                }

                if (bestWheelOrTrackDistSq < float.MaxValue)
                {
                    customAimPoint = bestWheelOrTrackPos;
                    hasCustomAimPoint = true;
                    return true;
                }
            }

            if (exposedCrewTarget != null)
            {
                customAimPoint = exposedCrewTarget.GetCenterOfUnit();
                hasCustomAimPoint = true;
                return true;
            }

            return false;
        }

        private static void TryThrowNormalGrenadeAtVehicle(Soldier soldier, Vehicle targetVeh, float dist)
        {
            if (!AiCore.IsLocallyOwnedBot(soldier) || targetVeh == null || targetVeh.transform == null || soldier.inventory == null)
                return;

            if (dist <= 7.5f || dist > 32f || soldier.isInBodyAnimation || soldier.isInHandsAnimation)
                return;

            VirtualItem grenadeItem = soldier.inventory.FindItemOfType<VirtualGrenade>();
            if (grenadeItem != null)
            {
                float maxThrowRange = soldier.GetGrenadeThrowRange(grenadeItem);
                if (dist <= maxThrowRange &&
                    UnityEngine.Random.Range(0, 100) > 65 &&
                    IsSafeToThrowGrenadeAt(soldier, targetVeh.transform.position, dist, 8.5f) &&
                    AiCore.TryConsumeBotRpcBudget(0.45f))
                {
                    soldier.Throw(grenadeItem, targetVeh.Cast<Spottable>());
                }
            }
        }

        internal static bool TryFindVisibleFootEnemy(Soldier shooter, out Spottable bestFootEnemy, out float bestFootDist)
        {
            bestFootEnemy = null;
            bestFootDist = float.MaxValue;
            if (shooter == null || shooter.transform == null)
                return false;

            GenericGun heldGun = shooter.GetHeldGun();
            float maxWeaponDist = heldGun != null ? Mathf.Min(heldGun.DamageRange(), 185.0f) : 140.0f;
            if (maxWeaponDist < 10.0f)
                return false;

            Vector3 myPos = shooter.transform.position;
            string myFaction = shooter.faction;

            // If this is an AT soldier, temporarily toggle isAtUnit off to query vanilla's octree
            if (shooter.isAtUnit)
            {
                shooter.isAtUnit = false;
                Spottable alt = shooter.GetBestVisibleEnemy(out float altDist);
                shooter.isAtUnit = true;
                if (alt != null && !alt.IsWheeledVehicleOrTank() && !alt.IsAirVehicle() && altDist <= maxWeaponDist)
                {
                    bestFootEnemy = alt;
                    bestFootDist = altDist;
                    return true;
                }
            }

            // For any infantryman whose octree nearest-enemy query returned an invulnerable tank,
            // check nearby enemy squads for a visible enemy foot soldier (e.g. infantry advancing with the tank!)
            RefreshCombinedArmsIntelIfNeeded();
            int clusterCount = _knownEnemyClusters.Count;
            for (int i = 0; i < clusterCount; i++)
            {
                KnownEnemyCluster cluster = _knownEnemyClusters[i];
                if (cluster.Faction == myFaction)
                    continue;

                for (int pass = 0; pass < 2; pass++)
                {
                    Spottable cand = pass == 0 ? cluster.PrimarySoldier : cluster.SecondarySoldier;
                    if (cand == null || cand.Pointer == IntPtr.Zero)
                        continue;

                    float d = Vector3.Distance(myPos, cand.GetCenterOfUnit());
                    if (d >= bestFootDist || d > maxWeaponDist)
                        continue;

                    if (shooter.CanSee(cand))
                    {
                        bestFootDist = d;
                        bestFootEnemy = cand;
                    }
                }
            }

            return bestFootEnemy != null;
        }

        private static bool TryEvadeArmorToCover(SoldierAI ai, Soldier soldier, Vehicle targetVeh, float dist)
        {
            if (ai == null || !AiCore.IsLocallyOwnedBot(soldier) || targetVeh == null || targetVeh.transform == null)
                return false;

            if (dist > 85f || soldier.IsOnVehicle() || soldier.UsingBipod() || soldier.isInBodyAnimation)
                return false;

            // If the soldier carries an AT grenade and is within assault distance (<= 38m), let them close in to throw it
            if (dist <= 38f && soldier.inventory != null && soldier.inventory.FindItemOfType<VirtualATGrenade>() != null)
                return false;

            Vector3 myPos = soldier.transform.position;
            Vector3 toTank = targetVeh.transform.position - myPos;
            toTank.y = 0f;
            Vector3 threatDir = toTank.sqrMagnitude > 0.01f ? toTank.normalized : soldier.transform.forward;
            Transform ignoreRoot = soldier.transform.root;

            // If already sheltered behind a front wall relative to the tank, stay low and concealed
            if (IsPositionShelteredByFrontCover(myPos, threatDir, ignoreRoot))
            {
                if (!ai.IsMoving())
                    SetBotPose(ai, soldier, SoldierPose.Crouch);
                return false;
            }

            // 1. If standing on the exposed front face of a wall, duck around to the leeward side immediately
            if (TryFindShelteredSideOfWall(myPos, threatDir, ignoreRoot, out Vector3 behindWallPos))
            {
                SetBotPose(ai, soldier, SoldierPose.Idle, true);
                ai.MoveDirectlyToward(behindWallPos, 1.2f);
                AiCore.LogDebug("AntiArmor", $"{soldier.faction} rifleman scrambling behind wall to evade {targetVeh.name} at {dist:F0}m.", 6.0f);
                return true;
            }

            // 2. Search for local cover around the soldier's current position (or assigned cover)
            if (!soldier.IsOnCover() && TrySeekNearbyCover(ai, soldier))
            {
                if (ai.IsMoving())
                {
                    SetBotPose(ai, soldier, SoldierPose.Idle, true);
                    return true;
                }
            }

            // 3. If caught in the open within 68m with no way to hurt the tank, bound diagonally away toward cover
            if (!soldier.IsOnCover() && !ai.IsMoving() && dist <= 68f)
            {
                Vector3 away = -threatDir;
                Vector3 right = new Vector3(threatDir.z, 0f, -threatDir.x);
                float sideSign = (soldier.Pointer.ToInt64() & 1L) == 0L ? 1f : -1f;

                Vector3 fleeDir1 = (away * 0.78f + right * (0.62f * sideSign)).normalized;
                Vector3 fleeDir2 = (away * 0.78f - right * (0.62f * sideSign)).normalized;

                Vector3 chosenDir = Vector3.zero;
                if (!AiCore.IsDirectionBlockedByEnvironment(myPos + Vector3.up * 0.85f, fleeDir1, 5.5f, ignoreRoot))
                    chosenDir = fleeDir1;
                else if (!AiCore.IsDirectionBlockedByEnvironment(myPos + Vector3.up * 0.85f, fleeDir2, 5.5f, ignoreRoot))
                    chosenDir = fleeDir2;
                else if (!AiCore.IsDirectionBlockedByEnvironment(myPos + Vector3.up * 0.85f, away, 5.5f, ignoreRoot))
                    chosenDir = away;

                if (chosenDir.sqrMagnitude > 0.01f)
                {
                    Vector3 retreatPos = myPos + chosenDir * 8.5f;
                    SetBotPose(ai, soldier, SoldierPose.Idle, true);
                    ai.MoveDirectlyToward(retreatPos, 1.5f);
                    AiCore.LogDebug("AntiArmor", $"{soldier.faction} infantryman fleeing open ground from {targetVeh.name} ({dist:F0}m).", 6.0f);
                    return true;
                }
            }

            return false;
        }

        // =====================================================================
        // Sheltered Tank & Vehicle Crew Repair Patches (VehicleRepairTask)
        // =====================================================================
        [HarmonyPatch(typeof(VehicleRepairTask), nameof(VehicleRepairTask.GetCoverPosition))]
        public static class VehicleRepairTask_GetCoverPosition_Patch
        {
            public static void Postfix(VehicleRepairTask __instance, ref Vector3 __result)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value ||
                        !VisionConfig.EnableSmartTankCrewRepair.Value ||
                        __instance == null)
                    {
                        return;
                    }

                    Vehicle veh = __instance.vehToRepair;
                    if (!AiCore.IsLocallyOwnedVehicle(veh) || veh.transform == null)
                        return;

                    Vector3 shelterPos = GetShelteredRepairPosition(veh, null, out _);
                    __instance.targetPos = shelterPos;
                    __instance.pose = SoldierPose.Crouch;
                    __result = shelterPos;
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(VehicleRepairTask), nameof(VehicleRepairTask.IsReached))]
        public static class VehicleRepairTask_IsReached_Patch
        {
            public static void Postfix(VehicleRepairTask __instance, Vector3 __0, ref bool __result)
            {
                try
                {
                    if (!__result ||
                        !VisionConfig.EnableMod.Value ||
                        !VisionConfig.EnableSmartTankCrewRepair.Value ||
                        __instance == null)
                    {
                        return;
                    }

                    if (!__instance.VehicleStillneedsRepair)
                        return;

                    Vehicle veh = __instance.vehToRepair;
                    if (!AiCore.IsLocallyOwnedVehicle(veh) || veh.transform == null)
                        return;

                    IntPtr taskPtr = __instance.Pointer;
                    if (!_repairTaskStartTime.TryGetValue(taskPtr, out float startTime))
                    {
                        startTime = Time.time;
                        _repairTaskStartTime[taskPtr] = startTime;
                    }

                    if (Time.time - startTime > 6.5f)
                        return;

                    Vector3 shelterPos = GetShelteredRepairPosition(veh, null, out Vector3 shelterDir);
                    Vector3 vehPos = veh.transform.position;

                    Vector3 toShelter = __0 - shelterPos;
                    toShelter.y = 0f;
                    if (toShelter.sqrMagnitude <= 2.25f * 2.25f)
                        return;

                    Vector3 toSoldier = __0 - vehPos;
                    toSoldier.y = 0f;
                    if (toSoldier.sqrMagnitude > 0.04f && Vector3.Dot(toSoldier.normalized, shelterDir) >= 0.30f)
                        return;

                    __result = false;
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(VehicleRepairTask), nameof(VehicleRepairTask.OnCoverReached))]
        public static class VehicleRepairTask_OnCoverReached_Patch
        {
            public static bool Prefix(VehicleRepairTask __instance, Soldier __0)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value ||
                        !VisionConfig.EnableSmartTankCrewRepair.Value ||
                        __instance == null ||
                        __0 == null)
                    {
                        return true;
                    }

                    if (!__instance.VehicleStillneedsRepair || !__0.IsAI() || !AiCore.IsLocallyOwnedBot(__0))
                        return true;

                    if (__0.aiController != null)
                    {
                        SetBotPose(__0.aiController, __0, SoldierPose.Crouch);

                        if (__0.GetSuppressionValue() > 55f &&
                            __0.aiController.HasEnemyAndCanFireAtHim &&
                            __0.aiController.distFromTarget > 0.1f &&
                            __0.aiController.distFromTarget < 35f)
                        {
                            return false;
                        }
                    }
                    else
                    {
                        __0.SetPose(SoldierPose.Crouch, true, false, 0.2f);
                    }

                    if (_countedRepairTasks.Add(__instance.Pointer))
                    {
                        ShelteredRepairsCount++;
                    }
                }
                catch { }
                return true;
            }
        }

        // =====================================================================
        // PHASE 3: Macro Objective Director, Static Gun Staffing, Smart APC
        // Dismounting & Tank Standoff / Reverse Discipline
        // =====================================================================
        private static float _nextMacroDirectorTime;
        internal static readonly Dictionary<IntPtr, IntPtr> _garrisonAssignedObjectiveBySquad = new Dictionary<IntPtr, IntPtr>(64);
        internal static readonly Dictionary<IntPtr, float> _assignedEmplacementUntil = new Dictionary<IntPtr, float>(128);
        internal static readonly Dictionary<IntPtr, float> _nextApcCheckTime = new Dictionary<IntPtr, float>(64);
        internal static readonly Dictionary<IntPtr, float> _nextApcDismountCooldown = new Dictionary<IntPtr, float>(64);
        internal static readonly Dictionary<IntPtr, float> _nextTankStandoffCountTime = new Dictionary<IntPtr, float>(64);
        internal static readonly Dictionary<IntPtr, Vector2> _cachedVehicleExtents = new Dictionary<IntPtr, Vector2>(64);
        internal static readonly Dictionary<IntPtr, float> _nextVehiclePedestrianScanTime = new Dictionary<IntPtr, float>(64);
        internal static readonly Dictionary<IntPtr, float> _vehicleYieldUntilTime = new Dictionary<IntPtr, float>(64);
        internal static readonly Dictionary<IntPtr, float> _vehicleContinuousYieldStart = new Dictionary<IntPtr, float>(64);
        internal static readonly Dictionary<IntPtr, float> _nextFriendlyStepAsideTime = new Dictionary<IntPtr, float>(128);
        internal static readonly Dictionary<IntPtr, float> _nextFriendlyYieldCountTime = new Dictionary<IntPtr, float>(64);
        internal static readonly Dictionary<IntPtr, float> _nextVehicleSupportSteerTime = new Dictionary<IntPtr, float>(64);
        internal static readonly Dictionary<IntPtr, float> _nextPlaneOrbitSteerTime = new Dictionary<IntPtr, float>(32);
        internal static readonly Dictionary<IntPtr, float> _nextCombinedArmsAssistCountTime = new Dictionary<IntPtr, float>(64);
        internal static readonly HashSet<IntPtr> _activeKamikazePlanes = new HashSet<IntPtr>();
        internal static readonly Dictionary<IntPtr, float> _nextKamikazeTargetSearchTime = new Dictionary<IntPtr, float>(16);
        public static int CombinedArmsPriorityAssistsCount;
        public static int KamikazeDivesTriggeredCount;

        private struct ActiveRadioSupportInfo
        {
            public string Faction;
            public Vector3 Position;
            public RadioRequest RequestType;
        }

        private struct KnownEnemyCluster
        {
            public string Faction;
            public Vector3 Position;
            public float BasePriority;
            public bool IsMarkedOrPinPoint;
            public bool IsHalfMarked;
            public bool NearRadioRequest;
            public bool IsEngagingFriendlies;
            public Spottable VehicleTarget;
            public Spottable PrimarySoldier;
            public Spottable SecondarySoldier;
        }

        private struct AirborneAircraftInfo
        {
            public string Faction;
            public VehiclePlane Plane;
            public Spottable Spottable;
            public Vector3 Position;
            public bool IsBomber;
        }

        private static float _nextCombinedArmsIntelRefreshTime;
        private static readonly List<ActiveRadioSupportInfo> _activeRadioRequests = new List<ActiveRadioSupportInfo>(16);
        private static readonly List<KnownEnemyCluster> _knownEnemyClusters = new List<KnownEnemyCluster>(32);
        private static readonly List<AirborneAircraftInfo> _airborneAircraft = new List<AirborneAircraftInfo>(8);

        public static void ClearCombinedArmsCaches()
        {
            _activeRadioRequests.Clear();
            _knownEnemyClusters.Clear();
            _airborneAircraft.Clear();
            _activeKamikazePlanes.Clear();
            _nextKamikazeTargetSearchTime.Clear();
            _nextCombinedArmsIntelRefreshTime = 0f;
            if (_nextVehicleSupportSteerTime.Count > 128)
                _nextVehicleSupportSteerTime.Clear();
            if (_nextPlaneOrbitSteerTime.Count > 64)
                _nextPlaneOrbitSteerTime.Clear();
            if (_nextCombinedArmsAssistCountTime.Count > 128)
                _nextCombinedArmsAssistCountTime.Clear();
            if (_squadTaskCommittedUntil.Count > 128)
                _squadTaskCommittedUntil.Clear();
            if (_nextTurretFFCheckTime.Count > 128)
            {
                _nextTurretFFCheckTime.Clear();
                _turretFFBlockedUntil.Clear();
            }
            if (_cachedVehicleExtents.Count > 128)
                _cachedVehicleExtents.Clear();
            if (_nextVehiclePedestrianScanTime.Count > 128)
            {
                _nextVehiclePedestrianScanTime.Clear();
                _vehicleYieldUntilTime.Clear();
                _vehicleContinuousYieldStart.Clear();
                _nextFriendlyYieldCountTime.Clear();
            }
        }

        private static void RecordCombinedArmsAssist(IntPtr unitPtr, float now, string tag, string msg)
        {
            if (!_nextCombinedArmsAssistCountTime.TryGetValue(unitPtr, out float nextCount) || now >= nextCount)
            {
                _nextCombinedArmsAssistCountTime[unitPtr] = now + 6.0f;
                CombinedArmsPriorityAssistsCount++;
                if (VisionConfig.DebugLogging.Value)
                {
                    AiCore.LogDebug(tag, msg);
                }
            }
        }

        private static string GetVehicleCrewFaction(Vehicle veh, Turret turret = null)
        {
            if (veh == null)
                return null;

            if (turret != null)
            {
                Soldier gunner = turret.GetSittedUnit(veh);
                if (gunner != null && !string.IsNullOrEmpty(gunner.faction))
                    return gunner.faction;
            }

            string vf = veh.GetVehicleFaction();
            if (!string.IsNullOrEmpty(vf))
                return vf;

            var seats = veh.seats;
            if (seats != null)
            {
                for (int i = 0; i < seats.Length; i++)
                {
                    Seats s = seats[i];
                    if (s != null && s.unitSet != null && !string.IsNullOrEmpty(s.unitSet.faction))
                        return s.unitSet.faction;
                }
            }

            return null;
        }

        private static void RefreshCombinedArmsIntelIfNeeded()
        {
            float now = Time.time;
            if (now < _nextCombinedArmsIntelRefreshTime)
                return;

            _nextCombinedArmsIntelRefreshTime = now + 1.0f;
            _activeRadioRequests.Clear();
            _knownEnemyClusters.Clear();
            _airborneAircraft.Clear();

            if (now >= _nextRepairStationScanTime)
            {
                _nextRepairStationScanTime = now + 15.0f;
                _activeRepairStations.Clear();
                try
                {
                    var stations = UnityEngine.Object.FindObjectsOfType<RepairStation>();
                    if (stations != null)
                    {
                        for (int i = 0; i < stations.Length; i++)
                        {
                            RepairStation st = stations[i];
                            if (st != null && st.Pointer != IntPtr.Zero && st.gameObject != null && st.gameObject.activeInHierarchy && st.transform != null)
                            {
                                _activeRepairStations.Add(st);
                            }
                        }
                    }
                }
                catch { }
            }

            var allSquads = Squad.AllSquads;
            if (allSquads == null || allSquads.Count == 0)
                return;

            // Pass 1: Collect active confirmed offensive radio support requests
            foreach (var kvp in allSquads)
            {
                Squad sq = kvp.Value;
                if (sq == null || sq.Pointer == IntPtr.Zero)
                    continue;

                Soldier leader = sq.Leader;
                if (leader == null || !leader.IsAlive || string.IsNullOrEmpty(leader.faction))
                    continue;

                if (sq.RadioRequestActiveAndConfirmed())
                {
                    RadioRequest reqType = sq.radioRequest;
                    if (reqType == RadioRequest.tankSupport || sq.RadioRequestIsExplosiveArtillery)
                    {
                        _activeRadioRequests.Add(new ActiveRadioSupportInfo
                        {
                            Faction = leader.faction,
                            Position = sq.radioRequestPosition,
                            RequestType = reqType
                        });
                        if (_activeRadioRequests.Count >= 16)
                            break;
                    }
                }
            }

            // Pass 2: Collect KNOWN enemy squads & combat vehicles (Marked, Radio-Requested, or Actively Engaging Friendlies)
            foreach (var kvp in allSquads)
            {
                Squad sq = kvp.Value;
                if (sq == null || sq.Pointer == IntPtr.Zero || sq.units == null)
                    continue;

                var units = sq.units;
                int uCount = units.Count;
                if (uCount == 0)
                    continue;

                Soldier refSoldier = sq.Leader;
                if (refSoldier == null || !refSoldier.IsAlive)
                {
                    refSoldier = null;
                    for (int i = 0; i < uCount; i++)
                    {
                        Soldier u = units[i];
                        if (u != null && u.IsAlive)
                        {
                            refSoldier = u;
                            break;
                        }
                    }
                }

                if (refSoldier == null || refSoldier.transform == null || string.IsNullOrEmpty(refSoldier.faction))
                    continue;

                Vehicle mountedVeh = refSoldier.IsOnVehicle() ? refSoldier.GetCurrentVehicle() : null;
                if (mountedVeh != null && mountedVeh.IsAirVehicle())
                {
                    VehiclePlane vp = mountedVeh.TryCast<VehiclePlane>();
                    if (vp != null && !vp.isGrounded && vp.CanFight() && vp.transform != null && _airborneAircraft.Count < 8)
                    {
                        bool alreadyAdded = false;
                        for (int a = 0; a < _airborneAircraft.Count; a++)
                        {
                            if (_airborneAircraft[a].Plane != null && _airborneAircraft[a].Plane.Pointer == vp.Pointer)
                            {
                                alreadyAdded = true;
                                break;
                            }
                        }

                        if (!alreadyAdded)
                        {
                            _airborneAircraft.Add(new AirborneAircraftInfo
                            {
                                Faction = refSoldier.faction,
                                Plane = vp,
                                Spottable = vp.Cast<Spottable>(),
                                Position = vp.transform.position,
                                IsBomber = vp.planeType == PlaneType.Bomber
                            });
                        }
                    }
                    continue;
                }

                string sqFaction = refSoldier.faction;
                Vector3 sqPos = refSoldier.transform.position;

                bool isPinPoint = sq.isMarkedPinPoint;
                bool isMarked = sq.isMarked || isPinPoint;
                bool isHalfMarked = sq.isHalfMarked;
                bool isAware = sq.IsEnemyAwareOfThisSquad || refSoldier.IsSpotted;

                bool nearRadio = false;
                RadioRequest matchedRadioType = RadioRequest.waiting;
                for (int r = 0; r < _activeRadioRequests.Count; r++)
                {
                    ActiveRadioSupportInfo req = _activeRadioRequests[r];
                    if (req.Faction == sqFaction)
                        continue;

                    if ((req.Position - sqPos).sqrMagnitude <= 85.0f * 85.0f)
                    {
                        nearRadio = true;
                        matchedRadioType = req.RequestType;
                        break;
                    }
                }

                Soldier primaryFoot = null;
                Soldier secondaryFoot = null;
                bool anyUnitSpottedOrFiring = false;
                int aliveCount = 0;

                for (int i = 0; i < uCount; i++)
                {
                    Soldier u = units[i];
                    if (u == null || !u.IsAlive || u.transform == null)
                        continue;

                    aliveCount++;

                    if (u.IsSpotted || VisionPatches.HasFiredRecently(u, 3.5f))
                        anyUnitSpottedOrFiring = true;

                    if (!u.IsOnVehicle())
                    {
                        if (primaryFoot == null)
                        {
                            primaryFoot = u;
                        }
                        else if (secondaryFoot == null)
                        {
                            secondaryFoot = u;
                        }
                        else if (u.hasMg || u.isAtUnit || u.m_pose == SoldierPose.Idle)
                        {
                            primaryFoot = u;
                        }
                    }
                }

                if (!isMarked && !isHalfMarked && !isAware && !nearRadio && !anyUnitSpottedOrFiring)
                    continue;

                bool hasEnemyGroundVehicle = mountedVeh != null && mountedVeh.CanFight() && !mountedVeh.IsAirVehicle();

                // Tier 3 Radioman Intel Gate: unmarked enemy contacts only reach the Team Combined-Arms Net
                // if a friendly squad/vehicle/aircraft within range is actually connected to the Radio Net!
                if (!isMarked && !isHalfMarked && !nearRadio && !CanBroadcastEnemyContactToTeamNet(sq, sqPos, sqFaction, hasEnemyGroundVehicle))
                    continue;

                float priority = Mathf.Min(aliveCount * 2.5f, 15.0f);
                if (isPinPoint)
                    priority += 55.0f;
                else if (isMarked)
                    priority += 45.0f;
                else if (isHalfMarked)
                    priority += 28.0f;

                if (nearRadio)
                {
                    priority += 50.0f;
                    if (matchedRadioType == RadioRequest.tankSupport || matchedRadioType == RadioRequest.artilleryAPHE)
                        priority += 15.0f;
                }

                if (isAware)
                    priority += 24.0f;
                if (anyUnitSpottedOrFiring)
                    priority += 10.0f;

                Spottable vehSpottable = null;
                if (mountedVeh != null && mountedVeh.CanFight() && !mountedVeh.IsAirVehicle())
                {
                    vehSpottable = mountedVeh.Cast<Spottable>();
                    priority += mountedVeh.GetMainTurret(false) == null ? 12.0f : 32.0f;
                }

                Soldier firstSoldier = primaryFoot != null ? primaryFoot : refSoldier;
                Spottable firstSpottable = firstSoldier != null ? firstSoldier.Cast<Spottable>() : null;
                Spottable secondSpottable = secondaryFoot != null ? secondaryFoot.Cast<Spottable>() : null;

                _knownEnemyClusters.Add(new KnownEnemyCluster
                {
                    Faction = sqFaction,
                    Position = firstSoldier != null && firstSoldier.transform != null ? firstSoldier.transform.position : sqPos,
                    BasePriority = priority,
                    IsMarkedOrPinPoint = isMarked,
                    IsHalfMarked = isHalfMarked,
                    NearRadioRequest = nearRadio,
                    IsEngagingFriendlies = isAware || anyUnitSpottedOrFiring,
                    VehicleTarget = vehSpottable,
                    PrimarySoldier = firstSpottable,
                    SecondarySoldier = secondSpottable
                });

                if (_knownEnemyClusters.Count >= 32)
                    break;
            }
        }

        private static float EvaluateExistingTargetPriority(Spottable target, string myFaction, Vector3 originPos, bool hasHeavyGun)
        {
            if (target == null || target.Pointer == IntPtr.Zero)
                return -999f;

            Vector3 targetPos = target.GetCenterOfUnit();
            float dist = Vector3.Distance(originPos, targetPos);
            float score = -(dist * 0.12f);

            if (target.IsWheeledVehicleOrTank())
            {
                score += hasHeavyGun ? 52.0f : 24.0f;
                Vehicle v = target.TryCast<Vehicle>();
                if (v != null)
                {
                    Squad vSq = v.GetSquadInside();
                    if (vSq != null)
                    {
                        if (vSq.isMarkedPinPoint) score += 55.0f;
                        else if (vSq.isMarked) score += 45.0f;
                        else if (vSq.isHalfMarked) score += 28.0f;
                        if (vSq.IsEnemyAwareOfThisSquad) score += 20.0f;
                    }
                }
            }
            else
            {
                score += 10.0f;
                Soldier s = target.TryCast<Soldier>();
                if (s != null && s.joinedSquad != null)
                {
                    Squad sq = s.joinedSquad;
                    if (sq.isMarkedPinPoint) score += 55.0f;
                    else if (sq.isMarked) score += 45.0f;
                    else if (sq.isHalfMarked) score += 28.0f;
                    if (sq.IsEnemyAwareOfThisSquad) score += 24.0f;
                }
            }

            for (int r = 0; r < _activeRadioRequests.Count; r++)
            {
                ActiveRadioSupportInfo req = _activeRadioRequests[r];
                if (req.Faction == myFaction && (req.Position - targetPos).sqrMagnitude <= 85.0f * 85.0f)
                {
                    score += 50.0f;
                    break;
                }
            }

            return score;
        }

        private static bool CanTankShellIndoorTargetFacade(Turret turret, Vehicle veh, Spottable target)
        {
            if (turret == null || veh == null || veh.transform == null || target == null || target.Pointer == IntPtr.Zero)
                return false;

            if (target.IsWheeledVehicleOrTank() || target.IsAirVehicle())
                return false;

            if (turret.GetCaliber() < 37.0f)
                return false;

            turret.CheckBullets(out bool hasHe, out _, out _, out _);
            if (!hasHe)
                return false;

            Vector3 targetPos = target.GetCenterOfUnit();
            if (!AiCore.HasLowCeilingOverhead(targetPos, 3.8f))
                return false;

            Vector3 firePos = turret.GetFirePos();
            Vector3 toTarget = targetPos - firePos;
            float dist = toTarget.magnitude;
            if (dist < 16.0f || dist > 320.0f)
                return false;

            return !AiCore.RaycastEnvironment(firePos, toTarget / dist, dist - 5.5f, veh.transform.root, out _);
        }

        public static void TryPrioritizeKnownTargetForTurret(TurretAI turretAi, Vehicle veh)
        {
            if (turretAi == null || turretAi.turret == null || veh == null || veh.Pointer == IntPtr.Zero)
                return;

            if (!veh.CanFight() || veh.IsAirVehicle() || veh.PlayerIsInside())
                return;

            Turret turret = turretAi.turret;
            Soldier gunner = turret.GetSittedUnit(veh);
            if (gunner != null && gunner.IsPlayer())
                return;

            string myFaction = GetVehicleCrewFaction(veh, turret);
            if (string.IsNullOrEmpty(myFaction))
                return;

            Vector3 firePos = turret.GetFirePos();
            Spottable curTarget = turretAi.CurrentVisibleTarget;

            if (curTarget != null && curTarget.Pointer != IntPtr.Zero)
            {
                if (curTarget.IsAirVehicle())
                    return;

                float curDist = Vector3.Distance(firePos, curTarget.GetCenterOfUnit());
                // Never override point-blank self-defense
                if (curDist < 32.0f)
                    return;

                // Never distract from an active close/mid-range duel against an armed enemy vehicle
                if (curTarget.IsWheeledVehicleOrTank() && curDist < 130.0f)
                {
                    Vehicle curVeh = curTarget.TryCast<Vehicle>();
                    if (curVeh != null && curVeh.GetMainTurret(false) != null)
                        return;
                }
            }

            RefreshCombinedArmsIntelIfNeeded();

            turret.CheckBullets(out bool hasHe, out bool hasAp, out bool hasFire, out _);
            float caliber = turret.GetCaliber();
            bool hasHeavyGun = hasHe || hasAp || caliber >= 20.0f;
            bool canDamageLightVehicles = hasHeavyGun || hasFire || caliber >= 12.0f;

            float curScore = curTarget != null
                ? EvaluateExistingTargetPriority(curTarget, myFaction, firePos, hasHeavyGun)
                : -999f;

            float bulletSpeed = turret.GetBulletSpeed();
            float bulletGrav = turret.GetBulletGravity();

            Spottable bestCandidate = null;
            float bestScore = curScore + 18.0f;
            float bestAngleAdj = 0f;

            int clusterCount = _knownEnemyClusters.Count;
            for (int i = 0; i < clusterCount; i++)
            {
                KnownEnemyCluster cluster = _knownEnemyClusters[i];
                if (cluster.Faction == myFaction)
                    continue;

                for (int pass = 0; pass < 3; pass++)
                {
                    Spottable cand = null;
                    bool isVehCand = false;
                    if (pass == 0)
                    {
                        if (!canDamageLightVehicles || cluster.VehicleTarget == null)
                            continue;
                        cand = cluster.VehicleTarget;
                        isVehCand = true;
                    }
                    else if (pass == 1)
                    {
                        cand = cluster.PrimarySoldier;
                    }
                    else
                    {
                        cand = cluster.SecondarySoldier;
                    }

                    if (cand == null || cand.Pointer == IntPtr.Zero)
                        continue;

                    Vector3 candPos = cand.GetCenterOfUnit();
                    Vector3 toCand = candPos - firePos;
                    float dist = toCand.magnitude;
                    float maxDist = isVehCand ? 550.0f : 320.0f;
                    if (dist < 8.0f || dist > maxDist)
                        continue;

                    float candScore = cluster.BasePriority + (isVehCand && hasHeavyGun ? 35.0f : 0f) - (dist * 0.12f);
                    if (candScore <= bestScore)
                        continue;

                    if (!turret.CanShootInDirection(toCand / dist, 1.0f))
                        continue;

                    float angleAdj = Corvostudio.Weapons.BulletInstance.CalculateTrajectory(dist, bulletSpeed, bulletGrav, out bool canReach);
                    if (!canReach)
                        continue;

                    bool canDirectlySee = turretAi.CanSee(cand);
                    if (!canDirectlySee)
                    {
                        // Building Demolition / Strongpoint Shelling:
                        // If a tank or field gun has High-Explosive shells (hasHe && caliber >= 37mm) and a known/spotted
                        // enemy soldier is hiding inside a building (behind a closed shutter or interior wall),
                        // allow the tank to shell the building's exterior facade if the line of fire from the barrel
                        // up to the building's outer wall (dist - 5.5m) is unobstructed!
                        bool canShellBuildingFacade =
                            !isVehCand &&
                            hasHe &&
                            caliber >= 37.0f &&
                            dist >= 16.0f &&
                            (cluster.IsMarkedOrPinPoint || cluster.IsHalfMarked || cluster.NearRadioRequest || cluster.IsEngagingFriendlies) &&
                            AiCore.HasLowCeilingOverhead(candPos, 3.8f) &&
                            !AiCore.RaycastEnvironment(firePos, toCand / dist, dist - 5.5f, veh.transform.root, out _);

                        if (!canShellBuildingFacade)
                            continue;

                        if (VisionConfig.DebugLogging.Value)
                        {
                            AiCore.LogDebug(
                                "BuildingHE",
                                $"{myFaction} {caliber:F0}mm HE turret targeting building facade to flush indoor enemy ({dist:F0}m).",
                                5.0f);
                        }
                    }

                    bestScore = candScore;
                    bestCandidate = cand;
                    bestAngleAdj = angleAdj;
                    break; // Found a valid visible target in this cluster
                }
            }

            if (bestCandidate != null)
            {
                if (curTarget == null || bestCandidate.Pointer != curTarget.Pointer)
                {
                    turretAi.CurrentVisibleTarget = bestCandidate;
                    turretAi.supportedSquad = null;
                    turretAi.targetAngleAdjust = bestAngleAdj;
                    turretAi.targetInWeaponRange = true;
                    turret.SelectBestAmmo(bestCandidate);
                    RecordCombinedArmsAssist(
                        veh.Pointer,
                        Time.time,
                        "CombArmsTurret",
                        $"{myFaction} turret prioritized known/marked enemy squad target (score:{bestScore:F0}).");
                }
                return;
            }

            // Fix vanilla RefreshTargets quirk (0x180832b3e): allow on-map HE artillery/howitzers/mortars and tanks
            // to answer friendly Team Net radio support requests both when idle (curTarget == null) and when only
            // a low-priority unmarked lone infantryman was spotted far away.
            if (hasHe && (curTarget == null || curScore < 26.0f) && !turretAi.HasRadioSupportRequest)
            {
                turretAi.FindBestArtilleryRequest();
                if (turretAi.HasRadioSupportRequest)
                {
                    Vector3 radioPos = turretAi.RadioRequestTargetPosition;
                    float rDist = Vector3.Distance(turret.transform.position, radioPos);
                    float rAngle = Corvostudio.Weapons.BulletInstance.CalculateTrajectory(rDist, bulletSpeed, bulletGrav, out bool rCanReach);
                    if (rCanReach)
                    {
                        turretAi.CurrentVisibleTarget = null;
                        turretAi.targetAngleAdjust = rAngle;
                        turretAi.targetInWeaponRange = true;
                        if (turretAi.supportedSquad != null)
                        {
                            turret.SelectBestAmmo(turretAi.supportedSquad.radioRequest);
                        }
                        RecordCombinedArmsAssist(
                            veh.Pointer,
                            Time.time,
                            "CombArmsRadio",
                            $"{myFaction} HE turret answering friendly radio support request ({rDist:F0}m).");
                    }
                    else
                    {
                        turretAi.supportedSquad = null;
                    }
                }
            }
        }

        // =====================================================================
        // Emplacement & Vehicle Turret Friendly Fire Prevention (TurretAI.AutoPilot)
        // =====================================================================
        internal static bool ShouldBlockTurretFriendlyFire(TurretAI turretAi, Turret turret, Soldier gunner)
        {
            if (turretAi == null || turret == null || turret.Pointer == IntPtr.Zero)
                return false;

            Vehicle rootVeh = AiCore.GetCachedTurretVehicle(turret);
            if (rootVeh != null && !AiCore.IsLocallyOwnedVehicle(rootVeh))
                return false;

            IntPtr turretPtr = turret.Pointer;
            float now = Time.time;

            if (_turretFFBlockedUntil.TryGetValue(turretPtr, out float blockedUntil) && now < blockedUntil)
                return true;

            if (_nextTurretFFCheckTime.TryGetValue(turretPtr, out float nextCheck) && now < nextCheck)
                return false;

            _nextTurretFFCheckTime[turretPtr] = now + 0.10f;

            string myFaction = (gunner != null && !string.IsNullOrEmpty(gunner.faction))
                ? gunner.faction
                : GetVehicleCrewFaction(rootVeh, turret);
            if (string.IsNullOrEmpty(myFaction))
                return false;

            Vector3 firePos = turret.GetFirePos();
            Vector3 fireDir = turret.GetFireDir();
            if (fireDir.sqrMagnitude < 0.001f)
                return false;
            fireDir.Normalize();

            bool hasTargetPos = false;
            Vector3 targetPos = Vector3.zero;
            if (turretAi.CurrentVisibleTarget != null && turretAi.CurrentVisibleTarget.Pointer != IntPtr.Zero)
            {
                targetPos = turretAi.CurrentVisibleTarget.GetCenterOfUnit();
                hasTargetPos = true;
            }
            else if (turretAi.HasRadioSupportRequest)
            {
                targetPos = turretAi.RadioRequestTargetPosition;
                hasTargetPos = true;
            }

            float targetDist = hasTargetPos ? Vector3.Distance(firePos, targetPos) : 160.0f;
            float maxCheckDist = Mathf.Clamp(targetDist + 5.0f, 12.0f, 220.0f);
            float baseConeDeg = VisionConfig.FriendlyFireConeDegrees.Value;
            float caliber = turret.GetCaliber();
            bool isArtillery = turret.IsArtillery();
            bool isIndirect = turret.IsUpIndirectFire();
            bool isExplosive = caliber >= 20.0f || isArtillery;

            float dangerCloseBlastRadius = 0f;
            if (isArtillery || caliber >= 70.0f)
                dangerCloseBlastRadius = 18.0f;
            else if (caliber >= 37.0f)
                dangerCloseBlastRadius = 11.0f;
            else if (caliber >= 20.0f)
                dangerCloseBlastRadius = 6.0f;

            // 1. Prevent direct-fire cannons/AT guns from detonating HE shells on a low wall or sandbag right in front of the muzzle
            if (isExplosive && !isIndirect && VisionConfig.EnableWallShootingPrevention.Value)
            {
                Transform ignoreRoot = (rootVeh != null && rootVeh.transform != null)
                    ? rootVeh.transform.root
                    : (turret.transform != null ? turret.transform.root : null);
                if (ignoreRoot != null && AiCore.IsDirectionBlockedByEnvironment(firePos, fireDir, 5.2f, ignoreRoot))
                {
                    _turretFFBlockedUntil[turretPtr] = now + 0.30f;
                    WallsBlocked++;
                    return true;
                }
            }

            IntPtr gunnerPtr = gunner != null ? gunner.Pointer : IntPtr.Zero;
            IntPtr rootVehPtr = rootVeh != null ? rootVeh.Pointer : IntPtr.Zero;

            // 2. Always check the Human Player first in O(1) so friendly emplacements/turrets never shoot through the player!
            Soldier player = Soldier.CurrentControlledSoldierOrNull();
            if (player != null &&
                !string.IsNullOrEmpty(player.faction) &&
                ResourcesManager.IsSameFaction(myFaction, player.faction))
            {
                if (IsFriendlySoldierInTurretFireDanger(
                    player, gunnerPtr, rootVehPtr, firePos, fireDir, maxCheckDist,
                    baseConeDeg, isIndirect, isExplosive, hasTargetPos, targetPos, dangerCloseBlastRadius))
                {
                    _turretFFBlockedUntil[turretPtr] = now + 0.30f;
                    FriendlyFirePrevented++;
                    return true;
                }
            }

            // 3. Check all friendly squads in the firing corridor or explosive impact zone
            var allSquads = Squad.AllSquads;
            if (allSquads != null)
            {
                foreach (var kvp in allSquads)
                {
                    Squad sq = kvp.Value;
                    if (sq == null || sq.units == null)
                        continue;

                    Soldier leader = sq.Leader;
                    if (leader == null || string.IsNullOrEmpty(leader.faction) || !ResourcesManager.IsSameFaction(myFaction, leader.faction))
                        continue;

                    if (leader.transform != null)
                    {
                        Vector3 lPos = leader.transform.position;
                        bool nearLine = (lPos - firePos).sqrMagnitude <= (maxCheckDist + 45f) * (maxCheckDist + 45f);
                        bool nearBlast = hasTargetPos && dangerCloseBlastRadius > 0f &&
                                         (lPos - targetPos).sqrMagnitude <= (dangerCloseBlastRadius + 45f) * (dangerCloseBlastRadius + 45f);
                        if (!nearLine && !nearBlast)
                            continue;
                    }

                    var units = sq.units;
                    int uCount = units.Count;
                    for (int i = 0; i < uCount; i++)
                    {
                        if (IsFriendlySoldierInTurretFireDanger(
                            units[i], gunnerPtr, rootVehPtr, firePos, fireDir, maxCheckDist,
                            baseConeDeg, isIndirect, isExplosive, hasTargetPos, targetPos, dangerCloseBlastRadius))
                        {
                            _turretFFBlockedUntil[turretPtr] = now + 0.30f;
                            FriendlyFirePrevented++;
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        private static bool IsFriendlySoldierInTurretFireDanger(
            Soldier friend,
            IntPtr gunnerPtr,
            IntPtr rootVehPtr,
            Vector3 firePos,
            Vector3 fireDir,
            float maxCheckDist,
            float baseConeDeg,
            bool isIndirect,
            bool isExplosive,
            bool hasTargetPos,
            Vector3 targetPos,
            float dangerCloseBlastRadius)
        {
            if (friend == null || friend.Pointer == IntPtr.Zero || !friend.IsAlive || friend.Pointer == gunnerPtr || friend.transform == null)
                return false;

            if (rootVehPtr != IntPtr.Zero && friend.IsOnVehicle())
            {
                Vehicle friendVeh = friend.GetCurrentVehicle();
                if (friendVeh != null && friendVeh.Pointer == rootVehPtr)
                    return false;
            }

            bool isPlayer = friend.IsPlayer();
            Vector3 chestPos = friend.GetCenterOfUnit();
            Vector3 feetPos = friend.transform.position + Vector3.up * 0.45f;

            // A. Danger-Close Explosive Blast Radius around the target impact point
            if (hasTargetPos && dangerCloseBlastRadius > 0f)
            {
                float blastR = isPlayer ? (dangerCloseBlastRadius + 3.5f) : dangerCloseBlastRadius;
                if ((chestPos - targetPos).sqrMagnitude <= blastR * blastR)
                    return true;
            }

            Vector3 toFriend = chestPos - firePos;
            float distSq = toFriend.sqrMagnitude;
            if (distSq < 0.04f)
                return false;

            float dist = Mathf.Sqrt(distSq);

            // B. Point-Blank Muzzle Danger Zone (friendly standing right in front of the barrel)
            if (dist <= 3.5f && Vector3.Angle(fireDir, toFriend) <= 45.0f)
                return true;

            // C. Direct Line-of-Fire Corridor & Cone
            if (!isIndirect && dist <= maxCheckDist)
            {
                float along = Vector3.Dot(toFriend, fireDir);
                if (along > 0.4f && along <= maxCheckDist)
                {
                    Vector3 rayPt = firePos + fireDir * along;
                    float perpChest = Vector3.Distance(rayPt, chestPos);
                    float perpFeet = Vector3.Distance(rayPt, feetPos);
                    float minPerp = Mathf.Min(perpChest, perpFeet);

                    // Also account for horizontal (XZ) sweep when vertical separation is within 2.4m
                    if (Mathf.Abs(rayPt.y - chestPos.y) <= 2.4f)
                    {
                        Vector2 flatRay = new Vector2(rayPt.x - chestPos.x, rayPt.z - chestPos.z);
                        minPerp = Mathf.Min(minPerp, flatRay.magnitude);
                    }

                    float corridorRadius = isPlayer
                        ? (isExplosive ? 3.6f : 2.6f)
                        : (isExplosive ? 2.8f : 1.85f);
                    float coneDeg = isPlayer
                        ? Mathf.Max(baseConeDeg, 8.5f)
                        : Mathf.Max(baseConeDeg, 6.0f);

                    if (minPerp <= corridorRadius || Vector3.Angle(fireDir, toFriend) <= coneDeg)
                        return true;
                }
            }

            return false;
        }

        private static bool TryRouteVehicleToRepairStation(AIVehicle aiVeh, Vehicle veh, float now)
        {
            if (aiVeh == null || veh == null || veh.Pointer == IntPtr.Zero || veh.transform == null)
                return false;

            if (!AiCore.IsLocallyOwnedVehicle(veh) ||
                veh.IsDisabled() ||
                veh.IsAirVehicle() ||
                veh.PlayerIsInside() ||
                veh.PlayerIsDriving() ||
                !veh.HasDriver())
            {
                _vehiclesSeekingRepairStation.Remove(veh.Pointer);
                return false;
            }

            MovableVehicle mov = veh.TryCast<MovableVehicle>();
            if (mov == null)
                return false;

            // Cannot drive to a repair station if tracks or engine are 100% broken (crew must field-repair first)
            float tracksDmg = mov.GetPercentageDamageFor(VehicleInternalPart.tracks);
            float engineDmg = mov.GetPercentageDamageFor(VehicleInternalPart.engine);
            if (tracksDmg >= 0.98f || engineDmg >= 0.98f)
            {
                _vehiclesSeekingRepairStation.Remove(veh.Pointer);
                return false;
            }

            RefreshCombinedArmsIntelIfNeeded();
            if (_activeRepairStations.Count == 0)
            {
                _vehiclesSeekingRepairStation.Remove(veh.Pointer);
                return false;
            }

            float hullDmg = mov.GetPercentageDamageFor(VehicleInternalPart.hull);
            float fuelDmg = mov.GetPercentageDamageFor(VehicleInternalPart.fuelTank);
            float maxPartDamage = Mathf.Max(Mathf.Max(hullDmg, fuelDmg), Mathf.Max(engineDmg, tracksDmg));

            Turret mainTurret = veh.GetMainTurret(false);
            bool outOfTurretAmmo = false;
            if (mainTurret != null && mainTurret.WeaponsCount() > 0)
            {
                mainTurret.CheckBullets(out bool hasHe, out bool hasAp, out bool hasFire, out bool hasFlak);
                outOfTurretAmmo = !hasHe && !hasAp && !hasFire && !hasFlak;
            }

            IntPtr vehPtr = veh.Pointer;
            bool alreadySeeking = _vehiclesSeekingRepairStation.TryGetValue(vehPtr, out RepairStation activeStation) &&
                                  activeStation != null && activeStation.Pointer != IntPtr.Zero &&
                                  activeStation.gameObject != null && activeStation.gameObject.activeInHierarchy &&
                                  activeStation.transform != null;

            bool needsStation = alreadySeeking
                ? (outOfTurretAmmo || maxPartDamage > 0.02f || veh.CanRefillAmmmo())
                : (outOfTurretAmmo || maxPartDamage >= 0.35f);

            if (!needsStation)
            {
                if (alreadySeeking)
                {
                    _vehiclesSeekingRepairStation.Remove(vehPtr);
                    Squad sq = aiVeh.squadInside;
                    if (sq != null && !sq.IsPlayerInSquad() && sq.Leader != null)
                    {
                        sq.Leader_OrderAttackCurrentTask(sq.Leader);
                    }
                    aiVeh.rerollDestination = true;
                    if (VisionConfig.DebugLogging.Value)
                    {
                        AiCore.LogDebug("RepairStation", $"Vehicle finished repairing/re-arming at RepairStation and is returning to battle.");
                    }
                }
                return false;
            }

            Vector3 vehPos = veh.transform.position;
            string myFaction = GetVehicleCrewFaction(veh, mainTurret);
            RepairStation bestStation = alreadySeeking ? activeStation : null;

            if (bestStation == null)
            {
                float bestDistSq = 650.0f * 650.0f;
                for (int i = 0; i < _activeRepairStations.Count; i++)
                {
                    RepairStation st = _activeRepairStations[i];
                    if (st == null || st.Pointer == IntPtr.Zero || st.gameObject == null || !st.gameObject.activeInHierarchy || st.transform == null)
                        continue;

                    Vector3 stPos = st.transform.position;
                    float dSq = (stPos - vehPos).sqrMagnitude;
                    if (dSq >= bestDistSq)
                        continue;

                    bool overrun = false;
                    if (!string.IsNullOrEmpty(myFaction))
                    {
                        for (int c = 0; c < _knownEnemyClusters.Count; c++)
                        {
                            if (_knownEnemyClusters[c].Faction != myFaction &&
                                (_knownEnemyClusters[c].Position - stPos).sqrMagnitude <= 40.0f * 40.0f)
                            {
                                overrun = true;
                                break;
                            }
                        }
                    }
                    if (overrun)
                        continue;

                    bestDistSq = dSq;
                    bestStation = st;
                }
            }

            if (bestStation == null)
                return false;

            _vehiclesSeekingRepairStation[vehPtr] = bestStation;
            Vector3 stationPos = bestStation.transform.position;
            float stationRadius = Mathf.Max(5.0f, bestStation.radius);
            float distToStation = Vector3.Distance(vehPos, stationPos);

            if (distToStation <= stationRadius * 0.85f)
            {
                // Parked inside the RepairStation circle: hold brake while RepairStation.Update repairs parts & refills turrets!
                float holdUntil = now + 1.0f;
                if (aiVeh.resumeDriveTime < holdUntil)
                    aiVeh.resumeDriveTime = holdUntil;
                return true;
            }

            Squad crewSq = aiVeh.squadInside;
            if (crewSq != null && !crewSq.IsPlayerInSquad())
            {
                if (crewSq.order == Order.follow)
                    crewSq.order = Order.defend;
                crewSq.moveOrderPosition = stationPos;
                crewSq.moveOrderRadius = Mathf.Max(4.0f, stationRadius * 0.65f);
            }

            if ((aiVeh.setVehicleDestination - stationPos).sqrMagnitude > 4.0f * 4.0f)
            {
                aiVeh.setVehicleDestination = stationPos;
                aiVeh.rerollDestination = false;
                RecordCombinedArmsAssist(
                    vehPtr,
                    now,
                    "RepairStation",
                    $"{myFaction ?? "AI"} vehicle routing to RepairStation ({distToStation:F0}m away, dmg:{maxPartDamage * 100f:F0}%, dryAmmo:{outOfTurretAmmo}).");
            }

            return true;
        }

        private static void TrySteerVehicleTowardKnownThreat(AIVehicle aiVeh, Vehicle veh, float now)
        {
            if (aiVeh == null || veh == null || veh.Pointer == IntPtr.Zero || veh.transform == null)
                return;

            if (TryRouteVehicleToRepairStation(aiVeh, veh, now))
                return;

            if (!AiCore.IsLocallyOwnedVehicle(veh) ||
                !veh.CanFight() ||
                veh.IsAirVehicle() ||
                veh.PlayerIsInside() ||
                veh.PlayerIsDriving())
            {
                return;
            }

            Turret mainTurret = veh.GetMainTurret(false);
            if (mainTurret == null)
                return;

            // If the main turret already has a visible target in weapon range & LOS, let Standoff / TurretAI handle it
            TurretAI turretAi = mainTurret.GetTurretAI();
            if (turretAi != null &&
                turretAi.CurrentVisibleTarget != null &&
                turretAi.targetInWeaponRange &&
                (turretAi.CanSee(turretAi.CurrentVisibleTarget) ||
                 CanTankShellIndoorTargetFacade(mainTurret, veh, turretAi.CurrentVisibleTarget)))
            {
                return;
            }

            string myFaction = GetVehicleCrewFaction(veh, mainTurret);
            if (string.IsNullOrEmpty(myFaction))
                return;

            Vector3 squadDest = aiVeh.GetCurrentSquadDestination();
            float areaSize = aiVeh.GetCurrentSquadDestinationAreaSize();
            if (areaSize < 12.0f)
                return;

            RefreshCombinedArmsIntelIfNeeded();

            Vector3 vehPos = veh.transform.position;
            Vector3 bestFocusPos = Vector3.zero;
            float bestFocusScore = -999f;
            bool hasFocus = false;

            // 1. Check active friendly Tank Support / Artillery radio requests near the objective or vehicle
            for (int r = 0; r < _activeRadioRequests.Count; r++)
            {
                ActiveRadioSupportInfo req = _activeRadioRequests[r];
                if (req.Faction != myFaction)
                    continue;

                float distToDest = Vector3.Distance(squadDest, req.Position);
                if (distToDest > 320.0f)
                    continue;

                float score = (req.RequestType == RadioRequest.tankSupport ? 95.0f : 70.0f) - (distToDest * 0.15f);
                if (score > bestFocusScore)
                {
                    bestFocusScore = score;
                    bestFocusPos = req.Position;
                    hasFocus = true;
                }
            }

            // 2. Check known/marked enemy squads contesting or near the vehicle's objective sector
            int clusterCount = _knownEnemyClusters.Count;
            for (int i = 0; i < clusterCount; i++)
            {
                KnownEnemyCluster cluster = _knownEnemyClusters[i];
                if (cluster.Faction == myFaction)
                    continue;

                float distToDest = Vector3.Distance(squadDest, cluster.Position);
                if (distToDest > 320.0f)
                    continue;

                float score = cluster.BasePriority - (distToDest * 0.15f);
                if (score > bestFocusScore)
                {
                    bestFocusScore = score;
                    bestFocusPos = cluster.Position;
                    hasFocus = true;
                }
            }

            if (!hasFocus)
                return;

            // Compute a tactical support position inside the squad's allowed destination radius
            // so AIVehicle.DestinationHasChanged stays false and UpdatePath skips RandomPointInCircleXZ.
            Vector3 toFocusFromDest = bestFocusPos - squadDest;
            toFocusFromDest.y = 0f;
            float focusDistFromDest = toFocusFromDest.magnitude;
            float maxAllowedOffset = areaSize * 0.82f;

            Vector3 desiredDest;
            if (focusDistFromDest <= maxAllowedOffset)
            {
                // Threat is inside the objective area: pull back ~35m toward the approaching tank for standoff
                Vector3 fromFocusToTank = vehPos - bestFocusPos;
                fromFocusToTank.y = 0f;
                Vector3 standoffDir = fromFocusToTank.sqrMagnitude > 0.25f ? fromFocusToTank.normalized : -veh.transform.forward;
                desiredDest = bestFocusPos + standoffDir * Mathf.Min(35.0f, maxAllowedOffset * 0.65f);

                Vector3 offsetFromDest = desiredDest - squadDest;
                offsetFromDest.y = 0f;
                if (offsetFromDest.magnitude > maxAllowedOffset)
                {
                    desiredDest = squadDest + offsetFromDest.normalized * maxAllowedOffset;
                }
            }
            else
            {
                // Threat is outside the objective circle: advance to the perimeter facing the known threat
                desiredDest = squadDest + (toFocusFromDest / focusDistFromDest) * maxAllowedOffset;
            }

            desiredDest.y = squadDest.y;

            if ((aiVeh.setVehicleDestination - desiredDest).sqrMagnitude > 12.0f * 12.0f)
            {
                aiVeh.setVehicleDestination = desiredDest;
                aiVeh.rerollDestination = false;
                RecordCombinedArmsAssist(
                    veh.Pointer,
                    now,
                    "CombArmsTank",
                    $"{myFaction} combat vehicle steering toward known enemy squad / support request.");
            }
        }

        private static Spottable FindBestAirborneEnemyAircraft(AIPlane plane, string myFaction, Vector3 planePos, out bool anyEnemyAirborne)
        {
            anyEnemyAirborne = false;
            Spottable bestTarget = null;
            float bestScore = -99999f;

            IntPtr selfVehPtr = plane != null && plane.veh != null ? plane.veh.Pointer : IntPtr.Zero;
            int count = _airborneAircraft.Count;
            for (int i = 0; i < count; i++)
            {
                AirborneAircraftInfo info = _airborneAircraft[i];
                if (info.Faction == myFaction ||
                    info.Plane == null ||
                    info.Plane.Pointer == IntPtr.Zero ||
                    info.Plane.Pointer == selfVehPtr ||
                    info.Spottable == null ||
                    info.Spottable.Pointer == IntPtr.Zero)
                {
                    continue;
                }

                if (info.Plane.isGrounded || !info.Plane.CanFight())
                    continue;

                Vector3 targetPos = info.Plane.transform != null ? info.Plane.transform.position : info.Position;
                float dist = Vector3.Distance(planePos, targetPos);
                // Vanilla GetBestAircraftVisibleEnemy (0x180571E56) intended 1700m for fighters and 4000m for bombers,
                // but accidentally truncated its PointOctree query to 850m. Restore full air-superiority detection range:
                float maxAirDist = info.IsBomber ? 4000.0f : 2200.0f;
                if (dist > maxAirDist)
                    continue;

                anyEnemyAirborne = true;
                // Prioritize enemy fighters over bombers so fighters clear threats before hunting bombers
                float score = (info.IsBomber ? 100.0f : 350.0f) - (dist * 0.25f);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestTarget = info.Spottable;
                }
            }

            return bestTarget;
        }

        public static void TrySteerAircraftOrbitTowardKnownThreat(AIPlane plane)
        {
            if (plane == null || plane.veh == null || plane.veh.Pointer == IntPtr.Zero || plane.transform == null)
                return;

            if (!AiCore.IsLocallyOwnedVehicle(plane.veh) || plane.veh.PlayerIsInside())
                return;

            IntPtr vehPtr = plane.veh.Pointer;
            float now = Time.time;
            if (_nextPlaneOrbitSteerTime.TryGetValue(vehPtr, out float nextSteer) && now < nextSteer)
                return;

            _nextPlaneOrbitSteerTime[vehPtr] = now + 2.5f;

            string myFaction = GetVehicleCrewFaction(plane.veh);
            if (string.IsNullOrEmpty(myFaction))
                return;

            RefreshCombinedArmsIntelIfNeeded();

            // Air Superiority First ("Clear Skies Only" Ground Support):
            // If an enemy aircraft is airborne in the battle airspace, never steer patrol orbits toward ground targets.
            // Furthermore, if our aircraft has forward pilot gun ammo and is either patrolling or locked into a ground
            // dive pass (preparingDive / doingDive), immediately break off the ground pass and engage the enemy plane!
            Spottable bestEnemyAir = FindBestAirborneEnemyAircraft(plane, myFaction, plane.transform.position, out bool anyEnemyAirborne);
            if (anyEnemyAirborne)
            {
                if (bestEnemyAir != null && plane.PilotGunHasAnyAmmo())
                {
                    AIPlane.PlaneAIState state = plane.planeState;
                    Vector3 curPlanePos = plane.transform.position;
                    Vector3 flyTarget = plane.flyTargetPosition;
                    bool canSafelyBreakOff =
                        state == AIPlane.PlaneAIState.flyingAroundArea ||
                        state == AIPlane.PlaneAIState.preparingDive ||
                        (state == AIPlane.PlaneAIState.doingDive &&
                         curPlanePos.y > flyTarget.y + 145.0f &&
                         (curPlanePos - flyTarget).sqrMagnitude > 220.0f * 220.0f);

                    Spottable curAtk = plane.targetToAttack;
                    bool alreadyTargetingAir = curAtk != null && curAtk.Pointer != IntPtr.Zero && curAtk.IsAirVehicle();

                    if (canSafelyBreakOff && !alreadyTargetingAir)
                    {
                        plane.DoAttackTarget(bestEnemyAir);
                        RecordCombinedArmsAssist(
                            vehPtr,
                            now,
                            "CombArmsAirSup",
                            $"{myFaction} aircraft aborted ground pass/patrol to intercept airborne enemy aircraft.");
                    }
                }
                return;
            }

            if (plane.planeState != AIPlane.PlaneAIState.flyingAroundArea)
                return;

            Vector3 bestOrbitPos = Vector3.zero;
            float bestScore = -999f;
            bool foundSupportZone = false;

            // 1. Highest priority: friendly Explosive Artillery / Support radio requests
            for (int r = 0; r < _activeRadioRequests.Count; r++)
            {
                ActiveRadioSupportInfo req = _activeRadioRequests[r];
                if (req.Faction != myFaction)
                    continue;

                float score = req.RequestType == RadioRequest.artilleryAPHE ? 115.0f : 95.0f;
                if (score > bestScore)
                {
                    bestScore = score;
                    bestOrbitPos = req.Position;
                    foundSupportZone = true;
                }
            }

            // 2. Next priority: Marked/Known enemy vehicles and squads actively engaging friendly infantry
            int clusterCount = _knownEnemyClusters.Count;
            for (int i = 0; i < clusterCount; i++)
            {
                KnownEnemyCluster cluster = _knownEnemyClusters[i];
                if (cluster.Faction == myFaction)
                    continue;

                if (!cluster.IsMarkedOrPinPoint &&
                    !cluster.IsHalfMarked &&
                    !cluster.NearRadioRequest &&
                    cluster.VehicleTarget == null &&
                    !cluster.IsEngagingFriendlies)
                {
                    continue;
                }

                if (cluster.BasePriority > bestScore)
                {
                    bestScore = cluster.BasePriority;
                    bestOrbitPos = cluster.Position;
                    foundSupportZone = true;
                }
            }

            if (foundSupportZone)
            {
                Vector3 delta = bestOrbitPos - plane.flyTargetPosition;
                delta.y = 0f;
                if (delta.sqrMagnitude > 50.0f * 50.0f)
                {
                    plane.SetFlyAroundPos(bestOrbitPos);
                    RecordCombinedArmsAssist(
                        vehPtr,
                        now,
                        "CombArmsPlaneOrbit",
                        $"{myFaction} aircraft shifted patrol orbit toward known enemy squad / support request.");
                }
            }
        }

        public static void TrySelectKnownAircraftTarget(AIPlane plane, ref Spottable currentResult)
        {
            if (plane == null || plane.veh == null || plane.transform == null)
                return;

            // Air-to-air dogfights always retain top priority
            if (currentResult != null && currentResult.Pointer != IntPtr.Zero && currentResult.IsAirVehicle())
                return;

            string myFaction = GetVehicleCrewFaction(plane.veh);
            if (string.IsNullOrEmpty(myFaction))
                return;

            RefreshCombinedArmsIntelIfNeeded();

            Vector3 planePos = plane.transform.position;

            // Air Superiority Gate ("Clear Skies Only" Ground Support):
            // Fix vanilla's 850m PointOctree radius cap in GetBestAircraftVisibleEnemy (0x180571BC6) and enforce
            // strict air superiority: if any enemy aircraft is airborne in the battle airspace, never assign
            // ground CAS targets, and direct armed fighters/attack planes to dogfight the enemy aircraft first.
            Spottable bestEnemyAir = FindBestAirborneEnemyAircraft(plane, myFaction, planePos, out bool anyEnemyAirborne);
            if (anyEnemyAirborne)
            {
                if (bestEnemyAir != null && plane.PilotGunHasAnyAmmo())
                {
                    currentResult = bestEnemyAir;
                    RecordCombinedArmsAssist(
                        plane.veh.Pointer,
                        Time.time,
                        "CombArmsAirSup",
                        $"{myFaction} aircraft detected enemy plane in airspace; prioritizing dogfight over ground attack.");
                }
                else if (!IsJapaneseKamikazeActive(plane))
                {
                    // Never initiate ground attacks while enemy aircraft contest the airspace (unless executing a final Kamikaze dive)
                    currentResult = null;
                }
                return;
            }

            if (_knownEnemyClusters.Count == 0)
                return;
            Vector3 planeFwd = plane.transform.forward;
            Transform planeRoot = plane.transform.root;

            const float effectiveMaxDist = 850.0f;
            const float effectiveMaxAngle = 55.0f;

            float curScore = currentResult != null
                ? EvaluateExistingTargetPriority(currentResult, myFaction, planePos, true)
                : -999f;

            Spottable bestCandidate = null;
            float bestScore = curScore + 15.0f;

            bool limitUnmarkedInfantry = VisionConfig.LimitAircraftInfantrySpotting.Value;
            float unmarkedAwareCap = Mathf.Max(VisionConfig.AircraftUnmarkedInfantryMaxDist.Value * 1.45f, 250.0f);
            bool hasBombs = plane.HasBombsToDeliver() || plane.HasBombBayConfigured();

            int clusterCount = _knownEnemyClusters.Count;
            for (int i = 0; i < clusterCount; i++)
            {
                KnownEnemyCluster cluster = _knownEnemyClusters[i];
                if (cluster.Faction == myFaction)
                    continue;

                bool isMarkedOrRadio = cluster.IsMarkedOrPinPoint || cluster.IsHalfMarked || cluster.NearRadioRequest;
                for (int pass = 0; pass < 2; pass++)
                {
                    Spottable cand = pass == 0 ? cluster.VehicleTarget : cluster.PrimarySoldier;
                    if (cand == null || cand.Pointer == IntPtr.Zero)
                        continue;

                    bool isVehCand = pass == 0;
                    if (!isVehCand && limitUnmarkedInfantry && !isMarkedOrRadio && !cluster.IsEngagingFriendlies)
                        continue;

                    float allowedDist = (!isVehCand && limitUnmarkedInfantry && !isMarkedOrRadio)
                        ? Mathf.Min(effectiveMaxDist, unmarkedAwareCap)
                        : effectiveMaxDist;

                    Vector3 candPos = cand.GetCenterOfUnit();
                    Vector3 toCand = candPos - planePos;
                    float dist = toCand.magnitude;
                    if (dist < 95.0f || dist > allowedDist)
                        continue;

                    Vector3 dirToCand = toCand / dist;
                    if (Vector3.Angle(planeFwd, dirToCand) > effectiveMaxAngle)
                        continue;

                    float vehBonus = 0f;
                    if (isVehCand)
                    {
                        bool isArmoredTank = cand.TryCast<VehicleTank>() != null;
                        vehBonus = (isArmoredTank && !hasBombs) ? 5.0f : 30.0f;
                    }

                    float candScore = cluster.BasePriority + vehBonus - (dist * 0.06f);
                    if (candScore <= bestScore)
                        continue;

                    // Verify unobstructed air-to-ground line of sight
                    if (AiCore.RaycastEnvironment(planePos, dirToCand, dist - 4.0f, planeRoot, out _))
                        continue;

                    bestScore = candScore;
                    bestCandidate = cand;
                    break;
                }
            }

            if (bestCandidate != null && (currentResult == null || bestCandidate.Pointer != currentResult.Pointer))
            {
                currentResult = bestCandidate;
                RecordCombinedArmsAssist(
                    plane.veh.Pointer,
                    Time.time,
                    "CombArmsCAS",
                    $"{myFaction} aircraft prioritized known/marked ground target for CAS run.");
            }
        }

        // =====================================================================
        // Easter Egg: Imperial Japanese Kamikaze (Tokkōtai) Aircraft Behavior
        // =====================================================================
        public static bool IsSpottableTargetValid(Spottable target)
        {
            if (target == null || target.Pointer == IntPtr.Zero)
                return false;

            Creature c = target.TryCast<Creature>();
            if (c != null)
                return c.IsAlive;

            Vehicle v = target.TryCast<Vehicle>();
            if (v != null)
                return v.CanFight();

            return true;
        }

        public static bool IsJapaneseKamikazeActive(AIPlane plane)
        {
            return plane != null &&
                   plane.veh != null &&
                   plane.veh.Pointer != IntPtr.Zero &&
                   !plane.veh.isGrounded &&
                   _activeKamikazePlanes.Contains(plane.veh.Pointer);
        }

        private static bool IsJapaneseKamikazeCandidate(AIPlane plane, out string myFaction)
        {
            myFaction = null;
            if (!VisionConfig.EnableMod.Value ||
                !VisionConfig.EnableFactionNetDoctrines.Value ||
                plane == null ||
                plane.veh == null ||
                plane.veh.Pointer == IntPtr.Zero ||
                plane.transform == null)
            {
                return false;
            }

            if (plane.veh.isGrounded ||
                !plane.veh.CanFight() ||
                !plane.HasAIDriver ||
                plane.veh.PlayerIsInside() ||
                !AiCore.IsLocallyOwnedVehicle(plane.veh))
            {
                _activeKamikazePlanes.Remove(plane.veh.Pointer);
                return false;
            }

            AIPlane.PlaneAIState state = plane.planeState;
            if (state == AIPlane.PlaneAIState.takeOff ||
                state == AIPlane.PlaneAIState.gatheringSpeed ||
                state == AIPlane.PlaneAIState.landing)
            {
                return false;
            }

            myFaction = GetVehicleCrewFaction(plane.veh);
            if (string.IsNullOrEmpty(myFaction) ||
                !myFaction.StartsWith("Japan", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (_activeKamikazePlanes.Contains(plane.veh.Pointer))
                return true;

            // Trigger 1: Crippled / heavily damaged aircraft (>= 45 hull damage) refuses to bail out or retreat
            bool isCrippled = plane.veh.GetDamage() >= 45;

            // Trigger 2: Winchester ("No Return") — all bombs dropped AND all forward pilot gun ammo expended
            bool expendedAllOrdnanceAndAmmo = !plane.HasBombsToDeliver() && !plane.PilotGunHasAnyAmmo();

            return isCrippled || expendedAllOrdnanceAndAmmo;
        }

        private static Spottable FindBestKamikazeTarget(AIPlane plane, string myFaction)
        {
            RefreshCombinedArmsIntelIfNeeded();

            Vector3 planePos = plane.transform.position;
            Spottable bestTarget = null;
            float bestScore = -99999f;

            // 1. Prioritize known enemy tanks, combat vehicles, and radio-requested / marked strongpoints
            int clusterCount = _knownEnemyClusters.Count;
            for (int i = 0; i < clusterCount; i++)
            {
                KnownEnemyCluster cluster = _knownEnemyClusters[i];
                if (cluster.Faction == myFaction)
                    continue;

                for (int pass = 0; pass < 2; pass++)
                {
                    Spottable cand = pass == 0 ? cluster.VehicleTarget : cluster.PrimarySoldier;
                    if (!IsSpottableTargetValid(cand))
                        continue;

                    Vector3 candPos = cand.GetCenterOfUnit();
                    float dist = Vector3.Distance(planePos, candPos);
                    if (dist > 2600.0f)
                        continue;

                    float bonus = 35.0f;
                    if (pass == 0)
                    {
                        bonus = cand.TryCast<VehicleTank>() != null ? 140.0f : 95.0f;
                    }

                    float score = cluster.BasePriority + bonus - (dist * 0.06f);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestTarget = cand;
                    }
                }
            }

            // 2. Check airborne enemy heavy bombers (Shinten Seikūtai aerial ramming)
            int airCount = _airborneAircraft.Count;
            IntPtr selfVehPtr = plane.veh.Pointer;
            for (int i = 0; i < airCount; i++)
            {
                AirborneAircraftInfo info = _airborneAircraft[i];
                if (info.Faction == myFaction ||
                    info.Plane == null ||
                    info.Plane.Pointer == IntPtr.Zero ||
                    info.Plane.Pointer == selfVehPtr ||
                    info.Spottable == null ||
                    info.Spottable.Pointer == IntPtr.Zero ||
                    info.Plane.isGrounded ||
                    !info.Plane.CanFight())
                {
                    continue;
                }

                // Prefer ramming enemy heavy bombers, or any nearby enemy aircraft if no ground target is known
                if (!info.IsBomber && bestTarget != null)
                    continue;

                Vector3 airPos = info.Plane.transform != null ? info.Plane.transform.position : info.Position;
                float dist = Vector3.Distance(planePos, airPos);
                if (dist > 1800.0f)
                    continue;

                float score = (info.IsBomber ? 115.0f : 45.0f) - (dist * 0.06f);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestTarget = info.Spottable;
                }
            }

            // 3. Fallback: direct visual scan of battlefield squads if all ground radio nets are down
            if (bestTarget == null)
            {
                var allSquads = Squad.AllSquads;
                if (allSquads != null)
                {
                    foreach (var kvp in allSquads)
                    {
                        Squad sq = kvp.Value;
                        if (sq == null || sq.Pointer == IntPtr.Zero)
                            continue;

                        Soldier leader = sq.Leader;
                        if (leader == null || !leader.IsAlive || leader.transform == null || string.IsNullOrEmpty(leader.faction) || leader.faction == myFaction)
                            continue;

                        Vehicle mountedVeh = leader.IsOnVehicle() ? leader.GetCurrentVehicle() : null;
                        Spottable cand = null;
                        float baseVal = 30.0f;

                        if (mountedVeh != null && mountedVeh.CanFight() && !mountedVeh.IsAirVehicle())
                        {
                            cand = mountedVeh.Cast<Spottable>();
                            baseVal = mountedVeh.TryCast<VehicleTank>() != null ? 135.0f : 90.0f;
                        }
                        else
                        {
                            cand = leader.Cast<Spottable>();
                        }

                        if (cand == null || cand.Pointer == IntPtr.Zero)
                            continue;

                        float dist = Vector3.Distance(planePos, leader.transform.position);
                        if (dist > 2800.0f)
                            continue;

                        float score = baseVal - (dist * 0.06f);
                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestTarget = cand;
                        }
                    }
                }
            }

            return bestTarget;
        }

        public static void TryExecuteJapaneseKamikazeDive(AIPlane plane, bool isPostfix)
        {
            if (!IsJapaneseKamikazeCandidate(plane, out string myFaction))
                return;

            IntPtr vehPtr = plane.veh.Pointer;
            float now = Time.time;

            if (_activeKamikazePlanes.Add(vehPtr))
            {
                KamikazeDivesTriggeredCount++;
                if (VisionConfig.DebugLogging.Value)
                {
                    AiCore.LogDebug(
                        "Kamikaze",
                        $"Imperial Japanese aircraft (dmg:{plane.veh.GetDamage()}, bombs:{plane.HasBombsToDeliver()}, gunAmmo:{plane.PilotGunHasAnyAmmo()}) committed to final Tokkōtai kamikaze dive!");
                }
            }

            Spottable target = plane.targetToAttack;
            bool needNewTarget = !IsSpottableTargetValid(target);

            if (needNewTarget ||
                (plane.planeState != AIPlane.PlaneAIState.doingDive &&
                 (!_nextKamikazeTargetSearchTime.TryGetValue(vehPtr, out float nextSearch) || now >= nextSearch)))
            {
                _nextKamikazeTargetSearchTime[vehPtr] = now + 1.5f;
                Spottable bestTarget = FindBestKamikazeTarget(plane, myFaction);
                if (bestTarget != null && bestTarget.Pointer != IntPtr.Zero)
                {
                    target = bestTarget;
                    plane.targetToAttack = target;
                }
            }

            if (!IsSpottableTargetValid(target))
                return;

            Vector3 planePos = plane.transform.position;
            Vector3 targetPos = target.GetCenterOfUnit();

            if (target.IsAirVehicle())
            {
                // Aerial ramming (Shinten Seikūtai)
                plane.planeState = AIPlane.PlaneAIState.doingDive;
                plane.exitDiveEndTime = 0f;
                Vehicle airVeh = target.TryCast<Vehicle>();
                Vector3 leadOffset = (airVeh != null && airVeh.transform != null) ? airVeh.transform.forward * 10.0f : Vector3.zero;
                plane.flyTargetPosition = targetPos + leadOffset;
                if (!isPostfix)
                {
                    plane.nextUpdateCheck = now + 0.35f;
                }
                return;
            }

            Vector3 flatDelta = targetPos - planePos;
            flatDelta.y = 0f;
            float horizDist = flatDelta.magnitude;
            float heightAboveTarget = planePos.y - targetPos.y;

            if (horizDist > 300.0f && heightAboveTarget < horizDist * 0.40f)
            {
                // Stage 1: Climb/cruise to roll-in apex directly over the target so the plane doesn't clip terrain short
                plane.planeState = AIPlane.PlaneAIState.preparingDive;
                float desiredApexY = Mathf.Max(targetPos.y + 170.0f, planePos.y + 15.0f);
                plane.flyTargetPosition = new Vector3(targetPos.x, desiredApexY, targetPos.z);
            }
            else
            {
                // Stage 2: Terminal steep Kamikaze dive — aim 3.5m below target center so the aircraft never levels off
                plane.planeState = AIPlane.PlaneAIState.doingDive;
                plane.exitDiveEndTime = 0f;
                plane.flyTargetPosition = targetPos - Vector3.up * 3.5f;
            }

            if (!isPostfix)
            {
                // Bypass vanilla's periodic GetTerrainHeght pull-up & NeedRefill checks inside AIPlane.Update
                // while allowing per-frame RotationTo(flyTargetPosition) and forward gun firing to run every frame
                plane.nextUpdateCheck = now + 0.35f;
            }
        }

        public static void UpdateMacroObjectiveAndEmplacementDirector()
        {
            if (!VisionConfig.EnableMod.Value)
                return;

            float now = Time.time;
            if (now < _nextMacroDirectorTime)
                return;

            _nextMacroDirectorTime = now + 6.0f;

            if (!AiCore.IsMasterClient())
                return;

            BattleManager bm = BattleManager.instance;
            BattlePhase phase = bm != null ? bm.currentPhase : null;
            if (phase == null)
            {
                _garrisonAssignedObjectiveBySquad.Clear();
                return;
            }

            if (VisionConfig.EnableMacroObjectiveDirector.Value)
            {
                RunMacroObjectiveDirector(phase);
            }
            else if (_garrisonAssignedObjectiveBySquad.Count > 0)
            {
                _garrisonAssignedObjectiveBySquad.Clear();
            }

            if (VisionConfig.EnableProactiveEmplacementStaffing.Value)
            {
                RunProactiveEmplacementStaffing(now);
            }

            // Tier 3 Team Net GOAP Coordinator (Anvil & Hammer multi-squad tactics across Radio-Connected squads)
            RunTeamNetGoapCoordinator(now);
        }

        private static void RunMacroObjectiveDirector(BattlePhase phase)
        {
            var rawObjectives = phase.objectives;
            var scriptedObjectives = MissionObjectiveScripted.scriptedMissionObjectives;
            int rawLen = rawObjectives != null ? rawObjectives.Length : 0;
            int scriptedLen = scriptedObjectives != null ? scriptedObjectives.Count : 0;
            if (rawLen == 0 && scriptedLen == 0)
            {
                _garrisonAssignedObjectiveBySquad.Clear();
                return;
            }

            var activeObjectives = new List<MissionObjective>(rawLen + scriptedLen);
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

            if (scriptedObjectives != null)
            {
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

            if (activeObjectives.Count == 0)
            {
                _garrisonAssignedObjectiveBySquad.Clear();
                _squadTaskCommittedUntil.Clear();
                return;
            }

            var allSquads = Squad.AllSquads;
            if (allSquads == null || allSquads.Count == 0)
                return;

            float now = Time.time;
            var squadsByFaction = new Dictionary<string, List<Squad>>(4);
            var playerSquadsByFaction = new Dictionary<string, List<Squad>>(4);
            var activeFactions = new HashSet<string>();
            var allEnemyLeaderPositions = new List<KeyValuePair<string, Vector3>>(32);

            foreach (var kvp in allSquads)
            {
                Squad sq = kvp.Value;
                if (sq == null || sq.Pointer == IntPtr.Zero || sq.units == null)
                    continue;

                Soldier leader = sq.Leader;
                if (leader == null || !leader.IsAlive || string.IsNullOrEmpty(leader.faction))
                    continue;

                allEnemyLeaderPositions.Add(new KeyValuePair<string, Vector3>(leader.faction, leader.transform.position));

                // When the Human Player is the Squad Leader, vanilla SoldierAI.SquadOrdersRoutine does not run,
                // so if the player's squad has no active objective (or their previous objective was captured/lost)
                // and is connected to the Team Radio Net, let the Team Net suggest the next priority objective!
                if (leader.IsPlayer())
                {
                    _garrisonAssignedObjectiveBySquad.Remove(sq.Pointer);
                    if (!sq.IsVehicleCrew && IsSquadConnectedToRadioNet(sq))
                    {
                        if (!playerSquadsByFaction.TryGetValue(leader.faction, out List<Squad> pList))
                        {
                            pList = new List<Squad>(4);
                            playerSquadsByFaction[leader.faction] = pList;
                        }
                        pList.Add(sq);
                        activeFactions.Add(leader.faction);
                    }
                    continue;
                }

                if (sq.IsVehicleCrew || !AiCore.IsLocallyOwnedBot(leader) || leader.IsOnVehicle())
                {
                    _garrisonAssignedObjectiveBySquad.Remove(sq.Pointer);
                    continue;
                }

                if (leader.Params != null && (!leader.Params.canGiveOrders || !leader.Params.canMove))
                {
                    _garrisonAssignedObjectiveBySquad.Remove(sq.Pointer);
                    continue;
                }

                // Tier 3 Team Net Gate: Only squads that can hear the Team Radio Net can be dynamically
                // re-tasked across objectives by the Macro Director. Disconnected squads stay on their last known task!
                if (!IsSquadConnectedToRadioNet(sq))
                {
                    _garrisonAssignedObjectiveBySquad.Remove(sq.Pointer);
                    continue;
                }

                if (!squadsByFaction.TryGetValue(leader.faction, out List<Squad> fList))
                {
                    fList = new List<Squad>(16);
                    squadsByFaction[leader.faction] = fList;
                }
                fList.Add(sq);
                activeFactions.Add(leader.faction);
            }

            foreach (string faction in activeFactions)
            {
                var heldObjectives = new List<MissionObjective>(activeObjectives.Count);
                var unheldObjectives = new List<MissionObjective>(activeObjectives.Count);

                for (int i = 0; i < activeObjectives.Count; i++)
                {
                    MissionObjective obj = activeObjectives[i];
                    MissionTask task = obj.missionTask;
                    string secFaction = obj.GetSecuredFaction();
                    if (task.TryCast<MissionTaskPushFrontline>() != null)
                    {
                        if (secFaction == faction)
                        {
                            heldObjectives.Add(obj);
                        }
                        else if (obj.CanAttractFaction(faction))
                        {
                            unheldObjectives.Add(obj);
                        }
                    }
                    else
                    {
                        // Standard Invasion mode: GetSecuredFaction() always returns InvadersFaction;
                        // while !IsSecured(), the point is held by the Defenders (secFaction != faction) and attacked by Invaders.
                        if (!obj.IsSecured())
                        {
                            if (!string.IsNullOrEmpty(secFaction) && secFaction != faction)
                            {
                                heldObjectives.Add(obj);
                            }
                            else if (obj.CanAttractFaction(faction))
                            {
                                unheldObjectives.Add(obj);
                            }
                        }
                    }
                }

                // Suggest an objective over the Team Radio Net for any Player Squad Leader who has no active objective
                if (playerSquadsByFaction.TryGetValue(faction, out List<Squad> playerSquads))
                {
                    for (int pIdx = 0; pIdx < playerSquads.Count; pIdx++)
                    {
                        TrySuggestObjectiveForPlayerSquadLeader(
                            playerSquads[pIdx],
                            faction,
                            heldObjectives,
                            unheldObjectives,
                            allEnemyLeaderPositions);
                    }
                }

                if (!squadsByFaction.TryGetValue(faction, out List<Squad> eligibleSquads) || eligibleSquads.Count == 0)
                    continue;

                // If this faction holds 1+ objectives while also having 1+ uncaptured/contested objectives to attack:
                // Allocate faction-doctrine garrison share (e.g. 28% UK/France/Italy vs 22% default vs 16% USSR/Japan/Mongolia)
                // to garrison held points, prioritizing held objectives that have enemy squads within 150m.
                if (heldObjectives.Count > 0 && unheldObjectives.Count > 0 && eligibleSquads.Count >= 2)
                {
                    float garrisonRatio = GetFactionCommandProfile(faction).GarrisonSquadRatio;
                    int targetGarrisonCount = Mathf.Clamp(
                        Mathf.RoundToInt(eligibleSquads.Count * garrisonRatio),
                        1,
                        Mathf.Min(heldObjectives.Count, eligibleSquads.Count - 1));

                    // Sort held objectives so threatened or actively back-capped objectives get garrisoned first
                    heldObjectives.Sort((a, b) =>
                    {
                        float dA = GetObjectiveThreatPriorityScore(a, faction, allEnemyLeaderPositions);
                        float dB = GetObjectiveThreatPriorityScore(b, faction, allEnemyLeaderPositions);
                        return dA.CompareTo(dB);
                    });

                    var assignedGarrisonSquads = new HashSet<IntPtr>();
                    for (int g = 0; g < targetGarrisonCount; g++)
                    {
                        MissionObjective heldObj = heldObjectives[g % heldObjectives.Count];
                        IntPtr heldTaskPtr = heldObj.missionTask.Pointer;
                        Vector3 objPos = heldObj.GetTaskPosition();
                        Squad bestSquad = null;
                        float bestScore = float.MaxValue;

                        for (int sIdx = 0; sIdx < eligibleSquads.Count; sIdx++)
                        {
                            Squad cand = eligibleSquads[sIdx];
                            if (assignedGarrisonSquads.Contains(cand.Pointer))
                                continue;

                            bool alreadyOnThisGarrison =
                                (_garrisonAssignedObjectiveBySquad.TryGetValue(cand.Pointer, out IntPtr curTaskPtr) && curTaskPtr == heldTaskPtr) ||
                                (cand.currentTask != null && cand.currentTask.Pointer == heldTaskPtr);

                            // Never yank a squad that is already committed to attacking an active unheld objective
                            // (or contains the human player) back to rear garrison duty!
                            if (!alreadyOnThisGarrison && IsSquadCommittedToActiveObjective(cand, unheldObjectives, now))
                                continue;

                            float dSq = (cand.Leader.transform.position - objPos).sqrMagnitude;
                            // Strong continuity bonus if this squad is already garrisoning this objective
                            if (alreadyOnThisGarrison)
                            {
                                dSq *= 0.25f;
                            }

                            if (dSq < bestScore)
                            {
                                bestScore = dSq;
                                bestSquad = cand;
                            }
                        }

                        if (bestSquad != null)
                        {
                            assignedGarrisonSquads.Add(bestSquad.Pointer);
                            bool isNewGarrison = !_garrisonAssignedObjectiveBySquad.TryGetValue(bestSquad.Pointer, out IntPtr prevTaskPtr) ||
                                                 prevTaskPtr != heldTaskPtr;
                            _garrisonAssignedObjectiveBySquad[bestSquad.Pointer] = heldTaskPtr;

                            if (isNewGarrison || bestSquad.currentTask == null || bestSquad.currentTask.Pointer != heldTaskPtr)
                            {
                                AssignAiSquadMacroTask(bestSquad, heldObj, now, 50.0f);
                                ConsumeInitialHqSpawnBriefingIfNoRadio(bestSquad);
                                if (isNewGarrison)
                                {
                                    MacroGarrisonsAssignedCount++;
                                    if (VisionConfig.DebugLogging.Value)
                                    {
                                        AiCore.LogDebug("MacroDirector", $"{faction} squad '{bestSquad.squadName}' assigned to garrison held objective '{heldObj.GetDisplayName()}'.");
                                    }
                                }
                            }
                        }
                    }

                    // Ensure all non-garrison squads push/counterattack the uncaptured objectives instead of lingering on a captured point
                    for (int sIdx = 0; sIdx < eligibleSquads.Count; sIdx++)
                    {
                        Squad sq = eligibleSquads[sIdx];
                        if (assignedGarrisonSquads.Contains(sq.Pointer))
                            continue;

                        _garrisonAssignedObjectiveBySquad.Remove(sq.Pointer);

                        // Only reassign if sq.currentTask is null or no longer in unheldObjectives (e.g., objective was just captured!)
                        bool onActiveUnheld = IsSquadTaskInObjectiveList(sq, unheldObjectives);
                        if (!onActiveUnheld)
                        {
                            MissionObjective bestUnheld = GetClosestObjectiveToPosition(sq.Leader.transform.position, unheldObjectives);
                            if (bestUnheld != null && bestUnheld.missionTask != null)
                            {
                                AssignAiSquadMacroTask(sq, bestUnheld, now, 45.0f);
                                ConsumeInitialHqSpawnBriefingIfNoRadio(sq);
                                if (VisionConfig.DebugLogging.Value)
                                {
                                    AiCore.LogDebug("MacroDirector", $"{faction} squad '{sq.squadName}' directed to attack/counterattack '{bestUnheld.GetDisplayName()}'.");
                                }
                            }
                        }
                        else
                        {
                            ConsumeInitialHqSpawnBriefingIfNoRadio(sq);
                        }
                    }
                }
                else if (heldObjectives.Count > 1 && unheldObjectives.Count == 0 && eligibleSquads.Count >= 2)
                {
                    // Pure defense across multiple held objectives (e.g., Invasion Defenders):
                    // Clear any stale garrison overrides so captured objectives never remain pinned in TaskCanStillAttract,
                    // and reassign any squad whose currentTask is null or no longer in heldObjectives.
                    for (int sIdx = 0; sIdx < eligibleSquads.Count; sIdx++)
                    {
                        Squad sq = eligibleSquads[sIdx];
                        _garrisonAssignedObjectiveBySquad.Remove(sq.Pointer);

                        if (!IsSquadTaskInObjectiveList(sq, heldObjectives))
                        {
                            MissionObjective closestHeld = GetClosestObjectiveToPosition(sq.Leader.transform.position, heldObjectives);
                            if (closestHeld != null && closestHeld.missionTask != null)
                            {
                                AssignAiSquadMacroTask(sq, closestHeld, now, 45.0f);
                            }
                        }
                        ConsumeInitialHqSpawnBriefingIfNoRadio(sq);
                    }

                    // Vanilla assigns defenders via GetRandomNonSecuredMissionObjective() once at spawn and never rebalances.
                    // Sort held objectives so the most threatened or actively back-capped objective is index 0.
                    heldObjectives.Sort((a, b) =>
                    {
                        float dA = GetObjectiveThreatPriorityScore(a, faction, allEnemyLeaderPositions);
                        float dB = GetObjectiveThreatPriorityScore(b, faction, allEnemyLeaderPositions);
                        return dA.CompareTo(dB);
                    });

                    // 1) Ensure any threatened or actively back-capped held objective (enemy within 165m or capture bar ticking) has at least 1 defending squad
                    for (int h = 0; h < heldObjectives.Count; h++)
                    {
                        MissionObjective heldObj = heldObjectives[h];
                        IntPtr heldTaskPtr = heldObj.missionTask.Pointer;
                        Vector3 objPos = heldObj.GetTaskPosition();
                        float closestEnemySq = GetObjectiveThreatPriorityScore(heldObj, faction, allEnemyLeaderPositions);
                        if (closestEnemySq > 165f * 165f)
                            continue;

                        bool hasDefenderAssigned = false;
                        for (int sIdx = 0; sIdx < eligibleSquads.Count; sIdx++)
                        {
                            Squad sq = eligibleSquads[sIdx];
                            if (sq.currentTask != null && sq.currentTask.Pointer == heldTaskPtr)
                            {
                                hasDefenderAssigned = true;
                                break;
                            }
                        }

                        if (!hasDefenderAssigned)
                        {
                            Squad bestReserve = PickBestReserveDefenderSquad(eligibleSquads, heldObjectives, heldTaskPtr, objPos, now);
                            if (bestReserve != null)
                            {
                                if (!heldObj.CanAttractFaction(faction))
                                    _garrisonAssignedObjectiveBySquad[bestReserve.Pointer] = heldTaskPtr;
                                AssignAiSquadMacroTask(bestReserve, heldObj, now, 50.0f);
                                MacroGarrisonsAssignedCount++;
                                if (VisionConfig.DebugLogging.Value)
                                {
                                    AiCore.LogDebug("MacroDirector", $"{faction} reserve squad '{bestReserve.squadName}' shifted to defend threatened objective '{heldObj.GetDisplayName()}'.");
                                }
                            }
                        }
                    }

                    // 2) If the primary threatened objective (enemy within 155m or capture progress ticking) is under heavy attack while an unthreatened
                    //    secondary objective (no enemy within 175m and 0% capture progress) has 2+ squads sitting idle, shift 1 excess squad per tick
                    //    (always leaving at least 1 sentry squad on each secondary objective).
                    MissionObjective primaryThreatObj = heldObjectives[0];
                    float primaryThreatSq = GetObjectiveThreatPriorityScore(primaryThreatObj, faction, allEnemyLeaderPositions);
                    if (primaryThreatSq <= 155f * 155f)
                    {
                        IntPtr primaryTaskPtr = primaryThreatObj.missionTask.Pointer;
                        Vector3 primaryPos = primaryThreatObj.GetTaskPosition();

                        for (int h = heldObjectives.Count - 1; h >= 1; h--)
                        {
                            MissionObjective quietObj = heldObjectives[h];
                            float quietEnemySq = GetObjectiveThreatPriorityScore(quietObj, faction, allEnemyLeaderPositions);
                            if (quietEnemySq <= 175f * 175f)
                                continue;

                            IntPtr quietTaskPtr = quietObj.missionTask.Pointer;
                            int quietSquadCount = 0;
                            Squad closestExcessSquad = null;
                            float closestExcessDistSq = float.MaxValue;

                            for (int sIdx = 0; sIdx < eligibleSquads.Count; sIdx++)
                            {
                                Squad sq = eligibleSquads[sIdx];
                                if (sq.currentTask != null && sq.currentTask.Pointer == quietTaskPtr)
                                {
                                    quietSquadCount++;
                                    // Respect objective commitment so we don't ping-pong a squad that was just reassigned
                                    if (IsSquadCommittedToActiveObjective(sq, heldObjectives, now))
                                        continue;

                                    float dSq = (sq.Leader.transform.position - primaryPos).sqrMagnitude;
                                    if (dSq < closestExcessDistSq)
                                    {
                                        closestExcessDistSq = dSq;
                                        closestExcessSquad = sq;
                                    }
                                }
                            }

                            if (quietSquadCount >= 2 && closestExcessSquad != null)
                            {
                                if (!primaryThreatObj.CanAttractFaction(faction))
                                    _garrisonAssignedObjectiveBySquad[closestExcessSquad.Pointer] = primaryTaskPtr;
                                AssignAiSquadMacroTask(closestExcessSquad, primaryThreatObj, now, 50.0f);
                                MacroGarrisonsAssignedCount++;
                                if (VisionConfig.DebugLogging.Value)
                                {
                                    AiCore.LogDebug("MacroDirector", $"{faction} excess defender squad '{closestExcessSquad.squadName}' shifted from quiet '{quietObj.GetDisplayName()}' to reinforce '{primaryThreatObj.GetDisplayName()}'.");
                                }
                                break;
                            }
                        }
                    }
                }
                else
                {
                    for (int sIdx = 0; sIdx < eligibleSquads.Count; sIdx++)
                    {
                        Squad sq = eligibleSquads[sIdx];
                        _garrisonAssignedObjectiveBySquad.Remove(sq.Pointer);

                        // In pure attack (heldObjectives.Count == 0 && unheldObjectives.Count > 0),
                        // if a squad's previous objective was just captured, assign the closest remaining unheld objective
                        // and update the HUD if the player is a member of this squad!
                        if (unheldObjectives.Count > 0 && !IsSquadTaskInObjectiveList(sq, unheldObjectives))
                        {
                            MissionObjective nextAttack = GetClosestObjectiveToPosition(sq.Leader.transform.position, unheldObjectives);
                            if (nextAttack != null && nextAttack.missionTask != null)
                            {
                                AssignAiSquadMacroTask(sq, nextAttack, now, 45.0f);
                            }
                        }
                    }
                }
            }
        }

        private static bool IsSquadTaskInObjectiveList(Squad sq, List<MissionObjective> objectives)
        {
            if (sq == null || sq.currentTask == null || sq.currentTask.Pointer == IntPtr.Zero || objectives == null)
                return false;

            try
            {
                if (sq.currentTask.GetTaskCompletationPercentage() >= 1.0f)
                    return false;
            }
            catch { }

            IntPtr curPtr = sq.currentTask.Pointer;
            for (int i = 0; i < objectives.Count; i++)
            {
                MissionObjective obj = objectives[i];
                if (obj != null && obj.missionTask != null && obj.missionTask.Pointer == curPtr)
                    return true;
            }
            return false;
        }

        private static bool IsSquadCommittedToActiveObjective(Squad sq, List<MissionObjective> validObjectives, float now)
        {
            if (!IsSquadTaskInObjectiveList(sq, validObjectives))
                return false;

            // When the Human Player is a subordinate member of an AI-led squad, keep the AI Squad Leader
            // committed to the current active objective until it is actually captured or lost!
            if (sq.IsPlayerInSquad())
                return true;

            if (_squadTaskCommittedUntil.TryGetValue(sq.Pointer, out float commitUntil) && now < commitUntil)
                return true;

            if (sq.Leader != null && sq.Leader.transform != null)
            {
                float distToTaskSq = (sq.Leader.transform.position - sq.currentTask.GetTaskDestination()).sqrMagnitude;
                if (distToTaskSq <= 85.0f * 85.0f)
                    return true;
            }

            if (_squadBlackboards.TryGetValue(sq.Pointer, out SquadBlackboard board) && board.HasActiveThreat)
                return true;

            return false;
        }

        private static void AssignAiSquadMacroTask(Squad sq, MissionObjective obj, float now, float commitDuration)
        {
            if (sq == null || obj == null || obj.missionTask == null)
                return;

            IntPtr newTaskPtr = obj.missionTask.Pointer;
            bool changed = sq.currentTask == null || sq.currentTask.Pointer != newTaskPtr;
            sq.currentTask = obj.missionTask.Cast<SquadTask>();
            _squadTaskCommittedUntil[sq.Pointer] = now + (sq.IsPlayerInSquad() ? Mathf.Max(commitDuration, 65.0f) : commitDuration);

            if (sq.Leader != null)
            {
                sq.Leader_OrderAttackCurrentTask(sq.Leader);
            }

            if (changed && sq.IsPlayerInSquad())
            {
                NotifyPlayerSquadTaskUpdated(sq);
            }
        }

        private static void TrySuggestObjectiveForPlayerSquadLeader(
            Squad playerSq,
            string faction,
            List<MissionObjective> heldObjectives,
            List<MissionObjective> unheldObjectives,
            List<KeyValuePair<string, Vector3>> allEnemyLeaderPositions)
        {
            if (playerSq == null || playerSq.Leader == null || playerSq.Leader.transform == null)
                return;

            // If the Player Squad Leader already has an active, valid objective (in either unheldObjectives or heldObjectives),
            // never overwrite the player's current objective!
            if (IsSquadTaskInObjectiveList(playerSq, unheldObjectives) ||
                IsSquadTaskInObjectiveList(playerSq, heldObjectives))
            {
                ConsumeInitialHqSpawnBriefingIfNoRadio(playerSq);
                return;
            }

            MissionObjective suggestedObj = null;
            Vector3 leaderPos = playerSq.Leader.transform.position;

            if (unheldObjectives != null && unheldObjectives.Count > 0)
            {
                suggestedObj = GetClosestObjectiveToPosition(leaderPos, unheldObjectives);
            }
            else if (heldObjectives != null && heldObjectives.Count > 0)
            {
                // Suggest the most threatened held objective if enemies are within 165m, otherwise the closest held objective
                MissionObjective bestThreatened = null;
                float bestThreatScore = 165f * 165f;
                for (int i = 0; i < heldObjectives.Count; i++)
                {
                    MissionObjective cand = heldObjectives[i];
                    float score = GetObjectiveThreatPriorityScore(cand, faction, allEnemyLeaderPositions);
                    if (score <= bestThreatScore)
                    {
                        bestThreatScore = score;
                        bestThreatened = cand;
                    }
                }
                suggestedObj = bestThreatened ?? GetClosestObjectiveToPosition(leaderPos, heldObjectives);
            }

            if (suggestedObj == null || suggestedObj.missionTask == null)
                return;

            playerSq.currentTask = suggestedObj.missionTask.Cast<SquadTask>();
            ConsumeInitialHqSpawnBriefingIfNoRadio(playerSq);
            NotifyPlayerSquadTaskUpdated(playerSq);

            // Have the squad's AI Radioman (if alive) audibly relay the new HQ objective to the Player Squad Leader
            if (playerSq.units != null)
            {
                var units = playerSq.units;
                int count = units.Count;
                for (int i = 0; i < count; i++)
                {
                    Soldier u = units[i];
                    if (u != null && u.IsAlive && !u.IsPlayer() && u.IsRadioman())
                    {
                        TrySayTacticalVoice(u, playerSq, VoiceManager.VoiceClip.attackThere, 0.25f);
                        break;
                    }
                }
            }

            if (VisionConfig.DebugLogging.Value)
            {
                AiCore.LogDebug(
                    "MacroDirector",
                    $"Team Radio Net suggested objective '{suggestedObj.GetDisplayName()}' to Player Squad Leader ('{playerSq.squadName}').");
            }
        }

        private static void NotifyPlayerSquadTaskUpdated(Squad sq)
        {
            if (sq == null)
                return;

            try
            {
                if (ResourcesManager.instance != null)
                {
                    ResourcesManager.instance.StartCoroutine(sq.UpdateSquadTaskUI());
                }
                PlayerGUI.UpdateSquadGUI();
            }
            catch { }
        }

        private static Squad PickBestReserveDefenderSquad(
            List<Squad> eligibleSquads,
            List<MissionObjective> heldObjectives,
            IntPtr targetTaskPtr,
            Vector3 targetPos,
            float now)
        {
            Squad bestSquad = null;
            float bestScore = float.MaxValue;

            for (int sIdx = 0; sIdx < eligibleSquads.Count; sIdx++)
            {
                Squad cand = eligibleSquads[sIdx];
                if (cand.currentTask != null && cand.currentTask.Pointer == targetTaskPtr)
                    continue;

                // Respect task commitment hysteresis so reserve squads don't get ping-ponged mid-march
                if (IsSquadCommittedToActiveObjective(cand, heldObjectives, now))
                    continue;

                int sameTaskCount = 0;
                if (cand.currentTask != null)
                {
                    IntPtr curPtr = cand.currentTask.Pointer;
                    for (int j = 0; j < eligibleSquads.Count; j++)
                    {
                        Squad other = eligibleSquads[j];
                        if (other.currentTask != null && other.currentTask.Pointer == curPtr)
                            sameTaskCount++;
                    }
                }

                float dSq = (cand.Leader.transform.position - targetPos).sqrMagnitude;
                // Distant (> 65m) reserve squads can only be re-tasked across the map if connected to the Radio Net
                if (dSq > 65f * 65f && !IsSquadConnectedToRadioNet(cand))
                    continue;

                // Strongly prefer pulling a squad from an objective that already has 2+ squads (or an unassigned squad)
                // so we don't strip the last defender from another objective unless necessary.
                if (cand.currentTask != null && sameTaskCount <= 1)
                {
                    dSq *= 4.0f;
                }

                if (dSq < bestScore)
                {
                    bestScore = dSq;
                    bestSquad = cand;
                }
            }

            return bestSquad;
        }

        private static float GetObjectiveThreatPriorityScore(
            MissionObjective obj,
            string myFaction,
            List<KeyValuePair<string, Vector3>> leaderPositions)
        {
            if (obj == null)
                return float.MaxValue;

            float bestSq = GetClosestEnemyDistanceSqToPoint(obj.GetTaskPosition(), myFaction, leaderPositions);
            if (obj.missionTask != null)
            {
                try
                {
                    float capPct = Mathf.Clamp01(obj.missionTask.GetTaskCompletationPercentage());
                    if (capPct > 0.05f)
                    {
                        // Back-cap / active capture alarm: even if only a lone enemy rifleman is inside the cap zone
                        // (without an enemy squad leader nearby), treat the point as critically threatened (< 50m)
                        // and scale priority urgently as capture progress approaches 100%.
                        float capVirtualDistSq = Mathf.Lerp(50f * 50f, 8f * 8f, capPct);
                        bestSq = Mathf.Min(bestSq * Mathf.Lerp(0.25f, 0.05f, capPct), capVirtualDistSq);
                    }
                }
                catch { }
            }
            return bestSq;
        }

        private static float GetClosestEnemyDistanceSqToPoint(
            Vector3 point,
            string myFaction,
            List<KeyValuePair<string, Vector3>> leaderPositions)
        {
            float bestSq = float.MaxValue;
            for (int i = 0; i < leaderPositions.Count; i++)
            {
                var kvp = leaderPositions[i];
                if (kvp.Key == myFaction)
                    continue;
                float dSq = (kvp.Value - point).sqrMagnitude;
                if (dSq < bestSq)
                    bestSq = dSq;
            }
            return bestSq;
        }

        private static MissionObjective GetClosestObjectiveToPosition(Vector3 pos, List<MissionObjective> list)
        {
            MissionObjective best = null;
            float bestSq = float.MaxValue;
            for (int i = 0; i < list.Count; i++)
            {
                MissionObjective obj = list[i];
                float dSq = (obj.GetTaskPosition() - pos).sqrMagnitude;
                if (obj.missionTask != null)
                {
                    try
                    {
                        float capPct = Mathf.Clamp01(obj.missionTask.GetTaskCompletationPercentage());
                        if (capPct > 0.15f)
                        {
                            // Weight nearby partially captured objectives higher so attacking squads finish the cap
                            dSq *= Mathf.Lerp(0.85f, 0.35f, capPct);
                        }
                    }
                    catch { }
                }
                if (dSq < bestSq)
                {
                    bestSq = dSq;
                    best = obj;
                }
            }
            return best;
        }

        private static void RunProactiveEmplacementStaffing(float now)
        {
            var allVehicles = Vehicle.allVehicles;
            var allSquads = Squad.AllSquads;
            if (allVehicles == null || allSquads == null || allVehicles.Count == 0)
                return;

            int vCount = allVehicles.Count;
            for (int i = 0; i < vCount; i++)
            {
                Vehicle veh = allVehicles[i];
                if (veh == null || veh.Pointer == IntPtr.Zero || veh.transform == null)
                    continue;

                // Note: Vehicle.CanFight() (0x18085B7E0) returns false whenever a vehicle/emplacement is empty!
                // Check !IsDisabled() && !IsCoverDestroyed() instead so empty MG tripods, bunker MGs, AT guns, and AA guns are staffed.
                if (!AiCore.IsLocallyOwnedVehicle(veh) || veh.IsDisabled() || veh.IsCoverDestroyed() || veh.IsAirVehicle() || veh.PlayerIsInside())
                    continue;

                bool isStaticGun = veh.TryCast<MovableVehicle>() == null;
                bool isUnmannedCombatVehicle = !isStaticGun && !veh.HasDriver() && !veh.IsTransportVehicle();

                Turret mainTurret = veh.GetMainTurret(false);
                if (mainTurret == null || mainTurret.WeaponsCount() <= 0 || mainTurret.GetSittedUnit(veh) != null)
                    continue;

                IntPtr vehPtr = veh.Pointer;
                Vector3 vehPos = veh.transform.position;

                // 1. If this is an empty/abandoned movable combat vehicle (Tank, Armored Car)
                //    and a bailed-out Vehicle Crew (`sq.IsVehicleCrew`) with >= 2 alive crewmen (driver + gunner) is within 85m,
                //    order all alive members of that crew (including the Leader) to re-mount it!
                if (isUnmannedCombatVehicle)
                {
                    Squad bestBailedCrew = null;
                    float bestCrewDistSq = 85.0f * 85.0f;

                    foreach (var kvp in allSquads)
                    {
                        Squad sq = kvp.Value;
                        if (sq == null || !sq.IsVehicleCrew || sq.units == null || sq.IsPlayerInSquad() || sq.SomeoneIsRepairing())
                            continue;

                        Soldier leader = sq.Leader;
                        if (leader == null || !AiCore.IsLocallyOwnedBot(leader) || leader.IsOnVehicle() || leader.transform == null)
                            continue;

                        int aliveCrewCount = 0;
                        for (int cIdx = 0; cIdx < sq.units.Count; cIdx++)
                        {
                            Soldier cUnit = sq.units[cIdx];
                            if (cUnit != null && cUnit.IsAlive && !cUnit.IsOnVehicle())
                                aliveCrewCount++;
                        }
                        if (aliveCrewCount < 2)
                            continue;

                        float dSq = (leader.transform.position - vehPos).sqrMagnitude;
                        if (dSq < bestCrewDistSq)
                        {
                            bestCrewDistSq = dSq;
                            bestBailedCrew = sq;
                        }
                    }

                    if (bestBailedCrew != null)
                    {
                        try
                        {
                            _squadOrderedToVehicle[bestBailedCrew.Pointer] = vehPtr;
                            AiDestination crewVehDest = veh.Cast<AiDestination>();
                            for (int cIdx = 0; cIdx < bestBailedCrew.units.Count; cIdx++)
                            {
                                Soldier cUnit = bestBailedCrew.units[cIdx];
                                if (cUnit != null && AiCore.IsLocallyOwnedBot(cUnit) && !cUnit.IsOnVehicle())
                                {
                                    _assignedEmplacementUntil[cUnit.Pointer] = now + 24.0f;
                                    cUnit.CoverPosition(crewVehDest);
                                }
                            }
                            StaticGunsStaffedCount++;
                            TrySayTacticalVoice(bestBailedCrew.Leader, bestBailedCrew, VoiceManager.VoiceClip.illTakeHisSeat, 0.15f);
                        }
                        catch { }
                        continue;
                    }
                }

                // Never assign regular foot soldiers to driverless movable vehicles (halftracks, trucks, jeeps, tanks) —
                // only staff true static guns/emplacements (`isStaticGun`) that have an open turret seat!
                if (!isStaticGun || !TryGetAvailableTurretSeatIndex(veh, out _))
                    continue;

                Soldier bestCandidate = null;
                float bestDistSq = 55.0f * 55.0f;
                bool alreadyClaimedByInboundBot = false;

                foreach (var kvp in allSquads)
                {
                    Squad sq = kvp.Value;
                    if (sq == null || sq.units == null || sq.IsPlayerInSquad())
                        continue;

                    bool isBailedCrew = sq.IsVehicleCrew && !sq.SomeoneIsRepairing();
                    Soldier leader = sq.Leader;
                    if (leader == null || !leader.IsAlive || leader.IsOnVehicle())
                        continue;

                    float empMult = GetFactionCommandProfile(leader.faction).EmplacementSearchMult;
                    float maxSquadDist = (isBailedCrew ? 75.0f : 60.0f) * empMult;
                    if ((leader.transform.position - vehPos).sqrMagnitude > maxSquadDist * maxSquadDist)
                        continue;

                    var units = sq.units;
                    int uCount = units.Count;
                    for (int uIdx = 0; uIdx < uCount; uIdx++)
                    {
                        Soldier u = units[uIdx];
                        if (u != null && u.IsAlive && u.targetDestination != null && u.targetDestination.Pointer == vehPtr)
                        {
                            if (_assignedEmplacementUntil.TryGetValue(u.Pointer, out float claimUntil) && now >= claimUntil && !u.IsOnVehicle())
                            {
                                // Anti-stuck safeguard: bot failed to reach/board emplacement within 24s; release and cooldown
                                u.targetDestination = null;
                                _assignedEmplacementUntil.Remove(u.Pointer);
                                _unreachableEmplacementUntil[u.Pointer] = now + 120.0f;
                                continue;
                            }
                            alreadyClaimedByInboundBot = true;
                            break;
                        }
                    }
                    if (alreadyClaimedByInboundBot)
                        break;

                    // Verify the turret can aim toward the squad's threat sector
                    Vector3 threatDir = GetEffectiveThreatDirection(leader, sq, vehPos, veh.transform.forward);
                    if (!mainTurret.CanShoot360 && threatDir.sqrMagnitude > 0.01f && !mainTurret.IsPositionInDirection(threatDir))
                        continue;

                    // Do not send soldiers to an emplacement that is already overrun at close range (< 22m)
                    bool emplacementOverrun = false;
                    for (int c = 0; c < _knownEnemyClusters.Count; c++)
                    {
                        if (_knownEnemyClusters[c].Faction != leader.faction &&
                            (_knownEnemyClusters[c].Position - vehPos).sqrMagnitude <= 22.0f * 22.0f)
                        {
                            emplacementOverrun = true;
                            break;
                        }
                    }
                    if (emplacementOverrun)
                        continue;

                    for (int uIdx = 0; uIdx < uCount; uIdx++)
                    {
                        Soldier u = units[uIdx];
                        if (u == null || !AiCore.IsLocallyOwnedBot(u) || u.IsOnVehicle())
                            continue;

                        // Allow bailed-out vehicle crew leaders to crew static guns/artillery, but keep infantry Squad Leaders free
                        if (u.IsSquadLeader() && !isBailedCrew)
                            continue;

                        if (u.hasSyringe || u.IsRadioman() || u.isBleeding || u.HasSurrended() || u.dontLeaveVehicle)
                            continue;

                        if (u.GetSuppressionValue() >= 30f ||
                            (u.aiController != null && u.aiController.HasEnemyAndCanFireAtHim && u.aiController.distFromTarget < 28f))
                        {
                            continue;
                        }

                        if (u.Params != null && (!u.Params.canMove || u.Params.neverLeaveVehicle))
                            continue;

                        if (_assignedEmplacementUntil.TryGetValue(u.Pointer, out float assignedUntil) && now < assignedUntil)
                            continue;

                        if (_unreachableEmplacementUntil.TryGetValue(u.Pointer, out float unreachableUntil) && now < unreachableUntil)
                            continue;

                        Vector3 uPos = u.transform.position;
                        // Avoid assigning ground infantry to unreachable rooftop/cliff emplacements (> 4.5m vertical delta)
                        if (Mathf.Abs(vehPos.y - uPos.y) > 4.5f)
                            continue;

                        float dSq = (uPos - vehPos).sqrMagnitude;
                        // Prioritize bailed-out vehicle crews for crewing empty emplacements/artillery
                        if (isBailedCrew)
                            dSq *= 0.45f;

                        if (dSq < bestDistSq)
                        {
                            bestDistSq = dSq;
                            bestCandidate = u;
                        }
                    }
                }

                if (alreadyClaimedByInboundBot)
                    continue;

                if (bestCandidate != null)
                {
                    _assignedEmplacementUntil[bestCandidate.Pointer] = now + 24.0f;
                    _combatCrouchLockUntil.Remove(bestCandidate.Pointer);
                    if (bestCandidate.aiController != null)
                    {
                        SetBotPose(bestCandidate.aiController, bestCandidate, SoldierPose.Idle, true);
                    }
                    AiDestination vehDest = veh.Cast<AiDestination>();
                    bestCandidate.CoverPosition(vehDest);
                    StaticGunsStaffedCount++;
                    TrySayTacticalVoice(bestCandidate, bestCandidate.joinedSquad, VoiceManager.VoiceClip.illTakeHisSeat, 0.2f);
                    if (VisionConfig.DebugLogging.Value)
                    {
                        AiCore.LogDebug("Emplacement", $"{bestCandidate.faction} soldier assigned to crew empty emplacement/turret ({Mathf.Sqrt(bestDistSq):F0}m away).");
                    }
                }
            }
        }

        private static bool TryGetAvailableTurretSeatIndex(Vehicle veh, out int turretSeatIndex)
        {
            turretSeatIndex = -1;
            if (veh == null || veh.seats == null)
                return false;

            var seats = veh.seats;
            int count = seats.Length;

            // Prefer the MainTurretSeat first if it is empty
            Seats mainSeat = veh.MainTurretSeat;
            if (mainSeat != null && mainSeat.hasTurret && !mainSeat.hasUnitSet &&
                mainSeat.connectedTurret != null && mainSeat.connectedTurret.WeaponsCount() > 0)
            {
                for (int i = 0; i < count; i++)
                {
                    if (seats[i] != null && seats[i].Pointer == mainSeat.Pointer)
                    {
                        turretSeatIndex = i;
                        return true;
                    }
                }
            }

            for (int i = 0; i < count; i++)
            {
                Seats s = seats[i];
                if (s != null && s.hasTurret && !s.hasUnitSet &&
                    s.connectedTurret != null && s.connectedTurret.WeaponsCount() > 0)
                {
                    turretSeatIndex = i;
                    return true;
                }
            }

            return false;
        }

        private static bool TryGetAvailableDriverSeatIndex(Vehicle veh, out int driverSeatIndex)
        {
            driverSeatIndex = -1;
            if (veh == null || veh.seats == null)
                return false;

            var seats = veh.seats;
            int count = seats.Length;
            for (int i = 0; i < count; i++)
            {
                Seats s = seats[i];
                if (s != null && s.isDriver && !s.hasUnitSet)
                {
                    driverSeatIndex = i;
                    return true;
                }
            }
            return false;
        }

        [HarmonyPatch(typeof(Squad), nameof(Squad.SendUnitsToVehicle))]
        public static class Squad_SendUnitsToVehicle_Patch
        {
            public static void Prefix(Squad __instance, Vehicle __0)
            {
                try
                {
                    if (__instance != null && __instance.Pointer != IntPtr.Zero && __0 != null && __0.Pointer != IntPtr.Zero)
                    {
                        _squadOrderedToVehicle[__instance.Pointer] = __0.Pointer;
                    }
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(Squad), nameof(Squad.OrderLeaveVehicle))]
        public static class Squad_OrderLeaveVehicle_Patch
        {
            public static void Postfix(Squad __instance)
            {
                try
                {
                    if (__instance != null && __instance.Pointer != IntPtr.Zero)
                    {
                        _squadOrderedToVehicle.Remove(__instance.Pointer);
                    }
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(Squad), nameof(Squad.OrderLeaveAllVehiclesAndCovers))]
        public static class Squad_OrderLeaveAllVehiclesAndCovers_Patch
        {
            public static void Postfix(Squad __instance)
            {
                try
                {
                    if (__instance != null && __instance.Pointer != IntPtr.Zero)
                    {
                        _squadOrderedToVehicle.Remove(__instance.Pointer);
                    }
                }
                catch { }
            }
        }

        /// <summary>
        /// Prevents CoverManager / Squad.SendUnitsToCovers from treating driverless movable vehicles
        /// (abandoned trucks, halftracks, jeeps, tanks) or emplacements with no open turret seats as valid cover.
        /// </summary>
        [HarmonyPatch(typeof(Vehicle), nameof(Vehicle.IsCoverAvailable))]
        public static class Vehicle_IsCoverAvailable_Patch
        {
            public static void Postfix(Vehicle __instance, ref bool __result)
            {
                try
                {
                    if (!__result || !VisionConfig.EnableMod.Value || __instance == null)
                        return;

                    if (__instance.TryCast<MovableVehicle>() != null && !__instance.HasDriver())
                    {
                        __result = false;
                        return;
                    }

                    if (!TryGetAvailableTurretSeatIndex(__instance, out _))
                    {
                        __result = false;
                    }
                }
                catch { }
            }
        }

        /// <summary>
        /// In vanilla (0x18085FD40), Vehicle.IsCoverOccupied returns false as long as ANY seat (including non-turret
        /// loader/spotter/passenger seats) is empty, causing extra foot soldiers to climb into non-turret seats.
        /// Mark the cover as occupied as soon as all turret seats are filled (or if it is a driverless movable vehicle).
        /// </summary>
        [HarmonyPatch(typeof(Vehicle), nameof(Vehicle.IsCoverOccupied))]
        public static class Vehicle_IsCoverOccupied_Patch
        {
            public static void Postfix(Vehicle __instance, ref bool __result)
            {
                try
                {
                    if (__result || !VisionConfig.EnableMod.Value || __instance == null)
                        return;

                    if (__instance.TryCast<MovableVehicle>() != null && !__instance.HasDriver())
                    {
                        __result = true;
                        return;
                    }

                    if (!TryGetAvailableTurretSeatIndex(__instance, out _))
                    {
                        __result = true;
                    }
                }
                catch { }
            }
        }

        /// <summary>
        /// In vanilla, Vehicle.OnCoverReached (0x180860560) calls GetOnVehicleBestPos (0x18085E430 / 0x18087B070),
        /// which places AI foot soldiers into whichever empty seat is first or physically closest — including
        /// driver seats and non-turret passenger/loader seats!
        /// This patch ensures:
        ///   1. Regular AI foot soldiers NEVER climb into driverless movable vehicles or non-turret seats,
        ///      and ONLY board an empty turret seat (`TryGetAvailableTurretSeatIndex`).
        ///   2. Bailed-out Vehicle Crews (`sq.IsVehicleCrew`) re-mounting an unmanned combat vehicle always fill
        ///      the driver seat first (`TryGetAvailableDriverSeatIndex`), then turret seats.
        /// </summary>
        [HarmonyPatch(typeof(Vehicle), nameof(Vehicle.OnCoverReached))]
        public static class Vehicle_OnCoverReached_Patch
        {
            public static bool Prefix(Vehicle __instance, Soldier __0)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value || __instance == null || __0 == null || !__0.IsAI())
                        return true;

                    if (__instance.IsCoverDestroyed() || __instance.IsDisabled())
                    {
                        __0.CoverPosition(null);
                        return false;
                    }

                    Squad sq = __0.joinedSquad;
                    bool isPlayerSquadOrVeh = __instance.PlayerIsInside() || (sq != null && sq.IsPlayerInSquad());
                    if (isPlayerSquadOrVeh)
                        return true;

                    bool isExplicitBoardOrder = sq != null &&
                                                _squadOrderedToVehicle.TryGetValue(sq.Pointer, out IntPtr orderedVehPtr) &&
                                                orderedVehPtr == __instance.Pointer;
                    bool isVehicleCrew = sq != null && sq.IsVehicleCrew;
                    bool isMovable = __instance.TryCast<MovableVehicle>() != null;

                    if (isVehicleCrew && isMovable)
                    {
                        if (!__instance.HasDriver() && TryGetAvailableDriverSeatIndex(__instance, out int driverIdx))
                        {
                            __instance.GetOnVehicle(driverIdx, __0);
                            return false;
                        }
                        if (TryGetAvailableTurretSeatIndex(__instance, out int crewTurretIdx))
                        {
                            __instance.GetOnVehicle(crewTurretIdx, __0);
                            return false;
                        }
                        return true;
                    }

                    if (isExplicitBoardOrder)
                        return true;

                    // Regular AI foot soldier reaching a Vehicle as cover/emplacement:
                    // 1. Never climb into a driverless movable vehicle (truck, halftrack, jeep, tank)
                    if (isMovable && !__instance.HasDriver())
                    {
                        __0.CoverPosition(null);
                        return false;
                    }

                    // 2. Only climb into an empty turret seat; never board a non-turret loader/passenger seat!
                    if (TryGetAvailableTurretSeatIndex(__instance, out int turretSeatIdx))
                    {
                        __instance.GetOnVehicle(turretSeatIdx, __0);
                        return false;
                    }

                    __0.CoverPosition(null);
                    return false;
                }
                catch { }
                return true;
            }
        }

        [HarmonyPatch(typeof(Squad), nameof(Squad.UnitCanChangeCover))]
        public static class Squad_UnitCanChangeCover_Patch
        {
            public static void Postfix(Soldier __0, ref bool __result)
            {
                try
                {
                    if (!__result || !VisionConfig.EnableMod.Value || __0 == null)
                        return;

                    IntPtr ptr = __0.Pointer;
                    float now = Time.time;
                    if (_assignedEmplacementUntil.TryGetValue(ptr, out float empUntil) && now < empUntil)
                    {
                        __result = false;
                        return;
                    }

                    if (_localCoverProtectedUntil.TryGetValue(ptr, out float covUntil) && now < covUntil)
                    {
                        __result = false;
                        return;
                    }

                    if (__0.targetDestination != null &&
                        (__0.targetDestination.TryCast<MedicHealingDestination>() != null ||
                         __0.targetDestination.TryCast<VehicleRepairTask>() != null))
                    {
                        __result = false;
                        return;
                    }

                    if (HasActiveGoapCoverLock(ptr, now))
                    {
                        __result = false;
                    }
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(Squad), nameof(Squad.TaskCanStillAttract))]
        public static class Squad_TaskCanStillAttract_Patch
        {
            public static void Postfix(Squad __instance, ref bool __result)
            {
                try
                {
                    if (__result ||
                        !VisionConfig.EnableMod.Value ||
                        !VisionConfig.EnableMacroObjectiveDirector.Value ||
                        __instance == null ||
                        __instance.currentTask == null)
                    {
                        return;
                    }

                    if (_garrisonAssignedObjectiveBySquad.TryGetValue(__instance.Pointer, out IntPtr garrisonObjPtr) &&
                        __instance.currentTask.Pointer == garrisonObjPtr)
                    {
                        __result = true;
                    }
                }
                catch { }
            }
        }

        // =====================================================================
        // Smart APC / Transport Dismounting Under Fire
        // =====================================================================
        private static void TryDismountTransportUnderThreat(Vehicle veh, AIVehicle aiVeh, bool forcedBySuppression)
        {
            if (veh == null || veh.Pointer == IntPtr.Zero || veh.transform == null)
                return;

            if (!AiCore.IsLocallyOwnedVehicle(veh) || !veh.IsTransportVehicle() || veh.IsAirVehicle() || veh.PlayerIsDriving())
                return;

            IntPtr vehPtr = veh.Pointer;
            float now = Time.time;
            if (_nextApcDismountCooldown.TryGetValue(vehPtr, out float cooldownUntil) && now < cooldownUntil)
                return;

            var seats = veh.seats;
            if (seats == null || seats.Length == 0)
                return;

            bool hasDismountablePassengers = false;
            string faction = null;
            for (int i = 0; i < seats.Length; i++)
            {
                Seats s = seats[i];
                if (s == null || s.isDriver || s.hasTurret)
                    continue;

                Soldier passenger = s.unitSet;
                if (passenger != null && passenger.IsAlive && !passenger.IsPlayer())
                {
                    if (passenger.dontLeaveVehicle || (passenger.Params != null && passenger.Params.neverLeaveVehicle))
                        continue;

                    hasDismountablePassengers = true;
                    if (string.IsNullOrEmpty(faction))
                        faction = passenger.faction;
                    break;
                }
            }

            if (!hasDismountablePassengers)
                return;

            bool shouldDismount = forcedBySuppression ||
                                  (aiVeh != null && aiVeh.hasEnemyTank) ||
                                  (aiVeh != null && aiVeh.SomeoneIsSuppressed()) ||
                                  (aiVeh != null && aiVeh.hasEnemy && veh.GetDamage() > 0);

            if (!shouldDismount && !string.IsNullOrEmpty(faction))
            {
                Vector3 vehPos = veh.transform.position;
                const float threatRadiusSq = 48.0f * 48.0f;

                var allSquads = Squad.AllSquads;
                if (allSquads != null)
                {
                    foreach (var kvp in allSquads)
                    {
                        Squad sq = kvp.Value;
                        if (sq == null || sq.Leader == null || !sq.Leader.IsAlive || sq.Leader.faction == faction)
                            continue;

                        if ((sq.Leader.transform.position - vehPos).sqrMagnitude <= threatRadiusSq)
                        {
                            shouldDismount = true;
                            break;
                        }
                    }
                }
            }

            if (shouldDismount)
            {
                _nextApcDismountCooldown[vehPtr] = now + 8.0f;
                VehicleWithWheels wheeled = veh.TryCast<VehicleWithWheels>();
                if (wheeled != null)
                {
                    wheeled.Brake();
                }

                veh.DropInfantry(false);
                Squad sqInside = veh.GetSquadInside();
                if (sqInside != null && !sqInside.IsPlayerInSquad())
                {
                    sqInside.OrderLeaveVehicle(veh, false);
                }

                SmartApcDismountsCount++;
                if (VisionConfig.DebugLogging.Value)
                {
                    AiCore.LogDebug("ApcDismount", $"Transport vehicle dismounted infantry passengers under combat threat.");
                }
            }
        }

        // =====================================================================
        // Friendly Infantry Right-of-Way & Anti-Roadkill
        // =====================================================================
        private static Vector2 GetCachedVehicleHalfExtents(Vehicle veh)
        {
            if (veh == null || veh.Pointer == IntPtr.Zero)
                return new Vector2(1.65f, 3.10f);

            IntPtr vehPtr = veh.Pointer;
            if (_cachedVehicleExtents.TryGetValue(vehPtr, out Vector2 extents))
                return extents;

            extents = new Vector2(1.65f, 3.10f);
            try
            {
                VehicleWithWheels wheeled = veh.TryCast<VehicleWithWheels>();
                if (wheeled != null)
                {
                    Bounds b = wheeled.CalculateVehicleLocalBounds();
                    extents.x = Mathf.Clamp(b.extents.x, 1.15f, 2.50f);
                    extents.y = Mathf.Clamp(b.extents.z, 2.00f, 5.00f);
                }
            }
            catch { }

            _cachedVehicleExtents[vehPtr] = extents;
            return extents;
        }

        private static void RecordFriendlyYieldOrSave(IntPtr vehPtr, float now, string reason)
        {
            if (!_nextFriendlyYieldCountTime.TryGetValue(vehPtr, out float nextCount) || now >= nextCount)
            {
                _nextFriendlyYieldCountTime[vehPtr] = now + 4.5f;
                FriendlyRoadkillsPreventedCount++;
                if (VisionConfig.DebugLogging.Value)
                {
                    AiCore.LogDebug("FriendlyRightOfWay", reason);
                }
            }
        }

        private static void TryNudgeFriendlySoldierAside(
            Soldier s,
            Vector3 vehPos,
            Vector3 travelDir,
            Vector3 vehRight,
            float halfWidth,
            float now,
            bool immediateControllerPush)
        {
            if (s == null || s.Pointer == IntPtr.Zero || !s.IsAI() || !AiCore.IsLocallyOwnedBot(s) || s.transform == null)
                return;

            Vector3 sPos = s.transform.position;
            Vector3 delta = sPos - vehPos;
            float sideSign = Vector3.Dot(delta, vehRight) >= 0f ? 1f : -1f;
            float stepDist = halfWidth + 2.35f;
            int solidMask = AiCore.GetSolidEnvironmentMask();

            Vector3 sideDir = vehRight * sideSign;
            Vector3 candidatePos = sPos + sideDir * stepDist + travelDir * 0.70f;
            bool primaryValid = !AiCore.IsDirectionBlockedByEnvironment(sPos + Vector3.up * 0.75f, sideDir, 2.6f, s.transform.root) &&
                                Physics.Raycast(candidatePos + Vector3.up * 0.85f, Vector3.down, 2.65f, solidMask, QueryTriggerInteraction.Ignore);

            if (!primaryValid)
            {
                sideSign = -sideSign;
                sideDir = vehRight * sideSign;
                candidatePos = sPos + sideDir * stepDist + travelDir * 0.70f;
                primaryValid = !AiCore.IsDirectionBlockedByEnvironment(sPos + Vector3.up * 0.75f, sideDir, 2.6f, s.transform.root) &&
                               Physics.Raycast(candidatePos + Vector3.up * 0.85f, Vector3.down, 2.65f, solidMask, QueryTriggerInteraction.Ignore);
            }

            if (immediateControllerPush && primaryValid)
            {
                CharacterController cc = s.m_controller;
                if (cc != null && cc.enabled)
                {
                    cc.Move(sideDir * 0.38f);
                }
            }

            if (_nextFriendlyStepAsideTime.TryGetValue(s.Pointer, out float nextNudge) && now < nextNudge)
                return;

            _nextFriendlyStepAsideTime[s.Pointer] = now + 1.15f;

            if (!primaryValid || s.UsingBipod() || s.isInBodyAnimation || (s.Params != null && !s.Params.canMove))
                return;

            if (s.m_pose == SoldierPose.Prone)
            {
                if (s.aiController != null)
                {
                    SetBotPose(s.aiController, s, SoldierPose.Idle, true);
                }
                else
                {
                    s.SetPose(SoldierPose.Idle, false);
                }
            }

            if (s.aiController != null)
            {
                s.aiController.MoveDirectlyToward(candidatePos, 1.15f);
            }
        }

        private static void EvaluateFriendlySoldierInVehicleCorridor(
            Soldier s,
            string vehFaction,
            Vector3 vehPos,
            Vector3 travelDir,
            Vector3 vehRight,
            float halfWidth,
            float minFwd,
            float maxStepAsideFwd,
            float maxBrakeFwd,
            float stepAsideHalfWidth,
            float brakeHalfWidth,
            float now,
            ref bool playerInBrakeCorridor,
            ref bool aiInBrakeCorridor)
        {
            if (s == null || s.Pointer == IntPtr.Zero || !s.IsAlive || s.IsOnVehicle() || s.transform == null)
                return;

            if (string.IsNullOrEmpty(s.faction) || !ResourcesManager.IsSameFaction(vehFaction, s.faction))
                return;

            Vector3 sPos = s.transform.position;
            Vector3 delta = sPos - vehPos;
            if (Mathf.Abs(delta.y) > 3.2f)
                return;

            float fwdDist = Vector3.Dot(delta, travelDir);
            if (fwdDist < minFwd || fwdDist > maxStepAsideFwd)
                return;

            float latAbs = Mathf.Abs(Vector3.Dot(delta, vehRight));
            if (latAbs > stepAsideHalfWidth)
                return;

            // Protect friendly soldier from accidental low-speed contact while clearing the corridor
            s.DisableHitByVehicleFor(1.5f);

            if (s.IsAI())
            {
                TryNudgeFriendlySoldierAside(s, vehPos, travelDir, vehRight, halfWidth, now, false);
            }

            if (fwdDist <= maxBrakeFwd && latAbs <= brakeHalfWidth)
            {
                if (s.IsPlayer())
                    playerInBrakeCorridor = true;
                else
                    aiInBrakeCorridor = true;
            }
        }

        private static bool ShouldYieldToFriendlyInfantry(AIVehicle aiVeh, Vehicle veh, bool isReversing)
        {
            if (veh == null || veh.Pointer == IntPtr.Zero || veh.transform == null)
                return false;

            if (!AiCore.IsLocallyOwnedVehicle(veh) || veh.PlayerIsDriving())
                return false;

            IntPtr vehPtr = veh.Pointer;
            float now = Time.time;

            // Fast cached path between 0.18s spatial checks
            if (_nextVehiclePedestrianScanTime.TryGetValue(vehPtr, out float nextScan) && now < nextScan)
            {
                return _vehicleYieldUntilTime.TryGetValue(vehPtr, out float yieldUntil) && now < yieldUntil;
            }

            _nextVehiclePedestrianScanTime[vehPtr] = now + 0.18f;

            // Always hold brake if a squadmate is actively repairing the vehicle
            if (aiVeh != null && aiVeh.squadInside != null && aiVeh.squadInside.SomeoneIsRepairing())
            {
                _vehicleYieldUntilTime[vehPtr] = now + 0.25f;
                return true;
            }

            string vehFaction = GetVehicleCrewFaction(veh);
            if (string.IsNullOrEmpty(vehFaction))
                return false;

            Transform tr = veh.transform;
            Vector3 vehPos = tr.position;
            Vector3 travelDir = isReversing ? -tr.forward : tr.forward;
            travelDir.y = 0f;
            if (travelDir.sqrMagnitude < 0.001f)
                return false;
            travelDir.Normalize();

            Vector3 vehRight = tr.right;
            vehRight.y = 0f;
            vehRight = vehRight.sqrMagnitude > 0.001f ? vehRight.normalized : Vector3.right;

            Vector2 extents = GetCachedVehicleHalfExtents(veh);
            float halfWidth = extents.x;
            float halfLength = extents.y;

            float speedMs = 0f;
            MovableVehicle mv = veh.TryCast<MovableVehicle>();
            if (mv != null)
                speedMs = Mathf.Abs(mv.GetCurrentSpeed());

            float minFwd = Mathf.Max(0.85f, halfLength * 0.45f);
            float maxStepAsideFwd = Mathf.Clamp(halfLength + 5.2f + speedMs * 0.65f, 5.5f, 11.0f);
            float maxBrakeFwd = Mathf.Clamp(halfLength + 3.8f + speedMs * 0.55f, 4.5f, 9.0f);
            float stepAsideHalfWidth = halfWidth + 1.45f;
            float brakeHalfWidth = halfWidth + 0.70f;

            bool playerInBrakeCorridor = false;
            bool aiInBrakeCorridor = false;

            Soldier player = Soldier.CurrentControlledSoldierOrNull();
            if (player != null)
            {
                EvaluateFriendlySoldierInVehicleCorridor(
                    player, vehFaction, vehPos, travelDir, vehRight,
                    halfWidth, minFwd, maxStepAsideFwd, maxBrakeFwd, stepAsideHalfWidth, brakeHalfWidth,
                    now, ref playerInBrakeCorridor, ref aiInBrakeCorridor);
            }

            Squad crewSquad = aiVeh != null ? aiVeh.squadInside : null;
            if (crewSquad != null && crewSquad.units != null)
            {
                var crewUnits = crewSquad.units;
                int crewCount = crewUnits.Count;
                for (int i = 0; i < crewCount; i++)
                {
                    EvaluateFriendlySoldierInVehicleCorridor(
                        crewUnits[i], vehFaction, vehPos, travelDir, vehRight,
                        halfWidth, minFwd, maxStepAsideFwd, maxBrakeFwd, stepAsideHalfWidth, brakeHalfWidth,
                        now, ref playerInBrakeCorridor, ref aiInBrakeCorridor);
                }
            }

            var allSquads = Squad.AllSquads;
            if (allSquads != null)
            {
                foreach (var kvp in allSquads)
                {
                    Squad sq = kvp.Value;
                    if (sq == null || sq == crewSquad || sq.units == null)
                        continue;

                    Soldier leader = sq.Leader;
                    if (leader == null || leader.transform == null || string.IsNullOrEmpty(leader.faction))
                        continue;

                    if (!ResourcesManager.IsSameFaction(vehFaction, leader.faction))
                        continue;

                    if ((leader.transform.position - vehPos).sqrMagnitude > 48f * 48f)
                        continue;

                    var units = sq.units;
                    int unitCount = units.Count;
                    for (int i = 0; i < unitCount; i++)
                    {
                        EvaluateFriendlySoldierInVehicleCorridor(
                            units[i], vehFaction, vehPos, travelDir, vehRight,
                            halfWidth, minFwd, maxStepAsideFwd, maxBrakeFwd, stepAsideHalfWidth, brakeHalfWidth,
                            now, ref playerInBrakeCorridor, ref aiInBrakeCorridor);
                    }
                }
            }

            if (playerInBrakeCorridor || aiInBrakeCorridor)
            {
                if (!_vehicleContinuousYieldStart.TryGetValue(vehPtr, out float yieldStart))
                {
                    yieldStart = now;
                    _vehicleContinuousYieldStart[vehPtr] = yieldStart;
                }

                // Always yield for the human player; yield up to 3.5s for AI infantry while they step aside
                if (playerInBrakeCorridor || (now - yieldStart) <= 3.5f)
                {
                    _vehicleYieldUntilTime[vehPtr] = now + 0.24f;
                    RecordFriendlyYieldOrSave(
                        vehPtr,
                        now,
                        $"{vehFaction} vehicle yielded right-of-way to friendly {(playerInBrakeCorridor ? "player" : "infantry")} ({(isReversing ? "reverse" : "forward")}).");
                    return true;
                }
            }
            else
            {
                _vehicleContinuousYieldStart.Remove(vehPtr);
                _vehicleYieldUntilTime.Remove(vehPtr);
            }

            return false;
        }

        [HarmonyPatch(typeof(AIVehicle), nameof(AIVehicle.MovableVehicleRoutine))]
        public static class AIVehicle_MovableVehicleRoutine_Patch
        {
            public static void Prefix(AIVehicle __instance)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value ||
                        !VisionConfig.EnableFriendlyVehicleRightOfWay.Value ||
                        __instance == null ||
                        __instance.placingInUnstuck)
                    {
                        return;
                    }

                    Vehicle veh = __instance.veh;
                    if (veh == null || veh.Pointer == IntPtr.Zero)
                        return;

                    if (_vehiclesSeekingRepairStation.TryGetValue(veh.Pointer, out RepairStation st) &&
                        st != null && st.Pointer != IntPtr.Zero && st.transform != null && veh.transform != null)
                    {
                        float r = Mathf.Max(5.0f, st.radius) * 0.85f;
                        if ((veh.transform.position - st.transform.position).sqrMagnitude <= r * r)
                        {
                            float pauseUntil = Time.time + 0.35f;
                            if (__instance.resumeDriveTime < pauseUntil)
                                __instance.resumeDriveTime = pauseUntil;
                            return;
                        }
                    }

                    bool isReversing = __instance.going_in_retro;
                    if (!isReversing && (__instance.path == null || __instance.path.NodesCount <= 0 || __instance.stopToShoot))
                        return;

                    if (ShouldYieldToFriendlyInfantry(__instance, veh, isReversing))
                    {
                        // Setting resumeDriveTime uses AIVehicle.MovableVehicleRoutine's native pause branch:
                        // it calls veh.HoldBrake() + veh.Move(Vector2.zero) and bypasses VehicleStuckCheck()!
                        float pauseUntil = Time.time + 0.24f;
                        if (__instance.resumeDriveTime < pauseUntil)
                            __instance.resumeDriveTime = pauseUntil;
                    }
                }
                catch { }
            }

            public static void Postfix(AIVehicle __instance)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value || __instance == null)
                        return;

                    Vehicle veh = __instance.veh;
                    if (veh == null || veh.Pointer == IntPtr.Zero)
                        return;

                    IntPtr vehPtr = veh.Pointer;
                    float now = Time.time;

                    if (VisionConfig.EnableSmartApcDismount.Value)
                    {
                        if (!_nextApcCheckTime.TryGetValue(vehPtr, out float nextCheck) || now >= nextCheck)
                        {
                            _nextApcCheckTime[vehPtr] = now + 0.75f;
                            TryDismountTransportUnderThreat(veh, __instance, false);
                        }
                    }

                    if (VisionConfig.EnableCombinedArmsPrioritySupport.Value)
                    {
                        if (!_nextVehicleSupportSteerTime.TryGetValue(vehPtr, out float nextSteer) || now >= nextSteer)
                        {
                            _nextVehicleSupportSteerTime[vehPtr] = now + 3.5f;
                            TrySteerVehicleTowardKnownThreat(__instance, veh, now);
                        }
                    }
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(VehicleWithWheels), nameof(VehicleWithWheels.OnTriggerEnter))]
        public static class VehicleWithWheels_OnTriggerEnter_Patch
        {
            public static bool Prefix(VehicleWithWheels __instance, Collider __0)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value ||
                        !VisionConfig.EnableFriendlyVehicleRightOfWay.Value ||
                        __instance == null ||
                        __0 == null)
                    {
                        return true;
                    }

                    GameObject go = __0.gameObject;
                    if (go == null)
                        return true;

                    Soldier soldier = go.GetComponent<Soldier>();
                    if (soldier == null || soldier.Pointer == IntPtr.Zero || !soldier.IsAlive)
                        return true;

                    string vehFaction = GetVehicleCrewFaction(__instance);
                    if (string.IsNullOrEmpty(vehFaction) || string.IsNullOrEmpty(soldier.faction))
                        return true;

                    // Enemy soldiers still take full vanilla roadkill damage
                    if (!ResourcesManager.IsSameFaction(vehFaction, soldier.faction))
                        return true;

                    // Friendly soldier: grant hit immunity and gently nudge AI bots clear of the hull/tracks
                    soldier.DisableHitByVehicleFor(1.25f);

                    float now = Time.time;
                    if (soldier.IsAI() && __instance.transform != null)
                    {
                        Vector2 extents = GetCachedVehicleHalfExtents(__instance);
                        Vector3 vehFwd = __instance.transform.forward;
                        vehFwd.y = 0f;
                        vehFwd = vehFwd.sqrMagnitude > 0.001f ? vehFwd.normalized : Vector3.forward;
                        Vector3 vehRight = __instance.transform.right;
                        vehRight.y = 0f;
                        vehRight = vehRight.sqrMagnitude > 0.001f ? vehRight.normalized : Vector3.right;

                        TryNudgeFriendlySoldierAside(soldier, __instance.transform.position, vehFwd, vehRight, extents.x, now, true);
                    }

                    // In vanilla VehicleWithWheels.OnTriggerEnter (0x18087C92B-0x18087C940), same-faction collisions
                    // still deal 1000f lethal roadkill damage whenever soldier.IsPlayer() or veh.PlayerIsInside()!
                    if (soldier.IsPlayer() || __instance.PlayerIsInside())
                    {
                        RecordFriendlyYieldOrSave(
                            __instance.Pointer,
                            now,
                            $"Prevented friendly {vehFaction} vehicle from roadkilling friendly {(soldier.IsPlayer() ? "player" : "soldier")}.");
                    }

                    return false;
                }
                catch { }
                return true;
            }
        }

        // NOTE: Never patch base Vehicle.OnVehicleSuppressed (0x180269ea0) because it is an
        // empty 3-byte `ret 0` virtual stub folded by IL2CPP ICF across 1,239 methods!
        // VehicleWithWheels.OnVehicleSuppressed (0x18087cae0) is a unique 54-byte method,
        // and tracked/halftrack APCs are covered via aiVeh.SomeoneIsSuppressed() above.
        [HarmonyPatch(typeof(VehicleWithWheels), nameof(VehicleWithWheels.OnVehicleSuppressed))]
        public static class Vehicle_OnVehicleSuppressed_Patch
        {
            public static void Postfix(VehicleWithWheels __instance)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value ||
                        !VisionConfig.EnableSmartApcDismount.Value ||
                        __instance == null)
                    {
                        return;
                    }

                    TryDismountTransportUnderThreat(__instance, null, true);
                }
                catch { }
            }
        }

        // =====================================================================
        // Tank Standoff & Reverse Discipline (AIVehicle.MoveTowardCurrentNodeTank)
        // =====================================================================
        [HarmonyPatch(typeof(AIVehicle), nameof(AIVehicle.MoveTowardCurrentNodeTank))]
        public static class AIVehicle_MoveTowardCurrentNodeTank_Patch
        {
            public static bool Prefix(AIVehicle __instance)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value ||
                        !VisionConfig.EnableTankStandoffAndReverse.Value ||
                        __instance == null)
                    {
                        return true;
                    }

                    Vehicle veh = __instance.veh;
                    if (!AiCore.IsLocallyOwnedVehicle(veh) || veh.PlayerIsDriving() || veh.transform == null)
                        return true;

                    Turret mainTurret = veh.GetMainTurret(false);
                    TurretAI turretAi = mainTurret != null ? mainTurret.GetTurretAI() : null;
                    Spottable target = turretAi != null ? turretAi.CurrentVisibleTarget : null;
                    if (target == null || target.Pointer == IntPtr.Zero || !turretAi.targetInWeaponRange)
                        return true;

                    if (target.IsAirVehicle())
                        return true;

                    Vector3 vehPos = veh.transform.position;
                    Vector3 targetPos = target.GetCenterOfUnit();
                    Vector3 toTarget = targetPos - vehPos;
                    float dist = toTarget.magnitude;

                    SoldierPersonality crewPers = GetVehicleCrewPersonality(veh);
                    float reverseDist = 28.0f;
                    float maxStandoffDist = 90.0f;
                    if (crewPers == SoldierPersonality.TankCrew_Sniper)
                    {
                        reverseDist = 36.0f;
                        maxStandoffDist = 140.0f;
                    }
                    else if (crewPers == SoldierPersonality.TankCrew_Brawler)
                    {
                        reverseDist = 20.0f;
                        maxStandoffDist = 62.0f;
                    }

                    if (dist > maxStandoffDist)
                        return true;

                    IntPtr vehPtr = veh.Pointer;
                    float now = Time.time;

                    // 1. Close-Range Threat Reverse: back up using MoveTankRetro while keeping frontal armor toward the enemy
                    if (dist < reverseDist && Vector3.Angle(veh.transform.forward, toTarget) <= 75f)
                    {
                        if (!AiCore.IsDirectionBlockedByEnvironment(vehPos + Vector3.up * 1.2f, -veh.transform.forward, 7.5f, veh.transform.root) &&
                            (!VisionConfig.EnableFriendlyVehicleRightOfWay.Value || !ShouldYieldToFriendlyInfantry(__instance, veh, true)))
                        {
                            __instance.ForceRetroFor(2.2f, AIVehicle.RetroBehaviour.backward);
                            __instance.MoveTankRetro();
                            if (!_nextTankStandoffCountTime.TryGetValue(vehPtr, out float nextRevCount) || now >= nextRevCount)
                            {
                                _nextTankStandoffCountTime[vehPtr] = now + 5.0f;
                                TankStandoffHoldsCount++;
                                if (VisionConfig.DebugLogging.Value)
                                {
                                    AiCore.LogDebug("TankStandoff", $"Tank reversing from close-range threat ({dist:F0}m < {reverseDist:F0}m).");
                                }
                            }
                            return false;
                        }
                    }

                    // 2. Standoff Firing Hold: halt and orient frontal hull toward target instead of driving into AT-grenade range
                    if (turretAi.CanSee(target) || CanTankShellIndoorTargetFacade(mainTurret, veh, target))
                    {
                        __instance.StopAndRotateTowardTarget();
                        if (!_nextTankStandoffCountTime.TryGetValue(vehPtr, out float nextHoldCount) || now >= nextHoldCount)
                        {
                            _nextTankStandoffCountTime[vehPtr] = now + 6.0f;
                            TankStandoffHoldsCount++;
                            if (VisionConfig.DebugLogging.Value)
                            {
                                AiCore.LogDebug("TankStandoff", $"Tank holding standoff firing position ({dist:F0}m, personality:{crewPers}).");
                            }
                        }
                        return false;
                    }
                }
                catch { }
                return true;
            }
        }
    }
}
