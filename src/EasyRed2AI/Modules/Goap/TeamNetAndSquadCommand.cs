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
                AiCore.IsAnyHumanPlayerSoldier(soldier) ||
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

        private static ActiveBarragePocket[] _friendlyArtilleryZones = new ActiveBarragePocket[16];
        private static int _friendlyArtilleryZoneCount;
        private static float _nextFriendlyArtilleryRefreshTime;
        private static readonly ActiveBarragePocket[] _activeShellingPockets = new ActiveBarragePocket[16];
        private static int _shellingPocketRingHead;
        private static float _nextLocalArtyWarnToastTime;
        private static float _nextCoopArtyWarnChatTime;
        private static Vector3 _lastCoopArtyWarnPos;

        internal static ActiveBarragePocket[] FriendlyArtilleryZones => _friendlyArtilleryZones;
        internal static int FriendlyArtilleryZoneCount => _friendlyArtilleryZoneCount;
        internal static ActiveBarragePocket[] ActiveShellingPockets => _activeShellingPockets;

        internal static void ClearBarrageCoordinationCaches()
        {
            _friendlyArtilleryZoneCount = 0;
            _nextFriendlyArtilleryRefreshTime = 0f;
            _shellingPocketRingHead = 0;
            _nextLocalArtyWarnToastTime = 0f;
            _nextCoopArtyWarnChatTime = 0f;
            _lastCoopArtyWarnPos = Vector3.zero;
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
            for (int s = 0; s < sqCount; s++)
            {
                ref AiCore.SquadSnapshot sqSnap = ref squads[s];
                Squad sq = sqSnap.Ref;
                if (sq == null || sq.Pointer == IntPtr.Zero || string.IsNullOrEmpty(sqSnap.Faction))
                    continue;

                try
                {
                    if (sq.RadioRequestActiveAndConfirmed() && sq.RadioRequestIsExplosiveArtillery)
                    {
                        if (!AiCore.TryEnsureArrayCapacity(ref _friendlyArtilleryZones, _friendlyArtilleryZoneCount + 1, 64))
                            break;

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

            if (VisionConfig.EnableMod.Value && VisionConfig.EnableFriendlyBlastWarnings.Value && _friendlyArtilleryZoneCount > 0)
            {
                NotifyPlayersInFriendlyArtilleryZones(now);
            }
        }

        private static void NotifyPlayersInFriendlyArtilleryZones(float now)
        {
            Soldier localPlayer = PlayerController.currentController != null ? PlayerController.currentController.ControlledCharacter : null;
            if (localPlayer != null && localPlayer.IsAlive && localPlayer.transform != null)
            {
                Vector3 pPos = localPlayer.transform.position;
                string pFac = AiCore.GetCachedFaction(localPlayer);
                for (int i = 0; i < _friendlyArtilleryZoneCount; i++)
                {
                    ref ActiveBarragePocket z = ref _friendlyArtilleryZones[i];
                    if (now >= z.ExpireTime || !AiCore.AreSameSideFactions(pFac, z.Faction))
                        continue;

                    float dx = pPos.x - z.Position.x;
                    float dz = pPos.z - z.Position.z;
                    float warnR = z.Radius + 6.0f;
                    float dSq = dx * dx + dz * dz;
                    if (dSq <= warnR * warnR && now >= _nextLocalArtyWarnToastTime)
                    {
                        _nextLocalArtyWarnToastTime = now + 10.0f;
                        try
                        {
                            PlayerController.HintToPlayer(
                                $"HQ Radio: DANGER CLOSE \u2014 Friendly artillery inbound ({Mathf.RoundToInt(Mathf.Sqrt(dSq))}m), clear area!",
                                false);
                        }
                        catch { }
                        break;
                    }
                }
            }

            if (!AiCore.IsInMultiplayerRoom() || !Photon.Pun.PhotonNetwork.IsMasterClient)
                return;

            AiCore.EnsureWorldSnapshot();
            int solCount = AiCore.WorldSoldierCount;
            var soldiers = AiCore.WorldSoldiers;
            for (int i = 0; i < _friendlyArtilleryZoneCount; i++)
            {
                ref ActiveBarragePocket z = ref _friendlyArtilleryZones[i];
                if (now >= z.ExpireTime || string.IsNullOrEmpty(z.Faction))
                    continue;

                float warnR = z.Radius + 6.0f;
                float warnRSq = warnR * warnR;
                Soldier threatenedHuman = null;
                for (int s = 0; s < solCount; s++)
                {
                    Soldier sol = soldiers[s].Ref;
                    if (sol == null || !sol.IsAlive || !AiCore.IsAnyHumanPlayerSoldier(sol))
                        continue;
                    if (!AiCore.AreSameSideFactions(soldiers[s].Faction, z.Faction))
                        continue;

                    Vector3 sp = soldiers[s].Pos;
                    float dx = sp.x - z.Position.x;
                    float dz = sp.z - z.Position.z;
                    if (dx * dx + dz * dz <= warnRSq)
                    {
                        threatenedHuman = sol;
                        break;
                    }
                }

                if (threatenedHuman == null)
                    continue;

                if (now >= _nextCoopArtyWarnChatTime || (z.Position - _lastCoopArtyWarnPos).sqrMagnitude > 25f * 25f)
                {
                    _nextCoopArtyWarnChatTime = now + 14.0f;
                    _lastCoopArtyWarnPos = z.Position;
                    if (BattleLoader.instance != null && BattleLoader.instance.photonView != null && Photon.Pun.PhotonNetwork.LocalPlayer != null)
                    {
                        var rpcArgs = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Il2CppSystem.Object>(3);
                        rpcArgs[0] = Photon.Pun.PhotonNetwork.LocalPlayer;
                        rpcArgs[1] = (Il2CppSystem.String)z.Faction;
                        rpcArgs[2] = (Il2CppSystem.String)"[HQ] DANGER CLOSE: Friendly artillery inbound near your position — clear area!";
                        BattleLoader.instance.photonView.RPC("Chat_NewMessageFaction", Photon.Pun.RpcTarget.All, rpcArgs);
                    }

                    Vector3 hp = threatenedHuman.transform.position;
                    for (int s = 0; s < solCount; s++)
                    {
                        Soldier ally = soldiers[s].Ref;
                        if (ally == null || !ally.IsAlive || !ally.IsAI() || AiCore.IsAnyHumanPlayerSoldier(ally))
                            continue;
                        if (!AiCore.AreSameSideFactions(soldiers[s].Faction, z.Faction))
                            continue;
                        if ((soldiers[s].Pos - hp).sqrMagnitude <= 22f * 22f)
                        {
                            TrySayTacticalVoice(ally, ally.joinedSquad, VoiceManager.VoiceClip.keepYourHeadDown, 0.15f);
                            break;
                        }
                    }
                }
                break;
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
            if (sq == null || leader == null)
                return;

            if (!sq.InChargeTime())
            {
                board.IsAiChargeOrdered = false;
                return;
            }

            if (sq.isChargeSquad || sq.IsPlayerInSquad())
                return;

            FactionCommandProfile profile = GetFactionCommandProfile(leader.faction);
            if (IsTrueGyokusaiOrBanzaiCondition(in profile, sq, in board))
                return;

            bool orderedChargeCas = board.IsAiChargeOrdered &&
                                    board.DecayedRecentDeaths >= board.AiChargeBaselineDeaths + 1.65f;
            bool recentHeavyDeaths = board.LastCasualtyTime > 0f && (now - board.LastCasualtyTime) <= 8.0f && board.DecayedRecentDeaths >= 1.65f;
            float leaderSupp = leader.GetSuppressionValue();
            Vector3 leaderPos = leader.transform != null ? leader.transform.position : board.SquadCenter;
            float distObj = board.HasActiveObjectiveTask ? Vector3.Distance(leaderPos, board.ObjectivePosition) : 999f;
            float distThreat = board.HasActiveThreat ? Vector3.Distance(leaderPos, board.ThreatPosition) : 999f;
            bool pinnedFar = leaderSupp >= 68.0f && distObj > 20.0f && distThreat > 20.0f;

            if ((orderedChargeCas || recentHeavyDeaths || pinnedFar) && (!Photon.Pun.PhotonNetwork.InRoom || AiCore.TryConsumeBotRpcBudget(0.45f)))
            {
                board.IsAiChargeOrdered = false;
                board.NextAiSquadChargeTime = Mathf.Max(board.NextAiSquadChargeTime, now + 40.0f);
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
                if (cand == null || !cand.IsAlive || AiCore.IsAnyHumanPlayerSoldier(cand) || cand.HasSurrended() ||
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

        internal static SquadBlackboard GetSquadBlackboardBudgeted(Squad sq, Soldier fallbackSoldier, float now, ref int refreshBudget)
        {
            if (sq == null || sq.Pointer == IntPtr.Zero)
                return default;

            if (_squadBlackboards.TryGetValue(sq.Pointer, out SquadBlackboard cached))
            {
                if (now < cached.NextRefreshTime || refreshBudget <= 0)
                    return cached;

                refreshBudget--;
                return GetOrUpdateSquadBlackboard(sq, fallbackSoldier, allowRefresh: true);
            }

            if (refreshBudget <= 0)
                return default;

            refreshBudget--;
            return GetOrUpdateSquadBlackboard(sq, fallbackSoldier, allowRefresh: true);
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

            if (board.FirstSeenTime <= 0f || now < board.FirstSeenTime)
                board.FirstSeenTime = now;
            bool inSpawnGrace = (now - board.FirstSeenTime) < 6.0f;

            bool playerInSquadNow = sq.IsPlayerInSquad();
            bool playerJustJoined = playerInSquadNow && !board.PlayerWasInSquad;
            if (playerJustJoined)
            {
                board.PlayerWasInSquad = true;
                board.PlayerJoinedSquadTime = now;
                board.EverNotifiedRadioCutoff = false;
            }
            else if (!playerInSquadNow)
            {
                board.PlayerWasInSquad = false;
                board.EverNotifiedRadioCutoff = false;
            }
            bool inPlayerJoinGrace = playerInSquadNow && (board.PlayerJoinedSquadTime <= 0f || (now - board.PlayerJoinedSquadTime) < 6.0f);

            // Check Radioman status & detect sudden loss or field recovery of squad Radioman
            Soldier rad = sq.GetRadioman();
            bool hasFootRadiomanNow = rad != null && rad.IsAlive && !rad.HasSurrended();
            bool hasAliveRadNow = hasFootRadiomanNow || IsMountedOnGroundVehicle(leader);
            bool isFirstRadioInit = board.RadioNetToastInitState == 0;
            if (isFirstRadioInit || inSpawnGrace)
            {
                board.PreviouslyHadAliveRadioman = hasAliveRadNow;
                board.PreviouslyHadFootRadioman = hasFootRadiomanNow;
            }
            bool radiomanJustDied = !isFirstRadioInit && !inSpawnGrace && board.PreviouslyHadFootRadioman && !hasFootRadiomanNow && !hasAliveRadNow;
            bool radiomanJustRecovered = !isFirstRadioInit && !inSpawnGrace && !inPlayerJoinGrace && !board.PreviouslyHadFootRadioman && hasFootRadiomanNow;
            bool hadRecordedRadioLoss = board.LastRadiomanLostTime > 0f;

            if (radiomanJustDied)
            {
                _initialHqSpawnBriefingConsumed.Add(sqPtr);
                board.LastRadiomanLostTime = now;
                board.NetDirective = SquadNetDirective.Disconnected_Autonomous;
                RadioNetCutoffsCount++;
                TrySayTacticalVoice(leader, sq, VoiceManager.VoiceClip.radiomanIsDead, 0.35f);
                if (VisionConfig.DebugLogging.Value)
                {
                    AiCore.LogDebug("RadioNet", $"{leader.faction} squad '{sq.squadName}' lost its Radioman! Cut off from distant Command Net.");
                }
            }
            else if (hasAliveRadNow)
            {
                _initialHqSpawnBriefingConsumed.Add(sqPtr);
                board.LastRadiomanLostTime = 0f;
            }
            board.PreviouslyHadAliveRadioman = hasAliveRadNow;
            board.PreviouslyHadFootRadioman = hasFootRadiomanNow;

            board.IsConnectedToRadioNet = IsSquadConnectedToRadioNet(sq);
            if (!board.IsConnectedToRadioNet)
            {
                board.NetDirective = SquadNetDirective.Disconnected_Autonomous;
            }

            // Debounced Radio Net Disconnected / Regained Toasts for Player Squads (Local HUD + Host-Assist Co-op Chat)
            if (VisionConfig.EnableRadiomanIntelNet.Value)
            {
                bool liveRadioLink = HasLiveRadioOrRelayLink(sq);
                if (isFirstRadioInit || inSpawnGrace || playerJustJoined)
                {
                    board.RadioNetToastInitState = (liveRadioLink || hasAliveRadNow) ? (byte)2 : (byte)1;
                    board.RadioRelayPendingFlipSince = 0f;
                    if (playerJustJoined)
                        board.NextRadioNetStatusToastTime = Mathf.Max(board.NextRadioNetStatusToastTime, now + 6.0f);
                }
                else if (playerInSquadNow)
                {
                    if (radiomanJustDied)
                    {
                        board.RadioNetToastInitState = liveRadioLink ? (byte)2 : (byte)1;
                        board.RadioRelayPendingFlipSince = 0f;
                        board.NextRadioNetStatusToastTime = now + 16.0f;
                        board.EverNotifiedRadioCutoff = !liveRadioLink;
                        string lostMsg = !liveRadioLink
                            ? "Squad Radio: Radioman down \u2014 squad cut off from Command Net! (Loot a radio or stay near friendly squads)"
                            : "Squad Radio: Radioman down \u2014 squad holding HQ link via nearby friendly relay";
                        AiCore.TryShowLocalPlayerRadioHint(sq, lostMsg, requireLeader: false, minInterval: 6.0f);
                    }
                    else if (radiomanJustRecovered)
                    {
                        board.RadioNetToastInitState = 2;
                        board.RadioRelayPendingFlipSince = 0f;
                        board.NextRadioNetStatusToastTime = now + 16.0f;
                        if (hadRecordedRadioLoss || board.EverNotifiedRadioCutoff || (now - board.FirstSeenTime) >= 6.0f)
                        {
                            Soldier lp = Soldier.CurrentControlledSoldierOrNull();
                            bool playerIsRad = lp != null && lp.IsAlive && lp.joinedSquad != null && lp.joinedSquad.Pointer == sqPtr && lp.IsRadioman();
                            string recMsg = playerIsRad
                                ? "Squad Radio: You recovered a field radio \u2014 squad reconnected to HQ Command Net!"
                                : "Squad Radio: Field radio recovered \u2014 squad reconnected to HQ Command Net!";
                            AiCore.TryShowLocalPlayerRadioHint(sq, recMsg, requireLeader: false, minInterval: 6.0f);
                        }
                        board.EverNotifiedRadioCutoff = false;
                    }
                    else if (!hasAliveRadNow)
                    {
                        // Relay notices fire strictly when NO alive Radioman exists in the squad
                        bool wasNotifiedConnected = board.RadioNetToastInitState == 2;
                        if (liveRadioLink != wasNotifiedConnected)
                        {
                            if (!inPlayerJoinGrace)
                            {
                                if (board.RadioRelayPendingFlipSince <= 0f)
                                    board.RadioRelayPendingFlipSince = now;

                                if ((now - board.RadioRelayPendingFlipSince) >= 4.0f && now >= board.NextRadioNetStatusToastTime)
                                {
                                    board.RadioNetToastInitState = liveRadioLink ? (byte)2 : (byte)1;
                                    board.RadioRelayPendingFlipSince = 0f;
                                    board.NextRadioNetStatusToastTime = now + 20.0f;

                                    if (!liveRadioLink)
                                    {
                                        board.EverNotifiedRadioCutoff = true;
                                        AiCore.TryShowLocalPlayerRadioHint(
                                            sq,
                                            "Squad Radio: Squad out of friendly relay range \u2014 cut off from HQ Command Net",
                                            requireLeader: false,
                                            minInterval: 6.0f);
                                    }
                                    else if (board.EverNotifiedRadioCutoff)
                                    {
                                        board.EverNotifiedRadioCutoff = false;
                                        AiCore.TryShowLocalPlayerRadioHint(
                                            sq,
                                            "Squad Radio: Squad back in friendly relay range \u2014 reconnected to HQ Command Net",
                                            requireLeader: false,
                                            minInterval: 6.0f);
                                    }
                                }
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
                        board.EverNotifiedRadioCutoff = false;
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

            // Scan squad members once to find active threat, alive count, MG/Medic/AT/smoke/ammo presence & surrendered comrades
            board.HasActiveThreat = false;
            board.ThreatIsArmor = false;
            board.HasAliveMachineGunner = false;
            board.HasAliveMedic = false;
            board.SquadHasAntiTank = false;
            board.SquadHasSmokeAvailable = false;
            board.SquadOutOfAmmo = false;
            board.PrimaryTarget = null;
            board.SurrenderedComrade = null;
            board.AliveMemberCount = 0;
            board.NearbyEnemyCount42m = 0;
            board.NearbyFriendlyCount42m = 0;
            board.FriendlyUnitsInsideObjective = 0;
            board.EnemyUnitsInsideObjective = 0;
            board.CloserFriendlyUnitsToObjective = 0;

            int dryAmmoMembers = 0;
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
                    if (!board.SquadHasSmokeAvailable && u.inventory != null && u.inventory.FindItemOfType<VirtualSmokeGrenade>() != null)
                    {
                        board.SquadHasSmokeAvailable = true;
                    }
                    if (!AiCore.HasUsableSmallArmsAmmo(u))
                    {
                        dryAmmoMembers++;
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

            board.SquadOutOfAmmo = board.AliveMemberCount > 0 && dryAmmoMembers * 2 >= board.AliveMemberCount;

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
                   (b.NetDirective == SquadNetDirective.Regroup_SectorWithdrawal || now < b.AiSquadRetreatUntil);
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
                if (candSq == null || candSq.Pointer == IntPtr.Zero || candSq.Pointer == anvilSqPtr || IsSquadActivelyRetreating(candSq.Pointer, now))
                    continue;
                if (AiCore.HasAnyHumanPlayerInSquad(candSq) || _garrisonAssignedObjectiveBySquad.ContainsKey(candSq.Pointer))
                    continue;

                Soldier candLeader = candSq.Leader;
                if (candLeader == null || !candLeader.IsAlive || !AiCore.AreSameSideFactions(faction, candLeader.faction) || candLeader.transform == null)
                    continue;

                if (!AiCore.IsLocallyOwnedBot(candLeader))
                    continue;

                if (candLeader.Params != null && (!candLeader.Params.canGiveOrders || !candLeader.Params.canMove))
                    continue;

                if (_squadBlackboards.TryGetValue(candSq.Pointer, out SquadBlackboard candBoard))
                {
                    if (now < candBoard.NextHammerOrderTime || now < candBoard.AiSquadRetreatUntil)
                        continue;
                    if (candBoard.AliveMemberCount <= 2 && (candBoard.HasActiveThreat || candLeader.GetSuppressionValue() >= 12.0f))
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
                    if (remLeader == null || !AiCore.IsAnyHumanPlayerSoldier(remLeader) || remLeader.transform == null || remSq == null || !HasLiveRadioOrRelayLink(remSq))
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

            if (TryGetActivePendingPlayerHqOrder(out PlayerPendingHqOrderState existing))
            {
                if (existing.Kind != PendingHqOrderKind.TacticalFlank ||
                    (existing.WorldPos - threatPos).sqrMagnitude > 22.0f * 22.0f)
                {
                    return;
                }
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

        internal static void ExecuteEmergentSquadLeaderOrders(
            Squad sq,
            Soldier leader,
            ref SquadBlackboard board,
            EmergentEngine.Tier2SquadDirective t2Directive,
            EmergentEngine.Tier3NetLever activeT3Lever,
            float now,
            ref bool issuedSquadCoverOrderThisTick)
        {
            if (sq == null || sq.Pointer == IntPtr.Zero || leader == null || !leader.IsAlive || leader.transform == null)
                return;

            if (AiCore.HasAnyHumanPlayerInSquad(sq) || !AiCore.IsLocallyOwnedBot(leader))
                return;

            CheckAndAbortSuicidalAiCharge(sq, leader, ref board, now);

            IntPtr sqPtr = sq.Pointer;
            Vector3 leaderPos = leader.transform.position;
            float leaderSupp = leader.GetSuppressionValue();
            float distToThreat = board.HasActiveThreat ? Vector3.Distance(leaderPos, board.ThreatPosition) : 9999f;
            float distToObj = board.HasActiveObjectiveTask ? Vector3.Distance(leaderPos, board.ObjectivePosition) : 9999f;
            bool syncOnline = Photon.Pun.PhotonNetwork.InRoom;

            // A. Auto-Release AI Covert Hold-Fire immediately when discovered, suppressed, or entering close assault range.
            //    Strictly scoped to board.IsAiCovertHoldFireActive so mission-scripted Lua holdFire orders are NEVER touched!
            if (board.IsAiCovertHoldFireActive)
            {
                if (!sq.HoldFire)
                {
                    board.IsAiCovertHoldFireActive = false;
                    board.AiCovertHoldFireExpireTime = 0f;
                }
                else
                {
                    bool expired = board.AiCovertHoldFireExpireTime <= 0f || now >= board.AiCovertHoldFireExpireTime;
                    bool compromised = sq.IsEnemyAwareOfThisSquad ||
                                       leaderSupp >= 8.0f ||
                                       leader.isBleeding ||
                                       distToThreat <= 36.0f ||
                                       distToObj <= 36.0f ||
                                       sq.InChargeTime() ||
                                       (board.IsDefendingObjective && board.ObjectiveCaptureProgress >= 0.35f);

                    if ((expired || compromised) && (!syncOnline || AiCore.TryConsumeBotRpcBudget(0.20f)))
                    {
                        board.IsAiCovertHoldFireActive = false;
                        board.AiCovertHoldFireExpireTime = 0f;
                        try
                        {
                            sq.SetHoldFireOrder(false, false, compromised && !expired, syncOnline);
                        }
                        catch { }
                    }
                }
            }

            // B. Auto-Release AI Follow Column March as soon as the squad nears the frontline (<= 78m) or takes contact
            //    so vanilla SquadLeaderRoutine deploys the squad into tactical cover immediately.
            if (board.AiFollowColumnUntil > 0f)
            {
                if (sq.order != Order.follow)
                {
                    board.AiFollowColumnUntil = 0f;
                }
                else if (now >= board.AiFollowColumnUntil ||
                         board.HasActiveThreat ||
                         sq.IsEnemyAwareOfThisSquad ||
                         leaderSupp >= 4.0f ||
                         leader.isBleeding ||
                         distToObj <= 78.0f)
                {
                    board.AiFollowColumnUntil = 0f;
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
            if (board.AiSquadRetreatUntil > 0f)
            {
                if (now >= board.AiSquadRetreatUntil || (!board.HasActiveThreat && leaderSupp < 5.0f))
                {
                    board.AiSquadRetreatUntil = 0f;
                }
                else
                {
                    return;
                }
            }

            // B3. Friendly Artillery Barrage Hold & Post-Barrage Surge + Enemy Shelling Pocket Lateral Flank
            if (board.IsConnectedToRadioNet)
            {
                bool hitFriendlyBarrage = TryGetFriendlyBarrageHoldZone(leader.faction, leaderPos, now, out Vector3 fBarrageCenter, out float fSafeRadius);
                if (!hitFriendlyBarrage && board.HasActiveObjectiveTask && distToObj <= 85.0f)
                {
                    hitFriendlyBarrage = TryGetFriendlyBarrageHoldZone(leader.faction, board.ObjectivePosition, now, out fBarrageCenter, out fSafeRadius);
                }

                if (hitFriendlyBarrage)
                {
                    bool wasHolding = board.WasHoldingForFriendlyBarrage;
                    board.WasHoldingForFriendlyBarrage = true;

                    Vector3 away = leaderPos - fBarrageCenter;
                    away.y = 0f;
                    Vector3 awayDir = away.sqrMagnitude > 0.25f ? away.normalized : (-board.ThreatDirection);
                    Vector3 rawHold = fBarrageCenter + awayDir * (fSafeRadius + 8.0f);
                    bool hasHoldWater = AiCore.TryGetActiveWaterLevel(out float holdWaterLevel);
                    Vector3 holdSpot = AiCore.TrySnapWalkableGroundProbe(leaderPos, rawHold, 8.0f, 16.0f, 0.55f, 3.0f, hasHoldWater, holdWaterLevel, out Vector3 snappedHold)
                        ? snappedHold
                        : rawHold;

                    if (!issuedSquadCoverOrderThisTick &&
                        (!wasHolding || (sq.moveOrderPosition - holdSpot).sqrMagnitude > 8.0f * 8.0f) &&
                        (!syncOnline || AiCore.TryConsumeBotRpcBudget(0.45f)))
                    {
                        try
                        {
                            sq.HoldArea(holdSpot, 10.0f, false);
                            issuedSquadCoverOrderThisTick = true;
                        }
                        catch { }
                    }
                }
                else if (board.WasHoldingForFriendlyBarrage)
                {
                    board.WasHoldingForFriendlyBarrage = false;
                    board.BarrageSurgeUntil = now + 10.0f;

                    if (sq.currentTask != null &&
                        !issuedSquadCoverOrderThisTick &&
                        (!syncOnline || AiCore.TryConsumeBotRpcBudget(0.45f)))
                    {
                        try
                        {
                            sq.Leader_OrderAttackCurrentTask(leader);
                            issuedSquadCoverOrderThisTick = true;
                            TrySayTacticalVoice(leader, sq, VoiceManager.VoiceClip.charge, 0.10f);
                        }
                        catch { }
                    }
                }
            }

            if (!issuedSquadCoverOrderThisTick &&
                !board.IsDefendingObjective &&
                sq.currentTask != null &&
                TryGetActiveEnemyShellingPocket(leader.faction, leaderPos, now, out Vector3 shellCenter, out _))
            {
                Vector3 fwdAxis = board.ThreatDirection.sqrMagnitude > 0.01f ? board.ThreatDirection : leader.transform.forward;
                Vector3 perp = Vector3.Cross(Vector3.up, fwdAxis);
                perp.y = 0f;
                Vector3 latDir = perp.sqrMagnitude > 0.01f ? perp.normalized : leader.transform.right;
                if (Vector3.Dot(leaderPos - shellCenter, latDir) < 0f)
                    latDir = -latDir;

                Vector3 flankDest = shellCenter + latDir * 32.0f;
                if ((sq.moveOrderPosition - flankDest).sqrMagnitude > 14.0f * 14.0f &&
                    (!syncOnline || AiCore.TryConsumeBotRpcBudget(0.45f)))
                {
                    try
                    {
                        sq.AttackFromSide(latDir, flankDest, 14.0f);
                        issuedSquadCoverOrderThisTick = true;
                        TrySayTacticalVoice(leader, sq, VoiceManager.VoiceClip.keepYourHeadDown, 0.10f);
                    }
                    catch { }
                }
            }

            // B3b. Radio Relay Cohesion & Reconnect:
            //      Squads without an alive intrinsic Radioman proactively stay within friendly relay range
            //      (or shift back into range when disconnected), unless recovering a radio pack, contesting
            //      an objective, fighting in CQB, or pinned/retreating.
            if (VisionConfig.EnableRadiomanIntelNet.Value &&
                !issuedSquadCoverOrderThisTick &&
                !board.PreviouslyHadAliveRadioman &&
                now >= board.NextRelayCohesionOrderTime &&
                !AiCore.IsSoldierInVehicleOrTransition(leader) &&
                !leader.HasSurrended() &&
                !leader.IsInjured &&
                !AiCore.HasAnyHumanPlayerInSquad(sq) &&
                leaderSupp < 35.0f &&
                !IsSquadOnRetreatTask(sq) &&
                !_squadOrderedToVehicle.ContainsKey(sqPtr) &&
                !(board.HasActiveObjectiveTask && distToObj <= 26.0f) &&
                !(board.HasActiveThreat && (board.ThreatPosition - leaderPos).sqrMagnitude <= 28.0f * 28.0f))
            {
                bool recoveringRadioPack =
                    (board.LastRadiomanLostTime > 0f && (now - board.LastRadiomanLostTime) < 6.0f) ||
                    HasActiveRadioPackClaimForSquad(sqPtr, now);

                if (!recoveringRadioPack)
                {
                    FactionCommandProfile relayProfile = GetFactionCommandProfile(leader.faction);
                    float relayR = relayProfile.RadioRelayRadius;
                    if (relayR > 1.0f &&
                        !(relayProfile.PreferAmbushWhenAlone && board.HasActiveThreat) &&
                        TryFindNearestFriendlyRelaySquad(sqPtr, leader.faction, leaderPos, relayR * 2.5f, out Vector3 relayPos, out float relayDist) &&
                        relayDist > relayR * 0.78f)
                    {
                        Vector3 axis = board.HasActiveObjectiveTask ? (board.ObjectivePosition - relayPos) : (leaderPos - relayPos);
                        axis.y = 0f;
                        Vector3 linkSpot;
                        if (axis.sqrMagnitude > 1.0f)
                        {
                            float axisDist = Mathf.Sqrt(axis.sqrMagnitude);
                            float offset = Mathf.Min(relayR * 0.65f, board.HasActiveObjectiveTask ? axisDist * 0.60f : relayR * 0.65f);
                            linkSpot = relayPos + (axis / axisDist) * offset;
                        }
                        else
                        {
                            linkSpot = relayPos;
                        }

                        if (AiCore.TrySnapWalkableGroundProbe(leaderPos, linkSpot, 8.0f, 16.0f, 0.55f, 3.0f, false, 0f, out Vector3 snappedLink))
                            linkSpot = snappedLink;

                        if ((sq.moveOrderPosition - linkSpot).sqrMagnitude > 10.0f * 10.0f &&
                            (!syncOnline || AiCore.TryConsumeBotRpcBudget(0.45f)))
                        {
                            try
                            {
                                sq.HoldArea(linkSpot, 12.0f, false);
                                board.NextRelayCohesionOrderTime = now + 10.0f;
                                issuedSquadCoverOrderThisTick = true;
                                AiCore.LogDebug("RadioNet", $"[RadioRelay] {leader.name} {(board.IsConnectedToRadioNet ? "leashing" : "reconnecting")} to relay ({relayDist:F0}m / {relayR:F0}m)");
                            }
                            catch { }
                        }
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
                !AiCore.IsSoldierInVehicleOrTransition(leader) &&
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
                }
                else if ((now - board.StallAnchorTime) >= 8.5f &&
                         now >= board.NextSquadUnstallTime &&
                         (!syncOnline || AiCore.TryConsumeBotRpcBudget(0.45f)))
                {
                    board.ConsecutiveStallCount++;
                    board.StallAnchorCenter = board.SquadCenter;
                    board.StallAnchorTime = now;
                    board.NextSquadUnstallTime = now + 13.0f;
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

            // B5. Garrison Hold-Fire Cleanup & Flank-Shift Cover Re-Orientation (~10.5s cadence when threat shifts > 55 deg)
            if (_garrisonAssignedObjectiveBySquad.ContainsKey(sqPtr) &&
                sq.HoldFire &&
                !board.IsAiCovertHoldFireActive &&
                (!syncOnline || AiCore.TryConsumeBotRpcBudget(0.25f)))
            {
                try { sq.SetHoldFireOrder(false, false, false, syncOnline); } catch { }
            }

            if (!issuedSquadCoverOrderThisTick &&
                sq.order == Order.defend &&
                !sq.InChargeTime() &&
                !AiCore.IsSoldierInVehicleOrTransition(leader) &&
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
                else if (VisionPatches.TryGetDominantEnemyGunfireDirection(board.SquadCenter, leader.faction, 175.0f, 6.0f, out Vector3 gfDir, out _, out _))
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

            // C. H.A.R.M.-Guided Tier 2 Execution Orders (Charge! / Covert Hold-Fire / Follow Column / Fighting Withdrawal)
            if (issuedSquadCoverOrderThisTick)
                return;

            if (leader.Params != null && (!leader.Params.canGiveOrders || !leader.Params.canMove))
                return;

            if (leader.HasSurrended() || leader.IsInjured || IsSquadOnRetreatTask(sq) || _squadOrderedToVehicle.ContainsKey(sqPtr))
                return;

            FactionCommandProfile profile = GetFactionCommandProfile(leader.faction);
            SoldierPersonality leaderPers = GetPersonality(leader);
            bool isTrueGyokusai = IsTrueGyokusaiOrBanzaiCondition(in profile, sq, in board);

            float bestCost = 0.98f;
            int bestAction = 0; // 1 = Charge, 2 = Covert HoldFire, 3 = Follow Column, 4 = Fighting Withdrawal / Regroup
            Vector3 chargeTarget = Vector3.zero;
            Vector3 retreatRallyPos = Vector3.zero;

            // 1. Evaluate Charge! (sq.Charge) — Short-Range Shock Breach
            bool chargeAllowedByDirective = isTrueGyokusai ||
                                            now < board.BarrageSurgeUntil ||
                                            activeT3Lever == EmergentEngine.Tier3NetLever.MassAssault ||
                                            activeT3Lever == EmergentEngine.Tier3NetLever.SectorDesperationBias ||
                                            t2Directive == EmergentEngine.Tier2SquadDirective.AdvanceBound ||
                                            t2Directive == EmergentEngine.Tier2SquadDirective.HammerFlank ||
                                            t2Directive == EmergentEngine.Tier2SquadDirective.SquadSmokePush;
            if (chargeAllowedByDirective &&
                !sq.InChargeTime() &&
                board.AliveMemberCount >= 3 &&
                now >= board.NextAiSquadChargeTime &&
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

                if (hasChargeTarget && !isTrueGyokusai && !isDesperateOvertime && board.DecayedRecentDeaths >= 1.40f)
                {
                    hasChargeTarget = false;
                }

                if (hasChargeTarget)
                {
                    float chargeCost = 1.15f * profile.BayonetBreachCostMult;
                    bool isDesperateCap = isDesperateOvertime;
                    if (leaderPers == SoldierPersonality.Leader_Spearhead)
                        chargeCost *= 0.72f;
                    else if (leaderPers == SoldierPersonality.Leader_OldGuard || leaderPers == SoldierPersonality.Leader_Tactician)
                        chargeCost *= 1.18f;

                    if (board.HasActiveObjectiveTask && board.IsDefendingObjective && board.ObjectiveCaptureProgress >= 0.60f)
                        chargeCost *= 0.72f;
                    else if (board.HasActiveObjectiveTask && !board.IsDefendingObjective && board.FriendlyUnitsInsideObjective == 0 && board.AliveMemberCount >= 5 && distToObj <= 24.0f)
                        chargeCost *= 0.84f;

                    if (board.IsInvaderFaction && board.TicketRatio01 <= 0.18f && board.HasActiveObjectiveTask && !board.IsDefendingObjective)
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
                        chargeCost *= 1.65f;

                    bool localTwoToOneOdds = board.NearbyFriendlyCount42m >= Mathf.Max(4, board.NearbyEnemyCount42m * 2);
                    bool hasTacticalJustification = isTrueGyokusai || isDesperateCap || hasSmokeCover || enemyPinned || localTwoToOneOdds || now < board.BarrageSurgeUntil;

                    if (board.NetDirective == SquadNetDirective.Hammer_WideFlankEnvelopment && targetDistNow <= 26.0f)
                        chargeCost *= 0.86f;

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
                now >= board.NextAiCovertOrderTime)
            {
                bool wantStealthFlank = t2Directive == EmergentEngine.Tier2SquadDirective.HammerFlank ||
                                        board.NetDirective == SquadNetDirective.Hammer_WideFlankEnvelopment;
                bool wantTacticianInfil = leaderPers == SoldierPersonality.Leader_Tactician && !board.IsDefendingObjective;
                bool wantDoctrineAmbush = profile.PreferAmbushWhenAlone && !board.IsConnectedToRadioNet;

                if (wantStealthFlank || wantTacticianInfil || wantDoctrineAmbush)
                {
                    float covertCost = 1.26f * profile.FlankMoveCostMult;
                    if (wantStealthFlank) covertCost *= 0.72f;
                    if (wantTacticianInfil) covertCost *= 0.76f;
                    if (wantDoctrineAmbush) covertCost *= 0.80f;
                    if (leaderPers == SoldierPersonality.Leader_Spearhead) covertCost *= 1.20f;
                    if (board.IsDefendingObjective && board.ObjectiveCaptureProgress >= 0.25f) covertCost *= 2.50f;

                    if (covertCost < bestCost)
                    {
                        bestCost = covertCost;
                        bestAction = 2;
                    }
                }
            }

            // 3. Evaluate Follow Column (sq.FollowLeader) — Fast Unengaged Rear-Echelon Approach (> 115m Out of Contact)
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
                now >= board.NextAiFollowOrderTime &&
                AreMajorityOfSquadFollowersNearLeader(sq, leader, leaderPos, 28.0f))
            {
                const float followCost = 0.86f;
                if (followCost < bestCost)
                {
                    bestCost = followCost;
                    bestAction = 3;
                }
            }

            // 4. Evaluate Fighting Withdrawal / Regroup (sq.HoldArea + Smoke + 'Retreat!')
            if (VisionConfig.EnableTacticalRetreatsAndRegroup.Value &&
                !sq.InChargeTime() &&
                board.HasActiveThreat &&
                distToThreat <= 68.0f &&
                !AiCore.IsOnBridgeOrNarrowCrossing(leaderPos) &&
                !(board.IsDefendingObjective && board.ObjectiveCaptureProgress >= 0.55f) &&
                !(board.IsInvaderFaction && board.TicketRatio01 <= 0.18f) &&
                now >= board.NextAiSquadRetreatTime)
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
                    bool netWithdrawal = t2Directive == EmergentEngine.Tier2SquadDirective.LocalFallback ||
                                         activeT3Lever == EmergentEngine.Tier3NetLever.AuthorizeWithdraw ||
                                         board.NetDirective == SquadNetDirective.Regroup_SectorWithdrawal;
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
                        if (leaderPers == SoldierPersonality.Leader_OldGuard) retreatCost *= 0.80f;
                        else if (leaderPers == SoldierPersonality.Leader_Tactician) retreatCost *= 0.88f;
                        else if (leaderPers == SoldierPersonality.Leader_Spearhead) retreatCost *= 1.18f;

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
                    if (board.IsAiCovertHoldFireActive)
                    {
                        board.IsAiCovertHoldFireActive = false;
                        board.AiCovertHoldFireExpireTime = 0f;
                        try { sq.SetHoldFireOrder(false, false, false, syncOnline); } catch { }
                    }
                    board.AiFollowColumnUntil = 0f;

                    try
                    {
                        sq.Charge(chargeTarget, 15.0f);
                        if (sq.timed_order_end < now + 14.0f)
                            sq.timed_order_end = now + 14.0f;
                    }
                    catch { }

                    board.IsAiChargeOrdered = true;
                    board.AiChargeBaselineDeaths = board.DecayedRecentDeaths;
                    board.NextAiSquadChargeTime = now + 62.0f;
                    issuedSquadCoverOrderThisTick = true;
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
                        board.IsAiCovertHoldFireActive = true;
                        board.AiCovertHoldFireExpireTime = now + 20.0f;
                    }
                    catch { }

                    board.NextAiCovertOrderTime = now + 38.0f;
                    issuedSquadCoverOrderThisTick = true;
                    GoapPlansExecutedCount++;

                    if (VisionConfig.DebugLogging.Value)
                    {
                        AiCore.LogDebug(
                            "SquadCovert",
                            $"{leader.faction} {leaderPers} ordered Covert Hold-Fire on '{sq.squadName}' (cost:{bestCost:F2}, directive:{t2Directive}).",
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
                        board.AiFollowColumnUntil = now + 12.0f;
                    }
                    catch { }

                    board.NextAiFollowOrderTime = now + 32.0f;
                    issuedSquadCoverOrderThisTick = true;
                    GoapPlansExecutedCount++;
                }
            }
            else if (bestAction == 4)
            {
                if (!syncOnline || AiCore.TryConsumeBotRpcBudget(0.45f))
                {
                    if (board.IsAiCovertHoldFireActive)
                    {
                        board.IsAiCovertHoldFireActive = false;
                        board.AiCovertHoldFireExpireTime = 0f;
                        try { sq.SetHoldFireOrder(false, false, false, syncOnline); } catch { }
                    }
                    board.AiFollowColumnUntil = 0f;

                    TryPopSquadRetreatSmoke(sq, leader, ref board, now);
                    TrySayTacticalVoice(leader, sq, VoiceManager.VoiceClip.timeToRetreat, 0.05f);

                    try
                    {
                        sq.HoldArea(retreatRallyPos, 14.0f, false);
                        sq.timed_order_end = now + 17.0f;
                    }
                    catch { }

                    board.AiSquadRetreatUntil = now + 17.0f;
                    board.NextAiSquadRetreatTime = now + 48.0f;
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

        private static float _nextPlayerTier3RadioTime;

        internal static void NotifyPlayerSquadLeaderOfTier3Shift(
            string faction,
            EmergentEngine.Tier3NetLever newLever,
            Vector3 focusPos,
            bool hasFocusPos,
            float now)
        {
            if (!VisionConfig.EnableMod.Value || !VisionConfig.EnableRadiomanIntelNet.Value || string.IsNullOrEmpty(faction))
                return;

            if (now < _nextPlayerTier3RadioTime - 60.0f)
                _nextPlayerTier3RadioTime = 0f;

            if (now < _nextPlayerTier3RadioTime)
                return;

            if (newLever != EmergentEngine.Tier3NetLever.ShiftAxisFlank &&
                newLever != EmergentEngine.Tier3NetLever.FireBrigade &&
                newLever != EmergentEngine.Tier3NetLever.SmokeBarrage &&
                newLever != EmergentEngine.Tier3NetLever.MassAssault)
            {
                return;
            }

            Soldier lp = Soldier.CurrentControlledSoldierOrNull();
            if (lp == null || !lp.IsAlive || lp.IsInjured || lp.HasSurrended() || lp.transform == null || !lp.IsSquadLeader())
                return;

            if (!AiCore.AreSameSideFactions(faction, lp.faction))
                return;

            Squad lpSq = lp.joinedSquad;
            if (lpSq == null || lpSq.Pointer == IntPtr.Zero || !HasLiveRadioOrRelayLink(lpSq))
                return;

            if (newLever == EmergentEngine.Tier3NetLever.ShiftAxisFlank &&
                hasFocusPos &&
                !lp.IsOnVehicle() &&
                !TryGetActivePendingPlayerHqOrder(out _) &&
                !IsFlankBalkedByPlayer(focusPos, now))
            {
                Vector3 lpPos = lp.transform.position;
                Vector3 flatToFocus = focusPos - lpPos;
                flatToFocus.y = 0f;
                float dSq = flatToFocus.sqrMagnitude;
                if (dSq >= 30.0f * 30.0f && dSq <= 220.0f * 220.0f)
                {
                    float dist = Mathf.Sqrt(dSq);
                    Vector3 axisDir = flatToFocus / dist;
                    Vector3 perpRight = new Vector3(axisDir.z, 0f, -axisDir.x);
                    Vector3 flankDir = (axisDir * 0.65f + perpRight * 0.76f).normalized;
                    string bearing = AiCore.FormatRelativeBearingAndDistance(lpPos, focusPos);

                    PostPendingPlayerHqOrder(
                        lpSq,
                        PendingHqOrderKind.TacticalFlank,
                        targetObj: null,
                        targetTask: null,
                        worldPos: focusPos,
                        flankDir: flankDir,
                        isAttackObjective: true,
                        shortActionLabel: $"Flank MIA ({bearing})",
                        toastLabel: $"Flank Overrun ({bearing}) \u2014 Shift Axis",
                        mapBadgeText: "[HQ]",
                        durationSeconds: 45.0f);
                    _nextPlayerTier3RadioTime = now + 45.0f;
                    return;
                }
            }

            if (TryGetActivePendingPlayerHqOrder(out _))
                return;

            string msg;
            switch (newLever)
            {
                case EmergentEngine.Tier3NetLever.FireBrigade:
                    msg = "HQ Command Net: Defense buckling \u2014 reinforce threatened point";
                    break;
                case EmergentEngine.Tier3NetLever.SmokeBarrage:
                    msg = "HQ Command Net: Heavy fire across line \u2014 smoke & maneuver";
                    break;
                case EmergentEngine.Tier3NetLever.MassAssault:
                    msg = "HQ Command Net: Enemy defense softening \u2014 press the assault";
                    break;
                default:
                    msg = "HQ Command Net: Friendly element MIA \u2014 shift attack axis";
                    break;
            }

            AiCore.TryShowLocalPlayerRadioHint(
                lpSq,
                msg,
                requireLeader: true,
                minInterval: 18.0f,
                notifyRemoteVanillaSquadmates: false);
            _nextPlayerTier3RadioTime = now + 45.0f;
        }

        internal static void ExecuteEmergentTeamNetManeuvers(
            List<Squad> connectedInfantrySquads,
            List<Squad> activeVehicleAnvils,
            EmergentEngine.Tier3NetLever side0Lever,
            EmergentEngine.Tier3NetLever side1Lever,
            float now,
            ref bool issuedSquadCoverOrderThisTick)
        {
            bool syncOnline = Photon.Pun.PhotonNetwork.InRoom;
            int infCount = connectedInfantrySquads.Count;

            // Execute active Tier 3 fire-support levers (ArtilleryHE / SmokeBarrage) on an engaged AI squad
            for (int sideIdx = 0; sideIdx < 2; sideIdx++)
            {
                EmergentEngine.Tier3NetLever lev = sideIdx == 0 ? side0Lever : side1Lever;
                if (lev != EmergentEngine.Tier3NetLever.ArtilleryHE && lev != EmergentEngine.Tier3NetLever.SmokeBarrage)
                    continue;

                for (int i = 0; i < infCount; i++)
                {
                    Squad sq = connectedInfantrySquads[i];
                    if (sq == null || sq.Pointer == IntPtr.Zero || AiCore.HasAnyHumanPlayerInSquad(sq))
                        continue;
                    Soldier ldr = sq.Leader;
                    if (ldr == null || !ldr.IsAlive || !AiCore.IsLocallyOwnedBot(ldr) || string.IsNullOrEmpty(ldr.faction))
                        continue;

                    int sqSide = EmergentRuntimeBridge.ResolveSideIndex(ldr.faction);
                    if (sqSide != sideIdx)
                        continue;

                    if (_squadBlackboards.TryGetValue(sq.Pointer, out SquadBlackboard b) && b.HasActiveThreat)
                    {
                        TryTriggerSquadRadioSupport(sq, ldr);
                        break;
                    }
                }
            }

            if (infCount == 0 && activeVehicleAnvils.Count == 0)
                return;

            // 1A. Vehicle/Tank Anvil + Infantry Hammer (Combined-Arms Flank)
            for (int v = 0; v < activeVehicleAnvils.Count; v++)
            {
                Squad vehSq = activeVehicleAnvils[v];
                if (vehSq == null || vehSq.Pointer == IntPtr.Zero)
                    continue;
                if (!_squadBlackboards.TryGetValue(vehSq.Pointer, out SquadBlackboard vehBoard) || !vehBoard.HasActiveThreat)
                    continue;

                Soldier vehLeader = vehSq.Leader;
                if (vehLeader == null || !vehLeader.IsAlive || vehLeader.transform == null || string.IsNullOrEmpty(vehLeader.faction))
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
                    Soldier hammerLeader = bestHammerSq.Leader;
                    if (!issuedSquadCoverOrderThisTick &&
                        hammerLeader != null &&
                        hammerLeader.transform != null &&
                        (!syncOnline || AiCore.TryConsumeBotRpcBudget(0.45f)))
                    {
                        Vector3 hammerPos = hammerLeader.transform.position;
                        float side = Vector3.Dot(hammerPos - vehPos, perpRight) >= 0f ? 1f : -1f;
                        Vector3 flankDir = (axisDir * 0.55f + perpRight * (side * 0.835f)).normalized;

                        try
                        {
                            bestHammerSq.AttackFromSide(flankDir, threatPos, 20.0f);
                            issuedSquadCoverOrderThisTick = true;
                        }
                        catch { }

                        if (_squadBlackboards.TryGetValue(bestHammerSq.Pointer, out SquadBlackboard hBoard))
                        {
                            hBoard.NetDirective = SquadNetDirective.Hammer_WideFlankEnvelopment;
                            hBoard.NetDirectiveExpireTime = now + 24.0f;
                            hBoard.NetDirectiveTargetPos = threatPos;
                            hBoard.NextHammerOrderTime = now + 26.0f;
                            _squadBlackboards[bestHammerSq.Pointer] = hBoard;
                        }
                        ConsumeInitialHqSpawnBriefingIfNoRadio(bestHammerSq);
                        GoapPinAndFlankCount++;

                        if (vehSq.IsPlayerInSquad() && HasLiveRadioOrRelayLink(vehSq))
                        {
                            string infLabel = AiCore.FormatInfantrySupportCallsign(bestHammerSq, vehPos);
                            AiCore.TryShowLocalPlayerRadioHint(
                                vehSq,
                                $"Command Net: {infLabel} flanking enemy contact to support your position",
                                requireLeader: false,
                                minInterval: 20.0f);
                        }
                    }
                }

                TryOfferPlayerHammerFlankOrder(vehLeader.faction, vehSq.Pointer, vehTaskPtr, vehPos, threatPos, axisDir, perpRight, now);
            }

            if (infCount == 0)
                return;

            // 1B. Infantry Anvil + Infantry Hammer
            for (int i = 0; i < infCount; i++)
            {
                Squad anvilSq = connectedInfantrySquads[i];
                if (anvilSq == null || anvilSq.Pointer == IntPtr.Zero || IsSquadActivelyRetreating(anvilSq.Pointer, now))
                    continue;

                if (!_squadBlackboards.TryGetValue(anvilSq.Pointer, out SquadBlackboard anvilBoard) || !anvilBoard.HasActiveThreat)
                    continue;

                if (anvilBoard.NetDirective != SquadNetDirective.Anvil_PinAndCallSupport)
                    continue;

                Soldier anvilLeader = anvilSq.Leader;
                if (anvilLeader == null || !anvilLeader.IsAlive || anvilLeader.transform == null || string.IsNullOrEmpty(anvilLeader.faction))
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

                Squad bestHammerSq = FindBestAvailableHammerSquad(connectedInfantrySquads, i, anvilSq.Pointer, anvilLeader.faction, anvilTaskPtr, threatPos, 105f, now);
                if (bestHammerSq != null)
                {
                    Soldier hammerLeader = bestHammerSq.Leader;
                    if (!issuedSquadCoverOrderThisTick &&
                        hammerLeader != null &&
                        hammerLeader.transform != null &&
                        (!syncOnline || AiCore.TryConsumeBotRpcBudget(0.45f)))
                    {
                        Vector3 hammerPos = hammerLeader.transform.position;
                        float side = Vector3.Dot(hammerPos - anvilPos, perpRight) >= 0f ? 1f : -1f;
                        Vector3 flankDir = (axisDir * 0.55f + perpRight * (side * 0.835f)).normalized;

                        try
                        {
                            bestHammerSq.AttackFromSide(flankDir, threatPos, 20.0f);
                            issuedSquadCoverOrderThisTick = true;
                        }
                        catch { }

                        if (_squadBlackboards.TryGetValue(bestHammerSq.Pointer, out SquadBlackboard hBoard))
                        {
                            hBoard.NetDirective = SquadNetDirective.Hammer_WideFlankEnvelopment;
                            hBoard.NetDirectiveExpireTime = now + 24.0f;
                            hBoard.NetDirectiveTargetPos = threatPos;
                            hBoard.NextHammerOrderTime = now + 26.0f;
                            _squadBlackboards[bestHammerSq.Pointer] = hBoard;
                        }
                        ConsumeInitialHqSpawnBriefingIfNoRadio(bestHammerSq);
                        GoapPinAndFlankCount++;
                    }
                }

                TryOfferPlayerHammerFlankOrder(anvilLeader.faction, anvilSq.Pointer, anvilTaskPtr, anvilPos, threatPos, axisDir, perpRight, now);
            }

            // 2. Multi-Squad Objective Pincers (swing additional unengaged attacking squads onto left/right AttackFromSide arcs)
            if (issuedSquadCoverOrderThisTick || infCount < 2)
                return;

            for (int i = 0; i < infCount && !issuedSquadCoverOrderThisTick; i++)
            {
                Squad sq = connectedInfantrySquads[i];
                if (sq == null || sq.Pointer == IntPtr.Zero || sq.currentTask == null || sq.currentTask.Pointer == IntPtr.Zero)
                    continue;
                if (IsSquadActivelyRetreating(sq.Pointer, now) || AiCore.HasAnyHumanPlayerInSquad(sq) || _garrisonAssignedObjectiveBySquad.ContainsKey(sq.Pointer))
                    continue;

                if (!_squadBlackboards.TryGetValue(sq.Pointer, out SquadBlackboard board))
                    continue;
                if (board.IsDefendingObjective || board.HasActiveThreat || now < board.NetDirectiveExpireTime || now < board.PincerOrderedUntil || now < board.NextHammerOrderTime)
                    continue;

                Soldier leader = sq.Leader;
                if (leader == null || !leader.IsAlive || leader.transform == null || !AiCore.IsLocallyOwnedBot(leader))
                    continue;
                if (leader.Params != null && (!leader.Params.canGiveOrders || !leader.Params.canMove))
                    continue;

                IntPtr taskPtr = sq.currentTask.Pointer;
                Vector3 taskPos = sq.currentTask.GetTaskDestination();
                Vector3 toTask = taskPos - leader.transform.position;
                toTask.y = 0f;
                float distToTask = toTask.magnitude;
                if (distToTask < 55f || distToTask > 175f)
                    continue;

                // Count how many connected same-faction squads earlier in the list share this objective task
                int earlierSameTaskCount = 0;
                for (int prev = 0; prev < i; prev++)
                {
                    Squad pSq = connectedInfantrySquads[prev];
                    if (pSq == null || pSq.Pointer == IntPtr.Zero || pSq.currentTask == null || pSq.currentTask.Pointer != taskPtr)
                        continue;
                    Soldier pLdr = pSq.Leader;
                    if (pLdr != null && AiCore.AreSameSideFactions(leader.faction, pLdr.faction))
                        earlierSameTaskCount++;
                }

                if (earlierSameTaskCount <= 0)
                {
                    ConsumeInitialHqSpawnBriefingIfNoRadio(sq);
                    continue;
                }

                if (!syncOnline || AiCore.TryConsumeBotRpcBudget(0.45f))
                {
                    FactionCommandProfile pincerProfile = GetFactionCommandProfile(leader.faction);
                    Vector3 axisDir = toTask / distToTask;
                    Vector3 perpRight = new Vector3(axisDir.z, 0f, -axisDir.x);
                    float sideSign = (earlierSameTaskCount % 2 == 1) ? 1f : -1f;
                    float latBias = pincerProfile.FlankLateralBias;
                    float fwdBias = Mathf.Sqrt(Mathf.Max(0.1f, 1f - latBias * latBias));
                    Vector3 pincerFlankDir = (axisDir * fwdBias - perpRight * (sideSign * latBias)).normalized;

                    try
                    {
                        sq.AttackFromSide(pincerFlankDir, taskPos, 22.0f);
                        issuedSquadCoverOrderThisTick = true;
                    }
                    catch { }

                    board.NetDirective = SquadNetDirective.Hammer_WideFlankEnvelopment;
                    board.NetDirectiveExpireTime = now + 24.0f;
                    board.NetDirectiveTargetPos = taskPos;
                    board.PincerOrderedTaskPtr = taskPtr;
                    board.PincerOrderedUntil = now + 28.0f;
                    board.NextHammerOrderTime = now + 28.0f;
                    _squadBlackboards[sq.Pointer] = board;
                    ConsumeInitialHqSpawnBriefingIfNoRadio(sq);
                    GoapPinAndFlankCount++;
                }
            }
        }

        private static bool TryFindNearestFriendlyRelaySquad(
            IntPtr selfSqPtr,
            string faction,
            Vector3 fromPos,
            float maxDist,
            out Vector3 relayPos,
            out float relayDist)
        {
            relayPos = Vector3.zero;
            relayDist = float.MaxValue;
            AiCore.EnsureWorldSnapshot();

            float bestSq = maxDist * maxDist;
            bool found = false;
            var squads = AiCore.WorldSquads;
            int count = AiCore.WorldSquadCount;
            for (int i = 0; i < count; i++)
            {
                ref AiCore.SquadSnapshot snap = ref squads[i];
                if (snap.Ptr == IntPtr.Zero || snap.Ptr == selfSqPtr || !snap.LeaderIsAlive || snap.RefOnPlane)
                    continue;
                if (!(snap.HasIntrinsicRadio || snap.LeaderOnGroundVehicle))
                    continue;
                if (!AiCore.AreSameSideFactions(faction, snap.Faction))
                    continue;

                float dSq = (snap.RefPos - fromPos).sqrMagnitude;
                if (dSq <= bestSq)
                {
                    bestSq = dSq;
                    relayPos = snap.RefPos;
                    found = true;
                }
            }

            if (found)
                relayDist = Mathf.Sqrt(bestSq);
            return found;
        }
    }
}
