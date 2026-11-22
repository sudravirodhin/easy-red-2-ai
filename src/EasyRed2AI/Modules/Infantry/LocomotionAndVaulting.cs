using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace EasyRed2HumanAI
{
    public static partial class VeteranAndTacticsPatches
    {
        // =====================================================================
        // PATCH 1B: SoldierAI.IsMoveTime (Committed Bounds vs. Firing Halts)
        // Replaces vanilla's 7.0s modulo duty cycle (0x180562080: 2.5s move / 4.5s stop),
        // which caused 1-step crouch-run-shoot stuttering, with hysteresis-locked bounds
        // (4.8s-6.8s upright dash <-> 3.7s-5.35s kneeling firing halt) and full commitment
        // into cover within <= 12m or under heavy open-ground suppression.
        // =====================================================================
        [HarmonyPatch(typeof(SoldierAI), nameof(SoldierAI.IsMoveTime), MethodType.Getter)]
        public static class SoldierAI_IsMoveTime_Patch
        {
            public static bool Prefix(SoldierAI __instance, ref bool __result)
            {
                long profStart = _inMovementRoutine ? 0L : HitchProfiler.BeginModScope();
                try
                {
                    if (!VisionConfig.EnableMod.Value || ReferenceEquals(__instance, null))
                        return true;

                    AiCore.SoldierFastHandles h = AiCore.GetOrCreateSoldierHandlesByAi(__instance);
                    if (!AiCore.IsLocallyOwnedBot(h) || h.Tr == null)
                        return true;

                    // Memoize per rendered frame: FixedUpdate + MovementRoutine.Prefix both call IsMoving() -> IsMoveTime,
                    // and FixedSteps: 5 catch-up frames can invoke FixedUpdate up to 5x on the exact same Time.frameCount!
                    int frame = Time.frameCount;
                    if (h.LastMoveTimeFrame == frame)
                    {
                        __result = h.CachedIsMoveTimeResult;
                        return false;
                    }

                    Soldier soldier = h.Ref;
                    IntPtr ptr = h.Ptr;
                    float now = Time.time;

                    bool hasPath = __instance.HasPath;
                    bool hasPathReq = __instance.HasPathRequest;
                    if (!hasPath && !hasPathReq)
                    {
                        h.Combat.FireAndMovement = default;
                        h.LastMoveTimeFrame = frame;
                        h.CachedIsMoveTimeResult = true;
                        __result = true;
                        return false;
                    }

                    if (__instance.moveDirectlyToward_end > now)
                    {
                        h.LastMoveTimeFrame = frame;
                        h.CachedIsMoveTimeResult = true;
                        __result = true;
                        return false;
                    }

                    Squad sq = AiCore.GetCachedSquad(h, now);
                    AiCore.CachedDestKind destKind = AiCore.ResolveTargetDestination(h);
                    Vector3 myPos = h.Tr.position;
                    Vector3 endpoint = GetCachedMoveEndpoint(h, __instance, soldier, out _);
                    float remDistSq = (endpoint - myPos).sqrMagnitude;
                    float supp = soldier.GetSuppressionValue();

                    bool hasPersonalCoverLock = now < h.Stance.LocalCoverProtectedUntil;
                    bool hasCloseEngageableEnemy = __instance.HasEnemyAndCanFireAtHim &&
                                                   __instance.distFromTarget >= 8.5f &&
                                                   __instance.distFromTarget <= 28.0f &&
                                                   !IsTargetUnengageableOnFoot(__instance, soldier, ptr, now);

                    bool mustFinishMoveNow =
                        (remDistSq <= 10.5f * 10.5f && (!hasCloseEngageableEnemy || (destKind == AiCore.CachedDestKind.CombatCover && remDistSq <= 6.5f * 6.5f))) ||
                        (!hasPersonalCoverLock && (soldier.IsCharging() || (!ReferenceEquals(sq, null) && sq.InChargeTime()))) ||
                        soldier.IsCarryingBody() ||
                        soldier.isBleeding ||
                        destKind == AiCore.CachedDestKind.VehicleRepair ||
                        destKind == AiCore.CachedDestKind.MedicHeal ||
                        IsActivelyHeadingToEmplacement(soldier, now) ||
                        IsInIndividualOrGoapFallback(ptr, now) ||
                        (__instance.HasEnemyAndCanFireAtHim && __instance.distFromTarget > 0.1f && __instance.distFromTarget <= 8.5f) ||
                        (supp >= 18f && !soldier.IsOnCover() && destKind == AiCore.CachedDestKind.CombatCover && remDistSq <= 9.5f * 9.5f) ||
                        (VisionConfig.EnableBridgeCrossingDiscipline.Value && !soldier.IsOnCover() && AiCore.IsOnBridgeOrNarrowCrossing(myPos)) ||
                        (h.MoveOverride.Kind == AiCore.BotMoveOverrideKind.VehicleEvade && now < h.MoveOverride.ExpireTime) ||
                        (h.MoveOverride.Kind == AiCore.BotMoveOverrideKind.GrenadeReflex && now < h.MoveOverride.ExpireTime);

                    if (mustFinishMoveNow)
                    {
                        if (h.Combat.FireAndMovement.InFiringHalt)
                        {
                            float dashDur = 4.8f + ((AiCore.GetDeterministicHash(ptr, 137) & 63) * (1.8f / 63f));
                            h.Combat.FireAndMovement.InFiringHalt = false;
                            h.Combat.FireAndMovement.PhaseStartTime = now;
                            h.Combat.FireAndMovement.PhaseEndTime = now + dashDur;
                            h.Combat.FireAndMovement.HaltLostSightTime = 0f;
                            h.Combat.FireAndMovement.AnchorMoveDest = __instance.MoveDestination;
                        }
                        h.LastMoveTimeFrame = frame;
                        h.CachedIsMoveTimeResult = true;
                        __result = true;
                        return false;
                    }

                    if (!VisionConfig.EnableBoundingOverwatch.Value)
                        return true;

                    Vector3 orderAnchor = __instance.MoveDestination;
                    int botHash = AiCore.GetDeterministicHash(ptr, 131);
                    FireAndMovementState st = h.Combat.FireAndMovement;
                    if (st.PhaseStartTime <= 0f ||
                        (st.AnchorMoveDest - orderAnchor).sqrMagnitude > 5.0f * 5.0f)
                    {
                        float initDash = 4.8f + ((botHash & 63) * (1.8f / 63f));
                        st = new FireAndMovementState
                        {
                            InFiringHalt = false,
                            PhaseStartTime = now,
                            PhaseEndTime = now + initDash,
                            HaltLostSightTime = 0f,
                            AnchorMoveDest = orderAnchor
                        };
                        h.Combat.FireAndMovement = st;
                        h.LastMoveTimeFrame = frame;
                        h.CachedIsMoveTimeResult = true;
                        __result = true;
                        return false;
                    }

                    if (st.InFiringHalt)
                    {
                        float dist = __instance.distFromTarget;
                        bool canStillEngage =
                            now < st.PhaseEndTime &&
                            __instance.HasEnemyAndCanFireAtHim &&
                            dist >= 8.5f &&
                            dist <= 165f &&
                            !IsTargetUnengageableOnFoot(__instance, soldier, ptr, now);

                        if (canStillEngage)
                        {
                            if (st.HaltLostSightTime > 0f)
                            {
                                st.HaltLostSightTime = 0f;
                                h.Combat.FireAndMovement = st;
                            }
                            h.LastMoveTimeFrame = frame;
                            h.CachedIsMoveTimeResult = false;
                            __result = false;
                            return false;
                        }

                        if (now < st.PhaseEndTime)
                        {
                            if (st.HaltLostSightTime <= 0f)
                            {
                                st.HaltLostSightTime = now;
                                h.Combat.FireAndMovement = st;
                            }

                            if ((now - st.PhaseStartTime) < 0.90f || (now - st.HaltLostSightTime) < 0.48f)
                            {
                                h.LastMoveTimeFrame = frame;
                                h.CachedIsMoveTimeResult = false;
                                __result = false;
                                return false;
                            }
                        }

                        float nextDash = 4.6f + (((botHash >> 6) & 63) * (1.6f / 63f));
                        st.InFiringHalt = false;
                        st.PhaseStartTime = now;
                        st.PhaseEndTime = now + nextDash;
                        st.HaltLostSightTime = 0f;
                        st.AnchorMoveDest = orderAnchor;
                        h.Combat.FireAndMovement = st;
                        h.LastMoveTimeFrame = frame;
                        h.CachedIsMoveTimeResult = true;
                        __result = true;
                        return false;
                    }

                    // Currently in Committed Dash (!st.InFiringHalt):
                    // Break open dash immediately when encountering a close enemy (<= 28m) or frontal contact (<= 45m within 42 deg)
                    // unless diving the final <= 7.5m into actual CombatCover!
                    if (now < st.PhaseEndTime)
                    {
                        bool breakDashForCloseContact = false;
                        if ((now - st.PhaseStartTime) >= 0.30f &&
                            !(destKind == AiCore.CachedDestKind.CombatCover && remDistSq <= 7.5f * 7.5f) &&
                            __instance.HasEnemyAndCanFireAtHim)
                        {
                            float cDist = __instance.distFromTarget;
                            if (cDist >= 8.5f && cDist <= 45.0f && !IsTargetUnengageableOnFoot(__instance, soldier, ptr, now))
                            {
                                if (cDist <= 28.0f)
                                {
                                    breakDashForCloseContact = true;
                                }
                                else if (AiCore.TryGetSpottableCenter(__instance.visibleTarget, out Vector3 visC) &&
                                         AiCore.HorizontalAngleToDirection(soldier, visC - myPos) <= 42.0f)
                                {
                                    breakDashForCloseContact = true;
                                }
                            }
                        }

                        if (!breakDashForCloseContact)
                        {
                            h.LastMoveTimeFrame = frame;
                            h.CachedIsMoveTimeResult = true;
                            __result = true;
                            return false;
                        }
                    }

                    float targetDist = __instance.distFromTarget;
                    bool hasCandidateTarget =
                        __instance.HasEnemyAndCanFireAtHim &&
                        targetDist >= 8.5f &&
                        targetDist <= 155f &&
                        !IsTargetUnengageableOnFoot(__instance, soldier, ptr, now);

                    if (!hasCandidateTarget)
                    {
                        // Stagger reaction windows during quiet marches so on sudden contact MGs + half of riflemen
                        // drop to a knee within 0.15-0.85s for base-of-fire while Assaulters + the other half keep bounding!
                        SoldierPersonality pers = GetPersonality(soldier);
                        bool isAssaulterBounder = pers == SoldierPersonality.Assaulter_Shock ||
                                                  pers == SoldierPersonality.Assaulter_Breacher ||
                                                  pers == SoldierPersonality.Assaulter_Skirmisher;
                        bool isBaseOfFireElement = soldier.hasMg ||
                                                   pers == SoldierPersonality.Gunner_Anchor ||
                                                   pers == SoldierPersonality.Gunner_Overwatch ||
                                                   (!isAssaulterBounder && ((botHash & 1) == 0));
                        float rollWindow = isBaseOfFireElement
                            ? (0.15f + (((botHash >> 8) & 31) * (0.70f / 31f)))
                            : (2.80f + (((botHash >> 8) & 31) * (2.00f / 31f)));

                        st.PhaseEndTime = now + rollWindow;
                        st.AnchorMoveDest = orderAnchor;
                        h.Combat.FireAndMovement = st;
                        h.LastMoveTimeFrame = frame;
                        h.CachedIsMoveTimeResult = true;
                        __result = true;
                        return false;
                    }

                    // Evaluate clear kneeling LOF strictly ONCE at the Dash -> Halt phase boundary (zero per-frame raycasts)
                    bool clearToKneelAndFire = false;
                    if (AiCore.TryGetSpottableCenter(__instance.visibleTarget, out Vector3 tPos))
                    {
                        clearToKneelAndFire = HasClearLineOfFireFromCrouch(soldier, tPos, targetDist) ||
                                              (!VisionConfig.NeverShootStandingInOpen.Value && HasClearLineOfFireFromStanding(soldier, tPos, targetDist));
                    }

                    if (!clearToKneelAndFire)
                    {
                        // Reverse slope / berm blocks crouched shot here: keep sprinting another ~2.4s (~9-10m) toward crest/cover!
                        st.PhaseEndTime = now + 2.40f;
                        st.AnchorMoveDest = orderAnchor;
                        h.Combat.FireAndMovement = st;
                        h.LastMoveTimeFrame = frame;
                        h.CachedIsMoveTimeResult = true;
                        __result = true;
                        return false;
                    }

                    GenericGun heldGun = soldier.GetHeldGun();
                    bool isAutoOrMg = soldier.hasMg || (heldGun != null && !heldGun.bolt_required);
                    float baseHalt = isAutoOrMg ? 4.40f : 3.70f;
                    float haltJitter = ((botHash >> 13) & 63) * (0.95f / 63f);

                    st.InFiringHalt = true;
                    st.PhaseStartTime = now;
                    st.PhaseEndTime = now + baseHalt + haltJitter;
                    st.HaltLostSightTime = 0f;
                    st.AnchorMoveDest = orderAnchor;
                    h.Combat.FireAndMovement = st;

                    if (now >= h.Combat.NextBoundingHoldCountTime)
                    {
                        h.Combat.NextBoundingHoldCountTime = now + 5.0f;
                        BoundingOverwatchHoldsCount++;
                        if (VisionConfig.DebugLogging.Value)
                        {
                            AiCore.LogDebug("BoundingOverwatch", $"{soldier.faction} soldier entered {(baseHalt + haltJitter):F1}s committed firing halt ({targetDist:F0}m).");
                        }
                    }

                    h.LastMoveTimeFrame = frame;
                    h.CachedIsMoveTimeResult = false;
                    __result = false;
                    return false;
                }
                catch (Exception ex)
                {
                    AiCore.ReportPatchException("IsMoveTime", ex);
                }
                finally
                {
                    if (profStart != 0L)
                        HitchProfiler.EndModScope(HitchBucket.MovementRoutine, profStart);
                }
                return true;
            }
        }

        // =====================================================================
        // PATCH 4: SoldierAI.UpdateMoveDirection (Wall, Trench-Exit & Stuck-Wire Vaulting)
        // =====================================================================
        internal struct BarbedWireStuckState
        {
            public Vector3 AnchorPos;
            public float FirstStuckTime;
            public float LastSeenTime;
        }

        internal static readonly Dictionary<IntPtr, bool> _barbedWireColliderCache = new Dictionary<IntPtr, bool>(256);

        private static bool IsBarbedWireName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return false;

            return name.IndexOf("wire", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("barbed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("concertina", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("chevaux", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("razor", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsBarbedWireCollider(Collider col)
        {
            if (col == null || col.Pointer == IntPtr.Zero)
                return false;

            IntPtr colPtr = col.Pointer;
            if (_barbedWireColliderCache.TryGetValue(colPtr, out bool cached))
                return cached;

            bool isWire = IsBarbedWireName(col.name);
            if (!isWire && col.transform != null)
            {
                Transform p1 = col.transform.parent;
                if (p1 != null)
                {
                    isWire = IsBarbedWireName(p1.name);
                    if (!isWire && p1.parent != null)
                        isWire = IsBarbedWireName(p1.parent.name);
                }
            }

            if (_barbedWireColliderCache.Count > 512)
                _barbedWireColliderCache.Clear();
            _barbedWireColliderCache[colPtr] = isWire;
            return isWire;
        }

        private static bool IsValidVaultObstacle(
            SoldierAI ai,
            Soldier soldier,
            IntPtr botPtr,
            Transform tr,
            ref Vector3 vaultPos,
            float now,
            out string vaultOrRejectReason)
        {
            vaultOrRejectReason = "wall_obstacle";
            Vector3 myPos = tr.position;

            Vector3 fwd = tr.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.001f)
            {
                vaultOrRejectReason = "bad_forward";
                return false;
            }
            fwd.Normalize();

            // 1. Check current path-segment move vector (ai.dir) OR final MoveDestination so multi-node paths
            //    that turn a corner 50-150m later are never falsely rejected as "not_moving_across"!
            Vector3 curMoveDir = ai.dir;
            curMoveDir.y = 0f;
            bool movingForwardAlongSegment = curMoveDir.sqrMagnitude > 0.04f && Vector3.Dot(fwd, curMoveDir.normalized) >= 0.30f;
            if (!movingForwardAlongSegment)
            {
                Vector3 toDest = ai.MoveDestination - myPos;
                toDest.y = 0f;
                if (toDest.sqrMagnitude > 0.36f && Vector3.Dot(fwd, toDest.normalized) < 0.30f)
                {
                    vaultOrRejectReason = "not_moving_across";
                    return false;
                }
            }

            // 2. Front-face probe using ResourcesManager.vaultMask | groundMask (Default | Terrain | InvisibleTrepassableWall):
            //    Matches native Soldier.CanVault (0x180742C40), which detects simplified InvisibleTrepassableWall collision
            //    boxes on sandbags, fences, parapets, and window sills via a r=0.30f CheckSphere at +0.50m.
            int vaultMask = AiCore.GetMovementObstacleMask();
            int groundMask = AiCore.GetSolidEnvironmentMask();
            RaycastHit frontHit;
            Vector3 sphereOrigin = myPos + Vector3.up * 0.50f - fwd * 0.22f;
            bool foundFace = Physics.SphereCast(sphereOrigin, 0.22f, fwd, out frontHit, 1.15f, vaultMask, QueryTriggerInteraction.Ignore);
            if (!foundFace || frontHit.distance <= 0.001f)
            {
                if (!Physics.Raycast(myPos + Vector3.up * 0.38f - fwd * 0.15f, fwd, out frontHit, 1.30f, vaultMask, QueryTriggerInteraction.Ignore) &&
                    !Physics.Raycast(myPos + Vector3.up * 0.78f - fwd * 0.15f, fwd, out frontHit, 1.30f, vaultMask, QueryTriggerInteraction.Ignore))
                {
                    vaultOrRejectReason = "no_solid_face";
                    return false;
                }
            }

            // 2B. Validate & repair vanilla CanVault's vaultPos (allow up to 6.2m drop when trapped inside an elevated
            //     building shell with missing stairs, and wider trench-parapet slope tolerances when stuck in a trench):
            AiCore.SoldierFastHandles vaultH = AiCore.GetOrCreateSoldierHandles(soldier);
            float trapAge = (vaultH != null && vaultH.Goap.FootStuck.RoomTrapSince > 0f && now >= vaultH.Goap.FootStuck.RoomTrapSince)
                ? (now - vaultH.Goap.FootStuck.RoomTrapSince)
                : 0f;
            bool isRoomOrTrenchTrapped = trapAge >= 8.0f;
            bool stuckEscalated = isRoomOrTrenchTrapped || (vaultH != null && vaultH.Goap.FootStuck.ConsecutiveUnsticks >= 2);
            float maxVaultDropY = (isRoomOrTrenchTrapped && !soldier.isBleeding) ? 6.2f : 1.68f;
            bool hasVaultWater = AiCore.TryGetActiveWaterLevel(out float vaultWaterY);

            Vector2 landingHoriz = new Vector2(vaultPos.x - myPos.x, vaultPos.z - myPos.z);
            float initRise = vaultPos.y - myPos.y;
            float initDrop = myPos.y - vaultPos.y;
            bool landingValid = vaultPos.sqrMagnitude > 0.04f &&
                                landingHoriz.sqrMagnitude >= 0.16f &&
                                landingHoriz.sqrMagnitude <= 2.50f * 2.50f &&
                                initRise <= 1.68f &&
                                initDrop <= maxVaultDropY &&
                                (!hasVaultWater || vaultPos.y > vaultWaterY + 0.15f) &&
                                !Physics.CheckSphere(vaultPos + Vector3.up * 0.95f, 0.20f, vaultMask | AiCore.GetSolidEnvironmentMask(), QueryTriggerInteraction.Ignore);
            if (!landingValid)
            {
                Vector3 landProbeStart = myPos + fwd * Mathf.Clamp(frontHit.distance + 0.68f, 0.95f, 1.55f) + Vector3.up * 1.68f;
                float rayDist = maxVaultDropY > 2.0f ? (1.68f + maxVaultDropY + 0.60f) : 3.20f;
                if (!Physics.Raycast(landProbeStart, Vector3.down, out RaycastHit landHit, rayDist, groundMask, QueryTriggerInteraction.Ignore) ||
                    landHit.normal.y < 0.62f ||
                    landHit.collider == null ||
                    landHit.collider.gameObject.layer == 4 ||
                    (hasVaultWater && landHit.point.y <= vaultWaterY + 0.15f) ||
                    AiCore.IsVehicleCollider(landHit.collider) ||
                    (landHit.point.y - myPos.y) > 1.68f ||
                    (myPos.y - landHit.point.y) > maxVaultDropY ||
                    Physics.CheckSphere(landHit.point + Vector3.up * 0.95f, 0.20f, vaultMask, QueryTriggerInteraction.Ignore))
                {
                    vaultOrRejectReason = AiCore.IsVehicleCollider(landHit.collider) ? "vehicle_hull" : "no_landing";
                    return false;
                }
                vaultPos = landHit.point;
            }

            Collider frontCol = frontHit.collider;
            if (AiCore.IsVehicleCollider(frontCol))
            {
                vaultOrRejectReason = "vehicle_hull";
                return false;
            }
            bool frontIsTerrain = AiCore.IsTerrainCollider(frontCol);
            float vaultLandingRise = vaultPos.y - myPos.y;

            if (frontIsTerrain)
            {
                // Carved terrain trench walls are steep unwalkable cuts (>= ~52 deg normally, widened to ~40 deg when
                // stuck in a trench) with a genuine step-up onto the parapet lip; ordinary hills/bumps are rejected.
                float maxTerrainNormalY = stuckEscalated ? 0.76f : 0.62f;
                if (Mathf.Abs(frontHit.normal.y) > maxTerrainNormalY || vaultLandingRise < 0.45f)
                {
                    vaultOrRejectReason = "terrain_slope";
                    return false;
                }
            }
            else
            {
                float maxWallNormalY = vaultLandingRise >= 0.45f
                    ? (stuckEscalated ? 0.78f : 0.68f)
                    : 0.60f;
                if (Mathf.Abs(frontHit.normal.y) > maxWallNormalY)
                {
                    // Allow vertical walls AND battered wooden/earth trench revetment walls when stepping up onto a parapet
                    // lip, while rejecting walkable <= 45 deg sloped ramps.
                    vaultOrRejectReason = "sloped_surface";
                    return false;
                }
            }

            // 3. Reject full-height walls (> 1.68m) and measure true obstacle crest height at BOTH +3cm (thin 4-12cm wooden
            //    fences / stone walls where +16cm previously overshot onto the far-side floor!) AND +20cm (thicker parapets).
            Vector3 intoWall = new Vector3(-frontHit.normal.x, 0f, -frontHit.normal.z);
            intoWall = intoWall.sqrMagnitude > 0.01f ? intoWall.normalized : fwd;

            int wallProbeMask = vaultMask | AiCore.GetSolidEnvironmentMask();
            if (Physics.CheckSphere(
                    new Vector3(frontHit.point.x + intoWall.x * 0.06f, myPos.y + 1.76f, frontHit.point.z + intoWall.z * 0.06f),
                    0.12f,
                    wallProbeMask,
                    QueryTriggerInteraction.Ignore) ||
                Physics.CheckSphere(
                    new Vector3(frontHit.point.x + intoWall.x * 0.24f, myPos.y + 1.76f, frontHit.point.z + intoWall.z * 0.24f),
                    0.12f,
                    wallProbeMask,
                    QueryTriggerInteraction.Ignore))
            {
                vaultOrRejectReason = "too_high";
                return false;
            }

            float crestHeight = vaultLandingRise;
            Collider crestCol = null;
            for (int pIdx = 0; pIdx < 2; pIdx++)
            {
                float inset = pIdx == 0 ? 0.03f : 0.20f;
                Vector3 crestProbeOrigin = new Vector3(
                    frontHit.point.x + intoWall.x * inset,
                    myPos.y + 1.74f,
                    frontHit.point.z + intoWall.z * inset);
                if (Physics.Raycast(crestProbeOrigin, Vector3.down, out RaycastHit crestHit, 1.50f, vaultMask, QueryTriggerInteraction.Ignore))
                {
                    float probedCrest = crestHit.point.y - myPos.y;
                    if (probedCrest > crestHeight)
                    {
                        crestHeight = probedCrest;
                        crestCol = crestHit.collider;
                    }
                }
            }

            // 3B. Vehicle Hull & Barbed Wire Discipline:
            // Never vault onto tank hulls, truck beds, or vehicle fenders; keep barbed wire unvaultable
            // during normal movement so wire lines act as real tactical obstacles until wedged >= 3.5s.
            if (AiCore.IsVehicleCollider(crestCol))
            {
                vaultOrRejectReason = "vehicle_hull";
                return false;
            }
            if (IsBarbedWireCollider(frontCol) || IsBarbedWireCollider(crestCol))
            {
                BarbedWireStuckState wireSt = vaultH != null ? vaultH.Combat.BarbedWireStuck : default;
                if (wireSt.LastSeenTime <= 0f ||
                    now - wireSt.LastSeenTime > 2.2f ||
                    (myPos - wireSt.AnchorPos).sqrMagnitude > 1.35f)
                {
                    if (vaultH != null)
                    {
                        vaultH.Combat.BarbedWireStuck = new BarbedWireStuckState
                        {
                            AnchorPos = myPos,
                            FirstStuckTime = now,
                            LastSeenTime = now
                        };
                    }
                    vaultOrRejectReason = "barbed_wire";
                    return false;
                }

                wireSt.LastSeenTime = now;
                vaultH.Combat.BarbedWireStuck = wireSt;
                if (now - wireSt.FirstStuckTime < 3.5f)
                {
                    vaultOrRejectReason = "barbed_wire";
                    return false;
                }

                vaultH.Combat.BarbedWireStuck = default;
                vaultOrRejectReason = "barbed_wire_unstuck";
            }

            if (crestHeight < 0.38f)
            {
                vaultOrRejectReason = "flat_or_low";
                return false;
            }
            if (crestHeight > 1.68f || vaultLandingRise > 1.68f)
            {
                vaultOrRejectReason = "too_high";
                return false;
            }

            if (!HasClearVaultArcAndLanding(myPos, vaultPos, vaultMask, crestHeight, out vaultOrRejectReason, maxVaultDropY))
                return false;

            // 4. Far-side grade & trench-lip level-off check:
            // Reject staircases or continuous hills/bluffs that keep rising beyond the lip, while allowing
            // genuine trench exits where the ground levels off onto the parapet / battlefield above!
            Vector3 farOrigin = myPos + fwd * (frontHit.distance + 0.88f) + Vector3.up * (crestHeight + 0.65f);
            if (Physics.Raycast(farOrigin, Vector3.down, out RaycastHit farHit, crestHeight + 1.85f, groundMask, QueryTriggerInteraction.Ignore))
            {
                if (AiCore.IsVehicleCollider(farHit.collider))
                {
                    vaultOrRejectReason = "vehicle_hull";
                    return false;
                }
                if (farHit.point.y > myPos.y + crestHeight + 0.18f)
                {
                    vaultOrRejectReason = "uphill_ramp";
                    return false;
                }

                bool farIsElevatedTerrain = AiCore.IsTerrainCollider(farHit.collider) &&
                                            farHit.point.y > myPos.y + 0.35f;
                bool isBatteredTrenchRevetment = !frontIsTerrain && Mathf.Abs(frontHit.normal.y) > 0.58f;
                if (frontIsTerrain || farIsElevatedTerrain || isBatteredTrenchRevetment)
                {
                    if (farHit.normal.y < 0.80f)
                    {
                        vaultOrRejectReason = "terrain_hillside";
                        return false;
                    }

                    Vector3 beyondOrigin = myPos + fwd * (frontHit.distance + 1.48f) + Vector3.up * (crestHeight + 0.85f);
                    if (Physics.Raycast(beyondOrigin, Vector3.down, out RaycastHit beyondHit, crestHeight + 2.05f, groundMask, QueryTriggerInteraction.Ignore) &&
                        beyondHit.point.y > farHit.point.y + 0.24f)
                    {
                        vaultOrRejectReason = "terrain_hillside";
                        return false;
                    }

                    if (vaultOrRejectReason == "wall_obstacle")
                        vaultOrRejectReason = "trench_exit";
                }
            }

            return true;
        }

        /// <summary>
        /// Allows grounded dry foot bots on a trench rim to step directly off the side into a trench
        /// (<c>0.65m..2.65m</c> drop onto a walkable trench floor) when native <c>CPathFinding</c>
        /// (which symmetrically rejects <c>|slope| &gt;= 58°</c> in both directions) either fails or routes
        /// them <c>20m–40m</c> around the exposed parapet to a trench entrance.
        /// </summary>
        private static bool TryStepOffIntoTrench(SoldierAI ai, Soldier soldier, AiCore.SoldierFastHandles h, float now)
        {
            if (ai.moveDirectlyToward_end > now || h.Tr == null)
                return false;

            Vector3 myPos = h.Tr.position;
            if (AiCore.IsOnBridgeOrNarrowCrossing(myPos))
                return false;

            if (AiCore.TryGetActiveWaterLevel(out float waterY) && myPos.y <= waterY + 0.35f)
                return false;

            Vector3 dest = ai.MoveDestination;
            Vector3 toDestFlat = new Vector3(dest.x - myPos.x, 0f, dest.z - myPos.z);
            float flatDistSq = toDestFlat.sqrMagnitude;
            if (flatDistSq < 0.49f)
                return false;

            float flatDist = Mathf.Sqrt(flatDistSq);
            Vector3 toDestDir = toDestFlat / flatDist;
            Vector3 curDirFlat = new Vector3(ai.dir.x, 0f, ai.dir.z);
            float dirAlignment = curDirFlat.sqrMagnitude > 0.01f
                ? Vector3.Dot(curDirFlat.normalized, toDestDir)
                : -1f;

            float destDropY = myPos.y - dest.y;
            bool destIsInsideTrench = flatDist <= 7.5f && destDropY >= 0.65f && destDropY <= 2.65f;
            bool pathDetouringOrFailed =
                ai.pathCreationFails > 0 ||
                (destIsInsideTrench ? dirAlignment < 0.60f : dirAlignment < 0.25f);

            if (!pathDetouringOrFailed)
                return false;

            float stepDist = Mathf.Clamp(flatDist, 1.35f, 2.35f);
            Vector3 rawStepTarget = new Vector3(
                myPos.x + toDestDir.x * stepDist,
                myPos.y - 1.35f,
                myPos.z + toDestDir.z * stepDist);

            if (!AiCore.TryValidateAndSnapDirectMicroStep(
                    myPos,
                    rawStepTarget,
                    out Vector3 snappedDropPos,
                    out _,
                    maxFlatDist: 2.8f,
                    maxStepDeltaY: 0.45f,
                    maxStepDownDeltaY: 2.65f))
            {
                return false;
            }

            float actualDrop = myPos.y - snappedDropPos.y;
            if (actualDrop < 0.65f || actualDrop > 2.65f)
                return false;

            if (destIsInsideTrench)
            {
                // Destination itself is at trench depth: ensure the step-off lands near the destination's floor level.
                if (Mathf.Abs(snappedDropPos.y - dest.y) > 1.10f)
                    return false;
            }
            else
            {
                // Crossing a trench toward a high-ground destination: only drop in if the trench is shallow enough
                // (<= 1.58m) to vault out the far side, and verify the floor doesn't keep plunging down a cliff/ravine.
                if (actualDrop > 1.58f || dest.y < snappedDropPos.y - 0.50f)
                    return false;

                int groundMask = AiCore.GetSolidEnvironmentMask();
                Vector3 aheadProbe = new Vector3(
                    snappedDropPos.x + toDestDir.x * 1.15f,
                    snappedDropPos.y + 1.20f,
                    snappedDropPos.z + toDestDir.z * 1.15f);
                if (!Physics.Raycast(aheadProbe, Vector3.down, out RaycastHit aheadHit, 2.00f, groundMask, QueryTriggerInteraction.Ignore) ||
                    aheadHit.point.y < snappedDropPos.y - 0.55f)
                {
                    return false;
                }
            }

            float stepDuration = Mathf.Clamp(stepDist / 2.2f, 0.55f, 1.10f);
            ai.MoveDirectlyToward(snappedDropPos, stepDuration);
            h.Combat.NextVaultCheckTime = now + stepDuration + 0.35f;
            return true;
        }

        [HarmonyPatch(typeof(SoldierAI), nameof(SoldierAI.UpdateMoveDirection))]
        public static class SoldierAI_UpdateMoveDirection_Patch
        {
            public static void Postfix(SoldierAI __instance)
            {
                long profStart = HitchProfiler.BeginModScope();
                try
                {
                    if (!VisionConfig.EnableMod.Value || !VisionConfig.EnableWallVaulting.Value || ReferenceEquals(__instance, null))
                        return;

                    AiCore.SoldierFastHandles h = AiCore.GetOrCreateSoldierHandlesByAi(__instance);
                    if (h == null)
                        return;

                    IntPtr ptr = h.Ptr;
                    float now = Time.time;
                    if (!AiCore.IsLocallyOwnedBot(h) || AiCore.IsSoldierInVehicleOrTransition(h.Ref))
                    {
                        if (now >= h.Combat.NextVaultCheckTime)
                            h.Combat.NextVaultCheckTime = now + 1.50f;
                        return;
                    }

                    Soldier soldier = h.Ref;
                    TryPopVerticalDeadzonePathNode(__instance, h, soldier);

                    if (now < h.Combat.NextVaultCheckTime)
                        return;

                    h.Combat.NextVaultCheckTime = now + 0.50f;

                    if (!__instance.IsMoving() ||
                        soldier.m_pose == SoldierPose.Prone ||
                        soldier.isInBodyAnimation ||
                        soldier.IsJumping() ||
                        soldier.IsCarryingBody() ||
                        soldier.IsCarried() ||
                        soldier.HasSurrended())
                    {
                        return;
                    }

                    if (soldier.CanVault(out Vector3 vaultPos))
                    {
                        if (!IsValidVaultObstacle(__instance, soldier, ptr, h.Tr, ref vaultPos, now, out string vaultOrRejectReason))
                        {
                            SlopedVaultsRejectedCount++;
                            if (vaultOrRejectReason == "sloped_surface" ||
                                vaultOrRejectReason == "terrain_slope" ||
                                vaultOrRejectReason == "uphill_ramp" ||
                                vaultOrRejectReason == "no_solid_face")
                            {
                                byte streak = h.Combat.ConsecutiveSlopeVaultRejects;
                                if (streak < 2)
                                    h.Combat.ConsecutiveSlopeVaultRejects = (byte)(streak + 1);
                                float cd = streak == 0 ? 0.85f : (streak == 1 ? 1.50f : 2.35f);
                                h.Combat.NextVaultCheckTime = now + cd;
                            }
                            else
                            {
                                h.Combat.ConsecutiveSlopeVaultRejects = 0;
                                h.Combat.NextVaultCheckTime = now + (vaultOrRejectReason == "barbed_wire" ? 0.55f : 0.85f);
                            }
                            BotActionTrace.RecordEvent(soldier, BotTraceEventKind.VaultRejected, vaultOrRejectReason);
                            return;
                        }

                        h.Combat.ConsecutiveSlopeVaultRejects = 0;
                        if (AiCore.TryConsumeBotRpcBudget(0.35f))
                        {
                            soldier.Vault(vaultPos);
                            h.Combat.NextVaultCheckTime = now + 2.20f;
                            WallsVaulted++;
                            BotActionTrace.RecordEvent(soldier, BotTraceEventKind.Vault, vaultOrRejectReason);
                        }
                        return;
                    }

                    h.Combat.ConsecutiveSlopeVaultRejects = 0;
                    TryStepOffIntoTrench(__instance, soldier, h, now);
                }
                catch (Exception ex)
                {
                    AiCore.ReportPatchException("UpdateMoveDir", ex);
                }
                finally
                {
                    HitchProfiler.EndModScope(HitchBucket.UpdateMoveDir, profStart);
                }
            }

            // Native SoldierAI.nodeReachedDist (0.30m) is 3D Euclidean; when terrain/rubble/trenches cause
            // a >=0.22m..1.40m height difference at a node, pop it horizontally so bots do not orbit.
            private static void TryPopVerticalDeadzonePathNode(SoldierAI ai, AiCore.SoldierFastHandles h, Soldier soldier)
            {
                if (h.Tr == null ||
                    !ai.IsMoving() ||
                    soldier.isInBodyAnimation ||
                    soldier.IsCarryingBody() ||
                    soldier.IsCarried() ||
                    soldier.HasSurrended())
                {
                    return;
                }

                A.d path = ai.path;
                if (ReferenceEquals(path, null) || path.Pointer == IntPtr.Zero || path.NodesCount <= 0)
                    return;

                A.g curNode = path.CurrentNode;
                if (curNode == null)
                    return;

                Vector3 myPos = h.Tr.position;
                Vector3 nodePos = curNode.point;
                float dx = nodePos.x - myPos.x;
                float dz = nodePos.z - myPos.z;
                if (dx * dx + dz * dz > 0.48f * 0.48f)
                    return;

                float absDy = Mathf.Abs(nodePos.y - myPos.y);
                if (absDy < 0.22f || absDy > 1.40f)
                    return;

                path.A(ai.Cast<AIUsingPath>());
                A.d postPath = ai.path;
                if (!ReferenceEquals(postPath, null) && postPath.Pointer != IntPtr.Zero && postPath.NodesCount > 0)
                {
                    A.g nextNode = postPath.CurrentNode;
                    if (nextNode != null)
                    {
                        Vector3 nextPt = nextNode.point;
                        ai.dir = nextPt - myPos;
                        soldier.CheckInteractionInWalkRange(myPos, nextPt);
                    }
                }
                else
                {
                    ai.Stop(true);
                    if (ai.CloseToDest(myPos, 1.5f))
                    {
                        soldier.TryToGetToActualCover();
                    }
                }
            }
        }

        // =====================================================================
        // PATCH 4B: SoldierAI.GetSpacingFromSquad (Bridge Discipline & Player Crosshair Clearance)
        // =====================================================================
        [HarmonyPatch(typeof(SoldierAI), nameof(SoldierAI.GetSpacingFromSquad))]
        public static class SoldierAI_GetSpacingFromSquad_Patch
        {
            public static void Postfix(SoldierAI __instance, ref Vector3 __result)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value || __instance == null)
                        return;

                    AiCore.SoldierFastHandles h = AiCore.GetOrCreateSoldierHandlesByAi(__instance);
                    if (!AiCore.IsLocallyOwnedBot(h) || h.Tr == null || h.Ref.IsOnVehicle())
                        return;

                    float now = Time.time;
                    if (TryGetProtectedBotMoveOverride(h.Ptr, now, out Vector3 moveOverride))
                    {
                        __result = moveOverride;
                        return;
                    }

                    bool bridgeEnabled = VisionConfig.EnableBridgeCrossingDiscipline.Value;
                    bool ffEnabled = VisionConfig.EnableFriendlyFireAvoidance.Value;
                    bool vehRowEnabled = VisionConfig.EnableFriendlyVehicleRightOfWay.Value;
                    if (!bridgeEnabled && !ffEnabled && !vehRowEnabled)
                        return;

                    Soldier soldier = h.Ref;
                    Vector3 myPos = h.Tr.position;
                    Vector3 moveDest = __instance.MoveDestination;
                    bool onBridgeSpan = false;

                    if (bridgeEnabled && (__result - moveDest).sqrMagnitude > 1.0f)
                    {
                        // Cancel vanilla's 5m-35m sideways waypoint scatter when on, targeting, or approaching a bridge,
                        // OR when sideways scatter flings the waypoint into water or down an embankment/ravine below the roadbed.
                        bool isBridge =
                            AiCore.IsApproachingOrOnBridge(__instance, h, myPos, moveDest) ||
                            AiCore.IsOnBridgeOrNarrowCrossing(__result);

                        bool dropOffEmbankment = false;
                        if (!isBridge)
                        {
                            bool inWater = AiCore.TryGetActiveWaterLevel(out float scatterWaterY) && __result.y <= scatterWaterY + 0.15f;
                            bool steepRavineDrop = __result.y < myPos.y - 2.6f && moveDest.y >= myPos.y - 1.5f;
                            dropOffEmbankment = inWater || steepRavineDrop;
                        }

                        if (isBridge || dropOffEmbankment)
                        {
                            __result = moveDest;
                            onBridgeSpan = true;
                            if (now >= h.Combat.NextBridgeAssistCountTime)
                            {
                                h.Combat.NextBridgeAssistCountTime = now + 8.0f;
                                BridgeCrossingsAssistedCount++;
                                if (VisionConfig.DebugLogging.Value)
                                {
                                    AiCore.LogDebug(
                                        "BridgeDiscipline",
                                        dropOffEmbankment
                                            ? $"{soldier.faction} soldier prevented ravine/water scatter drop-off (kept on bridge approach centerline)."
                                            : $"{soldier.faction} soldier kept on bridge/causeway centerline (cancelled 5-35m lateral squad scatter).");
                                }
                            }
                        }
                    }

                    // Keep squad-follower formation slots away from known armed mines
                    if (!onBridgeSpan &&
                        AiCore.TryGetNearbyKnownMine(__result, 4.5f, false, out Vector3 minePos) &&
                        (moveDest - minePos).sqrMagnitude > 4.5f * 4.5f)
                    {
                        Vector3 away = __result - minePos;
                        away.y = 0f;
                        Vector3 awayDir = away.sqrMagnitude > 0.04f ? away.normalized : Vector3.right;
                        Vector3 mineAvoid = minePos + awayDir * 5.0f;
                        if (Physics.Raycast(
                                mineAvoid + Vector3.up * 6.0f,
                                Vector3.down,
                                out RaycastHit avoidGround,
                                14.0f,
                                AiCore.GetSolidEnvironmentMask(),
                                QueryTriggerInteraction.Ignore) &&
                            avoidGround.normal.y >= 0.58f &&
                            (!AiCore.TryGetActiveWaterLevel(out float avoidWaterY) || avoidGround.point.y > avoidWaterY - 0.15f))
                        {
                            __result = avoidGround.point;
                        }
                        else
                        {
                            __result = moveDest;
                        }
                    }

                    // Keep squad-follower formation slots out of friendly human players' aiming lanes!
                    if (ffEnabled &&
                        AiCore.TryGetBlockingFriendlyPlayerAimLane(
                            h.Ptr,
                            AiCore.GetCachedFaction(h, now),
                            __result,
                            0.25f,
                            out AiCore.PlayerAimLane lane,
                            out float fwdDist,
                            out _,
                            out Vector3 clearStepDir))
                    {
                        Vector3 deflected = fwdDist <= 4.5f
                            ? (lane.FeetPos - lane.AimDirFlat * 1.75f + clearStepDir * 1.15f)
                            : (__result + clearStepDir * 2.15f);

                        if (!onBridgeSpan || AiCore.IsSafeBridgeDeckStep(myPos, deflected, out deflected))
                        {
                            __result = deflected;
                        }
                    }

                    // Keep squad-follower formation slots out of moving friendly vehicles' forward danger corridors!
                    if (vehRowEnabled &&
                        AiCore.TryGetBlockingFriendlyVehicleDangerCorridor(
                            __result,
                            AiCore.GetCachedFaction(h, now),
                            0.35f,
                            out AiCore.VehicleDangerCorridor vCorr,
                            out _,
                            out float vSignedLat,
                            out Vector3 vClearDir))
                    {
                        float pushDist = Mathf.Max(2.2f, (vCorr.HalfWidth + 2.55f) - Mathf.Abs(vSignedLat));
                        Vector3 deflectedVeh = __result + vClearDir * pushDist;
                        if (!onBridgeSpan || AiCore.IsSafeBridgeDeckStep(myPos, deflectedVeh, out deflectedVeh))
                        {
                            __result = deflectedVeh;
                        }
                    }
                }
                catch { }
            }
        }

        // =====================================================================
        // Strategic Door & Window Shutter Discipline (Soldier.CheckAndOpenDoor)
        // =====================================================================
        private static bool IsAnySquadmateApproachingDoor(Squad squad, Soldier self, Vector3 doorPos)
        {
            if (squad == null || squad.units == null)
                return false;

            var units = squad.units;
            int count = units.Count;
            for (int i = 0; i < count; i++)
            {
                Soldier ally = units[i];
                if (ally == null || !ally.IsAlive || ally.Pointer == self.Pointer || ally.transform == null)
                    continue;

                Vector3 allyPos = ally.transform.position;
                Vector3 toDoor = doorPos - allyPos;
                toDoor.y = 0f;
                float dSq = toDoor.sqrMagnitude;
                if (dSq <= 2.25f)
                    return true;

                SoldierAI allyAi = ally.aiController;
                if (dSq <= 16.0f && allyAi != null && allyAi.IsMoving())
                {
                    Vector3 allyMove = allyAi.MoveDestination - allyPos;
                    allyMove.y = 0f;
                    if (allyMove.sqrMagnitude > 0.04f && Vector3.Dot(allyMove.normalized, toDoor.normalized) > 0.25f)
                        return true;
                }
            }

            return false;
        }

        [HarmonyPatch(typeof(Soldier), nameof(Soldier.CheckAndOpenDoor))]
        public static class Soldier_CheckAndOpenDoor_Patch
        {
            public static bool Prefix(Soldier __instance, Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Collider> __0)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value ||
                        !VisionConfig.EnableStrategicDoorDiscipline.Value ||
                        __instance == null ||
                        __0 == null)
                    {
                        return true;
                    }

                    if (!AiCore.IsLocallyOwnedBot(__instance))
                        return true;

                    int len = __0.Length;
                    if (len == 0)
                        return false;

                    AiCore.SoldierFastHandles doorH = AiCore.GetOrCreateSoldierHandles(__instance);
                    float now = Time.time;

                    if (doorH == null || now < doorH.Combat.NextBotDoorCheckTime)
                        return false;

                    doorH.Combat.NextBotDoorCheckTime = now + 0.30f;
                    IntPtr botPtr = doorH.Ptr;

                    Transform botTransform = __instance.transform;
                    if (botTransform == null)
                        return false;

                    Vector3 botPos = botTransform.position;
                    Vector3 botFwd = botTransform.forward;
                    botFwd.y = 0f;
                    if (botFwd.sqrMagnitude > 0.001f)
                        botFwd.Normalize();

                    Transform botRoot = botTransform.root;
                    IntPtr botRootPtr = botRoot != null ? botRoot.Pointer : IntPtr.Zero;

                    SoldierAI ai = __instance.aiController;
                    bool isMoving = ai != null && ai.IsMoving();
                    Vector3 moveDir = botFwd;
                    if (isMoving && ai != null)
                    {
                        Vector3 toDest = ai.MoveDestination - botPos;
                        toDest.y = 0f;
                        if (toDest.sqrMagnitude > 0.04f)
                            moveDir = toDest.normalized;
                    }

                    float curSupp = __instance.GetSuppressionValue();
                    // Never slam open doors shut just because a soldier is reloading; only close exposed doorways
                    // under heavy suppression (>= 58) or while bleeding out so indoor defenders keep their firing lane!
                    bool isDoorVulnerable = curSupp >= 58f || __instance.isBleeding;
                    // Never close window shutters just because a soldier is reloading or lightly suppressed;
                    // only close shutters under severe suppression (>= 78) or while bleeding out.
                    bool isShutterVulnerable = curSupp >= 78f || __instance.isBleeding;

                    Squad squad = __instance.joinedSquad;
                    Vector3 threatDir = GetEffectiveThreatDirection(__instance, squad, botPos, botFwd);

                    for (int i = 0; i < len; i++)
                    {
                        Collider col = __0[i];
                        if (col == null || col.Pointer == IntPtr.Zero)
                            continue;

                        IntPtr colPtr = col.Pointer;
                        if (!_colliderDoorCache.TryGetValue(colPtr, out CachedDoorInfo info))
                        {
                            InteragibleDoor resolvedDoor = null;
                            InteragibleDoorAdditionalInteractor addInt = col.GetComponent<InteragibleDoorAdditionalInteractor>();
                            if (addInt != null && addInt.connectedDoor != null)
                            {
                                resolvedDoor = addInt.connectedDoor;
                            }
                            else
                            {
                                resolvedDoor = col.GetComponent<InteragibleDoor>();
                            }

                            IntPtr rPtr = IntPtr.Zero;
                            bool isShutter = false;
                            if (resolvedDoor != null && resolvedDoor.transform != null)
                            {
                                Transform dRoot = resolvedDoor.transform.root;
                                if (dRoot != null)
                                    rPtr = dRoot.Pointer;

                                Bounds b = col.bounds;
                                isShutter = b.size.y > 0.05f && b.size.y < 1.42f;
                            }

                            info = new CachedDoorInfo
                            {
                                Door = resolvedDoor,
                                RootPtr = rPtr,
                                IsWindowShutter = isShutter
                            };
                            _colliderDoorCache[colPtr] = info;
                        }

                        InteragibleDoor door = info.Door;
                        if (door == null || door.Pointer == IntPtr.Zero || !door.interactable)
                            continue;

                        if (info.RootPtr != IntPtr.Zero && info.RootPtr == botRootPtr)
                            continue;

                        Transform doorTr = door.transform;
                        if (doorTr == null)
                            continue;

                        Vector3 doorPos = doorTr.position;
                        Vector3 toDoor = doorPos - botPos;
                        toDoor.y = 0f;
                        float distSq = toDoor.sqrMagnitude;
                        Vector3 toDoorDir = distSq > 0.001f ? toDoor / Mathf.Sqrt(distSq) : botFwd;

                        IntPtr doorPtr = door.Pointer;
                        bool onCooldown = _nextDoorToggleTime.TryGetValue(doorPtr, out float nextToggle) && now < nextToggle;

                        if (!door.isOpen)
                        {
                            if (info.IsWindowShutter)
                            {
                                if (!onCooldown && !isShutterVulnerable && distSq <= 6.76f)
                                {
                                    if (AiCore.TryConsumeBotRpcBudget(0.25f, AiCore.BotRpcPriority.Low))
                                    {
                                        door.SetDoor(true);
                                        _nextDoorToggleTime[doorPtr] = now + 3.5f;
                                        InvalidateWatchWindowCache(botPtr);
                                    }
                                    else
                                    {
                                        _nextDoorToggleTime[doorPtr] = now + 1.5f;
                                    }
                                }
                            }
                            else
                            {
                                bool walkingThrough = isMoving && Vector3.Dot(moveDir, toDoorDir) > 0.20f;
                                bool facingClose = distSq <= 1.96f && Vector3.Dot(botFwd, toDoorDir) > 0.35f;
                                bool openDoor = walkingThrough || facingClose;
                                if (!openDoor &&
                                    !isMoving &&
                                    !onCooldown &&
                                    !isDoorVulnerable &&
                                    distSq <= 6.25f &&
                                    threatDir.sqrMagnitude > 0.01f &&
                                    Vector3.Dot(toDoorDir, threatDir) > 0.45f &&
                                    Vector3.Dot(botFwd, toDoorDir) > 0.35f)
                                {
                                    if (AiCore.TryConsumeBotRpcBudget(0.25f, AiCore.BotRpcPriority.Low))
                                        openDoor = true;
                                    else
                                        _nextDoorToggleTime[doorPtr] = now + 1.5f;
                                }
                                if (openDoor)
                                {
                                    door.SetDoor(true);
                                    _nextDoorToggleTime[doorPtr] = now + 3.5f;
                                }
                            }
                        }
                        else if (!onCooldown)
                        {
                            bool shouldClose = false;

                            if (info.IsWindowShutter)
                            {
                                if (!isMoving &&
                                    isShutterVulnerable &&
                                    distSq <= 5.76f &&
                                    (threatDir.sqrMagnitude < 0.01f || Vector3.Dot(toDoorDir, threatDir) > 0.15f))
                                {
                                    shouldClose = true;
                                }
                            }
                            else
                            {
                                bool passedThroughDoor = isMoving &&
                                                         distSq >= 1.44f &&
                                                         distSq <= 7.84f &&
                                                         Vector3.Dot(moveDir, toDoorDir) < -0.35f;

                                bool exposedIndoorDoor = !isMoving &&
                                                         isDoorVulnerable &&
                                                         distSq <= 6.25f &&
                                                         threatDir.sqrMagnitude > 0.01f &&
                                                         Vector3.Dot(toDoorDir, threatDir) > 0.30f;

                                if ((passedThroughDoor || exposedIndoorDoor) &&
                                    !IsAnySquadmateApproachingDoor(squad, __instance, doorPos))
                                {
                                    shouldClose = true;
                                }
                            }

                            if (shouldClose)
                            {
                                if (AiCore.TryConsumeBotRpcBudget(0.35f, AiCore.BotRpcPriority.Low))
                                {
                                    door.SetDoor(false);
                                    _nextDoorToggleTime[doorPtr] = now + (info.IsWindowShutter ? 4.0f : 10.0f);
                                    if (info.IsWindowShutter)
                                    {
                                        InvalidateWatchWindowCache(botPtr);
                                    }
                                    DoorsStrategicallyClosedCount++;
                                    if (VisionConfig.DebugLogging.Value)
                                    {
                                        string kind = info.IsWindowShutter ? "window shutter" : "door";
                                        AiCore.LogDebug("DoorDiscipline", $"{__instance.faction} soldier strategically closed {kind} for cover/concealment.");
                                    }
                                }
                                else
                                {
                                    _nextDoorToggleTime[doorPtr] = now + 1.5f;
                                }
                            }
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

        // =====================================================================
        // Player Hot-Swap Clean Trigger & GOAP State Reset (Soldier.OnSetPlayer)
        // Vanilla Soldier.OnSetPlayer (0x180758890) destroys soldier.aiController
        // without calling SetGunTriggerPressed(false)! Ensure trigger & bot locks
        // are cleanly released whenever the player takes control of an AI unit.
        // =====================================================================
        [HarmonyPatch(typeof(Soldier), nameof(Soldier.OnSetPlayer))]
        public static class Soldier_OnSetPlayer_Patch
        {
            public static void Postfix(Soldier __instance)
            {
                try
                {
                    if (__instance == null || __instance.Pointer == IntPtr.Zero)
                        return;

                    __instance.SetGunTriggerPressed(false);
                    IntPtr ptr = __instance.Pointer;
                    if (AiCore.TryGetSoldierHandles(ptr, out AiCore.SoldierFastHandles sh))
                    {
                        sh.Vision.IsTurningToWatchSector = false;
                        sh.Combat.FireAndMovement = default;
                        sh.Stance.LocalCoverProtectedUntil = 0f;
                        sh.Combat.FFBlockedUntil = 0f;
                        sh.Stance.WallBlockedUntil = 0f;
                        sh.Combat.PendingSmokeAimUntil = 0f;
                        sh.Combat.PendingSmokePrefabPtr = IntPtr.Zero;
                        sh.Goap.Plan = default;
                        sh.Goap.Scavenge = default;
                        sh.MoveOverride = default;
                    }

                    if (__instance.IsOnVehicle())
                    {
                        Vehicle veh = __instance.GetCurrentVehicle();
                        if (veh != null && veh.IsAirVehicle())
                        {
                            VehiclePlane vp = veh.TryCast<VehiclePlane>();
                            if (vp != null)
                            {
                                BeginPlaneHotStartWindow(vp, allowAiPilot: false, forceRecheck: true);
                            }
                        }
                    }
                }
                catch { }
            }
        }

        private static bool HasClearVaultArcAndLanding(
            Vector3 myPos,
            Vector3 vaultPos,
            int vaultMask,
            float crestHeight,
            out string rejectReason,
            float maxDropDeltaY = 1.68f)
        {
            rejectReason = string.Empty;
            int solidMask = AiCore.GetSolidEnvironmentMask();
            int blockMask = vaultMask | solidMask;

            // Never trust vanilla's pre-filled (myPos + fwd * 1.12m) if there is no solid walkable landing surface beneath it.
            float downDist = maxDropDeltaY > 2.0f ? (1.15f + maxDropDeltaY + 0.35f) : 3.0f;
            if (!Physics.Raycast(vaultPos + Vector3.up * 1.15f, Vector3.down, out RaycastHit landFloorHit, downDist, solidMask, QueryTriggerInteraction.Ignore) ||
                landFloorHit.normal.y < 0.55f ||
                (landFloorHit.point.y - myPos.y) > 1.68f ||
                (myPos.y - landFloorHit.point.y) > maxDropDeltaY)
            {
                rejectReason = "no_landing_floor";
                return false;
            }
            if (AiCore.IsVehicleCollider(landFloorHit.collider))
            {
                rejectReason = "vehicle_hull";
                return false;
            }
            if (AiCore.TryGetActiveWaterLevel(out float arcWaterY) && landFloorHit.point.y <= arcWaterY + 0.15f)
            {
                rejectReason = "no_landing_floor";
                return false;
            }

            Vector3 landPt = landFloorHit.point;
            Vector3 horizDelta = new Vector3(landPt.x - myPos.x, 0f, landPt.z - myPos.z);
            float horizDist = horizDelta.magnitude;
            if (horizDist < 0.20f)
            {
                rejectReason = "bad_landing_dist";
                return false;
            }

            Vector3 horizDir = horizDelta / horizDist;
            float baseHeight = Mathf.Clamp(crestHeight, 0.55f, 1.55f);
            float arcLowY = Mathf.Max(myPos.y, landPt.y) + baseHeight + 0.24f;
            float arcHighY = arcLowY + 0.28f;

            if (Physics.SphereCast(new Vector3(myPos.x, arcLowY, myPos.z), 0.19f, horizDir, out RaycastHit lowArcHit, horizDist, blockMask, QueryTriggerInteraction.Ignore) &&
                Mathf.Abs(lowArcHit.normal.y) < 0.72f)
            {
                rejectReason = "blocked_behind_wall";
                return false;
            }

            if (Physics.SphereCast(new Vector3(myPos.x, arcHighY, myPos.z), 0.19f, horizDir, out RaycastHit highArcHit, horizDist, blockMask, QueryTriggerInteraction.Ignore) &&
                Mathf.Abs(highArcHit.normal.y) < 0.72f)
            {
                rejectReason = "blocked_behind_wall";
                return false;
            }

            float dropDist = arcLowY - (landPt.y + 0.34f);
            if (dropDist > 0.10f &&
                Physics.SphereCast(new Vector3(landPt.x, arcLowY, landPt.z), 0.18f, Vector3.down, out RaycastHit dropHit, dropDist, blockMask, QueryTriggerInteraction.Ignore) &&
                Mathf.Abs(dropHit.normal.y) < 0.72f)
            {
                rejectReason = "landing_occupied";
                return false;
            }

            if (Physics.CheckCapsule(landPt + Vector3.up * 0.34f, landPt + Vector3.up * 1.48f, 0.19f, blockMask, QueryTriggerInteraction.Ignore))
            {
                rejectReason = "landing_occupied";
                return false;
            }

            return true;
        }

        private static bool GuardDirectMoveSegment(SoldierAI ai, ref Vector3 targetPos)
        {
            if (!VisionConfig.EnableMod.Value || ReferenceEquals(ai, null) || ai.Pointer == IntPtr.Zero)
                return true;

            AiCore.SoldierFastHandles h = AiCore.GetOrCreateSoldierHandlesByAi(ai);
            if (!AiCore.IsLocallyOwnedBot(h) || h.Tr == null)
                return true;

            Soldier s = h.Ref;
            if (s.IsOnVehicle() || s.FallingWithParachute())
                return true;

            Vector3 myPos = h.Tr.position;
            if (AiCore.TryGetActiveWaterLevel(out float waterY) && myPos.y <= waterY + 0.10f)
                return true;

            float now = Time.time;
            bool onVehicleHull = AiCore.IsFootBotStandingOnVehicleHull(myPos);
            bool isVehicleEvade = h.MoveOverride.Kind == AiCore.BotMoveOverrideKind.VehicleEvade && now < h.MoveOverride.ExpireTime;
            bool isCliffStepOff = now < h.Goap.FootStuck.CliffStepOffUntil;
            bool isUnstickRecovery =
                (h.MoveOverride.Kind == AiCore.BotMoveOverrideKind.UnstickSidestep && now < h.MoveOverride.ExpireTime) ||
                isVehicleEvade ||
                now < h.Goap.FootStuck.UnstickOverrideUntil;
            bool isRoomOrTrenchTrapped = isUnstickRecovery &&
                                         h.Goap.FootStuck.RoomTrapSince > 0f &&
                                         (now - h.Goap.FootStuck.RoomTrapSince) >= 8.0f;
            bool isTrenchOrLedgeStepDown = targetPos.y < myPos.y - 0.65f;

            if (AiCore.TryValidateAndSnapDirectMicroStep(
                    myPos,
                    targetPos,
                    out Vector3 snappedPos,
                    out _,
                    (isUnstickRecovery || isCliffStepOff || onVehicleHull) ? 5.0f : (isTrenchOrLedgeStepDown ? 3.6f : 2.8f),
                    isRoomOrTrenchTrapped ? 1.35f : 0.75f,
                    isCliffStepOff ? 6.2f : (onVehicleHull ? 4.2f : (isUnstickRecovery ? 2.95f : 2.65f)),
                    ignoreVehicleObstacles: isVehicleEvade || onVehicleHull))
            {
                targetPos = snappedPos;
                return true;
            }
            if (ai.moveDirectlyToward_end > now)
            {
                ai.moveDirectlyToward_end = 0f;
                ai.Stop(true);
            }

            ai.MoveDestination = targetPos;
            if (now >= h.NextMoveDirectRedirectPathTime || (h.LastRedirectedMoveDest - targetPos).sqrMagnitude > 2.25f)
            {
                h.NextMoveDirectRedirectPathTime = now + 0.35f;
                h.LastRedirectedMoveDest = targetPos;
                AiCore.ResetBotPathLockout(ai);
                ai.UpdatePath();
            }

            return false;
        }

        /// <summary>
        /// Guards <see cref="SoldierAI.MoveDirectlyToward"/> for locally owned grounded dry foot bots:
        /// allows unobstructed, floor-snapped micro-steps (&lt;= 2.8m), and redirects longer, wall-blocked,
        /// or floor-slab-crossing direct moves into vanilla <c>CPathFinding</c> so neither vanilla panic steps
        /// nor micro-nudge callers march bots through solid walls or floor slabs with CharacterController disabled.
        /// </summary>
        [HarmonyPatch(typeof(SoldierAI), nameof(SoldierAI.MoveDirectlyToward))]
        public static class SoldierAI_MoveDirectlyToward_Patch
        {
            public static bool Prefix(SoldierAI __instance, ref Vector3 __0)
            {
                try
                {
                    return GuardDirectMoveSegment(__instance, ref __0);
                }
                catch { }
                return true;
            }
        }

        /// <summary>
        /// Guards <see cref="SoldierAI.MoveDirectlyTowardNonPhysical"/> (<c>0x18055D130</c>) with the exact
        /// same surface-snapping and floor-slab penetration checks as <see cref="SoldierAI_MoveDirectlyToward_Patch"/>.
        /// </summary>
        [HarmonyPatch(typeof(SoldierAI), nameof(SoldierAI.MoveDirectlyTowardNonPhysical))]
        public static class SoldierAI_MoveDirectlyTowardNonPhysical_Patch
        {
            public static bool Prefix(SoldierAI __instance, ref Vector3 __0)
            {
                try
                {
                    return GuardDirectMoveSegment(__instance, ref __0);
                }
                catch { }
                return true;
            }
        }

        private static readonly Vector3[] AntiClipProbeDirs =
        {
            new Vector3(1f, 0f, 0f),
            new Vector3(0.7071f, 0f, 0.7071f),
            new Vector3(0f, 0f, 1f),
            new Vector3(-0.7071f, 0f, 0.7071f),
            new Vector3(-1f, 0f, 0f),
            new Vector3(-0.7071f, 0f, -0.7071f),
            new Vector3(0f, 0f, -1f),
            new Vector3(0.7071f, 0f, -0.7071f)
        };

        /// <summary>
        /// Intercepts vanilla <see cref="SoldierAI.MakeSureAiIsNotClippedInsideCollider"/>'s blind 6-second
        /// random-direction <c>10m</c> <c>d.A(myPos, groundPt)</c> panic branch (<c>0x18055C322</c>):
        /// immediately clears the corrupted synthetic 3D path (which otherwise angles bots on upper floors
        /// or raised foundations downward through floor slabs while async <c>CPathFinding</c> is in flight),
        /// restores their prior destination, and uses a surface-snapped <c>1.6m</c> radial sidestep or <c>UpdatePath()</c>.
        /// </summary>
        [HarmonyPatch(typeof(SoldierAI), nameof(SoldierAI.MakeSureAiIsNotClippedInsideCollider))]
        public static class SoldierAI_MakeSureAiIsNotClippedInsideCollider_Patch
        {
            public struct AntiClipSnapshot
            {
                public float PrevMoveDirectEnd;
                public Vector3 PrevMoveDest;
                public AiDestination PrevTargetDest;
            }

            public static void Prefix(SoldierAI __instance, out AntiClipSnapshot __state)
            {
                __state = default;
                try
                {
                    if (ReferenceEquals(__instance, null) || __instance.Pointer == IntPtr.Zero)
                        return;

                    __state.PrevMoveDirectEnd = __instance.moveDirectlyToward_end;
                    __state.PrevMoveDest = __instance.MoveDestination;
                    AiCore.SoldierFastHandles h = AiCore.GetOrCreateSoldierHandlesByAi(__instance);
                    if (h != null && h.Ref != null && h.Ptr != IntPtr.Zero &&
                        AiCore.GetRawTargetDestinationPtr(h.Ref) != IntPtr.Zero)
                    {
                        __state.PrevTargetDest = h.Ref.targetDestination;
                    }
                }
                catch { }
            }

            public static void Postfix(SoldierAI __instance, AntiClipSnapshot __state)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value || ReferenceEquals(__instance, null) || __instance.Pointer == IntPtr.Zero)
                        return;

                    // Detect vanilla's +6.0s blind IsInsideCollider escape without disrupting shorter normal steps.
                    if (__instance.moveDirectlyToward_end <= __state.PrevMoveDirectEnd + 4.5f)
                        return;

                    AiCore.SoldierFastHandles h = AiCore.GetOrCreateSoldierHandlesByAi(__instance);
                    if (!AiCore.IsLocallyOwnedBot(h) || h.Tr == null)
                        return;

                    Soldier s = h.Ref;
                    if (s.IsOnVehicle() || s.FallingWithParachute())
                        return;

                    Vector3 myPos = h.Tr.position;
                    if (AiCore.TryGetActiveWaterLevel(out float waterY) && myPos.y <= waterY + 0.10f)
                        return;

                    // CRITICAL: Immediately wipe vanilla's d.A(myPos, random10mGround) synthetic path AND +6s timers
                    // BEFORE UpdateMoveDirection can translate the bot diagonally through an upper floor / foundation!
                    __instance.moveDirectlyToward_end = 0f;
                    __instance.pathRefreshTime = 0f;
                    __instance.Stop(true);

                    // Restore the bot's real combat/objective destination that vanilla overwrote at 0x18055C2EA..0x18055C2F9.
                    if (!ReferenceEquals(__state.PrevTargetDest, null) && __state.PrevTargetDest.Pointer != IntPtr.Zero)
                    {
                        s.CoverPosition(__state.PrevTargetDest);
                    }
                    if (__state.PrevMoveDest.sqrMagnitude > 0.01f)
                    {
                        __instance.MoveDestination = __state.PrevMoveDest;
                    }

                    for (int i = 0; i < AntiClipProbeDirs.Length; i++)
                    {
                        Vector3 d = AntiClipProbeDirs[i];
                        if (AiCore.TryValidateAndSnapDirectMicroStep(
                                myPos,
                                myPos + d * 1.6f,
                                out Vector3 safeStep,
                                out _,
                                maxFlatDist: 1.8f,
                                maxStepDeltaY: 0.55f))
                        {
                            __instance.MoveDirectlyToward(safeStep, 0.75f);
                            return;
                        }
                    }

                    AiCore.ResetBotPathLockout(__instance);
                    __instance.UpdatePath();
                }
                catch { }
            }
        }

        /// <summary>
        /// Prevents native <see cref="SoldierAI.TeleportAlongPathIfPlayerIsNotLooking"/> (<c>0x1805609A0</c>) from
        /// permanently blacking out <c>CPathFinding</c> within 100m of the camera in Singleplayer or teleporting bots
        /// 20m forward in front of human players in Multiplayer (<c>PhotonNetwork.InRoom</c>). Clears <c>mustTeleport</c>
        /// (<c>+0xB4</c>), <c>pathCreationFails</c> (<c>+0xB0</c>), and <c>lockPathCalculationRetryTime</c> (<c>+0x74</c>),
        /// drops unreachable <see cref="CombatCover"/> targets cleanly, and lets player-style unstick + <c>CPathFinding</c> recover.
        /// </summary>
        [HarmonyPatch(typeof(SoldierAI), nameof(SoldierAI.TeleportAlongPathIfPlayerIsNotLooking))]
        public static class SoldierAI_TeleportAlongPathIfPlayerIsNotLooking_Patch
        {
            public static bool Prefix(SoldierAI __instance)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value || ReferenceEquals(__instance, null) || __instance.Pointer == IntPtr.Zero)
                        return true;

                    AiCore.SoldierFastHandles h = AiCore.GetOrCreateSoldierHandlesByAi(__instance);
                    if (!AiCore.IsLocallyOwnedBot(h))
                        return true;

                    AiCore.ResetBotPathLockout(__instance);
                    Soldier s = h.Ref;
                    if (s != null && !s.IsOnVehicle() && !s.FallingWithParachute())
                    {
                        s.TryChangeCombatCoverIfNotReachable();
                        AiCore.InvalidateTargetDestinationCache(s);
                        // Native SoldierAI (0x18055EF6C) skips pathRefreshTime (+0x68) whenever path == null,
                        // checking lockPathCalculationRetryTime (+0x74) at 0x18055EF25 instead.
                        __instance.LockPathCalculationFor(0.65f);
                        __instance.pathRefreshTime = Time.time + 0.65f;
                    }
                    return false;
                }
                catch { }
                return true;
            }
        }
    }
}
