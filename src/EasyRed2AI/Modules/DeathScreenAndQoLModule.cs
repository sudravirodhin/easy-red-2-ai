using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace EasyRed2HumanAI
{
    public static partial class VeteranAndTacticsPatches
    {
        internal struct DamageAttribution
        {
            public bool IsFriendlyFire;
            public bool IsSelfInflicted;
            public string WeaponOrSource;
            public string HitDetail;
            public int DistanceMeters;
            public float Timestamp;
        }

        private static readonly Dictionary<IntPtr, DamageAttribution> _lastDamageByVictim = new Dictionary<IntPtr, DamageAttribution>(32);
        private static string _activeExplosionWeaponName;
        private static bool _activeExplosionIsArtillery;
        private static bool _activeImpactHasContext;
        private static BulletData _activeImpactBulletData;
        private static bool _activeImpactIsMortar;
        private static Vector3 _activeImpactStartPos;

        public static void BeginProjectileImpactContext(Corvostudio.Weapons.BulletInstance inst, BulletData bulletData)
        {
            if (inst == null && bulletData == null)
            {
                _activeImpactHasContext = false;
                return;
            }

            _activeImpactHasContext = true;
            _activeImpactBulletData = bulletData ?? (inst != null ? inst.bulletData : null);
            _activeImpactIsMortar = inst != null && inst.isMortar;
            _activeImpactStartPos = inst != null ? inst.startPosition : Vector3.zero;
        }

        public static void EndProjectileImpactContext()
        {
            _activeImpactHasContext = false;
            _activeImpactBulletData = null;
            _activeImpactIsMortar = false;
        }

        // Active Death Screen Obituary Banner state (rendered on both DeathPanel and OnGUI overlay)
        public static string ActiveDeathAttributionText;
        public static bool ActiveDeathAttributionIsFF;
        public static bool ActiveDeathAttributionIsSelf;
        public static float ActiveDeathAttributionStartTime;
        public static float ActiveDeathAttributionExpireTime;
        public static IntPtr ActiveDeathAttributionVictimPtr;

        // Instant DeathPanel visibility state (bypasses staggered legacy Animation fade-in)
        private static float _deathPanelShowStartUnscaledTime;
        private static float _deathPanelHoldUntilUnscaledTime;
        private static float _deathPanelCleanupUntilUnscaledTime;
        private static IntPtr _deathPanelVictimPtr;

        // =====================================================================
        // 1. Extended Multiplayer Room Name Limit (8 -> 64 Characters)
        // =====================================================================
        public static void ApplyExtendedRoomNameLimits()
        {
            if (!VisionConfig.EnableMod.Value || !VisionConfig.EnableExtendedRoomNames.Value)
                return;

            try
            {
                ServerListMenu.maxServerNameLength = 64;
                ServerListMenu slm = ServerListMenu.instance;
                if (slm != null)
                {
                    if (slm.find_by_name_field != null && slm.find_by_name_field.characterLimit > 0 && slm.find_by_name_field.characterLimit < 64)
                    {
                        slm.find_by_name_field.characterLimit = 64;
                    }

                    var inputs = slm.GetComponentsInChildren<InputField>(true);
                    if (inputs != null)
                    {
                        for (int i = 0; i < inputs.Length; i++)
                        {
                            InputField inp = inputs[i];
                            if (inp != null && inp.characterLimit > 0 && inp.characterLimit <= 16)
                            {
                                inp.characterLimit = 64;
                            }
                        }
                    }

                    if (slm.campaignMenu != null)
                    {
                        var campInputs = slm.campaignMenu.GetComponentsInChildren<InputField>(true);
                        if (campInputs != null)
                        {
                            for (int i = 0; i < campInputs.Length; i++)
                            {
                                InputField inp = campInputs[i];
                                if (inp != null && inp.characterLimit > 0 && inp.characterLimit <= 16)
                                {
                                    inp.characterLimit = 64;
                                }
                            }
                        }
                    }
                }
            }
            catch { }
        }

        [HarmonyPatch(typeof(ServerListMenu), nameof(ServerListMenu.Awake))]
        public static class ServerListMenu_Awake_Patch
        {
            public static void Postfix()
            {
                ApplyExtendedRoomNameLimits();
            }
        }

        [HarmonyPatch(typeof(ServerListMenu), nameof(ServerListMenu.Start))]
        public static class ServerListMenu_Start_Patch
        {
            public static void Postfix()
            {
                ApplyExtendedRoomNameLimits();
            }
        }

        [HarmonyPatch(typeof(ServerListMenu), nameof(ServerListMenu.OnCreateMatchPressed))]
        public static class ServerListMenu_OnCreateMatchPressed_Patch
        {
            public static void Postfix()
            {
                ApplyExtendedRoomNameLimits();
            }
        }

        [HarmonyPatch(typeof(MatchData), nameof(MatchData.SetMultiplayerRoomSettings))]
        public static class MatchData_SetMultiplayerRoomSettings_Patch
        {
            public static void Prefix()
            {
                if (VisionConfig.EnableMod.Value && VisionConfig.EnableExtendedRoomNames.Value)
                {
                    ServerListMenu.maxServerNameLength = 64;
                }
            }
        }

        // =====================================================================
        // 2. Quick-Tap Squad Orders Keybind (Tab) to Spot in Look Direction
        // =====================================================================
        public static void UpdateQuickTapSpot(bool menuOpen)
        {
            if (!VisionConfig.EnableMod.Value || !VisionConfig.EnableQuickTapSpot.Value || menuOpen)
                return;

            PlayerController pc = PlayerController.currentController;
            Soldier player = pc != null ? pc.ControlledCharacter : null;
            if (pc != null &&
                !pc.lockRotationAndFiring &&
                player != null &&
                player.IsAlive &&
                !player.HasSurrended() &&
                player.joinedSquad != null)
            {
                VirtualGamepad gpad = GamepadsAPI.GetGamepad(0);
                if (gpad != null && gpad.GetButtonUp(GameInput.OrdersMenu, out float heldDuration, 0))
                {
                    float holdThreshold = PlayerController.holdButtonDuration > 0.05f ? PlayerController.holdButtonDuration : 0.25f;
                    if (heldDuration > 0f && heldDuration < holdThreshold)
                    {
                        PlayerController.TrySpotInLookDirection();
                    }
                }
            }
        }

        // =====================================================================
        // 3. Death Screen Kill Attribution & Soldier Age Obituary
        // =====================================================================
        private static string CleanUnityObjectName(string rawName)
        {
            if (string.IsNullOrWhiteSpace(rawName))
                return "";

            string cleaned = rawName.Replace("(Clone)", "").Replace('_', ' ').Trim();
            while (cleaned.Contains("  "))
                cleaned = cleaned.Replace("  ", " ");
            return cleaned;
        }

        private static string FormatYearLineWithAge(string yearLine)
        {
            if (string.IsNullOrWhiteSpace(yearLine))
                return "";

            string trimmed = yearLine.Trim();
            if (trimmed.IndexOf("Age ", StringComparison.OrdinalIgnoreCase) >= 0)
                return trimmed;

            int firstYear = -1;
            int secondYear = -1;
            int i = 0;
            while (i <= trimmed.Length - 4)
            {
                if (char.IsDigit(trimmed[i]) &&
                    char.IsDigit(trimmed[i + 1]) &&
                    char.IsDigit(trimmed[i + 2]) &&
                    char.IsDigit(trimmed[i + 3]) &&
                    (i == 0 || !char.IsDigit(trimmed[i - 1])) &&
                    (i + 4 == trimmed.Length || !char.IsDigit(trimmed[i + 4])))
                {
                    int yr = (trimmed[i] - '0') * 1000
                           + (trimmed[i + 1] - '0') * 100
                           + (trimmed[i + 2] - '0') * 10
                           + (trimmed[i + 3] - '0');
                    if (yr >= 1850 && yr <= 2050)
                    {
                        if (firstYear < 0)
                        {
                            firstYear = yr;
                        }
                        else
                        {
                            secondYear = yr;
                            break;
                        }
                    }
                    i += 4;
                    continue;
                }
                i++;
            }

            if (firstYear > 0 && secondYear > firstYear)
            {
                int age = secondYear - firstYear;
                if (age >= 12 && age <= 95)
                {
                    return $"{firstYear} - {secondYear} (Age {age})";
                }
            }

            return trimmed;
        }

        private static string FormatVehicleWeaponName(string vehName, Vehicle veh, BulletData bulletData)
        {
            if (bulletData != null)
            {
                float cal = bulletData.caliber;
                if (cal >= 30f)
                {
                    string shellType = bulletData.IsHe()
                        ? "HE Shell"
                        : (bulletData.IsAp() ? "AP Shell" : (bulletData.IsFlak() ? "Flak Shell" : "Cannon"));
                    return $"{vehName} ({Mathf.RoundToInt(cal)}mm {shellType})";
                }
                if (cal >= 18f)
                {
                    return $"{vehName} ({Mathf.RoundToInt(cal)}mm Autocannon)";
                }
                if (cal >= 12f)
                {
                    return $"{vehName} (Heavy MG)";
                }
                if (cal > 0f)
                {
                    return $"{vehName} (Machine Gun)";
                }
            }

            if (veh != null && veh.IsAirVehicle())
                return $"{vehName} (Bomb / Rocket)";

            return vehName;
        }

        private static string GetShooterWeaponOrVehicleName(Soldier shooter, BulletData bulletData = null)
        {
            if (shooter == null)
            {
                if (bulletData != null && bulletData.caliber >= 18f)
                {
                    return bulletData.caliber >= 30f
                        ? $"{Mathf.RoundToInt(bulletData.caliber)}mm {(bulletData.IsHe() ? "HE Shell" : "Cannon Shell")}"
                        : $"{Mathf.RoundToInt(bulletData.caliber)}mm Autocannon";
                }
                return "";
            }

            if (shooter.IsOnVehicle())
            {
                Vehicle veh = shooter.GetCurrentVehicle();
                string vehName = (veh != null && !string.IsNullOrWhiteSpace(veh.name))
                    ? CleanUnityObjectName(veh.name)
                    : "Vehicle";

                Seats seat = shooter.GetCurrentVehicleSeat();
                bool usingVehicleWeapon = shooter.IsUsingSeatTurret
                    || !shooter.CanUseHandItem
                    || (seat != null && seat.hasTurret)
                    || (bulletData != null && bulletData.caliber >= 12f);

                // Vehicle crewmen still carry their personal sidearm (e.g. Colt 1911) in GetHeldItem()
                // while operating a turret or vehicle weapon — never report the holstered sidearm.
                if (usingVehicleWeapon)
                {
                    return FormatVehicleWeaponName(vehName, veh, bulletData);
                }

                ItemObject heldInVeh = shooter.GetHeldItem();
                if (heldInVeh != null && !string.IsNullOrWhiteSpace(heldInVeh.name))
                    return $"{CleanUnityObjectName(heldInVeh.name)} ({vehName})";

                return FormatVehicleWeaponName(vehName, veh, bulletData);
            }

            // If a tank/autocannon shell hits after the crewman bailed out on foot carrying a pistol
            if (bulletData != null && bulletData.caliber >= 18f && !shooter.IsATUnit() && !shooter.hasGrenadeLauncher)
            {
                return bulletData.caliber >= 30f
                    ? $"{Mathf.RoundToInt(bulletData.caliber)}mm {(bulletData.IsHe() ? "HE Shell" : (bulletData.IsAp() ? "AP Shell" : "Cannon Shell"))}"
                    : $"{Mathf.RoundToInt(bulletData.caliber)}mm Autocannon";
            }

            ItemObject held = shooter.GetHeldItem();
            if (held != null && !string.IsNullOrWhiteSpace(held.name))
                return CleanUnityObjectName(held.name);

            GenericGun curWep = shooter.GetHeldGun();
            if (curWep != null && !string.IsNullOrWhiteSpace(curWep.name))
                return CleanUnityObjectName(curWep.name);

            return "";
        }

        private static string FormatBodyPart(BodyPartType part)
        {
            switch (part)
            {
                case BodyPartType.head:
                    return "Headshot";
                case BodyPartType.chest:
                    return "Torso";
                case BodyPartType.arm_l:
                case BodyPartType.arm_r:
                    return "Arm";
                case BodyPartType.leg_l:
                case BodyPartType.leg_r:
                    return "Leg";
                default:
                    return "Torso";
            }
        }

        private static bool IsPlayerCharacter(Soldier s)
        {
            if (s == null || s.Pointer == IntPtr.Zero)
                return false;

            if (s.IsPlayer() || PlayerController.CharacterIsPlayer(s))
                return true;

            Soldier cur = Soldier.CurrentControlledSoldierOrNull();
            return cur != null && cur.Pointer == s.Pointer;
        }

        [HarmonyPatch(typeof(Corvostudio.Weapons.Explosion), nameof(Corvostudio.Weapons.Explosion.CreateExplosion))]
        public static class Explosion_CreateExplosion_Patch
        {
            public static void Prefix(Vector3 __0, float __1, float __2, float __3, Soldier __4, GameObject __5, HitType __6, bool __7)
            {
                try
                {
                    // __7 is canDamage; ignore non-damaging visual explosions
                    if (!VisionConfig.EnableMod.Value || !VisionConfig.EnableDeathScreenAttribution.Value || !__7)
                        return;

                    string expSource = "";
                    bool isOffMapArtilleryOrAir = false;

                    // 1. Check if this explosion was triggered by an impacting projectile (BulletInstance.OnHit)
                    if (_activeImpactHasContext)
                    {
                        BulletData bd = _activeImpactBulletData;
                        float cal = bd != null ? bd.caliber : 0f;
                        ShellType st = bd != null ? bd.shellType : ShellType.HE;

                        if (__4 != null && __4.IsOnVehicle())
                        {
                            Vehicle shooterVeh = __4.GetCurrentVehicle();
                            string vName = (shooterVeh != null && !string.IsNullOrWhiteSpace(shooterVeh.name))
                                ? CleanUnityObjectName(shooterVeh.name)
                                : "Vehicle";

                            if (shooterVeh != null && shooterVeh.IsAirVehicle())
                            {
                                isOffMapArtilleryOrAir = true;
                                expSource = st == ShellType.Rocket
                                    ? $"{vName} (Aircraft Rocket)"
                                    : (cal >= 45f ? $"{vName} (Aerial Bomb)" : $"{vName} ({Mathf.RoundToInt(cal)}mm Cannon)");
                            }
                            else
                            {
                                expSource = FormatVehicleWeaponName(vName, shooterVeh, bd);
                            }
                        }
                        else if (_activeImpactIsMortar)
                        {
                            if (cal >= 130f)
                            {
                                isOffMapArtilleryOrAir = true;
                                expSource = $"{Mathf.RoundToInt(cal)}mm Heavy Artillery Barrage";
                            }
                            else if (cal >= 95f)
                            {
                                isOffMapArtilleryOrAir = true;
                                expSource = $"{Mathf.RoundToInt(cal)}mm Artillery Barrage";
                            }
                            else if (cal >= 45f)
                            {
                                expSource = $"{Mathf.RoundToInt(cal)}mm Mortar Shell";
                            }
                            else
                            {
                                isOffMapArtilleryOrAir = true;
                                expSource = "Artillery / Mortar Barrage";
                            }
                        }
                        else if (__4 != null)
                        {
                            if (st == ShellType.Rocket || st == ShellType.HEAT || __4.IsATUnit())
                            {
                                string held = GetShooterWeaponOrVehicleName(__4, null);
                                string tag = st == ShellType.Rocket ? "AT Rocket" : "HEAT";
                                expSource = !string.IsNullOrEmpty(held) ? $"{held} ({tag})" : $"Anti-Tank {tag}";
                            }
                            else if (__4.hasGrenadeLauncher)
                            {
                                string held = GetShooterWeaponOrVehicleName(__4, null);
                                expSource = !string.IsNullOrEmpty(held) ? $"{held} (Rifle Grenade)" : "Rifle Grenade";
                            }
                            else if (cal >= 95f)
                            {
                                isOffMapArtilleryOrAir = true;
                                expSource = $"{Mathf.RoundToInt(cal)}mm Artillery Shell";
                            }
                            else if (cal >= 30f)
                            {
                                expSource = $"{Mathf.RoundToInt(cal)}mm HE Shell";
                            }
                        }
                        else if (cal >= 95f)
                        {
                            isOffMapArtilleryOrAir = true;
                            expSource = $"{Mathf.RoundToInt(cal)}mm Artillery Barrage";
                        }
                    }

                    // 2. Check ignoreHittable (__5) for thrown ItemGrenade or exploding Vehicle
                    if (string.IsNullOrEmpty(expSource) && __5 != null)
                    {
                        ItemGrenade gren = __5.GetComponent<ItemGrenade>();
                        if (gren == null)
                            gren = __5.GetComponentInParent<ItemGrenade>();

                        if (gren != null && !string.IsNullOrWhiteSpace(gren.name))
                        {
                            string gName = CleanUnityObjectName(gren.name);
                            string gType = gren.grenadeType == GrenadeType.ATGrenade
                                ? "AT Grenade"
                                : (gren.grenadeType == GrenadeType.phosporusGrenade ? "White Phosphorus" : "Frag Grenade");
                            expSource = gName.IndexOf("Grenade", StringComparison.OrdinalIgnoreCase) >= 0
                                ? gName
                                : $"{gName} ({gType})";
                        }
                        else
                        {
                            Vehicle expVeh = __5.GetComponentInParent<Vehicle>();
                            if (expVeh != null && !string.IsNullOrWhiteSpace(expVeh.name))
                                expSource = $"{CleanUnityObjectName(expVeh.name)} (Vehicle Detonation)";
                        }
                    }

                    // 3. Fallback classification by responsible unit & blast parameters
                    if (string.IsNullOrEmpty(expSource))
                    {
                        if (__4 != null && __4.IsOnVehicle())
                        {
                            Vehicle shooterVeh = __4.GetCurrentVehicle();
                            if (shooterVeh != null && !string.IsNullOrWhiteSpace(shooterVeh.name))
                            {
                                string vName = CleanUnityObjectName(shooterVeh.name);
                                if (shooterVeh.IsAirVehicle())
                                {
                                    isOffMapArtilleryOrAir = true;
                                    expSource = $"{vName} (Aerial Bomb / Rocket)";
                                }
                                else
                                {
                                    expSource = $"{vName} (HE Shell)";
                                }
                            }
                            else
                            {
                                expSource = "Vehicle HE Shell";
                            }
                        }
                        else if (__4 == null)
                        {
                            if (__3 >= 8f)
                            {
                                isOffMapArtilleryOrAir = true;
                                expSource = "Artillery Barrage";
                            }
                            else
                            {
                                expSource = "Landmine / Explosive";
                            }
                        }
                        else
                        {
                            // __2 is maxPenetration: >= 30f on a thrown explosive indicates an AT Grenade or Satchel Charge
                            if (__2 >= 30f)
                            {
                                expSource = "Anti-Tank Grenade / Satchel";
                            }
                            else
                            {
                                expSource = "Frag Grenade";
                            }
                        }
                    }

                    _activeExplosionWeaponName = expSource;
                    _activeExplosionIsArtillery = isOffMapArtilleryOrAir || (__4 == null);

                    Soldier player = Soldier.CurrentControlledSoldierOrNull();
                    if (player != null && player.Pointer != IntPtr.Zero)
                    {
                        Vector3 playerPos = player.GetCenterOfUnit();
                        float blastDist = Vector3.Distance(__0, playerPos);
                        // __3 is explosionRadius
                        if (blastDist <= Mathf.Max(18f, __3 * 4.0f))
                        {
                            int distMeters;
                            if (isOffMapArtilleryOrAir || __4 == null || __4.Pointer == player.Pointer)
                            {
                                // For off-map artillery/aerial bombs or self-damage, report distance from blast impact point
                                distMeters = Mathf.RoundToInt(blastDist);
                            }
                            else if (_activeImpactHasContext && _activeImpactStartPos.sqrMagnitude > 1f)
                            {
                                distMeters = Mathf.RoundToInt(Vector3.Distance(_activeImpactStartPos, playerPos));
                            }
                            else
                            {
                                distMeters = Mathf.RoundToInt(Vector3.Distance(__4.GetCenterOfUnit(), playerPos));
                            }

                            bool isSelf = __4 != null && __4.Pointer == player.Pointer;
                            bool isFF = __4 != null && !isSelf && !string.IsNullOrEmpty(__4.faction) && __4.faction == player.faction;

                            string blastDetail = blastDist <= Mathf.Max(2.5f, __3 * 0.45f)
                                ? "Direct Blast"
                                : "Blast Wave";

                            _lastDamageByVictim[player.Pointer] = new DamageAttribution
                            {
                                IsSelfInflicted = isSelf,
                                IsFriendlyFire = isFF,
                                WeaponOrSource = expSource,
                                HitDetail = blastDetail,
                                DistanceMeters = Mathf.Max(0, distMeters),
                                Timestamp = Time.time
                            };
                        }
                    }
                }
                catch { }
            }

            public static void Postfix()
            {
                _activeExplosionWeaponName = null;
                _activeExplosionIsArtillery = false;
            }
        }

        [HarmonyPatch(typeof(Bullet), nameof(Bullet.BulletDamage))]
        public static class Bullet_BulletDamage_Patch
        {
            public static void Prefix(ImpactSpecifier __0, BulletData __1, Vector3 __2, int __3, Soldier __4)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value || !VisionConfig.EnableDeathScreenAttribution.Value || __0 == null)
                        return;

                    Soldier victim = null;
                    string hitDetail = "Torso";

                    BodyPart bp = __0.TryCast<BodyPart>();
                    if (bp != null && bp.unit != null)
                    {
                        victim = bp.unit.TryCast<Soldier>();
                        hitDetail = FormatBodyPart(bp.bodyPart);
                    }
                    else
                    {
                        ItemHelmet helmet = __0.TryCast<ItemHelmet>();
                        if (helmet != null && helmet.User != null)
                        {
                            victim = helmet.User;
                            hitDetail = "Headshot (Helmet)";
                        }
                        else
                        {
                            VehicleDamagablePart vPart = __0.TryCast<VehicleDamagablePart>();
                            if (vPart != null && vPart.rootVeh != null)
                            {
                                Soldier curPlayer = Soldier.CurrentControlledSoldierOrNull();
                                if (curPlayer != null &&
                                    curPlayer.IsOnVehicle() &&
                                    curPlayer.GetCurrentVehicle() != null &&
                                    curPlayer.GetCurrentVehicle().Pointer == vPart.rootVeh.Pointer)
                                {
                                    victim = curPlayer;
                                    hitDetail = "Vehicle Hit";
                                }
                            }
                        }
                    }

                    if (victim == null || !IsPlayerCharacter(victim))
                        return;

                    bool fromExplosion = !string.IsNullOrEmpty(_activeExplosionWeaponName);
                    string expWeaponName = _activeExplosionWeaponName;
                    int preservedDistMeters = -1;

                    // If a flying shrapnel BulletInstance from an explosion hits a few frames after CreateExplosion returned,
                    // preserve the explosion's WeaponOrSource instead of overwriting it with the shooter's holstered sidearm.
                    if (!fromExplosion &&
                        _lastDamageByVictim.TryGetValue(victim.Pointer, out DamageAttribution recentAttr) &&
                        (Time.time - recentAttr.Timestamp) <= 0.45f &&
                        (string.Equals(recentAttr.HitDetail, "Direct Blast", StringComparison.Ordinal) ||
                         string.Equals(recentAttr.HitDetail, "Blast Wave", StringComparison.Ordinal) ||
                         recentAttr.HitDetail.StartsWith("Shrapnel", StringComparison.Ordinal)))
                    {
                        fromExplosion = true;
                        expWeaponName = recentAttr.WeaponOrSource;
                        preservedDistMeters = recentAttr.DistanceMeters;
                    }

                    string weaponOrSource = fromExplosion
                        ? expWeaponName
                        : GetShooterWeaponOrVehicleName(__4, __1);

                    if (fromExplosion)
                    {
                        hitDetail = hitDetail.StartsWith("Headshot", StringComparison.Ordinal)
                            ? $"Shrapnel {hitDetail}"
                            : $"Shrapnel ({hitDetail})";
                    }

                    bool isSelf = __4 != null && __4.Pointer == victim.Pointer;
                    bool isFF = __4 != null && !isSelf && !string.IsNullOrEmpty(__4.faction) && __4.faction == victim.faction;

                    // Note: __3 in Bullet.BulletDamage is int damage, NOT distance; always compute distance in meters
                    int distMeters;
                    if (preservedDistMeters >= 0)
                    {
                        distMeters = preservedDistMeters;
                    }
                    else if (_activeExplosionIsArtillery)
                    {
                        distMeters = Mathf.RoundToInt(Vector3.Distance(__2, victim.GetCenterOfUnit()));
                    }
                    else if (__4 != null && !isSelf)
                    {
                        distMeters = Mathf.RoundToInt(Vector3.Distance(__4.GetCenterOfUnit(), victim.GetCenterOfUnit()));
                    }
                    else
                    {
                        distMeters = Mathf.RoundToInt(Vector3.Distance(__2, victim.GetCenterOfUnit()));
                    }

                    _lastDamageByVictim[victim.Pointer] = new DamageAttribution
                    {
                        IsSelfInflicted = isSelf,
                        IsFriendlyFire = isFF,
                        WeaponOrSource = !string.IsNullOrEmpty(weaponOrSource) ? weaponOrSource : (_activeExplosionIsArtillery ? "Artillery / Mortar Barrage" : "Enemy Fire"),
                        HitDetail = hitDetail,
                        DistanceMeters = Mathf.Max(0, distMeters),
                        Timestamp = Time.time
                    };
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(DeathPanel), nameof(DeathPanel.ShowDeath))]
        public static class DeathPanel_ShowDeath_Patch
        {
            public static void Postfix(Soldier __0, string __1, bool __2)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value || !VisionConfig.EnableDeathScreenAttribution.Value || __0 == null)
                        return;

                    DeathPanel dp = DeathPanel.instance;

                    // If the soldier surrendered or is still alive (showDeath == false), do NOT display cause-of-death attribution
                    if (!__2 || __0.HasSurrended() || __0.IsAlive)
                    {
                        _lastDamageByVictim.Remove(__0.Pointer);
                        ActiveDeathAttributionExpireTime = 0f;
                        ActiveDeathAttributionText = null;

                        if (dp != null)
                            ActivateInstantDeathPanel(dp, __0.Pointer);

                        return;
                    }

                    Text yearLabel = dp != null ? dp.text_year : null;
                    string yearLineWithAge = "";
                    int yearFontSize = 38;

                    if (yearLabel != null)
                    {
                        yearFontSize = yearLabel.fontSize;
                        if (yearLabel.resizeTextForBestFit && yearLabel.resizeTextMaxSize > yearFontSize)
                            yearFontSize = yearLabel.resizeTextMaxSize;
                        if (yearFontSize < 38)
                            yearFontSize = 38;

                        yearLabel.resizeTextForBestFit = false;
                        yearLabel.fontSize = yearFontSize;
                        yearLabel.supportRichText = true;
                        yearLabel.horizontalOverflow = HorizontalWrapMode.Overflow;
                        yearLabel.verticalOverflow = VerticalWrapMode.Overflow;

                        string curYearText = yearLabel.text ?? "";
                        int nlIdx = curYearText.IndexOf('\n');
                        string baseYearLine = nlIdx >= 0 ? curYearText.Substring(0, nlIdx) : curYearText;
                        yearLineWithAge = FormatYearLineWithAge(baseYearLine);
                    }

                    bool hasAttr = _lastDamageByVictim.TryGetValue(__0.Pointer, out DamageAttribution attr);
                    _lastDamageByVictim.Remove(__0.Pointer);

                    if (!hasAttr || Time.time - attr.Timestamp > 120f)
                    {
                        if (yearLabel != null && !string.IsNullOrEmpty(yearLineWithAge))
                            yearLabel.text = $"<size={yearFontSize}>{yearLineWithAge}</size>";

                        if (dp != null)
                            ActivateInstantDeathPanel(dp, __0.Pointer);

                        ActiveDeathAttributionExpireTime = 0f;
                        ActiveDeathAttributionText = null;
                        return;
                    }

                    bool bledOut = (Time.time - attr.Timestamp) > 1.5f;

                    string prefixTag = "";
                    string subColor = "#D8D2C2";
                    if (attr.IsSelfInflicted)
                    {
                        prefixTag = "[SELF-INFLICTED] ";
                        subColor = "#FFB347";
                    }
                    else if (attr.IsFriendlyFire)
                    {
                        prefixTag = "[FRIENDLY FIRE] ";
                        subColor = "#FF6666";
                    }

                    List<string> detailParts = new List<string>(4);
                    if (bledOut)
                    {
                        detailParts.Add("Bled out");
                    }

                    string cleanWep = "";
                    if (!string.IsNullOrEmpty(attr.WeaponOrSource))
                    {
                        cleanWep = attr.WeaponOrSource.Replace('_', ' ').Trim();
                        while (cleanWep.Contains("  "))
                            cleanWep = cleanWep.Replace("  ", " ");
                        if (!string.IsNullOrEmpty(cleanWep))
                            detailParts.Add(cleanWep);
                    }

                    if (!string.IsNullOrEmpty(attr.HitDetail))
                    {
                        string cleanHit = attr.HitDetail.Replace('_', ' ').Trim();
                        while (cleanHit.Contains("  "))
                            cleanHit = cleanHit.Replace("  ", " ");
                        if (!string.IsNullOrEmpty(cleanHit) &&
                            !string.Equals(cleanWep, cleanHit, StringComparison.OrdinalIgnoreCase))
                        {
                            detailParts.Add(cleanHit);
                        }
                    }

                    if (!attr.IsSelfInflicted || attr.DistanceMeters > 0)
                    {
                        detailParts.Add($"{attr.DistanceMeters}m");
                    }

                    string causeLine = detailParts.Count > 0
                        ? prefixTag + string.Join("  \u2022  ", detailParts)
                        : (prefixTag + "Killed in Action").Trim();

                    bool shownOnNativeDeathPanel = false;
                    if (yearLabel != null)
                    {
                        yearLabel.text = !string.IsNullOrEmpty(causeLine)
                            ? $"<size={yearFontSize}>{yearLineWithAge}</size>\n<size=26><color={subColor}>{causeLine}</color></size>"
                            : $"<size={yearFontSize}>{yearLineWithAge}</size>";
                        shownOnNativeDeathPanel = true;
                    }

                    if (dp != null)
                        ActivateInstantDeathPanel(dp, __0.Pointer);

                    if (shownOnNativeDeathPanel)
                    {
                        ActiveDeathAttributionExpireTime = 0f;
                        ActiveDeathAttributionText = null;
                    }
                    else
                    {
                        ActiveDeathAttributionText = causeLine;
                        ActiveDeathAttributionIsFF = attr.IsFriendlyFire;
                        ActiveDeathAttributionIsSelf = attr.IsSelfInflicted;
                        ActiveDeathAttributionStartTime = Time.unscaledTime;
                        ActiveDeathAttributionExpireTime = Time.unscaledTime + 8.5f;
                        ActiveDeathAttributionVictimPtr = __0.Pointer;
                    }
                }
                catch { }
            }
        }

        private static void ActivateInstantDeathPanel(DeathPanel dp, IntPtr victimPtr)
        {
            if (dp == null)
                return;

            DisableDeathPanelRaycasts(dp);

            Animation anim = dp.GetComponent<Animation>();
            if (anim != null)
            {
                if (anim.GetStateCount() > 0)
                {
                    AnimationState state = anim.GetStateAtIndex(0);
                    if (state != null && state.length > 0f)
                    {
                        state.enabled = true;
                        state.weight = 1f;
                        state.time = state.length * 0.72f;
                        anim.Sample();
                    }
                }
                anim.Stop();
            }

            _deathPanelShowStartUnscaledTime = Time.unscaledTime;
            _deathPanelHoldUntilUnscaledTime = Time.unscaledTime + 5.5f;
            _deathPanelCleanupUntilUnscaledTime = Time.unscaledTime + 15.0f;
            _deathPanelVictimPtr = victimPtr;

            ApplyDeathPanelAlpha(dp, 1.0f);
        }

        private static void DisableDeathPanelRaycasts(DeathPanel dp)
        {
            if (dp == null)
                return;

            if (dp.text_reason != null)
                dp.text_reason.raycastTarget = false;
            if (dp.text_name != null)
                dp.text_name.raycastTarget = false;
            if (dp.text_year != null)
                dp.text_year.raycastTarget = false;

            Graphic rootGraphic = dp.GetComponent<Graphic>();
            if (rootGraphic != null)
                rootGraphic.raycastTarget = false;

            CanvasGroup cg = dp.GetComponent<CanvasGroup>();
            if (cg != null)
            {
                cg.blocksRaycasts = false;
                cg.interactable = false;
            }
        }

        private static void FinishInstantDeathPanel(DeathPanel dp)
        {
            _deathPanelCleanupUntilUnscaledTime = 0f;
            if (dp == null)
                return;

            Animation anim = dp.GetComponent<Animation>();
            if (anim != null)
            {
                if (anim.GetStateCount() > 0)
                {
                    AnimationState state = anim.GetStateAtIndex(0);
                    if (state != null)
                    {
                        state.enabled = true;
                        state.weight = 1f;
                        state.normalizedTime = 1.0f;
                        anim.Sample();
                    }
                }
                anim.Stop();
            }

            ApplyDeathPanelAlpha(dp, 0.0f);
            DisableDeathPanelRaycasts(dp);
        }

        public static void MaintainInstantDeathPanel()
        {
            if (_deathPanelCleanupUntilUnscaledTime <= 0f)
                return;

            float now = Time.unscaledTime;
            DeathPanel dp = DeathPanel.instance;
            if (dp == null || now > _deathPanelCleanupUntilUnscaledTime)
            {
                FinishInstantDeathPanel(dp);
                return;
            }

            DisableDeathPanelRaycasts(dp);

            Animation anim = dp.GetComponent<Animation>();
            if (anim != null && anim.isPlaying)
                anim.Stop();

            if (now < _deathPanelHoldUntilUnscaledTime)
            {
                float elapsed = now - _deathPanelShowStartUnscaledTime;

                if (elapsed > 0.35f)
                {
                    Soldier cur = Soldier.CurrentControlledSoldierOrNull();
                    if (cur != null && cur.IsAlive && cur.Pointer != _deathPanelVictimPtr)
                    {
                        // Player has taken control of a new living soldier -> dismiss immediately
                        _deathPanelHoldUntilUnscaledTime = now;
                    }
                    else if (elapsed > 1.5f && DeathPanel.IsSkipping())
                    {
                        // Player explicitly held Skip after 1.5s -> fade out smoothly
                        _deathPanelHoldUntilUnscaledTime = Mathf.Min(_deathPanelHoldUntilUnscaledTime, now + 0.35f);
                    }
                }
            }

            if (now < _deathPanelHoldUntilUnscaledTime)
            {
                float remaining = _deathPanelHoldUntilUnscaledTime - now;
                float alpha = remaining < 0.45f ? Mathf.Clamp01(remaining / 0.45f) : 1.0f;
                ApplyDeathPanelAlpha(dp, alpha);
            }
            else
            {
                FinishInstantDeathPanel(dp);
            }
        }

        private static void ApplyDeathPanelAlpha(DeathPanel dp, float alpha)
        {
            if (dp == null)
                return;

            CanvasGroup cg = dp.GetComponent<CanvasGroup>();
            if (cg != null)
                cg.alpha = alpha;

            SetTextAlpha(dp.text_reason, alpha);
            SetTextAlpha(dp.text_name, alpha);
            SetTextAlpha(dp.text_year, alpha);
        }

        private static void SetTextAlpha(Text txt, float alpha)
        {
            if (txt == null)
                return;

            Color c = txt.color;
            if (Mathf.Abs(c.a - alpha) > 0.002f)
            {
                c.a = alpha;
                txt.color = c;
            }
        }
    }
}
