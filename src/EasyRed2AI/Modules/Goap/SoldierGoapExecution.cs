using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace EasyRed2HumanAI
{
    public static partial class VeteranAndTacticsPatches
    {
        // =====================================================================
        // Tier 1: Individual Bot 32-Bit WorldState & Bounded 2-Step GOAP Planner
        // =====================================================================
        internal static bool HasActiveGoapCoverLock(IntPtr soldierPtr, float now)
        {
            return AiCore.TryGetSoldierHandles(soldierPtr, out AiCore.SoldierFastHandles h) &&
                   h.Goap.Plan.LockUnitCoverChange &&
                   now < h.Goap.Plan.PlanExpireTime;
        }

        internal static bool IsSquadActivelyRetreating(IntPtr sqPtr, float now)
        {
            return sqPtr != IntPtr.Zero &&
                   _aiSquadRetreatUntil.TryGetValue(sqPtr, out float retUntil) &&
                   now < retUntil;
        }

        internal static bool IsInIndividualOrGoapFallback(IntPtr soldierPtr, float now)
        {
            if (!AiCore.TryGetSoldierHandles(soldierPtr, out AiCore.SoldierFastHandles h))
                return false;

            if (h.MoveOverride.Kind == AiCore.BotMoveOverrideKind.EmergencyFallback &&
                now < h.MoveOverride.ExpireTime)
            {
                return true;
            }

            if (now < h.Goap.Plan.PlanExpireTime)
            {
                if (h.Goap.Plan.IsEvacuatingBailedCrew)
                    return true;

                GoapActionId cur = h.Goap.Plan.StepIndex == 0 ? h.Goap.Plan.Step0 : h.Goap.Plan.Step1;
                if (cur == GoapActionId.TacticalFallbackRetreat ||
                    (cur == GoapActionId.ThrowSmokeScreen && h.Goap.Plan.StepCount > 1 && h.Goap.Plan.Step1 == GoapActionId.TacticalFallbackRetreat))
                {
                    return true;
                }
            }

            return false;
        }

        internal static bool HasPendingAtWeaponScavenge(IntPtr soldierPtr, Vector3 myPos, float maxDist = 24.0f)
        {
            if (!AiCore.TryGetSoldierHandles(soldierPtr, out AiCore.SoldierFastHandles h))
                return false;

            ref BotScavengeCache sc = ref h.Goap.Scavenge;
            return sc.IsAtWeaponPickup &&
                   (sc.TargetFloorItem != null || sc.TargetRefillCrate != null || sc.TargetInventory != null) &&
                   (sc.TargetPosition - myPos).sqrMagnitude <= maxDist * maxDist;
        }

        internal static bool TryGetProtectedBotMoveOverride(IntPtr soldierPtr, float now, out Vector3 overrideDest)
        {
            overrideDest = Vector3.zero;
            if (!AiCore.TryGetSoldierHandles(soldierPtr, out AiCore.SoldierFastHandles h))
                return false;

            if (h.MoveOverride.Kind != AiCore.BotMoveOverrideKind.None && now < h.MoveOverride.ExpireTime)
            {
                if (h.MoveOverride.Kind != AiCore.BotMoveOverrideKind.CasualtyDrag ||
                    (now < h.Goap.CasualtyDrag.DragEndTime &&
                     h.Goap.CasualtyDrag.Patient != null &&
                     !h.Goap.CasualtyDrag.Patient.IsDead))
                {
                    overrideDest = h.MoveOverride.Dest;
                    return true;
                }
            }

            if (h.Goap.Plan.HasMoveOverride && now < h.Goap.Plan.PlanExpireTime)
            {
                overrideDest = h.Goap.Plan.MoveOverrideDest;
                return true;
            }

            return false;
        }

        internal static void ExecuteSoldierGoapTick(
            SoldierAI ai,
            Soldier soldier,
            SoldierPersonality personality,
            float vet01,
            float suppression,
            float dist,
            bool isGunner,
            bool isOnBridge,
            bool inPointBlankMelee,
            bool isEvadingArmor,
            bool isStaffingEmplacement)
        {
            if (!VisionConfig.EnableTacticalGoapPlanner.Value ||
                ai == null ||
                AiCore.IsSoldierInVehicleOrTransition(soldier) ||
                soldier.HasSurrended() ||
                soldier.isInBodyAnimation)
            {
                return;
            }

            // Unconditional top-level carry check: even during Charges, emplacements, or budget contention,
            // a soldier carrying a wounded comrade must evaluate drop conditions so Medics aren't blocked by IsCarried().
            if (soldier.IsCarryingBody())
            {
                HandleActiveCasualtyCarryTick(ai, soldier, soldier.Pointer, Time.time);
                return;
            }

            if (isStaffingEmplacement)
            {
                return;
            }

            if (soldier.IsCharging() && !inPointBlankMelee)
            {
                Squad cSq = soldier.joinedSquad;
                FactionCommandProfile cProf = GetFactionCommandProfile(soldier.faction);
                BotMicroVariance cMicro = BotMicroVariance.FromSoldier(soldier.Pointer);
                SquadBlackboard cBoard = default;
                if (cSq != null)
                    _squadBlackboards.TryGetValue(cSq.Pointer, out cBoard);
                bool fanatical = IsTrueGyokusaiOrBanzaiCondition(in cProf, cSq, in cBoard);

                float personalBalkSupp = Mathf.Lerp(36.0f, 46.0f, vet01) / Mathf.Max(0.75f, cMicro.CautionCostMult);
                if (personality == SoldierPersonality.Assaulter_Shock || personality == SoldierPersonality.Leader_Spearhead)
                    personalBalkSupp += 12.0f;
                else if (personality == SoldierPersonality.Rifleman_Cautious || personality == SoldierPersonality.Medic_Triage)
                    personalBalkSupp -= 8.0f;

                bool comradeDroppedNearby = cSq != null &&
                                            _squadBlackboards.TryGetValue(cSq.Pointer, out SquadBlackboard cb) &&
                                            cb.LastCasualtyTime > 0f &&
                                            (Time.time - cb.LastCasualtyTime) <= 4.5f &&
                                            soldier.transform != null &&
                                            (soldier.transform.position - cb.LastCasualtyPos).sqrMagnitude <= 11.5f * 11.5f;

                bool balkFromCharge = !fanatical &&
                                      dist > 12.0f &&
                                      (soldier.isBleeding ||
                                       suppression >= personalBalkSupp ||
                                       (comradeDroppedNearby && suppression >= 18.0f));
                if (!balkFromCharge)
                    return;

                if (!soldier.IsOnCover())
                {
                    if (soldier.HasDestinationAssigned &&
                        AiCore.TryGetSoldierHandles(soldier.Pointer, out var balkH) &&
                        Time.time < balkH.Stance.LocalCoverProtectedUntil)
                    {
                        return;
                    }

                    if (AiCore.CanBotMoveAutonomously(soldier) && TrySeekNearbyCover(ai, soldier))
                    {
                        AiCore.GetOrCreateSoldierHandles(soldier).Stance.LocalCoverProtectedUntil = Time.time + 5.5f;
                        IndividualSelfPreservationBalksCount++;
                        BotActionTrace.RecordEvent(soldier, BotTraceEventKind.SelfBalk, "charge_balk_cover", dist);
                        return;
                    }

                    if (AiCore.CanBotChangePoseAutonomously(soldier) && soldier.m_pose == SoldierPose.Idle)
                    {
                        SetBotPose(ai, soldier, suppression >= personalBalkSupp + 14.0f ? SoldierPose.Prone : SoldierPose.Crouch, true);
                        IndividualSelfPreservationBalksCount++;
                        BotActionTrace.RecordEvent(soldier, BotTraceEventKind.SelfBalk, "charge_balk_drop", dist);
                    }
                }
                // Once in cover (or if no cover exists nearby), fall through to GOAP so bleeding/pinned bots
                // can BandageInCover, ThrowSmokeScreen, Suppress, or TacticalFallbackRetreat instead of freezing!
            }

            bool canMoveAutonomously = AiCore.CanBotMoveAutonomously(soldier);
            if (!canMoveAutonomously && !AiCore.CanBotChangePoseAutonomously(soldier))
            {
                return;
            }

            long profGoapStart = HitchProfiler.Begin();
            try
            {
            IntPtr ptr = soldier.Pointer;
            float now = Time.time;
            Squad sq = soldier.joinedSquad;
            SquadBlackboard board = GetOrUpdateSquadBlackboard(sq, soldier);

            bool canUnstickMove =
                canMoveAutonomously ||
                (!soldier.IsSquadLeader() && !soldier.IsOnVehicle() && (soldier.Params == null || soldier.Params.canMove));

            if (canUnstickMove &&
                !inPointBlankMelee &&
                soldier.transform != null &&
                TryRecoverChronicallyStuckFootBot(ai, soldier, ptr, ref board, soldier.transform.position, suppression, isOnBridge, dist, board.HasActiveObjectiveTask ? board.ObjectivePosition : Vector3.zero, board.HasActiveObjectiveTask, now))
            {
                if (sq != null && sq.Pointer != IntPtr.Zero)
                    _squadBlackboards[sq.Pointer] = board;
                return;
            }

            // Emergent Buddy Casualty Dragging (Soldier.CarryBodySynched):
            // If this soldier is currently carrying a wounded buddy to cover/Medic (or spots an unclaimed wounded buddy
            // in the open nearby while not bleeding, evading armor, or in point-blank combat), handle the carry & drop-off cleanly!
            if (canMoveAutonomously &&
                VisionConfig.EnableBuddyCasualtyDragging.Value &&
                !inPointBlankMelee &&
                !isOnBridge &&
                !isEvadingArmor &&
                soldier.transform != null &&
                TryExecuteBuddyCasualtyDrag(ai, soldier, sq, ref board, soldier.transform.position, now))
            {
                if (sq != null && sq.Pointer != IntPtr.Zero)
                    _squadBlackboards[sq.Pointer] = board;
                return;
            }

            // Special Munitions & Sappers (Satchel Bunker/Emplacement Breaches, Safe AT/AP Mines, & Deployable Tripods):
            if (VisionConfig.EnableSpecialMunitionsAndSappers.Value &&
                canMoveAutonomously &&
                !inPointBlankMelee &&
                !soldier.isBleeding &&
                !isEvadingArmor &&
                !IsSoldierBailedOutVehicleCrew(soldier, sq) &&
                soldier.transform != null &&
                TryExecuteSpecialMunitionsAndTripods(ai, soldier, ptr, sq, ref board, soldier.transform.position, suppression, dist, isOnBridge, now))
            {
                if (sq != null && sq.Pointer != IntPtr.Zero)
                    _squadBlackboards[sq.Pointer] = board;
                return;
            }

            // Build 32-Bit WorldState in O(1)
            AiCore.SoldierFastHandles goapH = AiCore.GetOrCreateSoldierHandles(soldier);
            if (goapH != null)
            {
                if (soldier.IsOnCover() && soldier.targetDestination != null && soldier.targetDestination.Pointer != IntPtr.Zero)
                {
                    IntPtr covPtr = soldier.targetDestination.Pointer;
                    if (goapH.Stance.LastSettledCoverPtr != covPtr)
                    {
                        goapH.Stance.LastSettledCoverPtr = covPtr;
                        float dwell = 7.5f + ((AiCore.GetDeterministicHash(ptr, 311) & 15) * 0.25f);
                        if (personality == SoldierPersonality.Gunner_Anchor ||
                            personality == SoldierPersonality.Gunner_Overwatch ||
                            personality == SoldierPersonality.Rifleman_Cautious ||
                            personality == SoldierPersonality.Leader_OldGuard ||
                            personality == SoldierPersonality.Medic_Triage ||
                            personality == SoldierPersonality.Medic_Combat)
                        {
                            dwell += 2.5f;
                        }
                        else if (personality == SoldierPersonality.Assaulter_Shock ||
                                 personality == SoldierPersonality.Assaulter_Breacher ||
                                 personality == SoldierPersonality.Assaulter_Skirmisher ||
                                 personality == SoldierPersonality.Leader_Spearhead)
                        {
                            dwell -= 2.0f;
                        }
                        goapH.Stance.CoverSettledUntil = now + dwell;
                    }
                }
                else if (!soldier.IsOnCover())
                {
                    goapH.Stance.LastSettledCoverPtr = IntPtr.Zero;
                    goapH.Stance.CoverSettledUntil = 0f;
                }
            }

            // Release stale post-combat covers when a calm unengaged bot's squad moved away or shifted sector (> 110 deg)
            if (canMoveAutonomously &&
                soldier.IsOnCover() &&
                !soldier.isBleeding &&
                suppression < 3.0f &&
                AiCore.GetRawVisibleTargetPtr(ai) == IntPtr.Zero &&
                !board.HasActiveThreat &&
                sq != null &&
                goapH != null &&
                goapH.Tr != null &&
                now >= goapH.Stance.NextLocalCoverSeekTime &&
                !HasActiveGoapCoverLock(ptr, now))
            {
                Vector3 curBotPos = goapH.Tr.position;
                bool squadMovedAway = !sq.IsInsideMoveOrderArea(curBotPos);
                bool coverFacingWrongSector = false;
                if (!squadMovedAway &&
                    now >= goapH.Stance.CoverSettledUntil &&
                    AiCore.ResolveTargetDestination(goapH) == AiCore.CachedDestKind.CombatCover &&
                    goapH.CachedCover != null &&
                    goapH.CachedCover.localLookAngleWideness < 165f)
                {
                    Vector3 sqDir = sq.formationDirection;
                    sqDir.y = 0f;
                    Vector3 covFwd = goapH.CachedCover.GetLookRotation() * Vector3.forward;
                    covFwd.y = 0f;
                    if (sqDir.sqrMagnitude > 0.01f && covFwd.sqrMagnitude > 0.01f &&
                        Vector3.Angle(covFwd, sqDir) > 110.0f)
                    {
                        coverFacingWrongSector = true;
                    }
                }

                if (squadMovedAway || coverFacingWrongSector)
                {
                    goapH.Stance.NextLocalCoverSeekTime = now + 6.0f;
                    goapH.Stance.LocalCoverProtectedUntil = 0f;
                    AiDestination refreshedCover = sq.GetCoveredPositionInArea();
                    if (refreshedCover != null && refreshedCover.Pointer != IntPtr.Zero)
                    {
                        AiCore.AssignBotCoverOrDestination(ai, soldier, refreshedCover);
                    }
                    else
                    {
                        AiCore.AssignBotCoverOrDestination(ai, soldier, null, false);
                        if (sq.moveOrderPosition.sqrMagnitude > 0.01f)
                        {
                            AiCore.SetBotMoveDestinationAndRefreshPath(ai, sq.moveOrderPosition);
                        }
                    }
                }
            }

            GoapStateBits ws = BuildSoldierWorldState(
                ai,
                soldier,
                sq,
                ref board,
                personality,
                suppression,
                dist,
                isGunner,
                isOnBridge,
                inPointBlankMelee,
                isEvadingArmor,
                now);

            BotGoapPlan plan = goapH != null ? goapH.Goap.Plan : default;
            bool hasActivePlan = now < plan.PlanExpireTime;
            GoapActionId activeStep = hasActivePlan ? (plan.StepIndex == 0 ? plan.Step0 : plan.Step1) : GoapActionId.None;
            bool vulnerableTaskInDanger =
                hasActivePlan &&
                ((activeStep == GoapActionId.ScavengeSupplyFromCorpse &&
                  (suppression >= 5.0f || (ws & ScavengeForbiddenMask) != 0)) ||
                 (activeStep == GoapActionId.RescueSurrenderedComrade &&
                  (suppression >= 15.0f || (ws & LiberateForbiddenMask) != 0)));
            bool closeBailedThreat = (ws & GoapStateBits.InPointBlankRange) != 0 ||
                                     ((ws & GoapStateBits.CanFireAtVisibleEnemy) != 0 && dist <= 24.0f);
            bool nonBleedingBailedCrew =
                IsSoldierBailedOutVehicleCrew(soldier, sq) &&
                board.AliveMemberCount <= 5 &&
                (ws & GoapStateBits.IsBleeding) == 0 &&
                !closeBailedThreat;
            bool emergencyPreempt =
                vulnerableTaskInDanger ||
                ((ws & GoapStateBits.InPointBlankRange) != 0 && activeStep != GoapActionId.PointBlankBayonetBreach) ||
                ((ws & GoapStateBits.IsBleeding) != 0 &&
                 (ws & GoapStateBits.BandageUnsafeUnderFire) == 0 &&
                 activeStep != GoapActionId.SeekEmergencyCover &&
                 activeStep != GoapActionId.BandageInCover &&
                 (activeStep != GoapActionId.ScavengeSupplyFromCorpse || suppression >= 5.0f || (ws & ScavengeForbiddenMask) != 0)) ||
                ((ws & (GoapStateBits.IsBleeding | GoapStateBits.BandageUnsafeUnderFire)) == (GoapStateBits.IsBleeding | GoapStateBits.BandageUnsafeUnderFire) &&
                 activeStep == GoapActionId.BandageInCover) ||
                ((ws & GoapStateBits.ExposedSideOfWall) != 0 &&
                 (ws & (GoapStateBits.InPointBlankRange | GoapStateBits.OnBridgeOrNarrow)) == 0 &&
                 activeStep != GoapActionId.RepositionBehindWall &&
                 activeStep != GoapActionId.SeekEmergencyCover) ||
                ((ws & GoapStateBits.OverwhelmedNeedFallback) != 0 &&
                 (ws & (GoapStateBits.InPointBlankRange | GoapStateBits.OnBridgeOrNarrow)) == 0 &&
                 activeStep != GoapActionId.TacticalFallbackRetreat &&
                 activeStep != GoapActionId.ThrowSmokeScreen) ||
                ((ws & GoapStateBits.RadioLinkDrifting) != 0 &&
                 activeStep != GoapActionId.MaintainRadioLinkWithLeader &&
                 (ws & (GoapStateBits.IsBleeding |
                        GoapStateBits.InPointBlankRange |
                        GoapStateBits.CanFireAtVisibleEnemy |
                        GoapStateBits.IsSuppressed |
                        GoapStateBits.IsHeavilySuppressed |
                        GoapStateBits.NearFreshCasualty)) == 0) ||
                (nonBleedingBailedCrew &&
                 activeStep != GoapActionId.LoneWolfLinkUpOrAmbush &&
                 activeStep != GoapActionId.ThrowSmokeScreen) ||
                (!nonBleedingBailedCrew &&
                 isEvadingArmor &&
                 (ws & GoapStateBits.InCover) == 0 &&
                 activeStep != GoapActionId.SeekEmergencyCover &&
                 activeStep != GoapActionId.TacticalFallbackRetreat &&
                 activeStep != GoapActionId.ThrowSmokeScreen) ||
                (!nonBleedingBailedCrew &&
                 (ws & (GoapStateBits.IsSuppressed | GoapStateBits.IsHeavilySuppressed | GoapStateBits.NearFreshCasualty)) != 0 &&
                 (ws & GoapStateBits.InCover) == 0 &&
                 activeStep != GoapActionId.SeekEmergencyCover &&
                 activeStep != GoapActionId.TacticalFallbackRetreat &&
                 activeStep != GoapActionId.RepositionBehindWall &&
                 activeStep != GoapActionId.ThrowSmokeScreen);

            if (!hasActivePlan || emergencyPreempt || now >= plan.NextReplanTime)
            {
                GoapActionId prevActiveAction = hasActivePlan && !emergencyPreempt
                    ? activeStep
                    : GoapActionId.None;
                plan = PlanBestGoapActionChain(ws, soldier, ref board, personality, vet01, now, prevActiveAction);
                if (goapH != null)
                    goapH.Goap.Plan = plan;
            }

            GoapActionId currentAction = plan.StepIndex == 0 ? plan.Step0 : plan.Step1;
            if (activeStep != currentAction &&
                (activeStep == GoapActionId.ScavengeSupplyFromCorpse ||
                 activeStep == GoapActionId.RescueSurrenderedComrade ||
                 activeStep == GoapActionId.MaintainRadioLinkWithLeader))
            {
                if (goapH != null)
                {
                    if (activeStep == GoapActionId.ScavengeSupplyFromCorpse)
                        goapH.Goap.Scavenge = default;
                    AiCore.ClearBotMoveOverride(goapH, AiCore.BotMoveOverrideKind.GoapAction);
                }
                if (ai.moveDirectlyToward_end > now)
                    ai.moveDirectlyToward_end = 0f;
                if (!IsMovementGoapAction(currentAction) && soldier.transform != null)
                {
                    if (soldier.HasDestinationAssigned && soldier.targetDestination != null)
                    {
                        Vector3 abortBotPos = (goapH != null && goapH.Tr != null) ? goapH.Tr.position : soldier.transform.position;
                        Vector3 oldDestPos = soldier.targetDestination.GetCoverPosition();
                        bool interruptedUnderFire =
                            (activeStep == GoapActionId.ScavengeSupplyFromCorpse ||
                             activeStep == GoapActionId.RescueSurrenderedComrade) &&
                            (suppression >= 4.0f || board.HasActiveThreat || soldier.isAlerted) &&
                            (oldDestPos - abortBotPos).sqrMagnitude > 36.0f;

                        if (interruptedUnderFire && AiCore.CanBotMoveAutonomously(soldier))
                        {
                            if (!TrySeekNearbyCover(ai, soldier))
                            {
                                if (sq != null && sq.IsInsideMoveOrderArea(abortBotPos))
                                {
                                    DestinationWithoutCover holdDest = new DestinationWithoutCover(abortBotPos, SoldierPose.Crouch);
                                    if (goapH != null)
                                        goapH.Stance.LocalCoverProtectedUntil = now + 4.5f;
                                    AiCore.AssignBotCoverOrDestination(ai, soldier, holdDest.Cast<AiDestination>());
                                }
                                else
                                {
                                    AiCore.SetBotMoveDestinationAndRefreshPath(ai, oldDestPos);
                                }
                            }
                        }
                        else
                        {
                            AiCore.SetBotMoveDestinationAndRefreshPath(ai, oldDestPos);
                        }
                    }
                    else
                    {
                        ai.MoveDestination = soldier.transform.position;
                        ai.path = null;
                    }
                }
            }

            if (currentAction == GoapActionId.None)
            {
                bool keepAcousticOverride = sq != null &&
                                            goapH != null &&
                                            now < board.AcousticInvestigateUntil &&
                                            board.AcousticInvestigatorPtr == ptr &&
                                            goapH.MoveOverride.Kind == AiCore.BotMoveOverrideKind.GoapAction &&
                                            now < goapH.MoveOverride.ExpireTime;
                if (!keepAcousticOverride &&
                    TryExecuteCloseAcousticInvestigate(ai, soldier, goapH, sq, ref board, ws, suppression, personality, now))
                {
                    if (sq != null && sq.Pointer != IntPtr.Zero)
                        _squadBlackboards[sq.Pointer] = board;
                    return;
                }
                if (!keepAcousticOverride)
                    AiCore.ClearBotMoveOverride(goapH, AiCore.BotMoveOverrideKind.GoapAction);
                return;
            }

            ExecuteGoapActionStep(ai, soldier, sq, ref board, ref plan, currentAction, ws, suppression, dist, now);
            if (goapH != null)
            {
                goapH.Goap.Plan = plan;
                if (plan.HasMoveOverride && now < plan.PlanExpireTime)
                    AiCore.SetBotMoveOverride(goapH, AiCore.BotMoveOverrideKind.GoapAction, plan.MoveOverrideDest, plan.PlanExpireTime);
                else
                    AiCore.ClearBotMoveOverride(goapH, AiCore.BotMoveOverrideKind.GoapAction);
            }
            BotActionTrace.RecordGoapState(
                ai,
                soldier,
                personality,
                board.NetDirective,
                plan.LastGoalBit,
                plan.Step0,
                plan.Step1,
                plan.IsEvacuatingBailedCrew,
                suppression,
                dist,
                now);
            if (sq != null && sq.Pointer != IntPtr.Zero)
            {
                _squadBlackboards[sq.Pointer] = board;
            }
            }
            finally
            {
                HitchProfiler.End(HitchBucket.GoapSoldier, profGoapStart);
            }
        }

        private static readonly float[] _playerUnstickCosTable = { 0.0f, 0.0f, 0.7071f, 0.7071f, -0.7071f, -0.7071f, -1.0f, 1.0f };
        private static readonly float[] _playerUnstickSinTable = { 1.0f, -1.0f, 1.0f, -1.0f, 1.0f, -1.0f, 0.0f, 0.0f };

        private static bool TryRecoverChronicallyStuckFootBot(
            SoldierAI ai,
            Soldier soldier,
            IntPtr ptr,
            ref SquadBlackboard board,
            Vector3 myPos,
            float suppression,
            bool isOnBridge,
            float targetDist,
            Vector3 objPos,
            bool hasObjTask,
            float now)
        {
            AiCore.SoldierFastHandles stuckH = AiCore.GetOrCreateSoldierHandles(soldier);
            FootStuckState st = stuckH != null ? stuckH.Goap.FootStuck : default;
            bool onVehicleHull = !soldier.IsOnVehicle() && AiCore.IsFootBotStandingOnVehicleHull(myPos);

            if (now < st.UnstickOverrideUntil)
            {
                bool allowStepDown = onVehicleHull ||
                                     st.ConsecutiveUnsticks >= 2 ||
                                     (myPos.y - st.UnstickDest.y >= 0.85f);
                if ((st.UnstickDest - myPos).sqrMagnitude <= 1.2f * 1.2f ||
                    !AiCore.TryValidateAndSnapDirectMicroStep(
                        myPos,
                        st.UnstickDest,
                        out Vector3 contSnapped,
                        out _,
                        allowStepDown ? 4.8f : 2.8f,
                        onVehicleHull ? 2.85f : 0.75f,
                        allowStepDown ? 2.90f : 0.75f))
                {
                    st.UnstickOverrideUntil = 0f;
                    st.AnchorPos = myPos;
                    st.AnchorTime = now;
                    if (stuckH != null)
                    {
                        stuckH.Goap.FootStuck = st;
                        AiCore.ClearBotMoveOverride(stuckH, AiCore.BotMoveOverrideKind.UnstickSidestep);
                    }
                    if (ai.moveDirectlyToward_end > now)
                        ai.moveDirectlyToward_end = 0f;
                    AiCore.ResetBotPathLockout(ai);
                    ai.UpdatePath();
                    return false;
                }

                if (!ai.HasEnemyAndCanFireAtHim || targetDist <= 6.2f)
                    SetBotPose(ai, soldier, suppression >= 35f ? SoldierPose.Crouch : SoldierPose.Idle, suppression < 35f);
                ai.MoveDirectlyToward(contSnapped, 0.85f);
                return true;
            }

            Vector3 desiredEnd = GetActualMoveEndpoint(ai, soldier);
            Vector2 toEndFlat = new Vector2(desiredEnd.x - myPos.x, desiredEnd.z - myPos.z);
            Vector3 rawMoveDest = ai.MoveDestination;
            Vector2 toMoveDestFlat = new Vector2(rawMoveDest.x - myPos.x, rawMoveDest.z - myPos.z);
            Squad mySq = soldier.joinedSquad;
            Soldier sqLeader = mySq != null ? mySq.Leader : null;
            bool laggingBehindLeader = !soldier.IsSquadLeader() &&
                                       !soldier.IsOnCover() &&
                                       sqLeader != null &&
                                       sqLeader.IsAlive &&
                                       sqLeader.transform != null &&
                                       (sqLeader.transform.position - myPos).sqrMagnitude > 28.0f * 28.0f;
            bool tryingToMove = onVehicleHull ||
                                toEndFlat.sqrMagnitude > 2.5f * 2.5f ||
                                (ai.IsMoving() && rawMoveDest.sqrMagnitude > 0.01f && toMoveDestFlat.sqrMagnitude > 3.5f * 3.5f) ||
                                laggingBehindLeader;

            bool farFromSquadOrGoal = toEndFlat.sqrMagnitude > 28.0f * 28.0f ||
                                      laggingBehindLeader ||
                                      (hasObjTask && (objPos - myPos).sqrMagnitude > 28.0f * 28.0f);

            bool firingHaltedOnSpot = ai.visibleTarget != null &&
                                      (IsInCommittedFiringHalt(ptr, now) ||
                                       (!ai.IsMoving() && (!farFromSquadOrGoal || st.AnchorTime <= 0f || (now - st.AnchorTime) < 5.0f)));

            bool intentionallyStationary =
                !onVehicleHull &&
                (soldier.IsOnCover() ||
                 soldier.UsingBipod() ||
                 firingHaltedOnSpot ||
                 soldier.isInHandsAnimation ||
                 soldier.isInBodyAnimation ||
                 soldier.IsCarryingBody());

            float movedSq = (myPos - st.AnchorPos).sqrMagnitude;
            if (intentionallyStationary || !tryingToMove || (!onVehicleHull && (st.AnchorTime <= 0f || movedSq > 0.65f * 0.65f)))
            {
                st.AnchorPos = myPos;
                st.AnchorTime = now;
                if (intentionallyStationary || !tryingToMove || movedSq > 2.0f * 2.0f)
                    st.ConsecutiveUnsticks = 0;
                if (stuckH != null)
                    stuckH.Goap.FootStuck = st;
                return false;
            }

            if ((!onVehicleHull && now - st.AnchorTime < 1.8f) || now < st.NextUnstickTime)
                return false;

            st.ConsecutiveUnsticks++;
            st.AnchorPos = myPos;
            st.AnchorTime = now;
            st.NextUnstickTime = now + (onVehicleHull ? 0.65f : 2.2f);

            // Player-style Step A ("Spacebar"): Try vaulting cleanly over an obstacle right in front
            if (!onVehicleHull &&
                VisionConfig.EnableWallVaulting.Value &&
                soldier.m_pose != SoldierPose.Prone &&
                !soldier.IsJumping() &&
                soldier.CanVault(out Vector3 vaultPos) &&
                IsValidVaultObstacle(ai, soldier, ptr, soldier.transform, ref vaultPos, now, out string vaultReason) &&
                AiCore.TryConsumeBotRpcBudget(0.35f))
            {
                if (stuckH != null)
                    stuckH.Goap.FootStuck = st;
                soldier.Vault(vaultPos);
                WallsVaulted++;
                FootBotUnsticksCount++;
                BotActionTrace.RecordEvent(soldier, BotTraceEventKind.Vault, vaultReason);
                return true;
            }

            // Player-style Step B ("Go Around"): Drop unreachable cover, sidestep 2.2m clear, and refresh A* path
            ai.moveDirectlyToward_end = 0f;
            ClearStaleCoverForUnstick(soldier, ptr, ref board);

            Vector3 fwd = rawMoveDest.sqrMagnitude > 0.01f && toMoveDestFlat.sqrMagnitude > 0.25f
                ? new Vector3(toMoveDestFlat.x, 0f, toMoveDestFlat.y)
                : new Vector3(toEndFlat.x, 0f, toEndFlat.y);
            if (fwd.sqrMagnitude < 0.04f && sqLeader != null && sqLeader.transform != null)
            {
                Vector3 toLdr = sqLeader.transform.position - myPos;
                fwd = new Vector3(toLdr.x, 0f, toLdr.z);
            }
            if (fwd.sqrMagnitude < 0.04f && hasObjTask)
            {
                Vector3 toObj = objPos - myPos;
                fwd = new Vector3(toObj.x, 0f, toObj.z);
            }
            if (fwd.sqrMagnitude < 0.04f && soldier.transform != null)
                fwd = new Vector3(soldier.transform.forward.x, 0f, soldier.transform.forward.z);
            fwd = fwd.sqrMagnitude > 0.001f ? fwd.normalized : Vector3.forward;

            Vector3 right = new Vector3(fwd.z, 0f, -fwd.x);
            int sideSign = ((AiCore.GetDeterministicHash(ptr, st.ConsecutiveUnsticks) & 1) == 0) ? 1 : -1;
            bool hasWater = AiCore.TryGetActiveWaterLevel(out float waterLevel);
            bool alreadyInShallowWater = hasWater && myPos.y <= waterLevel + 0.25f;
            float minAllowedWaterY = alreadyInShallowWater
                ? Mathf.Max(waterLevel - 1.35f, myPos.y - 0.15f)
                : (waterLevel + 0.15f);
            Vector3 waistOrigin = myPos + Vector3.up * 0.55f;
            float stepDist = onVehicleHull ? 2.8f : 2.2f;
            bool destSignificantlyBelow = (desiredEnd.sqrMagnitude > 0.01f && myPos.y - desiredEnd.y >= 1.15f) ||
                                          (rawMoveDest.sqrMagnitude > 0.01f && myPos.y - rawMoveDest.y >= 1.15f);
            bool elevatedOrChronicTrap = onVehicleHull || st.ConsecutiveUnsticks >= 2 || destSignificantlyBelow;
            float maxMicroFlatDist = elevatedOrChronicTrap ? 3.8f : 2.5f;
            float maxMicroUpY = onVehicleHull ? 2.85f : 0.72f;
            float maxMicroDownY = elevatedOrChronicTrap ? 2.85f : 0.72f;

            int tableLen = _playerUnstickCosTable.Length;
            int rotStart = st.ConsecutiveUnsticks % tableLen;

            // Pass 1: Level/gentle-slope micro-step (<= 2.2m, or up to 3.3m with step-down when perched on an
            // elevated ledge, parapet, rubble pile, or vehicle hull) pre-validated so GuardDirectMoveSegment allows it.
            bool hasLevelFallback = false;
            Vector3 levelFallbackCand = default;
            for (int k = 0; k < tableLen; k++)
            {
                int a = (rotStart + k) % tableLen;
                Vector3 dir = fwd * _playerUnstickCosTable[a] + right * (_playerUnstickSinTable[a] * sideSign);
                Vector3 rawProbe = myPos + dir * stepDist;
                bool valid = AiCore.TryValidateAndSnapDirectMicroStep(
                    myPos,
                    rawProbe,
                    out Vector3 candPos,
                    out _,
                    maxMicroFlatDist,
                    maxMicroUpY,
                    maxMicroDownY);

                // If bot is perched on a wider ledge/porch/wreck (>2.2m to the edge), also probe at 3.25m for a drop-off.
                if (!onVehicleHull && elevatedOrChronicTrap && (!valid || candPos.y > myPos.y - 0.55f))
                {
                    Vector3 wideProbe = myPos + dir * 3.25f;
                    if (AiCore.TryValidateAndSnapDirectMicroStep(
                            myPos,
                            wideProbe,
                            out Vector3 wideCand,
                            out _,
                            maxMicroFlatDist,
                            maxMicroUpY,
                            maxMicroDownY) &&
                        wideCand.y <= myPos.y - 0.55f)
                    {
                        candPos = wideCand;
                        rawProbe = wideProbe;
                        valid = true;
                    }
                }

                if (!valid)
                    continue;

                if (hasWater && candPos.y <= minAllowedWaterY)
                    continue;

                if (isOnBridge)
                {
                    if (!AiCore.IsSafeBridgeDeckStep(myPos, rawProbe, out Vector3 deckPos))
                        continue;
                    candPos = deckPos;
                }
                if (AiCore.TryGetNearbyKnownMine(candPos, 4.0f, false, out _))
                    continue;

                if (!onVehicleHull && elevatedOrChronicTrap && candPos.y > myPos.y - 0.55f)
                {
                    if (!hasLevelFallback)
                    {
                        hasLevelFallback = true;
                        levelFallbackCand = candPos;
                    }
                    continue;
                }

                st.UnstickDest = candPos;
                st.UnstickOverrideUntil = now + 1.35f;
                if (stuckH != null)
                {
                    stuckH.Goap.FootStuck = st;
                    AiCore.SetBotMoveOverride(stuckH, AiCore.BotMoveOverrideKind.UnstickSidestep, candPos, now + 1.35f);
                }

                if (!ai.HasEnemyAndCanFireAtHim || targetDist <= 6.2f)
                    SetBotPose(ai, soldier, suppression >= 35f ? SoldierPose.Crouch : SoldierPose.Idle, suppression < 35f);
                ai.MoveDirectlyToward(candPos, 1.35f);
                FootBotUnsticksCount++;
                return true;
            }

            if (hasLevelFallback)
            {
                st.UnstickDest = levelFallbackCand;
                st.UnstickOverrideUntil = now + 1.35f;
                if (stuckH != null)
                {
                    stuckH.Goap.FootStuck = st;
                    AiCore.SetBotMoveOverride(stuckH, AiCore.BotMoveOverrideKind.UnstickSidestep, levelFallbackCand, now + 1.35f);
                }

                if (!ai.HasEnemyAndCanFireAtHim || targetDist <= 6.2f)
                    SetBotPose(ai, soldier, suppression >= 35f ? SoldierPose.Crouch : SoldierPose.Idle, suppression < 35f);
                ai.MoveDirectlyToward(levelFallbackCand, 1.35f);
                FootBotUnsticksCount++;
                return true;
            }

            // Pass 2: Steep > 18.8 deg hillside or +0.34m curb fallback — route a 4.5m radial contour step through CPathFinding
            // (and if CPathFinding cannot bridge a >= 0.85m ledge drop, allow direct drop-off override onto the validated ground).
            if (!isOnBridge)
            {
                const float slopeStepDist = 4.5f;
                float maxNonTerrainDrop = elevatedOrChronicTrap ? 2.85f : 2.0f;
                for (int k = 0; k < tableLen; k++)
                {
                    int a = (rotStart + k) % tableLen;
                    Vector3 dir = fwd * _playerUnstickCosTable[a] + right * (_playerUnstickSinTable[a] * sideSign);
                    if (!elevatedOrChronicTrap && AiCore.IsMovementBlockedByObstacle(waistOrigin, dir, 1.75f))
                        continue;

                    Vector3 probe = myPos + dir * slopeStepDist;
                    // +/-8.0m vertical window covers full 60 deg (normal.y >= 0.50, 4.5 * tan(60) = 7.79m) TerrainCollider
                    // climbs/descents while capping non-terrain rooftops upward at +2.0m and downward drops at maxNonTerrainDrop.
                    if (!AiCore.TrySnapWalkableGroundProbe(myPos, probe, 8.0f, 16.0f, 0.50f, 2.0f, hasWater && !alreadyInShallowWater, waterLevel, out Vector3 candPos, maxNonTerrainDrop))
                        continue;

                    if (hasWater && candPos.y <= minAllowedWaterY)
                        continue;

                    if (AiCore.TryGetNearbyKnownMine(candPos, 4.0f, false, out _))
                        continue;

                    if (elevatedOrChronicTrap &&
                        (!ai.HasPath || myPos.y - candPos.y >= 0.85f) &&
                        AiCore.TryValidateAndSnapDirectMicroStep(myPos, candPos, out Vector3 snappedDropDest, out _, 4.8f, onVehicleHull ? 2.85f : 0.75f, 2.90f))
                    {
                        float duration = 1.35f + Mathf.Min(st.ConsecutiveUnsticks, 4) * 0.25f;
                        st.UnstickDest = snappedDropDest;
                        st.UnstickOverrideUntil = now + duration;
                        st.NextUnstickTime = now + duration + 0.6f;
                        if (stuckH != null)
                        {
                            stuckH.Goap.FootStuck = st;
                            AiCore.SetBotMoveOverride(stuckH, AiCore.BotMoveOverrideKind.UnstickSidestep, snappedDropDest, st.UnstickOverrideUntil);
                        }

                        if (!ai.HasEnemyAndCanFireAtHim || targetDist <= 6.2f)
                            SetBotPose(ai, soldier, suppression >= 35f ? SoldierPose.Crouch : SoldierPose.Idle, suppression < 35f);
                        ai.MoveDirectlyToward(snappedDropDest, duration);
                        FootBotUnsticksCount++;
                        return true;
                    }

                    st.UnstickOverrideUntil = 0f;
                    st.NextUnstickTime = now + 2.6f;
                    if (stuckH != null)
                    {
                        stuckH.Goap.FootStuck = st;
                        AiCore.SetBotMoveOverride(stuckH, AiCore.BotMoveOverrideKind.UnstickSidestep, candPos, now + 2.5f);
                    }

                    if (!ai.HasEnemyAndCanFireAtHim || targetDist <= 6.2f)
                        SetBotPose(ai, soldier, suppression >= 35f ? SoldierPose.Crouch : SoldierPose.Idle, suppression < 35f);
                    AiCore.ResetBotPathLockout(ai);
                    ai.MoveDestination = candPos;
                    ai.UpdatePath();
                    FootBotUnsticksCount++;
                    return true;
                }
            }

            if (stuckH != null)
                stuckH.Goap.FootStuck = st;
            AiCore.ResetBotPathLockout(ai);
            ai.UpdatePath();
            FootBotUnsticksCount++;
            return false;
        }

        private static void ClearStaleCoverForUnstick(Soldier soldier, IntPtr ptr, ref SquadBlackboard board)
        {
            if (AiCore.TryGetSoldierHandles(ptr, out var staleH))
            {
                staleH.Stance.LocalCoverProtectedUntil = 0f;
                staleH.Goap.Plan = default;
                AiCore.ClearBotMoveOverride(staleH, AiCore.BotMoveOverrideKind.GoapAction);
            }
            AiDestination curDest = soldier.HasDestinationAssigned ? soldier.targetDestination : null;
            if (curDest != null && soldier.nonReachableCovers != null)
                soldier.nonReachableCovers.Add(curDest);

            if (board.FlankerLeftPtr == ptr ||
                (curDest != null && board.FlankLeftCover != null && board.FlankLeftCover.Pointer == curDest.Pointer))
            {
                board.FlankerLeftPtr = IntPtr.Zero;
                board.FlankLeftCover = null;
            }
            if (board.FlankerRightPtr == ptr ||
                (curDest != null && board.FlankRightCover != null && board.FlankRightCover.Pointer == curDest.Pointer))
            {
                board.FlankerRightPtr = IntPtr.Zero;
                board.FlankRightCover = null;
            }

            soldier.CoverPosition(null);
            AiCore.InvalidateTargetDestinationCache(soldier);
        }

        private static bool IsMinePlacementEnvironmentSafe(Soldier sapper, Vector3 myPos, Squad sq, ref SquadBlackboard board)
        {
            if (sapper == null || string.IsNullOrEmpty(sapper.faction))
                return false;

            if (board.HasActiveObjectiveTask && sq != null && sq.currentTask != null)
            {
                if (sq.currentTask.IsInsideTaskArea(myPos, 0.55f) ||
                    (myPos - board.ObjectivePosition).sqrMagnitude < 14.0f * 14.0f)
                {
                    return false;
                }
            }

            if (AiCore.TryGetNearbyKnownMine(myPos, 9.0f, false, out _))
                return false;

            string myFaction = AiCore.GetCachedFaction(sapper);
            if (AiCore.TryGetNearestFriendlySpawnPosition(myFaction, myPos, out Vector3 friendlySpawn) &&
                (myPos - friendlySpawn).sqrMagnitude < 45.0f * 45.0f)
            {
                return false;
            }

            AiCore.EnsureWorldSnapshot();
            var soldiers = AiCore.WorldSoldiers;
            int sCount = AiCore.WorldSoldierCount;
            IntPtr sapperPtr = sapper.Pointer;

            for (int i = 0; i < sCount; i++)
            {
                ref AiCore.SoldierSnapshot sSnap = ref soldiers[i];
                if (sSnap.Ptr == sapperPtr || sSnap.HasSurrendered || sSnap.IsInjured)
                    continue;

                float dSq = (sSnap.Pos - myPos).sqrMagnitude;
                if (AiCore.AreSameSideFactions(myFaction, sSnap.Faction))
                {
                    if (dSq <= 12.0f * 12.0f)
                        return false;
                }
                else if (AiCore.AreEnemyFactions(myFaction, sSnap.Faction))
                {
                    if (dSq <= 22.0f * 22.0f)
                        return false;
                }
            }

            try
            {
                var allVeh = Vehicle.allVehicles;
                if (allVeh != null)
                {
                    int vCount = allVeh.Count;
                    for (int i = 0; i < vCount; i++)
                    {
                        Vehicle v = allVeh[i];
                        if (v == null || v.Pointer == IntPtr.Zero || v.transform == null || v.IsAirVehicle() || v.IsCoverDestroyed())
                            continue;

                        if ((v.transform.position - myPos).sqrMagnitude <= 22.0f * 22.0f)
                            return false;
                    }
                }
            }
            catch { }

            return true;
        }

        private static bool HasRecipeIngredientsInInventory(
            Il2CppSystem.Collections.Generic.List<VirtualItem> items,
            DeployableVehicleRecipy recipe,
            VirtualDeployableVehicle mainItem)
        {
            if (items == null || recipe == null)
                return false;

            var ings = recipe.ingredients;
            if (ings == null || ings.Length == 0)
                return true;

            int ingLen = ings.Length;
            int itemCount = items.Count;
            string mainId = mainItem != null ? mainItem.item_id : null;
            for (int i = 0; i < ingLen; i++)
            {
                string needed = ings[i];
                if (string.IsNullOrEmpty(needed))
                    continue;
                if (!string.IsNullOrEmpty(mainId) && string.Equals(needed, mainId, StringComparison.OrdinalIgnoreCase))
                    continue;

                bool found = false;
                for (int k = 0; k < itemCount; k++)
                {
                    VirtualItem vi = items[k];
                    if (vi != null && !string.IsNullOrEmpty(vi.item_id) &&
                        string.Equals(vi.item_id, needed, StringComparison.OrdinalIgnoreCase))
                    {
                        found = true;
                        break;
                    }
                }
                if (!found)
                    return false;
            }
            return true;
        }

        private static bool TryExecuteSpecialMunitionsAndTripods(
            SoldierAI ai,
            Soldier soldier,
            IntPtr ptr,
            Squad sq,
            ref SquadBlackboard board,
            Vector3 myPos,
            float suppression,
            float dist,
            bool isOnBridge,
            float now)
        {
            if (soldier.inventory == null || soldier.inventory.inventory == null || soldier.inventory.inventory.items == null)
                return false;

            if (soldier.isInHandsAnimation || soldier.isInBodyAnimation || soldier.IsReloading)
                return false;

            AiCore.SoldierFastHandles specH = AiCore.GetOrCreateSoldierHandles(soldier);
            if (specH != null && now < specH.Goap.NextSpecialMunitionTime)
                return false;

            var items = soldier.inventory.inventory.items;
            int itemCount = items.Count;
            if (itemCount <= 0)
                return false;

            bool syncOnline = Photon.Pun.PhotonNetwork.InRoom;

            // 1. Heavy Satchels / Demolition Charges vs bunkers, static emplacements, or enemy armor within throw range
            Spottable demoTarget = ai.visibleTarget != null ? ai.visibleTarget : board.PrimaryTarget;
            if (demoTarget != null && demoTarget.Pointer != IntPtr.Zero && now > soldier.throw_end_time + 5.0f)
            {
                Vehicle targetVeh = demoTarget.TryCast<Vehicle>();
                AiCore.VehicleFastHandles vh = targetVeh != null ? AiCore.GetOrCreateVehicleHandles(targetVeh) : null;
                bool isHeavyFortifiedOrArmor = targetVeh != null &&
                                               !targetVeh.IsDisabled() &&
                                               !targetVeh.IsCoverDestroyed() &&
                                               vh != null &&
                                               (vh.IsStaticEmplacement || vh.IsTank || vh.IsWheeledOrTank);
                if (isHeavyFortifiedOrArmor && AiCore.TryGetSpottableCenter(demoTarget, out Vector3 targetPos))
                {
                    float dTarget = Vector3.Distance(myPos, targetPos);
                    for (int i = 0; i < itemCount; i++)
                    {
                        VirtualItem vi = items[i];
                        if (vi == null || !AiCore.TryGetThrowablePrefabInfo(vi, out AiCore.ThrowablePrefabInfo tInfo) || !tInfo.IsSatchelOrHeavyDemo)
                            continue;

                        float maxRange = Mathf.Min(23.0f, soldier.GetGrenadeThrowRange(vi));
                        if (dTarget < 4.5f || dTarget > maxRange)
                            continue;

                        float safeRadius = Mathf.Max(8.5f, tInfo.ExplosionRadius + 3.0f);
                        if (!IsSafeToThrowGrenadeAt(soldier, targetPos, dTarget, safeRadius))
                            continue;

                        if (syncOnline && !AiCore.TryConsumeBotRpcBudget(0.45f))
                        {
                            if (specH != null)
                                specH.Goap.NextSpecialMunitionTime = now + 2.5f;
                            return false;
                        }

                        soldier.SetGunTriggerPressed(false);
                        soldier.Throw(vi, demoTarget);
                        if (specH != null)
                            specH.Goap.NextSpecialMunitionTime = now + 12.0f;
                        AiCore.SpecialMunitionsUsedCount++;
                        GoapPlansExecutedCount++;
                        TrySayTacticalVoice(soldier, sq, VoiceManager.VoiceClip.granade, 0.05f);

                        Vector3 away = myPos - targetPos;
                        away.y = 0f;
                        Vector3 awayDir = away.sqrMagnitude > 0.04f ? away.normalized : -soldier.transform.forward;
                        Vector3 sapDest = myPos + awayDir * 9.0f;
                        AiCore.SetBotMoveOverride(specH, AiCore.BotMoveOverrideKind.SapperStepBack, sapDest, now + 3.5f);
                        AiCore.MoveBotTowardPosition(ai, soldier, sapDest, 1.0f, 3.5f);
                        return true;
                    }
                }
            }

            // 2. Wheel-Triggered AT Pressure Mines (FuseType.VehiclePressionFuse == 2) along vehicle approaches!
            //    (Skip unimplemented vanilla InfantryPressionFuse == 1 duds, and only deploy AT mines when enemy ground vehicles exist!)
            if (!isOnBridge &&
                suppression < 5.0f &&
                ai.visibleTarget == null &&
                !ai.HasEnemyAndCanFireAtHim &&
                now > soldier.throw_end_time + 5.0f &&
                (board.ThreatIsArmor || ((board.IsDefendingObjective || (sq != null && sq.order == Order.defend)) && !ai.IsMoving())) &&
                AiCore.AnyEnemyGroundVehiclesPresent(soldier.faction))
            {
                VirtualItem mineItem = null;
                for (int i = 0; i < itemCount; i++)
                {
                    VirtualItem vi = items[i];
                    if (vi == null || !AiCore.IsPressureMineItem(vi, out bool atFlag) || !atFlag)
                        continue;

                    mineItem = vi;
                    break;
                }

                if (mineItem != null && IsMinePlacementEnvironmentSafe(soldier, myPos, sq, ref board))
                {
                    Vector3 fwd = soldier.transform != null ? soldier.transform.forward : Vector3.forward;
                    fwd.y = 0f;
                    fwd = fwd.sqrMagnitude > 0.01f ? fwd.normalized : Vector3.forward;
                    Vector3 tossLandingProbe = myPos + fwd * 3.5f;
                    bool hasMineWl = AiCore.TryGetActiveWaterLevel(out float mineWl);
                    if (Physics.Raycast(tossLandingProbe + Vector3.up * 2.0f, Vector3.down, out RaycastHit mGround, 4.5f, AiCore.GetSolidEnvironmentMask(), QueryTriggerInteraction.Ignore) &&
                        mGround.normal.y >= 0.72f &&
                        (!hasMineWl || mGround.point.y > mineWl + 0.15f))
                    {
                        if (syncOnline && !AiCore.TryConsumeBotRpcBudget(0.45f))
                        {
                            if (specH != null)
                                specH.Goap.NextSpecialMunitionTime = now + 2.5f;
                            return false;
                        }

                        soldier.SetGunTriggerPressed(false);
                        soldier.Throw(mineItem, null);
                        // RefreshActiveLandMinesIfDue tracks the real landed ItemGrenade transform automatically
                        // (no fake 1.15m assumption or janky 6.5m backward SapperStepBack sprint needed!).
                        if (specH != null)
                            specH.Goap.NextSpecialMunitionTime = now + 18.0f;
                        AiCore.SpecialMunitionsUsedCount++;
                        GoapPlansExecutedCount++;
                        return true;
                    }
                }
            }

            // 3. Deployable Tripods / Crew Weapons (VirtualDeployableVehicle -> Soldier.BuildVehicle)
            if (!isOnBridge &&
                !ai.IsMoving() &&
                suppression < 5.0f &&
                ai.visibleTarget == null &&
                !AiCore.HasLowCeilingOverhead(myPos, 2.2f))
            {
                bool hasAxis = false;
                Vector3 axisTarget = Vector3.zero;
                if (board.HasActiveThreat)
                {
                    axisTarget = board.ThreatPosition;
                    hasAxis = true;
                }
                else if (board.HasActiveObjectiveTask)
                {
                    axisTarget = board.ObjectivePosition;
                    hasAxis = true;
                }

                if (hasAxis)
                {
                    float axisDistSq = (axisTarget - myPos).sqrMagnitude;
                    if (axisDistSq >= 30.0f * 30.0f && axisDistSq <= 220.0f * 220.0f)
                    {
                        Vector3 toAxis = axisTarget - myPos;
                        toAxis.y = 0f;
                        Vector3 fwd = soldier.transform.forward;
                        fwd.y = 0f;
                        if (toAxis.sqrMagnitude > 1.0f && fwd.sqrMagnitude > 0.01f &&
                            Vector3.Dot(fwd.normalized, toAxis.normalized) >= 0.25f)
                        {
                            for (int i = 0; i < itemCount; i++)
                            {
                                VirtualItem vi = items[i];
                                if (vi == null)
                                    continue;

                                VirtualDeployableVehicle deployVi = vi.TryCast<VirtualDeployableVehicle>();
                                if (deployVi == null)
                                    continue;

                                ItemObject prefab = null;
                                try { prefab = vi.GetItemPrefab(); } catch { }
                                ItemObjectDeployableVehicle deployObj = prefab != null ? prefab.TryCast<ItemObjectDeployableVehicle>() : null;
                                if (deployObj == null || deployObj.recipies == null || deployObj.recipies.Length == 0)
                                    continue;

                                DeployableVehicleRecipy matched = null;
                                int rLen = deployObj.recipies.Length;
                                for (int r = 0; r < rLen; r++)
                                {
                                    DeployableVehicleRecipy rec = deployObj.recipies[r];
                                    if (rec != null && HasRecipeIngredientsInInventory(items, rec, deployVi))
                                    {
                                        matched = rec;
                                        break;
                                    }
                                }
                                if (matched == null)
                                    continue;

                                if (!soldier.CanBuildVehicle(out Vector3 buildNormal))
                                    continue;

                                if (syncOnline && !AiCore.TryConsumeBotRpcBudget(0.50f))
                                {
                                    if (specH != null)
                                        specH.Goap.NextSpecialMunitionTime = now + 2.5f;
                                    return false;
                                }

                                soldier.SetGunTriggerPressed(false);
                                if (soldier.BuildVehicle(deployVi, matched, deployObj.deploy_animation, buildNormal))
                                {
                                    if (specH != null)
                                        specH.Goap.NextSpecialMunitionTime = now + 18.0f;
                                    AiCore.SpecialMunitionsUsedCount++;
                                    GoapPlansExecutedCount++;
                                    return true;
                                }
                            }
                        }
                    }
                }
            }

            if (specH != null)
                specH.Goap.NextSpecialMunitionTime = now + 2.5f;
            return false;
        }

        private static bool IsSoldierBailedOutVehicleCrew(Soldier soldier, Squad sq)
        {
            if (soldier == null || sq == null || !AiCore.IsTrueVehicleCrewSquad(sq) || soldier.IsOnVehicle())
                return false;

            if (sq.SomeoneIsRepairing())
                return false;

            if (!IsSquadOnRetreatTask(sq) &&
                (soldier.IsReachingVehicleToMount() ||
                 soldier.IsEnteringVehicle ||
                 soldier.VehicleToOperate() != null))
            {
                if (!AiCore.TryGetSoldierHandles(soldier.Pointer, out AiCore.SoldierFastHandles h) ||
                    h.Goap.RemountCooldownUntil <= Time.time)
                {
                    return false;
                }
            }

            if (IsSquadOnRetreatTask(sq))
                return true;

            AiDestination td = soldier.targetDestination;
            return td == null || td.TryCast<VehicleRepairTask>() == null;
        }

        private static void ExecuteGoapActionStep(
            SoldierAI ai,
            Soldier soldier,
            Squad sq,
            ref SquadBlackboard board,
            ref BotGoapPlan plan,
            GoapActionId action,
            GoapStateBits ws,
            float suppression,
            float dist,
            float now)
        {
            IntPtr ptr = soldier.Pointer;
            AiCore.SoldierFastHandles shGoap = AiCore.GetOrCreateSoldierHandles(soldier);
            Vector3 myPos = soldier.transform.position;
            plan.HasMoveOverride = false;
            plan.IsEvacuatingBailedCrew = false;

            switch (action)
            {
                case GoapActionId.SeekEmergencyCover:
                    ExecuteGoapSeekEmergencyCover(ai, soldier, sq, shGoap, ref board, ref plan, ws, suppression, myPos, now);
                    break;

                case GoapActionId.BandageInCover:
                    plan.LockUnitCoverChange = true;
                    SetBotPose(ai, soldier, SoldierPose.Crouch);
                    if (soldier.isBleeding && !soldier.isInHandsAnimation && !soldier.isInBodyAnimation)
                    {
                        soldier.SetGunTriggerPressed(false);
                        ai.TryStopBleeding();
                    }
                    break;

                case GoapActionId.RepositionBehindWall:
                    {
                        if (shGoap != null)
                            shGoap.Stance.NextWallSideCheckTime = now + 10.0f;
                        Vector3 wallThreatDir = GetEffectiveThreatDirection(soldier, sq, myPos, board.ThreatDirection);
                        if (TryFindShelteredSideOfWall(myPos, wallThreatDir, soldier.transform.root, out Vector3 shelteredPos))
                        {
                            DestinationWithoutCover newDest = new DestinationWithoutCover(shelteredPos, SoldierPose.Crouch);
                            AiCore.AssignBotCoverOrDestination(ai, soldier, newDest.Cast<AiDestination>());
                            float shDistSq = (shelteredPos - myPos).sqrMagnitude;
                            if (soldier.m_pose == SoldierPose.Prone && shDistSq > 1.8f * 1.8f)
                                SetBotPose(ai, soldier, SoldierPose.Crouch, true);
                            plan.LockUnitCoverChange = true;
                            plan.HasMoveOverride = shDistSq > 1.8f * 1.8f;
                            plan.MoveOverrideDest = shelteredPos;
                            plan.PlanExpireTime = now + 4.0f;
                            plan.NextReplanTime = Mathf.Max(plan.NextReplanTime, now + 3.2f);
                            WallSideRepositionsCount++;
                            GoapPlansExecutedCount++;
                            if (shDistSq <= 1.8f * 1.8f && plan.StepCount > 1)
                            {
                                plan.StepIndex = 1;
                            }
                        }
                        else
                        {
                            // Solid building facade with no immediate wrap-around: seek nearby leeward CombatCover instead
                            TrySeekNearbyCover(ai, soldier);
                            plan.PlanExpireTime = now + 2.5f;
                            plan.NextReplanTime = Mathf.Max(plan.NextReplanTime, now + 2.0f);
                        }
                    }
                    break;

                case GoapActionId.MaintainRadioLinkWithLeader:
                    if (sq != null && sq.Leader != null && sq.Leader.IsAlive && sq.Leader.transform != null)
                    {
                        Vector3 leaderPos = sq.Leader.transform.position;
                        if (soldier.HasDestinationAssigned && soldier.targetDestination != null)
                        {
                            Vector3 covPos = soldier.targetDestination.GetCoverPosition();
                            if ((covPos - leaderPos).sqrMagnitude > 15.0f * 15.0f)
                            {
                                if (shGoap != null)
                                    shGoap.Stance.LocalCoverProtectedUntil = 0f;
                                soldier.CoverPosition(null);
                            }
                        }

                        Vector3 radioThreatDir = GetEffectiveThreatDirection(soldier, sq, leaderPos, board.ThreatDirection);
                        // Position the Radioman 3.5m behind the Squad Leader (away from the enemy threat line)
                        Vector3 safeRadioSpot = leaderPos - radioThreatDir * 3.5f;
                        plan.LockUnitCoverChange = true;
                        plan.HasMoveOverride = true;
                        plan.MoveOverrideDest = safeRadioSpot;
                        float radioDistSq = (myPos - safeRadioSpot).sqrMagnitude;
                        if (radioDistSq > 4.0f * 4.0f)
                        {
                            ApplyDesiredMovingPose(ai, soldier, radioDistSq, suppression);
                            AiCore.MoveBotTowardPosition(ai, soldier, safeRadioSpot, 0.5f, 2.8f);
                            GoapPlansExecutedCount++;
                        }
                        else if (!ai.IsMoving() && (board.HasActiveThreat || soldier.GetSuppressionValue() >= 5f || soldier.isAlerted))
                        {
                            SetBotPose(ai, soldier, SoldierPose.Crouch);
                        }
                    }
                    break;


                case GoapActionId.ThrowSmokeScreen:
                    ExecuteGoapThrowSmokeScreen(ai, soldier, sq, ptr, shGoap, ref board, ref plan, ws, suppression, dist, myPos, now);
                    break;

                case GoapActionId.SuppressThreatPosition:
                case GoapActionId.HoldOverwatchFire:
                    ExecuteGoapHoldOverwatchOrSuppress(ai, soldier, sq, ptr, shGoap, ref board, ref plan, action, dist, myPos, now);
                    break;

                case GoapActionId.MoveToFlankCover:
                    ExecuteGoapMoveToFlankCover(ai, soldier, sq, ptr, shGoap, ref board, ref plan, suppression, myPos, now);
                    break;

                case GoapActionId.FlushThreatWithFrag:
                    ExecuteGoapFlushThreatWithFrag(ai, soldier, ptr, shGoap, ref board, ref plan, myPos, now);
                    break;

                case GoapActionId.BoundWithOverwatch:
                    if (ai.IsMoving())
                    {
                        float bndRemSq = (GetActualMoveEndpoint(ai, soldier) - myPos).sqrMagnitude;
                        if (soldier.m_pose == SoldierPose.Prone && bndRemSq > 1.8f * 1.8f)
                            SetBotPose(ai, soldier, SoldierPose.Crouch, true);
                        ApplyDesiredMovingPose(ai, soldier, bndRemSq, suppression);
                    }
                    else if ((ws & GoapStateBits.ObjectiveCapContestUrgent) == 0 &&
                             soldier.IsOnCover() &&
                             shGoap != null &&
                             (now < shGoap.Stance.CoverSettledUntil ||
                              (shGoap.Vision.LastKnownTarget.LastSeenTime > 0f && (now - shGoap.Vision.LastKnownTarget.LastSeenTime) <= 3.0f)))
                    {
                        plan.LockUnitCoverChange = true;
                        plan.PlanExpireTime = Mathf.Min(plan.PlanExpireTime, now + 1.5f);
                    }
                    else if ((ws & GoapStateBits.ObjectiveCapContestUrgent) != 0 && board.HasActiveObjectiveTask)
                    {
                        Vector3 toObj = board.ObjectivePosition - myPos;
                        toObj.y = 0f;
                        Vector3 objDir = toObj.sqrMagnitude > 0.01f ? toObj.normalized : board.ThreatDirection;
                        TryAdvanceBoundTowardThreat(ai, soldier, sq, myPos, board.ObjectivePosition, objDir, now, ref plan);
                    }
                    else if (board.HasActiveThreat &&
                             (board.IsThreatBeingPinned(now) ||
                              now < board.SmokeActiveUntil ||
                              (ws & (GoapStateBits.IsSuppressed | GoapStateBits.IsHeavilySuppressed | GoapStateBits.NearFreshCasualty)) == 0) &&
                             !ai.HasEnemyAndCanFireAtHim &&
                             (myPos - board.ThreatPosition).sqrMagnitude > 16f * 16f)
                    {
                        TryAdvanceBoundTowardThreat(ai, soldier, sq, myPos, board.ThreatPosition, board.ThreatDirection, now, ref plan);
                    }
                    else
                    {
                        plan.PlanExpireTime = Mathf.Min(plan.PlanExpireTime, now + 0.85f);
                    }
                    break;

                case GoapActionId.PointBlankBayonetBreach:
                    if (ExecuteSoldierPointBlankCqbStep(ai, soldier, shGoap, dist, now))
                    {
                        plan.LockUnitCoverChange = true;
                        if (shGoap != null && now >= shGoap.Combat.NextBoundingHoldCountTime)
                        {
                            shGoap.Combat.NextBoundingHoldCountTime = now + 2.5f;
                            GoapPlansExecutedCount++;
                        }
                    }
                    else
                    {
                        plan.PlanExpireTime = 0f;
                    }
                    break;

                case GoapActionId.ScavengeSupplyFromCorpse:
                    ExecuteGoapScavengeSupplyFromCorpse(ai, soldier, sq, shGoap, ref plan, ws, suppression, myPos, now);
                    break;

                case GoapActionId.RescueSurrenderedComrade:
                    if (board.SurrenderedComrade != null && AiCore.IsTrueSurrenderedPrisoner(board.SurrenderedComrade))
                    {
                        Soldier targetSurr = board.SurrenderedComrade;
                        float dSq = (targetSurr.transform.position - myPos).sqrMagnitude;
                        AiCore.SoldierFastHandles libH = AiCore.GetOrCreateSoldierHandles(soldier);
                        bool canApproach = !(soldier.IsSquadLeader() && board.AliveMemberCount >= 2) &&
                                           !ai.HasEnemyAndCanFireAtHim &&
                                           suppression < 35f;
                        if (TryExecuteComradeLiberationStep(ai, soldier, libH, targetSurr, dSq, 28.0f * 28.0f, 8.5f, canApproach, now, out bool liberatedNow))
                        {
                            board.RescuerPtr = ptr;
                            board.RescueUntil = now + 4.0f;
                            if (liberatedNow)
                            {
                                GoapPlansExecutedCount++;
                                plan.PlanExpireTime = 0f;
                            }
                            else if (dSq > 3.8f * 3.8f)
                            {
                                plan.LockUnitCoverChange = true;
                                Vector3 surrPos = targetSurr.transform.position;
                                plan.HasMoveOverride = true;
                                plan.MoveOverrideDest = surrPos;
                                ApplyDesiredMovingPose(ai, soldier, dSq, suppression);
                            }
                        }
                        else
                        {
                            if (board.RescuerPtr == ptr)
                                board.RescuerPtr = IntPtr.Zero;
                            plan.PlanExpireTime = 0f;
                        }
                    }
                    else
                    {
                        if (board.RescuerPtr == ptr)
                            board.RescuerPtr = IntPtr.Zero;
                        plan.PlanExpireTime = 0f;
                    }
                    break;

                case GoapActionId.LoneWolfLinkUpOrAmbush:
                    ExecuteGoapLoneWolfLinkUpOrAmbush(ai, soldier, sq, ptr, shGoap, ref board, ref plan, ws, suppression, dist, myPos, now);
                    break;

                case GoapActionId.TacticalFallbackRetreat:
                    ExecuteGoapTacticalFallbackRetreat(ai, soldier, sq, shGoap, ref board, ref plan, dist, myPos, now);
                    break;

                case GoapActionId.DirectFireEngagement:
                    {
                        if (ai.visibleTarget != null)
                        {
                            Vehicle targetVeh = ai.visibleTarget.TryCast<Vehicle>();
                            if (targetVeh != null &&
                                targetVeh.TryCast<VehiclePlane>() == null &&
                                (board.SquadHasAntiTank || HasUsableAntiTankCapability(soldier)))
                            {
                                bool isTank = targetVeh.TryCast<VehicleTank>() != null;
                                TrySayTacticalVoice(
                                    soldier,
                                    sq,
                                    isTank ? VoiceManager.VoiceClip.attackThatTank : VoiceManager.VoiceClip.attackThatVehicle,
                                    0.08f);
                            }
                        }

                        if ((ws & GoapStateBits.InCover) == 0 &&
                            (suppression >= 8.0f || (ws & GoapStateBits.NearFreshCasualty) != 0) &&
                            AiCore.CanBotMoveAutonomously(soldier) &&
                            TrySeekNearbyCover(ai, soldier))
                        {
                            plan.LockUnitCoverChange = true;
                        }
                        else if (!ai.IsMoving() && dist > 6.2f && AiCore.CanBotChangePoseAutonomously(soldier))
                        {
                            if (AiCore.TryGetSpottableCenter(ai.visibleTarget, out Vector3 dfTargetPos) &&
                                HasClearLineOfFireFromCrouch(soldier, dfTargetPos, dist))
                            {
                                SetBotPose(ai, soldier, SoldierPose.Crouch);
                            }
                        }
                    }
                    break;
            }
        }

        private static void ExecuteGoapSeekEmergencyCover(SoldierAI ai, Soldier soldier, Squad sq, AiCore.SoldierFastHandles shGoap, ref SquadBlackboard board, ref BotGoapPlan plan, GoapStateBits ws, float suppression, Vector3 myPos, float now)
        {
            bool onNarrowBridge = (ws & GoapStateBits.OnBridgeOrNarrow) != 0;
            bool inMultiSidedOrIndoors = IsPositionInMultiSidedOrIndoorShelter(myPos, out bool isIndoorRoofed, out _);
            if (TrySeekNearbyCover(ai, soldier))
            {
                plan.LockUnitCoverChange = true;
                if (soldier.IsOnCover())
                {
                    SoldierPose coverPose = (!onNarrowBridge && !ai.HasEnemyAndCanFireAtHim && (isIndoorRoofed || inMultiSidedOrIndoors || suppression >= 28f))
                        ? SoldierPose.Prone
                        : SoldierPose.Crouch;
                    SetBotPose(ai, soldier, coverPose);
                    if (plan.StepCount > 1)
                        plan.StepIndex = 1;
                }
                else
                {
                    Vector3 covPos = (soldier.HasDestinationAssigned && soldier.targetDestination != null)
                        ? soldier.targetDestination.GetCoverPosition()
                        : GetActualMoveEndpoint(ai, soldier);
                    float covDistSq = (covPos - myPos).sqrMagnitude;
                    plan.HasMoveOverride = covDistSq > 1.8f * 1.8f;
                    plan.MoveOverrideDest = covPos;
                    if (soldier.m_pose == SoldierPose.Prone && covDistSq > 1.8f * 1.8f)
                        SetBotPose(ai, soldier, SoldierPose.Crouch, true);
                    ApplyDesiredMovingPose(ai, soldier, covDistSq, suppression);
                }
            }
            else
            {
                // Open-field / indoor / trench fallback so SeekEmergencyCover is never a no-op!
                if (soldier.isBleeding && (ws & GoapStateBits.BandageUnsafeUnderFire) == 0)
                {
                    SetBotPose(ai, soldier, onNarrowBridge ? SoldierPose.Crouch : SoldierPose.Prone, true);
                    if (plan.StepCount > 1)
                        plan.StepIndex = 1;
                }
                else if (inMultiSidedOrIndoors && !onNarrowBridge)
                {
                    // Already inside a building or multi-sided trench/crater: stay sheltered inside and drop prone!
                    if (shGoap != null)
                        shGoap.Stance.LocalCoverProtectedUntil = now + 4.5f;
                    plan.LockUnitCoverChange = true;
                    SoldierPose shelterPose = !ai.HasEnemyAndCanFireAtHim ? SoldierPose.Prone : SoldierPose.Crouch;
                    SetBotPose(ai, soldier, shelterPose, true);
                    plan.PlanExpireTime = Mathf.Min(plan.PlanExpireTime, now + 2.4f);
                }
                else
                {
                    Vector3 openThreatDir = GetEffectiveThreatDirection(soldier, sq, myPos, board.ThreatDirection);
                    if (AiCore.CanBotMoveAutonomously(soldier) &&
                        !onNarrowBridge &&
                        TryComputeTacticalFallbackPosition(soldier, sq, myPos, myPos + openThreatDir * 20.0f, 8.5f, 14.0f, out Vector3 openScatterPos))
                    {
                        DestinationWithoutCover openDest = new DestinationWithoutCover(openScatterPos, SoldierPose.Crouch);
                        AiCore.AssignBotCoverOrDestination(ai, soldier, openDest.Cast<AiDestination>());
                        if (shGoap != null)
                            shGoap.Stance.LocalCoverProtectedUntil = now + 4.2f;
                        plan.LockUnitCoverChange = true;
                        plan.HasMoveOverride = true;
                        plan.MoveOverrideDest = openScatterPos;
                        float scatterSq = (openScatterPos - myPos).sqrMagnitude;
                        if (soldier.m_pose == SoldierPose.Prone && scatterSq > 1.8f * 1.8f)
                            SetBotPose(ai, soldier, SoldierPose.Crouch, true);
                        ApplyDesiredMovingPose(ai, soldier, scatterSq, suppression);
                        GoapPlansExecutedCount++;
                    }
                    else
                    {
                        SoldierPose dropPose = (!onNarrowBridge && suppression >= 25f) ? SoldierPose.Prone : SoldierPose.Crouch;
                        SetBotPose(ai, soldier, dropPose, true);
                        plan.PlanExpireTime = Mathf.Min(plan.PlanExpireTime, now + 1.8f);
                    }
                }
            }
        }

        private static void ExecuteGoapThrowSmokeScreen(SoldierAI ai, Soldier soldier, Squad sq, IntPtr ptr, AiCore.SoldierFastHandles shGoap, ref SquadBlackboard board, ref BotGoapPlan plan, GoapStateBits ws, float suppression, float dist, Vector3 myPos, float now)
        {
            bool smokeThrown = false;
            float nextSmkThrow = shGoap != null ? shGoap.Goap.NextSmokeGrenadeTime : 0f;
            if (soldier.inventory != null &&
                now > soldier.throw_end_time + 5.0f &&
                board.PrimaryTarget != null &&
                (now >= nextSmkThrow || nextSmkThrow - now > 60.0f) &&
                (now >= board.SmokeActiveUntil || board.SmokeActiveUntil - now > 60.0f || (board.ThreatPosition - board.LastSmokeTargetPos).sqrMagnitude > 18f * 18f))
            {
                VirtualItem smokeItem = soldier.inventory.FindItemOfType<VirtualSmokeGrenade>();
                if (smokeItem != null)
                {
                    float dToThreat = Vector3.Distance(myPos, board.ThreatPosition);
                    if (dToThreat >= 12f && dToThreat <= soldier.GetGrenadeThrowRange(smokeItem) + 6f &&
                        !AiCore.HasLowCeilingOverhead(myPos, 3.2f) &&
                        AiCore.TryConsumeBotRpcBudget(0.45f))
                    {
                        if (shGoap != null)
                            shGoap.Goap.NextSmokeGrenadeTime = now + 32.0f;
                        board.SmokeActiveUntil = now + 22.0f;
                        board.LastSmokeTargetPos = board.ThreatPosition;
                        soldier.Throw(smokeItem, board.PrimaryTarget);
                        board.Suppressor0Ptr = ptr;
                        board.SuppressHoldUntil = now + 6.5f;
                        GoapSmokeScreensCount++;
                        GoapPlansExecutedCount++;
                        smokeThrown = true;
                        TrySayTacticalVoice(soldier, sq, VoiceManager.VoiceClip.keepYourHeadDown, 0.1f);
                        if (VisionConfig.DebugLogging.Value)
                        {
                            AiCore.LogDebug(
                                "GoapSmoke",
                                $"{soldier.faction} {GetPersonality(soldier)} threw GOAP smoke screen ({dToThreat:F0}m, nextStep:{plan.Step1}).",
                                3.0f);
                        }
                    }
                }
            }

            // Advance to Step 1 when smoke was thrown OR when an allied smoke/pin already covers the push
            // (or on non-offensive fallback/evac steps); never send an unmasked soldier into unpinned fire if smoke failed!
            bool step1SafeWithoutNewSmoke =
                smokeThrown ||
                now < board.SmokeActiveUntil ||
                (ws & GoapStateBits.AllyPinningThreat) != 0 ||
                plan.Step1 == GoapActionId.TacticalFallbackRetreat ||
                plan.Step1 == GoapActionId.LoneWolfLinkUpOrAmbush;

            if (plan.StepCount > 1 && plan.StepIndex == 0 && plan.Step1 != GoapActionId.None && step1SafeWithoutNewSmoke)
            {
                plan.StepIndex = 1;
                if (!smokeThrown && plan.Step1 != GoapActionId.ThrowSmokeScreen)
                {
                    ExecuteGoapActionStep(ai, soldier, sq, ref board, ref plan, plan.Step1, ws, suppression, dist, now);
                }
            }
            else if (!smokeThrown)
            {
                plan.PlanExpireTime = 0f;
            }
        }

        private static void ExecuteGoapHoldOverwatchOrSuppress(SoldierAI ai, Soldier soldier, Squad sq, IntPtr ptr, AiCore.SoldierFastHandles shGoap, ref SquadBlackboard board, ref BotGoapPlan plan, GoapActionId action, float dist, Vector3 myPos, float now)
        {
            bool hasRealPinningFire = action == GoapActionId.HoldOverwatchFire
                ? (ai.HasEnemyAndCanFireAtHim && AiCore.GetRawVisibleTargetPtr(ai) != IntPtr.Zero)
                : (board.HasActiveThreat && CanDeliverSuppressiveFireTowardPoint(ai, soldier, board.ThreatPosition, board.PrimaryTarget, now));

            if (hasRealPinningFire)
            {
                if (board.Suppressor0Ptr == IntPtr.Zero || board.Suppressor0Ptr == ptr)
                    board.Suppressor0Ptr = ptr;
                else if (board.Suppressor1Ptr == IntPtr.Zero || board.Suppressor1Ptr == ptr)
                    board.Suppressor1Ptr = ptr;

                board.SuppressHoldUntil = Mathf.Max(board.SuppressHoldUntil, now + 3.2f);
            }
            else
            {
                // Prevent "phantom pinning": never broadcast AllyPinningThreat from behind a blind wall!
                if (board.Suppressor0Ptr == ptr)
                    board.Suppressor0Ptr = IntPtr.Zero;
                if (board.Suppressor1Ptr == ptr)
                    board.Suppressor1Ptr = IntPtr.Zero;
                plan.PlanExpireTime = Mathf.Min(plan.PlanExpireTime, now + 0.85f);
                return;
            }

            if (action == GoapActionId.HoldOverwatchFire &&
                (!ai.IsMoving() || IsInCommittedFiringHalt(ptr, now)) &&
                AiCore.TryGetSpottableCenter(ai.visibleTarget, out Vector3 owTargetPos))
            {
                bool crouchClear = HasClearLineOfFireFromCrouch(soldier, owTargetPos, dist);
                SetBotPose(ai, soldier, crouchClear ? SoldierPose.Crouch : SoldierPose.Idle, true);
                AiCore.SoldierFastHandles owHandles = shGoap ?? AiCore.GetOrCreateSoldierHandles(soldier);
                if (owHandles != null && now >= owHandles.Combat.NextBoundingHoldCountTime)
                {
                    owHandles.Combat.NextBoundingHoldCountTime = now + 5.0f;
                    GoapPlansExecutedCount++;
                    TrySayTacticalVoice(soldier, sq, VoiceManager.VoiceClip.coveringFire, 0.15f);
                }
            }
            else if (action == GoapActionId.SuppressThreatPosition && !ai.IsMoving() && shGoap != null)
            {
                if (soldier.m_pose == SoldierPose.Idle &&
                    !soldier.UsingBipod() &&
                    HasClearLineOfFireFromCrouch(soldier, board.ThreatPosition + Vector3.up * 0.45f, Vector3.Distance(myPos, board.ThreatPosition)))
                {
                    SetBotPose(ai, soldier, SoldierPose.Crouch);
                }
                if (now >= shGoap.Combat.NextBoundingHoldCountTime)
                {
                    shGoap.Combat.NextBoundingHoldCountTime = now + 5.0f;
                    GoapPlansExecutedCount++;
                    TrySayTacticalVoice(soldier, sq, VoiceManager.VoiceClip.coveringFire, 0.15f);
                }
            }
        }

        private static void ExecuteGoapMoveToFlankCover(SoldierAI ai, Soldier soldier, Squad sq, IntPtr ptr, AiCore.SoldierFastHandles shGoap, ref SquadBlackboard board, ref BotGoapPlan plan, float suppression, Vector3 myPos, float now)
        {
            CombatCover chosenCover = null;
            if (board.FlankLeftCover != null &&
                (board.FlankerLeftPtr == IntPtr.Zero || board.FlankerLeftPtr == ptr) &&
                (soldier.nonReachableCovers == null || !soldier.nonReachableCovers.Contains(board.FlankLeftCover.Cast<AiDestination>())))
            {
                board.FlankerLeftPtr = ptr;
                chosenCover = board.FlankLeftCover;
            }
            else if (board.FlankRightCover != null &&
                     (board.FlankerRightPtr == IntPtr.Zero || board.FlankerRightPtr == ptr) &&
                     (soldier.nonReachableCovers == null || !soldier.nonReachableCovers.Contains(board.FlankRightCover.Cast<AiDestination>())))
            {
                board.FlankerRightPtr = ptr;
                chosenCover = board.FlankRightCover;
            }

            if (chosenCover != null)
            {
                Vector3 chosenPos = chosenCover.GetCoverPosition();
                float flankDistSq = (chosenPos - myPos).sqrMagnitude;
                board.FlankUntil = Mathf.Max(board.FlankUntil, now + 6.5f);
                plan.LockUnitCoverChange = true;
                plan.PlanExpireTime = now + 6.5f;
                if (shGoap != null)
                    shGoap.Stance.LocalCoverProtectedUntil = now + 6.5f;

                if (soldier.targetDestination == null || soldier.targetDestination.Pointer != chosenCover.Pointer)
                {
                    if (shGoap == null || now >= shGoap.Goap.NextFlankCoverAssignTime)
                    {
                        if (shGoap != null)
                            shGoap.Goap.NextFlankCoverAssignTime = now + 5.5f;
                        AiCore.AssignBotCoverOrDestination(ai, soldier, chosenCover.Cast<AiDestination>());
                        plan.HasMoveOverride = flankDistSq > 1.8f * 1.8f;
                        plan.MoveOverrideDest = chosenPos;
                        if (soldier.m_pose == SoldierPose.Prone && flankDistSq > 1.8f * 1.8f)
                            SetBotPose(ai, soldier, SoldierPose.Crouch, true);
                        ApplyDesiredMovingPose(ai, soldier, flankDistSq, suppression);
                        plan.NextReplanTime = Mathf.Max(plan.NextReplanTime, now + 4.5f);
                        GoapPinAndFlankCount++;
                        GoapPlansExecutedCount++;
                        TrySayTacticalVoice(soldier, sq, VoiceManager.VoiceClip.imMoving, 0.25f);
                        if (VisionConfig.DebugLogging.Value)
                        {
                            AiCore.LogDebug("GoapPinFlank", $"{soldier.faction} {GetPersonality(soldier)} flanking pinned enemy to lateral cover (nextStep:{plan.Step1}).", 3.0f);
                        }
                    }
                    else
                    {
                        plan.PlanExpireTime = 0f;
                    }
                }
                else if (soldier.IsOnCover())
                {
                    if (plan.StepCount > 1)
                        plan.StepIndex = 1;
                }
                else
                {
                    plan.HasMoveOverride = flankDistSq > 1.8f * 1.8f;
                    plan.MoveOverrideDest = chosenPos;
                    if (soldier.m_pose == SoldierPose.Prone && flankDistSq > 1.8f * 1.8f)
                        SetBotPose(ai, soldier, SoldierPose.Crouch, true);
                    ApplyDesiredMovingPose(ai, soldier, flankDistSq, suppression);
                    if (!ai.IsMoving() && !ai.HasPath && !ai.HasPathRequest)
                    {
                        AiCore.ResetBotPathLockout(ai);
                        ai.MoveDestination = chosenPos;
                        ai.UpdatePath();
                    }
                }
            }
            else
            {
                plan.PlanExpireTime = 0f;
            }
        }

        private static void ExecuteGoapFlushThreatWithFrag(SoldierAI ai, Soldier soldier, IntPtr ptr, AiCore.SoldierFastHandles shGoap, ref SquadBlackboard board, ref BotGoapPlan plan, Vector3 myPos, float now)
        {
            bool threwFrag = false;
            SoldierPersonality fragPers = GetPersonality(soldier);
            bool isCombatGrenadier = fragPers == SoldierPersonality.Assaulter_Breacher ||
                                     fragPers == SoldierPersonality.Rifleman_Grenadier ||
                                     fragPers == SoldierPersonality.AT_Demolitionist;
            if (VisionConfig.EnableLastKnownSuppression.Value || isCombatGrenadier)
            {
                if (VisionConfig.EnableLastKnownSuppression.Value &&
                    TryFlushLastKnownEnemyWithGrenade(ai, soldier, allowWhileMoving: true))
                {
                    threwFrag = true;
                    GoapPlansExecutedCount++;
                }
                else if (shGoap != null &&
                         now > soldier.throw_end_time + 6.0f &&
                         now >= shGoap.Vision.NextSuppressiveGrenadeTime)
                {
                    Spottable fragTarget = (isCombatGrenadier && ai.visibleTarget != null && !ai.visibleTarget.IsWheeledVehicleOrTank())
                        ? ai.visibleTarget
                        : ((VisionConfig.EnableLastKnownSuppression.Value &&
                            !ai.HasEnemyAndCanFireAtHim &&
                            board.HasActiveThreat)
                            ? board.PrimaryTarget
                            : null);
                    if (fragTarget != null && fragTarget.Pointer != IntPtr.Zero)
                    {
                        Soldier targetSol = fragTarget.TryCast<Soldier>();
                        if (targetSol != null && targetSol.IsAlive && targetSol.faction != soldier.faction)
                        {
                            Vector3 tPos = targetSol.GetCenterOfUnit();
                            float dFrag = Vector3.Distance(myPos, tPos);
                            if (TryThrowTacticalFragGrenade(
                                    soldier,
                                    fragTarget,
                                    tPos,
                                    dFrag,
                                    isCombatGrenadier ? 9.0f : 10.0f,
                                    isCombatGrenadier ? 34.0f : 29.0f,
                                    isCombatGrenadier ? 9.0f : 11.5f,
                                    now,
                                    sayVoice: true,
                                    cooldownSeconds: isCombatGrenadier ? 10.0f : 14.0f))
                            {
                                threwFrag = true;
                                GoapPlansExecutedCount++;
                                if (VisionConfig.DebugLogging.Value)
                                {
                                    AiCore.LogDebug(
                                        "GoapFrag",
                                        $"{soldier.faction} {fragPers} lobbed GOAP frag grenade ({dFrag:F0}m).",
                                        3.0f);
                                }
                            }
                        }
                    }
                }
            }

            if (threwFrag)
            {
                board.BreacherPtr = ptr;
                board.BreachUntil = now + 4.0f;
            }
            else if (board.BreacherPtr == ptr)
            {
                board.BreacherPtr = IntPtr.Zero;
            }
            plan.PlanExpireTime = Mathf.Min(plan.PlanExpireTime, now + 0.85f);
        }

        private static void ExecuteGoapScavengeSupplyFromCorpse(SoldierAI ai, Soldier soldier, Squad sq, AiCore.SoldierFastHandles shGoap, ref BotGoapPlan plan, GoapStateBits ws, float suppression, Vector3 myPos, float now)
        {
            if (suppression >= 5.0f ||
                (ws & ScavengeForbiddenMask) != 0 ||
                (ws & GoapStateBits.NeedAmmoOrMedScavenge) == 0)
            {
                if (shGoap != null)
                {
                    shGoap.Goap.Scavenge = default;
                    AiCore.ClearBotMoveOverride(shGoap, AiCore.BotMoveOverrideKind.GoapAction);
                }
                if (ai.moveDirectlyToward_end > now)
                    ai.moveDirectlyToward_end = 0f;
                plan.LockUnitCoverChange = false;
                plan.HasMoveOverride = false;
                plan.PlanExpireTime = 0f;
                plan.NextReplanTime = now;
                if (!soldier.IsOnCover() && AiCore.CanBotMoveAutonomously(soldier))
                    TrySeekNearbyCover(ai, soldier);
                return;
            }

            // Reuse the BotScavengeCache populated by BuildSoldierWorldState (eliminates duplicate 50-line evaluation per tick)
            BotScavengeCache scavCache = shGoap != null ? shGoap.Goap.Scavenge : default;
            if (scavCache.TargetFloorItem != null ||
                scavCache.TargetRefillCrate != null ||
                scavCache.TargetInventory != null ||
                scavCache.TargetBuddyDonor != null ||
                scavCache.TargetNeedyAllyToFeed != null)
            {
                if ((scavCache.IsBuddyBandageRefill || scavCache.IsBuddyAmmoBorrow) &&
                    scavCache.TargetBuddyDonor != null &&
                    scavCache.TargetBuddyDonor.Pointer != IntPtr.Zero &&
                    scavCache.TargetBuddyDonor.IsAlive &&
                    scavCache.TargetBuddyDonor.transform != null)
                {
                    scavCache.TargetPosition = scavCache.TargetBuddyDonor.transform.position;
                }
                else if (scavCache.IsFeedingNeedyAlly &&
                         scavCache.TargetNeedyAllyToFeed != null &&
                         scavCache.TargetNeedyAllyToFeed.Pointer != IntPtr.Zero &&
                         scavCache.TargetNeedyAllyToFeed.IsAlive &&
                         scavCache.TargetNeedyAllyToFeed.transform != null)
                {
                    scavCache.TargetPosition = scavCache.TargetNeedyAllyToFeed.transform.position;
                }

                bool isBuddyHandoff = scavCache.IsBuddyBandageRefill ||
                                      scavCache.IsBuddyAmmoBorrow ||
                                      scavCache.IsFeedingNeedyAlly;
                bool outOfAmmo = scavCache.NeedAmmo;
                bool outOfBandage = scavCache.NeedBandage;
                Vector3 targetPos = scavCache.TargetPosition;
                float dSq = (targetPos - myPos).sqrMagnitude;
                float triggerRadius = isBuddyHandoff ? 3.8f : 3.3f;
                ApplyDesiredMovingPose(ai, soldier, dSq, suppression);
                if (dSq <= triggerRadius * triggerRadius)
                {
                    bool looted = false;
                    string lootedDesc = "supply";

                    // 0A. Buddy Bandage Refill from living friendly Medic
                    if (scavCache.IsBuddyBandageRefill &&
                        scavCache.TargetBuddyDonor != null &&
                        scavCache.TargetBuddyDonor.Pointer != IntPtr.Zero &&
                        scavCache.TargetBuddyDonor.IsAlive &&
                        !scavCache.TargetBuddyDonor.IsInjured)
                    {
                        if (!Photon.Pun.PhotonNetwork.InRoom || AiCore.TryConsumeBotRpcBudget(0.25f))
                        {
                            soldier.RefillBandages();
                            if (shGoap != null)
                                shGoap.Goap.NextBuddyBandageRefillTime = now + 45.0f;
                            BuddyResuppliesCount++;
                            looted = true;
                            lootedDesc = "buddy bandage from medic";
                            TrySayTacticalVoice(soldier, sq, VoiceManager.VoiceClip.thankYou, 0.25f);
                        }
                    }
                    // 0B. Buddy Ammo Borrow from living friendly Ammo Carrier / Support rifleman
                    else if (scavCache.IsBuddyAmmoBorrow &&
                             scavCache.TargetBuddyDonor != null &&
                             scavCache.TargetBuddyDonor.Pointer != IntPtr.Zero &&
                             scavCache.TargetBuddyDonor.IsAlive &&
                             !scavCache.TargetBuddyDonor.IsInjured)
                    {
                        if (!Photon.Pun.PhotonNetwork.InRoom || AiCore.TryConsumeBotRpcBudget(0.30f))
                        {
                            soldier.inventory?.RemoveEmptyAmmoAndMagazines();
                            Soldier donor = scavCache.TargetBuddyDonor;
                            donor.BorrowAmmos(soldier);
                            // Creature.BorrowAmmos (0x180A51A60) already calls RefillAmmoForWearedWeapon(0, true, ...)
                            // when donor has matching ammo. Only fall back to RefillSoldierPrimaryAndStowedWeapons if
                            // no async refill is pending, preventing duplicate Slot-0 refills (4 mags instead of 2).
                            if (!soldier.IsAmmoRefillPending())
                            {
                                RefillSoldierPrimaryAndStowedWeapons(soldier, 2);
                            }
                            else
                            {
                                ScheduleBotInventoryWeightAudit(soldier, 0.70f);
                            }
                            if (shGoap != null)
                                shGoap.Goap.NextBuddyAmmoResupplyTime = now + 35.0f;
                            BuddyResuppliesCount++;
                            looted = true;
                            lootedDesc = "buddy ammo from carrier";
                            TrySayTacticalVoice(soldier, sq, VoiceManager.VoiceClip.thankYou, 0.25f);
                        }
                    }
                    // 0C. Support / Ammo Carrier proactively feeding a dry MG gunner or AT specialist
                    else if (scavCache.IsFeedingNeedyAlly &&
                             scavCache.TargetNeedyAllyToFeed != null &&
                             scavCache.TargetNeedyAllyToFeed.Pointer != IntPtr.Zero &&
                             scavCache.TargetNeedyAllyToFeed.IsAlive &&
                             !scavCache.TargetNeedyAllyToFeed.IsInjured)
                    {
                        if (!Photon.Pun.PhotonNetwork.InRoom || AiCore.TryConsumeBotRpcBudget(0.30f))
                        {
                            Soldier ally = scavCache.TargetNeedyAllyToFeed;
                            ally.inventory?.RemoveEmptyAmmoAndMagazines();
                            soldier.BorrowAmmos(ally);
                            if (AiCore.IsLocallyOwnedBot(ally))
                            {
                                if (!ally.IsAmmoRefillPending())
                                {
                                    RefillSoldierPrimaryAndStowedWeapons(ally, 2);
                                }
                                else
                                {
                                    ScheduleBotInventoryWeightAudit(ally, 0.70f);
                                }
                            }
                            if (shGoap != null)
                                shGoap.Goap.NextBuddyAmmoResupplyTime = now + 35.0f;
                            AiCore.SoldierFastHandles allyH = AiCore.GetOrCreateSoldierHandles(ally);
                            if (allyH != null)
                                allyH.Goap.NextBuddyAmmoResupplyTime = now + 35.0f;
                            BuddyResuppliesCount++;
                            looted = true;
                            lootedDesc = "fed ammo to needy MG/AT squadmate";
                        }
                    }
                    // 0D. Placed Map AmmoRefillCrate (trench / bunker / strongpoint resupply crate)
                    else if (scavCache.TargetRefillCrate != null &&
                             scavCache.TargetRefillCrate.Pointer != IntPtr.Zero &&
                             scavCache.TargetRefillCrate.gameObject != null &&
                             scavCache.TargetRefillCrate.gameObject.activeInHierarchy)
                    {
                        AmmoRefillCrate crate = scavCache.TargetRefillCrate;
                        CachedBoxOverrideInfo ov = GetCachedBoxOverrideInfo(
                            crate.Pointer,
                            crate.overrideContent,
                            crate.allowTakeWeaponIntoInventory);
                        looted = TryResupplyFromBoxOrCrate(
                            soldier,
                            crate.overrideContent,
                            crate.allowTakeWeaponIntoInventory,
                            ov,
                            outOfAmmo,
                            scavCache.NeedFragGrenade,
                            scavCache.NeedSmokeGrenade,
                            scavCache.IsAtWeaponPickup,
                            outOfBandage,
                            now,
                            ref plan,
                            out lootedDesc);
                    }
                    // 1. Pick up physical floor items (dropped AT launchers, primary rifles/SMGs/MGs, helmets, radios, DroppableAmmoBox, grenades, magazines, syringes)
                    else if (IsFloorItemFreeToPickUp(scavCache.TargetFloorItem))
                    {
                        DroppableAmmoBox floorBox = scavCache.IsDroppableAmmoBox
                            ? scavCache.TargetFloorItem.TryCast<DroppableAmmoBox>()
                            : null;
                        GenericGun floorGun = floorBox == null ? scavCache.TargetFloorItem.TryCast<GenericGun>() : null;

                        if (floorBox != null)
                        {
                            // Resupply from floor-dropped ammo box without locking out the player's 30s UI prompt or deleting the box!
                            CachedBoxOverrideInfo ov = GetCachedBoxOverrideInfo(floorBox.Pointer, floorBox.overrideContent, true);
                            looted = TryResupplyFromBoxOrCrate(
                                soldier,
                                floorBox.overrideContent,
                                true,
                                ov,
                                outOfAmmo,
                                scavCache.NeedFragGrenade,
                                scavCache.NeedSmokeGrenade,
                                scavCache.IsAtWeaponPickup,
                                outOfBandage,
                                now,
                                ref plan,
                                out lootedDesc);
                        }
                        else if (floorGun != null)
                        {
                            bool canSwitchWeaponNow = !soldier.IsPlayer() &&
                                                      !soldier.isInHandsAnimation &&
                                                      !soldier.isInBodyAnimation &&
                                                      soldier.CanSwitchHeldItem &&
                                                      (now >= _nextGlobalBotWeaponPickupTime || now + 5.0f < _nextGlobalBotWeaponPickupTime);

                            if (canSwitchWeaponNow && (!Photon.Pun.PhotonNetwork.InRoom || AiCore.TryConsumeBotRpcBudget(0.35f)))
                            {
                                int slot = floorGun.IsAtWeapon() ? 1 : 0;
                                soldier.SetGunTriggerPressed(false);
                                soldier.PickUpItem(scavCache.TargetFloorItem.gameObject, slot);
                                ScheduleDelayedPickedWeaponRefill(soldier, slot, now);
                                _nextGlobalBotWeaponPickupTime = now + 2.20f;
                                if (shGoap != null)
                                    shGoap.Goap.NextWeaponUpgradeAllowedTime = now + (scavCache.IsRoleWeaponUpgrade ? 90.0f : 20.0f);
                                plan.NextReplanTime = Mathf.Max(plan.NextReplanTime, now + 2.50f);
                                looted = true;
                                if (scavCache.IsRoleWeaponUpgrade)
                                {
                                    RoleWeaponUpgradesCount++;
                                    lootedDesc = $"upgraded primary ({floorGun.name})";
                                }
                                else
                                {
                                    lootedDesc = floorGun.IsAtWeapon() ? "dropped AT weapon" : "dropped firearm";
                                }
                            }
                            else
                            {
                                if (shGoap != null)
                                    shGoap.Goap.NextWeaponUpgradeAllowedTime = now + 8.0f;
                            }
                        }
                        else if ((scavCache.IsHelmetPickup || scavCache.IsRadioPackPickup) && soldier.inventory != null)
                        {
                            if (!Photon.Pun.PhotonNetwork.InRoom || AiCore.TryConsumeBotRpcBudget(0.35f))
                            {
                                VirtualItem addedWearable = soldier.inventory.AddItemToInventory(scavCache.TargetFloorItem);
                                if (addedWearable != null)
                                {
                                    ResourcesManager.SynchDestroy(scavCache.TargetFloorItem.gameObject, true);
                                    soldier.SetWerable(addedWearable, true);
                                    looted = true;
                                    if (scavCache.IsRadioPackPickup)
                                    {
                                        FieldRadiosInheritedCount++;
                                        if (sq != null && sq.Pointer != IntPtr.Zero)
                                            _nextSquadRadioScavengeTime[sq.Pointer] = now + 25.0f;
                                        lootedDesc = "floor radio pack";
                                    }
                                    else
                                    {
                                        HelmetsRecoveredCount++;
                                        lootedDesc = "helmet from ground";
                                        if (IsFriendlyPlayerWithinDistance(soldier, myPos, 7.5f))
                                        {
                                            TrySayTacticalVoice(soldier, sq, VoiceManager.VoiceClip.thankYou, 0.05f);
                                        }
                                    }
                                }
                            }
                        }
                        else if (soldier.inventory != null &&
                                 (scavCache.IsMedicSyringePickup ||
                                  scavCache.IsRecoverLifePickup ||
                                  (outOfBandage && soldier.isBleeding) ||
                                  CanSoldierAcceptExtraMass(soldier, scavCache.TargetFloorItem.mass, 0.15f)))
                        {
                            if (scavCache.IsRecoverLifePickup)
                            {
                                if ((!Photon.Pun.PhotonNetwork.InRoom || AiCore.TryConsumeBotRpcBudget(0.30f)) &&
                                    soldier.inventory.AddItemToInventoryAndDestroyInstance(scavCache.TargetFloorItem))
                                {
                                    VirtualRecoverLife vRec = soldier.inventory.FindItemOfType<VirtualRecoverLife>()?.TryCast<VirtualRecoverLife>();
                                    if (vRec != null)
                                        soldier.RecoverLife(vRec);
                                    ScheduleBotInventoryWeightAudit(soldier, 0.50f);
                                    looted = true;
                                    lootedDesc = "field rations/med tin";
                                }
                            }
                            else
                            {
                                bool pickedFloorAmmoOrMag = scavCache.TargetFloorItem.TryCast<AmmoItem>() != null ||
                                                            scavCache.TargetFloorItem.TryCast<Magazine>() != null;
                                if (soldier.inventory.AddItemToInventoryAndDestroyInstance(scavCache.TargetFloorItem))
                                {
                                    GenericGun g0Now = soldier.GetHeldGun(0);
                                    if (outOfAmmo && !pickedFloorAmmoOrMag && g0Now != null && soldier.IsOutOfAmmosFor(g0Now))
                                        RefillSoldierPrimaryAndStowedWeapons(soldier, 1);
                                    ScheduleBotInventoryWeightAudit(soldier, 0.50f);
                                    if (scavCache.IsMedicSyringePickup &&
                                        CountUniqueSyringesInInventory(soldier.inventory, out _, out _, out _) >= 3)
                                    {
                                        soldier.hasSyringe = true;
                                    }
                                    looted = true;
                                    if (scavCache.IsAtWeaponPickup)
                                        lootedDesc = "floor AT grenade";
                                    else if (scavCache.IsAtMinePickup)
                                        lootedDesc = "floor AT mine";
                                    else if (scavCache.IsDeployableTripodPickup)
                                        lootedDesc = "deployable tripod";
                                    else if (scavCache.IsShellAmmoPickup)
                                        lootedDesc = "mortar/cannon shell";
                                    else if (scavCache.IsToolboxPickup)
                                        lootedDesc = "repair toolbox";
                                    else if (scavCache.NeedFragGrenade)
                                        lootedDesc = "floor frag grenade";
                                    else if (scavCache.NeedSmokeGrenade)
                                        lootedDesc = "floor smoke grenade";
                                    else if (scavCache.IsMedicSyringePickup)
                                        lootedDesc = "fallen medic syringe";
                                    else
                                        lootedDesc = "floor ammo/bandage";
                                }
                            }
                        }
                    }
                    // 2. Scavenge from verified dead corpse inventory or supply crate
                    else if (IsInventoryValidCorpseOrCrate(
                                 scavCache.TargetInventory,
                                 soldier.faction,
                                 outOfAmmo || scavCache.NeedFragGrenade || scavCache.NeedSmokeGrenade,
                                 outOfBandage || scavCache.IsMedicSyringePickup,
                                 out _,
                                 out bool ownerStillDeadOrCrate))
                    {
                        InventoryManager targetInv = scavCache.TargetInventory;
                        bool canSwitchWeaponNow = ownerStillDeadOrCrate &&
                                                  !soldier.IsPlayer() &&
                                                  !soldier.isInHandsAnimation &&
                                                  !soldier.isInBodyAnimation &&
                                                  soldier.CanSwitchHeldItem &&
                                                  (now >= _nextGlobalBotWeaponPickupTime || now + 5.0f < _nextGlobalBotWeaponPickupTime);

                        if ((scavCache.IsRadioPackPickup || scavCache.IsHelmetPickup) &&
                            ownerStillDeadOrCrate &&
                            soldier.inventory != null &&
                            scavCache.TargetInventoryWearable != null &&
                            targetInv.Contains(scavCache.TargetInventoryWearable))
                        {
                            if (!Photon.Pun.PhotonNetwork.InRoom || AiCore.TryConsumeBotRpcBudget(0.35f))
                            {
                                VirtualItem wearable = scavCache.TargetInventoryWearable;
                                // InventoryManager.TakeIntoInventory (0x1807E50E0) always returns false (xor al, al):
                                // only VirtualHelmet (+0x113..+0x1BF) auto-calls soldier.SetWerable(copy, true), whereas
                                // VirtualClothing (radio packs!) jumps to +0x1C1 (AddVirtualItem) unworn and MUST be
                                // explicitly marked as worn via soldier.SetWerable(myRadio, true)!
                                soldier.inventory.TakeIntoInventory(wearable, targetInv);
                                if (!targetInv.Contains(wearable))
                                {
                                    looted = true;
                                    if (scavCache.IsRadioPackPickup)
                                    {
                                        VirtualItem myRadio = soldier.inventory.Contains(wearable)
                                            ? wearable
                                            : (soldier.inventory.inventory != null && !string.IsNullOrEmpty(wearable.item_id)
                                                ? soldier.inventory.inventory.FindItemWithID(wearable.item_id)
                                                : wearable);
                                        if (myRadio != null)
                                        {
                                            soldier.SetWerable(myRadio, true);
                                        }

                                        FieldRadiosInheritedCount++;
                                        if (sq != null && sq.Pointer != IntPtr.Zero)
                                            _nextSquadRadioScavengeTime[sq.Pointer] = now + 25.0f;
                                        lootedDesc = scavCache.IsEnemyRadioCapture
                                            ? "captured enemy field radio (retuned)"
                                            : "field radio pack from fallen comrade";
                                    }
                                    else
                                    {
                                        if (!soldier.hasHeadgear && soldier.inventory.Contains(wearable))
                                        {
                                            soldier.SetWerable(wearable, true);
                                        }

                                        HelmetsRecoveredCount++;
                                        lootedDesc = "helmet from fallen comrade";
                                    }
                                }
                            }
                        }
                        else if (scavCache.IsAtWeaponPickup && ownerStillDeadOrCrate)
                        {
                            if (scavCache.TargetInventoryAtWeapon != null && targetInv.Contains(scavCache.TargetInventoryAtWeapon))
                            {
                                if (canSwitchWeaponNow && (!Photon.Pun.PhotonNetwork.InRoom || AiCore.TryConsumeBotRpcBudget(0.35f)))
                                {
                                    soldier.SetGunTriggerPressed(false);
                                    soldier.PickUpItemFromInventory(scavCache.TargetInventoryAtWeapon, targetInv, 1);
                                    ScheduleDelayedPickedWeaponRefill(soldier, 1, now);
                                    _nextGlobalBotWeaponPickupTime = now + 2.20f;
                                    if (shGoap != null)
                                        shGoap.Goap.NextWeaponUpgradeAllowedTime = now + 20.0f;
                                    plan.NextReplanTime = Mathf.Max(plan.NextReplanTime, now + 2.50f);
                                    looted = true;
                                    lootedDesc = "AT launcher from body/crate";
                                }
                                else
                                {
                                    if (shGoap != null)
                                        shGoap.Goap.NextWeaponUpgradeAllowedTime = now + 8.0f;
                                }
                            }
                            else if (soldier.inventory != null)
                            {
                                VirtualItem atGren = targetInv.FindItemOfType<VirtualATGrenade>();
                                if (atGren != null &&
                                    !AiCore.IsPressureMineItem(atGren, out _) &&
                                    CanSoldierAcceptExtraMass(soldier, atGren.GetMass(), 0.20f))
                                {
                                    soldier.inventory.TakeIntoInventory(atGren, targetInv);
                                    if (!targetInv.Contains(atGren))
                                    {
                                        looted = true;
                                        lootedDesc = "AT grenade from body/crate";
                                    }
                                }
                            }
                        }
                        else if (scavCache.IsRoleWeaponUpgrade &&
                                 scavCache.TargetInventoryPrimaryWeapon != null &&
                                 targetInv.Contains(scavCache.TargetInventoryPrimaryWeapon))
                        {
                            if (canSwitchWeaponNow && (!Photon.Pun.PhotonNetwork.InRoom || AiCore.TryConsumeBotRpcBudget(0.35f)))
                            {
                                soldier.SetGunTriggerPressed(false);
                                soldier.PickUpItemFromInventory(scavCache.TargetInventoryPrimaryWeapon, targetInv, 0);
                                ScheduleDelayedPickedWeaponRefill(soldier, 0, now);
                                _nextGlobalBotWeaponPickupTime = now + 2.20f;
                                if (shGoap != null)
                                    shGoap.Goap.NextWeaponUpgradeAllowedTime = now + 90.0f;
                                plan.NextReplanTime = Mathf.Max(plan.NextReplanTime, now + 2.50f);
                                RoleWeaponUpgradesCount++;
                                looted = true;
                                lootedDesc = "role-matched primary from body/crate";
                            }
                            else
                            {
                                if (shGoap != null)
                                    shGoap.Goap.NextWeaponUpgradeAllowedTime = now + 8.0f;
                            }
                        }

                        if (scavCache.NeedFragGrenade &&
                            soldier.inventory != null &&
                            AiCore.FindThrowableGrenadeInInventory(soldier, findAtGrenade: false, findFragGrenade: true) == null)
                        {
                            VirtualItem fg = targetInv.FindItemOfType<VirtualGrenade>();
                            if (fg != null &&
                                !AiCore.IsPressureMineItem(fg, out _) &&
                                CanSoldierAcceptExtraMass(soldier, fg.GetMass(), 0.20f))
                            {
                                soldier.inventory.TakeIntoInventory(fg, targetInv);
                                if (!targetInv.Contains(fg))
                                {
                                    looted = true;
                                    if (lootedDesc == "supply")
                                        lootedDesc = "frag grenade from body/crate";
                                }
                            }
                        }

                        if (scavCache.NeedSmokeGrenade &&
                            soldier.inventory != null &&
                            soldier.inventory.FindItemOfType<VirtualSmokeGrenade>() == null)
                        {
                            VirtualItem sg = targetInv.FindItemOfType<VirtualSmokeGrenade>();
                            if (sg != null && CanSoldierAcceptExtraMass(soldier, sg.GetMass(), 0.20f))
                            {
                                soldier.inventory.TakeIntoInventory(sg, targetInv);
                                if (!targetInv.Contains(sg))
                                {
                                    looted = true;
                                    if (lootedDesc == "supply")
                                        lootedDesc = "smoke grenade from body/crate";
                                }
                            }
                        }

                        if (scavCache.IsMedicSyringePickup &&
                            soldier.inventory != null &&
                            !IsFriendlyPlayerWithinDistance(soldier, scavCache.TargetPosition, 25.0f))
                        {
                            if (TryTransferFullMedicKitAtomically(soldier, targetInv))
                            {
                                looted = true;
                                if (lootedDesc == "supply")
                                    lootedDesc = "complete fallen medic kit (3/3)";
                            }
                        }

                        if (outOfBandage && soldier.inventory != null)
                        {
                            VirtualItem bnd = targetInv.FindItemOfType<VirtualBandages>();
                            if (bnd != null &&
                                CanSoldierAcceptExtraMass(soldier, bnd.GetMass(), soldier.isBleeding ? 0.0f : 0.20f))
                            {
                                soldier.inventory.TakeIntoInventory(bnd, targetInv);
                                if (soldier.inventory.FindItemOfType<VirtualBandages>() != null)
                                {
                                    looted = true;
                                    if (lootedDesc == "supply")
                                        lootedDesc = "bandage";
                                }
                            }
                        }

                        if (outOfAmmo && !scavCache.IsRoleWeaponUpgrade)
                        {
                            RefillSoldierPrimaryAndStowedWeapons(soldier, 2);
                            looted = true;
                            if (lootedDesc == "supply")
                                lootedDesc = "ammo";
                        }

                        if (scavCache.IsAtMinePickup && soldier.inventory != null && !HasPressureAtMineInInventory(soldier))
                        {
                            VirtualItem candMine = targetInv.FindItemOfType<VirtualATGrenade>();
                            if (candMine != null &&
                                AiCore.IsPressureMineItem(candMine, out bool atFlag) &&
                                atFlag &&
                                CanSoldierAcceptExtraMass(soldier, candMine.GetMass(), 0.20f))
                            {
                                soldier.inventory.TakeIntoInventory(candMine, targetInv);
                                if (!targetInv.Contains(candMine))
                                {
                                    looted = true;
                                    if (lootedDesc == "supply") lootedDesc = "AT mine from chest/corpse";
                                }
                            }
                        }

                        if (scavCache.IsDeployableTripodPickup && soldier.inventory != null &&
                            soldier.inventory.FindItemOfType<VirtualDeployableVehicle>() == null)
                        {
                            VirtualItem tripod = targetInv.FindItemOfType<VirtualDeployableVehicle>();
                            if (tripod != null && CanSoldierAcceptExtraMass(soldier, tripod.GetMass(), 0.15f))
                            {
                                soldier.inventory.TakeIntoInventory(tripod, targetInv);
                                if (!targetInv.Contains(tripod))
                                {
                                    looted = true;
                                    if (lootedDesc == "supply") lootedDesc = "deployable tripod from chest/corpse";
                                }
                            }
                        }

                        if (scavCache.IsShellAmmoPickup && soldier.inventory != null)
                        {
                            VirtualItem shell = targetInv.FindItemOfType<VirtualShellAmmo>();
                            if (shell != null && CanSoldierAcceptExtraMass(soldier, shell.GetMass(), 0.20f))
                            {
                                soldier.inventory.TakeIntoInventory(shell, targetInv);
                                if (!targetInv.Contains(shell))
                                {
                                    looted = true;
                                    if (lootedDesc == "supply") lootedDesc = "shell from chest/corpse";
                                }
                            }
                        }

                        if (scavCache.IsToolboxPickup && soldier.inventory != null && soldier.GetRepairTools() == null)
                        {
                            VirtualItem tb = targetInv.FindItemOfType<VirtualToolbox>();
                            if (tb != null && CanSoldierAcceptExtraMass(soldier, tb.GetMass(), 0.10f))
                            {
                                soldier.inventory.TakeIntoInventory(tb, targetInv);
                                if (!targetInv.Contains(tb))
                                {
                                    looted = true;
                                    if (lootedDesc == "supply") lootedDesc = "toolbox from chest/corpse";
                                }
                            }
                        }

                        if (scavCache.IsRecoverLifePickup && soldier.inventory != null &&
                            (!Photon.Pun.PhotonNetwork.InRoom || AiCore.TryConsumeBotRpcBudget(0.30f)))
                        {
                            VirtualItem recVi = targetInv.FindItemOfType<VirtualRecoverLife>();
                            if (recVi != null && CanSoldierAcceptExtraMass(soldier, recVi.GetMass(), 0.0f))
                            {
                                soldier.inventory.TakeIntoInventory(recVi, targetInv);
                                VirtualRecoverLife myRec = soldier.inventory.FindItemOfType<VirtualRecoverLife>()?.TryCast<VirtualRecoverLife>();
                                if (myRec != null)
                                {
                                    soldier.RecoverLife(myRec);
                                    looted = true;
                                    if (lootedDesc == "supply") lootedDesc = "rations/med tin from chest/corpse";
                                }
                            }
                        }
                    }

                    if (shGoap != null)
                        shGoap.Goap.Scavenge = default;
                    if (looted)
                    {
                        if (ai.moveDirectlyToward_end > now)
                            ai.moveDirectlyToward_end = 0f;
                        GoapPlansExecutedCount++;
                        if (plan.StepCount > 1 && plan.StepIndex == 0)
                            plan.StepIndex = 1;
                        if (!soldier.IsOnCover() &&
                            AiCore.CanBotMoveAutonomously(soldier) &&
                            (soldier.targetDestination == null || (soldier.targetDestination.GetCoverPosition() - myPos).sqrMagnitude > 5.5f * 5.5f))
                        {
                            TrySeekNearbyCover(ai, soldier);
                        }
                        if (VisionConfig.DebugLogging.Value)
                        {
                            AiCore.LogDebug(
                                "GoapScavenge",
                                $"{soldier.faction} {GetPersonality(soldier)} scavenged {lootedDesc}.",
                                3.0f);
                        }
                    }
                }
                else
                {
                    plan.LockUnitCoverChange = true;
                    plan.HasMoveOverride = true;
                    plan.MoveOverrideDest = targetPos;
                    // Stop at 2.35m arm's-length handoff distance for living buddies (1.15m for floor/corpse items)
                    // using overlap-safe duration / CPathFinding so bots never body-hug or drop into 0.5x slow-walk!
                    float standoff = isBuddyHandoff ? 2.35f : 1.15f;
                    AiCore.MoveBotTowardPosition(ai, soldier, targetPos, standoff, 2.8f);
                }
            }
        }

        private static void ExecuteGoapLoneWolfLinkUpOrAmbush(SoldierAI ai, Soldier soldier, Squad sq, IntPtr ptr, AiCore.SoldierFastHandles shGoap, ref SquadBlackboard board, ref BotGoapPlan plan, GoapStateBits ws, float suppression, float dist, Vector3 myPos, float now)
        {
            // Bailed-out Pilots & Tank/Vehicle Crews follow a 3-step hierarchy:
            // 1. Remount a nearby operable friendly crew vehicle with a free seat (e.g., just repaired tank!)
            // 2. If unarmed/pistol-only or squad on retreat, fall back to friendly rear spawn
            // 3. Otherwise, if armed with a primary firearm, link up and join the nearest friendly infantry squad!
            SoldierPersonality lonePers = GetPersonality(soldier);
            bool isBailedCrewOrPilot = AiCore.IsConfirmedPlaneCrewSquad(sq) ||
                                       AiCore.IsTrueVehicleCrewSquad(sq) ||
                                       IsPilotOrVehicleCrewPersonality(lonePers);
            if (isBailedCrewOrPilot)
            {
                if (TryRemountNearbyOperableCrewVehicle(ai, soldier, sq, myPos, 28f))
                {
                    plan.LockUnitCoverChange = true;
                    ApplyDesiredMovingPose(ai, soldier, (GetActualMoveEndpoint(ai, soldier) - myPos).sqrMagnitude, suppression);
                    return;
                }

                if (shGoap != null && now < shGoap.Goap.BailoutShockUntil)
                {
                    if (AiCore.IsSoldierInVehicleOrTransition(soldier))
                        return;

                    SetBotPose(ai, soldier, suppression >= 45f ? SoldierPose.Prone : SoldierPose.Crouch, true);
                    Vector3 toWreck = myPos - shGoap.Goap.BailoutWreckPos;
                    toWreck.y = 0f;
                    if (toWreck.sqrMagnitude < 6.5f * 6.5f && !ai.IsMoving())
                    {
                        Vector3 awayDir;
                        if (toWreck.sqrMagnitude > 0.25f)
                        {
                            awayDir = toWreck.normalized;
                        }
                        else
                        {
                            Vector3 fallbackAway = board.HasActiveThreat ? (myPos - board.ThreatPosition) : soldier.transform.forward;
                            fallbackAway.y = 0f;
                            awayDir = fallbackAway.sqrMagnitude > 0.04f ? fallbackAway.normalized : soldier.transform.forward;
                        }
                        float jitterDeg = ((AiCore.GetDeterministicHash(ptr, 53) & 15) - 7) * 6.0f;
                        Vector3 lat = Quaternion.Euler(0f, jitterDeg, 0f) * awayDir;
                        Vector3 scramblePos = shGoap.Goap.BailoutWreckPos + lat * 6.5f;
                        float terrY = ResourcesManager.GetTerrainAltitude(scramblePos, myPos.y);
                        scramblePos.y = Mathf.Abs(terrY - myPos.y) <= 4.0f ? terrY : myPos.y;
                        AiCore.SetBotMoveDestinationAndRefreshPath(ai, scramblePos, 0.25f);
                        plan.HasMoveOverride = true;
                        plan.MoveOverrideDest = scramblePos;
                    }
                    plan.LockUnitCoverChange = true;
                    plan.IsEvacuatingBailedCrew = true;
                    return;
                }

                if (ShouldBailedCrewRetreatOverJoinSquad(soldier, sq, myPos))
                {
                    if (TryRetreatBailedCrewTowardFriendlySpawn(ai, soldier, sq, myPos, out Vector3 spawnOverride, out bool hasSpawnOverride))
                    {
                        plan.LockUnitCoverChange = true;
                        plan.HasMoveOverride = hasSpawnOverride;
                        plan.MoveOverrideDest = spawnOverride;
                        plan.IsEvacuatingBailedCrew = hasSpawnOverride;
                        if (ai.HasEnemyAndCanFireAtHim && dist > 6.2f && !ai.IsMoving())
                            SetBotPose(ai, soldier, SoldierPose.Crouch);
                        else
                            ApplyDesiredMovingPose(ai, soldier, (spawnOverride - myPos).sqrMagnitude, suppression);
                        return;
                    }
                }

                if (TryLinkUpOrJoinFriendlySquad(ai, soldier, myPos, 140f, out Vector3 linkDest1))
                {
                    DetachStaleFootCoverForEvacuation(soldier);
                    plan.LockUnitCoverChange = true;
                    plan.HasMoveOverride = true;
                    plan.MoveOverrideDest = linkDest1;
                    plan.IsEvacuatingBailedCrew = true;
                    if (ai.HasEnemyAndCanFireAtHim && dist > 6.2f && !ai.IsMoving())
                        SetBotPose(ai, soldier, SoldierPose.Crouch);
                    else
                        ApplyDesiredMovingPose(ai, soldier, (linkDest1 - myPos).sqrMagnitude, suppression);
                    return;
                }

                if (TryRetreatBailedCrewTowardFriendlySpawn(ai, soldier, sq, myPos, out Vector3 fallbackSpawnOverride, out bool hasFallbackSpawn))
                {
                    plan.LockUnitCoverChange = true;
                    plan.HasMoveOverride = hasFallbackSpawn;
                    plan.MoveOverrideDest = fallbackSpawnOverride;
                    plan.IsEvacuatingBailedCrew = hasFallbackSpawn;
                    if (ai.HasEnemyAndCanFireAtHim && dist > 6.2f && !ai.IsMoving())
                        SetBotPose(ai, soldier, SoldierPose.Crouch);
                    else
                        ApplyDesiredMovingPose(ai, soldier, (fallbackSpawnOverride - myPos).sqrMagnitude, suppression);
                    return;
                }
            }

            bool preferAmbush = !isBailedCrewOrPilot &&
                                (ws & GoapStateBits.OnBridgeOrNarrow) == 0 &&
                                GetFactionCommandProfile(soldier.faction).PreferAmbushWhenAlone;
            if (preferAmbush)
            {
                if (!soldier.IsOnCover() && TrySeekNearbyCover(ai, soldier))
                {
                    plan.LockUnitCoverChange = true;
                    ApplyDesiredMovingPose(ai, soldier, (GetActualMoveEndpoint(ai, soldier) - myPos).sqrMagnitude, suppression);
                }
                else if (soldier.IsOnCover())
                {
                    SetBotPose(ai, soldier, SoldierPose.Crouch);
                }
                else if (TryLinkUpOrJoinFriendlySquad(ai, soldier, myPos, 55f, out Vector3 linkDest2))
                {
                    plan.LockUnitCoverChange = true;
                    plan.HasMoveOverride = true;
                    plan.MoveOverrideDest = linkDest2;
                    ApplyDesiredMovingPose(ai, soldier, (linkDest2 - myPos).sqrMagnitude, suppression);
                }
            }
            else
            {
                if (TryLinkUpOrJoinFriendlySquad(ai, soldier, myPos, 75f, out Vector3 linkDest3))
                {
                    plan.LockUnitCoverChange = true;
                    plan.HasMoveOverride = true;
                    plan.MoveOverrideDest = linkDest3;
                    ApplyDesiredMovingPose(ai, soldier, (linkDest3 - myPos).sqrMagnitude, suppression);
                }
                else if (!soldier.IsOnCover() && TrySeekNearbyCover(ai, soldier))
                {
                    plan.LockUnitCoverChange = true;
                    ApplyDesiredMovingPose(ai, soldier, (GetActualMoveEndpoint(ai, soldier) - myPos).sqrMagnitude, suppression);
                }
            }
        }

        private static void ExecuteGoapTacticalFallbackRetreat(SoldierAI ai, Soldier soldier, Squad sq, AiCore.SoldierFastHandles shGoap, ref SquadBlackboard board, ref BotGoapPlan plan, float dist, Vector3 myPos, float now)
        {
            bool hasActiveDest = shGoap != null &&
                                 shGoap.MoveOverride.Kind == AiCore.BotMoveOverrideKind.EmergencyFallback &&
                                 now < shGoap.MoveOverride.ExpireTime;
            float fbEnd = hasActiveDest ? shGoap.MoveOverride.ExpireTime : 0f;
            Vector3 fbDest = hasActiveDest ? shGoap.MoveOverride.Dest : Vector3.zero;

            if (!hasActiveDest)
            {
                if (shGoap != null && now < shGoap.Goap.NextIndividualFallbackTime)
                {
                    AiCore.ClearBotMoveOverride(shGoap, AiCore.BotMoveOverrideKind.EmergencyFallback);
                    plan.PlanExpireTime = 0f;
                    plan.NextReplanTime = now;
                    return;
                }

                Vector3 threatRef = board.HasActiveThreat
                    ? board.ThreatPosition
                    : (myPos + (soldier.transform != null ? soldier.transform.forward * 16.0f : Vector3.forward * 16.0f));
                if (ai.visibleTarget != null && AiCore.TryGetSpottableCenter(ai.visibleTarget, out Vector3 visThreat))
                    threatRef = visThreat;

                if (!TryComputeTacticalFallbackPosition(soldier, sq, myPos, threatRef, 22.0f, 36.0f, out fbDest))
                {
                    if (shGoap != null)
                        shGoap.Goap.NextIndividualFallbackTime = now + 18.0f;
                    plan.PlanExpireTime = 0f;
                    plan.NextReplanTime = now;
                    return;
                }

                fbEnd = now + 8.5f;
                if (shGoap != null)
                {
                    AiCore.SetBotMoveOverride(shGoap, AiCore.BotMoveOverrideKind.EmergencyFallback, fbDest, fbEnd);
                    shGoap.Goap.NextIndividualFallbackTime = now + 38.0f;
                }
                TacticalRetreatsCount++;
                GoapPlansExecutedCount++;
                TrySayTacticalVoice(soldier, sq, VoiceManager.VoiceClip.timeToRetreat, 0.08f);
                BotActionTrace.RecordEvent(soldier, BotTraceEventKind.FallbackRetreat, "goap_break_contact", dist);

                if (VisionConfig.DebugLogging.Value)
                {
                    AiCore.LogDebug(
                        "IndivFallback",
                        $"{soldier.faction} {GetPersonality(soldier)} executing break-contact fallback ({Vector3.Distance(myPos, fbDest):F0}m rearward).",
                        3.0f);
                }
            }

            float remSq = (fbDest - myPos).sqrMagnitude;
            if (remSq <= 3.2f * 3.2f)
            {
                AiCore.ClearBotMoveOverride(shGoap, AiCore.BotMoveOverrideKind.EmergencyFallback);
                SetBotPose(ai, soldier, SoldierPose.Crouch);
                plan.PlanExpireTime = 0f;
                plan.NextReplanTime = now;
            }
            else
            {
                plan.LockUnitCoverChange = true;
                plan.PlanExpireTime = Mathf.Min(fbEnd, Mathf.Max(plan.PlanExpireTime, now + 4.5f));
                if (shGoap != null)
                    shGoap.Stance.LocalCoverProtectedUntil = now + 4.5f;
                plan.HasMoveOverride = true;
                plan.MoveOverrideDest = fbDest;
                if (ai.HasEnemyAndCanFireAtHim && dist > 6.2f && !ai.IsMoving())
                    SetBotPose(ai, soldier, SoldierPose.Crouch);
                else
                    SetBotPose(ai, soldier, remSq <= 5.0f * 5.0f ? SoldierPose.Crouch : SoldierPose.Idle, remSq > 5.0f * 5.0f);
                AiCore.MoveBotTowardPosition(ai, soldier, fbDest, 2.2f, 3.0f);
            }
        }

        private static bool TryExecuteCloseAcousticInvestigate(
            SoldierAI ai,
            Soldier soldier,
            AiCore.SoldierFastHandles h,
            Squad sq,
            ref SquadBlackboard board,
            GoapStateBits ws,
            float suppression,
            SoldierPersonality personality,
            float now)
        {
            const GoapStateBits AcousticBusyMask =
                GoapStateBits.IsBleeding |
                GoapStateBits.IsSuppressed |
                GoapStateBits.IsHeavilySuppressed |
                GoapStateBits.NearFreshCasualty |
                GoapStateBits.HasVisibleEnemy |
                GoapStateBits.CanFireAtVisibleEnemy |
                GoapStateBits.InPointBlankRange |
                GoapStateBits.ArmorThreatClose |
                GoapStateBits.ExposedSideOfWall |
                GoapStateBits.OverwhelmedNeedFallback |
                GoapStateBits.RadioLinkDrifting;

            if ((ws & AcousticBusyMask) != 0 ||
                ai == null ||
                soldier == null ||
                soldier.transform == null ||
                sq == null ||
                sq.Pointer == IntPtr.Zero ||
                ai.HasEnemyAndCanFireAtHim ||
                soldier.isBleeding ||
                suppression >= 15f ||
                soldier.IsRadioman() ||
                soldier.hasSyringe ||
                soldier.IsSquadLeader() ||
                soldier.UsingBipod() ||
                soldier.IsBipodTransitioning() ||
                sq.IsPlayerInSquad() ||
                IsSoldierBailedOutVehicleCrew(soldier, sq) ||
                IsPilotOrVehicleCrewPersonality(personality) ||
                IsActivelyHeadingToEmplacement(soldier, now) ||
                !AiCore.CanBotMoveAutonomously(soldier))
            {
                return false;
            }

            if (soldier.IsOnCover() && h != null && now < h.Stance.CoverSettledUntil)
                return false;

            if (h != null && h.MoveOverride.Kind != AiCore.BotMoveOverrideKind.None && now < h.MoveOverride.ExpireTime)
                return false;

            if (board.AcousticInvestigatorPtr != IntPtr.Zero &&
                board.AcousticInvestigatorPtr != soldier.Pointer &&
                now < board.AcousticInvestigateUntil)
            {
                return false;
            }

            Vector3 myPos = soldier.transform.position;
            string fac = AiCore.GetCachedFaction(soldier);
            if (!VisionPatches.TryGetNearestCloseAcousticThreat(myPos, fac, now, out Vector3 soundPos, out float soundDist, out _))
                return false;

            if (soundDist <= 3.5f || soundDist > 20.0f)
                return false;

            Vector3 toSound = soundPos - myPos;
            toSound.y = 0f;
            float flatMag = toSound.magnitude;
            if (flatMag <= 3.5f)
                return false;

            Vector3 stepDir = toSound / flatMag;
            float peekDist = Mathf.Max(1.0f, flatMag - 3.5f);
            Vector3 peekPos = myPos + stepDir * peekDist;
            float terrY = ResourcesManager.GetTerrainAltitude(peekPos, myPos.y);
            peekPos.y = Mathf.Abs(terrY - myPos.y) <= 4.0f ? terrY : myPos.y;

            board.AcousticInvestigatorPtr = soldier.Pointer;
            board.AcousticInvestigateUntil = now + 6.0f;

            DestinationWithoutCover peekDest = new DestinationWithoutCover(peekPos, SoldierPose.Crouch);
            if (h != null)
            {
                h.Stance.LocalCoverProtectedUntil = now + 5.5f;
                AiCore.SetBotMoveOverride(h, AiCore.BotMoveOverrideKind.GoapAction, peekPos, now + 4.5f);
            }

            AiCore.AssignBotCoverOrDestination(ai, soldier, peekDest.Cast<AiDestination>());
            SetBotPose(ai, soldier, SoldierPose.Crouch, true);
            soldier.SetAlertedFor(5.0f);
            return true;
        }

        private static bool IsMovementGoapAction(GoapActionId id)
        {
            switch (id)
            {
                case GoapActionId.SeekEmergencyCover:
                case GoapActionId.RepositionBehindWall:
                case GoapActionId.MaintainRadioLinkWithLeader:
                case GoapActionId.MoveToFlankCover:
                case GoapActionId.BoundWithOverwatch:
                case GoapActionId.PointBlankBayonetBreach:
                case GoapActionId.RescueSurrenderedComrade:
                case GoapActionId.LoneWolfLinkUpOrAmbush:
                case GoapActionId.ScavengeSupplyFromCorpse:
                case GoapActionId.TacticalFallbackRetreat:
                    return true;
                default:
                    return false;
            }
        }

        internal static bool IsSquadOnRetreatTask(Squad sq)
        {
            if (sq == null || sq.currentTask == null)
                return false;

            if (sq.currentTask.TryCast<MissionTaskRetreat>() != null)
                return true;

            // Support Dynamic Lua Retreat Finales (SHARED/phase_4.lua on Budapest, Cassino, Shanghai, etc.)
            MissionTaskScripted mTask = sq.currentTask.TryCast<MissionTaskScripted>();
            if (mTask != null && mTask.customTextDefenders == "#retreat")
            {
                Soldier leader = sq.Leader;
                BattleData bd = BattleManager.GetCurrentBattleData();
                if (leader != null && bd != null && !string.IsNullOrEmpty(leader.faction) && !bd.IsInvaderFaction(leader.faction))
                    return true;
            }
            return false;
        }

        private static bool IsPilotOrVehicleCrewPersonality(SoldierPersonality p)
        {
            return p == SoldierPersonality.Pilot_Ace ||
                   p == SoldierPersonality.Pilot_Daredevil ||
                   p == SoldierPersonality.Pilot_Disciplined ||
                   p == SoldierPersonality.TankCrew_Brawler ||
                   p == SoldierPersonality.TankCrew_Sniper ||
                   p == SoldierPersonality.TankCrew_Support ||
                   p == SoldierPersonality.LightCrew_Outrider ||
                   p == SoldierPersonality.LightCrew_Escort ||
                   p == SoldierPersonality.LightCrew_FlakWatch;
        }

        private static void DetachStaleFootCoverForEvacuation(Soldier soldier)
        {
            if (soldier == null || soldier.Pointer == IntPtr.Zero)
                return;

            if (AiCore.TryGetSoldierHandles(soldier.Pointer, out var evacH))
                evacH.Stance.LocalCoverProtectedUntil = 0f;
            if (!soldier.HasDestinationAssigned)
                return;

            AiDestination td = soldier.targetDestination;
            if (td != null &&
                (td.TryCast<Vehicle>() != null ||
                 td.TryCast<VehicleRepairTask>() != null ||
                 td.TryCast<MedicHealingDestination>() != null))
            {
                return;
            }

            soldier.CoverPosition(null);
        }

        private static bool TryRetreatBailedCrewTowardFriendlySpawn(
            SoldierAI ai,
            Soldier soldier,
            Squad sq,
            Vector3 myPos,
            out Vector3 rawMoveDest,
            out bool hasRawMoveDest)
        {
            rawMoveDest = Vector3.zero;
            hasRawMoveDest = false;
            if (ai == null || soldier == null || string.IsNullOrEmpty(soldier.faction))
                return false;

            try
            {
                bool isPlaneCrew = sq != null && AiCore.IsConfirmedPlaneCrewSquad(sq);
                Vector3 baseDest;
                bool alreadyRetreating = IsSquadOnRetreatTask(sq);
                if (alreadyRetreating)
                {
                    baseDest = sq.currentTask.GetTaskDestination();
                    float groundY = ResourcesManager.GetTerrainAltitude(baseDest, 0f);
                    if (Mathf.Abs(baseDest.y - groundY) > 3.5f && groundY >= 0.05f)
                    {
                        baseDest.y = groundY;
                    }
                }
                else
                {
                    if (!AiCore.TryComputeCrewRetreatDestination(soldier.faction, myPos, out baseDest, allowWaterSurfaceFallback: isPlaneCrew))
                        return false;

                    // Replace any null OR stale frontline objective with MissionTaskRetreat so SquadLeaderRoutine
                    // never re-orders bailed tankers or grounded aircrews forward toward an enemy objective!
                    if (sq != null && AiCore.IsTrueVehicleCrewSquad(sq))
                    {
                        AssignCrewSquadRetreatTask(sq, soldier.faction, myPos, allowWaterSurface: isPlaneCrew, replaceExistingIfFar: false);
                        if (sq.currentTask != null && sq.currentTask.TryCast<MissionTaskRetreat>() != null)
                        {
                            baseDest = sq.currentTask.GetTaskDestination();
                        }
                    }
                }

                // Deterministic 1.8m-4.0m personal offset well inside MissionTaskRetreat's 15m despawn sphere
                // prevents 3-4 crewmen from body-stacking while ensuring every survivor triggers Soldier.OnRetreat!
                int h = AiCore.GetDeterministicHash(soldier.Pointer, 733);
                float angRad = ((h & 0xFF) * (1f / 255f)) * (Mathf.PI * 2f);
                float rad = 1.8f + (((h >> 8) & 0xFF) * (1f / 255f)) * 2.2f;
                Vector3 spreadDest = new Vector3(
                    baseDest.x + Mathf.Cos(angRad) * rad,
                    baseDest.y,
                    baseDest.z + Mathf.Sin(angRad) * rad);
                float spreadAlt = ResourcesManager.GetTerrainAltitude(spreadDest, 0f);
                if (spreadAlt >= 0.05f && Mathf.Abs(spreadAlt - baseDest.y) <= 4.0f)
                {
                    spreadDest.y = spreadAlt;
                }
                else if (baseDest.y <= 0.05f && spreadAlt < 0.02f)
                {
                    // Water-surface rescue sphere: keep Y at 0f so swimming AI pilots trigger the 15m Euclidean check!
                    spreadDest.y = 0.0f;
                }
                else
                {
                    spreadDest = baseDest;
                }

                DetachStaleFootCoverForEvacuation(soldier);
                rawMoveDest = spreadDest;
                hasRawMoveDest = true;
                AiCore.MoveBotTowardPosition(ai, soldier, spreadDest, 0f, 3.2f);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void TryAdvanceBoundTowardThreat(
            SoldierAI ai,
            Soldier soldier,
            Squad sq,
            Vector3 myPos,
            Vector3 threatPos,
            Vector3 threatDir,
            float now,
            ref BotGoapPlan plan)
        {
            AiCore.SoldierFastHandles covH = AiCore.GetOrCreateSoldierHandles(soldier);
            if (now < covH.Stance.NextLocalCoverSeekTime)
                return;

            covH.Stance.NextLocalCoverSeekTime = now + 3.0f;

            bool isOnBridge = VisionConfig.EnableBridgeCrossingDiscipline.Value &&
                              AiCore.IsOnBridgeOrNarrowCrossing(myPos);

            // Walking-Pace Friendly Tank Escort: if a friendly tank nearby (<= 17.5m) is rolling forward
            // toward the threat, advance in its sheltered rear-quarter wake using its hull as mobile armor cover!
            if (TryFindTankHullCoverOrEscortSpot(soldier, myPos, threatDir, true, out Vector3 tankSpot, out bool isMovingEscort) &&
                isMovingEscort &&
                (!isOnBridge || AiCore.IsSafeBridgeDeckStep(myPos, tankSpot, out tankSpot)))
            {
                SetBotPose(ai, soldier, SoldierPose.Crouch);
                AiCore.MoveBotTowardPosition(ai, soldier, tankSpot, 0f, 1.2f);
                plan.LockUnitCoverChange = true;
                plan.PlanExpireTime = now + 3.8f;
                plan.NextReplanTime = Mathf.Max(plan.NextReplanTime, now + 2.4f);
                covH.Stance.LocalCoverProtectedUntil = now + 3.8f;
                TankHullCoverUsesCount++;
                GoapPlansExecutedCount++;
                return;
            }

            var octree = CoverManager.staticCovers;
            CombatCover bestAdvanceCover = null;
            if (octree != null)
            {
                Vector3 searchCenter = myPos + threatDir * 12.0f;
                var nearby = octree.GetNearby(searchCenter, 14.0f);
                if (nearby != null && nearby.Length > 0)
                {
                    float myDistToThreatSq = (threatPos - myPos).sqrMagnitude;
                    var unreachable = soldier.nonReachableCovers;
                    float bestScore = -999f;

                    int count = nearby.Length;
                    for (int i = 0; i < count; i++)
                    {
                        AiDestination dest = nearby[i];
                        if (dest == null || dest.Pointer == IntPtr.Zero)
                            continue;
                        if (unreachable != null && unreachable.Contains(dest))
                            continue;

                        CombatCover cc = dest.TryCast<CombatCover>();
                        if (cc == null || cc.destroyed)
                            continue;

                        Vector3 covPos = cc.GetCoverPosition();
                        float dFromMeSq = (covPos - myPos).sqrMagnitude;
                        if (dFromMeSq < 5.5f * 5.5f || dFromMeSq > 22.0f * 22.0f)
                            continue;

                        float maxBoundDeltaY = isOnBridge ? 1.15f : Mathf.Max(2.8f, Mathf.Sqrt(dFromMeSq) * 0.48f);
                        if (Mathf.Abs(covPos.y - myPos.y) > maxBoundDeltaY)
                            continue;

                        float covDistToThreatSq = (threatPos - covPos).sqrMagnitude;
                        if (covDistToThreatSq >= myDistToThreatSq - 25.0f || covDistToThreatSq < 8.0f * 8.0f)
                            continue;

                        float advanceGain = Vector3.Dot(covPos - myPos, threatDir);
                        if (advanceGain < 4.5f)
                            continue;

                        float score = advanceGain - Mathf.Sqrt(dFromMeSq) * 0.25f;
                        if (score <= bestScore)
                            continue;

                        if (isOnBridge && !AiCore.IsSafeBridgeDeckStep(myPos, covPos, out _))
                            continue;

                        if (!cc.IsCoverAvailable(threatDir, AiCore.GetCachedFaction(soldier)))
                            continue;

                        bestScore = score;
                        bestAdvanceCover = cc;
                    }
                }
            }

            if (bestAdvanceCover != null)
            {
                Vector3 advPos = bestAdvanceCover.GetCoverPosition();
                float advDistSq = (advPos - myPos).sqrMagnitude;
                AiCore.AssignBotCoverOrDestination(ai, soldier, bestAdvanceCover.Cast<AiDestination>());
                if (soldier.m_pose == SoldierPose.Prone && advDistSq > 1.8f * 1.8f)
                    SetBotPose(ai, soldier, SoldierPose.Crouch, true);
                ApplyDesiredMovingPose(ai, soldier, advDistSq, soldier.GetSuppressionValue());
                plan.LockUnitCoverChange = true;
                plan.HasMoveOverride = advDistSq > 1.8f * 1.8f;
                plan.MoveOverrideDest = advPos;
                plan.PlanExpireTime = now + 5.5f;
                plan.NextReplanTime = Mathf.Max(plan.NextReplanTime, now + 3.6f);
                covH.Stance.LocalCoverProtectedUntil = now + 5.5f;
                GoapPlansExecutedCount++;
                TrySayTacticalVoice(soldier, sq, VoiceManager.VoiceClip.imMoving, 0.20f);
                return;
            }

            // Bridge Parapet-to-Parapet Forward Bound when staticCovers nodes aren't baked along the bridge span
            if (isOnBridge)
            {
                Vector3 probeAhead = myPos + threatDir * 10.5f;
                if (AiCore.IsSafeBridgeDeckStep(myPos, probeAhead, out Vector3 safeAheadDeck))
                {
                    Vector3 bridgeBoundSpot = safeAheadDeck;
                    if (TryFindBridgeParapetOrPillarSpot(soldier, safeAheadDeck, threatDir, out Vector3 aheadParapet) &&
                        AiCore.IsSafeBridgeDeckStep(myPos, aheadParapet, out Vector3 verifiedParapet))
                    {
                        bridgeBoundSpot = verifiedParapet;
                    }

                    float bridgeDistSq = (bridgeBoundSpot - myPos).sqrMagnitude;
                    if (bridgeDistSq >= 4.5f * 4.5f &&
                        Vector3.Dot(bridgeBoundSpot - myPos, threatDir) >= 3.5f)
                    {
                        DestinationWithoutCover bridgeBoundDest = new DestinationWithoutCover(bridgeBoundSpot, SoldierPose.Crouch);
                        AiCore.AssignBotCoverOrDestination(ai, soldier, bridgeBoundDest.Cast<AiDestination>());
                        if (soldier.m_pose == SoldierPose.Prone && bridgeDistSq > 1.8f * 1.8f)
                            SetBotPose(ai, soldier, SoldierPose.Crouch, true);
                        ApplyDesiredMovingPose(ai, soldier, bridgeDistSq, soldier.GetSuppressionValue());
                        plan.LockUnitCoverChange = true;
                        plan.HasMoveOverride = true;
                        plan.MoveOverrideDest = bridgeBoundSpot;
                        plan.PlanExpireTime = now + 4.8f;
                        plan.NextReplanTime = Mathf.Max(plan.NextReplanTime, now + 3.2f);
                        covH.Stance.LocalCoverProtectedUntil = now + 4.8f;
                        GoapPlansExecutedCount++;
                        TrySayTacticalVoice(soldier, sq, VoiceManager.VoiceClip.imMoving, 0.20f);
                        return;
                    }
                }

                plan.PlanExpireTime = Mathf.Min(plan.PlanExpireTime, now + 0.85f);
                return;
            }

            // Open-field / hillside forward bound fallback when CoverManager.staticCovers has no cover node 5.5-22m ahead:
            // bound 9.5-12.5m forward along a staggered diagonal lane instead of freezing in old rear cover!
            Vector3 flatFwd = new Vector3(threatDir.x, 0f, threatDir.z);
            if (flatFwd.sqrMagnitude > 0.01f)
            {
                flatFwd.Normalize();
                Vector3 flatRight = new Vector3(flatFwd.z, 0f, -flatFwd.x);
                int bHash = AiCore.GetDeterministicHash(soldier.Pointer, 409);
                float latSign = ((bHash & 1) == 0) ? 1f : -1f;
                float boundLen = 9.5f + ((bHash >> 4) & 7) * 0.42f;
                bool hasWater = AiCore.TryGetActiveWaterLevel(out float waterY);
                Vector3 waist = myPos + Vector3.up * 0.55f;

                for (int attempt = 0; attempt < 2; attempt++)
                {
                    float s = attempt == 0 ? latSign : -latSign;
                    Vector3 bDir = (flatFwd * 0.94f + flatRight * (0.34f * s)).normalized;
                    if (AiCore.IsMovementBlockedByObstacle(waist, bDir, 2.2f))
                        continue;

                    Vector3 rawCand = myPos + bDir * boundLen;
                    // +/-19.0m vertical window covers full 56.6 deg (normal.y >= 0.55, 12.44 * tan(56.6) = 18.88m)
                    // TerrainCollider climbs/descents AND catches tall hollow 3-story roofs from above while capping non-terrain at +/-2.0m!
                    if (!AiCore.TrySnapWalkableGroundProbe(myPos, rawCand, 19.0f, 38.0f, 0.55f, 2.0f, hasWater, waterY, out Vector3 openBoundPos))
                        continue;
                    if ((threatPos - openBoundPos).sqrMagnitude < 9.0f * 9.0f)
                        continue;
                    if (AiCore.TryGetNearbyKnownMine(openBoundPos, 4.0f, false, out _))
                        continue;

                    float openDistSq = (openBoundPos - myPos).sqrMagnitude;
                    DestinationWithoutCover openBoundDest = new DestinationWithoutCover(openBoundPos, SoldierPose.Crouch);
                    AiCore.AssignBotCoverOrDestination(ai, soldier, openBoundDest.Cast<AiDestination>());
                    if (soldier.m_pose == SoldierPose.Prone && openDistSq > 1.8f * 1.8f)
                        SetBotPose(ai, soldier, SoldierPose.Crouch, true);
                    ApplyDesiredMovingPose(ai, soldier, openDistSq, soldier.GetSuppressionValue());
                    plan.LockUnitCoverChange = true;
                    plan.HasMoveOverride = true;
                    plan.MoveOverrideDest = openBoundPos;
                    plan.PlanExpireTime = now + 4.6f;
                    plan.NextReplanTime = Mathf.Max(plan.NextReplanTime, now + 3.0f);
                    covH.Stance.LocalCoverProtectedUntil = now + 4.6f;
                    GoapPlansExecutedCount++;
                    TrySayTacticalVoice(soldier, sq, VoiceManager.VoiceClip.imMoving, 0.20f);
                    return;
                }
            }

            plan.PlanExpireTime = Mathf.Min(plan.PlanExpireTime, now + 0.85f);
        }

        private static void HandleActiveCasualtyCarryTick(SoldierAI ai, Soldier soldier, IntPtr ptr, float now)
        {
            if (soldier == null)
                return;

            Vector3 myPos = soldier.transform != null ? soldier.transform.position : Vector3.zero;
            AiCore.SoldierFastHandles dragH = AiCore.GetOrCreateSoldierHandles(soldier);
            CasualtyDragState activeDrag = dragH != null ? dragH.Goap.CasualtyDrag : default;
            bool hasState = activeDrag.DragEndTime > 0f || activeDrag.Patient != null;
            Soldier carried = soldier.carryData != null ? soldier.carryData.carriedSoldier : activeDrag.Patient;

            bool stalled = false;
            if (hasState)
            {
                if (activeDrag.StallAnchorTime <= 0f || (myPos - activeDrag.StallAnchorPos).sqrMagnitude > 0.75f * 0.75f)
                {
                    activeDrag.StallAnchorPos = myPos;
                    activeDrag.StallAnchorTime = now;
                }
                else if ((now - activeDrag.StallAnchorTime) >= 2.2f)
                {
                    stalled = true;
                }
            }

            bool nearbyMedicOrCloseEnemy = false;
            string myFaction = AiCore.GetCachedFaction(soldier);
            if (!string.IsNullOrEmpty(myFaction))
            {
                AiCore.EnsureWorldSnapshot();
                var soldiers = AiCore.WorldSoldiers;
                int count = AiCore.WorldSoldierCount;
                for (int i = 0; i < count; i++)
                {
                    ref AiCore.SoldierSnapshot sSnap = ref soldiers[i];
                    if (sSnap.Ptr == ptr || sSnap.HasSurrendered || sSnap.IsInjured || sSnap.IsOnVehicle)
                        continue;

                    float dSq = (sSnap.Pos - myPos).sqrMagnitude;
                    if (AiCore.AreSameSideFactions(myFaction, sSnap.Faction))
                    {
                        bool isMedic = sSnap.IsMedic || (sSnap.IsPlayer && sSnap.Ref != null && sSnap.Ref.hasSyringe);
                        if (isMedic && dSq <= 11.0f * 11.0f)
                        {
                            nearbyMedicOrCloseEnemy = true;
                            break;
                        }
                    }
                    else if (AiCore.AreEnemyFactions(myFaction, sSnap.Faction) && dSq <= 16.0f * 16.0f)
                    {
                        nearbyMedicOrCloseEnemy = true;
                        break;
                    }
                }
            }

            bool shouldDrop = !VisionConfig.EnableBuddyCasualtyDragging.Value ||
                              !hasState ||
                              carried == null ||
                              carried.IsDead ||
                              !carried.IsInjured ||
                              now >= activeDrag.DragEndTime ||
                              (myPos - activeDrag.DropSpot).sqrMagnitude <= 2.6f * 2.6f ||
                              stalled ||
                              nearbyMedicOrCloseEnemy ||
                              soldier.isBleeding ||
                              soldier.IsInjured ||
                              (ai != null && (ai.HasEnemyAndCanFireAtHim || ai.visibleTarget != null)) ||
                              soldier.GetSuppressionValue() >= 18.0f;

            if (shouldDrop)
            {
                // Unconditional release: never skip CarryBodySynched(null) due to RPC budget contention,
                // otherwise the patient remains locked in pat.IsCarried() and medics cannot heal them!
                soldier.CarryBodySynched(null);
                if (dragH != null)
                {
                    dragH.Goap.CasualtyDrag = new CasualtyDragState { NextScanTime = activeDrag.NextScanTime };
                    AiCore.ClearBotMoveOverride(dragH, AiCore.BotMoveOverrideKind.CasualtyDrag);
                }
                if (ai != null && soldier.transform != null)
                {
                    Vector3 clearStep = myPos - soldier.transform.forward * 2.4f;
                    AiCore.MoveBotTowardPosition(ai, soldier, clearStep, 0.8f, 1.8f);
                }
                return;
            }

            if (dragH != null)
                dragH.Goap.CasualtyDrag = activeDrag;
            if (ai != null)
            {
                SetBotPose(ai, soldier, SoldierPose.Crouch);
                AiCore.MoveBotTowardPosition(ai, soldier, activeDrag.DropSpot, 1.4f, 2.6f);
            }
        }

        private static bool TryExecuteBuddyCasualtyDrag(
            SoldierAI ai,
            Soldier soldier,
            Squad sq,
            ref SquadBlackboard board,
            Vector3 myPos,
            float now)
        {
            if (ai == null || soldier == null)
                return false;

            IntPtr ptr = soldier.Pointer;

            // 1. If already carrying a casualty, drag them to the sheltered drop spot or release immediately for Medics
            if (soldier.IsCarryingBody())
            {
                HandleActiveCasualtyCarryTick(ai, soldier, ptr, now);
                return true;
            }

            if (!VisionConfig.EnableBuddyCasualtyDragging.Value || sq == null || sq.units == null)
                return false;

            if (soldier.isBleeding || soldier.IsSquadLeader() || soldier.IsRadioman() || soldier.IsMedic() || soldier.carryData != null)
                return false;

            SoldierPersonality pers = GetPersonality(soldier);
            if (AiCore.IsMedicSoldierOrPersonality(null, pers))
                return false;

            FactionCommandProfile factionProfile = GetFactionCommandProfile(soldier.faction);
            float comradeMult = Mathf.Max(0.55f, factionProfile.ComradeAidAndDragMult);
            float maxDragSupp = now < board.SmokeActiveUntil
                ? Mathf.Clamp(22f / comradeMult, 14f, 26f)
                : Mathf.Clamp(10f / comradeMult, 7f, 14f);
            if (soldier.GetSuppressionValue() >= maxDragSupp || ai.HasEnemyAndCanFireAtHim || ai.visibleTarget != null)
                return false;

            // Prevent multiple squadmates from simultaneously walking over to drag a casualty in the same squad
            if (now < board.DragClaimUntil && board.DragRescuerPtr != IntPtr.Zero && board.DragRescuerPtr != ptr)
                return false;

            AiCore.SoldierFastHandles dragH = AiCore.GetOrCreateSoldierHandles(soldier);
            CasualtyDragState dragState = dragH != null ? dragH.Goap.CasualtyDrag : default;
            if (now < dragState.NextScanTime)
                return false;

            dragState.NextScanTime = now + 3.2f;
            if (dragH != null)
                dragH.Goap.CasualtyDrag = dragState;

            if (!board.HasActiveThreat)
                return false;

            int uCount = sq.units.Count;
            Soldier bestPatient = null;
            Vector3 bestPatientPos = Vector3.zero;
            float maxDragRadius = Mathf.Clamp(9.5f / comradeMult, 7.0f, 12.5f);
            float bestDistSq = maxDragRadius * maxDragRadius;
            Soldier squadMedic = null;
            Vector3 squadMedicPos = Vector3.zero;

            for (int i = 0; i < uCount; i++)
            {
                Soldier u = sq.units[i];
                if (u == null || !u.IsAlive || u.transform == null)
                    continue;
                if (u.IsMedic() && !u.IsInjured)
                {
                    squadMedic = u;
                    squadMedicPos = u.transform.position;
                    break;
                }
            }

            for (int i = 0; i < uCount; i++)
            {
                Soldier cand = sq.units[i];
                if (cand == null || cand.Pointer == ptr || cand.IsDead || !cand.IsInjured || cand.IsOnVehicle())
                    continue;
                if (cand.carryData != null || cand.IsOnCover() || cand.transform == null)
                    continue;

                Vector3 cPos = cand.transform.position;
                if (Mathf.Abs(cPos.y - myPos.y) > 2.2f)
                    continue;

                // Skip if a squad medic is already right next to the casualty
                if (squadMedic != null && (squadMedicPos - cPos).sqrMagnitude <= 6.0f * 6.0f)
                    continue;

                float dSq = (cPos - myPos).sqrMagnitude;
                if (dSq < bestDistSq)
                {
                    bestDistSq = dSq;
                    bestPatient = cand;
                    bestPatientPos = cPos;
                }
            }

            if (bestPatient == null)
                return false;

            board.DragRescuerPtr = ptr;
            board.DragPatientPtr = bestPatient.Pointer;
            board.DragClaimUntil = now + 4.5f;

            Vector3 dropSpot;
            if (squadMedic != null && (squadMedicPos - bestPatientPos).sqrMagnitude <= 14.0f * 14.0f)
            {
                dropSpot = squadMedicPos - board.ThreatDirection * 1.8f;
            }
            else if (!TryFindShelteredSideOfWall(bestPatientPos, board.ThreatDirection, soldier.transform.root, out dropSpot))
            {
                dropSpot = bestPatientPos - board.ThreatDirection * 5.5f;
            }

            if (bestDistSq <= 2.35f * 2.35f)
            {
                if (!Photon.Pun.PhotonNetwork.InRoom || AiCore.TryConsumeBotRpcBudget(0.40f))
                {
                    soldier.SetGunTriggerPressed(false);
                    soldier.CarryBodySynched(bestPatient);
                    dragState.Patient = bestPatient;
                    dragState.DropSpot = dropSpot;
                    dragState.DragEndTime = now + 7.0f;
                    dragState.NextScanTime = now + 18.0f;
                    dragState.StallAnchorPos = myPos;
                    dragState.StallAnchorTime = now;
                    if (dragH != null)
                    {
                        dragH.Goap.CasualtyDrag = dragState;
                        AiCore.SetBotMoveOverride(dragH, AiCore.BotMoveOverrideKind.CasualtyDrag, dropSpot, dragState.DragEndTime);
                    }
                    CasualtiesDraggedCount++;
                    GoapPlansExecutedCount++;
                    TrySayTacticalVoice(soldier, sq, VoiceManager.VoiceClip.imMoving, 0.25f);
                    if (VisionConfig.DebugLogging.Value)
                    {
                        AiCore.LogDebug(
                            "CasualtyDrag",
                            $"{soldier.faction} {pers} dragging wounded squadmate out of fire line to shelter.",
                            3.0f);
                    }
                    return true;
                }
                return false;
            }

            ApplyDesiredMovingPose(ai, soldier, bestDistSq, soldier.GetSuppressionValue());

            AiCore.MoveBotTowardPosition(ai, soldier, bestPatientPos, 1.15f, 2.6f);
            dragState.NextScanTime = now + 0.45f;
            if (dragH != null)
                dragH.Goap.CasualtyDrag = dragState;
            return true;
        }

        private static bool TryRemountNearbyOperableCrewVehicle(
            SoldierAI ai,
            Soldier soldier,
            Squad sq,
            Vector3 myPos,
            float maxRadius)
        {
            if (ai == null || soldier == null || soldier.IsOnVehicle())
                return false;

            if (soldier.targetDestination != null && soldier.targetDestination.TryCast<VehicleRepairTask>() != null)
                return false;

            float now = Time.time;
            AiCore.SoldierFastHandles h = AiCore.GetOrCreateSoldierHandles(soldier);
            bool locallyOwned = AiCore.IsLocallyOwnedBot(soldier);
            if (h != null && now < h.Goap.RemountCooldownUntil)
            {
                if (locallyOwned && (soldier.VehicleToOperate() != null || soldier.IsReachingVehicleToMount()))
                {
                    soldier.CoverPosition(null);
                    AiCore.InvalidateTargetDestinationCache(soldier);
                }
                return false;
            }

            var allVehicles = Vehicle.allVehicles;
            if (allVehicles == null)
                return false;

            int vCount = allVehicles.Count;
            if (vCount == 0)
                return false;

            float bestDistSq = maxRadius * maxRadius;
            Vehicle bestVeh = null;
            string myFaction = soldier.faction;
            bool mySqIsPlayerSquad = sq != null && sq.IsPlayerInSquad();

            for (int i = 0; i < vCount; i++)
            {
                Vehicle veh = allVehicles[i];
                if (veh == null || veh.Pointer == IntPtr.Zero || veh.transform == null)
                    continue;

                if (veh.IsLocked() ||
                    veh.IsDisabled() ||
                    veh.IsCoverDestroyed() ||
                    veh.IsAirVehicle() ||
                    veh.IsTransportVehicle() ||
                    veh.TryCast<MovableVehicle>() == null ||
                    _vehiclesCrewRetreatedFrom.Contains(veh.Pointer))
                {
                    continue;
                }

                if (!mySqIsPlayerSquad && veh.PlayerIsInside())
                    continue;

                Squad insideSq = veh.GetSquadInside();
                if (insideSq != null && insideSq.Pointer != IntPtr.Zero && (sq == null || insideSq.Pointer != sq.Pointer))
                    continue;

                if (veh.CanBeRepaired())
                {
                    Soldier mech = sq != null ? sq.GetMechanic() : null;
                    if (mech == null || !mech.IsAlive || mech.GetRepairTools() == null)
                        continue;
                }

                string vf = GetVehicleCrewFaction(veh);
                if (!string.IsNullOrEmpty(vf) &&
                    vf != "Civilian" &&
                    vf != myFaction)
                    continue;

                float dSq = (veh.transform.position - myPos).sqrMagnitude;
                if (dSq >= bestDistSq)
                    continue;

                bool hasFreeCombatSeat = false;
                if (veh.seats != null)
                {
                    int sCount = veh.seats.Length;
                    for (int s = 0; s < sCount; s++)
                    {
                        Seats seat = veh.seats[s];
                        if (seat != null && seat.unitSet == null && (seat.isDriver || seat.hasTurret))
                        {
                            hasFreeCombatSeat = true;
                            break;
                        }
                    }
                }
                if (!hasFreeCombatSeat)
                    continue;

                bestDistSq = dSq;
                bestVeh = veh;
            }

            if (bestVeh == null)
                return false;

            if (h != null)
            {
                if (h.Goap.RemountTargetVehiclePtr != bestVeh.Pointer ||
                    now - h.Goap.RemountAttemptStartedAt > 15.0f)
                {
                    h.Goap.RemountTargetVehiclePtr = bestVeh.Pointer;
                    h.Goap.RemountAttemptStartedAt = now;
                }
                else if (now - h.Goap.RemountAttemptStartedAt > 6.5f)
                {
                    // Prevent endless circling/stalling against solid hull BoxColliders if mounting cannot complete.
                    h.Goap.RemountCooldownUntil = now + 45.0f;
                    h.Goap.RemountTargetVehiclePtr = IntPtr.Zero;
                    if (locallyOwned)
                    {
                        soldier.CoverPosition(null);
                        AiCore.InvalidateTargetDestinationCache(soldier);
                    }
                    return false;
                }
            }

            Vector3 vehPos = bestVeh.transform.position;
            Vector3 coverPos = bestVeh.GetCoverPosition();
            float dxVeh = vehPos.x - myPos.x, dzVeh = vehPos.z - myPos.z;
            float dxCov = coverPos.x - myPos.x, dzCov = coverPos.z - myPos.z;
            bool nearHull = Mathf.Abs(myPos.y - vehPos.y) <= 2.8f &&
                            ((dxVeh * dxVeh + dzVeh * dzVeh) <= 4.4f * 4.4f ||
                             (dxCov * dxCov + dzCov * dzCov) <= 3.4f * 3.4f);

            if (nearHull && locallyOwned)
            {
                bestVeh.OnCoverReached(soldier);
                if (soldier.IsOnVehicle() || soldier.IsEnteringVehicle)
                {
                    if (h != null)
                        h.Goap.RemountTargetVehiclePtr = IntPtr.Zero;
                    return true;
                }
            }

            if (soldier.targetDestination == null || soldier.targetDestination.Pointer != bestVeh.Pointer)
                AiCore.AssignBotCoverOrDestination(ai, soldier, bestVeh.Cast<AiDestination>());

            Vector3 approachPos = coverPos;
            if (AiCore.TryNormalizeFootSpawnPoint(approachPos, out Vector3 groundedApproach))
                approachPos = groundedApproach;

            AiCore.MoveBotTowardPosition(ai, soldier, approachPos, 0f, 1.0f);
            return true;
        }

        private static bool ShouldBailedCrewRetreatOverJoinSquad(Soldier soldier, Squad sq, Vector3 myPos)
        {
            if (IsSquadOnRetreatTask(sq))
                return true;

            SoldierPersonality pers = GetPersonality(soldier);
            bool isPilot = (sq != null && AiCore.IsConfirmedPlaneCrewSquad(sq)) ||
                           pers == SoldierPersonality.Pilot_Ace ||
                           pers == SoldierPersonality.Pilot_Daredevil ||
                           pers == SoldierPersonality.Pilot_Disciplined;

            GenericGun g0 = soldier.GetHeldGun(0);
            GenericGun g1 = soldier.GetHeldGun(1);
            GenericGun g2 = soldier.GetHeldGun(2);
            bool hasRealPrimary = (g0 != null && !g0.IsPistol() && !g0.IsAtWeapon() && !soldier.IsOutOfAmmosFor(g0)) ||
                                  (g1 != null && !g1.IsPistol() && !g1.IsAtWeapon() && !soldier.IsOutOfAmmosFor(g1)) ||
                                  (g2 != null && !g2.IsPistol() && !g2.IsAtWeapon() && !soldier.IsOutOfAmmosFor(g2));

            // Unarmed / pistol-only bailed crew or pilots retreat toward rear spawn to survive
            if (!hasRealPrimary || isPilot)
                return true;

            // Armed vehicle crewmen check if a friendly infantry squad is within 85m to link up with; otherwise retreat
            AiCore.EnsureWorldSnapshot();
            int sqCount = AiCore.WorldSquadCount;
            var squads = AiCore.WorldSquads;
            IntPtr mySqPtr = sq != null ? sq.Pointer : IntPtr.Zero;
            string myFaction = soldier.faction;

            for (int i = 0; i < sqCount; i++)
            {
                ref AiCore.SquadSnapshot other = ref squads[i];
                if (!other.LeaderIsAlive || other.Ptr == mySqPtr || other.IsVehicleCrew || other.IsPlayerInSquad || other.LeaderOnVehicle || other.Faction != myFaction)
                    continue;
                if ((other.RefPos - myPos).sqrMagnitude <= 85f * 85f)
                    return false;
            }

            return true;
        }

        internal static bool TryLinkUpOrJoinFriendlySquad(
            SoldierAI ai,
            Soldier self,
            Vector3 myPos,
            float maxRadius,
            out Vector3 activeMoveDest)
        {
            activeMoveDest = myPos;
            AiCore.EnsureWorldSnapshot();
            int sqCount = AiCore.WorldSquadCount;
            if (sqCount == 0 || self == null || self.Pointer == IntPtr.Zero)
                return false;

            float bestSq = maxRadius * maxRadius;
            Squad bestSquad = null;
            Vector3 leaderPos = Vector3.zero;
            string myFaction = self.faction;
            IntPtr mySqPtr = self.joinedSquad != null ? self.joinedSquad.Pointer : IntPtr.Zero;
            bool selfIsPlayer = self.IsPlayer();

            var squads = AiCore.WorldSquads;
            for (int i = 0; i < sqCount; i++)
            {
                ref AiCore.SquadSnapshot other = ref squads[i];
                if (!other.LeaderIsAlive || other.Ptr == mySqPtr || other.IsVehicleCrew || other.LeaderOnVehicle || other.Faction != myFaction)
                    continue;
                if (other.UnitCount >= 10)
                    continue;
                // Allow lone AI stragglers to reinforce depleted (< 8) player squads too!
                if (!selfIsPlayer && other.IsPlayerInSquad && other.UnitCount >= 8)
                    continue;

                float dSq = (other.RefPos - myPos).sqrMagnitude;
                if (dSq < bestSq)
                {
                    bestSq = dSq;
                    bestSquad = other.Ref;
                    leaderPos = other.RefPos;
                }
            }

            if (bestSquad == null || ai == null)
                return false;

            // Steer lone AI stragglers/survivors directly toward the friendly Squad Leader;
            // once within 10m of the Squad Leader, vanilla Squad.UpdateSquadData handles the native merge & SyncSquad RPC.
            activeMoveDest = leaderPos;
            AiCore.MoveBotTowardPosition(ai, self, leaderPos, 3.5f, 3.0f);
            return true;
        }

        private static float _nextLonePlayerRejoinCheckTime;
        private static readonly HashSet<IntPtr> _hadMultiMemberPlayerSquads = new HashSet<IntPtr>();
        internal static readonly Dictionary<IntPtr, IntPtr> _warnedWipedSquadByPlayerSoldier = new Dictionary<IntPtr, IntPtr>(8);

        public static void UpdateLonePlayerSquadStatusAndRejoin()
        {
            if (!VisionConfig.EnableMod.Value || Time.timeScale <= 0.01f)
                return;

            float now = Time.time;
            if (now < _nextLonePlayerRejoinCheckTime && (_nextLonePlayerRejoinCheckTime - now) <= 5.0f)
                return;

            _nextLonePlayerRejoinCheckTime = now + 0.25f;
            if (_hadMultiMemberPlayerSquads.Count > 128)
                _hadMultiMemberPlayerSquads.Clear();
            if (_warnedWipedSquadByPlayerSoldier.Count > 32)
                _warnedWipedSquadByPlayerSoldier.Clear();

            // 1. Local Player
            Soldier localPlayer = Soldier.CurrentControlledSoldierOrNull();
            if (localPlayer != null && localPlayer.Pointer != IntPtr.Zero && localPlayer.IsAlive)
            {
                EvaluateSingleHumanLoneSurvivor(localPlayer, isLocalPlayer: true, remoteVanillaOwner: null);
            }

            // 2. Remote Unmodded Co-op Players (Host-Assist)
            if (AiCore.IsInMultiplayerRoom() && AiCore.IsMasterClient())
            {
                AiCore.EnsureWorldSnapshot();
                var wSoldiers = AiCore.WorldSoldiers;
                int wCount = AiCore.WorldSoldierCount;
                IntPtr localPtr = localPlayer != null ? localPlayer.Pointer : IntPtr.Zero;
                for (int i = 0; i < wCount; i++)
                {
                    ref AiCore.SoldierSnapshot sSnap = ref wSoldiers[i];
                    if (!sSnap.IsPlayer || sSnap.Ptr == IntPtr.Zero || sSnap.Ptr == localPtr || sSnap.IsOnVehicle || sSnap.Ref == null)
                        continue;

                    SyncSoldier syn = sSnap.Ref.soldierSyncher;
                    if (syn == null || syn.OwnSyncher)
                        continue;

                    Photon.Realtime.Player owner = syn.Controller ?? syn.Owner;
                    if (owner == null || owner.IsLocal || AiCore.IsPhotonPlayerConfirmedModded(owner))
                        continue;

                    EvaluateSingleHumanLoneSurvivor(sSnap.Ref, isLocalPlayer: false, remoteVanillaOwner: owner);
                }
            }
        }

        private static void EvaluateSingleHumanLoneSurvivor(
            Soldier player,
            bool isLocalPlayer,
            Photon.Realtime.Player remoteVanillaOwner)
        {
            if (player == null || !player.IsAlive || player.IsOnVehicle() || player.HasSurrended() || player.transform == null)
                return;

            Squad sq = player.joinedSquad;
            if (sq == null || sq.Pointer == IntPtr.Zero || sq.IsVehicleCrew)
                return;

            int activeOrDownedCount = 0;
            var units = sq.units;
            if (units != null)
            {
                int uTotal = units.Count;
                for (int i = 0; i < uTotal; i++)
                {
                    Soldier u = units[i];
                    if (u != null && !u.IsDead && (u.IsAlive || u.IsInjured) && !u.HasSurrended())
                        activeOrDownedCount++;
                }
            }

            if (activeOrDownedCount >= 2)
            {
                _hadMultiMemberPlayerSquads.Add(sq.Pointer);
                return;
            }

            if (activeOrDownedCount != 1 || !_hadMultiMemberPlayerSquads.Contains(sq.Pointer))
                return;

            IntPtr oldSqPtr = sq.Pointer;
            if (_warnedWipedSquadByPlayerSoldier.TryGetValue(player.Pointer, out IntPtr warnedSq) && warnedSq == oldSqPtr)
                return;

            _warnedWipedSquadByPlayerSoldier[player.Pointer] = oldSqPtr;
            Vector3 pPos = player.transform.position;
            bool hasNearSq = TryFindNearestOpenFriendlyFootSquad(player.faction, oldSqPtr, pPos, out Squad nearSq, out Vector3 nearLeaderPos);

            string warnMsg;
            if (player.HasRadio() && hasNearSq)
            {
                string callsign = AiCore.FormatSquadPhoneticCallsign(nearSq);
                string bearing = AiCore.FormatRelativeBearingAndDistance(pPos, nearLeaderPos);
                warnMsg = $"Field Radio: Squad wiped out \u2014 {callsign} Leader is {bearing}, approach to rejoin";
            }
            else
            {
                warnMsg = "Your squad was wiped out \u2014 approach a friendly squad leader to rejoin";
            }

            if (isLocalPlayer)
            {
                try { PlayerController.HintToPlayer(warnMsg, false); } catch { }
            }
            else if (remoteVanillaOwner != null)
            {
                AiCore.SendTargetedVanillaChatMessage(remoteVanillaOwner, warnMsg, minPlayerInterval: 10.0f);
            }
        }

        private static bool TryFindNearestOpenFriendlyFootSquad(
            string myFaction,
            IntPtr excludeSqPtr,
            Vector3 fromPos,
            out Squad nearestSquad,
            out Vector3 nearestLeaderPos)
        {
            nearestSquad = null;
            nearestLeaderPos = fromPos;
            if (string.IsNullOrEmpty(myFaction))
                return false;

            AiCore.EnsureWorldSnapshot();
            int sqCount = AiCore.WorldSquadCount;
            var squads = AiCore.WorldSquads;
            float bestDistSq = 450.0f * 450.0f;

            for (int i = 0; i < sqCount; i++)
            {
                ref AiCore.SquadSnapshot other = ref squads[i];
                if (!other.LeaderIsAlive ||
                    other.Ptr == IntPtr.Zero ||
                    other.Ptr == excludeSqPtr ||
                    other.IsVehicleCrew ||
                    other.LeaderOnVehicle ||
                    other.UnitCount <= 0 ||
                    other.UnitCount >= 10 ||
                    other.Faction != myFaction)
                {
                    continue;
                }

                float dSq = (other.RefPos - fromPos).sqrMagnitude;
                if (dSq < bestDistSq)
                {
                    bestDistSq = dSq;
                    nearestSquad = other.Ref;
                    nearestLeaderPos = other.RefPos;
                }
            }

            return nearestSquad != null;
        }

        private static bool TryFindNearestRadioHubPosition(
            Squad selfSq,
            Vector3 myPos,
            string myFaction,
            float maxRadius,
            out Vector3 hubPos)
        {
            hubPos = Vector3.zero;
            if (selfSq == null)
                return false;

            AiCore.EnsureWorldSnapshot();
            int sqCount = AiCore.WorldSquadCount;
            if (sqCount == 0)
                return false;

            float bestSq = maxRadius * maxRadius;
            bool found = false;
            IntPtr selfPtr = selfSq.Pointer;
            var squads = AiCore.WorldSquads;

            for (int i = 0; i < sqCount; i++)
            {
                ref AiCore.SquadSnapshot other = ref squads[i];
                if (!other.LeaderIsAlive || other.Ptr == selfPtr || other.Faction != myFaction)
                    continue;

                if (!other.HasIntrinsicRadio && !other.LeaderOnGroundVehicle && !other.IsPlayerInSquad)
                    continue;

                float dSq = (other.RefPos - myPos).sqrMagnitude;
                if (dSq < bestSq && dSq > 45f * 45f)
                {
                    bestSq = dSq;
                    hubPos = other.RefPos;
                    found = true;
                }
            }

            return found;
        }
    }
}
