using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace EasyRed2HumanAI
{
    public static partial class VeteranAndTacticsPatches
    {
        internal static bool IsActivelyHealingTeammate(Soldier soldier, out MedicHealingDestination healDest)
        {
            healDest = null;
            if (soldier == null || soldier.Pointer == IntPtr.Zero)
                return false;

            float now = Time.time;
            AiCore.SoldierFastHandles medH = AiCore.GetOrCreateSoldierHandles(soldier);
            if (medH != null &&
                now < medH.Goap.ActiveSyringeHealUntil &&
                (soldier.isInBodyAnimation || soldier.isInHandsAnimation))
            {
                return true;
            }

            if (soldier.targetDestination == null || soldier.targetDestination.Pointer == IntPtr.Zero)
                return false;

            healDest = soldier.targetDestination.TryCast<MedicHealingDestination>();
            if (healDest == null)
                return false;

            Soldier patient = healDest.soldierToHealUp;
            if (patient == null ||
                patient.Pointer == IntPtr.Zero ||
                patient.IsDead ||
                patient.IsOnVehicle() ||
                patient.IsCarried() ||
                patient.HasSurrended() ||
                healDest.coverReached ||
                !(patient.IsInjured || patient.isBleeding || patient.GetBleedValue() > 0.01f))
            {
                if (medH != null)
                {
                    medH.Goap.MedicHealStartTime = 0f;
                    medH.Goap.MedicHealTargetPtr = IntPtr.Zero;
                    medH.Goap.MedicCloseApproachSince = 0f;
                }
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
                float flatDistSq = flatDelta.sqrMagnitude;
                if (vertDelta > 4.5f && flatDistSq < 12f * 12f)
                {
                    if (medH != null)
                    {
                        medH.Goap.MedicHealStartTime = 0f;
                        medH.Goap.MedicHealTargetPtr = IntPtr.Zero;
                        medH.Goap.MedicCloseApproachSince = 0f;
                        medH.Goap.MedicUnreachablePatientPtr = patient.Pointer;
                        medH.Goap.MedicUnreachableUntil = now + 12.0f;
                    }
                    soldier.CoverPosition(null);
                    healDest = null;
                    return false;
                }

                // Guard against unreachable patients while allowing medics within 6m to finish their final step
                if (medH != null)
                {
                    if (medH.Goap.MedicHealTargetPtr != patient.Pointer || medH.Goap.MedicHealStartTime <= 0f)
                    {
                        medH.Goap.MedicHealTargetPtr = patient.Pointer;
                        medH.Goap.MedicHealStartTime = now;
                        medH.Goap.MedicCloseApproachSince = 0f;
                    }
                    else if (now - medH.Goap.MedicHealStartTime > 18.0f && flatDistSq > 6.0f * 6.0f)
                    {
                        medH.Goap.MedicHealStartTime = 0f;
                        medH.Goap.MedicHealTargetPtr = IntPtr.Zero;
                        medH.Goap.MedicCloseApproachSince = 0f;
                        medH.Goap.MedicUnreachablePatientPtr = patient.Pointer;
                        medH.Goap.MedicUnreachableUntil = now + 12.0f;
                        soldier.CoverPosition(null);
                        healDest = null;
                        return false;
                    }
                }
            }

            return true;
        }

        internal static void DriveMedicHealingApproach(
            SoldierAI ai,
            Soldier medic,
            AiCore.SoldierFastHandles medH,
            MedicHealingDestination healDest,
            float now)
        {
            if (ai == null ||
                medic == null ||
                healDest == null ||
                healDest.coverReached ||
                medic.isInBodyAnimation ||
                (medH != null && now < medH.Goap.ActiveSyringeHealUntil))
            {
                return;
            }

            Soldier patient = healDest.soldierToHealUp;
            if (patient == null ||
                patient.Pointer == IntPtr.Zero ||
                patient.IsDead ||
                !patient.IsInjured ||
                patient.IsCarried() ||
                patient.IsOnVehicle() ||
                patient.HasSurrended())
            {
                return;
            }

            Transform medTr = medH != null && medH.Tr != null ? medH.Tr : medic.transform;
            Transform patTr = patient.transform;
            if (medTr == null || patTr == null)
                return;

            if (medH != null && medH.MoveOverride.Kind != AiCore.BotMoveOverrideKind.None)
            {
                medH.MoveOverride = default;
            }

            Vector3 medicPos = medTr.position;
            Vector3 patPos = patTr.position;

            Vector3 moveDiff = ai.MoveDestination - patPos;
            if (moveDiff.sqrMagnitude > 0.25f * 0.25f)
            {
                ai.MoveDestination = patPos;
            }

            float dx = patPos.x - medicPos.x;
            float dy = patPos.y - medicPos.y;
            float dz = patPos.z - medicPos.z;
            float flatDistSq = dx * dx + dz * dz;
            float dist3DSq = flatDistSq + dy * dy;
            float vertDelta = Mathf.Abs(dy);

            if (flatDistSq <= 3.6f * 3.6f && vertDelta <= 2.4f)
            {
                if (medH != null && medH.Goap.MedicCloseApproachSince <= 0f)
                {
                    medH.Goap.MedicCloseApproachSince = now;
                }

                // Clear any concurrent weapon reload or bayonet/hands animation when stepping up to the casualty
                // so Soldier.UseSyrynge -> CanPlaySpecialAnim() (0x180742920) never aborts the syringe coroutine!
                if (dist3DSq <= 2.6f * 2.6f)
                {
                    if (medic.IsReloading)
                        medic.IsReloading = false;
                    if (medic.stopHandsAnim_end > now)
                        medic.stopHandsAnim_end = now - 0.01f;
                    if (medic.stopBodyAnim_end > now)
                        medic.stopBodyAnim_end = now - 0.01f;
                }

                float closeElapsed = medH != null && medH.Goap.MedicCloseApproachSince > 0f
                    ? (now - medH.Goap.MedicCloseApproachSince)
                    : 0f;

                if (dist3DSq <= 1.80f * 1.80f ||
                    (flatDistSq <= 2.45f * 2.45f && closeElapsed >= 0.70f))
                {
                    if (medic.IsReloading)
                        medic.IsReloading = false;
                    if (medic.stopHandsAnim_end > now)
                        medic.stopHandsAnim_end = now - 0.01f;
                    if (medic.stopBodyAnim_end > now)
                        medic.stopBodyAnim_end = now - 0.01f;

                    ai.moveDirectlyToward_end = 0f;
                    ai.Stop(true);
                    healDest.OnCoverReached(medic);
                    return;
                }

                // Bridge the 1.5m-3.6m CPathFinding final-node grid gap so medics never kneel frozen
                // just outside MedicHealingDestination.IsReached's 1.5m sphere ("stuck when diagnosing")!
                if (!ai.IsMoving() || closeElapsed >= 0.35f)
                {
                    ai.MoveDirectlyToward(patPos, 0.65f);
                }
            }
            else
            {
                if (medH != null)
                    medH.Goap.MedicCloseApproachSince = 0f;

                if (!ai.IsMoving() && !ai.HasPathRequest && AiCore.CanBotMoveAutonomously(medic))
                {
                    ai.moveDirectlyToward_end = 0f;
                    ai.UpdatePath();
                }
            }
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
                soldier.IsVoiceLocked ||
                soldier.IsTalking())
            {
                return;
            }

            float now = Time.time;
            if (now < soldier.skip_say_time || now < soldier.sayDelayedEnd)
                return;

            AiCore.SoldierFastHandles voiceH = AiCore.GetOrCreateSoldierHandles(soldier);
            if (voiceH != null && now < voiceH.Goap.NextSoldierVoiceTime)
                return;

            if (squad != null && squad.Pointer != IntPtr.Zero)
            {
                IntPtr sqPtr = squad.Pointer;
                if (_nextSquadVoiceTime.TryGetValue(sqPtr, out float nextSqVoice) && now < nextSqVoice)
                    return;
                _nextSquadVoiceTime[sqPtr] = now + 5.5f;
            }

            if (voiceH != null)
            {
                voiceH.Goap.NextSoldierVoiceTime = now + 9.0f;
            }

            try
            {
                // Local 3D spatialized audio: native Soldier.Say (0x18076144C) automatically checks Creature.IsLodded2()
                // against the active rendering camera—so first-person, vehicle, and Free/Spectator Camera (F) all hear
                // nearby bots naturally anywhere on the map with zero RPC cost!
                soldier.Say(clip, delay, -1, null);

                // In Multiplayer Co-op, only spend a Photon SyncSay packet when a remote human player's soldier is within earshot (<= 75m)!
                if (Photon.Pun.PhotonNetwork.InRoom &&
                    soldier.transform != null &&
                    AiCore.IsNearAnyRemotePlayer(soldier.transform.position, 75.0f) &&
                    AiCore.TryConsumeBotRpcBudget(0.50f))
                {
                    soldier.SyncSay(clip, -1);
                }
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

            // O(1) lookup from AiCore's flat 0.35s WorldSnapshot (eliminates nested O(N^2)/O(N^3) Squad.AllSquads scans!)
            if (AiCore.TryGetSquadSnapshot(sq.Pointer, out AiCore.SquadSnapshot snap))
                return snap.HasRadioOrRelay;

            if (sq.RadioRequestActiveAndConfirmed())
                return true;

            bool isPlayerSq = sq.IsPlayerInSquad();
            Soldier leader = sq.Leader;
            bool leaderAlive = leader != null && leader.IsAlive && leader.transform != null;

            if (leaderAlive && leader.IsOnVehicle())
            {
                Vehicle veh = leader.GetCurrentVehicle();
                if (veh != null && veh.TryCast<VehicleParachute>() == null)
                    return true;
            }

            Soldier rad = sq.GetRadioman();
            if (rad == null || !rad.IsAlive || rad.HasSurrended() || rad.transform == null)
                return false;

            if (!leaderAlive)
                return true;

            float maxDistSq = isPlayerSq ? (95f * 95f) : (60f * 60f);
            return (rad.transform.position - leader.transform.position).sqrMagnitude <= maxDistSq;
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

            AiCore.EnsureWorldSnapshot();
            int sqCount = AiCore.WorldSquadCount;
            if (sqCount == 0)
                return true;

            var squads = AiCore.WorldSquads;
            for (int i = 0; i < sqCount; i++)
            {
                ref AiCore.SquadSnapshot fSnap = ref squads[i];
                if (!fSnap.LeaderIsAlive || string.IsNullOrEmpty(fSnap.Faction) || fSnap.Faction == enemyFaction)
                    continue;

                if ((fSnap.RefPos - enemyPos).sqrMagnitude > 190f * 190f)
                    continue;

                if (fSnap.HasRadioOrRelay)
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
        private static readonly List<Squad> _reusableAiFootSquads = new List<Squad>(48);

        private static void ReleaseAiSquadExecutionStateIfActive(Squad sq)
        {
            if (sq == null || sq.Pointer == IntPtr.Zero)
                return;

            IntPtr sqPtr = sq.Pointer;
            _aiFollowColumnUntil.Remove(sqPtr);
            _aiSquadRetreatUntil.Remove(sqPtr);
            if (_aiCovertHoldFireSquads.Remove(sqPtr))
            {
                _aiCovertHoldFireExpireTime.Remove(sqPtr);
                if (sq.HoldFire)
                {
                    Soldier leader = sq.Leader;
                    bool canSync = !Photon.Pun.PhotonNetwork.InRoom ||
                                   (leader != null && (leader.soldierSyncher == null || leader.soldierSyncher.OwnSyncher));
                    if (canSync)
                    {
                        try { sq.SetHoldFireOrder(false, false, false, Photon.Pun.PhotonNetwork.InRoom); } catch { }
                    }
                }
            }
        }

        internal static void RunTeamNetGoapCoordinator(float now)
        {
            if (!VisionConfig.EnableTacticalGoapPlanner.Value)
                return;

            AiCore.EnsureWorldSnapshot();
            int sqCount = AiCore.WorldSquadCount;
            if (sqCount < 1)
                return;

            var connectedInfantrySquads = _reusableConnectedInfantrySquads;
            var activeVehicleAnvils = _reusableActiveVehicleAnvils;
            var aiFootSquads = _reusableAiFootSquads;
            connectedInfantrySquads.Clear();
            activeVehicleAnvils.Clear();
            aiFootSquads.Clear();
            bool issuedSquadCoverOrderThisTick = false;

            var squads = AiCore.WorldSquads;
            for (int sIdx = 0; sIdx < sqCount; sIdx++)
            {
                ref AiCore.SquadSnapshot sqSnap = ref squads[sIdx];
                if (!sqSnap.LeaderIsAlive)
                    continue;

                Squad sq = sqSnap.Ref;
                Soldier leader = sqSnap.Leader;
                if (sq == null || sq.Pointer == IntPtr.Zero || leader == null || !leader.IsAlive || leader.transform == null)
                    continue;

                // If the Human Player is the Squad Leader, never issue movement orders to their squad,
                // BUT if the Player's Squad is connected to the Radio Net and engaged in a firefight,
                // allow the Team Net to treat the Player's Squad as an Anvil and send a nearby AI Hammer squad to flank!
                if (leader.IsPlayer())
                {
                    ReleaseAiSquadExecutionStateIfActive(sq);
                    SquadBlackboard playerBoard = GetOrUpdateSquadBlackboard(sq, leader);
                    if (HasLiveRadioOrRelayLink(sq) && playerBoard.HasActiveThreat)
                    {
                        activeVehicleAnvils.Add(sq);
                    }
                    continue;
                }

                if (!AiCore.IsLocallyOwnedBot(leader))
                    continue;

                // Mounted Ground Combat Vehicles have onboard radios and can act as Heavy Anvils for nearby infantry!
                if (sqSnap.LeaderOnVehicle)
                {
                    ReleaseAiSquadExecutionStateIfActive(sq);
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

                aiFootSquads.Add(sq);
                SquadBlackboard board = GetOrUpdateSquadBlackboard(sq, leader);
                if (!board.IsConnectedToRadioNet)
                {
                    // Allow a squad that received a one-time spawn command (or a 45m relay command) to finish executing
                    // that directive before reverting to Disconnected_Autonomous!
                    if (now >= board.NetDirectiveExpireTime)
                    {
                        FactionCommandProfile disProfile = GetFactionCommandProfile(leader.faction);
                        AiCore.BattleTheaterSnapshot disTheater = AiCore.GetBattleTheaterSnapshot();

                        // Decentralized Command Doctrines (or cut-off urban street-block CQB <= 60m):
                        // Squad Leader retains local tactical initiative even when cut off from HQ radio!
                        bool allowLocalFlank = disProfile.AllowDisconnectedLocalFlank || disTheater.IsDenseUrbanMap;
                        float maxLocalFlankDist = disProfile.AllowDisconnectedLocalFlank ? 135f : 60f;
                        if (allowLocalFlank &&
                            board.HasActiveThreat &&
                            board.AliveMemberCount >= 4 &&
                            board.IsThreatBeingPinned(now) &&
                            (board.FlankLeftCover != null || board.FlankRightCover != null) &&
                            leader.Params != null && leader.Params.canGiveOrders && leader.Params.canMove)
                        {
                            Vector3 toThreat = board.ThreatPosition - leader.transform.position;
                            toThreat.y = 0f;
                            float dThreat = toThreat.magnitude;
                            if (dThreat >= 25f && dThreat <= maxLocalFlankDist)
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

                                if (!issuedSquadCoverOrderThisTick &&
                                    (!Photon.Pun.PhotonNetwork.InRoom || AiCore.TryConsumeBotRpcBudget(0.45f)))
                                {
                                    issuedSquadCoverOrderThisTick = true;
                                    try
                                    {
                                        sq.AttackFromSide(localFlankDir, board.ThreatPosition, 18.0f);
                                    }
                                    catch { }
                                }

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
                        if (!issuedSquadCoverOrderThisTick &&
                            !board.HasActiveThreat &&
                            leader.Params != null && leader.Params.canGiveOrders && leader.Params.canMove &&
                            TryFindNearestRadioHubPosition(sq, leader.transform.position, leader.faction, 95f, out Vector3 hubPos) &&
                            (!Photon.Pun.PhotonNetwork.InRoom || AiCore.TryConsumeBotRpcBudget(0.45f)))
                        {
                            issuedSquadCoverOrderThisTick = true;
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

                // Tier 3 TeamNet Sector Withdrawal: if a Radio-Connected frontline squad is shattered & heavily outnumbered
                // or facing close enemy armor with zero AT capability (outside desperate cap saves), authorize a sector fallback
                // and call radio artillery/tank support onto the enemy position!
                if (VisionConfig.EnableTacticalRetreatsAndRegroup.Value &&
                    board.HasActiveThreat &&
                    board.NetDirective != SquadNetDirective.Regroup_SectorWithdrawal &&
                    !IsSquadOnRetreatTask(sq) &&
                    !(board.IsDefendingObjective && board.ObjectiveCaptureProgress >= 0.55f) &&
                    !(board.IsInvaderFaction && board.TicketRatio01 <= 0.18f) &&
                    (!_nextAiSquadRetreatTime.TryGetValue(sq.Pointer, out float nextNetRetT) || now >= nextNetRetT))
                {
                    float dNetThreat = Vector3.Distance(leader.transform.position, board.ThreatPosition);
                    Vehicle netThreatTank = (board.ThreatIsArmor && board.PrimaryTarget != null)
                        ? board.PrimaryTarget.TryCast<Vehicle>()
                        : null;
                    bool uncounterableCloseArmor =
                        board.ThreatIsArmor &&
                        !board.SquadHasAntiTank &&
                        dNetThreat <= 44.0f &&
                        (netThreatTank == null || IsTankThreateningPosition(netThreatTank, leader.transform.position, 52.0f));
                    bool shatteredOutnumbered = board.AliveMemberCount <= 3 &&
                                                dNetThreat <= 68.0f &&
                                                board.NearbyEnemyCount42m >= (board.NearbyFriendlyCount42m * 2 + 3);
                    if (uncounterableCloseArmor || shatteredOutnumbered)
                    {
                        board.NetDirective = SquadNetDirective.Regroup_SectorWithdrawal;
                        board.NetDirectiveExpireTime = now + 18.0f;
                        board.NetDirectiveTargetPos = board.ThreatPosition;
                        _squadBlackboards[sq.Pointer] = board;
                        TryTriggerSquadRadioSupport(sq, leader);
                        if (VisionConfig.DebugLogging.Value)
                        {
                            AiCore.LogDebug(
                                "TeamNetRetreat",
                                $"{leader.faction} Radio Net authorized Sector Withdrawal for '{sq.squadName}' (alive:{board.AliveMemberCount}, armor:{uncounterableCloseArmor}, enemies42m:{board.NearbyEnemyCount42m}).",
                                3.0f);
                        }
                    }
                }

                connectedInfantrySquads.Add(sq);
            }

            int infCount = connectedInfantrySquads.Count;
            if (infCount > 0)
            {
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
                    if (vehDist < 18f || vehDist > 180f)
                        continue;

                    Vector3 axisDir = vehToThreat / vehDist;
                    Vector3 perpRight = new Vector3(axisDir.z, 0f, -axisDir.x);
                    IntPtr vehTaskPtr = vehSq.currentTask != null ? vehSq.currentTask.Pointer : IntPtr.Zero;

                    Squad bestHammerSq = FindBestAvailableHammerSquad(connectedInfantrySquads, -1, vehSq.Pointer, vehLeader.faction, vehTaskPtr, threatPos, 115f, now);
                    if (bestHammerSq != null)
                    {
                        IssueHammerFlankDirective(bestHammerSq, vehPos, threatPos, axisDir, perpRight, now, $"Tank/Vehicle Anvil ('{vehSq.squadName}')");
                        if (vehSq.IsPlayerInSquad() && HasLiveRadioOrRelayLink(vehSq))
                        {
                            string infLabel = AiCore.FormatInfantrySupportCallsign(bestHammerSq, vehPos);
                            AiCore.TryShowLocalPlayerRadioHint(
                                vehSq,
                                $"Radio Net: {infLabel} flanking enemy contact to support your position",
                                requireLeader: false,
                                minInterval: 20.0f);
                        }
                    }
                    else
                    {
                        TryOfferPlayerHammerFlankOrder(vehLeader.faction, vehSq.Pointer, vehTaskPtr, vehPos, threatPos, axisDir, perpRight, now);
                    }
                }

                // =================================================================
                // 1B. Infantry Anvil + Infantry Hammer (or Single-Squad Pin & Flank)
                // =================================================================
                for (int i = 0; i < infCount; i++)
                {
                    Squad anvilSq = connectedInfantrySquads[i];
                    if (IsSquadActivelyRetreating(anvilSq.Pointer, now))
                        continue;

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
                    if (anvilDist < 18f || anvilDist > 160f)
                        continue;

                    Vector3 axisDir = anvilToThreat / anvilDist;
                    Vector3 perpRight = new Vector3(axisDir.z, 0f, -axisDir.x);
                    IntPtr anvilTaskPtr = anvilSq.currentTask != null ? anvilSq.currentTask.Pointer : IntPtr.Zero;

                    // Find best nearby Radio-Connected friendly squad within 115m on the same objective/sector to act as the HAMMER
                    Squad bestHammerSq = FindBestAvailableHammerSquad(connectedInfantrySquads, i, anvilSq.Pointer, anvilLeader.faction, anvilTaskPtr, threatPos, 115f, now);

                    if (bestHammerSq != null)
                    {
                        // Amphibious Beach Landing Gate: never lock invading troops standing at the surf/waterline into a static
                        // Anvil base-of-fire hold on open sand — keep beach invaders advancing/flanking inland under smoke!
                        AiCore.BattleTheaterSnapshot beachCheck = AiCore.GetBattleTheaterSnapshot();
                        bool onExposedBeachWaterline = beachCheck.IsAmphibiousBeachMap &&
                                                       anvilBoard.IsInvaderFaction &&
                                                       AiCore.TryGetActiveWaterLevel(out float bWl) &&
                                                       anvilPos.y <= bWl + 4.2f;
                        if (!onExposedBeachWaterline)
                        {
                            anvilBoard.SuggestedNetDirective = SquadNetDirective.Anvil_PinAndCallSupport;
                            anvilBoard.SuggestedDirectiveExpireTime = now + 24.0f;
                            anvilBoard.SuggestedDirectiveTargetPos = threatPos;
                            anvilBoard.SuggestedPincerTaskPtr = IntPtr.Zero;
                            _squadBlackboards[anvilSq.Pointer] = anvilBoard;
                            ConsumeInitialHqSpawnBriefingIfNoRadio(anvilSq);
                        }

                        // Post HAMMER advisory suggestion on Squad B (evaluated by Squad B's Leader)
                        IssueHammerFlankDirective(bestHammerSq, anvilPos, threatPos, axisDir, perpRight, now, $"Infantry Anvil ('{anvilSq.squadName}')");
                        if (anvilSq.IsPlayerInSquad() && HasLiveRadioOrRelayLink(anvilSq))
                        {
                            string infLabel = AiCore.FormatInfantrySupportCallsign(bestHammerSq, anvilPos);
                            AiCore.TryShowLocalPlayerRadioHint(
                                anvilSq,
                                $"Radio Net: {infLabel} flanking enemy contact to support your squad",
                                requireLeader: false,
                                minInterval: 20.0f);
                        }
                    }
                    else
                    {
                        TryOfferPlayerHammerFlankOrder(anvilLeader.faction, anvilSq.Pointer, anvilTaskPtr, anvilPos, threatPos, axisDir, perpRight, now);
                        if (anvilBoard.AliveMemberCount >= 4 &&
                            anvilBoard.IsThreatBeingPinned(now) &&
                            (anvilBoard.FlankLeftCover != null || anvilBoard.FlankRightCover != null) &&
                            anvilLeader.Params != null && anvilLeader.Params.canGiveOrders && anvilLeader.Params.canMove)
                        {
                            // Single-Squad Self-Pin & Flank suggestion: MG gunner has the enemy pinned and lateral cover exists.
                            anvilBoard.SuggestedNetDirective = SquadNetDirective.Hammer_WideFlankEnvelopment;
                            anvilBoard.SuggestedDirectiveExpireTime = now + 22.0f;
                            anvilBoard.SuggestedDirectiveTargetPos = threatPos;
                            anvilBoard.SuggestedPincerTaskPtr = IntPtr.Zero;
                            _squadBlackboards[anvilSq.Pointer] = anvilBoard;
                            ConsumeInitialHqSpawnBriefingIfNoRadio(anvilSq);
                        }
                    }
                }

                // =================================================================
                // 2. Multi-Squad Objective Pincer (Advisory HQ Suggestion -> NCO Decides Execution!)
                // =================================================================
                _reusablePrimarySquadByTask.Clear();
                _reusablePincerCountByTask.Clear();
                var primarySquadByTask = _reusablePrimarySquadByTask;
                var pincerCountByTask = _reusablePincerCountByTask;

                for (int i = 0; i < infCount; i++)
                {
                    Squad sq = connectedInfantrySquads[i];
                    if (sq.currentTask == null || sq.currentTask.Pointer == IntPtr.Zero || IsSquadActivelyRetreating(sq.Pointer, now))
                        continue;

                    if (_garrisonAssignedObjectiveBySquad.ContainsKey(sq.Pointer))
                        continue;

                    if (_squadBlackboards.TryGetValue(sq.Pointer, out SquadBlackboard board) &&
                        (board.IsDefendingObjective || board.HasActiveThreat || now < board.NetDirectiveExpireTime || now < board.SuggestedDirectiveExpireTime))
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

                    // Post advisory pincer maneuver to Squad Leader instead of HQ directly snapping sq.AttackFromSide!
                    board.SuggestedNetDirective = SquadNetDirective.Hammer_WideFlankEnvelopment;
                    board.SuggestedDirectiveExpireTime = now + 28.0f;
                    board.SuggestedDirectiveTargetPos = taskPos;
                    board.SuggestedPincerFlankDir = pincerFlankDir;
                    board.SuggestedPincerTaskPtr = taskPtr;
                    _squadBlackboards[sq.Pointer] = board;
                    ConsumeInitialHqSpawnBriefingIfNoRadio(sq);
                }
                _reusablePrimarySquadByTask.Clear();
                _reusablePincerCountByTask.Clear();
            }

            // =================================================================
            // 3. Tier 2 Squad Leader Execution GOAP (Charge! / Covert Hold-Fire / Follow Column)
            //    Evaluated for all AI foot Squad Leaders (both Radio-Connected and Disconnected)
            //    so each Squad Leader translates TeamNet directives & local battlefield state into
            //    native squad orders based on personality, doctrine, cap urgency, tickets & smoke.
            // =================================================================
            int footCount = aiFootSquads.Count;
            for (int i = 0; i < footCount; i++)
            {
                Squad footSq = aiFootSquads[i];
                if (footSq == null || footSq.Pointer == IntPtr.Zero)
                    continue;

                Soldier footLeader = footSq.Leader;
                if (footLeader == null || !footLeader.IsAlive || footLeader.transform == null)
                    continue;

                if (!_squadBlackboards.TryGetValue(footSq.Pointer, out SquadBlackboard footBoard))
                    footBoard = GetOrUpdateSquadBlackboard(footSq, footLeader);

                EvaluateSquadLeaderExecutionOrders(footSq, footLeader, ref footBoard, now, ref issuedSquadCoverOrderThisTick);
            }
            aiFootSquads.Clear();
        }

        private static void TryOfferPlayerHammerFlankOrder(
            string anvilFaction,
            IntPtr anvilSqPtr,
            IntPtr anvilTaskPtr,
            Vector3 anvilPos,
            Vector3 threatPos,
            Vector3 axisDir,
            Vector3 perpRight,
            float now)
        {
            // In Co-op Host mode, also send a non-intrusive advisory HQ radio chat message to any remote unmodded
            // Squad Leader within range (never touching their SyncSqOrder or mentioning modded-only [Radial] menus).
            if (AiCore.IsInMultiplayerRoom() && AiCore.IsMasterClient())
            {
                AiCore.EnsureWorldSnapshot();
                int sqCount = AiCore.WorldSquadCount;
                var squads = AiCore.WorldSquads;
                for (int i = 0; i < sqCount; i++)
                {
                    ref AiCore.SquadSnapshot snap = ref squads[i];
                    if (!snap.LeaderIsAlive || snap.Ptr == IntPtr.Zero || snap.Ptr == anvilSqPtr || snap.LeaderOnVehicle)
                        continue;
                    if (!AiCore.AreSameSideFactions(anvilFaction, snap.Faction))
                        continue;

                    Soldier remLeader = snap.Leader;
                    Squad remSq = snap.Ref;
                    if (remLeader == null || !remLeader.IsPlayer() || remLeader.transform == null || remSq == null || !HasLiveRadioOrRelayLink(remSq))
                        continue;

                    SyncSoldier syn = remLeader.soldierSyncher;
                    if (syn == null || syn.OwnSyncher)
                        continue;

                    Photon.Realtime.Player remotePlayer = syn.Controller ?? syn.Owner;
                    if (remotePlayer == null || remotePlayer.IsLocal || AiCore.IsPhotonPlayerConfirmedModded(remotePlayer))
                        continue;

                    Vector3 remLeaderPos = remLeader.transform.position;
                    float rdx = threatPos.x - remLeaderPos.x;
                    float rdz = threatPos.z - remLeaderPos.z;
                    float rdSq = rdx * rdx + rdz * rdz;
                    if (rdSq < 18.0f * 18.0f || rdSq > 125.0f * 125.0f)
                        continue;

                    string remBearing = AiCore.FormatRelativeBearingAndDistance(remLeaderPos, threatPos);
                    AiCore.SendTargetedVanillaChatMessage(
                        remotePlayer,
                        $"HQ Radio ({AiCore.FormatSquadPhoneticCallsign(remSq)}): Enemy contact {remBearing} \u2014 flanking opportunity!",
                        minPlayerInterval: 35.0f);
                }
            }

            Soldier localPlayer = Soldier.CurrentControlledSoldierOrNull();
            if (localPlayer == null || !localPlayer.IsAlive || localPlayer.IsOnVehicle() || localPlayer.transform == null)
                return;

            if (!AiCore.AreSameSideFactions(anvilFaction, localPlayer.faction))
                return;

            Squad playerSq = localPlayer.joinedSquad;
            if (playerSq == null || playerSq.Pointer == IntPtr.Zero || playerSq.Pointer == anvilSqPtr)
                return;

            if (playerSq.Leader == null || playerSq.Leader.Pointer != localPlayer.Pointer)
                return;

            if (!HasLiveRadioOrRelayLink(playerSq) ||
                IsSquadActivelyRetreating(playerSq.Pointer, now) ||
                IsFlankBalkedByPlayer(threatPos, now))
            {
                return;
            }

            if (TryGetActivePendingPlayerHqOrder(out PlayerPendingHqOrderState existing) &&
                existing.Kind == PendingHqOrderKind.ObjectiveTask)
            {
                return;
            }

            Vector3 playerPos = localPlayer.transform.position;
            float dx = threatPos.x - playerPos.x;
            float dz = threatPos.z - playerPos.z;
            float dSq = dx * dx + dz * dz;
            if (dSq < 18.0f * 18.0f || dSq > 125.0f * 125.0f)
                return;

            if (playerSq.currentTask != null &&
                playerSq.currentTask.Pointer != IntPtr.Zero &&
                playerSq.currentTask.Pointer != anvilTaskPtr &&
                dSq > 75.0f * 75.0f)
            {
                return;
            }

            float side = Vector3.Dot(playerPos - anvilPos, perpRight) >= 0f ? 1f : -1f;
            Vector3 flankDir = (axisDir * 0.68f + perpRight * (side * 0.73f)).normalized;
            string bearing = AiCore.FormatRelativeBearingAndDistance(playerPos, threatPos);

            PostPendingPlayerHqOrder(
                playerSq,
                PendingHqOrderKind.TacticalFlank,
                targetObj: null,
                targetTask: null,
                worldPos: threatPos,
                flankDir: flankDir,
                isAttackObjective: true,
                shortActionLabel: $"Flank ({bearing})",
                toastLabel: $"Flank Contact ({bearing})",
                mapBadgeText: "[HQ]",
                durationSeconds: 35.0f);
        }

        private static void EvaluateSquadLeaderExecutionOrders(
            Squad sq,
            Soldier leader,
            ref SquadBlackboard board,
            float now,
            ref bool issuedSquadCoverOrderThisTick)
        {
            IntPtr sqPtr = sq.Pointer;
            Vector3 leaderPos = leader.transform.position;
            float leaderSupp = leader.GetSuppressionValue();
            float distToThreat = board.HasActiveThreat ? Vector3.Distance(leaderPos, board.ThreatPosition) : 9999f;
            float distToObj = board.HasActiveObjectiveTask ? Vector3.Distance(leaderPos, board.ObjectivePosition) : 9999f;
            bool syncOnline = Photon.Pun.PhotonNetwork.InRoom;

            // A. Auto-Release AI Covert Hold-Fire immediately when discovered, suppressed, or entering close assault range.
            //    Strictly scoped to _aiCovertHoldFireSquads so mission-scripted Lua holdFire orders are NEVER touched!
            if (_aiCovertHoldFireSquads.Contains(sqPtr))
            {
                if (!sq.HoldFire)
                {
                    _aiCovertHoldFireSquads.Remove(sqPtr);
                    _aiCovertHoldFireExpireTime.Remove(sqPtr);
                }
                else
                {
                    bool expired = !_aiCovertHoldFireExpireTime.TryGetValue(sqPtr, out float expT) || now >= expT;
                    bool compromised = sq.IsEnemyAwareOfThisSquad ||
                                       leaderSupp >= 8.0f ||
                                       leader.isBleeding ||
                                       distToThreat <= 36.0f ||
                                       distToObj <= 36.0f ||
                                       sq.InChargeTime() ||
                                       (board.IsDefendingObjective && board.ObjectiveCaptureProgress >= 0.35f);

                    if ((expired || compromised) && (!syncOnline || AiCore.TryConsumeBotRpcBudget(0.20f)))
                    {
                        _aiCovertHoldFireSquads.Remove(sqPtr);
                        _aiCovertHoldFireExpireTime.Remove(sqPtr);
                        try
                        {
                            sq.SetHoldFireOrder(false, false, compromised && !expired, syncOnline);
                        }
                        catch { }
                    }
                }
            }

            // B. Auto-Release AI Follow Column March as soon as the squad nears the frontline (<=78m) or takes contact
            //    so vanilla SquadLeaderRoutine deploys the squad into tactical cover immediately.
            if (_aiFollowColumnUntil.TryGetValue(sqPtr, out float followUntil))
            {
                if (sq.order != Order.follow)
                {
                    _aiFollowColumnUntil.Remove(sqPtr);
                }
                else if (now >= followUntil ||
                         board.HasActiveThreat ||
                         sq.IsEnemyAwareOfThisSquad ||
                         leaderSupp >= 4.0f ||
                         leader.isBleeding ||
                         distToObj <= 78.0f)
                {
                    _aiFollowColumnUntil.Remove(sqPtr);
                    if (sq.timed_order_end > now)
                        sq.timed_order_end = 0f;
                    if (sq.currentTask != null && (!syncOnline || AiCore.TryConsumeBotRpcBudget(0.35f)))
                    {
                        try
                        {
                            sq.Leader_OrderAttackCurrentTask(leader);
                            issuedSquadCoverOrderThisTick = true;
                        }
                        catch { }
                    }
                }
            }

            // B2. Auto-Release AI Fighting Withdrawal once rally window completes or squad is out of immediate danger
            if (_aiSquadRetreatUntil.TryGetValue(sqPtr, out float sqRetUntil))
            {
                if (now >= sqRetUntil || (!board.HasActiveThreat && leaderSupp < 5.0f))
                {
                    _aiSquadRetreatUntil.Remove(sqPtr);
                }
                else
                {
                    return;
                }
            }

            FactionCommandProfile profile = GetFactionCommandProfile(leader.faction);
            SoldierPersonality leaderPers = GetPersonality(leader);
            bool isTrueGyokusai = IsTrueGyokusaiOrBanzaiCondition(in profile, sq, in board);

            // B3. Abort Suicidal AI Charge Early if 2+ Squadmates Are Cut Down Mid-Charge!
            if (_aiOrderedChargeSquads.Contains(sqPtr))
            {
                if (!sq.InChargeTime())
                {
                    _aiOrderedChargeSquads.Remove(sqPtr);
                    _aiChargeBaselineDeaths.Remove(sqPtr);
                }
                else if (!sq.isChargeSquad && !sq.IsPlayerInSquad() && !isTrueGyokusai)
                {
                    float baseDeaths = _aiChargeBaselineDeaths.TryGetValue(sqPtr, out float bd0) ? bd0 : 0f;
                    float chargeDeaths = board.DecayedRecentDeaths - baseDeaths;
                    float abortMinDist = profile.AllowGyokusaiDesperation ? 20.0f : 18.0f;
                    bool mowedDown = chargeDeaths >= 1.65f ||
                                     (leaderSupp >= 68.0f && distToObj > abortMinDist && distToThreat > abortMinDist);
                    if (mowedDown && (!syncOnline || AiCore.TryConsumeBotRpcBudget(0.45f)))
                    {
                        _aiOrderedChargeSquads.Remove(sqPtr);
                        _aiChargeBaselineDeaths.Remove(sqPtr);
                        sq.timed_order_end = 0f;
                        try { sq.Leader_OrderAttackCurrentTask(leader); } catch { }
                        TrySayTacticalVoice(leader, sq, VoiceManager.VoiceClip.keepYourHeadDown, 0.08f);
                        SquadLeaderBalksCount++;
                        BotActionTrace.RecordEvent(leader, BotTraceEventKind.LeaderBalk, "abort_charge_casualties", distToThreat);
                        issuedSquadCoverOrderThisTick = true;
                        if (VisionConfig.DebugLogging.Value)
                        {
                            AiCore.LogDebug(
                                "SquadChargeAbort",
                                $"{leader.faction} {leaderPers} aborted Charge! on '{sq.squadName}' after heavy casualties (deaths:{chargeDeaths:F1}, supp:{leaderSupp:F0}).",
                                3.0f);
                        }
                        return;
                    }
                }
            }

            // B4. Autonomous NCO Evaluation of HQ Advisory Suggestion (Obey, Adapt, or Balk/Refuse Suicidal Push!)
            if (board.IsConnectedToRadioNet &&
                now < board.SuggestedDirectiveExpireTime &&
                board.SuggestedNetDirective != SquadNetDirective.Disconnected_Autonomous)
            {
                SquadNetDirective suggested = board.SuggestedNetDirective;
                float sugExpire = board.SuggestedDirectiveExpireTime;
                Vector3 sugTarget = board.SuggestedDirectiveTargetPos;
                Vector3 sugPincerDir = board.SuggestedPincerFlankDir;
                IntPtr sugTaskPtr = board.SuggestedPincerTaskPtr;
                board.SuggestedDirectiveExpireTime = 0f;

                if (suggested == SquadNetDirective.Hammer_WideFlankEnvelopment)
                {
                    float balkDeathThresh = leaderPers == SoldierPersonality.Leader_Spearhead
                        ? 2.60f
                        : (leaderPers == SoldierPersonality.Leader_OldGuard ? 1.35f : 1.85f);
                    float balkSuppThresh = leaderPers == SoldierPersonality.Leader_Spearhead
                        ? 58.0f
                        : (leaderPers == SoldierPersonality.Leader_OldGuard ? 34.0f : 44.0f);
                    bool hasSmokeUp = now < board.SmokeActiveUntil;
                    bool hasActiveOrImminentThreat = (board.HasActiveThreat && distToThreat <= 165.0f) ||
                                                     leaderSupp >= 12.0f ||
                                                     sq.IsEnemyAwareOfThisSquad;

                    bool ncoBalks = !isTrueGyokusai &&
                                    hasActiveOrImminentThreat &&
                                    (board.AliveMemberCount <= 2 ||
                                     board.DecayedRecentDeaths >= balkDeathThresh ||
                                     (leaderSupp >= balkSuppThresh && !hasSmokeUp));

                    if (!hasActiveOrImminentThreat && board.AliveMemberCount <= 2)
                    {
                        // Tiny unengaged 1-2 man remnant quietly stays on standard approach without fake balk/support calls
                        board.NetDirective = SquadNetDirective.Connected_StandardObjective;
                        board.NetDirectiveExpireTime = now + 14.0f;
                        _squadBlackboards[sqPtr] = board;
                    }
                    else if (ncoBalks)
                    {
                        // NCO refuses HQ's flank/push order to save his men: converts locally into Anvil base-of-fire & requests fire support!
                        board.NetDirective = SquadNetDirective.Anvil_PinAndCallSupport;
                        board.NetDirectiveExpireTime = now + 18.0f;
                        board.NetDirectiveTargetPos = board.HasActiveThreat ? board.ThreatPosition : sugTarget;
                        _squadBlackboards[sqPtr] = board;

                        TrySayTacticalVoice(leader, sq, VoiceManager.VoiceClip.keepYourHeadDown, 0.15f);
                        TryTriggerSquadRadioSupport(sq, leader);
                        SquadLeaderBalksCount++;
                        BotActionTrace.RecordEvent(leader, BotTraceEventKind.LeaderBalk, "refused_hq_flank", distToThreat);
                        if (VisionConfig.DebugLogging.Value)
                        {
                            AiCore.LogDebug(
                                "NcoBalked",
                                $"{leader.faction} {leaderPers} ('{sq.squadName}') refused HQ flank suggestion (deaths:{board.DecayedRecentDeaths:F1}, supp:{leaderSupp:F0}) -> holding cover & calling support.",
                                3.0f);
                        }
                    }
                    else
                    {
                        // NCO accepts HQ's flank/pincer suggestion and chooses HOW to execute (smoke, stealth, or AttackFromSide)
                        board.NetDirective = SquadNetDirective.Hammer_WideFlankEnvelopment;
                        board.NetDirectiveExpireTime = sugExpire;
                        board.NetDirectiveTargetPos = sugTarget;
                        _squadBlackboards[sqPtr] = board;

                        if (sugTaskPtr != IntPtr.Zero &&
                            sq.currentTask != null &&
                            sq.currentTask.Pointer == sugTaskPtr &&
                            (!_pincerOrderedTaskBySquad.TryGetValue(sqPtr, out IntPtr prevPincerTask) || prevPincerTask != sugTaskPtr) &&
                            !issuedSquadCoverOrderThisTick &&
                            (!syncOnline || AiCore.TryConsumeBotRpcBudget(0.45f)))
                        {
                            issuedSquadCoverOrderThisTick = true;
                            _pincerOrderedTaskBySquad[sqPtr] = sugTaskPtr;
                            try
                            {
                                sq.AttackFromSide(sugPincerDir, sugTarget, 22.0f);
                                if (sq.timed_order_end < sugExpire)
                                    sq.timed_order_end = sugExpire;
                            }
                            catch { }
                        }

                        if (board.HasActiveThreat && distToThreat <= 42.0f && !hasSmokeUp && leaderPers != SoldierPersonality.Leader_Spearhead)
                        {
                            TryPopSquadRetreatSmoke(sq, leader, ref board, now);
                            _squadBlackboards[sqPtr] = board;
                        }

                        TrySayTacticalVoice(leader, sq, VoiceManager.VoiceClip.attackThere, 0.22f);
                        TeamNetAnvilHammerCount++;
                        GoapPlansExecutedCount++;
                    }
                }
                else
                {
                    board.NetDirective = suggested;
                    board.NetDirectiveExpireTime = sugExpire;
                    board.NetDirectiveTargetPos = sugTarget;
                    _squadBlackboards[sqPtr] = board;

                    if (suggested == SquadNetDirective.Anvil_PinAndCallSupport)
                    {
                        TrySayTacticalVoice(leader, sq, VoiceManager.VoiceClip.coveringFire, 0.15f);
                        TryTriggerSquadRadioSupport(sq, leader);
                    }
                }
            }

            // B4. Attacking Squad Stall Watchdog: if an attacking AI foot squad makes < 3.5m progress for >= 8.5s
            //     while > 32m from its objective and not pinned, release stale cover locks and either re-issue attack
            //     or contour around blocking cliffs/hillsides via a temporary lateral HoldArea waypoint!
            if (!issuedSquadCoverOrderThisTick &&
                !board.IsDefendingObjective &&
                board.HasActiveObjectiveTask &&
                distToObj > 32.0f &&
                leaderSupp < 35.0f &&
                !leader.IsOnVehicle() &&
                !leader.HasSurrended() &&
                !leader.IsInjured &&
                !IsSquadOnRetreatTask(sq) &&
                !_squadOrderedToVehicle.ContainsKey(sqPtr))
            {
                if (board.StallAnchorTime <= 0f || (board.SquadCenter - board.StallAnchorCenter).sqrMagnitude > 3.5f * 3.5f)
                {
                    board.StallAnchorCenter = board.SquadCenter;
                    board.StallAnchorTime = now;
                    board.ConsecutiveStallCount = 0;
                    _squadBlackboards[sqPtr] = board;
                }
                else if ((now - board.StallAnchorTime) >= 8.5f &&
                         now >= board.NextSquadUnstallTime &&
                         (!syncOnline || AiCore.TryConsumeBotRpcBudget(0.45f)))
                {
                    board.ConsecutiveStallCount++;
                    board.StallAnchorCenter = board.SquadCenter;
                    board.StallAnchorTime = now;
                    board.NextSquadUnstallTime = now + 13.0f;
                    _squadBlackboards[sqPtr] = board;
                    try
                    {
                        AllowExplicitSquadUnstallRelease = true;
                        try { sq.OrderLeaveAllVehiclesAndCovers(false, true, false); }
                        finally { AllowExplicitSquadUnstallRelease = false; }
                        if (board.ConsecutiveStallCount >= 2 &&
                            TryFindSquadHillsideContourPoint(leaderPos, board.ObjectivePosition, sqPtr, board.ConsecutiveStallCount, out Vector3 detourPt))
                        {
                            sq.HoldArea(detourPt, 12.0f, false);
                            sq.timed_order_end = now + 11.0f;
                        }
                        else
                        {
                            sq.Leader_OrderAttackCurrentTask(leader);
                        }
                        issuedSquadCoverOrderThisTick = true;
                    }
                    catch { }
                    return;
                }
            }

            // B5. Garrison Hold-Fire Cleanup & Flank-Shift Cover Re-Orientation (~10.5s cadence when threat shifts > 55 deg):
            if (_garrisonAssignedObjectiveBySquad.ContainsKey(sqPtr) &&
                sq.HoldFire &&
                !_aiCovertHoldFireSquads.Contains(sqPtr) &&
                (!syncOnline || AiCore.TryConsumeBotRpcBudget(0.25f)))
            {
                try { sq.SetHoldFireOrder(false, false, false, syncOnline); } catch { }
            }

            if (!issuedSquadCoverOrderThisTick &&
                sq.order == Order.defend &&
                !sq.InChargeTime() &&
                !leader.IsOnVehicle() &&
                !leader.HasSurrended() &&
                !leader.IsInjured &&
                !IsSquadOnRetreatTask(sq) &&
                !_squadOrderedToVehicle.ContainsKey(sqPtr) &&
                now >= board.NextCoverSectorReorientTime &&
                (sq.IsInsideMoveOrderArea(leaderPos) || (board.HasActiveObjectiveTask && distToObj <= 38.0f)))
            {
                Vector3 activeSectorDir = Vector3.zero;
                if (board.HasActiveThreat)
                {
                    activeSectorDir = board.ThreatDirection;
                }
                else if (board.LastKnownThreatTime > 0f && (now - board.LastKnownThreatTime) <= 14.0f)
                {
                    Vector3 toLast = board.LastKnownThreatPos - board.SquadCenter;
                    toLast.y = 0f;
                    if (toLast.sqrMagnitude > 1.0f)
                        activeSectorDir = toLast.normalized;
                }
                else if (VisionPatches.TryGetDominantEnemyGunfireDirection(board.SquadCenter, leader.faction, 175.0f, 6.0f, out Vector3 gfDir, out _))
                {
                    activeSectorDir = gfDir;
                }

                activeSectorDir.y = 0f;
                Vector3 curFormDir = sq.formationDirection;
                curFormDir.y = 0f;
                if (activeSectorDir.sqrMagnitude > 0.01f &&
                    (curFormDir.sqrMagnitude < 0.01f || Vector3.Angle(curFormDir.normalized, activeSectorDir.normalized) > 55.0f) &&
                    (!syncOnline || AiCore.TryConsumeBotRpcBudget(0.45f)))
                {
                    board.NextCoverSectorReorientTime = now + 10.5f;
                    _squadBlackboards[sqPtr] = board;
                    sq.formationDirection = activeSectorDir.normalized;
                    Vector3 holdAnchor = sq.moveOrderPosition.sqrMagnitude > 0.01f
                        ? sq.moveOrderPosition
                        : (board.HasActiveObjectiveTask ? board.ObjectivePosition : board.SquadCenter);
                    float holdRad = sq.moveOrderRadius > 1.0f ? sq.moveOrderRadius : 16.0f;
                    try
                    {
                        sq.SendUnitsToCovers(holdAnchor, Mathf.Max(10.0f, holdRad));
                        issuedSquadCoverOrderThisTick = true;
                    }
                    catch { }
                    return;
                }
            }

            // C. Evaluate New Tier 2 Squad Leader Execution Order (Baseline 1.00f = Standard Cover-to-Cover Assault)
            if (issuedSquadCoverOrderThisTick)
                return;

            if (leader.Params != null && (!leader.Params.canGiveOrders || !leader.Params.canMove))
                return;

            if (leader.HasSurrended() || leader.IsInjured || IsSquadOnRetreatTask(sq) || _squadOrderedToVehicle.ContainsKey(sqPtr))
                return;

            float bestCost = 0.98f;
            int bestAction = 0; // 1 = Charge, 2 = Covert HoldFire, 3 = Follow Column, 4 = Fighting Withdrawal / Regroup
            Vector3 chargeTarget = Vector3.zero;
            Vector3 retreatRallyPos = Vector3.zero;

            // 1. Evaluate Charge! (sq.Charge) — Short-Range Shock Breach (<= 26m non-fanatic, requires tactical advantage or desperate cap)
            if (!sq.InChargeTime() &&
                board.AliveMemberCount >= 3 &&
                (!_nextAiSquadChargeTime.TryGetValue(sqPtr, out float nextChargeT) || now >= nextChargeT) &&
                !AiCore.IsOnBridgeOrNarrowCrossing(leaderPos))
            {
                AiCore.BattleTheaterSnapshot chTheater = AiCore.GetBattleTheaterSnapshot();
                bool isDesperateOvertime = (board.HasActiveObjectiveTask && board.IsDefendingObjective && board.ObjectiveCaptureProgress >= 0.62f) ||
                                           (board.IsInvaderFaction && board.TicketRatio01 <= 0.16f && board.HasActiveObjectiveTask && !board.IsDefendingObjective);
                float baseMaxObjCharge = profile.AllowGyokusaiDesperation ? 28.0f : 26.0f;
                float baseMaxThreatCharge = profile.AllowGyokusaiDesperation ? 26.0f : 24.0f;
                if (chTheater.IsOpenDesertOrSteppeMap && !isTrueGyokusai)
                {
                    baseMaxObjCharge = 22.0f;
                    baseMaxThreatCharge = 20.0f;
                }

                float maxObjChargeDist = isTrueGyokusai ? 42.0f : (isDesperateOvertime ? 34.0f : baseMaxObjCharge);
                float maxThreatChargeDist = isTrueGyokusai ? 36.0f : baseMaxThreatCharge;

                bool hasChargeTarget = false;
                if (board.HasActiveObjectiveTask && distToObj >= 12.0f && distToObj <= maxObjChargeDist)
                {
                    chargeTarget = board.ObjectivePosition;
                    hasChargeTarget = !board.ThreatIsArmor || distToObj <= 24.0f;
                }
                else if (board.HasActiveThreat && !board.ThreatIsArmor && distToThreat >= 12.0f && distToThreat <= maxThreatChargeDist)
                {
                    chargeTarget = board.ThreatPosition;
                    hasChargeTarget = true;
                }

                // Non-Gyokusai NCOs refuse to order a fresh Charge! right after losing men into a meatgrinder
                if (hasChargeTarget && !isTrueGyokusai && !isDesperateOvertime && board.DecayedRecentDeaths >= 1.40f)
                {
                    hasChargeTarget = false;
                }

                if (hasChargeTarget)
                {
                    float chargeCost = 1.72f * profile.BayonetBreachCostMult;
                    if (leaderPers == SoldierPersonality.Leader_Spearhead)
                        chargeCost *= 0.78f;
                    else if (leaderPers == SoldierPersonality.Leader_Tactician)
                        chargeCost *= 1.02f;
                    else if (leaderPers == SoldierPersonality.Leader_OldGuard)
                        chargeCost *= 1.38f;

                    bool isDesperateCap = false;
                    if (board.HasActiveObjectiveTask &&
                        board.IsDefendingObjective &&
                        board.ObjectiveCaptureProgress >= 0.55f &&
                        distToObj <= maxObjChargeDist)
                    {
                        isDesperateCap = true;
                        float urg01 = Mathf.InverseLerp(0.55f, 0.92f, board.ObjectiveCaptureProgress);
                        chargeCost *= Mathf.Lerp(0.76f, 0.44f, urg01);
                    }
                    else if (board.HasActiveObjectiveTask &&
                             !board.IsDefendingObjective &&
                             distToObj <= maxObjChargeDist)
                    {
                        if (board.ObjectiveCaptureProgress >= 0.60f)
                        {
                            chargeCost *= 0.72f;
                        }
                        else if (board.FriendlyUnitsInsideObjective == 0 && board.AliveMemberCount >= 5 && distToObj <= 24.0f)
                        {
                            chargeCost *= 0.84f;
                        }
                    }

                    if (board.IsInvaderFaction &&
                        board.TicketRatio01 <= 0.18f &&
                        board.HasActiveObjectiveTask &&
                        !board.IsDefendingObjective &&
                        distToObj <= maxObjChargeDist)
                    {
                        isDesperateCap = true;
                        chargeCost *= 0.68f;
                    }

                    bool hasSmokeCover = (now < board.SmokeActiveUntil &&
                                          (board.LastSmokeTargetPos - chargeTarget).sqrMagnitude <= 28.0f * 28.0f) ||
                                         Physics.Linecast(
                                             leaderPos + Vector3.up * 1.2f,
                                             chargeTarget + Vector3.up * 1.2f,
                                             AiCore.GetSmokeOcclusionMask());
                    if (hasSmokeCover)
                        chargeCost *= 0.68f;

                    bool enemyPinned = board.HasActiveThreat && board.IsThreatBeingPinned(now);
                    if (enemyPinned)
                        chargeCost *= 0.80f;

                    float targetDistNow = Vector3.Distance(leaderPos, chargeTarget);
                    if (chTheater.IsOpenDesertOrSteppeMap && !hasSmokeCover && !isTrueGyokusai && !isDesperateCap && targetDistNow > 20.0f)
                    {
                        chargeCost *= 1.65f;
                    }

                    bool localTwoToOneOdds = board.NearbyFriendlyCount42m >= Mathf.Max(4, board.NearbyEnemyCount42m * 2);
                    bool hasTacticalJustification = isTrueGyokusai || isDesperateCap || hasSmokeCover || enemyPinned || localTwoToOneOdds;

                    if (board.NetDirective == SquadNetDirective.Hammer_WideFlankEnvelopment &&
                        targetDistNow <= 26.0f)
                    {
                        chargeCost *= 0.86f;
                    }

                    if (!isDesperateCap && leaderSupp >= 42.0f && !isTrueGyokusai)
                        chargeCost *= 1.55f;

                    if (hasTacticalJustification && chargeCost < bestCost)
                    {
                        bestCost = chargeCost;
                        bestAction = 1;
                    }
                }
            }

            // 2. Evaluate Covert Hold-Fire (sq.SetHoldFireOrder) — Stealth Hammer Envelopments, Tactician Infiltration, Ambush Doctrines
            if (!sq.HoldFire &&
                !sq.InChargeTime() &&
                !sq.IsEnemyAwareOfThisSquad &&
                !_garrisonAssignedObjectiveBySquad.ContainsKey(sqPtr) &&
                !(board.IsDefendingObjective && distToObj <= 55.0f) &&
                leaderSupp < 5.0f &&
                !leader.isBleeding &&
                board.AliveMemberCount >= 3 &&
                distToThreat > 42.0f &&
                distToObj > 40.0f &&
                (distToThreat <= 135.0f || distToObj <= 135.0f) &&
                (!_nextAiCovertOrderTime.TryGetValue(sqPtr, out float nextCovT) || now >= nextCovT))
            {
                bool wantStealthFlank = board.NetDirective == SquadNetDirective.Hammer_WideFlankEnvelopment;
                bool wantTacticianInfil = leaderPers == SoldierPersonality.Leader_Tactician && !board.IsDefendingObjective;
                bool wantDoctrineAmbush = profile.PreferAmbushWhenAlone && !board.IsConnectedToRadioNet;

                if (wantStealthFlank || wantTacticianInfil || wantDoctrineAmbush)
                {
                    float covertCost = 1.26f * profile.FlankMoveCostMult;
                    if (wantStealthFlank)
                        covertCost *= 0.72f;
                    if (wantTacticianInfil)
                        covertCost *= 0.76f;
                    if (wantDoctrineAmbush)
                        covertCost *= 0.80f;
                    if (leaderPers == SoldierPersonality.Leader_Spearhead)
                        covertCost *= 1.20f;
                    if (board.IsDefendingObjective && board.ObjectiveCaptureProgress >= 0.25f)
                        covertCost *= 2.50f;

                    if (covertCost < bestCost)
                    {
                        bestCost = covertCost;
                        bestAction = 2;
                    }
                }
            }

            // 3. Evaluate Follow Column (sq.FollowLeader) — Fast Unengaged Rear-Echelon Approach (>115m Out of Contact)
            if ((sq.order == Order.defend ||
                 (sq.order == Order.attackFromSide && board.NetDirective != SquadNetDirective.Hammer_WideFlankEnvelopment)) &&
                !sq.InChargeTime() &&
                !board.HasActiveThreat &&
                !sq.IsEnemyAwareOfThisSquad &&
                !board.IsDefendingObjective &&
                !_garrisonAssignedObjectiveBySquad.ContainsKey(sqPtr) &&
                distToObj >= 115.0f &&
                distToObj < 9000.0f &&
                leaderSupp < 2.0f &&
                (!_nextAiFollowOrderTime.TryGetValue(sqPtr, out float nextFolT) || now >= nextFolT) &&
                AreMajorityOfSquadFollowersNearLeader(sq, leader, leaderPos, 28.0f))
            {
                const float followCost = 0.86f;
                if (followCost < bestCost)
                {
                    bestCost = followCost;
                    bestAction = 3;
                }
            }

            // 4. Evaluate Fighting Withdrawal / Regroup (sq.HoldArea + Smoke + 'Retreat!') — Mauled Squads, Outnumbered Pinned Squads, Uncounterable Close Armor
            if (VisionConfig.EnableTacticalRetreatsAndRegroup.Value &&
                !sq.InChargeTime() &&
                board.HasActiveThreat &&
                distToThreat <= 68.0f &&
                !AiCore.IsOnBridgeOrNarrowCrossing(leaderPos) &&
                !(board.IsDefendingObjective && board.ObjectiveCaptureProgress >= 0.55f) &&
                !(board.IsInvaderFaction && board.TicketRatio01 <= 0.18f) &&
                (!_nextAiSquadRetreatTime.TryGetValue(sqPtr, out float nextRetT) || now >= nextRetT))
            {
                Vehicle closeThreatTank = (board.ThreatIsArmor && board.PrimaryTarget != null)
                    ? board.PrimaryTarget.TryCast<Vehicle>()
                    : null;
                bool noAtAgainstCloseTank =
                    board.ThreatIsArmor &&
                    !board.SquadHasAntiTank &&
                    distToThreat <= 42.0f &&
                    (closeThreatTank == null || IsTankThreateningPosition(closeThreatTank, leaderPos, 52.0f));
                bool fanaticalNoRetreat = isTrueGyokusai && !noAtAgainstCloseTank;

                if (!fanaticalNoRetreat)
                {
                    bool netWithdrawal = board.NetDirective == SquadNetDirective.Regroup_SectorWithdrawal;
                    bool mauledRemnant = board.AliveMemberCount <= 2 &&
                                         leaderSupp >= 22.0f &&
                                         board.NearbyEnemyCount42m > board.NearbyFriendlyCount42m;
                    bool pinnedAndOverwhelmed = board.AliveMemberCount <= 3 &&
                                                leaderSupp >= 38.0f &&
                                                board.NearbyEnemyCount42m >= board.NearbyFriendlyCount42m + 4;

                    if ((netWithdrawal || noAtAgainstCloseTank || mauledRemnant || pinnedAndOverwhelmed) &&
                        TryComputeTacticalFallbackPosition(leader, sq, leaderPos, board.ThreatPosition, 24.0f, 44.0f, out retreatRallyPos))
                    {
                        float retreatCost = (netWithdrawal || noAtAgainstCloseTank) ? 0.52f : 0.72f;
                        if (leaderPers == SoldierPersonality.Leader_OldGuard)
                            retreatCost *= 0.80f;
                        else if (leaderPers == SoldierPersonality.Leader_Tactician)
                            retreatCost *= 0.88f;
                        else if (leaderPers == SoldierPersonality.Leader_Spearhead)
                            retreatCost *= 1.18f;

                        if (retreatCost < bestCost)
                        {
                            bestCost = retreatCost;
                            bestAction = 4;
                        }
                    }
                }
            }

            if (bestAction == 1)
            {
                if (!syncOnline || AiCore.TryConsumeBotRpcBudget(0.45f))
                {
                    if (_aiCovertHoldFireSquads.Remove(sqPtr))
                    {
                        _aiCovertHoldFireExpireTime.Remove(sqPtr);
                        try { sq.SetHoldFireOrder(false, false, false, syncOnline); } catch { }
                    }
                    _aiFollowColumnUntil.Remove(sqPtr);

                    try
                    {
                        sq.Charge(chargeTarget, 15.0f);
                        if (sq.timed_order_end < now + 14.0f)
                            sq.timed_order_end = now + 14.0f;
                    }
                    catch { }

                    _aiOrderedChargeSquads.Add(sqPtr);
                    _aiChargeBaselineDeaths[sqPtr] = board.DecayedRecentDeaths;
                    _nextAiSquadChargeTime[sqPtr] = now + 62.0f;
                    issuedSquadCoverOrderThisTick = true;
                    SquadChargesOrderedCount++;
                    GoapPlansExecutedCount++;

                    if (VisionConfig.DebugLogging.Value)
                    {
                        AiCore.LogDebug(
                            "SquadCharge",
                            $"{leader.faction} {leaderPers} ordered Charge! on '{sq.squadName}' (cost:{bestCost:F2}, capProg:{board.ObjectiveCaptureProgress:F2}, dist:{Vector3.Distance(leaderPos, chargeTarget):F0}m).",
                            3.0f);
                    }
                }
            }
            else if (bestAction == 2)
            {
                if (!syncOnline || AiCore.TryConsumeBotRpcBudget(0.40f))
                {
                    try
                    {
                        sq.SetHoldFireOrder(true, false, true, syncOnline);
                        _aiCovertHoldFireSquads.Add(sqPtr);
                        _aiCovertHoldFireExpireTime[sqPtr] = now + 20.0f;
                    }
                    catch { }

                    _nextAiCovertOrderTime[sqPtr] = now + 38.0f;
                    issuedSquadCoverOrderThisTick = true;
                    SquadCovertOrdersCount++;
                    GoapPlansExecutedCount++;

                    if (VisionConfig.DebugLogging.Value)
                    {
                        AiCore.LogDebug(
                            "SquadCovert",
                            $"{leader.faction} {leaderPers} ordered Covert Hold-Fire on '{sq.squadName}' (cost:{bestCost:F2}, directive:{board.NetDirective}).",
                            3.0f);
                    }
                }
            }
            else if (bestAction == 3)
            {
                if (!syncOnline || AiCore.TryConsumeBotRpcBudget(0.40f))
                {
                    try
                    {
                        sq.FollowLeader(false);
                        sq.timed_order_end = now + 12.0f;
                        _aiFollowColumnUntil[sqPtr] = now + 12.0f;
                    }
                    catch { }

                    _nextAiFollowOrderTime[sqPtr] = now + 32.0f;
                    issuedSquadCoverOrderThisTick = true;
                    SquadFollowOrdersCount++;
                    GoapPlansExecutedCount++;
                }
            }
            else if (bestAction == 4)
            {
                if (!syncOnline || AiCore.TryConsumeBotRpcBudget(0.45f))
                {
                    if (_aiCovertHoldFireSquads.Remove(sqPtr))
                    {
                        _aiCovertHoldFireExpireTime.Remove(sqPtr);
                        try { sq.SetHoldFireOrder(false, false, false, syncOnline); } catch { }
                    }
                    _aiFollowColumnUntil.Remove(sqPtr);

                    // Pop screening smoke before breaking contact if the leader or a nearby squadmate carries smoke
                    TryPopSquadRetreatSmoke(sq, leader, ref board, now);
                    TrySayTacticalVoice(leader, sq, VoiceManager.VoiceClip.timeToRetreat, 0.05f);

                    try
                    {
                        sq.HoldArea(retreatRallyPos, 14.0f, false);
                        sq.timed_order_end = now + 17.0f;
                    }
                    catch { }

                    _aiSquadRetreatUntil[sqPtr] = now + 17.0f;
                    _nextAiSquadRetreatTime[sqPtr] = now + 48.0f;
                    issuedSquadCoverOrderThisTick = true;
                    TacticalRetreatsCount++;
                    GoapPlansExecutedCount++;
                    BotActionTrace.RecordEvent(leader, BotTraceEventKind.SquadRetreat, board.ThreatIsArmor ? "squad_withdraw_armor" : "squad_withdraw_mauled", distToThreat);

                    if (VisionConfig.DebugLogging.Value)
                    {
                        AiCore.LogDebug(
                            "SquadRetreat",
                            $"{leader.faction} {leaderPers} ordered Fighting Withdrawal / Regroup on '{sq.squadName}' (alive:{board.AliveMemberCount}, armor:{board.ThreatIsArmor}, rallyDist:{Vector3.Distance(leaderPos, retreatRallyPos):F0}m).",
                            3.0f);
                    }
                }
            }
        }

        internal static bool HasUsableAntiTankCapability(Soldier u)
        {
            if (u == null || !u.IsAlive)
                return false;

            try
            {
                for (int slot = 0; slot <= 2; slot++)
                {
                    GenericGun g = u.GetHeldGun(slot);
                    if (g != null && g.IsAtWeapon() && (g.GetCurrentAmmoCount() > 0 || !u.IsOutOfAmmosFor(g)))
                        return true;
                }

                if (AiCore.FindThrowableGrenadeInInventory(u, findAtGrenade: true, findFragGrenade: false) != null)
                    return true;

                VirtualItem vGren = AiCore.FindThrowableGrenadeInInventory(u, findAtGrenade: false, findFragGrenade: true);
                if (vGren != null && AiCore.TryGetThrowablePrefabInfo(vGren, out AiCore.ThrowablePrefabInfo gInfo) &&
                    (gInfo.Type == GrenadeType.ATGrenade || gInfo.Type == GrenadeType.phosporusGrenade || gInfo.MaxPenetration >= 16.0f || gInfo.IsSatchelOrHeavyDemo))
                {
                    return true;
                }
            }
            catch { }

            return false;
        }

        private static bool AreMajorityOfSquadFollowersNearLeader(Squad sq, Soldier leader, Vector3 leaderPos, float maxDist)
        {
            if (sq == null || sq.units == null)
                return true;

            int aliveFootFollowers = 0;
            int closeFollowers = 0;
            float maxDistSq = maxDist * maxDist;
            IntPtr leaderPtr = leader != null ? leader.Pointer : IntPtr.Zero;
            int count = sq.units.Count;
            for (int i = 0; i < count; i++)
            {
                Soldier u = sq.units[i];
                if (u == null || u.Pointer == IntPtr.Zero || u.Pointer == leaderPtr || u.IsDead || u.HasSurrended() || u.IsOnVehicle() || u.transform == null)
                    continue;

                aliveFootFollowers++;
                if ((u.transform.position - leaderPos).sqrMagnitude <= maxDistSq)
                    closeFollowers++;
            }

            return aliveFootFollowers == 0 || (closeFollowers * 2 >= aliveFootFollowers);
        }

        private static bool TryFindSquadHillsideContourPoint(
            Vector3 leaderPos,
            Vector3 objPos,
            IntPtr sqPtr,
            int stallCount,
            out Vector3 detourPos)
        {
            detourPos = Vector3.zero;
            Vector3 toObj = objPos - leaderPos;
            toObj.y = 0f;
            if (toObj.sqrMagnitude < 16.0f)
                return false;

            Vector3 fwd = toObj.normalized;
            Vector3 right = new Vector3(fwd.z, 0f, -fwd.x);
            int primarySign = ((AiCore.GetDeterministicHash(sqPtr, stallCount) & 1) == 0) ? 1 : -1;
            int moveMask = AiCore.GetMovementObstacleMask();
            bool hasWater = AiCore.TryGetActiveWaterLevel(out float waterLevel);

            float[] latWeights = { 0.90f, -0.90f, 1.0f, -1.0f };
            float[] fwdWeights = { 0.44f, 0.44f, 0.0f, 0.0f };
            float[] distances = { 22.0f, 26.0f, 18.0f };

            for (int i = 0; i < latWeights.Length; i++)
            {
                Vector3 dir = (right * (latWeights[i] * primarySign) + fwd * fwdWeights[i]).normalized;
                for (int dIdx = 0; dIdx < distances.Length; dIdx++)
                {
                    float dist = distances[dIdx];
                    if (Physics.Raycast(leaderPos + Vector3.up * 1.1f, dir, Mathf.Min(dist, 12.0f), moveMask, QueryTriggerInteraction.Ignore))
                        continue;

                    Vector3 cand = leaderPos + dir * dist;
                    float detourLift = dist * 1.35f;
                    if (!AiCore.TrySnapWalkableGroundProbe(leaderPos, cand, detourLift, detourLift * 2.0f, 0.60f, 2.4f, hasWater, waterLevel, out Vector3 snappedDetour))
                        continue;

                    detourPos = snappedDetour;
                    return true;
                }
            }

            return false;
        }

        private static void TryPopSquadRetreatSmoke(Squad sq, Soldier leader, ref SquadBlackboard board, float now)
        {
            if (!board.HasActiveThreat || board.PrimaryTarget == null || now < board.SmokeActiveUntil)
                return;

            var units = sq.units;
            int count = units != null ? units.Count : 0;
            for (int i = -1; i < count; i++)
            {
                Soldier cand = (i < 0) ? leader : units[i];
                if (cand == null || !cand.IsAlive || cand.IsPlayer() || cand.HasSurrended() || cand.inventory == null || cand.transform == null)
                    continue;
                if (cand.isInHandsAnimation || cand.isInBodyAnimation || now <= cand.throw_end_time + 4.0f)
                    continue;

                VirtualItem smoke = cand.inventory.FindItemOfType<VirtualSmokeGrenade>();
                if (smoke == null)
                    continue;

                Vector3 cPos = cand.transform.position;
                float d = Vector3.Distance(cPos, board.ThreatPosition);
                if (d >= 8.0f && d <= cand.GetGrenadeThrowRange(smoke) + 8.0f &&
                    !AiCore.HasLowCeilingOverhead(cPos, 3.2f) &&
                    AiCore.TryConsumeBotRpcBudget(0.45f))
                {
                    AiCore.SoldierFastHandles candH = AiCore.GetOrCreateSoldierHandles(cand);
                    if (candH != null)
                        candH.Goap.NextSmokeGrenadeTime = now + 32.0f;
                    board.SmokeActiveUntil = now + 22.0f;
                    board.LastSmokeTargetPos = board.ThreatPosition;
                    cand.Throw(smoke, board.PrimaryTarget);
                    GoapSmokeScreensCount++;
                    return;
                }
            }
        }

        private static bool TryComputeTacticalFallbackPosition(
            Soldier soldier,
            Squad excludeSquad,
            Vector3 fromPos,
            Vector3 threatPos,
            float minDist,
            float maxDist,
            out Vector3 fallbackPos)
        {
            fallbackPos = fromPos;
            if (soldier == null || string.IsNullOrEmpty(soldier.faction))
                return false;

            Vector3 awayDir = fromPos - threatPos;
            awayDir.y = 0f;
            if (awayDir.sqrMagnitude < 0.05f)
            {
                awayDir = soldier.transform != null ? -soldier.transform.forward : Vector3.back;
                awayDir.y = 0f;
            }
            awayDir = awayDir.sqrMagnitude > 0.001f ? awayDir.normalized : Vector3.back;
            Vector3 threatDir = -awayDir;

            // 1. Prefer rallying onto a nearby intact friendly squad or friendly ground vehicle in the rear hemisphere!
            AiCore.EnsureWorldSnapshot();
            var squads = AiCore.WorldSquads;
            int sqCount = AiCore.WorldSquadCount;
            IntPtr exclPtr = excludeSquad != null ? excludeSquad.Pointer : IntPtr.Zero;
            float bestAllyScore = float.MaxValue;
            Vector3 bestAllyRally = Vector3.zero;
            bool foundAllyRally = false;

            string myFac = AiCore.GetCachedFaction(soldier);
            for (int i = 0; i < sqCount; i++)
            {
                ref AiCore.SquadSnapshot fSnap = ref squads[i];
                if (fSnap.Ptr == exclPtr || fSnap.UnitCount < 2 || fSnap.RefOnPlane || string.IsNullOrEmpty(fSnap.Faction))
                    continue;

                if (!AiCore.AreSameSideFactions(myFac, fSnap.Faction))
                    continue;

                Vector3 toAlly = fSnap.RefPos - fromPos;
                float d = new Vector2(toAlly.x, toAlly.z).magnitude;
                if (d < minDist * 0.80f || d > 90.0f || Mathf.Abs(toAlly.y) > 12.0f)
                    continue;

                Vector3 toAllyDir = new Vector3(toAlly.x, 0f, toAlly.z) / d;
                if (Vector3.Dot(toAllyDir, awayDir) < 0.15f)
                    continue;

                float score = d - (fSnap.LeaderOnVehicle ? 14.0f : 0f) - Mathf.Min(fSnap.UnitCount, 8) * 1.5f;
                if (score < bestAllyScore)
                {
                    bestAllyScore = score;
                    bestAllyRally = fSnap.RefPos + awayDir * 4.0f;
                    foundAllyRally = true;
                }
            }

            Vector3 searchAnchor = foundAllyRally
                ? Vector3.Lerp(fromPos, bestAllyRally, Mathf.Clamp01(maxDist / Mathf.Max(1f, Vector3.Distance(fromPos, bestAllyRally))))
                : (fromPos + awayDir * Mathf.Lerp(minDist, maxDist, 0.65f));

            // 2. Check CoverManager.staticCovers around the fallback anchor for sheltered rear cover
            var octree = CoverManager.staticCovers;
            if (octree != null)
            {
                var nearby = octree.GetNearby(searchAnchor, 14.0f);
                if (nearby != null && nearby.Length > 0)
                {
                    float bestCovDistSq = float.MaxValue;
                    for (int i = 0; i < nearby.Length; i++)
                    {
                        AiDestination d = nearby[i];
                        CombatCover cc = d != null ? d.TryCast<CombatCover>() : null;
                        if (cc == null)
                            continue;

                        Vector3 cPos = cc.GetCoverPosition();
                        if (Mathf.Abs(cPos.y - fromPos.y) > 6.5f)
                            continue;

                        Vector3 stepVec = cPos - fromPos;
                        stepVec.y = 0f;
                        if (stepVec.sqrMagnitude < (minDist * 0.65f) * (minDist * 0.65f) || Vector3.Dot(stepVec.normalized, awayDir) < 0.15f)
                            continue;

                        if (!cc.IsCoverAvailable(threatDir, soldier.faction))
                            continue;

                        float dSq = (cPos - searchAnchor).sqrMagnitude;
                        if (dSq < bestCovDistSq)
                        {
                            bestCovDistSq = dSq;
                            fallbackPos = cPos;
                        }
                    }
                    if (bestCovDistSq < float.MaxValue)
                        return true;
                }
            }

            // 3. Snap fallback anchor onto walkable ground (rejecting urban rooftops, collider interiors, and water
            //    while preserving steep TerrainCollider hillside retreats & open-field SeekEmergencyCover scatters).
            bool hasFbWater = AiCore.TryGetActiveWaterLevel(out float fbWaterLevel);
            float anchorFlatDist = new Vector2(searchAnchor.x - fromPos.x, searchAnchor.z - fromPos.z).magnitude;
            float fbLiftY = Mathf.Max(8.0f, anchorFlatDist * 1.50f);
            float maxNonTerrainDy = Mathf.Clamp(anchorFlatDist * 0.08f, 2.0f, 2.4f);
            if (AiCore.TrySnapWalkableGroundProbe(fromPos, searchAnchor, fbLiftY, fbLiftY * 2.05f, 0.55f, maxNonTerrainDy, hasFbWater, fbWaterLevel, out Vector3 snappedFb))
            {
                fallbackPos = snappedFb;
                return true;
            }

            return false;
        }

        internal static bool TryTriggerImmediateIndividualFallback(Soldier soldier, Vector3 myPos)
        {
            if (soldier == null || soldier.Pointer == IntPtr.Zero || soldier.aiController == null || !AiCore.CanBotMoveAutonomously(soldier))
                return false;

            if (AiCore.IsOnBridgeOrNarrowCrossing(myPos))
                return false;

            AiCore.SoldierFastHandles fbHandles = AiCore.GetOrCreateSoldierHandles(soldier);
            float now = Time.time;
            if (fbHandles != null && now < fbHandles.Goap.NextIndividualFallbackTime)
                return false;

            Squad sq = soldier.joinedSquad;
            Vector3 threatPos = myPos + (soldier.transform != null ? soldier.transform.forward * 15.0f : Vector3.forward * 15.0f);
            if (soldier.aiController.visibleTarget != null &&
                AiCore.TryGetSpottableCenter(soldier.aiController.visibleTarget, out Vector3 visPos))
            {
                threatPos = visPos;
            }
            else if (sq != null && _squadBlackboards.TryGetValue(sq.Pointer, out SquadBlackboard b) && b.HasActiveThreat)
            {
                threatPos = b.ThreatPosition;
            }

            if (!TryComputeTacticalFallbackPosition(soldier, sq, myPos, threatPos, 20.0f, 34.0f, out Vector3 fbDest))
                return false;

            // Pop personal smoke grenade if carried before sprinting back!
            if (soldier.inventory != null &&
                !soldier.isInHandsAnimation &&
                !soldier.isInBodyAnimation &&
                now > soldier.throw_end_time + 4.0f &&
                soldier.aiController.visibleTarget != null)
            {
                VirtualItem smoke = soldier.inventory.FindItemOfType<VirtualSmokeGrenade>();
                if (smoke != null &&
                    !AiCore.HasLowCeilingOverhead(myPos, 3.2f) &&
                    AiCore.TryConsumeBotRpcBudget(0.45f))
                {
                    if (fbHandles != null)
                        fbHandles.Goap.NextSmokeGrenadeTime = now + 32.0f;
                    soldier.Throw(smoke, soldier.aiController.visibleTarget);
                    GoapSmokeScreensCount++;
                }
            }

            if (fbHandles != null)
            {
                AiCore.SetBotMoveOverride(fbHandles, AiCore.BotMoveOverrideKind.EmergencyFallback, fbDest, now + 8.5f);
                fbHandles.Goap.NextIndividualFallbackTime = now + 36.0f;
                fbHandles.Stance.LocalCoverProtectedUntil = now + 8.5f;
            }

            SetBotPose(soldier.aiController, soldier, SoldierPose.Idle, true);
            AiCore.MoveBotTowardPosition(soldier.aiController, soldier, fbDest, 2.2f, 3.0f);
            TrySayTacticalVoice(soldier, sq, VoiceManager.VoiceClip.timeToRetreat, 0.05f);
            TacticalRetreatsCount++;
            GoapPlansExecutedCount++;
            BotActionTrace.RecordEvent(soldier, BotTraceEventKind.FallbackRetreat, "immediate_fallback");
            return true;
        }

        private static Squad FindBestAvailableHammerSquad(
            List<Squad> connectedInfantrySquads,
            int excludeIndex,
            IntPtr anvilSqPtr,
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
                if (candSq == null || candSq.Pointer == anvilSqPtr || IsSquadActivelyRetreating(candSq.Pointer, now))
                    continue;
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
                if (dToThreatSq >= bestHammerDistSq || dToThreatSq <= 15f * 15f)
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
                (leader.Params == null || leader.Params.canUseRadio) &&
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
            hammerBoard.SuggestedNetDirective = SquadNetDirective.Hammer_WideFlankEnvelopment;
            hammerBoard.SuggestedDirectiveExpireTime = now + 24.0f;
            hammerBoard.SuggestedDirectiveTargetPos = threatPos;
            hammerBoard.SuggestedPincerTaskPtr = IntPtr.Zero;
            _squadBlackboards[hammerSq.Pointer] = hammerBoard;
            ConsumeInitialHqSpawnBriefingIfNoRadio(hammerSq);

            if (VisionConfig.DebugLogging.Value)
            {
                AiCore.LogDebug(
                    "TeamNetGOAP",
                    $"{hammerLeader.faction} Radio Net suggested {anvilLabel} + Hammer wide flank to '{hammerSq.squadName}'.");
            }
        }

        internal static void NotifySquadMemberKilled(Soldier victim)
        {
            if (victim == null || victim.Pointer == IntPtr.Zero)
                return;

            Squad sq = victim.joinedSquad;
            if (sq == null || sq.Pointer == IntPtr.Zero || AiCore.IsTrueVehicleCrewSquad(sq))
                return;

            Transform vTr = victim.transform;
            if (vTr == null)
                return;

            IntPtr sqPtr = sq.Pointer;
            Vector3 deathPos = vTr.position;
            float now = Time.time;

            _squadBlackboards.TryGetValue(sqPtr, out SquadBlackboard board);
            float dt = board.LastDeathDecayUpdateTime > 0f ? Mathf.Max(0f, now - board.LastDeathDecayUpdateTime) : 0f;
            board.DecayedRecentDeaths = board.DecayedRecentDeaths * Mathf.Exp(-dt * 0.05f) + 1.0f;
            board.LastDeathDecayUpdateTime = now;
            board.LastCasualtyPos = deathPos;
            board.LastCasualtyTime = now;
            _squadBlackboards[sqPtr] = board;

            // Nearby locally owned AI squadmates (<= 14m) witness their comrade drop and immediately re-evaluate their plan!
            var units = sq.units;
            if (units == null)
                return;

            IntPtr victimPtr = victim.Pointer;
            int count = units.Count;
            for (int i = 0; i < count; i++)
            {
                Soldier ally = units[i];
                if (ReferenceEquals(ally, null) || ally == null || ally.Pointer == victimPtr || !ally.IsAlive || !ally.IsAI() || !AiCore.IsLocallyOwnedBot(ally))
                    continue;

                AiCore.SoldierFastHandles ah = AiCore.GetOrCreateSoldierHandles(ally);
                if (ah == null || ah.Tr == null)
                    continue;

                if ((ah.Tr.position - deathPos).sqrMagnitude > 14.0f * 14.0f)
                    continue;

                float startle = 0.14f + (AiCore.GetDeterministicHash(ah.Ptr, victimPtr.GetHashCode()) & 15) * 0.018f;
                if (now < ah.Goap.Plan.PlanExpireTime)
                {
                    ah.Goap.Plan.NextReplanTime = Mathf.Min(ah.Goap.Plan.NextReplanTime, now + startle);
                }

                if (!ally.IsOnCover() && ah.Combat.FireAndMovement.PhaseStartTime > 0f && !ah.Combat.FireAndMovement.InFiringHalt)
                {
                    ah.Combat.FireAndMovement.InFiringHalt = true;
                    ah.Combat.FireAndMovement.PhaseStartTime = now;
                    ah.Combat.FireAndMovement.PhaseEndTime = now + 1.65f;
                    ah.Combat.FireAndMovement.HaltLostSightTime = 0f;
                }
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

            long profBoardStart = HitchProfiler.Begin();
            try
            {
            board.NextRefreshTime = now + 0.60f;
            if (board.LastDeathDecayUpdateTime > 0f)
            {
                float deathDt = Mathf.Max(0f, now - board.LastDeathDecayUpdateTime);
                board.DecayedRecentDeaths *= Mathf.Exp(-deathDt * 0.05f);
                if (board.DecayedRecentDeaths < 0.05f)
                    board.DecayedRecentDeaths = 0f;
            }
            board.LastDeathDecayUpdateTime = now;

            Soldier leader = sq.Leader != null && sq.Leader.IsAlive ? sq.Leader : fallbackSoldier;
            if (leader == null || leader.transform == null)
            {
                _squadBlackboards[sqPtr] = board;
                return board;
            }

            Vector3 sqCenter = leader.transform.position;
            board.SquadCenter = sqCenter;

            // Check Radioman status & detect sudden loss or field recovery of squad Radioman
            Soldier rad = sq.GetRadioman();
            bool hasAliveRadNow = (rad != null && rad.IsAlive && !rad.HasSurrended()) ||
                                  IsMountedOnGroundVehicle(leader);
            bool isFirstRadioInit = board.RadioNetToastInitState == 0;
            if (isFirstRadioInit)
            {
                board.PreviouslyHadAliveRadioman = hasAliveRadNow;
            }
            bool radiomanJustDied = !isFirstRadioInit && board.PreviouslyHadAliveRadioman && !hasAliveRadNow;
            bool radiomanJustRecovered = !isFirstRadioInit && !board.PreviouslyHadAliveRadioman && hasAliveRadNow;

            if (radiomanJustDied)
            {
                _initialHqSpawnBriefingConsumed.Add(sqPtr);
                board.PreviouslyHadAliveRadioman = false;
                board.LastRadiomanLostTime = now;
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
                _initialHqSpawnBriefingConsumed.Add(sqPtr);
                board.PreviouslyHadAliveRadioman = true;
                board.LastRadiomanLostTime = 0f;
            }

            board.IsConnectedToRadioNet = IsSquadConnectedToRadioNet(sq);
            if (!board.IsConnectedToRadioNet)
            {
                board.NetDirective = SquadNetDirective.Disconnected_Autonomous;
            }

            // Debounced Radio Net Disconnected / Regained Toasts for Player Squads (Local HUD + Host-Assist Co-op Chat)
            if (VisionConfig.EnableRadiomanIntelNet.Value)
            {
                bool liveRadioLink = HasLiveRadioOrRelayLink(sq);
                if (isFirstRadioInit)
                {
                    board.RadioNetToastInitState = (liveRadioLink || hasAliveRadNow) ? (byte)2 : (byte)1;
                    board.RadioRelayPendingFlipSince = 0f;
                }
                else if (sq.IsPlayerInSquad())
                {
                    if (radiomanJustDied)
                    {
                        board.RadioNetToastInitState = liveRadioLink ? (byte)2 : (byte)1;
                        board.RadioRelayPendingFlipSince = 0f;
                        board.NextRadioNetStatusToastTime = now + 16.0f;
                        string lostMsg = !liveRadioLink
                            ? "Squad Radio: Radioman down \u2014 squad cut off from HQ Net! (Loot a radio or stay near friendly squads)"
                            : "Squad Radio: Radioman down \u2014 squad holding HQ link via nearby friendly relay";
                        AiCore.TryShowLocalPlayerRadioHint(sq, lostMsg, requireLeader: false, minInterval: 6.0f);
                    }
                    else if (radiomanJustRecovered)
                    {
                        board.RadioNetToastInitState = 2;
                        board.RadioRelayPendingFlipSince = 0f;
                        board.NextRadioNetStatusToastTime = now + 16.0f;
                        Soldier lp = Soldier.CurrentControlledSoldierOrNull();
                        bool playerIsRad = lp != null && lp.IsAlive && lp.joinedSquad != null && lp.joinedSquad.Pointer == sqPtr && lp.IsRadioman();
                        string recMsg = playerIsRad
                            ? "Squad Radio: You recovered a field radio \u2014 squad reconnected to HQ Radio Net!"
                            : "Squad Radio: Field radio recovered \u2014 squad reconnected to HQ Radio Net!";
                        AiCore.TryShowLocalPlayerRadioHint(sq, recMsg, requireLeader: false, minInterval: 6.0f);
                    }
                    else if (!hasAliveRadNow)
                    {
                        // Relay notices fire strictly when NO alive Radioman exists in the squad
                        bool wasNotifiedConnected = board.RadioNetToastInitState == 2;
                        if (liveRadioLink != wasNotifiedConnected)
                        {
                            if (board.RadioRelayPendingFlipSince <= 0f)
                                board.RadioRelayPendingFlipSince = now;

                            if ((now - board.RadioRelayPendingFlipSince) >= 4.0f && now >= board.NextRadioNetStatusToastTime)
                            {
                                board.RadioNetToastInitState = liveRadioLink ? (byte)2 : (byte)1;
                                board.RadioRelayPendingFlipSince = 0f;
                                board.NextRadioNetStatusToastTime = now + 20.0f;

                                string relayMsg = !liveRadioLink
                                    ? "Squad Radio: Squad out of friendly relay range \u2014 cut off from HQ Radio Net"
                                    : "Squad Radio: Squad back in friendly relay range \u2014 reconnected to HQ Radio Net";
                                AiCore.TryShowLocalPlayerRadioHint(sq, relayMsg, requireLeader: false, minInterval: 6.0f);
                            }
                        }
                        else
                        {
                            board.RadioRelayPendingFlipSince = 0f;
                        }
                    }
                    else
                    {
                        board.RadioNetToastInitState = 2;
                        board.RadioRelayPendingFlipSince = 0f;
                    }
                }
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
                if (!ConquestEconomy.IsActive && bd != null && !string.IsNullOrEmpty(leader.faction))
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
                    AiCore.EvaluateSquadTaskForFaction(
                        curTask,
                        leader.faction,
                        bd,
                        _garrisonAssignedObjectiveBySquad.ContainsKey(sqPtr),
                        out board.IsDefendingObjective,
                        out board.ObjectiveCaptureProgress);
                    if (ConquestEconomy.IsActive)
                    {
                        board.IsInvaderFaction = !board.IsDefendingObjective;
                    }
                }
            }
            catch { }

            // Scan squad members once to find active threat, alive count, MG/Medic/AT presence & surrendered comrades
            board.HasActiveThreat = false;
            board.ThreatIsArmor = false;
            board.HasAliveMachineGunner = false;
            board.HasAliveMedic = false;
            board.SquadHasAntiTank = false;
            board.PrimaryTarget = null;
            board.SurrenderedComrade = null;
            board.AliveMemberCount = 0;
            board.NearbyEnemyCount42m = 0;
            board.NearbyFriendlyCount42m = 0;
            board.FriendlyUnitsInsideObjective = 0;
            board.EnemyUnitsInsideObjective = 0;

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

                    if (u.HasSurrended())
                    {
                        if (board.SurrenderedComrade == null && AiCore.IsTrueSurrenderedPrisoner(u))
                        {
                            Vector3 uPos = u.transform.position;
                            float dxU = uPos.x - sqCenter.x;
                            float dzU = uPos.z - sqCenter.z;
                            float horizDistU = Mathf.Sqrt(dxU * dxU + dzU * dzU);
                            if (Mathf.Abs(uPos.y - sqCenter.y) <= 2.6f + horizDistU * 0.55f &&
                                !HasArmedEnemyNearPosition(uPos, leader.faction, 10.0f))
                            {
                                board.SurrenderedComrade = u;
                            }
                        }
                        continue;
                    }

                    board.AliveMemberCount++;
                    if (u.hasMg || u.IsGunner() || u.GetHeldFunctionalBipod() != null)
                    {
                        board.HasAliveMachineGunner = true;
                    }
                    if (u.hasSyringe)
                    {
                        board.HasAliveMedic = true;
                    }
                    if (!board.SquadHasAntiTank && HasUsableAntiTankCapability(u))
                    {
                        board.SquadHasAntiTank = true;
                    }

                    SoldierAI uAi = u.aiController;
                    Spottable vt = null;
                    Vector3 tPos = Vector3.zero;
                    if (uAi != null && AiCore.TryGetSpottableCenter(uAi.visibleTarget, out tPos))
                    {
                        vt = uAi.visibleTarget;
                    }
                    else if (u.IsOnVehicle())
                    {
                        // Seated vehicle crew disable SoldierAI and aim via TurretAI.CurrentVisibleTarget
                        Vehicle curVeh = u.GetCurrentVehicle();
                        Turret mt = curVeh != null ? curVeh.GetMainTurret(false) : null;
                        Spottable turretVt = (mt != null && mt.autoPilot != null) ? mt.autoPilot.CurrentVisibleTarget : null;
                        if (turretVt != null && AiCore.TryGetSpottableCenter(turretVt, out tPos))
                        {
                            vt = turretVt;
                        }
                    }

                    if (vt != null)
                    {
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
                    else if (AiCore.TryGetSoldierHandles(u.Pointer, out AiCore.SoldierFastHandles uH) &&
                             uH.Vision.LastKnownTarget.LastSeenTime > 0f &&
                             now - uH.Vision.LastKnownTarget.LastSeenTime <= 6.5f)
                    {
                        LastKnownTargetInfo lk = uH.Vision.LastKnownTarget;
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

            // Single flat-array pass over AiCore.WorldSquads/WorldSoldiers (0.60s per squad) to compute:
            //  1) Local 42m force ratio (NearbyFriendlyCount42m vs NearbyEnemyCount42m) for Tactical Retreat decisions
            //  2) Active capture-zone headcount (FriendlyUnitsInsideObjective vs EnemyUnitsInsideObjective) so squads
            //     know whether they actually have capture majority inside the circle!
            if (!string.IsNullOrEmpty(leader.faction))
            {
                AiCore.EnsureWorldSnapshot();
                var wSquads = AiCore.WorldSquads;
                var wSoldiers = AiCore.WorldSoldiers;
                int wSqCount = AiCore.WorldSquadCount;

                bool countObjCircle = board.HasActiveObjectiveTask &&
                                      sq.currentTask != null &&
                                      (sqCenter - board.ObjectivePosition).sqrMagnitude <= 130.0f * 130.0f;
                float objRad = countObjCircle ? Mathf.Max(10.0f, sq.currentTask.GetTaskAreaRadius()) : 0f;
                float objRadSq = objRad * objRad;
                Vector3 objCenter = board.ObjectivePosition;
                const float localR42Sq = 42.0f * 42.0f;
                const float coarseSqRadius = 90.0f * 90.0f;

                string leaderFac = AiCore.GetCachedFaction(leader);
                for (int s = 0; s < wSqCount; s++)
                {
                    ref AiCore.SquadSnapshot sSnap = ref wSquads[s];
                    if (sSnap.UnitCount == 0 || sSnap.RefOnPlane || string.IsNullOrEmpty(sSnap.Faction))
                        continue;

                    bool nearSquad = (sSnap.RefPos - sqCenter).sqrMagnitude <= coarseSqRadius;
                    bool nearObj = countObjCircle && (sSnap.RefPos - objCenter).sqrMagnitude <= (objRad + 55.0f) * (objRad + 55.0f);
                    if (!nearSquad && !nearObj)
                        continue;

                    bool sameSide = AiCore.AreSameSideFactions(leaderFac, sSnap.Faction);
                    int end = sSnap.UnitStartIndex + sSnap.UnitCount;
                    for (int u = sSnap.UnitStartIndex; u < end; u++)
                    {
                        Vector3 uPos = wSoldiers[u].Pos;
                        if (nearSquad)
                        {
                            Vector3 dL = uPos - sqCenter;
                            if (dL.x * dL.x + dL.z * dL.z <= localR42Sq && Mathf.Abs(dL.y) <= 14.0f)
                            {
                                if (sameSide) board.NearbyFriendlyCount42m++;
                                else board.NearbyEnemyCount42m++;
                            }
                        }

                        if (nearObj)
                        {
                            Vector3 dO = uPos - objCenter;
                            if (dO.x * dO.x + dO.z * dO.z <= objRadSq && Mathf.Abs(dO.y) <= 16.0f)
                            {
                                if (sameSide) board.FriendlyUnitsInsideObjective++;
                                else board.EnemyUnitsInsideObjective++;
                            }
                        }
                    }
                }
            }

            // Vanilla Soldier.Surrender() detaches the soldier from sq.units (joinedSquad = null), so scan
            // AiCore.WorldSoldiers (O(N) flat struct array) for any surrendered friendly comrade within LiberateSearchRadius!
            if (board.SurrenderedComrade == null &&
                VisionConfig.EnableComradeLiberation.Value &&
                !string.IsNullOrEmpty(leader.faction))
            {
                AiCore.EnsureWorldSnapshot();
                int solCount = AiCore.WorldSoldierCount;
                var soldiers = AiCore.WorldSoldiers;
                float maxLibR = Mathf.Max(28.0f, VisionConfig.LiberateSearchRadius.Value + 12.0f);
                float bestSurrDistSq = maxLibR * maxLibR;

                string surrLeaderFac = AiCore.GetCachedFaction(leader);
                for (int i = 0; i < solCount; i++)
                {
                    ref AiCore.SoldierSnapshot sSnap = ref soldiers[i];
                    if (!sSnap.HasSurrendered || !AiCore.AreSameSideFactions(surrLeaderFac, sSnap.Faction) || sSnap.Ref == null)
                        continue;

                    float dx = sSnap.Pos.x - sqCenter.x;
                    float dz = sSnap.Pos.z - sqCenter.z;
                    float horizDist = Mathf.Sqrt(dx * dx + dz * dz);
                    if (Mathf.Abs(sSnap.Pos.y - sqCenter.y) > 2.6f + horizDist * 0.55f)
                        continue;

                    float dSq = (sSnap.Pos - sqCenter).sqrMagnitude;
                    if (dSq < bestSurrDistSq && !HasArmedEnemyNearPosition(sSnap.Pos, surrLeaderFac, 10.0f))
                    {
                        bestSurrDistSq = dSq;
                        board.SurrenderedComrade = sSnap.Ref;
                    }
                }
            }

            if (board.HasActiveThreat)
            {
                board.LastKnownThreatPos = board.ThreatPosition;
                board.LastKnownThreatTime = now;
                Vector3 toThreat = board.ThreatPosition - sqCenter;
                toThreat.y = 0f;
                board.ThreatDirection = toThreat.sqrMagnitude > 0.01f ? toThreat.normalized : leader.transform.forward;

                if (!leader.IsOnVehicle() &&
                    (now >= board.NextFlankCoverScanTime ||
                     (now >= board.FlankUntil && board.FlankLeftCover == null && board.FlankRightCover == null)))
                {
                    // Cap flank octree scans to at most 1 per frame globally (and never on a SquadLeaderRoutine/snapshot frame)
                    if (AiCore.IsHeavyWorkFrame() || _lastFlankCoverOctreeFrame == Time.frameCount)
                    {
                        board.NextFlankCoverScanTime = now + 0.25f;
                    }
                    else
                    {
                        _lastFlankCoverOctreeFrame = Time.frameCount;
                        board.NextFlankCoverScanTime = now + 4.5f;
                        CombatCover prevLeft = (now < board.FlankUntil && board.FlankerLeftPtr != IntPtr.Zero) ? board.FlankLeftCover : null;
                        CombatCover prevRight = (now < board.FlankUntil && board.FlankerRightPtr != IntPtr.Zero) ? board.FlankRightCover : null;
                        FindSquadFlankCovers(sqCenter, board.ThreatPosition, board.ThreatDirection, leader.faction, out CombatCover newLeft, out CombatCover newRight);
                        board.FlankLeftCover = prevLeft != null ? prevLeft : newLeft;
                        board.FlankRightCover = prevRight != null ? prevRight : newRight;
                    }
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
            if (now >= board.DragClaimUntil)
            {
                board.DragRescuerPtr = IntPtr.Zero;
                board.DragPatientPtr = IntPtr.Zero;
            }

            _squadBlackboards[sqPtr] = board;
            return board;
            }
            finally
            {
                HitchProfiler.End(HitchBucket.GoapSquadBoard, profBoardStart);
            }
        }

        private static bool HasArmedEnemyNearPosition(Vector3 pos, string myFaction, float radius)
        {
            if (string.IsNullOrEmpty(myFaction) || radius <= 0f)
                return false;

            AiCore.EnsureWorldSnapshot();
            int sqCount = AiCore.WorldSquadCount;
            var squads = AiCore.WorldSquads;
            var soldiers = AiCore.WorldSoldiers;
            float rSq = radius * radius;
            float coarseSq = (radius + 45.0f) * (radius + 45.0f);

            for (int s = 0; s < sqCount; s++)
            {
                ref AiCore.SquadSnapshot sqSnap = ref squads[s];
                if (sqSnap.UnitCount == 0 || sqSnap.RefOnPlane || !AiCore.AreEnemyFactions(myFaction, sqSnap.Faction))
                    continue;

                if ((sqSnap.RefPos - pos).sqrMagnitude > coarseSq)
                    continue;

                int end = sqSnap.UnitStartIndex + sqSnap.UnitCount;
                for (int i = sqSnap.UnitStartIndex; i < end; i++)
                {
                    ref AiCore.SoldierSnapshot uSnap = ref soldiers[i];
                    if (uSnap.HasSurrendered || uSnap.IsInjured)
                        continue;

                    Vector3 d = uSnap.Pos - pos;
                    if (d.x * d.x + d.z * d.z <= rSq && Mathf.Abs(d.y) <= 4.5f)
                        return true;
                }
            }
            return false;
        }

        private static int _lastFlankCoverOctreeFrame = -1;

        private static void FindSquadFlankCovers(
            Vector3 squadCenter,
            Vector3 threatPos,
            Vector3 threatDir,
            string faction,
            out CombatCover leftFlankCover,
            out CombatCover rightFlankCover)
        {
            long profFlankStart = HitchProfiler.Begin();
            try
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

                if (VisionConfig.EnableSmartWallSideCover.Value &&
                    IsPositionOnExposedSideOfWall(covPos, covToThreatDir, null, out _))
                {
                    continue;
                }

                float leewardBonus = IsPositionShelteredByFrontCover(covPos, covToThreatDir, null) ? 8.0f : 0f;
                float side = Vector3.Dot(covPos - squadCenter, rightPerp);
                float score = angleOffAxis * 0.5f - Mathf.Abs(distToThreat - 18f) * 0.4f + leewardBonus;
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
            finally
            {
                HitchProfiler.End(HitchBucket.GoapFlankCovers, profFlankStart);
            }
        }
    }
}
