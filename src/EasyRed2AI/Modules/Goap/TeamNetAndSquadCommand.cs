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
                clip = AiCore.ResolveSafeVoiceClip(soldier, clip, allowYesVariation: true);

                // Local 3D spatialized audio: native Soldier.Say (0x18076144C) automatically checks Creature.IsLodded2()
                // against the active rendering camera—so first-person, vehicle, and Free/Spectator Camera (F) all hear
                // nearby bots naturally anywhere on the map with zero RPC cost!
                soldier.Say(clip, delay, -1, null);

                // In Multiplayer Co-op, only spend a Photon SyncSay packet when a remote human player's soldier is within earshot (<= 75m)!
                if (Photon.Pun.PhotonNetwork.InRoom &&
                    soldier.transform != null &&
                    AiCore.IsNearAnyRemotePlayer(soldier.transform.position, 75.0f) &&
                    AiCore.TryConsumeBotRpcBudget(0.50f, AiCore.BotRpcPriority.Low))
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
        internal struct ActiveBarragePocket
        {
            public Vector3 Position;
            public float Radius;
            public string Faction;
            public float ExpireTime;
            public bool IsFriendlyRadioStrike;
        }

        private static readonly ActiveBarragePocket[] _friendlyArtilleryZones = new ActiveBarragePocket[12];
        private static int _friendlyArtilleryZoneCount;
        private static float _nextFriendlyArtilleryRefreshTime;
        private static readonly ActiveBarragePocket[] _activeShellingPockets = new ActiveBarragePocket[8];
        private static int _shellingPocketRingHead;

        internal static ActiveBarragePocket[] FriendlyArtilleryZones => _friendlyArtilleryZones;
        internal static int FriendlyArtilleryZoneCount => _friendlyArtilleryZoneCount;
        internal static ActiveBarragePocket[] ActiveShellingPockets => _activeShellingPockets;

        internal static void ClearBarrageCoordinationCaches()
        {
            _friendlyArtilleryZoneCount = 0;
            _nextFriendlyArtilleryRefreshTime = 0f;
            _shellingPocketRingHead = 0;
            Array.Clear(_friendlyArtilleryZones, 0, _friendlyArtilleryZones.Length);
            Array.Clear(_activeShellingPockets, 0, _activeShellingPockets.Length);
        }

        private static void RefreshFriendlyArtilleryZones(float now)
        {
            if (now < _nextFriendlyArtilleryRefreshTime)
                return;

            _nextFriendlyArtilleryRefreshTime = now + 1.0f;
            _friendlyArtilleryZoneCount = 0;

            int sqCount = AiCore.WorldSquadCount;
            var squads = AiCore.WorldSquads;
            for (int s = 0; s < sqCount && _friendlyArtilleryZoneCount < _friendlyArtilleryZones.Length; s++)
            {
                ref AiCore.SquadSnapshot sqSnap = ref squads[s];
                Squad sq = sqSnap.Ref;
                if (sq == null || sq.Pointer == IntPtr.Zero || string.IsNullOrEmpty(sqSnap.Faction))
                    continue;

                try
                {
                    if (sq.RadioRequestActiveAndConfirmed() && sq.RadioRequestIsExplosiveArtillery)
                    {
                        float exp = sq.radioRequestEnd > now ? sq.radioRequestEnd : (now + 2.0f);
                        _friendlyArtilleryZones[_friendlyArtilleryZoneCount++] = new ActiveBarragePocket
                        {
                            Position = sq.radioRequestPosition,
                            Radius = 42.0f,
                            Faction = sqSnap.Faction,
                            ExpireTime = exp,
                            IsFriendlyRadioStrike = true
                        };
                    }
                }
                catch { }
            }
        }

        internal static void RecordHeavyArtilleryShellingImpact(Vector3 impactPos, float explosionRadius, string sourceFaction)
        {
            float now = Time.time;
            float pocketRadius = Mathf.Clamp(explosionRadius * 3.8f, 24.0f, 34.0f);
            float expire = now + 14.0f;

            for (int i = 0; i < _activeShellingPockets.Length; i++)
            {
                ref ActiveBarragePocket existing = ref _activeShellingPockets[i];
                if (now >= existing.ExpireTime)
                    continue;

                if ((existing.Position - impactPos).sqrMagnitude <= 24.0f * 24.0f)
                {
                    existing.Position = Vector3.Lerp(existing.Position, impactPos, 0.35f);
                    existing.Radius = Mathf.Max(existing.Radius, pocketRadius);
                    existing.ExpireTime = expire;
                    if (!string.IsNullOrEmpty(sourceFaction))
                        existing.Faction = sourceFaction;
                    return;
                }
            }

            _activeShellingPockets[_shellingPocketRingHead] = new ActiveBarragePocket
            {
                Position = impactPos,
                Radius = pocketRadius,
                Faction = sourceFaction,
                ExpireTime = expire,
                IsFriendlyRadioStrike = false
            };
            _shellingPocketRingHead = (_shellingPocketRingHead + 1) % _activeShellingPockets.Length;
        }

        internal static bool TryGetFriendlyBarrageHoldZone(
            string myFaction,
            Vector3 checkPos,
            float now,
            out Vector3 barrageCenter,
            out float safeRadius)
        {
            barrageCenter = Vector3.zero;
            safeRadius = 42.0f;
            if (string.IsNullOrEmpty(myFaction) || _friendlyArtilleryZoneCount <= 0)
                return false;

            for (int i = 0; i < _friendlyArtilleryZoneCount; i++)
            {
                ref ActiveBarragePocket z = ref _friendlyArtilleryZones[i];
                if (now >= z.ExpireTime || string.IsNullOrEmpty(z.Faction))
                    continue;

                if (!AiCore.AreSameSideFactions(myFaction, z.Faction))
                    continue;

                float dx = checkPos.x - z.Position.x;
                float dy = checkPos.y - z.Position.y;
                float dz = checkPos.z - z.Position.z;
                if (dx * dx + dz * dz <= z.Radius * z.Radius && Mathf.Abs(dy) <= 35.0f)
                {
                    barrageCenter = z.Position;
                    safeRadius = z.Radius;
                    return true;
                }
            }

            return false;
        }

        internal static bool TryGetActiveEnemyShellingPocket(
            string myFaction,
            Vector3 checkPos,
            float now,
            out Vector3 pocketCenter,
            out float pocketRadius)
        {
            pocketCenter = Vector3.zero;
            pocketRadius = 28.0f;

            for (int i = 0; i < _activeShellingPockets.Length; i++)
            {
                ref ActiveBarragePocket p = ref _activeShellingPockets[i];
                if (now >= p.ExpireTime)
                    continue;

                if (!string.IsNullOrEmpty(myFaction) &&
                    !string.IsNullOrEmpty(p.Faction) &&
                    AiCore.AreSameSideFactions(myFaction, p.Faction))
                {
                    continue;
                }

                float dx = checkPos.x - p.Position.x;
                float dy = checkPos.y - p.Position.y;
                float dz = checkPos.z - p.Position.z;
                if (dx * dx + dz * dz <= 28.0f * 28.0f && Mathf.Abs(dy) <= 22.0f)
                {
                    pocketCenter = p.Position;
                    pocketRadius = p.Radius > 0f ? p.Radius : 28.0f;
                    return true;
                }
            }

            return false;
        }

        internal static void RunTeamNetGoapCoordinator(float now)
        {
            if (VisionConfig.EnableEmergentDecisionEngine.Value)
                EmergentRuntimeBridge.RunEmergentCommandNetTick(now);
        }

        internal static void CheckAndAbortSuicidalAiCharge(Squad sq, Soldier leader, ref SquadBlackboard board, float now)
        {
            if (sq == null || leader == null || !sq.InChargeTime() || sq.isChargeSquad || sq.IsPlayerInSquad())
                return;

            FactionCommandProfile profile = GetFactionCommandProfile(leader.faction);
            if (IsTrueGyokusaiOrBanzaiCondition(in profile, sq, in board))
                return;

            bool recentHeavyDeaths = board.LastCasualtyTime > 0f && (now - board.LastCasualtyTime) <= 8.0f && board.DecayedRecentDeaths >= 1.65f;
            float leaderSupp = leader.GetSuppressionValue();
            Vector3 leaderPos = leader.transform != null ? leader.transform.position : board.SquadCenter;
            float distObj = board.HasActiveObjectiveTask ? Vector3.Distance(leaderPos, board.ObjectivePosition) : 999f;
            float distThreat = board.HasActiveThreat ? Vector3.Distance(leaderPos, board.ThreatPosition) : 999f;
            bool pinnedFar = leaderSupp >= 68.0f && distObj > 20.0f && distThreat > 20.0f;

            if ((recentHeavyDeaths || pinnedFar) && (!Photon.Pun.PhotonNetwork.InRoom || AiCore.TryConsumeBotRpcBudget(0.45f)))
            {
                sq.timed_order_end = 0f;
                try { sq.Leader_OrderAttackCurrentTask(leader); } catch { }
                TrySayTacticalVoice(leader, sq, VoiceManager.VoiceClip.keepYourHeadDown, 0.08f);
                SquadLeaderBalksCount++;
                BotActionTrace.RecordEvent(leader, BotTraceEventKind.LeaderBalk, "abort_charge_casualties", distThreat);
            }
        }

        internal static bool HasUsableAntiTankCapability(Soldier u)
        {
            if (u == null || !u.IsAlive)
                return false;

            float now = Time.time;
            AiCore.SoldierFastHandles h = AiCore.GetOrCreateSoldierHandles(u);
            if (h != null && now < h.Combat.NextAtCapCheckTime && (now + 10.0f) >= h.Combat.NextAtCapCheckTime)
                return h.Combat.CachedHasUsableAt;

            bool hasAt = false;
            try
            {
                for (int slot = 0; slot <= 2; slot++)
                {
                    GenericGun g = u.GetHeldGun(slot);
                    if (g != null && g.IsAtWeapon() && (g.GetCurrentAmmoCount() > 0 || !u.IsOutOfAmmosFor(g)))
                    {
                        hasAt = true;
                        break;
                    }
                }

                if (!hasAt && AiCore.FindThrowableGrenadeInInventory(u, findAtGrenade: true, findFragGrenade: false) != null)
                    hasAt = true;
            }
            catch { }

            if (h != null)
            {
                h.Combat.CachedHasUsableAt = hasAt;
                h.Combat.NextAtCapCheckTime = now + 4.5f + (AiCore.GetDeterministicHash(h.Ptr, 73) & 15) * 0.10f;
            }

            return hasAt;
        }

        internal static bool TryResolveBotSmokeThreatPosition(
            Soldier soldier,
            AiCore.SoldierFastHandles h,
            Vector3 myPos,
            float now,
            Spottable explicitTarget,
            out Vector3 threatPos,
            out Spottable resolvedTarget)
        {
            threatPos = Vector3.zero;
            resolvedTarget = null;
            if (soldier == null || soldier.Pointer == IntPtr.Zero)
                return false;

            if (explicitTarget != null &&
                AiCore.TryGetSpottableCenter(explicitTarget, out Vector3 expPos) &&
                !explicitTarget.IsAirVehicle() &&
                (expPos - myPos).sqrMagnitude >= 4.0f)
            {
                threatPos = expPos;
                resolvedTarget = explicitTarget;
                return true;
            }

            SoldierAI ai = (h != null && h.Ai != null) ? h.Ai : soldier.aiController;
            if (ai != null &&
                ai.visibleTarget != null &&
                AiCore.TryGetSpottableCenter(ai.visibleTarget, out Vector3 visPos) &&
                !ai.visibleTarget.IsAirVehicle() &&
                (visPos - myPos).sqrMagnitude >= 4.0f)
            {
                threatPos = visPos;
                resolvedTarget = ai.visibleTarget;
                return true;
            }

            Squad sq = soldier.joinedSquad;
            if (sq != null &&
                _squadBlackboards.TryGetValue(sq.Pointer, out SquadBlackboard smkBoard) &&
                smkBoard.HasActiveThreat &&
                (smkBoard.ThreatPosition - myPos).sqrMagnitude <= 140.0f * 140.0f &&
                (smkBoard.ThreatPosition - myPos).sqrMagnitude >= 4.0f &&
                (smkBoard.PrimaryTarget == null || !smkBoard.PrimaryTarget.IsAirVehicle()))
            {
                threatPos = smkBoard.ThreatPosition;
                if (smkBoard.PrimaryTarget != null && AiCore.IsValidAliveSpottable(smkBoard.PrimaryTarget))
                    resolvedTarget = smkBoard.PrimaryTarget;
                return true;
            }

            if (h != null &&
                h.Vision.LastKnownTarget.LastSeenTime > 0f &&
                (now - h.Vision.LastKnownTarget.LastSeenTime) <= 12.0f &&
                (h.Vision.LastKnownTarget.LastSeenPos - myPos).sqrMagnitude <= 140.0f * 140.0f &&
                (h.Vision.LastKnownTarget.LastSeenPos - myPos).sqrMagnitude >= 4.0f &&
                (h.Vision.LastKnownTarget.Target == null || !h.Vision.LastKnownTarget.Target.IsAirVehicle()))
            {
                threatPos = h.Vision.LastKnownTarget.LastSeenPos;
                if (h.Vision.LastKnownTarget.Target != null && AiCore.IsValidAliveSpottable(h.Vision.LastKnownTarget.Target))
                    resolvedTarget = h.Vision.LastKnownTarget.Target;
                return true;
            }

            string fac = h != null ? AiCore.GetCachedFaction(h, now) : soldier.faction;
            if (VisionPatches.TryGetDominantEnemyGunfireDirection(myPos, fac, 145.0f, 4.0f, out Vector3 gunDir, out float gunDist, out _) &&
                gunDir.sqrMagnitude > 0.01f)
            {
                threatPos = myPos + gunDir.normalized * Mathf.Clamp(gunDist, 12.0f, 35.0f);
                return true;
            }

            if (VisionConfig.EnableThreatSectorConcentration.Value &&
                TryGetKnownThreatSectorDirection(soldier, out Vector3 secDir, out bool secActive) &&
                secActive &&
                secDir.sqrMagnitude > 0.01f)
            {
                threatPos = myPos + secDir.normalized * 24.0f;
                return true;
            }

            if (AiCore.TryGetEnemySpawnDirection(fac, myPos, out Vector3 spawnDir) && spawnDir.sqrMagnitude > 0.01f)
            {
                threatPos = myPos + spawnDir.normalized * 24.0f;
                return true;
            }

            return false;
        }

        internal static bool TryPrepareBotSmokeThrowAim(
            Soldier soldier,
            AiCore.SoldierFastHandles h,
            VirtualItem smokeItem,
            Vector3 threatPos,
            bool isDefensive,
            float now,
            bool checkRpcBudget)
        {
            if (soldier == null || soldier.Pointer == IntPtr.Zero || smokeItem == null ||
                h == null || h.Tr == null || !AiCore.IsLocallyOwnedBot(h))
            {
                return false;
            }

            Vector3 myPos = h.Tr.position;
            if (AiCore.HasLowCeilingOverhead(myPos, 3.2f))
                return false;

            Vector3 toThreat = threatPos - myPos;
            toThreat.y = 0f;
            float flatSq = toThreat.sqrMagnitude;
            if (flatSq < 4.0f)
                return false;

            if (checkRpcBudget && !AiCore.TryConsumeBotRpcBudget(0.45f))
                return false;

            float distToThreat = Mathf.Sqrt(flatSq);
            Vector3 dirToThreat = toThreat / distToThreat;
            float maxRange = Mathf.Clamp(soldier.GetGrenadeThrowRange(smokeItem), 10.0f, 32.0f);
            float throwDist = isDefensive
                ? Mathf.Clamp(distToThreat * 0.45f, 5.0f, Mathf.Min(maxRange * 0.72f, 14.0f))
                : Mathf.Clamp(distToThreat * 0.65f, 8.0f, maxRange * 0.88f);

            Vector3 landingPos = myPos + dirToThreat * throwDist;
            landingPos.y = myPos.y + Mathf.Clamp((threatPos.y - myPos.y) * (throwDist / distToThreat), -2.5f, 1.2f);

            h.Combat.PendingSmokeAimPoint = landingPos;
            h.Combat.PendingSmokeAimUntil = now + 2.6f;
            ItemObject prefabItem = smokeItem.GetItemPrefab();
            h.Combat.PendingSmokePrefabPtr = (prefabItem != null && prefabItem.gameObject != null)
                ? prefabItem.gameObject.Pointer
                : IntPtr.Zero;
            return true;
        }

        internal static bool TryThrowTacticalSmokeToward(
            Soldier soldier,
            VirtualItem smokeItem,
            Vector3 threatPos,
            Spottable preferredTarget,
            bool isDefensive,
            float now)
        {
            if (soldier == null || soldier.Pointer == IntPtr.Zero || smokeItem == null)
                return false;

            AiCore.SoldierFastHandles h = AiCore.GetOrCreateSoldierHandles(soldier);
            if (!TryPrepareBotSmokeThrowAim(soldier, h, smokeItem, threatPos, isDefensive, now, checkRpcBudget: true))
                return false;

            Spottable safeTarget = (preferredTarget != null &&
                                    AiCore.IsValidAliveSpottable(preferredTarget) &&
                                    !preferredTarget.IsAirVehicle())
                ? preferredTarget
                : soldier.Cast<Spottable>();
            soldier.Throw(smokeItem, safeTarget);
            return true;
        }

        private static void TryPopSquadRetreatSmoke(Squad sq, Soldier leader, ref SquadBlackboard board, float now)
        {
            if (!board.HasActiveThreat || now < board.SmokeActiveUntil)
                return;
            if (board.PrimaryTarget != null && board.PrimaryTarget.IsAirVehicle())
                return;

            var units = sq.units;
            int count = units != null ? units.Count : 0;
            for (int i = -1; i < count; i++)
            {
                Soldier cand = (i < 0) ? leader : units[i];
                if (cand == null || !cand.IsAlive || cand.IsPlayer() || cand.HasSurrended() ||
                    AiCore.IsSoldierInVehicleOrTransition(cand) || cand.inventory == null || cand.transform == null)
                {
                    continue;
                }
                if (cand.isInHandsAnimation || cand.isInBodyAnimation || now <= cand.throw_end_time + 4.0f)
                    continue;

                VirtualItem smoke = cand.inventory.FindItemOfType<VirtualSmokeGrenade>();
                if (smoke == null)
                    continue;

                Vector3 cPos = cand.transform.position;
                float d = Vector3.Distance(cPos, board.ThreatPosition);
                if (d >= 8.0f && d <= 95.0f &&
                    TryThrowTacticalSmokeToward(cand, smoke, board.ThreatPosition, board.PrimaryTarget, isDefensive: true, now))
                {
                    AiCore.SoldierFastHandles candH = AiCore.GetOrCreateSoldierHandles(cand);
                    if (candH != null)
                        candH.Goap.NextSmokeGrenadeTime = now + 32.0f;
                    board.SmokeActiveUntil = now + 22.0f;
                    board.LastSmokeTargetPos = board.ThreatPosition;
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
            bool hasFbWater = AiCore.TryGetActiveWaterLevel(out float fbWaterLevel);
            bool nearWaterline = hasFbWater && fromPos.y <= fbWaterLevel + 4.5f;
            bool onDryAboveWater = hasFbWater && fromPos.y > fbWaterLevel + 0.55f;

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

                        float covScore = (cPos - searchAnchor).sqrMagnitude;
                        if (hasFbWater)
                        {
                            if (cPos.y <= fbWaterLevel + 0.35f)
                                covScore += onDryAboveWater ? 2500.0f : 900.0f;
                            else if (nearWaterline)
                            {
                                if (cPos.y > fromPos.y + 0.35f)
                                    covScore *= 0.65f;
                                else if (cPos.y < fromPos.y - 0.35f)
                                    covScore += 400.0f;
                            }
                        }

                        if (covScore < bestCovDistSq)
                        {
                            bestCovDistSq = covScore;
                            fallbackPos = cPos;
                        }
                    }
                    if (bestCovDistSq < float.MaxValue)
                        return true;
                }
            }

            // 3. Snap fallback anchor onto walkable dry ground (discouraging seaward retreats by preferring dry
            //    lateral-shore arcs first, and only falling back to shallow surf when a wading soldier has no dry ground).
            float anchorFlatDist = new Vector2(searchAnchor.x - fromPos.x, searchAnchor.z - fromPos.z).magnitude;
            float fbLiftY = Mathf.Max(8.0f, anchorFlatDist * 1.50f);
            float maxNonTerrainDy = Mathf.Clamp(anchorFlatDist * 0.08f, 2.0f, 2.4f);
            if (AiCore.TrySnapWalkableGroundProbe(fromPos, searchAnchor, fbLiftY, fbLiftY * 2.05f, 0.55f, maxNonTerrainDy, hasFbWater, fbWaterLevel, out Vector3 snappedFb))
            {
                fallbackPos = snappedFb;
                return true;
            }

            if (hasFbWater)
            {
                Vector3 right = new Vector3(awayDir.z, 0f, -awayDir.x);
                float sideFirst = ((AiCore.GetDeterministicHash(soldier.Pointer, 19) & 1) == 0) ? 1f : -1f;
                float latDist = Mathf.Lerp(minDist, maxDist, 0.55f);
                const float cos65 = 0.4226f;
                const float sin65 = 0.9063f;
                for (int k = 0; k < 2; k++)
                {
                    float sSign = k == 0 ? sideFirst : -sideFirst;
                    Vector3 latProbe = fromPos + (awayDir * cos65 + right * (sSign * sin65)) * latDist;
                    if (AiCore.TrySnapWalkableGroundProbe(fromPos, latProbe, fbLiftY, fbLiftY * 2.05f, 0.55f, maxNonTerrainDy, true, fbWaterLevel, out Vector3 latSnapped))
                    {
                        fallbackPos = latSnapped;
                        return true;
                    }
                }

                // Last resort for soldiers already wading: allow shallow surf fallback instead of stranding them.
                if (!onDryAboveWater &&
                    AiCore.TrySnapWalkableGroundProbe(fromPos, searchAnchor, fbLiftY, fbLiftY * 2.05f, 0.55f, maxNonTerrainDy, false, fbWaterLevel, out Vector3 wetSnapped) &&
                    wetSnapped.y >= fbWaterLevel - 0.55f)
                {
                    fallbackPos = wetSnapped;
                    return true;
                }
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
            Spottable smokeTarget = null;
            bool hasGenuineThreat = TryResolveBotSmokeThreatPosition(soldier, fbHandles, myPos, now, null, out Vector3 resolvedThreatPos, out Spottable resolvedSpottable);
            if (hasGenuineThreat)
            {
                threatPos = resolvedThreatPos;
                smokeTarget = resolvedSpottable;
            }

            if (!TryComputeTacticalFallbackPosition(soldier, sq, myPos, threatPos, 20.0f, 34.0f, out Vector3 fbDest))
                return false;

            // Pop personal smoke grenade toward the genuine threat before sprinting back!
            if (hasGenuineThreat &&
                soldier.inventory != null &&
                !soldier.isInHandsAnimation &&
                !soldier.isInBodyAnimation &&
                now > soldier.throw_end_time + 4.0f)
            {
                VirtualItem smoke = soldier.inventory.FindItemOfType<VirtualSmokeGrenade>();
                if (smoke != null &&
                    TryThrowTacticalSmokeToward(soldier, smoke, threatPos, smokeTarget, isDefensive: true, now))
                {
                    if (fbHandles != null)
                        fbHandles.Goap.NextSmokeGrenadeTime = now + 32.0f;
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

        internal static void NotifySquadMemberKilled(Soldier victim)
        {
            NotifyFriendlyHitOrCasualty(victim, Vector3.zero, false, isFatalOrDown: true);
        }

        internal static void NotifyFriendlyHitOrCasualty(
            Soldier victim,
            Vector3 shooterOrOrigin,
            bool hasShooterOrigin,
            bool isFatalOrDown)
        {
            if (victim == null || victim.Pointer == IntPtr.Zero)
                return;

            Transform vTr = victim.transform;
            if (vTr == null)
                return;

            IntPtr victimPtr = victim.Pointer;
            Vector3 hitPos = vTr.position;
            string victimFac = AiCore.GetCachedFaction(victim);
            if (string.IsNullOrEmpty(victimFac))
                return;

            float now = Time.time;
            if (VisionConfig.EnableEmergentDecisionEngine.Value)
            {
                EmergentRuntimeBridge.NotifyFriendlyHit(victim, shooterOrOrigin, hasShooterOrigin, isFatalOrDown);
            }
            Squad victimSq = victim.joinedSquad;
            IntPtr victimSqPtr = (victimSq != null && victimSq.Pointer != IntPtr.Zero) ? victimSq.Pointer : IntPtr.Zero;

            if (isFatalOrDown && victimSqPtr != IntPtr.Zero && !AiCore.IsTrueVehicleCrewSquad(victimSq))
            {
                _squadBlackboards.TryGetValue(victimSqPtr, out SquadBlackboard board);
                float dt = board.LastDeathDecayUpdateTime > 0f ? Mathf.Max(0f, now - board.LastDeathDecayUpdateTime) : 0f;
                board.DecayedRecentDeaths = board.DecayedRecentDeaths * Mathf.Exp(-dt * 0.05f) + 1.0f;
                board.LastDeathDecayUpdateTime = now;
                board.LastCasualtyPos = hitPos;
                board.LastCasualtyTime = now;
                _squadBlackboards[victimSqPtr] = board;
            }

            if (!isFatalOrDown &&
                victim.IsAlive &&
                AiCore.IsLocallyOwnedBot(victim) &&
                VisionConfig.EnableTacticalVoiceCallouts.Value &&
                (AiCore.GetDeterministicHash(victimPtr, Mathf.RoundToInt(now * 10f)) & 15) < 7)
            {
                VoiceManager.VoiceClip hitClip = victim.isBleeding
                    ? VoiceManager.VoiceClip.iVeBeenHit
                    : VoiceManager.VoiceClip.imUnderFire;
                TrySayTacticalVoice(victim, victimSq, hitClip, 0.06f);
            }

            // Cross-squad witness reaction: nearby friendly infantry (<= 14m same squad, <= 12m adjacent squads)
            // flinch under realistic suppression caps, cancel open-ground halts, and replan into cover!
            AiCore.EnsureWorldSnapshot();
            int wCount = AiCore.WorldSoldierCount;
            var soldiers = AiCore.WorldSoldiers;
            int addCapBase = isFatalOrDown ? 28 : 22;

            for (int i = 0; i < wCount; i++)
            {
                ref AiCore.SoldierSnapshot s = ref soldiers[i];
                if (s.IsPlayer ||
                    s.Ref == null ||
                    s.Ptr == victimPtr ||
                    s.IsOnVehicle ||
                    s.IsInjured ||
                    s.HasSurrendered)
                {
                    continue;
                }

                if (!AiCore.AreSameSideFactions(victimFac, s.Faction))
                    continue;

                bool sameSquad = victimSqPtr != IntPtr.Zero && s.SquadPtr == victimSqPtr;
                float maxR = sameSquad ? 14.0f : 12.0f;
                if ((s.Pos - hitPos).sqrMagnitude > maxR * maxR)
                    continue;

                AiCore.SoldierFastHandles ah = s.Handles ?? AiCore.GetOrCreateSoldierHandles(s.Ref);
                if (ah == null || now < ah.NextWitnessReactTime || !AiCore.IsLocallyOwnedBot(ah))
                    continue;

                ah.NextWitnessReactTime = now + 3.5f;

                int addSupp = Mathf.Min(addCapBase, Mathf.Max(0, 58 - Mathf.RoundToInt(s.Ref.GetSuppressionValue())));
                if (addSupp > 0)
                {
                    s.Ref.Suppress(addSupp, null);
                }

                ah.Emergent.NextPlanTime = Mathf.Min(ah.Emergent.NextPlanTime, now + 0.08f + (i % 5) * 0.05f);
                if (ah.Combat.FireAndMovement.InFiringHalt)
                {
                    ah.Combat.FireAndMovement.InFiringHalt = false;
                }

                if (hasShooterOrigin &&
                    s.SquadPtr != IntPtr.Zero &&
                    _squadBlackboards.TryGetValue(s.SquadPtr, out SquadBlackboard wBoard) &&
                    (!wBoard.HasActiveThreat || now - wBoard.LastKnownThreatTime > 2.0f))
                {
                    wBoard.LastKnownThreatPos = shooterOrOrigin;
                    wBoard.LastKnownThreatTime = now;
                    Vector3 d = shooterOrOrigin - s.Pos;
                    d.y = 0f;
                    if (d.sqrMagnitude > 0.01f)
                        wBoard.ThreatDirection = d.normalized;
                    _squadBlackboards[s.SquadPtr] = wBoard;
                }
            }
        }

        // =====================================================================
        // Tier 2: Squad Tactical Blackboard (Computed 1x per Squad per 0.6s)
        // =====================================================================
        private static SquadBlackboard GetOrUpdateSquadBlackboardBudgeted(
            Squad sq,
            Soldier fallbackSoldier,
            float now,
            ref int refreshedBoardsThisTick,
            int maxRefreshPerTick = 3)
        {
            if (sq == null || sq.Pointer == IntPtr.Zero)
                return default;

            bool hadCached = _squadBlackboards.TryGetValue(sq.Pointer, out SquadBlackboard prevBoard);
            bool needsRefresh = !hadCached || now >= prevBoard.NextRefreshTime;
            bool allowRefresh = refreshedBoardsThisTick < maxRefreshPerTick;
            SquadBlackboard result = GetOrUpdateSquadBlackboard(sq, fallbackSoldier, allowRefresh);
            if (needsRefresh && (!hadCached || allowRefresh))
                refreshedBoardsThisTick++;
            return result;
        }

        internal static void TriggerSquadLeaderLossShock(Squad sq, ref SquadBlackboard board, IntPtr fallenLeaderPtr, float now)
        {
            if (sq == null || sq.Pointer == IntPtr.Zero)
                return;

            float peak = 0.13f;
            if (fallenLeaderPtr != IntPtr.Zero &&
                AiCore.TryGetSoldierHandles(fallenLeaderPtr, out AiCore.SoldierFastHandles oldH) &&
                oldH.Veterancy.Record.Initialized)
            {
                if (IsLeaderPersonality(oldH.Veterancy.Record.Personality))
                    peak += 0.03f;
                if (GetVeteranTierByPtr(fallenLeaderPtr) >= 2)
                    peak += 0.02f;
            }

            peak = Mathf.Clamp(peak, 0.12f, 0.18f);
            board.LeaderShockPeak = Mathf.Max(board.LeaderShockPeak, peak);
            board.LeaderShockUntil = Mathf.Max(board.LeaderShockUntil, now + 20.0f);
        }

        internal static void NotifySquadLeaderLost(Soldier victim, float now)
        {
            if (ReferenceEquals(victim, null) || victim == null || victim.Pointer == IntPtr.Zero)
                return;

            Squad sq = victim.joinedSquad;
            if (ReferenceEquals(sq, null) || sq == null || sq.Pointer == IntPtr.Zero)
                return;

            if (_squadBlackboards.TryGetValue(sq.Pointer, out SquadBlackboard board) &&
                (victim.IsSquadLeader() || board.LastAliveLeaderPtr == victim.Pointer))
            {
                TriggerSquadLeaderLossShock(sq, ref board, victim.Pointer, now);
                board.LastAliveLeaderPtr = IntPtr.Zero;
                board.LeaderBraveryAura = 0f;
                board.LeaderRoseToOccasion = false;
                board.PendingLeaderSuccession = true;
                _squadBlackboards[sq.Pointer] = board;
            }
        }

        internal static SquadBlackboard GetOrUpdateSquadBlackboard(Squad sq, Soldier fallbackSoldier, bool allowRefresh = true)
        {
            if (sq == null || sq.Pointer == IntPtr.Zero)
            {
                return default;
            }

            IntPtr sqPtr = sq.Pointer;
            float now = Time.time;
            if (_squadBlackboards.TryGetValue(sqPtr, out SquadBlackboard board) &&
                (now < board.NextRefreshTime || !allowRefresh))
            {
                return board;
            }

            long profBoardStart = HitchProfiler.Begin();
            try
            {
            board.NextRefreshTime = now + 0.60f + (AiCore.GetDeterministicHash(sqPtr, 19) & 15) * 0.03f;
            if (board.LastDeathDecayUpdateTime > 0f)
            {
                float deathDt = Mathf.Max(0f, now - board.LastDeathDecayUpdateTime);
                board.DecayedRecentDeaths *= Mathf.Exp(-deathDt * 0.05f);
                if (board.DecayedRecentDeaths < 0.05f)
                    board.DecayedRecentDeaths = 0f;
            }
            board.LastDeathDecayUpdateTime = now;

            Soldier actualLeader = sq.Leader;
            if (board.LastAliveLeaderPtr != IntPtr.Zero &&
                (actualLeader == null ||
                 actualLeader.Pointer != board.LastAliveLeaderPtr ||
                 !actualLeader.IsAlive ||
                 actualLeader.IsInjured ||
                 actualLeader.HasSurrended()))
            {
                bool prevDownOrLost = !AiCore.TryGetSoldierHandles(board.LastAliveLeaderPtr, out AiCore.SoldierFastHandles prevH) ||
                                      prevH == null ||
                                      prevH.Ref == null ||
                                      !prevH.Ref.IsAlive ||
                                      prevH.Ref.IsInjured ||
                                      prevH.Ref.HasSurrended();
                if (prevDownOrLost)
                {
                    TriggerSquadLeaderLossShock(sq, ref board, board.LastAliveLeaderPtr, now);
                    board.PendingLeaderSuccession = true;
                }
            }

            if (actualLeader != null && actualLeader.IsAlive && !actualLeader.IsInjured && !actualLeader.HasSurrended())
            {
                board.LastAliveLeaderPtr = actualLeader.Pointer;
                if (board.PendingLeaderSuccession)
                {
                    TryEvaluateRiseToTheOccasion(actualLeader, sq, ref board, now, hasBoard: true);
                    board.PendingLeaderSuccession = false;
                }
                board.LeaderBraveryAura = ComputeLeaderBraveryAura(actualLeader);
                board.LeaderRoseToOccasion = HasSoldierRisenToOccasion(actualLeader);
            }
            else
            {
                board.LeaderBraveryAura = 0f;
                board.LeaderRoseToOccasion = false;
            }

            Soldier leader = actualLeader != null && actualLeader.IsAlive ? actualLeader : fallbackSoldier;
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
            float capDelta = 0f;
            bool hasPrevCapSample = false;

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
                    if (board.PrevObjectiveTaskPtr == curTask.Pointer)
                    {
                        capDelta = board.ObjectiveCaptureProgress - board.PrevObjectiveCaptureProgress;
                        hasPrevCapSample = true;
                    }
                    else
                    {
                        board.ObjectiveTickingAgainstUntil = 0f;
                    }
                    board.PrevObjectiveTaskPtr = curTask.Pointer;
                    board.PrevObjectiveCaptureProgress = board.ObjectiveCaptureProgress;
                }
                else
                {
                    board.PrevObjectiveTaskPtr = IntPtr.Zero;
                    board.PrevObjectiveCaptureProgress = 0f;
                    board.ObjectiveTickingAgainstUntil = 0f;
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
            board.CloserFriendlyUnitsToObjective = 0;

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
                        if (board.SurrenderedComrade == null &&
                            AiCore.IsTrueSurrenderedPrisoner(u) &&
                            (!AiCore.TryGetSoldierHandles(u.Pointer, out AiCore.SoldierFastHandles uSurrH) || !uSurrH.Goap.FootStuck.SurrenderedFromTrap))
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
                    bool uOnVeh = u.IsOnVehicle();
                    if (uAi != null &&
                        uAi.visibleTarget != null &&
                        (uOnVeh || !uAi.visibleTarget.IsAirVehicle()) &&
                        AiCore.TryGetSpottableCenter(uAi.visibleTarget, out tPos))
                    {
                        vt = uAi.visibleTarget;
                    }
                    else if (uOnVeh)
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
                        bool lkIsAir = lk.Target != null && !uOnVeh && lk.Target.IsAirVehicle();
                        if (!lkIsAir)
                        {
                            float dist = Vector3.Distance(sqCenter, lk.LastSeenPos);
                            float score = 120f - dist;
                            if (score > bestThreatScore)
                            {
                                bestThreatScore = score;
                                board.HasActiveThreat = true;
                                board.ThreatIsArmor = false;
                                board.PrimaryTarget = (lk.Target != null && AiCore.IsValidAliveSpottable(lk.Target)) ? lk.Target : null;
                                board.ThreatPosition = lk.LastSeenPos;
                            }
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
                float myObjDistSq = board.HasActiveObjectiveTask ? (sqCenter - objCenter).sqrMagnitude : float.MaxValue;
                IntPtr mySqPtr = sq.Pointer;
                for (int s = 0; s < wSqCount; s++)
                {
                    ref AiCore.SquadSnapshot sSnap = ref wSquads[s];
                    if (sSnap.UnitCount == 0 || sSnap.RefOnPlane || string.IsNullOrEmpty(sSnap.Faction))
                        continue;

                    bool sameSide = AiCore.AreSameSideFactions(leaderFac, sSnap.Faction);
                    if (board.HasActiveObjectiveTask && sameSide && sSnap.Ptr != mySqPtr)
                    {
                        float otherObjDistSq = (sSnap.RefPos - objCenter).sqrMagnitude;
                        if (otherObjDistSq <= 95.0f * 95.0f && otherObjDistSq < myObjDistSq * 0.72f)
                        {
                            board.CloserFriendlyUnitsToObjective += sSnap.UnitCount;
                        }
                    }

                    bool nearSquad = (sSnap.RefPos - sqCenter).sqrMagnitude <= coarseSqRadius;
                    bool nearObj = countObjCircle && (sSnap.RefPos - objCenter).sqrMagnitude <= (objRad + 55.0f) * (objRad + 55.0f);
                    if (!nearSquad && !nearObj)
                        continue;

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

            if (hasPrevCapSample && board.HasActiveObjectiveTask)
            {
                bool tickingAgainst = board.IsDefendingObjective
                    ? (capDelta > 0.003f || (board.ObjectiveCaptureProgress >= 0.08f && capDelta >= -0.002f))
                    : (capDelta < -0.003f || (board.FriendlyUnitsInsideObjective > 0 && board.ObjectiveCaptureProgress < 0.96f && capDelta <= 0.002f));
                if (tickingAgainst)
                    board.ObjectiveTickingAgainstUntil = now + 8.0f;
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
                    if (!sSnap.HasSurrendered ||
                        !AiCore.AreSameSideFactions(surrLeaderFac, sSnap.Faction) ||
                        sSnap.Ref == null ||
                        (sSnap.Handles != null && sSnap.Handles.Goap.FootStuck.SurrenderedFromTrap))
                    {
                        continue;
                    }

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

        internal static bool IsSquadActivelyRetreating(IntPtr sqPtr, float now)
        {
            return sqPtr != IntPtr.Zero &&
                   _squadBlackboards.TryGetValue(sqPtr, out SquadBlackboard b) &&
                   b.NetDirective == SquadNetDirective.Regroup_SectorWithdrawal;
        }
    }
}
