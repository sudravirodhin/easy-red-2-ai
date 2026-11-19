using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace EasyRed2HumanAI
{
    public static partial class VeteranAndTacticsPatches
    {
        // =====================================================================
        // PHASE 3: Macro Objective Director, Static Gun Staffing, Smart APC
        // Dismounting & Tank Standoff / Reverse Discipline
        // =====================================================================
        private static float _nextMacroDirectorTime;
        internal static readonly Dictionary<IntPtr, IntPtr> _garrisonAssignedObjectiveBySquad = new Dictionary<IntPtr, IntPtr>(64);
        internal static readonly Dictionary<IntPtr, float> _assignedEmplacementUntil = new Dictionary<IntPtr, float>(128);
        internal static readonly Dictionary<IntPtr, IntPtr> _assignedEmplacementVehicle = new Dictionary<IntPtr, IntPtr>(128);
        internal static readonly Dictionary<IntPtr, float> _nextApcCheckTime = new Dictionary<IntPtr, float>(64);
        internal static readonly Dictionary<IntPtr, float> _nextApcDismountCooldown = new Dictionary<IntPtr, float>(64);
        internal static readonly Dictionary<IntPtr, float> _nextTankStandoffCountTime = new Dictionary<IntPtr, float>(64);
        internal static readonly Dictionary<IntPtr, Vector4> _cachedVehicleExtents = new Dictionary<IntPtr, Vector4>(64);
        internal static readonly Dictionary<IntPtr, float> _nextVehiclePedestrianScanTime = new Dictionary<IntPtr, float>(64);
        internal static readonly Dictionary<IntPtr, float> _vehicleYieldUntilTime = new Dictionary<IntPtr, float>(64);
        internal static readonly Dictionary<IntPtr, float> _vehicleContinuousYieldStart = new Dictionary<IntPtr, float>(64);
        internal static readonly Dictionary<IntPtr, float> _nextFriendlyYieldCountTime = new Dictionary<IntPtr, float>(64);
        internal static readonly Dictionary<IntPtr, float> _nextVehicleSupportSteerTime = new Dictionary<IntPtr, float>(64);
        internal static readonly Dictionary<IntPtr, float> _nextPlaneOrbitSteerTime = new Dictionary<IntPtr, float>(32);
        internal static readonly Dictionary<IntPtr, float> _nextCombinedArmsAssistCountTime = new Dictionary<IntPtr, float>(64);
        internal static readonly HashSet<IntPtr> _activeKamikazePlanes = new HashSet<IntPtr>();
        internal static readonly Dictionary<IntPtr, bool> _kamikazeDecisionByPlane = new Dictionary<IntPtr, bool>(16);
        internal static readonly Dictionary<IntPtr, float> _nextKamikazeTargetSearchTime = new Dictionary<IntPtr, float>(16);
        internal static readonly Dictionary<IntPtr, Squad> _lastKnownVehicleCrewSquad = new Dictionary<IntPtr, Squad>(32);
        private static float _nextGroundedCrewRetreatCheckTime;
        public static int CombinedArmsPriorityAssistsCount;
        public static int ArmoredDispersionAdjustmentsCount;
        public static int KamikazeDivesTriggeredCount;
        public static int RemoteClientRoadkillsAssistedCount;
        public static int TankRockUnsticksCount;
        public static int SmartAmmoSwitchesCount;

        private static readonly HashSet<IntPtr> _hotStartedPlayerPlanes = new HashSet<IntPtr>();
        internal static readonly Dictionary<IntPtr, TurretAI> _turretAiByTurretPtr = new Dictionary<IntPtr, TurretAI>(64);
        internal static readonly Dictionary<IntPtr, string> _lastKnownVehicleFaction = new Dictionary<IntPtr, string>(64);
        internal static readonly HashSet<IntPtr> _creditedDestroyedVehicles = new HashSet<IntPtr>();
        internal static readonly HashSet<IntPtr> _vehiclesCrewRetreatedFrom = new HashSet<IntPtr>();
        private static readonly Dictionary<IntPtr, bool> _lastTurretTargetWasVehicle = new Dictionary<IntPtr, bool>(64);

        private struct ActiveRadioSupportInfo
        {
            public string Faction;
            public Vector3 Position;
            public RadioRequest RequestType;
            public Squad SourceSquad;
        }

        private struct KnownEnemyCluster
        {
            public string Faction;
            public Vector3 Position;
            public float BasePriority;
            public bool IsMarkedOrPinPoint;
            public bool IsHalfMarked;
            public bool NearRadioRequest;
            public bool IsEngagingFriendlies;
            public Spottable VehicleTarget;
            public Spottable PrimarySoldier;
            public Spottable SecondarySoldier;
        }

        private struct AirborneAircraftInfo
        {
            public string Faction;
            public VehiclePlane Plane;
            public Spottable Spottable;
            public Vector3 Position;
            public bool IsBomber;
        }

        private struct ActiveGroundVehicleInfo
        {
            public IntPtr VehPtr;
            public Vehicle Veh;
            public string Faction;
            public Vector3 Position;
            public Vector3 Forward;
            public Vector3 TargetDest;
            public float HalfWidth;
            public float HalfLength;
            public float SpeedMs;
            public bool IsTank;
            public bool IsPlayerInsideOrDriving;
            public bool IsStoppedOrShooting;
        }

        private static float _nextCombinedArmsIntelRefreshTime;
        private static IntPtr _cachedRepairStationsPhasePtr;
        private static int _emplacementScanCursor;
        private static readonly List<ActiveRadioSupportInfo> _activeRadioRequests = new List<ActiveRadioSupportInfo>(16);
        private static readonly List<KnownEnemyCluster> _knownEnemyClusters = new List<KnownEnemyCluster>(32);
        private static readonly List<AirborneAircraftInfo> _airborneAircraft = new List<AirborneAircraftInfo>(16);
        private static readonly List<ActiveGroundVehicleInfo> _activeGroundVehicles = new List<ActiveGroundVehicleInfo>(24);
        private static readonly HashSet<IntPtr> _seenGroundVehiclePtrs = new HashSet<IntPtr>(24);
        internal static readonly Dictionary<IntPtr, Vector3> _claimedVehicleDestinations = new Dictionary<IntPtr, Vector3>(24);
        internal static readonly Dictionary<IntPtr, float> _nextArmorDispersionCountTime = new Dictionary<IntPtr, float>(32);
        private static readonly List<IntPtr> _reusableExpiredEmplacementBots = new List<IntPtr>(16);

        public static void ClearCombinedArmsCaches()
        {
            _activeRadioRequests.Clear();
            _knownEnemyClusters.Clear();
            _airborneAircraft.Clear();
            _activeGroundVehicles.Clear();
            _seenGroundVehiclePtrs.Clear();
            _claimedVehicleDestinations.Clear();
            _nextArmorDispersionCountTime.Clear();
            _reusableExpiredEmplacementBots.Clear();
            _activeRepairStations.Clear();
            _vehiclesSeekingRepairStation.Clear();
            _vehiclesAnsweringTankSupport.Clear();
            _squadOrderedToVehicle.Clear();
            _unreachableEmplacementUntil.Clear();
            _cachedRepairStationsPhasePtr = IntPtr.Zero;
            _currentDirectorBattleData = null;
            _emplacementScanCursor = 0;
            _activeKamikazePlanes.Clear();
            _kamikazeDecisionByPlane.Clear();
            _nextKamikazeTargetSearchTime.Clear();
            _lastKnownVehicleCrewSquad.Clear();
            _nextGroundedCrewRetreatCheckTime = 0f;
            _hotStartedPlayerPlanes.Clear();
            _turretAiByTurretPtr.Clear();
            _lastKnownVehicleFaction.Clear();
            _creditedDestroyedVehicles.Clear();
            _vehiclesCrewRetreatedFrom.Clear();
            _lastTurretTargetWasVehicle.Clear();
            _nextCombinedArmsIntelRefreshTime = 0f;
            _nextMacroDirectorTime = 0f;
            _macroDirectorStarvedSince = 0f;
            _garrisonAssignedObjectiveBySquad.Clear();
            _assignedEmplacementUntil.Clear();
            _assignedEmplacementVehicle.Clear();
            _nextApcCheckTime.Clear();
            _nextApcDismountCooldown.Clear();
            _nextTankStandoffCountTime.Clear();
            _cachedVehicleExtents.Clear();
            _nextVehiclePedestrianScanTime.Clear();
            _vehicleYieldUntilTime.Clear();
            _vehicleContinuousYieldStart.Clear();
            _nextFriendlyYieldCountTime.Clear();
            _nextVehicleSupportSteerTime.Clear();
            _nextPlaneOrbitSteerTime.Clear();
            _nextCombinedArmsAssistCountTime.Clear();
            _squadTaskCommittedUntil.Clear();
            _nextSquadAttritionPivotTime.Clear();
            _pendingMacroSquadOrders.Clear();
            _objectiveAttrition.Clear();
            _squadObjectiveLossTrackers.Clear();
            _nextTurretFFCheckTime.Clear();
            _turretFFBlockedUntil.Clear();
            _pendingTurretIntercom.Clear();
            _turretSightReaction.Clear();
            _planeAttackReaction.Clear();
            _cachedVehicleSolidColliders.Clear();
            _ignoredFriendlyVehicleCollisions.Clear();
            _ignoredFriendlyCollisionPairs.Clear();
            _nextIgnoredVehicleCollisionSweepTime = 0f;
            _pendingPlayerHqOrder = default;
            _playerBalkedTaskUntil.Clear();
            _playerBalkedFlankUntil = 0f;
            _playerBalkedFlankPos = Vector3.zero;
            _spawnRescueScatterCounter = 0;
            _minimapMarkerTextByImgPtr.Clear();
            _lastUnitMarkerTextComp = null;
            _lastUsedUnitMarkerIndex = -1;
            _insideMiniMapUpdate = false;
        }

        private struct PendingTurretIntercomHandoff
        {
            public Spottable Target;
            public float ReadyTime;
            public float AngleAdj;
        }

        internal struct TurretSightReactionState
        {
            public IntPtr LastTargetPtr;
            public float LastSeenTime;
        }

        internal struct PlaneAttackReactionState
        {
            public IntPtr LastTargetPtr;
            public float HoldFireUntil;
            public AIPlane.PlaneAIState LastPlaneState;
            public bool HasObservedState;
            public float LastAdjustedNextUpdateCheck;
            public float NextDiveGroundCheckTime;
        }

        private struct IgnoredFriendlyVehicleCollisionEntry
        {
            public CharacterController Controller;
            public Collider VehCollider;
            public long PairKey;
            public float RestoreTime;
        }

        private static readonly Dictionary<IntPtr, PendingTurretIntercomHandoff> _pendingTurretIntercom =
            new Dictionary<IntPtr, PendingTurretIntercomHandoff>(32);
        internal static readonly Dictionary<IntPtr, TurretSightReactionState> _turretSightReaction =
            new Dictionary<IntPtr, TurretSightReactionState>(64);
        internal static readonly Dictionary<IntPtr, PlaneAttackReactionState> _planeAttackReaction =
            new Dictionary<IntPtr, PlaneAttackReactionState>(32);
        private static readonly Dictionary<IntPtr, Collider[]> _cachedVehicleSolidColliders =
            new Dictionary<IntPtr, Collider[]>(32);
        private static readonly List<IgnoredFriendlyVehicleCollisionEntry> _ignoredFriendlyVehicleCollisions =
            new List<IgnoredFriendlyVehicleCollisionEntry>(64);
        private static readonly HashSet<long> _ignoredFriendlyCollisionPairs = new HashSet<long>();
        private static float _nextIgnoredVehicleCollisionSweepTime;
        public static int TankIntercomHandoffsCount;

        private static bool TryRecordCombinedArmsAssist(IntPtr unitPtr, float now)
        {
            if (!_nextCombinedArmsAssistCountTime.TryGetValue(unitPtr, out float nextCount) || now >= nextCount)
            {
                _nextCombinedArmsAssistCountTime[unitPtr] = now + 6.0f;
                CombinedArmsPriorityAssistsCount++;
                return VisionConfig.DebugLogging.Value;
            }
            return false;
        }

        private static string GetVehicleCrewFaction(Vehicle veh, Turret turret = null)
        {
            if (ReferenceEquals(veh, null) || veh == null)
                return null;

            string resolved = null;

            if (!ReferenceEquals(turret, null) && turret != null)
            {
                Soldier gunner = turret.GetSittedUnit(veh);
                if (gunner != null)
                {
                    string gf = AiCore.GetCachedFaction(gunner);
                    if (!string.IsNullOrEmpty(gf) && gf != "Civilian")
                        resolved = gf;
                }
            }

            // Check seated crew members before falling back to veh.GetVehicleFaction(), because runway-spawned
            // planes and newly boarded vehicles can temporarily report "Civilian" in GetVehicleFaction()!
            if (resolved == null)
            {
                var seats = veh.seats;
                if (seats != null)
                {
                    for (int i = 0; i < seats.Length; i++)
                    {
                        Seats s = seats[i];
                        if (s != null && s.unitSet != null)
                        {
                            string sf = AiCore.GetCachedFaction(s.unitSet);
                            if (!string.IsNullOrEmpty(sf) && sf != "Civilian")
                            {
                                resolved = sf;
                                break;
                            }
                        }
                    }
                }
            }

            if (resolved == null)
            {
                string vf = AiCore.InternFaction(veh.GetVehicleFaction());
                if (!string.IsNullOrEmpty(vf) && vf != "Civilian")
                    resolved = vf;
            }

            if (resolved != null && veh.Pointer != IntPtr.Zero)
            {
                _lastKnownVehicleFaction[veh.Pointer] = resolved;
            }

            return resolved;
        }

        private static void TryRegisterAirborneAircraft(VehiclePlane vp, string pilotFaction)
        {
            if (vp == null || vp.Pointer == IntPtr.Zero || vp.transform == null || !vp.CanFight())
                return;

            // Fix vanilla VehiclePlane.FixedUpdate (0x18085011B) skipping isGrounded updates for remote Co-op client pilots!
            if (VisionConfig.EnableCoopAirborneSpawnFix.Value && !vp.OwnDriverOrVehicle())
            {
                vp.RefreshGrounded();
            }

            if (vp.isGrounded || _airborneAircraft.Count >= 16)
                return;

            string faction = (!string.IsNullOrEmpty(pilotFaction) && pilotFaction != "Civilian")
                ? pilotFaction
                : GetVehicleCrewFaction(vp);
            if (string.IsNullOrEmpty(faction))
                return;

            for (int a = 0; a < _airborneAircraft.Count; a++)
            {
                if (_airborneAircraft[a].Plane != null && _airborneAircraft[a].Plane.Pointer == vp.Pointer)
                    return;
            }

            _airborneAircraft.Add(new AirborneAircraftInfo
            {
                Faction = faction,
                Plane = vp,
                Spottable = vp.Cast<Spottable>(),
                Position = vp.transform.position,
                IsBomber = vp.planeType == PlaneType.Bomber
            });
        }

        private static void RefreshCombinedArmsIntelIfNeeded()
        {
            float now = Time.time;
            if (now < _nextCombinedArmsIntelRefreshTime && _nextCombinedArmsIntelRefreshTime - now <= 5.0f)
                return;

            // Defer non-urgent intel rebuild by 1 frame if SquadLeaderRoutine / WorldSnapshot / MacroDirector already ran this frame
            if (AiCore.IsHeavyWorkFrame() && _nextCombinedArmsIntelRefreshTime > 0f && now < _nextCombinedArmsIntelRefreshTime + 0.60f)
                return;

            AiCore.NotifyDirectorWorkThisFrame();
            long profIntelStart = HitchProfiler.Begin();
            try
            {
            _nextCombinedArmsIntelRefreshTime = now + 1.0f;
            _activeRadioRequests.Clear();
            _knownEnemyClusters.Clear();
            _airborneAircraft.Clear();
            _activeGroundVehicles.Clear();
            _seenGroundVehiclePtrs.Clear();

            // Cache static RepairStations ONCE per BattlePhase (or once per BattleManager on sp_ME_ streaming maps)
            // instead of calling FindObjectsOfType<RepairStation>() every 15s mid-combat.
            BattleManager bm = BattleManager.instance;
            BattlePhase curPhase = AiCore.TryGetCurrentBattlePhase(bm);
            IntPtr curPhasePtr = curPhase != null
                ? curPhase.Pointer
                : ((bm != null && bm.Pointer != IntPtr.Zero && !BattleManager.isBattleEnded && !bm.isPreview) ? bm.Pointer : IntPtr.Zero);
            if (curPhasePtr != _cachedRepairStationsPhasePtr)
            {
                _cachedRepairStationsPhasePtr = curPhasePtr;
                _activeRepairStations.Clear();
                if (curPhasePtr != IntPtr.Zero)
                {
                    try
                    {
                        var stations = UnityEngine.Object.FindObjectsOfType<RepairStation>();
                        if (stations != null)
                        {
                            for (int i = 0; i < stations.Length; i++)
                            {
                                RepairStation st = stations[i];
                                if (st != null && st.Pointer != IntPtr.Zero && st.gameObject != null && st.gameObject.activeInHierarchy && st.transform != null)
                                {
                                    _activeRepairStations.Add(st);
                                }
                            }
                        }
                    }
                    catch { }
                }
            }

            AiCore.EnsureWorldSnapshot();
            int sqCount = AiCore.WorldSquadCount;
            int solCount = AiCore.WorldSoldierCount;
            if (sqCount == 0 && solCount == 0)
                return;

            var squads = AiCore.WorldSquads;
            var soldiers = AiCore.WorldSoldiers;

            // Pass 0: Scan all mounted soldiers in WorldSoldiers for airborne aircraft AND active movable ground vehicles
            for (int i = 0; i < solCount; i++)
            {
                ref AiCore.SoldierSnapshot sSnap = ref soldiers[i];
                if (!sSnap.IsOnVehicle || sSnap.Ref == null)
                    continue;

                Vehicle v = sSnap.Ref.GetCurrentVehicle();
                if (v == null || v.Pointer == IntPtr.Zero)
                    continue;

                if (v.IsAirVehicle())
                {
                    VehiclePlane vp = v.TryCast<VehiclePlane>();
                    if (vp != null)
                        TryRegisterAirborneAircraft(vp, sSnap.Faction);
                }
                else if (!v.IsDisabled() &&
                         !v.IsCoverDestroyed() &&
                         v.transform != null &&
                         _seenGroundVehiclePtrs.Add(v.Pointer) &&
                         _activeGroundVehicles.Count < 24)
                {
                    MovableVehicle mv = v.TryCast<MovableVehicle>();
                    if (mv != null)
                    {
                        string vf = (!string.IsNullOrEmpty(sSnap.Faction) && sSnap.Faction != "Civilian")
                            ? sSnap.Faction
                            : GetVehicleCrewFaction(v);
                        if (!string.IsNullOrEmpty(vf))
                        {
                            Vector3 vPos = v.transform.position;
                            Vector3 vFwd = v.transform.forward;
                            vFwd.y = 0f;
                            vFwd = vFwd.sqrMagnitude > 0.001f ? vFwd.normalized : Vector3.forward;
                            Vector2 ext = GetCachedVehicleHalfExtents(v);
                            float spd = Mathf.Abs(mv.GetCurrentSpeed());
                            Vector3 tDest = _claimedVehicleDestinations.TryGetValue(v.Pointer, out Vector3 cd) ? cd : vPos;

                            _activeGroundVehicles.Add(new ActiveGroundVehicleInfo
                            {
                                VehPtr = v.Pointer,
                                Veh = v,
                                Faction = vf,
                                Position = vPos,
                                Forward = vFwd,
                                TargetDest = tDest,
                                HalfWidth = ext.x,
                                HalfLength = ext.y,
                                SpeedMs = spd,
                                IsTank = v.TryCast<VehicleTank>() != null,
                                IsPlayerInsideOrDriving = v.PlayerIsInside() || v.PlayerIsDriving(),
                                IsStoppedOrShooting = spd < 1.15f
                            });
                        }
                    }
                }
            }

            AiCore.PruneDict(_claimedVehicleDestinations, _seenGroundVehiclePtrs);
            AiCore.PruneDict(_nextArmorDispersionCountTime, _seenGroundVehiclePtrs);

            // Pass 1: Collect active confirmed offensive radio support requests
            for (int s = 0; s < sqCount; s++)
            {
                ref AiCore.SquadSnapshot sqSnap = ref squads[s];
                if (!sqSnap.LeaderIsAlive || string.IsNullOrEmpty(sqSnap.Faction))
                    continue;

                Squad sq = sqSnap.Ref;
                if (sq != null && sq.RadioRequestActiveAndConfirmed())
                {
                    RadioRequest reqType = sq.radioRequest;
                    if (reqType == RadioRequest.tankSupport ||
                        sq.RadioRequestIsExplosiveArtillery ||
                        reqType == RadioRequest.artillerySmoke)
                    {
                        _activeRadioRequests.Add(new ActiveRadioSupportInfo
                        {
                            Faction = sqSnap.Faction,
                            Position = sq.radioRequestPosition,
                            RequestType = reqType,
                            SourceSquad = sq
                        });
                        if (_activeRadioRequests.Count >= 16)
                            break;
                    }
                }
            }

            // Pass 2: Collect KNOWN enemy squads & combat vehicles (Marked, Radio-Requested, or Actively Engaging Friendlies)
            for (int s = 0; s < sqCount; s++)
            {
                ref AiCore.SquadSnapshot sqSnap = ref squads[s];
                Squad sq = sqSnap.Ref;
                Soldier refSoldier = sqSnap.RefSoldier;
                if (sq == null || sqSnap.UnitCount == 0 || refSoldier == null || string.IsNullOrEmpty(sqSnap.Faction))
                    continue;

                Vehicle mountedVeh = refSoldier.IsOnVehicle() ? refSoldier.GetCurrentVehicle() : null;
                if (mountedVeh != null && mountedVeh.IsAirVehicle())
                {
                    VehiclePlane vp = mountedVeh.TryCast<VehiclePlane>();
                    if (vp != null)
                        TryRegisterAirborneAircraft(vp, sqSnap.Faction);
                    continue;
                }

                string sqFaction = sqSnap.Faction;
                Vector3 sqPos = sqSnap.RefPos;

                bool isPinPoint = sq.isMarkedPinPoint;
                bool isMarked = sq.isMarked || isPinPoint;
                bool isHalfMarked = sq.isHalfMarked;
                bool isAware = sqSnap.IsSpottedOrAware;

                bool nearRadio = false;
                RadioRequest matchedRadioType = RadioRequest.waiting;
                for (int r = 0; r < _activeRadioRequests.Count; r++)
                {
                    ActiveRadioSupportInfo req = _activeRadioRequests[r];
                    if (req.RequestType == RadioRequest.artillerySmoke || AiCore.AreSameSideFactions(req.Faction, sqFaction))
                        continue;

                    if ((req.Position - sqPos).sqrMagnitude <= 85.0f * 85.0f)
                    {
                        nearRadio = true;
                        matchedRadioType = req.RequestType;
                        break;
                    }
                }

                Soldier primaryFoot = null;
                Soldier secondaryFoot = null;
                bool anyUnitSpottedOrFiring = false;
                int aliveCount = sqSnap.UnitCount;
                int end = sqSnap.UnitStartIndex + sqSnap.UnitCount;

                for (int i = sqSnap.UnitStartIndex; i < end; i++)
                {
                    ref AiCore.SoldierSnapshot uSnap = ref soldiers[i];
                    Soldier u = uSnap.Ref;
                    if (u == null || uSnap.HasSurrendered || uSnap.IsInjured)
                        continue;

                    if (uSnap.IsSpotted || VisionPatches.HasFiredRecently(u, 3.5f))
                        anyUnitSpottedOrFiring = true;

                    if (!uSnap.IsOnVehicle)
                    {
                        if (primaryFoot == null)
                        {
                            primaryFoot = u;
                        }
                        else if (secondaryFoot == null)
                        {
                            secondaryFoot = u;
                        }
                        else if (u.hasMg || u.isAtUnit || u.m_pose == SoldierPose.Idle)
                        {
                            primaryFoot = u;
                        }
                    }
                }

                if (!isMarked && !isHalfMarked && !isAware && !nearRadio && !anyUnitSpottedOrFiring)
                    continue;

                bool hasEnemyGroundVehicle = mountedVeh != null && mountedVeh.CanFight() && !mountedVeh.IsAirVehicle();

                // Tier 3 Radioman Intel Gate: unmarked enemy contacts only reach the Team Combined-Arms Net
                // if a friendly squad/vehicle/aircraft within range is actually connected to the Radio Net!
                if (!isMarked && !isHalfMarked && !nearRadio && !CanBroadcastEnemyContactToTeamNet(sq, sqPos, sqFaction, hasEnemyGroundVehicle))
                    continue;

                float priority = Mathf.Min(aliveCount * 2.5f, 15.0f);
                if (isPinPoint)
                    priority += 55.0f;
                else if (isMarked)
                    priority += 45.0f;
                else if (isHalfMarked)
                    priority += 28.0f;

                if (nearRadio)
                {
                    priority += 50.0f;
                    if (matchedRadioType == RadioRequest.tankSupport || matchedRadioType == RadioRequest.artilleryAPHE)
                        priority += 15.0f;
                }

                if (isAware)
                    priority += 24.0f;
                if (anyUnitSpottedOrFiring)
                    priority += 10.0f;

                Spottable vehSpottable = null;
                if (mountedVeh != null)
                {
                    // Never broadcast an airborne aircraft's pilot Soldier (`refSoldier`) as a ground cluster target!
                    // That caused ground MGs and turrets to assign the pilot's Soldier body directly as CurrentVisibleTarget.
                    if (mountedVeh.IsAirVehicle())
                    {
                        if (primaryFoot == null)
                            continue;
                    }
                    else if (mountedVeh.CanFight())
                    {
                        vehSpottable = mountedVeh.Cast<Spottable>();
                        priority += mountedVeh.GetMainTurret(false) == null ? 12.0f : 32.0f;
                    }
                }

                Soldier firstSoldier = primaryFoot != null ? primaryFoot : (mountedVeh == null ? refSoldier : null);
                Spottable firstSpottable = firstSoldier != null ? firstSoldier.Cast<Spottable>() : null;
                Spottable secondSpottable = secondaryFoot != null ? secondaryFoot.Cast<Spottable>() : null;

                _knownEnemyClusters.Add(new KnownEnemyCluster
                {
                    Faction = sqFaction,
                    Position = firstSoldier != null && firstSoldier.transform != null ? firstSoldier.transform.position : sqPos,
                    BasePriority = priority,
                    IsMarkedOrPinPoint = isMarked,
                    IsHalfMarked = isHalfMarked,
                    NearRadioRequest = nearRadio,
                    IsEngagingFriendlies = isAware || anyUnitSpottedOrFiring,
                    VehicleTarget = vehSpottable,
                    PrimarySoldier = firstSpottable,
                    SecondarySoldier = secondSpottable
                });

                if (_knownEnemyClusters.Count >= 32)
                    break;
            }
            }
            finally
            {
                HitchProfiler.End(HitchBucket.CombinedArmsIntel, profIntelStart);
            }
        }

        private static float EvaluateExistingTargetPriority(Spottable target, string myFaction, Vector3 originPos, bool hasHeavyGun)
        {
            if (!AiCore.IsValidAliveSpottable(target))
                return -999f;

            Vector3 targetPos = target.GetCenterOfUnit();
            float dist = Vector3.Distance(originPos, targetPos);
            float score = -(dist * 0.12f);

            if (target.IsWheeledVehicleOrTank())
            {
                score += hasHeavyGun ? 52.0f : 24.0f;
                Vehicle v = target.TryCast<Vehicle>();
                if (v != null)
                {
                    Squad vSq = v.GetSquadInside();
                    if (vSq != null)
                    {
                        if (vSq.isMarkedPinPoint) score += 55.0f;
                        else if (vSq.isMarked) score += 45.0f;
                        else if (vSq.isHalfMarked) score += 28.0f;
                        if (vSq.IsEnemyAwareOfThisSquad) score += 20.0f;
                    }
                }
            }
            else
            {
                score += 10.0f;
                Soldier s = target.TryCast<Soldier>();
                if (s != null && s.joinedSquad != null)
                {
                    Squad sq = s.joinedSquad;
                    if (sq.isMarkedPinPoint) score += 55.0f;
                    else if (sq.isMarked) score += 45.0f;
                    else if (sq.isHalfMarked) score += 28.0f;
                    if (sq.IsEnemyAwareOfThisSquad) score += 24.0f;
                }
            }

            for (int r = 0; r < _activeRadioRequests.Count; r++)
            {
                ActiveRadioSupportInfo req = _activeRadioRequests[r];
                if (req.RequestType != RadioRequest.artillerySmoke &&
                    AiCore.AreSameSideFactions(req.Faction, myFaction) &&
                    (req.Position - targetPos).sqrMagnitude <= 85.0f * 85.0f)
                {
                    score += 50.0f;
                    break;
                }
            }

            return score;
        }

        private static bool CanTankShellIndoorTargetFacade(Turret turret, Vehicle veh, Spottable target)
        {
            if (turret == null || veh == null || veh.transform == null || !AiCore.IsValidAliveSpottable(target))
                return false;

            if (target.IsWheeledVehicleOrTank() || target.IsAirVehicle())
                return false;

            if (turret.GetCaliber() < 37.0f)
                return false;

            turret.CheckBullets(out bool hasHe, out _, out _, out _);
            if (!hasHe)
                return false;

            Vector3 targetPos = target.GetCenterOfUnit();
            if (!AiCore.HasLowCeilingOverhead(targetPos, 3.8f))
                return false;

            Vector3 firePos = turret.GetFirePos();
            Vector3 toTarget = targetPos - firePos;
            float dist = toTarget.magnitude;
            if (dist < 16.0f || dist > 320.0f)
                return false;

            return !AiCore.RaycastEnvironment(firePos, toTarget / dist, dist - 5.5f, veh.transform.root, out _);
        }

        public static void TryPrioritizeKnownTargetForTurret(TurretAI turretAi, Vehicle veh)
        {
            if (turretAi == null || turretAi.turret == null || veh == null || veh.Pointer == IntPtr.Zero)
                return;

            if (!veh.CanFight() || veh.IsAirVehicle() || veh.PlayerIsInside())
                return;

            Turret turret = turretAi.turret;
            AiCore.TurretFastTraits traits = AiCore.GetCachedTurretTraits(turret);
            if (traits.IsCommanderTurret)
                return;

            Soldier gunner = turret.GetSittedUnit(veh);
            if (gunner != null && gunner.IsPlayer())
                return;

            string myFaction = GetVehicleCrewFaction(veh, turret);
            if (string.IsNullOrEmpty(myFaction))
                return;

            bool checkFF = VisionConfig.FriendlyFireConeDegrees.Value > 0.1f;
            bool isIndirectOrArty = turret.IsUpIndirectFire() || turret.IsArtillery();
            IntPtr gunnerPtr = gunner != null ? gunner.Pointer : IntPtr.Zero;
            IntPtr turretPtr = turret.Pointer;
            float now = Time.time;

            Vector3 firePos = turret.GetFirePos();
            Spottable curTarget = turretAi.CurrentVisibleTarget;

            // 1. Sanitize danger-close indirect/artillery targets & radio support requests before vanilla fires into friendlies!
            if (checkFF && isIndirectOrArty)
            {
                if (curTarget != null && AiCore.IsValidAliveSpottable(curTarget) && !curTarget.IsAirVehicle())
                {
                    if (AiCore.IsIndirectFireImpactDangerClose(myFaction, curTarget.GetCenterOfUnit(), gunnerPtr, 30.0f, 36.0f))
                    {
                        turretAi.CurrentVisibleTarget = null;
                        curTarget = null;
                        FriendlyFirePrevented++;
                    }
                }

                bool isSmokeReq = turretAi.supportedSquad != null &&
                                  turretAi.supportedSquad.radioRequest == RadioRequest.artillerySmoke;
                if (turretAi.HasRadioSupportRequest &&
                    !isSmokeReq &&
                    AiCore.IsIndirectFireImpactDangerClose(myFaction, turretAi.RadioRequestTargetPosition, gunnerPtr, 30.0f, 36.0f))
                {
                    turretAi.supportedSquad = null;
                    FriendlyFirePrevented++;
                }
            }

            if (curTarget != null)
            {
                if (!AiCore.IsValidAliveSpottable(curTarget))
                {
                    turretAi.CurrentVisibleTarget = null;
                    curTarget = null;
                }
                else
                {
                    if (curTarget.IsAirVehicle())
                        return;

                    float curDist = Vector3.Distance(firePos, curTarget.GetCenterOfUnit());
                    // Never override point-blank direct-fire self-defense
                    if (!isIndirectOrArty && curDist < 32.0f)
                    {
                        _pendingTurretIntercom.Remove(turretPtr);
                        return;
                    }

                    // Never distract from an active close/mid-range duel against an armed enemy vehicle
                    if (curTarget.IsWheeledVehicleOrTank() && curDist < 130.0f)
                    {
                        Vehicle curVeh = curTarget.TryCast<Vehicle>();
                        if (curVeh != null && curVeh.GetMainTurret(false) != null)
                        {
                            _pendingTurretIntercom.Remove(turretPtr);
                            return;
                        }
                    }
                }
            }

            AiCore.VehicleFastHandles vh = traits.VehHandles ?? AiCore.GetOrCreateVehicleHandles(veh);
            bool isEnclosedArmorGun = vh != null &&
                                      vh.IsWheeledOrTank &&
                                      !traits.IsExposedCrewSeat &&
                                      !traits.IsAA &&
                                      !isIndirectOrArty;

            bool hasInternalSpotter = true;
            bool hasInfantryEscort = false;
            if (isEnclosedArmorGun)
            {
                AiCore.GetCachedVehicleSpotterStatus(vh, myFaction, now, out hasInternalSpotter, out hasInfantryEscort);
                if (!hasInternalSpotter && !hasInfantryEscort)
                {
                    // Lone buttoned-up tank with dead/missing commander and no nearby friendly infantry escort:
                    // cancel any pending intercom handoff; the gunner can only see inside their narrow gun-sight arc!
                    _pendingTurretIntercom.Remove(turretPtr);
                }
                else if (_pendingTurretIntercom.TryGetValue(turretPtr, out PendingTurretIntercomHandoff pending))
                {
                    if (!AiCore.IsValidAliveSpottable(pending.Target))
                    {
                        _pendingTurretIntercom.Remove(turretPtr);
                    }
                    else if (now < pending.ReadyTime)
                    {
                        // Commander / infantry escort is actively calling out the flank bearing over intercom/hull phone
                        return;
                    }
                    else
                    {
                        _pendingTurretIntercom.Remove(turretPtr);
                        Vector3 pPos = pending.Target.GetCenterOfUnit();
                        Vector3 pTo = pPos - firePos;
                        float pDist = pTo.magnitude;
                        if (pDist >= 4.0f && turret.CanShootInDirection(pTo / pDist, 1.0f))
                        {
                            bool canEngageHandedOff;
                            VisionPatches.AllowCommanderIntercomFovBypass = true;
                            try
                            {
                                canEngageHandedOff = turretAi.CanSee(pending.Target);
                            }
                            finally
                            {
                                VisionPatches.AllowCommanderIntercomFovBypass = false;
                            }

                            if (!canEngageHandedOff && CanTankShellIndoorTargetFacade(turret, veh, pending.Target))
                                canEngageHandedOff = true;

                            if (canEngageHandedOff)
                            {
                                turretAi.CurrentVisibleTarget = pending.Target;
                                turretAi.supportedSquad = null;
                                turretAi.targetAngleAdjust = pending.AngleAdj;
                                turretAi.targetInWeaponRange = true;
                                turret.SelectBestAmmo(pending.Target);
                                TankIntercomHandoffsCount++;
                                if (TryRecordCombinedArmsAssist(veh.Pointer, now))
                                {
                                    AiCore.LogDebug(
                                        "TankIntercom",
                                        $"{myFaction} {(hasInternalSpotter ? "tank commander" : "infantry escort")} handed off flank target ({pDist:F0}m) to enclosed gunner.");
                                }
                                return;
                            }
                        }
                    }
                }
            }

            bool hasCommanderCupolaSpot = vh != null &&
                                          vh.CommanderSpottedTarget != null &&
                                          (now - vh.CommanderSpotTime) <= 4.2f &&
                                          AiCore.IsValidAliveSpottable(vh.CommanderSpottedTarget);

            if (!VisionConfig.EnableCombinedArmsPrioritySupport.Value && !hasCommanderCupolaSpot)
                return;

            if (VisionConfig.EnableCombinedArmsPrioritySupport.Value)
                RefreshCombinedArmsIntelIfNeeded();

            turret.CheckBullets(out bool hasHe, out bool hasAp, out bool hasFire, out _);
            float caliber = turret.GetCaliber();
            bool hasHeavyGun = hasHe || hasAp || caliber >= 20.0f;
            bool canDamageLightVehicles = hasHeavyGun || hasFire || caliber >= 12.0f;

            float curScore = curTarget != null
                ? EvaluateExistingTargetPriority(curTarget, myFaction, firePos, hasHeavyGun)
                : -999f;

            float bulletSpeed = turret.GetBulletSpeed();
            float bulletGrav = turret.GetBulletGravity();
            Vector3 curFireDir = turret.GetFireDir();
            bool canBypassGunSlitViaSpotter = isEnclosedArmorGun && (hasInternalSpotter || hasInfantryEscort);
            bool loneButtonedUpArmor = isEnclosedArmorGun && !hasInternalSpotter && !hasInfantryEscort;

            Spottable bestCandidate = null;
            float bestScore = curScore + 18.0f;
            float bestAngleAdj = 0f;

            // 0. Intra-vehicle Tank Commander Cupola / Periscope direct spot (highest priority intra-crew handoff)
            if (hasCommanderCupolaSpot && (!loneButtonedUpArmor))
            {
                Spottable cmdCand = vh.CommanderSpottedTarget;
                bool cmdIsVeh = cmdCand.IsWheeledVehicleOrTank();
                if (!cmdCand.IsAirVehicle() && (!cmdIsVeh || canDamageLightVehicles))
                {
                    Vector3 cmdPos = cmdCand.GetCenterOfUnit();
                    Vector3 toCmd = cmdPos - firePos;
                    float cmdDist = toCmd.magnitude;
                    float cmdMaxDist = cmdIsVeh ? 550.0f : 320.0f;
                    if (cmdDist >= 6.0f && cmdDist <= cmdMaxDist && turret.CanShootInDirection(toCmd / cmdDist, 1.0f))
                    {
                        bool cmdSafe = true;
                        if (checkFF && hasHe && caliber >= 50.0f &&
                            AiCore.IsIndirectFireImpactDangerClose(myFaction, cmdPos, gunnerPtr, 20.0f, 26.0f))
                        {
                            cmdSafe = false;
                        }

                        if (cmdSafe)
                        {
                            float cmdAngleAdj = Corvostudio.Weapons.BulletInstance.CalculateTrajectory(cmdDist, bulletSpeed, bulletGrav, out bool cmdReach);
                            if (cmdReach)
                            {
                                bool cmdGunCanSee;
                                VisionPatches.AllowCommanderIntercomFovBypass = true;
                                try
                                {
                                    cmdGunCanSee = turretAi.CanSee(cmdCand);
                                }
                                finally
                                {
                                    VisionPatches.AllowCommanderIntercomFovBypass = false;
                                }

                                if (cmdGunCanSee || CanTankShellIndoorTargetFacade(turret, veh, cmdCand))
                                {
                                    float cmdScore = 95.0f + (cmdIsVeh && hasHeavyGun ? 40.0f : 0f) - (cmdDist * 0.10f);
                                    if (cmdScore > bestScore)
                                    {
                                        bestScore = cmdScore;
                                        bestCandidate = cmdCand;
                                        bestAngleAdj = cmdAngleAdj;
                                    }
                                }
                            }
                        }
                    }
                }
            }

            int clusterCount = VisionConfig.EnableCombinedArmsPrioritySupport.Value ? _knownEnemyClusters.Count : 0;
            for (int i = 0; i < clusterCount; i++)
            {
                KnownEnemyCluster cluster = _knownEnemyClusters[i];
                if (AiCore.AreSameSideFactions(cluster.Faction, myFaction))
                    continue;

                for (int pass = 0; pass < 3; pass++)
                {
                    Spottable cand = null;
                    bool isVehCand = false;
                    if (pass == 0)
                    {
                        if (!canDamageLightVehicles || cluster.VehicleTarget == null)
                            continue;
                        cand = cluster.VehicleTarget;
                        isVehCand = true;
                    }
                    else if (pass == 1)
                    {
                        cand = cluster.PrimarySoldier;
                    }
                    else
                    {
                        cand = cluster.SecondarySoldier;
                    }

                    if (!AiCore.IsValidAliveSpottable(cand))
                        continue;

                    Vector3 candPos = cand.GetCenterOfUnit();
                    Vector3 toCand = candPos - firePos;
                    float dist = toCand.magnitude;
                    float maxDist = isVehCand ? 550.0f : (isIndirectOrArty ? 650.0f : 320.0f);
                    if (dist < (isIndirectOrArty ? 34.0f : 8.0f) || dist > maxDist)
                        continue;

                    Vector3 dirToCand = toCand / dist;

                    // Lone buttoned-up tank with no alive commander and no friendly infantry escort within 18.5m:
                    // cannot magically snap its turret onto flank/rear team-net targets outside the gunner's forward sight!
                    if (loneButtonedUpArmor && Vector3.Angle(curFireDir, dirToCand) > 32.0f)
                        continue;

                    float candScore = cluster.BasePriority + (isVehCand && hasHeavyGun ? 35.0f : 0f) - (dist * 0.12f);
                    if (candScore <= bestScore)
                        continue;

                    if (checkFF && (isIndirectOrArty || (hasHe && caliber >= 50.0f)))
                    {
                        float safeR = isIndirectOrArty ? 30.0f : 20.0f;
                        float playerSafeR = isIndirectOrArty ? 36.0f : 26.0f;
                        if (AiCore.IsIndirectFireImpactDangerClose(myFaction, candPos, gunnerPtr, safeR, playerSafeR))
                            continue;
                    }

                    if (!turret.CanShootInDirection(dirToCand, 1.0f))
                        continue;

                    float angleAdj = Corvostudio.Weapons.BulletInstance.CalculateTrajectory(dist, bulletSpeed, bulletGrav, out bool canReach);
                    if (!canReach)
                        continue;

                    bool canDirectlySee;
                    if (canBypassGunSlitViaSpotter)
                    {
                        VisionPatches.AllowCommanderIntercomFovBypass = true;
                        try
                        {
                            canDirectlySee = turretAi.CanSee(cand);
                        }
                        finally
                        {
                            VisionPatches.AllowCommanderIntercomFovBypass = false;
                        }
                    }
                    else
                    {
                        canDirectlySee = turretAi.CanSee(cand);
                    }

                    if (!canDirectlySee)
                    {
                        // Static mortars / indirect artillery can engage spotted/marked team-net targets over hills/roofs
                        // without direct optical LOS as long as CalculateTrajectory & friendly-fire blast safety succeeded!
                        bool canShellIndirectly =
                            isIndirectOrArty &&
                            (cluster.IsMarkedOrPinPoint || cluster.IsHalfMarked || cluster.NearRadioRequest || cluster.IsEngagingFriendlies);

                        // Building Demolition / Strongpoint Shelling:
                        // If a tank or field gun has High-Explosive shells (hasHe && caliber >= 37mm) and a known/spotted
                        // enemy soldier is hiding inside a building (behind a closed shutter or interior wall),
                        // allow the tank to shell the building's exterior facade if the line of fire from the barrel
                        // up to the building's outer wall (dist - 5.5m) is unobstructed!
                        bool canShellBuildingFacade =
                            !canShellIndirectly &&
                            !isVehCand &&
                            hasHe &&
                            caliber >= 37.0f &&
                            dist >= 16.0f &&
                            (cluster.IsMarkedOrPinPoint || cluster.IsHalfMarked || cluster.NearRadioRequest || cluster.IsEngagingFriendlies) &&
                            AiCore.HasLowCeilingOverhead(candPos, 3.8f) &&
                            !AiCore.RaycastEnvironment(firePos, dirToCand, dist - 5.5f, veh.transform.root, out _);

                        if (!canShellIndirectly && !canShellBuildingFacade)
                            continue;

                        if (canShellBuildingFacade && VisionConfig.DebugLogging.Value)
                        {
                            AiCore.LogDebug(
                                "BuildingHE",
                                $"{myFaction} {caliber:F0}mm HE turret targeting building facade to flush indoor enemy ({dist:F0}m).",
                                5.0f);
                        }
                    }

                    bestScore = candScore;
                    bestCandidate = cand;
                    bestAngleAdj = angleAdj;
                    break; // Found a valid visible target in this cluster
                }
            }

            if (bestCandidate != null)
            {
                if (curTarget == null || bestCandidate.Pointer != curTarget.Pointer)
                {
                    float offBoreDeg = Vector3.Angle(curFireDir, bestCandidate.GetCenterOfUnit() - firePos);
                    if (canBypassGunSlitViaSpotter &&
                        offBoreDeg > 26.0f &&
                        VisionConfig.EnableDynamicHumanReactionTime.Value)
                    {
                        float gunnerVet = gunner != null ? GetVeterancy01(gunner) : 0f;
                        SoldierPersonality crewPers = GetVehicleCrewPersonality(veh);
                        float jitter = (AiCore.GetDeterministicHash(turretPtr, bestCandidate.Pointer.GetHashCode()) & 31) * 0.011f;
                        float baseCallout = hasInternalSpotter ? 0.78f : 1.08f;
                        float handoffDelay = (baseCallout + jitter + Mathf.Clamp01(offBoreDeg / 180.0f) * 0.18f) *
                                             Mathf.Lerp(1.0f, 0.76f, gunnerVet);
                        if (crewPers == SoldierPersonality.TankCrew_Brawler)
                            handoffDelay *= 0.88f;

                        handoffDelay = Mathf.Clamp(handoffDelay, 0.65f, 1.48f);
                        _pendingTurretIntercom[turretPtr] = new PendingTurretIntercomHandoff
                        {
                            Target = bestCandidate,
                            ReadyTime = now + handoffDelay,
                            AngleAdj = bestAngleAdj
                        };
                        turretAi.nextRefreshTime = Mathf.Min(turretAi.nextRefreshTime, now + handoffDelay + 0.04f);
                        return;
                    }

                    turretAi.CurrentVisibleTarget = bestCandidate;
                    turretAi.supportedSquad = null;
                    turretAi.targetAngleAdjust = bestAngleAdj;
                    turretAi.targetInWeaponRange = true;
                    turret.SelectBestAmmo(bestCandidate);
                    if (TryRecordCombinedArmsAssist(veh.Pointer, now))
                    {
                        AiCore.LogDebug(
                            "CombArmsTurret",
                            $"{myFaction} turret prioritized known/marked enemy squad target (score:{bestScore:F0}).");
                    }
                }
                return;
            }

            // Fix vanilla RefreshTargets quirk (0x180832b3e): allow on-map HE/Smoke artillery/howitzers/mortars and tanks
            // to answer friendly Team Net radio support requests both when idle (curTarget == null) and when only
            // a low-priority unmarked lone infantryman was spotted far away (including allied coalition factions & rear-line pits).
            if ((hasHe || isIndirectOrArty) && (curTarget == null || curScore < 26.0f) && !turretAi.HasRadioSupportRequest)
            {
                turretAi.FindBestArtilleryRequest();
                if (!turretAi.HasRadioSupportRequest &&
                    (isIndirectOrArty || (gunner != null && gunner.joinedSquad != null && IsSquadConnectedToRadioNet(gunner.joinedSquad))))
                {
                    RefreshCombinedArmsIntelIfNeeded();
                    Squad bestReqSquad = null;
                    float bestReqDist = float.MaxValue;
                    for (int r = 0; r < _activeRadioRequests.Count; r++)
                    {
                        ActiveRadioSupportInfo req = _activeRadioRequests[r];
                        if (req.RequestType == RadioRequest.tankSupport ||
                            !AiCore.AreSameSideFactions(req.Faction, myFaction) ||
                            req.SourceSquad == null ||
                            req.SourceSquad.Pointer == IntPtr.Zero ||
                            !req.SourceSquad.RadioRequestActiveAndConfirmed())
                        {
                            continue;
                        }

                        if (!isIndirectOrArty && req.RequestType == RadioRequest.artillerySmoke)
                            continue;
                        if (!hasHe && req.RequestType == RadioRequest.artilleryHE)
                            continue;

                        Vector3 toReq = req.Position - firePos;
                        float reqDist = toReq.magnitude;
                        if (reqDist < 20.0f || reqDist >= bestReqDist)
                            continue;

                        if (!turret.CanShootInDirection(toReq / reqDist, 1.0f))
                            continue;

                        Corvostudio.Weapons.BulletInstance.CalculateTrajectory(reqDist, bulletSpeed, bulletGrav, out bool reqCanReach);
                        if (!reqCanReach)
                            continue;

                        bestReqDist = reqDist;
                        bestReqSquad = req.SourceSquad;
                    }

                    if (bestReqSquad != null)
                    {
                        turretAi.supportedSquad = bestReqSquad;
                        turretAi.supportStartFireTime = now + 2.5f;
                    }
                }

                if (turretAi.HasRadioSupportRequest)
                {
                    bool isSmokeReq = turretAi.supportedSquad != null &&
                                      turretAi.supportedSquad.radioRequest == RadioRequest.artillerySmoke;
                    Vector3 radioPos = turretAi.RadioRequestTargetPosition;
                    bool dangerClose = checkFF && !isSmokeReq && AiCore.IsIndirectFireImpactDangerClose(
                        myFaction,
                        radioPos,
                        gunnerPtr,
                        isIndirectOrArty ? 30.0f : 22.0f,
                        isIndirectOrArty ? 36.0f : 28.0f);
                    if (dangerClose)
                    {
                        turretAi.supportedSquad = null;
                        FriendlyFirePrevented++;
                        return;
                    }

                    float rDist = Vector3.Distance(turret.transform.position, radioPos);
                    float rAngle = Corvostudio.Weapons.BulletInstance.CalculateTrajectory(rDist, bulletSpeed, bulletGrav, out bool rCanReach);
                    if (rCanReach)
                    {
                        bool ammoOk = true;
                        if (turretAi.supportedSquad != null)
                        {
                            ammoOk = turret.SelectBestAmmo(turretAi.supportedSquad.radioRequest) || hasHe;
                        }
                        if (!ammoOk)
                        {
                            turretAi.supportedSquad = null;
                            return;
                        }
                        turretAi.CurrentVisibleTarget = null;
                        turretAi.targetAngleAdjust = rAngle;
                        turretAi.targetInWeaponRange = true;
                        if (TryRecordCombinedArmsAssist(veh.Pointer, Time.time))
                        {
                            AiCore.LogDebug(
                                "CombArmsRadio",
                                $"{myFaction} HE turret answering friendly radio support request ({rDist:F0}m).");
                        }
                    }
                    else
                    {
                        turretAi.supportedSquad = null;
                    }
                }
            }
        }

        // =====================================================================
        // Emplacement & Vehicle Turret Friendly Fire Prevention (TurretAI.AutoPilot / TurretGun)
        // =====================================================================
        internal static bool ShouldBlockTurretFriendlyFire(TurretAI turretAi, Turret turret, Soldier gunner, bool forceFreshCheck = false)
        {
            if (turret == null || turret.Pointer == IntPtr.Zero)
                return false;

            IntPtr turretPtr = turret.Pointer;
            if (turretAi == null)
            {
                if (!_turretAiByTurretPtr.TryGetValue(turretPtr, out turretAi) || turretAi == null)
                {
                    turretAi = turret.GetTurretAI();
                    if (turretAi != null)
                        _turretAiByTurretPtr[turretPtr] = turretAi;
                }
            }

            Vehicle rootVeh = AiCore.GetCachedTurretVehicle(turret);
            if (gunner == null && rootVeh != null)
                gunner = turret.GetSittedUnit(rootVeh);

            if (gunner != null && gunner.IsPlayer())
                return false;

            bool ownedGunnerOrVeh =
                (gunner != null && AiCore.IsLocallyOwnedBot(gunner)) ||
                (rootVeh != null && AiCore.IsLocallyOwnedVehicle(rootVeh));
            if (!ownedGunnerOrVeh)
                return false;

            float now = Time.time;

            if (_turretFFBlockedUntil.TryGetValue(turretPtr, out float blockedUntil) && now < blockedUntil)
                return true;

            if (!forceFreshCheck && _nextTurretFFCheckTime.TryGetValue(turretPtr, out float nextCheck) && now < nextCheck)
                return false;

            _nextTurretFFCheckTime[turretPtr] = now + 0.10f;

            string myFaction = (gunner != null && !string.IsNullOrEmpty(gunner.faction))
                ? gunner.faction
                : GetVehicleCrewFaction(rootVeh, turret);
            if (string.IsNullOrEmpty(myFaction))
                return false;

            Vector3 firePos = turret.GetFirePos();
            Vector3 fireDir = turret.GetFireDir();
            if (fireDir.sqrMagnitude < 0.001f)
                return false;
            fireDir.Normalize();

            float caliber = turret.GetCaliber();
            bool isArtillery = turret.IsArtillery();
            bool isIndirect = turret.IsUpIndirectFire();
            bool isExplosive = caliber >= 20.0f || isArtillery || isIndirect;

            bool hasTargetPos = false;
            Vector3 targetPos = Vector3.zero;
            if (turretAi != null)
            {
                Spottable curTurretVis = turretAi.CurrentVisibleTarget;
                if (curTurretVis != null && curTurretVis.Pointer != IntPtr.Zero)
                {
                    if (AiCore.IsValidAliveSpottable(curTurretVis))
                    {
                        targetPos = curTurretVis.GetCenterOfUnit();
                        hasTargetPos = true;
                    }
                    else
                    {
                        // Stale surrendered or syringe-captured target in TurretAI: clear and block fire immediately!
                        turretAi.CurrentVisibleTarget = null;
                        turretAi.targetInWeaponRange = false;
                        return true;
                    }
                }
                else if (turretAi.HasRadioSupportRequest)
                {
                    targetPos = turretAi.RadioRequestTargetPosition;
                    hasTargetPos = true;
                }
            }

            // Fallback for indirect-fire mortars/howitzers (e.g. delayed FireOnReloadDone or unlinked target):
            // compute the exact ballistic ground impact point from current tube elevation!
            if ((isIndirect || isArtillery) && !hasTargetPos)
            {
                TurretGun tg = turret.TryCast<TurretGun>();
                if (tg != null)
                {
                    float estRange = tg.GetFireRangeAtCurrentRotation();
                    if (estRange >= 15.0f && estRange <= 2500.0f)
                    {
                        Vector3 flatDir = new Vector3(fireDir.x, 0f, fireDir.z);
                        if (flatDir.sqrMagnitude > 0.001f)
                        {
                            targetPos = firePos + flatDir.normalized * estRange;
                            hasTargetPos = true;
                        }
                    }
                }
            }

            float targetDist = hasTargetPos ? Vector3.Distance(firePos, targetPos) : 160.0f;
            float maxCheckDist = Mathf.Clamp(targetDist + 5.0f, 12.0f, 220.0f);
            float baseConeDeg = VisionConfig.FriendlyFireConeDegrees.Value;

            float dangerCloseBlastRadius = 0f;
            if (isIndirect || isArtillery)
                dangerCloseBlastRadius = 30.0f;
            else if (caliber >= 70.0f)
                dangerCloseBlastRadius = 20.0f;
            else if (caliber >= 37.0f)
                dangerCloseBlastRadius = 12.0f;
            else if (caliber >= 20.0f)
                dangerCloseBlastRadius = 6.5f;

            // 1. Prevent direct-fire cannons/AT guns from detonating HE shells on a low wall or sandbag right in front of the muzzle
            if (isExplosive && !isIndirect && VisionConfig.EnableWallShootingPrevention.Value)
            {
                Transform ignoreRoot = (rootVeh != null && rootVeh.transform != null)
                    ? rootVeh.transform.root
                    : (turret.transform != null ? turret.transform.root : null);
                if (ignoreRoot != null && AiCore.IsDirectionBlockedByEnvironment(firePos, fireDir, 5.2f, ignoreRoot))
                {
                    _turretFFBlockedUntil[turretPtr] = now + 0.30f;
                    WallsBlocked++;
                    return true;
                }
            }

            IntPtr gunnerPtr = gunner != null ? gunner.Pointer : IntPtr.Zero;
            IntPtr rootVehPtr = rootVeh != null ? rootVeh.Pointer : IntPtr.Zero;

            // 2. Always check the Human Player first in O(1) so friendly emplacements/turrets never shoot through or mortar the player!
            Soldier player = Soldier.CurrentControlledSoldierOrNull();
            if (player != null)
            {
                string pFac = AiCore.GetCachedFaction(player);
                if (!string.IsNullOrEmpty(pFac) && AiCore.AreSameSideFactions(myFaction, pFac))
                {
                    if (IsFriendlySoldierInTurretFireDanger(
                        player, gunnerPtr, rootVehPtr, firePos, fireDir, maxCheckDist,
                        baseConeDeg, isIndirect, isExplosive, hasTargetPos, targetPos, dangerCloseBlastRadius))
                    {
                        _turretFFBlockedUntil[turretPtr] = now + 0.35f;
                        FriendlyFirePrevented++;
                        return true;
                    }
                }
            }

            // 3. Check all friendly squads in the firing corridor or explosive impact zone
            AiCore.EnsureWorldSnapshot();
            int sqCount = AiCore.WorldSquadCount;
            if (sqCount > 0)
            {
                float maxLineRangeSq = (maxCheckDist + 45f) * (maxCheckDist + 45f);
                float maxBlastRangeSq = (dangerCloseBlastRadius + 48f) * (dangerCloseBlastRadius + 48f);
                var squads = AiCore.WorldSquads;
                var soldiers = AiCore.WorldSoldiers;

                for (int s = 0; s < sqCount; s++)
                {
                    ref AiCore.SquadSnapshot sqSnap = ref squads[s];
                    if (sqSnap.UnitCount == 0 || string.IsNullOrEmpty(sqSnap.Faction))
                        continue;

                    if (!AiCore.AreSameSideFactions(myFaction, sqSnap.Faction))
                        continue;

                    bool nearLine = !isIndirect && (sqSnap.RefPos - firePos).sqrMagnitude <= maxLineRangeSq;
                    Vector3 blastDelta = sqSnap.RefPos - targetPos;
                    float blastSq2D = blastDelta.x * blastDelta.x + blastDelta.z * blastDelta.z;
                    bool nearBlast = hasTargetPos && dangerCloseBlastRadius > 0f && blastSq2D <= maxBlastRangeSq;
                    if (!nearLine && !nearBlast)
                        continue;

                    int end = sqSnap.UnitStartIndex + sqSnap.UnitCount;
                    for (int i = sqSnap.UnitStartIndex; i < end; i++)
                    {
                        ref AiCore.SoldierSnapshot solSnap = ref soldiers[i];
                        if (solSnap.Ptr == gunnerPtr || solSnap.Ref == null)
                            continue;

                        if (IsFriendlySoldierInTurretFireDanger(
                            solSnap.Ref, gunnerPtr, rootVehPtr, firePos, fireDir, maxCheckDist,
                            baseConeDeg, isIndirect, isExplosive, hasTargetPos, targetPos, dangerCloseBlastRadius))
                        {
                            _turretFFBlockedUntil[turretPtr] = now + 0.35f;
                            FriendlyFirePrevented++;
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        private static bool IsFriendlySoldierInTurretFireDanger(
            Soldier friend,
            IntPtr gunnerPtr,
            IntPtr rootVehPtr,
            Vector3 firePos,
            Vector3 fireDir,
            float maxCheckDist,
            float baseConeDeg,
            bool isIndirect,
            bool isExplosive,
            bool hasTargetPos,
            Vector3 targetPos,
            float dangerCloseBlastRadius)
        {
            if (friend == null || friend.Pointer == IntPtr.Zero || !friend.IsAlive || friend.Pointer == gunnerPtr || friend.transform == null)
                return false;

            if (rootVehPtr != IntPtr.Zero && friend.IsOnVehicle())
            {
                Vehicle friendVeh = friend.GetCurrentVehicle();
                if (friendVeh != null && friendVeh.Pointer == rootVehPtr)
                    return false;
            }

            bool isPlayer = friend.IsPlayer();
            Vector3 chestPos = friend.GetCenterOfUnit();
            Vector3 feetPos = friend.transform.position + Vector3.up * 0.45f;

            // A. Danger-Close Explosive Blast Radius around the target impact point (3D + sloped-terrain XZ check for indirect mortars)
            if (hasTargetPos && dangerCloseBlastRadius > 0f)
            {
                float blastR = isPlayer ? (dangerCloseBlastRadius + 6.0f) : dangerCloseBlastRadius;
                float blastRSq = blastR * blastR;
                Vector3 impDelta = chestPos - targetPos;
                if (impDelta.sqrMagnitude <= blastRSq)
                    return true;

                if ((isIndirect || isExplosive) && Mathf.Abs(impDelta.y) <= (isIndirect ? 14.0f : 6.0f))
                {
                    if ((impDelta.x * impDelta.x + impDelta.z * impDelta.z) <= blastRSq)
                        return true;
                }
            }

            Vector3 toFriend = chestPos - firePos;
            float distSq = toFriend.sqrMagnitude;
            if (distSq < 0.04f)
                return false;

            float dist = Mathf.Sqrt(distSq);

            // B. Point-Blank Muzzle Danger Zone (friendly standing right in front of the barrel)
            if (dist <= 3.5f && Vector3.Angle(fireDir, toFriend) <= 45.0f)
                return true;

            // C. Direct Line-of-Fire Corridor & Cone
            if (!isIndirect && dist <= maxCheckDist)
            {
                float along = Vector3.Dot(toFriend, fireDir);
                if (along > 0.4f && along <= maxCheckDist)
                {
                    Vector3 rayPt = firePos + fireDir * along;
                    float perpChest = Vector3.Distance(rayPt, chestPos);
                    float perpFeet = Vector3.Distance(rayPt, feetPos);
                    float minPerp = Mathf.Min(perpChest, perpFeet);

                    // Also account for horizontal (XZ) sweep when vertical separation is within 2.4m
                    if (Mathf.Abs(rayPt.y - chestPos.y) <= 2.4f)
                    {
                        Vector2 flatRay = new Vector2(rayPt.x - chestPos.x, rayPt.z - chestPos.z);
                        minPerp = Mathf.Min(minPerp, flatRay.magnitude);
                    }

                    float corridorRadius = isPlayer
                        ? (isExplosive ? 3.6f : 2.6f)
                        : (isExplosive ? 2.8f : 1.85f);
                    float coneDeg = isPlayer
                        ? Mathf.Max(baseConeDeg, 8.5f)
                        : Mathf.Max(baseConeDeg, 6.0f);

                    if (minPerp <= corridorRadius || Vector3.Angle(fireDir, toFriend) <= coneDeg)
                        return true;
                }
            }

            return false;
        }

        private static void RunProactiveEmplacementStaffing(float now)
        {
            var allVehicles = Vehicle.allVehicles;
            if (allVehicles == null)
                return;

            int vCount = allVehicles.Count;
            if (vCount == 0)
                return;

            AiCore.EnsureWorldSnapshot();
            int sqCount = AiCore.WorldSquadCount;
            if (sqCount == 0)
                return;

            var squads = AiCore.WorldSquads;
            var soldiers = AiCore.WorldSoldiers;

            // Incremental round-robin scan: inspect at most 10 vehicles AND cap full squad-matching candidate evaluations
            // at 3 open static guns per director slice so maps with 50+ bunker MGs / flak guns NEVER spike a single frame!
            int maxInspect = Mathf.Min(vCount, 10);
            if (_emplacementScanCursor < 0 || _emplacementScanCursor >= vCount)
                _emplacementScanCursor = 0;

            int stepsTaken = 0;
            int staticEvaluations = 0;

            for (int step = 0; step < maxInspect; step++)
            {
                stepsTaken++;
                int i = (_emplacementScanCursor + step) % vCount;
                Vehicle veh = allVehicles[i];
                if (veh == null || veh.Pointer == IntPtr.Zero || veh.transform == null)
                    continue;

                // Note: Vehicle.CanFight() (0x18085B7E0) returns false whenever a vehicle/emplacement is empty!
                // Match vanilla Vehicle.IsCoverAvailable (0x18085F9A0) by also rejecting IsLocked() and out-of-ammo turrets
                // so bots never walk toward Lua-locked/prop emplacements or dry guns.
                if (!AiCore.IsLocallyOwnedVehicle(veh) || veh.IsLocked() || veh.IsDisabled() || veh.IsCoverDestroyed() || veh.IsAirVehicle() || veh.PlayerIsInside())
                    continue;

                bool isStaticGun = veh.TryCast<MovableVehicle>() == null;
                bool isTransport = !isStaticGun && veh.IsTransportVehicle();
                bool isUnmannedCombatVehicle = !isStaticGun && !veh.HasDriver() && !isTransport;
                Vector3 vehPos = veh.transform.position;
                IntPtr vehPtr = veh.Pointer;

                // 0. Empty Map-Parked Transport Requisition (Jeeps, Trucks, Halftracks, APCs):
                //    Allow unengaged rear-echelon AI foot squads (> 125m from objective, out of contact) within <= 45m
                //    to motorize to the front when no human player is within 32m!
                if (isTransport &&
                    !veh.HasDriver() &&
                    veh.peopleInside <= 0 &&
                    !veh.CanBeRepaired() &&
                    !_vehiclesCrewRetreatedFrom.Contains(vehPtr) &&
                    !IsAnyHumanPlayerWithinRadius(soldiers, AiCore.WorldSoldierCount, vehPos, 32.0f))
                {
                    string vehFac = veh.GetVehicleFaction();
                    int seatCap = veh.seats != null ? veh.seats.Length : 0;
                    if (seatCap >= 3)
                    {
                        Squad bestRearSq = null;
                        float bestRearDistSq = 45.0f * 45.0f;
                        for (int s = 0; s < sqCount; s++)
                        {
                            ref AiCore.SquadSnapshot sqSnap = ref squads[s];
                            if (!sqSnap.LeaderIsAlive ||
                                sqSnap.IsVehicleCrew ||
                                sqSnap.LeaderOnVehicle ||
                                sqSnap.IsPlayerInSquad ||
                                sqSnap.UnitCount < 2 ||
                                sqSnap.UnitCount > seatCap ||
                                _squadOrderedToVehicle.ContainsKey(sqSnap.Ptr) ||
                                _garrisonAssignedObjectiveBySquad.ContainsKey(sqSnap.Ptr))
                            {
                                continue;
                            }

                            if (!string.IsNullOrEmpty(vehFac) && AiCore.AreEnemyFactions(vehFac, sqSnap.Faction))
                                continue;

                            float dSq = (sqSnap.RefPos - vehPos).sqrMagnitude;
                            if (dSq >= bestRearDistSq)
                                continue;

                            Squad candSq = sqSnap.Ref;
                            Soldier candLeader = sqSnap.Leader;
                            if (candSq == null ||
                                candLeader == null ||
                                !AiCore.IsLocallyOwnedBot(candLeader) ||
                                candSq.InChargeTime() ||
                                IsSquadActivelyRetreating(sqSnap.Ptr, now) ||
                                candLeader.GetSuppressionValue() >= 6.0f ||
                                candSq.currentTask == null ||
                                candSq.currentTask.Pointer == IntPtr.Zero)
                            {
                                continue;
                            }

                            if (_squadBlackboards.TryGetValue(sqSnap.Ptr, out SquadBlackboard tBoard) &&
                                (tBoard.HasActiveThreat || tBoard.IsDefendingObjective))
                            {
                                continue;
                            }

                            Vector3 taskDest = candSq.currentTask.GetTaskDestination();
                            if ((taskDest - sqSnap.RefPos).sqrMagnitude < 125.0f * 125.0f)
                                continue;

                            bestRearDistSq = dSq;
                            bestRearSq = candSq;
                        }

                        if (bestRearSq != null && (!Photon.Pun.PhotonNetwork.InRoom || AiCore.TryConsumeBotRpcBudget(0.45f)))
                        {
                            try
                            {
                                _squadOrderedToVehicle[bestRearSq.Pointer] = vehPtr;
                                bestRearSq.SendUnitsToVehicle(veh);
                                StaticGunsStaffedCount++;
                                TrySayTacticalVoice(bestRearSq.Leader, bestRearSq, VoiceManager.VoiceClip.illTakeHisSeat, 0.15f);
                            }
                            catch { }
                            continue;
                        }
                    }
                }

                Turret mainTurret = veh.GetMainTurret(false);
                if (mainTurret == null || mainTurret.WeaponsCount() <= 0)
                    continue;

                bool hasAmmo = HasAnyEmplacementTurretAmmo(veh, mainTurret);

                if (isStaticGun)
                {
                    TryReleaseStaleStaticEmplacementCrew(veh, mainTurret, vehPos, hasAmmo, now, squads, sqCount);
                }

                if (!hasAmmo)
                    continue;

                if (!isStaticGun && mainTurret.GetSittedUnit(veh) != null)
                    continue;

                string occupantFaction = GetFirstAliveVehicleOccupantFaction(veh);

                // 1. If this is an empty/abandoned movable combat vehicle (Tank, Armored Car)
                //    and a bailed-out Vehicle Crew (`sq.IsVehicleCrew`) with >= 2 alive crewmen (driver + gunner) is within 85m,
                //    order all alive members of that crew (including the Leader) to re-mount it!
                //    Skip vehicles already abandoned via RetreatCrew or damaged without an equipped crew mechanic (prevents mount/eject loops).
                if (isUnmannedCombatVehicle && !_vehiclesCrewRetreatedFrom.Contains(vehPtr))
                {
                    Squad bestBailedCrew = null;
                    float bestCrewDistSq = 85.0f * 85.0f;
                    bool vehNeedsRepair = veh.CanBeRepaired();

                    for (int s = 0; s < sqCount; s++)
                    {
                        ref AiCore.SquadSnapshot sqSnap = ref squads[s];
                        if (!sqSnap.IsVehicleCrew || !sqSnap.LeaderIsAlive || sqSnap.IsPlayerInSquad || sqSnap.LeaderOnVehicle || sqSnap.UnitCount < 2)
                            continue;

                        if (occupantFaction != null && AiCore.AreEnemyFactions(occupantFaction, sqSnap.Faction))
                            continue;

                        float dSq = (sqSnap.RefPos - vehPos).sqrMagnitude;
                        if (dSq >= bestCrewDistSq)
                            continue;

                        Squad sq = sqSnap.Ref;
                        Soldier leader = sqSnap.Leader;
                        if (sq == null || sq.SomeoneIsRepairing() || !AiCore.IsLocallyOwnedBot(leader))
                            continue;

                        if (vehNeedsRepair)
                        {
                            Soldier mech = sq.GetMechanic();
                            if (mech == null || !mech.IsAlive || mech.GetRepairTools() == null)
                                continue;
                        }

                        int aliveFootCrew = 0;
                        int uEnd = sqSnap.UnitStartIndex + sqSnap.UnitCount;
                        for (int cIdx = sqSnap.UnitStartIndex; cIdx < uEnd; cIdx++)
                        {
                            if (!soldiers[cIdx].IsOnVehicle)
                                aliveFootCrew++;
                        }
                        if (aliveFootCrew < 2)
                            continue;

                        bestCrewDistSq = dSq;
                        bestBailedCrew = sq;
                    }

                    if (bestBailedCrew != null && bestBailedCrew.units != null)
                    {
                        try
                        {
                            _squadOrderedToVehicle[bestBailedCrew.Pointer] = vehPtr;
                            AiDestination crewVehDest = veh.Cast<AiDestination>();
                            for (int cIdx = 0; cIdx < bestBailedCrew.units.Count; cIdx++)
                            {
                                Soldier cUnit = bestBailedCrew.units[cIdx];
                                if (cUnit != null && AiCore.IsLocallyOwnedBot(cUnit) && !cUnit.IsOnVehicle())
                                {
                                    _assignedEmplacementUntil[cUnit.Pointer] = now + 20.0f;
                                    _assignedEmplacementVehicle[cUnit.Pointer] = vehPtr;
                                    cUnit.CoverPosition(crewVehDest);
                                }
                            }
                            StaticGunsStaffedCount++;
                            TrySayTacticalVoice(bestBailedCrew.Leader, bestBailedCrew, VoiceManager.VoiceClip.illTakeHisSeat, 0.15f);
                        }
                        catch { }
                        continue;
                    }

                    // 1B. Empty Map-Parked Operational Combat Vehicles (Tanks, Armored Cars, Armed Halftracks):
                    //     When no bailed tank crew is nearby and no human player is within 32m, allow a depleted 2-3 man
                    //     AI foot remnant squad within <= 55m to crew map-placed armor so operational tanks never sit idle!
                    if (!vehNeedsRepair &&
                        veh.peopleInside <= 0 &&
                        !IsAnyHumanPlayerWithinRadius(soldiers, AiCore.WorldSoldierCount, vehPos, 32.0f))
                    {
                        string vehFac = veh.GetVehicleFaction();
                        Squad bestRemnantSq = null;
                        float bestRemDistSq = 55.0f * 55.0f;
                        for (int s = 0; s < sqCount; s++)
                        {
                            ref AiCore.SquadSnapshot sqSnap = ref squads[s];
                            if (sqSnap.IsVehicleCrew ||
                                !sqSnap.LeaderIsAlive ||
                                sqSnap.IsPlayerInSquad ||
                                sqSnap.LeaderOnVehicle ||
                                sqSnap.UnitCount < 2 ||
                                sqSnap.UnitCount > 3 ||
                                _squadOrderedToVehicle.ContainsKey(sqSnap.Ptr))
                            {
                                continue;
                            }

                            if (!string.IsNullOrEmpty(vehFac) && AiCore.AreEnemyFactions(vehFac, sqSnap.Faction))
                                continue;
                            if (occupantFaction != null && AiCore.AreEnemyFactions(occupantFaction, sqSnap.Faction))
                                continue;

                            float dSq = (sqSnap.RefPos - vehPos).sqrMagnitude;
                            if (dSq >= bestRemDistSq)
                                continue;

                            Squad sq = sqSnap.Ref;
                            Soldier leader = sqSnap.Leader;
                            if (sq == null || leader == null || !AiCore.IsLocallyOwnedBot(leader) || sq.InChargeTime() || IsSquadActivelyRetreating(sqSnap.Ptr, now))
                                continue;

                            bestRemDistSq = dSq;
                            bestRemnantSq = sq;
                        }

                        if (bestRemnantSq != null && (!Photon.Pun.PhotonNetwork.InRoom || AiCore.TryConsumeBotRpcBudget(0.45f)))
                        {
                            try
                            {
                                _squadOrderedToVehicle[bestRemnantSq.Pointer] = vehPtr;
                                bestRemnantSq.SendUnitsToVehicle(veh);
                                StaticGunsStaffedCount++;
                                TrySayTacticalVoice(bestRemnantSq.Leader, bestRemnantSq, VoiceManager.VoiceClip.illTakeHisSeat, 0.15f);
                            }
                            catch { }
                            continue;
                        }
                    }
                }

                // Never assign regular foot soldiers to driverless movable vehicles (halftracks, trucks, jeeps, tanks) —
                // only staff true static guns/emplacements (`isStaticGun`) that have an open turret OR loader/assistant seat!
                if (!isStaticGun || !TryGetAvailableEmplacementOrTurretSeatIndex(veh, out _, out int openStaticSeats))
                    continue;

                // Count/expire inbound bots in O(K) over _assignedEmplacementVehicle (typically 0-4 bots total across map)
                // instead of wrapping u.targetDestination on every soldier across every nearby squad!
                int inboundBotsCount = 0;
                if (_assignedEmplacementVehicle.Count > 0)
                {
                    _reusableExpiredEmplacementBots.Clear();
                    foreach (var kvp in _assignedEmplacementVehicle)
                    {
                        if (kvp.Value != vehPtr)
                            continue;

                        IntPtr botPtr = kvp.Key;
                        if (!AiCore.TryGetSoldierHandles(botPtr, out AiCore.SoldierFastHandles bHandles) ||
                            !bHandles.Ref.IsAlive ||
                            bHandles.Ref.IsOnVehicle())
                        {
                            _reusableExpiredEmplacementBots.Add(botPtr);
                            continue;
                        }

                        if (_assignedEmplacementUntil.TryGetValue(botPtr, out float claimUntil) && now >= claimUntil)
                        {
                            bHandles.Ref.CoverPosition(null);
                            _reusableExpiredEmplacementBots.Add(botPtr);
                            continue;
                        }

                        if (!bHandles.Ref.HasDestinationAssigned ||
                            AiCore.ResolveTargetDestination(bHandles) == AiCore.CachedDestKind.None ||
                            bHandles.CachedDestPtr != vehPtr)
                        {
                            _reusableExpiredEmplacementBots.Add(botPtr);
                            continue;
                        }

                        inboundBotsCount++;
                    }

                    for (int eIdx = 0; eIdx < _reusableExpiredEmplacementBots.Count; eIdx++)
                    {
                        ClearEmplacementAssignment(_reusableExpiredEmplacementBots[eIdx], markUnreachable: true, 90.0f);
                    }
                    _reusableExpiredEmplacementBots.Clear();
                }

                if (inboundBotsCount >= openStaticSeats)
                    continue;

                staticEvaluations++;
                Soldier bestCandidate = null;
                float bestScoreDistSq = 65.0f * 65.0f;
                float bestRealDistSq = 0f;
                bool canShoot360 = mainTurret.CanShoot360;
                Vector3 vehFwd = veh.transform.forward;

                for (int s = 0; s < sqCount; s++)
                {
                    ref AiCore.SquadSnapshot sqSnap = ref squads[s];
                    if (!sqSnap.LeaderIsAlive || sqSnap.LeaderOnVehicle || sqSnap.IsPlayerInSquad || sqSnap.UnitCount == 0)
                        continue;

                    // Never send a soldier to board a multi-seat gun that currently has a live enemy occupant!
                    if (occupantFaction != null && AiCore.AreEnemyFactions(occupantFaction, sqSnap.Faction))
                        continue;

                    Squad sq = sqSnap.Ref;
                    Soldier leader = sqSnap.Leader;
                    if (sq == null || leader == null)
                        continue;

                    bool isBailedCrew = sqSnap.IsVehicleCrew && !sq.SomeoneIsRepairing();
                    if (!isBailedCrew && (sq.InChargeTime() || IsSquadActivelyRetreating(sq.Pointer, now)))
                        continue;

                    bool isDefendingSquad = _garrisonAssignedObjectiveBySquad.ContainsKey(sqSnap.Ptr) ||
                                            (_squadBlackboards.TryGetValue(sqSnap.Ptr, out SquadBlackboard defBb) && defBb.IsDefendingObjective);
                    float empMult = GetFactionCommandProfile(sqSnap.Faction).EmplacementSearchMult;
                    float baseSquadReach = isBailedCrew ? 75.0f : (isDefendingSquad ? 55.0f : 42.0f);
                    float maxSquadDist = baseSquadReach * empMult;
                    if ((sqSnap.RefPos - vehPos).sqrMagnitude > maxSquadDist * maxSquadDist)
                        continue;

                    // Keep regular infantry squads tethered near their active squad order area so advancing squads
                    // never peel riflemen 50m+ backwards off an assault axis to chase distant rear bunkers,
                    // while allowing defenders up to 50m reach across their strongpoint's AT/AA/coastal/MG pits!
                    if (!isBailedCrew && sq.order != Order.follow && sq.moveOrderPosition.sqrMagnitude > 0.1f)
                    {
                        float maxOrderOffset = isDefendingSquad
                            ? Mathf.Max(sq.moveOrderRadius + 36.0f, 52.0f)
                            : Mathf.Max(sq.moveOrderRadius + 22.0f, 34.0f);
                        if ((vehPos - sq.moveOrderPosition).sqrMagnitude > maxOrderOffset * maxOrderOffset)
                            continue;
                    }

                    // Check pure C# enemy-cluster overrun (< 22m) BEFORE computing threat direction!
                    bool emplacementOverrun = false;
                    for (int c = 0; c < _knownEnemyClusters.Count; c++)
                    {
                        if (!AiCore.AreSameSideFactions(_knownEnemyClusters[c].Faction, sqSnap.Faction) &&
                            (_knownEnemyClusters[c].Position - vehPos).sqrMagnitude <= 22.0f * 22.0f)
                        {
                            emplacementOverrun = true;
                            break;
                        }
                    }
                    if (emplacementOverrun)
                        continue;

                    // Verify the turret can aim toward the squad's threat sector or enemy approach axis (fast blackboard lookup first!)
                    if (!canShoot360)
                    {
                        Vector3 threatDir = (_squadBlackboards.TryGetValue(sqSnap.Ptr, out SquadBlackboard bb) &&
                                             bb.HasActiveThreat &&
                                             bb.ThreatDirection.sqrMagnitude > 0.01f)
                            ? bb.ThreatDirection
                            : GetEffectiveThreatDirection(leader, sq, vehPos, vehFwd);

                        if (threatDir.sqrMagnitude > 0.01f && !mainTurret.IsPositionInDirection(threatDir))
                            continue;
                    }

                    int uEnd = sqSnap.UnitStartIndex + sqSnap.UnitCount;
                    float maxSoldierDistSq = isBailedCrew
                        ? (65.0f * 65.0f)
                        : (isDefendingSquad ? (50.0f * 50.0f) : (32.0f * 32.0f));
                    for (int uIdx = sqSnap.UnitStartIndex; uIdx < uEnd; uIdx++)
                    {
                        ref AiCore.SoldierSnapshot uSnap = ref soldiers[uIdx];
                        if (uSnap.IsOnVehicle || uSnap.IsPlayer || uSnap.HasSurrendered)
                            continue;

                        // Avoid assigning ground infantry to unreachable rooftop/cliff emplacements (> 4.5m vertical delta)
                        if (Mathf.Abs(vehPos.y - uSnap.Pos.y) > 4.5f)
                            continue;

                        float rawDistSq = (uSnap.Pos - vehPos).sqrMagnitude;
                        if (rawDistSq > maxSoldierDistSq)
                            continue;

                        float scoreDistSq = isBailedCrew ? rawDistSq * 0.45f : rawDistSq;
                        if (scoreDistSq >= bestScoreDistSq)
                            continue;

                        // Allow bailed-out vehicle crew leaders to crew static guns/artillery, but keep infantry Squad Leaders free
                        if (uSnap.IsSquadLeader && !isBailedCrew)
                            continue;

                        if (_assignedEmplacementUntil.TryGetValue(uSnap.Ptr, out float assignedUntil))
                        {
                            if (now < assignedUntil)
                                continue;
                            ClearEmplacementAssignment(uSnap.Ptr, markUnreachable: true, 90.0f);
                            continue;
                        }

                        if (_unreachableEmplacementUntil.TryGetValue(uSnap.Ptr, out float unreachableUntil) && now < unreachableUntil)
                            continue;

                        Soldier u = uSnap.Ref;
                        if (u == null || !AiCore.IsLocallyOwnedBot(u))
                            continue;

                        if (u.hasSyringe || u.IsRadioman() || u.isBleeding || u.dontLeaveVehicle)
                            continue;

                        if (u.GetSuppressionValue() >= 30f ||
                            (u.aiController != null && u.aiController.HasEnemyAndCanFireAtHim && u.aiController.distFromTarget < 28f))
                        {
                            continue;
                        }

                        if (u.Params != null && (!u.Params.canMove || u.Params.neverLeaveVehicle))
                            continue;

                        bestScoreDistSq = scoreDistSq;
                        bestRealDistSq = rawDistSq;
                        bestCandidate = u;
                    }
                }

                if (bestCandidate != null)
                {
                    _assignedEmplacementUntil[bestCandidate.Pointer] = now + 18.0f;
                    _assignedEmplacementVehicle[bestCandidate.Pointer] = vehPtr;
                    if (AiCore.TryGetSoldierHandles(bestCandidate.Pointer, out var candHandles))
                        candHandles.Stance.CombatCrouchLockUntil = 0f;
                    if (bestCandidate.aiController != null)
                    {
                        SetBotPose(bestCandidate.aiController, bestCandidate, SoldierPose.Idle, true);
                    }
                    AiDestination vehDest = veh.Cast<AiDestination>();
                    AiCore.AssignBotCoverOrDestination(bestCandidate.aiController, bestCandidate, vehDest);
                    StaticGunsStaffedCount++;
                    TrySayTacticalVoice(bestCandidate, bestCandidate.joinedSquad, VoiceManager.VoiceClip.illTakeHisSeat, 0.2f);
                    if (VisionConfig.DebugLogging.Value)
                    {
                        AiCore.LogDebug("Emplacement", $"{AiCore.GetCachedFaction(bestCandidate)} soldier assigned to crew emplacement seat ({Mathf.Sqrt(bestRealDistSq):F0}m away, openSeats:{openStaticSeats}).");
                    }
                }

                if (staticEvaluations >= 3)
                    break;
            }

            _emplacementScanCursor = (_emplacementScanCursor + stepsTaken) % vCount;
        }

        private static bool IsAnyHumanPlayerWithinRadius(
            AiCore.SoldierSnapshot[] soldiers,
            int soldierCount,
            Vector3 targetPos,
            float radius)
        {
            if (soldiers == null || soldierCount <= 0)
                return false;

            float rSq = radius * radius;
            for (int i = 0; i < soldierCount; i++)
            {
                if (soldiers[i].IsPlayer && (soldiers[i].Pos - targetPos).sqrMagnitude <= rSq)
                    return true;
            }
            return false;
        }

        private static string GetObjectiveDebugLabel(MissionObjective obj)
        {
            if (obj == null)
                return "unknown";
            string disp = obj.GetDisplayName();
            if (!string.IsNullOrWhiteSpace(disp))
                return disp;
            string raw = obj.objectiveName;
            if (!string.IsNullOrWhiteSpace(raw))
                return raw;
            Vector3 p = obj.GetTaskPosition();
            return $"({p.x:F0},{p.z:F0})";
        }

        internal static bool IsActivelyHeadingToEmplacement(Soldier soldier, float now)
        {
            if (soldier == null)
                return false;

            IntPtr ptr = soldier.Pointer;
            if (!_assignedEmplacementUntil.TryGetValue(ptr, out float empUntil))
                return false;

            if (soldier.IsOnVehicle())
            {
                ClearEmplacementAssignment(ptr, markUnreachable: false);
                return false;
            }

            if (now >= empUntil)
            {
                ClearEmplacementAssignment(ptr, markUnreachable: true, 90.0f);
                return false;
            }

            if (!soldier.HasDestinationAssigned || soldier.targetDestination == null)
            {
                ClearEmplacementAssignment(ptr, markUnreachable: true, 45.0f);
                return false;
            }

            if (_assignedEmplacementVehicle.TryGetValue(ptr, out IntPtr expectedVehPtr) &&
                expectedVehPtr != IntPtr.Zero &&
                soldier.targetDestination.Pointer != expectedVehPtr)
            {
                ClearEmplacementAssignment(ptr, markUnreachable: true, 45.0f);
                return false;
            }

            return true;
        }

        private static void ClearEmplacementAssignment(IntPtr soldierPtr, bool markUnreachable, float cooldownSeconds = 120.0f)
        {
            if (soldierPtr == IntPtr.Zero)
                return;
            _assignedEmplacementUntil.Remove(soldierPtr);
            _assignedEmplacementVehicle.Remove(soldierPtr);
            if (markUnreachable)
            {
                _unreachableEmplacementUntil[soldierPtr] = Time.time + cooldownSeconds;
            }
        }

        private static void TryReleaseStaleStaticEmplacementCrew(
            Vehicle veh,
            Turret mainTurret,
            Vector3 vehPos,
            bool hasAmmo,
            float now,
            AiCore.SquadSnapshot[] squads,
            int sqCount)
        {
            if (veh == null || mainTurret == null || veh.seats == null)
                return;

            var seats = veh.seats;
            int seatCount = seats.Length;
            Soldier firstOcc = null;
            for (int i = 0; i < seatCount; i++)
            {
                Seats s = seats[i];
                Soldier occ = s != null ? s.unitSet : null;
                if (occ != null && occ.IsAlive && AiCore.IsLocallyOwnedBot(occ))
                {
                    firstOcc = occ;
                    break;
                }
            }

            if (firstOcc == null)
                return;

            string occFaction = AiCore.GetCachedFaction(firstOcc);
            if (string.IsNullOrEmpty(occFaction))
                return;

            RefreshCombinedArmsIntelIfNeeded();

            bool outOfAmmo = !hasAmmo;
            bool flankedBlindSpot = false;
            bool enemyInFiringSector = false;
            bool hasLiveTarget = veh.CurrentVisibleTarget != null && veh.CurrentVisibleTarget.Pointer != IntPtr.Zero;
            bool canShoot360 = mainTurret.CanShoot360;

            for (int c = 0; c < _knownEnemyClusters.Count; c++)
            {
                var cluster = _knownEnemyClusters[c];
                if (!AiCore.AreEnemyFactions(occFaction, cluster.Faction))
                    continue;

                Vector3 toEnemy = cluster.Position - vehPos;
                float dSq = toEnemy.sqrMagnitude;
                bool inArc = canShoot360 || (dSq > 0.25f && mainTurret.IsPositionInDirection(toEnemy / Mathf.Sqrt(dSq)));

                if (!canShoot360 && !inArc && dSq <= 22.0f * 22.0f)
                    flankedBlindSpot = true;
                if (inArc && dSq <= 155.0f * 155.0f)
                    enemyInFiringSector = true;
            }

            bool anyFriendlySquadNear = false;
            if (!hasLiveTarget && !enemyInFiringSector && !outOfAmmo && !flankedBlindSpot)
            {
                for (int s = 0; s < sqCount; s++)
                {
                    ref AiCore.SquadSnapshot sqSnap = ref squads[s];
                    if (sqSnap.IsVehicleCrew ||
                        !sqSnap.LeaderIsAlive ||
                        AiCore.AreEnemyFactions(occFaction, sqSnap.Faction))
                    {
                        continue;
                    }

                    if ((sqSnap.RefPos - vehPos).sqrMagnitude <= 72.0f * 72.0f)
                    {
                        anyFriendlySquadNear = true;
                        break;
                    }
                }
            }

            bool inConcreteBunker = AiCore.HasLowCeilingOverhead(vehPos, 3.8f);
            bool nearBlast = VisionPatches.TryGetNearestRecentExplosion(vehPos, inConcreteBunker ? 6.5f : 13.5f, 2.5f, out _, out _);

            for (int i = 0; i < seatCount; i++)
            {
                Seats s = seats[i];
                Soldier occ = s != null ? s.unitSet : null;
                if (occ == null || !occ.IsAlive || !AiCore.IsLocallyOwnedBot(occ))
                    continue;

                var occParams = occ.Params;
                if (occ.dontLeaveVehicle ||
                    (occParams != null && (!occParams.canMove || occParams.neverLeaveVehicle)))
                {
                    continue;
                }

                float occSupp = occ.GetSuppressionValue();
                bool selfPreservationBail = occ.isBleeding ||
                                            nearBlast ||
                                            (!inConcreteBunker && s.showUnitSet && (occSupp >= 62f || (occSupp >= 45f && !hasLiveTarget)));

                bool soldierSquadMovedOn = false;
                if (!hasLiveTarget && !enemyInFiringSector && !outOfAmmo && !flankedBlindSpot && !selfPreservationBail)
                {
                    Squad sq = occ.joinedSquad;
                    Soldier leader = sq != null ? sq.Leader : null;
                    if (leader != null &&
                        leader.IsAlive &&
                        leader.Pointer != occ.Pointer &&
                        !leader.IsOnVehicle() &&
                        leader.transform != null)
                    {
                        float leaderDistSq = (leader.transform.position - vehPos).sqrMagnitude;
                        if (leaderDistSq > 68.0f * 68.0f)
                        {
                            soldierSquadMovedOn = true;
                        }
                        else if (sq.currentTask != null &&
                                 leaderDistSq > 48.0f * 48.0f &&
                                 (sq.currentTask.GetTaskDestination() - vehPos).sqrMagnitude > 95.0f * 95.0f)
                        {
                            soldierSquadMovedOn = true;
                        }
                    }
                    else if (sq != null &&
                             leader != null &&
                             leader.Pointer == occ.Pointer &&
                             sq.order != Order.defend &&
                             sq.currentTask != null &&
                             (sq.currentTask.GetTaskDestination() - vehPos).sqrMagnitude > 105.0f * 105.0f)
                    {
                        soldierSquadMovedOn = true;
                    }
                }

                if (!outOfAmmo &&
                    !flankedBlindSpot &&
                    !selfPreservationBail &&
                    (hasLiveTarget || enemyInFiringSector || (anyFriendlySquadNear && !soldierSquadMovedOn)))
                {
                    continue;
                }

                try
                {
                    ClearEmplacementAssignment(occ.Pointer, markUnreachable: true, selfPreservationBail ? 45.0f : 90.0f);
                    occ.CoverPosition(null);
                    if (occ.IsOnVehicle())
                        veh.GetOffVehicle(occ);

                    if (selfPreservationBail && occ.aiController != null)
                    {
                        TrySeekNearbyCover(occ.aiController, occ);
                        SetBotPose(occ.aiController, occ, SoldierPose.Crouch, true);
                    }

                    if (VisionConfig.DebugLogging.Value)
                    {
                        string reason = outOfAmmo
                            ? "out_of_ammo"
                            : (selfPreservationBail
                                ? "self_preservation"
                                : (flankedBlindSpot ? "flanked_blind_spot" : "front_moved_on"));
                        AiCore.LogDebug("Emplacement", $"{occFaction} soldier dismounted static gun ({reason}).", 4.0f);
                    }
                }
                catch { }
            }
        }

        private static string GetFirstAliveVehicleOccupantFaction(Vehicle veh)
        {
            if (veh == null || veh.seats == null)
                return null;

            var seats = veh.seats;
            int count = seats.Length;
            for (int i = 0; i < count; i++)
            {
                Seats s = seats[i];
                Soldier occ = s != null ? s.unitSet : null;
                if (occ != null && occ.IsAlive)
                {
                    string f = AiCore.GetCachedFaction(occ);
                    if (!string.IsNullOrEmpty(f))
                        return f;
                }
            }
            return null;
        }

        private static bool HasHostileVehicleOccupant(Vehicle veh, string faction)
        {
            if (string.IsNullOrEmpty(faction))
                return false;
            string occFac = GetFirstAliveVehicleOccupantFaction(veh);
            return occFac != null && AiCore.AreEnemyFactions(occFac, faction);
        }

        private static bool HasAnyEmplacementTurretAmmo(Vehicle veh, Turret mainTurret)
        {
            if (mainTurret != null)
            {
                TurretGun mainGun = mainTurret.TryCast<TurretGun>();
                if (mainGun != null && mainGun.HasAmmo())
                    return true;
                if (mainGun == null)
                    return true;
            }

            if (veh != null && veh.seats != null)
            {
                var seats = veh.seats;
                int count = seats.Length;
                for (int i = 0; i < count; i++)
                {
                    Seats s = seats[i];
                    if (s != null && s.hasTurret && s.connectedTurret != null)
                    {
                        TurretGun tg = s.connectedTurret.TryCast<TurretGun>();
                        if (tg != null && tg.HasAmmo())
                            return true;
                    }
                }
            }
            return false;
        }

        private static bool TryGetAvailableTurretSeatIndex(Vehicle veh, out int turretSeatIndex)
        {
            turretSeatIndex = -1;
            if (veh == null || veh.seats == null)
                return false;

            var seats = veh.seats;
            int count = seats.Length;

            // Prefer the MainTurretSeat first if it is empty
            Seats mainSeat = veh.MainTurretSeat;
            if (mainSeat != null && mainSeat.hasTurret && !mainSeat.hasUnitSet &&
                mainSeat.connectedTurret != null && mainSeat.connectedTurret.WeaponsCount() > 0)
            {
                for (int i = 0; i < count; i++)
                {
                    if (seats[i] != null && seats[i].Pointer == mainSeat.Pointer)
                    {
                        turretSeatIndex = i;
                        return true;
                    }
                }
            }

            for (int i = 0; i < count; i++)
            {
                Seats s = seats[i];
                if (s != null && s.hasTurret && !s.hasUnitSet &&
                    s.connectedTurret != null && s.connectedTurret.WeaponsCount() > 0)
                {
                    turretSeatIndex = i;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// For movable vehicles, returns only open armed turret seats (<see cref="TryGetAvailableTurretSeatIndex"/>).
        /// For static emplacements (AT guns, field guns, howitzers, AA guns with 2 crew seats), prioritizes the
        /// gunner/turret seat first, and once the gunner seat is filled, returns the open secondary loader/traverse seat
        /// so both seats of two-man emplacements are staffed!
        /// </summary>
        private static bool TryGetAvailableEmplacementOrTurretSeatIndex(Vehicle veh, out int seatIndex, out int openSeatCount)
        {
            seatIndex = -1;
            openSeatCount = 0;
            if (veh == null || veh.seats == null)
                return false;

            bool isStaticEmplacement = veh.TryCast<MovableVehicle>() == null &&
                                       veh.GetMainTurret(false) != null &&
                                       veh.GetMainTurret(false).WeaponsCount() > 0;

            bool hasTurretSeat = TryGetAvailableTurretSeatIndex(veh, out int turretIdx);
            if (!isStaticEmplacement)
            {
                if (hasTurretSeat)
                {
                    seatIndex = turretIdx;
                    openSeatCount = 1;
                    return true;
                }
                return false;
            }

            var seats = veh.seats;
            int count = seats.Length;
            int firstSecondaryIdx = -1;
            for (int i = 0; i < count; i++)
            {
                Seats s = seats[i];
                if (s != null && !s.hasUnitSet)
                {
                    openSeatCount++;
                    if (firstSecondaryIdx < 0)
                        firstSecondaryIdx = i;
                }
            }

            if (hasTurretSeat)
            {
                seatIndex = turretIdx;
                return true;
            }

            if (firstSecondaryIdx >= 0)
            {
                seatIndex = firstSecondaryIdx;
                return true;
            }

            return false;
        }

        private static bool TryGetAvailableDriverSeatIndex(Vehicle veh, out int driverSeatIndex)
        {
            driverSeatIndex = -1;
            if (veh == null || veh.seats == null)
                return false;

            var seats = veh.seats;
            int count = seats.Length;
            for (int i = 0; i < count; i++)
            {
                Seats s = seats[i];
                if (s != null && s.isDriver && !s.hasUnitSet)
                {
                    driverSeatIndex = i;
                    return true;
                }
            }
            return false;
        }

        [HarmonyPatch(typeof(Squad), nameof(Squad.SendUnitsToVehicle))]
        public static class Squad_SendUnitsToVehicle_Patch
        {
            public static void Prefix(Squad __instance, Vehicle __0)
            {
                try
                {
                    if (__instance != null && __instance.Pointer != IntPtr.Zero && __0 != null && __0.Pointer != IntPtr.Zero)
                    {
                        _squadOrderedToVehicle[__instance.Pointer] = __0.Pointer;
                    }
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(Squad), nameof(Squad.OrderLeaveVehicle))]
        public static class Squad_OrderLeaveVehicle_Patch
        {
            public static void Postfix(Squad __instance)
            {
                try
                {
                    if (__instance != null && __instance.Pointer != IntPtr.Zero)
                    {
                        _squadOrderedToVehicle.Remove(__instance.Pointer);
                    }
                }
                catch { }
            }
        }

        [ThreadStatic]
        internal static bool AllowExplicitSquadUnstallRelease;

        [HarmonyPatch(typeof(Squad), nameof(Squad.OrderLeaveAllVehiclesAndCovers))]
        public static class Squad_OrderLeaveAllVehiclesAndCovers_Patch
        {
            public static bool Prefix(Squad __instance, bool __0, bool __1, bool __2)
            {
                try
                {
                    // Guard SquadLeaderRoutine's 0.8s OrderLeaveAllVehiclesAndCovers(false, true, false) sweep so it
                    // never wipes active timed withdrawal/detour/pincer orders OR individual GOAP/stance cover locks
                    // (> 1.5x taskRadius from objective) before SeekEmergencyCover -> BandageInCover / Flank / Bound complete!
                    if (!VisionConfig.EnableMod.Value ||
                        AllowExplicitSquadUnstallRelease ||
                        __0 || !__1 || __2 ||
                        __instance == null ||
                        __instance.Pointer == IntPtr.Zero ||
                        __instance.IsPlayerInSquad())
                    {
                        return true;
                    }

                    float now = Time.time;
                    IntPtr sqPtr = __instance.Pointer;
                    if (__instance.timed_order_end > now ||
                        IsSquadActivelyRetreating(sqPtr, now) ||
                        (_aiFollowColumnUntil.TryGetValue(sqPtr, out float folUntil) && now < folUntil))
                    {
                        return false;
                    }

                    SquadTask curTask = __instance.currentTask;
                    var units = __instance.units;
                    if (curTask == null || units == null)
                        return true;

                    int count = units.Count;
                    bool anyProtected = false;
                    for (int i = 0; i < count; i++)
                    {
                        Soldier u = units[i];
                        if (u == null || u.Pointer == IntPtr.Zero || u.IsDead || !u.HasDestinationAssigned)
                            continue;

                        IntPtr uPtr = u.Pointer;
                        if (HasActiveGoapCoverLock(uPtr, now) ||
                            IsActivelyHeadingToEmplacement(u, now) ||
                            (AiCore.TryGetSoldierHandles(uPtr, out var pH) && now < pH.Stance.LocalCoverProtectedUntil))
                        {
                            anyProtected = true;
                            break;
                        }
                    }

                    if (!anyProtected)
                        return true;

                    for (int i = 0; i < count; i++)
                    {
                        Soldier u = units[i];
                        if (u == null || u.Pointer == IntPtr.Zero || u.IsDead || !AiCore.IsLocallyOwnedBot(u) || !u.HasDestinationAssigned)
                            continue;

                        IntPtr uPtr = u.Pointer;
                        AiCore.TryGetSoldierHandles(uPtr, out var uH);
                        if (HasActiveGoapCoverLock(uPtr, now) ||
                            IsActivelyHeadingToEmplacement(u, now) ||
                            (uH != null && now < uH.Stance.LocalCoverProtectedUntil))
                        {
                            continue;
                        }

                        AiDestination td = u.targetDestination;
                        if (td == null || td.Pointer == IntPtr.Zero)
                            continue;

                        if (td.TryCast<Vehicle>() != null ||
                            td.TryCast<VehicleRepairTask>() != null ||
                            td.TryCast<MedicHealingDestination>() != null)
                        {
                            continue;
                        }

                        if (!curTask.IsInsideTaskArea(td.GetCoverPosition(), 1.5f))
                        {
                            if (uH != null && uH.Ai != null)
                                uH.Ai.Stop(false);
                            u.CoverPosition(null);
                            AiCore.InvalidateTargetDestinationCache(u);
                        }
                    }

                    return false;
                }
                catch { }
                return true;
            }

            public static void Postfix(Squad __instance)
            {
                try
                {
                    if (__instance != null && __instance.Pointer != IntPtr.Zero)
                    {
                        _squadOrderedToVehicle.Remove(__instance.Pointer);
                    }
                }
                catch { }
            }
        }

        /// <summary>
        /// Prevents CoverManager / Squad.SendUnitsToCovers from treating driverless movable vehicles
        /// (abandoned trucks, halftracks, jeeps, tanks), player-occupied movable vehicles, or full emplacements as valid cover.
        /// Allows both Gunner and Loader/Spotter seats on static emplacements.
        /// </summary>
        [HarmonyPatch(typeof(Vehicle), nameof(Vehicle.IsCoverAvailable))]
        public static class Vehicle_IsCoverAvailable_Patch
        {
            public static void Postfix(Vehicle __instance, ref bool __result)
            {
                try
                {
                    if (!__result || !VisionConfig.EnableMod.Value || __instance == null)
                        return;

                    if (__instance.TryCast<MovableVehicle>() != null)
                    {
                        if (!__instance.HasDriver() || __instance.PlayerIsInside())
                        {
                            __result = false;
                            return;
                        }
                        if (!TryGetAvailableTurretSeatIndex(__instance, out _))
                        {
                            __result = false;
                        }
                        return;
                    }

                    if (!TryGetAvailableEmplacementOrTurretSeatIndex(__instance, out _, out _))
                    {
                        __result = false;
                    }
                }
                catch { }
            }
        }

        /// <summary>
        /// In vanilla (0x18085FD40), Vehicle.IsCoverOccupied returns false as long as ANY seat (including non-turret
        /// passenger seats on movable vehicles) is empty, causing extra foot soldiers to climb into non-turret seats.
        /// Mark movable vehicles as occupied as soon as all turret seats are filled (or if driverless or player-occupied),
        /// while allowing static emplacements to fill both Gunner and Loader seats.
        /// </summary>
        [HarmonyPatch(typeof(Vehicle), nameof(Vehicle.IsCoverOccupied))]
        public static class Vehicle_IsCoverOccupied_Patch
        {
            public static void Postfix(Vehicle __instance, ref bool __result)
            {
                try
                {
                    if (__result || !VisionConfig.EnableMod.Value || __instance == null)
                        return;

                    if (__instance.TryCast<MovableVehicle>() != null)
                    {
                        if (!__instance.HasDriver() || __instance.PlayerIsInside())
                        {
                            __result = true;
                            return;
                        }
                        if (!TryGetAvailableTurretSeatIndex(__instance, out _))
                        {
                            __result = true;
                        }
                        return;
                    }

                    if (!TryGetAvailableEmplacementOrTurretSeatIndex(__instance, out _, out _))
                    {
                        __result = true;
                    }
                }
                catch { }
            }
        }

        /// <summary>
        /// In vanilla, Vehicle.OnCoverReached (0x180860560) calls GetOnVehicleBestPos (0x18085E430 / 0x18087B070),
        /// which places AI foot soldiers into whichever empty seat is first or physically closest — including
        /// driver seats and non-turret passenger/loader seats on movable vehicles!
        /// This patch ensures:
        ///   1. Regular AI foot soldiers NEVER climb into driverless movable vehicles, player-driven movable vehicles
        ///      (unless in the player's squad), or non-turret seats on movable vehicles.
        ///   2. Static emplacements fill the Gunner seat first, then the Loader/Assistant seat (`TryGetAvailableEmplacementOrTurretSeatIndex`).
        ///   3. Bailed-out Vehicle Crews (`sq.IsVehicleCrew`) re-mounting an unmanned combat vehicle always fill
        ///      the driver seat first (`TryGetAvailableDriverSeatIndex`), then turret seats.
        /// </summary>
        [HarmonyPatch(typeof(Vehicle), nameof(Vehicle.OnCoverReached))]
        public static class Vehicle_OnCoverReached_Patch
        {
            public static bool Prefix(Vehicle __instance, Soldier __0)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value || __instance == null || __0 == null || !__0.IsAI())
                        return true;

                    if (__instance.IsCoverDestroyed() || __instance.IsDisabled() || __instance.IsLocked() || HasHostileVehicleOccupant(__instance, __0.faction))
                    {
                        ClearEmplacementAssignment(__0.Pointer, markUnreachable: true, 120.0f);
                        __0.CoverPosition(null);
                        return false;
                    }

                    Squad sq = __0.joinedSquad;
                    bool isPlayerSquad = sq != null && sq.IsPlayerInSquad();
                    if (isPlayerSquad)
                    {
                        ClearEmplacementAssignment(__0.Pointer, markUnreachable: false);
                        return true;
                    }

                    bool isExplicitBoardOrder = sq != null &&
                                                _squadOrderedToVehicle.TryGetValue(sq.Pointer, out IntPtr orderedVehPtr) &&
                                                orderedVehPtr == __instance.Pointer;
                    bool isVehicleCrew = sq != null && AiCore.IsTrueVehicleCrewSquad(sq);
                    bool isMovable = __instance.TryCast<MovableVehicle>() != null;

                    // Prevent non-squad AI foot soldiers from climbing uninvited into a human player's movable vehicle!
                    if (isMovable && __instance.PlayerIsInside() && !isExplicitBoardOrder)
                    {
                        ClearEmplacementAssignment(__0.Pointer, markUnreachable: true, 60.0f);
                        __0.CoverPosition(null);
                        return false;
                    }

                    if (isVehicleCrew && isMovable)
                    {
                        if (!__instance.HasDriver() && TryGetAvailableDriverSeatIndex(__instance, out int driverIdx))
                        {
                            ClearEmplacementAssignment(__0.Pointer, markUnreachable: false);
                            __instance.GetOnVehicle(driverIdx, __0);
                            return false;
                        }
                        if (TryGetAvailableTurretSeatIndex(__instance, out int crewTurretIdx))
                        {
                            ClearEmplacementAssignment(__0.Pointer, markUnreachable: false);
                            __instance.GetOnVehicle(crewTurretIdx, __0);
                            return false;
                        }
                        ClearEmplacementAssignment(__0.Pointer, markUnreachable: false);
                        return true;
                    }

                    if (isExplicitBoardOrder)
                    {
                        ClearEmplacementAssignment(__0.Pointer, markUnreachable: false);
                        return true;
                    }

                    // Regular AI foot soldier reaching a Vehicle as cover/emplacement:
                    // 1. Never climb into a driverless movable vehicle (truck, halftrack, jeep, tank)
                    if (isMovable && !__instance.HasDriver())
                    {
                        ClearEmplacementAssignment(__0.Pointer, markUnreachable: true, 60.0f);
                        __0.CoverPosition(null);
                        return false;
                    }

                    // 2. On movable vehicles, only board an open turret seat; on static emplacements,
                    //    board the Gunner seat first and the Loader/Assistant seat second!
                    if (TryGetAvailableEmplacementOrTurretSeatIndex(__instance, out int targetSeatIdx, out _))
                    {
                        ClearEmplacementAssignment(__0.Pointer, markUnreachable: false);
                        __instance.GetOnVehicle(targetSeatIdx, __0);
                        return false;
                    }

                    ClearEmplacementAssignment(__0.Pointer, markUnreachable: true, 45.0f);
                    __0.CoverPosition(null);
                    return false;
                }
                catch { }
                return true;
            }
        }

        /// <summary>
        /// Pauses native AIVehicle.SwitchSeatAi (0x18057C740) whenever a human player is inside the vehicle
        /// so AI crewmen never musical-chairs swap seats or hijack the gunner/driver seat away from the player.
        /// </summary>
        [HarmonyPatch(typeof(AIVehicle), nameof(AIVehicle.SwitchSeatAi))]
        public static class AIVehicle_SwitchSeatAi_Patch
        {
            public static bool Prefix(Vehicle __1)
            {
                try
                {
                    if (VisionConfig.EnableMod.Value && __1 != null && __1.PlayerIsInside())
                        return false;
                }
                catch { }
                return true;
            }
        }

        /// <summary>
        /// In vanilla PlayerController.SwitchSeat(int seatIndex) (0x1807D4EC1), multiplayer clients are blocked
        /// from switching into a seat (`!targetSeat.unitSet.GetSyncher().IsMine`) even when the occupant is an AI bot
        /// owned by the Host. Allow human players to always swap seats with AI bots (`occupant.IsAI() && !occupant.IsPlayer()`).
        /// </summary>
        [HarmonyPatch(typeof(PlayerController), nameof(PlayerController.SwitchSeat), new Type[] { typeof(int) })]
        public static class PlayerController_SwitchSeat_Patch
        {
            public static void Postfix(PlayerController __instance, int __0, ref bool __result)
            {
                try
                {
                    if (__result || !VisionConfig.EnableMod.Value || __instance == null)
                        return;

                    Vehicle veh = __instance.ControlledVehicle;
                    if (veh == null || veh.seats == null || __0 < 0 || __0 >= veh.seats.Length)
                        return;

                    if (veh.IsLocked() || veh.IsCoverDestroyed() || veh.IsDisabled() || __instance.selectedVehicleTurret == __0)
                        return;

                    Soldier playerSoldier = __instance.ControlledCharacter;
                    if (playerSoldier == null || !playerSoldier.IsAlive || !playerSoldier.CanSwicthSeat())
                        return;

                    Seats targetSeat = veh.seats[__0];
                    if (targetSeat == null || !targetSeat.hasUnitSet)
                        return;

                    Soldier occupant = targetSeat.unitSet;
                    if (occupant == null || !occupant.IsAI() || occupant.IsPlayer())
                        return;

                    __instance.selectedVehicleTurret = __0;
                    Turret turret = targetSeat.connectedTurret;
                    if (VehicleGUI.instance != null)
                    {
                        VehicleGUI.instance.SelectTurret(turret, veh.name);
                    }
                    playerSoldier.SwitchSeat(targetSeat);
                    __result = true;
                }
                catch { }
            }
        }

        /// <summary>
        /// When a remote client human player switches into a vehicle seat occupied by a Host-owned AI bot,
        /// the Host receives `SyncSoldier.SyncUnitOnVehicle` -> `Vehicle.GetOnVehicle(seatIndex, playerSoldier)`.
        /// Relocate the displaced locally-owned AI bot into the player's previous seat (broadcasting its new seat
        /// across Photon) so both Host and Clients stay 100% synchronized.
        /// </summary>
        [HarmonyPatch(typeof(Vehicle), nameof(Vehicle.GetOnVehicle))]
        public static class Vehicle_GetOnVehicle_Patch
        {
            public struct SeatSwapState
            {
                public Soldier DisplacedBot;
                public int OldSeatIdx;
            }

            public static void Prefix(Vehicle __instance, int __0, Soldier __1, out SeatSwapState __state)
            {
                __state = default;
                __state.OldSeatIdx = -1;
                try
                {
                    if (!VisionConfig.EnableMod.Value || __instance == null || __1 == null || __1.IsAI())
                        return;

                    var seats = __instance.seats;
                    if (seats == null || __0 < 0 || __0 >= seats.Length)
                        return;

                    Seats targetSeat = seats[__0];
                    Soldier existing = targetSeat != null ? targetSeat.unitSet : null;
                    if (existing == null || existing.Pointer == __1.Pointer || !AiCore.IsLocallyOwnedBot(existing))
                        return;

                    int oldIdx = -1;
                    if (__1.IsOnVehicle())
                    {
                        Vehicle curVeh = __1.GetCurrentVehicle();
                        if (curVeh != null && curVeh.Pointer == __instance.Pointer)
                        {
                            Seats oldSeat = __1.GetCurrentVehicleSeat();
                            if (oldSeat != null)
                                oldIdx = oldSeat.GetSeatIndexInVehicle();
                        }
                    }

                    if (oldIdx < 0)
                    {
                        for (int i = 0; i < seats.Length; i++)
                        {
                            if (i != __0 && seats[i] != null && !seats[i].hasUnitSet)
                            {
                                oldIdx = i;
                                break;
                            }
                        }
                    }

                    __state.DisplacedBot = existing;
                    __state.OldSeatIdx = oldIdx;
                }
                catch { }
            }

            public static void Postfix(Vehicle __instance, int __0, Soldier __1, SeatSwapState __state)
            {
                try
                {
                    if (__instance != null && __instance.Pointer != IntPtr.Zero &&
                        __1 != null && !string.IsNullOrEmpty(__1.faction) && __1.faction != "Civilian")
                    {
                        _lastKnownVehicleFaction[__instance.Pointer] = __1.faction;
                        RegisterVehicleCrewSquad(__instance, __1);
                    }

                    if (__state.DisplacedBot == null || !__state.DisplacedBot.IsAlive || __instance == null)
                        return;

                    if (__state.OldSeatIdx >= 0 && __state.OldSeatIdx != __0 &&
                        __instance.seats != null && __state.OldSeatIdx < __instance.seats.Length)
                    {
                        __instance.GetOnVehicle(__state.OldSeatIdx, __state.DisplacedBot);
                    }
                }
                catch { }
            }
        }

        /// <summary>
        /// Vanilla LostVehicles Casualty Attribution Fix:
        /// In vanilla OnDisableVehicle, BattleManager.OnTankKilled(GetVehicleFaction()) fails whenever the blast/hit
        /// kills the last seated crewman first or the crew bailed out seconds before cook-off (GetVehicleFaction() returns null/"").
        /// Cache the last non-civilian crew faction per vehicle and credit BattleManager.OnTankKilled once on MasterClient!
        /// </summary>
        private static void TryCreditDestroyedVehicleCasualty(Vehicle veh, bool hadNoLiveCrewFaction)
        {
            if (!VisionConfig.EnableMod.Value ||
                !AiCore.IsMasterClient() ||
                veh == null ||
                veh.Pointer == IntPtr.Zero ||
                veh.TryCast<MovableVehicle>() == null)
            {
                return;
            }

            // Always mark destroyed movable vehicles as seen so subsequent hits on a burning wreck never double-count
            if (!_creditedDestroyedVehicles.Add(veh.Pointer))
                return;

            if (!hadNoLiveCrewFaction)
                return;

            if (_lastKnownVehicleFaction.TryGetValue(veh.Pointer, out string lastFaction) &&
                !string.IsNullOrEmpty(lastFaction) &&
                lastFaction != "Civilian")
            {
                BattleManager.OnTankKilled(lastFaction);
                AiCore.LogDebug("CasualtyFix", $"Credited destroyed {lastFaction} vehicle '{veh.name}' (crew already dead/bailed).");
            }
        }

        [HarmonyPatch(typeof(Vehicle), nameof(Vehicle.OnDisableVehicle))]
        public static class Vehicle_OnDisableVehicle_Patch
        {
            public static bool Prefix(Vehicle __instance, out bool __state)
            {
                __state = false;
                try
                {
                    if (__instance != null)
                    {
                        if (__instance.IsDisabled() &&
                            VisionConfig.EnableMod.Value &&
                            VisionConfig.EnableMultiplayerBattleStatSyncFix.Value)
                        {
                            return false;
                        }

                        if (!__instance.IsDisabled())
                        {
                            string curFac = __instance.GetVehicleFaction();
                            __state = string.IsNullOrEmpty(curFac) || curFac == "Civilian";
                        }
                    }
                }
                catch { }
                return true;
            }

            public static void Postfix(Vehicle __instance, bool __state)
            {
                try
                {
                    if (__instance != null && __instance.IsDisabled())
                    {
                        TryCreditDestroyedVehicleCasualty(__instance, __state);
                        TryCreditEnemyVehicleDisableToLocalPlayer(__instance, directPlayerHit: false);
                    }
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(MovableVehicle), nameof(MovableVehicle.OnDisableVehicle))]
        public static class MovableVehicle_OnDisableVehicle_Patch
        {
            public static bool Prefix(MovableVehicle __instance, out bool __state)
            {
                __state = false;
                try
                {
                    if (__instance != null)
                    {
                        if (__instance.IsDisabled() &&
                            VisionConfig.EnableMod.Value &&
                            VisionConfig.EnableMultiplayerBattleStatSyncFix.Value)
                        {
                            return false;
                        }

                        if (!__instance.IsDisabled())
                        {
                            string curFac = __instance.GetVehicleFaction();
                            __state = string.IsNullOrEmpty(curFac) || curFac == "Civilian";
                        }
                    }
                }
                catch { }
                return true;
            }

            public static void Postfix(MovableVehicle __instance, bool __state)
            {
                try
                {
                    if (__instance != null && __instance.IsDisabled())
                    {
                        TryCreditDestroyedVehicleCasualty(__instance, __state);
                        TryCreditEnemyVehicleDisableToLocalPlayer(__instance, directPlayerHit: false);
                    }
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(Vehicle), nameof(Vehicle.OnRepaired))]
        public static class Vehicle_OnRepaired_Patch
        {
            public static void Postfix(Vehicle __instance)
            {
                try
                {
                    if (__instance != null && __instance.Pointer != IntPtr.Zero)
                    {
                        _creditedDestroyedVehicles.Remove(__instance.Pointer);
                        _vehiclesCrewRetreatedFrom.Remove(__instance.Pointer);
                        NotifyVehicleRepairedForPtfo(__instance.Pointer);
                    }
                }
                catch { }
            }
        }

        private static void RegisterVehicleCrewSquad(Vehicle veh, Soldier occupant)
        {
            if (veh == null || veh.Pointer == IntPtr.Zero || occupant == null || occupant.Pointer == IntPtr.Zero)
                return;

            Squad sq = occupant.joinedSquad;
            if (sq == null || sq.Pointer == IntPtr.Zero || sq.isChargeSquad || veh.IsTransportVehicle())
                return;

            AiCore.VehicleFastHandles vh = AiCore.GetOrCreateVehicleHandles(veh);
            if (vh == null || vh.IsParachute)
                return;

            if (vh.IsPlane || vh.IsAir)
            {
                if (sq.isOpenForJoiners)
                {
                    var units = sq.units;
                    int uCount = units != null ? units.Count : 0;
                    Vector3 vPos = veh.transform != null ? veh.transform.position : Vector3.zero;
                    for (int i = 0; i < uCount; i++)
                    {
                        Soldier u = units[i];
                        if (u == null || u.Pointer == IntPtr.Zero || !u.IsAlive)
                            continue;
                        if (u.IsMedic() || u.hasMg || u.IsRadioman() || u.isAtUnit)
                            return;
                        if (!u.IsOnVehicle() && u.transform != null && (u.transform.position - vPos).sqrMagnitude > 40.0f * 40.0f)
                            return;
                    }
                }

                AiCore.MarkConfirmedPlaneCrewSquad(sq);
                _lastKnownVehicleCrewSquad[veh.Pointer] = sq;
                return;
            }

            if (sq.isOpenForJoiners)
                return;

            if (vh.IsTank)
            {
                AiCore.MarkConfirmedVehicleCrewSquad(sq);
                _lastKnownVehicleCrewSquad[veh.Pointer] = sq;
                return;
            }

            VehicleWithWheels wheeled = veh.TryCast<VehicleWithWheels>();
            if (wheeled != null && wheeled.RetreatCrewOnDestroy() && !wheeled.IsTransportVehicle())
            {
                AiCore.MarkConfirmedVehicleCrewSquad(sq);
                _lastKnownVehicleCrewSquad[veh.Pointer] = sq;
            }
        }

        private static void RegisterPlaneCrewFromSeats(VehiclePlane vp)
        {
            if (vp == null || vp.Pointer == IntPtr.Zero || vp.IsTransportVehicle() || vp.seats == null)
                return;

            var seats = vp.seats;
            int len = seats.Length;
            for (int i = 0; i < len; i++)
            {
                Seats s = seats[i];
                Soldier u = s != null ? s.unitSet : null;
                if (u != null && u.Pointer != IntPtr.Zero)
                {
                    if (!string.IsNullOrEmpty(u.faction) && u.faction != "Civilian")
                        _lastKnownVehicleFaction[vp.Pointer] = u.faction;
                    RegisterVehicleCrewSquad(vp, u);
                }
            }
        }

        internal static bool AssignCrewSquadRetreatTask(
            Squad crewSq,
            string faction,
            Vector3 originPos,
            bool allowWaterSurface,
            bool replaceExistingIfFar)
        {
            if (crewSq == null || crewSq.Pointer == IntPtr.Zero || crewSq.CountMembers <= 0)
                return false;

            MissionTaskRetreat existingRet = crewSq.currentTask != null
                ? crewSq.currentTask.TryCast<MissionTaskRetreat>()
                : null;
            if (existingRet != null)
            {
                if (!replaceExistingIfFar)
                    return false;

                Vector3 curDest = existingRet.GetTaskDestination();
                Vector3 dFlat = curDest - originPos;
                dFlat.y = 0f;
                if (dFlat.sqrMagnitude <= 210.0f * 210.0f)
                    return false;

                try
                {
                    if (existingRet.gui != null && existingRet.gui.gameObject != null)
                        UnityEngine.Object.Destroy(existingRet.gui.gameObject);
                    existingRet.RemoveTaskForever();
                }
                catch { }
            }
            else if (IsSquadOnRetreatTask(crewSq))
            {
                return false;
            }

            if (string.IsNullOrEmpty(faction) || faction == "Civilian")
            {
                var units = crewSq.units;
                int uCount = units != null ? units.Count : 0;
                for (int i = 0; i < uCount; i++)
                {
                    Soldier u = units[i];
                    if (u != null && u.Pointer != IntPtr.Zero && !string.IsNullOrEmpty(u.faction) && u.faction != "Civilian")
                    {
                        faction = u.faction;
                        break;
                    }
                }
            }

            if (string.IsNullOrEmpty(faction) || faction == "Civilian")
                return false;

            if (!AiCore.TryComputeCrewRetreatDestination(faction, originPos, out Vector3 retreatPos, allowWaterSurface))
                return false;

            var retTask = new MissionTaskRetreat(retreatPos, crewSq, false);
            crewSq.currentTask = retTask.Cast<SquadTask>();
            if (crewSq.IsPlayerInSquad())
            {
                RecordNotifiedPlayerSquadTask(crewSq.currentTask);
                NotifyPlayerSquadTaskUpdated(crewSq);
                AiCore.TryShowLocalPlayerRadioHint(
                    crewSq,
                    $"Squad Order: Retreat to friendly lines \u2014 {AiCore.FormatRelativeBearingAndDistance(originPos, retreatPos)}",
                    requireLeader: false,
                    minInterval: 8.0f);
            }

            Soldier leader = crewSq.Leader;
            if (!crewSq.IsPlayerInSquad() && leader != null && leader.IsAlive && AiCore.IsLocallyOwnedBot(leader))
            {
                TrySayTacticalVoice(leader, crewSq, VoiceManager.VoiceClip.timeToRetreat, 0.25f);
                crewSq.HoldArea(retreatPos, 15.0f, false);
            }

            if (VisionConfig.DebugLogging.Value)
            {
                AiCore.LogDebug(
                    "CrewBailout",
                    $"{faction} crew '{crewSq.squadName}' assigned Retreat ({Vector3.Distance(originPos, retreatPos):F0}m rearward, playerSquad={crewSq.IsPlayerInSquad()}).");
            }

            return true;
        }

        internal static bool HandlePlaneRetreatCrew(AIPlane aiPlane)
        {
            if (aiPlane == null)
                return true;

            VehiclePlane veh = aiPlane.veh;
            if (veh == null || veh.Pointer == IntPtr.Zero)
                return true;

            // Replace vanilla AIPlane.RetreatCrew (0x180572ED0), which:
            //   1) Crashes with NullReferenceException on all sp_ME_ / V2 maps where BattleManager.instance.currentPhase == null,
            //   2) Silently skips MissionTaskRetreat over water/ocean or outside Terrain bounds, and
            //   3) Computes the retreat point mid-air before parachutes drift 200m-500m away!
            _vehiclesCrewRetreatedFrom.Add(veh.Pointer);

            Soldier sampleCrew = null;
            var seats = veh.seats;
            if (seats != null)
            {
                int seatCount = seats.Length;
                for (int i = 0; i < seatCount; i++)
                {
                    Seats seat = seats[i];
                    Soldier unit = seat != null ? seat.unitSet : null;
                    if (unit == null || unit.Pointer == IntPtr.Zero)
                        continue;

                    if (sampleCrew == null)
                        sampleCrew = unit;

                    RegisterVehicleCrewSquad(veh, unit);

                    Syncher syn = unit.GetSyncher();
                    if (syn == null || !syn.OwnSyncher)
                        continue;

                    unit.CoverPosition(null);
                    if (AiCore.TryGetSoldierHandles(unit.Pointer, out var unitHandles))
                        unitHandles.Stance.LocalCoverProtectedUntil = 0f;
                    ClearEmplacementAssignment(unit.Pointer, markUnreachable: false);

                    if (unit.IsAI() && unit.IsAlive && !unit.IsInjured && !unit.IsDead)
                    {
                        BotActionTrace.RecordEvent(unit, BotTraceEventKind.VehicleBailout, "disabled_vehicle_bailout");
                        veh.GetOffVehicle(unit);
                    }
                }
            }

            if (veh.IsTransportVehicle())
                return false;

            Squad crewSq = sampleCrew != null ? sampleCrew.joinedSquad : null;
            if ((crewSq == null || crewSq.Pointer == IntPtr.Zero) &&
                _lastKnownVehicleCrewSquad.TryGetValue(veh.Pointer, out Squad cachedSq) &&
                cachedSq != null &&
                cachedSq.Pointer != IntPtr.Zero)
            {
                crewSq = cachedSq;
            }

            if (crewSq != null)
            {
                AiCore.MarkConfirmedPlaneCrewSquad(crewSq);

                // If the plane crashed/burned on the ground, assign Retreat immediately.
                // If airborne, UpdateGroundedCrewRetreatTasks() assigns Retreat right where the parachutes actually touch down!
                if (veh.isGrounded && veh.transform != null)
                {
                    string fac = sampleCrew != null ? sampleCrew.faction : null;
                    if (string.IsNullOrEmpty(fac))
                        _lastKnownVehicleFaction.TryGetValue(veh.Pointer, out fac);
                    AssignCrewSquadRetreatTask(crewSq, fac, veh.transform.position, allowWaterSurface: true, replaceExistingIfFar: true);
                }
            }

            return false;
        }

        private static bool HasIntactFlyablePlaneNearby(Vector3 pos, float maxRadius)
        {
            var allVehicles = Vehicle.allVehicles;
            if (allVehicles == null)
                return false;

            float rSq = maxRadius * maxRadius;
            int vCount = allVehicles.Count;
            for (int i = 0; i < vCount; i++)
            {
                Vehicle veh = allVehicles[i];
                if (veh == null || veh.Pointer == IntPtr.Zero || veh.transform == null || veh.IsDisabled() || _vehiclesCrewRetreatedFrom.Contains(veh.Pointer))
                    continue;

                AiCore.VehicleFastHandles vh = AiCore.GetOrCreateVehicleHandles(veh);
                if (vh == null || (!vh.IsPlane && !vh.IsAir) || vh.IsParachute || veh.IsTransportVehicle())
                    continue;

                if (!veh.CanFight())
                    continue;

                Vector3 d = veh.transform.position - pos;
                d.y = 0f;
                if (d.sqrMagnitude <= rSq)
                    return true;
            }

            return false;
        }

        private static bool IsNearFriendlyAirfieldOrSpawn(string faction, Vector3 pos)
        {
            if (!string.IsNullOrEmpty(faction) &&
                AiCore.TryGetNearestFriendlySpawnPosition(faction, pos, out Vector3 spawnPos))
            {
                Vector3 d = spawnPos - pos;
                d.y = 0f;
                if (d.sqrMagnitude <= 260.0f * 260.0f)
                    return true;
            }

            int rsCount = _activeRepairStations.Count;
            for (int i = 0; i < rsCount; i++)
            {
                RepairStation rs = _activeRepairStations[i];
                if (rs == null || rs.Pointer == IntPtr.Zero || rs.transform == null)
                    continue;

                Vector3 d = rs.transform.position - pos;
                d.y = 0f;
                if (d.sqrMagnitude <= 200.0f * 200.0f)
                    return true;
            }

            return false;
        }

        private static void EvaluateGroundedAircrewSquad(Squad sq)
        {
            if (sq == null || sq.Pointer == IntPtr.Zero || sq.CountMembers <= 0)
                return;

            Soldier localPlayerGrounded = null;
            Vector3 groundSum = Vector3.zero;
            int groundedCount = 0;
            bool anyAliveInActivePlane = false;
            bool anyAliveInParachute = false;
            bool localPlayerInPlaneOrChute = false;
            string faction = null;

            var units = sq.units;
            int uCount = units != null ? units.Count : 0;
            for (int uIdx = 0; uIdx < uCount; uIdx++)
            {
                Soldier u = units[uIdx];
                if (u == null || u.Pointer == IntPtr.Zero || !u.IsAlive || u.transform == null)
                    continue;

                if (string.IsNullOrEmpty(faction) && !string.IsNullOrEmpty(u.faction) && u.faction != "Civilian")
                    faction = u.faction;

                bool isLocalPlayer = u.IsPlayer();
                if (u.IsOnVehicle())
                {
                    Vehicle curVeh = u.GetCurrentVehicle();
                    AiCore.VehicleFastHandles cvh = AiCore.GetOrCreateVehicleHandles(curVeh);
                    if (cvh != null && cvh.IsParachute)
                    {
                        anyAliveInParachute = true;
                        if (isLocalPlayer)
                            localPlayerInPlaneOrChute = true;
                        continue;
                    }

                    if (curVeh != null && !curVeh.IsDisabled() && !_vehiclesCrewRetreatedFrom.Contains(curVeh.Pointer))
                    {
                        anyAliveInActivePlane = true;
                        if (isLocalPlayer)
                            localPlayerInPlaneOrChute = true;
                    }
                    continue;
                }

                Vector3 uPos = u.transform.position;
                float groundY = Mathf.Max(0f, ResourcesManager.GetTerrainAltitude(uPos, 0f));
                if (uPos.y > groundY + 18.0f &&
                    !AiCore.RaycastEnvironment(uPos + Vector3.up * 0.8f, Vector3.down, 18.0f, u.transform.root, out _))
                {
                    anyAliveInParachute = true;
                    if (isLocalPlayer)
                        localPlayerInPlaneOrChute = true;
                    continue;
                }

                groundSum += uPos;
                groundedCount++;
                if (isLocalPlayer)
                    localPlayerGrounded = u;
            }

            if (groundedCount == 0 || localPlayerInPlaneOrChute)
                return;

            if (localPlayerGrounded == null && (anyAliveInActivePlane || anyAliveInParachute))
                return;

            Vector3 groundPos = localPlayerGrounded != null
                ? localPlayerGrounded.transform.position
                : (groundSum / groundedCount);

            if (HasIntactFlyablePlaneNearby(groundPos, 45.0f) && IsNearFriendlyAirfieldOrSpawn(faction, groundPos))
                return;

            AssignCrewSquadRetreatTask(sq, faction, groundPos, allowWaterSurface: true, replaceExistingIfFar: true);
        }

        private static void EvaluateGroundedLocalPlayerTankCrew(Soldier localPlayer, Squad sq)
        {
            if (localPlayer == null ||
                sq == null ||
                !localPlayer.IsAlive ||
                localPlayer.IsOnVehicle() ||
                localPlayer.transform == null ||
                IsSquadOnRetreatTask(sq) ||
                sq.SomeoneIsRepairing())
            {
                return;
            }

            var allVehicles = Vehicle.allVehicles;
            if (allVehicles == null)
                return;

            Vector3 pPos = localPlayer.transform.position;
            Soldier mech = sq.GetMechanic();
            bool squadHasMechanic = mech != null && mech.IsAlive && mech.GetRepairTools() != null;
            bool hasDisabledCrewVehNearby = false;

            int vCount = allVehicles.Count;
            for (int i = 0; i < vCount; i++)
            {
                Vehicle veh = allVehicles[i];
                if (veh == null || veh.Pointer == IntPtr.Zero || veh.transform == null || veh.IsAirVehicle() || veh.IsTransportVehicle())
                    continue;

                float dSq = (veh.transform.position - pPos).sqrMagnitude;
                if (dSq > 65.0f * 65.0f)
                    continue;

                if (_vehiclesCrewRetreatedFrom.Contains(veh.Pointer) ||
                    (veh.IsDisabled() && (!veh.CanBeRepaired() || !squadHasMechanic)))
                {
                    if (_lastKnownVehicleCrewSquad.TryGetValue(veh.Pointer, out Squad knownSq) &&
                        knownSq != null &&
                        knownSq.Pointer == sq.Pointer)
                    {
                        hasDisabledCrewVehNearby = true;
                    }
                    continue;
                }

                if (dSq <= 45.0f * 45.0f &&
                    !veh.IsLocked() &&
                    !veh.IsCoverDestroyed() &&
                    (!veh.IsDisabled() || (veh.CanBeRepaired() && squadHasMechanic)))
                {
                    string vf = GetVehicleCrewFaction(veh);
                    if (string.IsNullOrEmpty(vf) || vf == "Civilian" || vf == localPlayer.faction)
                        return;
                }
            }

            if (hasDisabledCrewVehNearby)
            {
                AssignCrewSquadRetreatTask(sq, localPlayer.faction, pPos, allowWaterSurface: false, replaceExistingIfFar: false);
            }
        }

        private static void EvaluateGroundedAiTankCrewSquad(Squad sq)
        {
            if (sq == null ||
                sq.units == null ||
                sq.units.Count == 0 ||
                AiCore.IsConfirmedPlaneCrewSquad(sq) ||
                !AiCore.IsTrueVehicleCrewSquad(sq) ||
                IsSquadOnRetreatTask(sq) ||
                sq.SomeoneIsRepairing())
            {
                return;
            }

            Soldier sampleCrew = null;
            int uCount = sq.units.Count;
            for (int i = 0; i < uCount; i++)
            {
                Soldier u = sq.units[i];
                if (u == null || u.Pointer == IntPtr.Zero || u.IsDead || u.HasSurrended() || u.transform == null)
                    continue;

                if (u.IsOnVehicle() || u.IsReachingVehicleToMount() || u.IsEnteringVehicle || u.VehicleToOperate() != null)
                    return;

                if (sampleCrew == null && !u.IsInjured)
                    sampleCrew = u;
            }

            if (sampleCrew == null)
                return;

            Vector3 crewPos = sampleCrew.transform.position;
            Soldier mech = sq.GetMechanic();
            bool squadHasMechanic = mech != null && mech.IsAlive && mech.GetRepairTools() != null;
            string myFaction = AiCore.GetCachedFaction(sampleCrew);

            var allVehicles = Vehicle.allVehicles;
            if (allVehicles != null)
            {
                int vCount = allVehicles.Count;
                for (int i = 0; i < vCount; i++)
                {
                    Vehicle veh = allVehicles[i];
                    if (veh == null || veh.Pointer == IntPtr.Zero || veh.transform == null || veh.IsAirVehicle() || veh.IsTransportVehicle())
                        continue;

                    if (_vehiclesCrewRetreatedFrom.Contains(veh.Pointer) || veh.IsLocked() || veh.IsCoverDestroyed())
                        continue;

                    if (veh.IsDisabled() && (!veh.CanBeRepaired() || !squadHasMechanic))
                        continue;

                    if ((veh.transform.position - crewPos).sqrMagnitude > 48.0f * 48.0f)
                        continue;

                    string vf = GetVehicleCrewFaction(veh);
                    if (string.IsNullOrEmpty(vf) || vf == "Civilian" || AiCore.AreSameSideFactions(myFaction, vf))
                        return;
                }
            }

            // Allow local self-defense if enemy soldiers are already within close-quarters 24m
            AiCore.EnsureWorldSnapshot();
            var soldiers = AiCore.WorldSoldiers;
            int sCount = AiCore.WorldSoldierCount;
            for (int i = 0; i < sCount; i++)
            {
                ref AiCore.SoldierSnapshot sSnap = ref soldiers[i];
                if (!sSnap.HasSurrendered &&
                    !sSnap.IsInjured &&
                    AiCore.AreEnemyFactions(myFaction, sSnap.Faction) &&
                    (sSnap.Pos - crewPos).sqrMagnitude <= 24.0f * 24.0f)
                {
                    return;
                }
            }

            AssignCrewSquadRetreatTask(sq, myFaction, crewPos, allowWaterSurface: false, replaceExistingIfFar: false);
        }

        public static void UpdateGroundedCrewRetreatTasks()
        {
            if (!VisionConfig.EnableMod.Value)
                return;

            float now = Time.time;
            if (now < _nextGroundedCrewRetreatCheckTime)
                return;
            _nextGroundedCrewRetreatCheckTime = now + 1.0f;

            try
            {
                AiCore.FlushExpiredRetreatDespawningSoldiers(now);
                IntPtr localPlayerSqPtr = IntPtr.Zero;
                PlayerController pc = PlayerController.currentController;
                Soldier localPlayer = pc != null ? pc.ControlledCharacter : null;
                if (localPlayer != null && localPlayer.Pointer != IntPtr.Zero && localPlayer.IsAlive)
                {
                    Squad pSq = localPlayer.joinedSquad;
                    if (pSq != null && pSq.Pointer != IntPtr.Zero)
                    {
                        localPlayerSqPtr = pSq.Pointer;
                        if (AiCore.IsConfirmedPlaneCrewSquad(pSq))
                        {
                            EvaluateGroundedAircrewSquad(pSq);
                        }
                        else if (AiCore.IsTrueVehicleCrewSquad(pSq))
                        {
                            EvaluateGroundedLocalPlayerTankCrew(localPlayer, pSq);
                        }
                    }
                }

                AiCore.EnsureWorldSnapshot();
                int sqCount = AiCore.WorldSquadCount;
                var squads = AiCore.WorldSquads;
                for (int i = 0; i < sqCount; i++)
                {
                    Squad sq = squads[i].Ref;
                    if (sq == null || sq.Pointer == IntPtr.Zero || sq.Pointer == localPlayerSqPtr)
                        continue;

                    if (AiCore.IsConfirmedPlaneCrewSquad(sq))
                    {
                        EvaluateGroundedAircrewSquad(sq);
                    }
                    else if (AiCore.IsTrueVehicleCrewSquad(sq))
                    {
                        EvaluateGroundedAiTankCrewSquad(sq);
                    }
                }
            }
            catch { }
        }

        [HarmonyPatch(typeof(AIVehicle), nameof(AIVehicle.RetreatCrew))]
        public static class AIVehicle_RetreatCrew_Patch
        {
            public static bool Prefix(AIVehicle __instance)
            {
                if (!VisionConfig.EnableMod.Value || __instance == null)
                    return true;

                Vehicle veh = __instance.veh;
                if (veh == null || veh.Pointer == IntPtr.Zero)
                    return true;

                try
                {
                    // Replace vanilla AIVehicle.RetreatCrew (0x18057B980), which crashes/fails on V2 & water spawns.
                    // Short-circuit once all crew have already exited an empty burning hulk so destroyed tanks stop thrashing!
                    bool alreadyRetreated = !_vehiclesCrewRetreatedFrom.Add(veh.Pointer);

                    Soldier sampleCrew = null;
                    var seats = veh.seats;
                    if (seats != null)
                    {
                        int seatCount = seats.Length;
                        for (int i = 0; i < seatCount; i++)
                        {
                            Seats seat = seats[i];
                            Soldier unit = seat != null ? seat.unitSet : null;
                            if (unit == null || unit.Pointer == IntPtr.Zero)
                                continue;

                            if (sampleCrew == null)
                                sampleCrew = unit;

                            if (unit.joinedSquad != null)
                            {
                                // Classify while unit is still seated inside veh (even for remote-owned crew in Co-op)!
                                RegisterVehicleCrewSquad(veh, unit);
                                AiCore.IsTrueVehicleCrewSquad(unit.joinedSquad);
                            }

                            Syncher syn = unit.GetSyncher();
                            if (syn == null || !syn.OwnSyncher)
                                continue;

                            unit.CoverPosition(null);
                            if (AiCore.TryGetSoldierHandles(unit.Pointer, out var unitHandles))
                                unitHandles.Stance.LocalCoverProtectedUntil = 0f;
                            ClearEmplacementAssignment(unit.Pointer, markUnreachable: false);
                            sampleCrew = unit;

                            if (unit.IsAI() && unit.IsAlive && !unit.IsInjured && !unit.IsDead)
                            {
                                BotActionTrace.RecordEvent(unit, BotTraceEventKind.VehicleBailout, "disabled_vehicle_bailout");
                                veh.GetOffVehicle(unit);
                            }
                        }
                    }

                    if (alreadyRetreated && sampleCrew == null)
                        return false;

                    VehicleWithWheels wheeled = veh.TryCast<VehicleWithWheels>();
                    if (wheeled != null && !wheeled.RetreatCrewOnDestroy())
                        return false;

                    if (veh.IsTransportVehicle())
                        return false;

                    Squad crewSq = sampleCrew != null ? sampleCrew.joinedSquad : null;
                    if ((crewSq == null || crewSq.Pointer == IntPtr.Zero) &&
                        _lastKnownVehicleCrewSquad.TryGetValue(veh.Pointer, out Squad cachedSq) &&
                        cachedSq != null &&
                        cachedSq.Pointer != IntPtr.Zero)
                    {
                        crewSq = cachedSq;
                    }

                    if (crewSq != null && !crewSq.isOpenForJoiners)
                    {
                        AiCore.MarkConfirmedVehicleCrewSquad(crewSq);
                    }

                    if (crewSq != null && AiCore.IsTrueVehicleCrewSquad(crewSq))
                    {
                        string fac = sampleCrew != null ? sampleCrew.faction : null;
                        if (string.IsNullOrEmpty(fac))
                            _lastKnownVehicleFaction.TryGetValue(veh.Pointer, out fac);

                        Vector3 originPos = veh.transform != null
                            ? veh.transform.position
                            : (crewSq.Leader != null && crewSq.Leader.transform != null ? crewSq.Leader.transform.position : Vector3.zero);

                        AssignCrewSquadRetreatTask(crewSq, fac, originPos, allowWaterSurface: false, replaceExistingIfFar: false);
                    }
                }
                catch { }

                return false;
            }
        }

        [HarmonyPatch(typeof(Squad), nameof(Squad.UnitCanChangeCover))]
        public static class Squad_UnitCanChangeCover_Patch
        {
            public static void Postfix(Soldier __0, ref bool __result)
            {
                try
                {
                    if (!__result || !VisionConfig.EnableMod.Value || __0 == null)
                        return;

                    IntPtr ptr = __0.Pointer;
                    float now = Time.time;
                    if (IsActivelyHeadingToEmplacement(__0, now))
                    {
                        __result = false;
                        return;
                    }

                    if (__0.targetDestination != null &&
                        (__0.targetDestination.TryCast<MedicHealingDestination>() != null ||
                         __0.targetDestination.TryCast<VehicleRepairTask>() != null))
                    {
                        __result = false;
                        return;
                    }

                    AiCore.TryGetSoldierHandles(ptr, out var coverHandles);

                    // Release stale post-combat cover locks immediately when a calm, unengaged soldier's squad
                    // moves away or re-orients its formationDirection (> 55 deg) against a new flank approach!
                    if (!__0.isBleeding &&
                        __0.GetSuppressionValue() < 5.0f &&
                        (coverHandles == null || coverHandles.Ai == null || AiCore.GetRawVisibleTargetPtr(coverHandles.Ai) == IntPtr.Zero))
                    {
                        Squad sq = coverHandles != null ? AiCore.GetCachedSquad(coverHandles, now) : __0.joinedSquad;
                        bool hasActiveSquadThreat = sq != null &&
                                                    sq.Pointer != IntPtr.Zero &&
                                                    _squadBlackboards.TryGetValue(sq.Pointer, out SquadBlackboard cb) &&
                                                    cb.HasActiveThreat;
                        if (sq != null && !hasActiveSquadThreat && coverHandles != null && coverHandles.Tr != null)
                        {
                            bool outsideOrderArea = !sq.IsInsideMoveOrderArea(coverHandles.Tr.position);
                            bool staleCoverAngle = false;
                            if (!outsideOrderArea &&
                                AiCore.ResolveTargetDestination(coverHandles) == AiCore.CachedDestKind.CombatCover &&
                                coverHandles.CachedCover != null &&
                                coverHandles.CachedCover.localLookAngleWideness < 165f)
                            {
                                Vector3 sqDir = sq.formationDirection;
                                sqDir.y = 0f;
                                Vector3 covFwd = coverHandles.CachedCover.GetLookRotation() * Vector3.forward;
                                covFwd.y = 0f;
                                if (sqDir.sqrMagnitude > 0.01f && covFwd.sqrMagnitude > 0.01f &&
                                    Vector3.Angle(covFwd, sqDir) > 55.0f)
                                {
                                    staleCoverAngle = true;
                                }
                            }

                            if (outsideOrderArea || staleCoverAngle)
                            {
                                GoapActionId curAct = now < coverHandles.Goap.Plan.PlanExpireTime
                                    ? (coverHandles.Goap.Plan.StepIndex == 0 ? coverHandles.Goap.Plan.Step0 : coverHandles.Goap.Plan.Step1)
                                    : GoapActionId.None;
                                bool keepSpecialTask =
                                    coverHandles.MoveOverride.Kind == AiCore.BotMoveOverrideKind.CasualtyDrag ||
                                    coverHandles.Goap.Plan.IsEvacuatingBailedCrew ||
                                    curAct == GoapActionId.ScavengeSupplyFromCorpse ||
                                    curAct == GoapActionId.MaintainRadioLinkWithLeader ||
                                    curAct == GoapActionId.RescueSurrenderedComrade;
                                if (!keepSpecialTask)
                                {
                                    coverHandles.Stance.LocalCoverProtectedUntil = 0f;
                                    coverHandles.Goap.Plan.LockUnitCoverChange = false;
                                    return;
                                }
                            }
                        }
                    }

                    if (coverHandles != null && now < coverHandles.Stance.LocalCoverProtectedUntil)
                    {
                        __result = false;
                        return;
                    }

                    if (HasActiveGoapCoverLock(ptr, now))
                    {
                        __result = false;
                    }
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(Squad), nameof(Squad.SquadLeaderRoutine))]
        public static class Squad_SquadLeaderRoutine_Patch
        {
            public static void Prefix(Squad __instance, ref bool __1, out long __state)
            {
                // Vanilla SquadLeaderRoutine executes 8x CoverManager octree + pathfinding queries in a single frame.
                // Flag the frame so WorldSnapshot, MacroDirector, CleanupDictionaries, and flank octree scans shift to clean frames!
                AiCore.NotifyHeavyWorkThisFrame();
                __state = HitchProfiler.BeginTopLevel();

                try
                {
                    // Prevent native Leader_OrderAttackCurrentTask (which ignores timed_order_end and Order.follow!)
                    // from overwriting active mod Fighting Withdrawals, Follow Columns, Hillside Detours, Hammer Pincers,
                    // or properly stationed Conquest garrisons every ~0.8s!
                    if (VisionConfig.EnableMod.Value &&
                        __1 &&
                        __instance != null &&
                        __instance.Pointer != IntPtr.Zero &&
                        !__instance.IsPlayerInSquad())
                    {
                        float now = Time.time;
                        IntPtr sqPtr = __instance.Pointer;
                        bool holdTimedOrder =
                            __instance.timed_order_end > now ||
                            IsSquadActivelyRetreating(sqPtr, now) ||
                            (_aiFollowColumnUntil.TryGetValue(sqPtr, out float folT) && now < folT) ||
                            (_garrisonAssignedObjectiveBySquad.TryGetValue(sqPtr, out IntPtr garPtr) &&
                             __instance.currentTask != null &&
                             __instance.currentTask.Pointer == garPtr &&
                             __instance.order == Order.defend &&
                             !__instance.HoldFire);

                        if (holdTimedOrder)
                        {
                            __1 = false;
                        }
                    }
                }
                catch { }
            }

            public static void Finalizer(long __state)
            {
                HitchProfiler.End(HitchBucket.VanSquadLeaderRtn, __state);
            }
        }

        [HarmonyPatch(typeof(ResourcesManager), nameof(ResourcesManager.TrySpotSquad))]
        public static class ResourcesManager_TrySpotSquad_Patch
        {
            public static bool Prefix(Spottable __0, Soldier __1, ref bool __result)
            {
                if (ReferenceEquals(__0, null) || __0.Pointer == IntPtr.Zero ||
                    ReferenceEquals(__1, null) || __1 == null || __1.Pointer == IntPtr.Zero ||
                    __1.joinedSquad == null)
                {
                    __result = false;
                    return false;
                }

                if (!Photon.Pun.PhotonNetwork.InRoom)
                    return true;

                SyncSoldier spotterSyncher = __1.GetSyncher();
                bool validSpotterSyncher = !ReferenceEquals(spotterSyncher, null) && spotterSyncher != null && spotterSyncher.Pointer != IntPtr.Zero;

                Soldier enemySoldier = __0.TryCast<Soldier>();
                if (!ReferenceEquals(enemySoldier, null) && enemySoldier != null && enemySoldier.Pointer != IntPtr.Zero)
                {
                    Squad enemySq = enemySoldier.joinedSquad;
                    if (ReferenceEquals(enemySq, null) || enemySq == null || enemySq.Pointer == IntPtr.Zero)
                    {
                        __result = false;
                        return false;
                    }

                    SyncSoldier enemySyncher = enemySoldier.GetSyncher();
                    if (!validSpotterSyncher || ReferenceEquals(enemySyncher, null) || enemySyncher == null || enemySyncher.Pointer == IntPtr.Zero)
                    {
                        __result = enemySq.MarkSquad(__1);
                        return false;
                    }
                    return true;
                }

                Vehicle enemyVeh = __0.TryCast<Vehicle>();
                if (!ReferenceEquals(enemyVeh, null) && enemyVeh != null && enemyVeh.Pointer != IntPtr.Zero)
                {
                    Squad vehSq = enemyVeh.GetSquadInside();
                    if (ReferenceEquals(vehSq, null) || vehSq == null || vehSq.Pointer == IntPtr.Zero)
                    {
                        __result = false;
                        return false;
                    }

                    Soldier vehLeader = vehSq.Leader;
                    SyncSoldier leaderSyncher = (!ReferenceEquals(vehLeader, null) && vehLeader != null && vehLeader.Pointer != IntPtr.Zero)
                        ? vehLeader.GetSyncher()
                        : null;

                    if (!validSpotterSyncher || ReferenceEquals(leaderSyncher, null) || leaderSyncher == null || leaderSyncher.Pointer == IntPtr.Zero)
                    {
                        __result = vehSq.MarkSquad(__1);
                        return false;
                    }
                    return true;
                }

                return true;
            }
        }

        [HarmonyPatch(typeof(Squad), nameof(Squad.TaskCanStillAttract))]
        public static class Squad_TaskCanStillAttract_Patch
        {
            public static void Postfix(Squad __instance, string __0, ref bool __result)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value ||
                        __instance == null ||
                        __instance.currentTask == null)
                    {
                        return;
                    }

                    IntPtr taskPtr = __instance.currentTask.Pointer;
                    bool isGarrisonAssigned =
                        VisionConfig.EnableMacroObjectiveDirector.Value &&
                        _garrisonAssignedObjectiveBySquad.TryGetValue(__instance.Pointer, out IntPtr garrisonObjPtr) &&
                        taskPtr == garrisonObjPtr;

                    // Fix vanilla Dynamic Operations bug: BattleManager.ActiveObjectives(false) skips any MissionObjectiveScripted
                    // where both canAttractAllies == false && canAttractAxis == false, causing Squad.TaskCanStillAttract (0x18080B170)
                    // to fall through and return TRUE forever when a Lua phase/wave script deactivates an objective!
                    var scriptedList = MissionObjectiveScripted.scriptedMissionObjectives;
                    if (scriptedList != null)
                    {
                        int sCount = scriptedList.Count;
                        for (int i = 0; i < sCount; i++)
                        {
                            MissionObjectiveScripted sObj = scriptedList[i];
                            if (sObj != null && sObj.missionTask != null && sObj.missionTask.Pointer == taskPtr)
                            {
                                if (!sObj.IsSettedUp() ||
                                    sObj.IsSecured() ||
                                    !sObj.missionTask.IsTaskEnabled() ||
                                    (!sObj.canAttractAllies && !sObj.canAttractAxis))
                                {
                                    __result = false;
                                    return;
                                }

                                if (isGarrisonAssigned)
                                {
                                    __result = true;
                                    return;
                                }

                                if (__result && !string.IsNullOrEmpty(__0) && !sObj.CanAttractFaction(__0))
                                {
                                    __result = false;
                                }
                                return;
                            }
                        }
                    }

                    if (!__result && isGarrisonAssigned)
                    {
                        __result = true;
                    }
                }
                catch { }
            }
        }

        // =====================================================================
        // Vanilla AI Fix: Smart Emplacement, Tank & Plane Munition Selection (TurretGun.SelectBestAmmo)
        // Fixes four compounding vanilla issues in TurretGun.SelectBestAmmo (0x18083C3D0) & BulletData (0x1807ABB80):
        // 1) Vanilla BulletData.IsHe() (0x1807ABB80) returns TRUE for ShellType.APHE (3)! Almost every tank and
        //    AT-gun emplacement (Pak 38/40, ZiS-3, 88mm, Panzer III/IV/Tiger, T-34, Sherman) uses APHE in slot 0.
        // 2) Vanilla GetBestAmmoIndexForInfantry (0x180836A9C) and GetBestAmmoIndexForPlane (0x180836D8E) skip any
        //    TurretWeapon where reloadEndTime > Time.time, so single-gun emplacements (which immediately start
        //    reloading after firing) never switch from AP/APHE to HE!
        // 3) Vanilla classifies unarmored trucks/jeeps (VehicleWithWheels) as tanks requiring APCR, and classifies
        //    soldiers seated inside enclosed tanks as foot infantry.
        // 4) Against armor, vanilla ignores distance penetration falloff (CalculateMmPenetrationWithFallof) and
        //    post-penetration APHE lethality vs solid APCR/HEAT.
        // =====================================================================
        internal static bool IsTrueHeOrAntiInfantryRound(BulletData bd)
        {
            if (bd == null)
                return false;
            ShellType st = bd.shellType;
            return st == ShellType.HE ||
                   st == ShellType.Rocket ||
                   bd.IsFire() ||
                   bd.IsShotgun() ||
                   bd.IsFlak();
        }

        internal static float GetVehicleMaxHullArmorMm(Vehicle veh)
        {
            if (veh == null || veh.Pointer == IntPtr.Zero)
                return 0f;
            if (veh.IsStatic() || veh.IsAirVehicle())
                return 0f;

            float maxArmor = veh.TryCast<VehicleTank>() != null ? 25f : 0f;
            var parts = veh.damageableParts;
            if (parts != null)
            {
                int count = parts.Count;
                for (int i = 0; i < count; i++)
                {
                    VehicleDamagablePart p = parts[i];
                    if (p == null) continue;
                    if (p.TryCast<VehicleDamageableWheelPart>() != null ||
                        p.TryCast<VehicleDamageableTrackPart>() != null ||
                        p.TryCast<VehicleDamagableDetachablePart>() != null)
                    {
                        continue;
                    }
                    float a = (float)(int)p.armorThickness;
                    if (a > maxArmor)
                        maxArmor = a;
                }
            }
            return maxArmor;
        }

        private static bool HasTurretWeaponAmmoInSlot(
            TurretWeapon w,
            int aIdx,
            InventoryManager inv,
            ref bool refreshedInv)
        {
            if (w == null)
                return false;
            if (w.loadedAmmo_id == aIdx && w.loadedAmmoCount > 0)
                return true;

            var ammos = w.ammos;
            if (ammos == null || aIdx < 0 || aIdx >= ammos.Length)
                return false;

            TurretWeaponBullet slot = ammos[aIdx];
            if (slot == null)
                return false;

            if (slot.totalStoredAmmo <= 0 && !refreshedInv && inv != null && inv.inventory != null)
            {
                refreshedInv = true;
                w.RefresStoredAmmoCount(inv.inventory);
            }
            return slot.totalStoredAmmo > 0;
        }

        [HarmonyPatch(typeof(TurretGun), nameof(TurretGun.SelectBestAmmo), new Type[] { typeof(Spottable) })]
        public static class TurretGun_SelectBestAmmo_Patch
        {
            public static void Postfix(TurretGun __instance, Spottable __0, ref bool __result)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value ||
                        !VisionConfig.EnableSmartVehicleAmmoSelection.Value ||
                        __instance == null ||
                        __0 == null ||
                        __0.Pointer == IntPtr.Zero)
                    {
                        return;
                    }

                    var weapons = __instance.weapons;
                    if (weapons == null || weapons.Length == 0)
                        return;

                    int selW = __instance.selectedWeapon_id;
                    int selA = __instance.selectedAmmo_id;
                    float now = Time.time;
                    Vehicle rootVeh = __instance.rootVehicle;
                    InventoryManager inv = rootVeh != null ? rootVeh.GetInventory() : null;

                    // 1. Classify target: Aircraft vs. Armored Vehicle (Hull >= 8mm) vs. Soft/Infantry/Emplacement
                    bool isAirTarget = __0.IsAirVehicle();
                    bool isArmoredTarget = false;
                    float targetArmorMm = 0f;

                    if (!isAirTarget)
                    {
                        Vehicle targetVeh = __0.TryCast<Vehicle>();
                        if (targetVeh == null)
                        {
                            Soldier targetSol = __0.TryCast<Soldier>();
                            if (targetSol != null && targetSol.IsOnVehicle())
                            {
                                Vehicle seatVeh = targetSol.GetCurrentVehicle();
                                if (seatVeh != null && seatVeh.Pointer != IntPtr.Zero)
                                {
                                    if (seatVeh.IsAirVehicle())
                                    {
                                        isAirTarget = true;
                                    }
                                    else
                                    {
                                        Seats seat = targetSol.GetCurrentVehicleSeat();
                                        bool exposedSeat = seat != null && seat.showUnitSet;
                                        if (!exposedSeat)
                                            targetVeh = seatVeh;
                                    }
                                }
                            }
                        }

                        if (targetVeh != null && !isAirTarget)
                        {
                            targetArmorMm = GetVehicleMaxHullArmorMm(targetVeh);
                            if (targetArmorMm >= 8.0f)
                                isArmoredTarget = true;
                        }
                    }

                    int targetW = -1;
                    int targetA = -1;

                    if (isAirTarget)
                    {
                        // Aircraft target: prioritize Flak / Time-Fuse Flak, then rapid-fire AA autocannon/MG, then highest plane efficiency
                        float bestAirScore = -1f;
                        for (int wIdx = 0; wIdx < weapons.Length; wIdx++)
                        {
                            TurretWeapon w = weapons[wIdx];
                            if (w == null || w.ammos == null)
                                continue;

                            bool refreshedInv = false;
                            bool isReady = w.nextReloadEnd <= now;
                            bool isRapidFire = !w.IsLowFireRate();
                            var ammos = w.ammos;
                            for (int aIdx = 0; aIdx < ammos.Length; aIdx++)
                            {
                                if (!HasTurretWeaponAmmoInSlot(w, aIdx, inv, ref refreshedInv))
                                    continue;

                                BulletData bd = w.GetBulletDataFor(aIdx) ?? w.BulletData;
                                if (bd == null)
                                    continue;

                                float score = bd.GetEfficiencyAgainstPlanes();
                                if (bd.IsFlak())
                                    score += 50f;
                                if (isRapidFire)
                                    score += 20f;
                                if (isReady)
                                    score += 12f;

                                if (score > bestAirScore)
                                {
                                    bestAirScore = score;
                                    targetW = wIdx;
                                    targetA = aIdx;
                                }
                            }
                        }
                    }
                    else if (isArmoredTarget)
                    {
                        // Armored target (Tank, Armored Car, Halftrack with >= 8mm armor):
                        // Evaluate effective penetration at target distance (CalculateMmPenetrationWithFallof),
                        // preferring APHE when penetration suffices to pierce the target's armor, and APCR/HEAT when
                        // maximum penetration is required. Fall back to heavy HE (>= 37mm) before coaxial MGs.
                        float dist = __instance.transform != null
                            ? Vector3.Distance(__instance.transform.position, __0.GetPosition())
                            : 100f;
                        float penThreshold = Mathf.Max(8f, targetArmorMm * 0.85f);
                        float bestArmorScore = -1f;

                        for (int wIdx = 0; wIdx < weapons.Length; wIdx++)
                        {
                            TurretWeapon w = weapons[wIdx];
                            if (w == null || w.ammos == null)
                                continue;

                            bool refreshedInv = false;
                            var ammos = w.ammos;
                            for (int aIdx = 0; aIdx < ammos.Length; aIdx++)
                            {
                                if (!HasTurretWeaponAmmoInSlot(w, aIdx, inv, ref refreshedInv))
                                    continue;

                                BulletData bd = w.GetBulletDataFor(aIdx) ?? w.BulletData;
                                if (bd == null)
                                    continue;

                                float kinPen = bd.CalculateMmPenetrationWithFallof(dist);
                                float effPen = Mathf.Max(kinPen, bd.explosion_penetration);
                                ShellType st = bd.shellType;

                                float score;
                                if (effPen >= penThreshold)
                                {
                                    // Round can penetrate target at this distance: prefer APHE > HEAT > solid AP/APCR > HE
                                    float typeBonus = st == ShellType.APHE
                                        ? 220f
                                        : (st == ShellType.HEAT
                                            ? 175f
                                            : (st == ShellType.AP ? 140f : 80f));
                                    score = 1000f + typeBonus + Mathf.Min(effPen, 250f) * 0.25f;
                                }
                                else if (effPen >= 12f)
                                {
                                    // Target armor is thick: rank strictly by effective penetration at distance
                                    score = effPen * 5.0f + (st == ShellType.APHE ? 8f : 0f);
                                }
                                else if (bd.caliber >= 37f && IsTrueHeOrAntiInfantryRound(bd))
                                {
                                    // Out of AP/HEAT: heavy HE cannon shell can still track/concuss armor better than small-caliber MG
                                    score = 25f + bd.caliber * 0.15f;
                                }
                                else
                                {
                                    score = effPen;
                                }

                                if (score > bestArmorScore)
                                {
                                    bestArmorScore = score;
                                    targetW = wIdx;
                                    targetA = aIdx;
                                }
                            }
                        }
                    }
                    else
                    {
                        // Soft target (Foot Infantry, Exposed Crew, Static Gun Emplacement, or Unarmored Truck/Jeep):
                        // 1) Find best True HE / Frag / Flak / Fire / Shotgun slot (both ready-now and reloading).
                        // 2) Find best rapid-fire Coaxial / Wing MG (< 20mm) for firing while a tank's main gun reloads
                        //    or when the main gun has no true HE left (never waste AP/APHE/HEAT cannon shells on foot infantry
                        //    when HE or a Coaxial MG is available!).
                        int bestReadyHeW = -1, bestReadyHeA = -1;
                        float bestReadyHeScore = -1f;

                        int bestAnyHeW = -1, bestAnyHeA = -1;
                        float bestAnyHeScore = -1f;

                        int bestMgW = -1, bestMgA = -1;
                        float bestMgScore = -1f;

                        for (int wIdx = 0; wIdx < weapons.Length; wIdx++)
                        {
                            TurretWeapon w = weapons[wIdx];
                            if (w == null || w.ammos == null)
                                continue;

                            bool refreshedInv = false;
                            bool isReady = w.nextReloadEnd <= now;
                            bool isRapidFire = !w.IsLowFireRate();
                            var ammos = w.ammos;
                            for (int aIdx = 0; aIdx < ammos.Length; aIdx++)
                            {
                                if (!HasTurretWeaponAmmoInSlot(w, aIdx, inv, ref refreshedInv))
                                    continue;

                                BulletData bd = w.GetBulletDataFor(aIdx) ?? w.BulletData;
                                if (bd == null)
                                    continue;

                                if (IsTrueHeOrAntiInfantryRound(bd))
                                {
                                    float heScore = bd.GetEfficiencyAgainstInfantry() * 10f + Mathf.Min(bd.caliber, 150f) * 0.2f;
                                    if (heScore > bestAnyHeScore)
                                    {
                                        bestAnyHeScore = heScore;
                                        bestAnyHeW = wIdx;
                                        bestAnyHeA = aIdx;
                                    }
                                    if (isReady && heScore > bestReadyHeScore)
                                    {
                                        bestReadyHeScore = heScore;
                                        bestReadyHeW = wIdx;
                                        bestReadyHeA = aIdx;
                                    }
                                }
                                else if (isRapidFire && bd.caliber < 20.0f)
                                {
                                    float mgScore = bd.GetEfficiencyAgainstInfantry() * 10f + (isReady ? 5f : 0f);
                                    if (mgScore > bestMgScore)
                                    {
                                        bestMgScore = mgScore;
                                        bestMgW = wIdx;
                                        bestMgA = aIdx;
                                    }
                                }
                            }
                        }

                        // If a cannon has a True HE slot available (bestAnyHeW >= 0) but currently has AP/APHE/HEAT loaded,
                        // trigger Reload(bestAnyHeA) on that cannon immediately so the breech loads HE for the next shot!
                        if (bestAnyHeW >= 0)
                        {
                            TurretWeapon heWeap = weapons[bestAnyHeW];
                            if (heWeap != null && heWeap.loadedAmmo_id != bestAnyHeA)
                            {
                                heWeap.Reload((short)bestAnyHeA, inv, null, false, false);
                            }
                        }

                        if (bestReadyHeW >= 0)
                        {
                            targetW = bestReadyHeW;
                            targetA = bestReadyHeA;
                        }
                        else if (bestMgW >= 0)
                        {
                            targetW = bestMgW;
                            targetA = bestMgA;
                        }
                        else if (bestAnyHeW >= 0)
                        {
                            targetW = bestAnyHeW;
                            targetA = bestAnyHeA;
                        }
                    }

                    if (targetW >= 0 && targetW < weapons.Length && targetA >= 0)
                    {
                        bool changed = (targetW != selW || targetA != selA);
                        __instance.selectedWeapon_id = (short)targetW;
                        __instance.selectedAmmo_id = (short)targetA;
                        __result = true;

                        TurretWeapon chosenWeap = weapons[targetW];
                        if (chosenWeap != null && chosenWeap.loadedAmmo_id != targetA)
                        {
                            chosenWeap.Reload((short)targetA, inv, null, false, false);
                        }

                        if (changed)
                        {
                            SmartAmmoSwitchesCount++;
                            if (VisionConfig.DebugLogging.Value)
                            {
                                BulletData chosenBd = chosenWeap != null ? (chosenWeap.GetBulletDataFor(targetA) ?? chosenWeap.BulletData) : null;
                                AiCore.LogDebug("SmartAmmo", $"Turret '{__instance.name}' selected weapon[{targetW}] ammo[{targetA}] (shell:{chosenBd?.shellType}, cal:{chosenBd?.caliber:F0}mm) vs {(isAirTarget ? "Air" : (isArmoredTarget ? $"Armor({targetArmorMm:F0}mm)" : "Soft/Infantry"))}.");
                            }
                        }
                    }
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(TurretGun), nameof(TurretGun.SetGunTriggerPressed))]
        public static class TurretGun_SetGunTriggerPressed_Patch
        {
            public static void Prefix(TurretGun __instance, ref bool __0, Soldier __1)
            {
                try
                {
                    if (!__0 ||
                        !VisionConfig.EnableMod.Value ||
                        VisionConfig.FriendlyFireConeDegrees.Value <= 0.1f ||
                        __instance == null ||
                        __instance.Pointer == IntPtr.Zero)
                    {
                        return;
                    }

                    if (__1 != null && __1.IsPlayer())
                        return;

                    if (ShouldBlockTurretFriendlyFire(null, __instance, __1, true))
                    {
                        __0 = false;
                        __instance.triggerPressed = false;
                    }
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(TurretGun), nameof(TurretGun.StartFire))]
        public static class TurretGun_StartFire_Patch
        {
            public static bool Prefix(TurretGun __instance)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value ||
                        __instance == null ||
                        __instance.Pointer == IntPtr.Zero)
                    {
                        return true;
                    }

                    Vehicle rootVeh = AiCore.GetCachedTurretVehicle(__instance);
                    if (rootVeh != null && rootVeh.IsAirVehicle())
                        return true;

                    Soldier gunner = rootVeh != null ? __instance.GetSittedUnit(rootVeh) : null;

                    // Final safety gate right before a shell/mortar leaves the barrel (catches delayed FireOnReloadDone coroutines!)
                    if (VisionConfig.FriendlyFireConeDegrees.Value > 0.1f &&
                        (gunner == null || !gunner.IsPlayer()) &&
                        ShouldBlockTurretFriendlyFire(null, __instance, gunner, true))
                    {
                        __instance.triggerPressed = false;
                        return false;
                    }

                    // Feed tank cannons, armored cars, and static MG/AT/mortar emplacements (~210m) into the acoustic ring buffer
                    VisionPatches.RecordTurretFired(__instance, rootVeh, gunner);
                }
                catch { }
                return true;
            }
        }

        // =====================================================================
        // PATCH 14C: TurretWeapon.Reload (Empty-Ammo & Out-of-Range Slot Guard)
        // =====================================================================
        // Prevents native TurretWeapon.Reload (0x18083FE50) from throwing IndexOutOfRangeException at 0x18083FFEF
        // when Vehicle.RefillAllTurrets() -> TurretGun.AcquireAmmo(false, 0) reloads an auxiliary/dummy TurretWeapon
        // whose ammos array is empty, or when a caller passes a negative / out-of-range ammo slot index.
        [HarmonyPatch(typeof(TurretWeapon), nameof(TurretWeapon.Reload))]
        public static class TurretWeapon_Reload_Patch
        {
            public static bool Prefix(TurretWeapon __instance, ref short __0, ref bool __result)
            {
                try
                {
                    if (__instance == null || __instance.Pointer == IntPtr.Zero)
                        return true;

                    var ammos = __instance.ammos;
                    if (ammos == null || ammos.Length == 0)
                    {
                        __result = false;
                        return false;
                    }

                    if (__0 < 0 || __0 >= ammos.Length)
                    {
                        short fallback = __instance.loadedAmmo_id;
                        __0 = (short)(fallback >= 0 && fallback < ammos.Length ? fallback : 0);
                    }
                }
                catch { }
                return true;
            }
        }
    }
}
