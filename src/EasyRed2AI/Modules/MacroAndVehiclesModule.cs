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
                SetBotPose(ai, soldier, SoldierPose.Idle);
                ai.MoveDirectlyToward(behindWallPos, 1.2f);
                AiCore.LogDebug("AntiArmor", $"{soldier.faction} rifleman scrambling behind wall to evade {targetVeh.name} at {dist:F0}m.", 6.0f);
                return true;
            }

            // 2. If assigned cover exists but the soldier isn't on it yet, sprint to that cover
            if (!soldier.IsOnCover() && soldier.HasDestinationAssigned)
            {
                soldier.TryToGetToActualCover();
                if (ai.IsMoving())
                {
                    SetBotPose(ai, soldier, SoldierPose.Idle);
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
                    SetBotPose(ai, soldier, SoldierPose.Idle);
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
        public static int CombinedArmsPriorityAssistsCount;

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
            _nextCombinedArmsIntelRefreshTime = 0f;
            if (_nextVehicleSupportSteerTime.Count > 128)
                _nextVehicleSupportSteerTime.Clear();
            if (_nextPlaneOrbitSteerTime.Count > 64)
                _nextPlaneOrbitSteerTime.Clear();
            if (_nextCombinedArmsAssistCountTime.Count > 128)
                _nextCombinedArmsAssistCountTime.Clear();
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

                    if (!turretAi.CanSee(cand))
                        continue;

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

            // Fix vanilla RefreshTargets quirk (0x180832b3e): allow HE turrets to answer friendly radio support
            // requests even if a low-priority unmarked lone infantryman was spotted far away.
            if (hasHe && curTarget != null && curScore < 18.0f && !turretAi.HasRadioSupportRequest)
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

        private static void TrySteerVehicleTowardKnownThreat(AIVehicle aiVeh, Vehicle veh, float now)
        {
            if (aiVeh == null || veh == null || veh.Pointer == IntPtr.Zero || veh.transform == null)
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
                turretAi.CanSee(turretAi.CurrentVisibleTarget))
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
                else
                {
                    // Never initiate ground attacks while enemy aircraft contest the airspace
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
        }

        private static void RunMacroObjectiveDirector(BattlePhase phase)
        {
            var rawObjectives = phase.objectives;
            if (rawObjectives == null || rawObjectives.Length == 0)
            {
                _garrisonAssignedObjectiveBySquad.Clear();
                return;
            }

            var activeObjectives = new List<MissionObjective>(rawObjectives.Length);
            for (int i = 0; i < rawObjectives.Length; i++)
            {
                MissionObjective obj = rawObjectives[i];
                if (obj == null || obj.Pointer == IntPtr.Zero || obj.gameObject == null || !obj.gameObject.activeInHierarchy)
                    continue;
                if (obj.missionTask != null && !obj.missionTask.IsTaskEnabled())
                    continue;
                activeObjectives.Add(obj);
            }

            if (activeObjectives.Count == 0)
            {
                _garrisonAssignedObjectiveBySquad.Clear();
                return;
            }

            var allSquads = Squad.AllSquads;
            if (allSquads == null || allSquads.Count == 0)
                return;

            var squadsByFaction = new Dictionary<string, List<Squad>>(4);
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

                if (sq.IsPlayerInSquad() || sq.IsVehicleCrew || !AiCore.IsLocallyOwnedBot(leader) || leader.IsOnVehicle())
                {
                    _garrisonAssignedObjectiveBySquad.Remove(sq.Pointer);
                    continue;
                }

                if (leader.Params != null && (!leader.Params.canGiveOrders || !leader.Params.canMove))
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
            }

            foreach (var fKvp in squadsByFaction)
            {
                string faction = fKvp.Key;
                List<Squad> eligibleSquads = fKvp.Value;
                if (eligibleSquads.Count == 0)
                    continue;

                var heldObjectives = new List<MissionObjective>(activeObjectives.Count);
                var unheldObjectives = new List<MissionObjective>(activeObjectives.Count);

                for (int i = 0; i < activeObjectives.Count; i++)
                {
                    MissionObjective obj = activeObjectives[i];
                    string secFaction = obj.GetSecuredFaction();
                    if (secFaction == faction)
                    {
                        heldObjectives.Add(obj);
                    }
                    else if (obj.CanAttractFaction(faction))
                    {
                        unheldObjectives.Add(obj);
                    }
                }

                // If this faction holds 1+ objectives while also having 1+ uncaptured/contested objectives to attack:
                // Allocate ~22% of eligible squads (at least 1, leaving at least 1 squad attacking) to garrison held points,
                // prioritizing held objectives that have enemy squads within 150m.
                if (heldObjectives.Count > 0 && unheldObjectives.Count > 0 && eligibleSquads.Count >= 2)
                {
                    int targetGarrisonCount = Mathf.Clamp(
                        Mathf.RoundToInt(eligibleSquads.Count * 0.22f),
                        1,
                        Mathf.Min(heldObjectives.Count, eligibleSquads.Count - 1));

                    // Sort held objectives so threatened objectives (enemy within 150m) get garrisoned first
                    heldObjectives.Sort((a, b) =>
                    {
                        float dA = GetClosestEnemyDistanceSqToPoint(a.GetTaskPosition(), faction, allEnemyLeaderPositions);
                        float dB = GetClosestEnemyDistanceSqToPoint(b.GetTaskPosition(), faction, allEnemyLeaderPositions);
                        return dA.CompareTo(dB);
                    });

                    var assignedGarrisonSquads = new HashSet<IntPtr>();
                    for (int g = 0; g < targetGarrisonCount; g++)
                    {
                        MissionObjective heldObj = heldObjectives[g % heldObjectives.Count];
                        Vector3 objPos = heldObj.GetTaskPosition();
                        Squad bestSquad = null;
                        float bestScore = float.MaxValue;

                        for (int sIdx = 0; sIdx < eligibleSquads.Count; sIdx++)
                        {
                            Squad cand = eligibleSquads[sIdx];
                            if (assignedGarrisonSquads.Contains(cand.Pointer))
                                continue;

                            float dSq = (cand.Leader.transform.position - objPos).sqrMagnitude;
                            // Continuity bonus if this squad is already garrisoning this objective
                            if (_garrisonAssignedObjectiveBySquad.TryGetValue(cand.Pointer, out IntPtr curObjPtr) &&
                                curObjPtr == heldObj.Pointer)
                            {
                                dSq *= 0.45f;
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
                            bool isNewGarrison = !_garrisonAssignedObjectiveBySquad.TryGetValue(bestSquad.Pointer, out IntPtr prevPtr) ||
                                                 prevPtr != heldObj.Pointer;
                            _garrisonAssignedObjectiveBySquad[bestSquad.Pointer] = heldObj.Pointer;

                            if (isNewGarrison || bestSquad.currentTask == null || bestSquad.currentTask.Pointer != heldObj.Pointer)
                            {
                                bestSquad.currentTask = heldObj.Cast<SquadTask>();
                                bestSquad.Leader_OrderAttackCurrentTask(bestSquad.Leader);
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

                        bool needsAttackReassign = sq.currentTask == null;
                        if (!needsAttackReassign)
                        {
                            for (int h = 0; h < heldObjectives.Count; h++)
                            {
                                if (sq.currentTask.Pointer == heldObjectives[h].Pointer)
                                {
                                    needsAttackReassign = true;
                                    break;
                                }
                            }
                        }

                        if (needsAttackReassign)
                        {
                            MissionObjective bestUnheld = GetClosestObjectiveToPosition(sq.Leader.transform.position, unheldObjectives);
                            if (bestUnheld != null)
                            {
                                sq.currentTask = bestUnheld.Cast<SquadTask>();
                                sq.Leader_OrderAttackCurrentTask(sq.Leader);
                                if (VisionConfig.DebugLogging.Value)
                                {
                                    AiCore.LogDebug("MacroDirector", $"{faction} squad '{sq.squadName}' directed to attack/counterattack '{bestUnheld.GetDisplayName()}'.");
                                }
                            }
                        }
                    }
                }
                else if (heldObjectives.Count > 1 && unheldObjectives.Count == 0 && eligibleSquads.Count >= 2)
                {
                    // Pure defense across multiple held objectives: ensure threatened held objectives have at least 1 defending squad
                    for (int h = 0; h < heldObjectives.Count; h++)
                    {
                        MissionObjective heldObj = heldObjectives[h];
                        Vector3 objPos = heldObj.GetTaskPosition();
                        float closestEnemySq = GetClosestEnemyDistanceSqToPoint(objPos, faction, allEnemyLeaderPositions);
                        if (closestEnemySq > 140f * 140f)
                            continue;

                        bool hasDefenderAssigned = false;
                        Squad nearestReserve = null;
                        float nearestReserveDistSq = float.MaxValue;

                        for (int sIdx = 0; sIdx < eligibleSquads.Count; sIdx++)
                        {
                            Squad sq = eligibleSquads[sIdx];
                            if (sq.currentTask != null && sq.currentTask.Pointer == heldObj.Pointer)
                            {
                                hasDefenderAssigned = true;
                                break;
                            }

                            float dSq = (sq.Leader.transform.position - objPos).sqrMagnitude;
                            if (dSq < nearestReserveDistSq)
                            {
                                nearestReserveDistSq = dSq;
                                nearestReserve = sq;
                            }
                        }

                        if (!hasDefenderAssigned && nearestReserve != null)
                        {
                            _garrisonAssignedObjectiveBySquad[nearestReserve.Pointer] = heldObj.Pointer;
                            nearestReserve.currentTask = heldObj.Cast<SquadTask>();
                            nearestReserve.Leader_OrderAttackCurrentTask(nearestReserve.Leader);
                            MacroGarrisonsAssignedCount++;
                            if (VisionConfig.DebugLogging.Value)
                            {
                                AiCore.LogDebug("MacroDirector", $"{faction} reserve squad '{nearestReserve.squadName}' shifted to defend threatened objective '{heldObj.GetDisplayName()}'.");
                            }
                        }
                    }
                }
                else
                {
                    for (int sIdx = 0; sIdx < eligibleSquads.Count; sIdx++)
                    {
                        _garrisonAssignedObjectiveBySquad.Remove(eligibleSquads[sIdx].Pointer);
                    }
                }
            }
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

                if (!AiCore.IsLocallyOwnedVehicle(veh) || !veh.CanFight() || veh.IsAirVehicle() || veh.PlayerIsInside())
                    continue;

                bool isStaticGun = veh.TryCast<MovableVehicle>() == null;
                bool isParkedArmedTransport = !isStaticGun && veh.IsTransportVehicle() && !veh.HasDriver();
                if (!isStaticGun && !isParkedArmedTransport)
                    continue;

                Turret mainTurret = veh.GetMainTurret(false);
                if (mainTurret == null || mainTurret.WeaponsCount() <= 0 || mainTurret.GetSittedUnit(veh) != null)
                    continue;

                Vector3 vehPos = veh.transform.position;
                Soldier bestCandidate = null;
                float bestDistSq = 45.0f * 45.0f;

                foreach (var kvp in allSquads)
                {
                    Squad sq = kvp.Value;
                    if (sq == null || sq.units == null || sq.IsVehicleCrew)
                        continue;

                    Soldier leader = sq.Leader;
                    if (leader == null || !leader.IsAlive || leader.IsOnVehicle())
                        continue;

                    if ((leader.transform.position - vehPos).sqrMagnitude > 60.0f * 60.0f)
                        continue;

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

                    var units = sq.units;
                    int uCount = units.Count;
                    for (int uIdx = 0; uIdx < uCount; uIdx++)
                    {
                        Soldier u = units[uIdx];
                        if (u == null || !AiCore.IsLocallyOwnedBot(u) || u.IsOnVehicle() || u.IsSquadLeader())
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

                        float dSq = (u.transform.position - vehPos).sqrMagnitude;
                        if (dSq < bestDistSq)
                        {
                            bestDistSq = dSq;
                            bestCandidate = u;
                        }
                    }
                }

                if (bestCandidate != null)
                {
                    _assignedEmplacementUntil[bestCandidate.Pointer] = now + 24.0f;
                    bestCandidate.CoverPosition(veh.Cast<AiDestination>());
                    StaticGunsStaffedCount++;
                    if (VisionConfig.DebugLogging.Value)
                    {
                        AiCore.LogDebug("Emplacement", $"{bestCandidate.faction} soldier assigned to crew empty emplacement/turret ({Mathf.Sqrt(bestDistSq):F0}m away).");
                    }
                }
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
                s.SetPose(SoldierPose.Idle, false);
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
                    if (turretAi.CanSee(target))
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
