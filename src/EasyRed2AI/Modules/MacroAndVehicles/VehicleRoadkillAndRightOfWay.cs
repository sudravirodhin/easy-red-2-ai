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
        internal static Vector4 GetCachedVehicleLocalHullObb(Vehicle veh)
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

        internal static void UpdatePlayerVehicleInfantryEvacuation()
        {
            if (!VisionConfig.EnableMod.Value || !VisionConfig.EnableFriendlyVehicleRightOfWay.Value || Time.timeScale <= 0.01f)
                return;

            float now = Time.time;
            AiCore.EnsureVehicleDangerCorridorsUpdated(now);
            int corrCount = AiCore.ActiveVehicleDangerCorridorCount;
            if (corrCount <= 0)
                return;

            bool worldSnapEnsured = false;
            for (int i = 0; i < corrCount; i++)
            {
                ref AiCore.VehicleDangerCorridor c = ref AiCore.ActiveVehicleDangerCorridors[i];
                if (!c.IsPlayerDriven || c.Ref == null || c.Ref.Pointer == IntPtr.Zero || string.IsNullOrEmpty(c.Faction))
                    continue;

                AiCore.VehicleFastHandles vh = AiCore.GetOrCreateVehicleHandles(c.Ref);
                if (vh == null || now < vh.NextPlayerPedestrianSweepTime)
                    continue;

                vh.NextPlayerPedestrianSweepTime = now + 0.18f;
                if (!worldSnapEnsured)
                {
                    AiCore.EnsureWorldSnapshot();
                    worldSnapEnsured = true;
                }

                bool dummyPlayer = false;
                bool dummyAi = false;
                float minBrakeFwd = Mathf.Max(0.25f, c.HalfLength * 0.20f);
                float brakeHalfWidth = c.HalfWidth + 0.85f;

                Squad crewSquad = c.Ref.GetSquadInside();
                if (crewSquad != null && crewSquad.units != null)
                {
                    var crewUnits = crewSquad.units;
                    int crewCount = crewUnits.Count;
                    for (int u = 0; u < crewCount; u++)
                    {
                        EvaluateFriendlySoldierInVehicleCorridor(
                            crewUnits[u], c.Ref, c.Faction, c.HullCenter, c.TravelDirFlat, c.RightFlat,
                            c.HalfWidth, c.MinFwd, minBrakeFwd, c.MaxFwd, c.MaxFwd, c.CorridorHalfWidth, brakeHalfWidth,
                            now, ref dummyPlayer, ref dummyAi);
                    }
                }

                int sqCount = AiCore.WorldSquadCount;
                if (sqCount <= 0)
                    continue;

                IntPtr crewSqPtr = crewSquad != null ? crewSquad.Pointer : IntPtr.Zero;
                var squads = AiCore.WorldSquads;
                var soldiers = AiCore.WorldSoldiers;
                for (int s = 0; s < sqCount; s++)
                {
                    ref AiCore.SquadSnapshot sqSnap = ref squads[s];
                    if (sqSnap.Ptr == crewSqPtr || sqSnap.UnitCount == 0 || string.IsNullOrEmpty(sqSnap.Faction))
                        continue;

                    if (!AiCore.AreSameSideFactions(c.Faction, sqSnap.Faction))
                        continue;

                    if ((sqSnap.RefPos - c.HullCenter).sqrMagnitude > 48f * 48f)
                        continue;

                    int uEnd = sqSnap.UnitStartIndex + sqSnap.UnitCount;
                    for (int u = sqSnap.UnitStartIndex; u < uEnd; u++)
                    {
                        Soldier f = soldiers[u].Ref;
                        if (f != null)
                        {
                            EvaluateFriendlySoldierInVehicleCorridor(
                                f, c.Ref, c.Faction, c.HullCenter, c.TravelDirFlat, c.RightFlat,
                                c.HalfWidth, c.MinFwd, minBrakeFwd, c.MaxFwd, c.MaxFwd, c.CorridorHalfWidth, brakeHalfWidth,
                                now, ref dummyPlayer, ref dummyAi);
                        }
                    }
                }
            }
        }

        internal static bool TryEvadeFriendlyVehicleCorridor(
            Soldier s,
            SoldierAI sAi,
            AiCore.SoldierFastHandles sHandles,
            Vehicle veh,
            Vector3 hullCenter,
            Vector3 travelDir,
            Vector3 vehRight,
            float halfWidth,
            float now,
            bool ignoreHullCollision)
        {
            if (sHandles == null || sHandles.Tr == null || s == null || !s.IsAI() || !AiCore.IsLocallyOwnedBot(sHandles) || AiCore.IsSoldierInVehicleOrTransition(s))
                return false;

            s.DisableHitByVehicleFor(2.0f);
            if (ignoreHullCollision && veh != null)
            {
                TemporarilyIgnoreFriendlyVehicleCollision(s, veh, now + 1.85f);
            }

            if ((sHandles.MoveOverride.Kind == AiCore.BotMoveOverrideKind.GrenadeReflex && now < sHandles.MoveOverride.ExpireTime) ||
                s.isInBodyAnimation ||
                (s.Params != null && !s.Params.canMove))
            {
                return false;
            }

            if (s.UsingBipod())
                s.UndeployBipodNow();

            sHandles.Stance.NoProneUntil = Mathf.Max(sHandles.Stance.NoProneUntil, now + 2.5f);
            if (s.IsOnCover() || sHandles.Stance.LocalCoverProtectedUntil > now || sHandles.Stance.CoverSettledUntil > now)
            {
                sHandles.Stance.LocalCoverProtectedUntil = 0f;
                sHandles.Stance.CoverSettledUntil = 0f;
                sHandles.Goap.Plan.LockUnitCoverChange = false;
                if (s.IsOnCover())
                    s.CoverPosition(null);
            }

            if (s.m_pose == SoldierPose.Prone)
            {
                if (!ReferenceEquals(sAi, null) && sAi != null)
                    SetBotPose(sAi, s, SoldierPose.Idle, true);
                else
                    s.SetPose(SoldierPose.Idle, false);
            }

            if (sHandles.MoveOverride.Kind == AiCore.BotMoveOverrideKind.VehicleEvade &&
                now < sHandles.MoveOverride.ExpireTime &&
                now < sHandles.Combat.NextFriendlyStepAsideTime)
            {
                if (!ReferenceEquals(sAi, null) && sAi != null &&
                    !AiCore.IsDirectSegmentBlockedByWall(sHandles.Tr.position, sHandles.MoveOverride.Dest, out _, ignoreVehicleObstacles: true))
                {
                    sAi.MoveDirectlyToward(sHandles.MoveOverride.Dest, 1.55f);
                }
                return true;
            }

            if (now < sHandles.Combat.NextFriendlyStepAsideTime)
                return false;

            Vector3 sPos = sHandles.Tr.position;
            Vector3 delta = sPos - hullCenter;
            float latSigned = Vector3.Dot(delta, vehRight);
            float sideSign = latSigned >= 0f ? 1f : -1f;
            float neededLat = Mathf.Max(2.2f, (halfWidth + 2.65f) - Mathf.Abs(latSigned));
            int solidMask = AiCore.GetSolidEnvironmentMask();
            Transform sRoot = sHandles.RootTr;
            bool onBridge = VisionConfig.EnableBridgeCrossingDiscipline.Value && AiCore.IsOnBridgeOrNarrowCrossing(sPos);

            Vector3 candidatePos = Vector3.zero;
            bool primaryValid = false;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                Vector3 sideDir = vehRight * (attempt == 1 ? -sideSign : sideSign);
                Vector3 cand = attempt == 2
                    ? (sPos + (sideDir * 0.85f + travelDir * 0.52f) * neededLat)
                    : (sPos + sideDir * neededLat + travelDir * 0.70f);
                Vector3 rayDir = cand - sPos;
                rayDir.y = 0f;
                float rayDist = Mathf.Max(1.0f, rayDir.magnitude);
                if (rayDist > 0.001f)
                    rayDir /= rayDist;

                bool blockedBySolidEnv =
                    AiCore.RaycastEnvironment(sPos + Vector3.up * 0.75f, rayDir, Mathf.Min(rayDist, 3.2f), sRoot, out RaycastHit evadeEnvHit) &&
                    !AiCore.IsVehicleCollider(evadeEnvHit.collider);
                if (!blockedBySolidEnv &&
                    Physics.Raycast(cand + Vector3.up * 0.85f, Vector3.down, out RaycastHit groundHit, 2.65f, solidMask, QueryTriggerInteraction.Ignore) &&
                    !AiCore.IsVehicleCollider(groundHit.collider))
                {
                    cand.y = groundHit.point.y;
                    if (!onBridge || AiCore.IsSafeBridgeDeckStep(sPos, cand, out cand))
                    {
                        candidatePos = cand;
                        primaryValid = true;
                        break;
                    }
                }
            }

            sHandles.Combat.NextFriendlyStepAsideTime = now + 0.85f;
            if (!primaryValid)
                return false;

            if (ReferenceEquals(sAi, null) || sAi == null)
                sAi = sHandles.Ai ?? s.aiController;
            if (ReferenceEquals(sAi, null) || sAi == null)
                return false;

            AiCore.SetBotMoveOverride(sHandles, AiCore.BotMoveOverrideKind.VehicleEvade, candidatePos, now + 1.85f);
            if ((candidatePos - sPos).sqrMagnitude <= 4.4f * 4.4f &&
                !AiCore.IsDirectSegmentBlockedByWall(sPos, candidatePos, out _, ignoreVehicleObstacles: true))
            {
                sAi.MoveDirectlyToward(candidatePos, 1.55f);
            }
            else
            {
                AiCore.SetBotMoveDestinationAndRefreshPath(sAi, candidatePos, 0.25f);
            }
            return true;
        }

        private static void EvaluateFriendlySoldierInVehicleCorridor(
            Soldier s,
            Vehicle veh,
            string vehFaction,
            Vector3 vehPos,
            Vector3 travelDir,
            Vector3 vehRight,
            float halfWidth,
            float minStepAsideFwd,
            float minBrakeFwd,
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
            if (fwdDist < minStepAsideFwd || fwdDist > maxStepAsideFwd)
                return;

            float latAbs = Mathf.Abs(Vector3.Dot(delta, vehRight));
            if (latAbs > stepAsideHalfWidth)
                return;

            // Protect friendly soldier from accidental low-speed contact while clearing the corridor
            s.DisableHitByVehicleFor(2.0f);

            if (s.IsAI())
            {
                bool closeHull = fwdDist <= (minBrakeFwd + 3.6f) && latAbs <= (halfWidth + 0.90f);
                TryEvadeFriendlyVehicleCorridor(s, sHandles.Ai, sHandles, veh, vehPos, travelDir, vehRight, halfWidth, now, closeHull);
            }

            if (fwdDist >= minBrakeFwd && fwdDist <= maxBrakeFwd && latAbs <= brakeHalfWidth)
            {
                if (AiCore.IsAnyHumanPlayerSoldier(s))
                    playerInBrakeCorridor = true;
                else
                    aiInBrakeCorridor = true;
            }
        }

        private static bool ShouldYieldToFriendlyInfantry(AIVehicle aiVeh, Vehicle veh, bool isReversing)
        {
            if (veh == null || veh.Pointer == IntPtr.Zero || veh.transform == null)
                return false;

            if (!AiCore.IsLocallyOwnedVehicle(veh) || AiCore.IsAnyHumanPlayerDriving(veh))
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

            Vector4 obb = GetCachedVehicleLocalHullObb(veh);
            float halfWidth = obb.x;
            float halfLength = obb.y;
            Vector3 hullCenter = vehPos + tr.right.normalized * obb.z + tr.forward.normalized * obb.w;

            float speedMs = 0f;
            MovableVehicle mv = veh.TryCast<MovableVehicle>();
            if (mv != null)
                speedMs = Mathf.Abs(mv.GetCurrentSpeed());

            float minStepAsideFwd = -halfLength * 0.35f;
            float minBrakeFwd = Mathf.Max(0.25f, halfLength * 0.20f);
            float maxStepAsideFwd = Mathf.Clamp(halfLength + 5.8f + speedMs * 0.85f, 6.0f, 17.5f);
            float maxBrakeFwd = Mathf.Clamp(halfLength + 4.2f + speedMs * 0.75f, 4.8f, 14.0f);
            float stepAsideHalfWidth = halfWidth + 1.85f;
            float brakeHalfWidth = halfWidth + 0.85f;

            bool playerInBrakeCorridor = false;
            bool aiInBrakeCorridor = false;

            Soldier player = Soldier.CurrentControlledSoldierOrNull();
            if (player != null)
            {
                EvaluateFriendlySoldierInVehicleCorridor(
                    player, veh, vehFaction, hullCenter, travelDir, vehRight,
                    halfWidth, minStepAsideFwd, minBrakeFwd, maxStepAsideFwd, maxBrakeFwd, stepAsideHalfWidth, brakeHalfWidth,
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
                        crewUnits[i], veh, vehFaction, hullCenter, travelDir, vehRight,
                        halfWidth, minStepAsideFwd, minBrakeFwd, maxStepAsideFwd, maxBrakeFwd, stepAsideHalfWidth, brakeHalfWidth,
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

                    if ((sqSnap.RefPos - hullCenter).sqrMagnitude > 48f * 48f)
                        continue;

                    int uEnd = sqSnap.UnitStartIndex + sqSnap.UnitCount;
                    for (int i = sqSnap.UnitStartIndex; i < uEnd; i++)
                    {
                        Soldier f = soldiers[i].Ref;
                        if (f != null)
                        {
                            EvaluateFriendlySoldierInVehicleCorridor(
                                f, veh, vehFaction, hullCenter, travelDir, vehRight,
                                halfWidth, minStepAsideFwd, minBrakeFwd, maxStepAsideFwd, maxBrakeFwd, stepAsideHalfWidth, brakeHalfWidth,
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

                    float now = Time.time;
                    if (!AiCore.IsAnyHumanPlayerInsideVehicle(veh) && AiCore.IsVehicleReservedByNearbyActiveHuman(veh, now))
                    {
                        float pauseUntil = now + 0.45f;
                        if (__instance.resumeDriveTime < pauseUntil)
                            __instance.resumeDriveTime = pauseUntil;
                        return;
                    }

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

                    AiCore.VehicleFastHandles bvh = AiCore.GetOrCreateVehicleHandles(veh);
                    if (bvh != null && Time.time < bvh.BoardingWaitUntil)
                    {
                        bool waitForBoarders = false;
                        if (!__instance.hasEnemy && veh.HasSpace())
                        {
                            Squad sq = veh.GetSquadInside();
                            if (sq != null && sq.units != null)
                            {
                                int uc = sq.units.Count;
                                for (int ui = 0; ui < uc; ui++)
                                {
                                    Soldier u = sq.units[ui];
                                    if (u != null && u.IsAlive && !u.IsOnVehicle() &&
                                        u.HasDestinationAssigned && u.targetDestination != null &&
                                        u.targetDestination.Pointer == veh.Pointer)
                                    {
                                        waitForBoarders = true;
                                        break;
                                    }
                                }
                            }
                        }

                        if (waitForBoarders)
                        {
                            float pauseUntil = Time.time + 0.35f;
                            if (__instance.resumeDriveTime < pauseUntil)
                                __instance.resumeDriveTime = pauseUntil;
                            return;
                        }
                        bvh.BoardingWaitUntil = 0f;
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

                    AiCore.VehicleFastHandles vehH = AiCore.GetOrCreateVehicleHandles(veh);
                    if (vehH != null)
                    {
                        vehH.AiVeh = __instance;
                        vehH.AiVehChecked = true;
                    }
                    TryEvaluateLoneDriverCombatOrBailout(__instance, veh, vehH, now);

                    if (VisionConfig.EnableSmartApcDismount.Value)
                    {
                        if (!_nextApcCheckTime.TryGetValue(vehPtr, out float nextCheck) || now >= nextCheck)
                        {
                            _nextApcCheckTime[vehPtr] = now + 0.75f;
                            TryDismountTransportUnderThreat(veh, __instance, false);
                        }
                    }

                    if (VisionConfig.EnableCombinedArmsPrioritySupport.Value)
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

        [HarmonyPatch(typeof(VehicleWithWheels), nameof(VehicleWithWheels.OnTriggerEnter))]
        public static class VehicleWithWheels_OnTriggerEnter_Patch
        {
            public static bool Prefix(VehicleWithWheels __instance, Collider __0)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value ||
                        !VisionConfig.EnableFriendlyVehicleRightOfWay.Value ||
                        __instance == null ||
                        __0 == null)
                    {
                        return true;
                    }

                    GameObject go = __0.gameObject;
                    if (go == null)
                        return true;

                    Soldier soldier = go.GetComponent<Soldier>() ?? go.GetComponentInParent<Soldier>();
                    if (soldier == null || soldier.Pointer == IntPtr.Zero || !soldier.IsAlive)
                        return true;

                    string vehFaction = GetVehicleCrewFaction(__instance);
                    string solFaction = AiCore.GetCachedFaction(soldier);
                    bool isSameFaction = !string.IsNullOrEmpty(vehFaction) &&
                                         !string.IsNullOrEmpty(solFaction) &&
                                         AiCore.AreSameSideFactions(vehFaction, solFaction);

                    if (isSameFaction)
                    {
                        // Friendly soldier: grant hit immunity and smoothly step AI bots clear of the hull/tracks!
                        soldier.DisableHitByVehicleFor(2.0f);

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

                            AiCore.SoldierFastHandles sHandles = AiCore.GetOrCreateSoldierHandles(soldier);
                            TryEvadeFriendlyVehicleCorridor(soldier, sHandles != null ? sHandles.Ai : null, sHandles, __instance, hullCenter, vehFwd, vehRight, obb.x, now, true);
                        }

                        if ((AiCore.IsAnyHumanPlayerSoldier(soldier) || AiCore.IsAnyHumanPlayerInsideVehicle(__instance)) && TryRecordFriendlyYieldOrSave(__instance.Pointer, now))
                        {
                            AiCore.LogDebug(
                                "FriendlyRightOfWay",
                                $"Prevented friendly {vehFaction} vehicle from roadkilling friendly {(AiCore.IsAnyHumanPlayerSoldier(soldier) ? "player" : "soldier")}.");
                        }

                        return false;
                    }
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
