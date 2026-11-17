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
            if (ai == null || soldier == null)
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

            if (pose == SoldierPose.Idle &&
                soldier.m_pose != SoldierPose.Idle &&
                (soldier.isInHandsAnimation || soldier.IsReloading || soldier.isInBodyAnimation))
            {
                ai.actualPose = soldier.m_pose;
                return;
            }

            AiCore.SoldierFastHandles sh = AiCore.GetOrCreateSoldierHandles(soldier);

            // Indoor Ceiling & Bridge Prone Prevention: prevent dropping Prone under windowsills/stairs or on bridge decks
            if (pose == SoldierPose.Prone && !soldier.isBleeding)
            {
                Vector3 feetPos = soldier.transform.position;
                if (VisionConfig.EnableIndoorCeilingPronePrevention.Value &&
                    AiCore.HasLowCeilingOverhead(feetPos, 3.0f))
                {
                    pose = SoldierPose.Crouch;
                    if (sh != null)
                    {
                        sh.Stance.NoProneUntil = Time.time + 10.0f;
                        if (Time.time >= sh.Stance.NextIndoorCeilingCountTime)
                        {
                            sh.Stance.NextIndoorCeilingCountTime = Time.time + 5.0f;
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
                        sh.Stance.NoProneUntil = Time.time + 4.0f;
                    }
                }
            }

            if (pose >= SoldierPose.Crouch &&
                !ai.IsMoving() &&
                (ai.HasEnemyAndCanFireAtHim || soldier.GetSuppressionValue() >= 8f))
            {
                if (sh != null)
                {
                    float lockUntil = Time.time + 3.5f;
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
                                               ai.HasEnemyAndCanFireAtHim &&
                                               !soldier.IsCharging() &&
                                               !soldier.IsJumping() &&
                                               sh != null &&
                                               Time.time < sh.Stance.PoseLockUntil;

            bool isUrgentTransition = !blockCombativeStandOverride &&
                                      (forceImmediate ||
                                       (soldier.m_pose != SoldierPose.Idle && pose == SoldierPose.Idle && ai.IsMoving()) ||
                                       (soldier.m_pose == SoldierPose.Prone && ai.IsMoving()) ||
                                       (soldier.m_pose == SoldierPose.Idle && pose >= SoldierPose.Crouch && !ai.IsMoving() &&
                                        (ai.HasEnemyAndCanFireAtHim || soldier.GetSuppressionValue() >= 15f)));

            if (!isUrgentTransition && sh != null)
            {
                if (soldier.isInBodyAnimation || Time.time < sh.Stance.PoseLockUntil)
                {
                    ai.actualPose = soldier.m_pose;
                    return;
                }
            }

            if (sh != null)
            {
                sh.Stance.PoseLockUntil = Time.time + 2.8f;
            }

            ai.actualPose = pose;
            soldier.SetPose(pose, true, false, 0.2f);
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
            else if (h != null && AiCore.ResolveTargetDestination(h) != AiCore.CachedDestKind.None && h.CachedCover != null)
            {
                ep = h.CachedCover.GetCoverPosition();
            }
            else if (!ReferenceEquals(soldier, null) && soldier.HasDestinationAssigned && soldier.targetDestination != null)
            {
                ep = soldier.targetDestination.GetCoverPosition();
            }
            else
            {
                ep = ai.MoveDestination;
            }

            if (h != null)
            {
                h.LastEndpointFrame = frame;
                h.NextEndpointCheckTime = now + 0.10f;
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

        internal static bool IsActivelyDuckingFriendlyMuzzle(IntPtr ptr, float now)
        {
            return AiCore.TryGetSoldierHandles(ptr, out var h) &&
                   now < h.Combat.NextFriendlyStepAsideTime &&
                   now < h.Stance.CombatCrouchLockUntil;
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
            if (IsActivelyDuckingFriendlyMuzzle(ptr, Time.time))
                return SoldierPose.Crouch;

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

            // Duck into the final <= 5.5m approach into cover when taking heavy incoming fire
            if (remDistSq <= 5.5f * 5.5f && suppression >= 30f)
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

            float now = Time.time;
            if (now >= h.Stance.NextLocalCoverSeekTime)
            {
                h.Stance.NextLocalCoverSeekTime = now + 1.5f;

                Vector3 myPos = h.Tr.position;
                string myFaction = AiCore.GetCachedFaction(h, now);
                bool isOnBridge = VisionConfig.EnableBridgeCrossingDiscipline.Value &&
                                  AiCore.IsOnBridgeOrNarrowCrossing(myPos);
                Vector3 threatDir = GetEffectiveThreatDirection(soldier, soldier.joinedSquad, myPos, h.Tr.forward);
                CombatCover bestCover = null;
                float bestScoreSq = float.MaxValue;
                float bestRawDistSq = float.MaxValue;

                var octree = CoverManager.staticCovers;
                if (octree != null)
                {
                    var nearby = octree.GetNearby(myPos, 13.5f);
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
                            if (cc == null || cc.destroyed)
                                continue;

                            Vector3 covPos = cc.GetCoverPosition();
                            if (Mathf.Abs(covPos.y - myPos.y) > (isOnBridge ? 1.15f : 2.4f))
                                continue;

                            float dSq = (covPos - myPos).sqrMagnitude;
                            if (dSq * 0.42f >= bestScoreSq)
                                continue;

                            if (isOnBridge && !AiCore.IsSafeBridgeDeckStep(myPos, covPos, out _))
                                continue;

                            if (!cc.IsCoverAvailable(threatDir, myFaction))
                                continue;

                            if (VisionConfig.EnableSmartWallSideCover.Value &&
                                IsPositionOnExposedSideOfWall(covPos, threatDir, null, out _))
                            {
                                continue;
                            }

                            bool isLeewardSheltered = IsPositionShelteredByFrontCover(covPos, threatDir, null);
                            float scoreSq = isLeewardSheltered ? (dSq * 0.42f) : (dSq * 1.45f);
                            if (scoreSq >= bestScoreSq)
                                continue;

                            bestScoreSq = scoreSq;
                            bestRawDistSq = dSq;
                            bestCover = cc;
                        }
                    }
                }

                // Consider nearby idle/wrecked tank or armored vehicle hulls as leeward physical cover if closer/better than static cover!
                if (bestRawDistSq > 4.5f * 4.5f &&
                    TryFindTankHullCoverOrEscortSpot(soldier, myPos, threatDir, false, out Vector3 tankCoverPos, out _) &&
                    (!VisionConfig.EnableFriendlyFireAvoidance.Value || !AiCore.IsCoverInPlayerAimLane(tankCoverPos, threatDir)) &&
                    (!isOnBridge || AiCore.IsSafeBridgeDeckStep(myPos, tankCoverPos, out tankCoverPos)))
                {
                    float tankDistSq = (tankCoverPos - myPos).sqrMagnitude;
                    if (tankDistSq * 0.55f < bestScoreSq)
                    {
                        DestinationWithoutCover tankDest = new DestinationWithoutCover(tankCoverPos, SoldierPose.Crouch);
                        h.Stance.LocalCoverProtectedUntil = now + 6.5f;
                        AiCore.AssignBotCoverOrDestination(ai, soldier, tankDest.Cast<AiDestination>());
                        TankHullCoverUsesCount++;
                        return true;
                    }
                }

                if (bestCover != null)
                {
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
                    h.Stance.LocalCoverProtectedUntil = now + 6.5f;
                    AiCore.AssignBotCoverOrDestination(ai, soldier, bridgeDest.Cast<AiDestination>());
                    return true;
                }
            }

            if (soldier.HasDestinationAssigned)
            {
                soldier.TryToGetToActualCover();
                if (!soldier.IsOnCover() && !ai.IsMoving() && !ai.HasPath && !ai.HasPathRequest)
                {
                    if (ai.moveDirectlyToward_end > now)
                        ai.moveDirectlyToward_end = 0f;
                    AiCore.ResetBotPathLockout(ai);
                    ai.UpdatePath();
                }
                return soldier.IsOnCover() || ai.IsMoving() || ai.HasPathRequest;
            }

            return false;
        }

        private static bool HasClearLineOfFireAtHeight(Soldier soldier, Vector3 targetPos, float dist, float lowerBarrelHeight, float upperBarrelHeight, float maxRayDist)
        {
            if (soldier == null || dist <= 1.5f)
                return false;

            return HasClearLineOfFireFromPosition(soldier.transform.position, soldier.transform.root, targetPos, dist, lowerBarrelHeight, upperBarrelHeight, maxRayDist);
        }

        private static bool HasClearLineOfFireFromPosition(
            Vector3 basePos,
            Transform ignoreRoot,
            Vector3 targetPos,
            float dist,
            float lowerBarrelHeight,
            float upperBarrelHeight,
            float maxRayDist)
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
                if (AiCore.RaycastEnvironment(lowerOrigin - dirLower * 0.10f, dirLower, checkDistLower + 0.10f, ignoreRoot, out _))
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
                    if (AiCore.RaycastEnvironment(upperOrigin - dirUpper * 0.10f, dirUpper, checkDistUpper + 0.10f, ignoreRoot, out _))
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
            float maxEyeLintelDist)
        {
            if (!HasClearLineOfFireFromPosition(basePos, ignoreRoot, targetPos, dist, lowerBarrelHeight, upperBarrelHeight, maxRayDist))
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
                        AiCore.RaycastEnvironment(eyeOrigin - dirEye * 0.08f, dirEye, checkEyeDist + 0.08f, ignoreRoot, out _))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static bool HasClearLineOfFireFromProne(Soldier soldier, Vector3 targetPos, float dist)
        {
            return HasClearLineOfFireAtHeight(soldier, targetPos, dist, 0.22f, 0.32f, 15.0f);
        }

        private static bool HasClearLineOfFireFromCrouch(Soldier soldier, Vector3 targetPos, float dist)
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
                2.8f);
        }

        private static bool HasClearLineOfFireFromStanding(Soldier soldier, Vector3 targetPos, float dist)
        {
            if (soldier == null || dist <= 1.5f)
                return false;

            return HasClearLineOfFireAndSightFromPosition(
                soldier.transform.position,
                soldier.transform.root,
                targetPos,
                dist,
                1.22f,
                1.36f,
                18.0f,
                1.56f,
                3.5f);
        }

        private static bool TrySidestepWallObstruction(SoldierAI ai, Soldier soldier, Vector3 targetPos, float dist)
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

                    bool crouchStepClear = HasClearLineOfFireAndSightFromPosition(stepPos, myRoot, targetPos, dist, 0.64f, 0.78f, 18.0f, 1.04f, 2.8f);
                    bool standStepClear = !crouchStepClear &&
                                          HasClearLineOfFireAndSightFromPosition(stepPos, myRoot, targetPos, dist, 1.22f, 1.36f, 18.0f, 1.56f, 3.5f);
                    if (crouchStepClear || standStepClear)
                    {
                        sh.Stance.LocalCoverProtectedUntil = Time.time + 6.5f;
                        if (soldier.IsOnCover() || soldier.HasDestinationAssigned)
                        {
                            SoldierPose stepPose = crouchStepClear ? SoldierPose.Crouch : SoldierPose.Idle;
                            DestinationWithoutCover sideDest = new DestinationWithoutCover(stepPos, stepPose);
                            soldier.CoverPosition(sideDest.Cast<AiDestination>());
                        }
                        if ((ai.MoveDestination - curPos).sqrMagnitude <= 3.0f * 3.0f)
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

        // =====================================================================
        // Smart Wall-Side Cover & Threat Direction (Stand Leeward of Weapons Fire)
        // =====================================================================
        private static Vector3 GetEffectiveThreatDirection(Soldier soldier, Squad squad, Vector3 referencePos, Vector3 fallbackDir)
        {
            if (soldier != null && soldier.aiController != null &&
                AiCore.TryGetSpottableCenter(soldier.aiController.visibleTarget, out Vector3 ownTargetPos))
            {
                Vector3 toTarget = ownTargetPos - referencePos;
                toTarget.y = 0f;
                if (toTarget.sqrMagnitude > 0.25f)
                    return toTarget.normalized;
            }

            string faction = soldier != null
                ? AiCore.GetCachedFaction(soldier)
                : (squad != null && squad.Leader != null ? AiCore.GetCachedFaction(squad.Leader) : null);

            bool hasLiveGunfire = VisionPatches.TryGetDominantEnemyGunfireDirection(
                referencePos,
                faction,
                145.0f,
                3.8f,
                out Vector3 gunfireDir,
                out float gunfireDist);

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
                    // If an unseen enemy is actively firing much closer to this soldier (< 48m and < 0.65x squad target dist),
                    // take cover on the leeward side of the live gunfire rather than exposing our back to the shooter!
                    if (hasLiveGunfire && gunfireDist <= 48.0f && (gunfireDist * gunfireDist) < bestDistSq * 0.42f)
                        return gunfireDir;

                    return bestToEnemy.normalized;
                }
            }

            if (hasLiveGunfire)
                return gunfireDir;

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

        private static bool IsPositionShelteredByFrontCover(Vector3 pos, Vector3 threatDir, Transform ignoreRoot)
        {
            if (threatDir.sqrMagnitude < 0.001f)
                return false;

            return AiCore.IsMovementBlockedByObstacle(pos + Vector3.up * 0.46f, threatDir, 1.85f) ||
                   AiCore.IsMovementBlockedByObstacle(pos + Vector3.up * 0.85f, threatDir, 1.85f);
        }

        private static bool IsPositionOnExposedSideOfWall(Vector3 pos, Vector3 threatDir, Transform ignoreRoot, out RaycastHit rearWallHit)
        {
            rearWallHit = default;
            if (threatDir.sqrMagnitude < 0.001f)
                return false;

            if (IsPositionShelteredByFrontCover(pos, threatDir, ignoreRoot))
                return false;

            // Never mistake the interior back wall of a room, bunker, or pillbox for an exposed exterior wall face
            if (AiCore.HasLowCeilingOverhead(pos, 3.5f))
                return false;

            int obsMask = AiCore.GetMovementObstacleMask();
            if (Physics.Raycast(pos + Vector3.up * 0.82f, -threatDir, out rearWallHit, 1.95f, obsMask, QueryTriggerInteraction.Ignore) &&
                Mathf.Abs(rearWallHit.normal.y) < 0.58f)
            {
                return true;
            }

            if (Physics.Raycast(pos + Vector3.up * 0.46f, -threatDir, out rearWallHit, 1.95f, obsMask, QueryTriggerInteraction.Ignore) &&
                Mathf.Abs(rearWallHit.normal.y) < 0.58f)
            {
                return true;
            }

            return false;
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

            if (currentIsOutdoor && AiCore.IsDirectionBlockedByEnvironment(candidatePos + Vector3.up * 0.85f, Vector3.up, 6.0f, ignoreRoot))
                return false;

            if (!IsPositionShelteredByFrontCover(candidatePos, threatDir, ignoreRoot))
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
            bool canEngageOrMountOverWall = ai.HasEnemyAndCanFireAtHim ||
                                            (ai.visibleTarget != null && (soldier.IsOnCover() || soldier.IsTowardWall() || isOnBridge));

            if (bip == null || ai.IsMoving() || !canEngageOrMountOverWall || dist <= 5.0f)
                return;

            AiCore.SoldierFastHandles bipH = AiCore.GetOrCreateSoldierHandles(soldier);
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

            if (soldier.m_pose != SoldierPose.Idle && (isOnBridge || soldier.IsOnCover() || soldier.IsTowardWall() || IsMuzzleBlockedByWall(soldier, targetPos, dist)))
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

            if (dist > 8.5f && canTryProneBipod && HasClearLineOfFireFromProne(soldier, targetPos, dist))
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
            }

            public static bool Prefix(SoldierAI __instance, out SequentialUpdateState __state)
            {
                __state = default;
                long modPreStart = HitchProfiler.Begin();
                bool runVanilla = true;
                try
                {
                    if (!VisionConfig.EnableMod.Value || ReferenceEquals(__instance, null))
                        return true;

                    AiCore.SoldierFastHandles h = AiCore.GetOrCreateSoldierHandlesByAi(__instance);
                    __state.Handles = h;
                    if (!AiCore.IsLocallyOwnedBot(h) || h.Ref.IsOnVehicle())
                        return true;

                    Soldier soldier = h.Ref;
                    __state.PrevPointGunEndTime = soldier.pointGunEndTime;

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
                    //   - Watching (in cover / bipod / squad aware): 0.18s - 0.32s
                    //   - Caught Out (moving / patrolling out of contact): 0.38s - 0.65s
                    if (VisionConfig.EnableDynamicHumanReactionTime.Value && Time.time < soldier.alertEndTime)
                    {
                        float vet01 = GetVeterancy01(soldier);
                        float microJitter = (AiCore.GetDeterministicHash(soldier.Pointer, 173) & 15) * 0.011f;
                        bool isWatching = !__instance.IsMoving() &&
                                          (hasActiveContact || isSquadAware || soldier.IsOnCover() || soldier.UsingBipod());
                        float stage1Delay = isWatching
                            ? (Mathf.Lerp(0.27f, 0.18f, vet01) + microJitter * 0.45f)
                            : (Mathf.Lerp(0.52f, 0.36f, vet01) + microJitter);
                        float maxStartTime = Time.time - (2.50f - stage1Delay);
                        if (soldier.alertStartTime > maxStartTime)
                        {
                            soldier.alertStartTime = maxStartTime;
                        }
                    }
                    else if (VisionConfig.EnableThreatSectorConcentration.Value &&
                             (hasActiveContact || isSquadAware || soldier.GetSuppressionValue() >= 3f))
                    {
                        float vet01 = GetVeterancy01(soldier);
                        float minAlertElapsed = (!__instance.IsMoving() && hasActiveContact)
                            ? Mathf.Lerp(1.95f, 2.35f, vet01)
                            : Mathf.Lerp(1.65f, 2.15f, vet01);
                        float maxStartTime = Time.time - minAlertElapsed;
                        if (soldier.alertStartTime > maxStartTime)
                        {
                            soldier.alertStartTime = maxStartTime;
                        }
                    }

                    AiCore.SoldierFastHandles preH = __state.Handles ?? AiCore.GetOrCreateSoldierHandles(soldier);
                    float preNow = Time.time;
                    EvaluateSoldierBandageStateForTick(__instance, soldier, preH, preNow, out bool preBleedWithBandage, out bool safeToBandage, out _, out _);
                    if (preBleedWithBandage)
                    {
                        if (!safeToBandage)
                        {
                            // Keep vanilla SequentialUpdate combat-responsive (never self-bandaging in an enemy's face!)
                            if (soldier.isBleeding)
                            {
                                soldier.isBleeding = false;
                                __state.WasBleeding = true;
                            }
                        }
                        else if (VisionConfig.BandageOnlyAfterFindingCover.Value)
                        {
                            float deadline = preH != null ? preH.Stance.BleedCoverDeadline : 0f;
                            if (deadline <= 0f)
                            {
                                SoldierPersonality bleedPers = GetPersonality(soldier);
                                float seekWindow = (bleedPers == SoldierPersonality.Rifleman_Cautious || bleedPers == SoldierPersonality.Medic_Triage)
                                    ? 4.5f
                                    : (bleedPers == SoldierPersonality.Assaulter_Shock ? 2.2f : 3.5f);
                                deadline = preNow + seekWindow;
                                if (preH != null)
                                    preH.Stance.BleedCoverDeadline = deadline;
                            }

                            if (!soldier.IsOnCover() && preNow < deadline)
                            {
                                bool movingToCover = (VisionConfig.EnableTacticalGoapPlanner.Value &&
                                                      AiCore.CanBotMoveAutonomously(soldier) &&
                                                      !soldier.IsCharging()) ||
                                                     TrySeekNearbyCover(__instance, soldier);
                                if (movingToCover && soldier.isBleeding)
                                {
                                    soldier.isBleeding = false;
                                    __state.WasBleeding = true;
                                }
                            }
                        }
                    }
                }
                catch { }
                finally
                {
                    HitchProfiler.End(HitchBucket.ModSeqUpdate, modPreStart);
                    if (runVanilla)
                        __state.VanillaStartStamp = HitchProfiler.Begin();
                }
                return true;
            }

            public static void Postfix(SoldierAI __instance, SequentialUpdateState __state)
            {
                if (__state.VanillaStartStamp > 0L)
                    HitchProfiler.End(HitchBucket.VanSoldierSeq, __state.VanillaStartStamp);

                long modPostStart = HitchProfiler.Begin();
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

                    if (!VisionConfig.EnableMod.Value || h == null || soldier == null || h.Tr == null || !soldier.IsAlive || soldier.IsPlayer())
                        return;

                    if (__state.SkippedForPing || AiCore.ShouldSkipBotCycleForNetworkPing())
                        return;

                    IntPtr ptr = h.Ptr;

                    CheckAndApplyFieldPromotion(soldier);

                    if (!AiCore.IsLocallyOwnedBot(h) || soldier.IsOnVehicle() || soldier.HasSurrended())
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

                    float nowBleed = Time.time;
                    EvaluateSoldierBandageStateForTick(
                        __instance,
                        soldier,
                        h,
                        nowBleed,
                        out bool isBleedingWithBandage,
                        out bool safeToBandageNow,
                        out string bleedBlockReason,
                        out float bleedGfDist);
                    if (isBleedingWithBandage)
                    {
                        float deadline = h.Stance.BleedCoverDeadline;
                        if (deadline <= 0f)
                        {
                            SoldierPersonality bleedPers = personality;
                            float seekWindow = (bleedPers == SoldierPersonality.Rifleman_Cautious || bleedPers == SoldierPersonality.Medic_Triage)
                                ? 4.5f
                                : (bleedPers == SoldierPersonality.Assaulter_Shock ? 2.2f : 3.5f);
                            deadline = nowBleed + seekWindow;
                            h.Stance.BleedCoverDeadline = deadline;
                        }

                        if (!safeToBandageNow)
                        {
                            // Active shooter / close gunfire! Defer cover-bandage deadline and fall through into CQB & GOAP
                            // so the soldier returns fire or breaks LOS first instead of pulling out a bandage!
                            h.Stance.BleedCoverDeadline = Mathf.Max(deadline, nowBleed + 1.85f);
                            CountBandageBlocked(ptr, nowBleed);
                            BotActionTrace.RecordEvent(soldier, BotTraceEventKind.BandageBlocked, bleedBlockReason, __instance.distFromTarget, bleedGfDist);
                        }
                        else if (!VisionConfig.BandageOnlyAfterFindingCover.Value)
                        {
                            if (!soldier.isInBodyAnimation && !soldier.isInHandsAnimation)
                            {
                                SetBotPose(__instance, soldier, SoldierPose.Crouch);
                                __instance.TryStopBleeding();
                            }
                            return;
                        }
                        else
                        {
                            bool readyToBandage = soldier.IsOnCover() || nowBleed >= deadline;
                            if (readyToBandage)
                            {
                                SoldierPose healPose = (soldier.IsOnCover() || isOnBridge) ? SoldierPose.Crouch : SoldierPose.Prone;
                                SetBotPose(__instance, soldier, healPose);
                                if (!soldier.isInBodyAnimation && !soldier.isInHandsAnimation)
                                {
                                    __instance.TryStopBleeding();
                                }
                                return;
                            }
                            else if (!VisionConfig.EnableTacticalGoapPlanner.Value || !AiCore.CanBotMoveAutonomously(soldier) || soldier.IsCharging())
                            {
                                if (!__instance.IsMoving() && !IsAsyncPathRefreshPending(__instance, soldier, h.Tr.position, nowBleed))
                                {
                                    TrySeekNearbyCover(__instance, soldier);
                                }
                                SetBotPose(__instance, soldier, SoldierPose.Idle);
                                return;
                            }
                        }
                    }
                    else
                    {
                        h.Stance.BleedCoverDeadline = 0f;
                    }

                    GetEffectivePeekParameters(soldier, vet01, out float maxPeekSupp, out float duckBackSupp, out float peekCooldown);

                    // Record visual contact for 15m Local Contact Callouts & Last-Known Suppression (or legacy frag fallback when GOAP is OFF)
                    if (__instance.visibleTarget != null)
                    {
                        RecordAndBroadcastVisualContact(soldier, __instance.visibleTarget);
                    }
                    else if (!VisionConfig.EnableTacticalGoapPlanner.Value)
                    {
                        TryFlushLastKnownEnemyWithGrenade(__instance, soldier);
                    }

                    // Lift any indoor or bridge soldier that vanilla AI placed into Prone
                    if (soldier.m_pose == SoldierPose.Prone)
                    {
                        if (VisionConfig.EnableIndoorCeilingPronePrevention.Value &&
                            AiCore.HasLowCeilingOverhead(soldier.transform.position, 3.0f))
                        {
                            SetBotPose(__instance, soldier, SoldierPose.Crouch, true);
                            h.Stance.NoProneUntil = Time.time + 10.0f;
                            if (Time.time >= h.Stance.NextIndoorCeilingCountTime)
                            {
                                h.Stance.NextIndoorCeilingCountTime = Time.time + 5.0f;
                                IndoorPronePreventedCount++;
                            }
                        }
                        else if (isOnBridge)
                        {
                            bool bridgeMoving = __instance.IsMoving() || IsAsyncPathRefreshPending(__instance, soldier, h.Tr.position, Time.time);
                            SetBotPose(__instance, soldier, bridgeMoving ? SoldierPose.Idle : SoldierPose.Crouch, true);
                            h.Stance.NoProneUntil = Time.time + 4.0f;
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
                            bool crouchClearNow = HasClearLineOfFireFromCrouch(soldier, peekTargetPos, __instance.distFromTarget);
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

                    if (personality != SoldierPersonality.None)
                    {
                        if (personality == SoldierPersonality.Leader_Tactician &&
                            SoldierAI.next_requestingSupport > Time.time + 6.0f)
                        {
                            SoldierAI.next_requestingSupport = Time.time + 6.0f;
                        }
                        else if (!VisionConfig.EnableTacticalGoapPlanner.Value &&
                                 (personality == SoldierPersonality.Assaulter_Breacher ||
                                  personality == SoldierPersonality.Rifleman_Grenadier ||
                                  personality == SoldierPersonality.AT_Demolitionist) &&
                                 UnityEngine.Random.Range(0, 100) > 60 &&
                                 AiCore.TryGetSpottableCenter(__instance.visibleTarget, out Vector3 grenTargetPos) &&
                                 !__instance.visibleTarget.IsWheeledVehicleOrTank())
                        {
                            TryThrowTacticalFragGrenade(
                                soldier,
                                __instance.visibleTarget,
                                grenTargetPos,
                                __instance.distFromTarget,
                                9.0f,
                                34.0f,
                                9.0f,
                                Time.time,
                                sayVoice: false,
                                cooldownSeconds: 8.0f);
                        }
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

                            if (TryEvaluateVehicleTarget(
                                soldier,
                                targetVeh,
                                __instance.distFromTarget,
                                out Soldier exposedCrew,
                                out _,
                                out _))
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
                            else if (!hasStowedAtReady)
                            {
                                // Switch any rifleman, MG gunner, or dry AT unit away from an invulnerable tank
                                // to visible enemy infantry or bailed crewmen advancing behind or beside the tank!
                                if (TryFindVisibleFootEnemy(soldier, out Spottable altEnemy, out float altDist))
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
                                    isEvadingArmor = TryEvadeArmorToCover(__instance, soldier, targetVeh, __instance.distFromTarget);
                                    if (isEvadingArmor)
                                    {
                                        __instance.visibleTarget = null;
                                        h.Stance.CombatCrouchLockUntil = 0f;
                                    }
                                    else
                                    {
                                        h.Stance.CombatCrouchLockUntil = Time.time + 3.5f;
                                    }
                                    if (Time.time >= h.Combat.NextTankDisciplineCountTime)
                                    {
                                        h.Combat.NextTankDisciplineCountTime = Time.time + 3.0f;
                                        UselessTankFirePrevented++;
                                    }
                                }
                            }
                        }
                    }

                    bool isGunner = IsMachineGunnerOrHasBipod(soldier, out AttachmentBipod bipod);
                    bool isHighBipodStanding = Time.time < h.Stance.BipodStandUntil;
                    float dist = __instance.distFromTarget;

                    if (VisionConfig.EnablePointBlankMelee.Value &&
                        ((__instance.HasEnemyAndCanFireAtHim && dist <= 45f) || soldier.IsCharging() || soldier.isAlerted))
                    {
                        TryEnsureBayonetMounted(soldier);
                    }

                    VehicleRepairTask repairTask = null;
                    bool isRepairingVehicle = false;
                    if (VisionConfig.EnableSmartTankCrewRepair.Value && soldier.targetDestination != null)
                    {
                        repairTask = soldier.targetDestination.TryCast<VehicleRepairTask>();
                        if (repairTask != null && repairTask.VehicleStillneedsRepair && repairTask.vehToRepair != null)
                        {
                            isRepairingVehicle = true;
                            Vector3 shelterPos = GetShelteredRepairPosition(repairTask.vehToRepair, soldier, out _);
                            repairTask.targetPos = shelterPos;
                            repairTask.pose = SoldierPose.Crouch;
                            AiCore.SetBotMoveDestinationAndRefreshPath(__instance, shelterPos, 0.64f);
                        }
                    }

                    bool isHealingTeammate = IsActivelyHealingTeammate(soldier, out _);
                    bool isStaffingEmplacement = IsActivelyHeadingToEmplacement(soldier, Time.time);
                    bool goapHandlesCqb = VisionConfig.EnableTacticalGoapPlanner.Value &&
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

                    // Single-authority GOAP emergence: only run pre-GOAP melee fallback when Tactical GOAP isn't handling this bot!
                    if (inPointBlankMelee && meleeEnemy != null && !goapHandlesCqb)
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

                    // Execute 3-Tier Bitmask GOAP Planner & Squad Blackboard (Pin/Flank/Smoke/Overwatch/Rescue/PointBlankCQB)
                    if (VisionConfig.EnableTacticalGoapPlanner.Value && !isRepairingVehicle && !isHealingTeammate)
                    {
                        ExecuteSoldierGoapTick(
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

                    if (VisionConfig.EnableSmartWallSideCover.Value &&
                        !isOnBridge &&
                        !isEvadingArmor &&
                        !isRepairingVehicle &&
                        !isHealingTeammate &&
                        !isStaffingEmplacement &&
                        !soldier.IsCharging() &&
                        !inPointBlankMelee &&
                        (!__instance.HasEnemyAndCanFireAtHim || dist >= 22f) &&
                        !soldier.UsingBipod() &&
                        !soldier.IsBipodTransitioning())
                    {
                        if (Time.time >= h.Stance.NextWallSideCheckTime)
                        {
                            h.Stance.NextWallSideCheckTime = Time.time + 0.90f;
                            Vector3 threatDir = GetEffectiveThreatDirection(soldier, soldier.joinedSquad, soldier.transform.position, soldier.transform.forward);
                            if (threatDir.sqrMagnitude > 0.01f)
                            {
                                DestinationWithoutCover noCoverDest = soldier.targetDestination != null
                                    ? soldier.targetDestination.TryCast<DestinationWithoutCover>()
                                    : null;

                                if (noCoverDest != null &&
                                    TryFindShelteredSideOfWall(noCoverDest.targetPos, threatDir, soldier.transform.root, out Vector3 shelteredDestPos))
                                {
                                    noCoverDest.targetPos = shelteredDestPos;
                                    noCoverDest.pose = SoldierPose.Crouch;
                                    AiCore.SetBotMoveDestinationAndRefreshPath(__instance, shelteredDestPos);
                                    h.Stance.NextWallSideCheckTime = Time.time + 3.5f;
                                    WallSideRepositionsCount++;
                                }
                                else if (!VisionConfig.EnableTacticalGoapPlanner.Value &&
                                         !__instance.IsMoving() &&
                                         !IsAsyncPathRefreshPending(__instance, soldier, soldier.transform.position, Time.time) &&
                                         !IsInCommittedFiringHalt(ptr, Time.time) &&
                                         IsPositionOnExposedSideOfWall(soldier.transform.position, threatDir, soldier.transform.root, out _))
                                {
                                    if (soldier.joinedSquad != null)
                                    {
                                        soldier.joinedSquad.formationDirection = threatDir;
                                    }

                                    if (TryFindShelteredSideOfWall(soldier.transform.position, threatDir, soldier.transform.root, out Vector3 shelteredPos))
                                    {
                                        if (noCoverDest != null)
                                        {
                                            noCoverDest.targetPos = shelteredPos;
                                            noCoverDest.pose = SoldierPose.Crouch;
                                        }
                                        else
                                        {
                                            soldier.TryToGetToActualCover();
                                            if (soldier.targetDestination == null ||
                                                IsPositionOnExposedSideOfWall(soldier.targetDestination.GetCoverPosition(), threatDir, soldier.transform.root, out _))
                                            {
                                                DestinationWithoutCover newDest = new DestinationWithoutCover(shelteredPos, SoldierPose.Crouch);
                                                AiCore.AssignBotCoverOrDestination(__instance, soldier, newDest.Cast<AiDestination>());
                                            }
                                        }

                                        AiCore.SetBotMoveDestinationAndRefreshPath(__instance, shelteredPos);
                                        h.Stance.NextWallSideCheckTime = Time.time + 4.0f;
                                        WallSideRepositionsCount++;
                                    }
                                    else if (soldier.HasDestinationAssigned &&
                                             (__instance.HasEnemyAndCanFireAtHim || suppression >= 5f || soldier.isAlerted))
                                    {
                                        soldier.TryToGetToActualCover();
                                        h.Stance.NextWallSideCheckTime = Time.time + 3.0f;
                                    }
                                }
                            }
                        }
                    }

                    float proneSuppThreshold = GetEffectiveProneSuppressionThreshold(soldier, vet01);

                    if (VisionConfig.EnforceStandingWhenOutOfCombat.Value && !soldier.IsStealth)
                    {
                        __instance.pose = SoldierPose.Idle;

                        if (soldier.targetDestination != null)
                        {
                            DestinationWithoutCover noCoverDest = soldier.targetDestination.TryCast<DestinationWithoutCover>();
                            if (noCoverDest != null && noCoverDest.pose == SoldierPose.Prone && suppression < proneSuppThreshold)
                            {
                                noCoverDest.pose = SoldierPose.Crouch;
                            }
                        }
                    }

                    bool isEffectivelyMoving = __instance.IsMoving() ||
                                               IsAsyncPathRefreshPending(__instance, soldier, h.Tr.position, Time.time);

                    if (soldier.UsingBipod() || soldier.IsBipodTransitioning())
                    {
                        __instance.actualPose = soldier.m_pose;
                    }
                    else if (isRepairingVehicle)
                    {
                        SetBotPose(__instance, soldier, SoldierPose.Crouch);
                    }
                    else if (isEffectivelyMoving && (__instance.IsMoving() || !__instance.HasEnemyAndCanFireAtHim || soldier.IsCharging() || inPointBlankMelee))
                    {
                        bool inFallbackSprint = IsInIndividualOrGoapFallback(ptr, Time.time);
                        Vector3 moveEndpoint = GetActualMoveEndpoint(__instance, soldier);
                        float remDistSq = (h.Tr.position - moveEndpoint).sqrMagnitude;
                        bool stealthCrouch = soldier.IsStealth &&
                                             soldier.joinedSquad != null &&
                                             soldier.joinedSquad.Leader != null &&
                                             soldier.joinedSquad.Leader.m_pose > SoldierPose.Idle;

                        // If caught moving in the open under MG/rifle suppression (> 8.5m from destination), seek nearby local cover within 13.5m
                        // (including deck-safe bridge parapets/pillars when EnableBridgeCrossingDiscipline is active).
                        // When Tactical GOAP is enabled, GOAP's SeekEmergencyCover action handles this authoritatively.
                        if (!VisionConfig.EnableTacticalGoapPlanner.Value &&
                            suppression >= 20f &&
                            !soldier.IsOnCover() &&
                            !HasActiveGoapCoverLock(ptr, Time.time) &&
                            !inFallbackSprint &&
                            !isEvadingArmor &&
                            !isHealingTeammate &&
                            !isStaffingEmplacement &&
                            !soldier.IsCharging() &&
                            !inPointBlankMelee &&
                            remDistSq > 8.5f * 8.5f)
                        {
                            TrySeekNearbyCover(__instance, soldier);
                        }

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
                        if (inPointBlankMelee)
                        {
                            SetBotPose(__instance, soldier, GetDesiredMeleePose(meleeEnemy, dist), true);
                        }
                        else if (isPeeking || isHighBipodStanding)
                        {
                            __instance.actualPose = soldier.m_pose;
                        }
                        else if (__instance.HasEnemyAndCanFireAtHim && !soldier.IsCharging())
                        {
                            if (VisionConfig.NeverShootStandingInOpen.Value && dist > 4.5f)
                            {
                                Vector3 targetPos = AiCore.TryGetSpottableCenter(__instance.visibleTarget, out Vector3 validStandTargetPos)
                                    ? validStandTargetPos
                                    : (soldier.transform.position + soldier.transform.forward * Mathf.Max(dist, 15f));

                                bool crouchClear = HasClearLineOfFireFromCrouch(soldier, targetPos, dist);
                                bool standClear = !crouchClear && HasClearLineOfFireFromStanding(soldier, targetPos, dist);
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
                                else if (canPeekNow && !crouchClear && soldier.m_pose == SoldierPose.Idle && suppression < 32f && !soldier.IsReloading)
                                {
                                    __instance.actualPose = SoldierPose.Idle;
                                }
                                else
                                {
                                    bool inMidBoundFiringHalt = IsInCommittedFiringHalt(ptr, Time.time);
                                    bool canGoProne = Time.time >= h.Stance.NoProneUntil &&
                                                      !soldier.IsOnCover() &&
                                                      !soldier.IsTowardWall() &&
                                                      (!inMidBoundFiringHalt || isGunner || suppression >= 65f);

                                    bool shouldBeProne = suppression >= proneSuppThreshold ||
                                                         isGunner ||
                                                         (soldier.m_pose == SoldierPose.Prone && suppression >= proneSuppThreshold * 0.45f);

                                    SoldierPose combatPose = SoldierPose.Crouch;
                                    if (shouldBeProne &&
                                        canGoProne &&
                                        HasClearLineOfFireFromProne(soldier, targetPos, dist))
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
                            bool inCombatHold = Time.time < h.Stance.CombatStanceHoldUntil;
                            bool stealthCrouch = soldier.IsStealth &&
                                                 soldier.joinedSquad != null &&
                                                 soldier.joinedSquad.Leader != null &&
                                                 soldier.joinedSquad.Leader.m_pose > SoldierPose.Idle;
                            CombatCover activeCover = soldier.targetDestination != null
                                ? soldier.targetDestination.TryCast<CombatCover>()
                                : null;
                            bool onActualCombatCover = soldier.IsOnCover() ||
                                                       (activeCover != null && (soldier.transform.position - activeCover.GetCoverPosition()).sqrMagnitude <= 3.2f * 3.2f);
                            SquadTask currentTask = soldier.joinedSquad != null ? soldier.joinedSquad.currentTask : null;
                            bool inObjectiveZone = currentTask != null && currentTask.IsInsideTaskArea(soldier.transform.position, 1.15f);
                            bool isSquadAlerted = soldier.isAlerted || (soldier.joinedSquad != null && soldier.joinedSquad.IsEnemyAwareOfThisSquad);
                            Vector3 myPos = soldier.transform.position;
                            Transform myRoot = soldier.transform.root;
                            Vector3 threatDir = GetEffectiveThreatDirection(soldier, soldier.joinedSquad, myPos, soldier.transform.forward);
                            bool isShelteredByWall = VisionConfig.EnableSmartWallSideCover.Value &&
                                                     IsPositionShelteredByFrontCover(myPos, threatDir, myRoot);
                            bool hasMortar = __instance.HeldMortar != null;

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
                                    Vector3 lookFwd = soldier.transform.forward;
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

                            if (shouldStandAtCoverOrTrench)
                            {
                                SetBotPose(__instance, soldier, SoldierPose.Idle);
                            }
                            else if (!holdDefensiveCrouch && !inCombatHold && suppression < 8f && !stealthCrouch && !hasMortar)
                            {
                                SetBotPose(__instance, soldier, SoldierPose.Idle);
                            }
                            else if (soldier.m_pose == SoldierPose.Prone &&
                                     (inCombatHold || isGunner || suppression >= proneSuppThreshold * 0.45f) &&
                                     Time.time >= h.Stance.NoProneUntil)
                            {
                                __instance.pose = SoldierPose.Prone;
                                __instance.actualPose = SoldierPose.Prone;
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
                            : (soldier.transform.position + soldier.transform.forward * 25f);
                        HandleGunnerBipodAndWallMounting(__instance, soldier, bipod, targetPos, dist);
                    }

                    // Stabilize upper-body aiming posture (pointGunEndTime) vs wall proximity (isTowardWallTps):
                    // Native SoldierAI.SequentialUpdate (0x18055F7F2) and GetBestVisibleEnemy (0x18074AEEC) call
                    // SetAlertedFor(10f) whenever visibleTarget != null, even if targetInWeaponRange is false,
                    // the target is an invulnerable tank, or the soldier's muzzle is blocked by cover.
                    bool isWallBlockedNow = Time.time < h.Stance.WallBlockedUntil;
                    if (!__instance.HasEnemyAndCanFireAtHim || (isWallBlockedNow && !isPeeking))
                    {
                        soldier.pointGunEndTime = (soldier.isTowardWallTps || (isWallBlockedNow && !isPeeking))
                            ? Mathf.Min(__state.PrevPointGunEndTime, Time.time)
                            : __state.PrevPointGunEndTime;
                    }
                    else if (!isWallBlockedNow && soldier.isTowardWallTps && !soldier.IsTowardWall())
                    {
                        soldier.isTowardWallTps = false;
                    }

                    float maxLiberateSupp = (personality == SoldierPersonality.Medic_Heroic || personality == SoldierPersonality.Leader_OldGuard)
                        ? 52f
                        : ((personality == SoldierPersonality.Rifleman_Cautious || personality == SoldierPersonality.Medic_Triage) ? 16f : 35f);

                    if (VisionConfig.EnableComradeLiberation.Value &&
                        !VisionConfig.EnableTacticalGoapPlanner.Value &&
                        suppression < maxLiberateSupp &&
                        !isEvadingArmor &&
                        !isRepairingVehicle &&
                        !isStaffingEmplacement &&
                        !inPointBlankMelee &&
                        !soldier.UsingBipod() &&
                        (!__instance.HasEnemyAndCanFireAtHim || dist > 28f || personality == SoldierPersonality.Medic_Heroic))
                    {
                        if (Time.time >= h.Combat.NextLiberateCheckTime)
                        {
                            h.Combat.NextLiberateCheckTime = Time.time + 1.5f;
                            TryLiberateNearbyComrade(__instance, soldier, personality);
                        }
                    }
                }
                catch { }
                finally
                {
                    HitchProfiler.End(HitchBucket.ModSeqUpdate, modPostStart);
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
                    if (!AiCore.IsLocallyOwnedBot(soldier))
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

                    // Immediately expire GOAP plan inertia after a brief human startle (0.16s-0.43s) so shot-at bots
                    // re-evaluate their plan right away instead of continuing an old open-ground advance!
                    if (sh != null && now < sh.Goap.Plan.PlanExpireTime)
                    {
                        float startle = 0.16f + ((AiCore.GetDeterministicHash(ptr, 211) & 15) * 0.018f);
                        sh.Goap.Plan.NextReplanTime = Mathf.Min(sh.Goap.Plan.NextReplanTime, now + startle);
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

                    if (VisionConfig.EnableSelfPreservationStance.Value &&
                        !soldier.IsOnVehicle() &&
                        !IsInIndividualOrGoapFallback(ptr, now))
                    {
                        bool isCharging = soldier.IsCharging();
                        if (isCharging)
                        {
                            Squad cSq = soldier.joinedSquad;
                            FactionCommandProfile cProf = GetFactionCommandProfile(soldier.faction);
                            BotMicroVariance cMicro = BotMicroVariance.FromSoldier(ptr);
                            SquadBlackboard cBoard = default;
                            if (cSq != null)
                                _squadBlackboards.TryGetValue(cSq.Pointer, out cBoard);
                            bool fanatical = IsTrueGyokusaiOrBanzaiCondition(in cProf, cSq, in cBoard);
                            float balkSupp = Mathf.Lerp(36.0f, 46.0f, vet01) / Mathf.Max(0.75f, cMicro.CautionCostMult);
                            if (pers == SoldierPersonality.Assaulter_Shock || pers == SoldierPersonality.Leader_Spearhead)
                                balkSupp += 12.0f;
                            else if (pers == SoldierPersonality.Rifleman_Cautious || pers == SoldierPersonality.Medic_Triage)
                                balkSupp -= 8.0f;

                            if (fanatical || curSupp < balkSupp || (__instance.distFromTarget > 0.1f && __instance.distFromTarget <= 12.0f))
                                return;

                            IndividualSelfPreservationBalksCount++;
                        }

                        bool soughtCover = TrySeekNearbyCover(__instance, soldier);
                        if (soughtCover && isCharging && sh != null)
                        {
                            sh.Stance.LocalCoverProtectedUntil = now + 5.5f;
                        }

                        Vector3 myPos = soldier.transform != null ? soldier.transform.position : Vector3.zero;
                        float pendingCoverDistSq =
                            (soughtCover &&
                             !soldier.IsOnCover() &&
                             soldier.HasDestinationAssigned &&
                             soldier.targetDestination != null &&
                             soldier.transform != null)
                                ? (soldier.targetDestination.GetCoverPosition() - myPos).sqrMagnitude
                                : 0f;

                        if (soughtCover && pendingCoverDistSq > 2.5f * 2.5f && pendingCoverDistSq <= 14.0f * 14.0f)
                        {
                            // Nearby cover (2.5m-14m) found: sprint/duck into cover without belly-crawling in the open!
                            ApplyDesiredMovingPose(__instance, soldier, pendingCoverDistSq, curSupp);
                        }
                        else
                        {
                            // Caught in open ground (> 14m from any cover or no cover available) while being shot at:
                            // STOP sprinting blindly toward a distant waypoint, drop to Crouch/Prone, and orient toward incoming fire!
                            AiCore.CachedDestKind dKind = sh != null ? AiCore.ResolveTargetDestination(sh) : AiCore.CachedDestKind.None;
                            bool onBridge = VisionConfig.EnableBridgeCrossingDiscipline.Value && AiCore.IsOnBridgeOrNarrowCrossing(myPos);
                            bool mustKeepRunning = soldier.IsCarryingBody() ||
                                                   soldier.isBleeding ||
                                                   dKind == AiCore.CachedDestKind.VehicleRepair ||
                                                   dKind == AiCore.CachedDestKind.MedicHeal ||
                                                   onBridge;

                            if (!mustKeepRunning && !soldier.IsOnCover() && __instance.IsMoving() && curSupp >= 14.0f)
                            {
                                float remToDestSq = (__instance.MoveDestination - myPos).sqrMagnitude;
                                if (remToDestSq > 10.5f * 10.5f && sh != null)
                                {
                                    float haltDur = 1.85f + ((AiCore.GetDeterministicHash(ptr, 223) & 15) * 0.06f);
                                    sh.Combat.FireAndMovement = new FireAndMovementState
                                    {
                                        InFiringHalt = true,
                                        PhaseStartTime = now,
                                        PhaseEndTime = now + haltDur,
                                        HaltLostSightTime = 0f,
                                        AnchorMoveDest = __instance.MoveDestination
                                    };
                                    if (AiCore.CanBotMoveAutonomously(soldier))
                                    {
                                        __instance.Stop(false);
                                    }
                                }
                            }

                            if (!__instance.IsMoving() || (!mustKeepRunning && !soldier.IsOnCover() && curSupp >= 22.0f && !onBridge))
                            {
                                bool canGoProne = (sh == null || now >= sh.Stance.NoProneUntil) &&
                                                  !soldier.IsOnCover() &&
                                                  !soldier.IsTowardWall() &&
                                                  !onBridge;

                                float proneThresh = GetEffectiveProneSuppressionThreshold(soldier, vet01);
                                SoldierPose targetPose = (curSupp >= proneThresh && canGoProne)
                                    ? SoldierPose.Prone
                                    : SoldierPose.Crouch;

                                if (soldier.m_pose < targetPose)
                                {
                                    SetBotPose(__instance, soldier, targetPose);
                                    SelfPreservationDrops++;
                                }

                                if (!__instance.IsMoving() &&
                                    __instance.visibleTarget == null &&
                                    TryGetKnownThreatSectorDirection(soldier, out Vector3 suppDir, out _) &&
                                    suppDir.sqrMagnitude > 0.01f &&
                                    AiCore.HorizontalAngleToDirection(soldier, suppDir) > 16.0f)
                                {
                                    AiCore.RotateBodyWithoutRaisingGun(soldier, suppDir, 0.12f);
                                }
                            }
                        }
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

                    // Physics catch-up spiral guard (FixedSteps: 2..5 in the same render frame):
                    // Reuse frame N's fire-permission & aim-point decision without re-running raycasts,
                    // bridge grid probes, bipod lookups, or veterancy queries 5x in one frame!
                    RotateAndFireFrameState prevFrameState = h.Combat.RotateFireFrame;
                    if (prevFrameState.Frame == curFrame && curFrame > 0)
                    {
                        if (prevFrameState.HasCustomTarget)
                            __0 = prevFrameState.CustomTargetPos;

                        if (!prevFrameState.BlockFire)
                            return true;

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
                        dist > 3.2f &&
                        __instance.visibleTarget != null &&
                        __instance.visibleTarget.Pointer != IntPtr.Zero)
                    {
                        IntPtr curTgtPtr = __instance.visibleTarget.Pointer;
                        InfantrySightReactionState rState = h.Combat.SightReaction;
                        if (curTgtPtr != rState.LastTargetPtr || rState.LastSeenTime <= 0f || (now - rState.LastSeenTime) > 2.2f)
                        {
                            float turnDeg = AiCore.HorizontalAngleToDirection(soldier, __0 - myPos);
                            bool isWatchingSector = !isMoving &&
                                                    turnDeg <= 24.0f &&
                                                    (soldier.IsOnCover() || soldier.UsingBipod() || now < soldier.pointGunEndTime);
                            float baseDelay;
                            if (isWatchingSector)
                            {
                                baseDelay = Mathf.Lerp(0.28f, 0.17f, vet01) + (dist > 95.0f ? 0.07f : 0f);
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

                            float curSuppForReact = soldier.GetSuppressionValue();
                            if (curSuppForReact >= 18.0f)
                            {
                                baseDelay += Mathf.Clamp01((curSuppForReact - 18.0f) / 60.0f) * 0.14f;
                            }

                            float jitter = (AiCore.GetDeterministicHash(ptr, curTgtPtr.GetHashCode()) & 31) * 0.0045f;
                            float totalDelay = Mathf.Clamp(baseDelay + jitter, 0.14f, 0.92f);
                            if (__instance.nextAvailableShotFires < now + totalDelay)
                            {
                                __instance.nextAvailableShotFires = now + totalDelay;
                                __instance.shotFiresEnd = 0f;
                                soldier.SetGunTriggerPressed(false);
                            }
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

                        if (dist <= ceaseFireRange)
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

                    if (!isMoving &&
                        !inPointBlankMelee &&
                        (VisionConfig.EnableSelfPreservationStance.Value || VisionConfig.NeverShootStandingInOpen.Value) &&
                        !isPeeking &&
                        !isHighBipodStanding &&
                        !soldier.IsOnVehicle() &&
                        !soldier.IsCharging() &&
                        !soldier.UsingBipod())
                    {
                        if (soldier.m_pose == SoldierPose.Idle && dist > 4.5f && dist < 220f)
                        {
                            float curSupp = soldier.GetSuppressionValue();
                            bool canCheckCrouchLof = curSupp >= 32f || now >= h.Stance.NextStandCrouchLofCheckTime;
                            if (canCheckCrouchLof)
                            {
                                h.Stance.NextStandCrouchLofCheckTime = now + 0.25f;
                                if (curSupp >= 32f || HasClearLineOfFireFromCrouch(soldier, __0, dist))
                                {
                                    bool canGoProne = now >= h.Stance.NoProneUntil &&
                                                      !soldier.IsOnCover() &&
                                                      !soldier.IsTowardWall();

                                    SoldierPose fightPose = SoldierPose.Crouch;
                                    float proneThresh = GetEffectiveProneSuppressionThreshold(soldier, vet01);
                                    if ((curSupp >= proneThresh || isGunner) &&
                                        canGoProne &&
                                        HasClearLineOfFireFromProne(soldier, __0, dist))
                                    {
                                        fightPose = SoldierPose.Prone;
                                    }

                                    SetBotPose(__instance, soldier, fightPose);

                                    if (now >= h.Stance.NextStanceDropAllowed)
                                    {
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
                        if (now >= h.Stance.NextWallCheckTime)
                        {
                            float angleToTarget = AiCore.HorizontalAngleToDirection(soldier, __0 - myPos);
                            if (angleToTarget <= 35f)
                            {
                                h.Stance.NextWallCheckTime = now + 0.25f;
                                if (IsMuzzleBlockedByWall(soldier, __0, dist))
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
                                        angleToTarget <= 25f)
                                    {
                                        bool crouchClear = HasClearLineOfFireFromCrouch(soldier, __0, dist);
                                        bool standClear = !crouchClear && HasClearLineOfFireFromStanding(soldier, __0, dist);
                                        bool isStaffingStaticGun = IsActivelyHeadingToEmplacement(soldier, now);
                                        // Allow short lateral jamb/embrasure sidestep even when snapped to cover once both crouch & standing shots are blocked!
                                        bool canSidestep = !soldier.IsCharging() && !isOnBridge && !isStaffingStaticGun;
                                        float peekDur = GetJitteredPeekDuration(ptr, 4.1f);

                                        if (soldier.m_pose == SoldierPose.Prone &&
                                            !HasClearLineOfFireFromProne(soldier, __0, dist))
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
                                                TrySidestepWallObstruction(__instance, soldier, __0, dist);
                                            }
                                        }
                                        else if (soldier.m_pose == SoldierPose.Crouch && !crouchClear)
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
                                                TrySidestepWallObstruction(__instance, soldier, __0, dist);
                                            }
                                        }
                                        else if (soldier.m_pose == SoldierPose.Idle && canSidestep)
                                        {
                                            TrySidestepWallObstruction(__instance, soldier, __0, dist);
                                        }
                                    }
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

                    if (VisionConfig.EnableAntiStacking.Value &&
                        !isMoving &&
                        !isOnBridge &&
                        soldier.m_pose != SoldierPose.Prone &&
                        !soldier.IsOnVehicle() &&
                        !soldier.IsOnCover() &&
                        !soldier.IsTowardWall() &&
                        !soldier.UsingBipod() &&
                        !soldier.isInBodyAnimation)
                    {
                        if (now >= h.Combat.NextAntiStackTime)
                        {
                            h.Combat.NextAntiStackTime = now + 2.5f;
                            if (TryGetAntiStackPush(soldier, out Vector3 pushDir))
                            {
                                Vector3 pushTarget = myPos + pushDir * 1.40f;
                                if (!AiCore.IsMovementBlockedByObstacle(myPos + Vector3.up * 0.48f, pushDir, 1.65f) &&
                                    Physics.Raycast(pushTarget + Vector3.up * 0.85f, Vector3.down, 2.2f, AiCore.GetSolidEnvironmentMask(), QueryTriggerInteraction.Ignore))
                                {
                                    __instance.MoveDirectlyToward(pushTarget, 0.80f);
                                    h.Combat.NextAntiStackTime = now + 6.0f;
                                    AntiStackNudges++;
                                }
                            }
                        }
                    }

                    h.Combat.RotateFireFrame = new RotateAndFireFrameState
                    {
                        Frame = curFrame,
                        BlockFire = blockFire,
                        WallBlocked = wallBlocked,
                        InPointBlankMelee = inPointBlankMelee,
                        IsMoving = isMoving,
                        IsPeeking = isPeeking,
                        HasCustomTarget = hasCustomTarget,
                        CustomTargetPos = customTargetPos
                    };

                    if (blockFire)
                    {
                        __instance.shotFiresEnd = 0f;
                        soldier.SetGunTriggerPressed(false);

                        if (wallBlocked && !isPeeking)
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

                    if (soldier.isTowardWallTps && !soldier.IsTowardWall())
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

                    // Pin AI Squad Leaders in Order.follow to their squad's active task destination instead of
                    // vanilla Soldier.GetBestMissionObjective(), so Squad Leaders never leave their squad behind.
                    if (soldier.IsSquadLeader() && (!soldier.HasDestinationAssigned || soldier.targetDestination == null))
                    {
                        AiCore.SoldierFastHandles h = AiCore.GetOrCreateSoldierHandles(soldier);
                        Squad sq = h != null ? AiCore.GetCachedSquad(h, now) : soldier.joinedSquad;
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

                    if (!soldier.HasDestinationAssigned || soldier.targetDestination == null)
                    {
                        return true;
                    }

                    bool protectedCover =
                        (VisionConfig.EnableTacticalGoapPlanner.Value && HasActiveGoapCoverLock(ptr, now)) ||
                        (covH != null && now < covH.Stance.LocalCoverProtectedUntil) ||
                        IsActivelyHeadingToEmplacement(soldier, now);

                    if (protectedCover)
                    {
                        __result = soldier.targetDestination.GetCoverPosition();
                        return false;
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

            public static void Postfix(CombatCover __instance, Vector3 __0, ref bool __result)
            {
                long profStart = HitchProfiler.Begin();
                try
                {
                    if (!__result || !VisionConfig.EnableMod.Value || __instance == null)
                        return;

                    bool wallSideEnabled = VisionConfig.EnableSmartWallSideCover.Value;
                    bool ffEnabled = VisionConfig.EnableFriendlyFireAvoidance.Value;
                    if (!wallSideEnabled && !ffEnabled)
                        return;

                    Vector3 threatDir = __0;
                    threatDir.y = 0f;
                    if (threatDir.sqrMagnitude < 0.001f)
                        return;

                    threatDir.Normalize();
                    IntPtr covPtr = __instance.Pointer;

                    // Zero-raycast reject if this cover spot sits directly inside a friendly human player's active sightline
                    if (ffEnabled && covPtr != IntPtr.Zero)
                    {
                        AiCore.EnsurePlayerAimLanesUpdated();
                        if (AiCore.ActivePlayerAimLaneCount > 0)
                        {
                            if (!_staticCoverPosCache.TryGetValue(covPtr, out Vector3 cachedCovPos))
                            {
                                cachedCovPos = __instance.GetCoverPosition();
                                if (!__instance.IsVehicle())
                                    _staticCoverPosCache[covPtr] = cachedCovPos;
                            }

                            if (AiCore.IsCoverInPlayerAimLane(cachedCovPos, threatDir))
                            {
                                __result = false;
                                return;
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

                    if (!_coverExposureCache.TryGetValue(cacheKey, out bool isExposed))
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
                        isExposed = IsPositionOnExposedSideOfWall(coverPos, threatDir, null, out _);
                        _coverExposureCache[cacheKey] = isExposed;
                    }

                    if (isExposed)
                    {
                        __result = false;
                    }
                }
                catch { }
                finally
                {
                    HitchProfiler.End(HitchBucket.CoverAvail, profStart);
                }
            }
        }

        [HarmonyPatch(typeof(Squad), nameof(Squad.SendUnitsToCovers))]
        public static class Squad_SendUnitsToCovers_Patch
        {
            public static void Prefix(Squad __instance, Vector3 __0, ref float __1, out long __state)
            {
                __state = HitchProfiler.Begin();
                try
                {
                    if (!VisionConfig.EnableMod.Value || __instance == null)
                        return;

                    if (__instance.Leader != null && !AiCore.IsLocallyOwnedBot(__instance.Leader))
                        return;

                    // Keep squad members INSIDE the actual MissionTask capture radius instead of scattering onto
                    // outer perimeter covers 5-15m outside the active cap circle!
                    if (VisionConfig.EnableMacroObjectiveDirector.Value &&
                        __instance.currentTask != null)
                    {
                        Vector3 dTask = __instance.currentTask.GetTaskDestination() - __0;
                        if (dTask.x * dTask.x + dTask.z * dTask.z <= 22.0f * 22.0f)
                        {
                            float taskRadius = Mathf.Max(10.0f, __instance.currentTask.GetTaskAreaRadius());
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
                catch { }
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
