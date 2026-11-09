using System;
using System.Collections.Generic;
using UnityEngine;

namespace EasyRed2HumanAI
{
    public static partial class VeteranAndTacticsPatches
    {
        // =====================================================================
        // 3-TIER HIERARCHICAL GOAP, SQUAD BLACKBOARD & RADIOMAN INTEL NET (v0.6.1)
        // Tier 3 (Team Net GOAP): Cross-Squad Anvil & Hammer + Combined-Arms Support
        //         (Strictly gated by alive Radioman / Vehicle Radio relay!)
        // Tier 2 (Squad Leader GOAP): Translates Net Directive or Local Objective
        //         into Squad Directives & Role Slots (Suppress / Flank / Breach / Radio)
        // Tier 1 (Individual Bot GOAP): 32-bit WorldState + Role/Personality +
        //         Radioman Link Preservation + Lone-Wolf Survival/Link-Up
        // =====================================================================

        [Flags]
        internal enum GoapStateBits : uint
        {
            None                    = 0u,
            IsBleeding              = 1u << 0,
            InCover                 = 1u << 1,
            IsSuppressed            = 1u << 2,
            IsHeavilySuppressed     = 1u << 3,
            HasVisibleEnemy         = 1u << 4,
            CanFireAtVisibleEnemy   = 1u << 5,
            TargetLostBehindCover   = 1u << 6,
            SquadHasActiveThreat    = 1u << 7,
            AllyPinningThreat       = 1u << 8,
            ArmorThreatClose        = 1u << 9,
            HasFragGrenade          = 1u << 10,
            HasSmokeGrenade         = 1u << 11,
            InGrenadeRange          = 1u << 12,
            InPointBlankRange       = 1u << 13,
            FlankCoverAvailable     = 1u << 14,
            ExposedSideOfWall       = 1u << 15,
            ComradeNeedsLiberation  = 1u << 16,
            IsMachineGunner         = 1u << 17,
            IsAssaulterOrSkirmisher = 1u << 18,
            IsRadioman              = 1u << 19,
            RadioLinkDrifting       = 1u << 20,
            IsEntirelyAlone         = 1u << 21,
            OnBridgeOrNarrow        = 1u << 22,
            CanProvideOverwatch     = 1u << 23,
            NeedAmmoOrMedScavenge   = 1u << 24,
            ObjectiveCapContestUrgent = 1u << 25,

            // Goal & Chain Effect Bits
            SurvivedCrisis          = 1u << 26,
            RadioLinkPreserved      = 1u << 27,
            TargetPinned            = 1u << 28,
            TargetEliminated        = 1u << 29,
            SquadSupported          = 1u << 30
        }

        internal enum SquadNetDirective : byte
        {
            Disconnected_Autonomous = 0,
            Connected_StandardObjective = 1,
            Anvil_PinAndCallSupport = 2,
            Hammer_WideFlankEnvelopment = 3
        }

        internal enum GoapActionId : byte
        {
            None = 0,
            SeekEmergencyCover = 1,
            BandageInCover = 2,
            RepositionBehindWall = 3,
            MaintainRadioLinkWithLeader = 4,
            ThrowSmokeScreen = 5,
            SuppressThreatPosition = 6,
            MoveToFlankCover = 7,
            FlushThreatWithFrag = 8,
            HoldOverwatchFire = 9,
            BoundWithOverwatch = 10,
            PointBlankBayonetBreach = 11,
            RescueSurrenderedComrade = 12,
            LoneWolfLinkUpOrAmbush = 13,
            DirectFireEngagement = 14,
            ScavengeSupplyFromCorpse = 15
        }

        private struct GoapActionDef
        {
            public GoapActionId Id;
            public GoapStateBits Required;
            public GoapStateBits Forbidden;
            public GoapStateBits Add;
            public GoapStateBits Remove;
            public float BaseCost;
        }

        private static readonly GoapActionDef[] _goapActions = new GoapActionDef[]
        {
            new GoapActionDef
            {
                Id = GoapActionId.SeekEmergencyCover,
                Required = GoapStateBits.None,
                Forbidden = GoapStateBits.InCover | GoapStateBits.OnBridgeOrNarrow,
                Add = GoapStateBits.InCover | GoapStateBits.SurvivedCrisis,
                Remove = GoapStateBits.ExposedSideOfWall,
                BaseCost = 1.2f
            },
            new GoapActionDef
            {
                Id = GoapActionId.BandageInCover,
                Required = GoapStateBits.IsBleeding | GoapStateBits.InCover,
                Forbidden = GoapStateBits.InPointBlankRange,
                Add = GoapStateBits.SurvivedCrisis,
                Remove = GoapStateBits.IsBleeding,
                BaseCost = 0.7f
            },
            new GoapActionDef
            {
                Id = GoapActionId.RepositionBehindWall,
                Required = GoapStateBits.ExposedSideOfWall,
                Forbidden = GoapStateBits.OnBridgeOrNarrow | GoapStateBits.InPointBlankRange,
                Add = GoapStateBits.InCover | GoapStateBits.SurvivedCrisis,
                Remove = GoapStateBits.ExposedSideOfWall,
                BaseCost = 1.1f
            },
            // Radioman Lifeline Action: keep radio link alive with Squad Leader & confirm radio support calls
            new GoapActionDef
            {
                Id = GoapActionId.MaintainRadioLinkWithLeader,
                Required = GoapStateBits.IsRadioman | GoapStateBits.RadioLinkDrifting,
                Forbidden = GoapStateBits.IsBleeding | GoapStateBits.InPointBlankRange,
                Add = GoapStateBits.RadioLinkPreserved | GoapStateBits.SquadSupported | GoapStateBits.SurvivedCrisis,
                Remove = GoapStateBits.RadioLinkDrifting,
                BaseCost = 0.85f
            },
            new GoapActionDef
            {
                Id = GoapActionId.ThrowSmokeScreen,
                Required = GoapStateBits.HasSmokeGrenade | GoapStateBits.SquadHasActiveThreat,
                Forbidden = GoapStateBits.InPointBlankRange | GoapStateBits.AllyPinningThreat,
                Add = GoapStateBits.AllyPinningThreat | GoapStateBits.SurvivedCrisis | GoapStateBits.SquadSupported,
                Remove = GoapStateBits.HasSmokeGrenade,
                BaseCost = 1.45f
            },
            new GoapActionDef
            {
                Id = GoapActionId.SuppressThreatPosition,
                Required = GoapStateBits.SquadHasActiveThreat,
                Forbidden = GoapStateBits.IsBleeding | GoapStateBits.InPointBlankRange,
                Add = GoapStateBits.AllyPinningThreat | GoapStateBits.TargetPinned | GoapStateBits.SquadSupported,
                Remove = GoapStateBits.None,
                BaseCost = 1.8f
            },
            // Radioman is forbidden from volunteering for wide exposed flanking bounds so he preserves the radio link!
            new GoapActionDef
            {
                Id = GoapActionId.MoveToFlankCover,
                Required = GoapStateBits.SquadHasActiveThreat | GoapStateBits.AllyPinningThreat | GoapStateBits.FlankCoverAvailable,
                Forbidden = GoapStateBits.IsBleeding | GoapStateBits.IsMachineGunner | GoapStateBits.IsRadioman | GoapStateBits.OnBridgeOrNarrow,
                Add = GoapStateBits.InCover | GoapStateBits.InGrenadeRange | GoapStateBits.TargetEliminated,
                Remove = GoapStateBits.None,
                BaseCost = 1.6f
            },
            new GoapActionDef
            {
                Id = GoapActionId.FlushThreatWithFrag,
                Required = GoapStateBits.HasFragGrenade | GoapStateBits.InGrenadeRange,
                Forbidden = GoapStateBits.IsBleeding | GoapStateBits.IsRadioman | GoapStateBits.InPointBlankRange,
                Add = GoapStateBits.TargetEliminated | GoapStateBits.SquadSupported,
                Remove = GoapStateBits.HasFragGrenade,
                BaseCost = 1.4f
            },
            new GoapActionDef
            {
                Id = GoapActionId.HoldOverwatchFire,
                Required = GoapStateBits.CanProvideOverwatch | GoapStateBits.SquadHasActiveThreat,
                Forbidden = GoapStateBits.IsBleeding | GoapStateBits.OnBridgeOrNarrow | GoapStateBits.InPointBlankRange,
                Add = GoapStateBits.AllyPinningThreat | GoapStateBits.TargetPinned | GoapStateBits.SquadSupported,
                Remove = GoapStateBits.None,
                BaseCost = 1.85f
            },
            new GoapActionDef
            {
                Id = GoapActionId.BoundWithOverwatch,
                Required = GoapStateBits.SquadHasActiveThreat | GoapStateBits.AllyPinningThreat,
                Forbidden = GoapStateBits.IsBleeding | GoapStateBits.IsHeavilySuppressed | GoapStateBits.IsMachineGunner | GoapStateBits.IsRadioman | GoapStateBits.OnBridgeOrNarrow | GoapStateBits.InPointBlankRange,
                Add = GoapStateBits.InCover | GoapStateBits.CanFireAtVisibleEnemy | GoapStateBits.TargetEliminated | GoapStateBits.SquadSupported,
                Remove = GoapStateBits.None,
                BaseCost = 2.05f
            },
            new GoapActionDef
            {
                Id = GoapActionId.PointBlankBayonetBreach,
                Required = GoapStateBits.InPointBlankRange | GoapStateBits.HasVisibleEnemy,
                Forbidden = GoapStateBits.None,
                Add = GoapStateBits.TargetEliminated | GoapStateBits.SurvivedCrisis,
                Remove = GoapStateBits.None,
                BaseCost = 0.5f
            },
            new GoapActionDef
            {
                Id = GoapActionId.RescueSurrenderedComrade,
                Required = GoapStateBits.ComradeNeedsLiberation,
                Forbidden = GoapStateBits.IsBleeding | GoapStateBits.IsHeavilySuppressed | GoapStateBits.IsRadioman | GoapStateBits.InPointBlankRange,
                Add = GoapStateBits.SquadSupported,
                Remove = GoapStateBits.ComradeNeedsLiberation,
                BaseCost = 2.1f
            },
            new GoapActionDef
            {
                Id = GoapActionId.LoneWolfLinkUpOrAmbush,
                Required = GoapStateBits.IsEntirelyAlone,
                Forbidden = GoapStateBits.InPointBlankRange | GoapStateBits.OnBridgeOrNarrow,
                Add = GoapStateBits.InCover | GoapStateBits.SurvivedCrisis,
                Remove = GoapStateBits.IsEntirelyAlone,
                BaseCost = 1.5f
            },
            new GoapActionDef
            {
                Id = GoapActionId.DirectFireEngagement,
                Required = GoapStateBits.CanFireAtVisibleEnemy,
                Forbidden = GoapStateBits.InPointBlankRange,
                Add = GoapStateBits.TargetEliminated,
                Remove = GoapStateBits.None,
                BaseCost = 1.7f
            },
            new GoapActionDef
            {
                Id = GoapActionId.ScavengeSupplyFromCorpse,
                Required = GoapStateBits.NeedAmmoOrMedScavenge,
                Forbidden = GoapStateBits.IsBleeding | GoapStateBits.IsHeavilySuppressed | GoapStateBits.InPointBlankRange | GoapStateBits.OnBridgeOrNarrow,
                Add = GoapStateBits.SurvivedCrisis | GoapStateBits.SquadSupported,
                Remove = GoapStateBits.NeedAmmoOrMedScavenge,
                BaseCost = 1.15f
            }
        };

        internal struct BotScavengeCache
        {
            public float NextScanTime;
            public InventoryManager TargetInventory;
            public ItemObject TargetFloorItem;
            public VirtualItem TargetInventoryAtWeapon;
            public Vector3 TargetPosition;
            public bool IsAtWeaponPickup;
        }

        internal static readonly Dictionary<IntPtr, BotScavengeCache> _botScavengeCaches = new Dictionary<IntPtr, BotScavengeCache>(128);

        internal struct SquadBlackboard
        {
            public float NextRefreshTime;
            public bool IsConnectedToRadioNet;
            public bool PreviouslyHadAliveRadioman;
            public bool HasActiveThreat;
            public bool ThreatIsArmor;
            public int AliveMemberCount;
            public Vector3 SquadCenter;
            public Vector3 ThreatPosition;
            public Vector3 ThreatDirection;
            public Spottable PrimaryTarget;

            // Macro Objective & Ticket / Attrition Context
            public bool IsInvaderFaction;
            public float TicketRatio01;
            public bool HasActiveObjectiveTask;
            public bool IsDefendingObjective;
            public Vector3 ObjectivePosition;
            public float ObjectiveCaptureProgress;

            // Tier 3 Team Net Directive assigned to this Squad Leader
            public SquadNetDirective NetDirective;
            public float NetDirectiveExpireTime;
            public Vector3 NetDirectiveTargetPos;

            // Tier 2 Coordinated Squad Role Slots
            public IntPtr Suppressor0Ptr;
            public IntPtr Suppressor1Ptr;
            public float SuppressHoldUntil;

            public IntPtr FlankerLeftPtr;
            public IntPtr FlankerRightPtr;
            public float FlankUntil;
            public float NextFlankCoverScanTime;
            public CombatCover FlankLeftCover;
            public CombatCover FlankRightCover;

            public IntPtr BreacherPtr;
            public float BreachUntil;

            public Soldier SurrenderedComrade;
            public IntPtr RescuerPtr;
            public float RescueUntil;

            public bool IsThreatBeingPinned(float now)
            {
                return now < SuppressHoldUntil && (Suppressor0Ptr != IntPtr.Zero || Suppressor1Ptr != IntPtr.Zero);
            }
        }

        private static readonly Dictionary<IntPtr, float> _nextSquadVoiceTime = new Dictionary<IntPtr, float>(64);
        private static readonly Dictionary<IntPtr, float> _nextSoldierVoiceTime = new Dictionary<IntPtr, float>(256);
        private static readonly Dictionary<IntPtr, float> _nextFlankCoverAssignTime = new Dictionary<IntPtr, float>(256);
        private static readonly Dictionary<IntPtr, float> _medicHealStartTime = new Dictionary<IntPtr, float>(64);
        private static readonly Dictionary<IntPtr, IntPtr> _medicHealTargetPtr = new Dictionary<IntPtr, IntPtr>(64);
        private static readonly Dictionary<IntPtr, IntPtr> _pincerOrderedTaskBySquad = new Dictionary<IntPtr, IntPtr>(64);

        internal struct BotGoapPlan
        {
            public GoapActionId Step0;
            public GoapActionId Step1;
            public byte StepIndex;
            public byte StepCount;
            public float PlanExpireTime;
            public float NextReplanTime;
            public SoldierPose DesiredPose;
            public bool LockUnitCoverChange;
        }

        internal struct BotMicroVariance
        {
            public float AggressionCostMult;      // 0.85f .. 1.15f (lower = bolder flanking, grenade flushing, smoke pushes, CQB breaching)
            public float CautionCostMult;         // 0.85f .. 1.15f (lower = faster emergency cover seeking, wall shelter, suppression pinning, radio discipline)
            public float ReactionJitterSeconds;   // 0.00f .. 0.38f (desynchronizes squadmate replan intervals so bots never move in robotic lockstep)
            public float SuppressionJitter;       // -2.2f .. +2.2f (individual nerve variance on suppression thresholds)

            public static BotMicroVariance FromSoldier(IntPtr soldierPtr)
            {
                // Zero-allocation deterministic bit-mixing via MurmurHash3 (AiCore.GetDeterministicHash)
                int h0 = AiCore.GetDeterministicHash(soldierPtr, 901);
                int h1 = AiCore.GetDeterministicHash(soldierPtr, 902);

                float u0 = (h0 & 0xFFFF) * (1f / 65535f);
                float u1 = ((h0 >> 16) & 0x7FFF) * (1f / 32767f);
                float u2 = (h1 & 0xFFFF) * (1f / 65535f);
                float u3 = ((h1 >> 16) & 0x7FFF) * (1f / 32767f);

                return new BotMicroVariance
                {
                    AggressionCostMult = 0.85f + u0 * 0.30f,
                    CautionCostMult = 0.85f + u1 * 0.30f,
                    ReactionJitterSeconds = u2 * 0.38f,
                    SuppressionJitter = (u3 - 0.5f) * 4.4f
                };
            }
        }

        internal static readonly Dictionary<IntPtr, SquadBlackboard> _squadBlackboards = new Dictionary<IntPtr, SquadBlackboard>(64);
        internal static readonly Dictionary<IntPtr, BotGoapPlan> _botGoapPlans = new Dictionary<IntPtr, BotGoapPlan>(256);
        internal static readonly HashSet<IntPtr> _initialHqSpawnBriefingConsumed = new HashSet<IntPtr>();
        internal static readonly Dictionary<string, FactionCommandProfile> _factionProfileCache = new Dictionary<string, FactionCommandProfile>(32);

        internal struct FactionCommandProfile
        {
            public float RadioRelayRadius;            // Default 45m (USA: 55m, UK/Canada: 50m, USSR/France: 38m, Mongolia/China: 42m)
            public bool AllowDisconnectedLocalFlank;  // True for decentralized doctrines (Germany, Finland, Mongolia, Australia, NZ, Gurkha, Poland, Greece, China, Japan)
            public bool PreferAmbushWhenAlone;        // True for Japan, Finland, Gurkha, China (hold local ambush cover when alone vs retreating to merge)
            public float FlankLateralBias;            // 0.78f (tight) to 0.90f (wide encirclement like Finland/Mongolia/Japan; default 0.83f)
            public float GarrisonSquadRatio;          // Default 0.22f (UK/France/Italy: 0.28f; USSR/Japan/Mongolia: 0.16f)
            public float EmplacementSearchMult;       // Default 1.0f (Italy/France/Romania/Hungary/Greece/India: 1.18f-1.30f)
            public float SuppressionThresholdBase;    // Default 18f (USA/Manchukuo: 15f; Italy: 16f; Germany: 19f; Poland/NZ: 22f; Finland: 23f; Gurkha: 24f; Japan: 25f)
            public float SuppressCostMult;            // Germany: 0.72f, UK/India: 0.80f, France: 0.82f
            public float SmokeCostMult;               // USA: 0.70f, Canada/UK: 0.75f, Japan: 0.76f
            public float FlankMoveCostMult;           // Finland: 0.72f, Mongolia: 0.74f, USSR/Australia: 0.76f, Canada/NZ: 0.78f, Germany: 0.80f
            public float FragFlushCostMult;           // China: 0.74f, USSR/Canada: 0.78f, USA_Afrikan: 0.80f, Hungary: 0.84f
            public float OverwatchHoldCostMult;       // UK: 0.78f, Germany/USA/India: 0.82f, Italy/France: 0.84f
            public float BayonetBreachCostMult;       // Japan: 0.62f, Gurkha: 0.65f, NZ: 0.75f, Greece: 0.80f, Australia: 0.82f
            public float EmergencyCoverCostMult;      // USA: 0.82f, Manchukuo: 0.78f, China: 0.84f, Italy: 0.85f

            public static readonly FactionCommandProfile Default = new FactionCommandProfile
            {
                RadioRelayRadius = 45f,
                AllowDisconnectedLocalFlank = false,
                PreferAmbushWhenAlone = false,
                FlankLateralBias = 0.83f,
                GarrisonSquadRatio = 0.22f,
                EmplacementSearchMult = 1.0f,
                SuppressionThresholdBase = 18f,
                SuppressCostMult = 1.0f,
                SmokeCostMult = 1.0f,
                FlankMoveCostMult = 1.0f,
                FragFlushCostMult = 1.0f,
                OverwatchHoldCostMult = 1.0f,
                BayonetBreachCostMult = 1.0f,
                EmergencyCoverCostMult = 1.0f
            };
        }

        internal static FactionCommandProfile GetFactionCommandProfile(string faction)
        {
            if (!VisionConfig.EnableFactionNetDoctrines.Value || string.IsNullOrEmpty(faction))
                return FactionCommandProfile.Default;

            if (_factionProfileCache.TryGetValue(faction, out FactionCommandProfile cached))
                return cached;

            FactionCommandProfile p = FactionCommandProfile.Default;
            string f = faction;

            // Normalize Workshop/sub-faction prefixes (e.g. "Germany_Winter") to canonical switch keys
            if (f.StartsWith("UnitedStates_Afrikan", StringComparison.OrdinalIgnoreCase)) f = "UnitedStates_Afrikan";
            else if (f.StartsWith("UnitedStates", StringComparison.OrdinalIgnoreCase) || f.StartsWith("USA", StringComparison.OrdinalIgnoreCase)) f = "UnitedStates";
            else if (f.StartsWith("Germany", StringComparison.OrdinalIgnoreCase)) f = "Germany";
            else if (f.StartsWith("Finland", StringComparison.OrdinalIgnoreCase)) f = "Finland";
            else if (f.StartsWith("England", StringComparison.OrdinalIgnoreCase) || f.StartsWith("UK", StringComparison.OrdinalIgnoreCase)) f = "England";
            else if (f.StartsWith("Canada", StringComparison.OrdinalIgnoreCase)) f = "Canada";
            else if (f.StartsWith("Australia", StringComparison.OrdinalIgnoreCase)) f = "Australia";
            else if (f.StartsWith("NewZealand", StringComparison.OrdinalIgnoreCase)) f = "NewZealand";
            else if (f.StartsWith("India", StringComparison.OrdinalIgnoreCase)) f = "India";
            else if (f.StartsWith("Gurkha", StringComparison.OrdinalIgnoreCase)) f = "Gurkha";
            else if (f.StartsWith("France", StringComparison.OrdinalIgnoreCase)) f = "France";
            else if (f.StartsWith("Poland", StringComparison.OrdinalIgnoreCase)) f = "Poland";
            else if (f.StartsWith("Greece", StringComparison.OrdinalIgnoreCase)) f = "Greece";
            else if (f.StartsWith("Ussr", StringComparison.OrdinalIgnoreCase)) f = "Ussr";
            else if (f.StartsWith("Mongolia", StringComparison.OrdinalIgnoreCase)) f = "Mongolia";
            else if (f.StartsWith("China", StringComparison.OrdinalIgnoreCase)) f = "China";
            else if (f.StartsWith("Japan", StringComparison.OrdinalIgnoreCase)) f = "Japan";
            else if (f.StartsWith("Manchukuo", StringComparison.OrdinalIgnoreCase)) f = "Manchukuo";
            else if (f.StartsWith("Italy", StringComparison.OrdinalIgnoreCase)) f = "Italy";
            else if (f.StartsWith("Romania", StringComparison.OrdinalIgnoreCase)) f = "Romania";
            else if (f.StartsWith("Hungary", StringComparison.OrdinalIgnoreCase)) f = "Hungary";

            switch (f)
            {
                case "Germany":
                    // Wehrmacht / Fallschirmjager: Auftragstaktik (local initiative when cut off) + MG34/42 Base-of-Fire
                    p.RadioRelayRadius = 48f;
                    p.AllowDisconnectedLocalFlank = true;
                    p.FlankLateralBias = 0.82f;
                    p.SuppressionThresholdBase = 19.5f;
                    p.SuppressCostMult = 0.72f;
                    p.FlankMoveCostMult = 0.80f;
                    p.OverwatchHoldCostMult = 0.82f;
                    break;

                case "Finland":
                    // Finnish Army: Motti wide encirclement, autonomous forest ambush, zero reliance on HQ radio
                    p.RadioRelayRadius = 42f;
                    p.AllowDisconnectedLocalFlank = true;
                    p.PreferAmbushWhenAlone = true;
                    p.FlankLateralBias = 0.90f;
                    p.GarrisonSquadRatio = 0.18f;
                    p.SuppressionThresholdBase = 23f;
                    p.FlankMoveCostMult = 0.72f;
                    p.FragFlushCostMult = 0.82f;
                    break;

                case "UnitedStates_Afrikan":
                    // US 92nd Infantry (Italy): strong radio net + close-range flanking and grenade grit
                    p.RadioRelayRadius = 52f;
                    p.AllowDisconnectedLocalFlank = true;
                    p.SuppressionThresholdBase = 20f;
                    p.SmokeCostMult = 0.75f;
                    p.FlankMoveCostMult = 0.80f;
                    p.FragFlushCostMult = 0.80f;
                    p.OverwatchHoldCostMult = 0.84f;
                    break;

                case "UnitedStates":
                    // US Army / Marines: Superior radio net, smoke screens, combined arms, and high force preservation
                    p.RadioRelayRadius = 55f;
                    p.SuppressionThresholdBase = 15.5f;
                    p.SmokeCostMult = 0.70f;
                    p.OverwatchHoldCostMult = 0.82f;
                    p.FlankMoveCostMult = 0.85f;
                    p.EmergencyCoverCostMult = 0.82f;
                    break;

                case "England":
                    // British Army: Methodical "Bite & Hold" set-piece overwatch, smoke, and strong objective garrisoning
                    p.RadioRelayRadius = 50f;
                    p.FlankLateralBias = 0.80f;
                    p.GarrisonSquadRatio = 0.28f;
                    p.EmplacementSearchMult = 1.15f;
                    p.SuppressCostMult = 0.80f;
                    p.SmokeCostMult = 0.76f;
                    p.OverwatchHoldCostMult = 0.78f;
                    break;

                case "Canada":
                    // Canadian Army (Juno / Ortona / Scheldt): Commonwealth radio net + aggressive urban/beach shock breaching
                    p.RadioRelayRadius = 48f;
                    p.AllowDisconnectedLocalFlank = true;
                    p.SuppressionThresholdBase = 20f;
                    p.SmokeCostMult = 0.75f;
                    p.FlankMoveCostMult = 0.78f;
                    p.FragFlushCostMult = 0.78f;
                    break;

                case "Australia":
                    // Australian AIF (Tobruk / Kokoda): Autonomous bush/desert patrol flanking and close-quarters grit
                    p.RadioRelayRadius = 46f;
                    p.AllowDisconnectedLocalFlank = true;
                    p.FlankLateralBias = 0.86f;
                    p.SuppressionThresholdBase = 21.5f;
                    p.FlankMoveCostMult = 0.76f;
                    p.BayonetBreachCostMult = 0.82f;
                    break;

                case "NewZealand":
                    // 2nd NZ Division / Maori Battalion (Crete / Cassino): Fierce close-assault & counter-attack shock
                    p.RadioRelayRadius = 46f;
                    p.AllowDisconnectedLocalFlank = true;
                    p.FlankLateralBias = 0.85f;
                    p.SuppressionThresholdBase = 22f;
                    p.FlankMoveCostMult = 0.78f;
                    p.FragFlushCostMult = 0.82f;
                    p.BayonetBreachCostMult = 0.75f;
                    break;

                case "India":
                    // Indian Army: Disciplined mountain/desert overwatch and proactive emplacement defense
                    p.RadioRelayRadius = 46f;
                    p.GarrisonSquadRatio = 0.25f;
                    p.EmplacementSearchMult = 1.18f;
                    p.SuppressCostMult = 0.80f;
                    p.OverwatchHoldCostMult = 0.82f;
                    break;

                case "Gurkha":
                    // Gurkhas: Fearless close-quarters Kukri/melee shock, wide infiltration, and lone ambush resilience
                    p.RadioRelayRadius = 44f;
                    p.AllowDisconnectedLocalFlank = true;
                    p.PreferAmbushWhenAlone = true;
                    p.FlankLateralBias = 0.88f;
                    p.SuppressionThresholdBase = 24f;
                    p.FlankMoveCostMult = 0.75f;
                    p.BayonetBreachCostMult = 0.65f;
                    break;

                case "France":
                    // 1940 French Army: Bataille Conduite (Methodical top-down battle, strong emplacements, brittle if radio cut)
                    p.RadioRelayRadius = 38f;
                    p.AllowDisconnectedLocalFlank = false;
                    p.FlankLateralBias = 0.78f;
                    p.GarrisonSquadRatio = 0.28f;
                    p.EmplacementSearchMult = 1.25f;
                    p.SuppressCostMult = 0.82f;
                    p.OverwatchHoldCostMult = 0.84f;
                    break;

                case "Poland":
                    // Polish Army: Tenacious mobile defense, high cohesion when cut off, strong comrade liberation
                    p.RadioRelayRadius = 42f;
                    p.AllowDisconnectedLocalFlank = true;
                    p.EmplacementSearchMult = 1.15f;
                    p.SuppressionThresholdBase = 22f;
                    p.FlankMoveCostMult = 0.82f;
                    p.FragFlushCostMult = 0.84f;
                    break;

                case "Greece":
                    // Greek Army: Mountain fortification defense & counter-charge grit
                    p.RadioRelayRadius = 40f;
                    p.AllowDisconnectedLocalFlank = true;
                    p.GarrisonSquadRatio = 0.26f;
                    p.EmplacementSearchMult = 1.22f;
                    p.SuppressionThresholdBase = 21f;
                    p.BayonetBreachCostMult = 0.80f;
                    break;

                case "Ussr":
                    // Red Army: Deep Battle tank-infantry pincers & close SMG/grenade shock, but strictly centralized at HQ
                    p.RadioRelayRadius = 38f;
                    p.AllowDisconnectedLocalFlank = false;
                    p.FlankLateralBias = 0.86f;
                    p.GarrisonSquadRatio = 0.16f;
                    p.SuppressionThresholdBase = 20.5f;
                    p.FlankMoveCostMult = 0.76f;
                    p.FragFlushCostMult = 0.78f;
                    p.BayonetBreachCostMult = 0.84f;
                    break;

                case "Mongolia":
                    // Mongolian People's Army (Khalkhin Gol): Wide steppe mobility, autonomous recon flanking without radios
                    p.RadioRelayRadius = 40f;
                    p.AllowDisconnectedLocalFlank = true;
                    p.FlankLateralBias = 0.89f;
                    p.GarrisonSquadRatio = 0.16f;
                    p.SuppressionThresholdBase = 19.5f;
                    p.FlankMoveCostMult = 0.74f;
                    p.OverwatchHoldCostMult = 0.85f;
                    break;

                case "China":
                    // National Revolutionary Army (Shanghai / Nanking): Low radio reliance, close grenade ambushes, trench grit
                    p.RadioRelayRadius = 38f;
                    p.AllowDisconnectedLocalFlank = true;
                    p.PreferAmbushWhenAlone = true;
                    p.FlankLateralBias = 0.85f;
                    p.SuppressionThresholdBase = 21f;
                    p.FragFlushCostMult = 0.74f;
                    p.BayonetBreachCostMult = 0.78f;
                    p.EmergencyCoverCostMult = 0.84f;
                    break;

                case "Japan":
                    // Imperial Japanese Army / SNLF: Wide infiltration, smoke + bayonet shock, lone ambush to the last man
                    p.RadioRelayRadius = 42f;
                    p.AllowDisconnectedLocalFlank = true;
                    p.PreferAmbushWhenAlone = true;
                    p.FlankLateralBias = 0.88f;
                    p.GarrisonSquadRatio = 0.16f;
                    p.SuppressionThresholdBase = 25f;
                    p.SmokeCostMult = 0.76f;
                    p.FlankMoveCostMult = 0.78f;
                    p.BayonetBreachCostMult = 0.62f;
                    break;

                case "Manchukuo":
                    // Manchukuo Imperial Army (Khalkhin Gol): Conscript auxiliaries dependent on officers/radio, cautious under fire
                    p.RadioRelayRadius = 36f;
                    p.AllowDisconnectedLocalFlank = false;
                    p.PreferAmbushWhenAlone = false;
                    p.GarrisonSquadRatio = 0.26f;
                    p.SuppressionThresholdBase = 14.5f;
                    p.EmergencyCoverCostMult = 0.78f;
                    p.OverwatchHoldCostMult = 0.88f;
                    break;

                case "Italy":
                    // Regio Esercito: Strongest emplacement/artillery staffing & prepared defense, officer-dependent when isolated
                    p.RadioRelayRadius = 40f;
                    p.AllowDisconnectedLocalFlank = false;
                    p.GarrisonSquadRatio = 0.28f;
                    p.EmplacementSearchMult = 1.30f;
                    p.SuppressionThresholdBase = 16f;
                    p.OverwatchHoldCostMult = 0.84f;
                    p.EmergencyCoverCostMult = 0.85f;
                    break;

                case "Romania":
                    // Royal Romanian Army: Strong static AT/artillery gun staffing and defensive overwatch
                    p.RadioRelayRadius = 40f;
                    p.AllowDisconnectedLocalFlank = false;
                    p.GarrisonSquadRatio = 0.25f;
                    p.EmplacementSearchMult = 1.24f;
                    p.SuppressionThresholdBase = 16.5f;
                    p.SuppressCostMult = 0.84f;
                    p.EmergencyCoverCostMult = 0.86f;
                    break;

                case "Hungary":
                    // Royal Hungarian Honvedseg: Tenacious emplacement defense + close grenade counter-punches
                    p.RadioRelayRadius = 42f;
                    p.AllowDisconnectedLocalFlank = false;
                    p.GarrisonSquadRatio = 0.24f;
                    p.EmplacementSearchMult = 1.20f;
                    p.SuppressionThresholdBase = 18.5f;
                    p.SuppressCostMult = 0.84f;
                    p.FragFlushCostMult = 0.84f;
                    break;
            }

            _factionProfileCache[faction] = p;
            return p;
        }

        public static int GoapPlansExecutedCount { get; private set; }
        public static int GoapPinAndFlankCount { get; private set; }
        public static int GoapSmokeScreensCount { get; private set; }
        public static int RadioNetCutoffsCount { get; private set; }
        public static int TeamNetAnvilHammerCount { get; private set; }

        public static void ClearGoapCaches()
        {
            if (_squadBlackboards.Count > 128)
                _squadBlackboards.Clear();
            if (_botGoapPlans.Count > 512)
                _botGoapPlans.Clear();
            if (_botScavengeCaches.Count > 256)
                _botScavengeCaches.Clear();
            if (_initialHqSpawnBriefingConsumed.Count > 256)
                _initialHqSpawnBriefingConsumed.Clear();
            if (_nextSquadVoiceTime.Count > 128)
                _nextSquadVoiceTime.Clear();
            if (_nextSoldierVoiceTime.Count > 512)
                _nextSoldierVoiceTime.Clear();
            if (_nextFlankCoverAssignTime.Count > 512)
                _nextFlankCoverAssignTime.Clear();
            if (_medicHealStartTime.Count > 128)
            {
                _medicHealStartTime.Clear();
                _medicHealTargetPtr.Clear();
            }
            if (_pincerOrderedTaskBySquad.Count > 128)
                _pincerOrderedTaskBySquad.Clear();
        }

        internal static bool IsActivelyHealingTeammate(Soldier soldier, out MedicHealingDestination healDest)
        {
            healDest = null;
            if (soldier == null || soldier.targetDestination == null || soldier.targetDestination.Pointer == IntPtr.Zero)
                return false;

            healDest = soldier.targetDestination.TryCast<MedicHealingDestination>();
            if (healDest == null)
                return false;

            IntPtr medicPtr = soldier.Pointer;
            Soldier patient = healDest.soldierToHealUp;
            if (patient == null ||
                patient.Pointer == IntPtr.Zero ||
                !patient.IsAlive ||
                patient.IsOnVehicle() ||
                patient.HasSurrended() ||
                healDest.coverReached ||
                !(patient.isBleeding || patient.GetBleedValue() > 0.01f))
            {
                _medicHealStartTime.Remove(medicPtr);
                _medicHealTargetPtr.Remove(medicPtr);
                soldier.CoverPosition(null);
                healDest = null;
                return false;
            }

            // Guard against unreachable patients (e.g. different building floor/roof or 12s pathing timeout)
            float now = Time.time;
            if (!_medicHealTargetPtr.TryGetValue(medicPtr, out IntPtr prevPatientPtr) || prevPatientPtr != patient.Pointer)
            {
                _medicHealTargetPtr[medicPtr] = patient.Pointer;
                _medicHealStartTime[medicPtr] = now;
            }
            else if (_medicHealStartTime.TryGetValue(medicPtr, out float startT) && now - startT > 12.0f)
            {
                _medicHealStartTime.Remove(medicPtr);
                _medicHealTargetPtr.Remove(medicPtr);
                soldier.CoverPosition(null);
                healDest = null;
                return false;
            }

            if (soldier.transform != null && patient.transform != null)
            {
                Vector3 myPos = soldier.transform.position;
                Vector3 patPos = patient.transform.position;
                float vertDelta = Mathf.Abs(patPos.y - myPos.y);
                Vector2 flatDelta = new Vector2(patPos.x - myPos.x, patPos.z - myPos.z);
                if (vertDelta > 4.5f && flatDelta.sqrMagnitude < 12f * 12f)
                {
                    _medicHealStartTime.Remove(medicPtr);
                    _medicHealTargetPtr.Remove(medicPtr);
                    soldier.CoverPosition(null);
                    healDest = null;
                    return false;
                }
            }

            return true;
        }

        // =====================================================================
        // Audible Native Faction Voice Callouts (F.E.A.R.-Style Plan Telegraphing)
        // =====================================================================
        internal static void TrySayTacticalVoice(Soldier soldier, Squad squad, VoiceManager.VoiceClip clip, float delay = 0.1f)
        {
            if (!VisionConfig.EnableTacticalVoiceCallouts.Value ||
                soldier == null ||
                !soldier.IsAlive ||
                soldier.IsPlayer() ||
                soldier.HasSurrended() ||
                soldier.IsVoiceLocked)
            {
                return;
            }

            float now = Time.time;
            if (now < soldier.skip_say_time || now < soldier.sayDelayedEnd)
                return;

            IntPtr soldPtr = soldier.Pointer;
            if (soldPtr != IntPtr.Zero)
            {
                if (_nextSoldierVoiceTime.TryGetValue(soldPtr, out float nextSoldVoice) && now < nextSoldVoice)
                    return;
            }

            if (Photon.Pun.PhotonNetwork.InRoom && !AiCore.TryConsumeBotRpcBudget(0.55f))
                return;

            if (squad != null && squad.Pointer != IntPtr.Zero)
            {
                IntPtr sqPtr = squad.Pointer;
                if (_nextSquadVoiceTime.TryGetValue(sqPtr, out float nextSqVoice) && now < nextSqVoice)
                    return;
                _nextSquadVoiceTime[sqPtr] = now + 5.5f;
            }

            if (soldPtr != IntPtr.Zero)
            {
                _nextSoldierVoiceTime[soldPtr] = now + 9.0f;
            }

            try
            {
                soldier.Say(clip, delay, -1, null);
            }
            catch { }
        }

        // =====================================================================
        // Tier 3: Radioman-Gated Team Intel & Multi-Squad Net GOAP Planner
        // =====================================================================
        internal static bool IsMountedOnGroundVehicle(Soldier s)
        {
            if (s == null || !s.IsOnVehicle())
                return false;

            Vehicle veh = s.GetCurrentVehicle();
            if (veh == null || veh.Pointer == IntPtr.Zero || veh.IsAirVehicle() || veh.TryCast<VehicleParachute>() != null)
                return false;

            return true;
        }

        internal static bool HasLiveRadioOrRelayLink(Squad sq)
        {
            if (sq == null || sq.Pointer == IntPtr.Zero)
                return false;

            if (!VisionConfig.EnableRadiomanIntelNet.Value)
                return true;

            if (sq.RadioRequestActiveAndConfirmed())
                return true;

            Soldier leader = sq.Leader;
            if (leader == null || !leader.IsAlive || leader.transform == null)
                return false;

            // Mounted ground vehicles (Tanks, Armored Cars, Halftracks/APCs) and aircraft have onboard radios.
            // If a vehicle crew or pilot bails out on foot/parachute, they lose their vehicle radio
            // unless they link up within 45m of a friendly ground radio squad or operational ground vehicle!
            if (leader.IsOnVehicle())
            {
                Vehicle veh = leader.GetCurrentVehicle();
                if (veh != null && veh.TryCast<VehicleParachute>() == null)
                    return true;
            }

            Vector3 leaderPos = leader.transform.position;

            // 1. Direct squad Radioman within 32m of Squad Leader
            Soldier rad = sq.GetRadioman();
            if (rad != null && rad.IsAlive && !rad.HasSurrended() && rad.transform != null)
            {
                if ((rad.transform.position - leaderPos).sqrMagnitude <= 32f * 32f)
                    return true;
            }

            // 2. Within faction-specific voice/visual relay of another friendly squad with a Radioman or a friendly manned GROUND vehicle
            string myFaction = leader.faction;
            float relayRadius = GetFactionCommandProfile(myFaction).RadioRelayRadius;
            float relayRadiusSq = relayRadius * relayRadius;
            var allSquads = Squad.AllSquads;
            if (allSquads != null)
            {
                foreach (var kvp in allSquads)
                {
                    Squad other = kvp.Value;
                    if (other == null || other.Pointer == IntPtr.Zero || other.Pointer == sq.Pointer)
                        continue;

                    Soldier otherLeader = other.Leader;
                    if (otherLeader == null || !otherLeader.IsAlive || otherLeader.faction != myFaction || otherLeader.transform == null)
                        continue;

                    if ((otherLeader.transform.position - leaderPos).sqrMagnitude > relayRadiusSq)
                        continue;

                    if (IsMountedOnGroundVehicle(otherLeader))
                        return true;

                    Soldier otherRad = other.GetRadioman();
                    if (otherRad != null && otherRad.IsAlive && !otherRad.HasSurrended())
                        return true;
                }
            }

            return false;
        }

        internal static bool IsSquadConnectedToRadioNet(Squad sq)
        {
            if (sq == null || sq.Pointer == IntPtr.Zero)
                return false;

            if (HasLiveRadioOrRelayLink(sq))
                return true;

            // Newly spawned squads without a Radioman (e.g. AT squads, Sniper/Mortar/Engineer teams)
            // receive ONE initial command from the Team Net on spawn before going off-grid until they
            // physically link up within 45m of a Radio-Connected squad or friendly vehicle!
            return !_initialHqSpawnBriefingConsumed.Contains(sq.Pointer);
        }

        internal static void ConsumeInitialHqSpawnBriefingIfNoRadio(Squad sq)
        {
            if (sq != null && sq.Pointer != IntPtr.Zero && !HasLiveRadioOrRelayLink(sq))
            {
                _initialHqSpawnBriefingConsumed.Add(sq.Pointer);
            }
        }

        internal static bool CanBroadcastEnemyContactToTeamNet(Squad enemySquad, Vector3 enemyPos, string enemyFaction, bool isEnemyVehicle = false)
        {
            if (!VisionConfig.EnableRadiomanIntelNet.Value)
                return true;

            if (enemySquad != null && (enemySquad.isMarked || enemySquad.isMarkedPinPoint || enemySquad.isHalfMarked))
                return true;

            // Aerial Recon Broadcast: Friendly aircraft flying overhead have cockpit radios and can report
            // spotted enemy tanks/vehicles across the map to the Team Net!
            if (isEnemyVehicle && _airborneAircraft.Count > 0)
            {
                for (int a = 0; a < _airborneAircraft.Count; a++)
                {
                    AirborneAircraftInfo planeInfo = _airborneAircraft[a];
                    if (planeInfo.Faction != enemyFaction &&
                        planeInfo.Plane != null &&
                        planeInfo.Plane.Pointer != IntPtr.Zero &&
                        !planeInfo.Plane.isGrounded &&
                        planeInfo.Plane.CanFight() &&
                        (planeInfo.Position - enemyPos).sqrMagnitude <= 650f * 650f)
                    {
                        return true;
                    }
                }
            }

            var allSquads = Squad.AllSquads;
            if (allSquads == null)
                return true;

            foreach (var kvp in allSquads)
            {
                Squad friendlySq = kvp.Value;
                if (friendlySq == null || friendlySq.Pointer == IntPtr.Zero)
                    continue;

                Soldier fLeader = friendlySq.Leader;
                if (fLeader == null || !fLeader.IsAlive || string.IsNullOrEmpty(fLeader.faction) || fLeader.faction == enemyFaction || fLeader.transform == null)
                    continue;

                float distSq = (fLeader.transform.position - enemyPos).sqrMagnitude;
                if (distSq > 190f * 190f)
                    continue;

                if (HasLiveRadioOrRelayLink(friendlySq))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Tier 3 Team Net GOAP Planner: Treats each Faction's Radio Net as a strategic commander playing
        /// the match with whatever Infantry Squads and Combat Vehicles can hear it:
        ///   1. Multi-Squad / Combined-Arms "Anvil & Hammer": An engaged Infantry Squad OR Tank/Vehicle Anvil
        ///      pins the enemy + calls radio support while a nearby Radio-Connected Infantry Squad flanks wide (`AttackFromSide`).
        ///   2. Single-Squad "Pin & Envelop": When a connected squad's own MG gunners pin the threat and no neighbor
        ///      squad is nearby, the Squad Leader orders a local flank toward covered terrain.
        ///   3. Multi-Squad Objective Pincers: When 2+ connected squads advance on the same objective, additional
        ///      squads swing left/right (`AttackFromSide`) rather than funneling down the same center line.
        /// Disconnected squads (no Radioman and >45m from friendly forces) cannot hear the Team Net and fight on their own.
        /// </summary>
        private static readonly List<Squad> _reusableConnectedInfantrySquads = new List<Squad>(32);
        private static readonly List<Squad> _reusableActiveVehicleAnvils = new List<Squad>(16);

        internal static void RunTeamNetGoapCoordinator(float now)
        {
            if (!VisionConfig.EnableTacticalGoapPlanner.Value)
                return;

            var allSquads = Squad.AllSquads;
            if (allSquads == null || allSquads.Count < 1)
                return;

            var connectedInfantrySquads = _reusableConnectedInfantrySquads;
            var activeVehicleAnvils = _reusableActiveVehicleAnvils;
            connectedInfantrySquads.Clear();
            activeVehicleAnvils.Clear();

            foreach (var kvp in allSquads)
            {
                Squad sq = kvp.Value;
                if (sq == null || sq.Pointer == IntPtr.Zero)
                    continue;

                Soldier leader = sq.Leader;
                if (leader == null || !leader.IsAlive || leader.transform == null)
                    continue;

                // If the Human Player is the Squad Leader, never issue movement orders to their squad,
                // BUT if the Player's Squad is connected to the Radio Net and engaged in a firefight,
                // allow the Team Net to treat the Player's Squad as an Anvil and send a nearby AI Hammer squad to flank!
                if (leader.IsPlayer())
                {
                    SquadBlackboard playerBoard = GetOrUpdateSquadBlackboard(sq, leader);
                    if (playerBoard.IsConnectedToRadioNet && playerBoard.HasActiveThreat)
                    {
                        activeVehicleAnvils.Add(sq);
                    }
                    continue;
                }

                if (!AiCore.IsLocallyOwnedBot(leader))
                    continue;

                // Mounted Ground Combat Vehicles have onboard radios and can act as Heavy Anvils for nearby infantry!
                if (leader.IsOnVehicle())
                {
                    if (IsMountedOnGroundVehicle(leader))
                    {
                        SquadBlackboard vehBoard = GetOrUpdateSquadBlackboard(sq, leader);
                        if (vehBoard.HasActiveThreat)
                        {
                            activeVehicleAnvils.Add(sq);
                        }
                    }
                    continue;
                }

                SquadBlackboard board = GetOrUpdateSquadBlackboard(sq, leader);
                if (!board.IsConnectedToRadioNet)
                {
                    // Allow a squad that received a one-time spawn command (or a 45m relay command) to finish executing
                    // that directive before reverting to Disconnected_Autonomous!
                    if (now >= board.NetDirectiveExpireTime)
                    {
                        FactionCommandProfile disProfile = GetFactionCommandProfile(leader.faction);

                        // Decentralized Command Doctrines (e.g. Germany Auftragstaktik, Finland Motti, Mongolia, Australia/NZ/Gurkha,
                        // Poland, Greece, China, Japan): Squad Leader retains local tactical initiative even when cut off from HQ radio!
                        if (disProfile.AllowDisconnectedLocalFlank &&
                            board.HasActiveThreat &&
                            board.AliveMemberCount >= 4 &&
                            board.IsThreatBeingPinned(now) &&
                            (board.FlankLeftCover != null || board.FlankRightCover != null) &&
                            leader.Params != null && leader.Params.canGiveOrders && leader.Params.canMove)
                        {
                            Vector3 toThreat = board.ThreatPosition - leader.transform.position;
                            toThreat.y = 0f;
                            float dThreat = toThreat.magnitude;
                            if (dThreat >= 25f && dThreat <= 135f)
                            {
                                Vector3 axDir = toThreat / dThreat;
                                Vector3 pRight = new Vector3(axDir.z, 0f, -axDir.x);
                                float sSign = board.FlankRightCover != null ? 1f : -1f;
                                float lat = disProfile.FlankLateralBias;
                                float fwd = Mathf.Sqrt(Mathf.Max(0.1f, 1f - lat * lat));
                                Vector3 localFlankDir = (axDir * fwd - pRight * (sSign * lat)).normalized;

                                board.NetDirective = SquadNetDirective.Hammer_WideFlankEnvelopment;
                                board.NetDirectiveExpireTime = now + 15.0f;
                                board.NetDirectiveTargetPos = board.ThreatPosition;
                                _squadBlackboards[sq.Pointer] = board;

                                try
                                {
                                    sq.AttackFromSide(localFlankDir, board.ThreatPosition, 18.0f);
                                }
                                catch { }

                                TrySayTacticalVoice(leader, sq, VoiceManager.VoiceClip.attackThere, 0.2f);
                                TeamNetAnvilHammerCount++;
                                GoapPlansExecutedCount++;
                                continue;
                            }
                        }

                        board.NetDirective = SquadNetDirective.Disconnected_Autonomous;
                        _squadBlackboards[sq.Pointer] = board;

                        // If an unengaged disconnected squad (e.g. an AT squad after finishing its initial order)
                        // is within 95m of a friendly Radio-Connected squad/vehicle, move toward them to reconnect!
                        if (!board.HasActiveThreat &&
                            leader.Params != null && leader.Params.canGiveOrders && leader.Params.canMove &&
                            TryFindNearestRadioHubPosition(sq, leader.transform.position, leader.faction, 95f, out Vector3 hubPos))
                        {
                            try
                            {
                                sq.SendUnitsToCovers(hubPos, 18.0f);
                            }
                            catch { }
                        }
                    }
                    continue;
                }

                if (now >= board.NetDirectiveExpireTime)
                {
                    board.NetDirective = SquadNetDirective.Connected_StandardObjective;
                    _squadBlackboards[sq.Pointer] = board;
                }

                connectedInfantrySquads.Add(sq);
            }

            int infCount = connectedInfantrySquads.Count;
            if (infCount == 0)
                return;

            // =================================================================
            // 1A. Vehicle/Tank Anvil + Infantry Hammer (Combined-Arms Flank)
            // =================================================================
            for (int v = 0; v < activeVehicleAnvils.Count; v++)
            {
                Squad vehSq = activeVehicleAnvils[v];
                if (!_squadBlackboards.TryGetValue(vehSq.Pointer, out SquadBlackboard vehBoard) || !vehBoard.HasActiveThreat)
                    continue;

                Soldier vehLeader = vehSq.Leader;
                if (vehLeader == null || vehLeader.transform == null)
                    continue;

                Vector3 vehPos = vehLeader.transform.position;
                Vector3 threatPos = vehBoard.ThreatPosition;
                Vector3 vehToThreat = threatPos - vehPos;
                vehToThreat.y = 0f;
                float vehDist = vehToThreat.magnitude;
                if (vehDist < 25f || vehDist > 180f)
                    continue;

                Vector3 axisDir = vehToThreat / vehDist;
                Vector3 perpRight = new Vector3(axisDir.z, 0f, -axisDir.x);
                IntPtr vehTaskPtr = vehSq.currentTask != null ? vehSq.currentTask.Pointer : IntPtr.Zero;

                Squad bestHammerSq = FindBestAvailableHammerSquad(connectedInfantrySquads, -1, vehLeader.faction, vehTaskPtr, threatPos, 115f, now);
                if (bestHammerSq != null)
                {
                    IssueHammerFlankDirective(bestHammerSq, vehPos, threatPos, axisDir, perpRight, now, $"Tank/Vehicle Anvil ('{vehSq.squadName}')");
                }
            }

            // =================================================================
            // 1B. Infantry Anvil + Infantry Hammer (or Single-Squad Pin & Flank)
            // =================================================================
            for (int i = 0; i < infCount; i++)
            {
                Squad anvilSq = connectedInfantrySquads[i];
                if (!_squadBlackboards.TryGetValue(anvilSq.Pointer, out SquadBlackboard anvilBoard))
                    continue;

                if (!anvilBoard.HasActiveThreat || now < anvilBoard.NetDirectiveExpireTime)
                    continue;

                Soldier anvilLeader = anvilSq.Leader;
                if (anvilLeader == null || anvilLeader.transform == null)
                    continue;

                Vector3 anvilPos = anvilLeader.transform.position;
                Vector3 threatPos = anvilBoard.ThreatPosition;
                Vector3 anvilToThreat = threatPos - anvilPos;
                anvilToThreat.y = 0f;
                float anvilDist = anvilToThreat.magnitude;
                if (anvilDist < 28f || anvilDist > 160f)
                    continue;

                Vector3 axisDir = anvilToThreat / anvilDist;
                Vector3 perpRight = new Vector3(axisDir.z, 0f, -axisDir.x);
                IntPtr anvilTaskPtr = anvilSq.currentTask != null ? anvilSq.currentTask.Pointer : IntPtr.Zero;

                // Find best nearby Radio-Connected friendly squad within 115m on the same objective/sector to act as the HAMMER
                Squad bestHammerSq = FindBestAvailableHammerSquad(connectedInfantrySquads, i, anvilLeader.faction, anvilTaskPtr, threatPos, 115f, now);

                if (bestHammerSq != null)
                {
                    // Lock in ANVIL directive on Squad A (Pin & Call Radio Support if Radioman ready)
                    anvilBoard.NetDirective = SquadNetDirective.Anvil_PinAndCallSupport;
                    anvilBoard.NetDirectiveExpireTime = now + 24.0f;
                    anvilBoard.NetDirectiveTargetPos = threatPos;
                    _squadBlackboards[anvilSq.Pointer] = anvilBoard;
                    ConsumeInitialHqSpawnBriefingIfNoRadio(anvilSq);

                    TrySayTacticalVoice(anvilLeader, anvilSq, VoiceManager.VoiceClip.coveringFire, 0.1f);
                    TryTriggerSquadRadioSupport(anvilSq, anvilLeader);

                    // Lock in HAMMER directive on Squad B
                    IssueHammerFlankDirective(bestHammerSq, anvilPos, threatPos, axisDir, perpRight, now, $"Infantry Anvil ('{anvilSq.squadName}')");
                }
                else if (anvilBoard.AliveMemberCount >= 4 &&
                         anvilBoard.IsThreatBeingPinned(now) &&
                         (anvilBoard.FlankLeftCover != null || anvilBoard.FlankRightCover != null) &&
                         anvilLeader.Params != null && anvilLeader.Params.canGiveOrders && anvilLeader.Params.canMove)
                {
                    // Single-Squad Self-Pin & Flank: MG gunner has the enemy pinned and lateral cover exists.
                    // Note: Do NOT call anvilSq.AttackFromSide(..., threatPos, ...) because setting sq.orderPosition to threatPos
                    // (outside sq.currentTask) causes vanilla Squad.Leader_OrderAttackCurrentTask to immediately overwrite the order 250ms later!
                    anvilBoard.NetDirective = SquadNetDirective.Hammer_WideFlankEnvelopment;
                    anvilBoard.NetDirectiveExpireTime = now + 22.0f;
                    anvilBoard.NetDirectiveTargetPos = threatPos;
                    _squadBlackboards[anvilSq.Pointer] = anvilBoard;
                    ConsumeInitialHqSpawnBriefingIfNoRadio(anvilSq);

                    TrySayTacticalVoice(anvilLeader, anvilSq, VoiceManager.VoiceClip.attackThere, 0.2f);
                    TryTriggerSquadRadioSupport(anvilSq, anvilLeader);
                    TeamNetAnvilHammerCount++;
                    GoapPlansExecutedCount++;
                }
            }

            // =================================================================
            // 2. Multi-Squad Objective Pincer (When 2+ Connected Squads Push the Same Objective)
            // =================================================================
            var primarySquadByTask = new Dictionary<IntPtr, Squad>(8);
            var pincerCountByTask = new Dictionary<IntPtr, int>(8);

            for (int i = 0; i < infCount; i++)
            {
                Squad sq = connectedInfantrySquads[i];
                if (sq.currentTask == null || sq.currentTask.Pointer == IntPtr.Zero)
                    continue;

                if (_squadBlackboards.TryGetValue(sq.Pointer, out SquadBlackboard board) &&
                    (board.HasActiveThreat || now < board.NetDirectiveExpireTime))
                {
                    continue;
                }

                Soldier leader = sq.Leader;
                if (leader == null || leader.transform == null ||
                    (leader.Params != null && (!leader.Params.canGiveOrders || !leader.Params.canMove)))
                {
                    continue;
                }

                IntPtr taskPtr = sq.currentTask.Pointer;
                Vector3 taskPos = sq.currentTask.GetTaskDestination();
                Vector3 toTask = taskPos - leader.transform.position;
                toTask.y = 0f;
                float distToTask = toTask.magnitude;
                if (distToTask < 55f || distToTask > 175f)
                    continue;

                if (!primarySquadByTask.ContainsKey(taskPtr))
                {
                    primarySquadByTask[taskPtr] = sq;
                    pincerCountByTask[taskPtr] = 0;
                    ConsumeInitialHqSpawnBriefingIfNoRadio(sq);
                    continue;
                }

                int pincerIndex = pincerCountByTask[taskPtr] + 1;
                pincerCountByTask[taskPtr] = pincerIndex;

                FactionCommandProfile pincerProfile = GetFactionCommandProfile(leader.faction);
                Vector3 axisDir = toTask / distToTask;
                Vector3 perpRight = new Vector3(axisDir.z, 0f, -axisDir.x);
                float sideSign = (pincerIndex % 2 == 1) ? 1f : -1f;
                float latBias = pincerProfile.FlankLateralBias;
                float fwdBias = Mathf.Sqrt(Mathf.Max(0.1f, 1f - latBias * latBias));
                Vector3 pincerFlankDir = (axisDir * fwdBias - perpRight * (sideSign * latBias)).normalized;

                board.NetDirective = SquadNetDirective.Hammer_WideFlankEnvelopment;
                board.NetDirectiveExpireTime = now + 28.0f;
                board.NetDirectiveTargetPos = taskPos;
                _squadBlackboards[sq.Pointer] = board;
                ConsumeInitialHqSpawnBriefingIfNoRadio(sq);

                // Only issue the squad-level AttackFromSide waypoint ONCE per objective assignment so the Squad Leader
                // doesn't re-shout move orders and reset the waypoint every 18 seconds mid-march!
                if (!_pincerOrderedTaskBySquad.TryGetValue(sq.Pointer, out IntPtr prevPincerTask) || prevPincerTask != taskPtr)
                {
                    _pincerOrderedTaskBySquad[sq.Pointer] = taskPtr;
                    try
                    {
                        sq.AttackFromSide(pincerFlankDir, taskPos, 22.0f);
                    }
                    catch { }

                    TeamNetAnvilHammerCount++;
                    GoapPlansExecutedCount++;
                    if (VisionConfig.DebugLogging.Value)
                    {
                        AiCore.LogDebug(
                            "TeamNetPincer",
                            $"{leader.faction} squad '{sq.squadName}' executing objective pincer (latBias:{latBias:F2}, dist:{distToTask:F0}m).",
                            4.0f);
                    }
                }
            }
        }

        private static Squad FindBestAvailableHammerSquad(
            List<Squad> connectedInfantrySquads,
            int excludeIndex,
            string faction,
            IntPtr anvilTaskPtr,
            Vector3 threatPos,
            float maxDist,
            float now)
        {
            Squad bestHammerSq = null;
            float bestHammerDistSq = maxDist * maxDist;
            int count = connectedInfantrySquads.Count;

            for (int j = 0; j < count; j++)
            {
                if (j == excludeIndex)
                    continue;

                Squad candSq = connectedInfantrySquads[j];
                Soldier candLeader = candSq.Leader;
                if (candLeader == null || candLeader.faction != faction || candLeader.transform == null)
                    continue;

                if (candLeader.Params != null && (!candLeader.Params.canGiveOrders || !candLeader.Params.canMove))
                    continue;

                if (_squadBlackboards.TryGetValue(candSq.Pointer, out SquadBlackboard candBoard) &&
                    now < candBoard.NetDirectiveExpireTime)
                {
                    continue;
                }

                float dToThreatSq = (candLeader.transform.position - threatPos).sqrMagnitude;
                if (dToThreatSq >= bestHammerDistSq || dToThreatSq <= 22f * 22f)
                    continue;

                // Never pull a squad away from an unrelated objective across the map to flank a distant skirmish
                if (candSq.currentTask != null && candSq.currentTask.Pointer != IntPtr.Zero)
                {
                    bool sameObjective = anvilTaskPtr != IntPtr.Zero && candSq.currentTask.Pointer == anvilTaskPtr;
                    if (!sameObjective && dToThreatSq > 60f * 60f)
                    {
                        float taskToThreatSq = (candSq.currentTask.GetTaskDestination() - threatPos).sqrMagnitude;
                        if (taskToThreatSq > 85f * 85f)
                            continue;
                    }
                }

                bestHammerDistSq = dToThreatSq;
                bestHammerSq = candSq;
            }

            return bestHammerSq;
        }

        private static void TryTriggerSquadRadioSupport(Squad sq, Soldier leader)
        {
            if (sq != null &&
                leader != null &&
                sq.HasRadioman() &&
                !sq.RadioRequestActive() &&
                sq.SomeTimePassedSinceLastRadioRequest() &&
                leader.aiController != null)
            {
                try
                {
                    sq.TryAssignRadioOrder(leader.aiController, leader);
                }
                catch { }
            }
        }

        private static void IssueHammerFlankDirective(
            Squad hammerSq,
            Vector3 anvilPos,
            Vector3 threatPos,
            Vector3 axisDir,
            Vector3 perpRight,
            float now,
            string anvilLabel)
        {
            Soldier hammerLeader = hammerSq.Leader;
            if (hammerLeader == null || hammerLeader.transform == null)
                return;

            SquadBlackboard hammerBoard = GetOrUpdateSquadBlackboard(hammerSq, hammerLeader);
            hammerBoard.NetDirective = SquadNetDirective.Hammer_WideFlankEnvelopment;
            hammerBoard.NetDirectiveExpireTime = now + 24.0f;
            hammerBoard.NetDirectiveTargetPos = threatPos;
            _squadBlackboards[hammerSq.Pointer] = hammerBoard;
            ConsumeInitialHqSpawnBriefingIfNoRadio(hammerSq);

            // Note: Do NOT call hammerSq.AttackFromSide(flankDir, threatPos, 20f) here!
            // In vanilla Squad.Leader_OrderAttackCurrentTask (0x180806E8B), if sq.orderPosition is set to threatPos
            // (outside sq.currentTask), vanilla immediately overwrites the order 250ms later with a new voice callout.
            // Instead, Hammer_WideFlankEnvelopment drives individual soldier GOAP flanking & lateral cover selection!
            TrySayTacticalVoice(hammerLeader, hammerSq, VoiceManager.VoiceClip.attackThere, 0.25f);
            TeamNetAnvilHammerCount++;
            GoapPlansExecutedCount++;

            if (VisionConfig.DebugLogging.Value)
            {
                AiCore.LogDebug(
                    "TeamNetGOAP",
                    $"{hammerLeader.faction} Radio Net coordinated {anvilLabel} + Hammer wide flank ('{hammerSq.squadName}').");
            }
        }

        // =====================================================================
        // Tier 2: Squad Tactical Blackboard (Computed 1x per Squad per 0.6s)
        // =====================================================================
        internal static SquadBlackboard GetOrUpdateSquadBlackboard(Squad sq, Soldier fallbackSoldier)
        {
            if (sq == null || sq.Pointer == IntPtr.Zero)
            {
                return default;
            }

            IntPtr sqPtr = sq.Pointer;
            float now = Time.time;
            if (_squadBlackboards.TryGetValue(sqPtr, out SquadBlackboard board) && now < board.NextRefreshTime)
            {
                return board;
            }

            board.NextRefreshTime = now + 0.60f;

            Soldier leader = sq.Leader != null && sq.Leader.IsAlive ? sq.Leader : fallbackSoldier;
            if (leader == null || leader.transform == null)
            {
                _squadBlackboards[sqPtr] = board;
                return board;
            }

            Vector3 sqCenter = leader.transform.position;
            board.SquadCenter = sqCenter;

            // Check Radioman status & detect sudden loss of squad Radioman
            Soldier rad = sq.GetRadioman();
            bool hasAliveRadNow = rad != null && rad.IsAlive && !rad.HasSurrended();
            if (board.PreviouslyHadAliveRadioman && !hasAliveRadNow)
            {
                board.PreviouslyHadAliveRadioman = false;
                board.NetDirective = SquadNetDirective.Disconnected_Autonomous;
                RadioNetCutoffsCount++;
                TrySayTacticalVoice(leader, sq, VoiceManager.VoiceClip.radiomanIsDead, 0.35f);
                if (VisionConfig.DebugLogging.Value)
                {
                    AiCore.LogDebug("RadioNet", $"{leader.faction} squad '{sq.squadName}' lost its Radioman! Cut off from distant Team Net.");
                }
            }
            else if (hasAliveRadNow)
            {
                board.PreviouslyHadAliveRadioman = true;
            }

            board.IsConnectedToRadioNet = IsSquadConnectedToRadioNet(sq);
            if (!board.IsConnectedToRadioNet)
            {
                board.NetDirective = SquadNetDirective.Disconnected_Autonomous;
            }

            // Populate Macro Objective & Dynamic Operations Ticket / Attrition state
            board.IsInvaderFaction = false;
            board.TicketRatio01 = 1.0f;
            board.HasActiveObjectiveTask = false;
            board.IsDefendingObjective = false;
            board.ObjectiveCaptureProgress = 0f;

            try
            {
                BattleData bd = BattleManager.GetCurrentBattleData();
                if (bd != null && !string.IsNullOrEmpty(leader.faction))
                {
                    board.IsInvaderFaction = bd.IsInvaderFaction(leader.faction);
                    if (board.IsInvaderFaction && bd.invadersTickets > 0)
                    {
                        short remTickets = bd.GetRemainingInvaderTickets();
                        board.TicketRatio01 = Mathf.Clamp01(remTickets / Mathf.Max(40f, (float)bd.invadersTickets));
                    }
                }

                SquadTask curTask = sq.currentTask;
                if (curTask != null && curTask.Pointer != IntPtr.Zero)
                {
                    board.HasActiveObjectiveTask = true;
                    board.ObjectivePosition = curTask.GetTaskDestination();
                    board.ObjectiveCaptureProgress = Mathf.Clamp01(curTask.GetTaskCompletationPercentage());
                    board.IsDefendingObjective = _garrisonAssignedObjectiveBySquad.ContainsKey(sqPtr) || !board.IsInvaderFaction;
                }
            }
            catch { }

            // Scan squad members once to find active threat, alive count & surrendered comrades
            board.HasActiveThreat = false;
            board.ThreatIsArmor = false;
            board.PrimaryTarget = null;
            board.SurrenderedComrade = null;
            board.AliveMemberCount = 0;

            float bestThreatScore = -999f;
            var units = sq.units;
            if (units != null)
            {
                int uCount = units.Count;
                for (int i = 0; i < uCount; i++)
                {
                    Soldier u = units[i];
                    if (u == null || !u.IsAlive || u.transform == null)
                        continue;

                    if ( u.HasSurrended())
                    {
                        if (board.SurrenderedComrade == null && Mathf.Abs(u.transform.position.y - sqCenter.y) <= 2.4f)
                        {
                            board.SurrenderedComrade = u;
                        }
                        continue;
                    }

                    board.AliveMemberCount++;

                    SoldierAI uAi = u.aiController;
                    if (uAi != null && uAi.visibleTarget != null && uAi.visibleTarget.Pointer != IntPtr.Zero)
                    {
                        Spottable vt = uAi.visibleTarget;
                        Vector3 tPos = vt.GetCenterOfUnit();
                        float dist = Vector3.Distance(sqCenter, tPos);
                        bool isArmor = vt.TryCast<VehicleTank>() != null;
                        float score = 200f - dist + (isArmor ? 35f : 0f);
                        if (score > bestThreatScore)
                        {
                            bestThreatScore = score;
                            board.HasActiveThreat = true;
                            board.ThreatIsArmor = isArmor;
                            board.PrimaryTarget = vt;
                            board.ThreatPosition = tPos;
                        }
                    }
                    else if (_lastKnownTargets.TryGetValue(u.Pointer, out LastKnownTargetInfo lk) &&
                             now - lk.LastSeenTime <= 6.5f)
                    {
                        float dist = Vector3.Distance(sqCenter, lk.LastSeenPos);
                        float score = 120f - dist;
                        if (score > bestThreatScore)
                        {
                            bestThreatScore = score;
                            board.HasActiveThreat = true;
                            board.ThreatIsArmor = false;
                            board.PrimaryTarget = lk.Target;
                            board.ThreatPosition = lk.LastSeenPos;
                        }
                    }

                    // If a Machine Gunner in the squad is actively firing at a visible enemy, mark threat as pinned
                    if (uAi != null && uAi.HasEnemyAndCanFireAtHim && (u.hasMg || u.IsGunner() || u.UsingBipod()))
                    {
                        board.Suppressor0Ptr = u.Pointer;
                        if (board.SuppressHoldUntil < now + 1.5f)
                            board.SuppressHoldUntil = now + 1.5f;
                    }
                }
            }

            if (board.HasActiveThreat)
            {
                Vector3 toThreat = board.ThreatPosition - sqCenter;
                toThreat.y = 0f;
                board.ThreatDirection = toThreat.sqrMagnitude > 0.01f ? toThreat.normalized : leader.transform.forward;

                if (now >= board.NextFlankCoverScanTime ||
                    (now >= board.FlankUntil && board.FlankLeftCover == null && board.FlankRightCover == null))
                {
                    board.NextFlankCoverScanTime = now + 4.5f;
                    CombatCover prevLeft = (now < board.FlankUntil && board.FlankerLeftPtr != IntPtr.Zero) ? board.FlankLeftCover : null;
                    CombatCover prevRight = (now < board.FlankUntil && board.FlankerRightPtr != IntPtr.Zero) ? board.FlankRightCover : null;
                    FindSquadFlankCovers(sqCenter, board.ThreatPosition, board.ThreatDirection, leader.faction, out CombatCover newLeft, out CombatCover newRight);
                    board.FlankLeftCover = prevLeft != null ? prevLeft : newLeft;
                    board.FlankRightCover = prevRight != null ? prevRight : newRight;
                }
            }
            else
            {
                board.ThreatDirection = GetEffectiveThreatDirection(leader, sq, sqCenter, leader.transform.forward);
                board.FlankLeftCover = null;
                board.FlankRightCover = null;
            }

            if (now >= board.SuppressHoldUntil)
            {
                board.Suppressor0Ptr = IntPtr.Zero;
                board.Suppressor1Ptr = IntPtr.Zero;
            }
            if (now >= board.FlankUntil)
            {
                board.FlankerLeftPtr = IntPtr.Zero;
                board.FlankerRightPtr = IntPtr.Zero;
            }
            if (now >= board.BreachUntil)
            {
                board.BreacherPtr = IntPtr.Zero;
            }
            if (now >= board.RescueUntil || board.SurrenderedComrade == null || !board.SurrenderedComrade.HasSurrended())
            {
                board.RescuerPtr = IntPtr.Zero;
            }

            _squadBlackboards[sqPtr] = board;
            return board;
        }

        private static void FindSquadFlankCovers(
            Vector3 squadCenter,
            Vector3 threatPos,
            Vector3 threatDir,
            string faction,
            out CombatCover leftFlankCover,
            out CombatCover rightFlankCover)
        {
            leftFlankCover = null;
            rightFlankCover = null;

            var octree = CoverManager.staticCovers;
            if (octree == null)
                return;

            Vector3 midPoint = Vector3.Lerp(squadCenter, threatPos, 0.45f);
            var nearby = octree.GetNearby(midPoint, 22.0f);
            if (nearby == null || nearby.Length == 0)
                return;

            Vector3 rightPerp = new Vector3(threatDir.z, 0f, -threatDir.x);
            float bestLeftScore = -999f;
            float bestRightScore = -999f;

            int count = nearby.Length;
            for (int i = 0; i < count; i++)
            {
                AiDestination dest = nearby[i];
                if (dest == null || dest.Pointer == IntPtr.Zero)
                    continue;

                CombatCover cc = dest.TryCast<CombatCover>();
                if (cc == null)
                    continue;

                Vector3 covPos = cc.GetCoverPosition();
                if (Mathf.Abs(covPos.y - squadCenter.y) > 3.2f)
                    continue;

                Vector3 toThreatFromCov = threatPos - covPos;
                toThreatFromCov.y = 0f;
                float distToThreat = toThreatFromCov.magnitude;
                if (distToThreat < 6.5f || distToThreat > 45.0f)
                    continue;

                Vector3 covToThreatDir = toThreatFromCov / distToThreat;
                float angleOffAxis = Vector3.Angle(threatDir, covToThreatDir);
                if (angleOffAxis < 18f || angleOffAxis > 75f)
                    continue;

                if (!cc.IsCoverAvailable(covToThreatDir, faction))
                    continue;

                float side = Vector3.Dot(covPos - squadCenter, rightPerp);
                float score = angleOffAxis * 0.5f - Mathf.Abs(distToThreat - 18f) * 0.4f;
                if (side < -2.5f && score > bestLeftScore)
                {
                    bestLeftScore = score;
                    leftFlankCover = cc;
                }
                else if (side > 2.5f && score > bestRightScore)
                {
                    bestRightScore = score;
                    rightFlankCover = cc;
                }
            }
        }

        // =====================================================================
        // Tier 1: Individual Bot 32-Bit WorldState & Bounded 2-Step GOAP Planner
        // =====================================================================
        internal static bool HasActiveGoapCoverLock(IntPtr soldierPtr, float now)
        {
            return _botGoapPlans.TryGetValue(soldierPtr, out BotGoapPlan plan) &&
                   plan.LockUnitCoverChange &&
                   now < plan.PlanExpireTime;
        }

        internal static void ExecuteSoldierGoapTick(
            SoldierAI ai,
            Soldier soldier,
            SoldierPersonality personality,
            float vet01,
            float suppression,
            float dist,
            bool isGunner,
            bool isOnBridge,
            bool inPointBlankMelee,
            bool isEvadingArmor,
            bool isStaffingEmplacement)
        {
            if (!VisionConfig.EnableTacticalGoapPlanner.Value ||
                ai == null ||
                soldier == null ||
                soldier.IsOnVehicle() ||
                soldier.HasSurrended() ||
                soldier.IsCharging() ||
                isStaffingEmplacement)
            {
                return;
            }

            IntPtr ptr = soldier.Pointer;
            float now = Time.time;
            Squad sq = soldier.joinedSquad;
            SquadBlackboard board = GetOrUpdateSquadBlackboard(sq, soldier);

            // Build 32-Bit WorldState in O(1)
            GoapStateBits ws = BuildSoldierWorldState(
                ai,
                soldier,
                sq,
                ref board,
                personality,
                suppression,
                dist,
                isGunner,
                isOnBridge,
                inPointBlankMelee,
                isEvadingArmor,
                now);

            bool hasActivePlan = _botGoapPlans.TryGetValue(ptr, out BotGoapPlan plan) && now < plan.PlanExpireTime;
            bool emergencyPreempt =
                ((ws & GoapStateBits.InPointBlankRange) != 0 && plan.Step0 != GoapActionId.PointBlankBayonetBreach) ||
                ((ws & GoapStateBits.IsBleeding) != 0 && plan.Step0 != GoapActionId.SeekEmergencyCover && plan.Step0 != GoapActionId.BandageInCover) ||
                ((ws & GoapStateBits.RadioLinkDrifting) != 0 && plan.Step0 != GoapActionId.MaintainRadioLinkWithLeader) ||
                (isEvadingArmor && plan.Step0 != GoapActionId.SeekEmergencyCover && plan.Step0 != GoapActionId.ScavengeSupplyFromCorpse) ||
                ((ws & GoapStateBits.IsHeavilySuppressed) != 0 && (ws & GoapStateBits.InCover) == 0 && plan.Step0 != GoapActionId.SeekEmergencyCover);

            if (!hasActivePlan || emergencyPreempt || now >= plan.NextReplanTime)
            {
                GoapActionId prevActiveAction = hasActivePlan && !emergencyPreempt
                    ? (plan.StepIndex == 0 ? plan.Step0 : plan.Step1)
                    : GoapActionId.None;
                plan = PlanBestGoapActionChain(ws, soldier, ref board, personality, vet01, now, prevActiveAction);
                _botGoapPlans[ptr] = plan;
            }

            GoapActionId currentAction = plan.StepIndex == 0 ? plan.Step0 : plan.Step1;
            if (currentAction == GoapActionId.None)
                return;

            ExecuteGoapActionStep(ai, soldier, sq, ref board, ref plan, currentAction, ws, suppression, dist, now);
            _botGoapPlans[ptr] = plan;
            if (sq != null && sq.Pointer != IntPtr.Zero)
            {
                _squadBlackboards[sq.Pointer] = board;
            }
        }

        private static GoapStateBits BuildSoldierWorldState(
            SoldierAI ai,
            Soldier soldier,
            Squad sq,
            ref SquadBlackboard board,
            SoldierPersonality personality,
            float suppression,
            float dist,
            bool isGunner,
            bool isOnBridge,
            bool inPointBlankMelee,
            bool isEvadingArmor,
            float now)
        {
            GoapStateBits ws = GoapStateBits.None;
            Vector3 myPos = soldier.transform.position;
            bool isRad = soldier.IsRadioman();
            bool isBailedVehicleCrew = sq != null && sq.IsVehicleCrew && !soldier.IsOnVehicle() && !sq.SomeoneIsRepairing();

            if (soldier.isBleeding)
                ws |= GoapStateBits.IsBleeding;
            if (soldier.IsOnCover() || IsPositionShelteredByFrontCover(myPos, board.ThreatDirection, soldier.transform.root))
                ws |= GoapStateBits.InCover;

            // Radioman and bailed-out vehicle crews value self-preservation more highly (drop/seek cover at >=12 suppression),
            // while regular troops use their faction doctrine's SuppressionThresholdBase + individual BotMicroVariance!
            FactionCommandProfile factionProfile = GetFactionCommandProfile(soldier.faction);
            BotMicroVariance micro = BotMicroVariance.FromSoldier(soldier.Pointer);
            bool highSelfPreservation = isRad || isBailedVehicleCrew;
            float baseSuppThresh = highSelfPreservation
                ? 12f
                : Mathf.Max(8f, factionProfile.SuppressionThresholdBase + micro.SuppressionJitter);
            float heavySuppThresh = highSelfPreservation ? 30f : (baseSuppThresh * 2.55f);
            if (suppression >= baseSuppThresh)
                ws |= GoapStateBits.IsSuppressed;
            if (suppression >= heavySuppThresh)
                ws |= GoapStateBits.IsHeavilySuppressed;

            if (ai.visibleTarget != null)
                ws |= GoapStateBits.HasVisibleEnemy;
            if (ai.HasEnemyAndCanFireAtHim)
                ws |= GoapStateBits.CanFireAtVisibleEnemy;
            if (isGunner)
                ws |= GoapStateBits.IsMachineGunner;
            if (isOnBridge)
                ws |= GoapStateBits.OnBridgeOrNarrow;
            if (inPointBlankMelee)
                ws |= GoapStateBits.InPointBlankRange;
            if (isEvadingArmor)
                ws |= GoapStateBits.ArmorThreatClose;

            if (isRad)
            {
                ws |= GoapStateBits.IsRadioman;
                Soldier leader = sq != null ? sq.Leader : null;
                if (leader != null && leader.IsAlive && leader.Pointer != soldier.Pointer && leader.transform != null)
                {
                    float dToLeaderSq = (leader.transform.position - myPos).sqrMagnitude;
                    // If Radioman drifts > 18m from Leader OR Leader is waiting to confirm a radio request (> 5m), pull Radioman to Leader!
                    if (dToLeaderSq > 18f * 18f || (sq.RadioRequestWaiting() && dToLeaderSq > 5.0f * 5.0f))
                    {
                        ws |= GoapStateBits.RadioLinkDrifting;
                    }
                }
            }

            if (personality == SoldierPersonality.Assaulter_Shock ||
                personality == SoldierPersonality.Assaulter_Breacher ||
                personality == SoldierPersonality.Assaulter_Skirmisher)
            {
                ws |= GoapStateBits.IsAssaulterOrSkirmisher;
            }

            if (board.HasActiveThreat)
                ws |= GoapStateBits.SquadHasActiveThreat;

            if (board.IsThreatBeingPinned(now) &&
                board.Suppressor0Ptr != soldier.Pointer &&
                board.Suppressor1Ptr != soldier.Pointer)
            {
                ws |= GoapStateBits.AllyPinningThreat;
            }

            if (_lastKnownTargets.TryGetValue(soldier.Pointer, out LastKnownTargetInfo lk) &&
                now - lk.LastSeenTime <= 4.5f)
            {
                ws |= GoapStateBits.TargetLostBehindCover;
            }

            if (soldier.inventory != null && now > soldier.throw_end_time + 5.0f)
            {
                if (soldier.inventory.FindItemOfType<VirtualGrenade>() != null)
                    ws |= GoapStateBits.HasFragGrenade;
                if (soldier.inventory.FindItemOfType<VirtualSmokeGrenade>() != null)
                    ws |= GoapStateBits.HasSmokeGrenade;
            }

            if (board.HasActiveThreat)
            {
                float dToThreatSq = (board.ThreatPosition - myPos).sqrMagnitude;
                if (dToThreatSq >= 10f * 10f && dToThreatSq <= 29f * 29f)
                    ws |= GoapStateBits.InGrenadeRange;
            }

            if ((board.FlankLeftCover != null && (board.FlankerLeftPtr == IntPtr.Zero || board.FlankerLeftPtr == soldier.Pointer)) ||
                (board.FlankRightCover != null && (board.FlankerRightPtr == IntPtr.Zero || board.FlankerRightPtr == soldier.Pointer)))
            {
                ws |= GoapStateBits.FlankCoverAvailable;
            }

            if (VisionConfig.EnableSmartWallSideCover.Value &&
                !ai.IsMoving() &&
                (suppression >= 10f || board.HasActiveThreat) &&
                IsPositionOnExposedSideOfWall(myPos, board.ThreatDirection, soldier.transform.root, out _))
            {
                ws |= GoapStateBits.ExposedSideOfWall;
            }

            if (VisionConfig.EnableComradeLiberation.Value &&
                board.SurrenderedComrade != null &&
                board.SurrenderedComrade.IsAlive &&
                board.SurrenderedComrade.HasSurrended() &&
                (board.RescuerPtr == IntPtr.Zero || board.RescuerPtr == soldier.Pointer) &&
                (board.SurrenderedComrade.transform.position - myPos).sqrMagnitude <= 14.0f * 14.0f)
            {
                ws |= GoapStateBits.ComradeNeedsLiberation;
            }

            if (ai.HasEnemyAndCanFireAtHim && dist >= 16f && dist <= 140f && suppression < 42f && ai.visibleTarget != null)
            {
                Vector3 tPos = ai.visibleTarget.GetCenterOfUnit();
                if (HasClearLineOfFireFromCrouch(soldier, tPos, dist) || HasClearLineOfFireFromStanding(soldier, tPos, dist))
                {
                    ws |= GoapStateBits.CanProvideOverwatch;
                }
            }

            // Detect when the soldier is "Entirely Alone" (last survivor of squad, Leader dead and no squadmates within 35m,
            // or a bailed-out Vehicle Crew on foot whose vehicle was destroyed so retreating/linking up is cheaper per GOAP!)
            if (sq == null || board.AliveMemberCount <= 1 || isBailedVehicleCrew)
            {
                ws |= GoapStateBits.IsEntirelyAlone;
            }
            else if (sq.Leader == null || !sq.Leader.IsAlive ||
                     (!soldier.IsSquadLeader() && (sq.Leader.transform.position - myPos).sqrMagnitude > 65f * 65f))
            {
                Soldier closestAlly = sq.GetClosestSquadMember(soldier, out float closestAllyDist);
                if (closestAlly == null || closestAllyDist > 35f)
                {
                    ws |= GoapStateBits.IsEntirelyAlone;
                }
            }

            // Battlefield Scavenging:
            // 1) Out of reserve ammo for primary weapon OR out of bandages (!hasSyringe) when not fighting a visible foot enemy.
            // 2) Need an Anti-Tank weapon/AT grenade when enemy armor is threatening the squad (or bot is an AT trooper whose launcher is dry)!
            if (!soldier.isBleeding && suppression < 28f && !isOnBridge)
            {
                GenericGun heldGun = soldier.GetHeldGun();
                bool outOfAmmo = heldGun != null && soldier.IsOutOfAmmosFor(heldGun);
                bool outOfBandage = !soldier.hasSyringe;

                bool armorThreatPresent = (ws & GoapStateBits.ArmorThreatClose) != 0 ||
                                          (board.HasActiveThreat && board.PrimaryTarget != null && board.PrimaryTarget.TryCast<Vehicle>() != null) ||
                                          (ai.visibleTarget != null && ai.visibleTarget.TryCast<Vehicle>() != null);
                SoldierPersonality pers = GetPersonality(soldier);
                bool isAtRole = pers == SoldierPersonality.AT_TankHunter ||
                                pers == SoldierPersonality.AT_Ambusher ||
                                pers == SoldierPersonality.AT_Demolitionist;
                bool isMedicRole = pers == SoldierPersonality.Medic_Heroic ||
                                   pers == SoldierPersonality.Medic_Combat ||
                                   pers == SoldierPersonality.Medic_Triage;
                bool needAtWeapon = false;
                if ((armorThreatPresent || isAtRole) &&
                    !soldier.IsSquadLeader() && !soldier.IsRadioman() && !isMedicRole)
                {
                    GenericGun gun0 = soldier.GetHeldGun(0);
                    GenericGun gun1 = soldier.GetHeldGun(1);
                    bool hasUsableAtGun = (gun0 != null && gun0.IsAtWeapon() && (gun0.GetCurrentAmmoCount() > 0 || !soldier.IsOutOfAmmosFor(gun0))) ||
                                          (gun1 != null && gun1.IsAtWeapon() && (gun1.GetCurrentAmmoCount() > 0 || !soldier.IsOutOfAmmosFor(gun1)));
                    bool hasAtGrenade = soldier.inventory != null && soldier.inventory.FindItemOfType<VirtualATGrenade>() != null;
                    needAtWeapon = !hasUsableAtGun && !hasAtGrenade;
                }

                bool hasVisibleFootEnemy = ai.HasEnemyAndCanFireAtHim && (ai.visibleTarget == null || ai.visibleTarget.TryCast<Vehicle>() == null);
                if (!hasVisibleFootEnemy && (needAtWeapon || outOfAmmo || outOfBandage) &&
                    TryGetNearbyScavengeTarget(soldier, myPos, outOfAmmo, outOfBandage, needAtWeapon, now, out _))
                {
                    ws |= GoapStateBits.NeedAmmoOrMedScavenge;
                }
            }

            // Win-Condition Urgency: Defenders must contest objectives being back-capped/captured (capProgress >= 0.12f),
            // and Attackers push onto the cap circle when capture progress is >= 0.45f or tickets are critically low (<= 0.25f)!
            if (board.HasActiveObjectiveTask && sq != null && sq.currentTask != null)
            {
                float dToObjSq = (board.ObjectivePosition - myPos).sqrMagnitude;
                if (dToObjSq <= 85f * 85f && !sq.currentTask.IsInsideTaskArea(myPos, 0.85f))
                {
                    bool defenderPointBeingCapped = board.IsDefendingObjective && board.ObjectiveCaptureProgress >= 0.12f;
                    bool attackerMomentumOrLastStand = !board.IsDefendingObjective &&
                                                       (board.ObjectiveCaptureProgress >= 0.45f || board.TicketRatio01 <= 0.25f);
                    if (defenderPointBeingCapped || attackerMomentumOrLastStand)
                    {
                        ws |= GoapStateBits.ObjectiveCapContestUrgent;
                    }
                }
            }

            return ws;
        }

        private static BotGoapPlan PlanBestGoapActionChain(
            GoapStateBits ws,
            Soldier soldier,
            ref SquadBlackboard board,
            SoldierPersonality personality,
            float vet01,
            float now,
            GoapActionId prevActiveAction)
        {
            // Determine highest priority Goal bit based on 3-Tier Hierarchy:
            // 1. Immediate Crisis Survival (Bleeding, Armor close, Exposed wall side, Heavy open suppression)
            // 2. Radioman Lifeline Preservation (Keep radio link with Squad Leader alive!)
            // 3. Point-Blank CQB Self-Defense
            // 4. Lone-Wolf Survival/Link-Up (when Entirely Alone)
            // 5. Battlefield Ammo/Bandage Scavenging (when out of primary ammo or bandages out of direct LOS)
            // 6. Squad Leader & Team Net Directive (Anvil Pinning vs. Hammer Flanking vs. Local Fire & Maneuver)
            GoapStateBits desiredGoalBit;
            if ((ws & (GoapStateBits.IsBleeding | GoapStateBits.ArmorThreatClose | GoapStateBits.ExposedSideOfWall)) != 0 ||
                ((ws & GoapStateBits.IsHeavilySuppressed) != 0 && (ws & GoapStateBits.InCover) == 0))
            {
                desiredGoalBit = GoapStateBits.SurvivedCrisis;
            }
            else if ((ws & GoapStateBits.RadioLinkDrifting) != 0)
            {
                desiredGoalBit = GoapStateBits.RadioLinkPreserved;
            }
            else if ((ws & GoapStateBits.InPointBlankRange) != 0)
            {
                desiredGoalBit = GoapStateBits.TargetEliminated;
            }
            else if ((ws & GoapStateBits.IsEntirelyAlone) != 0 && (ws & GoapStateBits.InCover) == 0)
            {
                desiredGoalBit = GoapStateBits.SurvivedCrisis;
            }
            else if ((ws & GoapStateBits.NeedAmmoOrMedScavenge) != 0)
            {
                desiredGoalBit = GoapStateBits.SquadSupported;
            }
            else if ((ws & GoapStateBits.ComradeNeedsLiberation) != 0 && (ws & GoapStateBits.HasVisibleEnemy) == 0)
            {
                desiredGoalBit = GoapStateBits.SquadSupported;
            }
            else if ((ws & GoapStateBits.SquadHasActiveThreat) != 0)
            {
                bool isMedic = soldier.IsMedic() ||
                               personality == SoldierPersonality.Medic_Heroic ||
                               personality == SoldierPersonality.Medic_Combat ||
                               personality == SoldierPersonality.Medic_Triage;
                bool preferSupportOrPin =
                    (ws & GoapStateBits.ObjectiveCapContestUrgent) == 0 &&
                    (board.NetDirective == SquadNetDirective.Anvil_PinAndCallSupport ||
                     (ws & (GoapStateBits.IsMachineGunner | GoapStateBits.IsRadioman)) != 0 ||
                     isMedic ||
                     personality == SoldierPersonality.Gunner_Anchor ||
                     personality == SoldierPersonality.Gunner_Overwatch ||
                     personality == SoldierPersonality.Leader_Tactician ||
                     ((ws & GoapStateBits.AllyPinningThreat) == 0 && (ws & GoapStateBits.CanProvideOverwatch) != 0));

                desiredGoalBit = preferSupportOrPin ? GoapStateBits.SquadSupported : GoapStateBits.TargetEliminated;
            }
            else
            {
                desiredGoalBit = GoapStateBits.TargetEliminated;
            }

            GoapActionId bestStep0 = GoapActionId.None;
            GoapActionId bestStep1 = GoapActionId.None;
            float bestCost = 999f;
            FactionCommandProfile factionProfile = GetFactionCommandProfile(soldier.faction);
            BotMicroVariance micro = BotMicroVariance.FromSoldier(soldier.Pointer);

            int actionCount = _goapActions.Length;
            for (int i = 0; i < actionCount; i++)
            {
                GoapActionDef a0 = _goapActions[i];
                if ((ws & a0.Required) != a0.Required || (ws & a0.Forbidden) != 0)
                    continue;

                float cost0 = GetPersonalityAndDirectiveAdjustedCost(a0, ws, ref board, personality, vet01, ref factionProfile, ref micro);
                // Plan Commitment Hysteresis (-0.35f inertia bonus) prevents stuttering/flip-flopping between nearly-tied actions
                if (a0.Id == prevActiveAction)
                    cost0 = Mathf.Max(0.05f, cost0 - 0.35f);

                GoapStateBits wsAfter0 = (ws | a0.Add) & ~a0.Remove;

                if ((wsAfter0 & desiredGoalBit) != 0)
                {
                    if (cost0 < bestCost)
                    {
                        bestCost = cost0;
                        bestStep0 = a0.Id;
                        bestStep1 = GoapActionId.None;
                    }
                    // Allow MoveToFlankCover to also evaluate the 2-step Flank -> Frag Flush chain when carrying a frag grenade
                    if (a0.Id != GoapActionId.MoveToFlankCover || (ws & GoapStateBits.HasFragGrenade) == 0)
                        continue;
                }

                for (int j = 0; j < actionCount; j++)
                {
                    if (i == j)
                        continue;

                    GoapActionDef a1 = _goapActions[j];
                    if ((wsAfter0 & a1.Required) != a1.Required || (wsAfter0 & a1.Forbidden) != 0)
                        continue;

                    GoapStateBits wsAfter1 = (wsAfter0 | a1.Add) & ~a1.Remove;
                    if ((wsAfter1 & desiredGoalBit) == 0)
                        continue;

                    float cost1 = GetPersonalityAndDirectiveAdjustedCost(a1, wsAfter0, ref board, personality, vet01, ref factionProfile, ref micro);
                    // Synergy bonus for 2-step MoveToFlankCover -> FlushThreatWithFrag breach chain
                    if (a0.Id == GoapActionId.MoveToFlankCover && a1.Id == GoapActionId.FlushThreatWithFrag)
                        cost1 -= 1.05f;

                    float totalCost = cost0 + cost1;
                    if (totalCost < bestCost)
                    {
                        bestCost = totalCost;
                        bestStep0 = a0.Id;
                        bestStep1 = a1.Id;
                    }
                }
            }

            // Fallback Goal Bridge: if TargetEliminated had no valid path during an active squad firefight
            // (e.g. on a narrow bridge or pinned without flank cover or smoke), fall back to SuppressThreatPosition
            // so a combat soldier never stalls with GoapActionId.None!
            if (bestStep0 == GoapActionId.None && (ws & GoapStateBits.SquadHasActiveThreat) != 0 && (ws & GoapStateBits.IsBleeding) == 0)
            {
                bestStep0 = (ws & GoapStateBits.CanProvideOverwatch) != 0
                    ? GoapActionId.HoldOverwatchFire
                    : GoapActionId.SuppressThreatPosition;
            }

            return new BotGoapPlan
            {
                Step0 = bestStep0,
                Step1 = bestStep1,
                StepIndex = 0,
                StepCount = (byte)(bestStep1 != GoapActionId.None ? 2 : (bestStep0 != GoapActionId.None ? 1 : 0)),
                PlanExpireTime = now + 4.3f + micro.ReactionJitterSeconds,
                NextReplanTime = now + 1.35f + micro.ReactionJitterSeconds,
                DesiredPose = SoldierPose.Crouch,
                LockUnitCoverChange = false
            };
        }

        private static float GetPersonalityAndDirectiveAdjustedCost(
            GoapActionDef def,
            GoapStateBits ws,
            ref SquadBlackboard board,
            SoldierPersonality personality,
            float vet01,
            ref FactionCommandProfile factionProfile,
            ref BotMicroVariance micro)
        {
            float cost = def.BaseCost;
            SquadNetDirective netDirective = board.NetDirective;
            bool urgentObjPush = (ws & GoapStateBits.ObjectiveCapContestUrgent) != 0;
            bool lowTicketConservation = board.IsInvaderFaction &&
                                         board.TicketRatio01 <= 0.30f &&
                                         board.ObjectiveCaptureProgress < 0.35f &&
                                         !urgentObjPush;

            switch (def.Id)
            {
                case GoapActionId.MoveToFlankCover:
                    cost *= factionProfile.FlankMoveCostMult * micro.AggressionCostMult;
                    if (netDirective == SquadNetDirective.Hammer_WideFlankEnvelopment)
                        cost -= 0.75f;
                    if (urgentObjPush)
                        cost -= 0.55f;
                    if (personality == SoldierPersonality.Assaulter_Skirmisher ||
                        personality == SoldierPersonality.Assaulter_Shock ||
                        personality == SoldierPersonality.Assaulter_Breacher)
                        cost -= 0.65f;
                    else if (personality == SoldierPersonality.Medic_Heroic ||
                             personality == SoldierPersonality.Medic_Combat ||
                             personality == SoldierPersonality.Medic_Triage)
                        cost += 1.25f;
                    break;

                case GoapActionId.FlushThreatWithFrag:
                    cost *= factionProfile.FragFlushCostMult * micro.AggressionCostMult;
                    if (netDirective == SquadNetDirective.Hammer_WideFlankEnvelopment)
                        cost -= 0.45f;
                    if (urgentObjPush)
                        cost -= 0.50f;
                    if (personality == SoldierPersonality.Assaulter_Breacher ||
                        personality == SoldierPersonality.Rifleman_Grenadier ||
                        personality == SoldierPersonality.AT_Demolitionist)
                        cost -= 0.55f;
                    else if (personality == SoldierPersonality.Medic_Heroic ||
                             personality == SoldierPersonality.Medic_Combat ||
                             personality == SoldierPersonality.Medic_Triage)
                        cost += 0.90f;
                    break;

                case GoapActionId.ThrowSmokeScreen:
                    cost *= factionProfile.SmokeCostMult * micro.AggressionCostMult;
                    if (netDirective == SquadNetDirective.Hammer_WideFlankEnvelopment)
                        cost -= 0.45f;
                    if (urgentObjPush || lowTicketConservation)
                        cost -= 0.55f;
                    if (personality == SoldierPersonality.Assaulter_Breacher ||
                        personality == SoldierPersonality.Rifleman_Grenadier ||
                        personality == SoldierPersonality.AT_Demolitionist)
                        cost -= 0.55f;
                    break;

                case GoapActionId.SuppressThreatPosition:
                    cost *= factionProfile.SuppressCostMult * micro.CautionCostMult;
                    if (netDirective == SquadNetDirective.Anvil_PinAndCallSupport)
                        cost -= 0.80f;
                    if (lowTicketConservation)
                        cost -= 0.35f;
                    if (personality == SoldierPersonality.Gunner_Anchor ||
                        personality == SoldierPersonality.Gunner_Overwatch ||
                        personality == SoldierPersonality.Gunner_AssaultMG)
                        cost -= 0.70f;
                    break;

                case GoapActionId.HoldOverwatchFire:
                    cost *= factionProfile.OverwatchHoldCostMult * micro.CautionCostMult;
                    if (netDirective == SquadNetDirective.Anvil_PinAndCallSupport)
                        cost -= 0.70f;
                    // When objective is being back-capped or needs an overtime cap push, penalize sitting outside the cap radius!
                    if (urgentObjPush)
                        cost += 0.65f;
                    else if (lowTicketConservation)
                        cost -= 0.35f;
                    if (personality == SoldierPersonality.Gunner_Anchor ||
                        personality == SoldierPersonality.Gunner_Overwatch ||
                        personality == SoldierPersonality.Gunner_AssaultMG)
                        cost -= 0.65f;
                    break;

                case GoapActionId.BoundWithOverwatch:
                    cost *= factionProfile.OverwatchHoldCostMult * micro.AggressionCostMult;
                    if (netDirective == SquadNetDirective.Anvil_PinAndCallSupport)
                        cost -= 0.45f;
                    // Win-Condition Priority: strongly favor bounding into the cap zone when contesting or finishing an objective!
                    if (urgentObjPush)
                        cost -= 0.75f;
                    if (personality == SoldierPersonality.Gunner_Anchor ||
                        personality == SoldierPersonality.Gunner_Overwatch ||
                        personality == SoldierPersonality.Gunner_AssaultMG)
                        cost -= 0.45f;
                    break;

                case GoapActionId.PointBlankBayonetBreach:
                    cost *= factionProfile.BayonetBreachCostMult * micro.AggressionCostMult;
                    if (urgentObjPush)
                        cost -= 0.30f;
                    if (personality == SoldierPersonality.Assaulter_Shock ||
                        personality == SoldierPersonality.Assaulter_Breacher)
                        cost -= 0.35f;
                    break;

                case GoapActionId.RescueSurrenderedComrade:
                    cost *= micro.AggressionCostMult;
                    if (lowTicketConservation)
                        cost -= 0.50f;
                    if (personality == SoldierPersonality.Medic_Heroic ||
                        personality == SoldierPersonality.Leader_OldGuard)
                        cost -= 0.75f;
                    break;

                case GoapActionId.SeekEmergencyCover:
                    cost *= factionProfile.EmergencyCoverCostMult * micro.CautionCostMult;
                    if (lowTicketConservation)
                        cost -= 0.40f;
                    if (personality == SoldierPersonality.Rifleman_Cautious ||
                        personality == SoldierPersonality.Medic_Triage)
                        cost -= 0.45f;
                    break;

                case GoapActionId.RepositionBehindWall:
                case GoapActionId.BandageInCover:
                case GoapActionId.MaintainRadioLinkWithLeader:
                case GoapActionId.LoneWolfLinkUpOrAmbush:
                    cost *= micro.CautionCostMult;
                    if (lowTicketConservation)
                        cost -= 0.35f;
                    break;

                case GoapActionId.ScavengeSupplyFromCorpse:
                    cost *= micro.CautionCostMult;
                    if (lowTicketConservation)
                        cost -= 0.40f;
                    // Urgently prioritize grabbing a nearby fallen Bazooka/Panzerfaust/AT grenade when enemy armor is closing in!
                    if ((ws & GoapStateBits.ArmorThreatClose) != 0)
                        cost -= 0.70f;
                    break;

                case GoapActionId.DirectFireEngagement:
                    cost *= micro.AggressionCostMult;
                    break;
            }

            return Mathf.Max(0.1f, cost - vet01 * 0.15f);
        }

        private static void ExecuteGoapActionStep(
            SoldierAI ai,
            Soldier soldier,
            Squad sq,
            ref SquadBlackboard board,
            ref BotGoapPlan plan,
            GoapActionId action,
            GoapStateBits ws,
            float suppression,
            float dist,
            float now)
        {
            IntPtr ptr = soldier.Pointer;
            Vector3 myPos = soldier.transform.position;

            switch (action)
            {
                case GoapActionId.SeekEmergencyCover:
                    if (TrySeekNearbyCover(ai, soldier))
                    {
                        plan.LockUnitCoverChange = true;
                        SetBotPose(ai, soldier, SoldierPose.Crouch);
                        if (soldier.IsOnCover() && plan.StepCount > 1)
                        {
                            plan.StepIndex = 1;
                        }
                    }
                    break;

                case GoapActionId.RepositionBehindWall:
                    if (TryFindShelteredSideOfWall(myPos, board.ThreatDirection, soldier.transform.root, out Vector3 shelteredPos))
                    {
                        DestinationWithoutCover newDest = new DestinationWithoutCover(shelteredPos, SoldierPose.Crouch);
                        soldier.CoverPosition(newDest.Cast<AiDestination>());
                        ai.MoveDestination = shelteredPos;
                        plan.LockUnitCoverChange = true;
                        plan.PlanExpireTime = now + 4.0f;
                        plan.NextReplanTime = Mathf.Max(plan.NextReplanTime, now + 3.2f);
                        WallSideRepositionsCount++;
                        GoapPlansExecutedCount++;
                    }
                    break;

                case GoapActionId.MaintainRadioLinkWithLeader:
                    if (sq != null && sq.Leader != null && sq.Leader.IsAlive && sq.Leader.transform != null)
                    {
                        Vector3 leaderPos = sq.Leader.transform.position;
                        // Position the Radioman 3.5m behind the Squad Leader (away from the enemy threat line)
                        Vector3 safeRadioSpot = leaderPos - board.ThreatDirection * 3.5f;
                        plan.LockUnitCoverChange = true;
                        SetBotPose(ai, soldier, SoldierPose.Crouch);
                        if ((myPos - safeRadioSpot).sqrMagnitude > 4.0f * 4.0f)
                        {
                            ai.MoveDirectlyToward(safeRadioSpot, 1.25f);
                            GoapPlansExecutedCount++;
                        }
                    }
                    break;

                case GoapActionId.ThrowSmokeScreen:
                    if (soldier.inventory != null && now > soldier.throw_end_time + 5.0f && board.PrimaryTarget != null)
                    {
                        VirtualItem smokeItem = soldier.inventory.FindItemOfType<VirtualSmokeGrenade>();
                        if (smokeItem != null)
                        {
                            float dToThreat = Vector3.Distance(myPos, board.ThreatPosition);
                            if (dToThreat >= 12f && dToThreat <= soldier.GetGrenadeThrowRange(smokeItem) + 6f &&
                                !AiCore.HasLowCeilingOverhead(myPos, 3.2f) &&
                                AiCore.TryConsumeBotRpcBudget(0.45f))
                            {
                                soldier.Throw(smokeItem, board.PrimaryTarget);
                                board.Suppressor0Ptr = ptr;
                                board.SuppressHoldUntil = now + 6.5f;
                                GoapSmokeScreensCount++;
                                GoapPlansExecutedCount++;
                                TrySayTacticalVoice(soldier, sq, VoiceManager.VoiceClip.keepYourHeadDown, 0.1f);
                                if (VisionConfig.DebugLogging.Value)
                                {
                                    AiCore.LogDebug(
                                        "GoapSmoke",
                                        $"{soldier.faction} {GetPersonality(soldier)} threw GOAP smoke screen ({dToThreat:F0}m, nextStep:{plan.Step1}).",
                                        3.0f);
                                }
                                if (plan.StepCount > 1)
                                    plan.StepIndex = 1;
                            }
                        }
                    }
                    break;

                case GoapActionId.SuppressThreatPosition:
                case GoapActionId.HoldOverwatchFire:
                    if (board.Suppressor0Ptr == IntPtr.Zero || board.Suppressor0Ptr == ptr)
                        board.Suppressor0Ptr = ptr;
                    else if (board.Suppressor1Ptr == IntPtr.Zero || board.Suppressor1Ptr == ptr)
                        board.Suppressor1Ptr = ptr;

                    board.SuppressHoldUntil = Mathf.Max(board.SuppressHoldUntil, now + 3.6f);

                    if (action == GoapActionId.HoldOverwatchFire && ai.IsMoving() && ai.visibleTarget != null)
                    {
                        ai.moveCharacter = false;
                        Vector3 owTargetPos = ai.visibleTarget.GetCenterOfUnit();
                        bool crouchClear = HasClearLineOfFireFromCrouch(soldier, owTargetPos, dist);
                        SetBotPose(ai, soldier, crouchClear ? SoldierPose.Crouch : SoldierPose.Idle, true);
                        if (!_nextBoundingHoldCountTime.TryGetValue(ptr, out float nextBoundCount) || now >= nextBoundCount)
                        {
                            _nextBoundingHoldCountTime[ptr] = now + 5.0f;
                            BoundingOverwatchHoldsCount++;
                            GoapPlansExecutedCount++;
                            TrySayTacticalVoice(soldier, sq, VoiceManager.VoiceClip.coveringFire, 0.15f);
                        }
                    }
                    break;

                case GoapActionId.MoveToFlankCover:
                    {
                        CombatCover chosenCover = null;
                        if (board.FlankLeftCover != null && (board.FlankerLeftPtr == IntPtr.Zero || board.FlankerLeftPtr == ptr))
                        {
                            board.FlankerLeftPtr = ptr;
                            chosenCover = board.FlankLeftCover;
                        }
                        else if (board.FlankRightCover != null && (board.FlankerRightPtr == IntPtr.Zero || board.FlankerRightPtr == ptr))
                        {
                            board.FlankerRightPtr = ptr;
                            chosenCover = board.FlankRightCover;
                        }

                        if (chosenCover != null)
                        {
                            board.FlankUntil = Mathf.Max(board.FlankUntil, now + 6.5f);
                            plan.LockUnitCoverChange = true;
                            plan.PlanExpireTime = now + 6.5f;
                            _localCoverProtectedUntil[ptr] = now + 6.5f;

                            if (soldier.targetDestination == null || soldier.targetDestination.Pointer != chosenCover.Pointer)
                            {
                                if (!_nextFlankCoverAssignTime.TryGetValue(ptr, out float nextAssign) || now >= nextAssign)
                                {
                                    _nextFlankCoverAssignTime[ptr] = now + 5.5f;
                                    soldier.CoverPosition(chosenCover.Cast<AiDestination>());
                                    SetBotPose(ai, soldier, SoldierPose.Crouch);
                                    plan.NextReplanTime = Mathf.Max(plan.NextReplanTime, now + 4.5f);
                                    GoapPinAndFlankCount++;
                                    GoapPlansExecutedCount++;
                                    TrySayTacticalVoice(soldier, sq, VoiceManager.VoiceClip.imMoving, 0.25f);
                                    if (VisionConfig.DebugLogging.Value)
                                    {
                                        AiCore.LogDebug("GoapPinFlank", $"{soldier.faction} {GetPersonality(soldier)} flanking pinned enemy to lateral cover (nextStep:{plan.Step1}).", 3.0f);
                                    }
                                }
                            }
                            else if (soldier.IsOnCover() && plan.StepCount > 1)
                            {
                                plan.StepIndex = 1;
                            }
                        }
                    }
                    break;

                case GoapActionId.FlushThreatWithFrag:
                    if (VisionConfig.EnableLastKnownSuppression.Value)
                    {
                        board.BreacherPtr = ptr;
                        board.BreachUntil = now + 4.0f;
                        TryFlushLastKnownEnemyWithGrenade(ai, soldier);
                    }
                    break;

                case GoapActionId.BoundWithOverwatch:
                    if (ai.IsMoving())
                    {
                        SetBotPose(ai, soldier, SoldierPose.Crouch);
                    }
                    else if ((ws & GoapStateBits.ObjectiveCapContestUrgent) != 0 && board.HasActiveObjectiveTask)
                    {
                        Vector3 toObj = board.ObjectivePosition - myPos;
                        toObj.y = 0f;
                        Vector3 objDir = toObj.sqrMagnitude > 0.01f ? toObj.normalized : board.ThreatDirection;
                        TryAdvanceBoundTowardThreat(ai, soldier, sq, myPos, board.ObjectivePosition, objDir, now, ref plan);
                    }
                    else if (board.HasActiveThreat &&
                             board.IsThreatBeingPinned(now) &&
                             !ai.HasEnemyAndCanFireAtHim &&
                             (myPos - board.ThreatPosition).sqrMagnitude > 24f * 24f)
                    {
                        TryAdvanceBoundTowardThreat(ai, soldier, sq, myPos, board.ThreatPosition, board.ThreatDirection, now, ref plan);
                    }
                    break;

                case GoapActionId.ScavengeSupplyFromCorpse:
                    {
                        GenericGun heldGun = soldier.GetHeldGun();
                        bool outOfAmmo = heldGun != null && soldier.IsOutOfAmmosFor(heldGun);
                        bool outOfBandage = !soldier.hasSyringe;
                        bool armorThreatPresent = (ws & GoapStateBits.ArmorThreatClose) != 0 ||
                                                  (board.HasActiveThreat && board.PrimaryTarget != null && board.PrimaryTarget.TryCast<Vehicle>() != null) ||
                                                  (ai.visibleTarget != null && ai.visibleTarget.TryCast<Vehicle>() != null);
                        SoldierPersonality pers = GetPersonality(soldier);
                        bool isAtRole = pers == SoldierPersonality.AT_TankHunter ||
                                        pers == SoldierPersonality.AT_Ambusher ||
                                        pers == SoldierPersonality.AT_Demolitionist;
                        bool isMedicRole = pers == SoldierPersonality.Medic_Heroic ||
                                           pers == SoldierPersonality.Medic_Combat ||
                                           pers == SoldierPersonality.Medic_Triage;
                        bool needAtWeapon = (armorThreatPresent || isAtRole) &&
                                            !soldier.IsSquadLeader() && !soldier.IsRadioman() && !isMedicRole;

                        if ((needAtWeapon || outOfAmmo || outOfBandage) &&
                            TryGetNearbyScavengeTarget(soldier, myPos, outOfAmmo, outOfBandage, needAtWeapon, now, out BotScavengeCache scavCache))
                        {
                            Vector3 targetPos = scavCache.TargetPosition;
                            float dSq = (targetPos - myPos).sqrMagnitude;
                            SetBotPose(ai, soldier, SoldierPose.Crouch);
                            if (dSq <= 3.0f * 3.0f)
                            {
                                bool looted = false;
                                string lootedDesc = "supply";

                                // 1. Pick up physical floor items (dropped AT launchers, ammo boxes, magazines, firearms, syringes)
                                if (scavCache.TargetFloorItem != null && scavCache.TargetFloorItem.Pointer != IntPtr.Zero &&
                                    scavCache.TargetFloorItem.gameObject != null && scavCache.TargetFloorItem.gameObject.activeInHierarchy)
                                {
                                    GenericGun floorGun = scavCache.TargetFloorItem.TryCast<GenericGun>();
                                    if (floorGun != null)
                                    {
                                        int slot = floorGun.IsAtWeapon() ? 1 : 0;
                                        soldier.PickUpItem(scavCache.TargetFloorItem.gameObject, slot);
                                        soldier.RefillAmmoForWearedWeapon(slot, true, null, false, 2);
                                        looted = true;
                                        lootedDesc = floorGun.IsAtWeapon() ? "dropped AT weapon" : "dropped firearm";
                                    }
                                    else if (soldier.inventory != null && soldier.inventory.AddItemToInventoryAndDestroyInstance(scavCache.TargetFloorItem))
                                    {
                                        if (outOfAmmo)
                                            soldier.RefillAmmoForWearedWeapon(0, true, null, false, 2);
                                        if (outOfBandage && soldier.inventory.FindItemOfType<VirtualSyringe>() != null)
                                            soldier.hasSyringe = true;
                                        looted = true;
                                        lootedDesc = scavCache.IsAtWeaponPickup ? "floor AT grenade" : "floor ammo/supply";
                                    }
                                }
                                // 2. Scavenge from corpse inventory, supply crate, or friendly ammo carrier
                                else if (scavCache.TargetInventory != null && scavCache.TargetInventory.Pointer != IntPtr.Zero)
                                {
                                    InventoryManager targetInv = scavCache.TargetInventory;
                                    if (scavCache.IsAtWeaponPickup)
                                    {
                                        if (scavCache.TargetInventoryAtWeapon != null)
                                        {
                                            soldier.PickUpItemFromInventory(scavCache.TargetInventoryAtWeapon, targetInv, 1);
                                            soldier.RefillAmmoForWearedWeapon(1, true, null, false, 2);
                                            looted = true;
                                            lootedDesc = "AT launcher from body/crate";
                                        }
                                        else if (soldier.inventory != null)
                                        {
                                            VirtualItem atGren = targetInv.FindItemOfType<VirtualATGrenade>();
                                            if (atGren != null && soldier.inventory.TakeIntoInventory(atGren, targetInv))
                                            {
                                                looted = true;
                                                lootedDesc = "AT grenade from body/crate";
                                            }
                                        }
                                    }

                                    if (outOfBandage && soldier.inventory != null)
                                    {
                                        VirtualItem syr = targetInv.FindItemOfType<VirtualSyringe>();
                                        if (syr != null && soldier.inventory.TakeIntoInventory(syr, targetInv))
                                        {
                                            soldier.hasSyringe = true;
                                            looted = true;
                                            if (lootedDesc == "supply")
                                                lootedDesc = "bandage";
                                        }
                                    }

                                    if (outOfAmmo)
                                    {
                                        soldier.RefillAmmoForWearedWeapon(0, true, null, false, 2);
                                        looted = true;
                                        if (lootedDesc == "supply")
                                            lootedDesc = "ammo";
                                    }
                                }

                                _botScavengeCaches.Remove(ptr);
                                if (looted)
                                {
                                    GoapPlansExecutedCount++;
                                    if (VisionConfig.DebugLogging.Value)
                                    {
                                        AiCore.LogDebug(
                                            "GoapScavenge",
                                            $"{soldier.faction} {GetPersonality(soldier)} scavenged {lootedDesc}.",
                                            3.0f);
                                    }
                                }
                            }
                            else
                            {
                                plan.LockUnitCoverChange = true;
                                ai.MoveDirectlyToward(targetPos, 1.35f);
                            }
                        }
                    }
                    break;

                case GoapActionId.RescueSurrenderedComrade:
                    if (board.SurrenderedComrade != null && board.SurrenderedComrade.IsAlive && board.SurrenderedComrade.HasSurrended())
                    {
                        board.RescuerPtr = ptr;
                        board.RescueUntil = now + 4.0f;
                        Soldier targetSurr = board.SurrenderedComrade;
                        float dSq = (targetSurr.transform.position - myPos).sqrMagnitude;
                        if (dSq <= 3.8f * 3.8f)
                        {
                            _liberateMoveStartTime.Remove(ptr);
                            if (AiCore.TryConsumeBotRpcBudget(0.45f))
                            {
                                string sqId = sq != null ? sq.InstanceID.ToString() : "";
                                targetSurr.UnSurrenderSynched(sqId);
                                ComradesLiberated++;
                                GoapPlansExecutedCount++;
                                AddCombatExperienceBonus(soldier, 30f);
                            }
                        }
                        else if (dSq <= 11.0f * 11.0f && !soldier.IsSquadLeader() && !soldier.IsOnCover() && !ai.HasEnemyAndCanFireAtHim)
                        {
                            if (!_liberateMoveCooldownUntil.TryGetValue(ptr, out float cdUntil) || now >= cdUntil)
                            {
                                if (!_liberateMoveStartTime.TryGetValue(ptr, out float startT))
                                {
                                    _liberateMoveStartTime[ptr] = now;
                                }
                                else if (now - startT > 5.5f)
                                {
                                    _liberateMoveStartTime.Remove(ptr);
                                    _liberateMoveCooldownUntil[ptr] = now + 20.0f;
                                    break;
                                }
                                ai.MoveDirectlyToward(targetSurr.transform.position, 1.35f);
                            }
                        }
                    }
                    break;

                case GoapActionId.LoneWolfLinkUpOrAmbush:
                    // Entirely alone: ambush-oriented doctrines (Japan, Finland, Gurkha, China) on foot prefer holding
                    // local ambush cover first, whereas bailed-out vehicle crews and standard doctrines prioritize
                    // retreating to link up and merge into the nearest friendly infantry squad within 75m!
                    bool isBailedCrew = sq != null && sq.IsVehicleCrew;
                    bool preferAmbush = !isBailedCrew && GetFactionCommandProfile(soldier.faction).PreferAmbushWhenAlone;
                    if (preferAmbush)
                    {
                        if (!soldier.IsOnCover() && TrySeekNearbyCover(ai, soldier))
                        {
                            plan.LockUnitCoverChange = true;
                            SetBotPose(ai, soldier, SoldierPose.Crouch);
                        }
                        else if (TryLinkUpOrJoinFriendlySquad(ai, soldier, myPos, 55f))
                        {
                            SetBotPose(ai, soldier, SoldierPose.Crouch);
                        }
                    }
                    else
                    {
                        if (TryLinkUpOrJoinFriendlySquad(ai, soldier, myPos, 75f))
                        {
                            SetBotPose(ai, soldier, SoldierPose.Crouch);
                        }
                        else if (!soldier.IsOnCover() && TrySeekNearbyCover(ai, soldier))
                        {
                            plan.LockUnitCoverChange = true;
                        }
                    }
                    break;
            }
        }

        private static void TryAdvanceBoundTowardThreat(
            SoldierAI ai,
            Soldier soldier,
            Squad sq,
            Vector3 myPos,
            Vector3 threatPos,
            Vector3 threatDir,
            float now,
            ref BotGoapPlan plan)
        {
            IntPtr ptr = soldier.Pointer;
            if (_nextLocalCoverSeekTime.TryGetValue(ptr, out float nextSeek) && now < nextSeek)
                return;

            _nextLocalCoverSeekTime[ptr] = now + 3.0f;

            var octree = CoverManager.staticCovers;
            if (octree == null)
                return;

            Vector3 searchCenter = myPos + threatDir * 12.0f;
            var nearby = octree.GetNearby(searchCenter, 14.0f);
            if (nearby == null || nearby.Length == 0)
                return;

            float myDistToThreatSq = (threatPos - myPos).sqrMagnitude;
            var unreachable = soldier.nonReachableCovers;
            CombatCover bestAdvanceCover = null;
            float bestScore = -999f;

            int count = nearby.Length;
            for (int i = 0; i < count; i++)
            {
                AiDestination dest = nearby[i];
                if (dest == null || dest.Pointer == IntPtr.Zero)
                    continue;
                if (unreachable != null && unreachable.Contains(dest))
                    continue;

                CombatCover cc = dest.TryCast<CombatCover>();
                if (cc == null)
                    continue;

                Vector3 covPos = cc.GetCoverPosition();
                if (Mathf.Abs(covPos.y - myPos.y) > 2.8f)
                    continue;

                float dFromMeSq = (covPos - myPos).sqrMagnitude;
                if (dFromMeSq < 5.5f * 5.5f || dFromMeSq > 22.0f * 22.0f)
                    continue;

                float covDistToThreatSq = (threatPos - covPos).sqrMagnitude;
                if (covDistToThreatSq >= myDistToThreatSq - 25.0f || covDistToThreatSq < 8.0f * 8.0f)
                    continue;

                if (!cc.IsCoverAvailable(threatDir, soldier.faction))
                    continue;

                float advanceGain = Vector3.Dot(covPos - myPos, threatDir);
                if (advanceGain < 4.5f)
                    continue;

                float score = advanceGain - Mathf.Sqrt(dFromMeSq) * 0.25f;
                if (score > bestScore)
                {
                    bestScore = score;
                    bestAdvanceCover = cc;
                }
            }

            if (bestAdvanceCover != null)
            {
                soldier.CoverPosition(bestAdvanceCover.Cast<AiDestination>());
                SetBotPose(ai, soldier, SoldierPose.Crouch);
                plan.LockUnitCoverChange = true;
                plan.PlanExpireTime = now + 5.5f;
                plan.NextReplanTime = Mathf.Max(plan.NextReplanTime, now + 3.6f);
                _localCoverProtectedUntil[ptr] = now + 5.5f;
                GoapPlansExecutedCount++;
                TrySayTacticalVoice(soldier, sq, VoiceManager.VoiceClip.imMoving, 0.20f);
            }
        }

        private static bool TryGetNearbyScavengeTarget(
            Soldier soldier,
            Vector3 myPos,
            bool needAmmo,
            bool needBandage,
            bool needAtWeapon,
            float now,
            out BotScavengeCache result)
        {
            IntPtr ptr = soldier.Pointer;
            if (_botScavengeCaches.TryGetValue(ptr, out result) && now < result.NextScanTime)
            {
                if (result.TargetFloorItem != null && result.TargetFloorItem.Pointer != IntPtr.Zero &&
                    result.TargetFloorItem.gameObject != null && result.TargetFloorItem.gameObject.activeInHierarchy)
                {
                    result.TargetPosition = result.TargetFloorItem.transform.position;
                    return true;
                }
                if (result.TargetInventory != null && result.TargetInventory.Pointer != IntPtr.Zero)
                {
                    result.TargetPosition = result.TargetInventory.InventoryPosition;
                    return true;
                }
                return false;
            }

            result = new BotScavengeCache { NextScanTime = now + 2.5f };
            float bestDistSq = (needAtWeapon ? 18.0f : 15.0f) * (needAtWeapon ? 18.0f : 15.0f);
            GenericGun heldGun = soldier.GetHeldGun();
            bool isPrimaryCompletelyDry = needAmmo && heldGun != null && heldGun.GetCurrentAmmoCount() <= 0 && !heldGun.IsAtWeapon();

            // 1. Scan physical items dropped on the floor (ItemObject.spawnedItems)
            var floorItems = ItemObject.spawnedItems;
            if (floorItems != null)
            {
                int fCount = floorItems.Count;
                for (int i = 0; i < fCount; i++)
                {
                    ItemObject item = floorItems[i];
                    if (item == null || item.Pointer == IntPtr.Zero || item.gameObject == null || !item.gameObject.activeInHierarchy || item.transform == null)
                        continue;

                    Vector3 itemPos = item.transform.position;
                    if (Mathf.Abs(itemPos.y - myPos.y) > 2.4f)
                        continue;

                    float dSq = (itemPos - myPos).sqrMagnitude;
                    if (dSq >= bestDistSq)
                        continue;

                    GenericGun floorGun = item.TryCast<GenericGun>();
                    if (floorGun != null)
                    {
                        if (needAtWeapon && floorGun.IsAtWeapon() && floorGun.GetCurrentAmmoCount() > 0)
                        {
                            bestDistSq = dSq;
                            result.TargetFloorItem = item;
                            result.TargetInventory = null;
                            result.TargetPosition = itemPos;
                            result.IsAtWeaponPickup = true;
                            continue;
                        }
                        if (isPrimaryCompletelyDry && !floorGun.IsAtWeapon() && floorGun.GetCurrentAmmoCount() > 0)
                        {
                            bestDistSq = dSq;
                            result.TargetFloorItem = item;
                            result.TargetInventory = null;
                            result.TargetPosition = itemPos;
                            result.IsAtWeaponPickup = false;
                            continue;
                        }
                        continue;
                    }

                    if (needAmmo && item.TryCast<AmmoItem>() != null)
                    {
                        bestDistSq = dSq;
                        result.TargetFloorItem = item;
                        result.TargetInventory = null;
                        result.TargetPosition = itemPos;
                        result.IsAtWeaponPickup = false;
                        continue;
                    }

                    VirtualItem vi = item.ToVirtualItem();
                    if (vi == null)
                        continue;

                    if (needAtWeapon && vi.TryCast<VirtualATGrenade>() != null)
                    {
                        bestDistSq = dSq;
                        result.TargetFloorItem = item;
                        result.TargetInventory = null;
                        result.TargetPosition = itemPos;
                        result.IsAtWeaponPickup = true;
                    }
                    else if (needAmmo && vi.TryCast<VirtualMagazineItem>() != null)
                    {
                        bestDistSq = dSq;
                        result.TargetFloorItem = item;
                        result.TargetInventory = null;
                        result.TargetPosition = itemPos;
                        result.IsAtWeaponPickup = false;
                    }
                    else if (needBandage && (vi.TryCast<VirtualSyringe>() != null || vi.TryCast<VirtualBandages>() != null))
                    {
                        bestDistSq = dSq;
                        result.TargetFloorItem = item;
                        result.TargetInventory = null;
                        result.TargetPosition = itemPos;
                        result.IsAtWeaponPickup = false;
                    }
                }
            }

            // 2. Scan nearby corpse inventories, supply crates, and friendly ammo carriers (InventoryManager.activeInventories)
            var activeList = InventoryManager.activeInventories;
            if (activeList != null)
            {
                IntPtr myInvPtr = soldier.inventory != null ? soldier.inventory.Pointer : IntPtr.Zero;
                int count = activeList.Count;

                for (int i = 0; i < count; i++)
                {
                    InventoryManager inv = activeList[i];
                    if (inv == null || inv.Pointer == IntPtr.Zero || inv.Pointer == myInvPtr || inv.IsPlayersInventory())
                        continue;

                    Vector3 invPos = inv.InventoryPosition;
                    if (Mathf.Abs(invPos.y - myPos.y) > 2.4f)
                        continue;

                    float dSq = (invPos - myPos).sqrMagnitude;
                    if (dSq >= bestDistSq)
                        continue;

                    Soldier owner = inv.GetComponent<Soldier>();
                    if (owner != null && owner.IsAlive && (!needAmmo || !owner.IsAmmoBoxCarrier() || owner.faction != soldier.faction))
                        continue;

                    if (needAtWeapon && (owner == null || !owner.IsAlive))
                    {
                        VirtualItem atGren = inv.FindItemOfType<VirtualATGrenade>();
                        if (atGren != null)
                        {
                            bestDistSq = dSq;
                            result.TargetFloorItem = null;
                            result.TargetInventory = inv;
                            result.TargetInventoryAtWeapon = null;
                            result.TargetPosition = invPos;
                            result.IsAtWeaponPickup = true;
                            continue;
                        }

                        VirtualItem vw = inv.FindItemOfType<VirtualWeapon>();
                        if (vw != null)
                        {
                            ItemObject prefab = vw.GetItemPrefab();
                            GenericGun prefabGun = prefab != null ? prefab.TryCast<GenericGun>() : null;
                            if (prefabGun != null && prefabGun.IsAtWeapon())
                            {
                                bestDistSq = dSq;
                                result.TargetFloorItem = null;
                                result.TargetInventory = inv;
                                result.TargetInventoryAtWeapon = vw;
                                result.TargetPosition = invPos;
                                result.IsAtWeaponPickup = true;
                                continue;
                            }
                        }
                    }

                    if (!needAmmo && !needBandage)
                        continue;

                    if (needBandage && !needAmmo && inv.FindItemOfType<VirtualSyringe>() == null)
                        continue;

                    bestDistSq = dSq;
                    result.TargetFloorItem = null;
                    result.TargetInventory = inv;
                    result.TargetInventoryAtWeapon = null;
                    result.TargetPosition = invPos;
                    result.IsAtWeaponPickup = false;
                }
            }

            _botScavengeCaches[ptr] = result;
            return result.TargetFloorItem != null || result.TargetInventory != null;
        }

        private static bool TryLinkUpOrJoinFriendlySquad(SoldierAI ai, Soldier self, Vector3 myPos, float maxRadius)
        {
            var allSquads = Squad.AllSquads;
            if (allSquads == null)
                return false;

            float bestSq = maxRadius * maxRadius;
            Squad bestSquad = null;
            Vector3 linkUpPos = Vector3.zero;
            string myFaction = self.faction;
            IntPtr mySqPtr = self.joinedSquad != null ? self.joinedSquad.Pointer : IntPtr.Zero;

            foreach (var kvp in allSquads)
            {
                Squad other = kvp.Value;
                if (other == null || other.Pointer == IntPtr.Zero || other.Pointer == mySqPtr || other.IsVehicleCrew || other.IsPlayerInSquad())
                    continue;

                Soldier leader = other.Leader;
                if (leader == null || !leader.IsAlive || leader.IsOnVehicle() || leader.faction != myFaction || leader.transform == null)
                    continue;

                float dSq = (leader.transform.position - myPos).sqrMagnitude;
                if (dSq < bestSq)
                {
                    bestSq = dSq;
                    bestSquad = other;
                    linkUpPos = leader.transform.position;
                }
            }

            if (bestSquad == null)
                return false;

            // If within 18m of the friendly Squad Leader, pull the lone soldier directly into the squad!
            if (bestSq <= 18f * 18f)
            {
                try
                {
                    if (bestSquad.units != null && bestSquad.units.Count < 10 && bestSquad.AddIfNotInSquad(self))
                    {
                        GoapPlansExecutedCount++;
                        if (VisionConfig.DebugLogging.Value)
                        {
                            AiCore.LogDebug(
                                "GoapMerge",
                                $"{myFaction} lone survivor / bailed crewman merged into friendly squad '{bestSquad.squadName}'.",
                                3.0f);
                        }
                        return true;
                    }
                }
                catch { }
            }

            if (bestSq > 8f * 8f && ai != null)
            {
                ai.MoveDirectlyToward(linkUpPos, 1.5f);
                return true;
            }

            return false;
        }

        private static bool TryFindNearestRadioHubPosition(
            Squad selfSq,
            Vector3 myPos,
            string myFaction,
            float maxRadius,
            out Vector3 hubPos)
        {
            hubPos = Vector3.zero;
            var allSquads = Squad.AllSquads;
            if (allSquads == null || selfSq == null)
                return false;

            float bestSq = maxRadius * maxRadius;
            bool found = false;

            foreach (var kvp in allSquads)
            {
                Squad other = kvp.Value;
                if (other == null || other.Pointer == IntPtr.Zero || other.Pointer == selfSq.Pointer)
                    continue;

                Soldier otherLeader = other.Leader;
                if (otherLeader == null || !otherLeader.IsAlive || otherLeader.faction != myFaction || otherLeader.transform == null)
                    continue;

                bool isHub = IsMountedOnGroundVehicle(otherLeader) || other.IsPlayerInSquad();
                if (!isHub)
                {
                    Soldier rad = other.GetRadioman();
                    isHub = rad != null && rad.IsAlive && !rad.HasSurrended();
                }

                if (!isHub)
                    continue;

                float dSq = (otherLeader.transform.position - myPos).sqrMagnitude;
                if (dSq < bestSq && dSq > 45f * 45f)
                {
                    bestSq = dSq;
                    hubPos = otherLeader.transform.position;
                    found = true;
                }
            }

            return found;
        }
    }
}
