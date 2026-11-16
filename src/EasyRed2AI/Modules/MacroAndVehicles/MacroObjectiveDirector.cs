using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace EasyRed2HumanAI
{
    public static partial class VeteranAndTacticsPatches
    {
        private static int _macroDirectorSubPhase;
        private static float _macroDirectorStarvedSince;

        public static void UpdateMacroObjectiveAndEmplacementDirector()
        {
            if (!VisionConfig.EnableMod.Value)
                return;

            float now = Time.time;
            DrainOnePendingMacroSquadOrderIfReady(now);

            if (now < _nextMacroDirectorTime && _nextMacroDirectorTime - now <= 10.0f)
                return;

            if (_macroDirectorStarvedSince <= 0f || now - _macroDirectorStarvedSince > 15.0f)
                _macroDirectorStarvedSince = now;

            // Never run global squad/vehicle directors on a slow spike frame or on the same frame as dictionary pruning,
            // but allow moderate 60Hz VSync frames once overdue by >= 0.75s so Macro Director can never be starved!
            bool forceModerateSlot = (now - _macroDirectorStarvedSince) >= 0.75f;
            if (!AiCore.TryClaimHeavyFrameWorkSlot(forceModerateSlot))
                return;

            _macroDirectorStarvedSince = 0f;
            _nextMacroDirectorTime = now + 2.0f;

            int step = _macroDirectorSubPhase;
            _macroDirectorSubPhase = (step + 1) % 3;

            if (!AiCore.IsMasterClient())
            {
                if (step == 0 && VisionConfig.EnableMacroObjectiveDirector.Value)
                {
                    try { TryUpdateLocalClientSquadLeaderSuggestion(); } catch { }
                }
                return;
            }

            BattleManager bm = BattleManager.instance;
            if (bm == null || bm.Pointer == IntPtr.Zero || BattleManager.isBattleEnded || bm.isPreview)
            {
                _garrisonAssignedObjectiveBySquad.Clear();
                return;
            }

            // Note: Reworked vanilla & custom Mission Editor V2 (sp_ME_...) battles have bm.currentPhase == null
            // and store objectives in MissionObjectiveScripted.scriptedMissionObjectives instead of BattlePhase.objectives.
            // Never return early when phase == null so Garrisons, Emplacement Staffing, and TeamNet Anvil/Hammer all run!
            BattlePhase phase = AiCore.TryGetCurrentBattlePhase(bm);

            // Stagger the 3 heavy global passes across separate 2.0s steps (full cycle = 6.0s)
            // so Macro Objective assignment, Emplacement staffing, and TeamNet Anvil/Hammer never collide on one frame.
            HitchProfiler.RecordMacroStep(step);

            if (step == 0)
            {
                if (VisionConfig.EnableMacroObjectiveDirector.Value)
                {
                    long p0 = HitchProfiler.Begin();
                    try { RunMacroObjectiveDirector(phase); }
                    finally { HitchProfiler.End(HitchBucket.MacroDirector, p0); }
                }
                else if (_garrisonAssignedObjectiveBySquad.Count > 0)
                {
                    _garrisonAssignedObjectiveBySquad.Clear();
                }
            }
            else if (step == 1)
            {
                if (VisionConfig.EnableProactiveEmplacementStaffing.Value)
                {
                    long p1 = HitchProfiler.Begin();
                    try { RunProactiveEmplacementStaffing(now); }
                    finally { HitchProfiler.End(HitchBucket.Emplacements, p1); }
                }
            }
            else
            {
                // Tier 3 Team Net GOAP Coordinator (Anvil & Hammer multi-squad tactics across Radio-Connected squads)
                long p2 = HitchProfiler.Begin();
                try { RunTeamNetGoapCoordinator(now); }
                finally { HitchProfiler.End(HitchBucket.TeamNetGoap, p2); }
            }
        }

        private static readonly List<MissionObjective> _reusableActiveObjectives = new List<MissionObjective>(16);
        private static readonly List<MissionObjective> _reusableHeldObjectives = new List<MissionObjective>(16);
        private static readonly List<MissionObjective> _reusableUnheldObjectives = new List<MissionObjective>(16);
        private static readonly Dictionary<string, List<Squad>> _reusableSquadsByFaction = new Dictionary<string, List<Squad>>(4);
        private static readonly Dictionary<string, List<Squad>> _reusablePlayerSquadsByFaction = new Dictionary<string, List<Squad>>(4);
        private static readonly HashSet<string> _reusableActiveFactions = new HashSet<string>();
        private static readonly List<KeyValuePair<string, Vector3>> _reusableAllEnemyLeaderPositions = new List<KeyValuePair<string, Vector3>>(48);
        private static readonly HashSet<IntPtr> _reusableAssignedGarrisonSquads = new HashSet<IntPtr>(16);
        private static BattleData _currentDirectorBattleData;

        private struct ObjectiveAttritionKey : IEquatable<ObjectiveAttritionKey>
        {
            public string Faction;
            public IntPtr TaskPtr;

            public bool Equals(ObjectiveAttritionKey other)
            {
                return TaskPtr == other.TaskPtr &&
                       (ReferenceEquals(Faction, other.Faction) || string.Equals(Faction, other.Faction, StringComparison.Ordinal));
            }

            public override bool Equals(object obj) => obj is ObjectiveAttritionKey other && Equals(other);

            public override int GetHashCode()
            {
                int h1 = Faction != null ? StringComparer.Ordinal.GetHashCode(Faction) : 0;
                int h2 = TaskPtr.GetHashCode();
                return (h1 * 397) ^ h2;
            }
        }

        private struct ObjectiveAttritionState
        {
            public float DecayedLosses;
            public float LastUpdateTime;
            public float PivotCooldownUntil;
            public float AvoidUntil;
        }

        private struct SquadObjectiveLossTracker
        {
            public string Faction;
            public IntPtr TaskPtr;
            public Vector3 LastPos;
            public int LastAliveCount;
            public float LastSeenTime;
            public bool HadRadioContact;
        }

        private static readonly Dictionary<ObjectiveAttritionKey, ObjectiveAttritionState> _objectiveAttrition =
            new Dictionary<ObjectiveAttritionKey, ObjectiveAttritionState>(32);
        private static readonly Dictionary<IntPtr, SquadObjectiveLossTracker> _squadObjectiveLossTrackers =
            new Dictionary<IntPtr, SquadObjectiveLossTracker>(64);
        internal static readonly Dictionary<IntPtr, float> _nextSquadAttritionPivotTime =
            new Dictionary<IntPtr, float>(64);
        private static readonly List<ObjectiveAttritionKey> _reusableAttritionKeys = new List<ObjectiveAttritionKey>(32);
        private static readonly HashSet<IntPtr> _reusableActiveTaskPtrs = new HashSet<IntPtr>(16);
        private static readonly HashSet<IntPtr> _reusableSeenLossSquadPtrs = new HashSet<IntPtr>(64);
        private static readonly List<IntPtr> _reusableDeadLossSquadPtrs = new List<IntPtr>(32);

        private static bool TryGetObjectivePositionByTaskPtr(List<MissionObjective> activeObjectives, IntPtr taskPtr, out Vector3 objPos)
        {
            for (int i = 0; i < activeObjectives.Count; i++)
            {
                MissionObjective obj = activeObjectives[i];
                if (obj != null && obj.missionTask != null && obj.missionTask.Pointer == taskPtr)
                {
                    objPos = obj.GetTaskPosition();
                    return true;
                }
            }
            objPos = Vector3.zero;
            return false;
        }

        private static void RecordObjectiveAttritionCasualties(string faction, IntPtr taskPtr, int casualties, float now)
        {
            if (casualties <= 0 || taskPtr == IntPtr.Zero || string.IsNullOrEmpty(faction))
                return;

            var key = new ObjectiveAttritionKey { Faction = faction, TaskPtr = taskPtr };
            if (!_objectiveAttrition.TryGetValue(key, out ObjectiveAttritionState state))
            {
                state.LastUpdateTime = now;
            }
            state.DecayedLosses = Mathf.Min(28.0f, state.DecayedLosses + casualties);
            state.LastUpdateTime = now;
            _objectiveAttrition[key] = state;
        }

        private static void UpdateObjectiveAttritionTelemetry(List<MissionObjective> activeObjectives, float now)
        {
            _reusableActiveTaskPtrs.Clear();
            for (int i = 0; i < activeObjectives.Count; i++)
            {
                MissionObjective obj = activeObjectives[i];
                if (obj != null && obj.missionTask != null && obj.missionTask.Pointer != IntPtr.Zero)
                    _reusableActiveTaskPtrs.Add(obj.missionTask.Pointer);
            }

            if (_objectiveAttrition.Count > 0)
            {
                _reusableAttritionKeys.Clear();
                foreach (var k in _objectiveAttrition.Keys)
                    _reusableAttritionKeys.Add(k);

                for (int i = 0; i < _reusableAttritionKeys.Count; i++)
                {
                    ObjectiveAttritionKey key = _reusableAttritionKeys[i];
                    if (!_reusableActiveTaskPtrs.Contains(key.TaskPtr))
                    {
                        _objectiveAttrition.Remove(key);
                        continue;
                    }

                    ObjectiveAttritionState st = _objectiveAttrition[key];
                    float dt = Mathf.Max(0f, now - st.LastUpdateTime);
                    st.DecayedLosses *= Mathf.Exp(-dt * 0.014f); // ~49.5s half-life
                    st.LastUpdateTime = now;

                    if (st.DecayedLosses < 0.15f && now >= st.PivotCooldownUntil && now >= st.AvoidUntil)
                        _objectiveAttrition.Remove(key);
                    else
                        _objectiveAttrition[key] = st;
                }
                _reusableAttritionKeys.Clear();
            }

            _reusableSeenLossSquadPtrs.Clear();
            int sqCount = AiCore.WorldSquadCount;
            var worldSquads = AiCore.WorldSquads;
            const float maxAttritionRadiusSq = 195.0f * 195.0f;

            for (int s = 0; s < sqCount; s++)
            {
                ref AiCore.SquadSnapshot sqSnap = ref worldSquads[s];
                if (sqSnap.Ptr == IntPtr.Zero || sqSnap.UnitCount <= 0 || string.IsNullOrEmpty(sqSnap.Faction))
                    continue;

                _reusableSeenLossSquadPtrs.Add(sqSnap.Ptr);
                if (sqSnap.IsVehicleCrew || sqSnap.RefOnPlane)
                    continue;

                Squad sq = sqSnap.Ref;
                IntPtr curTaskPtr = (sq != null && sq.currentTask != null) ? sq.currentTask.Pointer : IntPtr.Zero;
                bool hasRadio = !VisionConfig.EnableRadiomanIntelNet.Value ||
                                sqSnap.HasRadioOrRelay ||
                                (sq != null && IsSquadConnectedToRadioNet(sq));

                if (_squadObjectiveLossTrackers.TryGetValue(sqSnap.Ptr, out SquadObjectiveLossTracker prev))
                {
                    int lost = prev.LastAliveCount - sqSnap.UnitCount;
                    if (lost > 0 &&
                        prev.TaskPtr != IntPtr.Zero &&
                        _reusableActiveTaskPtrs.Contains(prev.TaskPtr) &&
                        (curTaskPtr == prev.TaskPtr || curTaskPtr == IntPtr.Zero) &&
                        now - prev.LastSeenTime <= 20.0f &&
                        (hasRadio || prev.HadRadioContact) &&
                        TryGetObjectivePositionByTaskPtr(activeObjectives, prev.TaskPtr, out Vector3 objPos))
                    {
                        float curDistSq = (sqSnap.RefPos - objPos).sqrMagnitude;
                        float prevDistSq = (prev.LastPos - objPos).sqrMagnitude;
                        if (curDistSq <= maxAttritionRadiusSq || prevDistSq <= maxAttritionRadiusSq)
                        {
                            RecordObjectiveAttritionCasualties(sqSnap.Faction, prev.TaskPtr, lost, now);
                        }
                    }
                }

                _squadObjectiveLossTrackers[sqSnap.Ptr] = new SquadObjectiveLossTracker
                {
                    Faction = sqSnap.Faction,
                    TaskPtr = curTaskPtr,
                    LastPos = sqSnap.RefPos,
                    LastAliveCount = sqSnap.UnitCount,
                    LastSeenTime = now,
                    HadRadioContact = hasRadio
                };
            }

            if (_squadObjectiveLossTrackers.Count > 0)
            {
                _reusableDeadLossSquadPtrs.Clear();
                foreach (var kvp in _squadObjectiveLossTrackers)
                {
                    if (!_reusableSeenLossSquadPtrs.Contains(kvp.Key))
                        _reusableDeadLossSquadPtrs.Add(kvp.Key);
                }

                for (int i = 0; i < _reusableDeadLossSquadPtrs.Count; i++)
                {
                    IntPtr deadPtr = _reusableDeadLossSquadPtrs[i];
                    SquadObjectiveLossTracker prev = _squadObjectiveLossTrackers[deadPtr];
                    _squadObjectiveLossTrackers.Remove(deadPtr);

                    if (prev.LastAliveCount > 0 &&
                        prev.TaskPtr != IntPtr.Zero &&
                        _reusableActiveTaskPtrs.Contains(prev.TaskPtr) &&
                        now - prev.LastSeenTime <= 20.0f &&
                        TryGetObjectivePositionByTaskPtr(activeObjectives, prev.TaskPtr, out Vector3 objPos) &&
                        (prev.LastPos - objPos).sqrMagnitude <= maxAttritionRadiusSq)
                    {
                        bool wipeReported = !VisionConfig.EnableRadiomanIntelNet.Value || prev.HadRadioContact;
                        if (!wipeReported)
                        {
                            // A nearby friendly radio-linked squad within 95m witnesses the squad wipe and reports it on the Net
                            for (int s = 0; s < sqCount; s++)
                            {
                                ref AiCore.SquadSnapshot ally = ref worldSquads[s];
                                if (ally.UnitCount > 0 &&
                                    ally.HasRadioOrRelay &&
                                    AiCore.AreSameSideFactions(prev.Faction, ally.Faction) &&
                                    (ally.RefPos - prev.LastPos).sqrMagnitude <= 95.0f * 95.0f)
                                {
                                    wipeReported = true;
                                    break;
                                }
                            }
                        }

                        if (wipeReported)
                        {
                            RecordObjectiveAttritionCasualties(prev.Faction, prev.TaskPtr, Mathf.Min(prev.LastAliveCount, 6), now);
                        }
                    }
                }
                _reusableDeadLossSquadPtrs.Clear();
            }
            _reusableSeenLossSquadPtrs.Clear();
        }

        private static void CollectActiveMacroObjectives(BattlePhase phase, List<MissionObjective> activeObjectives)
        {
            AiCore.CollectActiveMissionObjectives(phase, activeObjectives);
        }

        private static void TryUpdateLocalClientSquadLeaderSuggestion()
        {
            Soldier localPlayer = Soldier.CurrentControlledSoldierOrNull();
            if (ReferenceEquals(localPlayer, null) || localPlayer == null || !localPlayer.IsAlive || string.IsNullOrEmpty(localPlayer.faction))
                return;

            Squad sq = localPlayer.joinedSquad;
            if (sq == null || sq.Pointer == IntPtr.Zero || sq.Leader == null || sq.Leader.Pointer != localPlayer.Pointer)
                return;

            if (AiCore.IsTrueVehicleCrewSquad(sq) || !IsSquadConnectedToRadioNet(sq))
                return;

            BattleManager bm = BattleManager.instance;
            if (bm == null || bm.Pointer == IntPtr.Zero || BattleManager.isBattleEnded || bm.isPreview)
                return;

            BattlePhase phase = AiCore.TryGetCurrentBattlePhase(bm);
            var activeObjectives = _reusableActiveObjectives;
            CollectActiveMacroObjectives(phase, activeObjectives);
            if (activeObjectives.Count == 0)
                return;

            BattleData bd = null;
            try { bd = BattleManager.GetCurrentBattleData(); } catch { }
            _currentDirectorBattleData = bd;

            string faction = localPlayer.faction;
            var heldObjectives = _reusableHeldObjectives;
            var unheldObjectives = _reusableUnheldObjectives;
            heldObjectives.Clear();
            unheldObjectives.Clear();

            for (int i = 0; i < activeObjectives.Count; i++)
            {
                MissionObjective obj = activeObjectives[i];
                if (AiCore.ClassifyMissionObjectiveForFaction(obj, faction, bd, out bool isHeld, out bool canAttract, out _))
                {
                    if (isHeld)
                        heldObjectives.Add(obj);
                    else if (canAttract)
                        unheldObjectives.Add(obj);
                }
            }

            var allEnemyLeaderPositions = _reusableAllEnemyLeaderPositions;
            allEnemyLeaderPositions.Clear();
            AiCore.EnsureWorldSnapshot();
            int sqCount = AiCore.WorldSquadCount;
            var worldSquads = AiCore.WorldSquads;
            for (int s = 0; s < sqCount; s++)
            {
                ref AiCore.SquadSnapshot sqSnap = ref worldSquads[s];
                if (sqSnap.LeaderIsAlive && sqSnap.UnitCount > 0 && !string.IsNullOrEmpty(sqSnap.Faction))
                    allEnemyLeaderPositions.Add(new KeyValuePair<string, Vector3>(sqSnap.Faction, sqSnap.RefPos));
            }

            TrySuggestObjectiveForPlayerSquadLeader(sq, faction, heldObjectives, unheldObjectives, null, allEnemyLeaderPositions);
        }

        private static void RunMacroObjectiveDirector(BattlePhase phase)
        {
            var activeObjectives = _reusableActiveObjectives;
            CollectActiveMacroObjectives(phase, activeObjectives);

            if (activeObjectives.Count == 0)
            {
                _garrisonAssignedObjectiveBySquad.Clear();
                _squadTaskCommittedUntil.Clear();
                return;
            }

            AiCore.EnsureWorldSnapshot();
            int sqCount = AiCore.WorldSquadCount;
            if (sqCount == 0)
                return;

            float now = Time.time;
            UpdateObjectiveAttritionTelemetry(activeObjectives, now);

            var squadsByFaction = _reusableSquadsByFaction;
            var playerSquadsByFaction = _reusablePlayerSquadsByFaction;
            var activeFactions = _reusableActiveFactions;
            var allEnemyLeaderPositions = _reusableAllEnemyLeaderPositions;

            foreach (var kv in squadsByFaction) kv.Value.Clear();
            foreach (var kv in playerSquadsByFaction) kv.Value.Clear();
            activeFactions.Clear();
            allEnemyLeaderPositions.Clear();

            var worldSquads = AiCore.WorldSquads;
            for (int s = 0; s < sqCount; s++)
            {
                ref AiCore.SquadSnapshot sqSnap = ref worldSquads[s];
                if (!sqSnap.LeaderIsAlive || sqSnap.UnitCount == 0 || string.IsNullOrEmpty(sqSnap.Faction))
                    continue;

                Squad sq = sqSnap.Ref;
                Soldier leader = sqSnap.Leader;
                if (sq == null || leader == null)
                    continue;

                allEnemyLeaderPositions.Add(new KeyValuePair<string, Vector3>(sqSnap.Faction, sqSnap.RefPos));

                // When the Human Player is the Squad Leader, vanilla SoldierAI.SquadOrdersRoutine does not run,
                // so if the player's squad has no active objective (or their previous objective was captured/lost)
                // and is connected to the Team Radio Net, let the Team Net suggest the next priority objective
                // (strictly on the local player's machine so Host never overwrites remote clients)!
                if (leader.IsPlayer())
                {
                    _garrisonAssignedObjectiveBySquad.Remove(sqSnap.Ptr);
                    bool isLocalSl = AiCore.IsLocalPlayerInSquad(sq, out bool isLocalLeader) && isLocalLeader;
                    bool isRemoteCoopSl = !isLocalSl &&
                                          AiCore.IsInMultiplayerRoom() &&
                                          AiCore.IsMasterClient() &&
                                          leader.IsPlayer();
                    if ((isLocalSl || isRemoteCoopSl) &&
                        !sqSnap.IsVehicleCrew &&
                        IsSquadConnectedToRadioNet(sq))
                    {
                        if (!playerSquadsByFaction.TryGetValue(sqSnap.Faction, out List<Squad> pList))
                        {
                            pList = new List<Squad>(4);
                            playerSquadsByFaction[sqSnap.Faction] = pList;
                        }
                        pList.Add(sq);
                        activeFactions.Add(sqSnap.Faction);
                    }
                    continue;
                }

                if (sqSnap.IsVehicleCrew || sqSnap.LeaderOnVehicle || !AiCore.IsLocallyOwnedBot(leader))
                {
                    _garrisonAssignedObjectiveBySquad.Remove(sqSnap.Ptr);
                    continue;
                }

                if (leader.Params != null && (!leader.Params.canGiveOrders || !leader.Params.canMove))
                {
                    _garrisonAssignedObjectiveBySquad.Remove(sqSnap.Ptr);
                    continue;
                }

                // Tier 3 Team Net Gate: Only squads that can hear the Team Radio Net can be dynamically
                // re-tasked across objectives by the Macro Director. Disconnected squads stay on their last known task!
                if (!IsSquadConnectedToRadioNet(sq))
                {
                    _garrisonAssignedObjectiveBySquad.Remove(sqSnap.Ptr);
                    continue;
                }

                if (!squadsByFaction.TryGetValue(sqSnap.Faction, out List<Squad> fList))
                {
                    fList = new List<Squad>(16);
                    squadsByFaction[sqSnap.Faction] = fList;
                }
                fList.Add(sq);
                activeFactions.Add(sqSnap.Faction);
            }

            BattleData bd = null;
            try { bd = BattleManager.GetCurrentBattleData(); } catch { }
            _currentDirectorBattleData = bd;

            foreach (string faction in activeFactions)
            {
                var heldObjectives = _reusableHeldObjectives;
                var unheldObjectives = _reusableUnheldObjectives;
                heldObjectives.Clear();
                unheldObjectives.Clear();

                for (int i = 0; i < activeObjectives.Count; i++)
                {
                    MissionObjective obj = activeObjectives[i];
                    if (AiCore.ClassifyMissionObjectiveForFaction(obj, faction, bd, out bool isHeld, out bool canAttract, out _))
                    {
                        if (isHeld)
                            heldObjectives.Add(obj);
                        else if (canAttract)
                            unheldObjectives.Add(obj);
                    }
                }

                // Suggest an objective over the Team Radio Net for any Player Squad Leader who has no active objective
                if (playerSquadsByFaction.TryGetValue(faction, out List<Squad> playerSquads))
                {
                    squadsByFaction.TryGetValue(faction, out List<Squad> facAiSquads);
                    for (int pIdx = 0; pIdx < playerSquads.Count; pIdx++)
                    {
                        TrySuggestObjectiveForPlayerSquadLeader(
                            playerSquads[pIdx],
                            faction,
                            heldObjectives,
                            unheldObjectives,
                            facAiSquads,
                            allEnemyLeaderPositions);
                    }
                }

                if (!squadsByFaction.TryGetValue(faction, out List<Squad> eligibleSquads) || eligibleSquads.Count == 0)
                    continue;

                // If this faction holds 1+ objectives while also having 1+ uncaptured/contested objectives to attack:
                // Allocate faction-doctrine garrison share (e.g. 28% UK/France/Italy vs 22% default vs 16% USSR/Japan/Mongolia)
                // to garrison held points, prioritizing held objectives that have enemy squads within 150m.
                if (heldObjectives.Count > 0 && unheldObjectives.Count > 0 && eligibleSquads.Count >= 2)
                {
                    // Sort held objectives so threatened or actively back-capped objectives get garrisoned first
                    heldObjectives.Sort((a, b) =>
                    {
                        float dA = GetObjectiveThreatPriorityScore(a, faction, allEnemyLeaderPositions);
                        float dB = GetObjectiveThreatPriorityScore(b, faction, allEnemyLeaderPositions);
                        return dA.CompareTo(dB);
                    });

                    int contestedHeldCount = 0;
                    for (int h = 0; h < heldObjectives.Count; h++)
                    {
                        if (GetObjectiveThreatPriorityScore(heldObjectives[h], faction, allEnemyLeaderPositions) <= 135f * 135f)
                            contestedHeldCount++;
                    }

                    float garrisonRatio = GetFactionCommandProfile(faction).GarrisonSquadRatio;
                    int desiredGarrisons = Mathf.Max(Mathf.RoundToInt(eligibleSquads.Count * garrisonRatio), contestedHeldCount);
                    int targetGarrisonCount = Mathf.Clamp(
                        desiredGarrisons,
                        1,
                        Mathf.Min(heldObjectives.Count, eligibleSquads.Count - 1));

                    var assignedGarrisonSquads = _reusableAssignedGarrisonSquads;
                    assignedGarrisonSquads.Clear();
                    for (int g = 0; g < targetGarrisonCount; g++)
                    {
                        MissionObjective heldObj = heldObjectives[g % heldObjectives.Count];
                        IntPtr heldTaskPtr = heldObj.missionTask.Pointer;
                        Vector3 objPos = heldObj.GetTaskPosition();
                        Squad bestSquad = null;
                        float bestScore = float.MaxValue;

                        for (int sIdx = 0; sIdx < eligibleSquads.Count; sIdx++)
                        {
                            Squad cand = eligibleSquads[sIdx];
                            if (assignedGarrisonSquads.Contains(cand.Pointer))
                                continue;

                            bool alreadyOnThisGarrison =
                                (_garrisonAssignedObjectiveBySquad.TryGetValue(cand.Pointer, out IntPtr curTaskPtr) && curTaskPtr == heldTaskPtr) ||
                                (cand.currentTask != null && cand.currentTask.Pointer == heldTaskPtr);

                            // Never yank a squad that is already committed to attacking an active unheld objective,
                            // locked in an active close firefight, or contains the human player back to rear garrison duty!
                            if (!alreadyOnThisGarrison &&
                                (IsSquadCommittedToActiveObjective(cand, unheldObjectives, now) || IsSquadLockedInCloseFirefight(cand)))
                                continue;

                            float dSq = (cand.Leader.transform.position - objPos).sqrMagnitude;
                            // Strong continuity bonus if this squad is already garrisoning this objective
                            if (alreadyOnThisGarrison)
                            {
                                dSq *= 0.25f;
                            }

                            if (dSq < bestScore)
                            {
                                bestScore = dSq;
                                bestSquad = cand;
                            }
                        }

                        if (bestSquad != null)
                        {
                            assignedGarrisonSquads.Add(bestSquad.Pointer);
                            bool isNewGarrison = !_garrisonAssignedObjectiveBySquad.TryGetValue(bestSquad.Pointer, out IntPtr prevTaskPtr) ||
                                                 prevTaskPtr != heldTaskPtr;
                            _garrisonAssignedObjectiveBySquad[bestSquad.Pointer] = heldTaskPtr;

                            if (isNewGarrison || !IsSquadProperlyHoldingGarrison(bestSquad, heldObj, heldTaskPtr))
                            {
                                AssignAiSquadMacroTask(bestSquad, heldObj, now, 50.0f, isGarrisonHold: true);
                                ConsumeInitialHqSpawnBriefingIfNoRadio(bestSquad);
                                if (isNewGarrison)
                                {
                                    MacroGarrisonsAssignedCount++;
                                    if (VisionConfig.DebugLogging.Value)
                                    {
                                        AiCore.LogDebug("MacroDirector", $"{faction} squad '{bestSquad.squadName}' assigned to garrison held objective '{GetObjectiveDebugLabel(heldObj)}'.");
                                    }
                                }
                            }
                        }
                    }

                    // Ensure all non-garrison squads push/counterattack the uncaptured objectives instead of lingering on a captured point
                    for (int sIdx = 0; sIdx < eligibleSquads.Count; sIdx++)
                    {
                        Squad sq = eligibleSquads[sIdx];
                        if (assignedGarrisonSquads.Contains(sq.Pointer))
                            continue;

                        _garrisonAssignedObjectiveBySquad.Remove(sq.Pointer);

                        // Only reassign if sq.currentTask is null or no longer in unheldObjectives (e.g., objective was just captured!)
                        bool onActiveUnheld = IsSquadTaskInObjectiveList(sq, unheldObjectives);
                        if (!onActiveUnheld)
                        {
                            MissionObjective bestUnheld = PickBestAttackObjectiveForSquad(sq.Leader.transform.position, faction, unheldObjectives, eligibleSquads);
                            if (bestUnheld != null && bestUnheld.missionTask != null)
                            {
                                AssignAiSquadMacroTask(sq, bestUnheld, now, 45.0f);
                                ConsumeInitialHqSpawnBriefingIfNoRadio(sq);
                                if (VisionConfig.DebugLogging.Value)
                                {
                                    AiCore.LogDebug("MacroDirector", $"{faction} squad '{sq.squadName}' directed to attack/counterattack '{GetObjectiveDebugLabel(bestUnheld)}'.");
                                }
                            }
                        }
                        else
                        {
                            ConsumeInitialHqSpawnBriefingIfNoRadio(sq);
                        }
                    }

                    if (unheldObjectives.Count >= 2 && eligibleSquads.Count >= 2)
                    {
                        TryExecuteTeamNetAttritionPivot(faction, bd, unheldObjectives, eligibleSquads, assignedGarrisonSquads, now);
                    }
                }
                else if (heldObjectives.Count > 1 && unheldObjectives.Count == 0 && eligibleSquads.Count >= 2)
                {
                    // Pure defense across multiple held objectives (e.g., Invasion Defenders):
                    // Clear any stale garrison overrides so captured objectives never remain pinned in TaskCanStillAttract,
                    // and reassign any squad whose currentTask is null or no longer in heldObjectives.
                    for (int sIdx = 0; sIdx < eligibleSquads.Count; sIdx++)
                    {
                        Squad sq = eligibleSquads[sIdx];
                        MissionObjective activeHeld = null;
                        if (sq.currentTask != null && sq.currentTask.Pointer != IntPtr.Zero)
                        {
                            IntPtr curPtr = sq.currentTask.Pointer;
                            for (int h = 0; h < heldObjectives.Count; h++)
                            {
                                if (heldObjectives[h].missionTask != null && heldObjectives[h].missionTask.Pointer == curPtr)
                                {
                                    activeHeld = heldObjectives[h];
                                    break;
                                }
                            }
                        }

                        if (activeHeld == null)
                        {
                            activeHeld = GetClosestObjectiveToPosition(sq.Leader.transform.position, faction, heldObjectives);
                            if (activeHeld != null && activeHeld.missionTask != null)
                            {
                                AssignAiSquadMacroTask(sq, activeHeld, now, 45.0f, isGarrisonHold: true);
                            }
                        }
                        else if (activeHeld.missionTask != null &&
                                 !IsSquadProperlyHoldingGarrison(sq, activeHeld, activeHeld.missionTask.Pointer))
                        {
                            AssignAiSquadMacroTask(sq, activeHeld, now, 45.0f, isGarrisonHold: true);
                        }

                        // On Conquest / PushTheFrontline total-out (when holding all flags and CanAttractFaction == false),
                        // maintain _garrisonAssignedObjectiveBySquad so TaskCanStillAttract keeps defenders on the flags!
                        if (activeHeld != null && activeHeld.missionTask != null && !activeHeld.CanAttractFaction(faction))
                            _garrisonAssignedObjectiveBySquad[sq.Pointer] = activeHeld.missionTask.Pointer;
                        else
                            _garrisonAssignedObjectiveBySquad.Remove(sq.Pointer);

                        ConsumeInitialHqSpawnBriefingIfNoRadio(sq);
                    }

                    // Vanilla assigns defenders via GetRandomNonSecuredMissionObjective() once at spawn and never rebalances.
                    // Sort held objectives so the most threatened or actively back-capped objective is index 0.
                    heldObjectives.Sort((a, b) =>
                    {
                        float dA = GetObjectiveThreatPriorityScore(a, faction, allEnemyLeaderPositions);
                        float dB = GetObjectiveThreatPriorityScore(b, faction, allEnemyLeaderPositions);
                        return dA.CompareTo(dB);
                    });

                    // 1) Ensure any threatened or actively back-capped held objective (enemy within 165m or capture bar ticking) has at least 1 defending squad
                    for (int h = 0; h < heldObjectives.Count; h++)
                    {
                        MissionObjective heldObj = heldObjectives[h];
                        IntPtr heldTaskPtr = heldObj.missionTask.Pointer;
                        Vector3 objPos = heldObj.GetTaskPosition();
                        float closestEnemySq = GetObjectiveThreatPriorityScore(heldObj, faction, allEnemyLeaderPositions);
                        if (closestEnemySq > 165f * 165f)
                            continue;

                        bool hasDefenderAssigned = CountAlliedGroundSquadsOnObjective(faction, heldTaskPtr, objPos, true) > 0;

                        if (!hasDefenderAssigned)
                        {
                            bool isUrgentRescue = closestEnemySq <= 85f * 85f;
                            Squad bestReserve = PickBestReserveDefenderSquad(
                                eligibleSquads,
                                heldObjectives,
                                heldTaskPtr,
                                objPos,
                                now,
                                isUrgentRescue,
                                faction,
                                allEnemyLeaderPositions);
                            if (bestReserve != null)
                            {
                                if (!heldObj.CanAttractFaction(faction))
                                    _garrisonAssignedObjectiveBySquad[bestReserve.Pointer] = heldTaskPtr;
                                AssignAiSquadMacroTask(bestReserve, heldObj, now, 50.0f, releaseStaleCovers: true, isGarrisonHold: true);
                                MacroGarrisonsAssignedCount++;
                                if (VisionConfig.DebugLogging.Value)
                                {
                                    AiCore.LogDebug("MacroDirector", $"{faction} reserve squad '{bestReserve.squadName}' shifted to defend threatened objective '{GetObjectiveDebugLabel(heldObj)}'.");
                                }
                            }
                        }
                    }

                    // 2) If the primary threatened objective (enemy within 155m or capture progress ticking) is under heavy attack while an unthreatened
                    //    secondary objective (no enemy within 175m and 0% capture progress) has 2+ squads sitting idle, shift 1 excess squad per tick
                    //    (always leaving at least 1 sentry squad on each secondary objective).
                    MissionObjective primaryThreatObj = heldObjectives[0];
                    float primaryThreatSq = GetObjectiveThreatPriorityScore(primaryThreatObj, faction, allEnemyLeaderPositions);
                    if (primaryThreatSq <= 155f * 155f)
                    {
                        IntPtr primaryTaskPtr = primaryThreatObj.missionTask.Pointer;
                        Vector3 primaryPos = primaryThreatObj.GetTaskPosition();

                        for (int h = heldObjectives.Count - 1; h >= 1; h--)
                        {
                            MissionObjective quietObj = heldObjectives[h];
                            float quietEnemySq = GetObjectiveThreatPriorityScore(quietObj, faction, allEnemyLeaderPositions);
                            if (quietEnemySq <= 175f * 175f)
                                continue;

                            IntPtr quietTaskPtr = quietObj.missionTask.Pointer;
                            int quietSquadCount = 0;
                            Squad closestExcessSquad = null;
                            float closestExcessDistSq = float.MaxValue;

                            for (int sIdx = 0; sIdx < eligibleSquads.Count; sIdx++)
                            {
                                Squad sq = eligibleSquads[sIdx];
                                if (sq.currentTask != null && sq.currentTask.Pointer == quietTaskPtr)
                                {
                                    quietSquadCount++;
                                    // Never strip player squads, recently reassigned squads, or squads in active local firefights
                                    if (sq.IsPlayerInSquad() ||
                                        (_squadTaskCommittedUntil.TryGetValue(sq.Pointer, out float qCommit) && now < qCommit && primaryThreatSq > 55f * 55f) ||
                                        (_squadBlackboards.TryGetValue(sq.Pointer, out SquadBlackboard qBoard) && qBoard.HasActiveThreat))
                                    {
                                        continue;
                                    }

                                    float dSq = (sq.Leader.transform.position - primaryPos).sqrMagnitude;
                                    if (dSq > 65f * 65f && !IsSquadConnectedToRadioNet(sq))
                                        continue;

                                    if (dSq < closestExcessDistSq)
                                    {
                                        closestExcessDistSq = dSq;
                                        closestExcessSquad = sq;
                                    }
                                }
                            }

                            int totalQuietDefenders = Mathf.Max(
                                quietSquadCount,
                                CountAlliedGroundSquadsOnObjective(faction, quietTaskPtr, quietObj.GetTaskPosition(), false));
                            if (totalQuietDefenders >= 2 && closestExcessSquad != null)
                            {
                                if (!primaryThreatObj.CanAttractFaction(faction))
                                    _garrisonAssignedObjectiveBySquad[closestExcessSquad.Pointer] = primaryTaskPtr;
                                AssignAiSquadMacroTask(closestExcessSquad, primaryThreatObj, now, 50.0f, releaseStaleCovers: true, isGarrisonHold: true);
                                MacroGarrisonsAssignedCount++;
                                if (VisionConfig.DebugLogging.Value)
                                {
                                    AiCore.LogDebug("MacroDirector", $"{faction} excess defender squad '{closestExcessSquad.squadName}' shifted from quiet '{GetObjectiveDebugLabel(quietObj)}' to reinforce '{GetObjectiveDebugLabel(primaryThreatObj)}'.");
                                }
                                break;
                            }
                        }
                    }
                }
                else
                {
                    MissionObjective singleNonAttractHeld =
                        (heldObjectives.Count == 1 && unheldObjectives.Count == 0 && !heldObjectives[0].CanAttractFaction(faction))
                            ? heldObjectives[0]
                            : null;

                    for (int sIdx = 0; sIdx < eligibleSquads.Count; sIdx++)
                    {
                        Squad sq = eligibleSquads[sIdx];
                        if (singleNonAttractHeld != null && singleNonAttractHeld.missionTask != null)
                        {
                            IntPtr heldPtr = singleNonAttractHeld.missionTask.Pointer;
                            _garrisonAssignedObjectiveBySquad[sq.Pointer] = heldPtr;
                            if (!IsSquadProperlyHoldingGarrison(sq, singleNonAttractHeld, heldPtr))
                                AssignAiSquadMacroTask(sq, singleNonAttractHeld, now, 45.0f, isGarrisonHold: true);
                            continue;
                        }

                        _garrisonAssignedObjectiveBySquad.Remove(sq.Pointer);

                        // In pure attack (heldObjectives.Count == 0 && unheldObjectives.Count > 0),
                        // if a squad's previous objective was just captured, distribute squads intelligently across
                        // remaining unheld objectives (prioritizing unstaffed and actively progressing flags)
                        // and update the HUD if the player is a member of this squad!
                        if (unheldObjectives.Count > 0 && !IsSquadTaskInObjectiveList(sq, unheldObjectives))
                        {
                            MissionObjective nextAttack = PickBestAttackObjectiveForSquad(sq.Leader.transform.position, faction, unheldObjectives, eligibleSquads);
                            if (nextAttack != null && nextAttack.missionTask != null)
                            {
                                AssignAiSquadMacroTask(sq, nextAttack, now, 45.0f);
                            }
                        }
                        ConsumeInitialHqSpawnBriefingIfNoRadio(sq);
                    }

                    if (unheldObjectives.Count >= 2 && eligibleSquads.Count >= 2)
                    {
                        TryExecuteTeamNetAttritionPivot(faction, bd, unheldObjectives, eligibleSquads, null, now);
                    }
                }
            }
        }

        private static void TryExecuteTeamNetAttritionPivot(
            string faction,
            BattleData bd,
            List<MissionObjective> unheldObjectives,
            List<Squad> eligibleSquads,
            HashSet<IntPtr> garrisonSquads,
            float now)
        {
            if (unheldObjectives == null || unheldObjectives.Count < 2 || eligibleSquads == null || eligibleSquads.Count < 2)
                return;

            float threshold = GetFactionCommandProfile(faction).AttritionPivotThreshold;
            MissionObjective worstStalledObj = null;
            ObjectiveAttritionKey worstKey = default;
            ObjectiveAttritionState worstState = default;
            float worstLosses = threshold;

            for (int i = 0; i < unheldObjectives.Count; i++)
            {
                MissionObjective cand = unheldObjectives[i];
                if (cand == null || cand.missionTask == null || cand.missionTask.Pointer == IntPtr.Zero)
                    continue;

                var key = new ObjectiveAttritionKey { Faction = faction, TaskPtr = cand.missionTask.Pointer };
                if (!_objectiveAttrition.TryGetValue(key, out ObjectiveAttritionState st))
                    continue;

                if (!AiCore.ClassifyMissionObjectiveForFaction(cand, faction, bd, out bool isHeld, out _, out float urgency) || isHeld)
                    continue;

                // Breakthrough underway: friendly troops are actively capturing the zone (>= 30% progress), so push through!
                if (urgency >= 0.30f)
                {
                    st.DecayedLosses *= 0.65f;
                    _objectiveAttrition[key] = st;
                    continue;
                }

                if (now < st.PivotCooldownUntil || urgency >= 0.20f || st.DecayedLosses < worstLosses)
                    continue;

                worstLosses = st.DecayedLosses;
                worstStalledObj = cand;
                worstKey = key;
                worstState = st;
            }

            if (worstStalledObj == null)
                return;

            IntPtr stalledTaskPtr = worstKey.TaskPtr;
            Vector3 stalledPos = worstStalledObj.GetTaskPosition();

            // Anti-ping-pong comparative gate: only pivot off the meatgrinder if at least one OTHER active unheld
            // objective either has a genuine active breakthrough (>= 40% cap) OR is clearly less bloody with AvoidUntil expired!
            bool hasViableAlternative = false;
            for (int i = 0; i < unheldObjectives.Count; i++)
            {
                MissionObjective alt = unheldObjectives[i];
                if (alt == null || alt.missionTask == null || alt.missionTask.Pointer == IntPtr.Zero || alt.missionTask.Pointer == stalledTaskPtr)
                    continue;

                var altKey = new ObjectiveAttritionKey { Faction = faction, TaskPtr = alt.missionTask.Pointer };
                _objectiveAttrition.TryGetValue(altKey, out ObjectiveAttritionState altState);

                float altUrgency = 0f;
                AiCore.ClassifyMissionObjectiveForFaction(alt, faction, bd, out _, out _, out altUrgency);

                bool isGenuineBreakthrough = altUrgency >= 0.40f;
                bool isClearlySofter = now >= altState.AvoidUntil &&
                                       altState.DecayedLosses < worstState.DecayedLosses * 0.55f &&
                                       (worstState.DecayedLosses - altState.DecayedLosses) >= 3.0f &&
                                       (altUrgency >= 0.20f || altState.DecayedLosses < threshold * 0.85f);
                if (isGenuineBreakthrough || isClearlySofter)
                {
                    hasViableAlternative = true;
                    break;
                }
            }

            if (!hasViableAlternative)
                return;

            // Count attacking squads currently on stalledObj and check whether non-eligible friendly forces
            // (players, disconnected AI squads, tanks) are already fixing the point before holding back an extra squad!
            int squadsOnStalled = 0;
            IntPtr pinningSquadPtr = IntPtr.Zero;
            float closestDistSq = float.MaxValue;

            for (int s = 0; s < eligibleSquads.Count; s++)
            {
                Squad sq = eligibleSquads[s];
                if (sq == null || sq.Leader == null || sq.Leader.transform == null)
                    continue;
                if (garrisonSquads != null && garrisonSquads.Contains(sq.Pointer))
                    continue;
                if (sq.currentTask == null || sq.currentTask.Pointer != stalledTaskPtr)
                    continue;

                squadsOnStalled++;
                float dSq = (sq.Leader.transform.position - stalledPos).sqrMagnitude;
                if (dSq < closestDistSq)
                {
                    closestDistSq = dSq;
                    pinningSquadPtr = sq.Pointer;
                }
            }

            if (squadsOnStalled == 0)
                return;

            int totalAlliedOnStalled = CountAlliedGroundSquadsOnObjective(faction, stalledTaskPtr, stalledPos, true);
            int nonEligibleFixingUnits = Mathf.Max(0, totalAlliedOnStalled - squadsOnStalled);
            bool keepFixingSquad = nonEligibleFixingUnits == 0 && eligibleSquads.Count >= 3 && squadsOnStalled >= 2;

            // Stamp AvoidUntil BEFORE PickBestAttackObjectiveForSquad runs so pivoting squads naturally avoid stalledObj
            worstState.PivotCooldownUntil = now + 90.0f;
            worstState.AvoidUntil = now + 105.0f;
            _objectiveAttrition[worstKey] = worstState;

            int pivotedNow = 0;
            string stalledName = null;

            for (int s = 0; s < eligibleSquads.Count; s++)
            {
                Squad sq = eligibleSquads[s];
                if (sq == null || sq.Leader == null || sq.Leader.transform == null)
                    continue;
                if (garrisonSquads != null && garrisonSquads.Contains(sq.Pointer))
                    continue;
                if (sq.currentTask == null || sq.currentTask.Pointer != stalledTaskPtr)
                    continue;
                if (keepFixingSquad && sq.Pointer == pinningSquadPtr)
                    continue;
                if (_nextSquadAttritionPivotTime.TryGetValue(sq.Pointer, out float nextPivot) && now < nextPivot)
                    continue;

                if (!CanSquadDisengageForAttritionPivot(sq, stalledPos, out bool shouldPopDisengageSmoke))
                    continue;

                MissionObjective pivotObj = PickBestAttackObjectiveForSquad(
                    sq.Leader.transform.position,
                    faction,
                    unheldObjectives,
                    eligibleSquads);

                if (pivotObj == null || pivotObj.missionTask == null || pivotObj.missionTask.Pointer == stalledTaskPtr)
                    continue;

                if (shouldPopDisengageSmoke && _squadBlackboards.TryGetValue(sq.Pointer, out SquadBlackboard pivBoard))
                {
                    TryPopSquadRetreatSmoke(sq, sq.Leader, ref pivBoard, now);
                    _squadBlackboards[sq.Pointer] = pivBoard;
                }

                _nextSquadAttritionPivotTime[sq.Pointer] = now + 135.0f;
                _pincerOrderedTaskBySquad.Remove(sq.Pointer);
                AssignAiSquadMacroTask(sq, pivotObj, now, 80.0f, releaseStaleCovers: true);
                TrySayTacticalVoice(sq.Leader, sq, VoiceManager.VoiceClip.attackThere, 0.20f);
                MacroObjectivePivotsCount++;
                pivotedNow++;

                if (VisionConfig.DebugLogging.Value)
                {
                    if (stalledName == null)
                        stalledName = GetObjectiveDebugLabel(worstStalledObj);
                    AiCore.LogDebug(
                        "AttritionPivot",
                        $"{faction} Team Net pivoted squad '{sq.squadName}' off high-loss '{stalledName}' (losses:{worstState.DecayedLosses:F1} >= {threshold:F1}) -> '{GetObjectiveDebugLabel(pivotObj)}'.",
                        3.0f);
                }
            }

            // If no eligible squad could break contact (or only 1 of 3+ squads broke away), shorten re-check cooldown
            // so newly respawned or disentangling squads can join the pivot instead of trickling 1 per 90s.
            if (pivotedNow == 0)
            {
                worstState.PivotCooldownUntil = now + 18.0f;
                _objectiveAttrition[worstKey] = worstState;
            }
            else if (pivotedNow == 1 && squadsOnStalled >= 3)
            {
                worstState.PivotCooldownUntil = now + 32.0f;
                _objectiveAttrition[worstKey] = worstState;
            }
        }

        private static bool CanSquadDisengageForAttritionPivot(
            Squad sq,
            Vector3 stalledPos,
            out bool shouldPopDisengageSmoke)
        {
            shouldPopDisengageSmoke = false;
            if (sq == null || sq.Pointer == IntPtr.Zero || sq.Leader == null || sq.Leader.transform == null)
                return false;
            if (sq.InChargeTime() || _squadOrderedToVehicle.ContainsKey(sq.Pointer))
                return false;

            float minSafePivotDist = sq.IsPlayerInSquad() ? 42.0f : 30.0f;
            if ((sq.Leader.transform.position - stalledPos).sqrMagnitude <= minSafePivotDist * minSafePivotDist)
                return false;

            float leaderSupp = sq.Leader.GetSuppressionValue();
            if (leaderSupp >= 48.0f)
                return false;

            bool closeThreat = false;
            if (_squadBlackboards.TryGetValue(sq.Pointer, out SquadBlackboard b) && b.HasActiveThreat)
            {
                float dSq = (b.ThreatPosition - b.SquadCenter).sqrMagnitude;
                if (dSq <= 28.0f * 28.0f)
                    return false;
                closeThreat = dSq <= 65.0f * 65.0f;
            }

            shouldPopDisengageSmoke = leaderSupp >= 14.0f || closeThreat;
            return true;
        }

        private static bool IsSquadTaskInObjectiveList(Squad sq, List<MissionObjective> objectives)
        {
            if (sq == null || sq.currentTask == null || sq.currentTask.Pointer == IntPtr.Zero || objectives == null)
                return false;

            try
            {
                if (sq.currentTask.GetTaskCompletationPercentage() >= 1.0f)
                    return false;
            }
            catch { }

            IntPtr curPtr = sq.currentTask.Pointer;
            for (int i = 0; i < objectives.Count; i++)
            {
                MissionObjective obj = objectives[i];
                if (obj != null && obj.missionTask != null && obj.missionTask.Pointer == curPtr)
                    return true;
            }
            return false;
        }

        private static bool IsSquadCommittedToActiveObjective(Squad sq, List<MissionObjective> validObjectives, float now)
        {
            if (!IsSquadTaskInObjectiveList(sq, validObjectives))
                return false;

            // When the Human Player is a subordinate member of an AI-led squad, keep the AI Squad Leader
            // committed to the current active objective until it is actually captured or lost!
            if (sq.IsPlayerInSquad())
                return true;

            if (_squadTaskCommittedUntil.TryGetValue(sq.Pointer, out float commitUntil) && now < commitUntil)
                return true;

            if (sq.Leader != null && sq.Leader.transform != null)
            {
                float distToTaskSq = (sq.Leader.transform.position - sq.currentTask.GetTaskDestination()).sqrMagnitude;
                if (distToTaskSq <= 85.0f * 85.0f)
                    return true;
            }

            if (_squadBlackboards.TryGetValue(sq.Pointer, out SquadBlackboard board) && board.HasActiveThreat)
                return true;

            return false;
        }

        private static bool IsSquadLockedInCloseFirefight(Squad sq)
        {
            if (sq == null || sq.Pointer == IntPtr.Zero)
                return false;

            Soldier leader = sq.Leader;
            if (leader != null && leader.IsAlive && leader.GetSuppressionValue() >= 18)
                return true;

            if (_squadBlackboards.TryGetValue(sq.Pointer, out SquadBlackboard board) && board.HasActiveThreat)
            {
                if ((board.ThreatPosition - board.SquadCenter).sqrMagnitude <= 55.0f * 55.0f)
                    return true;
            }

            return false;
        }

        private struct PendingMacroSquadOrder
        {
            public Squad SquadRef;
            public IntPtr SquadPtr;
            public IntPtr ExpectedTaskPtr;
            public float ExpireTime;
            public bool ReleaseStaleCovers;
            public bool IsGarrisonHold;
        }

        private static readonly List<PendingMacroSquadOrder> _pendingMacroSquadOrders = new List<PendingMacroSquadOrder>(16);
        private static int _lastMacroImmediateOrderFrame = -1;

        private static bool IsSquadProperlyHoldingGarrison(Squad sq, MissionObjective heldObj, IntPtr heldTaskPtr)
        {
            if (sq == null || heldObj == null || heldObj.missionTask == null)
                return true;

            if (sq.currentTask == null || sq.currentTask.Pointer != heldTaskPtr)
                return false;

            if (sq.HoldFire || sq.order != Order.defend)
                return false;

            return (sq.moveOrderPosition - heldObj.missionTask.GetTaskDestination()).sqrMagnitude <= 4.0f ||
                   heldObj.missionTask.IsInsideTaskArea(sq.moveOrderPosition, 0.95f);
        }

        private static void ExecuteMacroSquadTaskOrder(
            Squad sq,
            Soldier leader,
            SquadTask task,
            bool releaseStaleCovers,
            bool isGarrisonHold)
        {
            if (releaseStaleCovers)
            {
                AllowExplicitSquadUnstallRelease = true;
                try { sq.OrderLeaveAllVehiclesAndCovers(false, true, false); }
                catch { }
                finally { AllowExplicitSquadUnstallRelease = false; }
            }

            if (isGarrisonHold && task != null && task.Pointer != IntPtr.Zero)
            {
                // Native Squad.Leader_OrderAttackCurrentTask (0x180806F70) checks BattleData.IsDefenderFaction, which
                // returns FALSE for the invader-slot team on Dynamic Conquest even on their own held flags—rolling
                // 68% AttackFromSide wide arcs and 20-40% SetHoldFireOrder(true)! Issue explicit HoldArea + fire-at-will.
                IntPtr sqPtr = sq.Pointer;
                _aiCovertHoldFireSquads.Remove(sqPtr);
                _aiCovertHoldFireExpireTime.Remove(sqPtr);
                if (sq.HoldFire)
                {
                    try { sq.SetHoldFireOrder(false, false, false, Photon.Pun.PhotonNetwork.InRoom); } catch { }
                }

                Vector3 holdPos = task.GetTaskDestination();
                float holdRadius = Mathf.Max(10.0f, task.GetTaskAreaRadius());

                // Native Squad.HoldArea (0x180805E60) calls SendUnitsToCovers WITHOUT updating sq.formationDirection (+0x44)!
                // Orient formationDirection toward the active threat / enemy objective / enemy spawn BEFORE HoldArea runs.
                string fac = leader != null ? AiCore.GetCachedFaction(leader) : null;
                Vector3 garrisonDir = Vector3.zero;
                if (_squadBlackboards.TryGetValue(sqPtr, out SquadBlackboard gb))
                {
                    if (gb.HasActiveThreat)
                        garrisonDir = gb.ThreatDirection;
                    else if (gb.LastKnownThreatTime > 0f && (Time.time - gb.LastKnownThreatTime) <= 20.0f)
                        garrisonDir = gb.LastKnownThreatPos - holdPos;
                }
                garrisonDir.y = 0f;
                if (garrisonDir.sqrMagnitude < 0.01f && !string.IsNullOrEmpty(fac))
                {
                    if (VisionPatches.TryGetDominantEnemyGunfireDirection(holdPos, fac, 185.0f, 7.0f, out Vector3 gfDir, out _))
                        garrisonDir = gfDir;
                    else if (AiCore.TryGetNearestEnemyOrContestedObjectiveDirection(fac, holdPos, out Vector3 objDir))
                        garrisonDir = objDir;
                    else if (AiCore.TryGetEnemySpawnDirection(fac, holdPos, out Vector3 spDir))
                        garrisonDir = spDir;
                }
                garrisonDir.y = 0f;
                if (garrisonDir.sqrMagnitude > 0.01f)
                {
                    sq.formationDirection = garrisonDir.normalized;
                }

                sq.HoldArea(holdPos, holdRadius, false);
            }
            else
            {
                sq.Leader_OrderAttackCurrentTask(leader);
            }
        }

        private static void RemovePendingMacroSquadOrder(IntPtr sqPtr)
        {
            for (int i = _pendingMacroSquadOrders.Count - 1; i >= 0; i--)
            {
                if (_pendingMacroSquadOrders[i].SquadPtr == sqPtr)
                    _pendingMacroSquadOrders.RemoveAt(i);
            }
        }

        private static void EnqueuePendingMacroSquadOrder(
            Squad sq,
            IntPtr expectedTaskPtr,
            float now,
            bool releaseStaleCovers,
            bool isGarrisonHold)
        {
            if (sq == null || sq.Pointer == IntPtr.Zero || expectedTaskPtr == IntPtr.Zero)
                return;

            IntPtr sqPtr = sq.Pointer;
            PendingMacroSquadOrder entry = new PendingMacroSquadOrder
            {
                SquadRef = sq,
                SquadPtr = sqPtr,
                ExpectedTaskPtr = expectedTaskPtr,
                ExpireTime = now + 12.0f,
                ReleaseStaleCovers = releaseStaleCovers,
                IsGarrisonHold = isGarrisonHold
            };

            for (int i = 0; i < _pendingMacroSquadOrders.Count; i++)
            {
                if (_pendingMacroSquadOrders[i].SquadPtr == sqPtr)
                {
                    _pendingMacroSquadOrders[i] = entry;
                    return;
                }
            }

            if (_pendingMacroSquadOrders.Count < 24)
            {
                _pendingMacroSquadOrders.Add(entry);
            }
        }

        private static void DrainOnePendingMacroSquadOrderIfReady(float now)
        {
            if (_pendingMacroSquadOrders.Count == 0 ||
                Time.frameCount == _lastMacroImmediateOrderFrame ||
                AiCore.IsHeavyWorkFrame() ||
                Time.unscaledDeltaTime > 0.026f ||
                !AiCore.IsMasterClient())
            {
                return;
            }

            BattleManager bm = BattleManager.instance;
            if (bm == null || bm.Pointer == IntPtr.Zero || BattleManager.isBattleEnded || bm.isPreview)
            {
                _pendingMacroSquadOrders.Clear();
                return;
            }

            for (int i = 0; i < _pendingMacroSquadOrders.Count; )
            {
                PendingMacroSquadOrder p = _pendingMacroSquadOrders[i];
                Squad sq = p.SquadRef;
                if (sq == null ||
                    p.SquadPtr == IntPtr.Zero ||
                    sq.Pointer != p.SquadPtr ||
                    now >= p.ExpireTime)
                {
                    _pendingMacroSquadOrders.RemoveAt(i);
                    continue;
                }

                Soldier leader = sq.Leader;
                if (leader == null ||
                    leader.Pointer == IntPtr.Zero ||
                    !leader.IsAlive ||
                    leader.IsPlayer() ||
                    !AiCore.IsLocallyOwnedBot(leader) ||
                    sq.currentTask == null ||
                    sq.currentTask.Pointer != p.ExpectedTaskPtr ||
                    IsSquadActivelyRetreating(p.SquadPtr, now) ||
                    sq.InChargeTime() ||
                    _squadOrderedToVehicle.ContainsKey(p.SquadPtr))
                {
                    _pendingMacroSquadOrders.RemoveAt(i);
                    continue;
                }

                if (Photon.Pun.PhotonNetwork.InRoom && !AiCore.TryConsumeBotRpcBudget(0.45f))
                    return;

                _lastMacroImmediateOrderFrame = Time.frameCount;
                _pendingMacroSquadOrders.RemoveAt(i);
                try
                {
                    ExecuteMacroSquadTaskOrder(sq, leader, sq.currentTask, p.ReleaseStaleCovers, p.IsGarrisonHold);
                }
                catch { }
                return;
            }
        }

        private static void AssignAiSquadMacroTask(
            Squad sq,
            MissionObjective obj,
            float now,
            float commitDuration,
            bool releaseStaleCovers = false,
            bool isGarrisonHold = false)
        {
            if (sq == null || obj == null || obj.missionTask == null)
                return;

            IntPtr newTaskPtr = obj.missionTask.Pointer;
            bool changed = sq.currentTask == null || sq.currentTask.Pointer != newTaskPtr;
            bool needsGarrisonHold = isGarrisonHold && !IsSquadProperlyHoldingGarrison(sq, obj, newTaskPtr);
            SquadTask squadTask = obj.missionTask.Cast<SquadTask>();
            sq.currentTask = squadTask;
            _squadTaskCommittedUntil[sq.Pointer] = now + (sq.IsPlayerInSquad() ? Mathf.Max(commitDuration, 65.0f) : commitDuration);

            if ((changed || needsGarrisonHold) && sq.timed_order_end > now && !sq.InChargeTime() && !IsSquadActivelyRetreating(sq.Pointer, now))
            {
                sq.timed_order_end = 0f;
            }

            // Cap immediate task orders (which run CoverManager.GetCovers + reliable PhotonView.RPCs)
            // to at most 1 squad per frame; any additional reassigned squads are queued into _pendingMacroSquadOrders
            // so every pivoted/garrisoned squad executes on the next non-heavy frames!
            int frame = Time.frameCount;
            if ((changed || needsGarrisonHold) &&
                sq.Leader != null &&
                sq.Leader.IsAlive &&
                !sq.Leader.IsPlayer() &&
                AiCore.IsLocallyOwnedBot(sq.Leader) &&
                !IsSquadActivelyRetreating(sq.Pointer, now) &&
                !sq.InChargeTime())
            {
                if (frame != _lastMacroImmediateOrderFrame &&
                    !AiCore.IsHeavyWorkFrame() &&
                    (!Photon.Pun.PhotonNetwork.InRoom || AiCore.TryConsumeBotRpcBudget(0.45f)))
                {
                    _lastMacroImmediateOrderFrame = frame;
                    RemovePendingMacroSquadOrder(sq.Pointer);
                    try
                    {
                        ExecuteMacroSquadTaskOrder(sq, sq.Leader, squadTask, releaseStaleCovers, isGarrisonHold);
                    }
                    catch { }
                }
                else
                {
                    EnqueuePendingMacroSquadOrder(sq, newTaskPtr, now, releaseStaleCovers, isGarrisonHold);
                }
            }

            if (changed && sq.IsPlayerInSquad())
            {
                RecordNotifiedPlayerSquadTask(squadTask);
                NotifyPlayerSquadTaskUpdated(sq);
                if (IsSquadConnectedToRadioNet(sq))
                {
                    Vector3? listenerPos = sq.Leader != null && sq.Leader.transform != null
                        ? sq.Leader.transform.position
                        : (Vector3?)null;
                    string objName = AiCore.GetVanillaObjectiveDisplayName(obj, listenerPos);
                    string distBearing = listenerPos.HasValue
                        ? $" \u2014 {AiCore.FormatRelativeBearingAndDistance(listenerPos.Value, obj.GetTaskPosition())}"
                        : "";
                    AiCore.TryShowLocalPlayerRadioHint(
                        sq,
                        $"Squad Radio: New squad objective \u2014 {objName}{distBearing}",
                        requireLeader: false,
                        minInterval: 10.0f);
                }
            }
        }

        internal enum PendingHqOrderKind
        {
            None = 0,
            ObjectiveTask = 1,
            TacticalFlank = 2
        }

        internal struct PlayerPendingHqOrderState
        {
            public bool IsActive;
            public PendingHqOrderKind Kind;
            public IntPtr SquadPtr;
            public IntPtr TargetTaskPtr;
            public MissionObjective TargetObj;
            public SquadTask TargetTask;
            public Vector3 WorldPos;
            public Vector3 FlankDirection;
            public bool IsAttackObjective;
            public string ShortActionLabel;
            public string MapBadgeText;
            public float CreatedTime;
            public float ExpiresTime;
        }

        private static PlayerPendingHqOrderState _pendingPlayerHqOrder;
        private static readonly Dictionary<IntPtr, float> _playerBalkedTaskUntil = new Dictionary<IntPtr, float>(8);
        private static float _playerBalkedFlankUntil;
        private static Vector3 _playerBalkedFlankPos;

        internal static void RequestImmediateMacroReevaluation(bool preferTeamNet = false)
        {
            _nextMacroDirectorTime = 0f;
            _macroDirectorSubPhase = preferTeamNet ? 2 : 0;
        }

        internal static bool IsTaskBalkedByPlayer(IntPtr taskPtr, float now)
        {
            if (taskPtr == IntPtr.Zero)
                return false;

            if (_playerBalkedTaskUntil.TryGetValue(taskPtr, out float until))
            {
                if (now < until)
                    return true;
                _playerBalkedTaskUntil.Remove(taskPtr);
            }
            return false;
        }

        internal static bool IsFlankBalkedByPlayer(Vector3 threatPos, float now)
        {
            if (now >= _playerBalkedFlankUntil)
                return false;

            Vector3 delta = threatPos - _playerBalkedFlankPos;
            delta.y = 0f;
            return delta.sqrMagnitude <= 45.0f * 45.0f;
        }

        internal static bool TryGetActivePendingPlayerHqOrder(out PlayerPendingHqOrderState state)
        {
            state = default;
            if (!_pendingPlayerHqOrder.IsActive)
                return false;

            float now = Time.time;
            if (now >= _pendingPlayerHqOrder.ExpiresTime)
            {
                _pendingPlayerHqOrder = default;
                return false;
            }

            Soldier localPlayer = Soldier.CurrentControlledSoldierOrNull();
            if (localPlayer == null || !localPlayer.IsAlive)
            {
                _pendingPlayerHqOrder = default;
                return false;
            }

            Squad sq = localPlayer.joinedSquad;
            if (sq == null || sq.Pointer != _pendingPlayerHqOrder.SquadPtr ||
                sq.Leader == null || sq.Leader.Pointer != localPlayer.Pointer)
            {
                _pendingPlayerHqOrder = default;
                return false;
            }

            if (_pendingPlayerHqOrder.Kind == PendingHqOrderKind.ObjectiveTask)
            {
                MissionObjective obj = _pendingPlayerHqOrder.TargetObj;
                SquadTask targetTask = _pendingPlayerHqOrder.TargetTask;
                if (obj == null || obj.Pointer == IntPtr.Zero ||
                    targetTask == null || targetTask.Pointer == IntPtr.Zero ||
                    targetTask.Pointer != _pendingPlayerHqOrder.TargetTaskPtr)
                {
                    _pendingPlayerHqOrder = default;
                    return false;
                }

                // Clear automatically if player already switched to this task manually.
                if (sq.currentTask != null && sq.currentTask.Pointer == _pendingPlayerHqOrder.TargetTaskPtr)
                {
                    _pendingPlayerHqOrder = default;
                    return false;
                }

                BattleData bd = BattleManager.instance != null ? BattleManager.instance.battleData : null;
                if (!AiCore.ClassifyMissionObjectiveForFaction(obj, localPlayer.faction, bd, out bool isHeld, out bool canAttract, out _))
                {
                    _pendingPlayerHqOrder = default;
                    return false;
                }

                if (_pendingPlayerHqOrder.IsAttackObjective && (isHeld || !canAttract))
                {
                    _pendingPlayerHqOrder = default;
                    return false;
                }

                if (!_pendingPlayerHqOrder.IsAttackObjective && !isHeld)
                {
                    _pendingPlayerHqOrder = default;
                    return false;
                }
            }

            state = _pendingPlayerHqOrder;
            return true;
        }

        internal static void PostPendingPlayerHqOrder(
            Squad playerSq,
            PendingHqOrderKind kind,
            MissionObjective targetObj,
            SquadTask targetTask,
            Vector3 worldPos,
            Vector3 flankDir,
            bool isAttackObjective,
            string shortActionLabel,
            string toastLabel,
            string mapBadgeText,
            float durationSeconds)
        {
            if (playerSq == null || playerSq.Pointer == IntPtr.Zero ||
                !AiCore.IsLocalPlayerInSquad(playerSq, out bool isLocalLeader) ||
                !isLocalLeader)
            {
                return;
            }

            float now = Time.time;
            IntPtr taskPtr = targetTask != null ? targetTask.Pointer : IntPtr.Zero;

            if (TryGetActivePendingPlayerHqOrder(out PlayerPendingHqOrderState existing) &&
                existing.Kind == kind &&
                existing.SquadPtr == playerSq.Pointer)
            {
                bool sameTarget = kind == PendingHqOrderKind.ObjectiveTask
                    ? (existing.TargetTaskPtr == taskPtr && existing.IsAttackObjective == isAttackObjective)
                    : ((existing.WorldPos - worldPos).sqrMagnitude <= 22.0f * 22.0f);

                if (sameTarget)
                {
                    _pendingPlayerHqOrder.WorldPos = worldPos;
                    _pendingPlayerHqOrder.FlankDirection = flankDir;
                    _pendingPlayerHqOrder.ShortActionLabel = shortActionLabel;
                    _pendingPlayerHqOrder.ExpiresTime = Mathf.Max(_pendingPlayerHqOrder.ExpiresTime, now + durationSeconds);
                    return;
                }
            }

            _pendingPlayerHqOrder = new PlayerPendingHqOrderState
            {
                IsActive = true,
                Kind = kind,
                SquadPtr = playerSq.Pointer,
                TargetTaskPtr = taskPtr,
                TargetObj = targetObj,
                TargetTask = targetTask,
                WorldPos = worldPos,
                FlankDirection = flankDir,
                IsAttackObjective = isAttackObjective,
                ShortActionLabel = shortActionLabel,
                MapBadgeText = mapBadgeText,
                CreatedTime = now,
                ExpiresTime = now + durationSeconds
            };

            string hintSubject = string.IsNullOrEmpty(toastLabel) ? shortActionLabel : toastLabel;
            AiCore.TryShowLocalPlayerRadioHint(
                playerSq,
                $"HQ Radio: Order available \u2014 {hintSubject} [Radial]",
                requireLeader: true,
                minInterval: 8.0f,
                notifyRemoteVanillaSquadmates: false);

            // Play Radioman / NCO radio voice cue when available.
            if (playerSq.units != null)
            {
                var units = playerSq.units;
                int count = units.Count;
                for (int i = 0; i < count; i++)
                {
                    Soldier u = units[i];
                    if (u != null && u.IsAlive && !u.IsPlayer() && u.IsRadioman())
                    {
                        TrySayTacticalVoice(u, playerSq, VoiceManager.VoiceClip.attackThere, 0.25f);
                        break;
                    }
                }
            }

            if (VisionConfig.DebugLogging.Value)
            {
                AiCore.LogDebug(
                    "MacroDirector",
                    $"Posted pending HQ order ({kind}: '{shortActionLabel}') for Player Squad Leader ('{playerSq.squadName}').");
            }
        }

        internal static void AcceptPendingPlayerHqOrder()
        {
            if (!TryGetActivePendingPlayerHqOrder(out PlayerPendingHqOrderState state))
                return;

            Soldier localPlayer = Soldier.CurrentControlledSoldierOrNull();
            Squad playerSq = localPlayer != null ? localPlayer.joinedSquad : null;
            if (localPlayer == null || playerSq == null || playerSq.Pointer != state.SquadPtr)
                return;

            _pendingPlayerHqOrder = default;

            try
            {
                if (state.Kind == PendingHqOrderKind.ObjectiveTask &&
                    state.TargetTask != null &&
                    state.TargetTask.Pointer != IntPtr.Zero)
                {
                    playerSq.currentTask = state.TargetTask;
                    RecordNotifiedPlayerSquadTask(state.TargetTask);
                    ConsumeInitialHqSpawnBriefingIfNoRadio(playerSq);
                    NotifyPlayerSquadTaskUpdated(playerSq);
                    localPlayer.Say(VoiceManager.VoiceClip.yesSir, 0.05f);
                }
                else if (state.Kind == PendingHqOrderKind.TacticalFlank)
                {
                    Vector3 flankDir = state.FlankDirection.sqrMagnitude > 0.01f
                        ? state.FlankDirection.normalized
                        : Vector3.forward;
                    playerSq.AttackFromSide(flankDir, state.WorldPos, 24.0f);
                    localPlayer.Say(VoiceManager.VoiceClip.attackThere, 0.05f);
                }

                PlayerController.HintToPlayer($"HQ Radio: Copy — {state.ShortActionLabel}", false);
            }
            catch { }
        }

        internal static void BalkPendingPlayerHqOrder()
        {
            if (!TryGetActivePendingPlayerHqOrder(out PlayerPendingHqOrderState state))
                return;

            float now = Time.time;
            if (state.Kind == PendingHqOrderKind.ObjectiveTask && state.TargetTaskPtr != IntPtr.Zero)
            {
                _playerBalkedTaskUntil[state.TargetTaskPtr] = now + 75.0f;
            }
            else if (state.Kind == PendingHqOrderKind.TacticalFlank)
            {
                _playerBalkedFlankUntil = now + 75.0f;
                _playerBalkedFlankPos = state.WorldPos;
            }

            _pendingPlayerHqOrder = default;
            RequestImmediateMacroReevaluation(preferTeamNet: state.Kind == PendingHqOrderKind.TacticalFlank);

            try
            {
                PlayerController.HintToPlayer("HQ Radio: Balked HQ order — holding current initiative (reassigning net)", false);
            }
            catch { }
        }

        private static MissionObjective FindObjectiveWithTaskPtr(List<MissionObjective> objectives, IntPtr taskPtr)
        {
            if (objectives == null || taskPtr == IntPtr.Zero)
                return null;

            for (int i = 0; i < objectives.Count; i++)
            {
                MissionObjective cand = objectives[i];
                if (cand != null && cand.missionTask != null && cand.missionTask.Pointer == taskPtr)
                    return cand;
            }
            return null;
        }

        private static readonly List<MissionObjective> _reusablePlayerCandidateObjs = new List<MissionObjective>(12);

        private static void TrySuggestObjectiveForPlayerSquadLeader(
            Squad playerSq,
            string faction,
            List<MissionObjective> heldObjectives,
            List<MissionObjective> unheldObjectives,
            List<Squad> facAiSquads,
            List<KeyValuePair<string, Vector3>> allEnemyLeaderPositions)
        {
            if (playerSq == null || playerSq.Leader == null || playerSq.Leader.transform == null)
                return;

            if (!IsSquadConnectedToRadioNet(playerSq))
                return;

            bool isLocalSl = AiCore.IsLocalPlayerInSquad(playerSq, out bool isLocalLeader) && isLocalLeader;
            Photon.Realtime.Player remoteVanillaSl = null;
            if (!isLocalSl)
            {
                if (!AiCore.IsInMultiplayerRoom() || !AiCore.IsMasterClient() || !playerSq.Leader.IsPlayer())
                    return;

                SyncSoldier syn = playerSq.Leader.soldierSyncher;
                if (syn == null || syn.OwnSyncher)
                    return;

                Photon.Realtime.Player owner = syn.Controller ?? syn.Owner;
                if (owner == null || owner.IsLocal || AiCore.IsPhotonPlayerConfirmedModded(owner))
                    return;

                remoteVanillaSl = owner;
            }

            // Re-validate any currently held pending order first so stale/completed entries clear automatically.
            if (isLocalSl)
                TryGetActivePendingPlayerHqOrder(out _);

            float now = Time.time;
            Vector3 leaderPos = playerSq.Leader.transform.position;
            IntPtr curTaskPtr = playerSq.currentTask != null ? playerSq.currentTask.Pointer : IntPtr.Zero;

            bool hasActiveObjective = IsSquadTaskInObjectiveList(playerSq, unheldObjectives) ||
                                      IsSquadTaskInObjectiveList(playerSq, heldObjectives);

            if (hasActiveObjective)
            {
                ConsumeInitialHqSpawnBriefingIfNoRadio(playerSq);

                // 1. Check for Urgent Undefended Defense: nearby held objective under active threat with no defenders.
                if (heldObjectives != null && heldObjectives.Count > 0)
                {
                    MissionObjective urgentDefend = null;
                    float bestScore = 155f * 155f;
                    for (int i = 0; i < heldObjectives.Count; i++)
                    {
                        MissionObjective cand = heldObjectives[i];
                        if (cand == null || cand.missionTask == null || cand.missionTask.Pointer == IntPtr.Zero)
                            continue;
                        if (cand.missionTask.Pointer == curTaskPtr || (isLocalSl && IsTaskBalkedByPlayer(cand.missionTask.Pointer, now)))
                            continue;
                        if ((cand.GetTaskPosition() - leaderPos).sqrMagnitude > 260f * 260f)
                            continue;
                        if (CountAlliedGroundSquadsOnObjective(faction, cand.missionTask.Pointer, cand.GetTaskPosition(), true) > 0)
                            continue;

                        float score = GetObjectiveThreatPriorityScore(cand, faction, allEnemyLeaderPositions);
                        if (score <= bestScore)
                        {
                            bestScore = score;
                            urgentDefend = cand;
                        }
                    }

                    if (urgentDefend != null)
                    {
                        string fullLabel = AiCore.GetVanillaObjectiveDisplayName(urgentDefend, leaderPos, out string shortLetter);
                        if (remoteVanillaSl != null)
                        {
                            string bearing = AiCore.FormatRelativeBearingAndDistance(leaderPos, urgentDefend.GetTaskPosition());
                            AiCore.SendTargetedVanillaChatMessage(
                                remoteVanillaSl,
                                $"HQ Radio ({AiCore.FormatSquadPhoneticCallsign(playerSq)}): Urgent \u2014 {fullLabel} ({bearing}) needs defense!",
                                minPlayerInterval: 45.0f);
                            return;
                        }

                        string shortPoint = !string.IsNullOrEmpty(shortLetter) ? shortLetter : fullLabel;
                        PostPendingPlayerHqOrder(
                            playerSq,
                            PendingHqOrderKind.ObjectiveTask,
                            urgentDefend,
                            urgentDefend.missionTask.Cast<SquadTask>(),
                            urgentDefend.GetTaskPosition(),
                            Vector3.zero,
                            isAttackObjective: false,
                            shortActionLabel: $"Defend {shortPoint}",
                            toastLabel: $"Urgent: Defend {fullLabel}",
                            mapBadgeText: "[HQ]",
                            durationSeconds: 55.0f);
                        return;
                    }
                }

                // 2. Check for HQ Attrition Pivot if player squad is grinding on a heavily stalled meatgrinder objective.
                if (curTaskPtr != IntPtr.Zero && unheldObjectives != null && unheldObjectives.Count >= 2)
                {
                    MissionObjective currentAttackObj = FindObjectiveWithTaskPtr(unheldObjectives, curTaskPtr);
                    if (currentAttackObj != null)
                    {
                        var key = new ObjectiveAttritionKey { Faction = faction, TaskPtr = curTaskPtr };
                        if (_objectiveAttrition.TryGetValue(key, out ObjectiveAttritionState attState))
                        {
                            float threshold = GetFactionCommandProfile(faction).AttritionPivotThreshold;
                            if ((now < attState.AvoidUntil || attState.DecayedLosses >= threshold) &&
                                CanSquadDisengageForAttritionPivot(playerSq, currentAttackObj.GetTaskPosition(), out _))
                            {
                                var candidates = _reusablePlayerCandidateObjs;
                                candidates.Clear();
                                for (int i = 0; i < unheldObjectives.Count; i++)
                                {
                                    MissionObjective cand = unheldObjectives[i];
                                    if (cand == null || cand.missionTask == null || cand.missionTask.Pointer == IntPtr.Zero)
                                        continue;
                                    if (cand.missionTask.Pointer == curTaskPtr || (isLocalSl && IsTaskBalkedByPlayer(cand.missionTask.Pointer, now)))
                                        continue;
                                    candidates.Add(cand);
                                }

                                if (candidates.Count > 0)
                                {
                                    MissionObjective pivotObj = PickBestAttackObjectiveForSquad(leaderPos, faction, candidates, facAiSquads) ?? candidates[0];
                                    string fullLabel = AiCore.GetVanillaObjectiveDisplayName(pivotObj, leaderPos, out string shortLetter);
                                    if (remoteVanillaSl != null)
                                    {
                                        string bearing = AiCore.FormatRelativeBearingAndDistance(leaderPos, pivotObj.GetTaskPosition());
                                        AiCore.SendTargetedVanillaChatMessage(
                                            remoteVanillaSl,
                                            $"HQ Radio ({AiCore.FormatSquadPhoneticCallsign(playerSq)}): Heavy losses here \u2014 consider pivoting to {fullLabel} ({bearing})",
                                            minPlayerInterval: 45.0f);
                                    }
                                    else
                                    {
                                        string shortPoint = !string.IsNullOrEmpty(shortLetter) ? shortLetter : fullLabel;
                                        PostPendingPlayerHqOrder(
                                            playerSq,
                                            PendingHqOrderKind.ObjectiveTask,
                                            pivotObj,
                                            pivotObj.missionTask.Cast<SquadTask>(),
                                            pivotObj.GetTaskPosition(),
                                            Vector3.zero,
                                            isAttackObjective: true,
                                            shortActionLabel: $"Pivot {shortPoint}",
                                            toastLabel: $"Pivot \u2014 Attack {fullLabel}",
                                            mapBadgeText: "[HQ]",
                                            durationSeconds: 60.0f);
                                    }
                                }
                                candidates.Clear();
                            }
                        }
                    }
                }

                return;
            }

            // 3. Player Squad Leader currently has NO valid phase objective: propose best non-balked objective via HQ Radio (never overwrite currentTask).
            MissionObjective suggestedObj = null;
            bool isAttackObj = false;

            if (unheldObjectives != null && unheldObjectives.Count > 0)
            {
                var candidates = _reusablePlayerCandidateObjs;
                candidates.Clear();
                for (int i = 0; i < unheldObjectives.Count; i++)
                {
                    MissionObjective cand = unheldObjectives[i];
                    if (cand != null && cand.missionTask != null && cand.missionTask.Pointer != IntPtr.Zero &&
                        (!isLocalSl || !IsTaskBalkedByPlayer(cand.missionTask.Pointer, now)))
                    {
                        candidates.Add(cand);
                    }
                }

                if (candidates.Count > 0)
                {
                    suggestedObj = PickBestAttackObjectiveForSquad(leaderPos, faction, candidates, facAiSquads);
                    isAttackObj = true;
                }
                candidates.Clear();
            }
            else if (heldObjectives != null && heldObjectives.Count > 0)
            {
                var candidates = _reusablePlayerCandidateObjs;
                candidates.Clear();
                for (int i = 0; i < heldObjectives.Count; i++)
                {
                    MissionObjective cand = heldObjectives[i];
                    if (cand != null && cand.missionTask != null && cand.missionTask.Pointer != IntPtr.Zero &&
                        (!isLocalSl || !IsTaskBalkedByPlayer(cand.missionTask.Pointer, now)))
                    {
                        candidates.Add(cand);
                    }
                }

                if (candidates.Count > 0)
                {
                    MissionObjective bestThreatened = null;
                    float bestThreatScore = 165f * 165f;
                    for (int i = 0; i < candidates.Count; i++)
                    {
                        MissionObjective cand = candidates[i];
                        float score = GetObjectiveThreatPriorityScore(cand, faction, allEnemyLeaderPositions);
                        if (score <= bestThreatScore)
                        {
                            bestThreatScore = score;
                            bestThreatened = cand;
                        }
                    }
                    suggestedObj = bestThreatened ?? GetClosestObjectiveToPosition(leaderPos, faction, candidates);
                }
                candidates.Clear();
            }

            if (suggestedObj == null || suggestedObj.missionTask == null)
                return;

            ConsumeInitialHqSpawnBriefingIfNoRadio(playerSq);

            string objFull = AiCore.GetVanillaObjectiveDisplayName(suggestedObj, leaderPos, out string objShortLetter);
            string verb = isAttackObj ? "Attack" : "Defend";
            if (remoteVanillaSl != null)
            {
                string bearing = AiCore.FormatRelativeBearingAndDistance(leaderPos, suggestedObj.GetTaskPosition());
                AiCore.SendTargetedVanillaChatMessage(
                    remoteVanillaSl,
                    $"HQ Radio ({AiCore.FormatSquadPhoneticCallsign(playerSq)}): Suggested objective \u2014 {verb} {objFull} ({bearing})",
                    minPlayerInterval: 45.0f);
                return;
            }

            string objShort = !string.IsNullOrEmpty(objShortLetter) ? objShortLetter : objFull;
            PostPendingPlayerHqOrder(
                playerSq,
                PendingHqOrderKind.ObjectiveTask,
                suggestedObj,
                suggestedObj.missionTask.Cast<SquadTask>(),
                suggestedObj.GetTaskPosition(),
                Vector3.zero,
                isAttackObjective: isAttackObj,
                shortActionLabel: $"{verb} {objShort}",
                toastLabel: $"{verb} {objFull}",
                mapBadgeText: "[HQ]",
                durationSeconds: 65.0f);
        }

        private static IntPtr _lastNotifiedPlayerSquadTaskPtr;
        private static float _lastNotifiedPlayerSquadTaskTime;

        internal static void RecordNotifiedPlayerSquadTask(SquadTask task)
        {
            if (task != null && task.Pointer != IntPtr.Zero)
            {
                _lastNotifiedPlayerSquadTaskPtr = task.Pointer;
                _lastNotifiedPlayerSquadTaskTime = Time.unscaledTime;
            }
        }

        private static void NotifyPlayerSquadTaskUpdated(Squad sq)
        {
            if (sq == null)
                return;

            try
            {
                // Intentionally do NOT start sq.UpdateSquadTaskUI() (which waits 1.0s and triggers vanilla's
                // generic "New squad objective" banner on top of our rich contextual radio toast!).
                PlayerGUI.UpdateSquadGUI();
            }
            catch { }
        }

        private static MissionObjective FindActiveMissionObjectiveForTaskPtr(IntPtr taskPtr)
        {
            if (taskPtr == IntPtr.Zero)
                return null;

            try
            {
                BattlePhase phase = AiCore.TryGetCurrentBattlePhase(BattleManager.instance);
                MissionObjective[] phaseObjs = phase != null ? phase.objectives : null;
                if (phaseObjs != null)
                {
                    for (int i = 0; i < phaseObjs.Length; i++)
                    {
                        MissionObjective cand = phaseObjs[i];
                        if (cand != null && cand.missionTask != null && cand.missionTask.Pointer == taskPtr)
                            return cand;
                    }
                }

                var scriptedList = MissionObjectiveScripted.scriptedMissionObjectives;
                if (scriptedList != null)
                {
                    int sCount = scriptedList.Count;
                    for (int i = 0; i < sCount; i++)
                    {
                        MissionObjectiveScripted cand = scriptedList[i];
                        if (cand != null && cand.missionTask != null && cand.missionTask.Pointer == taskPtr)
                            return cand;
                    }
                }
            }
            catch { }
            return null;
        }

        [HarmonyPatch(typeof(PlayerController), nameof(PlayerController.OnSquadTaskUpdated))]
        public static class PlayerController_OnSquadTaskUpdated_Patch
        {
            public static bool Prefix()
            {
                if (!VisionConfig.EnableMod.Value)
                    return true;

                try
                {
                    Soldier localPlayer = Soldier.CurrentControlledSoldierOrNull();
                    Squad sq = localPlayer != null ? localPlayer.joinedSquad : null;
                    SquadTask curTask = sq != null ? sq.currentTask : null;
                    if (localPlayer == null || !localPlayer.IsAlive || curTask == null || curTask.Pointer == IntPtr.Zero)
                        return false;

                    float now = Time.unscaledTime;
                    if (curTask.Pointer == _lastNotifiedPlayerSquadTaskPtr && (now - _lastNotifiedPlayerSquadTaskTime) < 8.0f)
                        return false;

                    _lastNotifiedPlayerSquadTaskPtr = curTask.Pointer;
                    _lastNotifiedPlayerSquadTaskTime = now;

                    Vector3? pPos = localPlayer.transform != null ? localPlayer.transform.position : (Vector3?)null;
                    MissionObjective matchedObj = FindActiveMissionObjectiveForTaskPtr(curTask.Pointer);
                    if (matchedObj != null)
                    {
                        BattleData curBd = BattleManager.GetCurrentBattleData();
                        AiCore.ClassifyMissionObjectiveForFaction(matchedObj, localPlayer.faction, curBd, out bool isHeld, out _, out _);
                        string verb = isHeld ? "Defend" : "Attack";
                        string objLabel = AiCore.GetVanillaObjectiveDisplayName(matchedObj, pPos);
                        string distBearing = pPos.HasValue
                            ? $" \u2014 {AiCore.FormatRelativeBearingAndDistance(pPos.Value, matchedObj.GetTaskPosition())}"
                            : "";
                        PlayerController.HintToPlayer($"Squad Radio: {verb} {objLabel}{distBearing}", false);
                    }
                    else
                    {
                        string desc = null;
                        try { desc = curTask.GetTaskString(); } catch { }
                        string distBearing = "";
                        if (pPos.HasValue)
                        {
                            try { distBearing = $" \u2014 {AiCore.FormatRelativeBearingAndDistance(pPos.Value, curTask.GetTaskDestination())}"; }
                            catch { }
                        }
                        string cleanDesc = string.IsNullOrWhiteSpace(desc) ? "Updated objective" : desc.Trim();
                        PlayerController.HintToPlayer($"Squad Order: {cleanDesc}{distBearing}", false);
                    }
                }
                catch { }

                return false;
            }
        }

        private static int CountAlliedGroundSquadsOnObjective(
            string faction,
            IntPtr taskPtr,
            Vector3 objPos,
            bool includeNearbyPlayersAndArmor = true)
        {
            if (taskPtr == IntPtr.Zero || string.IsNullOrEmpty(faction))
                return 0;

            AiCore.EnsureWorldSnapshot();
            int sqCount = AiCore.WorldSquadCount;
            var worldSquads = AiCore.WorldSquads;
            int count = 0;
            float now = Time.time;

            for (int i = 0; i < sqCount; i++)
            {
                ref AiCore.SquadSnapshot snap = ref worldSquads[i];
                if (snap.Ptr == IntPtr.Zero || snap.UnitCount <= 0 || snap.RefOnPlane || string.IsNullOrEmpty(snap.Faction))
                    continue;
                if (!AiCore.AreSameSideFactions(faction, snap.Faction))
                    continue;

                Squad sq = snap.Ref;
                if (sq != null && sq.currentTask != null && sq.currentTask.Pointer == taskPtr)
                {
                    count++;
                    continue;
                }

                if (includeNearbyPlayersAndArmor &&
                    (snap.IsPlayerInSquad || snap.LeaderOnGroundVehicle) &&
                    (snap.RefPos - objPos).sqrMagnitude <= 85.0f * 85.0f)
                {
                    if (snap.IsPlayerInSquad &&
                        (IsTaskBalkedByPlayer(taskPtr, now) ||
                         (_pendingPlayerHqOrder.IsActive && _pendingPlayerHqOrder.SquadPtr == snap.Ptr && _pendingPlayerHqOrder.TargetTaskPtr == taskPtr)))
                    {
                        continue;
                    }
                    count++;
                }
            }

            return count;
        }

        private static MissionObjective PickBestAttackObjectiveForSquad(
            Vector3 leaderPos,
            string faction,
            List<MissionObjective> unheldObjectives,
            List<Squad> eligibleSquads)
        {
            if (unheldObjectives == null || unheldObjectives.Count == 0)
                return null;
            if (unheldObjectives.Count == 1)
                return unheldObjectives[0];

            BattleData bd = _currentDirectorBattleData ?? BattleManager.GetCurrentBattleData();
            MissionObjective bestObj = null;
            float bestScore = float.MaxValue;

            for (int i = 0; i < unheldObjectives.Count; i++)
            {
                MissionObjective obj = unheldObjectives[i];
                if (obj == null || obj.missionTask == null)
                    continue;

                IntPtr taskPtr = obj.missionTask.Pointer;
                Vector3 objPos = obj.GetTaskPosition();
                int assignedAttackers = CountAlliedGroundSquadsOnObjective(faction, taskPtr, objPos, true);

                float dist = Vector3.Distance(leaderPos, objPos) + 25.0f;
                float weight = 1.0f;

                // Ensure every open attack objective gets at least 1 pressuring squad instead of leaving a flank flag empty
                if (assignedAttackers == 0)
                    weight *= 0.52f;
                else if (assignedAttackers >= 3)
                    weight *= (1.0f + (assignedAttackers - 2) * 0.22f);

                // Reinforce flags where friendly troops are already ticking capture progress so caps finish decisively!
                bool classified = AiCore.ClassifyMissionObjectiveForFaction(obj, faction, bd, out bool isHeld, out _, out float urgency);
                if (classified && !isHeld && urgency > 0.05f)
                {
                    weight *= Mathf.Lerp(0.92f, 0.62f, urgency);
                }

                // Team-Net Attrition Avoidance: steer fresh spawns and pivoting squads away from stalled meatgrinder objectives
                // (< 25% capture progress) toward softer/unstaffed objectives!
                if ((!classified || urgency < 0.25f) &&
                    _objectiveAttrition.TryGetValue(new ObjectiveAttritionKey { Faction = faction, TaskPtr = taskPtr }, out ObjectiveAttritionState attrState))
                {
                    if (attrState.DecayedLosses >= 2.0f)
                        weight *= (1.0f + Mathf.Min(attrState.DecayedLosses, 16.0f) * 0.16f);
                    if (Time.time < attrState.AvoidUntil)
                        weight *= 2.85f;
                }

                float score = dist * weight;
                if (score < bestScore)
                {
                    bestScore = score;
                    bestObj = obj;
                }
            }

            return bestObj ?? GetClosestObjectiveToPosition(leaderPos, faction, unheldObjectives);
        }

        private static Squad PickBestReserveDefenderSquad(
            List<Squad> eligibleSquads,
            List<MissionObjective> heldObjectives,
            IntPtr targetTaskPtr,
            Vector3 targetPos,
            float now,
            bool urgentUndefendedRescue = false,
            string faction = null,
            List<KeyValuePair<string, Vector3>> allEnemyLeaderPositions = null)
        {
            Squad bestSquad = null;
            float bestScore = float.MaxValue;

            for (int sIdx = 0; sIdx < eligibleSquads.Count; sIdx++)
            {
                Squad cand = eligibleSquads[sIdx];
                if (cand.currentTask != null && cand.currentTask.Pointer == targetTaskPtr)
                    continue;

                if (cand.IsPlayerInSquad())
                    continue;

                if (!urgentUndefendedRescue)
                {
                    // Respect task commitment hysteresis so reserve squads don't get ping-ponged mid-march
                    if (IsSquadCommittedToActiveObjective(cand, heldObjectives, now))
                        continue;
                }
                else
                {
                    // Urgent rescue of an UNDEFENDED bleeding/threatened flag: allow unengaged squads sitting on
                    // a quiet secondary flag to break their initial spawn hold, while keeping squads in active combat
                    // or defending another threatened flag locked in place!
                    if (_squadBlackboards.TryGetValue(cand.Pointer, out SquadBlackboard board) && board.HasActiveThreat)
                        continue;

                    if (cand.currentTask != null && !string.IsNullOrEmpty(faction) && allEnemyLeaderPositions != null)
                    {
                        IntPtr curPtr = cand.currentTask.Pointer;
                        bool curIsAlsoThreatened = false;
                        for (int h = 0; h < heldObjectives.Count; h++)
                        {
                            MissionObjective hObj = heldObjectives[h];
                            if (hObj != null && hObj.missionTask != null && hObj.missionTask.Pointer == curPtr)
                            {
                                if (GetObjectiveThreatPriorityScore(hObj, faction, allEnemyLeaderPositions) <= 165f * 165f)
                                    curIsAlsoThreatened = true;
                                break;
                            }
                        }
                        if (curIsAlsoThreatened)
                            continue;
                    }
                }

                int sameTaskCount = 0;
                if (cand.currentTask != null)
                {
                    IntPtr curPtr = cand.currentTask.Pointer;
                    string countFac = !string.IsNullOrEmpty(faction) ? faction : cand.Leader.faction;
                    sameTaskCount = CountAlliedGroundSquadsOnObjective(countFac, curPtr, cand.currentTask.GetTaskDestination(), false);
                }

                float dSq = (cand.Leader.transform.position - targetPos).sqrMagnitude;
                // Distant (> 65m) reserve squads can only be re-tasked across the map if connected to the Radio Net
                if (dSq > 65f * 65f && !IsSquadConnectedToRadioNet(cand))
                    continue;

                // Strongly prefer pulling a squad from an objective that already has 2+ squads (or an unassigned squad)
                // so we don't strip the last defender from another objective unless necessary.
                if (cand.currentTask != null && sameTaskCount <= 1)
                {
                    dSq *= 4.0f;
                }

                if (dSq < bestScore)
                {
                    bestScore = dSq;
                    bestSquad = cand;
                }
            }

            return bestSquad;
        }

        private static float GetObjectiveThreatPriorityScore(
            MissionObjective obj,
            string myFaction,
            List<KeyValuePair<string, Vector3>> leaderPositions)
        {
            if (obj == null)
                return float.MaxValue;

            float bestSq = GetClosestEnemyDistanceSqToPoint(obj.GetTaskPosition(), myFaction, leaderPositions);
            if (obj.missionTask != null)
            {
                try
                {
                    BattleData bd = _currentDirectorBattleData ?? BattleManager.GetCurrentBattleData();
                    if (AiCore.ClassifyMissionObjectiveForFaction(obj, myFaction, bd, out bool isHeld, out _, out float contestUrgency) &&
                        isHeld &&
                        contestUrgency > 0.05f)
                    {
                        // Back-cap / active capture alarm: even if only a lone enemy rifleman is inside the cap zone
                        // (without an enemy squad leader nearby), treat the point as critically threatened (< 50m)
                        // and scale priority urgently as capture/drain urgency approaches 100%.
                        float capVirtualDistSq = Mathf.Lerp(50f * 50f, 8f * 8f, contestUrgency);
                        bestSq = Mathf.Min(bestSq * Mathf.Lerp(0.25f, 0.05f, contestUrgency), capVirtualDistSq);
                    }
                }
                catch { }
            }
            return bestSq;
        }

        private static float GetClosestEnemyDistanceSqToPoint(
            Vector3 point,
            string myFaction,
            List<KeyValuePair<string, Vector3>> leaderPositions)
        {
            float bestSq = float.MaxValue;
            for (int i = 0; i < leaderPositions.Count; i++)
            {
                var kvp = leaderPositions[i];
                if (kvp.Key == myFaction)
                    continue;
                float dSq = (kvp.Value - point).sqrMagnitude;
                if (dSq < bestSq)
                    bestSq = dSq;
            }
            return bestSq;
        }

        private static MissionObjective GetClosestObjectiveToPosition(Vector3 pos, string faction, List<MissionObjective> list)
        {
            MissionObjective best = null;
            float bestSq = float.MaxValue;
            BattleData bd = _currentDirectorBattleData;
            for (int i = 0; i < list.Count; i++)
            {
                MissionObjective obj = list[i];
                float dSq = (obj.GetTaskPosition() - pos).sqrMagnitude;
                if (obj.missionTask != null)
                {
                    try
                    {
                        if (bd == null)
                            bd = BattleManager.GetCurrentBattleData();

                        if (AiCore.ClassifyMissionObjectiveForFaction(obj, faction, bd, out bool isHeld, out _, out float contestOrProgress))
                        {
                            if (contestOrProgress > 0.15f)
                            {
                                // Weight nearby partially captured/contested objectives higher so squads finish the cap or stop a back-cap
                                dSq *= Mathf.Lerp(0.85f, 0.35f, contestOrProgress);
                            }
                            else if (!isHeld)
                            {
                                // On Conquest opening/flips, slightly favor unclaimed neutral flags over fortified 99% enemy flags at comparable range
                                MissionObjectiveScripted sObj = obj.TryCast<MissionObjectiveScripted>();
                                if (sObj != null && string.IsNullOrEmpty(sObj.securedFaction) && obj.missionTask.GetTaskCompletationPercentage() < 0.05f)
                                {
                                    dSq *= 0.78f;
                                }
                            }
                        }
                    }
                    catch { }
                }
                if (dSq < bestSq)
                {
                    bestSq = dSq;
                    best = obj;
                }
            }
            return best;
        }

        // ============================================================================
        // DYNAMIC OPERATIONS (ConquestEconomy) NET QUARTERMASTER & PLAYER TICKET BUFFER
        // Fixes vanilla's omission where SpawnManager.CanSpawn(false) and SpawnAISquad
        // completely bypassed ConquestEconomy, spawning 400-500 ticket tanks/aircraft for
        // 0 tickets on minute 0 while human players had to pay full ticket prices.
        // Strategically surges cheap infantry waves when outnumbered, saves surplus tickets
        // to buy heavy armor/aircraft counters, and reserves a ticket buffer for human players.
        // ============================================================================
        private struct ConquestFactionQuartermasterState
        {
            public int AliveFootSoldiers;
            public int ActiveFriendlyTanks;
            public int ActiveFriendlyPlanes;
            public int EnemyFootSoldiers;
            public int EnemyActiveTanks;
            public int EnemyActivePlanes;
            public int ReservedPlayerBuffer;
            public int BestAvailableHeavyCost;
            public bool HasCheapInfantrySpawner;
            public bool SurgeCheapInfantry;
            public bool BankForHeavyCounter;
            public float BankWindowUntil;
            public float NextBankAllowedTime;
        }

        private struct CachedHumanFactionEntry
        {
            public string Faction;
            public float ExpireTime;
            public bool CurrentlyAlive;
        }

        private static readonly Dictionary<string, ConquestFactionQuartermasterState> _conquestQuartermasterByFaction =
            new Dictionary<string, ConquestFactionQuartermasterState>(8, StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<int, CachedHumanFactionEntry> _knownHumanPlayersByKey =
            new Dictionary<int, CachedHumanFactionEntry>(8);
        private static readonly List<string> _reusableConquestFactions = new List<string>(8);
        private static readonly List<int> _reusableExpiredHumanKeys = new List<int>(8);
        private static float _nextConquestQuartermasterUpdateTime;
        private static IntPtr _armedConquestSpawnPtr;
        private static int _armedConquestSpawnFrame = -999;

        public static int ConquestPurchasesCount { get; internal set; }
        public static int UnderwaterSpawnsRescuedCount { get; internal set; }
        private static int _spawnRescueScatterCounter;

        public static void ClearConquestQuartermasterState()
        {
            _conquestQuartermasterByFaction.Clear();
            _knownHumanPlayersByKey.Clear();
            _nextConquestQuartermasterUpdateTime = 0f;
            _armedConquestSpawnPtr = IntPtr.Zero;
            _armedConquestSpawnFrame = -999;
        }

        public static void UpdateConquestQuartermasterIfNeeded()
        {
            if (!VisionConfig.EnableMod.Value || !VisionConfig.EnableConquestEconomyQuartermaster.Value)
                return;

            if (Photon.Pun.PhotonNetwork.InRoom && !Photon.Pun.PhotonNetwork.IsMasterClient)
                return;

            try
            {
                if (!ConquestEconomy.IsActive)
                {
                    if (_conquestQuartermasterByFaction.Count > 0)
                        ClearConquestQuartermasterState();
                    return;
                }

                float now = Time.unscaledTime;
                if (now < _nextConquestQuartermasterUpdateTime)
                    return;

                _nextConquestQuartermasterUpdateTime = now + 2.5f;
                long profQmStart = HitchProfiler.Begin();
                HitchProfiler.RecordConquestQuartermasterRan();
                try
                {
                    RefreshConquestQuartermasterStates(now);
                }
                finally
                {
                    HitchProfiler.End(HitchBucket.ConquestQM, profQmStart);
                }
            }
            catch { }
        }

        private static void RefreshConquestQuartermasterStates(float now)
        {
            var spawns = SpawnManager.activeSpawns;
            if (spawns == null || spawns.Count == 0)
                return;

            // 1. Mark all tracked human players temporarily dead for this snapshot pass, then refresh from live units
            _reusableExpiredHumanKeys.Clear();
            foreach (var kv in _knownHumanPlayersByKey)
            {
                if (now >= kv.Value.ExpireTime)
                    _reusableExpiredHumanKeys.Add(kv.Key);
            }
            for (int i = 0; i < _reusableExpiredHumanKeys.Count; i++)
                _knownHumanPlayersByKey.Remove(_reusableExpiredHumanKeys[i]);

            // Record Host/SP player faction even while dead on the respawn screen!
            try
            {
                string hostFaction = BattleManager.PlayerFaction;
                if (!string.IsNullOrEmpty(hostFaction))
                {
                    Soldier localControlled = Soldier.CurrentControlledSoldierOrNull();
                    bool localAlive = localControlled != null && localControlled.IsAlive;
                    _knownHumanPlayersByKey[0] = new CachedHumanFactionEntry
                    {
                        Faction = hostFaction,
                        ExpireTime = now + 300f,
                        CurrentlyAlive = localAlive
                    };
                }
            }
            catch { }

            // Collect active factions from SpawnManagers
            _reusableConquestFactions.Clear();
            int spawnCount = spawns.Count;
            for (int i = 0; i < spawnCount; i++)
            {
                SpawnManager sm = spawns[i];
                if (sm == null || sm.Pointer == IntPtr.Zero)
                    continue;

                string f = sm.GetFaction();
                if (string.IsNullOrEmpty(f))
                    continue;

                bool exists = false;
                for (int j = 0; j < _reusableConquestFactions.Count; j++)
                {
                    if (string.Equals(_reusableConquestFactions[j], f, StringComparison.OrdinalIgnoreCase))
                    {
                        exists = true;
                        break;
                    }
                }
                if (!exists)
                    _reusableConquestFactions.Add(f);
            }

            if (_reusableConquestFactions.Count == 0)
                return;

            // Reset alive flags on remote player entries before scanning WorldSoldiers
            if (_knownHumanPlayersByKey.Count > 1)
            {
                _reusableExpiredHumanKeys.Clear();
                foreach (var kv in _knownHumanPlayersByKey)
                {
                    if (kv.Key != 0)
                        _reusableExpiredHumanKeys.Add(kv.Key);
                }
                for (int i = 0; i < _reusableExpiredHumanKeys.Count; i++)
                {
                    int k = _reusableExpiredHumanKeys[i];
                    var entry = _knownHumanPlayersByKey[k];
                    entry.CurrentlyAlive = false;
                    _knownHumanPlayersByKey[k] = entry;
                }
            }

            AiCore.EnsureWorldSnapshot();
            var squads = AiCore.WorldSquads;
            var soldiers = AiCore.WorldSoldiers;
            int sqCount = AiCore.WorldSquadCount;

            // Update remote Co-op human players from WorldSoldiers
            for (int s = 0; s < sqCount; s++)
            {
                ref AiCore.SquadSnapshot sqSnap = ref squads[s];
                int end = sqSnap.UnitStartIndex + sqSnap.UnitCount;
                for (int u = sqSnap.UnitStartIndex; u < end; u++)
                {
                    ref AiCore.SoldierSnapshot solSnap = ref soldiers[u];
                    if (!solSnap.IsPlayer || solSnap.Ref == null || string.IsNullOrEmpty(solSnap.Faction))
                        continue;

                    int ownerKey = 0;
                    try
                    {
                        SyncSoldier syncher = solSnap.Ref.soldierSyncher;
                        if (syncher != null)
                        {
                            Photon.Realtime.Player ctrl = syncher.Controller ?? syncher.Owner;
                            if (ctrl != null && ctrl.ActorNumber > 0)
                                ownerKey = ctrl.ActorNumber;
                        }
                    }
                    catch { }

                    if (ownerKey == 0 && solSnap.Ref == Soldier.CurrentControlledSoldierOrNull())
                        ownerKey = 0;
                    else if (ownerKey == 0)
                        ownerKey = solSnap.Ptr.ToInt32();

                    _knownHumanPlayersByKey[ownerKey] = new CachedHumanFactionEntry
                    {
                        Faction = solSnap.Faction,
                        ExpireTime = now + 210f,
                        CurrentlyAlive = true
                    };
                }
            }

            // Build per-faction force & spawner profile
            var vehList = Vehicle.allVehicles;
            int vehCount = vehList != null ? vehList.Count : 0;

            for (int fIdx = 0; fIdx < _reusableConquestFactions.Count; fIdx++)
            {
                string faction = _reusableConquestFactions[fIdx];
                _conquestQuartermasterByFaction.TryGetValue(faction, out ConquestFactionQuartermasterState prev);

                ConquestFactionQuartermasterState st = new ConquestFactionQuartermasterState
                {
                    BankWindowUntil = prev.BankWindowUntil,
                    NextBankAllowedTime = prev.NextBankAllowedTime
                };

                // Count foot troops
                for (int s = 0; s < sqCount; s++)
                {
                    ref AiCore.SquadSnapshot sqSnap = ref squads[s];
                    if (sqSnap.UnitCount == 0 || sqSnap.RefOnPlane || string.IsNullOrEmpty(sqSnap.Faction))
                        continue;

                    bool sameSide = string.Equals(sqSnap.Faction, faction, StringComparison.OrdinalIgnoreCase) ||
                                    AiCore.AreSameSideFactions(faction, sqSnap.Faction);

                    int footInSq = 0;
                    int end = sqSnap.UnitStartIndex + sqSnap.UnitCount;
                    for (int u = sqSnap.UnitStartIndex; u < end; u++)
                    {
                        if (!soldiers[u].IsOnVehicle)
                            footInSq++;
                    }

                    if (sameSide)
                        st.AliveFootSoldiers += footInSq;
                    else
                        st.EnemyFootSoldiers += footInSq;
                }

                // Count active crewed tanks & combat aircraft
                for (int v = 0; v < vehCount; v++)
                {
                    Vehicle veh = vehList[v];
                    if (veh == null || veh.Pointer == IntPtr.Zero || veh.IsDisabled() || !veh.CanFight())
                        continue;

                    string vFac = GetActiveVehicleCrewFaction(veh);
                    if (string.IsNullOrEmpty(vFac))
                        continue;

                    bool sameSide = string.Equals(vFac, faction, StringComparison.OrdinalIgnoreCase) ||
                                    AiCore.AreSameSideFactions(faction, vFac);

                    if (veh.IsAirVehicle())
                    {
                        if (sameSide) st.ActiveFriendlyPlanes++;
                        else st.EnemyActivePlanes++;
                    }
                    else if (veh.TryCast<VehicleTank>() != null)
                    {
                        if (sameSide) st.ActiveFriendlyTanks++;
                        else st.EnemyActiveTanks++;
                    }
                }

                // Compute Human Player Ticket Buffer (0 on AI-only factions; scaled on human-occupied factions!)
                int humansOnSide = 0;
                int aliveHumansOnSide = 0;
                foreach (var kv in _knownHumanPlayersByKey)
                {
                    string hFac = kv.Value.Faction;
                    if (!string.IsNullOrEmpty(hFac) &&
                        (string.Equals(hFac, faction, StringComparison.OrdinalIgnoreCase) ||
                         AiCore.AreSameSideFactions(faction, hFac)))
                    {
                        humansOnSide++;
                        if (kv.Value.CurrentlyAlive)
                            aliveHumansOnSide++;
                    }
                }

                if (humansOnSide > 0)
                {
                    int baseBuf = Mathf.Max(0, VisionConfig.ConquestPlayerTicketBuffer.Value);
                    int extraPlayersBuf = Mathf.Max(0, humansOnSide - 1) * 75;
                    int respawnMenuCushion = (aliveHumansOnSide < humansOnSide && baseBuf > 0) ? 60 : 0;
                    st.ReservedPlayerBuffer = baseBuf + extraPlayersBuf + respawnMenuCushion;
                }
                else
                {
                    st.ReservedPlayerBuffer = 0;
                }

                // Inspect available AI spawners for this faction
                for (int i = 0; i < spawnCount; i++)
                {
                    SpawnManager sm = spawns[i];
                    if (sm == null || sm.Pointer == IntPtr.Zero)
                        continue;

                    SpawnType sType = sm.spawnType;
                    if (sType == SpawnType.None || sType == SpawnType.onlyPlayer)
                        continue;

                    string smFac = AiCore.InternFaction(sm.GetFaction());
                    if (string.IsNullOrEmpty(smFac) ||
                        (!string.Equals(smFac, faction, StringComparison.OrdinalIgnoreCase) &&
                         !AiCore.AreSameSideFactions(faction, smFac)))
                    {
                        continue;
                    }

                    int cost = sm.scriptedSpawnCost;
                    if (cost <= 90)
                    {
                        st.HasCheapInfantrySpawner = true;
                    }
                    else if (cost >= 220 && cost > st.BestAvailableHeavyCost && IsAnyVehicleSlotReady(sm))
                    {
                        st.BestAvailableHeavyCost = cost;
                    }
                }

                // Decide Quartermaster strategic posture
                st.SurgeCheapInfantry = st.AliveFootSoldiers < 18 ||
                                        (st.AliveFootSoldiers * 1.35f < st.EnemyFootSoldiers && st.AliveFootSoldiers < 32);

                int curBudget = ConquestEconomy.GetBudget(faction);
                bool wantsHeavyAsset = !st.SurgeCheapInfantry &&
                                       st.BestAvailableHeavyCost >= 220 &&
                                       ((st.EnemyActiveTanks > st.ActiveFriendlyTanks && st.ActiveFriendlyTanks == 0) ||
                                        (st.EnemyActivePlanes > 0 && st.ActiveFriendlyPlanes == 0) ||
                                        (st.AliveFootSoldiers >= 26 && st.ActiveFriendlyTanks == 0));

                int targetHeavyThreshold = st.ReservedPlayerBuffer + st.BestAvailableHeavyCost;
                int minSaveStartBudget = st.ReservedPlayerBuffer + Mathf.RoundToInt(st.BestAvailableHeavyCost * 0.45f);

                if (wantsHeavyAsset && curBudget >= minSaveStartBudget && curBudget < targetHeavyThreshold)
                {
                    if (st.BankWindowUntil > now)
                    {
                        st.BankForHeavyCounter = true;
                    }
                    else if (now >= st.NextBankAllowedTime)
                    {
                        st.BankWindowUntil = now + 50.0f;
                        st.NextBankAllowedTime = now + 80.0f;
                        st.BankForHeavyCounter = true;
                        if (VisionConfig.DebugLogging.Value)
                        {
                            AiCore.LogDebug(
                                "ConquestQM",
                                $"{faction} Quartermaster banking tickets ({curBudget}/{targetHeavyThreshold}, buf={st.ReservedPlayerBuffer}) for {st.BestAvailableHeavyCost}-ticket armor/air support.");
                        }
                    }
                }
                else
                {
                    st.BankForHeavyCounter = false;
                }

                _conquestQuartermasterByFaction[faction] = st;
            }
        }

        private static string GetActiveVehicleCrewFaction(Vehicle veh)
        {
            var seats = veh.seats;
            if (seats == null)
                return null;

            int len = seats.Length;
            for (int i = 0; i < len; i++)
            {
                Seats s = seats[i];
                if (s != null && s.unitSet != null && s.unitSet.IsAlive && !string.IsNullOrEmpty(s.unitSet.faction))
                    return s.unitSet.faction;
            }
            return null;
        }

        private static bool IsAnyVehicleSlotReady(SpawnManager sm)
        {
            var vehSpawners = sm.spawnOnVehicle;
            if (vehSpawners == null || vehSpawners.Length == 0)
                return true;

            for (int i = 0; i < vehSpawners.Length; i++)
            {
                VehicleSpawner vs = vehSpawners[i];
                if (vs != null && vs.IsAvailable())
                    return true;
            }
            return false;
        }

        private static bool CanAiQuartermasterAuthorizeSpawn(SpawnManager sm, int cost)
        {
            if (cost <= 0 || sm == null)
                return true;

            string faction = sm.GetFaction();
            if (string.IsNullOrEmpty(faction))
                return true;

            if (!ConquestEconomy.CanAfford(faction, cost))
                return false;

            float now = Time.unscaledTime;
            if (!_conquestQuartermasterByFaction.TryGetValue(faction, out ConquestFactionQuartermasterState st))
            {
                RefreshConquestQuartermasterStates(now);
                if (!_conquestQuartermasterByFaction.TryGetValue(faction, out st))
                    return true;
            }

            int budget = ConquestEconomy.GetBudget(faction);
            int remaining = budget - cost;

            var vehSpawners = sm.spawnOnVehicle;
            bool isPureFootWave = cost <= 65 && (vehSpawners == null || vehSpawners.Length == 0);

            // 1. Respect Human Player Ticket Buffer (allowing emergency cheap foot waves only if frontline collapsed < 10 infantry)
            if (isPureFootWave && st.AliveFootSoldiers < 10)
            {
                int emergencyMin = Mathf.Min(st.ReservedPlayerBuffer / 2, 35);
                if (remaining < emergencyMin)
                    return false;
            }
            else if (remaining < st.ReservedPlayerBuffer)
            {
                return false;
            }

            // 2. Surge cheap infantry waves first when foot strength is depleted (unless urgently countering enemy armor or flush with tickets)
            if (st.SurgeCheapInfantry &&
                st.HasCheapInfantrySpawner &&
                cost >= 180 &&
                st.EnemyActiveTanks <= st.ActiveFriendlyTanks &&
                remaining < st.ReservedPlayerBuffer + 150)
            {
                return false;
            }

            // 3. When banking toward an available 220-500 ticket tank/aircraft counter, hold mid-tier 75+ ticket purchases temporarily
            if (st.BankForHeavyCounter &&
                now < st.BankWindowUntil &&
                cost >= 75 &&
                cost < st.BestAvailableHeavyCost - 25)
            {
                return false;
            }

            return true;
        }

        [HarmonyPatch(typeof(SpawnManager), nameof(SpawnManager.CanSpawn))]
        public static class SpawnManager_CanSpawn_Patch
        {
            public static void Postfix(SpawnManager __instance, bool __0, ref bool __result)
            {
                try
                {
                    // Only gate AI spawns (!isPlayer) when vanilla CanSpawn returned true and ConquestEconomy is active
                    if (!__result ||
                        __0 ||
                        !VisionConfig.EnableMod.Value ||
                        !VisionConfig.EnableConquestEconomyQuartermaster.Value ||
                        __instance == null ||
                        __instance.Pointer == IntPtr.Zero)
                    {
                        return;
                    }

                    if (Photon.Pun.PhotonNetwork.InRoom && !Photon.Pun.PhotonNetwork.IsMasterClient)
                        return;

                    if (!ConquestEconomy.IsActive)
                        return;

                    int cost = __instance.scriptedSpawnCost;
                    if (cost <= 0)
                        return;

                    if (!CanAiQuartermasterAuthorizeSpawn(__instance, cost))
                    {
                        __result = false;
                        return;
                    }

                    _armedConquestSpawnPtr = __instance.Pointer;
                    _armedConquestSpawnFrame = Time.frameCount;
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(SpawnManager._SpawnAISquad_d__45), nameof(SpawnManager._SpawnAISquad_d__45.MoveNext))]
        public static class SpawnManager_SpawnAISquadMoveNext_Patch
        {
            public struct SpawnMoveNextProbeState
            {
                public bool IsStep0;
                public long StartStamp;
            }

            public static void Prefix(SpawnManager._SpawnAISquad_d__45 __instance, out SpawnMoveNextProbeState __state)
            {
                __state = new SpawnMoveNextProbeState
                {
                    IsStep0 = __instance != null &&
                              __instance.Pointer != IntPtr.Zero &&
                              __instance.__1__state == 0 &&
                              __instance.__8__1 == null,
                    StartStamp = HitchProfiler.BeginTopLevel()
                };
            }

            public static void Postfix(SpawnManager._SpawnAISquad_d__45 __instance, SpawnMoveNextProbeState __state)
            {
                try
                {
                    if (__state.IsStep0 &&
                        __instance != null &&
                        __instance.Pointer != IntPtr.Zero &&
                        __instance.__8__1 != null)
                    {
                        SpawnManager spawnedSm = __instance.__4__this;
                        if (spawnedSm != null && spawnedSm.Pointer != IntPtr.Zero)
                        {
                            var vSlots = spawnedSm.spawnOnVehicle;
                            HitchProfiler.RecordSquadSpawned(spawnedSm.squad_id, vSlots != null && vSlots.Length > 0);
                        }
                    }

                    // Charge ONLY when step 0 of <SpawnAISquad>d__45.MoveNext actually passed CanSpawn(false)
                    // and instantiated the Squad closure (__8__1 != null)!
                    if (!__state.IsStep0 ||
                        __instance == null ||
                        __instance.Pointer == IntPtr.Zero ||
                        __instance.__8__1 == null ||
                        __instance.squadIsForPlayer ||
                        __instance.forceSpawn ||
                        !VisionConfig.EnableMod.Value ||
                        !VisionConfig.EnableConquestEconomyQuartermaster.Value)
                    {
                        return;
                    }

                    if (Photon.Pun.PhotonNetwork.InRoom && !Photon.Pun.PhotonNetwork.IsMasterClient)
                        return;

                    if (!ConquestEconomy.IsActive)
                        return;

                    SpawnManager sm = __instance.__4__this;
                    if (sm == null || sm.Pointer == IntPtr.Zero)
                        return;

                    int cost = sm.scriptedSpawnCost;
                    if (cost <= 0)
                        return;

                    string faction = sm.GetFaction();
                    if (string.IsNullOrEmpty(faction))
                        return;

                    bool wasArmedByCanSpawn = sm.Pointer == _armedConquestSpawnPtr &&
                                              _armedConquestSpawnFrame == Time.frameCount;
                    if (wasArmedByCanSpawn)
                        _armedConquestSpawnPtr = IntPtr.Zero;

                    // If startPhaseSpawn bypassed CanSpawn(false) via sm.forceFirstSpawn, treat that pre-placed
                    // opening garrison as free (matching scripted forceSpawn); otherwise charge the Conquest budget!
                    if (!wasArmedByCanSpawn && __instance.startPhaseSpawn)
                        return;

                    if (wasArmedByCanSpawn || ConquestEconomy.CanAfford(faction, cost))
                    {
                        ConquestEconomy.Charge(faction, cost);
                        ConquestPurchasesCount++;

                        if (cost >= 220 && _conquestQuartermasterByFaction.TryGetValue(faction, out ConquestFactionQuartermasterState st))
                        {
                            st.BankForHeavyCounter = false;
                            st.BankWindowUntil = 0f;
                            st.NextBankAllowedTime = Time.unscaledTime + 30.0f;
                            _conquestQuartermasterByFaction[faction] = st;
                        }

                        if (VisionConfig.DebugLogging.Value)
                        {
                            int rem = ConquestEconomy.GetBudget(faction);
                            AiCore.LogDebug(
                                "ConquestQM",
                                $"{faction} AI Quartermaster purchased '{sm.squad_id}' for {cost} tickets (remaining={rem}).");
                        }
                    }
                }
                catch { }
                finally
                {
                    HitchProfiler.End(HitchBucket.VanSpawnSquad, __state.StartStamp);
                }
            }
        }

        // ============================================================================
        // VANILLA COASTAL, AMPHIBIOUS, UNDERGROUND & UNPARACHUTED SPAWN-DEATH FIX
        // Vanilla Soldier.FixedUpdate (y < -1.7f when injured) and Creature.UpdateGenericValues
        // (y < -2.5f on ALL maps, even with zero water!) call KillSynched() directly when
        // !IsOnVehicle() && OwnSyncher, bypassing SetInvincible(2f..10f). Meanwhile,
        // SpawnManager.GetSpawnPosition casts a short 6.7m downward ray (misses steep/mountain
        // slopes, underwater seabeds, or underground spawners), Steps 0..3 yields drop
        // pre-seated passengers, full-boat overflows dump unseated bots offshore, and unseated
        // airborne/CargoPlane-overflow spawns lack openParachuteHeight and fall to death.
        // ============================================================================
        private const float DryMapVoidFloorThresholdY = -2.35f;
        private const float DryMapAcceptMinY = -1.60f;
        private const float ShoreAcceptMinNormalY = 0.60f;
        private const float UnparachutedDropSnapMinAgl = 6.5f;
        private const float AirborneParachuteMinAgl = 45.0f;
        private static readonly float[] _shoreSearchRadii = { 8f, 18f, 32f, 50f, 75f, 105f, 145f, 195f };
        private static readonly int[] _shoreSearchAngleSteps = { 0, 1, -1, 2, -2, 3, -3, 4, -4, 5, -5, 6 };

        private static void GetSpawnFloorThresholds(out float floorMinY, out float acceptMinY)
        {
            if (AiCore.TryGetActiveWaterLevel(out float waterY))
            {
                // Preserve shallow surf down to ~1.30m (e.g. Omaha Beach dismounts & beachhead spawns)
                // while scaling with custom water levels and guarding Creature's global y < -2.5f kill plane.
                floorMinY = Mathf.Max(-2.30f, waterY - 1.30f);
                acceptMinY = Mathf.Max(-2.00f, waterY - 1.05f);
            }
            else
            {
                // Waterless map: never relocate dry trenches/craters above -2.35m; only catch
                // underground/void spawns approaching Creature.UpdateGenericValues (y < -2.5f).
                floorMinY = DryMapVoidFloorThresholdY;
                acceptMinY = DryMapAcceptMinY;
            }
        }

        private static float GetTerrainProbeTopY(float x, float z, float refY)
        {
            float terrY = ResourcesManager.GetTerrainAltitude(new Vector3(x, refY, z), 0f);
            return Mathf.Max(Mathf.Max(refY + 45f, terrY + 35f), 65f);
        }

        private static bool RaycastStaticEnvironmentDown(
            Vector3 origin,
            float maxDistance,
            int mask,
            out RaycastHit envHit)
        {
            Vector3 probe = origin;
            float remaining = maxDistance;
            for (int hop = 0; hop < 3 && remaining > 0.25f; hop++)
            {
                if (!Physics.Raycast(probe, Vector3.down, out envHit, remaining, mask, QueryTriggerInteraction.Ignore))
                {
                    envHit = default;
                    return false;
                }

                Transform tr = envHit.transform;
                if (tr == null || (tr.GetComponentInParent<Vehicle>() == null && tr.GetComponentInParent<Creature>() == null))
                    return true;

                float consumed = (probe.y - envHit.point.y) + 0.08f;
                remaining -= consumed;
                probe = new Vector3(origin.x, envHit.point.y - 0.08f, origin.z);
            }

            envHit = default;
            return false;
        }

        private static bool IsUnseatedSpawnGroundSafe(
            Vector3 pos,
            float floorMinY,
            out Vector3 safePos,
            out bool isHighAltitudeDrop)
        {
            safePos = pos;
            isHighAltitudeDrop = false;

            int mask = AiCore.GetSolidEnvironmentMask();
            Vector3 probeStart = new Vector3(pos.x, pos.y + 1.6f, pos.z);
            if (!RaycastStaticEnvironmentDown(probeStart, 600f, mask, out RaycastHit hit) ||
                hit.point.y < floorMinY)
            {
                return false;
            }

            float agl = pos.y - hit.point.y;
            if (agl >= AirborneParachuteMinAgl)
            {
                float terrAlt = ResourcesManager.GetTerrainAltitude(pos, 5f);
                if ((pos.y - terrAlt) > 24.0f)
                {
                    isHighAltitudeDrop = true;
                    return true;
                }
            }

            if (pos.y < floorMinY || agl >= UnparachutedDropSnapMinAgl)
            {
                Vector3 snapped = hit.point + Vector3.up * 0.14f;
                if (hit.normal.y >= ShoreAcceptMinNormalY &&
                    !Physics.CheckSphere(snapped + Vector3.up * 0.95f, 0.26f, mask, QueryTriggerInteraction.Ignore))
                {
                    safePos = snapped;
                    return true;
                }

                return false;
            }

            return true;
        }

        private static bool TrySampleWalkableDryGround(
            float x,
            float z,
            float probeTopY,
            float probeDist,
            float acceptMinY,
            int mask,
            out Vector3 groundPos)
        {
            if (RaycastStaticEnvironmentDown(new Vector3(x, probeTopY, z), probeDist, mask, out RaycastHit hit) &&
                hit.point.y >= acceptMinY &&
                hit.normal.y >= ShoreAcceptMinNormalY &&
                !Physics.CheckSphere(hit.point + Vector3.up * 1.0f, 0.28f, mask, QueryTriggerInteraction.Ignore))
            {
                groundPos = hit.point + Vector3.up * 0.14f;
                return true;
            }

            groundPos = default;
            return false;
        }

        private static Vector3 ApplySquadmateShoreScatter(Vector3 baseDryPos, float acceptMinY, int mask)
        {
            int idx = ++_spawnRescueScatterCounter;
            float angle = idx * 2.399963f;
            float radius = 1.35f + (idx % 4) * 0.85f;
            float sx = baseDryPos.x + Mathf.Cos(angle) * radius;
            float sz = baseDryPos.z + Mathf.Sin(angle) * radius;

            if (TrySampleWalkableDryGround(sx, sz, baseDryPos.y + 8.0f, 18.0f, acceptMinY, mask, out Vector3 scattered))
                return scattered;

            return baseDryPos;
        }

        private static float ComputePreferredLandwardAngleRad(Vector3 origin, string faction)
        {
            var spawns = SpawnManager.activeSpawns;
            if (spawns == null || spawns.Count == 0)
                return 0f;

            float bestFriendlyDistSq = float.MaxValue;
            Vector3 bestFriendlyPos = Vector3.zero;
            bool hasFriendly = false;

            float bestAnyDistSq = float.MaxValue;
            Vector3 bestAnyPos = Vector3.zero;
            bool hasAny = false;

            int count = spawns.Count;
            for (int i = 0; i < count; i++)
            {
                SpawnManager sm = spawns[i];
                if (sm == null || sm.Pointer == IntPtr.Zero || !sm.isActiveAndEnabled || sm.spawnType == SpawnType.None)
                    continue;

                Vector3 smPos = sm.transform.position;
                float dx = smPos.x - origin.x;
                float dz = smPos.z - origin.z;
                float dSq = dx * dx + dz * dz;
                if (dSq <= 36f)
                    continue;

                bool sameSide = string.IsNullOrEmpty(faction) || AiCore.AreSameSideFactions(faction, sm.GetFaction());
                if (sameSide && smPos.y >= -0.5f && dSq < bestFriendlyDistSq)
                {
                    bestFriendlyDistSq = dSq;
                    bestFriendlyPos = smPos;
                    hasFriendly = true;
                }
                else if (smPos.y >= 1.0f && dSq < bestAnyDistSq)
                {
                    bestAnyDistSq = dSq;
                    bestAnyPos = smPos;
                    hasAny = true;
                }
            }

            if (hasFriendly)
                return Mathf.Atan2(bestFriendlyPos.z - origin.z, bestFriendlyPos.x - origin.x);
            if (hasAny)
                return Mathf.Atan2(bestAnyPos.z - origin.z, bestAnyPos.x - origin.x);

            return 0f;
        }

        private static bool TryResolveSafeShoreOrLandSpawnPosition(
            Vector3 origin,
            string faction,
            bool isPlayer,
            float floorMinY,
            float acceptMinY,
            out Vector3 resolvedPos)
        {
            int mask = AiCore.GetSolidEnvironmentMask();
            Vector3 foundDryPos = default;
            bool found = false;

            // 1. Mountain-proof vertical re-probe at (origin.x, origin.z) to clear sunken coastal bunkers or steep hillsides
            float topY = GetTerrainProbeTopY(origin.x, origin.z, origin.y);
            float downDist = Mathf.Max(160f, (topY - floorMinY) + 35f);
            if (TrySampleWalkableDryGround(origin.x, origin.z, topY, downDist, acceptMinY, mask, out Vector3 verticalDry))
            {
                foundDryPos = verticalDry;
                found = true;
            }

            // 2. Landward-biased expanding radial shoreline/hillside search (8m..195m x 12 angles)
            if (!found)
            {
                float baseAngle = ComputePreferredLandwardAngleRad(origin, faction);
                const float angleStepRad = Mathf.PI / 6f;

                for (int rIdx = 0; rIdx < _shoreSearchRadii.Length && !found; rIdx++)
                {
                    float r = _shoreSearchRadii[rIdx];
                    for (int sIdx = 0; sIdx < _shoreSearchAngleSteps.Length; sIdx++)
                    {
                        float ang = baseAngle + _shoreSearchAngleSteps[sIdx] * angleStepRad;
                        float cx = origin.x + Mathf.Cos(ang) * r;
                        float cz = origin.z + Mathf.Sin(ang) * r;
                        float ringTopY = GetTerrainProbeTopY(cx, cz, origin.y);
                        float ringDownDist = Mathf.Max(160f, (ringTopY - floorMinY) + 35f);

                        if (TrySampleWalkableDryGround(cx, cz, ringTopY, ringDownDist, acceptMinY, mask, out Vector3 shoreHit))
                        {
                            foundDryPos = shoreHit;
                            found = true;
                            break;
                        }
                    }
                }
            }

            // 3. Fallback to nearest enabled dry friendly SpawnManager if far offshore (> 195m) or in sheer cliff void
            if (!found)
            {
                var spawns = SpawnManager.activeSpawns;
                if (spawns != null && spawns.Count > 0)
                {
                    float bestSmDistSq = float.MaxValue;
                    Vector3 bestSmPos = default;
                    int smCount = spawns.Count;

                    for (int i = 0; i < smCount; i++)
                    {
                        SpawnManager sm = spawns[i];
                        if (sm == null ||
                            sm.Pointer == IntPtr.Zero ||
                            !sm.isActiveAndEnabled ||
                            sm.spawnType == SpawnType.None ||
                            !sm.UserCanUseSpawner(isPlayer))
                        {
                            continue;
                        }

                        if (!string.IsNullOrEmpty(faction) && !AiCore.AreSameSideFactions(faction, sm.GetFaction()))
                            continue;

                        Vector3 smPos = sm.transform.position;
                        float dSq = (smPos - origin).sqrMagnitude;
                        if (dSq >= bestSmDistSq)
                            continue;

                        float smTopY = GetTerrainProbeTopY(smPos.x, smPos.z, smPos.y);
                        float smDownDist = Mathf.Max(160f, (smTopY - floorMinY) + 35f);
                        if (TrySampleWalkableDryGround(smPos.x, smPos.z, smTopY, smDownDist, acceptMinY, mask, out Vector3 smDry))
                        {
                            bestSmDistSq = dSq;
                            bestSmPos = smDry;
                            found = true;
                        }
                    }

                    if (found)
                        foundDryPos = bestSmPos;
                }
            }

            // 4. Last-resort fallback to nearest alive same-side ground squad on dry terrain
            if (!found)
            {
                AiCore.EnsureWorldSnapshot();
                var squads = AiCore.WorldSquads;
                int sqCount = AiCore.WorldSquadCount;
                float bestSqDistSq = float.MaxValue;
                Vector3 bestSqDry = default;

                for (int s = 0; s < sqCount; s++)
                {
                    ref AiCore.SquadSnapshot sqSnap = ref squads[s];
                    if (sqSnap.UnitCount <= 0 || sqSnap.RefOnPlane)
                        continue;

                    if (!string.IsNullOrEmpty(faction) && !AiCore.AreSameSideFactions(faction, sqSnap.Faction))
                        continue;

                    Vector3 sqPos = sqSnap.RefPos;
                    float dSq = (sqPos - origin).sqrMagnitude;
                    if (dSq >= bestSqDistSq)
                        continue;

                    float sqTopY = GetTerrainProbeTopY(sqPos.x, sqPos.z, sqPos.y);
                    float sqDownDist = Mathf.Max(160f, (sqTopY - floorMinY) + 35f);
                    if (TrySampleWalkableDryGround(sqPos.x, sqPos.z, sqTopY, sqDownDist, acceptMinY, mask, out Vector3 sqDry))
                    {
                        bestSqDistSq = dSq;
                        bestSqDry = sqDry;
                        found = true;
                    }
                }

                if (found)
                    foundDryPos = bestSqDry;
            }

            if (!found)
            {
                resolvedPos = origin;
                return false;
            }

            resolvedPos = ApplySquadmateShoreScatter(foundDryPos, acceptMinY, mask);
            return true;
        }

        private static void TeleportSpawnedSoldierSafely(Soldier soldier, Vector3 safePos)
        {
            soldier.TeleportTo(safePos);
            soldier.ResetGroundLeavePos();
            soldier.currentVelocity = Vector3.zero;
        }

        /// <summary>
        /// Prevents vanilla coastal/amphibious spawn-drowning, dry-map underground/void spawns below
        /// <c>y = -2.5f</c>, steep cliff-ray misses, and unparachuted high-altitude / <see cref="CargoPlane"/>
        /// overflow falls across all <see cref="SpawnManager._SpawnUnit_d__37"/> execution stages.
        /// </summary>
        [HarmonyPatch(typeof(SpawnManager._SpawnUnit_d__37), nameof(SpawnManager._SpawnUnit_d__37.MoveNext))]
        public static class SpawnManager_SpawnUnitMoveNext_Patch
        {
            public struct SpawnUnitGuardState
            {
                public int PrevState;
                public bool RescuedPreInstantiate;
            }

            public static void Prefix(SpawnManager._SpawnUnit_d__37 __instance, out SpawnUnitGuardState __state)
            {
                __state = default;
                try
                {
                    if (!VisionConfig.EnableMod.Value || __instance == null || __instance.Pointer == IntPtr.Zero)
                        return;

                    if (__instance.init_as_ai && Photon.Pun.PhotonNetwork.InRoom && !Photon.Pun.PhotonNetwork.IsMasterClient)
                        return;

                    int st = __instance.__1__state;
                    __state.PrevState = st;

                    Vehicle spawnVeh = __instance.spawnVehicle;
                    bool hasSpawnVeh = spawnVeh != null && spawnVeh.Pointer != IntPtr.Zero;
                    GetSpawnFloorThresholds(out float floorMinY, out float acceptMinY);

                    if (st == 0)
                    {
                        if (hasSpawnVeh)
                        {
                            Vector3 vPos = spawnVeh.transform.position;
                            if (__instance.position.y < floorMinY + 0.5f)
                                __instance.position = new Vector3(vPos.x, Mathf.Max(vPos.y + 2.2f, floorMinY + 2.8f), vPos.z);
                        }
                        else
                        {
                            Vector3 origPos = __instance.position;
                            if (IsUnseatedSpawnGroundSafe(origPos, floorMinY, out Vector3 snappedPos, out _))
                            {
                                if ((snappedPos - origPos).sqrMagnitude > 0.01f)
                                {
                                    __instance.position = snappedPos;
                                    if (origPos.y < floorMinY || (origPos.y - snappedPos.y) >= 18.0f)
                                    {
                                        __state.RescuedPreInstantiate = true;
                                        UnderwaterSpawnsRescuedCount++;

                                        if (VisionConfig.DebugLogging.Value)
                                        {
                                            AiCore.LogDebug(
                                                "SpawnRescue",
                                                $"Snapped unsafe spawn ({__instance.faction}) from {origPos} to {snappedPos}.",
                                                2.0f);
                                        }
                                    }
                                }
                            }
                            else
                            {
                                if (TryResolveSafeShoreOrLandSpawnPosition(
                                        origPos,
                                        __instance.faction,
                                        !__instance.init_as_ai,
                                        floorMinY,
                                        acceptMinY,
                                        out Vector3 rescuedPos))
                                {
                                    __instance.position = rescuedPos;
                                    __state.RescuedPreInstantiate = true;
                                    UnderwaterSpawnsRescuedCount++;

                                    if (VisionConfig.DebugLogging.Value)
                                    {
                                        AiCore.LogDebug(
                                            "SpawnRescue",
                                            $"Relocated underwater/underground spawn ({__instance.faction}) from {origPos} to {rescuedPos}.",
                                            2.0f);
                                    }
                                }
                            }
                        }
                    }
                    else if (st >= 1 && st <= 4 && hasSpawnVeh)
                    {
                        Soldier stagingSol = __instance._s_5__3;
                        if (stagingSol != null &&
                            stagingSol.Pointer != IntPtr.Zero &&
                            AiCore.IsLocallyOwnedSoldier(stagingSol) &&
                            !stagingSol.IsOnVehicle() &&
                            stagingSol.transform.position.y < floorMinY + 0.65f)
                        {
                            Vector3 vPos = spawnVeh.transform.position;
                            Vector3 holdPos = new Vector3(vPos.x, Mathf.Max(vPos.y + 2.2f, floorMinY + 2.8f), vPos.z);
                            TeleportSpawnedSoldierSafely(stagingSol, holdPos);
                        }
                    }
                }
                catch { }
            }

            public static void Postfix(SpawnManager._SpawnUnit_d__37 __instance, bool __result, SpawnUnitGuardState __state)
            {
                if (__result || (__state.PrevState != 3 && __state.PrevState != 4))
                    return;

                try
                {
                    if (!VisionConfig.EnableMod.Value || __instance == null || __instance.Pointer == IntPtr.Zero)
                        return;

                    if (__instance.init_as_ai && Photon.Pun.PhotonNetwork.InRoom && !Photon.Pun.PhotonNetwork.IsMasterClient)
                        return;

                    Soldier s = __instance._s_5__3;
                    if (s == null || s.Pointer == IntPtr.Zero || !AiCore.IsLocallyOwnedSoldier(s) || s.IsOnVehicle())
                        return;

                    GetSpawnFloorThresholds(out float floorMinY, out float acceptMinY);
                    Vector3 postPos = s.transform.position;
                    if (!IsUnseatedSpawnGroundSafe(postPos, floorMinY, out Vector3 postSnapped, out bool isHighAltDrop))
                    {
                        if (TryResolveSafeShoreOrLandSpawnPosition(
                                postPos,
                                __instance.faction,
                                !__instance.init_as_ai,
                                floorMinY,
                                acceptMinY,
                                out Vector3 rescuedPostPos))
                        {
                            TeleportSpawnedSoldierSafely(s, rescuedPostPos);
                            if (__instance.spawnVehicle != null)
                                s.dontLeaveVehicle = false;

                            SoldierAI ai = s.aiController;
                            if (ai != null)
                                ai.moveDirectlyToward_end = 0f;

                            if (!__state.RescuedPreInstantiate)
                                UnderwaterSpawnsRescuedCount++;

                            if (VisionConfig.DebugLogging.Value)
                            {
                                AiCore.LogDebug(
                                    "SpawnRescue",
                                    $"Rescued unseated post-spawn unit ({__instance.faction}) from {postPos} to {rescuedPostPos}.",
                                    2.0f);
                            }
                        }
                    }
                    else if (isHighAltDrop)
                    {
                        if (__instance.spawnVehicle != null)
                            s.dontLeaveVehicle = false;

                        if (!s.FallingWithParachute() && s.useParachuteHeight <= 0.01f)
                        {
                            float terrAlt = ResourcesManager.GetTerrainAltitude(postPos, 5f);
                            s.UseParachute(terrAlt + 450.0f);

                            if (!__state.RescuedPreInstantiate)
                                UnderwaterSpawnsRescuedCount++;

                            if (VisionConfig.DebugLogging.Value)
                            {
                                AiCore.LogDebug(
                                    "SpawnRescue",
                                    $"Armed parachute on high-altitude unseated spawn ({__instance.faction}) at {postPos} (openHeight={terrAlt + 450.0f:F1}).",
                                    2.0f);
                            }
                        }
                    }
                    else if ((postSnapped - postPos).sqrMagnitude > 0.01f)
                    {
                        TeleportSpawnedSoldierSafely(s, postSnapped);
                        if (__instance.spawnVehicle != null)
                            s.dontLeaveVehicle = false;

                        if ((postPos.y < floorMinY || (postPos.y - postSnapped.y) >= 18.0f) && !__state.RescuedPreInstantiate)
                            UnderwaterSpawnsRescuedCount++;
                    }
                }
                catch { }
            }
        }

        private const string HqAcceptRadialSentinel = "__BH_HQ_ACCEPT__";
        private const string HqBalkRadialSentinel = "__BH_HQ_BALK__";

        /// <summary>
        /// Renders a pooled native <c>[HQ]</c> preview marker on the <c>M</c> tactical map whenever the local
        /// Player Squad Leader has an active unaccepted HQ / Radio Net order.
        /// </summary>
        [HarmonyPatch(typeof(MiniMapGUI), nameof(MiniMapGUI.DisableRemainingUnitMarkers))]
        public static class MiniMapGUI_DisableRemainingUnitMarkers_Patch
        {
            public static void Prefix(MiniMapGUI __instance)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value || __instance == null || !MiniMapGUI.MiniMapOpened)
                        return;

                    if (!TryGetActivePendingPlayerHqOrder(out PlayerPendingHqOrderState pending))
                        return;

                    string badge = string.IsNullOrEmpty(pending.MapBadgeText) ? "[HQ]" : pending.MapBadgeText;
                    UnityEngine.UI.Image img = __instance.GetNextUnitMarker(badge, true);
                    if (img == null || img.rectTransform == null)
                        return;

                    RectTransform rt = img.rectTransform;
                    rt.anchoredPosition = __instance.GetPositionInContainer(pending.WorldPos, true);
                    img.color = new Color(1.0f, 0.82f, 0.18f, 0.96f);
                    rt.localEulerAngles = new Vector3(0f, 0f, -__instance.northShift);

                    Vector3 baseScale = __instance.unis_marker != null
                        ? __instance.unis_marker.transform.localScale
                        : Vector3.one;
                    if (baseScale.sqrMagnitude < 0.001f)
                        baseScale = Vector3.one;

                    float invZoom = 1.15f / Mathf.Max(0.25f, MiniMapGUI.zoomValue);
                    rt.localScale = baseScale * invZoom;
                }
                catch { }
            }
        }

        /// <summary>
        /// Injects <c>Accept HQ: ...</c> and <c>Balk HQ Order</c> wedges into the root <c>Q</c> Orders radial
        /// (<see cref="PlayerController"/> <c>OpenOrdersMenu</c>) while leaving sub-menus untouched.
        /// </summary>
        [HarmonyPatch(typeof(CircularMenuSelection.CircularMenu2), nameof(CircularMenuSelection.CircularMenu2.ShowCircle))]
        public static class CircularMenu2_ShowCircle_Patch
        {
            public static void Prefix(
                ref Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<CircularMenuSelection.CircularMenuData> __0,
                GameInput __1,
                ref Il2CppSystem.Action<CircularMenuSelection.CircularMenuData> __2,
                string __3)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value ||
                        __1 != GameInput.OrdersMenu ||
                        !string.IsNullOrEmpty(__3) ||
                        __0 == null ||
                        __0.Length == 0)
                    {
                        return;
                    }

                    CircularMenuSelection.CircularMenuData firstItem = __0[0];
                    if (firstItem == null || firstItem.returnData == null || firstItem.returnData.TryCast<Soldier>() != null)
                        return;

                    CircularMenuSelection.CircularMenuData lastItem = __0[__0.Length - 1];
                    if (lastItem != null &&
                        (string.Equals(lastItem.text, "Balk HQ Order", StringComparison.Ordinal) ||
                         (lastItem.returnData != null && string.Equals(lastItem.returnData.ToString(), HqBalkRadialSentinel, StringComparison.Ordinal))))
                    {
                        return;
                    }

                    if (!TryGetActivePendingPlayerHqOrder(out PlayerPendingHqOrderState pending))
                        return;

                    Sprite acceptSprite = null;
                    if (pending.TargetTask != null)
                    {
                        try { acceptSprite = pending.TargetTask.GetTaskIcon(); } catch { }
                    }
                    if (acceptSprite == null)
                        acceptSprite = firstItem.image;

                    int origLen = __0.Length;
                    var expanded = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<CircularMenuSelection.CircularMenuData>(origLen + 2);
                    for (int i = 0; i < origLen; i++)
                        expanded[i] = __0[i];

                    expanded[origLen] = new CircularMenuSelection.CircularMenuData(
                        acceptSprite,
                        $"Accept HQ: {pending.ShortActionLabel}",
                        new Color(0.55f, 0.95f, 0.55f, 1.0f),
                        (Il2CppSystem.String)HqAcceptRadialSentinel);

                    expanded[origLen + 1] = new CircularMenuSelection.CircularMenuData(
                        firstItem.image,
                        "Balk HQ Order",
                        new Color(0.95f, 0.55f, 0.45f, 1.0f),
                        (Il2CppSystem.String)HqBalkRadialSentinel);

                    Il2CppSystem.Action<CircularMenuSelection.CircularMenuData> origCallback = __2;
                    __2 = Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<Il2CppSystem.Action<CircularMenuSelection.CircularMenuData>>(
                        (Action<CircularMenuSelection.CircularMenuData>)(selected =>
                        {
                            try
                            {
                                string marker = selected != null && selected.returnData != null
                                    ? selected.returnData.ToString()
                                    : null;
                                string itemText = selected != null ? selected.text : null;
                                if (string.Equals(marker, HqAcceptRadialSentinel, StringComparison.Ordinal) ||
                                    (!string.IsNullOrEmpty(itemText) && itemText.StartsWith("Accept HQ:", StringComparison.Ordinal)))
                                {
                                    AcceptPendingPlayerHqOrder();
                                    return;
                                }
                                if (string.Equals(marker, HqBalkRadialSentinel, StringComparison.Ordinal) ||
                                    string.Equals(itemText, "Balk HQ Order", StringComparison.Ordinal))
                                {
                                    BalkPendingPlayerHqOrder();
                                    return;
                                }
                            }
                            catch { }

                            origCallback?.Invoke(selected);
                        }));

                    __0 = expanded;
                }
                catch { }
            }
        }
    }
}
