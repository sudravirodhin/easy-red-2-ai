using System;
using UnityEngine;

namespace EasyRed2HumanAI
{
    /// <summary>
    /// Pure, zero-IL2CPP 3-Tier Emergent Tactical Decision Core.
    /// Compiles identically inside <c>EasyRed2HumanAI.dll</c> and headless verification/simulation suites.
    /// Models two terminal drives (<c>Survive</c>, <c>Win</c>), one active lever (<c>Lethality</c> gated by <c>CanHarm &gt; 0</c>),
    /// shared radio-honest <c>DangerStamp</c> projected harm, two-speed survival reflexes, and shallow (2-3 ply) prerequisite chains.
    /// </summary>
    public static partial class EmergentEngine
    {
        public enum EmergentTier : byte
        {
            Individual = 1,
            SquadCrew = 2,
            TeamNet = 3
        }

        public enum DangerWeaponClass : byte
        {
            SmallArms = 0,
            ExplosionHE = 1,
            AntiTank = 2,
            Artillery = 3
        }

        public enum EmergentReflexKind : byte
        {
            None = 0,
            LiveGrenade = 1,
            HullCookoffDoom = 2,
            DoorwayFreshCasualty = 3,
            SuddenPenetratingFire = 4,
            PointBlankAmbush = 5,
            LethalBleedExposed = 6,
            CloseAcousticAlert = 7
        }

        public enum EmergentActionId : byte
        {
            None = 0,
            HoldAndFight = 1,
            SeekCover = 2,
            RepositionWallSide = 3,
            EvadeGrenade = 4,
            PopSmoke = 5,
            SelfBandage = 6,
            SuppressPin = 7,
            FlankManeuver = 8,
            FragFlush = 9,
            AdvanceObjective = 10,
            CloseBreach = 11,
            FallbackRegroup = 12,
            ResupplyAmmo = 13,
            PushAmmoDrop = 14,
            MedicHeal = 15,
            DragCasualty = 16,
            LiberatePrisoner = 17,
            MaintainRadioLink = 18,
            VehicleFaceAndFight = 19,
            VehicleCrewSwap = 20,
            VehicleFieldRepair = 21,
            VehicleBailout = 22
        }

        public enum Tier2SquadDirective : byte
        {
            AdvanceBound = 0,
            AnvilPin = 1,
            HammerFlank = 2,
            SquadSmokePush = 3,
            CovertApproach = 4,
            DecisiveBreach = 5,
            LocalFallback = 6,
            BalkNeedSupport = 7
        }

        public enum Tier2BalkReason : byte
        {
            None = 0,
            PinnedByMG = 1,
            BlockedByArmor = 2,
            HeavyCasualties = 3,
            OutOfAmmo = 4
        }

        public enum Tier3NetLever : byte
        {
            None = 0,
            SmokeBarrage = 1,
            ArtilleryHE = 2,
            TankDispatch = 3,
            ArmorInfantryPair = 4,
            CasVector = 5,
            AtCounterRoute = 6,
            MassAssault = 7,
            HoldAndFormUp = 8,
            ShiftAxisFlank = 9,
            AnvilAssign = 10,
            FireBrigade = 11,
            AuthorizeWithdraw = 12,
            SectorDesperationBias = 13
        }

        public struct EmergentWeights
        {
            public float WSurvive;
            public float WWin;
            public float RiskThreshold;
            public float WAid;
            public float Individualism;
            public float HarmAccuracy;

            public float BiasSuppress;
            public float BiasSmoke;
            public float BiasFlank;
            public float BiasFrag;
            public float BiasBayonet;
            public float BiasStandoff;
            public float BiasAid;
            public float BiasScavenge;
        }

        public struct DangerStamp
        {
            public Vector3 Pos;
            public Vector3 SourcePos;
            public float Time;
            public float DecaySec;
            public float Radius;
            public float Severity;
            public DangerWeaponClass WeaponClass;
            public bool IsFriendlyCasualty;
            public bool IsEnemyRelief;
            public long OriginSquadId;
            public bool ReportedToNet;
            public bool IsMiaTimeout;
            public bool Active;
        }

        /// <summary>
        /// Fixed-size thread-safe per-faction Danger Stamp ring buffer shared across Tier 1, Tier 2, and Tier 3
        /// via sensory/radio honesty filters.
        /// </summary>
        public sealed class DangerStampRing
        {
            public const int Capacity = 48;
            private readonly DangerStamp[] _slots = new DangerStamp[Capacity];
            private int _cursor;

            public void Clear()
            {
                Array.Clear(_slots, 0, Capacity);
                _cursor = 0;
            }

            public DangerStamp GetSlotCopy(int index) =>
                (index >= 0 && index < Capacity) ? _slots[index] : default;

            public void StampFriendlyHarm(
                Vector3 pos,
                Vector3 sourcePos,
                long squadId,
                bool reportedToNet,
                DangerWeaponClass weaponClass,
                float severity,
                float radius,
                float decaySec,
                float now)
            {
                AddOrRefreshStamp(new DangerStamp
                {
                    Pos = pos,
                    SourcePos = sourcePos,
                    Time = now,
                    DecaySec = Mathf.Max(2.0f, decaySec),
                    Radius = Mathf.Max(4.0f, radius),
                    Severity = Mathf.Clamp(severity, 0.05f, 1.0f),
                    WeaponClass = weaponClass,
                    IsFriendlyCasualty = true,
                    IsEnemyRelief = false,
                    OriginSquadId = squadId,
                    ReportedToNet = reportedToNet,
                    IsMiaTimeout = false,
                    Active = true
                }, now);
            }

            public void StampIncomingFireLane(
                Vector3 impactPos,
                Vector3 shooterPos,
                long squadId,
                bool reportedToNet,
                DangerWeaponClass weaponClass,
                float severity,
                float now)
            {
                AddOrRefreshStamp(new DangerStamp
                {
                    Pos = impactPos,
                    SourcePos = shooterPos,
                    Time = now,
                    DecaySec = 8.0f,
                    Radius = 11.0f,
                    Severity = Mathf.Clamp(severity, 0.08f, 0.90f),
                    WeaponClass = weaponClass,
                    IsFriendlyCasualty = false,
                    IsEnemyRelief = false,
                    OriginSquadId = squadId,
                    ReportedToNet = reportedToNet,
                    IsMiaTimeout = false,
                    Active = true
                }, now);
            }

            public void StampExplosionHazard(
                Vector3 pos,
                long squadId,
                bool reportedToNet,
                bool isHeavyOrAt,
                float radius,
                float severity,
                float now)
            {
                AddOrRefreshStamp(new DangerStamp
                {
                    Pos = pos,
                    SourcePos = pos,
                    Time = now,
                    DecaySec = isHeavyOrAt ? 16.0f : 12.0f,
                    Radius = Mathf.Max(7.0f, radius),
                    Severity = Mathf.Clamp(severity, 0.15f, 1.0f),
                    WeaponClass = isHeavyOrAt ? DangerWeaponClass.Artillery : DangerWeaponClass.ExplosionHE,
                    IsEnemyRelief = false,
                    OriginSquadId = squadId,
                    ReportedToNet = reportedToNet,
                    IsMiaTimeout = false,
                    Active = true
                }, now);
            }

            public void StampEnemyHarmRelief(
                Vector3 enemyPos,
                long reporterSquadId,
                bool reportedToNet,
                float severity,
                float now)
            {
                AddOrRefreshStamp(new DangerStamp
                {
                    Pos = enemyPos,
                    SourcePos = enemyPos,
                    Time = now,
                    DecaySec = 14.0f,
                    Radius = 22.0f,
                    Severity = Mathf.Clamp(severity, 0.15f, 1.0f),
                    WeaponClass = DangerWeaponClass.SmallArms,
                    IsEnemyRelief = true,
                    OriginSquadId = reporterSquadId,
                    ReportedToNet = reportedToNet,
                    IsMiaTimeout = false,
                    Active = true
                }, now);
            }

            public void StampMiaCommsTimeout(Vector3 lastKnownPos, long silentSquadId, float severity, float now)
            {
                AddOrRefreshStamp(new DangerStamp
                {
                    Pos = lastKnownPos,
                    SourcePos = lastKnownPos,
                    Time = now,
                    DecaySec = 45.0f,
                    Radius = 26.0f,
                    Severity = Mathf.Clamp(severity, 0.30f, 0.95f),
                    WeaponClass = DangerWeaponClass.AntiTank,
                    IsEnemyRelief = false,
                    OriginSquadId = silentSquadId,
                    ReportedToNet = true,
                    IsMiaTimeout = true,
                    Active = true
                }, now);
            }

            private void AddOrRefreshStamp(in DangerStamp incoming, float now)
            {
                for (int i = 0; i < Capacity; i++)
                {
                    ref DangerStamp s = ref _slots[i];
                    if (!s.Active)
                        continue;

                    if ((now - s.Time) > s.DecaySec)
                    {
                        s.Active = false;
                        continue;
                    }

                    if (s.IsEnemyRelief == incoming.IsEnemyRelief &&
                        s.IsFriendlyCasualty == incoming.IsFriendlyCasualty &&
                        s.WeaponClass == incoming.WeaponClass &&
                        s.IsMiaTimeout == incoming.IsMiaTimeout &&
                        (now - s.Time) <= 2.5f &&
                        (s.Pos - incoming.Pos).sqrMagnitude <= 25.0f)
                    {
                        s.Pos = incoming.Pos;
                        s.Time = now;
                        s.Severity = Mathf.Clamp01(Mathf.Max(s.Severity, incoming.Severity) + 0.15f);
                        s.Radius = Mathf.Max(s.Radius, incoming.Radius);
                        s.ReportedToNet = s.ReportedToNet || incoming.ReportedToNet;
                        return;
                    }
                }

                _slots[_cursor] = incoming;
                _cursor = (_cursor + 1) % Capacity;
            }

            public float EvaluateSpotProjectedHarm(
                Vector3 queryPos,
                Vector3 observerPos,
                long observerSquadId,
                bool hasRadio,
                bool isHeavyArmor,
                bool isLightArmor,
                EmergentTier viewTier,
                float now,
                out int activeStamps,
                out float enemyRelief,
                out bool freshDoorwayCasualty)
            {
                activeStamps = 0;
                enemyRelief = 0f;
                freshDoorwayCasualty = false;

                float harmSum = 0f;
                float reliefSum = 0f;

                for (int i = 0; i < Capacity; i++)
                {
                    ref DangerStamp s = ref _slots[i];
                    if (!s.Active)
                        continue;

                    float age = now - s.Time;
                    if (age < 0f || age > s.DecaySec)
                    {
                        if (age > s.DecaySec)
                            s.Active = false;
                        continue;
                    }

                    bool sameSquad = observerSquadId != 0 && s.OriginSquadId == observerSquadId;
                    float obsDistSq = (s.Pos - observerPos).sqrMagnitude;

                    // Strictly enforce Sensory & Radio Fog-of-War Honesty
                    bool visibleToTier = false;
                    if (viewTier == EmergentTier.TeamNet)
                    {
                        visibleToTier = s.ReportedToNet || s.IsMiaTimeout;
                    }
                    else if (viewTier == EmergentTier.SquadCrew)
                    {
                        float hearR = (s.WeaponClass == DangerWeaponClass.ExplosionHE || s.WeaponClass == DangerWeaponClass.Artillery) ? 65f : 36f;
                        visibleToTier = sameSquad || (hasRadio && (s.ReportedToNet || s.IsMiaTimeout)) || obsDistSq <= hearR * hearR;
                    }
                    else
                    {
                        float hearR = (s.WeaponClass == DangerWeaponClass.ExplosionHE || s.WeaponClass == DangerWeaponClass.Artillery) ? 65f : 34f;
                        visibleToTier = (sameSquad && obsDistSq <= 45f * 45f) ||
                                        obsDistSq <= hearR * hearR ||
                                        (hasRadio && (s.ReportedToNet || s.IsMiaTimeout) && obsDistSq <= 115f * 115f);
                    }

                    if (!visibleToTier)
                        continue;

                    float queryDistSq = (s.Pos - queryPos).sqrMagnitude;
                    float radSq = s.Radius * s.Radius;
                    if (queryDistSq > radSq)
                        continue;

                    // Platform-relative CanPenetrate check (e.g. Tiger front hull ignores rifle pings!)
                    float penMult = 1.0f;
                    if (isHeavyArmor)
                    {
                        if (s.WeaponClass == DangerWeaponClass.SmallArms) penMult = 0.0f;
                        else if (s.WeaponClass == DangerWeaponClass.ExplosionHE) penMult = 0.15f;
                        else penMult = 1.0f;
                    }
                    else if (isLightArmor)
                    {
                        if (s.WeaponClass == DangerWeaponClass.SmallArms) penMult = 0.35f;
                        else if (s.WeaponClass == DangerWeaponClass.ExplosionHE) penMult = 0.65f;
                        else penMult = 1.0f;
                    }

                    if (penMult <= 0.001f && !s.IsEnemyRelief)
                        continue;

                    float timeDecay = Mathf.Exp(-1.35f * (age / Mathf.Max(0.5f, s.DecaySec)));
                    float distFrac = Mathf.Sqrt(queryDistSq) / Mathf.Max(0.5f, s.Radius);
                    float spatialFalloff = Mathf.Clamp01(1.0f - 0.65f * distFrac);
                    float classWeight = (!s.IsFriendlyCasualty && !s.IsMiaTimeout && !s.IsEnemyRelief && s.WeaponClass == DangerWeaponClass.SmallArms) ? 0.55f : 1.0f;
                    float contribution = s.Severity * penMult * classWeight * timeDecay * spatialFalloff;

                    activeStamps++;
                    if (s.IsEnemyRelief)
                    {
                        reliefSum += contribution;
                    }
                    else
                    {
                        harmSum += contribution;
                        if (s.IsFriendlyCasualty &&
                            age <= 4.5f &&
                            s.Severity >= 0.65f &&
                            !isHeavyArmor &&
                            obsDistSq <= 8.5f * 8.5f)
                        {
                            freshDoorwayCasualty = true;
                        }
                    }
                }

                enemyRelief = Mathf.Clamp01(reliefSum);
                return Mathf.Clamp01(Mathf.Max(0f, harmSum - enemyRelief * 0.65f));
            }
        }

        public enum IntentKind : byte
        {
            None = 0,
            ExclusivePrisoner = 1,
            ExclusiveMedicPatient = 2,
            ExclusiveCasualtyDrag = 3,
            ExclusiveCorpseLoot = 4,
            ExclusiveSmokeLane = 5,
            CanAidOverwatchMedic = 6,
            CanAidScreenTank = 7,
            CanAidScreenRepair = 8,
            CanAidSuppressAnchor = 9
        }

        public struct IntentSlot
        {
            public long TargetKey;
            public long OwnerEntityId;
            public long OriginSquadId;
            public IntentKind Kind;
            public Vector3 Pos;
            public float ExpireTime;
            public byte HelperCount;
            public byte MaxHelpers;
            public long Helper0;
            public long Helper1;
            public bool Active;
        }

        /// <summary>
        /// Zero-alloc fixed blackboard managing Two-Mode Intent Sharing (<c>AlreadyHandled</c> mutex vs <c>CanAid</c> capped helpers).
        /// </summary>
        public sealed class IntentBoard
        {
            public const int Capacity = 32;
            private readonly IntentSlot[] _slots = new IntentSlot[Capacity];

            public void Clear() => Array.Clear(_slots, 0, Capacity);

            public int ActiveSlotCount
            {
                get
                {
                    int c = 0;
                    for (int i = 0; i < Capacity; i++)
                        if (_slots[i].Active) c++;
                    return c;
                }
            }

            public bool IsClaimableExclusive(IntentKind kind, long targetKey, long ownerId, float now)
            {
                if (targetKey == 0)
                    return true;

                for (int i = 0; i < Capacity; i++)
                {
                    ref IntentSlot s = ref _slots[i];
                    if (!s.Active || now >= s.ExpireTime)
                        continue;
                    if (s.Kind == kind && s.TargetKey == targetKey && s.OwnerEntityId != ownerId)
                        return false;
                }
                return true;
            }

            public bool HasOpenCanAid(IntentKind aidKind, Vector3 helperPos, float maxDistMeters, long helperId, float now)
                => HasOpenCanAid(aidKind, helperPos, maxDistMeters, helperId, 0L, now, out _);

            public bool HasOpenCanAid(IntentKind aidKind, Vector3 helperPos, float maxDistMeters, long helperId, long helperSquadId, float now, out bool sameSquad)
            {
                sameSquad = false;
                float bestSq = maxDistMeters * maxDistMeters;
                bool anyOpen = false;
                for (int i = 0; i < Capacity; i++)
                {
                    ref IntentSlot s = ref _slots[i];
                    if (!s.Active || now >= s.ExpireTime)
                        continue;
                    if (s.Kind != aidKind || s.OwnerEntityId == helperId)
                        continue;

                    bool alreadyHelper = s.Helper0 == helperId || s.Helper1 == helperId;
                    if (!alreadyHelper && s.HelperCount >= s.MaxHelpers)
                        continue;

                    if ((s.Pos - helperPos).sqrMagnitude < bestSq)
                    {
                        anyOpen = true;
                        if (helperSquadId != 0L && s.OriginSquadId == helperSquadId)
                            sameSquad = true;
                    }
                }
                return anyOpen;
            }

            public bool TryClaimExclusive(IntentKind kind, long targetKey, long ownerId, Vector3 pos, float durationSec, float now)
            {
                if (targetKey == 0)
                    return true;

                int freeIdx = -1;
                for (int i = 0; i < Capacity; i++)
                {
                    ref IntentSlot s = ref _slots[i];
                    if (s.Active && now >= s.ExpireTime)
                        s.Active = false;

                    if (!s.Active)
                    {
                        if (freeIdx < 0) freeIdx = i;
                        continue;
                    }

                    if (s.Kind == kind && s.TargetKey == targetKey)
                    {
                        if (s.OwnerEntityId == ownerId)
                        {
                            s.Pos = pos;
                            s.ExpireTime = now + durationSec;
                            return true;
                        }
                        return false;
                    }
                }

                if (freeIdx >= 0)
                {
                    _slots[freeIdx] = new IntentSlot
                    {
                        TargetKey = targetKey,
                        OwnerEntityId = ownerId,
                        Kind = kind,
                        Pos = pos,
                        ExpireTime = now + durationSec,
                        MaxHelpers = 0,
                        Active = true
                    };
                }
                return true;
            }

            public void ReleaseExclusive(IntentKind kind, long targetKey, long ownerId)
            {
                for (int i = 0; i < Capacity; i++)
                {
                    ref IntentSlot s = ref _slots[i];
                    if (s.Active && s.Kind == kind && s.TargetKey == targetKey && s.OwnerEntityId == ownerId)
                        s.Active = false;
                }
            }

            public void PostCanAid(IntentKind aidKind, long targetKey, long ownerId, Vector3 pos, byte maxHelpers, float durationSec, float now, long originSquadId = 0L)
            {
                if (targetKey == 0)
                    return;

                byte clampedCap = (byte)Math.Min(2, Math.Max(1, (int)maxHelpers));
                int freeIdx = -1;
                for (int i = 0; i < Capacity; i++)
                {
                    ref IntentSlot s = ref _slots[i];
                    if (s.Active && now >= s.ExpireTime)
                        s.Active = false;

                    if (!s.Active)
                    {
                        if (freeIdx < 0) freeIdx = i;
                        continue;
                    }

                    if (s.Kind == aidKind && s.TargetKey == targetKey)
                    {
                        s.OwnerEntityId = ownerId;
                        if (originSquadId != 0L) s.OriginSquadId = originSquadId;
                        s.Pos = pos;
                        s.MaxHelpers = clampedCap;
                        s.ExpireTime = now + durationSec;
                        return;
                    }
                }

                if (freeIdx >= 0)
                {
                    _slots[freeIdx] = new IntentSlot
                    {
                        TargetKey = targetKey,
                        OwnerEntityId = ownerId,
                        OriginSquadId = originSquadId,
                        Kind = aidKind,
                        Pos = pos,
                        ExpireTime = now + durationSec,
                        HelperCount = 0,
                        MaxHelpers = clampedCap,
                        Active = true
                    };
                }
            }

            public bool TryJoinCanAid(IntentKind aidKind, Vector3 helperPos, float maxDistMeters, long helperId, float now, out Vector3 aidPos, out long targetKey)
                => TryJoinCanAid(aidKind, helperPos, maxDistMeters, helperId, 0L, now, out aidPos, out targetKey);

            public bool TryJoinCanAid(IntentKind aidKind, Vector3 helperPos, float maxDistMeters, long helperId, long helperSquadId, float now, out Vector3 aidPos, out long targetKey)
            {
                float maxSq = maxDistMeters * maxDistMeters;
                float bestRankSq = float.MaxValue;
                int bestIdx = -1;
                aidPos = Vector3.zero;
                targetKey = 0;

                for (int i = 0; i < Capacity; i++)
                {
                    ref IntentSlot s = ref _slots[i];
                    if (!s.Active)
                        continue;
                    if (now >= s.ExpireTime)
                    {
                        s.Active = false;
                        continue;
                    }
                    if (s.Kind != aidKind || s.OwnerEntityId == helperId)
                        continue;

                    bool alreadyHelper = s.Helper0 == helperId || s.Helper1 == helperId;
                    if (!alreadyHelper && s.HelperCount >= s.MaxHelpers)
                        continue;

                    float dSq = (s.Pos - helperPos).sqrMagnitude;
                    if (dSq >= maxSq)
                        continue;

                    float rankSq = (helperSquadId != 0L && s.OriginSquadId == helperSquadId) ? (dSq * 0.45f) : dSq;
                    if (rankSq < bestRankSq)
                    {
                        bestRankSq = rankSq;
                        bestIdx = i;
                    }
                }

                if (bestIdx < 0)
                    return false;

                ref IntentSlot chosen = ref _slots[bestIdx];
                if (chosen.Helper0 != helperId && chosen.Helper1 != helperId)
                {
                    if (chosen.Helper0 == 0) chosen.Helper0 = helperId;
                    else chosen.Helper1 = helperId;
                    chosen.HelperCount++;
                }
                aidPos = chosen.Pos;
                targetKey = chosen.TargetKey;
                return true;
            }
        }

        public struct EmergentInput
        {
            public float Now;
            public Vector3 MyPos;
            public long SquadId;
            public bool IsLeader;
            public bool IsVehicle;
            public bool IsHeavyArmor;
            public bool IsLightArmor;
            public bool HasRadioLink;
            public bool IsRadioman;
            public bool RadioLinkDrifting;
            public bool IsMedic;
            public bool IsAmmoCarrier;
            public bool IsMachineGunnerOrBipod;
            public bool IsAtCapable;

            public float RawHarmNow;
            public float SmoothedHarmNow;
            public float ProjectedHarm;
            public float EnemyRelief;
            public int ActiveDangerStamps;
            public bool DoorwayFreshCasualtyAhead;
            public bool LiveGrenadeClose;
            public bool CloseAcousticThreat;
            public bool HullBurningOrDoomed;

            public bool InCover;
            public bool ExposedSideOfWall;
            public bool IsOnBridge;
            public bool IsBleeding;
            public bool HasBandage;
            public bool HasSmokeGrenade;
            public bool HasFragGrenade;
            public bool AmmoEmpty;
            public bool HasVisibleEnemy;
            public bool PointBlankThreat;
            public float VisibleEnemyDist;
            public float CanHarmTarget; // [0..1]: 0 for rifle vs heavy tank!

            public bool CoverAvailable;
            public bool FlankCoverAvailable;
            public bool AllyPinningThreat;
            public bool SmokeActive;
            public bool HasLastKnownThreat;
            public float LastKnownThreatDist;
            public bool TargetBehindCover;
            public bool Overwhelmed;
            public bool UncounterableArmorClose;

            public bool HasObjectiveTask;
            public bool InsideObjectiveZone;
            public bool IsHomeGroundDefender;
            public float ObjectiveDist;
            public float ObjectiveUrgency; // [0..1]
            public int ClusteredEnemiesAtThreat;
            public bool ObjectiveTickingAgainstUs;

            // Interactables & Prerequisites
            public bool NearbyAmmoSource; // crate / box / ammo carrier / buddy / corpse
            public bool NeedsPrimaryWeaponUpgrade; // unarmed/pistol-only soldier (e.g. bailed crew) near a dropped rifle/SMG
            public bool NeedsAtWeaponScavenge; // dropped AT launcher / AT grenade available nearby
            public bool AllyNeedsAmmoDropNearby;
            public bool HasInjuredAlly;
            public float InjuredAllyHarm;
            public bool InjuredAllyInOpen;
            public bool HasDownedAllyToDrag;
            public bool HasSurrenderedPrisoner;
            public bool HasAidOverwatchNeed;
            public bool InjuredAllySameSquad;
            public bool DownedAllySameSquad;
            public bool PrisonerSameSquad;
            public bool AidOverwatchSameSquad;
            public float SquadCohesionBonus; // [0..1] soft attraction to staying near living squadmates

            // Vehicle-specific
            public bool NeedsSeatSwap;
            public bool NeedsTrackOrEngineRepair;
            public float PrereqBlockedUntil;
            public byte PrereqBlockedAction;

            public Tier2SquadDirective SquadDirective;
            public float SectorDesperationBias;
            public EmergentActionId CurrentStep0;
            public EmergentReflexKind LastReflexKind;
            public float LastReflexTime;
        }

        public struct EmergentDecision
        {
            public EmergentActionId Step0;
            public EmergentActionId Step1;
            public EmergentReflexKind Reflex;
            public float Score;
            public EmergentActionId RunnerUp;
            public float RunnerUpScore;
            public float HarmNow;
            public float HarmProj;
            public float TotalHarm;
            public float WinUrgency;
            public float CanHarm;
            public float ChainHarm;
            public float DecideTime;
            public float CommitUntil;
            public bool UnsafePrereqRejected;
            public EmergentActionId RejectedPrereqAction;
            public string Reason;
        }

        public struct SoldierEmergentState
        {
            public bool WeightsInitialized;
            public int CachedVetTier;
            public float CachedDesperation;
            public float CachedLeaderMorale;
            public EmergentWeights Weights;

            public bool HasDecision;
            public float SmoothedHarmNow;
            public float LastSampleTime;
            public float NextPlanTime;
            public float PrereqBlockedUntil;
            public EmergentActionId PrereqBlockedAction;
            public EmergentDecision CurrentDecision;

            // Thrash Watchdog & Trace Dedup
            public EmergentActionId LastActionForFlip;
            public float FlipWindowStart;
            public byte RecentFlipCount;
            public ulong LastTraceSig;
            public float LastTraceTime;
            public bool DeathDangerStamped;
            public EmergentReflexKind LastReflexKind;
            public float LastReflexTime;

            // Qualitative Scorecard & Autopsy Telemetry
            public float ActionStartedAt;
            public Vector3 RecentAnchorPos;
            public float RecentAnchorTime;
            public float LastMoved5s;
            public float LastHitTime;
            public float LastHitDist;
            public byte LastHitKind; // 0=unk, 1=inf, 2=veh, 3=blast, 4=bleed
            public uint LastInputFlags;
            public bool ProfileTraced;
            public int LastProfileTraceVet;
            public float LastProfileTraceDesp;
        }

        public struct VehicleEmergentState
        {
            public bool HasDecision;
            public float SmoothedHarmNow;
            public float LastSampleTime;
            public float RepairBlockedUntil;
            public EmergentDecision CurrentDecision;
            public ulong LastTraceSig;
        }

        public struct Tier2SquadInput
        {
            public float Now;
            public int AliveMembers;
            public float AvgSquadHarm;
            public float ProjectedLaneHarm;
            public float DecayedRecentDeaths;
            public float EnemyRelief;
            public float ObjectiveDist;
            public float ObjectiveUrgency;
            public bool HasActiveThreat;
            public float ThreatDist;
            public bool ThreatIsArmor;
            public bool SquadHasAT;
            public bool SquadHasSmoke;
            public bool SquadOutOfAmmo;
            public bool AllySquadPinning;
            public bool FlankCoverAvailable;
            public bool HasIntactRadio;
            public bool IsVehicleCrew;
            public bool LeaderIsSpearhead;
            public bool IsGyokusaiOrFinalStand;
            public float SectorDesperationBias;
        }

        public struct Tier2SquadDecision
        {
            public Tier2SquadDirective Directive;
            public Tier2BalkReason BalkReason;
            public float SurviveUtility;
            public float WinUtility;
            public bool RequestSmoke;
            public bool RequestArmorOrArty;
            public string Reason;
        }

        public struct Tier3NetInput
        {
            public float Now;
            public int ActiveSquads;
            public int BalkingSquads;
            public int MiaSilentTimeouts;
            public float LaneHarmHeat;
            public float EnemyRelief;
            public float TicketRatio01;
            public float CriticalObjectiveThreat01; // [0..1] how close a crucial flag/final cap is to collapsing
            public bool SmokeAvailable;
            public bool ArtilleryAvailable;
            public bool TanksAvailable;
            public bool CasAvailable;
            public bool AtAssetsAvailable;
            public bool InfantryBlockedByArmor;
            public bool TankNeedsInfantryScreen;
            public bool AlternativeFlankOpen;
            public bool FreshSpawnNeedsStaging;
            public bool HasCollapsingFriendlyCap;
            public bool HasShreddedEncircledSquad;
        }

        public struct Tier3NetDecision
        {
            public Tier3NetLever PrimaryLever;
            public Tier3NetLever SecondaryLever;
            public float SectorDesperationBias;
            public float NetHarm;
            public float NetWin;
            public string Reason;
        }

        public static float SmoothHarmEnvelope(float prev, float sample, float dt, float attackRate = 12.0f, float decayRate = 0.38f)
        {
            dt = Mathf.Clamp(dt, 0.01f, 2.0f);
            float rate = sample >= prev ? attackRate : decayRate;
            float alpha = 1.0f - Mathf.Exp(-rate * dt);
            return Mathf.Clamp01(prev + (sample - prev) * alpha);
        }

        public static bool IsSafeForDuration(float projectedAndNowHarm, float durationSec, float riskThreshold)
        {
            float cumulativeHarm = projectedAndNowHarm * (0.65f + 0.12f * Mathf.Max(0f, durationSec));
            return cumulativeHarm <= riskThreshold;
        }

        internal static EmergentWeights ComputeWeights(
            in VeteranAndTacticsPatches.FactionCommandProfile faction,
            VeteranAndTacticsPatches.SoldierPersonality pers,
            float vet01,
            uint entityHash,
            float desperationBias = 0f,
            bool isLeader = false,
            bool isRadioman = false,
            float leaderMoraleDelta = 0f)
        {
            // Seeded +/- 12% individual variation so squadmates don't behave identically
            float u1 = ((entityHash & 255u) / 255.0f) * 2.0f - 1.0f;
            float u2 = (((entityHash >> 8) & 255u) / 255.0f) * 2.0f - 1.0f;
            float u3 = (((entityHash >> 16) & 255u) / 255.0f);

            float persSurvive = 1.0f;
            float persWin = 1.0f;
            float persRisk = 1.0f;
            float persAid = 1.0f;
            float persInd = 0.42f;
            float biasSuppress = 1.0f;
            float biasSmoke = 1.0f;
            float biasFlank = 1.0f;
            float biasFrag = 1.0f;
            float biasBayonet = 1.0f;
            float biasStandoff = 1.0f;

            switch (pers)
            {
                case VeteranAndTacticsPatches.SoldierPersonality.Leader_Spearhead:
                case VeteranAndTacticsPatches.SoldierPersonality.Assaulter_Shock:
                case VeteranAndTacticsPatches.SoldierPersonality.TankCrew_Brawler:
                case VeteranAndTacticsPatches.SoldierPersonality.Pilot_Daredevil:
                    persSurvive = 0.86f; persWin = 1.24f; persRisk = 1.22f; persInd = 0.55f;
                    biasFlank = 1.18f; biasBayonet = 1.25f; biasStandoff = 0.80f;
                    break;

                case VeteranAndTacticsPatches.SoldierPersonality.Assaulter_Breacher:
                    persSurvive = 0.90f; persWin = 1.18f; persRisk = 1.16f;
                    biasFrag = 1.28f; biasSmoke = 1.15f; biasBayonet = 1.18f;
                    break;

                case VeteranAndTacticsPatches.SoldierPersonality.Assaulter_Skirmisher:
                case VeteranAndTacticsPatches.SoldierPersonality.LightCrew_Outrider:
                    persSurvive = 0.96f; persWin = 1.10f; persRisk = 1.06f; persInd = 0.65f;
                    biasFlank = 1.24f;
                    break;

                case VeteranAndTacticsPatches.SoldierPersonality.Rifleman_Cautious:
                case VeteranAndTacticsPatches.SoldierPersonality.Leader_OldGuard:
                case VeteranAndTacticsPatches.SoldierPersonality.TankCrew_Sniper:
                case VeteranAndTacticsPatches.SoldierPersonality.Pilot_Disciplined:
                case VeteranAndTacticsPatches.SoldierPersonality.LightCrew_FlakWatch:
                    persSurvive = 1.22f; persWin = 0.92f; persRisk = 0.82f; persInd = 0.30f;
                    biasStandoff = 1.26f; biasSuppress = 1.14f; biasBayonet = 0.72f;
                    break;

                case VeteranAndTacticsPatches.SoldierPersonality.Rifleman_Grenadier:
                    biasFrag = 1.32f; biasSmoke = 1.16f;
                    break;

                case VeteranAndTacticsPatches.SoldierPersonality.Gunner_Anchor:
                case VeteranAndTacticsPatches.SoldierPersonality.Gunner_Overwatch:
                    persSurvive = 1.10f; persWin = 0.96f; persRisk = 0.90f; persAid = 1.18f; persInd = 0.25f;
                    biasSuppress = 1.35f; biasStandoff = 1.20f; biasFlank = 0.78f;
                    break;

                case VeteranAndTacticsPatches.SoldierPersonality.Gunner_AssaultMG:
                    persWin = 1.12f; persRisk = 1.08f; biasSuppress = 1.22f;
                    break;

                case VeteranAndTacticsPatches.SoldierPersonality.Marksman_Deadeye:
                case VeteranAndTacticsPatches.SoldierPersonality.Marksman_Stalker:
                    persSurvive = 1.16f; persRisk = 0.84f; persInd = 0.78f;
                    biasStandoff = 1.38f; biasFlank = 1.14f; biasBayonet = 0.65f;
                    break;

                case VeteranAndTacticsPatches.SoldierPersonality.Marksman_Spotter:
                    persSurvive = 1.12f; persAid = 1.22f; biasStandoff = 1.24f;
                    break;

                case VeteranAndTacticsPatches.SoldierPersonality.AT_TankHunter:
                case VeteranAndTacticsPatches.SoldierPersonality.AT_Demolitionist:
                    persWin = 1.14f; persRisk = 1.10f; persInd = 0.58f;
                    biasFlank = 1.18f; biasFrag = 1.20f;
                    break;

                case VeteranAndTacticsPatches.SoldierPersonality.AT_Ambusher:
                    persSurvive = 1.14f; persInd = 0.70f; biasStandoff = 1.16f;
                    break;

                case VeteranAndTacticsPatches.SoldierPersonality.Medic_Heroic:
                    persRisk = 1.14f; persAid = 1.42f; biasSmoke = 1.26f; persInd = 0.28f;
                    break;

                case VeteranAndTacticsPatches.SoldierPersonality.Medic_Combat:
                    persAid = 1.24f; biasSmoke = 1.12f; persInd = 0.34f;
                    break;

                case VeteranAndTacticsPatches.SoldierPersonality.Medic_Triage:
                    persSurvive = 1.18f; persRisk = 0.85f; persAid = 1.26f; persInd = 0.24f;
                    break;

                case VeteranAndTacticsPatches.SoldierPersonality.Leader_Tactician:
                case VeteranAndTacticsPatches.SoldierPersonality.TankCrew_Support:
                case VeteranAndTacticsPatches.SoldierPersonality.LightCrew_Escort:
                case VeteranAndTacticsPatches.SoldierPersonality.Pilot_Ace:
                    persWin = 1.08f; persAid = 1.16f; biasSmoke = 1.10f; biasFlank = 1.12f;
                    break;
            }

            if (faction.PreferAmbushWhenAlone) persInd += 0.16f;
            if (faction.AllowDisconnectedLocalFlank) persInd += 0.08f;
            if (isLeader) persInd = Mathf.Max(0.18f, persInd - 0.14f);
            if (isRadioman) persInd = Mathf.Max(0.12f, persInd - 0.18f);
            float individualism = Mathf.Clamp01(persInd + (u3 - 0.5f) * 0.22f);

            // Convert legacy CostMults cleanly into utility multipliers (>1 boosts preference)
            float facSuppress = faction.SuppressCostMult > 0.1f ? (1.0f / faction.SuppressCostMult) : 1.0f;
            float facSmoke = faction.SmokeCostMult > 0.1f ? (1.0f / faction.SmokeCostMult) : 1.0f;
            float facFlank = faction.FlankMoveCostMult > 0.1f ? (1.0f / faction.FlankMoveCostMult) : 1.0f;
            float facFrag = faction.FragFlushCostMult > 0.1f ? (1.0f / faction.FragFlushCostMult) : 1.0f;
            float facBayonet = faction.BayonetBreachCostMult > 0.1f ? (1.0f / faction.BayonetBreachCostMult) : 1.0f;
            float facAid = faction.ComradeAidAndDragMult > 0.1f ? (1.0f / faction.ComradeAidAndDragMult) : 1.0f;
            float facScavenge = faction.ScavengeAndResupplyMult > 0.1f ? (1.0f / faction.ScavengeAndResupplyMult) : 1.0f;
            float facStandoff = faction.OverwatchHoldCostMult > 0.1f ? (1.0f / faction.OverwatchHoldCostMult) : 1.0f;

            // Attrition pivot threshold also scales how much institutional risk doctrine tolerates vs cautious maneuvers
            float docRisk = Mathf.Clamp((faction.SuppressionThresholdBase > 1f ? faction.SuppressionThresholdBase / 18.0f : 1.0f), 0.78f, 1.32f);
            float clampedDesp = Mathf.Clamp(desperationBias, 0f, 0.55f);
            float clampedMorale = Mathf.Clamp(leaderMoraleDelta, -0.20f, 0.20f);

            // Tier 1 Invariant: baseline Survive (1.35) > Win (0.95)
            float wSurvive = Mathf.Clamp(1.35f * persSurvive * (1.0f + u1 * 0.10f) - clampedDesp * 0.35f - clampedMorale * 0.90f, 0.70f, 2.25f);
            float wWin = Mathf.Clamp(0.95f * persWin * (1.0f + u2 * 0.12f) + clampedDesp * 0.65f + clampedMorale * 0.65f, 0.60f, 2.05f);
            float riskThreshold = Mathf.Clamp(0.46f * persRisk * docRisk * (0.95f + 0.12f * vet01) + clampedDesp * 0.40f + clampedMorale, 0.22f, 0.96f);
            float wAid = Mathf.Clamp(1.0f * persAid * facAid * (1.18f - 0.36f * individualism), 0.45f, 1.90f);
            if (isRadioman)
            {
                wSurvive = Mathf.Clamp(wSurvive * 1.18f, 0.70f, 2.25f);
                riskThreshold = Mathf.Clamp(riskThreshold * 0.85f, 0.22f, 0.90f);
            }

            return new EmergentWeights
            {
                WSurvive = wSurvive,
                WWin = wWin,
                RiskThreshold = riskThreshold,
                WAid = wAid,
                Individualism = individualism,
                HarmAccuracy = Mathf.Clamp01(0.35f + 0.65f * vet01),
                BiasSuppress = Mathf.Clamp(biasSuppress * facSuppress, 0.65f, 1.65f),
                BiasSmoke = Mathf.Clamp(biasSmoke * facSmoke, 0.65f, 1.65f),
                BiasFlank = Mathf.Clamp(biasFlank * facFlank * (0.90f + 0.25f * individualism), 0.65f, 1.75f),
                BiasFrag = Mathf.Clamp(biasFrag * facFrag, 0.65f, 1.65f),
                BiasBayonet = Mathf.Clamp(biasBayonet * facBayonet, 0.55f, 1.75f),
                BiasStandoff = Mathf.Clamp(biasStandoff * facStandoff, 0.65f, 1.70f),
                BiasAid = wAid,
                BiasScavenge = Mathf.Clamp(facScavenge * (0.92f + 0.20f * individualism), 0.65f, 1.65f)
            };
        }

        public static bool EvaluateFastReflex(
            in EmergentInput inp,
            in EmergentWeights w,
            float now,
            out EmergentDecision decision)
        {
            decision = default;
            float harmTotal = Mathf.Clamp01(inp.SmoothedHarmNow + inp.ProjectedHarm * (1.15f - 0.30f * w.HarmAccuracy));

            if (inp.LiveGrenadeClose &&
                !(inp.CurrentStep0 == EmergentActionId.EvadeGrenade && inp.LastReflexTime > 0f && (now - inp.LastReflexTime) < 0.75f))
            {
                decision = MakeReflexDecision(EmergentActionId.EvadeGrenade, EmergentReflexKind.LiveGrenade, harmTotal, 0.95f, inp, now, 1.0f, "reflex_live_grenade");
                return true;
            }

            if (inp.IsVehicle && inp.HullBurningOrDoomed && inp.CurrentStep0 != EmergentActionId.VehicleBailout)
            {
                decision = MakeReflexDecision(EmergentActionId.VehicleBailout, EmergentReflexKind.HullCookoffDoom, harmTotal, 0.98f, inp, now, 2.0f, "reflex_hull_doom");
                return true;
            }

            if (inp.IsVehicle)
                return false;

            if (inp.PointBlankThreat && inp.CanHarmTarget > 0.10f &&
                !(inp.CurrentStep0 == EmergentActionId.CloseBreach && inp.LastReflexTime > 0f && (now - inp.LastReflexTime) < 0.85f))
            {
                decision = MakeReflexDecision(EmergentActionId.CloseBreach, EmergentReflexKind.PointBlankAmbush, harmTotal, 0.85f, inp, now, 0.85f, "reflex_cqb_ambush");
                return true;
            }

            bool cqbLoaded = inp.HasVisibleEnemy && !inp.AmmoEmpty && inp.CanHarmTarget > 0.20f && inp.VisibleEnemyDist <= 9.0f;

            if (inp.DoorwayFreshCasualtyAhead && !inp.InCover && !cqbLoaded && harmTotal >= 0.30f &&
                inp.CurrentStep0 != EmergentActionId.SeekCover &&
                inp.CurrentStep0 != EmergentActionId.PopSmoke &&
                inp.CurrentStep0 != EmergentActionId.EvadeGrenade &&
                inp.CurrentStep0 != EmergentActionId.FallbackRegroup &&
                (inp.LastReflexTime <= 0f || (now - inp.LastReflexTime) >= 4.0f))
            {
                EmergentActionId act = (!inp.CoverAvailable && inp.HasSmokeGrenade) ? EmergentActionId.PopSmoke : EmergentActionId.SeekCover;
                decision = MakeReflexDecision(act, EmergentReflexKind.DoorwayFreshCasualty, harmTotal, 0.82f, inp, now, 1.50f, "reflex_doorway_cas");
                return true;
            }

            if (inp.SmoothedHarmNow >= 0.72f && !inp.InCover && !cqbLoaded &&
                inp.CurrentStep0 != EmergentActionId.SeekCover &&
                inp.CurrentStep0 != EmergentActionId.PopSmoke &&
                inp.CurrentStep0 != EmergentActionId.FallbackRegroup &&
                (inp.LastReflexTime <= 0f || (now - inp.LastReflexTime) >= 3.0f))
            {
                decision = MakeReflexDecision(EmergentActionId.SeekCover, EmergentReflexKind.SuddenPenetratingFire, harmTotal, 0.84f, inp, now, 1.40f, "reflex_penetrating_fire");
                return true;
            }

            if (inp.IsBleeding && inp.HasBandage && !inp.InCover && !cqbLoaded && inp.SmoothedHarmNow >= 0.45f &&
                inp.CurrentStep0 != EmergentActionId.SeekCover &&
                inp.CurrentStep0 != EmergentActionId.PopSmoke &&
                inp.CurrentStep0 != EmergentActionId.SelfBandage &&
                inp.CurrentStep0 != EmergentActionId.FallbackRegroup &&
                (inp.LastReflexTime <= 0f || (now - inp.LastReflexTime) >= 3.0f))
            {
                decision = MakeReflexDecision(EmergentActionId.SeekCover, EmergentReflexKind.LethalBleedExposed, harmTotal, 0.76f, inp, now, 1.50f, "reflex_bleed_seek_cover");
                return true;
            }

            if (inp.CloseAcousticThreat && !inp.HasVisibleEnemy && !inp.AmmoEmpty &&
                inp.CurrentStep0 != EmergentActionId.HoldAndFight &&
                inp.CurrentStep0 != EmergentActionId.SeekCover &&
                inp.CurrentStep0 != EmergentActionId.EvadeGrenade &&
                inp.CurrentStep0 != EmergentActionId.PopSmoke &&
                inp.CurrentStep0 != EmergentActionId.SelfBandage &&
                inp.CurrentStep0 != EmergentActionId.MedicHeal &&
                inp.CurrentStep0 != EmergentActionId.DragCasualty &&
                inp.CurrentStep0 != EmergentActionId.FallbackRegroup &&
                (inp.LastReflexTime <= 0f || (now - inp.LastReflexTime) >= 2.5f))
            {
                decision = MakeReflexDecision(EmergentActionId.HoldAndFight, EmergentReflexKind.CloseAcousticAlert, harmTotal, 0.74f, inp, now, 0.85f, "reflex_close_acoustic");
                return true;
            }

            return false;
        }

        private static EmergentDecision MakeReflexDecision(
            EmergentActionId act,
            EmergentReflexKind reflex,
            float harmTotal,
            float score,
            in EmergentInput inp,
            float now,
            float commitSec,
            string reason)
        {
            return new EmergentDecision
            {
                Step0 = act,
                Step1 = EmergentActionId.None,
                Reflex = reflex,
                Score = score,
                RunnerUp = EmergentActionId.None,
                RunnerUpScore = 0f,
                HarmNow = inp.SmoothedHarmNow,
                HarmProj = inp.ProjectedHarm,
                TotalHarm = harmTotal,
                WinUrgency = inp.ObjectiveUrgency,
                CanHarm = inp.CanHarmTarget,
                ChainHarm = harmTotal,
                DecideTime = now,
                CommitUntil = now + commitSec,
                Reason = reason
            };
        }

        public static EmergentDecision EvaluateDeliberative(
            in EmergentInput inp,
            in EmergentWeights w,
            in EmergentDecision prev,
            float now)
        {
            float effProjHarm = inp.ProjectedHarm * (1.18f - 0.32f * w.HarmAccuracy);
            float curHarm = Mathf.Clamp01(inp.SmoothedHarmNow + effProjHarm - inp.EnemyRelief * 0.20f);
            bool hasCommit = prev.Step0 != EmergentActionId.None && now < prev.CommitUntil;
            EmergentActionId prevStep0 = prev.Step0;
            bool hasSoftTail = !hasCommit && prevStep0 != EmergentActionId.None && now < prev.CommitUntil + 3.0f;
            float wSurv = w.WSurvive;
            float wWin = w.WWin;
            float wAid = w.WAid;

            float bestScore = -999f;
            EmergentActionId bestStep0 = EmergentActionId.None;
            EmergentActionId bestStep1 = EmergentActionId.None;
            float runnerScore = -999f;
            EmergentActionId runnerAct = EmergentActionId.None;
            float bestChainHarm = curHarm;
            bool unsafePrereqRejected = false;
            EmergentActionId rejectedPrereqAction = EmergentActionId.None;
            string bestReason = "idle";

            void ConsiderCandidate(
                EmergentActionId act0,
                EmergentActionId act1,
                float afterHarm,
                float winDelta,
                float aidDelta,
                float chainHarm,
                float extraBonus,
                string reason)
            {
                float deltaSurvive = curHarm - afterHarm;
                float swCost = (hasCommit && prevStep0 != act0)
                    ? 0.30f
                    : ((hasSoftTail && prevStep0 != act0) ? 0.08f : 0.0f);
                float score = wSurv * deltaSurvive + wWin * winDelta + wAid * aidDelta + extraBonus - swCost;

                if (score > bestScore)
                {
                    if (bestStep0 != EmergentActionId.None && bestStep0 != act0)
                    {
                        runnerScore = bestScore;
                        runnerAct = bestStep0;
                    }
                    bestScore = score;
                    bestStep0 = act0;
                    bestStep1 = act1;
                    bestChainHarm = chainHarm;
                    bestReason = reason;
                }
                else if (score > runnerScore && act0 != bestStep0)
                {
                    runnerScore = score;
                    runnerAct = act0;
                }
            }

            if (inp.IsVehicle)
            {
                // ---- TANK / VEHICLE CREW ----
                float bailRiskHarm = inp.HullBurningOrDoomed
                    ? 0.98f
                    : (inp.UncounterableArmorClose && inp.AmmoEmpty ? 0.85f : Mathf.Clamp01(curHarm * 0.65f));

                if (inp.NeedsSeatSwap)
                {
                    ConsiderCandidate(
                        EmergentActionId.VehicleCrewSwap,
                        EmergentActionId.VehicleFaceAndFight,
                        curHarm * 0.72f,
                        0.32f,
                        0f,
                        curHarm * 0.85f,
                        0.12f,
                        "prereq_swap_seat");
                }

                if (inp.NeedsTrackOrEngineRepair && !(now < inp.PrereqBlockedUntil && inp.PrereqBlockedAction == (byte)EmergentActionId.VehicleFieldRepair))
                {
                    float repairChainHarm = Mathf.Max(inp.SmoothedHarmNow, curHarm);
                    bool safeFor10s = IsSafeForDuration(repairChainHarm, 10.0f, w.RiskThreshold);
                    if (safeFor10s)
                    {
                        ConsiderCandidate(
                            EmergentActionId.VehicleFieldRepair,
                            EmergentActionId.VehicleFaceAndFight,
                            curHarm * 0.45f,
                            0.38f,
                            0.08f,
                            repairChainHarm,
                            0.14f,
                            "safe_field_repair");
                    }
                    else if (inp.HasSmokeGrenade && !inp.SmokeActive &&
                             IsSafeForDuration(repairChainHarm * 0.42f, 10.0f, w.RiskThreshold))
                    {
                        ConsiderCandidate(
                            EmergentActionId.PopSmoke,
                            EmergentActionId.VehicleFieldRepair,
                            curHarm * 0.40f,
                            0.32f,
                            0.08f,
                            repairChainHarm * 0.42f,
                            0.06f,
                            "prereq_smoke_repair");
                    }
                    else
                    {
                        unsafePrereqRejected = true;
                        rejectedPrereqAction = EmergentActionId.VehicleFieldRepair;
                    }
                }

                if (bailRiskHarm >= 0.82f)
                {
                    ConsiderCandidate(
                        EmergentActionId.VehicleBailout,
                        EmergentActionId.None,
                        0.22f,
                        0.0f,
                        0f,
                        0.22f,
                        0.20f,
                        "hull_doom_bail");
                }

                // Default: keep buttoned up, angle thick frontal armor toward threat, fight!
                float fightAfterHarm = Mathf.Max(0f, curHarm * 0.55f - inp.CanHarmTarget * 0.25f);
                float fightWin = (inp.HasVisibleEnemy ? inp.CanHarmTarget * 0.35f : 0f) + (inp.HasObjectiveTask ? 0.22f : 0.08f);
                ConsiderCandidate(
                    EmergentActionId.VehicleFaceAndFight,
                    EmergentActionId.None,
                    fightAfterHarm,
                    fightWin,
                    0f,
                    fightAfterHarm,
                    0.06f,
                    inp.CanHarmTarget > 0.05f ? "armor_angle_engage" : "armor_angle_standoff");
            }
            else
            {
                // ---- INFANTRY ----
                bool safeInCover = inp.InCover && !inp.ExposedSideOfWall;
                bool cqbImmediate = inp.HasVisibleEnemy && !inp.AmmoEmpty && inp.CanHarmTarget > 0.20f && inp.VisibleEnemyDist <= 12.0f;
                float sqAff = Mathf.Lerp(1.45f, 1.12f, w.Individualism);
                bool prereqResupplyBlocked = now < inp.PrereqBlockedUntil && inp.PrereqBlockedAction == (byte)EmergentActionId.ResupplyAmmo;
                bool prereqBandageBlocked = now < inp.PrereqBlockedUntil && inp.PrereqBlockedAction == (byte)EmergentActionId.SelfBandage;
                bool prereqMedicBlocked = now < inp.PrereqBlockedUntil && inp.PrereqBlockedAction == (byte)EmergentActionId.MedicHeal;

                // 1. SeekCover / RepositionWallSide
                if (inp.ExposedSideOfWall && !inp.IsOnBridge && !cqbImmediate)
                {
                    ConsiderCandidate(
                        EmergentActionId.RepositionWallSide,
                        EmergentActionId.None,
                        curHarm * 0.28f,
                        0.04f,
                        0f,
                        curHarm * 0.30f,
                        curHarm >= 0.22f ? 0.14f : 0.0f,
                        "shelter_wall_side");
                }
                else if (!cqbImmediate && !safeInCover && curHarm >= 0.28f)
                {
                    float coverHarmDrop = curHarm * (inp.CoverAvailable ? 0.26f : 0.48f);
                    float urgency = inp.SmoothedHarmNow >= 0.62f
                        ? 0.28f
                        : ((curHarm >= 0.48f && !inp.SmokeActive && !inp.AllyPinningThreat)
                            ? 0.18f
                            : (curHarm >= 0.32f ? 0.10f : -0.05f));
                    ConsiderCandidate(
                        EmergentActionId.SeekCover,
                        EmergentActionId.None,
                        coverHarmDrop,
                        0.02f,
                        0f,
                        coverHarmDrop,
                        urgency,
                        "seek_cover");
                }

                // 2. SelfBandage (+ 2-3 ply SeekCover / Smoke prerequisite chain)
                if (inp.IsBleeding && inp.HasBandage && (safeInCover || !prereqBandageBlocked))
                {
                    float bleedThreat = safeInCover ? curHarm * 0.25f : curHarm;
                    bool canBandageNow = !inp.PointBlankThreat &&
                                         (safeInCover ? !cqbImmediate : (!cqbImmediate && IsSafeForDuration(bleedThreat, 5.0f, w.RiskThreshold)));
                    if (canBandageNow)
                    {
                        ConsiderCandidate(
                            EmergentActionId.SelfBandage,
                            EmergentActionId.None,
                            Mathf.Max(0f, bleedThreat - 0.12f),
                            0.18f,
                            0f,
                            bleedThreat,
                            safeInCover ? 0.56f : 0.42f,
                            "bandage_safe");
                    }
                    else if (!safeInCover && !cqbImmediate)
                    {
                        float afterCoverHarm = curHarm * (inp.CoverAvailable ? 0.22f : 0.42f);
                        if (inp.CoverAvailable && IsSafeForDuration(afterCoverHarm, 5.0f, w.RiskThreshold))
                        {
                            ConsiderCandidate(
                                EmergentActionId.SeekCover,
                                EmergentActionId.SelfBandage,
                                afterCoverHarm,
                                0.14f,
                                0f,
                                afterCoverHarm,
                                0.30f,
                                "prereq_cover_bandage");
                        }
                        else if (inp.HasSmokeGrenade && !inp.SmokeActive &&
                                 IsSafeForDuration(afterCoverHarm * 0.45f, 5.0f, w.RiskThreshold))
                        {
                            ConsiderCandidate(
                                EmergentActionId.PopSmoke,
                                EmergentActionId.SelfBandage,
                                afterCoverHarm * 0.45f,
                                0.10f,
                                0.05f,
                                afterCoverHarm * 0.45f,
                                0.18f,
                                "prereq_smoke_bandage");
                        }
                        else
                        {
                            unsafePrereqRejected = true;
                            rejectedPrereqAction = EmergentActionId.SelfBandage;
                        }
                    }
                }
                else if (inp.IsBleeding && !inp.HasBandage && inp.NearbyAmmoSource && !prereqResupplyBlocked && !inp.PointBlankThreat)
                {
                    float bleedScavHarm = safeInCover ? curHarm * 0.45f : curHarm;
                    if (IsSafeForDuration(bleedScavHarm, 1.5f, w.RiskThreshold))
                    {
                        ConsiderCandidate(
                            EmergentActionId.ResupplyAmmo,
                            EmergentActionId.SelfBandage,
                            bleedScavHarm * 0.65f,
                            0.38f,
                            0f,
                            bleedScavHarm,
                            0.34f * w.BiasScavenge,
                            "prereq_bandage_scavenge");
                    }
                    else
                    {
                        unsafePrereqRejected = true;
                        rejectedPrereqAction = EmergentActionId.ResupplyAmmo;
                    }
                }

                // 3. HoldAndFight (Lethality strictly gated on CanHarmTarget > 0) + Reload/Resupply prereq
                if (inp.NeedsPrimaryWeaponUpgrade &&
                    inp.NearbyAmmoSource &&
                    !prereqResupplyBlocked &&
                    !cqbImmediate &&
                    !(inp.HasVisibleEnemy && inp.VisibleEnemyDist <= 16.0f))
                {
                    float upgHarm = safeInCover ? curHarm * 0.50f : curHarm;
                    if (IsSafeForDuration(upgHarm, 1.5f, w.RiskThreshold))
                    {
                        ConsiderCandidate(
                            EmergentActionId.ResupplyAmmo,
                            EmergentActionId.None,
                            curHarm * 0.65f,
                            0.62f,
                            0f,
                            upgHarm,
                            0.24f * w.BiasScavenge,
                            "scavenge_primary_weapon");
                    }
                }

                if (inp.NeedsAtWeaponScavenge &&
                    inp.NearbyAmmoSource &&
                    !prereqResupplyBlocked &&
                    !cqbImmediate &&
                    !(inp.HasVisibleEnemy && inp.CanHarmTarget > 0.05f && inp.VisibleEnemyDist <= 16.0f))
                {
                    float atScavHarm = safeInCover ? curHarm * 0.40f : curHarm;
                    if (IsSafeForDuration(atScavHarm, 1.5f, w.RiskThreshold))
                    {
                        ConsiderCandidate(
                            EmergentActionId.ResupplyAmmo,
                            EmergentActionId.HoldAndFight,
                            curHarm * 0.55f,
                            inp.UncounterableArmorClose ? 0.68f : 0.46f,
                            0f,
                            atScavHarm,
                            (inp.UncounterableArmorClose ? 0.26f : 0.20f) * w.BiasScavenge,
                            "scavenge_at_weapon");
                    }
                    else if (inp.UncounterableArmorClose)
                    {
                        unsafePrereqRejected = true;
                        rejectedPrereqAction = EmergentActionId.ResupplyAmmo;
                    }
                }

                if (inp.HasVisibleEnemy && inp.CanHarmTarget > 0.05f)
                {
                    if (inp.AmmoEmpty)
                    {
                        float resupHarm = safeInCover ? curHarm * 0.40f : curHarm * 0.65f;
                        if (inp.NearbyAmmoSource && !prereqResupplyBlocked && IsSafeForDuration(resupHarm, 1.5f, w.RiskThreshold))
                        {
                            ConsiderCandidate(
                                EmergentActionId.ResupplyAmmo,
                                EmergentActionId.HoldAndFight,
                                curHarm * 0.50f,
                                0.48f,
                                0f,
                                resupHarm,
                                0.18f * w.BiasScavenge,
                                "prereq_ammo_fight");
                        }
                        else if (inp.NearbyAmmoSource)
                        {
                            unsafePrereqRejected = true;
                            rejectedPrereqAction = EmergentActionId.ResupplyAmmo;
                        }
                    }
                    else
                    {
                        float defReduction = inp.CanHarmTarget * 0.42f;
                        float afterFightHarm = safeInCover
                            ? Mathf.Max(0f, curHarm - defReduction * 0.85f)
                            : Mathf.Max(curHarm * 0.85f, curHarm - defReduction * 0.25f);
                        if (cqbImmediate)
                            afterFightHarm = Mathf.Max(0f, afterFightHarm - 0.22f);
                        float offWin = (0.34f + inp.ObjectiveUrgency * 0.24f) * inp.CanHarmTarget * w.BiasStandoff;
                        if (!safeInCover && !cqbImmediate && inp.HasObjectiveTask && !inp.InsideObjectiveZone)
                        {
                            if (inp.SmokeActive || inp.AllyPinningThreat || inp.EnemyRelief >= 0.15f)
                                offWin *= 0.72f;
                            else if (inp.VisibleEnemyDist > 30f)
                                offWin *= 0.82f;
                        }
                        float cqbBonus = cqbImmediate
                            ? Mathf.Lerp(0.46f, 0.18f, Mathf.Clamp01((inp.VisibleEnemyDist - 2.0f) / 10.0f))
                            : 0f;
                        float aidFight = inp.HasAidOverwatchNeed ? (inp.AidOverwatchSameSquad ? 0.16f * sqAff : 0.16f) : 0f;
                        float cohesionFight = (safeInCover && inp.SquadCohesionBonus > 0f)
                            ? (0.03f * inp.SquadCohesionBonus * (1.15f - w.Individualism))
                            : 0f;
                        ConsiderCandidate(
                            EmergentActionId.HoldAndFight,
                            EmergentActionId.None,
                            afterFightHarm,
                            offWin,
                            aidFight,
                            afterFightHarm,
                            (safeInCover ? 0.12f : 0.02f) + cqbBonus + cohesionFight,
                            cqbImmediate ? "cqb_snap_engage" : "direct_engage");
                    }
                }
                else
                {
                    if (!inp.HasVisibleEnemy && safeInCover && !inp.AmmoEmpty && !inp.ObjectiveTickingAgainstUs &&
                        (inp.InsideObjectiveZone || (inp.HasLastKnownThreat && inp.LastKnownThreatDist <= 65.0f)))
                    {
                        float overwatchWin = (inp.InsideObjectiveZone ? 0.42f : 0.24f) * w.BiasStandoff * (inp.IsMachineGunnerOrBipod ? 1.25f : 1.0f);
                        float overwatchBonus = (inp.HasLastKnownThreat ? 0.14f : 0.06f) + 0.10f * Mathf.Clamp01(1.10f - w.Individualism);
                        if (inp.IsHomeGroundDefender && inp.InsideObjectiveZone)
                        {
                            overwatchWin *= 1.18f;
                            overwatchBonus += 0.08f;
                        }
                        ConsiderCandidate(
                            EmergentActionId.HoldAndFight,
                            EmergentActionId.None,
                            curHarm * 0.65f,
                            overwatchWin,
                            0f,
                            curHarm * 0.65f,
                            overwatchBonus,
                            "hold_angle_overwatch");
                    }

                    if ((inp.AmmoEmpty || (!inp.HasVisibleEnemy && curHarm < 0.20f && !inp.ObjectiveTickingAgainstUs)) && inp.NearbyAmmoSource && !prereqResupplyBlocked)
                {
                    float resupHarm2 = safeInCover ? curHarm * 0.45f : curHarm;
                    if (IsSafeForDuration(resupHarm2, 1.5f, w.RiskThreshold))
                    {
                        ConsiderCandidate(
                            EmergentActionId.ResupplyAmmo,
                            EmergentActionId.None,
                            curHarm * 0.70f,
                            inp.AmmoEmpty ? 0.54f : 0.46f,
                            0f,
                            resupHarm2,
                            (inp.AmmoEmpty ? 0.20f : 0.18f) * w.BiasScavenge,
                            inp.AmmoEmpty ? "resupply_ammo" : "scavenge_gear");
                    }
                    else if (inp.AmmoEmpty)
                    {
                        unsafePrereqRejected = true;
                        rejectedPrereqAction = EmergentActionId.ResupplyAmmo;
                    }
                }
                }

                // 4. PushAmmoDrop (Ammo Bearer Push)
                if (inp.IsAmmoCarrier && inp.AllyNeedsAmmoDropNearby && !inp.PointBlankThreat &&
                    IsSafeForDuration(curHarm, 1.5f, w.RiskThreshold))
                {
                    ConsiderCandidate(
                        EmergentActionId.PushAmmoDrop,
                        EmergentActionId.None,
                        curHarm * 0.85f,
                        0.22f,
                        0.32f,
                        curHarm,
                        0.12f,
                        "ammo_carrier_drop");
                }

                // 5. SuppressPin (MG & Base of Fire + Overwatch CanAid)
                float suppressRange = inp.HasVisibleEnemy ? inp.VisibleEnemyDist : inp.LastKnownThreatDist;
                float maxSuppressDist = inp.IsMachineGunnerOrBipod ? 185f : 115f;
                if ((inp.IsMachineGunnerOrBipod || inp.SquadDirective == Tier2SquadDirective.AnvilPin || inp.HasAidOverwatchNeed) &&
                    !inp.AmmoEmpty && !inp.ExposedSideOfWall &&
                    (inp.HasLastKnownThreat || inp.HasVisibleEnemy) &&
                    inp.CanHarmTarget > 0.05f &&
                    suppressRange > 0f && suppressRange <= maxSuppressDist)
                {
                    float supAid = inp.HasAidOverwatchNeed ? (inp.AidOverwatchSameSquad ? 0.34f * sqAff : 0.34f) : 0.22f;
                    float supHarmAfter = Mathf.Max(0f, curHarm - (safeInCover ? 0.28f : 0.10f));
                    ConsiderCandidate(
                        EmergentActionId.SuppressPin,
                        EmergentActionId.None,
                        supHarmAfter,
                        0.18f * w.BiasSuppress,
                        supAid,
                        supHarmAfter,
                        inp.IsMachineGunnerOrBipod ? 0.14f : 0.04f,
                        "suppress_pin");
                }

                // 6. PopSmoke (Break high lane Harm / Enable Advance & Aid)
                bool assaultSmoke = inp.HasObjectiveTask && !inp.InsideObjectiveZone && curHarm >= 0.24f;
                if (!cqbImmediate && inp.HasSmokeGrenade && !inp.SmokeActive &&
                    (curHarm >= 0.36f || assaultSmoke || inp.SquadDirective == Tier2SquadDirective.SquadSmokePush || inp.InjuredAllyInOpen || inp.DoorwayFreshCasualtyAhead))
                {
                    float smokeHarm = curHarm * 0.32f;
                    float smokeAid = inp.InjuredAllyInOpen ? (inp.InjuredAllySameSquad ? 0.26f * sqAff : 0.26f) : 0.14f;
                    ConsiderCandidate(
                        EmergentActionId.PopSmoke,
                        EmergentActionId.None,
                        smokeHarm,
                        assaultSmoke ? 0.34f : 0.14f,
                        smokeAid,
                        smokeHarm,
                        (assaultSmoke ? 0.22f : 0.10f) * w.BiasSmoke,
                        "pop_smoke");
                }

                // 7. FragFlush (Entrenched, doorway killzones, OR clustered enemies blocking advance)
                float fragRangeDist = inp.HasVisibleEnemy ? inp.VisibleEnemyDist : inp.LastKnownThreatDist;
                bool roomOrCluster = inp.ClusteredEnemiesAtThreat >= 2 || inp.DoorwayFreshCasualtyAhead;
                float minFragDist = safeInCover ? 6.5f : 8.5f;
                if (inp.HasFragGrenade &&
                    (inp.TargetBehindCover || roomOrCluster) &&
                    fragRangeDist >= minFragDist &&
                    fragRangeDist <= 31.0f &&
                    IsSafeForDuration(curHarm * 0.78f, 1.5f, w.RiskThreshold))
                {
                    float clusterWin = inp.ClusteredEnemiesAtThreat >= 2
                        ? (0.26f + 0.08f * Mathf.Min(2, inp.ClusteredEnemiesAtThreat - 2))
                        : (inp.DoorwayFreshCasualtyAhead ? 0.24f : 0f);
                    float clusterBonus = roomOrCluster ? 0.18f : 0.08f;
                    ConsiderCandidate(
                        EmergentActionId.FragFlush,
                        EmergentActionId.None,
                        Mathf.Max(0f, curHarm - 0.30f),
                        (0.38f + clusterWin) * w.BiasFrag,
                        0.10f,
                        curHarm * 0.78f,
                        clusterBonus,
                        inp.ClusteredEnemiesAtThreat >= 2 ? "frag_cluster" : (inp.DoorwayFreshCasualtyAhead ? "frag_doorway_clear" : "frag_flush"));
                }

                // 8. FlankManeuver (Low Harm approach when enemy pinned/smoked or High Individualism)
                if (!cqbImmediate && !inp.IsLeader && !inp.IsRadioman && !inp.IsMachineGunnerOrBipod && !inp.IsOnBridge && !inp.InsideObjectiveZone && inp.FlankCoverAvailable &&
                    !(safeInCover && inp.HasVisibleEnemy && inp.CanHarmTarget >= 0.70f) &&
                    (inp.AllyPinningThreat || inp.SmokeActive || inp.SquadDirective == Tier2SquadDirective.HammerFlank || w.Individualism >= 0.55f))
                {
                    float flankHarm = safeInCover
                        ? Mathf.Max(curHarm * 0.75f, 0.16f)
                        : curHarm * (inp.AllyPinningThreat || inp.SmokeActive ? 0.48f : 0.68f);
                    if (flankHarm <= w.RiskThreshold + 0.12f)
                    {
                        ConsiderCandidate(
                            EmergentActionId.FlankManeuver,
                            EmergentActionId.None,
                            flankHarm,
                            (0.32f + inp.EnemyRelief * 0.20f) * w.BiasFlank,
                            0.08f,
                            flankHarm,
                            0.08f,
                            "flank_maneuver");
                    }
                }

                // 9. MedicHeal & DragCasualty (+ 3-ply Smoke -> Drag -> MedicHeal chain)
                if (inp.IsMedic && inp.HasInjuredAlly && !prereqMedicBlocked)
                {
                    float medExposure = inp.InjuredAllyInOpen ? Mathf.Max(curHarm, inp.InjuredAllyHarm) : curHarm * 0.55f;
                    float medSqMult = inp.InjuredAllySameSquad ? sqAff : 1.0f;
                    if (IsSafeForDuration(medExposure, 5.0f, w.RiskThreshold))
                    {
                        ConsiderCandidate(
                            EmergentActionId.MedicHeal,
                            EmergentActionId.None,
                            medExposure * 0.85f,
                            0.24f,
                            0.42f * medSqMult,
                            medExposure,
                            0.18f,
                            "medic_heal");
                    }
                    else if (inp.InjuredAllyInOpen)
                    {
                        float smokeExposure = medExposure * 0.38f;
                        if (inp.HasSmokeGrenade && !inp.SmokeActive &&
                            IsSafeForDuration(smokeExposure, 5.0f, w.RiskThreshold))
                        {
                            ConsiderCandidate(
                                EmergentActionId.PopSmoke,
                                EmergentActionId.MedicHeal,
                                smokeExposure,
                                0.22f,
                                0.40f * medSqMult,
                                smokeExposure,
                                0.16f,
                                "prereq_smoke_medic");
                        }
                        else if (inp.HasDownedAllyToDrag && IsSafeForDuration(smokeExposure, 4.0f, w.RiskThreshold + 0.10f))
                        {
                            ConsiderCandidate(
                                EmergentActionId.DragCasualty,
                                EmergentActionId.MedicHeal,
                                smokeExposure,
                                0.22f,
                                0.38f * medSqMult,
                                smokeExposure,
                                0.14f,
                                "prereq_drag_medic");
                        }
                        else
                        {
                            unsafePrereqRejected = true;
                            rejectedPrereqAction = EmergentActionId.MedicHeal;
                        }
                    }
                }
                else if (inp.HasDownedAllyToDrag &&
                         !inp.IsLeader &&
                         !inp.PointBlankThreat &&
                         !cqbImmediate &&
                         !(!safeInCover && inp.HasVisibleEnemy && inp.VisibleEnemyDist <= 24.0f))
                {
                    float dragHarm = curHarm * (inp.SmokeActive ? 0.38f : 0.68f);
                    if (IsSafeForDuration(dragHarm, 4.0f, w.RiskThreshold))
                    {
                        ConsiderCandidate(
                            EmergentActionId.DragCasualty,
                            EmergentActionId.None,
                            dragHarm * 0.70f,
                            0.18f,
                            inp.DownedAllySameSquad ? 0.36f * sqAff : 0.36f,
                            dragHarm,
                            0.08f,
                            "drag_casualty");
                    }
                }

                // 10. LiberatePrisoner (Exclusive claim + Safe window)
                if (inp.HasSurrenderedPrisoner && !inp.IsLeader && !inp.IsRadioman && !inp.PointBlankThreat && !cqbImmediate &&
                    IsSafeForDuration(curHarm, 1.5f, w.RiskThreshold))
                {
                    ConsiderCandidate(
                        EmergentActionId.LiberatePrisoner,
                        EmergentActionId.None,
                        curHarm * 0.85f,
                        0.28f,
                        inp.PrisonerSameSquad ? 0.24f * sqAff : 0.24f,
                        curHarm,
                        0.12f,
                        "liberate_comrade");
                }

                // 11. MaintainRadioLink
                if (inp.IsRadioman && inp.RadioLinkDrifting &&
                    !(inp.HasVisibleEnemy && !inp.AmmoEmpty && inp.CanHarmTarget > 0.20f && inp.VisibleEnemyDist <= 16.0f))
                {
                    ConsiderCandidate(
                        EmergentActionId.MaintainRadioLink,
                        EmergentActionId.None,
                        curHarm * 0.78f,
                        0.22f,
                        0.28f,
                        curHarm * 0.78f,
                        0.24f,
                        "preserve_radio_link");
                }

                // 12. AdvanceObjective (Bounded by Safe/Harm, or Sweep Contesters when objective is ticking against us)
                float effObjUrgency = inp.ObjectiveTickingAgainstUs ? Mathf.Max(inp.ObjectiveUrgency, 0.85f) : inp.ObjectiveUrgency;
                if (inp.HasObjectiveTask &&
                    (!inp.InsideObjectiveZone || (inp.ObjectiveTickingAgainstUs && !inp.HasVisibleEnemy && !cqbImmediate)) &&
                    inp.SquadDirective != Tier2SquadDirective.BalkNeedSupport)
                {
                    float coverFactor = (inp.SmokeActive || inp.AllyPinningThreat) ? 0.45f : 0.92f;
                    if (inp.InsideObjectiveZone && inp.ObjectiveTickingAgainstUs)
                    {
                        float sweepHarm = Mathf.Clamp01(curHarm * coverFactor - inp.EnemyRelief * 0.20f);
                        if (sweepHarm <= w.RiskThreshold + 0.22f || inp.SmokeActive || inp.AllyPinningThreat || inp.SectorDesperationBias >= 0.15f)
                        {
                            ConsiderCandidate(
                                EmergentActionId.AdvanceObjective,
                                EmergentActionId.None,
                                sweepHarm,
                                0.56f + effObjUrgency * 0.36f,
                                0.10f,
                                sweepHarm,
                                0.14f,
                                "sweep_cap_contesters");
                        }
                    }
                    else
                    {
                        float visPen = cqbImmediate
                            ? 0.36f
                            : ((inp.HasVisibleEnemy && !inp.SmokeActive && !inp.AllyPinningThreat)
                                ? Mathf.Lerp(0.28f, 0.08f, Mathf.Clamp01((inp.VisibleEnemyDist - 12.0f) / 38.0f))
                                : 0f);
                        float openAdvHarmPen = (!safeInCover && curHarm >= 0.52f && !inp.SmokeActive && !inp.AllyPinningThreat && inp.SectorDesperationBias < 0.20f)
                            ? 0.10f
                            : 0f;
                        float advHarm = Mathf.Clamp01(curHarm * coverFactor + visPen + openAdvHarmPen - inp.EnemyRelief * 0.25f);
                        bool underHeavyDirectFireExposed = !safeInCover && inp.SmoothedHarmNow >= 0.62f;
                        if ((!underHeavyDirectFireExposed && (advHarm <= w.RiskThreshold + 0.14f || inp.SmokeActive || inp.AllyPinningThreat)) ||
                            inp.SectorDesperationBias >= (underHeavyDirectFireExposed ? 0.30f : 0.20f))
                        {
                            float timeFactor = 1.0f / (1.0f + inp.ObjectiveDist * 0.006f);
                            float winGain = (0.42f + effObjUrgency * 0.50f) * timeFactor + inp.EnemyRelief * 0.25f + (inp.ObjectiveTickingAgainstUs ? 0.14f : 0f);
                            float advCohesion = inp.SquadCohesionBonus > 0f ? 0.03f * inp.SquadCohesionBonus * (1.15f - w.Individualism) : 0f;
                            ConsiderCandidate(
                                EmergentActionId.AdvanceObjective,
                                EmergentActionId.None,
                                advHarm,
                                winGain,
                                0f,
                                advHarm,
                                ((inp.EnemyRelief > 0.20f || inp.SmokeActive || inp.AllyPinningThreat || inp.ObjectiveTickingAgainstUs) ? 0.14f : 0.06f) + advCohesion,
                                inp.EnemyRelief > 0.20f ? "surge_enemy_relief" : "advance_objective");
                        }
                    }
                }

                // 13. FallbackRegroup (Impossible Harm / Uncounterable Armor)
                if (inp.Overwhelmed || (inp.UncounterableArmorClose && !inp.IsAtCapable && curHarm >= 0.35f) ||
                    (inp.SquadDirective == Tier2SquadDirective.LocalFallback && curHarm >= 0.35f))
                {
                    ConsiderCandidate(
                        EmergentActionId.FallbackRegroup,
                        EmergentActionId.None,
                        curHarm * 0.22f,
                        0.0f,
                        0f,
                        curHarm * 0.22f,
                        0.12f,
                        inp.UncounterableArmorClose ? "evade_armor" : "tactical_fallback");
                }
            }

            if (bestStep0 == EmergentActionId.None)
            {
                bool unsafeToMove = curHarm >= w.RiskThreshold * 0.65f || inp.InCover || inp.SquadDirective == Tier2SquadDirective.BalkNeedSupport;
                if (inp.HasObjectiveTask && !inp.InsideObjectiveZone && !unsafeToMove)
                {
                    bestStep0 = EmergentActionId.AdvanceObjective;
                    bestReason = "default_advance";
                }
                else if (!inp.InCover && inp.CoverAvailable && !inp.HasVisibleEnemy &&
                         (inp.InsideObjectiveZone || inp.IsHomeGroundDefender || !inp.HasObjectiveTask))
                {
                    bestStep0 = EmergentActionId.SeekCover;
                    bestReason = "default_take_cover";
                }
                else
                {
                    bestStep0 = EmergentActionId.HoldAndFight;
                    bestReason = "default_hold_cover";
                }
                bestScore = 0.05f;
            }

            float dwell = (bestStep0 == EmergentActionId.SeekCover ||
                           bestStep0 == EmergentActionId.FlankManeuver ||
                           bestStep0 == EmergentActionId.MedicHeal ||
                           bestStep0 == EmergentActionId.DragCasualty ||
                           bestStep0 == EmergentActionId.FallbackRegroup)
                ? 2.2f
                : ((bestStep0 == EmergentActionId.AdvanceObjective || bestStep0 == EmergentActionId.SuppressPin)
                    ? 2.0f
                    : (bestStep0 == EmergentActionId.SelfBandage || bestStep0 == EmergentActionId.VehicleFieldRepair ? 3.5f : 1.5f));

            return new EmergentDecision
            {
                Step0 = bestStep0,
                Step1 = bestStep1,
                Reflex = EmergentReflexKind.None,
                Score = bestScore,
                RunnerUp = runnerAct,
                RunnerUpScore = runnerScore > -900f ? runnerScore : 0f,
                HarmNow = inp.SmoothedHarmNow,
                HarmProj = effProjHarm,
                TotalHarm = curHarm,
                WinUrgency = inp.ObjectiveUrgency,
                CanHarm = inp.CanHarmTarget,
                ChainHarm = bestChainHarm,
                DecideTime = now,
                CommitUntil = now + dwell,
                UnsafePrereqRejected = unsafePrereqRejected,
                RejectedPrereqAction = rejectedPrereqAction,
                Reason = bestReason
            };
        }

        internal static Tier2SquadDecision EvaluateTier2Squad(
            in Tier2SquadInput inp,
            in VeteranAndTacticsPatches.FactionCommandProfile prof)
        {
            // Tier 2: Balanced (Win while keeping the squad alive; Approve vs Balk Pushback)
            float squadHarm = Mathf.Clamp01(Mathf.Max(inp.AvgSquadHarm, inp.ProjectedLaneHarm * 0.85f) + inp.DecayedRecentDeaths * 0.14f - inp.EnemyRelief * 0.28f);
            float armorBlocker = (inp.ThreatIsArmor && !inp.SquadHasAT) ? 0.45f : 0f;
            float harmTotal = Mathf.Clamp01(squadHarm + armorBlocker);

            float pivotTol = Mathf.Clamp(prof.AttritionPivotThreshold / 7.5f, 0.75f, 1.45f);
            float tolerance = Mathf.Clamp(0.56f * pivotTol + inp.SectorDesperationBias * 0.38f + (inp.IsGyokusaiOrFinalStand ? 0.35f : 0f), 0.35f, 0.96f);

            if (inp.IsVehicleCrew)
            {
                bool bail = inp.AliveMembers <= 0 || (harmTotal > tolerance && !inp.SquadHasAT && inp.ThreatIsArmor);
                return new Tier2SquadDecision
                {
                    Directive = bail ? Tier2SquadDirective.LocalFallback : Tier2SquadDirective.AdvanceBound,
                    BalkReason = bail ? Tier2BalkReason.BlockedByArmor : Tier2BalkReason.None,
                    SurviveUtility = 1.0f - harmTotal,
                    WinUtility = inp.ObjectiveUrgency,
                    Reason = bail ? "veh_crew_intercom_bail" : "veh_crew_engage"
                };
            }

            if (inp.SquadOutOfAmmo && inp.HasActiveThreat && inp.ThreatDist <= 65f)
            {
                return new Tier2SquadDecision
                {
                    Directive = Tier2SquadDirective.BalkNeedSupport,
                    BalkReason = Tier2BalkReason.OutOfAmmo,
                    SurviveUtility = harmTotal,
                    WinUtility = 0.05f,
                    RequestSmoke = inp.SquadHasSmoke,
                    Reason = "sl_balk_ammo"
                };
            }

            if (armorBlocker > 0.1f && inp.ThreatDist <= 95f && !inp.IsGyokusaiOrFinalStand)
            {
                return new Tier2SquadDecision
                {
                    Directive = harmTotal > tolerance ? Tier2SquadDirective.BalkNeedSupport : Tier2SquadDirective.LocalFallback,
                    BalkReason = Tier2BalkReason.BlockedByArmor,
                    SurviveUtility = harmTotal,
                    WinUtility = 0.10f,
                    RequestSmoke = inp.SquadHasSmoke,
                    RequestArmorOrArty = true,
                    Reason = "sl_balk_armor"
                };
            }

            if (harmTotal > tolerance && !inp.IsGyokusaiOrFinalStand &&
                inp.EnemyRelief < 0.20f &&
                (inp.DecayedRecentDeaths >= 1.25f || inp.AvgSquadHarm >= 0.62f || inp.AliveMembers <= 2))
            {
                Tier2BalkReason bReason = inp.DecayedRecentDeaths >= 2.0f ? Tier2BalkReason.HeavyCasualties : Tier2BalkReason.PinnedByMG;
                if (inp.SquadHasSmoke && inp.HasActiveThreat)
                {
                    return new Tier2SquadDecision
                    {
                        Directive = Tier2SquadDirective.SquadSmokePush,
                        BalkReason = bReason,
                        SurviveUtility = harmTotal * 0.6f,
                        WinUtility = inp.ObjectiveUrgency * 0.7f,
                        RequestSmoke = true,
                        RequestArmorOrArty = true,
                        Reason = "sl_smoke_counter_balk"
                    };
                }
                return new Tier2SquadDecision
                {
                    Directive = (inp.AliveMembers <= 2 || inp.DecayedRecentDeaths >= 2.8f) ? Tier2SquadDirective.LocalFallback : Tier2SquadDirective.BalkNeedSupport,
                    BalkReason = bReason,
                    SurviveUtility = harmTotal,
                    WinUtility = 0.15f,
                    RequestArmorOrArty = true,
                    Reason = bReason == Tier2BalkReason.HeavyCasualties ? "sl_balk_casualties" : "sl_balk_mg_pin"
                };
            }

            if (inp.HasActiveThreat && inp.FlankCoverAvailable && (inp.AllySquadPinning || inp.HasIntactRadio || prof.AllowDisconnectedLocalFlank) && !inp.LeaderIsSpearhead)
            {
                return new Tier2SquadDecision
                {
                    Directive = Tier2SquadDirective.HammerFlank,
                    SurviveUtility = 1.0f - harmTotal * 0.55f,
                    WinUtility = 0.55f + inp.ObjectiveUrgency * 0.3f,
                    Reason = "sl_hammer_flank"
                };
            }

            if (inp.HasActiveThreat && inp.ThreatDist > 45f && !inp.AllySquadPinning && !inp.LeaderIsSpearhead)
            {
                return new Tier2SquadDecision
                {
                    Directive = Tier2SquadDirective.AnvilPin,
                    SurviveUtility = 1.0f - harmTotal * 0.50f,
                    WinUtility = 0.48f,
                    Reason = "sl_anvil_pin"
                };
            }

            if (inp.HasActiveThreat && (inp.EnemyRelief >= 0.25f || inp.IsGyokusaiOrFinalStand || (inp.ObjectiveDist <= 35f && harmTotal <= 0.30f)))
            {
                return new Tier2SquadDecision
                {
                    Directive = Tier2SquadDirective.DecisiveBreach,
                    SurviveUtility = 1.0f - harmTotal,
                    WinUtility = 0.75f + inp.ObjectiveUrgency * 0.25f,
                    Reason = inp.EnemyRelief >= 0.25f ? "sl_breach_relief" : "sl_breach_final"
                };
            }

            return new Tier2SquadDecision
            {
                Directive = Tier2SquadDirective.AdvanceBound,
                SurviveUtility = 1.0f - harmTotal,
                WinUtility = 0.45f + inp.ObjectiveUrgency * 0.35f,
                Reason = "sl_advance_bound"
            };
        }

        internal static Tier3NetDecision EvaluateTier3Net(
            in Tier3NetInput inp,
            in VeteranAndTacticsPatches.FactionCommandProfile prof)
        {
            // Tier 3: Win first, bounded by Force Preservation (Survive) — Never sits idle!
            float attritionScale = prof.AttritionPivotThreshold > 1f ? (7.5f / prof.AttritionPivotThreshold) : 1.0f;
            float netHarm = Mathf.Clamp01(
                inp.LaneHarmHeat * 0.65f * attritionScale +
                Mathf.Clamp01(inp.MiaSilentTimeouts * 0.28f) +
                Mathf.Clamp01(inp.BalkingSquads * 0.18f) -
                inp.EnemyRelief * 0.30f);
            float netWinUrgency = Mathf.Clamp01(0.45f + (1.0f - inp.TicketRatio01) * 0.35f + inp.CriticalObjectiveThreat01 * 0.45f);

            Tier3NetLever bestPrimary = Tier3NetLever.MassAssault;
            Tier3NetLever bestSecondary = Tier3NetLever.None;
            float bestScore = -999f;
            float secondScore = -999f;
            string reason = "t3_mass_assault";

            void ConsiderLever(Tier3NetLever lev, float score, string r)
            {
                if (score > bestScore)
                {
                    secondScore = bestScore;
                    bestSecondary = bestPrimary;
                    bestScore = score;
                    bestPrimary = lev;
                    reason = r;
                }
                else if (score > secondScore && lev != bestPrimary)
                {
                    secondScore = score;
                    bestSecondary = lev;
                }
            }

            if (inp.HasCollapsingFriendlyCap && inp.CriticalObjectiveThreat01 >= 0.50f)
                ConsiderLever(Tier3NetLever.FireBrigade, 0.85f + netWinUrgency * 0.55f, "t3_fire_brigade");

            if (inp.InfantryBlockedByArmor)
            {
                if (inp.TanksAvailable)
                    ConsiderLever(Tier3NetLever.TankDispatch, 0.88f + netHarm * 0.45f, "t3_tank_dispatch");
                if (inp.AtAssetsAvailable)
                    ConsiderLever(Tier3NetLever.AtCounterRoute, 0.80f + netHarm * 0.40f, "t3_at_counter");
                if (inp.CasAvailable)
                    ConsiderLever(Tier3NetLever.CasVector, 0.78f + netHarm * 0.35f, "t3_cas_armor");
            }

            if (inp.TankNeedsInfantryScreen && inp.ActiveSquads >= 2)
                ConsiderLever(Tier3NetLever.ArmorInfantryPair, 0.76f + netHarm * 0.30f, "t3_armor_inf_pair");

            if (netHarm >= 0.38f || inp.BalkingSquads > 0 || inp.MiaSilentTimeouts > 0)
            {
                if (inp.SmokeAvailable)
                    ConsiderLever(Tier3NetLever.SmokeBarrage, 0.74f + netHarm * 0.52f, "t3_smoke_barrage");
                if (inp.ArtilleryAvailable)
                    ConsiderLever(Tier3NetLever.ArtilleryHE, 0.72f + netHarm * 0.50f, "t3_arty_he");
                if (inp.AlternativeFlankOpen)
                    ConsiderLever(Tier3NetLever.ShiftAxisFlank, 0.70f + netHarm * 0.48f + (inp.MiaSilentTimeouts > 0 ? 0.22f : 0f), inp.MiaSilentTimeouts > 0 ? "t3_shift_axis_mia" : "t3_shift_axis");
            }

            if (inp.ActiveSquads >= 2 && inp.HasShreddedEncircledSquad && netHarm >= 0.65f)
                ConsiderLever(Tier3NetLever.AuthorizeWithdraw, 0.66f + netHarm * 0.35f, "t3_authorize_withdraw");

            if (inp.FreshSpawnNeedsStaging && netHarm >= 0.40f && inp.EnemyRelief < 0.20f)
                ConsiderLever(Tier3NetLever.HoldAndFormUp, 0.62f + netHarm * 0.30f, "t3_hold_stage");

            if (inp.ActiveSquads >= 2)
                ConsiderLever(Tier3NetLever.AnvilAssign, 0.56f + netWinUrgency * 0.22f, "t3_anvil_assign");

            ConsiderLever(Tier3NetLever.MassAssault, 0.52f + netWinUrgency * 0.42f + inp.EnemyRelief * 0.35f - netHarm * 0.25f, inp.EnemyRelief >= 0.20f ? "t3_exploit_relief" : "t3_mass_assault");

            bool assetsExhausted = !inp.SmokeAvailable && !inp.ArtilleryAvailable && !inp.TanksAvailable && !inp.CasAvailable;
            float sectorDesp = 0f;
            if ((inp.TicketRatio01 <= 0.30f || inp.CriticalObjectiveThreat01 >= 0.65f) && (assetsExhausted || inp.BalkingSquads > 0))
            {
                sectorDesp = Mathf.Clamp((1.0f - inp.TicketRatio01) * 0.35f + inp.CriticalObjectiveThreat01 * 0.25f, 0.15f, 0.52f);
                ConsiderLever(Tier3NetLever.SectorDesperationBias, 0.82f + sectorDesp, "t3_desperation_bias");
            }

            return new Tier3NetDecision
            {
                PrimaryLever = bestPrimary,
                SecondaryLever = bestSecondary,
                SectorDesperationBias = sectorDesp,
                NetHarm = netHarm,
                NetWin = netWinUrgency,
                Reason = reason
            };
        }

        public static string GetActionShortName(EmergentActionId a)
        {
            switch (a)
            {
                case EmergentActionId.HoldAndFight: return "Engage";
                case EmergentActionId.SeekCover: return "SeekCover";
                case EmergentActionId.RepositionWallSide: return "WallSide";
                case EmergentActionId.EvadeGrenade: return "EvadeGren";
                case EmergentActionId.PopSmoke: return "PopSmoke";
                case EmergentActionId.SelfBandage: return "Bandage";
                case EmergentActionId.SuppressPin: return "Suppress";
                case EmergentActionId.FlankManeuver: return "Flank";
                case EmergentActionId.FragFlush: return "FragFlush";
                case EmergentActionId.AdvanceObjective: return "Advance";
                case EmergentActionId.CloseBreach: return "CQB";
                case EmergentActionId.FallbackRegroup: return "Fallback";
                case EmergentActionId.ResupplyAmmo: return "Resupply";
                case EmergentActionId.PushAmmoDrop: return "DropAmmo";
                case EmergentActionId.MedicHeal: return "MedicHeal";
                case EmergentActionId.DragCasualty: return "DragAlly";
                case EmergentActionId.LiberatePrisoner: return "Liberate";
                case EmergentActionId.MaintainRadioLink: return "RadioLink";
                case EmergentActionId.VehicleFaceAndFight: return "ArmorFight";
                case EmergentActionId.VehicleCrewSwap: return "SeatSwap";
                case EmergentActionId.VehicleFieldRepair: return "VehRepair";
                case EmergentActionId.VehicleBailout: return "VehBail";
                default: return "None";
            }
        }

        public static string GetReflexShortName(EmergentReflexKind r)
        {
            switch (r)
            {
                case EmergentReflexKind.LiveGrenade: return "Grenade";
                case EmergentReflexKind.HullCookoffDoom: return "HullDoom";
                case EmergentReflexKind.DoorwayFreshCasualty: return "DoorCas";
                case EmergentReflexKind.SuddenPenetratingFire: return "HeavyFire";
                case EmergentReflexKind.PointBlankAmbush: return "CQB";
                case EmergentReflexKind.LethalBleedExposed: return "BleedCov";
                case EmergentReflexKind.CloseAcousticAlert: return "Acoustic";
                default: return "None";
            }
        }

        public static uint PackInputFlags(in EmergentInput inp, bool unsafePrereqRejected)
        {
            uint f = 0u;
            if (inp.HasVisibleEnemy)           f |= 1u << 0;
            if (inp.InCover)                   f |= 1u << 1;
            if (inp.ExposedSideOfWall)         f |= 1u << 2;
            if (inp.IsBleeding)                f |= 1u << 3;
            if (inp.HasBandage)                f |= 1u << 4;
            if (inp.HasSmokeGrenade)           f |= 1u << 5;
            if (inp.HasFragGrenade)            f |= 1u << 6;
            if (inp.AmmoEmpty)                 f |= 1u << 7;
            if (inp.IsAtCapable)               f |= 1u << 8;
            if (inp.IsMedic)                   f |= 1u << 9;
            if (inp.IsLeader)                  f |= 1u << 10;
            if (inp.HasRadioLink)              f |= 1u << 11;
            if (inp.HasObjectiveTask)          f |= 1u << 12;
            if (inp.InsideObjectiveZone)       f |= 1u << 13;
            if (inp.FlankCoverAvailable)       f |= 1u << 14;
            if (inp.AllyPinningThreat)         f |= 1u << 15;
            if (inp.SmokeActive)               f |= 1u << 16;
            if (inp.HasLastKnownThreat)        f |= 1u << 17;
            if (inp.TargetBehindCover)         f |= 1u << 18;
            if (inp.DoorwayFreshCasualtyAhead) f |= 1u << 19;
            if (inp.UncounterableArmorClose)   f |= 1u << 20;
            if (inp.LiveGrenadeClose)          f |= 1u << 21;
            if (inp.Overwhelmed)               f |= 1u << 22;
            if (inp.HasInjuredAlly)            f |= 1u << 23;
            if (inp.NearbyAmmoSource)          f |= 1u << 24;
            if (unsafePrereqRejected)          f |= 1u << 25;
            if (inp.IsOnBridge)                f |= 1u << 26;
            if (inp.PointBlankThreat)          f |= 1u << 27;
            return f;
        }

        public enum AirActionId : byte
        {
            PatrolOrbit = 0,
            AttackGround = 1,
            InterceptAir = 2,
            RefillOrEvade = 3
        }

        public struct AirMissionInput
        {
            public bool IsBomber;
            public bool HasBombs;
            public bool HasPilotGunAmmo;
            public bool HasAntiArmorCannons;
            public bool UnderFireByAir;
            public bool UnderFireByGround;
            public bool HasEnemyAir;
            public bool EnemyAirIsBomber;
            public bool HasGroundTarget;
            public bool GroundTargetIsArmorOrCluster;
            public bool FriendlyGroundUnderThreat;
            public float Damage01;
            public float NearestEnemyAirDist;
            public float BestGroundTargetScore01;
            public float AltitudeAgl;
        }

        public struct AirMissionDecision
        {
            public AirActionId Action;
            public float Score;
            public float RunnerUpScore;
            public AirActionId RunnerUp;
            public float AirThreatHarm;
            public string Reason;
        }

        public static AirMissionDecision EvaluateAirMission(in AirMissionInput inp, in EmergentWeights w)
        {
            float airThreatHarm = 0f;
            if (inp.HasEnemyAir)
                airThreatHarm = Mathf.Clamp01(1.0f - Mathf.Clamp01((inp.NearestEnemyAirDist - 350.0f) / 1150.0f));
            if (inp.UnderFireByAir)
                airThreatHarm = Mathf.Max(airThreatHarm, 0.78f);

            AirActionId bestAct = AirActionId.PatrolOrbit;
            AirActionId runnerAct = AirActionId.PatrolOrbit;
            float bestScore = 0.15f;
            float runnerScore = -999f;
            string bestReason = "air_patrol";

            void ConsiderAir(AirActionId act, float score, string reason)
            {
                if (score > bestScore)
                {
                    runnerScore = bestScore;
                    runnerAct = bestAct;
                    bestScore = score;
                    bestAct = act;
                    bestReason = reason;
                }
                else if (score > runnerScore && act != bestAct)
                {
                    runnerScore = score;
                    runnerAct = act;
                }
            }

            if (!inp.HasBombs && !inp.HasPilotGunAmmo)
            {
                ConsiderAir(AirActionId.RefillOrEvade, 1.60f, "air_ammo_empty");
            }
            else if (inp.Damage01 >= 0.78f && (inp.UnderFireByAir || airThreatHarm >= 0.55f))
            {
                ConsiderAir(AirActionId.RefillOrEvade, 1.15f * w.WSurvive, "air_damage_evade");
            }

            if (inp.HasGroundTarget && (inp.HasBombs || inp.HasPilotGunAmmo))
            {
                float groundWin = (inp.IsBomber && inp.HasBombs ? 0.98f : (inp.HasBombs ? 0.80f : (inp.HasAntiArmorCannons ? 0.58f : 0.42f))) +
                                  inp.BestGroundTargetScore01 * 0.25f +
                                  (inp.GroundTargetIsArmorOrCluster ? 0.14f : 0f) +
                                  (inp.FriendlyGroundUnderThreat ? 0.10f : 0f);
                float groundHarm = (inp.IsBomber ? airThreatHarm * 0.42f : airThreatHarm * 0.85f) +
                                   (inp.UnderFireByGround ? 0.18f : 0f);
                float groundScore = w.WWin * groundWin - w.WSurvive * groundHarm * 0.65f + (inp.HasBombs ? 0.20f : 0f);
                ConsiderAir(AirActionId.AttackGround, groundScore, inp.HasBombs ? "air_bomb_run" : "air_cas_strafe");
            }

            if (inp.HasEnemyAir && inp.HasPilotGunAmmo && !inp.IsBomber)
            {
                float airWin = (inp.HasBombs ? 0.28f : 0.74f) +
                               (inp.EnemyAirIsBomber ? 0.26f : 0.08f) +
                               (inp.FriendlyGroundUnderThreat && inp.EnemyAirIsBomber ? 0.14f : 0f);
                float airSurv = inp.UnderFireByAir
                    ? 0.75f
                    : (airThreatHarm >= 0.45f ? airThreatHarm * 0.72f : airThreatHarm * 0.25f);
                float airScore = w.WWin * airWin + w.WSurvive * airSurv -
                                 (inp.HasBombs && !inp.UnderFireByAir && inp.NearestEnemyAirDist > 950.0f ? 0.28f : 0f);
                string airReason = inp.UnderFireByAir
                    ? "air_defend_jumped"
                    : (inp.EnemyAirIsBomber ? "air_intercept_bomber" : "air_screen_fighter");
                ConsiderAir(AirActionId.InterceptAir, airScore, airReason);
            }

            return new AirMissionDecision
            {
                Action = bestAct,
                Score = bestScore,
                RunnerUp = runnerAct,
                RunnerUpScore = runnerScore < -100f ? 0f : runnerScore,
                AirThreatHarm = airThreatHarm,
                Reason = bestReason
            };
        }
    }
}
