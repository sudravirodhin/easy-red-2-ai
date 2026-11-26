using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace EasyRed2HumanAI
{
    public static partial class VeteranAndTacticsPatches
    {
        // =====================================================================
        // Synchronized Bot Stance, Cover Seeking & Gunner Bipod Wall Mount
        // =====================================================================
        private static void SetBotPose(SoldierAI ai, Soldier soldier, SoldierPose pose, bool forceImmediate = false)
        {
            if (ai == null || soldier == null || AiCore.IsSoldierInVehicleOrTransition(soldier))
                return;

            // Fast-path when already in desired non-prone pose to avoid allocating soldier.Params wrapper on redundant calls
            if (!forceImmediate && pose != SoldierPose.Prone && soldier.m_pose == pose)
            {
                ai.actualPose = pose;
                if (pose == SoldierPose.Crouch &&
                    soldier.Pointer != IntPtr.Zero &&
                    !ai.IsMoving() &&
                    (ai.HasEnemyAndCanFireAtHim || soldier.GetSuppressionValue() >= 8f))
                {
                    float lockUntil = Time.time + 3.5f;
                    AiCore.SoldierFastHandles crouchH = AiCore.GetOrCreateSoldierHandles(soldier);
                    if (crouchH != null && lockUntil > crouchH.Stance.CombatCrouchLockUntil)
                        crouchH.Stance.CombatCrouchLockUntil = lockUntil;
                }
                return;
            }

            if (soldier.UsingBipod() || soldier.IsBipodTransitioning() || (!forceImmediate && !AiCore.CanBotChangePoseAutonomously(soldier)))
            {
                ai.actualPose = soldier.m_pose;
                return;
            }

            AiCore.SoldierFastHandles sh = AiCore.GetOrCreateSoldierHandles(soldier);
            float now = Time.time;
            bool isMoving = ai.IsMoving();
            bool wantsToMove = isMoving || HasActiveEscapeOrMoveIntent(ai, soldier, sh, now);

            if (pose == SoldierPose.Idle &&
                soldier.m_pose != SoldierPose.Idle &&
                (soldier.isInHandsAnimation || soldier.IsReloading || soldier.isInBodyAnimation))
            {
                if (soldier.m_pose == SoldierPose.Prone && wantsToMove)
                {
                    if (!soldier.isInBodyAnimation && now >= soldier.stopHandsAnim_end)
                    {
                        // Allow moving prone bots to rise to Crouch while reloading / finishing non-locking hands anims
                        pose = SoldierPose.Crouch;
                    }
                    else
                    {
                        // Native Soldier.SetPose blocks leaving Prone while stopHandsAnim_end is active;
                        // queue Crouch on actualPose so Soldier.Move lifts him the exact frame it finishes.
                        ai.actualPose = SoldierPose.Crouch;
                        if (sh != null)
                        {
                            sh.Stance.NextStandLiftAttemptTime = Mathf.Max(now + 0.25f, soldier.stopHandsAnim_end);
                        }
                        return;
                    }
                }
                else
                {
                    ai.actualPose = soldier.m_pose;
                    return;
                }
            }

            bool isActivelyBandaging = sh != null &&
                                       now < sh.Stance.BandageLowPoseUntil &&
                                       (soldier.isBleeding || soldier.stopHandsAnim_end > now);
            if (sh != null && sh.Stance.BandageLowPoseUntil > 0f && !soldier.isBleeding && now >= soldier.stopHandsAnim_end)
            {
                sh.Stance.BandageLowPoseUntil = 0f;
            }
            if (isActivelyBandaging && pose == SoldierPose.Idle && !soldier.IsJumping())
            {
                pose = isMoving
                    ? SoldierPose.Crouch
                    : (sh.Stance.BandageTargetPose >= SoldierPose.Crouch ? sh.Stance.BandageTargetPose : SoldierPose.Crouch);
            }

            // Never slam a fleeing or actively moving bot into Prone, even when bleeding
            if (pose == SoldierPose.Prone && wantsToMove)
            {
                pose = SoldierPose.Crouch;
            }

            // Indoor Ceiling & Bridge Prone Prevention + NoProneUntil guard:
            // prevent slamming NoProneUntil-locked bots into Prone (avoids Crouch<->Prone loops).
            if (pose == SoldierPose.Prone && !soldier.isBleeding)
            {
                if (sh != null && now < sh.Stance.NoProneUntil)
                {
                    pose = SoldierPose.Crouch;
                }
                else
                {
                    Vector3 feetPos = soldier.transform.position;
                    if (VisionConfig.EnableIndoorCeilingPronePrevention.Value &&
                        AiCore.HasLowCeilingOverhead(feetPos, 3.0f))
                    {
                        pose = SoldierPose.Crouch;
                        if (sh != null)
                        {
                            sh.Stance.NoProneUntil = now + 10.0f;
                            if (now >= sh.Stance.NextIndoorCeilingCountTime)
                            {
                                sh.Stance.NextIndoorCeilingCountTime = now + 5.0f;
                                IndoorPronePreventedCount++;
                                if (VisionConfig.DebugLogging.Value)
                                {
                                    AiCore.LogDebug("CeilingCrouch", $"{soldier.faction} indoor soldier kept in Crouch instead of Prone beneath low ceiling.");
                                }
                            }
                        }
                    }
                    else if (VisionConfig.EnableBridgeCrossingDiscipline.Value &&
                             AiCore.IsOnBridgeOrNarrowCrossing(feetPos))
                    {
                        pose = SoldierPose.Crouch;
                        if (sh != null)
                        {
                            sh.Stance.NoProneUntil = now + 4.0f;
                        }
                    }
                }
            }

            if (pose >= SoldierPose.Crouch &&
                !isMoving &&
                (ai.HasEnemyAndCanFireAtHim || soldier.GetSuppressionValue() >= 8f))
            {
                if (sh != null)
                {
                    float lockUntil = now + 3.5f;
                    if (lockUntil > sh.Stance.CombatCrouchLockUntil)
                    {
                        sh.Stance.CombatCrouchLockUntil = lockUntil;
                    }
                }
            }

            if (soldier.m_pose == pose)
            {
                ai.actualPose = pose;
                return;
            }

            bool blockCombativeStandOverride = pose == SoldierPose.Idle &&
                                               soldier.m_pose >= SoldierPose.Crouch &&
                                               !(soldier.m_pose == SoldierPose.Prone && wantsToMove) &&
                                               ai.HasEnemyAndCanFireAtHim &&
                                               !soldier.IsCharging() &&
                                               !soldier.IsJumping() &&
                                               sh != null &&
                                               now < sh.Stance.PoseLockUntil;

            bool isUrgentTransition = !blockCombativeStandOverride &&
                                      (forceImmediate ||
                                       (soldier.isBleeding && pose >= SoldierPose.Crouch) ||
                                       (soldier.m_pose != SoldierPose.Idle && pose == SoldierPose.Idle && wantsToMove) ||
                                       (soldier.m_pose == SoldierPose.Prone && wantsToMove) ||
                                       (soldier.m_pose == SoldierPose.Idle && pose >= SoldierPose.Crouch && !wantsToMove &&
                                        (ai.HasEnemyAndCanFireAtHim || soldier.GetSuppressionValue() >= 15f)));

            if (!isUrgentTransition && sh != null)
            {
                if (soldier.isInBodyAnimation || now < sh.Stance.PoseLockUntil)
                {
                    ai.actualPose = soldier.m_pose;
                    return;
                }
            }

            SoldierPose prevPose = soldier.m_pose;
            ai.actualPose = pose;
            soldier.SetPose(pose, true, false, 0.2f);

            // Native Soldier.SetPose (0x180767A70) checks 0.8m upward clearance for Prone -> Idle
            // but only 0.4m for Prone -> Crouch. If Prone -> Idle silently aborted under an overhang,
            // trench beam, branch, or steep bank, fall back to Crouch instead of belly-crawling.
            if (prevPose == SoldierPose.Prone && pose == SoldierPose.Idle && soldier.m_pose == SoldierPose.Prone)
            {
                ai.actualPose = SoldierPose.Crouch;
                soldier.SetPose(SoldierPose.Crouch, true, false, 0.2f);
            }

            if (sh != null)
            {
                if (soldier.m_pose != prevPose)
                {
                    sh.Stance.PoseLockUntil = now + 2.8f;
                    sh.Stance.NextStandLiftAttemptTime = 0f;
                    if (prevPose == SoldierPose.Prone && soldier.m_pose < SoldierPose.Prone && wantsToMove)
                    {
                        sh.Stance.NoProneUntil = Mathf.Max(sh.Stance.NoProneUntil, now + 4.5f);
                    }
                    if (soldier.m_pose > prevPose)
                    {
                        sh.Stance.NextWallCheckTime = 0f;
                    }
                }
                else if (pose < prevPose)
                {
                    // Upward lift silently failed (stopHandsAnim_end or tight < 0.4m overhead obstruction).
                    // Do not stamp PoseLockUntil for 2.8s; throttle 60 Hz retries briefly instead.
                    sh.Stance.NextStandLiftAttemptTime = Mathf.Max(now + 0.25f, soldier.stopHandsAnim_end);
                    if (prevPose == SoldierPose.Prone && wantsToMove)
                    {
                        ai.actualPose = SoldierPose.Crouch;
                    }
                }
            }
        }

        internal static SoldierPose GetPreferredBandagePose(Soldier soldier, AiCore.SoldierFastHandles h, float now)
        {
            if (soldier == null)
                return SoldierPose.Crouch;

            Vector3 pos = (h != null && h.Tr != null)
                ? h.Tr.position
                : (soldier.transform != null ? soldier.transform.position : Vector3.zero);

            bool forceCrouchForEnv = (h != null && now < h.Stance.NoProneUntil) ||
                                     (VisionConfig.EnableBridgeCrossingDiscipline.Value && AiCore.IsOnBridgeOrNarrowCrossing(pos)) ||
                                     (VisionConfig.EnableIndoorCeilingPronePrevention.Value && AiCore.HasLowCeilingOverhead(pos, 3.0f)) ||
                                     (AiCore.TryGetActiveWaterLevel(out float waterY) && pos.y <= waterY + 0.55f);
            if (forceCrouchForEnv)
                return SoldierPose.Crouch;

            if (soldier.IsOnCover() && soldier.GetSuppressionValue() < 48f)
                return SoldierPose.Crouch;

            return SoldierPose.Prone;
        }

        internal static void ApplyBandageLowPose(SoldierAI ai, Soldier soldier, AiCore.SoldierFastHandles h, float now)
        {
            if (ai == null || soldier == null || !AiCore.IsLocallyOwnedBot(soldier) || AiCore.IsSoldierInVehicleOrTransition(soldier))
                return;

            SoldierPose pose = GetPreferredBandagePose(soldier, h, now);
            if (h != null)
            {
                h.Stance.BandageTargetPose = pose;
                float lowUntil = now + 4.35f;
                if (lowUntil > h.Stance.BandageLowPoseUntil)
                    h.Stance.BandageLowPoseUntil = lowUntil;
            }

            if (!soldier.IsOnCover() && (ai.IsMoving() || ai.HasPath || ai.moveDirectlyToward_end > now))
                ai.Stop(true);

            SetBotPose(ai, soldier, pose, forceImmediate: true);
        }

        internal static Vector3 GetCachedMoveEndpoint(AiCore.SoldierFastHandles h, SoldierAI ai, Soldier soldier, out int nodesCount)
        {
            int frame = Time.frameCount;
            float now = Time.time;
            bool hasPath = !ReferenceEquals(ai, null) && ai.HasPath;

            if (h != null &&
                (h.LastEndpointFrame == frame ||
                 (now < h.NextEndpointCheckTime && (hasPath == (h.CachedNodesCount >= 0)))))
            {
                nodesCount = h.CachedNodesCount;
                return h.CachedEndpoint;
            }

            Vector3 ep;
            int nc = -1;
            if (hasPath)
            {
                var p = AiCore.GetCachedAiPath(h, ai);
                if (!ReferenceEquals(p, null) && p.Pointer != IntPtr.Zero)
                {
                    ep = p.Destination;
                    nc = p.NodesCount;
                }
                else
                {
                    ep = ai.MoveDestination;
                }
            }
            else if (h != null && AiCore.ResolveTargetDestination(h) != AiCore.CachedDestKind.None &&
                     !ReferenceEquals(h.CachedDest, null) && h.CachedDest != null)
            {
                ep = h.CachedDest.GetCoverPosition();
            }
            else if (h == null && !ReferenceEquals(soldier, null) && soldier.HasDestinationAssigned)
            {
                AiDestination rawDest = soldier.targetDestination;
                ep = (!ReferenceEquals(rawDest, null) && rawDest != null) ? rawDest.GetCoverPosition() : ai.MoveDestination;
            }
            else
            {
                ep = ai.MoveDestination;
            }

            if (h != null)
            {
                h.LastEndpointFrame = frame;
                h.NextEndpointCheckTime = now + (hasPath ? 0.10f : 0.16f);
                h.CachedEndpoint = ep;
                h.CachedNodesCount = nc;
            }

            nodesCount = nc;
            return ep;
        }

        internal static Vector3 GetActualMoveEndpoint(SoldierAI ai, Soldier soldier)
        {
            AiCore.SoldierFastHandles h = !ReferenceEquals(soldier, null) ? AiCore.GetOrCreateSoldierHandles(soldier) : AiCore.GetOrCreateSoldierHandlesByAi(ai);
            return GetCachedMoveEndpoint(h, ai, soldier, out _);
        }

        internal static bool IsInCommittedFiringHalt(IntPtr soldierPtr, float now)
        {
            return AiCore.TryGetSoldierHandles(soldierPtr, out var h) &&
                   h.Combat.FireAndMovement.InFiringHalt &&
                   now < h.Combat.FireAndMovement.PhaseEndTime;
        }

        internal static bool IsAsyncPathRefreshPending(SoldierAI ai, Soldier soldier, Vector3 myPos, float now)
        {
            if (ReferenceEquals(ai, null) || ReferenceEquals(soldier, null) || ai.IsMoving() || !ai.HasPathRequest || soldier.isInBodyAnimation)
                return false;

            if (IsInCommittedFiringHalt(soldier.Pointer, now))
                return false;

            Vector3 endpoint = GetActualMoveEndpoint(ai, soldier);
            return (endpoint - myPos).sqrMagnitude > 1.85f * 1.85f;
        }

        internal static bool HasActiveEscapeOrMoveIntent(
            SoldierAI ai,
            Soldier soldier,
            AiCore.SoldierFastHandles h,
            float now)
        {
            if (ReferenceEquals(ai, null) || ReferenceEquals(soldier, null))
                return false;

            if (h != null &&
                h.Emergent.HasDecision &&
                h.Emergent.CurrentDecision.Step0 == EmergentEngine.EmergentActionId.SelfBandage &&
                !(h.MoveOverride.Kind == AiCore.BotMoveOverrideKind.GrenadeReflex && now < h.MoveOverride.ExpireTime) &&
                !(h.MoveOverride.Kind == AiCore.BotMoveOverrideKind.VehicleEvade && now < h.MoveOverride.ExpireTime))
            {
                return false;
            }

            if (ai.IsMoving() || ai.moveDirectlyToward_end > now)
                return true;

            if (h != null &&
                h.MoveOverride.Kind != AiCore.BotMoveOverrideKind.None &&
                now < h.MoveOverride.ExpireTime)
            {
                return true;
            }

            Vector3 myPos = (h != null && h.Tr != null)
                ? h.Tr.position
                : (soldier.transform != null ? soldier.transform.position : Vector3.zero);

            if (h != null &&
                h.Goap.Plan.HasMoveOverride &&
                now < h.Goap.Plan.PlanExpireTime &&
                (h.Goap.Plan.MoveOverrideDest - myPos).sqrMagnitude > 1.85f * 1.85f)
            {
                return true;
            }

            if (ai.HasPathRequest)
            {
                Vector3 reqEnd = GetCachedMoveEndpoint(h, ai, soldier, out _);
                if ((reqEnd - myPos).sqrMagnitude > 1.85f * 1.85f)
                    return true;
            }

            if (ai.HasPath && ai.IsMoveTime)
            {
                Vector3 pathEnd = GetCachedMoveEndpoint(h, ai, soldier, out int nodesCount);
                if (nodesCount > 0 && (pathEnd - myPos).sqrMagnitude > 1.85f * 1.85f)
                    return true;
            }

            return false;
        }

        internal static bool EvaluateTacticalStealthCrouch(
            bool isStealthFollowingLowLeader,
            bool isCovertHoldFireActive,
            bool isCloseAcousticInvestigating,
            bool isUnspottedCloseOrNightApproach,
            bool isUnspottedUnderEnemyPlane,
            bool isEmergencyOrCharging,
            bool isCompromisedOrLoud,
            bool isPointBlankVisibleCqb)
        {
            if (isEmergencyOrCharging || isPointBlankVisibleCqb || isCompromisedOrLoud)
                return false;

            return isStealthFollowingLowLeader ||
                   isCovertHoldFireActive ||
                   isCloseAcousticInvestigating ||
                   isUnspottedCloseOrNightApproach ||
                   isUnspottedUnderEnemyPlane;
        }

        internal static bool ShouldBotStealthCrouch(SoldierAI ai, Soldier soldier, AiCore.SoldierFastHandles h, float now)
        {
            if (ReferenceEquals(soldier, null) || h == null)
                return false;

            bool emergencyOrCharge =
                soldier.IsCharging() ||
                soldier.isBleeding ||
                soldier.IsCarryingBody() ||
                (!ReferenceEquals(ai, null) && ai.knifeCharge) ||
                (h.MoveOverride.Kind == AiCore.BotMoveOverrideKind.GrenadeReflex && now < h.MoveOverride.ExpireTime) ||
                (h.MoveOverride.Kind == AiCore.BotMoveOverrideKind.VehicleEvade && now < h.MoveOverride.ExpireTime) ||
                (h.MoveOverride.Kind == AiCore.BotMoveOverrideKind.EmergencyFallback && now < h.MoveOverride.ExpireTime) ||
                (now < h.Goap.Plan.PlanExpireTime && h.Goap.Plan.IsEvacuatingBailedCrew) ||
                (h.Emergent.HasDecision &&
                 (h.Emergent.CurrentDecision.Step0 == EmergentEngine.EmergentActionId.FallbackRegroup ||
                  (h.Emergent.CurrentDecision.Step0 == EmergentEngine.EmergentActionId.PopSmoke &&
                   h.Emergent.CurrentDecision.Step1 == EmergentEngine.EmergentActionId.FallbackRegroup)));
            if (emergencyOrCharge)
                return false;

            if (!ReferenceEquals(ai, null) && ai.HasEnemyAndCanFireAtHim && ai.distFromTarget > 0.1f && ai.distFromTarget <= 11.0f)
                return false;

            bool compromised = now < h.Vision.UnderFireUntil ||
                               (h.Vision.LastFiredTime > 0f && now - h.Vision.LastFiredTime <= 3.5f);
            if (compromised)
                return false;

            if (soldier.IsStealth)
            {
                Squad sq = AiCore.GetCachedSquad(h, now);
                if (!ReferenceEquals(sq, null) && sq.Leader != null && sq.Leader.m_pose > SoldierPose.Idle)
                    return true;
            }

            if (now < h.Stance.StealthCrouchUntil)
            {
                if (soldier.IsSpotted || soldier.GetSuppressionValue() >= 24f)
                    return false;
                return true;
            }

            return false;
        }

        internal static bool EvaluateProneSafetyVsWin(
            bool currentlyProne,
            bool inCoverOrShelteredWall,
            bool hasClearProneLof,
            bool hasVisibleEnemy,
            float enemyDist,
            float suppression,
            float smoothedHarm,
            float totalHarm,
            bool underFireRecently,
            bool underShelling,
            bool enemyPlaneOverhead,
            bool isOpenGunnerOrMarksman,
            float wSurv,
            float wWin,
            float winUrgency,
            float canHarm,
            bool objectiveTickingAgainst,
            bool wantsToMoveOrWinPush)
        {
            if (wantsToMoveOrWinPush || (hasVisibleEnemy && enemyDist > 0.1f && enemyDist <= 13.0f))
                return false;

            float supp01 = Mathf.Clamp01(suppression / 85.0f);
            float danger01 = Mathf.Clamp01(Mathf.Max(totalHarm, Mathf.Max(smoothedHarm, supp01 * 0.90f)));

            bool proneIsSafer;
            if (enemyPlaneOverhead)
            {
                proneIsSafer = true;
            }
            else if (inCoverOrShelteredWall)
            {
                proneIsSafer = underShelling ||
                               suppression >= 52.0f ||
                               smoothedHarm >= 0.58f ||
                               (currentlyProne && (suppression >= 34.0f || smoothedHarm >= 0.42f));
            }
            else
            {
                proneIsSafer = underShelling ||
                               underFireRecently ||
                               danger01 >= 0.30f ||
                               (currentlyProne && danger01 >= 0.18f) ||
                               (isOpenGunnerOrMarksman && hasVisibleEnemy && enemyDist >= 28.0f && hasClearProneLof);
            }

            if (!proneIsSafer)
                return false;

            float safetyGain = inCoverOrShelteredWall
                ? (danger01 * 0.52f + (underShelling ? 0.48f : 0f))
                : (danger01 * 0.92f + (underShelling ? 0.48f : 0f) + (underFireRecently ? 0.18f : 0f));
            if (enemyPlaneOverhead)
                safetyGain = Mathf.Max(safetyGain, 0.80f);
            if (isOpenGunnerOrMarksman && !inCoverOrShelteredWall && hasVisibleEnemy && enemyDist >= 28.0f && hasClearProneLof)
                safetyGain += 0.32f;
            if (currentlyProne)
                safetyGain += 0.14f;

            float survivePronePull = Mathf.Max(0.50f, wSurv) * safetyGain;

            float winRiseNeed = 0.14f + Mathf.Clamp01(winUrgency) * 0.50f;
            if (objectiveTickingAgainst)
                winRiseNeed += 0.38f;
            if (hasVisibleEnemy && !hasClearProneLof)
                winRiseNeed += Mathf.Clamp01(canHarm) * 0.62f + 0.18f;
            else if (!hasVisibleEnemy && inCoverOrShelteredWall)
                winRiseNeed += 0.22f;

            float winRisePull = Mathf.Max(0.50f, wWin) * winRiseNeed;

            return survivePronePull > winRisePull;
        }

        internal static bool ShouldBotChooseOrStayProneForSafety(
            SoldierAI ai,
            Soldier soldier,
            AiCore.SoldierFastHandles h,
            bool inCoverOrShelteredWall,
            bool hasClearProneLof,
            bool hasVisibleEnemy,
            float enemyDist,
            float suppression,
            bool enemyPlaneOverhead,
            bool wantsToMoveOrWinPush,
            float now)
        {
            if (ReferenceEquals(soldier, null) || h == null)
                return false;

            bool currentlyProne = soldier.m_pose == SoldierPose.Prone;
            if (soldier.IsOnVehicle() ||
                soldier.IsCharging() ||
                soldier.UsingBipod() ||
                soldier.IsBipodTransitioning() ||
                soldier.IsTowardWall() ||
                (!currentlyProne && (soldier.IsReloading || soldier.MustReload())) ||
                (!ReferenceEquals(ai, null) && (ai.HeldMortar != null || ai.knifeCharge)) ||
                now < h.Stance.NoProneUntil)
            {
                return false;
            }

            bool harmPushingToMove = wantsToMoveOrWinPush;
            if (!harmPushingToMove && h.Emergent.HasDecision)
            {
                var step0 = h.Emergent.CurrentDecision.Step0;
                harmPushingToMove =
                    step0 == EmergentEngine.EmergentActionId.EvadeGrenade ||
                    step0 == EmergentEngine.EmergentActionId.FallbackRegroup ||
                    step0 == EmergentEngine.EmergentActionId.CloseBreach ||
                    step0 == EmergentEngine.EmergentActionId.FlankManeuver ||
                    step0 == EmergentEngine.EmergentActionId.DragCasualty ||
                    step0 == EmergentEngine.EmergentActionId.MedicHeal ||
                    step0 == EmergentEngine.EmergentActionId.LiberatePrisoner;
            }

            float wSurv = h.Emergent.WeightsInitialized ? h.Emergent.Weights.WSurvive : 1.35f;
            float wWin = h.Emergent.WeightsInitialized ? h.Emergent.Weights.WWin : 0.95f;
            float smoothedHarm = h.Emergent.HasDecision ? h.Emergent.SmoothedHarmNow : 0f;
            float totalHarm = h.Emergent.HasDecision ? h.Emergent.CurrentDecision.TotalHarm : smoothedHarm;
            float winUrgency = h.Emergent.HasDecision ? h.Emergent.CurrentDecision.WinUrgency : 0.25f;
            float canHarm = h.Emergent.HasDecision ? h.Emergent.CurrentDecision.CanHarm : (hasVisibleEnemy ? 0.70f : 0f);
            bool underFireRecently = now < h.Vision.UnderFireUntil;

            bool objectiveTickingAgainst = false;
            Squad sq = AiCore.GetCachedSquad(h, now);
            if (!ReferenceEquals(sq, null) &&
                sq.Pointer != IntPtr.Zero &&
                _squadBlackboards.TryGetValue(sq.Pointer, out SquadBlackboard b))
            {
                objectiveTickingAgainst = b.IsObjectiveTickingAgainstUs(now);
            }

            bool underShelling = h.Tr != null &&
                                 TryGetActiveEnemyShellingPocket(AiCore.GetCachedFaction(h, now), h.Tr.position, now, out _, out _);

            SoldierPersonality pers = GetPersonality(soldier);
            bool isOpenGunnerOrMarksman = pers >= SoldierPersonality.Gunner_Anchor &&
                                          pers <= SoldierPersonality.Marksman_Spotter;

            return EvaluateProneSafetyVsWin(
                currentlyProne,
                inCoverOrShelteredWall,
                hasClearProneLof,
                hasVisibleEnemy,
                enemyDist,
                suppression,
                smoothedHarm,
                totalHarm,
                underFireRecently,
                underShelling,
                enemyPlaneOverhead,
                isOpenGunnerOrMarksman,
                wSurv,
                wWin,
                winUrgency,
                canHarm,
                objectiveTickingAgainst,
                harmPushingToMove);
        }

        internal static SoldierPose GetDesiredMovingPose(
            SoldierAI ai,
            Soldier soldier,
            float remDistSq,
            float suppression,
            bool stealthCrouch = false)
        {
            if (stealthCrouch)
                return SoldierPose.Crouch;

            if (!ReferenceEquals(ai, null) &&
                !ai.IsMoving() &&
                ai.HasEnemyAndCanFireAtHim &&
                (ReferenceEquals(soldier, null) || !soldier.IsCharging()))
            {
                return SoldierPose.Crouch;
            }

            IntPtr ptr = !ReferenceEquals(soldier, null) ? soldier.Pointer : IntPtr.Zero;
            if (AiCore.TryGetSoldierHandles(ptr, out var hMove))
            {
                float nowMove = Time.time;
                if (ShouldBotStealthCrouch(ai, soldier, hMove, nowMove) ||
                    (nowMove < hMove.Combat.NextFriendlyStepAsideTime &&
                     nowMove < hMove.Stance.CombatCrouchLockUntil))
                {
                    return SoldierPose.Crouch;
                }
            }

            // Keep crouched posture during short <= 3.2m cover/sidestep/anti-stack micro-adjustments
            if (remDistSq <= 3.2f * 3.2f &&
                !ReferenceEquals(soldier, null) &&
                (soldier.m_pose >= SoldierPose.Crouch ||
                 suppression >= 8f ||
                 (!ReferenceEquals(ai, null) && ai.HasEnemyAndCanFireAtHim) ||
                 soldier.isAlerted))
            {
                return SoldierPose.Crouch;
            }

            // Crouch-run under suppressive fire when approaching cover/dest or under heavy fire
            if (suppression >= 24f && (remDistSq <= 14.0f * 14.0f || suppression >= 38f))
                return SoldierPose.Crouch;

            return SoldierPose.Idle;
        }

        internal static void ApplyDesiredMovingPose(
            SoldierAI ai,
            Soldier soldier,
            float remDistSq,
            float suppression,
            bool stealthCrouch = false)
        {
            SoldierPose desired = GetDesiredMovingPose(ai, soldier, remDistSq, suppression, stealthCrouch);
            if (desired == SoldierPose.Idle)
            {
                if (soldier.m_pose != SoldierPose.Idle || ai.actualPose != SoldierPose.Idle)
                    SetBotPose(ai, soldier, SoldierPose.Idle, true);
            }
            else
            {
                SetBotPose(ai, soldier, SoldierPose.Crouch, soldier.m_pose == SoldierPose.Prone);
            }
        }

        internal static bool IsTargetUnengageableOnFoot(SoldierAI ai, Soldier soldier, IntPtr soldierPtr, float now)
        {
            Spottable target = ai.visibleTarget;
            if (ReferenceEquals(target, null) || target.Pointer == IntPtr.Zero)
                return true;

            if (target.IsAirVehicle())
            {
                AiParams p = soldier.Params;
                return p == null || !p.targetAircaftsViolently;
            }

            if (VisionConfig.PreventSmallArmsFireAtTanks.Value && target.IsWheeledVehicleOrTank())
            {
                if (AiCore.TryGetSoldierHandles(soldierPtr, out var vcH) && now < vcH.Combat.VehicleAim.ExpireTime)
                    return !vcH.Combat.VehicleAim.CanEngage;

                if (!HasUsableAntiTankCapability(soldier))
                {
                    Vehicle veh = target.TryCast<Vehicle>();
                    if (veh != null && GetEffectiveVehicleExplosionArmor(veh) >= 8.0f)
                        return true;
                }
            }

            return false;
        }

        private static bool IsMachineGunnerOrHasBipod(Soldier soldier, out AttachmentBipod bipod)
        {
            bipod = null;
            AiCore.SoldierFastHandles h = AiCore.GetOrCreateSoldierHandles(soldier);
            if (h == null)
                return false;

            return AiCore.GetCachedGunnerAndBipod(h, Time.time, out bipod);
        }

        internal static bool TryFindTankHullCoverOrEscortSpot(
            Soldier soldier,
            Vector3 myPos,
            Vector3 threatDir,
            bool allowMovingEscort,
            out Vector3 hullSpotPos,
            out bool isMovingEscort)
        {
            hullSpotPos = Vector3.zero;
            isMovingEscort = false;

            var vehOctree = CoverManager.vehicleCovers;
            if (vehOctree == null || soldier == null)
                return false;

            var nearbyVehs = vehOctree.GetNearby(myPos, 17.5f);
            if (nearbyVehs == null || nearbyVehs.Length == 0)
                return false;

            threatDir.y = 0f;
            threatDir = threatDir.sqrMagnitude > 0.001f ? threatDir.normalized : soldier.transform.forward;

            int h = AiCore.GetDeterministicHash(soldier.Pointer, 73);
            float spread = ((h % 5) - 2) * 0.65f; // -1.3m .. +1.3m lateral fan behind hull
            float sideSign = (h & 1) == 0 ? 1.0f : -1.0f;

            float bestScore = float.MaxValue;
            bool found = false;

            int count = nearbyVehs.Length;
            for (int i = 0; i < count; i++)
            {
                AiDestination dest = nearbyVehs[i];
                if (dest == null || dest.Pointer == IntPtr.Zero)
                    continue;

                Vehicle v = dest.GetCoverConnectedVehicle();
                if (v == null || v.Pointer == IntPtr.Zero)
                    v = dest.TryCast<Vehicle>();
                AiCore.VehicleFastHandles vh = AiCore.GetOrCreateVehicleHandles(v);
                if (vh == null || vh.Tr == null || vh.IsAir)
                    continue;

                if (!vh.IsTank && GetEffectiveVehicleExplosionArmor(v) < 8.0f)
                    continue;

                bool destroyed = v.IsCoverDestroyed();
                if (!destroyed)
                {
                    string vf = GetVehicleCrewFaction(v);
                    string myFac = AiCore.GetCachedFaction(soldier);
                    if (!string.IsNullOrEmpty(vf) && !AiCore.AreSameSideFactions(vf, myFac))
                        continue; // Never try to crouch right behind a live enemy tank!
                }

                Vector3 vPos = vh.Tr.position;
                if (Mathf.Abs(vPos.y - myPos.y) > 2.6f)
                    continue;

                Vector3 vFwd = v.transform.forward;
                vFwd.y = 0f;
                vFwd = vFwd.sqrMagnitude > 0.001f ? vFwd.normalized : Vector3.forward;

                float speed = destroyed ? 0f : Mathf.Abs(v.GetCurrentSpeed());
                Vector3 relVel = destroyed ? Vector3.zero : v.GetCurrentRelativeSpeed();
                if (relVel.z < -0.25f)
                    continue; // Never step behind a reversing tank/vehicle!

                Vector3 candPos;
                bool candMoving = false;

                if (speed < 0.45f)
                {
                    // Stationary / Idle / Wrecked Tank: crouch on the sheltered leeward side opposite enemy fire
                    Vector3 shelterDir = -threatDir;
                    Vector3 latPerp = new Vector3(shelterDir.z, 0f, -shelterDir.x);
                    float hullOffset = Mathf.Lerp(2.9f, 4.2f, Mathf.Abs(Vector3.Dot(shelterDir, vFwd)));
                    candPos = vPos + shelterDir * hullOffset + latPerp * spread;
                }
                else if (allowMovingEscort && !destroyed && speed <= 5.2f && relVel.z > 0.35f && Vector3.Dot(vFwd, threatDir) > 0.35f)
                {
                    // Walking-Pace Friendly Tank Escort: advance in the sheltered rear-quarter wake behind the hull, clear of tracks!
                    Vector3 vRight = new Vector3(vFwd.z, 0f, -vFwd.x);
                    candPos = vPos - vFwd * 4.35f + vRight * (sideSign * 1.15f);
                    candMoving = true;
                }
                else
                {
                    continue;
                }

                Vector3 fromVeh = candPos - vPos;
                fromVeh.y = 0f;
                float fromVehDist = fromVeh.magnitude;
                if (fromVehDist > 0.5f &&
                    AiCore.IsDirectionBlockedByEnvironment(vPos + Vector3.up * 1.0f, fromVeh / fromVehDist, fromVehDist + 0.35f, v.transform.root))
                {
                    continue;
                }

                float dSq = (candPos - myPos).sqrMagnitude;
                if (dSq < bestScore)
                {
                    bestScore = dSq;
                    hullSpotPos = candPos;
                    isMovingEscort = candMoving;
                    found = true;
                }
            }

            return found;
        }

        private static bool TryFindBridgeParapetOrPillarSpot(
            Soldier soldier,
            Vector3 myPos,
            Vector3 threatDir,
            out Vector3 bestSpot)
        {
            bestSpot = myPos;
            int envMask = AiCore.GetSolidEnvironmentMask();
            Vector3 threatFlat = new Vector3(threatDir.x, 0f, threatDir.z);
            if (threatFlat.sqrMagnitude < 0.001f && soldier.transform != null)
                threatFlat = new Vector3(soldier.transform.forward.x, 0f, soldier.transform.forward.z);
            if (threatFlat.sqrMagnitude < 0.001f)
                threatFlat = Vector3.forward;
            else
                threatFlat.Normalize();

            Vector3 sideAxis = new Vector3(-threatFlat.z, 0f, threatFlat.x);
            float bestScore = -999f;
            bool found = false;

            for (int dirIdx = 0; dirIdx < 5; dirIdx++)
            {
                Vector3 probeDir;
                switch (dirIdx)
                {
                    case 0: probeDir = sideAxis; break;
                    case 1: probeDir = -sideAxis; break;
                    case 2: probeDir = (threatFlat + sideAxis * 0.85f).normalized; break;
                    case 3: probeDir = (threatFlat - sideAxis * 0.85f).normalized; break;
                    default: probeDir = threatFlat; break;
                }

                bool hitWall = Physics.Raycast(myPos + Vector3.up * 0.68f, probeDir, out RaycastHit hit, 6.8f, envMask, QueryTriggerInteraction.Ignore) ||
                               Physics.Raycast(myPos + Vector3.up * 1.05f, probeDir, out hit, 6.8f, envMask, QueryTriggerInteraction.Ignore);
                if (!hitWall || Mathf.Abs(hit.normal.y) >= 0.45f)
                    continue;

                Vector3 pushInward = new Vector3(hit.normal.x, 0f, hit.normal.z);
                if (pushInward.sqrMagnitude < 0.001f)
                    continue;
                pushInward.Normalize();

                Vector3 cand = new Vector3(hit.point.x, myPos.y, hit.point.z) + pushInward * 0.62f;
                if (!AiCore.IsSafeBridgeDeckStep(myPos, cand, out Vector3 snappedDeck))
                    continue;

                float moveDist = Vector3.Distance(myPos, snappedDeck);
                if (moveDist > 8.5f)
                    continue;

                // Score parapet/pillar spot: strong bonus if crouched chest is directly shielded from threatDir,
                // plus lateral wall-hug bonus that clears the exposed bridge centerline.
                bool blocksThreatCrouched = Physics.Raycast(
                    snappedDeck + Vector3.up * 0.62f,
                    threatFlat,
                    2.6f,
                    envMask,
                    QueryTriggerInteraction.Ignore);

                float score = (blocksThreatCrouched ? 12.0f : 4.5f) - moveDist * 0.45f;
                if (score > bestScore)
                {
                    bestScore = score;
                    bestSpot = snappedDeck;
                    found = true;
                }
            }

            return found;
        }

        private static bool CanBotPreferVantageOverwatch(
            Soldier soldier,
            AiCore.SoldierFastHandles h,
            Squad sq,
            Vector3 myPos,
            float curSupp,
            float now,
            out bool isOverwatchRole,
            out bool isDefenderOrGarrison,
            out Vector3 taskCenter,
            out float taskRadius)
        {
            isOverwatchRole = false;
            isDefenderOrGarrison = false;
            taskCenter = Vector3.zero;
            taskRadius = 24f;

            if (soldier == null || h == null || sq == null || soldier.IsOnVehicle() || soldier.isBleeding || soldier.IsCarryingBody() || curSupp >= 42f)
                return false;

            if (soldier.aiController != null && soldier.aiController.HeldMortar != null)
                return false;

            SoldierPersonality pers = GetPersonality(soldier);
            isOverwatchRole = pers >= SoldierPersonality.Gunner_Anchor && pers <= SoldierPersonality.Marksman_Spotter;
            bool isDefensiveRifle = (pers >= SoldierPersonality.Rifleman_Regular && pers <= SoldierPersonality.Rifleman_Grenadier) ||
                                    (pers >= SoldierPersonality.Leader_Spearhead && pers <= SoldierPersonality.Leader_OldGuard);
            if (!isOverwatchRole && !isDefensiveRifle)
                return false;

            SquadTask task = sq.currentTask;
            if (task != null)
            {
                taskCenter = task.GetTaskDestination();
                taskRadius = Mathf.Max(10f, task.GetTaskAreaRadius());
            }

            string fac = AiCore.GetCachedFaction(h, now);
            isDefenderOrGarrison = AiCore.IsAsymmetricDefender(fac) ||
                                   task == null ||
                                   sq.order == Order.defend;

            if (!isDefenderOrGarrison && !isOverwatchRole)
                return false;

            int liveCount = 0;
            int vantageCount = 0;
            IntPtr sqPtr = sq.Pointer;
            int wsCount = AiCore.WorldSoldierCount;
            var ws = AiCore.WorldSoldiers;
            for (int i = 0; i < wsCount; i++)
            {
                if (ws[i].SquadPtr != sqPtr || ws[i].Ptr == soldier.Pointer)
                    continue;

                Soldier m = ws[i].Ref;
                if (m == null || m.IsDead)
                    continue;

                liveCount++;
                AiDestination td = m.targetDestination;
                if (td == null)
                    continue;

                CombatCover mcc = td.TryCast<CombatCover>();
                if (mcc == null || !AiCore.IsCombatCoverIntact(mcc))
                    continue;

                Vector3 mCovPos = mcc.GetCoverPosition();
                if (mCovPos.y >= myPos.y + 1.8f ||
                    (mcc.GetCoverPose() == SoldierPose.Idle && AiCore.HasLowCeilingOverhead(mCovPos, 5.5f)))
                {
                    vantageCount++;
                }
            }

            int maxSlots = isDefenderOrGarrison
                ? (liveCount <= 5 ? 2 : 3)
                : Mathf.Min(2, Mathf.Max(1, (liveCount * 2) / 5));

            return vantageCount < maxSlots;
        }

        private static bool TryUpgradeToVantageOrWindowCover(SoldierAI ai, Soldier soldier, AiCore.SoldierFastHandles h, float now)
        {
            if (ai == null || soldier == null || h == null || h.Tr == null || soldier.joinedSquad == null)
                return false;

            Vector3 myPos = h.Tr.position;
            if (!CanBotPreferVantageOverwatch(
                    soldier,
                    h,
                    soldier.joinedSquad,
                    myPos,
                    soldier.GetSuppressionValue(),
                    now,
                    out bool isOverwatchRole,
                    out bool isDefenderOrGarrison,
                    out Vector3 taskCenter,
                    out float taskRadius))
            {
                return false;
            }

            var octree = CoverManager.staticCovers;
            if (octree == null)
                return false;

            var nearby = octree.GetNearby(myPos, 18.5f);
            if (nearby == null || nearby.Length == 0)
                return false;

            string myFaction = AiCore.GetCachedFaction(h, now);
            Vector3 threatDir = GetEffectiveThreatDirection(soldier, soldier.joinedSquad, myPos, h.Tr.forward);
            var unreachable = soldier.nonReachableCovers;
            float maxObjDist = isDefenderOrGarrison ? (taskRadius * 1.25f + 10f) : (taskRadius * 1.05f);
            float maxObjDistSq = maxObjDist * maxObjDist;

            CombatCover bestVantage = null;
            float bestScore = float.MaxValue;

            int count = nearby.Length;
            for (int i = 0; i < count; i++)
            {
                AiDestination dest = nearby[i];
                if (dest == null || dest.Pointer == IntPtr.Zero)
                    continue;
                if (unreachable != null && unreachable.Contains(dest))
                    continue;

                CombatCover cc = dest.TryCast<CombatCover>();
                if (!AiCore.IsCombatCoverIntact(cc))
                    continue;

                Vector3 covPos = cc.GetCoverPosition();
                float dy = covPos.y - myPos.y;
                if (dy < -1.0f || dy > 7.5f)
                    continue;

                bool isElevated = dy >= 2.0f;
                bool isIndoorWindow = cc.GetCoverPose() == SoldierPose.Idle && AiCore.HasLowCeilingOverhead(covPos, 5.5f);
                if (!isElevated && !isIndoorWindow)
                    continue;

                if (taskCenter != Vector3.zero)
                {
                    float dx = covPos.x - taskCenter.x;
                    float dz = covPos.z - taskCenter.z;
                    if (dx * dx + dz * dz > maxObjDistSq)
                        continue;
                }

                Vector3 covFwd = cc.GetLookRotation() * Vector3.forward;
                covFwd.y = 0f;
                float threatDot = covFwd.sqrMagnitude > 0.001f ? Vector3.Dot(covFwd.normalized, threatDir) : 0f;
                if (threatDot < 0.45f)
                    continue;

                if (!cc.IsCoverAvailable(threatDir, myFaction))
                    continue;

                float dSq = (covPos - myPos).sqrMagnitude;
                float score = dSq * (isElevated ? (isOverwatchRole ? 0.30f : 0.44f) : (isOverwatchRole ? 0.36f : 0.50f));
                if (score < bestScore)
                {
                    bestScore = score;
                    bestVantage = cc;
                }
            }

            if (bestVantage == null)
                return false;

            AiCore.AssignBotCoverOrDestination(ai, soldier, bestVantage.Cast<AiDestination>());
            h.Stance.LocalCoverProtectedUntil = now + 35.0f;
            h.Stance.NextVantageSeekTime = now + 45.0f;
            return true;
        }

        internal static bool EmergentTryDefenderPerimeterPatrol(
            SoldierAI ai,
            Soldier soldier,
            AiCore.SoldierFastHandles h,
            Squad sq,
            Vector3 myPos,
            float now)
        {
            if (ai == null || soldier == null || h == null || sq == null ||
                soldier.IsOnVehicle() || soldier.isBleeding || soldier.IsCarryingBody() ||
                soldier.IsOnCover() || ai.IsMoving() || ai.visibleTarget != null || soldier.GetSuppressionValue() >= 10f ||
                !AiCore.CanBotMoveAutonomously(soldier))
            {
                return false;
            }

            // Skip MGs/Marksmen already in elevated/window overwatch; keep ~12.5% of un-covered squad as roaming perimeter sentries.
            SoldierPersonality pers = GetPersonality(soldier);
            if (pers >= SoldierPersonality.Gunner_Anchor && pers <= SoldierPersonality.Marksman_Spotter)
                return false;
            if ((AiCore.GetDeterministicHash(h.Ptr, 503) & 7) != 0)
                return false;

            if (now < h.Stance.NextVantageSeekTime)
                return false;
            h.Stance.NextVantageSeekTime = now + 45.0f + (AiCore.GetDeterministicHash(h.Ptr, 509) & 15) * 1.5f;

            SquadTask task = sq.currentTask;
            if (task == null)
                return false;

            Vector3 objCenter = task.GetTaskDestination();
            float objRad = Mathf.Max(12f, task.GetTaskAreaRadius());
            if ((objCenter - myPos).sqrMagnitude > (objRad * 1.15f) * (objRad * 1.15f))
                return false;

            var octree = CoverManager.staticCovers;
            if (octree == null)
                return false;

            float phaseAngle = ((int)(now / 22f) + (AiCore.GetDeterministicHash(h.Ptr, 521) & 7)) * 0.785398f;
            Vector3 patrolAnchor = objCenter + new Vector3(Mathf.Cos(phaseAngle), 0f, Mathf.Sin(phaseAngle)) * (objRad * 0.65f);
            var nearby = octree.GetNearby(patrolAnchor, 14.0f);
            if (nearby == null || nearby.Length == 0)
                return false;

            string fac = AiCore.GetCachedFaction(h, now);
            Vector3 threatDir = GetEffectiveThreatDirection(soldier, sq, patrolAnchor, h.Tr != null ? h.Tr.forward : Vector3.forward);
            var unreachable = soldier.nonReachableCovers;
            float maxObjDistSq = (objRad * 1.05f) * (objRad * 1.05f);

            CombatCover bestPatrolCover = null;
            float bestScore = float.MaxValue;
            int count = nearby.Length;
            for (int i = 0; i < count; i++)
            {
                AiDestination dest = nearby[i];
                if (dest == null || dest.Pointer == IntPtr.Zero)
                    continue;
                if (unreachable != null && unreachable.Contains(dest))
                    continue;

                CombatCover cc = dest.TryCast<CombatCover>();
                if (!AiCore.IsCombatCoverIntact(cc))
                    continue;

                Vector3 covPos = cc.GetCoverPosition();
                if (Mathf.Abs(covPos.y - myPos.y) > 6.5f)
                    continue;
                if ((covPos - myPos).sqrMagnitude < 8.0f * 8.0f)
                    continue;
                if ((covPos - objCenter).sqrMagnitude > maxObjDistSq)
                    continue;
                if (!cc.IsCoverAvailable(threatDir, fac))
                    continue;

                float dSq = (covPos - patrolAnchor).sqrMagnitude;
                if (dSq < bestScore)
                {
                    bestScore = dSq;
                    bestPatrolCover = cc;
                }
            }

            if (bestPatrolCover == null)
                return false;

            AiCore.AssignBotCoverOrDestination(ai, soldier, bestPatrolCover.Cast<AiDestination>(), requestImmediatePath: true);
            h.Stance.LocalCoverProtectedUntil = now + 35.0f;
            return true;
        }

        private static bool TrySeekNearbyCover(SoldierAI ai, Soldier soldier)
        {
            AiCore.SoldierFastHandles h = AiCore.GetOrCreateSoldierHandles(soldier);
            if (ai == null || h == null || h.Tr == null || soldier.IsOnVehicle())
                return false;

            if (soldier.Params != null && (!soldier.Params.canMove || !soldier.Params.findCoverWhenSuppressed))
                return false;

            if (!soldier.isBleeding)
            {
                AiCore.CachedDestKind dk = AiCore.ResolveTargetDestination(h);
                if (dk == AiCore.CachedDestKind.MedicHeal || dk == AiCore.CachedDestKind.VehicleRepair)
                    return false;
            }

            if (soldier.IsOnCover())
                return true;

            Vector3 myPos = h.Tr.position;
            float now = Time.time;
            if (now >= h.Stance.NextLocalCoverSeekTime)
            {
                h.Stance.NextLocalCoverSeekTime = now + 1.5f;

                string myFaction = AiCore.GetCachedFaction(h, now);
                bool isOnBridge = VisionConfig.EnableBridgeCrossingDiscipline.Value &&
                                  AiCore.IsOnBridgeOrNarrowCrossing(myPos);
                bool currentlyIndoors = !isOnBridge && AiCore.HasLowCeilingOverhead(myPos, 5.5f);
                Vector3 threatDir = GetEffectiveThreatDirection(
                    soldier,
                    soldier.joinedSquad,
                    myPos,
                    h.Tr.forward,
                    out bool isCrossfire,
                    out bool hasDirectTarget);
                bool indoorsAlreadySheltered = currentlyIndoors &&
                                               IsPositionShelteredByFrontCover(myPos, threatDir, h.Tr.root, 2.35f);
                Vector3 threatOrigin3D;
                if (ai.visibleTarget != null && AiCore.TryGetSpottableCenter(ai.visibleTarget, out Vector3 visPos))
                    threatOrigin3D = visPos;
                else if (VisionPatches.TryGetNearestEnemyGunfirePosition(myPos, myFaction, 200f, 4.0f, out Vector3 gfPos))
                    threatOrigin3D = gfPos + Vector3.up * 1.4f;
                else
                    threatOrigin3D = myPos + threatDir * 28.0f + Vector3.up * 1.8f;

                float curSupp = soldier.GetSuppressionValue();
                bool isOverwatchRole = false;
                bool isDefenderOrGarrison = false;
                Vector3 taskCenter = Vector3.zero;
                float taskRadius = 24f;
                bool wantsVantage = !isOnBridge &&
                                    CanBotPreferVantageOverwatch(
                                        soldier,
                                        h,
                                        soldier.joinedSquad,
                                        myPos,
                                        curSupp,
                                        now,
                                        out isOverwatchRole,
                                        out isDefenderOrGarrison,
                                        out taskCenter,
                                        out taskRadius);
                float maxObjDistSq = 0f;
                if (wantsVantage && taskCenter != Vector3.zero)
                {
                    float maxObjDist = isDefenderOrGarrison ? (taskRadius * 1.25f + 10f) : (taskRadius * 1.05f);
                    maxObjDistSq = maxObjDist * maxObjDist;
                }
                bool inObjExposedDefender = !isOnBridge &&
                                            !soldier.IsOnCover() &&
                                            curSupp < 25f &&
                                            soldier.joinedSquad != null &&
                                            soldier.joinedSquad.currentTask != null &&
                                            soldier.joinedSquad.currentTask.IsInsideTaskArea(myPos, 1.15f) &&
                                            (AiCore.IsAsymmetricDefender(myFaction) || soldier.joinedSquad.order == Order.defend);
                bool hasActiveWater = AiCore.TryGetActiveWaterLevel(out float activeWaterY);
                bool nearBeachOrWaterline = hasActiveWater && myPos.y <= activeWaterY + 4.5f;
                bool onDryGroundAboveWater = hasActiveWater && myPos.y > activeWaterY + 0.55f;
                float earlyPruneScoreMult = (wantsVantage || inObjExposedDefender) ? 0.08f : (nearBeachOrWaterline ? 0.12f : 0.20f);
                CombatCover bestCover = null;
                float bestScoreSq = float.MaxValue;
                float bestRawDistSq = float.MaxValue;

                CombatCover bestCov0 = null, bestCov1 = null, bestCov2 = null;
                Vector3 bestCovPos0 = Vector3.zero, bestCovPos1 = Vector3.zero, bestCovPos2 = Vector3.zero;
                float bestCovScore0 = float.MaxValue, bestCovScore1 = float.MaxValue, bestCovScore2 = float.MaxValue;
                float bestCovDistSq0 = float.MaxValue, bestCovDistSq1 = float.MaxValue, bestCovDistSq2 = float.MaxValue;
                bool bestCovIndoor0 = false, bestCovIndoor1 = false, bestCovIndoor2 = false;

                var octree = CoverManager.staticCovers;
                if (octree != null)
                {
                    Vector3 coverSearchOrigin = inObjExposedDefender ? (myPos + threatDir * 6.0f) : myPos;
                    float coverSearchRadius = inObjExposedDefender ? 26.0f : (wantsVantage ? 18.5f : 13.5f);
                    var nearby = octree.GetNearby(coverSearchOrigin, coverSearchRadius);
                    if (nearby != null && nearby.Length > 0)
                    {
                        var unreachable = soldier.nonReachableCovers;
                        int count = nearby.Length;
                        for (int i = 0; i < count; i++)
                        {
                            AiDestination dest = nearby[i];
                            if (dest == null || dest.Pointer == IntPtr.Zero)
                                continue;
                            if (unreachable != null && unreachable.Contains(dest))
                                continue;

                            CombatCover cc = dest.TryCast<CombatCover>();
                            if (!AiCore.IsCombatCoverIntact(cc))
                                continue;

                            Vector3 covPos = cc.GetCoverPosition();
                            float dy = covPos.y - myPos.y;
                            if (dy < (isOnBridge ? -1.15f : -2.4f) || dy > (isOnBridge ? 1.15f : (wantsVantage ? 7.5f : 2.4f)))
                                continue;

                            if (wantsVantage && dy >= 1.8f && maxObjDistSq > 0f)
                            {
                                float dx = covPos.x - taskCenter.x;
                                float dz = covPos.z - taskCenter.z;
                                if (dx * dx + dz * dz > maxObjDistSq)
                                    continue;
                            }

                            float dSq = (covPos - myPos).sqrMagnitude;
                            if (indoorsAlreadySheltered && dSq > 8.5f * 8.5f)
                                continue;

                            if (dSq * earlyPruneScoreMult >= bestCovScore2)
                                continue;

                            if (isOnBridge && !AiCore.IsSafeBridgeDeckStep(myPos, covPos, out _))
                                continue;

                            if (!cc.IsCoverAvailable(threatDir, myFaction))
                                continue;

                            // When taking fire from multiple sides without a single direct target, do not reject
                            // cover spots just because another wall is behind them; prefer indoor or multi-sided shelter!
                            if (VisionConfig.EnableSmartWallSideCover.Value &&
                                !isCrossfire &&
                                IsPositionOnExposedSideOfWall(covPos, threatDir, null, out _))
                            {
                                continue;
                            }

                            bool isLeewardSheltered = IsPositionShelteredByFrontCover(covPos, threatDir, null);
                            float scoreMult = isLeewardSheltered ? 0.42f : 1.45f;

                            bool isIndoorCov = false;
                            if (!isOnBridge)
                            {
                                if (currentlyIndoors || isCrossfire || (!hasDirectTarget && curSupp >= 22f))
                                {
                                    if (IsPositionInMultiSidedOrIndoorShelter(covPos, out isIndoorCov, out int blockedDirs))
                                    {
                                        if (isIndoorCov)
                                            scoreMult = Mathf.Min(scoreMult, (isCrossfire || curSupp >= 22f) ? 0.22f : 0.35f);
                                        else if (blockedDirs >= 2 && (isCrossfire || (!hasDirectTarget && curSupp >= 22f)))
                                            scoreMult = Mathf.Min(scoreMult, 0.32f);
                                    }
                                }
                                else if (AiCore.HasLowCeilingOverhead(covPos, 5.5f))
                                {
                                    isIndoorCov = true;
                                    scoreMult = Mathf.Min(scoreMult, 0.35f);
                                }
                            }

                            if (indoorsAlreadySheltered && !isIndoorCov)
                                continue;

                            if (currentlyIndoors && !isIndoorCov)
                                scoreMult *= 2.4f;

                            if (wantsVantage)
                            {
                                Vector3 covFwd = cc.GetLookRotation() * Vector3.forward;
                                covFwd.y = 0f;
                                float threatDot = covFwd.sqrMagnitude > 0.001f ? Vector3.Dot(covFwd.normalized, threatDir) : 0f;
                                if (threatDot >= 0.45f)
                                {
                                    if (dy >= 2.0f)
                                        scoreMult *= (isOverwatchRole ? 0.30f : 0.44f);
                                    else if (dy >= 0.9f)
                                        scoreMult *= 0.65f;

                                    if (isIndoorCov && cc.GetCoverPose() == SoldierPose.Idle && threatDot >= 0.50f)
                                        scoreMult *= (isOverwatchRole ? 0.36f : 0.50f);
                                }
                            }

                            float scoreSq = dSq * scoreMult;
                            if (hasActiveWater)
                            {
                                if (covPos.y <= activeWaterY + 0.35f)
                                {
                                    // Discourage retreating down into or selecting surf/waterline covers, while keeping them
                                    // available if a wading soldier truly has zero dry upland cover nearby.
                                    scoreSq = onDryGroundAboveWater ? (scoreSq * 14.0f + 400.0f) : (scoreSq * 6.0f + 120.0f);
                                }
                                else if (nearBeachOrWaterline)
                                {
                                    if (covPos.y > myPos.y + 0.35f)
                                        scoreSq *= 0.62f;
                                    else if (covPos.y < myPos.y - 0.35f)
                                        scoreSq = scoreSq * 3.5f + 49.0f;
                                }
                            }
                            if (scoreSq >= bestCovScore2)
                                continue;

                            if (scoreSq < bestCovScore0)
                            {
                                bestCov2 = bestCov1; bestCovPos2 = bestCovPos1; bestCovScore2 = bestCovScore1; bestCovDistSq2 = bestCovDistSq1; bestCovIndoor2 = bestCovIndoor1;
                                bestCov1 = bestCov0; bestCovPos1 = bestCovPos0; bestCovScore1 = bestCovScore0; bestCovDistSq1 = bestCovDistSq0; bestCovIndoor1 = bestCovIndoor0;
                                bestCov0 = cc;       bestCovPos0 = covPos;      bestCovScore0 = scoreSq;       bestCovDistSq0 = dSq;            bestCovIndoor0 = isIndoorCov;
                            }
                            else if (scoreSq < bestCovScore1)
                            {
                                bestCov2 = bestCov1; bestCovPos2 = bestCovPos1; bestCovScore2 = bestCovScore1; bestCovDistSq2 = bestCovDistSq1; bestCovIndoor2 = bestCovIndoor1;
                                bestCov1 = cc;       bestCovPos1 = covPos;      bestCovScore1 = scoreSq;       bestCovDistSq1 = dSq;            bestCovIndoor1 = isIndoorCov;
                            }
                            else
                            {
                                bestCov2 = cc;       bestCovPos2 = covPos;      bestCovScore2 = scoreSq;       bestCovDistSq2 = dSq;            bestCovIndoor2 = isIndoorCov;
                            }
                        }
                    }
                }

                // Evaluate up to 3 finalists with a single crouched 3D LOS-break raycast (+0.82m) each so bots
                // keep windowed building walls and waist-high parapets while still detecting exposed flanks.
                if (bestCov0 != null)
                {
                    Transform ignoreRoot = h.Tr.root;
                    for (int f = 0; f < 3; f++)
                    {
                        CombatCover cand = f == 0 ? bestCov0 : (f == 1 ? bestCov1 : bestCov2);
                        if (cand == null)
                            break;

                        Vector3 candPos = f == 0 ? bestCovPos0 : (f == 1 ? bestCovPos1 : bestCovPos2);
                        float rawScore = f == 0 ? bestCovScore0 : (f == 1 ? bestCovScore1 : bestCovScore2);
                        float candDistSq = f == 0 ? bestCovDistSq0 : (f == 1 ? bestCovDistSq1 : bestCovDistSq2);
                        bool candIsIndoor = f == 0 ? bestCovIndoor0 : (f == 1 ? bestCovIndoor1 : bestCovIndoor2);

                        Vector3 crouchShelterPos = candPos + Vector3.up * 0.82f;
                        Vector3 toThreat = threatOrigin3D - crouchShelterPos;
                        float dist = toThreat.magnitude;
                        bool breaksLos = dist > 0.6f &&
                                         AiCore.RaycastEnvironment(crouchShelterPos, toThreat / dist, Mathf.Min(dist, 25.0f), ignoreRoot, out _);
                        float losFactor = breaksLos ? 0.48f : (candIsIndoor ? 1.10f : 1.55f);
                        float adjScore = rawScore * losFactor;
                        if (adjScore < bestScoreSq)
                        {
                            bestScoreSq = adjScore;
                            bestRawDistSq = candDistSq;
                            bestCover = cand;
                        }
                    }
                }

                // If already sheltered indoors behind a wall or window sill and no closer indoor node beats it, stay put.
                if (indoorsAlreadySheltered && bestCover == null)
                {
                    h.Stance.NoLocalCoverUntil = 0f;
                    h.Stance.LocalCoverProtectedUntil = Mathf.Max(h.Stance.LocalCoverProtectedUntil, now + 3.5f);
                    return true;
                }

                // Consider nearby idle/wrecked tank or armored vehicle hulls as leeward physical cover if closer/better than static cover!
                if (!currentlyIndoors &&
                    bestRawDistSq > 4.5f * 4.5f &&
                    TryFindTankHullCoverOrEscortSpot(soldier, myPos, threatDir, false, out Vector3 tankCoverPos, out _) &&
                    (!VisionConfig.EnableFriendlyFireAvoidance.Value || !AiCore.IsCoverInPlayerAimLane(tankCoverPos, threatDir)) &&
                    (!isOnBridge || AiCore.IsSafeBridgeDeckStep(myPos, tankCoverPos, out tankCoverPos)))
                {
                    float tankDistSq = (tankCoverPos - myPos).sqrMagnitude;
                    if (tankDistSq * 0.55f < bestScoreSq)
                    {
                        DestinationWithoutCover tankDest = new DestinationWithoutCover(tankCoverPos, SoldierPose.Crouch);
                        h.Stance.NoLocalCoverUntil = 0f;
                        h.Stance.LocalCoverProtectedUntil = now + 6.5f;
                        AiCore.AssignBotCoverOrDestination(ai, soldier, tankDest.Cast<AiDestination>());
                        TankHullCoverUsesCount++;
                        return true;
                    }
                }

                if (bestCover != null)
                {
                    h.Stance.NoLocalCoverUntil = 0f;
                    h.Stance.LocalCoverProtectedUntil = now + 7.5f;
                    AiCore.AssignBotCoverOrDestination(ai, soldier, bestCover.Cast<AiDestination>());
                    return true;
                }

                // Bridge Parapet / Balustrade / Pillar wall-hug fallback when static cover nodes weren't baked onto the span
                if (isOnBridge &&
                    TryFindBridgeParapetOrPillarSpot(soldier, myPos, threatDir, out Vector3 parapetSpot) &&
                    (!VisionConfig.EnableFriendlyFireAvoidance.Value || !AiCore.IsCoverInPlayerAimLane(parapetSpot, threatDir)))
                {
                    DestinationWithoutCover bridgeDest = new DestinationWithoutCover(parapetSpot, SoldierPose.Crouch);
                    h.Stance.NoLocalCoverUntil = 0f;
                    h.Stance.LocalCoverProtectedUntil = now + 6.5f;
                    AiCore.AssignBotCoverOrDestination(ai, soldier, bridgeDest.Cast<AiDestination>());
                    return true;
                }

                h.Stance.NoLocalCoverUntil = now + 8.0f;
                if ((curSupp >= 35f || soldier.isBleeding) &&
                    !isOnBridge &&
                    !soldier.IsOnVehicle() &&
                    soldier.m_pose == SoldierPose.Idle)
                {
                    SetBotPose(ai, soldier, currentlyIndoors ? SoldierPose.Crouch : SoldierPose.Prone, true);
                }
            }

            if (soldier.HasDestinationAssigned)
            {
                if (soldier.IsOnCover())
                {
                    soldier.TryToGetToActualCover();
                    return true;
                }

                AiDestination curDest = soldier.targetDestination;
                CombatCover curCov = curDest != null ? curDest.TryCast<CombatCover>() : null;
                if (curCov != null && !curCov.destroyed)
                {
                    Vector3 curCovPos = curCov.GetCoverPosition();
                    bool staleWaterlineTrap = AiCore.TryGetActiveWaterLevel(out float curWaterY) &&
                                              myPos.y > curWaterY + 0.55f &&
                                              curCovPos.y <= curWaterY + 0.35f;
                    if (!staleWaterlineTrap && (curCovPos - myPos).sqrMagnitude <= 14.0f * 14.0f)
                    {
                        soldier.TryToGetToActualCover();
                        if (!ai.IsMoving() && !ai.HasPath && !ai.HasPathRequest)
                        {
                            if (ai.moveDirectlyToward_end > now)
                                ai.moveDirectlyToward_end = 0f;
                            AiCore.ResetBotPathLockout(ai);
                            ai.UpdatePath();
                        }
                        return ai.IsMoving() || ai.HasPathRequest;
                    }
                }

                if (curCov == null &&
                    curDest != null &&
                    now < h.Stance.LocalCoverProtectedUntil)
                {
                    Vector3 curDestPos = curDest.GetCoverPosition();
                    float curDestDistSq = (curDestPos - myPos).sqrMagnitude;
                    if (curDestDistSq > 1.8f * 1.8f &&
                        curDestDistSq <= 16.0f * 16.0f &&
                        (ai.IsMoving() || ai.HasPath || ai.HasPathRequest || ai.moveDirectlyToward_end > now))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool HasClearLineOfFireAtHeight(Soldier soldier, Vector3 targetPos, float dist, float lowerBarrelHeight, float upperBarrelHeight, float maxRayDist, bool targetIsVehicle = false)
        {
            if (soldier == null || dist <= 1.5f)
                return false;

            return HasClearLineOfFireFromPosition(soldier.transform.position, soldier.transform.root, targetPos, dist, lowerBarrelHeight, upperBarrelHeight, maxRayDist, targetIsVehicle);
        }

        private static bool HasClearLineOfFireFromPosition(
            Vector3 basePos,
            Transform ignoreRoot,
            Vector3 targetPos,
            float dist,
            float lowerBarrelHeight,
            float upperBarrelHeight,
            float maxRayDist,
            bool targetIsVehicle = false)
        {
            if (dist <= 1.5f)
                return false;

            Vector3 lowerOrigin = basePos + Vector3.up * lowerBarrelHeight;
            Vector3 toTargetLower = targetPos - lowerOrigin;
            float lenLower = toTargetLower.magnitude;
            if (lenLower <= 1.0f)
                return true;

            Vector3 dirLower = toTargetLower / lenLower;
            float checkDistLower = Mathf.Min(lenLower - 0.50f, maxRayDist);
            if (checkDistLower > 0.35f)
            {
                if (AiCore.RaycastEnvironment(lowerOrigin - dirLower * 0.10f, dirLower, checkDistLower + 0.10f, ignoreRoot, out _, ignoreVehicles: targetIsVehicle))
                    return false;
            }

            Vector3 upperOrigin = basePos + Vector3.up * upperBarrelHeight;
            Vector3 toTargetUpper = targetPos - upperOrigin;
            float lenUpper = toTargetUpper.magnitude;
            if (lenUpper > 1.0f)
            {
                Vector3 dirUpper = toTargetUpper / lenUpper;
                float checkDistUpper = Mathf.Min(lenUpper - 0.50f, maxRayDist);
                if (checkDistUpper > 0.35f)
                {
                    if (AiCore.RaycastEnvironment(upperOrigin - dirUpper * 0.10f, dirUpper, checkDistUpper + 0.10f, ignoreRoot, out _, ignoreVehicles: targetIsVehicle))
                        return false;
                }
            }

            return true;
        }

        private static float GetJitteredPeekDuration(IntPtr ptr, float baseDuration)
        {
            float jitter = ((AiCore.GetDeterministicHash(ptr, 193) & 31) - 15) * 0.03f; // -0.45s .. +0.48s
            return Mathf.Max(2.6f, baseDuration + jitter);
        }

        private static bool HasClearLineOfFireAndSightFromPosition(
            Vector3 basePos,
            Transform ignoreRoot,
            Vector3 targetPos,
            float dist,
            float lowerBarrelHeight,
            float upperBarrelHeight,
            float maxRayDist,
            float eyeHeight,
            float maxEyeLintelDist,
            bool targetIsVehicle = false)
        {
            if (!HasClearLineOfFireFromPosition(basePos, ignoreRoot, targetPos, dist, lowerBarrelHeight, upperBarrelHeight, maxRayDist, targetIsVehicle))
                return false;

            if (maxEyeLintelDist > 0.25f)
            {
                // Verify local aperture/lintel clearance from actual eye height (Soldier.FacePosition: ~1.56m standing, ~1.04m crouched)
                // toward upper-torso SpotPosition so bots never stand up into blind pillbox headers or low attic/cottage window lintels.
                Vector3 eyeOrigin = basePos + Vector3.up * eyeHeight;
                Vector3 toSpot = (targetPos + Vector3.up * 0.25f) - eyeOrigin;
                float lenEye = toSpot.magnitude;
                if (lenEye > 1.0f)
                {
                    Vector3 dirEye = toSpot / lenEye;
                    float checkEyeDist = Mathf.Min(lenEye - 0.50f, maxEyeLintelDist);
                    if (checkEyeDist > 0.30f &&
                        AiCore.RaycastEnvironment(eyeOrigin - dirEye * 0.08f, dirEye, checkEyeDist + 0.08f, ignoreRoot, out _, ignoreVehicles: targetIsVehicle))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static bool HasClearLineOfFireFromProne(Soldier soldier, Vector3 targetPos, float dist, bool targetIsVehicle = false)
        {
            return HasClearLineOfFireAtHeight(soldier, targetPos, dist, 0.22f, 0.32f, 15.0f, targetIsVehicle);
        }

        private static bool HasClearLineOfFireFromCrouch(Soldier soldier, Vector3 targetPos, float dist, bool targetIsVehicle = false)
        {
            if (soldier == null || dist <= 1.5f)
                return false;

            return HasClearLineOfFireAndSightFromPosition(
                soldier.transform.position,
                soldier.transform.root,
                targetPos,
                dist,
                0.64f,
                0.78f,
                18.0f,
                1.04f,
                2.8f,
                targetIsVehicle);
        }

        private static Vector3 GetOverWallPeekTarget(Spottable target, Vector3 torsoPos)
        {
            if (target == null || target.IsWheeledVehicleOrTank() || target.IsAirVehicle())
                return torsoPos;

            Vector3 spotPos = target.SpotPosition();
            return spotPos.y > torsoPos.y + 0.14f
                ? Vector3.Lerp(torsoPos, spotPos, 0.80f)
                : torsoPos;
        }

        private static bool HasClearLineOfFireFromStanding(Soldier soldier, Vector3 targetPos, float dist, bool targetIsVehicle = false)
        {
            if (soldier == null || dist <= 1.5f)
                return false;

            return HasClearLineOfFireAndSightFromPosition(
                soldier.transform.position,
                soldier.transform.root,
                targetPos,
                dist,
                1.26f,
                1.36f,
                18.0f,
                1.56f,
                3.5f,
                targetIsVehicle);
        }

        private static bool TrySidestepWallObstruction(SoldierAI ai, Soldier soldier, Vector3 targetPos, float dist, Vector3 altTargetPos = default, bool targetIsVehicle = false)
        {
            if (ai == null || soldier == null || soldier.Pointer == IntPtr.Zero || soldier.transform == null)
                return false;

            AiCore.SoldierFastHandles sh = AiCore.GetOrCreateSoldierHandles(soldier);
            if (sh == null || Time.time < sh.Stance.NextWallSidestepTime)
                return false;

            IntPtr ptr = sh.Ptr;
            sh.Stance.NextWallSidestepTime = Time.time + 3.2f;

            Vector3 curPos = soldier.transform.position;
            Vector3 toTargetXZ = targetPos - curPos;
            toTargetXZ.y = 0f;
            if (toTargetXZ.sqrMagnitude < 0.25f)
                return false;
            toTargetXZ.Normalize();

            Vector3 right = new Vector3(toTargetXZ.z, 0f, -toTargetXZ.x);
            Transform myRoot = soldier.transform.root;
            bool preferRight = (AiCore.GetDeterministicHash(ptr, 61) & 1) == 0;
            bool startIndoors = AiCore.HasLowCeilingOverhead(curPos, 5.5f);
            bool hasAlt = altTargetPos.sqrMagnitude > 0.01f && altTargetPos != targetPos;

            int envMask = AiCore.GetSolidEnvironmentMask();
            for (int dIdx = 0; dIdx < 3; dIdx++)
            {
                float stepDist = dIdx == 0 ? 0.90f : (dIdx == 1 ? 1.35f : 1.85f);
                for (int side = 0; side < 2; side++)
                {
                    Vector3 sideDir = ((side == 0) == preferRight) ? right : -right;
                    if (AiCore.IsDirectionBlockedByEnvironment(curPos + Vector3.up * 0.75f, sideDir, stepDist + 0.30f, myRoot))
                        continue;

                    Vector3 candidateXZ = curPos + sideDir * stepDist;
                    Vector3 groundProbe = new Vector3(candidateXZ.x, curPos.y + 1.25f, candidateXZ.z);
                    if (!Physics.Raycast(groundProbe, Vector3.down, out RaycastHit groundHit, 2.6f, envMask, QueryTriggerInteraction.Ignore))
                        continue;

                    if (groundHit.normal.y < 0.65f)
                        continue;

                    Vector3 stepPos = groundHit.point;
                    if (Mathf.Abs(stepPos.y - curPos.y) > 1.20f)
                        continue;

                    // Never sidestep a building defender out through a doorway/wall into the open
                    if (startIndoors && !AiCore.HasLowCeilingOverhead(stepPos, 5.5f))
                        continue;

                    bool crouchStepClear = HasClearLineOfFireAndSightFromPosition(stepPos, myRoot, targetPos, dist, 0.64f, 0.78f, 18.0f, 1.04f, 2.8f, targetIsVehicle) ||
                                           (hasAlt && HasClearLineOfFireAndSightFromPosition(stepPos, myRoot, altTargetPos, dist, 0.64f, 0.78f, 18.0f, 1.04f, 2.8f, targetIsVehicle));
                    bool standStepClear = !crouchStepClear &&
                                          (HasClearLineOfFireAndSightFromPosition(stepPos, myRoot, targetPos, dist, 1.26f, 1.36f, 18.0f, 1.56f, 3.5f, targetIsVehicle) ||
                                           (hasAlt && HasClearLineOfFireAndSightFromPosition(stepPos, myRoot, altTargetPos, dist, 1.26f, 1.36f, 18.0f, 1.56f, 3.5f, targetIsVehicle)));
                    if (crouchStepClear || standStepClear)
                    {
                        sh.Stance.LocalCoverProtectedUntil = Time.time + 6.5f;
                        bool keepIndoorCover = startIndoors && soldier.IsOnCover() && stepDist <= 1.25f;
                        if (!keepIndoorCover && (soldier.IsOnCover() || soldier.HasDestinationAssigned))
                        {
                            SoldierPose stepPose = crouchStepClear ? SoldierPose.Crouch : SoldierPose.Idle;
                            DestinationWithoutCover sideDest = new DestinationWithoutCover(stepPos, stepPose);
                            soldier.CoverPosition(sideDest.Cast<AiDestination>());
                        }
                        if (!keepIndoorCover && (ai.MoveDestination - curPos).sqrMagnitude <= 3.0f * 3.0f)
                        {
                            ai.MoveDestination = stepPos;
                        }
                        AiCore.MoveBotTowardPosition(ai, soldier, stepPos, 0.05f, 2.2f);
                        sh.Stance.NextWallSidestepTime = Time.time + 4.5f;
                        AntiStackNudges++;
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool TryFindCornerLeanDirection(
            Soldier soldier,
            Vector3 aimPos,
            float dist,
            out Soldier.LeanDirection leanDir,
            bool targetIsVehicle = false)
        {
            leanDir = Soldier.LeanDirection.NotLeaning;
            if (soldier == null ||
                soldier.transform == null ||
                soldier.m_pose == SoldierPose.Prone ||
                soldier.UsingBipod() ||
                dist <= 1.8f)
            {
                return false;
            }

            Vector3 myPos = soldier.transform.position;
            Transform myRoot = soldier.transform.root;
            Vector3 toAim = aimPos - myPos;
            toAim.y = 0f;
            if (toAim.sqrMagnitude < 0.25f)
                return false;

            Vector3 fwd = toAim.normalized;
            Vector3 right = new Vector3(fwd.z, 0f, -fwd.x);

            float lowerH, upperH, eyeH, clearH;
            if (soldier.m_pose == SoldierPose.Crouch)
            {
                lowerH = 0.64f;
                upperH = 0.78f;
                eyeH = 1.04f;
                clearH = 1.05f;
            }
            else
            {
                lowerH = 1.26f;
                upperH = 1.36f;
                eyeH = 1.56f;
                clearH = 1.55f;
            }

            float maxCheckDist = Mathf.Min(dist - 0.4f, 18.0f);
            bool preferRight = (AiCore.GetDeterministicHash(soldier.Pointer, 73) & 1) == 0;
            for (int side = 0; side < 2; side++)
            {
                bool tryRight = (side == 0) == preferRight;
                Vector3 sideDir = tryRight ? right : -right;
                if (AiCore.IsDirectionBlockedByEnvironment(myPos + Vector3.up * clearH, sideDir, 0.65f, myRoot))
                    continue;

                if (HasClearLineOfFireAndSightFromPosition(
                        myPos + sideDir * 0.42f,
                        myRoot,
                        aimPos,
                        dist,
                        lowerH,
                        upperH,
                        maxCheckDist,
                        eyeH,
                        2.8f,
                        targetIsVehicle))
                {
                    leanDir = tryRight ? Soldier.LeanDirection.Right : Soldier.LeanDirection.Left;
                    return true;
                }
            }

            return false;
        }

        // =====================================================================
        // Smart Wall-Side Cover & Threat Direction (Stand Leeward of Weapons Fire)
        // =====================================================================
        private static Vector3 GetEffectiveThreatDirection(Soldier soldier, Squad squad, Vector3 referencePos, Vector3 fallbackDir)
        {
            return GetEffectiveThreatDirection(soldier, squad, referencePos, fallbackDir, out _, out _);
        }

        private static Vector3 GetEffectiveThreatDirection(
            Soldier soldier,
            Squad squad,
            Vector3 referencePos,
            Vector3 fallbackDir,
            out bool isCrossfire,
            out bool hasDirectTarget)
        {
            isCrossfire = false;
            hasDirectTarget = false;

            // 1. Direct personal target always takes priority: keep cover between the soldier and the enemy they are engaging!
            if (soldier != null && soldier.aiController != null &&
                AiCore.TryGetSpottableCenter(soldier.aiController.visibleTarget, out Vector3 ownTargetPos))
            {
                Vector3 toTarget = ownTargetPos - referencePos;
                toTarget.y = 0f;
                if (toTarget.sqrMagnitude > 0.25f)
                {
                    hasDirectTarget = true;
                    return toTarget.normalized;
                }
            }

            string faction = soldier != null
                ? AiCore.GetCachedFaction(soldier)
                : (squad != null && squad.Leader != null ? AiCore.GetCachedFaction(squad.Leader) : null);

            bool hasLiveGunfire = VisionPatches.TryGetDominantEnemyGunfireDirection(
                referencePos,
                faction,
                200.0f,
                3.8f,
                out Vector3 gunfireDir,
                out float gunfireDist,
                out isCrossfire);

            // 2. Fresh personal LastKnownTarget (< 5.0s) keeps cover anchored toward the enemy that just ducked,
            //    unless another enemy is firing from much closer (< 22m) on a different flank!
            if (soldier != null &&
                AiCore.TryGetSoldierHandles(soldier.Pointer, out AiCore.SoldierFastHandles sh) &&
                sh.Vision.LastKnownTarget.LastSeenTime > 0f &&
                (Time.time - sh.Vision.LastKnownTarget.LastSeenTime) <= 5.0f)
            {
                Vector3 toLk = sh.Vision.LastKnownTarget.LastSeenPos - referencePos;
                toLk.y = 0f;
                float lkDistSq = toLk.sqrMagnitude;
                if (lkDistSq > 0.25f)
                {
                    Vector3 lkDir = toLk / Mathf.Sqrt(lkDistSq);
                    if (hasLiveGunfire && gunfireDist <= 22.0f && Vector3.Dot(lkDir, gunfireDir) < 0.35f)
                    {
                        isCrossfire = true;
                        return gunfireDir;
                    }
                    hasDirectTarget = true;
                    return lkDir;
                }
            }

            if (squad != null && squad.units != null)
            {
                var units = squad.units;
                int count = units.Count;
                float bestDistSq = float.MaxValue;
                Vector3 bestToEnemy = Vector3.zero;

                for (int i = 0; i < count; i++)
                {
                    Soldier mate = units[i];
                    if (mate == null || !mate.IsAlive || mate.aiController == null) continue;
                    if (!AiCore.TryGetSpottableCenter(mate.aiController.visibleTarget, out Vector3 enemyPos)) continue;

                    Vector3 toEnemy = enemyPos - referencePos;
                    toEnemy.y = 0f;
                    float dSq = toEnemy.sqrMagnitude;
                    if (dSq > 0.25f && dSq < bestDistSq)
                    {
                        bestDistSq = dSq;
                        bestToEnemy = toEnemy;
                    }
                }

                if (bestToEnemy.sqrMagnitude > 0.25f)
                {
                    Vector3 sqEnemyDir = bestToEnemy.normalized;
                    if (hasLiveGunfire && gunfireDist <= 65.0f && Vector3.Dot(sqEnemyDir, gunfireDir) < 0.35f)
                    {
                        isCrossfire = true;
                    }

                    // If an unseen enemy is actively firing much closer to this soldier (< 48m and < 0.65x squad target dist),
                    // take cover on the leeward side of the live gunfire rather than exposing our back to the shooter!
                    if (hasLiveGunfire && gunfireDist <= 48.0f && (gunfireDist * gunfireDist) < bestDistSq * 0.42f)
                        return gunfireDir;

                    return sqEnemyDir;
                }
            }

            if (hasLiveGunfire)
                return gunfireDir;

            if (AiCore.TryGetNearestAudibleEnemyVehicle(referencePos, faction, 90.0f, Time.time, out Vector3 nearVehPos, out _, out bool nearVehIsTank, out bool nearVehMoving) &&
                (nearVehIsTank || nearVehMoving))
            {
                Vector3 toVeh = nearVehPos - referencePos;
                toVeh.y = 0f;
                if (toVeh.sqrMagnitude > 0.25f)
                    return toVeh.normalized;
            }

            if (VisionConfig.EnableThreatSectorConcentration.Value &&
                soldier != null &&
                TryGetKnownThreatSectorDirection(soldier, out Vector3 sectorDir, out bool hasActiveContact) &&
                hasActiveContact &&
                sectorDir.sqrMagnitude > 0.01f)
            {
                return sectorDir;
            }

            if (AiCore.TryGetEnemySpawnDirection(faction, referencePos, out Vector3 enemySpawnDir))
            {
                return enemySpawnDir;
            }

            fallbackDir.y = 0f;
            return fallbackDir.sqrMagnitude > 0.001f ? fallbackDir.normalized : Vector3.forward;
        }

        internal static bool IsPositionShelteredByFrontCover(Vector3 pos, Vector3 threatDir, Transform ignoreRoot, float probeDist = 1.85f)
        {
            if (threatDir.sqrMagnitude < 0.001f)
                return false;

            return AiCore.IsMovementBlockedByObstacle(pos + Vector3.up * 0.46f, threatDir, probeDist) ||
                   AiCore.IsMovementBlockedByObstacle(pos + Vector3.up * 0.85f, threatDir, probeDist);
        }

        /// <summary>
        /// Checks whether <paramref name="pos"/> is inside a roofed building/bunker (<c>HasLowCeilingOverhead(6.5m)</c>)
        /// or surrounded by walls/parapets on multiple sides (trench, crater, or interior corner).
        /// </summary>
        internal static bool IsPositionInMultiSidedOrIndoorShelter(Vector3 pos, out bool isIndoorRoofed, out int blockedDirections)
        {
            isIndoorRoofed = AiCore.HasLowCeilingOverhead(pos, 6.5f);
            blockedDirections = 0;

            Vector3 waist = pos + Vector3.up * 0.62f;
            if (AiCore.IsMovementBlockedByObstacle(waist, Vector3.forward, 2.15f))
                blockedDirections++;
            if (AiCore.IsMovementBlockedByObstacle(waist, Vector3.back, 2.15f))
                blockedDirections++;
            if (AiCore.IsMovementBlockedByObstacle(waist, Vector3.right, 2.15f))
                blockedDirections++;
            if (AiCore.IsMovementBlockedByObstacle(waist, Vector3.left, 2.15f))
                blockedDirections++;

            return isIndoorRoofed || blockedDirections >= 2;
        }

        private static bool IsPositionOnExposedSideOfWall(Vector3 pos, Vector3 threatDir, Transform ignoreRoot, out RaycastHit rearWallHit)
        {
            rearWallHit = default;
            if (threatDir.sqrMagnitude < 0.001f)
                return false;

            if (IsPositionShelteredByFrontCover(pos, threatDir, ignoreRoot))
                return false;

            // Never mistake the interior back wall of a 1- or 2-story building, bunker, barn, or pillbox (<= 6.5m ceiling)
            // for an exposed exterior wall face!
            if (AiCore.HasLowCeilingOverhead(pos, 6.5f))
                return false;

            int obsMask = AiCore.GetMovementObstacleMask();
            bool hasRearWall =
                (Physics.Raycast(pos + Vector3.up * 0.82f, -threatDir, out rearWallHit, 1.95f, obsMask, QueryTriggerInteraction.Ignore) &&
                 Mathf.Abs(rearWallHit.normal.y) < 0.58f) ||
                (Physics.Raycast(pos + Vector3.up * 0.46f, -threatDir, out rearWallHit, 1.95f, obsMask, QueryTriggerInteraction.Ignore) &&
                 Mathf.Abs(rearWallHit.normal.y) < 0.58f);

            if (!hasRearWall)
                return false;

            // If flanked by walls/parapets on the left or right as well (e.g., inside a trench, alcove, or corner),
            // this is multi-sided cover, NOT an exposed freestanding wall face!
            Vector3 right = new Vector3(threatDir.z, 0f, -threatDir.x);
            Vector3 waist = pos + Vector3.up * 0.62f;
            if (AiCore.IsMovementBlockedByObstacle(waist, right, 2.10f) ||
                AiCore.IsMovementBlockedByObstacle(waist, -right, 2.10f))
            {
                return false;
            }

            return true;
        }

        private static bool TryValidateLeewardCandidateGround(
            Vector3 currentPos,
            Vector3 candidateXZ,
            Vector3 threatDir,
            Transform ignoreRoot,
            bool currentIsOutdoor,
            out Vector3 validPos)
        {
            validPos = Vector3.zero;
            int groundMask = AiCore.GetSolidEnvironmentMask();
            Vector3 groundRayStart = new Vector3(candidateXZ.x, currentPos.y + 1.35f, candidateXZ.z);
            if (!Physics.Raycast(groundRayStart, Vector3.down, out RaycastHit groundHit, 3.0f, groundMask, QueryTriggerInteraction.Ignore))
                return false;

            if (groundHit.normal.y < 0.65f || groundHit.collider == null || groundHit.collider.gameObject.layer == 4)
                return false;

            Vector3 candidatePos = groundHit.point;
            if (Mathf.Abs(candidatePos.y - currentPos.y) > 1.50f)
                return false;

            int obsMask = AiCore.GetMovementObstacleMask();
            if (Physics.CheckSphere(candidatePos + Vector3.up * 0.85f, 0.24f, obsMask, QueryTriggerInteraction.Ignore))
                return false;

            Vector3 right = new Vector3(threatDir.z, 0f, -threatDir.x);
            Vector3 chestPos = candidatePos + Vector3.up * 0.80f;
            if (AiCore.IsMovementBlockedByObstacle(chestPos, right, 0.35f) &&
                AiCore.IsMovementBlockedByObstacle(chestPos, -right, 0.35f))
            {
                return false;
            }

            // Allow stepping into an indoor building (HasLowCeilingOverhead) OR behind front cover!
            if (!IsPositionShelteredByFrontCover(candidatePos, threatDir, ignoreRoot) &&
                !AiCore.HasLowCeilingOverhead(candidatePos, 6.5f))
                return false;

            validPos = candidatePos;
            return true;
        }

        private static bool TryFindShelteredSideOfWall(Vector3 currentPos, Vector3 threatDir, Transform ignoreRoot, out Vector3 shelteredPos)
        {
            shelteredPos = Vector3.zero;
            if (!IsPositionOnExposedSideOfWall(currentPos, threatDir, ignoreRoot, out RaycastHit rearWallHit))
                return false;

            bool currentIsOutdoor = !AiCore.IsDirectionBlockedByEnvironment(currentPos + Vector3.up * 0.85f, Vector3.up, 6.0f, ignoreRoot);
            int obsMask = AiCore.GetMovementObstacleMask();

            // 1. Direct Leeward Step-Behind for walls, sandbag parapets, fences, and earthen berms up to 2.85m thick
            Vector3 probeOrigin = rearWallHit.point - threatDir * 3.15f;
            if (Physics.Raycast(probeOrigin, threatDir, out RaycastHit backFaceHit, 3.10f, obsMask, QueryTriggerInteraction.Ignore))
            {
                float wallThickness = Vector3.Distance(rearWallHit.point, backFaceHit.point);
                if (wallThickness >= 0.03f && wallThickness <= 2.85f)
                {
                    Vector3 candidateXZ = backFaceHit.point - threatDir * 0.85f;
                    if (TryValidateLeewardCandidateGround(currentPos, candidateXZ, threatDir, ignoreRoot, currentIsOutdoor, out shelteredPos))
                        return true;
                }
            }

            // 2. Corner Wrap-Around Fallback for solid buildings / thick bunkers (> 2.85m) where stepping straight through isn't possible
            Vector3 right = new Vector3(threatDir.z, 0f, -threatDir.x);
            for (int sideIdx = 0; sideIdx < 2; sideIdx++)
            {
                Vector3 latDir = sideIdx == 0 ? right : -right;
                for (int step = 0; step < 2; step++)
                {
                    float latDist = step == 0 ? 1.95f : 3.20f;
                    if (AiCore.IsMovementBlockedByObstacle(currentPos + Vector3.up * 0.80f, latDir, latDist))
                        break;

                    Vector3 wrapXZ = currentPos + latDir * latDist - threatDir * 1.35f;
                    if (TryValidateLeewardCandidateGround(currentPos, wrapXZ, threatDir, ignoreRoot, currentIsOutdoor, out shelteredPos))
                        return true;
                }
            }

            return false;
        }

        private static void HandleGunnerBipodAndWallMounting(SoldierAI ai, Soldier soldier, AttachmentBipod bip, Vector3 targetPos, float dist)
        {
            if (!VisionConfig.EnableGunnerBipodWallMounting.Value || ai == null || soldier == null || soldier.IsOnVehicle())
                return;

            IntPtr ptr = soldier.Pointer;

            if (soldier.UsingBipod())
            {
                ai.actualPose = soldier.m_pose;
                if (ai.IsMoving() || soldier.IsCharging())
                {
                    soldier.PutAwayBipod(0.30f, false, false, false);
                    return;
                }

                if (ai.HasEnemyAndCanFireAtHim && dist > 2f)
                {
                    Vector3 toTarget = targetPos - soldier.transform.position;
                    if (AiCore.HorizontalAngleToDirection(soldier, toTarget) > 60f)
                    {
                        soldier.PutAwayBipod(0.35f, false, false, false);
                    }
                }
                return;
            }

            if (soldier.IsBipodTransitioning())
            {
                ai.actualPose = soldier.m_pose;
                return;
            }

            bool isOnBridge = VisionConfig.EnableBridgeCrossingDiscipline.Value &&
                              AiCore.IsOnBridgeOrNarrowCrossing(soldier.transform.position);
            AiCore.SoldierFastHandles bipH = AiCore.GetOrCreateSoldierHandles(soldier);
            bool overwatchCoverMount = soldier.IsOnCover() &&
                                       (soldier.isAlerted || (bipH != null && bipH.Vision.StationaryWatch.HasWatchDir));
            bool canEngageOrMountOverWall = ai.HasEnemyAndCanFireAtHim ||
                                            (ai.visibleTarget != null && (soldier.IsOnCover() || soldier.IsTowardWall() || isOnBridge)) ||
                                            overwatchCoverMount;
            float effDist = dist > 0.1f ? dist : (overwatchCoverMount ? 25f : 0f);

            if (bip == null || ai.IsMoving() || !canEngageOrMountOverWall || effDist <= 5.0f)
                return;

            if (bipH == null || Time.time < bipH.Stance.NextBipodDeployCheckTime)
                return;

            bipH.Stance.NextBipodDeployCheckTime = Time.time + 1.25f;

            float maxDeployAngle = isOnBridge ? 36f : 28f;
            if (soldier.isInBodyAnimation || AiCore.HorizontalAngleToDirection(soldier, targetPos - soldier.transform.position) > maxDeployAngle)
                return;

            if (!soldier.CanStartBipodDeploy())
                return;

            if (soldier.m_pose != SoldierPose.Prone)
            {
                if (soldier.ScanBipodSurface(bip, out Vector3 curAnchor, out Collider curSupport) && curSupport != null)
                {
                    SetBotPose(ai, soldier, soldier.m_pose);
                    if (soldier.m_pose == SoldierPose.Idle)
                    {
                        bipH.Stance.BipodStandUntil = Time.time + 4.5f;
                    }
                    soldier.OperateBipod(bip, curAnchor, curSupport, true, false);
                    BipodsDeployed++;
                    return;
                }
            }

            Vector3 fwd = soldier.transform.forward;
            fwd.y = 0f;
            fwd = fwd.sqrMagnitude > 0.001f ? fwd.normalized : Vector3.forward;
            Vector3 right = new Vector3(fwd.z, 0f, -fwd.x);
            Vector3 scanBase = soldier.transform.position + fwd * 0.35f;
            float feetY = soldier.transform.position.y;

            // Waist-high wall / bridge balustrade mount (+0.85m scan -> Crouch)
            {
                Vector3 crouchAnchor = Vector3.zero;
                Collider crouchSupport = null;
                bool foundCrouchMount =
                    soldier.ScanBipodSurfaceAt(bip, scanBase + Vector3.up * 0.85f, right, out crouchAnchor, out crouchSupport) &&
                    crouchSupport != null &&
                    crouchAnchor.y > feetY + 0.45f &&
                    crouchAnchor.y < feetY + 1.05f;

                if (!foundCrouchMount && isOnBridge)
                {
                    for (int side = -1; side <= 1; side += 2)
                    {
                        Vector3 diagFwd = (fwd + right * (0.52f * side)).normalized;
                        Vector3 diagRight = new Vector3(diagFwd.z, 0f, -diagFwd.x);
                        Vector3 diagBase = soldier.transform.position + diagFwd * 0.40f;
                        if (soldier.ScanBipodSurfaceAt(bip, diagBase + Vector3.up * 0.85f, diagRight, out crouchAnchor, out crouchSupport) &&
                            crouchSupport != null &&
                            crouchAnchor.y > feetY + 0.45f &&
                            crouchAnchor.y < feetY + 1.05f)
                        {
                            foundCrouchMount = true;
                            break;
                        }
                    }
                }

                if (foundCrouchMount && (soldier.m_pose != SoldierPose.Crouch || isOnBridge || !ai.HasEnemyAndCanFireAtHim))
                {
                    SetBotPose(ai, soldier, SoldierPose.Crouch, true);
                    bipH.Stance.BipodStandUntil = Time.time + 3.5f;
                    bipH.Stance.NoProneUntil = Time.time + 10.0f;
                    soldier.OperateBipod(bip, crouchAnchor, crouchSupport, true, false);
                    BipodsDeployed++;
                    return;
                }
            }

            if (soldier.m_pose != SoldierPose.Idle &&
                (isOnBridge ||
                 soldier.IsOnCover() ||
                 soldier.IsTowardWall() ||
                 IsMuzzleBlockedByWall(soldier, targetPos, dist, ai.visibleTarget != null && ai.visibleTarget.IsWheeledVehicleOrTank())))
            {
                Vector3 standAnchor = Vector3.zero;
                Collider standSupport = null;
                bool foundStandMount =
                    soldier.ScanBipodSurfaceAt(bip, scanBase + Vector3.up * 1.30f, right, out standAnchor, out standSupport) &&
                    standSupport != null &&
                    standAnchor.y > feetY + 0.92f;

                if (!foundStandMount && isOnBridge)
                {
                    for (int side = -1; side <= 1; side += 2)
                    {
                        Vector3 diagFwd = (fwd + right * (0.52f * side)).normalized;
                        Vector3 diagRight = new Vector3(diagFwd.z, 0f, -diagFwd.x);
                        Vector3 diagBase = soldier.transform.position + diagFwd * 0.40f;
                        if (soldier.ScanBipodSurfaceAt(bip, diagBase + Vector3.up * 1.30f, diagRight, out standAnchor, out standSupport) &&
                            standSupport != null &&
                            standAnchor.y > feetY + 0.92f)
                        {
                            foundStandMount = true;
                            break;
                        }
                    }
                }

                if (foundStandMount)
                {
                    SetBotPose(ai, soldier, SoldierPose.Idle, true);
                    bipH.Stance.BipodStandUntil = Time.time + 4.5f;
                    bipH.Stance.NextStanceDropAllowed = Time.time + 4.5f;
                    bipH.Stance.NoProneUntil = Time.time + 10.0f;
                    soldier.StartCoroutine(soldier.RetryBipodDeployAfterStanding());
                    BipodsDeployed++;
                    return;
                }
            }

            bool canTryProneBipod = Time.time >= bipH.Stance.NoProneUntil &&
                                    !soldier.IsOnCover() &&
                                    !soldier.IsTowardWall();

            if (dist > 8.5f && canTryProneBipod &&
                HasClearLineOfFireFromProne(soldier, targetPos, dist, ai.visibleTarget != null && ai.visibleTarget.IsWheeledVehicleOrTank()))
            {
                if (soldier.m_pose == SoldierPose.Prone)
                {
                    soldier.BipodCheck(true);
                    if (soldier.UsingBipod())
                    {
                        BipodsDeployed++;
                    }
                    else
                    {
                        bipH.Stance.NoProneUntil = Time.time + 12.0f;
                    }
                }
                else
                {
                    SetBotPose(ai, soldier, SoldierPose.Prone);
                }
            }
        }

        // =====================================================================
        // PATCH 1: SoldierAI.SequentialUpdate
        // =====================================================================
        [HarmonyPatch(typeof(SoldierAI), nameof(SoldierAI.SequentialUpdate))]
        public static class SoldierAI_SequentialUpdate_Patch
        {
            public struct SequentialUpdateState
            {
                public AiCore.SoldierFastHandles Handles;
                public bool WasBleeding;
                public bool SkippedForPing;
                public float PrevPointGunEndTime;
                public long VanillaStartStamp;
                public Spottable PrevVisibleTarget;
                public IntPtr PrevVisibleTargetPtr;
                public float PrevVisibleDist;
                public float PrevNextAvailableShotFires;
            }

            public static bool Prefix(SoldierAI __instance, out SequentialUpdateState __state)
            {
                __state = default;
                long modPreStart = HitchProfiler.BeginModScope();
                bool runVanilla = true;
                try
                {
                    if (!VisionConfig.EnableMod.Value || ReferenceEquals(__instance, null) || Time.timeScale <= 0.01f)
                        return true;

                    AiCore.SoldierFastHandles h = AiCore.GetOrCreateSoldierHandlesByAi(__instance);
                    __state.Handles = h;
                    if (!AiCore.IsLocallyOwnedBot(h) || AiCore.IsSoldierInVehicleOrTransition(h.Ref) || h.Ref.HasSurrended())
                        return true;

                    Soldier soldier = h.Ref;
                    __state.PrevPointGunEndTime = soldier.pointGunEndTime;

                    IntPtr seqVisPtr = AiCore.GetRawVisibleTargetPtr(__instance);
                    if (seqVisPtr != IntPtr.Zero &&
                        AiCore.TryGetSoldierHandles(seqVisPtr, out AiCore.SoldierFastHandles seqVisSolH) &&
                        AiCore.IsSoldierNonCombatantOrSurrendered(seqVisSolH.Ref, seqVisSolH))
                    {
                        __instance.visibleTarget = null;
                        __instance.allowFireAtEnemy = false;
                        __instance.targetInWeaponRange = false;
                        __instance.aimingEnemy = false;
                        __instance.shotFiresEnd = 0f;
                        soldier.CurrentVisibleTarget = null;
                        soldier.SetGunTriggerPressed(false);
                        h.Combat.RotateFireFrame.Frame = -1;
                        if (h.Vision.LastKnownTarget.Target != null && h.Vision.LastKnownTarget.Target.Pointer == seqVisPtr)
                            h.Vision.LastKnownTarget = default;
                    }

                    Spottable prevVis = __instance.visibleTarget;
                    __state.PrevVisibleTarget = prevVis;
                    __state.PrevVisibleTargetPtr = (!ReferenceEquals(prevVis, null) && prevVis != null) ? prevVis.Pointer : IntPtr.Zero;
                    __state.PrevVisibleDist = __instance.distFromTarget;
                    __state.PrevNextAvailableShotFires = __instance.nextAvailableShotFires;

                    // Prioritize multiplayer ping & frame-time stability over bot brain cycles:
                    // When hosting a room with elevated frame time, let bots coast/hang for a cycle.
                    if (AiCore.ShouldSkipBotCycleForNetworkPing())
                    {
                        __state.SkippedForPing = true;
                        if (AiCore.IsMultiplayerWithRemotePlayers() &&
                            Time.unscaledDeltaTime > 0.0205f &&
                            (__instance.visibleTarget == null || __instance.distFromTarget > 35f))
                        {
                            // Also skip native SoldierAI.SequentialUpdate (pathfinding/octree cover queries)
                            // for bots not in close combat so the host frame finishes immediately.
                            runVanilla = false;
                            return false;
                        }
                        return true;
                    }

                    bool hasActiveContact = false;
                    bool isSquadAware = soldier.joinedSquad != null && soldier.joinedSquad.IsEnemyAwareOfThisSquad;
                    if (VisionConfig.EnableThreatSectorConcentration.Value)
                    {
                        TryGetKnownThreatSectorDirection(soldier, out _, out hasActiveContact);
                        if (hasActiveContact || isSquadAware || soldier.GetSuppressionValue() >= 3f)
                        {
                            AiCore.RefreshAlertnessWithoutRaisingGun(soldier, 20f);
                        }
                    }

                    // Eliminate vanilla's 2.5s SetAlertedFor statue freeze (Soldier.GetBestVisibleEnemy @ 0x18074B4A8)
                    // while preserving a realistic human Stage 1 visual recognition window:
                    //   - Under active fire: 0.14s - 0.24s (never force a second 1.5s cycle once elapsed)
                    //   - Watching (in cover / bipod / squad aware): 0.18s - 0.32s
                    //   - Caught Out (moving / patrolling out of contact): 0.30s - 0.56s
                    AiCore.SoldierFastHandles preH = h;
                    float nowSeq = Time.time;
                    if (VisionConfig.EnableDynamicHumanReactionTime.Value && nowSeq < soldier.alertEndTime)
                    {
                        float vet01 = GetVeterancy01(soldier);
                        float microJitter = (AiCore.GetDeterministicHash(soldier.Pointer, 173) & 15) * 0.011f;
                        bool isWatching = !__instance.IsMoving() &&
                                          (hasActiveContact || isSquadAware || soldier.IsOnCover() || soldier.UsingBipod() || preH.Vision.StationaryWatch.HasWatchDir);
                        bool underActiveFire = nowSeq < preH.Vision.UnderFireUntil;

                        if (preH.Vision.LastAlertStartSeen <= 0f || soldier.alertStartTime > preH.Vision.LastAlertStartSeen + 0.25f)
                            preH.Vision.LastAlertStartSeen = soldier.alertStartTime;

                        float stage1Delay = underActiveFire
                            ? (Mathf.Lerp(0.24f, 0.14f, vet01) + microJitter * 0.45f)
                            : (isWatching
                                ? (Mathf.Lerp(0.21f, 0.13f, vet01) + microJitter * 0.45f)
                                : (Mathf.Lerp(0.46f, 0.30f, vet01) + microJitter));
                        if (isWatching)
                            stage1Delay *= 0.88f;
                        if (AiCore.IsAsymmetricDefender(soldier))
                            stage1Delay *= 0.85f;
                        float trueElapsed = nowSeq - preH.Vision.LastAlertStartSeen;
                        if (underActiveFire && preH.Vision.UnderFireStartedAt > 0f)
                            trueElapsed = Mathf.Max(trueElapsed, nowSeq - preH.Vision.UnderFireStartedAt);

                        if (trueElapsed >= stage1Delay)
                        {
                            float unlockedStart = nowSeq - 2.55f;
                            if (soldier.alertStartTime > unlockedStart)
                                soldier.alertStartTime = unlockedStart;
                        }
                        else
                        {
                            float maxStartTime = nowSeq - (2.50f - (stage1Delay - trueElapsed));
                            if (soldier.alertStartTime > maxStartTime)
                                soldier.alertStartTime = maxStartTime;
                        }
                    }
                    else
                    {
                        preH.Vision.LastAlertStartSeen = 0f;
                        if (VisionConfig.EnableThreatSectorConcentration.Value &&
                            (hasActiveContact || isSquadAware || soldier.GetSuppressionValue() >= 3f))
                        {
                            float vet01 = GetVeterancy01(soldier);
                            float minAlertElapsed = (!__instance.IsMoving() && hasActiveContact)
                                ? Mathf.Lerp(1.95f, 2.35f, vet01)
                                : Mathf.Lerp(1.65f, 2.15f, vet01);
                            float maxStartTime = nowSeq - minAlertElapsed;
                            if (soldier.alertStartTime > maxStartTime)
                            {
                                soldier.alertStartTime = maxStartTime;
                            }
                        }
                    }

                    if (VisionConfig.EnableEmergentDecisionEngine.Value &&
                        soldier.isBleeding &&
                        !(preH != null && preH.Emergent.HasDecision && preH.Emergent.CurrentDecision.Step0 == EmergentEngine.EmergentActionId.SelfBandage))
                    {
                        // Keep vanilla SequentialUpdate combat-responsive: let H.A.R.M. schedule SelfBandage explicitly!
                        soldier.isBleeding = false;
                        __state.WasBleeding = true;
                    }
                }
                catch { }
                finally
                {
                    HitchProfiler.EndModScope(HitchBucket.ModSeqUpdate, modPreStart);
                    if (runVanilla)
                        __state.VanillaStartStamp = HitchProfiler.Begin();
                }
                return true;
            }

            public static void Postfix(SoldierAI __instance, SequentialUpdateState __state)
            {
                if (__state.VanillaStartStamp > 0L)
                    HitchProfiler.End(HitchBucket.VanSoldierSeq, __state.VanillaStartStamp);

                long modPostStart = HitchProfiler.BeginModScope();
                try
                {
                    if (ReferenceEquals(__instance, null))
                        return;

                    AiCore.SoldierFastHandles h = __state.Handles ?? AiCore.GetOrCreateSoldierHandlesByAi(__instance);
                    Soldier soldier = h != null ? h.Ref : null;
                    if (__state.WasBleeding && soldier != null)
                    {
                        soldier.isBleeding = true;
                    }

                    if (!VisionConfig.EnableMod.Value || Time.timeScale <= 0.01f || h == null || soldier == null || h.Tr == null || !soldier.IsAlive || AiCore.IsAnyHumanPlayerSoldier(soldier))
                        return;

                    if (__state.SkippedForPing || AiCore.ShouldSkipBotCycleForNetworkPing())
                        return;

                    IntPtr ptr = h.Ptr;

                    CheckAndApplyFieldPromotion(soldier);

                    if (!AiCore.IsLocallyOwnedBot(h) || AiCore.IsSoldierInVehicleOrTransition(soldier) || soldier.HasSurrended())
                        return;

                    TickBotInventoryWeightAndDelayedWeaponRefill(h, soldier, Time.time);

                    float vet01 = GetVeterancy01(soldier);
                    SoldierPersonality personality = GetPersonality(soldier);
                    float suppression = soldier.GetSuppressionValue();

                    if (VisionConfig.EnableShellShockSurrender.Value &&
                        suppression >= 52f &&
                        TryEvaluateShellShockSurrender(__instance, soldier, personality, vet01, suppression))
                    {
                        return;
                    }

                    bool isOnBridge = VisionConfig.EnableBridgeCrossingDiscipline.Value &&
                                      AiCore.IsOnBridgeOrNarrowCrossing(h.Tr.position);
                    if (isOnBridge)
                    {
                        h.Stance.NoProneUntil = Time.time + 3.5f;
                    }

                    if (TryHandleLiveGrenadeReflex(__instance, soldier, h, personality, vet01, isOnBridge, Time.time))
                    {
                        return;
                    }

                    GetEffectivePeekParameters(soldier, vet01, out float maxPeekSupp, out float duckBackSupp, out float peekCooldown);

                    float nowTgtHold = Time.time;
                    Spottable curVis = __instance.visibleTarget;
                    IntPtr curVisPtr = (!ReferenceEquals(curVis, null) && curVis != null) ? curVis.Pointer : IntPtr.Zero;

                    // Target Commitment Hysteresis (Big Firefights):
                    // Native Soldier.GetBestVisibleEnemy (0x18074ADC0) wipes CurrentVisibleTarget and picks any enemy
                    // even 0.05m closer with zero hysteresis or aim-angle preference. Hold the current visible target
                    // unless it dies/loses LOS/is blocked, an enemy jumps into CQB range, the bot's active incoming
                    // attacker appears, or a rival is substantially closer.
                    if (__state.PrevVisibleTargetPtr != IntPtr.Zero &&
                        curVisPtr != IntPtr.Zero &&
                        curVisPtr != __state.PrevVisibleTargetPtr &&
                        !AiCore.HasHeldMortarCached(h, __instance, nowTgtHold) &&
                        nowTgtHold >= h.Stance.WallBlockedUntil &&
                        nowTgtHold >= h.Combat.FFBlockedUntil)
                    {
                        Spottable prevVis = __state.PrevVisibleTarget;
                        IntPtr prevPtr = __state.PrevVisibleTargetPtr;
                        bool prevValid = AiCore.IsValidAliveSpottable(prevVis) &&
                                         !prevVis.IsAirVehicle() &&
                                         !(AiCore.TryGetSoldierHandles(prevPtr, out AiCore.SoldierFastHandles prevSolH) &&
                                           AiCore.IsSoldierNonCombatantOrSurrendered(prevSolH.Ref, prevSolH));
                        if (prevValid)
                        {
                            bool prevIsVeh = prevVis.IsWheeledVehicleOrTank();
                            bool newIsVeh = curVis.IsWheeledVehicleOrTank();
                            bool allowHoldKind = (!prevIsVeh || newIsVeh) && !(soldier.isAtUnit && newIsVeh && !prevIsVeh);
                            bool newIsActiveAttacker = nowTgtHold < h.Vision.UnderFireUntil &&
                                                       h.Vision.UnderFireAttackerPtr != IntPtr.Zero &&
                                                       curVisPtr == h.Vision.UnderFireAttackerPtr &&
                                                       prevPtr != h.Vision.UnderFireAttackerPtr;
                            if (allowHoldKind && !newIsActiveAttacker)
                            {
                                Vector3 mySeqPos = h.Tr.position;
                                Vector3 prevCenter = prevVis.GetCenterOfUnit();
                                float prevDist = Vector3.Distance(mySeqPos, prevCenter);
                                float newDist = __instance.distFromTarget;
                                if (newDist <= 0.1f)
                                    newDist = Vector3.Distance(mySeqPos, curVis.GetCenterOfUnit());

                                GenericGun heldGunSeq = soldier.GetHeldGun();
                                bool prevInGunRange = heldGunSeq == null || prevDist <= heldGunSeq.DamageRange();
                                if (prevInGunRange)
                                {
                                    float prevAngle = AiCore.HorizontalAngleToDirection(soldier, prevCenter - mySeqPos);
                                    float newAngle = AiCore.HorizontalAngleToDirection(soldier, curVis.GetCenterOfUnit() - mySeqPos);
                                    bool committed = nowTgtHold < h.Combat.SightReaction.LockedUntil ||
                                                     nowTgtHold < __instance.shotFiresEnd ||
                                                     prevAngle <= 18.0f;
                                    bool newIsCqbEmergency = newDist <= 12.0f && newDist < prevDist - 3.5f;
                                    float requiredAdvantage = committed
                                        ? Mathf.Max(11.0f, prevDist * 0.30f) + ((prevAngle <= 18.0f && newAngle > 40.0f) ? 6.0f : 0f)
                                        : Mathf.Max(5.0f, prevDist * 0.15f);

                                    if (!newIsCqbEmergency && newDist >= prevDist - requiredAdvantage && soldier.CanSee(prevVis))
                                    {
                                        float prevAngleAdj = Corvostudio.Weapons.BulletInstance.CalculateTrajectory(
                                            prevDist, soldier.GetBulletSpeed(), soldier.GetBulletGravity(), out bool prevReach);
                                        if (prevReach)
                                        {
                                            __instance.visibleTarget = prevVis;
                                            soldier.CurrentVisibleTarget = prevVis;
                                            __instance.distFromTarget = prevDist;
                                            __instance.targetAngleAdjust = prevAngleAdj;
                                            __instance.targetInWeaponRange = true;
                                            if (!soldier.IsStealth && !soldier.HasMovementsSlowed())
                                            {
                                                __instance.allowFireAtEnemy = true;
                                                __instance.moveLookingTarget = true;
                                            }
                                            if (nowTgtHold >= h.Combat.ThrowFireHoldUntil && nowTgtHold >= h.Stance.StanceDropHoldFireUntil)
                                            {
                                                __instance.nextAvailableShotFires = Mathf.Min(__instance.nextAvailableShotFires, __state.PrevNextAvailableShotFires);
                                            }
                                            curVis = prevVis;
                                            curVisPtr = prevPtr;
                                        }
                                    }
                                }
                            }
                        }
                    }

                    // Neutralize vanilla SoldierAI.SequentialUpdate's 0.8s-3.0s nextAvailableShotFires freeze (0x18055F225)
                    // on brief 1-tick LOS flickers or wide target changes so RotateAndFireToward's SightReaction is the single
                    // authority for human reaction timing (while preserving grenade-throw and stance-drop fire holds).
                    if (VisionConfig.EnableDynamicHumanReactionTime.Value &&
                        curVisPtr != IntPtr.Zero &&
                        nowTgtHold >= h.Combat.ThrowFireHoldUntil &&
                        nowTgtHold >= h.Stance.StanceDropHoldFireUntil &&
                        __instance.nextAvailableShotFires > __state.PrevNextAvailableShotFires + 0.05f &&
                        __instance.nextAvailableShotFires > nowTgtHold + 0.55f)
                    {
                        __instance.nextAvailableShotFires = Mathf.Max(__state.PrevNextAvailableShotFires, nowTgtHold + 0.12f);
                    }

                    // Record visual contact for 15m Local Contact Callouts & Last-Known Suppression
                    if (__instance.visibleTarget != null)
                    {
                        RecordAndBroadcastVisualContact(soldier, __instance.visibleTarget);
                    }

                    // Lift any indoor or bridge soldier that vanilla AI placed into Prone
                    if (soldier.m_pose == SoldierPose.Prone)
                    {
                        float nowLift = Time.time;
                        if (VisionConfig.EnableIndoorCeilingPronePrevention.Value &&
                            AiCore.HasLowCeilingOverhead(soldier.transform.position, 3.0f))
                        {
                            if (nowLift < h.Stance.BandageLowPoseUntil)
                                h.Stance.BandageTargetPose = SoldierPose.Crouch;
                            SetBotPose(__instance, soldier, SoldierPose.Crouch, true);
                            h.Stance.NoProneUntil = nowLift + 10.0f;
                            if (nowLift >= h.Stance.NextIndoorCeilingCountTime)
                            {
                                h.Stance.NextIndoorCeilingCountTime = nowLift + 5.0f;
                                IndoorPronePreventedCount++;
                            }
                        }
                        else if (isOnBridge)
                        {
                            bool bridgeMoving = __instance.IsMoving() || IsAsyncPathRefreshPending(__instance, soldier, h.Tr.position, nowLift);
                            bool activelyBandaging = nowLift < h.Stance.BandageLowPoseUntil &&
                                                     (soldier.isBleeding || soldier.stopHandsAnim_end > nowLift);
                            SoldierPose bridgeTargetPose = (bridgeMoving && !activelyBandaging) ? SoldierPose.Idle : SoldierPose.Crouch;
                            if (nowLift < h.Stance.BandageLowPoseUntil)
                                h.Stance.BandageTargetPose = SoldierPose.Crouch;
                            SetBotPose(__instance, soldier, bridgeTargetPose, true);
                            h.Stance.NoProneUntil = nowLift + 4.0f;
                        }
                    }

                    bool isPeeking = false;
                    float restoreAt = h.Stance.PeekRestoreTime;
                    if (restoreAt > 0f)
                    {
                        float curSuppNow = soldier.GetSuppressionValue();
                        bool urgentDuck = soldier.IsReloading ||
                                          soldier.MustReload() ||
                                          curSuppNow > duckBackSupp ||
                                          !__instance.HasEnemyAndCanFireAtHim;

                        bool timerExpiredAndCanCrouch = false;
                        if (!urgentDuck && Time.time > restoreAt)
                        {
                            Vector3 peekTargetPos = AiCore.TryGetSpottableCenter(__instance.visibleTarget, out Vector3 validPeekPos)
                                ? validPeekPos
                                : (soldier.transform.position + soldier.transform.forward * Mathf.Max(__instance.distFromTarget, 15f));
                            bool peekTargetIsVeh = __instance.visibleTarget != null && __instance.visibleTarget.IsWheeledVehicleOrTank();
                            bool crouchClearNow = HasClearLineOfFireFromCrouch(soldier, peekTargetPos, __instance.distFromTarget, peekTargetIsVeh);
                            bool calmIndoorWindow = !crouchClearNow &&
                                                    curSuppNow < 12f &&
                                                    AiCore.HasLowCeilingOverhead(soldier.transform.position, 4.2f);
                            if (calmIndoorWindow)
                            {
                                h.Stance.PeekRestoreTime = Time.time + 2.8f;
                            }
                            else
                            {
                                // True Pop-Up / Burst / Duck Rhythm: after firing a burst over an outdoor low wall or trench parapet
                                // (or when taking return fire >= 12 at an indoor window), duck back into Crouch for peekCooldown!
                                timerExpiredAndCanCrouch = true;
                            }
                        }

                        if (urgentDuck || timerExpiredAndCanCrouch)
                        {
                            h.Stance.PeekRestoreTime = 0f;
                            float cdJitter = (AiCore.GetDeterministicHash(ptr, 211) & 15) * 0.05f;
                            h.Stance.NextPeekAllowedTime = Time.time + peekCooldown + cdJitter;
                            if (!__instance.IsMoving() &&
                                !IsAsyncPathRefreshPending(__instance, soldier, h.Tr.position, Time.time) &&
                                soldier.m_pose == SoldierPose.Idle)
                            {
                                SetBotPose(__instance, soldier, SoldierPose.Crouch, true);
                                h.Stance.CombatStanceHoldUntil = Time.time + 5.0f;
                                h.Stance.CombatCrouchLockUntil = Time.time + 3.5f;
                            }
                        }
                        else
                        {
                            isPeeking = true;
                        }
                    }

                    if (personality == SoldierPersonality.Leader_Tactician &&
                        SoldierAI.next_requestingSupport > Time.time + 6.0f)
                    {
                        SoldierAI.next_requestingSupport = Time.time + 6.0f;
                    }

                    bool isEvadingArmor = false;
                    if (VisionConfig.PreventSmallArmsFireAtTanks.Value &&
                        __instance.visibleTarget != null &&
                        __instance.visibleTarget.IsWheeledVehicleOrTank())
                    {
                        Vehicle targetVeh = __instance.visibleTarget.TryCast<Vehicle>();
                        if (targetVeh != null)
                        {
                            TryThrowNormalGrenadeAtVehicle(soldier, targetVeh, __instance.distFromTarget);
                            bool hasStowedAtReady = TryEquipOrHoldStowedAtWeaponAgainstVehicle(soldier, targetVeh, __instance.distFromTarget);

                            bool canEngageVehicle = TryEvaluateVehicleTarget(
                                soldier,
                                targetVeh,
                                __instance.distFromTarget,
                                out Soldier exposedCrew,
                                out _,
                                out _);
                            if (canEngageVehicle)
                            {
                                if (exposedCrew != null)
                                {
                                    Spottable crewSpottable = exposedCrew.Cast<Spottable>();
                                    float crewDist = Vector3.Distance(soldier.transform.position, exposedCrew.transform.position);
                                    __instance.visibleTarget = crewSpottable;
                                    soldier.CurrentVisibleTarget = crewSpottable;
                                    __instance.distFromTarget = crewDist;
                                    GenericGun heldGun = soldier.GetHeldGun();
                                    bool inRange = heldGun == null || heldGun.DamageRange() >= crewDist;
                                    __instance.targetInWeaponRange = inRange;
                                    if (inRange && !soldier.HasMovementsSlowed())
                                    {
                                        __instance.allowFireAtEnemy = true;
                                        __instance.moveLookingTarget = true;
                                    }
                                    RecordAndBroadcastVisualContact(soldier, crewSpottable);
                                    if (Time.time >= h.Combat.NextTankDisciplineCountTime)
                                    {
                                        h.Combat.NextTankDisciplineCountTime = Time.time + 3.0f;
                                        ExposedCrewTargeted++;
                                    }
                                }
                            }
                            if (hasStowedAtReady && __instance.distFromTarget <= 42.0f && soldier.transform != null)
                            {
                                TryStalkTankBlindFlank(__instance, soldier, targetVeh, soldier.transform.position, __instance.distFromTarget, Time.time);
                            }
                            else if (!canEngageVehicle && !hasStowedAtReady)
                            {
                                // Switch any rifleman, MG gunner, or dry AT unit away from an invulnerable tank
                                // to visible enemy infantry advancing with the tank — BUT if the enemy tank is close (<= 52m),
                                // only engage close-quarters infantry (<= 22m) so rifle muzzle flash and gunshot noise don't
                                // immediately attract the tank Commander's periscope onto our position!
                                float maxSafeInfDistNearTank = __instance.distFromTarget <= 52.0f ? 22.0f : 185.0f;
                                if (TryFindVisibleFootEnemy(soldier, out Spottable altEnemy, out float altDist) &&
                                    altDist <= maxSafeInfDistNearTank)
                                {
                                    __instance.visibleTarget = altEnemy;
                                    soldier.CurrentVisibleTarget = altEnemy;
                                    __instance.distFromTarget = altDist;
                                    GenericGun heldGun = soldier.GetHeldGun();
                                    bool inRange = heldGun != null && heldGun.DamageRange() >= altDist;
                                    __instance.targetInWeaponRange = inRange;
                                    if (inRange && !soldier.HasMovementsSlowed())
                                    {
                                        __instance.allowFireAtEnemy = true;
                                        __instance.moveLookingTarget = true;
                                    }
                                    RecordAndBroadcastVisualContact(soldier, altEnemy);
                                }
                                else
                                {
                                    __instance.targetInWeaponRange = false;
                                    soldier.SetGunTriggerPressed(false);
                                    h.Stance.CombatStanceHoldUntil = Time.time + 6.0f;
                                    isEvadingArmor = __instance.distFromTarget <= 55.0f;
                                    if (isEvadingArmor)
                                    {
                                        __instance.visibleTarget = null;
                                    }
                                    if (Time.time >= h.Combat.NextTankDisciplineCountTime)
                                    {
                                        h.Combat.NextTankDisciplineCountTime = Time.time + 3.0f;
                                        UselessTankFirePrevented++;
                                    }
                                }
                            }
                        }

                        // Multi-Target Self-Threat Priority:
                        // 1) Active shooter attacking me > 2) Closer CQB/flanking enemy (<= 18m), with 0.80s anti-jitter lock.
                        float prioNow = Time.time;
                        if (prioNow >= h.Combat.PriorityTargetLockUntil &&
                            !soldier.isInBodyAnimation &&
                            !soldier.isInHandsAnimation &&
                            h.Tr != null &&
                            AiCore.HasUsableSmallArmsAmmo(soldier))
                        {
                            Vector3 myPosPrio = h.Tr.position;
                            Spottable curVisPrio = __instance.visibleTarget;
                            float curDistPrio = curVisPrio != null ? __instance.distFromTarget : 999f;
                            Spottable newPrioTarget = null;
                            float newPrioDist = curDistPrio;

                            if (prioNow < h.Vision.UnderFireUntil &&
                                h.Vision.UnderFireAttacker != null &&
                                h.Vision.UnderFireAttacker.Pointer != IntPtr.Zero &&
                                (curVisPrio == null || curVisPrio.Pointer != h.Vision.UnderFireAttacker.Pointer) &&
                                curDistPrio > 12.0f)
                            {
                                Soldier ufSol = h.Vision.UnderFireAttacker.TryCast<Soldier>();
                                if (ufSol != null && ufSol.IsAlive && !ufSol.HasSurrended() && ufSol.transform != null)
                                {
                                    float ufDist = Vector3.Distance(myPosPrio, ufSol.transform.position);
                                    if (ufDist <= 120.0f && soldier.CanSee(h.Vision.UnderFireAttacker))
                                    {
                                        newPrioTarget = h.Vision.UnderFireAttacker;
                                        newPrioDist = ufDist;
                                    }
                                }
                            }
                            else if (curDistPrio > 22.0f)
                            {
                                string myFacPrio = AiCore.GetCachedFaction(h, prioNow);
                                AiCore.EnsureWorldSnapshot();
                                int wCount = AiCore.WorldSoldierCount;
                                var wSoldiers = AiCore.WorldSoldiers;
                                float bestScoreDist = 18.0f;
                                int canSeeChecks = 0;
                                for (int i = 0; i < wCount && canSeeChecks < 3; i++)
                                {
                                    Soldier cand = wSoldiers[i].Ref;
                                    if (cand == null || !cand.IsAlive || cand.HasSurrended() || cand.Pointer == soldier.Pointer)
                                        continue;
                                    if (!AiCore.AreEnemyFactions(myFacPrio, wSoldiers[i].Faction))
                                        continue;
                                    float cDist = Vector3.Distance(myPosPrio, wSoldiers[i].Pos);
                                    if (cDist > 18.0f)
                                        continue;
                                    float effDist = VisionPatches.HasFiredRecently(cand, 2.5f) ? cDist * 0.75f : cDist;
                                    if (effDist >= bestScoreDist)
                                        continue;
                                    Spottable cSpot = cand.TryCast<Spottable>();
                                    if (cSpot == null)
                                        continue;
                                    canSeeChecks++;
                                    if (soldier.CanSee(cSpot))
                                    {
                                        bestScoreDist = effDist;
                                        newPrioTarget = cSpot;
                                        newPrioDist = cDist;
                                    }
                                }
                            }

                            if (newPrioTarget != null)
                            {
                                __instance.visibleTarget = newPrioTarget;
                                soldier.CurrentVisibleTarget = newPrioTarget;
                                __instance.distFromTarget = newPrioDist;
                                h.Combat.PriorityTargetLockUntil = prioNow + 0.80f;
                                GenericGun heldGun = soldier.GetHeldGun();
                                bool inRange = heldGun != null && heldGun.DamageRange() >= newPrioDist;
                                __instance.targetInWeaponRange = inRange;
                                if (inRange && !soldier.HasMovementsSlowed())
                                {
                                    __instance.allowFireAtEnemy = true;
                                    __instance.moveLookingTarget = true;
                                }
                                RecordAndBroadcastVisualContact(soldier, newPrioTarget);
                            }
                        }
                    }

                    bool isGunner = IsMachineGunnerOrHasBipod(soldier, out AttachmentBipod bipod);
                    bool isHighBipodStanding = Time.time < h.Stance.BipodStandUntil;
                    float dist = __instance.distFromTarget;

                    bool standingOnVehicleHull = !AiCore.IsSoldierInVehicleOrTransition(soldier) &&
                                                 h.Tr != null &&
                                                 AiCore.IsFootBotStandingOnVehicleHull(h.Tr.position);
                    bool inHullStepOff = Time.time < h.Goap.FootStuck.UnstickOverrideUntil;

                    VehicleRepairTask repairTask = null;
                    bool isRepairingVehicle = false;
                    if (VisionConfig.EnableSmartTankCrewRepair.Value &&
                        AiCore.ResolveTargetDestination(h) == AiCore.CachedDestKind.VehicleRepair)
                    {
                        repairTask = h.CachedRepairTask;
                        if (repairTask != null && repairTask.VehicleStillneedsRepair && repairTask.vehToRepair != null)
                        {
                            isRepairingVehicle = true;
                            Vector3 shelterPos = GetShelteredRepairPosition(repairTask.vehToRepair, soldier, out _);
                            repairTask.targetPos = shelterPos;
                            repairTask.pose = SoldierPose.Crouch;
                            if (!standingOnVehicleHull && !inHullStepOff)
                                AiCore.SetBotMoveDestinationAndRefreshPath(__instance, shelterPos, 0.64f);
                        }
                    }

                    bool isHealingTeammate = IsActivelyHealingTeammate(soldier, out MedicHealingDestination activeHealDest);
                    if (isHealingTeammate && activeHealDest != null)
                    {
                        DriveMedicHealingApproach(__instance, soldier, h, activeHealDest, Time.time);
                    }

                    if (!isHealingTeammate &&
                        !isRepairingVehicle &&
                        VisionConfig.EnablePointBlankMelee.Value)
                    {
                        bool hasTargetForBayonet = __instance.HasEnemyAndCanFireAtHim || __instance.visibleTarget != null;
                        bool closeThreatForBayonet =
                            (hasTargetForBayonet && dist > 0.01f && dist <= 48f) ||
                            (Time.time < h.Vision.UnderFireUntil && h.Tr != null &&
                             (h.Tr.position - h.Vision.UnderFireFromPos).sqrMagnitude <= 45f * 45f);
                        bool fanaticAssaultForBayonet =
                            soldier.isAlerted &&
                            (!hasTargetForBayonet || dist <= 85f) &&
                            GetFactionCommandProfile(AiCore.GetCachedFaction(soldier)).BayonetBreachCostMult <= 0.78f;

                        if (soldier.IsCharging() || closeThreatForBayonet || fanaticAssaultForBayonet)
                        {
                            TryEnsureBayonetMounted(soldier);
                        }
                    }

                    bool isStaffingEmplacement = IsActivelyHeadingToEmplacement(soldier, Time.time);
                    bool emergentHandlesCqb = VisionConfig.EnableEmergentDecisionEngine.Value &&
                                              !isRepairingVehicle &&
                                              !isHealingTeammate &&
                                              !isStaffingEmplacement &&
                                              (AiCore.CanBotMoveAutonomously(soldier) || AiCore.CanBotChangePoseAutonomously(soldier));

                    bool inPointBlankMelee = IsEngagedInPointBlankMelee(
                        __instance,
                        soldier,
                        dist,
                        out Soldier meleeEnemy,
                        out _,
                        out _,
                        out float seqStrikeRange,
                        out int seqMeleeSlot);

                    // Single-authority emergence: only run fallback melee when H.A.R.M. isn't handling this bot
                    if (inPointBlankMelee && meleeEnemy != null && !emergentHandlesCqb)
                    {
                        __instance.knifeCharge = soldier.HoldsMeleeWeapon || soldier.HasBayonetReady;
                        __instance.moveLookingTarget = true;

                        if (!soldier.HasBayonetReady &&
                            !soldier.HoldsMeleeWeapon &&
                            soldier.InventoryKnife != null &&
                            !soldier.isInHandsAnimation)
                        {
                            GenericGun heldGun = soldier.GetHeldGun();
                            bool gunEmpty = AiCore.IsHeldGunEmptyOrReloading(soldier, heldGun);
                            if (gunEmpty && AiCore.TryConsumeBotRpcBudget(0.35f))
                            {
                                soldier.ToggleInventoryKnife();
                            }
                        }

                        UpdatePointBlankMeleeEngagement(
                            __instance,
                            soldier,
                            h,
                            meleeEnemy,
                            dist,
                            seqStrikeRange,
                            seqMeleeSlot,
                            Time.time,
                            isPerFrameHook: false);
                    }

                    if (__instance.HasEnemyAndCanFireAtHim || suppression >= 8f)
                    {
                        h.Stance.CombatStanceHoldUntil = Time.time + 6.0f;
                    }

                    // Execute 3-Tier H.A.R.M. Emergent Decision Engine (Single Tactical Authority)
                    if (VisionConfig.EnableEmergentDecisionEngine.Value && !isRepairingVehicle && !isHealingTeammate)
                    {
                        EmergentRuntimeBridge.ExecuteEmergentSoldierTick(
                            __instance,
                            soldier,
                            personality,
                            vet01,
                            suppression,
                            dist,
                            isGunner,
                            isOnBridge,
                            inPointBlankMelee,
                            isEvadingArmor,
                            isStaffingEmplacement);
                    }
                    else if ((isRepairingVehicle || isHealingTeammate) &&
                             (standingOnVehicleHull || inHullStepOff) &&
                             h.Tr != null &&
                             (soldier.Params == null || soldier.Params.canMove))
                    {
                        Squad utilSq = AiCore.GetCachedSquad(h, Time.time);
                        SquadBlackboard utilBoard = GetOrUpdateSquadBlackboard(utilSq, soldier);
                        if (EmergentTryUnstick(__instance, soldier, ptr, ref utilBoard, h.Tr.position, suppression, isOnBridge, dist, Time.time) &&
                            utilSq != null && utilSq.Pointer != IntPtr.Zero)
                        {
                            _squadBlackboards[utilSq.Pointer] = utilBoard;
                        }
                        inHullStepOff = Time.time < h.Goap.FootStuck.UnstickOverrideUntil;
                    }

                    float nowSeqPost = Time.time;
                    if (VisionConfig.EnforceStandingWhenOutOfCombat.Value && !soldier.IsStealth)
                    {
                        __instance.pose = SoldierPose.Idle;

                        if (AiCore.ResolveTargetDestination(h) == AiCore.CachedDestKind.DestinationWithoutCover)
                        {
                            DestinationWithoutCover noCoverDest = h.CachedNoCoverDest;
                            if (noCoverDest != null &&
                                noCoverDest.pose == SoldierPose.Prone &&
                                suppression < 62f &&
                                !ShouldBotChooseOrStayProneForSafety(__instance, soldier, h, false, true, __instance.HasEnemyAndCanFireAtHim, dist, suppression, false, false, nowSeqPost))
                            {
                                noCoverDest.pose = SoldierPose.Crouch;
                            }
                        }
                    }

                    bool hasMoveIntent = HasActiveEscapeOrMoveIntent(__instance, soldier, h, nowSeqPost);
                    bool isEffectivelyMoving = __instance.IsMoving() ||
                                               hasMoveIntent ||
                                               IsAsyncPathRefreshPending(__instance, soldier, h.Tr.position, nowSeqPost);
                    bool inFallbackSprint = IsInIndividualOrGoapFallback(ptr, nowSeqPost);
                    bool isFleeingOrCoverSeeking =
                        inFallbackSprint ||
                        isEvadingArmor ||
                        (h.MoveOverride.Kind != AiCore.BotMoveOverrideKind.None && nowSeqPost < h.MoveOverride.ExpireTime) ||
                        (h.Emergent.HasDecision &&
                         (h.Emergent.CurrentDecision.Step0 == EmergentEngine.EmergentActionId.SeekCover ||
                          h.Emergent.CurrentDecision.Step0 == EmergentEngine.EmergentActionId.FallbackRegroup ||
                          h.Emergent.CurrentDecision.Step0 == EmergentEngine.EmergentActionId.EvadeGrenade));

                    if (!soldier.IsOnVehicle() &&
                        __instance.IsMoving() &&
                        !soldier.IsCharging() &&
                        !isFleeingOrCoverSeeking &&
                        !isHealingTeammate &&
                        !isRepairingVehicle &&
                        !inPointBlankMelee &&
                        !h.Goap.Plan.IsEvacuatingBailedCrew &&
                        !soldier.IsCarryingBody() &&
                        h.Tr != null)
                    {
                        Vector3 smokeMoveDir = __instance.dir;
                        smokeMoveDir.y = 0f;
                        if (smokeMoveDir.sqrMagnitude < 0.01f)
                        {
                            smokeMoveDir = h.Tr.forward;
                            smokeMoveDir.y = 0f;
                        }
                        if (smokeMoveDir.sqrMagnitude >= 0.01f)
                        {
                            Vector3 curFootPos = h.Tr.position;
                            Vector3 smokeProbeEnd = curFootPos + smokeMoveDir.normalized * 12.0f;
                            if (AiCore.IsBlindSmokePathHighRisk(h, curFootPos, smokeProbeEnd, nowSeqPost, false, false, 0f, out _))
                            {
                                __instance.moveDirectlyToward_end = 0f;
                                __instance.Stop(true);
                                h.Stance.CombatStanceHoldUntil = Mathf.Max(h.Stance.CombatStanceHoldUntil, nowSeqPost + 2.5f);
                                if (soldier.m_pose == SoldierPose.Idle && AiCore.CanBotChangePoseAutonomously(soldier))
                                    SetBotPose(__instance, soldier, SoldierPose.Crouch);
                                isEffectivelyMoving = false;
                            }
                        }
                    }

                    bool enemyPlaneOverhead = false;
                    if (!soldier.IsOnVehicle() &&
                        !soldier.IsCharging() &&
                        !isEvadingArmor &&
                        !inFallbackSprint &&
                        !isStaffingEmplacement &&
                        !inPointBlankMelee &&
                        !soldier.isBleeding &&
                        !soldier.IsCarryingBody() &&
                        suppression < 15f &&
                        !soldier.IsSpotted &&
                        nowSeqPost >= h.Vision.UnderFireUntil &&
                        !VisionPatches.HasFiredRecently(soldier, 4.0f) &&
                        h.Tr != null)
                    {
                        Squad sqSeq = AiCore.GetCachedSquad(h, nowSeqPost);
                        SquadBlackboard seqBoard = default;
                        bool hasSeqBoard = !ReferenceEquals(sqSeq, null) &&
                                           sqSeq.Pointer != IntPtr.Zero &&
                                           _squadBlackboards.TryGetValue(sqSeq.Pointer, out seqBoard);
                        bool covertOrder = hasSeqBoard &&
                                           seqBoard.IsAiCovertHoldFireActive &&
                                           nowSeqPost < seqBoard.AiCovertHoldFireExpireTime &&
                                           (ReferenceEquals(sqSeq, null) || !sqSeq.IsEnemyAwareOfThisSquad);

                        bool closeOrNightApproach = false;
                        if (!__instance.HasEnemyAndCanFireAtHim)
                        {
                            bool hasThreatPos = hasSeqBoard && seqBoard.HasActiveThreat;
                            Vector3 threatPos = hasThreatPos
                                ? seqBoard.ThreatPosition
                                : (h.Vision.LastKnownTarget.LastSeenTime > 0f && nowSeqPost - h.Vision.LastKnownTarget.LastSeenTime <= 12.0f
                                    ? h.Vision.LastKnownTarget.LastSeenPos
                                    : default);
                            if (hasThreatPos || (h.Vision.LastKnownTarget.LastSeenTime > 0f && nowSeqPost - h.Vision.LastKnownTarget.LastSeenTime <= 12.0f))
                            {
                                float threatDistSq = (threatPos - h.Tr.position).sqrMagnitude;
                                float night01 = (VisionConfig.EnableRealisticNightDarkness != null && VisionConfig.EnableRealisticNightDarkness.Value)
                                    ? AiCore.NightDarkness01
                                    : 0f;
                                float stealthRadius = night01 >= 0.35f ? 52.0f : 24.0f;
                                closeOrNightApproach = threatDistSq <= stealthRadius * stealthRadius;
                            }
                        }

                        if (AiCore.WorldAirSquadCount > 0 &&
                            !__instance.HasEnemyAndCanFireAtHim &&
                            (ReferenceEquals(sqSeq, null) || (!sqSeq.IsEnemyAwareOfThisSquad && !sqSeq.isMarked && !sqSeq.isHalfMarked && !sqSeq.isMarkedPinPoint)))
                        {
                            string myFac = AiCore.GetCachedFaction(h, nowSeqPost);
                            if (AiCore.IsEnemyAircraftOverhead(h.Tr.position, myFac, 260f, 12f) &&
                                !AiCore.HasLowCeilingOverhead(h.Tr.position, 5.5f))
                            {
                                enemyPlaneOverhead = true;
                            }
                        }

                        if (EvaluateTacticalStealthCrouch(false, covertOrder, false, closeOrNightApproach, enemyPlaneOverhead, false, false, false))
                        {
                            h.Stance.StealthCrouchUntil = Mathf.Max(h.Stance.StealthCrouchUntil, nowSeqPost + 2.5f);
                        }
                    }

                    if (soldier.UsingBipod() || soldier.IsBipodTransitioning())
                    {
                        __instance.actualPose = soldier.m_pose;
                    }
                    else if ((isRepairingVehicle && !standingOnVehicleHull && !inHullStepOff) ||
                             (isHealingTeammate && !standingOnVehicleHull && !inHullStepOff && !isEffectivelyMoving))
                    {
                        SetBotPose(__instance, soldier, SoldierPose.Crouch);
                    }
                    else if (isEffectivelyMoving && (__instance.IsMoving() || isFleeingOrCoverSeeking || !__instance.HasEnemyAndCanFireAtHim || soldier.IsCharging() || inPointBlankMelee))
                    {
                        Vector3 moveEndpoint = GetActualMoveEndpoint(__instance, soldier);
                        float remDistSq = (h.Tr.position - moveEndpoint).sqrMagnitude;
                        bool stealthCrouch = ShouldBotStealthCrouch(__instance, soldier, h, nowSeqPost);

                        if (inPointBlankMelee)
                        {
                            SetBotPose(__instance, soldier, GetDesiredMeleePose(meleeEnemy, dist), true);
                        }
                        else if (soldier.IsCharging() || isEvadingArmor || inFallbackSprint || isStaffingEmplacement)
                        {
                            SetBotPose(__instance, soldier, SoldierPose.Idle, true);
                        }
                        else
                        {
                            ApplyDesiredMovingPose(__instance, soldier, remDistSq, suppression, stealthCrouch);
                        }
                    }
                    else
                    {
                        bool harmOwnsPose = VisionConfig.EnableEmergentDecisionEngine.Value &&
                                            h.Emergent.HasDecision &&
                                            (h.Emergent.CurrentDecision.Step0 == EmergentEngine.EmergentActionId.SelfBandage ||
                                             h.Emergent.CurrentDecision.Step0 == EmergentEngine.EmergentActionId.SeekCover ||
                                             h.Emergent.CurrentDecision.Step0 == EmergentEngine.EmergentActionId.FallbackRegroup);

                        if (inPointBlankMelee)
                        {
                            SetBotPose(__instance, soldier, GetDesiredMeleePose(meleeEnemy, dist), true);
                        }
                        else if (isPeeking || isHighBipodStanding || harmOwnsPose)
                        {
                            if (soldier.m_pose == SoldierPose.Prone && isFleeingOrCoverSeeking && hasMoveIntent)
                                SetBotPose(__instance, soldier, SoldierPose.Crouch, true);
                            else
                                __instance.actualPose = soldier.m_pose;
                        }
                        else if (__instance.HasEnemyAndCanFireAtHim && !soldier.IsCharging())
                        {
                            if (VisionConfig.NeverShootStandingInOpen.Value && dist > 11.0f)
                            {
                                Vector3 targetPos = AiCore.TryGetSpottableCenter(__instance.visibleTarget, out Vector3 validStandTargetPos)
                                    ? validStandTargetPos
                                    : (soldier.transform.position + soldier.transform.forward * Mathf.Max(dist, 15f));

                                bool standTargetIsVeh = __instance.visibleTarget != null && __instance.visibleTarget.IsWheeledVehicleOrTank();
                                Vector3 overWallPeekPos = GetOverWallPeekTarget(__instance.visibleTarget, targetPos);
                                bool crouchClear = HasClearLineOfFireFromCrouch(soldier, targetPos, dist, standTargetIsVeh) ||
                                                   (overWallPeekPos != targetPos && HasClearLineOfFireFromCrouch(soldier, overWallPeekPos, dist, standTargetIsVeh));
                                bool standClear = !crouchClear &&
                                                  (HasClearLineOfFireFromStanding(soldier, targetPos, dist, standTargetIsVeh) ||
                                                   (overWallPeekPos != targetPos && HasClearLineOfFireFromStanding(soldier, overWallPeekPos, dist, standTargetIsVeh)));
                                bool canPeekNow = Time.time >= h.Stance.NextPeekAllowedTime;
                                bool mustStandToClearWall = canPeekNow &&
                                                            !crouchClear &&
                                                            standClear &&
                                                            suppression < maxPeekSupp &&
                                                            !soldier.IsReloading &&
                                                            !soldier.MustReload();

                                if (mustStandToClearWall)
                                {
                                    if (__instance.actualPose != SoldierPose.Idle || soldier.m_pose != SoldierPose.Idle)
                                    {
                                        SetBotPose(__instance, soldier, SoldierPose.Idle);
                                        h.Stance.NoProneUntil = Time.time + 10.0f;
                                        h.Stance.PeekRestoreTime = Time.time + GetJitteredPeekDuration(ptr, 4.0f);
                                        AutoPeeksTriggered++;
                                    }
                                }
                                else if (canPeekNow && !crouchClear && soldier.m_pose == SoldierPose.Idle && suppression < maxPeekSupp && !soldier.IsReloading)
                                {
                                    __instance.actualPose = SoldierPose.Idle;
                                }
                                else
                                {
                                    bool inMidBoundFiringHalt = IsInCommittedFiringHalt(ptr, nowSeqPost);
                                    bool canGoProne = nowSeqPost >= h.Stance.NoProneUntil &&
                                                      !soldier.IsOnCover() &&
                                                      !soldier.IsTowardWall() &&
                                                      (!inMidBoundFiringHalt || isGunner || suppression >= 65f);

                                    bool checkProne = canGoProne &&
                                                      (soldier.m_pose == SoldierPose.Prone ||
                                                       suppression >= 24f ||
                                                       nowSeqPost < h.Vision.UnderFireUntil ||
                                                       (h.Emergent.HasDecision &&
                                                        (h.Emergent.SmoothedHarmNow >= 0.25f || h.Emergent.CurrentDecision.TotalHarm >= 0.25f)) ||
                                                       (isGunner && !soldier.IsOnCover() && dist >= 28f));

                                    bool proneLofClear = checkProne && HasClearLineOfFireFromProne(soldier, targetPos, dist, standTargetIsVeh);
                                    bool harmWantsProne = canGoProne &&
                                                          ShouldBotChooseOrStayProneForSafety(
                                                              __instance,
                                                              soldier,
                                                              h,
                                                              inCoverOrShelteredWall: false,
                                                              hasClearProneLof: proneLofClear,
                                                              hasVisibleEnemy: true,
                                                              enemyDist: dist,
                                                              suppression: suppression,
                                                              enemyPlaneOverhead: false,
                                                              wantsToMoveOrWinPush: inMidBoundFiringHalt && suppression < 65f && !isGunner,
                                                              now: nowSeqPost);

                                    bool shouldBeProne = suppression >= 62f ||
                                                         (isGunner && !soldier.IsOnCover() && dist >= 28f);

                                    SoldierPose combatPose = SoldierPose.Crouch;
                                    if (canGoProne &&
                                        ((shouldBeProne && proneLofClear) ||
                                         (harmWantsProne && (proneLofClear || suppression >= 62f || (h.Emergent.HasDecision && h.Emergent.CurrentDecision.TotalHarm >= 0.72f)))))
                                    {
                                        combatPose = SoldierPose.Prone;
                                    }

                                    if (__instance.actualPose != combatPose || soldier.m_pose != combatPose)
                                    {
                                        SetBotPose(__instance, soldier, combatPose);
                                        if (Time.time >= h.Stance.NextStanceDropAllowed)
                                        {
                                            h.Stance.NextStanceDropAllowed = Time.time + 3.5f;
                                            SelfPreservationDrops++;
                                        }
                                    }
                                }
                            }
                            else
                            {
                                __instance.actualPose = soldier.m_pose;
                            }
                        }
                        else if (VisionConfig.EnforceStandingWhenOutOfCombat.Value)
                        {
                            float seqNow = Time.time;
                            if (!soldier.IsOnVehicle() && AiCore.CanBotMoveAutonomously(soldier))
                            {
                                if (seqNow >= h.Stance.NextCoverIntegrityCheckTime)
                                {
                                    h.Stance.NextCoverIntegrityCheckTime = seqNow + 1.5f;
                                    AiCore.ResolveTargetDestination(h);
                                    if (h.DestKind == AiCore.CachedDestKind.CombatCover && h.CachedCover != null && !h.CachedCover.IsVehicle())
                                    {
                                        bool coverGone = !AiCore.IsCombatCoverIntact(h.CachedCover);
                                        if (!coverGone &&
                                            soldier.IsOnCover() &&
                                            h.CachedCover.localLookAngleWideness < 175f &&
                                            h.CachedCover.GetCoverPose() != SoldierPose.Prone)
                                        {
                                            Vector3 chkCovPos = h.CachedCover.GetCoverPosition();
                                            if (!AiCore.HasLowCeilingOverhead(chkCovPos, 5.5f))
                                            {
                                                Vector3 chkFwd = h.CachedCover.GetLookRotation() * Vector3.forward;
                                                chkFwd.y = 0f;
                                                if (chkFwd.sqrMagnitude > 0.001f &&
                                                    !IsPositionShelteredByFrontCover(chkCovPos, chkFwd.normalized, h.Tr.root, 1.85f))
                                                {
                                                    coverGone = true;
                                                }
                                            }
                                        }

                                        if (coverGone)
                                        {
                                            soldier.targetDestination = null;
                                            AiCore.InvalidateTargetDestinationCache(soldier);
                                            h.Stance.LocalCoverProtectedUntil = 0f;
                                            h.Stance.NextLocalCoverSeekTime = 0f;
                                            if (suppression >= 15f || seqNow < h.Vision.UnderFireUntil || __instance.HasEnemyAndCanFireAtHim)
                                            {
                                                TrySeekNearbyCover(__instance, soldier);
                                            }
                                        }
                                    }
                                }

                                if (!__instance.IsMoving() &&
                                    suppression < 20f &&
                                    !__instance.HasEnemyAndCanFireAtHim &&
                                    seqNow >= h.Stance.NextVantageSeekTime)
                                {
                                    h.Stance.NextVantageSeekTime = seqNow + 20.0f;
                                    CombatCover curCov = AiCore.ResolveTargetDestination(h) == AiCore.CachedDestKind.CombatCover
                                        ? h.CachedCover
                                        : null;
                                    bool settledInIntactCover = soldier.IsOnCover() && curCov != null && AiCore.IsCombatCoverIntact(curCov);
                                    SoldierPersonality vantagePers = GetPersonality(soldier);
                                    bool isOverwatchRole = vantagePers >= SoldierPersonality.Gunner_Anchor && vantagePers <= SoldierPersonality.Marksman_Spotter;
                                    bool alreadyVantage = curCov != null &&
                                                          AiCore.IsCombatCoverIntact(curCov) &&
                                                          curCov.GetCoverPose() == SoldierPose.Idle &&
                                                          AiCore.HasLowCeilingOverhead(h.Tr.position, 5.5f);
                                    if (!alreadyVantage &&
                                        (!settledInIntactCover || isOverwatchRole) &&
                                        !TryUpgradeToVantageOrWindowCover(__instance, soldier, h, seqNow))
                                    {
                                        h.Stance.NextVantageSeekTime = seqNow + 35.0f;
                                    }
                                }
                            }

                            bool inCombatHold = Time.time < h.Stance.CombatStanceHoldUntil;
                            bool stealthCrouch = ShouldBotStealthCrouch(__instance, soldier, h, nowSeqPost);
                            CombatCover activeCover = AiCore.ResolveTargetDestination(h) == AiCore.CachedDestKind.CombatCover
                                ? h.CachedCover
                                : null;
                            Vector3 myPos = h.Tr.position;
                            Transform myRoot = h.RootTr;
                            if (ReferenceEquals(myRoot, null))
                            {
                                myRoot = h.Tr.root;
                                h.RootTr = myRoot;
                            }
                            Squad seqPostSq = AiCore.GetCachedSquad(h, nowSeqPost);
                            bool onActualCombatCover = soldier.IsOnCover() ||
                                                       (activeCover != null && (myPos - activeCover.GetCoverPosition()).sqrMagnitude <= 3.2f * 3.2f);
                            SquadTask currentTask = seqPostSq != null ? seqPostSq.currentTask : null;
                            bool inObjectiveZone = currentTask != null && currentTask.IsInsideTaskArea(myPos, 1.15f);
                            bool isSquadAlerted = soldier.isAlerted || (seqPostSq != null && seqPostSq.IsEnemyAwareOfThisSquad);
                            Vector3 threatDir = GetEffectiveThreatDirection(soldier, seqPostSq, myPos, h.Tr.forward);
                            bool isShelteredByWall = VisionConfig.EnableSmartWallSideCover.Value &&
                                                     IsPositionShelteredByFrontCover(myPos, threatDir, myRoot);
                            bool hasMortar = AiCore.HasHeldMortarCached(h, __instance, nowSeqPost);

                            bool holdDefensiveCrouch = onActualCombatCover || inObjectiveZone || isShelteredByWall || isSquadAlerted;
                            bool canAttemptStand = !soldier.IsReloading &&
                                                   !soldier.MustReload() &&
                                                   !soldier.isBleeding &&
                                                   !stealthCrouch &&
                                                   !hasMortar &&
                                                   suppression < duckBackSupp;
                            bool shouldStandAtCoverOrTrench = false;
                            if ((holdDefensiveCrouch || inCombatHold) && canAttemptStand)
                            {
                                bool isIndoor = AiCore.HasLowCeilingOverhead(myPos, 4.2f);
                                bool inOutdoorTrenchWindow = false;
                                if (!isIndoor)
                                {
                                    float cycleLen = (isSquadAlerted || inCombatHold) ? 7.2f : 8.6f;
                                    float phaseOffset = (AiCore.GetDeterministicHash(ptr, 197) & 127) * (cycleLen / 128f);
                                    bool canPeekTrenchNow = Time.time >= h.Stance.NextPeekAllowedTime;
                                    inOutdoorTrenchWindow = canPeekTrenchNow &&
                                                            suppression < duckBackSupp * 0.85f &&
                                                            ((Time.time + phaseOffset) % cycleLen) < 3.4f;
                                }

                                if (isIndoor || inOutdoorTrenchWindow)
                                {
                                    Vector3 lookFwd = h.Tr.forward;
                                    lookFwd.y = 0f;
                                    lookFwd = lookFwd.sqrMagnitude > 0.001f ? lookFwd.normalized : threatDir;

                                    bool lowCoverBlocked = AiCore.RaycastEnvironment(myPos + Vector3.up * 0.76f, lookFwd, 2.2f, myRoot, out _);
                                    bool lintelClear = !AiCore.RaycastEnvironment(myPos + Vector3.up * 1.56f, lookFwd, 2.6f, myRoot, out _);
                                    bool standLipClear = lowCoverBlocked &&
                                                         lintelClear &&
                                                         !AiCore.RaycastEnvironment(myPos + Vector3.up * 1.36f, lookFwd, 5.5f, myRoot, out _);
                                    bool coverSuggestsStand = onActualCombatCover &&
                                                              activeCover != null &&
                                                              activeCover.GetCoverPose() == SoldierPose.Idle &&
                                                              lintelClear;

                                    if (isIndoor)
                                    {
                                        // Indoor Window (lintel > 1.56m): stand to watch out window when unsuppressed;
                                        // pillboxes and low cottage/attic slits (lintel <= 1.56m) fail lintelClear and stay crouched!
                                        shouldStandAtCoverOrTrench = standLipClear || coverSuggestsStand;
                                    }
                                    else
                                    {
                                        // Outdoor Waist-High Wall (~0.75m-1.00m): if crouched eye height (~1.04m) ALREADY clears the rim,
                                        // stay crouched behind the sandbags/wall while scanning and only pop up when firing!
                                        // Outdoor Trench / Chest-High Parapet (~1.05m-1.40m): staggered sentry peek cycle (2-3 squadmates at a time).
                                        bool crouchEyeBlocked = lowCoverBlocked &&
                                                                AiCore.RaycastEnvironment(myPos + Vector3.up * 1.04f, lookFwd, 2.3f, myRoot, out _);
                                        shouldStandAtCoverOrTrench = (crouchEyeBlocked && standLipClear) || coverSuggestsStand;
                                    }
                                }
                            }

                            bool inCoverOrWall = onActualCombatCover || isShelteredByWall;
                            bool wantSafetyProne = !hasMortar &&
                                                   !soldier.isBleeding &&
                                                   !soldier.IsTowardWall() &&
                                                   nowSeqPost >= h.Stance.NoProneUntil &&
                                                   ShouldBotChooseOrStayProneForSafety(
                                                       __instance,
                                                       soldier,
                                                       h,
                                                       inCoverOrShelteredWall: inCoverOrWall,
                                                       hasClearProneLof: true,
                                                       hasVisibleEnemy: false,
                                                       enemyDist: dist,
                                                       suppression: suppression,
                                                       enemyPlaneOverhead: enemyPlaneOverhead && !inCombatHold,
                                                       wantsToMoveOrWinPush: false,
                                                       now: nowSeqPost);
                            if (wantSafetyProne)
                            {
                                __instance.pose = SoldierPose.Prone;
                                if (soldier.m_pose == SoldierPose.Prone)
                                    __instance.actualPose = SoldierPose.Prone;
                                else
                                    SetBotPose(__instance, soldier, SoldierPose.Prone);
                            }
                            else if (stealthCrouch && !hasMortar)
                            {
                                __instance.pose = SoldierPose.Crouch;
                                if (__instance.actualPose != SoldierPose.Crouch || soldier.m_pose != SoldierPose.Crouch)
                                    SetBotPose(__instance, soldier, SoldierPose.Crouch);
                            }
                            else if (shouldStandAtCoverOrTrench)
                            {
                                SetBotPose(__instance, soldier, SoldierPose.Idle);
                            }
                            else if (!holdDefensiveCrouch && !inCombatHold && suppression < 8f && !stealthCrouch && !hasMortar)
                            {
                                SetBotPose(__instance, soldier, SoldierPose.Idle);
                            }
                            else if (suppression < 35f && !stealthCrouch && !hasMortar)
                            {
                                __instance.pose = SoldierPose.Crouch;
                                if (__instance.actualPose != SoldierPose.Crouch || soldier.m_pose != SoldierPose.Crouch)
                                {
                                    SetBotPose(__instance, soldier, SoldierPose.Crouch);
                                }
                            }
                        }
                    }

                    if (isGunner && !isEffectivelyMoving)
                    {
                        Vector3 targetPos = AiCore.TryGetSpottableCenter(__instance.visibleTarget, out Vector3 validGunnerPos)
                            ? validGunnerPos
                            : (h.Tr.position + h.Tr.forward * 25f);
                        HandleGunnerBipodAndWallMounting(__instance, soldier, bipod, targetPos, dist);
                    }

                    bool isCornerLeaning = false;
                    if (VisionConfig.EnableAutoPeekOverCover.Value && !soldier.IsOnVehicle())
                    {
                        float nowLean = Time.time;
                        if (h.Stance.DesiredLean != Soldier.LeanDirection.NotLeaning)
                        {
                            if (nowLean < h.Stance.LeanHoldUntil &&
                                !isEffectivelyMoving &&
                                soldier.m_pose != SoldierPose.Prone &&
                                !soldier.UsingBipod() &&
                                !soldier.IsReloading &&
                                !soldier.MustReload() &&
                                !soldier.isBleeding &&
                                suppression < duckBackSupp)
                            {
                                soldier.LeanState = h.Stance.DesiredLean;
                                isCornerLeaning = true;
                            }
                            else
                            {
                                h.Stance.DesiredLean = Soldier.LeanDirection.NotLeaning;
                                h.Stance.LeanHoldUntil = 0f;
                                h.Stance.NextPeekAllowedTime = Mathf.Max(h.Stance.NextPeekAllowedTime, nowLean + peekCooldown);
                                soldier.LeanState = Soldier.LeanDirection.NotLeaning;
                            }
                        }
                        else if (!isEffectivelyMoving &&
                                 soldier.m_pose != SoldierPose.Prone &&
                                 !soldier.UsingBipod() &&
                                 !soldier.IsReloading &&
                                 !soldier.MustReload() &&
                                 !soldier.isBleeding &&
                                 !inPointBlankMelee &&
                                 suppression < duckBackSupp &&
                                 nowLean >= h.Stance.NextLeanProbeTime &&
                                 nowLean >= h.Stance.NextPeekAllowedTime)
                        {
                            h.Stance.NextLeanProbeTime = nowLean + 1.15f;
                            Vector3 leanOriginPos = h.Tr.position;
                            Vector3 laneAimPos = Vector3.zero;
                            float laneDist = 0f;
                            bool hasLane = false;
                            bool laneTargetIsVeh = false;

                            if (__instance.visibleTarget != null &&
                                AiCore.TryGetSpottableCenter(__instance.visibleTarget, out Vector3 visCenter))
                            {
                                laneAimPos = visCenter;
                                laneDist = dist > 1.0f ? dist : Vector3.Distance(leanOriginPos, laneAimPos);
                                hasLane = laneDist >= 2.2f && laneDist <= 85.0f;
                                laneTargetIsVeh = __instance.visibleTarget.IsWheeledVehicleOrTank();
                            }
                            else if (h.Vision.LastKnownTarget.LastSeenTime > 0f &&
                                     (nowLean - h.Vision.LastKnownTarget.LastSeenTime) <= 12.0f)
                            {
                                laneAimPos = h.Vision.LastKnownTarget.LastSeenPos + Vector3.up * 0.95f;
                                laneDist = Vector3.Distance(leanOriginPos, laneAimPos);
                                hasLane = laneDist >= 4.0f && laneDist <= 80.0f;
                            }
                            else if (soldier.isAlerted || nowLean < h.Stance.CombatStanceHoldUntil)
                            {
                                Squad leanSq = AiCore.GetCachedSquad(h, nowLean);
                                Vector3 laneDirGuess = GetEffectiveThreatDirection(soldier, leanSq, leanOriginPos, h.Tr.forward);
                                if (laneDirGuess.sqrMagnitude > 0.01f)
                                {
                                    laneAimPos = leanOriginPos + laneDirGuess * 16.0f + Vector3.up * 1.0f;
                                    laneDist = 16.0f;
                                    hasLane = true;
                                }
                            }

                            if (hasLane)
                            {
                                Vector3 flatLane = laneAimPos - leanOriginPos;
                                flatLane.y = 0f;
                                if (flatLane.sqrMagnitude > 0.25f)
                                {
                                    Vector3 laneDir = flatLane.normalized;
                                    float centerH = soldier.m_pose == SoldierPose.Crouch ? 0.90f : 1.42f;
                                    if (AiCore.RaycastEnvironment(leanOriginPos + Vector3.up * centerH, laneDir, 2.2f, h.RootTr, out _) &&
                                        TryFindCornerLeanDirection(soldier, laneAimPos, laneDist, out Soldier.LeanDirection laneLean, laneTargetIsVeh))
                                    {
                                        h.Stance.DesiredLean = laneLean;
                                        h.Stance.LeanHoldUntil = nowLean + GetJitteredPeekDuration(ptr, 3.2f);
                                        soldier.LeanState = laneLean;
                                        isCornerLeaning = true;
                                        AutoPeeksTriggered++;
                                    }
                                }
                            }
                        }
                    }

                    if (!inPointBlankMelee && !isPeeking && !isCornerLeaning && !__instance.IsMoving())
                    {
                        TryApplyAntiStackSpacingStep(__instance, soldier, h, h.Tr.position, isOnBridge, Time.time, 3.5f);
                    }

                    // Stabilize upper-body aiming posture (pointGunEndTime) vs wall proximity (isTowardWallTps):
                    // Native SoldierAI.SequentialUpdate (0x18055F7F2) and GetBestVisibleEnemy (0x18074AEEC) call
                    // SetAlertedFor(10f) whenever visibleTarget != null, even if targetInWeaponRange is false,
                    // the target is an invulnerable tank, or the soldier's muzzle is blocked by cover.
                    bool isWallBlockedNow = Time.time < h.Stance.WallBlockedUntil;
                    bool activelyPeekingOrLeaning = isPeeking || isCornerLeaning;
                    if (!__instance.HasEnemyAndCanFireAtHim || (isWallBlockedNow && !activelyPeekingOrLeaning))
                    {
                        soldier.pointGunEndTime = (soldier.isTowardWallTps || (isWallBlockedNow && !activelyPeekingOrLeaning))
                            ? Mathf.Min(__state.PrevPointGunEndTime, Time.time)
                            : __state.PrevPointGunEndTime;
                    }
                    else if (!isWallBlockedNow && soldier.isTowardWallTps)
                    {
                        soldier.isTowardWallTps = false;
                    }
                }
                catch { }
                finally
                {
                    HitchProfiler.EndModScope(HitchBucket.ModSeqUpdate, modPostStart);
                }
            }
        }

        // =====================================================================
        // PATCH 2: SoldierAI.ProcessAiAccuracy
        // =====================================================================
        [HarmonyPatch(typeof(SoldierAI), nameof(SoldierAI.ProcessAiAccuracy))]
        public static class SoldierAI_ProcessAiAccuracy_Patch
        {
            public static void Postfix(Soldier __0, ref float __result)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value || __0 == null || __result <= 0f)
                        return;

                    float vet01 = GetVeterancy01(__0);
                    float spreadMult = 1.0f;

                    if (VisionConfig.EnableVeteranSystem.Value && vet01 > 0.02f)
                    {
                        float maxReduction = Mathf.Clamp(VisionConfig.VeteranMaxSpreadReduction.Value, 0f, 0.40f);
                        spreadMult *= (1.0f - (vet01 * maxReduction));
                    }

                    SoldierPersonality pers = GetPersonality(__0);
                    if (pers == SoldierPersonality.Marksman_Deadeye && __0.m_pose >= SoldierPose.Crouch)
                    {
                        spreadMult *= 0.88f;
                    }
                    else if (pers == SoldierPersonality.Rifleman_Cautious && __0.GetSuppressionValue() > 28f)
                    {
                        float cure = VisionConfig.VeterancyCuresSkittishness.Value ? vet01 : 0f;
                        spreadMult *= Mathf.Lerp(1.12f, 1.00f, cure);
                    }

                    __result *= spreadMult;
                }
                catch { }
            }
        }

        // =====================================================================
        // PATCH 3: SoldierAI.OnSuppressed
        // =====================================================================
        [HarmonyPatch(typeof(SoldierAI), nameof(SoldierAI.OnSuppressed))]
        public static class SoldierAI_OnSuppressed_Patch
        {
            public static bool Prefix(SoldierAI __instance)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value || __instance == null)
                        return true;

                    Soldier soldier = __instance.character;
                    if (!AiCore.IsLocallyOwnedBot(soldier))
                        return true;

                    float now = Time.time;
                    // Prevent vanilla OnSuppressed (0x18055DE16) from calling soldier.GetDestinationInsideArea() -> CoverPosition(nearestTree)
                    // when the bot is actively executing a protected raw-position override (bailed-crew evacuation, fallback sprint, casualty drag, etc.)!
                    if (TryGetProtectedBotMoveOverride(soldier.Pointer, now, out _))
                    {
                        __instance.nextSuppressionCheckTime = now + 2.2f;
                        return false;
                    }
                }
                catch { }
                return true;
            }

            public static void Postfix(SoldierAI __instance)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value || __instance == null)
                        return;

                    Soldier soldier = __instance.character;
                    if (!AiCore.IsLocallyOwnedBot(soldier) || AiCore.IsSoldierInVehicleOrTransition(soldier))
                        return;

                    AiCore.SoldierFastHandles sh = AiCore.GetOrCreateSoldierHandles(soldier);
                    IntPtr ptr = soldier.Pointer;
                    float now = Time.time;
                    if (sh != null)
                        sh.Stance.CombatStanceHoldUntil = now + 6.0f;

                    AddCombatExperienceBonus(soldier, 1.5f);

                    float vet01 = GetVeterancy01(soldier);
                    float curSupp = soldier.GetSuppressionValue();
                    SoldierPersonality pers = GetPersonality(soldier);

                    if (VisionConfig.EnableEmergentDecisionEngine.Value)
                    {
                        EmergentRuntimeBridge.NotifyIncomingSuppression(soldier, Vector3.zero, false, curSupp);
                    }

                    // Wake H.A.R.M. immediately after a brief human startle (0.16s-0.43s) so shot-at bots
                    // re-evaluate right away instead of continuing an old open-ground advance!
                    if (sh != null)
                    {
                        float startle = 0.16f + ((AiCore.GetDeterministicHash(ptr, 211) & 15) * 0.018f);
                        sh.Emergent.NextPlanTime = Mathf.Min(sh.Emergent.NextPlanTime, now + startle);
                        if (AiCore.GetRawVisibleTargetPtr(__instance) == IntPtr.Zero)
                            AiCore.EnqueueUrgentUnderFireWake(sh, now + startle);
                    }

                    if (VisionConfig.EnableShellShockSurrender.Value &&
                        curSupp >= 52f &&
                        TryEvaluateShellShockSurrender(__instance, soldier, pers, vet01, curSupp))
                    {
                        return;
                    }

                    float desiredCooldown = Mathf.Lerp(3.6f, 1.9f, vet01);
                    if (__instance.nextSuppressionCheckTime > now + desiredCooldown)
                    {
                        __instance.nextSuppressionCheckTime = now + desiredCooldown;
                    }
                }
                catch { }
            }
        }

        // =====================================================================
        // PATCH 5: SoldierAI.RotateAndFireToward
        // =====================================================================
        [HarmonyPatch(typeof(SoldierAI), nameof(SoldierAI.RotateAndFireToward))]
        public static class SoldierAI_RotateAndFireToward_Patch
        {
            public static bool Prefix(SoldierAI __instance, ref Vector3 __0, float __1, out long __state)
            {
                __state = HitchProfiler.Begin();
                try
                {
                    if (!VisionConfig.EnableMod.Value || ReferenceEquals(__instance, null))
                        return true;

                    AiCore.SoldierFastHandles h = AiCore.GetOrCreateSoldierHandlesByAi(__instance);
                    if (!AiCore.IsLocallyOwnedBot(h) || h.Tr == null)
                        return true;

                    Soldier soldier = h.Ref;
                    IntPtr ptr = h.Ptr;
                    int curFrame = Time.frameCount;
                    float now = Time.time;

                    // Immediately abort firing if the current target is a surrendered prisoner, incapacitated casualty,
                    // or enemy undergoing syringe capture (prevents 1.0s-2.0s stale visibleTarget friendly executions!)
                    IntPtr curVisPtr = AiCore.GetRawVisibleTargetPtr(__instance);
                    if (curVisPtr != IntPtr.Zero &&
                        AiCore.TryGetSoldierHandles(curVisPtr, out AiCore.SoldierFastHandles curVisSolH) &&
                        AiCore.IsSoldierNonCombatantOrSurrendered(curVisSolH.Ref, curVisSolH))
                    {
                        __instance.visibleTarget = null;
                        __instance.allowFireAtEnemy = false;
                        __instance.targetInWeaponRange = false;
                        __instance.aimingEnemy = false;
                        __instance.shotFiresEnd = 0f;
                        soldier.CurrentVisibleTarget = null;
                        soldier.SetGunTriggerPressed(false);
                        h.Combat.RotateFireFrame.Frame = -1;
                        if (h.Vision.LastKnownTarget.Target != null && h.Vision.LastKnownTarget.Target.Pointer == curVisPtr)
                            h.Vision.LastKnownTarget = default;
                        return false;
                    }

                    // Hold gunfire during an active grenade throw (including Co-op's 1.5s delayed SynchItemThrow
                    // remote animation window so vanilla clients never see full-auto gunfire mid-throw).
                    if (now < h.Combat.ThrowFireHoldUntil)
                    {
                        __instance.shotFiresEnd = 0f;
                        soldier.SetGunTriggerPressed(false);
                        if (soldier.triggerState == Soldier.TriggerState.Firing)
                            soldier.StopFire();
                        bool throwMoving = __instance.IsMoving();
                        h.Combat.RotateFireFrame = new RotateAndFireFrameState
                        {
                            Frame = curFrame,
                            BlockFire = true,
                            IsMoving = throwMoving
                        };
                        if (!throwMoving)
                        {
                            Vector3 faceDir = (now < h.Combat.PendingThrowFaceUntil && h.Combat.PendingThrowFaceDir.sqrMagnitude > 0.01f)
                                ? h.Combat.PendingThrowFaceDir
                                : (__0 - h.Tr.position);
                            if (AiCore.HorizontalAngleToDirection(soldier, faceDir) > 8.0f)
                                AiCore.RotateBodyWithoutRaisingGun(soldier, faceDir, __1);
                        }
                        return false;
                    }

                    // Physics catch-up spiral guard (FixedSteps: 2..5 in the same render frame):
                    // Reuse frame N's fire-permission & aim-point decision without re-running raycasts,
                    // bridge grid probes, bipod lookups, or veterancy queries 5x in one frame!
                    RotateAndFireFrameState prevFrameState = h.Combat.RotateFireFrame;
                    if (prevFrameState.Frame == curFrame && curFrame > 0)
                    {
                        if (prevFrameState.HasCustomTarget)
                            __0 = prevFrameState.CustomTargetPos;

                        if (!prevFrameState.BlockFire)
                        {
                            if (prevFrameState.ClampDistForEmplacementShoot && __instance.distFromTarget < 1.62f)
                                __instance.distFromTarget = 1.65f;
                            return true;
                        }

                        __instance.shotFiresEnd = 0f;
                        soldier.SetGunTriggerPressed(false);
                        if (prevFrameState.WallBlocked && !prevFrameState.IsPeeking)
                            soldier.pointGunEndTime = Mathf.Min(soldier.pointGunEndTime, now);

                        if (!prevFrameState.InPointBlankMelee && !prevFrameState.IsMoving)
                        {
                            Vector3 toTarget = __0 - h.Tr.position;
                            if (AiCore.HorizontalAngleToDirection(soldier, toTarget) > 8.0f)
                                AiCore.RotateBodyWithoutRaisingGun(soldier, toTarget, __1);
                        }
                        return false;
                    }

                    h.Stance.CombatStanceHoldUntil = now + 6.0f;
                    Vector3 myPos = h.Tr.position;

                    float dist = __instance.distFromTarget;
                    if (dist <= 0.1f)
                        dist = Vector3.Distance(myPos, __0);

                    bool hasCustomTarget = false;
                    Vector3 customTargetPos = __0;

                    if (VisionConfig.PreventSmallArmsFireAtTanks.Value &&
                        __instance.visibleTarget != null &&
                        __instance.visibleTarget.IsWheeledVehicleOrTank() &&
                        !soldier.IsOnVehicle())
                    {
                        Vehicle targetVeh = __instance.visibleTarget.TryCast<Vehicle>();
                        if (targetVeh != null)
                        {
                            IntPtr vehPtr = targetVeh.Pointer;
                            VehicleAimCache aimCache = h.Combat.VehicleAim;
                            if (aimCache.VehiclePtr != vehPtr || now >= aimCache.ExpireTime)
                            {
                                bool canEngage = TryEvaluateVehicleTarget(
                                    soldier,
                                    targetVeh,
                                    dist,
                                    out _,
                                    out Vector3 evalAimPoint,
                                    out bool evalHasCustomAim);

                                aimCache = new VehicleAimCache
                                {
                                    VehiclePtr = vehPtr,
                                    ExpireTime = now + 0.40f,
                                    CanEngage = canEngage,
                                    HasCustomAimPoint = evalHasCustomAim,
                                    CustomAimPoint = evalAimPoint
                                };
                                h.Combat.VehicleAim = aimCache;
                            }

                            if (aimCache.CanEngage)
                            {
                                if (aimCache.HasCustomAimPoint)
                                {
                                    __0 = aimCache.CustomAimPoint;
                                    hasCustomTarget = true;
                                    customTargetPos = __0;
                                    dist = Vector3.Distance(myPos, __0);
                                }
                            }
                            else if (TryFindVisibleFootEnemy(soldier, out Spottable rotAltEnemy, out float rotAltDist))
                            {
                                __instance.visibleTarget = rotAltEnemy;
                                soldier.CurrentVisibleTarget = rotAltEnemy;
                                __instance.distFromTarget = rotAltDist;
                                __instance.targetInWeaponRange = true;
                                if (!soldier.HasMovementsSlowed())
                                {
                                    __instance.allowFireAtEnemy = true;
                                    __instance.moveLookingTarget = true;
                                }
                                __0 = rotAltEnemy.GetCenterOfUnit();
                                hasCustomTarget = true;
                                customTargetPos = __0;
                                dist = rotAltDist;
                            }
                            else
                            {
                                __instance.targetInWeaponRange = false;
                                __instance.allowFireAtEnemy = false;
                                __instance.aimingEnemy = false;
                                __instance.shotFiresEnd = 0f;
                                soldier.SetGunTriggerPressed(false);
                                soldier.pointGunEndTime = Mathf.Min(soldier.pointGunEndTime, now);
                                if (now >= h.Combat.NextTankDisciplineCountTime)
                                {
                                    h.Combat.NextTankDisciplineCountTime = now + 3.0f;
                                    UselessTankFirePrevented++;
                                }
                                h.Combat.RotateFireFrame = new RotateAndFireFrameState
                                {
                                    Frame = curFrame,
                                    BlockFire = true,
                                    WallBlocked = true,
                                    IsMoving = true
                                };
                                return false;
                            }
                        }
                    }

                    Vector3 rawTargetPos = __0;
                    bool hasVehicleCustomTarget = hasCustomTarget;
                    if (!hasCustomTarget &&
                        now < h.Stance.ExposedAimUntil &&
                        h.Stance.ExposedAimYOffset > 0.01f &&
                        __instance.visibleTarget != null &&
                        !__instance.visibleTarget.IsWheeledVehicleOrTank() &&
                        !__instance.visibleTarget.IsAirVehicle())
                    {
                        __0 = new Vector3(__0.x, __0.y + h.Stance.ExposedAimYOffset, __0.z);
                        hasCustomTarget = true;
                        customTargetPos = __0;
                    }

                    float vet01 = GetVeterancy01(soldier);
                    bool blockFire = false;
                    bool wallBlocked = false;
                    bool isMoving = __instance.IsMoving();
                    bool isPeeking = h.Stance.PeekRestoreTime > 0f && now < h.Stance.PeekRestoreTime;
                    bool isHighBipodStanding = now < h.Stance.BipodStandUntil;
                    bool isGunner = AiCore.GetCachedGunnerAndBipod(h, now, out AttachmentBipod bipod);
                    bool isOnBridge = VisionConfig.EnableBridgeCrossingDiscipline.Value &&
                                      !soldier.IsOnVehicle() &&
                                      AiCore.IsOnBridgeOrNarrowCrossing(myPos);
                    if (isOnBridge)
                    {
                        h.Stance.NoProneUntil = now + 3.5f;
                    }

                    if (!isMoving && !soldier.IsOnVehicle())
                    {
                        __instance.actualPose = soldier.m_pose;
                    }

                    // Dynamic Human Reaction Time ("Watching vs. Caught Out"):
                    // Uses native SoldierAI.nextAvailableShotFires (+0x6C) & shotFiresEnd (+0x70) so the bot physically
                    // rotates its body, shoulders its weapon, and acquires a sight picture BEFORE pulling the trigger!
                    if (VisionConfig.EnableDynamicHumanReactionTime.Value &&
                        dist > 6.0f &&
                        __instance.visibleTarget != null &&
                        __instance.visibleTarget.Pointer != IntPtr.Zero)
                    {
                        IntPtr curTgtPtr = __instance.visibleTarget.Pointer;
                        InfantrySightReactionState rState = h.Combat.SightReaction;
                        bool isNewTarget = curTgtPtr != rState.LastTargetPtr;
                        bool isColdReacquire = rState.LastSeenTime <= 0f || (now - rState.LastSeenTime) > 2.2f;
                        if (isNewTarget || isColdReacquire)
                        {
                            bool inActiveFirefight = !isColdReacquire && rState.LastSeenTime > 0f && (now - rState.LastSeenTime) <= 1.8f;
                            bool rapidCqbSwitch = dist <= 14.0f && rState.LastSeenTime > 0f && (now - rState.LastSeenTime) <= 1.5f;
                            float turnDeg = AiCore.HorizontalAngleToDirection(soldier, __0 - myPos);
                            bool isWatchingSector = !isMoving &&
                                                    turnDeg <= 32.0f &&
                                                    (soldier.IsOnCover() || soldier.UsingBipod() || now < soldier.pointGunEndTime || h.Vision.StationaryWatch.HasWatchDir);
                            float baseDelay;
                            if (inActiveFirefight && isNewTarget)
                            {
                                // Already engaged with sights downrange: crisp human target-to-target shift instead of full cold acquisition
                                if (turnDeg <= 26.0f)
                                {
                                    baseDelay = Mathf.Lerp(0.18f, 0.10f, vet01) + (turnDeg / 26.0f) * 0.06f;
                                }
                                else
                                {
                                    baseDelay = Mathf.Lerp(0.26f, 0.16f, vet01) + Mathf.Clamp01((turnDeg - 26.0f) / 110.0f) * 0.24f;
                                }
                            }
                            else if (isWatchingSector)
                            {
                                baseDelay = Mathf.Lerp(0.21f, 0.12f, vet01) + (dist > 95.0f ? 0.07f : 0f);
                            }
                            else
                            {
                                baseDelay = Mathf.Lerp(0.42f, 0.27f, vet01) +
                                            Mathf.Clamp01(turnDeg / 135.0f) * 0.24f +
                                            (isMoving ? 0.12f : 0f);
                                if (isGunner && !soldier.UsingBipod())
                                    baseDelay += 0.10f;
                                else if (dist <= 12.0f && !isGunner)
                                    baseDelay -= 0.08f;
                            }

                            bool acquiringUnderFireShooter = now < h.Vision.UnderFireUntil &&
                                                             h.Vision.UnderFireAttackerPtr != IntPtr.Zero &&
                                                             curTgtPtr == h.Vision.UnderFireAttackerPtr;
                            float curSuppForReact = soldier.GetSuppressionValue();
                            if (!acquiringUnderFireShooter && curSuppForReact >= 18.0f)
                            {
                                baseDelay += Mathf.Clamp01((curSuppForReact - 18.0f) / 60.0f) * 0.14f;
                            }

                            float jitter = (AiCore.GetDeterministicHash(ptr, curTgtPtr.GetHashCode()) & 31) * 0.0045f;
                            float totalDelay = Mathf.Clamp(baseDelay + jitter, 0.10f, 0.92f);
                            if (rapidCqbSwitch)
                                totalDelay = Mathf.Min(totalDelay, 0.10f);
                            else if (acquiringUnderFireShooter)
                                totalDelay = Mathf.Min(totalDelay, dist <= 18.0f ? 0.18f : Mathf.Lerp(0.28f, 0.14f, vet01));
                            else if (dist <= 14.0f)
                                totalDelay = Mathf.Min(totalDelay, 0.26f);

                            if (__instance.nextAvailableShotFires < now + totalDelay)
                            {
                                __instance.nextAvailableShotFires = now + totalDelay;
                                if (!inActiveFirefight || turnDeg > 12.0f)
                                {
                                    __instance.shotFiresEnd = 0f;
                                    soldier.SetGunTriggerPressed(false);
                                }
                            }

                            float commitJitter = (AiCore.GetDeterministicHash(ptr, curTgtPtr.GetHashCode() ^ 73) & 15) * 0.06f;
                            rState.LockedUntil = now + 2.6f + commitJitter;
                        }
                        else if (now < __instance.shotFiresEnd && rState.LockedUntil < now + 0.6f)
                        {
                            rState.LockedUntil = now + 0.6f;
                        }

                        rState.LastTargetPtr = curTgtPtr;
                        rState.LastSeenTime = now;
                        h.Combat.SightReaction = rState;
                    }

                    bool inPointBlankMelee = false;
                    if (dist <= 6.2f &&
                        IsEngagedInPointBlankMelee(
                            __instance,
                            soldier,
                            dist,
                            out Soldier meleeEnemy,
                            out _,
                            out float ceaseFireRange,
                            out float strikeRange,
                            out int rotMeleeSlot) &&
                        meleeEnemy != null)
                    {
                        inPointBlankMelee = true;
                        AiCore.SoldierFastHandles enemyH = AiCore.GetOrCreateSoldierHandles(meleeEnemy);
                        Vector3 enemyPos = (enemyH != null && enemyH.Tr != null) ? enemyH.Tr.position : __0;

                        UpdatePointBlankMeleeEngagement(
                            __instance,
                            soldier,
                            h,
                            meleeEnemy,
                            dist,
                            strikeRange,
                            rotMeleeSlot,
                            now,
                            isPerFrameHook: true);

                        bool shouldCeaseFireForMelee = soldier.HoldsMeleeWeapon ||
                                                       AiCore.IsHeldGunEmptyOrReloading(soldier, soldier.GetHeldGun()) ||
                                                       dist <= strikeRange + 0.22f;
                        if (dist <= ceaseFireRange && shouldCeaseFireForMelee)
                        {
                            blockFire = true;
                            Vector3 dirToEnemy = enemyPos - myPos;
                            dirToEnemy.y = 0f;
                            if (dirToEnemy.sqrMagnitude > 0.06f && AiCore.HorizontalAngleToDirection(soldier, dirToEnemy) > 6.0f)
                            {
                                float turnMult = dist < 1.10f ? 1.25f : 2.0f;
                                soldier.RotateToward(dirToEnemy.normalized, __1 * turnMult);
                            }
                        }
                    }

                    bool rotateTargetIsVeh = __instance.visibleTarget != null && __instance.visibleTarget.IsWheeledVehicleOrTank();
                    if (!isMoving &&
                        !inPointBlankMelee &&
                        VisionConfig.NeverShootStandingInOpen.Value &&
                        !isPeeking &&
                        !isHighBipodStanding &&
                        !soldier.IsOnVehicle() &&
                        !soldier.IsCharging() &&
                        !soldier.UsingBipod())
                    {
                        if (soldier.m_pose == SoldierPose.Idle && dist > 11.0f && dist < 220f)
                        {
                            float curSupp = soldier.GetSuppressionValue();
                            float forceCrouchSupp = soldier.IsOnCover() ? 52f : 48f;
                            bool canCheckCrouchLof = curSupp >= forceCrouchSupp || now >= h.Stance.NextStandCrouchLofCheckTime;
                            if (canCheckCrouchLof)
                            {
                                h.Stance.NextStandCrouchLofCheckTime = now + 0.25f;
                                if (curSupp >= forceCrouchSupp || HasClearLineOfFireFromCrouch(soldier, __0, dist, rotateTargetIsVeh))
                                {
                                    bool canGoProne = now >= h.Stance.NoProneUntil &&
                                                      !soldier.IsOnCover() &&
                                                      !soldier.IsTowardWall();

                                    bool checkRotProne = canGoProne &&
                                                         (curSupp >= 26f ||
                                                          now < h.Vision.UnderFireUntil ||
                                                          (h.Emergent.HasDecision && h.Emergent.CurrentDecision.TotalHarm >= 0.30f) ||
                                                          (isGunner && !soldier.IsOnCover() && dist >= 28f));

                                    SoldierPose fightPose = SoldierPose.Crouch;
                                    if (checkRotProne &&
                                        HasClearLineOfFireFromProne(soldier, __0, dist, rotateTargetIsVeh) &&
                                        (curSupp >= 62f ||
                                         (isGunner && !soldier.IsOnCover() && dist >= 28f) ||
                                         ShouldBotChooseOrStayProneForSafety(__instance, soldier, h, false, true, true, dist, curSupp, false, false, now)))
                                    {
                                        fightPose = SoldierPose.Prone;
                                    }

                                    SetBotPose(__instance, soldier, fightPose);

                                    if (now >= h.Stance.NextStanceDropAllowed)
                                    {
                                        if (dist > 28.0f && now >= h.Vision.UnderFireUntil)
                                            h.Stance.StanceDropHoldFireUntil = now + 0.30f;
                                        h.Stance.NextStanceDropAllowed = now + 3.5f;
                                        SelfPreservationDrops++;
                                    }
                                }
                                else
                                {
                                    __instance.actualPose = SoldierPose.Idle;
                                }
                            }
                            else
                            {
                                __instance.actualPose = SoldierPose.Idle;
                            }
                        }

                        if (now < h.Stance.StanceDropHoldFireUntil)
                        {
                            blockFire = true;
                        }
                    }

                    if (VisionConfig.EnableWallShootingPrevention.Value && !soldier.IsOnVehicle())
                    {
                        if (h.Stance.DesiredLean != Soldier.LeanDirection.NotLeaning)
                        {
                            if (!isMoving &&
                                now < h.Stance.LeanHoldUntil &&
                                soldier.m_pose != SoldierPose.Prone &&
                                !soldier.UsingBipod() &&
                                !soldier.IsReloading)
                            {
                                soldier.LeanState = h.Stance.DesiredLean;
                                if (Mathf.Abs(soldier.LeanAngle) < 0.45f)
                                {
                                    blockFire = true;
                                }
                            }
                            else
                            {
                                h.Stance.DesiredLean = Soldier.LeanDirection.NotLeaning;
                                h.Stance.LeanHoldUntil = 0f;
                                soldier.LeanState = Soldier.LeanDirection.NotLeaning;
                            }
                        }

                        if (now >= h.Stance.NextWallCheckTime)
                        {
                            float angleToTarget = AiCore.HorizontalAngleToDirection(soldier, rawTargetPos - myPos);
                            if (angleToTarget <= 35f)
                            {
                                h.Stance.NextWallCheckTime = now + 0.15f;
                                bool targetIsVehicle = __instance.visibleTarget != null && __instance.visibleTarget.IsWheeledVehicleOrTank();
                                bool blockedAtRaw = IsMuzzleBlockedByWall(soldier, rawTargetPos, dist, targetIsVehicle);

                                // If the immediate muzzle is clear, still lift aim to exposed head/shoulders when the
                                // enemy's torso is behind a wall downrange (CanSee sees SpotPosition head-to-head!).
                                if (!blockedAtRaw &&
                                    !hasVehicleCustomTarget &&
                                    __instance.visibleTarget != null &&
                                    !__instance.visibleTarget.IsWheeledVehicleOrTank() &&
                                    !__instance.visibleTarget.IsAirVehicle())
                                {
                                    bool torsoOccluded = false;
                                    if (dist > 3.5f)
                                    {
                                        Vector3 shOrigin = myPos + Vector3.up * AiCore.GetBarrelHeightForPose(soldier.m_pose);
                                        if (Mathf.Abs(soldier.LeanAngle) > 0.15f)
                                            shOrigin += h.Tr.right * (soldier.LeanAngle * 0.40f);
                                        Vector3 toRaw = rawTargetPos - shOrigin;
                                        float rawLen = toRaw.magnitude;
                                        if (rawLen > 1.2f &&
                                            AiCore.RaycastEnvironment(shOrigin, toRaw / rawLen, rawLen - 0.35f, h.RootTr, out _))
                                        {
                                            torsoOccluded = true;
                                        }
                                    }

                                    if (torsoOccluded &&
                                        TryResolveExposedOverWallAim(soldier, __instance.visibleTarget, rawTargetPos, dist, out Vector3 farExposed))
                                    {
                                        __0 = farExposed;
                                        hasCustomTarget = true;
                                        customTargetPos = __0;
                                        h.Stance.ExposedAimYOffset = Mathf.Clamp(farExposed.y - rawTargetPos.y, 0f, 0.72f);
                                        h.Stance.ExposedAimUntil = now + 0.32f;
                                        h.Stance.WallBlockedUntil = 0f;
                                    }
                                    else if (!torsoOccluded)
                                    {
                                        __0 = rawTargetPos;
                                        hasCustomTarget = false;
                                        customTargetPos = rawTargetPos;
                                        h.Stance.ExposedAimUntil = 0f;
                                        h.Stance.ExposedAimYOffset = 0f;
                                    }
                                }

                                if (blockedAtRaw &&
                                    __instance.visibleTarget != null &&
                                    TryResolveExposedOverWallAim(soldier, __instance.visibleTarget, rawTargetPos, dist, out Vector3 nearExposed))
                                {
                                    __0 = nearExposed;
                                    hasCustomTarget = true;
                                    customTargetPos = __0;
                                    h.Stance.ExposedAimYOffset = Mathf.Clamp(nearExposed.y - rawTargetPos.y, 0f, 0.72f);
                                    h.Stance.ExposedAimUntil = now + 0.32f;
                                    h.Stance.WallBlockedUntil = 0f;
                                    blockedAtRaw = false;
                                }

                                if (blockedAtRaw)
                                {
                                    h.Stance.WallBlockedUntil = now + 0.28f;
                                    WallsBlocked++;

                                    bool canPeekNow = now >= h.Stance.NextPeekAllowedTime &&
                                                      now >= h.Stance.PoseLockUntil;
                                    GetEffectivePeekParameters(soldier, vet01, out float maxPeekSupp, out _, out _);
                                    // Only stand up / sidestep to peek when engaging an actual visible target (never during blind suppressive fire)
                                    if (!isMoving &&
                                        __instance.visibleTarget != null &&
                                        canPeekNow &&
                                        VisionConfig.EnableAutoPeekOverCover.Value &&
                                        soldier.GetSuppressionValue() < maxPeekSupp &&
                                        !soldier.IsReloading &&
                                        !soldier.isInBodyAnimation &&
                                        !soldier.UsingBipod() &&
                                        angleToTarget <= 35f)
                                    {
                                        Vector3 upperPeekPos = GetOverWallPeekTarget(__instance.visibleTarget, rawTargetPos);
                                        bool hasUpperPeek = upperPeekPos != rawTargetPos;
                                        bool crouchClear = HasClearLineOfFireFromCrouch(soldier, rawTargetPos, dist, targetIsVehicle) ||
                                                           (hasUpperPeek && HasClearLineOfFireFromCrouch(soldier, upperPeekPos, dist, targetIsVehicle));
                                        bool standClear = HasClearLineOfFireFromStanding(soldier, rawTargetPos, dist, targetIsVehicle) ||
                                                          (hasUpperPeek && HasClearLineOfFireFromStanding(soldier, upperPeekPos, dist, targetIsVehicle));
                                        bool isStaffingStaticGun = IsActivelyHeadingToEmplacement(soldier, now);
                                        // Allow short lateral jamb/embrasure sidestep even when snapped to cover once both crouch & standing shots are blocked!
                                        bool canSidestep = !soldier.IsCharging() && !isOnBridge && !isStaffingStaticGun;
                                        float peekDur = GetJitteredPeekDuration(ptr, 4.1f);

                                        if (soldier.m_pose != SoldierPose.Prone &&
                                            h.Stance.DesiredLean == Soldier.LeanDirection.NotLeaning &&
                                            (TryFindCornerLeanDirection(soldier, rawTargetPos, dist, out Soldier.LeanDirection cornerLean, targetIsVehicle) ||
                                             (hasUpperPeek && TryFindCornerLeanDirection(soldier, upperPeekPos, dist, out cornerLean, targetIsVehicle))))
                                        {
                                            h.Stance.DesiredLean = cornerLean;
                                            h.Stance.LeanHoldUntil = now + peekDur;
                                            h.Stance.CombatStanceHoldUntil = now + 6.0f;
                                            soldier.LeanState = cornerLean;
                                            AutoPeeksTriggered++;
                                        }
                                        else if (soldier.m_pose == SoldierPose.Prone)
                                        {
                                            if (crouchClear)
                                            {
                                                SetBotPose(__instance, soldier, SoldierPose.Crouch, true);
                                                h.Stance.NoProneUntil = now + 10.0f;
                                                h.Stance.CombatStanceHoldUntil = now + 6.0f;
                                                h.Stance.PeekRestoreTime = now + 5.0f;
                                                h.Stance.NextStanceDropAllowed = now + 5.0f;
                                                AutoPeeksTriggered++;
                                            }
                                            else if (standClear)
                                            {
                                                SetBotPose(__instance, soldier, SoldierPose.Idle, true);
                                                h.Stance.NoProneUntil = now + 10.0f;
                                                h.Stance.CombatStanceHoldUntil = now + 6.0f;
                                                h.Stance.PeekRestoreTime = now + peekDur;
                                                h.Stance.NextStanceDropAllowed = now + peekDur;
                                                if (isGunner && bipod != null)
                                                {
                                                    h.Stance.BipodStandUntil = now + 4.5f;
                                                    soldier.StartCoroutine(soldier.RetryBipodDeployAfterStanding());
                                                }
                                                AutoPeeksTriggered++;
                                            }
                                            else if (canSidestep)
                                            {
                                                TrySidestepWallObstruction(__instance, soldier, rawTargetPos, dist, upperPeekPos, targetIsVehicle);
                                            }
                                        }
                                        else if (soldier.m_pose == SoldierPose.Crouch)
                                        {
                                            if (standClear)
                                            {
                                                SetBotPose(__instance, soldier, SoldierPose.Idle, true);
                                                h.Stance.NoProneUntil = now + 10.0f;
                                                h.Stance.CombatStanceHoldUntil = now + 6.0f;
                                                h.Stance.PeekRestoreTime = now + peekDur;
                                                h.Stance.NextStanceDropAllowed = now + peekDur;
                                                if (isGunner && bipod != null)
                                                {
                                                    h.Stance.BipodStandUntil = now + 4.5f;
                                                    soldier.StartCoroutine(soldier.RetryBipodDeployAfterStanding());
                                                }
                                                AutoPeeksTriggered++;
                                            }
                                            else if (canSidestep)
                                            {
                                                TrySidestepWallObstruction(__instance, soldier, rawTargetPos, dist, upperPeekPos, targetIsVehicle);
                                            }
                                        }
                                        else if (soldier.m_pose == SoldierPose.Idle && canSidestep)
                                        {
                                            TrySidestepWallObstruction(__instance, soldier, rawTargetPos, dist, upperPeekPos, targetIsVehicle);
                                        }
                                    }
                                }
                                else if (__instance.visibleTarget != null &&
                                         IsTargetFullyOccludedBySolidWall(soldier, __instance.visibleTarget, __0, dist))
                                {
                                    h.Stance.WallBlockedUntil = now + 0.22f;
                                    WallsBlocked++;
                                }
                                else
                                {
                                    h.Stance.WallBlockedUntil = 0f;
                                }
                            }
                        }

                        if (now < h.Stance.WallBlockedUntil)
                        {
                            blockFire = true;
                            wallBlocked = true;
                        }
                    }

                    if (VisionConfig.EnableFriendlyFireAvoidance.Value)
                    {
                        if (now >= h.Combat.NextFFCheckTime)
                        {
                            h.Combat.NextFFCheckTime = now + 0.25f;
                            if (IsFriendlyInLineOfFire(soldier, __0, dist, vet01, out _, out Soldier blockingFriend))
                            {
                                h.Combat.FFBlockedUntil = now + 0.30f;
                                FriendlyFirePrevented++;
                                if (blockingFriend != null)
                                {
                                    TryNudgeFriendlyOutOfFireCone(soldier, blockingFriend, __0);
                                }
                            }
                        }

                        if (now < h.Combat.FFBlockedUntil)
                        {
                            blockFire = true;
                        }
                    }

                    if (!isMoving)
                    {
                        TryApplyAntiStackSpacingStep(__instance, soldier, h, myPos, isOnBridge, now, 2.5f);
                    }

                    bool clampDistForEmplacementShoot =
                        !inPointBlankMelee &&
                        !blockFire &&
                        dist < 1.62f &&
                        TryResolvePointBlankTargetSoldier(__instance, out _, out bool isExposedEmpGunner) &&
                        isExposedEmpGunner;

                    h.Combat.RotateFireFrame = new RotateAndFireFrameState
                    {
                        Frame = curFrame,
                        BlockFire = blockFire,
                        WallBlocked = wallBlocked,
                        InPointBlankMelee = inPointBlankMelee,
                        IsMoving = isMoving,
                        IsPeeking = isPeeking,
                        HasCustomTarget = hasCustomTarget,
                        ClampDistForEmplacementShoot = clampDistForEmplacementShoot,
                        CustomTargetPos = customTargetPos
                    };

                    if (blockFire)
                    {
                        __instance.shotFiresEnd = 0f;
                        soldier.SetGunTriggerPressed(false);

                        if (wallBlocked && !isPeeking && dist > 6.0f)
                        {
                            soldier.pointGunEndTime = Mathf.Min(soldier.pointGunEndTime, now);
                        }

                        if (!inPointBlankMelee && !isMoving)
                        {
                            Vector3 toTarget = __0 - myPos;
                            if (AiCore.HorizontalAngleToDirection(soldier, toTarget) > 8.0f)
                            {
                                AiCore.RotateBodyWithoutRaisingGun(soldier, toTarget, __1);
                            }
                        }
                        return false;
                    }

                    // Prevent vanilla SoldierAI.RotateAndFireToward (< 1.6m hardcoded melee trigger at 0x18055FA61)
                    // from aborting firearm fire into a whiffing 0.7m melee swing against a seated static-emplacement gunner!
                    if (clampDistForEmplacementShoot && __instance.distFromTarget < 1.62f)
                    {
                        __instance.distFromTarget = 1.65f;
                    }

                    if (soldier.isTowardWallTps)
                    {
                        soldier.isTowardWallTps = false;
                    }
                }
                catch { }
                return true;
            }

            public static void Postfix(long __state)
            {
                HitchProfiler.End(HitchBucket.RotateAndFire, __state);
            }
        }

        // =====================================================================
        // GOAP Cover Lock De-Confliction (SoldierAI.CalculateMoveOrderDestination)
        // Vanilla SquadOrdersRoutine (0x180560895 -> 0x180559CC8) bypasses UnitCanChangeCover
        // every 2.0s and overwrites soldier.CoverPosition(...) whenever a GOAP Flanker,
        // Scavenger, Wall-Side Repositioner, or Tank-Cover seeker steps outside the 15m
        // moveOrderRadius! Preserve their assigned GOAP destination while locked.
        // =====================================================================
        [HarmonyPatch(typeof(SoldierAI), nameof(SoldierAI.CalculateMoveOrderDestination))]
        public static class SoldierAI_CalculateMoveOrderDestination_Patch
        {
            public static bool Prefix(SoldierAI __instance, ref Vector3 __result)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value || __instance == null)
                        return true;

                    Soldier soldier = __instance.character;
                    if (!AiCore.IsLocallyOwnedBot(soldier) ||
                        soldier.IsOnVehicle())
                    {
                        return true;
                    }

                    AiCore.SoldierFastHandles covH = AiCore.GetOrCreateSoldierHandles(soldier);
                    IntPtr ptr = soldier.Pointer;
                    float now = Time.time;

                    if (soldier.IsCharging() &&
                        (covH == null || now >= covH.Stance.LocalCoverProtectedUntil))
                    {
                        return true;
                    }

                    // Preserve active raw-position GOAP/retreat/casualty-drag/scavenge/radio/lone-wolf move overrides
                    // even when soldier.targetDestination is null or still points at stale forward cover!
                    if (TryGetProtectedBotMoveOverride(ptr, now, out Vector3 moveOverride))
                    {
                        __result = moveOverride;
                        return false;
                    }

                    bool hasTargetDest = soldier.HasDestinationAssigned && AiCore.GetRawTargetDestinationPtr(soldier) != IntPtr.Zero;

                    // Pin AI Squad Leaders in Order.follow to their squad's active task destination instead of
                    // vanilla Soldier.GetBestMissionObjective(), so Squad Leaders never leave their squad behind.
                    if (soldier.IsSquadLeader() && !hasTargetDest)
                    {
                        Squad sq = covH != null ? AiCore.GetCachedSquad(covH, now) : soldier.joinedSquad;
                        if (sq != null && sq.order == Order.follow && sq.currentTask != null)
                        {
                            Vector3 taskDest = sq.currentTask.GetTaskDestination();
                            if (taskDest.sqrMagnitude > 0.01f)
                            {
                                __result = taskDest;
                                return false;
                            }
                        }
                    }

                    // Release bots from vanilla's 15m FollowLeader leash if their human Squad Leader is AFK (>= 25s
                    // with zero movement/rotation/animation), snapping back immediately when the player moves.
                    if (!soldier.IsSquadLeader() && !hasTargetDest)
                    {
                        Squad botSq = covH != null ? AiCore.GetCachedSquad(covH, now) : soldier.joinedSquad;
                        if (botSq != null &&
                            botSq.order == Order.follow &&
                            botSq.Leader != null &&
                            botSq.Leader.IsAlive &&
                            AiCore.IsAnyHumanPlayerSoldier(botSq.Leader) &&
                            AiCore.IsHumanSoldierAfk(botSq.Leader, now, 25.0f))
                        {
                            if (botSq.currentTask != null)
                            {
                                Vector3 taskDest = botSq.currentTask.GetTaskDestination();
                                if (taskDest.sqrMagnitude > 0.01f)
                                {
                                    __result = taskDest;
                                    return false;
                                }
                            }
                            Transform botTr = (covH != null && covH.Tr != null) ? covH.Tr : soldier.transform;
                            if (botTr != null &&
                                AiCore.TryFindFallbackObjectiveOrFriendlyPos(soldier.faction, botTr.position, out Vector3 fallbackPos))
                            {
                                __result = fallbackPos;
                                return false;
                            }
                        }
                    }

                    if (!hasTargetDest)
                    {
                        return true;
                    }

                    bool protectedCover =
                        (VisionConfig.EnableEmergentDecisionEngine.Value && HasActiveGoapCoverLock(ptr, now)) ||
                        (covH != null && now < covH.Stance.LocalCoverProtectedUntil) ||
                        IsActivelyHeadingToEmplacement(soldier, now);

                    if (protectedCover)
                    {
                        AiDestination cachedDest = (covH != null && AiCore.ResolveTargetDestination(covH) != AiCore.CachedDestKind.None)
                            ? covH.CachedDest
                            : soldier.targetDestination;
                        if (!ReferenceEquals(cachedDest, null) && cachedDest != null)
                        {
                            __result = cachedDest.GetCoverPosition();
                            return false;
                        }
                    }
                }
                catch { }
                return true;
            }
        }

        // =====================================================================
        // PATCH 10 & 11: Smart Wall-Side Cover (CombatCover & Squad)
        // =====================================================================
        [HarmonyPatch(typeof(CombatCover), nameof(CombatCover.IsCoverAvailable))]
        public static class CombatCover_IsCoverAvailable_Patch
        {
            private static int _coverExposureProbeFrame = -1;
            private static int _coverExposureUncachedProbesThisFrame;

            public static void Postfix(CombatCover __instance, Vector3 __0, string __1, ref bool __result)
            {
                long profStart = HitchProfiler.BeginModScope();
                try
                {
                    if (!__result || !VisionConfig.EnableMod.Value || __instance == null)
                        return;

                    if (!AiCore.IsCombatCoverIntact(__instance))
                    {
                        __result = false;
                        return;
                    }

                    bool wallSideEnabled = VisionConfig.EnableSmartWallSideCover.Value;
                    bool ffEnabled = VisionConfig.EnableFriendlyFireAvoidance.Value;
                    bool vehRowEnabled = VisionConfig.EnableFriendlyVehicleRightOfWay.Value;
                    if (!wallSideEnabled && !ffEnabled && !vehRowEnabled)
                        return;

                    Vector3 threatDir = __0;
                    threatDir.y = 0f;
                    if (threatDir.sqrMagnitude < 0.001f)
                        return;

                    threatDir.Normalize();
                    IntPtr covPtr = __instance.Pointer;

                    // Zero-raycast reject if this cover spot sits directly inside a friendly human aim lane OR moving friendly vehicle danger corridor
                    if (covPtr != IntPtr.Zero && (ffEnabled || vehRowEnabled))
                    {
                        Vector3 cachedCovPos = default;
                        bool hasCovPos = false;

                        if (ffEnabled)
                        {
                            AiCore.EnsurePlayerAimLanesUpdated();
                            if (AiCore.ActivePlayerAimLaneCount > 0)
                            {
                                if (!_staticCoverPosCache.TryGetValue(covPtr, out cachedCovPos))
                                {
                                    cachedCovPos = __instance.GetCoverPosition();
                                    if (!__instance.IsVehicle())
                                        _staticCoverPosCache[covPtr] = cachedCovPos;
                                }
                                hasCovPos = true;

                                if (AiCore.IsCoverInPlayerAimLane(cachedCovPos, threatDir))
                                {
                                    __result = false;
                                    return;
                                }
                            }
                        }

                        if (vehRowEnabled && !string.IsNullOrEmpty(__1))
                        {
                            AiCore.EnsureVehicleDangerCorridorsUpdated(Time.time);
                            if (AiCore.ActiveVehicleDangerCorridorCount > 0)
                            {
                                if (!hasCovPos && !_staticCoverPosCache.TryGetValue(covPtr, out cachedCovPos))
                                {
                                    cachedCovPos = __instance.GetCoverPosition();
                                    if (!__instance.IsVehicle())
                                        _staticCoverPosCache[covPtr] = cachedCovPos;
                                }

                                if (AiCore.IsPointInFriendlyVehicleDangerCorridor(cachedCovPos, __1, 0.45f))
                                {
                                    __result = false;
                                    return;
                                }
                            }
                        }
                    }

                    if (!wallSideEnabled)
                        return;

                    if (__instance.localLookAngleWideness < 175f)
                    {
                        Vector3 coverFwd = __instance.GetLookRotation() * Vector3.forward;
                        coverFwd.y = 0f;
                        if (coverFwd.sqrMagnitude > 0.001f && Vector3.Angle(coverFwd, threatDir) > 78f)
                        {
                            __result = false;
                            return;
                        }
                    }

                    float angleDeg = Mathf.Atan2(threatDir.x, threatDir.z) * Mathf.Rad2Deg;
                    if (angleDeg < 0f) angleDeg += 360f;
                    int octant = Mathf.FloorToInt((angleDeg + 22.5f) / 45f) & 7;
                    long cacheKey = ((long)covPtr << 4) | (uint)octant;
                    float now = Time.time;

                    bool isInvalid;
                    if (_coverExposureCache.TryGetValue(cacheKey, out CoverExposureCacheEntry cachedEntry) && now < cachedEntry.ExpireTime)
                    {
                        isInvalid = cachedEntry.IsInvalid;
                    }
                    else
                    {
                        // Cap uncached IsPositionOnExposedSideOfWall raycast sweeps to <= 40 per frame
                        // so CoverManager.GetCovers octree sweeps filter windward walls cleanly without spiking frame time!
                        int curFrame = Time.frameCount;
                        if (curFrame != _coverExposureProbeFrame)
                        {
                            _coverExposureProbeFrame = curFrame;
                            _coverExposureUncachedProbesThisFrame = 0;
                        }
                        if (_coverExposureUncachedProbesThisFrame >= 40)
                            return;

                        _coverExposureUncachedProbesThisFrame++;
                        HitchProfiler.RecordUncachedCoverProbe();
                        if (!_staticCoverPosCache.TryGetValue(covPtr, out Vector3 coverPos))
                        {
                            coverPos = __instance.GetCoverPosition();
                            if (!__instance.IsVehicle())
                                _staticCoverPosCache[covPtr] = coverPos;
                        }
                        isInvalid = IsPositionOnExposedSideOfWall(coverPos, threatDir, null, out _);
                        if (!isInvalid &&
                            !__instance.IsVehicle() &&
                            __instance.localLookAngleWideness < 175f &&
                            __instance.GetCoverPose() != SoldierPose.Prone &&
                            !AiCore.HasLowCeilingOverhead(coverPos, 5.5f))
                        {
                            Vector3 checkFwd = __instance.GetLookRotation() * Vector3.forward;
                            checkFwd.y = 0f;
                            Vector3 barrierDir = checkFwd.sqrMagnitude > 0.001f ? checkFwd.normalized : threatDir;
                            if (!IsPositionShelteredByFrontCover(coverPos, barrierDir, null, 1.85f) &&
                                !IsPositionShelteredByFrontCover(coverPos, threatDir, null, 1.85f))
                            {
                                isInvalid = true;
                            }
                        }
                        _coverExposureCache[cacheKey] = new CoverExposureCacheEntry
                        {
                            ExpireTime = now + 12.0f,
                            IsInvalid = isInvalid
                        };
                    }

                    if (isInvalid)
                    {
                        __result = false;
                        return;
                    }

                    if (!__instance.IsVehicle() && _coverExposureUncachedProbesThisFrame < 6)
                    {
                        _coverExposureUncachedProbesThisFrame++;
                        Vector3 eyePos = __instance.GetCoverPosition() + Vector3.up * 1.15f;
                        Vector3 coverFwd = __instance.GetLookRotation() * Vector3.forward;
                        coverFwd.y = 0f;
                        bool fwdBlocked = coverFwd.sqrMagnitude < 0.01f ||
                                          AiCore.IsDirectionBlockedByEnvironment(eyePos, coverFwd.normalized, 2.2f, null);
                        bool threatBlocked = AiCore.IsDirectionBlockedByEnvironment(eyePos, threatDir, 2.2f, null);
                        if (fwdBlocked && threatBlocked)
                        {
                            __result = false;
                        }
                    }
                }
                catch (Exception ex)
                {
                    AiCore.ReportPatchException("CoverAvail", ex);
                }
                finally
                {
                    HitchProfiler.EndModScope(HitchBucket.CoverAvail, profStart);
                }
            }
        }

        internal static bool TryComputeSquadObjectiveSectorAnchor(
            Squad sq,
            SquadTask task,
            Vector3 squadRefPos,
            out Vector3 sectorAnchor,
            out float sectorRadius,
            out bool isBoxTask)
        {
            sectorAnchor = Vector3.zero;
            sectorRadius = 16.0f;
            isBoxTask = false;
            if (sq == null || sq.Pointer == IntPtr.Zero || task == null || task.Pointer == IntPtr.Zero)
                return false;

            Vector3 center = task.GetTaskDestination();
            float taskRadius = Mathf.Max(8.0f, task.GetTaskAreaRadius());
            MissionTask mt = task.TryCast<MissionTask>();
            isBoxTask = mt != null && mt.shape == ObjectiveShape.Box;
            if (!isBoxTask && taskRadius < 10.0f)
                return false;

            int hash = AiCore.GetDeterministicHash(sq.Pointer, 313);
            Vector3 fromCenter = squadRefPos - center;
            fromCenter.y = 0f;
            float distSq = fromCenter.sqrMagnitude;

            Vector3 baseDir;
            float angleDeg;
            if (distSq > (taskRadius * 0.85f) * (taskRadius * 0.85f))
            {
                baseDir = fromCenter / Mathf.Sqrt(distSq);
                int slot = (int)((uint)hash % 9u) - 4;
                angleDeg = slot * 14.0f;
            }
            else
            {
                baseDir = Vector3.forward;
                angleDeg = ((hash & 7) * 45.0f) + (((hash >> 4) & 15) * 2.0f);
            }

            float rad = angleDeg * Mathf.Deg2Rad;
            float cosA = Mathf.Cos(rad);
            float sinA = Mathf.Sin(rad);
            Vector3 dir = new Vector3(
                baseDir.x * cosA - baseDir.z * sinA,
                0f,
                baseDir.x * sinA + baseDir.z * cosA);
            if (dir.sqrMagnitude < 0.01f)
                dir = Vector3.forward;
            dir.Normalize();

            bool hasWater = AiCore.TryGetActiveWaterLevel(out float waterY);
            float primaryFrac = 0.42f + (((hash >> 4) & 3) * 0.10f);
            for (int p = 0; p < 3; p++)
            {
                float frac = p == 0 ? primaryFrac : (p == 1 ? 0.48f : 0.30f);
                Vector3 raw = center + dir * (taskRadius * frac);
                if (!task.IsInsideTaskArea(raw, 0.88f))
                    continue;

                if (!AiCore.TrySnapWalkableGroundProbe(center, raw, 16.0f, 32.0f, 0.55f, 3.0f, hasWater, waterY, out Vector3 snapped))
                    continue;

                if (!task.IsInsideTaskArea(snapped, 0.90f))
                    continue;

                sectorAnchor = snapped;
                sectorRadius = isBoxTask ? Mathf.Max(taskRadius, 18.0f) : Mathf.Clamp(taskRadius * 0.52f, 8.5f, 20.0f);
                return true;
            }

            return false;
        }

        [HarmonyPatch(typeof(Squad), nameof(Squad.SendUnitsToCovers))]
        public static class Squad_SendUnitsToCovers_Patch
        {
            public static void Prefix(Squad __instance, ref Vector3 __0, ref float __1, out long __state)
            {
                __state = HitchProfiler.Begin();
                try
                {
                    if (!VisionConfig.EnableMod.Value || __instance == null)
                        return;

                    if (__instance.Leader != null && !AiCore.IsLocallyOwnedBot(__instance.Leader))
                        return;

                    // Keep squad members INSIDE the actual MissionTask capture radius without dogpiling the dead center!
                    if (VisionConfig.EnableMacroObjectiveDirector.Value &&
                        __instance.currentTask != null)
                    {
                        Vector3 taskCenter = __instance.currentTask.GetTaskDestination();
                        float dTaskX = taskCenter.x - __0.x;
                        float dTaskZ = taskCenter.z - __0.z;
                        float dTaskSqXZ = dTaskX * dTaskX + dTaskZ * dTaskZ;
                        float taskRadius = Mathf.Max(10.0f, __instance.currentTask.GetTaskAreaRadius());
                        MissionTask curMt = __instance.currentTask.TryCast<MissionTask>();
                        bool isBoxTask = curMt != null && curMt.shape == ObjectiveShape.Box;

                        if (dTaskSqXZ <= 8.0f * 8.0f)
                        {
                            Vector3 refPos = (__instance.Leader != null && __instance.Leader.transform != null)
                                ? __instance.Leader.transform.position
                                : __0;
                            if (TryComputeSquadObjectiveSectorAnchor(
                                    __instance,
                                    __instance.currentTask,
                                    refPos,
                                    out Vector3 sectorPos,
                                    out float sectorRad,
                                    out isBoxTask))
                            {
                                __0 = sectorPos;
                                __1 = isBoxTask
                                    ? Mathf.Max(__1, taskRadius)
                                    : Mathf.Clamp(__1, 10.0f, sectorRad);
                            }
                            else if (!isBoxTask)
                            {
                                float maxCapSpread = Mathf.Max(8.5f, taskRadius * 0.80f);
                                if (__1 > maxCapSpread)
                                    __1 = maxCapSpread;
                            }
                        }
                        else if (!isBoxTask && dTaskSqXZ <= 22.0f * 22.0f)
                        {
                            float maxCapSpread = Mathf.Max(8.5f, taskRadius * 0.80f);
                            if (__1 > maxCapSpread)
                                __1 = maxCapSpread;
                        }
                    }

                    if (VisionConfig.EnableSmartWallSideCover.Value)
                    {
                        Vector3 threatDir = GetEffectiveThreatDirection(__instance.Leader, __instance, __0, __instance.formationDirection);
                        if (threatDir.sqrMagnitude > 0.01f)
                        {
                            __instance.formationDirection = threatDir;
                        }
                    }
                }
                catch (Exception ex)
                {
                    AiCore.ReportPatchException("SendCovers", ex);
                }
            }

            public static void Postfix(long __state)
            {
                HitchProfiler.End(HitchBucket.VanSendCovers, __state);
            }
        }

        [HarmonyPatch(typeof(Squad), nameof(Squad.GetCoveredPositionInArea))]
        public static class Squad_GetCoveredPositionInArea_Patch
        {
            public static bool Prefix(Squad __instance, ref AiDestination __result)
            {
                try
                {
                    if (__instance == null)
                        return true;

                    Order order = __instance.order;
                    bool leaderMissing = order == Order.follow &&
                                         (__instance.Leader == null || __instance.Leader.transform == null);
                    bool phaseObjMissing = order != Order.defend &&
                                           order != Order.follow &&
                                           (BattleManager.instance == null ||
                                            BattleManager.instance.currentPhase == null ||
                                            BattleManager.instance.currentPhase.GetNearestObjective(__instance.moveOrderPosition) == null);
                    if (leaderMissing || phaseObjMissing)
                    {
                        Vector3 basePos = (__instance.Leader != null && __instance.Leader.transform != null)
                            ? __instance.Leader.transform.position
                            : __instance.moveOrderPosition;
                        Vector3 groundPos = ResourcesManager.SetOnGround(basePos, Mathf.Max(5f, __instance.moveOrderRadius));
                        __result = new DestinationWithoutCover(groundPos, SoldierPose.Idle).TryCast<AiDestination>();
                        return false;
                    }

                    if (!VisionConfig.EnableMod.Value ||
                        !VisionConfig.EnableSmartWallSideCover.Value)
                    {
                        return true;
                    }

                    if (__instance.Leader != null && !AiCore.IsLocallyOwnedBot(__instance.Leader))
                        return true;

                    Vector3 refPos = (__instance.Leader != null && __instance.Leader.transform != null)
                        ? __instance.Leader.transform.position
                        : Vector3.zero;
                    Vector3 threatDir = GetEffectiveThreatDirection(__instance.Leader, __instance, refPos, __instance.formationDirection);
                    if (threatDir.sqrMagnitude > 0.01f)
                    {
                        __instance.formationDirection = threatDir;
                    }
                }
                catch { }
                return true;
            }
        }
    }
}
