using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace EasyRed2HumanAI
{
    public static partial class VeteranAndTacticsPatches
    {
        internal struct WatchWindowCache
        {
            public Vector3 Pos;
            public float BaseAngleDeg;
            public Vector3 ResultDir;
            public Vector3 SecondaryDir;
            public bool IsWideOpen;
            public float ExpireTime;
        }

        internal struct ThreatSectorCache
        {
            public float ExpireTime;
            public Vector3 WatchDir;
            public bool HasActiveContact;
        }

        internal struct LocalCalloutRecord
        {
            public Vector3 EnemyPos;
            public float ExpireTime;
        }

        internal struct LastKnownTargetInfo
        {
            public Spottable Target;
            public Vector3 LastSeenPos;
            public float LastSeenTime;
            public bool WasInfantry;
        }

        internal struct CornerPieState
        {
            public float NextRefreshTime;
            public bool IsPieing;
            public bool RaiseRifle;
            public bool HasVerifiedCorner;
            public Vector3 PieLookDir;
            public Vector3 CachedNode0;
            public Vector3 CachedNode1;
        }

        internal struct StationaryWatchState
        {
            public float NextEvalTime;
            public bool HasWatchDir;
            public Vector3 WatchDir;
        }

        internal struct CoverEyeBlockState
        {
            public IntPtr CoverPtr;
            public SoldierPose Pose;
            public float ExpireTime;
            public bool Blocked;
        }

        public static int CornerPiesExecutedCount;

        // =====================================================================
        // Helpers: Under-Fire Attacker Tracking (Host + Co-op Remote Clients)
        // =====================================================================
        internal static void ResolveAttackerFromShooter(
            Soldier shooter,
            Vector3 fallbackOriginPos,
            out Spottable attacker,
            out Vector3 fromPos,
            out string shooterFaction)
        {
            if (shooter != null && shooter.Pointer != IntPtr.Zero && shooter.IsAlive)
            {
                shooterFaction = shooter.faction;
                if (shooter.IsOnVehicle())
                {
                    Vehicle sv = shooter.GetCurrentVehicle();
                    if (sv != null && sv.Pointer != IntPtr.Zero)
                    {
                        attacker = sv.TryCast<Spottable>();
                        fromPos = sv.transform != null
                            ? sv.transform.position
                            : (shooter.transform != null ? shooter.transform.position : fallbackOriginPos);
                        return;
                    }
                }

                attacker = shooter.TryCast<Spottable>();
                fromPos = shooter.transform != null ? shooter.transform.position : fallbackOriginPos;
                return;
            }

            attacker = null;
            fromPos = fallbackOriginPos;
            shooterFaction = null;
        }

        internal static void RecordSoldierUnderFire(
            Soldier victim,
            Soldier rawShooter,
            Spottable attacker,
            Vector3 fromPos,
            bool directHit,
            float now)
        {
            if (victim == null ||
                victim.Pointer == IntPtr.Zero ||
                !victim.IsAlive ||
                !AiCore.IsLocallyOwnedBot(victim) ||
                AiCore.IsSoldierInVehicleOrTransition(victim) ||
                victim.HasSurrended())
            {
                return;
            }

            AiCore.SoldierFastHandles vh = AiCore.GetOrCreateSoldierHandles(victim);
            if (vh == null)
                return;

            bool freshUnderFire = now >= vh.Vision.UnderFireUntil;
            if (freshUnderFire || vh.Vision.UnderFireStartedAt <= 0f || now < vh.Vision.UnderFireStartedAt)
                vh.Vision.UnderFireStartedAt = now;

            vh.Vision.UnderFireAttacker = attacker;
            vh.Vision.UnderFireAttackerPtr = attacker != null ? attacker.Pointer : IntPtr.Zero;
            vh.Vision.UnderFireFromPos = fromPos;
            vh.Vision.UnderFireUntil = Mathf.Max(vh.Vision.UnderFireUntil, now + (directHit ? 7.0f : 5.0f));

            // Immediately invalidate stale sector/watch caches so the bot snaps onto incoming fire next tick.
            vh.Vision.ThreatSector.ExpireTime = 0f;
            vh.Vision.StationaryWatch.NextEvalTime = 0f;

            AiCore.RefreshAlertnessWithoutRaisingGun(victim, 12.0f);

            float fromDistSq = vh.Tr != null ? (fromPos - vh.Tr.position).sqrMagnitude : 9999f;
            bool closeAmbush = fromDistSq <= 32.0f * 32.0f;

            // Bypass vanilla Projectile.SuppressBots' global 0.5s battle-wide cooldown when suppressed by bullets,
            // and gate suppress_time_end on close/direct hits so native Suppress() doesn't lock body rotation or RPC-sync a multi-second cringe to vanilla clients.
            if (now >= vh.Vision.NextIncomingFireSuppressTime)
            {
                vh.Vision.NextIncomingFireSuppressTime = now + (directHit ? 0.55f : 0.75f);
                float savedSuppressEnd = victim.suppress_time_end;
                if (closeAmbush || directHit)
                    victim.suppress_time_end = now + 10.0f;
                victim.Suppress(directHit ? 28 : 16, rawShooter);
                if (closeAmbush || directHit)
                    victim.suppress_time_end = savedSuppressEnd;
            }

            if (vh.Ai != null && AiCore.GetRawVisibleTargetPtr(vh.Ai) == IntPtr.Zero)
            {
                float vet01 = GetVeterancy01(victim);
                float baseWake = directHit ? Mathf.Lerp(0.18f, 0.10f, vet01) : Mathf.Lerp(0.32f, 0.18f, vet01);
                float jitter = (AiCore.GetDeterministicHash(vh.Ptr, 313) & 15) * 0.012f;
                AiCore.EnqueueUrgentUnderFireWake(vh, now + baseWake + jitter);
            }
        }

        internal static void RecordVehicleUnderFire(
            Vehicle veh,
            Spottable attacker,
            Vector3 fromPos,
            bool isHeavy,
            float now)
        {
            if (veh == null ||
                veh.Pointer == IntPtr.Zero ||
                veh.IsCoverDestroyed() ||
                !veh.CanFight())
            {
                return;
            }

            if (!AiCore.IsLocallyOwnedVehicle(veh) && !veh.PlayerIsInside())
                return;

            AiCore.VehicleFastHandles vh = AiCore.GetOrCreateVehicleHandles(veh);
            if (vh == null)
                return;

            IntPtr atkPtr = attacker != null ? attacker.Pointer : IntPtr.Zero;
            if (now >= vh.UnderFireUntil ||
                isHeavy ||
                !vh.UnderFireIsHeavy ||
                vh.UnderFireAttackerPtr == atkPtr)
            {
                vh.UnderFireAttacker = attacker;
                vh.UnderFireAttackerPtr = atkPtr;
                vh.UnderFireFromPos = fromPos;
                vh.UnderFireIsHeavy = isHeavy;
            }

            vh.UnderFireUntil = Mathf.Max(vh.UnderFireUntil, now + (vh.IsAir ? 9.5f : 8.0f));
            vh.LastDamageTime = now;
            vh.LastHazardPos = fromPos;

            if (now >= vh.NextUnderFireWakeTime)
            {
                vh.NextUnderFireWakeTime = now + 0.45f;
                var seats = veh.seats;
                if (seats != null)
                {
                    int sCount = seats.Length;
                    for (int s = 0; s < sCount; s++)
                    {
                        Seats seat = seats[s];
                        if (seat == null || !seat.hasTurret || seat.connectedTurret == null)
                            continue;

                        TurretAI tAi = seat.connectedTurret.autoPilot;
                        if (ReferenceEquals(tAi, null) || tAi == null)
                            _turretAiByTurretPtr.TryGetValue(seat.connectedTurret.Pointer, out tAi);

                        if (tAi != null && tAi.Pointer != IntPtr.Zero && tAi.nextRefreshTime > now + 0.30f)
                            tAi.nextRefreshTime = now + 0.30f;
                    }
                }
            }
        }

        internal static void NotifyProjectileImpactReaction(
            Corvostudio.Weapons.BulletInstance bullet,
            Vector3 hitDirection,
            BulletData bulletData,
            RaycastHit hit)
        {
            if (bullet == null || bulletData == null)
                return;

            Collider hitCol = hit.collider;
            if (hitCol == null || hitCol.Pointer == IntPtr.Zero)
                return;

            Soldier shooter = bullet.shooter;
            if (shooter == null || shooter.Pointer == IntPtr.Zero || !shooter.IsAlive)
                return;

            Vector3 fallbackFrom = hitDirection.sqrMagnitude > 0.0001f
                ? (hit.point - hitDirection.normalized * 60.0f)
                : hit.point;
            ResolveAttackerFromShooter(shooter, fallbackFrom, out Spottable attacker, out Vector3 fromPos, out string shooterFac);
            if (string.IsNullOrEmpty(shooterFac))
                return;

            float now = Time.time;
            bool isHeavy = bulletData.IsHighCaliber() ||
                           bulletData.IsHe() ||
                           bulletData.IsFlak() ||
                           bulletData.mmPenetration >= 12 ||
                           bulletData.caliber >= 12.5f;

            // Step A: Direct impact against a vehicle part or soldier body part (skip terrain colliders)
            IntPtr directHitFootPtr = IntPtr.Zero;
            if (!AiCore.IsTerrainCollider(hitCol))
            {
                VehicleDamagablePart vPart = hitCol.GetComponent<VehicleDamagablePart>();
                if (vPart != null && vPart.Pointer != IntPtr.Zero)
                {
                    Vehicle hitVeh = vPart.rootVeh;
                    if (hitVeh == null || hitVeh.Pointer == IntPtr.Zero)
                        hitVeh = vPart.GetRootVehicle();

                    if (hitVeh != null && hitVeh.Pointer != IntPtr.Zero)
                    {
                        string vehFac = GetVehicleCrewFaction(hitVeh);
                        if (!string.IsNullOrEmpty(vehFac) && AiCore.AreEnemyFactions(shooterFac, vehFac))
                        {
                            RecordVehicleUnderFire(hitVeh, attacker, fromPos, isHeavy || hitVeh.IsAirVehicle(), now);
                        }
                    }
                    return;
                }

                BodyPart bPart = hitCol.GetComponent<BodyPart>();
                if (bPart != null && bPart.Pointer != IntPtr.Zero && bPart.unit != null)
                {
                    Soldier hitSol = bPart.unit.TryCast<Soldier>();
                    if (hitSol != null &&
                        hitSol.Pointer != IntPtr.Zero &&
                        hitSol.IsAlive &&
                        AiCore.AreEnemyFactions(shooterFac, hitSol.faction))
                    {
                        if (hitSol.IsOnVehicle())
                        {
                            Vehicle sv = hitSol.GetCurrentVehicle();
                            if (sv != null && sv.Pointer != IntPtr.Zero)
                                RecordVehicleUnderFire(sv, attacker, fromPos, isHeavy: true, now);
                            return;
                        }

                        RecordSoldierUnderFire(hitSol, shooter, attacker, fromPos, directHit: true, now);
                        directHitFootPtr = hitSol.Pointer;
                    }
                    else
                    {
                        return;
                    }
                }
            }

            // Step B: Per-shooter throttled near-miss / companion alert using flat cached WorldSoldiers (zero allocations)
            AiCore.SoldierFastHandles shooterH = AiCore.GetOrCreateSoldierHandles(shooter);
            float minNextAlert = directHitFootPtr != IntPtr.Zero
                ? (shooterH != null ? shooterH.Vision.NextNearMissAlertTime - 0.16f : 0f)
                : (shooterH != null ? shooterH.Vision.NextNearMissAlertTime : 0f);
            if (shooterH == null || now < minNextAlert)
                return;

            shooterH.Vision.NextNearMissAlertTime = now + 0.28f;

            int count = AiCore.WorldSoldierCount;
            if (count <= 0)
                return;

            var soldiers = AiCore.WorldSoldiers;
            Vector3 impactPos = hit.point;
            float footRadiusSq = (isHeavy || directHitFootPtr != IntPtr.Zero) ? (10.5f * 10.5f) : (8.0f * 8.0f);
            float vehRadiusSq = isHeavy ? (11.0f * 11.0f) : (6.0f * 6.0f);
            int footAlerted = 0;
            int vehAlerted = 0;
            IntPtr lastVehPtr = IntPtr.Zero;

            for (int i = 0; i < count; i++)
            {
                ref AiCore.SoldierSnapshot snap = ref soldiers[i];
                if (snap.Ptr == directHitFootPtr ||
                    snap.IsPlayer ||
                    snap.HasSurrendered ||
                    string.IsNullOrEmpty(snap.Faction) ||
                    snap.Ref == null ||
                    snap.Ref.Pointer == IntPtr.Zero ||
                    !snap.Ref.IsAlive ||
                    AiCore.AreSameSideFactions(shooterFac, snap.Faction))
                {
                    continue;
                }

                float dSq = (snap.Pos - impactPos).sqrMagnitude;
                if (!snap.IsOnVehicle)
                {
                    if (dSq <= footRadiusSq &&
                        footAlerted < 6 &&
                        snap.Handles != null &&
                        AiCore.IsLocallyOwnedBot(snap.Handles))
                    {
                        RecordSoldierUnderFire(snap.Ref, shooter, attacker, fromPos, directHit: false, now);
                        footAlerted++;
                    }
                }
                else if (dSq <= vehRadiusSq && vehAlerted < 2)
                {
                    Vehicle nearVeh = snap.Ref.GetCurrentVehicle();
                    if (nearVeh != null &&
                        nearVeh.Pointer != IntPtr.Zero &&
                        nearVeh.Pointer != lastVehPtr &&
                        !nearVeh.IsAirVehicle())
                    {
                        lastVehPtr = nearVeh.Pointer;
                        vehAlerted++;
                        RecordVehicleUnderFire(nearVeh, attacker, fromPos, isHeavy, now);
                    }
                }
            }
        }

        internal static void NotifyExplosionReactionOnSoldier(Soldier victim, Vector3 explosionPos, Soldier responsible)
        {
            if (victim == null || victim.Pointer == IntPtr.Zero || !victim.IsAlive)
                return;

            ResolveAttackerFromShooter(responsible, explosionPos, out Spottable attacker, out Vector3 fromPos, out string shooterFac);
            if (!string.IsNullOrEmpty(shooterFac) && !AiCore.AreEnemyFactions(shooterFac, victim.faction))
                return;

            float expDist = victim.transform != null ? Vector3.Distance(victim.transform.position, explosionPos) : -1f;
            EmergentRuntimeBridge.RecordLastHitContext(victim, 3, expDist, Time.time);
            RecordSoldierUnderFire(victim, responsible, attacker, fromPos, directHit: true, Time.time);
        }

        internal static void NotifyExplosionReactionOnVehicle(Vehicle veh, Vector3 explosionPos, Soldier responsible)
        {
            if (veh == null || veh.Pointer == IntPtr.Zero)
                return;

            ResolveAttackerFromShooter(responsible, explosionPos, out Spottable attacker, out Vector3 fromPos, out string shooterFac);
            string vehFac = GetVehicleCrewFaction(veh);
            if (!string.IsNullOrEmpty(shooterFac) && (string.IsNullOrEmpty(vehFac) || !AiCore.AreEnemyFactions(shooterFac, vehFac)))
                return;

            RecordVehicleUnderFire(veh, attacker, fromPos, isHeavy: true, Time.time);
        }

        // =====================================================================
        // Helper: Local Contact Callouts (15m) & Last-Known Target Memory
        // =====================================================================
        internal static void RecordAndBroadcastVisualContact(Soldier spotter, Spottable visibleEnemy)
        {
            AiCore.SoldierFastHandles spotterH = AiCore.GetOrCreateSoldierHandles(spotter);
            if (spotterH == null || spotterH.Tr == null ||
                !AiCore.TryResolveSpottableFast(visibleEnemy, out AiCore.SoldierFastHandles enemySolH, out AiCore.VehicleFastHandles enemyVehH))
            {
                return;
            }

            if ((enemyVehH != null && enemyVehH.IsAir) || (enemySolH == null && enemyVehH == null && visibleEnemy.IsAirVehicle()))
                return;

            Soldier enemySoldier = enemySolH != null ? enemySolH.Ref : null;
            if (enemySoldier != null && enemySoldier.IsOnVehicle())
            {
                AiCore.VehicleFastHandles evH = AiCore.GetOrCreateVehicleHandles(enemySoldier.GetCurrentVehicle());
                if (evH != null && evH.IsAir)
                    return;
            }

            IntPtr spotterPtr = spotterH.Ptr;
            Vector3 enemyPos = visibleEnemy.GetCenterOfUnit();
            float now = Time.time;

            if (VisionConfig.EnableLastKnownSuppression.Value)
            {
                bool isWheeledOrTank = enemyVehH != null ? enemyVehH.IsWheeledOrTank : (enemySolH == null && visibleEnemy.IsWheeledVehicleOrTank());
                spotterH.Vision.LastKnownTarget = new LastKnownTargetInfo
                {
                    Target = visibleEnemy,
                    LastSeenPos = enemyPos,
                    LastSeenTime = now,
                    WasInfantry = !isWheeledOrTank
                };
            }

            if (!VisionConfig.EnableLocalContactCallouts.Value)
                return;

            if (now < spotterH.Vision.NextSpotterCalloutTime)
                return;

            bool isMpWithPlayers = AiCore.IsMultiplayerWithRemotePlayers();
            spotterH.Vision.NextSpotterCalloutTime = now + (isMpWithPlayers ? 4.25f : 2.50f);

            Vector3 spotterPos = spotterH.Tr.position;
            string myFaction = AiCore.GetCachedFaction(spotterH, now);
            const float calloutRadiusSq = 15.0f * 15.0f;
            int sharedWith = 0;

            Squad mySquad = spotter.joinedSquad;
            if (mySquad != null && mySquad.units != null)
            {
                var units = mySquad.units;
                int c = units.Count;
                for (int i = 0; i < c; i++)
                {
                    Soldier ally = units[i];
                    if (ReferenceEquals(ally, null) || ally == null || !ally.IsAlive || ally.Pointer == spotterPtr || ally.IsPlayer() || ally.IsOnVehicle())
                        continue;

                    AiCore.SoldierFastHandles allyH = AiCore.GetOrCreateSoldierHandles(ally);
                    if (allyH != null && allyH.Tr != null && (allyH.Tr.position - spotterPos).sqrMagnitude <= calloutRadiusSq)
                    {
                        allyH.Vision.LocalCallout = new LocalCalloutRecord
                        {
                            EnemyPos = enemyPos,
                            ExpireTime = now + 5.5f
                        };
                        AiCore.RefreshAlertnessWithoutRaisingGun(ally, 12f);
                        sharedWith++;
                    }
                }
            }

            if (!isMpWithPlayers && sharedWith == 0)
            {
                AiCore.EnsureWorldSnapshot();
                int sqCount = AiCore.WorldSquadCount;
                if (sqCount > 0)
                {
                    IntPtr mySqPtr = mySquad != null ? mySquad.Pointer : IntPtr.Zero;
                    var squads = AiCore.WorldSquads;
                    var soldiers = AiCore.WorldSoldiers;

                    for (int s = 0; s < sqCount; s++)
                    {
                        ref AiCore.SquadSnapshot sqSnap = ref squads[s];
                        if (sqSnap.Ptr == mySqPtr || sqSnap.UnitCount == 0 || sqSnap.Faction != myFaction)
                            continue;

                        if ((sqSnap.RefPos - spotterPos).sqrMagnitude > 36f * 36f)
                            continue;

                        int end = sqSnap.UnitStartIndex + sqSnap.UnitCount;
                        for (int i = sqSnap.UnitStartIndex; i < end; i++)
                        {
                            ref AiCore.SoldierSnapshot allySnap = ref soldiers[i];
                            if (allySnap.Ptr == spotterPtr || allySnap.IsPlayer || allySnap.IsOnVehicle)
                                continue;

                            if ((allySnap.Pos - spotterPos).sqrMagnitude <= calloutRadiusSq)
                            {
                                Soldier ally = allySnap.Ref;
                                if (ally == null || !ally.IsAlive)
                                    continue;

                                if (allySnap.Handles != null)
                                {
                                    allySnap.Handles.Vision.LocalCallout = new LocalCalloutRecord
                                    {
                                        EnemyPos = enemyPos,
                                        ExpireTime = now + 5.5f
                                    };
                                }
                                AiCore.RefreshAlertnessWithoutRaisingGun(ally, 12f);
                                sharedWith++;
                            }
                        }
                    }
                }
            }

            if (sharedWith > 0)
            {
                LocalCalloutsSharedCount += sharedWith;
                if (VisionConfig.DebugLogging.Value)
                {
                    AiCore.LogDebug("LocalCallout", $"{spotter.faction} soldier shared visual contact with {sharedWith} allies within 15m.");
                }
            }
        }

        internal static bool IsSafeToThrowGrenadeAt(Soldier thrower, Vector3 targetPos, float dist, float friendlySafeRadius = 11.5f)
        {
            if (thrower == null || thrower.transform == null)
                return false;

            Vector3 myPos = thrower.transform.position;
            if (dist <= 0.01f)
            {
                dist = Vector3.Distance(myPos, targetPos);
            }

            // Never throw lethal grenades at point-blank targets (< 8.5m) where blast radius endangers thrower and squadmates
            if (dist < 8.5f)
            {
                VeteranAndTacticsPatches.FriendlyFirePrevented++;
                return false;
            }

            // Never throw grenades from indoors beneath a low ceiling (high risk of bouncing off window lintel/ceiling)
            if (AiCore.HasLowCeilingOverhead(myPos, 3.2f))
            {
                VeteranAndTacticsPatches.WallsBlocked++;
                return false;
            }

            // 1. Ensure throw arc is not blocked by an interior ceiling, window frame, or wall right in front of the thrower
            Vector3 throwOrigin = myPos + Vector3.up * 1.45f;
            Vector3 arcPeakTarget = targetPos + Vector3.up * 1.85f;
            Vector3 toArc = arcPeakTarget - throwOrigin;
            if (toArc.sqrMagnitude > 0.04f)
            {
                Vector3 arcDir = toArc.normalized;
                float checkLen = Mathf.Min(dist * 0.55f, 7.5f);
                if (AiCore.IsDirectionBlockedByEnvironment(throwOrigin, arcDir, checkLen, thrower.transform.root))
                {
                    VeteranAndTacticsPatches.WallsBlocked++;
                    return false;
                }

                Vector3 highArcDir = (arcDir + Vector3.up * 0.35f).normalized;
                if (AiCore.IsDirectionBlockedByEnvironment(throwOrigin, highArcDir, Mathf.Min(checkLen, 5.0f), thrower.transform.root))
                {
                    VeteranAndTacticsPatches.WallsBlocked++;
                    return false;
                }
            }

            // 2. Ensure no friendly soldiers (or the player) are within the blast radius or throw trajectory corridor
            string myFaction = thrower.faction;
            IntPtr throwerPtr = thrower.Pointer;
            float aiSafeRadius = Mathf.Max(friendlySafeRadius, 11.5f);
            float aiSafeRadiusSq = aiSafeRadius * aiSafeRadius;
            float playerSafeRadius = Mathf.Max(friendlySafeRadius, 15.0f);
            float playerSafeRadiusSq = playerSafeRadius * playerSafeRadius;
            Vector3 throwDir = (targetPos - myPos) / Mathf.Max(dist, 0.1f);

            Soldier player = Soldier.CurrentControlledSoldierOrNull();
            if (player != null && player.IsAlive && player.faction == myFaction && player.transform != null)
            {
                Vector3 playerPos = player.transform.position;
                if ((playerPos - targetPos).sqrMagnitude <= playerSafeRadiusSq)
                {
                    VeteranAndTacticsPatches.FriendlyFirePrevented++;
                    return false;
                }

                float playerProj = Vector3.Dot(playerPos - myPos, throwDir);
                if (playerProj > -1.5f && playerProj < dist + 4.0f)
                {
                    Vector3 closestOnArc = myPos + throwDir * Mathf.Clamp(playerProj, 0f, dist);
                    if ((playerPos - closestOnArc).sqrMagnitude <= 5.5f * 5.5f)
                    {
                        VeteranAndTacticsPatches.FriendlyFirePrevented++;
                        return false;
                    }
                }
            }

            AiCore.EnsureWorldSnapshot();
            int sqCount = AiCore.WorldSquadCount;
            if (sqCount > 0)
            {
                var squads = AiCore.WorldSquads;
                var soldiers = AiCore.WorldSoldiers;
                for (int s = 0; s < sqCount; s++)
                {
                    ref AiCore.SquadSnapshot sqSnap = ref squads[s];
                    if (sqSnap.UnitCount == 0 || sqSnap.Faction != myFaction) continue;
                    if ((sqSnap.RefPos - targetPos).sqrMagnitude > 52f * 52f) continue;

                    int end = sqSnap.UnitStartIndex + sqSnap.UnitCount;
                    for (int i = sqSnap.UnitStartIndex; i < end; i++)
                    {
                        ref AiCore.SoldierSnapshot fSnap = ref soldiers[i];
                        if (fSnap.Ptr == throwerPtr)
                            continue;

                        Vector3 fPos = fSnap.Pos;
                        if ((fPos - targetPos).sqrMagnitude <= aiSafeRadiusSq)
                        {
                            VeteranAndTacticsPatches.FriendlyFirePrevented++;
                            return false;
                        }

                        float fProj = Vector3.Dot(fPos - myPos, throwDir);
                        if (fProj > 1.0f && fProj < dist + 2.0f)
                        {
                            Vector3 closestOnArc = myPos + throwDir * Mathf.Clamp(fProj, 0f, dist);
                            if ((fPos - closestOnArc).sqrMagnitude <= 3.5f * 3.5f)
                            {
                                VeteranAndTacticsPatches.FriendlyFirePrevented++;
                                return false;
                            }
                        }
                    }
                }
            }

            return true;
        }

        internal static bool TryThrowTacticalFragGrenade(
            Soldier thrower,
            Spottable target,
            Vector3 targetPos,
            float dist,
            float minDist,
            float maxDist,
            float safeRadius,
            float now,
            bool sayVoice = true,
            Vector3? extraSafetyPos = null,
            float extraSafetyDist = 0f,
            float cooldownSeconds = 14.0f)
        {
            if (thrower == null ||
                target == null ||
                target.Pointer == IntPtr.Zero ||
                thrower.isInHandsAnimation ||
                thrower.isInBodyAnimation ||
                thrower.IsReloading ||
                thrower.inventory == null ||
                now <= thrower.throw_end_time + 5.0f)
            {
                return false;
            }

            if (dist < minDist || dist > maxDist)
                return false;

            if (extraSafetyPos.HasValue && (extraSafetyDist < minDist || extraSafetyDist > maxDist))
                return false;

            VirtualItem fragItem = AiCore.FindThrowableGrenadeInInventory(thrower, findAtGrenade: false, findFragGrenade: true);
            float checkRangeDist = extraSafetyPos.HasValue ? Mathf.Max(dist, extraSafetyDist) : dist;
            if (fragItem == null || checkRangeDist > thrower.GetGrenadeThrowRange(fragItem))
                return false;

            if (!IsSafeToThrowGrenadeAt(thrower, targetPos, dist, safeRadius))
                return false;

            if (extraSafetyPos.HasValue &&
                !IsSafeToThrowGrenadeAt(thrower, extraSafetyPos.Value, extraSafetyDist, safeRadius))
            {
                return false;
            }

            if (!AiCore.TryConsumeBotRpcBudget(0.45f))
                return false;

            AiCore.SoldierFastHandles sh = AiCore.GetOrCreateSoldierHandles(thrower);
            if (sh != null)
                sh.Vision.NextSuppressiveGrenadeTime = now + cooldownSeconds;

            thrower.Throw(fragItem, target);
            if (sayVoice)
                TrySayTacticalVoice(thrower, thrower.joinedSquad, VoiceManager.VoiceClip.granade, 0.05f);
            return true;
        }

        internal static bool TryFlushLastKnownEnemyWithGrenade(SoldierAI ai, Soldier soldier, bool allowWhileMoving = false)
        {
            if (!VisionConfig.EnableLastKnownSuppression.Value || ai == null || soldier == null || soldier.Pointer == IntPtr.Zero)
                return false;

            if (ai.visibleTarget != null ||
                (!allowWhileMoving && ai.IsMoving()) ||
                soldier.IsOnVehicle() ||
                soldier.isInHandsAnimation ||
                soldier.isInBodyAnimation ||
                soldier.IsReloading ||
                soldier.inventory == null)
            {
                return false;
            }

            AiCore.SoldierFastHandles sh = AiCore.GetOrCreateSoldierHandles(soldier);
            if (sh == null)
                return false;

            float now = Time.time;
            if (now <= soldier.throw_end_time + 6.0f || now < sh.Vision.NextSuppressiveGrenadeTime)
                return false;

            LastKnownTargetInfo lastInfo = sh.Vision.LastKnownTarget;
            if (lastInfo.LastSeenTime <= 0f || !lastInfo.WasInfantry)
                return false;

            float elapsed = now - lastInfo.LastSeenTime;
            if (elapsed < 0.35f || elapsed > 4.5f)
                return false;

            if (lastInfo.Target == null || lastInfo.Target.Pointer == IntPtr.Zero)
                return false;

            Soldier targetSoldier = lastInfo.Target.TryCast<Soldier>();
            if (AiCore.IsSoldierNonCombatantOrSurrendered(targetSoldier))
            {
                sh.Vision.LastKnownTarget = default;
                return false;
            }

            Vector3 myPos = soldier.transform.position;
            Vector3 liveTargetPos = targetSoldier.GetCenterOfUnit();
            if ((liveTargetPos - lastInfo.LastSeenPos).sqrMagnitude > 7.0f * 7.0f)
                return false;

            float dist = Vector3.Distance(myPos, lastInfo.LastSeenPos);
            float liveDist = Vector3.Distance(myPos, liveTargetPos);
            if (!TryThrowTacticalFragGrenade(
                    soldier,
                    lastInfo.Target,
                    liveTargetPos,
                    liveDist,
                    10.0f,
                    30.0f,
                    11.5f,
                    now,
                    sayVoice: true,
                    extraSafetyPos: lastInfo.LastSeenPos,
                    extraSafetyDist: dist))
            {
                return false;
            }

            SuppressiveEventsCount++;
            if (VisionConfig.DebugLogging.Value)
            {
                AiCore.LogDebug("LastKnownGrenade", $"{soldier.faction} soldier threw frag grenade at last-known enemy cover ({dist:F0}m).");
            }
            return true;
        }

        private static bool HasClearSuppressiveSightline(Soldier soldier, Vector3 aimPos, Vector3 gunPos, Vector3 toAim, float aimLen)
        {
            if (soldier.IsTowardWall() || IsBotGunClippedThroughWall(soldier))
                return false;

            Transform myRoot = soldier.transform.root;
            Vector3 spineAtBarrel = soldier.transform.position + Vector3.up * AiCore.GetBarrelHeightForPose(soldier.m_pose);
            Vector3 toAimFromBarrel = aimPos - spineAtBarrel;
            float barrelDist = toAimFromBarrel.magnitude;
            if (barrelDist > 3.5f)
            {
                float barrelCheckDist = Mathf.Min(12.0f, barrelDist * 0.45f);
                if (barrelCheckDist > 0.35f &&
                    AiCore.RaycastEnvironment(spineAtBarrel, toAimFromBarrel / barrelDist, barrelCheckDist, myRoot, out _))
                {
                    return false;
                }
            }

            if (aimLen > 1.5f &&
                AiCore.RaycastEnvironment(gunPos, toAim / aimLen, aimLen - 0.65f, myRoot, out _))
            {
                return false;
            }

            return true;
        }

        private static bool CanWeaponDeliverBlindSuppressiveFire(Soldier soldier, GenericGun heldGun, int ammoLeft, float dist)
        {
            if (soldier == null || heldGun == null || ammoLeft <= 3 || dist < 8.0f || dist > 135.0f || heldGun.DamageRange() < dist)
                return false;

            SoldierPersonality pers = GetPersonality(soldier);
            bool isDedicatedGunner = soldier.hasMg ||
                                     soldier.IsGunner() ||
                                     pers == SoldierPersonality.Gunner_Anchor ||
                                     pers == SoldierPersonality.Gunner_Overwatch ||
                                     pers == SoldierPersonality.Gunner_AssaultMG;
            if (isDedicatedGunner)
                return true;

            // Use isManualBoltOperated (+0x178 weapon archetype), NEVER raw bolt_required (+0xA8 chamber flag)!
            return !heldGun.isManualBoltOperated && ammoLeft >= 8 && dist <= 55.0f;
        }

        private static bool TryMaintainSuppressiveFireOnLastKnown(SoldierAI ai, Soldier soldier, float deltaTime)
        {
            if (!VisionConfig.EnableLastKnownSuppression.Value || ai == null)
                return false;

            AiCore.SoldierFastHandles sh = AiCore.GetOrCreateSoldierHandles(soldier);
            if (sh == null)
                return false;

            LastKnownTargetInfo lastInfo = sh.Vision.LastKnownTarget;
            if (lastInfo.LastSeenTime <= 0f || !lastInfo.WasInfantry)
                return false;

            float now = Time.time;
            float elapsed = now - lastInfo.LastSeenTime;
            if (elapsed < 0.08f)
                return false;

            if (elapsed > 2.25f)
            {
                if (elapsed < 2.65f)
                {
                    ai.shotFiresEnd = 0f;
                    soldier.SetGunTriggerPressed(false);
                }
                return false;
            }

            if (lastInfo.Target == null || lastInfo.Target.Pointer == IntPtr.Zero)
                return false;

            Soldier targetSoldier = lastInfo.Target.TryCast<Soldier>();
            if (AiCore.IsSoldierNonCombatantOrSurrendered(targetSoldier))
            {
                sh.Vision.LastKnownTarget = default;
                return false;
            }

            if (soldier.GetSuppressionValue() >= 28f || soldier.IsReloading || soldier.MustReload() || now < sh.Combat.ThrowFireHoldUntil)
                return false;

            GenericGun heldGun = soldier.GetHeldGun();
            if (heldGun == null)
                return false;

            int ammoLeft = heldGun.GetCurrentAmmoCount();
            Vector3 aimPos = lastInfo.LastSeenPos + Vector3.up * 0.45f;
            float dist = Vector3.Distance(soldier.transform.position, aimPos);
            if (!CanWeaponDeliverBlindSuppressiveFire(soldier, heldGun, ammoLeft, dist))
                return false;

            // Verify unobstructed sightline both from barrel height (for low walls/berms up to 12m) and up to the enemy's cover edge.
            Vector3 gunPos = soldier.GunPosition();
            Vector3 toAim = aimPos - gunPos;
            float aimLen = toAim.magnitude;
            if (!HasClearSuppressiveSightline(soldier, aimPos, gunPos, toAim, aimLen))
                return false;

            Spottable prevVisible = ai.visibleTarget;
            bool prevAiming = ai.aimingEnemy;
            try
            {
                // Native SoldierAI.RotateAndFireToward (0x18055E454) returns early if visibleTarget == null
                // and checks aimingEnemy (0x18055E7FA) before starting a burst.
                ai.visibleTarget = lastInfo.Target;
                ai.distFromTarget = dist;
                ai.targetInWeaponRange = true;
                ai.allowFireAtEnemy = true;
                ai.aimingEnemy = AiCore.HorizontalAngleToDirection(soldier, aimPos - soldier.transform.position) < 25f;
                ai.RotateAndFireToward(aimPos, deltaTime);
            }
            finally
            {
                ai.visibleTarget = prevVisible;
                ai.aimingEnemy = prevAiming;
            }

            if (now >= sh.Vision.NextSuppressiveBurstCountTime)
            {
                sh.Vision.NextSuppressiveBurstCountTime = now + 3.0f;
                SuppressiveEventsCount++;
                if (VisionConfig.DebugLogging.Value)
                {
                    AiCore.LogDebug("LastKnownSupp", $"{soldier.faction} gunner firing suppressive burst at last-known cover ({dist:F0}m).");
                }
            }
            return true;
        }

        internal static bool CanDeliverSuppressiveFireTowardPoint(
            SoldierAI ai,
            Soldier soldier,
            Vector3 targetWorldPos,
            Spottable knownTarget,
            float now)
        {
            if (ai == null || soldier == null || soldier.Pointer == IntPtr.Zero)
                return false;

            if (ai.HasEnemyAndCanFireAtHim && AiCore.GetRawVisibleTargetPtr(ai) != IntPtr.Zero)
                return true;

            if (!VisionConfig.EnableLastKnownSuppression.Value ||
                ai.IsMoving() ||
                soldier.IsTowardWall() ||
                soldier.IsReloading ||
                soldier.MustReload() ||
                soldier.GetSuppressionValue() >= 28f)
            {
                return false;
            }

            AiCore.SoldierFastHandles sh = AiCore.GetOrCreateSoldierHandles(soldier);
            Spottable effTarget = (knownTarget != null && knownTarget.Pointer != IntPtr.Zero)
                ? knownTarget
                : (sh != null && sh.Vision.LastKnownTarget.LastSeenTime > 0f && now - sh.Vision.LastKnownTarget.LastSeenTime <= 2.25f
                    ? sh.Vision.LastKnownTarget.Target
                    : null);
            if (effTarget == null || effTarget.Pointer == IntPtr.Zero)
                return false;

            Soldier targetSoldier = effTarget.TryCast<Soldier>();
            if (AiCore.IsSoldierNonCombatantOrSurrendered(targetSoldier))
            {
                if (sh != null && sh.Vision.LastKnownTarget.Target != null && sh.Vision.LastKnownTarget.Target.Pointer == effTarget.Pointer)
                    sh.Vision.LastKnownTarget = default;
                return false;
            }

            GenericGun heldGun = soldier.GetHeldGun();
            if (heldGun == null)
                return false;

            int ammoLeft = heldGun.GetCurrentAmmoCount();
            Vector3 aimPos = targetWorldPos + Vector3.up * 0.45f;
            Vector3 gunPos = soldier.GunPosition();
            Vector3 toAim = aimPos - gunPos;
            float dist = toAim.magnitude;
            if (!CanWeaponDeliverBlindSuppressiveFire(soldier, heldGun, ammoLeft, dist))
                return false;

            if (!HasClearSuppressiveSightline(soldier, aimPos, gunPos, toAim, dist))
                return false;

            // Seed LastKnownTarget when suppressing a verified unobstructed squad contact so MovementRoutine.Postfix
            // fires real suppressive bursts at the enemy's cover edge instead of silent phantom pinning!
            if (sh != null && (sh.Vision.LastKnownTarget.LastSeenTime <= 0f || now - sh.Vision.LastKnownTarget.LastSeenTime > 1.5f))
            {
                sh.Vision.LastKnownTarget = new LastKnownTargetInfo
                {
                    Target = effTarget,
                    LastSeenPos = targetWorldPos,
                    LastSeenTime = now - 0.15f,
                    WasInfantry = true
                };
            }

            return true;
        }

        internal static void InvalidateWatchWindowCache(IntPtr soldierPtr)
        {
            if (AiCore.TryGetSoldierHandles(soldierPtr, out AiCore.SoldierFastHandles sh))
            {
                sh.Vision.WatchWindow = default;
                sh.Vision.ThreatSector = default;
            }
        }

        // =====================================================================
        // Helper: Threat-Sector Gaze Concentration & Window-Aperture Alignment
        // =====================================================================
        public static bool HasActiveThreatSectorContact(Soldier soldier)
        {
            if (soldier == null || soldier.Pointer == IntPtr.Zero)
                return false;

            // Pure O(1) handle lookup — NEVER trigger Squad.AllSquads iteration or 18-angle window raycast
            // sweeps inside Creature.CanSee! ThreatSector is populated by SequentialUpdate & MovementRoutine.
            return AiCore.TryGetSoldierHandles(soldier.Pointer, out AiCore.SoldierFastHandles sh) &&
                   Time.time < sh.Vision.ThreatSector.ExpireTime &&
                   sh.Vision.ThreatSector.HasActiveContact;
        }

        private static readonly float[] _watchSweepOffsets = new float[]
        {
            0f, -18f, 18f, -36f, 36f, -54f, 54f, -72f, 72f, -90f, 90f, -112f, 112f, -135f, 135f, -158f, 158f, 180f
        };

        private static Vector3 CenterGazeInWindowAperture(Vector3 upperOrigin, Vector3 dir, Transform ignoreRoot, float fanT, float maxFanDeg)
        {
            float minOpenDeg = 0f;
            for (int step = 1; step <= 4; step++)
            {
                float deg = -8f * step;
                Vector3 probe = Quaternion.Euler(0f, deg, 0f) * dir;
                if (!AiCore.IsDirectionBlockedByEnvironment(upperOrigin, probe, 5.5f, ignoreRoot))
                    minOpenDeg = deg;
                else
                    break;
            }

            float maxOpenDeg = 0f;
            for (int step = 1; step <= 4; step++)
            {
                float deg = 8f * step;
                Vector3 probe = Quaternion.Euler(0f, deg, 0f) * dir;
                if (!AiCore.IsDirectionBlockedByEnvironment(upperOrigin, probe, 5.5f, ignoreRoot))
                    maxOpenDeg = deg;
                else
                    break;
            }

            float centerDeg = 0.5f * (minOpenDeg + maxOpenDeg);
            float halfWidth = 0.5f * (maxOpenDeg - minOpenDeg);
            float safeMargin = Mathf.Max(0f, halfWidth - 6f);
            float safeFan = Mathf.Clamp(fanT * maxFanDeg, -safeMargin, safeMargin);
            return (Quaternion.Euler(0f, centerDeg + safeFan, 0f) * dir).normalized;
        }

        private static Vector3 ResolveScannedWatchDirection(IntPtr ptr, in WatchWindowCache wCache, float maxFanDeg)
        {
            if (wCache.ResultDir.sqrMagnitude < 0.01f)
                return wCache.ResultDir;

            int h = AiCore.GetDeterministicHash(ptr, 97);
            int scanSlot = (int)(Time.time * 0.40f) + (h & 7);

            if (wCache.SecondaryDir.sqrMagnitude > 0.01f && (scanSlot % 5 == 1 || scanSlot % 5 == 3))
                return wCache.SecondaryDir;

            if (wCache.IsWideOpen)
            {
                int phase = scanSlot % 3;
                if (phase == 1 || phase == 2)
                {
                    float sweepDeg = Mathf.Min(22f, maxFanDeg + 8f);
                    float yaw = phase == 1 ? -sweepDeg : sweepDeg;
                    return (Quaternion.Euler(0f, yaw, 0f) * wCache.ResultDir).normalized;
                }
            }

            return wCache.ResultDir;
        }

        private static Vector3 PickClearWatchDirection(Soldier soldier, Vector3 myPos, Vector3 baseDir, float maxFanDeg)
        {
            baseDir.y = 0f;
            if (baseDir.sqrMagnitude < 0.001f)
                return Vector3.zero;
            baseDir.Normalize();

            AiCore.SoldierFastHandles sh = AiCore.GetOrCreateSoldierHandles(soldier);
            if (sh == null)
                return Vector3.zero;

            IntPtr ptr = sh.Ptr;
            float baseAngleDeg = Mathf.Atan2(baseDir.x, baseDir.z) * Mathf.Rad2Deg;
            WatchWindowCache wCache = sh.Vision.WatchWindow;
            if (Time.time < wCache.ExpireTime &&
                (myPos - wCache.Pos).sqrMagnitude < 0.25f &&
                Mathf.Abs(Mathf.DeltaAngle(baseAngleDeg, wCache.BaseAngleDeg)) < 10.0f)
            {
                return ResolveScannedWatchDirection(ptr, in wCache, maxFanDeg);
            }

            int h = AiCore.GetDeterministicHash(ptr, 97);
            float t = ((h % 1001) / 500.0f) - 1.0f;
            float fanAngle = t * maxFanDeg;
            Vector3 fannedDir = Quaternion.Euler(0f, fanAngle, 0f) * baseDir;

            Transform root = soldier.transform.root;
            // Probe at the soldier's ACTUAL stance eye height so crouched trench/parapet defenders (0.80m)
            // don't falsely treat a 1.15m dirt trench wall as "open" at 1.28m and stare straight into dirt!
            float eyeH = soldier.m_pose == SoldierPose.Prone ? 0.34f : (soldier.m_pose == SoldierPose.Crouch ? 0.80f : 1.28f);
            Vector3 upperOrigin = myPos + Vector3.up * eyeH;
            Vector3 standOrigin = myPos + Vector3.up * 1.28f;
            Vector3 sillOrigin = myPos + Vector3.up * 0.52f;

            bool fannedBlocked = AiCore.RaycastEnvironment(upperOrigin, fannedDir, 6.5f, root, out _);
            bool fannedHasWindowSill = !fannedBlocked &&
                                       soldier.m_pose != SoldierPose.Prone &&
                                       !AiCore.RaycastEnvironment(standOrigin, fannedDir, 7.5f, root, out _) &&
                                       AiCore.RaycastEnvironment(sillOrigin, fannedDir, 2.35f, root, out _);

            Vector3 finalDir;
            if (fannedHasWindowSill)
            {
                finalDir = CenterGazeInWindowAperture(standOrigin, fannedDir, root, t, maxFanDeg);
                sh.Vision.WatchWindow = new WatchWindowCache
                {
                    Pos = myPos,
                    BaseAngleDeg = baseAngleDeg,
                    ResultDir = finalDir,
                    SecondaryDir = Vector3.zero,
                    IsWideOpen = false,
                    ExpireTime = Time.time + 8.0f
                };
                return finalDir;
            }

            bool nearbyWindowDetected = false;
            bool inNarrowTrenchOrHall = false;
            if (!fannedBlocked)
            {
                Vector3 left90 = new Vector3(-fannedDir.z, 0f, fannedDir.x);
                Vector3 right90 = new Vector3(fannedDir.z, 0f, -fannedDir.x);
                bool leftCloseWall = AiCore.RaycastEnvironment(upperOrigin, left90, 2.35f, root, out _);
                bool rightCloseWall = AiCore.RaycastEnvironment(upperOrigin, right90, 2.35f, root, out _);
                if (leftCloseWall && rightCloseWall)
                {
                    inNarrowTrenchOrHall = true;
                }
                else if (soldier.m_pose != SoldierPose.Prone &&
                         ((AiCore.RaycastEnvironment(sillOrigin, left90, 2.35f, root, out _) && !AiCore.RaycastEnvironment(standOrigin, left90, 7.5f, root, out _)) ||
                          (AiCore.RaycastEnvironment(sillOrigin, right90, 2.35f, root, out _) && !AiCore.RaycastEnvironment(standOrigin, right90, 7.5f, root, out _))))
                {
                    nearbyWindowDetected = true;
                }
            }

            if (!fannedBlocked && !nearbyWindowDetected && !inNarrowTrenchOrHall)
            {
                sh.Vision.WatchWindow = new WatchWindowCache
                {
                    Pos = myPos,
                    BaseAngleDeg = baseAngleDeg,
                    ResultDir = fannedDir,
                    SecondaryDir = Vector3.zero,
                    IsWideOpen = true,
                    ExpireTime = Time.time + 6.0f
                };
                return ResolveScannedWatchDirection(ptr, in sh.Vision.WatchWindow, maxFanDeg);
            }

            Vector3 bestDir = fannedDir;
            float bestScore = -999f;
            bool bestIsWindow = false;
            Vector3 secondApproachDir = Vector3.zero;
            float secondApproachScore = -999f;

            for (int i = 0; i < _watchSweepOffsets.Length; i++)
            {
                float angleOffset = _watchSweepOffsets[i];
                Vector3 cand = i == 0 ? baseDir : (Quaternion.Euler(0f, angleOffset, 0f) * baseDir);

                float clearDist = 18.0f;
                if (AiCore.RaycastEnvironment(upperOrigin, cand, 18.0f, root, out RaycastHit upperHit))
                {
                    clearDist = upperHit.distance;
                }

                if (clearDist < 2.1f)
                    continue;

                bool isWindowAperture = soldier.m_pose != SoldierPose.Prone &&
                                        clearDist >= 7.0f &&
                                        AiCore.RaycastEnvironment(sillOrigin, cand, 2.35f, root, out RaycastHit sillHit) &&
                                        sillHit.distance < clearDist - 3.0f;

                float alignDot = Vector3.Dot(cand, baseDir);
                float score = clearDist + (alignDot * 6.5f);
                if (isWindowAperture)
                {
                    score += 18.0f;
                }

                if (score > bestScore)
                {
                    if (bestScore > -900f && Vector3.Dot(cand, bestDir) < 0.50f)
                    {
                        secondApproachDir = bestDir;
                        secondApproachScore = bestScore;
                    }
                    bestScore = score;
                    bestDir = cand;
                    bestIsWindow = isWindowAperture;
                }
                else if (clearDist >= 5.5f && Vector3.Dot(cand, bestDir) < 0.50f && score > secondApproachScore)
                {
                    secondApproachScore = score;
                    secondApproachDir = cand;
                }
            }

            finalDir = (bestIsWindow || fannedBlocked)
                ? CenterGazeInWindowAperture(upperOrigin, bestDir, root, t, maxFanDeg)
                : bestDir;

            float cacheDur = (fannedBlocked || inNarrowTrenchOrHall) ? 3.8f : 6.0f;
            sh.Vision.WatchWindow = new WatchWindowCache
            {
                Pos = myPos,
                BaseAngleDeg = baseAngleDeg,
                ResultDir = finalDir,
                SecondaryDir = (!bestIsWindow && secondApproachDir.sqrMagnitude > 0.01f) ? secondApproachDir : Vector3.zero,
                IsWideOpen = !fannedBlocked && !bestIsWindow && !inNarrowTrenchOrHall && secondApproachDir.sqrMagnitude < 0.01f,
                ExpireTime = Time.time + cacheDur
            };
            return ResolveScannedWatchDirection(ptr, in sh.Vision.WatchWindow, maxFanDeg);
        }

        private static void InsertClosestContact(
            Vector3 dir,
            float distSq,
            ref int count,
            ref Vector3 c0, ref float d0,
            ref Vector3 c1, ref float d1,
            ref Vector3 c2, ref float d2,
            ref Vector3 c3, ref float d3)
        {
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.25f)
                return;
            Vector3 norm = dir.normalized;

            if (count > 0 && Vector3.Dot(norm, c0) > 0.95f)
            {
                if (distSq < d0) { c0 = norm; d0 = distSq; }
                return;
            }
            if (count > 1 && Vector3.Dot(norm, c1) > 0.95f)
            {
                if (distSq < d1) { c1 = norm; d1 = distSq; }
                return;
            }
            if (count > 2 && Vector3.Dot(norm, c2) > 0.95f)
            {
                if (distSq < d2) { c2 = norm; d2 = distSq; }
                return;
            }
            if (count > 3 && Vector3.Dot(norm, c3) > 0.95f)
            {
                if (distSq < d3) { c3 = norm; d3 = distSq; }
                return;
            }

            if (count == 0 || distSq < d0)
            {
                c3 = c2; d3 = d2;
                c2 = c1; d2 = d1;
                c1 = c0; d1 = d0;
                c0 = norm; d0 = distSq;
                if (count < 4) count++;
            }
            else if (count == 1 || distSq < d1)
            {
                c3 = c2; d3 = d2;
                c2 = c1; d2 = d1;
                c1 = norm; d1 = distSq;
                if (count < 4) count++;
            }
            else if (count == 2 || distSq < d2)
            {
                c3 = c2; d3 = d2;
                c2 = norm; d2 = distSq;
                if (count < 4) count++;
            }
            else if (count == 3 || distSq < d3)
            {
                c3 = norm; d3 = distSq;
                if (count < 4) count++;
            }
        }

        private static bool TryGetKnownThreatSectorDirection(Soldier soldier, out Vector3 watchDir, out bool hasActiveContact)
        {
            watchDir = Vector3.zero;
            hasActiveContact = false;
            AiCore.SoldierFastHandles sh = AiCore.GetOrCreateSoldierHandles(soldier);
            if (sh == null || sh.Tr == null)
                return false;

            IntPtr ptr = sh.Ptr;
            float now = Time.time;
            ThreatSectorCache cached = sh.Vision.ThreatSector;
            bool hadPrevCache = cached.ExpireTime > 0f;
            if (now < cached.ExpireTime)
            {
                hasActiveContact = cached.HasActiveContact;
                watchDir = (sh.Vision.WatchWindow.ExpireTime > now && sh.Vision.WatchWindow.ResultDir.sqrMagnitude > 0.01f)
                    ? ResolveScannedWatchDirection(ptr, in sh.Vision.WatchWindow, hasActiveContact ? 14f : 24f)
                    : cached.WatchDir;
                return watchDir.sqrMagnitude > 0.01f;
            }

            Vector3 myPos = sh.Tr.position;
            string myFaction = AiCore.GetCachedFaction(sh, now);
            Squad mySquad = soldier.joinedSquad;
            SquadTask myTask = mySquad != null ? mySquad.currentTask : null;

            int contactCount = 0;
            Vector3 c0 = Vector3.zero, c1 = Vector3.zero, c2 = Vector3.zero, c3 = Vector3.zero;
            float d0 = float.MaxValue, d1 = float.MaxValue, d2 = float.MaxValue, d3 = float.MaxValue;

            if (now < sh.Vision.UnderFireUntil)
            {
                Vector3 ufPos = AiCore.TryGetSpottableCenter(sh.Vision.UnderFireAttacker, out Vector3 liveUfPos)
                    ? liveUfPos
                    : sh.Vision.UnderFireFromPos;
                Vector3 toUf = ufPos - myPos;
                toUf.y = 0f;
                float ufSq = toUf.sqrMagnitude;
                if (ufSq > 0.25f)
                {
                    InsertClosestContact(toUf, ufSq * 0.12f, ref contactCount, ref c0, ref d0, ref c1, ref d1, ref c2, ref d2, ref c3, ref d3);
                }
            }

            if (VisionConfig.EnableLocalContactCallouts.Value &&
                now < sh.Vision.LocalCallout.ExpireTime)
            {
                LocalCalloutRecord callout = sh.Vision.LocalCallout;
                Vector3 toCalledEnemy = callout.EnemyPos - myPos;
                toCalledEnemy.y = 0f;
                float callDistSq = toCalledEnemy.sqrMagnitude;
                if (callDistSq > 0.25f)
                {
                    InsertClosestContact(toCalledEnemy, callDistSq * 0.45f, ref contactCount, ref c0, ref d0, ref c1, ref d1, ref c2, ref d2, ref c3, ref d3);
                }
            }

            // Personal LastKnownTarget memory (18s): don't forget the sector an enemy ducked behind 3s ago!
            if (sh.Vision.LastKnownTarget.LastSeenTime > 0f)
            {
                float lkAge = now - sh.Vision.LastKnownTarget.LastSeenTime;
                if (lkAge >= 0f && lkAge <= 18.0f)
                {
                    Vector3 toLk = sh.Vision.LastKnownTarget.LastSeenPos - myPos;
                    toLk.y = 0f;
                    float lkSq = toLk.sqrMagnitude;
                    if (lkSq > 0.25f)
                    {
                        float lkWeight = lkAge <= 6.0f ? 0.65f : 1.15f;
                        InsertClosestContact(toLk, lkSq * lkWeight, ref contactCount, ref c0, ref d0, ref c1, ref d1, ref c2, ref d2, ref c3, ref d3);
                    }
                }
            }

            if (mySquad != null)
            {
                if (mySquad.Pointer != IntPtr.Zero && _squadBlackboards.TryGetValue(mySquad.Pointer, out SquadBlackboard sqBoard))
                {
                    if (sqBoard.HasActiveThreat)
                    {
                        Vector3 toSqThreat = sqBoard.ThreatPosition - myPos;
                        toSqThreat.y = 0f;
                        float sqTSq = toSqThreat.sqrMagnitude;
                        if (sqTSq > 0.25f)
                        {
                            InsertClosestContact(toSqThreat, sqTSq * 0.65f, ref contactCount, ref c0, ref d0, ref c1, ref d1, ref c2, ref d2, ref c3, ref d3);
                        }
                    }
                    else if (sqBoard.LastKnownThreatTime > 0f && (now - sqBoard.LastKnownThreatTime) <= 20.0f)
                    {
                        Vector3 toRecentSqThreat = sqBoard.LastKnownThreatPos - myPos;
                        toRecentSqThreat.y = 0f;
                        float rSq = toRecentSqThreat.sqrMagnitude;
                        if (rSq > 0.25f)
                        {
                            InsertClosestContact(toRecentSqThreat, rSq * 1.15f, ref contactCount, ref c0, ref d0, ref c1, ref d1, ref c2, ref d2, ref c3, ref d3);
                        }
                    }

                    if (sqBoard.IsConnectedToRadioNet &&
                        now < sqBoard.NetDirectiveExpireTime &&
                        sqBoard.NetDirectiveTargetPos.sqrMagnitude > 0.01f)
                    {
                        Vector3 toNetTarget = sqBoard.NetDirectiveTargetPos - myPos;
                        toNetTarget.y = 0f;
                        float nSq = toNetTarget.sqrMagnitude;
                        if (nSq > 4.0f * 4.0f && nSq <= 320.0f * 320.0f)
                        {
                            InsertClosestContact(toNetTarget, nSq * 1.35f, ref contactCount, ref c0, ref d0, ref c1, ref d1, ref c2, ref d2, ref c3, ref d3);
                        }
                    }
                }

                if (mySquad.units != null)
                {
                    var units = mySquad.units;
                    int uCount = units.Count;
                    for (int i = 0; i < uCount; i++)
                    {
                        Soldier mate = units[i];
                        if (ReferenceEquals(mate, null) || mate == null || !mate.IsAlive) continue;
                        AiCore.SoldierFastHandles mateH = AiCore.GetOrCreateSoldierHandles(mate);
                        if (mateH == null || ReferenceEquals(mateH.Ai, null) || mateH.Ai == null) continue;
                        Spottable mateTarget = mateH.Ai.visibleTarget;
                        if (!AiCore.TryGetSpottableCenter(mateTarget, out Vector3 mateEnemyPos) || mateTarget.IsAirVehicle()) continue;

                        Vector3 toEnemy = mateEnemyPos - myPos;
                        toEnemy.y = 0f;
                        float dSq = toEnemy.sqrMagnitude;
                        if (dSq > 0.25f)
                        {
                            InsertClosestContact(toEnemy, dSq * 0.55f, ref contactCount, ref c0, ref d0, ref c1, ref d1, ref c2, ref d2, ref c3, ref d3);
                        }
                    }
                }
            }

            // Acoustic Ring Buffer: distant small arms (~140m), tank cannons / emplacements (~210m), and artillery/explosions (~260m)
            if (VisionPatches.TryGetAudibleBattleDirection(myPos, myFaction, 9.0f, 11.0f, out Vector3 audibleVec, out float audibleEffDistSq))
            {
                InsertClosestContact(audibleVec, audibleEffDistSq, ref contactCount, ref c0, ref d0, ref c1, ref d1, ref c2, ref d2, ref c3, ref d3);
            }

            AiCore.EnsureWorldSnapshot();
            int sqCount = AiCore.WorldSquadCount;
            if (sqCount > 0)
            {
                IntPtr mySqPtr = mySquad != null ? mySquad.Pointer : IntPtr.Zero;
                var squads = AiCore.WorldSquads;
                var soldiers = AiCore.WorldSoldiers;

                for (int s = 0; s < sqCount; s++)
                {
                    ref AiCore.SquadSnapshot sqSnap = ref squads[s];
                    if (sqSnap.Ptr == mySqPtr || sqSnap.UnitCount == 0 || sqSnap.RefOnPlane || AiCore.AreSameSideFactions(myFaction, sqSnap.Faction))
                        continue;

                    Vector3 toSquad = sqSnap.RefPos - myPos;
                    toSquad.y = 0f;
                    float dSq = toSquad.sqrMagnitude;
                    // Allow stationary defenders to hear distant gunfire/battles up to 320m away so idle troops face the audible battle!
                    if (dSq > 320f * 320f || dSq < 0.25f)
                        continue;

                    bool isKnownContact = dSq <= 195f * 195f && sqSnap.IsSpottedOrAware;
                    Vector3 contactVector = toSquad;

                    if (!isKnownContact)
                    {
                        int end = sqSnap.UnitStartIndex + sqSnap.UnitCount;
                        for (int i = sqSnap.UnitStartIndex; i < end; i++)
                        {
                            ref AiCore.SoldierSnapshot uSnap = ref soldiers[i];
                            if ((dSq <= 180f * 180f && uSnap.IsSpotted) ||
                                (uSnap.Ref != null && VisionPatches.HasFiredRecently(uSnap.Ref, 6.0f)))
                            {
                                isKnownContact = true;
                                contactVector = uSnap.Pos - myPos;
                                contactVector.y = 0f;
                                dSq = contactVector.sqrMagnitude;
                                if (dSq > 165f * 165f)
                                    dSq *= 1.35f; // Prioritize local (< 165m) contacts over distant audible gunfire
                                break;
                            }
                        }
                    }

                    if (!isKnownContact && myTask != null && dSq <= 55f * 55f)
                    {
                        if (myTask.IsInsideTaskArea(sqSnap.RefPos, 1.05f))
                        {
                            isKnownContact = true;
                        }
                    }

                    if (isKnownContact)
                    {
                        InsertClosestContact(contactVector, dSq, ref contactCount, ref c0, ref d0, ref c1, ref d1, ref c2, ref d2, ref c3, ref d3);
                    }
                }
            }

            Soldier player = Soldier.CurrentControlledSoldierOrNull();
            if (player != null && player.IsAlive)
            {
                AiCore.SoldierFastHandles pH = AiCore.GetOrCreateSoldierHandles(player);
                if (pH != null && pH.Tr != null && !AiCore.AreSameSideFactions(myFaction, AiCore.GetCachedFaction(pH, now)))
                {
                    Vector3 toPlayer = pH.Tr.position - myPos;
                    toPlayer.y = 0f;
                    float pDistSq = toPlayer.sqrMagnitude;
                    if (pDistSq <= 175f * 175f && pDistSq > 0.25f)
                    {
                        bool playerIsKnown = player.IsSpotted ||
                                             VisionPatches.HasFiredRecently(player, 4.5f) ||
                                             (player.joinedSquad != null &&
                                              (player.joinedSquad.IsEnemyAwareOfThisSquad ||
                                               player.joinedSquad.isMarked ||
                                               player.joinedSquad.isHalfMarked ||
                                               player.joinedSquad.isMarkedPinPoint));

                        if (playerIsKnown)
                        {
                            InsertClosestContact(toPlayer, pDistSq, ref contactCount, ref c0, ref d0, ref c1, ref d1, ref c2, ref d2, ref c3, ref d3);
                        }
                    }
                }
            }

            long sqId = (mySquad != null && mySquad.Pointer != IntPtr.Zero) ? mySquad.Pointer.ToInt64() : 0L;
            bool radioConn = (mySquad != null && mySquad.Pointer != IntPtr.Zero && _squadBlackboards.TryGetValue(mySquad.Pointer, out SquadBlackboard dsBoard)) &&
                             dsBoard.IsConnectedToRadioNet;
            if (EmergentRuntimeBridge.TryGetNearestDangerStampDirection(myFaction, myPos, sqId, radioConn, now, out Vector3 toStamp, out float stampDistSq))
            {
                InsertClosestContact(toStamp, stampDistSq * 1.25f, ref contactCount, ref c0, ref d0, ref c1, ref d1, ref c2, ref d2, ref c3, ref d3);
            }

            if (contactCount > 0)
            {
                hasActiveContact = true;
                // Avoid "% contactCount" index shifts when distant 3rd/4th contacts flicker in and out;
                // only split gaze between c0 and c1 when c1 is comparable in distance to c0.
                bool useSecondContact = contactCount >= 2 &&
                                        d1 <= d0 * 1.65f &&
                                        (AiCore.GetDeterministicHash(ptr, 41) & 1) == 1;
                Vector3 chosenBase = useSecondContact ? c1 : c0;
                float maxFan = (soldier.hasMg || soldier.isMarksMan) ? 8f : 14f;
                watchDir = PickClearWatchDirection(soldier, myPos, chosenBase, maxFan);
            }
            else
            {
                hasActiveContact = false;
                Vector3 fallbackBase = sh.Tr.forward;
                bool foundObjectiveDir = false;

                // Prefer facing the squad's assigned objective when outside its perimeter so idle troops
                // look toward the contested battle area rather than an off-axis rear map edge!
                if (myTask != null && myTask.Pointer != IntPtr.Zero && !myTask.IsInsideTaskArea(myPos, 0.90f))
                {
                    Vector3 toTask = myTask.GetTaskDestination() - myPos;
                    toTask.y = 0f;
                    if (toTask.sqrMagnitude > 16.0f * 16.0f)
                    {
                        fallbackBase = toTask;
                        foundObjectiveDir = true;
                    }
                }

                if (!foundObjectiveDir &&
                    AiCore.TryGetNearestEnemyOrContestedObjectiveDirection(myFaction, myPos, out Vector3 contestedObjDir))
                {
                    fallbackBase = contestedObjDir;
                    foundObjectiveDir = true;
                }

                if (!foundObjectiveDir && AiCore.TryGetEnemySpawnDirection(myFaction, myPos, out Vector3 enemySpawnDir))
                {
                    fallbackBase = enemySpawnDir;
                }
                watchDir = PickClearWatchDirection(soldier, myPos, fallbackBase, 24f);
            }

            // Directional hysteresis: keep previous watch direction if the new sector is within ~25 degrees
            if (hadPrevCache &&
                cached.HasActiveContact == hasActiveContact &&
                cached.WatchDir.sqrMagnitude > 0.01f &&
                watchDir.sqrMagnitude > 0.01f &&
                Vector3.Dot(cached.WatchDir, watchDir) >= 0.90f)
            {
                watchDir = cached.WatchDir;
            }

            float sectorJitter = (AiCore.GetDeterministicHash(ptr, 53) % 160) * 0.01f;
            float cacheDuration = hasActiveContact ? 2.80f : 4.50f;
            sh.Vision.ThreatSector = new ThreatSectorCache
            {
                ExpireTime = Time.time + cacheDuration + sectorJitter,
                WatchDir = watchDir,
                HasActiveContact = hasActiveContact
            };

            return watchDir.sqrMagnitude > 0.01f;
        }

        private static bool TryGetActiveCornerPieDirection(
            SoldierAI ai,
            AiCore.SoldierFastHandles h,
            out Vector3 pieLookDir,
            out bool raiseRifle)
        {
            pieLookDir = Vector3.zero;
            raiseRifle = false;
            if (h == null || h.Tr == null || h.Ref == null)
                return false;

            Soldier soldier = h.Ref;
            float now = Time.time;

            CornerPieState cached = h.Vision.CornerPie;
            bool hadCache = cached.NextRefreshTime > 0f;
            if (hadCache && now < cached.NextRefreshTime)
            {
                if (!cached.IsPieing || !cached.RaiseRifle || soldier.isTowardWallTps)
                    return false;

                pieLookDir = cached.PieLookDir;
                raiseRifle = cached.RaiseRifle;
                return pieLookDir.sqrMagnitude > 0.01f;
            }

            var path = AiCore.GetCachedAiPath(h, ai);
            if (ReferenceEquals(path, null) || path.Pointer == IntPtr.Zero || path.NodesCount < 2 || soldier.isTowardWallTps)
            {
                h.Vision.CornerPie = new CornerPieState { NextRefreshTime = now + 0.32f, IsPieing = false };
                return false;
            }

            Vector3 myPos = h.Tr.position;
            Vector3 node0 = path.CurrentNode.point;
            Vector3 toCorner = node0 - myPos;
            toCorner.y = 0f;
            float distToCornerSq = toCorner.sqrMagnitude;
            if (distToCornerSq < 0.45f * 0.45f || distToCornerSq > 2.55f * 2.55f)
            {
                h.Vision.CornerPie = new CornerPieState { NextRefreshTime = now + 0.26f, IsPieing = false };
                return false;
            }

            bool alerted = now < soldier.alertEndTime || soldier.GetSuppressionValue() > 0;
            bool indoor = AiCore.HasLowCeilingOverhead(myPos, 4.2f);
            bool hasThreatContact = HasActiveThreatSectorContact(soldier);
            raiseRifle = indoor || (alerted && hasThreatContact);
            if (!raiseRifle)
            {
                // Never override MovementRoutine with angled MoveFPSOptimized when the rifle is lowered;
                // Easy Red 2's un-aimed TPS walk tree has no lateral strafe blend and causes sideways foot-sliding.
                h.Vision.CornerPie = new CornerPieState { NextRefreshTime = now + 0.34f, IsPieing = false };
                return false;
            }

            float distToCorner = Mathf.Sqrt(distToCornerSq);
            Vector3 seg0 = toCorner / distToCorner;
            Vector3 node1;

            // Reuse cached node1 when approaching the exact same corner node0 so path.A(1) (Stack.GetEnumerator)
            // and the blind-corner raycast only run ONCE per corner instead of every 0.16s!
            if (hadCache && cached.HasVerifiedCorner && (node0 - cached.CachedNode0).sqrMagnitude <= 0.09f)
            {
                if (!cached.IsPieing)
                {
                    cached.NextRefreshTime = now + 0.30f;
                    h.Vision.CornerPie = cached;
                    return false;
                }
                node1 = cached.CachedNode1;
            }
            else
            {
                node1 = path.A(1);
                Vector3 checkSeg1 = node1 - node0;
                checkSeg1.y = 0f;
                if (checkSeg1.sqrMagnitude < 0.64f)
                {
                    h.Vision.CornerPie = new CornerPieState { NextRefreshTime = now + 0.30f, IsPieing = false, HasVerifiedCorner = true, CachedNode0 = node0, CachedNode1 = node1 };
                    return false;
                }

                float bendAngle = Vector3.Angle(seg0, checkSeg1.normalized);
                if (bendAngle < 28f || bendAngle > 115f)
                {
                    h.Vision.CornerPie = new CornerPieState { NextRefreshTime = now + 0.30f, IsPieing = false, HasVerifiedCorner = true, CachedNode0 = node0, CachedNode1 = node1 };
                    return false;
                }

                // Confirm an actual solid wall/doorframe/trench corner blocks direct sight to node1 ("blind corner")!
                Vector3 eyePos = myPos + Vector3.up * 0.95f;
                Vector3 toBeyond = (node1 + Vector3.up * 0.95f) - eyePos;
                float beyondDist = toBeyond.magnitude;
                if (beyondDist < 1.2f ||
                    !AiCore.IsDirectionBlockedByEnvironment(eyePos, toBeyond / beyondDist, Mathf.Min(beyondDist - 0.3f, 6.2f), h.RootTr))
                {
                    h.Vision.CornerPie = new CornerPieState { NextRefreshTime = now + 0.30f, IsPieing = false, HasVerifiedCorner = true, CachedNode0 = node0, CachedNode1 = node1 };
                    return false;
                }
            }

            Vector3 seg1 = node1 - node0;
            seg1.y = 0f;
            if (seg1.sqrMagnitude < 0.04f)
                return false;
            seg1.Normalize();

            // Smoothly "slice the pie" within <= 38 deg of travel vector seg0 so the aimed 4-way diagonal
            // walk/jog blend tree stays crisp with zero sideways foot-skating!
            float pieT = Mathf.Clamp01(1.0f - (distToCorner - 0.45f) / 2.10f);
            Vector3 desiredPie = Vector3.Slerp(seg0, seg1, 0.28f + 0.44f * pieT).normalized;
            pieLookDir = Vector3.RotateTowards(seg0, desiredPie, 38f * Mathf.Deg2Rad, 0f).normalized;

            h.Vision.CornerPie = new CornerPieState
            {
                NextRefreshTime = now + 0.14f,
                IsPieing = true,
                RaiseRifle = true,
                HasVerifiedCorner = true,
                PieLookDir = pieLookDir,
                CachedNode0 = node0,
                CachedNode1 = node1
            };

            if (now >= h.Vision.NextCornerPieCountTime)
            {
                h.Vision.NextCornerPieCountTime = now + 4.5f;
                CornerPiesExecutedCount++;
            }
            return true;
        }

        internal static bool _inMovementRoutine;

        // =====================================================================
        // Corner-Pieing & Stationary Threat-Sector Gaze (SoldierAI.MovementRoutine)
        // =====================================================================
        [HarmonyPatch(typeof(SoldierAI), nameof(SoldierAI.MovementRoutine))]
        public static class SoldierAI_MovementRoutine_Patch
        {
            public struct MovementRoutineState
            {
                public long ProfStart;
                public AiCore.SoldierFastHandles Handles;
                public AiCore.CachedDestKind DestKind;
                public bool PieHandled;
                public bool SkipPostfix;
            }

            public static bool Prefix(SoldierAI __instance, float __0, out MovementRoutineState __state)
            {
                _inMovementRoutine = true;
                __state = new MovementRoutineState { ProfStart = HitchProfiler.Begin() };
                try
                {
                    if (!VisionConfig.EnableMod.Value || ReferenceEquals(__instance, null))
                        return true;

                    AiCore.SoldierFastHandles h = AiCore.GetOrCreateSoldierHandlesByAi(__instance);
                    if (!AiCore.IsLocallyOwnedBot(h))
                        return true;

                    __state.Handles = h;
                    Soldier soldier = h.Ref;
                    int frameCount = Time.frameCount;
                    if (h.Vision.LastMovementRoutineRenderFrame == frameCount && frameCount > 0)
                    {
                        __state.SkipPostfix = true;
                        switch (h.Vision.LastMovementRoutineCatchUpMode)
                        {
                            case 1:
                                __state.PieHandled = true;
                                soldier.SetGunTriggerPressed(false);
                                __instance.MoveOptimized(true, __0);
                                return false;
                            case 2:
                                __state.PieHandled = true;
                                soldier.SetGunTriggerPressed(false);
                                __instance.MoveOptimized(false, __0);
                                return false;
                            case 3:
                                __state.PieHandled = true;
                                soldier.SetGunTriggerPressed(false);
                                __instance.MoveFPSOptimized(0.88f, __0);
                                soldier.RotateToward(h.Vision.CornerPie.PieLookDir, __0);
                                return false;
                            case 4:
                                __state.PieHandled = true;
                                __instance.MoveFPSOptimized(1.0f, __0);
                                if (h.Combat.PointBlankFaceDir.sqrMagnitude > 0.01f)
                                    soldier.RotateToward(h.Combat.PointBlankFaceDir, __0 * 2.5f);
                                return false;
                            default:
                                return true;
                        }
                    }

                    h.Vision.LastMovementRoutineRenderFrame = frameCount;
                    h.Vision.LastMovementRoutineCatchUpMode = 0;

                    AiCore.CachedDestKind destKind = AiCore.ResolveTargetDestination(h);
                    __state.DestKind = destKind;

                    // Tank Crew Repair Priority: while assigned to a VehicleRepairTask, keep allowFireAtEnemy false
                    // (unless an enemy is point-blank <= 6.0m) so the crewman sprints to the sheltered hull side and
                    // repairs immediately without stopping to duel with a pistol/SMG!
                    if (VisionConfig.EnableSmartTankCrewRepair.Value &&
                        destKind == AiCore.CachedDestKind.VehicleRepair)
                    {
                        bool pointBlankSelfDefense = __instance.HasEnemyAndCanFireAtHim &&
                                                     __instance.distFromTarget > 0.1f &&
                                                     __instance.distFromTarget <= 6.0f;
                        if (!pointBlankSelfDefense)
                        {
                            __instance.allowFireAtEnemy = false;
                            __instance.shotFiresEnd = 0f;
                            soldier.SetGunTriggerPressed(false);
                        }
                    }

                    float now = Time.time;
                    if (soldier.IsCarried() ||
                        AiCore.IsSoldierInVehicleOrTransition(soldier) ||
                        AiCore.HasHeldMortarCached(h, __instance, now) ||
                        soldier.HasSurrended())
                    {
                        return true;
                    }

                    Vector3 myPos = h.Tr != null ? h.Tr.position : Vector3.zero;
                    bool hasActiveEscapeMove = HasActiveEscapeOrMoveIntent(__instance, soldier, h, now);

                    if (__instance.actualPose == SoldierPose.Prone && hasActiveEscapeMove)
                    {
                        __instance.actualPose = SoldierPose.Crouch;
                    }

                    if (!__instance.IsMoving() && !hasActiveEscapeMove)
                    {
                        // Native MovementRoutine (0x18055D4BD) skips RotateAndFireToward even when stationary if moveLookingTarget is false!
                        if (__instance.HasEnemyAndCanFireAtHim && !soldier.HasMovementsSlowed())
                        {
                            __instance.moveLookingTarget = true;
                        }

                        // Frame-1 Kneel on Committed Firing Halt (don't wait ~0.8s for next SequentialUpdate tick)
                        if (IsInCommittedFiringHalt(h.Ptr, now) &&
                            soldier.m_pose == SoldierPose.Idle &&
                            !soldier.isInBodyAnimation &&
                            !soldier.UsingBipod() &&
                            !soldier.IsBipodTransitioning())
                        {
                            SetBotPose(__instance, soldier, SoldierPose.Crouch);
                        }

                        return true;
                    }

                    // --- Moving Branch (__instance.IsMoving() == true) ---
                    if (soldier.isInBodyAnimation ||
                        soldier.UsingBipod() ||
                        soldier.IsBipodTransitioning())
                    {
                        return true;
                    }

                    Vector3 endpoint = GetCachedMoveEndpoint(h, __instance, soldier, out int nodesCount);
                    float remDistSq = h.Tr != null ? (endpoint - myPos).sqrMagnitude : 100f;

                    // Frame-1 Prone-Crawl Recovery: never keep belly-crawling while moving once hands/bandage lock ends.
                    // Lift straight to Idle (or Crouch for short shifts, reloads, repair, stealth, or < 0.8m overhangs).
                    if (soldier.m_pose == SoldierPose.Prone)
                    {
                        bool intentionalBandageHold =
                            now < h.Stance.BandageLowPoseUntil &&
                            (soldier.isBleeding || soldier.stopHandsAnim_end > now) &&
                            !(h.MoveOverride.Kind == AiCore.BotMoveOverrideKind.GrenadeReflex && now < h.MoveOverride.ExpireTime) &&
                            !(h.MoveOverride.Kind == AiCore.BotMoveOverrideKind.VehicleEvade && now < h.MoveOverride.ExpireTime);
                        if (intentionalBandageHold)
                        {
                            __instance.Stop(true);
                            return true;
                        }

                        if ((__instance.HasPath || __instance.moveDirectlyToward_end > now || hasActiveEscapeMove) &&
                            now >= h.Stance.NextStandLiftAttemptTime)
                        {
                            if (now < soldier.stopHandsAnim_end)
                            {
                                __instance.actualPose = SoldierPose.Crouch;
                                h.Stance.NextStandLiftAttemptTime = soldier.stopHandsAnim_end;
                                if (now < h.Stance.BandageLowPoseUntil &&
                                    !(h.MoveOverride.Kind == AiCore.BotMoveOverrideKind.GrenadeReflex && now < h.MoveOverride.ExpireTime))
                                {
                                    __instance.Stop(false);
                                    return true;
                                }
                            }
                            else
                            {
                                bool stealthCrouch = false;
                                if (soldier.IsStealth)
                                {
                                    Squad sq = AiCore.GetCachedSquad(h, now);
                                    stealthCrouch = !ReferenceEquals(sq, null) && sq.Leader != null && sq.Leader.m_pose > SoldierPose.Idle;
                                }

                                SoldierPose desiredMovePose = (destKind == AiCore.CachedDestKind.VehicleRepair ||
                                                               soldier.IsReloading ||
                                                               soldier.isInHandsAnimation)
                                    ? SoldierPose.Crouch
                                    : GetDesiredMovingPose(__instance, soldier, remDistSq, soldier.GetSuppressionValue(), stealthCrouch);
                                SetBotPose(__instance, soldier, desiredMovePose, true);
                            }
                        }
                    }
                    // Frame-1 Rise from Crouch to Upright Sprint when starting or resuming a bound (eliminates 0.8s crouch-waddle lag)
                    else if (remDistSq > 5.5f * 5.5f &&
                             destKind != AiCore.CachedDestKind.VehicleRepair &&
                             !__instance.knifeCharge &&
                             (__instance.HasPath || __instance.moveDirectlyToward_end > now) &&
                             now >= h.Stance.StanceDropHoldFireUntil &&
                             now >= h.Stance.NextStandLiftAttemptTime &&
                             (!__instance.HasEnemyAndCanFireAtHim || soldier.IsCharging()) &&
                             (soldier.m_pose != SoldierPose.Idle || __instance.actualPose != SoldierPose.Idle) &&
                             !soldier.isInHandsAnimation &&
                             !soldier.IsReloading)
                    {
                        bool stealthCrouch = false;
                        if (soldier.IsStealth)
                        {
                            Squad sq = AiCore.GetCachedSquad(h, now);
                            stealthCrouch = !ReferenceEquals(sq, null) && sq.Leader != null && sq.Leader.m_pose > SoldierPose.Idle;
                        }
                        if (GetDesiredMovingPose(__instance, soldier, remDistSq, soldier.GetSuppressionValue(), stealthCrouch) == SoldierPose.Idle)
                        {
                            SetBotPose(__instance, soldier, SoldierPose.Idle, true);
                        }
                    }

                    if (__instance.moveDirectlyToward_end > now || __instance.knifeCharge)
                    {
                        if (VisionConfig.EnablePointBlankMelee.Value &&
                            (now < h.Combat.PointBlankFaceUntil || __instance.knifeCharge) &&
                            !soldier.IsOnVehicle() &&
                            soldier.m_pose != SoldierPose.Prone &&
                            h.Tr != null)
                        {
                            Soldier pbEnemy = h.Combat.CachedMeleeEnemy;
                            if (pbEnemy == null || AiCore.IsSoldierNonCombatantOrSurrendered(pbEnemy))
                                pbEnemy = __instance.visibleTarget != null ? __instance.visibleTarget.TryCast<Soldier>() : null;

                            if (pbEnemy != null && !AiCore.IsSoldierNonCombatantOrSurrendered(pbEnemy))
                            {
                                AiCore.SoldierFastHandles pbH = AiCore.GetOrCreateSoldierHandles(pbEnemy);
                                Vector3 pbEnemyPos = (pbH != null && pbH.Tr != null) ? pbH.Tr.position : pbEnemy.transform.position;
                                Vector3 toPb = pbEnemyPos - myPos;
                                toPb.y = 0f;
                                float pbDistSq = toPb.sqrMagnitude;
                                if (pbDistSq > 0.01f && pbDistSq <= 5.2f * 5.2f)
                                {
                                    Vector3 faceDir = toPb / Mathf.Sqrt(pbDistSq);
                                    __state.PieHandled = true;
                                    h.Vision.LastMovementRoutineCatchUpMode = 4;
                                    h.Combat.PointBlankFaceDir = faceDir;
                                    __instance.MoveFPSOptimized(1.0f, __0);
                                    if (__instance.HasEnemyAndCanFireAtHim && __instance.visibleTarget != null)
                                        __instance.RotateAndFireToward(pbEnemyPos, __0);
                                    else
                                        soldier.RotateToward(faceDir, __0 * 2.5f);
                                    return false;
                                }
                            }
                        }
                        return true;
                    }

                    // Only permit firing on the move in genuine point-blank CQB (<= 8.5m) or final charge assault (<= 16m)
                    // when the target lies within a natural forward 52-degree arc of travel (never sideways FPS strafing!).
                    float dist = __instance.distFromTarget;
                    if (__instance.HasEnemyAndCanFireAtHim &&
                        dist > 0.1f &&
                        (dist <= 8.5f || (soldier.IsCharging() && dist <= 16.0f)) &&
                        h.Tr != null &&
                        AiCore.TryGetSpottableCenter(__instance.visibleTarget, out Vector3 cqbPos))
                    {
                        Vector3 fwdRef = __instance.dir;
                        fwdRef.y = 0f;
                        if (fwdRef.sqrMagnitude < 0.01f)
                        {
                            fwdRef = h.Tr.forward;
                            fwdRef.y = 0f;
                        }

                        Vector3 toCqb = cqbPos - myPos;
                        toCqb.y = 0f;
                        if (fwdRef.sqrMagnitude > 0.01f && toCqb.sqrMagnitude > 0.01f && Vector3.Angle(fwdRef, toCqb) <= 52f)
                        {
                            __instance.moveLookingTarget = true;
                            return true;
                        }
                    }

                    // Clear stale moveLookingTarget when moving away from enemies so TPS animator never slides backward.
                    if (__instance.moveLookingTarget)
                        __instance.moveLookingTarget = false;

                    if (VisionConfig.EnableThreatSectorConcentration.Value &&
                        !__instance.HasEnemyAndCanFireAtHim &&
                        !soldier.IsCharging() &&
                        !soldier.IsCarryingBody() &&
                        !soldier.isTowardWallTps &&
                        soldier.m_pose != SoldierPose.Prone &&
                        !h.Goap.Plan.IsEvacuatingBailedCrew &&
                        !IsInIndividualOrGoapFallback(h.Ptr, now) &&
                        TryGetActiveCornerPieDirection(__instance, h, out Vector3 pieDir, out bool raiseRifle) &&
                        raiseRifle)
                    {
                        __state.PieHandled = true;
                        h.Vision.LastMovementRoutineCatchUpMode = 3;
                        soldier.SetGunTriggerPressed(false);
                        __instance.MoveFPSOptimized(0.88f, __0);
                        soldier.RotateToward(pieDir, __0);
                        return false;
                    }

                    // Committed Forward Bound: bypass vanilla's 2.0s ShouldLookTargetWhileMoving FPS strafe-shoot parity,
                    // vanilla's dist <= 60m sprint=false casual firefight stroll, and vanilla's NodesCount == 1 long slow-walk,
                    // while letting vanilla's MoveFPSOptimized settle smoothly inside 1.65m of the final endpoint and
                    // dropping sprint during sharp (>55 deg) turns so bots do not orbit waypoints.
                    if (remDistSq > 1.65f * 1.65f &&
                        (__instance.HasEnemyAndCanFireAtHim || (nodesCount >= 0 && nodesCount < 2)))
                    {
                        __state.PieHandled = true;
                        soldier.SetGunTriggerPressed(false);
                        bool sharpTurn = false;
                        if (h.Tr != null)
                        {
                            Vector3 steerDir = __instance.dir;
                            steerDir.y = 0f;
                            if (steerDir.sqrMagnitude < 0.01f)
                            {
                                steerDir = endpoint - myPos;
                                steerDir.y = 0f;
                            }
                            Vector3 fwd = h.Tr.forward;
                            fwd.y = 0f;
                            if (steerDir.sqrMagnitude > 0.01f && fwd.sqrMagnitude > 0.01f)
                            {
                                sharpTurn = Vector3.Angle(fwd, steerDir) > 55.0f;
                            }
                        }
                        bool shouldSprint = soldier.m_pose != SoldierPose.Prone &&
                                            !soldier.IsStealth &&
                                            !sharpTurn;
                        h.Vision.LastMovementRoutineCatchUpMode = shouldSprint ? (byte)1 : (byte)2;
                        __instance.MoveOptimized(shouldSprint, __0);
                        return false;
                    }
                }
                catch { }
                return true;
            }

            public static void Postfix(SoldierAI __instance, float __0, MovementRoutineState __state)
            {
                try
                {
                    AiCore.SoldierFastHandles h = __state.Handles;
                    if (h == null || __state.PieHandled || __state.SkipPostfix)
                        return;

                    Soldier soldier = h.Ref;
                    IntPtr ptr = h.Ptr;

                    float now = Time.time;
                    if (now < h.Combat.PendingThrowFaceUntil &&
                        h.Combat.PendingThrowFaceDir.sqrMagnitude > 0.01f &&
                        !AiCore.IsSoldierInVehicleOrTransition(soldier) &&
                        !soldier.HasSurrended() &&
                        !soldier.IsCarried())
                    {
                        AiCore.RotateBodyWithoutRaisingGun(soldier, h.Combat.PendingThrowFaceDir, __0 * 1.65f);
                        if (soldier.isInHandsAnimation)
                        {
                            h.Vision.IsTurningToWatchSector = false;
                            return;
                        }
                    }

                    bool isMovingNow = __instance.IsMoving();
                    if (isMovingNow)
                    {
                        h.Vision.WasMovingLastWatchTick = true;
                        if (__state.DestKind != AiCore.CachedDestKind.VehicleRepair &&
                            __state.DestKind != AiCore.CachedDestKind.MedicHeal &&
                            !__instance.HasEnemyAndCanFireAtHim &&
                            !AiCore.HasHeldMortarCached(h, __instance, now) &&
                            !AiCore.IsSoldierInVehicleOrTransition(soldier) &&
                            !soldier.UsingBipod() &&
                            !soldier.IsBipodTransitioning() &&
                            !soldier.isInBodyAnimation &&
                            !soldier.isInHandsAnimation &&
                            !soldier.HasSurrended() &&
                            !soldier.IsCarried() &&
                            !soldier.IsCarryingBody())
                        {
                            Vector3 reflexDir = Vector3.zero;
                            bool hasReflexDir = false;
                            Vector3 obsPos = h.Tr != null ? h.Tr.position : Vector3.zero;
                            if (now < h.Vision.UnderFireUntil)
                            {
                                Vector3 ufPos = AiCore.TryGetSpottableCenter(h.Vision.UnderFireAttacker, out Vector3 liveUfPos)
                                    ? liveUfPos
                                    : h.Vision.UnderFireFromPos;
                                Vector3 toUf = ufPos - obsPos;
                                toUf.y = 0f;
                                if (toUf.sqrMagnitude > 0.04f)
                                {
                                    reflexDir = toUf.normalized;
                                    hasReflexDir = true;
                                }
                            }
                            else if (now >= h.Vision.StationaryWatch.NextEvalTime)
                            {
                                h.Vision.StationaryWatch.NextEvalTime = now + 0.15f;
                                string obsFac = AiCore.GetCachedFaction(h, now);
                                if (!string.IsNullOrEmpty(obsFac) &&
                                    VisionPatches.TryGetNearestCloseAcousticThreat(obsPos, obsFac, now, out Vector3 mAcousticPos, out float mAcousticDist, out _) &&
                                    mAcousticDist <= 16.0f)
                                {
                                    Vector3 toSound = mAcousticPos - obsPos;
                                    toSound.y = 0f;
                                    if (toSound.sqrMagnitude > 0.04f)
                                    {
                                        h.Vision.StationaryWatch.HasWatchDir = true;
                                        h.Vision.StationaryWatch.WatchDir = toSound.normalized;
                                    }
                                    else
                                    {
                                        h.Vision.StationaryWatch.HasWatchDir = false;
                                    }
                                }
                                else
                                {
                                    h.Vision.StationaryWatch.HasWatchDir = false;
                                }
                            }

                            if (!hasReflexDir && h.Vision.StationaryWatch.HasWatchDir && h.Vision.StationaryWatch.WatchDir.sqrMagnitude > 0.01f)
                            {
                                reflexDir = h.Vision.StationaryWatch.WatchDir;
                                hasReflexDir = true;
                            }

                            if (hasReflexDir)
                            {
                                float reflexTurnDt = now < h.Vision.UnderFireUntil ? (__0 * 1.55f) : (__0 * 1.25f);
                                AiCore.RotateBodyWithoutRaisingGun(soldier, reflexDir, reflexTurnDt);
                                __instance.moveLookingTarget = true;
                                if (now >= soldier.alertEndTime - 1.0f)
                                    soldier.SetAlertedFor(4.0f);
                            }
                        }
                    }

                    if (__state.DestKind == AiCore.CachedDestKind.VehicleRepair ||
                        __state.DestKind == AiCore.CachedDestKind.MedicHeal ||
                        isMovingNow ||
                        __instance.HasEnemyAndCanFireAtHim ||
                        AiCore.HasHeldMortarCached(h, __instance, now) ||
                        AiCore.IsSoldierInVehicleOrTransition(soldier) ||
                        soldier.isInBodyAnimation ||
                        soldier.isInHandsAnimation ||
                        soldier.HasSurrended() ||
                        soldier.IsCarried() ||
                        soldier.IsCarryingBody())
                    {
                        h.Vision.IsTurningToWatchSector = false;
                        return;
                    }

                    if (h.Vision.WasMovingLastWatchTick)
                    {
                        h.Vision.WasMovingLastWatchTick = false;
                        h.Vision.ThreatSector.ExpireTime = 0f;
                        h.Vision.StationaryWatch.NextEvalTime = 0f;
                        h.Vision.WatchWindow.ExpireTime = 0f;
                    }

                    // Last-Known Position Suppressive Fire for stationary gunners / automatic weapons
                    if (VisionConfig.EnableLastKnownSuppression.Value &&
                        AiCore.GetRawVisibleTargetPtr(__instance) == IntPtr.Zero &&
                        TryMaintainSuppressiveFireOnLastKnown(__instance, soldier, __0))
                    {
                        h.Vision.IsTurningToWatchSector = false;
                        return;
                    }

                    if (!VisionConfig.EnableThreatSectorConcentration.Value ||
                        soldier.UsingBipod() ||
                        soldier.IsBipodTransitioning())
                    {
                        h.Vision.IsTurningToWatchSector = false;
                        return;
                    }

                    Vector3 watchDir = Vector3.zero;
                    bool hasWatchDir = false;

                    StationaryWatchState wState = h.Vision.StationaryWatch;
                    if (now < wState.NextEvalTime)
                    {
                        hasWatchDir = wState.HasWatchDir;
                        watchDir = wState.WatchDir;
                    }
                    else
                    {
                        bool hasActiveWatchContact = false;
                        Vector3 obsPos = h.Tr != null ? h.Tr.position : Vector3.zero;
                        string obsFac = AiCore.GetCachedFaction(h, now);
                        if (now < h.Vision.UnderFireUntil)
                        {
                            Vector3 ufPos = AiCore.TryGetSpottableCenter(h.Vision.UnderFireAttacker, out Vector3 liveUfPos)
                                ? liveUfPos
                                : h.Vision.UnderFireFromPos;
                            Vector3 toUf = ufPos - obsPos;
                            toUf.y = 0f;
                            if (toUf.sqrMagnitude > 0.04f)
                            {
                                Vector3 rawDir = toUf.normalized;
                                float eyeH = soldier.m_pose == SoldierPose.Prone ? 0.34f : (soldier.m_pose == SoldierPose.Crouch ? 0.80f : 1.28f);
                                if (obsPos != Vector3.zero && AiCore.IsDirectionBlockedByEnvironment(obsPos + Vector3.up * eyeH, rawDir, 3.8f, h.RootTr))
                                {
                                    Vector3 clearDir = PickClearWatchDirection(soldier, obsPos, rawDir, 14f);
                                    watchDir = clearDir.sqrMagnitude > 0.01f ? clearDir : rawDir;
                                }
                                else
                                {
                                    watchDir = rawDir;
                                }
                                hasWatchDir = true;
                                hasActiveWatchContact = true;
                                if (now >= soldier.alertEndTime - 1.0f)
                                    soldier.SetAlertedFor(5.0f);
                            }
                        }

                        if (!hasWatchDir)
                        {
                            Vector3 acousticPos = Vector3.zero;
                            bool heardCloseAcoustic = !string.IsNullOrEmpty(obsFac) &&
                                                      VisionPatches.TryGetNearestCloseAcousticThreat(obsPos, obsFac, now, out acousticPos, out _, out _);
                            if (heardCloseAcoustic)
                            {
                                Vector3 toSound = acousticPos - obsPos;
                                toSound.y = 0f;
                                if (toSound.sqrMagnitude > 0.04f)
                                {
                                    Vector3 rawDir = toSound.normalized;
                                    float eyeH = soldier.m_pose == SoldierPose.Prone ? 0.34f : (soldier.m_pose == SoldierPose.Crouch ? 0.80f : 1.28f);
                                    if (obsPos != Vector3.zero && AiCore.IsDirectionBlockedByEnvironment(obsPos + Vector3.up * eyeH, rawDir, 3.8f, h.RootTr))
                                    {
                                        Vector3 clearDir = PickClearWatchDirection(soldier, obsPos, rawDir, 14f);
                                        watchDir = clearDir.sqrMagnitude > 0.01f ? clearDir : rawDir;
                                    }
                                    else
                                    {
                                        watchDir = rawDir;
                                    }
                                    hasWatchDir = true;
                                    hasActiveWatchContact = true;
                                    if (now >= soldier.alertEndTime - 1.0f)
                                        soldier.SetAlertedFor(4.0f);
                                }
                            }
                        }

                        if (!hasWatchDir)
                        {
                            hasWatchDir = TryGetKnownThreatSectorDirection(soldier, out watchDir, out hasActiveWatchContact) && watchDir.sqrMagnitude > 0.01f;
                        }

                        if (hasWatchDir &&
                            __state.DestKind == AiCore.CachedDestKind.CombatCover &&
                            h.CachedCover != null &&
                            soldier.IsOnCover())
                        {
                            CombatCover activeCover = h.CachedCover;
                            if (activeCover.localLookAngleWideness < 145f)
                            {
                                Vector3 coverFwd = activeCover.GetLookRotation() * Vector3.forward;
                                coverFwd.y = 0f;
                                if (coverFwd.sqrMagnitude > 0.01f)
                                {
                                    coverFwd.Normalize();
                                    float flankAngle = Vector3.Angle(coverFwd, watchDir);
                                    if (hasActiveWatchContact && flankAngle > 55.0f)
                                    {
                                        // Active threat/gunfire/explosion on the flank (> 55 deg): allow the defender to turn
                                        // his body toward the flanking threat instead of clamping gaze to old coverFwd!
                                        if (soldier.m_pose == SoldierPose.Idle && AiCore.CanBotChangePoseAutonomously(soldier))
                                        {
                                            SetBotPose(__instance, soldier, SoldierPose.Crouch);
                                        }
                                    }
                                    else
                                    {
                                        IntPtr covPtr = activeCover.Pointer;
                                        SoldierPose pose = soldier.m_pose;
                                        bool coverFwdBlockedAtEye;

                                        CoverEyeBlockState eyeState = h.Vision.CoverEyeBlock;
                                        if (eyeState.CoverPtr == covPtr &&
                                            eyeState.Pose == pose &&
                                            now < eyeState.ExpireTime)
                                        {
                                            coverFwdBlockedAtEye = eyeState.Blocked;
                                        }
                                        else
                                        {
                                            float eyeH = pose == SoldierPose.Prone ? 0.34f : (pose == SoldierPose.Crouch ? 0.80f : 1.28f);
                                            coverFwdBlockedAtEye = h.Tr != null && AiCore.IsDirectionBlockedByEnvironment(
                                                h.Tr.position + Vector3.up * eyeH,
                                                coverFwd,
                                                4.2f,
                                                h.RootTr);
                                            h.Vision.CoverEyeBlock = new CoverEyeBlockState
                                            {
                                                CoverPtr = covPtr,
                                                Pose = pose,
                                                ExpireTime = now + 2.5f,
                                                Blocked = coverFwdBlockedAtEye
                                            };
                                        }

                                        if (!coverFwdBlockedAtEye)
                                        {
                                            float maxHalfArc = Mathf.Clamp(activeCover.localLookAngleWideness * 0.5f, 22f, 52f);
                                            if (flankAngle > maxHalfArc)
                                            {
                                                watchDir = Vector3.RotateTowards(coverFwd, watchDir, maxHalfArc * Mathf.Deg2Rad, 0f).normalized;
                                            }
                                        }
                                    }
                                }
                            }
                        }

                        float jitter = (AiCore.GetDeterministicHash(ptr, 89) & 15) * 0.005f;
                        h.Vision.StationaryWatch = new StationaryWatchState
                        {
                            NextEvalTime = now + 0.22f + jitter,
                            HasWatchDir = hasWatchDir,
                            WatchDir = watchDir
                        };
                    }

                    if (hasWatchDir)
                    {
                        float angle = AiCore.HorizontalAngleToDirection(soldier, watchDir);
                        float turnDt = now < h.Vision.UnderFireUntil ? (__0 * 1.45f) : __0;
                        if (h.Vision.IsTurningToWatchSector)
                        {
                            if (angle <= 4.0f)
                            {
                                h.Vision.IsTurningToWatchSector = false;
                            }
                            else
                            {
                                AiCore.RotateBodyWithoutRaisingGun(soldier, watchDir, turnDt);
                            }
                        }
                        else if (angle > 14.0f)
                        {
                            h.Vision.IsTurningToWatchSector = true;
                            ThreatSectorOrientationsCount++;
                            AiCore.RotateBodyWithoutRaisingGun(soldier, watchDir, turnDt);
                        }
                    }
                }
                catch { }
                finally
                {
                    _inMovementRoutine = false;
                    HitchProfiler.End(HitchBucket.MovementRoutine, __state.ProfStart);
                }
            }
        }
    }
}
