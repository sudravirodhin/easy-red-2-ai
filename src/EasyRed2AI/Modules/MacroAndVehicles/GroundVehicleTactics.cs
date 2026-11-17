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

            // 3. Unbuttoned commander / open-top / periscope spotter within 52m with unobstructed LOS
            if (dist3D <= 52.0f)
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

            // 4. Staffed turret/hull-MG firing arcs or active target lock near the observer
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
                        float halfCone = (mainTurret != null && turret.Pointer == mainTurret.Pointer) ? 48f : 38f;
                        if (Vector3.Angle(fireDirFlat.normalized, dirToObsFlat) <= halfCone)
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

            // If the soldier carries any usable AT capability (launcher, AT grenade, satchel, Molotov, Gammon) within assault range, close & engage!
            if (dist <= 42f && HasUsableAntiTankCapability(soldier))
                return false;

            // Only panic-evade when the tank is actually threatening our position (turret/MG cone, open commander LOS, or closing hull)
            if (!IsTankThreateningPosition(targetVeh, myPos, 85f))
                return false;
            Vector3 toTank = targetVeh.transform.position - myPos;
            toTank.y = 0f;
            Vector3 threatDir = toTank.sqrMagnitude > 0.01f ? toTank.normalized : soldier.transform.forward;
            Transform ignoreRoot = soldier.transform.root;

            // If already sheltered behind a front wall relative to the tank, stay low and concealed
            if (IsPositionShelteredByFrontCover(myPos, threatDir, ignoreRoot))
            {
                if (!ai.IsMoving())
                    SetBotPose(ai, soldier, SoldierPose.Crouch);
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
                    if (sq != null && !sq.IsPlayerInSquad() && sq.Leader != null)
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
            if (crewSq != null && !crewSq.IsPlayerInSquad())
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

        private static bool TryValidateVehicleGroundPoint(
            Vector3 candidateXZ,
            float refY,
            Vector3 squadDest,
            float maxAllowedOffset,
            Vector3 fwdAxis,
            Transform ignoreRoot,
            out Vector3 validPoint)
        {
            Vector3 offsetFromDest = candidateXZ - squadDest;
            offsetFromDest.y = 0f;
            float offsetMag = offsetFromDest.magnitude;
            if (offsetMag > maxAllowedOffset && offsetMag > 0.01f)
            {
                candidateXZ = squadDest + (offsetFromDest / offsetMag) * maxAllowedOffset;
            }

            Vector3 rayOrigin = new Vector3(candidateXZ.x, refY + 18.0f, candidateXZ.z);
            if (Physics.Raycast(rayOrigin, Vector3.down, out RaycastHit gHit, 42.0f, AiCore.GetSolidEnvironmentMask(), QueryTriggerInteraction.Ignore) &&
                gHit.normal.y >= 0.72f &&
                Mathf.Abs(gHit.point.y - refY) <= 9.5f &&
                !AiCore.IsDirectionBlockedByEnvironment(gHit.point + Vector3.up * 1.6f, fwdAxis, 3.8f, ignoreRoot))
            {
                validPoint = gHit.point;
                return true;
            }

            validPoint = Vector3.zero;
            return false;
        }

        private static void TrySteerVehicleTowardKnownThreat(AIVehicle aiVeh, Vehicle veh, float now)
        {
            if (aiVeh == null || veh == null || veh.Pointer == IntPtr.Zero || veh.transform == null)
                return;

            if (TryRouteVehicleToRepairStation(aiVeh, veh, now))
                return;

            if (!AiCore.IsLocallyOwnedVehicle(veh) ||
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

            bool isTank = veh.TryCast<VehicleTank>() != null;
            Turret mainTurret = veh.GetMainTurret(false);
            string myFaction = GetVehicleCrewFaction(veh, mainTurret);
            if (string.IsNullOrEmpty(myFaction))
                return;

            RefreshCombinedArmsIntelIfNeeded();

            IntPtr vehPtr = veh.Pointer;
            Vector3 vehPos = veh.transform.position;
            Vector3 squadDest = aiVeh.GetCurrentSquadDestination();

            // Count friendly ground vehicles operating on the same objective sector (<= 115m) and compute
            // a deterministic zero-GC echelon rank by VehPtr order so friendly tanks never stack hull-to-hull
            // as an easy double-kill for CAS bombing runs!
            int totalFriendlyNearObj = 1;
            int myRankAtObj = 0;
            bool physicallyHuggingPeer = false;
            long myId = vehPtr.ToInt64();

            for (int i = 0; i < _activeGroundVehicles.Count; i++)
            {
                ActiveGroundVehicleInfo other = _activeGroundVehicles[i];
                if (other.VehPtr == vehPtr || string.IsNullOrEmpty(other.Faction))
                    continue;

                if (!AiCore.AreSameSideFactions(myFaction, other.Faction))
                    continue;

                if ((other.Position - vehPos).sqrMagnitude <= 15.0f * 15.0f)
                {
                    physicallyHuggingPeer = true;
                }

                if ((other.TargetDest - squadDest).sqrMagnitude <= 115.0f * 115.0f ||
                    (other.Position - squadDest).sqrMagnitude <= 115.0f * 115.0f)
                {
                    totalFriendlyNearObj++;
                    if (other.VehPtr.ToInt64() < myId)
                        myRankAtObj++;
                }
            }

            // If the main turret already has a visible target in weapon range & LOS, let Standoff / TurretAI handle it
            // UNLESS this vehicle is a stacked wingman (myRankAtObj > 0) physically hugging a friendly tank (< 15m)
            // against a distant target (> 38m), in which case let it compute its anti-bombing flank offset!
            TurretAI turretAi = mainTurret != null ? mainTurret.GetTurretAI() : null;
            if (turretAi != null &&
                turretAi.CurrentVisibleTarget != null &&
                turretAi.targetInWeaponRange &&
                (turretAi.CanSee(turretAi.CurrentVisibleTarget) ||
                 CanTankShellIndoorTargetFacade(mainTurret, veh, turretAi.CurrentVisibleTarget)))
            {
                bool shouldPeelOffFromStackedTank = physicallyHuggingPeer &&
                                                    myRankAtObj > 0 &&
                                                    (turretAi.CurrentVisibleTarget.GetCenterOfUnit() - vehPos).sqrMagnitude > 38.0f * 38.0f;
                if (!shouldPeelOffFromStackedTank)
                    return;
            }

            // Widen cramped vanilla moveOrderRadius (often 15m-20m) when >= 2 friendly combat vehicles share
            // the same objective so AIVehicle.DestinationHasChanged permits 32m-42m anti-bombing dispersion!
            Squad crewSq = aiVeh.squadInside;
            if (totalFriendlyNearObj >= 2 &&
                crewSq != null &&
                !crewSq.IsPlayerInSquad() &&
                crewSq.order != Order.follow)
            {
                float desiredMinRadius = isTank ? 46.0f : 34.0f;
                if (crewSq.moveOrderRadius < desiredMinRadius)
                    crewSq.moveOrderRadius = desiredMinRadius;
            }

            float areaSize = aiVeh.GetCurrentSquadDestinationAreaSize();
            if (areaSize < 12.0f)
                return;

            Vector3 bestFocusPos = Vector3.zero;
            float bestFocusScore = -999f;
            bool hasFocus = false;
            bool focusIsTankSupport = false;
            Squad focusSupportSquad = null;

            if (VisionConfig.EnableCombinedArmsPrioritySupport.Value && mainTurret != null)
            {
                // 1. Check active friendly Tank Support / Artillery radio requests near the objective or vehicle
                for (int r = 0; r < _activeRadioRequests.Count; r++)
                {
                    ActiveRadioSupportInfo req = _activeRadioRequests[r];
                    if (!AiCore.AreSameSideFactions(req.Faction, myFaction) || req.RequestType == RadioRequest.artillerySmoke)
                        continue;

                    float distToDest = Vector3.Distance(squadDest, req.Position);
                    if (distToDest > 320.0f)
                        continue;

                    float score = (req.RequestType == RadioRequest.tankSupport ? 95.0f : 70.0f) - (distToDest * 0.15f);
                    if (score > bestFocusScore)
                    {
                        bestFocusScore = score;
                        bestFocusPos = req.Position;
                        hasFocus = true;
                        focusIsTankSupport = req.RequestType == RadioRequest.tankSupport;
                        focusSupportSquad = focusIsTankSupport ? req.SourceSquad : null;
                    }
                }

                // 2. Check known/marked enemy squads contesting or near the vehicle's objective sector
                int clusterCount = _knownEnemyClusters.Count;
                for (int i = 0; i < clusterCount; i++)
                {
                    KnownEnemyCluster cluster = _knownEnemyClusters[i];
                    if (AiCore.AreSameSideFactions(cluster.Faction, myFaction))
                        continue;

                    float distToDest = Vector3.Distance(squadDest, cluster.Position);
                    if (distToDest > 320.0f)
                        continue;

                    float score = cluster.BasePriority - (distToDest * 0.15f);
                    if (score > bestFocusScore)
                    {
                        bestFocusScore = score;
                        bestFocusPos = cluster.Position;
                        hasFocus = true;
                        focusIsTankSupport = false;
                        focusSupportSquad = null;
                    }
                }
            }

            float minPeerSpacing = isTank ? 28.0f : 19.0f;
            float minPeerSpacingSq = minPeerSpacing * minPeerSpacing;

            if (!hasFocus)
            {
                if (_vehiclesAnsweringTankSupport.Remove(vehPtr))
                {
                    if (crewSq != null && !crewSq.IsPlayerInSquad() && crewSq.Leader != null &&
                        (!Photon.Pun.PhotonNetwork.InRoom || AiCore.TryConsumeBotRpcBudget(0.45f)))
                    {
                        crewSq.Leader_OrderAttackCurrentTask(crewSq.Leader);
                    }
                    aiVeh.rerollDestination = true;
                }

                if (totalFriendlyNearObj < 2)
                    return;

                // Even before enemy clusters are spotted, check if our current destination crowds a friendly peer!
                Vector3 curDest = aiVeh.setVehicleDestination;
                bool isCrowdingPeer = physicallyHuggingPeer;
                if (!isCrowdingPeer)
                {
                    for (int i = 0; i < _activeGroundVehicles.Count; i++)
                    {
                        ActiveGroundVehicleInfo other = _activeGroundVehicles[i];
                        if (other.VehPtr == vehPtr || string.IsNullOrEmpty(other.Faction))
                            continue;
                        if (!AiCore.AreSameSideFactions(myFaction, other.Faction))
                            continue;

                        if ((other.TargetDest - curDest).sqrMagnitude < minPeerSpacingSq ||
                            (other.Position - curDest).sqrMagnitude < minPeerSpacingSq)
                        {
                            isCrowdingPeer = true;
                            break;
                        }
                    }
                }

                if (!isCrowdingPeer)
                    return;
            }

            float maxAllowedOffset = areaSize * 0.84f;
            bool answeringFarTankSupport = hasFocus && focusIsTankSupport && Vector3.Distance(squadDest, bestFocusPos) > maxAllowedOffset;
            if (!answeringFarTankSupport && _vehiclesAnsweringTankSupport.Remove(vehPtr))
            {
                if (crewSq != null && !crewSq.IsPlayerInSquad() && crewSq.Leader != null &&
                    (!Photon.Pun.PhotonNetwork.InRoom || AiCore.TryConsumeBotRpcBudget(0.45f)))
                {
                    crewSq.Leader_OrderAttackCurrentTask(crewSq.Leader);
                }
                aiVeh.rerollDestination = true;
            }

            Vector3 fwdAxis;
            Vector3 baseDest;

            if (hasFocus)
            {
                Vector3 toFocusFromDest = bestFocusPos - squadDest;
                toFocusFromDest.y = 0f;
                float focusDistFromDest = toFocusFromDest.magnitude;
                fwdAxis = focusDistFromDest > 0.25f ? (toFocusFromDest / focusDistFromDest) : veh.transform.forward;
                fwdAxis.y = 0f;
                fwdAxis = fwdAxis.sqrMagnitude > 0.001f ? fwdAxis.normalized : Vector3.forward;

                if (answeringFarTankSupport)
                {
                    Vector3 fromFocusToTank = vehPos - bestFocusPos;
                    fromFocusToTank.y = 0f;
                    Vector3 standoffDir = fromFocusToTank.sqrMagnitude > 0.25f ? fromFocusToTank.normalized : -fwdAxis;
                    baseDest = bestFocusPos + standoffDir * 38.0f;
                    if (Physics.Raycast(
                            new Vector3(baseDest.x, bestFocusPos.y + 22.0f, baseDest.z),
                            Vector3.down,
                            out RaycastHit sHit,
                            50.0f,
                            AiCore.GetSolidEnvironmentMask(),
                            QueryTriggerInteraction.Ignore) &&
                        sHit.normal.y >= 0.70f)
                    {
                        baseDest = sHit.point;
                    }
                    else
                    {
                        baseDest.y = bestFocusPos.y;
                    }
                }
                else if (focusDistFromDest <= maxAllowedOffset)
                {
                    // Threat is inside the objective area: pull back ~35m toward the approaching tank for standoff
                    Vector3 fromFocusToTank = vehPos - bestFocusPos;
                    fromFocusToTank.y = 0f;
                    Vector3 standoffDir = fromFocusToTank.sqrMagnitude > 0.25f ? fromFocusToTank.normalized : -fwdAxis;
                    baseDest = bestFocusPos + standoffDir * Mathf.Min(35.0f, maxAllowedOffset * 0.65f);

                    Vector3 offsetFromDest = baseDest - squadDest;
                    offsetFromDest.y = 0f;
                    if (offsetFromDest.magnitude > maxAllowedOffset)
                    {
                        baseDest = squadDest + offsetFromDest.normalized * maxAllowedOffset;
                    }
                }
                else
                {
                    // Threat is outside the objective circle: advance to the perimeter facing the known threat
                    baseDest = squadDest + fwdAxis * maxAllowedOffset * 0.88f;
                }
            }
            else
            {
                Vector3 approachVec = squadDest - vehPos;
                approachVec.y = 0f;
                fwdAxis = approachVec.sqrMagnitude > 0.25f ? approachVec.normalized : veh.transform.forward;
                fwdAxis.y = 0f;
                fwdAxis = fwdAxis.sqrMagnitude > 0.001f ? fwdAxis.normalized : Vector3.forward;
                baseDest = squadDest;
            }

            Vector3 desiredDest = baseDest;
            if (!answeringFarTankSupport)
                desiredDest.y = squadDest.y;

            Vector3 clampCenter = answeringFarTankSupport ? bestFocusPos : squadDest;
            float clampRefY = answeringFarTankSupport ? baseDest.y : squadDest.y;
            float clampMaxOffset = answeringFarTankSupport ? 60.0f : maxAllowedOffset;

            // Apply WW2 Armored Anti-Bombing Echelon / Breitkeil Wedge Dispersion when >= 2 friendly vehicles share the sector!
            if (totalFriendlyNearObj >= 2)
            {
                Vector3 rightAxis = new Vector3(fwdAxis.z, 0f, -fwdAxis.x);
                float latOffset;
                float depthOffset;

                if (totalFriendlyNearObj == 2)
                {
                    float flankDist = isTank ? 20.0f : 14.0f;
                    latOffset = (myRankAtObj == 0) ? -flankDist : flankDist; // 40m lateral tank interval!
                    depthOffset = (myRankAtObj == 0) ? 3.0f : -4.0f;
                }
                else
                {
                    if (myRankAtObj == 0)
                    {
                        latOffset = 0f;
                        depthOffset = 6.0f;
                    }
                    else
                    {
                        int wingPair = (myRankAtObj + 1) / 2;
                        float sign = (myRankAtObj & 1) == 1 ? 1f : -1f;
                        float step = isTank ? 31.0f : 21.0f;
                        latOffset = sign * wingPair * step;
                        depthOffset = -8.0f * wingPair;
                    }
                }

                Transform vehRoot = veh.transform.root;
                Vector3 candPrimary = baseDest + rightAxis * latOffset + fwdAxis * depthOffset;
                Vector3 candOpposite = baseDest - rightAxis * latOffset + fwdAxis * depthOffset;
                Vector3 candHalf = baseDest + rightAxis * (latOffset * 0.55f);

                if (TryValidateVehicleGroundPoint(candPrimary, clampRefY, clampCenter, clampMaxOffset, fwdAxis, vehRoot, out Vector3 valid1))
                {
                    desiredDest = valid1;
                }
                else if (Mathf.Abs(latOffset) > 1f &&
                         TryValidateVehicleGroundPoint(candOpposite, clampRefY, clampCenter, clampMaxOffset, fwdAxis, vehRoot, out Vector3 valid2))
                {
                    desiredDest = valid2;
                }
                else if (Mathf.Abs(latOffset) > 1f &&
                         TryValidateVehicleGroundPoint(candHalf, clampRefY, clampCenter, clampMaxOffset, fwdAxis, vehRoot, out Vector3 valid3))
                {
                    desiredDest = valid3;
                }

                // Final peer-destination repulsion pass so terrain clamping never folds two tanks back onto the same spot
                for (int i = 0; i < _activeGroundVehicles.Count; i++)
                {
                    ActiveGroundVehicleInfo other = _activeGroundVehicles[i];
                    if (other.VehPtr == vehPtr || other.VehPtr.ToInt64() > myId || string.IsNullOrEmpty(other.Faction))
                        continue;
                    if (!AiCore.AreSameSideFactions(myFaction, other.Faction))
                        continue;

                    Vector3 diff = desiredDest - other.TargetDest;
                    diff.y = 0f;
                    float dSq = diff.sqrMagnitude;
                    if (dSq < minPeerSpacingSq)
                    {
                        Vector3 pushDir = dSq > 0.25f ? diff.normalized : (rightAxis * ((myRankAtObj & 1) == 0 ? -1f : 1f));
                        Vector3 pushed = other.TargetDest + pushDir * minPeerSpacing;
                        if (TryValidateVehicleGroundPoint(pushed, clampRefY, clampCenter, clampMaxOffset, fwdAxis, vehRoot, out Vector3 validPushed))
                        {
                            desiredDest = validPushed;
                        }
                    }
                }
            }

            bool destChanged = (aiVeh.setVehicleDestination - desiredDest).sqrMagnitude > 10.0f * 10.0f;
            if (answeringFarTankSupport && (destChanged || !_vehiclesAnsweringTankSupport.Contains(vehPtr)))
            {
                _vehiclesAnsweringTankSupport.Add(vehPtr);
                if (crewSq != null && !crewSq.IsPlayerInSquad())
                {
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

                if (focusIsTankSupport && focusSupportSquad != null)
                {
                    Vector3? listenerPos = (focusSupportSquad.Leader != null && focusSupportSquad.Leader.transform != null)
                        ? focusSupportSquad.Leader.transform.position
                        : (Vector3?)desiredDest;
                    string armorLabel = AiCore.FormatVehicleSupportCallsign(veh, crewSq, listenerPos);
                    AiCore.TryShowLocalPlayerRadioHint(
                        focusSupportSquad,
                        $"Radio Net: {armorLabel} moving to support your position",
                        requireLeader: false,
                        minInterval: 22.0f);
                }

                if (totalFriendlyNearObj >= 2)
                {
                    if (!_nextArmorDispersionCountTime.TryGetValue(vehPtr, out float nextDispCount) || now >= nextDispCount)
                    {
                        _nextArmorDispersionCountTime[vehPtr] = now + 8.0f;
                        ArmoredDispersionAdjustmentsCount++;
                        if (VisionConfig.DebugLogging.Value)
                        {
                            AiCore.LogDebug(
                                "ArmorDispersion",
                                $"{myFaction} {(isTank ? "tank" : "combat vehicle")} dispersed to echelon slot #{myRankAtObj + 1}/{totalFriendlyNearObj} (anti-CAS flank spacing).");
                        }
                    }
                }

                if (hasFocus && TryRecordCombinedArmsAssist(vehPtr, now))
                {
                    AiCore.LogDebug(
                        "CombArmsTank",
                        $"{myFaction} combat vehicle steering toward known enemy squad / support request (slot #{myRankAtObj + 1}/{totalFriendlyNearObj}).");
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
                if (sqInside != null && !sqInside.IsPlayerInSquad())
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
        // Tank Standoff & Reverse Discipline (AIVehicle.MoveTowardCurrentNodeTank)
        // =====================================================================
        [HarmonyPatch(typeof(AIVehicle), nameof(AIVehicle.MoveTowardCurrentNodeTank))]
        public static class AIVehicle_MoveTowardCurrentNodeTank_Patch
        {
            public static bool Prefix(AIVehicle __instance)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value ||
                        !VisionConfig.EnableTankStandoffAndReverse.Value ||
                        __instance == null)
                    {
                        return true;
                    }

                    Vehicle veh = __instance.veh;
                    if (!AiCore.IsLocallyOwnedVehicle(veh) || veh.PlayerIsDriving() || veh.transform == null)
                        return true;

                    Turret mainTurret = veh.GetMainTurret(false);
                    TurretAI turretAi = mainTurret != null ? mainTurret.GetTurretAI() : null;
                    Spottable target = turretAi != null ? turretAi.CurrentVisibleTarget : null;
                    if (target == null || target.Pointer == IntPtr.Zero || !turretAi.targetInWeaponRange)
                        return true;

                    if (target.IsAirVehicle())
                        return true;

                    Vector3 vehPos = veh.transform.position;
                    Vector3 targetPos = target.GetCenterOfUnit();
                    Vector3 toTarget = targetPos - vehPos;
                    float dist = toTarget.magnitude;

                    SoldierPersonality crewPers = GetVehicleCrewPersonality(veh);
                    float reverseDist = 28.0f;
                    float maxStandoffDist = 90.0f;
                    if (crewPers == SoldierPersonality.TankCrew_Sniper)
                    {
                        reverseDist = 36.0f;
                        maxStandoffDist = 140.0f;
                    }
                    else if (crewPers == SoldierPersonality.TankCrew_Brawler)
                    {
                        reverseDist = 20.0f;
                        maxStandoffDist = 62.0f;
                    }

                    if (dist > maxStandoffDist)
                        return true;

                    IntPtr vehPtr = veh.Pointer;
                    float now = Time.time;

                    // 1. Close-Range Threat Reverse: back up using MoveTankRetro while keeping frontal armor toward the enemy
                    if (dist < reverseDist && Vector3.Angle(veh.transform.forward, toTarget) <= 75f)
                    {
                        if (!AiCore.IsDirectionBlockedByEnvironment(vehPos + Vector3.up * 1.2f, -veh.transform.forward, 7.5f, veh.transform.root) &&
                            (!VisionConfig.EnableFriendlyVehicleRightOfWay.Value || !ShouldYieldToFriendlyInfantry(__instance, veh, true)))
                        {
                            __instance.ForceRetroFor(2.2f, AIVehicle.RetroBehaviour.backward);
                            __instance.MoveTankRetro();
                            if (!_nextTankStandoffCountTime.TryGetValue(vehPtr, out float nextRevCount) || now >= nextRevCount)
                            {
                                _nextTankStandoffCountTime[vehPtr] = now + 5.0f;
                                TankStandoffHoldsCount++;
                                if (VisionConfig.DebugLogging.Value)
                                {
                                    AiCore.LogDebug("TankStandoff", $"Tank reversing from close-range threat ({dist:F0}m < {reverseDist:F0}m).");
                                }
                            }
                            return false;
                        }
                    }

                    // 2. Standoff Firing Hold: halt and orient frontal hull toward target instead of driving into AT-grenade range
                    //    (If this tank is a stacked wingman < 13.5m from a higher-priority friendly tank and target is > 42m away,
                    //    let it roll out to its dispersed flank slot while firing on the move instead of parking hull-to-hull!)
                    if (turretAi.CanSee(target) || CanTankShellIndoorTargetFacade(mainTurret, veh, target))
                    {
                        if (dist > 42.0f && (vehPos - __instance.setVehicleDestination).sqrMagnitude > 14.0f * 14.0f)
                        {
                            string myFaction = GetVehicleCrewFaction(veh, mainTurret);
                            long myId = vehPtr.ToInt64();
                            for (int i = 0; i < _activeGroundVehicles.Count; i++)
                            {
                                ActiveGroundVehicleInfo other = _activeGroundVehicles[i];
                                if (other.VehPtr == vehPtr || other.VehPtr.ToInt64() > myId || string.IsNullOrEmpty(other.Faction))
                                    continue;
                                if (!string.IsNullOrEmpty(myFaction) &&
                                    AiCore.AreSameSideFactions(myFaction, other.Faction) &&
                                    (other.Position - vehPos).sqrMagnitude < 13.5f * 13.5f)
                                {
                                    return true;
                                }
                            }
                        }

                        __instance.StopAndRotateTowardTarget();
                        if (!_nextTankStandoffCountTime.TryGetValue(vehPtr, out float nextHoldCount) || now >= nextHoldCount)
                        {
                            _nextTankStandoffCountTime[vehPtr] = now + 6.0f;
                            TankStandoffHoldsCount++;
                            if (VisionConfig.DebugLogging.Value)
                            {
                                AiCore.LogDebug("TankStandoff", $"Tank holding standoff firing position ({dist:F0}m, personality:{crewPers}).");
                            }
                        }
                        return false;
                    }
                }
                catch { }
                return true;
            }
        }

        // =====================================================================
        // Vanilla Physics Fix: Tank Sprocket & Rock Unstick Assist (VehicleTank.FixedUpdate)
        // Easy Red 2 tanks use discrete WheelCollider rays (leftTrack / rightTrack). When a rock,
        // stump, or trench lip wedges under the belly between roadwheels, the WheelColliders unload
        // (zero motorTorque traction) while Transform.Rotate spins in place. Apply a smooth
        // track-bite crawl impulse when throttle is held and upper hull is clear of walls!
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
                        ReferenceEquals(__instance, null))
                    {
                        return;
                    }

                    IntPtr ptr = __instance.Pointer;
                    if (ptr == IntPtr.Zero)
                        return;

                    // Guard against FixedSteps: 5 physics catch-up spirals: evaluate raycasts & IL2CPP damage/input
                    // state at most ONCE per rendered frame per tank, replaying only cached AddForce on substeps 2..5!
                    int curFrame = Time.frameCount;
                    if (_tankUnstickFrameCache.TryGetValue(ptr, out TankUnstickFrameState fState) && fState.Frame == curFrame)
                    {
                        if (fState.ApplyForce)
                        {
                            Rigidbody cachedRb = __instance.rb;
                            if (cachedRb != null && !cachedRb.isKinematic)
                                cachedRb.AddForce(fState.ForceVec, ForceMode.Acceleration);
                        }
                        return;
                    }

                    if (__instance == null ||
                        !__instance.OwnDriverOrVehicle() ||
                        !__instance.engineStarted ||
                        __instance.IsDisabled() ||
                        __instance.transform == null)
                    {
                        _tankUnstickFrameCache[ptr] = new TankUnstickFrameState { Frame = curFrame, ApplyForce = false };
                        return;
                    }

                    float throttleY = __instance.GetMovingDir().y;
                    if (Mathf.Abs(throttleY) <= 0.25f ||
                        __instance.GetPercentageDamageFor(VehicleInternalPart.tracks) >= 0.98f ||
                        __instance.GetPercentageDamageFor(VehicleInternalPart.engine) >= 0.98f)
                    {
                        _tankHighCenteredDuration.Remove(ptr);
                        _tankUnstickFrameCache[ptr] = new TankUnstickFrameState { Frame = curFrame, ApplyForce = false };
                        return;
                    }

                    Rigidbody rb = __instance.rb;
                    if (rb == null || rb.isKinematic)
                    {
                        _tankUnstickFrameCache[ptr] = new TankUnstickFrameState { Frame = curFrame, ApplyForce = false };
                        return;
                    }

                    float speedSq = rb.velocity.sqrMagnitude;
                    if (speedSq > 2.25f * 2.25f)
                    {
                        _tankHighCenteredDuration.Remove(ptr);
                        _tankUnstickFrameCache[ptr] = new TankUnstickFrameState { Frame = curFrame, ApplyForce = false };
                        return;
                    }

                    float stuckTime = _tankHighCenteredDuration.TryGetValue(ptr, out float prev) ? prev : 0f;
                    if (speedSq < 0.85f * 0.85f)
                    {
                        stuckTime += Mathf.Min(Time.deltaTime, 0.10f);
                        _tankHighCenteredDuration[ptr] = stuckTime;
                    }
                    else if (stuckTime < 1.40f)
                    {
                        _tankUnstickFrameCache[ptr] = new TankUnstickFrameState { Frame = curFrame, ApplyForce = false };
                        return;
                    }

                    if (stuckTime < 1.40f)
                    {
                        _tankUnstickFrameCache[ptr] = new TankUnstickFrameState { Frame = curFrame, ApplyForce = false };
                        return;
                    }

                    Vector3 tankPos = __instance.transform.position;
                    Vector3 driveDir = throttleY >= 0f ? __instance.transform.forward : -__instance.transform.forward;
                    driveDir.y = 0f;
                    if (driveDir.sqrMagnitude < 0.001f)
                    {
                        _tankUnstickFrameCache[ptr] = new TankUnstickFrameState { Frame = curFrame, ApplyForce = false };
                        return;
                    }
                    driveDir.Normalize();

                    // Ensure upper hull (+1.55m) is not pushing into a solid building/cliff wall
                    if (AiCore.IsDirectionBlockedByEnvironment(tankPos + Vector3.up * 1.55f, driveDir, 4.2f, __instance.transform.root))
                    {
                        _tankUnstickFrameCache[ptr] = new TankUnstickFrameState { Frame = curFrame, ApplyForce = false };
                        return;
                    }

                    Vector3 forceVec = driveDir * 5.8f + Vector3.up * 1.35f;
                    _tankUnstickFrameCache[ptr] = new TankUnstickFrameState
                    {
                        Frame = curFrame,
                        ApplyForce = true,
                        ForceVec = forceVec
                    };
                    rb.AddForce(forceVec, ForceMode.Acceleration);

                    float now = Time.time;
                    if (!_nextTankUnstickCountTime.TryGetValue(ptr, out float nextCount) || now >= nextCount)
                    {
                        _nextTankUnstickCountTime[ptr] = now + 5.0f;
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
