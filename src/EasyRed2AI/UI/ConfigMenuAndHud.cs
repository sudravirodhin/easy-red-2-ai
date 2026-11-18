using System;
using System.Collections.Generic;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace EasyRed2HumanAI
{
    public static partial class VisionPatches
    {
        private static string _toastMessage = "";
        private static float _toastExpireTime;
        private static bool _showConfigMenu;
        private static bool _showMapPicker;
        private static bool _showDogTagArchive;
        private static int _lastModOverlayClosedFrame = -1;
        private static bool _revealMapPickerSpoilers;
        private static int _activeConfigTab;

        public static bool IsAnyModOverlayOpen =>
            _showConfigMenu || _showMapPicker || _showDogTagArchive || Time.frameCount == _lastModOverlayClosedFrame;

        private static void EnsureOverlayCursorUnlocked()
        {
            if (!Cursor.visible)
                Cursor.visible = true;
            if (Cursor.lockState != CursorLockMode.None)
                Cursor.lockState = CursorLockMode.None;
        }

        [HarmonyPatch(typeof(ResourcesManager), nameof(ResourcesManager.UsingGUI))]
        public static class ResourcesManager_UsingGUI_Patch
        {
            public static bool Prefix(ref bool __result)
            {
                if (IsAnyModOverlayOpen)
                {
                    __result = true;
                    return false;
                }
                return true;
            }
        }

        [HarmonyPatch(typeof(ResourcesManager), nameof(ResourcesManager.UsingGUIExceptEndBattle))]
        public static class ResourcesManager_UsingGUIExceptEndBattle_Patch
        {
            public static bool Prefix(ref bool __result)
            {
                if (IsAnyModOverlayOpen)
                {
                    __result = true;
                    return false;
                }
                return true;
            }
        }

        public static void ShowToast(string msg, float duration = 4.0f)
        {
            _toastMessage = msg;
            _toastExpireTime = Time.unscaledTime + duration;
            Plugin.Log.LogInfo(msg);
        }

        // =====================================================================
        // In-Game Hotkeys (F5 Map Picker, F6 Menu, F8 Toggle, F9 Reload, F10 Stats)
        // =====================================================================
        [HarmonyPatch(typeof(BattleManager), nameof(BattleManager.Update))]
        public static class BattleManager_Update_Patch
        {
            public static void Prefix(out long __state)
            {
                __state = HitchProfiler.OnBattleManagerUpdatePrefix();
            }

            public static void Postfix(long __state)
            {
                long modProfStart = HitchProfiler.OnBattleManagerUpdatePostfixStart(__state);
                try
                {
                    AiCore.RecordFrameTiming();
                    AiCore.UpdateNightVisionSnapshotIfDue();
                    AiCore.WarmWorldSnapshotIfDue();
                    if (Time.timeScale > 0.01f)
                        AiCore.RefreshActiveLandMinesIfDue();
                    UpdatePendingMpMapSwitch();
                    CleanupExpiredFiringRecords();
                    try { VeteranAndTacticsPatches.RenderVeteran3DMarkersAndCleanup(); } catch { }
                    VeteranAndTacticsPatches.UpdateMacroObjectiveAndEmplacementDirector();
                    VeteranAndTacticsPatches.UpdateConquestQuartermasterIfNeeded();
                    VeteranAndTacticsPatches.UpdateFriendlyPlayerCrosshairYield();
                    VeteranAndTacticsPatches.UpdateFriendlyVehicleCollisionRestores();
                    VeteranAndTacticsPatches.UpdateGroundedCrewRetreatTasks();
                    VeteranAndTacticsPatches.UpdateMultiplayerBattleStatSync();
                    VeteranAndTacticsPatches.UpdateLobbyJoinMotdQueue();
                    VeteranAndTacticsPatches.UpdateLonePlayerSquadStatusAndRejoin();
                    VeteranAndTacticsPatches.UpdateSquadRosterHeaderAndFastSync();
                    VeteranAndTacticsPatches.UpdateQuickTapSpot(_showConfigMenu || _showMapPicker || _showDogTagArchive);
                    if (_showMapPicker)
                        BattleManager_OnGUI_Patch.StepResolveSelectedCampaignBattles();

                    var input = UnityInput.Current;
                    if (input != null)
                    {
                        KeyCode mapPickerKey = VisionConfig.MapPickerHotkey != null ? VisionConfig.MapPickerHotkey.Value : KeyCode.F5;
                        KeyCode dogTagKey = VisionConfig.DogTagArchiveHotkey != null ? VisionConfig.DogTagArchiveHotkey.Value : KeyCode.F7;
                        if (input.GetKeyDown(KeyCode.Escape) && (_showConfigMenu || _showMapPicker || _showDogTagArchive))
                        {
                            _showConfigMenu = false;
                            _showMapPicker = false;
                            _showDogTagArchive = false;
                            _lastModOverlayClosedFrame = Time.frameCount;
                        }
                        else if (mapPickerKey != KeyCode.None && input.GetKeyDown(mapPickerKey))
                        {
                            _showMapPicker = !_showMapPicker;
                            if (_showMapPicker)
                            {
                                _showConfigMenu = false;
                                _showDogTagArchive = false;
                                BattleManager_OnGUI_Patch.EnsureMapPickerCatalog();
                                BattleManager_OnGUI_Patch.TryAutoSelectActiveMissionInPicker();
                                EnsureOverlayCursorUnlocked();
                            }
                            else
                            {
                                _lastModOverlayClosedFrame = Time.frameCount;
                            }
                        }
                        else if (dogTagKey != KeyCode.None && input.GetKeyDown(dogTagKey))
                        {
                            _showDogTagArchive = !_showDogTagArchive;
                            if (_showDogTagArchive)
                            {
                                _showConfigMenu = false;
                                _showMapPicker = false;
                                EnsureOverlayCursorUnlocked();
                            }
                            else
                            {
                                _lastModOverlayClosedFrame = Time.frameCount;
                            }
                        }
                        else if (input.GetKeyDown(KeyCode.F6))
                        {
                            _showConfigMenu = !_showConfigMenu;
                            if (_showConfigMenu)
                            {
                                _showMapPicker = false;
                                _showDogTagArchive = false;
                                EnsureOverlayCursorUnlocked();
                            }
                            else
                            {
                                _lastModOverlayClosedFrame = Time.frameCount;
                            }
                        }
                        else if (input.GetKeyDown(KeyCode.F8))
                        {
                            VisionConfig.EnableMod.Value = !VisionConfig.EnableMod.Value;
                            Plugin.Instance.Config.Save();
                            VeteranAndTacticsPatches.RefreshBottomLeftSquadRosterVisuals();
                            string state = VisionConfig.EnableMod.Value
                                ? $"ENABLED (Stand:{VisionConfig.StandingSightRange.Value}m, Crouch:{VisionConfig.CrouchingSightRange.Value}m, Prone:{VisionConfig.ProneSightRange.Value}m)"
                                : "DISABLED (Vanilla AI)";
                            ShowToast($"[Battle Hardened v{Plugin.PluginVersion}] Mod {state}");
                        }
                        else if (input.GetKeyDown(KeyCode.F9))
                        {
                            Plugin.Instance.Config.Reload();
                            HitchProfiler.RefreshConfigState();
                            BotActionTrace.RefreshConfigState();
                            VeteranAndTacticsPatches.SyncPtfoRoomPropertyIfMaster();
                            VeteranAndTacticsPatches.RefreshBottomLeftSquadRosterVisuals();
                            ShowToast($"[Battle Hardened v{Plugin.PluginVersion}] Reloaded Config! Stand:{VisionConfig.StandingSightRange.Value}m | Crouch:{VisionConfig.CrouchingSightRange.Value}m | Prone:{VisionConfig.ProneSightRange.Value}m | Personalities:{VisionConfig.EnableRolePersonalities.Value}");
                        }
                        else if (input.GetKeyDown(KeyCode.F10))
                        {
                            VeteranAndTacticsPatches.CleanupDictionaries();
                            string mpSbSummary = VeteranAndTacticsPatches.TryGetLiveScoreboardSnapshot(
                                out short sbScore, out short sbKills, out short sbDeaths, out bool ptfoOn)
                                ? $" | MPScore:{sbScore} (K:{sbKills} D:{sbDeaths} PTFO:{(ptfoOn ? "ON" : "OFF")} +{VeteranAndTacticsPatches.PtfoTotalPointsEarned}[ObjK:{VeteranAndTacticsPatches.PtfoObjKillBonusesCount} Tick:{VeteranAndTacticsPatches.PtfoActiveTicksCount} Cap:{VeteranAndTacticsPatches.PtfoCapturesCount} Rev:{VeteranAndTacticsPatches.PtfoRevivesCount} Veh:{VeteranAndTacticsPatches.PtfoVehicleDisablesCount}])"
                                : "";
                            ShowToast(
                                $"[Battle Hardened v{Plugin.PluginVersion} Telemetry] MaxFrame:{AiCore.RollingMaxFrameMs:F1}ms (Hitches:{AiCore.TotalHitchesOver28ms}) | Vets:{VeteranAndTacticsPatches.ActiveVeteransCount} (Prom:{VeteranAndTacticsPatches.FieldPromotionsCount}) | ExtraTicks:{AiCore.TotalAdaptiveAiExtraTicks} (PingSkip:{AiCore.TotalPingGovernorSkippedCycles}) | FFSaved:{VeteranAndTacticsPatches.FriendlyFirePrevented} (Yield:{VeteranAndTacticsPatches.PlayerCrosshairYieldsCount}) | MedRedirect:{VeteranAndTacticsPatches.MedicTriageRedirectsCount}\n" +
                                $"GOAP:{VeteranAndTacticsPatches.GoapPlansExecutedCount} (Anvil:{VeteranAndTacticsPatches.TeamNetAnvilHammerCount} Flank:{VeteranAndTacticsPatches.GoapPinAndFlankCount} Charge:{VeteranAndTacticsPatches.SquadChargesOrderedCount} Balk:{VeteranAndTacticsPatches.SquadLeaderBalksCount}/{VeteranAndTacticsPatches.IndividualSelfPreservationBalksCount} Covert:{VeteranAndTacticsPatches.SquadCovertOrdersCount} Follow:{VeteranAndTacticsPatches.SquadFollowOrdersCount} Retreat:{VeteranAndTacticsPatches.TacticalRetreatsCount} Smoke:{VeteranAndTacticsPatches.GoapSmokeScreensCount} RadioCut:{VeteranAndTacticsPatches.RadioNetCutoffsCount}) | WepUp:{VeteranAndTacticsPatches.RoleWeaponUpgradesCount} | BuddyResup:{VeteranAndTacticsPatches.BuddyResuppliesCount} | RadioLoot:{VeteranAndTacticsPatches.FieldRadiosInheritedCount} | Helmets:{VeteranAndTacticsPatches.HelmetsRecoveredCount} | Dragged:{VeteranAndTacticsPatches.CasualtiesDraggedCount} | Sap:{AiCore.SpecialMunitionsUsedCount}\n" +
                                $"Drops:{VeteranAndTacticsPatches.SelfPreservationDrops} | Peeks:{VeteranAndTacticsPatches.AutoPeeksTriggered} | BndBlk:{VeteranAndTacticsPatches.UnsafeBandagesBlockedCount} | IndoorCrouch:{VeteranAndTacticsPatches.IndoorPronePreventedCount} | Pies:{VeteranAndTacticsPatches.CornerPiesExecutedCount} | TankCov:{VeteranAndTacticsPatches.TankHullCoverUsesCount} | Bounds:{VeteranAndTacticsPatches.BoundingOverwatchHoldsCount} | Doors:{VeteranAndTacticsPatches.DoorsStrategicallyClosedCount} | Bipods:{VeteranAndTacticsPatches.BipodsDeployed} | WallBlk:{VeteranAndTacticsPatches.WallsBlocked} | FragBlk:{VeteranAndTacticsPatches.UselessTankFragsBlockedCount} | Melee:{VeteranAndTacticsPatches.MeleeAttacksTriggered} (Sup:{VeteranAndTacticsPatches.CloseGunfireMeleeSuppressedCount}) | Vault:{VeteranAndTacticsPatches.WallsVaulted} (Rej:{VeteranAndTacticsPatches.SlopedVaultsRejectedCount}) | Surr:{VeteranAndTacticsPatches.ShellShockSurrendersCount}/Lib:{VeteranAndTacticsPatches.ComradesLiberated}\n" +
                                $"Garrisons:{VeteranAndTacticsPatches.MacroGarrisonsAssignedCount} (Pivots:{VeteranAndTacticsPatches.MacroObjectivePivotsCount}) | ConqBuy:{VeteranAndTacticsPatches.ConquestPurchasesCount} | SpawnSaved:{VeteranAndTacticsPatches.UnderwaterSpawnsRescuedCount} | Emplace:{VeteranAndTacticsPatches.StaticGunsStaffedCount} | APC:{VeteranAndTacticsPatches.SmartApcDismountsCount} | TankHold:{VeteranAndTacticsPatches.TankStandoffHoldsCount} | TankSpread:{VeteranAndTacticsPatches.ArmoredDispersionAdjustmentsCount} | SmartAmmo:{VeteranAndTacticsPatches.SmartAmmoSwitchesCount} | Unsticks:{VeteranAndTacticsPatches.TankRockUnsticksCount} (Unstk:{VeteranAndTacticsPatches.FootBotUnsticksCount}) | Repairs:{VeteranAndTacticsPatches.ShelteredRepairsCount} | RoadkillSaved:{VeteranAndTacticsPatches.FriendlyRoadkillsPreventedCount} (ClientRK:{VeteranAndTacticsPatches.RemoteClientRoadkillsAssistedCount})\n" +
                                $"CombArms:{VeteranAndTacticsPatches.CombinedArmsPriorityAssistsCount} (Intercom:{VeteranAndTacticsPatches.TankIntercomHandoffsCount} Kamikaze:{VeteranAndTacticsPatches.KamikazeDivesTriggeredCount}) | Callouts:{VeteranAndTacticsPatches.LocalCalloutsSharedCount} | Supp:{VeteranAndTacticsPatches.SuppressiveEventsCount} | WoodPen:{VeteranAndTacticsPatches.ThinCoverPenetrationsCount} | TopAP:{VeteranAndTacticsPatches.AircraftTopAttackPenetrationsAssistedCount} (HostAssist:{VeteranAndTacticsPatches.HostVanillaClientApAssistsCount}) | CrewBlastBlk:{VeteranAndTacticsPatches.EnclosedCrewExplosionHitsBlockedCount} | CrewClipBlk:{VeteranAndTacticsPatches.EnclosedCrewBulletClipsBlockedCount} | AirMgBlk:{VeteranAndTacticsPatches.AirplanePilotMgHitsBlockedCount} | Chutes:{VeteranAndTacticsPatches.ParachutesDriftedCount} | BloodOcc:{VeteranAndTacticsPatches.BloodSmokeWaterOccludedCount}\n" +
                                $"Handles:{AiCore.ActiveSoldierHandlesCount}/{AiCore.ActiveVehicleHandlesCount} | MoveOvr:{AiCore.MoveOverridePreemptionsCount} | BotRPC:{AiCore.BotRpcBudgetGrantedCount}/{AiCore.BotRpcBudgetThrottledCount} | NetFlush:{VeteranAndTacticsPatches.ScoreboardCoalescedFlushesCount} (Sup:{VeteranAndTacticsPatches.ScoreboardVanillaPacketsSuppressedCount}){mpSbSummary}",
                                9.5f);
                        }
                        else if (input.GetKeyDown(KeyCode.F) &&
                                 !_showConfigMenu &&
                                 !_showMapPicker &&
                                 !_showDogTagArchive &&
                                 GUIUtility.keyboardControl == 0 &&
                                 VeteranAndTacticsPatches.IsSpectatorCameraModeActive(out _, out bool canCycleSpec) &&
                                 canCycleSpec)
                        {
                            ShowToast(VeteranAndTacticsPatches.CycleSpectatorTacticalHudMode(), 2.4f);
                        }
                    }

                    // CRITICAL: Check CanEmitDebugLog("Heartbeat", 15.0f) BEFORE building the multi-line string so zero
                    // string concatenations or int.ToString() allocations occur on the 99.9% of frames while on cooldown!
                    if (AiCore.CanEmitDebugLog("Heartbeat", 15.0f))
                    {
                        float dtMs = Mathf.Max(Time.unscaledDeltaTime, 0.0001f) * 1000f;
                        float fps = 1000f / dtMs;
                        string mpSbHb = VeteranAndTacticsPatches.TryGetLiveScoreboardSnapshot(
                            out short sbScore, out short sbKills, out short sbDeaths, out bool ptfoOn)
                            ? $" | MPScore:{sbScore}(K:{sbKills} D:{sbDeaths} PTFO:{(ptfoOn ? "ON" : "OFF")}+{VeteranAndTacticsPatches.PtfoTotalPointsEarned}[K:{VeteranAndTacticsPatches.PtfoObjKillBonusesCount} T:{VeteranAndTacticsPatches.PtfoActiveTicksCount} C:{VeteranAndTacticsPatches.PtfoCapturesCount} R:{VeteranAndTacticsPatches.PtfoRevivesCount} V:{VeteranAndTacticsPatches.PtfoVehicleDisablesCount}])"
                            : "";
                        AiCore.LogDebug(
                            "Heartbeat",
                            $"FPS:{fps:F0} ({dtMs:F1}ms, Max15s:{AiCore.RollingMaxFrameMs:F1}ms, Hitches:{AiCore.TotalHitchesOver28ms}) | Vets:{VeteranAndTacticsPatches.ActiveVeteransCount} (Prom:{VeteranAndTacticsPatches.FieldPromotionsCount}) | " +
                            $"GOAP:{VeteranAndTacticsPatches.GoapPlansExecutedCount} (Anvil:{VeteranAndTacticsPatches.TeamNetAnvilHammerCount} PinFlank:{VeteranAndTacticsPatches.GoapPinAndFlankCount} Charge:{VeteranAndTacticsPatches.SquadChargesOrderedCount} Balk:{VeteranAndTacticsPatches.SquadLeaderBalksCount}/{VeteranAndTacticsPatches.IndividualSelfPreservationBalksCount} Covert:{VeteranAndTacticsPatches.SquadCovertOrdersCount} Follow:{VeteranAndTacticsPatches.SquadFollowOrdersCount} Retreat:{VeteranAndTacticsPatches.TacticalRetreatsCount} Smoke:{VeteranAndTacticsPatches.GoapSmokeScreensCount} WepUp:{VeteranAndTacticsPatches.RoleWeaponUpgradesCount} BuddyResup:{VeteranAndTacticsPatches.BuddyResuppliesCount} RadioLoot:{VeteranAndTacticsPatches.FieldRadiosInheritedCount} Helmets:{VeteranAndTacticsPatches.HelmetsRecoveredCount} Dragged:{VeteranAndTacticsPatches.CasualtiesDraggedCount} Sap:{AiCore.SpecialMunitionsUsedCount} RadioCut:{VeteranAndTacticsPatches.RadioNetCutoffsCount}) | " +
                            $"Drops:{VeteranAndTacticsPatches.SelfPreservationDrops} | Peeks:{VeteranAndTacticsPatches.AutoPeeksTriggered} | BndBlk:{VeteranAndTacticsPatches.UnsafeBandagesBlockedCount} | IndoorCrouch:{VeteranAndTacticsPatches.IndoorPronePreventedCount} | Pies:{VeteranAndTacticsPatches.CornerPiesExecutedCount} | TankCov:{VeteranAndTacticsPatches.TankHullCoverUsesCount} | " +
                            $"Callouts:{VeteranAndTacticsPatches.LocalCalloutsSharedCount} (KillVoice:{VeteranAndTacticsPatches.BotKillCalloutsCount}) | Supp:{VeteranAndTacticsPatches.SuppressiveEventsCount} | " +
                            $"Bounds:{VeteranAndTacticsPatches.BoundingOverwatchHoldsCount} | Garrisons:{VeteranAndTacticsPatches.MacroGarrisonsAssignedCount} (Pivots:{VeteranAndTacticsPatches.MacroObjectivePivotsCount}) | ConqBuy:{VeteranAndTacticsPatches.ConquestPurchasesCount} | SpawnSaved:{VeteranAndTacticsPatches.UnderwaterSpawnsRescuedCount} | " +
                            $"Emplace:{VeteranAndTacticsPatches.StaticGunsStaffedCount} | APC:{VeteranAndTacticsPatches.SmartApcDismountsCount} | " +
                            $"TankHold:{VeteranAndTacticsPatches.TankStandoffHoldsCount} | TankSpread:{VeteranAndTacticsPatches.ArmoredDispersionAdjustmentsCount} | SmartAmmo:{VeteranAndTacticsPatches.SmartAmmoSwitchesCount} | Unsticks:{VeteranAndTacticsPatches.TankRockUnsticksCount} (Unstk:{VeteranAndTacticsPatches.FootBotUnsticksCount}) | MedRedirect:{VeteranAndTacticsPatches.MedicTriageRedirectsCount} | RoadkillSaved:{VeteranAndTacticsPatches.FriendlyRoadkillsPreventedCount} (ClientRK:{VeteranAndTacticsPatches.RemoteClientRoadkillsAssistedCount}) | CombArms:{VeteranAndTacticsPatches.CombinedArmsPriorityAssistsCount} (Intercom:{VeteranAndTacticsPatches.TankIntercomHandoffsCount} Kamikaze:{VeteranAndTacticsPatches.KamikazeDivesTriggeredCount}) | " +
                            $"Bridges:{VeteranAndTacticsPatches.BridgeCrossingsAssistedCount} | Surr:{VeteranAndTacticsPatches.ShellShockSurrendersCount} (Lib:{VeteranAndTacticsPatches.ComradesLiberated}) | " +
                            $"WoodPen:{VeteranAndTacticsPatches.ThinCoverPenetrationsCount} | TopAP:{VeteranAndTacticsPatches.AircraftTopAttackPenetrationsAssistedCount} (HostAssist:{VeteranAndTacticsPatches.HostVanillaClientApAssistsCount}) | CrewBlastBlk:{VeteranAndTacticsPatches.EnclosedCrewExplosionHitsBlockedCount} | CrewClipBlk:{VeteranAndTacticsPatches.EnclosedCrewBulletClipsBlockedCount} | AirMgBlk:{VeteranAndTacticsPatches.AirplanePilotMgHitsBlockedCount} | PlaneAudio:{VeteranAndTacticsPatches.PlaneAudioLoopsDesyncedCount} | Chutes:{VeteranAndTacticsPatches.ParachutesDriftedCount} | BloodOcc:{VeteranAndTacticsPatches.BloodSmokeWaterOccludedCount} | " +
                            $"Doors:{VeteranAndTacticsPatches.DoorsStrategicallyClosedCount} | Bipods:{VeteranAndTacticsPatches.BipodsDeployed} | WallBlk:{VeteranAndTacticsPatches.WallsBlocked} | " +
                            $"Melee:{VeteranAndTacticsPatches.MeleeAttacksTriggered} (Sup:{VeteranAndTacticsPatches.CloseGunfireMeleeSuppressedCount}) | Vault:{VeteranAndTacticsPatches.WallsVaulted} (Rej:{VeteranAndTacticsPatches.SlopedVaultsRejectedCount}) | TankBlk:{VeteranAndTacticsPatches.UselessTankFirePrevented} (Crew:{VeteranAndTacticsPatches.ExposedCrewTargeted}, Frag:{VeteranAndTacticsPatches.UselessTankFragsBlockedCount}) | " +
                            $"Ticks:{AiCore.TotalAdaptiveAiExtraTicks} (PingSkip:{AiCore.TotalPingGovernorSkippedCycles}) | FFSaved:{VeteranAndTacticsPatches.FriendlyFirePrevented} (Yield:{VeteranAndTacticsPatches.PlayerCrosshairYieldsCount}) | Repairs:{VeteranAndTacticsPatches.ShelteredRepairsCount} | Handles:{AiCore.ActiveSoldierHandlesCount}/{AiCore.ActiveVehicleHandlesCount} | MoveOvr:{AiCore.MoveOverridePreemptionsCount} | BotRPC:{AiCore.BotRpcBudgetGrantedCount}/{AiCore.BotRpcBudgetThrottledCount} | NetFlush:{VeteranAndTacticsPatches.ScoreboardCoalescedFlushesCount}(Sup:{VeteranAndTacticsPatches.ScoreboardVanillaPacketsSuppressedCount}){mpSbHb}",
                            15.0f);
                    }
                }
                catch (Exception ex)
                {
                    AiCore.LogDebug("UpdateErr", ex.Message, 10.0f);
                }
                finally
                {
                    HitchProfiler.OnBattleManagerUpdatePostfixEnd(modProfStart);
                }
            }
        }

        internal static float GetEffectiveImGuiScale(out float virtualW, out float virtualH)
        {
            float sw = Mathf.Max(1f, Screen.width);
            float sh = Mathf.Max(1f, Screen.height);
            float autoScale = Mathf.Clamp(sh / 1080f, 1.0f, 2.5f);
            float userMult = VisionConfig.UiScaleMultiplier != null
                ? Mathf.Clamp(VisionConfig.UiScaleMultiplier.Value, 0.75f, 1.50f)
                : 1.0f;
            float scale = autoScale * userMult;

            float maxScaleW = sw / 850f;
            float maxScaleH = sh / 704f;
            float maxAllowed = Mathf.Max(0.75f, Mathf.Min(maxScaleW, maxScaleH));
            if (scale > maxAllowed)
                scale = maxAllowed;
            if (scale < 0.75f)
                scale = 0.75f;

            virtualW = sw / scale;
            virtualH = sh / scale;
            return scale;
        }

        [HarmonyPatch(typeof(BattleManager), nameof(BattleManager.OnGUI))]
        public static partial class BattleManager_OnGUI_Patch
        {
            public static void Prefix(out long __state)
            {
                __state = HitchProfiler.BeginTopLevel();
            }

            public static void Postfix(long __state)
            {
                Matrix4x4 oldGuiMatrix = GUI.matrix;
                try
                {
                    // Draw 3D in-world squad member overhead names BEFORE applying the 1080p virtual UI matrix
                    // because GuiExtension.OutlinedLabel uses raw Screen.width / Screen.height pixel coordinates
                    // matching PlayerController.OnGUI (0x1807CBD20).
                    VeteranAndTacticsPatches.DrawInWorldSquadMemberNames();

                    float uiScale = GetEffectiveImGuiScale(out float virtualW, out float virtualH);
                    if (Mathf.Abs(uiScale - 1.0f) > 0.001f)
                    {
                        GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(uiScale, uiScale, 1f));
                    }

                    float toastBottomY = 0f;
                    if (Time.unscaledTime < _toastExpireTime && !string.IsNullOrEmpty(_toastMessage))
                    {
                        int lineCount = 1;
                        for (int i = 0; i < _toastMessage.Length; i++)
                        {
                            if (_toastMessage[i] == '\n')
                                lineCount++;
                        }
                        bool isMultiLine = lineCount > 1 || _toastMessage.Length > 120;
                        float toastW = isMultiLine ? Mathf.Min(virtualW - 40f, 1240f) : Mathf.Min(virtualW - 40f, 980f);
                        float toastH = isMultiLine ? Mathf.Max(42f, 16f + lineCount * 21f) : 38f;
                        toastBottomY = 20f + toastH;

                        GUI.color = new Color(0.03f, 0.04f, 0.05f, 0.88f);
                        GUI.Box(new Rect(20f, 20f, toastW, toastH), "");

                        GUIStyle lblStyle = GUI.skin != null ? GUI.skin.label : null;
                        bool oldWrap = false;
                        int oldSize = 0;
                        if (lblStyle != null)
                        {
                            oldWrap = lblStyle.wordWrap;
                            oldSize = lblStyle.fontSize;
                            lblStyle.wordWrap = true;
                            lblStyle.fontSize = isMultiLine ? 13 : 14;
                        }

                        GUI.color = Color.Lerp(Color.yellow, Color.white, 0.35f);
                        GUI.Label(new Rect(30f, 26f, toastW - 20f, toastH - 10f), _toastMessage);

                        if (lblStyle != null)
                        {
                            lblStyle.wordWrap = oldWrap;
                            lblStyle.fontSize = oldSize;
                        }
                        GUI.color = Color.white;
                    }

                    // Render Death Screen Kill Attribution fallback banner (if native DeathPanel is hidden)
                    if (VisionConfig.EnableMod.Value &&
                        VisionConfig.EnableDeathScreenAttribution.Value &&
                        Time.unscaledTime < VeteranAndTacticsPatches.ActiveDeathAttributionExpireTime &&
                        !string.IsNullOrEmpty(VeteranAndTacticsPatches.ActiveDeathAttributionText))
                    {
                        Soldier cur = Soldier.CurrentControlledSoldierOrNull();
                        bool respawnedIntoNewSoldier = cur != null &&
                                                       cur.IsAlive &&
                                                       cur.Pointer != VeteranAndTacticsPatches.ActiveDeathAttributionVictimPtr;

                        if (respawnedIntoNewSoldier && Time.unscaledTime > VeteranAndTacticsPatches.ActiveDeathAttributionStartTime + 4.2f)
                        {
                            VeteranAndTacticsPatches.ActiveDeathAttributionExpireTime = 0f;
                        }
                        else
                        {
                            float boxW = Mathf.Min(virtualW - 40f, 920f);
                            float boxH = 58f;
                            float boxX = (virtualW - boxW) * 0.5f;
                            float boxY = virtualH * 0.78f;

                            GUI.color = new Color(0.04f, 0.04f, 0.05f, 0.90f);
                            GUI.Box(new Rect(boxX, boxY, boxW, boxH), "");
                            GUI.Box(new Rect(boxX + 2f, boxY + 2f, boxW - 4f, boxH - 4f), "");

                            GUIStyle labelStyle = GUI.skin != null ? GUI.skin.label : null;
                            int oldFontSize = 0;
                            TextAnchor oldAlign = TextAnchor.UpperLeft;
                            FontStyle oldFontStyle = FontStyle.Normal;
                            bool oldWordWrap = false;

                            if (labelStyle != null)
                            {
                                oldFontSize = labelStyle.fontSize;
                                oldAlign = labelStyle.alignment;
                                oldFontStyle = labelStyle.fontStyle;
                                oldWordWrap = labelStyle.wordWrap;

                                labelStyle.fontSize = 19;
                                labelStyle.alignment = TextAnchor.MiddleCenter;
                                labelStyle.fontStyle = FontStyle.Bold;
                                labelStyle.wordWrap = true;
                            }

                            Rect textRect = new Rect(boxX + 16f, boxY + 4f, boxW - 32f, boxH - 8f);
                            Rect shadowRect = new Rect(textRect.x + 1.5f, textRect.y + 1.5f, textRect.width, textRect.height);

                            GUI.color = new Color(0f, 0f, 0f, 0.92f);
                            GUI.Label(shadowRect, VeteranAndTacticsPatches.ActiveDeathAttributionText);

                            if (VeteranAndTacticsPatches.ActiveDeathAttributionIsFF)
                                GUI.color = new Color(1.0f, 0.36f, 0.36f, 1.0f);
                            else if (VeteranAndTacticsPatches.ActiveDeathAttributionIsSelf)
                                GUI.color = new Color(1.0f, 0.74f, 0.30f, 1.0f);
                            else
                                GUI.color = new Color(0.96f, 0.93f, 0.85f, 1.0f);

                            GUI.Label(textRect, VeteranAndTacticsPatches.ActiveDeathAttributionText);

                            if (labelStyle != null)
                            {
                                labelStyle.fontSize = oldFontSize;
                                labelStyle.alignment = oldAlign;
                                labelStyle.fontStyle = oldFontStyle;
                                labelStyle.wordWrap = oldWordWrap;
                            }

                            GUI.color = Color.white;
                        }
                    }

                    if (_showConfigMenu)
                    {
                        EnsureOverlayCursorUnlocked();
                        DrawConfigMenu(virtualW, virtualH);
                    }
                    else if (_showDogTagArchive)
                    {
                        EnsureOverlayCursorUnlocked();
                        VeteranAndTacticsPatches.DrawDogTagArchiveWindow(
                            virtualW,
                            virtualH,
                            onClose: () =>
                            {
                                _showDogTagArchive = false;
                                _lastModOverlayClosedFrame = Time.frameCount;
                            },
                            onOpenConfigMenu: () =>
                            {
                                _showDogTagArchive = false;
                                _showConfigMenu = true;
                            });
                    }
                    else if (_showMapPicker)
                    {
                        EnsureOverlayCursorUnlocked();
                        DrawMapPickerWindow(virtualW, virtualH);
                    }
                    else
                    {
                        VeteranAndTacticsPatches.DrawSpectatorBotInspectorGui(virtualW, virtualH, toastBottomY);
                    }
                }
                catch { }
                finally
                {
                    GUI.matrix = oldGuiMatrix;
                    HitchProfiler.End(HitchBucket.OnGUI, __state);
                }
            }

            private static readonly string _cachedConfigHeaderTitle =
                $"Easy Red 2: Battle Hardened v{Plugin.PluginVersion}  |  Feature Menu (F6)";
            private static KeyCode _cachedDtBtnKey = (KeyCode)(-999);
            private static string _cachedDtBtnText = "Dogtag Archive (F7)";
            private static KeyCode _cachedMpBtnKey = (KeyCode)(-999);
            private static string _cachedMpBtnText = "Open Map Picker (F5)";
            private static readonly string[] _configTabNames =
            {
                "1. GOAP & Bot Behaviors",
                "2. Vanilla Fixes",
                "3. Vision & Realism",
                "4. Vets & Roles",
                "5. Optimizations & QoL"
            };
            private static int _cachedUiScalePct = -1;
            private static int _cachedEffScaleX100 = -1;
            private static string _cachedUiScaleHeaderText = "=== 4K / High-DPI UI Scale: 100% (1.00x) ===";
            private static readonly Dictionary<string, string> _sectionHeaderLabelCache = new Dictionary<string, string>(32, StringComparer.Ordinal);
            private static readonly Dictionary<string, string> _toggleOnLabelCache = new Dictionary<string, string>(96, StringComparer.Ordinal);
            private static readonly Dictionary<string, string> _toggleOffLabelCache = new Dictionary<string, string>(96, StringComparer.Ordinal);

            private static void DrawConfigMenu(float virtualW, float virtualH)
            {
                float panelW = 820f;
                float panelH = 684f;
                float panelX = (virtualW - panelW) * 0.5f;
                float panelY = (virtualH - panelH) * 0.5f;

                GUI.color = new Color(0.06f, 0.07f, 0.09f, 0.95f);
                GUI.Box(new Rect(panelX, panelY, panelW, panelH), "");

                GUI.color = new Color(1.0f, 0.88f, 0.35f, 1.0f);
                GUI.Label(new Rect(panelX + 20f, panelY + 10f, panelW - 410f, 22f), _cachedConfigHeaderTitle);

                KeyCode dtKey = VisionConfig.DogTagArchiveHotkey != null ? VisionConfig.DogTagArchiveHotkey.Value : KeyCode.F7;
                if (dtKey != _cachedDtBtnKey)
                {
                    _cachedDtBtnKey = dtKey;
                    _cachedDtBtnText = $"Dogtag Archive ({dtKey})";
                }
                GUI.color = new Color(0.55f, 0.96f, 0.72f, 1.0f);
                if (GUI.Button(new Rect(panelX + panelW - 392f, panelY + 8f, 184f, 24f), _cachedDtBtnText))
                {
                    _showConfigMenu = false;
                    _showMapPicker = false;
                    _showDogTagArchive = true;
                }

                KeyCode mpKey = VisionConfig.MapPickerHotkey != null ? VisionConfig.MapPickerHotkey.Value : KeyCode.F5;
                if (mpKey != _cachedMpBtnKey)
                {
                    _cachedMpBtnKey = mpKey;
                    _cachedMpBtnText = $"Open Map Picker ({mpKey})";
                }
                GUI.color = new Color(0.55f, 0.90f, 1.0f, 1.0f);
                if (GUI.Button(new Rect(panelX + panelW - 202f, panelY + 8f, 182f, 24f), _cachedMpBtnText))
                {
                    _showConfigMenu = false;
                    _showDogTagArchive = false;
                    _showMapPicker = true;
                    EnsureMapPickerCatalog();
                    TryAutoSelectActiveMissionInPicker();
                }

                float tabY = panelY + 38f;
                float tabW = (panelW - 40f - 16f) / 5f;
                for (int t = 0; t < _configTabNames.Length; t++)
                {
                    bool active = _activeConfigTab == t;
                    GUI.color = active
                        ? new Color(1.0f, 0.86f, 0.32f, 1.0f)
                        : new Color(0.68f, 0.74f, 0.82f, 0.92f);
                    if (GUI.Button(new Rect(panelX + 20f + t * (tabW + 4f), tabY, tabW, 26f), _configTabNames[t]))
                    {
                        _activeConfigTab = t;
                    }
                }

                float colLeftX = panelX + 20f;
                float colRightX = panelX + 418f;
                float colW = 382f;
                float rowH = 23f;
                float startY = panelY + 74f;
                float yL = startY;
                float yR = startY;

                switch (_activeConfigTab)
                {
                    case 0: // 1. GOAP & Bot Behaviors
                        DrawSectionHeaderWithBundle(colLeftX, ref yL, colW, "GOAP Planner & Macro Command",
                            VisionConfig.EnableTacticalGoapPlanner,
                            VisionConfig.EnableTacticalVoiceCallouts,
                            VisionConfig.EnableRadiomanIntelNet,
                            VisionConfig.EnableMacroObjectiveDirector,
                            VisionConfig.EnableConquestEconomyQuartermaster,
                            VisionConfig.EnableProactiveEmplacementStaffing,
                            VisionConfig.EnableCombinedArmsPrioritySupport);
                        DrawToggleButton(colLeftX, ref yL, colW, rowH, "3-Tier GOAP Planner & Squad Blackboard", VisionConfig.EnableTacticalGoapPlanner);
                        DrawToggleButton(colLeftX, ref yL, colW, rowH, "Audible GOAP Plan Voice Callouts", VisionConfig.EnableTacticalVoiceCallouts);
                        DrawToggleButton(colLeftX, ref yL, colW, rowH, "Radioman-Gated Team Intel Net", VisionConfig.EnableRadiomanIntelNet);
                        DrawToggleButton(colLeftX, ref yL, colW, rowH, "Objective Garrison, Pivot & Counterattack", VisionConfig.EnableMacroObjectiveDirector);
                        DrawToggleButton(colLeftX, ref yL, colW, rowH, "Dynamic Operations Ticket Quartermaster", VisionConfig.EnableConquestEconomyQuartermaster);
                        DrawToggleButton(colLeftX, ref yL, colW, rowH, "Proactive Static Gun & Halftrack Staffing", VisionConfig.EnableProactiveEmplacementStaffing);
                        DrawToggleButton(colLeftX, ref yL, colW, rowH, "Combined-Arms Known Target Priority", VisionConfig.EnableCombinedArmsPrioritySupport);

                        yL += 8f;
                        DrawSectionHeaderWithBundle(colLeftX, ref yL, colW, "Emergent Squad & Battlefield Actions",
                            VisionConfig.EnableTacticalRetreatsAndRegroup,
                            VisionConfig.EnableBuddyResupply,
                            VisionConfig.EnableEmergentWearableScavenge,
                            VisionConfig.EnableBuddyCasualtyDragging,
                            VisionConfig.EnableShellShockSurrender,
                            VisionConfig.EnableComradeLiberation,
                            VisionConfig.EnableSpecialMunitionsAndSappers);
                        DrawToggleButton(colLeftX, ref yL, colW, rowH, "3-Tier Tactical Retreats & Regroup", VisionConfig.EnableTacticalRetreatsAndRegroup);
                        DrawToggleButton(colLeftX, ref yL, colW, rowH, "Buddy Resupply (Medic Bandage & Ammo Share)", VisionConfig.EnableBuddyResupply);
                        DrawToggleButton(colLeftX, ref yL, colW, rowH, "Field Radio Inheritance & Helmet Gifting", VisionConfig.EnableEmergentWearableScavenge);
                        DrawToggleButton(colLeftX, ref yL, colW, rowH, "Buddy Casualty Dragging to Cover / Medic", VisionConfig.EnableBuddyCasualtyDragging);
                        DrawToggleButton(colLeftX, ref yL, colW, rowH, "Shell-Shocked & Skittish Surrender", VisionConfig.EnableShellShockSurrender);
                        DrawToggleButton(colLeftX, ref yL, colW, rowH, "Surrendered Comrade Liberation", VisionConfig.EnableComradeLiberation);
                        DrawToggleButton(colLeftX, ref yL, colW, rowH, "Special Munitions & Sappers (AT/AP Mines, Satchels & Tripods)", VisionConfig.EnableSpecialMunitionsAndSappers);

                        DrawSectionHeaderWithBundle(colRightX, ref yR, colW, "Infantry Combat, Cover & CQB",
                            VisionConfig.EnableBoundingOverwatch,
                            VisionConfig.EnableSelfPreservationStance,
                            VisionConfig.EnableSmartWallSideCover,
                            VisionConfig.BandageOnlyAfterFindingCover,
                            VisionConfig.EnableAutoPeekOverCover,
                            VisionConfig.EnableGunnerBipodWallMounting,
                            VisionConfig.EnablePointBlankMelee,
                            VisionConfig.EnableStrategicDoorDiscipline,
                            VisionConfig.EnableBridgeCrossingDiscipline,
                            VisionConfig.EnableWallVaulting,
                            VisionConfig.EnableAntiStacking,
                            VisionConfig.EnableSmartApcDismount,
                            VisionConfig.EnableTankStandoffAndReverse);
                        DrawToggleButton(colRightX, ref yR, colW, rowH, "Attacker Bounding Overwatch", VisionConfig.EnableBoundingOverwatch);
                        DrawToggleButton(colRightX, ref yR, colW, rowH, "Self-Preservation Stances Under Fire", VisionConfig.EnableSelfPreservationStance);
                        DrawToggleButton(colRightX, ref yR, colW, rowH, "Stand BEHIND Walls (Smart Wall Cover)", VisionConfig.EnableSmartWallSideCover);
                        DrawToggleButton(colRightX, ref yR, colW, rowH, "Seek Cover Before Bandaging", VisionConfig.BandageOnlyAfterFindingCover);
                        DrawToggleButton(colRightX, ref yR, colW, rowH, "Auto-Peek Over Low Cover", VisionConfig.EnableAutoPeekOverCover);
                        DrawToggleButton(colRightX, ref yR, colW, rowH, "Gunner Bipod Wall, Bridge & Ground Mounting", VisionConfig.EnableGunnerBipodWallMounting);
                        DrawToggleButton(colRightX, ref yR, colW, rowH, "Point-Blank Bayonet & Melee CQB", VisionConfig.EnablePointBlankMelee);
                        DrawToggleButton(colRightX, ref yR, colW, rowH, "Strategic Door & Shutter Discipline", VisionConfig.EnableStrategicDoorDiscipline);
                        DrawToggleButton(colRightX, ref yR, colW, rowH, "Bridge Cover, Wall Hug & Crossing Discipline", VisionConfig.EnableBridgeCrossingDiscipline);
                        DrawToggleButton(colRightX, ref yR, colW, rowH, "Wall & Fence Vaulting", VisionConfig.EnableWallVaulting);
                        DrawToggleButton(colRightX, ref yR, colW, rowH, "Anti-Stacking Personal Spacing", VisionConfig.EnableAntiStacking);
                        DrawToggleButton(colRightX, ref yR, colW, rowH, "Smart APC / Transport Dismount Under Fire", VisionConfig.EnableSmartApcDismount);
                        DrawToggleButton(colRightX, ref yR, colW, rowH, "Tank Standoff & Reverse Discipline", VisionConfig.EnableTankStandoffAndReverse);
                        break;

                    case 1: // 2. Vanilla Fixes
                        DrawSectionHeaderWithBundle(colLeftX, ref yL, colW, "Vanilla Infantry & Camera Bugfixes",
                            VisionConfig.EnableBloodSmokeWaterOcclusion,
                            VisionConfig.NeverShootStandingInOpen,
                            VisionConfig.EnforceStandingWhenOutOfCombat,
                            VisionConfig.EnableIndoorCeilingPronePrevention,
                            VisionConfig.EnableWallShootingPrevention,
                            VisionConfig.EnableFriendlyFireAvoidance,
                            VisionConfig.EnableAntiDogpileMedicTriage);
                        DrawToggleButton(colLeftX, ref yL, colW, rowH, "Smoke & Water Blood Spray Occlusion Fix", VisionConfig.EnableBloodSmokeWaterOcclusion);
                        DrawToggleButton(colLeftX, ref yL, colW, rowH, "Never Shoot Standing Upright in Open", VisionConfig.NeverShootStandingInOpen);
                        DrawToggleButton(colLeftX, ref yL, colW, rowH, "Enforce Standing Speed Out of Combat", VisionConfig.EnforceStandingWhenOutOfCombat);
                        DrawToggleButton(colLeftX, ref yL, colW, rowH, "Indoor Low-Ceiling Prone Prevention", VisionConfig.EnableIndoorCeilingPronePrevention);
                        DrawToggleButton(colLeftX, ref yL, colW, rowH, "Wall-Muzzle Shooting Prevention", VisionConfig.EnableWallShootingPrevention);
                        DrawToggleButton(colLeftX, ref yL, colW, rowH, "Friendly Fire Line-of-Fire Avoidance", VisionConfig.EnableFriendlyFireAvoidance);
                        DrawToggleButton(colLeftX, ref yL, colW, rowH, "Anti-Dogpile Medic Triage & Claim Release", VisionConfig.EnableAntiDogpileMedicTriage);

                        DrawSectionHeaderWithBundle(colRightX, ref yR, colW, "Vanilla Vehicle, Plane & MP Sync Fixes",
                            VisionConfig.EnableSmartVehicleAmmoSelection,
                            VisionConfig.EnableSmartTankCrewRepair,
                            VisionConfig.EnableTankRockUnstickAssist,
                            VisionConfig.EnableCoopAirborneSpawnFix,
                            VisionConfig.EnableMultiplayerBattleStatSyncFix,
                            VisionConfig.EnableFriendlyVehicleRightOfWay,
                            VisionConfig.PreventSmallArmsFireAtTanks,
                            VisionConfig.EnablePlaneAudioSmoothing);
                        DrawToggleButton(colRightX, ref yR, colW, rowH, "Smart Vehicle & Plane Ammo (HE vs AP)", VisionConfig.EnableSmartVehicleAmmoSelection);
                        DrawToggleButton(colRightX, ref yR, colW, rowH, "Sheltered Tank Crew Repair & Remount Fix", VisionConfig.EnableSmartTankCrewRepair);
                        DrawToggleButton(colRightX, ref yR, colW, rowH, "Tank Rock / Obstacle Unstick Assist", VisionConfig.EnableTankRockUnstickAssist);
                        DrawToggleButton(colRightX, ref yR, colW, rowH, "Co-op Airborne Plane Spawn Hot-Start Fix", VisionConfig.EnableCoopAirborneSpawnFix);
                        DrawToggleButton(colRightX, ref yR, colW, rowH, "MP Scoreboard, Casualties & Stat Sync Fix", VisionConfig.EnableMultiplayerBattleStatSyncFix);
                        DrawToggleButton(colRightX, ref yR, colW, rowH, "Friendly Right-of-Way & Client Roadkill Fix", VisionConfig.EnableFriendlyVehicleRightOfWay);
                        DrawToggleButton(colRightX, ref yR, colW, rowH, "Stop Small-Arms Fire at Tanks / Aim Crew", VisionConfig.PreventSmallArmsFireAtTanks);
                        DrawToggleButton(colRightX, ref yR, colW, rowH, "Plane Engine Audio Loop De-Throbber", VisionConfig.EnablePlaneAudioSmoothing);
                        break;

                    case 2: // 3. Vision & Realism
                        DrawSectionHeaderWithBundle(colLeftX, ref yL, colW, "Human Vision, Gaze & Memory",
                            VisionConfig.PreserveStanceStealthInCombat,
                            VisionConfig.EnableRealisticNightDarkness,
                            VisionConfig.EnforceHumanFOVWhenAlerted,
                            VisionConfig.EnableDynamicHumanReactionTime,
                            VisionConfig.EnableThreatSectorConcentration,
                            VisionConfig.EnableLocalContactCallouts,
                            VisionConfig.EnableLastKnownSuppression,
                            VisionConfig.EnableScopeVisionScaling,
                            VisionConfig.LimitAircraftInfantrySpotting);
                        DrawToggleButton(colLeftX, ref yL, colW, rowH, "Preserve Stance Stealth in Combat", VisionConfig.PreserveStanceStealthInCombat);
                        DrawToggleButton(colLeftX, ref yL, colW, rowH, "Night Darkness & Muzzle-Flare Contrast", VisionConfig.EnableRealisticNightDarkness);
                        DrawToggleButton(colLeftX, ref yL, colW, rowH, "Enforce Human Squint FOV Cones", VisionConfig.EnforceHumanFOVWhenAlerted);
                        DrawToggleButton(colLeftX, ref yL, colW, rowH, "Dynamic Human Reaction & Tank Intercom", VisionConfig.EnableDynamicHumanReactionTime);
                        DrawToggleButton(colLeftX, ref yL, colW, rowH, "Threat-Sector Gaze & Window Alignment", VisionConfig.EnableThreatSectorConcentration);
                        DrawToggleButton(colLeftX, ref yL, colW, rowH, "Local Contact Callouts (15m Radius)", VisionConfig.EnableLocalContactCallouts);
                        DrawToggleButton(colLeftX, ref yL, colW, rowH, "Last-Known Suppressive Fire & Grenades", VisionConfig.EnableLastKnownSuppression);
                        DrawToggleButton(colLeftX, ref yL, colW, rowH, "Scope & Turret Optic Vision Scaling", VisionConfig.EnableScopeVisionScaling);
                        DrawToggleButton(colLeftX, ref yL, colW, rowH, "Limit Aircraft Infantry Spotting", VisionConfig.LimitAircraftInfantrySpotting);

                        DrawSectionHeaderWithBundle(colRightX, ref yR, colW, "Ballistics, Armor & Environment Realism",
                            VisionConfig.EnableThinCoverBulletPenetration,
                            VisionConfig.EnableRealisticVehicleArmorAndTopAttack,
                            VisionConfig.EnableParachuteWindDrift);
                        DrawToggleButton(colRightX, ref yR, colW, rowH, "Thin-Cover Wood Bullet Penetration", VisionConfig.EnableThinCoverBulletPenetration);
                        DrawToggleButton(colRightX, ref yR, colW, rowH, "Realistic Vehicle Armor & Top-Attack AP", VisionConfig.EnableRealisticVehicleArmorAndTopAttack);
                        DrawToggleButton(colRightX, ref yR, colW, rowH, "Parachute Wind Drift & Slower Descent", VisionConfig.EnableParachuteWindDrift);
                        break;

                    case 3: // 4. Vets & Roles
                        DrawSectionHeaderWithBundle(colLeftX, ref yL, colW, "15-Minute Veteran Progression",
                            VisionConfig.EnableVeteranSystem,
                            VisionConfig.PromoteVeteranRanks,
                            VisionConfig.ShowVeteran3DIcons,
                            VisionConfig.ShowVeteranSquadRosterTags);
                        DrawToggleButton(colLeftX, ref yL, colW, rowH, "15-Minute Veteran Progression (Master)", VisionConfig.EnableVeteranSystem);
                        DrawToggleButton(colLeftX, ref yL, colW, rowH, "Field Rank Promotions & Uniform Decals", VisionConfig.PromoteVeteranRanks);
                        DrawToggleButton(colLeftX, ref yL, colW, rowH, "3D Overhead Veteran Chevron Icons", VisionConfig.ShowVeteran3DIcons);
                        DrawToggleButton(colLeftX, ref yL, colW, rowH, "Squad Roster Veteran Tags", VisionConfig.ShowVeteranSquadRosterTags);

                        DrawSectionHeaderWithBundle(colRightX, ref yR, colW, "30 Role Personalities & Doctrines",
                            VisionConfig.EnableRolePersonalities,
                            VisionConfig.EnableInfantryPersonalities,
                            VisionConfig.EnableVehicleAndPilotPersonalities,
                            VisionConfig.VeterancyCuresSkittishness,
                            VisionConfig.ShowPersonalityInSquadRoster,
                            VisionConfig.EnableFactionNetDoctrines);
                        DrawToggleButton(colRightX, ref yR, colW, rowH, "Enable AI Role Personalities (Master)", VisionConfig.EnableRolePersonalities);
                        DrawToggleButton(colRightX, ref yR, colW, rowH, "Infantry Role Personalities (21 Types)", VisionConfig.EnableInfantryPersonalities);
                        DrawToggleButton(colRightX, ref yR, colW, rowH, "Vehicle & Pilot Personalities (9 Types)", VisionConfig.EnableVehicleAndPilotPersonalities);
                        DrawToggleButton(colRightX, ref yR, colW, rowH, "Veterancy Cures Skittishness", VisionConfig.VeterancyCuresSkittishness);
                        DrawToggleButton(colRightX, ref yR, colW, rowH, "Show Personalities in Squad Roster", VisionConfig.ShowPersonalityInSquadRoster);
                        DrawToggleButton(colRightX, ref yR, colW, rowH, "20-Nation Historical Command Doctrines", VisionConfig.EnableFactionNetDoctrines);
                        break;

                    case 4: // 5. Optimizations & QoL
                        DrawSectionHeaderWithBundle(colLeftX, ref yL, colW, "CPU & Network Frame-Time Optimizations",
                            VisionConfig.EnableAdaptiveAiTickScaling);
                        DrawToggleButton(colLeftX, ref yL, colW, rowH, "Adaptive AI Brain Tick Scaling (SP & Co-op)", VisionConfig.EnableAdaptiveAiTickScaling);

                        yL += 10f;
                        DrawSectionHeaderWithBundle(colLeftX, ref yL, colW, "Developer Diagnostics (Default OFF)",
                            VisionConfig.DebugLogging,
                            VisionConfig.EnableHitchFlightRecorder,
                            VisionConfig.EnableBotActionTrace);
                        DrawToggleButton(colLeftX, ref yL, colW, rowH, "Diagnostic Debug Logs (BepInEx Console)", VisionConfig.DebugLogging);
                        DrawToggleButton(colLeftX, ref yL, colW, rowH, "Hitch Flight Recorder (LogOutput)", VisionConfig.EnableHitchFlightRecorder);
                        DrawToggleButton(colLeftX, ref yL, colW, rowH, "Per-Bot Action Trace (BotActionTrace.jsonl)", VisionConfig.EnableBotActionTrace);

                        yL += 10f;
                        DrawSectionHeaderWithBundle(colLeftX, ref yL, colW, "Multiplayer Gameplay Enhancements (Default OFF)",
                            VisionConfig.EnableObjectiveTeamplayScoring);
                        string ptfoLabel = "Objective & Teamplay Scoring (PTFO, Host-Synced)";
                        if (VeteranAndTacticsPatches.IsPtfoControlledByRemoteHost(out bool ptfoHostOn, out bool ptfoHostKnown))
                        {
                            ptfoLabel = ptfoHostKnown
                                ? (ptfoHostOn ? "Objective & Teamplay Scoring (PTFO) [Host: ON]" : "Objective & Teamplay Scoring (PTFO) [Host: OFF]")
                                : "Objective & Teamplay Scoring (PTFO) [Host: Vanilla/OFF]";
                        }
                        DrawToggleButton(colLeftX, ref yL, colW, rowH, ptfoLabel, VisionConfig.EnableObjectiveTeamplayScoring);

                        yL += 10f;
                        DrawSectionHeaderWithBundle(colLeftX, ref yL, colW, "Squad Roster, Map & Overhead Names",
                            VisionConfig.EnableAlwaysShowSquadNames,
                            VisionConfig.EnableInWorldSquadMemberNames,
                            VisionConfig.EnableMapSquadLabels);
                        DrawToggleButton(colLeftX, ref yL, colW, rowH, "Always Show Squad Roster Names (Bottom-Left)", VisionConfig.EnableAlwaysShowSquadNames);
                        DrawToggleButton(colLeftX, ref yL, colW, rowH, "Always Show Own Squad 3D Overhead Names", VisionConfig.EnableInWorldSquadMemberNames);
                        DrawToggleButton(colLeftX, ref yL, colW, rowH, "Tactical Map Friendly Squad Letters (A, B, C...)", VisionConfig.EnableMapSquadLabels);

                        DrawSectionHeaderWithBundle(colRightX, ref yR, colW, "Player QoL & Obituary",
                            VisionConfig.EnableQuickTapSpot,
                            VisionConfig.EnableDeathScreenAttribution,
                            VisionConfig.EnableDogTagArchive,
                            VisionConfig.EnableObjectiveBarFactionNames,
                            VisionConfig.EnableSpectatorHudRetention,
                            VisionConfig.EnableSpectatorBotInspector,
                            VisionConfig.EnableExtendedRoomNames,
                            VisionConfig.EnableLobbyJoinMotd,
                            VisionConfig.EnableTacticalReloadAndBipodCancel);
                        DrawToggleButton(colRightX, ref yR, colW, rowH, "Master Mod Toggle (F8)", VisionConfig.EnableMod);
                        DrawToggleButton(colRightX, ref yR, colW, rowH, "Short-Press Orders Key to Spot", VisionConfig.EnableQuickTapSpot);
                        DrawToggleButton(colRightX, ref yR, colW, rowH, "Death Screen Kill Attribution + Age", VisionConfig.EnableDeathScreenAttribution);
                        DrawToggleButton(colRightX, ref yR, colW, rowH, "Dogtag Collection Archive (F7)", VisionConfig.EnableDogTagArchive);
                        DrawToggleButton(colRightX, ref yR, colW, rowH, "Objective Bar Faction + Side Names", VisionConfig.EnableObjectiveBarFactionNames);
                        DrawToggleButton(colRightX, ref yR, colW, rowH, "Spectator Tactical HUD + 3D Markers (F Cycles)", VisionConfig.EnableSpectatorHudRetention);
                        DrawToggleButton(colRightX, ref yR, colW, rowH, "Spectator Hovered-Bot GOAP Inspector (F Cycles)", VisionConfig.EnableSpectatorBotInspector);
                        DrawToggleButton(colRightX, ref yR, colW, rowH, "64-Char Multiplayer Room Names", VisionConfig.EnableExtendedRoomNames);
                        DrawToggleButton(colRightX, ref yR, colW, rowH, "Host Lobby Welcome MOTD (Joiners)", VisionConfig.EnableLobbyJoinMotd);
                        DrawToggleButton(colRightX, ref yR, colW, rowH, "Tactical Reload & Bipod Animation Cancel", VisionConfig.EnableTacticalReloadAndBipodCancel);

                        yR += 6f;
                        float curMult = VisionConfig.UiScaleMultiplier != null
                            ? Mathf.Clamp(VisionConfig.UiScaleMultiplier.Value, 0.75f, 1.50f)
                            : 1.0f;
                        float effScale = GetEffectiveImGuiScale(out _, out _);
                        int curScalePct = Mathf.RoundToInt(curMult * 100f);
                        int effScaleX100 = Mathf.RoundToInt(effScale * 100f);
                        if (curScalePct != _cachedUiScalePct || effScaleX100 != _cachedEffScaleX100)
                        {
                            _cachedUiScalePct = curScalePct;
                            _cachedEffScaleX100 = effScaleX100;
                            _cachedUiScaleHeaderText = $"=== 4K / High-DPI UI Scale: {curScalePct}% ({effScale:F2}x) ===";
                        }
                        GUI.color = new Color(0.55f, 0.82f, 1.0f, 1.0f);
                        GUI.Label(new Rect(colRightX, yR, colW, 20f), _cachedUiScaleHeaderText);
                        yR += 21f;

                        float btnW = (colW - 12f) / 3f;
                        GUI.color = new Color(0.82f, 0.90f, 1.0f, 1.0f);
                        if (GUI.Button(new Rect(colRightX, yR, btnW, 23f), "- 10% Smaller") && VisionConfig.UiScaleMultiplier != null)
                        {
                            VisionConfig.UiScaleMultiplier.Value = Mathf.Clamp(Mathf.Round((curMult - 0.10f) * 20f) / 20f, 0.75f, 1.50f);
                            Plugin.Instance.Config.Save();
                        }
                        GUI.color = new Color(1.0f, 0.88f, 0.42f, 1.0f);
                        if (GUI.Button(new Rect(colRightX + btnW + 6f, yR, btnW, 23f), "Auto (100%)") && VisionConfig.UiScaleMultiplier != null)
                        {
                            VisionConfig.UiScaleMultiplier.Value = 1.0f;
                            Plugin.Instance.Config.Save();
                        }
                        GUI.color = new Color(0.82f, 0.90f, 1.0f, 1.0f);
                        if (GUI.Button(new Rect(colRightX + (btnW + 6f) * 2f, yR, btnW, 23f), "+ 10% Larger") && VisionConfig.UiScaleMultiplier != null)
                        {
                            VisionConfig.UiScaleMultiplier.Value = Mathf.Clamp(Mathf.Round((curMult + 0.10f) * 20f) / 20f, 0.75f, 1.50f);
                            Plugin.Instance.Config.Save();
                        }
                        yR += 25f;
                        break;
                }

                // Bottom Action Buttons
                float bottomY = panelY + panelH - 38f;
                GUI.color = new Color(0.75f, 0.90f, 1.0f, 1.0f);
                if (GUI.Button(new Rect(panelX + 20f, bottomY, 240f, 26f), "Reload .cfg from Disk (F9)"))
                {
                    Plugin.Instance.Config.Reload();
                    HitchProfiler.RefreshConfigState();
                    BotActionTrace.RefreshConfigState();
                    VeteranAndTacticsPatches.SyncPtfoRoomPropertyIfMaster();
                    VeteranAndTacticsPatches.RefreshBottomLeftSquadRosterVisuals();
                    ShowToast($"[Battle Hardened v{Plugin.PluginVersion}] Reloaded configuration from disk!");
                }

                GUI.color = new Color(0.85f, 1.0f, 0.85f, 1.0f);
                if (GUI.Button(new Rect(panelX + 290f, bottomY, 240f, 26f), "Save to .cfg File"))
                {
                    Plugin.Instance.Config.Save();
                    ShowToast($"[Battle Hardened v{Plugin.PluginVersion}] Saved configuration to com.antigravity.easyred2.humanai.cfg!");
                }

                GUI.color = new Color(1.0f, 0.80f, 0.80f, 1.0f);
                if (GUI.Button(new Rect(panelX + 560f, bottomY, 240f, 26f), "Close Menu (F6)"))
                {
                    _showConfigMenu = false;
                    _lastModOverlayClosedFrame = Time.frameCount;
                }

                GUI.color = Color.white;
            }

            private static void DrawSectionHeaderWithBundle(float x, ref float y, float width, string title, params ConfigEntry<bool>[] bundleEntries)
            {
                if (!_sectionHeaderLabelCache.TryGetValue(title, out string headerLabel))
                {
                    headerLabel = $"=== {title} ===";
                    _sectionHeaderLabelCache[title] = headerLabel;
                }

                GUI.color = new Color(0.55f, 0.82f, 1.0f, 1.0f);
                GUI.Label(new Rect(x, y, width - 96f, 20f), headerLabel);

                GUI.color = new Color(0.45f, 0.95f, 0.52f, 0.92f);
                if (GUI.Button(new Rect(x + width - 92f, y - 1f, 44f, 19f), "ALL"))
                {
                    VisionConfig.SetBundleEntries(true, bundleEntries);
                    HitchProfiler.RefreshConfigState();
                    BotActionTrace.RefreshConfigState();
                    VeteranAndTacticsPatches.SyncPtfoRoomPropertyIfMaster();
                    VeteranAndTacticsPatches.RefreshBottomLeftSquadRosterVisuals();
                    Plugin.Instance.Config.Save();
                }

                GUI.color = new Color(0.95f, 0.45f, 0.45f, 0.92f);
                if (GUI.Button(new Rect(x + width - 45f, y - 1f, 45f, 19f), "OFF"))
                {
                    VisionConfig.SetBundleEntries(false, bundleEntries);
                    HitchProfiler.RefreshConfigState();
                    BotActionTrace.RefreshConfigState();
                    VeteranAndTacticsPatches.SyncPtfoRoomPropertyIfMaster();
                    VeteranAndTacticsPatches.RefreshBottomLeftSquadRosterVisuals();
                    Plugin.Instance.Config.Save();
                }

                y += 20f;
            }

            private static void DrawToggleButton(float x, ref float y, float width, float height, string label, ConfigEntry<bool> entry)
            {
                if (entry == null) return;
                bool val = entry.Value;
                GUI.color = val
                    ? new Color(0.45f, 0.95f, 0.52f, 1.0f)
                    : new Color(0.95f, 0.45f, 0.45f, 1.0f);

                string fullLabel;
                if (val)
                {
                    if (!_toggleOnLabelCache.TryGetValue(label, out fullLabel))
                    {
                        if (_toggleOnLabelCache.Count > 128) _toggleOnLabelCache.Clear();
                        fullLabel = "[ON]  " + label;
                        _toggleOnLabelCache[label] = fullLabel;
                    }
                }
                else
                {
                    if (!_toggleOffLabelCache.TryGetValue(label, out fullLabel))
                    {
                        if (_toggleOffLabelCache.Count > 128) _toggleOffLabelCache.Clear();
                        fullLabel = "[OFF] " + label;
                        _toggleOffLabelCache[label] = fullLabel;
                    }
                }

                if (GUI.Button(new Rect(x, y, width, height - 2f), fullLabel))
                {
                    entry.Value = !val;
                    Plugin.Instance.Config.Save();
                    if (entry == VisionConfig.EnableExtendedRoomNames && entry.Value)
                    {
                        VeteranAndTacticsPatches.ApplyExtendedRoomNameLimits();
                    }
                    else if (entry == VisionConfig.EnableHitchFlightRecorder)
                    {
                        HitchProfiler.RefreshConfigState();
                    }
                    else if (entry == VisionConfig.EnableBotActionTrace)
                    {
                        BotActionTrace.RefreshConfigState();
                    }
                    else if (entry == VisionConfig.EnableObjectiveTeamplayScoring ||
                             entry == VisionConfig.EnableMultiplayerBattleStatSyncFix ||
                             entry == VisionConfig.EnableMod)
                    {
                        VeteranAndTacticsPatches.SyncPtfoRoomPropertyIfMaster();
                    }
                    VeteranAndTacticsPatches.RefreshBottomLeftSquadRosterVisuals();
                }
                y += height;
            }
        }
    }

    public static partial class VeteranAndTacticsPatches
    {
        private static float _nextStateCleanupTime;

        // Procedural 32x32 chevron textures for Tier 1 (^), Tier 2 (^^), Tier 3 (^^^ ★)
        private static Texture2D _texVet1;
        private static Texture2D _texVet2;
        private static Texture2D _texVet3;

        private static void EnsureBadgeTextures()
        {
            if (_texVet1 != null && _texVet2 != null && _texVet3 != null)
                return;

            _texVet1 = CreateChevronTexture(1, new Color(0.90f, 0.72f, 0.38f, 0.95f), false);
            _texVet2 = CreateChevronTexture(2, new Color(0.88f, 0.93f, 1.00f, 0.95f), false);
            _texVet3 = CreateChevronTexture(3, new Color(1.00f, 0.84f, 0.15f, 1.00f), true);
        }

        private static Texture2D CreateChevronTexture(int chevrons, Color mainColor, bool drawStar)
        {
            const int size = 32;
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.hideFlags = HideFlags.HideAndDontSave;
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;

            Color clear = new Color(0f, 0f, 0f, 0f);
            Color outline = new Color(0.08f, 0.08f, 0.08f, 0.88f);

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    tex.SetPixel(x, y, clear);
                }
            }

            for (int c = 0; c < chevrons; c++)
            {
                int apexY = 26 - (c * 7);
                for (int dx = -10; dx <= 10; dx++)
                {
                    int x = 16 + dx;
                    int yCenter = apexY - (Mathf.Abs(dx) * 6) / 10;

                    for (int dy = -2; dy <= 2; dy++)
                    {
                        int y = yCenter + dy;
                        if (x >= 1 && x < size - 1 && y >= 1 && y < size - 1)
                        {
                            if (Mathf.Abs(dy) == 2 || Mathf.Abs(dx) == 10)
                            {
                                if (tex.GetPixel(x, y).a < 0.5f)
                                    tex.SetPixel(x, y, outline);
                            }
                            else
                            {
                                tex.SetPixel(x, y, mainColor);
                            }
                        }
                    }
                }
            }

            if (drawStar)
            {
                int cx = 16;
                int cy = 5;
                for (int dy = -3; dy <= 3; dy++)
                {
                    for (int dx = -3; dx <= 3; dx++)
                    {
                        int manhattan = Mathf.Abs(dx) + Mathf.Abs(dy);
                        if (manhattan <= 2)
                            tex.SetPixel(cx + dx, cy + dy, mainColor);
                        else if (manhattan == 3)
                            tex.SetPixel(cx + dx, cy + dy, outline);
                    }
                }
            }

            tex.Apply();
            return tex;
        }

        private static readonly StringBuilder _specSb = new StringBuilder(384);
        private static Soldier _specTargetSoldier;
        private static IntPtr _specTargetPtr;
        private static IntPtr _lastFormattedSpecPtr;
        private static float _specTargetHoldUntil;
        private static float _nextSpecFormatTime;
        private static bool _specHasValidTarget;
        private static bool _specIsFreeCamera;
        private static bool _specTargetInDistress;
        private static string _specHeaderLine = "";
        private static string _specGoapLine = "";
        private static string _specVitalsLine = "";

        internal static void DrawSpectatorBotInspectorGui(float virtualW, float virtualH, float toastBottomY)
        {
            if (!VisionConfig.EnableMod.Value ||
                !VisionConfig.EnableSpectatorBotInspector.Value ||
                !_specIsFreeCamera ||
                ActiveSpectatorHudMode != SpectatorTacticalHudMode.FullTactical)
            {
                return;
            }

            // Subtle 4x4 center reticle pip in Free Camera so hovering a specific bot is effortless
            float cx = virtualW * 0.5f;
            float cy = virtualH * 0.5f;
            GUI.color = _specHasValidTarget
                ? new Color(1.0f, 0.88f, 0.32f, 0.80f)
                : new Color(0.90f, 0.94f, 1.0f, 0.42f);
            GUI.Box(new Rect(cx - 2f, cy - 2f, 4f, 4f), "");

            if (!_specHasValidTarget || string.IsNullOrEmpty(_specHeaderLine))
            {
                GUI.color = Color.white;
                return;
            }

            // Anchor at Top-Center right beneath the Objective Bar so it NEVER overlaps bottom-center "Free Camera"!
            float cardW = Mathf.Min(virtualW - 48f, 880f);
            float cardH = 74f;
            float cardX = (virtualW - cardW) * 0.5f;
            float cardY = toastBottomY > 0f ? Mathf.Max(68f, toastBottomY + 8f) : 68f;

            GUI.color = new Color(0.04f, 0.05f, 0.07f, 0.90f);
            GUI.Box(new Rect(cardX, cardY, cardW, cardH), "");
            GUI.Box(new Rect(cardX + 2f, cardY + 2f, cardW - 4f, cardH - 4f), "");

            GUIStyle lbl = GUI.skin != null ? GUI.skin.label : null;
            int oldSize = 0;
            FontStyle oldStyle = FontStyle.Normal;
            TextAnchor oldAlign = TextAnchor.UpperLeft;
            bool oldWrap = false;
            if (lbl != null)
            {
                oldSize = lbl.fontSize;
                oldStyle = lbl.fontStyle;
                oldAlign = lbl.alignment;
                oldWrap = lbl.wordWrap;
                lbl.alignment = TextAnchor.MiddleLeft;
                lbl.wordWrap = false;
            }

            if (lbl != null)
            {
                lbl.fontSize = 14;
                lbl.fontStyle = FontStyle.Bold;
            }
            GUI.color = _specTargetInDistress
                ? new Color(1.0f, 0.62f, 0.42f, 1.0f)
                : new Color(1.0f, 0.88f, 0.38f, 1.0f);
            GUI.Label(new Rect(cardX + 14f, cardY + 5f, cardW - 28f, 21f), _specHeaderLine);

            if (lbl != null)
            {
                lbl.fontSize = 13;
                lbl.fontStyle = FontStyle.Normal;
            }
            GUI.color = new Color(0.62f, 0.92f, 1.0f, 1.0f);
            GUI.Label(new Rect(cardX + 14f, cardY + 27f, cardW - 28f, 20f), _specGoapLine);

            GUI.color = new Color(0.88f, 0.91f, 0.95f, 0.98f);
            GUI.Label(new Rect(cardX + 14f, cardY + 48f, cardW - 28f, 20f), _specVitalsLine);

            if (lbl != null)
            {
                lbl.fontSize = oldSize;
                lbl.fontStyle = oldStyle;
                lbl.alignment = oldAlign;
                lbl.wordWrap = oldWrap;
            }
            GUI.color = Color.white;
        }

        private static void UpdateSpectatorMarkersAndInspector(bool isFreeCamera, bool canShowOverlays)
        {
            bool allowFullTacticalOverlays = canShowOverlays &&
                                             ActiveSpectatorHudMode == SpectatorTacticalHudMode.FullTactical;
            _specIsFreeCamera = isFreeCamera && allowFullTacticalOverlays;
            if (!allowFullTacticalOverlays)
            {
                _specHasValidTarget = false;
                return;
            }

            bool wantVetIcons = VisionConfig.EnableVeteranSystem.Value &&
                                VisionConfig.ShowVeteran3DIcons.Value &&
                                (SavableData.Settings == null ||
                                 SavableData.Settings.system == null ||
                                 !SavableData.Settings.system.disable3DMarkers);
            bool wantInspector = VisionConfig.EnableSpectatorBotInspector.Value && isFreeCamera;
            if (!wantVetIcons && !wantInspector)
            {
                _specHasValidTarget = false;
                return;
            }

            Camera cam = ResourcesManager.mainCamera;
            if (cam == null)
                cam = Camera.main;
            if (cam == null || cam.transform == null)
            {
                _specHasValidTarget = false;
                return;
            }

            AiCore.EnsureWorldSnapshot();
            var wSoldiers = AiCore.WorldSoldiers;
            int wCount = AiCore.WorldSoldierCount;

            Vector3 camPos = cam.transform.position;
            Vector2 screenCenter = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
            float maxHoverPx = Mathf.Max(28f, Screen.height * 0.13f);
            float stickyHoverPx = maxHoverPx * 1.35f;

            Soldier bestSoldier = null;
            float bestScorePx = float.MaxValue;
            float bestRawScreenDist = maxHoverPx;

            for (int i = 0; i < wCount; i++)
            {
                ref AiCore.SoldierSnapshot sSnap = ref wSoldiers[i];
                if (sSnap.Ptr == IntPtr.Zero || sSnap.Ref == null)
                    continue;

                float worldDistSq = (sSnap.Pos - camPos).sqrMagnitude;
                if (worldDistSq > 145f * 145f)
                    continue;

                Vector3 chestPos = sSnap.Pos + new Vector3(0f, 1.35f, 0f);
                Vector3 sp = cam.WorldToScreenPoint(chestPos);
                if (sp.z <= 0.4f || sp.z > 145f)
                    continue;

                float dPx = Vector2.Distance(new Vector2(sp.x, sp.y), screenCenter);
                bool isCurrentTarget = _specTargetPtr != IntPtr.Zero && sSnap.Ptr == _specTargetPtr;
                float limitPx = isCurrentTarget ? stickyHoverPx : maxHoverPx;
                if (dPx > limitPx)
                    continue;

                float scorePx = (isCurrentTarget ? dPx * 0.76f : dPx) + sp.z * 0.18f;
                if (scorePx < bestScorePx)
                {
                    bestScorePx = scorePx;
                    bestRawScreenDist = dPx;
                    bestSoldier = sSnap.Ref;
                }
            }

            if (wantVetIcons && bestSoldier != null && bestSoldier.IsAlive)
            {
                int tier = GetVeteranTier(bestSoldier);
                if (tier > 0)
                {
                    Vector3 uiPos = bestSoldier.UINamePos();
                    if (!Marker3DGUI.IsOccluded(bestSoldier, 2, uiPos))
                    {
                        EnsureBadgeTextures();
                        Texture2D tex = tier == 1 ? _texVet1 : (tier == 2 ? _texVet2 : _texVet3);
                        if (tex != null)
                        {
                            float alpha = Mathf.Clamp01((1.0f - (bestRawScreenDist / stickyHoverPx)) * 1.6f) * 0.92f;
                            Marker3DGUI.Draw(bestSoldier, 77, tex, uiPos + new Vector3(0f, 0.26f, 0f), tier == 3 ? 18f : 15f, alpha);
                        }
                    }
                }
            }

            if (!wantInspector)
            {
                _specHasValidTarget = false;
                return;
            }

            float unscaledNow = Time.unscaledTime;
            if (bestSoldier != null && bestSoldier.IsAlive)
            {
                _specTargetSoldier = bestSoldier;
                _specTargetPtr = bestSoldier.Pointer;
                _specTargetHoldUntil = unscaledNow + 0.55f;
            }
            else if (_specTargetSoldier != null &&
                     _specTargetSoldier.Pointer != IntPtr.Zero &&
                     unscaledNow < _specTargetHoldUntil &&
                     !_specTargetSoldier.IsDead &&
                     _specTargetSoldier.IsAlive)
            {
                bestSoldier = _specTargetSoldier;
            }
            else
            {
                _specHasValidTarget = false;
                _specTargetSoldier = null;
                _specTargetPtr = IntPtr.Zero;
                _lastFormattedSpecPtr = IntPtr.Zero;
                return;
            }

            _specHasValidTarget = true;
            if (_lastFormattedSpecPtr == bestSoldier.Pointer &&
                unscaledNow < _nextSpecFormatTime &&
                _nextSpecFormatTime - unscaledNow <= 2.0f)
            {
                return;
            }

            _lastFormattedSpecPtr = bestSoldier.Pointer;
            _nextSpecFormatTime = unscaledNow + 0.12f;
            FormatSpectatorBotCardStrings(bestSoldier);
        }

        private static void FormatSpectatorBotCardStrings(Soldier s)
        {
            AiCore.SoldierFastHandles h = AiCore.GetOrCreateSoldierHandles(s);
            int botId = h != null ? h.DiagId : 0;
            Squad sq = s.joinedSquad;
            int sqId = sq != null ? AiCore.GetSquadDiagId(sq.Pointer) : 0;

            string rawName = s.name_surname ?? "Soldier";
            int bIdx = rawName.IndexOf(" [", StringComparison.Ordinal);
            if (bIdx > 0)
                rawName = rawName.Substring(0, bIdx);

            string fac = AiCore.GetCachedFaction(s);
            string displayFac = AiCore.FormatFactionWithAlliance(fac);
            SoldierPersonality pers = GetPersonality(s);
            string roleStr = GetPersonalityShortLabel(pers);
            int vetTier = GetVeteranTier(s);
            int vetPct = Mathf.RoundToInt(GetVeterancy01(s) * 100f);
            string vetLabel = vetTier == 0 ? "Recruit" : (vetTier == 1 ? "Vet I" : (vetTier == 2 ? "Vet II" : "Vet III \u2605"));

            // Line 1: Header (Bot# matching BotActionTrace.jsonl, Squad#, Name, Faction, Personality, Veterancy)
            _specSb.Length = 0;
            _specSb.Append("[Bot #").Append(botId);
            if (sqId > 0)
                _specSb.Append(" | Sq#").Append(sqId);
            _specSb.Append("]  ").Append(rawName)
                   .Append("  \u2022  ").Append(string.IsNullOrEmpty(displayFac) ? "Unknown" : displayFac)
                   .Append("  \u2022  ").Append(roleStr)
                   .Append("  \u2022  ").Append(vetLabel).Append(" (").Append(vetPct).Append("%)");
            if (s.IsPlayer())
                _specSb.Append("  [PLAYER]");
            else if (s.IsSquadLeader())
                _specSb.Append("  [SL]");
            else if (s.IsRadioman())
                _specSb.Append("  [RADIO]");
            _specHeaderLine = _specSb.ToString();

            // Line 2: 3-Tier GOAP Plan / Vehicle State
            _specSb.Length = 0;
            if (s.IsOnVehicle())
            {
                Vehicle veh = s.GetCurrentVehicle();
                string vName = (veh != null && !string.IsNullOrEmpty(veh.name)) ? veh.name : "Vehicle";
                int cloneIdx = vName.IndexOf("(Clone)", StringComparison.Ordinal);
                if (cloneIdx > 0)
                    vName = vName.Substring(0, cloneIdx);
                _specSb.Append("Mounted: ").Append(vName);
                if (sq != null)
                    _specSb.Append("  |  Squad Order: ").Append(sq.order.ToString());
                if (veh != null && veh.IsDisabled())
                    _specSb.Append("  [VEHICLE DISABLED]");
            }
            else if (s.HasSurrended())
            {
                _specSb.Append("State: SURRENDERED (Awaiting Comrade Liberation)");
            }
            else
            {
                bool hasPlan = TryGetBotGoapSummary(
                    s,
                    out SquadNetDirective netDir,
                    out GoapStateBits goalBit,
                    out GoapActionId step0,
                    out GoapActionId step1,
                    out byte stepIdx,
                    out bool isEvacCrew);

                _specSb.Append("Net: ").Append(GetNetDirectiveShortName(netDir));
                if (sq != null)
                    _specSb.Append("  |  Squad: ").Append(sq.order.ToString());

                _specSb.Append("  |  GOAP: ");
                if (!hasPlan || step0 == GoapActionId.None)
                {
                    _specSb.Append(s.IsCharging() ? "SquadChargeAssault" : "NativeSquadCombat");
                }
                else
                {
                    _specSb.Append(GetGoapGoalShortName(goalBit)).Append(" -> ");
                    if (stepIdx == 0)
                        _specSb.Append('[').Append(GetGoapActionShortName(step0)).Append(']');
                    else
                        _specSb.Append(GetGoapActionShortName(step0));

                    if (step1 != GoapActionId.None)
                    {
                        _specSb.Append(" -> ");
                        if (stepIdx > 0)
                            _specSb.Append('[').Append(GetGoapActionShortName(step1)).Append(']');
                        else
                            _specSb.Append(GetGoapActionShortName(step1));
                    }
                }

                if (isEvacCrew)
                    _specSb.Append("  [EVAC CREW]");
                else if (s.IsCharging())
                    _specSb.Append("  [CHARGE!]");
                if (h != null &&
                    h.MoveOverride.Kind > AiCore.BotMoveOverrideKind.GoapAction &&
                    Time.time < h.MoveOverride.ExpireTime)
                {
                    _specSb.Append("  [OVR:").Append(AiCore.GetMoveOverrideShortLabel(h.MoveOverride.Kind)).Append(']');
                }
            }
            _specGoapLine = _specSb.ToString();

            // Line 3: Live Vitals, Cover, Suppression, Bleed/Bandage, Weapon & Threat Distances
            float supp = s.GetSuppressionValue();
            bool bleeding = s.isBleeding || s.GetBleedValue() > 0.01f;
            _specTargetInDistress = bleeding || supp >= 35f;
            bool hasBandage = s.inventory != null && s.inventory.FindItemOfType<VirtualBandages>() != null;

            GenericGun heldGun = s.GetHeldGun();
            string wepName = "Unarmed";
            int ammoCount = -1;
            if (heldGun != null)
            {
                wepName = !string.IsNullOrEmpty(heldGun.name) ? heldGun.name : "Firearm";
                int cIdx = wepName.IndexOf("(Clone)", StringComparison.Ordinal);
                if (cIdx > 0)
                    wepName = wepName.Substring(0, cIdx);
                if (wepName.Length > 18)
                    wepName = wepName.Substring(0, 18);
                ammoCount = heldGun.GetCurrentAmmoCount();
            }

            SoldierAI ai = h != null && h.Ai != null ? h.Ai : s.aiController;
            float enemyDist = (ai != null && ai.visibleTarget != null) ? ai.distFromTarget : -1f;
            Vector3 myPos = (h != null && h.Tr != null) ? h.Tr.position : Vector3.zero;
            if (!VisionPatches.TryGetNearestEnemyGunfire(myPos, fac, 35.0f, 3.0f, out float gfDist))
                gfDist = -1f;

            _specSb.Length = 0;
            _specSb.Append("Pose: ").Append(s.m_pose.ToString())
                   .Append("  |  Cover: ").Append(s.IsOnCover() ? "YES" : "NO")
                   .Append("  |  Supp: ").Append(Mathf.RoundToInt(supp))
                   .Append("  |  Bleed: ").Append(bleeding ? "YES" : "NO")
                   .Append(hasBandage ? " (Bnd:Y)" : " (Bnd:N)")
                   .Append("  |  Wep: ").Append(wepName);
            if (ammoCount >= 0)
                _specSb.Append(" (").Append(ammoCount).Append(')');

            _specSb.Append("  |  Enemy: ");
            if (enemyDist >= 0f)
                _specSb.Append(Mathf.RoundToInt(enemyDist)).Append('m');
            else
                _specSb.Append("--");

            _specSb.Append("  |  Gunfire: ");
            if (gfDist >= 0f)
                _specSb.Append(Mathf.RoundToInt(gfDist)).Append('m');
            else
                _specSb.Append("--");

            _specVitalsLine = _specSb.ToString();
        }

        public static void RenderVeteran3DMarkersAndCleanup()
        {
            if (!VisionConfig.EnableMod.Value)
            {
                _specHasValidTarget = false;
                _specIsFreeCamera = false;
                return;
            }

            if (VisionConfig.EnableDeathScreenAttribution.Value)
                MaintainInstantDeathPanel();

            MaintainSpectatorHudVisibility();

            if ((Time.time > _nextStateCleanupTime || Time.time + 2.0f < _nextStateCleanupTime) && AiCore.TryClaimHeavyFrameWorkSlot())
            {
                _nextStateCleanupTime = Time.time + 2.0f;
                CleanupDictionaries(false);
            }

            if (IsSpectatorCameraModeActive(out bool isFreeCamera, out bool specGuiVisible))
            {
                UpdateSpectatorMarkersAndInspector(isFreeCamera, specGuiVisible);
                return;
            }

            _specHasValidTarget = false;
            _specIsFreeCamera = false;

            if (!VisionConfig.EnableVeteranSystem.Value || !VisionConfig.ShowVeteran3DIcons.Value)
                return;

            PlayerController pc = PlayerController.currentController;
            if (pc == null) return;
            Soldier player = pc.ControlledCharacter;
            if (player == null || !player.IsAlive)
                return;

            if (SavableData.Settings == null ||
                SavableData.Settings.system == null ||
                !SavableData.Settings.system.enableGUI ||
                SavableData.Settings.system.disable3DMarkers)
            {
                return;
            }

            if (pc.IsAiming && !pc.IsLimitedAiming)
                return;

            var cursorSoldiers = pc.cursorSoldiers;
            if (cursorSoldiers == null || cursorSoldiers.Count == 0)
                return;

            Camera cam = ResourcesManager.mainCamera;
            if (cam == null) return;

            Vector2 screenCenter = pc.screenCenter;
            float maxHoverRadius = Screen.height * 0.20f;
            if (maxHoverRadius <= 1f) return;

            Soldier bestHoveredFriendly = null;
            float bestAlpha = 0f;
            string playerFac = AiCore.GetCachedFaction(player);

            int count = cursorSoldiers.Count;
            for (int i = 0; i < count; i++)
            {
                CursorSoldierData csd = cursorSoldiers[i];
                if (csd == null) continue;
                Soldier s = csd.soldier;
                if (s == null || !s.IsAlive || s.Pointer == player.Pointer) continue;

                if (!AiCore.AreSameSideFactions(playerFac, AiCore.GetCachedFaction(s)))
                    continue;

                if (s.IsOnVehicle() && player.IsOnVehicle())
                    continue;

                Vector3 uiPos = s.UINamePos();
                if (Marker3DGUI.IsOccluded(s, 2, uiPos))
                    continue;

                Vector3 screenPos3 = cam.WorldToScreenPoint(uiPos);
                if (screenPos3.z <= 0f)
                    continue;

                Vector2 screenPos2 = new Vector2(screenPos3.x, screenPos3.y);
                float distToCursor = Vector2.Distance(screenPos2, screenCenter);
                float alpha = (1.0f - (distToCursor / maxHoverRadius)) * 2.0f;
                if (alpha > bestAlpha)
                {
                    bestAlpha = alpha;
                    bestHoveredFriendly = s;
                }
            }

            if (bestHoveredFriendly != null && bestAlpha > 0.05f)
            {
                int tier = GetVeteranTier(bestHoveredFriendly);
                if (tier > 0)
                {
                    EnsureBadgeTextures();
                    Texture2D tex = tier == 1 ? _texVet1 : (tier == 2 ? _texVet2 : _texVet3);
                    if (tex != null)
                    {
                        Vector3 badgeWorldPos = bestHoveredFriendly.UINamePos() + new Vector3(0f, 0.26f, 0f);
                        float clampedAlpha = Mathf.Clamp01(bestAlpha) * 0.92f;
                        float iconSize = tier == 3 ? 18f : 15f;
                        Marker3DGUI.Draw(bestHoveredFriendly, 77, tex, badgeWorldPos, iconSize, clampedAlpha);
                    }
                }
            }
        }

        public static void DrawInWorldSquadMemberNames()
        {
            try
            {
                if (!VisionConfig.EnableMod.Value || !VisionConfig.EnableInWorldSquadMemberNames.Value)
                    return;

                Event ev = Event.current;
                if (ev == null || ev.type != EventType.Repaint)
                    return;

                if (IsSpectatorCameraModeActive(out _, out _) ||
                    SavableData.Settings == null ||
                    SavableData.Settings.system == null ||
                    !SavableData.Settings.system.enableGUI ||
                    SavableData.Settings.system.disable3DMarkers)
                {
                    return;
                }

                PlayerController pc = PlayerController.currentController;
                if (pc == null)
                    return;

                if (pc.IsAiming && !pc.IsLimitedAiming)
                    return;

                Soldier player = pc.ControlledCharacter;
                if (player == null || !player.IsAlive)
                    return;

                Squad playerSquad = player.joinedSquad;
                if (playerSquad == null || playerSquad.Pointer == IntPtr.Zero)
                    return;

                var cursorSoldiers = pc.cursorSoldiers;
                if (cursorSoldiers == null || cursorSoldiers.Count == 0)
                    return;

                Camera cam = ResourcesManager.mainCamera;
                if (cam == null)
                    return;

                IntPtr playerPtr = player.Pointer;
                IntPtr playerSquadPtr = playerSquad.Pointer;
                bool playerOnVehicle = player.IsOnVehicle();
                Vector2 screenCenter = pc.screenCenter;
                float maxHoverRadius = Screen.height * 0.20f;

                // Pass 1: Identify the single center-hovered AI friendly that vanilla PlayerController.OnGUI (0x1807CBD20)
                // is already drawing so we never double-render on top of the hovered soldier.
                IntPtr vanillaHoveredPtr = IntPtr.Zero;
                float bestVanillaAlpha = 0f;
                int count = cursorSoldiers.Count;
                if (maxHoverRadius > 1f)
                {
                    for (int i = 0; i < count; i++)
                    {
                        CursorSoldierData csd = cursorSoldiers[i];
                        if (csd == null) continue;
                        Soldier s = csd.soldier;
                        if (s == null || !s.IsAlive || s.Pointer == playerPtr || s.IsPlayer())
                            continue;
                        if (playerOnVehicle && s.IsOnVehicle())
                            continue;

                        Vector3 uiPos = s.UINamePos();
                        if (Marker3DGUI.IsOccluded(s, 2, uiPos))
                            continue;

                        Vector3 sp = cam.WorldToScreenPoint(uiPos);
                        if (sp.z <= 0f)
                            continue;

                        float distToCursor = Vector2.Distance(new Vector2(sp.x, sp.y), screenCenter);
                        float alpha = (1.0f - (distToCursor / maxHoverRadius)) * 2.0f;
                        if (alpha > bestVanillaAlpha)
                        {
                            bestVanillaAlpha = alpha;
                            vanillaHoveredPtr = s.Pointer;
                        }
                    }
                }

                GUIStyle lblStyle = GUI.skin != null ? GUI.skin.label : null;
                TextAnchor oldAlign = TextAnchor.UpperLeft;
                Color oldColor = GUI.color;
                bool restoredStyle = false;

                try
                {
                    float screenH = Screen.height;
                    for (int i = 0; i < count; i++)
                    {
                        CursorSoldierData csd = cursorSoldiers[i];
                        if (csd == null) continue;
                        Soldier s = csd.soldier;
                        if (s == null || !s.IsAlive || s.Pointer == playerPtr || s.Pointer == vanillaHoveredPtr)
                            continue;

                        // Human Co-op players already have their nickname rendered by vanilla PlayerController.OnGUI
                        if (s.IsPlayer())
                            continue;

                        // Strictly restrict always-visible 3D overhead names to members of the player's own squad!
                        Squad sq = s.joinedSquad;
                        if (sq == null || sq.Pointer != playerSquadPtr)
                            continue;

                        if (playerOnVehicle && s.IsOnVehicle())
                            continue;

                        string dispName = s.name_surname;
                        if (string.IsNullOrEmpty(dispName))
                            continue;

                        Vector3 uiPos = s.UINamePos();
                        if (Marker3DGUI.IsOccluded(s, 2, uiPos))
                            continue;

                        Vector3 sp = cam.WorldToScreenPoint(uiPos);
                        if (sp.z <= 0.5f || sp.z > 65.0f)
                            continue;

                        float distAlpha = Mathf.Clamp01((65.0f - sp.z) / 20.0f) * 0.88f;
                        if (distAlpha <= 0.05f)
                            continue;

                        if (!restoredStyle && lblStyle != null)
                        {
                            oldAlign = lblStyle.alignment;
                            lblStyle.alignment = TextAnchor.MiddleCenter;
                            restoredStyle = true;
                        }

                        if (s.IsInjured || s.isBleeding)
                            GUI.color = new Color(1.0f, 0.42f, 0.42f, distAlpha);
                        else
                            GUI.color = new Color(0.58f, 1.0f, 0.64f, distAlpha);

                        Rect rect = new Rect(sp.x - 150f, (screenH - sp.y) - 25f, 300f, 50f);
                        GuiExtension.OutlinedLabel(rect, dispName, 1);
                    }
                }
                finally
                {
                    if (restoredStyle && lblStyle != null)
                        lblStyle.alignment = oldAlign;
                    GUI.color = oldColor;
                }
            }
            catch { }
        }

        private static GameObject _squadHeaderGo;
        private static RectTransform _squadHeaderRect;
        private static Text _squadHeaderText;
        private static float _nextSquadRosterSyncCheckTime;

        public static void DetachSquadHeaderBeforeClear()
        {
            try
            {
                if (_squadHeaderRect != null)
                {
                    _squadHeaderRect.SetParent(null, false);
                }
                if (_squadHeaderGo != null && _squadHeaderGo.activeSelf)
                {
                    _squadHeaderGo.SetActive(false);
                }
            }
            catch { }
        }

        public static void RefreshSquadHeaderLabel()
        {
            try
            {
                if (!VisionConfig.EnableMod.Value)
                {
                    if (_squadHeaderGo != null && _squadHeaderGo.activeSelf)
                        _squadHeaderGo.SetActive(false);
                    return;
                }

                PlayerGUI pgui = PlayerGUI.instance;
                if (pgui == null || pgui.squadGUI_Panel == null || pgui.squadLabelPrefab == null)
                    return;

                Transform panel = pgui.squadGUI_Panel;
                int childCount = panel.childCount;
                int memberLabelCount = 0;
                for (int i = 0; i < childCount; i++)
                {
                    Transform child = panel.GetChild(i);
                    if (child == null) continue;
                    SquadSoldierLabel lbl = child.GetComponent<SquadSoldierLabel>();
                    if (lbl != null)
                        memberLabelCount++;
                }

                Squad guiSquad = PlayerGUI.GUISquad;
                if (guiSquad == null || guiSquad.Pointer == IntPtr.Zero)
                {
                    Soldier localSol = Soldier.CurrentControlledSoldierOrNull();
                    if (localSol != null)
                        guiSquad = localSol.joinedSquad;
                }

                if (memberLabelCount <= 0 || guiSquad == null || guiSquad.Pointer == IntPtr.Zero)
                {
                    if (_squadHeaderGo != null && _squadHeaderGo.activeSelf)
                        _squadHeaderGo.SetActive(false);
                    return;
                }

                RectTransform prefabRect = pgui.squadLabelPrefab.GetComponent<RectTransform>();
                if (prefabRect == null)
                    return;

                if (_squadHeaderGo == null || _squadHeaderRect == null || _squadHeaderText == null)
                {
                    SquadSoldierLabel prefabLbl = pgui.squadLabelPrefab.GetComponent<SquadSoldierLabel>();
                    Text templateText = prefabLbl != null ? prefabLbl.name_surname : null;
                    if (templateText == null || templateText.gameObject == null)
                        return;

                    _squadHeaderGo = UnityEngine.Object.Instantiate(templateText.gameObject, panel);
                    _squadHeaderGo.name = "BH_SquadHeaderLabel";
                    _squadHeaderRect = _squadHeaderGo.GetComponent<RectTransform>();
                    _squadHeaderText = _squadHeaderGo.GetComponent<Text>();
                    if (_squadHeaderRect == null || _squadHeaderText == null)
                        return;

                    _squadHeaderText.fontStyle = FontStyle.Bold;
                    _squadHeaderText.alignment = TextAnchor.MiddleLeft;
                    _squadHeaderText.horizontalOverflow = HorizontalWrapMode.Overflow;
                    _squadHeaderText.verticalOverflow = VerticalWrapMode.Overflow;
                    _squadHeaderText.raycastTarget = false;
                }

                if (_squadHeaderRect.parent != panel)
                {
                    _squadHeaderRect.SetParent(panel, false);
                }

                _squadHeaderRect.anchorMin = prefabRect.anchorMin;
                _squadHeaderRect.anchorMax = prefabRect.anchorMax;
                _squadHeaderRect.pivot = prefabRect.pivot;
                _squadHeaderRect.localScale = Vector3.one;
                float rowHeight = prefabRect.sizeDelta.y > 1f ? prefabRect.sizeDelta.y : 25.0f;
                _squadHeaderRect.sizeDelta = new Vector2(Mathf.Max(prefabRect.sizeDelta.x, 280.0f), rowHeight);

                float headerY = (memberLabelCount + 1) * rowHeight + PlayerGUI.labelStartY - 2.0f;
                _squadHeaderRect.anchoredPosition = new Vector2(4.0f, headerY);

                string callsign = AiCore.FormatSquadPhoneticCallsign(guiSquad);
                string headerStr = !string.IsNullOrEmpty(callsign) ? callsign.ToUpperInvariant() : "SQUAD";
                if (_squadHeaderText.text != headerStr)
                    _squadHeaderText.text = headerStr;

                _squadHeaderText.color = new Color(1.00f, 0.88f, 0.45f, 0.96f);
                _squadHeaderText.enabled = true;
                if (!_squadHeaderGo.activeSelf)
                    _squadHeaderGo.SetActive(true);
            }
            catch { }
        }

        public static void UpdateSquadRosterHeaderAndFastSync()
        {
            if (!VisionConfig.EnableMod.Value || Time.timeScale <= 0.01f)
                return;

            float now = Time.unscaledTime;
            if (now < _nextSquadRosterSyncCheckTime && (_nextSquadRosterSyncCheckTime - now) <= 2.0f)
                return;
            _nextSquadRosterSyncCheckTime = now + 0.20f;

            try
            {
                PlayerGUI pgui = PlayerGUI.instance;
                if (pgui == null || pgui.squadGUI_Panel == null)
                    return;

                if (!PlayerGUI.IsSelectingSquad())
                {
                    Soldier localPlayer = Soldier.CurrentControlledSoldierOrNull();
                    if (localPlayer != null && localPlayer.Pointer != IntPtr.Zero && localPlayer.IsAlive)
                    {
                        Squad pSq = localPlayer.joinedSquad;
                        if (pSq != null && pSq.Pointer != IntPtr.Zero && (pSq.CountMembers > 1 || pSq.isOpenForJoiners))
                        {
                            Squad guiSq = PlayerGUI.GUISquad;
                            int panelMemberLabels = 0;
                            Transform panel = pgui.squadGUI_Panel;
                            int childCount = panel.childCount;
                            for (int i = 0; i < childCount; i++)
                            {
                                Transform child = panel.GetChild(i);
                                if (child == null) continue;
                                SquadSoldierLabel lbl = child.GetComponent<SquadSoldierLabel>();
                                if (lbl != null && !lbl.isRespawnAction)
                                    panelMemberLabels++;
                            }

                            if (guiSq == null ||
                                guiSq.Pointer != pSq.Pointer ||
                                panelMemberLabels != pSq.CountMembers)
                            {
                                PlayerGUI.ShowSquadList(pSq, 0.15f);
                                PlayerGUI.UpdateSquadGUI();
                            }
                        }
                    }
                }

                RefreshSquadHeaderLabel();
            }
            catch { }
        }

        public static void RefreshBottomLeftSquadRosterVisuals()
        {
            try
            {
                PlayerGUI pgui = PlayerGUI.instance;
                if (pgui == null || pgui.squadGUI_Panel == null)
                    return;

                Transform panel = pgui.squadGUI_Panel;
                int childCount = panel.childCount;
                for (int i = 0; i < childCount; i++)
                {
                    Transform child = panel.GetChild(i);
                    if (child == null) continue;
                    SquadSoldierLabel lbl = child.GetComponent<SquadSoldierLabel>();
                    if (lbl != null && !lbl.isRespawnAction)
                    {
                        lbl.RefreshVisual();
                    }
                }

                RefreshSquadHeaderLabel();
            }
            catch { }
        }

        private static readonly HashSet<IntPtr> _reusableAlivePtrs = new HashSet<IntPtr>(256);
        private static readonly HashSet<IntPtr> _reusableVehiclePtrs = new HashSet<IntPtr>(64);
        private static readonly List<IntPtr> _reusableExpiredTasks = new List<IntPtr>(32);
        private static IntPtr _lastObservedBattleManagerPtr;
        private static float _lastSceneTimeObserved;
        private static int _cleanupSlice;

        public static void CleanupDictionaries(bool forceFull = true)
        {
            long profCleanupStart = HitchProfiler.Begin();
            float now = Time.time;
            BattleManager bm = BattleManager.instance;
            IntPtr curBmPtr = bm != null ? bm.Pointer : IntPtr.Zero;
            bool bmChanged = curBmPtr != IntPtr.Zero &&
                             _lastObservedBattleManagerPtr != IntPtr.Zero &&
                             curBmPtr != _lastObservedBattleManagerPtr;
            if (curBmPtr != IntPtr.Zero)
                _lastObservedBattleManagerPtr = curBmPtr;

            // Detect match/scene restart (BattleManager instance swap or Time.time reset) and perform a full cache reset
            bool sceneRestarted = bmChanged || (now < _lastSceneTimeObserved - 2.0f);
            _lastSceneTimeObserved = now;

            if (sceneRestarted)
            {
                forceFull = true;
                HitchProfiler.ResetOnSceneChange();
                BotActionTrace.OnSceneRestart();
                ResetMultiplayerBattleStatLedger();
                _specHasValidTarget = false;
                _specTargetSoldier = null;
                _specTargetPtr = IntPtr.Zero;
                _lastFormattedSpecPtr = IntPtr.Zero;
                _coverExposureCache.Clear();
                _medicPatientClaims.Clear();
                _cachedVehiclePersonalities.Clear();
                _barbedWireColliderCache.Clear();
                VisionPatches.ClearVisionCaches();
                AiCore.ClearSpatialCaches();
                ClearBallisticsCaches(forceClear: true);
                ClearDoorCaches(forceFull: true);
                ClearCombinedArmsCaches();
                ClearGoapCaches();
                ClearConquestQuartermasterState();
                VeteranAndTacticsPatches.PruneExpiredDamageAttributions(Time.time, forceClear: true);
            }

            AiCore.EnsureWorldSnapshot();
            _reusableAlivePtrs.Clear();
            int vetCount = 0;
            int solCount = AiCore.WorldSoldierCount;
            var soldiers = AiCore.WorldSoldiers;
            for (int i = 0; i < solCount; i++)
            {
                ref AiCore.SoldierSnapshot sol = ref soldiers[i];
                if (sol.Ptr != IntPtr.Zero)
                {
                    _reusableAlivePtrs.Add(sol.Ptr);
                    if (!sol.IsPlayer && GetVeteranTierByPtr(sol.Ptr) > 0)
                        vetCount++;
                }
            }
            // CRITICAL: Include incapacitated (!IsDead && IsInjured) soldiers in _reusableAlivePtrs so downed veterans
            // keep their XP/rank while awaiting a Medic revive, active _medicPatientClaims aren't wiped mid-rescue,
            // and incapacitated players keep _lastDamageByVictim attribution until the Death Screen!
            int injCount = AiCore.WorldInjuredSoldierCount;
            var injured = AiCore.WorldInjuredSoldiers;
            for (int i = 0; i < injCount; i++)
            {
                ref AiCore.SoldierSnapshot inj = ref injured[i];
                if (inj.Ptr != IntPtr.Zero)
                {
                    _reusableAlivePtrs.Add(inj.Ptr);
                }
            }
            Soldier controlled = Soldier.CurrentControlledSoldierOrNull();
            if (controlled != null && !controlled.IsDead && controlled.Pointer != IntPtr.Zero)
                _reusableAlivePtrs.Add(controlled.Pointer);

            ActiveVeteransCount = vetCount;

            int slice = _cleanupSlice;
            _cleanupSlice = (slice + 1) & 7;
            HitchProfiler.RecordCleanupSlice(slice, forceFull);

            if (forceFull || slice == 3)
            {
                AiCore.PruneDict(_meleeTargetSlots, _reusableAlivePtrs);
            }

            if (forceFull || slice == 5)
            {
                AiCore.PruneDict(_assignedEmplacementUntil, _reusableAlivePtrs);
                AiCore.PruneDict(_assignedEmplacementVehicle, _reusableAlivePtrs);
            }

            if (forceFull || slice == 6)
            {
                AiCore.PruneDict(_medicPatientClaims, _reusableAlivePtrs);
                VeteranAndTacticsPatches.PruneExpiredDamageAttributions(Time.time);
                BotActionTrace.PruneAliveBots(_reusableAlivePtrs);
            }

            if (forceFull || slice == 7)
            {
                _reusableVehiclePtrs.Clear();
                var allVehicles = Vehicle.allVehicles;
                if (allVehicles != null)
                {
                    int vCount = allVehicles.Count;
                    for (int i = 0; i < vCount; i++)
                    {
                        Vehicle v = allVehicles[i];
                        if (!ReferenceEquals(v, null) && v != null && v.Pointer != IntPtr.Zero)
                            _reusableVehiclePtrs.Add(v.Pointer);
                    }
                }

                AiCore.PruneFastHandleCaches(_reusableAlivePtrs, _reusableVehiclePtrs);
                AiCore.PruneDict(_cachedVehiclePersonalities, _reusableVehiclePtrs);
                AiCore.PruneDict(_cachedVehicleSolidColliders, _reusableVehiclePtrs);
                AiCore.PruneDict(_cachedVehicleExtents, _reusableVehiclePtrs);
                _reusableVehiclePtrs.Clear();

                if (_cachedInventoryOwners.Count > 2048)
                    _cachedInventoryOwners.Clear();

                // Reuse _reusableAlivePtrs to prune per-squad dictionaries against active squads in zero extra allocations
                _reusableAlivePtrs.Clear();
                int sqCount = AiCore.WorldSquadCount;
                var squads = AiCore.WorldSquads;
                for (int i = 0; i < sqCount; i++)
                {
                    if (squads[i].Ptr != IntPtr.Zero)
                        _reusableAlivePtrs.Add(squads[i].Ptr);
                }
                AiCore.PruneDict(_squadBlackboards, _reusableAlivePtrs);
                AiCore.PruneDict(_nextSquadVoiceTime, _reusableAlivePtrs);
                AiCore.PruneDict(_pincerOrderedTaskBySquad, _reusableAlivePtrs);
                AiCore.PruneDict(_garrisonAssignedObjectiveBySquad, _reusableAlivePtrs);
                AiCore.PruneDict(_squadTaskCommittedUntil, _reusableAlivePtrs);
                AiCore.PruneDict(_nextSquadAttritionPivotTime, _reusableAlivePtrs);
                AiCore.PruneDict(_squadOrderedToVehicle, _reusableAlivePtrs);
                AiCore.PruneDict(_aiCovertHoldFireExpireTime, _reusableAlivePtrs);
                AiCore.PruneDict(_aiFollowColumnUntil, _reusableAlivePtrs);
                AiCore.PruneDict(_nextAiSquadChargeTime, _reusableAlivePtrs);
                AiCore.PruneDict(_nextAiCovertOrderTime, _reusableAlivePtrs);
                AiCore.PruneDict(_nextAiFollowOrderTime, _reusableAlivePtrs);
                AiCore.PruneDict(_nextAiSquadRetreatTime, _reusableAlivePtrs);
                AiCore.PruneDict(_nextSquadRadioScavengeTime, _reusableAlivePtrs);
                AiCore.PruneDict(_aiSquadRetreatUntil, _reusableAlivePtrs);
                AiCore.PruneDict(_aiChargeBaselineDeaths, _reusableAlivePtrs);
                AiCore.PruneSet(_aiCovertHoldFireSquads, _reusableAlivePtrs);
                AiCore.PruneSet(_aiOrderedChargeSquads, _reusableAlivePtrs);
                AiCore.PruneSet(_initialHqSpawnBriefingConsumed, _reusableAlivePtrs);
                AiCore.PruneSquadClassificationCaches(_reusableAlivePtrs);

                if (_coverExposureCache.Count > 4096)
                    _coverExposureCache.Clear();
                if (_turretAiByTurretPtr.Count > 256)
                    _turretAiByTurretPtr.Clear();
                if (_turretSightReaction.Count > 256)
                    _turretSightReaction.Clear();
                if (_pendingTurretIntercom.Count > 128)
                    _pendingTurretIntercom.Clear();
                if (_planeAttackReaction.Count > 128)
                    _planeAttackReaction.Clear();
                AiCore.PruneSpatialCachesIfOversized();

                if (_repairTaskStartTime.Count > 0)
                {
                    _reusableExpiredTasks.Clear();
                    foreach (var kvp in _repairTaskStartTime)
                    {
                        if (now - kvp.Value > 60f)
                            _reusableExpiredTasks.Add(kvp.Key);
                    }
                    for (int i = 0; i < _reusableExpiredTasks.Count; i++)
                    {
                        _repairTaskStartTime.Remove(_reusableExpiredTasks[i]);
                        _countedRepairTasks.Remove(_reusableExpiredTasks[i]);
                    }
                    _reusableExpiredTasks.Clear();
                }
            }

            _reusableAlivePtrs.Clear();
            HitchProfiler.End(HitchBucket.DictCleanup, profCleanupStart);
        }

        // =====================================================================
        // Squad Roster UI Always-Show Names, Active Soldier Marker, Squad Header & Fast Link-Up Sync
        // =====================================================================
        [HarmonyPatch(typeof(PlayerGUI), nameof(PlayerGUI.ClearSquadList))]
        public static class PlayerGUI_ClearSquadList_Patch
        {
            public static void Prefix()
            {
                DetachSquadHeaderBeforeClear();
            }
        }

        [HarmonyPatch(typeof(PlayerGUI), nameof(PlayerGUI.ShowSquadList))]
        public static class PlayerGUI_ShowSquadList_Patch
        {
            public static void Postfix()
            {
                RefreshSquadHeaderLabel();
            }
        }

        [HarmonyPatch(typeof(PlayerGUI), nameof(PlayerGUI.LayoutSquadEntries))]
        public static class PlayerGUI_LayoutSquadEntries_Patch
        {
            public static void Postfix()
            {
                RefreshSquadHeaderLabel();
            }
        }

        [HarmonyPatch(typeof(Squad), nameof(Squad.Join))]
        public static class Squad_Join_Patch
        {
            public static void Postfix(Squad __instance, Soldier __0)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value ||
                        __instance == null ||
                        __instance.Pointer == IntPtr.Zero ||
                        __0 == null ||
                        __0.Pointer == IntPtr.Zero ||
                        PlayerGUI.instance == null ||
                        PlayerGUI.IsSelectingSquad())
                    {
                        return;
                    }

                    Soldier localPlayer = Soldier.CurrentControlledSoldierOrNull();
                    if (localPlayer != null &&
                        localPlayer.Pointer != IntPtr.Zero &&
                        localPlayer.IsAlive &&
                        localPlayer.joinedSquad != null &&
                        localPlayer.joinedSquad.Pointer == __instance.Pointer)
                    {
                        PlayerGUI.ShowSquadList(__instance, 0.15f);
                        PlayerGUI.UpdateSquadGUI();
                        RefreshSquadHeaderLabel();
                    }
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(SquadSoldierLabel), nameof(SquadSoldierLabel.RefreshVisual))]
        public static class SquadSoldierLabel_RefreshVisual_Patch
        {
            private const string ActiveSoldierPrefix = "\u25ba ";

            public static void Postfix(SquadSoldierLabel __instance)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value || __instance == null || __instance.isRespawnAction)
                        return;

                    Soldier s = __instance.assignedSoldier;
                    Text nameLabel = __instance.name_surname;
                    if (s == null || nameLabel == null)
                        return;

                    if (VisionConfig.EnableAlwaysShowSquadNames.Value)
                    {
                        nameLabel.enabled = true;
                    }

                    bool isSelecting = PlayerGUI.IsSelectingSquad();
                    bool isHighlightedInSelect = isSelecting &&
                        ((PlayerGUI.highLightedUnit != null && PlayerGUI.highLightedUnit.Pointer == s.Pointer) ||
                         (SquadSoldierLabel.currentHighlighted != null && SquadSoldierLabel.currentHighlighted.Pointer == __instance.Pointer));
                    bool isCurrentPlayerSoldier = !isSelecting && (s.IsPlayer() || PlayerController.CharacterIsPlayer(s));
                    bool isActiveMarkerRow = isHighlightedInSelect || isCurrentPlayerSoldier;

                    bool showVet = VisionConfig.EnableVeteranSystem.Value && VisionConfig.ShowVeteranSquadRosterTags.Value;
                    bool showPers = VisionConfig.EnableRolePersonalities.Value && VisionConfig.ShowPersonalityInSquadRoster.Value;

                    string curText = nameLabel.text ?? "";
                    if (curText.StartsWith(ActiveSoldierPrefix, StringComparison.Ordinal))
                    {
                        curText = curText.Substring(ActiveSoldierPrefix.Length);
                    }
                    int bracketIdx = curText.IndexOf(" [", StringComparison.Ordinal);
                    string baseText = bracketIdx >= 0 ? curText.Substring(0, bracketIdx) : curText;

                    string tag = (showVet || showPers) ? BuildSoldierBracketTag(s, showVet, showPers, true) : "";
                    string desiredText = (isActiveMarkerRow ? ActiveSoldierPrefix : "") + baseText + tag;
                    if (nameLabel.text != desiredText)
                    {
                        nameLabel.text = desiredText;
                    }

                    // Preserve vanilla's urgent red injuredNameColor when a squadmate is bleeding or incapacitated
                    if (!s.IsInjured && !s.isBleeding)
                    {
                        bool isOtherHumanPlayer = __instance.EvaluateState() == SquadSoldierLabel.LabelState.OtherPlayer;

                        if (isActiveMarkerRow)
                        {
                            Color activeHighlight = isSelecting
                                ? __instance.highlightColor
                                : Color.white;
                            nameLabel.color = activeHighlight;
                            if (!isSelecting && __instance.role != null)
                            {
                                __instance.role.color = activeHighlight;
                            }
                        }
                        else if (!isOtherHumanPlayer)
                        {
                            int tier = showVet ? GetVeteranTier(s) : 0;
                            if (tier == 1)
                                nameLabel.color = new Color(0.84f, 0.72f, 0.50f, 0.90f);
                            else if (tier == 2)
                                nameLabel.color = new Color(0.74f, 0.84f, 0.92f, 0.90f);
                            else if (tier >= 3)
                                nameLabel.color = new Color(0.95f, 0.80f, 0.24f, 0.92f);
                            else
                                nameLabel.color = new Color(0.68f, 0.70f, 0.73f, 0.88f);
                        }
                    }
                }
                catch { }
            }
        }
    }
}
