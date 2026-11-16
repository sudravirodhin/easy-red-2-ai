using System;
using System.Collections.Generic;
using Corvostudio.CinematicCamera;
using Corvostudio.Weapons;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;
using UnityEngine.UI;

namespace EasyRed2HumanAI
{
    public static partial class VeteranAndTacticsPatches
    {
        // ============================================================================
        // 4. SPECTATOR / FREE-CAMERA HUD, 'F' VIEW CYCLE & OBJECTIVE BAR FACTION NAMES
        //  - Decouples tactical overlays from CinematicCameraGUI.GUIVisible() so hiding
        //    the Free Camera UI banner does not wipe out Bot Stats or Objectives.
        //  - Pressing 'F' in Free Camera / Auto-Spectator cycles:
        //      Full Tactical -> Objectives Only -> Clean Cinema
        //  - Keeps both MissionStatusGUI (top bar) and ObjectiveGUI (3D A/B/C markers)
        //    live in Spectator Mode while filtering out private MissionTaskRetreat tasks.
        //  - Displays human-readable Faction + Side labels (e.g. "Germany (Axis)",
        //    "USSR (Allies)") flanking the Left and Right flags on the Objective Bar.
        // ============================================================================
        internal enum SpectatorTacticalHudMode : byte
        {
            FullTactical = 0,
            ObjectivesOnly = 1,
            CleanCinema = 2
        }

        internal static SpectatorTacticalHudMode ActiveSpectatorHudMode = SpectatorTacticalHudMode.FullTactical;
        private static float _nextSpectatorMissionRefreshTime;

        private static IntPtr _cachedMissionStatusGuiPtr;
        private static GameObject _leftFactionTextGo;
        private static GameObject _rightFactionTextGo;
        private static Text _leftFactionText;
        private static Text _rightFactionText;
        private static LayoutElement _leftFactionLayout;
        private static LayoutElement _rightFactionLayout;
        private static float _leftAppliedPrefW;
        private static float _rightAppliedPrefW;
        private static string _lastLeftFacStr;
        private static string _lastRightFacStr;

        private static void ResetObjectiveBarFactionState()
        {
            _cachedMissionStatusGuiPtr = IntPtr.Zero;
            _leftFactionTextGo = null;
            _rightFactionTextGo = null;
            _leftFactionText = null;
            _rightFactionText = null;
            _leftFactionLayout = null;
            _rightFactionLayout = null;
            _leftAppliedPrefW = 0f;
            _rightAppliedPrefW = 0f;
            _lastLeftFacStr = null;
            _lastRightFacStr = null;
        }

        internal static string CycleSpectatorTacticalHudMode()
        {
            ActiveSpectatorHudMode = (SpectatorTacticalHudMode)(((int)ActiveSpectatorHudMode + 1) % 3);
            MaintainSpectatorHudVisibility();
            switch (ActiveSpectatorHudMode)
            {
                case SpectatorTacticalHudMode.FullTactical:
                    return "[Battle Hardened] Spectator View (F): FULL TACTICAL (Objectives + Bot GOAP Inspector)";
                case SpectatorTacticalHudMode.ObjectivesOnly:
                    return "[Battle Hardened] Spectator View (F): OBJECTIVES ONLY (Objective Bar + 3D Markers)";
                default:
                    return "[Battle Hardened] Spectator View (F): CLEAN CINEMA (All Tactical Overlays Hidden)";
            }
        }

        internal static bool IsSpectatorCameraModeActive(out bool isFreeCamera, out bool canShowOverlays)
        {
            isFreeCamera = false;
            canShowOverlays = false;

            try
            {
                if (SavableData.Settings != null &&
                    SavableData.Settings.system != null &&
                    !SavableData.Settings.system.enableGUI)
                {
                    return false;
                }

                if (CinematicCameraGUI.instance != null && CinematicCameraGUI.cinematicCameraEnabled)
                {
                    isFreeCamera = true;
                    canShowOverlays = !RespawnPanel.GUIenabled;
                    return true;
                }

                Soldier controlled = Soldier.CurrentControlledSoldierOrNull();
                if ((controlled == null || !controlled.IsAlive) &&
                    CameraDirector.Instance != null &&
                    CameraDirector.Instance.isActiveAndEnabled)
                {
                    isFreeCamera = false;
                    canShowOverlays = !RespawnPanel.GUIenabled;
                    return true;
                }
            }
            catch { }

            return false;
        }

        public static void MaintainSpectatorHudVisibility()
        {
            if (!VisionConfig.EnableMod.Value || !VisionConfig.EnableSpectatorHudRetention.Value)
                return;

            if (CinematicCameraGUI.instance == null || !CinematicCameraGUI.cinematicCameraEnabled)
                return;

            // Only retain PlayerGUI (weapon/ammo/stance widgets) when a live controlled character exists
            // and the Cinematic Camera's own GUI is visible outside Clean Cinema mode!
            Soldier controlled = Soldier.CurrentControlledSoldierOrNull();
            bool hasLiveControlledSoldier = controlled != null && controlled.IsAlive;
            bool wantPlayerHud = CinematicCameraGUI.GUIVisible() &&
                                 hasLiveControlledSoldier &&
                                 !RespawnPanel.GUIenabled &&
                                 ActiveSpectatorHudMode != SpectatorTacticalHudMode.CleanCinema;

            PlayerGUI playerGui = PlayerGUI.instance;
            if (playerGui != null && playerGui.gui_obj != null)
            {
                if (playerGui.gui_obj.activeSelf != wantPlayerHud)
                    playerGui.gui_obj.SetActive(wantPlayerHud);
            }
        }

        private static Text CreateObjectiveBarFactionLabel(
            string objectName,
            Transform mainRowTransform,
            Text templateText,
            bool isLeftSide,
            out GameObject createdGo,
            out LayoutElement createdLayout)
        {
            createdGo = new GameObject(objectName);
            RectTransform rt = createdGo.AddComponent<RectTransform>();
            rt.SetParent(mainRowTransform, false);
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(100f, 22f);
            rt.localScale = Vector3.one;

            // Participate directly in mainRow's HorizontalLayoutGroup (padding=8,8,3,3, spacing=6) so
            // panel's ContentSizeFitter(PreferredSize) and mainRow's stretched 'bg' (+ left/right 'l'/'r'
            // pointed arrow caps) automatically expand around both faction names!
            createdLayout = createdGo.AddComponent<LayoutElement>();
            createdLayout.ignoreLayout = false;
            createdLayout.preferredHeight = 22f;
            createdLayout.preferredWidth = -1f;

            Text txt = createdGo.AddComponent<Text>();
            if (templateText != null)
            {
                txt.font = templateText.font;
                txt.fontSize = Mathf.Clamp(templateText.fontSize - 1, 13, 18);
            }
            else
            {
                txt.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
                txt.fontSize = 14;
            }
            txt.fontStyle = FontStyle.Bold;
            // Align toward the inner flag so the +6px outer LayoutElement width cushions the pointed arrow cap
            txt.alignment = isLeftSide ? TextAnchor.MiddleRight : TextAnchor.MiddleLeft;
            txt.horizontalOverflow = HorizontalWrapMode.Overflow;
            txt.verticalOverflow = VerticalWrapMode.Overflow;
            txt.raycastTarget = false;
            txt.color = new Color(0.96f, 0.94f, 0.88f, 0.96f);

            Shadow shadow = createdGo.AddComponent<Shadow>();
            shadow.effectColor = new Color(0f, 0f, 0f, 0.88f);
            shadow.effectDistance = new Vector2(1.25f, -1.25f);

            return txt;
        }

        private static void RebuildObjectiveBarLayout(MissionStatusGUI gui)
        {
            if (gui == null)
                return;

            if (gui.mainRow != null)
            {
                RectTransform mainRowRt = gui.mainRow.GetComponent<RectTransform>();
                if (mainRowRt != null)
                    LayoutRebuilder.ForceRebuildLayoutImmediate(mainRowRt);
            }
            if (gui.panel != null)
            {
                RectTransform panelRt = gui.panel.GetComponent<RectTransform>();
                if (panelRt != null)
                    LayoutRebuilder.ForceRebuildLayoutImmediate(panelRt);
            }
        }

        private static void UpdateMissionStatusFactionLabels(MissionStatusGUI gui)
        {
            if (gui == null || gui.Pointer == IntPtr.Zero || gui.mainRow == null)
                return;

            if (!VisionConfig.EnableObjectiveBarFactionNames.Value)
            {
                bool changedOff = false;
                if (_leftFactionTextGo != null && _leftFactionTextGo.activeSelf)
                {
                    _leftFactionTextGo.SetActive(false);
                    changedOff = true;
                }
                if (_rightFactionTextGo != null && _rightFactionTextGo.activeSelf)
                {
                    _rightFactionTextGo.SetActive(false);
                    changedOff = true;
                }
                if (changedOff)
                    RebuildObjectiveBarLayout(gui);
                return;
            }

            if (gui.flagInvaders == null || gui.flagDefenders == null)
                return;

            BattleData bd = BattleManager.GetCurrentBattleData();
            string invFac = bd != null ? bd.GetInvadersFaction() : null;
            string defFac = bd != null ? bd.GetDefendersFaction() : null;

            if ((string.IsNullOrEmpty(invFac) || string.IsNullOrEmpty(defFac)) && bd != null)
            {
                AiCore.EnsureWorldSnapshot();
                int sqCount = AiCore.WorldSquadCount;
                var squads = AiCore.WorldSquads;
                for (int i = 0; i < sqCount; i++)
                {
                    string f = squads[i].Faction;
                    if (string.IsNullOrEmpty(f))
                        continue;
                    if (bd.IsInvaderFaction(f))
                    {
                        if (string.IsNullOrEmpty(invFac))
                            invFac = f;
                    }
                    else if (string.IsNullOrEmpty(defFac))
                    {
                        defFac = f;
                    }
                    if (!string.IsNullOrEmpty(invFac) && !string.IsNullOrEmpty(defFac))
                        break;
                }
            }

            bool mirrored = gui.flagsMirrored;
            string leftFormatted = AiCore.FormatFactionWithAlliance(mirrored ? defFac : invFac);
            string rightFormatted = AiCore.FormatFactionWithAlliance(mirrored ? invFac : defFac);

            Transform mainRowTr = gui.mainRow.transform;
            bool layoutDirty = false;

            if (_cachedMissionStatusGuiPtr != gui.Pointer ||
                _leftFactionText == null ||
                _rightFactionText == null ||
                _leftFactionTextGo == null ||
                _rightFactionTextGo == null)
            {
                _cachedMissionStatusGuiPtr = gui.Pointer;
                _lastLeftFacStr = null;
                _lastRightFacStr = null;
                _leftAppliedPrefW = 0f;
                _rightAppliedPrefW = 0f;
                _leftFactionText = CreateObjectiveBarFactionLabel(
                    "BH_FactionLeft",
                    mainRowTr,
                    gui.ticketsText,
                    isLeftSide: true,
                    out _leftFactionTextGo,
                    out _leftFactionLayout);
                _rightFactionText = CreateObjectiveBarFactionLabel(
                    "BH_FactionRight",
                    mainRowTr,
                    gui.ticketsText,
                    isLeftSide: false,
                    out _rightFactionTextGo,
                    out _rightFactionLayout);
                layoutDirty = true;
            }

            // Keep BH_FactionLeft immediately before flag_invaders (and after sibling 0 'bg' so it renders above the bar)
            int flagInvIdx = gui.flagInvaders.transform.GetSiblingIndex();
            int leftIdx = _leftFactionTextGo.transform.GetSiblingIndex();
            int desiredLeftIdx = leftIdx < flagInvIdx ? flagInvIdx - 1 : flagInvIdx;
            if (mainRowTr.childCount > 0 && mainRowTr.GetChild(0) != _leftFactionTextGo.transform && flagInvIdx > 0 && desiredLeftIdx < 1)
                desiredLeftIdx = 1;
            if (leftIdx != desiredLeftIdx)
            {
                _leftFactionTextGo.transform.SetSiblingIndex(desiredLeftIdx);
                layoutDirty = true;
            }

            // Keep BH_FactionRight as the last child in mainRow (immediately after flag_defenders)
            int lastIdx = mainRowTr.childCount - 1;
            if (_rightFactionTextGo.transform.GetSiblingIndex() != lastIdx)
            {
                _rightFactionTextGo.transform.SetAsLastSibling();
                layoutDirty = true;
            }

            if (_lastLeftFacStr != leftFormatted)
            {
                _lastLeftFacStr = leftFormatted;
                _leftFactionText.text = leftFormatted;
                _leftAppliedPrefW = 0f;
                layoutDirty = true;
            }
            if (_lastRightFacStr != rightFormatted)
            {
                _lastRightFacStr = rightFormatted;
                _rightFactionText.text = rightFormatted;
                _rightAppliedPrefW = 0f;
                layoutDirty = true;
            }

            bool showLeft = !string.IsNullOrEmpty(leftFormatted);
            bool showRight = !string.IsNullOrEmpty(rightFormatted);
            if (_leftFactionTextGo.activeSelf != showLeft)
            {
                _leftFactionTextGo.SetActive(showLeft);
                layoutDirty = true;
            }
            if (_rightFactionTextGo.activeSelf != showRight)
            {
                _rightFactionTextGo.SetActive(showRight);
                layoutDirty = true;
            }

            if (showLeft && _leftAppliedPrefW <= 0f && _leftFactionLayout != null)
            {
                float w = _leftFactionText.preferredWidth;
                if (w > 1f)
                {
                    _leftAppliedPrefW = Mathf.Ceil(w) + 6f;
                    _leftFactionLayout.preferredWidth = _leftAppliedPrefW;
                    layoutDirty = true;
                }
                else
                {
                    _leftFactionLayout.preferredWidth = -1f;
                }
            }
            if (showRight && _rightAppliedPrefW <= 0f && _rightFactionLayout != null)
            {
                float w = _rightFactionText.preferredWidth;
                if (w > 1f)
                {
                    _rightAppliedPrefW = Mathf.Ceil(w) + 6f;
                    _rightFactionLayout.preferredWidth = _rightAppliedPrefW;
                    layoutDirty = true;
                }
                else
                {
                    _rightFactionLayout.preferredWidth = -1f;
                }
            }

            if (layoutDirty)
            {
                RebuildObjectiveBarLayout(gui);
            }
        }

        [HarmonyPatch(typeof(CinematicCameraGUI), nameof(CinematicCameraGUI.EnableCinematicCamera))]
        public static class CinematicCameraGUI_EnableCinematicCamera_Patch
        {
            public static void Postfix()
            {
                try
                {
                    MaintainSpectatorHudVisibility();
                }
                catch { }
            }
        }

        // Native MissionStatusGUI.LateUpdate (0x1804AB520) sets guiEnabled = false (skipping RefreshData) whenever
        // ControlledCharacter == null, and unconditionally calls panel.SetActive(false) whenever cinematicCameraEnabled
        // is true! Keep the Objective Bar live at 4 Hz in Free Cam & Auto-Spectator (unless CleanCinema is selected)
        // and maintain formatted Left/Right Faction + Side labels across all mission types.
        [HarmonyPatch(typeof(MissionStatusGUI), nameof(MissionStatusGUI.LateUpdate))]
        public static class MissionStatusGUI_LateUpdate_Patch
        {
            public static void Postfix(MissionStatusGUI __instance)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value ||
                        __instance == null ||
                        __instance.panel == null)
                    {
                        return;
                    }

                    if (IsSpectatorCameraModeActive(out _, out bool canShowOverlays))
                    {
                        bool showSpecBar = canShowOverlays &&
                                           VisionConfig.EnableSpectatorHudRetention.Value &&
                                           ActiveSpectatorHudMode != SpectatorTacticalHudMode.CleanCinema;
                        if (!showSpecBar)
                        {
                            if (__instance.panel.activeSelf)
                                __instance.panel.SetActive(false);
                            return;
                        }

                        if (!__instance.guiEnabled || CinematicCameraGUI.cinematicCameraEnabled)
                        {
                            float now = Time.unscaledTime;
                            if (now >= _nextSpectatorMissionRefreshTime || now + 5.0f < _nextSpectatorMissionRefreshTime)
                            {
                                _nextSpectatorMissionRefreshTime = now + 0.25f;
                                __instance.RefreshData();
                            }

                            bool luaMaster = MissionStatusGUI.lua_masterEnabled;
                            bool showMainRow = !luaMaster || MissionStatusGUI.lua_vanillaBar;
                            if (__instance.mainRow != null && __instance.mainRow.activeSelf != showMainRow)
                                __instance.mainRow.SetActive(showMainRow);

                            bool hasLuaFields = luaMaster && MissionStatusGUI.lua_fields != null && MissionStatusGUI.lua_fields.Count > 0;
                            if (__instance.fieldsRow != null && __instance.fieldsRow.activeSelf != hasLuaFields)
                                __instance.fieldsRow.SetActive(hasLuaFields);
                            if (__instance.fieldsRowRight != null && __instance.fieldsRowRight.activeSelf != hasLuaFields)
                                __instance.fieldsRowRight.SetActive(hasLuaFields);

                            if (luaMaster && __instance.appliedLuaVersion != MissionStatusGUI.lua_version)
                            {
                                __instance.appliedLuaVersion = MissionStatusGUI.lua_version;
                                __instance.ApplyLuaFields();
                            }

                            var slots = __instance.objSlots;
                            if (slots != null)
                            {
                                float step = Mathf.Max(0.005f, Time.unscaledDeltaTime * 2.5f);
                                int sLen = slots.Count;
                                for (int i = 0; i < sLen; i++)
                                {
                                    var slot = slots[i];
                                    if (slot != null && slot.go != null && slot.go.activeSelf && slot.fill != null)
                                    {
                                        slot.fill.fillAmount = Mathf.MoveTowards(slot.fill.fillAmount, slot.targetFill, step);
                                    }
                                }
                            }
                        }

                        if (!__instance.panel.activeSelf)
                            __instance.panel.SetActive(true);

                        if (__instance.panelGroup != null && __instance.panelGroup.alpha < 0.90f)
                            __instance.panelGroup.alpha = 0.95f;
                    }

                    if (__instance.panel.activeSelf)
                    {
                        UpdateMissionStatusFactionLabels(__instance);
                    }
                }
                catch { }
            }
        }

        // Native ObjectiveGUI.LateUpdate (0x1804BA6B0) hides 3D world objective markers (guiPanel.SetActive(false))
        // whenever cinematicCameraEnabled is true OR ControlledCharacter == null. Keep public objective markers
        // (excluding private AI MissionTaskRetreat spheres) live in Free Cam & Auto-Spectator!
        [HarmonyPatch(typeof(ObjectiveGUI), nameof(ObjectiveGUI.LateUpdate))]
        public static class ObjectiveGUI_LateUpdate_Patch
        {
            public static void Postfix(ObjectiveGUI __instance)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value ||
                        __instance == null ||
                        __instance.guiPanel == null)
                    {
                        return;
                    }

                    if (!IsSpectatorCameraModeActive(out _, out bool canShowOverlays))
                        return;

                    bool allow3DMarkers = SavableData.Settings == null ||
                                          SavableData.Settings.system == null ||
                                          !SavableData.Settings.system.disable3DMarkers;
                    SquadTask task = __instance.currentTask;
                    MissionTaskHighlightPosition highlightTask = task != null ? task.TryCast<MissionTaskHighlightPosition>() : null;
                    bool isPublicObjective = task != null &&
                                             (highlightTask == null || !highlightTask.removed) &&
                                             task.TryCast<MissionTaskRetreat>() == null;
                    bool wantWorldMarker = canShowOverlays &&
                                           allow3DMarkers &&
                                           isPublicObjective &&
                                           VisionConfig.EnableSpectatorHudRetention.Value &&
                                           ActiveSpectatorHudMode != SpectatorTacticalHudMode.CleanCinema;

                    if (!wantWorldMarker)
                    {
                        if (__instance.guiPanel.activeSelf)
                            __instance.guiPanel.SetActive(false);
                        return;
                    }

                    float now = Time.unscaledTime;
                    if (now >= __instance.nextUpdateTime || __instance.nextUpdateTime - now > 5.0f)
                    {
                        __instance.nextUpdateTime = now + 0.25f;
                        __instance.RefreshData();
                    }

                    if (__instance.currentBaseAlpha <= 0.05f)
                    {
                        __instance.currentBaseAlpha = 0.85f;
                        __instance.slowTargetAlpha = 0.85f;
                        if (__instance.currentAlpha < 0.15f)
                            __instance.currentAlpha = 0.85f;
                    }

                    if (!__instance.guiPanel.activeSelf)
                        __instance.guiPanel.SetActive(true);

                    __instance.UpdateGUI();
                    __instance.UpdateSmoothing();
                }
                catch { }
            }
        }

        // ============================================================================
        // 5. VANILLA VISUAL FIX: CONNECTED 3RD-PERSON SHADOW RIG IN FIRST PERSON
        // Native x64 Root Causes in GameAssembly.dll:
        //  1) SoldierLodManager.ApplyFace (0x18079B4D0) swaps 3P head submeshes on
        //     bodyMesh to hideMaterial (zero shadow), leaving a floating helmet shadow.
        //  2) FPSGunManager.UpdateItemInFPSHands (0x180796610) calls ShowRealArms(false)
        //     (collapsing 3P leftArm & rightArm to Vector3.zero) and mounts severed
        //     camera-pitched bicep tubes + held weapon on FPSGunManager—projecting
        //     bizarre disembodied floating arm/sleeve shadows when looking down!
        // Fix (Strictly Local FPS Player Only — Zero Bot Overhead):
        //  - Suppress ShadowCastingMode.Off on FPSGunManager's camera-locked viewmodel
        //    renderers (automatically restoring ShadowCastingMode.On whenever a weapon
        //    is dropped, stowed on back, or leaving 1P).
        //  - Clone lightweight Vector3.one-scaled shadow arm bone subtrees mirroring the
        //    Animator-driven 3P leftArm & rightArm in LateUpdate, driving ShadowsOnly
        //    body, uniform, and 3P held-weapon proxies while keeping vanilla's camera-safe
        //    visible 3P legs/torso, zero-scale 3P arms, and hidden 3P head untouched!
        // ============================================================================
        private struct ShadowBonePair
        {
            public Transform Source;
            public Transform Shadow;
        }

        private struct ObservedLodFaceConfig
        {
            public Corvostudio.EasyRed2.Characters.FaceData Face;
            public UniformSleeveCoverType SleeveCover;
            public UniformHeadCoverType HeadCover;
            public bool Valid;
        }

        private static readonly Dictionary<IntPtr, ObservedLodFaceConfig> _observedLodFaceConfigs = new Dictionary<IntPtr, ObservedLodFaceConfig>(8);
        private static readonly Dictionary<int, Transform> _fpsShadowBoneMap = new Dictionary<int, Transform>(64);
        private static readonly List<ShadowBonePair> _fpsShadowBonePairs = new List<ShadowBonePair>(64);
        private static readonly List<Renderer> _suppressedFpsViewmodelRenderers = new List<Renderer>(24);

        private static IntPtr _localControlledSoldierPtrThisFrame;
        private static IntPtr _activeShadowRigSoldierPtr;
        private static Soldier _activeShadowRigSoldier;
        private static SoldierLodManager _activeShadowRigLodMgr;
        private static GameObject _leftArmShadowRootGo;
        private static GameObject _rightArmShadowRootGo;
        private static Transform _leftShadowUpperArm;
        private static Transform _leftShadowForeArm;
        private static Transform _leftShadowHand;
        private static Transform _rightShadowHand;
        private static Transform _shadowHandHeldItemPos;
        private static bool _shadowWeaponAttachedToLeftHand;
        private static SkinnedMeshRenderer _fpsBodyShadowProxy;
        private static SkinnedMeshRenderer _fpsUniformShadowProxy;
        private static SkinnedMeshRenderer _fpsVestShadowProxy;
        private static GameObject _fpsWeaponShadowProxyRoot;

        private static IntPtr _lastBoundBodyMeshPtr;
        private static int _lastBoundBodyBoneCount;
        private static IntPtr _lastBoundUniformMeshPtr;
        private static int _lastBoundUniformBoneCount;
        private static IntPtr _lastBoundVestMeshPtr;
        private static int _lastBoundVestBoneCount;
        private static IntPtr _lastBoundHeldWeaponPtr;
        private static int _lastBoundHeldWeaponRendererCount;
        private static int _lastBoundHeldWeaponChildCount = -1;
        private static bool _hasCachedWeaponGrip;
        private static Vector3 _cachedWeaponGripLocalPos;
        private static Quaternion _cachedWeaponGripLocalRot;
        private static bool _cachedWeaponAllowSprintIK;
        private static bool _cachedWeaponDoubleHanded;
        private static float _nextHeldWeaponAttachmentCheckTime;
        private static float _nextViewmodelShadowScanTime;
        private static bool _fpsShadowMaterialsDirty = true;
        private static bool _fpsPropertyBlockDirty = true;

        public static void RefreshLocalPlayerFirstPersonShadow()
        {
            try
            {
                _fpsShadowMaterialsDirty = true;
                _fpsPropertyBlockDirty = true;
                _nextViewmodelShadowScanTime = 0f;
                UpdateLocalPlayerFirstPersonShadowRig();
            }
            catch { }
        }

        internal static void ResetFirstPersonShadowRigOnSceneChange()
        {
            TeardownActiveFpsShadowRig(destroyObjects: true);
            _observedLodFaceConfigs.Clear();
        }

        private static void RestoreSuppressedViewmodelRenderers(Transform activeFpsGunRoot)
        {
            for (int i = _suppressedFpsViewmodelRenderers.Count - 1; i >= 0; i--)
            {
                Renderer r = _suppressedFpsViewmodelRenderers[i];
                if (r == null || r.Pointer == IntPtr.Zero)
                {
                    _suppressedFpsViewmodelRenderers.RemoveAt(i);
                    continue;
                }

                bool stillInFpsHands = activeFpsGunRoot != null &&
                                       r.transform != null &&
                                       r.transform.IsChildOf(activeFpsGunRoot);
                if (!stillInFpsHands)
                {
                    if (r.shadowCastingMode == UnityEngine.Rendering.ShadowCastingMode.Off)
                        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                    _suppressedFpsViewmodelRenderers.RemoveAt(i);
                }
            }
        }

        private static void SuppressActiveViewmodelShadows(FPSGunManager fpsGun)
        {
            if (fpsGun == null || fpsGun.Pointer == IntPtr.Zero || fpsGun.transform == null)
            {
                RestoreSuppressedViewmodelRenderers(null);
                return;
            }

            Transform fpsRoot = fpsGun.transform;
            RestoreSuppressedViewmodelRenderers(fpsRoot);

            var renderers = fpsGun.GetComponentsInChildren<Renderer>(true);
            if (renderers == null)
                return;

            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer r = renderers[i];
                if (r == null || r.Pointer == IntPtr.Zero)
                    continue;

                if (r.shadowCastingMode == UnityEngine.Rendering.ShadowCastingMode.On)
                {
                    r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    if (!_suppressedFpsViewmodelRenderers.Contains(r))
                        _suppressedFpsViewmodelRenderers.Add(r);
                }
            }
        }

        private static GameObject CloneShadowBoneSubtree(
            Transform src,
            Transform shadowParent,
            bool isRootArm,
            IntPtr skipHandHeldItemPosPtr)
        {
            if (src == null || shadowParent == null)
                return null;

            // Never clone or copy localPosition from player.handHeldItemPos!
            // In 1P, ShowRealArms(false) zeroes leftArm/rightArm localScale to (0,0,0), so whenever vanilla calls
            // MoveItemToLeftHand / MoveItemToRightHand (worldPositionStays = true), Unity divides by (0,0,0) and
            // explodes handHeldItemPos.localPosition into midair! We attach a clean shadow holder to _rightShadowHand/_leftShadowHand instead.
            if (!isRootArm && skipHandHeldItemPosPtr != IntPtr.Zero && src.Pointer == skipHandHeldItemPosPtr)
                return null;

            string srcName = src.name ?? "Bone";
            if (srcName.EndsWith("_BHShadow", StringComparison.Ordinal) ||
                srcName.StartsWith("BH_Fps", StringComparison.Ordinal))
            {
                return null;
            }

            // Do not treat temporary 3P handheld ItemObject instances attached to hands as skeleton bones
            if (!isRootArm && src.GetComponent<ItemObject>() != null)
                return null;

            GameObject shadowGo = new GameObject(srcName + "_BHShadow");
            Transform st = shadowGo.transform;
            st.SetParent(shadowParent, false);
            st.localPosition = src.localPosition;
            st.localRotation = src.localRotation;
            st.localScale = isRootArm
                ? Vector3.one
                : (src.localScale.sqrMagnitude < 0.0001f ? Vector3.one : src.localScale);

            _fpsShadowBoneMap[src.GetInstanceID()] = st;
            _fpsShadowBonePairs.Add(new ShadowBonePair { Source = src, Shadow = st });

            int childCount = src.childCount;
            for (int i = 0; i < childCount; i++)
            {
                Transform child = src.GetChild(i);
                if (child != null)
                    CloneShadowBoneSubtree(child, st, isRootArm: false, skipHandHeldItemPosPtr);
            }

            return shadowGo;
        }

        private static void TeardownActiveFpsShadowRig(bool destroyObjects)
        {
            try
            {
                RestoreSuppressedViewmodelRenderers(null);

                if (_activeShadowRigLodMgr != null && _activeShadowRigLodMgr.Pointer != IntPtr.Zero)
                {
                    SkinnedMeshRenderer bodyMesh = _activeShadowRigLodMgr.bodyMesh;
                    if (bodyMesh != null && bodyMesh.Pointer != IntPtr.Zero)
                        bodyMesh.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;

                    SkinnedMeshRenderer uniMesh = _activeShadowRigLodMgr.uniformMesh;
                    if (uniMesh != null && uniMesh.Pointer != IntPtr.Zero)
                    {
                        bool bipodHidingUniform = _activeShadowRigSoldier != null &&
                                                  _activeShadowRigSoldier.Pointer != IntPtr.Zero &&
                                                  _activeShadowRigSoldier.bipodShadowOnlyBodyRenderer == uniMesh;
                        if (bipodHidingUniform)
                            _activeShadowRigSoldier.bipodBodyShadowModeBeforeDeploy = UnityEngine.Rendering.ShadowCastingMode.On;
                        else
                            uniMesh.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                    }

                    SkinnedMeshRenderer vestMesh = _activeShadowRigLodMgr.vestMesh;
                    if (vestMesh != null && vestMesh.Pointer != IntPtr.Zero)
                    {
                        bool bipodHidingVest = _activeShadowRigSoldier != null &&
                                               _activeShadowRigSoldier.Pointer != IntPtr.Zero &&
                                               _activeShadowRigSoldier.bipodShadowOnlyVestRenderer == vestMesh;
                        if (bipodHidingVest)
                            _activeShadowRigSoldier.bipodVestShadowModeBeforeDeploy = UnityEngine.Rendering.ShadowCastingMode.On;
                        else
                            vestMesh.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                    }
                }

                if (_fpsBodyShadowProxy != null && _fpsBodyShadowProxy.Pointer != IntPtr.Zero)
                {
                    if (destroyObjects && _fpsBodyShadowProxy.gameObject != null)
                        UnityEngine.Object.Destroy(_fpsBodyShadowProxy.gameObject);
                    else
                        _fpsBodyShadowProxy.enabled = false;
                }

                if (_fpsUniformShadowProxy != null && _fpsUniformShadowProxy.Pointer != IntPtr.Zero)
                {
                    if (destroyObjects && _fpsUniformShadowProxy.gameObject != null)
                        UnityEngine.Object.Destroy(_fpsUniformShadowProxy.gameObject);
                    else
                        _fpsUniformShadowProxy.enabled = false;
                }

                if (_fpsVestShadowProxy != null && _fpsVestShadowProxy.Pointer != IntPtr.Zero)
                {
                    if (destroyObjects && _fpsVestShadowProxy.gameObject != null)
                        UnityEngine.Object.Destroy(_fpsVestShadowProxy.gameObject);
                    else
                        _fpsVestShadowProxy.enabled = false;
                }

                if (_fpsWeaponShadowProxyRoot != null && _fpsWeaponShadowProxyRoot.Pointer != IntPtr.Zero)
                {
                    if (destroyObjects)
                        UnityEngine.Object.Destroy(_fpsWeaponShadowProxyRoot);
                    else
                        _fpsWeaponShadowProxyRoot.SetActive(false);
                }

                if (destroyObjects)
                {
                    if (_shadowHandHeldItemPos != null && _shadowHandHeldItemPos.Pointer != IntPtr.Zero && _shadowHandHeldItemPos.gameObject != null)
                        UnityEngine.Object.Destroy(_shadowHandHeldItemPos.gameObject);
                    if (_leftArmShadowRootGo != null && _leftArmShadowRootGo.Pointer != IntPtr.Zero)
                        UnityEngine.Object.Destroy(_leftArmShadowRootGo);
                    if (_rightArmShadowRootGo != null && _rightArmShadowRootGo.Pointer != IntPtr.Zero)
                        UnityEngine.Object.Destroy(_rightArmShadowRootGo);

                    _leftArmShadowRootGo = null;
                    _rightArmShadowRootGo = null;
                    _leftShadowUpperArm = null;
                    _leftShadowForeArm = null;
                    _leftShadowHand = null;
                    _rightShadowHand = null;
                    _shadowHandHeldItemPos = null;
                    _shadowWeaponAttachedToLeftHand = false;
                    _fpsBodyShadowProxy = null;
                    _fpsUniformShadowProxy = null;
                    _fpsVestShadowProxy = null;
                    _fpsWeaponShadowProxyRoot = null;
                    _fpsShadowBoneMap.Clear();
                    _fpsShadowBonePairs.Clear();
                    _activeShadowRigSoldierPtr = IntPtr.Zero;
                    _activeShadowRigSoldier = null;
                    _activeShadowRigLodMgr = null;
                }
                else
                {
                    _activeShadowRigSoldierPtr = IntPtr.Zero;
                    _shadowWeaponAttachedToLeftHand = false;
                }

                _lastBoundBodyMeshPtr = IntPtr.Zero;
                _lastBoundBodyBoneCount = -1;
                _lastBoundUniformMeshPtr = IntPtr.Zero;
                _lastBoundUniformBoneCount = -1;
                _lastBoundVestMeshPtr = IntPtr.Zero;
                _lastBoundVestBoneCount = -1;
                _lastBoundHeldWeaponPtr = IntPtr.Zero;
                _lastBoundHeldWeaponRendererCount = -1;
                _lastBoundHeldWeaponChildCount = -1;
                _hasCachedWeaponGrip = false;
                _nextHeldWeaponAttachmentCheckTime = 0f;
                _nextViewmodelShadowScanTime = 0f;
            }
            catch { }
        }

        private static bool EnsureSkinnedShadowProxyBound(
            Transform parentTransform,
            SkinnedMeshRenderer src,
            ref SkinnedMeshRenderer proxy,
            string proxyName,
            ref IntPtr lastMeshPtr,
            ref int lastBoneCount)
        {
            if (src == null || src.Pointer == IntPtr.Zero || src.sharedMesh == null || src.sharedMesh.Pointer == IntPtr.Zero)
            {
                if (proxy != null && proxy.Pointer != IntPtr.Zero)
                    proxy.enabled = false;
                return false;
            }

            if (proxy == null || proxy.Pointer == IntPtr.Zero)
            {
                GameObject proxyGo = new GameObject(proxyName);
                proxyGo.transform.SetParent(parentTransform, false);
                proxyGo.layer = src.gameObject.layer;
                proxy = proxyGo.AddComponent<SkinnedMeshRenderer>();
                lastMeshPtr = IntPtr.Zero;
                lastBoneCount = -1;
            }

            var srcBones = src.bones;
            int boneCount = srcBones != null ? srcBones.Length : 0;
            IntPtr curMeshPtr = src.sharedMesh.Pointer;

            if (curMeshPtr != lastMeshPtr || boneCount != lastBoneCount)
            {
                lastMeshPtr = curMeshPtr;
                lastBoneCount = boneCount;
                proxy.sharedMesh = src.sharedMesh;

                if (srcBones != null && boneCount > 0)
                {
                    var mappedBones = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Transform>(boneCount);
                    for (int i = 0; i < boneCount; i++)
                    {
                        Transform b = srcBones[i];
                        if (b != null && _fpsShadowBoneMap.TryGetValue(b.GetInstanceID(), out Transform sb) && sb != null)
                            mappedBones[i] = sb;
                        else
                            mappedBones[i] = b;
                    }
                    proxy.bones = mappedBones;
                }

                Transform rb = src.rootBone;
                proxy.rootBone = (rb != null && _fpsShadowBoneMap.TryGetValue(rb.GetInstanceID(), out Transform srb) && srb != null)
                    ? srb
                    : rb;
                Bounds expandedBounds = src.localBounds;
                expandedBounds.Expand(1.25f);
                proxy.localBounds = expandedBounds;
                proxy.quality = src.quality;
                _fpsShadowMaterialsDirty = true;
                _fpsPropertyBlockDirty = true;
            }

            proxy.updateWhenOffscreen = true;
            proxy.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly;
            proxy.receiveShadows = false;
            proxy.enabled = true;
            return true;
        }

        private static bool ShouldSkipWeaponShadowPart(string name)
        {
            if (string.IsNullOrEmpty(name))
                return false;

            return name.IndexOf("muzzle", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("flash", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("reticle", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("crosshair", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("lens", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool TryComputeRelativeLocalTRS(
            Transform target,
            Transform root,
            out Vector3 relPos,
            out Quaternion relRot,
            out Vector3 relScale)
        {
            relPos = Vector3.zero;
            relRot = Quaternion.identity;
            relScale = Vector3.one;
            if (target == null || root == null)
                return false;

            if (target.Pointer == root.Pointer)
                return true;

            // Walk purely local Transform TRS steps from target up to root so zero-scaled ancestor arms
            // NEVER make worldToLocalMatrix singular or explode weapon part positions!
            Matrix4x4 mat = Matrix4x4.identity;
            Transform cur = target;
            int guard = 0;
            while (cur != null && cur.Pointer != root.Pointer && guard++ < 24)
            {
                mat = Matrix4x4.TRS(cur.localPosition, cur.localRotation, cur.localScale) * mat;
                cur = cur.parent;
            }

            if (cur == null || cur.Pointer != root.Pointer)
                return false;

            relPos = new Vector3(mat.m03, mat.m13, mat.m23);
            relRot = mat.rotation;
            relScale = new Vector3(
                mat.GetColumn(0).magnitude,
                mat.GetColumn(1).magnitude,
                mat.GetColumn(2).magnitude);
            return relScale.sqrMagnitude >= 0.00001f;
        }

        private static void SyncThirdPersonWeaponShadowProxy(Soldier player)
        {
            // Keep _shadowHandHeldItemPos cleanly synced to _rightShadowHand (handHeldItem_StartPos/Rot) or
            // _leftShadowHand (worldPositionStays = true across Vector3.one shadow hands during bolt-action/reload)
            if (_shadowHandHeldItemPos != null && _shadowHandHeldItemPos.Pointer != IntPtr.Zero)
            {
                bool attachedLeft = player.handHeldItemPos != null &&
                                    player.leftHand != null &&
                                    player.handHeldItemPos.parent != null &&
                                    player.handHeldItemPos.parent.Pointer == player.leftHand.Pointer;

                if (attachedLeft && _leftShadowHand != null)
                {
                    if (!_shadowWeaponAttachedToLeftHand)
                    {
                        _shadowWeaponAttachedToLeftHand = true;
                        _shadowHandHeldItemPos.SetParent(_leftShadowHand, true);
                    }
                }
                else if (_rightShadowHand != null)
                {
                    if (_shadowWeaponAttachedToLeftHand || _shadowHandHeldItemPos.parent != _rightShadowHand)
                    {
                        _shadowWeaponAttachedToLeftHand = false;
                        _shadowHandHeldItemPos.SetParent(_rightShadowHand, false);
                    }
                    _shadowHandHeldItemPos.localPosition = player.handHeldItem_StartPos;
                    _shadowHandHeldItemPos.localRotation = player.handHeldItem_StartRot;
                    _shadowHandHeldItemPos.localScale = Vector3.one;
                }
            }

            HandheldItem held = player.inventoryItemInHands != null ? player.inventoryItemInHands : player.GetHeldItem(0);
            if (held == null ||
                held.Pointer == IntPtr.Zero ||
                held.transform == null ||
                player.IsCarryingBody() ||
                player.HasSurrended())
            {
                if (_fpsWeaponShadowProxyRoot != null && _fpsWeaponShadowProxyRoot.Pointer != IntPtr.Zero)
                    _fpsWeaponShadowProxyRoot.SetActive(false);
                _lastBoundHeldWeaponPtr = IntPtr.Zero;
                _lastBoundHeldWeaponRendererCount = -1;
                _hasCachedWeaponGrip = false;
                return;
            }

            Transform shadowHolder = _shadowHandHeldItemPos != null && _shadowHandHeldItemPos.Pointer != IntPtr.Zero
                ? _shadowHandHeldItemPos
                : (_rightShadowHand != null ? _rightShadowHand : player.rightHand);
            if (shadowHolder == null)
                return;

            if (_fpsWeaponShadowProxyRoot == null || _fpsWeaponShadowProxyRoot.Pointer == IntPtr.Zero)
            {
                _fpsWeaponShadowProxyRoot = new GameObject("BH_FpsWeaponShadowProxy");
                _lastBoundHeldWeaponPtr = IntPtr.Zero;
            }

            Transform rootTr = _fpsWeaponShadowProxyRoot.transform;
            if (rootTr.parent != shadowHolder)
                rootTr.SetParent(shadowHolder, false);

            var tps = held.tpsAnims;
            rootTr.localPosition = tps != null ? tps.localPosInHands : Vector3.zero;
            rootTr.localRotation = Quaternion.identity;
            float wScale = (tps != null && tps.localScaleInHands > 0.01f) ? tps.localScaleInHands : 1.0f;
            rootTr.localScale = new Vector3(wScale, wScale, wScale);

            // Zero-Allocation Steady State: only invoke GetComponentsInChildren<MeshFilter> when the held weapon
            // pointer changes (or its child attachment count changes on a 1 Hz check for attached bayonets/scopes),
            // eliminating finalizable Il2CppArrayBase<MeshFilter> allocations on 99.9% of frames!
            bool needRebuild = held.Pointer != _lastBoundHeldWeaponPtr;
            float now = Time.unscaledTime;
            if (!needRebuild && now >= _nextHeldWeaponAttachmentCheckTime)
            {
                _nextHeldWeaponAttachmentCheckTime = now + 1.0f;
                int childCount = held.transform.childCount;
                if (childCount != _lastBoundHeldWeaponChildCount)
                    needRebuild = true;
            }

            if (needRebuild)
            {
                var meshFilters = held.GetComponentsInChildren<MeshFilter>(false);
                int mfCount = meshFilters != null ? meshFilters.Length : 0;
                _lastBoundHeldWeaponPtr = held.Pointer;
                _lastBoundHeldWeaponRendererCount = mfCount;
                _lastBoundHeldWeaponChildCount = held.transform.childCount;
                _nextHeldWeaponAttachmentCheckTime = now + 1.0f;

                for (int i = rootTr.childCount - 1; i >= 0; i--)
                {
                    Transform oldChild = rootTr.GetChild(i);
                    if (oldChild != null && oldChild.gameObject != null)
                        UnityEngine.Object.Destroy(oldChild.gameObject);
                }

                if (meshFilters != null)
                {
                    Transform heldRootTr = held.transform;
                    Transform grip = held.leftHandHoldPosition;
                    GenericGun heldGun = held.TryCast<GenericGun>();
                    _cachedWeaponDoubleHanded = heldGun == null || heldGun.DoubleHandedIK();
                    _cachedWeaponAllowSprintIK = tps != null && tps.IKWhenSprinting;
                    if (grip != null &&
                        _cachedWeaponDoubleHanded &&
                        TryComputeRelativeLocalTRS(grip, heldRootTr, out Vector3 gripPos, out Quaternion gripRot, out _))
                    {
                        _cachedWeaponGripLocalPos = gripPos;
                        _cachedWeaponGripLocalRot = gripRot;
                        _hasCachedWeaponGrip = true;
                    }
                    else
                    {
                        _hasCachedWeaponGrip = false;
                    }

                    for (int i = 0; i < mfCount; i++)
                    {
                        MeshFilter mf = meshFilters[i];
                        if (mf == null || mf.sharedMesh == null || mf.transform == null)
                            continue;

                        MeshRenderer mr = mf.GetComponent<MeshRenderer>();
                        if (mr == null || !mr.enabled || ShouldSkipWeaponShadowPart(mf.name))
                            continue;

                        if (!TryComputeRelativeLocalTRS(mf.transform, heldRootTr, out Vector3 relPos, out Quaternion relRot, out Vector3 relScale))
                            continue;

                        GameObject partGo = new GameObject("BH_WepPartShadow");
                        partGo.layer = player.gameObject.layer;
                        Transform pt = partGo.transform;
                        pt.SetParent(rootTr, false);
                        pt.localPosition = relPos;
                        pt.localRotation = relRot;
                        pt.localScale = relScale;

                        MeshFilter pmf = partGo.AddComponent<MeshFilter>();
                        pmf.sharedMesh = mf.sharedMesh;

                        MeshRenderer pmr = partGo.AddComponent<MeshRenderer>();
                        pmr.sharedMaterials = mr.sharedMaterials;
                        pmr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly;
                        pmr.receiveShadows = false;
                    }
                }
            }

            if (!_fpsWeaponShadowProxyRoot.activeSelf)
                _fpsWeaponShadowProxyRoot.SetActive(true);
        }

        private static void SolveTwoBoneShadowArmIK(
            Transform upperArm,
            Transform foreArm,
            Transform hand,
            Vector3 targetPos,
            Quaternion targetRot,
            Transform playerRoot)
        {
            Vector3 p0 = upperArm.position;
            Vector3 p1 = foreArm.position;
            Vector3 p2 = hand.position;

            float l1 = Vector3.Distance(p0, p1);
            float l2 = Vector3.Distance(p1, p2);
            if (l1 < 0.05f || l2 < 0.05f)
                return;

            Vector3 toTarget = targetPos - p0;
            float rawDist = toTarget.magnitude;
            if (rawDist < 0.05f)
                return;

            Vector3 targetDir = toTarget / rawDist;
            float minDist = Mathf.Abs(l1 - l2) + 0.02f;
            float maxDist = (l1 + l2) * 0.995f;
            float d = Mathf.Clamp(rawDist, minDist, maxDist);

            Vector3 curReach = p2 - p0;
            Vector3 elbowHint = p1 - p0;
            if (curReach.sqrMagnitude > 0.0001f)
                elbowHint -= curReach * (Vector3.Dot(elbowHint, curReach) / curReach.sqrMagnitude);

            Vector3 bendNormal = elbowHint - targetDir * Vector3.Dot(elbowHint, targetDir);
            if (bendNormal.sqrMagnitude < 0.0004f && playerRoot != null)
            {
                elbowHint = -playerRoot.right * 0.45f - playerRoot.up * 0.85f - playerRoot.forward * 0.25f;
                bendNormal = elbowHint - targetDir * Vector3.Dot(elbowHint, targetDir);
            }
            if (bendNormal.sqrMagnitude < 0.0001f)
                return;
            bendNormal.Normalize();

            float cosAlpha = Mathf.Clamp((l1 * l1 + d * d - l2 * l2) / (2f * l1 * d), -0.999f, 0.999f);
            float sinAlpha = Mathf.Sqrt(Mathf.Max(0f, 1f - cosAlpha * cosAlpha));
            Vector3 desiredUpperDir = (targetDir * cosAlpha + bendNormal * sinAlpha).normalized;

            Vector3 curUpperDir = (p1 - p0).normalized;
            if (curUpperDir.sqrMagnitude > 0.5f)
            {
                Quaternion upperSwing = Quaternion.FromToRotation(curUpperDir, desiredUpperDir);
                upperArm.rotation = upperSwing * upperArm.rotation;
            }

            Vector3 newP1 = foreArm.position;
            Vector3 curLowerDir = (hand.position - newP1).normalized;
            Vector3 desiredLowerDir = (p0 + targetDir * d - newP1).normalized;
            if (curLowerDir.sqrMagnitude > 0.5f && desiredLowerDir.sqrMagnitude > 0.5f)
            {
                Quaternion lowerSwing = Quaternion.FromToRotation(curLowerDir, desiredLowerDir);
                foreArm.rotation = lowerSwing * foreArm.rotation;
            }

            if (Quaternion.Angle(hand.rotation, targetRot) < 55f)
            {
                hand.rotation = Quaternion.Slerp(hand.rotation, targetRot, 0.45f);
            }
        }

        public static void UpdateLocalPlayerFirstPersonShadowRig()
        {
            try
            {
                Soldier player = Soldier.CurrentControlledSoldierOrNull();
                bool wantRig = VisionConfig.EnableMod.Value &&
                               VisionConfig.EnableFirstPersonShadowFix.Value &&
                               player != null &&
                               player.Pointer != IntPtr.Zero &&
                               player.IsAlive &&
                               player.IsFPSPlayer() &&
                               !player.IsOnVehicle() &&
                               player.lodManager != null &&
                               player.leftArm != null &&
                               player.rightArm != null;

                if (!wantRig)
                {
                    if (_activeShadowRigSoldierPtr != IntPtr.Zero || _suppressedFpsViewmodelRenderers.Count > 0)
                        TeardownActiveFpsShadowRig(destroyObjects: player == null || !player.IsAlive);
                    return;
                }

                SoldierLodManager lodMgr = player.lodManager;
                IntPtr playerPtr = player.Pointer;

                // Build or rebuild shadow bone subtrees when controlled soldier changes
                if (_activeShadowRigSoldierPtr != playerPtr ||
                    _leftArmShadowRootGo == null ||
                    _rightArmShadowRootGo == null)
                {
                    if (_activeShadowRigSoldier != null && _activeShadowRigSoldier.Pointer != playerPtr)
                        TeardownActiveFpsShadowRig(destroyObjects: true);

                    _fpsShadowBoneMap.Clear();
                    _fpsShadowBonePairs.Clear();

                    if (_shadowHandHeldItemPos != null && _shadowHandHeldItemPos.Pointer != IntPtr.Zero && _shadowHandHeldItemPos.gameObject != null)
                        UnityEngine.Object.Destroy(_shadowHandHeldItemPos.gameObject);
                    if (_leftArmShadowRootGo != null && _leftArmShadowRootGo.Pointer != IntPtr.Zero)
                        UnityEngine.Object.Destroy(_leftArmShadowRootGo);
                    if (_rightArmShadowRootGo != null && _rightArmShadowRootGo.Pointer != IntPtr.Zero)
                        UnityEngine.Object.Destroy(_rightArmShadowRootGo);

                    IntPtr skipHolderPtr = player.handHeldItemPos != null ? player.handHeldItemPos.Pointer : IntPtr.Zero;
                    _leftArmShadowRootGo = CloneShadowBoneSubtree(player.leftArm, player.leftArm.parent, isRootArm: true, skipHolderPtr);
                    _rightArmShadowRootGo = CloneShadowBoneSubtree(player.rightArm, player.rightArm.parent, isRootArm: true, skipHolderPtr);

                    _leftShadowUpperArm = _leftArmShadowRootGo != null ? _leftArmShadowRootGo.transform : null;
                    _leftShadowHand = (player.leftHand != null && _fpsShadowBoneMap.TryGetValue(player.leftHand.GetInstanceID(), out Transform shLeftHand))
                        ? shLeftHand
                        : null;
                    _rightShadowHand = (player.rightHand != null && _fpsShadowBoneMap.TryGetValue(player.rightHand.GetInstanceID(), out Transform shRightHand))
                        ? shRightHand
                        : null;
                    _leftShadowForeArm = _leftShadowHand != null ? _leftShadowHand.parent : null;
                    if (_leftShadowForeArm == _leftShadowUpperArm)
                        _leftShadowForeArm = null;

                    _shadowWeaponAttachedToLeftHand = false;
                    if (_rightShadowHand != null)
                    {
                        GameObject holderGo = new GameObject("handHeldItemPos_BHShadow");
                        _shadowHandHeldItemPos = holderGo.transform;
                        _shadowHandHeldItemPos.SetParent(_rightShadowHand, false);
                        _shadowHandHeldItemPos.localPosition = player.handHeldItem_StartPos;
                        _shadowHandHeldItemPos.localRotation = player.handHeldItem_StartRot;
                        _shadowHandHeldItemPos.localScale = Vector3.one;
                        if (player.handHeldItemPos != null)
                            _fpsShadowBoneMap[player.handHeldItemPos.GetInstanceID()] = _shadowHandHeldItemPos;
                    }
                    else
                    {
                        _shadowHandHeldItemPos = null;
                    }

                    _activeShadowRigSoldierPtr = playerPtr;
                    _activeShadowRigSoldier = player;
                    _activeShadowRigLodMgr = lodMgr;
                    _lastBoundBodyMeshPtr = IntPtr.Zero;
                    _lastBoundUniformMeshPtr = IntPtr.Zero;
                    _lastBoundVestMeshPtr = IntPtr.Zero;
                    _lastBoundHeldWeaponPtr = IntPtr.Zero;
                    _fpsShadowMaterialsDirty = true;
                    _fpsPropertyBlockDirty = true;
                    _nextViewmodelShadowScanTime = 0f;
                }

                // 1. Copy Animator + Soldier.LateUpdate 3P arm bone poses into Vector3.one-scaled shadow arm bones
                //    (Soldier.LateUpdate has ALREADY pitched spine by lookRotation.upDown, applied lean, and orbited bipod!)
                int pairCount = _fpsShadowBonePairs.Count;
                for (int i = 0; i < pairCount; i++)
                {
                    ShadowBonePair pair = _fpsShadowBonePairs[i];
                    if (pair.Source == null || pair.Shadow == null)
                        continue;
                    pair.Shadow.localPosition = pair.Source.localPosition;
                    pair.Shadow.localRotation = pair.Source.localRotation;
                }

                // 2. Bind & sync 3P Body and Uniform ShadowsOnly proxies
                Transform proxyParent = lodMgr.transform;
                SkinnedMeshRenderer bodyMesh = lodMgr.bodyMesh;
                bool hasBodyProxy = EnsureSkinnedShadowProxyBound(
                    proxyParent,
                    bodyMesh,
                    ref _fpsBodyShadowProxy,
                    "BH_FpsBodyShadowProxy",
                    ref _lastBoundBodyMeshPtr,
                    ref _lastBoundBodyBoneCount);
                if (hasBodyProxy && bodyMesh.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.Off)
                {
                    bodyMesh.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                }

                SkinnedMeshRenderer uniMesh = lodMgr.uniformMesh;
                bool hasUniProxy = EnsureSkinnedShadowProxyBound(
                    proxyParent,
                    uniMesh,
                    ref _fpsUniformShadowProxy,
                    "BH_FpsUniformShadowProxy",
                    ref _lastBoundUniformMeshPtr,
                    ref _lastBoundUniformBoneCount);
                if (hasUniProxy)
                {
                    // During bipod deploy, vanilla sets uniformMesh to ShadowsOnly to hide the 3P torso from the camera;
                    // preserve ShadowsOnly while deployed so the torso stays invisible to camera, and record Off for undeploy!
                    if (player.bipodShadowOnlyBodyRenderer == uniMesh)
                    {
                        player.bipodBodyShadowModeBeforeDeploy = UnityEngine.Rendering.ShadowCastingMode.Off;
                    }
                    else if (uniMesh.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.Off)
                    {
                        uniMesh.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    }
                }

                SkinnedMeshRenderer vestMesh = lodMgr.vestMesh;
                bool hasVestProxy = EnsureSkinnedShadowProxyBound(
                    proxyParent,
                    vestMesh,
                    ref _fpsVestShadowProxy,
                    "BH_FpsVestShadowProxy",
                    ref _lastBoundVestMeshPtr,
                    ref _lastBoundVestBoneCount);
                if (hasVestProxy)
                {
                    if (player.bipodShadowOnlyVestRenderer == vestMesh)
                    {
                        player.bipodVestShadowModeBeforeDeploy = UnityEngine.Rendering.ShadowCastingMode.Off;
                    }
                    else if (vestMesh.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.Off)
                    {
                        vestMesh.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    }
                }

                if (_fpsShadowMaterialsDirty)
                {
                    _fpsShadowMaterialsDirty = false;
                    if (hasBodyProxy)
                    {
                        Corvostudio.EasyRed2.Characters.FaceData fd = player.face;
                        UniformSleeveCoverType usct = player.uniform_Obj != null
                            ? player.uniform_Obj.forceHideHands
                            : (UniformSleeveCoverType)0;
                        UniformHeadCoverType uhct = player.uniform_Obj != null
                            ? player.uniform_Obj.forceHideHead
                            : (UniformHeadCoverType)0;

                        if (_observedLodFaceConfigs.TryGetValue(lodMgr.Pointer, out ObservedLodFaceConfig obs) && obs.Valid)
                        {
                            if (obs.Face != null) fd = obs.Face;
                            usct = obs.SleeveCover;
                            uhct = obs.HeadCover;
                        }

                        if (fd != null && fd.materials != null)
                        {
                            bool showBody = (int)usct != 2;
                            bool showHead3P = (int)uhct == 0;
                            SoldierLodManager.SetBodyHeadVisibility(
                                _fpsBodyShadowProxy,
                                fd.materials,
                                fd.index_body_submesh,
                                showBody,
                                showHead3P);
                        }
                    }

                    if (hasUniProxy && uniMesh.sharedMaterials != null)
                    {
                        _fpsUniformShadowProxy.sharedMaterials = uniMesh.sharedMaterials;
                    }

                    if (hasVestProxy && vestMesh.sharedMaterials != null)
                    {
                        _fpsVestShadowProxy.sharedMaterials = vestMesh.sharedMaterials;
                    }
                }

                if (_fpsPropertyBlockDirty)
                {
                    _fpsPropertyBlockDirty = false;
                    MaterialPropertyBlock mpb = lodMgr.ShaderPropertyBlock;
                    if (mpb != null)
                    {
                        if (hasBodyProxy) _fpsBodyShadowProxy.SetPropertyBlock(mpb);
                        if (hasUniProxy) _fpsUniformShadowProxy.SetPropertyBlock(mpb);
                        if (hasVestProxy) _fpsVestShadowProxy.SetPropertyBlock(mpb);
                    }
                }

                // 3. Suppress disembodied floating shadows on FPSGunManager viewmodel & sync 3P weapon shadow proxy
                FPSGunManager fpsGun = player.fpsGun != null ? player.fpsGun : player.GetFPSGunManager();
                HandheldItem curHeld = player.inventoryItemInHands != null ? player.inventoryItemInHands : player.GetHeldItem(0);
                IntPtr curHeldPtr = curHeld != null ? curHeld.Pointer : IntPtr.Zero;
                float now = Time.unscaledTime;
                if (curHeldPtr != _lastBoundHeldWeaponPtr || now >= _nextViewmodelShadowScanTime)
                {
                    _nextViewmodelShadowScanTime = now + 1.25f;
                    SuppressActiveViewmodelShadows(fpsGun);
                }

                SyncThirdPersonWeaponShadowProxy(player);

                // 4. Two-bone left-hand foregrip IK for shadow rig (vanilla skips OnAnimatorIK for FPS players)
                if (_hasCachedWeaponGrip &&
                    _fpsWeaponShadowProxyRoot != null &&
                    _fpsWeaponShadowProxyRoot.activeSelf &&
                    _leftShadowUpperArm != null &&
                    _leftShadowForeArm != null &&
                    _leftShadowHand != null &&
                    !player.IsReloading &&
                    !player.isInHandsAnimation &&
                    !player.IsThrowing &&
                    !player.DoingMelee() &&
                    (!player.IsRunning() || _cachedWeaponAllowSprintIK))
                {
                    Transform wRoot = _fpsWeaponShadowProxyRoot.transform;
                    Vector3 targetPos = wRoot.TransformPoint(_cachedWeaponGripLocalPos);
                    Quaternion targetRot = wRoot.rotation * _cachedWeaponGripLocalRot;
                    SolveTwoBoneShadowArmIK(_leftShadowUpperArm, _leftShadowForeArm, _leftShadowHand, targetPos, targetRot, player.transform);
                }
            }
            catch { }
        }

        public static void CheckLocalPlayerFirstPersonShadowTeardown()
        {
            try
            {
                Soldier player = Soldier.CurrentControlledSoldierOrNull();
                bool wantRig = VisionConfig.EnableMod.Value &&
                               VisionConfig.EnableFirstPersonShadowFix.Value &&
                               player != null &&
                               player.Pointer != IntPtr.Zero &&
                               player.IsAlive &&
                               player.IsFPSPlayer() &&
                               !player.IsOnVehicle();

                _localControlledSoldierPtrThisFrame = wantRig ? player.Pointer : IntPtr.Zero;

                if (!wantRig && (_activeShadowRigSoldierPtr != IntPtr.Zero || _suppressedFpsViewmodelRenderers.Count > 0))
                {
                    TeardownActiveFpsShadowRig(destroyObjects: player == null || !player.IsAlive);
                }
            }
            catch
            {
                _localControlledSoldierPtrThisFrame = IntPtr.Zero;
            }
        }

        [HarmonyPatch(typeof(Soldier), nameof(Soldier.LateUpdate))]
        public static class Soldier_LateUpdate_Patch
        {
            public static void Postfix(Soldier __instance)
            {
                // Single IntPtr check across 100+ AI bots (0 IL2CPP calls!); runs ONLY on the local 1P soldier
                // strictly AFTER Soldier.LateUpdate finishes spine pitch/yaw, lean, and bipod torso math!
                if (ReferenceEquals(__instance, null) || __instance.Pointer != _localControlledSoldierPtrThisFrame)
                    return;

                UpdateLocalPlayerFirstPersonShadowRig();
            }
        }

        [HarmonyPatch(typeof(FPSGunManager), nameof(FPSGunManager.UpdateItemInFPSHands))]
        public static class FPSGunManager_UpdateItemInFPSHands_Patch
        {
            public static void Postfix(FPSGunManager __instance)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value || !VisionConfig.EnableFirstPersonShadowFix.Value || __instance == null)
                        return;

                    Soldier player = Soldier.CurrentControlledSoldierOrNull();
                    if (player != null && player.IsAlive && player.IsFPSPlayer() && !player.IsOnVehicle())
                    {
                        _localControlledSoldierPtrThisFrame = player.Pointer;
                        _nextViewmodelShadowScanTime = 0f;
                        SuppressActiveViewmodelShadows(__instance);
                    }
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(SoldierLodManager), nameof(SoldierLodManager.RefreshUniformLod))]
        public static class SoldierLodManager_RefreshUniformLod_Patch
        {
            public static void Postfix(SoldierLodManager __instance)
            {
                if (_activeShadowRigLodMgr != null && __instance != null && _activeShadowRigLodMgr.Pointer == __instance.Pointer)
                {
                    _fpsShadowMaterialsDirty = true;
                    _fpsPropertyBlockDirty = true;
                }
            }
        }

        [HarmonyPatch(typeof(SoldierLodManager), nameof(SoldierLodManager.RefreshVestLod))]
        public static class SoldierLodManager_RefreshVestLod_Patch
        {
            public static void Postfix(SoldierLodManager __instance)
            {
                if (_activeShadowRigLodMgr != null && __instance != null && _activeShadowRigLodMgr.Pointer == __instance.Pointer)
                {
                    _fpsShadowMaterialsDirty = true;
                    _fpsPropertyBlockDirty = true;
                }
            }
        }

        [HarmonyPatch(typeof(SoldierLodManager), nameof(SoldierLodManager.ApplyFace))]
        public static class SoldierLodManager_ApplyFace_Patch
        {
            public static void Postfix(
                SoldierLodManager __instance,
                Corvostudio.EasyRed2.Characters.FaceData __1,
                UniformSleeveCoverType __2,
                UniformHeadCoverType __3)
            {
                try
                {
                    if (__instance == null || __instance.Pointer == IntPtr.Zero)
                        return;

                    if (_observedLodFaceConfigs.Count > 24)
                        _observedLodFaceConfigs.Clear();

                    _observedLodFaceConfigs[__instance.Pointer] = new ObservedLodFaceConfig
                    {
                        Face = __1,
                        SleeveCover = __2,
                        HeadCover = __3,
                        Valid = true
                    };

                    if (_activeShadowRigLodMgr != null && _activeShadowRigLodMgr.Pointer == __instance.Pointer)
                    {
                        _fpsShadowMaterialsDirty = true;
                        _fpsPropertyBlockDirty = true;
                    }
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(SoldierLodManager), nameof(SoldierLodManager.PushPropertyBlock))]
        public static class SoldierLodManager_PushPropertyBlock_Patch
        {
            public static void Postfix(SoldierLodManager __instance)
            {
                try
                {
                    if (_activeShadowRigLodMgr != null &&
                        __instance != null &&
                        _activeShadowRigLodMgr.Pointer == __instance.Pointer)
                    {
                        _fpsShadowMaterialsDirty = true;
                        _fpsPropertyBlockDirty = true;
                        _nextViewmodelShadowScanTime = 0f;
                    }
                }
                catch { }
            }
        }

        // ============================================================================
        // 6. VANILLA VISUAL FIX: SMOKE & WATER BLOOD SPRAY OCCLUSION FIX
        // Eliminates disembodied "ghost" blood sprays floating on top of smoke or water:
        //  1) Underwater & Cross-Water Hits: BulletInstance.OnHit sets onWater = true
        //     only when hitting Layer 4 (Water), so bullets hitting submerged/wading
        //     Soldier BodyPart colliders spawn airborne "ImpactFX_flesh" underwater.
        //  2) Tactical Smoke Volumes: ResourcesManager.PlayImpactEffect checks camera
        //     frustum (PointInCameraView) without checking smoke-collider LOS, causing
        //     red blood clouds to pop over opaque smoke screens. Swapping to vanilla's
        //     pooled "ImpactFX_flesh_noBlood" preserves 100% of bullet-flesh impact audio.
        //  3) Ambient Map Smoke & Water Depth Sorting: Normalizes pooled ImpactFX_flesh
        //     ParticleSystemRenderers once per pooled instance (clamping sortingOrder <= 0,
        //     renderQueue <= 3000, and sortingFudge >= +6.5m) so colliderless Stalingrad
        //     building/rubble/vehicle smoke and water transparencies sort over blood mist.
        // ============================================================================
        private struct OriginalBloodRendererState
        {
            public ParticleSystemRenderer Renderer;
            public int SortingOrder;
            public float SortingFudge;
            public Material SharedMat;
            public int RenderQueue;
        }

        private static readonly HashSet<IntPtr> _inspectedBloodFxRoots = new HashSet<IntPtr>(64);
        private static readonly List<OriginalBloodRendererState> _modifiedBloodRenderers = new List<OriginalBloodRendererState>(32);
        private static Il2CppSystem.Action<GameObject> _cachedBloodFxCallback;
        private static bool _loggedBloodRendererNormalization;

        private static Il2CppSystem.Action<GameObject> GetOrCreateBloodFxCallback()
        {
            if (_cachedBloodFxCallback == null)
            {
                _cachedBloodFxCallback = Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<Il2CppSystem.Action<GameObject>>(
                    new Action<GameObject>(OnBloodImpactFxSpawned));
            }
            return _cachedBloodFxCallback;
        }

        private static void OnBloodImpactFxSpawned(GameObject go)
        {
            try
            {
                if (go == null || go.Pointer == IntPtr.Zero || !_inspectedBloodFxRoots.Add(go.Pointer))
                    return;

                var renderers = go.GetComponentsInChildren<ParticleSystemRenderer>(true);
                if (renderers == null)
                    return;

                for (int i = 0; i < renderers.Length; i++)
                {
                    ParticleSystemRenderer psr = renderers[i];
                    if (psr == null || psr.Pointer == IntPtr.Zero)
                        continue;

                    int origOrder = psr.sortingOrder;
                    float origFudge = psr.sortingFudge;
                    Material sharedMat = psr.sharedMaterial;
                    bool hasMat = sharedMat != null && sharedMat.Pointer != IntPtr.Zero;
                    int origQueue = hasMat ? sharedMat.renderQueue : -1;

                    int targetOrder = origOrder > 0 ? 0 : origOrder;
                    float targetFudge = origFudge < 6.5f ? 6.5f : origFudge;
                    bool clampQueue = hasMat && origQueue > 3000;

                    if (targetOrder != origOrder || !Mathf.Approximately(targetFudge, origFudge) || clampQueue)
                    {
                        if (_modifiedBloodRenderers.Count < 256)
                        {
                            _modifiedBloodRenderers.Add(new OriginalBloodRendererState
                            {
                                Renderer = psr,
                                SortingOrder = origOrder,
                                SortingFudge = origFudge,
                                SharedMat = hasMat ? sharedMat : null,
                                RenderQueue = origQueue
                            });
                        }

                        psr.sortingOrder = targetOrder;
                        psr.sortingFudge = targetFudge;
                        if (clampQueue)
                            sharedMat.renderQueue = 3000;

                        if (!_loggedBloodRendererNormalization)
                        {
                            _loggedBloodRendererNormalization = true;
                            AiCore.LogDebug(
                                "BloodVfx",
                                $"Normalized pooled ImpactFX_flesh renderer '{psr.name}': order {origOrder}->{targetOrder}, fudge {origFudge:F1}->{targetFudge:F1}, queue {origQueue}->{(clampQueue ? 3000 : origQueue)}",
                                10.0f);
                        }
                    }
                }
            }
            catch { }
        }

        private static void RestoreBloodRendererDefaults()
        {
            for (int i = 0; i < _modifiedBloodRenderers.Count; i++)
            {
                OriginalBloodRendererState st = _modifiedBloodRenderers[i];
                try
                {
                    if (st.Renderer != null && st.Renderer.Pointer != IntPtr.Zero)
                    {
                        st.Renderer.sortingOrder = st.SortingOrder;
                        st.Renderer.sortingFudge = st.SortingFudge;
                    }
                    if (st.SharedMat != null && st.SharedMat.Pointer != IntPtr.Zero && st.RenderQueue > 0)
                    {
                        st.SharedMat.renderQueue = st.RenderQueue;
                    }
                }
                catch { }
            }
            _modifiedBloodRenderers.Clear();
            _inspectedBloodFxRoots.Clear();
        }

        [HarmonyPatch(typeof(ResourcesManager), nameof(ResourcesManager.PlayImpactEffect))]
        public static class ResourcesManager_PlayImpactEffect_Patch
        {
            public static void Prefix(
                Vector3 pos,
                ref string impact_prefab_name,
                bool isExplosion,
                int maxSpawnDistance,
                ref Il2CppSystem.Action<GameObject> gameObjectFx,
                out long __state)
            {
                __state = HitchProfiler.Begin();
                try
                {
                    if (!VisionConfig.EnableMod.Value)
                        return;

                    if (isExplosion &&
                        !string.IsNullOrEmpty(impact_prefab_name) &&
                        (IsIncendiaryString(impact_prefab_name) ||
                         impact_prefab_name.IndexOf("fire", StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        _lastFireExplosionFxFrame = Time.frameCount;
                        _lastFireExplosionFxPos = pos;
                    }

                    if (string.IsNullOrEmpty(impact_prefab_name) ||
                        !string.Equals(impact_prefab_name, "ImpactFX_flesh", StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    if (!VisionConfig.EnableBloodSmokeWaterOcclusion.Value)
                    {
                        if (_modifiedBloodRenderers.Count > 0)
                            RestoreBloodRendererDefaults();
                        return;
                    }

                    if (!AiCore.TryGetActiveCameraPosition(out Vector3 camPos))
                        return;

                    if (maxSpawnDistance > 0)
                    {
                        float maxDistSq = (float)maxSpawnDistance * (float)maxSpawnDistance;
                        if ((pos - camPos).sqrMagnitude > maxDistSq)
                            return;
                    }

                    if (AiCore.IsPointOccludedByWaterOrSmoke(camPos, pos))
                    {
                        impact_prefab_name = "ImpactFX_flesh_noBlood";
                        BloodSmokeWaterOccludedCount++;
                        return;
                    }

                    if (gameObjectFx == null)
                        gameObjectFx = GetOrCreateBloodFxCallback();
                }
                catch { }
            }

            public static void Postfix(long __state)
            {
                HitchProfiler.End(HitchBucket.BloodImpactFx, __state);
            }
        }

        // ============================================================================
        // 7. TACTICAL RELOAD & BIPOD ANIMATION CANCEL (LOCAL HUMAN PLAYER ONLY)
        // Allows emergency interrupting reloads or mounted bipod states via weapon switch,
        // melee, knife pullout, grenade throw, vault, single-bullet fire interrupt, or a brief
        // held sprint, while preserving ammo/magazine state with zero fast-reload exploits.
        // ============================================================================
        public static int TacticalReloadCancelsCount;

        private static GenericGun._ReloadCR_d__159 _activeLocalReloadEnum;
        private static IntPtr _activeLocalReloadSoldierPtr;
        private static IntPtr _canceledReloadEnumPtr;
        private static float _localSprintCancelHoldSeconds;

        private static void ResetTacticalReloadCancelState()
        {
            _activeLocalReloadEnum = null;
            _activeLocalReloadSoldierPtr = IntPtr.Zero;
            _canceledReloadEnumPtr = IntPtr.Zero;
            _localSprintCancelHoldSeconds = 0f;
        }

        private static bool IsLocalControlledHumanSoldier(Soldier s)
        {
            if (s == null || s.Pointer == IntPtr.Zero || !s.IsAlive)
                return false;

            Soldier current = Soldier.CurrentControlledSoldierOrNull();
            return current != null &&
                   current.Pointer == s.Pointer &&
                   AiCore.IsLocallyOwnedSoldier(s);
        }

        private static bool IsSoldierCurrentlyReloading(Soldier s, out GenericGun heldGun)
        {
            heldGun = s.GetHeldGun(0);
            return s.IsReloading || (heldGun != null && heldGun.IsReloading);
        }

        private static bool TryCancelLocalPlayerReload(Soldier player, bool undeployBipodIfMounted, bool restoreHoldPose)
        {
            if (!VisionConfig.EnableMod.Value || !VisionConfig.EnableTacticalReloadAndBipodCancel.Value)
                return false;

            if (!IsLocalControlledHumanSoldier(player))
                return false;

            if (!IsSoldierCurrentlyReloading(player, out GenericGun heldGun))
                return false;

            GenericGun targetGun = heldGun;
            GenericGun._ReloadCR_d__159 activeEnum = _activeLocalReloadEnum;
            if (activeEnum != null &&
                activeEnum.Pointer != IntPtr.Zero &&
                _activeLocalReloadSoldierPtr == player.Pointer)
            {
                GenericGun enumGun = activeEnum.__4__this;
                if (enumGun != null && (targetGun == null || enumGun.Pointer == targetGun.Pointer))
                {
                    if (targetGun == null)
                        targetGun = enumGun;

                    int st = activeEnum.__1__state;
                    // States 7 & 8 occur after ReplaceWithMagazineOrAmmo already swapped the magazine in inventory.
                    // Finish moving magazine rounds into the gun and apply remaining tail cooldown to nextAvailableShot
                    // so neither the swapped magazine is lost nor LMB can fast-reload exploit.
                    if (st == 7 || st == 8)
                    {
                        try
                        {
                            targetGun.MoveAmmosFromMagazineToWeapon();
                            float remTail = Mathf.Clamp(activeEnum._len_5__13 - activeEnum._firstHalfLenght_5__15, 0.25f, 1.75f);
                            targetGun.nextAvailableShot = Mathf.Max(targetGun.nextAvailableShot, Time.time + remTail);
                            if (activeEnum._is_completly_empty_5__7 && targetGun.isManualBoltOperated && targetGun.boltActionBeforeFiring)
                                targetGun.bolt_required = true;
                        }
                        catch { }
                    }

                    _canceledReloadEnumPtr = activeEnum.Pointer;
                    activeEnum.__1__state = -1;
                }
            }

            _activeLocalReloadEnum = null;
            _activeLocalReloadSoldierPtr = IntPtr.Zero;
            _localSprintCancelHoldSeconds = 0f;

            try { if (targetGun != null) targetGun.TryInterruptReload(); } catch { }
            try { player.TryInterruptReload(); } catch { }

            if (targetGun != null)
            {
                try
                {
                    targetGun.IsReloading = false;
                    targetGun.usingBolt = false;
                    targetGun.OnBipodUndeployed();
                    if (targetGun.audioSource != null && targetGun.audioSource.isPlaying)
                        targetGun.audioSource.Stop();
                }
                catch { }
            }

            try
            {
                player.IsReloading = false;
                player.stopHandsAnim_end = 0f;
                player.RemovePreviouslyAttached();
                player.FixMagazinePosition();
            }
            catch { }

            if (undeployBipodIfMounted && player.usingBipod)
            {
                try { player.PutAwayBipod(0.25f, false, false, false); } catch { }
            }

            if (restoreHoldPose && targetGun != null)
            {
                try
                {
                    FPSGunManager fg = player.fpsGun != null ? player.fpsGun : player.GetFPSGunManager();
                    if (fg != null)
                        fg.FixOnGunHoldPose(targetGun, player.usingBipod);
                }
                catch { }
            }

            try { PlayerGUI.OnPlayerUiStatsChanged(); } catch { }
            TacticalReloadCancelsCount++;
            return true;
        }

        [HarmonyPatch(typeof(GenericGun), nameof(GenericGun.ReloadCR))]
        public static class GenericGun_ReloadCR_Patch
        {
            public static void Postfix(Soldier soldier, Il2CppSystem.Collections.IEnumerator __result)
            {
                try
                {
                    if (__result == null || !IsLocalControlledHumanSoldier(soldier))
                        return;

                    var cr = __result.TryCast<GenericGun._ReloadCR_d__159>();
                    if (cr != null && cr.Pointer != IntPtr.Zero)
                    {
                        _activeLocalReloadEnum = cr;
                        _activeLocalReloadSoldierPtr = soldier.Pointer;
                        _canceledReloadEnumPtr = IntPtr.Zero;
                        _localSprintCancelHoldSeconds = 0f;
                    }
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(GenericGun._ReloadCR_d__159), nameof(GenericGun._ReloadCR_d__159.MoveNext))]
        public static class GenericGun_ReloadCRMoveNext_Patch
        {
            public static bool Prefix(GenericGun._ReloadCR_d__159 __instance, ref bool __result)
            {
                try
                {
                    if (__instance != null && __instance.Pointer == _canceledReloadEnumPtr)
                    {
                        __instance.__1__state = -1;
                        __result = false;
                        return false;
                    }
                }
                catch { }
                return true;
            }
        }

        [HarmonyPatch(typeof(Soldier), nameof(Soldier.SwitchTo))]
        public static class Soldier_SwitchTo_ReloadCancel_Patch
        {
            public static void Prefix(Soldier __instance, int wearedItemIndex)
            {
                try
                {
                    if (__instance == null || wearedItemIndex == 0 || !__instance.HasHeldItem(wearedItemIndex))
                        return;
                    if (IsSoldierCurrentlyReloading(__instance, out _))
                        TryCancelLocalPlayerReload(__instance, undeployBipodIfMounted: true, restoreHoldPose: false);
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(Soldier), nameof(Soldier.Melee))]
        public static class Soldier_Melee_ReloadCancel_Patch
        {
            public static void Prefix(Soldier __instance)
            {
                try
                {
                    if (__instance != null && IsSoldierCurrentlyReloading(__instance, out _))
                        TryCancelLocalPlayerReload(__instance, undeployBipodIfMounted: true, restoreHoldPose: true);
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(Soldier), nameof(Soldier.ToggleInventoryKnife))]
        public static class Soldier_ToggleInventoryKnife_ReloadCancel_Patch
        {
            public static void Prefix(Soldier __instance)
            {
                try
                {
                    if (__instance != null &&
                        (__instance.HoldsInventoryItem || __instance.InventoryKnife != null) &&
                        IsSoldierCurrentlyReloading(__instance, out _))
                    {
                        TryCancelLocalPlayerReload(__instance, undeployBipodIfMounted: true, restoreHoldPose: false);
                    }
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(Soldier), nameof(Soldier.CycleInventoryKnife))]
        public static class Soldier_CycleInventoryKnife_ReloadCancel_Patch
        {
            public static void Prefix(Soldier __instance)
            {
                try
                {
                    if (__instance != null &&
                        __instance.InventoryKnife != null &&
                        IsSoldierCurrentlyReloading(__instance, out _))
                    {
                        TryCancelLocalPlayerReload(__instance, undeployBipodIfMounted: true, restoreHoldPose: false);
                    }
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(Soldier), nameof(Soldier.Throw))]
        public static class Soldier_Throw_ReloadCancel_Patch
        {
            public static void Prefix(Soldier __instance, VirtualItem item)
            {
                try
                {
                    if (__instance != null && item != null && IsSoldierCurrentlyReloading(__instance, out _))
                        TryCancelLocalPlayerReload(__instance, undeployBipodIfMounted: true, restoreHoldPose: false);
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(Soldier), nameof(Soldier.Vault))]
        public static class Soldier_Vault_ReloadCancel_Patch
        {
            public static void Prefix(Soldier __instance)
            {
                try
                {
                    if (__instance != null && IsSoldierCurrentlyReloading(__instance, out _))
                        TryCancelLocalPlayerReload(__instance, undeployBipodIfMounted: true, restoreHoldPose: true);
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(PlayerController), nameof(PlayerController.Update))]
        public static class PlayerController_Update_ReloadAndBipodCancel_Patch
        {
            private static Func<VirtualGamepad, GameInput, StickPressCondition, bool> _vgpGetButton;
            private static Func<VirtualGamepad, GameInput, StickPressCondition, bool> _vgpGetButtonDown;
            private static bool _vgpDelegatesInitialized;

            private static void EnsureGamepadDelegates()
            {
                if (_vgpDelegatesInitialized)
                    return;
                _vgpDelegatesInitialized = true;
                try
                {
                    Type vgpType = typeof(VirtualGamepad);
                    Type[] args = { typeof(GameInput), typeof(StickPressCondition) };
                    var mGet = vgpType.GetMethod(
                        "GetButton",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance,
                        null, args, null);
                    if (mGet != null)
                        _vgpGetButton = (Func<VirtualGamepad, GameInput, StickPressCondition, bool>)Delegate.CreateDelegate(
                            typeof(Func<VirtualGamepad, GameInput, StickPressCondition, bool>), mGet);

                    var mDown = vgpType.GetMethod(
                        "GetButtonDown",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance,
                        null, args, null);
                    if (mDown != null)
                        _vgpGetButtonDown = (Func<VirtualGamepad, GameInput, StickPressCondition, bool>)Delegate.CreateDelegate(
                            typeof(Func<VirtualGamepad, GameInput, StickPressCondition, bool>), mDown);
                }
                catch { }
            }

            public static void Prefix(PlayerController __instance)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value || !VisionConfig.EnableTacticalReloadAndBipodCancel.Value)
                    {
                        _localSprintCancelHoldSeconds = 0f;
                        return;
                    }

                    if (__instance == null || Time.timeScale <= 0.01f || __instance.lockRotationAndFiring)
                    {
                        _localSprintCancelHoldSeconds = 0f;
                        return;
                    }

                    Soldier player = __instance.ControlledCharacter;
                    if (player == null || player.Pointer == IntPtr.Zero || !player.IsAlive || player.IsOnVehicle() || !AiCore.IsLocallyOwnedSoldier(player))
                    {
                        _localSprintCancelHoldSeconds = 0f;
                        return;
                    }

                    if (!IsSoldierCurrentlyReloading(player, out GenericGun heldGun))
                    {
                        _localSprintCancelHoldSeconds = 0f;
                        return;
                    }

                    VirtualGamepad gpad = GamepadsAPI.GetGamepad(0);
                    if (gpad == null)
                    {
                        _localSprintCancelHoldSeconds = 0f;
                        return;
                    }

                    EnsureGamepadDelegates();
                    if (_vgpGetButton == null || _vgpGetButtonDown == null)
                    {
                        _localSprintCancelHoldSeconds = 0f;
                        return;
                    }

                    // 1) Mounted bipod escape while reloading (never pin player onto bipod until reload ends)
                    if (player.usingBipod)
                    {
                        bool wantsDismount =
                            _vgpGetButtonDown(gpad, GameInput.Crouch, StickPressCondition.All) ||
                            _vgpGetButtonDown(gpad, GameInput.Prone, StickPressCondition.All) ||
                            _vgpGetButtonDown(gpad, GameInput.Jump, StickPressCondition.All) ||
                            (_vgpGetButton(gpad, GameInput.MoveBack, StickPressCondition.All) &&
                             _vgpGetButton(gpad, GameInput.Sprint, StickPressCondition.All));
                        if (wantsDismount)
                        {
                            TryCancelLocalPlayerReload(player, undeployBipodIfMounted: true, restoreHoldPose: true);
                            return;
                        }
                    }

                    // 2) Fire-key interrupt strictly for single-bullet / clip top-off reloads once >= 1 round is chambered/loaded
                    if (_vgpGetButtonDown(gpad, GameInput.Fire, StickPressCondition.All) &&
                        _activeLocalReloadEnum == null &&
                        heldGun != null &&
                        heldGun.GetCurrentAmmoCount() > 0)
                    {
                        try { heldGun.TryInterruptReload(); } catch { }
                        try { player.TryInterruptReload(); } catch { }
                        _localSprintCancelHoldSeconds = 0f;
                        return;
                    }

                    // 3) Deliberate held-sprint reload breakout (0.18s forward + sprint hold prevents accidental tap-cancels)
                    if (!player.IsCrawling &&
                        _vgpGetButton(gpad, GameInput.Sprint, StickPressCondition.All) &&
                        _vgpGetButton(gpad, GameInput.MoveForward, StickPressCondition.All))
                    {
                        _localSprintCancelHoldSeconds += Time.unscaledDeltaTime;
                        if (_localSprintCancelHoldSeconds >= 0.18f)
                        {
                            _localSprintCancelHoldSeconds = 0f;
                            TryCancelLocalPlayerReload(player, undeployBipodIfMounted: true, restoreHoldPose: true);
                        }
                    }
                    else
                    {
                        _localSprintCancelHoldSeconds = 0f;
                    }
                }
                catch { }
            }
        }
    }
}
