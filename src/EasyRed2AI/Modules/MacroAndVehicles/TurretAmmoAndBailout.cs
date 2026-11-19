using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace EasyRed2HumanAI
{
    public static partial class VeteranAndTacticsPatches
    {
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

        /// <summary>
        /// Returns true if the soldier carries a non-pistol, non-AT primary firearm with loaded or reserve ammo.
        /// </summary>
        internal static bool HasRealPrimaryWithAmmo(Soldier soldier)
        {
            if (soldier == null || soldier.Pointer == IntPtr.Zero)
                return false;

            for (int slot = 0; slot <= 2; slot++)
            {
                GenericGun g = soldier.GetHeldGun(slot);
                if (g != null && !g.IsPistol() && !g.IsAtWeapon() && (g.GetCurrentAmmoCount() > 0 || !soldier.IsOutOfAmmosFor(g)))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Returns true if an eligible open friendly infantry squad with an alive foot leader exists within <paramref name="maxRadius"/>.
        /// </summary>
        internal static bool HasNearbyOpenFriendlyInfantrySquad(Squad selfSq, string faction, Vector3 originPos, float maxRadius)
        {
            if (string.IsNullOrEmpty(faction))
                return false;

            AiCore.EnsureWorldSnapshot();
            int sqCount = AiCore.WorldSquadCount;
            var squads = AiCore.WorldSquads;
            IntPtr mySqPtr = selfSq != null ? selfSq.Pointer : IntPtr.Zero;
            float maxSq = maxRadius * maxRadius;

            for (int i = 0; i < sqCount; i++)
            {
                ref AiCore.SquadSnapshot other = ref squads[i];
                if (!other.LeaderIsAlive ||
                    other.Ptr == mySqPtr ||
                    other.IsVehicleCrew ||
                    other.LeaderOnVehicle ||
                    other.Faction != faction)
                {
                    continue;
                }

                int cap = other.IsPlayerInSquad ? 8 : 10;
                if (other.UnitCount >= cap)
                    continue;

                if ((other.RefPos - originPos).sqrMagnitude <= maxSq)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Returns true if a grounded AI tank/vehicle crew should stay on foot to reinforce nearby friendly infantry instead of retreating.
        /// </summary>
        internal static bool ShouldGroundedTankCrewStayAndJoinInfantry(Squad sq, string faction, Vector3 originPos)
        {
            if (sq == null ||
                sq.Pointer == IntPtr.Zero ||
                sq.units == null ||
                string.IsNullOrEmpty(faction) ||
                AiCore.IsConfirmedPlaneCrewSquad(sq))
            {
                return false;
            }

            if (!HasNearbyOpenFriendlyInfantrySquad(sq, faction, originPos, 85.0f))
                return false;

            int uCount = sq.units.Count;
            for (int i = 0; i < uCount; i++)
            {
                Soldier u = sq.units[i];
                if (u == null || u.Pointer == IntPtr.Zero || u.IsDead || u.IsInjured || u.HasSurrended())
                    continue;

                if (HasRealPrimaryWithAmmo(u))
                    return true;

                SoldierPersonality pers = GetPersonality(u);
                if (pers == SoldierPersonality.TankCrew_Brawler ||
                    pers == SoldierPersonality.LightCrew_Outrider ||
                    pers == SoldierPersonality.LightCrew_Escort)
                {
                    return true;
                }
            }

            return false;
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

            if (ShouldGroundedTankCrewStayAndJoinInfantry(sq, myFaction, crewPos))
                return;

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

        private struct PendingStaggeredBailout
        {
            public Soldier Unit;
            public Vehicle Veh;
            public float ExitAtTime;
            public Vector3 WreckPos;
        }

        private static readonly List<PendingStaggeredBailout> _pendingStaggeredBailouts = new List<PendingStaggeredBailout>(16);

        private static bool IsPendingStaggeredBailout(IntPtr unitPtr)
        {
            for (int i = 0; i < _pendingStaggeredBailouts.Count; i++)
            {
                Soldier u = _pendingStaggeredBailouts[i].Unit;
                if (u != null && u.Pointer == unitPtr)
                    return true;
            }
            return false;
        }

        private static void ExecuteCrewmanHatchExit(Soldier unit, Vehicle veh, Vector3 wreckPos, float now, bool shoutBailout)
        {
            if (unit == null || unit.Pointer == IntPtr.Zero || veh == null || veh.Pointer == IntPtr.Zero)
                return;
            if (!unit.IsAI() || !unit.IsAlive || unit.IsInjured || unit.IsDead || !unit.IsOnVehicle() || !AiCore.IsLocallyOwnedBot(unit))
                return;

            unit.CoverPosition(null);
            ClearEmplacementAssignment(unit.Pointer, markUnreachable: false);
            if (AiCore.TryGetSoldierHandles(unit.Pointer, out var unitHandles))
            {
                unitHandles.Stance.LocalCoverProtectedUntil = 0f;
                SoldierPersonality crewPers = GetPersonality(unit);
                float baseShock = (crewPers == SoldierPersonality.TankCrew_Brawler || crewPers == SoldierPersonality.Pilot_Disciplined)
                    ? 2.1f
                    : ((crewPers == SoldierPersonality.TankCrew_Support || crewPers == SoldierPersonality.LightCrew_Escort) ? 3.5f : 2.8f);
                unitHandles.Goap.BailoutShockUntil = now + baseShock + (AiCore.GetDeterministicHash(unit.Pointer, 79) & 7) * 0.08f;
                unitHandles.Goap.BailoutWreckPos = wreckPos;
            }

            if (shoutBailout)
            {
                VoiceManager.VoiceClip bailClip = veh.GetDamage() >= 90
                    ? VoiceManager.VoiceClip.getOutTankOnFire
                    : VoiceManager.VoiceClip.getOutTankDestroyed;
                TrySayTacticalVoice(unit, unit.joinedSquad, bailClip, 0.05f);
            }

            BotActionTrace.RecordEvent(unit, BotTraceEventKind.VehicleBailout, "disabled_vehicle_bailout");
            veh.GetOffVehicle(unit);
        }

        internal static void ProcessPendingStaggeredBailouts(float now)
        {
            if (_pendingStaggeredBailouts.Count == 0)
                return;

            for (int i = _pendingStaggeredBailouts.Count - 1; i >= 0; i--)
            {
                PendingStaggeredBailout entry = _pendingStaggeredBailouts[i];
                if (now < entry.ExitAtTime)
                    continue;

                _pendingStaggeredBailouts.RemoveAt(i);
                try
                {
                    ExecuteCrewmanHatchExit(entry.Unit, entry.Veh, entry.WreckPos, now, shoutBailout: false);
                }
                catch { }
            }
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

                    float now = Time.time;
                    Vector3 wreckPos = veh.transform != null ? veh.transform.position : Vector3.zero;
                    int exitOrderIdx = 0;
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

                            sampleCrew = unit;
                            if (IsPendingStaggeredBailout(unit.Pointer))
                                continue;

                            if (unit.IsAI() && unit.IsAlive && !unit.IsInjured && !unit.IsDead)
                            {
                                if (exitOrderIdx == 0)
                                {
                                    ExecuteCrewmanHatchExit(unit, veh, wreckPos, now, shoutBailout: true);
                                }
                                else if (_pendingStaggeredBailouts.Count < 32)
                                {
                                    float delay = exitOrderIdx * 0.35f + (AiCore.GetDeterministicHash(unit.Pointer, 41) & 7) * 0.05f;
                                    _pendingStaggeredBailouts.Add(new PendingStaggeredBailout
                                    {
                                        Unit = unit,
                                        Veh = veh,
                                        ExitAtTime = now + delay,
                                        WreckPos = wreckPos
                                    });
                                }
                                exitOrderIdx++;
                            }
                            else
                            {
                                unit.CoverPosition(null);
                                if (AiCore.TryGetSoldierHandles(unit.Pointer, out var unitHandles))
                                    unitHandles.Stance.LocalCoverProtectedUntil = 0f;
                                ClearEmplacementAssignment(unit.Pointer, markUnreachable: false);
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

                        if (crewSq.IsPlayerInSquad() || !ShouldGroundedTankCrewStayAndJoinInfantry(crewSq, fac, originPos))
                        {
                            AssignCrewSquadRetreatTask(crewSq, fac, originPos, allowWaterSurface: false, replaceExistingIfFar: false);
                        }
                        else if (VisionConfig.DebugLogging.Value)
                        {
                            AiCore.LogDebug(
                                "CrewBailout",
                                $"{fac} tank crew '{crewSq.squadName}' staying on foot to scavenge & reinforce nearby infantry.");
                        }
                    }
                }
                catch { }

                return false;
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
