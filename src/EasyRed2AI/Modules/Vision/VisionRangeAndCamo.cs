using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace EasyRed2HumanAI
{
    public static partial class VisionPatches
    {
        private static float _nextCleanupTime;

        // Thread-local optic zoom multiplier passed from Observer (Creature.CanSee / TurretAI.CanSee)
        // into Target (Soldier.CamoDistance / Soldier.CamoDistanceAwareEnemies)
        [ThreadStatic]
        private static float _currentObserverOpticMultiplier;

        // Thread-local flag set only when an alive Tank Commander / close infantry escort checks optical LOS
        // to hand off an off-bore threat to an enclosed turret gunner over crew intercom.
        [ThreadStatic]
        internal static bool AllowCommanderIntercomFovBypass;

        // Thread-local flag set during AIVehicle.SwitchSeatAi when a lone driver evaluates targets for an
        // uncrewed/frozen main turret so he uses hull-forward driver awareness instead of a narrow bore slit.
        [ThreadStatic]
        internal static bool AllowLoneDriverSeatSwitchFovBypass;

        // Live telemetry counters for HUD / debug feedback
        public static int TotalSightChecks;
        public static int BlockedByFOV;
        public static int BlockedByRange;
        public static int BoostedByScope;
        public static int BlockedPlaneSpot;

        public static float GetObserverOpticMultiplier()
        {
            return _currentObserverOpticMultiplier > 0.01f ? _currentObserverOpticMultiplier : 1.0f;
        }

        private struct RecentGunfireEntry
        {
            public IntPtr ShooterPtr;
            public Vector3 Pos;
            public string Faction;
            public float FiredTime;
            public float MaxAudibleRadius;
            public bool IsExplosionImpact;
        }

        private struct FootstepEntry
        {
            public IntPtr SoldierPtr;
            public Vector3 Pos;
            public string Faction;
            public float StepTime;
            public float AudibleRadius;
        }

        private const int GunfireRingSize = 64;
        private const int GunfireRingMask = GunfireRingSize - 1;
        private static readonly RecentGunfireEntry[] _gunfireRing = new RecentGunfireEntry[GunfireRingSize];
        private static int _gunfireRingHead;
        private static int _explosionSeqCounter = 1;

        private const int FootstepRingSize = 32;
        private const int FootstepRingMask = FootstepRingSize - 1;
        private static readonly FootstepEntry[] _footstepRing = new FootstepEntry[FootstepRingSize];
        private static int _footstepRingHead;

        internal static float ComputeFootstepAudibleRadius(SoldierPose pose, bool isSprinting)
        {
            if (pose == SoldierPose.Prone)
                return 0f;
            if (pose == SoldierPose.Crouch)
                return isSprinting ? 7.0f : 4.5f;
            return isSprinting ? 18.0f : 11.5f;
        }

        private static void RecordSoldierFootstep(Soldier s)
        {
            if (s == null || s.Pointer == IntPtr.Zero || s.transform == null)
                return;

            // Prone = silent; quiet crouch-walk = 4.5m; crouch-sprint = 7.0m; standing = 11.5m/18.0m.
            if (s.m_pose == SoldierPose.Prone)
                return;

            float now = Time.time;
            IntPtr ptr = s.Pointer;
            Vector3 pos = s.transform.position;
            string fac = AiCore.GetCachedFaction(s);
            if (string.IsNullOrEmpty(fac))
                return;

            float radius = ComputeFootstepAudibleRadius(s.m_pose, s.isSprinting);
            for (int i = 0; i < FootstepRingSize; i++)
            {
                if (_footstepRing[i].SoldierPtr == ptr)
                {
                    _footstepRing[i].Pos = pos;
                    _footstepRing[i].Faction = fac;
                    _footstepRing[i].StepTime = now;
                    _footstepRing[i].AudibleRadius = radius;
                    return;
                }
            }

            int slot = _footstepRingHead & FootstepRingMask;
            _footstepRingHead = (slot + 1) & FootstepRingMask;
            _footstepRing[slot] = new FootstepEntry
            {
                SoldierPtr = ptr,
                Pos = pos,
                Faction = fac,
                StepTime = now,
                AudibleRadius = radius
            };
        }

        internal static bool HasAnyLoudEventNear(Vector3 observerPos, float maxRadius, float maxAgeSeconds, float now)
        {
            float maxDistSq = maxRadius * maxRadius;
            for (int i = 0; i < GunfireRingSize; i++)
            {
                ref RecentGunfireEntry e = ref _gunfireRing[i];
                if (e.ShooterPtr == IntPtr.Zero)
                    continue;

                float age = now - e.FiredTime;
                if (age < 0f || age > maxAgeSeconds)
                    continue;

                float dx = e.Pos.x - observerPos.x;
                float dy = e.Pos.y - observerPos.y;
                float dz = e.Pos.z - observerPos.z;
                if (dx * dx + dy * dy + dz * dz <= maxDistSq)
                    return true;
            }
            return false;
        }

        public static bool TryGetNearestCloseAcousticThreat(
            Vector3 observerPos,
            string observerFaction,
            float now,
            out Vector3 soundPos,
            out float soundDist,
            out bool isGunfire)
        {
            soundPos = Vector3.zero;
            soundDist = -1f;
            isGunfire = false;
            if (string.IsNullOrEmpty(observerFaction))
                return false;

            float gunRainMult = ComputeGunfireRainMaskMultiplier(AiCore.RainIntensity01);
            float maxGunR = 20.0f * gunRainMult;
            float maxGunRSq = maxGunR * maxGunR;
            float bestDistSq = 20.0f * 20.0f + 1.0f;
            bool found = false;

            for (int i = 0; i < GunfireRingSize; i++)
            {
                ref RecentGunfireEntry e = ref _gunfireRing[i];
                if (e.ShooterPtr == IntPtr.Zero || e.IsExplosionImpact)
                    continue;

                float age = now - e.FiredTime;
                if (age < 0f || age > 2.2f)
                    continue;

                float dy = e.Pos.y - observerPos.y;
                if (Mathf.Abs(dy) > 6.0f)
                    continue;

                float dx = e.Pos.x - observerPos.x;
                float dz = e.Pos.z - observerPos.z;
                float distSq = dx * dx + dy * dy + dz * dz;
                if (distSq > maxGunRSq || distSq >= bestDistSq)
                    continue;

                if (!AiCore.AreEnemyFactions(observerFaction, e.Faction))
                    continue;

                bestDistSq = distSq;
                soundPos = e.Pos;
                isGunfire = true;
                found = true;
            }

            float rain01 = AiCore.RainIntensity01;
            float rainStepMult = rain01 > 0.05f ? Mathf.Lerp(1.0f, 0.55f, rain01) : 1.0f;
            float combatStepMult = HasAnyLoudEventNear(observerPos, 35.0f, 3.0f, now) ? 0.62f : 1.0f;
            float stepMaskMult = Mathf.Max(0.35f, rainStepMult * combatStepMult);
            for (int i = 0; i < FootstepRingSize; i++)
            {
                ref FootstepEntry fe = ref _footstepRing[i];
                if (fe.SoldierPtr == IntPtr.Zero)
                    continue;

                float age = now - fe.StepTime;
                if (age < 0f || age > 1.8f)
                    continue;

                float dy = fe.Pos.y - observerPos.y;
                if (Mathf.Abs(dy) > 4.5f)
                    continue;

                float maxR = Mathf.Min(20.0f, fe.AudibleRadius * stepMaskMult);
                float dx = fe.Pos.x - observerPos.x;
                float dz = fe.Pos.z - observerPos.z;
                float distSq = dx * dx + dy * dy + dz * dz;
                if (distSq > maxR * maxR || distSq >= bestDistSq)
                    continue;

                if (!AiCore.AreEnemyFactions(observerFaction, fe.Faction))
                    continue;

                bestDistSq = distSq;
                soundPos = fe.Pos;
                isGunfire = false;
                found = true;
            }

            if (!found)
                return false;

            soundDist = Mathf.Sqrt(bestDistSq);
            return true;
        }

        public static void RecordSoldierFired(Soldier s)
        {
            if (s == null || s.Pointer == IntPtr.Zero) return;
            float now = Time.time;
            IntPtr ptr = s.Pointer;

            AiCore.SoldierFastHandles h = AiCore.GetOrCreateSoldierHandles(s);
            if (h == null)
                return;

            h.Vision.LastFiredTime = now;
            if (h.Tr == null)
                return;

            Vector3 pos = h.Tr.position;
            string fac = AiCore.GetCachedFaction(h, now);
            RecordAcousticEventInternal(ptr, pos, fac, now, 200.0f, false);
        }

        public static void RecordTurretFired(TurretGun gun, Vehicle rootVeh, Soldier gunner)
        {
            if (ReferenceEquals(gun, null) || gun.Pointer == IntPtr.Zero)
                return;

            float now = Time.time;
            string fac = null;
            if (!ReferenceEquals(gunner, null) && gunner != null && gunner.Pointer != IntPtr.Zero)
            {
                AiCore.SoldierFastHandles gh = AiCore.GetOrCreateSoldierHandles(gunner);
                if (gh != null)
                {
                    gh.Vision.LastFiredTime = now;
                    fac = AiCore.GetCachedFaction(gh, now);
                }
            }

            if (!ReferenceEquals(rootVeh, null) && rootVeh != null && rootVeh.Pointer != IntPtr.Zero)
            {
                AiCore.VehicleFastHandles rvh = AiCore.GetOrCreateVehicleHandles(rootVeh);
                if (rvh != null)
                    rvh.LastTurretFiredTime = now;
                if (string.IsNullOrEmpty(fac))
                    fac = AiCore.InternFaction(rootVeh.GetVehicleFaction());
            }

            Transform tr = gun.transform != null
                ? gun.transform
                : (!ReferenceEquals(rootVeh, null) && rootVeh != null ? rootVeh.transform : null);
            if (tr == null)
                return;

            float playerAudDist = 250.0f;
            try
            {
                AudioSource src = gun.audioSource;
                if (src != null && src.Pointer != IntPtr.Zero && src.maxDistance >= 100.0f)
                    playerAudDist = src.maxDistance;
            }
            catch { }
            float audibleRadius = playerAudDist >= 400.0f ? 340.0f : (playerAudDist >= 300.0f ? 260.0f : 200.0f);
            RecordAcousticEventInternal(gun.Pointer, tr.position, fac, now, audibleRadius, false);
        }

        public static void RecordExplosionSound(Vector3 pos, float blastRadius, Soldier attacker, string fallbackFaction = null)
        {
            if (blastRadius < 0.35f)
                return;

            float now = Time.time;
            string fac = (!ReferenceEquals(attacker, null) && attacker != null && attacker.Pointer != IntPtr.Zero)
                ? AiCore.GetCachedFaction(attacker)
                : (!string.IsNullOrEmpty(fallbackFaction) ? AiCore.InternFaction(fallbackFaction) : null);
            float audibleRadius = blastRadius >= 6.0f ? 290.0f : 235.0f;

            // Coalesce multi-rocket/mortar salvos landing within 12m & 0.85s so a barrage doesn't evict live shooters
            for (int i = 0; i < GunfireRingSize; i++)
            {
                ref RecentGunfireEntry existing = ref _gunfireRing[i];
                if (!existing.IsExplosionImpact || existing.ShooterPtr == IntPtr.Zero)
                    continue;

                if (now - existing.FiredTime <= 0.85f &&
                    (existing.Pos - pos).sqrMagnitude <= 12.0f * 12.0f)
                {
                    existing.Pos = pos;
                    if (!string.IsNullOrEmpty(fac))
                        existing.Faction = fac;
                    existing.FiredTime = now;
                    if (audibleRadius > existing.MaxAudibleRadius)
                        existing.MaxAudibleRadius = audibleRadius;
                    return;
                }
            }

            int seq = _explosionSeqCounter++;
            if (_explosionSeqCounter > 1000000)
                _explosionSeqCounter = 1;
            RecordAcousticEventInternal(new IntPtr(-1000 - seq), pos, fac, now, audibleRadius, true);
        }

        private static void RecordAcousticEventInternal(
            IntPtr sourceKey,
            Vector3 pos,
            string fac,
            float now,
            float maxAudibleRadius,
            bool isExplosionImpact)
        {
            if (!isExplosionImpact)
            {
                // Coalesce rapid-fire bursts from the same shooter/turret so one MG42 doesn't evict 63 other shooters
                for (int i = 0; i < GunfireRingSize; i++)
                {
                    if (!_gunfireRing[i].IsExplosionImpact && _gunfireRing[i].ShooterPtr == sourceKey)
                    {
                        _gunfireRing[i].Pos = pos;
                        _gunfireRing[i].Faction = fac;
                        _gunfireRing[i].FiredTime = now;
                        _gunfireRing[i].MaxAudibleRadius = maxAudibleRadius;
                        return;
                    }
                }
            }

            int slot = _gunfireRingHead & GunfireRingMask;
            _gunfireRingHead = (slot + 1) & GunfireRingMask;
            _gunfireRing[slot] = new RecentGunfireEntry
            {
                ShooterPtr = sourceKey,
                Pos = pos,
                Faction = fac,
                FiredTime = now,
                MaxAudibleRadius = maxAudibleRadius,
                IsExplosionImpact = isExplosionImpact
            };
        }

        public static float ComputeGunfireRainMaskMultiplier(float rain01)
        {
            return rain01 > 0.05f ? Mathf.Lerp(1.0f, 0.78f, Mathf.Clamp01(rain01)) : 1.0f;
        }

        public static float ComputeVehicleEngineAudibleRadius(
            float vehicleMaxSoundDist,
            bool isTank,
            bool isMoving,
            float rain01,
            bool loudCombatNear)
        {
            float playerCap = vehicleMaxSoundDist > 10.0f
                ? Mathf.Clamp(vehicleMaxSoundDist, 25.0f, 300.0f)
                : (isTank ? 150.0f : 90.0f);

            float rawRadius;
            if (isMoving)
            {
                float frac = isTank ? 0.74f : 0.65f;
                rawRadius = Mathf.Min(playerCap * frac, isTank ? 155.0f : 95.0f);
            }
            else
            {
                float frac = isTank ? 0.36f : 0.28f;
                rawRadius = Mathf.Min(playerCap * frac, isTank ? 55.0f : 32.0f);
            }

            // Strictly guarantee bots never hear an engine farther than a human player can
            rawRadius = Mathf.Min(rawRadius, playerCap * 0.85f);

            float rainMult = rain01 > 0.05f ? Mathf.Lerp(1.0f, 0.65f, Mathf.Clamp01(rain01)) : 1.0f;
            float combatMult = loudCombatNear ? 0.62f : 1.0f;
            float maskMult = Mathf.Max(0.38f, rainMult * combatMult);
            return rawRadius * maskMult;
        }

        /// <summary>
        /// Pure L1-cache scan over the 64 most recent weapon shooters (zero IL2CPP calls, zero heap allocations).
        /// Strictly excludes explosion impact points so nearby shell craters never masquerade as close enemy riflemen
        /// during melee or field-bandage safety checks.
        /// </summary>
        public static bool TryGetNearestEnemyGunfire(
            Vector3 observerPos,
            string observerFaction,
            float maxRadius,
            float maxAgeSeconds,
            out float nearestDist)
        {
            if (TryGetNearestEnemyGunfirePosition(observerPos, observerFaction, maxRadius, maxAgeSeconds, out Vector3 gunfirePos))
            {
                nearestDist = Mathf.Sqrt((gunfirePos - observerPos).sqrMagnitude);
                return true;
            }

            nearestDist = -1f;
            return false;
        }

        /// <summary>
        /// Zero-allocation scan over the live acoustic ring buffer returning the 3D muzzle origin
        /// of the nearest recent unexpired non-explosion enemy weapon report.
        /// </summary>
        public static bool TryGetNearestEnemyGunfirePosition(
            Vector3 observerPos,
            string observerFaction,
            float maxRadius,
            float maxAgeSeconds,
            out Vector3 gunfirePos)
        {
            gunfirePos = Vector3.zero;
            if (string.IsNullOrEmpty(observerFaction) || maxRadius <= 0f)
                return false;

            float now = Time.time;
            float rainMult = ComputeGunfireRainMaskMultiplier(AiCore.RainIntensity01);
            float maxDistSq = maxRadius * maxRadius;
            float bestDistSq = maxDistSq + 1f;
            bool found = false;

            for (int i = 0; i < GunfireRingSize; i++)
            {
                ref RecentGunfireEntry e = ref _gunfireRing[i];
                if (e.ShooterPtr == IntPtr.Zero || e.IsExplosionImpact)
                    continue;

                float age = now - e.FiredTime;
                if (age < 0f || age > maxAgeSeconds)
                    continue;

                float entryCap = (e.MaxAudibleRadius > 0f ? e.MaxAudibleRadius : 200.0f) * rainMult;
                float allowedSq = Mathf.Min(maxDistSq, entryCap * entryCap);

                float dx = e.Pos.x - observerPos.x;
                float dy = e.Pos.y - observerPos.y;
                float dz = e.Pos.z - observerPos.z;
                float distSq = dx * dx + dy * dy + dz * dz;
                if (distSq >= bestDistSq || distSq > allowedSq)
                    continue;

                if (!AiCore.AreEnemyFactions(observerFaction, e.Faction))
                    continue;

                bestDistSq = distSq;
                gunfirePos = e.Pos;
                found = true;
            }

            return found;
        }

        /// <summary>
        /// Zero-allocation scan over the 64-entry live acoustic ring buffer to compute the dominant
        /// incoming enemy weapon/shellfire direction weighted by proximity and recency.
        /// Ensures infantry take cover on the leeward side of actual incoming weapons fire even when
        /// their own <c>visibleTarget</c> is null or occluded behind the wall/parapet!
        /// </summary>
        public static bool TryGetDominantEnemyGunfireDirection(
            Vector3 observerPos,
            string observerFaction,
            float maxRadius,
            float maxAgeSeconds,
            out Vector3 fireDir,
            out float nearestDist)
        {
            return TryGetDominantEnemyGunfireDirection(
                observerPos,
                observerFaction,
                maxRadius,
                maxAgeSeconds,
                out fireDir,
                out nearestDist,
                out _);
        }

        /// <summary>
        /// Zero-allocation scan over the 64-entry live acoustic ring buffer to compute the dominant
        /// incoming enemy weapon/shellfire direction weighted by proximity and recency, plus circular
        /// resultant coherence to detect multi-directional crossfire (&gt; 70 deg spread).
        /// When in crossfire, returns the single highest-threat shooter direction rather than an
        /// averaged bisector pointing between shooters!
        /// </summary>
        public static bool TryGetDominantEnemyGunfireDirection(
            Vector3 observerPos,
            string observerFaction,
            float maxRadius,
            float maxAgeSeconds,
            out Vector3 fireDir,
            out float nearestDist,
            out bool isCrossfire)
        {
            fireDir = Vector3.zero;
            nearestDist = -1f;
            isCrossfire = false;
            if (string.IsNullOrEmpty(observerFaction) || maxRadius <= 0f || maxAgeSeconds <= 0f)
                return false;

            float now = Time.time;
            float rainMult = ComputeGunfireRainMaskMultiplier(AiCore.RainIntensity01);
            float maxDistSq = maxRadius * maxRadius;
            float bestDistSq = maxDistSq + 1f;
            Vector3 weightedSum = Vector3.zero;
            float totalWeight = 0f;
            float bestSingleWeight = -1f;
            Vector3 bestSingleDir = Vector3.zero;
            int shooterCount = 0;

            for (int i = 0; i < GunfireRingSize; i++)
            {
                ref RecentGunfireEntry e = ref _gunfireRing[i];
                // Strictly exclude explosion impacts: a shell crater landing behind a wall/trench is an IMPACT point,
                // never the muzzle origin of incoming fire (which would invert leeward wall cover 180 deg into the enemy!).
                if (e.ShooterPtr == IntPtr.Zero || e.IsExplosionImpact)
                    continue;

                float age = now - e.FiredTime;
                if (age < 0f || age > maxAgeSeconds)
                    continue;

                float entryCap = (e.MaxAudibleRadius > 0f ? e.MaxAudibleRadius : 200.0f) * rainMult;
                float allowedSq = Mathf.Min(maxDistSq, entryCap * entryCap);

                float dx = e.Pos.x - observerPos.x;
                float dy = e.Pos.y - observerPos.y;
                float dz = e.Pos.z - observerPos.z;
                float distSq = dx * dx + dy * dy + dz * dz;
                if (distSq < 0.25f || distSq > allowedSq)
                    continue;

                if (!AiCore.AreEnemyFactions(observerFaction, e.Faction))
                    continue;

                if (distSq < bestDistSq)
                    bestDistSq = distSq;

                float horizLen = Mathf.Sqrt(dx * dx + dz * dz);
                if (horizLen < 0.25f)
                    continue;

                float invHoriz = 1.0f / horizLen;
                float ux = dx * invHoriz;
                float uz = dz * invHoriz;

                float freshness = 1.25f - 0.75f * (age / maxAgeSeconds);
                float weight = freshness / (12.0f + distSq);
                weightedSum.x += ux * weight;
                weightedSum.z += uz * weight;
                totalWeight += weight;
                shooterCount++;

                if (weight > bestSingleWeight)
                {
                    bestSingleWeight = weight;
                    bestSingleDir = new Vector3(ux, 0f, uz);
                }
            }

            if (totalWeight > 1e-10f && bestDistSq <= maxDistSq)
            {
                float resultantMag = Mathf.Sqrt(weightedSum.sqrMagnitude);
                float coherence = resultantMag / totalWeight;
                isCrossfire = shooterCount >= 2 && coherence < 0.82f;
                fireDir = (isCrossfire || resultantMag <= 1e-6f) ? bestSingleDir : weightedSum.normalized;
                nearestDist = Mathf.Sqrt(bestDistSq);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Zero-allocation L1-cache scan over the 64-entry acoustic ring buffer for recent explosion impacts
        /// (artillery shells, tank HE, mortars, grenades, satchel charges) near <paramref name="observerPos"/>.
        /// Used so tanks, combat vehicles, and exposed emplacement gunners can evade active shelling/explosions.
        /// </summary>
        public static bool TryGetNearestRecentExplosion(
            Vector3 observerPos,
            float maxRadius,
            float maxAgeSeconds,
            out Vector3 blastPos,
            out float nearestDist)
        {
            blastPos = Vector3.zero;
            nearestDist = -1f;
            if (maxRadius <= 0f || maxAgeSeconds <= 0f)
                return false;

            float now = Time.time;
            float maxDistSq = maxRadius * maxRadius;
            float bestDistSq = maxDistSq + 1f;
            bool found = false;

            for (int i = 0; i < GunfireRingSize; i++)
            {
                ref RecentGunfireEntry e = ref _gunfireRing[i];
                if (!e.IsExplosionImpact || e.ShooterPtr == IntPtr.Zero)
                    continue;

                float age = now - e.FiredTime;
                if (age < 0f || age > maxAgeSeconds)
                    continue;

                float dx = e.Pos.x - observerPos.x;
                float dy = e.Pos.y - observerPos.y;
                float dz = e.Pos.z - observerPos.z;
                float distSq = dx * dx + dy * dy + dz * dz;
                if (distSq >= bestDistSq || distSq > maxDistSq)
                    continue;

                bestDistSq = distSq;
                blastPos = e.Pos;
                found = true;
            }

            if (found)
            {
                nearestDist = Mathf.Sqrt(bestDistSq);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Pure L1-cache acoustic watch-sector query for stationary/defending troops: hears enemy small-arms (~140m),
        /// tank cannons / static emplacements (~210m), and damaging explosions / artillery (~225-260m).
        /// </summary>
        public static bool TryGetAudibleBattleDirection(
            Vector3 observerPos,
            string observerFaction,
            float maxWeaponAgeSeconds,
            float maxExplosionAgeSeconds,
            out Vector3 toBattleHorizontal,
            out float effectiveDistSq)
        {
            toBattleHorizontal = Vector3.zero;
            effectiveDistSq = float.MaxValue;
            if (string.IsNullOrEmpty(observerFaction))
                return false;

            float now = Time.time;
            float rainMult = ComputeGunfireRainMaskMultiplier(AiCore.RainIntensity01);
            bool found = false;

            for (int i = 0; i < GunfireRingSize; i++)
            {
                ref RecentGunfireEntry e = ref _gunfireRing[i];
                if (e.ShooterPtr == IntPtr.Zero)
                    continue;

                float maxAge = e.IsExplosionImpact ? maxExplosionAgeSeconds : maxWeaponAgeSeconds;
                float age = now - e.FiredTime;
                if (age < 0f || age > maxAge)
                    continue;

                float dx = e.Pos.x - observerPos.x;
                float dz = e.Pos.z - observerPos.z;
                float horizSq = dx * dx + dz * dz;
                float cap = (e.MaxAudibleRadius > 0f ? e.MaxAudibleRadius : 200.0f) * rainMult;
                if (horizSq > cap * cap || horizSq < (e.IsExplosionImpact ? 36.0f * 36.0f : 1.0f))
                    continue;

                float weightMult;
                if (!e.IsExplosionImpact)
                {
                    if (!AiCore.AreEnemyFactions(observerFaction, e.Faction))
                        continue;
                    weightMult = cap >= 190.0f ? 0.85f : 1.05f; // Heavy cannons/MGs carry unmistakable directional authority
                }
                else
                {
                    // Distant explosions (>= 36m) orient sentries toward active shelling/battles without letting
                    // nearby overshot shell craters outrank live enemy shooters or spotted squads in front.
                    bool isEnemyOrUnattributed = string.IsNullOrEmpty(e.Faction) ||
                                                 AiCore.AreEnemyFactions(observerFaction, e.Faction);
                    weightMult = isEnemyOrUnattributed ? 1.75f : 2.15f;
                }

                float agePenalty = 1.0f + 0.45f * (age / Mathf.Max(0.5f, maxAge));
                float candEffSq = horizSq * weightMult * agePenalty;
                if (candEffSq < effectiveDistSq)
                {
                    effectiveDistSq = candEffSq;
                    toBattleHorizontal = new Vector3(dx, 0f, dz);
                    found = true;
                }
            }

            return found;
        }

        public static bool HasFiredRecently(Soldier s, float windowSeconds = 4.5f)
        {
            AiCore.SoldierFastHandles h = AiCore.GetOrCreateSoldierHandles(s);
            if (h == null)
                return false;

            float now = Time.time;
            if (s.pressTriggerRequest)
            {
                h.Vision.LastFiredTime = now;
                return true;
            }

            float firedAt = h.Vision.LastFiredTime;
            if (firedAt > 0f)
            {
                float elapsed = now - firedAt;
                return elapsed >= 0f && elapsed <= windowSeconds;
            }
            return false;
        }

        public static float GetFiringVisibilityBonus(Soldier s)
        {
            AiCore.SoldierFastHandles h = AiCore.GetOrCreateSoldierHandles(s);
            if (h == null) return 0f;
            float duration = VisionConfig.FiringRevealDuration.Value;
            if (duration <= 0f) return 0f;

            // Stalker Marksmen have a 35% shorter firing reveal window when shooting from crouch/prone cover
            if (s.m_pose >= SoldierPose.Crouch &&
                VeteranAndTacticsPatches.GetPersonality(s) == VeteranAndTacticsPatches.SoldierPersonality.Marksman_Stalker)
            {
                duration *= 0.65f;
            }

            float now = Time.time;
            // Also catch sustained full-auto fire if trigger is currently depressed
            if (s.pressTriggerRequest)
            {
                h.Vision.LastFiredTime = now;
                return VisionConfig.FiringSightRangeBonus.Value;
            }

            float firedAt = h.Vision.LastFiredTime;
            if (firedAt > 0f)
            {
                float elapsed = now - firedAt;
                if (elapsed >= 0f && elapsed < duration)
                {
                    float t = 1.0f - (elapsed / duration);
                    return VisionConfig.FiringSightRangeBonus.Value * t;
                }
            }
            return 0f;
        }

        public static void ClearVisionCaches()
        {
            Array.Clear(_gunfireRing, 0, GunfireRingSize);
            _gunfireRingHead = 0;
            Array.Clear(_footstepRing, 0, FootstepRingSize);
            _footstepRingHead = 0;
            _nextCleanupTime = 0f;
        }

        public static void CleanupExpiredFiringRecords()
        {
            if (Time.time <= _nextCleanupTime && _nextCleanupTime - Time.time <= 30f)
                return;

            if (AiCore.IsHeavyWorkFrame() && Time.time < _nextCleanupTime + 1.0f)
                return;

            _nextCleanupTime = Time.time + 15f;

            if (VisionConfig.DebugLogging.Value && TotalSightChecks > 0)
            {
                AiCore.LogDebug(
                    "SightStats",
                    $"SightChecks={TotalSightChecks}, BlockedFOV={BlockedByFOV}, BlockedRange={BlockedByRange}, ScopeBoosts={BoostedByScope}, BlockedPlane={BlockedPlaneSpot}",
                    15.0f);
            }
        }

        // =====================================================================
        // 1. Track Individual Weapon Firing (Muzzle Flash / Gunshot Reveal)
        // =====================================================================
        [HarmonyPatch(typeof(Soldier), nameof(Soldier.StartFire))]
        public static class Soldier_StartFire_Patch
        {
            public static bool Prefix(Soldier __instance, out bool __state)
            {
                __state = true;
                try
                {
                    if (!VisionConfig.EnableMod.Value || !VisionConfig.EnableWallShootingPrevention.Value || __instance == null)
                        return true;

                    AiCore.SoldierFastHandles h = AiCore.GetOrCreateSoldierHandles(__instance);
                    if (AiCore.IsAnyHumanPlayerSoldier(h) || AiCore.IsSoldierInVehicleOrTransition(__instance) || !AiCore.IsLocallyOwnedBot(h))
                        return true;

                    IntPtr visPtr = (h != null && h.Ai != null) ? AiCore.GetRawVisibleTargetPtr(h.Ai) : IntPtr.Zero;
                    bool targetIsVehicle = visPtr != IntPtr.Zero &&
                                           (AiCore.TryGetVehicleHandles(visPtr, out AiCore.VehicleFastHandles visVh)
                                               ? visVh.IsWheeledOrTank
                                               : (!AiCore.TryGetSoldierHandles(visPtr, out _) &&
                                                  h.Ai.visibleTarget != null &&
                                                  h.Ai.visibleTarget.IsWheeledVehicleOrTank()));
                    if (VeteranAndTacticsPatches.IsBotGunClippedThroughWall(__instance, targetIsVehicle, shooterH: h))
                    {
                        __instance.SetGunTriggerPressed(false);
                        VeteranAndTacticsPatches.IncrementWallsBlocked();
                        __state = false;
                        return false;
                    }
                }
                catch { }
                return true;
            }

            public static void Postfix(Soldier __instance, bool __state)
            {
                try
                {
                    if (!__state || !VisionConfig.EnableMod.Value || __instance == null) return;
                    RecordSoldierFired(__instance);
                    VeteranAndTacticsPatches.AddCombatExperienceBonus(__instance, 0.75f);
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(Soldier), nameof(Soldier.TryPlayStepSound))]
        public static class Soldier_TryPlayStepSound_Patch
        {
            public static void Postfix(Soldier __instance, bool __result)
            {
                try
                {
                    if (!__result ||
                        !VisionConfig.EnableMod.Value ||
                        __instance == null ||
                        !__instance.IsAlive ||
                        AiCore.IsSoldierInVehicleOrTransition(__instance) ||
                        __instance.HasSurrended())
                    {
                        return;
                    }

                    AiCore.SoldierFastHandles stepH = AiCore.GetOrCreateSoldierHandles(__instance);
                    if (stepH == null || stepH.Tr == null)
                        return;

                    RecordSoldierFootstep(__instance);
                }
                catch { }
            }
        }

        // =====================================================================
        // 2. Infantry Stance, Camouflage & Squad Awareness Sight Calculation
        // =====================================================================
        internal static float ComputeEffectiveSoldierSightRange(
            SoldierPose pose,
            bool aware,
            bool preserveStanceInCombat,
            float standingRange,
            float crouchRange,
            float proneRange,
            float combatAwareMult,
            float night01,
            float vanillaNightDenom,
            float sinceShot,
            float firingBonus,
            float camoMultiplier,
            float opticMultiplier)
        {
            float baseDist;
            if (!aware || preserveStanceInCombat)
            {
                switch (pose)
                {
                    case SoldierPose.Idle:
                        baseDist = standingRange;
                        break;
                    case SoldierPose.Crouch:
                        baseDist = crouchRange;
                        break;
                    case SoldierPose.Prone:
                        baseDist = proneRange;
                        break;
                    default:
                        baseDist = standingRange;
                        break;
                }

                if (aware)
                    baseDist *= combatAwareMult;
            }
            else
            {
                baseDist = 300.0f;
            }

            if (night01 > 0.05f)
            {
                float denom = Mathf.Max(0.25f, vanillaNightDenom);
                float silentNightDist = baseDist * Mathf.Lerp(1.0f, 0.30f, night01);
                if (sinceShot >= 0f && sinceShot <= 1.85f)
                {
                    // Active muzzle flare in darkness: pops starkly against night sky/ground (155m-215m in FOV cone),
                    // decaying rapidly over 1.85s so night shoot-and-scoot stealth works once firing stops!
                    float flare01 = 1.0f - (sinceShot / 1.85f);
                    float flarePeakDist = baseDist * Mathf.Lerp(1.0f, 0.72f, night01) + Mathf.Lerp(85f, 130f, night01);
                    float worldTargetDist = Mathf.Lerp(silentNightDist, flarePeakDist, flare01);
                    return (worldTargetDist * camoMultiplier * opticMultiplier) / denom;
                }
                return (silentNightDist * camoMultiplier * opticMultiplier) / denom;
            }

            return (baseDist + firingBonus) * camoMultiplier * opticMultiplier;
        }

        [HarmonyPatch(typeof(Soldier), nameof(Soldier.CamoDistanceAwareEnemies))]
        public static class Soldier_CamoDistanceAwareEnemies_Patch
        {
            public static bool Prefix(Soldier __instance, bool __0, ref float __result)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value || ReferenceEquals(__instance, null) || __instance.Pointer == IntPtr.Zero)
                        return true;

                    AiCore.SoldierFastHandles h = AiCore.GetOrCreateSoldierHandles(__instance);
                    if (h == null)
                        return true;

                    float now = Time.time;
                    float camoMultiplier = AiCore.GetCachedCamoMultiplier(h, now);
                    float night01 = (VisionConfig.EnableRealisticNightDarkness != null && VisionConfig.EnableRealisticNightDarkness.Value)
                        ? AiCore.NightDarkness01
                        : 0f;
                    float sinceShot = h.Vision.LastFiredTime > 0f ? (now - h.Vision.LastFiredTime) : 999f;
                    float firingBonus = night01 > 0.05f ? 0f : GetFiringVisibilityBonus(__instance);

                    __result = ComputeEffectiveSoldierSightRange(
                        __instance.m_pose,
                        __0,
                        VisionConfig.PreserveStanceStealthInCombat.Value,
                        VisionConfig.StandingSightRange.Value,
                        VisionConfig.CrouchingSightRange.Value,
                        VisionConfig.ProneSightRange.Value,
                        VisionConfig.CombatAwareStanceMultiplier.Value,
                        night01,
                        AiCore.VanillaNightFactor,
                        sinceShot,
                        firingBonus,
                        camoMultiplier,
                        GetObserverOpticMultiplier());
                    return false;
                }
                catch
                {
                    return true;
                }
            }
        }

        // =====================================================================
        // 3. Infantry Observer FOV Cones & Scope Magnification (Creature.CanSee)
        // =====================================================================
        internal static bool IsObserverBlockedByCloseThinWall(
            bool observerOnVehicle,
            Vector3 observerPos,
            Vector3 toTarget,
            float dist,
            Transform observerRoot,
            bool targetIsVehicle)
        {
            if (observerOnVehicle || dist <= 0.30f)
                return false;

            float checkDist = Mathf.Min(0.50f, dist - 0.15f);
            if (checkDist <= 0.05f)
                return false;

            return AiCore.IsDirectionBlockedByEnvironment(
                observerPos,
                toTarget / dist,
                checkDist,
                observerRoot,
                ignoreVehicles: targetIsVehicle);
        }

        [HarmonyPatch(typeof(Creature), nameof(Creature.CanSee))]
        public static class Creature_CanSee_Patch
        {
            public static bool Prefix(Creature __instance, Spottable __0, ref bool __result, out long __state)
            {
                long pStart = HitchProfiler.BeginModScope();
                __state = pStart;
                bool runVanillaLinecast = true;
                _currentObserverOpticMultiplier = 1.0f;
                try
                {
                    if (!VisionConfig.EnableMod.Value || ReferenceEquals(__instance, null) || ReferenceEquals(__0, null))
                        return true;

                    if (!AiCore.TryResolveSpottableFast(__0, out AiCore.SoldierFastHandles targetSolH, out AiCore.VehicleFastHandles targetVehH))
                    {
                        __result = false;
                        runVanillaLinecast = false;
                        return false;
                    }

                    float now = Time.time;
                    Soldier targetSoldier = targetSolH != null ? targetSolH.Ref : null;
                    if (targetSoldier != null && targetSoldier.IsOnVehicle())
                    {
                        AiCore.VehicleFastHandles tvH = AiCore.GetCachedCurrentVehicleHandles(targetSolH, now);
                        if (tvH != null && (tvH.IsAir || AiCore.IsButtonedUpArmoredVehicleCrew(targetSolH, tvH)))
                        {
                            // Never allow ground infantry to X-ray target a pilot inside an aircraft cockpit OR a buttoned-up
                            // crewman (!seat.showUnitSet) inside an armored hull (fixes vanilla Soldier.GetBestVisibleEnemy
                            // Crewman #2+ foot-soldier fallthrough causing riflemen to dump clips into tank hulls!).
                            __result = false;
                            runVanillaLinecast = false;
                            return false;
                        }
                    }

                    TotalSightChecks++;

                    if (!AiCore.TryGetSoldierHandlesFromCreature(__instance, out AiCore.SoldierFastHandles obsH) || obsH.Tr == null)
                        return true;

                    float night01 = (VisionConfig.EnableRealisticNightDarkness != null && VisionConfig.EnableRealisticNightDarkness.Value)
                        ? AiCore.NightDarkness01
                        : 0f;
                    bool targetMuzzleFlare = night01 > 0.05f &&
                                             ((targetSolH != null && targetSolH.Vision.LastFiredTime > 0f && (now - targetSolH.Vision.LastFiredTime) <= 1.85f) ||
                                              (targetVehH != null && targetVehH.LastTurretFiredTime > 0f && (now - targetVehH.LastTurretFiredTime) <= 2.20f));

                    Soldier observer = obsH.Ref;
                    Vector3 observerPos = observer.FacePosition();
                    Vector3 targetPos = __0.SpotPosition();
                    Vector3 toTarget = targetPos - observerPos;
                    float dist = toTarget.magnitude;

                    if (dist > 4.5f && !targetMuzzleFlare)
                    {
                        if (now >= obsH.NextInSmokeCheckTime)
                        {
                            obsH.NextInSmokeCheckTime = now + 0.25f;
                            int smokeMask = AiCore.GetSmokeOcclusionMask();
                            obsH.InSmokeCached = smokeMask != 0 &&
                                                 Physics.CheckSphere(observerPos, 0.45f, smokeMask, QueryTriggerInteraction.Collide);
                        }
                        if (obsH.InSmokeCached)
                        {
                            __result = false;
                            return false;
                        }
                    }

                    Vector3 bodyFwd = obsH.Tr.forward;
                    float angle;
                    if (observer.IsAiming)
                    {
                        Vector3 gunDir = observer.GetFireDir();
                        float gunAngle = Vector3.Angle(toTarget, gunDir);
                        // At close/near combat range (<= 34m), human peripheral vision includes torso/head orientation
                        // even when shouldering a rifle slightly off to one side.
                        angle = dist <= 34.0f
                            ? Mathf.Min(gunAngle, Vector3.Angle(toTarget, bodyFwd))
                            : gunAngle;
                    }
                    else
                    {
                        angle = Vector3.Angle(toTarget, bodyFwd);
                    }

                    bool targetFiredRecently = targetSolH != null
                        ? (targetSolH.Vision.LastFiredTime > 0f && (now - targetSolH.Vision.LastFiredTime) <= 4.0f)
                        : HasFiredRecently(targetSoldier, 4.0f);
                    bool isQuietFlanker = targetSoldier != null &&
                                          !targetSoldier.IsSpotted &&
                                          !targetFiredRecently;
                    bool isActiveUnderFireAttacker = now < obsH.Vision.UnderFireUntil &&
                                                     obsH.Vision.UnderFireAttackerPtr != IntPtr.Zero &&
                                                     __0.Pointer == obsH.Vision.UnderFireAttackerPtr;
                    bool overwatchEnabled = VisionConfig.EnableThreatSectorConcentration.Value;
                    bool overwatchEvaluated = false;
                    bool isStationaryOverwatch = false;
                    bool hasActiveThreatSector = false;

                    if (VisionConfig.EnforceHumanFOVWhenAlerted.Value)
                    {
                        float closeOmni = VisionConfig.CloseOmnidirectionalRange.Value;
                        if (night01 > 0.05f && !targetMuzzleFlare)
                        {
                            // Shrink 360-degree footstep/breathing sense radius in darkness (12m -> ~4.5m)
                            closeOmni = Mathf.Lerp(closeOmni, Mathf.Min(closeOmni, 4.5f), night01);
                        }

                        float rain01 = AiCore.RainIntensity01;
                        if (rain01 > 0.05f && !targetMuzzleFlare)
                        {
                            closeOmni = Mathf.Max(3.5f, closeOmni * Mathf.Lerp(1.0f, 0.58f, rain01));
                        }

                        float allowedHalfFOV = AiCore.EvaluateHumanSquintHalfFov(
                            dist,
                            closeOmni,
                            VisionConfig.MidRangeMaxDistance.Value,
                            VisionConfig.MidRangeHalfFOV.Value,
                            VisionConfig.LongRangeHalfFOV.Value);

                        if (AiCore.IsAsymmetricDefender(AiCore.GetCachedFaction(obsH, now)))
                        {
                            allowedHalfFOV = Mathf.Min(110.0f, allowedHalfFOV + 8.0f);
                        }

                        if (night01 > 0.05f)
                        {
                            if (targetMuzzleFlare)
                                allowedHalfFOV = Mathf.Min(102.0f, allowedHalfFOV + 8.0f * night01);
                            else if (dist > closeOmni)
                                allowedHalfFOV *= Mathf.Lerp(1.0f, 0.74f, night01);
                        }

                        // Only narrow peripheral awareness for stationary overwatch beyond 34m so close-quarters
                        // flankers (12m-34m) never hit an artificial 60-degree blind-spot wall!
                        if (!isActiveUnderFireAttacker && isQuietFlanker && dist > 34.0f && overwatchEnabled)
                        {
                            SoldierAI obsAi = obsH.Ai;
                            isStationaryOverwatch = !AiCore.IsAnyHumanPlayerSoldier(obsH) &&
                                                    !AiCore.IsSoldierInVehicleOrTransition(observer) &&
                                                    !ReferenceEquals(obsAi, null) &&
                                                    obsAi != null &&
                                                    !obsAi.IsMoving();
                            hasActiveThreatSector = isStationaryOverwatch &&
                                                    VeteranAndTacticsPatches.HasActiveThreatSectorContact(obsH, now);
                            overwatchEvaluated = true;
                            if (hasActiveThreatSector)
                            {
                                float focusCap = Mathf.Lerp(76.0f, 52.0f, Mathf.InverseLerp(34.0f, 95.0f, dist));
                                allowedHalfFOV = Mathf.Min(allowedHalfFOV, focusCap);
                            }
                        }

                        if (isActiveUnderFireAttacker)
                        {
                            // Human glance toward active incoming fire bearing while still obeying vanilla LOS raycasts.
                            allowedHalfFOV = Mathf.Max(allowedHalfFOV, dist <= 85.0f ? 135.0f : 95.0f);
                        }

                        if (angle > allowedHalfFOV)
                        {
                            BlockedByFOV++;
                            __result = false;
                            runVanillaLinecast = false;
                            return false;
                        }
                    }

                    float opticZoom = VisionConfig.EnableScopeVisionScaling.Value
                        ? AiCore.GetCachedOpticZoom(obsH, now)
                        : 1.0f;

                    float allowedOpticHalfFOV = VisionConfig.OpticLongRangeHalfFOV.Value;
                    VeteranAndTacticsPatches.SoldierPersonality observerPers = obsH.Veterancy.Record.Initialized
                        ? obsH.Veterancy.Record.Personality
                        : VeteranAndTacticsPatches.GetPersonality(observer);
                    if (observerPers == VeteranAndTacticsPatches.SoldierPersonality.Marksman_Spotter)
                    {
                        allowedOpticHalfFOV += 5.0f;
                    }

                    if (opticZoom > 1.0f && angle <= allowedOpticHalfFOV)
                    {
                        _currentObserverOpticMultiplier = opticZoom;
                        BoostedByScope++;
                    }

                    if (!overwatchEvaluated && overwatchEnabled)
                    {
                        SoldierAI obsAi = obsH.Ai;
                        isStationaryOverwatch = !AiCore.IsAnyHumanPlayerSoldier(obsH) &&
                                                !AiCore.IsSoldierInVehicleOrTransition(observer) &&
                                                !ReferenceEquals(obsAi, null) &&
                                                obsAi != null &&
                                                !obsAi.IsMoving();
                        hasActiveThreatSector = isStationaryOverwatch &&
                                                VeteranAndTacticsPatches.HasActiveThreatSectorContact(obsH, now);
                    }

                    if (isStationaryOverwatch)
                    {
                        if (angle <= 28f)
                        {
                            float focusMult = (observerPers == VeteranAndTacticsPatches.SoldierPersonality.Gunner_Overwatch ||
                                               observerPers == VeteranAndTacticsPatches.SoldierPersonality.Marksman_Spotter)
                                ? 1.28f
                                : 1.20f;
                            _currentObserverOpticMultiplier = Mathf.Max(_currentObserverOpticMultiplier, focusMult);
                        }
                        else if (hasActiveThreatSector && isQuietFlanker && angle > 38f)
                        {
                            _currentObserverOpticMultiplier *= 0.72f;
                        }
                    }

                    bool isWheeledOrTank = targetVehH != null ? targetVehH.IsWheeledOrTank : (targetSolH == null && __0.IsWheeledVehicleOrTank());
                    if (isWheeledOrTank)
                    {
                        float vehSightCap = VisionConfig.VehicleCamoDistance.Value * GetObserverOpticMultiplier();
                        if (night01 > 0.05f)
                        {
                            vehSightCap = targetMuzzleFlare
                                ? Mathf.Max(vehSightCap, 240.0f)
                                : vehSightCap * Mathf.Lerp(1.0f, 0.34f, night01);
                        }

                        if (dist > vehSightCap)
                        {
                            BlockedByRange++;
                            __result = false;
                            runVanillaLinecast = false;
                            return false;
                        }
                    }

                    // Vanilla Creature.CanSee starts its linecast at observerPos + dir * 0.40f,
                    // skipping thin walls (<= 0.35m) pressed right in front of the observer's eyes.
                    // Skip for vehicle/emplacement crews near shields/frames and ignore vehicle hulls when looking at vehicles.
                    Transform obsRoot = obsH.RootTr;
                    if (ReferenceEquals(obsRoot, null))
                    {
                        obsRoot = obsH.Tr.root;
                        obsH.RootTr = obsRoot;
                    }
                    if (IsObserverBlockedByCloseThinWall(observer.IsOnVehicle(), observerPos, toTarget, dist, obsRoot, isWheeledOrTank))
                    {
                        __result = false;
                        runVanillaLinecast = false;
                        return false;
                    }

                    return true;
                }
                catch (Exception ex)
                {
                    AiCore.ReportPatchException("CanSee", ex);
                    _currentObserverOpticMultiplier = 1.0f;
                    return true;
                }
                finally
                {
                    HitchProfiler.EndModScope(HitchBucket.CanSeeMod, pStart);
                    if (runVanillaLinecast && !ReferenceEquals(__instance, null) && !ReferenceEquals(__0, null))
                        HitchProfiler.RecordCanSeeLinecastPassThrough();
                }
            }

            public static void Postfix(bool __result, long __state)
            {
                if (!__result && VisionConfig.EnableMod.Value)
                {
                    BlockedByRange++;
                }
                _currentObserverOpticMultiplier = 1.0f;
                HitchProfiler.End(HitchBucket.CanSeeTotal, __state);
            }
        }

        // =====================================================================
        // 4. Vehicle & Tank Turret Vision (TurretAI.CanSee)
        // =====================================================================
        [HarmonyPatch(typeof(TurretAI), nameof(TurretAI.CanSee))]
        public static class TurretAI_CanSee_Patch
        {
            public static bool Prefix(TurretAI __instance, Spottable __0, ref bool __result, out long __state)
            {
                __state = HitchProfiler.Begin();
                _currentObserverOpticMultiplier = 1.0f;
                try
                {
                    if (!VisionConfig.EnableMod.Value || ReferenceEquals(__instance, null) || ReferenceEquals(__0, null))
                        return true;

                    Turret turret = AiCore.GetCachedTurretForAi(__instance);
                    if (turret == null || !AiCore.TryResolveSpottableFast(__0, out AiCore.SoldierFastHandles targetSolH, out AiCore.VehicleFastHandles targetVehH))
                    {
                        __result = false;
                        return false;
                    }

                    float now = Time.time;
                    Soldier targetSoldier = targetSolH != null ? targetSolH.Ref : null;
                    if (targetSoldier != null && targetSoldier.IsOnVehicle())
                    {
                        AiCore.VehicleFastHandles tvH = AiCore.GetCachedCurrentVehicleHandles(targetSolH, now);
                        if (tvH != null && (tvH.IsAir || AiCore.IsButtonedUpArmoredVehicleCrew(targetSolH, tvH)))
                        {
                            // Never allow turrets to target the pilot inside an airplane cockpit OR a buttoned-up crewman
                            // inside an armored vehicle hull; turrets must target the Vehicle itself.
                            __result = false;
                            return false;
                        }
                    }

                    TotalSightChecks++;

                    float night01 = (VisionConfig.EnableRealisticNightDarkness != null && VisionConfig.EnableRealisticNightDarkness.Value)
                        ? AiCore.NightDarkness01
                        : 0f;
                    bool targetMuzzleFlare = night01 > 0.05f &&
                                             ((targetSolH != null && targetSolH.Vision.LastFiredTime > 0f && (now - targetSolH.Vision.LastFiredTime) <= 1.85f) ||
                                              (targetVehH != null && targetVehH.LastTurretFiredTime > 0f && (now - targetVehH.LastTurretFiredTime) <= 2.20f));

                    Vector3 firePos = turret.GetFirePos();
                    Vector3 targetPos = __0.SpotPosition();
                    Vector3 toTarget = targetPos - firePos;
                    float dist = toTarget.magnitude;
                    Vector3 fireDir = turret.GetFireDir();
                    float angle = Vector3.Angle(toTarget, fireDir);

                    bool isWheeledOrTank = targetVehH != null ? targetVehH.IsWheeledOrTank : (targetSolH == null && __0.IsWheeledVehicleOrTank());
                    bool isAir = targetVehH != null ? targetVehH.IsAir : (targetSolH == null && __0.IsAirVehicle());
                    bool isInfantryTarget = !isWheeledOrTank && !isAir;

                    AiCore.TurretFastTraits traits = AiCore.GetCachedTurretTraits(turret);
                    float turretAimFov = traits.AimFov > 0.1f ? traits.AimFov : turret.GetAimingFOV();
                    Vehicle turretVeh = traits.RootVehicle;
                    AiCore.VehicleFastHandles obsVehH = traits.VehHandles;

                    if (!traits.IsCommanderTurret &&
                        isWheeledOrTank &&
                        targetVehH != null &&
                        targetVehH.Ref != null &&
                        !VeteranAndTacticsPatches.CanTurretEffectivelyDamageArmoredVehicle(turret, targetVehH.Ref, dist))
                    {
                        __result = false;
                        return false;
                    }
                    VeteranAndTacticsPatches.SoldierPersonality crewPers = turretVeh != null
                        ? VeteranAndTacticsPatches.GetVehicleCrewPersonality(turretVeh)
                        : VeteranAndTacticsPatches.SoldierPersonality.None;

                    IntPtr curTrackedPtr = AiCore.GetRawTurretVisibleTargetPtr(__instance);
                    bool isAlreadyTrackingThis = curTrackedPtr != IntPtr.Zero && curTrackedPtr == __0.Pointer;
                    bool isUnderFireAttacker = obsVehH != null &&
                                               now < obsVehH.UnderFireUntil &&
                                               obsVehH.UnderFireAttackerPtr != IntPtr.Zero &&
                                               obsVehH.UnderFireAttackerPtr == __0.Pointer;

                    if (VisionConfig.EnforceHumanFOVWhenAlerted.Value &&
                        !isAlreadyTrackingThis &&
                        !AllowCommanderIntercomFovBypass &&
                        !isUnderFireAttacker)
                    {
                        if (AllowLoneDriverSeatSwitchFovBypass && obsVehH != null && obsVehH.Tr != null)
                        {
                            float driverAngle = Vector3.Angle(toTarget, obsVehH.Tr.forward);
                            float driverHalfFov = dist <= 28.0f ? 115.0f : (dist <= 110.0f ? 72.0f : 50.0f);
                            if (driverAngle > driverHalfFov)
                            {
                                BlockedByFOV++;
                                __result = false;
                                return false;
                            }
                        }
                        else
                        {
                            float allowedTurretHalfFov = 180.0f;

                            if (traits.IsCommanderTurret)
                            {
                                // 1. Tank Commander Cupola / Periscope / Binoculars:
                                //    Directional periscope/vision-block cone aligned with TurretCommander.GetFireDir() as the
                                //    Commander actively pans 360 degrees (only unbuttoned cupolas get <= 7.5m ear awareness).
                                if (traits.IsExposedCrewSeat && dist <= 7.5f)
                                    allowedTurretHalfFov = 180.0f;
                                else if (dist <= 55.0f)
                                    allowedTurretHalfFov = Mathf.Lerp(78.0f, 62.0f, Mathf.Clamp01((dist - 7.5f) / 47.5f));
                                else
                                    allowedTurretHalfFov = Mathf.Lerp(62.0f, 42.0f, Mathf.Clamp01((dist - 55.0f) / 155.0f));
                            }
                            else if (obsVehH != null && obsVehH.IsAir)
                            {
                                // 2. Aircraft Defensive / Flexible Gunners: wide framed hemisphere, squinting at range.
                                allowedTurretHalfFov = dist <= 120.0f
                                    ? 86.0f
                                    : Mathf.Lerp(86.0f, 54.0f, Mathf.Clamp01((dist - 120.0f) / 280.0f));
                            }
                            else if (obsVehH != null && obsVehH.IsWheeledOrTank && !traits.IsExposedCrewSeat && !traits.IsAA)
                            {
                                // 3. Enclosed / Buttoned-Up Tank & Armored Vehicle Guns (Main Cannon, Coax, Hull Bow MG):
                                //    NO magical 360-degree bubble! Gunner stares through a narrow telescope/ball-mount slit;
                                //    flankers outside this bore cone require an alive Commander / escort intercom handoff!
                                float closeSlit = turretAimFov < 42.0f ? 28.0f : 34.0f;
                                float midSlit = turretAimFov < 42.0f ? 22.0f : 27.0f;
                                float farSlit = turretAimFov < 42.0f ? 15.0f : 19.0f;
                                if (!isInfantryTarget)
                                {
                                    // Large vehicle silhouettes & muzzle flashes are slightly easier to catch at the edge of a vision block
                                    closeSlit += 8.0f;
                                    midSlit += 6.0f;
                                    farSlit += 4.0f;
                                }

                                if (dist <= 10.0f)
                                    allowedTurretHalfFov = closeSlit + 6.0f;
                                else if (dist <= 45.0f)
                                    allowedTurretHalfFov = Mathf.Lerp(closeSlit, midSlit, (dist - 10.0f) / 35.0f);
                                else
                                    allowedTurretHalfFov = Mathf.Lerp(midSlit, farSlit, Mathf.Clamp01((dist - 45.0f) / 165.0f));
                            }
                            else if (traits.IsAA)
                            {
                                // 4A. Open 360-degree Flak / AA Mounts: point-blank ear awareness + wide sky/horizon scan.
                                if (dist <= 9.5f)
                                    allowedTurretHalfFov = 180.0f;
                                else if (dist <= 125.0f)
                                    allowedTurretHalfFov = 90.0f + (crewPers == VeteranAndTacticsPatches.SoldierPersonality.LightCrew_FlakWatch ? 12.0f : 0f);
                                else
                                    allowedTurretHalfFov = Mathf.Lerp(88.0f, 58.0f, Mathf.Clamp01((dist - 125.0f) / 220.0f));
                            }
                            else
                            {
                                // 4B. Static Emplacements (Shielded AT Guns, HMG Tripods/Pillboxes) & Open-Top Pintle MGs:
                                //     Exposed crew hear/sense point-blank threats (<= 8.5m), restricted laterally by gun shield / embrasure.
                                if (traits.IsExposedCrewSeat && dist <= 8.5f)
                                    allowedTurretHalfFov = 180.0f;
                                else if (dist <= 35.0f)
                                    allowedTurretHalfFov = Mathf.Lerp(traits.IsExposedCrewSeat ? 68.0f : 52.0f, 48.0f, Mathf.Clamp01((dist - 8.5f) / 26.5f));
                                else
                                    allowedTurretHalfFov = Mathf.Lerp(48.0f, 30.0f, Mathf.Clamp01((dist - 35.0f) / 150.0f));
                            }

                            // Tank Hull Dead-Zone & Low-Profile Stealth:
                            // Buttoned-up tanks/armored vehicles cannot see silent crouching/prone infantry hugging within 11.5m
                            // below their cupola/gun-depression line (outside the dead-ahead 20-deg bore), and silent prone
                            // infantry in craters/tall grass within 38m require a direct narrow sight sweep to spot!
                            if (isInfantryTarget &&
                                targetSoldier != null &&
                                obsVehH != null &&
                                obsVehH.IsWheeledOrTank &&
                                !traits.IsExposedCrewSeat &&
                                targetSoldier.m_pose >= SoldierPose.Crouch &&
                                !HasFiredRecently(targetSoldier, 3.2f))
                            {
                                if (dist <= 11.5f && angle > 20.0f)
                                {
                                    BlockedByFOV++;
                                    __result = false;
                                    return false;
                                }
                                if (targetSoldier.m_pose == SoldierPose.Prone && dist <= 38.0f)
                                {
                                    allowedTurretHalfFov *= 0.55f;
                                }
                            }

                            if (angle > allowedTurretHalfFov)
                            {
                                BlockedByFOV++;
                                __result = false;
                                return false;
                            }
                        }
                    }

                    if (VisionConfig.EnableScopeVisionScaling.Value)
                    {
                        if (traits.IsCommanderTurret && angle <= 26.0f)
                        {
                            _currentObserverOpticMultiplier = Mathf.Min(1.45f, VisionConfig.MaxOpticZoomMultiplier.Value);
                            BoostedByScope++;
                        }
                        else if (turretAimFov > 1f && turretAimFov < 45f)
                        {
                            float opticCone = VisionConfig.OpticLongRangeHalfFOV.Value;
                            if (crewPers == VeteranAndTacticsPatches.SoldierPersonality.TankCrew_Sniper)
                                opticCone += 5.0f;

                            if (angle <= opticCone)
                            {
                                _currentObserverOpticMultiplier = Mathf.Clamp(50f / turretAimFov, 1.0f, VisionConfig.MaxOpticZoomMultiplier.Value);
                                BoostedByScope++;
                            }
                        }
                    }

                    if (isWheeledOrTank)
                    {
                        float vehSightCap = VisionConfig.VehicleCamoDistance.Value * GetObserverOpticMultiplier();
                        if (night01 > 0.05f)
                        {
                            vehSightCap = targetMuzzleFlare
                                ? Mathf.Max(vehSightCap, 250.0f)
                                : vehSightCap * Mathf.Lerp(1.0f, 0.34f, night01);
                        }

                        if (dist > vehSightCap)
                        {
                            BlockedByRange++;
                            __result = false;
                            return false;
                        }
                    }

                    return true;
                }
                catch (Exception ex)
                {
                    AiCore.ReportPatchException("TurretCanSee", ex);
                    _currentObserverOpticMultiplier = 1.0f;
                    return true;
                }
            }

            public static void Postfix(long __state)
            {
                _currentObserverOpticMultiplier = 1.0f;
                HitchProfiler.End(HitchBucket.TurretPlaneSee, __state);
            }
        }

        // =====================================================================
        // 5. Aircraft Ground Spotting & CAS Target Priority (AIPlane.GetBestAircraftVisibleEnemy)
        // =====================================================================
        [HarmonyPatch(typeof(AIPlane), nameof(AIPlane.GetBestAircraftVisibleEnemy))]
        public static class AIPlane_GetBestAircraftVisibleEnemy_Patch
        {
            public static void Prefix(out long __state)
            {
                __state = HitchProfiler.Begin();
            }

            public static void Postfix(AIPlane __instance, ref Spottable __result, long __state)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value || __instance == null)
                        return;

                    float night01 = (VisionConfig.EnableRealisticNightDarkness != null && VisionConfig.EnableRealisticNightDarkness.Value)
                        ? AiCore.NightDarkness01
                        : 0f;

                    if (__result != null && !__result.IsAirVehicle())
                    {
                        if (VeteranAndTacticsPatches.IsAircraftTargetDangerCloseToFriendlySquad(
                                __instance,
                                __result.GetPosition(),
                                __instance.HasBombsToDeliver()))
                        {
                            BlockedPlaneSpot++;
                            __result = null;
                        }
                    }

                    if (__result != null && !__result.IsAirVehicle())
                    {
                        if (!__result.IsWheeledVehicleOrTank())
                        {
                            if (VisionConfig.LimitAircraftInfantrySpotting.Value || night01 >= 0.30f)
                            {
                                Soldier targetSoldier = __result.TryCast<Soldier>();
                                if (targetSoldier != null)
                                {
                                    Squad sq = targetSoldier.joinedSquad;
                                    bool isMarkedSquad = sq != null && (sq.isMarked || sq.isHalfMarked || sq.isMarkedPinPoint);
                                    if (!isMarkedSquad)
                                    {
                                        Vector3 planePos = __instance.transform.position;
                                        Vector3 solPos = targetSoldier.GetPosition();
                                        float dx = planePos.x - solPos.x;
                                        float dz = planePos.z - solPos.z;
                                        float horizDist = Mathf.Sqrt(dx * dx + dz * dz);
                                        float altAbove = Mathf.Max(0f, planePos.y - solPos.y);

                                        AiCore.TryGetSoldierHandles(targetSoldier.Pointer, out AiCore.SoldierFastHandles tSolH);
                                        bool activeMuzzleFlare = tSolH != null && tSolH.Vision.LastFiredTime > 0f && (Time.time - tSolH.Vision.LastFiredTime) <= 2.50f;
                                        bool recentlyFired = activeMuzzleFlare || GetFiringVisibilityBonus(targetSoldier) > 8f;
                                        bool squadEngaged = sq != null && sq.IsEnemyAwareOfThisSquad;

                                        // Account for 350m-500m patrol altitude geometry: a 55-deg forward cone at 400m altitude
                                        // has a minimum 3D slant distance of ~490m (horizontal standoff ~280m-580m).
                                        float baseHorizDist = Mathf.Min(720.0f, Mathf.Max(VisionConfig.AircraftUnmarkedInfantryMaxDist.Value * 2.5f, altAbove * 1.35f + 140.0f));
                                        float maxAirInfHorizDist = baseHorizDist;
                                        if (night01 >= 0.30f)
                                        {
                                            maxAirInfHorizDist = activeMuzzleFlare
                                                ? Mathf.Max(baseHorizDist * 0.75f, 320.0f)
                                                : Mathf.Lerp(baseHorizDist, 45.0f, night01);
                                        }

                                        bool concealedProne = VisionConfig.LimitAircraftInfantrySpotting.Value &&
                                                              targetSoldier.m_pose == SoldierPose.Prone &&
                                                              !recentlyFired &&
                                                              !squadEngaged;

                                        if (horizDist > maxAirInfHorizDist ||
                                            (night01 >= 0.45f && !activeMuzzleFlare) ||
                                            concealedProne)
                                        {
                                            BlockedPlaneSpot++;
                                            __result = null;
                                        }
                                    }
                                }
                            }
                        }
                        else if (night01 >= 0.35f)
                        {
                            // Unlit ground vehicles at night: aircraft cannot spot blacked-out armor from 500m+ unless firing tracers/cannons!
                            Vehicle targetVeh = __result.TryCast<Vehicle>();
                            if (targetVeh != null)
                            {
                                AiCore.VehicleFastHandles tvH = AiCore.GetOrCreateVehicleHandles(targetVeh);
                                bool vehFiringFlare = tvH != null && tvH.LastTurretFiredTime > 0f && (Time.time - tvH.LastTurretFiredTime) <= 2.50f;
                                if (!vehFiringFlare)
                                {
                                    float dist = Vector3.Distance(__instance.transform.position, __result.GetPosition());
                                    if (dist > Mathf.Lerp(450.0f, 115.0f, night01))
                                    {
                                        BlockedPlaneSpot++;
                                        __result = null;
                                    }
                                }
                            }
                        }
                    }

                    if (VisionConfig.EnableCombinedArmsPrioritySupport.Value)
                    {
                        VeteranAndTacticsPatches.TrySelectKnownAircraftTarget(__instance, ref __result);
                    }
                }
                catch { }
                finally
                {
                    HitchProfiler.End(HitchBucket.TurretPlaneSee, __state);
                }
            }
        }
    }
}
