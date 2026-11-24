using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace EasyRed2HumanAI
{
    public static partial class VeteranAndTacticsPatches
    {
        private static Spottable FindBestAirborneEnemyAircraft(AIPlane plane, string myFaction, Vector3 planePos, out bool anyEnemyAirborne)
            => FindBestAirborneEnemyAircraft(plane, myFaction, planePos, out anyEnemyAirborne, out _, out _);

        private static Spottable FindBestAirborneEnemyAircraft(
            AIPlane plane,
            string myFaction,
            Vector3 planePos,
            out bool anyEnemyAirborne,
            out float nearestAirDist,
            out bool bestIsBomber)
        {
            anyEnemyAirborne = false;
            nearestAirDist = 99999f;
            bestIsBomber = false;
            Spottable bestTarget = null;
            float bestScore = -99999f;

            IntPtr selfVehPtr = plane != null && plane.veh != null ? plane.veh.Pointer : IntPtr.Zero;
            int count = _airborneAircraft.Count;
            for (int i = 0; i < count; i++)
            {
                AirborneAircraftInfo info = _airborneAircraft[i];
                if (AiCore.AreSameSideFactions(info.Faction, myFaction) ||
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
                float maxAirDist = info.IsBomber ? 4500.0f : 3500.0f;
                if (dist > maxAirDist)
                    continue;

                anyEnemyAirborne = true;
                if (dist < nearestAirDist)
                    nearestAirDist = dist;

                // Prioritize enemy fighters over bombers, and penalize point-blank (< 240m) targets when picking a fresh
                // dogfight target so fighters set up proper lead turns instead of initiating instant head-on merges.
                float score = (info.IsBomber ? 100.0f : 350.0f) - (dist * 0.25f);
                if (dist < 240.0f)
                    score -= 350.0f;
                if (score > bestScore)
                {
                    bestScore = score;
                    bestTarget = info.Spottable;
                    bestIsBomber = info.IsBomber;
                }
            }

            return bestTarget;
        }

        public static void TrySteerAircraftOrbitTowardKnownThreat(AIPlane plane)
        {
            if (plane == null || plane.veh == null || plane.veh.Pointer == IntPtr.Zero || plane.transform == null)
                return;

            if (!AiCore.IsLocallyOwnedVehicle(plane.veh) || AiCore.IsAnyHumanPlayerInsideVehicle(plane.veh))
                return;

            IntPtr vehPtr = plane.veh.Pointer;
            float now = Time.time;
            AiCore.VehicleFastHandles planeVh = AiCore.GetOrCreateVehicleHandles(plane.veh);
            AIPlane.PlaneAIState curPlaneState = plane.planeState;

            if (planeVh != null)
            {
                if (curPlaneState == AIPlane.PlaneAIState.goingTorefill ||
                    curPlaneState == AIPlane.PlaneAIState.doingRefill)
                {
                    if (planeVh.RefillStateEnteredAt <= 0f)
                        planeVh.RefillStateEnteredAt = now;
                    else if (now - planeVh.RefillStateEnteredAt >= 38.0f || plane.nearestRepairStation == null)
                    {
                        // Escape infinite repair-ring circling if the trigger was missed or unreachable
                        planeVh.RefillStateEnteredAt = 0f;
                        planeVh.BombsDepletedAt = 0f;
                        if (plane.veh.CanRefillAmmmo())
                            plane.veh.RefillAllTurrets();
                        if (plane.veh.CanRefillBombBay())
                            plane.veh.RefillBombBaySynched();
                        plane.ExitDive();
                        curPlaneState = plane.planeState;
                    }
                }
                else
                {
                    planeVh.RefillStateEnteredAt = 0f;
                }

                bool orbitingWithoutTarget =
                    curPlaneState == AIPlane.PlaneAIState.flyingAroundArea &&
                    (plane.targetToAttack == null || plane.targetToAttack.Pointer == IntPtr.Zero);
                if (orbitingWithoutTarget)
                {
                    if (planeVh.FlyingAroundAreaSince <= 0f)
                        planeVh.FlyingAroundAreaSince = now;
                }
                else
                {
                    planeVh.FlyingAroundAreaSince = 0f;
                }
            }

            if (planeVh != null &&
                now < planeVh.UnderFireUntil &&
                now >= planeVh.NextUnderFireWakeTime &&
                AiCore.IsValidAliveSpottable(planeVh.UnderFireAttacker) &&
                (plane.PilotGunHasAnyAmmo() || plane.HasBombsToDeliver()) &&
                curPlaneState != AIPlane.PlaneAIState.takeOff &&
                curPlaneState != AIPlane.PlaneAIState.gatheringSpeed &&
                curPlaneState != AIPlane.PlaneAIState.landing &&
                curPlaneState != AIPlane.PlaneAIState.exitDive &&
                curPlaneState != AIPlane.PlaneAIState.goingTorefill &&
                curPlaneState != AIPlane.PlaneAIState.doingRefill &&
                curPlaneState != AIPlane.PlaneAIState.bomberAligning &&
                curPlaneState != AIPlane.PlaneAIState.bomberApproaching &&
                curPlaneState != AIPlane.PlaneAIState.bomberRetreating)
            {
                planeVh.NextUnderFireWakeTime = now + 0.50f;
                Spottable ufAtk = planeVh.UnderFireAttacker;
                string ufFac = GetVehicleCrewFaction(plane.veh);
                if (!string.IsNullOrEmpty(ufFac))
                {
                    if (ufAtk.IsAirVehicle())
                    {
                        VehiclePlane atkPlane = ufAtk.TryCast<VehiclePlane>();
                        if (atkPlane != null &&
                            !atkPlane.isGrounded &&
                            atkPlane.CanFight() &&
                            plane.PilotGunHasAnyAmmo())
                        {
                            Vector3 atkPos = atkPlane.transform != null ? atkPlane.transform.position : ufAtk.GetPosition();
                            bool shouldEngageAirAttacker =
                                curPlaneState == AIPlane.PlaneAIState.flyingAroundArea ||
                                curPlaneState == AIPlane.PlaneAIState.preparingDive;
                            if (!shouldEngageAirAttacker && curPlaneState == AIPlane.PlaneAIState.doingDive)
                            {
                                Spottable diveTgt = plane.targetToAttack;
                                if ((diveTgt == null || !diveTgt.IsAirVehicle()) &&
                                    plane.transform.forward.y > -0.35f &&
                                    (plane.transform.position.y - atkPos.y) > -600f)
                                {
                                    shouldEngageAirAttacker = true;
                                }
                            }
                            else if (!shouldEngageAirAttacker && curPlaneState == AIPlane.PlaneAIState.targetingPlane)
                            {
                                Spottable curAirTgt = plane.targetToAttack;
                                if (curAirTgt == null || curAirTgt.Pointer == IntPtr.Zero)
                                {
                                    shouldEngageAirAttacker = true;
                                }
                                else if (curAirTgt.Pointer != ufAtk.Pointer)
                                {
                                    float ufDist = Vector3.Distance(plane.transform.position, atkPos);
                                    float curDist = Vector3.Distance(plane.transform.position, curAirTgt.GetPosition());
                                    if (curDist > ufDist * 2.2f)
                                        shouldEngageAirAttacker = true;
                                }
                            }

                            if (shouldEngageAirAttacker)
                            {
                                plane.DoAttackTarget(ufAtk);
                                planeVh.FlyingAroundAreaSince = 0f;
                                return;
                            }
                        }
                    }
                    else if (curPlaneState == AIPlane.PlaneAIState.flyingAroundArea)
                    {
                        Spottable airThreat = FindBestAirborneEnemyAircraft(plane, ufFac, plane.transform.position, out bool anyEnemyAir, out float nearestAirDist, out bool bestAirBomber);
                        bool hasBombs = plane.HasBombsToDeliver();
                        bool hasApCannons = HasAircraftAntiArmorCannons(plane, out float bestPenMm);
                        bool emergentAirOn = VisionConfig.EnableEmergentDecisionEngine != null && VisionConfig.EnableEmergentDecisionEngine.Value;
                        if (CanAircraftCounterAttackGroundUnit(
                                ufFac,
                                plane.transform.position,
                                plane.transform.root,
                                ufAtk,
                                hasBombs,
                                hasApCannons,
                                bestPenMm,
                                out float ufDist))
                        {
                            bool allowCounter = !anyEnemyAir;
                            if (emergentAirOn &&
                                EmergentRuntimeBridge.TryEvaluateEmergentAirDecision(
                                    plane,
                                    airThreat,
                                    bestAirBomber,
                                    nearestAirDist,
                                    hasGroundTarget: true,
                                    Mathf.Clamp01(0.75f - ufDist * 0.0005f),
                                    groundIsArmorOrCluster: true,
                                    hasApCannons,
                                    out var airDec))
                            {
                                allowCounter = airDec.Action == EmergentEngine.AirActionId.AttackGround;
                            }

                            if (allowCounter)
                            {
                                plane.DoAttackTarget(ufAtk);
                                planeVh.FlyingAroundAreaSince = 0f;
                                return;
                            }
                        }
                    }
                }
            }

            if (planeVh != null && now < planeVh.NextPlaneOrbitSteerTime)
                return;

            if (planeVh != null)
                planeVh.NextPlaneOrbitSteerTime = now + 2.0f;

            string myFaction = GetVehicleCrewFaction(plane.veh);
            if (string.IsNullOrEmpty(myFaction))
                return;

            RefreshCombinedArmsIntelIfNeeded();

            // Fix vanilla AIPlane.Update (0x180575BD7) BomberDestination bug:
            // PlaneType.Bomber aircraft spawned via standard SpawnManagers lack BomberDestination,
            // causing AIPlane.Update to jump to SetFlyAroundConquerArea (planeState = 7) and circle forever!
            if (plane.veh.planeType == PlaneType.Bomber &&
                !plane.veh.BomberDestination.HasValue &&
                plane.veh.CountBombs() > 0)
            {
                var calcDest = plane.veh.CalculateBomberTarget(false);
                if (calcDest.HasValue)
                {
                    plane.veh.BomberDestination = calcDest;
                }
                else if (_knownEnemyClusters.Count > 0)
                {
                    for (int c = 0; c < _knownEnemyClusters.Count; c++)
                    {
                        KnownEnemyCluster cl = _knownEnemyClusters[c];
                        if (!AiCore.AreSameSideFactions(cl.Faction, myFaction))
                        {
                            float bAlt = Mathf.Max(plane.transform.position.y, cl.Position.y + 380.0f);
                            plane.veh.BomberDestination = new Il2CppSystem.Nullable<Vector3>(
                                new Vector3(cl.Position.x, bAlt, cl.Position.z));
                            break;
                        }
                    }
                }
            }

            bool emergentAir = VisionConfig.EnableEmergentDecisionEngine != null && VisionConfig.EnableEmergentDecisionEngine.Value;
            if (emergentAir)
            {
                if (plane.planeState != AIPlane.PlaneAIState.flyingAroundArea)
                    return;

                if (!plane.PilotGunHasAnyAmmo() && !plane.HasBombsToDeliver())
                    return;

                Spottable emergentTarget = null;
                TrySelectKnownAircraftTarget(plane, ref emergentTarget);
                if (emergentTarget == null || emergentTarget.Pointer == IntPtr.Zero)
                    return;

                if (emergentTarget.IsAirVehicle())
                {
                    if (AIPlane.fighterDogfightExtraLeash < 1800.0f)
                        AIPlane.fighterDogfightExtraLeash = 1800.0f;

                    Spottable curAtk = plane.targetToAttack;
                    bool alreadyTargetingAir = curAtk != null && curAtk.Pointer != IntPtr.Zero && curAtk.IsAirVehicle();
                    if (!alreadyTargetingAir && plane.veh.planeType != PlaneType.Bomber && plane.PilotGunHasAnyAmmo())
                    {
                        planeVh.FlyingAroundAreaSince = 0f;
                        plane.DoAttackTarget(emergentTarget);
                        if (TryRecordCombinedArmsAssist(vehPtr, now))
                        {
                            AiCore.LogDebug("CombArmsAirSup", $"{myFaction} fighter intercepted enemy aircraft via H.A.R.M.");
                        }
                    }
                }
                else
                {
                    bool canLaunchCasBreakout = (plane.veh.planeType == PlaneType.Fighter || (!plane.veh.BomberDestination.HasValue && plane.HasBombsToDeliver())) &&
                                                (plane.targetToAttack == null || plane.targetToAttack.Pointer == IntPtr.Zero) &&
                                                planeVh != null &&
                                                planeVh.FlyingAroundAreaSince > 0f &&
                                                (now - planeVh.FlyingAroundAreaSince) >= 2.0f;
                    if (canLaunchCasBreakout)
                    {
                        planeVh.FlyingAroundAreaSince = 0f;
                        plane.DoAttackTarget(emergentTarget);
                    }
                }
                return;
            }

            // Legacy Air Superiority First ("Clear Skies Only" Ground Support) when H.A.R.M. is toggled OFF:
            Spottable bestEnemyAir = FindBestAirborneEnemyAircraft(plane, myFaction, plane.transform.position, out bool anyEnemyAirborne);
            if (anyEnemyAirborne)
            {
                if (AIPlane.fighterDogfightExtraLeash < 1800.0f)
                {
                    AIPlane.fighterDogfightExtraLeash = 1800.0f;
                }

                if (bestEnemyAir != null &&
                    plane.PilotGunHasAnyAmmo() &&
                    plane.planeState == AIPlane.PlaneAIState.flyingAroundArea)
                {
                    Spottable curAtk = plane.targetToAttack;
                    bool alreadyTargetingAir = curAtk != null && curAtk.Pointer != IntPtr.Zero && curAtk.IsAirVehicle();
                    if (!alreadyTargetingAir)
                    {
                        plane.DoAttackTarget(bestEnemyAir);
                        if (TryRecordCombinedArmsAssist(vehPtr, now))
                        {
                            AiCore.LogDebug(
                                "CombArmsAirSup",
                                $"{myFaction} aircraft engaged airborne enemy aircraft from patrol.");
                        }
                    }
                }
                return;
            }

            if (plane.planeState != AIPlane.PlaneAIState.flyingAroundArea)
                return;

            // Break out of "vulture circling": if an armed fighter/CAS plane has been orbiting in flyingAroundArea
            // for >= 2.5s without acquiring a target (because vanilla's 200m tangential bank keeps the orbit center
            // 90 degrees off-bore!), directly select a valid ground target and initiate DoAttackTarget!
            if (plane.veh.planeType == PlaneType.Fighter &&
                (plane.targetToAttack == null || plane.targetToAttack.Pointer == IntPtr.Zero) &&
                (plane.PilotGunHasAnyAmmo() || plane.HasBombsToDeliver()) &&
                planeVh != null &&
                planeVh.FlyingAroundAreaSince > 0f &&
                (now - planeVh.FlyingAroundAreaSince) >= 2.5f)
            {
                Spottable casTarget = null;
                TrySelectKnownAircraftTarget(plane, ref casTarget);
                if (casTarget != null && casTarget.Pointer != IntPtr.Zero)
                {
                    planeVh.FlyingAroundAreaSince = 0f;
                    plane.DoAttackTarget(casTarget);
                }
            }
        }

        /// <summary>
        /// Zero-allocation squad-level danger-close check for AI aircraft CAS target selection and bomb release.
        /// Pilots recognize friendly squad formations (>= 2 alive members, friendly ground vehicles, or player squads)
        /// rather than lone individual stragglers, waving off bomb drops within 38m (or gun strafes within 18m) of a friendly squad.
        /// </summary>
        public static bool IsAircraftTargetDangerCloseToFriendlySquad(
            AIPlane plane,
            Vector3 targetPos,
            bool hasBombsToDeliver)
        {
            if (plane == null || plane.veh == null)
                return false;
            return IsAircraftTargetDangerCloseToFriendlySquad(GetVehicleCrewFaction(plane.veh), targetPos, hasBombsToDeliver);
        }

        public static bool IsAircraftTargetDangerCloseToFriendlySquad(
            string myFaction,
            Vector3 targetPos,
            bool hasBombsToDeliver)
        {
            if (string.IsNullOrEmpty(myFaction))
                return false;

            AiCore.EnsureWorldSnapshot();
            int sqCount = AiCore.WorldSquadCount;
            if (sqCount <= 0)
                return false;

            float safeRadius = hasBombsToDeliver ? 26.0f : 15.0f;
            float safeRadiusSq = safeRadius * safeRadius;
            float maxVertDelta = hasBombsToDeliver ? 28.0f : 20.0f;
            var squads = AiCore.WorldSquads;

            for (int s = 0; s < sqCount; s++)
            {
                ref AiCore.SquadSnapshot sqSnap = ref squads[s];
                if (sqSnap.RefOnPlane ||
                    string.IsNullOrEmpty(sqSnap.Faction) ||
                    !AiCore.AreSameSideFactions(myFaction, sqSnap.Faction))
                {
                    continue;
                }

                // Pilots identify friendly squad formations (>= 2 soldiers), friendly ground vehicles, or player squads
                if (sqSnap.UnitCount < 2 && !sqSnap.LeaderOnGroundVehicle && !sqSnap.IsPlayerInSquad)
                    continue;

                Vector3 d = sqSnap.RefPos - targetPos;
                if (d.x * d.x + d.z * d.z <= safeRadiusSq && Mathf.Abs(d.y) <= maxVertDelta)
                    return true;
            }

            return false;
        }

        private static bool HasAircraftAntiArmorCannons(AIPlane plane, out float bestPenMm)
        {
            bestPenMm = 0f;
            if (plane == null || plane.veh == null)
                return false;

            try
            {
                TurretGun pilotTurret = plane.veh.GetPilotTurret();
                if (pilotTurret == null || pilotTurret.weapons == null)
                    return false;

                InventoryManager inv = plane.veh.GetInventory();
                var weapons = pilotTurret.weapons;
                for (int wIdx = 0; wIdx < weapons.Length; wIdx++)
                {
                    TurretWeapon w = weapons[wIdx];
                    if (w == null || w.ammos == null)
                        continue;

                    bool refreshedInv = false;
                    var ammos = w.ammos;
                    for (int aIdx = 0; aIdx < ammos.Length; aIdx++)
                    {
                        if (!HasTurretWeaponAmmoInSlot(w, aIdx, inv, ref refreshedInv))
                            continue;

                        BulletData bd = w.GetBulletDataFor(aIdx) ?? w.BulletData;
                        if (bd == null)
                            continue;

                        float pen = Mathf.Max(bd.CalculateMmPenetrationWithFallof(250f), bd.explosion_penetration);
                        if (pen > bestPenMm)
                            bestPenMm = pen;
                    }
                }
            }
            catch { }
            return bestPenMm >= 22.0f;
        }

        private static bool CanAircraftCounterAttackGroundUnit(
            string myFaction,
            Vector3 planePos,
            Transform planeRoot,
            Spottable target,
            bool hasBombs,
            bool hasAntiArmorCannons,
            float bestPlanePenMm,
            out float dist)
        {
            dist = 0f;
            if (!AiCore.IsValidAliveSpottable(target) || target.IsAirVehicle())
                return false;

            Vector3 targetPos = target.GetCenterOfUnit();
            Vector3 toTarget = targetPos - planePos;
            dist = toTarget.magnitude;
            if (dist < 95.0f || dist > 1150.0f)
                return false;

            if (IsAircraftTargetDangerCloseToFriendlySquad(myFaction, targetPos, hasBombs))
                return false;

            if (target.IsWheeledVehicleOrTank())
            {
                Vehicle v = target.TryCast<Vehicle>();
                if (v != null)
                {
                    float armorMm = GetVehicleMaxHullArmorMm(v);
                    bool canPierceTopOrSide = hasAntiArmorCannons && bestPlanePenMm >= armorMm * 0.40f;
                    if (!hasBombs && !v.IsStatic() && armorMm >= 12.0f && !canPierceTopOrSide)
                        return false;
                }
            }

            return !AiCore.RaycastEnvironment(planePos, toTarget / dist, dist - 4.0f, planeRoot, out _);
        }

        public static void TrySelectKnownAircraftTarget(AIPlane plane, ref Spottable currentResult)
        {
            if (plane == null || plane.veh == null || plane.transform == null)
                return;

            bool emergentOn = VisionConfig.EnableEmergentDecisionEngine != null && VisionConfig.EnableEmergentDecisionEngine.Value;

            if (!emergentOn)
            {
                // Air-to-air dogfights always retain top priority
                if (currentResult != null && currentResult.Pointer != IntPtr.Zero && currentResult.IsAirVehicle())
                    return;
            }

            string myFaction = GetVehicleCrewFaction(plane.veh);
            if (string.IsNullOrEmpty(myFaction))
                return;

            RefreshCombinedArmsIntelIfNeeded();

            AiCore.VehicleFastHandles planeVh = AiCore.GetOrCreateVehicleHandles(plane.veh);
            Vector3 planePos = plane.transform.position;

            Spottable bestEnemyAir = FindBestAirborneEnemyAircraft(plane, myFaction, planePos, out bool anyEnemyAirborne, out float nearestAirDist, out bool bestAirIsBomber);
            if (!emergentOn)
            {
                if (anyEnemyAirborne)
                {
                    if (bestEnemyAir != null && plane.PilotGunHasAnyAmmo())
                    {
                        currentResult = bestEnemyAir;
                        if (TryRecordCombinedArmsAssist(plane.veh.Pointer, Time.time))
                        {
                            AiCore.LogDebug(
                                "CombArmsAirSup",
                                $"{myFaction} aircraft detected enemy plane in airspace; prioritizing dogfight over ground attack.");
                        }
                    }
                    else if (!IsJapaneseKamikazeActive(plane))
                    {
                        currentResult = null;
                    }
                    return;
                }
            }
            else if (currentResult != null && currentResult.Pointer != IntPtr.Zero && currentResult.IsAirVehicle() && bestEnemyAir == null)
            {
                bestEnemyAir = currentResult;
                anyEnemyAirborne = true;
                nearestAirDist = Vector3.Distance(planePos, currentResult.GetCenterOfUnit());
                VehiclePlane vp = currentResult.TryCast<VehiclePlane>();
                bestAirIsBomber = vp != null && vp.planeType == PlaneType.Bomber;
            }

            Vector3 planeFwd = plane.transform.forward;
            Transform planeRoot = plane.transform.root;
            IntPtr selfVehPtr = plane.veh.Pointer;

            const float effectiveMaxDist = 950.0f;
            // In flyingAroundArea (state 7), vanilla banks -90 degrees tangentially inside 200m of flyTargetPosition
            // (0x180577001), keeping targets inside the orbit 75-115 degrees off the nose! Because DoAttackTarget
            // immediately transitions to preparingDive (300m climb-out standoff), allow 135-deg roll-in acquisition.
            bool isOrbitingArea = plane.planeState == AIPlane.PlaneAIState.flyingAroundArea;
            float effectiveMaxAngle = isOrbitingArea ? 135.0f : 62.0f;

            Spottable groundBase = (currentResult != null && AiCore.IsValidAliveSpottable(currentResult) && !currentResult.IsAirVehicle())
                ? currentResult
                : null;
            float curScore = groundBase != null
                ? EvaluateExistingTargetPriority(groundBase, myFaction, planePos, true)
                : -999f;

            Spottable bestCandidate = null;
            float bestScore = curScore + 15.0f;
            bool bestIsArmorOrCluster = groundBase != null && groundBase.IsWheeledVehicleOrTank();

            bool limitUnmarkedInfantry = VisionConfig.LimitAircraftInfantrySpotting.Value;
            float altitudeCompensation = Mathf.Max(0f, planePos.y) * 1.25f;
            float unmarkedAwareCap = Mathf.Min(
                effectiveMaxDist,
                Mathf.Max(VisionConfig.AircraftUnmarkedInfantryMaxDist.Value * 2.5f + altitudeCompensation, 680.0f));
            bool hasLiveBombsNow = plane.HasBombsToDeliver();
            bool hasAntiArmorCannons = HasAircraftAntiArmorCannons(plane, out float bestPlanePenMm);

            float now = Time.time;
            if (planeVh != null &&
                now < planeVh.UnderFireUntil &&
                AiCore.IsValidAliveSpottable(planeVh.UnderFireAttacker) &&
                !planeVh.UnderFireAttacker.IsAirVehicle())
            {
                Spottable ufCand = planeVh.UnderFireAttacker;
                if (CanAircraftCounterAttackGroundUnit(
                        myFaction,
                        planePos,
                        planeRoot,
                        ufCand,
                        hasLiveBombsNow,
                        hasAntiArmorCannons,
                        bestPlanePenMm,
                        out float ufDist))
                {
                    float ufScore = 125.0f - (ufDist * 0.05f);
                    if (ufScore > bestScore)
                    {
                        bestScore = ufScore;
                        bestCandidate = ufCand;
                        bestIsArmorOrCluster = ufCand.IsWheeledVehicleOrTank();
                    }
                }
            }

            int clusterCount = _knownEnemyClusters.Count;
            for (int i = 0; i < clusterCount; i++)
            {
                KnownEnemyCluster cluster = _knownEnemyClusters[i];
                if (AiCore.AreSameSideFactions(cluster.Faction, myFaction))
                    continue;

                bool isMarkedOrRadio = cluster.IsMarkedOrPinPoint || cluster.IsHalfMarked || cluster.NearRadioRequest;
                for (int pass = 0; pass < 2; pass++)
                {
                    Spottable cand = pass == 0 ? cluster.VehicleTarget : cluster.PrimarySoldier;
                    if (!AiCore.IsValidAliveSpottable(cand))
                        continue;

                    bool isVehCand = pass == 0;
                    if (!isVehCand && limitUnmarkedInfantry && !isMarkedOrRadio && !cluster.IsEngagingFriendlies)
                        continue;

                    float allowedDist = (!isVehCand && limitUnmarkedInfantry && !isMarkedOrRadio)
                        ? unmarkedAwareCap
                        : effectiveMaxDist;

                    Vector3 candPos = cand.GetCenterOfUnit();
                    Vector3 toCand = candPos - planePos;
                    float dist = toCand.magnitude;
                    if (dist < 95.0f || dist > allowedDist)
                        continue;

                    // Avoid danger-close CAS runs on enemy targets that have a friendly squad right on top of them
                    if (IsAircraftTargetDangerCloseToFriendlySquad(myFaction, candPos, hasLiveBombsNow))
                        continue;

                    Vector3 dirToCand = toCand / dist;
                    if (Vector3.Angle(planeFwd, dirToCand) > effectiveMaxAngle)
                        continue;

                    float vehBonus = 0f;
                    if (isVehCand)
                    {
                        Vehicle candVeh = cand.TryCast<Vehicle>();
                        float armorMm = candVeh != null ? GetVehicleMaxHullArmorMm(candVeh) : 0f;
                        if (armorMm >= 12.0f)
                        {
                            bool canPierceTopOrSide = bestPlanePenMm >= armorMm * 0.40f;
                            if (!hasLiveBombsNow && !canPierceTopOrSide)
                                continue;
                            vehBonus = hasLiveBombsNow ? 30.0f : (hasAntiArmorCannons ? 35.0f : 8.0f);
                            if (cluster.IsEngagingFriendlies || cluster.IsMarkedOrPinPoint || cluster.NearRadioRequest)
                            {
                                vehBonus += 32.0f;
                                if (cluster.IsEngagingFriendlies && hasLiveBombsNow)
                                    vehBonus += 10.0f;
                            }
                        }
                        else
                        {
                            vehBonus = 24.0f;
                        }
                    }

                    // Per-plane deterministic tie-breaker so multiple CAS aircraft spread attacks across clusters
                    float spreadBonus = (AiCore.GetDeterministicHash(selfVehPtr, i + 17) & 7) * 2.5f;
                    float candScore = cluster.BasePriority + vehBonus + spreadBonus - (dist * 0.06f);
                    if (candScore <= bestScore)
                        continue;

                    // Verify unobstructed air-to-ground line of sight
                    if (AiCore.RaycastEnvironment(planePos, dirToCand, dist - 4.0f, planeRoot, out _))
                        continue;

                    bestScore = candScore;
                    bestCandidate = cand;
                    bestIsArmorOrCluster = isVehCand || cluster.SecondarySoldier != null || cluster.IsEngagingFriendlies;
                    break;
                }
            }

            // Fallback for island / infantry-defense maps (e.g. Battle of Makin) when orbiting with no target:
            // scan WorldSquads for any exposed/standing or combat-aware enemy squad or ground vehicle in LOS.
            if (bestCandidate == null && groundBase == null && (isOrbitingArea || (emergentOn && hasLiveBombsNow)))
            {
                AiCore.EnsureWorldSnapshot();
                int sqCount = AiCore.WorldSquadCount;
                var squads = AiCore.WorldSquads;
                for (int s = 0; s < sqCount; s++)
                {
                    ref AiCore.SquadSnapshot sqSnap = ref squads[s];
                    if (!sqSnap.LeaderIsAlive ||
                        sqSnap.RefOnPlane ||
                        string.IsNullOrEmpty(sqSnap.Faction) ||
                        AiCore.AreSameSideFactions(myFaction, sqSnap.Faction))
                    {
                        continue;
                    }

                    Soldier leader = sqSnap.Leader;
                    if (leader == null)
                        continue;

                    Spottable cand = null;
                    bool isVeh = false;
                    if (sqSnap.LeaderOnGroundVehicle)
                    {
                        Vehicle v = leader.GetCurrentVehicle();
                        if (v != null && v.CanFight())
                        {
                            cand = v.TryCast<Spottable>();
                            isVeh = true;
                        }
                    }

                    if (cand == null)
                    {
                        Squad sq = leader.joinedSquad;
                        bool sqKnownOrEngaged = sq != null &&
                            (sq.isMarked || sq.isHalfMarked || sq.isMarkedPinPoint || sq.IsEnemyAwareOfThisSquad);
                        if (limitUnmarkedInfantry &&
                            !sqKnownOrEngaged &&
                            leader.m_pose == SoldierPose.Prone)
                        {
                            continue;
                        }
                        cand = leader.TryCast<Spottable>();
                    }

                    if (!AiCore.IsValidAliveSpottable(cand))
                        continue;

                    Vector3 candPos = cand.GetCenterOfUnit();
                    Vector3 toCand = candPos - planePos;
                    float dist = toCand.magnitude;
                    float maxD = isVeh ? effectiveMaxDist : unmarkedAwareCap;
                    if (dist < 95.0f || dist > maxD)
                        continue;

                    if (IsAircraftTargetDangerCloseToFriendlySquad(myFaction, candPos, hasLiveBombsNow))
                        continue;

                    Vector3 dirToCand = toCand / dist;
                    if (Vector3.Angle(planeFwd, dirToCand) > effectiveMaxAngle)
                        continue;

                    float spreadBonus = (AiCore.GetDeterministicHash(selfVehPtr, s + 53) & 7) * 3.0f;
                    float candScore = (isVeh ? 45.0f : 22.0f) + spreadBonus - (dist * 0.05f);
                    if (candScore <= bestScore)
                        continue;

                    if (AiCore.RaycastEnvironment(planePos, dirToCand, dist - 4.0f, planeRoot, out _))
                        continue;

                    bestScore = candScore;
                    bestCandidate = cand;
                    bestIsArmorOrCluster = isVeh || sqSnap.UnitCount >= 3;
                }
            }

            float casNow = Time.time;
            if (planeVh != null &&
                casNow < planeVh.CasLockedTargetUntil &&
                AiCore.IsValidAliveSpottable(planeVh.CasLockedTarget) &&
                !IsAircraftTargetDangerCloseToFriendlySquad(myFaction, planeVh.CasLockedTarget.GetCenterOfUnit(), hasLiveBombsNow))
            {
                groundBase = planeVh.CasLockedTarget;
                bestScore = curScore;
            }
            else if (bestCandidate != null && (groundBase == null || bestCandidate.Pointer != groundBase.Pointer))
                groundBase = bestCandidate;
            else if (groundBase != null)
                bestScore = curScore;

            if (emergentOn && !IsJapaneseKamikazeActive(plane))
            {
                float groundScore01 = groundBase != null ? Mathf.Clamp01(Mathf.Max(20f, bestScore) / 120f) : 0f;
                if (EmergentRuntimeBridge.TryEvaluateEmergentAirDecision(
                        plane,
                        bestEnemyAir,
                        bestAirIsBomber,
                        nearestAirDist,
                        groundBase != null,
                        groundScore01,
                        bestIsArmorOrCluster,
                        hasAntiArmorCannons,
                        out var airDec))
                {
                    if (airDec.Action == EmergentEngine.AirActionId.InterceptAir && bestEnemyAir != null)
                    {
                        if (planeVh != null)
                        {
                            planeVh.CasLockedTarget = null;
                            planeVh.CasLockedTargetUntil = 0f;
                        }
                        if (AIPlane.fighterDogfightExtraLeash < 1800.0f)
                            AIPlane.fighterDogfightExtraLeash = 1800.0f;
                        currentResult = bestEnemyAir;
                        if (TryRecordCombinedArmsAssist(plane.veh.Pointer, Time.time))
                        {
                            AiCore.LogDebug("CombArmsAirSup", $"{myFaction} aircraft selected {airDec.Reason} over ground CAS via H.A.R.M.");
                        }
                        return;
                    }
                    if (airDec.Action == EmergentEngine.AirActionId.AttackGround && groundBase != null)
                    {
                        if (planeVh != null && (planeVh.CasLockedTarget == null || planeVh.CasLockedTarget.Pointer != groundBase.Pointer || casNow >= planeVh.CasLockedTargetUntil))
                        {
                            planeVh.CasLockedTarget = groundBase;
                            planeVh.CasLockedTargetUntil = casNow + 12.0f;
                        }
                        if (currentResult == null || groundBase.Pointer != currentResult.Pointer)
                        {
                            currentResult = groundBase;
                            if (TryRecordCombinedArmsAssist(plane.veh.Pointer, Time.time))
                            {
                                AiCore.LogDebug("CombArmsCAS", $"{myFaction} aircraft prioritized ground CAS ({airDec.Reason}) via H.A.R.M.");
                            }
                        }
                        return;
                    }
                    if (airDec.Action == EmergentEngine.AirActionId.RefillOrEvade)
                    {
                        if (planeVh != null)
                        {
                            planeVh.CasLockedTarget = null;
                            planeVh.CasLockedTargetUntil = 0f;
                        }
                        currentResult = (!hasLiveBombsNow && !plane.PilotGunHasAnyAmmo()) ? null : (groundBase ?? bestEnemyAir);
                        return;
                    }
                }
            }

            if (groundBase != null && (currentResult == null || groundBase.Pointer != currentResult.Pointer))
            {
                if (planeVh != null && (planeVh.CasLockedTarget == null || planeVh.CasLockedTarget.Pointer != groundBase.Pointer || casNow >= planeVh.CasLockedTargetUntil))
                {
                    planeVh.CasLockedTarget = groundBase;
                    planeVh.CasLockedTargetUntil = casNow + 12.0f;
                }
                currentResult = groundBase;
                if (TryRecordCombinedArmsAssist(plane.veh.Pointer, Time.time))
                {
                    AiCore.LogDebug(
                        "CombArmsCAS",
                        $"{myFaction} aircraft prioritized known/marked ground target for CAS run.");
                }
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
                   AiCore.TryGetVehicleHandles(plane.veh.Pointer, out AiCore.VehicleFastHandles vh) &&
                   vh.IsActiveKamikaze;
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

            AiCore.VehicleFastHandles vh = AiCore.GetOrCreateVehicleHandles(plane.veh);
            if (plane.veh.isGrounded ||
                !plane.veh.CanFight() ||
                !plane.HasAIDriver ||
                AiCore.IsAnyHumanPlayerInsideVehicle(plane.veh) ||
                !AiCore.IsLocallyOwnedVehicle(plane.veh))
            {
                if (vh != null)
                    vh.IsActiveKamikaze = false;
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

            if (vh != null && vh.IsActiveKamikaze)
                return true;

            // Kamikaze is strictly a last-resort sacrifice for severely crippled Japanese aircraft:
            // dropping bombs or expending forward gun ammo NEVER triggers kamikaze (intact planes RTB/refill normally).
            bool severelyDamaged =
                plane.veh.GetDamage() >= 80 ||
                plane.veh.hasMissingParts ||
                !plane.veh.useWingsL ||
                !plane.veh.useWingsR;

            if (!severelyDamaged)
                return false;

            if (vh != null && vh.KamikazeDecisionMade)
                return vh.KamikazeCommitted;

            SoldierPersonality pilotPers = GetVehicleCrewPersonality(plane.veh);
            float chance = pilotPers == SoldierPersonality.Pilot_Disciplined
                ? 0f
                : (pilotPers == SoldierPersonality.Pilot_Daredevil ? 0.55f : 0.30f);
            bool committed = chance > 0f && UnityEngine.Random.value < chance;
            if (vh != null)
            {
                vh.KamikazeDecisionMade = true;
                vh.KamikazeCommitted = committed;
            }
            return committed;
        }

        private static Spottable FindBestKamikazeTarget(AIPlane plane, string myFaction)
        {
            RefreshCombinedArmsIntelIfNeeded();

            Vector3 planePos = plane.transform.position;
            Spottable bestTarget = null;
            float bestScore = -99999f;

            // 1. Highest Priority: Operational enemy tanks and ground combat vehicles
            int gvCount = _activeGroundVehicles.Count;
            for (int i = 0; i < gvCount; i++)
            {
                ActiveGroundVehicleInfo gv = _activeGroundVehicles[i];
                if (string.IsNullOrEmpty(gv.Faction) ||
                    AiCore.AreSameSideFactions(myFaction, gv.Faction) ||
                    gv.Veh == null ||
                    gv.Veh.Pointer == IntPtr.Zero ||
                    !gv.Veh.CanFight())
                {
                    continue;
                }

                Spottable vSpot = gv.Veh.TryCast<Spottable>();
                if (!IsSpottableTargetValid(vSpot))
                    continue;

                float dist = Vector3.Distance(planePos, gv.Position);
                if (dist > 2800.0f)
                    continue;

                float score = (gv.IsTank ? 320.0f : 230.0f) - (dist * 0.05f);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestTarget = vSpot;
                }
            }

            // 2. Radio-spotted / known enemy clusters (armor/vehicles heavily favored; infantry strictly last resort)
            int clusterCount = _knownEnemyClusters.Count;
            for (int i = 0; i < clusterCount; i++)
            {
                KnownEnemyCluster cluster = _knownEnemyClusters[i];
                if (AiCore.AreSameSideFactions(myFaction, cluster.Faction))
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

                    float score;
                    if (pass == 0)
                    {
                        float vehBase = cand.TryCast<VehicleTank>() != null ? 320.0f : 235.0f;
                        score = vehBase + Mathf.Min(cluster.BasePriority, 45.0f) - (dist * 0.05f);
                    }
                    else
                    {
                        // Infantry clusters score well below any enemy armor, vehicle, bomber, or fighter
                        score = Mathf.Min(cluster.BasePriority, 25.0f) + 15.0f - (dist * 0.06f);
                    }

                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestTarget = cand;
                    }
                }
            }

            // 3. Airborne enemy bombers & fighters (Shinten Seikūtai aerial ramming — always preferred over infantry)
            int airCount = _airborneAircraft.Count;
            IntPtr selfVehPtr = plane.veh.Pointer;
            for (int i = 0; i < airCount; i++)
            {
                AirborneAircraftInfo info = _airborneAircraft[i];
                if (AiCore.AreSameSideFactions(myFaction, info.Faction) ||
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

                Vector3 airPos = info.Plane.transform != null ? info.Plane.transform.position : info.Position;
                float dist = Vector3.Distance(planePos, airPos);
                if (dist > 2400.0f)
                    continue;

                float score = (info.IsBomber ? 275.0f : 210.0f) - (dist * 0.05f);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestTarget = info.Spottable;
                }
            }

            // 4. Direct visual scan of battlefield squads if no high-value armor/plane was found yet
            if (bestTarget == null || bestScore < 120.0f)
            {
                AiCore.EnsureWorldSnapshot();
                int sqCount = AiCore.WorldSquadCount;
                var squads = AiCore.WorldSquads;
                for (int s = 0; s < sqCount; s++)
                {
                    ref AiCore.SquadSnapshot sqSnap = ref squads[s];
                    if (!sqSnap.LeaderIsAlive || string.IsNullOrEmpty(sqSnap.Faction) || AiCore.AreSameSideFactions(myFaction, sqSnap.Faction))
                        continue;

                    Soldier leader = sqSnap.Leader;
                    if (leader == null)
                        continue;

                    Vehicle mountedVeh = sqSnap.LeaderOnVehicle ? leader.GetCurrentVehicle() : null;
                    Spottable cand = null;
                    float baseVal = 12.0f;

                    if (mountedVeh != null && mountedVeh.CanFight() && !mountedVeh.IsAirVehicle())
                    {
                        cand = mountedVeh.Cast<Spottable>();
                        baseVal = mountedVeh.TryCast<VehicleTank>() != null ? 310.0f : 220.0f;
                    }
                    else if (bestTarget == null)
                    {
                        cand = leader.Cast<Spottable>();
                    }

                    if (cand == null || cand.Pointer == IntPtr.Zero)
                        continue;

                    float dist = Vector3.Distance(planePos, sqSnap.RefPos);
                    if (dist > 2800.0f)
                        continue;

                    float score = baseVal - (dist * (baseVal >= 200f ? 0.05f : 0.06f));
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestTarget = cand;
                    }
                }
            }

            return bestTarget;
        }

        public static void TryExecuteJapaneseKamikazeDive(AIPlane plane, bool isPostfix)
        {
            if (!IsJapaneseKamikazeCandidate(plane, out string myFaction))
                return;

            AiCore.VehicleFastHandles vh = AiCore.GetOrCreateVehicleHandles(plane.veh);
            float now = Time.time;

            if (vh != null && !vh.IsActiveKamikaze)
            {
                vh.IsActiveKamikaze = true;
                KamikazeDivesTriggeredCount++;
                if (VisionConfig.DebugLogging.Value)
                {
                    AiCore.LogDebug(
                        "Kamikaze",
                        $"Imperial Japanese aircraft (dmg:{plane.veh.GetDamage()}, missingParts:{plane.veh.hasMissingParts}) committed to final Tokkōtai kamikaze dive!");
                }
            }

            Spottable target = plane.targetToAttack;
            bool needNewTarget = !IsSpottableTargetValid(target);

            if (needNewTarget ||
                (plane.planeState != AIPlane.PlaneAIState.doingDive &&
                 (vh == null || now >= vh.NextKamikazeTargetSearchTime)))
            {
                if (vh != null)
                    vh.NextKamikazeTargetSearchTime = now + 1.5f;
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
                // Aerial ramming (Shinten Seikūtai) with velocity lead intercept
                plane.planeState = AIPlane.PlaneAIState.doingDive;
                plane.exitDiveEndTime = 0f;
                Vehicle airVeh = target.TryCast<Vehicle>();
                float airDist = Vector3.Distance(planePos, targetPos);
                Vector3 leadOffset = Vector3.zero;
                if (airVeh != null)
                {
                    MovableVehicle airMv = airVeh.TryCast<MovableVehicle>();
                    if (airMv != null && airMv.rb != null && airMv.rb.velocity.sqrMagnitude > 4f)
                        leadOffset = airMv.rb.velocity * Mathf.Clamp(airDist / 95.0f, 0.15f, 1.35f);
                    else if (airVeh.transform != null)
                        leadOffset = airVeh.transform.forward * 12.0f;
                }
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
            Vector3 horizDir = horizDist > 0.5f ? (flatDelta / horizDist) : Vector3.forward;
            float heightAboveTarget = planePos.y - targetPos.y;

            // Sample terrain elevation ahead along the approach so crippled planes climb over ridges instead of smashing short into hills
            float terrMid = ResourcesManager.GetTerrainAltitude(planePos + horizDir * Mathf.Min(horizDist * 0.50f, 180.0f), 0f);
            float terrFar = ResourcesManager.GetTerrainAltitude(planePos + horizDir * Mathf.Min(horizDist * 0.82f, 360.0f), 0f);
            float maxTerrainAheadY = Mathf.Max(targetPos.y, Mathf.Max(terrMid, terrFar));

            Vector3 toTarget3D = (targetPos + Vector3.up * 2.5f) - planePos;
            float dist3D = toTarget3D.magnitude;
            bool losBlockedByRidge =
                horizDist > 140.0f &&
                dist3D > 16.0f &&
                AiCore.RaycastEnvironment(planePos, toTarget3D / dist3D, dist3D - 12.0f, plane.transform.root, out _);

            if ((horizDist > 165.0f && heightAboveTarget < horizDist * 0.72f) ||
                planePos.y < maxTerrainAheadY + 75.0f ||
                losBlockedByRidge)
            {
                // Stage 1: Climb to high roll-in apex over the target so the steep terminal dive clears all terrain
                plane.planeState = AIPlane.PlaneAIState.preparingDive;
                float minSafeApexY = Mathf.Max(targetPos.y + 245.0f, Mathf.Max(maxTerrainAheadY + 145.0f, planePos.y + 20.0f));
                plane.flyTargetPosition = new Vector3(targetPos.x, minSafeApexY, targetPos.z);
            }
            else
            {
                // Stage 2: Steep terminal dive with gravity-droop compensation above 90m so the aircraft impacts the target instead of the dirt short of it
                plane.planeState = AIPlane.PlaneAIState.doingDive;
                plane.exitDiveEndTime = 0f;
                float aimOffsetY = horizDist > 90.0f ? Mathf.Clamp(horizDist * 0.065f, 1.0f, 15.0f) : -1.5f;
                plane.flyTargetPosition = targetPos + Vector3.up * aimOffsetY;
            }

            if (!isPostfix)
            {
                // Bypass vanilla's periodic GetTerrainHeght pull-up & NeedRefill checks inside AIPlane.Update
                // while allowing per-frame RotationTo(flyTargetPosition) and forward gun firing to run every frame
                plane.nextUpdateCheck = now + 0.35f;
            }
        }

        /// <summary>
        /// Prevents AI aircraft from nosediving into steep mountain slopes during CAS runs
        /// and ensures pilot personalities apply strictly once per state transition instead of 60 Hz drift,
        /// without overwriting flyTargetPosition on top of vanilla flight control.
        /// </summary>
        public static void UpdateAircraftDiveTerrainSafetyAndPersonality(AIPlane plane)
        {
            if (plane == null ||
                plane.Pointer == IntPtr.Zero ||
                plane.veh == null ||
                plane.veh.Pointer == IntPtr.Zero ||
                plane.transform == null ||
                !AiCore.IsLocallyOwnedVehicle(plane.veh) ||
                AiCore.IsAnyHumanPlayerInsideVehicle(plane.veh) ||
                !plane.HasAIDriver ||
                plane.veh.isGrounded ||
                !plane.veh.CanFight() ||
                IsJapaneseKamikazeActive(plane))
            {
                return;
            }

            IntPtr planePtr = plane.Pointer;
            _planeAttackReaction.TryGetValue(planePtr, out PlaneAttackReactionState st);

            float now = Time.time;
            AIPlane.PlaneAIState curState = plane.planeState;
            Vector3 pos = plane.transform.position;
            Vector3 fwd = plane.transform.forward;
            Vector3 flatFwd = new Vector3(fwd.x, 0f, fwd.z);
            flatFwd = flatFwd.sqrMagnitude > 0.001f ? flatFwd.normalized : Vector3.forward;

            bool persEnabled = VisionConfig.EnableRolePersonalities.Value &&
                               VisionConfig.EnableVehicleAndPilotPersonalities.Value;
            SoldierPersonality pilotPers = persEnabled
                ? GetVehicleCrewPersonality(plane.veh)
                : SoldierPersonality.None;

            // One-shot Pilot_Ace decision cadence boost per vanilla nextUpdateCheck reset
            if (persEnabled &&
                pilotPers == SoldierPersonality.Pilot_Ace &&
                plane.nextUpdateCheck > st.LastAdjustedNextUpdateCheck + 0.15f)
            {
                float rem = plane.nextUpdateCheck - now;
                if (rem > 0.25f)
                {
                    plane.nextUpdateCheck = now + rem * 0.78f;
                    st.LastAdjustedNextUpdateCheck = plane.nextUpdateCheck;
                }
            }

            // One-shot entry into exitDive personality duration tweak
            if (st.HasObservedState &&
                curState == AIPlane.PlaneAIState.exitDive &&
                st.LastPlaneState != AIPlane.PlaneAIState.exitDive)
            {
                if (persEnabled)
                {
                    if (pilotPers == SoldierPersonality.Pilot_Daredevil)
                        plane.exitDiveEndTime = Mathf.Max(now + 1.4f, plane.exitDiveEndTime - 0.45f);
                    else if (pilotPers == SoldierPersonality.Pilot_Disciplined)
                        plane.exitDiveEndTime += 0.65f;
                }
            }

            // Flak / AA Self-Preservation Abort: if a non-Kamikaze, non-Daredevil aircraft takes heavy flak/AA damage
            // mid-dive (>= 12 damage spike or >= 50 total damage while taking hits), salvo-drop bombs if near target
            // (and not danger-close to a friendly squad) and immediately pull out of the dive via vanilla ExitDive!
            string myFaction = GetVehicleCrewFaction(plane.veh);
            AiCore.VehicleFastHandles planeVh = AiCore.GetOrCreateVehicleHandles(plane.veh);
            if (planeVh != null)
            {
                short curPlaneDmg = plane.veh.GetDamage();
                if ((curState == AIPlane.PlaneAIState.doingDive || curState == AIPlane.PlaneAIState.preparingDive) &&
                    pilotPers != SoldierPersonality.Pilot_Daredevil &&
                    curPlaneDmg > planeVh.LastObservedDamage &&
                    (curPlaneDmg - planeVh.LastObservedDamage >= 12 || curPlaneDmg >= 50))
                {
                    Spottable atkTgt = plane.targetToAttack;
                    if (plane.HasBombsToDeliver() &&
                        atkTgt != null &&
                        atkTgt.Pointer != IntPtr.Zero)
                    {
                        Vector3 tgtPos = atkTgt.GetPosition();
                        Vector3 dTgt = tgtPos - pos;
                        if (dTgt.x * dTgt.x + dTgt.z * dTgt.z <= 260.0f * 260.0f &&
                            !IsAircraftTargetDangerCloseToFriendlySquad(myFaction, tgtPos, true))
                        {
                            plane.veh.DropBombs();
                        }
                    }

                    plane.ExitDive();
                    curState = plane.planeState;
                }
                planeVh.LastObservedDamage = curPlaneDmg;
            }

            // Throttled (~8 Hz) danger-close wave-off & ground-proximity pull-up guard during dive/bombing runs
            if ((curState == AIPlane.PlaneAIState.doingDive ||
                 curState == AIPlane.PlaneAIState.preparingDive ||
                 curState == AIPlane.PlaneAIState.bomberApproaching) &&
                now >= st.NextDiveGroundCheckTime)
            {
                st.NextDiveGroundCheckTime = now + 0.12f;
                bool hasLiveBombs = plane.HasBombsToDeliver();
                if (curState == AIPlane.PlaneAIState.bomberApproaching)
                {
                    Vector3 bDest = plane.flyTargetPosition;
                    Vector3 dBomb = bDest - pos;
                    if (dBomb.x * dBomb.x + dBomb.z * dBomb.z <= 165.0f * 165.0f &&
                        IsAircraftTargetDangerCloseToFriendlySquad(myFaction, bDest, true))
                    {
                        plane.SetRetreatBomber();
                        curState = plane.planeState;
                        FriendlyFirePrevented++;
                    }
                }
                else
                {
                    Spottable atkTgt = plane.targetToAttack;
                    if (atkTgt != null && atkTgt.Pointer != IntPtr.Zero && !atkTgt.IsAirVehicle())
                    {
                        Vector3 tgtPos = atkTgt.GetPosition();
                        if (IsAircraftTargetDangerCloseToFriendlySquad(myFaction, tgtPos, hasLiveBombs))
                        {
                            plane.targetToAttack = null;
                            plane.ExitDive();
                            curState = plane.planeState;
                            FriendlyFirePrevented++;
                        }
                    }
                }

                if (curState == AIPlane.PlaneAIState.doingDive)
                {
                    Rigidbody rb = plane.veh.GetRigidbody();
                    Vector3 vel = rb != null ? rb.velocity : fwd * 75.0f;
                    float speed = Mathf.Max(45.0f, vel.magnitude);
                    float sinkRate = Mathf.Max(0f, -vel.y);
                    Vector3 velDir = vel.sqrMagnitude > 1.0f ? vel / vel.magnitude : fwd;

                    float terrHere = ResourcesManager.GetTerrainAltitude(pos, 0f);
                    float terrNear = ResourcesManager.GetTerrainAltitude(pos + flatFwd * (speed * 1.35f), 0f);
                    float terrFar = ResourcesManager.GetTerrainAltitude(pos + flatFwd * (speed * 2.50f), 0f);
                    float maxGroundY = Mathf.Max(terrHere, Mathf.Max(terrNear, terrFar));
                    if (AiCore.TryGetActiveWaterLevel(out float waterY) && waterY > maxGroundY)
                        maxGroundY = waterY;

                    plane.GetDiveData(out _, out float nativeLimitHeight);
                    float rayDist = Mathf.Clamp(speed * 1.35f, 65.0f, 135.0f);
                    bool rayHitObstacle = Physics.Raycast(
                        pos,
                        velDir,
                        rayDist,
                        AiCore.GetSolidEnvironmentMask(),
                        QueryTriggerInteraction.Ignore);

                    float minSafeAlt = maxGroundY + Mathf.Max(nativeLimitHeight, 52.0f + sinkRate * 1.15f);
                    if (pos.y <= minSafeAlt || rayHitObstacle)
                    {
                        Spottable atkTgt = plane.targetToAttack;
                        if (plane.HasBombsToDeliver() &&
                            atkTgt != null &&
                            atkTgt.Pointer != IntPtr.Zero)
                        {
                            Vector3 tgtPos = atkTgt.GetPosition();
                            Vector3 dTgt = tgtPos - pos;
                            if (dTgt.x * dTgt.x + dTgt.z * dTgt.z <= 240.0f * 240.0f &&
                                !IsAircraftTargetDangerCloseToFriendlySquad(myFaction, tgtPos, true))
                            {
                                plane.veh.DropBombs();
                            }
                        }

                        plane.ExitDive();
                        curState = plane.planeState;
                    }
                }
            }

            st.LastPlaneState = curState;
            st.HasObservedState = true;
            _planeAttackReaction[planePtr] = st;
        }

        private static float GetPlaneCruiseVelocityMs(VehiclePlane vp)
        {
            if (vp == null)
                return 60.0f;

            return Mathf.Max(55.0f, Mathf.Max(vp.startLiftVelocity * 1.35f, (vp.maxKmhSpeed / 3.6f) * 0.80f));
        }

        private static bool IsPlaneOnGroundOrRunway(VehiclePlane vp)
        {
            if (vp == null || vp.transform == null)
                return true;

            vp.RefreshGrounded();
            if (vp.isGrounded)
                return true;

            Vector3 pos = vp.transform.position;
            if (pos.y <= 6.0f)
                return true;

            if (AiCore.RaycastEnvironment(pos + Vector3.up * 1.8f, Vector3.down, 11.0f, vp.transform.root, out _))
                return true;

            return ResourcesManager.GetDistanceFromTerrain(pos, 5.0f, false) <= 10.0f;
        }

        internal static void TryStartAirborneSpawnHold(VehiclePlane vp)
        {
            if (vp == null ||
                vp.Pointer == IntPtr.Zero ||
                vp.transform == null ||
                !VisionConfig.EnableMod.Value ||
                !VisionConfig.EnableCoopAirborneSpawnFix.Value)
            {
                return;
            }

            if (Photon.Pun.PhotonNetwork.InRoom && !AiCore.IsMasterClient())
                return;

            if (vp.IsDisabled() || !vp.HasNeverExploded() || vp.HasDriver())
                return;

            AiCore.VehicleFastHandles vh = AiCore.GetOrCreateVehicleHandles(vp);
            if (vh == null)
                return;

            if (IsPlaneOnGroundOrRunway(vp))
            {
                vh.PlaneGroundSpawnChecked = true;
                return;
            }

            float now = Time.time;
            float holdDuration = (Photon.Pun.PhotonNetwork.InRoom && AiCore.IsMasterClient()) ? 5.0f : 2.5f;
            vh.AirborneSpawnHoldActive = true;
            vh.AirborneSpawnHoldUntil = now + holdDuration;
            vh.AirborneSpawnHoldPos = vp.transform.position;
            vh.AirborneSpawnHoldRot = vp.transform.rotation;
            vp.nextCollisionDamageTime = Mathf.Max(vp.nextCollisionDamageTime, now + 2.0f);

            Rigidbody rb = vp.rb;
            if (rb != null)
            {
                rb.useGravity = false;
                if (!rb.isKinematic)
                {
                    rb.velocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                }
            }
        }

        internal static void ReleaseAirborneSpawnHold(VehiclePlane vp, AiCore.VehicleFastHandles vh)
        {
            if (vh == null || !vh.AirborneSpawnHoldActive)
                return;

            vh.AirborneSpawnHoldActive = false;
            vh.AirborneSpawnHoldUntil = 0f;

            if (vp != null && vp.rb != null && !vp.rb.useGravity)
            {
                vp.rb.useGravity = true;
            }
        }

        private static bool ApplyPlaneHotStartState(VehiclePlane vp, AiCore.VehicleFastHandles vh, bool forceVelocity)
        {
            ReleaseAirborneSpawnHold(vp, vh);

            if (vp == null ||
                vp.transform == null ||
                vp.IsDisabled() ||
                !vp.CanStartEngine ||
                !vp.HasAtLeastAPropeller() ||
                !vp.HasNeverExploded())
            {
                return false;
            }

            if (!vp.IsPhysicsEnabled)
                vp.SetPhysicsEnabled(true);

            Rigidbody rb = vp.rb;
            if (rb != null && !rb.useGravity)
                rb.useGravity = true;

            float now = Time.time;
            if (!vp.engineStarted)
                vp.StartEngine();

            vp.startingCanceled = false;
            // Native MovableVehicle.get_engineStarted checks engineStartTime > 0f && Time.time >= engineStartTime.
            vp.engineStartTime = now > 0.05f ? Mathf.Max(0.01f, now - 8.0f) : 0.001f;
            vp.isBraking = false;
            vp.throttle = 100.0f;
            float maxThrust = Mathf.Max(100.0f, vp.thrustForceMultiplier * 100.0f);
            vp.thrustForce = maxThrust;
            vp._trgtThrust = maxThrust;
            vp.groundLeaveTime = 0f;
            vp.farFromGround = true;
            vp.SetGearClosed();
            vp.isGrounded = false;

            Quaternion rot = vp.transform.rotation;
            vp.targetRotation = rot;
            vp.previousDir = rot;
            vp.nextCollisionDamageTime = Mathf.Max(vp.nextCollisionDamageTime, now + 1.0f);

            SyncVehicle syncher = vp.vehicleSyncher;
            if (syncher != null && vp.OwnDriver())
            {
                syncher.syncDestPos = new Il2CppSystem.Nullable<Vector3>();
                syncher.syncDestRot = new Il2CppSystem.Nullable<Quaternion>();
            }

            float cruiseMs = GetPlaneCruiseVelocityMs(vp);
            bool velReady = false;
            if (rb != null && !rb.isKinematic)
            {
                float minCruiseSq = (cruiseMs * 0.72f) * (cruiseMs * 0.72f);
                if (forceVelocity || rb.velocity.sqrMagnitude < minCruiseSq)
                {
                    rb.velocity = vp.transform.forward * cruiseMs;
                    rb.angularVelocity = Vector3.zero;
                }
                velReady = rb.velocity.sqrMagnitude >= (cruiseMs * 0.65f) * (cruiseMs * 0.65f);
            }

            return vp.engineStarted && vp.throttle >= 95.0f && velReady;
        }

        internal static void BeginPlaneHotStartWindow(VehiclePlane vp, bool allowAiPilot, bool forceRecheck = false)
        {
            if (vp == null ||
                vp.Pointer == IntPtr.Zero ||
                !VisionConfig.EnableMod.Value ||
                !VisionConfig.EnableCoopAirborneSpawnFix.Value ||
                vp.IsDisabled() ||
                vp.transform == null)
            {
                return;
            }

            AiCore.VehicleFastHandles vh = AiCore.GetOrCreateVehicleHandles(vp);
            if (vh == null)
                return;

            vp.CheckDriverAndOwnership();

            if (vp.HasDriverAlive)
                ReleaseAirborneSpawnHold(vp, vh);

            if (forceRecheck)
            {
                vh.PlaneGroundSpawnChecked = false;
                vh.PlaneHotStarted = false;
            }

            float now = Time.time;
            if (!vp.OwnDriverOrVehicle())
            {
                vh.PlaneHotStartPendingUntil = Mathf.Max(vh.PlaneHotStartPendingUntil, now + 3.0f);
                vh.PlaneHotStartAllowAi = vh.PlaneHotStartAllowAi || allowAiPilot;
                return;
            }

            if (!allowAiPilot && !AiCore.IsAnyHumanPlayerInsideVehicle(vp))
                return;

            if (vh.PlaneGroundSpawnChecked)
                return;

            if (IsPlaneOnGroundOrRunway(vp))
            {
                vh.PlaneGroundSpawnChecked = true;
                vh.PlaneHotStartPendingUntil = 0f;
                vh.PlaneHotStartWindowUntil = 0f;
                return;
            }

            vh.PlaneHotStartWindowUntil = Mathf.Max(vh.PlaneHotStartWindowUntil, now + 2.5f);
            vh.PlaneHotStartPendingUntil = 0f;
            vh.PlaneHotStartAllowAi = vh.PlaneHotStartAllowAi || allowAiPilot;
            if (ApplyPlaneHotStartState(vp, vh, forceVelocity: false))
                vh.PlaneHotStarted = true;
        }

        [HarmonyPatch(typeof(VehiclePlane), nameof(VehiclePlane.OnSquadSpawnInside))]
        public static class VehiclePlane_OnSquadSpawnInside_Patch
        {
            public static void Postfix(VehiclePlane __instance)
            {
                try
                {
                    if (__instance == null || !VisionConfig.EnableMod.Value)
                        return;

                    GetVehicleCrewFaction(__instance);
                    RegisterPlaneCrewFromSeats(__instance);
                    if (VisionConfig.EnableCoopAirborneSpawnFix.Value)
                    {
                        BeginPlaneHotStartWindow(__instance, allowAiPilot: true, forceRecheck: false);
                    }
                }
                catch { }
            }
        }

        private static bool IsRemotePilotedPlane(VehiclePlane vp)
        {
            return vp != null &&
                   vp.Pointer != IntPtr.Zero &&
                   Photon.Pun.PhotonNetwork.InRoom &&
                   !vp.IsDisabled() &&
                   vp.HasNeverExploded() &&
                   vp.IsPhysicsEnabled &&
                   vp.HasDriverAlive &&
                   !vp.OwnDriverOrVehicle();
        }

        private static void RestoreRemotePlaneGravityIfNeeded(VehiclePlane vp, AiCore.VehicleFastHandles vh)
        {
            if (vh == null || !vh.RemotePlaneGravitySuppressed)
                return;

            vh.RemotePlaneGravitySuppressed = false;
            vh.HasRemotePlaneSync = false;
            if (!vh.AirborneSpawnHoldActive && vp != null && vp.rb != null && !vp.rb.useGravity)
            {
                vp.rb.useGravity = true;
            }
        }

        private static bool IsFiniteFloat(float v)
        {
            return !float.IsNaN(v) && !float.IsInfinity(v);
        }

        private static bool IsFiniteVector3(Vector3 v)
        {
            return IsFiniteFloat(v.x) && IsFiniteFloat(v.y) && IsFiniteFloat(v.z);
        }

        private static bool IsFiniteQuaternion(Quaternion q)
        {
            return IsFiniteFloat(q.x) && IsFiniteFloat(q.y) && IsFiniteFloat(q.z) && IsFiniteFloat(q.w);
        }

        [HarmonyPatch(typeof(VehiclePlane), nameof(VehiclePlane.FixedUpdate))]
        public static class VehiclePlane_FixedUpdate_Patch
        {
            public static void Prefix(VehiclePlane __instance)
            {
                try
                {
                    if (__instance == null || __instance.Pointer == IntPtr.Zero)
                        return;

                    AiCore.VehicleFastHandles vh = AiCore.GetOrCreateVehicleHandles(__instance);
                    if (vh == null || !vh.AirborneSpawnHoldActive)
                        return;

                    if (!VisionConfig.EnableMod.Value ||
                        !VisionConfig.EnableCoopAirborneSpawnFix.Value ||
                        Time.time >= vh.AirborneSpawnHoldUntil ||
                        __instance.HasDriver() ||
                        __instance.IsDisabled() ||
                        !__instance.HasNeverExploded())
                    {
                        ReleaseAirborneSpawnHold(__instance, vh);
                    }
                    else if (__instance.rb != null && __instance.rb.useGravity)
                    {
                        __instance.rb.useGravity = false;
                    }
                }
                catch { }
            }

            public static void Postfix(VehiclePlane __instance)
            {
                try
                {
                    if (__instance == null || __instance.Pointer == IntPtr.Zero)
                        return;

                    AiCore.VehicleFastHandles vh = AiCore.GetOrCreateVehicleHandles(__instance);
                    if (vh == null)
                        return;

                    if (!VisionConfig.EnableMod.Value || !VisionConfig.EnableCoopAirborneSpawnFix.Value)
                    {
                        if (vh.AirborneSpawnHoldActive)
                            ReleaseAirborneSpawnHold(__instance, vh);
                        RestoreRemotePlaneGravityIfNeeded(__instance, vh);
                        return;
                    }

                    float now = Time.time;
                    bool ownNow = __instance.OwnDriverOrVehicle();
                    bool hadObserved = vh.HasObservedOwnershipState;
                    bool prevOwn = vh.WasOwnDriverOrVehicle;
                    vh.WasOwnDriverOrVehicle = ownNow;
                    vh.HasObservedOwnershipState = true;

                    if (vh.AirborneSpawnHoldActive)
                    {
                        if (!ownNow ||
                            __instance.HasDriverAlive ||
                            __instance.IsDisabled() ||
                            !__instance.HasNeverExploded() ||
                            now >= vh.AirborneSpawnHoldUntil)
                        {
                            ReleaseAirborneSpawnHold(__instance, vh);
                        }
                        else
                        {
                            Rigidbody holdRb = __instance.rb;
                            if (holdRb != null)
                            {
                                if (holdRb.useGravity)
                                    holdRb.useGravity = false;
                                if (!holdRb.isKinematic)
                                {
                                    holdRb.velocity = Vector3.zero;
                                    holdRb.angularVelocity = Vector3.zero;
                                }
                            }

                            if (__instance.transform != null)
                            {
                                __instance.transform.SetPositionAndRotation(vh.AirborneSpawnHoldPos, vh.AirborneSpawnHoldRot);
                            }

                            __instance.nextCollisionDamageTime = Mathf.Max(__instance.nextCollisionDamageTime, now + 1.5f);
                            return;
                        }
                    }

                    bool remotePiloted =
                        !ownNow &&
                        Photon.Pun.PhotonNetwork.InRoom &&
                        __instance.HasDriverAlive &&
                        __instance.IsPhysicsEnabled &&
                        !__instance.IsDisabled() &&
                        __instance.HasNeverExploded();
                    if (remotePiloted)
                    {
                        Rigidbody remoteRb = __instance.rb;
                        if (remoteRb != null)
                        {
                            if (remoteRb.useGravity)
                                remoteRb.useGravity = false;
                            vh.RemotePlaneGravitySuppressed = true;
                            if (!remoteRb.isKinematic)
                            {
                                if (remoteRb.angularVelocity.sqrMagnitude > 0.0001f)
                                    remoteRb.angularVelocity = Vector3.zero;
                                if (vh.HasRemotePlaneSync && (now - vh.RemotePlaneSyncReceivedAt) <= 1.0f)
                                    remoteRb.velocity = vh.RemotePlaneSyncVel;
                            }
                        }
                        return;
                    }

                    RestoreRemotePlaneGravityIfNeeded(__instance, vh);

                    if (!ownNow)
                        return;

                    if (hadObserved && !prevOwn &&
                        __instance.HasDriverAlive &&
                        !__instance.IsDisabled() &&
                        (!__instance.engineStarted || __instance.throttle < 50.0f))
                    {
                        BeginPlaneHotStartWindow(__instance, allowAiPilot: true, forceRecheck: true);
                        return;
                    }

                    if (vh.PlaneHotStartPendingUntil > 0f)
                    {
                        if (now < vh.PlaneHotStartPendingUntil)
                        {
                            bool allowAi = vh.PlaneHotStartAllowAi;
                            vh.PlaneHotStartPendingUntil = 0f;
                            BeginPlaneHotStartWindow(__instance, allowAi, forceRecheck: true);
                            return;
                        }
                        vh.PlaneHotStartPendingUntil = 0f;
                    }

                    if (vh.PlaneHotStartWindowUntil > 0f)
                    {
                        if (now >= vh.PlaneHotStartWindowUntil ||
                            __instance.IsDisabled() ||
                            !__instance.HasDriverAlive ||
                            vh.PlaneGroundSpawnChecked)
                        {
                            vh.PlaneHotStartWindowUntil = 0f;
                            return;
                        }

                        float cruiseMs = GetPlaneCruiseVelocityMs(__instance);
                        Rigidbody rb = __instance.rb;
                        bool stalled = rb != null &&
                            !rb.isKinematic &&
                            rb.velocity.sqrMagnitude < (cruiseMs * 0.55f) * (cruiseMs * 0.55f);
                        bool deadStick =
                            !__instance.engineStarted ||
                            __instance.throttle < 10.0f ||
                            __instance.isBraking ||
                            __instance.isGrounded ||
                            !__instance.IsPhysicsEnabled;

                        if (!vh.PlaneHotStarted || deadStick || stalled)
                        {
                            if (ApplyPlaneHotStartState(__instance, vh, forceVelocity: stalled))
                            {
                                vh.PlaneHotStarted = true;
                            }
                        }
                    }
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(SyncSoldier), nameof(SyncSoldier.OnPhotonSerializeView))]
        public static class SyncSoldier_OnPhotonSerializeView_Patch
        {
            public static void Postfix(SyncSoldier __instance, Photon.Pun.PhotonStream __0)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value ||
                        !VisionConfig.EnableCoopAirborneSpawnFix.Value ||
                        !Photon.Pun.PhotonNetwork.InRoom ||
                        __instance == null ||
                        __instance.Pointer == IntPtr.Zero ||
                        __0 == null ||
                        !__0.IsWriting)
                    {
                        return;
                    }

                    float now = Time.time;
                    if (__instance.next_position_sync <= now + 0.14f)
                        return;

                    Soldier s = __instance.soldier;
                    if (s == null || !s.IsAlive || !s.IsOnVehicle())
                        return;

                    Vehicle veh = s.GetCurrentVehicle();
                    if (veh == null || !veh.IsAirVehicle())
                        return;

                    Soldier drv = veh.GetDriver();
                    if (drv != null && drv.Pointer == s.Pointer)
                    {
                        __instance.next_position_sync = now + 0.12f;
                    }
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(SyncVehiclePlane), nameof(SyncVehiclePlane.SyncVehiclePosition))]
        public static class SyncVehiclePlane_SyncVehiclePosition_Patch
        {
            public static bool Prefix(SyncVehiclePlane __instance, Vector3 __0, Quaternion __1, float __2, Vector2 __3, bool __4)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value ||
                        !VisionConfig.EnableCoopAirborneSpawnFix.Value ||
                        !Photon.Pun.PhotonNetwork.InRoom ||
                        __instance == null ||
                        __instance.Pointer == IntPtr.Zero)
                    {
                        return true;
                    }

                    if (!IsFiniteVector3(__0) || !IsFiniteQuaternion(__1) || !IsFiniteFloat(__2))
                        return false;

                    Vehicle veh = __instance.vehicle;
                    VehiclePlane vp = veh != null ? veh.TryCast<VehiclePlane>() : null;
                    if (vp == null)
                        return true;

                    vp.CheckDriverAndOwnership();
                    if (!IsRemotePilotedPlane(vp))
                        return true;

                    AiCore.VehicleFastHandles vh = AiCore.GetOrCreateVehicleHandles(vp);
                    if (vh == null)
                        return true;

                    float now = Time.time;
                    float maxSpeedMs = Mathf.Max(160f, (vp.maxKmhSpeed / 3.6f) * 1.45f);
                    float clampedSpeed = Mathf.Clamp(__2, -40f, maxSpeedMs);
                    Vector3 syncVel;
                    if (Mathf.Abs(clampedSpeed) >= 4.0f)
                    {
                        syncVel = (__1 * Vector3.forward) * clampedSpeed;
                    }
                    else if (vh.HasRemotePlaneSync)
                    {
                        float packetDt = now - vh.RemotePlaneSyncReceivedAt;
                        syncVel = (packetDt >= 0.05f && packetDt <= 0.65f)
                            ? Vector3.ClampMagnitude((__0 - vh.RemotePlaneSyncPos) / packetDt, maxSpeedMs)
                            : Vector3.zero;
                    }
                    else
                    {
                        syncVel = Vector3.zero;
                    }

                    bool hadSync = vh.HasRemotePlaneSync;
                    vh.RemotePlaneSyncPos = __0;
                    vh.RemotePlaneSyncRot = __1;
                    vh.RemotePlaneSyncVel = syncVel;
                    vh.RemotePlaneSyncReceivedAt = now;
                    vh.HasRemotePlaneSync = true;

                    __instance.syncDestPos = new Il2CppSystem.Nullable<Vector3>(__0);
                    __instance.syncDestRot = new Il2CppSystem.Nullable<Quaternion>(__1);
                    __instance.syncBraking = __4;
                    __instance.synchedSpeed = syncVel;
                    vp.SetMoveDir(__3);

                    Rigidbody rb = vp.rb;
                    if (rb != null)
                    {
                        if (rb.useGravity)
                            rb.useGravity = false;
                        vh.RemotePlaneGravitySuppressed = true;
                        if (!rb.isKinematic)
                        {
                            rb.velocity = syncVel;
                            rb.angularVelocity = Vector3.zero;
                        }
                    }

                    Transform tr = vp.transform;
                    if (tr != null && (!hadSync || (__0 - tr.position).sqrMagnitude > 85f * 85f))
                    {
                        tr.SetPositionAndRotation(__0, __1);
                    }

                    return false;
                }
                catch
                {
                    return true;
                }
            }
        }

        [HarmonyPatch(typeof(SyncVehiclePlane), nameof(SyncVehiclePlane.UpdatePosAndRot))]
        public static class SyncVehiclePlane_UpdatePosAndRot_Patch
        {
            public static bool Prefix(SyncVehiclePlane __instance)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value ||
                        !VisionConfig.EnableCoopAirborneSpawnFix.Value ||
                        !Photon.Pun.PhotonNetwork.InRoom ||
                        __instance == null ||
                        __instance.Pointer == IntPtr.Zero)
                    {
                        return true;
                    }

                    Vehicle veh = __instance.vehicle;
                    VehiclePlane vp = veh != null ? veh.TryCast<VehiclePlane>() : null;
                    if (!IsRemotePilotedPlane(vp))
                        return true;

                    AiCore.VehicleFastHandles vh = AiCore.GetOrCreateVehicleHandles(vp);
                    if (vh == null || !vh.HasRemotePlaneSync)
                        return false;

                    float dt = Time.deltaTime;
                    if (dt <= 0f)
                        return false;

                    float age = Mathf.Clamp(Time.time - vh.RemotePlaneSyncReceivedAt, 0f, 0.35f);
                    Vector3 vel = vh.RemotePlaneSyncVel;
                    Vector3 extrap = vel * age;
                    if (vel.y < -2.0f)
                        extrap.y = vel.y * Mathf.Min(age, 0.20f);

                    Vector3 targetPos = vh.RemotePlaneSyncPos + extrap;
                    Quaternion targetRot = vh.RemotePlaneSyncRot;

                    Transform tr = vp.transform;
                    if (tr != null)
                    {
                        if ((targetPos - tr.position).sqrMagnitude > 85f * 85f)
                        {
                            tr.SetPositionAndRotation(targetPos, targetRot);
                        }
                        else
                        {
                            tr.position = Vector3.Lerp(tr.position, targetPos, 1f - Mathf.Exp(-12f * dt));
                            tr.rotation = Quaternion.Slerp(tr.rotation, targetRot, 1f - Mathf.Exp(-14f * dt));
                        }
                    }

                    Rigidbody rb = vp.rb;
                    if (rb != null)
                    {
                        if (rb.useGravity)
                            rb.useGravity = false;
                        vh.RemotePlaneGravitySuppressed = true;
                        if (!rb.isKinematic)
                        {
                            rb.velocity = vel;
                            if (rb.angularVelocity.sqrMagnitude > 0.0001f)
                                rb.angularVelocity = Vector3.zero;
                        }
                    }

                    return false;
                }
                catch
                {
                    return true;
                }
            }
        }

        [HarmonyPatch(typeof(VehiclePlane), nameof(VehiclePlane.OnCollisionEnter))]
        public static class VehiclePlane_OnCollisionEnter_Patch
        {
            public static bool Prefix(VehiclePlane __instance)
            {
                try
                {
                    if (VisionConfig.EnableMod.Value &&
                        VisionConfig.EnableCoopAirborneSpawnFix.Value &&
                        Photon.Pun.PhotonNetwork.InRoom &&
                        __instance != null &&
                        __instance.Pointer != IntPtr.Zero &&
                        __instance.HasDriverAlive &&
                        !__instance.OwnDriverOrVehicle())
                    {
                        return false;
                    }
                }
                catch { }
                return true;
            }
        }

        [HarmonyPatch(typeof(VehiclePlane), nameof(VehiclePlane.Explode))]
        public static class VehiclePlane_Explode_Patch
        {
            public static bool Prefix(VehiclePlane __instance)
            {
                try
                {
                    if (VisionConfig.EnableMod.Value &&
                        VisionConfig.EnableCoopAirborneSpawnFix.Value &&
                        Photon.Pun.PhotonNetwork.InRoom &&
                        __instance != null &&
                        __instance.Pointer != IntPtr.Zero &&
                        __instance.HasDriverAlive &&
                        !__instance.OwnDriverOrVehicle())
                    {
                        return false;
                    }
                }
                catch { }
                return true;
            }
        }
    }
}
