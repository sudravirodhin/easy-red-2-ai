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
        public const string PluginName = "Easy Red 2 - Humanized AI & Veteran Tactics";
        public const string PluginVersion = "0.3.3";

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

            // Phase 1: Humanized Vision, Stance Stealth, Optics & FOV Cones
            SafePatch(typeof(VisionPatches.Soldier_StartFire_Patch));
            SafePatch(typeof(VisionPatches.Soldier_CamoDistanceAwareEnemies_Patch));
            SafePatch(typeof(VisionPatches.Creature_CanSee_Patch));
            SafePatch(typeof(VisionPatches.TurretAI_CanSee_Patch));
            SafePatch(typeof(VisionPatches.AIPlane_GetBestAircraftVisibleEnemy_Patch));
            SafePatch(typeof(VisionPatches.BattleManager_Update_Patch));
            SafePatch(typeof(VisionPatches.BattleManager_OnGUI_Patch));

            // Phase 2: 15-Minute Veteran Progression, 5-Tier Survival Priority, Muzzle Discipline & Vaulting
            SafePatch(typeof(VeteranAndTacticsPatches.SoldierAI_SequentialUpdate_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.SoldierAI_ProcessAiAccuracy_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.SoldierAI_OnSuppressed_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.SoldierAI_UpdateMoveDirection_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.SoldierAI_RotateAndFireToward_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.SoldierAI_MovementRoutine_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.SquadSoldierLabel_RefreshVisual_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.VehicleRepairTask_GetCoverPosition_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.VehicleRepairTask_IsReached_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.VehicleRepairTask_OnCoverReached_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.CombatCover_IsCoverAvailable_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.Squad_SendUnitsToCovers_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.Squad_GetCoveredPositionInArea_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.Explosion_CreateExplosion_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.Bullet_BulletDamage_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.DeathPanel_ShowDeath_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.TurretAI_RefreshTargets_Patch));
            SafePatch(typeof(VeteranAndTacticsPatches.AIPlane_Update_Patch));

            Log.LogInfo($"{PluginName} v{PluginVersion} loaded! (Hotkeys: F6 Config Menu, F8 Toggle ON/OFF, F9 Reload Config, F10 Show Live Stats)");
            Log.LogInfo($"Sight Ranges -> Standing: {VisionConfig.StandingSightRange.Value}m | Crouching: {VisionConfig.CrouchingSightRange.Value}m | Prone: {VisionConfig.ProneSightRange.Value}m | MaxOpticZoom: {VisionConfig.MaxOpticZoomMultiplier.Value}x");
            Log.LogInfo($"Phase 2 -> VeteranCurve: {VisionConfig.VeteranFullMaturitySeconds.Value}s | FFCone: {VisionConfig.FriendlyFireConeDegrees.Value} deg | AntiWall: {VisionConfig.EnableWallShootingPrevention.Value} | Vaulting: {VisionConfig.EnableWallVaulting.Value}");
        }
    }
}
