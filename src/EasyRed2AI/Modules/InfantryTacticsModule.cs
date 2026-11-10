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
        private static readonly Dictionary<IntPtr, float> _nextIndoorCeilingCountTime = new Dictionary<IntPtr, float>(256);
        private static readonly Dictionary<IntPtr, float> _nextBoundingHoldCountTime = new Dictionary<IntPtr, float>(256);
        private static readonly Dictionary<IntPtr, float> _nextBridgeAssistCountTime = new Dictionary<IntPtr, float>(256);
        private static readonly Dictionary<IntPtr, float> _nextSurrenderCheckTime = new Dictionary<IntPtr, float>(256);
        private static readonly Dictionary<IntPtr, float> _nextLocalCoverSeekTime = new Dictionary<IntPtr, float>(256);
        private static readonly Dictionary<IntPtr, float> _localCoverProtectedUntil = new Dictionary<IntPtr, float>(256);
        private static readonly HashSet<IntPtr> _bayonetCheckedSoldiers = new HashSet<IntPtr>();
        private static readonly Dictionary<long, bool> _coverExposureCache = new Dictionary<long, bool>(1024);

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

        public static void ClearDoorCaches()
        {
            if (_colliderDoorCache.Count > 512)
                _colliderDoorCache.Clear();
            if (_nextDoorToggleTime.Count > 256)
                _nextDoorToggleTime.Clear();
            if (_liberateMoveStartTime.Count > 256)
                _liberateMoveStartTime.Clear();
            if (_liberateMoveCooldownUntil.Count > 256)
                _liberateMoveCooldownUntil.Clear();
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
            string myFaction = shooter.faction;
            IntPtr myPtr = shooter.Pointer;

            Soldier player = Soldier.CurrentControlledSoldierOrNull();
            if (player != null && player.IsAlive && player.Pointer != myPtr && player.faction == myFaction)
            {
                if (CheckSingleFriendlyInCone(gunPos, fireDir, maxCheckDist, allowedConeDeg, corridorWidth, shooter, player, out sidestepDir))
                {
                    blockingFriend = player;
                    return true;
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
                    if (f == null || !f.IsAlive || f.Pointer == myPtr) continue;
                    if (CheckSingleFriendlyInCone(gunPos, fireDir, maxCheckDist, allowedConeDeg, corridorWidth, shooter, f, out sidestepDir))
                    {
                        blockingFriend = f;
                        return true;
                    }
                }
            }

            AiCore.EnsureWorldSnapshot();
            int sqCount = AiCore.WorldSquadCount;
            if (sqCount > 0)
            {
                IntPtr mySqPtr = mySquad != null ? mySquad.Pointer : IntPtr.Zero;
                float maxDistWithMarginSq = (maxCheckDist + 2.2f) * (maxCheckDist + 2.2f);
                var squads = AiCore.WorldSquads;
                var soldiers = AiCore.WorldSoldiers;

                for (int s = 0; s < sqCount; s++)
                {
                    ref AiCore.SquadSnapshot sqSnap = ref squads[s];
                    if (sqSnap.Ptr == mySqPtr || sqSnap.UnitCount == 0 || sqSnap.Faction != myFaction)
                        continue;

                    if ((sqSnap.RefPos - gunPos).sqrMagnitude > 75f * 75f)
                        continue;

                    int end = sqSnap.UnitStartIndex + sqSnap.UnitCount;
                    for (int i = sqSnap.UnitStartIndex; i < end; i++)
                    {
                        ref AiCore.SoldierSnapshot solSnap = ref soldiers[i];
                        if (solSnap.Ptr == myPtr)
                            continue;

                        // Pure C# L1-cache cull before crossing the IL2CPP boundary for GetCenterOfUnit()
                        Vector3 approxDelta = (solSnap.Pos + new Vector3(0f, 0.9f, 0f)) - gunPos;
                        float approxSq = approxDelta.sqrMagnitude;
                        if (approxSq > maxDistWithMarginSq || Vector3.Dot(approxDelta, fireDir) <= -0.5f)
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

            if (angle <= allowedConeDeg || (forwardDist <= 8.0f && lateralDist <= corridorWidth))
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

            if (friendAi == null || friend.IsOnCover())
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
                friendAi.moveCharacter = true;
                TrySayTacticalVoice(friend, friend.joinedSquad, VoiceManager.VoiceClip.watchYourFire, 0.05f);
            }
            else if ((friendAi.MoveDestination - friendPos).sqrMagnitude <= 9.0f * 9.0f)
            {
                friendAi.MoveDestination += stepDir * 1.65f;
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

            if (soldier.UsingBipod() || soldier.IsBipodTransitioning())
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

        private static bool IsMachineGunnerOrHasBipod(Soldier soldier, out AttachmentBipod bipod)
        {
            bipod = null;
            if (soldier == null)
                return false;

            bipod = soldier.GetHeldFunctionalBipod();
            return bipod != null || soldier.hasMg || soldier.IsGunner();
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

                Vehicle v = dest.TryCast<Vehicle>();
                if (v == null || v.Pointer == IntPtr.Zero || v.transform == null || v.IsAirVehicle())
                    continue;

                bool isTank = v.TryCast<VehicleTank>() != null;
                if (!isTank && GetEffectiveVehicleExplosionArmor(v) < 8.0f)
                    continue;

                bool destroyed = v.IsCoverDestroyed();
                if (!destroyed)
                {
                    string vf = GetVehicleCrewFaction(v);
                    if (!string.IsNullOrEmpty(vf) && !ResourcesManager.IsSameFaction(vf, soldier.faction))
                        continue; // Never try to crouch right behind a live enemy tank!
                }

                Vector3 vPos = v.transform.position;
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

        private static bool TrySeekNearbyCover(SoldierAI ai, Soldier soldier)
        {
            if (ai == null || soldier == null || soldier.IsOnVehicle() || soldier.transform == null)
                return false;

            if (!soldier.isBleeding &&
                soldier.targetDestination != null &&
                (soldier.targetDestination.TryCast<MedicHealingDestination>() != null ||
                 soldier.targetDestination.TryCast<VehicleRepairTask>() != null))
            {
                return false;
            }

            if (soldier.IsOnCover())
                return true;

            IntPtr ptr = soldier.Pointer;
            float now = Time.time;
            if (!_nextLocalCoverSeekTime.TryGetValue(ptr, out float nextSeek) || now >= nextSeek)
            {
                _nextLocalCoverSeekTime[ptr] = now + 1.5f;

                Vector3 myPos = soldier.transform.position;
                Vector3 threatDir = GetEffectiveThreatDirection(soldier, soldier.joinedSquad, myPos, soldier.transform.forward);
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
                            if (cc == null)
                                continue;

                            Vector3 covPos = cc.GetCoverPosition();
                            if (Mathf.Abs(covPos.y - myPos.y) > 2.4f)
                                continue;

                            float dSq = (covPos - myPos).sqrMagnitude;
                            if (dSq >= bestDistSq)
                                continue;

                            if (!cc.IsCoverAvailable(threatDir, soldier.faction))
                                continue;

                            bestDistSq = dSq;
                            bestCover = cc;
                        }
                    }
                }

                // Consider nearby idle/wrecked tank or armored vehicle hulls as physical cover if closer than static cover!
                if (bestDistSq > 7.0f * 7.0f &&
                    TryFindTankHullCoverOrEscortSpot(soldier, myPos, threatDir, false, out Vector3 tankCoverPos, out _))
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

        private static bool HasClearLineOfFireFromProne(Soldier soldier, Vector3 targetPos, float dist)
        {
            return HasClearLineOfFireAtHeight(soldier, targetPos, dist, 0.22f, 0.32f, 15.0f);
        }

        private static bool HasClearLineOfFireFromCrouch(Soldier soldier, Vector3 targetPos, float dist)
        {
            return HasClearLineOfFireAtHeight(soldier, targetPos, dist, 0.64f, 0.78f, 18.0f);
        }

        private static bool HasClearLineOfFireFromStanding(Soldier soldier, Vector3 targetPos, float dist)
        {
            return HasClearLineOfFireAtHeight(soldier, targetPos, dist, 1.18f, 1.32f, 18.0f);
        }

        private static bool TrySidestepWallObstruction(SoldierAI ai, Soldier soldier, Vector3 targetPos, float dist)
        {
            if (ai == null || soldier == null || soldier.Pointer == IntPtr.Zero || soldier.transform == null)
                return false;

            IntPtr ptr = soldier.Pointer;
            if (_nextWallSidestepTime.TryGetValue(ptr, out float nextSide) && Time.time < nextSide)
                return false;

            _nextWallSidestepTime[ptr] = Time.time + 3.5f;

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
            for (int dIdx = 0; dIdx < 2; dIdx++)
            {
                float stepDist = dIdx == 0 ? 1.25f : 1.80f;
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

                    if (HasClearLineOfFireFromPosition(stepPos, myRoot, targetPos, dist, 1.18f, 1.32f, 18.0f) ||
                        HasClearLineOfFireFromPosition(stepPos, myRoot, targetPos, dist, 0.64f, 0.78f, 18.0f))
                    {
                        if ((ai.MoveDestination - curPos).sqrMagnitude <= 2.5f * 2.5f)
                        {
                            ai.MoveDestination = stepPos;
                        }
                        ai.MoveDirectlyToward(stepPos, 0.45f);
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
            if (soldier != null && soldier.aiController != null && soldier.aiController.visibleTarget != null)
            {
                Vector3 toTarget = soldier.aiController.visibleTarget.GetCenterOfUnit() - referencePos;
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
                    Spottable mateTarget = mate.aiController.visibleTarget;
                    if (mateTarget == null) continue;

                    Vector3 enemyPos = mateTarget.GetCenterOfUnit();
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
            BattleManager bm = BattleManager.instance;
            BattlePhase phase = bm != null ? bm.currentPhase : null;
            if (phase != null && !string.IsNullOrEmpty(faction) && phase.EnemyFactionHasSpawn(faction))
            {
                Vector3 enemyTerritory = phase.GetOppositeFactionTerritoryPosition(faction);
                Vector3 toTerritory = enemyTerritory - referencePos;
                toTerritory.y = 0f;
                if (toTerritory.sqrMagnitude > 1.0f)
                    return toTerritory.normalized;
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

            if (bip == null || ai.IsMoving() || !ai.HasEnemyAndCanFireAtHim || dist <= 5.0f)
                return;

            if (_nextBipodDeployCheckTime.TryGetValue(ptr, out float nextCheck) && Time.time < nextCheck)
                return;

            _nextBipodDeployCheckTime[ptr] = Time.time + 1.25f;

            if (soldier.isInBodyAnimation || AiCore.HorizontalAngleToDirection(soldier, targetPos - soldier.transform.position) > 28f)
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

            if (soldier.m_pose == SoldierPose.Prone)
            {
                if (soldier.ScanBipodSurfaceAt(bip, scanBase + Vector3.up * 0.85f, right, out Vector3 crouchAnchor, out Collider crouchSupport) &&
                    crouchSupport != null &&
                    crouchAnchor.y > feetY + 0.45f)
                {
                    SetBotPose(ai, soldier, SoldierPose.Crouch, true);
                    _bipodStandUntil[ptr] = Time.time + 3.0f;
                    _noProneUntil[ptr] = Time.time + 10.0f;
                    soldier.OperateBipod(bip, crouchAnchor, crouchSupport, true, false);
                    BipodsDeployed++;
                    return;
                }
            }

            if (soldier.m_pose != SoldierPose.Idle && (soldier.IsOnCover() || soldier.IsTowardWall() || IsMuzzleBlockedByWall(soldier, targetPos, dist)))
            {
                if (soldier.ScanBipodSurfaceAt(bip, scanBase + Vector3.up * 1.30f, right, out Vector3 standAnchor, out Collider standSupport) &&
                    standSupport != null &&
                    standAnchor.y > feetY + 0.92f)
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

        private static bool IsEngagedInPointBlankMelee(
            SoldierAI ai,
            Soldier soldier,
            float dist,
            out Soldier enemySoldier,
            out float lungeRange,
            out float ceaseFireRange,
            out float strikeRange)
        {
            enemySoldier = null;
            lungeRange = 0f;
            ceaseFireRange = 0f;
            strikeRange = 0f;

            if (!VisionConfig.EnablePointBlankMelee.Value ||
                ai == null ||
                soldier == null ||
                dist > 6.2f ||
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

            bool hasBayonet = soldier.HasBayonetReady;
            bool holdsMelee = soldier.HoldsMeleeWeapon;
            GenericGun heldGun = soldier.GetHeldGun();
            bool gunEmptyOrReloading = soldier.IsReloading ||
                                       soldier.MustReload() ||
                                       (heldGun != null && heldGun.GetCurrentAmmoCount() <= 0);
            bool boltCyclingClose = soldier.CantAimDueToBoltActionUsage ||
                                    (heldGun != null && (heldGun.bolt_required || heldGun.IsReloadingOrUsingBolt()));

            if (holdsMelee)
            {
                lungeRange = 6.0f;
                ceaseFireRange = 6.0f;
                strikeRange = 1.90f;
            }
            else if (hasBayonet)
            {
                lungeRange = (soldier.IsCharging() || gunEmptyOrReloading || boltCyclingClose) ? 5.2f : 3.8f;
                ceaseFireRange = (gunEmptyOrReloading || boltCyclingClose) ? 4.5f : 2.75f;
                strikeRange = 2.25f;
            }
            else
            {
                lungeRange = gunEmptyOrReloading ? 4.8f : (boltCyclingClose ? 3.2f : 2.5f);
                ceaseFireRange = (gunEmptyOrReloading || boltCyclingClose) ? 3.8f : 2.15f;
                strikeRange = 1.65f;
            }

            if (dist > lungeRange)
                return false;

            Vector3 toEnemy = (enemyPos + Vector3.up * 1.05f) - (myPos + Vector3.up * 1.05f);
            float checkDist = Mathf.Min(dist - 0.25f, 3.5f);
            if (checkDist > 0.30f &&
                AiCore.IsDirectionBlockedByEnvironment(myPos + Vector3.up * 1.05f, toEnemy.normalized, checkDist, soldier.transform.root))
            {
                return false;
            }

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
                public bool WasBleeding;
                public bool SkippedForPing;
                public float PrevPointGunEndTime;
            }

            public static bool Prefix(SoldierAI __instance, out SequentialUpdateState __state)
            {
                __state = default;
                try
                {
                    if (!VisionConfig.EnableMod.Value || __instance == null)
                        return true;

                    Soldier soldier = __instance.character;
                    if (!AiCore.IsLocallyOwnedBot(soldier) || soldier.IsOnVehicle())
                        return true;

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
                            return false;
                        }
                        return true;
                    }

                    if (VisionConfig.EnableThreatSectorConcentration.Value)
                    {
                        TryGetKnownThreatSectorDirection(soldier, out _, out bool hasActiveContact);
                        bool isSquadAware = soldier.joinedSquad != null && soldier.joinedSquad.IsEnemyAwareOfThisSquad;
                        if (hasActiveContact || isSquadAware || soldier.GetSuppressionValue() >= 3f)
                        {
                            AiCore.RefreshAlertnessWithoutRaisingGun(soldier, 20f);
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
                    }

                    if (VisionConfig.BandageOnlyAfterFindingCover.Value &&
                        (soldier.isBleeding || soldier.GetBleedValue() > 0.01f))
                    {
                        IntPtr ptr = soldier.Pointer;
                        if (!_bleedCoverDeadline.TryGetValue(ptr, out float deadline))
                        {
                            SoldierPersonality bleedPers = GetPersonality(soldier);
                            float seekWindow = (bleedPers == SoldierPersonality.Rifleman_Cautious || bleedPers == SoldierPersonality.Medic_Triage)
                                ? 4.5f
                                : (bleedPers == SoldierPersonality.Assaulter_Shock ? 2.2f : 3.5f);
                            deadline = Time.time + seekWindow;
                            _bleedCoverDeadline[ptr] = deadline;
                        }

                        if (!soldier.IsOnCover() && Time.time < deadline)
                        {
                            bool movingToCover = TrySeekNearbyCover(__instance, soldier);
                            if (movingToCover && __instance.IsMoving() && soldier.isBleeding)
                            {
                                soldier.isBleeding = false;
                                __state.WasBleeding = true;
                            }
                        }
                    }
                }
                catch { }
                return true;
            }

            public static void Postfix(SoldierAI __instance, SequentialUpdateState __state)
            {
                try
                {
                    if (__instance == null)
                        return;

                    Soldier soldier = __instance.character;
                    if (__state.WasBleeding && soldier != null)
                    {
                        soldier.isBleeding = true;
                    }

                    if (!VisionConfig.EnableMod.Value || soldier == null || !soldier.IsAlive || soldier.IsPlayer())
                        return;

                    if (__state.SkippedForPing || AiCore.ShouldSkipBotCycleForNetworkPing())
                        return;

                    IntPtr ptr = soldier.Pointer;

                    CheckAndApplyFieldPromotion(soldier);

                    if (!AiCore.IsLocallyOwnedBot(soldier) || soldier.IsOnVehicle() || soldier.HasSurrended())
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
                                      AiCore.IsOnBridgeOrNarrowCrossing(soldier.transform.position);
                    if (isOnBridge)
                    {
                        _noProneUntil[ptr] = Time.time + 3.5f;
                    }

                    bool isBleeding = soldier.isBleeding || soldier.GetBleedValue() > 0.01f;
                    if (isBleeding)
                    {
                        if (!VisionConfig.BandageOnlyAfterFindingCover.Value)
                        {
                            if (!soldier.isInBodyAnimation && !soldier.isInHandsAnimation)
                            {
                                SetBotPose(__instance, soldier, SoldierPose.Crouch);
                                __instance.TryStopBleeding();
                            }
                            return;
                        }

                        if (!_bleedCoverDeadline.TryGetValue(ptr, out float deadline))
                        {
                            SoldierPersonality bleedPers = personality;
                            float seekWindow = (bleedPers == SoldierPersonality.Rifleman_Cautious || bleedPers == SoldierPersonality.Medic_Triage)
                                ? 4.5f
                                : (bleedPers == SoldierPersonality.Assaulter_Shock ? 2.2f : 3.5f);
                            deadline = Time.time + seekWindow;
                            _bleedCoverDeadline[ptr] = deadline;
                        }

                        bool readyToBandage = soldier.IsOnCover() ||
                                              Time.time >= deadline ||
                                              !__instance.IsMoving();

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
                            SetBotPose(__instance, soldier, SoldierPose.Idle);
                            return;
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
                            SetBotPose(__instance, soldier, __instance.IsMoving() ? SoldierPose.Idle : SoldierPose.Crouch, true);
                            _noProneUntil[ptr] = Time.time + 4.0f;
                        }
                    }

                    bool isPeeking = false;
                    if (_peekRestoreTime.TryGetValue(ptr, out float restoreAt))
                    {
                        bool urgentDuck = soldier.IsReloading ||
                                          soldier.GetSuppressionValue() > duckBackSupp ||
                                          !__instance.HasEnemyAndCanFireAtHim;

                        bool timerExpiredAndCanCrouch = false;
                        if (!urgentDuck && Time.time > restoreAt)
                        {
                            Vector3 peekTargetPos = __instance.visibleTarget != null
                                ? __instance.visibleTarget.GetCenterOfUnit()
                                : (soldier.transform.position + soldier.transform.forward * Mathf.Max(__instance.distFromTarget, 15f));
                            if (HasClearLineOfFireFromCrouch(soldier, peekTargetPos, __instance.distFromTarget))
                            {
                                timerExpiredAndCanCrouch = true;
                            }
                            else
                            {
                                _peekRestoreTime[ptr] = Time.time + 2.5f;
                            }
                        }

                        if (urgentDuck || timerExpiredAndCanCrouch)
                        {
                            _peekRestoreTime.Remove(ptr);
                            _nextPeekAllowedTime[ptr] = Time.time + peekCooldown;
                            if (!__instance.IsMoving() && soldier.m_pose == SoldierPose.Idle)
                            {
                                SetBotPose(__instance, soldier, SoldierPose.Crouch);
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
                        else if (personality == SoldierPersonality.Assaulter_Skirmisher &&
                                 __instance.HasEnemyAndCanFireAtHim &&
                                 __instance.distFromTarget < 55f)
                        {
                            __instance.moveLookingTarget = true;
                        }
                        else if ((personality == SoldierPersonality.Assaulter_Breacher ||
                                  personality == SoldierPersonality.Rifleman_Grenadier ||
                                  personality == SoldierPersonality.AT_Demolitionist) &&
                                 __instance.visibleTarget != null &&
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
                                IsSafeToThrowGrenadeAt(soldier, __instance.visibleTarget.GetCenterOfUnit(), __instance.distFromTarget, 9.0f) &&
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
                            else
                            {
                                // Switch any rifleman, MG gunner, or AT unit away from an invulnerable tank
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
                        out float seqStrikeRange);

                    if (inPointBlankMelee && meleeEnemy != null)
                    {
                        __instance.knifeCharge = soldier.HoldsMeleeWeapon || soldier.HasBayonetReady;
                        __instance.moveLookingTarget = true;
                        SetBotPose(__instance, soldier, SoldierPose.Idle, true);

                        if (!soldier.HasBayonetReady &&
                            !soldier.HoldsMeleeWeapon &&
                            soldier.InventoryKnife != null &&
                            !soldier.isInHandsAnimation)
                        {
                            GenericGun heldGun = soldier.GetHeldGun();
                            bool gunEmpty = soldier.IsReloading ||
                                            soldier.MustReload() ||
                                            (heldGun != null && heldGun.GetCurrentAmmoCount() <= 0);
                            if ((gunEmpty || dist <= 2.15f) && AiCore.TryConsumeBotRpcBudget(0.35f))
                            {
                                soldier.ToggleInventoryKnife();
                            }
                        }

                        if (dist > 1.15f && !soldier.isInBodyAnimation)
                        {
                            __instance.MoveDirectlyToward(meleeEnemy.transform.position, 0.35f);
                        }

                        if (dist <= seqStrikeRange)
                        {
                            TryExecuteMeleeAttack(__instance, soldier, meleeEnemy);
                        }
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

                    bool isStaffingEmplacement = _assignedEmplacementUntil.TryGetValue(ptr, out float empUntil) &&
                                                 Time.time < empUntil &&
                                                 !soldier.IsOnVehicle();

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

                    if (soldier.UsingBipod() || soldier.IsBipodTransitioning())
                    {
                        __instance.actualPose = soldier.m_pose;
                    }
                    else if (isRepairingVehicle)
                    {
                        SetBotPose(__instance, soldier, SoldierPose.Crouch);
                    }
                    else if (__instance.IsMoving())
                    {
                        bool isLongMove = isEvadingArmor ||
                                          isStaffingEmplacement ||
                                          (soldier.transform.position - __instance.MoveDestination).sqrMagnitude > 3.5f * 3.5f;
                        bool inCombatCrouchLock = !isLongMove &&
                                                  _combatCrouchLockUntil.TryGetValue(ptr, out float crouchLockUntil) &&
                                                  Time.time < crouchLockUntil;
                        bool stealthCrouch = soldier.IsStealth &&
                                             soldier.joinedSquad != null &&
                                             soldier.joinedSquad.Leader != null &&
                                             soldier.joinedSquad.Leader.m_pose > SoldierPose.Idle;

                        if ((isOnBridge || dist <= 60f) && __instance.HasEnemyAndCanFireAtHim)
                        {
                            __instance.moveLookingTarget = true;
                        }

                        // If caught moving in the open under MG/rifle suppression (> 8.5m from destination), seek nearby local cover within 13.5m
                        if (suppression >= 20f &&
                            !soldier.IsOnCover() &&
                            !HasActiveGoapCoverLock(ptr, Time.time) &&
                            !isEvadingArmor &&
                            !isHealingTeammate &&
                            !isStaffingEmplacement &&
                            !soldier.IsCharging() &&
                            !inPointBlankMelee &&
                            !isOnBridge &&
                            (soldier.transform.position - __instance.MoveDestination).sqrMagnitude > 8.5f * 8.5f)
                        {
                            TrySeekNearbyCover(__instance, soldier);
                        }

                        // Attacker Bounding Overwatch: state-driven via GOAP when EnableTacticalGoapPlanner is active,
                        // or fallback staggered cycle when GOAP is toggled off
                        bool inBoundingOverwatch = false;
                        bool holdOverwatchFireNow = false;
                        if (VisionConfig.EnableBoundingOverwatch.Value &&
                            !VisionConfig.EnableTacticalGoapPlanner.Value &&
                            !isOnBridge &&
                            !isEvadingArmor &&
                            !isHealingTeammate &&
                            !isStaffingEmplacement &&
                            !soldier.IsCharging() &&
                            !inPointBlankMelee &&
                            !soldier.IsOnCover() &&
                            (soldier.transform.position - __instance.MoveDestination).sqrMagnitude > 4.5f * 4.5f &&
                            (suppression >= 10f ||
                             (__instance.HasEnemyAndCanFireAtHim && dist >= 14f && dist <= 145f) ||
                             (HasActiveThreatSectorContact(soldier) && soldier.isAlerted)))
                        {
                            inBoundingOverwatch = true;
                            float cycleOffset = (AiCore.GetDeterministicHash(ptr, 113) % 90) * 0.1f;
                            float phase = (Time.time + cycleOffset) % 9.0f;
                            if (phase >= 5.2f &&
                                __instance.HasEnemyAndCanFireAtHim &&
                                dist >= 18f &&
                                dist <= 145f &&
                                suppression < 42f &&
                                __instance.visibleTarget != null)
                            {
                                Vector3 owTargetPos = __instance.visibleTarget.GetCenterOfUnit();
                                if (HasClearLineOfFireFromCrouch(soldier, owTargetPos, dist) ||
                                    HasClearLineOfFireFromStanding(soldier, owTargetPos, dist))
                                {
                                    holdOverwatchFireNow = true;
                                }
                            }
                        }

                        if (soldier.IsCharging() || inPointBlankMelee)
                        {
                            SetBotPose(__instance, soldier, SoldierPose.Idle, true);
                        }
                        else if (holdOverwatchFireNow)
                        {
                            __instance.moveCharacter = false;
                            Vector3 owTargetPos = __instance.visibleTarget.GetCenterOfUnit();
                            bool crouchClear = HasClearLineOfFireFromCrouch(soldier, owTargetPos, dist);
                            SetBotPose(__instance, soldier, crouchClear ? SoldierPose.Crouch : SoldierPose.Idle, true);
                            if (!_nextBoundingHoldCountTime.TryGetValue(ptr, out float nextBoundCount) || Time.time >= nextBoundCount)
                            {
                                _nextBoundingHoldCountTime[ptr] = Time.time + 5.0f;
                                BoundingOverwatchHoldsCount++;
                                if (VisionConfig.DebugLogging.Value)
                                {
                                    AiCore.LogDebug("BoundingOverwatch", $"{soldier.faction} attacker holding 3.8s overwatch fire ({dist:F0}m) while squadmates bound.");
                                }
                            }
                        }
                        else if (!isLongMove && soldier.m_pose == SoldierPose.Crouch && !isPeeking && !isHighBipodStanding)
                        {
                            // Keep crouched posture during short <=3.5m cover/anti-stack micro-adjustments
                            __instance.actualPose = SoldierPose.Crouch;
                        }
                        else if (__instance.HasEnemyAndCanFireAtHim &&
                                 dist > 4.5f &&
                                 (__instance.moveLookingTarget || dist <= 22f || suppression >= 26f))
                        {
                            Vector3 targetPos = __instance.visibleTarget != null
                                ? __instance.visibleTarget.GetCenterOfUnit()
                                : (soldier.transform.position + soldier.transform.forward * Mathf.Max(dist, 15f));

                            bool crouchClear = HasClearLineOfFireFromCrouch(soldier, targetPos, dist);
                            bool standClear = !crouchClear && HasClearLineOfFireFromStanding(soldier, targetPos, dist);

                            if (soldier.m_pose == SoldierPose.Prone || __instance.actualPose == SoldierPose.Prone)
                            {
                                SetBotPose(__instance, soldier, standClear ? SoldierPose.Idle : SoldierPose.Crouch, true);
                            }
                            else if (VisionConfig.NeverShootStandingInOpen.Value &&
                                     !isPeeking &&
                                     !isHighBipodStanding &&
                                     (crouchClear || (!standClear && soldier.m_pose == SoldierPose.Crouch)))
                            {
                                SetBotPose(__instance, soldier, SoldierPose.Crouch);
                            }
                            else
                            {
                                SetBotPose(__instance, soldier, SoldierPose.Idle, true);
                            }
                        }
                        else if (VisionConfig.EnforceStandingWhenOutOfCombat.Value)
                        {
                            bool shouldBoundCrouched = (inBoundingOverwatch && suppression >= 10f) ||
                                                       suppression >= 12f ||
                                                       (__instance.HasEnemyAndCanFireAtHim && dist <= 75f);
                            if (soldier.m_pose == SoldierPose.Prone || __instance.actualPose == SoldierPose.Prone)
                            {
                                SoldierPose liftPose = (inCombatCrouchLock || suppression >= 12f || stealthCrouch || shouldBoundCrouched)
                                    ? SoldierPose.Crouch
                                    : SoldierPose.Idle;
                                SetBotPose(__instance, soldier, liftPose, true);
                            }
                            else if (shouldBoundCrouched)
                            {
                                SetBotPose(__instance, soldier, SoldierPose.Crouch);
                            }
                            else if (!inCombatCrouchLock && suppression < 12f && !stealthCrouch)
                            {
                                SetBotPose(__instance, soldier, SoldierPose.Idle, true);
                            }
                            else if (soldier.m_pose == SoldierPose.Crouch)
                            {
                                __instance.actualPose = SoldierPose.Crouch;
                            }
                        }
                    }
                    else
                    {
                        if (inPointBlankMelee)
                        {
                            SetBotPose(__instance, soldier, SoldierPose.Idle, true);
                        }
                        else if (isPeeking || isHighBipodStanding)
                        {
                            __instance.actualPose = soldier.m_pose;
                        }
                        else if (__instance.HasEnemyAndCanFireAtHim && !soldier.IsCharging())
                        {
                            if (VisionConfig.NeverShootStandingInOpen.Value && dist > 4.5f)
                            {
                                Vector3 targetPos = __instance.visibleTarget != null
                                    ? __instance.visibleTarget.GetCenterOfUnit()
                                    : (soldier.transform.position + soldier.transform.forward * Mathf.Max(dist, 15f));

                                bool crouchClear = HasClearLineOfFireFromCrouch(soldier, targetPos, dist);
                                bool standClear = !crouchClear && HasClearLineOfFireFromStanding(soldier, targetPos, dist);
                                bool mustStandToClearWall = !crouchClear &&
                                                            standClear &&
                                                            suppression < maxPeekSupp &&
                                                            !soldier.IsReloading;

                                if (mustStandToClearWall)
                                {
                                    if (__instance.actualPose != SoldierPose.Idle || soldier.m_pose != SoldierPose.Idle)
                                    {
                                        SetBotPose(__instance, soldier, SoldierPose.Idle);
                                        _noProneUntil[ptr] = Time.time + 10.0f;
                                        _peekRestoreTime[ptr] = Time.time + 4.5f;
                                    }
                                }
                                else if (!crouchClear && soldier.m_pose == SoldierPose.Idle && suppression < 32f)
                                {
                                    __instance.actualPose = SoldierPose.Idle;
                                }
                                else
                                {
                                    bool canGoProne = (!_noProneUntil.TryGetValue(ptr, out float noProne) || Time.time >= noProne) &&
                                                      !soldier.IsOnCover() &&
                                                      !soldier.IsTowardWall();

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
                            Vector3 lookFwd = soldier.transform.forward;
                            lookFwd.y = 0f;
                            lookFwd = lookFwd.sqrMagnitude > 0.001f ? lookFwd.normalized : threatDir;
                            bool windowSillRequiresStanding =
                                (onActualCombatCover && activeCover != null && activeCover.GetCoverPose() == SoldierPose.Idle) ||
                                (AiCore.RaycastEnvironment(myPos + Vector3.up * 0.78f, lookFwd, 2.2f, myRoot, out _) &&
                                 !AiCore.RaycastEnvironment(myPos + Vector3.up * 1.28f, lookFwd, 6.0f, myRoot, out _));

                            bool canStandAtWindow = windowSillRequiresStanding &&
                                                    suppression < duckBackSupp &&
                                                    !soldier.IsReloading &&
                                                    !soldier.isBleeding;

                            if (canStandAtWindow && !stealthCrouch && !hasMortar)
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

                    if (isGunner && !__instance.IsMoving())
                    {
                        Vector3 targetPos = __instance.visibleTarget != null
                            ? __instance.visibleTarget.GetCenterOfUnit()
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

                        ai.MoveDirectlyToward(bestSurrendered.transform.position, 1.35f);
                    }
                }
                else
                {
                    _liberateMoveStartTime.Remove(rescuerPtr);
                }
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
                    _combatStanceHoldUntil[ptr] = Time.time + 6.0f;

                    AddCombatExperienceBonus(soldier, 1.5f);

                    float vet01 = GetVeterancy01(soldier);
                    float curSupp = soldier.GetSuppressionValue();
                    if (VisionConfig.EnableShellShockSurrender.Value &&
                        curSupp >= 52f &&
                        TryEvaluateShellShockSurrender(__instance, soldier, GetPersonality(soldier), vet01, curSupp))
                    {
                        return;
                    }

                    float desiredCooldown = Mathf.Lerp(4.8f, 2.2f, vet01);
                    if (__instance.nextSuppressionCheckTime > Time.time + desiredCooldown)
                    {
                        __instance.nextSuppressionCheckTime = Time.time + desiredCooldown;
                    }

                    if (VisionConfig.EnableSelfPreservationStance.Value &&
                        !soldier.IsOnVehicle() &&
                        !soldier.IsCharging())
                    {
                        TrySeekNearbyCover(__instance, soldier);

                        if (!__instance.IsMoving())
                        {
                            bool canGoProne = (!_noProneUntil.TryGetValue(ptr, out float noProne) || Time.time >= noProne) &&
                                              !soldier.IsOnCover() &&
                                              !soldier.IsTowardWall();

                            float proneThresh = GetEffectiveProneSuppressionThreshold(soldier, vet01);
                            SoldierPose targetPose = (curSupp >= proneThresh && canGoProne)
                                ? SoldierPose.Prone
                                : SoldierPose.Crouch;

                            if (soldier.m_pose < targetPose)
                            {
                                SetBotPose(__instance, soldier, targetPose);
                                SelfPreservationDrops++;
                            }
                        }
                    }
                }
                catch { }
            }
        }

        // =====================================================================
        // PATCH 4: SoldierAI.UpdateMoveDirection (Wall & Fence Vaulting)
        // =====================================================================
        [HarmonyPatch(typeof(SoldierAI), nameof(SoldierAI.UpdateMoveDirection))]
        public static class SoldierAI_UpdateMoveDirection_Patch
        {
            public static void Postfix(SoldierAI __instance)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value || !VisionConfig.EnableWallVaulting.Value || __instance == null)
                        return;

                    Soldier soldier = __instance.character;
                    if (!AiCore.IsLocallyOwnedBot(soldier) || soldier.IsOnVehicle())
                        return;

                    IntPtr ptr = soldier.Pointer;
                    if (_nextVaultCheckTime.TryGetValue(ptr, out float nextVault) && Time.time < nextVault)
                        return;

                    _nextVaultCheckTime[ptr] = Time.time + 0.50f;

                    if (!__instance.IsMoving() ||
                        soldier.isInBodyAnimation ||
                        soldier.IsJumping() ||
                        soldier.IsCarryingBody() ||
                        soldier.IsCarried() ||
                        soldier.HasSurrended())
                    {
                        return;
                    }

                    if (soldier.CanVault(out Vector3 vaultPos) && AiCore.TryConsumeBotRpcBudget(0.35f))
                    {
                        soldier.Vault(vaultPos);
                        _nextVaultCheckTime[ptr] = Time.time + 2.20f;
                        WallsVaulted++;
                    }
                }
                catch { }
            }
        }

        // =====================================================================
        // PATCH 4B: SoldierAI.GetSpacingFromSquad (Bridge & Narrow Crossing Discipline)
        // =====================================================================
        [HarmonyPatch(typeof(SoldierAI), nameof(SoldierAI.GetSpacingFromSquad))]
        public static class SoldierAI_GetSpacingFromSquad_Patch
        {
            public static void Postfix(SoldierAI __instance, ref Vector3 __result)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value ||
                        !VisionConfig.EnableBridgeCrossingDiscipline.Value ||
                        __instance == null)
                    {
                        return;
                    }

                    Soldier soldier = __instance.character;
                    if (!AiCore.IsLocallyOwnedBot(soldier) || soldier.IsOnVehicle())
                        return;

                    Vector3 moveDest = __instance.MoveDestination;
                    if ((__result - moveDest).sqrMagnitude <= 1.0f)
                        return;

                    Vector3 myPos = soldier.transform.position;
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
                        IntPtr ptr = soldier.Pointer;
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
                catch { }
            }
        }

        // =====================================================================
        // PATCH 5: SoldierAI.RotateAndFireToward
        // =====================================================================
        [HarmonyPatch(typeof(SoldierAI), nameof(SoldierAI.RotateAndFireToward))]
        public static class SoldierAI_RotateAndFireToward_Patch
        {
            public static bool Prefix(SoldierAI __instance, ref Vector3 __0, float __1)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value || __instance == null)
                        return true;

                    Soldier soldier = __instance.character;
                    if (!AiCore.IsLocallyOwnedBot(soldier))
                        return true;

                    IntPtr ptr = soldier.Pointer;
                    _combatStanceHoldUntil[ptr] = Time.time + 6.0f;

                    float dist = __instance.distFromTarget;
                    if (dist <= 0.1f)
                        dist = Vector3.Distance(soldier.transform.position, __0);

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
                                Time.time >= aimCache.ExpireTime)
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
                                    ExpireTime = Time.time + 0.40f,
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
                                    dist = Vector3.Distance(soldier.transform.position, __0);
                                }
                            }
                            else
                            {
                                __instance.targetInWeaponRange = false;
                                __instance.allowFireAtEnemy = false;
                                __instance.aimingEnemy = false;
                                __instance.shotFiresEnd = 0f;
                                soldier.SetGunTriggerPressed(false);
                                soldier.pointGunEndTime = Mathf.Min(soldier.pointGunEndTime, Time.time);
                                if (!_nextTankDisciplineCountTime.TryGetValue(ptr, out float nextCount) || Time.time >= nextCount)
                                {
                                    _nextTankDisciplineCountTime[ptr] = Time.time + 3.0f;
                                    UselessTankFirePrevented++;
                                }
                                return false;
                            }
                        }
                    }

                    float vet01 = GetVeterancy01(soldier);
                    bool blockFire = false;
                    bool wallBlocked = false;
                    bool isMoving = __instance.IsMoving();
                    bool isPeeking = _peekRestoreTime.TryGetValue(ptr, out float peekEnd) && Time.time < peekEnd;
                    bool isHighBipodStanding = _bipodStandUntil.TryGetValue(ptr, out float standUntil) && Time.time < standUntil;
                    bool isGunner = IsMachineGunnerOrHasBipod(soldier, out AttachmentBipod bipod);
                    bool isOnBridge = VisionConfig.EnableBridgeCrossingDiscipline.Value &&
                                      !soldier.IsOnVehicle() &&
                                      AiCore.IsOnBridgeOrNarrowCrossing(soldier.transform.position);
                    if (isOnBridge)
                    {
                        _noProneUntil[ptr] = Time.time + 3.5f;
                    }

                    if (!isMoving && !soldier.IsOnVehicle())
                    {
                        __instance.actualPose = soldier.m_pose;
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
                            out float strikeRange) &&
                        meleeEnemy != null)
                    {
                        inPointBlankMelee = true;
                        if (soldier.m_pose != SoldierPose.Idle)
                        {
                            SetBotPose(__instance, soldier, SoldierPose.Idle, true);
                        }

                        Vector3 enemyPos = meleeEnemy.transform.position;
                        if (dist > 1.15f && !soldier.isInBodyAnimation)
                        {
                            if (!_nextMeleeStepTime.TryGetValue(ptr, out float nextStep) || Time.time >= nextStep)
                            {
                                _nextMeleeStepTime[ptr] = Time.time + 0.20f;
                                __instance.MoveDirectlyToward(enemyPos, 0.30f);
                                __instance.moveLookingTarget = true;
                            }
                        }

                        if (dist <= ceaseFireRange)
                        {
                            blockFire = true;
                            Vector3 dirToEnemy = enemyPos - soldier.transform.position;
                            dirToEnemy.y = 0f;
                            if (dirToEnemy.sqrMagnitude > 0.01f)
                            {
                                soldier.RotateToward(dirToEnemy.normalized, __1 * 2.5f);
                            }
                        }

                        if (dist <= strikeRange)
                        {
                            TryExecuteMeleeAttack(__instance, soldier, meleeEnemy);
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
                            if (curSupp >= 32f || HasClearLineOfFireFromCrouch(soldier, __0, dist))
                            {
                                bool canGoProne = (!_noProneUntil.TryGetValue(ptr, out float noProne) || Time.time >= noProne) &&
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

                                if (!_nextStanceDropAllowed.TryGetValue(ptr, out float nextAllowed) || Time.time >= nextAllowed)
                                {
                                    _stanceDropHoldFireUntil[ptr] = Time.time + 0.30f;
                                    _nextStanceDropAllowed[ptr] = Time.time + 3.5f;
                                    SelfPreservationDrops++;
                                }
                            }
                            else
                            {
                                __instance.actualPose = SoldierPose.Idle;
                            }
                        }

                        if (_stanceDropHoldFireUntil.TryGetValue(ptr, out float holdUntil) && Time.time < holdUntil)
                        {
                            blockFire = true;
                        }
                    }

                    if (VisionConfig.EnableWallShootingPrevention.Value && !soldier.IsOnVehicle())
                    {
                        float angleToTarget = AiCore.HorizontalAngleToDirection(soldier, __0 - soldier.transform.position);
                        if (angleToTarget <= 35f &&
                            (!_nextWallCheckTime.TryGetValue(ptr, out float nextWall) || Time.time >= nextWall))
                        {
                            _nextWallCheckTime[ptr] = Time.time + 0.25f;
                            if (IsMuzzleBlockedByWall(soldier, __0, dist))
                            {
                                _wallBlockedUntil[ptr] = Time.time + 0.28f;
                                WallsBlocked++;

                                bool canPeekNow = (!_nextPeekAllowedTime.TryGetValue(ptr, out float nextPeek) || Time.time >= nextPeek) &&
                                                  (!_poseLockUntil.TryGetValue(ptr, out float poseLock) || Time.time >= poseLock);
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
                                    bool canSidestep = !soldier.IsCharging() && !isOnBridge && !soldier.IsOnCover();

                                    if (soldier.m_pose == SoldierPose.Prone &&
                                        !HasClearLineOfFireFromProne(soldier, __0, dist))
                                    {
                                        if (crouchClear)
                                        {
                                            SetBotPose(__instance, soldier, SoldierPose.Crouch, true);
                                            _noProneUntil[ptr] = Time.time + 10.0f;
                                            _combatStanceHoldUntil[ptr] = Time.time + 6.0f;
                                            _peekRestoreTime[ptr] = Time.time + 5.0f;
                                            _nextStanceDropAllowed[ptr] = Time.time + 5.0f;
                                            AutoPeeksTriggered++;
                                        }
                                        else if (standClear)
                                        {
                                            SetBotPose(__instance, soldier, SoldierPose.Idle, true);
                                            _noProneUntil[ptr] = Time.time + 10.0f;
                                            _combatStanceHoldUntil[ptr] = Time.time + 6.0f;
                                            _peekRestoreTime[ptr] = Time.time + 4.5f;
                                            _nextStanceDropAllowed[ptr] = Time.time + 4.5f;
                                            if (isGunner && bipod != null)
                                            {
                                                _bipodStandUntil[ptr] = Time.time + 4.5f;
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
                                            _noProneUntil[ptr] = Time.time + 10.0f;
                                            _combatStanceHoldUntil[ptr] = Time.time + 6.0f;
                                            _peekRestoreTime[ptr] = Time.time + 4.5f;
                                            _nextStanceDropAllowed[ptr] = Time.time + 4.5f;
                                            if (isGunner && bipod != null)
                                            {
                                                _bipodStandUntil[ptr] = Time.time + 4.5f;
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

                        if (_wallBlockedUntil.TryGetValue(ptr, out float wallUntil) && Time.time < wallUntil)
                        {
                            blockFire = true;
                            wallBlocked = true;
                        }
                    }

                    if (VisionConfig.EnableFriendlyFireAvoidance.Value)
                    {
                        if (!_nextFFCheckTime.TryGetValue(ptr, out float nextFF) || Time.time >= nextFF)
                        {
                            _nextFFCheckTime[ptr] = Time.time + 0.25f;
                            if (IsFriendlyInLineOfFire(soldier, __0, dist, vet01, out _, out Soldier blockingFriend))
                            {
                                _ffBlockedUntil[ptr] = Time.time + 0.30f;
                                FriendlyFirePrevented++;
                                if (blockingFriend != null)
                                {
                                    TryNudgeFriendlyOutOfFireCone(soldier, blockingFriend, __0);
                                }
                            }
                        }

                        if (_ffBlockedUntil.TryGetValue(ptr, out float ffUntil) && Time.time < ffUntil)
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
                        if (!_nextAntiStackTime.TryGetValue(ptr, out float nextAS) || Time.time >= nextAS)
                        {
                            _nextAntiStackTime[ptr] = Time.time + 2.5f;
                            if (TryGetAntiStackPush(soldier, out Vector3 pushDir))
                            {
                                Vector3 curPos = soldier.transform.position;
                                if (!AiCore.IsDirectionBlockedByEnvironment(curPos + Vector3.up * 0.75f, pushDir, 1.50f, soldier.transform.root))
                                {
                                    __instance.MoveDirectlyToward(curPos + pushDir * 1.40f, 0.45f);
                                    _nextAntiStackTime[ptr] = Time.time + 6.0f;
                                    AntiStackNudges++;
                                }
                            }
                        }
                    }

                    if (blockFire)
                    {
                        // Native SoldierAI.RotateAndFireToward (0x18055E3C0) never checks allowFireAtEnemy;
                        // it calls Soldier.RotateToward (which sets pointGunEndTime = Time.time + 1.5f) and
                        // checks shotFiresEnd / aimingEnemy to call SetGunTriggerPressed(true).
                        // Returning false here prevents raising the weapon into walls or pulling the trigger.
                        __instance.shotFiresEnd = 0f;
                        soldier.SetGunTriggerPressed(false);

                        if (wallBlocked && !isPeeking)
                        {
                            soldier.pointGunEndTime = Mathf.Min(soldier.pointGunEndTime, Time.time);
                        }

                        if (!inPointBlankMelee && !isMoving)
                        {
                            Vector3 toTarget = __0 - soldier.transform.position;
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
                        soldier.IsOnVehicle() ||
                        soldier.IsCharging() ||
                        !soldier.HasDestinationAssigned ||
                        soldier.targetDestination == null)
                    {
                        return true;
                    }

                    IntPtr ptr = soldier.Pointer;
                    float now = Time.time;
                    bool protectedCover =
                        (VisionConfig.EnableTacticalGoapPlanner.Value && HasActiveGoapCoverLock(ptr, now)) ||
                        (_localCoverProtectedUntil.TryGetValue(ptr, out float protUntil) && now < protUntil);

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
            public static void Postfix(CombatCover __instance, Vector3 __0, ref bool __result)
            {
                try
                {
                    if (!__result ||
                        !VisionConfig.EnableMod.Value ||
                        !VisionConfig.EnableSmartWallSideCover.Value ||
                        __instance == null)
                    {
                        return;
                    }

                    Vector3 threatDir = __0;
                    threatDir.y = 0f;
                    if (threatDir.sqrMagnitude < 0.001f)
                        return;

                    threatDir.Normalize();

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
                    long cacheKey = ((long)__instance.Pointer << 4) | (uint)octant;

                    if (!_coverExposureCache.TryGetValue(cacheKey, out bool isExposed))
                    {
                        Vector3 coverPos = __instance.GetCoverPosition();
                        isExposed = IsPositionOnExposedSideOfWall(coverPos, threatDir, null, out _);
                        _coverExposureCache[cacheKey] = isExposed;
                    }

                    if (isExposed)
                    {
                        __result = false;
                    }
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(Squad), nameof(Squad.SendUnitsToCovers))]
        public static class Squad_SendUnitsToCovers_Patch
        {
            public static void Prefix(Squad __instance, Vector3 __0)
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

                    Vector3 threatDir = GetEffectiveThreatDirection(__instance.Leader, __instance, __0, __instance.formationDirection);
                    if (threatDir.sqrMagnitude > 0.01f)
                    {
                        __instance.formationDirection = threatDir;
                    }
                }
                catch { }
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
                if (dSq <= 16.0f && allyAi != null && allyAi.moveCharacter)
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
                    bool isMoving = ai != null && ai.moveCharacter;
                    Vector3 moveDir = botFwd;
                    if (isMoving && ai != null)
                    {
                        Vector3 toDest = ai.MoveDestination - botPos;
                        toDest.y = 0f;
                        if (toDest.sqrMagnitude > 0.04f)
                            moveDir = toDest.normalized;
                    }

                    float curSupp = __instance.GetSuppressionValue();
                    bool isDoorVulnerable = curSupp >= 32f ||
                                            __instance.IsReloading ||
                                            __instance.isBleeding;
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
                                if (walkingThrough || facingClose)
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
                    _localCoverProtectedUntil.Remove(ptr);
                    _isTurningToWatchSector.Remove(ptr);
                    _ffBlockedUntil.Remove(ptr);
                    _wallBlockedUntil.Remove(ptr);
                }
                catch { }
            }
        }
    }
}
