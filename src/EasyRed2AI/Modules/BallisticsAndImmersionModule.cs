using System;
using System.Collections.Generic;
using Corvostudio.CinematicCamera;
using Corvostudio.Weapons;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;

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
        [HarmonyPatch(typeof(BulletInstance), nameof(BulletInstance.OnHit))]
        public static class BulletInstance_OnHit_Patch
        {
            private const int ImpactUnknownOrBlocked = -1;

            public static void Prefix(
                BulletInstance __instance,
                Vector3 hitDirection,
                BulletData bulletData,
                RaycastHit hit,
                out bool __state)
            {
                __state = false;
                VeteranAndTacticsPatches.BeginProjectileImpactContext(__instance, bulletData, hitDirection);

                try
                {
                    if (__instance != null)
                    {
                        // Guard against vanilla BulletInstance.OnHit -> Soldier.SyncSay -> Creature.GetSyncher NRE
                        // when a long-flight artillery/mortar shooter died & despawned while the shell was in mid-air.
                        Soldier rawShooter = __instance.shooter;
                        if (!ReferenceEquals(rawShooter, null) &&
                            (rawShooter == null || rawShooter.Pointer == IntPtr.Zero || rawShooter.gameObject == null))
                        {
                            __instance.shooter = null;
                        }
                    }

                    if (!VisionConfig.EnableMod.Value || __instance == null || bulletData == null)
                        return;

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
            }

            public static void Postfix(
                BulletInstance __instance,
                Vector3 hitDirection,
                float impactSpeed,
                BulletData bulletData,
                RaycastHit hit,
                bool __state)
            {
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
                    if (__instance == null || !__instance.canRicochet || bulletData == null)
                        return;

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

                    var penetratedBullet = new BulletInstance(
                        exitPos,
                        exitDir,
                        exitSpeed,
                        bulletData,
                        __instance.shooter,
                        false, // Prevents a penetrated round from penetrating or ricocheting a second time
                        __instance.canDamage,
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
            }

            private static int ClassifyColliderImpactType(Collider col)
            {
                try
                {
                    // Never treat terrain, vehicles, or living creatures as thin static cover props
                    if (col.GetComponent<TerrainCollider>() != null)
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
                    if (!VisionConfig.EnableMod.Value ||
                        !VisionConfig.EnableRealisticVehicleArmorAndTopAttack.Value ||
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

            public static void Postfix(VehicleDamagablePart __instance, ArmorPenetrationPatchState __state)
            {
                if (__state.ModifiedCrewSpall)
                {
                    ActiveCrewSpallContext = false;
                    ActiveCrewSpallMultiplier = 1.0f;
                    ActiveCrewSpallMaxDamage = 9999f;
                }

                if (__state.ModifiedArmorThickness && __instance != null && __instance.Pointer != IntPtr.Zero)
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
            public static bool Prefix(ref float __0)
            {
                if (!ActiveCrewSpallContext)
                    return true;

                if (ActiveCrewSpallMultiplier <= 0.001f)
                    return false;

                __0 = Mathf.Min(__0 * ActiveCrewSpallMultiplier, ActiveCrewSpallMaxDamage);
                return __0 > 0.05f;
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

        // ============================================================================
        // 4. SPECTATOR / FREE-CAMERA HUD RETENTION
        // Keeps the player HUD and 3D tactical markers visible when entering Free /
        // Cinematic Camera (F) until the user explicitly presses the Cinematic Camera
        // Hide/Show GUI toggle.
        // ============================================================================
        public static void MaintainSpectatorHudVisibility()
        {
            if (!VisionConfig.EnableMod.Value || !VisionConfig.EnableSpectatorHudRetention.Value)
                return;

            if (CinematicCameraGUI.instance == null || !CinematicCameraGUI.cinematicCameraEnabled)
                return;

            bool wantHudVisible = CinematicCameraGUI.GUIVisible();
            PlayerGUI playerGui = PlayerGUI.instance;
            if (playerGui != null && playerGui.gui_obj != null)
            {
                if (playerGui.gui_obj.activeSelf != wantHudVisible)
                    playerGui.gui_obj.SetActive(wantHudVisible);
            }
        }

        [HarmonyPatch(typeof(CinematicCameraGUI), nameof(CinematicCameraGUI.EnableCinematicCamera))]
        public static class CinematicCameraGUI_EnableCinematicCamera_Patch
        {
            public static void Postfix()
            {
                try
                {
                    MaintainSpectatorHudVisibility();
                }
                catch { }
            }
        }

        // ============================================================================
        // 5. VANILLA VISUAL FIX: FIRST-PERSON SELF-SHADOW FIX
        // In vanilla First Person:
        //  1) SoldierLodManager.SetBodyHeadVisibility (0x18079C040) swaps the player's
        //     head submesh materials to ResourcesManager.hideMaterial (which casts ZERO shadow)
        //     while ItemHelmet.SetHidden() uses ShadowCastingMode.ShadowsOnly—producing a
        //     headless torso shadow with a floating helmet hovering over an empty neck!
        //  2) FPSGunManager leaves shadowCastingMode = On on the disembodied camera viewmodel
        //     arms (hands_mesh, uniform_mesh, and FPS handheld weapon renderers), casting a
        //     second giant pair of floating arms on top of the 3P body shadow!
        // Fix (Local Player Only — Zero Bot Overhead):
        //  - Turn ShadowCastingMode.Off on FPSGunManager viewmodel renderers.
        //  - When !showHead (First Person), set bodyMesh.shadowCastingMode = Off (keeping
        //    hideMaterial so the head never clips into the FPS camera) and drive a cached
        //    ShadowsOnly child SkinnedMeshRenderer with full FaceData materials so 1st-person
        //    shadows match 3rd-person!
        // ============================================================================
        private static readonly Dictionary<IntPtr, SkinnedMeshRenderer> _fpsHeadShadowProxies = new Dictionary<IntPtr, SkinnedMeshRenderer>(4);

        private static void ApplyFpsViewmodelShadowMode(FPSGunManager mgr)
        {
            if (mgr == null || mgr.Pointer == IntPtr.Zero)
                return;

            var mode = (VisionConfig.EnableMod.Value && VisionConfig.EnableFirstPersonShadowFix.Value)
                ? UnityEngine.Rendering.ShadowCastingMode.Off
                : UnityEngine.Rendering.ShadowCastingMode.On;

            SkinnedMeshRenderer hands = mgr.hands_mesh;
            if (hands != null)
                hands.shadowCastingMode = mode;

            SkinnedMeshRenderer uni = mgr.uniform_mesh;
            if (uni != null)
                uni.shadowCastingMode = mode;

            SkinnedMeshRenderer cachedUni = mgr.cachedUniformMesh;
            if (cachedUni != null)
                cachedUni.shadowCastingMode = mode;

            Transform wJnt = mgr.WeaponJNT;
            if (wJnt != null)
            {
                var renderers = wJnt.GetComponentsInChildren<Renderer>(true);
                if (renderers != null)
                {
                    for (int i = 0; i < renderers.Length; i++)
                    {
                        if (renderers[i] != null)
                            renderers[i].shadowCastingMode = mode;
                    }
                }
            }

            Transform wJntGen = mgr.WeaponJNT_generic;
            if (wJntGen != null)
            {
                var renderers = wJntGen.GetComponentsInChildren<Renderer>(true);
                if (renderers != null)
                {
                    for (int i = 0; i < renderers.Length; i++)
                    {
                        if (renderers[i] != null)
                            renderers[i].shadowCastingMode = mode;
                    }
                }
            }
        }

        [HarmonyPatch(typeof(FPSGunManager), nameof(FPSGunManager.RefreshSkin))]
        public static class FPSGunManager_RefreshSkin_Patch
        {
            public static void Postfix(FPSGunManager __instance)
            {
                try
                {
                    ApplyFpsViewmodelShadowMode(__instance);
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(FPSGunManager), nameof(FPSGunManager.UpdateItemInFPSHands))]
        public static class FPSGunManager_UpdateItemInFPSHands_Patch
        {
            public static void Postfix(FPSGunManager __instance)
            {
                try
                {
                    ApplyFpsViewmodelShadowMode(__instance);
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(SoldierLodManager), nameof(SoldierLodManager.ApplyBodyHeadVisibility))]
        public static class SoldierLodManager_ApplyBodyHeadVisibility_Patch
        {
            public static void Postfix(
                SoldierLodManager __instance,
                Corvostudio.EasyRed2.Characters.FaceData __0,
                bool __1,
                bool __2)
            {
                try
                {
                    if (__instance == null || __instance.Pointer == IntPtr.Zero)
                        return;

                    SkinnedMeshRenderer bodyMesh = __instance.bodyMesh;
                    if (bodyMesh == null || bodyMesh.Pointer == IntPtr.Zero || bodyMesh.transform == null)
                        return;

                    IntPtr lodPtr = __instance.Pointer;
                    bool wantFpsShadowProxy = VisionConfig.EnableMod.Value &&
                                              VisionConfig.EnableFirstPersonShadowFix.Value &&
                                              __1 &&  // showBody == true
                                              !__2 && // showHead == false (First-Person camera!)
                                              __0 != null &&
                                              __0.materials != null;

                    if (!wantFpsShadowProxy)
                    {
                        bodyMesh.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                        if (_fpsHeadShadowProxies.TryGetValue(lodPtr, out SkinnedMeshRenderer existingProxy) &&
                            existingProxy != null && existingProxy.Pointer != IntPtr.Zero)
                        {
                            existingProxy.enabled = false;
                        }
                        return;
                    }

                    if (!_fpsHeadShadowProxies.TryGetValue(lodPtr, out SkinnedMeshRenderer proxy) ||
                        proxy == null ||
                        proxy.Pointer == IntPtr.Zero)
                    {
                        if (_fpsHeadShadowProxies.Count > 16)
                            _fpsHeadShadowProxies.Clear();

                        GameObject proxyGo = new GameObject("BH_FpsHeadShadowProxy");
                        proxyGo.transform.SetParent(bodyMesh.transform, false);
                        proxyGo.layer = bodyMesh.gameObject.layer;
                        proxy = proxyGo.AddComponent<SkinnedMeshRenderer>();
                        _fpsHeadShadowProxies[lodPtr] = proxy;
                    }

                    proxy.sharedMesh = bodyMesh.sharedMesh;
                    proxy.bones = bodyMesh.bones;
                    proxy.rootBone = bodyMesh.rootBone;
                    proxy.localBounds = bodyMesh.localBounds;
                    proxy.updateWhenOffscreen = bodyMesh.updateWhenOffscreen;
                    proxy.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly;
                    proxy.receiveShadows = false;
                    SoldierLodManager.SetBodyHeadVisibility(proxy, __0.materials, 0, true, true);
                    proxy.enabled = true;

                    // Prevent duplicate body shadow from bodyMesh while keeping its visible body submesh + hidden head submesh intact
                    bodyMesh.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                }
                catch { }
            }
        }

        // ============================================================================
        // 6. VANILLA VISUAL FIX: SMOKE & WATER BLOOD SPRAY OCCLUSION FIX
        // Eliminates disembodied "ghost" blood sprays floating on top of smoke or water:
        //  1) Underwater & Cross-Water Hits: BulletInstance.OnHit sets onWater = true
        //     only when hitting Layer 4 (Water), so bullets hitting submerged/wading
        //     Soldier BodyPart colliders spawn airborne "ImpactFX_flesh" underwater.
        //  2) Tactical Smoke Volumes: ResourcesManager.PlayImpactEffect checks camera
        //     frustum (PointInCameraView) without checking smoke-collider LOS, causing
        //     red blood clouds to pop over opaque smoke screens. Swapping to vanilla's
        //     pooled "ImpactFX_flesh_noBlood" preserves 100% of bullet-flesh impact audio.
        //  3) Ambient Map Smoke & Water Depth Sorting: Normalizes pooled ImpactFX_flesh
        //     ParticleSystemRenderers once per pooled instance (clamping sortingOrder <= 0,
        //     renderQueue <= 3000, and sortingFudge >= +6.5m) so colliderless Stalingrad
        //     building/rubble/vehicle smoke and water transparencies sort over blood mist.
        // ============================================================================
        private struct OriginalBloodRendererState
        {
            public ParticleSystemRenderer Renderer;
            public int SortingOrder;
            public float SortingFudge;
            public Material SharedMat;
            public int RenderQueue;
        }

        private static readonly HashSet<IntPtr> _inspectedBloodFxRoots = new HashSet<IntPtr>(64);
        private static readonly List<OriginalBloodRendererState> _modifiedBloodRenderers = new List<OriginalBloodRendererState>(32);
        private static Il2CppSystem.Action<GameObject> _cachedBloodFxCallback;
        private static bool _loggedBloodRendererNormalization;

        private static Il2CppSystem.Action<GameObject> GetOrCreateBloodFxCallback()
        {
            if (_cachedBloodFxCallback == null)
            {
                _cachedBloodFxCallback = Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<Il2CppSystem.Action<GameObject>>(
                    new Action<GameObject>(OnBloodImpactFxSpawned));
            }
            return _cachedBloodFxCallback;
        }

        private static void OnBloodImpactFxSpawned(GameObject go)
        {
            try
            {
                if (go == null || go.Pointer == IntPtr.Zero || !_inspectedBloodFxRoots.Add(go.Pointer))
                    return;

                var renderers = go.GetComponentsInChildren<ParticleSystemRenderer>(true);
                if (renderers == null)
                    return;

                for (int i = 0; i < renderers.Length; i++)
                {
                    ParticleSystemRenderer psr = renderers[i];
                    if (psr == null || psr.Pointer == IntPtr.Zero)
                        continue;

                    int origOrder = psr.sortingOrder;
                    float origFudge = psr.sortingFudge;
                    Material sharedMat = psr.sharedMaterial;
                    bool hasMat = sharedMat != null && sharedMat.Pointer != IntPtr.Zero;
                    int origQueue = hasMat ? sharedMat.renderQueue : -1;

                    int targetOrder = origOrder > 0 ? 0 : origOrder;
                    float targetFudge = origFudge < 6.5f ? 6.5f : origFudge;
                    bool clampQueue = hasMat && origQueue > 3000;

                    if (targetOrder != origOrder || !Mathf.Approximately(targetFudge, origFudge) || clampQueue)
                    {
                        if (_modifiedBloodRenderers.Count < 256)
                        {
                            _modifiedBloodRenderers.Add(new OriginalBloodRendererState
                            {
                                Renderer = psr,
                                SortingOrder = origOrder,
                                SortingFudge = origFudge,
                                SharedMat = hasMat ? sharedMat : null,
                                RenderQueue = origQueue
                            });
                        }

                        psr.sortingOrder = targetOrder;
                        psr.sortingFudge = targetFudge;
                        if (clampQueue)
                            sharedMat.renderQueue = 3000;

                        if (!_loggedBloodRendererNormalization)
                        {
                            _loggedBloodRendererNormalization = true;
                            AiCore.LogDebug(
                                "BloodVfx",
                                $"Normalized pooled ImpactFX_flesh renderer '{psr.name}': order {origOrder}->{targetOrder}, fudge {origFudge:F1}->{targetFudge:F1}, queue {origQueue}->{(clampQueue ? 3000 : origQueue)}",
                                10.0f);
                        }
                    }
                }
            }
            catch { }
        }

        private static void RestoreBloodRendererDefaults()
        {
            for (int i = 0; i < _modifiedBloodRenderers.Count; i++)
            {
                OriginalBloodRendererState st = _modifiedBloodRenderers[i];
                try
                {
                    if (st.Renderer != null && st.Renderer.Pointer != IntPtr.Zero)
                    {
                        st.Renderer.sortingOrder = st.SortingOrder;
                        st.Renderer.sortingFudge = st.SortingFudge;
                    }
                    if (st.SharedMat != null && st.SharedMat.Pointer != IntPtr.Zero && st.RenderQueue > 0)
                    {
                        st.SharedMat.renderQueue = st.RenderQueue;
                    }
                }
                catch { }
            }
            _modifiedBloodRenderers.Clear();
            _inspectedBloodFxRoots.Clear();
        }

        [HarmonyPatch(typeof(ResourcesManager), nameof(ResourcesManager.PlayImpactEffect))]
        public static class ResourcesManager_PlayImpactEffect_Patch
        {
            public static void Prefix(
                Vector3 pos,
                ref string impact_prefab_name,
                int maxSpawnDistance,
                ref Il2CppSystem.Action<GameObject> gameObjectFx)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value)
                        return;

                    if (string.IsNullOrEmpty(impact_prefab_name) ||
                        !string.Equals(impact_prefab_name, "ImpactFX_flesh", StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    if (!VisionConfig.EnableBloodSmokeWaterOcclusion.Value)
                    {
                        if (_modifiedBloodRenderers.Count > 0)
                            RestoreBloodRendererDefaults();
                        return;
                    }

                    if (!AiCore.TryGetActiveCameraPosition(out Vector3 camPos))
                        return;

                    if (maxSpawnDistance > 0)
                    {
                        float maxDistSq = (float)maxSpawnDistance * (float)maxSpawnDistance;
                        if ((pos - camPos).sqrMagnitude > maxDistSq)
                            return;
                    }

                    if (AiCore.IsPointOccludedByWaterOrSmoke(camPos, pos))
                    {
                        impact_prefab_name = "ImpactFX_flesh_noBlood";
                        BloodSmokeWaterOccludedCount++;
                        return;
                    }

                    if (gameObjectFx == null)
                        gameObjectFx = GetOrCreateBloodFxCallback();
                }
                catch { }
            }
        }
    }
}
