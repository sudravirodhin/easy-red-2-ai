using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Corvostudio.Weapons;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Photon.Pun;
using UnityEngine;

namespace EasyRed2HumanAI
{
    /// <summary>
    /// Shared core dependency layer for EasyRed2HumanAI.
    /// Centralizes Photon Co-op ownership checks, global bot RPC rate-limiting,
    /// solid-environment raycasts (ResourcesManager.groundMask), overhead ceiling caching,
    /// deterministic hashing, and dictionary/hashset memory hygiene.
    /// </summary>
    public static partial class AiCore
    {
        private static int _cachedSolidEnvMask;
        private static int _cachedMovementObstacleMask;
        private static int _cachedSmokeOcclusionMask;
        private static IntPtr _cachedWaterInstancePtr;
        private static Renderer _cachedWaterRenderer;
        private static float _botRpcNormalTokens = 2.5f;
        private static float _botRpcNormalLastRefillTime;
        private static float _nextAllowedLowPriorityBotRpcTime;
        private static float _nextCoopNetHealthSampleTime;
        private static float _coopBaselineRttMs = -1f;
        private static int _lastSampledResentReliableCommands = -1;
        private static float _coopRecentResendsPerSec;
        private static Il2CppSystem.Object _propKeyPingObj;
        private static float _nextRoomCheckTime;
        private static int _cachedRoomPlayerCount = 1;
        private static IntPtr _lastPublishedRoomPtr;
        private static int _lastHeavyWorkFrame = -1;
        private static int _lastDirectorWorkFrame = -1;
        private static int _lastDebugLogWriteFrame = -1;
        [ThreadStatic]
        private static StringBuilder _sharedLogBuilder;
        private static readonly List<IntPtr> _sharedPruneBuffer = new List<IntPtr>(256);
        private static readonly Dictionary<IntPtr, Vehicle> _turretVehicleCache = new Dictionary<IntPtr, Vehicle>(64);
        private static readonly Dictionary<IntPtr, (IntPtr TurretPtr, Turret Obj)> _turretByAiPtr = new Dictionary<IntPtr, (IntPtr TurretPtr, Turret Obj)>(64);
        private static readonly Dictionary<IntPtr, TurretFastTraits> _turretTraitsCache = new Dictionary<IntPtr, TurretFastTraits>(96);
        private static readonly Dictionary<long, bool> _ceilingCache = new Dictionary<long, bool>(512);
        private static readonly Dictionary<long, bool> _bridgeCellCache = new Dictionary<long, bool>(512);
        private static readonly Dictionary<IntPtr, bool> _terrainColliderCache = new Dictionary<IntPtr, bool>(64);
        internal struct ColliderEnvMeta
        {
            public bool IsVehicle;
            public IntPtr RootPtr;
        }
        private static readonly Dictionary<IntPtr, ColliderEnvMeta> _vehicleColliderCache = new Dictionary<IntPtr, ColliderEnvMeta>(256);
        private static readonly Dictionary<int, ColliderEnvMeta> _vehicleColliderCacheById = new Dictionary<int, ColliderEnvMeta>(256);
        private static Il2CppSystem.Collections.Generic.List<AiDestination> _sharedOctreeCoverResults;
        public static SoldierFastHandles CachedLocalPlayerHandles;
        public static IntPtr CachedLocalPlayerSquadPtr;
        public struct LocalPlayerFastHandles
        {
            public PlayerController Controller;
            public Soldier Soldier;
            public SoldierFastHandles SoldierHandles;
            public Vehicle ControlledVehicle;
            public Squad Squad;
            public IntPtr SquadPtr;
            public string Faction;
            public bool HasControlledCharacter;
            public bool HasPlayer;
        }
        private static int _localPlayerCacheFrame = -1;
        private static LocalPlayerFastHandles _cachedLocalPlayerState;
        private static Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<RaycastHit> _envRayScratch;
        private static readonly Dictionary<string, float> _debugLogCooldowns = new Dictionary<string, float>(32);
        private static readonly Dictionary<int, (bool HasMod, float ExpireTime)> _playerModdedCache = new Dictionary<int, (bool HasMod, float ExpireTime)>(16);
        private static readonly List<Vector3> _cachedRemotePlayerPositions = new List<Vector3>(12);
        private static float _nextRemotePlayerPosScanTime;

        // =====================================================================
        // Zero-Allocation Faction Relation & Entity Fast-Handle Caches
        // Eliminates thousands of per-frame IL2CPP string marshaling calls
        // (ManagedStringToIl2Cpp / Il2CppStringToManaged) and finalizable
        // Il2CppObjectBase wrapper GCHandle allocations across 100+ bots.
        // =====================================================================
        public enum CachedDestKind : byte
        {
            None = 0,
            Other = 1,
            CombatCover = 2,
            VehicleRepair = 3,
            MedicHeal = 4,
            DestinationWithoutCover = 5
        }

        public sealed class SoldierFastHandles
        {
            public IntPtr Ptr;
            public IntPtr AiPtr;
            public int DiagId;
            public string CachedFullName = string.Empty;
            public Soldier Ref;
            public SoldierAI Ai;
            public Transform Tr;
            public Transform RootTr;
            public SyncSoldier Syncher;
            public bool SyncherChecked;
            public string Faction = string.Empty;
            public float NextFactionRefreshTime;

            public float CachedOpticZoom = 1.0f;
            public float NextOpticCheckTime;

            public float CachedCamoMultiplier = 1.0f;
            public float NextCamoCheckTime;

            public AttachmentBipod CachedBipod;
            public bool CachedIsGunner;
            public float NextBipodCheckTime;

            public IntPtr CachedDestPtr;
            public CachedDestKind DestKind;
            public float NextDestCheckTime;
            public CombatCover CachedCover;
            public VehicleRepairTask CachedRepairTask;
            public MedicHealingDestination CachedHealDest;
            public AiDestination CachedDest;
            public DestinationWithoutCover CachedNoCoverDest;

            public float NextMortarCheckTime;
            public bool CachedHasHeldMortar;
            public VehicleFastHandles CachedVehicleHandles;
            public float NextVehicleHandleCheckTime;

            public Squad CachedSquad;
            public float NextSquadRefreshTime;

            public int LastMoveTimeFrame = -1;
            public bool CachedIsMoveTimeResult;
            public int LastEndpointFrame = -1;
            public float NextEndpointCheckTime;
            public Vector3 CachedEndpoint;
            public int CachedNodesCount;
            public IntPtr CachedPathPtr;
            public A.d CachedPathObj;
            public float NextMoveDirectRedirectPathTime;
            public Vector3 LastRedirectedMoveDest;

            public float NextInventoryWeightAuditTime;
            public float PendingPickedWeaponRefillTime;
            public int PendingPickedWeaponSlot = -1;

            public float CreatedAtTime;
            public float LastSpawnOrientTime;
            public float NextEnclosedSeatCheckTime;
            public bool CachedIsButtonedArmoredCrew;
            public float EnemySyringeCaptureUntil;
            public bool IsRetreatDespawning;
            public float RetreatDespawnForceCleanupAt;
            public float LastCoopSpawnHealTime;
            public float SurrenderedSinceTime;
            public float UnliberatedWithNearbyAllySince;
            public Vector3 WatchdogAnchorPos;
            public float WatchdogStationarySince;
            public float NextAnomalyAlertTime;
            public Vector3 LastHumanActivePos;
            public float LastHumanActiveYaw;
            public float LastHumanActiveTime;
            public float NextWitnessReactTime;
            public bool InSmokeCached;
            public float NextInSmokeCheckTime;
            public float CachedVoiceBasePitch;
            public uint VoiceLineCounter;
            public LookParameters CachedLookRot;
            public bool LookRotChecked;
            public InventoryManager CachedInventory;
            public bool InventoryChecked;
            public float NextInventoryItemsCacheTime;
            public bool CachedHasBandage;
            public bool CachedHasSmoke;
            public bool CachedHasFrag;
            public bool CachedHasUsableSmallArms;
            public float NextCoopOrderRelayTime;

            internal BotMoveOverrideState MoveOverride;
            internal SoldierVeterancyState Veterancy;
            internal SoldierVisionState Vision;
            internal SoldierStanceState Stance;
            internal SoldierCombatState Combat;
            internal SoldierGoapState Goap;
            internal EmergentEngine.SoldierEmergentState Emergent;
        }

        internal enum BotMoveOverrideKind : byte
        {
            None = 0,
            GoapAction = 1,
            CasualtyDrag = 2,
            UnstickSidestep = 3,
            SapperStepBack = 4,
            EmergencyFallback = 5,
            VehicleEvade = 6,
            GrenadeReflex = 7
        }

        internal struct BotMoveOverrideState
        {
            public BotMoveOverrideKind Kind;
            public Vector3 Dest;
            public float ExpireTime;
        }

        internal struct SoldierVeterancyState
        {
            public VeteranAndTacticsPatches.SoldierRecord Record;
            public float NextPromotionCheckTime;
        }

        internal struct SoldierVisionState
        {
            public float LastFiredTime;
            public bool IsTurningToWatchSector;
            public float NextSpotterCalloutTime;
            public float NextSuppressiveGrenadeTime;
            public float NextSuppressiveBurstCountTime;
            public float NextCornerPieCountTime;
            public int LastMovementRoutineRenderFrame;
            public int LastUpdateMoveDirRenderFrame;
            public byte LastMovementRoutineCatchUpMode; // 0 = VanillaFallthrough, 1 = CommittedMoveSprint, 2 = CommittedMoveWalk, 3 = CornerPie
            public VeteranAndTacticsPatches.WatchWindowCache WatchWindow;
            public VeteranAndTacticsPatches.ThreatSectorCache ThreatSector;
            public VeteranAndTacticsPatches.LocalCalloutRecord LocalCallout;
            public VeteranAndTacticsPatches.LastKnownTargetInfo LastKnownTarget;
            public VeteranAndTacticsPatches.CornerPieState CornerPie;
            public VeteranAndTacticsPatches.StationaryWatchState StationaryWatch;
            public VeteranAndTacticsPatches.CoverEyeBlockState CoverEyeBlock;
            public Spottable UnderFireAttacker;
            public IntPtr UnderFireAttackerPtr;
            public Vector3 UnderFireFromPos;
            public float UnderFireUntil;
            public float UnderFireStartedAt;
            public float UnderFireWakeAt;
            public float LastAlertStartSeen;
            public float NextNearMissAlertTime;
            public float NextIncomingFireSuppressTime;
            public bool WasMovingLastWatchTick;
        }

        internal struct SoldierStanceState
        {
            public float StanceDropHoldFireUntil;
            public float NextStanceDropAllowed;
            public float CombatStanceHoldUntil;
            public float CombatCrouchLockUntil;
            public float NoProneUntil;
            public float NextWallCheckTime;
            public float WallBlockedUntil;
            public float PeekRestoreTime;
            public float NextPeekAllowedTime;
            public float PoseLockUntil;
            public float BipodStandUntil;
            public float NextBipodDeployCheckTime;
            public float NextWallSidestepTime;
            public float LocalCoverProtectedUntil;
            public float NextLocalCoverSeekTime;
            public float CoverSettledUntil;
            public float NextStandCrouchLofCheckTime;
            public float NextIndoorCeilingCountTime;
            public float BandageLowPoseUntil;
            public SoldierPose BandageTargetPose;
            public float NextStandLiftAttemptTime;
            public Soldier.LeanDirection DesiredLean;
            public float LeanHoldUntil;
            public float NextLeanProbeTime;
            public float NextCoverIntegrityCheckTime;
            public float NextVantageSeekTime;
            public float NoLocalCoverUntil;
            public float ExposedAimYOffset;
            public float ExposedAimUntil;
            public byte ZombieRecoveries;
            public float StealthCrouchUntil;
        }

        internal struct SoldierCombatState
        {
            public float NextFFCheckTime;
            public float FFBlockedUntil;
            public float NextAntiStackTime;
            public float NextVaultCheckTime;
            public float LiberateMoveStartTime;
            public float LiberateMoveCooldownUntil;
            public float NextBayonetCheckTime;
            public float NextMeleeAttemptTime;
            public float PriorityTargetLockUntil;
            public float NextMeleeStepTime;
            public float NextBoundingHoldCountTime;
            public float NextBridgeAssistCountTime;
            public float NextSurrenderCheckTime;
            public float NextBotDoorCheckTime;
            public float NextBandageBlockCountTime;
            public float NextMeleeSuppressCountTime;
            public float NextTankDisciplineCountTime;
            public float NextFriendlyStepAsideTime;
            public IntPtr LastBayonetCheckedGunPtr;
            public int LastMeleeEvalFrame;
            public bool CachedInPointBlankMelee;
            public Soldier CachedMeleeEnemy;
            public float CachedMeleeLungeRange;
            public float CachedMeleeCeaseFireRange;
            public float CachedMeleeStrikeRange;
            public int CachedMeleeSlotIndex;
            public IntPtr LastMeleeSwingTargetPtr;
            public float LastMeleeSwingTime;
            public byte ConsecutiveMeleeSwingsOnTarget;
            public byte ConsecutiveSlopeVaultRejects;
            public byte ConsecutiveNoFloorVaultRejects;
            public float MeleeWhiffFallbackUntil;
            public float NextAtCapCheckTime;
            public bool CachedHasUsableAt;
            public float NextDeadzoneNodeCheckTime;
            public VeteranAndTacticsPatches.FireAndMovementState FireAndMovement;
            public VeteranAndTacticsPatches.RotateAndFireFrameState RotateFireFrame;
            public VeteranAndTacticsPatches.InfantrySightReactionState SightReaction;
            public VeteranAndTacticsPatches.BarbedWireStuckState BarbedWireStuck;
            public VeteranAndTacticsPatches.VehicleAimCache VehicleAim;
            public IntPtr LastEvadedGrenadePtr;
            public float GrenadeReflexCooldownUntil;
            public Vector3 PendingSmokeAimPoint;
            public float PendingSmokeAimUntil;
            public IntPtr PendingSmokePrefabPtr;
            public float ThrowFireHoldUntil;
            public Vector3 PendingThrowFaceDir;
            public float PendingThrowFaceUntil;
            public float PointBlankFaceUntil;
            public Vector3 PointBlankFaceDir;
        }

        internal struct SoldierGoapState
        {
            public VeteranAndTacticsPatches.BotGoapPlan Plan;
            public VeteranAndTacticsPatches.BotScavengeCache Scavenge;
            public VeteranAndTacticsPatches.FootStuckState FootStuck;
            public VeteranAndTacticsPatches.CasualtyDragState CasualtyDrag;
            public float NextWeaponUpgradeAllowedTime;
            public float NextBuddyBandageRefillTime;
            public float NextBuddyAmmoResupplyTime;
            public float NextBotBoxOrCrateResupplyTime;
            public float NextSmokeGrenadeTime;
            public float NextSoldierVoiceTime;
            public float NextFlankCoverAssignTime;
            public float NextSpecialMunitionTime;
            public float NextIndividualFallbackTime;
            public float NextOpenScatterTime;
            public float MedicHealStartTime;
            public IntPtr MedicHealTargetPtr;
            public float ActiveSyringeHealUntil;
            public float MedicCloseApproachSince;
            public IntPtr MedicUnreachablePatientPtr;
            public float MedicUnreachableUntil;
            public IntPtr RemountTargetVehiclePtr;
            public float RemountAttemptStartedAt;
            public float RemountCooldownUntil;
            public float BailoutShockUntil;
            public Vector3 BailoutWreckPos;
        }

        internal static void SetBotMoveOverride(SoldierFastHandles h, BotMoveOverrideKind kind, Vector3 dest, float expireTime)
        {
            if (h == null)
                return;

            float now = Time.time;
            if (kind >= h.MoveOverride.Kind || now >= h.MoveOverride.ExpireTime)
            {
                if (h.MoveOverride.Kind != BotMoveOverrideKind.None &&
                    h.MoveOverride.Kind != kind &&
                    now < h.MoveOverride.ExpireTime)
                {
                    MoveOverridePreemptionsCount++;
                }

                h.MoveOverride = new BotMoveOverrideState
                {
                    Kind = kind,
                    Dest = dest,
                    ExpireTime = expireTime
                };
            }
        }

        internal static void ClearBotMoveOverride(SoldierFastHandles h, BotMoveOverrideKind kind)
        {
            if (h != null && h.MoveOverride.Kind == kind)
                h.MoveOverride = default;
        }

        internal static string GetMoveOverrideShortLabel(BotMoveOverrideKind kind)
        {
            switch (kind)
            {
                case BotMoveOverrideKind.GoapAction: return "GOAP";
                case BotMoveOverrideKind.CasualtyDrag: return "Drag";
                case BotMoveOverrideKind.UnstickSidestep: return "Unstick";
                case BotMoveOverrideKind.SapperStepBack: return "SapperBack";
                case BotMoveOverrideKind.EmergencyFallback: return "Fallback";
                case BotMoveOverrideKind.VehicleEvade: return "VehEvade";
                case BotMoveOverrideKind.GrenadeReflex: return "Grenade";
                default: return "None";
            }
        }

        public const byte VehicleMarkerKindNone = 0;
        public const byte VehicleMarkerKindMechanized = 1;
        public const byte VehicleMarkerKindTank = 2;
        public const byte VehicleMarkerKindPlane = 3;

        public sealed class VehicleFastHandles
        {
            public IntPtr Ptr;
            public Vehicle Ref;
            public Transform Tr;
            public Transform RootTr;
            public float NextDefensiveSmokeShellTime;
            public float ActiveSmokeShellFireUntil;
            public SyncVehicle Syncher;
            public bool SyncherChecked;
            public float CreatedAtTime;
            public bool IsAir;
            public bool IsWheeledOrTank;
            public bool IsTank;
            public bool IsPlane;
            public bool IsParachute;
            public bool IsStaticEmplacement;
            public byte CachedMarkerVehicleKind;
            public bool MarkerVehicleKindChecked;

            public bool CommanderSeatChecked;
            public int CommanderSeatIndex = -1;
            public bool HasDedicatedCommanderTurret;
            public float NextSpotterCheckTime;
            public bool CachedHasAliveCommanderOrExposedSpotter;
            public bool CachedHasCloseFriendlyEscort;
            public Spottable CommanderSpottedTarget;
            public float CommanderSpotTime;
            public float LastTurretFiredTime;
            public short LastObservedDamage;
            public float LastDamageTime;
            public Vector3 LastHazardPos;
            public float BombsDepletedAt;
            public float FlyingAroundAreaSince;
            public float RefillStateEnteredAt;
            public float OffMapRearmReadyTime;
            public float NextStaticSelfPreservationCheckTime;
            public AIVehicle AiVeh;
            public float BoardingWaitUntil;
            public bool AiVehChecked;
            public float LoneGunnerHoldUntil;
            public float NextLoneDriverEvalTime;
            public float EmplacementHazardCooldownUntil;
            public float LastCrewLossTime;
            public int LastObservedPeopleInside;
            public float RepairStationRouteStartedAt;
            public float RepairStationBestDist;
            public float NextRepairStationAttemptTime;

            public int UnstickLastFrame = -1;
            public bool UnstickApplyForce;
            public Vector3 UnstickForceVec;
            public float HighCenteredDuration;
            public float NextUnstickCountTime;
            public Soldier LastHumanOccupant;
            public float LastHumanInsideTime;
            public Vector3 WatchdogAnchorPos;
            public float WatchdogStationarySince;
            public float NextAnomalyAlertTime;
            public Spottable UnderFireAttacker;
            public IntPtr UnderFireAttackerPtr;
            public Vector3 UnderFireFromPos;
            public float UnderFireUntil;
            public bool UnderFireIsHeavy;
            public float NextUnderFireWakeTime;

            public bool AirborneSpawnHoldActive;
            public float AirborneSpawnHoldUntil;
            public Vector3 AirborneSpawnHoldPos;
            public Quaternion AirborneSpawnHoldRot;
            public bool PlaneHotStarted;
            public bool PlaneGroundSpawnChecked;
            public float PlaneHotStartPendingUntil;
            public float PlaneHotStartWindowUntil;
            public bool PlaneHotStartAllowAi;
            public Spottable CasLockedTarget;
            public float CasLockedTargetUntil;
            public bool WasOwnDriverOrVehicle;
            public bool HasObservedOwnershipState;
            public bool HasRemotePlaneSync;
            public bool RemotePlaneGravitySuppressed;
            public Vector3 RemotePlaneSyncPos;
            public Quaternion RemotePlaneSyncRot;
            public Vector3 RemotePlaneSyncVel;
            public float RemotePlaneSyncReceivedAt;
            public float TankSupportHoldUntil;
            public Vector3 LastTrackedWorldPos;
            public float LastTrackedWorldPosTime;
            public Vector3 EstimatedFlatVel;
            public float NextPlayerPedestrianSweepTime;
            public float NextApcCheckTime;
            public float NextApcDismountCooldown;
            public bool HasCachedExtents;
            public Vector4 CachedExtents;
            public float CachedEngineMaxSoundDist;
            public bool EngineSoundDistChecked;
            public float NextPedestrianScanTime;
            public float YieldUntilTime;
            public float ContinuousYieldStart;
            public float NextFriendlyYieldCountTime;
            public float NextSupportSteerTime;
            public float NextPlaneOrbitSteerTime;
            public float NextCombinedArmsAssistCountTime;
            public bool IsActiveKamikaze;
            public bool KamikazeDecisionMade;
            public bool KamikazeCommitted;
            public float NextKamikazeTargetSearchTime;
            public Squad LastKnownCrewSquad;
            public string LastKnownFaction;
            public bool CrewRetreatedFrom;
            public bool CreditedDestroyed;
            public RepairStation SeekingRepairStation;
            public bool AnsweringTankSupport;
            public byte CachedPersonality;
            public float CachedPersonalityExpireTime;
            public MovableVehicle Movable;
            public VehicleWithWheels Wheeled;
            public VehiclePlane Plane;
            public VehicleTank Tank;
            public Rigidbody CachedRb;
            public bool RbChecked;
            public FloatingObject CachedFloating;
            public bool FloatingChecked;
            public AudioSource CachedEngineSource;
            public bool EngineSourceChecked;
            public float NextPlaneAudioClipCheckTime;
            public IntPtr CachedAiVehPathPtr;
            public A.d CachedAiVehPath;
            public float NextCrewFactionCheckTime;
            internal EmergentEngine.VehicleEmergentState Emergent;
        }

        public struct TurretFastTraits
        {
            public Vehicle RootVehicle;
            public VehicleFastHandles VehHandles;
            public bool IsCommanderTurret;
            public bool IsExposedCrewSeat;
            public bool IsAA;
            public bool IsIndirectOrArty;
            public bool CanShoot360;
            public bool IsSecondaryTurret;
            public float YawBondDeg;
            public float AimFov;
        }

        private struct EnemySpawnDirCacheEntry
        {
            public bool HasCentroid;
            public Vector3 Centroid;
            public float ExpireTime;
        }

        private static readonly Dictionary<string, string> _internedFactions = new Dictionary<string, string>(16, StringComparer.Ordinal);
        private static readonly Dictionary<(string, string), bool> _sameSideFactionCache = new Dictionary<(string, string), bool>(32);
        private static readonly Dictionary<(string, string), bool> _enemyFactionCache = new Dictionary<(string, string), bool>(32);
        private static readonly Dictionary<IntPtr, SoldierFastHandles> _soldierHandlesByPtr = new Dictionary<IntPtr, SoldierFastHandles>(256);
        private static readonly Dictionary<IntPtr, SoldierFastHandles> _soldierHandlesByAiPtr = new Dictionary<IntPtr, SoldierFastHandles>(256);
        private static readonly Dictionary<IntPtr, VehicleFastHandles> _vehicleHandlesByPtr = new Dictionary<IntPtr, VehicleFastHandles>(96);
        private static readonly HashSet<IntPtr> _nonSoldierCreaturePtrs = new HashSet<IntPtr>(64);
        private static readonly HashSet<IntPtr> _nonUnitSpottablePtrs = new HashSet<IntPtr>(64);
        private static readonly Dictionary<(IntPtr, string), EnemySpawnDirCacheEntry> _enemySpawnDirCache = new Dictionary<(IntPtr, string), EnemySpawnDirCacheEntry>(16);
        private static readonly List<SoldierFastHandles> _cachedDetachedSoldiers = new List<SoldierFastHandles>(32);
        private static float _nextDetachedCreatureScanTime;
        private static int _mpRoomStateFrame = -1;
        private static bool _mpInRoomCached;
        private static bool _mpIsMasterCached = true;

        private static float _nextLocalPlayerRadioHintTime;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static string InternFaction(string raw)
        {
            if (string.IsNullOrEmpty(raw))
                return string.Empty;
            if (_internedFactions.TryGetValue(raw, out string canonical))
                return canonical;
            _internedFactions[raw] = raw;
            return raw;
        }

        public static bool IsAsymmetricDefender(Soldier soldier)
        {
            if (soldier == null || soldier.Pointer == IntPtr.Zero)
                return false;
            return IsAsymmetricDefender(GetCachedFaction(soldier));
        }

        public static bool IsAsymmetricDefender(string faction)
        {
            if (string.IsNullOrEmpty(faction))
                return false;
            BattleData bd = BattleManager.GetCurrentBattleData();
            return bd != null &&
                   bd.invadersTickets > 0 &&
                   !bd.IsInvaderFaction(faction);
        }

        public static bool AreSameSideFactions(string fA, string fB)
        {
            if (string.IsNullOrEmpty(fA) || string.IsNullOrEmpty(fB))
                return false;
            if (ReferenceEquals(fA, fB) || fA == fB)
                return true;

            var key = (fA, fB);
            if (_sameSideFactionCache.TryGetValue(key, out bool same))
                return same;

            same = ResourcesManager.IsSameFaction(fA, fB);
            if (_sameSideFactionCache.Count > 128)
                _sameSideFactionCache.Clear();
            _sameSideFactionCache[key] = same;
            _sameSideFactionCache[(fB, fA)] = same;
            return same;
        }

        public static bool AreEnemyFactions(string fA, string fB)
        {
            if (string.IsNullOrEmpty(fA) || string.IsNullOrEmpty(fB))
                return false;
            if (ReferenceEquals(fA, fB) || fA == fB)
                return false;

            var key = (fA, fB);
            if (_enemyFactionCache.TryGetValue(key, out bool enemy))
                return enemy;

            enemy = ResourcesManager.IsEnemyFaction(fA, fB);
            if (_enemyFactionCache.Count > 128)
                _enemyFactionCache.Clear();
            _enemyFactionCache[key] = enemy;
            _enemyFactionCache[(fB, fA)] = enemy;
            return enemy;
        }

        private static void EnsureMpRoomStateCached()
        {
            int frame = Time.frameCount;
            if (_mpRoomStateFrame == frame)
                return;

            _mpRoomStateFrame = frame;
            try
            {
                bool inRoom = PhotonNetwork.InRoom && !PhotonNetwork.OfflineMode;
                _mpInRoomCached = inRoom;
                _mpIsMasterCached = !inRoom || PhotonNetwork.IsMasterClient;
            }
            catch
            {
                _mpInRoomCached = false;
                _mpIsMasterCached = true;
            }
        }

        public static bool IsInMultiplayerRoom()
        {
            EnsureMpRoomStateCached();
            return _mpInRoomCached;
        }

        private static int _nextSoldierDiagId;
        private static int _nextSquadDiagId;
        private static readonly Dictionary<IntPtr, int> _squadDiagIds = new Dictionary<IntPtr, int>(96);
        private static readonly Dictionary<string, string> _formattedFactionDisplayCache = new Dictionary<string, string>(32, StringComparer.Ordinal);
        public static string FormatFactionWithAlliance(string rawFaction)
        {
            if (string.IsNullOrWhiteSpace(rawFaction))
                return string.Empty;

            if (_formattedFactionDisplayCache.TryGetValue(rawFaction, out string cached))
                return cached;

            string trimmed = rawFaction.Trim();
            string alliance = null;
            string core = trimmed;

            if (core.EndsWith("_axis", StringComparison.OrdinalIgnoreCase))
            {
                alliance = "Axis";
                core = core.Substring(0, core.Length - 5);
            }
            else if (core.EndsWith("_allies", StringComparison.OrdinalIgnoreCase))
            {
                alliance = "Allies";
                core = core.Substring(0, core.Length - 7);
            }
            else
            {
                try
                {
                    if (AreSameSideFactions(trimmed, "Germany_axis") && !AreSameSideFactions(trimmed, "UnitedStates_allies"))
                        alliance = "Axis";
                    else if (AreSameSideFactions(trimmed, "UnitedStates_allies") && !AreSameSideFactions(trimmed, "Germany_axis"))
                        alliance = "Allies";
                }
                catch { }
            }

            string prettyNation;
            if (core.StartsWith("UnitedStates_Afrikan", StringComparison.OrdinalIgnoreCase) ||
                core.Equals("UnitedStates", StringComparison.OrdinalIgnoreCase) ||
                core.Equals("USA", StringComparison.OrdinalIgnoreCase))
            {
                prettyNation = "United States";
            }
            else if (core.StartsWith("Germany", StringComparison.OrdinalIgnoreCase))
            {
                prettyNation = "Germany";
            }
            else if (core.StartsWith("Ussr", StringComparison.OrdinalIgnoreCase) ||
                     core.Equals("SovietUnion", StringComparison.OrdinalIgnoreCase))
            {
                prettyNation = "USSR";
            }
            else if (core.StartsWith("England", StringComparison.OrdinalIgnoreCase) ||
                     core.Equals("UK", StringComparison.OrdinalIgnoreCase) ||
                     core.Equals("GreatBritain", StringComparison.OrdinalIgnoreCase) ||
                     core.Equals("Britain", StringComparison.OrdinalIgnoreCase))
            {
                prettyNation = "Britain";
            }
            else if (core.StartsWith("NewZealand", StringComparison.OrdinalIgnoreCase))
            {
                prettyNation = "New Zealand";
            }
            else if (core.StartsWith("SouthAfrica", StringComparison.OrdinalIgnoreCase))
            {
                prettyNation = "South Africa";
            }
            else
            {
                // Humanize underscores and PascalCase / camelCase for all other vanilla & Workshop nations
                var sb = new System.Text.StringBuilder(core.Length + 8);
                bool prevWasSpace = true;
                for (int i = 0; i < core.Length; i++)
                {
                    char c = core[i];
                    if (c == '_' || c == '-')
                    {
                        if (!prevWasSpace && sb.Length > 0)
                        {
                            sb.Append(' ');
                            prevWasSpace = true;
                        }
                        continue;
                    }

                    if (i > 0 &&
                        !prevWasSpace &&
                        char.IsUpper(c) &&
                        (char.IsLower(core[i - 1]) || (i + 1 < core.Length && char.IsLower(core[i + 1]))))
                    {
                        sb.Append(' ');
                        prevWasSpace = true;
                    }

                    sb.Append(prevWasSpace ? char.ToUpperInvariant(c) : c);
                    prevWasSpace = (c == ' ');
                }
                prettyNation = sb.ToString().Trim();
            }

            if (string.IsNullOrEmpty(prettyNation))
                prettyNation = trimmed;

            string result = !string.IsNullOrEmpty(alliance)
                ? $"{prettyNation} ({alliance})"
                : prettyNation;

            if (_formattedFactionDisplayCache.Count > 64)
                _formattedFactionDisplayCache.Clear();
            _formattedFactionDisplayCache[rawFaction] = result;
            return result;
        }

        public static int GetSquadDiagId(IntPtr sqPtr)
        {
            if (sqPtr == IntPtr.Zero)
                return 0;
            if (_squadDiagIds.TryGetValue(sqPtr, out int id))
                return id;
            if (_squadDiagIds.Count > 512)
                _squadDiagIds.Clear();
            id = ++_nextSquadDiagId;
            _squadDiagIds[sqPtr] = id;
            return id;
        }

        public static SoldierFastHandles GetOrCreateSoldierHandles(Soldier s)
        {
            if (ReferenceEquals(s, null))
                return null;
            IntPtr ptr = s.Pointer;
            if (ptr == IntPtr.Zero)
                return null;

            if (_soldierHandlesByPtr.TryGetValue(ptr, out SoldierFastHandles h) && !ReferenceEquals(h.Ref, null) && h.Ref != null)
            {
                return h;
            }

            if (s == null)
                return null;

            Transform tr = s.transform;
            SoldierAI ai = s.aiController;
            IntPtr aiPtr = (!ReferenceEquals(ai, null) && ai != null) ? ai.Pointer : IntPtr.Zero;
            float now = Time.time;
            float jitter = (GetDeterministicHash(ptr, 31) & 63) * 0.05f;
            bool inMp = IsInMultiplayerRoom();
            SyncSoldier syn = inMp ? s.soldierSyncher : null;

            h = new SoldierFastHandles
            {
                Ptr = ptr,
                AiPtr = aiPtr,
                DiagId = ++_nextSoldierDiagId,
                CachedFullName = s.name_surname ?? string.Empty,
                Ref = s,
                Ai = ai,
                Tr = tr,
                RootTr = tr != null ? tr.root : null,
                Syncher = syn,
                SyncherChecked = !inMp || !ReferenceEquals(syn, null),
                Faction = InternFaction(s.faction),
                NextFactionRefreshTime = now + 5.0f + jitter,
                CreatedAtTime = now
            };

            _soldierHandlesByPtr[ptr] = h;
            if (aiPtr != IntPtr.Zero)
                _soldierHandlesByAiPtr[aiPtr] = h;
            return h;
        }

        public static bool IsButtonedUpArmoredVehicleCrew(SoldierFastHandles targetSolH, VehicleFastHandles tvH)
        {
            if (targetSolH == null || tvH == null || tvH.IsAir || tvH.Ref == null || tvH.Ref.Pointer == IntPtr.Zero)
                return false;

            float now = Time.time;
            if (now < targetSolH.NextEnclosedSeatCheckTime)
                return targetSolH.CachedIsButtonedArmoredCrew;

            targetSolH.NextEnclosedSeatCheckTime = now + 0.50f;
            bool buttoned = false;
            try
            {
                Vehicle veh = tvH.Ref;
                if (!veh.IsCoverDestroyed() &&
                    VeteranAndTacticsPatches.GetEffectiveVehicleExplosionArmor(veh) >= 8.0f &&
                    VeteranAndTacticsPatches.IsSoldierInEnclosedVehicleSeat(veh, targetSolH.Ref))
                {
                    buttoned = true;
                }
            }
            catch { }

            targetSolH.CachedIsButtonedArmoredCrew = buttoned;
            return buttoned;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryGetSoldierHandles(IntPtr ptr, out SoldierFastHandles h)
        {
            if (ptr != IntPtr.Zero && _soldierHandlesByPtr.TryGetValue(ptr, out h) && !ReferenceEquals(h.Ref, null) && h.Ref != null)
                return true;
            h = null;
            return false;
        }

        public static SoldierFastHandles GetOrCreateSoldierHandlesByAi(SoldierAI ai)
        {
            if (ReferenceEquals(ai, null))
                return null;
            IntPtr aiPtr = ai.Pointer;
            if (aiPtr == IntPtr.Zero)
                return null;

            if (_soldierHandlesByAiPtr.TryGetValue(aiPtr, out SoldierFastHandles h) && !ReferenceEquals(h.Ref, null) && h.Ref != null)
                return h;

            if (ai == null)
                return null;

            Soldier s = ai.character;
            if (s == null || s.Pointer == IntPtr.Zero)
                return null;

            h = GetOrCreateSoldierHandles(s);
            if (h != null)
            {
                h.Ai = ai;
                h.AiPtr = aiPtr;
                _soldierHandlesByAiPtr[aiPtr] = h;
            }
            return h;
        }

        public static bool TryGetSoldierHandlesFromCreature(Creature c, out SoldierFastHandles h)
        {
            h = null;
            if (ReferenceEquals(c, null))
                return false;
            IntPtr ptr = c.Pointer;
            if (ptr == IntPtr.Zero)
                return false;

            if (_soldierHandlesByPtr.TryGetValue(ptr, out h))
            {
                if (!ReferenceEquals(h.Ref, null) && h.Ref != null)
                    return true;
                _soldierHandlesByPtr.Remove(ptr);
                h = null;
                return false;
            }

            if (_nonSoldierCreaturePtrs.Contains(ptr))
                return false;

            Soldier s = c.TryCast<Soldier>();
            if (s == null || s.Pointer == IntPtr.Zero)
            {
                if (_nonSoldierCreaturePtrs.Count > 128)
                    _nonSoldierCreaturePtrs.Clear();
                _nonSoldierCreaturePtrs.Add(ptr);
                return false;
            }

            h = GetOrCreateSoldierHandles(s);
            return h != null;
        }

        public static string GetCachedFaction(SoldierFastHandles h, float now)
        {
            if (h == null)
                return string.Empty;
            if (now >= h.NextFactionRefreshTime && h.Ref != null)
            {
                h.Faction = InternFaction(h.Ref.faction);
                h.NextFactionRefreshTime = now + 5.0f;
            }
            return h.Faction;
        }

        public static string GetCachedFaction(Soldier s)
        {
            SoldierFastHandles h = GetOrCreateSoldierHandles(s);
            return h != null ? GetCachedFaction(h, Time.time) : string.Empty;
        }

        public static float GetCachedOpticZoom(SoldierFastHandles h, float now)
        {
            if (h == null || h.Ref == null)
                return 1.0f;
            if (now < h.NextOpticCheckTime)
                return h.CachedOpticZoom;

            h.NextOpticCheckTime = now + 1.5f;
            float zoom = 1.0f;
            Soldier obs = h.Ref;
            if (obs.HasHeldGun(0))
            {
                GenericGun gun = obs.GetHeldGun(0);
                if (gun != null && (gun.HasScopeWithMagnification() || obs.IsMarksman()))
                {
                    float fovMult = gun.GetAimingFOVMultiplier();
                    if (fovMult > 0.01f && fovMult < 0.85f)
                        zoom = Mathf.Clamp(0.85f / fovMult, 1.0f, VisionConfig.MaxOpticZoomMultiplier.Value);
                    else if (obs.IsMarksman())
                        zoom = Mathf.Min(1.75f, VisionConfig.MaxOpticZoomMultiplier.Value);
                }
            }
            h.CachedOpticZoom = zoom;
            return zoom;
        }

        public static float GetCachedCamoMultiplier(SoldierFastHandles h, float now)
        {
            if (h == null || h.Ref == null)
                return 1.0f;
            if (now < h.NextCamoCheckTime)
                return h.CachedCamoMultiplier;

            h.NextCamoCheckTime = now + 4.0f;
            Soldier s = h.Ref;
            float camoSum = 0f;
            if (s.hasUniform)
            {
                var u = s.GetUniform;
                if (u != null) camoSum += u.CamoPercReduction;
            }
            if (s.hasVest)
            {
                var v = s.GetVest;
                if (v != null) camoSum += v.CamoPercReduction;
            }
            if (s.hasHeadgear)
            {
                var hg = s.GetHeadgear;
                if (hg != null) camoSum += hg.CamoPercReduction;
            }
            float mult = 1.0f - (camoSum / 3.0f) * 0.4f;
            h.CachedCamoMultiplier = mult;
            return mult;
        }

        public static bool GetCachedGunnerAndBipod(SoldierFastHandles h, float now, out AttachmentBipod bipod)
        {
            bipod = null;
            if (h == null || h.Ref == null)
                return false;

            if (now >= h.NextBipodCheckTime)
            {
                h.NextBipodCheckTime = now + 1.4f;
                Soldier s = h.Ref;
                h.CachedBipod = s.GetHeldFunctionalBipod();
                h.CachedIsGunner = h.CachedBipod != null || s.hasMg || s.IsGunner();
            }

            bipod = h.CachedBipod;
            return h.CachedIsGunner;
        }

        public static Squad GetCachedSquad(SoldierFastHandles h, float now)
        {
            if (h == null || h.Ref == null)
                return null;

            if (now >= h.NextSquadRefreshTime)
            {
                h.NextSquadRefreshTime = now + 0.5f;
                Squad sq = h.Ref.joinedSquad;
                h.CachedSquad = (!ReferenceEquals(sq, null) && sq != null) ? sq : null;
            }
            else if (!ReferenceEquals(h.CachedSquad, null) && h.CachedSquad == null)
            {
                h.CachedSquad = null;
            }

            return h.CachedSquad;
        }

        public static void InvalidateTargetDestinationCache(Soldier s)
        {
            if (ReferenceEquals(s, null) || s.Pointer == IntPtr.Zero)
                return;
            if (_soldierHandlesByPtr.TryGetValue(s.Pointer, out SoldierFastHandles h) && h != null)
            {
                h.CachedDestPtr = IntPtr.Zero;
                h.DestKind = CachedDestKind.None;
                h.CachedDest = null;
                h.CachedCover = null;
                h.CachedRepairTask = null;
                h.CachedHealDest = null;
                h.CachedNoCoverDest = null;
                h.NextDestCheckTime = 0f;
                h.NextEndpointCheckTime = 0f;
                h.LastEndpointFrame = -1;
            }
        }

        public static bool HasHeldMortarCached(SoldierFastHandles h, SoldierAI ai, float now)
        {
            if (h == null || ReferenceEquals(ai, null))
                return false;

            if (now >= h.NextMortarCheckTime)
            {
                h.NextMortarCheckTime = now + 0.35f;
                var m = ai.HeldMortar;
                h.CachedHasHeldMortar = !ReferenceEquals(m, null) && m != null;
            }
            return h.CachedHasHeldMortar;
        }

        private static bool _nativeFieldOffsetsResolved;
        private static int _offsetSoldierTargetDest = -1;
        private static int _offsetSoldierAiVisibleTarget = -1;
        private static int _offsetSoldierAiPath = -1;
        private static int _offsetAiVehiclePath = -1;
        private static int _offsetAiVehicleVeh = -1;
        private static int _offsetAiPlaneVeh = -1;
        private static int _offsetAiPlaneTargetToAttack = -1;
        private static int _offsetTurretAiCurrentVisibleTarget = -1;
        private static int _offsetTurretAiTurret = -1;

        private static int ResolveIl2CppFieldOffset<T>(string fieldName)
        {
            try
            {
                RuntimeHelpers.RunClassConstructor(typeof(T).TypeHandle);
                IntPtr classPtr = Il2CppClassPointerStore<T>.NativeClassPtr;
                if (classPtr == IntPtr.Zero)
                    return -1;
                IntPtr fieldPtr = IL2CPP.GetIl2CppField(classPtr, fieldName);
                if (fieldPtr == IntPtr.Zero)
                    return -1;
                long rawOff = IL2CPP.il2cpp_field_get_offset(fieldPtr);
                return (rawOff > 0 && rawOff < 0x10000) ? (int)rawOff : -1;
            }
            catch
            {
                return -1;
            }
        }

        internal static void EnsureNativeFieldOffsetsResolved()
        {
            if (_nativeFieldOffsetsResolved)
                return;

            _nativeFieldOffsetsResolved = true;
            _offsetSoldierTargetDest = ResolveIl2CppFieldOffset<Soldier>("targetDestination");
            _offsetSoldierAiVisibleTarget = ResolveIl2CppFieldOffset<SoldierAI>("visibleTarget");
            _offsetSoldierAiPath = ResolveIl2CppFieldOffset<SoldierAI>("path");
            _offsetAiVehiclePath = ResolveIl2CppFieldOffset<AIVehicle>("path");
            _offsetAiVehicleVeh = ResolveIl2CppFieldOffset<AIVehicle>("veh");
            _offsetAiPlaneVeh = ResolveIl2CppFieldOffset<AIPlane>("veh");
            _offsetAiPlaneTargetToAttack = ResolveIl2CppFieldOffset<AIPlane>("targetToAttack");
            _offsetTurretAiCurrentVisibleTarget = ResolveIl2CppFieldOffset<TurretAI>("<CurrentVisibleTarget>k__BackingField");
            _offsetTurretAiTurret = ResolveIl2CppFieldOffset<TurretAI>("turret");
        }

        internal static string FormatNativeFieldOffsetsSummary()
        {
            EnsureNativeFieldOffsetsResolved();
            string Fmt(int off) => off > 0 ? $"+0x{off:X}" : "fallback";
            return $"targetDest={Fmt(_offsetSoldierTargetDest)}, visibleTarget={Fmt(_offsetSoldierAiVisibleTarget)}, path={Fmt(_offsetSoldierAiPath)}, vehPath={Fmt(_offsetAiVehiclePath)}, aiVeh={Fmt(_offsetAiVehicleVeh)}, aiPlane={Fmt(_offsetAiPlaneVeh)}, planeTgt={Fmt(_offsetAiPlaneTargetToAttack)}";
        }

        /// <summary>
        /// Zero-allocation raw <c>IntPtr</c> read for <c>Soldier.targetDestination</c> using the
        /// runtime-resolved IL2CPP field offset (falling back to managed property access if unavailable).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static IntPtr GetRawTargetDestinationPtr(Soldier s)
        {
            if (ReferenceEquals(s, null))
                return IntPtr.Zero;
            IntPtr sPtr = s.Pointer;
            if (sPtr == IntPtr.Zero)
                return IntPtr.Zero;

            if (!_nativeFieldOffsetsResolved)
                EnsureNativeFieldOffsetsResolved();

            if (_offsetSoldierTargetDest > 0)
                return Marshal.ReadIntPtr(sPtr + _offsetSoldierTargetDest);

            AiDestination d = s.targetDestination;
            return (!ReferenceEquals(d, null) && d != null) ? d.Pointer : IntPtr.Zero;
        }

        public static CachedDestKind ResolveTargetDestination(SoldierFastHandles h)
        {
            if (h == null || h.Ref == null)
                return CachedDestKind.None;

            Soldier s = h.Ref;
            if (!s.HasDestinationAssigned)
            {
                if (h.CachedDestPtr != IntPtr.Zero || !ReferenceEquals(h.CachedDest, null))
                {
                    h.CachedDestPtr = IntPtr.Zero;
                    h.DestKind = CachedDestKind.None;
                    h.CachedDest = null;
                    h.CachedCover = null;
                    h.CachedRepairTask = null;
                    h.CachedHealDest = null;
                    h.CachedNoCoverDest = null;
                }
                return CachedDestKind.None;
            }

            float now = Time.time;
            if (now < h.NextDestCheckTime)
                return h.DestKind;

            h.NextDestCheckTime = now + 0.16f;
            IntPtr destPtr = GetRawTargetDestinationPtr(s);
            if (destPtr == IntPtr.Zero)
            {
                if (h.CachedDestPtr != IntPtr.Zero || !ReferenceEquals(h.CachedDest, null))
                {
                    h.CachedDestPtr = IntPtr.Zero;
                    h.DestKind = CachedDestKind.None;
                    h.CachedDest = null;
                    h.CachedCover = null;
                    h.CachedRepairTask = null;
                    h.CachedHealDest = null;
                    h.CachedNoCoverDest = null;
                }
                return CachedDestKind.None;
            }

            if (destPtr == h.CachedDestPtr && !ReferenceEquals(h.CachedDest, null))
                return h.DestKind;

            AiDestination dest = s.targetDestination;
            if (ReferenceEquals(dest, null) || dest == null || dest.Pointer == IntPtr.Zero)
            {
                h.CachedDestPtr = IntPtr.Zero;
                h.DestKind = CachedDestKind.None;
                h.CachedDest = null;
                h.CachedCover = null;
                h.CachedRepairTask = null;
                h.CachedHealDest = null;
                h.CachedNoCoverDest = null;
                return CachedDestKind.None;
            }

            h.CachedDestPtr = destPtr;
            h.CachedDest = dest;
            h.CachedCover = null;
            h.CachedRepairTask = null;
            h.CachedHealDest = null;
            h.CachedNoCoverDest = null;

            CombatCover cc = dest.TryCast<CombatCover>();
            if (cc != null)
            {
                h.CachedCover = cc;
                h.DestKind = CachedDestKind.CombatCover;
                return CachedDestKind.CombatCover;
            }

            VehicleRepairTask rep = dest.TryCast<VehicleRepairTask>();
            if (rep != null)
            {
                h.CachedRepairTask = rep;
                h.DestKind = CachedDestKind.VehicleRepair;
                return CachedDestKind.VehicleRepair;
            }

            MedicHealingDestination heal = dest.TryCast<MedicHealingDestination>();
            if (heal != null)
            {
                h.CachedHealDest = heal;
                h.DestKind = CachedDestKind.MedicHeal;
                return CachedDestKind.MedicHeal;
            }

            DestinationWithoutCover noCov = dest.TryCast<DestinationWithoutCover>();
            if (noCov != null)
            {
                h.CachedNoCoverDest = noCov;
                h.DestKind = CachedDestKind.DestinationWithoutCover;
                return CachedDestKind.DestinationWithoutCover;
            }

            h.DestKind = CachedDestKind.Other;
            return CachedDestKind.Other;
        }

        /// <summary>
        /// Zero-allocation raw <c>IntPtr</c> read for <c>SoldierAI.visibleTarget</c>,
        /// avoiding per-frame <c>Spottable</c> wrapper allocations in <c>MovementRoutine</c>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static IntPtr GetRawVisibleTargetPtr(SoldierAI ai)
        {
            if (ReferenceEquals(ai, null))
                return IntPtr.Zero;
            IntPtr aiPtr = ai.Pointer;
            if (aiPtr == IntPtr.Zero)
                return IntPtr.Zero;

            if (!_nativeFieldOffsetsResolved)
                EnsureNativeFieldOffsetsResolved();

            if (_offsetSoldierAiVisibleTarget > 0)
                return Marshal.ReadIntPtr(aiPtr + _offsetSoldierAiVisibleTarget);

            Spottable vt = ai.visibleTarget;
            return (!ReferenceEquals(vt, null) && vt != null) ? vt.Pointer : IntPtr.Zero;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static IntPtr GetRawTurretVisibleTargetPtr(TurretAI ai)
        {
            if (ReferenceEquals(ai, null))
                return IntPtr.Zero;
            IntPtr aiPtr = ai.Pointer;
            if (aiPtr == IntPtr.Zero)
                return IntPtr.Zero;

            if (!_nativeFieldOffsetsResolved)
                EnsureNativeFieldOffsetsResolved();

            if (_offsetTurretAiCurrentVisibleTarget > 0)
                return Marshal.ReadIntPtr(aiPtr + _offsetTurretAiCurrentVisibleTarget);

            Spottable vt = ai.CurrentVisibleTarget;
            return (!ReferenceEquals(vt, null) && vt != null) ? vt.Pointer : IntPtr.Zero;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Turret GetCachedTurretForAi(TurretAI ai)
        {
            if (ReferenceEquals(ai, null))
                return null;
            IntPtr aiPtr = ai.Pointer;
            if (aiPtr == IntPtr.Zero)
                return null;

            if (!_nativeFieldOffsetsResolved)
                EnsureNativeFieldOffsetsResolved();

            if (_offsetTurretAiTurret <= 0)
                return ai.turret;

            IntPtr rawTurretPtr = Marshal.ReadIntPtr(aiPtr + _offsetTurretAiTurret);
            if (rawTurretPtr == IntPtr.Zero)
                return null;

            if (_turretByAiPtr.TryGetValue(aiPtr, out var cached) &&
                cached.TurretPtr == rawTurretPtr &&
                !ReferenceEquals(cached.Obj, null) &&
                cached.Obj.Pointer == rawTurretPtr)
            {
                return cached.Obj;
            }

            Turret resolved = ai.turret;
            _turretByAiPtr[aiPtr] = (rawTurretPtr, resolved);
            return resolved;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static IntPtr GetRawPlaneTargetToAttackPtr(AIPlane plane)
        {
            if (ReferenceEquals(plane, null))
                return IntPtr.Zero;
            IntPtr planePtr = plane.Pointer;
            if (planePtr == IntPtr.Zero)
                return IntPtr.Zero;

            if (!_nativeFieldOffsetsResolved)
                EnsureNativeFieldOffsetsResolved();

            if (_offsetAiPlaneTargetToAttack > 0)
                return Marshal.ReadIntPtr(planePtr + _offsetAiPlaneTargetToAttack);

            Spottable tgt = plane.targetToAttack;
            return (!ReferenceEquals(tgt, null) && tgt != null) ? tgt.Pointer : IntPtr.Zero;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static VehicleFastHandles GetCachedVehicleHandlesForAiVehicle(AIVehicle aiVeh)
        {
            if (ReferenceEquals(aiVeh, null))
                return null;
            IntPtr aiVehPtr = aiVeh.Pointer;
            if (aiVehPtr == IntPtr.Zero)
                return null;

            if (!_nativeFieldOffsetsResolved)
                EnsureNativeFieldOffsetsResolved();

            if (_offsetAiVehicleVeh > 0)
            {
                IntPtr rawVehPtr = Marshal.ReadIntPtr(aiVehPtr + _offsetAiVehicleVeh);
                if (rawVehPtr == IntPtr.Zero)
                    return null;
                if (TryGetVehicleHandles(rawVehPtr, out VehicleFastHandles vh) &&
                    !ReferenceEquals(vh.Ref, null) && vh.Ref != null)
                {
                    return vh;
                }
            }

            return GetOrCreateVehicleHandles(aiVeh.veh);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static VehicleFastHandles GetCachedVehicleHandlesForAiPlane(AIPlane plane)
        {
            if (ReferenceEquals(plane, null))
                return null;
            IntPtr planePtr = plane.Pointer;
            if (planePtr == IntPtr.Zero)
                return null;

            if (!_nativeFieldOffsetsResolved)
                EnsureNativeFieldOffsetsResolved();

            if (_offsetAiPlaneVeh > 0)
            {
                IntPtr rawVehPtr = Marshal.ReadIntPtr(planePtr + _offsetAiPlaneVeh);
                if (rawVehPtr == IntPtr.Zero)
                    return null;
                if (TryGetVehicleHandles(rawVehPtr, out VehicleFastHandles vh) &&
                    !ReferenceEquals(vh.Ref, null) && vh.Ref != null)
                {
                    return vh;
                }
            }

            return GetOrCreateVehicleHandles(plane.veh);
        }

        public static LocalPlayerFastHandles GetCachedLocalPlayerHandles()
        {
            int curFrame = Time.frameCount;
            if (curFrame == _localPlayerCacheFrame &&
                (_cachedLocalPlayerState.Soldier == null || _cachedLocalPlayerState.Soldier.Pointer != IntPtr.Zero))
            {
                return _cachedLocalPlayerState;
            }

            _localPlayerCacheFrame = curFrame;
            LocalPlayerFastHandles res = default;
            try
            {
                PlayerController pc = PlayerController.currentController;
                if (!ReferenceEquals(pc, null) && pc != null && pc.Pointer != IntPtr.Zero)
                {
                    res.Controller = pc;
                    Soldier s = pc.ControlledCharacter;
                    if (!ReferenceEquals(s, null) && s != null && s.Pointer != IntPtr.Zero)
                    {
                        res.Soldier = s;
                        res.HasControlledCharacter = true;
                        bool isAlive = s.IsAlive;
                        res.HasPlayer = isAlive;
                        if (isAlive)
                        {
                            SoldierFastHandles sh = GetOrCreateSoldierHandles(s);
                            res.SoldierHandles = sh;
                            float now = Time.time;
                            res.Faction = sh != null ? GetCachedFaction(sh, now) : InternFaction(s.faction);
                            Squad sq = sh != null ? GetCachedSquad(sh, now) : s.joinedSquad;
                            if (!ReferenceEquals(sq, null) && sq != null && sq.Pointer != IntPtr.Zero)
                            {
                                res.Squad = sq;
                                res.SquadPtr = sq.Pointer;
                            }
                        }
                    }

                    Vehicle cv = (res.HasControlledCharacter && res.Soldier.IsOnVehicle()) ? pc.ControlledVehicle : null;
                    if (!ReferenceEquals(cv, null) && cv != null && cv.Pointer != IntPtr.Zero)
                        res.ControlledVehicle = cv;
                }
            }
            catch { }

            _cachedLocalPlayerState = res;
            return res;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static VehicleFastHandles GetCachedCurrentVehicleHandles(SoldierFastHandles h, float now)
        {
            if (h == null || h.Ref == null || !h.Ref.IsOnVehicle())
            {
                if (h != null)
                    h.CachedVehicleHandles = null;
                return null;
            }

            if (now < h.NextVehicleHandleCheckTime &&
                h.CachedVehicleHandles != null &&
                h.CachedVehicleHandles.Ref != null &&
                h.CachedVehicleHandles.Ptr != IntPtr.Zero)
            {
                return h.CachedVehicleHandles;
            }

            h.NextVehicleHandleCheckTime = now + 0.35f;
            Vehicle curVeh = h.Ref.GetCurrentVehicle();
            VehicleFastHandles vh = !ReferenceEquals(curVeh, null) && curVeh != null
                ? GetOrCreateVehicleHandles(curVeh)
                : null;
            h.CachedVehicleHandles = vh;
            return vh;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryGetVisibleTargetCenterFast(SoldierAI ai, out Vector3 center, bool ignoreAirVehicles = false)
        {
            IntPtr visPtr = GetRawVisibleTargetPtr(ai);
            if (visPtr == IntPtr.Zero)
            {
                center = Vector3.zero;
                return false;
            }

            if (_soldierHandlesByPtr.TryGetValue(visPtr, out SoldierFastHandles sh) &&
                sh != null &&
                sh.Ref != null &&
                sh.Ref.IsAlive &&
                !ReferenceEquals(sh.Tr, null) &&
                sh.Tr != null)
            {
                center = sh.Tr.position + Vector3.up * 0.90f;
                return true;
            }

            if (_vehicleHandlesByPtr.TryGetValue(visPtr, out VehicleFastHandles vh) &&
                vh != null &&
                vh.Ref != null &&
                vh.Ref.CanFight() &&
                !ReferenceEquals(vh.Tr, null) &&
                vh.Tr != null)
            {
                if (ignoreAirVehicles && vh.IsAir)
                {
                    center = Vector3.zero;
                    return false;
                }
                center = vh.Tr.position + Vector3.up * 1.10f;
                return true;
            }

            Spottable vis = ai.visibleTarget;
            if (ignoreAirVehicles && !ReferenceEquals(vis, null) && vis != null && vis.IsAirVehicle())
            {
                center = Vector3.zero;
                return false;
            }
            return TryGetSpottableCenter(vis, out center);
        }

        /// <summary>
        /// Zero-allocation cached <c>A.d</c> path lookup (<c>SoldierAI.path</c>).
        /// Only materializes a managed <c>A.d</c> wrapper when the underlying native path pointer changes.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static A.d GetCachedAiPath(SoldierFastHandles h, SoldierAI ai)
        {
            if (ReferenceEquals(ai, null))
                return null;
            IntPtr aiPtr = ai.Pointer;
            if (aiPtr == IntPtr.Zero)
                return null;

            if (!_nativeFieldOffsetsResolved)
                EnsureNativeFieldOffsetsResolved();

            if (_offsetSoldierAiPath <= 0)
                return ai.path;

            IntPtr rawPathPtr = Marshal.ReadIntPtr(aiPtr + _offsetSoldierAiPath);
            if (rawPathPtr == IntPtr.Zero)
            {
                if (h != null && h.CachedPathPtr != IntPtr.Zero)
                {
                    h.CachedPathPtr = IntPtr.Zero;
                    h.CachedPathObj = null;
                }
                return null;
            }

            if (h != null)
            {
                if (rawPathPtr == h.CachedPathPtr && !ReferenceEquals(h.CachedPathObj, null))
                    return h.CachedPathObj;

                A.d resolved = ai.path;
                h.CachedPathPtr = rawPathPtr;
                h.CachedPathObj = resolved;
                return resolved;
            }

            return ai.path;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static A.d GetCachedAiVehiclePath(VehicleFastHandles vh, AIVehicle aiVeh)
        {
            if (ReferenceEquals(aiVeh, null))
                return null;
            IntPtr aiVehPtr = aiVeh.Pointer;
            if (aiVehPtr == IntPtr.Zero)
                return null;

            if (!_nativeFieldOffsetsResolved)
                EnsureNativeFieldOffsetsResolved();

            if (_offsetAiVehiclePath <= 0)
                return aiVeh.path;

            IntPtr rawPathPtr = Marshal.ReadIntPtr(aiVehPtr + _offsetAiVehiclePath);
            if (rawPathPtr == IntPtr.Zero)
            {
                if (vh != null && vh.CachedAiVehPathPtr != IntPtr.Zero)
                {
                    vh.CachedAiVehPathPtr = IntPtr.Zero;
                    vh.CachedAiVehPath = null;
                }
                return null;
            }

            if (vh != null)
            {
                if (rawPathPtr == vh.CachedAiVehPathPtr && !ReferenceEquals(vh.CachedAiVehPath, null))
                    return vh.CachedAiVehPath;

                A.d resolved = aiVeh.path;
                vh.CachedAiVehPathPtr = rawPathPtr;
                vh.CachedAiVehPath = resolved;
                return resolved;
            }

            return aiVeh.path;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Rigidbody GetCachedVehicleRb(VehicleFastHandles vh)
        {
            if (vh == null)
                return null;
            if (!vh.RbChecked && (ReferenceEquals(vh.CachedRb, null) || vh.CachedRb == null))
            {
                Rigidbody rb = vh.Movable != null
                    ? vh.Movable.rb
                    : (!ReferenceEquals(vh.Ref, null) && vh.Ref != null ? vh.Ref.GetRigidbody() : null);
                vh.RbChecked = true;
                if (!ReferenceEquals(rb, null) && rb != null && rb.Pointer != IntPtr.Zero)
                {
                    vh.CachedRb = rb;
                }
            }
            return (!ReferenceEquals(vh.CachedRb, null) && vh.CachedRb != null && vh.CachedRb.Pointer != IntPtr.Zero)
                ? vh.CachedRb
                : null;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static InventoryManager GetCachedInventory(SoldierFastHandles h)
        {
            if (h == null || h.Ref == null)
                return null;
            if (!h.InventoryChecked)
            {
                InventoryManager inv = h.Ref.inventory;
                if (!ReferenceEquals(inv, null) && inv != null && inv.Pointer != IntPtr.Zero)
                {
                    h.CachedInventory = inv;
                    h.InventoryChecked = true;
                }
            }
            return h.CachedInventory;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void InvalidateSoldierInventoryCache(SoldierFastHandles h)
        {
            if (h != null)
                h.NextInventoryItemsCacheTime = 0f;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void InvalidateSoldierInventoryCache(Soldier s)
        {
            if (ReferenceEquals(s, null) || s.Pointer == IntPtr.Zero)
                return;
            if (_soldierHandlesByPtr.TryGetValue(s.Pointer, out SoldierFastHandles h) && h != null)
                h.NextInventoryItemsCacheTime = 0f;
        }

        public static void GetCachedInventoryFlags(
            SoldierFastHandles h,
            float now,
            GenericGun heldGun,
            out bool hasBandage,
            out bool hasSmoke,
            out bool hasFrag,
            out bool hasUsableSmallArms)
        {
            hasBandage = false;
            hasSmoke = false;
            hasFrag = false;
            hasUsableSmallArms = false;
            if (h == null || h.Ref == null)
                return;

            if (now < h.NextInventoryItemsCacheTime && now >= h.NextInventoryItemsCacheTime - 2.0f)
            {
                hasBandage = h.CachedHasBandage;
                hasSmoke = h.CachedHasSmoke;
                hasFrag = h.CachedHasFrag;
                hasUsableSmallArms = h.CachedHasUsableSmallArms ||
                                     (heldGun != null && !heldGun.IsAtWeapon() && heldGun.GetCurrentAmmoCount() > 0);
                return;
            }

            h.NextInventoryItemsCacheTime = now + 0.70f + (h.DiagId & 7) * 0.04f;
            InventoryManager inv = GetCachedInventory(h);
            var invInner = inv != null ? inv.inventory : null;
            var items = invInner != null ? invInner.items : null;
            if (items != null)
            {
                int count = items.Count;
                for (int i = 0; i < count; i++)
                {
                    VirtualItem vi = items[i];
                    if (ReferenceEquals(vi, null) || vi == null || vi.Pointer == IntPtr.Zero)
                        continue;

                    if (!hasBandage && vi.TryCast<VirtualBandages>() != null)
                    {
                        hasBandage = true;
                        if (hasSmoke && hasFrag)
                            break;
                        continue;
                    }

                    if (!hasSmoke && vi.TryCast<VirtualSmokeGrenade>() != null)
                    {
                        hasSmoke = true;
                        if (hasBandage && hasFrag)
                            break;
                        continue;
                    }

                    if (!hasFrag &&
                        vi.TryCast<VirtualATGrenade>() == null &&
                        vi.TryCast<VirtualGrenade>() != null &&
                        !IsPressureMineItem(vi, out _))
                    {
                        hasFrag = true;
                        if (hasBandage && hasSmoke)
                            break;
                    }
                }
            }

            if (heldGun != null && !heldGun.IsAtWeapon() && heldGun.GetCurrentAmmoCount() > 0)
                hasUsableSmallArms = true;
            else
                hasUsableSmallArms = HasUsableSmallArmsAmmo(h.Ref);

            h.CachedHasBandage = hasBandage;
            h.CachedHasSmoke = hasSmoke;
            h.CachedHasFrag = hasFrag;
            h.CachedHasUsableSmallArms = hasUsableSmallArms;
        }

        public static VehicleFastHandles GetOrCreateVehicleHandles(Vehicle v)
        {
            if (ReferenceEquals(v, null))
                return null;
            IntPtr ptr = v.Pointer;
            if (ptr == IntPtr.Zero)
                return null;

            if (_vehicleHandlesByPtr.TryGetValue(ptr, out VehicleFastHandles h) && !ReferenceEquals(h.Ref, null) && h.Ref != null)
                return h;

            if (v == null)
                return null;

            Transform tr = v.transform;
            bool isAir = v.IsAirVehicle();
            bool isWheeledOrTank = !isAir && v.IsWheeledVehicleOrTank();
            VehiclePlane plane = isAir ? v.TryCast<VehiclePlane>() : null;
            bool isPlane = plane != null;
            VehicleWithWheels wheeled = isWheeledOrTank ? v.TryCast<VehicleWithWheels>() : null;
            VehicleTank tank = isWheeledOrTank ? v.TryCast<VehicleTank>() : null;
            bool isTank = tank != null;
            bool isPara = !isPlane && !isTank && v.TryCast<VehicleParachute>() != null;
            MovableVehicle movable = plane != null
                ? plane.Cast<MovableVehicle>()
                : (wheeled != null
                    ? wheeled.Cast<MovableVehicle>()
                    : (!isPara ? v.TryCast<MovableVehicle>() : null));
            Rigidbody rb = movable != null ? movable.rb : v.GetRigidbody();
            bool inMp = IsInMultiplayerRoom();
            SyncVehicle syn = inMp ? v.vehicleSyncher : null;
            h = new VehicleFastHandles
            {
                Ptr = ptr,
                Ref = v,
                Tr = tr,
                RootTr = tr != null ? tr.root : null,
                Syncher = syn,
                SyncherChecked = !inMp || !ReferenceEquals(syn, null),
                CreatedAtTime = Time.time,
                IsAir = isAir,
                IsWheeledOrTank = isWheeledOrTank,
                IsTank = isTank,
                IsPlane = isPlane,
                IsParachute = isPara,
                IsStaticEmplacement = !isAir && !isWheeledOrTank && !isPara && movable == null,
                Movable = movable,
                Wheeled = wheeled,
                Plane = plane,
                Tank = tank,
                CachedRb = rb,
                RbChecked = rb != null
            };

            _vehicleHandlesByPtr[ptr] = h;
            return h;
        }

        /// <summary>
        /// Classifies a vehicle into its cached NATO map/3D marker category (<see cref="VehicleMarkerKindPlane"/>,
        /// <see cref="VehicleMarkerKindTank"/>, <see cref="VehicleMarkerKindMechanized"/>, or
        /// <see cref="VehicleMarkerKindNone"/>) so squad markers reflect the combat vehicle they actually crew.
        /// </summary>
        public static byte GetVehicleMarkerKind(Vehicle veh, VehicleFastHandles vh = null)
        {
            if (ReferenceEquals(veh, null) || veh == null || veh.Pointer == IntPtr.Zero)
                return VehicleMarkerKindNone;

            if (vh == null)
                vh = GetOrCreateVehicleHandles(veh);
            if (vh == null)
                return VehicleMarkerKindNone;

            if (vh.MarkerVehicleKindChecked)
                return vh.CachedMarkerVehicleKind;

            byte kind = VehicleMarkerKindNone;
            if (vh.IsPlane)
            {
                // VehiclePlane (fighters/bombers/attack planes); excludes CargoPlane, Glider, and VehicleParachute.
                kind = VehicleMarkerKindPlane;
            }
            else if (vh.IsTank)
            {
                // Tracked tanks, tankettes, SPGs, StuGs, tank destroyers.
                kind = VehicleMarkerKindTank;
            }
            else if (vh.IsWheeledOrTank &&
                     !vh.IsAir &&
                     !vh.IsParachute &&
                     !vh.IsStaticEmplacement &&
                     veh.TryCast<AutoTransportVehicle>() == null)
            {
                float hullArmorMm = VeteranAndTacticsPatches.GetVehicleMaxHullArmorMm(veh);
                if (hullArmorMm >= 6.0f)
                {
                    // Armored combat cars / wheeled TDs (!IsTransportVehicle) -> armor oval (mkr_tank);
                    // Armored halftracks / APCs (IsTransportVehicle) -> mechanized oval+X (mkr_mechanized).
                    kind = !veh.IsTransportVehicle()
                        ? VehicleMarkerKindTank
                        : VehicleMarkerKindMechanized;
                }
            }

            vh.CachedMarkerVehicleKind = kind;
            vh.MarkerVehicleKindChecked = true;
            return kind;
        }

        public static bool TryGetVehicleHandles(IntPtr ptr, out VehicleFastHandles h)
        {
            return _vehicleHandlesByPtr.TryGetValue(ptr, out h) && h != null;
        }

        public static bool IsSoldierNonCombatantOrSurrendered(Soldier s, SoldierFastHandles sh = null)
        {
            if (ReferenceEquals(s, null) || s == null || s.Pointer == IntPtr.Zero)
                return true;

            if (!s.IsAlive || s.IsInjured || s.IsDead || s.HasSurrended())
                return true;

            if (sh == null)
                _soldierHandlesByPtr.TryGetValue(s.Pointer, out sh);

            return sh != null && Time.time < sh.EnemySyringeCaptureUntil;
        }

        public static bool TryResolveSpottableFast(
            Spottable s,
            out SoldierFastHandles solHandles,
            out VehicleFastHandles vehHandles)
        {
            solHandles = null;
            vehHandles = null;
            if (ReferenceEquals(s, null))
                return false;

            IntPtr ptr = s.Pointer;
            if (ptr == IntPtr.Zero)
                return false;

            if (_soldierHandlesByPtr.TryGetValue(ptr, out solHandles))
            {
                if (!ReferenceEquals(solHandles.Ref, null) && solHandles.Ref != null && solHandles.Tr != null && !IsSoldierNonCombatantOrSurrendered(solHandles.Ref, solHandles))
                    return true;
                solHandles = null;
                return false;
            }

            if (_vehicleHandlesByPtr.TryGetValue(ptr, out vehHandles))
            {
                if (!ReferenceEquals(vehHandles.Ref, null) && vehHandles.Ref != null && vehHandles.Tr != null)
                    return true;
                vehHandles = null;
                return false;
            }

            if (_nonUnitSpottablePtrs.Contains(ptr))
            {
                try
                {
                    Component comp = s.TryCast<Component>();
                    return comp != null && comp.transform != null;
                }
                catch
                {
                    return false;
                }
            }

            try
            {
                Soldier sol = s.TryCast<Soldier>();
                if (sol != null && sol.Pointer != IntPtr.Zero)
                {
                    solHandles = GetOrCreateSoldierHandles(sol);
                    if (solHandles != null && solHandles.Tr != null && !IsSoldierNonCombatantOrSurrendered(sol, solHandles))
                        return true;
                    solHandles = null;
                    return false;
                }

                Vehicle veh = s.TryCast<Vehicle>();
                if (veh != null && veh.Pointer != IntPtr.Zero)
                {
                    vehHandles = GetOrCreateVehicleHandles(veh);
                    if (vehHandles != null && vehHandles.Tr != null)
                        return true;
                    vehHandles = null;
                    return false;
                }

                Component comp = s.TryCast<Component>();
                if (comp == null || comp.transform == null)
                    return false;

                Creature c = s.TryCast<Creature>();
                if (c != null && !c.IsAlive)
                    return false;

                if (_nonUnitSpottablePtrs.Count > 128)
                    _nonUnitSpottablePtrs.Clear();
                _nonUnitSpottablePtrs.Add(ptr);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static void PruneFastHandleCaches(HashSet<IntPtr> activeSoldierPtrs, HashSet<IntPtr> activeVehiclePtrs)
        {
            if (activeSoldierPtrs != null && _soldierHandlesByPtr.Count > 0)
            {
                _sharedPruneBuffer.Clear();
                foreach (var kvp in _soldierHandlesByPtr)
                {
                    if (!activeSoldierPtrs.Contains(kvp.Key))
                    {
                        _sharedPruneBuffer.Add(kvp.Key);
                        if (kvp.Value.AiPtr != IntPtr.Zero)
                            _soldierHandlesByAiPtr.Remove(kvp.Value.AiPtr);
                    }
                }
                for (int i = 0; i < _sharedPruneBuffer.Count; i++)
                    _soldierHandlesByPtr.Remove(_sharedPruneBuffer[i]);
                _sharedPruneBuffer.Clear();
            }

            if (activeVehiclePtrs != null && _vehicleHandlesByPtr.Count > 0)
            {
                PruneDict(_vehicleHandlesByPtr, activeVehiclePtrs);
            }

            if (_nonSoldierCreaturePtrs.Count > 128)
                _nonSoldierCreaturePtrs.Clear();
            if (_nonUnitSpottablePtrs.Count > 128)
                _nonUnitSpottablePtrs.Clear();
        }

        // Rolling 15s Frame-Time & Hitch Telemetry
        private static float _currentWindowMaxFrameMs;
        private static float _previousWindowMaxFrameMs;
        private static float _nextFrameWindowRolloverTime;
        public static float RollingMaxFrameMs => Mathf.Max(_currentWindowMaxFrameMs, _previousWindowMaxFrameMs);
        public static int TotalHitchesOver28ms;

        public static int TotalPingGovernorSkippedCycles;

        // Alt-Tab Background FPS Cap & Two-Stage .NET 6 Nursery GC Pacing
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentProcessId();

        private static uint _currentProcessId;
        private static IntPtr _lastForegroundHwnd;
        private static float _nextWindowFocusPollUnscaledTime;
        private static bool _isGameWindowFocused = true;
        private static bool _backgroundFpsCapActive;
        private static int _backgroundFpsCapTarget = 30;
        private static int _savedVSyncCountBeforeUnfocus = 1;
        private static int _savedTargetFpsBeforeUnfocus = -1;

        public static bool IsGameWindowFocused => _isGameWindowFocused;

        public static void RestoreForegroundFpsIfCapped()
        {
            if (!_backgroundFpsCapActive)
                return;

            _backgroundFpsCapActive = false;
            try
            {
                var graphic = SavableData.Settings?.graphic;
                if (graphic != null)
                {
                    graphic.ApplyTargetFrameRate();
                    return;
                }
            }
            catch { }

            try
            {
                QualitySettings.vSyncCount = _savedVSyncCountBeforeUnfocus;
                Application.targetFrameRate = _savedTargetFpsBeforeUnfocus;
            }
            catch { }
        }

        private static void UpdateWindowFocusAndBackgroundFpsCap(float unscaledNow)
        {
            try
            {
                if (unscaledNow >= _nextWindowFocusPollUnscaledTime || unscaledNow < _nextWindowFocusPollUnscaledTime - 2.0f)
                {
                    _nextWindowFocusPollUnscaledTime = unscaledNow + 0.20f;
                    if (OperatingSystem.IsWindows())
                    {
                        if (_currentProcessId == 0u)
                            _currentProcessId = GetCurrentProcessId();

                        IntPtr hwnd = GetForegroundWindow();
                        if (hwnd != _lastForegroundHwnd)
                        {
                            _lastForegroundHwnd = hwnd;
                            if (hwnd == IntPtr.Zero)
                            {
                                _isGameWindowFocused = false;
                            }
                            else
                            {
                                GetWindowThreadProcessId(hwnd, out uint fgPid);
                                _isGameWindowFocused = fgPid == _currentProcessId;
                            }
                        }
                    }
                    else
                    {
                        _isGameWindowFocused = Application.isFocused;
                    }
                }

                if (!_isGameWindowFocused)
                {
                    int desiredBgFps = IsMultiplayerWithRemotePlayers() ? 45 : 30;
                    if (!_backgroundFpsCapActive)
                    {
                        _savedVSyncCountBeforeUnfocus = QualitySettings.vSyncCount;
                        _savedTargetFpsBeforeUnfocus = Application.targetFrameRate;
                        QualitySettings.vSyncCount = 0;
                        Application.targetFrameRate = desiredBgFps;
                        _backgroundFpsCapTarget = desiredBgFps;
                        _backgroundFpsCapActive = true;
                    }
                    else if (_backgroundFpsCapTarget != desiredBgFps)
                    {
                        Application.targetFrameRate = desiredBgFps;
                        _backgroundFpsCapTarget = desiredBgFps;
                    }
                }
                else
                {
                    RestoreForegroundFpsIfCapped();
                }
            }
            catch { }
        }

        public static void RecordFrameTiming()
        {
            float now = Time.unscaledTime;
            UpdateWindowFocusAndBackgroundFpsCap(now);

            if (now >= _nextFrameWindowRolloverTime)
            {
                _previousWindowMaxFrameMs = _currentWindowMaxFrameMs;
                _currentWindowMaxFrameMs = 0f;
                _nextFrameWindowRolloverTime = now + 15.0f;
            }

            // Ignore scene load / alt-tab / pause frames so telemetry reflects true gameplay frame pacing
            if (Time.timeSinceLevelLoad < 3.0f || Time.timeScale <= 0.01f)
                return;

            float dt = Time.unscaledDeltaTime;
            if (dt <= 0f || dt > 0.50f)
                return;

            float dtMs = dt * 1000.0f;
            if (!_isGameWindowFocused)
                return;

            if (dtMs > _currentWindowMaxFrameMs)
                _currentWindowMaxFrameMs = dtMs;
            if (dtMs > 28.0f)
                TotalHitchesOver28ms++;
        }

        public static bool CanEmitDebugLog(string category, float minInterval = 4.0f)
        {
            if (!VisionConfig.DebugLogging.Value || Plugin.Log == null)
                return false;

            if (_lastDebugLogWriteFrame == Time.frameCount && category != "Heartbeat")
                return false;

            float now = Time.unscaledTime;
            if (_debugLogCooldowns.TryGetValue(category, out float nextAllowed) && now < nextAllowed)
                return false;

            return true;
        }

        [InterpolatedStringHandler]
        public ref struct DebugLogInterpolatedStringHandler
        {
            private StringBuilder _builder;

            public DebugLogInterpolatedStringHandler(
                int literalLength,
                int formattedCount,
                string category,
                out bool shouldAppend)
            {
                if (!CanEmitDebugLog(category))
                {
                    _builder = null;
                    shouldAppend = false;
                    return;
                }
                StringBuilder sb = _sharedLogBuilder;
                if (sb == null)
                {
                    sb = new StringBuilder(Math.Max(256, literalLength + formattedCount * 12));
                    _sharedLogBuilder = sb;
                }
                else
                {
                    sb.Clear();
                }
                _builder = sb;
                shouldAppend = true;
            }

            public void AppendLiteral(string s) => _builder?.Append(s);
            public void AppendFormatted<T>(T value) => _builder?.Append(value);
            public void AppendFormatted<T>(T value, string format)
            {
                if (_builder == null)
                    return;
                if (value is IFormattable formattable)
                    _builder.Append(formattable.ToString(format, null));
                else
                    _builder.Append(value);
            }

            internal bool IsActive => _builder != null;
            internal string GetFormattedText() => _builder != null ? _builder.ToString() : string.Empty;
        }

        private const int AsyncLogRingCapacity = 512;
        private const int AsyncLogRingMask = AsyncLogRingCapacity - 1;

        private struct AsyncLogEntry
        {
            public int Ready;
            public bool IsWarning;
            public string Message;
        }

        private static readonly AsyncLogEntry[] _asyncLogRing = new AsyncLogEntry[AsyncLogRingCapacity];
        private static int _asyncLogWriteSeq;
        private static int _asyncLogReadSeq;
        private static AutoResetEvent _asyncLogWake;
        private static Thread _asyncLogThread;
        private static volatile bool _asyncLogThreadStarted;
        private static readonly object _asyncLogInitLock = new object();

        private static void EnsureAsyncLoggerStarted()
        {
            if (_asyncLogThreadStarted)
                return;

            lock (_asyncLogInitLock)
            {
                if (_asyncLogThreadStarted)
                    return;

                _asyncLogWake = new AutoResetEvent(false);
                _asyncLogThread = new Thread(AsyncLoggerThreadLoop)
                {
                    Name = "ER2AI_AsyncLogger",
                    IsBackground = true,
                    Priority = System.Threading.ThreadPriority.BelowNormal
                };
                _asyncLogThreadStarted = true;
                _asyncLogThread.Start();
            }
        }

        public static void EnqueueAsyncLog(bool isWarning, string message)
        {
            if (string.IsNullOrEmpty(message) || Plugin.Log == null)
                return;

            EnsureAsyncLoggerStarted();

            int pending = Volatile.Read(ref _asyncLogWriteSeq) - Volatile.Read(ref _asyncLogReadSeq);
            if (pending >= AsyncLogRingCapacity - 1)
                return;

            int seq = Interlocked.Increment(ref _asyncLogWriteSeq) - 1;
            int slot = seq & AsyncLogRingMask;
            Volatile.Write(ref _asyncLogRing[slot].Ready, 0);
            _asyncLogRing[slot].IsWarning = isWarning;
            _asyncLogRing[slot].Message = message;
            Volatile.Write(ref _asyncLogRing[slot].Ready, 1);
            _asyncLogWake?.Set();
        }

        private static void AsyncLoggerThreadLoop()
        {
            while (true)
            {
                try
                {
                    _asyncLogWake.WaitOne(200);
                    while (true)
                    {
                        int r = Volatile.Read(ref _asyncLogReadSeq);
                        int w = Volatile.Read(ref _asyncLogWriteSeq);
                        if (r == w)
                            break;

                        int slot = r & AsyncLogRingMask;
                        if (Volatile.Read(ref _asyncLogRing[slot].Ready) == 0)
                        {
                            Thread.Yield();
                            if (Volatile.Read(ref _asyncLogRing[slot].Ready) == 0)
                                break;
                        }

                        bool isWarn = _asyncLogRing[slot].IsWarning;
                        string msg = _asyncLogRing[slot].Message;
                        _asyncLogRing[slot].Message = null;
                        Volatile.Write(ref _asyncLogRing[slot].Ready, 0);
                        Volatile.Write(ref _asyncLogReadSeq, r + 1);

                        if (!string.IsNullOrEmpty(msg) && Plugin.Log != null)
                        {
                            if (isWarn)
                                Plugin.Log.LogWarning(msg);
                            else
                                Plugin.Log.LogInfo(msg);
                        }
                    }
                }
                catch
                {
                    Thread.Sleep(250);
                }
            }
        }

        public static void LogDebug(
            string category,
            [InterpolatedStringHandlerArgument("category")] DebugLogInterpolatedStringHandler message,
            float minInterval = 4.0f)
        {
            if (!message.IsActive || !CanEmitDebugLog(category, minInterval))
                return;

            _debugLogCooldowns[category] = Time.unscaledTime + minInterval;
            _lastDebugLogWriteFrame = Time.frameCount;
            EnqueueAsyncLog(false, $"[Diag:{category}] {message.GetFormattedText()}");
        }

        public static void LogDebug(string category, string message, float minInterval = 4.0f)
        {
            if (!CanEmitDebugLog(category, minInterval))
                return;

            _debugLogCooldowns[category] = Time.unscaledTime + minInterval;
            _lastDebugLogWriteFrame = Time.frameCount;
            EnqueueAsyncLog(false, $"[Diag:{category}] {message}");
        }

        public static int GetConnectedPlayerCount()
        {
            float now = Time.unscaledTime;
            if (now < _nextRoomCheckTime)
                return _cachedRoomPlayerCount;

            _nextRoomCheckTime = now + 1.0f;
            try
            {
                if (PhotonNetwork.InRoom && PhotonNetwork.CurrentRoom != null)
                {
                    _cachedRoomPlayerCount = Mathf.Max(1, (int)PhotonNetwork.CurrentRoom.PlayerCount);

                    IntPtr roomPtr = PhotonNetwork.CurrentRoom.Pointer;
                    if (roomPtr != IntPtr.Zero && roomPtr != _lastPublishedRoomPtr && PhotonNetwork.LocalPlayer != null)
                    {
                        ResetCoopRpcNetworkState();
                        EnsureLocalModVersionPublished();
                    }
                }
                else
                {
                    _cachedRoomPlayerCount = 1;
                    if (_lastPublishedRoomPtr != IntPtr.Zero)
                    {
                        _lastPublishedRoomPtr = IntPtr.Zero;
                        _playerModdedCache.Clear();
                        _nextTargetedPlayerChatTime.Clear();
                        ResetCoopRpcNetworkState();
                    }
                }
            }
            catch
            {
                _cachedRoomPlayerCount = 1;
            }
            return _cachedRoomPlayerCount;
        }

        public static bool IsMultiplayerWithRemotePlayers()
        {
            return GetConnectedPlayerCount() > 1;
        }

        /// <summary>
        /// Returns true when the current frame time is elevated and bot brain cycles should
        /// yield/coast for a cycle so human player ping and Photon packet delivery never spike.
        /// </summary>
        public static bool ShouldSkipBotCycleForNetworkPing()
        {
            float dt = Time.unscaledDeltaTime;
            int players = GetConnectedPlayerCount();
            // Keep thresholds safely above normal 60Hz VSync jitter (16.67ms-17.4ms) so locked 60 FPS lobbies
            // never false-trigger PingSkip or starve global squad/macro directors.
            float threshold = players >= 6 ? 0.0208f : (players > 1 ? 0.0222f : 0.0265f);
            if (_backgroundFpsCapActive)
            {
                float bgFrameBudget = (1.0f / Mathf.Max(20, _backgroundFpsCapTarget)) + 0.008f;
                if (threshold < bgFrameBudget)
                    threshold = bgFrameBudget;
            }
            if (dt > threshold)
            {
                TotalPingGovernorSkippedCycles++;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Marks the current frame as having executed heavy spatial/octree/snapshot work
        /// (e.g., vanilla <c>Squad.SquadLeaderRoutine</c> or a full world snapshot rebuild)
        /// so secondary octree sweeps shift to subsequent frames.
        /// </summary>
        public static void NotifyHeavyWorkThisFrame()
        {
            _lastHeavyWorkFrame = Time.frameCount;
        }

        public static void NotifyDirectorWorkThisFrame()
        {
            int frame = Time.frameCount;
            _lastHeavyWorkFrame = frame;
            _lastDirectorWorkFrame = frame;
        }

        public static bool IsHeavyWorkFrame()
        {
            return _lastHeavyWorkFrame == Time.frameCount;
        }

        /// <summary>
        /// Ensures at most ONE global mod director pass (Macro Director, TeamNet GOAP Coordinator,
        /// Snapshot Rebuild, or Dictionary Pruning) executes on any single frame, without allowing
        /// frequent vanilla <c>Squad.SquadLeaderRoutine</c> coroutines (80+/s across 20 squads) to starve directors.
        /// </summary>
        public static bool TryClaimHeavyFrameWorkSlot(bool allowOnModerateFrame = false)
        {
            if (!allowOnModerateFrame && ShouldSkipBotCycleForNetworkPing())
                return false;

            int frame = Time.frameCount;
            if (_lastDirectorWorkFrame == frame)
                return false;

            _lastDirectorWorkFrame = frame;
            _lastHeavyWorkFrame = frame;
            return true;
        }

        public static bool CanBotMoveAutonomously(Soldier s)
        {
            if (s == null || s.Pointer == IntPtr.Zero)
                return false;

            AiParams p = s.Params;
            return p == null || p.canMove;
        }

        public static bool CanBotChangePoseAutonomously(Soldier s)
        {
            if (s == null || s.Pointer == IntPtr.Zero)
                return false;

            AiParams p = s.Params;
            return p == null || (p.changePoseAutomatically && !p.forcedPose.HasValue);
        }

        public static bool IsMasterClient()
        {
            EnsureMpRoomStateCached();
            return _mpIsMasterCached;
        }

        public static bool IsLocalPlayerInSquad(Squad sq, out bool isLeader)
        {
            isLeader = false;
            if (sq == null || sq.Pointer == IntPtr.Zero)
                return false;

            Soldier localPlayer = Soldier.CurrentControlledSoldierOrNull();
            if (ReferenceEquals(localPlayer, null) || localPlayer == null || !localPlayer.IsAlive)
                return false;

            Squad playerSq = localPlayer.joinedSquad;
            if (playerSq == null || playerSq.Pointer != sq.Pointer)
                return false;

            Soldier leader = sq.Leader;
            isLeader = leader != null && leader.Pointer == localPlayer.Pointer;
            return true;
        }

        /// <summary>
        /// Co-op-safe check for whether <paramref name="h"/> is currently controlled by ANY human player
        /// using cached <see cref="SyncSoldier"/> handles with zero IL2CPP wrapper allocations.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsAnyHumanPlayerSoldier(SoldierFastHandles h)
        {
            if (h == null || ReferenceEquals(h.Ref, null) || h.Ref == null || h.Ptr == IntPtr.Zero)
                return false;

            if (h.Ref.IsPlayer())
                return true;

            if (!IsInMultiplayerRoom())
                return false;

            SyncSoldier syn = GetCachedSoldierSyncher(h);
            return syn != null && syn.controlled_by_player;
        }

        /// <summary>
        /// Co-op-safe check for whether <paramref name="s"/> is currently controlled by ANY human player
        /// (local <c>PlayerController</c> OR remote Photon client via <c>SyncSoldier.controlled_by_player</c>).
        /// </summary>
        public static bool IsAnyHumanPlayerSoldier(Soldier s)
        {
            if (ReferenceEquals(s, null) || s == null || s.Pointer == IntPtr.Zero)
                return false;

            if (s.IsPlayer())
                return true;

            if (!IsInMultiplayerRoom())
                return false;

            if (TryGetSoldierHandles(s.Pointer, out SoldierFastHandles h))
            {
                SyncSoldier cachedSyn = GetCachedSoldierSyncher(h);
                return cachedSyn != null && cachedSyn.controlled_by_player;
            }

            SyncSoldier syn = s.soldierSyncher;
            return syn != null && syn.controlled_by_player;
        }

        /// <summary>
        /// Co-op-safe equivalent of vanilla <c>Squad.IsPlayerInSquad()</c>, which only checks the
        /// local client's <c>PlayerController</c>. Returns true on Host or Client whenever ANY alive
        /// human player (<see cref="IsAnyHumanPlayerSoldier"/>) belongs to <paramref name="sq"/>.
        /// </summary>
        public static bool HasAnyHumanPlayerInSquad(Squad sq)
        {
            if (sq == null || sq.Pointer == IntPtr.Zero)
                return false;

            if (sq.IsPlayerInSquad())
                return true;

            try
            {
                var units = sq.units;
                if (units != null)
                {
                    int c = units.Count;
                    for (int i = 0; i < c; i++)
                    {
                        Soldier u = units[i];
                        if (!ReferenceEquals(u, null) && u != null && u.IsAlive && IsAnyHumanPlayerSoldier(u))
                            return true;
                    }
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// Returns true in an online room when ANY non-dead member of <paramref name="sq"/> is owned by
        /// a remote Photon client (<c>!syn.OwnSyncher</c>) or actively controlled by a remote human player.
        /// Remains true even while a remote vehicle player is dead on the Squad Selection screen.
        /// </summary>
        public static bool IsSquadOwnedOrOccupiedByRemoteHuman(Squad sq)
        {
            if (!IsInMultiplayerRoom() || sq == null || sq.Pointer == IntPtr.Zero)
                return false;

            try
            {
                var units = sq.units;
                if (units != null)
                {
                    int c = units.Count;
                    for (int i = 0; i < c; i++)
                    {
                        Soldier u = units[i];
                        if (ReferenceEquals(u, null) || u == null || u.Pointer == IntPtr.Zero || u.IsDead || (!u.IsAlive && !u.IsInjured))
                            continue;

                        SyncSoldier syn = TryGetSoldierHandles(u.Pointer, out SoldierFastHandles uh)
                            ? GetCachedSoldierSyncher(uh)
                            : u.soldierSyncher;
                        if (syn != null && (!syn.OwnSyncher || (!u.IsPlayer() && syn.controlled_by_player)))
                            return true;
                    }
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// Co-op-safe check for whether ANY alive human player (local Host/Client or remote Photon client)
        /// is currently occupying a driver seat of <paramref name="veh"/>.
        /// </summary>
        public static bool IsAnyHumanPlayerDriving(Vehicle veh)
        {
            if (ReferenceEquals(veh, null) || veh == null || veh.Pointer == IntPtr.Zero)
                return false;

            if (veh.PlayerIsDriving())
                return true;

            if (!IsInMultiplayerRoom())
                return false;

            try
            {
                var seats = veh.seats;
                if (seats != null)
                {
                    int len = seats.Length;
                    for (int i = 0; i < len; i++)
                    {
                        Seats s = seats[i];
                        if (s != null && s.isDriver && s.hasUnitSet && IsAnyHumanPlayerSoldier(s.unitSet))
                            return true;
                    }
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// Co-op-safe check for whether ANY alive human player (local Host/Client or remote Photon client)
        /// is currently occupying any seat inside <paramref name="veh"/>.
        /// </summary>
        public static bool IsAnyHumanPlayerInsideVehicle(Vehicle veh)
        {
            if (ReferenceEquals(veh, null) || veh == null || veh.Pointer == IntPtr.Zero)
                return false;

            if (veh.PlayerIsInside())
                return true;

            if (!IsInMultiplayerRoom())
                return false;

            try
            {
                var seats = veh.seats;
                if (seats != null)
                {
                    int len = seats.Length;
                    for (int i = 0; i < len; i++)
                    {
                        Seats s = seats[i];
                        if (s != null && s.hasUnitSet && IsAnyHumanPlayerSoldier(s.unitSet))
                            return true;
                    }
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// Returns an accurate velocity vector for both locally simulated vehicles (<c>rb.velocity</c>)
        /// and remote Co-op client vehicles whose Rigidbodies are driven via network transform updates
        /// (<see cref="SyncVehicle.last_speed_update"/> / <see cref="SyncVehiclePlane.synchedSpeed"/>).
        /// </summary>
        public static Vector3 GetEffectiveVehicleVelocity(Vehicle veh)
        {
            if (ReferenceEquals(veh, null) || veh == null || veh.Pointer == IntPtr.Zero)
                return Vector3.zero;

            try
            {
                MovableVehicle mv = veh.TryCast<MovableVehicle>();
                if (mv != null && mv.rb != null && mv.rb.velocity.sqrMagnitude > 0.04f)
                    return mv.rb.velocity;

                SyncVehicle sv = veh.vehicleSyncher;
                if (sv != null && sv.Pointer != IntPtr.Zero)
                {
                    SyncVehiclePlane svp = sv.TryCast<SyncVehiclePlane>();
                    if (svp != null && svp.synchedSpeed.sqrMagnitude > 0.04f)
                        return svp.synchedSpeed;

                    float spd = sv.last_speed_update;
                    if (Mathf.Abs(spd) > 0.2f && veh.transform != null)
                        return veh.transform.forward * spd;
                }
            }
            catch { }
            return Vector3.zero;
        }

        public static bool IsHumanSoldierAfk(Soldier player, float now, float afkSeconds = 25.0f)
        {
            if (ReferenceEquals(player, null) || player == null || !player.IsAlive || !player.IsPlayer())
                return false;

            if (!TryGetSoldierHandles(player.Pointer, out SoldierFastHandles h) || h.LastHumanActiveTime <= 0f)
                return false;

            if (now < h.LastHumanActiveTime)
                return false;

            return (now - h.LastHumanActiveTime) >= afkSeconds;
        }

        public static bool IsVehicleReservedByNearbyActiveHuman(Vehicle veh, float now)
        {
            if (veh == null || veh.Pointer == IntPtr.Zero)
                return false;

            if (IsAnyHumanPlayerInsideVehicle(veh))
                return true;

            if (!_vehicleHandlesByPtr.TryGetValue(veh.Pointer, out VehicleFastHandles vh) || vh == null || vh.LastHumanInsideTime <= 0f)
                return false;

            if (now < vh.LastHumanInsideTime || (now - vh.LastHumanInsideTime) > 90.0f)
                return false;

            Soldier human = vh.LastHumanOccupant;
            if (ReferenceEquals(human, null) || human == null || !human.IsAlive || !human.IsPlayer() || human.IsOnVehicle() || human.transform == null || veh.transform == null)
                return false;

            if ((human.transform.position - veh.transform.position).sqrMagnitude > 25.0f * 25.0f)
                return false;

            return !IsHumanSoldierAfk(human, now, 25.0f);
        }

        private static readonly List<MissionObjective> _reusableFallbackObjectives = new List<MissionObjective>(12);

        public static bool TryFindFallbackObjectiveOrFriendlyPos(string faction, Vector3 fromPos, out Vector3 fallbackPos)
        {
            fallbackPos = Vector3.zero;
            float bestDistSq = float.MaxValue;
            bool found = false;

            try
            {
                BattleManager bm = BattleManager.instance;
                BattlePhase phase = (bm != null && bm.phases != null && bm.currentPhaseNum >= 0 && bm.currentPhaseNum < bm.phases.Length)
                    ? bm.phases[bm.currentPhaseNum]
                    : null;
                if (phase != null)
                {
                    CollectActiveMissionObjectives(phase, _reusableFallbackObjectives);
                    for (int i = 0; i < _reusableFallbackObjectives.Count; i++)
                    {
                        MissionObjective obj = _reusableFallbackObjectives[i];
                        if (obj == null || obj.transform == null)
                            continue;
                        Vector3 oPos = obj.transform.position;
                        float dSq = (oPos - fromPos).sqrMagnitude;
                        if (dSq < bestDistSq)
                        {
                            bestDistSq = dSq;
                            fallbackPos = oPos;
                            found = true;
                        }
                    }
                    _reusableFallbackObjectives.Clear();
                }

                if (!found && !string.IsNullOrEmpty(faction))
                {
                    EnsureWorldSnapshot();
                    for (int s = 0; s < WorldSquadCount; s++)
                    {
                        ref SquadSnapshot sqSnap = ref WorldSquads[s];
                        if (!sqSnap.LeaderIsAlive || sqSnap.RefOnPlane || sqSnap.IsPlayerInSquad || !AreSameSideFactions(faction, sqSnap.Faction))
                            continue;
                        float dSq = (sqSnap.RefPos - fromPos).sqrMagnitude;
                        if (dSq < bestDistSq)
                        {
                            bestDistSq = dSq;
                            fallbackPos = sqSnap.RefPos;
                            found = true;
                        }
                    }
                }
            }
            catch { }
            return found;
        }

        private static readonly Dictionary<int, float> _nextTargetedPlayerChatTime = new Dictionary<int, float>(16);
        private static readonly Dictionary<string, string> _vehicleShortNameCache = new Dictionary<string, string>(64, StringComparer.Ordinal);
        private static readonly HashSet<int> _reusableNotifiedActors = new HashSet<int>();

        public static void EnsureLocalModVersionPublished()
        {
            try
            {
                if (!PhotonNetwork.InRoom || PhotonNetwork.CurrentRoom == null || PhotonNetwork.LocalPlayer == null)
                    return;

                IntPtr roomPtr = PhotonNetwork.CurrentRoom.Pointer;
                if (roomPtr == IntPtr.Zero)
                    return;

                _lastPublishedRoomPtr = roomPtr;
                _playerModdedCache.Clear();
                var props = new ExitGames.Client.Photon.Hashtable();
                props[(Il2CppSystem.String)"bh_ver"] = (Il2CppSystem.String)Plugin.PluginVersion;
                PhotonNetwork.LocalPlayer.SetCustomProperties(props);
            }
            catch { }
        }

        public static void InvalidatePlayerModdedCache(int actorNum = -1)
        {
            if (actorNum < 0)
                _playerModdedCache.Clear();
            else
                _playerModdedCache.Remove(actorNum);
        }

        /// <summary>
        /// Strictly returns true ONLY when <paramref name="player"/> is the local player or has explicitly
        /// published <c>"bh_ver"</c> in Photon CustomProperties. Unmodded vanilla clients return false.
        /// </summary>
        public static bool IsPhotonPlayerConfirmedModded(Photon.Realtime.Player player)
        {
            if (player == null)
                return false;

            try
            {
                if (player.IsLocal)
                    return true;

                int actorNum = player.ActorNumber;
                float now = Time.unscaledTime;
                if (_playerModdedCache.TryGetValue(actorNum, out var cached) && now < cached.ExpireTime)
                    return cached.HasMod;

                bool hasMod = false;
                var props = player.CustomProperties;
                if (props != null && props.ContainsKey((Il2CppSystem.String)"bh_ver"))
                    hasMod = true;

                if (_playerModdedCache.Count > 32)
                    _playerModdedCache.Clear();
                _playerModdedCache[actorNum] = (hasMod, now + 5.0f);
                return hasMod;
            }
            catch
            {
                return false;
            }
        }

        public static bool SendTargetedVanillaChatMessage(
            Photon.Realtime.Player targetPlayer,
            string message,
            float minPlayerInterval = 10.0f,
            bool bypassRpcBudget = false)
        {
            if (!IsInMultiplayerRoom() ||
                !IsMasterClient() ||
                targetPlayer == null ||
                targetPlayer.IsLocal ||
                string.IsNullOrEmpty(message))
            {
                return false;
            }

            try
            {
                int actorNum = targetPlayer.ActorNumber;
                float now = Time.unscaledTime;
                if (minPlayerInterval > 0f &&
                    _nextTargetedPlayerChatTime.TryGetValue(actorNum, out float nextAllowed) &&
                    now < nextAllowed)
                {
                    return false;
                }

                BattleLoader bl = BattleLoader.instance;
                if (bl == null || bl.photonView == null || PhotonNetwork.LocalPlayer == null)
                    return false;

                if (!bypassRpcBudget && !TryConsumeBotRpcBudget(0.20f, BotRpcPriority.Low))
                    return false;

                if (_nextTargetedPlayerChatTime.Count > 32)
                    _nextTargetedPlayerChatTime.Clear();
                if (minPlayerInterval > 0f)
                    _nextTargetedPlayerChatTime[actorNum] = now + minPlayerInterval;

                var rpcArgs = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Il2CppSystem.Object>(2);
                rpcArgs[0] = PhotonNetwork.LocalPlayer.Cast<Il2CppSystem.Object>();
                rpcArgs[1] = (Il2CppSystem.String)message;
                bl.photonView.RPC("Chat_NewMessage", targetPlayer, rpcArgs);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static void TryShowLocalPlayerRadioHint(
            Squad targetSquad,
            string message,
            bool requireLeader = false,
            float minInterval = 14.0f,
            bool notifyRemoteVanillaSquadmates = true,
            bool bypassCooldown = false)
        {
            if (string.IsNullOrEmpty(message))
                return;

            float now = Time.time;
            if ((bypassCooldown || now >= _nextLocalPlayerRadioHintTime) &&
                IsLocalPlayerInSquad(targetSquad, out bool isLeader) &&
                (!requireLeader || isLeader))
            {
                _nextLocalPlayerRadioHintTime = bypassCooldown
                    ? Mathf.Max(_nextLocalPlayerRadioHintTime, now + minInterval)
                    : (now + minInterval);
                try
                {
                    PlayerController.HintToPlayer(message, false);
                }
                catch { }
            }

            if (!notifyRemoteVanillaSquadmates ||
                !IsInMultiplayerRoom() ||
                !IsMasterClient() ||
                targetSquad == null ||
                targetSquad.Pointer == IntPtr.Zero ||
                !targetSquad.IsPlayerInSquad())
            {
                return;
            }

            try
            {
                var units = targetSquad.units;
                if (units == null)
                    return;

                Soldier sqLeader = targetSquad.Leader;
                IntPtr leaderPtr = sqLeader != null ? sqLeader.Pointer : IntPtr.Zero;
                _reusableNotifiedActors.Clear();

                int count = units.Count;
                for (int i = 0; i < count; i++)
                {
                    Soldier u = units[i];
                    if (u == null || !u.IsAlive || !u.IsPlayer())
                        continue;
                    if (requireLeader && u.Pointer != leaderPtr)
                        continue;

                    SyncSoldier syncher = u.soldierSyncher;
                    if (syncher == null || syncher.OwnSyncher)
                        continue;

                    Photon.Realtime.Player remotePlayer = syncher.Controller ?? syncher.Owner;
                    if (remotePlayer == null || remotePlayer.IsLocal || IsPhotonPlayerConfirmedModded(remotePlayer))
                        continue;

                    if (_reusableNotifiedActors.Add(remotePlayer.ActorNumber))
                    {
                        SendTargetedVanillaChatMessage(remotePlayer, message, minInterval);
                    }
                }
                _reusableNotifiedActors.Clear();
            }
            catch { }
        }

        private static readonly string[] WwiiPhoneticAlphabet =
        {
            "Able", "Baker", "Charlie", "Dog", "Easy", "Fox", "George", "How", "Item",
            "Jig", "King", "Love", "Mike", "Nan", "Oboe", "Peter", "Queen", "Roger",
            "Sugar", "Tare", "Uncle", "Victor", "William", "X-Ray", "Yoke", "Zebra"
        };

        /// <summary>
        /// Formats single-letter ER2 squad codes (<c>"A"</c>..<c>"Z"</c>) as authentic WWII Phonetic
        /// callsigns (<c>"Able Squad"</c>, <c>"Baker Squad"</c>, <c>"Easy Squad"</c>) so radio calls
        /// never collide mentally with Objective letters (<c>Point A-E</c>) or keybinds (<c>[E]</c>).
        /// </summary>
        public static string FormatSquadPhoneticCallsign(Squad sq)
        {
            if (sq == null || sq.Pointer == IntPtr.Zero)
                return "Allied Squad";

            string raw = null;
            try { raw = sq.squadName; } catch { }
            if (string.IsNullOrWhiteSpace(raw))
                return "Allied Squad";

            string trimmed = raw.Trim();
            if (trimmed.Length == 1)
            {
                char ch = char.ToUpperInvariant(trimmed[0]);
                if (ch >= 'A' && ch <= 'Z')
                    return $"{WwiiPhoneticAlphabet[ch - 'A']} Squad";
            }
            else if (trimmed.Length == 2 && char.IsLetter(trimmed[0]) && char.IsDigit(trimmed[1]))
            {
                char ch = char.ToUpperInvariant(trimmed[0]);
                if (ch >= 'A' && ch <= 'Z')
                    return $"{WwiiPhoneticAlphabet[ch - 'A']}-{trimmed[1]} Squad";
            }

            if (trimmed.IndexOf("Squad", StringComparison.OrdinalIgnoreCase) >= 0)
                return trimmed;

            return $"Squad {trimmed}";
        }

        public static string GetSoldierClassLabel(Soldier s)
        {
            if (s == null || s.Pointer == IntPtr.Zero)
                return "Soldier";

            try
            {
                if (s.IsAmmoBoxCarrier() &&
                    !s.hasMg &&
                    !s.IsSquadLeader() &&
                    !s.IsRadioman() &&
                    !s.hasSyringe &&
                    !s.isAtUnit &&
                    !s.isSapper &&
                    !s.isMarksMan)
                {
                    return "Ammo Carrier";
                }
            }
            catch { }

            string raw = null;
            try { raw = s.GetClassName(); } catch { }
            if (!string.IsNullOrWhiteSpace(raw) &&
                !raw.StartsWith("Missing", StringComparison.OrdinalIgnoreCase) &&
                !raw.StartsWith("loadout_", StringComparison.OrdinalIgnoreCase))
            {
                string trimmed = raw.Trim();
                return trimmed.Length > 24 ? trimmed.Substring(0, 24) : trimmed;
            }

            try
            {
                if (s.IsSquadLeader()) return "Squad Leader";
                if (s.IsRadioman()) return "Radioman";
                if (s.hasSyringe || s.IsMedic()) return "Medic";
                if (s.isAtUnit) return "Anti-Tank";
                if (s.hasGrenadeLauncher) return "Grenadier";
                if (s.isSapper) return "Sapper";
                if (s.isMarksMan) return "Marksman";
                if (s.hasMg) return "Machine Gunner";
                if (s.IsAmmoBoxCarrier()) return "Ammo Carrier";
            }
            catch { }

            return "Rifleman";
        }

        public static string GetVehicleDisplayNameShort(Vehicle veh)
        {
            if (veh == null || veh.Pointer == IntPtr.Zero)
                return "Allied Armor";

            string nid = null;
            try { nid = veh.name_id; } catch { }
            if (!string.IsNullOrWhiteSpace(nid) && _vehicleShortNameCache.TryGetValue(nid, out string cached))
                return cached;

            string resolved = null;
            if (!string.IsNullOrWhiteSpace(nid))
            {
                try
                {
                    string loc = Language.GetText(nid);
                    if (!string.IsNullOrWhiteSpace(loc) && !loc.StartsWith("Missing", StringComparison.OrdinalIgnoreCase))
                        resolved = loc.Trim();
                }
                catch { }
            }

            if (string.IsNullOrWhiteSpace(resolved))
            {
                try
                {
                    string rawName = veh.name;
                    if (!string.IsNullOrWhiteSpace(rawName))
                        resolved = rawName.Replace("(Clone)", "").Replace('_', ' ').Trim();
                }
                catch { }
            }

            if (string.IsNullOrWhiteSpace(resolved))
                resolved = "Allied Armor";

            if (!string.IsNullOrWhiteSpace(nid))
            {
                if (_vehicleShortNameCache.Count > 96)
                    _vehicleShortNameCache.Clear();
                _vehicleShortNameCache[nid] = resolved;
            }
            return resolved;
        }

        public static string FormatVehicleSupportCallsign(Vehicle veh, Squad crewSq, Vector3? listenerPos = null)
        {
            string vehName = GetVehicleDisplayNameShort(veh);
            string sqCall = crewSq != null && crewSq.Pointer != IntPtr.Zero ? FormatSquadPhoneticCallsign(crewSq) : null;
            string bearing = null;
            if (listenerPos.HasValue && veh != null && veh.Pointer != IntPtr.Zero)
            {
                try
                {
                    Transform tr = veh.transform;
                    if (tr != null)
                        bearing = FormatRelativeBearingAndDistance(listenerPos.Value, tr.position);
                }
                catch { }
            }

            if (!string.IsNullOrEmpty(sqCall) && !string.IsNullOrEmpty(bearing))
                return $"{vehName} ({sqCall}, {bearing})";
            if (!string.IsNullOrEmpty(bearing))
                return $"{vehName} ({bearing})";
            if (!string.IsNullOrEmpty(sqCall))
                return $"{vehName} ({sqCall})";
            return vehName;
        }

        public static string FormatInfantrySupportCallsign(Squad sq, Vector3? listenerPos = null)
        {
            string sqCall = FormatSquadPhoneticCallsign(sq);
            if (listenerPos.HasValue && sq != null && sq.Pointer != IntPtr.Zero)
            {
                try
                {
                    Soldier leader = sq.Leader;
                    if (leader != null && leader.transform != null)
                        return $"{sqCall} ({FormatRelativeBearingAndDistance(listenerPos.Value, leader.transform.position)})";
                }
                catch { }
            }
            return sqCall;
        }

        // =====================================================================
        // Global 0.45s Cached DayNightCycle Darkness / Rain Snapshot & Match Theater
        // =====================================================================
        private static float _nextNightSnapshotTime;
        public static float NightDarkness01 { get; private set; }
        public static float VanillaNightFactor { get; private set; } = 1.0f;
        public static float RainIntensity01 { get; private set; }

        public static void UpdateNightVisionSnapshotIfDue()
        {
            float now = Time.unscaledTime;
            if (now < _nextNightSnapshotTime)
                return;

            _nextNightSnapshotTime = now + 0.45f;
            try
            {
                var dnc = Corvostudio.SuperDayNightCycle.DayNightCycle.instance;
                if (dnc == null || dnc.Pointer == IntPtr.Zero)
                {
                    NightDarkness01 = 0f;
                    VanillaNightFactor = 1.0f;
                    RainIntensity01 = 0f;
                    return;
                }

                float rain = Mathf.Clamp01(Corvostudio.SuperDayNightCycle.DayNightCycle.currentRainIntensity);
                if (rain < 0.01f && Corvostudio.SuperDayNightCycle.DayNightCycle.lastSetWeather == WeatherType.Raining)
                    rain = 1.0f;
                RainIntensity01 = rain;

                float nv = Mathf.Clamp(Corvostudio.SuperDayNightCycle.DayNightCycle.NightVisionMultiplier, 0.10f, 1.0f);
                VanillaNightFactor = 0.20f + 0.80f * nv;

                float dayMult = Mathf.Clamp01(dnc.currentDayMultiplier);
                if (dayMult >= 0.78f && nv >= 0.92f)
                {
                    NightDarkness01 = 0f;
                    return;
                }

                float darkFromDay = 1.0f - Mathf.InverseLerp(0.05f, 0.78f, dayMult);
                float darkFromNv = 1.0f - Mathf.InverseLerp(0.16f, 0.92f, nv);
                float darkness = Mathf.Clamp01(darkFromDay * 0.68f + darkFromNv * 0.32f);
                float clouds = Mathf.Clamp01(dnc.cloudsAmount);
                darkness = Mathf.Clamp01(darkness * (1.0f + 0.18f * clouds));

                float lightning = Mathf.Clamp01(Corvostudio.SuperDayNightCycle.DayNightCycle.lightningStrength);
                if (lightning > 0.12f)
                {
                    darkness *= Mathf.Clamp01(1.0f - 0.75f * lightning);
                }

                NightDarkness01 = darkness;
            }
            catch
            {
                NightDarkness01 = 0f;
                VanillaNightFactor = 1.0f;
                RainIntensity01 = 0f;
            }
        }

        public enum BattleAtmosphereArchetype : byte
        {
            None = 0,
            Order227NotOneStepBack = 1,
            KesselEncircledPocket = 2,
            UrbanFortress = 3,
            AmphibiousBeachhead = 4,
            SiegeOfBastogne = 5,
            PacificBunkerAndNightGyokusai = 6,
            MountainRidgeGrind = 7,
            BlitzkriegSchwerpunkt = 8,
            NightAirborneCoupDeMain = 9
        }

        public struct BattleTheaterSnapshot
        {
            public IntPtr BattleDataPtr;
            public Gamemap Map;
            public int Year;
            public string InvadersFactionId;
            public string DefendersFactionId;
            public string InvadersBattalionLower;
            public string DefendersBattalionLower;
            public string LocationLower;
            public bool IsNightStart;
            public BattleAtmosphereArchetype Atmosphere;
            public bool IsAmphibiousBeachMap;
            public bool IsOpenDesertOrSteppeMap;
            public bool IsDenseUrbanMap;
            public bool IsJungleOrAtollMap;

            public string GetBattalionLowerForFaction(string faction)
            {
                if (string.IsNullOrEmpty(faction))
                    return string.Empty;
                if (!string.IsNullOrEmpty(InvadersFactionId) && AreSameSideFactions(faction, InvadersFactionId))
                    return InvadersBattalionLower ?? string.Empty;
                if (!string.IsNullOrEmpty(DefendersFactionId) && AreSameSideFactions(faction, DefendersFactionId))
                    return DefendersBattalionLower ?? string.Empty;
                return string.Empty;
            }
        }

        private static BattleTheaterSnapshot _cachedTheaterSnapshot;
        public static int BattleTheaterGeneration { get; private set; } = 1;

        public static BattleAtmosphereArchetype ResolveBattleAtmosphere(
            string mapLower,
            string locationLower,
            string invFac,
            string defFac,
            string invBatLower,
            string defBatLower,
            int year,
            bool isNightStart,
            bool isBeach,
            bool isUrban,
            bool isJungleAtoll,
            bool isSteepRidge)
        {
            mapLower ??= string.Empty;
            locationLower ??= string.Empty;
            invFac ??= string.Empty;
            defFac ??= string.Empty;
            invBatLower ??= string.Empty;
            defBatLower ??= string.Empty;

            // 1. Kessel / Encircled Pocket
            if (locationLower.Contains("gumrak") ||
                locationLower.Contains("pocket") ||
                locationLower.Contains("noose") ||
                locationLower.Contains("kessel") ||
                locationLower.Contains("budapest has fallen") ||
                locationLower.Contains("encircl") ||
                locationLower.Contains("trapped") ||
                (mapLower.Contains("stalingrad") && year >= 1943 && defFac.StartsWith("Germany", StringComparison.OrdinalIgnoreCase)))
            {
                return BattleAtmosphereArchetype.KesselEncircledPocket;
            }

            // 2. Order No. 227 ("Not One Step Back")
            if ((mapLower.Contains("stalingrad") ||
                 locationLower.Contains("volga") ||
                 locationLower.Contains("mamayev") ||
                 locationLower.Contains("red october") ||
                 locationLower.Contains("pavlov") ||
                 locationLower.Contains("univermag") ||
                 locationLower.Contains("barrikady") ||
                 locationLower.Contains("tractor") ||
                 locationLower.Contains("grain elevator")) &&
                (defFac.StartsWith("Ussr", StringComparison.OrdinalIgnoreCase) ||
                 invFac.StartsWith("Ussr", StringComparison.OrdinalIgnoreCase)))
            {
                return BattleAtmosphereArchetype.Order227NotOneStepBack;
            }

            // 3. Siege of Bastogne ("NUTS!")
            if (locationLower.Contains("bastogne") ||
                locationLower.Contains("foy") ||
                locationLower.Contains("noville") ||
                locationLower.Contains("marvie") ||
                locationLower.Contains("nuts") ||
                ((mapLower.Contains("bulge") || mapLower.Contains("ardennes")) &&
                 (defBatLower.Contains("101") || defBatLower.Contains("airborne") || locationLower.Contains("siege"))))
            {
                return BattleAtmosphereArchetype.SiegeOfBastogne;
            }

            // 4. Night Coup de Main / Airborne
            if (locationLower.Contains("pegasus") ||
                locationLower.Contains("sainte-m") ||
                locationLower.Contains("sainte m") ||
                locationLower.Contains("st mere") ||
                locationLower.Contains("advance by darkness") ||
                locationLower.Contains("airfield drop") ||
                locationLower.Contains("paradrop") ||
                locationLower.Contains("drop zone") ||
                (isNightStart && !isJungleAtoll &&
                 (invBatLower.Contains("airborne") ||
                  invBatLower.Contains("parachut") ||
                  invBatLower.Contains("fallschirm") ||
                  invBatLower.Contains("glider"))))
            {
                return BattleAtmosphereArchetype.NightAirborneCoupDeMain;
            }

            // 5. Pacific Bunker & Night Gyokusai (night/bunker Pacific island phases beat generic beach tag)
            bool hasJapan = defFac.StartsWith("Japan", StringComparison.OrdinalIgnoreCase) ||
                            invFac.StartsWith("Japan", StringComparison.OrdinalIgnoreCase);
            bool pacificIsland = isJungleAtoll ||
                                 mapLower.Contains("kwajalein") ||
                                 mapLower.Contains("makin") ||
                                 mapLower.Contains("roinamur");
            if (pacificIsland && hasJapan &&
                (isNightStart ||
                 locationLower.Contains("midnight") ||
                 locationLower.Contains("last of") ||
                 locationLower.Contains("bunker") ||
                 locationLower.Contains("pillbox")))
            {
                return BattleAtmosphereArchetype.PacificBunkerAndNightGyokusai;
            }

            // 6. Amphibious Beachhead Assault
            if (locationLower.Contains("omaha") ||
                locationLower.Contains("utah") ||
                locationLower.Contains("gold") ||
                locationLower.Contains("juno") ||
                locationLower.Contains("sword") ||
                (locationLower.Contains("pointe du hoc") && !locationLower.Contains("counterattack")) ||
                locationLower.Contains("beach") ||
                locationLower.Contains("landing") ||
                locationLower.Contains("amphib") ||
                locationLower.Contains("shore") ||
                locationLower.Contains("anzio") ||
                locationLower.Contains("d-day") ||
                locationLower.Contains("red 1") ||
                locationLower.Contains("red 2") ||
                locationLower.Contains("blue 1") ||
                (isBeach && (locationLower.Contains("invasion") || locationLower.Contains("harbor") || locationLower.Contains("harbour"))))
            {
                return BattleAtmosphereArchetype.AmphibiousBeachhead;
            }

            // 5b. Inland Pacific Bunker & Night Gyokusai fallback
            if (pacificIsland && hasJapan)
            {
                return BattleAtmosphereArchetype.PacificBunkerAndNightGyokusai;
            }

            // 7. Mountain / Ridge Grind
            if ((locationLower.Contains("cassino") && !locationLower.Contains("streets") && !locationLower.Contains("town")) ||
                locationLower.Contains("snakeshead") ||
                locationLower.Contains("castle hill") ||
                locationLower.Contains("takrouna") ||
                locationLower.Contains("longstop") ||
                locationLower.Contains("monte") ||
                locationLower.Contains("ridge") ||
                locationLower.Contains("summit") ||
                locationLower.Contains("monastery") ||
                locationLower.Contains("abbey") ||
                (isSteepRidge && !isUrban))
            {
                return BattleAtmosphereArchetype.MountainRidgeGrind;
            }

            // 8. Blitzkrieg / Schwerpunkt
            if (mapLower.Contains("stonne") ||
                mapLower.Contains("balaton") ||
                locationLower.Contains("sedan") ||
                locationLower.Contains("stonne") ||
                locationLower.Contains("kasserine") ||
                locationLower.Contains("sidi-bou-zid") ||
                locationLower.Contains("sidi bou zid") ||
                locationLower.Contains("frühlingswind") ||
                locationLower.Contains("fruhlingswind") ||
                locationLower.Contains("wiking") ||
                locationLower.Contains("blitz") ||
                locationLower.Contains("spearhead") ||
                locationLower.Contains("breakthrough"))
            {
                return BattleAtmosphereArchetype.BlitzkriegSchwerpunkt;
            }

            // 9. Urban Fortress / House-to-House
            if (isUrban ||
                locationLower.Contains("sihang") ||
                locationLower.Contains("warehouse") ||
                locationLower.Contains("guanghua") ||
                locationLower.Contains("house to house") ||
                locationLower.Contains("house-to-house") ||
                locationLower.Contains("streets") ||
                locationLower.Contains("block by block") ||
                locationLower.Contains("factory") ||
                locationLower.Contains("citadel") ||
                locationLower.Contains("fortress"))
            {
                return BattleAtmosphereArchetype.UrbanFortress;
            }

            return BattleAtmosphereArchetype.None;
        }

        public static string GetBattleAtmosphereDispatchText(BattleAtmosphereArchetype arch)
        {
            switch (arch)
            {
                case BattleAtmosphereArchetype.Order227NotOneStepBack:
                    return "[HQ Dispatch] ORDER NO. 227: 'Not one step back!' - Soviet squads hold firm around officers; storm-group CQB active.";
                case BattleAtmosphereArchetype.KesselEncircledPocket:
                    return "[HQ Dispatch] ENCIRCLED POCKET: Supply lines severed - defenders scavenge aggressively and hold fortified strongpoints.";
                case BattleAtmosphereArchetype.UrbanFortress:
                    return "[HQ Dispatch] URBAN FORTRESS: House-to-house doctrine - heavy grenade clearing and dogged strongpoint defense.";
                case BattleAtmosphereArchetype.AmphibiousBeachhead:
                    return "[HQ Dispatch] AMPHIBIOUS ASSAULT: Push off the beach under smoke - coastal strongpoints heavily manned.";
                case BattleAtmosphereArchetype.SiegeOfBastogne:
                    return "[HQ Dispatch] SIEGE OF BASTOGNE ('NUTS!'): Perimeter hold - defenders dig in tight with heightened squad cohesion.";
                case BattleAtmosphereArchetype.PacificBunkerAndNightGyokusai:
                    return "[HQ Dispatch] ISLAND GARRISON: Defenders dug deep into bunkers - stay alert for close-quarters night counterattacks.";
                case BattleAtmosphereArchetype.MountainRidgeGrind:
                    return "[HQ Dispatch] RIDGE ASSAULT: Uphill fire lanes covered - smoke screens and fire-base suppression emphasized.";
                case BattleAtmosphereArchetype.BlitzkriegSchwerpunkt:
                    return "[HQ Dispatch] SCHWERPUNKT: Rapid armored spearhead - attackers press aggressive breakthroughs; AT strongpoints on alert.";
                case BattleAtmosphereArchetype.NightAirborneCoupDeMain:
                    return "[HQ Dispatch] NIGHT AIRBORNE OP: Low visibility - autonomous small-unit flanking and close-quarters shock active.";
                default:
                    return string.Empty;
            }
        }

        public static BattleTheaterSnapshot GetBattleTheaterSnapshot()
        {
            try
            {
                BattleData bd = BattleManager.GetCurrentBattleData();
                IntPtr bdPtr = (bd != null && bd.Pointer != IntPtr.Zero) ? bd.Pointer : IntPtr.Zero;
                if (bdPtr == _cachedTheaterSnapshot.BattleDataPtr)
                    return _cachedTheaterSnapshot;

                if (bdPtr == IntPtr.Zero)
                {
                    _cachedTheaterSnapshot = default;
                    BattleTheaterGeneration++;
                    return _cachedTheaterSnapshot;
                }

                Gamemap map = bd.map;
                int year = bd.year;
                string invFac = InternFaction(bd.invadersFaction_id ?? string.Empty);
                string defFac = InternFaction(bd.defendersFaction_id ?? string.Empty);
                string invBat = ((bd.invadersBatalion_id ?? string.Empty) + " " + (bd.invadersBatalion ?? string.Empty)).Trim().ToLowerInvariant();
                string defBat = ((bd.defendersBatalion_id ?? string.Empty) + " " + (bd.defendersBatalion ?? string.Empty)).Trim().ToLowerInvariant();
                string locDisplay = string.Empty;
                try { locDisplay = bd.GetLocationName() ?? string.Empty; } catch { }
                string locLower = (locDisplay + " " + (bd.location_name ?? string.Empty) + " " + (bd.location_name_id ?? string.Empty)).Trim().ToLowerInvariant();
                string mapLower = map.ToString().ToLowerInvariant();
                bool isNightStart = bd.TimeHH >= 20 || bd.TimeHH <= 5;

                bool isBeach = map == Gamemap.Normandy ||
                               map == Gamemap.Kwajalein ||
                               map == Gamemap.RoiNamur ||
                               map == Gamemap.Makin ||
                               map == Gamemap.Anzio ||
                               map == Gamemap.Kos;

                bool isDesert = map == Gamemap.TunisiaNorth ||
                                map == Gamemap.TunisiaSouth ||
                                map == Gamemap.Nomonhan ||
                                map == Gamemap.Balaton ||
                                map == Gamemap.Aberdeen;

                bool isUrban = map == Gamemap.Stalingrad ||
                               map == Gamemap.Shanghai ||
                               map == Gamemap.Nanking ||
                               map == Gamemap.Budapest ||
                               map == Gamemap.Cassino;

                bool isJungleAtoll = map == Gamemap.Kwajalein ||
                                     map == Gamemap.RoiNamur ||
                                     map == Gamemap.Makin;

                bool isSteepRidge = map == Gamemap.Cassino ||
                                    locLower.Contains("ridge") ||
                                    locLower.Contains("summit") ||
                                    locLower.Contains("hill") ||
                                    locLower.Contains("takrouna") ||
                                    locLower.Contains("snakeshead");

                BattleAtmosphereArchetype atmosphere = ResolveBattleAtmosphere(
                    mapLower,
                    locLower,
                    invFac,
                    defFac,
                    invBat,
                    defBat,
                    year,
                    isNightStart,
                    isBeach,
                    isUrban,
                    isJungleAtoll,
                    isSteepRidge);

                _cachedTheaterSnapshot = new BattleTheaterSnapshot
                {
                    BattleDataPtr = bdPtr,
                    Map = map,
                    Year = year,
                    InvadersFactionId = invFac,
                    DefendersFactionId = defFac,
                    InvadersBattalionLower = invBat,
                    DefendersBattalionLower = defBat,
                    LocationLower = locLower,
                    IsNightStart = isNightStart,
                    Atmosphere = atmosphere,
                    IsAmphibiousBeachMap = isBeach,
                    IsOpenDesertOrSteppeMap = isDesert,
                    IsDenseUrbanMap = isUrban,
                    IsJungleOrAtollMap = isJungleAtoll
                };
                BattleTheaterGeneration++;
            }
            catch { }

            return _cachedTheaterSnapshot;
        }

        private static readonly string[] CompassPoints8 = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };

        /// <summary>
        /// Formats a relative tactical distance (rounded to 5m) and 8-wind compass bearing
        /// so player-facing radio messages never display raw <c>(x, z)</c> world coordinates.
        /// </summary>
        public static string FormatRelativeBearingAndDistance(Vector3 fromPos, Vector3 targetPos)
        {
            float dx = targetPos.x - fromPos.x;
            float dz = targetPos.z - fromPos.z;
            float dist = Mathf.Sqrt(dx * dx + dz * dz);
            int roundedDist = dist < 5f ? 5 : Mathf.RoundToInt(dist / 5f) * 5;
            if (dist <= 0.1f)
                return $"{roundedDist}m";

            float deg = Mathf.Atan2(dx, dz) * Mathf.Rad2Deg;
            if (deg < 0f)
                deg += 360f;
            int idx = Mathf.RoundToInt(deg / 45f) & 7;
            return $"{roundedDist}m {CompassPoints8[idx]}";
        }

        /// <summary>
        /// Resolves a vanilla-style objective label matching <c>ObjectiveGUI.RefreshLetterOrIcon</c>
        /// (<c>Point A</c>, <c>Point B</c>, etc. over <c>BattleManager.ActiveObjectives(true)</c> order)
        /// combined with <see cref="MissionObjective.GetDisplayName"/> when set.
        /// </summary>
        public static string GetVanillaObjectiveDisplayName(MissionObjective obj, Vector3? listenerPos, out string shortLetter)
        {
            shortLetter = string.Empty;
            if (obj == null || obj.Pointer == IntPtr.Zero)
                return "Objective";

            IntPtr targetPtr = obj.Pointer;
            int objIndex = -1;
            try
            {
                int runningIdx = 0;
                BattlePhase phase = TryGetCurrentBattlePhase(BattleManager.instance);
                MissionObjective[] phaseObjs = phase != null ? phase.objectives : null;
                if (phaseObjs != null)
                {
                    for (int i = 0; i < phaseObjs.Length; i++)
                    {
                        MissionObjective cand = phaseObjs[i];
                        if (cand == null || cand.Pointer == IntPtr.Zero)
                            continue;
                        if (cand.Pointer == targetPtr)
                        {
                            objIndex = runningIdx;
                            break;
                        }
                        runningIdx++;
                    }
                }

                if (objIndex < 0)
                {
                    var scriptedList = MissionObjectiveScripted.scriptedMissionObjectives;
                    if (scriptedList != null)
                    {
                        int sCount = scriptedList.Count;
                        for (int i = 0; i < sCount; i++)
                        {
                            MissionObjectiveScripted cand = scriptedList[i];
                            if (cand == null || cand.Pointer == IntPtr.Zero)
                                continue;
                            if (cand.Pointer == targetPtr)
                            {
                                objIndex = runningIdx;
                                break;
                            }
                            runningIdx++;
                        }
                    }
                }
            }
            catch { }

            if (objIndex >= 0 && objIndex < 26)
                shortLetter = $"Point {(char)('A' + objIndex)}";
            else if (objIndex >= 26)
                shortLetter = $"Point #{objIndex + 1}";

            string displayName = string.Empty;
            try { displayName = obj.GetDisplayName(); } catch { }
            if (!string.IsNullOrEmpty(displayName))
                displayName = displayName.Trim();

            if (!string.IsNullOrEmpty(shortLetter))
            {
                if (string.IsNullOrEmpty(displayName))
                    return shortLetter;

                string letterOnly = objIndex < 26 ? ((char)('A' + objIndex)).ToString() : string.Empty;
                if (string.Equals(displayName, shortLetter, StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrEmpty(letterOnly) && string.Equals(displayName, letterOnly, StringComparison.OrdinalIgnoreCase)))
                {
                    return shortLetter;
                }

                return $"{shortLetter} ({displayName})";
            }

            if (!string.IsNullOrEmpty(displayName))
                return displayName;

            if (listenerPos.HasValue)
            {
                try { return FormatRelativeBearingAndDistance(listenerPos.Value, obj.GetTaskPosition()); }
                catch { }
            }

            return "Assigned Objective";
        }

        public static string GetVanillaObjectiveDisplayName(MissionObjective obj, Vector3? listenerPos = null)
        {
            return GetVanillaObjectiveDisplayName(obj, listenerPos, out _);
        }

        /// <summary>
        /// Returns true only when <paramref name="s"/> is a living, non-incapacitated prisoner who has
        /// genuinely surrendered (not a downed <c>IsInjured</c> casualty or a retreating soldier in their
        /// 3-second <see cref="Soldier.OnRetreat"/> despawn window).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsTrueSurrenderedPrisoner(Soldier s, SoldierFastHandles h = null)
        {
            if (ReferenceEquals(s, null) || s.Pointer == IntPtr.Zero || !s.IsAlive || s.IsInjured || s.IsDead || !s.HasSurrended())
                return false;

            if (h == null)
                _soldierHandlesByPtr.TryGetValue(s.Pointer, out h);

            return h == null || !h.IsRetreatDespawning;
        }

        public static void FlushExpiredRetreatDespawningSoldiers(float now)
        {
            if (_soldierHandlesByPtr.Count == 0)
                return;

            foreach (var kvp in _soldierHandlesByPtr)
            {
                SoldierFastHandles h = kvp.Value;
                if (h == null || !h.IsRetreatDespawning || h.RetreatDespawnForceCleanupAt <= 0f || now < h.RetreatDespawnForceCleanupAt)
                    continue;

                Soldier s = h.Ref;
                if (now > h.RetreatDespawnForceCleanupAt + 2.0f &&
                    now >= h.NextAnomalyAlertTime &&
                    !ReferenceEquals(s, null) &&
                    s != null)
                {
                    h.NextAnomalyAlertTime = now + 5.0f;
                    AnomalyRetreatStallCount++;
                    BotActionTrace.RecordEvent(s, BotTraceEventKind.AnomalyAlert, "retreat_despawn_stalled", now - h.RetreatDespawnForceCleanupAt);
                }

                h.RetreatDespawnForceCleanupAt = now + 2.0f;
                if (ReferenceEquals(s, null) || s == null || s.Pointer == IntPtr.Zero || s.IsPlayer() || !IsLocallyOwnedSoldier(s))
                    continue;

                GameObject go = s.gameObject;
                if (go == null)
                    continue;

                if (s.joinedSquad != null)
                    s.joinedSquad.Leave(s, true);

                ResourcesManager.CleanupNow(go);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsSoldierInVehicleOrTransition(Soldier s)
        {
            return ReferenceEquals(s, null) ||
                   s.Pointer == IntPtr.Zero ||
                   s.IsOnVehicle() ||
                   s.IsEnteringVehicle ||
                   s.IsExitingFromVehicle();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SyncSoldier GetCachedSoldierSyncher(SoldierFastHandles h)
        {
            if (h == null || ReferenceEquals(h.Ref, null))
                return null;

            SyncSoldier syn = h.Syncher;
            if (!ReferenceEquals(syn, null))
                return syn != null ? syn : null;

            if (!h.SyncherChecked)
            {
                syn = h.Ref.soldierSyncher;
                if (!ReferenceEquals(syn, null) && syn != null)
                {
                    h.Syncher = syn;
                    h.SyncherChecked = true;
                    return syn;
                }
                if (Time.time - h.CreatedAtTime >= 1.5f)
                    h.SyncherChecked = true;
            }
            return null;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SyncVehicle GetCachedVehicleSyncher(VehicleFastHandles vh)
        {
            if (vh == null || ReferenceEquals(vh.Ref, null))
                return null;

            SyncVehicle syn = vh.Syncher;
            if (!ReferenceEquals(syn, null))
                return syn != null ? syn : null;

            if (!vh.SyncherChecked)
            {
                syn = vh.Ref.vehicleSyncher;
                if (!ReferenceEquals(syn, null) && syn != null)
                {
                    vh.Syncher = syn;
                    vh.SyncherChecked = true;
                    return syn;
                }
                if (Time.time - vh.CreatedAtTime >= 1.5f)
                    vh.SyncherChecked = true;
            }
            return null;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsLocallyOwnedBot(SoldierFastHandles h)
        {
            if (h == null || h.Ref == null || !h.Ref.IsAlive || h.Ref.IsPlayer())
                return false;

            if (!IsInMultiplayerRoom())
                return true;

            SyncSoldier syn = GetCachedSoldierSyncher(h);
            return syn == null || (syn.OwnSyncher && !syn.controlled_by_player);
        }

        public static bool IsLocallyOwnedBot(Soldier s)
        {
            if (ReferenceEquals(s, null) || s.Pointer == IntPtr.Zero)
                return false;
            return IsLocallyOwnedBot(GetOrCreateSoldierHandles(s));
        }

        public static bool IsLocallyOwnedSoldier(Soldier s)
        {
            if (ReferenceEquals(s, null) || s.Pointer == IntPtr.Zero)
                return false;
            SoldierFastHandles h = GetOrCreateSoldierHandles(s);
            if (h == null || !h.Ref.IsAlive)
                return false;

            if (!IsInMultiplayerRoom())
                return true;

            SyncSoldier syn = GetCachedSoldierSyncher(h);
            return syn == null || syn.OwnSyncher;
        }

        public static bool IsLocallyOwnedVehicleSyncher(Vehicle v)
        {
            if (ReferenceEquals(v, null) || v.Pointer == IntPtr.Zero)
                return false;

            if (!IsInMultiplayerRoom())
                return true;

            VehicleFastHandles vh = GetOrCreateVehicleHandles(v);
            if (vh == null)
                return false;
            SyncVehicle syn = GetCachedVehicleSyncher(vh);
            return syn == null || syn.OwnSyncher;
        }

        public static bool IsLocallyOwnedVehicle(Vehicle v)
        {
            if (ReferenceEquals(v, null) || v.Pointer == IntPtr.Zero)
                return false;

            if (!IsInMultiplayerRoom())
                return v != null;

            VehicleFastHandles vh = GetOrCreateVehicleHandles(v);
            if (vh == null)
                return false;

            if (v.HasDriverAlive)
                return v.OwnDriverOrVehicle();

            SyncVehicle syn = GetCachedVehicleSyncher(vh);
            return syn == null || syn.OwnSyncher;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool RequiresTrainedVehicleCrew(VehicleFastHandles vh, Vehicle veh)
        {
            if (vh != null)
                return vh.IsTank || vh.IsAir || vh.IsPlane;
            if (veh == null || veh.Pointer == IntPtr.Zero)
                return false;
            return veh.IsAirVehicle() || veh.TryCast<VehicleTank>() != null;
        }

        public static AIVehicle GetOrCacheAIVehicle(Vehicle veh, VehicleFastHandles vh = null)
        {
            if (veh == null || veh.Pointer == IntPtr.Zero)
                return null;
            if (vh == null)
                vh = GetOrCreateVehicleHandles(veh);
            if (vh == null)
                return null;
            if (!vh.AiVehChecked)
            {
                vh.AiVehChecked = true;
                vh.AiVeh = veh.GetComponent<AIVehicle>();
            }
            return vh.AiVeh;
        }

        /// <summary>
        /// Checks whether the human player controlling <paramref name="s"/> has published the
        /// <c>"bh_ver"</c> Photon custom property (cached for 5s per ActorNumber).
        /// Returns true for local players/bots or on error so Host-assist only activates for confirmed unmodded remote players.
        /// </summary>
        public static bool IsPlayerRunningBattleHardened(Soldier s)
        {
            if (s == null || s.Pointer == IntPtr.Zero)
                return true;

            try
            {
                SyncSoldier syncher = TryGetSoldierHandles(s.Pointer, out SoldierFastHandles ph)
                    ? GetCachedSoldierSyncher(ph)
                    : s.soldierSyncher;
                if (syncher == null || syncher.OwnSyncher)
                    return true;

                Photon.Realtime.Player controller = syncher.Controller ?? syncher.Owner;
                if (controller == null)
                    return true;

                return IsPhotonPlayerConfirmedModded(controller);
            }
            catch
            {
                return true;
            }
        }

        public enum BotRpcPriority : byte
        {
            Normal = 0,
            Low = 1
        }

        public static int ActiveSoldierHandlesCount => _soldierHandlesByPtr.Count;
        public static int ActiveVehicleHandlesCount => _vehicleHandlesByPtr.Count;
        public static int MoveOverridePreemptionsCount { get; private set; }
        public static int BotRpcBudgetGrantedCount { get; private set; }
        public static int BotRpcBudgetThrottledCount { get; private set; }
        public static int CachedMaxRemotePingMs { get; private set; } = -1;
        public static int CachedEffectiveMaxRttMs { get; private set; }
        public static float CachedCoopRpcCongestionScale { get; private set; } = 1.0f;

        private static readonly Dictionary<string, int> _caughtExceptionCountsBySite = new Dictionary<string, int>(32, StringComparer.Ordinal);
        public static int TotalCaughtPatchExceptions;
        public static string FirstCaughtPatchExceptionSite = string.Empty;
        public static int AnomalyZombieCount;
        public static int AnomalyPrisonerCount;
        public static int AnomalyMeleeCount;
        public static int AnomalySurrenderCount;
        public static int AnomalyRetreatStallCount;
        public static int AnomalyVehicleStuckCount;
        private static float _matchStartUnscaledTime = -1f;
        private static bool _matchEndSummaryEmitted;

        public static void ResetMatchTelemetryAndExceptions()
        {
            _caughtExceptionCountsBySite.Clear();
            TotalCaughtPatchExceptions = 0;
            FirstCaughtPatchExceptionSite = string.Empty;
            AnomalyZombieCount = 0;
            AnomalyPrisonerCount = 0;
            AnomalyMeleeCount = 0;
            AnomalySurrenderCount = 0;
            AnomalyRetreatStallCount = 0;
            AnomalyVehicleStuckCount = 0;
            _matchStartUnscaledTime = Time.unscaledTime;
            _matchEndSummaryEmitted = false;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void ReportPatchException(string site, Exception ex)
        {
            TotalCaughtPatchExceptions++;
            if (ex == null)
                return;
            if (string.IsNullOrEmpty(site))
                site = "Unknown";

            int count;
            if (_caughtExceptionCountsBySite.TryGetValue(site, out int prev))
            {
                count = prev + 1;
            }
            else
            {
                if (_caughtExceptionCountsBySite.Count >= 64)
                    return;
                count = 1;
            }
            _caughtExceptionCountsBySite[site] = count;

            if (count == 1)
            {
                if (string.IsNullOrEmpty(FirstCaughtPatchExceptionSite))
                    FirstCaughtPatchExceptionSite = site;
                EnqueueAsyncLog(true, $"[PatchErr:{site}] {ex.GetType().Name}: {ex.Message}");
            }
            else if (count == 10 || count == 100 || count == 1000)
            {
                EnqueueAsyncLog(true, $"[PatchErr:{site}] count={count} (last: {ex.GetType().Name}: {ex.Message})");
            }
        }

        public static void EmitMatchEndHealthSummary()
        {
            if (_matchEndSummaryEmitted || !VisionConfig.EnableMod.Value)
                return;

            _matchEndSummaryEmitted = true;
            float dur = _matchStartUnscaledTime >= 0f
                ? Mathf.Max(0f, Time.unscaledTime - _matchStartUnscaledTime)
                : Mathf.Max(0f, Time.time);

            string errSuffix = string.Empty;
            if (TotalCaughtPatchExceptions > 0 && _caughtExceptionCountsBySite.Count > 0)
            {
                string s1 = null, s2 = null, s3 = null;
                int c1 = 0, c2 = 0, c3 = 0;
                foreach (var kv in _caughtExceptionCountsBySite)
                {
                    int v = kv.Value;
                    if (v > c1) { c3 = c2; s3 = s2; c2 = c1; s2 = s1; c1 = v; s1 = kv.Key; }
                    else if (v > c2) { c3 = c2; s3 = s2; c2 = v; s2 = kv.Key; }
                    else if (v > c3) { c3 = v; s3 = kv.Key; }
                }
                errSuffix = c3 > 0 ? $" ({s1}={c1},{s2}={c2},{s3}={c3})"
                    : c2 > 0 ? $" ({s1}={c1},{s2}={c2})"
                    : $" ({s1}={c1})";
            }

            EnqueueAsyncLog(
                false,
                $"[MatchSummary] Dur:{dur:F0}s | MaxFrame:{RollingMaxFrameMs:F1}ms Hitches:{TotalHitchesOver28ms} | PatchErrs:{TotalCaughtPatchExceptions}{errSuffix} | Stuck:{VeteranAndTacticsPatches.FootBotUnsticksCount}/{VeteranAndTacticsPatches.TankRockUnsticksCount} Vault:{VeteranAndTacticsPatches.WallsVaulted}(Rej:{VeteranAndTacticsPatches.SlopedVaultsRejectedCount}) | Anoms[Zombie:{AnomalyZombieCount} Pris:{AnomalyPrisonerCount} Melee:{AnomalyMeleeCount} Surr:{AnomalySurrenderCount} RetStall:{AnomalyRetreatStallCount} VehStuck:{AnomalyVehicleStuckCount}] | BotRPC:{BotRpcBudgetGrantedCount}/{BotRpcBudgetThrottledCount}{GetCoopRpcTelemetrySuffix()}");

            EmergentRuntimeBridge.EmitTelemetrySummary("match_end");
        }

        public static string GetCoopRpcTelemetrySuffix()
        {
            return GetConnectedPlayerCount() > 1
                ? $" (RTT:{CachedEffectiveMaxRttMs}ms/{CachedCoopRpcCongestionScale:F1}x)"
                : string.Empty;
        }

        private static readonly int[] _remoteRttSampleScratch = new int[32];

        private static void ResetCoopRpcNetworkState()
        {
            BotRpcBudgetGrantedCount = 0;
            BotRpcBudgetThrottledCount = 0;
            _botRpcNormalTokens = 3.0f;
            _botRpcNormalLastRefillTime = 0f;
            _nextAllowedLowPriorityBotRpcTime = 0f;
            _nextCoopNetHealthSampleTime = 0f;
            _coopBaselineRttMs = -1f;
            _lastSampledResentReliableCommands = -1;
            _coopRecentResendsPerSec = 0f;
            CachedMaxRemotePingMs = -1;
            CachedEffectiveMaxRttMs = 0;
            CachedCoopRpcCongestionScale = 1.0f;
        }

        private static bool TryReadPlayerPingMs(Photon.Realtime.Player player, out short pingMs)
        {
            pingMs = -1;
            if (player == null)
                return false;

            try
            {
                var props = player.CustomProperties;
                if (props == null)
                    return false;

                if (_propKeyPingObj == null)
                    _propKeyPingObj = ((Il2CppSystem.String)"ping").Cast<Il2CppSystem.Object>();

                if (!props.ContainsKey(_propKeyPingObj))
                    return false;

                Il2CppSystem.Object raw = props[_propKeyPingObj];
                if (raw == null)
                    return false;

                try
                {
                    pingMs = raw.Unbox<short>();
                    return true;
                }
                catch
                {
                    if (short.TryParse(raw.ToString(), out pingMs))
                        return true;
                }
            }
            catch { }

            return false;
        }

        private static void RefreshCoopNetworkHealthIfDue()
        {
            float nowUnscaled = Time.unscaledTime;
            if (nowUnscaled < _nextCoopNetHealthSampleTime && _nextCoopNetHealthSampleTime - nowUnscaled <= 5.0f)
                return;

            float elapsed = _nextCoopNetHealthSampleTime > 0f
                ? Mathf.Clamp(nowUnscaled - (_nextCoopNetHealthSampleTime - 1.5f), 0.25f, 10.0f)
                : 1.5f;
            _nextCoopNetHealthSampleTime = nowUnscaled + 1.5f;

            try
            {
                int localRelayPing = Mathf.Max(0, PhotonNetwork.GetPing());
                int maxRemoteOneWayMs = -1;
                int remoteCount = 0;

                var room = PhotonNetwork.CurrentRoom;
                var playersMap = room != null ? room.Players : null;
                if (playersMap != null)
                {
                    foreach (var kv in playersMap)
                    {
                        Photon.Realtime.Player p = kv.Value;
                        if (p == null || p.IsLocal)
                            continue;

                        if (TryReadPlayerPingMs(p, out short pPing) && pPing >= 0 && pPing <= 5000)
                        {
                            if (pPing > maxRemoteOneWayMs)
                                maxRemoteOneWayMs = pPing;
                            if (remoteCount < _remoteRttSampleScratch.Length)
                                _remoteRttSampleScratch[remoteCount++] = pPing * 2;
                        }
                    }
                }

                for (int i = 1; i < remoteCount; i++)
                {
                    int key = _remoteRttSampleScratch[i];
                    int j = i - 1;
                    while (j >= 0 && _remoteRttSampleScratch[j] > key)
                    {
                        _remoteRttSampleScratch[j + 1] = _remoteRttSampleScratch[j];
                        j--;
                    }
                    _remoteRttSampleScratch[j + 1] = key;
                }
                int medianRemoteRtt = remoteCount > 0 ? _remoteRttSampleScratch[(remoteCount - 1) / 2] : -1;

                CachedMaxRemotePingMs = maxRemoteOneWayMs;
                int effectiveRtt = remoteCount > 0
                    ? Mathf.Max(medianRemoteRtt, localRelayPing)
                    : localRelayPing;
                CachedEffectiveMaxRttMs = effectiveRtt;

                if (effectiveRtt > 0)
                {
                    if (_coopBaselineRttMs <= 0f)
                        _coopBaselineRttMs = effectiveRtt;
                    else if (effectiveRtt < _coopBaselineRttMs)
                        _coopBaselineRttMs = Mathf.Lerp(_coopBaselineRttMs, effectiveRtt, 0.30f);
                    else
                        _coopBaselineRttMs = Mathf.Min(effectiveRtt, _coopBaselineRttMs + 4.0f);
                }

                int curResends = PhotonNetwork.ResentReliableCommands;
                if (_lastSampledResentReliableCommands >= 0 && curResends >= _lastSampledResentReliableCommands)
                {
                    float instantResendRate = (curResends - _lastSampledResentReliableCommands) / elapsed;
                    _coopRecentResendsPerSec = instantResendRate > _coopRecentResendsPerSec
                        ? instantResendRate
                        : (_coopRecentResendsPerSec * 0.55f);
                }
                else
                {
                    _coopRecentResendsPerSec *= 0.55f;
                }
                _lastSampledResentReliableCommands = curResends;

                float rttInflation = _coopBaselineRttMs > 0f ? Mathf.Max(0f, effectiveRtt - _coopBaselineRttMs) : 0f;
                float inflationScale = rttInflation <= 80f
                    ? 1.0f
                    : (1.0f + Mathf.Clamp01((rttInflation - 80f) / 200f) * 1.5f);

                float hostRelayScale;
                if (localRelayPing <= 140)
                    hostRelayScale = 1.0f;
                else if (localRelayPing <= 320)
                    hostRelayScale = 1.0f + ((localRelayPing - 140f) / 180f) * 1.0f;
                else
                    hostRelayScale = 2.0f + Mathf.Clamp01((localRelayPing - 320f) / 220f) * 1.0f;

                float resendScale = _coopRecentResendsPerSec <= 1.0f
                    ? 1.0f
                    : (1.0f + Mathf.Clamp01((_coopRecentResendsPerSec - 1.0f) / 4.0f) * 2.0f);

                CachedCoopRpcCongestionScale = Mathf.Clamp(
                    Mathf.Max(inflationScale, Mathf.Max(hostRelayScale, resendScale)),
                    1.0f,
                    3.5f);
            }
            catch
            {
                CachedCoopRpcCongestionScale = Mathf.Clamp(CachedCoopRpcCongestionScale, 1.0f, 3.5f);
            }
        }

        /// <summary>
        /// Two-lane Co-op RPC pacer scaled by Photon Cloud room fan-out, median RTT inflation,
        /// host relay latency, and reliable command resends. Guarantees a Normal-priority combat/order
        /// floor (&gt;= 2/s) while isolating Low-priority ambient doors/chat/voice to shed only on real congestion.
        /// </summary>
        public static bool TryConsumeBotRpcBudget(float minInterval = 0.35f, BotRpcPriority priority = BotRpcPriority.Normal)
        {
            int players = GetConnectedPlayerCount();
            if (players <= 1)
            {
                BotRpcBudgetGrantedCount++;
                return true;
            }

            if (ShouldSkipBotCycleForNetworkPing())
            {
                BotRpcBudgetThrottledCount++;
                return false;
            }

            RefreshCoopNetworkHealthIfDue();

            int remotePlayers = Mathf.Max(1, players - 1);
            float fanoutScale = 1.0f + Mathf.Min(remotePlayers - 1, 8) * 0.15f;
            float congestion = Mathf.Clamp(CachedCoopRpcCongestionScale, 1.0f, 3.5f);
            float now = Time.unscaledTime;

            if (priority == BotRpcPriority.Low)
            {
                if (congestion >= 2.25f)
                {
                    BotRpcBudgetThrottledCount++;
                    return false;
                }

                if (_nextAllowedLowPriorityBotRpcTime > now + 5.0f)
                    _nextAllowedLowPriorityBotRpcTime = 0f;

                if (now < _nextAllowedLowPriorityBotRpcTime)
                {
                    BotRpcBudgetThrottledCount++;
                    return false;
                }

                _nextAllowedLowPriorityBotRpcTime = now + Mathf.Clamp(minInterval * 1.15f * fanoutScale * congestion, 0.20f, 4.0f);
                BotRpcBudgetGrantedCount++;
                return true;
            }

            float maxBurst = congestion <= 1.50f ? 3.0f : 1.5f;
            float tokensPerSec = Mathf.Max(2.0f, 1.0f / (0.125f * fanoutScale * congestion));

            if (_botRpcNormalLastRefillTime <= 0f || now < _botRpcNormalLastRefillTime || (now - _botRpcNormalLastRefillTime) > 10.0f)
            {
                _botRpcNormalTokens = maxBurst;
                _botRpcNormalLastRefillTime = now;
            }
            else
            {
                float dt = now - _botRpcNormalLastRefillTime;
                if (dt > 0f)
                {
                    _botRpcNormalTokens = Mathf.Min(maxBurst, _botRpcNormalTokens + dt * tokensPerSec);
                    _botRpcNormalLastRefillTime = now;
                }
            }

            float cost = Mathf.Clamp(minInterval / 0.35f, 0.60f, 1.60f);
            if (_botRpcNormalTokens < cost)
            {
                BotRpcBudgetThrottledCount++;
                return false;
            }

            _botRpcNormalTokens -= cost;
            BotRpcBudgetGrantedCount++;
            return true;
        }

        public static Vehicle GetCachedTurretVehicle(Turret turret)
        {
            if (turret == null || turret.Pointer == IntPtr.Zero)
                return null;

            IntPtr ptr = turret.Pointer;
            if (_turretVehicleCache.TryGetValue(ptr, out Vehicle cachedVeh))
            {
                if (cachedVeh != null && cachedVeh.Pointer != IntPtr.Zero)
                    return cachedVeh;
            }

            Vehicle resolved = turret.GetComponentInParent<Vehicle>();
            _turretVehicleCache[ptr] = resolved;
            return resolved;
        }

        public static TurretFastTraits GetCachedTurretTraits(Turret turret)
        {
            if (ReferenceEquals(turret, null) || turret == null || turret.Pointer == IntPtr.Zero)
                return default;

            IntPtr ptr = turret.Pointer;
            if (_turretTraitsCache.TryGetValue(ptr, out TurretFastTraits traits))
            {
                if (traits.RootVehicle == null || traits.RootVehicle.Pointer != IntPtr.Zero)
                    return traits;
            }

            Vehicle rootVeh = GetCachedTurretVehicle(turret);
            VehicleFastHandles vh = rootVeh != null ? GetOrCreateVehicleHandles(rootVeh) : null;
            Seats seat = null;
            try { seat = turret.GetSeat(); } catch { }

            bool can360 = false;
            float yawBondDeg = 180.0f;
            bool isSecondary = false;
            try
            {
                can360 = turret.CanShoot360;
                int rawBond = turret.yAxisBond;
                yawBondDeg = can360 ? 180.0f : Mathf.Clamp(rawBond > 0 ? (float)rawBond : 25.0f, 10.0f, 179.0f);
                if (rootVeh != null)
                {
                    Turret mainTurret = rootVeh.GetMainTurret(false);
                    isSecondary = mainTurret != null && mainTurret.Pointer != ptr;
                }
            }
            catch { }

            traits = new TurretFastTraits
            {
                RootVehicle = rootVeh,
                VehHandles = vh,
                IsCommanderTurret = turret.TryCast<TurretCommander>() != null,
                IsExposedCrewSeat = seat != null && seat.showUnitSet,
                IsAA = turret.isAA,
                IsIndirectOrArty = turret.IsUpIndirectFire() || turret.IsArtillery(),
                CanShoot360 = can360,
                IsSecondaryTurret = isSecondary,
                YawBondDeg = yawBondDeg,
                AimFov = turret.GetAimingFOV()
            };

            _turretTraitsCache[ptr] = traits;
            return traits;
        }

        /// <summary>
        /// Continuous human distance "squint" FOV curve: wide peripheral/ear awareness up close,
        /// tapering smoothly with distance so observers neither freeze blind at 21m nor keep 160-degree vision at 75m.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float EvaluateHumanSquintHalfFov(float dist, float closeOmniRange, float midMaxDist, float midHalfFov, float longHalfFov)
        {
            float omniRadius = Mathf.Clamp(closeOmniRange, 6.0f, 14.0f);
            if (dist <= omniRadius)
                return 180.0f;

            float nearBandEnd = Mathf.Max(omniRadius + 14.0f, 34.0f);
            if (dist <= nearBandEnd)
            {
                float tNear = (dist - omniRadius) / (nearBandEnd - omniRadius);
                return Mathf.Lerp(136.0f, Mathf.Max(midHalfFov, 90.0f), tNear);
            }

            float midEnd = Mathf.Max(nearBandEnd + 25.0f, midMaxDist);
            if (dist <= midEnd)
            {
                float tMid = (dist - nearBandEnd) / (midEnd - nearBandEnd);
                return Mathf.Lerp(Mathf.Max(midHalfFov, 90.0f), Mathf.Min(midHalfFov, 64.0f), tMid);
            }

            float farEnd = Mathf.Max(midEnd + 95.0f, 210.0f);
            float tFar = Mathf.Clamp01((dist - midEnd) / (farEnd - midEnd));
            return Mathf.Lerp(Mathf.Min(midHalfFov, 64.0f), Mathf.Clamp(longHalfFov, 26.0f, 52.0f), tFar);
        }

        /// <summary>
        /// Evaluates (throttled to 0.55s per vehicle) whether an enclosed vehicle/tank currently has an alive
        /// Commander cupola/periscope observer, an unbuttoned/open-top crewman, or friendly infantry within 18.5m
        /// capable of calling out flank targets over the intercom / tank-infantry phone.
        /// </summary>
        public static void GetCachedVehicleSpotterStatus(
            VehicleFastHandles vh,
            string myFaction,
            float now,
            out bool hasAliveCommanderOrExposedSpotter,
            out bool hasCloseFriendlyEscort)
        {
            hasAliveCommanderOrExposedSpotter = false;
            hasCloseFriendlyEscort = false;
            if (vh == null || vh.Ref == null || vh.Tr == null)
                return;

            if (now < vh.NextSpotterCheckTime)
            {
                hasAliveCommanderOrExposedSpotter = vh.CachedHasAliveCommanderOrExposedSpotter;
                hasCloseFriendlyEscort = vh.CachedHasCloseFriendlyEscort;
                return;
            }

            vh.NextSpotterCheckTime = now + 0.55f;
            Vehicle veh = vh.Ref;

            try
            {
                var seats = veh.seats;
                if (seats != null)
                {
                    int sLen = seats.Length;
                    if (!vh.CommanderSeatChecked)
                    {
                        vh.CommanderSeatChecked = true;
                        vh.CommanderSeatIndex = -1;
                        for (int i = 0; i < sLen; i++)
                        {
                            Seats s = seats[i];
                            if (s != null && s.connectedTurret != null && s.connectedTurret.TryCast<TurretCommander>() != null)
                            {
                                vh.CommanderSeatIndex = i;
                                vh.HasDedicatedCommanderTurret = true;
                                break;
                            }
                        }
                    }

                    if (vh.CommanderSeatIndex >= 0 && vh.CommanderSeatIndex < sLen)
                    {
                        Seats cmdSeat = seats[vh.CommanderSeatIndex];
                        if (cmdSeat != null && cmdSeat.unitSet != null && cmdSeat.unitSet.IsAlive)
                            hasAliveCommanderOrExposedSpotter = true;
                    }

                    if (!hasAliveCommanderOrExposedSpotter)
                    {
                        for (int i = 0; i < sLen; i++)
                        {
                            Seats s = seats[i];
                            if (s == null || s.unitSet == null || !s.unitSet.IsAlive)
                                continue;

                            // Exposed open-top / pintle gunner / unbuttoned passenger or dedicated periscope observer seat (non-driver, non-main-gunner)
                            if (s.showUnitSet || (!s.isDriver && !s.hasTurret && s.HasPeriscope()))
                            {
                                hasAliveCommanderOrExposedSpotter = true;
                                break;
                            }
                        }
                    }
                }
            }
            catch { }

            if (!string.IsNullOrEmpty(myFaction))
            {
                EnsureWorldSnapshot();
                Vector3 vPos = vh.Tr.position;
                var squads = WorldSquads;
                int sqCount = WorldSquadCount;
                for (int i = 0; i < sqCount; i++)
                {
                    ref SquadSnapshot sqSnap = ref squads[i];
                    if (sqSnap.UnitCount == 0 || sqSnap.RefOnPlane || sqSnap.LeaderOnVehicle || string.IsNullOrEmpty(sqSnap.Faction))
                        continue;
                    if (!AreSameSideFactions(myFaction, sqSnap.Faction))
                        continue;

                    if ((sqSnap.RefPos - vPos).sqrMagnitude <= 18.5f * 18.5f)
                    {
                        hasCloseFriendlyEscort = true;
                        break;
                    }
                }
            }

            vh.CachedHasAliveCommanderOrExposedSpotter = hasAliveCommanderOrExposedSpotter;
            vh.CachedHasCloseFriendlyEscort = hasCloseFriendlyEscort;
        }

        /// <summary>
        /// Extends a soldier's alertness window (<see cref="Soldier.SetAlertedFor"/>) without forcing
        /// <c>pointGunEndTime = Time.time + 3.0f</c> when the soldier does not currently have an
        /// engageable visible target (<c>HasEnemyAndCanFireAtHim</c>). This prevents stationary
        /// defenders behind cover/walls/windows from raising their rifle into the shooting posture
        /// every 250ms and oscillating against <c>CheckTowardWallTps(1.0f)</c>.
        /// </summary>
        public static void RefreshAlertnessWithoutRaisingGun(Soldier soldier, float duration)
        {
            SoldierFastHandles h = GetOrCreateSoldierHandles(soldier);
            if (h == null || h.Ref == null)
                return;

            float now = Time.time;
            float prevPointGunEnd = h.Ref.pointGunEndTime;
            h.Ref.SetAlertedFor(duration);

            SoldierAI ai = h.Ai;
            bool canFireAtVisibleEnemy = !ReferenceEquals(ai, null) && ai != null && ai.HasEnemyAndCanFireAtHim;
            if (!canFireAtVisibleEnemy)
            {
                h.Ref.pointGunEndTime = h.Ref.isTowardWallTps
                    ? Mathf.Min(prevPointGunEnd, now)
                    : prevPointGunEnd;
            }
        }

        /// <summary>
        /// Rotates a soldier's body horizontally toward <paramref name="flatDir"/> via
        /// <see cref="Soldier.RotateToward"/> while preventing native <c>RotateToward</c>
        /// (<c>0x18076060C</c>) from forcing <c>pointGunEndTime = Time.time + 1.5f</c> (which raises
        /// the rifle into the shooting posture) or twisting <c>lookRotation.leftRight</c> against
        /// <c>LookForward</c> when the soldier is not actively aiming at an enemy.
        /// </summary>
        public static void RotateBodyWithoutRaisingGun(Soldier soldier, Vector3 flatDir, float deltaTime)
        {
            SoldierFastHandles h = GetOrCreateSoldierHandles(soldier);
            if (h == null || h.Ref == null || deltaTime <= 0f)
                return;

            flatDir.y = 0f;
            if (flatDir.sqrMagnitude < 0.001f)
                return;

            Soldier s = h.Ref;
            float now = Time.time;
            float prevPointGunEnd = s.pointGunEndTime;
            SoldierAI ai = h.Ai;
            bool isActivelyAiming = now < prevPointGunEnd &&
                                    !s.isTowardWallTps &&
                                    !ReferenceEquals(ai, null) &&
                                    ai != null &&
                                    ai.HasEnemyAndCanFireAtHim;

            if (isActivelyAiming)
            {
                s.RotateToward(flatDir.normalized, deltaTime);
                return;
            }

            LookParameters lookRot = h.CachedLookRot;
            if (!h.LookRotChecked)
            {
                lookRot = s.lookRotation;
                h.CachedLookRot = lookRot;
                h.LookRotChecked = true;
            }

            float prevLeftRight = lookRot != null ? lookRot.leftRight : 0f;
            float prevUpDown = lookRot != null ? lookRot.upDown : 0f;

            s.RotateToward(flatDir.normalized, deltaTime);

            s.pointGunEndTime = s.isTowardWallTps
                ? Mathf.Min(prevPointGunEnd, now)
                : prevPointGunEnd;

            if (lookRot != null)
            {
                lookRot.leftRight = prevLeftRight;
                lookRot.upDown = prevUpDown;
            }
        }

        /// <summary>
        /// Moves an AI bot smoothly toward <paramref name="targetPos"/> while stopping at
        /// <paramref name="standoffDist"/> meters so bots never body-hug squadmates/corpses.
        /// Uses vanilla async <c>CPathFinding</c> (<see cref="SoldierAI.MoveDestination"/> +
        /// <see cref="SoldierAI.UpdatePath"/>) for longer, sloped, or obstructed routes, and
        /// reserves <see cref="SoldierAI.MoveDirectlyToward"/> strictly for unobstructed level
        /// micro-steps (&lt;= 2.5m).
        /// </summary>
        public static VoiceManager.VoiceClip ResolveSafeVoiceClip(
            Soldier s,
            VoiceManager.VoiceClip clip,
            bool allowYesVariation = true)
        {
            if (ReferenceEquals(s, null) || s == null || s.Pointer == IntPtr.Zero)
                return clip;

            if (clip == VoiceManager.VoiceClip.keepYourHeadDown)
            {
                try
                {
                    VoiceManager vm = s._voice;
                    if (vm == null || vm.keepYourHeadDown == null || vm.keepYourHeadDown.Length == 0)
                        return VoiceManager.VoiceClip.imUnderFire;
                }
                catch
                {
                    return VoiceManager.VoiceClip.imUnderFire;
                }
            }
            else if (allowYesVariation && clip == VoiceManager.VoiceClip.yesSir && s.IsAI())
            {
                if ((GetDeterministicHash(s.Pointer, (int)(Time.time * 2.0f)) & 15) < 7)
                    return VoiceManager.VoiceClip.yes;
            }

            return clip;
        }

        public static void MoveBotTowardPosition(
            SoldierAI ai,
            Soldier soldier,
            Vector3 targetPos,
            float standoffDist = 0f,
            float minDirectDuration = 1.2f)
        {
            if (ai == null || soldier == null || soldier.transform == null)
                return;

            Vector3 myPos = soldier.transform.position;
            Vector3 flatDelta = targetPos - myPos;
            flatDelta.y = 0f;
            float flatDistSq = flatDelta.sqrMagnitude;
            float stopThreshold = Mathf.Max(0f, standoffDist) + 0.25f;
            if (flatDistSq <= stopThreshold * stopThreshold)
                return;

            float flatDist = Mathf.Sqrt(flatDistSq);
            Vector3 dir = flatDelta / flatDist;
            Vector3 dest = standoffDist > 0.05f ? (targetPos - dir * standoffDist) : targetPos;
            dest.y = targetPos.y;
            float moveDist = Mathf.Max(0.25f, flatDist - standoffDist);

            // Player-style locomotion: use MoveDirectlyToward ONLY for unobstructed, floor-snapped micro-steps (<= 2.5m).
            // Anything farther, sloped, slab-blocked, or obstructed uses vanilla async CPathFinding (MoveDestination + UpdatePath).
            if (moveDist <= 2.5f &&
                TryValidateAndSnapDirectMicroStep(myPos, dest, out Vector3 snappedDest, out _, 2.5f, 0.75f))
            {
                float duration = Mathf.Clamp(moveDist / 2.6f + 0.55f, Mathf.Min(minDirectDuration, 1.4f), 2.0f);
                ai.MoveDirectlyToward(snappedDest, duration);
                return;
            }

            if (ai.moveDirectlyToward_end > Time.time)
            {
                ai.moveDirectlyToward_end = 0f;
                ai.Stop(true);
            }

            if ((ai.MoveDestination - dest).sqrMagnitude > 1.44f ||
                (!ai.HasPath && !ai.HasPathRequest) ||
                ai.mustTeleport ||
                ai.PathCalculationLocked)
            {
                ResetBotPathLockout(ai);
                ai.MoveDestination = dest;
                ai.UpdatePath();
            }
        }

        /// <summary>
        /// Computes true horizontal (XZ-plane) unsigned angle in degrees between <paramref name="soldier"/>'s
        /// forward vector and <paramref name="direction"/>. Replaces vanilla <c>Soldier.AngleToDirection</c>
        /// (<c>0x18073D4E0</c>), which accidentally casts <see cref="Vector3"/> to <see cref="Vector2"/>
        /// and drops the North/South (<c>Z</c>) component completely!
        /// </summary>
        public static float HorizontalAngleToDirection(Soldier soldier, Vector3 direction)
        {
            SoldierFastHandles h = GetOrCreateSoldierHandles(soldier);
            if (h == null || h.Tr == null)
                return 180f;

            Vector3 fwd = h.Tr.forward;
            float fx = fwd.x, fz = fwd.z;
            float dx = direction.x, dz = direction.z;
            float lenSq = (fx * fx + fz * fz) * (dx * dx + dz * dz);
            if (lenSq < 1e-8f)
                return 0f;

            float cos = Mathf.Clamp((fx * dx + fz * dz) / Mathf.Sqrt(lenSq), -1f, 1f);
            return Mathf.Acos(cos) * Mathf.Rad2Deg;
        }

        public static float HorizontalSignedAngleToDirection(Soldier soldier, Vector3 direction)
        {
            SoldierFastHandles h = GetOrCreateSoldierHandles(soldier);
            if (h == null || h.Tr == null)
                return 0f;

            Vector3 fwd = h.Tr.forward;
            float fx = fwd.x, fz = fwd.z;
            float dx = direction.x, dz = direction.z;
            float lenSq = (fx * fx + fz * fz) * (dx * dx + dz * dz);
            if (lenSq < 1e-8f)
                return 0f;

            float cos = Mathf.Clamp((fx * dx + fz * dz) / Mathf.Sqrt(lenSq), -1f, 1f);
            float angle = Mathf.Acos(cos) * Mathf.Rad2Deg;
            // Match vanilla SignedAngleToDirection's intended 2D (x, z) cross sign: fx*dz - dx*fz
            return (fx * dz - dx * fz) < 0f ? -angle : angle;
        }

        /// <summary>
        /// Returns true if any remote multiplayer human client's controlled soldier is within
        /// <paramref name="maxRadius"/> of <paramref name="worldPos"/> (cached 2 Hz scan, O(1) per call).
        /// Used so <c>Soldier.SyncSay</c> Photon RPCs are only sent when a remote player can actually hear the bot.
        /// </summary>
        public static bool IsNearAnyRemotePlayer(Vector3 worldPos, float maxRadius)
        {
            if (!IsMultiplayerWithRemotePlayers())
                return false;

            float now = Time.unscaledTime;
            if (now >= _nextRemotePlayerPosScanTime)
            {
                _nextRemotePlayerPosScanTime = now + 0.50f;
                _cachedRemotePlayerPositions.Clear();
                try
                {
                    var list = Creature.allCreatures;
                    if (list != null)
                    {
                        int count = list.Count;
                        for (int i = 0; i < count; i++)
                        {
                            Creature c = list[i];
                            if (c == null || !c.IsAlive)
                                continue;

                            Soldier s = c.TryCast<Soldier>();
                            if (s == null || s.Pointer == IntPtr.Zero)
                                continue;

                            SoldierFastHandles rh = TryGetSoldierHandles(s.Pointer, out SoldierFastHandles cachedRh) ? cachedRh : null;
                            Transform sTr = (rh != null && rh.Tr != null) ? rh.Tr : s.transform;
                            if (sTr == null)
                                continue;

                            SyncSoldier syncher = rh != null ? GetCachedSoldierSyncher(rh) : s.soldierSyncher;
                            if (syncher != null && !syncher.OwnSyncher && (syncher.controlled_by_player || s.IsPlayer()))
                            {
                                _cachedRemotePlayerPositions.Add(sTr.position);
                            }
                        }
                    }
                }
                catch { }
            }

            float maxSq = maxRadius * maxRadius;
            for (int i = 0; i < _cachedRemotePlayerPositions.Count; i++)
            {
                if ((_cachedRemotePlayerPositions[i] - worldPos).sqrMagnitude <= maxSq)
                    return true;
            }
            return false;
        }

        [HarmonyPatch(typeof(Soldier), nameof(Soldier.AngleToDirection))]
        public static class Soldier_AngleToDirection_Patch
        {
            public static bool Prefix(Soldier __instance, Vector3 __0, ref float __result)
            {
                if (!VisionConfig.EnableMod.Value || __instance == null)
                    return true;

                __result = HorizontalAngleToDirection(__instance, __0);
                return false;
            }
        }

        [HarmonyPatch(typeof(Soldier), nameof(Soldier.SignedAngleToDirection))]
        public static class Soldier_SignedAngleToDirection_Patch
        {
            public static bool Prefix(Soldier __instance, Vector3 __0, ref float __result)
            {
                if (!VisionConfig.EnableMod.Value || __instance == null)
                    return true;

                __result = HorizontalSignedAngleToDirection(__instance, __0);
                return false;
            }
        }

        public static int GetDeterministicHash(IntPtr ptr, int seed)
        {
            unchecked
            {
                long val = ptr.ToInt64();
                int h = (int)(val ^ (val >> 32)) ^ (seed * 397);
                h ^= (h >> 16);
                h *= 0x7feb352d;
                h ^= (h >> 15);
                return h & 0x7FFFFFFF;
            }
        }

        public static void PruneDict<T>(Dictionary<IntPtr, T> dict, HashSet<IntPtr> alive)
        {
            if (dict == null || dict.Count == 0)
                return;

            _sharedPruneBuffer.Clear();
            foreach (var k in dict.Keys)
            {
                if (!alive.Contains(k))
                    _sharedPruneBuffer.Add(k);
            }
            for (int i = 0; i < _sharedPruneBuffer.Count; i++)
            {
                dict.Remove(_sharedPruneBuffer[i]);
            }
            _sharedPruneBuffer.Clear();
        }

        public static void PruneSet(HashSet<IntPtr> set, HashSet<IntPtr> alive)
        {
            if (set == null || set.Count == 0)
                return;

            _sharedPruneBuffer.Clear();
            foreach (var k in set)
            {
                if (!alive.Contains(k))
                    _sharedPruneBuffer.Add(k);
            }
            for (int i = 0; i < _sharedPruneBuffer.Count; i++)
            {
                set.Remove(_sharedPruneBuffer[i]);
            }
            _sharedPruneBuffer.Clear();
        }

        public static int TotalAdaptiveAiExtraTicks;

        // =====================================================================
        // Adaptive AI Brain Tick Scheduler (Creature.ProcessSequentialUpdates)
        // =====================================================================
        // Vanilla Creature.ProcessSequentialUpdates (0x180A53760) hardcodes `cmp ebp, 1`,
        // updating only 1 slot in Creature.aliveCreatures per frame. In a 120-140 bot match,
        // each soldier waits 2.0-2.5+ seconds between SoldierAI.SequentialUpdate brain ticks,
        // and any frame that lands on a Vehicle, dead soldier, or seated passenger wastes
        // the entire frame's AI budget.
        private static SoldierFastHandles[] _urgentUnderFireWakeQueue = new SoldierFastHandles[64];
        private static int _urgentUnderFireWakeCount;

        internal static bool TryEnsureArrayCapacity<T>(ref T[] array, int neededLength, int maxLength)
        {
            if (neededLength <= array.Length)
                return true;
            if (array.Length >= maxLength)
                return false;

            int newSize = Math.Max(1, array.Length) * 2;
            while (newSize < neededLength && newSize < maxLength)
                newSize *= 2;
            Array.Resize(ref array, Math.Min(maxLength, newSize));
            return neededLength <= array.Length;
        }

        public static void ClearUrgentUnderFireWakeQueue()
        {
            if (_urgentUnderFireWakeCount > 0)
            {
                Array.Clear(_urgentUnderFireWakeQueue, 0, _urgentUnderFireWakeCount);
                _urgentUnderFireWakeCount = 0;
            }
        }

        private static bool IsStaleUrgentWakeEntry(SoldierFastHandles uh, float now)
        {
            return uh == null ||
                   uh.Ref == null ||
                   uh.Ptr == IntPtr.Zero ||
                   !uh.Ref.IsAlive ||
                   uh.Ref.IsOnVehicle() ||
                   uh.Vision.UnderFireWakeAt <= 0f ||
                   now < uh.Vision.UnderFireWakeAt - 10.0f ||
                   now > uh.Vision.UnderFireWakeAt + 4.0f;
        }

        public static void EnqueueUrgentUnderFireWake(SoldierFastHandles h, float wakeAt)
        {
            if (h == null || h.Ref == null || h.Ptr == IntPtr.Zero)
                return;

            if (h.Vision.UnderFireWakeAt > 0f && h.Vision.UnderFireWakeAt <= wakeAt)
                return;

            h.Vision.UnderFireWakeAt = wakeAt;
            for (int i = 0; i < _urgentUnderFireWakeCount; i++)
            {
                if (ReferenceEquals(_urgentUnderFireWakeQueue[i], h))
                    return;
            }

            if (_urgentUnderFireWakeCount >= _urgentUnderFireWakeQueue.Length)
            {
                float now = Time.time;
                int writeIdx = 0;
                for (int i = 0; i < _urgentUnderFireWakeCount; i++)
                {
                    SoldierFastHandles uh = _urgentUnderFireWakeQueue[i];
                    if (IsStaleUrgentWakeEntry(uh, now))
                    {
                        if (uh != null)
                            uh.Vision.UnderFireWakeAt = 0f;
                        continue;
                    }

                    _urgentUnderFireWakeQueue[writeIdx++] = uh;
                }
                if (_urgentUnderFireWakeCount > writeIdx)
                    Array.Clear(_urgentUnderFireWakeQueue, writeIdx, _urgentUnderFireWakeCount - writeIdx);
                _urgentUnderFireWakeCount = writeIdx;

                if (!TryEnsureArrayCapacity(ref _urgentUnderFireWakeQueue, _urgentUnderFireWakeCount + 1, 256))
                    return;
            }

            _urgentUnderFireWakeQueue[_urgentUnderFireWakeCount++] = h;
        }

        private static Il2CppSystem.Collections.Generic.List<Creature> _cachedAliveCreaturesList;
        private static float _nextAliveCreaturesListRefreshTime;

        internal static void ClearAliveCreaturesCache()
        {
            _cachedAliveCreaturesList = null;
            _nextAliveCreaturesListRefreshTime = 0f;
            _localPlayerCacheFrame = -1;
            _cachedLocalPlayerState = default;
        }

        [HarmonyPatch(typeof(Creature), nameof(Creature.ProcessSequentialUpdates))]
        public static class Creature_ProcessSequentialUpdates_Patch
        {
            private static bool _isRunningExtraTicks;

            public static void Prefix(out long __state)
            {
                __state = _isRunningExtraTicks ? 0L : HitchProfiler.BeginTopLevel();
            }

            public static void Postfix(long __state)
            {
                if (__state > 0L)
                    HitchProfiler.End(HitchBucket.VanSeqTick, __state);

                if (_isRunningExtraTicks)
                    return;

                if (!VisionConfig.EnableMod.Value || !VisionConfig.EnableAdaptiveAiTickScaling.Value || Time.timeScale <= 0.01f)
                    return;

                // Prioritize multiplayer ping & frame-time stability: never add extra ticks on busy frames
                // or on frames where vanilla SquadLeaderRoutine / world snapshot rebuild already ran!
                if (IsHeavyWorkFrame() || ShouldSkipBotCycleForNetworkPing())
                    return;

                float dt = Time.unscaledDeltaTime;
                if (dt > 0.025f)
                    return;

                long extraProfStart = HitchProfiler.BeginModScope();
                try
                {
                    float uNow = Time.unscaledTime;
                    if (_cachedAliveCreaturesList == null ||
                        uNow >= _nextAliveCreaturesListRefreshTime ||
                        uNow < _nextAliveCreaturesListRefreshTime - 2.0f)
                    {
                        _cachedAliveCreaturesList = Creature.aliveCreatures;
                        _nextAliveCreaturesListRefreshTime = uNow + 0.50f;
                    }

                    var list = _cachedAliveCreaturesList;
                    if (list == null)
                        return;

                    int count = list.Count;
                    if (count <= 1)
                        return;

                    int players = GetConnectedPlayerCount();
                    int targetBgFootTicks;
                    int maxScanSteps;

                    if (players >= 4)
                    {
                        // Large multiplayer lobby (4-10+ players): strictly 1 active foot tick per frame
                        // so Photon outgoing serialization queues never contend with player ping.
                        targetBgFootTicks = 1;
                        maxScanSteps = Mathf.Min(count - 1, 2);
                    }
                    else if (players > 1)
                    {
                        // Small Co-op session (2-3 players): allow 2 total ticks when 75+ units on smooth frames
                        targetBgFootTicks = (count > 75 && dt < 0.0185f) ? 2 : 1;
                        maxScanSteps = Mathf.Min(count - 1, 3);
                    }
                    else
                    {
                        // Singleplayer: only add a 2nd background foot tick above 75 creatures on smooth <17.5ms frames
                        // (cuts redundant SequentialUpdate IL2CPP churn across 55-75 bot battles while urgent wakes stay instant).
                        targetBgFootTicks = (count > 75 && dt < 0.0175f) ? 2 : 1;
                        maxScanSteps = Mathf.Min(count - 1, 4);
                    }

                    bool hasUrgentWake = _urgentUnderFireWakeCount > 0 && players < 4 && dt < 0.020f;
                    if (!hasUrgentWake && targetBgFootTicks <= 1)
                        return;

                    int lastIdx = Creature.sequentialUpdateIndex - 1;
                    if (lastIdx < 0 || lastIdx >= count)
                        lastIdx = count - 1;

                    int activeFootSoldiersTicked = 0;
                    bool squadLeaderTicked = false;
                    Creature lastCreature = list[lastIdx];
                    if (!ReferenceEquals(lastCreature, null) && lastCreature != null && lastCreature.IsAlive &&
                        TryGetSoldierHandlesFromCreature(lastCreature, out SoldierFastHandles lastHandles))
                    {
                        Soldier lastSoldier = lastHandles.Ref;
                        if (lastSoldier.isActiveAndEnabled && !lastSoldier.IsOnVehicle())
                        {
                            activeFootSoldiersTicked = 1;
                            if (lastSoldier.IsSquadLeader())
                                squadLeaderTicked = true;
                        }
                    }

                    int urgentCap = hasUrgentWake ? Mathf.Max(targetBgFootTicks, 3) : targetBgFootTicks;
                    if (activeFootSoldiersTicked >= urgentCap)
                        return;

                    _isRunningExtraTicks = true;
                    int scanned = 0;
                    long startStamp = Stopwatch.GetTimestamp();
                    long budgetTicks = (long)(Stopwatch.Frequency * 0.00045); // Strict 0.45ms ceiling for extra AI ticks

                    float nowWake = Time.time;
                    if (hasUrgentWake)
                    {
                        for (int wi = _urgentUnderFireWakeCount - 1; wi >= 0 && activeFootSoldiersTicked < urgentCap; wi--)
                        {
                            if ((Stopwatch.GetTimestamp() - startStamp) >= budgetTicks)
                                break;

                            SoldierFastHandles uh = _urgentUnderFireWakeQueue[wi];
                            if (IsStaleUrgentWakeEntry(uh, nowWake))
                            {
                                if (uh != null)
                                    uh.Vision.UnderFireWakeAt = 0f;
                                int last = --_urgentUnderFireWakeCount;
                                _urgentUnderFireWakeQueue[wi] = _urgentUnderFireWakeQueue[last];
                                _urgentUnderFireWakeQueue[last] = null;
                                continue;
                            }

                            if (nowWake < uh.Vision.UnderFireWakeAt || !uh.Ref.isActiveAndEnabled)
                                continue;

                            bool isUrgentLeader = uh.Ref.IsSquadLeader();
                            if (isUrgentLeader && squadLeaderTicked)
                                continue;

                            uh.Vision.UnderFireWakeAt = 0f;
                            int lastIdxWake = --_urgentUnderFireWakeCount;
                            _urgentUnderFireWakeQueue[wi] = _urgentUnderFireWakeQueue[lastIdxWake];
                            _urgentUnderFireWakeQueue[lastIdxWake] = null;

                            uh.Ref.SequentialUpdate();
                            SoldierAI uAi = uh.Ai;
                            if (!ReferenceEquals(uAi, null) && uAi != null && uAi.enabled)
                                uAi.SequentialUpdate();

                            activeFootSoldiersTicked++;
                            TotalAdaptiveAiExtraTicks++;
                            if (isUrgentLeader)
                                squadLeaderTicked = true;
                        }
                    }

                    while (activeFootSoldiersTicked < targetBgFootTicks && scanned < maxScanSteps)
                    {
                        if ((Stopwatch.GetTimestamp() - startStamp) >= budgetTicks)
                            break;

                        count = list.Count;
                        if (count <= 1)
                            break;

                        int idx = Creature.sequentialUpdateIndex;
                        if (idx < 0 || idx >= count)
                            idx = 0;

                        Creature c = list[idx];
                        if (ReferenceEquals(c, null) || c == null)
                        {
                            Creature.sequentialUpdateIndex = idx + 1;
                            scanned++;
                            continue;
                        }

                        if (!TryGetSoldierHandlesFromCreature(c, out SoldierFastHandles sHandles) || !sHandles.Ref.isActiveAndEnabled)
                        {
                            Creature.sequentialUpdateIndex = idx + 1;
                            scanned++;
                            continue;
                        }

                        Soldier s = sHandles.Ref;
                        // Never run two Squad Leaders (SquadOrdersRoutine -> 8x CoverManager octree searches) in the same frame!
                        bool isLeader = s.IsAlive && !s.IsOnVehicle() && s.IsSquadLeader();
                        if (isLeader && squadLeaderTicked)
                            break;

                        Creature.sequentialUpdateIndex = idx + 1;
                        scanned++;

                        s.SequentialUpdate();

                        SoldierAI ai = sHandles.Ai;
                        if (!ReferenceEquals(ai, null) && ai != null && ai.enabled)
                        {
                            ai.SequentialUpdate();
                        }

                        if (s.IsAlive && !s.IsOnVehicle())
                        {
                            activeFootSoldiersTicked++;
                            TotalAdaptiveAiExtraTicks++;
                            if (isLeader)
                                squadLeaderTicked = true;
                        }
                    }
                }
                catch
                {
                }
                finally
                {
                    _isRunningExtraTicks = false;
                    HitchProfiler.EndModScope(HitchBucket.ModExtraSeqTicks, extraProfStart);
                }
            }
        }
    }
}
