using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace EasyRed2HumanAI
{
    public static partial class VeteranAndTacticsPatches
    {
        private static readonly Dictionary<IntPtr, float> _stanceDropHoldFireUntil = new Dictionary<IntPtr, float>(256);
        private static readonly Dictionary<IntPtr, float> _nextStanceDropAllowed = new Dictionary<IntPtr, float>(256);
        private static readonly Dictionary<IntPtr, float> _combatStanceHoldUntil = new Dictionary<IntPtr, float>(256);
        private static readonly Dictionary<IntPtr, float> _combatCrouchLockUntil = new Dictionary<IntPtr, float>(256);
        private static readonly Dictionary<IntPtr, float> _noProneUntil = new Dictionary<IntPtr, float>(256);
        private static readonly Dictionary<IntPtr, float> _nextWallCheckTime = new Dictionary<IntPtr, float>(256);
        private static readonly Dictionary<IntPtr, float> _wallBlockedUntil = new Dictionary<IntPtr, float>(256);
        private static readonly Dictionary<IntPtr, float> _peekRestoreTime = new Dictionary<IntPtr, float>(256);
        private static readonly Dictionary<IntPtr, float> _nextPeekAllowedTime = new Dictionary<IntPtr, float>(256);
        private static readonly Dictionary<IntPtr, float> _nextFFCheckTime = new Dictionary<IntPtr, float>(256);
        private static readonly Dictionary<IntPtr, float> _ffBlockedUntil = new Dictionary<IntPtr, float>(256);
        private static readonly Dictionary<IntPtr, float> _nextAntiStackTime = new Dictionary<IntPtr, float>(256);
        private static readonly Dictionary<IntPtr, float> _nextVaultCheckTime = new Dictionary<IntPtr, float>(256);
        private static readonly Dictionary<IntPtr, float> _nextLiberateCheckTime = new Dictionary<IntPtr, float>(256);
        private static readonly Dictionary<IntPtr, float> _liberateMoveStartTime = new Dictionary<IntPtr, float>(128);
        private static readonly Dictionary<IntPtr, float> _liberateMoveCooldownUntil = new Dictionary<IntPtr, float>(128);
        private static readonly Dictionary<IntPtr, float> _bleedCoverDeadline = new Dictionary<IntPtr, float>(128);
        private static readonly Dictionary<IntPtr, float> _nextBipodSeekTime = new Dictionary<IntPtr, float>(128);
        private static readonly Dictionary<IntPtr, float> _nextBipodDeployCheckTime = new Dictionary<IntPtr, float>(128);
        private static readonly Dictionary<IntPtr, float> _bipodStandUntil = new Dictionary<IntPtr, float>(128);
        private static readonly Dictionary<IntPtr, float> _nextWallSideCheckTime = new Dictionary<IntPtr, float>(256);
        private static readonly Dictionary<IntPtr, float> _nextWallSidestepTime = new Dictionary<IntPtr, float>(256);
        private static readonly Dictionary<IntPtr, float> _poseLockUntil = new Dictionary<IntPtr, float>(256);
        private static readonly Dictionary<IntPtr, float> _nextBayonetCheckTime = new Dictionary<IntPtr, float>(256);
        private static readonly Dictionary<IntPtr, float> _nextMeleeAttemptTime = new Dictionary<IntPtr, float>(256);
        private static readonly Dictionary<IntPtr, float> _nextMeleeStepTime = new Dictionary<IntPtr, float>(256);

        private struct MeleeTargetSlotState
        {
            public IntPtr Attacker0;
            public float Expire0;
            public IntPtr Attacker1;
            public float Expire1;
        }

        private static readonly Dictionary<IntPtr, MeleeTargetSlotState> _meleeTargetSlots = new Dictionary<IntPtr, MeleeTargetSlotState>(64);
        private static readonly Dictionary<IntPtr, float> _nextIndoorCeilingCountTime = new Dictionary<IntPtr, float>(256);
        private static readonly Dictionary<IntPtr, float> _nextBoundingHoldCountTime = new Dictionary<IntPtr, float>(256);
        private static readonly Dictionary<IntPtr, float> _nextBridgeAssistCountTime = new Dictionary<IntPtr, float>(256);
        private static readonly Dictionary<IntPtr, float> _nextSurrenderCheckTime = new Dictionary<IntPtr, float>(256);
        private static readonly Dictionary<IntPtr, float> _nextLocalCoverSeekTime = new Dictionary<IntPtr, float>(256);
        private static readonly Dictionary<IntPtr, float> _localCoverProtectedUntil = new Dictionary<IntPtr, float>(256);
        internal static readonly Dictionary<IntPtr, float> _nextStandCrouchLofCheckTime = new Dictionary<IntPtr, float>(256);

        internal struct FireAndMovementState
        {
            public bool InFiringHalt;
            public float PhaseStartTime;
            public float PhaseEndTime;
            public float HaltLostSightTime;
            public Vector3 AnchorMoveDest;
        }

        internal static readonly Dictionary<IntPtr, FireAndMovementState> _fireAndMovementStates = new Dictionary<IntPtr, FireAndMovementState>(256);

        internal struct RotateAndFireFrameState
        {
            public int Frame;
            public bool BlockFire;
            public bool WallBlocked;
            public bool InPointBlankMelee;
            public bool IsMoving;
            public bool IsPeeking;
            public bool HasCustomTarget;
            public Vector3 CustomTargetPos;
        }

        internal struct InfantrySightReactionState
        {
            public IntPtr LastTargetPtr;
            public float LastSeenTime;
        }

        internal static readonly Dictionary<IntPtr, RotateAndFireFrameState> _rotateFireFrameCache = new Dictionary<IntPtr, RotateAndFireFrameState>(256);
        internal static readonly Dictionary<IntPtr, InfantrySightReactionState> _infantryTargetReaction = new Dictionary<IntPtr, InfantrySightReactionState>(256);
        private static readonly HashSet<IntPtr> _bayonetCheckedSoldiers = new HashSet<IntPtr>();
        private static readonly Dictionary<long, bool> _coverExposureCache = new Dictionary<long, bool>(1024);
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
        private static readonly Dictionary<IntPtr, float> _nextBotDoorCheckTime = new Dictionary<IntPtr, float>(256);
        public static int DoorsStrategicallyClosedCount { get; private set; }
        public static int TankHullCoverUsesCount { get; private set; }
        public static int PlayerCrosshairYieldsCount { get; private set; }
        public static int UnsafeBandagesBlockedCount { get; private set; }
        public static int CloseGunfireMeleeSuppressedCount { get; private set; }
        public static int SlopedVaultsRejectedCount { get; private set; }
        private static readonly Dictionary<IntPtr, float> _nextBandageBlockCountTime = new Dictionary<IntPtr, float>(128);
        private static readonly Dictionary<IntPtr, float> _nextMeleeSuppressCountTime = new Dictionary<IntPtr, float>(128);

        public static void ClearDoorCaches(bool forceFull = false)
        {
            if (forceFull || _colliderDoorCache.Count > 512)
                _colliderDoorCache.Clear();
            if (forceFull || _nextDoorToggleTime.Count > 256)
                _nextDoorToggleTime.Clear();
            if (forceFull || _liberateMoveStartTime.Count > 256)
                _liberateMoveStartTime.Clear();
            if (forceFull || _liberateMoveCooldownUntil.Count > 256)
                _liberateMoveCooldownUntil.Clear();
            if (forceFull || _meleeTargetSlots.Count > 128)
                _meleeTargetSlots.Clear();
            if (forceFull || _staticCoverPosCache.Count > 4096)
                _staticCoverPosCache.Clear();
            if (forceFull || _nextBandageBlockCountTime.Count > 256)
                _nextBandageBlockCountTime.Clear();
            if (forceFull || _nextMeleeSuppressCountTime.Count > 256)
                _nextMeleeSuppressCountTime.Clear();
            _nextPlayerCrosshairYieldPassTime = 0f;
        }

        // =====================================================================
        // Muzzle Obstruction, Friendly Fire & Anti-Stacking Helpers
        // =====================================================================
        private static bool IsMuzzleBlockedByWall(Soldier shooter, Vector3 targetPos, float distToTarget)
        {
            if (shooter == null || distToTarget <= 1.2f)
                return false;

            if (AiCore.HorizontalAngleToDirection(shooter, targetPos - shooter.transform.position) > 35f)
                return false;

            if (shooter.IsTowardWall())
                return true;

            Transform myRoot = shooter.transform.root;
            Vector3 soldierPos = shooter.transform.position;
            float barrelH = AiCore.GetBarrelHeightForPose(shooter.m_pose);

            Vector3 spineAtBarrel = soldierPos + Vector3.up * barrelH;
            Vector3 toTargetFromSpine = targetPos - spineAtBarrel;
            if (toTargetFromSpine.sqrMagnitude > 0.04f)
            {
                Vector3 spineAimDir = toTargetFromSpine.normalized;
                float spineMaxCheck = Mathf.Min(VisionConfig.MuzzleObstructionCheckDist.Value + 0.65f, distToTarget - 0.35f);
                if (spineMaxCheck > 0.25f)
                {
                    Vector3 spineStart = spineAtBarrel - spineAimDir * 0.10f;
                    if (AiCore.RaycastEnvironment(spineStart, spineAimDir, spineMaxCheck, myRoot, out _))
                        return true;

                    if (AiCore.RaycastEnvironment(spineStart - Vector3.up * 0.06f, spineAimDir, Mathf.Min(spineMaxCheck, 2.0f), myRoot, out _))
                        return true;
                }
            }

            if (!shooter.isInBodyAnimation && !shooter.IsReloading)
            {
                Vector3 gunPos = shooter.GunPosition();
                GenericGun heldGun = shooter.GetHeldGun();
                Vector3 receiverPos = (heldGun != null && heldGun.transform != null) ? heldGun.transform.position : gunPos;

                float minLiveY = soldierPos.y + barrelH - 0.12f;
                if (receiverPos.y < minLiveY) receiverPos.y = minLiveY;
                if (gunPos.y < minLiveY) gunPos.y = minLiveY;

                Vector3 toTarget = targetPos - receiverPos;
                Vector3 aimDir = toTarget.sqrMagnitude > 0.01f ? toTarget.normalized : shooter.GetFireDir();

                float maxCheck = Mathf.Min(VisionConfig.MuzzleObstructionCheckDist.Value + 0.35f, distToTarget - 0.35f);
                if (maxCheck > 0.25f)
                {
                    if (AiCore.RaycastEnvironment(receiverPos - aimDir * 0.10f, aimDir, maxCheck, myRoot, out _))
                        return true;

                    if (AiCore.RaycastEnvironment(gunPos - aimDir * 0.15f, aimDir, maxCheck, myRoot, out _))
                        return true;

                    if (AiCore.RaycastEnvironment(gunPos - Vector3.up * 0.06f - aimDir * 0.10f, aimDir, Mathf.Min(maxCheck, 1.85f), myRoot, out _))
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

            IntPtr friendPtr = friend.Pointer;
            float now = Time.time;
            if (_nextFriendlyStepAsideTime.TryGetValue(friendPtr, out float nextStep) && now < nextStep)
                return;

            _nextFriendlyStepAsideTime[friendPtr] = now + 1.8f;

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
                friendAi.MoveDestination = friendPos + stepDir * 1.85f;
                TrySayTacticalVoice(friend, friend.joinedSquad, VoiceManager.VoiceClip.watchYourFire, 0.05f);
            }
            else if ((friendAi.MoveDestination - friendPos).sqrMagnitude <= 9.0f * 9.0f)
            {
                friendAi.MoveDestination += stepDir * 1.65f;
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
            if (AiCore.RaycastEnvironment(originPos + Vector3.up * 0.65f, dir, Mathf.Min(dist + 0.25f, 3.8f), ignoreRoot, out _))
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

                        if (_nextFriendlyStepAsideTime.TryGetValue(uSnap.Ptr, out float nextYield) && now < nextYield)
                            continue;

                        Soldier bot = uSnap.Ref;
                        AiCore.SoldierFastHandles botH = AiCore.GetOrCreateSoldierHandles(bot);
                        if (!AiCore.IsLocallyOwnedBot(botH) ||
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
                                _combatCrouchLockUntil[botPtr] = now + 2.2f;
                                _noProneUntil[botPtr] = now + 2.5f;
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
                                        botAi.MoveDirectlyToward(safeBehind, 1.15f);
                                        _nextFriendlyStepAsideTime[botPtr] = now + 1.85f;
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
                                                               fwd <= 8.5f &&
                                                               !HasActiveGoapCoverLock(botPtr, now) &&
                                                               !IsActivelyHeadingToEmplacement(bot, now));
                                        if (canVacateCover)
                                        {
                                            if (bot.IsOnCover() || bot.HasDestinationAssigned)
                                            {
                                                DestinationWithoutCover yieldDest = new DestinationWithoutCover(stepPos, SoldierPose.Crouch);
                                                _localCoverProtectedUntil[botPtr] = now + 4.5f;
                                                bot.CoverPosition(yieldDest.Cast<AiDestination>());
                                            }
                                            botAi.MoveDestination = stepPos;
                                            botAi.MoveDirectlyToward(stepPos, 0.95f);
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
                                            botAi.MoveDestination += (stepPos - botPos);
                                        botAi.MoveDirectlyToward(stepPos, 0.75f);
                                        yielded = true;
                                    }
                                }
                            }

                            if (yielded)
                            {
                                _nextFriendlyStepAsideTime[botPtr] = now + 1.55f;
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
                                    botAi.MoveDirectlyToward(safeBehind, 1.20f);
                                    _nextFriendlyStepAsideTime[botPtr] = now + 2.10f;
                                    PlayerCrosshairYieldsCount++;
                                    FriendlyFirePrevented++;
                                    continue;
                                }
                            }

                            if (!lane.IsProne && AiCore.CanBotChangePoseAutonomously(bot))
                            {
                                SetBotPose(botAi, bot, SoldierPose.Crouch, true);
                                _combatCrouchLockUntil[botPtr] = now + 2.0f;
                                _noProneUntil[botPtr] = now + 2.5f;
                                _nextFriendlyStepAsideTime[botPtr] = now + 1.60f;
                                PlayerCrosshairYieldsCount++;
                                FriendlyFirePrevented++;
                            }
                        }
                    }
                }
            }
        }

        private static bool TryGetAntiStackPush(Soldier shooter, out Vector3 pushDir)
        {
            pushDir = Vector3.zero;
            float configuredSpace = VisionConfig.MinFriendlySpacingMeters.Value;
            if (configuredSpace <= 0.2f) return false;

            float minSpace = Mathf.Min(configuredSpace, 1.25f);
            if (GetPersonality(shooter) == SoldierPersonality.Assaulter_Skirmisher)
                minSpace = Mathf.Min(configuredSpace * 1.25f, 1.65f);

            float minSpaceSq = minSpace * minSpace;
            Vector3 myPos = shooter.transform.position;
            IntPtr myPtr = shooter.Pointer;
            string myFaction = shooter.faction;

            Squad mySquad = shooter.joinedSquad;
            if (mySquad != null && mySquad.units != null)
            {
                var units = mySquad.units;
                int c = units.Count;
                for (int i = 0; i < c; i++)
                {
                    Soldier f = units[i];
                    if (f == null || !f.IsAlive || f.Pointer == myPtr || f.IsOnVehicle()) continue;
                    if (myPtr.ToInt64() < f.Pointer.ToInt64()) continue;

                    Vector3 diff = myPos - f.transform.position;
                    diff.y = 0f;
                    float dSq = diff.sqrMagnitude;
                    if (dSq < minSpaceSq)
                    {
                        if (dSq > 0.01f)
                            pushDir = diff.normalized;
                        else
                            pushDir = (myPtr.ToInt64() & 1) == 0 ? shooter.transform.right : -shooter.transform.right;
                        return true;
                    }
                }
            }

            Soldier player = Soldier.CurrentControlledSoldierOrNull();
            if (player != null && player.IsAlive && player.Pointer != myPtr && player.faction == myFaction && !player.IsOnVehicle())
            {
                Vector3 diff = myPos - player.transform.position;
                diff.y = 0f;
                float dSq = diff.sqrMagnitude;
                if (dSq < minSpaceSq)
                {
                    pushDir = dSq > 0.01f ? diff.normalized : shooter.transform.right;
                    return true;
                }
            }

            return false;
        }

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
                    if (!_combatCrouchLockUntil.TryGetValue(soldier.Pointer, out float cur) || lockUntil > cur)
                        _combatCrouchLockUntil[soldier.Pointer] = lockUntil;
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

            IntPtr ptr = soldier.Pointer;

            // Indoor Ceiling & Bridge Prone Prevention: prevent dropping Prone under windowsills/stairs or on bridge decks
            if (pose == SoldierPose.Prone && !(soldier.isBleeding || soldier.GetBleedValue() > 0.01f))
            {
                Vector3 feetPos = soldier.transform.position;
                if (VisionConfig.EnableIndoorCeilingPronePrevention.Value &&
                    AiCore.HasLowCeilingOverhead(feetPos, 3.0f))
                {
                    pose = SoldierPose.Crouch;
                    if (ptr != IntPtr.Zero)
                    {
                        _noProneUntil[ptr] = Time.time + 10.0f;
                        if (!_nextIndoorCeilingCountTime.TryGetValue(ptr, out float nextCount) || Time.time >= nextCount)
                        {
                            _nextIndoorCeilingCountTime[ptr] = Time.time + 5.0f;
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
                    if (ptr != IntPtr.Zero)
                    {
                        _noProneUntil[ptr] = Time.time + 4.0f;
                    }
                }
            }

            if (pose >= SoldierPose.Crouch &&
                !ai.IsMoving() &&
                (ai.HasEnemyAndCanFireAtHim || soldier.GetSuppressionValue() >= 8f))
            {
                if (ptr != IntPtr.Zero)
                {
                    float lockUntil = Time.time + 3.5f;
                    if (!_combatCrouchLockUntil.TryGetValue(ptr, out float cur) || lockUntil > cur)
                    {
                        _combatCrouchLockUntil[ptr] = lockUntil;
                    }
                }
            }

            if (soldier.m_pose == pose)
            {
                ai.actualPose = pose;
                return;
            }

            bool isUrgentTransition = forceImmediate ||
                                      (soldier.m_pose != SoldierPose.Idle && pose == SoldierPose.Idle && ai.IsMoving()) ||
                                      (soldier.m_pose == SoldierPose.Prone && ai.IsMoving()) ||
                                      (soldier.m_pose == SoldierPose.Idle && pose >= SoldierPose.Crouch && !ai.IsMoving() &&
                                       (ai.HasEnemyAndCanFireAtHim || soldier.GetSuppressionValue() >= 15f));

            if (!isUrgentTransition && ptr != IntPtr.Zero)
            {
                if (soldier.isInBodyAnimation ||
                    (_poseLockUntil.TryGetValue(ptr, out float poseLock) && Time.time < poseLock))
                {
                    ai.actualPose = soldier.m_pose;
                    return;
                }
            }

            if (ptr != IntPtr.Zero)
            {
                _poseLockUntil[ptr] = Time.time + 2.8f;
            }

            ai.actualPose = pose;
            soldier.SetPose(pose, true, false, 0.2f);
        }

        internal static Vector3 GetCachedMoveEndpoint(AiCore.SoldierFastHandles h, SoldierAI ai, Soldier soldier, out int nodesCount)
        {
            int frame = Time.frameCount;
            if (h != null && h.LastEndpointFrame == frame)
            {
                nodesCount = h.CachedNodesCount;
                return h.CachedEndpoint;
            }

            Vector3 ep;
            int nc = -1;
            var p = ai.path;
            if (!ReferenceEquals(p, null) && p.Pointer != IntPtr.Zero)
            {
                ep = p.Destination;
                nc = p.NodesCount;
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
            return soldierPtr != IntPtr.Zero &&
                   _fireAndMovementStates.TryGetValue(soldierPtr, out FireAndMovementState st) &&
                   st.InFiringHalt &&
                   now < st.PhaseEndTime;
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
            return ptr != IntPtr.Zero &&
                   _nextFriendlyStepAsideTime.TryGetValue(ptr, out float stepUntil) &&
                   now < stepUntil &&
                   _combatCrouchLockUntil.TryGetValue(ptr, out float crouchUntil) &&
                   now < crouchUntil;
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
                if (_vehicleAimCache.TryGetValue(soldierPtr, out VehicleAimCache vc) && now < vc.ExpireTime)
                    return !vc.CanEngage;

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

            IntPtr ptr = h.Ptr;
            float now = Time.time;
            if (!_nextLocalCoverSeekTime.TryGetValue(ptr, out float nextSeek) || now >= nextSeek)
            {
                _nextLocalCoverSeekTime[ptr] = now + 1.5f;

                Vector3 myPos = h.Tr.position;
                string myFaction = AiCore.GetCachedFaction(h, now);
                bool isOnBridge = VisionConfig.EnableBridgeCrossingDiscipline.Value &&
                                  AiCore.IsOnBridgeOrNarrowCrossing(myPos);
                Vector3 threatDir = GetEffectiveThreatDirection(soldier, soldier.joinedSquad, myPos, h.Tr.forward);
                CombatCover bestCover = null;
                float bestDistSq = float.MaxValue;

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
                            if (dSq >= bestDistSq)
                                continue;

                            if (isOnBridge && !AiCore.IsSafeBridgeDeckStep(myPos, covPos, out _))
                                continue;

                            if (!cc.IsCoverAvailable(threatDir, myFaction))
                                continue;

                            bestDistSq = dSq;
                            bestCover = cc;
                        }
                    }
                }

                // Consider nearby idle/wrecked tank or armored vehicle hulls as physical cover if closer than static cover!
                if (bestDistSq > 7.0f * 7.0f &&
                    TryFindTankHullCoverOrEscortSpot(soldier, myPos, threatDir, false, out Vector3 tankCoverPos, out _) &&
                    (!VisionConfig.EnableFriendlyFireAvoidance.Value || !AiCore.IsCoverInPlayerAimLane(tankCoverPos, threatDir)) &&
                    (!isOnBridge || AiCore.IsSafeBridgeDeckStep(myPos, tankCoverPos, out tankCoverPos)))
                {
                    float tankDistSq = (tankCoverPos - myPos).sqrMagnitude;
                    if (tankDistSq < bestDistSq)
                    {
                        DestinationWithoutCover tankDest = new DestinationWithoutCover(tankCoverPos, SoldierPose.Crouch);
                        _localCoverProtectedUntil[ptr] = now + 6.5f;
                        soldier.CoverPosition(tankDest.Cast<AiDestination>());
                        TankHullCoverUsesCount++;
                        return true;
                    }
                }

                if (bestCover != null)
                {
                    _localCoverProtectedUntil[ptr] = now + 7.5f;
                    soldier.CoverPosition(bestCover.Cast<AiDestination>());
                    return true;
                }

                // Bridge Parapet / Balustrade / Pillar wall-hug fallback when static cover nodes weren't baked onto the span
                if (isOnBridge &&
                    TryFindBridgeParapetOrPillarSpot(soldier, myPos, threatDir, out Vector3 parapetSpot) &&
                    (!VisionConfig.EnableFriendlyFireAvoidance.Value || !AiCore.IsCoverInPlayerAimLane(parapetSpot, threatDir)))
                {
                    DestinationWithoutCover bridgeDest = new DestinationWithoutCover(parapetSpot, SoldierPose.Crouch);
                    _localCoverProtectedUntil[ptr] = now + 6.5f;
                    soldier.CoverPosition(bridgeDest.Cast<AiDestination>());
                    return true;
                }
            }

            if (soldier.HasDestinationAssigned)
            {
                soldier.TryToGetToActualCover();
                return soldier.IsOnCover() || ai.IsMoving();
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

            IntPtr ptr = soldier.Pointer;
            if (_nextWallSidestepTime.TryGetValue(ptr, out float nextSide) && Time.time < nextSide)
                return false;

            _nextWallSidestepTime[ptr] = Time.time + 3.2f;

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
                        _localCoverProtectedUntil[ptr] = Time.time + 6.5f;
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
                        _nextWallSidestepTime[ptr] = Time.time + 4.5f;
                        AntiStackNudges++;
                        return true;
                    }
                }
            }

            return false;
        }

        // =====================================================================
        // Smart Wall-Side Cover & Threat Direction (Stand BEHIND Walls)
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
                    return bestToEnemy.normalized;
            }

            if (VisionConfig.EnableThreatSectorConcentration.Value &&
                soldier != null &&
                TryGetKnownThreatSectorDirection(soldier, out Vector3 sectorDir, out bool hasActiveContact) &&
                hasActiveContact &&
                sectorDir.sqrMagnitude > 0.01f)
            {
                return sectorDir;
            }

            string faction = soldier != null ? soldier.faction : (squad != null && squad.Leader != null ? squad.Leader.faction : null);
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

            return AiCore.IsDirectionBlockedByEnvironment(pos + Vector3.up * 0.50f, threatDir, 1.85f, ignoreRoot) ||
                   AiCore.IsDirectionBlockedByEnvironment(pos + Vector3.up * 0.88f, threatDir, 1.85f, ignoreRoot);
        }

        private static bool IsPositionOnExposedSideOfWall(Vector3 pos, Vector3 threatDir, Transform ignoreRoot, out RaycastHit rearWallHit)
        {
            rearWallHit = default;
            if (threatDir.sqrMagnitude < 0.001f)
                return false;

            if (IsPositionShelteredByFrontCover(pos, threatDir, ignoreRoot))
                return false;

            // Never mistake the interior back wall of a room, bunker, or pillbox for an exposed exterior wall face
            if (AiCore.HasLowCeilingOverhead(pos, 3.5f) ||
                AiCore.IsDirectionBlockedByEnvironment(pos + Vector3.up * 0.65f, threatDir, 4.5f, ignoreRoot))
            {
                return false;
            }

            Vector3 rearOrigin = pos + Vector3.up * 0.75f;
            if (Physics.Raycast(rearOrigin, -threatDir, out rearWallHit, 1.95f, AiCore.GetSolidEnvironmentMask(), QueryTriggerInteraction.Ignore))
            {
                if (Mathf.Abs(rearWallHit.normal.y) < 0.55f)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryFindShelteredSideOfWall(Vector3 currentPos, Vector3 threatDir, Transform ignoreRoot, out Vector3 shelteredPos)
        {
            shelteredPos = Vector3.zero;
            if (!IsPositionOnExposedSideOfWall(currentPos, threatDir, ignoreRoot, out RaycastHit rearWallHit))
                return false;

            int envMask = AiCore.GetSolidEnvironmentMask();
            Vector3 probeOrigin = rearWallHit.point - threatDir * 2.30f;
            if (!Physics.Raycast(probeOrigin, threatDir, out RaycastHit backFaceHit, 2.25f, envMask, QueryTriggerInteraction.Ignore))
                return false;

            float wallThickness = Vector3.Distance(rearWallHit.point, backFaceHit.point);
            if (wallThickness < 0.04f || wallThickness > 1.65f)
                return false;

            Vector3 candidateXZ = backFaceHit.point - threatDir * 0.85f;
            Vector3 groundRayStart = new Vector3(candidateXZ.x, currentPos.y + 1.35f, candidateXZ.z);
            if (!Physics.Raycast(groundRayStart, Vector3.down, out RaycastHit groundHit, 3.0f, envMask, QueryTriggerInteraction.Ignore))
                return false;

            if (groundHit.normal.y < 0.65f)
                return false;

            Vector3 candidatePos = groundHit.point;
            if (Mathf.Abs(candidatePos.y - currentPos.y) > 1.50f)
                return false;

            Vector3 backSurfaceCheck = backFaceHit.point - threatDir * 0.08f;
            Vector3 toCandidate = (candidatePos + Vector3.up * 0.75f) - backSurfaceCheck;
            if (toCandidate.sqrMagnitude > 0.01f &&
                AiCore.IsDirectionBlockedByEnvironment(backSurfaceCheck, toCandidate.normalized, toCandidate.magnitude, ignoreRoot))
            {
                return false;
            }

            Vector3 right = new Vector3(threatDir.z, 0f, -threatDir.x);
            Vector3 chestPos = candidatePos + Vector3.up * 0.80f;
            if (AiCore.IsDirectionBlockedByEnvironment(chestPos, right, 0.35f, ignoreRoot) &&
                AiCore.IsDirectionBlockedByEnvironment(chestPos, -right, 0.35f, ignoreRoot))
            {
                return false;
            }

            bool currentIsOutdoor = !AiCore.IsDirectionBlockedByEnvironment(currentPos + Vector3.up * 0.85f, Vector3.up, 6.0f, ignoreRoot);
            if (currentIsOutdoor && AiCore.IsDirectionBlockedByEnvironment(candidatePos + Vector3.up * 0.85f, Vector3.up, 6.0f, ignoreRoot))
            {
                return false;
            }

            shelteredPos = candidatePos;
            return true;
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

            if (_nextBipodDeployCheckTime.TryGetValue(ptr, out float nextCheck) && Time.time < nextCheck)
                return;

            _nextBipodDeployCheckTime[ptr] = Time.time + 1.25f;

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
                        _bipodStandUntil[ptr] = Time.time + 4.5f;
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
                    _bipodStandUntil[ptr] = Time.time + 3.5f;
                    _noProneUntil[ptr] = Time.time + 10.0f;
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
                    _bipodStandUntil[ptr] = Time.time + 4.5f;
                    _nextStanceDropAllowed[ptr] = Time.time + 4.5f;
                    _noProneUntil[ptr] = Time.time + 10.0f;
                    soldier.StartCoroutine(soldier.RetryBipodDeployAfterStanding());
                    BipodsDeployed++;
                    return;
                }
            }

            bool canTryProneBipod = (!_noProneUntil.TryGetValue(ptr, out float noProne) || Time.time >= noProne) &&
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
                        _noProneUntil[ptr] = Time.time + 12.0f;
                    }
                }
                else
                {
                    SetBotPose(ai, soldier, SoldierPose.Prone);
                }
            }
        }

        // =====================================================================
        // Point-Blank Bayonet, Melee & Butt-Stroke CQB Combat
        // =====================================================================
        private static void TryEnsureBayonetMounted(Soldier soldier)
        {
            if (!AiCore.IsLocallyOwnedBot(soldier) ||
                soldier.IsOnVehicle() ||
                soldier.HasBayonetReady ||
                soldier.isInHandsAnimation ||
                soldier.IsReloading)
            {
                return;
            }

            IntPtr ptr = soldier.Pointer;
            if (!_bayonetCheckedSoldiers.Add(ptr))
                return;

            GenericGun heldGun = soldier.GetHeldGun();
            if (heldGun == null)
                return;

            if (heldGun.integratedBayonet != null && !heldGun.integratedBayonet.IsMounted)
            {
                heldGun.integratedBayonet.SetMounted(true);
            }
        }

        private static bool TryClaimMeleeTargetSlot(Soldier attacker, Soldier target, float now, out int slotIndex)
        {
            slotIndex = 0;
            if (attacker == null || target == null || attacker.Pointer == IntPtr.Zero || target.Pointer == IntPtr.Zero)
                return false;

            IntPtr atkPtr = attacker.Pointer;
            IntPtr tgtPtr = target.Pointer;
            const float slotHoldSeconds = 0.90f;

            _meleeTargetSlots.TryGetValue(tgtPtr, out MeleeTargetSlotState state);

            if (state.Attacker0 == atkPtr)
            {
                state.Expire0 = now + slotHoldSeconds;
                _meleeTargetSlots[tgtPtr] = state;
                slotIndex = 0;
                return true;
            }

            if (state.Attacker1 == atkPtr)
            {
                state.Expire1 = now + slotHoldSeconds;
                _meleeTargetSlots[tgtPtr] = state;
                slotIndex = 1;
                return true;
            }

            if (state.Attacker0 == IntPtr.Zero || now >= state.Expire0)
            {
                state.Attacker0 = atkPtr;
                state.Expire0 = now + slotHoldSeconds;
                _meleeTargetSlots[tgtPtr] = state;
                slotIndex = 0;
                return true;
            }

            if (state.Attacker1 == IntPtr.Zero || now >= state.Expire1)
            {
                state.Attacker1 = atkPtr;
                state.Expire1 = now + slotHoldSeconds;
                _meleeTargetSlots[tgtPtr] = state;
                slotIndex = 1;
                return true;
            }

            return false;
        }

        private static SoldierPose GetDesiredMeleePose(Soldier enemy, float dist)
        {
            bool enemyIsLow = enemy != null &&
                              (enemy.m_pose == SoldierPose.Prone || enemy.m_pose == SoldierPose.Crouch);
            return (enemyIsLow && dist <= 2.75f) ? SoldierPose.Crouch : SoldierPose.Idle;
        }

        private static Vector3 GetMeleeApproachPosition(Soldier attacker, Soldier enemy, int slotIndex, float standoffDist)
        {
            Vector3 enemyPos = enemy.transform.position;
            Vector3 attackerPos = attacker.transform.position;
            Vector3 fromEnemy = attackerPos - enemyPos;
            fromEnemy.y = 0f;
            if (fromEnemy.sqrMagnitude < 0.04f)
            {
                fromEnemy = -attacker.transform.forward;
                fromEnemy.y = 0f;
            }
            fromEnemy = fromEnemy.sqrMagnitude > 0.001f ? fromEnemy.normalized : Vector3.back;

            // Primary attacker (Slot 0) approaches straight along the line of contact to standoffDist (0-deg offset)
            // so he never spirals in a tangential orbit or walks onto the enemy's center capsule!
            if (slotIndex == 0)
                return enemyPos + fromEnemy * standoffDist;

            // Secondary attacker (Slot 1) anchors +-65 deg off Attacker 0's bearing (NOT Slot 1's own live bearing),
            // creating a stable world-anchored 2-man CQB pincer that never orbits or overlaps Slot 0.
            if (_meleeTargetSlots.TryGetValue(enemy.Pointer, out MeleeTargetSlotState slotState) &&
                slotState.Attacker0 != IntPtr.Zero &&
                slotState.Attacker0 != attacker.Pointer &&
                AiCore.TryGetSoldierHandles(slotState.Attacker0, out AiCore.SoldierFastHandles atk0H) &&
                atk0H != null &&
                atk0H.Tr != null)
            {
                Vector3 baseDir = atk0H.Tr.position - enemyPos;
                baseDir.y = 0f;
                if (baseDir.sqrMagnitude > 0.04f)
                {
                    baseDir.Normalize();
                    const float cos65 = 0.42262f;
                    const float sin65 = 0.90631f;
                    Vector3 flankLeft = new Vector3(
                        baseDir.x * cos65 - baseDir.z * sin65,
                        0f,
                        baseDir.x * sin65 + baseDir.z * cos65);
                    Vector3 flankRight = new Vector3(
                        baseDir.x * cos65 + baseDir.z * sin65,
                        0f,
                        -baseDir.x * sin65 + baseDir.z * cos65);

                    bool preferLeft = Vector3.Dot(flankLeft, fromEnemy) >= Vector3.Dot(flankRight, fromEnemy);
                    Vector3 primaryFlank = preferLeft ? flankLeft : flankRight;
                    Vector3 altFlank = preferLeft ? flankRight : flankLeft;
                    Transform enemyRoot = enemy.transform.root;
                    Vector3 probeOrigin = enemyPos + Vector3.up * 0.85f;

                    if (!AiCore.IsDirectionBlockedByEnvironment(probeOrigin, primaryFlank, standoffDist + 0.25f, enemyRoot))
                        return enemyPos + primaryFlank * standoffDist;
                    if (!AiCore.IsDirectionBlockedByEnvironment(probeOrigin, altFlank, standoffDist + 0.25f, enemyRoot))
                        return enemyPos + altFlank * standoffDist;
                }
            }

            return enemyPos + fromEnemy * standoffDist;
        }

        private static void UpdatePointBlankMeleeEngagement(
            SoldierAI ai,
            Soldier soldier,
            AiCore.SoldierFastHandles h,
            Soldier meleeEnemy,
            float dist,
            float strikeRange,
            int meleeSlot,
            float now,
            bool isPerFrameHook)
        {
            IntPtr ptr = soldier.Pointer;
            SoldierPose desiredPose = GetDesiredMeleePose(meleeEnemy, dist);
            _noProneUntil[ptr] = now + 2.5f;
            if (desiredPose == SoldierPose.Crouch)
                _combatCrouchLockUntil[ptr] = now + 1.0f;

            if (soldier.m_pose != desiredPose || ai.actualPose != desiredPose)
            {
                SetBotPose(ai, soldier, desiredPose, true);
            }

            // Maintain clean weapon-reach standoff (1.35m bayonet / 1.15m knife or rifle butt) outside capsule
            // collision diameter (~0.90m) so combatants never stack on top of each other or whip yaw in circles.
            float standoffDist = soldier.HasBayonetReady ? 1.35f : 1.15f;
            const float minPersonalSpace = 0.94f;

            if (!soldier.isInBodyAnimation)
            {
                if (dist > standoffDist + 0.12f)
                {
                    if (!isPerFrameHook || !_nextMeleeStepTime.TryGetValue(ptr, out float nextStep) || now >= nextStep)
                    {
                        _nextMeleeStepTime[ptr] = now + 0.18f;
                        Vector3 approachPos = GetMeleeApproachPosition(soldier, meleeEnemy, meleeSlot, standoffDist);
                        ai.MoveDirectlyToward(approachPos, isPerFrameHook ? 0.55f : 0.85f);
                        ai.moveLookingTarget = true;
                    }
                }
                else if (dist < minPersonalSpace)
                {
                    Vector3 enemyPos = meleeEnemy.transform.position;
                    Vector3 myPos = (h != null && h.Tr != null) ? h.Tr.position : soldier.transform.position;
                    Vector3 away = myPos - enemyPos;
                    away.y = 0f;
                    if (away.sqrMagnitude < 0.01f)
                    {
                        away = -soldier.transform.forward;
                        away.y = 0f;
                    }
                    away = away.sqrMagnitude > 0.001f ? away.normalized : Vector3.back;
                    Transform myRoot = (h != null && h.RootTr != null) ? h.RootTr : soldier.transform.root;

                    if (!AiCore.IsDirectionBlockedByEnvironment(myPos + Vector3.up * 0.75f, away, 0.85f, myRoot))
                    {
                        if (!isPerFrameHook || !_nextMeleeStepTime.TryGetValue(ptr, out float nextBack) || now >= nextBack)
                        {
                            _nextMeleeStepTime[ptr] = now + 0.18f;
                            ai.MoveDirectlyToward(enemyPos + away * standoffDist, 0.45f);
                            ai.moveLookingTarget = true;
                        }
                    }
                    else if (ai.IsMoving())
                    {
                        ai.Stop(true);
                    }
                }
                else
                {
                    if (ai.moveDirectlyToward_end > now)
                        ai.moveDirectlyToward_end = 0f;
                    if (ai.IsMoving())
                        ai.Stop(true);
                }
            }

            if (dist <= strikeRange)
            {
                TryExecuteMeleeAttack(ai, soldier, meleeEnemy);
            }
        }

        private static bool HasAnyUsableSmallArmsAmmo(Soldier soldier)
        {
            if (soldier == null)
                return false;

            GenericGun held = soldier.GetHeldGun();
            if (held != null && !held.IsAtWeapon() && (held.GetCurrentAmmoCount() > 0 || !soldier.IsOutOfAmmosFor(held)))
                return true;

            GenericGun g0 = soldier.GetHeldGun(0);
            if (g0 != null && !g0.IsAtWeapon() && (g0.GetCurrentAmmoCount() > 0 || !soldier.IsOutOfAmmosFor(g0)))
                return true;

            GenericGun g2 = soldier.GetHeldGun(2);
            if (g2 != null && !g2.IsAtWeapon() && (g2.GetCurrentAmmoCount() > 0 || !soldier.IsOutOfAmmosFor(g2)))
                return true;

            return false;
        }

        /// <summary>
        /// Central combat-safety gate preventing bleeding soldiers from holstering their weapon to bandage
        /// while an enemy is at close quarters, actively engaging them with LOF, or firing right next to them.
        /// </summary>
        internal static bool CanSafelyBandageNow(
            SoldierAI ai,
            Soldier soldier,
            float now,
            out string blockReason,
            out float gunfireDist)
        {
            blockReason = null;
            gunfireDist = -1f;
            if (soldier == null)
                return false;

            AiCore.SoldierFastHandles h = AiCore.GetOrCreateSoldierHandles(soldier);
            if (h == null || h.Tr == null)
                return true;

            bool hasVisibleTarget = ai != null && ai.visibleTarget != null;
            float enemyDist = hasVisibleTarget ? ai.distFromTarget : 999f;

            // Never pull out a bandage within melee / point-blank reach of a visible enemy, even if dry on ammo!
            if (hasVisibleTarget && enemyDist <= 4.5f)
            {
                blockReason = "enemy_melee_range";
                return false;
            }

            bool canFightWithGun = HasAnyUsableSmallArmsAmmo(soldier);
            Vector3 myPos = h.Tr.position;
            string myFac = AiCore.GetCachedFaction(h, now);
            bool hasNearGunfire = VisionPatches.TryGetNearestEnemyGunfire(myPos, myFac, 18.0f, 3.0f, out gunfireDist);

            if (!canFightWithGun)
            {
                // Out of ammo: only block bandaging if still standing in the open under point-blank gunfire
                if (!soldier.IsOnCover() && hasNearGunfire && gunfireDist <= 10.5f)
                {
                    blockReason = "unarmed_exposed_gunfire";
                    return false;
                }
                return true;
            }

            if (hasVisibleTarget && enemyDist <= 10.5f)
            {
                blockReason = "enemy_very_close";
                return false;
            }

            if (ai != null && ai.HasEnemyAndCanFireAtHim && enemyDist <= 26.0f)
            {
                blockReason = "enemy_lof_close";
                return false;
            }

            if (hasNearGunfire)
            {
                if (gunfireDist <= 10.5f)
                {
                    blockReason = "gunfire_point_blank";
                    return false;
                }

                // Between 10.5m and 18m: safe ONLY if tucked in cover with no visible enemy and low active suppression
                if (!soldier.IsOnCover() || hasVisibleTarget || soldier.GetSuppressionValue() >= 28f)
                {
                    blockReason = "gunfire_exposed";
                    return false;
                }
            }

            if (!soldier.IsOnCover() && soldier.GetSuppressionValue() >= 35f && hasVisibleTarget && enemyDist <= 45.0f)
            {
                blockReason = "suppressed_in_open";
                return false;
            }

            return true;
        }

        private static void CountBandageBlocked(IntPtr ptr, float now)
        {
            if (!_nextBandageBlockCountTime.TryGetValue(ptr, out float nextT) || now >= nextT)
            {
                _nextBandageBlockCountTime[ptr] = now + 2.5f;
                UnsafeBandagesBlockedCount++;
            }
        }

        [HarmonyPatch(typeof(SoldierAI), nameof(SoldierAI.TryStopBleeding))]
        public static class SoldierAI_TryStopBleeding_Patch
        {
            public static bool Prefix(SoldierAI __instance)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value || ReferenceEquals(__instance, null))
                        return true;

                    AiCore.SoldierFastHandles h = AiCore.GetOrCreateSoldierHandlesByAi(__instance);
                    if (!AiCore.IsLocallyOwnedBot(h))
                        return true;

                    float now = Time.time;
                    Soldier soldier = h.Ref;
                    if (!CanSafelyBandageNow(__instance, soldier, now, out string blockReason, out float gfDist))
                    {
                        CountBandageBlocked(h.Ptr, now);
                        BotActionTrace.RecordEvent(soldier, BotTraceEventKind.BandageBlocked, blockReason, __instance.distFromTarget, gfDist);
                        return false;
                    }

                    BotActionTrace.RecordEvent(
                        soldier,
                        BotTraceEventKind.BandageStart,
                        soldier.IsOnCover() ? "in_cover" : "safe_window",
                        __instance.distFromTarget,
                        gfDist);
                }
                catch { }
                return true;
            }
        }

        private static bool IsEngagedInPointBlankMelee(
            SoldierAI ai,
            Soldier soldier,
            float dist,
            out Soldier enemySoldier,
            out float lungeRange,
            out float ceaseFireRange,
            out float strikeRange,
            out int meleeSlotIndex)
        {
            enemySoldier = null;
            lungeRange = 0f;
            ceaseFireRange = 0f;
            strikeRange = 0f;
            meleeSlotIndex = 0;

            if (!VisionConfig.EnablePointBlankMelee.Value ||
                ai == null ||
                soldier == null ||
                dist > 6.0f ||
                ai.visibleTarget == null ||
                ai.HeldMortar != null ||
                soldier.IsOnVehicle() ||
                soldier.UsingBipod() ||
                soldier.IsBipodTransitioning() ||
                soldier.HasSurrended() ||
                soldier.IsCarryingBody() ||
                soldier.IsCarried())
            {
                return false;
            }

            enemySoldier = ai.visibleTarget.TryCast<Soldier>();
            if (enemySoldier == null ||
                !enemySoldier.IsAlive ||
                enemySoldier.IsOnVehicle() ||
                enemySoldier.HasSurrended())
            {
                return false;
            }

            Vector3 myPos = soldier.transform.position;
            Vector3 enemyPos = enemySoldier.transform.position;
            if (Mathf.Abs(enemyPos.y - myPos.y) > 1.65f)
                return false;

            float now = Time.time;
            bool hasBayonet = soldier.HasBayonetReady;
            bool holdsMelee = soldier.HoldsMeleeWeapon;
            bool isChargingOrder = soldier.IsCharging();
            GenericGun heldGun = soldier.GetHeldGun();
            bool gunEmptyOrReloading = soldier.IsReloading ||
                                       soldier.MustReload() ||
                                       (heldGun != null && heldGun.GetCurrentAmmoCount() <= 0);
            // CRITICAL: Use heldGun.boltActionRequired (isManualBoltOperated && bolt_required @ 0x180876300),
            // NEVER raw heldGun.bolt_required!
            bool boltCyclingClose = soldier.CantAimDueToBoltActionUsage ||
                                    (heldGun != null && (heldGun.boltActionRequired || heldGun.IsReloadingOrUsingBolt()));

            string myFac = AiCore.GetCachedFaction(soldier);
            bool hasCloseGunfire = VisionPatches.TryGetNearestEnemyGunfire(myPos, myFac, 15.0f, 2.5f, out float nearGunfireDist) ||
                                   VisionPatches.HasFiredRecently(enemySoldier, 2.5f);

            if (holdsMelee)
            {
                lungeRange = 5.6f;
                ceaseFireRange = 5.6f;
                strikeRange = 1.90f;
            }
            else if (isChargingOrder)
            {
                lungeRange = hasBayonet ? 5.2f : 3.6f;
                ceaseFireRange = (gunEmptyOrReloading || boltCyclingClose) ? 4.5f : 2.5f;
                strikeRange = hasBayonet ? 2.25f : 1.65f;
            }
            else if (gunEmptyOrReloading)
            {
                lungeRange = hasBayonet ? 4.2f : 3.2f;
                ceaseFireRange = lungeRange;
                strikeRange = hasBayonet ? 2.25f : 1.65f;
            }
            else if (hasCloseGunfire)
            {
                // Active gunfire within 15m while holding a loaded firearm (and NOT on a squad bayonet charge):
                // Do NOT break stance/cover to sprint into melee! Keep point-shooting unless the enemy physically
                // enters immediate contact reach.
                lungeRange = boltCyclingClose ? (hasBayonet ? 2.35f : 1.85f) : (hasBayonet ? 1.85f : 1.55f);
                ceaseFireRange = boltCyclingClose ? lungeRange : 1.25f;
                strikeRange = hasBayonet ? 2.20f : 1.65f;

                if (dist > lungeRange && dist <= 4.2f)
                {
                    IntPtr sPtr = soldier.Pointer;
                    if (!_nextMeleeSuppressCountTime.TryGetValue(sPtr, out float nextSupT) || now >= nextSupT)
                    {
                        _nextMeleeSuppressCountTime[sPtr] = now + 2.5f;
                        CloseGunfireMeleeSuppressedCount++;
                    }
                    BotActionTrace.RecordEvent(soldier, BotTraceEventKind.MeleeSuppressed, "close_gunfire", dist, nearGunfireDist);
                    return false;
                }
            }
            else
            {
                // Quiet CQB / indoor corner encounter with no surrounding gunfire
                lungeRange = boltCyclingClose ? (hasBayonet ? 3.1f : 2.2f) : (hasBayonet ? 2.55f : 1.80f);
                ceaseFireRange = boltCyclingClose ? lungeRange : (hasBayonet ? 1.85f : 1.40f);
                strikeRange = hasBayonet ? 2.25f : 1.65f;
            }

            if (dist > lungeRange)
                return false;

            // Anti-stacking cap: allow at most 2 friendly bots to actively melee the same target;
            // additional bots keep squad spacing and cover with firearms instead of dogpiling.
            if (!TryClaimMeleeTargetSlot(soldier, enemySoldier, now, out meleeSlotIndex))
                return false;

            Vector3 toEnemy = (enemyPos + Vector3.up * 1.05f) - (myPos + Vector3.up * 1.05f);
            float checkDist = Mathf.Min(dist - 0.25f, 3.5f);
            if (checkDist > 0.30f &&
                AiCore.IsDirectionBlockedByEnvironment(myPos + Vector3.up * 1.05f, toEnemy.normalized, checkDist, soldier.transform.root))
            {
                return false;
            }

            BotActionTrace.RecordEvent(
                soldier,
                BotTraceEventKind.MeleeEnter,
                isChargingOrder ? "charge_order" : (gunEmptyOrReloading ? "gun_empty" : "point_blank"),
                dist,
                nearGunfireDist);
            return true;
        }

        private static void TryExecuteMeleeAttack(SoldierAI ai, Soldier soldier, Soldier enemy)
        {
            if (ai == null || !AiCore.IsLocallyOwnedBot(soldier) || enemy == null)
                return;

            Vector3 toEnemy = enemy.transform.position - soldier.transform.position;
            toEnemy.y = 0f;
            if (toEnemy.sqrMagnitude > 0.01f && AiCore.HorizontalAngleToDirection(soldier, toEnemy) > 42f)
                return;

            // Native Soldier+<MeleeDamageCR>d__489 (0x18077EB52) casts a horizontal 0.50m-radius OverlapCapsule
            // forward from Soldier.FacePosition() (animated headBone.position @ [rdx+160h]). Standing FacePosition().y (~1.62m)
            // puts the capsule bottom at ~1.12m, passing completely over prone enemies (0.15m-0.55m) and skimming crouched ones!
            // Crouch first and wait (~0.15s) until the animated headBone drops within strike height of the enemy's feet.
            if (enemy.m_pose == SoldierPose.Prone || enemy.m_pose == SoldierPose.Crouch)
            {
                if (soldier.m_pose != SoldierPose.Crouch || ai.actualPose != SoldierPose.Crouch)
                    SetBotPose(ai, soldier, SoldierPose.Crouch, true);

                if (AiCore.CanBotChangePoseAutonomously(soldier) &&
                    (soldier.FacePosition().y - enemy.transform.position.y) > 1.34f)
                {
                    return;
                }
            }

            ai.MeleeBlockWindow();
            if (soldier.IsBlocking || soldier.isInHandsAnimation || soldier.isInBodyAnimation)
                return;

            IntPtr ptr = soldier.Pointer;
            if (_nextMeleeAttemptTime.TryGetValue(ptr, out float nextMelee) && Time.time < nextMelee)
                return;

            if (!AiCore.TryConsumeBotRpcBudget(0.25f))
                return;

            _nextMeleeAttemptTime[ptr] = Time.time + 0.45f;
            soldier.SetGunTriggerPressed(false);
            soldier.Melee();
            MeleeAttacksTriggered++;
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

                    if ((soldier.isBleeding || soldier.GetBleedValue() > 0.01f) &&
                        soldier.inventory != null &&
                        soldier.inventory.FindItemOfType<VirtualBandages>() != null)
                    {
                        IntPtr ptr = soldier.Pointer;
                        float now = Time.time;
                        bool safeToBandage = CanSafelyBandageNow(__instance, soldier, now, out _, out _);

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
                            if (!_bleedCoverDeadline.TryGetValue(ptr, out float deadline))
                            {
                                SoldierPersonality bleedPers = GetPersonality(soldier);
                                float seekWindow = (bleedPers == SoldierPersonality.Rifleman_Cautious || bleedPers == SoldierPersonality.Medic_Triage)
                                    ? 4.5f
                                    : (bleedPers == SoldierPersonality.Assaulter_Shock ? 2.2f : 3.5f);
                                deadline = now + seekWindow;
                                _bleedCoverDeadline[ptr] = deadline;
                            }

                            if (!soldier.IsOnCover() && now < deadline)
                            {
                                bool movingToCover = TrySeekNearbyCover(__instance, soldier);
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
                        _noProneUntil[ptr] = Time.time + 3.5f;
                    }

                    // Only enter the self-bandaging routine if the soldier actually carries VirtualBandages
                    // AND is not actively fighting / under close enemy gunfire!
                    bool isBleedingWithBandage = (soldier.isBleeding || soldier.GetBleedValue() > 0.01f) &&
                                                 soldier.inventory != null &&
                                                 soldier.inventory.FindItemOfType<VirtualBandages>() != null;
                    if (isBleedingWithBandage)
                    {
                        float nowBleed = Time.time;
                        if (!_bleedCoverDeadline.TryGetValue(ptr, out float deadline))
                        {
                            SoldierPersonality bleedPers = personality;
                            float seekWindow = (bleedPers == SoldierPersonality.Rifleman_Cautious || bleedPers == SoldierPersonality.Medic_Triage)
                                ? 4.5f
                                : (bleedPers == SoldierPersonality.Assaulter_Shock ? 2.2f : 3.5f);
                            deadline = nowBleed + seekWindow;
                            _bleedCoverDeadline[ptr] = deadline;
                        }

                        if (!CanSafelyBandageNow(__instance, soldier, nowBleed, out string bleedBlockReason, out float bleedGfDist))
                        {
                            // Active shooter / close gunfire! Defer cover-bandage deadline and fall through into CQB & GOAP
                            // so the soldier returns fire or breaks LOS first instead of pulling out a bandage!
                            _bleedCoverDeadline[ptr] = Mathf.Max(deadline, nowBleed + 1.85f);
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
                            else
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
                        _bleedCoverDeadline.Remove(ptr);
                    }

                    GetEffectivePeekParameters(soldier, vet01, out float maxPeekSupp, out float duckBackSupp, out float peekCooldown);

                    // Record visual contact for 15m Local Contact Callouts & Last-Known Suppression, or flush ducked enemy with grenade
                    if (__instance.visibleTarget != null)
                    {
                        RecordAndBroadcastVisualContact(soldier, __instance.visibleTarget);
                    }
                    else
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
                            _noProneUntil[ptr] = Time.time + 10.0f;
                            if (!_nextIndoorCeilingCountTime.TryGetValue(ptr, out float nextIndoorCount) || Time.time >= nextIndoorCount)
                            {
                                _nextIndoorCeilingCountTime[ptr] = Time.time + 5.0f;
                                IndoorPronePreventedCount++;
                            }
                        }
                        else if (isOnBridge)
                        {
                            bool bridgeMoving = __instance.IsMoving() || IsAsyncPathRefreshPending(__instance, soldier, h.Tr.position, Time.time);
                            SetBotPose(__instance, soldier, bridgeMoving ? SoldierPose.Idle : SoldierPose.Crouch, true);
                            _noProneUntil[ptr] = Time.time + 4.0f;
                        }
                    }

                    bool isPeeking = false;
                    if (_peekRestoreTime.TryGetValue(ptr, out float restoreAt))
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
                                _peekRestoreTime[ptr] = Time.time + 2.8f;
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
                            _peekRestoreTime.Remove(ptr);
                            float cdJitter = (AiCore.GetDeterministicHash(ptr, 211) & 15) * 0.05f;
                            _nextPeekAllowedTime[ptr] = Time.time + peekCooldown + cdJitter;
                            if (!__instance.IsMoving() &&
                                !IsAsyncPathRefreshPending(__instance, soldier, h.Tr.position, Time.time) &&
                                soldier.m_pose == SoldierPose.Idle)
                            {
                                SetBotPose(__instance, soldier, SoldierPose.Crouch, true);
                                _combatStanceHoldUntil[ptr] = Time.time + 5.0f;
                                _combatCrouchLockUntil[ptr] = Time.time + 3.5f;
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
                        else if ((personality == SoldierPersonality.Assaulter_Breacher ||
                                  personality == SoldierPersonality.Rifleman_Grenadier ||
                                  personality == SoldierPersonality.AT_Demolitionist) &&
                                 AiCore.TryGetSpottableCenter(__instance.visibleTarget, out Vector3 grenTargetPos) &&
                                 !__instance.visibleTarget.IsWheeledVehicleOrTank() &&
                                 __instance.distFromTarget >= 9.0f &&
                                 __instance.distFromTarget <= 34.0f &&
                                 !soldier.isInHandsAnimation &&
                                 !soldier.IsReloading &&
                                 soldier.inventory != null &&
                                 Time.time > soldier.throw_end_time + 5.0f)
                        {
                            VirtualItem fragItem = soldier.inventory.FindItemOfType<VirtualGrenade>();
                            if (fragItem != null &&
                                __instance.distFromTarget <= soldier.GetGrenadeThrowRange(fragItem) &&
                                UnityEngine.Random.Range(0, 100) > 60 &&
                                IsSafeToThrowGrenadeAt(soldier, grenTargetPos, __instance.distFromTarget, 9.0f) &&
                                AiCore.TryConsumeBotRpcBudget(0.45f))
                            {
                                soldier.Throw(fragItem, __instance.visibleTarget);
                            }
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
                                    __instance.visibleTarget = crewSpottable;
                                    soldier.CurrentVisibleTarget = crewSpottable;
                                    __instance.distFromTarget = Vector3.Distance(soldier.transform.position, exposedCrew.transform.position);
                                    RecordAndBroadcastVisualContact(soldier, crewSpottable);
                                    if (!_nextTankDisciplineCountTime.TryGetValue(ptr, out float nextCount) || Time.time >= nextCount)
                                    {
                                        _nextTankDisciplineCountTime[ptr] = Time.time + 3.0f;
                                        ExposedCrewTargeted++;
                                    }
                                }
                            }
                            else if (!hasStowedAtReady)
                            {
                                // Switch any rifleman, MG gunner, or dry AT unit away from an invulnerable tank
                                // to visible enemy infantry advancing behind or beside the tank!
                                if (TryFindVisibleFootEnemy(soldier, out Spottable altEnemy, out float altDist))
                                {
                                    __instance.visibleTarget = altEnemy;
                                    soldier.CurrentVisibleTarget = altEnemy;
                                    __instance.distFromTarget = altDist;
                                    GenericGun heldGun = soldier.GetHeldGun();
                                    __instance.targetInWeaponRange = heldGun != null && heldGun.DamageRange() >= altDist;
                                    RecordAndBroadcastVisualContact(soldier, altEnemy);
                                }
                                else
                                {
                                    __instance.targetInWeaponRange = false;
                                    soldier.SetGunTriggerPressed(false);
                                    _combatStanceHoldUntil[ptr] = Time.time + 6.0f;
                                    isEvadingArmor = TryEvadeArmorToCover(__instance, soldier, targetVeh, __instance.distFromTarget);
                                    if (isEvadingArmor)
                                    {
                                        __instance.visibleTarget = null;
                                        _combatCrouchLockUntil.Remove(ptr);
                                    }
                                    else
                                    {
                                        _combatCrouchLockUntil[ptr] = Time.time + 3.5f;
                                    }
                                    if (!_nextTankDisciplineCountTime.TryGetValue(ptr, out float nextCount) || Time.time >= nextCount)
                                    {
                                        _nextTankDisciplineCountTime[ptr] = Time.time + 3.0f;
                                        UselessTankFirePrevented++;
                                    }
                                }
                            }
                        }
                    }

                    bool isGunner = IsMachineGunnerOrHasBipod(soldier, out AttachmentBipod bipod);
                    bool isHighBipodStanding = _bipodStandUntil.TryGetValue(ptr, out float standUntil) && Time.time < standUntil;
                    float dist = __instance.distFromTarget;

                    if (VisionConfig.EnablePointBlankMelee.Value &&
                        ((__instance.HasEnemyAndCanFireAtHim && dist <= 45f) || soldier.IsCharging() || soldier.isAlerted))
                    {
                        TryEnsureBayonetMounted(soldier);
                    }

                    bool inPointBlankMelee = IsEngagedInPointBlankMelee(
                        __instance,
                        soldier,
                        dist,
                        out Soldier meleeEnemy,
                        out _,
                        out _,
                        out float seqStrikeRange,
                        out int seqMeleeSlot);

                    if (inPointBlankMelee && meleeEnemy != null)
                    {
                        __instance.knifeCharge = soldier.HoldsMeleeWeapon || soldier.HasBayonetReady;
                        __instance.moveLookingTarget = true;

                        if (!soldier.HasBayonetReady &&
                            !soldier.HoldsMeleeWeapon &&
                            soldier.InventoryKnife != null &&
                            !soldier.isInHandsAnimation)
                        {
                            GenericGun heldGun = soldier.GetHeldGun();
                            bool gunEmpty = soldier.IsReloading ||
                                            soldier.MustReload() ||
                                            (heldGun != null && heldGun.GetCurrentAmmoCount() <= 0);
                            // Only holster firearm for inventory knife if the held gun is actually empty/reloading;
                            // if loaded at point-blank, buttstroke with Melee() or keep point-shooting!
                            if (gunEmpty && AiCore.TryConsumeBotRpcBudget(0.35f))
                            {
                                soldier.ToggleInventoryKnife();
                            }
                        }

                        UpdatePointBlankMeleeEngagement(
                            __instance,
                            soldier,
                            AiCore.GetOrCreateSoldierHandles(soldier),
                            meleeEnemy,
                            dist,
                            seqStrikeRange,
                            seqMeleeSlot,
                            Time.time,
                            isPerFrameHook: false);
                    }

                    if (__instance.HasEnemyAndCanFireAtHim || suppression >= 8f)
                    {
                        _combatStanceHoldUntil[ptr] = Time.time + 6.0f;
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
                            if ((__instance.MoveDestination - shelterPos).sqrMagnitude > 0.64f)
                            {
                                __instance.MoveDestination = shelterPos;
                            }
                        }
                    }

                    bool isHealingTeammate = IsActivelyHealingTeammate(soldier, out _);

                    bool isStaffingEmplacement = IsActivelyHeadingToEmplacement(soldier, Time.time);

                    // Execute 3-Tier Bitmask GOAP Planner & Squad Blackboard (Pin/Flank/Smoke/Overwatch/Rescue)
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
                        if (!_nextWallSideCheckTime.TryGetValue(ptr, out float nextWallSide) || Time.time >= nextWallSide)
                        {
                            _nextWallSideCheckTime[ptr] = Time.time + 0.90f;
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
                                    if ((__instance.MoveDestination - shelteredDestPos).sqrMagnitude > 0.49f)
                                    {
                                        __instance.MoveDestination = shelteredDestPos;
                                    }
                                    _nextWallSideCheckTime[ptr] = Time.time + 3.5f;
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
                                                soldier.CoverPosition(newDest.Cast<AiDestination>());
                                            }
                                        }

                                        if ((__instance.MoveDestination - shelteredPos).sqrMagnitude > 0.49f)
                                        {
                                            __instance.MoveDestination = shelteredPos;
                                        }
                                        _nextWallSideCheckTime[ptr] = Time.time + 4.0f;
                                        WallSideRepositionsCount++;
                                    }
                                    else if (soldier.HasDestinationAssigned &&
                                             (__instance.HasEnemyAndCanFireAtHim || suppression >= 5f || soldier.isAlerted))
                                    {
                                        soldier.TryToGetToActualCover();
                                        _nextWallSideCheckTime[ptr] = Time.time + 3.0f;
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
                    else if (isEffectivelyMoving)
                    {
                        bool inFallbackSprint = IsInIndividualOrGoapFallback(ptr, Time.time);
                        Vector3 moveEndpoint = GetActualMoveEndpoint(__instance, soldier);
                        float remDistSq = (h.Tr.position - moveEndpoint).sqrMagnitude;
                        bool stealthCrouch = soldier.IsStealth &&
                                             soldier.joinedSquad != null &&
                                             soldier.joinedSquad.Leader != null &&
                                             soldier.joinedSquad.Leader.m_pose > SoldierPose.Idle;

                        // If caught moving in the open under MG/rifle suppression (> 8.5m from destination), seek nearby local cover within 13.5m
                        // (including deck-safe bridge parapets/pillars when EnableBridgeCrossingDiscipline is active)
                        if (suppression >= 20f &&
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
                                bool canPeekNow = !_nextPeekAllowedTime.TryGetValue(ptr, out float nextPeekAllowed) || Time.time >= nextPeekAllowed;
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
                                        _noProneUntil[ptr] = Time.time + 10.0f;
                                        _peekRestoreTime[ptr] = Time.time + GetJitteredPeekDuration(ptr, 4.0f);
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
                                    bool canGoProne = (!_noProneUntil.TryGetValue(ptr, out float noProne) || Time.time >= noProne) &&
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
                                        if (!_nextStanceDropAllowed.TryGetValue(ptr, out float nextAllowed) || Time.time >= nextAllowed)
                                        {
                                            _nextStanceDropAllowed[ptr] = Time.time + 3.5f;
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
                            bool inCombatHold = _combatStanceHoldUntil.TryGetValue(ptr, out float holdUntil) && Time.time < holdUntil;
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
                                    bool canPeekTrenchNow = !_nextPeekAllowedTime.TryGetValue(ptr, out float nextTrenchP) || Time.time >= nextTrenchP;
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
                                     (!_noProneUntil.TryGetValue(ptr, out float noProneHold) || Time.time >= noProneHold))
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
                    bool isWallBlockedNow = _wallBlockedUntil.TryGetValue(ptr, out float wbUntil) && Time.time < wbUntil;
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
                        if (!_nextLiberateCheckTime.TryGetValue(ptr, out float nextLib) || Time.time >= nextLib)
                        {
                            _nextLiberateCheckTime[ptr] = Time.time + 1.5f;
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

            private static void TryLiberateNearbyComrade(SoldierAI ai, Soldier rescuer, SoldierPersonality personality)
            {
                AiCore.EnsureWorldSnapshot();
                int solCount = AiCore.WorldSoldierCount;
                if (solCount == 0) return;

                IntPtr rescuerPtr = rescuer.Pointer;
                string myFaction = rescuer.faction;
                Vector3 myPos = rescuer.transform.position;
                float searchRadius = VisionConfig.LiberateSearchRadius.Value;
                if (personality == SoldierPersonality.Medic_Heroic || personality == SoldierPersonality.Leader_OldGuard)
                    searchRadius *= 1.50f;
                else if (personality == SoldierPersonality.Rifleman_Cautious || personality == SoldierPersonality.Medic_Triage)
                    searchRadius *= 0.60f;

                float bestDistSq = searchRadius * searchRadius;
                Soldier bestSurrendered = null;
                var soldiers = AiCore.WorldSoldiers;

                for (int i = 0; i < solCount; i++)
                {
                    ref AiCore.SoldierSnapshot sol = ref soldiers[i];
                    if (!sol.HasSurrendered || sol.Ptr == rescuerPtr || sol.Faction != myFaction)
                        continue;

                    // Ignore surrendered teammates on a different building floor or cliff level
                    if (Mathf.Abs(sol.Pos.y - myPos.y) > 2.4f)
                        continue;

                    float dSq = (sol.Pos - myPos).sqrMagnitude;
                    if (dSq < bestDistSq && sol.Ref != null && sol.Ref.IsAlive && sol.Ref.HasSurrended())
                    {
                        bestDistSq = dSq;
                        bestSurrendered = sol.Ref;
                    }
                }

                if (bestSurrendered != null)
                {
                    if (bestDistSq <= 3.8f * 3.8f)
                    {
                        _liberateMoveStartTime.Remove(rescuerPtr);
                        if (ai != null && ai.moveDirectlyToward_end > Time.time)
                            ai.moveDirectlyToward_end = 0f;
                        if (AiCore.TryConsumeBotRpcBudget(0.45f))
                        {
                            string sqId = rescuer.joinedSquad != null ? rescuer.joinedSquad.InstanceID.ToString() : "";
                            bestSurrendered.UnSurrenderSynched(sqId);
                            ComradesLiberated++;
                            AddCombatExperienceBonus(rescuer, 30f);
                        }
                    }
                    else if (bestDistSq <= 9.5f * 9.5f &&
                             !rescuer.IsSquadLeader() &&
                             !rescuer.IsOnCover() &&
                             !ai.HasEnemyAndCanFireAtHim)
                    {
                        float now = Time.time;
                        if (_liberateMoveCooldownUntil.TryGetValue(rescuerPtr, out float cooldownUntil) && now < cooldownUntil)
                            return;

                        if (!_liberateMoveStartTime.TryGetValue(rescuerPtr, out float startTime))
                        {
                            startTime = now;
                            _liberateMoveStartTime[rescuerPtr] = startTime;
                        }
                        else if (now - startTime > 5.5f)
                        {
                            // Prevent pathing soft-lock if the surrendered teammate is behind an unpathable wall/obstacle
                            _liberateMoveStartTime.Remove(rescuerPtr);
                            _liberateMoveCooldownUntil[rescuerPtr] = now + 20.0f;
                            return;
                        }

                        AiCore.MoveBotTowardPosition(ai, rescuer, bestSurrendered.transform.position, 2.0f, 2.8f);
                    }
                }
                else
                {
                    _liberateMoveStartTime.Remove(rescuerPtr);
                }
            }
        }

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
                long profStart = HitchProfiler.Begin();
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
                        _fireAndMovementStates.Remove(ptr);
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

                    bool hasPersonalCoverLock = _localCoverProtectedUntil.TryGetValue(ptr, out float cProt) && now < cProt;
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
                        (VisionConfig.EnableBridgeCrossingDiscipline.Value && !soldier.IsOnCover() && AiCore.IsOnBridgeOrNarrowCrossing(myPos));

                    if (mustFinishMoveNow)
                    {
                        if (_fireAndMovementStates.TryGetValue(ptr, out FireAndMovementState activeSt) && activeSt.InFiringHalt)
                        {
                            float dashDur = 4.8f + ((AiCore.GetDeterministicHash(ptr, 137) & 63) * (1.8f / 63f));
                            activeSt.InFiringHalt = false;
                            activeSt.PhaseStartTime = now;
                            activeSt.PhaseEndTime = now + dashDur;
                            activeSt.HaltLostSightTime = 0f;
                            activeSt.AnchorMoveDest = __instance.MoveDestination;
                            _fireAndMovementStates[ptr] = activeSt;
                        }
                        h.LastMoveTimeFrame = frame;
                        h.CachedIsMoveTimeResult = true;
                        __result = true;
                        return false;
                    }

                    Vector3 orderAnchor = __instance.MoveDestination;
                    int botHash = AiCore.GetDeterministicHash(ptr, 131);
                    if (!_fireAndMovementStates.TryGetValue(ptr, out FireAndMovementState st) ||
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
                        _fireAndMovementStates[ptr] = st;
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
                                _fireAndMovementStates[ptr] = st;
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
                                _fireAndMovementStates[ptr] = st;
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
                        _fireAndMovementStates[ptr] = st;
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
                        _fireAndMovementStates[ptr] = st;
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
                        _fireAndMovementStates[ptr] = st;
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
                    _fireAndMovementStates[ptr] = st;

                    if (!_nextBoundingHoldCountTime.TryGetValue(ptr, out float nextBoundCount) || now >= nextBoundCount)
                    {
                        _nextBoundingHoldCountTime[ptr] = now + 5.0f;
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
                catch { }
                finally
                {
                    HitchProfiler.End(HitchBucket.MovementRoutine, profStart);
                }
                return true;
            }
        }

        // =====================================================================
        // Shell-Shocked & Skittish Dynamic Surrender Helper
        // =====================================================================
        private static bool TryEvaluateShellShockSurrender(
            SoldierAI ai,
            Soldier soldier,
            SoldierPersonality personality,
            float vet01,
            float suppression)
        {
            if (ai == null ||
                soldier == null ||
                soldier.IsOnVehicle() ||
                soldier.HasSurrended() ||
                soldier.IsCharging() ||
                soldier.IsSquadLeader())
            {
                return false;
            }

            // Officers and fearless/fanatic archetypes never surrender dynamically
            if (personality == SoldierPersonality.Leader_Spearhead ||
                personality == SoldierPersonality.Leader_Tactician ||
                personality == SoldierPersonality.Leader_OldGuard ||
                personality == SoldierPersonality.Assaulter_Shock ||
                personality == SoldierPersonality.Gunner_Anchor ||
                personality == SoldierPersonality.Medic_Heroic)
            {
                return false;
            }

            bool isSkittish = personality == SoldierPersonality.Rifleman_Cautious ||
                              personality == SoldierPersonality.Medic_Triage;
            if (isSkittish && VisionConfig.VeterancyCuresSkittishness.Value && vet01 >= 0.35f)
            {
                isSkittish = false;
            }

            FactionCommandProfile doc = GetFactionCommandProfile(soldier.faction);
            bool isFanaticDoctrine = doc.PreferAmbushWhenAlone && doc.BayonetBreachCostMult <= 0.78f;

            GenericGun heldGun = soldier.GetHeldGun();
            bool isCompletelyOutOfAmmo = heldGun != null &&
                                         heldGun.GetCurrentAmmoCount() <= 0 &&
                                         soldier.IsOutOfAmmosFor(heldGun);
            bool isBleeding = soldier.isBleeding || soldier.GetBleedValue() > 0.01f;
            bool isBleedingOrDry = isBleeding ||
                                   heldGun == null ||
                                   isCompletelyOutOfAmmo ||
                                   (heldGun.GetCurrentAmmoCount() <= 0 && soldier.MustReload());

            // Fanatic doctrines (Japan, Gurkha, Finland) never surrender unless critically wounded/bleeding under extreme suppression
            if (isFanaticDoctrine && !isBleeding)
            {
                return false;
            }

            bool isGreenRookie = !isSkittish && vet01 < 0.20f;
            bool isDryRegular = !isSkittish && !isGreenRookie && isCompletelyOutOfAmmo && vet01 < 0.45f;
            if (!isSkittish && !isGreenRookie && !isDryRegular)
            {
                return false;
            }

            float reqSupp;
            if (isSkittish)
            {
                float vetBonus = VisionConfig.VeterancyCuresSkittishness.Value ? (vet01 * 24f) : 0f;
                reqSupp = (isBleedingOrDry ? 52f : 68f) + vetBonus;
            }
            else if (isDryRegular)
            {
                reqSupp = 62f + (vet01 * 20f);
            }
            else
            {
                reqSupp = isBleedingOrDry ? 70f : 86f;
            }

            if (isFanaticDoctrine)
            {
                reqSupp += 22f;
            }

            if (suppression < reqSupp)
            {
                return false;
            }

            IntPtr ptr = soldier.Pointer;
            float now = Time.time;
            if (_nextSurrenderCheckTime.TryGetValue(ptr, out float nextCheck) && now < nextCheck)
            {
                return false;
            }
            _nextSurrenderCheckTime[ptr] = now + 1.50f;

            AiCore.EnsureWorldSnapshot();
            int sqCount = AiCore.WorldSquadCount;
            if (sqCount == 0)
            {
                return false;
            }

            Vector3 myPos = soldier.transform.position;
            string myFaction = soldier.faction;
            int enemyCloseCount = 0;
            int friendlyCloseCount = 0;
            float closestEnemyDistSq = 9999f;
            var squads = AiCore.WorldSquads;
            var soldiers = AiCore.WorldSoldiers;

            for (int s = 0; s < sqCount; s++)
            {
                ref AiCore.SquadSnapshot sqSnap = ref squads[s];
                if (sqSnap.UnitCount == 0 || (sqSnap.RefPos - myPos).sqrMagnitude > 65f * 65f)
                    continue;

                int end = sqSnap.UnitStartIndex + sqSnap.UnitCount;
                for (int i = sqSnap.UnitStartIndex; i < end; i++)
                {
                    ref AiCore.SoldierSnapshot uSnap = ref soldiers[i];
                    if (uSnap.Ptr == ptr || uSnap.HasSurrendered)
                        continue;

                    float dSq = (uSnap.Pos - myPos).sqrMagnitude;
                    if (uSnap.Faction == myFaction)
                    {
                        if (dSq <= 22f * 22f)
                        {
                            Soldier u = uSnap.Ref;
                            if (u != null)
                            {
                                SoldierPersonality allyPers = GetPersonality(u);
                                if (uSnap.IsSquadLeader ||
                                    allyPers == SoldierPersonality.Leader_Spearhead ||
                                    allyPers == SoldierPersonality.Leader_OldGuard)
                                {
                                    // Inspiring officer nearby keeps wavering soldiers in the fight
                                    return false;
                                }
                            }
                        }

                        if (dSq <= 16f * 16f)
                        {
                            friendlyCloseCount++;
                            if (friendlyCloseCount > 1)
                                return false;
                        }
                    }
                    else if (dSq <= 18f * 18f && Mathf.Abs(uSnap.Pos.y - myPos.y) <= 6.5f)
                    {
                        enemyCloseCount++;
                        if (dSq < closestEnemyDistSq)
                            closestEnemyDistSq = dSq;
                    }
                }
            }

            Soldier player = Soldier.CurrentControlledSoldierOrNull();
            if (player != null && player.IsAlive && player.Pointer != ptr && !player.HasSurrended() && player.transform != null)
            {
                float pDistSq = (player.transform.position - myPos).sqrMagnitude;
                if (player.faction == myFaction)
                {
                    if (pDistSq <= 18f * 18f)
                        return false;
                }
                else if (pDistSq <= 18f * 18f)
                {
                    enemyCloseCount++;
                    if (pDistSq < closestEnemyDistSq)
                        closestEnemyDistSq = pDistSq;
                }
            }

            if (enemyCloseCount < 1 || friendlyCloseCount > 1)
            {
                return false;
            }

            bool isOverrun = enemyCloseCount > friendlyCloseCount ||
                             (closestEnemyDistSq <= 10.5f * 10.5f && isBleedingOrDry);
            if (!isOverrun)
            {
                return false;
            }

            // Before surrendering at mid-close range (> 11m), able-bodied isolated soldiers first attempt
            // an "Oh Shit!" break-contact sprint rearward (with smoke if carried) toward friendly lines/cover!
            if (VisionConfig.EnableTacticalRetreatsAndRegroup.Value &&
                !soldier.isBleeding &&
                closestEnemyDistSq > 11.0f * 11.0f &&
                TryTriggerImmediateIndividualFallback(soldier, myPos))
            {
                return true;
            }

            if (!AiCore.TryConsumeBotRpcBudget(0.60f))
            {
                return false;
            }

            soldier.SetGunTriggerPressed(false);
            soldier.SurrenderSynched();
            ShellShockSurrendersCount++;

            if (VisionConfig.DebugLogging.Value)
            {
                string reason = isSkittish ? "Skittish/Shell-Shocked" : "Green Rookie Overrun";
                AiCore.LogDebug("Surrender", $"{soldier.faction} soldier surrendered ({reason}, supp={suppression:F0}, enemiesWithin18m={enemyCloseCount}).");
            }

            return true;
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

                    IntPtr ptr = soldier.Pointer;
                    float now = Time.time;
                    _combatStanceHoldUntil[ptr] = now + 6.0f;

                    AddCombatExperienceBonus(soldier, 1.5f);

                    float vet01 = GetVeterancy01(soldier);
                    float curSupp = soldier.GetSuppressionValue();
                    SoldierPersonality pers = GetPersonality(soldier);

                    // Immediately expire GOAP plan inertia after a brief human startle (0.16s-0.43s) so shot-at bots
                    // re-evaluate their plan right away instead of continuing an old open-ground advance!
                    if (_botGoapPlans.TryGetValue(ptr, out BotGoapPlan curPlan))
                    {
                        float startle = 0.16f + ((AiCore.GetDeterministicHash(ptr, 211) & 15) * 0.018f);
                        curPlan.NextReplanTime = Mathf.Min(curPlan.NextReplanTime, now + startle);
                        _botGoapPlans[ptr] = curPlan;
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
                            bool scriptedCharge = cSq != null && cSq.isChargeSquad;
                            FactionCommandProfile cProf = GetFactionCommandProfile(soldier.faction);
                            BotMicroVariance cMicro = BotMicroVariance.FromSoldier(ptr);
                            bool fanatical = scriptedCharge || cProf.BayonetBreachCostMult <= 0.66f;
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
                        if (soughtCover && isCharging)
                        {
                            _localCoverProtectedUntil[ptr] = now + 5.5f;
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
                            AiCore.SoldierFastHandles sh = AiCore.GetOrCreateSoldierHandles(soldier);
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
                                if (remToDestSq > 10.5f * 10.5f)
                                {
                                    float haltDur = 1.85f + ((AiCore.GetDeterministicHash(ptr, 223) & 15) * 0.06f);
                                    _fireAndMovementStates[ptr] = new FireAndMovementState
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
                                bool canGoProne = (!_noProneUntil.TryGetValue(ptr, out float noProne) || now >= noProne) &&
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
        // PATCH 4: SoldierAI.UpdateMoveDirection (Wall, Trench-Exit & Stuck-Wire Vaulting)
        // =====================================================================
        internal struct BarbedWireStuckState
        {
            public Vector3 AnchorPos;
            public float FirstStuckTime;
            public float LastSeenTime;
        }

        internal static readonly Dictionary<IntPtr, bool> _barbedWireColliderCache = new Dictionary<IntPtr, bool>(256);
        internal static readonly Dictionary<IntPtr, BarbedWireStuckState> _botBarbedWireStuckCache = new Dictionary<IntPtr, BarbedWireStuckState>(128);

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
            Vector3 vaultPos,
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

            // 1. Make sure the bot's move destination actually crosses forward across the obstacle
            Vector3 toDest = ai.MoveDestination - myPos;
            toDest.y = 0f;
            if (toDest.sqrMagnitude > 0.36f && Vector3.Dot(fwd, toDest.normalized) < 0.45f)
            {
                vaultOrRejectReason = "not_moving_across";
                return false;
            }

            // 2. Front-face probe:
            // Native Soldier.CanVault (0x180742C40) detects the obstacle at pos + fwd*0.50f + up*0.50f (r=0.30f),
            // while vaultPos is probed at pos + fwd*1.12f (0x180742FCC) — which lands on the FAR-SIDE floor (vaultPos.y ≈ myPos.y)
            // for thin walls/fences, or on the upper parapet lip (vaultPos.y ≈ myPos.y + 0.6m..1.5m) for trench exits!
            int envMask = AiCore.GetSolidEnvironmentMask();
            Vector3 rayOriginLow = myPos + Vector3.up * 0.50f - fwd * 0.15f;
            if (!Physics.Raycast(rayOriginLow, fwd, out RaycastHit frontHit, 1.30f, envMask, QueryTriggerInteraction.Ignore))
            {
                Vector3 rayOriginMid = myPos + Vector3.up * 0.82f - fwd * 0.15f;
                if (!Physics.Raycast(rayOriginMid, fwd, out frontHit, 1.30f, envMask, QueryTriggerInteraction.Ignore))
                {
                    vaultOrRejectReason = "no_solid_face";
                    return false;
                }
            }

            Collider frontCol = frontHit.collider;
            bool frontIsTerrain = frontCol != null && frontCol.TryCast<TerrainCollider>() != null;
            float vaultLandingRise = vaultPos.y - myPos.y;

            if (frontIsTerrain)
            {
                // Carved terrain trench walls are steep unwalkable cuts (|normal.y| <= 0.62f, >= ~52 deg)
                // with a genuine step-up onto the parapet lip; ordinary hills/bumps are rejected.
                if (Mathf.Abs(frontHit.normal.y) > 0.62f || vaultLandingRise < 0.48f)
                {
                    vaultOrRejectReason = "terrain_slope";
                    return false;
                }
            }
            else if (Mathf.Abs(frontHit.normal.y) > 0.56f)
            {
                // Allow vertical walls AND battered wooden trench revetment planks / sandbags (|normal.y| <= 0.56f),
                // while rejecting walkable <= 45 deg sloped ramps (|normal.y| >= 0.707f).
                vaultOrRejectReason = "sloped_surface";
                return false;
            }

            // 3. Measure true obstacle crest height directly above the wall face (0.16m into the obstacle),
            // including TerrainCollider lips behind wooden trench revetments.
            float crestHeight = vaultLandingRise;
            Collider crestCol = null;
            Vector3 crestProbeOrigin = new Vector3(
                frontHit.point.x + fwd.x * 0.16f,
                myPos.y + 1.80f,
                frontHit.point.z + fwd.z * 0.16f);
            if (Physics.Raycast(crestProbeOrigin, Vector3.down, out RaycastHit crestHit, 1.54f, envMask, QueryTriggerInteraction.Ignore))
            {
                crestCol = crestHit.collider;
                float probedCrest = crestHit.point.y - myPos.y;
                if (probedCrest > crestHeight)
                    crestHeight = probedCrest;
            }

            // 3B. Barbed Wire Discipline:
            // Keep barbed wire unvaultable during normal movement so wire lines act as real tactical obstacles,
            // allowing wire clambering ONLY if a bot has been genuinely wedged against the wire for >= 3.5s!
            if (IsBarbedWireCollider(frontCol) || IsBarbedWireCollider(crestCol))
            {
                if (!_botBarbedWireStuckCache.TryGetValue(botPtr, out BarbedWireStuckState wireSt) ||
                    now - wireSt.LastSeenTime > 2.2f ||
                    (myPos - wireSt.AnchorPos).sqrMagnitude > 1.35f)
                {
                    _botBarbedWireStuckCache[botPtr] = new BarbedWireStuckState
                    {
                        AnchorPos = myPos,
                        FirstStuckTime = now,
                        LastSeenTime = now
                    };
                    vaultOrRejectReason = "barbed_wire";
                    return false;
                }

                wireSt.LastSeenTime = now;
                _botBarbedWireStuckCache[botPtr] = wireSt;
                if (now - wireSt.FirstStuckTime < 3.5f)
                {
                    vaultOrRejectReason = "barbed_wire";
                    return false;
                }

                _botBarbedWireStuckCache.Remove(botPtr);
                vaultOrRejectReason = "barbed_wire_unstuck";
            }

            if (crestHeight < 0.44f)
            {
                vaultOrRejectReason = "flat_or_low";
                return false;
            }
            if (crestHeight > 1.68f || vaultLandingRise > 1.68f)
            {
                vaultOrRejectReason = "too_high";
                return false;
            }

            // 4. Far-side grade & trench-lip level-off check:
            // Reject staircases or continuous hills/bluffs that keep rising beyond the lip, while allowing
            // genuine trench exits where the ground levels off onto the parapet / battlefield above!
            Vector3 farOrigin = myPos + fwd * (frontHit.distance + 0.88f) + Vector3.up * (crestHeight + 0.65f);
            if (Physics.Raycast(farOrigin, Vector3.down, out RaycastHit farHit, crestHeight + 1.85f, envMask, QueryTriggerInteraction.Ignore))
            {
                if (farHit.point.y > myPos.y + crestHeight + 0.18f)
                {
                    vaultOrRejectReason = "uphill_ramp";
                    return false;
                }

                bool farIsElevatedTerrain = farHit.collider != null &&
                                            farHit.collider.TryCast<TerrainCollider>() != null &&
                                            farHit.point.y > myPos.y + 0.35f;
                if (frontIsTerrain || farIsElevatedTerrain)
                {
                    if (farHit.normal.y < 0.80f)
                    {
                        vaultOrRejectReason = "terrain_hillside";
                        return false;
                    }

                    Vector3 beyondOrigin = myPos + fwd * (frontHit.distance + 1.48f) + Vector3.up * (crestHeight + 0.85f);
                    if (Physics.Raycast(beyondOrigin, Vector3.down, out RaycastHit beyondHit, crestHeight + 2.05f, envMask, QueryTriggerInteraction.Ignore) &&
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

        [HarmonyPatch(typeof(SoldierAI), nameof(SoldierAI.UpdateMoveDirection))]
        public static class SoldierAI_UpdateMoveDirection_Patch
        {
            public static void Postfix(SoldierAI __instance)
            {
                long profStart = HitchProfiler.Begin();
                try
                {
                    if (!VisionConfig.EnableMod.Value || !VisionConfig.EnableWallVaulting.Value || ReferenceEquals(__instance, null))
                        return;

                    AiCore.SoldierFastHandles h = AiCore.GetOrCreateSoldierHandlesByAi(__instance);
                    if (h == null)
                        return;

                    IntPtr ptr = h.Ptr;
                    float now = Time.time;
                    if (_nextVaultCheckTime.TryGetValue(ptr, out float nextVault) && now < nextVault)
                        return;

                    if (!AiCore.IsLocallyOwnedBot(h) || h.Ref.IsOnVehicle())
                    {
                        _nextVaultCheckTime[ptr] = now + 1.50f;
                        return;
                    }

                    _nextVaultCheckTime[ptr] = now + 0.50f;
                    Soldier soldier = h.Ref;

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
                        if (!IsValidVaultObstacle(__instance, soldier, ptr, h.Tr, vaultPos, now, out string vaultOrRejectReason))
                        {
                            SlopedVaultsRejectedCount++;
                            _nextVaultCheckTime[ptr] = now + (vaultOrRejectReason == "barbed_wire" ? 0.55f : 0.85f);
                            BotActionTrace.RecordEvent(soldier, BotTraceEventKind.VaultRejected, vaultOrRejectReason);
                            return;
                        }

                        if (AiCore.TryConsumeBotRpcBudget(0.35f))
                        {
                            soldier.Vault(vaultPos);
                            _nextVaultCheckTime[ptr] = now + 2.20f;
                            WallsVaulted++;
                            BotActionTrace.RecordEvent(soldier, BotTraceEventKind.Vault, vaultOrRejectReason);
                        }
                    }
                }
                catch { }
                finally
                {
                    HitchProfiler.End(HitchBucket.UpdateMoveDir, profStart);
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

                    bool bridgeEnabled = VisionConfig.EnableBridgeCrossingDiscipline.Value;
                    bool ffEnabled = VisionConfig.EnableFriendlyFireAvoidance.Value;
                    if (!bridgeEnabled && !ffEnabled)
                        return;

                    AiCore.SoldierFastHandles h = AiCore.GetOrCreateSoldierHandlesByAi(__instance);
                    if (!AiCore.IsLocallyOwnedBot(h) || h.Tr == null || h.Ref.IsOnVehicle())
                        return;

                    Soldier soldier = h.Ref;
                    Vector3 myPos = h.Tr.position;
                    Vector3 moveDest = __instance.MoveDestination;
                    bool onBridgeSpan = false;

                    if (bridgeEnabled && (__result - moveDest).sqrMagnitude > 1.0f)
                    {
                        Vector3 midPos = Vector3.Lerp(myPos, moveDest, 0.5f);

                        // Cancel vanilla's 5m-35m sideways waypoint scatter if the soldier is on a bridge,
                        // moving onto/across a bridge, or if the laterally-scattered waypoint lands over a
                        // river/ravine/drop-off (> 2.1m below __result.y) where CPathFinding.A would snap
                        // the path down to riverbed/water nodes instead of the bridge deck.
                        bool cancelLateralScatter =
                            AiCore.IsOnBridgeOrNarrowCrossing(myPos) ||
                            AiCore.IsOnBridgeOrNarrowCrossing(moveDest) ||
                            AiCore.IsOnBridgeOrNarrowCrossing(midPos) ||
                            AiCore.IsOnBridgeOrNarrowCrossing(__result) ||
                            !Physics.Raycast(
                                __result + Vector3.up * 1.5f,
                                Vector3.down,
                                3.6f,
                                AiCore.GetSolidEnvironmentMask(),
                                QueryTriggerInteraction.Ignore);

                        if (cancelLateralScatter)
                        {
                            __result = moveDest;
                            onBridgeSpan = true;
                            IntPtr ptr = h.Ptr;
                            if (ptr != IntPtr.Zero &&
                                (!_nextBridgeAssistCountTime.TryGetValue(ptr, out float nextCount) || Time.time >= nextCount))
                            {
                                _nextBridgeAssistCountTime[ptr] = Time.time + 8.0f;
                                BridgeCrossingsAssistedCount++;
                                if (VisionConfig.DebugLogging.Value)
                                {
                                    AiCore.LogDebug("BridgeDiscipline", $"{soldier.faction} soldier kept on bridge/causeway centerline (cancelled 5-35m lateral squad scatter).");
                                }
                            }
                        }
                    }

                    // Keep squad-follower formation slots out of friendly human players' aiming lanes!
                    if (ffEnabled &&
                        AiCore.TryGetBlockingFriendlyPlayerAimLane(
                            h.Ptr,
                            AiCore.GetCachedFaction(h, Time.time),
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
                    if (_rotateFireFrameCache.TryGetValue(ptr, out RotateAndFireFrameState prevFrameState) &&
                        prevFrameState.Frame == curFrame)
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

                    _combatStanceHoldUntil[ptr] = now + 6.0f;
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
                            if (!_vehicleAimCache.TryGetValue(ptr, out VehicleAimCache aimCache) ||
                                aimCache.VehiclePtr != vehPtr ||
                                now >= aimCache.ExpireTime)
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
                                _vehicleAimCache[ptr] = aimCache;
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
                            else
                            {
                                __instance.targetInWeaponRange = false;
                                __instance.allowFireAtEnemy = false;
                                __instance.aimingEnemy = false;
                                __instance.shotFiresEnd = 0f;
                                soldier.SetGunTriggerPressed(false);
                                soldier.pointGunEndTime = Mathf.Min(soldier.pointGunEndTime, now);
                                if (!_nextTankDisciplineCountTime.TryGetValue(ptr, out float nextCount) || now >= nextCount)
                                {
                                    _nextTankDisciplineCountTime[ptr] = now + 3.0f;
                                    UselessTankFirePrevented++;
                                }
                                _rotateFireFrameCache[ptr] = new RotateAndFireFrameState
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
                    bool isPeeking = _peekRestoreTime.TryGetValue(ptr, out float peekEnd) && now < peekEnd;
                    bool isHighBipodStanding = _bipodStandUntil.TryGetValue(ptr, out float standUntil) && now < standUntil;
                    bool isGunner = AiCore.GetCachedGunnerAndBipod(h, now, out AttachmentBipod bipod);
                    bool isOnBridge = VisionConfig.EnableBridgeCrossingDiscipline.Value &&
                                      !soldier.IsOnVehicle() &&
                                      AiCore.IsOnBridgeOrNarrowCrossing(myPos);
                    if (isOnBridge)
                    {
                        _noProneUntil[ptr] = now + 3.5f;
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
                        _infantryTargetReaction.TryGetValue(ptr, out InfantrySightReactionState rState);
                        if (curTgtPtr != rState.LastTargetPtr || (now - rState.LastSeenTime) > 2.2f)
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
                        _infantryTargetReaction[ptr] = rState;
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
                            bool canCheckCrouchLof = curSupp >= 32f ||
                                                     !_nextStandCrouchLofCheckTime.TryGetValue(ptr, out float nextLofCheck) ||
                                                     now >= nextLofCheck;
                            if (canCheckCrouchLof)
                            {
                                _nextStandCrouchLofCheckTime[ptr] = now + 0.25f;
                                if (curSupp >= 32f || HasClearLineOfFireFromCrouch(soldier, __0, dist))
                                {
                                    bool canGoProne = (!_noProneUntil.TryGetValue(ptr, out float noProne) || now >= noProne) &&
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

                                    if (!_nextStanceDropAllowed.TryGetValue(ptr, out float nextAllowed) || now >= nextAllowed)
                                    {
                                        _stanceDropHoldFireUntil[ptr] = now + 0.30f;
                                        _nextStanceDropAllowed[ptr] = now + 3.5f;
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

                        if (_stanceDropHoldFireUntil.TryGetValue(ptr, out float holdUntil) && now < holdUntil)
                        {
                            blockFire = true;
                        }
                    }

                    if (VisionConfig.EnableWallShootingPrevention.Value && !soldier.IsOnVehicle())
                    {
                        if (!_nextWallCheckTime.TryGetValue(ptr, out float nextWall) || now >= nextWall)
                        {
                            float angleToTarget = AiCore.HorizontalAngleToDirection(soldier, __0 - myPos);
                            if (angleToTarget <= 35f)
                            {
                                _nextWallCheckTime[ptr] = now + 0.25f;
                                if (IsMuzzleBlockedByWall(soldier, __0, dist))
                                {
                                    _wallBlockedUntil[ptr] = now + 0.28f;
                                    WallsBlocked++;

                                    bool canPeekNow = (!_nextPeekAllowedTime.TryGetValue(ptr, out float nextPeek) || now >= nextPeek) &&
                                                      (!_poseLockUntil.TryGetValue(ptr, out float poseLock) || now >= poseLock);
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
                                                _noProneUntil[ptr] = now + 10.0f;
                                                _combatStanceHoldUntil[ptr] = now + 6.0f;
                                                _peekRestoreTime[ptr] = now + 5.0f;
                                                _nextStanceDropAllowed[ptr] = now + 5.0f;
                                                AutoPeeksTriggered++;
                                            }
                                            else if (standClear)
                                            {
                                                SetBotPose(__instance, soldier, SoldierPose.Idle, true);
                                                _noProneUntil[ptr] = now + 10.0f;
                                                _combatStanceHoldUntil[ptr] = now + 6.0f;
                                                _peekRestoreTime[ptr] = now + peekDur;
                                                _nextStanceDropAllowed[ptr] = now + peekDur;
                                                if (isGunner && bipod != null)
                                                {
                                                    _bipodStandUntil[ptr] = now + 4.5f;
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
                                                _noProneUntil[ptr] = now + 10.0f;
                                                _combatStanceHoldUntil[ptr] = now + 6.0f;
                                                _peekRestoreTime[ptr] = now + peekDur;
                                                _nextStanceDropAllowed[ptr] = now + peekDur;
                                                if (isGunner && bipod != null)
                                                {
                                                    _bipodStandUntil[ptr] = now + 4.5f;
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

                        if (_wallBlockedUntil.TryGetValue(ptr, out float wallUntil) && now < wallUntil)
                        {
                            blockFire = true;
                            wallBlocked = true;
                        }
                    }

                    if (VisionConfig.EnableFriendlyFireAvoidance.Value)
                    {
                        if (!_nextFFCheckTime.TryGetValue(ptr, out float nextFF) || now >= nextFF)
                        {
                            _nextFFCheckTime[ptr] = now + 0.25f;
                            if (IsFriendlyInLineOfFire(soldier, __0, dist, vet01, out _, out Soldier blockingFriend))
                            {
                                _ffBlockedUntil[ptr] = now + 0.30f;
                                FriendlyFirePrevented++;
                                if (blockingFriend != null)
                                {
                                    TryNudgeFriendlyOutOfFireCone(soldier, blockingFriend, __0);
                                }
                            }
                        }

                        if (_ffBlockedUntil.TryGetValue(ptr, out float ffUntil) && now < ffUntil)
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
                        if (!_nextAntiStackTime.TryGetValue(ptr, out float nextAS) || now >= nextAS)
                        {
                            _nextAntiStackTime[ptr] = now + 2.5f;
                            if (TryGetAntiStackPush(soldier, out Vector3 pushDir))
                            {
                                if (!AiCore.IsDirectionBlockedByEnvironment(myPos + Vector3.up * 0.75f, pushDir, 1.50f, h.RootTr))
                                {
                                    __instance.MoveDirectlyToward(myPos + pushDir * 1.40f, 0.80f);
                                    _nextAntiStackTime[ptr] = now + 6.0f;
                                    AntiStackNudges++;
                                }
                            }
                        }
                    }

                    _rotateFireFrameCache[ptr] = new RotateAndFireFrameState
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

                    IntPtr ptr = soldier.Pointer;
                    float now = Time.time;

                    if (soldier.IsCharging() &&
                        (!_localCoverProtectedUntil.TryGetValue(ptr, out float chargeBalkLock) || now >= chargeBalkLock))
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

                    if (!soldier.HasDestinationAssigned || soldier.targetDestination == null)
                    {
                        return true;
                    }

                    bool protectedCover =
                        (VisionConfig.EnableTacticalGoapPlanner.Value && HasActiveGoapCoverLock(ptr, now)) ||
                        (_localCoverProtectedUntil.TryGetValue(ptr, out float protUntil) && now < protUntil) ||
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
                        // Cap uncached IsPositionOnExposedSideOfWall raycast sweeps to <= 12 per frame
                        // so a CoverManager.GetCovers sweep in a virgin area never spikes frame time!
                        int curFrame = Time.frameCount;
                        if (curFrame != _coverExposureProbeFrame)
                        {
                            _coverExposureProbeFrame = curFrame;
                            _coverExposureUncachedProbesThisFrame = 0;
                        }
                        if (_coverExposureUncachedProbesThisFrame >= 12)
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
            public static void Prefix(Squad __instance)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value ||
                        !VisionConfig.EnableSmartWallSideCover.Value ||
                        __instance == null)
                    {
                        return;
                    }

                    if (__instance.Leader != null && !AiCore.IsLocallyOwnedBot(__instance.Leader))
                        return;

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

                    IntPtr botPtr = __instance.Pointer;
                    float now = Time.time;

                    if (_nextBotDoorCheckTime.TryGetValue(botPtr, out float nextBotCheck) && now < nextBotCheck)
                        return false;

                    _nextBotDoorCheckTime[botPtr] = now + 0.30f;

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
                                if (!onCooldown &&
                                    !isShutterVulnerable &&
                                    distSq <= 6.76f &&
                                    AiCore.TryConsumeBotRpcBudget(0.25f))
                                {
                                    door.SetDoor(true);
                                    _nextDoorToggleTime[doorPtr] = now + 3.5f;
                                    InvalidateWatchWindowCache(botPtr);
                                }
                            }
                            else
                            {
                                bool walkingThrough = isMoving && Vector3.Dot(moveDir, toDoorDir) > 0.20f;
                                bool facingClose = distSq <= 1.96f && Vector3.Dot(botFwd, toDoorDir) > 0.35f;
                                bool clearingFiringLane = !isMoving &&
                                                          !onCooldown &&
                                                          !isDoorVulnerable &&
                                                          distSq <= 6.25f &&
                                                          threatDir.sqrMagnitude > 0.01f &&
                                                          Vector3.Dot(toDoorDir, threatDir) > 0.45f &&
                                                          Vector3.Dot(botFwd, toDoorDir) > 0.35f &&
                                                          AiCore.TryConsumeBotRpcBudget(0.25f);
                                if (walkingThrough || facingClose || clearingFiringLane)
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

                            if (shouldClose && AiCore.TryConsumeBotRpcBudget(0.35f))
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
                    _botGoapPlans.Remove(ptr);
                    _botScavengeCaches.Remove(ptr);
                    _fireAndMovementStates.Remove(ptr);
                    _localCoverProtectedUntil.Remove(ptr);
                    _individualFallbackUntil.Remove(ptr);
                    _individualFallbackDest.Remove(ptr);
                    _isTurningToWatchSector.Remove(ptr);
                    _ffBlockedUntil.Remove(ptr);
                    _wallBlockedUntil.Remove(ptr);

                    if (__instance.IsOnVehicle())
                    {
                        Vehicle veh = __instance.GetCurrentVehicle();
                        if (veh != null && veh.IsAirVehicle())
                        {
                            VehiclePlane vp = veh.TryCast<VehiclePlane>();
                            if (vp != null)
                            {
                                TryHotStartAirbornePlane(vp, false);
                            }
                        }
                    }
                }
                catch { }
            }
        }

        // =====================================================================
        // Vanilla AI Fix: Anti-Dogpile Medic Triage Reservation (Squad.MedicRoutine)
        // In vanilla Squad.MedicRoutine (0x1808079B0), every medic calls character.GetNearestInjured()
        // and creates a new MedicHealingDestination(injured, character) with ZERO reservation check,
        // causing 3-4 medics to dogpile the same wounded soldier while other casualties bleed out.
        // This patch caps each casualty to 1 medic (or 2 max ONLY if the primary medic is > 20m away
        // or heavily suppressed >= 40f), IMMEDIATELY releases claims if the medic dies, goes down
        // (IsInjured), surrenders, or drops the task, and spreads extra medics to other wounded allies!
        // =====================================================================
        internal struct MedicPatientClaim
        {
            public Soldier PrimaryMedic;
            public Soldier BackupMedic;
        }

        internal static readonly Dictionary<IntPtr, MedicPatientClaim> _medicPatientClaims = new Dictionary<IntPtr, MedicPatientClaim>(32);
        private static readonly List<IntPtr> _reusableDeadPatientPtrs = new List<IntPtr>(32);
        public static int MedicTriageRedirectsCount;

        private static bool IsMedicClaimStillActiveForPatient(Soldier medic, IntPtr patientPtr)
        {
            AiCore.SoldierFastHandles mh = AiCore.GetOrCreateSoldierHandles(medic);
            if (mh == null ||
                !medic.IsAlive ||
                medic.IsInjured ||
                medic.HasSurrended() ||
                medic.IsOnVehicle() ||
                medic.IsCarried())
            {
                return false;
            }

            if (AiCore.ResolveTargetDestination(mh) != AiCore.CachedDestKind.MedicHeal || mh.CachedHealDest == null)
                return false;

            MedicHealingDestination healDest = mh.CachedHealDest;
            if (healDest.soldierToHealUp == null ||
                healDest.soldierToHealUp.Pointer != patientPtr ||
                healDest.soldierToHealUp.IsDead ||
                !healDest.soldierToHealUp.IsInjured ||
                healDest.soldierToHealUp.IsCarried())
            {
                return false;
            }

            return true;
        }

        private static void PruneAndValidateMedicClaims(IntPtr selfMedicPtr, out IntPtr alreadyClaimedPatientPtr)
        {
            alreadyClaimedPatientPtr = IntPtr.Zero;
            if (_medicPatientClaims.Count == 0)
                return;

            _reusableDeadPatientPtrs.Clear();
            foreach (var kvp in _medicPatientClaims)
            {
                IntPtr patPtr = kvp.Key;
                MedicPatientClaim claim = kvp.Value;

                bool p1Valid = IsMedicClaimStillActiveForPatient(claim.PrimaryMedic, patPtr);
                bool p2Valid = IsMedicClaimStillActiveForPatient(claim.BackupMedic, patPtr);

                if (!p1Valid && p2Valid)
                {
                    claim.PrimaryMedic = claim.BackupMedic;
                    claim.BackupMedic = null;
                    p1Valid = true;
                    p2Valid = false;
                }
                else if (!p2Valid)
                {
                    claim.BackupMedic = null;
                }

                if (!p1Valid)
                {
                    _reusableDeadPatientPtrs.Add(patPtr);
                }
                else
                {
                    _medicPatientClaims[patPtr] = claim;
                    if (claim.PrimaryMedic != null && claim.PrimaryMedic.Pointer == selfMedicPtr)
                        alreadyClaimedPatientPtr = patPtr;
                    else if (claim.BackupMedic != null && claim.BackupMedic.Pointer == selfMedicPtr)
                        alreadyClaimedPatientPtr = patPtr;
                }
            }

            for (int i = 0; i < _reusableDeadPatientPtrs.Count; i++)
            {
                _medicPatientClaims.Remove(_reusableDeadPatientPtrs[i]);
            }
            _reusableDeadPatientPtrs.Clear();
        }

        [HarmonyPatch(typeof(Squad), nameof(Squad.MedicRoutine))]
        public static class Squad_MedicRoutine_Patch
        {
            public static bool Prefix(SoldierAI ai, Soldier character, out long __state)
            {
                __state = HitchProfiler.BeginTopLevel();
                try
                {
                    if (!VisionConfig.EnableMod.Value ||
                        !VisionConfig.EnableAntiDogpileMedicTriage.Value ||
                        ReferenceEquals(character, null) ||
                        character.Pointer == IntPtr.Zero)
                    {
                        return true;
                    }

                    AiCore.SoldierFastHandles mh = AiCore.GetOrCreateSoldierHandles(character);
                    if (!AiCore.IsLocallyOwnedBot(mh))
                        return true;

                    // Match vanilla Squad.MedicRoutine (@ 0x1808079B0): check !hasSyringe FIRST before pruning global medic claims!
                    if (!character.hasSyringe ||
                        !character.IsAlive ||
                        character.IsInjured ||
                        (character.Params != null && (!character.Params.canDoMedic || !character.Params.canMove)) ||
                        character.IsCharging() ||
                        character.isInBodyAnimation ||
                        character.HasSurrended() ||
                        character.IsOnVehicle() ||
                        character.IsCarryingBody() ||
                        mh.Tr == null)
                    {
                        return false;
                    }

                    IntPtr medicPtr = mh.Ptr;
                    PruneAndValidateMedicClaims(medicPtr, out IntPtr activePatientPtr);

                    // If this medic is already actively heading to a valid incapacitated (!IsDead && IsInjured) patient, let him continue!
                    if (activePatientPtr != IntPtr.Zero &&
                        AiCore.ResolveTargetDestination(mh) == AiCore.CachedDestKind.MedicHeal &&
                        mh.CachedHealDest != null)
                    {
                        MedicHealingDestination curHeal = mh.CachedHealDest;
                        if (curHeal.soldierToHealUp != null &&
                            !curHeal.soldierToHealUp.IsDead &&
                            curHeal.soldierToHealUp.IsInjured &&
                            !curHeal.soldierToHealUp.IsCarried())
                        {
                            return false;
                        }
                    }

                    Vector3 medicPos = mh.Tr.position;
                    string myFaction = AiCore.GetCachedFaction(mh, Time.time);
                    if (string.IsNullOrEmpty(myFaction))
                        return true;

                    AiCore.EnsureWorldSnapshot();
                    int injCount = AiCore.WorldInjuredSoldierCount;
                    var injuredSoldiers = AiCore.WorldInjuredSoldiers;

                    Soldier bestPatient = null;
                    float bestScoreDistSq = 55.0f * 55.0f;
                    bool skippedDogpiledNearest = false;

                    for (int i = 0; i < injCount; i++)
                    {
                        ref AiCore.SoldierSnapshot sSnap = ref injuredSoldiers[i];
                        if (!sSnap.IsInjured || sSnap.Ptr == medicPtr || sSnap.IsOnVehicle || sSnap.Ref == null)
                            continue;

                        if (!AiCore.AreSameSideFactions(myFaction, sSnap.Faction))
                            continue;

                        Soldier pat = sSnap.Ref;
                        if (pat.IsDead || !pat.IsInjured || pat.IsCarried() || pat.IsOnVehicle())
                            continue;

                        float dSq = (sSnap.Pos - medicPos).sqrMagnitude;
                        if (dSq > 55.0f * 55.0f)
                            continue;

                        bool isBackupSlot = false;
                        if (_medicPatientClaims.TryGetValue(sSnap.Ptr, out MedicPatientClaim existingClaim) &&
                            existingClaim.PrimaryMedic != null &&
                            existingClaim.PrimaryMedic.Pointer != medicPtr)
                        {
                            // Hard cap of 2 medics max per casualty (and only allow a 2nd medic if the 1st medic is > 20m away or heavily suppressed >= 40f!)
                            if (existingClaim.BackupMedic != null && existingClaim.BackupMedic.Pointer != medicPtr)
                            {
                                skippedDogpiledNearest = true;
                                continue;
                            }

                            Soldier p1 = existingClaim.PrimaryMedic;
                            float p1DistSq = p1.transform != null ? (p1.transform.position - sSnap.Pos).sqrMagnitude : 0f;
                            bool p1NeedsBackup = p1DistSq > 20.0f * 20.0f || p1.GetSuppressionValue() >= 40.0f;
                            if (!p1NeedsBackup)
                            {
                                skippedDogpiledNearest = true;
                                continue;
                            }
                            isBackupSlot = true;
                        }

                        // Strongly prefer completely unclaimed casualties over adding a 2nd backup medic
                        float effectiveDistSq = isBackupSlot ? (dSq * 2.5f + 225.0f) : dSq;
                        if (effectiveDistSq < bestScoreDistSq)
                        {
                            bestScoreDistSq = effectiveDistSq;
                            bestPatient = pat;
                        }
                    }

                    if (bestPatient != null)
                    {
                        IntPtr bestPtr = bestPatient.Pointer;
                        _medicPatientClaims.TryGetValue(bestPtr, out MedicPatientClaim claim);
                        if (claim.PrimaryMedic == null || claim.PrimaryMedic.Pointer == medicPtr)
                            claim.PrimaryMedic = character;
                        else
                            claim.BackupMedic = character;
                        _medicPatientClaims[bestPtr] = claim;

                        MedicHealingDestination newHealDest = new MedicHealingDestination(bestPatient, character);
                        character.CoverPosition(newHealDest.Cast<AiDestination>());

                        if (skippedDogpiledNearest)
                        {
                            MedicTriageRedirectsCount++;
                            AiCore.LogDebug("MedicTriage", $"{myFaction} medic redirected to secondary casualty (avoided dogpile).");
                        }
                    }
                    else
                    {
                        if (character.targetDestination != null && character.targetDestination.TryCast<MedicHealingDestination>() != null)
                        {
                            character.CoverPosition(null);
                        }
                        if (skippedDogpiledNearest)
                        {
                            // All nearby wounded already have a healthy nearby medic (or 2 max): keep rifle up in cover!
                            MedicTriageRedirectsCount++;
                        }
                    }

                    return false;
                }
                catch { }
                return true;
            }

            public static void Postfix(long __state)
            {
                HitchProfiler.End(HitchBucket.VanMedicRtn, __state);
            }
        }

        // =====================================================================
        // Vanilla AI Fix: Handheld Knee-Mortar Danger-Close Friendly-Fire Guard
        // In vanilla SoldierAI.MortarTarget (0x18055CA60), mortar bots pick the single closest
        // spotted enemy (or keep shelling Squad.mortarOrderPos) with ZERO friendly-troop blast check.
        // Redirect danger-close mortar aims to a spotted enemy cluster clear of friendlies, or hold fire!
        // =====================================================================
        [HarmonyPatch(typeof(SoldierAI), nameof(SoldierAI.MortarTarget))]
        public static class SoldierAI_MortarTarget_Patch
        {
            public static void Postfix(SoldierAI __instance, ref float __0, ref Spottable __result)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value ||
                        VisionConfig.FriendlyFireConeDegrees.Value <= 0.1f ||
                        __instance == null ||
                        __result == null ||
                        __result.Pointer == IntPtr.Zero)
                    {
                        return;
                    }

                    AiCore.SoldierFastHandles sh = AiCore.GetOrCreateSoldierHandlesByAi(__instance);
                    if (!AiCore.IsLocallyOwnedBot(sh) || sh.Tr == null)
                        return;

                    Soldier soldier = sh.Ref;
                    string myFaction = AiCore.GetCachedFaction(sh, Time.time);
                    if (string.IsNullOrEmpty(myFaction))
                        return;

                    Vector3 targetPos = __result.GetCenterOfUnit();
                    if (!AiCore.IsIndirectFireImpactDangerClose(myFaction, targetPos, sh.Ptr, 29.0f, 35.0f))
                        return;

                    FriendlyFirePrevented++;

                    KneeMortar mortar = __instance.HeldMortar;
                    Spottable bestSafeTarget = null;
                    float bestSafeDist = 0f;
                    float bestScore = -9999f;

                    if (mortar != null && mortar.Pointer != IntPtr.Zero)
                    {
                        RefreshCombinedArmsIntelIfNeeded();
                        Vector3 myPos = sh.Tr.position;
                        int clusterCount = _knownEnemyClusters.Count;

                        for (int i = 0; i < clusterCount; i++)
                        {
                            KnownEnemyCluster cluster = _knownEnemyClusters[i];
                            if (AiCore.AreSameSideFactions(myFaction, cluster.Faction))
                                continue;

                            for (int pass = 0; pass < 2; pass++)
                            {
                                Spottable cand = pass == 0 ? cluster.PrimarySoldier : cluster.SecondarySoldier;
                                if (!AiCore.IsValidAliveSpottable(cand))
                                    continue;

                                Vector3 candPos = cand.GetCenterOfUnit();
                                float d = Vector3.Distance(myPos, candPos);
                                if (!mortar.InRange(soldier, d))
                                    continue;

                                if (AiCore.IsIndirectFireImpactDangerClose(myFaction, candPos, sh.Ptr, 29.0f, 35.0f))
                                    continue;

                                float score = cluster.BasePriority - d * 0.15f;
                                if (score > bestScore)
                                {
                                    bestScore = score;
                                    bestSafeTarget = cand;
                                    bestSafeDist = d;
                                }
                                break;
                            }
                        }
                    }

                    if (bestSafeTarget != null)
                    {
                        __result = bestSafeTarget;
                        __0 = bestSafeDist;
                        AiCore.LogDebug("MortarFFSaved", $"{myFaction} mortarman shifted target away from danger-close friendlies ({bestSafeDist:F0}m).", 5.0f);
                    }
                    else
                    {
                        __result = null;
                        __0 = 0f;
                        AiCore.LogDebug("MortarFFSaved", $"{myFaction} mortarman held fire (all targets danger-close to friendlies).", 5.0f);
                    }
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(KneeMortar), nameof(KneeMortar.Use))]
        public static class KneeMortar_Use_Patch
        {
            public static bool Prefix(KneeMortar __instance, Creature __0, ref bool __result)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value ||
                        VisionConfig.FriendlyFireConeDegrees.Value <= 0.1f ||
                        __instance == null ||
                        __0 == null)
                    {
                        return true;
                    }

                    Soldier soldier = __0.TryCast<Soldier>();
                    if (!AiCore.IsLocallyOwnedBot(soldier) || soldier.transform == null || string.IsNullOrEmpty(soldier.faction))
                        return true;

                    float range = Mathf.Max(20.0f, __instance.CurrentRange);
                    Vector3 flatFwd = soldier.transform.forward;
                    flatFwd.y = 0f;
                    flatFwd = flatFwd.sqrMagnitude > 0.001f ? flatFwd.normalized : Vector3.forward;

                    Vector3 predictedImpact = soldier.transform.position + flatFwd * range;
                    if (AiCore.IsIndirectFireImpactDangerClose(soldier.faction, predictedImpact, soldier.Pointer, 27.0f, 34.0f))
                    {
                        FriendlyFirePrevented++;
                        __result = false;
                        return false;
                    }
                }
                catch { }
                return true;
            }
        }
    }
}
