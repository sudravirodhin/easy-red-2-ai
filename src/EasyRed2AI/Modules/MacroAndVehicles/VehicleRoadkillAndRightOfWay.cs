using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace EasyRed2HumanAI
{
    public static partial class VeteranAndTacticsPatches
    {
        // =====================================================================
        // Friendly Infantry Right-of-Way & Anti-Roadkill
        // =====================================================================
        private static bool TryGetColliderLocalBox(Collider c, out Vector3 center, out Vector3 ext)
        {
            BoxCollider box = c.TryCast<BoxCollider>();
            if (box != null)
            {
                center = box.center;
                ext = box.size * 0.5f;
                return true;
            }

            MeshCollider meshCol = c.TryCast<MeshCollider>();
            if (meshCol != null)
            {
                Mesh sm = meshCol.sharedMesh;
                if (sm != null && sm.Pointer != IntPtr.Zero)
                {
                    Bounds mb = sm.bounds;
                    center = mb.center;
                    ext = mb.extents;
                    return true;
                }
                center = Vector3.zero;
                ext = Vector3.zero;
                return false;
            }

            CapsuleCollider cap = c.TryCast<CapsuleCollider>();
            if (cap != null)
            {
                float r = cap.radius;
                float hh = Mathf.Max(r, cap.height * 0.5f);
                center = cap.center;
                if (cap.direction == 0)
                    ext = new Vector3(hh, r, r);
                else if (cap.direction == 2)
                    ext = new Vector3(r, r, hh);
                else
                    ext = new Vector3(r, hh, r);
                return true;
            }

            SphereCollider sph = c.TryCast<SphereCollider>();
            if (sph != null)
            {
                float r = sph.radius;
                center = sph.center;
                ext = new Vector3(r, r, r);
                return true;
            }

            center = Vector3.zero;
            ext = Vector3.zero;
            return false;
        }

        /// <summary>
        /// Computes and caches rotation-invariant vehicle-local hull OBB
        /// (halfWidth, halfLength, localCenterX, localCenterZ), combining wheel bounds
        /// with non-turret low-hull solid colliders so truck hoods, bumpers, and cargo beds
        /// (plus off-center vehicle pivots) are accurately covered without including elevated gun barrels.
        /// </summary>
        private static Vector4 GetCachedVehicleLocalHullObb(Vehicle veh)
        {
            Vector4 fallback = new Vector4(1.65f, 3.10f, 0f, 0f);
            if (veh == null || veh.Pointer == IntPtr.Zero)
                return fallback;

            IntPtr vehPtr = veh.Pointer;
            if (_cachedVehicleExtents.TryGetValue(vehPtr, out Vector4 cachedObb))
                return cachedObb;

            bool hasBounds = false;
            float minX = 0f, maxX = 0f, minZ = 0f, maxZ = 0f;
            try
            {
                VehicleWithWheels wheeled = veh.TryCast<VehicleWithWheels>();
                if (wheeled != null)
                {
                    Bounds wb = wheeled.CalculateVehicleLocalBounds();
                    if (wb.extents.x > 0.05f || wb.extents.z > 0.05f)
                    {
                        minX = wb.center.x - wb.extents.x;
                        maxX = wb.center.x + wb.extents.x;
                        minZ = wb.center.z - wb.extents.z;
                        maxZ = wb.center.z + wb.extents.z;
                        hasBounds = true;
                    }
                }

                Transform vehTr = veh.transform;
                Collider[] solidCols = vehTr != null ? GetCachedVehicleSolidColliders(veh) : null;
                if (solidCols != null)
                {
                    for (int i = 0; i < solidCols.Length; i++)
                    {
                        Collider c = solidCols[i];
                        if (c == null || c.Pointer == IntPtr.Zero || !c.enabled)
                            continue;

                        // Exclude turrets/cannon barrels so elevated tank barrels do not protrude into ground roadkill OBB
                        if (c.GetComponentInParent<Turret>() != null)
                            continue;

                        if (!TryGetColliderLocalBox(c, out Vector3 cCenter, out Vector3 cExt))
                            continue;

                        Transform ct = c.transform;
                        if (ct == null)
                            continue;

                        float cMinX = float.MaxValue, cMaxX = float.MinValue;
                        float cMinY = float.MaxValue, cMaxY = float.MinValue;
                        float cMinZ = float.MaxValue, cMaxZ = float.MinValue;

                        for (int cornerIdx = 0; cornerIdx < 8; cornerIdx++)
                        {
                            float sx = (cornerIdx & 1) == 0 ? -cExt.x : cExt.x;
                            float sy = (cornerIdx & 2) == 0 ? -cExt.y : cExt.y;
                            float sz = (cornerIdx & 4) == 0 ? -cExt.z : cExt.z;
                            Vector3 vLocal = vehTr.InverseTransformPoint(
                                ct.TransformPoint(new Vector3(cCenter.x + sx, cCenter.y + sy, cCenter.z + sz)));

                            if (vLocal.x < cMinX) cMinX = vLocal.x;
                            if (vLocal.x > cMaxX) cMaxX = vLocal.x;
                            if (vLocal.y < cMinY) cMinY = vLocal.y;
                            if (vLocal.y > cMaxY) cMaxY = vLocal.y;
                            if (vLocal.z < cMinZ) cMinZ = vLocal.z;
                            if (vLocal.z > cMaxZ) cMaxZ = vLocal.z;
                        }

                        if (cMinY <= 1.55f && cMaxY >= -0.50f &&
                            (cMaxX - cMinX) <= 6.0f && (cMaxZ - cMinZ) <= 13.0f)
                        {
                            if (!hasBounds)
                            {
                                minX = cMinX; maxX = cMaxX;
                                minZ = cMinZ; maxZ = cMaxZ;
                                hasBounds = true;
                            }
                            else
                            {
                                if (cMinX < minX) minX = cMinX;
                                if (cMaxX > maxX) maxX = cMaxX;
                                if (cMinZ < minZ) minZ = cMinZ;
                                if (cMaxZ > maxZ) maxZ = cMaxZ;
                            }
                        }
                    }
                }
            }
            catch { }

            Vector4 obb = hasBounds
                ? new Vector4(
                    Mathf.Clamp((maxX - minX) * 0.5f, 1.15f, 2.55f),
                    Mathf.Clamp((maxZ - minZ) * 0.5f, 2.05f, 5.60f),
                    Mathf.Clamp((minX + maxX) * 0.5f, -0.60f, 0.60f),
                    Mathf.Clamp((minZ + maxZ) * 0.5f, -2.20f, 2.20f))
                : fallback;

            _cachedVehicleExtents[vehPtr] = obb;
            return obb;
        }

        private static Vector2 GetCachedVehicleHalfExtents(Vehicle veh)
        {
            Vector4 obb = GetCachedVehicleLocalHullObb(veh);
            return new Vector2(obb.x, obb.y);
        }

        private static bool TryRecordFriendlyYieldOrSave(IntPtr vehPtr, float now)
        {
            if (!_nextFriendlyYieldCountTime.TryGetValue(vehPtr, out float nextCount) || now >= nextCount)
            {
                _nextFriendlyYieldCountTime[vehPtr] = now + 4.5f;
                FriendlyRoadkillsPreventedCount++;
                return VisionConfig.DebugLogging.Value;
            }
            return false;
        }

        private static Collider[] GetCachedVehicleSolidColliders(Vehicle veh)
        {
            if (veh == null || veh.Pointer == IntPtr.Zero)
                return Array.Empty<Collider>();

            IntPtr vehPtr = veh.Pointer;
            if (_cachedVehicleSolidColliders.TryGetValue(vehPtr, out Collider[] cached))
                return cached;

            Collider[] solid = Array.Empty<Collider>();
            try
            {
                var rawCols = veh.GetComponentsInChildren<Collider>(false);
                if (rawCols != null && rawCols.Length > 0)
                {
                    var list = new List<Collider>(Mathf.Min(rawCols.Length, 16));
                    for (int i = 0; i < rawCols.Length; i++)
                    {
                        Collider c = rawCols[i];
                        if (c == null || c.Pointer == IntPtr.Zero || c.isTrigger)
                            continue;
                        if (c.TryCast<BoxCollider>() == null &&
                            c.TryCast<MeshCollider>() == null &&
                            c.TryCast<CapsuleCollider>() == null &&
                            c.TryCast<SphereCollider>() == null)
                        {
                            continue;
                        }
                        list.Add(c);
                    }
                    if (list.Count > 0)
                        solid = list.ToArray();
                }
            }
            catch { }

            _cachedVehicleSolidColliders[vehPtr] = solid;
            return solid;
        }

        private static void TemporarilyIgnoreFriendlyVehicleCollision(Soldier s, Vehicle veh, float restoreTime)
        {
            if (s == null || veh == null || s.Pointer == IntPtr.Zero || veh.Pointer == IntPtr.Zero)
                return;

            CharacterController cc = s.m_controller;
            if (cc == null || cc.Pointer == IntPtr.Zero || !cc.enabled)
                return;

            Collider[] hullCols = GetCachedVehicleSolidColliders(veh);
            if (hullCols == null || hullCols.Length == 0)
                return;

            long ccHash = cc.Pointer.ToInt64();
            for (int i = 0; i < hullCols.Length; i++)
            {
                Collider vc = hullCols[i];
                if (vc == null || vc.Pointer == IntPtr.Zero)
                    continue;

                long pairKey = (ccHash * 397L) ^ vc.Pointer.ToInt64();
                if (_ignoredFriendlyCollisionPairs.Contains(pairKey))
                {
                    for (int j = 0; j < _ignoredFriendlyVehicleCollisions.Count; j++)
                    {
                        if (_ignoredFriendlyVehicleCollisions[j].PairKey == pairKey)
                        {
                            var existing = _ignoredFriendlyVehicleCollisions[j];
                            if (restoreTime > existing.RestoreTime)
                            {
                                existing.RestoreTime = restoreTime;
                                _ignoredFriendlyVehicleCollisions[j] = existing;
                            }
                            break;
                        }
                    }
                    continue;
                }

                try
                {
                    Physics.IgnoreCollision(cc, vc, true);
                    _ignoredFriendlyCollisionPairs.Add(pairKey);
                    _ignoredFriendlyVehicleCollisions.Add(new IgnoredFriendlyVehicleCollisionEntry
                    {
                        Controller = cc,
                        VehCollider = vc,
                        PairKey = pairKey,
                        RestoreTime = restoreTime
                    });
                }
                catch { }
            }
        }

        public static void UpdateFriendlyVehicleCollisionRestores()
        {
            if (_ignoredFriendlyVehicleCollisions.Count == 0)
                return;

            float now = Time.time;
            if (now < _nextIgnoredVehicleCollisionSweepTime && _nextIgnoredVehicleCollisionSweepTime - now <= 2.0f)
                return;

            _nextIgnoredVehicleCollisionSweepTime = now + 0.35f;

            for (int i = _ignoredFriendlyVehicleCollisions.Count - 1; i >= 0; i--)
            {
                var entry = _ignoredFriendlyVehicleCollisions[i];
                if (now < entry.RestoreTime)
                    continue;

                _ignoredFriendlyCollisionPairs.Remove(entry.PairKey);
                int last = _ignoredFriendlyVehicleCollisions.Count - 1;
                _ignoredFriendlyVehicleCollisions[i] = _ignoredFriendlyVehicleCollisions[last];
                _ignoredFriendlyVehicleCollisions.RemoveAt(last);

                try
                {
                    if (entry.Controller != null && entry.Controller.Pointer != IntPtr.Zero &&
                        entry.VehCollider != null && entry.VehCollider.Pointer != IntPtr.Zero)
                    {
                        Physics.IgnoreCollision(entry.Controller, entry.VehCollider, false);
                    }
                }
                catch { }
            }
        }

        private static void TryNudgeFriendlySoldierAside(
            Soldier s,
            Vehicle veh,
            Vector3 vehPos,
            Vector3 travelDir,
            Vector3 vehRight,
            float halfWidth,
            float now,
            bool ignoreHullCollision)
        {
            AiCore.SoldierFastHandles sHandles = AiCore.GetOrCreateSoldierHandles(s);
            if (sHandles == null || sHandles.Tr == null || !s.IsAI() || !AiCore.IsLocallyOwnedBot(s))
                return;

            if (ignoreHullCollision && veh != null)
            {
                TemporarilyIgnoreFriendlyVehicleCollision(s, veh, now + 1.85f);
            }

            if (now < sHandles.Combat.NextFriendlyStepAsideTime)
                return;

            Vector3 sPos = sHandles.Tr.position;
            Vector3 delta = sPos - vehPos;
            float sideSign = Vector3.Dot(delta, vehRight) >= 0f ? 1f : -1f;
            float stepDist = halfWidth + 2.35f;
            int solidMask = AiCore.GetSolidEnvironmentMask();
            Transform sRoot = sHandles.RootTr;

            Vector3 sideDir = vehRight * sideSign;
            Vector3 candidatePos = sPos + sideDir * stepDist + travelDir * 0.70f;
            bool primaryValid = !AiCore.IsDirectionBlockedByEnvironment(sPos + Vector3.up * 0.75f, sideDir, 2.6f, sRoot) &&
                                Physics.Raycast(candidatePos + Vector3.up * 0.85f, Vector3.down, 2.65f, solidMask, QueryTriggerInteraction.Ignore);

            if (!primaryValid)
            {
                sideSign = -sideSign;
                sideDir = vehRight * sideSign;
                candidatePos = sPos + sideDir * stepDist + travelDir * 0.70f;
                primaryValid = !AiCore.IsDirectionBlockedByEnvironment(sPos + Vector3.up * 0.75f, sideDir, 2.6f, sRoot) &&
                               Physics.Raycast(candidatePos + Vector3.up * 0.85f, Vector3.down, 2.65f, solidMask, QueryTriggerInteraction.Ignore);
            }

            sHandles.Combat.NextFriendlyStepAsideTime = now + 1.15f;

            if (!primaryValid || s.UsingBipod() || s.isInBodyAnimation || (s.Params != null && !s.Params.canMove))
                return;

            SoldierAI sAi = sHandles.Ai;
            if (s.m_pose == SoldierPose.Prone)
            {
                if (!ReferenceEquals(sAi, null) && sAi != null)
                {
                    SetBotPose(sAi, s, SoldierPose.Idle, true);
                }
                else
                {
                    s.SetPose(SoldierPose.Idle, false);
                }
            }

            if (!ReferenceEquals(sAi, null) && sAi != null &&
                s.transform != null &&
                !AiCore.IsDirectSegmentBlockedByWall(s.transform.position, candidatePos, out _))
            {
                sAi.MoveDirectlyToward(candidatePos, 1.45f);
            }
        }

        private static void EvaluateFriendlySoldierInVehicleCorridor(
            Soldier s,
            Vehicle veh,
            string vehFaction,
            Vector3 vehPos,
            Vector3 travelDir,
            Vector3 vehRight,
            float halfWidth,
            float minFwd,
            float maxStepAsideFwd,
            float maxBrakeFwd,
            float stepAsideHalfWidth,
            float brakeHalfWidth,
            float now,
            ref bool playerInBrakeCorridor,
            ref bool aiInBrakeCorridor)
        {
            AiCore.SoldierFastHandles sHandles = AiCore.GetOrCreateSoldierHandles(s);
            if (sHandles == null || sHandles.Tr == null || !s.IsAlive || s.IsOnVehicle())
                return;

            string sFac = AiCore.GetCachedFaction(sHandles, now);
            if (string.IsNullOrEmpty(sFac) || !AiCore.AreSameSideFactions(vehFaction, sFac))
                return;

            Vector3 sPos = sHandles.Tr.position;
            Vector3 delta = sPos - vehPos;
            if (Mathf.Abs(delta.y) > 3.2f)
                return;

            float fwdDist = Vector3.Dot(delta, travelDir);
            if (fwdDist < minFwd || fwdDist > maxStepAsideFwd)
                return;

            float latAbs = Mathf.Abs(Vector3.Dot(delta, vehRight));
            if (latAbs > stepAsideHalfWidth)
                return;

            // Protect friendly soldier from accidental low-speed contact while clearing the corridor
            s.DisableHitByVehicleFor(1.6f);

            if (s.IsAI())
            {
                bool closeHull = fwdDist <= (minFwd + 3.6f) && latAbs <= (halfWidth + 0.75f);
                TryNudgeFriendlySoldierAside(s, veh, vehPos, travelDir, vehRight, halfWidth, now, closeHull);
            }

            if (fwdDist <= maxBrakeFwd && latAbs <= brakeHalfWidth)
            {
                if (s.IsPlayer())
                    playerInBrakeCorridor = true;
                else
                    aiInBrakeCorridor = true;
            }
        }

        private static bool ShouldYieldToFriendlyInfantry(AIVehicle aiVeh, Vehicle veh, bool isReversing)
        {
            if (veh == null || veh.Pointer == IntPtr.Zero || veh.transform == null)
                return false;

            if (!AiCore.IsLocallyOwnedVehicle(veh) || veh.PlayerIsDriving())
                return false;

            IntPtr vehPtr = veh.Pointer;
            float now = Time.time;

            // Fast cached path between 0.18s spatial checks
            if (_nextVehiclePedestrianScanTime.TryGetValue(vehPtr, out float nextScan) && now < nextScan)
            {
                return _vehicleYieldUntilTime.TryGetValue(vehPtr, out float yieldUntil) && now < yieldUntil;
            }

            _nextVehiclePedestrianScanTime[vehPtr] = now + 0.18f;

            // Always hold brake if a squadmate is actively repairing the vehicle
            if (aiVeh != null && aiVeh.squadInside != null && aiVeh.squadInside.SomeoneIsRepairing())
            {
                _vehicleYieldUntilTime[vehPtr] = now + 0.25f;
                return true;
            }

            string vehFaction = GetVehicleCrewFaction(veh);
            if (string.IsNullOrEmpty(vehFaction))
                return false;

            Transform tr = veh.transform;
            Vector3 vehPos = tr.position;
            Vector3 travelDir = isReversing ? -tr.forward : tr.forward;
            travelDir.y = 0f;
            if (travelDir.sqrMagnitude < 0.001f)
                return false;
            travelDir.Normalize();

            Vector3 vehRight = tr.right;
            vehRight.y = 0f;
            vehRight = vehRight.sqrMagnitude > 0.001f ? vehRight.normalized : Vector3.right;

            Vector2 extents = GetCachedVehicleHalfExtents(veh);
            float halfWidth = extents.x;
            float halfLength = extents.y;

            float speedMs = 0f;
            MovableVehicle mv = veh.TryCast<MovableVehicle>();
            if (mv != null)
                speedMs = Mathf.Abs(mv.GetCurrentSpeed());

            bool isTank = veh.TryCast<VehicleTank>() != null;
            float minFwd = Mathf.Max(0.85f, halfLength * 0.45f);

            // Friendly Vehicle Convoy Spacing & Reverse Collision Guard:
            // Prevent trailing tanks from rear-ending / hull-hugging lead vehicles on roads/bridges (holding 18m-24m
            // anti-bombing road interval) while allowing instant lateral peel-off (> 22 deg) onto flank routes!
            RefreshCombinedArmsIntelIfNeeded();
            if (_activeGroundVehicles.Count > 1)
            {
                Vector3 steerDir = travelDir;
                if (!isReversing && aiVeh != null && aiVeh.path != null && aiVeh.path.NodesCount > 0)
                {
                    Vector3 toNode = aiVeh.path.CurrentNode.point - vehPos;
                    toNode.y = 0f;
                    if (toNode.sqrMagnitude > 0.25f)
                        steerDir = toNode.normalized;
                }

                long myId = vehPtr.ToInt64();
                for (int i = 0; i < _activeGroundVehicles.Count; i++)
                {
                    ActiveGroundVehicleInfo other = _activeGroundVehicles[i];
                    if (other.VehPtr == vehPtr || string.IsNullOrEmpty(other.Faction))
                        continue;
                    if (!AiCore.AreSameSideFactions(vehFaction, other.Faction))
                        continue;

                    Vector3 vDelta = other.Position - vehPos;
                    if (Mathf.Abs(vDelta.y) > 4.5f)
                        continue;
                    vDelta.y = 0f;

                    float fwdProj = Vector3.Dot(vDelta, travelDir);
                    if (fwdProj <= minFwd)
                        continue;

                    float latDist = Mathf.Abs(Vector3.Dot(vDelta, vehRight));
                    float closeBumperDist = halfLength + other.HalfLength + (isReversing ? 5.5f : 5.0f);

                    if (isReversing)
                    {
                        if (fwdProj < closeBumperDist && latDist < (halfWidth + other.HalfWidth + 1.35f))
                        {
                            _vehicleYieldUntilTime[vehPtr] = now + 0.28f;
                            return true;
                        }
                        continue;
                    }

                    float convoyDist = (isTank || other.IsTank)
                        ? Mathf.Clamp(halfLength + other.HalfLength + 13.5f + speedMs * 0.75f, 18.0f, 24.5f)
                        : Mathf.Clamp(halfLength + other.HalfLength + 7.5f + speedMs * 0.65f, 12.0f, 17.5f);

                    if (fwdProj >= convoyDist || latDist >= (halfWidth + other.HalfWidth + 1.85f))
                        continue;

                    // Allow immediate lateral peel-off when pathing diverges > 22 deg and hulls have pivot clearance
                    if (fwdProj > closeBumperDist && Vector3.Angle(steerDir, vDelta) > 22.0f)
                        continue;

                    float headingDot = Vector3.Dot(travelDir, other.Forward);
                    if (other.IsPlayerInsideOrDriving ||
                        (headingDot >= -0.35f && (other.IsStoppedOrShooting || speedMs > other.SpeedMs - 0.35f || fwdProj < closeBumperDist)))
                    {
                        _vehicleYieldUntilTime[vehPtr] = now + 0.28f;
                        return true;
                    }

                    if (headingDot < -0.35f && myId > other.VehPtr.ToInt64())
                    {
                        if (!_vehicleContinuousYieldStart.TryGetValue(vehPtr, out float headOnStart))
                        {
                            headOnStart = now;
                            _vehicleContinuousYieldStart[vehPtr] = headOnStart;
                        }
                        if ((now - headOnStart) <= 2.8f)
                        {
                            _vehicleYieldUntilTime[vehPtr] = now + 0.28f;
                            return true;
                        }
                    }
                }
            }

            float maxStepAsideFwd = Mathf.Clamp(halfLength + 5.2f + speedMs * 0.65f, 5.5f, 11.0f);
            float maxBrakeFwd = Mathf.Clamp(halfLength + 3.8f + speedMs * 0.55f, 4.5f, 9.0f);
            float stepAsideHalfWidth = halfWidth + 1.45f;
            float brakeHalfWidth = halfWidth + 0.70f;

            bool playerInBrakeCorridor = false;
            bool aiInBrakeCorridor = false;

            Soldier player = Soldier.CurrentControlledSoldierOrNull();
            if (player != null)
            {
                EvaluateFriendlySoldierInVehicleCorridor(
                    player, veh, vehFaction, vehPos, travelDir, vehRight,
                    halfWidth, minFwd, maxStepAsideFwd, maxBrakeFwd, stepAsideHalfWidth, brakeHalfWidth,
                    now, ref playerInBrakeCorridor, ref aiInBrakeCorridor);
            }

            Squad crewSquad = aiVeh != null ? aiVeh.squadInside : null;
            if (crewSquad != null && crewSquad.units != null)
            {
                var crewUnits = crewSquad.units;
                int crewCount = crewUnits.Count;
                for (int i = 0; i < crewCount; i++)
                {
                    EvaluateFriendlySoldierInVehicleCorridor(
                        crewUnits[i], veh, vehFaction, vehPos, travelDir, vehRight,
                        halfWidth, minFwd, maxStepAsideFwd, maxBrakeFwd, stepAsideHalfWidth, brakeHalfWidth,
                        now, ref playerInBrakeCorridor, ref aiInBrakeCorridor);
                }
            }

            AiCore.EnsureWorldSnapshot();
            int sqCount = AiCore.WorldSquadCount;
            if (sqCount > 0)
            {
                IntPtr crewSqPtr = crewSquad != null ? crewSquad.Pointer : IntPtr.Zero;
                var squads = AiCore.WorldSquads;
                var soldiers = AiCore.WorldSoldiers;

                for (int s = 0; s < sqCount; s++)
                {
                    ref AiCore.SquadSnapshot sqSnap = ref squads[s];
                    if (sqSnap.Ptr == crewSqPtr || sqSnap.UnitCount == 0 || string.IsNullOrEmpty(sqSnap.Faction))
                        continue;

                    if (!AiCore.AreSameSideFactions(vehFaction, sqSnap.Faction))
                        continue;

                    if ((sqSnap.RefPos - vehPos).sqrMagnitude > 48f * 48f)
                        continue;

                    int uEnd = sqSnap.UnitStartIndex + sqSnap.UnitCount;
                    for (int i = sqSnap.UnitStartIndex; i < uEnd; i++)
                    {
                        Soldier f = soldiers[i].Ref;
                        if (f != null)
                        {
                            EvaluateFriendlySoldierInVehicleCorridor(
                                f, veh, vehFaction, vehPos, travelDir, vehRight,
                                halfWidth, minFwd, maxStepAsideFwd, maxBrakeFwd, stepAsideHalfWidth, brakeHalfWidth,
                                now, ref playerInBrakeCorridor, ref aiInBrakeCorridor);
                        }
                    }
                }
            }

            if (playerInBrakeCorridor || aiInBrakeCorridor)
            {
                if (!_vehicleContinuousYieldStart.TryGetValue(vehPtr, out float yieldStart))
                {
                    yieldStart = now;
                    _vehicleContinuousYieldStart[vehPtr] = yieldStart;
                }

                // Always yield for the human player; yield up to 3.5s for AI infantry while they step aside
                if (playerInBrakeCorridor || (now - yieldStart) <= 3.5f)
                {
                    _vehicleYieldUntilTime[vehPtr] = now + 0.24f;
                    if (TryRecordFriendlyYieldOrSave(vehPtr, now))
                    {
                        AiCore.LogDebug(
                            "FriendlyRightOfWay",
                            $"{vehFaction} vehicle yielded right-of-way to friendly {(playerInBrakeCorridor ? "player" : "infantry")} ({(isReversing ? "reverse" : "forward")}).");
                    }
                    return true;
                }
            }
            else
            {
                _vehicleContinuousYieldStart.Remove(vehPtr);
                _vehicleYieldUntilTime.Remove(vehPtr);
            }

            return false;
        }

        [HarmonyPatch(typeof(AIVehicle), nameof(AIVehicle.MovableVehicleRoutine))]
        public static class AIVehicle_MovableVehicleRoutine_Patch
        {
            public static void Prefix(AIVehicle __instance, out long __state)
            {
                __state = HitchProfiler.BeginTopLevel();
                try
                {
                    if (!VisionConfig.EnableMod.Value ||
                        !VisionConfig.EnableFriendlyVehicleRightOfWay.Value ||
                        __instance == null ||
                        __instance.placingInUnstuck)
                    {
                        return;
                    }

                    Vehicle veh = __instance.veh;
                    if (veh == null || veh.Pointer == IntPtr.Zero)
                        return;

                    if (_vehiclesSeekingRepairStation.TryGetValue(veh.Pointer, out RepairStation st) &&
                        st != null && st.Pointer != IntPtr.Zero && st.transform != null && veh.transform != null)
                    {
                        float r = Mathf.Max(5.0f, st.radius) * 0.85f;
                        if ((veh.transform.position - st.transform.position).sqrMagnitude <= r * r)
                        {
                            float pauseUntil = Time.time + 0.35f;
                            if (__instance.resumeDriveTime < pauseUntil)
                                __instance.resumeDriveTime = pauseUntil;
                            return;
                        }
                    }

                    bool isReversing = __instance.going_in_retro;
                    if (!isReversing && (__instance.path == null || __instance.path.NodesCount <= 0 || __instance.stopToShoot))
                        return;

                    if (ShouldYieldToFriendlyInfantry(__instance, veh, isReversing))
                    {
                        // Setting resumeDriveTime uses AIVehicle.MovableVehicleRoutine's native pause branch:
                        // it calls veh.HoldBrake() + veh.Move(Vector2.zero) and bypasses VehicleStuckCheck()!
                        float pauseUntil = Time.time + 0.24f;
                        if (__instance.resumeDriveTime < pauseUntil)
                            __instance.resumeDriveTime = pauseUntil;
                        return;
                    }

                    // Friendly/observed AT mine avoidance: brake if an armed AT mine sits dead-ahead at close range on a bridge or corridor
                    if (AiCore.ActiveLandMineCount > 0 &&
                        veh.transform != null &&
                        AiCore.IsLocallyOwnedVehicle(veh) &&
                        !veh.IsAirVehicle())
                    {
                        Vector3 travelDir = isReversing ? -veh.transform.forward : veh.transform.forward;
                        travelDir.y = 0f;
                        if (travelDir.sqrMagnitude > 0.01f)
                        {
                            travelDir.Normalize();
                            Vector3 vehPos = veh.transform.position;
                            if (AiCore.TryGetNearbyKnownMine(vehPos + travelDir * 6.5f, 9.0f, vehiclesOnly: true, out Vector3 minePos))
                            {
                                Vector3 delta = minePos - vehPos;
                                delta.y = 0f;
                                float fwdProj = Vector3.Dot(delta, travelDir);
                                Vector3 right = new Vector3(travelDir.z, 0f, -travelDir.x);
                                float latProj = Vector3.Dot(delta, right);
                                if (fwdProj > 0.5f && fwdProj < 9.5f && Mathf.Abs(latProj) < 3.4f &&
                                    AiCore.IsOnBridgeOrNarrowCrossing(vehPos))
                                {
                                    float pauseUntil = Time.time + 0.50f;
                                    if (__instance.resumeDriveTime < pauseUntil)
                                        __instance.resumeDriveTime = pauseUntil;
                                }
                            }
                        }
                    }
                }
                catch { }
            }

            public static void Postfix(AIVehicle __instance, long __state)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value || __instance == null)
                        return;

                    Vehicle veh = __instance.veh;
                    if (veh == null || veh.Pointer == IntPtr.Zero)
                        return;

                    IntPtr vehPtr = veh.Pointer;
                    float now = Time.time;
                    _claimedVehicleDestinations[vehPtr] = __instance.setVehicleDestination;

                    if (AiCore.ActiveLandMineCount > 0 &&
                        !__instance.going_in_retro &&
                        !__instance.stopToShoot &&
                        now >= __instance.resumeDriveTime &&
                        veh.transform != null &&
                        AiCore.IsLocallyOwnedVehicle(veh) &&
                        !veh.IsAirVehicle())
                    {
                        Vector3 fwd = veh.transform.forward;
                        fwd.y = 0f;
                        if (fwd.sqrMagnitude > 0.01f)
                        {
                            fwd.Normalize();
                            Vector3 vehPos = veh.transform.position;
                            if (AiCore.TryGetNearbyKnownMine(vehPos + fwd * 8.5f, 11.5f, vehiclesOnly: true, out Vector3 minePos))
                            {
                                Vector3 delta = minePos - vehPos;
                                delta.y = 0f;
                                float fwdProj = Vector3.Dot(delta, fwd);
                                Vector3 right = new Vector3(fwd.z, 0f, -fwd.x);
                                float latProj = Vector3.Dot(delta, right);
                                if (fwdProj > 0.5f && fwdProj < 18.0f && Mathf.Abs(latProj) < 4.5f)
                                {
                                    float steerSide = latProj >= 0f ? -1f : 1f;
                                    veh.Move(new Vector2(steerSide * 0.85f, 0.45f));
                                }
                            }
                        }
                    }

                    if (VisionConfig.EnableSmartApcDismount.Value)
                    {
                        if (!_nextApcCheckTime.TryGetValue(vehPtr, out float nextCheck) || now >= nextCheck)
                        {
                            _nextApcCheckTime[vehPtr] = now + 0.75f;
                            TryDismountTransportUnderThreat(veh, __instance, false);
                        }
                    }

                    if (VisionConfig.EnableCombinedArmsPrioritySupport.Value || VisionConfig.EnableTankStandoffAndReverse.Value)
                    {
                        if (!_nextVehicleSupportSteerTime.TryGetValue(vehPtr, out float nextSteer) || now >= nextSteer)
                        {
                            _nextVehicleSupportSteerTime[vehPtr] = now + 2.5f;
                            TrySteerVehicleTowardKnownThreat(__instance, veh, now);
                        }
                    }
                }
                catch { }
                finally
                {
                    HitchProfiler.End(HitchBucket.GroundVehicles, __state);
                }
            }
        }

        [HarmonyPatch(typeof(SyncVehicle), nameof(SyncVehicle.SyncVehiclePosition))]
        public static class SyncVehicle_SyncVehiclePosition_Patch
        {
            public struct PreSyncPosState
            {
                public Vector3 Pos;
                public bool HasPos;
            }

            public static void Prefix(SyncVehicle __instance, out PreSyncPosState __state)
            {
                __state = default;
                try
                {
                    if (!VisionConfig.EnableMod.Value || __instance == null)
                        return;

                    Vehicle veh = __instance.vehicle;
                    if (veh == null || veh.Pointer == IntPtr.Zero || veh.transform == null)
                        return;

                    if (!veh.OwnDriverOrVehicle() && !veh.IsAirVehicle())
                    {
                        __state.Pos = veh.transform.position;
                        __state.HasPos = true;
                    }
                }
                catch { }
            }

            public static void Postfix(SyncVehicle __instance, float __2, PreSyncPosState __state)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value || __instance == null)
                        return;

                    Vehicle veh = __instance.vehicle;
                    if (veh == null || veh.Pointer == IntPtr.Zero)
                        return;

                    float now = Time.time;
                    IntPtr ptr = veh.Pointer;
                    _remoteVehicleMotion.TryGetValue(ptr, out RemoteVehicleMotionState state);
                    state.LastSignedSpeed = __2;
                    state.LastPacketTime = now;

                    // When SyncVehiclePosition snaps a lagging fast truck >6.0m in one frame, sweep the skipped
                    // hull corridor immediately so enemies aren't jumped straight over on the Host.
                    if (__state.HasPos && !veh.OwnDriverOrVehicle() && !veh.IsAirVehicle() && veh.transform != null)
                    {
                        Vector3 postPos = veh.transform.position;
                        Vector3 snapDelta = postPos - __state.Pos;
                        float snapDistSq = snapDelta.sqrMagnitude;
                        if (snapDistSq > 1.0f * 1.0f && snapDistSq <= 16.0f * 16.0f)
                        {
                            Vector3 sweepFrom = (state.HasSweptPos &&
                                                 now - state.LastSweptTime <= 0.55f &&
                                                 (postPos - state.LastSweptVehPos).sqrMagnitude <= 16.0f * 16.0f)
                                ? state.LastSweptVehPos
                                : __state.Pos;
                            Vector3 snapFlat = new Vector3(snapDelta.x, 0f, snapDelta.z);
                            if (snapFlat.sqrMagnitude > 0.25f)
                            {
                                Vector3 snapVelFlat = snapFlat.normalized * Mathf.Max(Mathf.Abs(__2), 3.5f);
                                RunVehicleRoadkillAndRightOfWayObbScan(veh, true, sweepFrom, postPos, false, Vector3.zero, snapVelFlat, now, true);
                            }

                            state.LastSweptVehPos = postPos;
                            state.LastSweptTime = now;
                            state.HasSweptPos = true;
                            state.LastSamplePos = postPos;
                            state.LastSampleTime = now;
                            state.HasSample = true;
                        }
                        else if (snapDistSq > 16.0f * 16.0f)
                        {
                            // Reset sweep anchor across respawn / cross-map teleports
                            state.LastSweptVehPos = postPos;
                            state.LastSweptTime = now;
                            state.HasSweptPos = true;
                            state.LastSamplePos = postPos;
                            state.LastSampleTime = now;
                            state.HasSample = true;
                        }
                    }

                    _remoteVehicleMotion[ptr] = state;
                }
                catch { }
            }
        }

        private static bool ClipSweptAxis(float coord, float delta, float halfExt, ref float tMin, ref float tMax)
        {
            if (Mathf.Abs(delta) <= 1e-5f)
                return Mathf.Abs(coord) <= halfExt;

            float inv = 1f / delta;
            float t0 = (coord - halfExt) * inv;
            float t1 = (coord + halfExt) * inv;
            if (t0 > t1)
            {
                float tmp = t0;
                t0 = t1;
                t1 = tmp;
            }
            if (t0 > tMin)
                tMin = t0;
            if (t1 < tMax)
                tMax = t1;
            return tMin <= tMax;
        }

        private static bool IsPointInSweptLocalBox(
            float solF, float solR, float solY,
            float dF, float dR, float dY,
            float halfL, float halfW, float halfY)
        {
            float tMin = 0f;
            float tMax = 1f;
            return ClipSweptAxis(solF, dF, halfL, ref tMin, ref tMax) &&
                   ClipSweptAxis(solR, dR, halfW, ref tMin, ref tMax) &&
                   ClipSweptAxis(solY, dY, halfY, ref tMin, ref tMax);
        }

        private static void RunVehicleRoadkillAndRightOfWayObbScan(
            Vehicle veh,
            bool hasSweepStart,
            Vector3 sweepStartVehPos,
            Vector3 curPos,
            bool hasLeadPos,
            Vector3 leadPos,
            Vector3 velFlat,
            float now,
            bool isRemoteDriverScan)
        {
            float horizSpeed = velFlat.magnitude;
            if (horizSpeed <= 0.85f || veh == null || veh.transform == null)
                return;

            string vehFaction = GetVehicleCrewFaction(veh);
            if (string.IsNullOrEmpty(vehFaction))
                return;

            Vector3 fwdFlat = veh.transform.forward;
            fwdFlat.y = 0f;
            fwdFlat = fwdFlat.sqrMagnitude > 0.001f ? fwdFlat.normalized : Vector3.forward;

            Vector3 rightFlat = veh.transform.right;
            rightFlat.y = 0f;
            rightFlat = rightFlat.sqrMagnitude > 0.001f ? rightFlat.normalized : Vector3.right;

            Vector4 obb = GetCachedVehicleLocalHullObb(veh);
            float halfW = obb.x + 0.50f;
            float halfL = obb.y + 0.60f;
            Vector3 localCenterOffset = rightFlat * obb.z + fwdFlat * obb.w;
            Vector3 curCenter = curPos + localCenterOffset;

            float startDist = hasSweepStart ? Vector3.Distance(sweepStartVehPos, curPos) : 0f;
            if (startDist <= 0.05f || startDist > 16.0f)
            {
                hasSweepStart = false;
                startDist = 0f;
            }
            Vector3 startCenter = hasSweepStart ? (sweepStartVehPos + localCenterOffset) : curCenter;
            Vector3 startSeg = startCenter - curCenter;
            float startF = startSeg.x * fwdFlat.x + startSeg.z * fwdFlat.z;
            float startR = startSeg.x * rightFlat.x + startSeg.z * rightFlat.z;
            float startY = startSeg.y;

            float leadDist = hasLeadPos ? Vector3.Distance(curPos, leadPos) : 0f;
            if (leadDist <= 0.05f || leadDist > 12.0f)
            {
                hasLeadPos = false;
                leadDist = 0f;
            }
            Vector3 leadCenter = hasLeadPos ? (leadPos + localCenterOffset) : curCenter;
            Vector3 leadSeg = leadCenter - curCenter;
            float leadF = leadSeg.x * fwdFlat.x + leadSeg.z * fwdFlat.z;
            float leadR = leadSeg.x * rightFlat.x + leadSeg.z * rightFlat.z;
            float leadY = leadSeg.y;

            float broadRadius = halfL + halfW + Mathf.Max(startDist, leadDist) + 2.75f;
            float broadRadiusSq = broadRadius * broadRadius;

            AiCore.EnsureWorldSnapshot();
            int solCount = AiCore.WorldSoldierCount;
            var soldiers = AiCore.WorldSoldiers;
            Vector3 travelDir = velFlat / horizSpeed;
            Vector3 anchorCenter = hasSweepStart ? startCenter : curCenter;

            for (int i = 0; i < solCount; i++)
            {
                ref AiCore.SoldierSnapshot sSnap = ref soldiers[i];
                if (sSnap.IsOnVehicle || sSnap.Ref == null)
                    continue;

                Vector3 snapDelta = sSnap.Pos - curCenter;
                if ((snapDelta.x * snapDelta.x + snapDelta.z * snapDelta.z) > broadRadiusSq)
                    continue;

                Soldier s = sSnap.Ref;
                if (s == null || !s.IsAlive || s.IsOnVehicle())
                    continue;

                AiCore.SoldierFastHandles sHandles = AiCore.GetOrCreateSoldierHandles(s);
                if (sHandles == null || sHandles.Tr == null)
                    continue;

                // Use live transform position once inside the broadphase circle so fast-moving soldiers/vehicles never miss due to snapshot age
                Vector3 livePos = sHandles.Tr.position;
                Vector3 rel = livePos - curCenter;
                float solF = rel.x * fwdFlat.x + rel.z * fwdFlat.z;
                float solR = rel.x * rightFlat.x + rel.z * rightFlat.z;
                float solY = rel.y;

                bool hitHull = (Mathf.Abs(solY) <= 2.85f && Mathf.Abs(solF) <= halfL && Mathf.Abs(solR) <= halfW) ||
                               (hasSweepStart && IsPointInSweptLocalBox(solF, solR, solY, startF, startR, startY, halfL, halfW, 2.85f)) ||
                               (hasLeadPos && IsPointInSweptLocalBox(solF, solR, solY, leadF, leadR, leadY, halfL, halfW, 2.85f));
                if (!hitHull)
                    continue;

                string sFac = !string.IsNullOrEmpty(sSnap.Faction) ? sSnap.Faction : AiCore.GetCachedFaction(sHandles, now);
                bool sameFaction = !string.IsNullOrEmpty(sFac) &&
                                   AiCore.AreSameSideFactions(vehFaction, sFac);

                if (sameFaction)
                {
                    if (VisionConfig.EnableFriendlyVehicleRightOfWay.Value)
                    {
                        s.DisableHitByVehicleFor(1.6f);
                        if (!sSnap.IsPlayer && AiCore.IsLocallyOwnedBot(s))
                        {
                            TryNudgeFriendlySoldierAside(s, veh, curCenter, travelDir, rightFlat, obb.x, now, true);
                        }
                    }
                }
                else if (s.AllowHitByVehicle)
                {
                    // On remote-driver scan (Host watching client vehicle), only the soldier's owner applies authoritative damage;
                    // on local-driver scan (driver's own machine), allow immediate roadkill damage synced via SyncDamageWithOthers.
                    if (isRemoteDriverScan && !AiCore.IsLocallyOwnedSoldier(s))
                        continue;

                    Vector3 fromAnchor = livePos - anchorCenter;
                    float alongTravel = fromAnchor.x * travelDir.x + fromAnchor.z * travelDir.z;
                    if (alongTravel >= -obb.y * 0.45f)
                    {
                        if (!isRemoteDriverScan)
                            NotifyVehicleRoadkillHit(veh, s);
                        s.DisableHitByVehicleFor(1.0f);
                        s.Damage(1000f);
                        if (isRemoteDriverScan)
                        {
                            RemoteClientRoadkillsAssistedCount++;
                            if (VisionConfig.DebugLogging.Value)
                            {
                                AiCore.LogDebug("RoadkillAssist", $"Host swept OBB roadkill applied for remote client driver ({horizSpeed:F1}m/s).");
                            }
                        }
                    }
                }
            }
        }

        [HarmonyPatch(typeof(SyncVehicle), nameof(SyncVehicle.UpdatePosAndRot))]
        public static class SyncVehicle_UpdatePosAndRot_Patch
        {
            public static void Postfix(SyncVehicle __instance)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value || __instance == null)
                        return;

                    Vehicle veh = __instance.vehicle;
                    if (veh == null || veh.Pointer == IntPtr.Zero || veh.OwnDriverOrVehicle() || veh.transform == null)
                        return;

                    IntPtr ptr = veh.Pointer;
                    float now = Time.time;
                    _remoteVehicleMotion.TryGetValue(ptr, out RemoteVehicleMotionState state);
                    if (!veh.HasDriverAlive && !(state.LastPacketTime > 0f && now - state.LastPacketTime <= 0.45f))
                        return;

                    Vector3 curPos = veh.transform.position;
                    float dt = now - state.LastSampleTime;
                    if (!state.HasSample || dt >= 0.08f)
                    {
                        if (state.HasSample && dt > 0.04f && dt < 0.65f)
                        {
                            state.SmoothedVelocity = (curPos - state.LastSamplePos) / dt;
                            state.LastMoveTime = now;
                        }
                        state.LastSamplePos = curPos;
                        state.LastSampleTime = now;
                        state.HasSample = true;
                    }

                    if (VisionConfig.EnableCoopAirborneSpawnFix.Value && veh.IsAirVehicle())
                    {
                        if (now >= state.NextGroundCheckTime)
                        {
                            state.NextGroundCheckTime = now + 0.25f;
                            VehiclePlane vp = veh.TryCast<VehiclePlane>();
                            if (vp != null)
                            {
                                vp.RefreshGrounded();
                            }
                        }
                        _remoteVehicleMotion[ptr] = state;
                        return;
                    }

                    // Host-Authoritative Remote Client Roadkill & Friendly Right-of-Way OBB Scan:
                    // Do NOT gate on IsLocallyOwnedVehicle(veh) because when a remote client drives a truck/tank,
                    // veh.OwnDriverOrVehicle() is false on the Host while the Host owns all AI soldiers!
                    if (now >= state.NextRoadkillScanTime)
                    {
                        state.NextRoadkillScanTime = now + 0.06f;

                        Vector3 fwdFlat = veh.transform.forward;
                        fwdFlat.y = 0f;
                        fwdFlat = fwdFlat.sqrMagnitude > 0.001f ? fwdFlat.normalized : Vector3.forward;

                        Vector3 velFlat = Vector3.zero;
                        if (now - state.LastMoveTime <= 0.45f)
                        {
                            velFlat = new Vector3(state.SmoothedVelocity.x, 0f, state.SmoothedVelocity.z);
                        }
                        if (now - state.LastPacketTime <= 0.75f)
                        {
                            Vector3 packetVel = fwdFlat * state.LastSignedSpeed;
                            if (packetVel.sqrMagnitude > velFlat.sqrMagnitude)
                                velFlat = packetVel;
                        }
                        bool hasDestPos = __instance.syncDestPos.HasValue;
                        Vector3 destPos = hasDestPos ? __instance.syncDestPos.Value : curPos;
                        if (velFlat.sqrMagnitude < 0.25f && hasDestPos)
                        {
                            Vector3 fallbackVel = fwdFlat * __instance.last_speed_update;
                            if (fallbackVel.sqrMagnitude > velFlat.sqrMagnitude)
                                velFlat = fallbackVel;
                        }

                        if (velFlat.sqrMagnitude > 0.85f * 0.85f)
                        {
                            bool hasSweepStart = state.HasSweptPos && (now - state.LastSweptTime <= 0.55f);
                            RunVehicleRoadkillAndRightOfWayObbScan(veh, hasSweepStart, state.LastSweptVehPos, curPos, hasDestPos, destPos, velFlat, now, true);
                        }

                        state.LastSweptVehPos = curPos;
                        state.LastSweptTime = now;
                        state.HasSweptPos = true;
                    }

                    _remoteVehicleMotion[ptr] = state;
                }
                catch { }
            }
        }

        /// <summary>
        /// Local Driver (Human & AI) Swept OBB Roadkill Scan (VehicleWithWheels.FixedUpdate):
        /// In Unity 2022 PhysX, grounded AI bots have m_controller disabled and fast trucks can step cleanly
        /// through triggers, while vanilla's 3D 70-degree center-to-feet angle check rejects close bumper/sprocket
        /// hits. Run a throttled swept OBB scan for moving locally owned vehicles in forward AND reverse.
        /// </summary>
        [HarmonyPatch(typeof(VehicleWithWheels), nameof(VehicleWithWheels.FixedUpdate))]
        public static class VehicleWithWheels_FixedUpdate_Patch
        {
            public static void Prefix(out long __state)
            {
                __state = HitchProfiler.Begin();
            }

            public static void Postfix(VehicleWithWheels __instance, long __state)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value || ReferenceEquals(__instance, null))
                        return;

                    IntPtr ptr = __instance.Pointer;
                    if (ptr == IntPtr.Zero)
                        return;

                    float now = Time.time;
                    if (_nextLocalDriverRoadkillScanTime.TryGetValue(ptr, out float nextScan) && now < nextScan)
                        return;

                    if (__instance == null ||
                        !__instance.OwnDriverOrVehicle() ||
                        !__instance.HasDriverAlive ||
                        __instance.IsDisabled() ||
                        __instance.transform == null)
                    {
                        // Throttle unoccupied/remote vehicles for 0.25s in pure C# so FixedUpdate catch-up steps skip IL2CPP calls
                        _nextLocalDriverRoadkillScanTime[ptr] = now + 0.25f;
                        return;
                    }

                    bool isPlayerDriving = __instance.PlayerIsDriving();
                    float scanDt = isPlayerDriving ? 0.05f : 0.10f;
                    _nextLocalDriverRoadkillScanTime[ptr] = now + scanDt;

                    Rigidbody rb = __instance.rb;
                    if (rb == null || rb.isKinematic)
                        return;

                    Vector3 rbVel = rb.velocity;
                    Vector3 velFlat = new Vector3(rbVel.x, 0f, rbVel.z);
                    float minSpeed = isPlayerDriving ? 1.0f : 1.35f;
                    if (velFlat.sqrMagnitude > minSpeed * minSpeed)
                    {
                        Vector3 curPos = __instance.transform.position;
                        Vector3 sweepStartPos = curPos - velFlat * (scanDt + 0.01f);
                        RunVehicleRoadkillAndRightOfWayObbScan(__instance, true, sweepStartPos, curPos, false, Vector3.zero, velFlat, now, false);
                    }
                }
                catch { }
                finally
                {
                    HitchProfiler.End(HitchBucket.VehFixedUpdate, __state);
                }
            }
        }

        private static bool TryApplyTriggerRoadkillHit(Vehicle veh, Soldier soldier, Vector3 velFlat, bool isRemoteDriver)
        {
            float horizSpeed = velFlat.magnitude;
            if (horizSpeed <= 0.85f || veh.transform == null || soldier.transform == null)
                return false;

            Vector3 fwdFlat = veh.transform.forward;
            fwdFlat.y = 0f;
            fwdFlat = fwdFlat.sqrMagnitude > 0.001f ? fwdFlat.normalized : Vector3.forward;

            Vector3 rightFlat = veh.transform.right;
            rightFlat.y = 0f;
            rightFlat = rightFlat.sqrMagnitude > 0.001f ? rightFlat.normalized : Vector3.right;

            Vector4 obb = GetCachedVehicleLocalHullObb(veh);
            Vector3 hullCenter = veh.transform.position + rightFlat * obb.z + fwdFlat * obb.w;
            Vector3 toSoldierFlat = soldier.transform.position - hullCenter;
            toSoldierFlat.y = 0f;

            if (toSoldierFlat.sqrMagnitude < 0.64f ||
                Vector3.Dot(velFlat / horizSpeed, toSoldierFlat.normalized) > -0.25f)
            {
                if (!isRemoteDriver)
                    NotifyVehicleRoadkillHit(veh, soldier);
                soldier.DisableHitByVehicleFor(1.0f);
                soldier.Damage(1000f);
                if (isRemoteDriver)
                {
                    RemoteClientRoadkillsAssistedCount++;
                    if (VisionConfig.DebugLogging.Value)
                    {
                        AiCore.LogDebug("RoadkillAssist", $"Host trigger roadkill applied for remote client driver ({horizSpeed:F1}m/s).");
                    }
                }
                return true;
            }
            return false;
        }

        [HarmonyPatch(typeof(VehicleWithWheels), nameof(VehicleWithWheels.OnTriggerEnter))]
        public static class VehicleWithWheels_OnTriggerEnter_Patch
        {
            public static bool Prefix(VehicleWithWheels __instance, Collider __0)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value || __instance == null || __0 == null)
                        return true;

                    GameObject go = __0.gameObject;
                    if (go == null)
                        return true;

                    Soldier soldier = go.GetComponent<Soldier>();
                    if (soldier == null || soldier.Pointer == IntPtr.Zero || !soldier.IsAlive)
                        return true;

                    string vehFaction = GetVehicleCrewFaction(__instance);
                    string solFaction = AiCore.GetCachedFaction(soldier);
                    bool isSameFaction = !string.IsNullOrEmpty(vehFaction) &&
                                         !string.IsNullOrEmpty(solFaction) &&
                                         AiCore.AreSameSideFactions(vehFaction, solFaction);

                    if (isSameFaction)
                    {
                        if (!VisionConfig.EnableFriendlyVehicleRightOfWay.Value)
                            return true;

                        // Friendly soldier: grant hit immunity, temporarily disable solid hull/controller pinball bounce,
                        // and smoothly walk AI bots clear of the hull/tracks!
                        soldier.DisableHitByVehicleFor(1.65f);

                        float now = Time.time;
                        if (soldier.IsAI() && __instance.transform != null)
                        {
                            Vector4 obb = GetCachedVehicleLocalHullObb(__instance);
                            Vector3 vehFwd = __instance.transform.forward;
                            vehFwd.y = 0f;
                            vehFwd = vehFwd.sqrMagnitude > 0.001f ? vehFwd.normalized : Vector3.forward;
                            Vector3 vehRight = __instance.transform.right;
                            vehRight.y = 0f;
                            vehRight = vehRight.sqrMagnitude > 0.001f ? vehRight.normalized : Vector3.right;
                            Vector3 hullCenter = __instance.transform.position + vehRight * obb.z + vehFwd * obb.w;

                            TryNudgeFriendlySoldierAside(soldier, __instance, hullCenter, vehFwd, vehRight, obb.x, now, true);
                        }

                        // In vanilla VehicleWithWheels.OnTriggerEnter (0x18087C92B-0x18087C940), same-faction collisions
                        // still deal 1000f lethal roadkill damage whenever soldier.IsPlayer() or veh.PlayerIsInside()!
                        if ((soldier.IsPlayer() || __instance.PlayerIsInside()) && TryRecordFriendlyYieldOrSave(__instance.Pointer, now))
                        {
                            AiCore.LogDebug(
                                "FriendlyRightOfWay",
                                $"Prevented friendly {vehFaction} vehicle from roadkilling friendly {(soldier.IsPlayer() ? "player" : "soldier")}.");
                        }

                        return false;
                    }

                    bool ownVeh = __instance.OwnDriverOrVehicle();
                    bool canApplyHit = ownVeh || AiCore.IsLocallyOwnedSoldier(soldier);
                    if (!canApplyHit ||
                        !soldier.AllowHitByVehicle ||
                        soldier.IsOnVehicle() ||
                        __instance.transform == null ||
                        soldier.transform == null)
                    {
                        return true;
                    }

                    float triggerNow = Time.time;

                    // Fix 1: Remote multiplayer client driving a vehicle/truck/tank (evaluated on soldier's owner, e.g. Host).
                    if (!ownVeh)
                    {
                        bool hasMotionState = _remoteVehicleMotion.TryGetValue(__instance.Pointer, out RemoteVehicleMotionState mState);
                        bool hasRemotePacketDriver = hasMotionState &&
                                                     mState.LastPacketTime > 0f &&
                                                     triggerNow - mState.LastPacketTime <= 0.45f;
                        if (!__instance.HasDriverAlive && !hasRemotePacketDriver)
                            return true;

                        Vector3 velFlat = Vector3.zero;
                        Vector3 fwdFlat = __instance.transform.forward;
                        fwdFlat.y = 0f;
                        fwdFlat = fwdFlat.sqrMagnitude > 0.001f ? fwdFlat.normalized : Vector3.forward;

                        if (hasMotionState)
                        {
                            if (triggerNow - mState.LastMoveTime <= 0.45f)
                            {
                                velFlat = new Vector3(mState.SmoothedVelocity.x, 0f, mState.SmoothedVelocity.z);
                            }

                            if (triggerNow - mState.LastPacketTime <= 0.75f)
                            {
                                Vector3 packetVel = fwdFlat * mState.LastSignedSpeed;
                                if (packetVel.sqrMagnitude > velFlat.sqrMagnitude)
                                    velFlat = packetVel;
                            }
                        }

                        SyncVehicle syncher = __instance.vehicleSyncher;
                        if (velFlat.sqrMagnitude < 0.25f && syncher != null && syncher.syncDestPos.HasValue)
                        {
                            Vector3 fallbackVel = fwdFlat * syncher.last_speed_update;
                            if (fallbackVel.sqrMagnitude > velFlat.sqrMagnitude)
                                velFlat = fallbackVel;
                        }

                        return !TryApplyTriggerRoadkillHit(__instance, soldier, velFlat, true);
                    }

                    // Fix 2: Local driver (Client or Host) running over an enemy in forward OR reverse:
                    // Bypass vanilla's broken 3D 70-degree center-to-feet angle check & forward-only speed check using 2D XZ velocity!
                    if (__instance.HasDriverAlive)
                    {
                        Rigidbody rb = __instance.rb;
                        if (rb != null && !rb.isKinematic)
                        {
                            Vector3 rbVel = rb.velocity;
                            Vector3 rbVelFlat = new Vector3(rbVel.x, 0f, rbVel.z);
                            return !TryApplyTriggerRoadkillHit(__instance, soldier, rbVelFlat, false);
                        }
                    }

                    if (__instance.PlayerIsDriving())
                        NotifyVehicleRoadkillHit(__instance, soldier);

                    return true;
                }
                catch { }
                return true;
            }
        }

        // NOTE: Never patch base Vehicle.OnVehicleSuppressed (0x180269ea0) because it is an
        // empty 3-byte `ret 0` virtual stub folded by IL2CPP ICF across 1,239 methods!
        // VehicleWithWheels.OnVehicleSuppressed (0x18087cae0) is a unique 54-byte method,
        // and tracked/halftrack APCs are covered via aiVeh.SomeoneIsSuppressed() above.
        [HarmonyPatch(typeof(VehicleWithWheels), nameof(VehicleWithWheels.OnVehicleSuppressed))]
        public static class Vehicle_OnVehicleSuppressed_Patch
        {
            public static void Postfix(VehicleWithWheels __instance)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value ||
                        !VisionConfig.EnableSmartApcDismount.Value ||
                        __instance == null)
                    {
                        return;
                    }

                    TryDismountTransportUnderThreat(__instance, null, true);
                }
                catch { }
            }
        }
    }
}
