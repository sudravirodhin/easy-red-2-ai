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
        private static float _nextGroundedCrewRetreatCheckTime;
        public static int CombinedArmsPriorityAssistsCount;
        public static int KamikazeDivesTriggeredCount;
        public static int TankRockUnsticksCount;
        public static int SmartAmmoSwitchesCount;

        internal static readonly Dictionary<IntPtr, TurretAI> _turretAiByTurretPtr = new Dictionary<IntPtr, TurretAI>(64);
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
            _squadOrderedToVehicle.Clear();
            _unreachableEmplacementUntil.Clear();
            _cachedRepairStationsPhasePtr = IntPtr.Zero;
            _currentDirectorBattleData = null;
            _emplacementScanCursor = 0;
            ResetSpawnerReservations();
            _nextGroundedCrewRetreatCheckTime = 0f;
            _turretAiByTurretPtr.Clear();
            _lastTurretTargetWasVehicle.Clear();
            _nextCombinedArmsIntelRefreshTime = 0f;
            _nextMacroDirectorTime = 0f;
            _macroDirectorStarvedSince = 0f;
            _garrisonAssignedObjectiveBySquad.Clear();
            _assignedEmplacementUntil.Clear();
            _assignedEmplacementVehicle.Clear();
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
            _lastPooledMarker3DEntry = null;
            _squad3DMarkerEntries.Clear();
            _marker3DEntryOwnerByEntryPtr.Clear();
            _marker3DEntryTexts.Clear();
            _activeSquad3DLabels.Clear();
            _pendingStaggeredBailouts.Clear();
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
            public float LockedUntil;
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

        private static bool TryRecordCombinedArmsAssist(IntPtr vehPtr, float now)
        {
            if (AiCore.TryGetVehicleHandles(vehPtr, out AiCore.VehicleFastHandles vh))
            {
                if (now < vh.NextCombinedArmsAssistCountTime)
                    return false;
                vh.NextCombinedArmsAssistCountTime = now + 6.0f;
            }
            CombinedArmsPriorityAssistsCount++;
            return VisionConfig.DebugLogging.Value;
        }

        internal static string GetVehicleCrewFaction(Vehicle veh, Turret turret = null)
        {
            if (ReferenceEquals(veh, null) || veh == null)
                return null;

            AiCore.VehicleFastHandles vh = AiCore.GetOrCreateVehicleHandles(veh);
            float now = Time.time;
            if (ReferenceEquals(turret, null) &&
                vh != null &&
                now < vh.NextCrewFactionCheckTime &&
                now >= vh.NextCrewFactionCheckTime - 2.0f &&
                !string.IsNullOrEmpty(vh.LastKnownFaction))
            {
                return vh.LastKnownFaction;
            }

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

            if (resolved == null && vh != null && !string.IsNullOrEmpty(vh.LastKnownFaction))
            {
                resolved = vh.LastKnownFaction;
            }

            if (resolved != null && vh != null)
            {
                vh.LastKnownFaction = resolved;
                if (ReferenceEquals(turret, null))
                    vh.NextCrewFactionCheckTime = now + 0.50f;
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
                                IsPlayerInsideOrDriving = AiCore.IsAnyHumanPlayerInsideVehicle(v) || AiCore.IsAnyHumanPlayerDriving(v),
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

            if (!veh.CanFight())
                return;

            Turret turret = turretAi.turret;
            AiCore.TurretFastTraits traits = AiCore.GetCachedTurretTraits(turret);
            if (traits.IsCommanderTurret)
                return;

            Soldier gunner = turret.GetSittedUnit(veh);
            if (gunner != null && AiCore.IsAnyHumanPlayerSoldier(gunner))
                return;

            bool playerInVeh = AiCore.IsAnyHumanPlayerInsideVehicle(veh);
            if (playerInVeh && gunner == null)
                return;

            string myFaction = GetVehicleCrewFaction(veh, turret);
            if (string.IsNullOrEmpty(myFaction))
                return;

            AiCore.VehicleFastHandles vh = traits.VehHandles ?? AiCore.GetOrCreateVehicleHandles(veh);
            float now = Time.time;
            Vector3 firePos = turret.GetFirePos();
            Spottable curTarget = turretAi.CurrentVisibleTarget;

            // Air Vehicle Defensive Gunners (Tail/Waist/Dorsal): return fire on attacking planes or AA shooters
            if (veh.IsAirVehicle())
            {
                if (vh != null &&
                    now < vh.UnderFireUntil &&
                    AiCore.IsValidAliveSpottable(vh.UnderFireAttacker))
                {
                    Spottable ufAirAtk = vh.UnderFireAttacker;
                    bool shouldSwitch = curTarget == null ||
                                        !AiCore.IsValidAliveSpottable(curTarget) ||
                                        (!curTarget.IsAirVehicle() && ufAirAtk.IsAirVehicle());
                    if (shouldSwitch)
                    {
                        Vector3 ufPos = ufAirAtk.GetCenterOfUnit();
                        Vector3 toUf = ufPos - firePos;
                        float ufDist = toUf.magnitude;
                        if (ufDist >= 8.0f &&
                            ufDist <= 1150.0f &&
                            turret.CanShootInDirection(toUf / ufDist, 1.0f))
                        {
                            float ufAirAngleAdj = Corvostudio.Weapons.BulletInstance.CalculateTrajectory(
                                ufDist, turret.GetBulletSpeed(), turret.GetBulletGravity(), out bool ufAirReach);
                            if (ufAirReach)
                            {
                                bool airCanSee;
                                VisionPatches.AllowCommanderIntercomFovBypass = true;
                                try
                                {
                                    airCanSee = turretAi.CanSee(ufAirAtk);
                                }
                                finally
                                {
                                    VisionPatches.AllowCommanderIntercomFovBypass = false;
                                }

                                if (airCanSee)
                                {
                                    turretAi.CurrentVisibleTarget = ufAirAtk;
                                    turretAi.supportedSquad = null;
                                    turretAi.targetAngleAdjust = ufAirAngleAdj;
                                    turretAi.targetInWeaponRange = true;
                                    turret.SelectBestAmmo(ufAirAtk);
                                }
                            }
                        }
                    }
                }
                return;
            }

            bool checkFF = VisionConfig.FriendlyFireConeDegrees.Value > 0.1f;
            bool isIndirectOrArty = turret.IsUpIndirectFire() || turret.IsArtillery();
            IntPtr gunnerPtr = gunner != null ? gunner.Pointer : IntPtr.Zero;
            IntPtr turretPtr = turret.Pointer;

            // 1. Sanitize danger-close indirect/artillery targets & radio support requests before vanilla fires into friendlies!
            if (!playerInVeh && checkFF && isIndirectOrArty)
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

            bool hasActiveUnderFire = vh != null &&
                                      now < vh.UnderFireUntil &&
                                      AiCore.IsValidAliveSpottable(vh.UnderFireAttacker);

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

            if (playerInVeh && !hasActiveUnderFire)
                return;

            bool isEnclosedArmorGun = vh != null &&
                                      vh.IsWheeledOrTank &&
                                      !traits.IsExposedCrewSeat &&
                                      !traits.IsAA &&
                                      !isIndirectOrArty;

            bool hasInternalSpotter = true;
            bool hasInfantryEscort = false;
            if (!playerInVeh && isEnclosedArmorGun)
            {
                AiCore.GetCachedVehicleSpotterStatus(vh, myFaction, now, out hasInternalSpotter, out hasInfantryEscort);
                if (!hasInternalSpotter && !hasInfantryEscort)
                {
                    // Lone buttoned-up tank with dead/missing commander and no nearby friendly infantry escort:
                    // cancel any pending intercom handoff; the gunner can only see inside their narrow gun-sight arc
                    // (except when actively under incoming fire via Step -1 below!).
                    _pendingTurretIntercom.Remove(turretPtr);
                }
                else if (_pendingTurretIntercom.TryGetValue(turretPtr, out PendingTurretIntercomHandoff pending))
                {
                    if (!AiCore.IsValidAliveSpottable(pending.Target) ||
                        (hasActiveUnderFire && vh.UnderFireAttackerPtr != pending.Target.Pointer))
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

            bool hasCommanderCupolaSpot = !playerInVeh &&
                                          vh != null &&
                                          vh.CommanderSpottedTarget != null &&
                                          (now - vh.CommanderSpotTime) <= 4.2f &&
                                          AiCore.IsValidAliveSpottable(vh.CommanderSpottedTarget);

            if (!VisionConfig.EnableCombinedArmsPrioritySupport.Value && !hasCommanderCupolaSpot && !hasActiveUnderFire)
                return;

            if (!playerInVeh && VisionConfig.EnableCombinedArmsPrioritySupport.Value)
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
            IntPtr activeUfCandPtr = IntPtr.Zero;

            // -1. Active Under-Fire Counter-Fire (works even for lone buttoned-up armor: crew feels which side was hit!)
            if (hasActiveUnderFire)
            {
                Spottable ufCand = vh.UnderFireAttacker;
                bool ufIsAir = ufCand.IsAirVehicle();
                bool ufIsVeh = !ufIsAir && ufCand.IsWheeledVehicleOrTank();
                Vehicle ufVeh = ufIsVeh ? ufCand.TryCast<Vehicle>() : null;
                if ((!ufIsAir || traits.IsAA) && (!ufIsVeh || canDamageLightVehicles))
                {
                    Vector3 ufPos = ufCand.GetCenterOfUnit();
                    Vector3 toUf = ufPos - firePos;
                    float ufDist = toUf.magnitude;
                    float ufMaxDist = ufIsAir ? 900.0f : (ufIsVeh ? 600.0f : (isIndirectOrArty ? 650.0f : 360.0f));
                    bool canDamageUfVeh = ufVeh == null || CanTurretEffectivelyDamageArmoredVehicle(turret, ufVeh, ufDist);
                    if (canDamageUfVeh && ufDist >= 4.0f && ufDist <= ufMaxDist && turret.CanShootInDirection(toUf / ufDist, 1.0f))
                    {
                        bool ufSafe = true;
                        if (checkFF && (isIndirectOrArty || (hasHe && caliber >= 50.0f)))
                        {
                            float safeR = isIndirectOrArty ? 30.0f : 20.0f;
                            float playerSafeR = isIndirectOrArty ? 36.0f : 26.0f;
                            if (AiCore.IsIndirectFireImpactDangerClose(myFaction, ufPos, gunnerPtr, safeR, playerSafeR))
                                ufSafe = false;
                        }

                        if (ufSafe)
                        {
                            float ufAngleAdj = Corvostudio.Weapons.BulletInstance.CalculateTrajectory(ufDist, bulletSpeed, bulletGrav, out bool ufReach);
                            if (ufReach)
                            {
                                bool ufCanSee;
                                VisionPatches.AllowCommanderIntercomFovBypass = true;
                                try
                                {
                                    ufCanSee = turretAi.CanSee(ufCand);
                                }
                                finally
                                {
                                    VisionPatches.AllowCommanderIntercomFovBypass = false;
                                }

                                if (ufCanSee || CanTankShellIndoorTargetFacade(turret, veh, ufCand))
                                {
                                    float ufScore = (vh.UnderFireIsHeavy ? 135.0f : 96.0f) + (ufIsVeh && hasHeavyGun ? 40.0f : 0f) - (ufDist * 0.08f);
                                    if (ufScore > bestScore)
                                    {
                                        bestScore = ufScore;
                                        bestCandidate = ufCand;
                                        bestAngleAdj = ufAngleAdj;
                                        activeUfCandPtr = ufCand.Pointer;
                                    }
                                }
                            }
                        }
                    }
                }
            }

            if (playerInVeh)
            {
                if (bestCandidate != null && (curTarget == null || bestCandidate.Pointer != curTarget.Pointer))
                {
                    turretAi.CurrentVisibleTarget = bestCandidate;
                    turretAi.supportedSquad = null;
                    turretAi.targetAngleAdjust = bestAngleAdj;
                    turretAi.targetInWeaponRange = true;
                    turret.SelectBestAmmo(bestCandidate);
                }
                return;
            }

            // 0. Intra-vehicle Tank Commander Cupola / Periscope direct spot (highest priority intra-crew handoff)
            if (hasCommanderCupolaSpot && (!loneButtonedUpArmor))
            {
                Spottable cmdCand = vh.CommanderSpottedTarget;
                bool cmdIsVeh = cmdCand.IsWheeledVehicleOrTank();
                Vehicle cmdVeh = cmdIsVeh ? cmdCand.TryCast<Vehicle>() : null;
                if (!cmdCand.IsAirVehicle() && (!cmdIsVeh || canDamageLightVehicles))
                {
                    Vector3 cmdPos = cmdCand.GetCenterOfUnit();
                    Vector3 toCmd = cmdPos - firePos;
                    float cmdDist = toCmd.magnitude;
                    float cmdMaxDist = cmdIsVeh ? 550.0f : 320.0f;
                    bool canDamageCmdVeh = cmdVeh == null || CanTurretEffectivelyDamageArmoredVehicle(turret, cmdVeh, cmdDist);
                    if (canDamageCmdVeh && cmdDist >= 6.0f && cmdDist <= cmdMaxDist && turret.CanShootInDirection(toCmd / cmdDist, 1.0f))
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

                    if (isVehCand)
                    {
                        Vehicle candVeh = cand.TryCast<Vehicle>();
                        if (candVeh != null && !CanTurretEffectivelyDamageArmoredVehicle(turret, candVeh, dist))
                            continue;
                    }

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
                    if (bestCandidate.Pointer != activeUfCandPtr &&
                        canBypassGunSlitViaSpotter &&
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
            bool hasSmoke = HasTurretSmokeShellAmmo(turret);
            if ((hasHe || hasSmoke || isIndirectOrArty) && (curTarget == null || curScore < 26.0f) && !turretAi.HasRadioSupportRequest)
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

                        if (!hasSmoke && req.RequestType == RadioRequest.artillerySmoke)
                            continue;
                        if (!hasHe && req.RequestType == RadioRequest.artilleryHE)
                            continue;

                        Vector3 toReq = req.Position - firePos;
                        float reqDist = toReq.magnitude;
                        if (reqDist < 20.0f || reqDist >= bestReqDist)
                            continue;

                        if (!isIndirectOrArty && req.RequestType == RadioRequest.artillerySmoke)
                        {
                            if (reqDist < 25.0f || reqDist > 380.0f)
                                continue;
                            Vector3 toSmokeTarget = (req.Position + Vector3.up * 2.0f) - firePos;
                            float smokeRayDist = toSmokeTarget.magnitude;
                            if (smokeRayDist > 6.0f &&
                                AiCore.RaycastEnvironment(firePos, toSmokeTarget / smokeRayDist, smokeRayDist - 5.5f, veh.transform.root, out _))
                            {
                                continue;
                            }
                        }

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
                            ammoOk = isSmokeReq
                                ? (hasSmoke && turret.SelectBestAmmo(RadioRequest.artillerySmoke))
                                : (turret.SelectBestAmmo(turretAi.supportedSquad.radioRequest) || hasHe);
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
                                isSmokeReq
                                    ? $"{myFaction} Smoke turret answering friendly radio smoke request ({rDist:F0}m)."
                                    : $"{myFaction} HE turret answering friendly radio support request ({rDist:F0}m).");
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
        internal static bool IsTurretDirectFireBlockedByWall(
            Vector3 pivotPos,
            Vector3 firePos,
            Vector3 fireDir,
            bool isAirVehicle,
            bool isIndirect,
            bool isExplosive,
            bool hasTargetPos,
            float targetDist,
            Transform ignoreRoot)
        {
            if (isAirVehicle || isIndirect)
                return false;

            // 0. Direct-fire barrel/muzzle clip check: ignore own hull/gun-shield and vehicle hulls, catch solid walls/berms
            if ((firePos - pivotPos).sqrMagnitude > 0.04f &&
                AiCore.LinecastEnvironment(pivotPos, firePos, ignoreRoot, out _, ignoreVehicles: true))
            {
                return true;
            }

            // 1. Direct-fire explosive/autocannon muzzle check: prevent detonating on masonry right in front of barrel
            //    without muting point-blank tank-vs-tank duels or hitting a building behind a close target.
            if (isExplosive)
            {
                float wallCheckDist = hasTargetPos
                    ? Mathf.Min(5.2f, targetDist - 0.35f)
                    : 5.2f;
                if (wallCheckDist > 0.25f &&
                    AiCore.IsDirectionBlockedByEnvironment(firePos, fireDir, wallCheckDist, ignoreRoot, ignoreVehicles: true))
                {
                    return true;
                }
            }

            return false;
        }

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

            if (gunner != null && AiCore.IsAnyHumanPlayerSoldier(gunner))
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

            // 0 & 1. Block direct-fire ground/static turrets whose barrel or explosive muzzle is blocked by a solid wall/berm
            //       (ignores own vehicle/gun-shield and enemy vehicle hulls so point-blank duels and shielded emplacements still fire).
            bool isAirVeh = rootVeh != null && rootVeh.IsAirVehicle();
            if (VisionConfig.EnableWallShootingPrevention.Value && !isAirVeh && !isIndirect)
            {
                Transform ignoreRoot = (rootVeh != null && rootVeh.transform != null)
                    ? rootVeh.transform.root
                    : (turret.transform != null ? turret.transform.root : null);
                Vector3 pivotPos = turret.transform != null ? turret.transform.position : firePos;
                if (IsTurretDirectFireBlockedByWall(pivotPos, firePos, fireDir, isAirVeh, isIndirect, isExplosive, hasTargetPos, targetDist, ignoreRoot))
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

            bool isPlayer = AiCore.IsAnyHumanPlayerSoldier(friend);
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
                                now >= coverHandles.Stance.CoverSettledUntil &&
                                AiCore.ResolveTargetDestination(coverHandles) == AiCore.CachedDestKind.CombatCover &&
                                coverHandles.CachedCover != null &&
                                coverHandles.CachedCover.localLookAngleWideness < 165f)
                            {
                                Vector3 sqDir = sq.formationDirection;
                                sqDir.y = 0f;
                                Vector3 covFwd = coverHandles.CachedCover.GetLookRotation() * Vector3.forward;
                                covFwd.y = 0f;
                                if (sqDir.sqrMagnitude > 0.01f && covFwd.sqrMagnitude > 0.01f &&
                                    Vector3.Angle(covFwd, sqDir) > 105.0f)
                                {
                                    staleCoverAngle = true;
                                }
                            }

                            if (outsideOrderArea || staleCoverAngle)
                            {
                                var curAct = coverHandles.Emergent.HasDecision
                                    ? coverHandles.Emergent.CurrentDecision.Step0
                                    : EmergentEngine.EmergentActionId.None;
                                bool keepSpecialTask =
                                    coverHandles.MoveOverride.Kind == AiCore.BotMoveOverrideKind.CasualtyDrag ||
                                    coverHandles.Goap.Plan.IsEvacuatingBailedCrew ||
                                    curAct == EmergentEngine.EmergentActionId.ResupplyAmmo ||
                                    curAct == EmergentEngine.EmergentActionId.MaintainRadioLink ||
                                    curAct == EmergentEngine.EmergentActionId.LiberatePrisoner;
                                if (!keepSpecialTask)
                                {
                                    coverHandles.Stance.LocalCoverProtectedUntil = 0f;
                                    coverHandles.Goap.Plan.LockUnitCoverChange = false;
                                    return;
                                }
                            }
                        }
                    }

                    if (coverHandles != null &&
                        (coverHandles.MoveOverride.Kind == AiCore.BotMoveOverrideKind.GrenadeReflex ||
                         coverHandles.MoveOverride.Kind == AiCore.BotMoveOverrideKind.VehicleEvade) &&
                        now < coverHandles.MoveOverride.ExpireTime)
                    {
                        return;
                    }

                    if (coverHandles != null &&
                        (now < coverHandles.Stance.LocalCoverProtectedUntil || now < coverHandles.Stance.CoverSettledUntil))
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
                            IsSquadVehicleSeekingRepairStation(__instance) ||
                            IsSquadVehicleAnsweringTankSupport(__instance) ||
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
    }
}
