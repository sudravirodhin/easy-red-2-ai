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

        private struct CachedGrenadeBlastInfo
        {
            public GrenadeType Type;
            public float MaxPenMm;
            public bool Valid;
        }

        private static readonly Dictionary<IntPtr, PlaneAudioState> _planeAudioStates = new Dictionary<IntPtr, PlaneAudioState>(32);
        private static readonly Dictionary<IntPtr, int> _colliderImpactCache = new Dictionary<IntPtr, int>(512);
        private static readonly Dictionary<IntPtr, ParachuteDriftState> _parachuteDriftStates = new Dictionary<IntPtr, ParachuteDriftState>(32);
        private static readonly Dictionary<IntPtr, float> _vehicleExplosionArmorCache = new Dictionary<IntPtr, float>(64);
        private static readonly Dictionary<IntPtr, TrailRenderer[]> _cachedTracerTrails = new Dictionary<IntPtr, TrailRenderer[]>(64);
        private static readonly Dictionary<string, CachedGrenadeBlastInfo> _grenadeBlastCache = new Dictionary<string, CachedGrenadeBlastInfo>(32, StringComparer.OrdinalIgnoreCase);

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
            if (forceClear || _grenadeBlastCache.Count > 64)
                _grenadeBlastCache.Clear();
            if (forceClear || _inspectedBloodFxRoots.Count > 128)
            {
                _inspectedBloodFxRoots.Clear();
                _modifiedBloodRenderers.Clear();
            }
            if (forceClear)
            {
                _fpsHeadShadowProxies.Clear();
                ResetObjectiveBarFactionState();
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
                finally
                {
                    HitchProfiler.End(HitchBucket.BallisticsExpl, profStart);
                }
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

            if (throwable.TryCast<VirtualSmokeGrenade>() != null || throwable.TryCast<VirtualATGrenade>() != null)
                return true;

            string itemId = throwable.item_id ?? string.Empty;
            if (!_grenadeBlastCache.TryGetValue(itemId, out CachedGrenadeBlastInfo info))
            {
                info = default;
                try
                {
                    ItemObject prefab = throwable.GetItemPrefab();
                    ItemGrenade ig = prefab != null ? prefab.TryCast<ItemGrenade>() : null;
                    if (ig != null)
                    {
                        info.Type = ig.grenadeType;
                        info.MaxPenMm = ig.explosionMaxPenetration;
                        info.Valid = true;
                    }
                }
                catch { }

                if (!string.IsNullOrEmpty(itemId))
                {
                    if (_grenadeBlastCache.Count > 96)
                        _grenadeBlastCache.Clear();
                    _grenadeBlastCache[itemId] = info;
                }
            }

            if (info.Valid &&
                (info.Type == GrenadeType.smokeGrenade ||
                 info.Type == GrenadeType.ATGrenade ||
                 info.Type == GrenadeType.phosporusGrenade))
            {
                return true;
            }

            // Modded incendiary / satchel / anti-tank throwable name fallback
            if (!string.IsNullOrEmpty(itemId) &&
                (itemId.IndexOf("molotov", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 itemId.IndexOf("satchel", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 itemId.IndexOf("geballte", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 itemId.IndexOf("hafthohl", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 itemId.IndexOf("panzerwurf", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 itemId.IndexOf("gammon", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 itemId.IndexOf("hawkins", StringComparison.OrdinalIgnoreCase) >= 0))
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
            if (info.Valid && info.MaxPenMm >= Mathf.Max(12.0f, effArmor))
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
        // 4. SPECTATOR / FREE-CAMERA HUD, 'F' VIEW CYCLE & OBJECTIVE BAR FACTION NAMES
        //  - Decouples tactical overlays from CinematicCameraGUI.GUIVisible() so hiding
        //    the Free Camera UI banner does not wipe out Bot Stats or Objectives.
        //  - Pressing 'F' in Free Camera / Auto-Spectator cycles:
        //      Full Tactical -> Objectives Only -> Clean Cinema
        //  - Keeps both MissionStatusGUI (top bar) and ObjectiveGUI (3D A/B/C markers)
        //    live in Spectator Mode while filtering out private MissionTaskRetreat tasks.
        //  - Displays human-readable Faction + Side labels (e.g. "Germany (Axis)",
        //    "USSR (Allies)") flanking the Left and Right flags on the Objective Bar.
        // ============================================================================
        internal enum SpectatorTacticalHudMode : byte
        {
            FullTactical = 0,
            ObjectivesOnly = 1,
            CleanCinema = 2
        }

        internal static SpectatorTacticalHudMode ActiveSpectatorHudMode = SpectatorTacticalHudMode.FullTactical;
        private static float _nextSpectatorMissionRefreshTime;

        private static IntPtr _cachedMissionStatusGuiPtr;
        private static GameObject _leftFactionTextGo;
        private static GameObject _rightFactionTextGo;
        private static Text _leftFactionText;
        private static Text _rightFactionText;
        private static LayoutElement _leftFactionLayout;
        private static LayoutElement _rightFactionLayout;
        private static float _leftAppliedPrefW;
        private static float _rightAppliedPrefW;
        private static string _lastLeftFacStr;
        private static string _lastRightFacStr;

        private static void ResetObjectiveBarFactionState()
        {
            _cachedMissionStatusGuiPtr = IntPtr.Zero;
            _leftFactionTextGo = null;
            _rightFactionTextGo = null;
            _leftFactionText = null;
            _rightFactionText = null;
            _leftFactionLayout = null;
            _rightFactionLayout = null;
            _leftAppliedPrefW = 0f;
            _rightAppliedPrefW = 0f;
            _lastLeftFacStr = null;
            _lastRightFacStr = null;
        }

        internal static string CycleSpectatorTacticalHudMode()
        {
            ActiveSpectatorHudMode = (SpectatorTacticalHudMode)(((int)ActiveSpectatorHudMode + 1) % 3);
            MaintainSpectatorHudVisibility();
            switch (ActiveSpectatorHudMode)
            {
                case SpectatorTacticalHudMode.FullTactical:
                    return "[Battle Hardened] Spectator View (F): FULL TACTICAL (Objectives + Bot GOAP Inspector)";
                case SpectatorTacticalHudMode.ObjectivesOnly:
                    return "[Battle Hardened] Spectator View (F): OBJECTIVES ONLY (Objective Bar + 3D Markers)";
                default:
                    return "[Battle Hardened] Spectator View (F): CLEAN CINEMA (All Tactical Overlays Hidden)";
            }
        }

        internal static bool IsSpectatorCameraModeActive(out bool isFreeCamera, out bool canShowOverlays)
        {
            isFreeCamera = false;
            canShowOverlays = false;

            try
            {
                if (SavableData.Settings != null &&
                    SavableData.Settings.system != null &&
                    !SavableData.Settings.system.enableGUI)
                {
                    return false;
                }

                if (CinematicCameraGUI.instance != null && CinematicCameraGUI.cinematicCameraEnabled)
                {
                    isFreeCamera = true;
                    canShowOverlays = !RespawnPanel.GUIenabled;
                    return true;
                }

                Soldier controlled = Soldier.CurrentControlledSoldierOrNull();
                if ((controlled == null || !controlled.IsAlive) &&
                    CameraDirector.Instance != null &&
                    CameraDirector.Instance.isActiveAndEnabled)
                {
                    isFreeCamera = false;
                    canShowOverlays = !RespawnPanel.GUIenabled;
                    return true;
                }
            }
            catch { }

            return false;
        }

        public static void MaintainSpectatorHudVisibility()
        {
            if (!VisionConfig.EnableMod.Value || !VisionConfig.EnableSpectatorHudRetention.Value)
                return;

            if (CinematicCameraGUI.instance == null || !CinematicCameraGUI.cinematicCameraEnabled)
                return;

            // Only retain PlayerGUI (weapon/ammo/stance widgets) when a live controlled character exists
            // and the Cinematic Camera's own GUI is visible outside Clean Cinema mode!
            Soldier controlled = Soldier.CurrentControlledSoldierOrNull();
            bool hasLiveControlledSoldier = controlled != null && controlled.IsAlive;
            bool wantPlayerHud = CinematicCameraGUI.GUIVisible() &&
                                 hasLiveControlledSoldier &&
                                 !RespawnPanel.GUIenabled &&
                                 ActiveSpectatorHudMode != SpectatorTacticalHudMode.CleanCinema;

            PlayerGUI playerGui = PlayerGUI.instance;
            if (playerGui != null && playerGui.gui_obj != null)
            {
                if (playerGui.gui_obj.activeSelf != wantPlayerHud)
                    playerGui.gui_obj.SetActive(wantPlayerHud);
            }
        }

        private static Text CreateObjectiveBarFactionLabel(
            string objectName,
            Transform mainRowTransform,
            Text templateText,
            bool isLeftSide,
            out GameObject createdGo,
            out LayoutElement createdLayout)
        {
            createdGo = new GameObject(objectName);
            RectTransform rt = createdGo.AddComponent<RectTransform>();
            rt.SetParent(mainRowTransform, false);
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(100f, 22f);
            rt.localScale = Vector3.one;

            // Participate directly in mainRow's HorizontalLayoutGroup (padding=8,8,3,3, spacing=6) so
            // panel's ContentSizeFitter(PreferredSize) and mainRow's stretched 'bg' (+ left/right 'l'/'r'
            // pointed arrow caps) automatically expand around both faction names!
            createdLayout = createdGo.AddComponent<LayoutElement>();
            createdLayout.ignoreLayout = false;
            createdLayout.preferredHeight = 22f;
            createdLayout.preferredWidth = -1f;

            Text txt = createdGo.AddComponent<Text>();
            if (templateText != null)
            {
                txt.font = templateText.font;
                txt.fontSize = Mathf.Clamp(templateText.fontSize - 1, 13, 18);
            }
            else
            {
                txt.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
                txt.fontSize = 14;
            }
            txt.fontStyle = FontStyle.Bold;
            // Align toward the inner flag so the +6px outer LayoutElement width cushions the pointed arrow cap
            txt.alignment = isLeftSide ? TextAnchor.MiddleRight : TextAnchor.MiddleLeft;
            txt.horizontalOverflow = HorizontalWrapMode.Overflow;
            txt.verticalOverflow = VerticalWrapMode.Overflow;
            txt.raycastTarget = false;
            txt.color = new Color(0.96f, 0.94f, 0.88f, 0.96f);

            Shadow shadow = createdGo.AddComponent<Shadow>();
            shadow.effectColor = new Color(0f, 0f, 0f, 0.88f);
            shadow.effectDistance = new Vector2(1.25f, -1.25f);

            return txt;
        }

        private static void RebuildObjectiveBarLayout(MissionStatusGUI gui)
        {
            if (gui == null)
                return;

            if (gui.mainRow != null)
            {
                RectTransform mainRowRt = gui.mainRow.GetComponent<RectTransform>();
                if (mainRowRt != null)
                    LayoutRebuilder.ForceRebuildLayoutImmediate(mainRowRt);
            }
            if (gui.panel != null)
            {
                RectTransform panelRt = gui.panel.GetComponent<RectTransform>();
                if (panelRt != null)
                    LayoutRebuilder.ForceRebuildLayoutImmediate(panelRt);
            }
        }

        private static void UpdateMissionStatusFactionLabels(MissionStatusGUI gui)
        {
            if (gui == null || gui.Pointer == IntPtr.Zero || gui.mainRow == null)
                return;

            if (!VisionConfig.EnableObjectiveBarFactionNames.Value)
            {
                bool changedOff = false;
                if (_leftFactionTextGo != null && _leftFactionTextGo.activeSelf)
                {
                    _leftFactionTextGo.SetActive(false);
                    changedOff = true;
                }
                if (_rightFactionTextGo != null && _rightFactionTextGo.activeSelf)
                {
                    _rightFactionTextGo.SetActive(false);
                    changedOff = true;
                }
                if (changedOff)
                    RebuildObjectiveBarLayout(gui);
                return;
            }

            if (gui.flagInvaders == null || gui.flagDefenders == null)
                return;

            BattleData bd = BattleManager.GetCurrentBattleData();
            string invFac = bd != null ? bd.GetInvadersFaction() : null;
            string defFac = bd != null ? bd.GetDefendersFaction() : null;

            if ((string.IsNullOrEmpty(invFac) || string.IsNullOrEmpty(defFac)) && bd != null)
            {
                AiCore.EnsureWorldSnapshot();
                int sqCount = AiCore.WorldSquadCount;
                var squads = AiCore.WorldSquads;
                for (int i = 0; i < sqCount; i++)
                {
                    string f = squads[i].Faction;
                    if (string.IsNullOrEmpty(f))
                        continue;
                    if (bd.IsInvaderFaction(f))
                    {
                        if (string.IsNullOrEmpty(invFac))
                            invFac = f;
                    }
                    else if (string.IsNullOrEmpty(defFac))
                    {
                        defFac = f;
                    }
                    if (!string.IsNullOrEmpty(invFac) && !string.IsNullOrEmpty(defFac))
                        break;
                }
            }

            bool mirrored = gui.flagsMirrored;
            string leftFormatted = AiCore.FormatFactionWithAlliance(mirrored ? defFac : invFac);
            string rightFormatted = AiCore.FormatFactionWithAlliance(mirrored ? invFac : defFac);

            Transform mainRowTr = gui.mainRow.transform;
            bool layoutDirty = false;

            if (_cachedMissionStatusGuiPtr != gui.Pointer ||
                _leftFactionText == null ||
                _rightFactionText == null ||
                _leftFactionTextGo == null ||
                _rightFactionTextGo == null)
            {
                _cachedMissionStatusGuiPtr = gui.Pointer;
                _lastLeftFacStr = null;
                _lastRightFacStr = null;
                _leftAppliedPrefW = 0f;
                _rightAppliedPrefW = 0f;
                _leftFactionText = CreateObjectiveBarFactionLabel(
                    "BH_FactionLeft",
                    mainRowTr,
                    gui.ticketsText,
                    isLeftSide: true,
                    out _leftFactionTextGo,
                    out _leftFactionLayout);
                _rightFactionText = CreateObjectiveBarFactionLabel(
                    "BH_FactionRight",
                    mainRowTr,
                    gui.ticketsText,
                    isLeftSide: false,
                    out _rightFactionTextGo,
                    out _rightFactionLayout);
                layoutDirty = true;
            }

            // Keep BH_FactionLeft immediately before flag_invaders (and after sibling 0 'bg' so it renders above the bar)
            int flagInvIdx = gui.flagInvaders.transform.GetSiblingIndex();
            int leftIdx = _leftFactionTextGo.transform.GetSiblingIndex();
            int desiredLeftIdx = leftIdx < flagInvIdx ? flagInvIdx - 1 : flagInvIdx;
            if (mainRowTr.childCount > 0 && mainRowTr.GetChild(0) != _leftFactionTextGo.transform && flagInvIdx > 0 && desiredLeftIdx < 1)
                desiredLeftIdx = 1;
            if (leftIdx != desiredLeftIdx)
            {
                _leftFactionTextGo.transform.SetSiblingIndex(desiredLeftIdx);
                layoutDirty = true;
            }

            // Keep BH_FactionRight as the last child in mainRow (immediately after flag_defenders)
            int lastIdx = mainRowTr.childCount - 1;
            if (_rightFactionTextGo.transform.GetSiblingIndex() != lastIdx)
            {
                _rightFactionTextGo.transform.SetAsLastSibling();
                layoutDirty = true;
            }

            if (_lastLeftFacStr != leftFormatted)
            {
                _lastLeftFacStr = leftFormatted;
                _leftFactionText.text = leftFormatted;
                _leftAppliedPrefW = 0f;
                layoutDirty = true;
            }
            if (_lastRightFacStr != rightFormatted)
            {
                _lastRightFacStr = rightFormatted;
                _rightFactionText.text = rightFormatted;
                _rightAppliedPrefW = 0f;
                layoutDirty = true;
            }

            bool showLeft = !string.IsNullOrEmpty(leftFormatted);
            bool showRight = !string.IsNullOrEmpty(rightFormatted);
            if (_leftFactionTextGo.activeSelf != showLeft)
            {
                _leftFactionTextGo.SetActive(showLeft);
                layoutDirty = true;
            }
            if (_rightFactionTextGo.activeSelf != showRight)
            {
                _rightFactionTextGo.SetActive(showRight);
                layoutDirty = true;
            }

            if (showLeft && _leftAppliedPrefW <= 0f && _leftFactionLayout != null)
            {
                float w = _leftFactionText.preferredWidth;
                if (w > 1f)
                {
                    _leftAppliedPrefW = Mathf.Ceil(w) + 6f;
                    _leftFactionLayout.preferredWidth = _leftAppliedPrefW;
                    layoutDirty = true;
                }
                else
                {
                    _leftFactionLayout.preferredWidth = -1f;
                }
            }
            if (showRight && _rightAppliedPrefW <= 0f && _rightFactionLayout != null)
            {
                float w = _rightFactionText.preferredWidth;
                if (w > 1f)
                {
                    _rightAppliedPrefW = Mathf.Ceil(w) + 6f;
                    _rightFactionLayout.preferredWidth = _rightAppliedPrefW;
                    layoutDirty = true;
                }
                else
                {
                    _rightFactionLayout.preferredWidth = -1f;
                }
            }

            if (layoutDirty)
            {
                RebuildObjectiveBarLayout(gui);
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

        // Native MissionStatusGUI.LateUpdate (0x1804AB520) sets guiEnabled = false (skipping RefreshData) whenever
        // ControlledCharacter == null, and unconditionally calls panel.SetActive(false) whenever cinematicCameraEnabled
        // is true! Keep the Objective Bar live at 4 Hz in Free Cam & Auto-Spectator (unless CleanCinema is selected)
        // and maintain formatted Left/Right Faction + Side labels across all mission types.
        [HarmonyPatch(typeof(MissionStatusGUI), nameof(MissionStatusGUI.LateUpdate))]
        public static class MissionStatusGUI_LateUpdate_Patch
        {
            public static void Postfix(MissionStatusGUI __instance)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value ||
                        __instance == null ||
                        __instance.panel == null)
                    {
                        return;
                    }

                    if (IsSpectatorCameraModeActive(out _, out bool canShowOverlays))
                    {
                        bool showSpecBar = canShowOverlays &&
                                           VisionConfig.EnableSpectatorHudRetention.Value &&
                                           ActiveSpectatorHudMode != SpectatorTacticalHudMode.CleanCinema;
                        if (!showSpecBar)
                        {
                            if (__instance.panel.activeSelf)
                                __instance.panel.SetActive(false);
                            return;
                        }

                        if (!__instance.guiEnabled || CinematicCameraGUI.cinematicCameraEnabled)
                        {
                            float now = Time.unscaledTime;
                            if (now >= _nextSpectatorMissionRefreshTime || now + 5.0f < _nextSpectatorMissionRefreshTime)
                            {
                                _nextSpectatorMissionRefreshTime = now + 0.25f;
                                __instance.RefreshData();
                            }

                            bool luaMaster = MissionStatusGUI.lua_masterEnabled;
                            bool showMainRow = !luaMaster || MissionStatusGUI.lua_vanillaBar;
                            if (__instance.mainRow != null && __instance.mainRow.activeSelf != showMainRow)
                                __instance.mainRow.SetActive(showMainRow);

                            bool hasLuaFields = luaMaster && MissionStatusGUI.lua_fields != null && MissionStatusGUI.lua_fields.Count > 0;
                            if (__instance.fieldsRow != null && __instance.fieldsRow.activeSelf != hasLuaFields)
                                __instance.fieldsRow.SetActive(hasLuaFields);
                            if (__instance.fieldsRowRight != null && __instance.fieldsRowRight.activeSelf != hasLuaFields)
                                __instance.fieldsRowRight.SetActive(hasLuaFields);

                            if (luaMaster && __instance.appliedLuaVersion != MissionStatusGUI.lua_version)
                            {
                                __instance.appliedLuaVersion = MissionStatusGUI.lua_version;
                                __instance.ApplyLuaFields();
                            }

                            var slots = __instance.objSlots;
                            if (slots != null)
                            {
                                float step = Mathf.Max(0.005f, Time.unscaledDeltaTime * 2.5f);
                                int sLen = slots.Count;
                                for (int i = 0; i < sLen; i++)
                                {
                                    var slot = slots[i];
                                    if (slot != null && slot.go != null && slot.go.activeSelf && slot.fill != null)
                                    {
                                        slot.fill.fillAmount = Mathf.MoveTowards(slot.fill.fillAmount, slot.targetFill, step);
                                    }
                                }
                            }
                        }

                        if (!__instance.panel.activeSelf)
                            __instance.panel.SetActive(true);

                        if (__instance.panelGroup != null && __instance.panelGroup.alpha < 0.90f)
                            __instance.panelGroup.alpha = 0.95f;
                    }

                    if (__instance.panel.activeSelf)
                    {
                        UpdateMissionStatusFactionLabels(__instance);
                    }
                }
                catch { }
            }
        }

        // Native ObjectiveGUI.LateUpdate (0x1804BA6B0) hides 3D world objective markers (guiPanel.SetActive(false))
        // whenever cinematicCameraEnabled is true OR ControlledCharacter == null. Keep public objective markers
        // (excluding private AI MissionTaskRetreat spheres) live in Free Cam & Auto-Spectator!
        [HarmonyPatch(typeof(ObjectiveGUI), nameof(ObjectiveGUI.LateUpdate))]
        public static class ObjectiveGUI_LateUpdate_Patch
        {
            public static void Postfix(ObjectiveGUI __instance)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value ||
                        __instance == null ||
                        __instance.guiPanel == null)
                    {
                        return;
                    }

                    if (!IsSpectatorCameraModeActive(out _, out bool canShowOverlays))
                        return;

                    bool allow3DMarkers = SavableData.Settings == null ||
                                          SavableData.Settings.system == null ||
                                          !SavableData.Settings.system.disable3DMarkers;
                    SquadTask task = __instance.currentTask;
                    MissionTaskHighlightPosition highlightTask = task != null ? task.TryCast<MissionTaskHighlightPosition>() : null;
                    bool isPublicObjective = task != null &&
                                             (highlightTask == null || !highlightTask.removed) &&
                                             task.TryCast<MissionTaskRetreat>() == null;
                    bool wantWorldMarker = canShowOverlays &&
                                           allow3DMarkers &&
                                           isPublicObjective &&
                                           VisionConfig.EnableSpectatorHudRetention.Value &&
                                           ActiveSpectatorHudMode != SpectatorTacticalHudMode.CleanCinema;

                    if (!wantWorldMarker)
                    {
                        if (__instance.guiPanel.activeSelf)
                            __instance.guiPanel.SetActive(false);
                        return;
                    }

                    float now = Time.unscaledTime;
                    if (now >= __instance.nextUpdateTime || __instance.nextUpdateTime - now > 5.0f)
                    {
                        __instance.nextUpdateTime = now + 0.25f;
                        __instance.RefreshData();
                    }

                    if (__instance.currentBaseAlpha <= 0.05f)
                    {
                        __instance.currentBaseAlpha = 0.85f;
                        __instance.slowTargetAlpha = 0.85f;
                        if (__instance.currentAlpha < 0.15f)
                            __instance.currentAlpha = 0.85f;
                    }

                    if (!__instance.guiPanel.activeSelf)
                        __instance.guiPanel.SetActive(true);

                    __instance.UpdateGUI();
                    __instance.UpdateSmoothing();
                }
                catch { }
            }
        }

        // ============================================================================
        // 5. VANILLA VISUAL FIX: FIRST-PERSON SELF-SHADOW FIX
        // Native x64 Root Causes in GameAssembly.dll:
        //  1) SoldierLodManager.AssignFaceLod (0x18079B730) calls SoldierLodManager.ApplyFace
        //     (0x18079B4D0 -> SetBodyHeadVisibility @ 0x18079C040, bypassing dead method
        //     ApplyBodyHeadVisibility @ 0x18079B430!) and swaps the local player's head
        //     submesh materials to ResourcesManager.hideMaterial (ZERO shadow), while
        //     ItemHelmet.SetHidden() uses ShadowCastingMode.ShadowsOnly—producing a
        //     headless torso shadow with a floating helmet hovering over an empty neck!
        //  2) FPSGunManager.UpdateItemInFPSHands (0x180796610) calls unit.ShowRealArms(false)
        //     (scaling 3P leftArm & rightArm to Vector3.zero) and reparents the SOLE
        //     HandheldItem onto WeaponJNT / WeaponJNT_generic—meaning FPSGunManager's
        //     hands_mesh, uniform_mesh, and WeaponJNT ARE the player's only arms & weapon
        //     in First Person and MUST keep ShadowCastingMode.On!
        // Fix (Local Player Only — Zero Bot Overhead):
        //  - Keep FPSGunManager arms, uniform sleeves, and held weapon ShadowCastingMode.On
        //    so animated arms and held weapons cast natural shadows alongside the head.
        //  - Hook SoldierLodManager.ApplyFace (and ApplyBodyHeadVisibility for safety): when
        //    isFpsPlayer hides the head (and UniformHeadCoverType == 0), set bodyMesh to
        //    ShadowCastingMode.Off (preserving camera-safe hideMaterial on the head) and drive
        //    a cached ShadowsOnly child SkinnedMeshRenderer with full FaceData materials
        //    (using fd.index_body_submesh) so head, neck, helmet, torso, arms, and weapon
        //    form a complete first-person shadow!
        // ============================================================================
        private static readonly Dictionary<IntPtr, SkinnedMeshRenderer> _fpsHeadShadowProxies = new Dictionary<IntPtr, SkinnedMeshRenderer>(4);

        public static void RefreshLocalPlayerFirstPersonShadow()
        {
            try
            {
                Soldier localPlayer = Soldier.CurrentControlledSoldierOrNull();
                if (localPlayer != null && localPlayer.IsAlive)
                {
                    localPlayer.RefreshFace();
                }
            }
            catch { }
        }

        private static void SyncFpsHeadShadowProxy(
            SoldierLodManager lodMgr,
            Corvostudio.EasyRed2.Characters.FaceData fd,
            bool showBody,
            bool wantFpsShadowProxy)
        {
            if (lodMgr == null || lodMgr.Pointer == IntPtr.Zero)
                return;

            SkinnedMeshRenderer bodyMesh = lodMgr.bodyMesh;
            if (bodyMesh == null || bodyMesh.Pointer == IntPtr.Zero || bodyMesh.transform == null)
                return;

            IntPtr lodPtr = lodMgr.Pointer;
            if (!wantFpsShadowProxy || fd == null || fd.materials == null)
            {
                bodyMesh.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                if (_fpsHeadShadowProxies.TryGetValue(lodPtr, out SkinnedMeshRenderer existingProxy) &&
                    existingProxy != null &&
                    existingProxy.Pointer != IntPtr.Zero)
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
            proxy.quality = bodyMesh.quality;
            proxy.updateWhenOffscreen = bodyMesh.updateWhenOffscreen;
            proxy.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly;
            proxy.receiveShadows = false;
            SoldierLodManager.SetBodyHeadVisibility(proxy, fd.materials, fd.index_body_submesh, showBody, true);
            if (lodMgr.ShaderPropertyBlock != null)
            {
                proxy.SetPropertyBlock(lodMgr.ShaderPropertyBlock);
            }
            proxy.enabled = true;

            // Prevent duplicate body shadow from bodyMesh while keeping its visible body submesh + hidden head submesh intact
            bodyMesh.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        [HarmonyPatch(typeof(SoldierLodManager), nameof(SoldierLodManager.ApplyFace))]
        public static class SoldierLodManager_ApplyFace_Patch
        {
            public static void Postfix(
                SoldierLodManager __instance,
                bool __0,
                Corvostudio.EasyRed2.Characters.FaceData __1,
                UniformSleeveCoverType __2,
                UniformHeadCoverType __3)
            {
                try
                {
                    // Match native SoldierLodManager.ApplyFace (0x18079B69A):
                    // showBody = (usct != 2), and in 3rd-person showHead = (uhct == 0).
                    bool showBody = (int)__2 != 2;
                    bool headVisibleIn3P = (int)__3 == 0;
                    bool wantProxy = VisionConfig.EnableMod.Value &&
                                     VisionConfig.EnableFirstPersonShadowFix.Value &&
                                     __0 &&
                                     headVisibleIn3P;
                    SyncFpsHeadShadowProxy(__instance, __1, showBody, wantProxy);
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
                    bool wantProxy = VisionConfig.EnableMod.Value &&
                                     VisionConfig.EnableFirstPersonShadowFix.Value &&
                                     !__2;
                    SyncFpsHeadShadowProxy(__instance, __0, __1, wantProxy);
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
                ref Il2CppSystem.Action<GameObject> gameObjectFx,
                out long __state)
            {
                __state = HitchProfiler.Begin();
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

            public static void Postfix(long __state)
            {
                HitchProfiler.End(HitchBucket.BloodImpactFx, __state);
            }
        }
    }
}
