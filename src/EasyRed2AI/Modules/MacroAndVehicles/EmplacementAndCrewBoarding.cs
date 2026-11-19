using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace EasyRed2HumanAI
{
    public static partial class VeteranAndTacticsPatches
    {
        private static void RunProactiveEmplacementStaffing(float now)
        {
            var allVehicles = Vehicle.allVehicles;
            if (allVehicles == null)
                return;

            int vCount = allVehicles.Count;
            if (vCount == 0)
                return;

            AiCore.EnsureWorldSnapshot();
            int sqCount = AiCore.WorldSquadCount;
            if (sqCount == 0)
                return;

            var squads = AiCore.WorldSquads;
            var soldiers = AiCore.WorldSoldiers;

            // Incremental round-robin scan: inspect at most 10 vehicles AND cap full squad-matching candidate evaluations
            // at 3 open static guns per director slice so maps with 50+ bunker MGs / flak guns NEVER spike a single frame!
            int maxInspect = Mathf.Min(vCount, 10);
            if (_emplacementScanCursor < 0 || _emplacementScanCursor >= vCount)
                _emplacementScanCursor = 0;

            int stepsTaken = 0;
            int staticEvaluations = 0;

            for (int step = 0; step < maxInspect; step++)
            {
                stepsTaken++;
                int i = (_emplacementScanCursor + step) % vCount;
                Vehicle veh = allVehicles[i];
                if (veh == null || veh.Pointer == IntPtr.Zero || veh.transform == null)
                    continue;

                // Note: Vehicle.CanFight() (0x18085B7E0) returns false whenever a vehicle/emplacement is empty!
                // Match vanilla Vehicle.IsCoverAvailable (0x18085F9A0) by also rejecting IsLocked() and out-of-ammo turrets
                // so bots never walk toward Lua-locked/prop emplacements or dry guns.
                if (!AiCore.IsLocallyOwnedVehicle(veh) || veh.IsLocked() || veh.IsDisabled() || veh.IsCoverDestroyed() || veh.IsAirVehicle() || veh.PlayerIsInside())
                    continue;

                bool isStaticGun = veh.TryCast<MovableVehicle>() == null;
                bool isTransport = !isStaticGun && veh.IsTransportVehicle();
                bool isUnmannedCombatVehicle = !isStaticGun && !veh.HasDriver() && !isTransport;
                Vector3 vehPos = veh.transform.position;
                IntPtr vehPtr = veh.Pointer;

                // 0. Empty Map-Parked Transport Requisition (Jeeps, Trucks, Halftracks, APCs):
                //    Allow unengaged rear-echelon AI foot squads (> 125m from objective, out of contact) within <= 45m
                //    to motorize to the front when no human player is within 32m!
                if (isTransport &&
                    !veh.HasDriver() &&
                    veh.peopleInside <= 0 &&
                    !veh.CanBeRepaired() &&
                    !_vehiclesCrewRetreatedFrom.Contains(vehPtr) &&
                    !IsAnyHumanPlayerWithinRadius(soldiers, AiCore.WorldSoldierCount, vehPos, 32.0f))
                {
                    string vehFac = veh.GetVehicleFaction();
                    int seatCap = veh.seats != null ? veh.seats.Length : 0;
                    if (seatCap >= 3)
                    {
                        Squad bestRearSq = null;
                        float bestRearDistSq = 45.0f * 45.0f;
                        for (int s = 0; s < sqCount; s++)
                        {
                            ref AiCore.SquadSnapshot sqSnap = ref squads[s];
                            if (!sqSnap.LeaderIsAlive ||
                                sqSnap.IsVehicleCrew ||
                                sqSnap.LeaderOnVehicle ||
                                sqSnap.IsPlayerInSquad ||
                                sqSnap.UnitCount < 2 ||
                                sqSnap.UnitCount > seatCap ||
                                _squadOrderedToVehicle.ContainsKey(sqSnap.Ptr) ||
                                _garrisonAssignedObjectiveBySquad.ContainsKey(sqSnap.Ptr))
                            {
                                continue;
                            }

                            if (!string.IsNullOrEmpty(vehFac) && AiCore.AreEnemyFactions(vehFac, sqSnap.Faction))
                                continue;

                            float dSq = (sqSnap.RefPos - vehPos).sqrMagnitude;
                            if (dSq >= bestRearDistSq)
                                continue;

                            Squad candSq = sqSnap.Ref;
                            Soldier candLeader = sqSnap.Leader;
                            if (candSq == null ||
                                candLeader == null ||
                                !AiCore.IsLocallyOwnedBot(candLeader) ||
                                candSq.InChargeTime() ||
                                IsSquadActivelyRetreating(sqSnap.Ptr, now) ||
                                candLeader.GetSuppressionValue() >= 6.0f ||
                                candSq.currentTask == null ||
                                candSq.currentTask.Pointer == IntPtr.Zero)
                            {
                                continue;
                            }

                            if (_squadBlackboards.TryGetValue(sqSnap.Ptr, out SquadBlackboard tBoard) &&
                                (tBoard.HasActiveThreat || tBoard.IsDefendingObjective))
                            {
                                continue;
                            }

                            Vector3 taskDest = candSq.currentTask.GetTaskDestination();
                            if ((taskDest - sqSnap.RefPos).sqrMagnitude < 125.0f * 125.0f)
                                continue;

                            bestRearDistSq = dSq;
                            bestRearSq = candSq;
                        }

                        if (bestRearSq != null && (!Photon.Pun.PhotonNetwork.InRoom || AiCore.TryConsumeBotRpcBudget(0.45f)))
                        {
                            try
                            {
                                _squadOrderedToVehicle[bestRearSq.Pointer] = vehPtr;
                                bestRearSq.SendUnitsToVehicle(veh);
                                StaticGunsStaffedCount++;
                                TrySayTacticalVoice(bestRearSq.Leader, bestRearSq, VoiceManager.VoiceClip.illTakeHisSeat, 0.15f);
                            }
                            catch { }
                            continue;
                        }
                    }
                }

                Turret mainTurret = veh.GetMainTurret(false);
                if (mainTurret == null || mainTurret.WeaponsCount() <= 0)
                    continue;

                bool hasAmmo = HasAnyEmplacementTurretAmmo(veh, mainTurret);

                if (isStaticGun)
                {
                    TryReleaseStaleStaticEmplacementCrew(veh, mainTurret, vehPos, hasAmmo, now, squads, sqCount);
                }

                if (!hasAmmo)
                    continue;

                if (!isStaticGun && mainTurret.GetSittedUnit(veh) != null)
                    continue;

                string occupantFaction = GetFirstAliveVehicleOccupantFaction(veh);

                // 1. If this is an empty/abandoned movable combat vehicle (Tank, Armored Car)
                //    and a bailed-out Vehicle Crew (`sq.IsVehicleCrew`) with >= 2 alive crewmen (driver + gunner) is within 85m,
                //    order all alive members of that crew (including the Leader) to re-mount it!
                //    Skip vehicles already abandoned via RetreatCrew or damaged without an equipped crew mechanic (prevents mount/eject loops).
                if (isUnmannedCombatVehicle && !_vehiclesCrewRetreatedFrom.Contains(vehPtr))
                {
                    Squad bestBailedCrew = null;
                    float bestCrewDistSq = 85.0f * 85.0f;
                    bool vehNeedsRepair = veh.CanBeRepaired();

                    for (int s = 0; s < sqCount; s++)
                    {
                        ref AiCore.SquadSnapshot sqSnap = ref squads[s];
                        if (!sqSnap.IsVehicleCrew || !sqSnap.LeaderIsAlive || sqSnap.IsPlayerInSquad || sqSnap.LeaderOnVehicle || sqSnap.UnitCount < 2)
                            continue;

                        if (occupantFaction != null && AiCore.AreEnemyFactions(occupantFaction, sqSnap.Faction))
                            continue;

                        float dSq = (sqSnap.RefPos - vehPos).sqrMagnitude;
                        if (dSq >= bestCrewDistSq)
                            continue;

                        Squad sq = sqSnap.Ref;
                        Soldier leader = sqSnap.Leader;
                        if (sq == null || sq.SomeoneIsRepairing() || !AiCore.IsLocallyOwnedBot(leader))
                            continue;

                        if (vehNeedsRepair)
                        {
                            Soldier mech = sq.GetMechanic();
                            if (mech == null || !mech.IsAlive || mech.GetRepairTools() == null)
                                continue;
                        }

                        int aliveFootCrew = 0;
                        int uEnd = sqSnap.UnitStartIndex + sqSnap.UnitCount;
                        for (int cIdx = sqSnap.UnitStartIndex; cIdx < uEnd; cIdx++)
                        {
                            if (!soldiers[cIdx].IsOnVehicle)
                                aliveFootCrew++;
                        }
                        if (aliveFootCrew < 2)
                            continue;

                        bestCrewDistSq = dSq;
                        bestBailedCrew = sq;
                    }

                    if (bestBailedCrew != null && bestBailedCrew.units != null)
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
                                    _assignedEmplacementUntil[cUnit.Pointer] = now + 20.0f;
                                    _assignedEmplacementVehicle[cUnit.Pointer] = vehPtr;
                                    cUnit.CoverPosition(crewVehDest);
                                }
                            }
                            StaticGunsStaffedCount++;
                            TrySayTacticalVoice(bestBailedCrew.Leader, bestBailedCrew, VoiceManager.VoiceClip.illTakeHisSeat, 0.15f);
                        }
                        catch { }
                        continue;
                    }

                    // 1B. Empty Map-Parked Operational Combat Vehicles (Tanks, Armored Cars, Armed Halftracks):
                    //     When no bailed tank crew is nearby and no human player is within 32m, allow a depleted 2-3 man
                    //     AI foot remnant squad within <= 55m to crew map-placed armor so operational tanks never sit idle!
                    if (!vehNeedsRepair &&
                        veh.peopleInside <= 0 &&
                        !IsAnyHumanPlayerWithinRadius(soldiers, AiCore.WorldSoldierCount, vehPos, 32.0f))
                    {
                        string vehFac = veh.GetVehicleFaction();
                        Squad bestRemnantSq = null;
                        float bestRemDistSq = 55.0f * 55.0f;
                        for (int s = 0; s < sqCount; s++)
                        {
                            ref AiCore.SquadSnapshot sqSnap = ref squads[s];
                            if (sqSnap.IsVehicleCrew ||
                                !sqSnap.LeaderIsAlive ||
                                sqSnap.IsPlayerInSquad ||
                                sqSnap.LeaderOnVehicle ||
                                sqSnap.UnitCount < 2 ||
                                sqSnap.UnitCount > 3 ||
                                _squadOrderedToVehicle.ContainsKey(sqSnap.Ptr))
                            {
                                continue;
                            }

                            if (!string.IsNullOrEmpty(vehFac) && AiCore.AreEnemyFactions(vehFac, sqSnap.Faction))
                                continue;
                            if (occupantFaction != null && AiCore.AreEnemyFactions(occupantFaction, sqSnap.Faction))
                                continue;

                            float dSq = (sqSnap.RefPos - vehPos).sqrMagnitude;
                            if (dSq >= bestRemDistSq)
                                continue;

                            Squad sq = sqSnap.Ref;
                            Soldier leader = sqSnap.Leader;
                            if (sq == null || leader == null || !AiCore.IsLocallyOwnedBot(leader) || sq.InChargeTime() || IsSquadActivelyRetreating(sqSnap.Ptr, now))
                                continue;

                            bestRemDistSq = dSq;
                            bestRemnantSq = sq;
                        }

                        if (bestRemnantSq != null && (!Photon.Pun.PhotonNetwork.InRoom || AiCore.TryConsumeBotRpcBudget(0.45f)))
                        {
                            try
                            {
                                _squadOrderedToVehicle[bestRemnantSq.Pointer] = vehPtr;
                                bestRemnantSq.SendUnitsToVehicle(veh);
                                StaticGunsStaffedCount++;
                                TrySayTacticalVoice(bestRemnantSq.Leader, bestRemnantSq, VoiceManager.VoiceClip.illTakeHisSeat, 0.15f);
                            }
                            catch { }
                            continue;
                        }
                    }
                }

                // Never assign regular foot soldiers to driverless movable vehicles (halftracks, trucks, jeeps, tanks) —
                // only staff true static guns/emplacements (`isStaticGun`) that have an open turret OR loader/assistant seat!
                if (!isStaticGun || !TryGetAvailableEmplacementOrTurretSeatIndex(veh, out _, out int openStaticSeats))
                    continue;

                // Count/expire inbound bots in O(K) over _assignedEmplacementVehicle (typically 0-4 bots total across map)
                // instead of wrapping u.targetDestination on every soldier across every nearby squad!
                int inboundBotsCount = 0;
                if (_assignedEmplacementVehicle.Count > 0)
                {
                    _reusableExpiredEmplacementBots.Clear();
                    foreach (var kvp in _assignedEmplacementVehicle)
                    {
                        if (kvp.Value != vehPtr)
                            continue;

                        IntPtr botPtr = kvp.Key;
                        if (!AiCore.TryGetSoldierHandles(botPtr, out AiCore.SoldierFastHandles bHandles) ||
                            !bHandles.Ref.IsAlive ||
                            bHandles.Ref.IsOnVehicle())
                        {
                            _reusableExpiredEmplacementBots.Add(botPtr);
                            continue;
                        }

                        if (_assignedEmplacementUntil.TryGetValue(botPtr, out float claimUntil) && now >= claimUntil)
                        {
                            bHandles.Ref.CoverPosition(null);
                            _reusableExpiredEmplacementBots.Add(botPtr);
                            continue;
                        }

                        if (!bHandles.Ref.HasDestinationAssigned ||
                            AiCore.ResolveTargetDestination(bHandles) == AiCore.CachedDestKind.None ||
                            bHandles.CachedDestPtr != vehPtr)
                        {
                            _reusableExpiredEmplacementBots.Add(botPtr);
                            continue;
                        }

                        inboundBotsCount++;
                    }

                    for (int eIdx = 0; eIdx < _reusableExpiredEmplacementBots.Count; eIdx++)
                    {
                        ClearEmplacementAssignment(_reusableExpiredEmplacementBots[eIdx], markUnreachable: true, 90.0f);
                    }
                    _reusableExpiredEmplacementBots.Clear();
                }

                if (inboundBotsCount >= openStaticSeats)
                    continue;

                staticEvaluations++;
                Soldier bestCandidate = null;
                float bestScoreDistSq = 65.0f * 65.0f;
                float bestRealDistSq = 0f;
                bool canShoot360 = mainTurret.CanShoot360;
                Vector3 vehFwd = veh.transform.forward;
                bool playerOnThisStaticGun = isStaticGun && veh.PlayerIsInside();
                Squad staticSquadInside = playerOnThisStaticGun ? veh.GetSquadInside() : null;

                for (int s = 0; s < sqCount; s++)
                {
                    ref AiCore.SquadSnapshot sqSnap = ref squads[s];
                    if (!sqSnap.LeaderIsAlive || sqSnap.UnitCount == 0)
                        continue;

                    bool isPlayerJoiningEmplacement = false;
                    if (sqSnap.IsPlayerInSquad)
                    {
                        if (!playerOnThisStaticGun || sqSnap.Ref == null)
                            continue;

                        if (sqSnap.Ref.order == Order.defend &&
                            sqSnap.Ref.moveOrderPosition.sqrMagnitude > 0.1f &&
                            (vehPos - sqSnap.Ref.moveOrderPosition).sqrMagnitude > 35.0f * 35.0f)
                        {
                            continue;
                        }

                        bool playerFromSqOnGun = staticSquadInside != null && staticSquadInside.Pointer == sqSnap.Ptr;
                        if (!playerFromSqOnGun && veh.seats != null)
                        {
                            var seats = veh.seats;
                            int sLen = seats.Length;
                            for (int si = 0; si < sLen; si++)
                            {
                                Soldier occ = seats[si] != null ? seats[si].unitSet : null;
                                if (occ != null && occ.IsPlayer() && occ.joinedSquad != null && occ.joinedSquad.Pointer == sqSnap.Ptr)
                                {
                                    playerFromSqOnGun = true;
                                    break;
                                }
                            }
                        }
                        if (!playerFromSqOnGun)
                            continue;
                        isPlayerJoiningEmplacement = true;
                    }
                    else if (sqSnap.LeaderOnVehicle)
                    {
                        continue;
                    }

                    // Never send a soldier to board a multi-seat gun that currently has a live enemy occupant!
                    if (occupantFaction != null && AiCore.AreEnemyFactions(occupantFaction, sqSnap.Faction))
                        continue;

                    Squad sq = sqSnap.Ref;
                    Soldier leader = sqSnap.Leader;
                    if (sq == null || leader == null)
                        continue;

                    bool isBailedCrew = sqSnap.IsVehicleCrew && !sq.SomeoneIsRepairing();
                    if (!isBailedCrew && (sq.InChargeTime() || IsSquadActivelyRetreating(sq.Pointer, now)))
                        continue;

                    bool isDefendingSquad = _garrisonAssignedObjectiveBySquad.ContainsKey(sqSnap.Ptr) ||
                                            (_squadBlackboards.TryGetValue(sqSnap.Ptr, out SquadBlackboard defBb) && defBb.IsDefendingObjective);
                    float empMult = GetFactionCommandProfile(sqSnap.Faction).EmplacementSearchMult;
                    float baseSquadReach = isPlayerJoiningEmplacement
                        ? 36.0f
                        : (isBailedCrew ? 75.0f : (isDefendingSquad ? 55.0f : 42.0f));
                    float maxSquadDist = isPlayerJoiningEmplacement ? 36.0f : (baseSquadReach * empMult);
                    if ((sqSnap.RefPos - vehPos).sqrMagnitude > maxSquadDist * maxSquadDist)
                        continue;

                    // Keep regular infantry squads tethered near their active squad order area so advancing squads
                    // never peel riflemen 50m+ backwards off an assault axis to chase distant rear bunkers,
                    // while allowing defenders up to 50m reach across their strongpoint's AT/AA/coastal/MG pits!
                    if (!isPlayerJoiningEmplacement && !isBailedCrew && sq.order != Order.follow && sq.moveOrderPosition.sqrMagnitude > 0.1f)
                    {
                        float maxOrderOffset = isDefendingSquad
                            ? Mathf.Max(sq.moveOrderRadius + 36.0f, 52.0f)
                            : Mathf.Max(sq.moveOrderRadius + 22.0f, 34.0f);
                        if ((vehPos - sq.moveOrderPosition).sqrMagnitude > maxOrderOffset * maxOrderOffset)
                            continue;
                    }

                    // Check pure C# enemy-cluster overrun (< 22m) BEFORE computing threat direction!
                    bool emplacementOverrun = false;
                    for (int c = 0; c < _knownEnemyClusters.Count; c++)
                    {
                        if (!AiCore.AreSameSideFactions(_knownEnemyClusters[c].Faction, sqSnap.Faction) &&
                            (_knownEnemyClusters[c].Position - vehPos).sqrMagnitude <= 22.0f * 22.0f)
                        {
                            emplacementOverrun = true;
                            break;
                        }
                    }
                    if (emplacementOverrun)
                        continue;

                    // Verify the turret can aim toward the squad's threat sector or enemy approach axis (fast blackboard lookup first!)
                    if (!canShoot360 && !isPlayerJoiningEmplacement)
                    {
                        Vector3 threatDir = (_squadBlackboards.TryGetValue(sqSnap.Ptr, out SquadBlackboard bb) &&
                                             bb.HasActiveThreat &&
                                             bb.ThreatDirection.sqrMagnitude > 0.01f)
                            ? bb.ThreatDirection
                            : GetEffectiveThreatDirection(leader, sq, vehPos, vehFwd);

                        if (threatDir.sqrMagnitude > 0.01f && !mainTurret.IsPositionInDirection(threatDir))
                            continue;
                    }

                    int uEnd = sqSnap.UnitStartIndex + sqSnap.UnitCount;
                    float maxSoldierDistSq = (isPlayerJoiningEmplacement || !isDefendingSquad)
                        ? (isBailedCrew ? (65.0f * 65.0f) : (32.0f * 32.0f))
                        : (50.0f * 50.0f);
                    for (int uIdx = sqSnap.UnitStartIndex; uIdx < uEnd; uIdx++)
                    {
                        ref AiCore.SoldierSnapshot uSnap = ref soldiers[uIdx];
                        if (uSnap.IsOnVehicle || uSnap.IsPlayer || uSnap.HasSurrendered)
                            continue;

                        // Avoid assigning ground infantry to unreachable rooftop/cliff emplacements (> 4.5m vertical delta)
                        if (Mathf.Abs(vehPos.y - uSnap.Pos.y) > 4.5f)
                            continue;

                        float rawDistSq = (uSnap.Pos - vehPos).sqrMagnitude;
                        if (rawDistSq > maxSoldierDistSq)
                            continue;

                        float scoreDistSq = isPlayerJoiningEmplacement
                            ? rawDistSq * 0.25f
                            : (isBailedCrew ? rawDistSq * 0.45f : rawDistSq);
                        if (scoreDistSq >= bestScoreDistSq)
                            continue;

                        // Allow bailed-out vehicle crew leaders to crew static guns/artillery, but keep infantry Squad Leaders free
                        if (uSnap.IsSquadLeader && !isBailedCrew)
                            continue;

                        if (_assignedEmplacementUntil.TryGetValue(uSnap.Ptr, out float assignedUntil))
                        {
                            if (now < assignedUntil)
                                continue;
                            ClearEmplacementAssignment(uSnap.Ptr, markUnreachable: true, 90.0f);
                            continue;
                        }

                        if (_unreachableEmplacementUntil.TryGetValue(uSnap.Ptr, out float unreachableUntil) && now < unreachableUntil)
                            continue;

                        Soldier u = uSnap.Ref;
                        if (u == null || !AiCore.IsLocallyOwnedBot(u))
                            continue;

                        if (u.hasSyringe || u.IsRadioman() || u.isBleeding || u.dontLeaveVehicle)
                            continue;

                        if (u.GetSuppressionValue() >= 30f ||
                            (u.aiController != null && u.aiController.HasEnemyAndCanFireAtHim && u.aiController.distFromTarget < 28f))
                        {
                            continue;
                        }

                        if (u.Params != null && (!u.Params.canMove || u.Params.neverLeaveVehicle))
                            continue;

                        bestScoreDistSq = scoreDistSq;
                        bestRealDistSq = rawDistSq;
                        bestCandidate = u;
                    }
                }

                if (bestCandidate != null)
                {
                    _assignedEmplacementUntil[bestCandidate.Pointer] = now + 18.0f;
                    _assignedEmplacementVehicle[bestCandidate.Pointer] = vehPtr;
                    if (AiCore.TryGetSoldierHandles(bestCandidate.Pointer, out var candHandles))
                        candHandles.Stance.CombatCrouchLockUntil = 0f;
                    if (bestCandidate.aiController != null)
                    {
                        SetBotPose(bestCandidate.aiController, bestCandidate, SoldierPose.Idle, true);
                    }
                    AiDestination vehDest = veh.Cast<AiDestination>();
                    AiCore.AssignBotCoverOrDestination(bestCandidate.aiController, bestCandidate, vehDest);
                    StaticGunsStaffedCount++;
                    TrySayTacticalVoice(bestCandidate, bestCandidate.joinedSquad, VoiceManager.VoiceClip.illTakeHisSeat, 0.2f);
                    if (VisionConfig.DebugLogging.Value)
                    {
                        AiCore.LogDebug("Emplacement", $"{AiCore.GetCachedFaction(bestCandidate)} soldier assigned to crew emplacement seat ({Mathf.Sqrt(bestRealDistSq):F0}m away, openSeats:{openStaticSeats}).");
                    }
                }

                if (staticEvaluations >= 3)
                    break;
            }

            _emplacementScanCursor = (_emplacementScanCursor + stepsTaken) % vCount;
        }

        private static bool IsAnyHumanPlayerWithinRadius(
            AiCore.SoldierSnapshot[] soldiers,
            int soldierCount,
            Vector3 targetPos,
            float radius)
        {
            if (soldiers == null || soldierCount <= 0)
                return false;

            float rSq = radius * radius;
            for (int i = 0; i < soldierCount; i++)
            {
                if (soldiers[i].IsPlayer && (soldiers[i].Pos - targetPos).sqrMagnitude <= rSq)
                    return true;
            }
            return false;
        }

        private static string GetObjectiveDebugLabel(MissionObjective obj)
        {
            if (obj == null)
                return "unknown";
            string disp = obj.GetDisplayName();
            if (!string.IsNullOrWhiteSpace(disp))
                return disp;
            string raw = obj.objectiveName;
            if (!string.IsNullOrWhiteSpace(raw))
                return raw;
            Vector3 p = obj.GetTaskPosition();
            return $"({p.x:F0},{p.z:F0})";
        }

        internal static bool IsActivelyHeadingToEmplacement(Soldier soldier, float now)
        {
            if (soldier == null)
                return false;

            IntPtr ptr = soldier.Pointer;
            if (!_assignedEmplacementUntil.TryGetValue(ptr, out float empUntil))
                return false;

            if (soldier.IsOnVehicle())
            {
                ClearEmplacementAssignment(ptr, markUnreachable: false);
                return false;
            }

            if (now >= empUntil)
            {
                ClearEmplacementAssignment(ptr, markUnreachable: true, 90.0f);
                return false;
            }

            if (!soldier.HasDestinationAssigned || soldier.targetDestination == null)
            {
                ClearEmplacementAssignment(ptr, markUnreachable: true, 45.0f);
                return false;
            }

            if (_assignedEmplacementVehicle.TryGetValue(ptr, out IntPtr expectedVehPtr) &&
                expectedVehPtr != IntPtr.Zero &&
                soldier.targetDestination.Pointer != expectedVehPtr)
            {
                ClearEmplacementAssignment(ptr, markUnreachable: true, 45.0f);
                return false;
            }

            return true;
        }

        private static void ClearEmplacementAssignment(IntPtr soldierPtr, bool markUnreachable, float cooldownSeconds = 120.0f)
        {
            if (soldierPtr == IntPtr.Zero)
                return;
            _assignedEmplacementUntil.Remove(soldierPtr);
            _assignedEmplacementVehicle.Remove(soldierPtr);
            if (markUnreachable)
            {
                _unreachableEmplacementUntil[soldierPtr] = Time.time + cooldownSeconds;
            }
        }

        internal static void TryReleaseStaleStaticEmplacementCrew(
            Vehicle veh,
            Turret mainTurret,
            Vector3 vehPos,
            bool hasAmmo,
            float now,
            AiCore.SquadSnapshot[] squads,
            int sqCount)
        {
            if (veh == null || mainTurret == null || veh.seats == null)
                return;

            var seats = veh.seats;
            int seatCount = seats.Length;
            Soldier firstOcc = null;
            for (int i = 0; i < seatCount; i++)
            {
                Seats s = seats[i];
                Soldier occ = s != null ? s.unitSet : null;
                if (occ != null && occ.IsAlive && AiCore.IsLocallyOwnedBot(occ))
                {
                    firstOcc = occ;
                    break;
                }
            }

            if (firstOcc == null)
                return;

            string occFaction = AiCore.GetCachedFaction(firstOcc);
            if (string.IsNullOrEmpty(occFaction))
                return;

            RefreshCombinedArmsIntelIfNeeded();

            bool outOfAmmo = !hasAmmo;
            bool flankedBlindSpot = false;
            bool enemyInFiringSector = false;
            bool hasLiveTarget = veh.CurrentVisibleTarget != null && veh.CurrentVisibleTarget.Pointer != IntPtr.Zero;
            bool canShoot360 = mainTurret.CanShoot360;

            for (int c = 0; c < _knownEnemyClusters.Count; c++)
            {
                var cluster = _knownEnemyClusters[c];
                if (!AiCore.AreEnemyFactions(occFaction, cluster.Faction))
                    continue;

                Vector3 toEnemy = cluster.Position - vehPos;
                float dSq = toEnemy.sqrMagnitude;
                bool inArc = canShoot360 || (dSq > 0.25f && mainTurret.IsPositionInDirection(toEnemy / Mathf.Sqrt(dSq)));

                if (!canShoot360 && !inArc && dSq <= 22.0f * 22.0f)
                    flankedBlindSpot = true;
                if (inArc && dSq <= 155.0f * 155.0f)
                    enemyInFiringSector = true;
            }

            // Close flanking / storming infantry check directly against WorldSoldiers:
            // bail if an enemy is within 18m outside the gun's traverse arc, or within 8.5m while
            // the barrel points >55 deg away or the weapon is reloading.
            if (!flankedBlindSpot)
            {
                TurretGun mainGun = mainTurret.TryCast<TurretGun>();
                TurretWeapon selWep = mainGun != null ? mainGun.selectedWeapon : null;
                bool gunReloading = selWep != null && selWep.IsReloading();
                Vector3 gunFwd = mainTurret.GetFireDir();
                int solCount = AiCore.WorldSoldierCount;
                var soldiers = AiCore.WorldSoldiers;
                const float maxFlankSq = 18.0f * 18.0f;
                const float stormSq = 8.5f * 8.5f;

                for (int j = 0; j < solCount; j++)
                {
                    ref AiCore.SoldierSnapshot sol = ref soldiers[j];
                    if (sol.HasSurrendered || sol.IsInjured || !AiCore.AreEnemyFactions(occFaction, sol.Faction))
                        continue;

                    Vector3 toEnemy = sol.Pos - vehPos;
                    if (Mathf.Abs(toEnemy.y) > 6.0f)
                        continue;

                    float dSq = toEnemy.sqrMagnitude;
                    if (dSq > maxFlankSq)
                        continue;

                    bool inArc = canShoot360 || (dSq > 0.25f && mainTurret.IsPositionInDirection(toEnemy / Mathf.Sqrt(dSq)));
                    if (!canShoot360 && !inArc)
                    {
                        flankedBlindSpot = true;
                        break;
                    }

                    if (dSq <= stormSq && (gunReloading || Vector3.Angle(gunFwd, toEnemy) > 55.0f))
                    {
                        flankedBlindSpot = true;
                        break;
                    }
                }
            }

            bool anyFriendlySquadNear = false;
            if (!hasLiveTarget && !enemyInFiringSector && !outOfAmmo && !flankedBlindSpot)
            {
                for (int s = 0; s < sqCount; s++)
                {
                    ref AiCore.SquadSnapshot sqSnap = ref squads[s];
                    if (sqSnap.IsVehicleCrew ||
                        !sqSnap.LeaderIsAlive ||
                        AiCore.AreEnemyFactions(occFaction, sqSnap.Faction))
                    {
                        continue;
                    }

                    if ((sqSnap.RefPos - vehPos).sqrMagnitude <= 72.0f * 72.0f)
                    {
                        anyFriendlySquadNear = true;
                        break;
                    }
                }
            }

            bool inConcreteBunker = AiCore.HasLowCeilingOverhead(vehPos, 3.8f);
            bool nearBlast = VisionPatches.TryGetNearestRecentExplosion(vehPos, inConcreteBunker ? 6.5f : 13.5f, 2.5f, out _, out _);

            for (int i = 0; i < seatCount; i++)
            {
                Seats s = seats[i];
                Soldier occ = s != null ? s.unitSet : null;
                if (occ == null || !occ.IsAlive || !AiCore.IsLocallyOwnedBot(occ))
                    continue;

                var occParams = occ.Params;
                if (occParams != null && !occParams.canMove)
                    continue;

                float occSupp = occ.GetSuppressionValue();
                bool selfPreservationBail = occ.isBleeding ||
                                            nearBlast ||
                                            (!inConcreteBunker && s.showUnitSet && (occSupp >= 62f || (occSupp >= 45f && !hasLiveTarget)));

                bool emergencyBail = selfPreservationBail || flankedBlindSpot || outOfAmmo;
                if (!emergencyBail && (occ.dontLeaveVehicle || (occParams != null && occParams.neverLeaveVehicle)))
                    continue;

                bool soldierSquadMovedOn = false;
                if (!hasLiveTarget && !enemyInFiringSector && !emergencyBail)
                {
                    Squad sq = occ.joinedSquad;
                    Soldier leader = sq != null ? sq.Leader : null;
                    if (leader != null &&
                        leader.IsAlive &&
                        leader.Pointer != occ.Pointer &&
                        !leader.IsOnVehicle() &&
                        leader.transform != null)
                    {
                        float leaderDistSq = (leader.transform.position - vehPos).sqrMagnitude;
                        if (leaderDistSq > 68.0f * 68.0f)
                        {
                            soldierSquadMovedOn = true;
                        }
                        else if (sq.currentTask != null &&
                                 leaderDistSq > 48.0f * 48.0f &&
                                 (sq.currentTask.GetTaskDestination() - vehPos).sqrMagnitude > 95.0f * 95.0f)
                        {
                            soldierSquadMovedOn = true;
                        }
                    }
                    else if (sq != null &&
                             leader != null &&
                             leader.Pointer == occ.Pointer &&
                             sq.order != Order.defend &&
                             sq.currentTask != null &&
                             (sq.currentTask.GetTaskDestination() - vehPos).sqrMagnitude > 105.0f * 105.0f)
                    {
                        soldierSquadMovedOn = true;
                    }
                }

                if (!emergencyBail &&
                    (hasLiveTarget || enemyInFiringSector || (anyFriendlySquadNear && !soldierSquadMovedOn)))
                {
                    continue;
                }

                try
                {
                    ClearEmplacementAssignment(occ.Pointer, markUnreachable: true, (selfPreservationBail || flankedBlindSpot) ? 45.0f : 90.0f);
                    if (occ.dontLeaveVehicle)
                        occ.dontLeaveVehicle = false;
                    occ.CoverPosition(null);
                    if (occ.IsOnVehicle())
                        veh.GetOffVehicle(occ);

                    if ((selfPreservationBail || flankedBlindSpot) && occ.aiController != null)
                    {
                        TrySeekNearbyCover(occ.aiController, occ);
                        SetBotPose(occ.aiController, occ, SoldierPose.Crouch, true);
                    }

                    if (VisionConfig.DebugLogging.Value)
                    {
                        string reason = outOfAmmo
                            ? "out_of_ammo"
                            : (selfPreservationBail
                                ? "self_preservation"
                                : (flankedBlindSpot ? "flanked_blind_spot" : "front_moved_on"));
                        AiCore.LogDebug("Emplacement", $"{occFaction} soldier dismounted static gun ({reason}).", 4.0f);
                    }
                }
                catch { }
            }
        }

        private static string GetFirstAliveVehicleOccupantFaction(Vehicle veh)
        {
            if (veh == null || veh.seats == null)
                return null;

            var seats = veh.seats;
            int count = seats.Length;
            for (int i = 0; i < count; i++)
            {
                Seats s = seats[i];
                Soldier occ = s != null ? s.unitSet : null;
                if (occ != null && occ.IsAlive)
                {
                    string f = AiCore.GetCachedFaction(occ);
                    if (!string.IsNullOrEmpty(f))
                        return f;
                }
            }
            return null;
        }

        private static bool HasHostileVehicleOccupant(Vehicle veh, string faction)
        {
            if (string.IsNullOrEmpty(faction))
                return false;
            string occFac = GetFirstAliveVehicleOccupantFaction(veh);
            return occFac != null && AiCore.AreEnemyFactions(occFac, faction);
        }

        private static bool HasAnyEmplacementTurretAmmo(Vehicle veh, Turret mainTurret)
        {
            if (mainTurret != null)
            {
                TurretGun mainGun = mainTurret.TryCast<TurretGun>();
                if (mainGun != null && mainGun.HasAmmo())
                    return true;
                if (mainGun == null)
                    return true;
            }

            if (veh != null && veh.seats != null)
            {
                var seats = veh.seats;
                int count = seats.Length;
                for (int i = 0; i < count; i++)
                {
                    Seats s = seats[i];
                    if (s != null && s.hasTurret && s.connectedTurret != null)
                    {
                        TurretGun tg = s.connectedTurret.TryCast<TurretGun>();
                        if (tg != null && tg.HasAmmo())
                            return true;
                    }
                }
            }
            return false;
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

        /// <summary>
        /// For movable vehicles, returns only open armed turret seats (<see cref="TryGetAvailableTurretSeatIndex"/>).
        /// For static emplacements (AT guns, field guns, howitzers, AA guns with 2 crew seats), prioritizes the
        /// gunner/turret seat first, and once the gunner seat is filled, returns the open secondary loader/traverse seat
        /// so both seats of two-man emplacements are staffed!
        /// </summary>
        private static bool TryGetAvailableEmplacementOrTurretSeatIndex(Vehicle veh, out int seatIndex, out int openSeatCount)
        {
            seatIndex = -1;
            openSeatCount = 0;
            if (veh == null || veh.seats == null)
                return false;

            bool isStaticEmplacement = veh.TryCast<MovableVehicle>() == null &&
                                       veh.GetMainTurret(false) != null &&
                                       veh.GetMainTurret(false).WeaponsCount() > 0;

            bool hasTurretSeat = TryGetAvailableTurretSeatIndex(veh, out int turretIdx);
            if (!isStaticEmplacement)
            {
                if (hasTurretSeat)
                {
                    seatIndex = turretIdx;
                    openSeatCount = 1;
                    return true;
                }
                return false;
            }

            var seats = veh.seats;
            int count = seats.Length;
            int firstSecondaryIdx = -1;
            for (int i = 0; i < count; i++)
            {
                Seats s = seats[i];
                if (s != null && !s.hasUnitSet)
                {
                    openSeatCount++;
                    if (firstSecondaryIdx < 0)
                        firstSecondaryIdx = i;
                }
            }

            if (hasTurretSeat)
            {
                seatIndex = turretIdx;
                return true;
            }

            if (firstSecondaryIdx >= 0)
            {
                seatIndex = firstSecondaryIdx;
                return true;
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

        private static bool IsVehicleExplicitlyOrderedByAnySquad(IntPtr vehPtr)
        {
            if (vehPtr == IntPtr.Zero || _squadOrderedToVehicle.Count == 0)
                return false;

            foreach (var kv in _squadOrderedToVehicle)
            {
                if (kv.Value == vehPtr)
                    return true;
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

            public static void Postfix(Squad __instance, Vehicle __0)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value ||
                        __instance == null ||
                        __instance.Pointer == IntPtr.Zero ||
                        __0 == null ||
                        __0.Pointer == IntPtr.Zero ||
                        __instance.units == null)
                    {
                        return;
                    }

                    IntPtr vehPtr = __0.Pointer;
                    float now = Time.time;
                    var units = __instance.units;
                    int count = units.Count;
                    for (int i = 0; i < count; i++)
                    {
                        Soldier u = units[i];
                        if (u == null || !u.IsAI() || !u.IsAlive || u.IsOnVehicle() || !AiCore.IsLocallyOwnedBot(u))
                            continue;

                        if (u.HasDestinationAssigned && u.targetDestination != null && u.targetDestination.Pointer == vehPtr)
                        {
                            _assignedEmplacementUntil[u.Pointer] = now + 22.0f;
                            _assignedEmplacementVehicle[u.Pointer] = vehPtr;
                            if (AiCore.TryGetSoldierHandles(u.Pointer, out var uh))
                                uh.Stance.CombatCrouchLockUntil = 0f;
                            if (u.aiController != null)
                                SetBotPose(u.aiController, u, SoldierPose.Idle, true);
                        }
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

        [ThreadStatic]
        internal static bool AllowExplicitSquadUnstallRelease;

        [HarmonyPatch(typeof(Squad), nameof(Squad.OrderLeaveAllVehiclesAndCovers))]
        public static class Squad_OrderLeaveAllVehiclesAndCovers_Patch
        {
            public static bool Prefix(Squad __instance, bool __0, bool __1, bool __2)
            {
                try
                {
                    // Guard SquadLeaderRoutine's 0.8s OrderLeaveAllVehiclesAndCovers(false, true, false) sweep so it
                    // never wipes active timed withdrawal/detour/pincer orders OR individual GOAP/stance cover locks
                    // (> 1.5x taskRadius from objective) before SeekEmergencyCover -> BandageInCover / Flank / Bound complete!
                    if (!VisionConfig.EnableMod.Value ||
                        AllowExplicitSquadUnstallRelease ||
                        __0 || !__1 || __2 ||
                        __instance == null ||
                        __instance.Pointer == IntPtr.Zero ||
                        __instance.IsPlayerInSquad())
                    {
                        return true;
                    }

                    float now = Time.time;
                    IntPtr sqPtr = __instance.Pointer;
                    if (__instance.timed_order_end > now ||
                        IsSquadActivelyRetreating(sqPtr, now) ||
                        (_aiFollowColumnUntil.TryGetValue(sqPtr, out float folUntil) && now < folUntil))
                    {
                        return false;
                    }

                    SquadTask curTask = __instance.currentTask;
                    var units = __instance.units;
                    if (curTask == null || units == null)
                        return true;

                    int count = units.Count;
                    bool anyProtected = false;
                    for (int i = 0; i < count; i++)
                    {
                        Soldier u = units[i];
                        if (u == null || u.Pointer == IntPtr.Zero || u.IsDead || !u.HasDestinationAssigned)
                            continue;

                        IntPtr uPtr = u.Pointer;
                        if (HasActiveGoapCoverLock(uPtr, now) ||
                            IsActivelyHeadingToEmplacement(u, now) ||
                            (AiCore.TryGetSoldierHandles(uPtr, out var pH) && now < pH.Stance.LocalCoverProtectedUntil))
                        {
                            anyProtected = true;
                            break;
                        }
                    }

                    if (!anyProtected)
                        return true;

                    for (int i = 0; i < count; i++)
                    {
                        Soldier u = units[i];
                        if (u == null || u.Pointer == IntPtr.Zero || u.IsDead || !AiCore.IsLocallyOwnedBot(u) || !u.HasDestinationAssigned)
                            continue;

                        IntPtr uPtr = u.Pointer;
                        AiCore.TryGetSoldierHandles(uPtr, out var uH);
                        if (HasActiveGoapCoverLock(uPtr, now) ||
                            IsActivelyHeadingToEmplacement(u, now) ||
                            (uH != null && now < uH.Stance.LocalCoverProtectedUntil))
                        {
                            continue;
                        }

                        AiDestination td = u.targetDestination;
                        if (td == null || td.Pointer == IntPtr.Zero)
                            continue;

                        if (td.TryCast<Vehicle>() != null ||
                            td.TryCast<VehicleRepairTask>() != null ||
                            td.TryCast<MedicHealingDestination>() != null)
                        {
                            continue;
                        }

                        if (!curTask.IsInsideTaskArea(td.GetCoverPosition(), 1.5f))
                        {
                            if (uH != null && uH.Ai != null)
                                uH.Ai.Stop(false);
                            u.CoverPosition(null);
                            AiCore.InvalidateTargetDestinationCache(u);
                        }
                    }

                    return false;
                }
                catch { }
                return true;
            }

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
        /// (abandoned trucks, halftracks, jeeps, tanks), player-occupied movable vehicles, or full emplacements as valid cover.
        /// Allows both Gunner and Loader/Spotter seats on static emplacements.
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

                    if (__instance.TryCast<MovableVehicle>() != null)
                    {
                        bool explicitOrder = __instance.PlayerIsInside() && IsVehicleExplicitlyOrderedByAnySquad(__instance.Pointer);
                        if (explicitOrder)
                            return;

                        if (!__instance.HasDriver() || __instance.PlayerIsInside())
                        {
                            __result = false;
                            return;
                        }
                        if (!TryGetAvailableTurretSeatIndex(__instance, out _))
                        {
                            __result = false;
                        }
                        return;
                    }

                    if (!TryGetAvailableEmplacementOrTurretSeatIndex(__instance, out _, out _))
                    {
                        __result = false;
                    }
                }
                catch { }
            }
        }

        /// <summary>
        /// In vanilla (0x18085FD40), Vehicle.IsCoverOccupied returns false as long as ANY seat (including non-turret
        /// passenger seats on movable vehicles) is empty, causing extra foot soldiers to climb into non-turret seats.
        /// Mark movable vehicles as occupied as soon as all turret seats are filled (or if driverless or player-occupied),
        /// while allowing static emplacements to fill both Gunner and Loader seats.
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

                    if (__instance.TryCast<MovableVehicle>() != null)
                    {
                        bool explicitOrder = __instance.PlayerIsInside() && IsVehicleExplicitlyOrderedByAnySquad(__instance.Pointer);
                        if (explicitOrder)
                            return;

                        if (!__instance.HasDriver() || __instance.PlayerIsInside())
                        {
                            __result = true;
                            return;
                        }
                        if (!TryGetAvailableTurretSeatIndex(__instance, out _))
                        {
                            __result = true;
                        }
                        return;
                    }

                    if (!TryGetAvailableEmplacementOrTurretSeatIndex(__instance, out _, out _))
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
        /// driver seats and non-turret passenger/loader seats on movable vehicles!
        /// This patch ensures:
        ///   1. Regular AI foot soldiers NEVER climb into driverless movable vehicles, player-driven movable vehicles
        ///      (unless in the player's squad), or non-turret seats on movable vehicles.
        ///   2. Static emplacements fill the Gunner seat first, then the Loader/Assistant seat (`TryGetAvailableEmplacementOrTurretSeatIndex`).
        ///   3. Bailed-out Vehicle Crews (`sq.IsVehicleCrew`) re-mounting an unmanned combat vehicle always fill
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

                    if (__instance.IsCoverDestroyed() || __instance.IsDisabled() || __instance.IsLocked() || HasHostileVehicleOccupant(__instance, __0.faction))
                    {
                        ClearEmplacementAssignment(__0.Pointer, markUnreachable: true, 120.0f);
                        __0.CoverPosition(null);
                        return false;
                    }

                    Squad sq = __0.joinedSquad;
                    bool isPlayerSquad = sq != null && sq.IsPlayerInSquad();
                    if (isPlayerSquad)
                    {
                        ClearEmplacementAssignment(__0.Pointer, markUnreachable: false);
                        return true;
                    }

                    bool isExplicitBoardOrder = sq != null &&
                                                _squadOrderedToVehicle.TryGetValue(sq.Pointer, out IntPtr orderedVehPtr) &&
                                                orderedVehPtr == __instance.Pointer;
                    bool isVehicleCrew = sq != null && AiCore.IsTrueVehicleCrewSquad(sq);
                    bool isMovable = __instance.TryCast<MovableVehicle>() != null;

                    // Prevent non-squad AI foot soldiers from climbing uninvited into a human player's movable vehicle!
                    if (isMovable && __instance.PlayerIsInside() && !isExplicitBoardOrder)
                    {
                        ClearEmplacementAssignment(__0.Pointer, markUnreachable: true, 60.0f);
                        __0.CoverPosition(null);
                        return false;
                    }

                    if (isVehicleCrew && isMovable)
                    {
                        if (!__instance.HasDriver() && TryGetAvailableDriverSeatIndex(__instance, out int driverIdx))
                        {
                            ClearEmplacementAssignment(__0.Pointer, markUnreachable: false);
                            __instance.GetOnVehicle(driverIdx, __0);
                            return false;
                        }
                        if (TryGetAvailableTurretSeatIndex(__instance, out int crewTurretIdx))
                        {
                            ClearEmplacementAssignment(__0.Pointer, markUnreachable: false);
                            __instance.GetOnVehicle(crewTurretIdx, __0);
                            return false;
                        }
                        ClearEmplacementAssignment(__0.Pointer, markUnreachable: false);
                        return true;
                    }

                    if (isExplicitBoardOrder)
                    {
                        ClearEmplacementAssignment(__0.Pointer, markUnreachable: false);
                        return true;
                    }

                    // Regular AI foot soldier reaching a Vehicle as cover/emplacement:
                    // 1. Never climb into a driverless movable vehicle (truck, halftrack, jeep, tank)
                    if (isMovable && !__instance.HasDriver())
                    {
                        ClearEmplacementAssignment(__0.Pointer, markUnreachable: true, 60.0f);
                        __0.CoverPosition(null);
                        return false;
                    }

                    // 2. On movable vehicles, only board an open turret seat; on static emplacements,
                    //    board the Gunner seat first and the Loader/Assistant seat second!
                    if (TryGetAvailableEmplacementOrTurretSeatIndex(__instance, out int targetSeatIdx, out _))
                    {
                        ClearEmplacementAssignment(__0.Pointer, markUnreachable: false);
                        __instance.GetOnVehicle(targetSeatIdx, __0);
                        return false;
                    }

                    ClearEmplacementAssignment(__0.Pointer, markUnreachable: true, 45.0f);
                    __0.CoverPosition(null);
                    return false;
                }
                catch { }
                return true;
            }
        }

        /// <summary>
        /// Pauses native AIVehicle.SwitchSeatAi (0x18057C740) whenever a human player is inside the vehicle
        /// so AI crewmen never musical-chairs swap seats or hijack the gunner/driver seat away from the player.
        /// </summary>
        [HarmonyPatch(typeof(AIVehicle), nameof(AIVehicle.SwitchSeatAi))]
        public static class AIVehicle_SwitchSeatAi_Patch
        {
            public static bool Prefix(Vehicle __1)
            {
                try
                {
                    if (VisionConfig.EnableMod.Value && __1 != null && __1.PlayerIsInside())
                        return false;
                }
                catch { }
                return true;
            }
        }

        /// <summary>
        /// In vanilla PlayerController.SwitchSeat(int seatIndex) (0x1807D4EC1), multiplayer clients are blocked
        /// from switching into a seat (`!targetSeat.unitSet.GetSyncher().IsMine`) even when the occupant is an AI bot
        /// owned by the Host. Allow human players to always swap seats with AI bots (`occupant.IsAI() && !occupant.IsPlayer()`).
        /// </summary>
        [HarmonyPatch(typeof(PlayerController), nameof(PlayerController.SwitchSeat), new Type[] { typeof(int) })]
        public static class PlayerController_SwitchSeat_Patch
        {
            public static void Postfix(PlayerController __instance, int __0, ref bool __result)
            {
                try
                {
                    if (__result || !VisionConfig.EnableMod.Value || __instance == null)
                        return;

                    Vehicle veh = __instance.ControlledVehicle;
                    if (veh == null || veh.seats == null || __0 < 0 || __0 >= veh.seats.Length)
                        return;

                    if (veh.IsLocked() || veh.IsCoverDestroyed() || veh.IsDisabled() || __instance.selectedVehicleTurret == __0)
                        return;

                    Soldier playerSoldier = __instance.ControlledCharacter;
                    if (playerSoldier == null || !playerSoldier.IsAlive || !playerSoldier.CanSwicthSeat())
                        return;

                    Seats targetSeat = veh.seats[__0];
                    if (targetSeat == null || !targetSeat.hasUnitSet)
                        return;

                    Soldier occupant = targetSeat.unitSet;
                    if (occupant == null || !occupant.IsAI() || occupant.IsPlayer())
                        return;

                    __instance.selectedVehicleTurret = __0;
                    Turret turret = targetSeat.connectedTurret;
                    if (VehicleGUI.instance != null)
                    {
                        VehicleGUI.instance.SelectTurret(turret, veh.name);
                    }
                    playerSoldier.SwitchSeat(targetSeat);
                    __result = true;
                }
                catch { }
            }
        }

        /// <summary>
        /// When a remote client human player switches into a vehicle seat occupied by a Host-owned AI bot,
        /// the Host receives `SyncSoldier.SyncUnitOnVehicle` -> `Vehicle.GetOnVehicle(seatIndex, playerSoldier)`.
        /// Relocate the displaced locally-owned AI bot into the player's previous seat (broadcasting its new seat
        /// across Photon) so both Host and Clients stay 100% synchronized.
        /// </summary>
        [HarmonyPatch(typeof(Vehicle), nameof(Vehicle.GetOnVehicle))]
        public static class Vehicle_GetOnVehicle_Patch
        {
            public struct SeatSwapState
            {
                public Soldier DisplacedBot;
                public int OldSeatIdx;
            }

            public static void Prefix(Vehicle __instance, int __0, Soldier __1, out SeatSwapState __state)
            {
                __state = default;
                __state.OldSeatIdx = -1;
                try
                {
                    if (!VisionConfig.EnableMod.Value || __instance == null || __1 == null || __1.IsAI())
                        return;

                    var seats = __instance.seats;
                    if (seats == null || __0 < 0 || __0 >= seats.Length)
                        return;

                    Seats targetSeat = seats[__0];
                    Soldier existing = targetSeat != null ? targetSeat.unitSet : null;
                    if (existing == null || existing.Pointer == __1.Pointer || !AiCore.IsLocallyOwnedBot(existing))
                        return;

                    int oldIdx = -1;
                    if (__1.IsOnVehicle())
                    {
                        Vehicle curVeh = __1.GetCurrentVehicle();
                        if (curVeh != null && curVeh.Pointer == __instance.Pointer)
                        {
                            Seats oldSeat = __1.GetCurrentVehicleSeat();
                            if (oldSeat != null)
                                oldIdx = oldSeat.GetSeatIndexInVehicle();
                        }
                    }

                    if (oldIdx < 0)
                    {
                        for (int i = 0; i < seats.Length; i++)
                        {
                            if (i != __0 && seats[i] != null && !seats[i].hasUnitSet)
                            {
                                oldIdx = i;
                                break;
                            }
                        }
                    }

                    __state.DisplacedBot = existing;
                    __state.OldSeatIdx = oldIdx;
                }
                catch { }
            }

            public static void Postfix(Vehicle __instance, int __0, Soldier __1, SeatSwapState __state)
            {
                try
                {
                    if (__instance != null && __instance.Pointer != IntPtr.Zero &&
                        __1 != null && !string.IsNullOrEmpty(__1.faction) && __1.faction != "Civilian")
                    {
                        _lastKnownVehicleFaction[__instance.Pointer] = __1.faction;
                        RegisterVehicleCrewSquad(__instance, __1);
                    }

                    if (__state.DisplacedBot == null || !__state.DisplacedBot.IsAlive || __instance == null)
                        return;

                    if (__state.OldSeatIdx >= 0 && __state.OldSeatIdx != __0 &&
                        __instance.seats != null && __state.OldSeatIdx < __instance.seats.Length)
                    {
                        __instance.GetOnVehicle(__state.OldSeatIdx, __state.DisplacedBot);
                    }
                }
                catch { }
            }
        }
    }
}
