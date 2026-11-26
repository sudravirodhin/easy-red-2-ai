using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace EasyRed2HumanAI
{
    public static partial class VeteranAndTacticsPatches
    {
        // Static battlefield / corpse / door / target-slot caches remain here;
        // per-soldier stance, cover, and combat state now live on AiCore.SoldierFastHandles.

        private struct MeleeTargetSlotState
        {
            public IntPtr Attacker0;
            public float Expire0;
            public IntPtr Attacker1;
            public float Expire1;
        }

        private static readonly Dictionary<IntPtr, MeleeTargetSlotState> _meleeTargetSlots = new Dictionary<IntPtr, MeleeTargetSlotState>(64);
        // Suppression-pacing & cover-protection timers are stored on AiCore.SoldierFastHandles.Stance / Combat.

        internal struct FireAndMovementState
        {
            public bool InFiringHalt;
            public float PhaseStartTime;
            public float PhaseEndTime;
            public float HaltLostSightTime;
            public Vector3 AnchorMoveDest;
        }

        // Active FireAndMovementState lives in AiCore.SoldierFastHandles.Combat.FireAndMovement.

        internal struct RotateAndFireFrameState
        {
            public int Frame;
            public bool BlockFire;
            public bool WallBlocked;
            public bool InPointBlankMelee;
            public bool IsMoving;
            public bool IsPeeking;
            public bool HasCustomTarget;
            public bool ClampDistForEmplacementShoot;
            public Vector3 CustomTargetPos;
        }

        internal struct InfantrySightReactionState
        {
            public IntPtr LastTargetPtr;
            public float LastSeenTime;
            public float LockedUntil;
        }

        // RotateAndFireFrameState / InfantrySightReactionState / LastBayonetCheckedGunPtr live in AiCore.SoldierFastHandles.Combat.
        private struct CoverExposureCacheEntry
        {
            public float ExpireTime;
            public bool IsInvalid;
        }

        private static readonly Dictionary<long, CoverExposureCacheEntry> _coverExposureCache = new Dictionary<long, CoverExposureCacheEntry>(1024);
        private static readonly Dictionary<IntPtr, Vector3> _staticCoverPosCache = new Dictionary<IntPtr, Vector3>(1024);
        private static float _nextPlayerCrosshairYieldPassTime;

        private struct CachedDoorInfo
        {
            public InteragibleDoor Door;
            public IntPtr RootPtr;
            public bool IsWindowShutter;
        }

        private static readonly Dictionary<IntPtr, CachedDoorInfo> _colliderDoorCache = new Dictionary<IntPtr, CachedDoorInfo>(256);
        private static readonly Dictionary<IntPtr, float> _nextDoorToggleTime = new Dictionary<IntPtr, float>(128);
        public static int DoorsStrategicallyClosedCount { get; private set; }
        public static int TankHullCoverUsesCount { get; private set; }
        public static int PlayerCrosshairYieldsCount { get; private set; }
        public static int UnsafeBandagesBlockedCount { get; private set; }
        public static int CloseGunfireMeleeSuppressedCount { get; private set; }
        public static int SlopedVaultsRejectedCount { get; private set; }

        public static void ClearDoorCaches(bool forceFull = false)
        {
            if (forceFull || _colliderDoorCache.Count > 512)
                _colliderDoorCache.Clear();
            if (forceFull || _nextDoorToggleTime.Count > 256)
                _nextDoorToggleTime.Clear();
            if (forceFull || _meleeTargetSlots.Count > 128)
                _meleeTargetSlots.Clear();
            if (forceFull || _staticCoverPosCache.Count > 4096)
                _staticCoverPosCache.Clear();
            _nextPlayerCrosshairYieldPassTime = 0f;
        }

        // =====================================================================
        // Muzzle Obstruction, Friendly Fire & Anti-Stacking Helpers
        // =====================================================================
        internal static void IncrementWallsBlocked() => WallsBlocked++;

        /// <summary>
        /// Catches rifle/SMG barrels clipping through thin walls (including leaning around corners into a wall)
        /// without falsely blocking legitimate shots over waist-high sandbags or window sills where face-to-muzzle is clear.
        /// </summary>
        internal static bool IsBotGunClippedThroughWall(Soldier shooter, bool targetIsVehicle = false)
        {
            if (shooter == null || AiCore.IsSoldierInVehicleOrTransition(shooter))
                return false;

            GenericGun heldGun = shooter.GetHeldGun();
            if (heldGun == null)
                return false;

            Vector3 muzzlePos = heldGun.GetBulletGenerationPosition();
            if (muzzlePos.sqrMagnitude < 0.01f)
                muzzlePos = shooter.GunPosition();

            Transform myRoot = shooter.transform != null ? shooter.transform.root : null;
            Vector3 basePos = shooter.transform.position;
            Vector3 unleanedTorso = basePos + Vector3.up * AiCore.GetBarrelHeightForPose(shooter.m_pose);
            Vector3 facePos = shooter.FacePosition();
            Vector3 leanedTorso = new Vector3(facePos.x, unleanedTorso.y, facePos.z);

            if (Mathf.Abs(shooter.LeanAngle) >= 0.45f &&
                AiCore.LinecastEnvironment(unleanedTorso, leanedTorso, myRoot, out _, targetIsVehicle))
            {
                return true;
            }

            if (AiCore.LinecastEnvironment(leanedTorso, muzzlePos, myRoot, out _, targetIsVehicle) &&
                AiCore.LinecastEnvironment(facePos, muzzlePos, myRoot, out _, targetIsVehicle))
            {
                return true;
            }

            return false;
        }

        private static bool IsTargetFullyOccludedBySolidWall(Soldier shooter, Spottable target, Vector3 targetCenterPos, float distToTarget)
        {
            if (shooter == null || target == null || distToTarget <= 0.45f || target.IsAirVehicle())
                return false;

            Transform myRoot = shooter.transform != null ? shooter.transform.root : null;
            bool targetIsVehicle = target.IsWheeledVehicleOrTank();
            Vector3 eyePos = shooter.FacePosition();
            Vector3 targetSpot = target.SpotPosition();

            if (!AiCore.LinecastEnvironment(eyePos, targetSpot, myRoot, out _, targetIsVehicle))
                return false;

            Vector3 barrelPos = shooter.transform.position + Vector3.up * AiCore.GetBarrelHeightForPose(shooter.m_pose);
            if (!AiCore.LinecastEnvironment(barrelPos, targetCenterPos, myRoot, out _, targetIsVehicle))
                return false;

            return true;
        }

        private static bool IsMuzzleBlockedByWall(Soldier shooter, Vector3 targetPos, float distToTarget, bool targetIsVehicle = false)
        {
            if (shooter == null || distToTarget <= 0.30f)
                return false;

            if (AiCore.HorizontalAngleToDirection(shooter, targetPos - shooter.transform.position) > 35f)
                return false;

            float leanAngle = shooter.LeanAngle;
            bool activelyLeaning = Mathf.Abs(leanAngle) >= 0.45f;

            if (IsBotGunClippedThroughWall(shooter, targetIsVehicle))
                return true;

            Transform myRoot = shooter.transform.root;
            Vector3 soldierPos = shooter.transform.position;
            float barrelH = AiCore.GetBarrelHeightForPose(shooter.m_pose);

            float midLowWallDist = distToTarget > 3.5f ? Mathf.Min(12.0f, distToTarget * 0.45f) : (distToTarget - 0.35f);
            Vector3 spineAtBarrel = soldierPos + Vector3.up * barrelH;
            if (Mathf.Abs(leanAngle) > 0.15f && shooter.transform != null)
            {
                spineAtBarrel += shooter.transform.right * (leanAngle * 0.40f);
            }
            Vector3 toTargetFromSpine = targetPos - spineAtBarrel;
            if (toTargetFromSpine.sqrMagnitude > 0.04f)
            {
                Vector3 spineAimDir = toTargetFromSpine.normalized;
                float spineMaxCheck = Mathf.Min(Mathf.Max(VisionConfig.MuzzleObstructionCheckDist.Value + 0.65f, midLowWallDist), distToTarget - 0.35f);
                if (spineMaxCheck > 0.25f)
                {
                    Vector3 spineStart = spineAtBarrel - spineAimDir * 0.10f;
                    if (AiCore.IsDirectionBlockedByEnvironment(spineStart, spineAimDir, spineMaxCheck, myRoot, targetIsVehicle))
                        return true;
                }

                // Short solid-environment eye-lintel check catches face-in-wall or low window headers
                // without tripping on friendly silhouettes or waist-high parapets.
                if (!activelyLeaning)
                {
                    float eyeCheckDist = Mathf.Min(0.55f, distToTarget - 0.35f);
                    if (eyeCheckDist > 0.20f &&
                        AiCore.IsDirectionBlockedByEnvironment(shooter.FacePosition(), spineAimDir, eyeCheckDist, myRoot, targetIsVehicle))
                    {
                        return true;
                    }
                }
            }

            if (!shooter.isInBodyAnimation && !shooter.IsReloading && shooter.pointingGun)
            {
                Vector3 gunPos = shooter.GunPosition();
                GenericGun heldGun = shooter.GetHeldGun();
                Vector3 receiverPos = (heldGun != null && heldGun.transform != null) ? heldGun.transform.position : gunPos;

                // Only clamp against sub-ground-plane bone anomalies; never lift rays up over low walls.
                float floorSafeY = soldierPos.y + 0.15f;
                if (receiverPos.y < floorSafeY) receiverPos.y = floorSafeY;
                if (gunPos.y < floorSafeY) gunPos.y = floorSafeY;

                // Skip hip/low-ready weapon bone probes while still raising the rifle into shoulder height.
                if (gunPos.y >= soldierPos.y + barrelH - 0.16f)
                {
                    Vector3 toTarget = targetPos - receiverPos;
                    Vector3 aimDir = toTarget.sqrMagnitude > 0.01f ? toTarget.normalized : shooter.GetFireDir();

                    float maxCheck = Mathf.Min(Mathf.Max(VisionConfig.MuzzleObstructionCheckDist.Value + 0.35f, midLowWallDist), distToTarget - 0.35f);
                    if (maxCheck > 0.25f)
                    {
                        if (AiCore.IsDirectionBlockedByEnvironment(receiverPos - aimDir * 0.10f, aimDir, maxCheck, myRoot, targetIsVehicle))
                            return true;

                        if (AiCore.IsDirectionBlockedByEnvironment(gunPos - aimDir * 0.15f, aimDir, maxCheck, myRoot, targetIsVehicle))
                            return true;
                    }
                }
            }

            return false;
        }

        internal static bool TryResolveExposedOverWallAim(
            Soldier shooter,
            Spottable target,
            Vector3 torsoPos,
            float distToTarget,
            out Vector3 exposedPos)
        {
            exposedPos = torsoPos;
            if (shooter == null || target == null || distToTarget <= 0.60f || target.IsWheeledVehicleOrTank() || target.IsAirVehicle())
                return false;

            Vector3 spotPos = target.SpotPosition();
            if (spotPos.y <= torsoPos.y + 0.14f)
                return false;

            Vector3 shoulderedOrigin = shooter.transform.position + Vector3.up * AiCore.GetBarrelHeightForPose(shooter.m_pose);
            float leanAngle = shooter.LeanAngle;
            if (Mathf.Abs(leanAngle) > 0.15f && shooter.transform != null)
            {
                shoulderedOrigin += shooter.transform.right * (leanAngle * 0.40f);
            }
            Transform myRoot = shooter.transform.root;

            for (int i = 0; i < 2; i++)
            {
                Vector3 cand = Vector3.Lerp(torsoPos, spotPos, i == 0 ? 0.80f : 0.90f);
                if (IsMuzzleBlockedByWall(shooter, cand, distToTarget))
                    continue;

                Vector3 toCand = cand - shoulderedOrigin;
                float candLen = toCand.magnitude;
                if (candLen <= 0.40f)
                {
                    exposedPos = cand;
                    return true;
                }

                float checkDist = Mathf.Max(0.25f, candLen - 0.30f);
                if (!AiCore.RaycastEnvironment(shoulderedOrigin, toCand / candLen, checkDist, myRoot, out _))
                {
                    exposedPos = cand;
                    return true;
                }
            }

            return false;
        }

        private static bool IsFriendlyInLineOfFire(Soldier shooter, Vector3 targetPos, float distToTarget, float vet01, out Vector3 sidestepDir, out Soldier blockingFriend)
        {
            sidestepDir = Vector3.zero;
            blockingFriend = null;
            if (distToTarget <= 1.8f)
                return false;

            Vector3 gunPos = shooter.GunPosition();
            Vector3 toTarget = targetPos - gunPos;
            Vector3 fireDir = toTarget.sqrMagnitude > 0.001f ? toTarget.normalized : shooter.GetFireDir();

            float maxCheckDist = Mathf.Min(distToTarget - 0.5f, Mathf.Lerp(40f, 60f, vet01));
            if (maxCheckDist <= 0.5f)
                return false;

            float allowedConeDeg = VisionConfig.FriendlyFireConeDegrees.Value + (vet01 * 1.5f);
            float corridorWidth = VisionConfig.FriendlyFireCorridorWidth.Value;
            float now = Time.time;
            AiCore.SoldierFastHandles shooterH = AiCore.GetOrCreateSoldierHandles(shooter);
            string myFaction = shooterH != null ? AiCore.GetCachedFaction(shooterH, now) : AiCore.InternFaction(shooter.faction);
            IntPtr myPtr = shooter.Pointer;

            Soldier player = Soldier.CurrentControlledSoldierOrNull();
            if (player != null &&
                player.IsAlive &&
                player.Pointer != myPtr)
            {
                string pFaction = AiCore.GetCachedFaction(player);
                if (!string.IsNullOrEmpty(pFaction) && AiCore.AreSameSideFactions(myFaction, pFaction))
                {
                    if (CheckSingleFriendlyInCone(gunPos, fireDir, maxCheckDist, allowedConeDeg, corridorWidth, shooter, player, out sidestepDir))
                    {
                        blockingFriend = player;
                        return true;
                    }
                }
            }

            Squad mySquad = shooter.joinedSquad;
            if (mySquad != null && mySquad.units != null)
            {
                var units = mySquad.units;
                int c = units.Count;
                for (int i = 0; i < c; i++)
                {
                    Soldier f = units[i];
                    if (ReferenceEquals(f, null) || f == null || !f.IsAlive || f.Pointer == myPtr) continue;
                    if (CheckSingleFriendlyInCone(gunPos, fireDir, maxCheckDist, allowedConeDeg, corridorWidth, shooter, f, out sidestepDir))
                    {
                        blockingFriend = f;
                        return true;
                    }
                }
            }

            // Scan flat WorldSoldiers directly so detached vehicle crewmen, remote Co-op human players,
            // and allied sub-factions are all protected with pure C# L1-cache spatial culling.
            AiCore.EnsureWorldSnapshot();
            int solCount = AiCore.WorldSoldierCount;
            if (solCount > 0)
            {
                IntPtr mySqPtr = mySquad != null ? mySquad.Pointer : IntPtr.Zero;
                IntPtr playerPtr = player != null ? player.Pointer : IntPtr.Zero;
                float maxDistWithMarginSq = (maxCheckDist + 3.0f) * (maxCheckDist + 3.0f);
                var soldiers = AiCore.WorldSoldiers;

                for (int i = 0; i < solCount; i++)
                {
                    ref AiCore.SoldierSnapshot solSnap = ref soldiers[i];
                    if (solSnap.Ptr == myPtr || solSnap.Ptr == playerPtr || (mySqPtr != IntPtr.Zero && solSnap.SquadPtr == mySqPtr))
                        continue;

                    // Pure C# L1-cache spatial cull BEFORE any string/IL2CPP boundary crossing
                    Vector3 approxDelta = (solSnap.Pos + new Vector3(0f, 0.9f, 0f)) - gunPos;
                    float approxSq = approxDelta.sqrMagnitude;
                    if (approxSq > maxDistWithMarginSq || Vector3.Dot(approxDelta, fireDir) <= -0.5f)
                        continue;

                    if (!AiCore.AreSameSideFactions(myFaction, solSnap.Faction))
                        continue;

                    Soldier f = solSnap.Ref;
                    if (f == null || !f.IsAlive)
                        continue;

                    if (CheckSingleFriendlyInCone(gunPos, fireDir, maxCheckDist, allowedConeDeg, corridorWidth, shooter, f, out sidestepDir))
                    {
                        blockingFriend = f;
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool CheckSingleFriendlyInCone(
            Vector3 gunPos,
            Vector3 fireDir,
            float maxCheckDist,
            float allowedConeDeg,
            float corridorWidth,
            Soldier shooter,
            Soldier friend,
            out Vector3 sidestepDir)
        {
            sidestepDir = Vector3.zero;
            Vector3 friendPos = friend.GetCenterOfUnit();
            Vector3 toFriend = friendPos - gunPos;
            float distSq = toFriend.sqrMagnitude;
            if (distSq > maxCheckDist * maxCheckDist || distSq < 0.04f)
                return false;

            float forwardDist = Vector3.Dot(toFriend, fireDir);
            if (forwardDist <= 0.1f || forwardDist >= maxCheckDist)
                return false;

            float angle = Vector3.Angle(toFriend, fireDir);
            float lateralDist = Vector3.Cross(fireDir, toFriend).magnitude;

            float effCorridor = corridorWidth;
            float corridorRange = 8.0f;
            if (friend.IsOnVehicle())
            {
                Vehicle fv = friend.GetCurrentVehicle();
                if (fv != null && fv.Pointer != IntPtr.Zero && !fv.IsAirVehicle())
                {
                    IntPtr shooterVehPtr = (shooter.IsOnVehicle() && shooter.GetCurrentVehicle() != null)
                        ? shooter.GetCurrentVehicle().Pointer
                        : IntPtr.Zero;
                    if (shooterVehPtr != fv.Pointer)
                    {
                        effCorridor = Mathf.Max(corridorWidth, 1.65f);
                        corridorRange = 14.0f;
                    }
                }
            }

            if (angle <= allowedConeDeg || (forwardDist <= corridorRange && lateralDist <= effCorridor))
            {
                Vector3 right = shooter.transform.right;
                float side = Vector3.Dot(toFriend, right);
                sidestepDir = side >= 0f ? -right : right;
                return true;
            }
            return false;
        }

        private static void TryNudgeFriendlyOutOfFireCone(Soldier shooter, Soldier friend, Vector3 targetPos)
        {
            if (shooter == null ||
                friend == null ||
                !AiCore.IsLocallyOwnedBot(friend) ||
                friend.IsOnVehicle() ||
                friend.UsingBipod() ||
                friend.isInBodyAnimation ||
                friend.IsCharging() ||
                friend.transform == null ||
                shooter.transform == null)
            {
                return;
            }

            Vector3 gunPos = shooter.GunPosition();
            Vector3 friendPos = friend.transform.position;
            Vector3 toFriend = friendPos - gunPos;
            toFriend.y = 0f;
            float distSq = toFriend.sqrMagnitude;
            if (distSq > 14.0f * 14.0f || distSq < 0.04f)
                return;

            AiCore.SoldierFastHandles friendH = AiCore.GetOrCreateSoldierHandles(friend);
            if (friendH == null)
                return;

            float now = Time.time;
            if (now < friendH.Combat.NextFriendlyStepAsideTime)
                return;

            friendH.Combat.NextFriendlyStepAsideTime = now + 1.8f;

            SoldierAI friendAi = friend.aiController;
            // If standing right in front of the shooter (<= 6.5m), duck into a crouch so the gunner can fire over them immediately
            if (distSq <= 6.5f * 6.5f && friend.m_pose == SoldierPose.Idle && friendAi != null)
            {
                SetBotPose(friendAi, friend, SoldierPose.Crouch);
            }

            if (friendAi == null || friend.IsOnCover() || !AiCore.CanBotMoveAutonomously(friend))
                return;

            Vector3 fireDir = targetPos - gunPos;
            fireDir.y = 0f;
            fireDir = fireDir.sqrMagnitude > 0.001f ? fireDir.normalized : shooter.transform.forward;
            Vector3 right = new Vector3(fireDir.z, 0f, -fireDir.x);
            float side = Vector3.Dot(toFriend, right);
            Vector3 clearDir = side >= 0f ? right : -right;

            Transform friendRoot = friend.transform.root;
            Vector3 stepDir = clearDir;
            if (AiCore.RaycastEnvironment(friendPos + Vector3.up * 0.5f, stepDir, 2.0f, friendRoot, out _))
            {
                stepDir = -clearDir;
                if (AiCore.RaycastEnvironment(friendPos + Vector3.up * 0.5f, stepDir, 2.0f, friendRoot, out _))
                    return;
            }

            if (!friendAi.IsMoving())
            {
                AiCore.MoveBotTowardPosition(friendAi, friend, friendPos + stepDir * 1.85f, 0f, 0.95f);
                TrySayTacticalVoice(friend, friend.joinedSquad, VoiceManager.VoiceClip.watchYourFire, 0.05f);
            }
            else if ((friendAi.MoveDestination - friendPos).sqrMagnitude <= 9.0f * 9.0f)
            {
                if (friendAi.moveDirectlyToward_end > Time.time)
                    friendAi.moveDirectlyToward_end = 0f;
                friendAi.MoveDestination += stepDir * 1.65f;
                friendAi.UpdatePath();
            }
        }

        // =====================================================================
        // Human Player Crosshair Awareness & Proactive Yield (SP & Co-op)
        // Runs a lightweight player-centric pass every 0.12s (~8.3 Hz):
        //  1) Friendly bots caught inside a player's active sightline immediately
        //     duck under iron sights/scopes (<= 9.5m) and sidestep ~2m clear
        //     (even vacating non-locked cover right in front of an ADS player).
        //  2) Moving friendly bots about to cross in front of an ADS/firing player
        //     detour 1.65m BEHIND the player's back (when <= 4.5m) or crouch low
        //     under the player's sightline (4.5m..7.2m) instead of walking upright
        //     across the crosshair.
        // =====================================================================
        private static bool TryGetSafeYieldPoint(
            Vector3 originPos,
            Transform ignoreRoot,
            bool isOnBridge,
            Vector3 candidateXZ,
            out Vector3 validatedPos)
        {
            validatedPos = originPos;
            Vector3 delta = candidateXZ - originPos;
            delta.y = 0f;
            float dist = delta.magnitude;
            if (dist < 0.25f)
                return false;

            Vector3 dir = delta / dist;
            if (AiCore.IsMovementBlockedByObstacle(originPos + Vector3.up * 0.48f, dir, dist + 0.25f))
                return false;

            if (isOnBridge)
                return AiCore.IsSafeBridgeDeckStep(originPos, candidateXZ, out validatedPos);

            int envMask = AiCore.GetSolidEnvironmentMask();
            Vector3 groundProbe = new Vector3(candidateXZ.x, originPos.y + 1.30f, candidateXZ.z);
            if (!Physics.Raycast(groundProbe, Vector3.down, out RaycastHit groundHit, 2.8f, envMask, QueryTriggerInteraction.Ignore))
                return false;

            if (groundHit.normal.y < 0.60f || Mathf.Abs(groundHit.point.y - originPos.y) > 1.25f)
                return false;

            validatedPos = groundHit.point;
            return true;
        }

        private static bool TryFindClearLateralYieldStep(
            Vector3 botPos,
            Transform botRoot,
            bool isOnBridge,
            Vector3 preferredSideDir,
            in AiCore.PlayerAimLane lane,
            float forwardDist,
            out Vector3 stepPos)
        {
            const float lateralDist = 2.05f;
            if (TryGetSafeYieldPoint(botPos, botRoot, isOnBridge, botPos + preferredSideDir * lateralDist, out stepPos))
                return true;

            if (TryGetSafeYieldPoint(botPos, botRoot, isOnBridge, botPos - preferredSideDir * lateralDist, out stepPos))
                return true;

            if (forwardDist <= 3.5f &&
                !AiCore.RaycastEnvironment(lane.FeetPos + Vector3.up * 0.70f, -lane.AimDirFlat, 1.75f, lane.RootTr, out _) &&
                TryGetSafeYieldPoint(botPos, botRoot, isOnBridge, lane.FeetPos - lane.AimDirFlat * 1.65f, out stepPos))
            {
                return true;
            }

            stepPos = botPos;
            return false;
        }

        public static void UpdateFriendlyPlayerCrosshairYield()
        {
            if (!VisionConfig.EnableMod.Value ||
                !VisionConfig.EnableFriendlyFireAvoidance.Value ||
                Time.timeScale <= 0.01f)
            {
                return;
            }

            try
            {
                UpdateFriendlyPlayerCrosshairYieldCore();
            }
            catch { }
        }

        private static void UpdateFriendlyPlayerCrosshairYieldCore()
        {
            float now = Time.time;
            if (now < _nextPlayerCrosshairYieldPassTime && _nextPlayerCrosshairYieldPassTime - now <= 2.0f)
                return;

            if (AiCore.ShouldSkipBotCycleForNetworkPing())
                return;

            _nextPlayerCrosshairYieldPassTime = now + 0.12f;

            AiCore.EnsurePlayerAimLanesUpdated();
            int laneCount = AiCore.ActivePlayerAimLaneCount;
            if (laneCount <= 0)
                return;

            AiCore.EnsureWorldSnapshot();
            int sqCount = AiCore.WorldSquadCount;
            if (sqCount <= 0)
                return;

            var squads = AiCore.WorldSquads;
            var soldiers = AiCore.WorldSoldiers;
            bool checkBridges = VisionConfig.EnableBridgeCrossingDiscipline.Value;

            for (int pIdx = 0; pIdx < laneCount; pIdx++)
            {
                ref AiCore.PlayerAimLane lane = ref AiCore.ActivePlayerAimLanes[pIdx];
                for (int s = 0; s < sqCount; s++)
                {
                    ref AiCore.SquadSnapshot sqSnap = ref squads[s];
                    if (sqSnap.UnitCount == 0 || sqSnap.RefOnPlane || !AiCore.AreSameSideFactions(lane.Faction, sqSnap.Faction))
                        continue;

                    Vector3 sqDelta = sqSnap.RefPos - lane.FeetPos;
                    if (sqDelta.x * sqDelta.x + sqDelta.z * sqDelta.z > 44.0f * 44.0f)
                        continue;

                    int end = sqSnap.UnitStartIndex + sqSnap.UnitCount;
                    for (int i = sqSnap.UnitStartIndex; i < end; i++)
                    {
                        ref AiCore.SoldierSnapshot uSnap = ref soldiers[i];
                        if (uSnap.IsPlayer || uSnap.IsOnVehicle || uSnap.HasSurrendered || uSnap.Ptr == lane.Ptr)
                            continue;

                        if (Mathf.Abs(uSnap.Pos.y - lane.FeetPos.y) > 2.35f)
                            continue;

                        float snapDx = uSnap.Pos.x - lane.AimOrigin.x;
                        float snapDz = uSnap.Pos.z - lane.AimOrigin.z;
                        if (snapDx * snapDx + snapDz * snapDz > 18.0f * 18.0f)
                            continue;

                        Soldier bot = uSnap.Ref;
                        if (ReferenceEquals(bot, null) || bot == null || bot.Pointer == IntPtr.Zero)
                            continue;

                        AiCore.SoldierFastHandles botH = uSnap.Handles ?? AiCore.GetOrCreateSoldierHandles(bot);
                        if (botH == null ||
                            now < botH.Combat.NextFriendlyStepAsideTime ||
                            !AiCore.IsLocallyOwnedBot(botH) ||
                            botH.Ai == null ||
                            botH.Tr == null ||
                            bot.IsOnVehicle() ||
                            bot.UsingBipod() ||
                            bot.IsBipodTransitioning() ||
                            bot.isInBodyAnimation ||
                            bot.IsCharging() ||
                            bot.HasSurrended() ||
                            bot.IsCarried() ||
                            bot.IsCarryingBody() ||
                            IsActivelyHealingTeammate(bot, out _))
                        {
                            continue;
                        }

                        SoldierAI botAi = botH.Ai;
                        IntPtr botPtr = botH.Ptr;
                        Vector3 botPos = botH.Tr.position;
                        if (Mathf.Abs(botPos.y - lane.FeetPos.y) > 2.35f)
                            continue;

                        float dx = botPos.x - lane.AimOrigin.x;
                        float dz = botPos.z - lane.AimOrigin.z;
                        float fwd = dx * lane.AimDirFlat.x + dz * lane.AimDirFlat.z;
                        float sLat = dx * lane.RightFlat.x + dz * lane.RightFlat.z;
                        float halfWidth = lane.HalfCorridorWidth + Mathf.Max(0f, fwd) * 0.035f;
                        float rayY = lane.AimOrigin.y + lane.AimDir3D.y * Mathf.Max(0f, fwd);

                        bool insideCorridor = fwd >= 0.30f &&
                                              fwd <= lane.MaxClearForward &&
                                              Mathf.Abs(sLat) <= halfWidth &&
                                              rayY <= botPos.y + 2.35f &&
                                              rayY >= botPos.y - 0.55f;

                        if (insideCorridor)
                        {
                            if (fwd > 1.0f)
                            {
                                Vector3 toBotChest = (botPos + Vector3.up * 0.95f) - lane.AimOrigin;
                                float chestDist = toBotChest.magnitude;
                                if (chestDist > 0.6f &&
                                    AiCore.RaycastEnvironment(lane.AimOrigin, toBotChest / chestDist, chestDist - 0.25f, lane.RootTr, out _))
                                {
                                    continue;
                                }
                            }

                            bool isOnBridge = checkBridges && AiCore.IsOnBridgeOrNarrowCrossing(botPos);
                            bool yielded = false;

                            // Immediately drop head below standing/crouched player's sights within 9.5m
                            if (fwd <= 9.5f &&
                                !lane.IsProne &&
                                bot.m_pose == SoldierPose.Idle &&
                                AiCore.CanBotChangePoseAutonomously(bot))
                            {
                                SetBotPose(botAi, bot, SoldierPose.Crouch, true);
                                botH.Stance.CombatCrouchLockUntil = now + 2.2f;
                                botH.Stance.NoProneUntil = now + 2.5f;
                                yielded = true;
                            }

                            if (AiCore.CanBotMoveAutonomously(bot))
                            {
                                Vector3 clearDir = Mathf.Abs(sLat) > 0.08f
                                    ? (sLat >= 0f ? lane.RightFlat : -lane.RightFlat)
                                    : (((AiCore.GetDeterministicHash(botPtr, 97) & 1) == 0) ? lane.RightFlat : -lane.RightFlat);

                                bool isMovingNow = botAi.IsMoving();
                                if (isMovingNow && fwd <= 4.5f)
                                {
                                    Vector3 toDest = botAi.MoveDestination - botPos;
                                    toDest.y = 0f;
                                    float destSideDot = Vector3.Dot(toDest, lane.RightFlat);
                                    Vector3 destSide = Mathf.Abs(destSideDot) > 0.1f
                                        ? (destSideDot >= 0f ? lane.RightFlat : -lane.RightFlat)
                                        : clearDir;
                                    Vector3 behindTarget = lane.FeetPos - lane.AimDirFlat * 1.65f + destSide * 1.05f;

                                    if (!AiCore.RaycastEnvironment(lane.FeetPos + Vector3.up * 0.70f, -lane.AimDirFlat, 1.75f, lane.RootTr, out _) &&
                                        TryGetSafeYieldPoint(botPos, botH.RootTr, isOnBridge, behindTarget, out Vector3 safeBehind))
                                    {
                                        AiCore.MoveBotTowardPosition(botAi, botAi.character, safeBehind, 0f, 1.15f);
                                        botH.Combat.NextFriendlyStepAsideTime = now + 1.85f;
                                        PlayerCrosshairYieldsCount++;
                                        FriendlyFirePrevented++;
                                        continue;
                                    }
                                }

                                if (TryFindClearLateralYieldStep(botPos, botH.RootTr, isOnBridge, clearDir, in lane, fwd, out Vector3 stepPos))
                                {
                                    if (!isMovingNow)
                                    {
                                        bool canVacateCover = !bot.IsOnCover() ||
                                                              (lane.IsAdsOrFiring &&
                                                               fwd <= 4.8f &&
                                                               !HasActiveGoapCoverLock(botPtr, now) &&
                                                               !IsActivelyHeadingToEmplacement(bot, now));
                                        if (canVacateCover)
                                        {
                                            if (bot.IsOnCover() || bot.HasDestinationAssigned)
                                            {
                                                DestinationWithoutCover yieldDest = new DestinationWithoutCover(stepPos, SoldierPose.Crouch);
                                                botH.Stance.LocalCoverProtectedUntil = now + 4.5f;
                                                bot.CoverPosition(yieldDest.Cast<AiDestination>());
                                            }
                                            AiCore.MoveBotTowardPosition(botAi, bot, stepPos, 0f, 0.95f);
                                            if (lane.IsAdsOrFiring && fwd <= 6.5f)
                                            {
                                                TrySayTacticalVoice(bot, bot.joinedSquad, VoiceManager.VoiceClip.watchYourFire, 0.04f);
                                            }
                                            yielded = true;
                                        }
                                    }
                                    else
                                    {
                                        if ((botAi.MoveDestination - botPos).sqrMagnitude <= 10.0f * 10.0f)
                                        {
                                            if (botAi.moveDirectlyToward_end > now)
                                                botAi.moveDirectlyToward_end = 0f;
                                            botAi.MoveDestination += (stepPos - botPos);
                                        }
                                        AiCore.MoveBotTowardPosition(botAi, bot, stepPos, 0f, 0.75f);
                                        yielded = true;
                                    }
                                }
                            }

                            if (yielded)
                            {
                                botH.Combat.NextFriendlyStepAsideTime = now + 1.55f;
                                PlayerCrosshairYieldsCount++;
                                FriendlyFirePrevented++;
                            }
                        }
                        else if (lane.IsAdsOrFiring &&
                                 botAi.IsMoving() &&
                                 fwd >= -0.60f &&
                                 fwd <= 7.80f &&
                                 Mathf.Abs(sLat) <= 4.20f)
                        {
                            // Moving bot outside the corridor: check if its immediate travel vector is about to cross the player's ADS sightline
                            Vector3 toDest = botAi.MoveDestination - botPos;
                            toDest.y = 0f;
                            float toDestMag = toDest.magnitude;
                            if (toDestMag < 0.80f)
                                continue;

                            float lookLen = Mathf.Min(toDestMag, 5.50f);
                            Vector3 endPt = botPos + (toDest / toDestMag) * lookLen;
                            float endDx = endPt.x - lane.AimOrigin.x;
                            float endDz = endPt.z - lane.AimOrigin.z;
                            float endFwd = endDx * lane.AimDirFlat.x + endDz * lane.AimDirFlat.z;
                            float endSLat = endDx * lane.RightFlat.x + endDz * lane.RightFlat.z;

                            bool crossesRay = (sLat * endSLat <= 0f) ||
                                              (Mathf.Abs(endSLat) <= lane.HalfCorridorWidth * 0.85f &&
                                               Mathf.Abs(endSLat) < Mathf.Abs(sLat) - 0.35f);
                            if (!crossesRay)
                                continue;

                            float t = Mathf.Clamp01(Mathf.Abs(sLat) / Mathf.Max(Mathf.Abs(sLat - endSLat), 0.01f));
                            float crossFwd = Mathf.Lerp(fwd, endFwd, t);
                            if (crossFwd < 0.35f || crossFwd > 7.20f)
                                continue;

                            float crossRayY = lane.AimOrigin.y + lane.AimDir3D.y * crossFwd;
                            if (crossRayY > botPos.y + 2.35f || crossRayY < botPos.y - 0.55f)
                                continue;

                            if (crossFwd > 1.0f &&
                                AiCore.RaycastEnvironment(lane.AimOrigin, lane.AimDir3D, crossFwd - 0.20f, lane.RootTr, out _))
                            {
                                continue;
                            }

                            bool isOnBridge = checkBridges && AiCore.IsOnBridgeOrNarrowCrossing(botPos);
                            if (crossFwd <= 4.50f && AiCore.CanBotMoveAutonomously(bot))
                            {
                                Vector3 destSide = (endSLat >= sLat) ? lane.RightFlat : -lane.RightFlat;
                                Vector3 passBehind = lane.FeetPos - lane.AimDirFlat * 1.65f + destSide * 1.05f;
                                if (!AiCore.RaycastEnvironment(lane.FeetPos + Vector3.up * 0.70f, -lane.AimDirFlat, 1.75f, lane.RootTr, out _) &&
                                    TryGetSafeYieldPoint(botPos, botH.RootTr, isOnBridge, passBehind, out Vector3 safeBehind))
                                {
                                    AiCore.MoveBotTowardPosition(botAi, botAi.character, safeBehind, 0f, 1.20f);
                                    botH.Combat.NextFriendlyStepAsideTime = now + 2.10f;
                                    PlayerCrosshairYieldsCount++;
                                    FriendlyFirePrevented++;
                                    continue;
                                }
                            }

                            if (!lane.IsProne && AiCore.CanBotChangePoseAutonomously(bot))
                            {
                                SetBotPose(botAi, bot, SoldierPose.Crouch, true);
                                botH.Stance.CombatCrouchLockUntil = now + 2.0f;
                                botH.Stance.NoProneUntil = now + 2.5f;
                                botH.Combat.NextFriendlyStepAsideTime = now + 1.60f;
                                PlayerCrosshairYieldsCount++;
                                FriendlyFirePrevented++;
                            }
                        }
                    }
                }
            }
        }

        private static bool TryGetAntiStackPush(
            Soldier shooter,
            AiCore.SoldierFastHandles h,
            Vector3 myPos,
            out Vector3 pushDir,
            out float pushDist,
            out bool isIndoors)
        {
            pushDir = Vector3.zero;
            pushDist = 0f;
            isIndoors = false;

            float configuredSpace = VisionConfig.MinFriendlySpacingMeters.Value;
            if (configuredSpace <= 0.2f || shooter == null || h == null)
                return false;

            float outerSpace = Mathf.Clamp(configuredSpace, 0.5f, 3.2f);
            if (GetPersonality(shooter) == SoldierPersonality.Assaulter_Skirmisher)
                outerSpace = Mathf.Min(outerSpace * 1.15f, 3.5f);

            float outerSpaceSq = outerSpace * outerSpace;
            float effectiveSpace = outerSpace;
            float effectiveSpaceSq = outerSpaceSq;
            bool checkedIndoors = false;

            IntPtr myPtr = h.Ptr;
            string myFaction = AiCore.GetCachedFaction(h, Time.time);

            AiCore.EnsureWorldSnapshot();
            int wCount = AiCore.WorldSoldierCount;
            Vector3 accum = Vector3.zero;
            float nearestDist = float.MaxValue;
            int crowdCount = 0;

            for (int i = 0; i < wCount; i++)
            {
                ref AiCore.SoldierSnapshot s = ref AiCore.WorldSoldiers[i];
                if (s.Ptr == IntPtr.Zero || s.Ptr == myPtr || s.IsOnVehicle || s.HasSurrendered || s.IsInjured)
                    continue;

                if (!AiCore.AreSameSideFactions(myFaction, s.Faction))
                    continue;

                if (Mathf.Abs(s.Pos.y - myPos.y) > 1.8f)
                    continue;

                Vector3 diff = myPos - s.Pos;
                diff.y = 0f;
                float dSq = diff.sqrMagnitude;
                if (dSq >= outerSpaceSq)
                    continue;

                if (!checkedIndoors)
                {
                    checkedIndoors = true;
                    isIndoors = AiCore.HasLowCeilingOverhead(myPos, 5.5f);
                    effectiveSpace = isIndoors ? Mathf.Min(outerSpace, 1.35f) : outerSpace;
                    effectiveSpaceSq = effectiveSpace * effectiveSpace;
                }

                if (dSq >= effectiveSpaceSq)
                    continue;

                // Deterministic tiebreaker so only one overlapping bot steps aside; bots always yield to human players.
                if (!s.IsPlayer && myPtr.ToInt64() < s.Ptr.ToInt64())
                    continue;

                float d = Mathf.Sqrt(dSq);
                Vector3 away = d > 0.05f
                    ? (diff / d)
                    : ((myPtr.ToInt64() & 1) == 0 ? shooter.transform.right : -shooter.transform.right);
                float weight = (effectiveSpace - d) / Mathf.Max(0.25f, effectiveSpace);
                accum += away * (0.35f + weight);
                if (d < nearestDist)
                    nearestDist = d;
                crowdCount++;
            }

            if (wCount == 0)
            {
                Soldier player = Soldier.CurrentControlledSoldierOrNull();
                if (player != null &&
                    player.IsAlive &&
                    player.Pointer != myPtr &&
                    AiCore.AreSameSideFactions(myFaction, player.faction) &&
                    !player.IsOnVehicle() &&
                    player.transform != null)
                {
                    Vector3 pPos = player.transform.position;
                    if (Mathf.Abs(pPos.y - myPos.y) <= 1.8f)
                    {
                        Vector3 diff = myPos - pPos;
                        diff.y = 0f;
                        float dSq = diff.sqrMagnitude;
                        if (dSq < outerSpaceSq)
                        {
                            if (!checkedIndoors)
                            {
                                checkedIndoors = true;
                                isIndoors = AiCore.HasLowCeilingOverhead(myPos, 5.5f);
                                effectiveSpace = isIndoors ? Mathf.Min(outerSpace, 1.35f) : outerSpace;
                                effectiveSpaceSq = effectiveSpace * effectiveSpace;
                            }

                            if (dSq < effectiveSpaceSq)
                            {
                                float d = Mathf.Sqrt(dSq);
                                accum += d > 0.05f ? (diff / d) : shooter.transform.right;
                                nearestDist = d;
                                crowdCount = 1;
                            }
                        }
                    }
                }
            }

            if (crowdCount == 0 || accum.sqrMagnitude < 0.0004f)
                return false;

            pushDir = accum.normalized;
            pushDist = Mathf.Clamp((effectiveSpace - nearestDist) + 0.50f, 1.15f, 2.35f);
            return true;
        }

        private static bool TryApplyAntiStackSpacingStep(
            SoldierAI ai,
            Soldier soldier,
            AiCore.SoldierFastHandles h,
            Vector3 myPos,
            bool isOnBridge,
            float now,
            float missCooldown)
        {
            if (!VisionConfig.EnableAntiStacking.Value ||
                ai == null ||
                soldier == null ||
                h == null ||
                ai.IsMoving() ||
                isOnBridge ||
                soldier.m_pose == SoldierPose.Prone ||
                soldier.IsOnVehicle() ||
                soldier.IsOnCover() ||
                soldier.IsTowardWall() ||
                soldier.UsingBipod() ||
                soldier.isBleeding ||
                soldier.isInBodyAnimation ||
                !AiCore.CanBotMoveAutonomously(soldier))
            {
                return false;
            }

            if (now < h.Combat.NextAntiStackTime)
                return false;

            h.Combat.NextAntiStackTime = now + missCooldown;
            if (!TryGetAntiStackPush(soldier, h, myPos, out Vector3 pushDir, out float pushDist, out bool isIndoors))
                return false;

            Vector3 pushTarget = myPos + pushDir * pushDist;
            if (!AiCore.IsMovementBlockedByObstacle(myPos + Vector3.up * 0.48f, pushDir, pushDist + 0.30f) &&
                Physics.Raycast(pushTarget + Vector3.up * 0.90f, Vector3.down, out RaycastHit gHit, 2.2f, AiCore.GetSolidEnvironmentMask(), QueryTriggerInteraction.Ignore) &&
                gHit.normal.y >= 0.65f &&
                Mathf.Abs(gHit.point.y - myPos.y) <= 0.95f &&
                (!isIndoors || AiCore.HasLowCeilingOverhead(gHit.point, 5.5f)))
            {
                ai.MoveDirectlyToward(gHit.point, 0.85f);
                h.Combat.NextAntiStackTime = now + 8.0f;
                AntiStackNudges++;
                return true;
            }

            return false;
        }
    }
}
