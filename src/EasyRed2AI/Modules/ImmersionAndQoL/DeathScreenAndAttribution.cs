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
        private static readonly List<IntPtr> _reusableExpiredAttrKeys = new List<IntPtr>(32);

        internal static void PruneExpiredDamageAttributions(float now, bool forceClear = false)
        {
            if (forceClear)
            {
                _lastDamageByVictim.Clear();
                return;
            }

            if (_lastDamageByVictim.Count == 0)
                return;

            _reusableExpiredAttrKeys.Clear();
            foreach (var kv in _lastDamageByVictim)
            {
                if (now - kv.Value.Timestamp > 90.0f || now < kv.Value.Timestamp)
                    _reusableExpiredAttrKeys.Add(kv.Key);
            }

            for (int i = 0; i < _reusableExpiredAttrKeys.Count; i++)
            {
                _lastDamageByVictim.Remove(_reusableExpiredAttrKeys[i]);
            }
        }
        private static string _activeExplosionWeaponName;
        private static bool _activeExplosionIsArtillery;
        private static bool _activeImpactHasContext;
        private static BulletData _activeImpactBulletData;
        private static bool _activeImpactIsMortar;
        private static Vector3 _activeImpactStartPos;
        private static Vector3 _activeImpactDirection;
        private static IntPtr _activeDamageAttackerPtr;

        public static void BeginProjectileImpactContext(Corvostudio.Weapons.BulletInstance inst, BulletData bulletData, Vector3 hitDirection = default)
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
            _activeImpactDirection = hitDirection.sqrMagnitude > 0.0001f ? hitDirection.normalized : Vector3.zero;
        }

        public static void EndProjectileImpactContext()
        {
            _activeImpactHasContext = false;
            _activeImpactBulletData = null;
            _activeImpactIsMortar = false;
            _activeImpactDirection = Vector3.zero;
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

        private static string FormatCannonShellLabel(BulletData bulletData)
        {
            if (bulletData == null)
                return "Cannon";
            switch (bulletData.shellType)
            {
                case ShellType.HE:
                    return "HE Shell";
                case ShellType.APHE:
                    return "APHE Shell";
                case ShellType.HEAT:
                    return "HEAT Shell";
                case ShellType.AP:
                    return "AP Shell";
                case ShellType.Rocket:
                    return "Rocket";
                default:
                    if (bulletData.IsFlak())
                        return "Flak Shell";
                    return "Cannon";
            }
        }

        private static string FormatVehicleWeaponName(string vehName, Vehicle veh, BulletData bulletData)
        {
            if (bulletData != null)
            {
                float cal = bulletData.caliber;
                if (cal >= 30f)
                {
                    string shellType = FormatCannonShellLabel(bulletData);
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
                        ? $"{Mathf.RoundToInt(bulletData.caliber)}mm {FormatCannonShellLabel(bulletData)}"
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
            public static void Prefix(Vector3 __0, float __1, float __2, float __3, Soldier __4, GameObject __5, ref HitType __6, bool __7, out IntPtr __state)
            {
                __state = _activeDamageAttackerPtr;
                try
                {
                    // __7 is canDamage; ignore non-damaging visual explosions
                    if (!VisionConfig.EnableMod.Value || !__7)
                        return;

                    if (__4 != null && __4.Pointer != IntPtr.Zero)
                    {
                        VisionPatches.RecordExplosionSound(__0, __3, __4);
                        _activeDamageAttackerPtr = __4.Pointer;
                    }
                    else
                    {
                        Vehicle srcVeh = __5 != null ? (__5.GetComponent<Vehicle>() ?? __5.GetComponentInParent<Vehicle>()) : null;
                        string srcVehFac = (srcVeh != null && srcVeh.Pointer != IntPtr.Zero) ? srcVeh.GetVehicleFaction() : null;
                        VisionPatches.RecordExplosionSound(__0, __3, null, srcVehFac);

                        IntPtr resolvedVehicleKillerPtr = new IntPtr(-1);
                        if (srcVeh != null &&
                            srcVeh.Pointer != IntPtr.Zero &&
                            IsOnlineScoreboardFixActive() &&
                            _localPlayerDamagedVehicles.TryGetValue(srcVeh.Pointer, out float hitTime) &&
                            (Time.time - hitTime) <= 20.0f)
                        {
                            Soldier curLocal = Soldier.CurrentControlledSoldierOrNull();
                            if (curLocal != null && curLocal.Pointer != IntPtr.Zero)
                                resolvedVehicleKillerPtr = curLocal.Pointer;
                        }
                        _activeDamageAttackerPtr = resolvedVehicleKillerPtr;
                    }

                    // Reclassify Molotov / phosphorus / incendiary grenade shatters from HitType.Explosion -> HitType.Fire
                    // BEFORE native CreateExplosion runs so tank tracks (VehicleDamageableTrackPart) are 100% immune!
                    TryReclassifyIncendiaryGrenadeExplosion(__0, __5, ref __6);

                    if (!VisionConfig.EnableDeathScreenAttribution.Value)
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

            public static void Postfix(IntPtr __state)
            {
                _activeExplosionWeaponName = null;
                _activeExplosionIsArtillery = false;
                _activeDamageAttackerPtr = __state;
            }
        }

        public struct BulletDamagePatchState
        {
            public Soldier BotCalloutVictim;
            public IntPtr PrevAttackerPtr;
        }

        [HarmonyPatch(typeof(Bullet), nameof(Bullet.BulletDamage))]
        public static class Bullet_BulletDamage_Patch
        {
            public static bool Prefix(
                ImpactSpecifier __0,
                BulletData __1,
                Vector3 __2,
                int __3,
                Soldier __4,
                ref Creature __result,
                out BulletDamagePatchState __state)
            {
                __state = new BulletDamagePatchState { PrevAttackerPtr = _activeDamageAttackerPtr };
                try
                {
                    if (!VisionConfig.EnableMod.Value || __0 == null)
                        return true;

                    if (__4 != null && __4.Pointer != IntPtr.Zero)
                        _activeDamageAttackerPtr = __4.Pointer;
                    else if (__state.PrevAttackerPtr == IntPtr.Zero)
                        _activeDamageAttackerPtr = IntPtr.Zero;

                    Soldier victim = null;
                    VehicleDamagablePart hitVehiclePart = null;
                    string hitDetail = "Torso";
                    bool isDirectBodyOrHelmetHit = false;

                    BodyPart bp = __0.TryCast<BodyPart>();
                    if (bp != null && bp.unit != null)
                    {
                        victim = bp.unit.TryCast<Soldier>();
                        hitDetail = FormatBodyPart(bp.bodyPart);
                        isDirectBodyOrHelmetHit = true;
                    }
                    else
                    {
                        ItemHelmet helmet = __0.TryCast<ItemHelmet>();
                        if (helmet != null && helmet.User != null)
                        {
                            victim = helmet.User;
                            hitDetail = "Headshot (Helmet)";
                            isDirectBodyOrHelmetHit = true;
                        }
                        else
                        {
                            hitVehiclePart = __0.TryCast<VehicleDamagablePart>();
                            if (hitVehiclePart != null && hitVehiclePart.rootVeh != null)
                            {
                                Soldier curPlayer = Soldier.CurrentControlledSoldierOrNull();
                                if (curPlayer != null &&
                                    curPlayer.IsOnVehicle() &&
                                    curPlayer.GetCurrentVehicle() != null &&
                                    curPlayer.GetCurrentVehicle().Pointer == hitVehiclePart.rootVeh.Pointer)
                                {
                                    // Ignore harmless non-penetrating small-arms ricochets and non-explosive friendly hull hits
                                    // so stray SMG/rifle pings on tank armor never overwrite _lastDamageByVictim with "Vehicle Hit".
                                    bool fromExp = !string.IsNullOrEmpty(_activeExplosionWeaponName);
                                    bool sameFactionBullet = !fromExp &&
                                                             __4 != null &&
                                                             __4.Pointer != IntPtr.Zero &&
                                                             AiCore.AreSameSideFactions(AiCore.GetCachedFaction(__4), AiCore.GetCachedFaction(curPlayer));
                                    bool canThreatenArmor = fromExp ||
                                                            __1 == null ||
                                                            __1.IsHe() ||
                                                            __1.IsFire() ||
                                                            __1.explosion_penetration > 2.0f ||
                                                            __1.mmPenetration > hitVehiclePart.armorThickness;
                                    if (!sameFactionBullet && canThreatenArmor)
                                    {
                                        victim = curPlayer;
                                        hitDetail = "Vehicle Hit";
                                    }
                                }
                            }
                        }
                    }

                    // Enclosed Armored Ground Vehicle Crew Protection:
                    // Stop small-arms bullets (<12mm / non-AP / non-HE / non-Fire) from killing buttoned-up crewmen
                    // (!seat.showUnitSet) inside intact armored hulls via BodyPart/Helmet collider clipping or
                    // point-blank friendly/enemy muzzles poking inside the outer hull collider.
                    if (VisionConfig.EnableRealisticVehicleArmorAndTopAttack.Value &&
                        isDirectBodyOrHelmetHit &&
                        victim != null &&
                        victim.Pointer != IntPtr.Zero &&
                        victim.IsOnVehicle() &&
                        string.IsNullOrEmpty(_activeExplosionWeaponName) &&
                        !ActiveCrewSpallContext)
                    {
                        Vehicle groundVeh = victim.GetCurrentVehicle();
                        if (groundVeh != null &&
                            groundVeh.Pointer != IntPtr.Zero &&
                            !groundVeh.IsAirVehicle() &&
                            !groundVeh.IsCoverDestroyed())
                        {
                            float effArmor = GetEffectiveVehicleExplosionArmor(groundVeh);
                            if (effArmor >= 8.0f &&
                                (__1 == null ||
                                 (!__1.IsHe() &&
                                  !__1.IsFlak() &&
                                  !__1.IsFire() &&
                                  __1.explosion_penetration < 5.0f &&
                                  __1.mmPenetration < Mathf.Max(12.0f, effArmor))) &&
                                IsSoldierInEnclosedVehicleSeat(groundVeh, victim))
                            {
                                EnclosedCrewBulletClipsBlockedCount++;
                                __result = null;
                                return false;
                            }
                        }
                    }

                    // Realistic Airplane Canopy vs. Armored Belly/Seat Pan Protection:
                    // Allow direct canopy/window BodyPart & Helmet hits from level/above (dogfighting aircraft,
                    // open cockpits) and from heavy .50 cal+ rounds (>= 12.5mm), while stopping rifle-caliber
                    // ground fire (< 12.5mm, non-HE, non-Flak) coming up from steep below through the floor/engine.
                    if (isDirectBodyOrHelmetHit &&
                        victim != null &&
                        victim.Pointer != IntPtr.Zero &&
                        victim.IsOnVehicle() &&
                        __1 != null &&
                        !__1.IsHe() &&
                        !__1.IsFlak() &&
                        __1.caliber < 12.5f &&
                        string.IsNullOrEmpty(_activeExplosionWeaponName))
                    {
                        Vehicle victimVeh = victim.GetCurrentVehicle();
                        if (victimVeh != null &&
                            victimVeh.Pointer != IntPtr.Zero &&
                            victimVeh.IsAirVehicle() &&
                            !victimVeh.IsCoverDestroyed() &&
                            !victimVeh.IsDisabled())
                        {
                            bool fromSteepBelow = _activeImpactHasContext &&
                                                  _activeImpactDirection.sqrMagnitude > 0.001f &&
                                                  _activeImpactDirection.y > 0.30f;

                            if (!fromSteepBelow && !_activeImpactHasContext &&
                                __4 != null && __4.Pointer != IntPtr.Zero &&
                                __4.transform != null && victim.transform != null)
                            {
                                Vehicle shooterVeh = __4.IsOnVehicle() ? __4.GetCurrentVehicle() : null;
                                bool shooterAirborne = shooterVeh != null && shooterVeh.Pointer != IntPtr.Zero && shooterVeh.IsAirVehicle();
                                if (!shooterAirborne && (victim.transform.position.y - __4.transform.position.y) >= 15.0f)
                                    fromSteepBelow = true;
                            }

                            if (fromSteepBelow)
                            {
                                AirplanePilotMgHitsBlockedCount++;
                                __result = null;
                                return false;
                            }
                        }
                    }

                    // Attribute direct local-player hits & vehicle hits (runs only after armor/airplane blocks pass)
                    if (IsOnlineScoreboardFixActive() && __4 != null && __4.Pointer != IntPtr.Zero && IsPlayerCharacter(__4))
                    {
                        if (isDirectBodyOrHelmetHit && victim != null && victim.Pointer != IntPtr.Zero)
                        {
                            TagVictimDamagedByLocalPlayer(__4, victim, __1 != null && __1.IsFire());
                        }
                        else if (hitVehiclePart != null && hitVehiclePart.rootVeh != null && hitVehiclePart.rootVeh.Pointer != IntPtr.Zero)
                        {
                            RecordLocalPlayerDamagedVehicle(hitVehiclePart.rootVeh.Pointer);
                        }
                    }

                    // Track pre-hit alive enemy victim for AI Bot Kill Callouts (VoiceClip.enemyDown — "I got him!")
                    if (isDirectBodyOrHelmetHit &&
                        VisionConfig.EnableTacticalVoiceCallouts.Value &&
                        victim != null &&
                        victim.Pointer != IntPtr.Zero &&
                        victim.IsAlive &&
                        __4 != null &&
                        __4.Pointer != IntPtr.Zero &&
                        AiCore.IsLocallyOwnedBot(__4) &&
                        !AiCore.AreSameSideFactions(AiCore.GetCachedFaction(__4), AiCore.GetCachedFaction(victim)))
                    {
                        __state.BotCalloutVictim = victim;
                    }

                    if (!VisionConfig.EnableDeathScreenAttribution.Value || victim == null || !IsPlayerCharacter(victim))
                        return true;

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

                return true;
            }

            public static void Postfix(Soldier __4, BulletDamagePatchState __state)
            {
                _activeDamageAttackerPtr = __state.PrevAttackerPtr;

                Soldier botVictim = __state.BotCalloutVictim;
                if (botVictim == null)
                    return;

                try
                {
                    // Confirmed lethal hit by an AI bot: match vanilla DamageFeedback.OnSoldierHit (~25% roll)
                    // routed through unified Spectator-safe TrySayTacticalVoice + Co-op SyncSay!
                    if (!botVictim.IsAlive &&
                        __4 != null &&
                        __4.IsAlive &&
                        !__4.isBleeding &&
                        __4.GetSuppressionValue() < 55)
                    {
                        int chance = __4.IsMarksman() ? 35 : 25;
                        if (UnityEngine.Random.Range(0, 100) < chance)
                        {
                            BotKillCalloutsCount++;
                            TrySayTacticalVoice(__4, __4.joinedSquad, VoiceManager.VoiceClip.enemyDown, 0.25f);
                        }
                    }
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

                    bool staleVehicleHit = hasAttr &&
                                           string.Equals(attr.HitDetail, "Vehicle Hit", StringComparison.Ordinal) &&
                                           (Time.time - attr.Timestamp) > 8.0f;

                    if (!hasAttr || Time.time - attr.Timestamp > 120f || staleVehicleHit)
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

        // ============================================================================
        // MULTIPLAYER CASUALTIES, PRISONERS & LOST VEHICLES SYNC FIX
        // Native Root Causes (GameAssembly.dll):
        //  1) BattleManager.AddBattleStat (0x18058B940) reads CountBattleStat -> Lua_Globals.get
        //     from Photon Room Custom Properties (which only update AFTER the server round-trip!)
        //     and calls Lua_Globals.set -> SetCustomProperties. Any 2+ casualties, surrenders, or
        //     vehicle losses within one RTT overwrite each other to baseline + 1!
        //  2) BattleManager.OnWin sends the EndBattle RPC on PhotonView immediately while async
        //     Room Custom Properties (and post-victory ragdoll/burning-wreck casualties) arrive
        //     later, while EndBattleGUI.EndBattle (0x180488C10) formats its 6 Text labels once!
        // Fix:
        //  - Maintain an authoritative synchronous short[2, 4] ledger on MasterClient, coalescing
        //    Photon Lua_Globals.set updates at 4 Hz (zero SetCustomProperties spam) and flushing
        //    synchronously on BattleManager.OnWin.
        //  - Refresh EndBattleGUI's 6 casualty/prisoner/vehicle labels at 4 Hz on both Host and
        //    Clients whenever any synced stat changes while the end-of-battle table is open!
        // ============================================================================
        // Native BattleManager.AddBattleStat (0x18058B9C6) & CountBattleStat (0x18058C58E) box
        // (int)side (0=Invaders, 1=Defenders) and (int)stat (1=Deceased, 2=Prisoners, 3=LostVehicles)
        // with Int32_TypeInfo into "BS.{0}.{1}" -> "BS.0.1" .. "BS.1.3".
        private static readonly string[,] _battleStatIds = new string[2, 4]
        {
            { null, "BS.0.1", "BS.0.2", "BS.0.3" },
            { null, "BS.1.1", "BS.1.2", "BS.1.3" }
        };

        private static readonly short[,] _authBattleStats = new short[2, 4];
        private static readonly short[] _lastDisplayedEndStats = new short[6] { -1, -1, -1, -1, -1, -1 };
        private static IntPtr _seededBattleManagerPtr;
        private static bool _wasMasterClientWhenSeeded;
        private static int _dirtyBattleStatsMask;
        private static float _nextBattleStatFlushUnscaledTime;
        private static float _nextEndBattleUiRefreshUnscaledTime;

        public static void ResetMultiplayerBattleStatLedger()
        {
            Array.Clear(_authBattleStats, 0, _authBattleStats.Length);
            for (int i = 0; i < _lastDisplayedEndStats.Length; i++)
                _lastDisplayedEndStats[i] = -1;
            _seededBattleManagerPtr = IntPtr.Zero;
            _wasMasterClientWhenSeeded = false;
            _dirtyBattleStatsMask = 0;
            _nextBattleStatFlushUnscaledTime = 0f;
            _nextEndBattleUiRefreshUnscaledTime = 0f;
            ResetLocalPlayerScoreboardLedger();
        }

        private static bool IsOnlineMasterForStatSync()
        {
            return VisionConfig.EnableMod.Value &&
                   VisionConfig.EnableMultiplayerBattleStatSyncFix.Value &&
                   Photon.Pun.PhotonNetwork.InRoom &&
                   !Photon.Pun.PhotonNetwork.OfflineMode &&
                   Photon.Pun.PhotonNetwork.IsMasterClient;
        }

        private static bool TryMapBattleStatIndices(
            BattleManager.BattleSide side,
            BattleManager.BattleStatistic stat,
            out int sideIdx,
            out int statIdx,
            out string statId)
        {
            sideIdx = -1;
            statIdx = -1;
            statId = null;

            if (side == BattleManager.BattleSide.Invaders)
                sideIdx = 0;
            else if (side == BattleManager.BattleSide.Defenders)
                sideIdx = 1;
            else
                return false;

            if (stat == BattleManager.BattleStatistic.Deceased)
                statIdx = 1;
            else if (stat == BattleManager.BattleStatistic.Prisoners)
                statIdx = 2;
            else if (stat == BattleManager.BattleStatistic.LostVehicles)
                statIdx = 3;
            else
                return false;

            statId = _battleStatIds[sideIdx, statIdx];
            return statId != null;
        }

        private static void EnsureMasterBattleStatLedgerSeeded()
        {
            BattleManager bm = BattleManager.instance;
            IntPtr bmPtr = bm != null ? bm.Pointer : IntPtr.Zero;
            if (bmPtr != IntPtr.Zero && _seededBattleManagerPtr == bmPtr && _wasMasterClientWhenSeeded)
                return;

            for (int s = 0; s <= 1; s++)
            {
                for (int st = 1; st <= 3; st++)
                {
                    short val = 0;
                    string key = _battleStatIds[s, st];
                    try
                    {
                        if (!string.IsNullOrEmpty(key) && Lua_Globals.has(key))
                        {
                            var dv = Lua_Globals.get(key);
                            if (dv != null)
                            {
                                int n = (int)Math.Round(dv.Number);
                                if (n > 0)
                                    val = (short)Mathf.Clamp(n, 0, short.MaxValue);
                            }
                        }
                    }
                    catch { }
                    _authBattleStats[s, st] = val;
                }
            }

            _seededBattleManagerPtr = bmPtr;
            _wasMasterClientWhenSeeded = true;
            _dirtyBattleStatsMask = 0;
        }

        private static void FlushDirtyMasterBattleStats(bool sendImmediate)
        {
            if (!IsOnlineMasterForStatSync())
                return;

            EnsureMasterBattleStatLedgerSeeded();
            if (_dirtyBattleStatsMask == 0)
                return;

            int mask = _dirtyBattleStatsMask;
            _dirtyBattleStatsMask = 0;
            ScoreboardCoalescedFlushesCount++;
            HitchProfiler.NotifyNetPropFlush();

            for (int s = 0; s <= 1; s++)
            {
                for (int st = 1; st <= 3; st++)
                {
                    int bit = 1 << (s * 4 + st);
                    if ((mask & bit) == 0)
                        continue;

                    string key = _battleStatIds[s, st];
                    short val = _authBattleStats[s, st];
                    try
                    {
                        Lua_Globals.set(MoonSharp.Interpreter.DynValue.NewNumber(val), key);
                    }
                    catch { }
                }
            }

            _nextBattleStatFlushUnscaledTime = Time.unscaledTime + 2.0f;
            if (sendImmediate)
            {
                try { Photon.Pun.PhotonNetwork.SendAllOutgoingCommands(); } catch { }
            }
        }

        private static void RefreshEndBattleGuiLabels(EndBattleGUI gui, bool forceUpdate)
        {
            if (gui == null || gui.Pointer == IntPtr.Zero)
                return;

            short atkCas = BattleManager.CountBattleStat(BattleManager.BattleSide.Invaders, BattleManager.BattleStatistic.Deceased, out _);
            short atkPris = BattleManager.CountBattleStat(BattleManager.BattleSide.Invaders, BattleManager.BattleStatistic.Prisoners, out _);
            short atkVeh = BattleManager.CountBattleStat(BattleManager.BattleSide.Invaders, BattleManager.BattleStatistic.LostVehicles, out _);
            short defCas = BattleManager.CountBattleStat(BattleManager.BattleSide.Defenders, BattleManager.BattleStatistic.Deceased, out _);
            short defPris = BattleManager.CountBattleStat(BattleManager.BattleSide.Defenders, BattleManager.BattleStatistic.Prisoners, out _);
            short defVeh = BattleManager.CountBattleStat(BattleManager.BattleSide.Defenders, BattleManager.BattleStatistic.LostVehicles, out _);

            if (forceUpdate || _lastDisplayedEndStats[0] != atkCas)
            {
                _lastDisplayedEndStats[0] = atkCas;
                if (gui.attackers_casualties != null)
                    gui.attackers_casualties.text = atkCas.ToString();
            }
            if (forceUpdate || _lastDisplayedEndStats[1] != atkPris)
            {
                _lastDisplayedEndStats[1] = atkPris;
                if (gui.attackers_prisoners != null)
                    gui.attackers_prisoners.text = atkPris.ToString();
            }
            if (forceUpdate || _lastDisplayedEndStats[2] != atkVeh)
            {
                _lastDisplayedEndStats[2] = atkVeh;
                if (gui.attackers_vehiclesLost != null)
                    gui.attackers_vehiclesLost.text = atkVeh.ToString();
            }
            if (forceUpdate || _lastDisplayedEndStats[3] != defCas)
            {
                _lastDisplayedEndStats[3] = defCas;
                if (gui.defenders_casualties != null)
                    gui.defenders_casualties.text = defCas.ToString();
            }
            if (forceUpdate || _lastDisplayedEndStats[4] != defPris)
            {
                _lastDisplayedEndStats[4] = defPris;
                if (gui.defenders_prisoners != null)
                    gui.defenders_prisoners.text = defPris.ToString();
            }
            if (forceUpdate || _lastDisplayedEndStats[5] != defVeh)
            {
                _lastDisplayedEndStats[5] = defVeh;
                if (gui.defenders_vehiclesLost != null)
                    gui.defenders_vehiclesLost.text = defVeh.ToString();
            }
        }

        public static void UpdateMultiplayerBattleStatSync()
        {
            if (!VisionConfig.EnableMod.Value || !VisionConfig.EnableMultiplayerBattleStatSyncFix.Value)
                return;

            if (!Photon.Pun.PhotonNetwork.InRoom || Photon.Pun.PhotonNetwork.OfflineMode)
            {
                if (_wasMasterClientWhenSeeded)
                {
                    _wasMasterClientWhenSeeded = false;
                    _dirtyBattleStatsMask = 0;
                }
                if (_localScoreboardSeeded || _localScoreboardDirty)
                {
                    ResetLocalPlayerScoreboardLedger();
                }
                return;
            }

            if (!Photon.Pun.PhotonNetwork.IsMasterClient)
            {
                _wasMasterClientWhenSeeded = false;
                _dirtyBattleStatsMask = 0;
            }
            else
            {
                EnsureMasterBattleStatLedgerSeeded();
                float now = Time.unscaledTime;
                if (_dirtyBattleStatsMask != 0 &&
                    (now >= _nextBattleStatFlushUnscaledTime || _nextBattleStatFlushUnscaledTime - now > 5.0f))
                {
                    FlushDirtyMasterBattleStats(sendImmediate: false);
                }
            }

            EnsureLocalPlayerScoreboardSeeded();
            float nowUnscaled = Time.unscaledTime;
            if (nowUnscaled >= _nextPtfoRoomAndObjPollTime || _nextPtfoRoomAndObjPollTime - nowUnscaled > 5.0f)
            {
                _nextPtfoRoomAndObjPollTime = nowUnscaled + 1.0f;
                Soldier curLocal = Soldier.CurrentControlledSoldierOrNull();
                if (curLocal != null && curLocal.Pointer != IntPtr.Zero)
                {
                    string fac = AiCore.GetCachedFaction(curLocal);
                    if (!string.IsNullOrEmpty(fac))
                        _lastKnownLocalPlayerFaction = fac;
                }

                bool isMaster = Photon.Pun.PhotonNetwork.IsMasterClient;
                IntPtr roomPtr = Photon.Pun.PhotonNetwork.CurrentRoom != null ? Photon.Pun.PhotonNetwork.CurrentRoom.Pointer : IntPtr.Zero;
                bool desiredPtfo = VisionConfig.EnableMod.Value &&
                                   VisionConfig.EnableMultiplayerBattleStatSyncFix.Value &&
                                   VisionConfig.EnableObjectiveTeamplayScoring.Value;
                if (!isMaster || !_wasMasterForPtfoSync || roomPtr != _lastSyncedPtfoRoomPtr || desiredPtfo != _lastPublishedPtfoValue)
                {
                    SyncPtfoRoomPropertyIfMaster();
                }

                if (IsOnlinePtfoScoringActive())
                {
                    PollLocalPlayerPtfoObjectives(Time.time);
                }
            }

            if (_localScoreboardDirty &&
                (nowUnscaled >= _nextLocalScoreboardFlushUnscaledTime || _nextLocalScoreboardFlushUnscaledTime - nowUnscaled > 5.0f))
            {
                FlushDirtyLocalPlayerScoreboardStats(sendImmediate: false);
                if (MiniMapGUI.ShowingPlayerList())
                    _pendingOpenPlayerListRefresh = true;
            }

            if (_pendingOpenPlayerListRefresh &&
                (nowUnscaled >= _nextOpenPlayerListRefreshUnscaledTime || _nextOpenPlayerListRefreshUnscaledTime - nowUnscaled > 5.0f))
            {
                _pendingOpenPlayerListRefresh = false;
                _nextOpenPlayerListRefreshUnscaledTime = nowUnscaled + 0.40f;
                if (MiniMapGUI.ShowingPlayerList())
                {
                    try { MiniMapGUI.RefreshPlayerList(); } catch { }
                }
            }

            EndBattleGUI endGui = BattleManager.endBattleGUI;
            if (endGui != null && endGui.animationStarted)
            {
                float now = Time.unscaledTime;
                if (now >= _nextEndBattleUiRefreshUnscaledTime || _nextEndBattleUiRefreshUnscaledTime - now > 5.0f)
                {
                    _nextEndBattleUiRefreshUnscaledTime = now + 0.25f;
                    RefreshEndBattleGuiLabels(endGui, forceUpdate: false);
                }
            }
        }

        [HarmonyPatch(typeof(BattleManager), nameof(BattleManager.AddBattleStat))]
        public static class BattleManager_AddBattleStat_Patch
        {
            public static bool Prefix(
                BattleManager.BattleSide __0,
                BattleManager.BattleStatistic __1,
                short __2)
            {
                try
                {
                    if (!IsOnlineMasterForStatSync() ||
                        !TryMapBattleStatIndices(__0, __1, out int sIdx, out int stIdx, out _))
                    {
                        return true;
                    }

                    if (__2 == 0)
                        return false;

                    EnsureMasterBattleStatLedgerSeeded();
                    int updated = Mathf.Clamp(_authBattleStats[sIdx, stIdx] + __2, 0, short.MaxValue);
                    _authBattleStats[sIdx, stIdx] = (short)updated;
                    _dirtyBattleStatsMask |= (1 << (sIdx * 4 + stIdx));
                    ScoreboardVanillaPacketsSuppressedCount++;
                    return false;
                }
                catch { }
                return true;
            }
        }

        [HarmonyPatch(typeof(BattleManager), nameof(BattleManager.CountBattleStat))]
        public static class BattleManager_CountBattleStat_Patch
        {
            public static bool Prefix(
                BattleManager.BattleSide __0,
                BattleManager.BattleStatistic __1,
                ref string __2,
                ref short __result)
            {
                try
                {
                    if (!IsOnlineMasterForStatSync() ||
                        !TryMapBattleStatIndices(__0, __1, out int sIdx, out int stIdx, out string statId))
                    {
                        return true;
                    }

                    EnsureMasterBattleStatLedgerSeeded();
                    __2 = statId;
                    __result = _authBattleStats[sIdx, stIdx];
                    return false;
                }
                catch { }
                return true;
            }
        }

        [HarmonyPatch(typeof(BattleManager), nameof(BattleManager.OnWin))]
        public static class BattleManager_OnWin_Patch
        {
            public static void Prefix(string __0)
            {
                try
                {
                    // Pre-surrender surviving losing-side AI soldiers BEFORE native BattleManager.OnWin sends its
                    // SyncWin RPC so final prisoner counts are captured in our pre-OnWin Lua_Globals.set flush and
                    // arrive on remote clients in the exact same Photon outgoing batch as SyncWin!
                    if (IsOnlineMasterForStatSync() && !string.IsNullOrEmpty(__0))
                    {
                        var aliveList = Creature.aliveCreatures;
                        if (aliveList != null)
                        {
                            for (int i = aliveList.Count - 1; i >= 0; i--)
                            {
                                Creature c = aliveList[i];
                                if (c == null || c.Pointer == IntPtr.Zero || !c.IsAlive || c.IsDead)
                                    continue;

                                Soldier s = c.TryCast<Soldier>();
                                if (s == null ||
                                    s.Pointer == IntPtr.Zero ||
                                    s.IsPlayer() ||
                                    s.HasSurrended() ||
                                    AiCore.AreSameSideFactions(s.faction, __0))
                                {
                                    continue;
                                }

                                try { s.SurrenderSynched(); } catch { }
                            }
                        }
                    }

                    FlushDirtyMasterBattleStats(sendImmediate: true);
                    FlushDirtyLocalPlayerScoreboardStats(sendImmediate: true);
                }
                catch { }
            }

            public static void Postfix()
            {
                try
                {
                    FlushDirtyMasterBattleStats(sendImmediate: true);
                    FlushDirtyLocalPlayerScoreboardStats(sendImmediate: true);
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(EndBattleGUI), nameof(EndBattleGUI.EndBattle))]
        public static class EndBattleGUI_EndBattle_Patch
        {
            public static void Postfix(EndBattleGUI __instance)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value ||
                        !VisionConfig.EnableMultiplayerBattleStatSyncFix.Value ||
                        __instance == null)
                    {
                        return;
                    }

                    FlushDirtyMasterBattleStats(sendImmediate: true);
                    FlushDirtyLocalPlayerScoreboardStats(sendImmediate: true);
                    RefreshEndBattleGuiLabels(__instance, forceUpdate: true);
                }
                catch { }
            }
        }

        // ============================================================================
        // MULTIPLAYER SCOREBOARD KILLS, DEATHS, SCORE & SCORE-DESCENDING SORT FIX
        // Native Root Causes (GameAssembly.dll):
        //  1) DamageFeedback.OnSoldierHit (0x1808866B5) skips CustomProperties["kills"]
        //     whenever !target.GetSyncher().isPlayer, and skips BOTH "kills" and "score"
        //     at 0x180886665 whenever !target.IsAlive is false (every bleed-out / burning
        //     finish, vehicle explosion crew kill, and roadkill awards 0 kills & 0 score!).
        //  2) Soldier.Kill (0x180751974) gates CustomProperties["deaths"] behind
        //     gameObject.activeInHierarchy — skipping every death inside an enclosed
        //     tank/armored car/plane (!seat.showUnitSet), as well as abandoning a
        //     mortally downed (isBleeding) soldier via squad-switch or Respawn.
        //  3) ResourcesManager.SetPlayerCustomProperty read-modifies Photon LocalPlayer
        //     CustomProperties using async RTT values and triggers back-to-back RPC/prop
        //     writes. Native MiniMapGUI.ShowPlayerList (0x1804A49A8) iterates raw
        //     PhotonNetwork.PlayerList without sorting.
        // Fix:
        //  - Authoritative synchronous short ledger for LocalPlayer ("kills", "deaths",
        //    "score" boxed as System.Int16), coalesced at 4 Hz (immediate on Tab/Win).
        //  - Suppresses vanilla's stale 1-key "kills"/"deaths"/"score" property packets.
        //  - Sorts each MiniMapGUI player list container by Score DESC (then Kills DESC,
        //    Deaths ASC, ActorNumber ASC) while preserving native slot Y spacing.
        // ============================================================================
        private struct LocalPlayerVictimTag
        {
            public float Timestamp;
            public bool IsEnemy;
            public bool IsHumanPlayer;
            public bool WasMortalOrFireOrRoadkill;
            public bool WasObjectiveZone;
        }

        private struct PtfoObjectiveState
        {
            public MissionObjective ObjRef;
            public IntPtr PhasePtr;
            public bool Initialized;
            public bool WasHeldByMySide;
            public bool WasSecured;
            public bool CanAttractMySide;
            public float LastProgress01;
            public float LastRawComplete;
            public float LastCaptureAwardTime;
            public bool LocalWasInsideLastPoll;
        }

        private struct PlayerScoreboardRowSortEntry
        {
            public RectTransform Rect;
            public short Score;
            public short Kills;
            public short Deaths;
            public int ActorNumber;
        }

        public static int ScoreboardCoalescedFlushesCount { get; private set; }
        public static int ScoreboardVanillaPacketsSuppressedCount { get; private set; }
        public static int PtfoTotalPointsEarned { get; private set; }
        public static int PtfoObjKillBonusesCount { get; private set; }
        public static int PtfoActiveTicksCount { get; private set; }
        public static int PtfoCapturesCount { get; private set; }
        public static int PtfoRevivesCount { get; private set; }
        public static int PtfoVehicleDisablesCount { get; private set; }

        private static short _authLocalKills;
        private static short _authLocalDeaths;
        private static short _authLocalScore;
        private static bool _localScoreboardSeeded;
        private static bool _localScoreboardDirty;
        private static bool _isInternalScoreboardFlush;
        private static bool _inBattleLoaderOnJoinedRoom;
        private static bool _pendingOpenPlayerListRefresh;
        private static IntPtr _seededLocalScoreboardRoomPtr;
        private static float _nextLocalScoreboardFlushUnscaledTime;
        private static float _nextOpenPlayerListRefreshUnscaledTime;

        private static IntPtr _lastSyncedPtfoRoomPtr;
        private static bool _wasMasterForPtfoSync;
        private static bool _lastPublishedPtfoValue;
        private static bool _remoteHostPtfoKnown;
        private static bool _remoteHostPtfoEnabled;
        private static float _nextPtfoRoomAndObjPollTime;
        private static float _ptfoActiveObjectiveAccumSec;
        private static float _lastPtfoPollTime;
        private static IntPtr _lastPtfoPhasePtr;
        private static string _lastKnownLocalPlayerFaction;
        private static IntPtr _pendingLocalFriendlySyringePatientPtr;
        private static float _pendingLocalFriendlySyringeExpireTime;

        private static Il2CppSystem.Object _propKeyKillsObj;
        private static Il2CppSystem.Object _propKeyDeathsObj;
        private static Il2CppSystem.Object _propKeyScoreObj;
        private static Il2CppSystem.Object _propKeyRoomPtfoObj;
        private static Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<short> _shortBoxBuffer;
        private static Il2CppSystem.Array _shortBoxArrayView;

        private static readonly Dictionary<IntPtr, LocalPlayerVictimTag> _localPlayerDamagedVictims = new Dictionary<IntPtr, LocalPlayerVictimTag>(64);
        private static readonly Dictionary<IntPtr, float> _localPlayerDamagedVehicles = new Dictionary<IntPtr, float>(16);
        private static readonly HashSet<IntPtr> _creditedKillVictimPtrs = new HashSet<IntPtr>();
        private static readonly HashSet<IntPtr> _creditedLocalDeathSoldierPtrs = new HashSet<IntPtr>();
        private static readonly HashSet<IntPtr> _abandonedBleedingLocalSoldiers = new HashSet<IntPtr>();
        private static readonly List<IntPtr> _reusableScoreboardPruneKeys = new List<IntPtr>(32);

        private static readonly List<MissionObjective> _ptfoScratchObjectives = new List<MissionObjective>(16);
        private static readonly Dictionary<IntPtr, PtfoObjectiveState> _ptfoObjectiveStates = new Dictionary<IntPtr, PtfoObjectiveState>(16);
        private static readonly HashSet<IntPtr> _creditedPtfoDisabledVehiclePtrs = new HashSet<IntPtr>();
        private static readonly Dictionary<IntPtr, float> _recentPtfoRevivedPatients = new Dictionary<IntPtr, float>(16);

        private static readonly List<Transform> _reusableRowParents = new List<Transform>(4);
        private static readonly List<List<PlayerScoreboardRowSortEntry>> _reusableParentGroups = new List<List<PlayerScoreboardRowSortEntry>>(4);
        private static readonly List<Vector2> _reusableSlotPositions = new List<Vector2>(32);

        private static void EnsureScoreboardPropertyKeys()
        {
            if (_propKeyKillsObj != null)
                return;

            _propKeyKillsObj = ((Il2CppSystem.String)"kills").Cast<Il2CppSystem.Object>();
            _propKeyDeathsObj = ((Il2CppSystem.String)"deaths").Cast<Il2CppSystem.Object>();
            _propKeyScoreObj = ((Il2CppSystem.String)"score").Cast<Il2CppSystem.Object>();
            _propKeyRoomPtfoObj = ((Il2CppSystem.String)"bh_ptfo").Cast<Il2CppSystem.Object>();
        }

        private static Il2CppSystem.Object BoxIl2CppInt16(short val)
        {
            if (_shortBoxBuffer == null)
            {
                _shortBoxBuffer = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<short>(1);
                _shortBoxArrayView = _shortBoxBuffer.Cast<Il2CppSystem.Array>();
            }
            _shortBoxBuffer[0] = val;
            return _shortBoxArrayView.GetValue(0);
        }

        private static bool TryReadPlayerShortProp(ExitGames.Client.Photon.Hashtable props, Il2CppSystem.Object key, out short value)
        {
            value = 0;
            if (props == null || key == null || !props.ContainsKey(key))
                return false;

            try
            {
                Il2CppSystem.Object raw = props[key];
                if (raw == null)
                    return false;

                try
                {
                    value = raw.Unbox<short>();
                    return true;
                }
                catch
                {
                    if (short.TryParse(raw.ToString(), out value))
                        return true;
                }
            }
            catch { }

            return false;
        }

        private static bool IsOnlineScoreboardFixActive()
        {
            return VisionConfig.EnableMod.Value &&
                   VisionConfig.EnableMultiplayerBattleStatSyncFix.Value &&
                   Photon.Pun.PhotonNetwork.InRoom &&
                   !Photon.Pun.PhotonNetwork.OfflineMode;
        }

        public static void SyncPtfoRoomPropertyIfMaster()
        {
            try
            {
                if (!Photon.Pun.PhotonNetwork.InRoom ||
                    Photon.Pun.PhotonNetwork.OfflineMode ||
                    Photon.Pun.PhotonNetwork.CurrentRoom == null)
                {
                    return;
                }

                EnsureScoreboardPropertyKeys();
                Photon.Realtime.Room room = Photon.Pun.PhotonNetwork.CurrentRoom;
                if (Photon.Pun.PhotonNetwork.IsMasterClient)
                {
                    bool desired = VisionConfig.EnableMod.Value &&
                                   VisionConfig.EnableMultiplayerBattleStatSyncFix.Value &&
                                   VisionConfig.EnableObjectiveTeamplayScoring.Value;
                    var roomProps = new ExitGames.Client.Photon.Hashtable();
                    roomProps[_propKeyRoomPtfoObj] = BoxIl2CppInt16((short)(desired ? 1 : 0));
                    room.SetCustomProperties(roomProps);
                    HitchProfiler.NotifyNetPropFlush();

                    _lastSyncedPtfoRoomPtr = room.Pointer;
                    _wasMasterForPtfoSync = true;
                    _lastPublishedPtfoValue = desired;
                }
                else
                {
                    _wasMasterForPtfoSync = false;
                    bool found = TryReadPlayerShortProp(room.CustomProperties, _propKeyRoomPtfoObj, out short hostVal);
                    _remoteHostPtfoKnown = found;
                    _remoteHostPtfoEnabled = found && hostVal != 0;
                }
            }
            catch { }
        }

        public static bool IsOnlinePtfoScoringActive()
        {
            if (!IsOnlineScoreboardFixActive())
                return false;

            if (Photon.Pun.PhotonNetwork.IsMasterClient)
                return VisionConfig.EnableObjectiveTeamplayScoring.Value;

            return _remoteHostPtfoEnabled;
        }

        public static bool IsPtfoControlledByRemoteHost(out bool hostEnabled, out bool hostKnown)
        {
            hostEnabled = _remoteHostPtfoEnabled;
            hostKnown = _remoteHostPtfoKnown;
            return Photon.Pun.PhotonNetwork.InRoom &&
                   !Photon.Pun.PhotonNetwork.OfflineMode &&
                   !Photon.Pun.PhotonNetwork.IsMasterClient;
        }

        public static bool TryGetLiveScoreboardSnapshot(
            out short score,
            out short kills,
            out short deaths,
            out bool ptfoActive)
        {
            score = _authLocalScore;
            kills = _authLocalKills;
            deaths = _authLocalDeaths;
            ptfoActive = IsOnlinePtfoScoringActive();
            return _localScoreboardSeeded && IsOnlineScoreboardFixActive();
        }

        private static void ResetLocalPlayerScoreboardLedger()
        {
            _authLocalKills = 0;
            _authLocalDeaths = 0;
            _authLocalScore = 0;
            _localScoreboardSeeded = false;
            _localScoreboardDirty = false;
            _isInternalScoreboardFlush = false;
            _inBattleLoaderOnJoinedRoom = false;
            _pendingOpenPlayerListRefresh = false;
            _seededLocalScoreboardRoomPtr = IntPtr.Zero;
            _nextLocalScoreboardFlushUnscaledTime = 0f;
            _nextOpenPlayerListRefreshUnscaledTime = 0f;
            _activeDamageAttackerPtr = IntPtr.Zero;

            _lastSyncedPtfoRoomPtr = IntPtr.Zero;
            _wasMasterForPtfoSync = false;
            _lastPublishedPtfoValue = false;
            _remoteHostPtfoKnown = false;
            _remoteHostPtfoEnabled = false;
            _nextPtfoRoomAndObjPollTime = 0f;
            _ptfoActiveObjectiveAccumSec = 0f;
            _lastPtfoPollTime = 0f;
            _lastPtfoPhasePtr = IntPtr.Zero;
            _pendingLocalFriendlySyringePatientPtr = IntPtr.Zero;
            _pendingLocalFriendlySyringeExpireTime = 0f;

            _localPlayerDamagedVictims.Clear();
            _localPlayerDamagedVehicles.Clear();
            _creditedKillVictimPtrs.Clear();
            _creditedLocalDeathSoldierPtrs.Clear();
            _abandonedBleedingLocalSoldiers.Clear();
            _ptfoObjectiveStates.Clear();
            _creditedPtfoDisabledVehiclePtrs.Clear();
            _recentPtfoRevivedPatients.Clear();
        }

        private static void EnsureLocalPlayerScoreboardSeeded()
        {
            if (!IsOnlineScoreboardFixActive())
                return;

            Photon.Realtime.Room room = Photon.Pun.PhotonNetwork.CurrentRoom;
            Photon.Realtime.Player local = Photon.Pun.PhotonNetwork.LocalPlayer;
            IntPtr roomPtr = room != null ? room.Pointer : IntPtr.Zero;
            if (roomPtr == IntPtr.Zero || local == null)
                return;

            if (_localScoreboardSeeded && _seededLocalScoreboardRoomPtr == roomPtr)
                return;

            EnsureScoreboardPropertyKeys();
            ExitGames.Client.Photon.Hashtable props = local.CustomProperties;
            _authLocalKills = TryReadPlayerShortProp(props, _propKeyKillsObj, out short k) ? k : (short)0;
            _authLocalDeaths = TryReadPlayerShortProp(props, _propKeyDeathsObj, out short d) ? d : (short)0;
            _authLocalScore = TryReadPlayerShortProp(props, _propKeyScoreObj, out short sc) ? sc : (short)0;

            _seededLocalScoreboardRoomPtr = roomPtr;
            _localScoreboardSeeded = true;
            _localScoreboardDirty = false;
        }

        private static void SyncAndFlushLocalScoreboardBeforeListRender()
        {
            if (!IsOnlineScoreboardFixActive())
                return;

            EnsureLocalPlayerScoreboardSeeded();
            Photon.Realtime.Player local = Photon.Pun.PhotonNetwork.LocalPlayer;
            if (local == null)
                return;

            EnsureScoreboardPropertyKeys();
            ExitGames.Client.Photon.Hashtable props = local.CustomProperties;
            if (!TryReadPlayerShortProp(props, _propKeyKillsObj, out short curK) || curK != _authLocalKills ||
                !TryReadPlayerShortProp(props, _propKeyDeathsObj, out short curD) || curD != _authLocalDeaths ||
                !TryReadPlayerShortProp(props, _propKeyScoreObj, out short curS) || curS != _authLocalScore)
            {
                _localScoreboardDirty = true;
            }

            FlushDirtyLocalPlayerScoreboardStats(sendImmediate: false);
        }

        private static void FlushDirtyLocalPlayerScoreboardStats(bool sendImmediate)
        {
            if (!IsOnlineScoreboardFixActive())
                return;

            EnsureLocalPlayerScoreboardSeeded();
            if (!_localScoreboardDirty)
                return;

            Photon.Realtime.Player local = Photon.Pun.PhotonNetwork.LocalPlayer;
            if (local == null)
                return;

            EnsureScoreboardPropertyKeys();
            _localScoreboardDirty = false;
            ScoreboardCoalescedFlushesCount++;
            HitchProfiler.NotifyNetPropFlush();

            try
            {
                var props = new ExitGames.Client.Photon.Hashtable();
                props[_propKeyKillsObj] = BoxIl2CppInt16(_authLocalKills);
                props[_propKeyDeathsObj] = BoxIl2CppInt16(_authLocalDeaths);
                props[_propKeyScoreObj] = BoxIl2CppInt16(_authLocalScore);

                _isInternalScoreboardFlush = true;
                try
                {
                    local.SetCustomProperties(props);
                }
                finally
                {
                    _isInternalScoreboardFlush = false;
                }
            }
            catch { }

            _nextLocalScoreboardFlushUnscaledTime = Time.unscaledTime + 0.25f;
            if (sendImmediate)
            {
                try { Photon.Pun.PhotonNetwork.SendAllOutgoingCommands(); } catch { }
            }
        }

        private static bool IsHumanControlledSoldier(Soldier s)
        {
            if (s == null || s.Pointer == IntPtr.Zero)
                return false;

            if (IsPlayerCharacter(s))
                return true;

            SyncSoldier syncher = s.soldierSyncher;
            return syncher != null && syncher.controlled_by_player;
        }

        private static void PruneExpiredVictimAndVehicleTags(float now)
        {
            if (_localPlayerDamagedVictims.Count >= 48)
            {
                _reusableScoreboardPruneKeys.Clear();
                foreach (var kv in _localPlayerDamagedVictims)
                {
                    if ((now - kv.Value.Timestamp) > 90.0f || now < kv.Value.Timestamp)
                        _reusableScoreboardPruneKeys.Add(kv.Key);
                }
                for (int i = 0; i < _reusableScoreboardPruneKeys.Count; i++)
                    _localPlayerDamagedVictims.Remove(_reusableScoreboardPruneKeys[i]);
            }

            if (_localPlayerDamagedVehicles.Count >= 24)
            {
                _reusableScoreboardPruneKeys.Clear();
                foreach (var kv in _localPlayerDamagedVehicles)
                {
                    if ((now - kv.Value) > 30.0f || now < kv.Value)
                        _reusableScoreboardPruneKeys.Add(kv.Key);
                }
                for (int i = 0; i < _reusableScoreboardPruneKeys.Count; i++)
                    _localPlayerDamagedVehicles.Remove(_reusableScoreboardPruneKeys[i]);
            }
        }

        private static void RecordLocalPlayerDamagedVehicle(IntPtr vehiclePtr)
        {
            if (vehiclePtr == IntPtr.Zero || !IsOnlineScoreboardFixActive())
                return;

            float now = Time.time;
            PruneExpiredVictimAndVehicleTags(now);
            _localPlayerDamagedVehicles[vehiclePtr] = now;
        }

        private static void AwardLocalPtfoScore(short points, string reason)
        {
            if (points <= 0 || !IsOnlinePtfoScoringActive())
                return;

            EnsureLocalPlayerScoreboardSeeded();
            _authLocalScore = (short)Mathf.Clamp(_authLocalScore + points, short.MinValue, short.MaxValue);
            _localScoreboardDirty = true;
            PtfoTotalPointsEarned += points;
            AiCore.LogDebug("PTFO", $"+{points} Score ({reason}) -> TotalScore:{_authLocalScore}");
        }

        private static bool TryGetSoldierWorldPosition(Soldier s, out Vector3 pos)
        {
            pos = Vector3.zero;
            if (s == null || s.Pointer == IntPtr.Zero)
                return false;

            if (s.IsOnVehicle())
            {
                Vehicle v = s.GetCurrentVehicle();
                if (v != null && v.Pointer != IntPtr.Zero && v.transform != null)
                {
                    pos = v.transform.position;
                    return true;
                }
            }

            if (s.transform != null)
            {
                pos = s.transform.position;
                return true;
            }
            return false;
        }

        private static bool IsEitherSoldierInActiveObjective(Soldier localAttacker, Soldier victim)
        {
            if (localAttacker == null || victim == null || localAttacker.Pointer == IntPtr.Zero || victim.Pointer == IntPtr.Zero)
                return false;

            BattleManager bm = BattleManager.instance;
            if (bm == null || bm.Pointer == IntPtr.Zero || BattleManager.isBattleEnded || bm.isPreview)
                return false;

            string fac = AiCore.GetCachedFaction(localAttacker);
            if (string.IsNullOrEmpty(fac))
                return false;

            if (!TryGetSoldierWorldPosition(localAttacker, out Vector3 posA) ||
                !TryGetSoldierWorldPosition(victim, out Vector3 posB))
            {
                return false;
            }

            BattlePhase phase = AiCore.TryGetCurrentBattlePhase(bm);
            AiCore.CollectActiveMissionObjectives(phase, _ptfoScratchObjectives);
            if (_ptfoScratchObjectives.Count == 0)
                return false;

            BattleData bd = null;
            try { bd = BattleManager.GetCurrentBattleData(); } catch { }

            for (int i = 0; i < _ptfoScratchObjectives.Count; i++)
            {
                MissionObjective obj = _ptfoScratchObjectives[i];
                if (obj == null || obj.Pointer == IntPtr.Zero)
                    continue;

                if (!AiCore.ClassifyMissionObjectiveForFaction(obj, fac, bd, out bool isHeld, out bool canAttract, out _))
                    continue;

                if (!isHeld && !canAttract)
                    continue;

                if (obj.IsInsideObjectiveArea(posA, 1.15f) || obj.IsInsideObjectiveArea(posB, 1.15f))
                    return true;
            }
            return false;
        }

        private static bool HasLiveEnemyInObjective(MissionObjective obj, string playerFac, float areaScale)
        {
            if (obj == null || obj.Pointer == IntPtr.Zero || string.IsNullOrEmpty(playerFac))
                return false;

            AiCore.EnsureWorldSnapshot();
            int count = AiCore.WorldSoldierCount;
            var soldiers = AiCore.WorldSoldiers;
            for (int i = 0; i < count; i++)
            {
                ref AiCore.SoldierSnapshot sol = ref soldiers[i];
                if (sol.Ptr == IntPtr.Zero ||
                    sol.Ref == null ||
                    sol.IsInjured ||
                    sol.HasSurrendered ||
                    string.IsNullOrEmpty(sol.Faction) ||
                    AiCore.AreSameSideFactions(playerFac, sol.Faction))
                {
                    continue;
                }

                if (sol.IsOnVehicle)
                {
                    Vehicle sv = sol.Ref.GetCurrentVehicle();
                    if (sv != null && sv.IsAirVehicle())
                        continue;
                }

                if (obj.IsInsideObjectiveArea(sol.Pos, areaScale))
                    return true;
            }
            return false;
        }

        private static void PollLocalPlayerPtfoObjectives(float now)
        {
            BattleManager bm = BattleManager.instance;
            if (bm == null || bm.Pointer == IntPtr.Zero || BattleManager.isBattleEnded || bm.isPreview)
                return;

            Soldier curLocal = Soldier.CurrentControlledSoldierOrNull();
            bool localValid = curLocal != null && curLocal.Pointer != IntPtr.Zero && curLocal.IsAlive && !curLocal.IsDead;
            bool localEligible = localValid && !curLocal.isBleeding;
            if (localEligible && curLocal.IsOnVehicle())
            {
                Vehicle v = curLocal.GetCurrentVehicle();
                if (v != null && v.IsAirVehicle())
                    localEligible = false;
            }

            Vector3 playerPos = Vector3.zero;
            if (localEligible && !TryGetSoldierWorldPosition(curLocal, out playerPos))
                localEligible = false;

            if (!localValid)
                _ptfoActiveObjectiveAccumSec = 0f;

            string playerFac = localValid ? AiCore.GetCachedFaction(curLocal) : _lastKnownLocalPlayerFaction;
            if (string.IsNullOrEmpty(playerFac))
                return;

            BattlePhase phase = AiCore.TryGetCurrentBattlePhase(bm);
            IntPtr curPhasePtr = phase != null ? phase.Pointer : IntPtr.Zero;

            // Check if a phase transition just secured an objective the local player was actively capturing
            if (_lastPtfoPhasePtr != IntPtr.Zero && curPhasePtr != _lastPtfoPhasePtr && _ptfoObjectiveStates.Count > 0)
            {
                _reusableScoreboardPruneKeys.Clear();
                foreach (var kv in _ptfoObjectiveStates)
                {
                    PtfoObjectiveState st = kv.Value;
                    if (st.PhasePtr == _lastPtfoPhasePtr)
                    {
                        _reusableScoreboardPruneKeys.Add(kv.Key);
                        if (localEligible &&
                            st.Initialized &&
                            st.LocalWasInsideLastPoll &&
                            !st.WasHeldByMySide &&
                            !st.WasSecured &&
                            st.CanAttractMySide &&
                            st.ObjRef != null &&
                            (now - st.LastCaptureAwardTime) >= 20.0f)
                        {
                            bool secNow = false;
                            try { secNow = st.ObjRef.IsSecured(); } catch { }
                            if (secNow)
                            {
                                st.LastCaptureAwardTime = now;
                                PtfoCapturesCount++;
                                AwardLocalPtfoScore(5, "Objective Captured (Phase)");
                            }
                        }
                    }
                }
                for (int i = 0; i < _reusableScoreboardPruneKeys.Count; i++)
                    _ptfoObjectiveStates.Remove(_reusableScoreboardPruneKeys[i]);
            }
            _lastPtfoPhasePtr = curPhasePtr;

            AiCore.CollectActiveMissionObjectives(phase, _ptfoScratchObjectives);
            if (_ptfoScratchObjectives.Count == 0)
                return;

            BattleData bd = null;
            try { bd = BattleManager.GetCurrentBattleData(); } catch { }

            float dt = _lastPtfoPollTime > 0f && now > _lastPtfoPollTime
                ? Mathf.Clamp(now - _lastPtfoPollTime, 0.5f, 2.0f)
                : 1.0f;
            _lastPtfoPollTime = now;

            bool activeTickThisPoll = false;
            for (int i = 0; i < _ptfoScratchObjectives.Count; i++)
            {
                MissionObjective obj = _ptfoScratchObjectives[i];
                if (obj == null || obj.Pointer == IntPtr.Zero)
                    continue;

                IntPtr objPtr = obj.Pointer;
                bool classified = AiCore.ClassifyMissionObjectiveForFaction(
                    obj,
                    playerFac,
                    bd,
                    out bool isHeld,
                    out bool canAttract,
                    out float contestOrProgress01);

                bool nowSecured = false;
                try { nowSecured = obj.IsSecured(); } catch { }

                float rawComplete = 0f;
                try
                {
                    if (obj.missionTask != null)
                        rawComplete = obj.missionTask.GetTaskCompletationPercentage();
                }
                catch { }

                _ptfoObjectiveStates.TryGetValue(objPtr, out PtfoObjectiveState st);
                st.ObjRef = obj;
                st.PhasePtr = curPhasePtr;

                bool insideCap = localEligible && obj.IsInsideObjectiveArea(playerPos, 1.08f);
                bool insideTick = localEligible && obj.IsInsideObjectiveArea(playerPos, 1.0f);

                if (st.Initialized &&
                    localEligible &&
                    !st.WasHeldByMySide &&
                    !st.WasSecured &&
                    (isHeld || (nowSecured && st.CanAttractMySide)) &&
                    (insideCap || st.LocalWasInsideLastPoll) &&
                    (now - st.LastCaptureAwardTime) >= 20.0f)
                {
                    st.LastCaptureAwardTime = now;
                    PtfoCapturesCount++;
                    AwardLocalPtfoScore(5, "Objective Captured");
                }

                if (classified && insideTick)
                {
                    bool barMoving = st.Initialized &&
                                     (Mathf.Abs(contestOrProgress01 - st.LastProgress01) > 0.0005f ||
                                      Mathf.Abs(rawComplete - st.LastRawComplete) > 0.0005f);
                    if (!isHeld && canAttract && !nowSecured)
                    {
                        if (barMoving || HasLiveEnemyInObjective(obj, playerFac, 1.20f))
                            activeTickThisPoll = true;
                    }
                    else if (isHeld)
                    {
                        if ((contestOrProgress01 > 0.01f && barMoving) ||
                            HasLiveEnemyInObjective(obj, playerFac, 1.25f))
                        {
                            activeTickThisPoll = true;
                        }
                    }
                }

                st.Initialized = true;
                st.WasHeldByMySide = isHeld;
                st.WasSecured = nowSecured;
                st.CanAttractMySide = canAttract;
                st.LastProgress01 = contestOrProgress01;
                st.LastRawComplete = rawComplete;
                st.LocalWasInsideLastPoll = insideCap;
                _ptfoObjectiveStates[objPtr] = st;
            }

            if (activeTickThisPoll)
            {
                _ptfoActiveObjectiveAccumSec += dt;
                if (_ptfoActiveObjectiveAccumSec >= 10.0f)
                {
                    _ptfoActiveObjectiveAccumSec -= 10.0f;
                    PtfoActiveTicksCount++;
                    AwardLocalPtfoScore(1, "Active Objective Contest/Cap");
                }
            }
        }

        public static void NotifyVehicleRepairedForPtfo(IntPtr vehPtr)
        {
            if (vehPtr != IntPtr.Zero)
                _creditedPtfoDisabledVehiclePtrs.Remove(vehPtr);
        }

        public static void TryCreditEnemyVehicleDisableToLocalPlayer(Vehicle veh, bool directPlayerHit)
        {
            try
            {
                if (!IsOnlinePtfoScoringActive() || veh == null || veh.Pointer == IntPtr.Zero)
                    return;

                IntPtr vehPtr = veh.Pointer;
                if (!directPlayerHit)
                {
                    if (!_localPlayerDamagedVehicles.TryGetValue(vehPtr, out float vHitTime) ||
                        (Time.time - vHitTime) > 20.0f)
                    {
                        return;
                    }
                }

                AiCore.VehicleFastHandles vh = AiCore.GetOrCreateVehicleHandles(veh);
                if (vh != null && vh.IsParachute)
                    return;

                string vehFac = veh.GetVehicleFaction();
                if (string.IsNullOrEmpty(vehFac) || vehFac == "Civilian")
                    _lastKnownVehicleFaction.TryGetValue(vehPtr, out vehFac);

                Soldier curLocal = Soldier.CurrentControlledSoldierOrNull();
                string localFac = curLocal != null && curLocal.Pointer != IntPtr.Zero
                    ? AiCore.GetCachedFaction(curLocal)
                    : null;
                if (string.IsNullOrEmpty(localFac))
                    localFac = _lastKnownLocalPlayerFaction;

                if (string.IsNullOrEmpty(vehFac) ||
                    vehFac == "Civilian" ||
                    string.IsNullOrEmpty(localFac) ||
                    AiCore.AreSameSideFactions(localFac, vehFac))
                {
                    return;
                }

                if (!_creditedPtfoDisabledVehiclePtrs.Add(vehPtr))
                    return;

                PtfoVehicleDisablesCount++;
                AwardLocalPtfoScore(3, $"Enemy Vehicle Disabled ({veh.name})");
            }
            catch { }
        }

        private static void TagVictimDamagedByLocalPlayer(Soldier attacker, Soldier victim, bool mortalOrFireOrRoadkill)
        {
            if (!IsOnlineScoreboardFixActive() ||
                attacker == null ||
                victim == null ||
                attacker.Pointer == IntPtr.Zero ||
                victim.Pointer == IntPtr.Zero ||
                attacker.Pointer == victim.Pointer ||
                !IsPlayerCharacter(attacker))
            {
                return;
            }

            IntPtr victimPtr = victim.Pointer;
            if (_creditedKillVictimPtrs.Contains(victimPtr) || victim.HasSurrended())
                return;

            float now = Time.time;
            PruneExpiredVictimAndVehicleTags(now);

            string atkFac = AiCore.GetCachedFaction(attacker);
            if (!string.IsNullOrEmpty(atkFac))
                _lastKnownLocalPlayerFaction = atkFac;

            string vicFac = AiCore.GetCachedFaction(victim);
            bool isEnemy = string.IsNullOrEmpty(atkFac) ||
                           string.IsNullOrEmpty(vicFac) ||
                           !AiCore.AreSameSideFactions(atkFac, vicFac);
            bool isHumanVictim = IsHumanControlledSoldier(victim);

            bool hasPrev = _localPlayerDamagedVictims.TryGetValue(victimPtr, out LocalPlayerVictimTag existing) &&
                           (now - existing.Timestamp) <= 90.0f;

            bool wasMortal = mortalOrFireOrRoadkill || victim.isBleeding || victim.IsOnFire || !victim.IsAlive || victim.IsDead;
            if (hasPrev && existing.WasMortalOrFireOrRoadkill)
                wasMortal = true;

            bool wasObjZone = hasPrev && existing.WasObjectiveZone;
            if (!wasObjZone && isEnemy && IsOnlinePtfoScoringActive() && IsEitherSoldierInActiveObjective(attacker, victim))
                wasObjZone = true;

            _localPlayerDamagedVictims[victimPtr] = new LocalPlayerVictimTag
            {
                Timestamp = now,
                IsEnemy = isEnemy,
                IsHumanPlayer = isHumanVictim,
                WasMortalOrFireOrRoadkill = wasMortal,
                WasObjectiveZone = wasObjZone
            };
        }

        internal static bool WasVictimKilledByLocalPlayer(IntPtr victimPtr)
        {
            return victimPtr != IntPtr.Zero && _creditedKillVictimPtrs.Contains(victimPtr);
        }

        private static bool IsLocalKillTrackingActive()
        {
            return IsOnlineScoreboardFixActive() ||
                   (VisionConfig.EnableMod.Value && VisionConfig.EnableDogTagArchive != null && VisionConfig.EnableDogTagArchive.Value);
        }

        private static void TryCreditVictimToLocalPlayer(IntPtr victimPtr, LocalPlayerVictimTag tag)
        {
            if (victimPtr == IntPtr.Zero || !IsLocalKillTrackingActive())
                return;

            if (!_creditedKillVictimPtrs.Add(victimPtr))
                return;

            _localPlayerDamagedVictims.Remove(victimPtr);
            if (!IsOnlineScoreboardFixActive())
                return;

            EnsureLocalPlayerScoreboardSeeded();

            short deltaScore = (short)(tag.IsHumanPlayer ? 5 : 1);
            if (tag.IsEnemy)
            {
                bool ptfoObjKill = tag.WasObjectiveZone && IsOnlinePtfoScoringActive();
                if (ptfoObjKill)
                {
                    deltaScore += 1;
                    PtfoObjKillBonusesCount++;
                    PtfoTotalPointsEarned++;
                }
                _authLocalKills = (short)Mathf.Clamp(_authLocalKills + 1, 0, short.MaxValue);
                _authLocalScore = (short)Mathf.Clamp(_authLocalScore + deltaScore, short.MinValue, short.MaxValue);
                if (ptfoObjKill)
                    AiCore.LogDebug("PTFO", $"+1 Obj Attack/Defense Kill Bonus -> TotalScore:{_authLocalScore}");
            }
            else
            {
                // Preserve vanilla friendly-fire score penalty without incrementing Kills
                _authLocalScore = (short)Mathf.Clamp(_authLocalScore - deltaScore, short.MinValue, short.MaxValue);
            }
            _localScoreboardDirty = true;
        }

        private static void TryRecordLocalDeath(Soldier deadSoldier, bool isAbandonedAliveBleeder)
        {
            if (deadSoldier == null || deadSoldier.Pointer == IntPtr.Zero || deadSoldier.HasSurrended())
                return;

            IntPtr ptr = deadSoldier.Pointer;
            if (!_creditedLocalDeathSoldierPtrs.Add(ptr))
                return;

            EnsureLocalPlayerScoreboardSeeded();
            _authLocalDeaths = (short)Mathf.Clamp(_authLocalDeaths + 1, 0, short.MaxValue);
            _localScoreboardDirty = true;

            if (isAbandonedAliveBleeder)
                _abandonedBleedingLocalSoldiers.Add(ptr);
        }

        public static void NotifyVehicleRoadkillHit(Vehicle veh, Soldier victim)
        {
            try
            {
                if (!IsOnlineScoreboardFixActive() ||
                    veh == null ||
                    victim == null ||
                    veh.Pointer == IntPtr.Zero ||
                    victim.Pointer == IntPtr.Zero ||
                    !veh.PlayerIsDriving())
                {
                    return;
                }

                Soldier localDriver = Soldier.CurrentControlledSoldierOrNull();
                if (localDriver == null ||
                    localDriver.Pointer == IntPtr.Zero ||
                    !localDriver.IsOnVehicle() ||
                    localDriver.GetCurrentVehicle() == null ||
                    localDriver.GetCurrentVehicle().Pointer != veh.Pointer)
                {
                    return;
                }

                TagVictimDamagedByLocalPlayer(localDriver, victim, mortalOrFireOrRoadkill: true);
            }
            catch { }
        }

        [HarmonyPatch(typeof(Photon.Realtime.Player), nameof(Photon.Realtime.Player.SetCustomProperties))]
        public static class Player_SetCustomProperties_Patch
        {
            public static bool Prefix(Photon.Realtime.Player __instance, ExitGames.Client.Photon.Hashtable __0)
            {
                try
                {
                    if (!IsOnlineScoreboardFixActive() ||
                        _isInternalScoreboardFlush ||
                        _inBattleLoaderOnJoinedRoom ||
                        __instance == null ||
                        !__instance.IsLocal ||
                        __0 == null)
                    {
                        return true;
                    }

                    EnsureScoreboardPropertyKeys();
                    bool stripped = false;
                    if (__0.ContainsKey(_propKeyKillsObj))
                    {
                        __0.Remove(_propKeyKillsObj);
                        stripped = true;
                    }
                    if (__0.ContainsKey(_propKeyDeathsObj))
                    {
                        __0.Remove(_propKeyDeathsObj);
                        stripped = true;
                    }
                    if (__0.ContainsKey(_propKeyScoreObj))
                    {
                        __0.Remove(_propKeyScoreObj);
                        stripped = true;
                    }

                    // Suppress empty stale scoreboard packets so vanilla DamageFeedback/Soldier.Kill never clobbers our ledger
                    if (stripped && __0.Count == 0)
                    {
                        ScoreboardVanillaPacketsSuppressedCount++;
                        return false;
                    }
                    return __0.Count > 0;
                }
                catch { }
                return true;
            }
        }

        [HarmonyPatch(typeof(BattleLoader), nameof(BattleLoader.OnJoinedRoom))]
        public static class BattleLoader_OnJoinedRoom_Patch
        {
            public static void Prefix()
            {
                ResetLocalPlayerScoreboardLedger();
                _inBattleLoaderOnJoinedRoom = true;
            }

            public static void Postfix()
            {
                _inBattleLoaderOnJoinedRoom = false;
                try
                {
                    if (VisionConfig.EnableMod.Value)
                    {
                        AiCore.EnsureLocalModVersionPublished();
                    }

                    if (IsOnlineScoreboardFixActive())
                    {
                        EnsureLocalPlayerScoreboardSeeded();
                        SyncPtfoRoomPropertyIfMaster();
                    }
                }
                catch { }
            }
        }

        private struct PendingJoinMotdEntry
        {
            public Photon.Realtime.Player PlayerRef;
            public int ActorNumber;
            public string DedupKey;
            public float EnqueuedAtUnscaledTime;
            public float ReadyAtUnscaledTime;
            public float SendAtUnscaledTime;
        }

        private static readonly Il2CppSystem.Object _propKeyFactionObj = (Il2CppSystem.String)"faction";
        private static readonly List<PendingJoinMotdEntry> _pendingJoinMotdQueue = new List<PendingJoinMotdEntry>(8);
        private static readonly HashSet<string> _welcomedMotdPlayerKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static bool TryGetPhotonPlayerFaction(Photon.Realtime.Player p, out string faction)
        {
            faction = null;
            if (p == null)
                return false;

            try
            {
                ExitGames.Client.Photon.Hashtable props = p.CustomProperties;
                if (props != null && props.ContainsKey(_propKeyFactionObj))
                {
                    Il2CppSystem.Object raw = props[_propKeyFactionObj];
                    string s = raw != null ? raw.ToString() : null;
                    if (!string.IsNullOrWhiteSpace(s))
                    {
                        faction = s.Trim();
                        return true;
                    }
                }
            }
            catch { }

            try
            {
                AiCore.EnsureWorldSnapshot();
                int count = AiCore.WorldSoldierCount;
                var soldiers = AiCore.WorldSoldiers;
                int targetActor = p.ActorNumber;
                for (int i = 0; i < count; i++)
                {
                    ref AiCore.SoldierSnapshot ss = ref soldiers[i];
                    if (!ss.IsPlayer || ss.Ref == null || string.IsNullOrEmpty(ss.Faction))
                        continue;

                    SyncSoldier syn = ss.Ref.soldierSyncher;
                    if (syn == null)
                        continue;

                    Photon.Realtime.Player owner = syn.Controller ?? syn.Owner;
                    if (owner != null && owner.ActorNumber == targetActor)
                    {
                        faction = ss.Faction;
                        return true;
                    }
                }
            }
            catch { }

            return false;
        }

        private static string GetFactionMotdGreeting(string faction)
        {
            if (string.IsNullOrWhiteSpace(faction))
                return "Welcome to the front, soldier!";

            string f = faction.Trim().ToLowerInvariant();
            if (f.Contains("germany") || f.Contains("ger") || f.Contains("wehrmacht") || f.Contains("_ss"))
                return "Willkommen an der Front, Kamerad!";
            if (f.Contains("italy") || f.Contains("ita"))
                return "Benvenuto al fronte, commilitone!";
            if (f.Contains("france") || f.StartsWith("fr") || f.Contains("_fr"))
                return "Bienvenue au front, camarade!";
            if (f.Contains("poland") || f.Contains("pol"))
                return "Witamy na froncie, towarzyszu!";
            if (f.Contains("ussr") || f.Contains("soviet") || f.Contains("rus"))
                return "Dobro pozhalovat na front, tovarishch!";
            if (f.Contains("japan") || f.Contains("jap") || f.Contains("ijn") || f.Contains("ija"))
                return "Sensen e yokoso, senyu!";
            if (f.Contains("china") || f.Contains("chn"))
                return "Huan ying lai dao qian xian, zhan you!";
            if (f.Contains("hungary") || f.Contains("hun"))
                return "Udvozoljuk a fronton, bajtars!";
            if (f.Contains("romania") || f.Contains("rom"))
                return "Bine ai venit pe front, camarade!";
            if (f.Contains("uk") || f.Contains("brit") || f.Contains("eng") || f.Contains("aus") || f.Contains("can") || f.Contains("ind") || f.Contains("commonwealth"))
                return "Welcome to the front, mate!";

            return "Welcome to the front, soldier!";
        }

        private static string GetJoinerMotdDedupKey(Photon.Realtime.Player p)
        {
            if (p == null)
                return string.Empty;
            try
            {
                string uid = p.UserId;
                if (!string.IsNullOrWhiteSpace(uid))
                    return "uid:" + uid.Trim();
            }
            catch { }
            try
            {
                string nick = p.NickName;
                if (!string.IsNullOrWhiteSpace(nick))
                    return "nick:" + nick.Trim();
            }
            catch { }
            try
            {
                IntPtr roomPtr = Photon.Pun.PhotonNetwork.CurrentRoom != null ? Photon.Pun.PhotonNetwork.CurrentRoom.Pointer : IntPtr.Zero;
                return $"act:{roomPtr}:{p.ActorNumber}";
            }
            catch
            {
                return string.Empty;
            }
        }

        [HarmonyPatch(typeof(BattleLoader), nameof(BattleLoader.OnPlayerEnteredRoom))]
        public static class BattleLoader_OnPlayerEnteredRoom_Patch
        {
            public static void Postfix(Photon.Realtime.Player __0)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value ||
                        (VisionConfig.EnableLobbyJoinMotd != null && !VisionConfig.EnableLobbyJoinMotd.Value) ||
                        __0 == null ||
                        __0.IsLocal ||
                        !AiCore.IsInMultiplayerRoom() ||
                        !AiCore.IsMasterClient())
                    {
                        return;
                    }

                    string dedupKey = GetJoinerMotdDedupKey(__0);
                    if (!string.IsNullOrEmpty(dedupKey) && _welcomedMotdPlayerKeys.Contains(dedupKey))
                        return;

                    int actorNum = __0.ActorNumber;
                    for (int i = 0; i < _pendingJoinMotdQueue.Count; i++)
                    {
                        if (_pendingJoinMotdQueue[i].ActorNumber == actorNum)
                            return;
                    }

                    if (_pendingJoinMotdQueue.Count < 16)
                    {
                        float nowUnscaled = Time.unscaledTime;
                        _pendingJoinMotdQueue.Add(new PendingJoinMotdEntry
                        {
                            PlayerRef = __0,
                            ActorNumber = actorNum,
                            DedupKey = dedupKey,
                            EnqueuedAtUnscaledTime = nowUnscaled,
                            ReadyAtUnscaledTime = 0f,
                            SendAtUnscaledTime = nowUnscaled + 1.5f
                        });
                    }
                }
                catch { }
            }
        }

        public static void UpdateLobbyJoinMotdQueue()
        {
            if (_pendingJoinMotdQueue.Count == 0)
                return;

            if (!VisionConfig.EnableMod.Value ||
                (VisionConfig.EnableLobbyJoinMotd != null && !VisionConfig.EnableLobbyJoinMotd.Value) ||
                !AiCore.IsInMultiplayerRoom() ||
                !AiCore.IsMasterClient())
            {
                _pendingJoinMotdQueue.Clear();
                return;
            }

            float now = Time.unscaledTime;
            for (int i = _pendingJoinMotdQueue.Count - 1; i >= 0; i--)
            {
                PendingJoinMotdEntry entry = _pendingJoinMotdQueue[i];
                if (now < entry.SendAtUnscaledTime)
                    continue;

                Photon.Realtime.Player target = entry.PlayerRef;
                if (target == null || target.IsLocal)
                {
                    _pendingJoinMotdQueue.RemoveAt(i);
                    continue;
                }

                // Confirm player is still connected in the current room
                bool stillInRoom = false;
                try
                {
                    var room = Photon.Pun.PhotonNetwork.CurrentRoom;
                    if (room != null && room.Players != null && room.Players.ContainsKey(entry.ActorNumber))
                        stillInRoom = true;
                }
                catch { }
                if (!stillInRoom)
                {
                    _pendingJoinMotdQueue.RemoveAt(i);
                    continue;
                }

                if (!string.IsNullOrEmpty(entry.DedupKey) && _welcomedMotdPlayerKeys.Contains(entry.DedupKey))
                {
                    _pendingJoinMotdQueue.RemoveAt(i);
                    continue;
                }

                bool hasFaction = TryGetPhotonPlayerFaction(target, out string joinerFaction);
                if (entry.ReadyAtUnscaledTime <= 0f)
                {
                    if (hasFaction)
                    {
                        entry.ReadyAtUnscaledTime = now + 1.5f;
                        _pendingJoinMotdQueue[i] = entry;
                        continue;
                    }
                    if (now - entry.EnqueuedAtUnscaledTime < 45.0f)
                        continue;
                    entry.ReadyAtUnscaledTime = now;
                }

                if (now < entry.ReadyAtUnscaledTime)
                    continue;

                _pendingJoinMotdQueue.RemoveAt(i);

                AiCore.InvalidatePlayerModdedCache(entry.ActorNumber);
                bool isModdedJoiner = AiCore.IsPhotonPlayerConfirmedModded(target);
                string greetingLine = GetFactionMotdGreeting(joinerFaction);
                string motd = isModdedJoiner
                    ? $"{greetingLine}\n" +
                      $"[Battle Hardened v{Plugin.PluginVersion} Lobby] Host AI active.\n" +
                      "\u2022 Squad Leaders: Open [Radial] or [Map] to Accept / Balk incoming HQ radio orders."
                    : $"{greetingLine}\n" +
                      "[Battle Hardened AI \u2014 Host Mod Active | No Download Needed]\n" +
                      "\u2022 Smarter AI: Bots use human sight cones (no 360\u00B0 eyes), flank, smoke, scavenge gear & crew weapons.\n" +
                      "\u2022 Cover & Night: Rifles/MGs pierce thin wood; stance/camo matter, and night is dark\u2014watch muzzle flashes!\n" +
                      "\u2022 Squad Radio: Keep your Radioman alive so your squad stays linked to HQ orders & armor/flank support.";

                if (AiCore.SendTargetedVanillaChatMessage(target, motd, minPlayerInterval: 0f, bypassRpcBudget: true))
                {
                    try
                    {
                        if (PlayerGUI.instance != null && Photon.Pun.PhotonNetwork.LocalPlayer != null)
                        {
                            string joinerNick = !string.IsNullOrEmpty(target.NickName) ? target.NickName : $"Player#{entry.ActorNumber}";
                            ChatMessage.NewChatMessage(Photon.Pun.PhotonNetwork.LocalPlayer, $"[MOTD -> {joinerNick}] {motd}");
                        }
                    }
                    catch { }

                    if (!string.IsNullOrEmpty(entry.DedupKey))
                    {
                        if (_welcomedMotdPlayerKeys.Count > 128)
                            _welcomedMotdPlayerKeys.Clear();
                        _welcomedMotdPlayerKeys.Add(entry.DedupKey);
                    }
                }
            }
        }

        [HarmonyPatch(typeof(BattleLoader), nameof(BattleLoader.OnPlayerPropertiesUpdate))]
        public static class BattleLoader_OnPlayerPropertiesUpdate_Patch
        {
            public static void Postfix(Photon.Realtime.Player __0, ExitGames.Client.Photon.Hashtable __1)
            {
                try
                {
                    if (__1 == null)
                        return;

                    if (__0 != null && __1.ContainsKey((Il2CppSystem.String)"bh_ver"))
                    {
                        AiCore.InvalidatePlayerModdedCache(__0.ActorNumber);
                    }

                    if (!IsOnlineScoreboardFixActive())
                        return;

                    EnsureScoreboardPropertyKeys();
                    if (__1.ContainsKey(_propKeyScoreObj) ||
                        __1.ContainsKey(_propKeyKillsObj) ||
                        __1.ContainsKey(_propKeyDeathsObj))
                    {
                        if (MiniMapGUI.ShowingPlayerList())
                            _pendingOpenPlayerListRefresh = true;
                    }
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(DamageFeedback), nameof(DamageFeedback.OnSoldierHit))]
        public static class DamageFeedback_OnSoldierHit_Patch
        {
            public static void Postfix(Soldier __0, Soldier __1)
            {
                try
                {
                    if (!IsLocalKillTrackingActive() || __1 == null || __1.Pointer == IntPtr.Zero)
                        return;

                    IntPtr targetPtr = __1.Pointer;
                    bool targetDead = !__1.IsAlive || __1.IsDead;

                    if (__0 != null && __0.Pointer != IntPtr.Zero && __0.Pointer != targetPtr)
                    {
                        if (IsPlayerCharacter(__0))
                        {
                            bool mortalNow = targetDead || __1.isBleeding || __1.IsOnFire;
                            TagVictimDamagedByLocalPlayer(__0, __1, mortalNow);
                            if (targetDead &&
                                !__1.HasSurrended() &&
                                _localPlayerDamagedVictims.TryGetValue(targetPtr, out LocalPlayerVictimTag tag))
                            {
                                TryCreditVictimToLocalPlayer(targetPtr, tag);
                            }
                        }
                        else if (_localPlayerDamagedVictims.TryGetValue(targetPtr, out LocalPlayerVictimTag oldTag) &&
                                 (targetDead || !oldTag.WasMortalOrFireOrRoadkill))
                        {
                            // Another soldier finished off or took primary damage precedence on this unit
                            _localPlayerDamagedVictims.Remove(targetPtr);
                        }
                    }
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(DamageFeedback), nameof(DamageFeedback.OnVehicleHit))]
        public static class DamageFeedback_OnVehicleHit_Patch
        {
            public static void Postfix(Soldier __0, Vehicle __1, bool __5)
            {
                try
                {
                    if (IsLocalKillTrackingActive() &&
                        __0 != null &&
                        __0.Pointer != IntPtr.Zero &&
                        __1 != null &&
                        __1.Pointer != IntPtr.Zero &&
                        IsPlayerCharacter(__0))
                    {
                        string atkFac = AiCore.GetCachedFaction(__0);
                        if (!string.IsNullOrEmpty(atkFac))
                            _lastKnownLocalPlayerFaction = atkFac;

                        RecordLocalPlayerDamagedVehicle(__1.Pointer);
                        if (IsOnlineScoreboardFixActive() && __5 && (!__1.IsActive() || __1.IsDisabled()))
                        {
                            TryCreditEnemyVehicleDisableToLocalPlayer(__1, directPlayerHit: true);
                        }
                    }
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(Soldier), nameof(Soldier.UseSyryngeCR))]
        public static class Soldier_UseSyryngeCR_Patch
        {
            public static void Prefix(Soldier __instance, Creature __1)
            {
                try
                {
                    if (__instance == null || __instance.Pointer == IntPtr.Zero)
                        return;

                    AiCore.SoldierFastHandles docH = AiCore.GetOrCreateSoldierHandles(__instance);
                    if (docH != null)
                    {
                        docH.Goap.ActiveSyringeHealUntil = Time.time + 5.2f;
                        docH.Goap.MedicCloseApproachSince = 0f;
                        docH.Goap.MedicHealStartTime = 0f;
                        docH.Goap.MedicHealTargetPtr = IntPtr.Zero;
                    }

                    Soldier patSol = __1 != null ? __1.TryCast<Soldier>() : null;
                    if (VisionConfig.EnableMod.Value &&
                        patSol != null &&
                        patSol.Pointer != IntPtr.Zero &&
                        !patSol.IsDead)
                    {
                        string docFacEarly = AiCore.GetCachedFaction(__instance);
                        string patFacEarly = AiCore.GetCachedFaction(patSol);
                        if (!string.IsNullOrEmpty(docFacEarly) &&
                            !string.IsNullOrEmpty(patFacEarly) &&
                            AiCore.AreEnemyFactions(docFacEarly, patFacEarly))
                        {
                            // Mark this patient as actively undergoing an enemy syringe capture so Soldier.Surrender
                            // revives them (SetIncapacitated(false) -> IsAlive = true, LayerWeight(1) = 1f) BEFORE
                            // Surrender() sets m_Animator.SetBool("Surrender", true)!
                            AiCore.SoldierFastHandles patH = AiCore.GetOrCreateSoldierHandles(patSol);
                            if (patH != null)
                                patH.EnemySyringeCaptureUntil = Time.time + 10.0f;
                            ClearAllFriendlyTargetLocksOnSoldier(patSol);
                        }
                    }

                    if (!IsOnlinePtfoScoringActive() ||
                        !IsPlayerCharacter(__instance))
                    {
                        return;
                    }

                    _pendingLocalFriendlySyringePatientPtr = IntPtr.Zero;
                    if (patSol == null ||
                        patSol.Pointer == IntPtr.Zero ||
                        patSol.Pointer == __instance.Pointer ||
                        patSol.IsDead ||
                        (!patSol.IsAlive && !patSol.IsInjured) ||
                        patSol.HasSurrended())
                    {
                        return;
                    }

                    string docFac = AiCore.GetCachedFaction(__instance);
                    string patFac = AiCore.GetCachedFaction(patSol);
                    if (string.IsNullOrEmpty(docFac) ||
                        string.IsNullOrEmpty(patFac) ||
                        !AiCore.AreSameSideFactions(docFac, patFac))
                    {
                        return;
                    }

                    bool needsHeal = patSol.IsInjured ||
                                     patSol.isBleeding ||
                                     patSol.GetBleedValue() > 0.01f ||
                                     patSol.life_total.Value < 95;
                    if (!needsHeal)
                        return;

                    float now = Time.time;
                    if (_recentPtfoRevivedPatients.TryGetValue(patSol.Pointer, out float lastRevTime) &&
                        now >= lastRevTime &&
                        (now - lastRevTime) <= 15.0f)
                    {
                        return;
                    }

                    _pendingLocalFriendlySyringePatientPtr = patSol.Pointer;
                    _pendingLocalFriendlySyringeExpireTime = now + 12.0f;
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(GameStatistics), nameof(GameStatistics.OnAlliedRevived))]
        public static class GameStatistics_OnAlliedRevived_Patch
        {
            public static void Postfix()
            {
                try
                {
                    if (!IsOnlinePtfoScoringActive() || _pendingLocalFriendlySyringePatientPtr == IntPtr.Zero)
                        return;

                    float now = Time.time;
                    if (now <= _pendingLocalFriendlySyringeExpireTime)
                    {
                        if (_recentPtfoRevivedPatients.Count >= 32)
                        {
                            _reusableScoreboardPruneKeys.Clear();
                            foreach (var kv in _recentPtfoRevivedPatients)
                            {
                                if ((now - kv.Value) > 30.0f || now < kv.Value)
                                    _reusableScoreboardPruneKeys.Add(kv.Key);
                            }
                            for (int i = 0; i < _reusableScoreboardPruneKeys.Count; i++)
                                _recentPtfoRevivedPatients.Remove(_reusableScoreboardPruneKeys[i]);
                        }

                        _recentPtfoRevivedPatients[_pendingLocalFriendlySyringePatientPtr] = now;
                        PtfoRevivesCount++;
                        AwardLocalPtfoScore(2, "Friendly Syringe Revive");
                    }

                    _pendingLocalFriendlySyringePatientPtr = IntPtr.Zero;
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(Soldier), nameof(Soldier.SetBleeding))]
        public static class Soldier_SetBleeding_Patch
        {
            public static void Postfix(Soldier __instance, bool __0)
            {
                try
                {
                    if (!IsLocalKillTrackingActive() || __instance == null || __instance.Pointer == IntPtr.Zero)
                        return;

                    IntPtr ptr = __instance.Pointer;
                    if (__0)
                    {
                        if (_activeDamageAttackerPtr != IntPtr.Zero && _activeDamageAttackerPtr != new IntPtr(-1))
                        {
                            Soldier curLocal = Soldier.CurrentControlledSoldierOrNull();
                            if (curLocal != null && curLocal.Pointer == _activeDamageAttackerPtr && curLocal.Pointer != ptr)
                            {
                                TagVictimDamagedByLocalPlayer(curLocal, __instance, mortalOrFireOrRoadkill: true);
                            }
                            else
                            {
                                _localPlayerDamagedVictims.Remove(ptr);
                            }
                        }
                        else if (_localPlayerDamagedVictims.TryGetValue(ptr, out LocalPlayerVictimTag tag) &&
                                 (Time.time - tag.Timestamp) <= 1.5f)
                        {
                            tag.WasMortalOrFireOrRoadkill = true;
                            tag.Timestamp = Time.time;
                            _localPlayerDamagedVictims[ptr] = tag;
                        }
                    }
                    else if (__instance.IsAlive && !__instance.IsDead)
                    {
                        // Medic stopped bleeding on a live soldier: cancel pending bleed-out kill tag and refund abandoned-bleed death
                        _localPlayerDamagedVictims.Remove(ptr);

                        if (_abandonedBleedingLocalSoldiers.Remove(ptr))
                        {
                            _creditedLocalDeathSoldierPtrs.Remove(ptr);
                            EnsureLocalPlayerScoreboardSeeded();
                            if (_authLocalDeaths > 0)
                            {
                                _authLocalDeaths--;
                                _localScoreboardDirty = true;
                            }
                        }
                    }
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(Soldier), nameof(Soldier.SetOnFire))]
        public static class Soldier_SetOnFire_Patch
        {
            public static void Postfix(Soldier __instance)
            {
                try
                {
                    if (!IsLocalKillTrackingActive() || __instance == null || __instance.Pointer == IntPtr.Zero)
                        return;

                    Soldier curLocal = Soldier.CurrentControlledSoldierOrNull();
                    if (curLocal == null || curLocal.Pointer == IntPtr.Zero || curLocal.Pointer == __instance.Pointer)
                        return;

                    bool attackerIsLocal = _activeDamageAttackerPtr == curLocal.Pointer ||
                        (_localPlayerDamagedVictims.TryGetValue(__instance.Pointer, out LocalPlayerVictimTag fTag) &&
                         (Time.time - fTag.Timestamp) <= 2.0f);
                    if (attackerIsLocal)
                    {
                        TagVictimDamagedByLocalPlayer(curLocal, __instance, mortalOrFireOrRoadkill: true);
                    }
                }
                catch { }
            }
        }

        internal static bool TryComputeFrontlineSpawnDirection(Soldier s, Squad sq, Vector3 spawnPos, out Vector3 outDir)
        {
            outDir = Vector3.zero;
            if (s == null || s.Pointer == IntPtr.Zero)
                return false;

            string fac = AiCore.GetCachedFaction(s);
            if (sq != null && sq.Pointer != IntPtr.Zero)
            {
                try
                {
                    SquadTask task = sq.currentTask;
                    if (task != null && task.Pointer != IntPtr.Zero)
                    {
                        Vector3 dest = task.GetTaskDestination();
                        Vector3 toDest = dest - spawnPos;
                        toDest.y = 0f;
                        if (toDest.sqrMagnitude > 9.0f)
                        {
                            outDir = toDest.normalized;
                            return true;
                        }
                    }
                }
                catch { }
            }

            if (!string.IsNullOrEmpty(fac))
            {
                if (AiCore.TryGetNearestEnemyOrContestedObjectiveDirection(fac, spawnPos, out Vector3 objDir))
                {
                    objDir.y = 0f;
                    if (objDir.sqrMagnitude > 0.001f)
                    {
                        outDir = objDir.normalized;
                        return true;
                    }
                }

                if (VisionPatches.TryGetDominantEnemyGunfireDirection(spawnPos, fac, 320.0f, 12.0f, out Vector3 gfDir, out _))
                {
                    gfDir.y = 0f;
                    if (gfDir.sqrMagnitude > 0.001f)
                    {
                        outDir = gfDir.normalized;
                        return true;
                    }
                }

                if (AiCore.TryGetEnemySpawnDirection(fac, spawnPos, out Vector3 spDir))
                {
                    spDir.y = 0f;
                    if (spDir.sqrMagnitude > 0.001f)
                    {
                        outDir = spDir.normalized;
                        return true;
                    }
                }
            }

            return false;
        }

        internal static void OrientNewlySpawnedSoldierTowardFrontline(
            Soldier s,
            Squad sq,
            float aiJitterDegrees,
            bool isPreSetPlayerHook)
        {
            if (!VisionConfig.EnableMod.Value ||
                s == null ||
                s.Pointer == IntPtr.Zero ||
                !s.IsAlive ||
                s.IsOnVehicle() ||
                s.transform == null)
            {
                return;
            }

            if (AiCore.IsInMultiplayerRoom() && !AiCore.IsLocallyOwnedSoldier(s))
                return;

            AiCore.SoldierFastHandles h = AiCore.GetOrCreateSoldierHandles(s);
            float now = Time.time;
            if (h != null)
            {
                // When SetPlayer switches to a veteran squadmate mid-battle (> 2.5s after spawn), NEVER snap their aim!
                if (isPreSetPlayerHook && h.CreatedAtTime > 0f && (now - h.CreatedAtTime) > 2.5f)
                    return;

                if (!isPreSetPlayerHook && h.LastSpawnOrientTime > 0f && (now - h.LastSpawnOrientTime) < 2.5f)
                    return;

                h.LastSpawnOrientTime = now;
            }

            Vector3 spawnPos = s.transform.position;
            if (!TryComputeFrontlineSpawnDirection(s, sq ?? s.joinedSquad, spawnPos, out Vector3 dir))
                return;

            float yaw = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
            bool isHuman = isPreSetPlayerHook || s.IsPlayer();
            if (!isHuman && aiJitterDegrees > 0.05f)
            {
                float u = ((AiCore.GetDeterministicHash(s.Pointer, 441) & 1023) / 511.5f) - 1.0f;
                yaw += u * aiJitterDegrees;
            }

            s.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            if (isHuman)
            {
                try
                {
                    LookParameters lp = s.lookRotation;
                    if (lp != null)
                        lp.leftRight = 0f;
                    LookParameters camLp = PlayerController.camLookRotation;
                    if (camLp != null)
                        camLp.leftRight = yaw;
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(Squad), nameof(Squad.SetFullySpawned))]
        public static class Squad_SetFullySpawned_Patch
        {
            public static void Postfix(Squad __instance)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value || __instance == null || __instance.Pointer == IntPtr.Zero)
                        return;

                    var units = __instance.units;
                    if (units == null)
                        return;

                    int count = units.Count;
                    for (int i = 0; i < count; i++)
                    {
                        Soldier u = units[i];
                        if (u != null && u.IsAlive && !u.IsOnVehicle())
                        {
                            OrientNewlySpawnedSoldierTowardFrontline(u, __instance, aiJitterDegrees: 10.0f, isPreSetPlayerHook: false);
                        }
                    }
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(PlayerController), nameof(PlayerController.SetPlayer))]
        public static class PlayerController_SetPlayer_Patch
        {
            public static void Prefix(PlayerController __instance, Soldier __0)
            {
                try
                {
                    // Orient freshly spawned on-foot player soldier BEFORE Soldier.OnSetPlayer -> RespawnCameraTransition
                    // so the 1.3s respawn camera swoop lands cleanly facing the objective/frontline!
                    if (__0 != null && __0.Pointer != IntPtr.Zero)
                    {
                        OrientNewlySpawnedSoldierTowardFrontline(__0, __0.joinedSquad, aiJitterDegrees: 0f, isPreSetPlayerHook: true);
                    }

                    if (!IsOnlineScoreboardFixActive() || __instance == null)
                        return;

                    Soldier prev = __instance.ControlledCharacter;
                    if (prev == null || prev.Pointer == IntPtr.Zero)
                        return;

                    if (__0 != null && __0.Pointer == prev.Pointer)
                        return;

                    // Count abandoning a mortally downed (isBleeding) or dead soldier on squad switch / Respawn
                    if (!prev.IsAlive || prev.IsDead || prev.isBleeding)
                    {
                        bool isAbandonedAliveBleeder = prev.IsAlive && !prev.IsDead && prev.isBleeding;
                        TryRecordLocalDeath(prev, isAbandonedAliveBleeder);
                    }
                }
                catch { }
            }

            public static void Postfix()
            {
                try
                {
                    if (VisionConfig.EnableMod.Value)
                        RefreshBottomLeftSquadRosterVisuals();
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(Soldier), nameof(Soldier.Kill))]
        public static class Soldier_Kill_Patch
        {
            public static void Prefix(Soldier __instance)
            {
                try
                {
                    if (__instance != null && __instance.Pointer != IntPtr.Zero && __instance.IsCarryingBody())
                    {
                        __instance.CarryBody(null);
                    }

                    if (!IsLocalKillTrackingActive() || __instance == null || __instance.Pointer == IntPtr.Zero)
                        return;

                    IntPtr victimPtr = __instance.Pointer;
                    _abandonedBleedingLocalSoldiers.Remove(victimPtr);

                    // 1. Local player death (runs before Soldier.Kill detaches ControlledCharacter and regardless of vehicle hide state!)
                    if (IsPlayerCharacter(__instance))
                    {
                        if (IsOnlineScoreboardFixActive())
                            TryRecordLocalDeath(__instance, isAbandonedAliveBleeder: false);
                        return;
                    }

                    // 2. Local player kill / friendly-fire attribution
                    if (_creditedKillVictimPtrs.Contains(victimPtr) || __instance.HasSurrended())
                        return;

                    Soldier curLocal = Soldier.CurrentControlledSoldierOrNull();
                    IntPtr localPtr = curLocal != null ? curLocal.Pointer : IntPtr.Zero;

                    // 2a. Synchronous attacker context
                    if (_activeDamageAttackerPtr != IntPtr.Zero)
                    {
                        if (_activeDamageAttackerPtr == localPtr && localPtr != IntPtr.Zero)
                        {
                            TagVictimDamagedByLocalPlayer(curLocal, __instance, mortalOrFireOrRoadkill: true);
                            if (_localPlayerDamagedVictims.TryGetValue(victimPtr, out LocalPlayerVictimTag syncTag))
                            {
                                TryCreditVictimToLocalPlayer(victimPtr, syncTag);
                            }
                            return;
                        }

                        // Another specific non-local shooter is landing the synchronous kill
                        if (_activeDamageAttackerPtr != new IntPtr(-1))
                        {
                            _localPlayerDamagedVictims.Remove(victimPtr);
                            return;
                        }
                    }

                    // 2b. Delayed bleed-out, fire, melee, roadkill, or SyncDeath RPC from prior local player hit
                    if (_localPlayerDamagedVictims.TryGetValue(victimPtr, out LocalPlayerVictimTag delayedTag))
                    {
                        float age = Time.time - delayedTag.Timestamp;
                        bool validDelayed = (delayedTag.WasMortalOrFireOrRoadkill && age <= 90.0f) ||
                                            (_activeDamageAttackerPtr == IntPtr.Zero && age <= 6.0f);
                        if (validDelayed)
                        {
                            if (!delayedTag.WasObjectiveZone &&
                                delayedTag.IsEnemy &&
                                IsOnlinePtfoScoringActive() &&
                                curLocal != null &&
                                IsEitherSoldierInActiveObjective(curLocal, __instance))
                            {
                                delayedTag.WasObjectiveZone = true;
                            }
                            TryCreditVictimToLocalPlayer(victimPtr, delayedTag);
                            return;
                        }
                    }

                    // 2c. Enemy crew/pilot killed inside a vehicle/aircraft recently crippled by local player
                    if (curLocal != null && localPtr != IntPtr.Zero && __instance.IsOnVehicle())
                    {
                        Vehicle crewVeh = __instance.GetCurrentVehicle();
                        if (crewVeh != null &&
                            crewVeh.Pointer != IntPtr.Zero &&
                            _localPlayerDamagedVehicles.TryGetValue(crewVeh.Pointer, out float vHitTime) &&
                            (Time.time - vHitTime) <= 20.0f &&
                            !AiCore.AreSameSideFactions(AiCore.GetCachedFaction(curLocal), AiCore.GetCachedFaction(__instance)))
                        {
                            TryCreditVictimToLocalPlayer(victimPtr, new LocalPlayerVictimTag
                            {
                                Timestamp = Time.time,
                                IsEnemy = true,
                                IsHumanPlayer = IsHumanControlledSoldier(__instance),
                                WasMortalOrFireOrRoadkill = true,
                                WasObjectiveZone = IsOnlinePtfoScoringActive() && IsEitherSoldierInActiveObjective(curLocal, __instance)
                            });
                            return;
                        }
                    }

                    // 2d. Remote-client driver roadkill fallback (Host-swept OBB roadkill SyncDeath RPC while client is driving)
                    if (_activeDamageAttackerPtr == IntPtr.Zero &&
                        curLocal != null &&
                        localPtr != IntPtr.Zero &&
                        curLocal.IsOnVehicle() &&
                        __instance.transform != null)
                    {
                        Vehicle drvVeh = curLocal.GetCurrentVehicle();
                        if (drvVeh != null &&
                            drvVeh.Pointer != IntPtr.Zero &&
                            !drvVeh.IsAirVehicle() &&
                            drvVeh.PlayerIsDriving() &&
                            drvVeh.transform != null &&
                            !AiCore.AreSameSideFactions(AiCore.GetCachedFaction(curLocal), AiCore.GetCachedFaction(__instance)))
                        {
                            MovableVehicle mv = drvVeh.TryCast<MovableVehicle>();
                            Rigidbody rb = mv != null ? mv.rb : null;
                            if (rb != null && !rb.isKinematic)
                            {
                                Vector3 vel = rb.velocity;
                                if ((vel.x * vel.x + vel.z * vel.z) > 0.75f * 0.75f)
                                {
                                    Vector4 obb = GetCachedVehicleLocalHullObb(drvVeh);
                                    Vector3 d = __instance.transform.position - drvVeh.transform.position;
                                    float maxHoriz = obb.x + obb.y + 4.5f;
                                    if (Mathf.Abs(d.y) <= 3.2f && (d.x * d.x + d.z * d.z) <= maxHoriz * maxHoriz)
                                    {
                                        TryCreditVictimToLocalPlayer(victimPtr, new LocalPlayerVictimTag
                                        {
                                            Timestamp = Time.time,
                                            IsEnemy = true,
                                            IsHumanPlayer = IsHumanControlledSoldier(__instance),
                                            WasMortalOrFireOrRoadkill = true,
                                            WasObjectiveZone = IsOnlinePtfoScoringActive() && IsEitherSoldierInActiveObjective(curLocal, __instance)
                                        });
                                    }
                                }
                            }
                        }
                    }
                }
                catch { }
            }
        }

        private static void GetPlayerScoreboardSortStats(
            Photon.Realtime.Player p,
            out short score,
            out short kills,
            out short deaths,
            out int actorNumber)
        {
            score = 0;
            kills = 0;
            deaths = 0;
            actorNumber = int.MaxValue;
            if (p == null)
                return;

            actorNumber = p.ActorNumber;
            if (p.IsLocal && _localScoreboardSeeded)
            {
                score = _authLocalScore;
                kills = _authLocalKills;
                deaths = _authLocalDeaths;
                return;
            }

            EnsureScoreboardPropertyKeys();
            ExitGames.Client.Photon.Hashtable props = p.CustomProperties;
            TryReadPlayerShortProp(props, _propKeyScoreObj, out score);
            TryReadPlayerShortProp(props, _propKeyKillsObj, out kills);
            TryReadPlayerShortProp(props, _propKeyDeathsObj, out deaths);
        }

        private static void ClearReusableScoreboardGroups()
        {
            for (int i = 0; i < _reusableParentGroups.Count; i++)
                _reusableParentGroups[i].Clear();
            _reusableRowParents.Clear();
            _reusableSlotPositions.Clear();
        }

        [HarmonyPatch(typeof(MiniMapGUI), nameof(MiniMapGUI.ShowPlayerList))]
        public static class MiniMapGUI_ShowPlayerList_Patch
        {
            public static void Prefix(bool __0, out bool __state)
            {
                __state = false;
                try
                {
                    if (!VisionConfig.EnableMod.Value ||
                        !VisionConfig.EnableMultiplayerBattleStatSyncFix.Value ||
                        !__0)
                    {
                        return;
                    }

                    MiniMapGUI inst = MiniMapGUI.Instance;
                    if (inst == null || inst.playerList == null || inst.playerList.activeSelf)
                        return;

                    __state = true;
                    SyncAndFlushLocalScoreboardBeforeListRender();

                    // Deactivate old pending-Destroy PlayerListLine rows before native Object.Destroy runs
                    // (Unity defers Destroy until end-of-frame, so includeInactive:true + inactive guard prevents stale rows in Postfix).
                    var oldLines = inst.playerList.GetComponentsInChildren<PlayerListLine>(true);
                    if (oldLines != null)
                    {
                        for (int i = 0; i < oldLines.Length; i++)
                        {
                            PlayerListLine old = oldLines[i];
                            if (old != null && old.gameObject != null)
                                old.gameObject.SetActive(false);
                        }
                    }
                }
                catch { }
            }

            public static void Postfix(bool __0, bool __state)
            {
                try
                {
                    if (!__state || !__0)
                        return;

                    MiniMapGUI inst = MiniMapGUI.Instance;
                    if (inst == null || inst.playerList == null)
                        return;

                    var activeLines = inst.playerList.GetComponentsInChildren<PlayerListLine>(false);
                    if (activeLines == null || activeLines.Length < 2)
                        return;

                    ClearReusableScoreboardGroups();
                    for (int i = 0; i < activeLines.Length; i++)
                    {
                        PlayerListLine line = activeLines[i];
                        if (line == null || line.transform == null)
                            continue;

                        Transform parent = line.transform.parent;
                        RectTransform rect = line.transform.TryCast<RectTransform>() ?? line.GetComponent<RectTransform>();
                        if (parent == null || rect == null)
                            continue;

                        int groupIdx = -1;
                        for (int p = 0; p < _reusableRowParents.Count; p++)
                        {
                            if (_reusableRowParents[p].Pointer == parent.Pointer)
                            {
                                groupIdx = p;
                                break;
                            }
                        }
                        if (groupIdx < 0)
                        {
                            groupIdx = _reusableRowParents.Count;
                            _reusableRowParents.Add(parent);
                            while (_reusableParentGroups.Count <= groupIdx)
                                _reusableParentGroups.Add(new List<PlayerScoreboardRowSortEntry>(16));
                        }

                        GetPlayerScoreboardSortStats(
                            line.assignedPlayer,
                            out short sc,
                            out short kl,
                            out short dt,
                            out int actorNum);

                        _reusableParentGroups[groupIdx].Add(new PlayerScoreboardRowSortEntry
                        {
                            Rect = rect,
                            Score = sc,
                            Kills = kl,
                            Deaths = dt,
                            ActorNumber = actorNum
                        });
                    }

                    for (int g = 0; g < _reusableRowParents.Count; g++)
                    {
                        List<PlayerScoreboardRowSortEntry> group = _reusableParentGroups[g];
                        int count = group.Count;
                        if (count < 2)
                            continue;

                        _reusableSlotPositions.Clear();
                        for (int i = 0; i < count; i++)
                            _reusableSlotPositions.Add(group[i].Rect.anchoredPosition);

                        group.Sort((a, b) =>
                        {
                            int cmp = b.Score.CompareTo(a.Score);
                            if (cmp != 0)
                                return cmp;
                            cmp = b.Kills.CompareTo(a.Kills);
                            if (cmp != 0)
                                return cmp;
                            cmp = a.Deaths.CompareTo(b.Deaths);
                            if (cmp != 0)
                                return cmp;
                            return a.ActorNumber.CompareTo(b.ActorNumber);
                        });

                        for (int i = 0; i < count; i++)
                        {
                            RectTransform r = group[i].Rect;
                            if (r != null)
                            {
                                r.SetAsLastSibling();
                                r.anchoredPosition = _reusableSlotPositions[i];
                            }
                        }
                    }

                    ClearReusableScoreboardGroups();
                }
                catch { }
            }
        }
    }
}
