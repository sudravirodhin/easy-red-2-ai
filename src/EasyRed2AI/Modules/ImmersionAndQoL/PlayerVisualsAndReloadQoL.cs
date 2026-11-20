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
        //      Full Tactical -> Objectives Only -> Clean Cinema -> Command Net Overhead
        //  - Keeps both MissionStatusGUI (top bar) and ObjectiveGUI (3D A/B/C markers)
        //    live in Spectator Mode while filtering out private MissionTaskRetreat tasks.
        //  - Displays human-readable Faction + Side labels (e.g. "Germany (Axis)",
        //    "USSR (Allies)") flanking the Left and Right flags on the Objective Bar.
        // ============================================================================
        internal enum SpectatorTacticalHudMode : byte
        {
            FullTactical = 0,
            ObjectivesOnly = 1,
            CleanCinema = 2,
            CommandNetOverview = 3
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

        internal static bool IsCommandNetOverviewAllowed()
        {
            if (!IsSpectatorCameraModeActive(out _, out bool canShowOverlays) || !canShowOverlays)
                return false;

            // Anti-ESP guard in Multiplayer: only allow Command Net Overhead when not controlling a living soldier
            if (PhotonNetwork.InRoom)
            {
                Soldier controlled = Soldier.CurrentControlledSoldierOrNull();
                if (controlled != null && controlled.IsAlive)
                    return false;
            }

            return true;
        }

        internal static string CycleSpectatorTacticalHudMode()
        {
            ActiveSpectatorHudMode = (SpectatorTacticalHudMode)(((int)ActiveSpectatorHudMode + 1) % 4);
            if (ActiveSpectatorHudMode == SpectatorTacticalHudMode.CommandNetOverview && !IsCommandNetOverviewAllowed())
                ActiveSpectatorHudMode = SpectatorTacticalHudMode.FullTactical;

            MaintainSpectatorHudVisibility();
            switch (ActiveSpectatorHudMode)
            {
                case SpectatorTacticalHudMode.FullTactical:
                    return "[Battle Hardened] Spectator View (F): FULL TACTICAL (Objectives + Bot GOAP Inspector)";
                case SpectatorTacticalHudMode.ObjectivesOnly:
                    return "[Battle Hardened] Spectator View (F): OBJECTIVES ONLY (Objective Bar + 3D Markers)";
                case SpectatorTacticalHudMode.CommandNetOverview:
                    return "[Battle Hardened] Spectator View (F): COMMAND NET OVERHEAD (Yellow/Red Meshes + Cyan Routes + Net/SL Readout)";
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
            if (!VisionConfig.EnableSpectatorHudRetention.Value)
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
                    bool modEnabled = VisionConfig.EnableMod.Value;
                    if (__instance == null || __instance.panel == null)
                    {
                        return;
                    }

                    bool isSpectator = IsSpectatorCameraModeActive(out _, out bool canShowOverlays);
                    if (!modEnabled && !isSpectator)
                    {
                        return;
                    }

                    if (isSpectator)
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

                    if ((modEnabled || isSpectator) && __instance.panel.activeSelf)
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
                    if (__instance == null || __instance.guiPanel == null)
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

        // Guards native ObjectiveGUI.SetPhase (0x1804BB9F0) -> ObjectiveGUI.UpdateGUI (0x1804BBD80) against infinite
        // recursion (0xC00000FD STATUS_STACK_OVERFLOW): in UpdateGUI, Around phase tests localPos.x/canvasPos.x signs
        // and EllipseFrac < 0.96 without checking localPos.z > 0f, ping-ponging InFront <-> Around when an objective
        // behind the camera crosses the vertical centerline.
        [HarmonyPatch(typeof(ObjectiveGUI), nameof(ObjectiveGUI.SetPhase))]
        public static class ObjectiveGUI_SetPhase_Patch
        {
            [ThreadStatic]
            private static int _objectiveGuiSetPhaseDepth;

            public static bool Prefix(ObjectiveGUI __instance, ObjectiveGuiPhase __0, out bool __state)
            {
                __state = false;
                try
                {
                    if (_objectiveGuiSetPhaseDepth >= 2)
                        return false;

                    if (__0 == ObjectiveGuiPhase.InFront &&
                        __instance != null &&
                        __instance.currentTask != null)
                    {
                        Camera cam = ResourcesManager.mainCamera;
                        if (cam != null && cam.transform != null)
                        {
                            Vector3 localPos = cam.transform.InverseTransformPoint(__instance.GetTaskDestination());
                            if (localPos.z <= 0.01f)
                                return false;
                        }
                    }

                    _objectiveGuiSetPhaseDepth++;
                    __state = true;
                }
                catch { }
                return true;
            }

            public static void Finalizer(Exception __exception, bool __state)
            {
                if (__state && _objectiveGuiSetPhaseDepth > 0)
                    _objectiveGuiSetPhaseDepth--;
            }
        }
        // ============================================================================
        // 5. VANILLA VISUAL FIX: SMOKE & WATER BLOOD SPRAY OCCLUSION FIX
        // Eliminates disembodied "ghost" blood sprays floating on top of smoke or water:
        //  1) Underwater & Cross-Water Hits: BulletInstance.OnHit sets onWater = true
        //     only when hitting Layer 4 (Water), so bullets hitting submerged/wading
        //     Soldier BodyPart colliders spawn airborne "ImpactFX_flesh" underwater.
        //  2) Tactical Smoke Volumes: ResourcesManager.PlayImpactEffect checks camera
        //     frustum (PointInCameraView) without checking smoke-collider LOS, causing
        //     red blood clouds to pop over opaque smoke screens. Swapping to vanilla's
        //     pooled "ImpactFX_flesh_noBlood" preserves 100% of bullet-flesh impact audio.
        //  3) Ambient Map, Burning Vehicle & Explosion Smoke Depth Sorting: Normalizes
        //     pooled ImpactFX_flesh ParticleSystemRenderers once per pooled instance
        //     (renderQueue = 2990 [Transparent-10], sortingOrder = -10, sortingFudge = +40m)
        //     so blood still renders in the Transparent pass after all opaque/cutout world
        //     geometry (<= 2450), while every Queue=3000 smoke/fire/dust/water particle
        //     system is guaranteed to draw AFTER (over) blood mist regardless of emitter distance.
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

                    int targetOrder = origOrder > -10 ? -10 : origOrder;
                    float targetFudge = origFudge < 40.0f ? 40.0f : origFudge;
                    bool shiftQueueBeforeSmoke = hasMat && origQueue >= 2990;
                    int targetQueue = shiftQueueBeforeSmoke ? 2990 : origQueue;

                    if (targetOrder != origOrder || !Mathf.Approximately(targetFudge, origFudge) || (hasMat && targetQueue != origQueue))
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
                        if (hasMat && targetQueue != origQueue)
                            sharedMat.renderQueue = targetQueue;

                        if (!_loggedBloodRendererNormalization)
                        {
                            _loggedBloodRendererNormalization = true;
                            AiCore.LogDebug(
                                "BloodVfx",
                                $"Normalized pooled ImpactFX_flesh renderer '{psr.name}': order {origOrder}->{targetOrder}, fudge {origFudge:F1}->{targetFudge:F1}, queue {origQueue}->{targetQueue}",
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
