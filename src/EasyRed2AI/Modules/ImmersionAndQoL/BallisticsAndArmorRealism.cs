using System;
using System.Collections.Generic;
using Corvostudio.CinematicCamera;
using Corvostudio.Weapons;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;
using UnityEngine.UI;

namespace EasyRed2HumanAI
{
    public static partial class VeteranAndTacticsPatches
    {
        // ============================================================================
        // MODULE: BALLISTICS, PLANE AUDIO DE-THROBBER & BATTLEFIELD IMMERSION (Phase 4)
        // Contains:
        //  1. Client-Side Plane Engine Loop De-Throbber (EnablePlaneAudioSmoothing)
        //  2. Material-Aware Thin-Cover Bullet Penetration (EnableThinCoverBulletPenetration)
        //  3. Parachute Wind Drift & Slower Descent (EnableParachuteWindDrift)
        //  4. Spectator / Free-Camera HUD Retention (EnableSpectatorHudRetention)
        // ============================================================================

        private struct PlaneAudioState
        {
            public IntPtr LastClipPtr;
            public float SmoothedPitch;
            public float PhaseOffset;
            public float PitchDetune;
        }

        private struct ParachuteDriftState
        {
            public Vector3 BeforeMoveDownPos;
            public float NextGroundCheckTime;
            public bool IsHighAboveGround;
            public float PhaseSeed;
        }

        private static readonly Dictionary<IntPtr, PlaneAudioState> _planeAudioStates = new Dictionary<IntPtr, PlaneAudioState>(32);
        private static readonly Dictionary<IntPtr, int> _colliderImpactCache = new Dictionary<IntPtr, int>(512);
        private static readonly Dictionary<IntPtr, ParachuteDriftState> _parachuteDriftStates = new Dictionary<IntPtr, ParachuteDriftState>(32);
        private static readonly Dictionary<IntPtr, float> _vehicleExplosionArmorCache = new Dictionary<IntPtr, float>(64);
        private static readonly Dictionary<IntPtr, TrailRenderer[]> _cachedTracerTrails = new Dictionary<IntPtr, TrailRenderer[]>(64);
        private static Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<RaycastHit> _overPenHits;

        public static int PlaneAudioLoopsDesyncedCount { get; private set; }
        public static int ThinCoverPenetrationsCount { get; private set; }
        public static int HumanOverPenetrationsCount { get; private set; }
        public static int ParachutesDriftedCount { get; private set; }
        public static int AircraftTopAttackPenetrationsAssistedCount { get; private set; }
        public static int AirplanePilotMgHitsBlockedCount { get; internal set; }
        public static int EnclosedCrewExplosionHitsBlockedCount { get; private set; }
        public static int EnclosedCrewBulletClipsBlockedCount { get; internal set; }
        public static int HostVanillaClientApAssistsCount { get; private set; }
        public static int BloodSmokeWaterOccludedCount { get; private set; }
        public static int UselessTankFragsBlockedCount { get; internal set; }

        internal static bool ActiveCrewSpallContext;
        internal static float ActiveCrewSpallMultiplier = 1.0f;
        internal static float ActiveCrewSpallMaxDamage = 9999f;

        public static void ClearBallisticsCaches(bool forceClear = false)
        {
            if (forceClear || _colliderImpactCache.Count > 1024)
                _colliderImpactCache.Clear();
            if (forceClear || _planeAudioStates.Count > 64)
                _planeAudioStates.Clear();
            if (forceClear || _parachuteDriftStates.Count > 64)
                _parachuteDriftStates.Clear();
            if (forceClear || _vehicleExplosionArmorCache.Count > 128)
                _vehicleExplosionArmorCache.Clear();
            if (forceClear || _cachedTracerTrails.Count > 128)
                _cachedTracerTrails.Clear();
            if (forceClear || _inspectedBloodFxRoots.Count > 128)
            {
                _inspectedBloodFxRoots.Clear();
                _inspectedBloodMatPtrs.Clear();
                _modifiedBloodRenderers.Clear();
                _modifiedBloodMaterials.Clear();
                _suppressedBloodFxRoots.Clear();
            }
            if (forceClear || _nextVehicleFireSeepageTime.Count > 64)
                _nextVehicleFireSeepageTime.Clear();
            if (forceClear)
            {
                _activeIncendiaryGrenades.Clear();
                ResetObjectiveBarFactionState();
                ResetTacticalReloadCancelState();
            }
        }

        // ============================================================================
        // 1. CLIENT-SIDE PLANE ENGINE LOOP DE-THROBBER
        // Desynchronizes loop playback offsets on start/near-far switch, clamps Doppler
        // pitch spikes (<= 0.22), and low-pass filters pitch changes to eliminate the
        // repetitive "wah-wah" flyby throbbing across single or multiple aircraft.
        // ============================================================================
        [HarmonyPatch(typeof(VehiclePlane), nameof(VehiclePlane.UpdateMotorSound))]
        public static class VehiclePlane_UpdateMotorSound_Patch
        {
            public static void Postfix(VehiclePlane __instance)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value || !VisionConfig.EnablePlaneAudioSmoothing.Value)
                        return;

                    if (__instance == null || __instance.Pointer == IntPtr.Zero)
                        return;

                    AiCore.VehicleFastHandles vh = AiCore.GetOrCreateVehicleHandles(__instance);
                    if (vh == null)
                        return;

                    if (!vh.EngineSourceChecked)
                    {
                        vh.EngineSourceChecked = true;
                        vh.CachedEngineSource = __instance.engine_source;
                    }
                    AudioSource src = vh.CachedEngineSource;
                    if (src == null || !src.isPlaying)
                        return;

                    IntPtr planePtr = __instance.Pointer;
                    if (!_planeAudioStates.TryGetValue(planePtr, out PlaneAudioState state))
                    {
                        uint hash = (uint)planePtr.ToInt64();
                        hash ^= hash >> 16;
                        hash *= 0x85ebca6bu;
                        hash ^= hash >> 13;
                        float norm = (hash & 0xFFFFu) / 65535f;

                        state = new PlaneAudioState
                        {
                            LastClipPtr = IntPtr.Zero,
                            SmoothedPitch = Mathf.Clamp(src.pitch, 0.85f, 1.15f),
                            PhaseOffset = 0.10f + norm * 0.78f,
                            PitchDetune = (norm - 0.5f) * 0.045f
                        };
                    }

                    float t = Time.unscaledTime;
                    if (t >= vh.NextPlaneAudioClipCheckTime || t < vh.NextPlaneAudioClipCheckTime - 2.0f)
                    {
                        vh.NextPlaneAudioClipCheckTime = t + 0.25f;
                        AudioClip clip = src.clip;
                        if (clip != null && clip.Pointer != IntPtr.Zero)
                        {
                            if (clip.Pointer != state.LastClipPtr)
                            {
                                state.LastClipPtr = clip.Pointer;
                                AudioClip startClip = __instance.engine_start;
                                bool isStartupClip = __instance.engineStarting ||
                                                     (startClip != null && startClip.Pointer == clip.Pointer);

                                // Never seek compressed FMOD startup streams or the local player's own cockpit audio
                                // (prevents synchronous decode hitches on plane entry and preserves startup sound).
                                if (!isStartupClip && !__instance.PlayerIsInside())
                                {
                                    float len = clip.length;
                                    if (len > 0.45f && src.time < 0.08f)
                                    {
                                        float targetTime = ((state.PhaseOffset + t * 0.29f) % 0.82f + 0.08f) * len;
                                        src.time = Mathf.Clamp(targetTime, 0.05f, len - 0.05f);
                                        PlaneAudioLoopsDesyncedCount++;
                                        AiCore.LogDebug("PlaneAudio", $"Desynchronized plane motor loop '{clip.name}' to {src.time:F2}s/{len:F2}s (detune={state.PitchDetune:+0.000;-0.000}).", 8.0f);
                                    }
                                }
                            }
                        }
                    }

                    // Clamp excessive Unity Doppler pitch warble on high-speed diving/flyby passes
                    if (src.dopplerLevel > 0.22f)
                        src.dopplerLevel = 0.22f;

                    // Break up the rigid metronome cadence of short engine_move loops at constant throttle:
                    // 1. Climb/dive vertical-velocity load (climbing lags RPM slightly; diving winds up prop RPM)
                    // 2. Bank-angle aerodynamic load
                    // 3. Incommensurate dual-sine governor micro-drift so consecutive loop wraps never fall on identical cadence
                    float flightLoadPitch = 0f;
                    Rigidbody rb = AiCore.GetCachedVehicleRb(vh);
                    if (rb != null)
                    {
                        float vy = rb.velocity.y;
                        flightLoadPitch += Mathf.Clamp(-vy * 0.0022f, -0.045f, 0.055f);
                    }
                    flightLoadPitch += Mathf.Clamp(Mathf.Abs(__instance.rollAngle) * 0.0004f, 0f, 0.02f);

                    float phase = state.PhaseOffset * 6.283185f;
                    float rpmDrift = Mathf.Sin(t * 1.37f + phase) * 0.015f + Mathf.Sin(t * 3.11f + phase * 1.7f) * 0.010f;

                    // Low-pass filter engine pitch transitions & apply per-plane harmonic detune + flight load
                    float targetPitch = Mathf.Clamp(src.pitch + state.PitchDetune + flightLoadPitch + rpmDrift, 0.76f, 1.24f);
                    float dt = Mathf.Clamp(Time.deltaTime, 0.001f, 0.1f);
                    state.SmoothedPitch = Mathf.Lerp(state.SmoothedPitch, targetPitch, dt * 4.8f);
                    src.pitch = state.SmoothedPitch;

                    _planeAudioStates[planePtr] = state;
                }
                catch { }
            }
        }

        // ============================================================================
        // 2. MATERIAL-AWARE THIN-COVER BULLET PENETRATION
        // Allows full-powered rifle/MG rounds (mmPenetration >= 6, speed >= 220m/s) to
        // penetrate thin wooden fences, doors, shutters, and planks (<= 0.35m) once,
        // and heavy high-caliber rounds (12.7mm+) to penetrate thin wood/generic/light
        // metal obstacles (<= 0.48m) using a fast single-collider back-face raycast.
        // ============================================================================
        [HarmonyPatch(typeof(Soldier), nameof(Soldier.Say))]
        public static class Soldier_Say_Patch
        {
            private const int DeathVoiceClipId = 4; // Soldier.Kill (0x180751D35) / <KillDelayed> (0x18077DB41)

            public static bool Prefix(Soldier __instance, ref VoiceManager.VoiceClip __0)
            {
                if (ReferenceEquals(__instance, null) || __instance == null || __instance.Pointer == IntPtr.Zero)
                    return false;

                if (__instance.IsDead && (int)__0 != DeathVoiceClipId)
                    return false;

                if (VisionConfig.EnableMod.Value)
                    __0 = AiCore.ResolveSafeVoiceClip(__instance, __0, allowYesVariation: true);

                return true;
            }
        }

        [HarmonyPatch(typeof(Soldier), nameof(Soldier.SyncSay))]
        public static class Soldier_SyncSay_Patch
        {
            private const int DeathVoiceClipId = 4;

            public static bool Prefix(Soldier __instance, ref VoiceManager.VoiceClip __0)
            {
                if (ReferenceEquals(__instance, null) || __instance == null || __instance.Pointer == IntPtr.Zero)
                    return false;

                if (__instance.IsDead && (int)__0 != DeathVoiceClipId)
                    return false;

                SyncSoldier syncher = __instance.GetSyncher();
                if (ReferenceEquals(syncher, null) || syncher == null || syncher.Pointer == IntPtr.Zero)
                    return false;

                if (VisionConfig.EnableMod.Value)
                    __0 = AiCore.ResolveSafeVoiceClip(__instance, __0, allowYesVariation: false);

                return true;
            }
        }

        [HarmonyPatch(typeof(Soldier), nameof(Soldier.SayClip))]
        public static class Soldier_SayClip_Patch
        {
            private static float ComputeDeterministicVoiceBasePitch(Soldier s, AiCore.SoldierFastHandles h)
            {
                string rawName = h != null && !string.IsNullOrEmpty(h.CachedFullName)
                    ? h.CachedFullName
                    : (s.name_surname ?? string.Empty);

                int baseLen = rawName.IndexOf(" [", StringComparison.Ordinal);
                if (baseLen < 0)
                    baseLen = rawName.Length;

                uint hash = 2166136261u;
                for (int i = 0; i < baseLen; i++)
                {
                    hash ^= rawName[i];
                    hash *= 16777619u;
                }

                int bornYear = s.bornYear;
                int seedVal = s._randomSeed_k__BackingField;
                hash ^= unchecked((uint)bornYear);
                hash *= 16777619u;
                hash ^= unchecked((uint)seedVal);
                hash *= 16777619u;

                // Only mix native pointer if neither name nor network seed identifies the soldier
                if (baseLen == 0 && seedVal == 0)
                {
                    hash ^= unchecked((uint)AiCore.GetDeterministicHash(s.Pointer, 757));
                    hash *= 16777619u;
                }

                // Murmur3 fmix32 finalizer for uniform bit distribution
                hash ^= hash >> 16;
                hash *= 0x85ebca6bu;
                hash ^= hash >> 13;
                hash *= 0xc2b2ae35u;
                hash ^= hash >> 16;

                float hashSpread = (((hash & 0xFFFFu) / 65535.0f) * 2.0f - 1.0f) * 0.058f;
                float ageBias = (bornYear >= 1895 && bornYear <= 1928)
                    ? Mathf.Clamp((bornYear - 1915) / 12.0f, -1.0f, 1.0f) * 0.014f
                    : 0.0f;

                return Mathf.Clamp(1.0f + hashSpread + ageBias, 0.93f, 1.07f);
            }

            public static void Prefix(Soldier __instance, AudioClip __0, bool __1, out float __state)
            {
                __state = 0f;
                try
                {
                    if (ReferenceEquals(__instance, null) || __instance == null || __instance.Pointer == IntPtr.Zero || __0 == null)
                        return;

                    AudioSource src = __instance.voiceSoundEmitter;
                    if (src == null)
                        return;

                    if (!VisionConfig.EnableMod.Value || !VisionConfig.EnableDynamicSoldierVoicePitch.Value)
                    {
                        if (Mathf.Abs(src.pitch - 1.0f) > 0.001f)
                            src.pitch = 1.0f;
                        return;
                    }

                    AiCore.SoldierFastHandles h = AiCore.GetOrCreateSoldierHandles(__instance);
                    if (h != null && h.CachedVoiceBasePitch <= 0.01f)
                        h.CachedVoiceBasePitch = ComputeDeterministicVoiceBasePitch(__instance, h);

                    float basePitch = h != null
                        ? h.CachedVoiceBasePitch
                        : ComputeDeterministicVoiceBasePitch(__instance, null);

                    uint lineSeq = h != null ? ++h.VoiceLineCounter : 1u;
                    int infHash = AiCore.GetDeterministicHash(__instance.Pointer, unchecked((int)(lineSeq * 131u)));
                    float inflection = (((infHash & 0xFF) / 255.0f) * 2.0f - 1.0f) * 0.010f;
                    float stressLift = (__instance.IsInjured || __instance.suppressionValue >= 60) ? 0.012f : 0.0f;

                    float finalPitch = Mathf.Clamp(basePitch + inflection + stressLift, 0.92f, 1.08f);
                    src.pitch = finalPitch;
                    __state = finalPitch;
                }
                catch { }
            }

            public static void Postfix(Soldier __instance, AudioClip __0, bool __1, float __state)
            {
                try
                {
                    if (__state <= 0.01f ||
                        ReferenceEquals(__instance, null) ||
                        __instance == null ||
                        __instance.Pointer == IntPtr.Zero ||
                        __0 == null)
                    {
                        return;
                    }

                    float clipLen = __0.length;
                    if (clipLen <= 0.01f)
                        return;

                    // Only scale talk/lock timers if native SayClip actually started playing this clip
                    if (Mathf.Abs(__instance.talkEndTime - (Time.unscaledTime + clipLen)) < 0.06f)
                    {
                        float scaledLen = clipLen / __state;
                        __instance.talkEndTime = Time.unscaledTime + scaledLen;
                        if (__1 && scaledLen > clipLen)
                        {
                            __instance.voiceLockedUntil = Mathf.Max(__instance.voiceLockedUntil, Time.time + scaledLen + 0.1f);
                        }
                    }
                }
                catch { }
            }
        }

        internal static bool TryComputeHumanOverPenetration(
            float impactSpeed,
            int massGrains,
            float caliberMm,
            int mmPenetration,
            bool isHighCaliber,
            bool isLimb,
            bool isHelmet,
            float bodyDepthMeters,
            out float exitSpeed,
            out float deflectionRad)
        {
            exitSpeed = 0f;
            deflectionRad = 0f;

            float impactKE = massGrains > 0
                ? (0.5f * impactSpeed * impactSpeed * (massGrains * 6.48e-5f))
                : 0f;

            bool isHeavyCal = isHighCaliber || caliberMm >= 11.0f || mmPenetration >= 14;
            if (isHelmet)
            {
                if (!isHeavyCal || impactSpeed < 620f || (massGrains > 0 && impactKE < 8000f))
                    return false;
            }
            else if (isLimb)
            {
                if (impactSpeed < 420f || mmPenetration < 2 || (massGrains > 0 && impactKE < 1150f))
                    return false;
            }
            else
            {
                if (impactSpeed < 520f || mmPenetration < 2 || (massGrains > 0 && impactKE < 1800f))
                    return false;
            }

            float maxBodyDepth = isHeavyCal ? 1.05f : (isLimb ? 0.55f : 0.72f);
            if (bodyDepthMeters <= 0.01f || bodyDepthMeters > maxBodyDepth)
                return false;

            float depthRatio = Mathf.Clamp01(bodyDepthMeters / maxBodyDepth);
            float retention;
            if (isHeavyCal)
            {
                retention = Mathf.Lerp(0.84f, 0.70f, depthRatio);
                if (isHelmet)
                    retention *= 0.90f;
            }
            else if (isLimb)
            {
                retention = Mathf.Lerp(0.76f, 0.60f, depthRatio);
            }
            else
            {
                retention = Mathf.Lerp(0.62f, 0.46f, depthRatio);
            }

            exitSpeed = impactSpeed * retention;
            if (exitSpeed < 180f)
                return false;

            deflectionRad = isHeavyCal ? 0.015f : (isLimb ? 0.030f : 0.048f);
            return true;
        }

        [HarmonyPatch(typeof(BulletInstance), nameof(BulletInstance.OnHit))]
        public static class BulletInstance_OnHit_Patch
        {
            private const int ImpactUnknownOrBlocked = -1;
            private const int ImpactHumanBodyPart = -2;
            private const int ImpactHumanHelmet = -3;

            public static bool Prefix(
                BulletInstance __instance,
                Vector3 hitDirection,
                BulletData bulletData,
                RaycastHit hit,
                out bool __state)
            {
                __state = false;

                try
                {
                    // Prevent vanilla BulletInstance.OnHit NullReferenceException (0x1809DE99C / 0x1809E03ED)
                    // when two simultaneous projectiles/explosions destroy a destructible prop collider on the same tick!
                    if (__instance == null || __instance.Pointer == IntPtr.Zero || bulletData == null || bulletData.Pointer == IntPtr.Zero)
                        return false;

                    Collider hitCol = hit.collider;
                    if (hitCol == null || hitCol.Pointer == IntPtr.Zero || hitCol.transform == null || hitCol.gameObject == null)
                        return false;

                    Soldier rawShooter = __instance.shooter;
                    if (ReferenceEquals(rawShooter, null) || rawShooter.Pointer == IntPtr.Zero)
                        return false;

                    // BulletInstance.OnHit (0x1809DEC0C) dereferences shooter.gameObject only for incendiary/fire ammo.
                    if (bulletData.IsFire() && (rawShooter == null || rawShooter.gameObject == null))
                        return false;

                    VeteranAndTacticsPatches.BeginProjectileImpactContext(__instance, bulletData, hitDirection);

                    if (!VisionConfig.EnableMod.Value)
                        return true;

                    VeteranAndTacticsPatches.NotifyProjectileImpactReaction(__instance, hitDirection, bulletData, hit);

                    // Prevent diving aircraft AP cannons/HMGs from falsely ricocheting off simplified
                    // tank hull/turret box colliders before VehicleDamagablePart.TryPenetrateArmor runs,
                    // and provide Host-authoritative AP roof-penetration assist for unmodded vanilla clients.
                    if (bulletData.mmPenetration >= 12 && (__instance.canRicochet || (!__instance.canDamage && AiCore.IsMasterClient())))
                    {
                        Soldier shooter = __instance.shooter;
                        bool isAirOrTopAttack =
                            ((__instance.startPosition.y - hit.point.y) >= 20.0f && hitDirection.y <= -0.15f);

                        if (!isAirOrTopAttack && shooter != null && shooter.Pointer != IntPtr.Zero && shooter.IsOnVehicle())
                        {
                            Vehicle shooterVeh = shooter.GetCurrentVehicle();
                            if (shooterVeh != null && shooterVeh.Pointer != IntPtr.Zero && shooterVeh.IsAirVehicle())
                                isAirOrTopAttack = true;
                        }

                        if (isAirOrTopAttack)
                        {
                            Collider col = hit.collider;
                            VehicleDamagablePart vPart = (col != null && col.Pointer != IntPtr.Zero)
                                ? col.GetComponent<VehicleDamagablePart>()
                                : null;

                            if (vPart != null && vPart.Pointer != IntPtr.Zero)
                            {
                                __instance.canRicochet = false;

                                // Host-Assist for Unmodded Vanilla Clients:
                                // When an unmodded remote player fires an AP gun from an aircraft/top-attack pass and
                                // vanilla TryPenetrateArmor is 100% guaranteed to reject penetration on their client
                                // (`mmPen <= baseArmor || mmPen <= vanillaAngledArmor`), promote `canDamage = true`
                                // solely for this impact on the Host so the Host evaluates our roof-armor check and
                                // syncs SyncVehicle damage to all players with zero risk of double-damage.
                                // Note: BulletData.IsHe() (0x1807ABB80) returns TRUE for ShellType.APHE (3)!
                                // Check shellType explicitly so aircraft APHE cannons are included in Host-Assist.
                                if (!__instance.canDamage &&
                                    bulletData.shellType != ShellType.HE &&
                                    bulletData.shellType != ShellType.Rocket &&
                                    vPart.armorThickness > 8f &&
                                    AiCore.IsMasterClient())
                                {
                                    Vehicle rootVeh = vPart.rootVeh;
                                    if (rootVeh != null &&
                                        rootVeh.Pointer != IntPtr.Zero &&
                                        !rootVeh.IsAirVehicle() &&
                                        AiCore.IsLocallyOwnedVehicleSyncher(rootVeh) &&
                                        shooter != null &&
                                        shooter.Pointer != IntPtr.Zero &&
                                        AiCore.IsAnyHumanPlayerSoldier(shooter) &&
                                        !AiCore.IsPlayerRunningBattleHardened(shooter))
                                    {
                                        float dist = Vector3.Distance(__instance.startPosition, hit.point);
                                        float mmPen = bulletData.CalculateMmPenetrationWithFallof(dist);
                                        float baseArmor = (float)(int)vPart.armorThickness;
                                        float obliquity = Vector3.Angle(-hitDirection, hit.normal);
                                        float vanillaArmor = vPart.considerAngledArmor
                                            ? (float)vPart.GetPenetrationAtAngle(obliquity)
                                            : baseArmor;

                                        if (mmPen <= baseArmor || mmPen <= vanillaArmor)
                                        {
                                            __instance.canDamage = true;
                                            __state = true;
                                            HostVanillaClientApAssistsCount++;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                catch { }
                return true;
            }

            public static void Postfix(
                BulletInstance __instance,
                Vector3 hitDirection,
                float impactSpeed,
                BulletData bulletData,
                RaycastHit hit,
                bool __state)
            {
                long profStart = HitchProfiler.BeginModScope();
                try
                {
                    if (__state && __instance != null)
                        __instance.canDamage = false;

                    VeteranAndTacticsPatches.EndProjectileImpactContext();

                    if (__state)
                        return;

                    if (!VisionConfig.EnableMod.Value)
                        return;

                    bool allowWoodPen = VisionConfig.EnableThinCoverBulletPenetration.Value;
                    bool allowHumanPen = VisionConfig.EnableHumanBulletOverPenetration.Value;
                    if (!allowWoodPen && !allowHumanPen)
                        return;

                    // Penetrated continuation bullets are spawned with canRicochet = false,
                    // which guarantees each projectile can penetrate thin cover or human flesh at most once.
                    if (__instance == null ||
                        !__instance.canRicochet ||
                        bulletData == null ||
                        ReferenceEquals(__instance.shooter, null) ||
                        __instance.shooter.Pointer == IntPtr.Zero)
                    {
                        return;
                    }

                    if (bulletData.IsHe() || bulletData.IsSmoke() || bulletData.IsFire() || bulletData.IsFlak() || bulletData.IsShotgun())
                        return;

                    Collider col = hit.collider;
                    if (col == null || col.Pointer == IntPtr.Zero || col.isTrigger)
                        return;

                    IntPtr colPtr = col.Pointer;
                    if (!_colliderImpactCache.TryGetValue(colPtr, out int cachedImpact))
                    {
                        cachedImpact = ClassifyColliderImpactType(col);
                        if (_colliderImpactCache.Count > 2048)
                            _colliderImpactCache.Clear();
                        _colliderImpactCache[colPtr] = cachedImpact;
                    }

                    if (cachedImpact == ImpactUnknownOrBlocked)
                        return;

                    if (cachedImpact == ImpactHumanBodyPart || cachedImpact == ImpactHumanHelmet)
                    {
                        if (allowHumanPen)
                            TryHandleHumanOverPenetration(__instance, hitDirection, impactSpeed, bulletData, hit, col, cachedImpact);
                        return;
                    }

                    if (!allowWoodPen || impactSpeed < 220f || bulletData.mmPenetration < 6)
                        return;

                    ImpactType impactType = (ImpactType)cachedImpact;
                    bool isHighCal = bulletData.IsHighCaliber();

                    bool canPenetrateMaterial =
                        impactType == ImpactType.wood ||
                        (isHighCal && bulletData.mmPenetration >= 14 && (impactType == ImpactType.generic || impactType == ImpactType.metal));

                    if (!canPenetrateMaterial)
                        return;

                    float maxThickness = isHighCal ? 0.48f : 0.35f;
                    if (hitDirection.sqrMagnitude < 0.0001f)
                        return;

                    Vector3 dir = hitDirection.normalized;
                    Vector3 probeOrigin = hit.point + dir * (maxThickness + 0.04f);
                    Ray backRay = new Ray(probeOrigin, -dir);

                    // Single-collider back-face probe (O(1), no physics world broadphase)
                    if (!col.Raycast(backRay, out RaycastHit exitHit, maxThickness + 0.03f))
                        return;

                    float thickness = Vector3.Distance(hit.point, exitHit.point);
                    if (thickness < 0.008f || thickness > maxThickness)
                        return;

                    Vector3 exitPos = exitHit.point + dir * 0.035f;
                    float thicknessRatio = Mathf.Clamp01(thickness / maxThickness);
                    float exitSpeed = impactSpeed * Mathf.Lerp(0.64f, 0.42f, thicknessRatio);
                    if (exitSpeed < 120f)
                        return;

                    // Deterministic slight deflection (~1.1 deg max) after passing through cover
                    Vector3 jitter = new Vector3(
                        Mathf.Sin(hit.point.x * 37.3f + hit.point.z * 13.7f) * 0.016f,
                        Mathf.Cos(hit.point.y * 29.1f + hit.point.x * 19.3f) * 0.014f,
                        Mathf.Sin(hit.point.z * 43.7f + hit.point.y * 17.1f) * 0.016f);
                    Vector3 exitDir = (dir + jitter).normalized;

                    SpawnPenetratedContinuationBullet(__instance, bulletData, exitPos, exitDir, exitSpeed);

                    ThinCoverPenetrationsCount++;
                    AiCore.LogDebug("Ballistics", $"Bullet penetrated {thickness:F2}m {impactType} cover (in={impactSpeed:F0}m/s -> out={exitSpeed:F0}m/s, pen={bulletData.mmPenetration}mm).", 5.0f);
                }
                catch { }
                finally
                {
                    HitchProfiler.EndModScope(HitchBucket.BallisticsExpl, profStart);
                }
            }

            private static void SpawnPenetratedContinuationBullet(
                BulletInstance src,
                BulletData bulletData,
                Vector3 exitPos,
                Vector3 exitDir,
                float exitSpeed)
            {
                bool penCanDamage = src.canDamage;
                if (!penCanDamage &&
                    AiCore.IsInMultiplayerRoom() &&
                    AiCore.IsMasterClient() &&
                    src.shooter != null &&
                    src.shooter.Pointer != IntPtr.Zero &&
                    AiCore.IsAnyHumanPlayerSoldier(src.shooter) &&
                    !AiCore.IsPlayerRunningBattleHardened(src.shooter))
                {
                    // Unmodded vanilla client's own bullet stopped on the front face (0% double-damage risk);
                    // promote Host's penetrated continuation round to authoritative damage for two-way fairness!
                    penCanDamage = true;
                    HostVanillaClientApAssistsCount++;
                }

                var penetratedBullet = new BulletInstance(
                    exitPos,
                    exitDir,
                    exitSpeed,
                    bulletData,
                    src.shooter,
                    false, // Prevents a penetrated round from penetrating or ricocheting a second time
                    penCanDamage,
                    null);

                ProjectilesManager pm = ProjectilesManager.instance;
                if (pm != null && pm.activeBullets != null)
                {
                    int count = pm.activeBullets.Count;
                    if (count == 0 || pm.activeBullets[count - 1] == null || pm.activeBullets[count - 1].Pointer != penetratedBullet.Pointer)
                    {
                        ProjectilesManager.OnNewBulletCreated(penetratedBullet);
                    }
                }
            }

            private static bool IsTransformDescendantOf(Transform tr, IntPtr ancestorPtr)
            {
                if (ancestorPtr == IntPtr.Zero)
                    return false;

                for (int hop = 0; hop < 14 && tr != null && tr.Pointer != IntPtr.Zero; hop++)
                {
                    if (tr.Pointer == ancestorPtr)
                        return true;
                    tr = tr.parent;
                }
                return false;
            }

            private static void TryHandleHumanOverPenetration(
                BulletInstance bullet,
                Vector3 hitDirection,
                float impactSpeed,
                BulletData bulletData,
                RaycastHit hit,
                Collider col,
                int cachedImpact)
            {
                if (hitDirection.sqrMagnitude < 0.0001f)
                    return;

                bool isHelmet = cachedImpact == ImpactHumanHelmet;
                Creature victim = null;
                BodyPartType partType = BodyPartType.chest;
                if (!isHelmet)
                {
                    BodyPart bp = col.GetComponent<BodyPart>();
                    if (bp == null || bp.Pointer == IntPtr.Zero)
                        return;
                    victim = bp.unit;
                    partType = bp.bodyPart == BodyPartType.auto_detect ? bp.GetNearestBodyPart(hit.point) : bp.bodyPart;
                }
                else
                {
                    victim = col.GetComponentInParent<Creature>();
                    partType = BodyPartType.head;
                }

                if (victim == null || victim.Pointer == IntPtr.Zero || victim.transform == null || victim.Pointer == bullet.shooter.Pointer)
                    return;

                bool isLimb = partType == BodyPartType.arm_l ||
                              partType == BodyPartType.arm_r ||
                              partType == BodyPartType.leg_l ||
                              partType == BodyPartType.leg_r;
                bool isHighCal = bulletData.IsHighCaliber();

                // Fast kinetic/velocity gate before any physics exit probe (zero overhead for pistols/SMGs/slow bullets).
                if (!TryComputeHumanOverPenetration(
                        impactSpeed,
                        bulletData.mass_grains,
                        bulletData.caliber,
                        bulletData.mmPenetration,
                        isHighCal,
                        isLimb,
                        isHelmet,
                        0.20f,
                        out _,
                        out _))
                {
                    return;
                }

                Vector3 dir = hitDirection.normalized;
                float entryExitDist = isLimb ? 0.14f : 0.26f;
                if (col.Raycast(new Ray(hit.point + dir * 0.85f, -dir), out RaycastHit entryBackHit, 0.84f))
                {
                    float backD = Vector3.Dot(entryBackHit.point - hit.point, dir);
                    if (backD > 0.02f && backD <= 0.85f)
                        entryExitDist = Mathf.Clamp(backD, 0.04f, 0.85f);
                }

                if (_overPenHits == null)
                    _overPenHits = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<RaycastHit>(16);

                IntPtr victimTrPtr = victim.transform.Pointer;
                Transform shooterRoot = bullet.shooter.transform != null ? bullet.shooter.transform.root : null;
                IntPtr shooterRootPtr = (shooterRoot != null && shooterRoot.Pointer != IntPtr.Zero) ? shooterRoot.Pointer : IntPtr.Zero;

                float nearestOtherDist = 999f;
                float farthestVictimExitDist = entryExitDist;
                int hitCount = Physics.RaycastNonAlloc(
                    hit.point + dir * 0.02f,
                    dir,
                    _overPenHits,
                    1.40f,
                    ResourcesManager.bulletMask.value,
                    QueryTriggerInteraction.Ignore);

                for (int i = 0; i < hitCount; i++)
                {
                    Collider hCol = _overPenHits[i].collider;
                    if (hCol == null || hCol.Pointer == IntPtr.Zero || hCol.isTrigger)
                        continue;

                    Transform hTr = hCol.transform;
                    if (IsTransformDescendantOf(hTr, victimTrPtr) || IsTransformDescendantOf(hTr, shooterRootPtr))
                        continue;

                    float d = Vector3.Dot(_overPenHits[i].point - hit.point, dir);
                    if (d > 0.005f && d < nearestOtherDist)
                        nearestOtherDist = d;
                }

                for (int i = 0; i < hitCount; i++)
                {
                    Collider hCol = _overPenHits[i].collider;
                    if (hCol == null || hCol.Pointer == IntPtr.Zero || hCol.isTrigger || hCol.Pointer == col.Pointer)
                        continue;

                    if (!IsTransformDescendantOf(hCol.transform, victimTrPtr))
                        continue;

                    float forwardD = Vector3.Dot(_overPenHits[i].point - hit.point, dir);
                    if (forwardD >= nearestOtherDist)
                        continue;

                    float subExit = forwardD + 0.14f;
                    if (hCol.Raycast(new Ray(_overPenHits[i].point + dir * 0.70f, -dir), out RaycastHit vBack, 0.69f))
                    {
                        float bd = Vector3.Dot(vBack.point - hit.point, dir);
                        if (bd > forwardD && bd <= 1.25f)
                            subExit = bd;
                    }

                    if (subExit > farthestVictimExitDist)
                        farthestVictimExitDist = subExit;
                }

                if (!TryComputeHumanOverPenetration(
                        impactSpeed,
                        bulletData.mass_grains,
                        bulletData.caliber,
                        bulletData.mmPenetration,
                        isHighCal,
                        isLimb,
                        isHelmet,
                        farthestVictimExitDist,
                        out float exitSpeed,
                        out float deflectionRad))
                {
                    return;
                }

                if (nearestOtherDist <= 0.05f)
                    return;

                float exitDist = farthestVictimExitDist + 0.04f;
                if (exitDist >= nearestOtherDist - 0.02f)
                {
                    if (nearestOtherDist <= entryExitDist * 0.65f)
                        return;
                    exitDist = nearestOtherDist - 0.025f;
                }

                Vector3 exitPos = hit.point + dir * exitDist;
                Vector3 jitter = new Vector3(
                    Mathf.Sin(hit.point.x * 41.3f + hit.point.z * 17.9f) * deflectionRad,
                    Mathf.Cos(hit.point.y * 31.7f + hit.point.x * 23.1f) * (deflectionRad * 0.85f),
                    Mathf.Sin(hit.point.z * 47.1f + hit.point.y * 19.7f) * deflectionRad);
                Vector3 exitDir = (dir + jitter).normalized;

                SpawnPenetratedContinuationBullet(bullet, bulletData, exitPos, exitDir, exitSpeed);
                HumanOverPenetrationsCount++;
                AiCore.LogDebug(
                    "Ballistics",
                    $"Bullet over-penetrated {partType} ({farthestVictimExitDist:F2}m flesh, in={impactSpeed:F0}m/s -> out={exitSpeed:F0}m/s, cal={bulletData.caliber:F1}mm).",
                    5.0f);
            }

            public static void Finalizer(BulletInstance __instance, bool __state)
            {
                if (__state && !ReferenceEquals(__instance, null) && __instance.Pointer != IntPtr.Zero)
                {
                    try { __instance.canDamage = false; } catch { }
                }
                VeteranAndTacticsPatches.EndProjectileImpactContext();
            }

            private static int ClassifyColliderImpactType(Collider col)
            {
                try
                {
                    // Never treat terrain as thin static cover props; human BodyPart/ItemHelmet use dedicated over-pen path.
                    if (AiCore.IsTerrainCollider(col))
                        return ImpactUnknownOrBlocked;
                    if (col.GetComponent<BodyPart>() != null)
                        return ImpactHumanBodyPart;
                    if (col.GetComponent<ItemHelmet>() != null)
                        return ImpactHumanHelmet;
                    if (col.GetComponentInParent<Creature>() != null)
                        return ImpactUnknownOrBlocked;
                    if (col.GetComponentInParent<Vehicle>() != null)
                        return ImpactUnknownOrBlocked;

                    // CRITICAL IL2CPP FIX: ImpactSpecifier is an IL2CPP interface (Il2CppObjectBase), so generic
                    // col.GetComponent<ImpactSpecifier>() fails in IL2CPP! Use Il2CppType.Of<ImpactSpecifier>() + TryCast.
                    var specType = Il2CppInterop.Runtime.Il2CppType.Of<ImpactSpecifier>();
                    Component specComp = col.GetComponent(specType);
                    if (specComp == null)
                        specComp = col.GetComponentInParent(specType);

                    ImpactSpecifier spec = specComp != null ? specComp.TryCast<ImpactSpecifier>() : null;
                    if (spec != null)
                    {
                        ImpactType t = spec.ImpactType;
                        if (t == ImpactType.wood || t == ImpactType.generic || t == ImpactType.metal)
                            return (int)t;
                        return ImpactUnknownOrBlocked;
                    }

                    string name = col.name;
                    if (!string.IsNullOrEmpty(name))
                    {
                        string lower = name.ToLowerInvariant();
                        if (lower.Contains("wood") || lower.Contains("fence") || lower.Contains("door") ||
                            lower.Contains("plank") || lower.Contains("shutter") || lower.Contains("crate") ||
                            lower.Contains("palisade") || lower.Contains("tatami") || lower.Contains("bamboo"))
                        {
                            return (int)ImpactType.wood;
                        }
                    }
                }
                catch { }

                return ImpactUnknownOrBlocked;
            }
        }

        // ============================================================================
        // 2A-2. POOLED TRACER MID-AIR DOGLEG FIX & INDIRECT-FIRE RICOCHET GUARD
        // - In vanilla BulletInstance.AlignBulletEffect (0x1809DCE10), off-screen artillery/mortar
        //   shells only pop their pooled TracerBehaviour on the mid-air frame they enter camera view,
        //   but spawn it at `this.startPosition` (hundreds of meters away) without calling TrailRenderer.Clear(),
        //   drawing a ~90-degree horizontal-to-vertical dogleg across the sky on the next frame!
        // - Also prevents plunging indirect-fire artillery/mortar rounds from deflecting sideways off foliage/roofs.
        // ============================================================================
        private static void ClearTracerTrailsCached(GameObject tracerGo)
        {
            if (tracerGo == null || tracerGo.Pointer == IntPtr.Zero)
                return;

            IntPtr goPtr = tracerGo.Pointer;
            if (!_cachedTracerTrails.TryGetValue(goPtr, out TrailRenderer[] trails))
            {
                var il2cppTrails = tracerGo.GetComponentsInChildren<TrailRenderer>(true);
                if (il2cppTrails != null && il2cppTrails.Count > 0)
                {
                    trails = new TrailRenderer[il2cppTrails.Count];
                    for (int i = 0; i < il2cppTrails.Count; i++)
                        trails[i] = il2cppTrails[i];
                }
                else
                {
                    trails = Array.Empty<TrailRenderer>();
                }
                if (_cachedTracerTrails.Count > 128)
                    _cachedTracerTrails.Clear();
                _cachedTracerTrails[goPtr] = trails;
            }

            for (int i = 0; i < trails.Length; i++)
            {
                TrailRenderer tr = trails[i];
                if (tr != null && tr.Pointer != IntPtr.Zero)
                {
                    tr.Clear();
                }
            }
        }

        [HarmonyPatch(typeof(BulletInstance), nameof(BulletInstance._AlignBulletEffect_b__40_0))]
        public static class BulletInstance_OnTracerPopped_Patch
        {
            public static void Postfix(BulletInstance __instance, GameObject __0)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value || __instance == null || __0 == null || __0.Pointer == IntPtr.Zero)
                        return;

                    Vector3 cur = __instance.curPos;
                    if (cur.sqrMagnitude > 0.01f && (cur - __instance.startPosition).sqrMagnitude > 0.25f && __0.transform != null)
                    {
                        __0.transform.position = cur;
                        Vector3 vel = __instance.curVelocity;
                        if (vel.sqrMagnitude > 0.01f)
                        {
                            __0.transform.rotation = Quaternion.LookRotation(vel.normalized);
                        }
                    }

                    ClearTracerTrailsCached(__0);
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(BulletInstance), nameof(BulletInstance.DestroyFX))]
        public static class BulletInstance_DestroyFX_Patch
        {
            public static void Prefix(BulletInstance __instance)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value || __instance == null)
                        return;

                    TracerBehaviour tracer = __instance.tracerInstance;
                    if (tracer != null && tracer.Pointer != IntPtr.Zero && tracer.gameObject != null)
                    {
                        ClearTracerTrailsCached(tracer.gameObject);
                    }
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(BulletInstance), nameof(BulletInstance.TryRichochet))]
        public static class BulletInstance_TryRichochet_Patch
        {
            public static bool Prefix(
                BulletInstance __instance,
                Vector3 __1,
                BulletData __3,
                RaycastHit __4,
                ref bool __result)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value || __instance == null)
                        return true;

                    // Plunging mortar and heavy indirect-fire artillery shells should detonate/impact cleanly
                    // rather than bouncing at bizarre 90-degree angles off tree canopies or roof edges.
                    if (__instance.isMortar ||
                        (__3 != null && __3.caliber >= 50.0f &&
                         (__1.y <= -0.32f || (__instance.startPosition.y - __4.point.y) >= 18.0f)))
                    {
                        __result = false;
                        return false;
                    }
                }
                catch { }
                return true;
            }
        }

        // ============================================================================
        // 2B. REALISTIC AIRCRAFT CANOPY/SPALL, TOP-ATTACK AP & ENCLOSED TANK CREW ARMOR
        // - Normalizes top-down roof impact angles and models thinner tank roof/engine-deck
        //   armor (plus diving aircraft kinetic energy) when aircraft AP weapons strike ground armor.
        // - Replaces vanilla's oversized 1.2m x 2.0m x 1.2m Random(10..150) airplane fuselage
        //   spall box: blocks rifle-caliber (< 12.5mm) fuselage spall while allowing tight 0.42m
        //   cockpit strikes from .50 cal / HMG (12.5mm-17.9mm) with scaled fragmentation, and
        //   full 1.0x lethality for 18mm+ cannons, HE, and Flak.
        // - Scales solid kinetic AP spall inside armored ground vehicles by shell caliber.
        // - Protects buttoned-up crew inside intact enclosed armor (!seat.showUnitSet) from
        //   non-penetrating external blasts (BodyPart.TryDamageWithExplosion), converting light
        //   external explosions into concussive suppression while AT grenades, HEAT, mines,
        //   satchel charges, heavy artillery, aerial bombs, and open-topped seats stay lethal.
        // ============================================================================
        internal static void EvaluateArmorImpactModifiers(
            bool targetIsAir,
            bool coverDestroyedOrDisabled,
            bool isHeOrFlak,
            float caliberMm,
            int mmPenetration,
            float basePlateArmor,
            bool isAirOrTopAttack,
            bool nearAirCrew,
            float obliquityDeg,
            out float effectiveArmorMm,
            out float effectiveObliquityDeg,
            out bool modifiedArmor,
            out bool modifiedCrewSpall,
            out float crewSpallMult,
            out float crewSpallMaxDamage,
            out bool blockedAirMgHit)
        {
            effectiveArmorMm = basePlateArmor;
            effectiveObliquityDeg = obliquityDeg;
            modifiedArmor = false;
            modifiedCrewSpall = false;
            crewSpallMult = 1.0f;
            crewSpallMaxDamage = 9999f;
            blockedAirMgHit = false;

            // Case 1: Intact airplane fuselage hit by small-arms or heavy machine gun (< 18mm, non-HE, non-Flak).
            if (targetIsAir)
            {
                if (!coverDestroyedOrDisabled && !isHeOrFlak && caliberMm < 18f)
                {
                    if (caliberMm >= 12.5f && nearAirCrew)
                    {
                        modifiedCrewSpall = true;
                        crewSpallMult = 0.28f;
                        crewSpallMaxDamage = 34.0f;
                    }
                    else
                    {
                        modifiedCrewSpall = true;
                        crewSpallMult = 0.0f;
                        crewSpallMaxDamage = 0.0f;
                        blockedAirMgHit = true;
                    }
                }
                return;
            }

            // Case 2: Ground vehicle / tank hit by an aircraft AP weapon or steep top-down dive attack.
            if (mmPenetration >= 12 && basePlateArmor > 8f && isAirOrTopAttack)
            {
                modifiedArmor = true;
                if (effectiveObliquityDeg > 15.0f)
                    effectiveObliquityDeg = 15.0f;
                effectiveArmorMm = Mathf.Max(8.0f, basePlateArmor * 0.45f);
            }

            // Case 3: Solid kinetic AP spall scaling inside armored ground vehicles (>= 8mm hull/turret armor).
            if (!coverDestroyedOrDisabled && !isHeOrFlak && basePlateArmor >= 8.0f)
            {
                if (caliberMm < 25.0f)
                {
                    modifiedCrewSpall = true;
                    crewSpallMult = 0.40f;
                    crewSpallMaxDamage = 45.0f;
                }
                else if (caliberMm < 58.0f)
                {
                    modifiedCrewSpall = true;
                    crewSpallMult = 0.72f;
                    crewSpallMaxDamage = 95.0f;
                }
            }
        }

        [HarmonyPatch(typeof(VehicleDamagablePart), nameof(VehicleDamagablePart.TryPenetrateArmor))]
        public static class VehicleDamagablePart_TryPenetrateArmor_Patch
        {
            public struct ArmorPenetrationPatchState
            {
                public float SavedArmorThickness;
                public bool ModifiedArmorThickness;
                public bool ModifiedCrewSpall;
                public IntPtr PrevAttackerPtr;
                public bool RestoreAttackerPtr;
            }

            private static bool IsImpactNearSeatedAirCrew(Vehicle airVeh, Vector3 impactPos, float maxDistSq)
            {
                var seats = airVeh.seats;
                if (seats == null)
                    return false;

                int count = seats.Length;
                for (int i = 0; i < count; i++)
                {
                    Seats seat = seats[i];
                    if (seat == null)
                        continue;

                    Soldier crew = seat.unitSet;
                    if (crew != null && crew.Pointer != IntPtr.Zero && crew.IsAlive)
                    {
                        if ((crew.GetCenterOfUnit() - impactPos).sqrMagnitude <= maxDistSq)
                            return true;
                    }
                }
                return false;
            }

            public static void Prefix(
                VehicleDamagablePart __instance,
                BulletData __0,
                Vector3 __1,
                Vector3 __2,
                Vector3 __3,
                ref float __4,
                Soldier __5,
                out ArmorPenetrationPatchState __state)
            {
                __state = default;

                try
                {
                    if (!VisionConfig.EnableMod.Value)
                        return;

                    __state.PrevAttackerPtr = _activeDamageAttackerPtr;
                    __state.RestoreAttackerPtr = true;
                    if (__5 != null && __5.Pointer != IntPtr.Zero)
                    {
                        _activeDamageAttackerPtr = __5.Pointer;
                        if (__instance != null && __instance.Pointer != IntPtr.Zero && IsPlayerCharacter(__5))
                        {
                            Vehicle hitRootVeh = __instance.rootVeh;
                            if (hitRootVeh != null && hitRootVeh.Pointer != IntPtr.Zero)
                                RecordLocalPlayerDamagedVehicle(hitRootVeh.Pointer);
                        }
                    }

                    if (!VisionConfig.EnableRealisticVehicleArmorAndTopAttack.Value ||
                        __instance == null ||
                        __instance.Pointer == IntPtr.Zero ||
                        __0 == null)
                    {
                        return;
                    }

                    Vehicle rootVeh = __instance.rootVeh;
                    bool targetIsAir = rootVeh != null && rootVeh.Pointer != IntPtr.Zero && rootVeh.IsAirVehicle();
                    bool coverDestroyedOrDisabled = rootVeh == null || rootVeh.Pointer == IntPtr.Zero || rootVeh.IsCoverDestroyed() || rootVeh.IsDisabled();
                    bool isHeOrFlak = __0.IsHe() || __0.IsFlak();
                    float basePlateArmor = __instance.armorThickness;

                    bool nearAirCrew = false;
                    if (targetIsAir && !coverDestroyedOrDisabled && !isHeOrFlak && __0.caliber >= 12.5f && __0.caliber < 18f)
                        nearAirCrew = IsImpactNearSeatedAirCrew(rootVeh, __2, 0.42f * 0.42f);

                    bool isAirOrTopAttack = false;
                    if (!targetIsAir && __0.mmPenetration >= 12 && basePlateArmor > 8f)
                    {
                        isAirOrTopAttack = ((__1.y - __2.y) >= 20.0f && __3.y <= -0.15f);
                        if (!isAirOrTopAttack && __5 != null && __5.Pointer != IntPtr.Zero && __5.IsOnVehicle())
                        {
                            Vehicle shooterVeh = __5.GetCurrentVehicle();
                            if (shooterVeh != null && shooterVeh.Pointer != IntPtr.Zero && shooterVeh.IsAirVehicle())
                                isAirOrTopAttack = true;
                        }
                    }

                    EvaluateArmorImpactModifiers(
                        targetIsAir,
                        coverDestroyedOrDisabled,
                        isHeOrFlak,
                        __0.caliber,
                        __0.mmPenetration,
                        basePlateArmor,
                        isAirOrTopAttack,
                        nearAirCrew,
                        __4,
                        out float effectiveArmorMm,
                        out float effectiveObliquityDeg,
                        out bool modifiedArmor,
                        out bool modifiedCrewSpall,
                        out float crewSpallMult,
                        out float crewSpallMaxDamage,
                        out bool blockedAirMgHit);

                    if (modifiedArmor)
                    {
                        __state.SavedArmorThickness = basePlateArmor;
                        __state.ModifiedArmorThickness = true;
                        __4 = effectiveObliquityDeg;
                        __instance.armorThickness = effectiveArmorMm;
                        AircraftTopAttackPenetrationsAssistedCount++;
                    }

                    if (modifiedCrewSpall)
                    {
                        ActiveCrewSpallContext = true;
                        ActiveCrewSpallMultiplier = crewSpallMult;
                        ActiveCrewSpallMaxDamage = crewSpallMaxDamage;
                        __state.ModifiedCrewSpall = true;
                        if (blockedAirMgHit)
                            AirplanePilotMgHitsBlockedCount++;
                    }
                }
                catch { }
            }

            public static void Finalizer(VehicleDamagablePart __instance, ArmorPenetrationPatchState __state)
            {
                if (__state.RestoreAttackerPtr)
                    _activeDamageAttackerPtr = __state.PrevAttackerPtr;

                if (__state.ModifiedCrewSpall)
                {
                    ActiveCrewSpallContext = false;
                    ActiveCrewSpallMultiplier = 1.0f;
                    ActiveCrewSpallMaxDamage = 9999f;
                }

                if (__state.ModifiedArmorThickness && !ReferenceEquals(__instance, null) && __instance.Pointer != IntPtr.Zero)
                {
                    try
                    {
                        __instance.armorThickness = __state.SavedArmorThickness;
                    }
                    catch { }
                }
            }
        }

        [HarmonyPatch(typeof(Soldier), nameof(Soldier.Damage), new Type[] { typeof(float) })]
        public static class Soldier_Damage_Patch
        {
            public static bool Prefix(Soldier __instance, ref float __0, out bool __state)
            {
                long profStart = HitchProfiler.BeginModScope();
                try
                {
                    __state = !ReferenceEquals(__instance, null) &&
                              __instance != null &&
                              __instance.Pointer != IntPtr.Zero &&
                              __instance.IsAlive;

                    if (!ActiveCrewSpallContext)
                        return true;

                    if (ActiveCrewSpallMultiplier <= 0.001f)
                        return false;

                    __0 = Mathf.Min(__0 * ActiveCrewSpallMultiplier, ActiveCrewSpallMaxDamage);
                    return __0 > 0.05f;
                }
                finally
                {
                    HitchProfiler.EndModScope(HitchBucket.DeathHitAttr, profStart);
                }
            }

            public static void Postfix(Soldier __instance, bool __state)
            {
                if (!__state || !VisionConfig.EnableMod.Value)
                    return;

                try
                {
                    if (!ReferenceEquals(__instance, null) &&
                        __instance != null &&
                        __instance.Pointer != IntPtr.Zero)
                    {
                        if (!__instance.IsAlive)
                        {
                            NotifySquadMemberKilled(__instance);
                        }
                        else if (!__instance.IsInjured &&
                                 !__instance.IsDead &&
                                 __instance.IsAI() &&
                                 AiCore.IsLocallyOwnedBot(__instance) &&
                                 (AiCore.GetDeterministicHash(__instance.Pointer, (int)(Time.time * 4.0f)) & 7) < 5)
                        {
                            VeteranAndTacticsPatches.TrySayTacticalVoice(__instance, __instance.joinedSquad, VoiceManager.VoiceClip.iVeBeenHit, 0.05f);
                        }
                    }
                }
                catch { }
            }
        }

        internal static float GetEffectiveVehicleExplosionArmor(Vehicle veh)
        {
            if (veh == null || veh.Pointer == IntPtr.Zero)
                return 0f;

            IntPtr ptr = veh.Pointer;
            if (_vehicleExplosionArmorCache.TryGetValue(ptr, out float cachedArmor))
                return cachedArmor;

            float maxHullArmor = 0f;
            var parts = veh.damageableParts;
            if (parts != null)
            {
                int count = parts.Count;
                for (int i = 0; i < count; i++)
                {
                    VehicleDamagablePart p = parts[i];
                    if (p == null || p.Pointer == IntPtr.Zero)
                        continue;

                    if (p.TryCast<VehicleDamageableWheelPart>() != null ||
                        p.TryCast<VehicleDamageableTrackPart>() != null ||
                        p.TryCast<VehicleDamagableDetachablePart>() != null)
                    {
                        continue;
                    }

                    if (p.armorThickness > maxHullArmor)
                        maxHullArmor = p.armorThickness;
                }
            }

            float effArmor = maxHullArmor < 6.0f
                ? 0.0f
                : Mathf.Clamp(maxHullArmor * 0.35f, 8.0f, 28.0f);

            _vehicleExplosionArmorCache[ptr] = effArmor;
            return effArmor;
        }

        internal static bool IsSoldierInEnclosedVehicleSeat(Vehicle veh, Soldier victim)
        {
            if (veh == null || victim == null)
                return false;

            var seats = veh.seats;
            if (seats == null)
                return false;

            IntPtr victimPtr = victim.Pointer;
            int count = seats.Length;
            for (int i = 0; i < count; i++)
            {
                Seats seat = seats[i];
                if (seat != null && seat.unitSet != null && seat.unitSet.Pointer == victimPtr)
                    return !seat.showUnitSet;
            }
            return false;
        }

        /// <summary>
        /// Determines whether a thrown item can actually damage, blind, burn, or kill exposed crew on <paramref name="targetVeh"/>.
        /// Prevents AI soldiers from wasting standard fragmentation grenades (&lt; 12mm blast penetration) against buttoned-up
        /// tanks and enclosed armored vehicles while allowing AT grenades, Molotov/phosphorus grenades, smoke screens,
        /// soft-skinned vehicle attacks, and open-topped crew kills.
        /// </summary>
        internal static bool CanThrowableDamageOrFlushVehicle(Soldier thrower, VirtualItem throwable, Vehicle targetVeh)
        {
            if (throwable == null || targetVeh == null || targetVeh.Pointer == IntPtr.Zero)
                return false;

            if (AiCore.IsPressureMineItem(throwable, out _))
                return false;

            if (throwable.TryCast<VirtualSmokeGrenade>() != null || throwable.TryCast<VirtualATGrenade>() != null)
                return true;

            string itemId = throwable.item_id ?? string.Empty;
            AiCore.TryGetThrowablePrefabInfo(throwable, out AiCore.ThrowablePrefabInfo info);

            if (info.IsValid &&
                (info.Type == GrenadeType.smokeGrenade ||
                 info.Type == GrenadeType.ATGrenade ||
                 info.Type == GrenadeType.phosporusGrenade))
            {
                return true;
            }

            // Modded incendiary / satchel / anti-tank throwable name fallback
            if (info.IsSatchelOrHeavyDemo ||
                (!string.IsNullOrEmpty(itemId) && itemId.IndexOf("molotov", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                return true;
            }

            if (targetVeh.IsCoverDestroyed())
                return true;

            float effArmor = GetEffectiveVehicleExplosionArmor(targetVeh);
            // Soft-skinned trucks/jeeps (< 6mm hull -> effArmor == 0) take full blast/wheel damage from frags
            if (effArmor <= 0.1f)
                return true;

            // High-penetration HE charges (>= 12mm and able to defeat effective roof/hull blast armor)
            if (info.IsValid && info.MaxPenetration >= Mathf.Max(12.0f, effArmor))
                return true;

            // Open-topped tank destroyers / halftracks / unbuttoned commanders: allow fragging exposed alive enemy crew
            if (!targetVeh.IsAirVehicle())
            {
                var seats = targetVeh.seats;
                if (seats != null)
                {
                    string myFaction = thrower != null ? AiCore.GetCachedFaction(thrower) : null;
                    int seatCount = seats.Length;
                    for (int i = 0; i < seatCount; i++)
                    {
                        Seats seat = seats[i];
                        if (seat == null || !seat.showUnitSet)
                            continue;

                        Soldier crew = seat.unitSet;
                        if (crew != null && crew.IsAlive &&
                            (string.IsNullOrEmpty(myFaction) ||
                             !AiCore.AreSameSideFactions(myFaction, AiCore.GetCachedFaction(crew))))
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        // Prevent vanilla NullReferenceException (and ~175ms exception hitch) in ResourcesManager.FactionCanDamageFaction
        // when unowned map/artillery shrapnel or projectiles from a despawned shooter hit a VehicleDamagablePart or Soldier.
        [HarmonyPatch(typeof(ResourcesManager), nameof(ResourcesManager.FactionCanDamageFaction))]
        public static class ResourcesManager_FactionCanDamageFaction_Patch
        {
            public static bool Prefix(Soldier __0, string __1, ref bool __result)
            {
                if (__0 == null || __0.Pointer == IntPtr.Zero || string.IsNullOrEmpty(__1))
                {
                    __result = true;
                    return false;
                }
                return true;
            }
        }

        [HarmonyPatch(typeof(BodyPart), nameof(BodyPart.TryDamageWithExplosion))]
        public static class BodyPart_TryDamageWithExplosion_Patch
        {
            public static bool Prefix(BodyPart __instance, float __0, Vector3 __2, Soldier __3)
            {
                long profStart = HitchProfiler.BeginModScope();
                try
                {
                    if (!VisionConfig.EnableMod.Value ||
                        __instance == null ||
                        __instance.unit == null)
                    {
                        return true;
                    }

                    Soldier victim = __instance.unit.TryCast<Soldier>();
                    if (victim != null && victim.Pointer != IntPtr.Zero && victim.IsAlive)
                    {
                        if (victim.IsOnVehicle())
                        {
                            Vehicle expVeh = victim.GetCurrentVehicle();
                            if (expVeh != null && expVeh.Pointer != IntPtr.Zero)
                                VeteranAndTacticsPatches.NotifyExplosionReactionOnVehicle(expVeh, __2, __3);
                        }
                        else
                        {
                            VeteranAndTacticsPatches.NotifyExplosionReactionOnSoldier(victim, __2, __3);
                        }
                    }

                    if (!VisionConfig.EnableRealisticVehicleArmorAndTopAttack.Value)
                    {
                        return true;
                    }

                    if (victim == null || victim.Pointer == IntPtr.Zero || !victim.IsOnVehicle() || !victim.IsAlive)
                        return true;

                    Vehicle veh = victim.GetCurrentVehicle();
                    if (veh == null || veh.Pointer == IntPtr.Zero || veh.IsAirVehicle() || veh.IsCoverDestroyed())
                        return true;

                    float effectiveArmor = GetEffectiveVehicleExplosionArmor(veh);
                    if (effectiveArmor <= 0f || __0 >= effectiveArmor)
                        return true;

                    if (!IsSoldierInEnclosedVehicleSeat(veh, victim))
                        return true;

                    // Buttoned-up crew inside intact enclosed armor survive external non-penetrating blasts
                    // with concussive suppression instead of dying inside an undamaged tank hull.
                    SyncSoldier syncher = victim.soldierSyncher;
                    if (syncher == null || syncher.OwnSyncher)
                    {
                        victim.Suppress(25);
                    }

                    EnclosedCrewExplosionHitsBlockedCount++;
                    return false;
                }
                catch { }
                finally
                {
                    HitchProfiler.EndModScope(HitchBucket.BallisticsExpl, profStart);
                }
                return true;
            }
        }

        // ============================================================================
        // 2B. MOLOTOV / INCENDIARY TANK-TRACK IMMUNITY & ENGINE-DECK FIRE SEEPAGE
        // Native x64 Root Cause in GameAssembly.dll:
        //  - VehicleDamageableTrackPart.TryDamageWithExplosion (0x18081607C) checks
        //    `if (hitType == HitType.Fire) return;`, blocking Flame.FixedUpdate (0x180866878).
        //  - However, ItemGrenade+<ExplodeAfter>d__6.MoveNext (0x18051ED9C) hardcodes
        //    HitType.Explosion (1) for ALL non-smoke grenades, including phosporusGrenade
        //    / Molotovs! On shatter, explosionMaxPenetration beats the track collider's
        //    low armorThickness and breaks steel tank tracks instantly.
        // Fix (under EnableRealisticVehicleArmorAndTopAttack):
        //  1) Reclassify incendiary / Molotov shatter blasts to HitType.Fire (2) and guard
        //     VehicleDamageableTrackPart so Molotovs NEVER snap steel tank tracks.
        //  2) Route HitType.Fire on armored MovableVehicles away from raw structural
        //     DamageHull HP loss and into realistic engine-grille / fuel-tank fire seepage
        //     when burning liquid sits on the upper hull / engine deck!
        // ============================================================================
        private static readonly List<ItemGrenade> _activeIncendiaryGrenades = new List<ItemGrenade>(16);
        internal static readonly Dictionary<IntPtr, float> _nextVehicleFireSeepageTime = new Dictionary<IntPtr, float>(32);
        private static int _lastFireExplosionFxFrame = -1;
        private static Vector3 _lastFireExplosionFxPos;

        private static bool IsIncendiaryString(string s)
        {
            if (string.IsNullOrEmpty(s))
                return false;

            return s.IndexOf("molotov", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   s.IndexOf("phosphor", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   s.IndexOf("incend", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   s.IndexOf("napalm", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   s.IndexOf("firebomb", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsIncendiaryGrenadeObject(ItemGrenade gren)
        {
            if (gren == null || gren.Pointer == IntPtr.Zero)
                return false;

            if (gren.grenadeType == GrenadeType.phosporusGrenade)
                return true;

            return IsIncendiaryString(gren.item_id) ||
                   IsIncendiaryString(gren.name) ||
                   IsIncendiaryString(gren.explosion_fx);
        }

        internal static void TryReclassifyIncendiaryGrenadeExplosion(
            Vector3 explosionPos,
            GameObject ignoreHittable,
            ref HitType hitType)
        {
            if (!VisionConfig.EnableRealisticVehicleArmorAndTopAttack.Value || hitType != HitType.Explosion)
                return;

            if (ignoreHittable != null && ignoreHittable.Pointer != IntPtr.Zero)
            {
                ItemGrenade ig = ignoreHittable.GetComponent<ItemGrenade>();
                if (ig == null)
                    ig = ignoreHittable.GetComponentInParent<ItemGrenade>();
                if (ig != null && IsIncendiaryGrenadeObject(ig))
                {
                    hitType = HitType.Fire;
                    return;
                }
            }

            if (_lastFireExplosionFxFrame == Time.frameCount &&
                (explosionPos - _lastFireExplosionFxPos).sqrMagnitude <= 1.25f)
            {
                hitType = HitType.Fire;
                return;
            }

            for (int i = _activeIncendiaryGrenades.Count - 1; i >= 0; i--)
            {
                ItemGrenade g = _activeIncendiaryGrenades[i];
                if (g == null || g.Pointer == IntPtr.Zero || g.transform == null)
                {
                    _activeIncendiaryGrenades.RemoveAt(i);
                    continue;
                }

                if ((g.transform.position - explosionPos).sqrMagnitude <= 2.25f)
                {
                    hitType = HitType.Fire;
                    _activeIncendiaryGrenades.RemoveAt(i);
                    return;
                }
            }
        }

        [HarmonyPatch(typeof(ItemGrenade), nameof(ItemGrenade.ExplodeAfter))]
        public static class ItemGrenade_ExplodeAfter_Patch
        {
            public static void Prefix(ItemGrenade __instance)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value ||
                        !VisionConfig.EnableRealisticVehicleArmorAndTopAttack.Value ||
                        !IsIncendiaryGrenadeObject(__instance))
                    {
                        return;
                    }

                    for (int i = _activeIncendiaryGrenades.Count - 1; i >= 0; i--)
                    {
                        ItemGrenade existing = _activeIncendiaryGrenades[i];
                        if (existing == null || existing.Pointer == IntPtr.Zero || existing.Pointer == __instance.Pointer)
                            _activeIncendiaryGrenades.RemoveAt(i);
                    }

                    if (_activeIncendiaryGrenades.Count >= 16)
                        _activeIncendiaryGrenades.RemoveAt(0);
                    _activeIncendiaryGrenades.Add(__instance);
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(VehicleDamageableTrackPart), nameof(VehicleDamageableTrackPart.TryDamageWithExplosion))]
        public static class VehicleDamageableTrackPart_TryDamageWithExplosion_Patch
        {
            public static bool Prefix(Vector3 __2, HitType __4)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value || !VisionConfig.EnableRealisticVehicleArmorAndTopAttack.Value)
                        return true;

                    if (__4 == HitType.Fire ||
                        (_lastFireExplosionFxFrame == Time.frameCount && (__2 - _lastFireExplosionFxPos).sqrMagnitude <= 6.25f))
                    {
                        return false;
                    }
                }
                catch { }
                return true;
            }
        }

        [HarmonyPatch(typeof(VehicleDamagablePart), nameof(VehicleDamagablePart.TryDamageWithExplosion))]
        public static class VehicleDamagablePart_TryDamageWithExplosion_Patch
        {
            public static bool Prefix(
                VehicleDamagablePart __instance,
                float __0,
                float __1,
                Vector3 __2,
                Soldier __3,
                HitType __4)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value ||
                        __instance == null ||
                        __instance.Pointer == IntPtr.Zero)
                    {
                        return true;
                    }

                    Vehicle expRootVeh = __instance.rootVeh;
                    if (expRootVeh == null || expRootVeh.Pointer == IntPtr.Zero)
                        expRootVeh = __instance.GetRootVehicle();
                    if (expRootVeh != null && expRootVeh.Pointer != IntPtr.Zero)
                    {
                        VeteranAndTacticsPatches.NotifyExplosionReactionOnVehicle(expRootVeh, __2, __3);
                    }

                    if (!VisionConfig.EnableRealisticVehicleArmorAndTopAttack.Value)
                    {
                        return true;
                    }

                    // Vanilla Bug Fix: VehicleDamagableDetachablePart (1mm-5mm external fenders, side skirts,
                    // stowage boxes, spare wheels) overrides TryPenetrateArmor so bullets only detach the
                    // external part, but forgot to override TryDamageWithExplosion! Without this guard, ANY
                    // nearby blast with maxPenetration > 1mm hits a detachable fender and runs base
                    // VehicleDamagablePart.TryDamageWithExplosion, dealing full DamageHull + DamageEngine +
                    // DamageFuelTank through the tank's heavy armor!
                    VehicleDamagableDetachablePart detachable = __instance.TryCast<VehicleDamagableDetachablePart>();
                    if (detachable != null)
                    {
                        if (detachable.IsDetached() || __4 == HitType.Fire)
                            return false;

                        Vehicle detRoot = __instance.GetRootVehicle();
                        if (detRoot != null &&
                            detRoot.Pointer != IntPtr.Zero &&
                            !detRoot.IsAirVehicle() &&
                            detRoot.IsPhysicsEnabled &&
                            detRoot.isActiveAndEnabled &&
                            __0 > __instance.armorThickness &&
                            AiCore.IsLocallyOwnedVehicle(detRoot))
                        {
                            string shooterFac = (__3 != null && __3.Pointer != IntPtr.Zero) ? __3.faction : null;
                            if (BodyPart.AllowDamage(shooterFac, detRoot.GetVehicleFaction()))
                            {
                                detachable.Damage(__1);
                            }
                        }
                        return false;
                    }

                    if (__4 != HitType.Fire)
                        return true;

                    // Steel tank tracks never take fire damage
                    if (__instance.TryCast<VehicleDamageableTrackPart>() != null)
                        return false;

                    Vehicle rootVeh = __instance.GetRootVehicle();
                    MovableVehicle movVeh = rootVeh != null ? rootVeh.TryCast<MovableVehicle>() : null;
                    if (movVeh == null || rootVeh.IsAirVehicle())
                        return true;

                    // Unarmored soft-skinned trucks/jeeps (< 6mm hull -> effArmor <= 0.1f) burn normally
                    float effArmor = GetEffectiveVehicleExplosionArmor(rootVeh);
                    if (effArmor <= 0.1f)
                        return true;

                    // Armored vehicle hit by Fire: block raw structural HE hull damage, and on the owning peer
                    // apply realistic engine-grille / fuel-tank fire seepage when burning liquid sits on the deck!
                    if (!rootVeh.IsDisabled() && AiCore.IsLocallyOwnedVehicle(rootVeh))
                    {
                        if (__3 != null &&
                            __3.Pointer != IntPtr.Zero &&
                            !ResourcesManager.FactionCanDamageFaction(__3, __instance.GetFaction()))
                        {
                            return false;
                        }

                        IntPtr vehPtr = rootVeh.Pointer;
                        float now = Time.time;
                        if (!_nextVehicleFireSeepageTime.TryGetValue(vehPtr, out float nextTick) || now >= nextTick)
                        {
                            Transform vehTr = movVeh.transform;
                            if (vehTr != null)
                            {
                                Vector3 localFire = vehTr.InverseTransformPoint(__2);
                                Vector3 localCenter = vehTr.InverseTransformPoint(movVeh.GetCenterOfUnit());

                                // Burning fuel must sit at or above mid-hull / upper deck (not beside lower roadwheels)
                                if (localFire.y >= localCenter.y - 0.25f)
                                {
                                    bool overEngineDeck;
                                    AudioSource engSrc = movVeh.engine_source;
                                    if (engSrc != null && engSrc.transform != null)
                                    {
                                        Vector3 localEng = vehTr.InverseTransformPoint(engSrc.transform.position);
                                        float dx = localFire.x - localEng.x;
                                        float dz = localFire.z - localEng.z;
                                        overEngineDeck = (dx * dx + dz * dz) <= (2.65f * 2.65f);
                                    }
                                    else
                                    {
                                        overEngineDeck = localFire.z <= localCenter.z + 1.15f;
                                    }

                                    if (overEngineDeck)
                                    {
                                        _nextVehicleFireSeepageTime[vehPtr] = now + 0.45f;
                                        short engDam = (short)Mathf.Clamp(Mathf.RoundToInt(Mathf.Max(15f, __1 * 0.95f)), 12, 26);
                                        short fuelDam = (short)Mathf.Clamp(Mathf.RoundToInt(Mathf.Max(9f, __1 * 0.60f)), 8, 18);
                                        movVeh.DamageEngine(engDam);
                                        movVeh.DamageFuelTank(fuelDam);
                                        rootVeh.OnVehicleSuppressed();
                                    }
                                }
                            }
                        }
                    }

                    return false;
                }
                catch { }
                return true;
            }
        }

        // ============================================================================
        // 2C. TANK COLLISION HULL-DAMAGE & FALSE "HULL DAMAGED" POPUP FIX
        // Fixes three vanilla collision bugs in VehicleWithWheels.OnCollisionEnter
        // (0x18087B730) and VehicleDamagablePart.OnVehicleCollision (0x180814400):
        //  1) Detachable fenders/skirts/stowage boxes (VehicleDamagableDetachablePart)
        //     have `damageHullOnGettingRammed = true` (0x18081387E) and keep `rootVeh`
        //     pointing to the tank even after Detach() adds a Rigidbody to them, causing
        //     collisions with fenders/stowage or self-parts to call `rootVeh.DamageHull`!
        //  2) Armored vehicles/tanks take direct hull HP damage when bumped by light
        //     vehicles/props (`OnVehicleCollision` ignores armorThickness and mass ratio).
        //  3) When a tank crushes a static emplacement/tent gun (`rootVeh.IsStaticVehicle()`),
        //     `VehicleWithWheels.OnCollisionEnter` (0x18087C14B) checks `this.IsStaticVehicle()`
        //     (false on the player's tank) instead of `rootVeh.IsStaticVehicle()`, popping up
        //     "Hull damaged" / "Hull destroyed" on the tank driver's screen!
        // ============================================================================
        private static IntPtr _activeCollidingVehiclePtr;
        private static bool _activeCollisionTargetIsStaticVehicle;

        [HarmonyPatch(typeof(VehicleWithWheels), nameof(VehicleWithWheels.OnCollisionEnter))]
        public static class VehicleWithWheels_OnCollisionEnter_Patch
        {
            public static void Prefix(VehicleWithWheels __instance, Collision __0)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value || __instance == null || __instance.Pointer == IntPtr.Zero)
                        return;

                    _activeCollidingVehiclePtr = __instance.Pointer;
                    _activeCollisionTargetIsStaticVehicle = false;

                    if (__0 != null && __0.Pointer != IntPtr.Zero)
                    {
                        GameObject go = __0.gameObject;
                        if (go != null && go.Pointer != IntPtr.Zero)
                        {
                            VehicleDamagablePart part = go.GetComponent<VehicleDamagablePart>();
                            if (part != null && part.Pointer != IntPtr.Zero)
                            {
                                Vehicle targetVeh = part.rootVeh;
                                if (targetVeh != null && targetVeh.Pointer != IntPtr.Zero && targetVeh.IsStatic())
                                {
                                    _activeCollisionTargetIsStaticVehicle = true;
                                }
                            }
                        }
                    }
                }
                catch { }
            }

            public static void Finalizer()
            {
                _activeCollidingVehiclePtr = IntPtr.Zero;
                _activeCollisionTargetIsStaticVehicle = false;
            }
        }

        [HarmonyPatch(typeof(VehicleDamagablePart), nameof(VehicleDamagablePart.OnVehicleCollision))]
        public static class VehicleDamagablePart_OnVehicleCollision_Patch
        {
            public static bool Prefix(
                VehicleDamagablePart __instance,
                float __0,
                float __1,
                ref bool __result)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value || __instance == null || __instance.Pointer == IntPtr.Zero)
                        return true;

                    Vehicle rootVeh = __instance.rootVeh;
                    if (rootVeh == null || rootVeh.Pointer == IntPtr.Zero)
                    {
                        __result = false;
                        return false;
                    }

                    // 1. Never allow a vehicle to ram its own child or detached VehicleDamagablePart collider
                    if (_activeCollidingVehiclePtr != IntPtr.Zero && rootVeh.Pointer == _activeCollidingVehiclePtr)
                    {
                        __result = false;
                        return false;
                    }

                    // 2. Collisions with external detachable parts (fenders, side skirts, stowage boxes)
                    // should only damage/detach the external part, NEVER the main vehicle hull!
                    VehicleDamagableDetachablePart detachable = __instance.TryCast<VehicleDamagableDetachablePart>();
                    if (detachable != null)
                    {
                        if (!detachable.IsDetached() &&
                            __1 > 4.0f &&
                            !rootVeh.IsDisabled() &&
                            AiCore.IsLocallyOwnedVehicle(rootVeh))
                        {
                            detachable.Damage((__0 * __1) * 0.01f);
                        }
                        __result = false;
                        return false;
                    }

                    // 3. Armored vehicles and tanks should never take hull damage from light collisions
                    // (e.g. bumping a motorcycle, jeep, wheeled gun cart, or light prop).
                    bool isTank = rootVeh.TryCast<VehicleTank>() != null;
                    float effArmor = GetEffectiveVehicleExplosionArmor(rootVeh);
                    if (isTank || effArmor >= 8.0f || __instance.armorThickness >= 15.0f)
                    {
                        MovableVehicle mov = rootVeh.TryCast<MovableVehicle>();
                        Rigidbody ownRb = mov != null ? mov.rb : null;
                        float ownMass = (ownRb != null && ownRb.Pointer != IntPtr.Zero) ? Mathf.Max(4000f, ownRb.mass) : 12000f;

                        // Require a heavy vehicle collision (>= 45% of the armored vehicle's mass and >= 3500 kg)
                        // at >= 6.5 m/s (~23.4 km/h) before an armored hull can suffer structural ram damage.
                        if (__0 < Mathf.Max(3500f, ownMass * 0.45f) || __1 < 6.5f)
                        {
                            __result = false;
                            return false;
                        }
                    }
                }
                catch { }
                return true;
            }
        }

        [HarmonyPatch(typeof(VehicleDamagablePart), nameof(VehicleDamagablePart.InternalPartToDamageText))]
        public static class VehicleDamagablePart_InternalPartToDamageText_Patch
        {
            public static void Prefix(ref bool __1)
            {
                if (_activeCollidingVehiclePtr != IntPtr.Zero && _activeCollisionTargetIsStaticVehicle)
                {
                    __1 = true;
                }
            }
        }

        // ============================================================================
        // 3. PARACHUTE WIND DRIFT & SLOWER DESCENT
        // Softens paratrooper vertical descent speed by ~25% and adds smooth horizontal
        // canopy wind drift while high above ground (> 18m AGL), tapering off cleanly
        // before touchdown so paratroopers land safely on target.
        // ============================================================================
        [HarmonyPatch(typeof(VehicleParachute), nameof(VehicleParachute.MoveDown))]
        public static class VehicleParachute_MoveDown_Patch
        {
            public static void Prefix(VehicleParachute __instance)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value || !VisionConfig.EnableParachuteWindDrift.Value)
                        return;

                    if (__instance == null || __instance.Pointer == IntPtr.Zero || __instance.transform == null)
                        return;

                    IntPtr ptr = __instance.Pointer;
                    if (!_parachuteDriftStates.TryGetValue(ptr, out ParachuteDriftState state))
                    {
                        uint h = (uint)ptr.ToInt64();
                        float seed = ((h ^ (h >> 11)) & 0xFFu) * 0.08f;
                        state = new ParachuteDriftState
                        {
                            NextGroundCheckTime = 0f,
                            IsHighAboveGround = true,
                            PhaseSeed = seed
                        };
                        ParachutesDriftedCount++;
                    }

                    state.BeforeMoveDownPos = __instance.transform.position;
                    _parachuteDriftStates[ptr] = state;
                }
                catch { }
            }

            public static void Postfix(VehicleParachute __instance)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value || !VisionConfig.EnableParachuteWindDrift.Value)
                        return;

                    if (__instance == null || __instance.Pointer == IntPtr.Zero || __instance.transform == null)
                        return;

                    if (!AiCore.IsLocallyOwnedVehicle(__instance))
                        return;

                    IntPtr ptr = __instance.Pointer;
                    if (!_parachuteDriftStates.TryGetValue(ptr, out ParachuteDriftState state))
                        return;

                    if (state.BeforeMoveDownPos.sqrMagnitude < 0.001f)
                        return;

                    float now = Time.time;
                    Transform tr = __instance.transform;
                    Vector3 posAfter = tr.position;
                    Vector3 delta = posAfter - state.BeforeMoveDownPos;

                    if (now >= state.NextGroundCheckTime)
                    {
                        state.NextGroundCheckTime = now + 0.45f;
                        state.IsHighAboveGround = !AiCore.RaycastEnvironment(posAfter, Vector3.down, 18.0f, null, out _);
                    }
                    _parachuteDriftStates[ptr] = state;

                    bool modified = false;

                    // 1. Soften direct transform descent step by 25%
                    if (delta.y < -0.0005f)
                    {
                        posAfter.y -= delta.y * 0.25f;
                        modified = true;
                    }

                    // 2. Apply smooth horizontal wind drift while airborne (> 18m AGL)
                    if (state.IsHighAboveGround)
                    {
                        float dt = Mathf.Clamp(Time.fixedDeltaTime, 0.005f, 0.05f);
                        float windPhase = now * 0.35f + state.PhaseSeed;
                        Vector3 windVel = new Vector3(
                            Mathf.Sin(windPhase) * 1.15f + 0.45f,
                            0f,
                            Mathf.Cos(windPhase * 0.73f) * 0.95f);

                        posAfter += windVel * dt;
                        modified = true;
                    }

                    if (modified)
                        tr.position = posAfter;

                    // 3. Also soften Rigidbody downward velocity if MoveDown uses physics velocity
                    Rigidbody rb = __instance.rb;
                    if (rb != null && !rb.isKinematic)
                    {
                        Vector3 vel = rb.velocity;
                        if (vel.y < -2.0f)
                        {
                            vel.y *= 0.82f;
                            rb.velocity = vel;
                        }
                    }
                }
                catch { }
            }
        }
    }
}
