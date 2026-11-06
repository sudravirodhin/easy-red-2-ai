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

        public static void ShowToast(string msg, float duration = 4.0f)
        {
            _toastMessage = msg;
            _toastExpireTime = Time.unscaledTime + duration;
            Plugin.Log.LogInfo(msg);
        }

        // =====================================================================
        // In-Game Hotkeys (F6 Menu, F8 Toggle, F9 Reload, F10 Stats) & HUD Toast
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
                    VeteranAndTacticsPatches.UpdateQuickTapSpot(_showConfigMenu);

                    var input = UnityInput.Current;
                    if (input != null)
                    {
                        if (input.GetKeyDown(KeyCode.F6))
                        {
                            _showConfigMenu = !_showConfigMenu;
                            if (_showConfigMenu)
                            {
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
                                $"[Battle Hardened] Vets:{VeteranAndTacticsPatches.ActiveVeteransCount} (Prom:{VeteranAndTacticsPatches.FieldPromotionsCount}) | " +
                                $"Drops:{VeteranAndTacticsPatches.SelfPreservationDrops} (Indoor:{VeteranAndTacticsPatches.IndoorPronePreventedCount}) | " +
                                $"Callouts:{VeteranAndTacticsPatches.LocalCalloutsSharedCount} | Supp:{VeteranAndTacticsPatches.SuppressiveEventsCount} | Bounds:{VeteranAndTacticsPatches.BoundingOverwatchHoldsCount} | " +
                                $"Garrisons:{VeteranAndTacticsPatches.MacroGarrisonsAssignedCount} | Emplace:{VeteranAndTacticsPatches.StaticGunsStaffedCount} | APC:{VeteranAndTacticsPatches.SmartApcDismountsCount} | TankHold:{VeteranAndTacticsPatches.TankStandoffHoldsCount} | RoadkillSaved:{VeteranAndTacticsPatches.FriendlyRoadkillsPreventedCount} | " +
                                $"CombArms:{VeteranAndTacticsPatches.CombinedArmsPriorityAssistsCount} | Bridges:{VeteranAndTacticsPatches.BridgeCrossingsAssistedCount} | Surr:{VeteranAndTacticsPatches.ShellShockSurrendersCount}/{VeteranAndTacticsPatches.ComradesLiberated} | Ticks:{AiCore.TotalAdaptiveAiExtraTicks} | FFSaved:{VeteranAndTacticsPatches.FriendlyFirePrevented}",
                                6.5f);
                        }
                    }

                        if (VisionConfig.DebugLogging.Value)
                    {
                        AiCore.LogDebug(
                            "Heartbeat",
                            $"Vets:{VeteranAndTacticsPatches.ActiveVeteransCount} (Prom:{VeteranAndTacticsPatches.FieldPromotionsCount}) | " +
                            $"Drops:{VeteranAndTacticsPatches.SelfPreservationDrops} | IndoorCrouch:{VeteranAndTacticsPatches.IndoorPronePreventedCount} | " +
                            $"Callouts:{VeteranAndTacticsPatches.LocalCalloutsSharedCount} | Supp:{VeteranAndTacticsPatches.SuppressiveEventsCount} | " +
                            $"Bounds:{VeteranAndTacticsPatches.BoundingOverwatchHoldsCount} | Garrisons:{VeteranAndTacticsPatches.MacroGarrisonsAssignedCount} | " +
                            $"Emplace:{VeteranAndTacticsPatches.StaticGunsStaffedCount} | APC:{VeteranAndTacticsPatches.SmartApcDismountsCount} | " +
                            $"TankHold:{VeteranAndTacticsPatches.TankStandoffHoldsCount} | RoadkillSaved:{VeteranAndTacticsPatches.FriendlyRoadkillsPreventedCount} | CombArms:{VeteranAndTacticsPatches.CombinedArmsPriorityAssistsCount} | " +
                            $"Bridges:{VeteranAndTacticsPatches.BridgeCrossingsAssistedCount} | Surr:{VeteranAndTacticsPatches.ShellShockSurrendersCount} (Lib:{VeteranAndTacticsPatches.ComradesLiberated}) | " +
                            $"WoodPen:{VeteranAndTacticsPatches.ThinCoverPenetrationsCount} | PlaneAudio:{VeteranAndTacticsPatches.PlaneAudioLoopsDesyncedCount} | Chutes:{VeteranAndTacticsPatches.ParachutesDriftedCount} | " +
                            $"Doors:{VeteranAndTacticsPatches.DoorsStrategicallyClosedCount} | Bipods:{VeteranAndTacticsPatches.BipodsDeployed} | WallBlk:{VeteranAndTacticsPatches.WallsBlocked} | " +
                            $"Melee:{VeteranAndTacticsPatches.MeleeAttacksTriggered} | TankBlk:{VeteranAndTacticsPatches.UselessTankFirePrevented} (Crew:{VeteranAndTacticsPatches.ExposedCrewTargeted}) | " +
                            $"Ticks:{AiCore.TotalAdaptiveAiExtraTicks} | FFSaved:{VeteranAndTacticsPatches.FriendlyFirePrevented} | Repairs:{VeteranAndTacticsPatches.ShelteredRepairsCount}",
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
                        GUI.color = new Color(0f, 0f, 0f, 0.78f);
                        GUI.Box(new Rect(20f, 20f, 980f, 38f), "");
                        GUI.color = Color.Lerp(Color.yellow, Color.white, 0.35f);
                        GUI.Label(new Rect(30f, 28f, 960f, 26f), _toastMessage);
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
                }
                catch { }
            }

            private static void DrawConfigMenu()
            {
                float panelW = 780f;
                float panelH = 796f;
                float panelX = (Screen.width - panelW) * 0.5f;
                float panelY = (Screen.height - panelH) * 0.5f;

                GUI.color = new Color(0.06f, 0.07f, 0.09f, 0.94f);
                GUI.Box(new Rect(panelX, panelY, panelW, panelH), "");

                GUI.color = new Color(1.0f, 0.88f, 0.35f, 1.0f);
                GUI.Label(new Rect(panelX + 20f, panelY + 10f, panelW - 40f, 22f),
                    $"Easy Red 2: Battle Hardened v{Plugin.PluginVersion}  |  Feature Bundles (F6 to Close)");

                GUI.color = new Color(0.75f, 0.78f, 0.82f, 1.0f);
                GUI.Label(new Rect(panelX + 20f, panelY + 29f, panelW - 40f, 20f),
                    "Click any feature or [ALL] / [OFF] bundle button below to toggle live in-game (saves to com.antigravity.easyred2.humanai.cfg):");

                float colLeftX = panelX + 20f;
                float colRightX = panelX + 398f;
                float colW = 362f;
                float rowH = 21f;
                float startY = panelY + 52f;

                // Left Column: Bundle 1 (Vision & Memory), Bundle 3 (Macro & Vehicles), Bundle 4A (Veterancy)
                float yL = startY;
                DrawSectionHeaderWithBundle(colLeftX, ref yL, colW, "Bundle 1: Vision, Memory & Suppression",
                    VisionConfig.EnableAdaptiveAiTickScaling,
                    VisionConfig.PreserveStanceStealthInCombat,
                    VisionConfig.EnforceHumanFOVWhenAlerted,
                    VisionConfig.EnableThreatSectorConcentration,
                    VisionConfig.EnableLocalContactCallouts,
                    VisionConfig.EnableLastKnownSuppression,
                    VisionConfig.EnableScopeVisionScaling,
                    VisionConfig.LimitAircraftInfantrySpotting);
                DrawToggleButton(colLeftX, ref yL, colW, rowH, "Master Mod Toggle (F8)", VisionConfig.EnableMod);
                DrawToggleButton(colLeftX, ref yL, colW, rowH, "Adaptive AI Brain Tick Scaling (SP & Co-op)", VisionConfig.EnableAdaptiveAiTickScaling);
                DrawToggleButton(colLeftX, ref yL, colW, rowH, "Preserve Stance Stealth in Combat", VisionConfig.PreserveStanceStealthInCombat);
                DrawToggleButton(colLeftX, ref yL, colW, rowH, "Enforce Human FOV Cones", VisionConfig.EnforceHumanFOVWhenAlerted);
                DrawToggleButton(colLeftX, ref yL, colW, rowH, "Threat-Sector Gaze & Window Alignment", VisionConfig.EnableThreatSectorConcentration);
                DrawToggleButton(colLeftX, ref yL, colW, rowH, "Local Contact Callouts (15m Radius)", VisionConfig.EnableLocalContactCallouts);
                DrawToggleButton(colLeftX, ref yL, colW, rowH, "Last-Known Suppressive Fire & Grenades", VisionConfig.EnableLastKnownSuppression);
                DrawToggleButton(colLeftX, ref yL, colW, rowH, "Scope & Turret Optic Scaling", VisionConfig.EnableScopeVisionScaling);
                DrawToggleButton(colLeftX, ref yL, colW, rowH, "Limit Aircraft Infantry Spotting", VisionConfig.LimitAircraftInfantrySpotting);

                yL += 4f;
                DrawSectionHeaderWithBundle(colLeftX, ref yL, colW, "Bundle 3: Macro, Emplacements & Armor",
                    VisionConfig.EnableMacroObjectiveDirector,
                    VisionConfig.EnableProactiveEmplacementStaffing,
                    VisionConfig.EnableCombinedArmsPrioritySupport,
                    VisionConfig.EnableSmartApcDismount,
                    VisionConfig.EnableTankStandoffAndReverse,
                    VisionConfig.EnableFriendlyVehicleRightOfWay,
                    VisionConfig.PreventSmallArmsFireAtTanks,
                    VisionConfig.EnableSmartTankCrewRepair);
                DrawToggleButton(colLeftX, ref yL, colW, rowH, "Objective Garrison & Counterattack Director", VisionConfig.EnableMacroObjectiveDirector);
                DrawToggleButton(colLeftX, ref yL, colW, rowH, "Proactive Static Gun & Halftrack Staffing", VisionConfig.EnableProactiveEmplacementStaffing);
                DrawToggleButton(colLeftX, ref yL, colW, rowH, "Combined-Arms Known Target Priority", VisionConfig.EnableCombinedArmsPrioritySupport);
                DrawToggleButton(colLeftX, ref yL, colW, rowH, "Smart APC / Transport Dismount Under Fire", VisionConfig.EnableSmartApcDismount);
                DrawToggleButton(colLeftX, ref yL, colW, rowH, "Tank Standoff & Reverse Discipline", VisionConfig.EnableTankStandoffAndReverse);
                DrawToggleButton(colLeftX, ref yL, colW, rowH, "Friendly Infantry Right-of-Way (No Roadkill)", VisionConfig.EnableFriendlyVehicleRightOfWay);
                DrawToggleButton(colLeftX, ref yL, colW, rowH, "Stop Small-Arms Fire at Tanks / Aim Crew", VisionConfig.PreventSmallArmsFireAtTanks);
                DrawToggleButton(colLeftX, ref yL, colW, rowH, "Sheltered Leeward Tank Crew Repairs", VisionConfig.EnableSmartTankCrewRepair);

                yL += 4f;
                DrawSectionHeaderWithBundle(colLeftX, ref yL, colW, "Bundle 4A: 15-Min Veteran System",
                    VisionConfig.EnableVeteranSystem,
                    VisionConfig.PromoteVeteranRanks,
                    VisionConfig.ShowVeteran3DIcons,
                    VisionConfig.ShowVeteranSquadRosterTags);
                DrawToggleButton(colLeftX, ref yL, colW, rowH, "15-Minute Veteran Progression", VisionConfig.EnableVeteranSystem);
                DrawToggleButton(colLeftX, ref yL, colW, rowH, "Field Rank Promotions & Decals", VisionConfig.PromoteVeteranRanks);
                DrawToggleButton(colLeftX, ref yL, colW, rowH, "3D Overhead Veteran Icons", VisionConfig.ShowVeteran3DIcons);
                DrawToggleButton(colLeftX, ref yL, colW, rowH, "Squad Roster Veteran Tags", VisionConfig.ShowVeteranSquadRosterTags);

                yL += 4f;
                DrawSectionHeaderWithBundle(colLeftX, ref yL, colW, "Bundle 4B: 30 Role Personalities",
                    VisionConfig.EnableRolePersonalities,
                    VisionConfig.EnableInfantryPersonalities,
                    VisionConfig.EnableVehicleAndPilotPersonalities,
                    VisionConfig.VeterancyCuresSkittishness,
                    VisionConfig.ShowPersonalityInSquadRoster);
                DrawToggleButton(colLeftX, ref yL, colW, rowH, "Enable AI Role Personalities (Master)", VisionConfig.EnableRolePersonalities);
                DrawToggleButton(colLeftX, ref yL, colW, rowH, "Infantry Role Personalities (21 Types)", VisionConfig.EnableInfantryPersonalities);
                DrawToggleButton(colLeftX, ref yL, colW, rowH, "Vehicle & Pilot Personalities (9 Types)", VisionConfig.EnableVehicleAndPilotPersonalities);
                DrawToggleButton(colLeftX, ref yL, colW, rowH, "Veterancy Cures Skittishness", VisionConfig.VeterancyCuresSkittishness);
                DrawToggleButton(colLeftX, ref yL, colW, rowH, "Show Personalities in Squad Roster", VisionConfig.ShowPersonalityInSquadRoster);

                // Right Column: Bundle 2 (Infantry Combat & Cover) & Bundle 5 (QoL & Diagnostics)
                float yR = startY;
                DrawSectionHeaderWithBundle(colRightX, ref yR, colW, "Bundle 2: Infantry Combat, Stance & Cover",
                    VisionConfig.EnableSelfPreservationStance,
                    VisionConfig.EnableIndoorCeilingPronePrevention,
                    VisionConfig.EnableStrategicDoorDiscipline,
                    VisionConfig.EnableBridgeCrossingDiscipline,
                    VisionConfig.EnableBoundingOverwatch,
                    VisionConfig.EnforceStandingWhenOutOfCombat,
                    VisionConfig.NeverShootStandingInOpen,
                    VisionConfig.BandageOnlyAfterFindingCover,
                    VisionConfig.EnableSmartWallSideCover,
                    VisionConfig.EnablePointBlankMelee,
                    VisionConfig.EnableGunnerBipodWallMounting,
                    VisionConfig.EnableWallShootingPrevention,
                    VisionConfig.EnableAutoPeekOverCover,
                    VisionConfig.EnableFriendlyFireAvoidance,
                    VisionConfig.EnableAntiStacking,
                    VisionConfig.EnableWallVaulting,
                    VisionConfig.EnableShellShockSurrender,
                    VisionConfig.EnableComradeLiberation);
                DrawToggleButton(colRightX, ref yR, colW, rowH, "Self-Preservation Stances", VisionConfig.EnableSelfPreservationStance);
                DrawToggleButton(colRightX, ref yR, colW, rowH, "Indoor Ceiling Prone Prevention", VisionConfig.EnableIndoorCeilingPronePrevention);
                DrawToggleButton(colRightX, ref yR, colW, rowH, "Strategic Door & Shutter Discipline", VisionConfig.EnableStrategicDoorDiscipline);
                DrawToggleButton(colRightX, ref yR, colW, rowH, "Bridge & Narrow Crossing Discipline", VisionConfig.EnableBridgeCrossingDiscipline);
                DrawToggleButton(colRightX, ref yR, colW, rowH, "Attacker Bounding Overwatch", VisionConfig.EnableBoundingOverwatch);
                DrawToggleButton(colRightX, ref yR, colW, rowH, "Enforce Standing Out of Combat", VisionConfig.EnforceStandingWhenOutOfCombat);
                DrawToggleButton(colRightX, ref yR, colW, rowH, "Never Shoot Standing in Open", VisionConfig.NeverShootStandingInOpen);
                DrawToggleButton(colRightX, ref yR, colW, rowH, "Seek Cover Before Bandaging", VisionConfig.BandageOnlyAfterFindingCover);
                DrawToggleButton(colRightX, ref yR, colW, rowH, "Stand BEHIND Walls (Smart Wall Cover)", VisionConfig.EnableSmartWallSideCover);
                DrawToggleButton(colRightX, ref yR, colW, rowH, "Point-Blank Bayonet & Melee CQB", VisionConfig.EnablePointBlankMelee);
                DrawToggleButton(colRightX, ref yR, colW, rowH, "Gunner Bipod Wall & Ground Mounting", VisionConfig.EnableGunnerBipodWallMounting);
                DrawToggleButton(colRightX, ref yR, colW, rowH, "Wall-Shooting Prevention", VisionConfig.EnableWallShootingPrevention);
                DrawToggleButton(colRightX, ref yR, colW, rowH, "Auto-Peek Over Low Cover", VisionConfig.EnableAutoPeekOverCover);
                DrawToggleButton(colRightX, ref yR, colW, rowH, "Friendly Fire Avoidance", VisionConfig.EnableFriendlyFireAvoidance);
                DrawToggleButton(colRightX, ref yR, colW, rowH, "Anti-Stacking Personal Spacing", VisionConfig.EnableAntiStacking);
                DrawToggleButton(colRightX, ref yR, colW, rowH, "Wall & Fence Vaulting", VisionConfig.EnableWallVaulting);
                DrawToggleButton(colRightX, ref yR, colW, rowH, "Shell-Shocked & Skittish Surrender", VisionConfig.EnableShellShockSurrender);
                DrawToggleButton(colRightX, ref yR, colW, rowH, "Surrendered Comrade Liberation", VisionConfig.EnableComradeLiberation);

                yR += 4f;
                DrawSectionHeaderWithBundle(colRightX, ref yR, colW, "Bundle 5: Player QoL, Obituary & Logs",
                    VisionConfig.EnableQuickTapSpot,
                    VisionConfig.EnableDeathScreenAttribution,
                    VisionConfig.EnableExtendedRoomNames,
                    VisionConfig.DebugLogging);
                DrawToggleButton(colRightX, ref yR, colW, rowH, "Short-Press Orders Key to Spot", VisionConfig.EnableQuickTapSpot);
                DrawToggleButton(colRightX, ref yR, colW, rowH, "Death Screen Kill Attribution + Age", VisionConfig.EnableDeathScreenAttribution);
                DrawToggleButton(colRightX, ref yR, colW, rowH, "64-Char Multiplayer Room Names", VisionConfig.EnableExtendedRoomNames);
                DrawToggleButton(colRightX, ref yR, colW, rowH, "Diagnostic Debug Logs (BepInEx Console)", VisionConfig.DebugLogging);

                yR += 4f;
                DrawSectionHeaderWithBundle(colRightX, ref yR, colW, "Bundle 6: Ballistics, Audio & Immersion",
                    VisionConfig.EnablePlaneAudioSmoothing,
                    VisionConfig.EnableThinCoverBulletPenetration,
                    VisionConfig.EnableParachuteWindDrift,
                    VisionConfig.EnableSpectatorHudRetention);
                DrawToggleButton(colRightX, ref yR, colW, rowH, "Plane Engine Loop De-Throbber (Client)", VisionConfig.EnablePlaneAudioSmoothing);
                DrawToggleButton(colRightX, ref yR, colW, rowH, "Thin-Cover Wood Bullet Penetration", VisionConfig.EnableThinCoverBulletPenetration);
                DrawToggleButton(colRightX, ref yR, colW, rowH, "Parachute Wind Drift & Slower Descent", VisionConfig.EnableParachuteWindDrift);
                DrawToggleButton(colRightX, ref yR, colW, rowH, "Keep Tactical HUD in Free Camera (F)", VisionConfig.EnableSpectatorHudRetention);

                // Bottom Action Buttons
                float bottomY = panelY + panelH - 38f;
                GUI.color = new Color(0.75f, 0.90f, 1.0f, 1.0f);
                if (GUI.Button(new Rect(panelX + 20f, bottomY, 220f, 26f), "Reload .cfg from Disk (F9)"))
                {
                    Plugin.Instance.Config.Reload();
                    ShowToast($"[Battle Hardened v{Plugin.PluginVersion}] Reloaded configuration from disk!");
                }

                GUI.color = new Color(0.85f, 1.0f, 0.85f, 1.0f);
                if (GUI.Button(new Rect(panelX + 260f, bottomY, 220f, 26f), "Save to .cfg File"))
                {
                    Plugin.Instance.Config.Save();
                    ShowToast($"[Battle Hardened v{Plugin.PluginVersion}] Saved configuration to com.antigravity.easyred2.humanai.cfg!");
                }

                GUI.color = new Color(1.0f, 0.80f, 0.80f, 1.0f);
                if (GUI.Button(new Rect(panelX + 520f, bottomY, 220f, 26f), "Close Menu (F6)"))
                {
                    _showConfigMenu = false;
                }

                GUI.color = Color.white;
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
        private static float _nextCoverCacheClearTime;

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

            if (Time.time > _nextStateCleanupTime)
            {
                _nextStateCleanupTime = Time.time + 3f;
                CleanupDictionaries();
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

        public static void CleanupDictionaries()
        {
            var alivePtrs = new HashSet<IntPtr>();
            int vetCount = 0;
            var allSquads = Squad.AllSquads;
            if (allSquads != null)
            {
                foreach (var kvp in allSquads)
                {
                    Squad sq = kvp.Value;
                    if (sq == null || sq.units == null) continue;
                    var units = sq.units;
                    int c = units.Count;
                    for (int i = 0; i < c; i++)
                    {
                        Soldier s = units[i];
                        if (s != null && s.IsAlive && s.Pointer != IntPtr.Zero)
                        {
                            alivePtrs.Add(s.Pointer);
                            if (!s.IsPlayer() && GetVeteranTier(s) > 0)
                                vetCount++;
                        }
                    }
                }
            }
            ActiveVeteransCount = vetCount;

            AiCore.PruneDict(_records, alivePtrs);
            AiCore.PruneDict(_stanceDropHoldFireUntil, alivePtrs);
            AiCore.PruneDict(_nextStanceDropAllowed, alivePtrs);
            AiCore.PruneDict(_combatStanceHoldUntil, alivePtrs);
            AiCore.PruneDict(_combatCrouchLockUntil, alivePtrs);
            AiCore.PruneDict(_noProneUntil, alivePtrs);
            AiCore.PruneDict(_nextWallCheckTime, alivePtrs);
            AiCore.PruneDict(_wallBlockedUntil, alivePtrs);
            AiCore.PruneDict(_peekRestoreTime, alivePtrs);
            AiCore.PruneDict(_nextPeekAllowedTime, alivePtrs);
            AiCore.PruneDict(_nextFFCheckTime, alivePtrs);
            AiCore.PruneDict(_ffBlockedUntil, alivePtrs);
            AiCore.PruneDict(_nextAntiStackTime, alivePtrs);
            AiCore.PruneDict(_nextVaultCheckTime, alivePtrs);
            AiCore.PruneDict(_nextLiberateCheckTime, alivePtrs);
            AiCore.PruneDict(_bleedCoverDeadline, alivePtrs);
            AiCore.PruneDict(_nextBipodSeekTime, alivePtrs);
            AiCore.PruneDict(_nextBipodDeployCheckTime, alivePtrs);
            AiCore.PruneDict(_bipodStandUntil, alivePtrs);
            AiCore.PruneDict(_nextWallSideCheckTime, alivePtrs);
            AiCore.PruneDict(_nextWallSidestepTime, alivePtrs);
            AiCore.PruneDict(_nextTankDisciplineCountTime, alivePtrs);
            AiCore.PruneDict(_poseLockUntil, alivePtrs);
            AiCore.PruneDict(_nextBayonetCheckTime, alivePtrs);
            AiCore.PruneDict(_nextMeleeAttemptTime, alivePtrs);
            AiCore.PruneDict(_nextMeleeStepTime, alivePtrs);
            AiCore.PruneDict(_nextPromotionCheckTime, alivePtrs);
            AiCore.PruneDict(_vehicleAimCache, alivePtrs);
            AiCore.PruneDict(_threatSectorCache, alivePtrs);
            AiCore.PruneDict(_watchWindowCache, alivePtrs);
            AiCore.PruneDict(_localCalloutCache, alivePtrs);
            AiCore.PruneDict(_nextSpotterCalloutTime, alivePtrs);
            AiCore.PruneDict(_lastKnownTargets, alivePtrs);
            AiCore.PruneDict(_nextSuppressiveGrenadeTime, alivePtrs);
            AiCore.PruneDict(_nextSuppressiveBurstCountTime, alivePtrs);
            AiCore.PruneDict(_nextIndoorCeilingCountTime, alivePtrs);
            AiCore.PruneDict(_nextBoundingHoldCountTime, alivePtrs);
            AiCore.PruneDict(_nextBridgeAssistCountTime, alivePtrs);
            AiCore.PruneDict(_nextSurrenderCheckTime, alivePtrs);
            AiCore.PruneDict(_nextBotDoorCheckTime, alivePtrs);
            AiCore.PruneDict(_assignedEmplacementUntil, alivePtrs);
            AiCore.PruneDict(_nextFriendlyStepAsideTime, alivePtrs);
            AiCore.PruneDict(_lastDamageByVictim, alivePtrs);

            AiCore.PruneSet(_bayonetCheckedSoldiers, alivePtrs);
            AiCore.PruneSet(_isTurningToWatchSector, alivePtrs);

            if (Time.time > _nextCoverCacheClearTime)
            {
                _nextCoverCacheClearTime = Time.time + 60f;
                _coverExposureCache.Clear();
                AiCore.ClearSpatialCaches();
                ClearBallisticsCaches();
                ClearDoorCaches();
                ClearCombinedArmsCaches();
            }

            if (_repairTaskStartTime.Count > 0)
            {
                float now = Time.time;
                var expiredTasks = new List<IntPtr>();
                foreach (var kvp in _repairTaskStartTime)
                {
                    if (now - kvp.Value > 60f)
                        expiredTasks.Add(kvp.Key);
                }
                for (int i = 0; i < expiredTasks.Count; i++)
                {
                    _repairTaskStartTime.Remove(expiredTasks[i]);
                    _countedRepairTasks.Remove(expiredTasks[i]);
                }
            }
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
