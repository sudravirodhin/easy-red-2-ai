using System;
using System.Collections.Generic;
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
        private static int _activeConfigTab;

        private struct PickerBattleEntry
        {
            public string DisplayName;
            public string UniqueId;
            public ICampaignTreeBattleData BattleRef;
        }

        private struct PickerCampaignEntry
        {
            public string CampaignName;
            public List<PickerBattleEntry> Battles;
        }

        private static readonly List<PickerCampaignEntry> _mapPickerCampaigns = new List<PickerCampaignEntry>(24);
        private static bool _mapPickerInitialized;
        private static int _selectedCampaignIdx;
        private static int _selectedBattleIdx;
        private static int _campaignPage;
        private static int _battlePage;

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
            public static void Postfix()
            {
                try
                {
                    CleanupExpiredFiringRecords();
                    VeteranAndTacticsPatches.RenderVeteran3DMarkersAndCleanup();
                    VeteranAndTacticsPatches.UpdateMacroObjectiveAndEmplacementDirector();
                    VeteranAndTacticsPatches.UpdateQuickTapSpot(_showConfigMenu || _showMapPicker);

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
                            string state = VisionConfig.EnableMod.Value
                                ? $"ENABLED (Stand:{VisionConfig.StandingSightRange.Value}m, Crouch:{VisionConfig.CrouchingSightRange.Value}m, Prone:{VisionConfig.ProneSightRange.Value}m)"
                                : "DISABLED (Vanilla AI)";
                            ShowToast($"[Battle Hardened v{Plugin.PluginVersion}] Mod {state}");
                        }
                        else if (input.GetKeyDown(KeyCode.F9))
                        {
                            Plugin.Instance.Config.Reload();
                            ShowToast($"[Battle Hardened v{Plugin.PluginVersion}] Reloaded Config! Stand:{VisionConfig.StandingSightRange.Value}m | Crouch:{VisionConfig.CrouchingSightRange.Value}m | Prone:{VisionConfig.ProneSightRange.Value}m | Personalities:{VisionConfig.EnableRolePersonalities.Value}");
                        }
                        else if (input.GetKeyDown(KeyCode.F10))
                        {
                            VeteranAndTacticsPatches.CleanupDictionaries();
                            ShowToast(
                                $"[Battle Hardened v{Plugin.PluginVersion} Telemetry] Vets:{VeteranAndTacticsPatches.ActiveVeteransCount} (Prom:{VeteranAndTacticsPatches.FieldPromotionsCount}) | ExtraTicks:{AiCore.TotalAdaptiveAiExtraTicks} (PingSkip:{AiCore.TotalPingGovernorSkippedCycles}) | FFSaved:{VeteranAndTacticsPatches.FriendlyFirePrevented} | MedRedirect:{VeteranAndTacticsPatches.MedicTriageRedirectsCount}\n" +
                                $"GOAP:{VeteranAndTacticsPatches.GoapPlansExecutedCount} (Anvil:{VeteranAndTacticsPatches.TeamNetAnvilHammerCount} Flank:{VeteranAndTacticsPatches.GoapPinAndFlankCount} Smoke:{VeteranAndTacticsPatches.GoapSmokeScreensCount} RadioCut:{VeteranAndTacticsPatches.RadioNetCutoffsCount}) | WepUp:{VeteranAndTacticsPatches.RoleWeaponUpgradesCount} | BuddyResup:{VeteranAndTacticsPatches.BuddyResuppliesCount} | RadioLoot:{VeteranAndTacticsPatches.FieldRadiosInheritedCount} | Helmets:{VeteranAndTacticsPatches.HelmetsRecoveredCount} | Dragged:{VeteranAndTacticsPatches.CasualtiesDraggedCount}\n" +
                                $"Drops:{VeteranAndTacticsPatches.SelfPreservationDrops} | IndoorCrouch:{VeteranAndTacticsPatches.IndoorPronePreventedCount} | Pies:{VeteranAndTacticsPatches.CornerPiesExecutedCount} | TankCov:{VeteranAndTacticsPatches.TankHullCoverUsesCount} | Bounds:{VeteranAndTacticsPatches.BoundingOverwatchHoldsCount} | Doors:{VeteranAndTacticsPatches.DoorsStrategicallyClosedCount} | Bipods:{VeteranAndTacticsPatches.BipodsDeployed} | WallBlk:{VeteranAndTacticsPatches.WallsBlocked} | Melee:{VeteranAndTacticsPatches.MeleeAttacksTriggered} | Surr:{VeteranAndTacticsPatches.ShellShockSurrendersCount}/Lib:{VeteranAndTacticsPatches.ComradesLiberated}\n" +
                                $"Garrisons:{VeteranAndTacticsPatches.MacroGarrisonsAssignedCount} | Emplace:{VeteranAndTacticsPatches.StaticGunsStaffedCount} | APC:{VeteranAndTacticsPatches.SmartApcDismountsCount} | TankHold:{VeteranAndTacticsPatches.TankStandoffHoldsCount} | SmartAmmo:{VeteranAndTacticsPatches.SmartAmmoSwitchesCount} | Unsticks:{VeteranAndTacticsPatches.TankRockUnsticksCount} | Repairs:{VeteranAndTacticsPatches.ShelteredRepairsCount} | RoadkillSaved:{VeteranAndTacticsPatches.FriendlyRoadkillsPreventedCount} (ClientRK:{VeteranAndTacticsPatches.RemoteClientRoadkillsAssistedCount})\n" +
                                $"CombArms:{VeteranAndTacticsPatches.CombinedArmsPriorityAssistsCount} (Kamikaze:{VeteranAndTacticsPatches.KamikazeDivesTriggeredCount}) | Callouts:{VeteranAndTacticsPatches.LocalCalloutsSharedCount} | Supp:{VeteranAndTacticsPatches.SuppressiveEventsCount} | WoodPen:{VeteranAndTacticsPatches.ThinCoverPenetrationsCount} | TopAP:{VeteranAndTacticsPatches.AircraftTopAttackPenetrationsAssistedCount} (HostAssist:{VeteranAndTacticsPatches.HostVanillaClientApAssistsCount}) | CrewBlastBlk:{VeteranAndTacticsPatches.EnclosedCrewExplosionHitsBlockedCount} | AirMgBlk:{VeteranAndTacticsPatches.AirplanePilotMgHitsBlockedCount} | Chutes:{VeteranAndTacticsPatches.ParachutesDriftedCount}",
                                9.5f);
                        }
                    }

                    if (VisionConfig.DebugLogging.Value)
                    {
                        float dtMs = Mathf.Max(Time.unscaledDeltaTime, 0.0001f) * 1000f;
                        float fps = 1000f / dtMs;
                        AiCore.LogDebug(
                            "Heartbeat",
                            $"FPS:{fps:F0} ({dtMs:F1}ms) | Vets:{VeteranAndTacticsPatches.ActiveVeteransCount} (Prom:{VeteranAndTacticsPatches.FieldPromotionsCount}) | " +
                            $"GOAP:{VeteranAndTacticsPatches.GoapPlansExecutedCount} (Anvil:{VeteranAndTacticsPatches.TeamNetAnvilHammerCount} PinFlank:{VeteranAndTacticsPatches.GoapPinAndFlankCount} Smoke:{VeteranAndTacticsPatches.GoapSmokeScreensCount} WepUp:{VeteranAndTacticsPatches.RoleWeaponUpgradesCount} BuddyResup:{VeteranAndTacticsPatches.BuddyResuppliesCount} RadioLoot:{VeteranAndTacticsPatches.FieldRadiosInheritedCount} Helmets:{VeteranAndTacticsPatches.HelmetsRecoveredCount} Dragged:{VeteranAndTacticsPatches.CasualtiesDraggedCount} RadioCut:{VeteranAndTacticsPatches.RadioNetCutoffsCount}) | " +
                            $"Drops:{VeteranAndTacticsPatches.SelfPreservationDrops} | IndoorCrouch:{VeteranAndTacticsPatches.IndoorPronePreventedCount} | Pies:{VeteranAndTacticsPatches.CornerPiesExecutedCount} | TankCov:{VeteranAndTacticsPatches.TankHullCoverUsesCount} | " +
                            $"Callouts:{VeteranAndTacticsPatches.LocalCalloutsSharedCount} (KillVoice:{VeteranAndTacticsPatches.BotKillCalloutsCount}) | Supp:{VeteranAndTacticsPatches.SuppressiveEventsCount} | " +
                            $"Bounds:{VeteranAndTacticsPatches.BoundingOverwatchHoldsCount} | Garrisons:{VeteranAndTacticsPatches.MacroGarrisonsAssignedCount} | " +
                            $"Emplace:{VeteranAndTacticsPatches.StaticGunsStaffedCount} | APC:{VeteranAndTacticsPatches.SmartApcDismountsCount} | " +
                            $"TankHold:{VeteranAndTacticsPatches.TankStandoffHoldsCount} | SmartAmmo:{VeteranAndTacticsPatches.SmartAmmoSwitchesCount} | Unsticks:{VeteranAndTacticsPatches.TankRockUnsticksCount} | MedRedirect:{VeteranAndTacticsPatches.MedicTriageRedirectsCount} | RoadkillSaved:{VeteranAndTacticsPatches.FriendlyRoadkillsPreventedCount} (ClientRK:{VeteranAndTacticsPatches.RemoteClientRoadkillsAssistedCount}) | CombArms:{VeteranAndTacticsPatches.CombinedArmsPriorityAssistsCount} (Kamikaze:{VeteranAndTacticsPatches.KamikazeDivesTriggeredCount}) | " +
                            $"Bridges:{VeteranAndTacticsPatches.BridgeCrossingsAssistedCount} | Surr:{VeteranAndTacticsPatches.ShellShockSurrendersCount} (Lib:{VeteranAndTacticsPatches.ComradesLiberated}) | " +
                            $"WoodPen:{VeteranAndTacticsPatches.ThinCoverPenetrationsCount} | TopAP:{VeteranAndTacticsPatches.AircraftTopAttackPenetrationsAssistedCount} (HostAssist:{VeteranAndTacticsPatches.HostVanillaClientApAssistsCount}) | CrewBlastBlk:{VeteranAndTacticsPatches.EnclosedCrewExplosionHitsBlockedCount} | AirMgBlk:{VeteranAndTacticsPatches.AirplanePilotMgHitsBlockedCount} | PlaneAudio:{VeteranAndTacticsPatches.PlaneAudioLoopsDesyncedCount} | Chutes:{VeteranAndTacticsPatches.ParachutesDriftedCount} | " +
                            $"Doors:{VeteranAndTacticsPatches.DoorsStrategicallyClosedCount} | Bipods:{VeteranAndTacticsPatches.BipodsDeployed} | WallBlk:{VeteranAndTacticsPatches.WallsBlocked} | " +
                            $"Melee:{VeteranAndTacticsPatches.MeleeAttacksTriggered} | TankBlk:{VeteranAndTacticsPatches.UselessTankFirePrevented} (Crew:{VeteranAndTacticsPatches.ExposedCrewTargeted}) | " +
                            $"Ticks:{AiCore.TotalAdaptiveAiExtraTicks} (PingSkip:{AiCore.TotalPingGovernorSkippedCycles}) | FFSaved:{VeteranAndTacticsPatches.FriendlyFirePrevented} | Repairs:{VeteranAndTacticsPatches.ShelteredRepairsCount}",
                            15.0f);
                    }
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(BattleManager), nameof(BattleManager.OnGUI))]
        public static class BattleManager_OnGUI_Patch
        {
            public static void Postfix()
            {
                try
                {
                    if (Time.unscaledTime < _toastExpireTime && !string.IsNullOrEmpty(_toastMessage))
                    {
                        int lineCount = 1;
                        for (int i = 0; i < _toastMessage.Length; i++)
                        {
                            if (_toastMessage[i] == '\n')
                                lineCount++;
                        }
                        bool isMultiLine = lineCount > 1 || _toastMessage.Length > 120;
                        float toastW = isMultiLine ? Mathf.Min(Screen.width - 40f, 1240f) : 980f;
                        float toastH = isMultiLine ? Mathf.Max(42f, 16f + lineCount * 21f) : 38f;

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
                            float boxW = Mathf.Min(Screen.width - 40f, 920f);
                            float boxH = 58f;
                            float boxX = (Screen.width - boxW) * 0.5f;
                            float boxY = Screen.height * 0.78f;

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
                        DrawConfigMenu();
                    }
                    else if (_showMapPicker)
                    {
                        Cursor.visible = true;
                        Cursor.lockState = CursorLockMode.None;
                        DrawMapPickerWindow();
                    }
                }
                catch { }
            }

            private static void DrawConfigMenu()
            {
                float panelW = 820f;
                float panelH = 660f;
                float panelX = (Screen.width - panelW) * 0.5f;
                float panelY = (Screen.height - panelH) * 0.5f;

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
                            VisionConfig.EnableProactiveEmplacementStaffing,
                            VisionConfig.EnableCombinedArmsPrioritySupport);
                        DrawToggleButton(colLeftX, ref yL, colW, rowH, "3-Tier GOAP Planner & Squad Blackboard", VisionConfig.EnableTacticalGoapPlanner);
                        DrawToggleButton(colLeftX, ref yL, colW, rowH, "Audible GOAP Plan Voice Callouts", VisionConfig.EnableTacticalVoiceCallouts);
                        DrawToggleButton(colLeftX, ref yL, colW, rowH, "Radioman-Gated Team Intel Net", VisionConfig.EnableRadiomanIntelNet);
                        DrawToggleButton(colLeftX, ref yL, colW, rowH, "Objective Garrison & Counterattack Director", VisionConfig.EnableMacroObjectiveDirector);
                        DrawToggleButton(colLeftX, ref yL, colW, rowH, "Proactive Static Gun & Halftrack Staffing", VisionConfig.EnableProactiveEmplacementStaffing);
                        DrawToggleButton(colLeftX, ref yL, colW, rowH, "Combined-Arms Known Target Priority", VisionConfig.EnableCombinedArmsPrioritySupport);

                        yL += 8f;
                        DrawSectionHeaderWithBundle(colLeftX, ref yL, colW, "Emergent Squad & Battlefield Actions",
                            VisionConfig.EnableBuddyResupply,
                            VisionConfig.EnableEmergentWearableScavenge,
                            VisionConfig.EnableBuddyCasualtyDragging,
                            VisionConfig.EnableShellShockSurrender,
                            VisionConfig.EnableComradeLiberation);
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
                        DrawToggleButton(colRightX, ref yR, colW, rowH, "Gunner Bipod Wall & Ground Mounting", VisionConfig.EnableGunnerBipodWallMounting);
                        DrawToggleButton(colRightX, ref yR, colW, rowH, "Point-Blank Bayonet & Melee CQB", VisionConfig.EnablePointBlankMelee);
                        DrawToggleButton(colRightX, ref yR, colW, rowH, "Strategic Door & Shutter Discipline", VisionConfig.EnableStrategicDoorDiscipline);
                        DrawToggleButton(colRightX, ref yR, colW, rowH, "Bridge & Narrow Crossing Discipline", VisionConfig.EnableBridgeCrossingDiscipline);
                        DrawToggleButton(colRightX, ref yR, colW, rowH, "Wall & Fence Vaulting", VisionConfig.EnableWallVaulting);
                        DrawToggleButton(colRightX, ref yR, colW, rowH, "Anti-Stacking Personal Spacing", VisionConfig.EnableAntiStacking);
                        DrawToggleButton(colRightX, ref yR, colW, rowH, "Smart APC / Transport Dismount Under Fire", VisionConfig.EnableSmartApcDismount);
                        DrawToggleButton(colRightX, ref yR, colW, rowH, "Tank Standoff & Reverse Discipline", VisionConfig.EnableTankStandoffAndReverse);
                        break;

                    case 1: // 2. Vanilla Fixes
                        DrawSectionHeaderWithBundle(colLeftX, ref yL, colW, "Vanilla Infantry & Camera Bugfixes",
                            VisionConfig.EnableFirstPersonShadowFix,
                            VisionConfig.NeverShootStandingInOpen,
                            VisionConfig.EnforceStandingWhenOutOfCombat,
                            VisionConfig.EnableIndoorCeilingPronePrevention,
                            VisionConfig.EnableWallShootingPrevention,
                            VisionConfig.EnableFriendlyFireAvoidance,
                            VisionConfig.EnableAntiDogpileMedicTriage);
                        DrawToggleButton(colLeftX, ref yL, colW, rowH, "First-Person Self-Shadow Fix (Body/Gun)", VisionConfig.EnableFirstPersonShadowFix);
                        DrawToggleButton(colLeftX, ref yL, colW, rowH, "Never Shoot Standing Upright in Open", VisionConfig.NeverShootStandingInOpen);
                        DrawToggleButton(colLeftX, ref yL, colW, rowH, "Enforce Standing Speed Out of Combat", VisionConfig.EnforceStandingWhenOutOfCombat);
                        DrawToggleButton(colLeftX, ref yL, colW, rowH, "Indoor Low-Ceiling Prone Prevention", VisionConfig.EnableIndoorCeilingPronePrevention);
                        DrawToggleButton(colLeftX, ref yL, colW, rowH, "Wall-Muzzle Shooting Prevention", VisionConfig.EnableWallShootingPrevention);
                        DrawToggleButton(colLeftX, ref yL, colW, rowH, "Friendly Fire Line-of-Fire Avoidance", VisionConfig.EnableFriendlyFireAvoidance);
                        DrawToggleButton(colLeftX, ref yL, colW, rowH, "Anti-Dogpile Medic Triage & Claim Release", VisionConfig.EnableAntiDogpileMedicTriage);

                        DrawSectionHeaderWithBundle(colRightX, ref yR, colW, "Vanilla Vehicle, Armor & Plane Fixes",
                            VisionConfig.EnableSmartVehicleAmmoSelection,
                            VisionConfig.EnableSmartTankCrewRepair,
                            VisionConfig.EnableTankRockUnstickAssist,
                            VisionConfig.EnableCoopAirborneSpawnFix,
                            VisionConfig.EnableFriendlyVehicleRightOfWay,
                            VisionConfig.PreventSmallArmsFireAtTanks,
                            VisionConfig.EnablePlaneAudioSmoothing);
                        DrawToggleButton(colRightX, ref yR, colW, rowH, "Smart Vehicle & Plane Ammo (HE vs AP)", VisionConfig.EnableSmartVehicleAmmoSelection);
                        DrawToggleButton(colRightX, ref yR, colW, rowH, "Sheltered Tank Crew Repair & Remount Fix", VisionConfig.EnableSmartTankCrewRepair);
                        DrawToggleButton(colRightX, ref yR, colW, rowH, "Tank Rock / Obstacle Unstick Assist", VisionConfig.EnableTankRockUnstickAssist);
                        DrawToggleButton(colRightX, ref yR, colW, rowH, "Co-op Airborne Plane Spawn Hot-Start Fix", VisionConfig.EnableCoopAirborneSpawnFix);
                        DrawToggleButton(colRightX, ref yR, colW, rowH, "Friendly Right-of-Way & Client Roadkill Fix", VisionConfig.EnableFriendlyVehicleRightOfWay);
                        DrawToggleButton(colRightX, ref yR, colW, rowH, "Stop Small-Arms Fire at Tanks / Aim Crew", VisionConfig.PreventSmallArmsFireAtTanks);
                        DrawToggleButton(colRightX, ref yR, colW, rowH, "Plane Engine Audio Loop De-Throbber", VisionConfig.EnablePlaneAudioSmoothing);
                        break;

                    case 2: // 3. Vision & Realism
                        DrawSectionHeaderWithBundle(colLeftX, ref yL, colW, "Human Vision, Gaze & Memory",
                            VisionConfig.PreserveStanceStealthInCombat,
                            VisionConfig.EnforceHumanFOVWhenAlerted,
                            VisionConfig.EnableThreatSectorConcentration,
                            VisionConfig.EnableLocalContactCallouts,
                            VisionConfig.EnableLastKnownSuppression,
                            VisionConfig.EnableScopeVisionScaling,
                            VisionConfig.LimitAircraftInfantrySpotting);
                        DrawToggleButton(colLeftX, ref yL, colW, rowH, "Preserve Stance Stealth in Combat", VisionConfig.PreserveStanceStealthInCombat);
                        DrawToggleButton(colLeftX, ref yL, colW, rowH, "Enforce Human FOV Cones When Alerted", VisionConfig.EnforceHumanFOVWhenAlerted);
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

                        DrawSectionHeaderWithBundle(colRightX, ref yR, colW, "Player QoL, Obituary & Diagnostics",
                            VisionConfig.EnableQuickTapSpot,
                            VisionConfig.EnableDeathScreenAttribution,
                            VisionConfig.EnableSpectatorHudRetention,
                            VisionConfig.EnableExtendedRoomNames,
                            VisionConfig.DebugLogging);
                        DrawToggleButton(colRightX, ref yR, colW, rowH, "Master Mod Toggle (F8)", VisionConfig.EnableMod);
                        DrawToggleButton(colRightX, ref yR, colW, rowH, "Short-Press Orders Key to Spot", VisionConfig.EnableQuickTapSpot);
                        DrawToggleButton(colRightX, ref yR, colW, rowH, "Death Screen Kill Attribution + Age", VisionConfig.EnableDeathScreenAttribution);
                        DrawToggleButton(colRightX, ref yR, colW, rowH, "Keep Tactical HUD in Free Camera (F)", VisionConfig.EnableSpectatorHudRetention);
                        DrawToggleButton(colRightX, ref yR, colW, rowH, "64-Char Multiplayer Room Names", VisionConfig.EnableExtendedRoomNames);
                        DrawToggleButton(colRightX, ref yR, colW, rowH, "Diagnostic Debug Logs (BepInEx Console)", VisionConfig.DebugLogging);
                        break;
                }

                // Bottom Action Buttons
                float bottomY = panelY + panelH - 38f;
                GUI.color = new Color(0.75f, 0.90f, 1.0f, 1.0f);
                if (GUI.Button(new Rect(panelX + 20f, bottomY, 240f, 26f), "Reload .cfg from Disk (F9)"))
                {
                    Plugin.Instance.Config.Reload();
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

            internal static void EnsureMapPickerCatalog()
            {
                if (_mapPickerInitialized && _mapPickerCampaigns.Count > 0)
                    return;

                _mapPickerCampaigns.Clear();
                try
                {
                    var rawCampaigns = VanillaCampaignsList.vanilla_campaign_data;
                    if (rawCampaigns != null)
                    {
                        for (int i = 0; i < rawCampaigns.Length; i++)
                        {
                            CampaignTreeData camp = rawCampaigns[i];
                            if (camp == null || camp.battles == null || camp.battles.Count == 0)
                                continue;

                            string campName = camp.GetName();
                            if (string.IsNullOrEmpty(campName))
                                campName = camp.campaing_id ?? $"Campaign #{i + 1}";

                            List<PickerBattleEntry> bList = new List<PickerBattleEntry>(camp.battles.Count);
                            for (int j = 0; j < camp.battles.Count; j++)
                            {
                                ICampaignTreeBattleData bRef = camp.battles[j];
                                if (bRef == null)
                                    continue;

                                string uid = bRef.GetUniqueId() ?? $"Mission_{j + 1}";
                                string bName = Language.GetBattleName(uid);
                                if (string.IsNullOrEmpty(bName))
                                    bName = Language.GetText(uid);
                                if (string.IsNullOrEmpty(bName))
                                    bName = uid;

                                bList.Add(new PickerBattleEntry
                                {
                                    DisplayName = bName,
                                    UniqueId = uid,
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
                    }
                }
                catch { }

                _mapPickerInitialized = true;
                _selectedCampaignIdx = Mathf.Clamp(_selectedCampaignIdx, 0, Mathf.Max(0, _mapPickerCampaigns.Count - 1));
                _selectedBattleIdx = 0;
            }

            private static void DrawMapPickerWindow()
            {
                EnsureMapPickerCatalog();

                float panelW = 820f;
                float panelH = 620f;
                float panelX = (Screen.width - panelW) * 0.5f;
                float panelY = (Screen.height - panelH) * 0.5f;

                GUI.color = new Color(0.05f, 0.06f, 0.08f, 0.95f);
                GUI.Box(new Rect(panelX, panelY, panelW, panelH), "");

                KeyCode mpKey = VisionConfig.MapPickerHotkey != null ? VisionConfig.MapPickerHotkey.Value : KeyCode.F5;
                GUI.color = new Color(1.0f, 0.88f, 0.35f, 1.0f);
                GUI.Label(new Rect(panelX + 20f, panelY + 10f, panelW - 240f, 22f),
                    $"Easy Red 2: Battle Hardened  |  In-Game Campaign & Mission Picker ({mpKey})");

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
                GUI.Label(new Rect(colBatX, listTopY, colBatW - 110f, 20f), $"=== Missions ({batCount}) ===");
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
                        GUI.color = selected
                            ? new Color(0.45f, 0.95f, 0.52f, 1.0f)
                            : new Color(0.82f, 0.86f, 0.92f, 0.92f);

                        string prefix = selected ? "[SELECTED] " : $"#{j + 1}. ";
                        PickerBattleEntry bEntry = curBattles[j];
                        if (GUI.Button(new Rect(colBatX, yB, colBatW, rowH - 2f), $"{prefix}{bEntry.DisplayName}"))
                        {
                            _selectedBattleIdx = j;
                        }
                        yB += rowH;
                    }
                }

                // Bottom Selection Summary & Action Buttons
                float summaryY = panelY + panelH - 72f;
                string selSummary = "No mission selected.";
                PickerBattleEntry chosenBattle = default;
                bool hasChosen = false;
                if (curBattles != null && _selectedBattleIdx >= 0 && _selectedBattleIdx < curBattles.Count)
                {
                    chosenBattle = curBattles[_selectedBattleIdx];
                    hasChosen = chosenBattle.BattleRef != null;
                    string campTitle = _mapPickerCampaigns[_selectedCampaignIdx].CampaignName;
                    int playlistCount = MatchData.data != null && MatchData.data.battles != null ? MatchData.data.battles.Count : 0;
                    int curIdx = MatchData.data != null ? MatchData.data.currentBattleId + 1 : 1;
                    selSummary = $"Selected: [{campTitle}]  {chosenBattle.DisplayName}   |   Active Playlist: Battle {curIdx} of {playlistCount}";
                }

                GUI.color = new Color(0.95f, 0.92f, 0.78f, 1.0f);
                GUI.Label(new Rect(panelX + 20f, summaryY, panelW - 40f, 22f), selSummary);

                float bottomY = panelY + panelH - 42f;
                GUI.color = (hasChosen && canControlMatch)
                    ? new Color(0.45f, 0.95f, 0.52f, 1.0f)
                    : new Color(0.55f, 0.55f, 0.55f, 0.75f);
                if (GUI.Button(new Rect(panelX + 20f, bottomY, 260f, 28f), "Switch to Mission Now") && hasChosen && canControlMatch)
                {
                    TriggerMapPickerMission(chosenBattle.BattleRef, chosenBattle.DisplayName, switchNow: true);
                }

                GUI.color = (hasChosen && canControlMatch)
                    ? new Color(0.55f, 0.85f, 1.0f, 1.0f)
                    : new Color(0.55f, 0.55f, 0.55f, 0.75f);
                if (GUI.Button(new Rect(panelX + 295f, bottomY, 260f, 28f), "Queue as Next Mission") && hasChosen && canControlMatch)
                {
                    TriggerMapPickerMission(chosenBattle.BattleRef, chosenBattle.DisplayName, switchNow: false);
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

                    if (Photon.Pun.PhotonNetwork.InRoom)
                    {
                        md.UpdateNetworkMatchInfo();
                        BattleLoader bl = BattleLoader.instance;
                        if (bl != null && bl.photonView != null)
                        {
                            var rawBytes = ResourcesManager.SerializeToByteArray(md.Cast<Il2CppSystem.Object>());
                            var waitArr = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<float>(1);
                            waitArr[0] = 1.5f;
                            var rpcArgs = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Il2CppSystem.Object>(2);
                            rpcArgs[0] = rawBytes.Cast<Il2CppSystem.Object>();
                            rpcArgs[1] = waitArr.Cast<Il2CppSystem.Array>().GetValue(0);
                            bl.photonView.RPC("ConnectToNextMatch", Photon.Pun.RpcTarget.Others, rpcArgs);
                            bl.LoadNextMpMatch();
                            ShowToast($"[Battle Hardened] Switching Multiplayer Room to '{displayName}'...");
                            return;
                        }
                    }

                    md.StartBattleLoading();
                    ShowToast($"[Battle Hardened] Loading '{displayName}'...");
                }
                catch (Exception ex)
                {
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
                    Plugin.Instance.Config.Save();
                }

                GUI.color = new Color(0.95f, 0.45f, 0.45f, 0.92f);
                if (GUI.Button(new Rect(x + width - 45f, y - 1f, 45f, 19f), "OFF"))
                {
                    VisionConfig.SetBundleEntries(false, bundleEntries);
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

        public static void RenderVeteran3DMarkersAndCleanup()
        {
            if (!VisionConfig.EnableMod.Value)
                return;

            if (VisionConfig.EnableDeathScreenAttribution.Value)
                MaintainInstantDeathPanel();

            MaintainSpectatorHudVisibility();

            if ((Time.time > _nextStateCleanupTime || Time.time + 2.0f < _nextStateCleanupTime) && AiCore.TryClaimHeavyFrameWorkSlot())
            {
                _nextStateCleanupTime = Time.time + 3.0f;
                CleanupDictionaries(false);
            }

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

            int count = cursorSoldiers.Count;
            for (int i = 0; i < count; i++)
            {
                CursorSoldierData csd = cursorSoldiers[i];
                if (csd == null) continue;
                Soldier s = csd.soldier;
                if (s == null || !s.IsAlive || s.Pointer == player.Pointer) continue;

                if (s.faction != player.faction)
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
        private static readonly List<IntPtr> _reusableExpiredTasks = new List<IntPtr>(32);
        private static float _lastSceneTimeObserved;
        private static int _cleanupSlice;

        public static void CleanupDictionaries(bool forceFull = true)
        {
            float now = Time.time;
            // Detect match/scene restart (Time.time reset) and perform a full cache reset only then
            bool sceneRestarted = now < _lastSceneTimeObserved - 2.0f;
            _lastSceneTimeObserved = now;

            if (sceneRestarted)
            {
                forceFull = true;
                _coverExposureCache.Clear();
                _medicPatientClaims.Clear();
                VisionPatches.ClearVisionCaches();
                AiCore.ClearSpatialCaches();
                ClearBallisticsCaches();
                ClearDoorCaches();
                ClearCombinedArmsCaches();
                ClearGoapCaches();
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
                    if (!sol.IsPlayer && sol.Ref != null && GetVeteranTier(sol.Ref) > 0)
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
            _cleanupSlice = (slice + 1) & 3;

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
                AiCore.PruneDict(_peekRestoreTime, _reusableAlivePtrs);
                AiCore.PruneDict(_nextPeekAllowedTime, _reusableAlivePtrs);
                AiCore.PruneDict(_nextFFCheckTime, _reusableAlivePtrs);
                AiCore.PruneDict(_ffBlockedUntil, _reusableAlivePtrs);
                AiCore.PruneDict(_nextAntiStackTime, _reusableAlivePtrs);
                AiCore.PruneDict(_nextVaultCheckTime, _reusableAlivePtrs);
            }

            if (forceFull || slice == 1)
            {
                AiCore.PruneDict(_nextLiberateCheckTime, _reusableAlivePtrs);
                AiCore.PruneDict(_liberateMoveStartTime, _reusableAlivePtrs);
                AiCore.PruneDict(_liberateMoveCooldownUntil, _reusableAlivePtrs);
                AiCore.PruneDict(_bleedCoverDeadline, _reusableAlivePtrs);
                AiCore.PruneDict(_nextBipodSeekTime, _reusableAlivePtrs);
                AiCore.PruneDict(_nextBipodDeployCheckTime, _reusableAlivePtrs);
                AiCore.PruneDict(_bipodStandUntil, _reusableAlivePtrs);
                AiCore.PruneDict(_nextWallSideCheckTime, _reusableAlivePtrs);
                AiCore.PruneDict(_nextWallSidestepTime, _reusableAlivePtrs);
                AiCore.PruneDict(_nextTankDisciplineCountTime, _reusableAlivePtrs);
                AiCore.PruneDict(_poseLockUntil, _reusableAlivePtrs);
                AiCore.PruneDict(_nextBayonetCheckTime, _reusableAlivePtrs);
                AiCore.PruneDict(_nextMeleeAttemptTime, _reusableAlivePtrs);
                AiCore.PruneDict(_nextMeleeStepTime, _reusableAlivePtrs);
            }

            if (forceFull || slice == 2)
            {
                AiCore.PruneDict(_nextPromotionCheckTime, _reusableAlivePtrs);
                AiCore.PruneDict(_vehicleAimCache, _reusableAlivePtrs);
                AiCore.PruneDict(_threatSectorCache, _reusableAlivePtrs);
                AiCore.PruneDict(_watchWindowCache, _reusableAlivePtrs);
                AiCore.PruneDict(_localCalloutCache, _reusableAlivePtrs);
                AiCore.PruneDict(_nextSpotterCalloutTime, _reusableAlivePtrs);
                AiCore.PruneDict(_lastKnownTargets, _reusableAlivePtrs);
                AiCore.PruneDict(_nextSuppressiveGrenadeTime, _reusableAlivePtrs);
                AiCore.PruneDict(_nextSuppressiveBurstCountTime, _reusableAlivePtrs);
                AiCore.PruneDict(_nextIndoorCeilingCountTime, _reusableAlivePtrs);
                AiCore.PruneDict(_nextBoundingHoldCountTime, _reusableAlivePtrs);
                AiCore.PruneDict(_nextBridgeAssistCountTime, _reusableAlivePtrs);
                AiCore.PruneDict(_nextSurrenderCheckTime, _reusableAlivePtrs);
                AiCore.PruneDict(_nextLocalCoverSeekTime, _reusableAlivePtrs);
            }

            if (forceFull || slice == 3)
            {
                AiCore.PruneDict(_localCoverProtectedUntil, _reusableAlivePtrs);
                AiCore.PruneDict(_nextBotDoorCheckTime, _reusableAlivePtrs);
                AiCore.PruneDict(_assignedEmplacementUntil, _reusableAlivePtrs);
                AiCore.PruneDict(_nextFriendlyStepAsideTime, _reusableAlivePtrs);
                AiCore.PruneDict(_botGoapPlans, _reusableAlivePtrs);
                AiCore.PruneDict(_botScavengeCaches, _reusableAlivePtrs);
                AiCore.PruneDict(_nextWeaponUpgradeAllowedTime, _reusableAlivePtrs);
                AiCore.PruneDict(_nextBuddyBandageRefillTime, _reusableAlivePtrs);
                AiCore.PruneDict(_nextBuddyAmmoResupplyTime, _reusableAlivePtrs);
                AiCore.PruneDict(_nextSoldierVoiceTime, _reusableAlivePtrs);
                AiCore.PruneDict(_nextFlankCoverAssignTime, _reusableAlivePtrs);
                AiCore.PruneDict(_medicHealStartTime, _reusableAlivePtrs);
                AiCore.PruneDict(_medicHealTargetPtr, _reusableAlivePtrs);
                AiCore.PruneDict(_casualtyDragStates, _reusableAlivePtrs);
                AiCore.PruneDict(_medicPatientClaims, _reusableAlivePtrs);
                AiCore.PruneDict(_cornerPieCache, _reusableAlivePtrs);
                AiCore.PruneDict(_nextCornerPieCountTime, _reusableAlivePtrs);
                AiCore.PruneDict(_lastDamageByVictim, _reusableAlivePtrs);
                AiCore.PruneSet(_bayonetCheckedSoldiers, _reusableAlivePtrs);
                AiCore.PruneSet(_isTurningToWatchSector, _reusableAlivePtrs);

                if (_cachedInventoryOwners.Count > 1024)
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
                AiCore.PruneDict(_squadOrderedToVehicle, _reusableAlivePtrs);
                AiCore.PruneSet(_initialHqSpawnBriefingConsumed, _reusableAlivePtrs);

                if (_coverExposureCache.Count > 4096)
                    _coverExposureCache.Clear();
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
