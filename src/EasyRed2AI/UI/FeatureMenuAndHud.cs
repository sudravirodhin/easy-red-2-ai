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
            public string SaveDataId;
            public string PreviousSaveDataId;
            public bool IsVanilla;
            public bool InitialIsUnlocked;
            public bool MetadataResolved;
            public ICampaignTreeBattleData BattleRef;
        }

        private struct PickerCampaignEntry
        {
            public string CampaignName;
            public List<PickerBattleEntry> Battles;
        }

        private static readonly List<PickerCampaignEntry> _mapPickerCampaigns = new List<PickerCampaignEntry>(24);
        private static bool _mapPickerInitialized;
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
                    UpdatePendingMpMapSwitch();
                    CleanupExpiredFiringRecords();
                    VeteranAndTacticsPatches.RenderVeteran3DMarkersAndCleanup();
                    VeteranAndTacticsPatches.UpdateMacroObjectiveAndEmplacementDirector();
                    VeteranAndTacticsPatches.UpdateConquestQuartermasterIfNeeded();
                    VeteranAndTacticsPatches.UpdateFriendlyPlayerCrosshairYield();
                    VeteranAndTacticsPatches.UpdateFriendlyVehicleCollisionRestores();
                    VeteranAndTacticsPatches.UpdateMultiplayerBattleStatSync();
                    VeteranAndTacticsPatches.UpdateQuickTapSpot(_showConfigMenu || _showMapPicker);
                    VeteranAndTacticsPatches.UpdateLocalPlayerFirstPersonShadowRig();
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
                                $"GOAP:{VeteranAndTacticsPatches.GoapPlansExecutedCount} (Anvil:{VeteranAndTacticsPatches.TeamNetAnvilHammerCount} Flank:{VeteranAndTacticsPatches.GoapPinAndFlankCount} Charge:{VeteranAndTacticsPatches.SquadChargesOrderedCount} Balk:{VeteranAndTacticsPatches.SquadLeaderBalksCount}/{VeteranAndTacticsPatches.IndividualSelfPreservationBalksCount} Covert:{VeteranAndTacticsPatches.SquadCovertOrdersCount} Follow:{VeteranAndTacticsPatches.SquadFollowOrdersCount} Retreat:{VeteranAndTacticsPatches.TacticalRetreatsCount} Smoke:{VeteranAndTacticsPatches.GoapSmokeScreensCount} RadioCut:{VeteranAndTacticsPatches.RadioNetCutoffsCount}) | WepUp:{VeteranAndTacticsPatches.RoleWeaponUpgradesCount} | BuddyResup:{VeteranAndTacticsPatches.BuddyResuppliesCount} | RadioLoot:{VeteranAndTacticsPatches.FieldRadiosInheritedCount} | Helmets:{VeteranAndTacticsPatches.HelmetsRecoveredCount} | Dragged:{VeteranAndTacticsPatches.CasualtiesDraggedCount}\n" +
                                $"Drops:{VeteranAndTacticsPatches.SelfPreservationDrops} | Peeks:{VeteranAndTacticsPatches.AutoPeeksTriggered} | BndBlk:{VeteranAndTacticsPatches.UnsafeBandagesBlockedCount} | IndoorCrouch:{VeteranAndTacticsPatches.IndoorPronePreventedCount} | Pies:{VeteranAndTacticsPatches.CornerPiesExecutedCount} | TankCov:{VeteranAndTacticsPatches.TankHullCoverUsesCount} | Bounds:{VeteranAndTacticsPatches.BoundingOverwatchHoldsCount} | Doors:{VeteranAndTacticsPatches.DoorsStrategicallyClosedCount} | Bipods:{VeteranAndTacticsPatches.BipodsDeployed} | WallBlk:{VeteranAndTacticsPatches.WallsBlocked} | FragBlk:{VeteranAndTacticsPatches.UselessTankFragsBlockedCount} | Melee:{VeteranAndTacticsPatches.MeleeAttacksTriggered} (Sup:{VeteranAndTacticsPatches.CloseGunfireMeleeSuppressedCount}) | Vault:{VeteranAndTacticsPatches.WallsVaulted} (Rej:{VeteranAndTacticsPatches.SlopedVaultsRejectedCount}) | Surr:{VeteranAndTacticsPatches.ShellShockSurrendersCount}/Lib:{VeteranAndTacticsPatches.ComradesLiberated}\n" +
                                $"Garrisons:{VeteranAndTacticsPatches.MacroGarrisonsAssignedCount} (Pivots:{VeteranAndTacticsPatches.MacroObjectivePivotsCount}) | ConqBuy:{VeteranAndTacticsPatches.ConquestPurchasesCount} | Emplace:{VeteranAndTacticsPatches.StaticGunsStaffedCount} | APC:{VeteranAndTacticsPatches.SmartApcDismountsCount} | TankHold:{VeteranAndTacticsPatches.TankStandoffHoldsCount} | TankSpread:{VeteranAndTacticsPatches.ArmoredDispersionAdjustmentsCount} | SmartAmmo:{VeteranAndTacticsPatches.SmartAmmoSwitchesCount} | Unsticks:{VeteranAndTacticsPatches.TankRockUnsticksCount} | Repairs:{VeteranAndTacticsPatches.ShelteredRepairsCount} | RoadkillSaved:{VeteranAndTacticsPatches.FriendlyRoadkillsPreventedCount} (ClientRK:{VeteranAndTacticsPatches.RemoteClientRoadkillsAssistedCount})\n" +
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
                            $"GOAP:{VeteranAndTacticsPatches.GoapPlansExecutedCount} (Anvil:{VeteranAndTacticsPatches.TeamNetAnvilHammerCount} PinFlank:{VeteranAndTacticsPatches.GoapPinAndFlankCount} Charge:{VeteranAndTacticsPatches.SquadChargesOrderedCount} Balk:{VeteranAndTacticsPatches.SquadLeaderBalksCount}/{VeteranAndTacticsPatches.IndividualSelfPreservationBalksCount} Covert:{VeteranAndTacticsPatches.SquadCovertOrdersCount} Follow:{VeteranAndTacticsPatches.SquadFollowOrdersCount} Retreat:{VeteranAndTacticsPatches.TacticalRetreatsCount} Smoke:{VeteranAndTacticsPatches.GoapSmokeScreensCount} WepUp:{VeteranAndTacticsPatches.RoleWeaponUpgradesCount} BuddyResup:{VeteranAndTacticsPatches.BuddyResuppliesCount} RadioLoot:{VeteranAndTacticsPatches.FieldRadiosInheritedCount} Helmets:{VeteranAndTacticsPatches.HelmetsRecoveredCount} Dragged:{VeteranAndTacticsPatches.CasualtiesDraggedCount} RadioCut:{VeteranAndTacticsPatches.RadioNetCutoffsCount}) | " +
                            $"Drops:{VeteranAndTacticsPatches.SelfPreservationDrops} | Peeks:{VeteranAndTacticsPatches.AutoPeeksTriggered} | BndBlk:{VeteranAndTacticsPatches.UnsafeBandagesBlockedCount} | IndoorCrouch:{VeteranAndTacticsPatches.IndoorPronePreventedCount} | Pies:{VeteranAndTacticsPatches.CornerPiesExecutedCount} | TankCov:{VeteranAndTacticsPatches.TankHullCoverUsesCount} | " +
                            $"Callouts:{VeteranAndTacticsPatches.LocalCalloutsSharedCount} (KillVoice:{VeteranAndTacticsPatches.BotKillCalloutsCount}) | Supp:{VeteranAndTacticsPatches.SuppressiveEventsCount} | " +
                            $"Bounds:{VeteranAndTacticsPatches.BoundingOverwatchHoldsCount} | Garrisons:{VeteranAndTacticsPatches.MacroGarrisonsAssignedCount} (Pivots:{VeteranAndTacticsPatches.MacroObjectivePivotsCount}) | ConqBuy:{VeteranAndTacticsPatches.ConquestPurchasesCount} | " +
                            $"Emplace:{VeteranAndTacticsPatches.StaticGunsStaffedCount} | APC:{VeteranAndTacticsPatches.SmartApcDismountsCount} | " +
                            $"TankHold:{VeteranAndTacticsPatches.TankStandoffHoldsCount} | TankSpread:{VeteranAndTacticsPatches.ArmoredDispersionAdjustmentsCount} | SmartAmmo:{VeteranAndTacticsPatches.SmartAmmoSwitchesCount} | Unsticks:{VeteranAndTacticsPatches.TankRockUnsticksCount} | MedRedirect:{VeteranAndTacticsPatches.MedicTriageRedirectsCount} | RoadkillSaved:{VeteranAndTacticsPatches.FriendlyRoadkillsPreventedCount} (ClientRK:{VeteranAndTacticsPatches.RemoteClientRoadkillsAssistedCount}) | CombArms:{VeteranAndTacticsPatches.CombinedArmsPriorityAssistsCount} (Intercom:{VeteranAndTacticsPatches.TankIntercomHandoffsCount} Kamikaze:{VeteranAndTacticsPatches.KamikazeDivesTriggeredCount}) | " +
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
                            VisionConfig.EnableComradeLiberation);
                        DrawToggleButton(colLeftX, ref yL, colW, rowH, "3-Tier Tactical Retreats & Regroup", VisionConfig.EnableTacticalRetreatsAndRegroup);
                        DrawToggleButton(colLeftX, ref yL, colW, rowH, "Buddy Resupply (Medic Bandage & Ammo Share)", VisionConfig.EnableBuddyResupply);
                        DrawToggleButton(colLeftX, ref yL, colW, rowH, "Field Radio Inheritance & Helmet Gifting", VisionConfig.EnableEmergentWearableScavenge);
                        DrawToggleButton(colLeftX, ref yL, colW, rowH, "Buddy Casualty Dragging to Cover / Medic", VisionConfig.EnableBuddyCasualtyDragging);
                        DrawToggleButton(colLeftX, ref yL, colW, rowH, "Shell-Shocked & Skittish Surrender", VisionConfig.EnableShellShockSurrender);
                        DrawToggleButton(colLeftX, ref yL, colW, rowH, "Surrendered Comrade Liberation", VisionConfig.EnableComradeLiberation);

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
                            VisionConfig.EnableExtendedRoomNames);
                        DrawToggleButton(colRightX, ref yR, colW, rowH, "Master Mod Toggle (F8)", VisionConfig.EnableMod);
                        DrawToggleButton(colRightX, ref yR, colW, rowH, "Short-Press Orders Key to Spot", VisionConfig.EnableQuickTapSpot);
                        DrawToggleButton(colRightX, ref yR, colW, rowH, "Death Screen Kill Attribution + Age", VisionConfig.EnableDeathScreenAttribution);
                        DrawToggleButton(colRightX, ref yR, colW, rowH, "Objective Bar Faction + Side Names", VisionConfig.EnableObjectiveBarFactionNames);
                        DrawToggleButton(colRightX, ref yR, colW, rowH, "Spectator Tactical HUD + 3D Markers (F Cycles)", VisionConfig.EnableSpectatorHudRetention);
                        DrawToggleButton(colRightX, ref yR, colW, rowH, "Spectator Hovered-Bot GOAP Inspector (F Cycles)", VisionConfig.EnableSpectatorBotInspector);
                        DrawToggleButton(colRightX, ref yR, colW, rowH, "64-Char Multiplayer Room Names", VisionConfig.EnableExtendedRoomNames);

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
                        InitialIsUnlocked = !isVanillaCamp || j == 0,
                        MetadataResolved = !isVanillaCamp,
                        BattleRef = bRef
                    });
                }

                if (bList.Count > 0)
                {
                    _mapPickerCampaigns.Add(new PickerCampaignEntry
                    {
                        CampaignName = campName,
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

            // Amortizes IBattle / BattleData metadata loading (SaveDataId + PreviousMission_SaveDataId) at strictly <= 1 mission
            // per frame while F5 is open so sp_ME_ streaming campaigns resolve exact gamedata.er2 unlock status with zero hitches.
            internal static void StepResolveSelectedCampaignBattles()
            {
                if (!_showMapPicker || !_mapPickerInitialized)
                    return;

                if (_selectedCampaignIdx < 0 || _selectedCampaignIdx >= _mapPickerCampaigns.Count)
                    return;

                List<PickerBattleEntry> list = _mapPickerCampaigns[_selectedCampaignIdx].Battles;
                if (list == null || list.Count == 0)
                    return;

                int count = list.Count;
                int bStart = Mathf.Clamp(_battlePage * 17, 0, count - 1);

                for (int offset = 0; offset < count; offset++)
                {
                    int idx = (bStart + offset) % count;
                    PickerBattleEntry entry = list[idx];
                    if (entry.MetadataResolved)
                        continue;

                    entry = GetResolvedBattleEntry(list, idx);
                    try
                    {
                        if (entry.BattleRef != null)
                        {
                            IBattle ib = entry.BattleRef.LoadBattle();
                            if (ib != null)
                            {
                                if (string.IsNullOrEmpty(entry.DisplayName) || entry.DisplayName == entry.UniqueId)
                                {
                                    string loadedName = ib.GetBattleName();
                                    if (!string.IsNullOrEmpty(loadedName))
                                        entry.DisplayName = loadedName;
                                }

                                BattleData bd = ib.GetBattleData();
                                if (bd != null)
                                {
                                    entry.IsVanilla = bd.IsVanilla();
                                    entry.InitialIsUnlocked = bd.IsUnlocked();
                                    entry.SaveDataId = !string.IsNullOrEmpty(bd.SaveDataId) ? bd.SaveDataId : entry.UniqueId;
                                    entry.PreviousSaveDataId = bd.PreviousMission_SaveDataId;
                                }
                            }
                        }
                    }
                    catch { }

                    if (string.IsNullOrEmpty(entry.SaveDataId))
                        entry.SaveDataId = entry.UniqueId;

                    entry.MetadataResolved = true;
                    list[idx] = entry;
                    return; // Strictly <= 1 LoadBattle() per frame
                }
            }

            private static void EvaluateBattleUnlockAndStars(
                PickerBattleEntry bEntry,
                int battleIdx,
                GameProgresses prog,
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
                    // Mask unresolved >0 vanilla rows during the <=0.25s amortized scan window so spoiler titles never flash
                    isUnlocked = (battleIdx == 0) || isCompleted;
                }
                else
                {
                    bool prevDone = false;
                    if (!string.IsNullOrEmpty(bEntry.PreviousSaveDataId) && prog != null)
                    {
                        try { prevDone = prog.IsLevelCompleted(bEntry.PreviousSaveDataId); } catch { }
                    }

                    isUnlocked = (battleIdx == 0) ||
                                 isCompleted ||
                                 bEntry.InitialIsUnlocked ||
                                 string.IsNullOrEmpty(bEntry.PreviousSaveDataId) ||
                                 prevDone;
                }
            }

            private static void DrawMapPickerWindow(float virtualW, float virtualH)
            {
                EnsureMapPickerCatalog();

                GameProgresses prog = null;
                try { prog = SavableData.Progresses; } catch { }

                float panelW = 820f;
                float panelH = 620f;
                float panelX = (virtualW - panelW) * 0.5f;
                float panelY = (virtualH - panelH) * 0.5f;

                GUI.color = new Color(0.05f, 0.06f, 0.08f, 0.95f);
                GUI.Box(new Rect(panelX, panelY, panelW, panelH), "");

                KeyCode mpKey = VisionConfig.MapPickerHotkey != null ? VisionConfig.MapPickerHotkey.Value : KeyCode.F5;
                GUI.color = new Color(1.0f, 0.88f, 0.35f, 1.0f);
                GUI.Label(new Rect(panelX + 20f, panelY + 10f, panelW - 360f, 22f),
                    $"Easy Red 2: Battle Hardened  |  Campaign & Mission Picker ({mpKey})");

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
                    ? "Select a Campaign on the left and a Mission on the right to Switch Immediately or Queue Next:"
                    : "Note: In Multiplayer Co-op, only the Room Host (MasterClient) can switch or queue missions.";
                GUI.Label(new Rect(panelX + 20f, panelY + 34f, panelW - 40f, 20f), roleNote);

                float colCampX = panelX + 20f;
                float colCampW = 310f;
                float colBatX = panelX + 346f;
                float colBatW = 454f;
                float listTopY = panelY + 60f;
                int rowsPerPage = 17;
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

                float yC = listTopY + 24f;
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
                    if (GUI.Button(new Rect(colCampX, yC, colCampW, rowH - 2f), $"{prefix}{cEntry.CampaignName} ({cEntry.Battles.Count})"))
                    {
                        _selectedCampaignIdx = i;
                        _selectedBattleIdx = 0;
                        _battlePage = 0;
                    }
                    yC += rowH;
                }

                // Right Column: Missions in Selected Campaign
                List<PickerBattleEntry> curBattles = (_selectedCampaignIdx >= 0 && _selectedCampaignIdx < campCount)
                    ? _mapPickerCampaigns[_selectedCampaignIdx].Battles
                    : null;
                int batCount = curBattles != null ? curBattles.Count : 0;
                int totalBatPages = Mathf.Max(1, (batCount + rowsPerPage - 1) / rowsPerPage);
                _battlePage = Mathf.Clamp(_battlePage, 0, totalBatPages - 1);
                _selectedBattleIdx = Mathf.Clamp(_selectedBattleIdx, 0, Mathf.Max(0, batCount - 1));

                GUI.color = new Color(0.55f, 0.82f, 1.0f, 1.0f);
                GUI.Label(new Rect(colBatX, listTopY, colBatW - 110f, 20f), $"=== Missions ({batCount})  [★ Beat | ??? Locked] ===");
                if (totalBatPages > 1)
                {
                    GUI.color = new Color(0.85f, 0.90f, 1.0f, 1.0f);
                    if (GUI.Button(new Rect(colBatX + colBatW - 104f, listTopY - 1f, 48f, 20f), "< Prev"))
                        _battlePage = (_battlePage - 1 + totalBatPages) % totalBatPages;
                    if (GUI.Button(new Rect(colBatX + colBatW - 52f, listTopY - 1f, 52f, 20f), "Next >"))
                        _battlePage = (_battlePage + 1) % totalBatPages;
                }

                float yB = listTopY + 24f;
                if (curBattles != null)
                {
                    int bStart = _battlePage * rowsPerPage;
                    int bEnd = Mathf.Min(batCount, bStart + rowsPerPage);
                    for (int j = bStart; j < bEnd; j++)
                    {
                        bool selected = j == _selectedBattleIdx;
                        PickerBattleEntry bEntry = GetResolvedBattleEntry(curBattles, j);
                        EvaluateBattleUnlockAndStars(bEntry, j, prog, out bool isCompleted, out bool wonBoth, out bool isUnlocked);

                        bool maskedAsUnknown = !isUnlocked && !_revealMapPickerSpoilers;
                        if (selected)
                        {
                            GUI.color = maskedAsUnknown
                                ? new Color(0.78f, 0.72f, 0.52f, 0.96f)
                                : new Color(0.45f, 0.95f, 0.52f, 1.0f);
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
                        string labelTitle = maskedAsUnknown ? "??? (Locked)" : $"{badge}{bEntry.DisplayName}";

                        if (GUI.Button(new Rect(colBatX, yB, colBatW, rowH - 2f), $"{prefix}{labelTitle}"))
                        {
                            _selectedBattleIdx = j;
                        }
                        yB += rowH;
                    }
                }

                // Bottom Selection Summary & Action Buttons
                float summaryY = panelY + panelH - 72f;
                string selSummary = "No mission selected.";
                string chosenVisibleName = null;
                PickerBattleEntry chosenBattle = default;
                bool hasChosen = false;
                if (curBattles != null && _selectedBattleIdx >= 0 && _selectedBattleIdx < curBattles.Count)
                {
                    chosenBattle = GetResolvedBattleEntry(curBattles, _selectedBattleIdx);
                    hasChosen = chosenBattle.BattleRef != null;
                    EvaluateBattleUnlockAndStars(chosenBattle, _selectedBattleIdx, prog, out bool selDone, out bool selBoth, out bool selUnlocked);
                    bool selMasked = !selUnlocked && !_revealMapPickerSpoilers;
                    string selBadge = selDone ? (selBoth ? "★★ " : "★ ") : "";
                    chosenVisibleName = selMasked ? "??? (Locked)" : chosenBattle.DisplayName;
                    string campTitle = _mapPickerCampaigns[_selectedCampaignIdx].CampaignName;
                    int playlistCount = MatchData.data != null && MatchData.data.battles != null ? MatchData.data.battles.Count : 0;
                    int curIdx = MatchData.data != null ? MatchData.data.currentBattleId + 1 : 1;
                    selSummary = $"Selected: [{campTitle}]  {selBadge}{chosenVisibleName}   |   Active Playlist: Battle {curIdx} of {playlistCount}";
                }

                GUI.color = new Color(0.95f, 0.92f, 0.78f, 1.0f);
                GUI.Label(new Rect(panelX + 20f, summaryY, panelW - 40f, 22f), selSummary);

                float bottomY = panelY + panelH - 42f;
                GUI.color = (hasChosen && canControlMatch)
                    ? new Color(0.45f, 0.95f, 0.52f, 1.0f)
                    : new Color(0.55f, 0.55f, 0.55f, 0.75f);
                if (GUI.Button(new Rect(panelX + 20f, bottomY, 260f, 28f), "Switch to Mission Now") && hasChosen && canControlMatch)
                {
                    TriggerMapPickerMission(chosenBattle.BattleRef, chosenVisibleName ?? chosenBattle.DisplayName, switchNow: true);
                }

                GUI.color = (hasChosen && canControlMatch)
                    ? new Color(0.55f, 0.85f, 1.0f, 1.0f)
                    : new Color(0.55f, 0.55f, 0.55f, 0.75f);
                if (GUI.Button(new Rect(panelX + 295f, bottomY, 260f, 28f), "Queue as Next Mission") && hasChosen && canControlMatch)
                {
                    TriggerMapPickerMission(chosenBattle.BattleRef, chosenVisibleName ?? chosenBattle.DisplayName, switchNow: false);
                }

                GUI.color = new Color(1.0f, 0.80f, 0.80f, 1.0f);
                if (GUI.Button(new Rect(panelX + 570f, bottomY, 230f, 28f), $"Close Picker ({mpKey})"))
                {
                    _showMapPicker = false;
                }

                GUI.color = Color.white;
            }

            private static void TriggerMapPickerMission(ICampaignTreeBattleData battleRef, string displayName, bool switchNow)
            {
                if (battleRef == null)
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

                    MatchBattle newMb = new MatchBattle(battleRef);
                    MatchBattle curMb = md.CurrentBattle;
                    if (curMb != null)
                    {
                        newMb.ticketsMultiplier = curMb.ticketsMultiplier;
                        newMb.maxAllies = curMb.maxAllies;
                        newMb.maxAxis = curMb.maxAxis;
                        newMb.proportionalAlliesRefValue = curMb.proportionalAlliesRefValue;
                        newMb.proportionalAxisRefValue = curMb.proportionalAxisRefValue;
                        newMb.proportionalSliderRefValue = curMb.proportionalSliderRefValue;
                        newMb.allowAchievements = curMb.allowAchievements;
                    }

                    if (md.battles == null)
                        md.ResetSelectedBattles();

                    int insertIdx = Mathf.Clamp(md.currentBattleId + 1, 0, md.battles.Count);
                    md.battles.Insert(insertIdx, newMb);

                    if (!switchNow)
                    {
                        if (Photon.Pun.PhotonNetwork.InRoom && Photon.Pun.PhotonNetwork.IsMasterClient)
                        {
                            md.UpdateNetworkMatchInfo();
                        }
                        ShowToast($"[Battle Hardened] Queued '{displayName}' as Next Mission (#{insertIdx + 1} in playlist)!");
                        return;
                    }

                    _showMapPicker = false;
                    _showConfigMenu = false;
                    md.currentBattleId = insertIdx;
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
                        _mpSwitchDisplayName = displayName;

                        if (hasRemoteClients)
                        {
                            _mpSwitchStage = 1;
                            _mpSwitchStageDeadline = Time.unscaledTime + 1.0f;
                        }
                        else
                        {
                            BeginHostLeaveRoomForMapSwitch(bl);
                        }

                        ShowToast($"[Battle Hardened] Switching Multiplayer Room to '{displayName}'...");
                        return;
                    }

                    md.StartBattleLoading();
                    ShowToast($"[Battle Hardened] Loading '{displayName}'...");
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
                _trenchCollisionGuardCache.Clear();
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
