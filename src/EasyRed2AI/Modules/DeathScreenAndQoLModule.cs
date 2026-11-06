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
            public string AttackerName;
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

        private static string FormatSoldierIdentity(Soldier s)
        {
            if (s == null)
                return "";

            string rankPrefix;
            if (s.rank == Rank.corporal)
                rankPrefix = "Cpl. ";
            else if (s.rank == Rank.seargent)
                rankPrefix = "Sgt. ";
            else if (s.rank == Rank.first_seargent)
                rankPrefix = "1st Sgt. ";
            else if ((int)s.rank > (int)Rank.first_seargent)
                rankPrefix = "Lt. ";
            else
                rankPrefix = "Pvt. ";

            string name = string.IsNullOrWhiteSpace(s.name_surname) ? "Unknown Soldier" : s.name_surname.Replace('_', ' ').Trim();
            while (name.Contains("  "))
                name = name.Replace("  ", " ");

            int bracketIdx = name.IndexOf(" [", StringComparison.Ordinal);
            if (bracketIdx >= 0)
                name = name.Substring(0, bracketIdx);

            return rankPrefix + name;
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

        private static string GetShooterWeaponOrVehicleName(Soldier shooter)
        {
            if (shooter == null)
                return "";

            if (shooter.IsOnVehicle())
            {
                Vehicle veh = shooter.GetCurrentVehicle();
                string vehName = (veh != null && !string.IsNullOrWhiteSpace(veh.name))
                    ? CleanUnityObjectName(veh.name)
                    : "Vehicle";

                ItemObject heldInVeh = shooter.GetHeldItem();
                if (heldInVeh != null && !string.IsNullOrWhiteSpace(heldInVeh.name))
                    return $"{CleanUnityObjectName(heldInVeh.name)} ({vehName})";

                return vehName;
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
                    if (!VisionConfig.EnableMod.Value || !VisionConfig.EnableDeathScreenAttribution.Value)
                        return;

                    string expSource = "";
                    if (__5 != null)
                    {
                        ItemGrenade gren = __5.GetComponent<ItemGrenade>();
                        if (gren == null)
                            gren = __5.GetComponentInParent<ItemGrenade>();

                        if (gren != null && !string.IsNullOrWhiteSpace(gren.name))
                        {
                            expSource = CleanUnityObjectName(gren.name);
                        }
                        else if (!__7)
                        {
                            Vehicle expVeh = __5.GetComponentInParent<Vehicle>();
                            if (expVeh != null && !string.IsNullOrWhiteSpace(expVeh.name))
                                expSource = CleanUnityObjectName(expVeh.name);
                        }
                    }

                    if (string.IsNullOrEmpty(expSource))
                    {
                        if (__7)
                        {
                            expSource = "Artillery / Mortar Barrage";
                        }
                        else if (__4 != null && __4.IsOnVehicle())
                        {
                            Vehicle shooterVeh = __4.GetCurrentVehicle();
                            expSource = (shooterVeh != null && !string.IsNullOrWhiteSpace(shooterVeh.name))
                                ? $"{CleanUnityObjectName(shooterVeh.name)} (HE Shell)"
                                : "Vehicle HE Shell";
                        }
                        else if (__4 != null)
                        {
                            string heldName = GetShooterWeaponOrVehicleName(__4);
                            expSource = !string.IsNullOrEmpty(heldName) ? heldName : "Grenade / Explosive";
                        }
                        else
                        {
                            expSource = "Explosion";
                        }
                    }

                    _activeExplosionWeaponName = expSource;
                    _activeExplosionIsArtillery = __7;

                    Soldier player = Soldier.CurrentControlledSoldierOrNull();
                    if (player != null && player.Pointer != IntPtr.Zero)
                    {
                        float blastDist = Vector3.Distance(__0, player.GetCenterOfUnit());
                        if (blastDist <= Mathf.Max(14f, __1 * 3.5f))
                        {
                            int distMeters = (__4 != null && __4.Pointer != player.Pointer)
                                ? Mathf.RoundToInt(Vector3.Distance(__4.GetCenterOfUnit(), player.GetCenterOfUnit()))
                                : Mathf.RoundToInt(blastDist);

                            bool isSelf = __4 != null && __4.Pointer == player.Pointer;
                            bool isFF = __4 != null && !isSelf && !string.IsNullOrEmpty(__4.faction) && __4.faction == player.faction;

                            _lastDamageByVictim[player.Pointer] = new DamageAttribution
                            {
                                AttackerName = __4 != null ? FormatSoldierIdentity(__4) : "",
                                IsSelfInflicted = isSelf,
                                IsFriendlyFire = isFF,
                                WeaponOrSource = expSource,
                                HitDetail = "Explosion",
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
                    string weaponOrSource = fromExplosion
                        ? _activeExplosionWeaponName
                        : GetShooterWeaponOrVehicleName(__4);

                    if (fromExplosion)
                    {
                        hitDetail = hitDetail.StartsWith("Headshot", StringComparison.Ordinal)
                            ? $"Shrapnel {hitDetail}"
                            : $"Shrapnel ({hitDetail})";
                    }

                    bool isSelf = __4 != null && __4.Pointer == victim.Pointer;
                    bool isFF = __4 != null && !isSelf && !string.IsNullOrEmpty(__4.faction) && __4.faction == victim.faction;

                    int distMeters = __3;
                    if ((fromExplosion || distMeters <= 0) && __4 != null && !isSelf)
                    {
                        distMeters = Mathf.RoundToInt(Vector3.Distance(__4.GetCenterOfUnit(), victim.GetCenterOfUnit()));
                    }

                    _lastDamageByVictim[victim.Pointer] = new DamageAttribution
                    {
                        AttackerName = __4 != null ? FormatSoldierIdentity(__4) : (_activeExplosionIsArtillery ? "Artillery Support" : ""),
                        IsSelfInflicted = isSelf,
                        IsFriendlyFire = isFF,
                        WeaponOrSource = weaponOrSource,
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
            public static void Postfix(Soldier __0)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value || !VisionConfig.EnableDeathScreenAttribution.Value || __0 == null)
                        return;

                    DeathPanel dp = DeathPanel.instance;
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

                    if (!_lastDamageByVictim.TryGetValue(__0.Pointer, out DamageAttribution attr) ||
                        Time.time - attr.Timestamp > 120f)
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
                    string actionVerb = bledOut ? "Bled out from wounds by" : "Killed by";

                    string prefixTag = "";
                    string richColor = "#E8E2D0";
                    string subColor = "#CFC8B8";
                    if (attr.IsSelfInflicted)
                    {
                        prefixTag = "[SELF-INFLICTED] ";
                        richColor = "#FFB347";
                        subColor = "#FFD08A";
                    }
                    else if (attr.IsFriendlyFire)
                    {
                        prefixTag = "[FRIENDLY FIRE] ";
                        richColor = "#FF5555";
                        subColor = "#FF8888";
                    }

                    string whoLine;
                    if (attr.IsSelfInflicted)
                    {
                        string ownSource = !string.IsNullOrEmpty(attr.WeaponOrSource) ? attr.WeaponOrSource : "Explosion";
                        whoLine = $"{prefixTag}Killed by own {ownSource}";
                    }
                    else
                    {
                        string attacker = !string.IsNullOrEmpty(attr.AttackerName)
                            ? attr.AttackerName
                            : (!string.IsNullOrEmpty(attr.WeaponOrSource) ? attr.WeaponOrSource : "Enemy Fire");
                        whoLine = $"{prefixTag}{actionVerb} {attacker}";
                    }

                    whoLine = whoLine.Replace('_', ' ').Trim();
                    while (whoLine.Contains("  "))
                        whoLine = whoLine.Replace("  ", " ");

                    List<string> detailParts = new List<string>(3);
                    if (!attr.IsSelfInflicted && !string.IsNullOrEmpty(attr.WeaponOrSource))
                    {
                        string cleanWep = attr.WeaponOrSource.Replace('_', ' ').Trim();
                        while (cleanWep.Contains("  "))
                            cleanWep = cleanWep.Replace("  ", " ");
                        if (!string.IsNullOrEmpty(cleanWep) &&
                            !whoLine.EndsWith(cleanWep, StringComparison.OrdinalIgnoreCase))
                        {
                            detailParts.Add(cleanWep);
                        }
                    }

                    if (!string.IsNullOrEmpty(attr.HitDetail))
                    {
                        string cleanHit = attr.HitDetail.Replace('_', ' ').Trim();
                        while (cleanHit.Contains("  "))
                            cleanHit = cleanHit.Replace("  ", " ");
                        if (!string.IsNullOrEmpty(cleanHit) &&
                            !whoLine.EndsWith(cleanHit, StringComparison.OrdinalIgnoreCase) &&
                            (detailParts.Count == 0 || !string.Equals(detailParts[0], cleanHit, StringComparison.OrdinalIgnoreCase)))
                        {
                            detailParts.Add(cleanHit);
                        }
                    }

                    if (!attr.IsSelfInflicted || attr.DistanceMeters > 0)
                    {
                        detailParts.Add($"{attr.DistanceMeters}m");
                    }

                    string howLine = detailParts.Count > 0 ? string.Join("  \u2022  ", detailParts) : "";

                    bool shownOnNativeDeathPanel = false;
                    if (yearLabel != null)
                    {
                        yearLabel.text = !string.IsNullOrEmpty(howLine)
                            ? $"<size={yearFontSize}>{yearLineWithAge}</size>\n<size=27><color={richColor}>{whoLine}</color></size>\n<size=23><color={subColor}>{howLine}</color></size>"
                            : $"<size={yearFontSize}>{yearLineWithAge}</size>\n<size=27><color={richColor}>{whoLine}</color></size>";
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
                        ActiveDeathAttributionText = !string.IsNullOrEmpty(howLine)
                            ? $"{whoLine}\n{howLine}"
                            : whoLine;
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
