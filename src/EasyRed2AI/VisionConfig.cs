using BepInEx.Configuration;

namespace EasyRed2HumanAI
{
    public static class VisionConfig
    {
        public static ConfigEntry<bool> EnableMod;
        public static ConfigEntry<bool> DebugLogging;
        public static ConfigEntry<bool> EnableDeathScreenAttribution;

        // 1. Infantry Sight & Stance
        public static ConfigEntry<float> StandingSightRange;
        public static ConfigEntry<float> CrouchingSightRange;
        public static ConfigEntry<float> ProneSightRange;
        public static ConfigEntry<bool> PreserveStanceStealthInCombat;
        public static ConfigEntry<float> CombatAwareStanceMultiplier;
        public static ConfigEntry<float> FiringRevealDuration;
        public static ConfigEntry<float> FiringSightRangeBonus;

        // 2. Scopes & Optics
        public static ConfigEntry<bool> EnableScopeVisionScaling;
        public static ConfigEntry<float> MaxOpticZoomMultiplier;
        public static ConfigEntry<float> OpticLongRangeHalfFOV;

        // 3. Human FOV Cones
        public static ConfigEntry<bool> EnforceHumanFOVWhenAlerted;
        public static ConfigEntry<float> CloseOmnidirectionalRange;
        public static ConfigEntry<float> MidRangeMaxDistance;
        public static ConfigEntry<float> MidRangeHalfFOV;
        public static ConfigEntry<float> LongRangeHalfFOV;

        // 4. Vehicles & Aircraft
        public static ConfigEntry<float> VehicleCamoDistance;
        public static ConfigEntry<bool> LimitAircraftInfantrySpotting;
        public static ConfigEntry<float> AircraftUnmarkedInfantryMaxDist;

        // 5. Phase 2: Veteran Progression (15-Minute Linear Scale & Realistic Caps)
        public static ConfigEntry<bool> EnableVeteranSystem;
        public static ConfigEntry<float> VeteranFullMaturitySeconds;
        public static ConfigEntry<float> VeteranMaxSpreadReduction;
        public static ConfigEntry<bool> PromoteVeteranRanks;
        public static ConfigEntry<bool> ShowVeteran3DIcons;
        public static ConfigEntry<bool> ShowVeteranSquadRosterTags;

        // 6. Phase 2: 5-Tier Survival Priority, Stance Redirection, Bipods, Muzzle Discipline & Vaulting
        public static ConfigEntry<bool> EnableSelfPreservationStance;
        public static ConfigEntry<bool> EnforceStandingWhenOutOfCombat;
        public static ConfigEntry<bool> NeverShootStandingInOpen;
        public static ConfigEntry<bool> BandageOnlyAfterFindingCover;
        public static ConfigEntry<bool> EnableGunnerBipodWallMounting;
        public static ConfigEntry<bool> EnableWallShootingPrevention;
        public static ConfigEntry<float> MuzzleObstructionCheckDist;
        public static ConfigEntry<bool> EnableAutoPeekOverCover;
        public static ConfigEntry<bool> EnableFriendlyFireAvoidance;
        public static ConfigEntry<float> FriendlyFireConeDegrees;
        public static ConfigEntry<float> FriendlyFireCorridorWidth;
        public static ConfigEntry<bool> EnableAntiStacking;
        public static ConfigEntry<float> MinFriendlySpacingMeters;
        public static ConfigEntry<bool> EnableWallVaulting;
        public static ConfigEntry<bool> EnableComradeLiberation;
        public static ConfigEntry<float> LiberateSearchRadius;
        public static ConfigEntry<bool> EnableSmartTankCrewRepair;
        public static ConfigEntry<bool> EnableSmartWallSideCover;
        public static ConfigEntry<bool> PreventSmallArmsFireAtTanks;

        public static void Init(ConfigFile config)
        {
            EnableMod = config.Bind(
                "0. General",
                "EnableMod",
                true,
                "Master toggle for the Easy Red 2 Humanized AI Vision & Awareness mod.");

            DebugLogging = config.Bind(
                "0. General",
                "DebugLogging",
                false,
                "Log periodic AI spotting/vision statistics to the BepInEx console for tuning.");

            EnableDeathScreenAttribution = config.Bind(
                "0. General",
                "EnableDeathScreenAttribution",
                true,
                "Display who killed you, whether it was Friendly Fire, what weapon/vehicle/explosive was used, hit location, and distance on the death screen.");

            // 1. Infantry Sight & Stance
            StandingSightRange = config.Bind(
                "1. Infantry Sight & Stance",
                "StandingSightRange",
                150f,
                "Base distance (meters) at which un-scoped AI can spot a Standing soldier (Vanilla: 220m unspotted / 300m in combat).");

            CrouchingSightRange = config.Bind(
                "1. Infantry Sight & Stance",
                "CrouchingSightRange",
                80f,
                "Base distance (meters) at which un-scoped AI can spot a Crouching soldier (Vanilla: 50m unspotted / 300m in combat).");

            ProneSightRange = config.Bind(
                "1. Infantry Sight & Stance",
                "ProneSightRange",
                40f,
                "Base distance (meters) at which un-scoped AI can spot a Prone soldier (Vanilla: 37m unspotted / 300m in combat).");

            PreserveStanceStealthInCombat = config.Bind(
                "1. Infantry Sight & Stance",
                "PreserveStanceStealthInCombat",
                true,
                "If true, squad combat awareness (IsEnemyAwareOfThisSquad) no longer jumps crouched/prone soldiers to a flat 300m visibility.");

            CombatAwareStanceMultiplier = config.Bind(
                "1. Infantry Sight & Stance",
                "CombatAwareStanceMultiplier",
                1.20f,
                "Multiplier applied to stance sight range when the enemy is aware of your squad's general area (e.g. 1.2x means 40m prone becomes 48m instead of 300m).");

            FiringRevealDuration = config.Bind(
                "1. Infantry Sight & Stance",
                "FiringRevealDuration",
                4.0f,
                "Seconds after an individual soldier fires a weapon during which that specific soldier is easier to spot (muzzle flash/smoke/sound).");

            FiringSightRangeBonus = config.Bind(
                "1. Infantry Sight & Stance",
                "FiringSightRangeBonus",
                85f,
                "Extra distance (meters) added to an individual soldier's visibility for FiringRevealDuration seconds after they shoot.");

            // 2. Scopes & Optics
            EnableScopeVisionScaling = config.Bind(
                "2. Scopes & Optics",
                "EnableScopeVisionScaling",
                true,
                "Scale AI sight distance by the observer's actual weapon scope or turret optic zoom (60 / AimingFOV).");

            MaxOpticZoomMultiplier = config.Bind(
                "2. Scopes & Optics",
                "MaxOpticZoomMultiplier",
                2.25f,
                "Maximum sight range multiplier granted to scoped marksmen/snipers and magnified tank optics.");

            OpticLongRangeHalfFOV = config.Bind(
                "2. Scopes & Optics",
                "OpticLongRangeHalfFOV",
                25f,
                "Half-angle FOV cone (degrees, so 25 = 50 deg total cone) required for a scoped sniper or turret to spot targets beyond un-scoped eye range.");

            // 3. Human FOV Cones
            EnforceHumanFOVWhenAlerted = config.Bind(
                "3. Human FOV Cones",
                "EnforceHumanFOVWhenAlerted",
                true,
                "Fixes the vanilla quirk where alerted or suppressed AI skip the FOV check and get 360-degree vision out to 300m.");

            CloseOmnidirectionalRange = config.Bind(
                "3. Human FOV Cones",
                "CloseOmnidirectionalRange",
                20f,
                "Distance (meters) within which AI can hear/detect enemies in 360 degrees (Vanilla: 30m).");

            MidRangeMaxDistance = config.Bind(
                "3. Human FOV Cones",
                "MidRangeMaxDistance",
                75f,
                "Upper distance (meters) for mid-range peripheral vision before transitioning to the narrower long-range gaze cone.");

            MidRangeHalfFOV = config.Bind(
                "3. Human FOV Cones",
                "MidRangeHalfFOV",
                80f,
                "Half-angle FOV (degrees, 80 = 160 deg total cone) for spotting enemies at mid range (CloseOmnidirectionalRange to MidRangeMaxDistance).");

            LongRangeHalfFOV = config.Bind(
                "3. Human FOV Cones",
                "LongRangeHalfFOV",
                45f,
                "Half-angle FOV (degrees, 45 = 90 deg total cone) for spotting enemies beyond MidRangeMaxDistance.");

            // 4. Vehicles & Aircraft
            VehicleCamoDistance = config.Bind(
                "4. Vehicles & Aircraft",
                "VehicleCamoDistance",
                550f,
                "Base distance (meters) at which vehicles can be spotted (Vanilla: 550m).");

            LimitAircraftInfantrySpotting = config.Bind(
                "4. Vehicles & Aircraft",
                "LimitAircraftInfantrySpotting",
                true,
                "If true, AI aircraft will only spot ground infantry from afar if their squad is Marked (spotted) or near a radio request, rather than X-ray spotting un-marked infantry across the map.");

            AircraftUnmarkedInfantryMaxDist = config.Bind(
                "4. Vehicles & Aircraft",
                "AircraftUnmarkedInfantryMaxDist",
                180f,
                "Maximum distance (meters) at which an aircraft pilot can spot an un-marked, non-stealth infantryman on the ground without a squad marker.");

            // 5. Phase 2: Veteran Progression (15-Minute Linear Scale & Realistic Caps)
            EnableVeteranSystem = config.Bind(
                "5. Veteran Progression",
                "EnableVeteranSystem",
                true,
                "Enable the 15-minute linear Veteran-Hardened progression system (survivors gradually become more cautious, steady, and cover-responsive up to realistic human caps).");

            VeteranFullMaturitySeconds = config.Bind(
                "5. Veteran Progression",
                "VeteranFullMaturitySeconds",
                900f,
                "Survival time in seconds (900s = 15 minutes) required for a soldier to linearly reach 100% Veteran status.");

            VeteranMaxSpreadReduction = config.Bind(
                "5. Veteran Progression",
                "VeteranMaxSpreadReduction",
                0.20f,
                "Maximum weapon dispersion reduction at 100% Veteran cap (0.20 = 20% steadier aim; never laser-accurate or impossible to kill).");

            PromoteVeteranRanks = config.Bind(
                "5. Veteran Progression",
                "PromoteVeteranRanks",
                true,
                "Dynamically promote surviving soldiers in rank (Private -> Corporal -> Sergeant -> First Sergeant) and update their 3D uniform shoulder rank decals.");

            ShowVeteran3DIcons = config.Bind(
                "5. Veteran Progression",
                "ShowVeteran3DIcons",
                true,
                "Display an in-game 3D overhead chevron badge above friendly (and spectated) Veteran soldiers alongside Squad Leader / Medic / Radioman icons.");

            ShowVeteranSquadRosterTags = config.Bind(
                "5. Veteran Progression",
                "ShowVeteranSquadRosterTags",
                true,
                "Display [Vet I], [Vet II], and [Vet III ★] tags and rank colors in the bottom-left squad member UI.");

            // 6. Phase 2: 5-Tier Survival Priority, Stance Redirection, Bipods, Muzzle Discipline & Vaulting
            EnableSelfPreservationStance = config.Bind(
                "6. Tactical Self-Preservation & Muzzle Discipline",
                "EnableSelfPreservationStance",
                true,
                "Priority 1 (Don't Die): Bots caught in the open drop to Crouch/Prone or duck into cover BEFORE opening fire.");

            EnforceStandingWhenOutOfCombat = config.Bind(
                "6. Tactical Self-Preservation & Muzzle Discipline",
                "EnforceStandingWhenOutOfCombat",
                true,
                "Redirect vanilla's 'favourite fighting pose': bots stay fully standing (uncrouched) when moving or out of combat unless actively engaging a target, suppressed, bandaging in cover, or stealth-following a crouched squad leader.");

            NeverShootStandingInOpen = config.Bind(
                "6. Tactical Self-Preservation & Muzzle Discipline",
                "NeverShootStandingInOpen",
                true,
                "Firing from standing is ineffective: bots always at least Crouch when shooting (and Machine Gunners drop Prone or mount on cover) unless peeking over high cover or high-wall bipod mounted.");

            BandageOnlyAfterFindingCover = config.Bind(
                "6. Tactical Self-Preservation & Muzzle Discipline",
                "BandageOnlyAfterFindingCover",
                true,
                "Bleeding soldiers actively seek and sprint to nearby cover FIRST before stopping to bandage (unless no cover exists nearby or emergency bleed-out timeout is reached).");

            EnableGunnerBipodWallMounting = config.Bind(
                "6. Tactical Self-Preservation & Muzzle Discipline",
                "EnableGunnerBipodWallMounting",
                true,
                "Machine gunners and automatic riflemen (BAR, Bren, MG34/42, DP-27, etc.) actively seek out walls/sandbags/windowsills for stability and mount their bipods on wall surfaces or the ground.");

            EnableWallShootingPrevention = config.Bind(
                "6. Tactical Self-Preservation & Muzzle Discipline",
                "EnableWallShootingPrevention",
                true,
                "Priority 3 (Muzzle Discipline): Prevent bots from shooting straight into walls, windowsills, sandbags, or terrain crests right in front of their gun barrel.");

            MuzzleObstructionCheckDist = config.Bind(
                "6. Tactical Self-Preservation & Muzzle Discipline",
                "MuzzleObstructionCheckDist",
                2.2f,
                "Distance (meters) in front of the weapon muzzle to check for wall/cover obstruction before pulling the trigger.");

            EnableAutoPeekOverCover = config.Bind(
                "6. Tactical Self-Preservation & Muzzle Discipline",
                "EnableAutoPeekOverCover",
                true,
                "When a bot's muzzle is blocked by low cover (sandbag/windowsill), automatically step up one stance (Prone -> Crouch, Crouch -> Stand) to peek over the obstacle.");

            EnableFriendlyFireAvoidance = config.Bind(
                "6. Tactical Self-Preservation & Muzzle Discipline",
                "EnableFriendlyFireAvoidance",
                true,
                "Hold fire if a friendly soldier is in the line of fire between the shooter and the target.");

            FriendlyFireConeDegrees = config.Bind(
                "6. Tactical Self-Preservation & Muzzle Discipline",
                "FriendlyFireConeDegrees",
                5.0f,
                "Half-angle cone (degrees) around the line of fire within which a friendly soldier causes the AI to hold fire (5.0 deg base, 6.5 deg for veterans).");

            FriendlyFireCorridorWidth = config.Bind(
                "6. Tactical Self-Preservation & Muzzle Discipline",
                "FriendlyFireCorridorWidth",
                0.65f,
                "Close-range lateral corridor width (meters) within 8m of the muzzle where friendly torso/shoulders block firing.");

            EnableAntiStacking = config.Bind(
                "6. Tactical Self-Preservation & Muzzle Discipline",
                "EnableAntiStacking",
                true,
                "Prevent bots from stacking on top of friendly soldiers when stopping to aim/shoot by nudging them laterally into a firing line.");

            MinFriendlySpacingMeters = config.Bind(
                "6. Tactical Self-Preservation & Muzzle Discipline",
                "MinFriendlySpacingMeters",
                2.4f,
                "Minimum personal space separation (meters) maintained between friendly soldiers when stationary/firing.");

            EnableWallVaulting = config.Bind(
                "6. Tactical Self-Preservation & Muzzle Discipline",
                "EnableWallVaulting",
                true,
                "Priority 2 (Objective Mobility): Teach AI soldiers to vault over low walls, fences, and sandbags when advancing.");

            EnableComradeLiberation = config.Bind(
                "6. Tactical Self-Preservation & Muzzle Discipline",
                "EnableComradeLiberation",
                true,
                "Priority 4 (Rescue & Liberate): Nearby unpinned friendly bots actively move toward surrendered comrades and liberate them.");

            LiberateSearchRadius = config.Bind(
                "6. Tactical Self-Preservation & Muzzle Discipline",
                "LiberateSearchRadius",
                30.0f,
                "Radius (meters) within which unpinned friendly bots search for surrendered comrades to liberate.");

            EnableSmartTankCrewRepair = config.Bind(
                "6. Tactical Self-Preservation & Muzzle Discipline",
                "EnableSmartTankCrewRepair",
                true,
                "Tank and vehicle crews route to the sheltered leeward/rear side of the vehicle hull (opposite the enemy firing line / turret threat direction) and crouch behind the armor before repairing.");

            EnableSmartWallSideCover = config.Bind(
                "6. Tactical Self-Preservation & Muzzle Discipline",
                "EnableSmartWallSideCover",
                true,
                "Ensure defenders and squads stand BEHIND walls/cover (with the wall between the soldier and incoming fire) rather than standing on the exposed front side with the wall at their back, and keep stationary cover/objective defenders crouched.");

            PreventSmallArmsFireAtTanks = config.Bind(
                "6. Tactical Self-Preservation & Muzzle Discipline",
                "PreventSmallArmsFireAtTanks",
                true,
                "Stop infantry from uselessly firing small arms (rifles, SMGs, MGs, pistols) at armored tanks/vehicles unless their weapon can actually penetrate a vehicle part/wheel/track or they can shoot a visible exposed crew member.");
        }
    }
}
