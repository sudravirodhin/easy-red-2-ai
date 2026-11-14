using BepInEx.Configuration;
using UnityEngine;

namespace EasyRed2HumanAI
{
    public static class VisionConfig
    {
        public static ConfigEntry<bool> EnableMod;
        public static ConfigEntry<bool> DebugLogging;
        public static ConfigEntry<bool> EnableHitchFlightRecorder;
        public static ConfigEntry<bool> EnableBotActionTrace;
        public static ConfigEntry<bool> EnableDeathScreenAttribution;
        public static ConfigEntry<bool> EnableQuickTapSpot;
        public static ConfigEntry<bool> EnableExtendedRoomNames;
        public static ConfigEntry<bool> EnableMultiplayerBattleStatSyncFix;
        public static ConfigEntry<bool> EnableAdaptiveAiTickScaling;
        public static ConfigEntry<float> UiScaleMultiplier;
        public static ConfigEntry<KeyCode> MapPickerHotkey;

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

        // 3. Human FOV Cones & Dynamic Reaction Latency
        public static ConfigEntry<bool> EnforceHumanFOVWhenAlerted;
        public static ConfigEntry<bool> EnableDynamicHumanReactionTime;
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
        public static ConfigEntry<bool> EnableThreatSectorConcentration;
        public static ConfigEntry<bool> EnableLocalContactCallouts;
        public static ConfigEntry<bool> EnableLastKnownSuppression;
        public static ConfigEntry<bool> EnableIndoorCeilingPronePrevention;
        public static ConfigEntry<bool> EnableBoundingOverwatch;
        public static ConfigEntry<bool> EnableMacroObjectiveDirector;
        public static ConfigEntry<bool> EnableProactiveEmplacementStaffing;
        public static ConfigEntry<bool> EnableSmartApcDismount;
        public static ConfigEntry<bool> EnableTankStandoffAndReverse;
        public static ConfigEntry<bool> EnableFriendlyVehicleRightOfWay;
        public static ConfigEntry<bool> PreventSmallArmsFireAtTanks;
        public static ConfigEntry<bool> EnablePointBlankMelee;
        public static ConfigEntry<bool> EnableStrategicDoorDiscipline;
        public static ConfigEntry<bool> EnableCombinedArmsPrioritySupport;
        public static ConfigEntry<bool> EnableBridgeCrossingDiscipline;
        public static ConfigEntry<bool> EnableShellShockSurrender;
        public static ConfigEntry<bool> EnableTacticalGoapPlanner;
        public static ConfigEntry<bool> EnableRadiomanIntelNet;
        public static ConfigEntry<bool> EnableTacticalVoiceCallouts;
        public static ConfigEntry<bool> EnableFactionNetDoctrines;
        public static ConfigEntry<bool> EnableCoopAirborneSpawnFix;
        public static ConfigEntry<bool> EnableTankRockUnstickAssist;
        public static ConfigEntry<bool> EnableSmartVehicleAmmoSelection;
        public static ConfigEntry<bool> EnableAntiDogpileMedicTriage;
        public static ConfigEntry<bool> EnableBuddyResupply;
        public static ConfigEntry<bool> EnableEmergentWearableScavenge;
        public static ConfigEntry<bool> EnableBuddyCasualtyDragging;
        public static ConfigEntry<bool> EnableConquestEconomyQuartermaster;
        public static ConfigEntry<int> ConquestPlayerTicketBuffer;
        public static ConfigEntry<bool> EnableTacticalRetreatsAndRegroup;

        // 7. Phase 3: Role-Based AI Personalities (3 Archetypes per Infantry & Vehicle Role)
        public static ConfigEntry<bool> EnableRolePersonalities;
        public static ConfigEntry<bool> EnableInfantryPersonalities;
        public static ConfigEntry<bool> EnableVehicleAndPilotPersonalities;
        public static ConfigEntry<bool> VeterancyCuresSkittishness;
        public static ConfigEntry<bool> ShowPersonalityInSquadRoster;
        public static ConfigEntry<bool> ShowPersonalityOnDeathScreen;

        // 8. Phase 4: Ballistics, Plane Audio De-Throbber & Battlefield Immersion
        public static ConfigEntry<bool> EnablePlaneAudioSmoothing;
        public static ConfigEntry<bool> EnableThinCoverBulletPenetration;
        public static ConfigEntry<bool> EnableRealisticVehicleArmorAndTopAttack;
        public static ConfigEntry<bool> EnableParachuteWindDrift;
        public static ConfigEntry<bool> EnableSpectatorHudRetention;
        public static ConfigEntry<bool> EnableSpectatorBotInspector;
        public static ConfigEntry<bool> EnableObjectiveBarFactionNames;
        public static ConfigEntry<bool> EnableFirstPersonShadowFix;
        public static ConfigEntry<bool> EnableBloodSmokeWaterOcclusion;

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

            EnableHitchFlightRecorder = config.Bind(
                "0. General",
                "EnableHitchFlightRecorder",
                false,
                "Diagnostics: Enable the zero-allocation [HitchAutopsy] (>28ms frame-spike profiler) and 15s [HitchSummary] GC/subsystem flight recorder in BepInEx/LogOutput.log (default false).");

            EnableBotActionTrace = config.Bind(
                "0. General",
                "EnableBotActionTrace",
                false,
                "Diagnostics: Write transition-only machine-readable per-bot GOAP, combat, bandage, melee, retreat, and vault events to BepInEx/BotActionTrace.jsonl on a background thread with zero main-thread I/O hitches (default false).");

            EnableDeathScreenAttribution = config.Bind(
                "0. General",
                "EnableDeathScreenAttribution",
                true,
                "Display who killed you, whether it was Friendly Fire, what weapon/vehicle/explosive was used, hit location, and distance on the death screen.");

            EnableQuickTapSpot = config.Bind(
                "0. General",
                "EnableQuickTapSpot",
                true,
                "QoL: Short-pressing your configured Squad Orders keybind (GameInput.OrdersMenu, default Tab) spots in your look direction (PlayerController.TrySpotInLookDirection), even as a Squad Leader or Radioman, while holding the key still opens the radial Squad Orders wheel.");

            EnableExtendedRoomNames = config.Bind(
                "0. General",
                "EnableExtendedRoomNames",
                true,
                "QoL: Lift vanilla's 8-character multiplayer room name cap up to 64 characters when hosting or searching for servers.");

            EnableMultiplayerBattleStatSyncFix = config.Bind(
                "0. General",
                "EnableMultiplayerBattleStatSyncFix",
                true,
                "Co-op / Multiplayer Fix: Prevents Photon CustomProperties read-modify-write clobbering, EndBattleGUI RPC races, and duplicate vehicle-disable increments so Casualties, Prisoners, and Vehicles Lost match accurately across Host and all Clients.");

            EnableAdaptiveAiTickScaling = config.Bind(
                "0. General",
                "EnableAdaptiveAiTickScaling",
                true,
                "Performance & Responsiveness (SP & Co-op): Dynamically scales Creature.ProcessSequentialUpdates from vanilla's hardcoded 1 slot/frame up to 2-3 active on-foot SoldierAI brain ticks/frame in large battles (while skipping non-soldier vehicle/dead slots without wasting a frame tick, and backing off automatically if frame time exceeds 25ms).");

            UiScaleMultiplier = config.Bind(
                "0. General",
                "UiScaleMultiplier",
                1.0f,
                "UI scale multiplier applied on top of automatic 1080p-normalized DPI scaling (e.g. 1.0 = auto 2.0x at 4K / 1.33x at 1440p; range 0.75 - 1.50) for F5, F6, F10 banners, and Spectator Bot Inspector.");

            MapPickerHotkey = config.Bind(
                "0. General",
                "MapPickerHotkey",
                KeyCode.F5,
                "Hotkey to open the standalone In-Game Campaign & Mission Picker window (default F5).");

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

            // 3. Human FOV Cones & Dynamic Reaction Latency
            EnforceHumanFOVWhenAlerted = config.Bind(
                "3. Human FOV Cones",
                "EnforceHumanFOVWhenAlerted",
                true,
                "Enforces continuous distance-scaled 'squint' vision cones (wide close peripheral awareness tapering smoothly with distance) and platform-specific tank/emplacement/aircraft arcs.");

            EnableDynamicHumanReactionTime = config.Bind(
                "3. Human FOV Cones",
                "EnableDynamicHumanReactionTime",
                true,
                "Models human 'Watching vs. Caught Out' reaction latency, eliminating vanilla's 2.5s statue freeze while enforcing realistic recognition, shoulder/turn, sight-settle, and Tank Commander -> Gunner intercom callout delays.");

            CloseOmnidirectionalRange = config.Bind(
                "3. Human FOV Cones",
                "CloseOmnidirectionalRange",
                12f,
                "Point-blank distance (meters) within which on-foot infantry can hear/sense nearby enemies in 360 degrees before smoothly tapering into wide close-combat peripheral vision.");

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
                "Hold fire if a friendly soldier is in the line of fire, and make friendly bots avoid claiming cover or walking upright into a human player's active aiming crosshair (ducking under sights, sidestepping clear, or passing behind the player).");

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

            EnableThreatSectorConcentration = config.Bind(
                "6. Tactical Self-Preservation & Muzzle Discipline",
                "EnableThreatSectorConcentration",
                true,
                "Stationary defenders and overwatch troops orient their gaze toward known enemy contact sectors (spotted/loud/aware squads) and spot targets along their watched sector faster, while leaving quiet unspotted flanks open to flanking maneuvers.");

            EnableLocalContactCallouts = config.Bind(
                "6. Tactical Self-Preservation & Muzzle Discipline",
                "EnableLocalContactCallouts",
                true,
                "When a soldier visually spots an enemy, share that enemy's last-known contact position with nearby friendly bots within 15m so nearby squadmates orient their gaze toward the threat sector without granting X-ray wallhacks.");

            EnableLastKnownSuppression = config.Bind(
                "6. Tactical Self-Preservation & Muzzle Discipline",
                "EnableLastKnownSuppression",
                true,
                "When a confirmed enemy ducks behind cover, stationary Machine Gunners / automatic riflemen fire a short suppressive burst at the last-known cover edge and nearby infantry within range can flush the cover with a fragmentation grenade.");

            EnableIndoorCeilingPronePrevention = config.Bind(
                "6. Tactical Self-Preservation & Muzzle Discipline",
                "EnableIndoorCeilingPronePrevention",
                true,
                "Prevent soldiers inside buildings, bunkers, and pillboxes (where a low ceiling is overhead) from dropping Prone under windowsills or stairs, keeping them in a functional Crouch fighting stance.");

            EnableBoundingOverwatch = config.Bind(
                "6. Tactical Self-Preservation & Muzzle Discipline",
                "EnableBoundingOverwatch",
                true,
                "Attacking infantry advancing under suppression execute staggered 5-second crouched bounds alternated with 4-second overwatch firing pauses so half the squad provides covering fire while the other half advances.");

            EnableMacroObjectiveDirector = config.Bind(
                "6. Tactical Self-Preservation & Muzzle Discipline",
                "EnableMacroObjectiveDirector",
                true,
                "6.0s MasterClient Macro Director: keeps ~20% of autonomous squads garrisoning newly captured objectives (prioritizing contested/threatened points) while directing remaining squads to attack or counterattack uncaptured objectives.");

            EnableProactiveEmplacementStaffing = config.Bind(
                "6. Tactical Self-Preservation & Muzzle Discipline",
                "EnableProactiveEmplacementStaffing",
                true,
                "Defending and garrisoning squads proactively assign an available rifleman/gunner to crew nearby empty static weapons (MG tripods, bunker MGs, AT guns, AA guns) or parked armed halftracks facing the threat sector.");

            EnableSmartApcDismount = config.Bind(
                "6. Tactical Self-Preservation & Muzzle Discipline",
                "EnableSmartApcDismount",
                true,
                "Infantry riding in AI-driven APCs, halftracks, and trucks dismount immediately when the transport is suppressed, takes hull damage under fire, or comes within 48m of enemy AT/infantry threats rather than riding into point-blank destruction.");

            EnableTankStandoffAndReverse = config.Bind(
                "6. Tactical Self-Preservation & Muzzle Discipline",
                "EnableTankStandoffAndReverse",
                true,
                "AI tanks with a clear main-gun line of sight hold practical standoff distance (scaled by Brawler/Support/Sniper crew personality) instead of driving into infantry AT-grenade range, and reverse (MoveTankRetro) when threats get too close.");

            EnableFriendlyVehicleRightOfWay = config.Bind(
                "6. Tactical Self-Preservation & Muzzle Discipline",
                "EnableFriendlyVehicleRightOfWay",
                true,
                "Friendly Infantry Right-of-Way & Anti-Roadkill: AI tanks and wheeled vehicles yield/brake when friendly soldiers (player or AI) or mechanics are in their driving/reversing corridor, nudge friendly AI infantry to step aside off the tracks/road, and prevent friendly vehicles from roadkilling same-faction soldiers on contact.");

            PreventSmallArmsFireAtTanks = config.Bind(
                "6. Tactical Self-Preservation & Muzzle Discipline",
                "PreventSmallArmsFireAtTanks",
                true,
                "Stop infantry from uselessly firing small arms (rifles, SMGs, MGs, pistols) or throwing standard fragmentation grenades (< 12mm penetration) at buttoned-up armored tanks/vehicles unless they can actually penetrate a vehicle part/wheel/track or flush out an exposed crew member.");

            EnablePointBlankMelee = config.Bind(
                "6. Tactical Self-Preservation & Muzzle Discipline",
                "EnablePointBlankMelee",
                true,
                "In point-blank CQB (<= 3.8m with bayonet, <= 2.5m with any weapon, or <= 5.0m when empty/reloading/bolt-cycling), bots automatically fix/unfold bayonets, stand up, close distance, draw knives when empty, and execute bayonet stabs, melee slashes, and rifle butt-strokes instead of crouched point-blank mag-dumping.");

            EnableStrategicDoorDiscipline = config.Bind(
                "6. Tactical Self-Preservation & Muzzle Discipline",
                "EnableStrategicDoorDiscipline",
                true,
                "Strategic Door & Window Shutter Discipline: prevents indoor bots from accidentally popping open closed window shutters and side doors as they walk past, closes doors behind advancing/garrisoning troops once squadmates are clear, and pulls window shutters closed when heavily suppressed, reloading, or bandaging.");

            EnableCombinedArmsPrioritySupport = config.Bind(
                "6. Tactical Self-Preservation & Muzzle Discipline",
                "EnableCombinedArmsPrioritySupport",
                true,
                "Combined-Arms Priority Support: AI tanks, emplacements/turrets, and CAS aircraft prioritize KNOWN enemy squads and vehicles (Spotted/Marked squads, active Friendly Radio Support Requests, and enemy squads actively engaging friendly infantry) instead of randomly attacking lone targets or orbiting the first objective in the list.");

            EnableBridgeCrossingDiscipline = config.Bind(
                "6. Tactical Self-Preservation & Muzzle Discipline",
                "EnableBridgeCrossingDiscipline",
                true,
                "Bridge Crossing, Parapet Cover & Bipod Propping: prevents vanilla's 5m-35m sideways squad-spacing scatter from flinging waypoints off bridges into rivers/ravines, while letting suppressed bridge troops hug deck-safe parapet walls/pillars, prop gunner bipods on bridge balustrades, and bound safely along the span.");

            EnableShellShockSurrender = config.Bind(
                "6. Tactical Self-Preservation & Muzzle Discipline",
                "EnableShellShockSurrender",
                true,
                "Shell-Shocked & Skittish Dynamic Surrender: isolated, heavily suppressed/shell-shocked Skittish ([Cautious] / [Triage]) or green rookie soldiers who are overrun at close range (<= 18m) with no inspiring Squad Leader nearby can dynamically surrender (SurrenderSynched), and can later be liberated by friendly counterattacks.");

            EnableTacticalGoapPlanner = config.Bind(
                "6. Tactical Self-Preservation & Muzzle Discipline",
                "EnableTacticalGoapPlanner",
                true,
                "F.E.A.R.-Style 3-Tier Bitmask GOAP Planner & Squad Blackboard: coordinates Individual -> Squad -> Team fire-and-maneuver (Suppressors pin enemy positions on the Squad Blackboard to unlock lateral Flanker bounds, Smoke-Screen advances, and Grenade breaches).");

            EnableRadiomanIntelNet = config.Bind(
                "6. Tactical Self-Preservation & Muzzle Discipline",
                "EnableRadiomanIntelNet",
                true,
                "Radioman-Gated Team Intel & Combined-Arms Net: squads only broadcast distant enemy contacts/armor sightings to friendly Tanks, CAS Aircraft, Artillery, and HQ Reserve Squads if they have an alive Radioman within 30m of their Squad Leader or are within 45m voice/visual contact of friendly forces.");

            EnableTacticalVoiceCallouts = config.Bind(
                "6. Tactical Self-Preservation & Muzzle Discipline",
                "EnableTacticalVoiceCallouts",
                true,
                "Audible GOAP Plan Voice Callouts: soldiers audibly announce coordinated plan transitions in their native faction voice ('Covering fire!', 'I'm moving!', 'Grenade!', 'Radioman is dead!', 'I'll take his seat!').");

            EnableFactionNetDoctrines = config.Bind(
                "6. Tactical Self-Preservation & Muzzle Discipline",
                "EnableFactionNetDoctrines",
                true,
                "Nation-Specific Team Net & GOAP Doctrines: gives all 20 Easy Red 2 factions (USA, Germany, USSR, UK, Japan, France, Italy, Poland, China, Finland, Mongolia, Manchukuo, Canada, Australia, New Zealand, India, Gurkha, Greece, Romania, Hungary + balanced default fallback) historically authentic radio net, flanking, emplacement, and GOAP cost habits.");

            EnableCoopAirborneSpawnFix = config.Bind(
                "6. Tactical Self-Preservation & Muzzle Discipline",
                "EnableCoopAirborneSpawnFix",
                true,
                "Vanilla Co-op Fix: hot-starts aircraft engine, throttle, and cruise velocity when a multiplayer client spawns into an airborne plane, and keeps remote client aircraft isGrounded state synced on the Host so enemy AI fighters intercept them.");

            EnableTankRockUnstickAssist = config.Bind(
                "6. Tactical Self-Preservation & Muzzle Discipline",
                "EnableTankRockUnstickAssist",
                true,
                "Vanilla Physics Fix: applies a smooth sprocket/track crawl impulse when a tank's discrete WheelCollider rays get high-centered on a rock, stump, or trench lip while throttle is held.");

            EnableSmartVehicleAmmoSelection = config.Bind(
                "6. Tactical Self-Preservation & Muzzle Discipline",
                "EnableSmartVehicleAmmoSelection",
                true,
                "Vanilla AI Fix: prevents tanks and attack planes from wasting heavy AP/APHE/HEAT cannon rounds on foot infantry when they have a loaded Coaxial, Hull, or Wing Machine Gun.");

            EnableAntiDogpileMedicTriage = config.Bind(
                "6. Tactical Self-Preservation & Muzzle Discipline",
                "EnableAntiDogpileMedicTriage",
                true,
                "Vanilla AI Fix: prevents 3-4 medics from dogpiling the same wounded soldier (caps at 1 medic, or 2 max if the first is distant/suppressed), releases the claim immediately if the medic dies or goes down, and fans extra medics out to other wounded allies.");

            EnableBuddyResupply = config.Bind(
                "6. Tactical Self-Preservation & Muzzle Discipline",
                "EnableBuddyResupply",
                true,
                "Emergent GOAP Buddy Resupply: bots visit live Medics for extra bandages (RefillBandages), borrow ammo from Riflemen/Support carriers (BorrowAmmos), and Support carriers proactively feed dry MG/AT squadmates.");

            EnableEmergentWearableScavenge = config.Bind(
                "6. Tactical Self-Preservation & Muzzle Discipline",
                "EnableEmergentWearableScavenge",
                true,
                "Emergent GOAP Radio Inheritance & Helmet Recovery: squads that lost their Radioman can loot and equip (SetWerable) a fallen friendly or enemy radio backpack to reconnect to the Team Radio Net, and helmetless bots pick up knocked-off or player-gifted helmets.");

            EnableBuddyCasualtyDragging = config.Bind(
                "6. Tactical Self-Preservation & Muzzle Discipline",
                "EnableBuddyCasualtyDragging",
                true,
                "Emergent GOAP Buddy Casualty Dragging: nearby riflemen drag/carry (CarryBodySynched) wounded teammates out of open fire into cover or directly to a nearby friendly/player Medic.");

            EnableConquestEconomyQuartermaster = config.Bind(
                "6. Tactical Self-Preservation & Muzzle Discipline",
                "EnableConquestEconomyQuartermaster",
                true,
                "Dynamic Operations (ConquestEconomy) Net Quartermaster: makes AI teams actually obey Conquest ticket budgets instead of spawning 400-500 ticket vehicles for free, surges cheap infantry waves when outnumbered, saves surplus tickets to unlock and buy heavy armor/aircraft counters, and protects a ticket buffer for human players.");

            ConquestPlayerTicketBuffer = config.Bind(
                "6. Tactical Self-Preservation & Muzzle Discipline",
                "ConquestPlayerTicketBuffer",
                135,
                new ConfigDescription(
                    "Minimum ConquestEconomy tickets reserved on human-occupied factions (+75 per extra Co-op player, +60 while a player is on the respawn screen) before AI unlocks or purchases expensive vehicle/aircraft squads. AI-only opposing factions use 0 buffer.",
                    new AcceptableValueRange<int>(0, 600)));

            EnableTacticalRetreatsAndRegroup = config.Bind(
                "6. Tactical Self-Preservation & Muzzle Discipline",
                "EnableTacticalRetreatsAndRegroup",
                true,
                "3-Tier Tactical Retreats & Regroup: enables TeamNet sector withdrawals for shattered squads, Squad Leader fighting withdrawals (smoke + 'Retreat!' callout + fallback rally point) when mauled or facing uncounterable close armor, and individual 'Oh Shit' break-contact sprints before surrendering.");

            // 7. AI Role Personalities (3 Archetypes per Infantry & Vehicle Role)
            EnableRolePersonalities = config.Bind(
                "7. AI Role Personalities",
                "EnableRolePersonalities",
                true,
                "Master toggle for the 3-per-role AI Personality system across all infantry, vehicle, and aircraft roles.");

            EnableInfantryPersonalities = config.Bind(
                "7. AI Role Personalities",
                "EnableInfantryPersonalities",
                true,
                "Enable role-weighted personalities for Squad Leaders, Assaulters, Riflemen, Machine Gunners, Marksmen, Anti-Tank/Sappers, and Medics.");

            EnableVehicleAndPilotPersonalities = config.Bind(
                "7. AI Role Personalities",
                "EnableVehicleAndPilotPersonalities",
                true,
                "Enable role-weighted personalities for Armored Tank Crews, Light/Wheeled Vehicle Crews, and Aircraft Pilots.");

            VeterancyCuresSkittishness = config.Bind(
                "7. AI Role Personalities",
                "VeterancyCuresSkittishness",
                true,
                "Cautious/Skittish rookies gradually shed their panic suppression penalties as they survive and gain Veterancy while retaining their smart cover habits.");

            ShowPersonalityInSquadRoster = config.Bind(
                "7. AI Role Personalities",
                "ShowPersonalityInSquadRoster",
                true,
                "Display each squadmate's personality trait (e.g. [Shock], [Anchor], [Deadeye]) in the bottom-left Squad Roster UI.");

            ShowPersonalityOnDeathScreen = config.Bind(
                "7. AI Role Personalities",
                "ShowPersonalityOnDeathScreen",
                true,
                "Include the attacker's personality archetype on the Death Screen obituary banner.");

            // 8. Ballistics, Plane Audio De-Throbber & Battlefield Immersion
            EnablePlaneAudioSmoothing = config.Bind(
                "8. Ballistics & Immersion",
                "EnablePlaneAudioSmoothing",
                true,
                "Client-side Plane Engine Loop De-Throbber: desynchronizes aircraft loop start times across near/far audio clips, clamps Doppler pitch spikes (<= 0.22), and low-pass smooths engine pitch transitions to eliminate repetitive flyby throbbing.");

            EnableThinCoverBulletPenetration = config.Bind(
                "8. Ballistics & Immersion",
                "EnableThinCoverBulletPenetration",
                true,
                "Material-aware thin-cover bullet penetration: allows full-power rifle, MG, and high-caliber rounds to penetrate thin wooden fences, doors, shutters, and light planks once with reduced velocity while solid terrain, masonry, and armor still stop rounds.");

            EnableRealisticVehicleArmorAndTopAttack = config.Bind(
                "8. Ballistics & Immersion",
                "EnableRealisticVehicleArmorAndTopAttack",
                true,
                "Realistic aircraft top-attack AP roof penetration, cockpit MG spall protection, and enclosed tank crew external blast protection.");

            EnableParachuteWindDrift = config.Bind(
                "8. Ballistics & Immersion",
                "EnableParachuteWindDrift",
                true,
                "Airborne parachute realism: softens paratrooper vertical descent speed by ~25% and adds smooth high-altitude canopy wind drift that tapers off before landing.");

            EnableSpectatorHudRetention = config.Bind(
                "8. Ballistics & Immersion",
                "EnableSpectatorHudRetention",
                true,
                "Keep the Mission Objectives/Tickets/Phase HUD (MissionStatusGUI) and 3D world objective/unit markers visible in Free Camera and Auto Battle Camera (press F in Spectator/Free Cam to cycle Full Tactical -> Objectives Only -> Clean Cinema).");

            EnableSpectatorBotInspector = config.Bind(
                "8. Ballistics & Immersion",
                "EnableSpectatorBotInspector",
                true,
                "Display a live Spectator Bot Inspector readout (Bot #ID matching BotActionTrace.jsonl, Role/Personality, Veterancy, Net/Squad Order, GOAP Goal -> Actions, and Combat Vitals) below the top Objective Bar when aiming near a bot in Free Camera.");

            EnableObjectiveBarFactionNames = config.Bind(
                "8. Ballistics & Immersion",
                "EnableObjectiveBarFactionNames",
                true,
                "QoL: Display formatted Faction + Alliance names (e.g. 'Germany (Axis)', 'USSR (Allies)') on the matching Left and Right sides of the top Objective Bar.");

            EnableFirstPersonShadowFix = config.Bind(
                "8. Ballistics & Immersion",
                "EnableFirstPersonShadowFix",
                true,
                "Vanilla Visual Fix: renders a ShadowsOnly head/neck/body proxy on the local player via SoldierLodManager.ApplyFace while keeping animated FPS arms, sleeves, and held weapon shadows intact so first-person self-shadows include head, helmet, torso, arms, and weapon without clipping into the camera.");

            EnableBloodSmokeWaterOcclusion = config.Bind(
                "8. Ballistics & Immersion",
                "EnableBloodSmokeWaterOcclusion",
                true,
                "Vanilla Visual Fix: suppresses disembodied 'ghost' blood sprays when flesh impacts occur underwater, across a water surface, or behind/inside tactical smoke clouds (swapping to ImpactFX_flesh_noBlood so impact audio still plays), and normalizes pooled blood particle depth-sorting so ambient map smoke and water transparencies never sort behind blood mist.");
        }

        public static void SetBundleEntries(bool state, params ConfigEntry<bool>[] entries)
        {
            if (entries == null) return;
            for (int i = 0; i < entries.Length; i++)
            {
                if (entries[i] != null)
                    entries[i].Value = state;
            }
        }
    }
}
