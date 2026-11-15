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
        private static bool _revealMapPickerSpoilers;
        private static int _activeConfigTab;

        private struct PickerBattleEntry
        {
            public string DisplayName;
            public string UniqueId;
            public string BattleId;
            public string PreviousBattleId;
            public string SaveDataId;
            public string PreviousSaveDataId;
            public bool IsVanilla;
            public bool InitialIsUnlocked;
            public bool MetadataResolved;
            public bool UnitCountsResolved;
            public bool IsFinale;
            public int DefaultMaxAllies;
            public int DefaultMaxAxis;
            public ICampaignTreeBattleData BattleRef;
        }

        private struct PickerCampaignEntry
        {
            public string CampaignName;
            public bool TopologySorted;
            public List<PickerBattleEntry> Battles;
        }

        private struct QueuedPickerEntry
        {
            public PickerBattleEntry Battle;
            public string VisibleName;
        }

        private const int MapPickerRowsPerPage = 15;
        private static readonly List<PickerCampaignEntry> _mapPickerCampaigns = new List<PickerCampaignEntry>(24);
        private static readonly List<QueuedPickerEntry> _mapPickerPlaylist = new List<QueuedPickerEntry>(16);
        private static float _mapPickerBotScale = 1.0f;
        private static bool _mapPickerBotScaleDirty;
        private static bool _mapPickerInitialized;
        private static bool _mapPickerUserPickedBattle;
        private static int _lastCustomCampaignCount = -1;
        private static int _selectedCampaignIdx;
        private static int _selectedBattleIdx;
        private static int _campaignPage;
        private static int _battlePage;
        private static int _mpSwitchStage;
        private static float _mpSwitchStageDeadline;
        private static string _mpSwitchDisplayName;
        private static bool _mpSwitchRetriedConnect;

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
                    VeteranAndTacticsPatches.UpdateQuickTapSpot(_showConfigMenu || _showMapPicker);
                    VeteranAndTacticsPatches.CheckLocalPlayerFirstPersonShadowTeardown();
                    if (_showMapPicker)
                        BattleManager_OnGUI_Patch.StepResolveSelectedCampaignBattles();

                    var input = UnityInput.Current;
                    if (input != null)
                    {
                        KeyCode mapPickerKey = VisionConfig.MapPickerHotkey != null ? VisionConfig.MapPickerHotkey.Value : KeyCode.F5;
                        if (mapPickerKey != KeyCode.None && input.GetKeyDown(mapPickerKey))
                        {
                            _showMapPicker = !_showMapPicker;
                            if (_showMapPicker)
                            {
                                _showConfigMenu = false;
                                BattleManager_OnGUI_Patch.EnsureMapPickerCatalog();
                                BattleManager_OnGUI_Patch.TryAutoSelectActiveMissionInPicker();
                                Cursor.visible = true;
                                Cursor.lockState = CursorLockMode.None;
                            }
                        }
                        else if (input.GetKeyDown(KeyCode.F6))
                        {
                            _showConfigMenu = !_showConfigMenu;
                            if (_showConfigMenu)
                            {
                                _showMapPicker = false;
                                Cursor.visible = true;
                                Cursor.lockState = CursorLockMode.None;
                            }
                        }
                        else if (input.GetKeyDown(KeyCode.F8))
                        {
                            VisionConfig.EnableMod.Value = !VisionConfig.EnableMod.Value;
                            Plugin.Instance.Config.Save();
                            VeteranAndTacticsPatches.RefreshLocalPlayerFirstPersonShadow();
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
                            VeteranAndTacticsPatches.RefreshLocalPlayerFirstPersonShadow();
                            ShowToast($"[Battle Hardened v{Plugin.PluginVersion}] Reloaded Config! Stand:{VisionConfig.StandingSightRange.Value}m | Crouch:{VisionConfig.CrouchingSightRange.Value}m | Prone:{VisionConfig.ProneSightRange.Value}m | Personalities:{VisionConfig.EnableRolePersonalities.Value}");
                        }
                        else if (input.GetKeyDown(KeyCode.F10))
                        {
                            VeteranAndTacticsPatches.CleanupDictionaries();
                            ShowToast(
                                $"[Battle Hardened v{Plugin.PluginVersion} Telemetry] MaxFrame:{AiCore.RollingMaxFrameMs:F1}ms (Hitches:{AiCore.TotalHitchesOver28ms}) | Vets:{VeteranAndTacticsPatches.ActiveVeteransCount} (Prom:{VeteranAndTacticsPatches.FieldPromotionsCount}) | ExtraTicks:{AiCore.TotalAdaptiveAiExtraTicks} (PingSkip:{AiCore.TotalPingGovernorSkippedCycles}) | FFSaved:{VeteranAndTacticsPatches.FriendlyFirePrevented} (Yield:{VeteranAndTacticsPatches.PlayerCrosshairYieldsCount}) | MedRedirect:{VeteranAndTacticsPatches.MedicTriageRedirectsCount}\n" +
                                $"GOAP:{VeteranAndTacticsPatches.GoapPlansExecutedCount} (Anvil:{VeteranAndTacticsPatches.TeamNetAnvilHammerCount} Flank:{VeteranAndTacticsPatches.GoapPinAndFlankCount} Charge:{VeteranAndTacticsPatches.SquadChargesOrderedCount} Balk:{VeteranAndTacticsPatches.SquadLeaderBalksCount}/{VeteranAndTacticsPatches.IndividualSelfPreservationBalksCount} Covert:{VeteranAndTacticsPatches.SquadCovertOrdersCount} Follow:{VeteranAndTacticsPatches.SquadFollowOrdersCount} Retreat:{VeteranAndTacticsPatches.TacticalRetreatsCount} Smoke:{VeteranAndTacticsPatches.GoapSmokeScreensCount} RadioCut:{VeteranAndTacticsPatches.RadioNetCutoffsCount}) | WepUp:{VeteranAndTacticsPatches.RoleWeaponUpgradesCount} | BuddyResup:{VeteranAndTacticsPatches.BuddyResuppliesCount} | RadioLoot:{VeteranAndTacticsPatches.FieldRadiosInheritedCount} | Helmets:{VeteranAndTacticsPatches.HelmetsRecoveredCount} | Dragged:{VeteranAndTacticsPatches.CasualtiesDraggedCount} | Sap:{AiCore.SpecialMunitionsUsedCount}\n" +
                                $"Drops:{VeteranAndTacticsPatches.SelfPreservationDrops} | Peeks:{VeteranAndTacticsPatches.AutoPeeksTriggered} | BndBlk:{VeteranAndTacticsPatches.UnsafeBandagesBlockedCount} | IndoorCrouch:{VeteranAndTacticsPatches.IndoorPronePreventedCount} | Pies:{VeteranAndTacticsPatches.CornerPiesExecutedCount} | TankCov:{VeteranAndTacticsPatches.TankHullCoverUsesCount} | Bounds:{VeteranAndTacticsPatches.BoundingOverwatchHoldsCount} | Doors:{VeteranAndTacticsPatches.DoorsStrategicallyClosedCount} | Bipods:{VeteranAndTacticsPatches.BipodsDeployed} | WallBlk:{VeteranAndTacticsPatches.WallsBlocked} | FragBlk:{VeteranAndTacticsPatches.UselessTankFragsBlockedCount} | Melee:{VeteranAndTacticsPatches.MeleeAttacksTriggered} (Sup:{VeteranAndTacticsPatches.CloseGunfireMeleeSuppressedCount}) | Vault:{VeteranAndTacticsPatches.WallsVaulted} (Rej:{VeteranAndTacticsPatches.SlopedVaultsRejectedCount}) | Surr:{VeteranAndTacticsPatches.ShellShockSurrendersCount}/Lib:{VeteranAndTacticsPatches.ComradesLiberated}\n" +
                                $"Garrisons:{VeteranAndTacticsPatches.MacroGarrisonsAssignedCount} (Pivots:{VeteranAndTacticsPatches.MacroObjectivePivotsCount}) | ConqBuy:{VeteranAndTacticsPatches.ConquestPurchasesCount} | SpawnSaved:{VeteranAndTacticsPatches.UnderwaterSpawnsRescuedCount} | Emplace:{VeteranAndTacticsPatches.StaticGunsStaffedCount} | APC:{VeteranAndTacticsPatches.SmartApcDismountsCount} | TankHold:{VeteranAndTacticsPatches.TankStandoffHoldsCount} | TankSpread:{VeteranAndTacticsPatches.ArmoredDispersionAdjustmentsCount} | SmartAmmo:{VeteranAndTacticsPatches.SmartAmmoSwitchesCount} | Unsticks:{VeteranAndTacticsPatches.TankRockUnsticksCount} (Unstk:{VeteranAndTacticsPatches.FootBotUnsticksCount}) | Repairs:{VeteranAndTacticsPatches.ShelteredRepairsCount} | RoadkillSaved:{VeteranAndTacticsPatches.FriendlyRoadkillsPreventedCount} (ClientRK:{VeteranAndTacticsPatches.RemoteClientRoadkillsAssistedCount})\n" +
                                $"CombArms:{VeteranAndTacticsPatches.CombinedArmsPriorityAssistsCount} (Intercom:{VeteranAndTacticsPatches.TankIntercomHandoffsCount} Kamikaze:{VeteranAndTacticsPatches.KamikazeDivesTriggeredCount}) | Callouts:{VeteranAndTacticsPatches.LocalCalloutsSharedCount} | Supp:{VeteranAndTacticsPatches.SuppressiveEventsCount} | WoodPen:{VeteranAndTacticsPatches.ThinCoverPenetrationsCount} | TopAP:{VeteranAndTacticsPatches.AircraftTopAttackPenetrationsAssistedCount} (HostAssist:{VeteranAndTacticsPatches.HostVanillaClientApAssistsCount}) | CrewBlastBlk:{VeteranAndTacticsPatches.EnclosedCrewExplosionHitsBlockedCount} | CrewClipBlk:{VeteranAndTacticsPatches.EnclosedCrewBulletClipsBlockedCount} | AirMgBlk:{VeteranAndTacticsPatches.AirplanePilotMgHitsBlockedCount} | Chutes:{VeteranAndTacticsPatches.ParachutesDriftedCount} | BloodOcc:{VeteranAndTacticsPatches.BloodSmokeWaterOccludedCount}",
                                9.5f);
                        }
                        else if (input.GetKeyDown(KeyCode.F) &&
                                 !_showConfigMenu &&
                                 !_showMapPicker &&
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
                            $"Ticks:{AiCore.TotalAdaptiveAiExtraTicks} (PingSkip:{AiCore.TotalPingGovernorSkippedCycles}) | FFSaved:{VeteranAndTacticsPatches.FriendlyFirePrevented} (Yield:{VeteranAndTacticsPatches.PlayerCrosshairYieldsCount}) | Repairs:{VeteranAndTacticsPatches.ShelteredRepairsCount}",
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

        // Vanilla BattleLoader.ConnectToNextMatch (0x180A6E2CD) accidentally sets `this.waitBeforeJoining = (float)this.waitTime`
        // (reading the int retry counter `this.waitTime` = 0 instead of the `float waitTime` RPC parameter), causing Clients
        // to race into JoinRoom before the Host has finished leaving the old room and creating the new room.
        [HarmonyPatch(typeof(BattleLoader), nameof(BattleLoader.ConnectToNextMatch))]
        public static class BattleLoader_ConnectToNextMatch_Patch
        {
            public static void Postfix(BattleLoader __instance, float __1)
            {
                try
                {
                    if (__instance != null && __1 > __instance.waitBeforeJoining)
                    {
                        __instance.waitBeforeJoining = __1;
                    }
                }
                catch { }
            }
        }

        private static void BeginHostLeaveRoomForMapSwitch(BattleLoader bl)
        {
            if (bl == null)
                bl = BattleLoader.instance;

            // Unregister the dying scene's BattleLoader from Photon callbacks (OnDisable -> RemoveCallbackTarget)
            // so BattleLoader.OnConnectedToMaster does not fire ConnectUsingSettings() / ConnectToNextMatchCR on the Host.
            if (bl != null)
                bl.enabled = false;

            MatchData.actorType = MatchData.RoomActorType.master;
            Photon.Pun.PhotonNetwork.LeaveRoom();
            Photon.Pun.PhotonNetwork.SendAllOutgoingCommands();
            _mpSwitchStage = 2;
            _mpSwitchRetriedConnect = false;
            _mpSwitchStageDeadline = Time.unscaledTime + 3.5f;
        }

        private static void UpdatePendingMpMapSwitch()
        {
            if (_mpSwitchStage == 0)
                return;

            MatchData md = MatchData.data;
            if (md == null)
            {
                _mpSwitchStage = 0;
                return;
            }

            float now = Time.unscaledTime;
            if (_mpSwitchStage == 1)
            {
                Photon.Pun.PhotonNetwork.SendAllOutgoingCommands();
                bool allClientsLeft = !Photon.Pun.PhotonNetwork.InRoom ||
                                      Photon.Pun.PhotonNetwork.CurrentRoom == null ||
                                      Photon.Pun.PhotonNetwork.CurrentRoom.PlayerCount <= 1;
                if (allClientsLeft || now >= _mpSwitchStageDeadline)
                {
                    if (Photon.Pun.PhotonNetwork.InRoom)
                    {
                        BeginHostLeaveRoomForMapSwitch(BattleLoader.instance);
                    }
                    else
                    {
                        _mpSwitchStage = 2;
                        _mpSwitchRetriedConnect = false;
                        _mpSwitchStageDeadline = now + 3.5f;
                    }
                }
                return;
            }

            if (_mpSwitchStage == 2)
            {
                var netState = Photon.Pun.PhotonNetwork.NetworkClientState;
                if (!_mpSwitchRetriedConnect &&
                    netState == Photon.Realtime.ClientState.Disconnected &&
                    now < _mpSwitchStageDeadline)
                {
                    _mpSwitchRetriedConnect = true;
                    Photon.Pun.PhotonNetwork.ConnectUsingSettings();
                    return;
                }

                bool readyOnMaster = !Photon.Pun.PhotonNetwork.InRoom &&
                                     netState == Photon.Realtime.ClientState.ConnectedToMasterServer;
                if (readyOnMaster || now >= _mpSwitchStageDeadline)
                {
                    _mpSwitchStage = 0;
                    MatchData.actorType = MatchData.RoomActorType.master;
                    BattleManager.SetPlayerFaction("");
                    md.StartBattleLoading();
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
        public static class BattleManager_OnGUI_Patch
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
                        Cursor.visible = true;
                        Cursor.lockState = CursorLockMode.None;
                        DrawConfigMenu(virtualW, virtualH);
                    }
                    else if (_showMapPicker)
                    {
                        Cursor.visible = true;
                        Cursor.lockState = CursorLockMode.None;
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

            private static void DrawConfigMenu(float virtualW, float virtualH)
            {
                float panelW = 820f;
                float panelH = 684f;
                float panelX = (virtualW - panelW) * 0.5f;
                float panelY = (virtualH - panelH) * 0.5f;

                GUI.color = new Color(0.06f, 0.07f, 0.09f, 0.95f);
                GUI.Box(new Rect(panelX, panelY, panelW, panelH), "");

                GUI.color = new Color(1.0f, 0.88f, 0.35f, 1.0f);
                GUI.Label(new Rect(panelX + 20f, panelY + 10f, panelW - 240f, 22f),
                    $"Easy Red 2: Battle Hardened v{Plugin.PluginVersion}  |  Modular Feature Menu (F6)");

                KeyCode mpKey = VisionConfig.MapPickerHotkey != null ? VisionConfig.MapPickerHotkey.Value : KeyCode.F5;
                GUI.color = new Color(0.55f, 0.90f, 1.0f, 1.0f);
                if (GUI.Button(new Rect(panelX + panelW - 210f, panelY + 8f, 190f, 24f), $"Open Map Picker ({mpKey})"))
                {
                    _showConfigMenu = false;
                    _showMapPicker = true;
                    EnsureMapPickerCatalog();
                    TryAutoSelectActiveMissionInPicker();
                }

                // 5 Overarching Concept Tabs
                string[] tabNames =
                {
                    "1. GOAP & Bot Behaviors",
                    "2. Vanilla Fixes",
                    "3. Vision & Realism",
                    "4. Vets & Roles",
                    "5. Optimizations & QoL"
                };

                float tabY = panelY + 38f;
                float tabW = (panelW - 40f - 16f) / 5f;
                for (int t = 0; t < tabNames.Length; t++)
                {
                    bool active = _activeConfigTab == t;
                    GUI.color = active
                        ? new Color(1.0f, 0.86f, 0.32f, 1.0f)
                        : new Color(0.68f, 0.74f, 0.82f, 0.92f);
                    if (GUI.Button(new Rect(panelX + 20f + t * (tabW + 4f), tabY, tabW, 26f), tabNames[t]))
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
                            VisionConfig.EnableFirstPersonShadowFix,
                            VisionConfig.EnableBloodSmokeWaterOcclusion,
                            VisionConfig.NeverShootStandingInOpen,
                            VisionConfig.EnforceStandingWhenOutOfCombat,
                            VisionConfig.EnableIndoorCeilingPronePrevention,
                            VisionConfig.EnableWallShootingPrevention,
                            VisionConfig.EnableFriendlyFireAvoidance,
                            VisionConfig.EnableAntiDogpileMedicTriage);
                        DrawToggleButton(colLeftX, ref yL, colW, rowH, "First-Person Self-Shadow Fix (Body/Gun)", VisionConfig.EnableFirstPersonShadowFix);
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
                        DrawToggleButton(colRightX, ref yR, colW, rowH, "MP Casualties, Prisoners & Vehicle Sync Fix", VisionConfig.EnableMultiplayerBattleStatSyncFix);
                        DrawToggleButton(colRightX, ref yR, colW, rowH, "Friendly Right-of-Way & Client Roadkill Fix", VisionConfig.EnableFriendlyVehicleRightOfWay);
                        DrawToggleButton(colRightX, ref yR, colW, rowH, "Stop Small-Arms Fire at Tanks / Aim Crew", VisionConfig.PreventSmallArmsFireAtTanks);
                        DrawToggleButton(colRightX, ref yR, colW, rowH, "Plane Engine Audio Loop De-Throbber", VisionConfig.EnablePlaneAudioSmoothing);
                        break;

                    case 2: // 3. Vision & Realism
                        DrawSectionHeaderWithBundle(colLeftX, ref yL, colW, "Human Vision, Gaze & Memory",
                            VisionConfig.PreserveStanceStealthInCombat,
                            VisionConfig.EnforceHumanFOVWhenAlerted,
                            VisionConfig.EnableDynamicHumanReactionTime,
                            VisionConfig.EnableThreatSectorConcentration,
                            VisionConfig.EnableLocalContactCallouts,
                            VisionConfig.EnableLastKnownSuppression,
                            VisionConfig.EnableScopeVisionScaling,
                            VisionConfig.LimitAircraftInfantrySpotting);
                        DrawToggleButton(colLeftX, ref yL, colW, rowH, "Preserve Stance Stealth in Combat", VisionConfig.PreserveStanceStealthInCombat);
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

                        DrawSectionHeaderWithBundle(colRightX, ref yR, colW, "Player QoL & Obituary",
                            VisionConfig.EnableQuickTapSpot,
                            VisionConfig.EnableDeathScreenAttribution,
                            VisionConfig.EnableObjectiveBarFactionNames,
                            VisionConfig.EnableSpectatorHudRetention,
                            VisionConfig.EnableSpectatorBotInspector,
                            VisionConfig.EnableExtendedRoomNames,
                            VisionConfig.EnableTacticalReloadAndBipodCancel);
                        DrawToggleButton(colRightX, ref yR, colW, rowH, "Master Mod Toggle (F8)", VisionConfig.EnableMod);
                        DrawToggleButton(colRightX, ref yR, colW, rowH, "Short-Press Orders Key to Spot", VisionConfig.EnableQuickTapSpot);
                        DrawToggleButton(colRightX, ref yR, colW, rowH, "Death Screen Kill Attribution + Age", VisionConfig.EnableDeathScreenAttribution);
                        DrawToggleButton(colRightX, ref yR, colW, rowH, "Objective Bar Faction + Side Names", VisionConfig.EnableObjectiveBarFactionNames);
                        DrawToggleButton(colRightX, ref yR, colW, rowH, "Spectator Tactical HUD + 3D Markers (F Cycles)", VisionConfig.EnableSpectatorHudRetention);
                        DrawToggleButton(colRightX, ref yR, colW, rowH, "Spectator Hovered-Bot GOAP Inspector (F Cycles)", VisionConfig.EnableSpectatorBotInspector);
                        DrawToggleButton(colRightX, ref yR, colW, rowH, "64-Char Multiplayer Room Names", VisionConfig.EnableExtendedRoomNames);
                        DrawToggleButton(colRightX, ref yR, colW, rowH, "Tactical Reload & Bipod Animation Cancel", VisionConfig.EnableTacticalReloadAndBipodCancel);

                        yR += 6f;
                        float curMult = VisionConfig.UiScaleMultiplier != null
                            ? Mathf.Clamp(VisionConfig.UiScaleMultiplier.Value, 0.75f, 1.50f)
                            : 1.0f;
                        float effScale = GetEffectiveImGuiScale(out _, out _);
                        GUI.color = new Color(0.55f, 0.82f, 1.0f, 1.0f);
                        GUI.Label(new Rect(colRightX, yR, colW, 20f), $"=== 4K / High-DPI UI Scale: {Mathf.RoundToInt(curMult * 100f)}% ({effScale:F2}x) ===");
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
                }

                GUI.color = Color.white;
            }

            private static void AppendCampaignTreeEntry(CampaignTreeData camp, string prefix, int fallbackIdx)
            {
                if (camp == null || camp.battles == null || camp.battles.Count == 0)
                    return;

                string campName = camp.GetName();
                if (string.IsNullOrEmpty(campName))
                    campName = camp.campaing_id ?? $"Campaign #{fallbackIdx + 1}";
                if (!string.IsNullOrEmpty(prefix))
                    campName = $"{prefix} {campName}";

                bool isVanillaCamp = string.IsNullOrEmpty(prefix);
                List<PickerBattleEntry> bList = new List<PickerBattleEntry>(camp.battles.Count);
                for (int j = 0; j < camp.battles.Count; j++)
                {
                    ICampaignTreeBattleData bRef = camp.battles[j];
                    if (bRef == null)
                        continue;

                    string uid = bRef.GetUniqueId() ?? $"Mission_{j + 1}";
                    bool knownSaveId = !isVanillaCamp || !uid.StartsWith("sp_ME_", StringComparison.OrdinalIgnoreCase);
                    bList.Add(new PickerBattleEntry
                    {
                        DisplayName = null, // Lazily resolved per visible page to avoid upfront XML disk I/O & Missing translation spam
                        UniqueId = uid,
                        SaveDataId = knownSaveId ? uid : null,
                        PreviousSaveDataId = null,
                        IsVanilla = isVanillaCamp,
                        InitialIsUnlocked = !isVanillaCamp,
                        MetadataResolved = !isVanillaCamp,
                        BattleRef = bRef
                    });
                }

                if (bList.Count > 0)
                {
                    _mapPickerCampaigns.Add(new PickerCampaignEntry
                    {
                        CampaignName = campName,
                        TopologySorted = !isVanillaCamp,
                        Battles = bList
                    });
                }
            }

            internal static void EnsureMapPickerCatalog()
            {
                try
                {
                    if (!SavableData.IsDataLoaded())
                        SavableData.LoadDataIfNotYet();
                }
                catch { }

                int curCustomCount = 0;
                try
                {
                    var loc = CustomCampaignsList.local_campaign_data;
                    var ws = CustomCampaignsList.workshop_campaign_data;
                    if (loc != null) curCustomCount += loc.Count;
                    if (ws != null) curCustomCount += ws.Count;
                }
                catch { }

                if (_mapPickerInitialized && _mapPickerCampaigns.Count > 0 && curCustomCount == _lastCustomCampaignCount)
                    return;

                _lastCustomCampaignCount = curCustomCount;
                _mapPickerCampaigns.Clear();
                _resolvedUnitCountsCache.Clear();
                try
                {
                    var rawCampaigns = VanillaCampaignsList.vanilla_campaign_data;
                    if (rawCampaigns != null)
                    {
                        for (int i = 0; i < rawCampaigns.Length; i++)
                        {
                            AppendCampaignTreeEntry(rawCampaigns[i], null, i);
                        }
                    }

                    var localCampaigns = CustomCampaignsList.local_campaign_data;
                    if (localCampaigns != null)
                    {
                        for (int i = 0; i < localCampaigns.Count; i++)
                        {
                            AppendCampaignTreeEntry(localCampaigns[i], "[Local]", i);
                        }
                    }

                    var workshopCampaigns = CustomCampaignsList.workshop_campaign_data;
                    if (workshopCampaigns != null)
                    {
                        for (int i = 0; i < workshopCampaigns.Count; i++)
                        {
                            AppendCampaignTreeEntry(workshopCampaigns[i], "[Workshop]", i);
                        }
                    }
                }
                catch { }

                _mapPickerInitialized = true;
                _selectedCampaignIdx = Mathf.Clamp(_selectedCampaignIdx, 0, Mathf.Max(0, _mapPickerCampaigns.Count - 1));
                _selectedBattleIdx = 0;
            }

            private static PickerBattleEntry GetResolvedBattleEntry(List<PickerBattleEntry> list, int index)
            {
                PickerBattleEntry entry = list[index];
                if (!string.IsNullOrEmpty(entry.DisplayName))
                    return entry;

                string bName = null;
                string uid = entry.UniqueId ?? $"Mission_{index + 1}";
                try
                {
                    string contentPath = entry.BattleRef != null ? entry.BattleRef.GetContentPath() : null;
                    if (!string.IsNullOrEmpty(contentPath))
                    {
                        // Streaming / Mission Editor V2 (sp_ME_...) battles store localized names in <contentPath>/localization.xml
                        bName = Language.GetText("battle_name", contentPath, "localization.xml");
                    }
                    else if (!uid.StartsWith("sp_ME_", StringComparison.OrdinalIgnoreCase))
                    {
                        var dict = Language.languages;
                        if (dict != null)
                        {
                            if (dict.ContainsKey(uid))
                                bName = dict[uid];
                        }
                        else
                        {
                            // Populate Language.languages once if not yet parsed
                            bName = Language.GetBattleName(uid);
                        }
                    }
                }
                catch { }

                if (string.IsNullOrEmpty(bName))
                    bName = uid;

                entry.DisplayName = bName;
                list[index] = entry;
                return entry;
            }

            private static bool PathMatchesBattleId(string contentPath, string battleId)
            {
                if (string.IsNullOrEmpty(contentPath) || string.IsNullOrEmpty(battleId))
                    return false;

                string trimmed = contentPath.TrimEnd('/', '\\');
                int sep = Math.Max(trimmed.LastIndexOf('/'), trimmed.LastIndexOf('\\'));
                string leaf = sep >= 0 ? trimmed.Substring(sep + 1) : trimmed;
                return string.Equals(leaf, battleId, StringComparison.OrdinalIgnoreCase);
            }

            private static void GetActivePlayingBattleIdentifiers(
                out string activeBattleId,
                out string activeSaveId,
                out string activeBattleName)
            {
                activeBattleId = null;
                activeSaveId = null;
                activeBattleName = null;
                try
                {
                    MatchData md = MatchData.data;
                    IBattle lb = md != null ? md.loadedBattle : null;
                    if (lb != null)
                    {
                        activeBattleName = lb.GetBattleName();
                        BattleData lbd = lb.GetBattleData();
                        if (lbd != null)
                        {
                            activeBattleId = lbd.battle_id;
                            activeSaveId = lbd.SaveDataId;
                        }
                    }

                    if (string.IsNullOrEmpty(activeBattleId) && string.IsNullOrEmpty(activeSaveId))
                    {
                        BattleData curBd = BattleManager.GetCurrentBattleData();
                        if (curBd != null)
                        {
                            activeBattleId = curBd.battle_id;
                            activeSaveId = curBd.SaveDataId;
                        }
                    }
                }
                catch { }
            }

            private static bool IsBattleEntryActive(
                in PickerBattleEntry entry,
                string activeBattleId,
                string activeSaveId,
                string activeBattleName)
            {
                if (!string.IsNullOrEmpty(activeSaveId))
                {
                    if ((!string.IsNullOrEmpty(entry.SaveDataId) && string.Equals(entry.SaveDataId, activeSaveId, StringComparison.OrdinalIgnoreCase)) ||
                        (!string.IsNullOrEmpty(entry.UniqueId) && string.Equals(entry.UniqueId, activeSaveId, StringComparison.OrdinalIgnoreCase)))
                    {
                        return true;
                    }
                }

                if (!string.IsNullOrEmpty(activeBattleId))
                {
                    if ((!string.IsNullOrEmpty(entry.BattleId) && string.Equals(entry.BattleId, activeBattleId, StringComparison.OrdinalIgnoreCase)) ||
                        (!string.IsNullOrEmpty(entry.UniqueId) && string.Equals(entry.UniqueId, activeBattleId, StringComparison.OrdinalIgnoreCase)) ||
                        (!string.IsNullOrEmpty(entry.SaveDataId) && string.Equals(entry.SaveDataId, activeBattleId, StringComparison.OrdinalIgnoreCase)))
                    {
                        return true;
                    }
                }

                if (!string.IsNullOrEmpty(activeBattleName) &&
                    !string.IsNullOrEmpty(entry.DisplayName) &&
                    string.Equals(entry.DisplayName, activeBattleName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                return false;
            }

            internal static void TryAutoSelectActiveMissionInPicker()
            {
                _mapPickerUserPickedBattle = false;
                if (_mapPickerCampaigns.Count == 0)
                    return;

                GetActivePlayingBattleIdentifiers(out string activeBattleId, out string activeSaveId, out string activeBattleName);
                if (string.IsNullOrEmpty(activeBattleId) && string.IsNullOrEmpty(activeSaveId) && string.IsNullOrEmpty(activeBattleName))
                    return;

                const int rowsPerPage = MapPickerRowsPerPage;
                for (int c = 0; c < _mapPickerCampaigns.Count; c++)
                {
                    List<PickerBattleEntry> list = _mapPickerCampaigns[c].Battles;
                    if (list == null)
                        continue;

                    for (int b = 0; b < list.Count; b++)
                    {
                        PickerBattleEntry entry = list[b];
                        if (IsBattleEntryActive(in entry, activeBattleId, activeSaveId, activeBattleName))
                        {
                            _selectedCampaignIdx = c;
                            _selectedBattleIdx = b;
                            _campaignPage = c / rowsPerPage;
                            _battlePage = b / rowsPerPage;
                            return;
                        }
                    }
                }

                // Pass 2: match unresolved sp_ME_ campaigns by their mission content folder leaf without forcing LoadBattle().
                if (!string.IsNullOrEmpty(activeBattleId) || !string.IsNullOrEmpty(activeSaveId))
                {
                    for (int c = 0; c < _mapPickerCampaigns.Count; c++)
                    {
                        List<PickerBattleEntry> list = _mapPickerCampaigns[c].Battles;
                        if (list == null)
                            continue;

                        for (int b = 0; b < list.Count; b++)
                        {
                            PickerBattleEntry entry = list[b];
                            if (entry.MetadataResolved || entry.BattleRef == null)
                                continue;

                            string contentPath = null;
                            try { contentPath = entry.BattleRef.GetContentPath(); } catch { }
                            if (PathMatchesBattleId(contentPath, activeBattleId) || PathMatchesBattleId(contentPath, activeSaveId))
                            {
                                _selectedCampaignIdx = c;
                                _selectedBattleIdx = b;
                                _campaignPage = c / rowsPerPage;
                                _battlePage = b / rowsPerPage;
                                return;
                            }
                        }
                    }
                }
            }

            private static void SortCampaignBattlesTopologically(ref PickerCampaignEntry camp)
            {
                List<PickerBattleEntry> list = camp.Battles;
                if (list == null)
                {
                    camp.TopologySorted = true;
                    return;
                }

                int n = list.Count;
                if (n <= 1)
                {
                    if (n == 1)
                    {
                        PickerBattleEntry only = list[0];
                        only.IsFinale = false;
                        list[0] = only;
                    }
                    camp.TopologySorted = true;
                    return;
                }

                string prevSelUid = (_selectedBattleIdx >= 0 && _selectedBattleIdx < n) ? list[_selectedBattleIdx].UniqueId : null;

                var battleIdToIdx = new Dictionary<string, int>(n * 2, StringComparer.OrdinalIgnoreCase);
                var saveIdToIdx = new Dictionary<string, int>(n, StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < n; i++)
                {
                    PickerBattleEntry e = list[i];
                    if (!string.IsNullOrEmpty(e.BattleId) && !battleIdToIdx.ContainsKey(e.BattleId))
                        battleIdToIdx[e.BattleId] = i;
                    if (!string.IsNullOrEmpty(e.SaveDataId) && !saveIdToIdx.ContainsKey(e.SaveDataId))
                        saveIdToIdx[e.SaveDataId] = i;
                }
                for (int i = 0; i < n; i++)
                {
                    PickerBattleEntry e = list[i];
                    if (!string.IsNullOrEmpty(e.UniqueId) && !battleIdToIdx.ContainsKey(e.UniqueId))
                        battleIdToIdx[e.UniqueId] = i;
                }

                var nextNodes = new List<int>[n];
                var inDeg = new int[n];
                var outDeg = new int[n];
                int edgeCount = 0;

                for (int v = 0; v < n; v++)
                {
                    PickerBattleEntry e = list[v];
                    int u = -1;
                    if (!string.IsNullOrEmpty(e.PreviousBattleId))
                    {
                        if ((battleIdToIdx.TryGetValue(e.PreviousBattleId, out int tmpIdx) ||
                             saveIdToIdx.TryGetValue(e.PreviousBattleId, out tmpIdx)) &&
                            tmpIdx != v)
                        {
                            u = tmpIdx;
                        }
                    }

                    if (u < 0 && !string.IsNullOrEmpty(e.PreviousSaveDataId))
                    {
                        if ((saveIdToIdx.TryGetValue(e.PreviousSaveDataId, out int tmpIdx) ||
                             battleIdToIdx.TryGetValue(e.PreviousSaveDataId, out tmpIdx)) &&
                            tmpIdx != v)
                        {
                            u = tmpIdx;
                        }
                    }

                    if (u >= 0 && u != v)
                    {
                        if (nextNodes[u] == null)
                            nextNodes[u] = new List<int>(2);
                        if (!nextNodes[u].Contains(v))
                        {
                            nextNodes[u].Add(v);
                            inDeg[v]++;
                            outDeg[u]++;
                            edgeCount++;
                        }
                    }
                }

                var visited = new bool[n];
                var order = new List<int>(n);
                while (order.Count < n)
                {
                    int pick = -1;
                    for (int i = 0; i < n; i++)
                    {
                        if (!visited[i] && inDeg[i] == 0)
                        {
                            pick = i;
                            break;
                        }
                    }

                    if (pick < 0)
                        break;

                    visited[pick] = true;
                    order.Add(pick);
                    List<int> adj = nextNodes[pick];
                    if (adj != null)
                    {
                        for (int k = 0; k < adj.Count; k++)
                            inDeg[adj[k]]--;
                    }
                }

                for (int i = 0; i < n; i++)
                {
                    if (!visited[i])
                        order.Add(i);
                }

                bool anyVanilla = false;
                for (int i = 0; i < n; i++)
                {
                    if (list[i].IsVanilla)
                    {
                        anyVanilla = true;
                        break;
                    }
                }

                var sorted = new List<PickerBattleEntry>(n);
                for (int idxPos = 0; idxPos < order.Count; idxPos++)
                {
                    int origIdx = order[idxPos];
                    PickerBattleEntry item = list[origIdx];
                    item.IsFinale = edgeCount > 0 ? outDeg[origIdx] == 0 : (anyVanilla && idxPos == order.Count - 1);
                    sorted.Add(item);
                }

                camp.Battles = sorted;
                camp.TopologySorted = true;

                const int rowsPerPage = MapPickerRowsPerPage;
                if (!_mapPickerUserPickedBattle)
                {
                    GetActivePlayingBattleIdentifiers(out string activeBattleId, out string activeSaveId, out string activeBattleName);
                    for (int i = 0; i < sorted.Count; i++)
                    {
                        PickerBattleEntry item = sorted[i];
                        if (IsBattleEntryActive(in item, activeBattleId, activeSaveId, activeBattleName))
                        {
                            _selectedBattleIdx = i;
                            _battlePage = i / rowsPerPage;
                            return;
                        }
                    }
                }

                if (!string.IsNullOrEmpty(prevSelUid))
                {
                    for (int i = 0; i < sorted.Count; i++)
                    {
                        if (string.Equals(sorted[i].UniqueId, prevSelUid, StringComparison.OrdinalIgnoreCase))
                        {
                            _selectedBattleIdx = i;
                            _battlePage = i / rowsPerPage;
                            break;
                        }
                    }
                }
            }

            // Amortizes IBattle / BattleData metadata loading (battle_id, previousMission_id, SaveDataId, PreviousMission_SaveDataId)
            // at strictly <= 3 missions per frame while F5 is open so sp_ME_ campaigns resolve exact progression and sort cleanly.
            internal static void StepResolveSelectedCampaignBattles()
            {
                if (!_showMapPicker || !_mapPickerInitialized)
                    return;

                if (_selectedCampaignIdx < 0 || _selectedCampaignIdx >= _mapPickerCampaigns.Count)
                    return;

                PickerCampaignEntry camp = _mapPickerCampaigns[_selectedCampaignIdx];
                List<PickerBattleEntry> list = camp.Battles;
                if (list == null || list.Count == 0)
                    return;

                GetActivePlayingBattleIdentifiers(out string activeBattleId, out string activeSaveId, out string activeBattleName);
                int count = list.Count;
                int bStart = Mathf.Clamp(_battlePage * MapPickerRowsPerPage, 0, count - 1);
                int resolvedThisFrame = 0;
                long startTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                long budgetTicks = System.Diagnostics.Stopwatch.Frequency / 500; // 2.0ms per-frame budget

                for (int offset = 0; offset < count; offset++)
                {
                    int idx = (bStart + offset) % count;
                    PickerBattleEntry entry = list[idx];
                    if (entry.MetadataResolved && (entry.UnitCountsResolved || (entry.DefaultMaxAllies > 0 && entry.DefaultMaxAxis > 0)))
                        continue;

                    bool needFullMeta = !entry.MetadataResolved;
                    entry = GetResolvedBattleEntry(list, idx);
                    try
                    {
                        if (entry.BattleRef != null)
                        {
                            IBattle ib = entry.BattleRef.LoadBattle();
                            if (ib != null)
                            {
                                if (needFullMeta && (string.IsNullOrEmpty(entry.DisplayName) || entry.DisplayName == entry.UniqueId))
                                {
                                    string loadedName = ib.GetBattleName();
                                    if (!string.IsNullOrEmpty(loadedName))
                                        entry.DisplayName = loadedName;
                                }

                                BattleData bd = ib.GetBattleData();
                                if (bd != null)
                                {
                                    if (needFullMeta)
                                    {
                                        entry.BattleId = bd.battle_id;
                                        entry.PreviousBattleId = bd.previousMission_id;
                                        entry.IsVanilla = bd.IsVanilla();
                                        entry.InitialIsUnlocked = bd.IsUnlocked();
                                        entry.SaveDataId = !string.IsNullOrEmpty(bd.SaveDataId) ? bd.SaveDataId : entry.UniqueId;
                                        entry.PreviousSaveDataId = !string.IsNullOrEmpty(bd.previousMission_id) ? bd.PreviousMission_SaveDataId : null;
                                    }
                                    PopulateEntryUnitCounts(ref entry, bd);
                                }
                            }
                        }
                    }
                    catch { }

                    if (string.IsNullOrEmpty(entry.SaveDataId))
                        entry.SaveDataId = entry.UniqueId;

                    entry.MetadataResolved = true;
                    entry.UnitCountsResolved = true;
                    list[idx] = entry;

                    if (!_mapPickerUserPickedBattle && IsBattleEntryActive(in entry, activeBattleId, activeSaveId, activeBattleName))
                    {
                        _selectedBattleIdx = idx;
                        _battlePage = idx / MapPickerRowsPerPage;
                    }

                    if (++resolvedThisFrame >= 3 || (System.Diagnostics.Stopwatch.GetTimestamp() - startTicks) >= budgetTicks)
                        break;
                }

                if (!camp.TopologySorted)
                {
                    bool allResolved = true;
                    for (int k = 0; k < list.Count; k++)
                    {
                        if (!list[k].MetadataResolved)
                        {
                            allResolved = false;
                            break;
                        }
                    }

                    if (allResolved)
                    {
                        SortCampaignBattlesTopologically(ref camp);
                        _mapPickerCampaigns[_selectedCampaignIdx] = camp;
                    }
                }
            }

            private static void EvaluateBattleUnlockAndStars(
                PickerBattleEntry bEntry,
                List<PickerBattleEntry> campaignBattles,
                GameProgresses prog,
                bool isCurrentlyPlaying,
                out bool isCompleted,
                out bool wonBothFactions,
                out bool isUnlocked)
            {
                isCompleted = false;
                wonBothFactions = false;

                if (prog != null && !string.IsNullOrEmpty(bEntry.SaveDataId))
                {
                    try
                    {
                        var comp = prog.GetCompletedLevelData(bEntry.SaveDataId);
                        if (comp != null && comp.IsCompleted())
                        {
                            isCompleted = true;
                            wonBothFactions = comp.WonOnBothFactions;
                        }
                    }
                    catch { }
                }

                if (!bEntry.IsVanilla)
                {
                    isUnlocked = true;
                }
                else if (!bEntry.MetadataResolved)
                {
                    // Mask unresolved vanilla rows during the amortized scan window unless completed or actively playing
                    isUnlocked = isCompleted || isCurrentlyPlaying;
                }
                else
                {
                    bool hasPrereq = !string.IsNullOrEmpty(bEntry.PreviousBattleId) || !string.IsNullOrEmpty(bEntry.PreviousSaveDataId);
                    bool prevDone = false;
                    if (hasPrereq && prog != null)
                    {
                        if (!string.IsNullOrEmpty(bEntry.PreviousSaveDataId))
                        {
                            try { prevDone = prog.IsLevelCompleted(bEntry.PreviousSaveDataId); } catch { }
                        }

                        if (!prevDone && !string.IsNullOrEmpty(bEntry.PreviousBattleId) && campaignBattles != null)
                        {
                            for (int k = 0; k < campaignBattles.Count; k++)
                            {
                                PickerBattleEntry prevEntry = campaignBattles[k];
                                if ((!string.IsNullOrEmpty(prevEntry.BattleId) && string.Equals(prevEntry.BattleId, bEntry.PreviousBattleId, StringComparison.OrdinalIgnoreCase)) ||
                                    (!string.IsNullOrEmpty(prevEntry.UniqueId) && string.Equals(prevEntry.UniqueId, bEntry.PreviousBattleId, StringComparison.OrdinalIgnoreCase)))
                                {
                                    if (!string.IsNullOrEmpty(prevEntry.SaveDataId))
                                    {
                                        try { prevDone = prog.IsLevelCompleted(prevEntry.SaveDataId); } catch { }
                                    }
                                    break;
                                }
                            }
                        }
                    }

                    isUnlocked = isCompleted ||
                                 isCurrentlyPlaying ||
                                 bEntry.InitialIsUnlocked ||
                                 !hasPrereq ||
                                 prevDone;
                }
            }

            private static readonly Dictionary<IntPtr, (bool isMp, int allies, int axis)> _resolvedUnitCountsCache
                = new Dictionary<IntPtr, (bool isMp, int allies, int axis)>(64);

            private static void PopulateEntryUnitCounts(ref PickerBattleEntry entry, BattleData bd)
            {
                if (bd == null)
                    return;

                bool isMp = MatchData.data != null && MatchData.data.isMultiplayer;
                int allies;
                int axis;
                try
                {
                    bd.GetMaxUnitsForThisSystemProportional(isMp, out short sysAllies, out short sysAxis);
                    allies = sysAllies > 0 ? sysAllies : Mathf.Max(1, bd.maxAllies);
                    axis = sysAxis > 0 ? sysAxis : Mathf.Max(1, bd.maxAxis);
                }
                catch
                {
                    allies = Mathf.Max(1, bd.maxAllies);
                    axis = Mathf.Max(1, bd.maxAxis);
                }

                entry.DefaultMaxAllies = allies;
                entry.DefaultMaxAxis = axis;
                if (entry.BattleRef != null && entry.BattleRef.Pointer != IntPtr.Zero)
                    _resolvedUnitCountsCache[entry.BattleRef.Pointer] = (isMp, allies, axis);
            }

            private static void EnsureBattleUnitCountsResolved(
                ref PickerBattleEntry entry,
                List<PickerBattleEntry> parentList = null,
                int parentIdx = -1)
            {
                if (entry.UnitCountsResolved || (entry.DefaultMaxAllies > 0 && entry.DefaultMaxAxis > 0))
                    return;

                if (entry.BattleRef == null)
                    return;

                bool isMp = MatchData.data != null && MatchData.data.isMultiplayer;
                IntPtr refPtr = entry.BattleRef.Pointer;
                if (refPtr != IntPtr.Zero &&
                    _resolvedUnitCountsCache.TryGetValue(refPtr, out var cached) &&
                    cached.isMp == isMp &&
                    cached.allies > 0 &&
                    cached.axis > 0)
                {
                    entry.DefaultMaxAllies = cached.allies;
                    entry.DefaultMaxAxis = cached.axis;
                    entry.UnitCountsResolved = true;
                    if (parentList != null && parentIdx >= 0 && parentIdx < parentList.Count)
                        parentList[parentIdx] = entry;
                    return;
                }

                try
                {
                    IBattle ib = entry.BattleRef.LoadBattle();
                    if (ib != null)
                    {
                        BattleData bd = ib.GetBattleData();
                        if (bd != null)
                        {
                            if (string.IsNullOrEmpty(entry.BattleId))
                                entry.BattleId = bd.battle_id;
                            if (string.IsNullOrEmpty(entry.SaveDataId))
                                entry.SaveDataId = !string.IsNullOrEmpty(bd.SaveDataId) ? bd.SaveDataId : entry.UniqueId;
                            PopulateEntryUnitCounts(ref entry, bd);
                        }
                    }
                }
                catch { }

                entry.UnitCountsResolved = true;
                if (parentList != null && parentIdx >= 0 && parentIdx < parentList.Count)
                    parentList[parentIdx] = entry;
            }

            private static void EnsureBattleRefUnitCountsResolved(
                ICampaignTreeBattleData battleRef,
                MatchBattle fallbackMb,
                out int baseAllies,
                out int baseAxis)
            {
                bool isMp = MatchData.data != null && MatchData.data.isMultiplayer;
                if (battleRef != null && battleRef.Pointer != IntPtr.Zero)
                {
                    if (_resolvedUnitCountsCache.TryGetValue(battleRef.Pointer, out var cached) &&
                        cached.isMp == isMp &&
                        cached.allies > 0 &&
                        cached.axis > 0)
                    {
                        baseAllies = cached.allies;
                        baseAxis = cached.axis;
                        return;
                    }

                    try
                    {
                        IBattle ib = battleRef.LoadBattle();
                        if (ib != null)
                        {
                            BattleData bd = ib.GetBattleData();
                            if (bd != null)
                            {
                                PickerBattleEntry tmp = new PickerBattleEntry { BattleRef = battleRef };
                                PopulateEntryUnitCounts(ref tmp, bd);
                                if (tmp.DefaultMaxAllies > 0 && tmp.DefaultMaxAxis > 0)
                                {
                                    baseAllies = tmp.DefaultMaxAllies;
                                    baseAxis = tmp.DefaultMaxAxis;
                                    return;
                                }
                            }
                        }
                    }
                    catch { }
                }

                if (fallbackMb != null)
                {
                    if (fallbackMb.proportionalAlliesRefValue > 0 && fallbackMb.proportionalAxisRefValue > 0)
                    {
                        baseAllies = fallbackMb.proportionalAlliesRefValue;
                        baseAxis = fallbackMb.proportionalAxisRefValue;
                        return;
                    }
                    if (fallbackMb.maxAllies > 0 && fallbackMb.maxAxis > 0)
                    {
                        baseAllies = fallbackMb.maxAllies;
                        baseAxis = fallbackMb.maxAxis;
                        return;
                    }
                }

                baseAllies = 32;
                baseAxis = 32;
            }

            private static void ComputeScaledUnitCounts(
                int baseAllies,
                int baseAxis,
                float botScale,
                out short scaledAllies,
                out short scaledAxis)
            {
                float clampedScale = Mathf.Clamp(botScale, 0.25f, 3.00f);
                scaledAllies = (short)Mathf.Clamp(Mathf.RoundToInt(Mathf.Max(1, baseAllies) * clampedScale), 1, 500);
                scaledAxis = (short)Mathf.Clamp(Mathf.RoundToInt(Mathf.Max(1, baseAxis) * clampedScale), 1, 500);
            }

            private static MatchBattle CreateConfiguredMatchBattle(
                ref PickerBattleEntry entry,
                MatchBattle curMb,
                float botScale)
            {
                EnsureBattleUnitCountsResolved(ref entry);
                int baseAllies = entry.DefaultMaxAllies > 0
                    ? entry.DefaultMaxAllies
                    : (curMb != null && curMb.maxAllies > 0 ? curMb.maxAllies : 32);
                int baseAxis = entry.DefaultMaxAxis > 0
                    ? entry.DefaultMaxAxis
                    : (curMb != null && curMb.maxAxis > 0 ? curMb.maxAxis : 32);

                ComputeScaledUnitCounts(baseAllies, baseAxis, botScale, out short scaledAllies, out short scaledAxis);

                MatchBattle newMb = new MatchBattle(entry.BattleRef);
                newMb.maxAllies = scaledAllies;
                newMb.maxAxis = scaledAxis;
                newMb.proportionalAlliesRefValue = (short)Mathf.Clamp(baseAllies, 1, 500);
                newMb.proportionalAxisRefValue = (short)Mathf.Clamp(baseAxis, 1, 500);
                newMb.proportionalSliderRefValue = Mathf.Clamp(botScale, 0.25f, 3.00f);
                if (curMb != null)
                {
                    newMb.ticketsMultiplier = curMb.ticketsMultiplier;
                    newMb.allowAchievements = curMb.allowAchievements;
                }
                return newMb;
            }

            private static int FindPlaylistIndex(in PickerBattleEntry entry)
            {
                IntPtr entryPtr = entry.BattleRef != null ? entry.BattleRef.Pointer : IntPtr.Zero;
                for (int i = 0; i < _mapPickerPlaylist.Count; i++)
                {
                    PickerBattleEntry existing = _mapPickerPlaylist[i].Battle;
                    if (!string.IsNullOrEmpty(entry.UniqueId) &&
                        string.Equals(existing.UniqueId, entry.UniqueId, StringComparison.OrdinalIgnoreCase))
                        return i;
                    if (entryPtr != IntPtr.Zero && existing.BattleRef != null && existing.BattleRef.Pointer == entryPtr)
                        return i;
                }
                return -1;
            }

            private static bool TogglePlaylistEntry(PickerBattleEntry entry, string visibleName)
            {
                int idx = FindPlaylistIndex(in entry);
                if (idx >= 0)
                {
                    _mapPickerPlaylist.RemoveAt(idx);
                    return false;
                }

                EnsureBattleUnitCountsResolved(ref entry);
                _mapPickerPlaylist.Add(new QueuedPickerEntry
                {
                    Battle = entry,
                    VisibleName = string.IsNullOrEmpty(visibleName) ? entry.DisplayName : visibleName
                });
                return true;
            }

            private static List<QueuedPickerEntry> BuildCampaignFromIndexItems(
                List<PickerBattleEntry> curBattles,
                int startIdx,
                GameProgresses prog,
                string activeBattleId,
                string activeSaveId,
                string activeBattleName)
            {
                var items = new List<QueuedPickerEntry>();
                if (curBattles == null || startIdx < 0 || startIdx >= curBattles.Count)
                    return items;

                for (int i = startIdx; i < curBattles.Count; i++)
                {
                    PickerBattleEntry entry = GetResolvedBattleEntry(curBattles, i);
                    if (entry.BattleRef == null)
                        continue;

                    bool active = IsBattleEntryActive(in entry, activeBattleId, activeSaveId, activeBattleName);
                    EvaluateBattleUnlockAndStars(entry, curBattles, prog, active, out _, out _, out bool unlocked);
                    bool masked = !unlocked && !_revealMapPickerSpoilers;
                    string visName = masked ? $"Mission #{i + 1} (Locked)" : entry.DisplayName;
                    items.Add(new QueuedPickerEntry
                    {
                        Battle = entry,
                        VisibleName = visName
                    });
                }

                return items;
            }

            private static int ApplyBotScaleToQueuedBattles(float botScale, out short nextAllies, out short nextAxis)
            {
                nextAllies = 0;
                nextAxis = 0;
                MatchData md = MatchData.data;
                if (md == null || md.battles == null)
                    return 0;

                int startIdx = Mathf.Max(0, md.currentBattleId + 1);
                if (startIdx >= md.battles.Count)
                    return 0;

                int updated = 0;
                float clampedScale = Mathf.Clamp(botScale, 0.25f, 3.00f);
                for (int i = startIdx; i < md.battles.Count; i++)
                {
                    MatchBattle queuedMb = md.battles[i];
                    if (queuedMb == null)
                        continue;

                    EnsureBattleRefUnitCountsResolved(queuedMb.battle_reference, queuedMb, out int baseAllies, out int baseAxis);
                    ComputeScaledUnitCounts(baseAllies, baseAxis, clampedScale, out short scaledAllies, out short scaledAxis);
                    queuedMb.maxAllies = scaledAllies;
                    queuedMb.maxAxis = scaledAxis;
                    queuedMb.proportionalAlliesRefValue = (short)Mathf.Clamp(baseAllies, 1, 500);
                    queuedMb.proportionalAxisRefValue = (short)Mathf.Clamp(baseAxis, 1, 500);
                    queuedMb.proportionalSliderRefValue = clampedScale;
                    if (updated == 0)
                    {
                        nextAllies = scaledAllies;
                        nextAxis = scaledAxis;
                    }
                    updated++;
                }

                if (updated > 0 && Photon.Pun.PhotonNetwork.InRoom && Photon.Pun.PhotonNetwork.IsMasterClient)
                {
                    try { md.UpdateNetworkMatchInfo(); } catch { }
                }

                _mapPickerBotScaleDirty = false;
                return updated;
            }

            private static bool DrawBotScaleSliderBar(Rect trackRect, ref float scale)
            {
                bool changed = false;
                GUI.color = new Color(0.14f, 0.16f, 0.21f, 1.0f);
                GUI.Box(trackRect, "");

                float t = Mathf.InverseLerp(0.25f, 3.00f, scale);
                float fillW = Mathf.Max(6f, trackRect.width * t);
                GUI.color = Mathf.Abs(scale - 1.0f) < 0.01f
                    ? new Color(0.45f, 0.90f, 0.52f, 0.92f)
                    : new Color(1.0f, 0.80f, 0.30f, 0.95f);
                GUI.Box(new Rect(trackRect.x + 1f, trackRect.y + 2f, Mathf.Max(4f, fillW - 2f), trackRect.height - 4f), "");

                GUI.color = Color.white;
                GUI.Label(new Rect(trackRect.x + 6f, trackRect.y + 1f, trackRect.width - 12f, trackRect.height - 2f),
                    $"Slide / Click: {Mathf.RoundToInt(scale * 100f)}% (25%-300%)");

                Event ev = Event.current;
                if (ev != null && (ev.type == EventType.MouseDown || ev.type == EventType.MouseDrag) && ev.button == 0)
                {
                    if (trackRect.Contains(ev.mousePosition))
                    {
                        float norm = Mathf.Clamp01((ev.mousePosition.x - trackRect.x) / Mathf.Max(1f, trackRect.width));
                        float rawScale = Mathf.Lerp(0.25f, 3.00f, norm);
                        float snapped = Mathf.Clamp(Mathf.Round(rawScale * 20f) / 20f, 0.25f, 3.00f);
                        if (!Mathf.Approximately(snapped, scale))
                        {
                            scale = snapped;
                            changed = true;
                        }
                        ev.Use();
                    }
                }

                return changed;
            }

            private static void DrawMapPickerWindow(float virtualW, float virtualH)
            {
                EnsureMapPickerCatalog();

                GameProgresses prog = null;
                try { prog = SavableData.Progresses; } catch { }

                float panelW = 860f;
                float panelH = 640f;
                float panelX = (virtualW - panelW) * 0.5f;
                float panelY = (virtualH - panelH) * 0.5f;

                GUI.color = new Color(0.05f, 0.06f, 0.08f, 0.95f);
                GUI.Box(new Rect(panelX, panelY, panelW, panelH), "");

                KeyCode mpKey = VisionConfig.MapPickerHotkey != null ? VisionConfig.MapPickerHotkey.Value : KeyCode.F5;
                GUI.color = new Color(1.0f, 0.88f, 0.35f, 1.0f);
                GUI.Label(new Rect(panelX + 20f, panelY + 10f, panelW - 360f, 22f),
                    $"Easy Red 2: Battle Hardened  |  Campaign, Playlist & Mission Picker ({mpKey})");

                GUI.color = _revealMapPickerSpoilers
                    ? new Color(1.0f, 0.86f, 0.36f, 1.0f)
                    : new Color(0.68f, 0.78f, 0.90f, 0.95f);
                if (GUI.Button(new Rect(panelX + panelW - 332f, panelY + 8f, 114f, 24f), _revealMapPickerSpoilers ? "Hide ??? [ON]" : "Show ???"))
                {
                    _revealMapPickerSpoilers = !_revealMapPickerSpoilers;
                }

                GUI.color = new Color(0.75f, 0.90f, 1.0f, 1.0f);
                if (GUI.Button(new Rect(panelX + panelW - 210f, panelY + 8f, 190f, 24f), "Back to Feature Menu (F6)"))
                {
                    _showMapPicker = false;
                    _showConfigMenu = true;
                }

                bool canControlMatch = !Photon.Pun.PhotonNetwork.InRoom || Photon.Pun.PhotonNetwork.IsMasterClient;
                GUI.color = canControlMatch
                    ? new Color(0.75f, 0.88f, 0.78f, 1.0f)
                    : new Color(1.0f, 0.65f, 0.55f, 1.0f);
                string roleNote = canControlMatch
                    ? "Select missions, toggle [+] to build a multi-map playlist, pick 'Campaign From Here', and tune Next Bot Scale:"
                    : "Note: In Multiplayer Co-op, only the Room Host (MasterClient) can switch or queue missions.";
                GUI.Label(new Rect(panelX + 20f, panelY + 34f, panelW - 40f, 20f), roleNote);

                float colCampX = panelX + 20f;
                float colCampW = 304f;
                float colBatX = panelX + 338f;
                float colBatW = 502f;
                float listTopY = panelY + 56f;
                int rowsPerPage = MapPickerRowsPerPage;
                float rowH = 24f;

                // Left Column: Campaigns
                int campCount = _mapPickerCampaigns.Count;
                int totalCampPages = Mathf.Max(1, (campCount + rowsPerPage - 1) / rowsPerPage);
                _campaignPage = Mathf.Clamp(_campaignPage, 0, totalCampPages - 1);

                GUI.color = new Color(0.55f, 0.82f, 1.0f, 1.0f);
                GUI.Label(new Rect(colCampX, listTopY, colCampW - 110f, 20f), $"=== Campaigns ({campCount}) ===");
                if (totalCampPages > 1)
                {
                    GUI.color = new Color(0.85f, 0.90f, 1.0f, 1.0f);
                    if (GUI.Button(new Rect(colCampX + colCampW - 104f, listTopY - 1f, 48f, 20f), "< Prev"))
                        _campaignPage = (_campaignPage - 1 + totalCampPages) % totalCampPages;
                    if (GUI.Button(new Rect(colCampX + colCampW - 52f, listTopY - 1f, 52f, 20f), "Next >"))
                        _campaignPage = (_campaignPage + 1) % totalCampPages;
                }

                float yC = listTopY + 22f;
                int cStart = _campaignPage * rowsPerPage;
                int cEnd = Mathf.Min(campCount, cStart + rowsPerPage);
                for (int i = cStart; i < cEnd; i++)
                {
                    bool selected = i == _selectedCampaignIdx;
                    GUI.color = selected
                        ? new Color(1.0f, 0.86f, 0.32f, 1.0f)
                        : new Color(0.82f, 0.86f, 0.92f, 0.92f);

                    string prefix = selected ? ">> " : "   ";
                    PickerCampaignEntry cEntry = _mapPickerCampaigns[i];
                    int doneCount = 0;
                    bool hasAnySaveId = false;
                    if (prog != null && cEntry.Battles != null)
                    {
                        for (int k = 0; k < cEntry.Battles.Count; k++)
                        {
                            string sid = cEntry.Battles[k].SaveDataId;
                            if (string.IsNullOrEmpty(sid))
                                continue;
                            hasAnySaveId = true;
                            try
                            {
                                if (prog.IsLevelCompleted(sid))
                                    doneCount++;
                            }
                            catch { }
                        }
                    }
                    string countText = (prog != null && hasAnySaveId)
                        ? $"[{doneCount}/{cEntry.Battles.Count} ★]"
                        : $"({cEntry.Battles.Count})";

                    if (GUI.Button(new Rect(colCampX, yC, colCampW, rowH - 2f), $"{prefix}{cEntry.CampaignName} {countText}"))
                    {
                        _selectedCampaignIdx = i;
                        _selectedBattleIdx = 0;
                        _battlePage = 0;
                        _mapPickerUserPickedBattle = false;
                    }
                    yC += rowH;
                }

                // Right Column: Missions in Selected Campaign
                PickerCampaignEntry selectedCamp = (_selectedCampaignIdx >= 0 && _selectedCampaignIdx < campCount)
                    ? _mapPickerCampaigns[_selectedCampaignIdx]
                    : default;
                List<PickerBattleEntry> curBattles = selectedCamp.Battles;
                int batCount = curBattles != null ? curBattles.Count : 0;
                int totalBatPages = Mathf.Max(1, (batCount + rowsPerPage - 1) / rowsPerPage);
                _battlePage = Mathf.Clamp(_battlePage, 0, totalBatPages - 1);
                _selectedBattleIdx = Mathf.Clamp(_selectedBattleIdx, 0, Mathf.Max(0, batCount - 1));

                GUI.color = new Color(0.55f, 0.82f, 1.0f, 1.0f);
                GUI.Label(new Rect(colBatX, listTopY, colBatW - 110f, 20f), $"=== Missions ({batCount})  [★ Beat | [+] Add to Playlist] ===");
                if (totalBatPages > 1)
                {
                    GUI.color = new Color(0.85f, 0.90f, 1.0f, 1.0f);
                    if (GUI.Button(new Rect(colBatX + colBatW - 104f, listTopY - 1f, 48f, 20f), "< Prev"))
                        _battlePage = (_battlePage - 1 + totalBatPages) % totalBatPages;
                    if (GUI.Button(new Rect(colBatX + colBatW - 52f, listTopY - 1f, 52f, 20f), "Next >"))
                        _battlePage = (_battlePage + 1) % totalBatPages;
                }

                GetActivePlayingBattleIdentifiers(out string activeBattleId, out string activeSaveId, out string activeBattleName);
                int firstNextUpIdx = -1;
                if (curBattles != null && selectedCamp.TopologySorted)
                {
                    for (int k = 0; k < curBattles.Count; k++)
                    {
                        PickerBattleEntry ke = curBattles[k];
                        bool kActive = IsBattleEntryActive(in ke, activeBattleId, activeSaveId, activeBattleName);
                        EvaluateBattleUnlockAndStars(ke, curBattles, prog, kActive, out bool kDone, out _, out bool kUnlocked);
                        if (kUnlocked && !kDone)
                        {
                            firstNextUpIdx = k;
                            break;
                        }
                    }
                }

                float yB = listTopY + 22f;
                float batBtnW = colBatW - 46f;
                float addBtnX = colBatX + batBtnW + 4f;
                float addBtnW = 42f;
                if (curBattles != null)
                {
                    int bStart = _battlePage * rowsPerPage;
                    int bEnd = Mathf.Min(batCount, bStart + rowsPerPage);
                    for (int j = bStart; j < bEnd; j++)
                    {
                        bool selected = j == _selectedBattleIdx;
                        PickerBattleEntry bEntry = GetResolvedBattleEntry(curBattles, j);
                        bool isNowPlaying = IsBattleEntryActive(in bEntry, activeBattleId, activeSaveId, activeBattleName);
                        EvaluateBattleUnlockAndStars(bEntry, curBattles, prog, isNowPlaying, out bool isCompleted, out bool wonBoth, out bool isUnlocked);

                        bool maskedAsUnknown = !isUnlocked && !_revealMapPickerSpoilers;
                        if (selected)
                        {
                            GUI.color = maskedAsUnknown
                                ? new Color(0.78f, 0.72f, 0.52f, 0.96f)
                                : new Color(0.45f, 0.95f, 0.52f, 1.0f);
                        }
                        else if (isNowPlaying)
                        {
                            GUI.color = new Color(0.42f, 0.92f, 1.0f, 1.0f);
                        }
                        else if (j == firstNextUpIdx && !maskedAsUnknown)
                        {
                            GUI.color = new Color(0.66f, 0.96f, 0.70f, 1.0f);
                        }
                        else if (isCompleted)
                        {
                            GUI.color = new Color(0.98f, 0.90f, 0.58f, 0.96f);
                        }
                        else if (isUnlocked)
                        {
                            GUI.color = new Color(0.86f, 0.92f, 0.98f, 0.96f);
                        }
                        else if (_revealMapPickerSpoilers)
                        {
                            GUI.color = new Color(0.78f, 0.72f, 0.64f, 0.85f);
                        }
                        else
                        {
                            GUI.color = new Color(0.55f, 0.59f, 0.65f, 0.78f);
                        }

                        string prefix = selected ? "[SELECTED] " : $"#{j + 1}. ";
                        string badge = isCompleted ? (wonBoth ? "★★ " : "★ ") : (!isUnlocked && _revealMapPickerSpoilers ? "[Locked] " : "");
                        string statusTag = "";
                        if (!maskedAsUnknown)
                        {
                            if (bEntry.IsFinale) statusTag += " [FINALE]";
                            if (isNowPlaying) statusTag += " [NOW PLAYING]";
                            else if (j == firstNextUpIdx) statusTag += " [NEXT UP]";
                        }
                        string labelTitle = maskedAsUnknown ? "??? (Locked)" : $"{badge}{bEntry.DisplayName}{statusTag}";

                        if (GUI.Button(new Rect(colBatX, yB, batBtnW, rowH - 2f), $"{prefix}{labelTitle}"))
                        {
                            _selectedBattleIdx = j;
                            _mapPickerUserPickedBattle = true;
                        }

                        int plOrder = FindPlaylistIndex(in bEntry);
                        GUI.color = plOrder >= 0
                            ? new Color(0.48f, 0.95f, 0.58f, 1.0f)
                            : new Color(0.72f, 0.82f, 0.96f, 0.92f);
                        string plBtnLabel = plOrder >= 0 ? $"#{plOrder + 1}" : "+";
                        if (GUI.Button(new Rect(addBtnX, yB, addBtnW, rowH - 2f), plBtnLabel) && bEntry.BattleRef != null)
                        {
                            string plVisibleName = maskedAsUnknown ? $"Mission #{j + 1} (Locked)" : bEntry.DisplayName;
                            TogglePlaylistEntry(bEntry, plVisibleName);
                        }

                        yB += rowH;
                    }
                }

                // Bottom Selection Summary, Playlist Strip, Bot Scale Slider & Launch/Queue Actions
                float summaryY = panelY + 444f;
                string selSummary = "No mission selected.";
                string chosenVisibleName = null;
                PickerBattleEntry chosenBattle = default;
                bool hasChosen = false;
                if (curBattles != null && _selectedBattleIdx >= 0 && _selectedBattleIdx < curBattles.Count)
                {
                    chosenBattle = GetResolvedBattleEntry(curBattles, _selectedBattleIdx);
                    EnsureBattleUnitCountsResolved(ref chosenBattle, curBattles, _selectedBattleIdx);
                    hasChosen = chosenBattle.BattleRef != null;
                    bool selNowPlaying = IsBattleEntryActive(in chosenBattle, activeBattleId, activeSaveId, activeBattleName);
                    EvaluateBattleUnlockAndStars(chosenBattle, curBattles, prog, selNowPlaying, out bool selDone, out bool selBoth, out bool selUnlocked);
                    bool selMasked = !selUnlocked && !_revealMapPickerSpoilers;
                    string selBadge = selDone ? (selBoth ? "★★ " : "★ ") : "";
                    string selStatusTag = "";
                    if (!selMasked)
                    {
                        if (chosenBattle.IsFinale) selStatusTag += " [FINALE]";
                        if (selNowPlaying) selStatusTag += " [NOW PLAYING]";
                        else if (_selectedBattleIdx == firstNextUpIdx) selStatusTag += " [NEXT UP]";
                    }
                    chosenVisibleName = selMasked ? "??? (Locked)" : chosenBattle.DisplayName;
                    string campTitle = _mapPickerCampaigns[_selectedCampaignIdx].CampaignName;
                    MatchData activeMd = MatchData.data;
                    int playlistCount = activeMd != null && activeMd.battles != null ? activeMd.battles.Count : 0;
                    int curIdx = activeMd != null ? activeMd.currentBattleId + 1 : 1;
                    string modeLabel = activeMd != null ? activeMd.playType.ToString() : "Single";
                    selSummary = $"Selected: [{campTitle}]  {selBadge}{chosenVisibleName}{selStatusTag}   |   Match Queue: {curIdx}/{playlistCount} ({modeLabel})";
                }

                GUI.color = new Color(0.95f, 0.92f, 0.78f, 1.0f);
                GUI.Label(new Rect(panelX + 20f, summaryY, panelW - 40f, 20f), selSummary);

                // Playlist Preview Strip
                float playlistY = panelY + 467f;
                StringBuilder plSb = new StringBuilder(128);
                plSb.Append($"Multi-Map Playlist ({_mapPickerPlaylist.Count}): ");
                if (_mapPickerPlaylist.Count == 0)
                {
                    plSb.Append("(click [+] on missions above to build an ordered multi-map list)");
                }
                else
                {
                    int showMax = Math.Min(_mapPickerPlaylist.Count, 5);
                    for (int k = 0; k < showMax; k++)
                    {
                        if (k > 0) plSb.Append(" -> ");
                        plSb.Append($"{k + 1}.{_mapPickerPlaylist[k].VisibleName}");
                    }
                    if (_mapPickerPlaylist.Count > showMax)
                        plSb.Append($" ... (+{_mapPickerPlaylist.Count - showMax} more)");
                }
                GUI.color = _mapPickerPlaylist.Count > 0
                    ? new Color(0.65f, 0.96f, 0.74f, 1.0f)
                    : new Color(0.72f, 0.76f, 0.84f, 0.92f);
                GUI.Label(new Rect(panelX + 20f, playlistY + 1f, panelW - 144f, 20f), plSb.ToString());

                GUI.color = _mapPickerPlaylist.Count > 0
                    ? new Color(1.0f, 0.76f, 0.68f, 1.0f)
                    : new Color(0.55f, 0.55f, 0.55f, 0.65f);
                if (GUI.Button(new Rect(panelX + panelW - 116f, playlistY, 96f, 22f), "Clear List") && _mapPickerPlaylist.Count > 0)
                {
                    _mapPickerPlaylist.Clear();
                }

                // Next-Map Bot Scale Slider Bar
                float sliderY = panelY + 494f;
                int botPct = Mathf.RoundToInt(_mapPickerBotScale * 100f);
                GUI.color = new Color(0.85f, 0.94f, 1.0f, 1.0f);
                GUI.Label(new Rect(panelX + 20f, sliderY + 2f, 138f, 20f), $"Next Bot Scale: {botPct}%");

                float sx = panelX + 162f;
                if (DrawBotScaleSliderBar(new Rect(sx, sliderY, 186f, 23f), ref _mapPickerBotScale))
                    _mapPickerBotScaleDirty = true;
                sx += 192f;

                GUI.color = new Color(0.82f, 0.90f, 1.0f, 1.0f);
                if (GUI.Button(new Rect(sx, sliderY, 44f, 23f), "-10%"))
                {
                    _mapPickerBotScale = Mathf.Clamp(Mathf.Round((_mapPickerBotScale - 0.10f) * 20f) / 20f, 0.25f, 3.00f);
                    _mapPickerBotScaleDirty = true;
                }
                sx += 48f;
                if (GUI.Button(new Rect(sx, sliderY, 44f, 23f), "+10%"))
                {
                    _mapPickerBotScale = Mathf.Clamp(Mathf.Round((_mapPickerBotScale + 0.10f) * 20f) / 20f, 0.25f, 3.00f);
                    _mapPickerBotScaleDirty = true;
                }
                sx += 50f;

                float[] presets = { 0.5f, 1.0f, 1.5f, 2.0f, 3.0f };
                string[] presetLabels = { "50%", "100%", "150%", "200%", "300%" };
                for (int p = 0; p < presets.Length; p++)
                {
                    float pv = presets[p];
                    float bw = p == 0 ? 44f : 50f;
                    bool isCur = Mathf.Abs(_mapPickerBotScale - pv) < 0.025f;
                    GUI.color = isCur ? new Color(0.55f, 0.95f, 0.62f, 1.0f) : new Color(0.80f, 0.86f, 0.94f, 1.0f);
                    if (GUI.Button(new Rect(sx, sliderY, bw, 23f), presetLabels[p]))
                    {
                        _mapPickerBotScale = pv;
                        _mapPickerBotScaleDirty = true;
                    }
                    sx += bw + 4f;
                }

                MatchData curMd = MatchData.data;
                int upcomingCount = (curMd != null && curMd.battles != null)
                    ? Mathf.Max(0, curMd.battles.Count - (curMd.currentBattleId + 1))
                    : 0;
                GUI.color = (upcomingCount > 0 && canControlMatch)
                    ? (_mapPickerBotScaleDirty ? new Color(1.0f, 0.84f, 0.34f, 1.0f) : new Color(0.62f, 0.90f, 1.0f, 1.0f))
                    : new Color(0.55f, 0.55f, 0.55f, 0.65f);
                string applyQueueText = upcomingCount > 0
                    ? (_mapPickerBotScaleDirty ? $"Apply Queue ({upcomingCount})*" : $"Apply Queue ({upcomingCount})")
                    : "Apply Queue (0)";
                if (GUI.Button(new Rect(panelX + panelW - 144f, sliderY, 124f, 23f), applyQueueText) && upcomingCount > 0 && canControlMatch)
                {
                    int updated = ApplyBotScaleToQueuedBattles(_mapPickerBotScale, out short nextA, out short nextX);
                    ShowToast($"[Battle Hardened] Applied {Mathf.RoundToInt(_mapPickerBotScale * 100f)}% Bot Scale to {updated} queued mission(s) (Next: {nextA} Allies vs {nextX} Axis).");
                }

                // Preview line showing selected mission & upcoming queued mission scaled unit counts
                float previewY = panelY + 523f;
                string unitPreviewText;
                if (hasChosen && chosenBattle.DefaultMaxAllies > 0 && chosenBattle.DefaultMaxAxis > 0)
                {
                    ComputeScaledUnitCounts(chosenBattle.DefaultMaxAllies, chosenBattle.DefaultMaxAxis, _mapPickerBotScale, out short selScaledA, out short selScaledX);
                    unitPreviewText = $"Selected Unit Cap: {chosenBattle.DefaultMaxAllies} Allies / {chosenBattle.DefaultMaxAxis} Axis  ->  {selScaledA} Allies / {selScaledX} Axis";
                }
                else
                {
                    unitPreviewText = $"Bot Scale Multiplier: {botPct}% (applies per mission's default Allies/Axis unit budget)";
                }
                if (upcomingCount > 0 && curMd != null && curMd.battles != null)
                {
                    MatchBattle nextMb = curMd.battles[curMd.currentBattleId + 1];
                    if (nextMb != null)
                    {
                        EnsureBattleRefUnitCountsResolved(nextMb.battle_reference, nextMb, out int nBaseA, out int nBaseX);
                        ComputeScaledUnitCounts(nBaseA, nBaseX, _mapPickerBotScale, out short nScaledA, out short nScaledX);
                        unitPreviewText += $"   |   Next Map Queue: currently {nextMb.maxAllies}/{nextMb.maxAxis} (preview {nScaledA}/{nScaledX})";
                    }
                }
                GUI.color = new Color(0.76f, 0.88f, 0.96f, 0.95f);
                GUI.Label(new Rect(panelX + 20f, previewY, panelW - 40f, 20f), unitPreviewText);

                int campFromHereCount = (curBattles != null && _selectedBattleIdx >= 0 && _selectedBattleIdx < curBattles.Count)
                    ? (curBattles.Count - _selectedBattleIdx)
                    : 0;
                bool hasCampFromHere = hasChosen && campFromHereCount > 0;
                bool hasPlaylist = _mapPickerPlaylist.Count > 0;

                // Bottom Row 1: Start / Replace Active Playlist Now
                float row1Y = panelY + 549f;
                GUI.color = (hasChosen && canControlMatch)
                    ? new Color(0.45f, 0.95f, 0.52f, 1.0f)
                    : new Color(0.55f, 0.55f, 0.55f, 0.70f);
                if (GUI.Button(new Rect(panelX + 20f, row1Y, 202f, 28f), "Play Selected Now") && hasChosen && canControlMatch)
                {
                    var single = new List<QueuedPickerEntry>(1)
                    {
                        new QueuedPickerEntry
                        {
                            Battle = chosenBattle,
                            VisibleName = chosenVisibleName ?? chosenBattle.DisplayName
                        }
                    };
                    ExecuteMapPickerLaunchOrQueue(single, switchNow: true, isCampaignFromHere: false, single[0].VisibleName);
                }

                GUI.color = (hasCampFromHere && canControlMatch)
                    ? new Color(0.48f, 0.92f, 0.68f, 1.0f)
                    : new Color(0.55f, 0.55f, 0.55f, 0.70f);
                if (GUI.Button(new Rect(panelX + 230f, row1Y, 220f, 28f), $"Campaign From Here ({campFromHereCount})") && hasCampFromHere && canControlMatch)
                {
                    List<QueuedPickerEntry> campSeq = BuildCampaignFromIndexItems(
                        curBattles, _selectedBattleIdx, prog, activeBattleId, activeSaveId, activeBattleName);
                    if (campSeq.Count > 0)
                    {
                        string campTitle = _mapPickerCampaigns[_selectedCampaignIdx].CampaignName;
                        ExecuteMapPickerLaunchOrQueue(campSeq, switchNow: true, isCampaignFromHere: true, $"{campTitle}: {campSeq[0].VisibleName} (+{campSeq.Count - 1})");
                    }
                }

                GUI.color = (hasPlaylist && canControlMatch)
                    ? new Color(0.48f, 0.88f, 0.98f, 1.0f)
                    : new Color(0.55f, 0.55f, 0.55f, 0.70f);
                if (GUI.Button(new Rect(panelX + 458f, row1Y, 220f, 28f), $"Play Playlist Now ({_mapPickerPlaylist.Count})") && hasPlaylist && canControlMatch)
                {
                    var copy = new List<QueuedPickerEntry>(_mapPickerPlaylist);
                    string summary = copy.Count == 1 ? copy[0].VisibleName : $"{copy[0].VisibleName} (+{copy.Count - 1} maps)";
                    ExecuteMapPickerLaunchOrQueue(copy, switchNow: true, isCampaignFromHere: false, summary);
                }

                GUI.color = new Color(1.0f, 0.80f, 0.80f, 1.0f);
                if (GUI.Button(new Rect(panelX + 686f, row1Y, 154f, 28f), $"Close Picker ({mpKey})"))
                {
                    _showMapPicker = false;
                }

                // Bottom Row 2: Queue After Current Mission
                float row2Y = panelY + 584f;
                GUI.color = (hasChosen && canControlMatch)
                    ? new Color(0.58f, 0.84f, 1.0f, 1.0f)
                    : new Color(0.55f, 0.55f, 0.55f, 0.70f);
                if (GUI.Button(new Rect(panelX + 20f, row2Y, 202f, 28f), "Queue Selected After Current") && hasChosen && canControlMatch)
                {
                    var single = new List<QueuedPickerEntry>(1)
                    {
                        new QueuedPickerEntry
                        {
                            Battle = chosenBattle,
                            VisibleName = chosenVisibleName ?? chosenBattle.DisplayName
                        }
                    };
                    ExecuteMapPickerLaunchOrQueue(single, switchNow: false, isCampaignFromHere: false, single[0].VisibleName);
                }

                GUI.color = (hasCampFromHere && canControlMatch)
                    ? new Color(0.66f, 0.88f, 0.98f, 1.0f)
                    : new Color(0.55f, 0.55f, 0.55f, 0.70f);
                if (GUI.Button(new Rect(panelX + 230f, row2Y, 220f, 28f), $"Queue Campaign From Here ({campFromHereCount})") && hasCampFromHere && canControlMatch)
                {
                    List<QueuedPickerEntry> campSeq = BuildCampaignFromIndexItems(
                        curBattles, _selectedBattleIdx, prog, activeBattleId, activeSaveId, activeBattleName);
                    if (campSeq.Count > 0)
                    {
                        string campTitle = _mapPickerCampaigns[_selectedCampaignIdx].CampaignName;
                        ExecuteMapPickerLaunchOrQueue(campSeq, switchNow: false, isCampaignFromHere: true, $"{campTitle}: {campSeq.Count} missions");
                    }
                }

                GUI.color = (hasPlaylist && canControlMatch)
                    ? new Color(0.72f, 0.86f, 1.0f, 1.0f)
                    : new Color(0.55f, 0.55f, 0.55f, 0.70f);
                if (GUI.Button(new Rect(panelX + 458f, row2Y, 220f, 28f), $"Queue Playlist ({_mapPickerPlaylist.Count})") && hasPlaylist && canControlMatch)
                {
                    var copy = new List<QueuedPickerEntry>(_mapPickerPlaylist);
                    ExecuteMapPickerLaunchOrQueue(copy, switchNow: false, isCampaignFromHere: false, $"{copy.Count} playlist missions");
                }

                GUI.color = (upcomingCount > 0 && canControlMatch)
                    ? new Color(1.0f, 0.74f, 0.66f, 1.0f)
                    : new Color(0.55f, 0.55f, 0.55f, 0.65f);
                if (GUI.Button(new Rect(panelX + 686f, row2Y, 154f, 28f), $"Clear Upcoming ({upcomingCount})") && upcomingCount > 0 && canControlMatch && curMd != null && curMd.battles != null)
                {
                    int removeStart = curMd.currentBattleId + 1;
                    int removeCount = curMd.battles.Count - removeStart;
                    if (removeStart >= 0 && removeCount > 0)
                    {
                        curMd.battles.RemoveRange(removeStart, removeCount);
                        if (Photon.Pun.PhotonNetwork.InRoom && Photon.Pun.PhotonNetwork.IsMasterClient)
                        {
                            try { curMd.UpdateNetworkMatchInfo(); } catch { }
                        }
                        ShowToast($"[Battle Hardened] Cleared {removeCount} upcoming mission(s) from match queue.");
                    }
                }

                GUI.color = Color.white;
            }

            private static void ExecuteMapPickerLaunchOrQueue(
                List<QueuedPickerEntry> items,
                bool switchNow,
                bool isCampaignFromHere,
                string labelSummary)
            {
                if (items == null || items.Count == 0)
                    return;

                try
                {
                    if (switchNow && _mpSwitchStage != 0)
                    {
                        ShowToast($"[Battle Hardened] Already switching Multiplayer Room to '{_mpSwitchDisplayName}'...");
                        return;
                    }

                    MatchData md = MatchData.data;
                    if (md == null)
                    {
                        ShowToast("[Battle Hardened] MatchData is not active yet.");
                        return;
                    }

                    if (Photon.Pun.PhotonNetwork.InRoom && !Photon.Pun.PhotonNetwork.IsMasterClient)
                    {
                        ShowToast("[Battle Hardened] Only the Multiplayer Host can change or queue missions.");
                        return;
                    }

                    MatchBattle curMb = md.CurrentBattle;
                    if (md.battles == null)
                        md.ResetSelectedBattles();

                    var configured = new List<MatchBattle>(items.Count);
                    for (int i = 0; i < items.Count; i++)
                    {
                        QueuedPickerEntry item = items[i];
                        if (item.Battle.BattleRef == null)
                            continue;
                        PickerBattleEntry copyEntry = item.Battle;
                        configured.Add(CreateConfiguredMatchBattle(ref copyEntry, curMb, _mapPickerBotScale));
                    }

                    if (configured.Count == 0)
                        return;

                    if (!switchNow)
                    {
                        int firstNewSlot = md.battles.Count + 1;
                        for (int i = 0; i < configured.Count; i++)
                            md.battles.Add(configured[i]);

                        if (isCampaignFromHere)
                            md.playType = MatchData.MissionsPlayType.FullCampaign;
                        else if (md.battles.Count > 1 && md.playType == MatchData.MissionsPlayType.Single)
                            md.playType = MatchData.MissionsPlayType.MultiSelectOrdered;

                        if (Photon.Pun.PhotonNetwork.InRoom && Photon.Pun.PhotonNetwork.IsMasterClient)
                        {
                            md.UpdateNetworkMatchInfo();
                        }

                        MatchBattle firstAdded = configured[0];
                        int pct = Mathf.RoundToInt(_mapPickerBotScale * 100f);
                        ShowToast($"[Battle Hardened] Queued {configured.Count} mission(s) ({labelSummary}) at slot #{firstNewSlot} (Bot Scale {pct}%: {firstAdded.maxAllies} Allies vs {firstAdded.maxAxis} Axis).");
                        return;
                    }

                    md.battles.Clear();
                    for (int i = 0; i < configured.Count; i++)
                        md.battles.Add(configured[i]);

                    if (isCampaignFromHere)
                        md.playType = MatchData.MissionsPlayType.FullCampaign;
                    else if (configured.Count > 1)
                        md.playType = MatchData.MissionsPlayType.MultiSelectOrdered;
                    else
                        md.playType = MatchData.MissionsPlayType.Single;

                    _showMapPicker = false;
                    _showConfigMenu = false;
                    md.currentBattleId = 0;
                    md.loadedBattle = null;
                    BattleManager.SetPlayerFaction("");

                    if (Photon.Pun.PhotonNetwork.InRoom)
                    {
                        BattleLoader bl = BattleLoader.instance;
                        if (bl != null)
                        {
                            try { bl.StopVoting(); } catch { }
                        }

                        bool hasRemoteClients = Photon.Pun.PhotonNetwork.CurrentRoom != null &&
                                                Photon.Pun.PhotonNetwork.CurrentRoom.PlayerCount > 1;
                        if (bl != null && bl.photonView != null && hasRemoteClients)
                        {
                            var rawBytes = ResourcesManager.SerializeToByteArray(md.Cast<Il2CppSystem.Object>());
                            var waitArr = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<float>(1);
                            waitArr[0] = 3.0f;
                            var rpcArgs = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Il2CppSystem.Object>(2);
                            rpcArgs[0] = rawBytes.Cast<Il2CppSystem.Object>();
                            rpcArgs[1] = waitArr.Cast<Il2CppSystem.Array>().GetValue(0);
                            bl.photonView.RPC("ConnectToNextMatch", Photon.Pun.RpcTarget.Others, rpcArgs);
                            Photon.Pun.PhotonNetwork.SendAllOutgoingCommands();
                        }

                        // Replicate vanilla EndBattleGUI.VictoryAnimation (0x180499286):
                        // NEVER call bl.LoadNextMpMatch() on the Host (which sets MatchData.actorType = client!).
                        // SetMultiplayerRoomSettings() keeps actorType = master (so TryConnect() calls CreateRoom instead of JoinRoom)
                        // and refreshes RoomOptions custom properties for the new battle.
                        md.SetMultiplayerRoomSettings();
                        MatchData.actorType = MatchData.RoomActorType.master;
                        _mpSwitchDisplayName = labelSummary;

                        if (hasRemoteClients)
                        {
                            _mpSwitchStage = 1;
                            _mpSwitchStageDeadline = Time.unscaledTime + 1.0f;
                        }
                        else
                        {
                            BeginHostLeaveRoomForMapSwitch(bl);
                        }

                        ShowToast($"[Battle Hardened] Switching Multiplayer Room to '{labelSummary}' ({configured.Count} map(s), {Mathf.RoundToInt(_mapPickerBotScale * 100f)}% bots)...");
                        return;
                    }

                    md.StartBattleLoading();
                    ShowToast($"[Battle Hardened] Loading '{labelSummary}' ({configured.Count} map(s), {Mathf.RoundToInt(_mapPickerBotScale * 100f)}% bots)...");
                }
                catch (Exception ex)
                {
                    _mpSwitchStage = 0;
                    ShowToast($"[Battle Hardened] Map Picker error: {ex.Message}");
                }
            }

            private static void DrawSectionHeaderWithBundle(float x, ref float y, float width, string title, params ConfigEntry<bool>[] bundleEntries)
            {
                GUI.color = new Color(0.55f, 0.82f, 1.0f, 1.0f);
                GUI.Label(new Rect(x, y, width - 96f, 20f), $"=== {title} ===");

                GUI.color = new Color(0.45f, 0.95f, 0.52f, 0.92f);
                if (GUI.Button(new Rect(x + width - 92f, y - 1f, 44f, 19f), "ALL"))
                {
                    VisionConfig.SetBundleEntries(true, bundleEntries);
                    HitchProfiler.RefreshConfigState();
                    BotActionTrace.RefreshConfigState();
                    VeteranAndTacticsPatches.RefreshLocalPlayerFirstPersonShadow();
                    Plugin.Instance.Config.Save();
                }

                GUI.color = new Color(0.95f, 0.45f, 0.45f, 0.92f);
                if (GUI.Button(new Rect(x + width - 45f, y - 1f, 45f, 19f), "OFF"))
                {
                    VisionConfig.SetBundleEntries(false, bundleEntries);
                    HitchProfiler.RefreshConfigState();
                    BotActionTrace.RefreshConfigState();
                    VeteranAndTacticsPatches.RefreshLocalPlayerFirstPersonShadow();
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

                string status = val ? "[ON]  " : "[OFF] ";
                if (GUI.Button(new Rect(x, y, width, height - 2f), status + label))
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
                    else if (entry == VisionConfig.EnableFirstPersonShadowFix)
                    {
                        VeteranAndTacticsPatches.RefreshLocalPlayerFirstPersonShadow();
                    }
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
                _stationaryWatchCache.Clear();
                _coverEyeBlockCache.Clear();
                _barbedWireColliderCache.Clear();
                _botBarbedWireStuckCache.Clear();
                _rotateFireFrameCache.Clear();
                _nextStandCrouchLofCheckTime.Clear();
                _fireAndMovementStates.Clear();
                _infantryTargetReaction.Clear();
                VisionPatches.ClearVisionCaches();
                AiCore.ClearSpatialCaches();
                ClearBallisticsCaches(forceClear: true);
                ClearDoorCaches(forceFull: true);
                ClearCombinedArmsCaches();
                ClearGoapCaches();
                ClearConquestQuartermasterState();
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

            if (forceFull || slice == 0)
            {
                AiCore.PruneDict(_records, _reusableAlivePtrs);
                AiCore.PruneDict(_stanceDropHoldFireUntil, _reusableAlivePtrs);
                AiCore.PruneDict(_nextStanceDropAllowed, _reusableAlivePtrs);
                AiCore.PruneDict(_combatStanceHoldUntil, _reusableAlivePtrs);
                AiCore.PruneDict(_combatCrouchLockUntil, _reusableAlivePtrs);
                AiCore.PruneDict(_noProneUntil, _reusableAlivePtrs);
                AiCore.PruneDict(_nextWallCheckTime, _reusableAlivePtrs);
                AiCore.PruneDict(_wallBlockedUntil, _reusableAlivePtrs);
            }

            if (forceFull || slice == 1)
            {
                AiCore.PruneDict(_peekRestoreTime, _reusableAlivePtrs);
                AiCore.PruneDict(_nextPeekAllowedTime, _reusableAlivePtrs);
                AiCore.PruneDict(_nextFFCheckTime, _reusableAlivePtrs);
                AiCore.PruneDict(_ffBlockedUntil, _reusableAlivePtrs);
                AiCore.PruneDict(_nextAntiStackTime, _reusableAlivePtrs);
                AiCore.PruneDict(_nextVaultCheckTime, _reusableAlivePtrs);
                AiCore.PruneDict(_rotateFireFrameCache, _reusableAlivePtrs);
                AiCore.PruneDict(_nextStandCrouchLofCheckTime, _reusableAlivePtrs);
                AiCore.PruneDict(_fireAndMovementStates, _reusableAlivePtrs);
                AiCore.PruneDict(_infantryTargetReaction, _reusableAlivePtrs);
            }

            if (forceFull || slice == 2)
            {
                AiCore.PruneDict(_nextLiberateCheckTime, _reusableAlivePtrs);
                AiCore.PruneDict(_liberateMoveStartTime, _reusableAlivePtrs);
                AiCore.PruneDict(_liberateMoveCooldownUntil, _reusableAlivePtrs);
                AiCore.PruneDict(_bleedCoverDeadline, _reusableAlivePtrs);
                AiCore.PruneDict(_nextBipodSeekTime, _reusableAlivePtrs);
                AiCore.PruneDict(_nextBipodDeployCheckTime, _reusableAlivePtrs);
                AiCore.PruneDict(_bipodStandUntil, _reusableAlivePtrs);
                AiCore.PruneDict(_nextWallSideCheckTime, _reusableAlivePtrs);
            }

            if (forceFull || slice == 3)
            {
                AiCore.PruneDict(_nextWallSidestepTime, _reusableAlivePtrs);
                AiCore.PruneDict(_nextTankDisciplineCountTime, _reusableAlivePtrs);
                AiCore.PruneDict(_poseLockUntil, _reusableAlivePtrs);
                AiCore.PruneDict(_nextBayonetCheckTime, _reusableAlivePtrs);
                AiCore.PruneDict(_nextMeleeAttemptTime, _reusableAlivePtrs);
                AiCore.PruneDict(_nextMeleeStepTime, _reusableAlivePtrs);
                AiCore.PruneDict(_meleeTargetSlots, _reusableAlivePtrs);
                AiCore.PruneDict(_stationaryWatchCache, _reusableAlivePtrs);
                AiCore.PruneDict(_coverEyeBlockCache, _reusableAlivePtrs);
            }

            if (forceFull || slice == 4)
            {
                AiCore.PruneDict(_nextPromotionCheckTime, _reusableAlivePtrs);
                AiCore.PruneDict(_vehicleAimCache, _reusableAlivePtrs);
                AiCore.PruneDict(_threatSectorCache, _reusableAlivePtrs);
                AiCore.PruneDict(_watchWindowCache, _reusableAlivePtrs);
                AiCore.PruneDict(_localCalloutCache, _reusableAlivePtrs);
                AiCore.PruneDict(_nextSpotterCalloutTime, _reusableAlivePtrs);
                AiCore.PruneDict(_lastKnownTargets, _reusableAlivePtrs);
                AiCore.PruneDict(_nextSuppressiveGrenadeTime, _reusableAlivePtrs);
            }

            if (forceFull || slice == 5)
            {
                AiCore.PruneDict(_nextSuppressiveBurstCountTime, _reusableAlivePtrs);
                AiCore.PruneDict(_nextIndoorCeilingCountTime, _reusableAlivePtrs);
                AiCore.PruneDict(_nextBoundingHoldCountTime, _reusableAlivePtrs);
                AiCore.PruneDict(_nextBridgeAssistCountTime, _reusableAlivePtrs);
                AiCore.PruneDict(_nextSurrenderCheckTime, _reusableAlivePtrs);
                AiCore.PruneDict(_nextLocalCoverSeekTime, _reusableAlivePtrs);
                AiCore.PruneDict(_localCoverProtectedUntil, _reusableAlivePtrs);
                AiCore.PruneDict(_nextBotDoorCheckTime, _reusableAlivePtrs);
                AiCore.PruneDict(_assignedEmplacementUntil, _reusableAlivePtrs);
                AiCore.PruneDict(_assignedEmplacementVehicle, _reusableAlivePtrs);
                AiCore.PruneDict(_nextFriendlyStepAsideTime, _reusableAlivePtrs);
            }

            if (forceFull || slice == 6)
            {
                AiCore.PruneDict(_botGoapPlans, _reusableAlivePtrs);
                AiCore.PruneDict(_botScavengeCaches, _reusableAlivePtrs);
                AiCore.PruneDict(_nextWeaponUpgradeAllowedTime, _reusableAlivePtrs);
                AiCore.PruneDict(_nextBuddyBandageRefillTime, _reusableAlivePtrs);
                AiCore.PruneDict(_nextBuddyAmmoResupplyTime, _reusableAlivePtrs);
                AiCore.PruneDict(_nextBotBoxOrCrateResupplyTime, _reusableAlivePtrs);
                AiCore.PruneDict(_nextSmokeGrenadeTime, _reusableAlivePtrs);
                AiCore.PruneDict(_nextSoldierVoiceTime, _reusableAlivePtrs);
                AiCore.PruneDict(_nextFlankCoverAssignTime, _reusableAlivePtrs);
                AiCore.PruneDict(_medicHealStartTime, _reusableAlivePtrs);
                AiCore.PruneDict(_medicHealTargetPtr, _reusableAlivePtrs);
                AiCore.PruneDict(_casualtyDragStates, _reusableAlivePtrs);
                AiCore.PruneDict(_footStuckStates, _reusableAlivePtrs);
                AiCore.PruneDict(_medicPatientClaims, _reusableAlivePtrs);
                AiCore.PruneDict(_cornerPieCache, _reusableAlivePtrs);
                AiCore.PruneDict(_nextCornerPieCountTime, _reusableAlivePtrs);
                AiCore.PruneDict(_lastDamageByVictim, _reusableAlivePtrs);
                AiCore.PruneDict(_nextIndividualFallbackTime, _reusableAlivePtrs);
                AiCore.PruneDict(_individualFallbackUntil, _reusableAlivePtrs);
                AiCore.PruneDict(_individualFallbackDest, _reusableAlivePtrs);
                AiCore.PruneSet(_bayonetCheckedSoldiers, _reusableAlivePtrs);
                AiCore.PruneSet(_isTurningToWatchSector, _reusableAlivePtrs);
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
                AiCore.PruneDict(_tankUnstickFrameCache, _reusableVehiclePtrs);
                AiCore.PruneDict(_nextLocalDriverRoadkillScanTime, _reusableVehiclePtrs);
                AiCore.PruneDict(_cachedVehicleSolidColliders, _reusableVehiclePtrs);
                AiCore.PruneDict(_cachedVehicleExtents, _reusableVehiclePtrs);
                AiCore.PruneDict(_remoteVehicleMotion, _reusableVehiclePtrs);
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
                if (_planeDogfightBreakOff.Count > 128)
                {
                    _planeDogfightBreakOff.Clear();
                    _remotePlaneSpawnSeenTime.Clear();
                    _nextRemotePlaneGroundCheckTime.Clear();
                    _planeHotStartWindowUntil.Clear();
                }
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
        // Squad Roster UI Veteran & Personality Tags (SquadSoldierLabel)
        // =====================================================================
        [HarmonyPatch(typeof(SquadSoldierLabel), nameof(SquadSoldierLabel.RefreshVisual))]
        public static class SquadSoldierLabel_RefreshVisual_Patch
        {
            public static void Postfix(SquadSoldierLabel __instance)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value || __instance == null)
                        return;

                    bool showVet = VisionConfig.EnableVeteranSystem.Value && VisionConfig.ShowVeteranSquadRosterTags.Value;
                    bool showPers = VisionConfig.EnableRolePersonalities.Value && VisionConfig.ShowPersonalityInSquadRoster.Value;
                    if (!showVet && !showPers)
                        return;

                    Soldier s = __instance.assignedSoldier;
                    Text nameLabel = __instance.name_surname;
                    if (s == null || nameLabel == null)
                        return;

                    string curText = nameLabel.text ?? "";
                    int bracketIdx = curText.IndexOf(" [", StringComparison.Ordinal);
                    string baseText = bracketIdx >= 0 ? curText.Substring(0, bracketIdx) : curText;

                    string tag = BuildSoldierBracketTag(s, showVet, showPers, true);
                    nameLabel.text = baseText + tag;

                    int tier = showVet ? GetVeteranTier(s) : 0;
                    if (tier == 1)
                        nameLabel.color = new Color(0.95f, 0.82f, 0.55f, 1f);
                    else if (tier == 2)
                        nameLabel.color = new Color(0.85f, 0.95f, 1.00f, 1f);
                    else if (tier >= 3)
                        nameLabel.color = new Color(1.00f, 0.86f, 0.22f, 1f);
                }
                catch { }
            }
        }
    }
}
