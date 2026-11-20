using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace EasyRed2HumanAI
{
    public static partial class VeteranAndTacticsPatches
    {
        internal struct VehicleAimCache
        {
            public IntPtr VehiclePtr;
            public float ExpireTime;
            public bool CanEngage;
            public bool HasCustomAimPoint;
            public Vector3 CustomAimPoint;
        }

        private static readonly Dictionary<IntPtr, float> _repairTaskStartTime = new Dictionary<IntPtr, float>(64);
        private static readonly HashSet<IntPtr> _countedRepairTasks = new HashSet<IntPtr>();
        private static readonly Dictionary<IntPtr, float> _unreachableEmplacementUntil = new Dictionary<IntPtr, float>(128);
        private static readonly List<RepairStation> _activeRepairStations = new List<RepairStation>(16);
        private static readonly Dictionary<IntPtr, RepairStation> _vehiclesSeekingRepairStation = new Dictionary<IntPtr, RepairStation>(32);
        private static readonly HashSet<IntPtr> _vehiclesAnsweringTankSupport = new HashSet<IntPtr>();
        private static readonly Dictionary<IntPtr, IntPtr> _squadOrderedToVehicle = new Dictionary<IntPtr, IntPtr>(32);
        private static readonly Dictionary<IntPtr, float> _squadTaskCommittedUntil = new Dictionary<IntPtr, float>(64);
        private static readonly Dictionary<IntPtr, float> _nextTurretFFCheckTime = new Dictionary<IntPtr, float>(64);
        private static readonly Dictionary<IntPtr, float> _turretFFBlockedUntil = new Dictionary<IntPtr, float>(64);

        // =====================================================================
        // Helper: Sheltered Tank / Vehicle Repair Position (Leeward Hull Cover)
        // =====================================================================
        private static Vector3 GetShelteredRepairPosition(Vehicle veh, Soldier mechanic, out Vector3 shelterDir)
        {
            shelterDir = Vector3.back;
            if (veh == null || veh.transform == null)
                return Vector3.zero;

            Vector3 vehPos = veh.transform.position;
            Vector3 vehFwd = veh.transform.forward;
            vehFwd.y = 0f;
            vehFwd = vehFwd.sqrMagnitude > 0.001f ? vehFwd.normalized : Vector3.forward;

            Vector3 threatDir = Vector3.zero;

            // 1. Check the mechanic's own visible target first
            if (mechanic != null && mechanic.aiController != null &&
                AiCore.TryGetSpottableCenter(mechanic.aiController.visibleTarget, out Vector3 mechEnemyPos))
            {
                Vector3 toEnemy = mechEnemyPos - vehPos;
                toEnemy.y = 0f;
                if (toEnemy.sqrMagnitude > 0.25f)
                    threatDir = toEnemy.normalized;
            }

            // 2. Check the vehicle's main turret target
            Turret mainTurret = veh.GetMainTurret(false);
            if (threatDir.sqrMagnitude < 0.01f && mainTurret != null && mainTurret.autoPilot != null)
            {
                if (AiCore.TryGetSpottableCenter(mainTurret.autoPilot.CurrentVisibleTarget, out Vector3 turretEnemyPos))
                {
                    Vector3 toEnemy = turretEnemyPos - vehPos;
                    toEnemy.y = 0f;
                    if (toEnemy.sqrMagnitude > 0.25f)
                        threatDir = toEnemy.normalized;
                }
            }

            // 3. Check secondary seat turrets or seated crew members' targets
            if (threatDir.sqrMagnitude < 0.01f && veh.seats != null)
            {
                var seats = veh.seats;
                int seatCount = seats.Length;
                for (int i = 0; i < seatCount; i++)
                {
                    Seats seat = seats[i];
                    if (seat == null) continue;

                    if (seat.connectedTurret != null && seat.connectedTurret.autoPilot != null)
                    {
                        if (AiCore.TryGetSpottableCenter(seat.connectedTurret.autoPilot.CurrentVisibleTarget, out Vector3 secTurretPos))
                        {
                            Vector3 toEnemy = secTurretPos - vehPos;
                            toEnemy.y = 0f;
                            if (toEnemy.sqrMagnitude > 0.25f)
                            {
                                threatDir = toEnemy.normalized;
                                break;
                            }
                        }
                    }

                    if (seat.unitSet != null && seat.unitSet.aiController != null &&
                        AiCore.TryGetSpottableCenter(seat.unitSet.aiController.visibleTarget, out Vector3 seatEnemyPos))
                    {
                        Vector3 toEnemy = seatEnemyPos - vehPos;
                        toEnemy.y = 0f;
                        if (toEnemy.sqrMagnitude > 0.25f)
                        {
                            threatDir = toEnemy.normalized;
                            break;
                        }
                    }
                }
            }

            // 4. Use the main turret's aim / fire direction (points toward the last known threat sector)
            if (threatDir.sqrMagnitude < 0.01f && mainTurret != null)
            {
                Vector3 fireDir = mainTurret.GetFireDir();
                fireDir.y = 0f;
                if (fireDir.sqrMagnitude > 0.01f)
                    threatDir = fireDir.normalized;
            }

            // 5. Fallback to the tank hull's forward vector (enemies are typically ahead of the frontal glacis)
            if (threatDir.sqrMagnitude < 0.01f)
            {
                threatDir = vehFwd;
            }

            // Sheltered side is directly opposite the threat so the tank hull acts as solid armor cover
            shelterDir = -threatDir;

            float absFwdDot = Mathf.Abs(Vector3.Dot(shelterDir, vehFwd));
            float hullOffset = Mathf.Lerp(2.35f, 3.25f, absFwdDot);

            Transform vehRoot = veh.transform.root;
            Vector3 rayOrigin = vehPos + Vector3.up * 1.1f;
            if (AiCore.IsDirectionBlockedByEnvironment(rayOrigin, shelterDir, hullOffset + 0.5f, vehRoot))
            {
                Vector3 altLeft = Quaternion.Euler(0f, 55f, 0f) * shelterDir;
                float altLeftOffset = Mathf.Lerp(2.35f, 3.25f, Mathf.Abs(Vector3.Dot(altLeft, vehFwd)));
                if (!AiCore.IsDirectionBlockedByEnvironment(rayOrigin, altLeft, altLeftOffset + 0.5f, vehRoot))
                {
                    shelterDir = altLeft;
                    hullOffset = altLeftOffset;
                }
                else
                {
                    Vector3 altRight = Quaternion.Euler(0f, -55f, 0f) * shelterDir;
                    float altRightOffset = Mathf.Lerp(2.35f, 3.25f, Mathf.Abs(Vector3.Dot(altRight, vehFwd)));
                    if (!AiCore.IsDirectionBlockedByEnvironment(rayOrigin, altRight, altRightOffset + 0.5f, vehRoot))
                    {
                        shelterDir = altRight;
                        hullOffset = altRightOffset;
                    }
                }
            }

            return vehPos + shelterDir * hullOffset;
        }

        // =====================================================================
        // Smart Anti-Armor Target & Fire Discipline
        // =====================================================================
        private static bool TryEvaluateVehicleTarget(
            Soldier shooter,
            Vehicle veh,
            float dist,
            out Soldier exposedCrewTarget,
            out Vector3 customAimPoint,
            out bool hasCustomAimPoint)
        {
            exposedCrewTarget = null;
            customAimPoint = Vector3.zero;
            hasCustomAimPoint = false;

            if (shooter == null || veh == null)
                return false;

            GenericGun heldGun = shooter.GetHeldGun();
            if (heldGun == null)
                return false;

            float maxShootDist = heldGun.DamageRange();
            if (dist > maxShootDist + 5f)
                return false;

            bool isAtWeapon = heldGun.IsAtWeapon();
            bool isExplosiveOrFlame = heldGun.isFlamethrower || heldGun.IsGrenadeLauncher();
            float bulletPenMm = 0f;

            AmmoItem ammoItem = heldGun.GetAmmoItem();
            if (ammoItem == null)
                ammoItem = heldGun.usedAmmoItem;

            if (ammoItem != null)
            {
                BulletData bd = ammoItem.GetBulletData();
                if (bd != null)
                {
                    if (IsTrueHeOrAntiInfantryRound(bd) || bd.explosion_penetration > 10f || bd.mmPenetration > 25)
                    {
                        isExplosiveOrFlame = true;
                    }
                    bulletPenMm = bd.CalculateMmPenetrationWithFallof(dist);
                }
            }

            if (bulletPenMm <= 0.01f)
            {
                bulletPenMm = isAtWeapon ? 35f : 6f;
            }

            Vector3 shooterPos = shooter.transform.position;

            // 1. Check for exposed crew members / passengers (Seats.showUnitSet == true) on ground/sea vehicles
            //    (Never target pilots inside aircraft cockpits directly as exposed crew!)
            var seats = veh.IsAirVehicle() ? null : veh.seats;
            if (seats != null)
            {
                float bestCrewDistSq = float.MaxValue;
                int seatCount = seats.Length;
                for (int i = 0; i < seatCount; i++)
                {
                    Seats seat = seats[i];
                    if (seat == null || !seat.showUnitSet)
                        continue;

                    Soldier crew = seat.unitSet;
                    if (crew == null || !crew.IsAlive || AiCore.AreSameSideFactions(crew.faction, shooter.faction))
                        continue;

                    Spottable crewSpottable = crew.Cast<Spottable>();
                    if (shooter.CanSee(crewSpottable))
                    {
                        float dSq = (crew.transform.position - shooterPos).sqrMagnitude;
                        if (dSq < bestCrewDistSq)
                        {
                            bestCrewDistSq = dSq;
                            exposedCrewTarget = crew;
                        }
                    }
                }

                if (exposedCrewTarget != null && !isAtWeapon && !isExplosiveOrFlame)
                {
                    customAimPoint = exposedCrewTarget.GetCenterOfUnit();
                    hasCustomAimPoint = true;
                    return true;
                }
            }

            // 2. Dedicated explosive / HEAT / Flamethrower / heavy AT weapons (>40mm pen) can engage vehicle hulls directly
            if (isExplosiveOrFlame || bulletPenMm >= 40f)
            {
                return true;
            }

            bool isTank = veh.TryCast<VehicleTank>() != null;

            // Standard small arms (<12mm penetration) cannot penetrate tank hulls or tank tracks
            if (isTank && !isAtWeapon && bulletPenMm < 12f)
            {
                if (exposedCrewTarget != null)
                {
                    customAimPoint = exposedCrewTarget.GetCenterOfUnit();
                    hasCustomAimPoint = true;
                    return true;
                }
                return false;
            }

            // 3. Inspect vehicle damageable parts for penetrable hull armor, vulnerable wheels, or breakable tracks
            var parts = veh.damageableParts;
            if (parts != null)
            {
                bool canPenetrateMainHull = false;
                Vector3 bestPartPos = Vector3.zero;
                float bestPartDistSq = float.MaxValue;

                Vector3 bestWheelOrTrackPos = Vector3.zero;
                float bestWheelOrTrackDistSq = float.MaxValue;

                int partCount = parts.Count;
                for (int i = 0; i < partCount; i++)
                {
                    VehicleDamagablePart part = parts[i];
                    if (part == null || part.transform == null)
                        continue;

                    if (part.TryCast<VehicleDamagableDetachablePart>() != null)
                        continue;

                    VehicleDamageableWheelPart wheelPart = part.TryCast<VehicleDamageableWheelPart>();
                    if (wheelPart != null)
                    {
                        if (!isTank && !wheelPart.WheelIsFlat && dist <= 60f && bulletPenMm > wheelPart.armorThickness + 0.5f)
                        {
                            Vector3 wPos = wheelPart.transform.position;
                            float dSq = (wPos - shooterPos).sqrMagnitude;
                            if (dSq < bestWheelOrTrackDistSq)
                            {
                                bestWheelOrTrackDistSq = dSq;
                                bestWheelOrTrackPos = wPos;
                            }
                        }
                        continue;
                    }

                    VehicleDamageableTrackPart trackPart = part.TryCast<VehicleDamageableTrackPart>();
                    if (trackPart != null)
                    {
                        if (!trackPart.TrackIsBroken && bulletPenMm > trackPart.armorThickness + 0.5f)
                        {
                            Vector3 tPos = trackPart.transform.position;
                            float dSq = (tPos - shooterPos).sqrMagnitude;
                            if (dSq < bestWheelOrTrackDistSq)
                            {
                                bestWheelOrTrackDistSq = dSq;
                                bestWheelOrTrackPos = tPos;
                            }
                        }
                        continue;
                    }

                    float effectiveArmor = part.considerAngledArmor ? (part.armorThickness * 1.15f) : part.armorThickness;
                    if (bulletPenMm > effectiveArmor + 0.5f)
                    {
                        canPenetrateMainHull = true;
                        Vector3 pPos = part.transform.position;
                        float dSq = (pPos - shooterPos).sqrMagnitude;
                        if (dSq < bestPartDistSq)
                        {
                            bestPartDistSq = dSq;
                            bestPartPos = pPos;
                        }
                    }
                }

                if (canPenetrateMainHull)
                {
                    if (isTank && bestPartDistSq < float.MaxValue)
                    {
                        customAimPoint = bestPartPos;
                        hasCustomAimPoint = true;
                    }
                    return true;
                }

                if (bestWheelOrTrackDistSq < float.MaxValue)
                {
                    customAimPoint = bestWheelOrTrackPos;
                    hasCustomAimPoint = true;
                    return true;
                }
            }

            if (exposedCrewTarget != null)
            {
                customAimPoint = exposedCrewTarget.GetCenterOfUnit();
                hasCustomAimPoint = true;
                return true;
            }

            return false;
        }

        private static void TryThrowNormalGrenadeAtVehicle(Soldier soldier, Vehicle targetVeh, float dist)
        {
            if (!AiCore.IsLocallyOwnedBot(soldier) || targetVeh == null || targetVeh.transform == null || soldier.inventory == null)
                return;

            if (dist <= 7.5f || dist > 32f || soldier.isInBodyAnimation || soldier.isInHandsAnimation)
                return;

            // Prefer dedicated throwable AT / sapper grenades first, then non-mine grenades (Molotov / HE / frag)
            VirtualItem grenadeItem = AiCore.FindThrowableGrenadeInInventory(soldier, findAtGrenade: true, findFragGrenade: false);
            if (grenadeItem == null)
                grenadeItem = AiCore.FindThrowableGrenadeInInventory(soldier, findAtGrenade: false, findFragGrenade: true);

            if (grenadeItem != null)
            {
                if (VisionConfig.PreventSmallArmsFireAtTanks.Value &&
                    !CanThrowableDamageOrFlushVehicle(soldier, grenadeItem, targetVeh))
                {
                    return;
                }

                float maxThrowRange = soldier.GetGrenadeThrowRange(grenadeItem);
                if (dist <= maxThrowRange &&
                    UnityEngine.Random.Range(0, 100) > 65 &&
                    IsSafeToThrowGrenadeAt(soldier, targetVeh.transform.position, dist, 8.5f) &&
                    AiCore.TryConsumeBotRpcBudget(0.45f))
                {
                    soldier.Throw(grenadeItem, targetVeh.Cast<Spottable>());
                    bool isTank = targetVeh.TryCast<VehicleTank>() != null;
                    TrySayTacticalVoice(
                        soldier,
                        soldier.joinedSquad,
                        isTank ? VoiceManager.VoiceClip.attackThatTank : VoiceManager.VoiceClip.attackThatVehicle,
                        0.08f);
                }
            }
        }

        internal static bool TryFindVisibleFootEnemy(Soldier shooter, out Spottable bestFootEnemy, out float bestFootDist)
        {
            bestFootEnemy = null;
            bestFootDist = float.MaxValue;
            if (shooter == null || shooter.transform == null)
                return false;

            GenericGun heldGun = shooter.GetHeldGun();
            float maxWeaponDist = heldGun != null ? Mathf.Min(heldGun.DamageRange(), 185.0f) : 140.0f;
            if (maxWeaponDist < 10.0f)
                return false;

            Vector3 myPos = shooter.transform.position;
            string myFaction = shooter.faction;

            // If this is an AT soldier, temporarily toggle isAtUnit off to query vanilla's octree
            if (shooter.isAtUnit)
            {
                shooter.isAtUnit = false;
                Spottable alt = shooter.GetBestVisibleEnemy(out float altDist);
                shooter.isAtUnit = true;
                if (alt != null && !alt.IsWheeledVehicleOrTank() && !alt.IsAirVehicle() && altDist <= maxWeaponDist)
                {
                    bestFootEnemy = alt;
                    bestFootDist = altDist;
                    return true;
                }
            }

            // For any infantryman whose octree nearest-enemy query returned an invulnerable tank,
            // check nearby enemy squads for a visible enemy foot soldier (e.g. infantry advancing with the tank!)
            RefreshCombinedArmsIntelIfNeeded();
            int clusterCount = _knownEnemyClusters.Count;
            for (int i = 0; i < clusterCount; i++)
            {
                KnownEnemyCluster cluster = _knownEnemyClusters[i];
                if (AiCore.AreSameSideFactions(cluster.Faction, myFaction))
                    continue;

                for (int pass = 0; pass < 2; pass++)
                {
                    Spottable cand = pass == 0 ? cluster.PrimarySoldier : cluster.SecondarySoldier;
                    if (cand == null || cand.Pointer == IntPtr.Zero)
                        continue;

                    float d = Vector3.Distance(myPos, cand.GetCenterOfUnit());
                    if (d >= bestFootDist || d > maxWeaponDist)
                        continue;

                    if (shooter.CanSee(cand))
                    {
                        bestFootDist = d;
                        bestFootEnemy = cand;
                    }
                }
            }

            return bestFootEnemy != null;
        }

        internal static bool TryEquipOrHoldStowedAtWeaponAgainstVehicle(Soldier soldier, Vehicle targetVeh, float dist)
        {
            if (soldier == null || targetVeh == null || soldier.IsOnVehicle() || dist > 95f)
                return false;

            GenericGun g0 = soldier.GetHeldGun(0);
            if (g0 != null && g0.IsAtWeapon() && (g0.GetCurrentAmmoCount() > 0 || !soldier.IsOutOfAmmosFor(g0)))
                return true;

            for (int slot = 1; slot <= 2; slot++)
            {
                GenericGun g = soldier.GetHeldGun(slot);
                if (g == null || !g.IsAtWeapon() || (g.GetCurrentAmmoCount() <= 0 && soldier.IsOutOfAmmosFor(g)))
                    continue;

                float engageWindow = Mathf.Max(55f, g.DamageRange() + 18f);
                if (dist > engageWindow)
                    continue;

                if (!soldier.isInHandsAnimation &&
                    !soldier.isInBodyAnimation &&
                    soldier.CanSwitchHeldItem &&
                    (!Photon.Pun.PhotonNetwork.InRoom || AiCore.TryConsumeBotRpcBudget(0.35f)))
                {
                    soldier.SetGunTriggerPressed(false);
                    soldier.SwitchTo(slot);
                }
                return true;
            }

            return false;
        }

        // =====================================================================
        // Vanilla AI Fix: Handheld AT vs. Primary Firearm Selection (SoldierAI.UseBestWearedWeaponCheck)
        // In vanilla SoldierAI.UseBestWearedWeaponCheck (0x180561950):
        // 1) Vanilla only checks if visibleTarget is VehicleTank (0x180561B2E). When targeting an armored
        //    VehicleWithWheels (halftrack / armored car), vanilla thinks the target is NOT a vehicle and
        //    switches the soldier AWAY from their AT launcher back to their rifle (SwitchTo(1))!
        // 2) Conversely, if a non-isAtUnit soldier scavenged an AT launcher into Slot 0 (or switched to an
        //    AT weapon against armor) and now targets foot infantry, ensure they switch back to a standard
        //    firearm/sidearm if they have one with ammo.
        // =====================================================================
        [HarmonyPatch(typeof(SoldierAI), nameof(SoldierAI.UseBestWearedWeaponCheck))]
        public static class SoldierAI_UseBestWearedWeaponCheck_Patch
        {
            public static bool Prefix(SoldierAI __instance)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value || __instance == null)
                        return true;

                    Soldier soldier = __instance.character;
                    if (!AiCore.IsLocallyOwnedBot(soldier) ||
                        soldier.IsOnVehicle() ||
                        soldier.isInHandsAnimation ||
                        soldier.isInBodyAnimation ||
                        !soldier.CanSwitchHeldItem)
                    {
                        return true;
                    }

                    Spottable vt = __instance.visibleTarget;
                    if (vt != null && vt.Pointer != IntPtr.Zero && vt.IsWheeledVehicleOrTank() && !vt.IsAirVehicle())
                    {
                        Vehicle targetVeh = vt.TryCast<Vehicle>();
                        if (targetVeh != null &&
                            !targetVeh.IsStatic() &&
                            GetVehicleMaxHullArmorMm(targetVeh) >= 6.0f &&
                            soldier.transform != null)
                        {
                            float dist = Vector3.Distance(soldier.transform.position, vt.GetPosition());
                            if (TryEquipOrHoldStowedAtWeaponAgainstVehicle(soldier, targetVeh, dist))
                            {
                                return false;
                            }
                        }
                    }
                    else
                    {
                        // Target is foot infantry (or no target): if currently holding a dedicated AT weapon in Slot 0,
                        // switch back to a non-AT primary/secondary firearm if one has ammo!
                        GenericGun g0 = soldier.GetHeldGun(0);
                        if (g0 != null && g0.IsAtWeapon())
                        {
                            for (int slot = 1; slot <= 2; slot++)
                            {
                                GenericGun altGun = soldier.GetHeldGun(slot);
                                if (altGun != null &&
                                    !altGun.IsAtWeapon() &&
                                    (altGun.GetCurrentAmmoCount() > 0 || !soldier.IsOutOfAmmosFor(altGun)))
                                {
                                    if (!Photon.Pun.PhotonNetwork.InRoom || AiCore.TryConsumeBotRpcBudget(0.35f))
                                    {
                                        soldier.SetGunTriggerPressed(false);
                                        soldier.SwitchTo(slot);
                                    }
                                    return false;
                                }
                            }
                        }
                    }
                }
                catch { }
                return true;
            }
        }

        internal static bool IsTankThreateningPosition(Vehicle tankVeh, Vector3 observerPos, float maxThreatDist = 85f)
        {
            if (tankVeh == null || tankVeh.Pointer == IntPtr.Zero || !tankVeh.CanFight() || tankVeh.transform == null)
                return false;

            Vector3 tankPos = tankVeh.transform.position;
            Vector3 toObs = observerPos - tankPos;
            float distSq = toObs.sqrMagnitude;
            if (distSq > maxThreatDist * maxThreatDist)
                return false;

            // 1. Point-blank crush / hull-traverse danger zone
            if (distSq <= 14.0f * 14.0f)
                return true;

            float dist3D = Mathf.Sqrt(Mathf.Max(0.01f, distSq));
            Vector3 dir3D = toObs / dist3D;

            // 2. Closing hull moving toward the soldier within 24m
            if (dist3D <= 24.0f)
            {
                MovableVehicle mv = tankVeh.TryCast<MovableVehicle>();
                if (mv != null && mv.rb != null && Vector3.Dot(mv.rb.velocity, dir3D) > 1.2f)
                    return true;
            }

            // 3. Unbuttoned / open-top spotter within 14m ear/peripheral range with unobstructed LOS
            if (dist3D <= 14.0f)
            {
                AiCore.VehicleFastHandles vh = AiCore.GetOrCreateVehicleHandles(tankVeh);
                string tankFaction = GetVehicleCrewFaction(tankVeh);
                AiCore.GetCachedVehicleSpotterStatus(vh, tankFaction, Time.time, out bool hasOpenSpotter, out _);
                if (hasOpenSpotter &&
                    !AiCore.RaycastEnvironment(tankPos + Vector3.up * 2.1f, dir3D, Mathf.Max(0.5f, dist3D - 1.5f), tankVeh.transform.root, out _))
                {
                    return true;
                }
            }

            // 4. Staffed turret/hull-MG firing arcs, Commander periscope look cone, or active target lock near the observer
            var seats = tankVeh.seats;
            if (seats != null)
            {
                Vector3 toObsFlat = new Vector3(toObs.x, 0f, toObs.z);
                Vector3 dirToObsFlat = toObsFlat.sqrMagnitude > 0.001f ? toObsFlat.normalized : Vector3.forward;
                Turret mainTurret = tankVeh.GetMainTurret(false);
                int seatCount = seats.Length;
                for (int i = 0; i < seatCount; i++)
                {
                    Seats seat = seats[i];
                    if (seat == null || seat.unitSet == null || !seat.unitSet.IsAlive || seat.connectedTurret == null)
                        continue;

                    Turret turret = seat.connectedTurret;
                    if (turret.autoPilot != null &&
                        AiCore.TryGetSpottableCenter(turret.autoPilot.CurrentVisibleTarget, out Vector3 vtPos) &&
                        (vtPos - observerPos).sqrMagnitude <= 14.0f * 14.0f)
                    {
                        return true;
                    }

                    Vector3 fireDir = turret.GetFireDir();
                    Vector3 fireDirFlat = new Vector3(fireDir.x, 0f, fireDir.z);
                    if (fireDirFlat.sqrMagnitude > 0.001f)
                    {
                        AiCore.TurretFastTraits tTraits = AiCore.GetCachedTurretTraits(turret);
                        float halfCone = tTraits.IsCommanderTurret
                            ? (dist3D <= 52.0f ? 62f : 42f)
                            : ((mainTurret != null && turret.Pointer == mainTurret.Pointer) ? 45f : 36f);
                        if (Vector3.Angle(fireDirFlat.normalized, dirToObsFlat) <= halfCone)
                            return true;
                    }
                }
            }

            return false;
        }

        private static bool TryStalkTankBlindFlank(SoldierAI ai, Soldier soldier, Vehicle targetVeh, Vector3 myPos, float dist, float now)
        {
            if (ai == null || soldier == null || targetVeh == null || targetVeh.transform == null)
                return false;

            // If already holding a ranged AT rocket launcher (Bazooka/Panzerschreck/Panzerfaust/PIAT) in Slot 0,
            // crouch in place or fire from cover rather than running closer!
            GenericGun g0 = soldier.GetHeldGun(0);
            if (g0 != null && g0.IsAtWeapon() && dist <= Mathf.Max(35f, g0.DamageRange()))
            {
                if (!ai.IsMoving())
                    SetBotPose(ai, soldier, SoldierPose.Crouch);
                return false;
            }

            bool tankLookingAtMe = IsTankThreateningPosition(targetVeh, myPos, 52f);
            if (dist <= 12.5f)
            {
                // Inside the tank hull's 11.5m dead-angle depression blind zone: stay crouched while closing for the throw/plant!
                if (soldier.m_pose == SoldierPose.Idle)
                    SetBotPose(ai, soldier, SoldierPose.Crouch, true);
                return false;
            }

            if (tankLookingAtMe)
            {
                // Tank turret or Commander periscope is sweeping over us: drop to Crouch (or Prone if stationary in cover)
                // so we don't sprint upright through the tank's optics!
                if (!ai.IsMoving() && soldier.IsOnCover() && dist > 18.0f)
                    SetBotPose(ai, soldier, SoldierPose.Prone);
                else if (soldier.m_pose == SoldierPose.Idle)
                    SetBotPose(ai, soldier, SoldierPose.Crouch, true);
            }

            // When closing from 15m-42m with close-assault AT grenades/satchels/mines, arc 55 degrees off the main turret's
            // sightline toward the tank's blind flank/rear instead of running straight down the cannon barrel!
            Turret mainTurret = targetVeh.GetMainTurret(false);
            Vector3 turretLook = mainTurret != null ? mainTurret.GetFireDir() : targetVeh.transform.forward;
            turretLook.y = 0f;
            if (turretLook.sqrMagnitude < 0.01f)
                turretLook = Vector3.forward;
            else
                turretLook.Normalize();

            Vector3 tankPos = targetVeh.transform.position;
            Vector3 fromTankToMe = myPos - tankPos;
            fromTankToMe.y = 0f;
            if (fromTankToMe.sqrMagnitude > 0.25f && Vector3.Angle(turretLook, fromTankToMe.normalized) < 52.0f)
            {
                AiCore.SoldierFastHandles sh = AiCore.GetOrCreateSoldierHandles(soldier);
                if (sh != null && now >= sh.Stance.NextLocalCoverSeekTime)
                {
                    sh.Stance.NextLocalCoverSeekTime = now + 2.4f;
                    Vector3 right = new Vector3(turretLook.z, 0f, -turretLook.x);
                    float side = Vector3.Dot(fromTankToMe, right) >= 0f ? 1f : -1f;
                    Vector3 blindFlankDir = (-turretLook * 0.45f + right * (0.89f * side)).normalized;
                    Vector3 flankPoint = tankPos + blindFlankDir * Mathf.Clamp(dist * 0.72f, 11.0f, 22.0f);
                    bool hasWater = AiCore.TryGetActiveWaterLevel(out float waterY);
                    if (AiCore.TrySnapWalkableGroundProbe(myPos, flankPoint, 10.0f, 22.0f, 0.55f, 2.5f, hasWater, waterY, out Vector3 snappedFlank))
                    {
                        SetBotPose(ai, soldier, tankLookingAtMe ? SoldierPose.Crouch : SoldierPose.Idle, true);
                        AiCore.MoveBotTowardPosition(ai, soldier, snappedFlank, 1.5f, 2.4f);
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool TryEvadeArmorToCover(SoldierAI ai, Soldier soldier, Vehicle targetVeh, float dist)
        {
            if (ai == null || !AiCore.IsLocallyOwnedBot(soldier) || targetVeh == null || targetVeh.transform == null || soldier.transform == null)
                return false;

            if (dist > 85f || soldier.IsOnVehicle() || soldier.UsingBipod() || soldier.isInBodyAnimation)
                return false;

            IntPtr ptr = soldier.Pointer;
            float now = Time.time;
            Vector3 myPos = soldier.transform.position;

            // Defer cleanly if the soldier is already executing a GOAP/individual fallback sprint OR sprinting to grab a nearby fallen AT weapon!
            if (IsInIndividualOrGoapFallback(ptr, now) ||
                (soldier.GetSuppressionValue() < 62f && HasPendingAtWeaponScavenge(ptr, myPos, 24.0f)))
            {
                return false;
            }

            // If the soldier carries any usable AT capability (launcher, AT grenade, satchel, Molotov, Gammon) within assault range,
            // stalk the tank's blind flank / low-crawl in its dead zone rather than fleeing!
            if (dist <= 42f && HasUsableAntiTankCapability(soldier))
            {
                TryStalkTankBlindFlank(ai, soldier, targetVeh, myPos, dist, now);
                return false;
            }

            bool tankThreatening = IsTankThreateningPosition(targetVeh, myPos, 85f);
            if (!tankThreatening)
            {
                // Tank & Commander are currently looking away: stay low and quiet in cover/concealment so we aren't spotted!
                if (dist <= 52f && !ai.IsMoving())
                {
                    SetBotPose(ai, soldier, (dist <= 36f && !soldier.IsOnCover()) ? SoldierPose.Prone : SoldierPose.Crouch);
                }
                return false;
            }

            Vector3 toTank = targetVeh.transform.position - myPos;
            toTank.y = 0f;
            Vector3 threatDir = toTank.sqrMagnitude > 0.01f ? toTank.normalized : soldier.transform.forward;
            Transform ignoreRoot = soldier.transform.root;

            // If already sheltered behind a front wall relative to the tank, stay low and concealed
            if (IsPositionShelteredByFrontCover(myPos, threatDir, ignoreRoot))
            {
                if (!ai.IsMoving())
                    SetBotPose(ai, soldier, dist <= 34f ? SoldierPose.Prone : SoldierPose.Crouch);
                return false;
            }

            // If already in cover (trench, crater, building) when the tank is looking our way, drop Prone to stay hidden!
            if (soldier.IsOnCover() && !ai.IsMoving())
            {
                SetBotPose(ai, soldier, dist <= 48f ? SoldierPose.Prone : SoldierPose.Crouch);
                return false;
            }

            // 1. If standing on the exposed front face of a wall, duck around to the leeward side immediately
            if (TryFindShelteredSideOfWall(myPos, threatDir, ignoreRoot, out Vector3 behindWallPos))
            {
                SetBotPose(ai, soldier, SoldierPose.Idle, true);
                AiCore.MoveBotTowardPosition(ai, soldier, behindWallPos, 0f, 2.4f);
                AiCore.LogDebug("AntiArmor", $"{soldier.faction} rifleman scrambling behind wall to evade {targetVeh.name} at {dist:F0}m.", 6.0f);
                return true;
            }

            // 2. Search for local cover around the soldier's current position (or assigned cover)
            if (!soldier.IsOnCover() && TrySeekNearbyCover(ai, soldier))
            {
                if (ai.IsMoving() || ai.HasPathRequest)
                {
                    SetBotPose(ai, soldier, SoldierPose.Idle, true);
                    return true;
                }
            }

            // 3. If caught in the open within 68m with no way to hurt the tank, bound diagonally away toward cover
            if (!soldier.IsOnCover() && !ai.IsMoving() && !ai.HasPathRequest && dist <= 68f)
            {
                Vector3 away = -threatDir;
                Vector3 right = new Vector3(threatDir.z, 0f, -threatDir.x);
                float sideSign = (soldier.Pointer.ToInt64() & 1L) == 0L ? 1f : -1f;

                Vector3 fleeDir1 = (away * 0.78f + right * (0.62f * sideSign)).normalized;
                Vector3 fleeDir2 = (away * 0.78f - right * (0.62f * sideSign)).normalized;

                Vector3 chosenDir = Vector3.zero;
                float fleeDist = 8.5f;
                Vector3 rayOrigin = myPos + Vector3.up * 0.48f;
                if (!AiCore.IsMovementBlockedByObstacle(rayOrigin, fleeDir1, 8.5f))
                    chosenDir = fleeDir1;
                else if (!AiCore.IsMovementBlockedByObstacle(rayOrigin, fleeDir2, 8.5f))
                    chosenDir = fleeDir2;
                else if (!AiCore.IsMovementBlockedByObstacle(rayOrigin, away, 8.5f))
                    chosenDir = away;
                else
                {
                    fleeDist = 3.5f;
                    if (!AiCore.IsMovementBlockedByObstacle(rayOrigin, fleeDir1, 3.5f))
                        chosenDir = fleeDir1;
                    else if (!AiCore.IsMovementBlockedByObstacle(rayOrigin, fleeDir2, 3.5f))
                        chosenDir = fleeDir2;
                    else if (!AiCore.IsMovementBlockedByObstacle(rayOrigin, away, 3.5f))
                        chosenDir = away;
                }

                if (chosenDir.sqrMagnitude > 0.01f)
                {
                    Vector3 retreatPos = myPos + chosenDir * fleeDist;
                    SetBotPose(ai, soldier, SoldierPose.Idle, true);
                    AiCore.MoveBotTowardPosition(ai, soldier, retreatPos, 0f, 1.4f);
                    AiCore.LogDebug("AntiArmor", $"{soldier.faction} infantryman fleeing open ground from {targetVeh.name} ({dist:F0}m).", 6.0f);
                    return true;
                }
            }

            return false;
        }

        // =====================================================================
        // Sheltered Tank & Vehicle Crew Repair Patches (VehicleRepairTask)
        // =====================================================================
        [HarmonyPatch(typeof(VehicleRepairTask), nameof(VehicleRepairTask.GetCoverPosition))]
        public static class VehicleRepairTask_GetCoverPosition_Patch
        {
            public static void Postfix(VehicleRepairTask __instance, ref Vector3 __result)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value ||
                        !VisionConfig.EnableSmartTankCrewRepair.Value ||
                        __instance == null)
                    {
                        return;
                    }

                    Vehicle veh = __instance.vehToRepair;
                    if (veh == null || veh.Pointer == IntPtr.Zero || veh.transform == null)
                        return;

                    Vector3 shelterPos = GetShelteredRepairPosition(veh, null, out _);
                    __instance.targetPos = shelterPos;
                    __instance.pose = SoldierPose.Crouch;
                    __result = shelterPos;
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(VehicleRepairTask), nameof(VehicleRepairTask.IsReached))]
        public static class VehicleRepairTask_IsReached_Patch
        {
            public static void Postfix(VehicleRepairTask __instance, Vector3 __0, ref bool __result)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value ||
                        !VisionConfig.EnableSmartTankCrewRepair.Value ||
                        __instance == null ||
                        !__instance.VehicleStillneedsRepair)
                    {
                        return;
                    }

                    Vehicle veh = __instance.vehToRepair;
                    if (veh == null || veh.Pointer == IntPtr.Zero || veh.transform == null)
                        return;

                    IntPtr taskPtr = __instance.Pointer;
                    if (!_repairTaskStartTime.TryGetValue(taskPtr, out float startTime))
                    {
                        startTime = Time.time;
                        _repairTaskStartTime[taskPtr] = startTime;
                    }

                    Vector3 shelterPos = GetShelteredRepairPosition(veh, null, out Vector3 shelterDir);
                    Vector3 vehPos = veh.transform.position;

                    // Even if vanilla IsReached returned false (e.g. long tank hull where shelterPos is 3.2m from center),
                    // consider the repair task reached as soon as the crewman is within 2.35m of shelterPos!
                    Vector3 toShelter = __0 - shelterPos;
                    toShelter.y = 0f;
                    if (toShelter.sqrMagnitude <= 2.35f * 2.35f && Mathf.Abs(__0.y - vehPos.y) <= 3.2f)
                    {
                        __result = true;
                        return;
                    }

                    if (!__result)
                        return;

                    if (Time.time - startTime > 5.5f)
                        return;

                    Vector3 toSoldier = __0 - vehPos;
                    toSoldier.y = 0f;
                    if (toSoldier.sqrMagnitude > 0.04f && Vector3.Dot(toSoldier.normalized, shelterDir) >= 0.25f)
                        return;

                    __result = false;
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(VehicleRepairTask), nameof(VehicleRepairTask.OnCoverReached))]
        public static class VehicleRepairTask_OnCoverReached_Patch
        {
            public static bool Prefix(VehicleRepairTask __instance, Soldier __0)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value ||
                        !VisionConfig.EnableSmartTankCrewRepair.Value ||
                        __instance == null ||
                        __0 == null)
                    {
                        return true;
                    }

                    if (!__instance.VehicleStillneedsRepair || !__0.IsAI() || !AiCore.IsLocallyOwnedBot(__0))
                        return true;

                    // Ensure the crewman automatically remounts his tank/vehicle once repair finishes!
                    __instance.getOnVehicleAfter = true;

                    if (__0.aiController != null)
                    {
                        SetBotPose(__0.aiController, __0, SoldierPose.Crouch);
                        __0.SetGunTriggerPressed(false);

                        // Tank repair is absolute priority for dismounted crews—only abort for point-blank self-defense (< 6.0m)
                        if (__0.aiController.HasEnemyAndCanFireAtHim &&
                            __0.aiController.distFromTarget > 0.1f &&
                            __0.aiController.distFromTarget < 6.0f)
                        {
                            return false;
                        }
                    }
                    else
                    {
                        __0.SetPose(SoldierPose.Crouch, true, false, 0.2f);
                    }

                    if (_countedRepairTasks.Add(__instance.Pointer))
                    {
                        ShelteredRepairsCount++;
                    }
                }
                catch { }
                return true;
            }
        }

        private static bool TryRouteVehicleToRepairStation(AIVehicle aiVeh, Vehicle veh, float now)
        {
            if (aiVeh == null || veh == null || veh.Pointer == IntPtr.Zero || veh.transform == null)
                return false;

            if (!AiCore.IsLocallyOwnedVehicle(veh) ||
                veh.IsDisabled() ||
                veh.IsAirVehicle() ||
                veh.PlayerIsInside() ||
                veh.PlayerIsDriving() ||
                !veh.HasDriver())
            {
                _vehiclesSeekingRepairStation.Remove(veh.Pointer);
                return false;
            }

            MovableVehicle mov = veh.TryCast<MovableVehicle>();
            if (mov == null)
                return false;

            // Cannot drive to a repair station if tracks or engine are 100% broken (crew must field-repair first)
            float tracksDmg = mov.GetPercentageDamageFor(VehicleInternalPart.tracks);
            float engineDmg = mov.GetPercentageDamageFor(VehicleInternalPart.engine);
            if (tracksDmg >= 0.98f || engineDmg >= 0.98f)
            {
                _vehiclesSeekingRepairStation.Remove(veh.Pointer);
                return false;
            }

            RefreshCombinedArmsIntelIfNeeded();
            if (_activeRepairStations.Count == 0)
            {
                _vehiclesSeekingRepairStation.Remove(veh.Pointer);
                return false;
            }

            float hullDmg = mov.GetPercentageDamageFor(VehicleInternalPart.hull);
            float fuelDmg = mov.GetPercentageDamageFor(VehicleInternalPart.fuelTank);
            float maxPartDamage = Mathf.Max(Mathf.Max(hullDmg, fuelDmg), Mathf.Max(engineDmg, tracksDmg));

            Turret mainTurret = veh.GetMainTurret(false);
            bool outOfTurretAmmo = false;
            if (mainTurret != null && mainTurret.WeaponsCount() > 0)
            {
                mainTurret.CheckBullets(out bool hasHe, out bool hasAp, out bool hasFire, out bool hasFlak);
                outOfTurretAmmo = !hasHe && !hasAp && !hasFire && !hasFlak;
            }

            IntPtr vehPtr = veh.Pointer;
            bool alreadySeeking = _vehiclesSeekingRepairStation.TryGetValue(vehPtr, out RepairStation activeStation) &&
                                  activeStation != null && activeStation.Pointer != IntPtr.Zero &&
                                  activeStation.gameObject != null && activeStation.gameObject.activeInHierarchy &&
                                  activeStation.transform != null;

            bool needsStation = alreadySeeking
                ? (outOfTurretAmmo || maxPartDamage > 0.02f || veh.CanRefillAmmmo())
                : (outOfTurretAmmo || maxPartDamage >= 0.35f);

            if (!needsStation)
            {
                if (alreadySeeking)
                {
                    _vehiclesSeekingRepairStation.Remove(vehPtr);
                    Squad sq = aiVeh.squadInside;
                    if (sq != null && !AiCore.HasAnyHumanPlayerInSquad(sq) && sq.Leader != null)
                    {
                        sq.Leader_OrderAttackCurrentTask(sq.Leader);
                    }
                    aiVeh.rerollDestination = true;
                    if (VisionConfig.DebugLogging.Value)
                    {
                        AiCore.LogDebug("RepairStation", $"Vehicle finished repairing/re-arming at RepairStation and is returning to battle.");
                    }
                }
                return false;
            }

            Vector3 vehPos = veh.transform.position;
            string myFaction = GetVehicleCrewFaction(veh, mainTurret);
            RepairStation bestStation = alreadySeeking ? activeStation : null;

            if (bestStation != null && (bestStation.transform.position - vehPos).sqrMagnitude > 420.0f * 420.0f)
            {
                _vehiclesSeekingRepairStation.Remove(vehPtr);
                bestStation = null;
            }

            if (bestStation == null)
            {
                // Cap repair-station detours to 360m when completely out of main turret ammo (240m for partial damage)
                // so armor never abandons the frontline to drive 700m+ across large Conquest/Operation maps!
                float maxSearchDist = outOfTurretAmmo ? 360.0f : 240.0f;
                float bestDistSq = maxSearchDist * maxSearchDist;
                for (int i = 0; i < _activeRepairStations.Count; i++)
                {
                    RepairStation st = _activeRepairStations[i];
                    if (st == null || st.Pointer == IntPtr.Zero || st.gameObject == null || !st.gameObject.activeInHierarchy || st.transform == null)
                        continue;

                    Vector3 stPos = st.transform.position;
                    float dSq = (stPos - vehPos).sqrMagnitude;
                    if (dSq >= bestDistSq)
                        continue;

                    bool overrun = false;
                    if (!string.IsNullOrEmpty(myFaction))
                    {
                        for (int c = 0; c < _knownEnemyClusters.Count; c++)
                        {
                            if (!AiCore.AreSameSideFactions(_knownEnemyClusters[c].Faction, myFaction) &&
                                (_knownEnemyClusters[c].Position - stPos).sqrMagnitude <= 40.0f * 40.0f)
                            {
                                overrun = true;
                                break;
                            }
                        }
                    }
                    if (overrun)
                        continue;

                    bestDistSq = dSq;
                    bestStation = st;
                }
            }

            if (bestStation == null)
                return false;

            _vehiclesSeekingRepairStation[vehPtr] = bestStation;
            Vector3 stationPos = bestStation.transform.position;
            float stationRadius = Mathf.Max(5.0f, bestStation.radius);
            float distToStation = Vector3.Distance(vehPos, stationPos);

            if (distToStation <= stationRadius * 0.85f)
            {
                // Parked inside the RepairStation circle: hold brake while RepairStation.Update repairs parts & refills turrets!
                float holdUntil = now + 1.0f;
                if (aiVeh.resumeDriveTime < holdUntil)
                    aiVeh.resumeDriveTime = holdUntil;
                return true;
            }

            Squad crewSq = aiVeh.squadInside;
            if (crewSq != null && !AiCore.HasAnyHumanPlayerInSquad(crewSq))
            {
                if (crewSq.order == Order.follow)
                    crewSq.order = Order.defend;
                crewSq.moveOrderPosition = stationPos;
                crewSq.moveOrderRadius = Mathf.Max(4.0f, stationRadius * 0.65f);
            }

            if ((aiVeh.setVehicleDestination - stationPos).sqrMagnitude > 4.0f * 4.0f)
            {
                aiVeh.setVehicleDestination = stationPos;
                aiVeh.rerollDestination = false;
                if (TryRecordCombinedArmsAssist(vehPtr, now))
                {
                    AiCore.LogDebug(
                        "RepairStation",
                        $"{myFaction ?? "AI"} vehicle routing to RepairStation ({distToStation:F0}m away, dmg:{maxPartDamage * 100f:F0}%, dryAmmo:{outOfTurretAmmo}).");
                }
            }

            return true;
        }

        private static void TrySteerVehicleTowardKnownThreat(AIVehicle aiVeh, Vehicle veh, float now)
        {
            if (aiVeh == null || veh == null || veh.Pointer == IntPtr.Zero || veh.transform == null)
                return;

            // 1. Route damaged or out-of-ammo vehicles to a nearby safe RepairStation
            if (TryRouteVehicleToRepairStation(aiVeh, veh, now))
                return;

            if (!VisionConfig.EnableCombinedArmsPrioritySupport.Value ||
                !AiCore.IsLocallyOwnedVehicle(veh) ||
                !veh.CanFight() ||
                veh.IsAirVehicle() ||
                veh.PlayerIsInside() ||
                veh.PlayerIsDriving())
            {
                return;
            }

            MovableVehicle mv = veh.TryCast<MovableVehicle>();
            if (mv == null)
                return;

            Turret mainTurret = veh.GetMainTurret(false);
            if (mainTurret == null)
                return;

            string myFaction = GetVehicleCrewFaction(veh, mainTurret);
            if (string.IsNullOrEmpty(myFaction))
                return;

            RefreshCombinedArmsIntelIfNeeded();

            IntPtr vehPtr = veh.Pointer;
            Vector3 vehPos = veh.transform.position;
            Vector3 squadDest = aiVeh.GetCurrentSquadDestination();
            Squad crewSq = aiVeh.squadInside;

            // 2. Answer explicit friendly RadioRequest.tankSupport calls when within 320m;
            //    otherwise let vanilla AIVehicle road/node pathfinding pilot the vehicle naturally!
            Vector3 bestFocusPos = Vector3.zero;
            float bestFocusScore = -999f;
            Squad focusSupportSquad = null;

            for (int r = 0; r < _activeRadioRequests.Count; r++)
            {
                ActiveRadioSupportInfo req = _activeRadioRequests[r];
                if (req.RequestType != RadioRequest.tankSupport || !AiCore.AreSameSideFactions(req.Faction, myFaction))
                    continue;

                float distToDest = Vector3.Distance(squadDest, req.Position);
                if (distToDest > 320.0f)
                    continue;

                float score = 95.0f - (distToDest * 0.15f);
                if (score > bestFocusScore)
                {
                    bestFocusScore = score;
                    bestFocusPos = req.Position;
                    focusSupportSquad = req.SourceSquad;
                }
            }

            float maxAllowedOffset = Mathf.Max(15.0f, aiVeh.GetCurrentSquadDestinationAreaSize() * 0.84f);
            bool answeringFarTankSupport = focusSupportSquad != null && Vector3.Distance(squadDest, bestFocusPos) > maxAllowedOffset;
            if (!answeringFarTankSupport)
            {
                if (_vehiclesAnsweringTankSupport.Remove(vehPtr))
                {
                    if (crewSq != null && !AiCore.HasAnyHumanPlayerInSquad(crewSq) && crewSq.Leader != null &&
                        (!Photon.Pun.PhotonNetwork.InRoom || AiCore.TryConsumeBotRpcBudget(0.45f)))
                    {
                        crewSq.Leader_OrderAttackCurrentTask(crewSq.Leader);
                    }
                    aiVeh.rerollDestination = true;
                }
                return;
            }

            Vector3 fromFocusToTank = vehPos - bestFocusPos;
            fromFocusToTank.y = 0f;
            Vector3 standoffDir = fromFocusToTank.sqrMagnitude > 0.25f ? fromFocusToTank.normalized : -veh.transform.forward;
            Vector3 desiredDest = bestFocusPos + standoffDir * 38.0f;
            if (Physics.Raycast(
                    new Vector3(desiredDest.x, bestFocusPos.y + 22.0f, desiredDest.z),
                    Vector3.down,
                    out RaycastHit sHit,
                    50.0f,
                    AiCore.GetSolidEnvironmentMask(),
                    QueryTriggerInteraction.Ignore) &&
                sHit.normal.y >= 0.70f)
            {
                desiredDest = sHit.point;
            }
            else
            {
                desiredDest.y = bestFocusPos.y;
            }

            bool destChanged = (aiVeh.setVehicleDestination - desiredDest).sqrMagnitude > 10.0f * 10.0f;
            if (destChanged || !_vehiclesAnsweringTankSupport.Contains(vehPtr))
            {
                _vehiclesAnsweringTankSupport.Add(vehPtr);
                if (crewSq != null && !AiCore.HasAnyHumanPlayerInSquad(crewSq))
                {
                    bool isTank = veh.TryCast<VehicleTank>() != null;
                    if (crewSq.order == Order.follow)
                        crewSq.order = Order.defend;
                    crewSq.moveOrderPosition = desiredDest;
                    crewSq.moveOrderRadius = isTank ? 46.0f : 34.0f;
                }
            }

            if (destChanged)
            {
                aiVeh.setVehicleDestination = desiredDest;
                aiVeh.rerollDestination = false;
                _claimedVehicleDestinations[vehPtr] = desiredDest;

                Vector3? listenerPos = (focusSupportSquad.Leader != null && focusSupportSquad.Leader.transform != null)
                    ? focusSupportSquad.Leader.transform.position
                    : (Vector3?)desiredDest;
                string armorLabel = AiCore.FormatVehicleSupportCallsign(veh, crewSq, listenerPos);
                AiCore.TryShowLocalPlayerRadioHint(
                    focusSupportSquad,
                    $"Radio Net: {armorLabel} moving to support your position",
                    requireLeader: false,
                    minInterval: 22.0f);

                if (TryRecordCombinedArmsAssist(vehPtr, now))
                {
                    AiCore.LogDebug(
                        "CombArmsTank",
                        $"{myFaction} combat vehicle answering friendly tank support radio request.");
                }
            }
        }

        // =====================================================================
        // Smart APC / Transport Dismounting Under Fire
        // =====================================================================
        private static void TryDismountTransportUnderThreat(Vehicle veh, AIVehicle aiVeh, bool forcedBySuppression)
        {
            if (veh == null || veh.Pointer == IntPtr.Zero || veh.transform == null)
                return;

            if (!AiCore.IsLocallyOwnedVehicle(veh) || !veh.IsTransportVehicle() || veh.IsAirVehicle() || veh.PlayerIsDriving())
                return;

            IntPtr vehPtr = veh.Pointer;
            float now = Time.time;
            if (_nextApcDismountCooldown.TryGetValue(vehPtr, out float cooldownUntil) && now < cooldownUntil)
                return;

            var seats = veh.seats;
            if (seats == null || seats.Length == 0)
                return;

            bool hasDismountablePassengers = false;
            string faction = null;
            for (int i = 0; i < seats.Length; i++)
            {
                Seats s = seats[i];
                if (s == null || s.isDriver || s.hasTurret)
                    continue;

                Soldier passenger = s.unitSet;
                if (passenger != null && passenger.IsAlive && !passenger.IsPlayer())
                {
                    if (passenger.dontLeaveVehicle || (passenger.Params != null && passenger.Params.neverLeaveVehicle))
                        continue;

                    hasDismountablePassengers = true;
                    if (string.IsNullOrEmpty(faction))
                        faction = passenger.faction;
                    break;
                }
            }

            if (!hasDismountablePassengers)
                return;

            bool shouldDismount = forcedBySuppression ||
                                  (aiVeh != null && aiVeh.hasEnemyTank) ||
                                  (aiVeh != null && aiVeh.SomeoneIsSuppressed()) ||
                                  (aiVeh != null && aiVeh.hasEnemy && veh.GetDamage() > 0);

            if (!shouldDismount && !string.IsNullOrEmpty(faction))
            {
                Vector3 vehPos = veh.transform.position;
                const float threatRadiusSq = 48.0f * 48.0f;

                AiCore.EnsureWorldSnapshot();
                int sqCount = AiCore.WorldSquadCount;
                var squads = AiCore.WorldSquads;
                for (int s = 0; s < sqCount; s++)
                {
                    ref AiCore.SquadSnapshot sqSnap = ref squads[s];
                    if (sqSnap.UnitCount == 0 ||
                        sqSnap.RefOnPlane ||
                        string.IsNullOrEmpty(sqSnap.Faction) ||
                        AiCore.AreSameSideFactions(sqSnap.Faction, faction))
                        continue;

                    if ((sqSnap.RefPos - vehPos).sqrMagnitude <= threatRadiusSq)
                    {
                        shouldDismount = true;
                        break;
                    }
                }
            }

            if (shouldDismount)
            {
                _nextApcDismountCooldown[vehPtr] = now + 8.0f;
                VehicleWithWheels wheeled = veh.TryCast<VehicleWithWheels>();
                if (wheeled != null)
                {
                    wheeled.Brake();
                }

                veh.DropInfantry(false);
                Squad sqInside = veh.GetSquadInside();
                if (sqInside != null && !AiCore.HasAnyHumanPlayerInSquad(sqInside))
                {
                    sqInside.OrderLeaveVehicle(veh, false);
                }

                SmartApcDismountsCount++;
                if (VisionConfig.DebugLogging.Value)
                {
                    AiCore.LogDebug("ApcDismount", $"Transport vehicle dismounted infantry passengers under combat threat.");
                }
            }
        }

        // =====================================================================
        // Tank Rock / Rubble Belly High-Center Crawl Assist (VehicleTank.FixedUpdate)
        // =====================================================================
        [HarmonyPatch(typeof(VehicleTank), nameof(VehicleTank.FixedUpdate))]
        public static class VehicleTank_FixedUpdate_Patch
        {
            public static void Prefix(out long __state)
            {
                __state = HitchProfiler.Begin();
            }

            public static void Postfix(VehicleTank __instance, long __state)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value ||
                        !VisionConfig.EnableTankRockUnstickAssist.Value ||
                        ReferenceEquals(__instance, null) ||
                        __instance == null ||
                        __instance.Pointer == IntPtr.Zero)
                    {
                        return;
                    }

                    AiCore.VehicleFastHandles vh = AiCore.GetOrCreateVehicleHandles(__instance);
                    if (vh == null)
                        return;

                    // Evaluate raycasts & damage/input state at most once per rendered frame per tank;
                    // replay only cached AddForce on subsequent FixedUpdate physics substeps.
                    int curFrame = Time.frameCount;
                    if (vh.UnstickLastFrame == curFrame)
                    {
                        if (vh.UnstickApplyForce)
                        {
                            Rigidbody cachedRb = __instance.rb;
                            if (cachedRb != null && !cachedRb.isKinematic)
                                cachedRb.AddForce(vh.UnstickForceVec, ForceMode.Acceleration);
                        }
                        return;
                    }

                    vh.UnstickLastFrame = curFrame;

                    if (!__instance.OwnDriverOrVehicle() ||
                        !__instance.engineStarted ||
                        __instance.IsDisabled() ||
                        __instance.transform == null)
                    {
                        vh.UnstickApplyForce = false;
                        return;
                    }

                    float throttleY = __instance.GetMovingDir().y;
                    if (Mathf.Abs(throttleY) <= 0.25f ||
                        __instance.GetPercentageDamageFor(VehicleInternalPart.tracks) >= 0.98f ||
                        __instance.GetPercentageDamageFor(VehicleInternalPart.engine) >= 0.98f)
                    {
                        vh.HighCenteredDuration = 0f;
                        vh.UnstickApplyForce = false;
                        return;
                    }

                    Rigidbody rb = __instance.rb;
                    if (rb == null || rb.isKinematic)
                    {
                        vh.UnstickApplyForce = false;
                        return;
                    }

                    float speedSq = rb.velocity.sqrMagnitude;
                    if (speedSq > 2.25f * 2.25f)
                    {
                        vh.HighCenteredDuration = 0f;
                        vh.UnstickApplyForce = false;
                        return;
                    }

                    if (speedSq < 0.85f * 0.85f)
                    {
                        vh.HighCenteredDuration += Mathf.Min(Time.deltaTime, 0.10f);
                    }
                    else if (vh.HighCenteredDuration < 1.40f)
                    {
                        vh.UnstickApplyForce = false;
                        return;
                    }

                    if (vh.HighCenteredDuration < 1.40f)
                    {
                        vh.UnstickApplyForce = false;
                        return;
                    }

                    Vector3 tankPos = __instance.transform.position;
                    Vector3 driveDir = throttleY >= 0f ? __instance.transform.forward : -__instance.transform.forward;
                    driveDir.y = 0f;
                    if (driveDir.sqrMagnitude < 0.001f)
                    {
                        vh.UnstickApplyForce = false;
                        return;
                    }
                    driveDir.Normalize();

                    if (AiCore.IsDirectionBlockedByEnvironment(tankPos + Vector3.up * 1.55f, driveDir, 4.2f, __instance.transform.root))
                    {
                        vh.UnstickApplyForce = false;
                        return;
                    }

                    Vector3 forceVec = driveDir * 5.8f + Vector3.up * 1.35f;
                    vh.UnstickApplyForce = true;
                    vh.UnstickForceVec = forceVec;
                    rb.AddForce(forceVec, ForceMode.Acceleration);

                    float now = Time.time;
                    if (now >= vh.NextUnstickCountTime)
                    {
                        vh.NextUnstickCountTime = now + 5.0f;
                        TankRockUnsticksCount++;
                        if (VisionConfig.DebugLogging.Value)
                        {
                            AiCore.LogDebug("TankUnstick", $"Applied sprocket/track rock-crawl assist to '{__instance.name}'.");
                        }
                    }
                }
                catch { }
                finally
                {
                    HitchProfiler.End(HitchBucket.VehFixedUpdate, __state);
                }
            }
        }
    }
}
