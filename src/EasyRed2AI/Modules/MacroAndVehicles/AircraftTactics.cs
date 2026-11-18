using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace EasyRed2HumanAI
{
    public static partial class VeteranAndTacticsPatches
    {
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
                // Prioritize enemy fighters over bombers, and penalize point-blank (< 240m) targets when picking a fresh
                // dogfight target so fighters set up proper lead turns instead of initiating instant head-on merges.
                float score = (info.IsBomber ? 100.0f : 350.0f) - (dist * 0.25f);
                if (dist < 240.0f)
                    score -= 350.0f;
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
                if (AIPlane.fighterDogfightExtraLeash < 1800.0f)
                {
                    AIPlane.fighterDogfightExtraLeash = 1800.0f;
                }

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
                        if (TryRecordCombinedArmsAssist(vehPtr, now))
                        {
                            AiCore.LogDebug(
                                "CombArmsAirSup",
                                $"{myFaction} aircraft aborted ground pass/patrol to intercept airborne enemy aircraft.");
                        }
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
                if (!AiCore.AreSameSideFactions(req.Faction, myFaction) || req.RequestType == RadioRequest.artillerySmoke)
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
                if (AiCore.AreSameSideFactions(cluster.Faction, myFaction))
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
                    if (TryRecordCombinedArmsAssist(vehPtr, now))
                    {
                        AiCore.LogDebug(
                            "CombArmsPlaneOrbit",
                            $"{myFaction} aircraft shifted patrol orbit toward known enemy squad / support request.");
                    }
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

            float safeRadius = hasBombsToDeliver ? 38.0f : 18.0f;
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
                    if (TryRecordCombinedArmsAssist(plane.veh.Pointer, Time.time))
                    {
                        AiCore.LogDebug(
                            "CombArmsAirSup",
                            $"{myFaction} aircraft detected enemy plane in airspace; prioritizing dogfight over ground attack.");
                    }
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
            bool hasLiveBombsNow = plane.HasBombsToDeliver();

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
                        ? Mathf.Min(effectiveMaxDist, unmarkedAwareCap)
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

            // Kamikaze is strictly a last-resort sacrifice for severely crippled Japanese aircraft:
            // dropping bombs or expending forward gun ammo NEVER triggers kamikaze (intact planes RTB/refill normally).
            bool severelyDamaged =
                plane.veh.GetDamage() >= 80 ||
                plane.veh.hasMissingParts ||
                !plane.veh.useWingsL ||
                !plane.veh.useWingsR;

            if (!severelyDamaged)
                return false;

            IntPtr vehPtr = plane.veh.Pointer;
            if (_kamikazeDecisionByPlane.TryGetValue(vehPtr, out bool committed))
                return committed;

            SoldierPersonality pilotPers = GetVehicleCrewPersonality(plane.veh);
            float chance = pilotPers == SoldierPersonality.Pilot_Disciplined
                ? 0f
                : (pilotPers == SoldierPersonality.Pilot_Daredevil ? 0.55f : 0.30f);
            committed = chance > 0f && UnityEngine.Random.value < chance;
            _kamikazeDecisionByPlane[vehPtr] = committed;
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

            IntPtr vehPtr = plane.veh.Pointer;
            float now = Time.time;

            if (_activeKamikazePlanes.Add(vehPtr))
            {
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

        public static void TryExecuteDogfightAntiChickenBreakOff(AIPlane plane, bool isPostfix)
        {
            if (plane == null ||
                plane.veh == null ||
                plane.veh.Pointer == IntPtr.Zero ||
                plane.transform == null ||
                !AiCore.IsLocallyOwnedVehicle(plane.veh) ||
                plane.veh.PlayerIsInside() ||
                !plane.HasAIDriver ||
                plane.veh.isGrounded ||
                !plane.veh.CanFight())
            {
                return;
            }

            AIPlane.PlaneAIState aiState = plane.planeState;
            if (aiState == AIPlane.PlaneAIState.takeOff ||
                aiState == AIPlane.PlaneAIState.gatheringSpeed ||
                aiState == AIPlane.PlaneAIState.landing)
            {
                return;
            }

            IntPtr vehPtr = plane.veh.Pointer;
            float now = Time.time;

            if (_planeDogfightBreakOff.TryGetValue(vehPtr, out DogfightBreakOffState breakState) && now < breakState.ExpireTime)
            {
                plane.followTarget = false;
                if (aiState == AIPlane.PlaneAIState.doingDive || aiState == AIPlane.PlaneAIState.preparingDive)
                {
                    plane.ExitDive();
                }
                plane.flyTargetPosition = breakState.EscapeTargetPos;
                if (!isPostfix && plane.nextUpdateCheck < now + 0.30f)
                {
                    plane.nextUpdateCheck = now + 0.30f;
                }
                return;
            }

            if (isPostfix || now < breakState.CooldownUntil || now < breakState.NextCheckTime)
                return;

            breakState.NextCheckTime = now + 0.10f;
            _planeDogfightBreakOff[vehPtr] = breakState;

            string myFaction = GetVehicleCrewFaction(plane.veh);
            if (string.IsNullOrEmpty(myFaction))
                return;

            RefreshCombinedArmsIntelIfNeeded();

            Vector3 myPos = plane.transform.position;
            Vector3 myFwd = plane.transform.forward;
            if (myFwd.sqrMagnitude < 0.001f)
                return;
            myFwd.Normalize();

            SoldierPersonality pilotPers = GetVehicleCrewPersonality(plane.veh);
            float headOnBreakDist = 155.0f;
            if (pilotPers == SoldierPersonality.Pilot_Ace || pilotPers == SoldierPersonality.Pilot_Disciplined)
                headOnBreakDist = 170.0f;
            else if (pilotPers == SoldierPersonality.Pilot_Daredevil)
                headOnBreakDist = 125.0f;

            bool triggerBreak = false;

            // 1. Check current targetToAttack first: break ONLY after firing through the head-on window on a true reciprocal nose-to-nose collision course!
            Spottable curAtk = plane.targetToAttack;
            if (curAtk != null && curAtk.Pointer != IntPtr.Zero && curAtk.IsAirVehicle())
            {
                VehiclePlane enemyVp = curAtk.TryCast<VehiclePlane>();
                if (enemyVp != null && !enemyVp.isGrounded && enemyVp.transform != null)
                {
                    Vector3 toEnemy = enemyVp.transform.position - myPos;
                    float dist = toEnemy.magnitude;
                    if (dist > 1.0f && dist <= headOnBreakDist)
                    {
                        Vector3 toEnemyDir = toEnemy / dist;
                        Vector3 enemyFwd = enemyVp.transform.forward;
                        float approachDot = Vector3.Dot(toEnemyDir, myFwd);
                        float mergeDot = Vector3.Dot(myFwd, enemyFwd);
                        if ((approachDot > 0.84f && mergeDot < -0.65f) ||
                            (dist <= 48.0f && approachDot > 0.72f && mergeDot < 0.25f))
                        {
                            triggerBreak = true;
                        }
                    }
                }
            }

            // 2. Scan _airborneAircraft for un-targeted reciprocal head-on collision courses
            if (!triggerBreak)
            {
                int airCount = _airborneAircraft.Count;
                for (int i = 0; i < airCount; i++)
                {
                    AirborneAircraftInfo info = _airborneAircraft[i];
                    if (AiCore.AreSameSideFactions(myFaction, info.Faction) ||
                        info.Plane == null ||
                        info.Plane.Pointer == IntPtr.Zero ||
                        info.Plane.Pointer == vehPtr ||
                        info.Plane.isGrounded ||
                        info.Plane.transform == null)
                    {
                        continue;
                    }

                    Vector3 toEnemy = info.Plane.transform.position - myPos;
                    float dist = toEnemy.magnitude;
                    if (dist <= 1.0f || dist > headOnBreakDist)
                        continue;

                    Vector3 toEnemyDir = toEnemy / dist;
                    Vector3 enemyFwd = info.Plane.transform.forward;
                    float approachDot = Vector3.Dot(toEnemyDir, myFwd);
                    float mergeDot = Vector3.Dot(myFwd, enemyFwd);
                    if ((approachDot > 0.84f && mergeDot < -0.65f) ||
                        (dist <= 48.0f && approachDot > 0.72f && mergeDot < 0.25f))
                    {
                        triggerBreak = true;
                        break;
                    }
                }
            }

            if (triggerBreak)
            {
                // Right-hand break turn with hard altitude ceiling so dogfighting fighters never spiral up into space
                float groundY = Mathf.Max(0f, ResourcesManager.GetTerrainAltitude(myPos, 0f));
                float maxSafeAlt = Mathf.Min(plane.veh.maxFlyHeight > 80f ? plane.veh.maxFlyHeight * 0.88f : 420f, groundY + 340f);
                float climbDelta = myPos.y >= maxSafeAlt - 45.0f ? -28.0f : 38.0f;

                Vector3 rightVec = plane.transform.right;
                Vector3 escapePos = myPos + myFwd * 195.0f + rightVec * 195.0f;
                escapePos.y = Mathf.Clamp(myPos.y + climbDelta, groundY + 95.0f, Mathf.Max(groundY + 135.0f, maxSafeAlt));

                breakState.EscapeTargetPos = escapePos;
                breakState.ExpireTime = now + 2.10f;
                breakState.CooldownUntil = now + 4.50f;
                _planeDogfightBreakOff[vehPtr] = breakState;

                plane.followTarget = false;
                if (aiState == AIPlane.PlaneAIState.doingDive || aiState == AIPlane.PlaneAIState.preparingDive)
                {
                    plane.ExitDive();
                }
                plane.flyTargetPosition = escapePos;
                if (plane.nextUpdateCheck < now + 0.30f)
                {
                    plane.nextUpdateCheck = now + 0.30f;
                }

                AiCore.LogDebug("DogfightBreak", $"{myFaction} fighter executed right-hand break turn to avoid head-on collision.");
            }
        }

        private static void LiftPlaneExitDiveTargetAboveTerrain(AIPlane plane, Vector3 pos, Vector3 flatFwd, float minTerrainY)
        {
            float terrMid = ResourcesManager.GetTerrainAltitude(pos + flatFwd * 220f, 0f);
            float terrFar = ResourcesManager.GetTerrainAltitude(pos + flatFwd * 480f, 0f);
            float terrMax = Mathf.Max(minTerrainY, Mathf.Max(terrMid, terrFar));

            Vector3 safeClimb = pos + flatFwd * 650f;
            safeClimb.y = Mathf.Max(plane.flyTargetPosition.y, Mathf.Max(pos.y + 220f, terrMax + 260f));
            plane.flyTargetPosition = safeClimb;
        }

        /// <summary>
        /// Prevents AI aircraft from nosediving into steep mountain slopes during CAS runs
        /// and ensures pilot personalities apply strictly once per state transition instead of 60 Hz drift.
        /// </summary>
        public static void UpdateAircraftDiveTerrainSafetyAndPersonality(AIPlane plane)
        {
            if (plane == null ||
                plane.Pointer == IntPtr.Zero ||
                plane.veh == null ||
                plane.veh.Pointer == IntPtr.Zero ||
                plane.transform == null ||
                !AiCore.IsLocallyOwnedVehicle(plane.veh) ||
                plane.veh.PlayerIsInside() ||
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

            // One-shot entry into exitDive (personality tweak + lift climb target clear of mountain ridges)
            if (st.HasObservedState &&
                curState == AIPlane.PlaneAIState.exitDive &&
                st.LastPlaneState != AIPlane.PlaneAIState.exitDive)
            {
                LiftPlaneExitDiveTargetAboveTerrain(plane, pos, flatFwd, ResourcesManager.GetTerrainAltitude(pos, 0f));

                if (persEnabled)
                {
                    if (pilotPers == SoldierPersonality.Pilot_Daredevil)
                        plane.exitDiveEndTime = Mathf.Max(now + 1.4f, plane.exitDiveEndTime - 0.45f);
                    else if (pilotPers == SoldierPersonality.Pilot_Disciplined)
                        plane.exitDiveEndTime += 0.65f;
                }
            }

            // Prevent overrunning flyTargetPosition mid-pullout (which leaves target behind/below nose)
            if (curState == AIPlane.PlaneAIState.exitDive)
            {
                Vector3 toFly = plane.flyTargetPosition - pos;
                float flatFlySq = toFly.x * toFly.x + toFly.z * toFly.z;
                float fwdDot = toFly.x * flatFwd.x + toFly.z * flatFwd.z;
                if (flatFlySq < 75.0f * 75.0f || fwdDot < 25.0f)
                {
                    plane.flyTargetPosition = new Vector3(
                        pos.x + flatFwd.x * 550.0f,
                        Mathf.Max(plane.flyTargetPosition.y, pos.y + 200.0f),
                        pos.z + flatFwd.z * 550.0f);
                }
            }

            // Flak / AA Self-Preservation Abort: if a non-Kamikaze, non-Daredevil aircraft takes heavy flak/AA damage
            // mid-dive (>= 12 damage spike or >= 50 total damage while taking hits), salvo-drop bombs if near target
            // (and not danger-close to a friendly squad) and immediately pull out of the dive!
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
                    LiftPlaneExitDiveTargetAboveTerrain(plane, pos, flatFwd, Mathf.Max(0f, ResourcesManager.GetTerrainAltitude(pos, 0f)));
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
                            LiftPlaneExitDiveTargetAboveTerrain(plane, pos, flatFwd, Mathf.Max(0f, ResourcesManager.GetTerrainAltitude(pos, 0f)));
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
                        LiftPlaneExitDiveTargetAboveTerrain(plane, pos, flatFwd, maxGroundY);
                        curState = plane.planeState;
                    }
                }
            }

            st.LastPlaneState = curState;
            st.HasObservedState = true;
            _planeAttackReaction[planePtr] = st;
        }

        // =====================================================================
        // Vanilla Co-op Fix: Mid-Air Plane Spawn Hot-Start & Remote Flight Sync
        // 1. VehiclePlane.OnSquadSpawnInside (0x180853800) runs ONLY on the Host and fails over
        //    terrainless ocean/mesh spawners. On remote clients, SyncVehiclePlane does not replicate
        //    engineStarted, throttle, thrustForce, _trgtThrust, farFromGround, or rb.velocity, and
        //    rb.isKinematic can stay true on frame 1—causing dead-stick mid-air spawns!
        // 2. SyncVehiclePlane overrides UpdatePosAndRot (0x180735930) while VehiclePlane.FixedUpdate
        //    (0x18085011B) skips lift & thrust on non-owners (!OwnDriverOrVehicle) yet leaves
        //    rb.useGravity = true! Between network packets, PhysX pulls remote planes down at -9.81 m/s^2
        //    ("falling in columns") until vertical error exceeds 3x outOfSyncDistance and SyncVehiclePosition
        //    teleports them back up ("jumping planes")!
        // =====================================================================
        private static float GetPlaneCruiseVelocityMs(VehiclePlane vp)
        {
            if (vp == null)
                return 60.0f;

            return Mathf.Max(55.0f, Mathf.Max(vp.startLiftVelocity * 1.35f, (vp.maxKmhSpeed / 3.6f) * 0.80f));
        }

        internal static void TryHotStartAirbornePlane(VehiclePlane vp, bool allowAiPilot)
        {
            if (vp == null ||
                vp.Pointer == IntPtr.Zero ||
                !VisionConfig.EnableMod.Value ||
                !VisionConfig.EnableCoopAirborneSpawnFix.Value ||
                !vp.OwnDriverOrVehicle() ||
                (!allowAiPilot && !vp.PlayerIsInside()) ||
                vp.IsDisabled() ||
                vp.transform == null)
            {
                return;
            }

            IntPtr ptr = vp.Pointer;
            float now = Time.time;
            bool inWindow = _planeHotStartWindowUntil.TryGetValue(ptr, out float windowUntil);

            if (_hotStartedPlayerPlanes.Contains(ptr))
            {
                if (!inWindow || now >= windowUntil || vp.isGrounded)
                    return;

                Rigidbody checkRb = vp.rb;
                float minMaintainMs = GetPlaneCruiseVelocityMs(vp) * 0.62f;
                if (vp.engineStarted && (checkRb == null || checkRb.isKinematic || checkRb.velocity.sqrMagnitude >= minMaintainMs * minMaintainMs))
                    return;
            }

            if (!inWindow)
            {
                vp.RefreshGrounded();
                bool onGroundOrRunway =
                    vp.isGrounded ||
                    (vp.transform.position.y < 45.0f &&
                     AiCore.RaycastEnvironment(vp.transform.position + Vector3.up * 1.8f, Vector3.down, 11.0f, vp.transform.root, out _));

                if (onGroundOrRunway)
                {
                    // Plane is on a runway / carrier deck: mark as seen so manual runway startup works normally
                    _hotStartedPlayerPlanes.Add(ptr);
                    return;
                }

                // Airborne spawn confirmed: hold a 2.5s stabilization window so late kinematic release or RPCs cannot stall it!
                windowUntil = now + 2.5f;
                _planeHotStartWindowUntil[ptr] = windowUntil;
            }
            else if (now >= windowUntil)
            {
                _hotStartedPlayerPlanes.Add(ptr);
                return;
            }

            vp.SetPhysicsEnabled(true);
            if (!vp.engineStarted)
                vp.StartEngine();

            vp.engineStartTime = now - 8.0f;
            vp.throttle = 100.0f;
            float maxThrust = Mathf.Max(100.0f, vp.thrustForceMultiplier * 100.0f);
            vp.thrustForce = maxThrust;
            vp._trgtThrust = maxThrust;
            vp.groundLeaveTime = 0f;
            vp.farFromGround = true;
            vp.SetGearClosed();
            vp.isGrounded = false;

            float cruiseMs = GetPlaneCruiseVelocityMs(vp);
            Rigidbody rb = vp.rb;
            if (rb != null)
            {
                if (!rb.useGravity)
                    rb.useGravity = true;

                if (!rb.isKinematic)
                {
                    if (rb.velocity.sqrMagnitude < (cruiseMs * 0.72f) * (cruiseMs * 0.72f))
                    {
                        rb.velocity = vp.transform.forward * cruiseMs;
                    }

                    if (vp.engineStarted && rb.velocity.sqrMagnitude >= (cruiseMs * 0.70f) * (cruiseMs * 0.70f))
                    {
                        if (_hotStartedPlayerPlanes.Add(ptr))
                        {
                            AiCore.LogDebug("PlaneSpawnFix", $"Hot-started airborne plane '{vp.name}' ({cruiseMs:F0}m/s).");
                        }
                    }
                }
            }
        }

        [HarmonyPatch(typeof(Vehicle), nameof(Vehicle.SetPhysicsEnabled))]
        public static class Vehicle_SetPhysicsEnabled_Patch
        {
            public static void Prefix(Vehicle __instance, ref bool __0)
            {
                try
                {
                    if (!__0 &&
                        VisionConfig.EnableMod.Value &&
                        VisionConfig.EnableCoopAirborneSpawnFix.Value &&
                        __instance != null &&
                        __instance.IsAirVehicle() &&
                        !__instance.IsDisabled())
                    {
                        VehiclePlane vp = __instance.TryCast<VehiclePlane>();
                        if (vp != null && (!vp.isGrounded || vp.HasDriverAlive))
                        {
                            __0 = true;
                        }
                    }
                }
                catch { }
            }
        }

        // =====================================================================
        // Host-Side Vanilla-Client Plane Sync Rate & Zero-Speed Guard
        // 1. When a plane has a live pilot, SyncVehiclePlane.OnPhotonSerializeView (0x1807350AA)
        //    returns early and SyncSoldier.OnPhotonSerializeView (0x18073176D) sends the plane's
        //    pos/rot/speed—throttled to `Time.time + 0.40f` (2.5 Hz walking infantry rate!).
        //    At 100 m/s, 40m gaps + uncompensated client gravity cause vanilla clients to sag
        //    and snap-teleport ("jumping planes"); and if Host camera LOD ever zeroed rb.velocity,
        //    sending speed=0 sets allowedOutOfSyncDist=0 on vanilla clients ("falling in columns")!
        // 2. When pilotless/shot-down, SyncVehiclePlane.OnPhotonSerializeView (0x1807350E9) throttles
        //    updates to `Time.time + 1.50f` (once every 1.5s!).
        // Lowering next_position_sync to ~0.08s-0.10s (~10-12 Hz, matching Photon's native tick)
        // keeps 100% identical vanilla wire format while smoothing planes even for UNMODDED clients!
        // =====================================================================
        [HarmonyPatch(typeof(SyncSoldier), nameof(SyncSoldier.OnPhotonSerializeView))]
        public static class SyncSoldier_OnPhotonSerializeView_Patch
        {
            public static void Prefix(SyncSoldier __instance, Photon.Pun.PhotonStream __0)
            {
                try
                {
                    if (__0 == null ||
                        !__0.IsWriting ||
                        !VisionConfig.EnableMod.Value ||
                        !VisionConfig.EnableCoopAirborneSpawnFix.Value ||
                        __instance == null)
                    {
                        return;
                    }

                    Soldier s = __instance.soldier;
                    if (s == null || s.Pointer == IntPtr.Zero || !s.IsOnVehicle())
                        return;

                    Vehicle veh = s.GetCurrentVehicle();
                    if (veh == null || veh.Pointer == IntPtr.Zero || !veh.IsAirVehicle() || veh.IsDisabled())
                        return;

                    VehiclePlane vp = veh.TryCast<VehiclePlane>();
                    if (vp == null || vp.isGrounded)
                        return;

                    if (!vp.IsPhysicsEnabled)
                        vp.SetPhysicsEnabled(true);

                    if (vp.IsOperative() && vp.GetDriver() == s)
                    {
                        Rigidbody rb = vp.rb;
                        if (rb != null && !rb.isKinematic && rb.velocity.sqrMagnitude < 12.0f * 12.0f)
                        {
                            TryHotStartAirbornePlane(vp, true);
                        }
                    }
                }
                catch { }
            }

            public static void Postfix(SyncSoldier __instance, Photon.Pun.PhotonStream __0)
            {
                try
                {
                    if (__0 == null ||
                        !__0.IsWriting ||
                        !VisionConfig.EnableMod.Value ||
                        !VisionConfig.EnableCoopAirborneSpawnFix.Value ||
                        __instance == null)
                    {
                        return;
                    }

                    Soldier s = __instance.soldier;
                    if (s == null || s.Pointer == IntPtr.Zero || !s.IsOnVehicle())
                        return;

                    Vehicle veh = s.GetCurrentVehicle();
                    if (veh == null || veh.Pointer == IntPtr.Zero || !veh.IsAirVehicle() || veh.IsDisabled())
                        return;

                    VehiclePlane vp = veh.TryCast<VehiclePlane>();
                    if (vp == null || vp.isGrounded)
                        return;

                    float targetNextSync = Time.time + 0.08f;
                    if (__instance.next_position_sync > targetNextSync)
                    {
                        __instance.next_position_sync = targetNextSync;
                    }
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(SyncVehiclePlane), nameof(SyncVehiclePlane.OnPhotonSerializeView))]
        public static class SyncVehiclePlane_OnPhotonSerializeView_Patch
        {
            public static void Prefix(SyncVehiclePlane __instance, Photon.Pun.PhotonStream __0)
            {
                try
                {
                    if (__0 == null ||
                        !__0.IsWriting ||
                        !VisionConfig.EnableMod.Value ||
                        !VisionConfig.EnableCoopAirborneSpawnFix.Value ||
                        __instance == null)
                    {
                        return;
                    }

                    Vehicle veh = __instance.vehicle;
                    if (veh == null || veh.Pointer == IntPtr.Zero || veh.IsDisabled())
                        return;

                    VehiclePlane vp = veh.TryCast<VehiclePlane>();
                    if (vp != null && !vp.isGrounded && !vp.IsPhysicsEnabled)
                    {
                        vp.SetPhysicsEnabled(true);
                    }
                }
                catch { }
            }

            public static void Postfix(SyncVehiclePlane __instance, Photon.Pun.PhotonStream __0)
            {
                try
                {
                    if (__0 == null ||
                        !__0.IsWriting ||
                        !VisionConfig.EnableMod.Value ||
                        !VisionConfig.EnableCoopAirborneSpawnFix.Value ||
                        __instance == null)
                    {
                        return;
                    }

                    Vehicle veh = __instance.vehicle;
                    if (veh == null || veh.Pointer == IntPtr.Zero)
                        return;

                    VehiclePlane vp = veh.TryCast<VehiclePlane>();
                    if (vp == null || vp.isGrounded)
                        return;

                    float targetNextSync = Time.time + 0.10f;
                    if (__instance.next_position_sync > targetNextSync)
                    {
                        __instance.next_position_sync = targetNextSync;
                    }
                }
                catch { }
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
                        __instance == null)
                    {
                        return true;
                    }

                    Vehicle veh = __instance.vehicle;
                    if (veh == null || veh.Pointer == IntPtr.Zero)
                        return true;

                    VehiclePlane vp = veh.TryCast<VehiclePlane>();
                    if (vp == null)
                        return true;

                    // Prevent stale SyncVehiclePlane interpolation targets from rubber-banding the local pilot's aircraft
                    if (vp.OwnDriverOrVehicle() && vp.PlayerIsDriving())
                        return false;

                    if (!vp.OwnDriverOrVehicle())
                    {
                        float now = Time.time;
                        IntPtr ptr = vp.Pointer;
                        if (!_nextRemotePlaneGroundCheckTime.TryGetValue(ptr, out float nextGrounded) || now >= nextGrounded)
                        {
                            _nextRemotePlaneGroundCheckTime[ptr] = now + 0.25f;
                            vp.RefreshGrounded();
                        }
                    }
                }
                catch { }
                return true;
            }
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
                        TryHotStartAirbornePlane(__instance, true);
                    }
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(VehiclePlane), nameof(VehiclePlane.FixedUpdate))]
        public static class VehiclePlane_FixedUpdate_Patch
        {
            public static void Prefix(VehiclePlane __instance)
            {
                try
                {
                    if (__instance == null ||
                        __instance.Pointer == IntPtr.Zero ||
                        !VisionConfig.EnableMod.Value ||
                        !VisionConfig.EnableCoopAirborneSpawnFix.Value)
                    {
                        return;
                    }

                    IntPtr ptr = __instance.Pointer;
                    float now = Time.time;

                    if (!__instance.OwnDriverOrVehicle())
                    {
                        if (!_nextRemotePlaneGroundCheckTime.TryGetValue(ptr, out float nextGrounded) || now >= nextGrounded)
                        {
                            _nextRemotePlaneGroundCheckTime[ptr] = now + 0.25f;
                            __instance.RefreshGrounded();
                        }

                        if (!_remotePlaneSpawnSeenTime.TryGetValue(ptr, out float firstSeen))
                        {
                            firstSeen = now;
                            _remotePlaneSpawnSeenTime[ptr] = firstSeen;
                        }

                        Rigidbody rb = __instance.rb;
                        if (rb != null)
                        {
                            bool activeAirborneRemote = !__instance.IsDisabled() && !__instance.isGrounded && __instance.HasDriverAlive;
                            if (activeAirborneRemote)
                            {
                                // Disable local PhysX gravity on non-owned airborne planes so they don't plummet between
                                // network packets ("falling in columns") while SyncVehiclePlane.UpdatePosAndRot drives rb.velocity!
                                if (rb.useGravity)
                                    rb.useGravity = false;

                                SyncVehicle syncher = __instance.vehicleSyncher;
                                bool hasSyncTarget = syncher != null && syncher.syncDestPos.HasValue;
                                if (!rb.isKinematic)
                                {
                                    if (hasSyncTarget)
                                    {
                                        // NOTE: Do NOT zero rb.velocity here—SyncVehiclePlane.UpdatePosAndRot (0x180735930)
                                        // moves airborne remote planes exclusively via VehiclePlane.SetSpeed (rb.velocity)!
                                        if (rb.angularVelocity.sqrMagnitude > 0.001f)
                                            rb.angularVelocity = Vector3.zero;
                                    }
                                    else if (now - firstSeen <= 4.5f && __instance.transform != null)
                                    {
                                        float cruiseMs = GetPlaneCruiseVelocityMs(__instance);
                                        if (rb.velocity.sqrMagnitude < 14.0f * 14.0f)
                                            rb.velocity = __instance.transform.forward * cruiseMs;
                                    }
                                }
                            }
                            else if (!rb.useGravity)
                            {
                                rb.useGravity = true;
                            }
                        }
                        return;
                    }

                    if (__instance.rb != null && !__instance.rb.useGravity)
                    {
                        __instance.rb.useGravity = true;
                    }

                    if (__instance.PlayerIsInside() || !_hotStartedPlayerPlanes.Contains(ptr))
                    {
                        TryHotStartAirbornePlane(__instance, false);
                    }
                }
                catch { }
            }

            public static void Postfix(VehiclePlane __instance)
            {
                try
                {
                    if (__instance == null ||
                        __instance.Pointer == IntPtr.Zero ||
                        !VisionConfig.EnableMod.Value ||
                        !VisionConfig.EnableCoopAirborneSpawnFix.Value)
                    {
                        return;
                    }

                    // Fix vanilla terrainless-ocean bug in VehiclePlane.FixedUpdate (0x180851228):
                    // GetDistanceFromTerrain returns 0 beyond Terrain bounds over ocean, clearing farFromGround
                    // once per second (nextSlowCheck) and popping landing gear open in mid-air!
                    if (!__instance.farFromGround &&
                        !__instance.isGrounded &&
                        !__instance.IsDisabled() &&
                        __instance.HasDriverAlive &&
                        __instance.rb != null &&
                        __instance.rb.velocity.sqrMagnitude > 22.0f * 22.0f &&
                        __instance.transform != null &&
                        !AiCore.RaycastEnvironment(__instance.transform.position + Vector3.up * 1.8f, Vector3.down, 18.0f, __instance.transform.root, out _))
                    {
                        __instance.farFromGround = true;
                        if (__instance.gear_opened)
                            __instance.SetGearClosed();
                    }

                    if (!__instance.OwnDriverOrVehicle())
                        return;

                    if (__instance.rb != null && !__instance.rb.useGravity)
                    {
                        __instance.rb.useGravity = true;
                    }

                    if (__instance.PlayerIsInside() || _planeHotStartWindowUntil.ContainsKey(__instance.Pointer))
                    {
                        TryHotStartAirbornePlane(__instance, true);
                    }
                }
                catch { }
            }
        }
    }
}
