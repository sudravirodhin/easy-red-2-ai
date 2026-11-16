using System;
using System.Collections.Generic;
using HarmonyLib;
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
        internal enum GoapStateBits : ulong
        {
            None                      = 0UL,
            IsBleeding                = 1UL << 0,
            InCover                   = 1UL << 1,
            IsSuppressed              = 1UL << 2,
            IsHeavilySuppressed       = 1UL << 3,
            HasVisibleEnemy           = 1UL << 4,
            CanFireAtVisibleEnemy     = 1UL << 5,
            TargetLostBehindCover     = 1UL << 6,
            SquadHasActiveThreat      = 1UL << 7,
            AllyPinningThreat         = 1UL << 8,
            ArmorThreatClose          = 1UL << 9,
            HasFragGrenade            = 1UL << 10,
            HasSmokeGrenade           = 1UL << 11,
            InGrenadeRange            = 1UL << 12,
            InPointBlankRange         = 1UL << 13,
            FlankCoverAvailable       = 1UL << 14,
            ExposedSideOfWall         = 1UL << 15,
            ComradeNeedsLiberation    = 1UL << 16,
            IsMachineGunner           = 1UL << 17,
            IsAssaulterOrSkirmisher   = 1UL << 18,
            IsRadioman                = 1UL << 19,
            RadioLinkDrifting         = 1UL << 20,
            IsEntirelyAlone           = 1UL << 21,
            OnBridgeOrNarrow          = 1UL << 22,
            CanProvideOverwatch       = 1UL << 23,
            NeedAmmoOrMedScavenge     = 1UL << 24,
            ObjectiveCapContestUrgent = 1UL << 25,

            // Goal & Chain Effect Bits
            SurvivedCrisis            = 1UL << 26,
            RadioLinkPreserved        = 1UL << 27,
            TargetPinned              = 1UL << 28,
            TargetEliminated          = 1UL << 29,
            SquadSupported            = 1UL << 30,
            HasBandage                = 1UL << 31,
            OverwhelmedNeedFallback   = 1UL << 32,
            NearFreshCasualty         = 1UL << 33,
            BandageUnsafeUnderFire    = 1UL << 34
        }

        internal enum SquadNetDirective : byte
        {
            Disconnected_Autonomous = 0,
            Connected_StandardObjective = 1,
            Anvil_PinAndCallSupport = 2,
            Hammer_WideFlankEnvelopment = 3,
            Regroup_SectorWithdrawal = 4
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
            ScavengeSupplyFromCorpse = 15,
            TacticalFallbackRetreat = 16
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
                Forbidden = GoapStateBits.InCover,
                Add = GoapStateBits.InCover | GoapStateBits.SurvivedCrisis,
                Remove = GoapStateBits.ExposedSideOfWall,
                BaseCost = 1.2f
            },
            new GoapActionDef
            {
                Id = GoapActionId.BandageInCover,
                Required = GoapStateBits.IsBleeding | GoapStateBits.HasBandage | GoapStateBits.InCover,
                Forbidden = GoapStateBits.InPointBlankRange | GoapStateBits.ExposedSideOfWall | GoapStateBits.ArmorThreatClose | GoapStateBits.BandageUnsafeUnderFire,
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
                Forbidden = GoapStateBits.IsBleeding | GoapStateBits.InPointBlankRange | GoapStateBits.CanProvideOverwatch,
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
                Add = GoapStateBits.InCover | GoapStateBits.InGrenadeRange | GoapStateBits.TargetLostBehindCover | GoapStateBits.TargetEliminated,
                Remove = GoapStateBits.None,
                BaseCost = 1.6f
            },
            new GoapActionDef
            {
                Id = GoapActionId.FlushThreatWithFrag,
                Required = GoapStateBits.HasFragGrenade | GoapStateBits.InGrenadeRange | GoapStateBits.TargetLostBehindCover,
                Forbidden = GoapStateBits.IsBleeding | GoapStateBits.IsRadioman | GoapStateBits.InPointBlankRange,
                Add = GoapStateBits.TargetEliminated | GoapStateBits.SquadSupported,
                Remove = GoapStateBits.HasFragGrenade,
                BaseCost = 1.4f
            },
            new GoapActionDef
            {
                Id = GoapActionId.HoldOverwatchFire,
                Required = GoapStateBits.CanProvideOverwatch | GoapStateBits.SquadHasActiveThreat,
                Forbidden = GoapStateBits.IsBleeding | GoapStateBits.InPointBlankRange,
                Add = GoapStateBits.AllyPinningThreat | GoapStateBits.TargetPinned | GoapStateBits.SquadSupported,
                Remove = GoapStateBits.None,
                BaseCost = 1.55f
            },
            new GoapActionDef
            {
                Id = GoapActionId.BoundWithOverwatch,
                Required = GoapStateBits.SquadHasActiveThreat | GoapStateBits.AllyPinningThreat,
                Forbidden = GoapStateBits.IsBleeding | GoapStateBits.IsHeavilySuppressed | GoapStateBits.IsMachineGunner | GoapStateBits.IsRadioman | GoapStateBits.InPointBlankRange,
                Add = GoapStateBits.InCover | GoapStateBits.CanFireAtVisibleEnemy | GoapStateBits.TargetEliminated,
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
                Forbidden = GoapStateBits.IsBleeding | GoapStateBits.InPointBlankRange | GoapStateBits.CanFireAtVisibleEnemy,
                Add = GoapStateBits.InCover | GoapStateBits.SurvivedCrisis | GoapStateBits.SquadSupported,
                Remove = GoapStateBits.IsEntirelyAlone,
                BaseCost = 1.35f
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
                Forbidden = GoapStateBits.IsHeavilySuppressed | GoapStateBits.InPointBlankRange | GoapStateBits.OnBridgeOrNarrow,
                Add = GoapStateBits.HasBandage | GoapStateBits.SurvivedCrisis | GoapStateBits.SquadSupported,
                Remove = GoapStateBits.NeedAmmoOrMedScavenge,
                BaseCost = 1.15f
            },
            new GoapActionDef
            {
                Id = GoapActionId.TacticalFallbackRetreat,
                Required = GoapStateBits.OverwhelmedNeedFallback,
                Forbidden = GoapStateBits.IsBleeding | GoapStateBits.InPointBlankRange | GoapStateBits.OnBridgeOrNarrow,
                Add = GoapStateBits.InCover | GoapStateBits.SurvivedCrisis | GoapStateBits.SquadSupported,
                Remove = GoapStateBits.OverwhelmedNeedFallback | GoapStateBits.ArmorThreatClose | GoapStateBits.ExposedSideOfWall,
                BaseCost = 0.88f
            }
        };

        internal struct SquadBlackboard
        {
            public float NextRefreshTime;
            public bool IsConnectedToRadioNet;
            public bool PreviouslyHadAliveRadioman;
            public float LastRadiomanLostTime;
            public byte RadioNetToastInitState;
            public float RadioRelayPendingFlipSince;
            public float NextRadioNetStatusToastTime;
            public bool HasActiveThreat;
            public bool ThreatIsArmor;
            public bool HasAliveMachineGunner;
            public bool HasAliveMedic;
            public bool SquadHasAntiTank;
            public int AliveMemberCount;
            public int NearbyEnemyCount42m;
            public int NearbyFriendlyCount42m;
            public int FriendlyUnitsInsideObjective;
            public int EnemyUnitsInsideObjective;
            public Vector3 SquadCenter;
            public Vector3 ThreatPosition;
            public Vector3 ThreatDirection;
            public Spottable PrimaryTarget;
            public Vector3 LastKnownThreatPos;
            public float LastKnownThreatTime;
            public float NextCoverSectorReorientTime;

            // Macro Objective & Ticket / Attrition Context
            public bool IsInvaderFaction;
            public float TicketRatio01;
            public bool HasActiveObjectiveTask;
            public bool IsDefendingObjective;
            public Vector3 ObjectivePosition;
            public float ObjectiveCaptureProgress;

            // Tier 3 Team Net Directive assigned to this Squad Leader (plus HQ Advisory Suggestion)
            public SquadNetDirective NetDirective;
            public float NetDirectiveExpireTime;
            public Vector3 NetDirectiveTargetPos;

            public SquadNetDirective SuggestedNetDirective;
            public float SuggestedDirectiveExpireTime;
            public Vector3 SuggestedDirectiveTargetPos;
            public Vector3 SuggestedPincerFlankDir;
            public IntPtr SuggestedPincerTaskPtr;

            // Zero-Raycast Casualty / Fatal-Funnel Hazard Memory
            public Vector3 LastCasualtyPos;
            public float LastCasualtyTime;
            public float DecayedRecentDeaths;
            public float LastDeathDecayUpdateTime;

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

            public float SmokeActiveUntil;
            public Vector3 LastSmokeTargetPos;

            public Soldier SurrenderedComrade;
            public IntPtr RescuerPtr;
            public float RescueUntil;

            public IntPtr DragRescuerPtr;
            public IntPtr DragPatientPtr;
            public float DragClaimUntil;

            public Vector3 StallAnchorCenter;
            public float StallAnchorTime;
            public float NextSquadUnstallTime;
            public int ConsecutiveStallCount;

            public bool IsThreatBeingPinned(float now)
            {
                return now < SuppressHoldUntil && (Suppressor0Ptr != IntPtr.Zero || Suppressor1Ptr != IntPtr.Zero);
            }
        }

        internal static readonly Dictionary<IntPtr, float> _nextSquadVoiceTime = new Dictionary<IntPtr, float>(64);
        internal static readonly Dictionary<IntPtr, IntPtr> _pincerOrderedTaskBySquad = new Dictionary<IntPtr, IntPtr>(64);
        private static readonly Dictionary<IntPtr, Squad> _reusablePrimarySquadByTask = new Dictionary<IntPtr, Squad>(8);
        private static readonly Dictionary<IntPtr, int> _reusablePincerCountByTask = new Dictionary<IntPtr, int>(8);

        internal struct BotGoapPlan
        {
            public GoapStateBits LastGoalBit;
            public GoapActionId Step0;
            public GoapActionId Step1;
            public byte StepIndex;
            public byte StepCount;
            public float PlanExpireTime;
            public float NextReplanTime;
            public SoldierPose DesiredPose;
            public bool LockUnitCoverChange;
            public bool HasMoveOverride;
            public bool IsEvacuatingBailedCrew;
            public Vector3 MoveOverrideDest;
        }

        internal static bool TryGetBotGoapSummary(
            Soldier soldier,
            out SquadNetDirective netDir,
            out GoapStateBits goalBit,
            out GoapActionId step0,
            out GoapActionId step1,
            out byte stepIdx,
            out bool isEvacCrew)
        {
            netDir = SquadNetDirective.Disconnected_Autonomous;
            goalBit = GoapStateBits.None;
            step0 = GoapActionId.None;
            step1 = GoapActionId.None;
            stepIdx = 0;
            isEvacCrew = false;

            if (ReferenceEquals(soldier, null) || soldier == null || soldier.Pointer == IntPtr.Zero)
                return false;

            Squad sq = soldier.joinedSquad;
            if (sq != null && sq.Pointer != IntPtr.Zero && _squadBlackboards.TryGetValue(sq.Pointer, out SquadBlackboard b))
            {
                netDir = b.NetDirective;
            }

            if (AiCore.TryGetSoldierHandles(soldier.Pointer, out AiCore.SoldierFastHandles sumH) &&
                Time.time < sumH.Goap.Plan.PlanExpireTime)
            {
                goalBit = sumH.Goap.Plan.LastGoalBit;
                step0 = sumH.Goap.Plan.Step0;
                step1 = sumH.Goap.Plan.Step1;
                stepIdx = sumH.Goap.Plan.StepIndex;
                isEvacCrew = sumH.Goap.Plan.IsEvacuatingBailedCrew;
                return true;
            }
            return false;
        }

        internal static string GetNetDirectiveShortName(SquadNetDirective d)
        {
            switch (d)
            {
                case SquadNetDirective.Connected_StandardObjective: return "Connected";
                case SquadNetDirective.Anvil_PinAndCallSupport: return "AnvilPin";
                case SquadNetDirective.Hammer_WideFlankEnvelopment: return "HammerFlank";
                case SquadNetDirective.Regroup_SectorWithdrawal: return "SectorWithdraw";
                default: return "Autonomous";
            }
        }

        internal static string GetGoapGoalShortName(GoapStateBits g)
        {
            if ((g & GoapStateBits.SurvivedCrisis) != 0) return "SurviveCrisis";
            if ((g & GoapStateBits.RadioLinkPreserved) != 0) return "PreserveRadio";
            if ((g & GoapStateBits.TargetPinned) != 0) return "PinTarget";
            if ((g & GoapStateBits.TargetEliminated) != 0) return "EliminateTarget";
            if ((g & GoapStateBits.SquadSupported) != 0) return "SupportSquad";
            return "None";
        }

        internal static string GetGoapActionShortName(GoapActionId a)
        {
            switch (a)
            {
                case GoapActionId.SeekEmergencyCover: return "SeekCover";
                case GoapActionId.BandageInCover: return "BandageInCover";
                case GoapActionId.RepositionBehindWall: return "ShelterWall";
                case GoapActionId.MaintainRadioLinkWithLeader: return "KeepRadioLink";
                case GoapActionId.ThrowSmokeScreen: return "ThrowSmoke";
                case GoapActionId.SuppressThreatPosition: return "SuppressThreat";
                case GoapActionId.MoveToFlankCover: return "FlankCover";
                case GoapActionId.FlushThreatWithFrag: return "FlushFrag";
                case GoapActionId.HoldOverwatchFire: return "HoldOverwatch";
                case GoapActionId.BoundWithOverwatch: return "BoundForward";
                case GoapActionId.PointBlankBayonetBreach: return "PointBlankCQB";
                case GoapActionId.RescueSurrenderedComrade: return "LiberateComrade";
                case GoapActionId.LoneWolfLinkUpOrAmbush: return "LoneLinkOrEvac";
                case GoapActionId.DirectFireEngagement: return "DirectFire";
                case GoapActionId.ScavengeSupplyFromCorpse: return "ScavengeSupply";
                case GoapActionId.TacticalFallbackRetreat: return "FallbackRetreat";
                default: return "None";
            }
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
        internal static readonly HashSet<IntPtr> _initialHqSpawnBriefingConsumed = new HashSet<IntPtr>();
        internal static readonly Dictionary<string, FactionCommandProfile> _factionProfileCache = new Dictionary<string, FactionCommandProfile>(32);
        private static int _lastFactionProfileTheaterGen = -1;

        internal struct FactionCommandProfile
        {
            public float RadioRelayRadius;            // Default 45m (USA: 55m, UK/Canada: 50m, USSR/France: 38m, Mongolia/China: 42m)
            public bool AllowDisconnectedLocalFlank;  // True for decentralized doctrines (Germany, Finland, USA, Mongolia, Australia, NZ, Gurkha, Poland, Greece, China, Japan)
            public bool PreferAmbushWhenAlone;        // True for Japan, Finland, Gurkha, China (hold local ambush cover when alone vs retreating to merge)
            public bool AllowGyokusaiDesperation;     // True ONLY for Japan (night Banzai / scripted charge / desperate final-stand Gyokusai; NEVER Gurkha!)
            public float FlankLateralBias;            // 0.78f (tight) to 0.90f (wide encirclement like Finland/Mongolia/Japan; default 0.83f)
            public float GarrisonSquadRatio;          // Default 0.22f (UK/France/Italy: 0.28f; USSR/Japan/Mongolia: 0.16f)
            public float EmplacementSearchMult;       // Default 1.0f (Italy/France/Romania/Hungary/Greece/India: 1.18f-1.30f)
            public float SuppressionThresholdBase;    // Default 18f (USA/Manchukuo: 15f; Italy: 16f; Germany: 19f; Poland/NZ: 22f; Finland: 23f; Gurkha: 24f; Japan: 25f)
            public float SuppressCostMult;            // Germany: 0.72f, UK/India: 0.80f, France: 0.82f
            public float SmokeCostMult;               // USA: 0.70f, Canada/UK: 0.75f, Japan: 0.76f
            public float FlankMoveCostMult;           // Finland: 0.72f, Mongolia: 0.74f, USSR/Australia/Canada: 0.76f, Germany/NZ: 0.78f, USA: 0.82f
            public float FragFlushCostMult;           // China: 0.74f, USSR/Canada: 0.78f, USA_Afrikan: 0.80f, Hungary: 0.84f
            public float OverwatchHoldCostMult;       // UK: 0.78f, Germany/USA/India: 0.82f, Italy/France: 0.84f
            public float BayonetBreachCostMult;       // Japan: 0.62f, Gurkha: 0.65f, NZ: 0.75f, Greece: 0.80f, Australia: 0.82f
            public float EmergencyCoverCostMult;      // USA: 0.82f, Manchukuo: 0.78f, China: 0.84f, Italy: 0.85f
            public float ScavengeAndResupplyMult;     // Finland: 0.72f, China: 0.74f, Poland: 0.76f, Gurkha/Greece: 0.78f, Germany/USSR: 0.80f, USA/Aus/NZ: 0.82f
            public float ComradeAidAndDragMult;       // USA/USA_Afrikan: 0.74f, Aus/NZ: 0.75f, Finland: 0.76f, Canada/Poland/Gurkha: 0.78f; USSR: 1.08f, Manchukuo: 1.18f, Japan: 1.22f
            public float AttritionPivotThreshold;     // Decayed casualties on a stalled (<20%) objective before Team Net pivots squads to a softer objective (Finland: 5.5, USA: 6.0, Ger/Can/Aus/NZ: 6.5, Default: 7.5, USSR: 10.5, Japan: 12.0)

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
                EmergencyCoverCostMult = 1.0f,
                ScavengeAndResupplyMult = 1.0f,
                ComradeAidAndDragMult = 1.0f,
                AttritionPivotThreshold = 7.5f
            };
        }

        internal static bool IsTrueGyokusaiOrBanzaiCondition(
            in FactionCommandProfile profile,
            Squad sq,
            in SquadBlackboard board)
        {
            if (sq != null && sq.isChargeSquad)
                return true;

            if (!profile.AllowGyokusaiDesperation)
                return false;

            // Imperial Japanese Gyokusai / Night Banzai: unlocked ONLY at night (NightDarkness01 >= 0.55)
            // or during a desperate final stand (objective >= 60% lost or attacker tickets <= 20%)!
            // Routine daytime IJA squads fight with disciplined bush-camo infiltration, smoke, and cover.
            if (AiCore.NightDarkness01 >= 0.55f)
                return true;

            if (board.HasActiveObjectiveTask && board.IsDefendingObjective && board.ObjectiveCaptureProgress >= 0.60f)
                return true;

            if (board.IsInvaderFaction && board.TicketRatio01 <= 0.20f)
                return true;

            return false;
        }

        internal static FactionCommandProfile GetFactionCommandProfile(string faction)
        {
            if ((VisionConfig.EnableFactionNetDoctrines != null && !VisionConfig.EnableFactionNetDoctrines.Value) ||
                string.IsNullOrEmpty(faction))
                return FactionCommandProfile.Default;

            AiCore.BattleTheaterSnapshot theater = AiCore.GetBattleTheaterSnapshot();
            if (AiCore.BattleTheaterGeneration != _lastFactionProfileTheaterGen)
            {
                _lastFactionProfileTheaterGen = AiCore.BattleTheaterGeneration;
                _factionProfileCache.Clear();
            }

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
                    // Wehrmacht / Fallschirmjager: Auftragstaktik (local initiative when cut off) + MG34/42 Base-of-Fire + Beutewaffen scavenging
                    p.RadioRelayRadius = 48f;
                    p.AllowDisconnectedLocalFlank = true;
                    p.FlankLateralBias = 0.82f;
                    p.SuppressionThresholdBase = 19.5f;
                    p.SuppressCostMult = 0.72f;
                    p.FlankMoveCostMult = 0.78f;
                    p.OverwatchHoldCostMult = 0.82f;
                    p.ScavengeAndResupplyMult = 0.80f;
                    p.ComradeAidAndDragMult = 0.86f;
                    p.AttritionPivotThreshold = 6.5f;
                    break;

                case "Finland":
                    // Finnish Army: Motti wide encirclement, autonomous forest ambush, master field scavengers & "Kaveria ei jateta" comrade rescue
                    p.RadioRelayRadius = 42f;
                    p.AllowDisconnectedLocalFlank = true;
                    p.PreferAmbushWhenAlone = true;
                    p.FlankLateralBias = 0.90f;
                    p.GarrisonSquadRatio = 0.18f;
                    p.SuppressionThresholdBase = 23f;
                    p.FlankMoveCostMult = 0.72f;
                    p.FragFlushCostMult = 0.82f;
                    p.ScavengeAndResupplyMult = 0.72f;
                    p.ComradeAidAndDragMult = 0.76f;
                    p.AttritionPivotThreshold = 5.5f;
                    break;

                case "UnitedStates_Afrikan":
                    // US 92nd Infantry (Italy): strong radio net + close-range flanking, grenade grit, and high buddy aid
                    p.RadioRelayRadius = 52f;
                    p.AllowDisconnectedLocalFlank = true;
                    p.SuppressionThresholdBase = 20f;
                    p.SmokeCostMult = 0.75f;
                    p.FlankMoveCostMult = 0.80f;
                    p.FragFlushCostMult = 0.80f;
                    p.OverwatchHoldCostMult = 0.84f;
                    p.ScavengeAndResupplyMult = 0.82f;
                    p.ComradeAidAndDragMult = 0.74f;
                    p.AttritionPivotThreshold = 6.0f;
                    break;

                case "UnitedStates":
                    // US Army / Marines: Superior radio net, NCO initiative, smoke screens, buddy resupply & casualty care
                    p.RadioRelayRadius = 55f;
                    p.AllowDisconnectedLocalFlank = true;
                    p.SuppressionThresholdBase = 15.5f;
                    p.SmokeCostMult = 0.70f;
                    p.OverwatchHoldCostMult = 0.82f;
                    p.FlankMoveCostMult = 0.82f;
                    p.EmergencyCoverCostMult = 0.82f;
                    p.ScavengeAndResupplyMult = 0.82f;
                    p.ComradeAidAndDragMult = 0.74f;
                    p.AttritionPivotThreshold = 6.0f;
                    break;

                case "England":
                    // British Army: Methodical "Bite & Hold" set-piece overwatch, smoke, strong garrisoning, and disciplined casualty aid
                    p.RadioRelayRadius = 50f;
                    p.FlankLateralBias = 0.80f;
                    p.GarrisonSquadRatio = 0.28f;
                    p.EmplacementSearchMult = 1.15f;
                    p.SuppressCostMult = 0.80f;
                    p.SmokeCostMult = 0.76f;
                    p.OverwatchHoldCostMult = 0.78f;
                    p.ScavengeAndResupplyMult = 0.86f;
                    p.ComradeAidAndDragMult = 0.82f;
                    p.AttritionPivotThreshold = 7.0f;
                    break;

                case "Canada":
                    // Canadian Army (Juno / Ortona / Scheldt): Commonwealth radio net + aggressive urban/beach shock breaching & comrade rescue
                    p.RadioRelayRadius = 48f;
                    p.AllowDisconnectedLocalFlank = true;
                    p.SuppressionThresholdBase = 20f;
                    p.SmokeCostMult = 0.75f;
                    p.FlankMoveCostMult = 0.76f;
                    p.FragFlushCostMult = 0.78f;
                    p.ScavengeAndResupplyMult = 0.84f;
                    p.ComradeAidAndDragMult = 0.78f;
                    p.AttritionPivotThreshold = 6.5f;
                    break;

                case "Australia":
                    // Australian AIF (Tobruk / Kokoda): Autonomous bush/desert patrol flanking, close-quarters grit, and "mateship" casualty care
                    p.RadioRelayRadius = 46f;
                    p.AllowDisconnectedLocalFlank = true;
                    p.FlankLateralBias = 0.86f;
                    p.SuppressionThresholdBase = 21.5f;
                    p.FlankMoveCostMult = 0.76f;
                    p.BayonetBreachCostMult = 0.82f;
                    p.ScavengeAndResupplyMult = 0.82f;
                    p.ComradeAidAndDragMult = 0.75f;
                    p.AttritionPivotThreshold = 6.5f;
                    break;

                case "NewZealand":
                    // 2nd NZ Division / Maori Battalion (Crete / Cassino): Fierce close-assault, counter-attack shock & strong mate recovery
                    p.RadioRelayRadius = 46f;
                    p.AllowDisconnectedLocalFlank = true;
                    p.FlankLateralBias = 0.85f;
                    p.SuppressionThresholdBase = 22f;
                    p.FlankMoveCostMult = 0.78f;
                    p.FragFlushCostMult = 0.82f;
                    p.BayonetBreachCostMult = 0.75f;
                    p.ScavengeAndResupplyMult = 0.82f;
                    p.ComradeAidAndDragMult = 0.75f;
                    p.AttritionPivotThreshold = 6.5f;
                    break;

                case "India":
                    // Indian Army: Disciplined mountain/desert overwatch, proactive emplacement defense & regimental casualty care
                    p.RadioRelayRadius = 46f;
                    p.GarrisonSquadRatio = 0.25f;
                    p.EmplacementSearchMult = 1.18f;
                    p.SuppressCostMult = 0.80f;
                    p.OverwatchHoldCostMult = 0.82f;
                    p.ScavengeAndResupplyMult = 0.88f;
                    p.ComradeAidAndDragMult = 0.82f;
                    p.AttritionPivotThreshold = 7.0f;
                    break;

                case "Gurkha":
                    // Gurkhas: Fearless close-quarters Kukri/melee shock, wide infiltration, field scavenging & fierce unit brotherhood
                    p.RadioRelayRadius = 44f;
                    p.AllowDisconnectedLocalFlank = true;
                    p.PreferAmbushWhenAlone = true;
                    p.FlankLateralBias = 0.88f;
                    p.SuppressionThresholdBase = 24f;
                    p.FlankMoveCostMult = 0.75f;
                    p.BayonetBreachCostMult = 0.65f;
                    p.ScavengeAndResupplyMult = 0.78f;
                    p.ComradeAidAndDragMult = 0.78f;
                    p.AttritionPivotThreshold = 8.5f;
                    break;

                case "France":
                    // 1940 French Army: Bataille Conduite (Methodical top-down battle, strong emplacements, urgent radio recovery if cut)
                    p.RadioRelayRadius = 38f;
                    p.AllowDisconnectedLocalFlank = false;
                    p.FlankLateralBias = 0.78f;
                    p.GarrisonSquadRatio = 0.28f;
                    p.EmplacementSearchMult = 1.25f;
                    p.SuppressCostMult = 0.82f;
                    p.OverwatchHoldCostMult = 0.84f;
                    p.ScavengeAndResupplyMult = 0.94f;
                    p.ComradeAidAndDragMult = 0.88f;
                    break;

                case "Poland":
                    // Polish Army: Tenacious mobile defense, high cohesion when cut off, aggressive weapon scavenging & comrade liberation
                    p.RadioRelayRadius = 42f;
                    p.AllowDisconnectedLocalFlank = true;
                    p.EmplacementSearchMult = 1.15f;
                    p.SuppressionThresholdBase = 22f;
                    p.FlankMoveCostMult = 0.82f;
                    p.FragFlushCostMult = 0.84f;
                    p.ScavengeAndResupplyMult = 0.76f;
                    p.ComradeAidAndDragMult = 0.78f;
                    p.AttritionPivotThreshold = 7.0f;
                    break;

                case "Greece":
                    // Greek Army: Mountain fortification defense, counter-charge grit, captured supply scavenging & comrade aid
                    p.RadioRelayRadius = 40f;
                    p.AllowDisconnectedLocalFlank = true;
                    p.GarrisonSquadRatio = 0.26f;
                    p.EmplacementSearchMult = 1.22f;
                    p.SuppressionThresholdBase = 21f;
                    p.BayonetBreachCostMult = 0.80f;
                    p.ScavengeAndResupplyMult = 0.78f;
                    p.ComradeAidAndDragMult = 0.80f;
                    break;

                case "Ussr":
                    // Red Army: Deep Battle tank-infantry pincers & close SMG/grenade shock, active battlefield re-arming, relentless assault wave
                    p.RadioRelayRadius = 38f;
                    p.AllowDisconnectedLocalFlank = false;
                    p.FlankLateralBias = 0.86f;
                    p.GarrisonSquadRatio = 0.16f;
                    p.SuppressionThresholdBase = 20.5f;
                    p.FlankMoveCostMult = 0.76f;
                    p.FragFlushCostMult = 0.78f;
                    p.BayonetBreachCostMult = 0.84f;
                    p.ScavengeAndResupplyMult = 0.80f;
                    p.ComradeAidAndDragMult = 1.08f;
                    p.AttritionPivotThreshold = 10.5f;
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
                    p.ScavengeAndResupplyMult = 0.84f;
                    p.ComradeAidAndDragMult = 0.90f;
                    p.AttritionPivotThreshold = 7.0f;
                    break;

                case "China":
                    // National Revolutionary Army (Shanghai / Nanking): Low radio reliance, close grenade ambushes, aggressive battlefield scavenging
                    p.RadioRelayRadius = 38f;
                    p.AllowDisconnectedLocalFlank = true;
                    p.PreferAmbushWhenAlone = true;
                    p.FlankLateralBias = 0.85f;
                    p.SuppressionThresholdBase = 21f;
                    p.FragFlushCostMult = 0.74f;
                    p.BayonetBreachCostMult = 0.78f;
                    p.EmergencyCoverCostMult = 0.84f;
                    p.ScavengeAndResupplyMult = 0.74f;
                    p.ComradeAidAndDragMult = 0.92f;
                    p.AttritionPivotThreshold = 9.5f;
                    break;

                case "Japan":
                    // Imperial Japanese Army / SNLF: Daytime bush-camo infiltration, smoke + tactical shock, and night/final-stand Gyokusai
                    p.RadioRelayRadius = 42f;
                    p.AllowDisconnectedLocalFlank = true;
                    p.PreferAmbushWhenAlone = true;
                    p.AllowGyokusaiDesperation = true;
                    p.FlankLateralBias = 0.88f;
                    p.GarrisonSquadRatio = 0.16f;
                    p.SuppressionThresholdBase = 25f;
                    p.SmokeCostMult = 0.76f;
                    p.FlankMoveCostMult = 0.78f;
                    p.BayonetBreachCostMult = 0.62f;
                    p.ScavengeAndResupplyMult = 0.84f;
                    p.ComradeAidAndDragMult = 1.22f;
                    p.AttritionPivotThreshold = 12.0f;
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
                    p.ScavengeAndResupplyMult = 0.95f;
                    p.ComradeAidAndDragMult = 1.18f;
                    p.AttritionPivotThreshold = 6.5f;
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
                    p.ScavengeAndResupplyMult = 0.92f;
                    p.ComradeAidAndDragMult = 0.90f;
                    p.AttritionPivotThreshold = 7.0f;
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
                    p.ScavengeAndResupplyMult = 0.90f;
                    p.ComradeAidAndDragMult = 0.92f;
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
                    p.ScavengeAndResupplyMult = 0.86f;
                    p.ComradeAidAndDragMult = 0.88f;
                    break;
            }

            // Sub-Faction, Era (BattleData.year), & Elite Battalion Adaptations
            string batLower = theater.GetBattalionLowerForFaction(faction);
            if (f == "France" &&
                (theater.Year >= 1943 ||
                 faction.IndexOf("Free", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 batLower.Contains("ffi") ||
                 batLower.Contains("partisan") ||
                 batLower.Contains("resistance") ||
                 batLower.Contains("2e") ||
                 batLower.Contains("maroc") ||
                 batLower.Contains("goumier")))
            {
                // 1943-45 Free French (2e DB / 1re Armee / Goumiers) & FFI Resistance: US radio net + decentralized initiative
                p.RadioRelayRadius = 48f;
                p.AllowDisconnectedLocalFlank = true;
                p.FlankLateralBias = 0.84f;
                p.SmokeCostMult = 0.76f;
                p.FlankMoveCostMult = 0.78f;
                p.ScavengeAndResupplyMult = 0.78f;
            }
            else if (f == "Italy" &&
                     (faction.EndsWith("_allies", StringComparison.OrdinalIgnoreCase) ||
                      batLower.Contains("folgore") ||
                      batLower.Contains("alpini") ||
                      batLower.Contains("bersaglieri") ||
                      batLower.Contains("san marco") ||
                      batLower.Contains("arditi") ||
                      batLower.Contains("partig")))
            {
                // 1943-45 Italian Co-Belligerent / Resistance & Elite Folgore / Alpini / Bersaglieri / San Marco battalions
                p.RadioRelayRadius = 44f;
                p.AllowDisconnectedLocalFlank = true;
                p.SuppressionThresholdBase = 21.5f;
                p.FlankMoveCostMult = 0.78f;
                p.FragFlushCostMult = 0.80f;
                p.ScavengeAndResupplyMult = 0.80f;
                p.ComradeAidAndDragMult = 0.80f;
            }
            else if (f == "Ussr" &&
                     (theater.Map == Gamemap.Stalingrad ||
                      theater.Year >= 1943 ||
                      batLower.Contains("guard") ||
                      batLower.Contains("shock") ||
                      batLower.Contains("storm")))
            {
                // Stalingrad Urban Storm Groups & 1943-45 Late-War Guards / Red Army
                p.RadioRelayRadius = 44f;
                p.AllowDisconnectedLocalFlank = true;
                p.SmokeCostMult = 0.82f;
                p.ComradeAidAndDragMult = 0.94f;
                p.AttritionPivotThreshold = 8.5f;
            }
            else if (f == "UnitedStates" &&
                     (theater.IsJungleOrAtollMap ||
                      batLower.Contains("marine") ||
                      batLower.Contains("usmc") ||
                      batLower.Contains("airborne") ||
                      batLower.Contains("parachut") ||
                      batLower.Contains("ranger") ||
                      batLower.Contains("101") ||
                      batLower.Contains("82")))
            {
                // USMC Pacific & US Airborne / Rangers: fierce CQB shock + "no Marine/paratrooper left behind"
                p.SuppressionThresholdBase = 21.0f;
                p.FlankMoveCostMult = 0.78f;
                p.FragFlushCostMult = 0.76f;
                p.BayonetBreachCostMult = 0.80f;
                p.ComradeAidAndDragMult = 0.72f;
            }

            // Map & Terrain Theater Modifiers (Beachhead, Open Desert/Steppe, Dense Urban)
            if (theater.IsAmphibiousBeachMap &&
                !string.IsNullOrEmpty(theater.InvadersFactionId) &&
                AiCore.AreSameSideFactions(faction, theater.InvadersFactionId))
            {
                p.SmokeCostMult *= 0.60f;
            }

            if (theater.IsOpenDesertOrSteppeMap)
            {
                p.FlankLateralBias = Mathf.Min(0.93f, p.FlankLateralBias + 0.08f);
                p.SmokeCostMult *= 0.82f;
            }

            if (theater.IsDenseUrbanMap)
            {
                p.FlankLateralBias = Mathf.Max(0.72f, p.FlankLateralBias - 0.06f);
                p.GarrisonSquadRatio = Mathf.Min(0.36f, p.GarrisonSquadRatio + 0.06f);
                p.FragFlushCostMult *= 0.88f;
            }

            _factionProfileCache[faction] = p;
            return p;
        }

        public static int GoapPlansExecutedCount { get; private set; }
        public static int GoapPinAndFlankCount { get; private set; }
        public static int GoapSmokeScreensCount { get; private set; }
        public static int RadioNetCutoffsCount { get; private set; }
        public static int TeamNetAnvilHammerCount { get; private set; }
        public static int RoleWeaponUpgradesCount { get; private set; }
        public static int BuddyResuppliesCount { get; private set; }
        public static int FieldRadiosInheritedCount { get; private set; }
        public static int HelmetsRecoveredCount { get; private set; }
        public static int CasualtiesDraggedCount { get; private set; }
        public static int BotKillCalloutsCount { get; set; }
        public static int SquadChargesOrderedCount { get; private set; }
        public static int SquadCovertOrdersCount { get; private set; }
        public static int SquadFollowOrdersCount { get; private set; }
        public static int TacticalRetreatsCount { get; private set; }
        public static int SquadLeaderBalksCount { get; private set; }
        public static int IndividualSelfPreservationBalksCount { get; private set; }
        public static int FootBotUnsticksCount { get; private set; }

        internal static readonly HashSet<IntPtr> _aiCovertHoldFireSquads = new HashSet<IntPtr>();
        internal static readonly HashSet<IntPtr> _aiOrderedChargeSquads = new HashSet<IntPtr>();
        internal static readonly Dictionary<IntPtr, float> _aiChargeBaselineDeaths = new Dictionary<IntPtr, float>(32);
        internal static readonly Dictionary<IntPtr, float> _aiCovertHoldFireExpireTime = new Dictionary<IntPtr, float>(32);
        internal static readonly Dictionary<IntPtr, float> _aiFollowColumnUntil = new Dictionary<IntPtr, float>(32);
        internal static readonly Dictionary<IntPtr, float> _nextAiSquadChargeTime = new Dictionary<IntPtr, float>(64);
        internal static readonly Dictionary<IntPtr, float> _nextAiCovertOrderTime = new Dictionary<IntPtr, float>(64);
        internal static readonly Dictionary<IntPtr, float> _nextAiFollowOrderTime = new Dictionary<IntPtr, float>(64);
        internal static readonly Dictionary<IntPtr, float> _nextAiSquadRetreatTime = new Dictionary<IntPtr, float>(64);
        internal static readonly Dictionary<IntPtr, float> _nextSquadRadioScavengeTime = new Dictionary<IntPtr, float>(64);
        internal static readonly Dictionary<IntPtr, float> _aiSquadRetreatUntil = new Dictionary<IntPtr, float>(32);

        internal struct CasualtyDragState
        {
            public Soldier Patient;
            public Vector3 DropSpot;
            public float DragEndTime;
            public float NextScanTime;
            public Vector3 StallAnchorPos;
            public float StallAnchorTime;
        }

        internal struct FootStuckState
        {
            public Vector3 AnchorPos;
            public float AnchorTime;
            public float NextUnstickTime;
            public float UnstickOverrideUntil;
            public Vector3 UnstickDest;
            public int ConsecutiveUnsticks;
        }

        public static void ClearGoapCaches()
        {
            _squadBlackboards.Clear();
            _freeFloorItemsSnapshot.Clear();
            _stagingFreeFloorItems.Clear();
            _nextFreeFloorItemsSnapshotTime = 0f;
            _floorScanCursor = 0;
            _lastFloorScanFrame = 0;
            _activeScavengeInventories.Clear();
            _stagingScavengeInventories.Clear();
            _nextScavengeInvSnapshotTime = 0f;
            _scavengeInvScanCursor = 0;
            _lastScavengeInvScanFrame = 0;
            _cachedBoxOverrides.Clear();
            _cachedFloorItemMeta.Clear();
            _cachedInventoryOwners.Clear();
            _initialHqSpawnBriefingConsumed.Clear();
            _nextSquadVoiceTime.Clear();
            _pincerOrderedTaskBySquad.Clear();
            _aiCovertHoldFireSquads.Clear();
            _aiOrderedChargeSquads.Clear();
            _aiChargeBaselineDeaths.Clear();
            _aiCovertHoldFireExpireTime.Clear();
            _aiFollowColumnUntil.Clear();
            _nextAiSquadChargeTime.Clear();
            _nextAiCovertOrderTime.Clear();
            _nextAiFollowOrderTime.Clear();
            _nextAiSquadRetreatTime.Clear();
            _nextSquadRadioScavengeTime.Clear();
            _aiSquadRetreatUntil.Clear();
            FootBotUnsticksCount = 0;
            _nextGlobalBotWeaponPickupTime = 0f;
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
            AiCore.SoldierFastHandles h = AiCore.GetOrCreateSoldierHandles(soldier);
            GoapStateBits ws = GoapStateBits.None;
            Vector3 myPos = soldier.transform.position;
            Vector3 localThreatDir = GetEffectiveThreatDirection(soldier, sq, myPos, board.ThreatDirection);
            bool isRad = soldier.IsRadioman();

            // Never classify a crewman as "bailed out" while he is actively repairing OR walking to remount his vehicle!
            bool isBailedVehicleCrew = IsSoldierBailedOutVehicleCrew(soldier, sq);

            if (soldier.isBleeding)
            {
                ws |= GoapStateBits.IsBleeding;
                EvaluateSoldierBandageStateForTick(ai, soldier, h, now, out _, out bool canSafelyBandageWs, out _, out _);
                if (!canSafelyBandageWs)
                    ws |= GoapStateBits.BandageUnsafeUnderFire;
            }
            if (soldier.IsOnCover() || IsPositionShelteredByFrontCover(myPos, localThreatDir, soldier.transform.root))
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

            if (board.LastCasualtyTime > 0f &&
                (now - board.LastCasualtyTime) <= 5.5f &&
                (myPos - board.LastCasualtyPos).sqrMagnitude <= 11.5f * 11.5f &&
                (now >= board.SmokeActiveUntil || (board.LastSmokeTargetPos - myPos).sqrMagnitude > 18.0f * 18.0f))
            {
                ws |= GoapStateBits.NearFreshCasualty;
            }

            if (ai.visibleTarget != null)
                ws |= GoapStateBits.HasVisibleEnemy;
            // Bailed vehicle crews only block evacuation for close-quarters self-defense (<= 24m), never to plink at distant riflemen!
            if (ai.HasEnemyAndCanFireAtHim && (!isBailedVehicleCrew || dist <= 24.0f))
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
                    // Hysteresis (20.5m enter / 12.5m exit) prevents Radioman from thrashing at the 18m boundary!
                    bool alreadyMaintainingLink =
                        h != null &&
                        now < h.Goap.Plan.PlanExpireTime &&
                        (h.Goap.Plan.StepIndex == 0 ? h.Goap.Plan.Step0 : h.Goap.Plan.Step1) == GoapActionId.MaintainRadioLinkWithLeader;
                    float driftThreshold = alreadyMaintainingLink ? 12.5f : 20.5f;
                    if (dToLeaderSq > driftThreshold * driftThreshold || (sq.RadioRequestWaiting() && dToLeaderSq > 5.0f * 5.0f))
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

            bool isCombatGrenadier = personality == SoldierPersonality.Assaulter_Breacher ||
                                     personality == SoldierPersonality.Rifleman_Grenadier ||
                                     personality == SoldierPersonality.AT_Demolitionist;
            bool hasCombatGrenadierVisibleTarget = isCombatGrenadier &&
                                                   ai.visibleTarget != null &&
                                                   !ai.visibleTarget.IsWheeledVehicleOrTank() &&
                                                   dist >= 9.0f &&
                                                   dist <= 34.0f;

            if ((VisionConfig.EnableLastKnownSuppression.Value &&
                 ((h != null &&
                   h.Vision.LastKnownTarget.LastSeenTime > 0f &&
                   now - h.Vision.LastKnownTarget.LastSeenTime <= 4.5f) ||
                  (board.HasActiveThreat && !board.ThreatIsArmor && !ai.HasEnemyAndCanFireAtHim))) ||
                hasCombatGrenadierVisibleTarget)
            {
                ws |= GoapStateBits.TargetLostBehindCover;
            }

            float dToThreatSq = board.HasActiveThreat ? (board.ThreatPosition - myPos).sqrMagnitude : 999999f;
            if ((board.HasActiveThreat && dToThreatSq >= 10f * 10f && dToThreatSq <= 29f * 29f) || hasCombatGrenadierVisibleTarget)
                ws |= GoapStateBits.InGrenadeRange;

            if (soldier.inventory != null && now > soldier.throw_end_time + 5.0f)
            {
                if ((VisionConfig.EnableLastKnownSuppression.Value || hasCombatGrenadierVisibleTarget) &&
                    (h == null || now >= h.Vision.NextSuppressiveGrenadeTime || h.Vision.NextSuppressiveGrenadeTime - now > 45.0f) &&
                    (board.BreacherPtr == IntPtr.Zero || board.BreacherPtr == soldier.Pointer || now >= board.BreachUntil) &&
                    AiCore.FindThrowableGrenadeInInventory(soldier, findAtGrenade: false, findFragGrenade: true) != null)
                {
                    ws |= GoapStateBits.HasFragGrenade;
                }

                // Prevent smoke-screen spam: enforce both a 32s personal smoke cooldown and a 22s squad-wide smoke cooldown
                // within 18m of an already-smoked threat sector (while allowing smoke on a fresh threat > 18m away).
                float nextSmk = h != null ? h.Goap.NextSmokeGrenadeTime : 0f;
                bool personalSmokeReady = now >= nextSmk || nextSmk - now > 60.0f;
                bool sectorNeedsSmoke = now >= board.SmokeActiveUntil ||
                                        board.SmokeActiveUntil - now > 60.0f ||
                                        (board.ThreatPosition - board.LastSmokeTargetPos).sqrMagnitude > 18f * 18f;
                if (board.HasActiveThreat &&
                    personalSmokeReady &&
                    sectorNeedsSmoke &&
                    dToThreatSq >= 12f * 12f &&
                    dToThreatSq <= 38f * 38f &&
                    !AiCore.HasLowCeilingOverhead(myPos, 3.2f) &&
                    soldier.inventory.FindItemOfType<VirtualSmokeGrenade>() != null)
                {
                    ws |= GoapStateBits.HasSmokeGrenade;
                }
            }

            if (!(soldier.IsSquadLeader() && board.AliveMemberCount >= 2) &&
                ((board.FlankLeftCover != null && (board.FlankerLeftPtr == IntPtr.Zero || board.FlankerLeftPtr == soldier.Pointer)) ||
                 (board.FlankRightCover != null && (board.FlankerRightPtr == IntPtr.Zero || board.FlankerRightPtr == soldier.Pointer))))
            {
                ws |= GoapStateBits.FlankCoverAvailable;
            }

            if (VisionConfig.EnableSmartWallSideCover.Value &&
                !isBailedVehicleCrew &&
                !ai.IsMoving() &&
                !IsAsyncPathRefreshPending(ai, soldier, myPos, now) &&
                !IsInCommittedFiringHalt(soldier.Pointer, now) &&
                (suppression >= 3f ||
                 board.HasActiveThreat ||
                 VisionPatches.TryGetNearestEnemyGunfire(myPos, soldier.faction, 135f, 3.5f, out _)) &&
                IsPositionOnExposedSideOfWall(myPos, localThreatDir, soldier.transform.root, out _))
            {
                ws |= GoapStateBits.ExposedSideOfWall;
                ws &= ~GoapStateBits.InCover;
            }

            if (VisionConfig.EnableComradeLiberation.Value &&
                !(soldier.IsSquadLeader() && board.AliveMemberCount >= 2) &&
                (h == null || now >= h.Combat.LiberateMoveCooldownUntil) &&
                board.SurrenderedComrade != null &&
                board.SurrenderedComrade.IsAlive &&
                board.SurrenderedComrade.HasSurrended() &&
                (board.RescuerPtr == IntPtr.Zero || board.RescuerPtr == soldier.Pointer) &&
                (board.SurrenderedComrade.transform.position - myPos).sqrMagnitude <= 28.0f * 28.0f)
            {
                ws |= GoapStateBits.ComradeNeedsLiberation;
            }

            if (ai.HasEnemyAndCanFireAtHim && dist >= 16f && dist <= 140f && suppression < 42f &&
                AiCore.TryGetSpottableCenter(ai.visibleTarget, out Vector3 tPos))
            {
                // Synchronize GOAP overwatch with SoldierAI_IsMoveTime_Patch's committed firing halt so
                // advancing bots never enter out-of-phase micro-halts or freeze on open bridges.
                bool canHoldOverwatchNow = true;
                if (isOnBridge && (ws & GoapStateBits.InCover) == 0 && !soldier.UsingBipod())
                {
                    canHoldOverwatchNow = false;
                }
                else if ((ai.HasPath || ai.HasPathRequest) &&
                         !soldier.UsingBipod() &&
                         !(board.IsDefendingObjective && sq != null && sq.IsInsideMoveOrderArea(myPos)))
                {
                    canHoldOverwatchNow = IsInCommittedFiringHalt(soldier.Pointer, now);
                }

                if (canHoldOverwatchNow &&
                    (HasClearLineOfFireFromCrouch(soldier, tPos, dist) || HasClearLineOfFireFromStanding(soldier, tPos, dist)))
                {
                    ws |= GoapStateBits.CanProvideOverwatch;
                }
            }

            // Detect when the soldier is "Entirely Alone" (last survivor of squad, Leader dead and no squadmates within 35m,
            // or any bailed-out Vehicle Crew on foot — even while executing MissionTaskRetreat so GOAP drives evacuation!)
            bool onRetreatTask = IsSquadOnRetreatTask(sq);
            if (isBailedVehicleCrew)
            {
                ws |= GoapStateBits.IsEntirelyAlone;
            }
            else if (!onRetreatTask)
            {
                if (sq == null || board.AliveMemberCount <= 1)
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
            }

            // Authentic Field Bandage Tracking: Soldier.hasSyringe (0x502) is strictly the Medic's revive syringe!
            // Personal self-bleed dressings live in soldier.inventory as VirtualBandages (consumed by Soldier.UseBandages).
            bool hasBandage = soldier.inventory != null && soldier.inventory.FindItemOfType<VirtualBandages>() != null;
            if (hasBandage)
                ws |= GoapStateBits.HasBandage;

            // Battlefield Scavenging, Buddy Resupply, Wearable Scavenging & Role-Smart Weapon Upgrades:
            // 1) Out of reserve ammo for primary/stowed weapon OR out of VirtualBandages when not fighting a visible foot enemy.
            // 2) Need an Anti-Tank weapon/AT grenade when enemy armor is threatening the squad (or bot is an AT trooper whose launcher is dry).
            // 3) Pistol-only on foot (merged tankers/pilots/officers), fallen Squad MG inheritance, or role-matched primary upgrade!
            // 4) Emergent Field Radio / Fallen Medic Syringe Inheritance & Knocked-Off / Player-Gifted Helmet Recovery!
            // 5) Support Ammo-Bearer proactively feeding a dry MG or AT squadmate!
            bool urgentBandageWhileBleeding = soldier.isBleeding && !hasBandage;
            if ((!soldier.isBleeding || urgentBandageWhileBleeding) && suppression < 28f && !isOnBridge)
            {
                GenericGun gun0 = soldier.GetHeldGun(0);
                GenericGun gun1 = soldier.GetHeldGun(1);
                GenericGun gun2 = soldier.GetHeldGun(2);
                bool recentFootContact = h != null &&
                                         h.Vision.LastKnownTarget.LastSeenTime > 0f &&
                                         (now - h.Vision.LastKnownTarget.LastSeenTime) < 4.5f;
                bool casualScavengeAllowed = !urgentBandageWhileBleeding && !isEvadingArmor && !isBailedVehicleCrew;
                bool nonUrgentScavengeAllowed = casualScavengeAllowed && !recentFootContact;
                bool outOfAmmo = casualScavengeAllowed &&
                                 ((gun0 != null && soldier.IsOutOfAmmosFor(gun0)) ||
                                  (gun1 != null && gun1.IsAtWeapon() && !string.IsNullOrEmpty(gun1.compatibleAmmo) && soldier.IsOutOfAmmosFor(gun1)) ||
                                  (gun2 != null && !gun2.IsPistol() && !gun2.IsAtWeapon() && soldier.IsOutOfAmmosFor(gun2)));
                bool outOfBandage = (urgentBandageWhileBleeding || !recentFootContact) &&
                                    !isEvadingArmor &&
                                    !isBailedVehicleCrew &&
                                    !hasBandage;

                bool armorThreatPresent = (ws & GoapStateBits.ArmorThreatClose) != 0 ||
                                          (board.HasActiveThreat && board.PrimaryTarget != null && board.PrimaryTarget.TryCast<Vehicle>() != null) ||
                                          (ai.visibleTarget != null && ai.visibleTarget.TryCast<Vehicle>() != null);
                SoldierPersonality pers = GetPersonality(soldier);
                bool isAtRole = AiCore.IsAtSoldierPersonality(pers);
                bool isMedicRole = AiCore.IsMedicSoldierOrPersonality(null, pers);
                bool needAtWeapon = false;
                if (!urgentBandageWhileBleeding &&
                    (armorThreatPresent || isAtRole) &&
                    !onRetreatTask && !soldier.IsSquadLeader() && !soldier.IsRadioman() && !isMedicRole)
                {
                    bool hasUsableAtGun = (gun0 != null && gun0.IsAtWeapon() && (gun0.GetCurrentAmmoCount() > 0 || !soldier.IsOutOfAmmosFor(gun0))) ||
                                          (gun1 != null && gun1.IsAtWeapon() && (gun1.GetCurrentAmmoCount() > 0 || !soldier.IsOutOfAmmosFor(gun1))) ||
                                          (gun2 != null && gun2.IsAtWeapon() && (gun2.GetCurrentAmmoCount() > 0 || !soldier.IsOutOfAmmosFor(gun2)));
                    bool hasAtGrenade = AiCore.FindThrowableGrenadeInInventory(soldier, findAtGrenade: true, findFragGrenade: false) != null;
                    needAtWeapon = !hasUsableAtGun && !hasAtGrenade;
                }

                bool needUrgentPrimary = false;
                bool wantSquadMg = false;
                bool wantRoleUpgrade = false;
                if (casualScavengeAllowed)
                {
                    EvaluateRoleWeaponUpgradeNeed(
                        soldier,
                        sq,
                        pers,
                        isMedicRole,
                        onRetreatTask,
                        ref board,
                        now,
                        out needUrgentPrimary,
                        out wantSquadMg,
                        out wantRoleUpgrade);
                    if (recentFootContact)
                    {
                        wantSquadMg = false;
                        wantRoleUpgrade = false;
                    }
                }
                bool wantAnyWeaponUpgrade = needUrgentPrimary || wantSquadMg || wantRoleUpgrade;

                bool wantRadioPack = nonUrgentScavengeAllowed &&
                                     VisionConfig.EnableEmergentWearableScavenge.Value &&
                                     !onRetreatTask &&
                                     sq != null &&
                                     !AiCore.IsTrueVehicleCrewSquad(sq) &&
                                     !board.PreviouslyHadAliveRadioman &&
                                     !sq.HasRadioman() &&
                                     (!_nextSquadRadioScavengeTime.TryGetValue(sq.Pointer, out float nextRadScavT) || now >= nextRadScavT) &&
                                     !IsRadioPackAlreadyClaimedForSquad(soldier.Pointer, sq.Pointer, now) &&
                                     suppression < 10.0f &&
                                     !isRad &&
                                     !isMedicRole &&
                                     (soldier.vest_Obj == null || !soldier.vest_Obj.isFuelTank);

                bool wantMedicSyringe = nonUrgentScavengeAllowed &&
                                        !onRetreatTask &&
                                        sq != null &&
                                        !AiCore.IsTrueVehicleCrewSquad(sq) &&
                                        !board.HasAliveMedic &&
                                        !soldier.hasSyringe &&
                                        !isRad &&
                                        !soldier.IsSquadLeader();

                bool wantHelmet = nonUrgentScavengeAllowed &&
                                  VisionConfig.EnableEmergentWearableScavenge.Value &&
                                  !onRetreatTask &&
                                  !soldier.hasHeadgear &&
                                  suppression < 12f;

                bool canFeedNeedyAlly = nonUrgentScavengeAllowed &&
                                        VisionConfig.EnableBuddyResupply.Value &&
                                        !onRetreatTask &&
                                        !outOfAmmo &&
                                        soldier.IsAmmoBoxCarrier();

                bool wantFragGrenade = nonUrgentScavengeAllowed &&
                                       !onRetreatTask &&
                                       !isMedicRole &&
                                       suppression < 16f &&
                                       soldier.inventory != null &&
                                       AiCore.FindThrowableGrenadeInInventory(soldier, findAtGrenade: false, findFragGrenade: true) == null;

                // Do not immediately break off a smoke-covered push just to scavenge another smoke grenade right after throwing one!
                float nextSmkScav = h != null ? h.Goap.NextSmokeGrenadeTime : 0f;
                bool wantSmokeGrenade = nonUrgentScavengeAllowed &&
                                        !onRetreatTask &&
                                        suppression < 16f &&
                                        (now >= nextSmkScav || nextSmkScav - now > 60.0f) &&
                                        (soldier.IsSquadLeader() ||
                                         isMedicRole ||
                                         pers == SoldierPersonality.Assaulter_Breacher ||
                                         pers == SoldierPersonality.Assaulter_Skirmisher) &&
                                        soldier.inventory != null &&
                                        soldier.inventory.FindItemOfType<VirtualSmokeGrenade>() == null;

                bool wantToolbox = isBailedVehicleCrew &&
                                   !urgentBandageWhileBleeding &&
                                   !isEvadingArmor &&
                                   suppression < 20f &&
                                   soldier.GetRepairTools() == null;

                bool wantAtMine = nonUrgentScavengeAllowed &&
                                  VisionConfig.EnableSpecialMunitionsAndSappers.Value &&
                                  !onRetreatTask &&
                                  !isMedicRole &&
                                  !soldier.IsSquadLeader() &&
                                  suppression < 14f &&
                                  (isAtRole || board.IsDefendingObjective) &&
                                  AiCore.AnyEnemyGroundVehiclesPresent(soldier.faction) &&
                                  !HasPressureAtMineInInventory(soldier);

                bool wantDeployableTripod = nonUrgentScavengeAllowed &&
                                            VisionConfig.EnableSpecialMunitionsAndSappers.Value &&
                                            !onRetreatTask &&
                                            !isMedicRole &&
                                            !soldier.IsSquadLeader() &&
                                            !isRad &&
                                            suppression < 12f &&
                                            soldier.inventory != null &&
                                            soldier.inventory.FindItemOfType<VirtualDeployableVehicle>() == null;

                bool wantShellAmmo = nonUrgentScavengeAllowed &&
                                     !onRetreatTask &&
                                     suppression < 14f &&
                                     h != null &&
                                     AiCore.HasHeldMortarCached(h, ai, now);

                bool wantRecoverLife = nonUrgentScavengeAllowed &&
                                       !onRetreatTask &&
                                       suppression < 6f &&
                                       soldier.IsAlive &&
                                       soldier.life_total.Value > 0 &&
                                       soldier.life_total.Value < 85;

                bool hasVisibleFootEnemy = ai.HasEnemyAndCanFireAtHim && (ai.visibleTarget == null || ai.visibleTarget.TryCast<Vehicle>() == null);
                if (!hasVisibleFootEnemy &&
                    (needAtWeapon || wantAnyWeaponUpgrade || outOfAmmo || outOfBandage || wantFragGrenade || wantSmokeGrenade ||
                     wantRadioPack || wantMedicSyringe || wantHelmet || canFeedNeedyAlly ||
                     wantToolbox || wantAtMine || wantDeployableTripod || wantShellAmmo || wantRecoverLife) &&
                    TryGetNearbyScavengeTarget(
                        soldier,
                        myPos,
                        pers,
                        outOfAmmo,
                        outOfBandage,
                        wantFragGrenade,
                        wantSmokeGrenade,
                        needAtWeapon,
                        needUrgentPrimary,
                        wantSquadMg,
                        wantRoleUpgrade,
                        wantRadioPack,
                        wantMedicSyringe,
                        wantHelmet,
                        canFeedNeedyAlly,
                        wantToolbox,
                        wantAtMine,
                        wantDeployableTripod,
                        wantShellAmmo,
                        wantRecoverLife,
                        now,
                        out _))
                {
                    ws |= GoapStateBits.NeedAmmoOrMedScavenge;
                }
            }

            // Win-Condition Urgency: Defenders contest objectives being back-capped or outnumbered inside the circle,
            // and Attackers push into the cap circle whenever they lack capture majority inside the zone, capture is
            // >= 35% underway, or tickets are critically low (<= 25%)!
            bool desperateCapHold = false;
            if (board.HasActiveObjectiveTask && sq != null && sq.currentTask != null)
            {
                float dToObjSq = (board.ObjectivePosition - myPos).sqrMagnitude;
                bool insideCap = sq.currentTask.IsInsideTaskArea(myPos, 0.85f);
                desperateCapHold = insideCap &&
                                   ((board.IsDefendingObjective && board.ObjectiveCaptureProgress >= 0.40f) ||
                                    (!board.IsDefendingObjective && (board.ObjectiveCaptureProgress >= 0.50f || board.TicketRatio01 <= 0.22f)));

                if (dToObjSq <= 85f * 85f && !insideCap)
                {
                    bool defenderPointBeingCapped = board.IsDefendingObjective &&
                                                    (board.ObjectiveCaptureProgress >= 0.08f ||
                                                     board.EnemyUnitsInsideObjective > board.FriendlyUnitsInsideObjective);
                    bool urgentAttackerProgressOrTickets = !board.IsDefendingObjective &&
                                                           dToObjSq <= 75f * 75f &&
                                                           (board.ObjectiveCaptureProgress >= 0.35f ||
                                                            (board.IsInvaderFaction && board.TicketRatio01 <= 0.25f));
                    bool attackerOutnumberedInsideCircle = !board.IsDefendingObjective &&
                                                           board.EnemyUnitsInsideObjective > 0 &&
                                                           board.FriendlyUnitsInsideObjective <= board.EnemyUnitsInsideObjective &&
                                                           dToObjSq <= 65f * 65f;
                    bool attackerCloseEmptyCircleBreach = !board.IsDefendingObjective &&
                                                          board.FriendlyUnitsInsideObjective == 0 &&
                                                          dToObjSq <= 45f * 45f;
                    if (defenderPointBeingCapped ||
                        urgentAttackerProgressOrTickets ||
                        attackerOutnumberedInsideCircle ||
                        attackerCloseEmptyCircleBreach)
                    {
                        ws |= GoapStateBits.ObjectiveCapContestUrgent;
                    }
                }
            }

            // Tier 1 Individual "Oh Shit!" Break-Contact / Fallback State:
            // Triggered when an active individual fallback is underway, OR when caught close to an enemy tank (<= 34m)
            // with zero usable AT capability (and no nearby fallen AT launcher/grenade to grab!), OR when isolated/remnant
            // under heavy suppression and outnumbered!
            if (VisionConfig.EnableTacticalRetreatsAndRegroup.Value &&
                !isBailedVehicleCrew &&
                !(soldier.IsSquadLeader() && board.AliveMemberCount >= 2) &&
                !soldier.isBleeding &&
                !isOnBridge &&
                !inPointBlankMelee &&
                !desperateCapHold)
            {
                IntPtr sPtr = soldier.Pointer;
                if (h != null && h.MoveOverride.Kind == AiCore.BotMoveOverrideKind.EmergencyFallback && now < h.MoveOverride.ExpireTime)
                {
                    ws |= GoapStateBits.OverwhelmedNeedFallback;
                }
                else if (h == null || now >= h.Goap.NextIndividualFallbackTime)
                {
                    bool canGrabNearbyAtWeapon = suppression < 62.0f && HasPendingAtWeaponScavenge(sPtr, myPos, 22.0f);
                    Vehicle indivThreatTank = (board.ThreatIsArmor && board.PrimaryTarget != null)
                        ? board.PrimaryTarget.TryCast<Vehicle>()
                        : null;
                    bool helplessAgainstCloseTank =
                        !canGrabNearbyAtWeapon &&
                        ((ws & GoapStateBits.ArmorThreatClose) != 0 ||
                         (board.HasActiveThreat && board.ThreatIsArmor && dToThreatSq <= 34.0f * 34.0f)) &&
                        !HasUsableAntiTankCapability(soldier) &&
                        (indivThreatTank == null || IsTankThreateningPosition(indivThreatTank, myPos, 52.0f));

                    bool isolatedAndOverwhelmed =
                        ((ws & GoapStateBits.IsEntirelyAlone) != 0 || board.AliveMemberCount <= 2) &&
                        suppression >= 34.0f &&
                        board.NearbyEnemyCount42m >= board.NearbyFriendlyCount42m + 3 &&
                        !IsTrueGyokusaiOrBanzaiCondition(in factionProfile, soldier.joinedSquad, in board);

                    if (helplessAgainstCloseTank || isolatedAndOverwhelmed)
                    {
                        ws |= GoapStateBits.OverwhelmedNeedFallback;
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
            bool isBailedCrew = IsSoldierBailedOutVehicleCrew(soldier, soldier.joinedSquad);
            bool isMedic = AiCore.IsMedicSoldierOrPersonality(soldier, personality);
            bool canMove = AiCore.CanBotMoveAutonomously(soldier);
            return PlanBestGoapActionChainCore(
                ws,
                soldier.Pointer,
                soldier.faction,
                canMove,
                isMedic,
                isBailedCrew,
                ref board,
                personality,
                vet01,
                now,
                prevActiveAction);
        }

        internal static BotGoapPlan PlanBestGoapActionChainCore(
            GoapStateBits ws,
            IntPtr soldierPtr,
            string faction,
            bool canMove,
            bool isMedic,
            bool isBailedCrew,
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
            bool nonBleedingBailedCrew =
                isBailedCrew &&
                board.AliveMemberCount <= 5 &&
                (ws & (GoapStateBits.IsBleeding | GoapStateBits.InPointBlankRange | GoapStateBits.CanFireAtVisibleEnemy)) == 0 &&
                (ws & GoapStateBits.IsEntirelyAlone) != 0;

            bool bleedingInCoverMustFightFirst =
                (ws & (GoapStateBits.IsBleeding | GoapStateBits.BandageUnsafeUnderFire | GoapStateBits.InCover)) ==
                (GoapStateBits.IsBleeding | GoapStateBits.BandageUnsafeUnderFire | GoapStateBits.InCover) &&
                (ws & (GoapStateBits.CanFireAtVisibleEnemy | GoapStateBits.InPointBlankRange)) != 0;

            GoapStateBits desiredGoalBit;
            if (nonBleedingBailedCrew)
            {
                // Bailed-out vehicle crews bolt away from the burning wreck toward friendly lines / infantry
                // instead of huddling behind a 1-slot sapling next to the knocked-out hull!
                desiredGoalBit = GoapStateBits.SquadSupported;
            }
            else if (bleedingInCoverMustFightFirst)
            {
                // Bleeding soldier already in cover with an active shooter on top of him keeps his rifle up
                // to neutralize the attacker BEFORE holstering his weapon to bandage!
                desiredGoalBit = GoapStateBits.TargetEliminated;
            }
            else if ((ws & (GoapStateBits.IsBleeding | GoapStateBits.OverwhelmedNeedFallback | GoapStateBits.ArmorThreatClose | GoapStateBits.ExposedSideOfWall)) != 0 ||
                     ((ws & (GoapStateBits.IsSuppressed | GoapStateBits.IsHeavilySuppressed | GoapStateBits.NearFreshCasualty)) != 0 &&
                      (ws & GoapStateBits.InCover) == 0))
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
            else if ((ws & GoapStateBits.NeedAmmoOrMedScavenge) != 0)
            {
                desiredGoalBit = GoapStateBits.SquadSupported;
            }
            else if ((ws & GoapStateBits.IsEntirelyAlone) != 0 && (ws & GoapStateBits.CanFireAtVisibleEnemy) == 0)
            {
                desiredGoalBit = GoapStateBits.SquadSupported;
            }
            else if ((ws & GoapStateBits.ComradeNeedsLiberation) != 0 && (ws & GoapStateBits.HasVisibleEnemy) == 0)
            {
                desiredGoalBit = GoapStateBits.SquadSupported;
            }
            else if ((ws & GoapStateBits.SquadHasActiveThreat) != 0)
            {
                // When a visible enemy has closed inside < 16m CQB shooting range (CanFireAtVisibleEnemy == 1 && CanProvideOverwatch == 0),
                // EVERY soldier prioritizes neutralizing the close attacker directly rather than area suppression!
                bool closeCqbDirectFire = (ws & GoapStateBits.CanFireAtVisibleEnemy) != 0 &&
                                          (ws & GoapStateBits.CanProvideOverwatch) == 0;
                bool isDedicatedSupportRole =
                    !closeCqbDirectFire &&
                    board.AliveMemberCount >= 3 &&
                    !(board.IsDefendingObjective && board.ObjectiveCaptureProgress >= 0.65f) &&
                    (isMedic ||
                     (ws & GoapStateBits.IsRadioman) != 0 ||
                     ((ws & GoapStateBits.ObjectiveCapContestUrgent) == 0 && personality == SoldierPersonality.Leader_Tactician));

                bool isBaseOfFireOrPinRole =
                    !closeCqbDirectFire &&
                    !isDedicatedSupportRole &&
                    board.AliveMemberCount >= 3 &&
                    !(board.IsDefendingObjective && board.ObjectiveCaptureProgress >= 0.65f) &&
                    ((ws & GoapStateBits.IsMachineGunner) != 0 ||
                     personality == SoldierPersonality.Gunner_Anchor ||
                     personality == SoldierPersonality.Gunner_Overwatch ||
                     ((ws & GoapStateBits.ObjectiveCapContestUrgent) == 0 && board.NetDirective == SquadNetDirective.Anvil_PinAndCallSupport) ||
                     ((ws & GoapStateBits.AllyPinningThreat) == 0 && (ws & GoapStateBits.CanProvideOverwatch) != 0));

                desiredGoalBit = isDedicatedSupportRole
                    ? GoapStateBits.SquadSupported
                    : (isBaseOfFireOrPinRole ? GoapStateBits.TargetPinned : GoapStateBits.TargetEliminated);
            }
            else
            {
                desiredGoalBit = GoapStateBits.TargetEliminated;
            }

            GoapActionId bestStep0 = GoapActionId.None;
            GoapActionId bestStep1 = GoapActionId.None;
            float bestCost = 999f;
            FactionCommandProfile factionProfile = GetFactionCommandProfile(faction);
            BotMicroVariance micro = BotMicroVariance.FromSoldier(soldierPtr);
            bool isFragChainSpecialist = personality == SoldierPersonality.Assaulter_Breacher ||
                                         personality == SoldierPersonality.Rifleman_Grenadier ||
                                         personality == SoldierPersonality.AT_Demolitionist;

            int actionCount = _goapActions.Length;
            for (int i = 0; i < actionCount; i++)
            {
                GoapActionDef a0 = _goapActions[i];
                if (!canMove && IsMovementGoapAction(a0.Id))
                    continue;
                if ((ws & a0.Required) != a0.Required || (ws & a0.Forbidden) != 0)
                    continue;

                float cost0 = GetPersonalityAndDirectiveAdjustedCost(a0, ws, soldierPtr, ref board, personality, vet01, now, ref factionProfile, ref micro);
                // Plan Commitment Hysteresis (-0.35f inertia bonus) prevents stuttering/flip-flopping between nearly-tied actions
                if (a0.Id == prevActiveAction)
                    cost0 = Mathf.Max(0.05f, cost0 - 0.35f);

                GoapStateBits wsAfter0 = (ws | a0.Add) & ~a0.Remove;
                // A soldier cannot pin the threat for HIMSELF while simultaneously bounding/flanking in Step 1!
                // Strip self-generated AllyPinningThreat on sustained fire actions so 2-step self-pinning loops never form.
                if ((ws & GoapStateBits.AllyPinningThreat) == 0 &&
                    (a0.Id == GoapActionId.SuppressThreatPosition || a0.Id == GoapActionId.HoldOverwatchFire))
                {
                    wsAfter0 &= ~GoapStateBits.AllyPinningThreat;
                }

                if ((wsAfter0 & desiredGoalBit) != 0)
                {
                    // Allow 2-step tactical chains to compete cleanly when Step 0 leaves a core sub-objective unfinished:
                    //  1) MoveToFlankCover -> FlushThreatWithFrag (grenadiers / Hammer flankers vs ducked targets)
                    //  2) SeekEmergencyCover / RepositionBehindWall / ScavengeSupplyFromCorpse -> BandageInCover (when bleeding)
                    //  3) ThrowSmokeScreen -> MoveToFlankCover / BoundWithOverwatch / Fallback (when smoking to mask a move)
                    bool canChainFlankFrag = a0.Id == GoapActionId.MoveToFlankCover &&
                                             (ws & GoapStateBits.HasFragGrenade) != 0 &&
                                             (isFragChainSpecialist || board.NetDirective == SquadNetDirective.Hammer_WideFlankEnvelopment);
                    bool canChainCoverBandage = (a0.Id == GoapActionId.SeekEmergencyCover ||
                                                 a0.Id == GoapActionId.RepositionBehindWall ||
                                                 a0.Id == GoapActionId.ScavengeSupplyFromCorpse) &&
                                                (ws & GoapStateBits.IsBleeding) != 0 &&
                                                (ws & (GoapStateBits.BandageUnsafeUnderFire | GoapStateBits.ArmorThreatClose)) == 0;
                    bool canChainSmokePush = a0.Id == GoapActionId.ThrowSmokeScreen &&
                                             (ws & GoapStateBits.HasSmokeGrenade) != 0 &&
                                             ((ws & GoapStateBits.AllyPinningThreat) == 0 || (ws & GoapStateBits.OverwhelmedNeedFallback) != 0);

                    float oneStepCost = cost0;
                    if (canChainCoverBandage && (ws & GoapStateBits.HasBandage) != 0)
                        oneStepCost += 0.35f;
                    else if (canChainSmokePush && (ws & GoapStateBits.OverwhelmedNeedFallback) != 0)
                        oneStepCost += 0.35f;
                    else if (canChainFlankFrag && (ws & GoapStateBits.TargetLostBehindCover) != 0)
                        oneStepCost += 0.20f;

                    if (oneStepCost < bestCost)
                    {
                        bestCost = oneStepCost;
                        bestStep0 = a0.Id;
                        bestStep1 = GoapActionId.None;
                    }

                    if (!canChainFlankFrag && !canChainCoverBandage && !canChainSmokePush)
                        continue;
                }

                for (int j = 0; j < actionCount; j++)
                {
                    if (i == j)
                        continue;

                    GoapActionDef a1 = _goapActions[j];
                    if (!canMove && IsMovementGoapAction(a1.Id))
                        continue;
                    if ((wsAfter0 & a1.Required) != a1.Required || (wsAfter0 & a1.Forbidden) != 0)
                        continue;

                    GoapStateBits wsAfter1 = (wsAfter0 | a1.Add) & ~a1.Remove;
                    if ((wsAfter1 & desiredGoalBit) == 0)
                        continue;

                    float cost1 = GetPersonalityAndDirectiveAdjustedCost(a1, wsAfter0, soldierPtr, ref board, personality, vet01, now, ref factionProfile, ref micro);
                    // Synergy bonuses for intentional 2-step tactical chains (clamped >= 0.10f so Step 1 never drives totalCost below cost0!):
                    if (a0.Id == GoapActionId.MoveToFlankCover && a1.Id == GoapActionId.FlushThreatWithFrag)
                        cost1 -= 1.45f;
                    else if ((a0.Id == GoapActionId.SeekEmergencyCover ||
                              a0.Id == GoapActionId.RepositionBehindWall ||
                              a0.Id == GoapActionId.ScavengeSupplyFromCorpse) &&
                             a1.Id == GoapActionId.BandageInCover)
                        cost1 -= 0.85f;
                    else if (a0.Id == GoapActionId.ThrowSmokeScreen &&
                             (a1.Id == GoapActionId.MoveToFlankCover ||
                              a1.Id == GoapActionId.BoundWithOverwatch ||
                              a1.Id == GoapActionId.TacticalFallbackRetreat ||
                              a1.Id == GoapActionId.LoneWolfLinkUpOrAmbush))
                        cost1 -= 1.15f;

                    cost1 = Mathf.Max(0.10f, cost1);
                    float totalCost = cost0 + cost1;
                    if (totalCost < bestCost)
                    {
                        bestCost = totalCost;
                        bestStep0 = a0.Id;
                        bestStep1 = a1.Id;
                    }
                }
            }

            // Fallback Goal Bridge: if TargetEliminated/SurvivedCrisis had no valid path during an active firefight
            // (e.g. on a narrow bridge, pinned without flank cover, or bleeding in cover with a close shooter right around
            // the corner blocking bandaging), fall back to DirectFire / Overwatch / Suppress so a soldier never stalls!
            if (bestStep0 == GoapActionId.None)
            {
                if ((ws & GoapStateBits.CanFireAtVisibleEnemy) != 0)
                {
                    bestStep0 = GoapActionId.DirectFireEngagement;
                }
                else if ((ws & GoapStateBits.SquadHasActiveThreat) != 0 &&
                         ((ws & GoapStateBits.IsBleeding) == 0 || (ws & GoapStateBits.BandageUnsafeUnderFire) != 0))
                {
                    bestStep0 = (ws & GoapStateBits.CanProvideOverwatch) != 0
                        ? GoapActionId.HoldOverwatchFire
                        : GoapActionId.SuppressThreatPosition;
                }
            }

            return new BotGoapPlan
            {
                LastGoalBit = desiredGoalBit,
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
            IntPtr soldierPtr,
            ref SquadBlackboard board,
            SoldierPersonality personality,
            float vet01,
            float now,
            ref FactionCommandProfile factionProfile,
            ref BotMicroVariance micro)
        {
            float cost = def.BaseCost;
            SquadNetDirective netDirective = board.NetDirective;
            bool urgentObjPush = (ws & GoapStateBits.ObjectiveCapContestUrgent) != 0;
            bool underFireOrNearCasualty = (ws & (GoapStateBits.IsSuppressed | GoapStateBits.IsHeavilySuppressed | GoapStateBits.NearFreshCasualty)) != 0;
            bool advanceMaskedOrPinned = (ws & GoapStateBits.AllyPinningThreat) != 0 || now < board.SmokeActiveUntil;
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
                    if (urgentObjPush && (!underFireOrNearCasualty || advanceMaskedOrPinned))
                        cost -= 0.55f;
                    else if (underFireOrNearCasualty && !advanceMaskedOrPinned)
                        cost += 0.55f;
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
                    bool isFragSpecialist = personality == SoldierPersonality.Assaulter_Breacher ||
                                            personality == SoldierPersonality.Rifleman_Grenadier ||
                                            personality == SoldierPersonality.AT_Demolitionist;
                    if ((ws & GoapStateBits.CanFireAtVisibleEnemy) != 0 && !isFragSpecialist)
                        cost += 10.0f;
                    if (netDirective == SquadNetDirective.Hammer_WideFlankEnvelopment)
                        cost -= 0.45f;
                    if (urgentObjPush)
                        cost -= 0.50f;
                    if (isFragSpecialist)
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
                    if (urgentObjPush || lowTicketConservation || underFireOrNearCasualty)
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
                    if (lowTicketConservation || (underFireOrNearCasualty && !advanceMaskedOrPinned))
                        cost -= 0.35f;
                    if (personality == SoldierPersonality.Gunner_Anchor ||
                        personality == SoldierPersonality.Gunner_Overwatch ||
                        personality == SoldierPersonality.Gunner_AssaultMG)
                        cost -= 0.70f;
                    if ((ws & GoapStateBits.CanFireAtVisibleEnemy) == 0 &&
                        AiCore.TryGetSoldierHandles(soldierPtr, out AiCore.SoldierFastHandles supCostH) &&
                        supCostH.Ref != null &&
                        supCostH.Ref.IsTowardWall())
                    {
                        cost += 2.25f;
                    }
                    break;

                case GoapActionId.HoldOverwatchFire:
                    cost *= factionProfile.OverwatchHoldCostMult * micro.CautionCostMult;
                    if (netDirective == SquadNetDirective.Anvil_PinAndCallSupport)
                        cost -= 0.75f;
                    // When objective is being back-capped or needs an overtime cap push, penalize sitting outside the cap radius
                    // UNLESS incoming fire / fresh casualties require pinning fire first!
                    if (urgentObjPush && (!underFireOrNearCasualty || advanceMaskedOrPinned))
                        cost += 0.65f;
                    else if (lowTicketConservation || underFireOrNearCasualty)
                        cost -= 0.35f;
                    if (personality == SoldierPersonality.Gunner_Anchor ||
                        personality == SoldierPersonality.Gunner_Overwatch ||
                        personality == SoldierPersonality.Gunner_AssaultMG)
                        cost -= 0.65f;
                    break;

                case GoapActionId.BoundWithOverwatch:
                    cost *= factionProfile.FlankMoveCostMult * micro.AggressionCostMult;
                    if (netDirective == SquadNetDirective.Hammer_WideFlankEnvelopment)
                        cost -= 0.45f;
                    else if (netDirective == SquadNetDirective.Anvil_PinAndCallSupport && !urgentObjPush)
                        cost += 0.75f;
                    // Self-Preservation Priority #1: never blindly jog into unpinned, unsmoked fire or over a fresh comrade's body!
                    if (underFireOrNearCasualty && !advanceMaskedOrPinned)
                    {
                        cost += 1.15f;
                        IndividualSelfPreservationBalksCount++;
                    }
                    else if (urgentObjPush)
                    {
                        cost -= 0.75f;
                    }
                    if (!urgentObjPush &&
                        board.HasActiveThreat &&
                        AiCore.TryGetSoldierHandles(soldierPtr, out AiCore.SoldierFastHandles bndH) &&
                        bndH.Tr != null &&
                        (bndH.Tr.position - board.ThreatPosition).sqrMagnitude <= 16.0f * 16.0f)
                    {
                        cost += 2.50f;
                    }
                    if (personality == SoldierPersonality.Assaulter_Shock ||
                        personality == SoldierPersonality.Assaulter_Breacher ||
                        personality == SoldierPersonality.Assaulter_Skirmisher ||
                        personality == SoldierPersonality.Rifleman_Grenadier ||
                        personality == SoldierPersonality.Gunner_AssaultMG)
                        cost -= 0.45f;
                    else if (personality == SoldierPersonality.Medic_Heroic ||
                             personality == SoldierPersonality.Medic_Combat ||
                             personality == SoldierPersonality.Medic_Triage)
                        cost += 1.25f;
                    else if (personality == SoldierPersonality.Leader_Tactician && !urgentObjPush)
                        cost += 0.45f;
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
                    cost *= factionProfile.ComradeAidAndDragMult * micro.AggressionCostMult;
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
                    cost *= factionProfile.ScavengeAndResupplyMult * micro.CautionCostMult;
                    if (lowTicketConservation)
                        cost -= 0.40f;
                    // Urgently prioritize grabbing a nearby fallen Bazooka/Panzerfaust/AT grenade when enemy armor is closing in,
                    // while heavily penalizing any non-AT scavenging until out of immediate armor danger!
                    if ((ws & GoapStateBits.ArmorThreatClose) != 0)
                    {
                        bool isAtPickup = AiCore.TryGetSoldierHandles(soldierPtr, out AiCore.SoldierFastHandles scH) &&
                                          scH.Goap.Scavenge.IsAtWeaponPickup;
                        cost += isAtPickup ? -0.70f : 1.50f;
                    }
                    // Prioritize inheriting a fallen radio pack when the squad is cut off from its Radioman!
                    if (!board.PreviouslyHadAliveRadioman && !factionProfile.AllowDisconnectedLocalFlank)
                        cost -= 0.45f;
                    break;

                case GoapActionId.DirectFireEngagement:
                    cost *= micro.AggressionCostMult;
                    if ((ws & GoapStateBits.IsSuppressed) != 0)
                        cost += 0.45f;
                    if (netDirective == SquadNetDirective.Hammer_WideFlankEnvelopment)
                        cost += 0.50f;
                    if (urgentObjPush)
                        cost += 0.60f;
                    break;

                case GoapActionId.TacticalFallbackRetreat:
                    cost *= micro.CautionCostMult;
                    if (netDirective == SquadNetDirective.Regroup_SectorWithdrawal)
                        cost -= 0.45f;
                    if ((ws & GoapStateBits.ArmorThreatClose) != 0)
                        cost -= 0.40f;
                    if (personality == SoldierPersonality.Rifleman_Cautious ||
                        personality == SoldierPersonality.Medic_Triage ||
                        personality == SoldierPersonality.Leader_OldGuard)
                        cost -= 0.35f;
                    else if (personality == SoldierPersonality.Assaulter_Shock)
                        cost += 0.25f;
                    break;
            }

            return Mathf.Max(0.1f, cost - vet01 * 0.15f);
        }
    }
}
