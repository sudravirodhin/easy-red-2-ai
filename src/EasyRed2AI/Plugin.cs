using System;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

[assembly: AssemblyVersion(EasyRed2HumanAI.Plugin.PluginVersion + ".0")]
[assembly: AssemblyFileVersion(EasyRed2HumanAI.Plugin.PluginVersion + ".0")]
[assembly: AssemblyInformationalVersion(EasyRed2HumanAI.Plugin.PluginVersion)]

namespace EasyRed2HumanAI
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BasePlugin
    {
        public const string PluginGuid = "com.antigravity.easyred2.humanai";
        public const string PluginName = "Easy Red 2 - Battle Hardened";
        public const string PluginVersion = "0.10.10";

        internal static Plugin Instance;
        internal static new ManualLogSource Log;
        private Harmony _harmony;
        private int _patchesApplied;
        private int _patchesFailed;

        private void SafePatch(Type patchType)
        {
            try
            {
                _harmony.PatchAll(patchType);
                _patchesApplied++;
            }
            catch (Exception ex)
            {
                _patchesFailed++;
                Log.LogError($"Failed to apply patch {patchType.Name}: {ex}");
            }
        }

        public override void Load()
        {
            Instance = this;
            Log = base.Log;

            VisionConfig.Init(Config);

            _harmony = new Harmony(PluginGuid);

            // Bundle 1: Vision, Camo, Threat Sectors & Adaptive AI Brain Tick Scheduler
            SafePatch(typeof(AiCore.Creature_ProcessSequentialUpdates_Patch));
            SafePatch(typeof(AiCore.Soldier_AngleToDirection_Patch));
            SafePatch(typeof(AiCore.Soldier_SignedAngleToDirection_Patch));
            SafePatch(typeof(VisionPatches.Soldier_StartFire_Patch));
            SafePatch(typeof(VisionPatches.Soldier_CamoDistanceAwareEnemies_Patch));
            SafePatch(typeof(VisionPatches.Creature_CanSee_Patch));
            SafePatch(typeof(VisionPatches.TurretAI_CanSee_Patch));
            SafePatch(typeof(VisionPatches.AIPlane_GetBestAircraftVisibleEnemy_Patch));
            SafePatch(typeof(VisionPatches.BattleManager_Update_Patch));
            SafePatch(typeof(VisionPatches.BattleManager_OnGUI_Patch));
            SafePatch(typeof(VisionPatches.ResourcesManager_UsingGUI_Patch));
            SafePatch(typeof(VisionPatches.ResourcesManager_UsingGUIExceptEndBattle_Patch));
            SafePatch(typeof(VisionPatches.BattleLoader_ConnectToNextMatch_Patch));
            SafePatch(typeof(VisionPatches.EndBattleGUI_WaitAnthemEnd_Patch));
            try { AddComponent<VisionPatches.MpTransitionBannerBehaviour>(); } catch (Exception ex) { Log.LogWarning($"MpTransitionBannerBehaviour init skipped: {ex.Message}"); }

            // Bundle 2-5: Infantry Combat, Cover, Macro & Vehicles, Veterancy & Personalities, UI & QoL
            SafePatch(typeof(VeteranAndTacticsPatches.SoldierAI_SequentialUpdate_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.SoldierAI_TryStopBleeding_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.Soldier_UseBandages_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.SoldierAI_IsMoveTime_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.SoldierAI_ProcessAiAccuracy_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.SoldierAI_OnSuppressed_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.SoldierAI_UpdateMoveDirection_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.SoldierAI_GetSpacingFromSquad_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.SoldierAI_CalculateMoveOrderDestination_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.SoldierAI_RotateAndFireToward_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.SoldierAI_MovementRoutine_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.SoldierAI_MoveDirectlyToward_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.SoldierAI_MoveDirectlyTowardNonPhysical_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.SoldierAI_MakeSureAiIsNotClippedInsideCollider_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.SoldierAI_TeleportAlongPathIfPlayerIsNotLooking_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.SquadSoldierLabel_RefreshVisual_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.VehicleRepairTask_GetCoverPosition_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.VehicleRepairTask_IsReached_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.VehicleRepairTask_OnCoverReached_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.CombatCover_IsCoverAvailable_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.Squad_SendUnitsToCovers_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.Squad_GetCoveredPositionInArea_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.Soldier_CheckAndOpenDoor_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.AmmoRefillCrate_Start_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.Soldier_OnSetPlayer_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.Soldier_Throw_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.SoldierAI_MortarTarget_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.KneeMortar_Use_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.Explosion_CreateExplosion_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.Bullet_BulletDamage_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.DeathPanel_ShowDeath_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.Soldier_ExtractDogTag_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.TurretAI_RefreshTargets_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.TurretAI_AutoPilot_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.TurretGun_SetGunTriggerPressed_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.TurretGun_StartFire_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.AIPlane_Update_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.AIPlane_ExitDive_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.AIPlane_NeedRefill_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.AIPlane_RetreatCrew_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.Squad_SquadLeaderRoutine_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.Squad_TaskCanStillAttract_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.Squad_UnitCanChangeCover_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.Squad_SendUnitsToVehicle_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.Squad_OrderLeaveVehicle_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.Squad_OrderRepairVehicle_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.Squad_GetMechanic_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.PlayerController_OpenOrdersMenu_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.InventoryManager_ReceiveNetInventory_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.Squad_OrderLeaveAllVehiclesAndCovers_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.Vehicle_IsCoverAvailable_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.Vehicle_IsCoverOccupied_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.Vehicle_OnCoverReached_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.AIVehicle_SwitchSeatAi_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.PlayerController_SwitchSeat_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.VehicleDamagablePart_GetInteractions_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.Vehicle_HasSpace_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.Vehicle_GetOnVehicleBestPos_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.VehicleWithWheels_GetOnVehicleBestPos_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.Vehicle_GetOnVehicle_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.VehicleSpawner_SpawnVehicle_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.VehicleSpawner_IsAvailable_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.Vehicle_OnDisableVehicle_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.MovableVehicle_OnDisableVehicle_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.Vehicle_OnRepaired_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.AIVehicle_RetreatCrew_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.AIVehicle_MovableVehicleRoutine_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.AIVehicle_MoveTowardCurrentNodeWheeledVehicle_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.AIVehicle_MoveTowardCurrentNodeTank_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.VehicleTank_FixedUpdate_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.A_f_EnqueueOrUpdateOpenNode_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.A_f_CanSkipStringPullNode_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.A_f_ClearGrid_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.A_f_InvalidateRadius_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.Vehicle_OnVehicleSuppressed_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.VehicleWithWheels_OnTriggerEnter_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.VehiclePlane_OnSquadSpawnInside_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.VehiclePlane_FixedUpdate_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.VehiclePlane_OnCollisionEnter_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.VehiclePlane_Explode_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.SyncVehiclePlane_SyncVehiclePosition_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.SyncVehiclePlane_UpdatePosAndRot_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.SyncSoldier_OnPhotonSerializeView_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.TurretGun_SelectBestAmmo_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.TurretWeapon_Reload_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.SoldierAI_UseBestWearedWeaponCheck_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.Squad_MedicRoutine_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.MedicHealingDestination_OnCoverReached_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.Soldier_SurrenderSynched_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.Soldier_Surrender_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.Soldier_SetIncapacitated_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.Soldier_RefreshAnimatorState_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.Soldier_SetAnimatorController_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.Soldier_OnRetreat_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.Soldier_UnSurrender_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.Soldier_MeleeDamageCR_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.ResourcesManager_TrySpotSquad_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.SpawnManager_SpawnAISquadMoveNext_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.SpawnManager_SpawnUnitMoveNext_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.MiniMapGUI_GetNextUnitMarker_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.Squad_GetMarkerIcon_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.MiniMapGUI_Update_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.Marker3DGUI_GetPooled_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.Marker3DGUI_DrawOccluded_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.Marker3DGUI_LateUpdate_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.CircularMenu2_ShowCircle_Patch));

            // 64-Character Multiplayer Room Name Limit & Battle-Stat / Scoreboard Sync Fix patches
            SafePatch(typeof(VeteranAndTacticsPatches.ServerListMenu_Awake_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.ServerListMenu_Start_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.ServerListMenu_OnCreateMatchPressed_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.MatchData_SetMultiplayerRoomSettings_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.MatchData_UpdateNetworkMatchInfo_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.BattleManager_AddBattleStat_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.BattleManager_CountBattleStat_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.BattleManager_OnWin_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.EndBattleGUI_EndBattle_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.Player_SetCustomProperties_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.BattleLoader_OnJoinedRoom_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.BattleLoader_OnPlayerEnteredRoom_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.BattleLoader_OnPlayerPropertiesUpdate_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.BattleLoader_RequestOwnershipAndSpawnOn_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.PlayerController_OnSquadTaskUpdated_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.DamageFeedback_OnSoldierHit_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.DamageFeedback_OnVehicleHit_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.Soldier_Kill_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.Soldier_SetBleeding_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.Soldier_SetOnFire_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.Squad_SetFullySpawned_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.SyncSoldier_SyncSquad_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.Squad_Join_Patch));
            SafePatch(typeof(VisionPatches.Soldier_TryPlayStepSound_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.PlayerGUI_ClearSquadList_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.PlayerGUI_ShowSquadList_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.PlayerGUI_LayoutSquadEntries_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.PlayerGUI_UpdateSquadGUI_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.PlayerGUI_GetSquadOrderPosition_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.SquadSoldierLabel_BeginSpawnOnUnit_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.SyncSoldier_SetControlledByPlayer_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.PlayerController_SetPlayer_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.MiniMapGUI_ShowPlayerList_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.Soldier_UseSyryngeCR_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.GameStatistics_OnAlliedRevived_Patch));

            // Bundle 6: Ballistics, Plane Audio De-Throbber & Battlefield Immersion
            SafePatch(typeof(VeteranAndTacticsPatches.VehiclePlane_UpdateMotorSound_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.Soldier_Say_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.Soldier_SyncSay_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.Soldier_SayClip_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.BulletInstance_OnHit_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.BulletInstance_OnTracerPopped_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.BulletInstance_DestroyFX_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.BulletInstance_TryRichochet_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.VehicleDamagablePart_TryPenetrateArmor_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.Soldier_Damage_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.BodyPart_HitPart_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.Soldier_DamageWithAnimation_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.LoadBalancingClient_OnEvent_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.PhotonNetwork_ExecuteRpc_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.SyncSoldier_UpdateSoldierLife_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.SyncSoldier_SyncDeath_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.ResourcesManager_FactionCanDamageFaction_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.BodyPart_TryDamageWithExplosion_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.ItemGrenade_ActivateGrenade_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.ItemGrenade_ExplodeAfter_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.VehicleDamageableTrackPart_TryDamageWithExplosion_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.VehicleDamagablePart_TryDamageWithExplosion_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.VehicleWithWheels_OnCollisionEnter_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.VehicleDamagablePart_OnVehicleCollision_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.VehicleDamagablePart_InternalPartToDamageText_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.VehicleParachute_MoveDown_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.CinematicCameraGUI_EnableCinematicCamera_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.CinematicCameraGUI_Close_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.PlayerController_LateUpdate_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.PlayerController_ShakeCameraRoutine_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.CinematicCameraController_TrackingRigidbody_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.CinematicCameraController_MoveAndRotateCamera_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.CameraDirector_Awake_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.CameraDirector_Update_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.CameraDirector_CutOrRetryLater_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.CameraDirector_FixedUpdate_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.CameraDirector_LateUpdate_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.CameraDirector_RefreshBrowsingSuppression_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.MissionStatusGUI_LateUpdate_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.ObjectiveGUI_LateUpdate_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.MissionTaskScripted_GetTaskCompletationColor_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.MissionTaskConquerArea_GetTaskCompletationColor_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.ObjectiveGUI_SetPhase_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.ResourcesManager_PlayImpactEffect_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.LimbLeftoverManager_Start_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.CorvoDecal_ApplyMeshData_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.GenericGun_ReloadCR_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.GenericGun_ReloadCRMoveNext_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.Soldier_SwitchTo_ReloadCancel_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.Soldier_Melee_ReloadCancel_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.Soldier_ToggleInventoryKnife_ReloadCancel_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.Soldier_CycleInventoryKnife_ReloadCancel_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.Soldier_InstantiateAndThrowAfter_MoveNext_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.Soldier_CalculateBallisticThrow_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.Soldier_Vault_ReloadCancel_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.PlayerController_Update_ReloadAndBipodCancel_Patch));

            // HitchAutopsy Flight Recorder, Live Perf Overlay & Per-Bot JSONL Action Trace (all default OFF)
            HitchProfiler.RefreshConfigState();
            BotActionTrace.RefreshConfigState();
            string hitchTag = (HitchProfiler.FlightRecorderActive && HitchProfiler.PerfOverlayActive)
                ? " [HitchAutopsy + PerfOverlay ACTIVE]"
                : (HitchProfiler.FlightRecorderActive
                    ? " [HitchAutopsy Flight Recorder ACTIVE]"
                    : (HitchProfiler.PerfOverlayActive ? " [Live Perf Overlay ACTIVE]" : ""));
            string traceTag = BotActionTrace.IsEnabled ? " [BotActionTrace ACTIVE]" : "";

            string osLabel = OperatingSystem.IsWindows() ? "Windows"
                : OperatingSystem.IsMacOS() ? "macOS"
                : OperatingSystem.IsLinux() ? "Linux"
                : "OtherOS";
            Log.LogInfo($"Platform: {osLabel} ({IntPtr.Size * 8}-bit) | Patches: {_patchesApplied}/{_patchesApplied + _patchesFailed} OK ({_patchesFailed} failed) | Offsets: {AiCore.FormatNativeFieldOffsetsSummary()}");

            string cfgHotkeyLabel = VisionConfig.ConfigMenuHotkey != null ? VisionConfig.ConfigMenuHotkey.Value.ToString() : "F6";
            Log.LogInfo($"{PluginName} v{PluginVersion}{hitchTag}{traceTag} loaded! (Hotkeys: F5 Map Picker, {cfgHotkeyLabel} Config Menu, F7 Dogtag Archive, F8 Toggle ON/OFF, F9 Reload Config, F10 Show Live Stats)");
            Log.LogInfo($"Sight Ranges -> Standing: {VisionConfig.StandingSightRange.Value}m | Crouching: {VisionConfig.CrouchingSightRange.Value}m | Prone: {VisionConfig.ProneSightRange.Value}m | MaxOpticZoom: {VisionConfig.MaxOpticZoomMultiplier.Value}x");
            Log.LogInfo($"Tactics -> VeteranCurve: {VisionConfig.VeteranFullMaturitySeconds.Value}s | FFCone: {VisionConfig.FriendlyFireConeDegrees.Value} deg | AntiWall: {VisionConfig.EnableWallShootingPrevention.Value} | Vaulting: {VisionConfig.EnableWallVaulting.Value} | ExtendedRoomNames: {VisionConfig.EnableExtendedRoomNames.Value}");
        }
    }
}
