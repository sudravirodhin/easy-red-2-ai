using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace EasyRed2HumanAI
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BasePlugin
    {
        public const string PluginGuid = "com.antigravity.easyred2.humanai";
        public const string PluginName = "Easy Red 2 - Battle Hardened";
        public const string PluginVersion = "0.6.0";

        internal static Plugin Instance;
        internal static new ManualLogSource Log;
        private Harmony _harmony;

        private void SafePatch(Type patchType)
        {
            try
            {
                _harmony.PatchAll(patchType);
            }
            catch (Exception ex)
            {
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
            SafePatch(typeof(VisionPatches.Soldier_StartFire_Patch));
            SafePatch(typeof(VisionPatches.Soldier_CamoDistanceAwareEnemies_Patch));
            SafePatch(typeof(VisionPatches.Creature_CanSee_Patch));
            SafePatch(typeof(VisionPatches.TurretAI_CanSee_Patch));
            SafePatch(typeof(VisionPatches.AIPlane_GetBestAircraftVisibleEnemy_Patch));
            SafePatch(typeof(VisionPatches.BattleManager_Update_Patch));
            SafePatch(typeof(VisionPatches.BattleManager_OnGUI_Patch));

            // Bundle 2-5: Infantry Combat, Cover, Macro & Vehicles, Veterancy & Personalities, UI & QoL
            SafePatch(typeof(VeteranAndTacticsPatches.SoldierAI_SequentialUpdate_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.SoldierAI_ProcessAiAccuracy_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.SoldierAI_OnSuppressed_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.SoldierAI_UpdateMoveDirection_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.SoldierAI_GetSpacingFromSquad_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.SoldierAI_RotateAndFireToward_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.SoldierAI_MovementRoutine_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.SquadSoldierLabel_RefreshVisual_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.VehicleRepairTask_GetCoverPosition_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.VehicleRepairTask_IsReached_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.VehicleRepairTask_OnCoverReached_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.CombatCover_IsCoverAvailable_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.Squad_SendUnitsToCovers_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.Squad_GetCoveredPositionInArea_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.Soldier_CheckAndOpenDoor_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.Explosion_CreateExplosion_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.Bullet_BulletDamage_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.DeathPanel_ShowDeath_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.TurretAI_RefreshTargets_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.AIPlane_Update_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.Squad_TaskCanStillAttract_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.Squad_UnitCanChangeCover_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.AIVehicle_MovableVehicleRoutine_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.Vehicle_OnVehicleSuppressed_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.VehicleWithWheels_OnTriggerEnter_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.AIVehicle_MoveTowardCurrentNodeTank_Patch));

            // 64-Character Multiplayer Room Name Limit patches
            SafePatch(typeof(VeteranAndTacticsPatches.ServerListMenu_Awake_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.ServerListMenu_Start_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.ServerListMenu_OnCreateMatchPressed_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.MatchData_SetMultiplayerRoomSettings_Patch));

            // Bundle 6: Ballistics, Plane Audio De-Throbber & Battlefield Immersion
            SafePatch(typeof(VeteranAndTacticsPatches.VehiclePlane_UpdateMotorSound_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.BulletInstance_OnHit_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.VehicleParachute_MoveDown_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.CinematicCameraGUI_EnableCinematicCamera_Patch));

            Log.LogInfo($"{PluginName} v{PluginVersion} loaded! (Hotkeys: F6 Config Menu, F8 Toggle ON/OFF, F9 Reload Config, F10 Show Live Stats)");
            Log.LogInfo($"Sight Ranges -> Standing: {VisionConfig.StandingSightRange.Value}m | Crouching: {VisionConfig.CrouchingSightRange.Value}m | Prone: {VisionConfig.ProneSightRange.Value}m | MaxOpticZoom: {VisionConfig.MaxOpticZoomMultiplier.Value}x");
            Log.LogInfo($"Tactics -> VeteranCurve: {VisionConfig.VeteranFullMaturitySeconds.Value}s | FFCone: {VisionConfig.FriendlyFireConeDegrees.Value} deg | AntiWall: {VisionConfig.EnableWallShootingPrevention.Value} | Vaulting: {VisionConfig.EnableWallVaulting.Value} | ExtendedRoomNames: {VisionConfig.EnableExtendedRoomNames.Value}");
        }
    }
}
