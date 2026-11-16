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

        public static int PlaneAudioLoopsDesyncedCount { get; private set; }
        public static int ThinCoverPenetrationsCount { get; private set; }
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
                _modifiedBloodRenderers.Clear();
            }
            if (forceClear || _nextVehicleFireSeepageTime.Count > 64)
                _nextVehicleFireSeepageTime.Clear();
            if (forceClear)
            {
                _activeIncendiaryGrenades.Clear();
                ResetFirstPersonShadowRigOnSceneChange();
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

                    AudioSource src = __instance.engine_source;
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
                                    float targetTime = ((state.PhaseOffset + Time.unscaledTime * 0.29f) % 0.82f + 0.08f) * len;
                                    src.time = Mathf.Clamp(targetTime, 0.05f, len - 0.05f);
                                    PlaneAudioLoopsDesyncedCount++;
                                    AiCore.LogDebug("PlaneAudio", $"Desynchronized plane motor loop '{clip.name}' to {src.time:F2}s/{len:F2}s (detune={state.PitchDetune:+0.000;-0.000}).", 8.0f);
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
                    Rigidbody rb = __instance.rb;
                    if (rb != null)
                    {
                        float vy = rb.velocity.y;
                        flightLoadPitch += Mathf.Clamp(-vy * 0.0022f, -0.045f, 0.055f);
                    }
                    flightLoadPitch += Mathf.Clamp(Mathf.Abs(__instance.rollAngle) * 0.0004f, 0f, 0.02f);

                    float t = Time.unscaledTime;
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

            public static bool Prefix(Soldier __instance, VoiceManager.VoiceClip __0)
            {
                if (ReferenceEquals(__instance, null) || __instance == null || __instance.Pointer == IntPtr.Zero)
                    return false;

                if (__instance.IsDead && (int)__0 != DeathVoiceClipId)
                    return false;

                return true;
            }
        }

        [HarmonyPatch(typeof(Soldier), nameof(Soldier.SyncSay))]
        public static class Soldier_SyncSay_Patch
        {
            private const int DeathVoiceClipId = 4;

            public static bool Prefix(Soldier __instance, VoiceManager.VoiceClip __0)
            {
                if (ReferenceEquals(__instance, null) || __instance == null || __instance.Pointer == IntPtr.Zero)
                    return false;

                if (__instance.IsDead && (int)__0 != DeathVoiceClipId)
                    return false;

                SyncSoldier syncher = __instance.GetSyncher();
                if (ReferenceEquals(syncher, null) || syncher == null || syncher.Pointer == IntPtr.Zero)
                    return false;

                return true;
            }
        }

        [HarmonyPatch(typeof(BulletInstance), nameof(BulletInstance.OnHit))]
        public static class BulletInstance_OnHit_Patch
        {
            private const int ImpactUnknownOrBlocked = -1;

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
                                if (!__instance.canDamage &&
                                    !bulletData.IsHe() &&
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
                                        shooter.IsPlayer() &&
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
                long profStart = HitchProfiler.Begin();
                try
                {
                    if (__state && __instance != null)
                        __instance.canDamage = false;

                    VeteranAndTacticsPatches.EndProjectileImpactContext();

                    if (__state)
                        return;

                    if (!VisionConfig.EnableMod.Value || !VisionConfig.EnableThinCoverBulletPenetration.Value)
                        return;

                    // Penetrated continuation bullets are spawned with canRicochet = false,
                    // which guarantees each projectile can penetrate thin cover at most once.
                    if (__instance == null ||
                        !__instance.canRicochet ||
                        bulletData == null ||
                        ReferenceEquals(__instance.shooter, null) ||
                        __instance.shooter.Pointer == IntPtr.Zero)
                    {
                        return;
                    }

                    if (impactSpeed < 220f || bulletData.mmPenetration < 6)
                        return;

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

                    bool penCanDamage = __instance.canDamage;
                    if (!penCanDamage &&
                        AiCore.IsInMultiplayerRoom() &&
                        AiCore.IsMasterClient() &&
                        __instance.shooter != null &&
                        __instance.shooter.Pointer != IntPtr.Zero &&
                        __instance.shooter.IsPlayer() &&
                        !AiCore.IsPlayerRunningBattleHardened(__instance.shooter))
                    {
                        // Unmodded vanilla client's own bullet stopped on the front face of the wooden cover (0% double-damage risk);
                        // promote Host's penetrated continuation round to authoritative damage for two-way fairness!
                        penCanDamage = true;
                        HostVanillaClientApAssistsCount++;
                    }

                    var penetratedBullet = new BulletInstance(
                        exitPos,
                        exitDir,
                        exitSpeed,
                        bulletData,
                        __instance.shooter,
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

                    ThinCoverPenetrationsCount++;
                    AiCore.LogDebug("Ballistics", $"Bullet penetrated {thickness:F2}m {impactType} cover (in={impactSpeed:F0}m/s -> out={exitSpeed:F0}m/s, pen={bulletData.mmPenetration}mm).", 5.0f);
                }
                catch { }
                finally
                {
                    HitchProfiler.End(HitchBucket.BallisticsExpl, profStart);
                }
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
                    // Never treat terrain, vehicles, or living creatures as thin static cover props
                    if (AiCore.IsTerrainCollider(col))
                        return ImpactUnknownOrBlocked;
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

                    // Case 1: Intact airplane fuselage hit by small-arms or heavy machine gun (< 18mm, non-HE, non-Flak).
                    if (targetIsAir)
                    {
                        if (!rootVeh.IsCoverDestroyed() &&
                            !rootVeh.IsDisabled() &&
                            !__0.IsHe() &&
                            !__0.IsFlak() &&
                            __0.caliber < 18f)
                        {
                            // .50 cal / HMG (12.5mm-17.9mm) hitting directly within 0.42m of a seated crewman deals scaled spall;
                            // rifle-caliber (< 12.5mm) or fuselage/wing hits away from the cockpit only damage aircraft HP.
                            if (__0.caliber >= 12.5f && IsImpactNearSeatedAirCrew(rootVeh, __2, 0.42f * 0.42f))
                            {
                                ActiveCrewSpallContext = true;
                                ActiveCrewSpallMultiplier = 0.28f;
                                ActiveCrewSpallMaxDamage = 34.0f;
                                __state.ModifiedCrewSpall = true;
                            }
                            else
                            {
                                ActiveCrewSpallContext = true;
                                ActiveCrewSpallMultiplier = 0.0f;
                                ActiveCrewSpallMaxDamage = 0.0f;
                                __state.ModifiedCrewSpall = true;
                                AirplanePilotMgHitsBlockedCount++;
                            }
                        }
                        return;
                    }

                    float basePlateArmor = __instance.armorThickness;

                    // Case 2: Ground vehicle / tank hit by an aircraft AP weapon or steep top-down dive attack.
                    if (__0.mmPenetration >= 12 && basePlateArmor > 8f)
                    {
                        bool isAirOrTopAttack = ((__1.y - __2.y) >= 20.0f && __3.y <= -0.15f);
                        if (!isAirOrTopAttack && __5 != null && __5.Pointer != IntPtr.Zero && __5.IsOnVehicle())
                        {
                            Vehicle shooterVeh = __5.GetCurrentVehicle();
                            if (shooterVeh != null && shooterVeh.Pointer != IntPtr.Zero && shooterVeh.IsAirVehicle())
                                isAirOrTopAttack = true;
                        }

                        if (isAirOrTopAttack)
                        {
                            __state.SavedArmorThickness = basePlateArmor;
                            __state.ModifiedArmorThickness = true;

                            // Normalize obliquity for top-deck roof strikes and scale hull/turret box armor
                            // to reflect thin WWII tank roof/engine-deck plating (12mm-25mm) + dive velocity energy.
                            if (__4 > 15.0f)
                                __4 = 15.0f;

                            __instance.armorThickness = Mathf.Max(8.0f, basePlateArmor * 0.45f);
                            AircraftTopAttackPenetrationsAssistedCount++;
                        }
                    }

                    // Case 3: Solid kinetic AP spall scaling inside armored ground vehicles (>= 8mm hull/turret armor).
                    // AT rifles / autocannons (< 25mm) and light solid AP (25mm-57mm) wound/incapacitate individual
                    // crewmen along the penetration path instead of rolling vanilla's full 150 HP wipe, while 58mm+
                    // tank/AT guns and all HE/APHE/HEAT bursts retain full 1.0x crew lethality.
                    if (rootVeh != null &&
                        rootVeh.Pointer != IntPtr.Zero &&
                        !rootVeh.IsCoverDestroyed() &&
                        !__0.IsHe() &&
                        !__0.IsFlak() &&
                        basePlateArmor >= 8.0f)
                    {
                        if (__0.caliber < 25.0f)
                        {
                            ActiveCrewSpallContext = true;
                            ActiveCrewSpallMultiplier = 0.40f;
                            ActiveCrewSpallMaxDamage = 45.0f;
                            __state.ModifiedCrewSpall = true;
                        }
                        else if (__0.caliber < 58.0f)
                        {
                            ActiveCrewSpallContext = true;
                            ActiveCrewSpallMultiplier = 0.72f;
                            ActiveCrewSpallMaxDamage = 95.0f;
                            __state.ModifiedCrewSpall = true;
                        }
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

            public static void Postfix(Soldier __instance, bool __state)
            {
                if (!__state || !VisionConfig.EnableMod.Value)
                    return;

                try
                {
                    if (!ReferenceEquals(__instance, null) &&
                        __instance != null &&
                        __instance.Pointer != IntPtr.Zero &&
                        !__instance.IsAlive)
                    {
                        NotifySquadMemberKilled(__instance);
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
                        p.TryCast<VehicleDamageableTrackPart>() != null)
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
            public static bool Prefix(BodyPart __instance, float __0)
            {
                long profStart = HitchProfiler.Begin();
                try
                {
                    if (!VisionConfig.EnableMod.Value ||
                        !VisionConfig.EnableRealisticVehicleArmorAndTopAttack.Value ||
                        __instance == null ||
                        __instance.unit == null)
                    {
                        return true;
                    }

                    Soldier victim = __instance.unit.TryCast<Soldier>();
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
                    HitchProfiler.End(HitchBucket.BallisticsExpl, profStart);
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
                float __1,
                Vector3 __2,
                Soldier __3,
                HitType __4)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value ||
                        !VisionConfig.EnableRealisticVehicleArmorAndTopAttack.Value ||
                        __4 != HitType.Fire ||
                        __instance == null ||
                        __instance.Pointer == IntPtr.Zero)
                    {
                        return true;
                    }

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
