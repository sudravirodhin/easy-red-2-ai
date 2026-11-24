using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace EasyRed2HumanAI
{
    public static partial class VeteranAndTacticsPatches
    {
        // =====================================================================
        // SQUAD BLACKBOARD, NET DIRECTIVES & MICRO-VARIANCE SUPPORT TYPES
        // (Decision & planning authority lives in EmergentDecisionCore + EmergentRuntimeBridge)
        // =====================================================================

        internal enum SquadNetDirective : byte
        {
            Disconnected_Autonomous = 0,
            Connected_StandardObjective = 1,
            Anvil_PinAndCallSupport = 2,
            Hammer_WideFlankEnvelopment = 3,
            Regroup_SectorWithdrawal = 4
        }

        internal struct SquadBlackboard
        {
            public float NextRefreshTime;
            public bool IsConnectedToRadioNet;
            public bool PreviouslyHadAliveRadioman;
            public float LastRadiomanLostTime;
            public byte RadioNetToastInitState;
            public float RadioRelayPendingFlipSince;
            public float NextRadioNetStatusToastTime;
            public bool PreviouslyHadFootRadioman;
            public float FirstSeenTime;
            public bool PlayerWasInSquad;
            public float PlayerJoinedSquadTime;
            public bool EverNotifiedRadioCutoff;
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
            public int CloserFriendlyUnitsToObjective;
            public Vector3 SquadCenter;
            public Vector3 ThreatPosition;
            public Vector3 ThreatDirection;
            public Spottable PrimaryTarget;
            public Vector3 LastKnownThreatPos;
            public float LastKnownThreatTime;

            // Macro Objective & Ticket / Attrition Context
            public bool IsInvaderFaction;
            public float TicketRatio01;
            public bool HasActiveObjectiveTask;
            public bool IsDefendingObjective;
            public Vector3 ObjectivePosition;
            public float ObjectiveCaptureProgress;
            public IntPtr PrevObjectiveTaskPtr;
            public float PrevObjectiveCaptureProgress;
            public float ObjectiveTickingAgainstUntil;

            public bool IsObjectiveTickingAgainstUs(float now) => now < ObjectiveTickingAgainstUntil;

            // Tier 3 Team Net Directive assigned to this Squad Leader
            public SquadNetDirective NetDirective;
            public float NetDirectiveExpireTime;
            public Vector3 NetDirectiveTargetPos;

            public bool IsOuterScreenSquad;
            public Vector3 OuterScreenAnchor;
            public float OuterScreenUntil;

            // Zero-Raycast Casualty / Fatal-Funnel Hazard Memory
            public Vector3 LastCasualtyPos;
            public float LastCasualtyTime;
            public float DecayedRecentDeaths;
            public float LastDeathDecayUpdateTime;
            public IntPtr LastAliveLeaderPtr;
            public float LeaderShockUntil;
            public float LeaderShockPeak;
            public float LeaderBraveryAura;
            public bool LeaderRoseToOccasion;
            public bool PendingLeaderSuccession;

            // Tier 2 / Tier 3 Squad Physical Order State & Live Supply Flags
            public bool WasHoldingForFriendlyBarrage;
            public float BarrageSurgeUntil;
            public Vector3 StallAnchorCenter;
            public float StallAnchorTime;
            public int ConsecutiveStallCount;
            public float NextRelayCohesionOrderTime;
            public float NextSquadUnstallTime;
            public float NextCoverSectorReorientTime;
            public bool IsAiCovertHoldFireActive;
            public float AiCovertHoldFireExpireTime;
            public float NextAiCovertOrderTime;
            public float AiFollowColumnUntil;
            public float NextAiFollowOrderTime;
            public bool IsAiChargeOrdered;
            public float AiChargeBaselineDeaths;
            public float NextAiSquadChargeTime;
            public float AiSquadRetreatUntil;
            public float NextAiSquadRetreatTime;
            public float NextHammerOrderTime;
            public IntPtr PincerOrderedTaskPtr;
            public float PincerOrderedUntil;
            public bool SquadHasSmokeAvailable;
            public bool SquadOutOfAmmo;

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

            public IntPtr DragRescuerPtr;
            public IntPtr DragPatientPtr;
            public float DragClaimUntil;

            public IntPtr AcousticInvestigatorPtr;
            public float AcousticInvestigateUntil;

            public bool IsThreatBeingPinned(float now)
            {
                return now < SuppressHoldUntil && (Suppressor0Ptr != IntPtr.Zero || Suppressor1Ptr != IntPtr.Zero);
            }
        }

        internal static readonly Dictionary<IntPtr, float> _nextSquadVoiceTime = new Dictionary<IntPtr, float>(64);

        internal struct BotGoapPlan
        {
            public float PlanExpireTime;
            public bool LockUnitCoverChange;
            public bool HasMoveOverride;
            public bool IsEvacuatingBailedCrew;
            public Vector3 MoveOverrideDest;
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
            public float LeaderAuraMult;              // Multiplier on SL/NCO Bravery Aura (default 1.0f; boosted by Order No. 227 / Bastogne)
            public float SurrenderSuppDelta;          // Additive shift on shell-shock surrender suppression requirement (positive = more steadfast)

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
                AttritionPivotThreshold = 7.5f,
                LeaderAuraMult = 1.0f,
                SurrenderSuppDelta = 0f
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

            // 9 Historical Battle Atmosphere Archetypes (bounded +/-10-25% theater modifiers)
            if ((VisionConfig.EnableBattleAtmosphereDirectives == null || VisionConfig.EnableBattleAtmosphereDirectives.Value) &&
                theater.Atmosphere != AiCore.BattleAtmosphereArchetype.None)
            {
                bool isInv = !string.IsNullOrEmpty(theater.InvadersFactionId) &&
                             AiCore.AreSameSideFactions(faction, theater.InvadersFactionId);
                bool isDef = !string.IsNullOrEmpty(theater.DefendersFactionId) &&
                             AiCore.AreSameSideFactions(faction, theater.DefendersFactionId);

                switch (theater.Atmosphere)
                {
                    case AiCore.BattleAtmosphereArchetype.Order227NotOneStepBack:
                        if (f == "Ussr")
                        {
                            p.LeaderAuraMult *= 1.35f;
                            p.SurrenderSuppDelta += 14f;
                            p.SuppressionThresholdBase = Mathf.Min(26f, p.SuppressionThresholdBase + 3f);
                            p.AttritionPivotThreshold = Mathf.Min(13f, p.AttritionPivotThreshold * 1.18f);
                            p.FragFlushCostMult *= 0.86f;
                            p.BayonetBreachCostMult *= 0.88f;
                            p.GarrisonSquadRatio = Mathf.Min(0.34f, p.GarrisonSquadRatio + 0.06f);
                        }
                        break;

                    case AiCore.BattleAtmosphereArchetype.KesselEncircledPocket:
                        if (isDef)
                        {
                            p.ScavengeAndResupplyMult *= 0.78f;
                            p.GarrisonSquadRatio = Mathf.Min(0.38f, p.GarrisonSquadRatio + 0.08f);
                            p.EmplacementSearchMult *= 1.16f;
                            p.OverwatchHoldCostMult *= 0.88f;
                            p.AttritionPivotThreshold = Mathf.Min(12.5f, p.AttritionPivotThreshold * 1.15f);
                        }
                        break;

                    case AiCore.BattleAtmosphereArchetype.UrbanFortress:
                        p.GarrisonSquadRatio = Mathf.Min(0.38f, p.GarrisonSquadRatio + (isDef ? 0.08f : 0.04f));
                        p.FragFlushCostMult *= 0.82f;
                        p.BayonetBreachCostMult *= 0.88f;
                        p.FlankLateralBias = Mathf.Max(0.72f, p.FlankLateralBias - 0.04f);
                        if (isDef)
                        {
                            p.OverwatchHoldCostMult *= 0.86f;
                            p.SurrenderSuppDelta += 6f;
                        }
                        break;

                    case AiCore.BattleAtmosphereArchetype.AmphibiousBeachhead:
                        if (isInv)
                        {
                            p.SmokeCostMult *= 0.76f;
                            p.SuppressionThresholdBase = Mathf.Min(25f, p.SuppressionThresholdBase + 2.5f);
                            p.FlankMoveCostMult *= 0.86f;
                        }
                        if (isDef)
                        {
                            p.EmplacementSearchMult *= 1.24f;
                            p.GarrisonSquadRatio = Mathf.Min(0.36f, p.GarrisonSquadRatio + 0.06f);
                            p.OverwatchHoldCostMult *= 0.84f;
                        }
                        break;

                    case AiCore.BattleAtmosphereArchetype.SiegeOfBastogne:
                        if (isDef)
                        {
                            p.GarrisonSquadRatio = Mathf.Min(0.38f, p.GarrisonSquadRatio + 0.08f);
                            p.ComradeAidAndDragMult *= 0.82f;
                            p.ScavengeAndResupplyMult *= 0.82f;
                            p.LeaderAuraMult *= 1.22f;
                            p.SurrenderSuppDelta += 10f;
                        }
                        if (isInv)
                        {
                            p.FlankMoveCostMult *= 0.86f;
                            p.FlankLateralBias = Mathf.Min(0.92f, p.FlankLateralBias + 0.05f);
                            p.SmokeCostMult *= 0.86f;
                        }
                        break;

                    case AiCore.BattleAtmosphereArchetype.PacificBunkerAndNightGyokusai:
                        if (f == "Japan" || isDef)
                        {
                            p.EmplacementSearchMult *= 1.22f;
                            p.OverwatchHoldCostMult *= 0.82f;
                            p.PreferAmbushWhenAlone = true;
                            p.GarrisonSquadRatio = Mathf.Min(0.34f, p.GarrisonSquadRatio + 0.06f);
                            if (f == "Japan")
                            {
                                p.AllowGyokusaiDesperation = true;
                                p.BayonetBreachCostMult *= 0.88f;
                                p.SurrenderSuppDelta += 12f;
                            }
                        }
                        if (isInv)
                        {
                            p.FragFlushCostMult *= 0.84f;
                            p.SmokeCostMult *= 0.84f;
                        }
                        break;

                    case AiCore.BattleAtmosphereArchetype.MountainRidgeGrind:
                        p.SuppressCostMult *= 0.84f;
                        p.OverwatchHoldCostMult *= 0.84f;
                        p.SmokeCostMult *= 0.78f;
                        p.EmergencyCoverCostMult *= 0.88f;
                        break;

                    case AiCore.BattleAtmosphereArchetype.BlitzkriegSchwerpunkt:
                        if (isInv)
                        {
                            p.FlankMoveCostMult *= 0.84f;
                            p.AttritionPivotThreshold = Mathf.Max(4.5f, p.AttritionPivotThreshold * 0.84f);
                            p.SmokeCostMult *= 0.86f;
                            p.GarrisonSquadRatio = Mathf.Max(0.12f, p.GarrisonSquadRatio - 0.04f);
                        }
                        if (isDef)
                        {
                            p.EmplacementSearchMult *= 1.22f;
                            p.OverwatchHoldCostMult *= 0.88f;
                        }
                        break;

                    case AiCore.BattleAtmosphereArchetype.NightAirborneCoupDeMain:
                        p.AllowDisconnectedLocalFlank = true;
                        p.PreferAmbushWhenAlone = true;
                        p.FlankMoveCostMult *= 0.84f;
                        p.BayonetBreachCostMult *= 0.86f;
                        p.FragFlushCostMult *= 0.88f;
                        break;
                }

                p.SmokeCostMult = Mathf.Max(0.45f, p.SmokeCostMult);
                p.FragFlushCostMult = Mathf.Max(0.55f, p.FragFlushCostMult);
                p.FlankMoveCostMult = Mathf.Max(0.55f, p.FlankMoveCostMult);
                p.OverwatchHoldCostMult = Mathf.Max(0.60f, p.OverwatchHoldCostMult);
                p.SuppressCostMult = Mathf.Max(0.55f, p.SuppressCostMult);
                p.BayonetBreachCostMult = Mathf.Max(0.50f, p.BayonetBreachCostMult);
                p.ScavengeAndResupplyMult = Mathf.Max(0.55f, p.ScavengeAndResupplyMult);
                p.ComradeAidAndDragMult = Mathf.Max(0.55f, p.ComradeAidAndDragMult);
                p.EmergencyCoverCostMult = Mathf.Max(0.60f, p.EmergencyCoverCostMult);
                p.EmplacementSearchMult = Mathf.Min(1.55f, p.EmplacementSearchMult);
                p.LeaderAuraMult = Mathf.Clamp(p.LeaderAuraMult, 0.75f, 1.45f);
            }

            _factionProfileCache[faction] = p;
            return p;
        }

        public static int GoapPlansExecutedCount { get; private set; }
        public static int GoapPinAndFlankCount { get; private set; }
        public static int GoapSmokeScreensCount { get; private set; }
        public static int RadioNetCutoffsCount { get; private set; }
        public static int RoleWeaponUpgradesCount { get; private set; }
        public static int BuddyResuppliesCount { get; private set; }
        public static int FieldRadiosInheritedCount { get; private set; }
        public static int HelmetsRecoveredCount { get; private set; }
        public static int CasualtiesDraggedCount { get; private set; }
        public static int BotKillCalloutsCount { get; set; }
        public static int TacticalRetreatsCount { get; private set; }
        public static int SquadLeaderBalksCount { get; private set; }
        public static int FootBotUnsticksCount { get; private set; }
        public static int StuckSurrendersCount { get; private set; }

        internal static readonly Dictionary<IntPtr, float> _nextSquadRadioScavengeTime = new Dictionary<IntPtr, float>(64);

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
            public Vector3 RoomTrapAnchorPos;
            public float RoomTrapSince;
            public float CliffStepOffUntil;
            public bool SurrenderedFromTrap;
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
            _nextSquadRadioScavengeTime.Clear();
            ClearBarrageCoordinationCaches();
            FootBotUnsticksCount = 0;
            StuckSurrendersCount = 0;
            _nextGlobalBotWeaponPickupTime = 0f;
        }

    }
}
