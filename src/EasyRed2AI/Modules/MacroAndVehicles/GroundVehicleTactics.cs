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

                if (!soldier.IsReloading &&
                    !soldier.isInHandsAnimation &&
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

        internal enum AtWeaponSwitchAction : byte
        {
            FallthroughToVanilla = 0,
            HoldSlot0AndFinalize = 1,
            SwitchToSlotAndFinalize = 2
        }

        internal struct AtWeaponSelectionDecision
        {
            public AtWeaponSwitchAction Action;
            public int TargetSlot;
            public bool ShouldRefillSlot0;
            public bool EnemyIsInRange;
        }

        internal static AtWeaponSelectionDecision DecideAtWeaponSelection(
            bool hasTarget,
            bool targetIsArmoredVehicle,
            float dist,
            bool g0IsAt,
            bool g0HasAmmo,
            float g0DamageRange,
            int stowedAtSlot,
            float stowedAtDamageRange,
            int stowedNonAtSlot,
            float stowedNonAtDamageRange)
        {
            if (hasTarget && targetIsArmoredVehicle)
            {
                if (dist <= 95.0f)
                {
                    if (g0IsAt && g0HasAmmo)
                    {
                        return new AtWeaponSelectionDecision
                        {
                            Action = AtWeaponSwitchAction.HoldSlot0AndFinalize,
                            TargetSlot = 0,
                            ShouldRefillSlot0 = true,
                            EnemyIsInRange = dist <= g0DamageRange
                        };
                    }

                    if (stowedAtSlot >= 1 && dist <= Mathf.Max(55.0f, stowedAtDamageRange + 18.0f))
                    {
                        return new AtWeaponSelectionDecision
                        {
                            Action = AtWeaponSwitchAction.SwitchToSlotAndFinalize,
                            TargetSlot = stowedAtSlot,
                            ShouldRefillSlot0 = false,
                            EnemyIsInRange = dist <= stowedAtDamageRange
                        };
                    }
                }

                return new AtWeaponSelectionDecision { Action = AtWeaponSwitchAction.FallthroughToVanilla };
            }

            if (g0IsAt)
            {
                if (stowedNonAtSlot >= 1)
                {
                    return new AtWeaponSelectionDecision
                    {
                        Action = AtWeaponSwitchAction.SwitchToSlotAndFinalize,
                        TargetSlot = stowedNonAtSlot,
                        ShouldRefillSlot0 = false,
                        EnemyIsInRange = hasTarget && dist <= stowedNonAtDamageRange
                    };
                }

                if (g0HasAmmo)
                {
                    return new AtWeaponSelectionDecision
                    {
                        Action = AtWeaponSwitchAction.HoldSlot0AndFinalize,
                        TargetSlot = 0,
                        ShouldRefillSlot0 = hasTarget,
                        EnemyIsInRange = hasTarget && dist <= g0DamageRange
                    };
                }
            }

            return new AtWeaponSelectionDecision { Action = AtWeaponSwitchAction.FallthroughToVanilla };
        }

        // =====================================================================
        // Vanilla AI Fix: Handheld AT vs. Primary Firearm Selection (SoldierAI.UseBestWearedWeaponCheck)
        // In vanilla SoldierAI.UseBestWearedWeaponCheck (0x180561950):
        // 1) Vanilla only checks if visibleTarget is VehicleTank (0x180561B2E). When targeting an armored
        //    VehicleWithWheels (halftrack / armored car), vanilla thinks the target is NOT a vehicle and
        //    switches the soldier AWAY from their AT launcher back to their rifle (SwitchTo(1))!
        // 2) Returning false early without running the tail (0x180561CE2..0x180561D83) would skip setting
        //    SoldierAI.enemyIsInRange (+0xAE) and calling RefillAmmoForWearedWeapon(0), leaving bots muted!
        // 3) Conversely, if a non-isAtUnit soldier scavenged an AT launcher into Slot 0 (or switched to an
        //    AT weapon against armor) and now targets foot infantry, switch back to a loaded non-AT firearm
        //    only if one actually has ammo (never switch to an empty Slot 1 weapon!).
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
                        soldier.HoldsMeleeWeapon ||
                        soldier.IsReloading ||
                        soldier.isInHandsAnimation ||
                        soldier.isInBodyAnimation ||
                        !soldier.CanSwitchHeldItem)
                    {
                        return true;
                    }

                    Spottable vt = __instance.visibleTarget;
                    bool hasTarget = vt != null && vt.Pointer != IntPtr.Zero;
                    bool targetIsArmoredVeh = false;
                    float dist = __instance.distFromTarget;

                    if (hasTarget && vt.IsWheeledVehicleOrTank() && !vt.IsAirVehicle())
                    {
                        Vehicle targetVeh = vt.TryCast<Vehicle>();
                        if (targetVeh != null &&
                            !targetVeh.IsStatic() &&
                            GetVehicleMaxHullArmorMm(targetVeh) >= 6.0f &&
                            soldier.transform != null)
                        {
                            targetIsArmoredVeh = true;
                            dist = Vector3.Distance(soldier.transform.position, vt.GetPosition());
                        }
                    }

                    GenericGun g0 = soldier.GetHeldGun(0);
                    bool g0IsAt = g0 != null && g0.IsAtWeapon();
                    bool g0HasAmmo = g0 != null && (g0.GetCurrentAmmoCount() > 0 || !soldier.IsOutOfAmmosFor(g0));
                    float g0Range = g0 != null ? g0.DamageRange() : 0f;

                    int stowedAtSlot = -1;
                    float stowedAtRange = 0f;
                    int stowedNonAtSlot = -1;
                    float stowedNonAtRange = 0f;

                    for (int slot = 1; slot <= 2; slot++)
                    {
                        GenericGun g = soldier.GetHeldGun(slot);
                        if (g == null || (g.GetCurrentAmmoCount() <= 0 && soldier.IsOutOfAmmosFor(g)))
                            continue;

                        if (g.IsAtWeapon())
                        {
                            if (stowedAtSlot < 0)
                            {
                                stowedAtSlot = slot;
                                stowedAtRange = g.DamageRange();
                            }
                        }
                        else if (stowedNonAtSlot < 0)
                        {
                            stowedNonAtSlot = slot;
                            stowedNonAtRange = g.DamageRange();
                        }
                    }

                    AtWeaponSelectionDecision decision = DecideAtWeaponSelection(
                        hasTarget,
                        targetIsArmoredVeh,
                        dist,
                        g0IsAt,
                        g0HasAmmo,
                        g0Range,
                        stowedAtSlot,
                        stowedAtRange,
                        stowedNonAtSlot,
                        stowedNonAtRange);

                    if (decision.Action == AtWeaponSwitchAction.FallthroughToVanilla)
                        return true;

                    if (decision.Action == AtWeaponSwitchAction.SwitchToSlotAndFinalize)
                    {
                        bool canSwitch = !Photon.Pun.PhotonNetwork.InRoom || AiCore.TryConsumeBotRpcBudget(0.35f);
                        if (canSwitch)
                        {
                            soldier.SetGunTriggerPressed(false);
                            soldier.SwitchTo(decision.TargetSlot);
                        }
                        else if (hasTarget)
                        {
                            soldier.RefillAmmoForWearedWeapon(0, false, null, true, 0);
                        }

                        if (hasTarget)
                        {
                            bool inRange = canSwitch ? decision.EnemyIsInRange : (g0 != null && dist <= g0Range);
                            __instance.enemyIsInRange = inRange;
                            if (!canSwitch && inRange && !soldier.IsStealth)
                            {
                                __instance.allowFireAtEnemy = true;
                                if (!soldier.HasMovementsSlowed())
                                    __instance.moveLookingTarget = true;
                            }
                        }
                        return false;
                    }

                    // HoldSlot0AndFinalize: mirror vanilla tail so Slot 0 refills & enemyIsInRange (+0xAE) is set
                    if (decision.ShouldRefillSlot0)
                    {
                        soldier.RefillAmmoForWearedWeapon(0, false, null, true, 0);
                    }
                    if (hasTarget)
                    {
                        __instance.enemyIsInRange = decision.EnemyIsInRange;
                        if (decision.EnemyIsInRange && !soldier.IsStealth)
                        {
                            __instance.allowFireAtEnemy = true;
                            if (!soldier.HasMovementsSlowed())
                                __instance.moveLookingTarget = true;
                        }
                    }
                    return false;
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

                    if (AiCore.IsFootBotStandingOnVehicleHull(__0))
                    {
                        __result = false;
                        return;
                    }

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

                    if (VisionConfig.EnableEmergentDecisionEngine != null &&
                        VisionConfig.EnableEmergentDecisionEngine.Value &&
                        __instance.vehToRepair != null &&
                        EmergentRuntimeBridge.TryEvaluateEmergentVehicleCrew(__instance.vehToRepair, out _, out _))
                    {
                        var vhRep = AiCore.GetOrCreateVehicleHandles(__instance.vehToRepair);
                        if (vhRep != null &&
                            vhRep.Emergent.HasDecision &&
                            vhRep.Emergent.CurrentDecision.UnsafePrereqRejected)
                        {
                            return false;
                        }
                    }

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

        internal static bool IsSquadVehicleSeekingRepairStation(Squad sq)
        {
            if (sq == null || sq.Pointer == IntPtr.Zero || sq.Leader == null || !sq.Leader.IsOnVehicle())
                return false;

            Vehicle v = sq.Leader.GetCurrentVehicle();
            AiCore.VehicleFastHandles vh = v != null ? AiCore.GetOrCreateVehicleHandles(v) : null;
            RepairStation st = vh != null ? vh.SeekingRepairStation : null;
            return st != null && st.Pointer != IntPtr.Zero;
        }

        internal static bool IsSquadVehicleAnsweringTankSupport(Squad sq)
        {
            if (sq == null || sq.Pointer == IntPtr.Zero || sq.Leader == null || !sq.Leader.IsOnVehicle())
                return false;

            Vehicle v = sq.Leader.GetCurrentVehicle();
            return v != null &&
                   AiCore.TryGetVehicleHandles(v.Pointer, out AiCore.VehicleFastHandles vh) &&
                   vh.AnsweringTankSupport;
        }

        private static bool TryRouteVehicleToRepairStation(AIVehicle aiVeh, Vehicle veh, float now)
        {
            if (aiVeh == null || veh == null || veh.Pointer == IntPtr.Zero || veh.transform == null)
                return false;

            IntPtr vehPtr = veh.Pointer;
            AiCore.VehicleFastHandles vh = AiCore.GetOrCreateVehicleHandles(veh);
            if (!AiCore.IsLocallyOwnedVehicle(veh) ||
                veh.IsDisabled() ||
                veh.IsAirVehicle() ||
                AiCore.IsAnyHumanPlayerInsideVehicle(veh) ||
                AiCore.IsAnyHumanPlayerDriving(veh) ||
                !veh.HasDriver())
            {
                if (vh != null)
                    vh.SeekingRepairStation = null;
                return false;
            }

            MovableVehicle mov = veh.TryCast<MovableVehicle>();
            if (mov == null)
                return false;

            // MovableVehicle.GetPercentageDamageFor (0x1808270C0) returns remaining health ratio [0..1] (1.0f = undamaged).
            // Cannot drive to a repair station if tracks or engine are destroyed (<= 5% health remaining; crew must field-repair first).
            float tracksHealth = Mathf.Clamp01(mov.GetPercentageDamageFor(VehicleInternalPart.tracks));
            float engineHealth = Mathf.Clamp01(mov.GetPercentageDamageFor(VehicleInternalPart.engine));
            if (tracksHealth <= 0.05f || engineHealth <= 0.05f)
            {
                if (vh != null)
                    vh.SeekingRepairStation = null;
                return false;
            }

            RefreshCombinedArmsIntelIfNeeded();
            if (_activeRepairStations.Count == 0)
            {
                if (vh != null)
                    vh.SeekingRepairStation = null;
                return false;
            }

            float hullDamage = 1.0f - Mathf.Clamp01(mov.GetPercentageDamageFor(VehicleInternalPart.hull));
            float fuelDamage = 1.0f - Mathf.Clamp01(mov.GetPercentageDamageFor(VehicleInternalPart.fuelTank));
            float tracksDamage = 1.0f - tracksHealth;
            float engineDamage = 1.0f - engineHealth;
            float maxPartDamage = Mathf.Max(Mathf.Max(hullDamage, fuelDamage), Mathf.Max(tracksDamage, engineDamage));

            Turret mainTurret = veh.GetMainTurret(false);
            bool outOfTurretAmmo = false;
            if (mainTurret != null && mainTurret.WeaponsCount() > 0)
            {
                mainTurret.CheckBullets(out bool hasHe, out bool hasAp, out bool hasFire, out bool hasFlak);
                outOfTurretAmmo = !hasHe && !hasAp && !hasFire && !hasFlak;
            }

            RepairStation activeStation = vh != null ? vh.SeekingRepairStation : null;
            bool alreadySeeking = activeStation != null &&
                                  activeStation.Pointer != IntPtr.Zero &&
                                  activeStation.gameObject != null &&
                                  activeStation.gameObject.activeInHierarchy &&
                                  activeStation.transform != null;

            if (!alreadySeeking && vh != null && now < vh.NextRepairStationAttemptTime)
                return false;

            Vector3 vehPos = veh.transform.position;
            float activeRadius = alreadySeeking ? Mathf.Max(5.0f, activeStation.radius) : 0f;
            bool insideStationNow = alreadySeeking &&
                                    (activeStation.transform.position - vehPos).sqrMagnitude <= activeRadius * activeRadius;
            bool needsStation = alreadySeeking
                ? (outOfTurretAmmo || maxPartDamage > 0.08f || (insideStationNow && (maxPartDamage > 0.02f || veh.CanRefillAmmmo())))
                : (outOfTurretAmmo || maxPartDamage >= 0.40f);

            if (!needsStation)
            {
                if (alreadySeeking)
                {
                    if (vh != null)
                    {
                        vh.SeekingRepairStation = null;
                        vh.RepairStationRouteStartedAt = 0f;
                        vh.RepairStationBestDist = 0f;
                    }
                    Squad sq = aiVeh.squadInside;
                    if (sq != null && !AiCore.HasAnyHumanPlayerInSquad(sq) && sq.Leader != null)
                    {
                        sq.timed_order_end = 0f;
                        sq.Leader_OrderAttackCurrentTask(sq.Leader);
                    }
                    aiVeh.rerollDestination = true;
                    try { aiVeh.UpdatePath(); } catch { }
                    if (VisionConfig.DebugLogging.Value)
                    {
                        AiCore.LogDebug("RepairStation", $"Vehicle finished repairing/re-arming at RepairStation and is returning to battle.");
                    }
                }
                return false;
            }

            string myFaction = GetVehicleCrewFaction(veh, mainTurret);
            RepairStation bestStation = alreadySeeking ? activeStation : null;

            if (bestStation != null && (bestStation.transform.position - vehPos).sqrMagnitude > 420.0f * 420.0f)
            {
                if (vh != null)
                    vh.SeekingRepairStation = null;
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

            bool startedNewRoute = !alreadySeeking || activeStation.Pointer != bestStation.Pointer;
            if (vh != null)
                vh.SeekingRepairStation = bestStation;
            Vector3 stationPos = bestStation.transform.position;
            float stationRadius = Mathf.Max(5.0f, bestStation.radius);
            float distToStation = Vector3.Distance(vehPos, stationPos);

            if (vh != null)
            {
                if (startedNewRoute || vh.RepairStationRouteStartedAt <= 0f)
                {
                    vh.RepairStationRouteStartedAt = now;
                    vh.RepairStationBestDist = distToStation;
                }
                else if (distToStation > stationRadius * 0.95f)
                {
                    if (distToStation < vh.RepairStationBestDist - 8.0f)
                    {
                        vh.RepairStationBestDist = distToStation;
                        vh.RepairStationRouteStartedAt = now;
                    }
                    else if ((now - vh.RepairStationRouteStartedAt) >= 24.0f)
                    {
                        vh.SeekingRepairStation = null;
                        vh.RepairStationRouteStartedAt = 0f;
                        vh.RepairStationBestDist = 0f;
                        vh.NextRepairStationAttemptTime = now + 45.0f;
                        Squad abortSq = aiVeh.squadInside;
                        if (abortSq != null && !AiCore.HasAnyHumanPlayerInSquad(abortSq) && abortSq.Leader != null)
                        {
                            abortSq.timed_order_end = 0f;
                            abortSq.Leader_OrderAttackCurrentTask(abortSq.Leader);
                        }
                        aiVeh.rerollDestination = true;
                        try { aiVeh.UpdatePath(); } catch { }
                        return false;
                    }
                }
                else
                {
                    vh.RepairStationBestDist = distToStation;
                    vh.RepairStationRouteStartedAt = now;
                }
            }

            Squad crewSq = aiVeh.squadInside;
            if (crewSq != null && !AiCore.HasAnyHumanPlayerInSquad(crewSq))
            {
                crewSq.order = Order.defend;
                crewSq.moveOrderPosition = stationPos;
                crewSq.moveOrderRadius = Mathf.Max(4.0f, stationRadius * 0.65f);
                if (crewSq.timed_order_end < now + 6.0f)
                    crewSq.timed_order_end = now + 6.0f;
            }

            if (distToStation <= stationRadius * 0.85f)
            {
                // Parked inside the RepairStation circle: hold brake while RepairStation.Update repairs parts & refills turrets!
                float holdUntil = now + 1.0f;
                if (aiVeh.resumeDriveTime < holdUntil)
                    aiVeh.resumeDriveTime = holdUntil;
                return true;
            }

            if ((aiVeh.setVehicleDestination - stationPos).sqrMagnitude > 4.0f * 4.0f)
            {
                aiVeh.setVehicleDestination = stationPos;
                aiVeh.rerollDestination = false;
                try { aiVeh.UpdatePath(); } catch { }
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
                AiCore.IsAnyHumanPlayerInsideVehicle(veh) ||
                AiCore.IsAnyHumanPlayerDriving(veh))
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
            //    otherwise let combat tanks/TDs intercept Radio-Net-confirmed enemy armor,
            //    keeping vanilla AIVehicle road/node pathfinding untouched!
            Vector3 bestFocusPos = Vector3.zero;
            float bestFocusScore = -999f;
            Squad focusSupportSquad = null;
            bool isCounterArmorIntercept = false;

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

            if (focusSupportSquad == null &&
                !veh.IsTransportVehicle() &&
                (veh.TryCast<VehicleTank>() != null || GetVehicleMaxHullArmorMm(veh) >= 12.0f))
            {
                int clusterCount = _knownEnemyClusters.Count;
                for (int c = 0; c < clusterCount; c++)
                {
                    KnownEnemyCluster cluster = _knownEnemyClusters[c];
                    if (cluster.VehicleTarget == null || AiCore.AreSameSideFactions(cluster.Faction, myFaction))
                        continue;

                    Vehicle enemyVeh = cluster.VehicleTarget.TryCast<Vehicle>();
                    if (enemyVeh == null ||
                        enemyVeh.Pointer == IntPtr.Zero ||
                        enemyVeh.transform == null ||
                        enemyVeh.IsAirVehicle() ||
                        !enemyVeh.IsWheeledVehicleOrTank() ||
                        !enemyVeh.CanFight() ||
                        GetVehicleMaxHullArmorMm(enemyVeh) < 12.0f)
                    {
                        continue;
                    }

                    Vector3 enemyPos = enemyVeh.transform.position;
                    float distToVeh = Vector3.Distance(vehPos, enemyPos);
                    if (distToVeh < 45.0f || distToVeh > 340.0f)
                        continue;

                    if (Vector3.Distance(squadDest, enemyPos) > 320.0f)
                        continue;

                    if (!CanTurretEffectivelyDamageArmoredVehicle(mainTurret, enemyVeh, distToVeh))
                        continue;

                    float spreadBonus = (AiCore.GetDeterministicHash(vehPtr, c + 11) & 3) * 3.0f;
                    float score = cluster.BasePriority +
                                  (cluster.IsEngagingFriendlies ? 32.0f : 0f) +
                                  (cluster.IsMarkedOrPinPoint ? 15.0f : 0f) +
                                  spreadBonus -
                                  (distToVeh * 0.14f);
                    if (score > bestFocusScore)
                    {
                        bestFocusScore = score;
                        bestFocusPos = enemyPos;
                        isCounterArmorIntercept = true;
                    }
                }
            }

            AiCore.VehicleFastHandles vh = AiCore.GetOrCreateVehicleHandles(veh);
            float maxAllowedOffset = Mathf.Max(15.0f, aiVeh.GetCurrentSquadDestinationAreaSize() * 0.84f);
            bool hasMissionFocus = focusSupportSquad != null || (isCounterArmorIntercept && bestFocusScore > -900f);
            bool answeringFarTankSupport = hasMissionFocus && Vector3.Distance(squadDest, bestFocusPos) > maxAllowedOffset;
            if (!answeringFarTankSupport)
            {
                if (vh != null && vh.AnsweringTankSupport)
                {
                    if (now < vh.TankSupportHoldUntil)
                    {
                        if (crewSq != null && !AiCore.HasAnyHumanPlayerInSquad(crewSq))
                            crewSq.timed_order_end = Mathf.Max(crewSq.timed_order_end, vh.TankSupportHoldUntil);
                        return;
                    }

                    vh.AnsweringTankSupport = false;
                    vh.TankSupportHoldUntil = 0f;
                    if (crewSq != null && !AiCore.HasAnyHumanPlayerInSquad(crewSq))
                    {
                        crewSq.timed_order_end = 0f;
                        if (crewSq.Leader != null &&
                            (!Photon.Pun.PhotonNetwork.InRoom || AiCore.TryConsumeBotRpcBudget(0.45f)))
                        {
                            crewSq.Leader_OrderAttackCurrentTask(crewSq.Leader);
                        }
                    }
                    aiVeh.rerollDestination = true;
                }
                return;
            }

            Vector3 fromFocusToTank = vehPos - bestFocusPos;
            fromFocusToTank.y = 0f;
            Vector3 standoffDir = fromFocusToTank.sqrMagnitude > 0.25f ? fromFocusToTank.normalized : -veh.transform.forward;
            float standoffDist = isCounterArmorIntercept ? 82.0f : 38.0f;
            Vector3 desiredDest = bestFocusPos + standoffDir * standoffDist;
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

            bool alreadyAnswering = vh != null && vh.AnsweringTankSupport;
            Vector3 currentAnchor = (crewSq != null && crewSq.moveOrderPosition.sqrMagnitude > 0.01f)
                ? crewSq.moveOrderPosition
                : aiVeh.setVehicleDestination;
            bool destChanged = !alreadyAnswering || (currentAnchor - desiredDest).sqrMagnitude > 24.0f * 24.0f;

            if (vh != null)
                vh.TankSupportHoldUntil = now + 5.0f;
            if (crewSq != null && !AiCore.HasAnyHumanPlayerInSquad(crewSq))
                crewSq.timed_order_end = Mathf.Max(crewSq.timed_order_end, now + 10.0f);

            if (destChanged)
            {
                if (vh != null)
                    vh.AnsweringTankSupport = true;
                if (crewSq != null && !AiCore.HasAnyHumanPlayerInSquad(crewSq))
                {
                    bool isTank = veh.TryCast<VehicleTank>() != null;
                    if (crewSq.order == Order.follow)
                        crewSq.order = Order.defend;
                    crewSq.moveOrderPosition = desiredDest;
                    crewSq.moveOrderRadius = isTank ? 46.0f : 34.0f;
                }

                aiVeh.setVehicleDestination = desiredDest;
                aiVeh.rerollDestination = false;
                _claimedVehicleDestinations[vehPtr] = desiredDest;

                if (focusSupportSquad != null)
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

                if (TryRecordCombinedArmsAssist(vehPtr, now))
                {
                    AiCore.LogDebug(
                        isCounterArmorIntercept ? "CombArmsAntiArmor" : "CombArmsTank",
                        isCounterArmorIntercept
                            ? $"{myFaction} combat vehicle moving to intercept Radio-Net-reported enemy armor."
                            : $"{myFaction} combat vehicle answering friendly tank support radio request.");
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

            if (!AiCore.IsLocallyOwnedVehicle(veh) || !veh.IsTransportVehicle() || veh.IsAirVehicle() || AiCore.IsAnyHumanPlayerDriving(veh) || AiCore.IsVehicleInWater(veh))
                return;

            AiCore.VehicleFastHandles vh = AiCore.GetOrCreateVehicleHandles(veh);
            float now = Time.time;
            if (vh != null && now < vh.NextApcDismountCooldown)
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
                if (passenger != null && passenger.IsAlive && !AiCore.IsAnyHumanPlayerSoldier(passenger))
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

            bool closeEnemyTank = false;
            if (aiVeh != null && aiVeh.hasEnemyTank)
            {
                if (veh.CurrentVisibleTarget != null &&
                    veh.CurrentVisibleTarget.Pointer != IntPtr.Zero &&
                    veh.transform != null &&
                    AiCore.TryGetSpottableCenter(veh.CurrentVisibleTarget, out Vector3 visTankPos))
                {
                    closeEnemyTank = (visTankPos - veh.transform.position).sqrMagnitude <= 140.0f * 140.0f;
                }
                else if (veh.GetDamage() > 0)
                {
                    closeEnemyTank = true;
                }
            }
            bool shouldDismount = forcedBySuppression ||
                                  closeEnemyTank ||
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
                if (vh != null)
                    vh.NextApcDismountCooldown = now + 8.0f;
                VehicleWithWheels wheeled = veh.TryCast<VehicleWithWheels>();
                if (wheeled != null)
                {
                    wheeled.Brake();
                }
                if (aiVeh != null && aiVeh.resumeDriveTime < now + 3.0f)
                {
                    aiVeh.resumeDriveTime = now + 3.0f;
                }

                bool isUnarmedTransport = true;
                try
                {
                    var dismountSeats = veh.seats;
                    if (dismountSeats != null)
                    {
                        for (int i = 0; i < dismountSeats.Length; i++)
                        {
                            Seats s = dismountSeats[i];
                            if (s != null && s.hasTurret && s.connectedTurret != null && s.connectedTurret.WeaponsCount() > 0)
                            {
                                isUnarmedTransport = false;
                                break;
                            }
                        }
                    }
                }
                catch { }

                if (isUnarmedTransport)
                {
                    veh.EjectAll(false, true);
                }
                else
                {
                    veh.DropInfantry(false);
                }
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
                        __instance.GetPercentageDamageFor(VehicleInternalPart.tracks) <= 0.05f ||
                        __instance.GetPercentageDamageFor(VehicleInternalPart.engine) <= 0.05f)
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
                catch (Exception ex)
                {
                    AiCore.ReportPatchException("TankFixedUpd", ex);
                }
                finally
                {
                    HitchProfiler.End(HitchBucket.VehFixedUpdate, __state);
                }
            }
        }

        // =====================================================================
        // Urgent Straightaway AI Vehicle Cruise Speed (Preserves Corner Braking)
        // =====================================================================
        [HarmonyPatch(typeof(AIVehicle), nameof(AIVehicle.MoveTowardCurrentNodeWheeledVehicle))]
        public static class AIVehicle_MoveTowardCurrentNodeWheeledVehicle_Patch
        {
            public static void Postfix(AIVehicle __instance)
            {
                try
                {
                    ApplyUrgentStraightawayCruiseBoost(__instance, isTank: false);
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(AIVehicle), nameof(AIVehicle.MoveTowardCurrentNodeTank))]
        public static class AIVehicle_MoveTowardCurrentNodeTank_Patch
        {
            public static void Postfix(AIVehicle __instance)
            {
                try
                {
                    ApplyUrgentStraightawayCruiseBoost(__instance, isTank: true);
                }
                catch { }
            }
        }

        private static void ApplyUrgentStraightawayCruiseBoost(AIVehicle ai, bool isTank)
        {
            if (!VisionConfig.EnableMod.Value || !VisionConfig.EnableUrgentVehicleCruiseSpeed.Value)
                return;

            if (ai == null || ai.going_in_retro || ai.lastDriveThrottle < 0f || ai.placingInUnstuck)
                return;

            Vehicle veh = ai.veh;
            if (veh == null || veh.Pointer == IntPtr.Zero || veh.transform == null ||
                veh.IsDisabled() || veh.IsAirVehicle() || AiCore.IsAnyHumanPlayerDriving(veh) ||
                !AiCore.IsLocallyOwnedVehicle(veh))
            {
                return;
            }

            VehicleWithWheels wheeled = veh.TryCast<VehicleWithWheels>();
            if (wheeled == null)
                return;

            if (wheeled.floatingObject != null && wheeled.floatingObject.OnWater)
                return;

            A.d path = ai.path;
            if (ReferenceEquals(path, null) || path.Pointer == IntPtr.Zero)
                return;

            int nodesCount = path.NodesCount;
            if (nodesCount <= 0)
                return;

            if (nodesCount == 1 && ai.distFronCurrentNode < 18.0f)
                return;

            ai.GetDriveData(out float dirAngle, out _, out float kmhSpeed, out int movingDir);
            if (movingDir < 0 || dirAngle > 24.0f)
                return;

            if (kmhSpeed < 12.5f && ai.lastDriveThrottle >= 0.95f)
                return;

            float vehMaxKmh = wheeled.maxKmhSpeed;
            if (vehMaxKmh <= 5.0f)
                vehMaxKmh = isTank ? 32.0f : 48.0f;

            float straightCap;
            if (isTank)
            {
                straightCap = Mathf.Clamp(vehMaxKmh * 0.78f, 22.0f, 34.0f);
                if (ai.hasEnemy)
                    straightCap = Mathf.Min(straightCap, 25.0f);
            }
            else
            {
                bool isTransport = veh.IsTransportVehicle();
                straightCap = isTransport
                    ? Mathf.Clamp(vehMaxKmh * 0.74f, 28.0f, 46.0f)
                    : Mathf.Clamp(vehMaxKmh * 0.70f, 26.0f, 40.0f);
                if (!isTransport && ai.hasEnemy)
                    straightCap = Mathf.Min(straightCap, 30.0f);
            }

            Vector3 vPos = veh.transform.position;
            Vector3 n0 = path.A(0);
            Vector3 seg0 = n0 - vPos;
            seg0.y = 0f;
            float dist0 = seg0.magnitude;
            float cornerCap = straightCap;

            if (nodesCount >= 2 && dist0 > 0.25f)
            {
                Vector3 n1 = path.A(1);
                Vector3 seg1 = n1 - n0;
                seg1.y = 0f;
                if (seg1.sqrMagnitude > 0.25f)
                {
                    float turn1 = Vector3.Angle(seg0, seg1);
                    if (turn1 > 20.0f && dist0 < 28.0f)
                    {
                        float prox1 = Mathf.Clamp01(dist0 / 28.0f);
                        float turn1Max = turn1 >= 45.0f
                            ? 14.0f
                            : Mathf.Lerp(28.0f, 15.0f, (turn1 - 20.0f) / 25.0f);
                        cornerCap = Mathf.Min(cornerCap, Mathf.Lerp(turn1Max, straightCap, prox1 * prox1));
                    }

                    if (nodesCount >= 3)
                    {
                        float dist1 = dist0 + seg1.magnitude;
                        if (dist1 < 34.0f)
                        {
                            Vector3 n2 = path.A(2);
                            Vector3 seg2 = n2 - n1;
                            seg2.y = 0f;
                            if (seg2.sqrMagnitude > 0.25f)
                            {
                                float turn2 = Vector3.Angle(seg1, seg2);
                                if (turn2 > 28.0f)
                                {
                                    float prox2 = Mathf.Clamp01(dist1 / 34.0f);
                                    float turn2Max = turn2 >= 50.0f
                                        ? 15.0f
                                        : Mathf.Lerp(28.0f, 16.0f, (turn2 - 28.0f) / 22.0f);
                                    cornerCap = Mathf.Min(cornerCap, Mathf.Lerp(turn2Max, straightCap, prox2 * prox2));
                                }
                            }
                        }
                    }
                }
            }
            else if (nodesCount == 1)
            {
                float destProx = Mathf.Clamp01((dist0 - 18.0f) / 26.0f);
                cornerCap = Mathf.Min(cornerCap, Mathf.Lerp(17.0f, straightCap, destProx));
            }

            float alignFactor = isTank
                ? (dirAngle <= 5.0f ? 1.0f : Mathf.Clamp01(1.0f - (dirAngle - 5.0f) / 15.0f))
                : (dirAngle <= 8.0f ? 1.0f : Mathf.Clamp01(1.0f - (dirAngle - 8.0f) / 16.0f));
            float vanillaCap = isTank ? 17.0f : 16.0f;
            float targetKmh = Mathf.Lerp(vanillaCap, cornerCap, alignFactor);
            if (targetKmh <= vanillaCap + 0.5f)
                return;

            float steerX = veh.movingDir.x;
            if (isTank)
            {
                if (dirAngle <= 3.5f)
                {
                    steerX = 0f;
                }
                else if (Mathf.Abs(steerX) > 0.001f)
                {
                    float speedRatio = Mathf.Clamp01(kmhSpeed / Mathf.Max(10.0f, vehMaxKmh));
                    float vanillaRatio = Mathf.Clamp01(17.0f / Mathf.Max(10.0f, vehMaxKmh));
                    float gainComp = (1.0f + vanillaRatio) / (1.0f + speedRatio);
                    float dampedMag = Mathf.Clamp01((dirAngle - 3.5f) / 28.0f) * gainComp;
                    steerX = Mathf.Sign(steerX) * Mathf.Min(Mathf.Abs(steerX), dampedMag);
                }
            }

            float brakeKmh = targetKmh * 1.10f;
            if (kmhSpeed <= targetKmh)
            {
                float throttle = kmhSpeed < (targetKmh - 3.0f)
                    ? 1.0f
                    : Mathf.Lerp(1.0f, 0.35f, (kmhSpeed - (targetKmh - 3.0f)) / 3.0f);
                wheeled.lastHoldBrakeFrame = -10;
                wheeled.lastBrakeRequestFrame = -10;
                ai.lastDriveThrottle = throttle;
                veh.Move(new Vector2(steerX, throttle));
            }
            else if (kmhSpeed <= brakeKmh)
            {
                wheeled.lastHoldBrakeFrame = -10;
                wheeled.lastBrakeRequestFrame = -10;
                ai.lastDriveThrottle = 0f;
                veh.Move(new Vector2(steerX, 0f));
            }
        }

        // =====================================================================
        // Vehicle & Tank Road-Preferring & Clear-Corridor Pathing (A.F.vehicleGrid)
        // =====================================================================
        private const byte VehicleCellClearOpen = 1;
        private const byte VehicleCellModerateClutter = 2;
        private const byte VehicleCellTightObstructed = 3;
        private const int MaxVehicleRoadCells = 45000;
        private const int MaxVehicleClearanceCacheEntries = 24000;

        private static IntPtr _cachedVehicleGridPtr;
        private static int _cachedVehicleStaticObstacleMask;
        private static readonly HashSet<long> _vehicleRoadCells = new HashSet<long>(2048);
        private static readonly Dictionary<long, byte> _vehicleCellClearanceCache = new Dictionary<long, byte>(4096);
        private static IntPtr _vehicleRoadCacheDbPtr;
        private static int _vehicleRoadCacheStreetCount = -1;
        private static float _vehicleRoadCacheNextSyncTime;
        private static int _vehicleRoadCacheSyncAttempts;
        private static readonly Dictionary<int, Vector4> _reusableRoadNodes = new Dictionary<int, Vector4>(512);
        private static readonly HashSet<string> _reusableRiverProfileIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static long PackVehicleCell(int cx, int cz)
        {
            return ((long)cx << 32) | (uint)cz;
        }

        private static long PackVehicleCell(Vector3 worldPos)
        {
            int cx = Mathf.RoundToInt(worldPos.x * 0.25f);
            int cz = Mathf.RoundToInt(worldPos.z * 0.25f);
            return PackVehicleCell(cx, cz);
        }

        private static bool IsVehicleGrid(global::A.f grid)
        {
            if (ReferenceEquals(grid, null) || grid == null || grid.Pointer == IntPtr.Zero)
                return false;

            if (_cachedVehicleGridPtr == IntPtr.Zero)
            {
                global::A.f vg = global::A.F.vehicleGrid;
                if (!ReferenceEquals(vg, null) && vg != null)
                    _cachedVehicleGridPtr = vg.Pointer;
            }

            if (_cachedVehicleGridPtr != IntPtr.Zero)
                return grid.Pointer == _cachedVehicleGridPtr;

            return grid.preferOpenFieldNodes || grid.nodeWidth >= 3.5f;
        }

        private static int GetVehicleStaticObstacleMask()
        {
            if (_cachedVehicleStaticObstacleMask != 0)
                return _cachedVehicleStaticObstacleMask;

            int mask = 1 << 0; // Default (buildings, rocks, trees, walls, fences)
            int invWallLayer = LayerMask.NameToLayer("InvisibleWall");
            if (invWallLayer >= 0)
                mask |= (1 << invWallLayer);
            int invTrepLayer = LayerMask.NameToLayer("InvisibleTrepassableWall");
            if (invTrepLayer >= 0)
                mask |= (1 << invTrepLayer);

            _cachedVehicleStaticObstacleMask = mask;
            return mask;
        }

        internal static void ClearVehiclePathingCaches(bool keepRoadMap = false)
        {
            _vehicleCellClearanceCache.Clear();
            if (!keepRoadMap)
            {
                _vehicleRoadCells.Clear();
                _cachedVehicleGridPtr = IntPtr.Zero;
                _vehicleRoadCacheDbPtr = IntPtr.Zero;
                _vehicleRoadCacheStreetCount = -1;
                _vehicleRoadCacheNextSyncTime = 0f;
                _vehicleRoadCacheSyncAttempts = 0;
            }
        }

        private static void MarkRoadDisk(float wx, float wz, float radius)
        {
            if (_vehicleRoadCells.Count >= MaxVehicleRoadCells)
                return;

            int centerCx = Mathf.RoundToInt(wx * 0.25f);
            int centerCz = Mathf.RoundToInt(wz * 0.25f);
            _vehicleRoadCells.Add(PackVehicleCell(centerCx, centerCz));

            int minCx = Mathf.RoundToInt((wx - radius) * 0.25f);
            int maxCx = Mathf.RoundToInt((wx + radius) * 0.25f);
            int minCz = Mathf.RoundToInt((wz - radius) * 0.25f);
            int maxCz = Mathf.RoundToInt((wz + radius) * 0.25f);
            float rSq = radius * radius;

            for (int cx = minCx; cx <= maxCx; cx++)
            {
                float dx = (cx * 4.0f) - wx;
                float dxSq = dx * dx;
                if (dxSq > rSq)
                    continue;

                for (int cz = minCz; cz <= maxCz; cz++)
                {
                    float dz = (cz * 4.0f) - wz;
                    if (dxSq + (dz * dz) <= rSq)
                    {
                        _vehicleRoadCells.Add(PackVehicleCell(cx, cz));
                        if (_vehicleRoadCells.Count >= MaxVehicleRoadCells)
                            return;
                    }
                }
            }
        }

        private static bool IsRiverProfileId(string profileId)
        {
            if (string.IsNullOrEmpty(profileId))
                return false;
            if (_reusableRiverProfileIds.Contains(profileId))
                return true;

            return profileId.IndexOf("river", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   profileId.IndexOf("stream", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   profileId.IndexOf("canal", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void EnsureVehicleRoadCellsBuilt()
        {
            MapPropManager.MapPropDatabase db = null;
            IntPtr dbPtr = IntPtr.Zero;
            try
            {
                db = MapPropManager.MapPropDatabase.instance;
                if (!ReferenceEquals(db, null) && db != null)
                    dbPtr = db.Pointer;
            }
            catch { }

            Il2CppSystem.Collections.Generic.List<StreetColliderData> streetList = null;
            int streetCount = 0;
            try
            {
                streetList = StreetColliderManager.collidersToManage;
                streetCount = streetList != null ? streetList.Count : 0;
            }
            catch { }

            bool dbChanged = dbPtr != _vehicleRoadCacheDbPtr;
            bool streetChanged = streetCount != _vehicleRoadCacheStreetCount &&
                (_vehicleRoadCacheSyncAttempts < 3 || streetCount > _vehicleRoadCacheStreetCount + 4);
            bool retryEmpty = _vehicleRoadCells.Count == 0 &&
                _vehicleRoadCacheSyncAttempts < 2 &&
                Time.time >= _vehicleRoadCacheNextSyncTime;

            if (!dbChanged && !streetChanged && !retryEmpty)
                return;

            _vehicleRoadCells.Clear();
            _vehicleRoadCacheDbPtr = dbPtr;
            _vehicleRoadCacheStreetCount = streetCount;
            _vehicleRoadCacheNextSyncTime = Time.time + 5.0f;
            _vehicleRoadCacheSyncAttempts++;

            // Source 1: Corvostudio.Roads RoadNetwork (modern ER2 maps)
            if (db != null)
            {
                try
                {
                    Corvostudio.Roads.RoadSystem rs = db.roadSystem;
                    Corvostudio.Roads.RoadNetwork net = (rs != null && rs.network != null) ? rs.network : db.bakedRoadNetwork;
                    if (net != null && net.nodes != null && net.segments != null && net.segments.Count > 0)
                    {
                        _reusableRiverProfileIds.Clear();
                        var snapshots = db.bakedRoadProfileSnapshots;
                        if (snapshots != null)
                        {
                            int snapCount = snapshots.Count;
                            for (int i = 0; i < snapCount; i++)
                            {
                                RoadProfileSnapshot snap = snapshots[i];
                                if (snap != null &&
                                    snap.kind == Corvostudio.Roads.RoadProfileKind.River &&
                                    !string.IsNullOrEmpty(snap.profileId))
                                {
                                    _reusableRiverProfileIds.Add(snap.profileId);
                                }
                            }
                        }

                        _reusableRoadNodes.Clear();
                        var nodes = net.nodes;
                        int nodeCount = nodes.Count;
                        for (int i = 0; i < nodeCount; i++)
                        {
                            Corvostudio.Roads.RoadNode node = nodes[i];
                            if (node != null)
                            {
                                // Pack junction radius into node width upper bound only for endpoint disks when touched by non-river segments.
                                float endpointHalfW = Mathf.Max(node.width * 0.5f, node.junctionRadius > 0.5f ? node.junctionRadius : 0f);
                                _reusableRoadNodes[node.id] = new Vector4(node.positionX, node.junctionRadius > 0.5f ? endpointHalfW : node.width * 0.5f, node.positionZ, node.width);
                            }
                        }

                        var segments = net.segments;
                        int segCount = segments.Count;
                        for (int i = 0; i < segCount; i++)
                        {
                            if (_vehicleRoadCells.Count >= MaxVehicleRoadCells)
                                break;

                            Corvostudio.Roads.RoadSegment seg = segments[i];
                            if (seg == null)
                                continue;

                            // Strict river exclusion across all 4 signals so vehicles never treat rivers as roads.
                            if (rs != null && rs.SegmentIsRiver(seg.id))
                                continue;
                            Corvostudio.Roads.RoadProfile prof = seg.cachedProfile;
                            if (prof != null && (prof.IsRiver || prof.kind == Corvostudio.Roads.RoadProfileKind.River))
                                continue;
                            if (IsRiverProfileId(seg.profileId))
                                continue;

                            if (!_reusableRoadNodes.TryGetValue(seg.nodeAId, out Vector4 nA) ||
                                !_reusableRoadNodes.TryGetValue(seg.nodeBId, out Vector4 nB))
                            {
                                continue;
                            }

                            float halfWidth = Mathf.Clamp(Mathf.Max(nA.w, nB.w) * 0.5f, 2.5f, 10.0f) + 1.5f;
                            float endHalfA = Mathf.Clamp(nA.y, 2.5f, 14.0f) + 1.5f;
                            float endHalfB = Mathf.Clamp(nB.y, 2.5f, 14.0f) + 1.5f;

                            float dx = nB.x - nA.x;
                            float dz = nB.z - nA.z;
                            float segLen = Mathf.Sqrt((dx * dx) + (dz * dz));
                            int steps = Mathf.Clamp((int)(segLen * 0.5f) + 1, 1, 256);

                            MarkRoadDisk(nA.x, nA.z, endHalfA);
                            for (int s = 0; s <= steps; s++)
                            {
                                float t = steps > 0 ? ((float)s / steps) : 0f;
                                MarkRoadDisk(nA.x + (dx * t), nA.z + (dz * t), halfWidth);
                                if (_vehicleRoadCells.Count >= MaxVehicleRoadCells)
                                    break;
                            }
                            MarkRoadDisk(nB.x, nB.z, endHalfB);
                        }

                        _reusableRoadNodes.Clear();
                        _reusableRiverProfileIds.Clear();
                    }
                }
                catch
                {
                    _reusableRoadNodes.Clear();
                    _reusableRiverProfileIds.Clear();
                }
            }

            // Source 2: Legacy StreetColliderManager street pieces (oriented BoxCollider or bounded AABB)
            if (streetList != null && streetCount > 0 && _vehicleRoadCells.Count < MaxVehicleRoadCells)
            {
                try
                {
                    for (int i = 0; i < streetCount; i++)
                    {
                        if (_vehicleRoadCells.Count >= MaxVehicleRoadCells)
                            break;

                        StreetColliderData data = streetList[i];
                        if (data == null)
                            continue;

                        bool handledBox = false;
                        Collider col = data.collider;
                        if (col != null)
                        {
                            BoxCollider box = col.TryCast<BoxCollider>();
                            if (box != null && box.transform != null)
                            {
                                Vector3 ls = box.transform.lossyScale;
                                Vector3 localSize = box.size;
                                float worldX = Mathf.Abs(localSize.x * ls.x);
                                float worldZ = Mathf.Abs(localSize.z * ls.z);
                                float minAxis = Mathf.Min(worldX, worldZ);
                                float maxAxis = Mathf.Max(worldX, worldZ);
                                if (maxAxis >= 1.5f && minAxis <= 22.0f && maxAxis <= 85.0f)
                                {
                                    Vector3 center = box.center;
                                    Transform tf = box.transform;
                                    if (worldX >= worldZ)
                                    {
                                        float halfLen = localSize.x * 0.5f;
                                        float halfWidth = Mathf.Clamp(worldZ * 0.5f, 2.0f, 11.0f) + 1.5f;
                                        int steps = Mathf.Clamp((int)(worldX * 0.5f) + 1, 1, 64);
                                        for (int s = 0; s <= steps; s++)
                                        {
                                            float lx = Mathf.Lerp(-halfLen, halfLen, (float)s / steps);
                                            Vector3 wp = tf.TransformPoint(new Vector3(center.x + lx, center.y, center.z));
                                            MarkRoadDisk(wp.x, wp.z, halfWidth);
                                        }
                                    }
                                    else
                                    {
                                        float halfLen = localSize.z * 0.5f;
                                        float halfWidth = Mathf.Clamp(worldX * 0.5f, 2.0f, 11.0f) + 1.5f;
                                        int steps = Mathf.Clamp((int)(worldZ * 0.5f) + 1, 1, 64);
                                        for (int s = 0; s <= steps; s++)
                                        {
                                            float lz = Mathf.Lerp(-halfLen, halfLen, (float)s / steps);
                                            Vector3 wp = tf.TransformPoint(new Vector3(center.x, center.y, center.z + lz));
                                            MarkRoadDisk(wp.x, wp.z, halfWidth);
                                        }
                                    }
                                }
                                handledBox = true;
                            }
                        }

                        if (!handledBox)
                        {
                            Bounds b = data.bound;
                            float sizeX = b.size.x;
                            float sizeZ = b.size.z;
                            float minXZ = Mathf.Min(sizeX, sizeZ);
                            float maxXZ = Mathf.Max(sizeX, sizeZ);
                            if (maxXZ < 1.5f || minXZ > 22.0f || maxXZ > 85.0f)
                                continue;

                            int minCx = Mathf.RoundToInt((b.min.x - 1.0f) * 0.25f);
                            int maxCx = Mathf.RoundToInt((b.max.x + 1.0f) * 0.25f);
                            int minCz = Mathf.RoundToInt((b.min.z - 1.0f) * 0.25f);
                            int maxCz = Mathf.RoundToInt((b.max.z + 1.0f) * 0.25f);
                            for (int cx = minCx; cx <= maxCx; cx++)
                            {
                                for (int cz = minCz; cz <= maxCz; cz++)
                                {
                                    _vehicleRoadCells.Add(PackVehicleCell(cx, cz));
                                    if (_vehicleRoadCells.Count >= MaxVehicleRoadCells)
                                        break;
                                }
                                if (_vehicleRoadCells.Count >= MaxVehicleRoadCells)
                                    break;
                            }
                        }
                    }
                }
                catch { }
            }
        }

        private static byte ProbeVehicleCellClearance(Vector3 worldPos)
        {
            int mask = GetVehicleStaticObstacleMask();
            Vector3 outerCenter = new Vector3(worldPos.x, worldPos.y + 2.6f, worldPos.z);
            bool hitOuter = Physics.CheckBox(
                outerCenter,
                new Vector3(3.8f, 0.9f, 3.8f),
                Quaternion.identity,
                mask,
                QueryTriggerInteraction.Ignore);
            if (!hitOuter)
                return VehicleCellClearOpen;

            Vector3 innerCenter = new Vector3(worldPos.x, worldPos.y + 2.3f, worldPos.z);
            bool hitInner = Physics.CheckBox(
                innerCenter,
                new Vector3(2.2f, 0.85f, 2.2f),
                Quaternion.identity,
                mask,
                QueryTriggerInteraction.Ignore);
            return hitInner ? VehicleCellTightObstructed : VehicleCellModerateClutter;
        }

        private static byte GetOrClassifyVehicleCell(long cellKey, Vector3 worldPos, bool storeInCache = true)
        {
            if (_vehicleCellClearanceCache.TryGetValue(cellKey, out byte cached))
                return cached;

            byte cls = ProbeVehicleCellClearance(worldPos);
            if (storeInCache)
            {
                if (_vehicleCellClearanceCache.Count >= MaxVehicleClearanceCacheEntries)
                    _vehicleCellClearanceCache.Clear();
                _vehicleCellClearanceCache[cellKey] = cls;
            }
            return cls;
        }

        [HarmonyPatch(typeof(global::A.f), nameof(global::A.f.A), new[] { typeof(global::A.G), typeof(float) })]
        public static class A_f_EnqueueOrUpdateOpenNode_Patch
        {
            static void Prefix(global::A.f __instance, global::A.G __0, ref float __1)
            {
                if (!VisionConfig.EnableVehicleRoadAndClearPathing.Value)
                    return;
                if (!IsVehicleGrid(__instance))
                    return;
                if (__0 == null || __0.Pointer == IntPtr.Zero || __instance.pathIsInsideCollider)
                    return;

                float hCost = __0.h_cost;
                if (hCost <= 0.01f || float.IsNaN(hCost) || float.IsInfinity(hCost))
                    return;

                EnsureVehicleRoadCellsBuilt();

                Vector3 pos = __0.worldPosition;
                long cellKey = PackVehicleCell(pos);
                bool isRoad = pos.y >= -0.35f && _vehicleRoadCells.Count > 0 && _vehicleRoadCells.Contains(cellKey);
                byte clearance = GetOrClassifyVehicleCell(cellKey, pos, storeInCache: true);

                float desiredMult;
                if (isRoad)
                {
                    desiredMult = clearance == VehicleCellClearOpen ? 0.62f
                        : (clearance == VehicleCellModerateClutter ? 0.72f : 0.92f);
                    if (__1 > 1.05f)
                        desiredMult *= 1.10f;
                }
                else
                {
                    desiredMult = clearance == VehicleCellClearOpen ? 1.00f
                        : (clearance == VehicleCellModerateClutter ? 1.48f : 2.05f);
                    desiredMult *= (__1 > 0.01f ? __1 : 1.0f);
                }

                global::A.G prev = __0.PreviousNode;
                if (prev != null && prev.Pointer != IntPtr.Zero && prev.Pointer != __0.Pointer)
                {
                    Vector3 prevPos = prev.worldPosition;
                    float dx = pos.x - prevPos.x;
                    float dz = pos.z - prevPos.z;
                    float horizSq = (dx * dx) + (dz * dz);
                    if (horizSq > 0.25f)
                    {
                        float grade = Mathf.Abs(pos.y - prevPos.y) / Mathf.Sqrt(horizSq);
                        if (grade > 0.25f)
                        {
                            float slopePenalty = 1.0f + Mathf.Clamp01((grade - 0.25f) / 0.20f) * 0.45f;
                            desiredMult *= slopePenalty;
                        }
                    }
                }

                desiredMult = Mathf.Clamp(desiredMult, 0.55f, 3.20f);
                if (hCost < 400f)
                    desiredMult = Mathf.Lerp(__1, desiredMult, Mathf.Clamp01(hCost / 400f));

                __1 = desiredMult;
            }
        }

        private static bool IsRoadCellOrAdjacent(Vector3 worldPos)
        {
            int cx = Mathf.RoundToInt(worldPos.x * 0.25f);
            int cz = Mathf.RoundToInt(worldPos.z * 0.25f);
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dz = -1; dz <= 1; dz++)
                {
                    if (_vehicleRoadCells.Contains(PackVehicleCell(cx + dx, cz + dz)))
                        return true;
                }
            }
            return false;
        }

        [HarmonyPatch(typeof(global::A.f), nameof(global::A.f.A), new[] { typeof(global::A.G), typeof(global::A.G), typeof(byte) })]
        public static class A_f_CanSkipStringPullNode_Patch
        {
            static bool Prefix(global::A.f __instance, global::A.G __0, global::A.G __1, byte __2, ref bool __result)
            {
                if (!VisionConfig.EnableVehicleRoadAndClearPathing.Value)
                    return true;
                if (!IsVehicleGrid(__instance))
                    return true;
                if (__0 == null || __0.Pointer == IntPtr.Zero || __1 == null || __1.Pointer == IntPtr.Zero)
                    return true;

                Vector3 a = __0.worldPosition;
                Vector3 b = __1.worldPosition;
                Vector3 mid = (a + b) * 0.5f;
                long keyA = PackVehicleCell(a);
                long keyB = PackVehicleCell(b);
                long keyMid = PackVehicleCell(mid);

                bool hasRoads = _vehicleRoadCells.Count > 0;
                bool aRoad = hasRoads && _vehicleRoadCells.Contains(keyA);
                bool bRoad = hasRoads && _vehicleRoadCells.Contains(keyB);

                // Preserve road curve anchors while still string-pulling straight road segments cleanly.
                if (aRoad && bRoad)
                {
                    float chordSq = (a.x - b.x) * (a.x - b.x) + (a.z - b.z) * (a.z - b.z);
                    if (chordSq > 144f && !IsRoadCellOrAdjacent(mid))
                    {
                        __result = false;
                        return false;
                    }

                    if (chordSq > 324f)
                    {
                        Vector3 p1 = Vector3.Lerp(a, b, 0.333f);
                        Vector3 p2 = Vector3.Lerp(a, b, 0.667f);
                        if (!IsRoadCellOrAdjacent(p1) || !IsRoadCellOrAdjacent(p2))
                        {
                            __result = false;
                            return false;
                        }
                    }
                }

                // Prevent string-pulling from cutting across tight obstructed woods, fences, or rubble corners.
                byte midClear = GetOrClassifyVehicleCell(keyMid, mid, storeInCache: false);
                if (midClear == VehicleCellTightObstructed)
                {
                    byte aClear = GetOrClassifyVehicleCell(keyA, a, storeInCache: true);
                    byte bClear = GetOrClassifyVehicleCell(keyB, b, storeInCache: true);
                    if (aClear != VehicleCellTightObstructed || bClear != VehicleCellTightObstructed)
                    {
                        __result = false;
                        return false;
                    }
                }

                return true;
            }
        }

        [HarmonyPatch(typeof(global::A.f), nameof(global::A.f.A), new[] { typeof(bool) })]
        public static class A_f_ClearGrid_Patch
        {
            static void Postfix(global::A.f __instance)
            {
                if (IsVehicleGrid(__instance))
                    ClearVehiclePathingCaches(keepRoadMap: false);
            }
        }

        [HarmonyPatch(typeof(global::A.f), nameof(global::A.f.A), new[] { typeof(Vector3), typeof(float) })]
        public static class A_f_InvalidateRadius_Patch
        {
            static void Postfix(global::A.f __instance, Vector3 __0, float __1)
            {
                if (!IsVehicleGrid(__instance) || _vehicleCellClearanceCache.Count == 0)
                    return;

                int cellRadius = Mathf.Clamp(Mathf.CeilToInt((__1 + 5.0f) * 0.25f), 1, 16);
                int centerCx = Mathf.RoundToInt(__0.x * 0.25f);
                int centerCz = Mathf.RoundToInt(__0.z * 0.25f);
                for (int dx = -cellRadius; dx <= cellRadius; dx++)
                {
                    for (int dz = -cellRadius; dz <= cellRadius; dz++)
                        _vehicleCellClearanceCache.Remove(PackVehicleCell(centerCx + dx, centerCz + dz));
                }
            }
        }
    }
}
