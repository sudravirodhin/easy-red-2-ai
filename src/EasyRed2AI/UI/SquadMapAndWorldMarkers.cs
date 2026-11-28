using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace EasyRed2HumanAI
{
    public static partial class VeteranAndTacticsPatches
    {
        private const string HqAcceptRadialSentinel = "__BH_HQ_ACCEPT__";
        private const string HqBalkRadialSentinel = "__BH_HQ_BALK__";

        /// <summary>
        /// Renders a pooled native <c>[HQ]</c> preview marker on the <c>M</c> tactical map whenever the local
        /// Player Squad Leader has an active unaccepted HQ / Radio Net order.
        /// </summary>
        internal struct MiniMapMarkerTextCache
        {
            public UnityEngine.UI.Text Txt;
            public GameObject Go;
        }

        internal sealed class ActiveSquad3DLabel
        {
            public IntPtr OwnerKeyPtr;
            public Marker3DGUI.Entry Entry;
            public UnityEngine.UI.RawImage Img;
            public GameObject ImgGo;
            public UnityEngine.UI.Text Txt;
            public GameObject TxtGo;
            public string CachedLetter;
            public Color BaseColor;
            public float LastAlpha;
            public float NextRefreshTime;
            public bool LastActive;
            public bool IsNonSquadKey;
            public bool IsSuppressedSquad;
        }

        internal static readonly Dictionary<IntPtr, MiniMapMarkerTextCache> _minimapMarkerTextByImgPtr =
            new Dictionary<IntPtr, MiniMapMarkerTextCache>(64);
        internal static UnityEngine.UI.Text _lastUnitMarkerTextComp;
        private static GameObject _lastUnitMarkerTextGo;
        internal static int _lastUsedUnitMarkerIndex = -1;
        internal static bool _insideMiniMapUpdate;
        private static Sprite _cachedMkrTankBlue;
        private static Sprite _cachedMkrTankRed;
        private static Sprite _cachedMkrPlaneBlue;
        private static Sprite _cachedMkrPlaneRed;
        private static Sprite _cachedMkrMechBlue;
        private static Sprite _cachedMkrMechRed;

        internal static void ResetVehicleMarkerSpriteCache()
        {
            _cachedMkrTankBlue = null;
            _cachedMkrTankRed = null;
            _cachedMkrPlaneBlue = null;
            _cachedMkrPlaneRed = null;
            _cachedMkrMechBlue = null;
            _cachedMkrMechRed = null;
        }

        [HarmonyPatch(typeof(MiniMapGUI), nameof(MiniMapGUI.GetNextUnitMarker))]
        public static class MiniMapGUI_GetNextUnitMarker_Patch
        {
            public static void Postfix(MiniMapGUI __instance, string __0, bool __1, UnityEngine.UI.Image __result)
            {
                try
                {
                    _lastUnitMarkerTextComp = null;
                    _lastUnitMarkerTextGo = null;
                    if (!VisionConfig.EnableMod.Value ||
                        __instance == null ||
                        __result == null ||
                        __result.Pointer == IntPtr.Zero)
                    {
                        return;
                    }

                    _lastUsedUnitMarkerIndex = __instance.usedMarker_index;

                    // Fix vanilla pooled marker reuse bug (0x1804A1D20 in MiniMapGUI.asm): when reusing a pooled
                    // child marker, vanilla returns early without updating img.enabled or child(0) Text state!
                    if (__result.enabled != __1)
                        __result.enabled = __1;

                    IntPtr imgPtr = __result.Pointer;
                    if (!_minimapMarkerTextByImgPtr.TryGetValue(imgPtr, out MiniMapMarkerTextCache txtCache) ||
                        ReferenceEquals(txtCache.Txt, null) ||
                        txtCache.Txt == null)
                    {
                        UnityEngine.UI.Text txtResolved = null;
                        GameObject goResolved = null;
                        Transform tr = __result.transform;
                        if (tr != null && tr.childCount > 0)
                        {
                            Transform child0 = tr.GetChild(0);
                            if (child0 != null)
                            {
                                txtResolved = child0.GetComponent<UnityEngine.UI.Text>();
                                if (txtResolved != null)
                                    goResolved = txtResolved.gameObject;
                            }
                        }
                        if (_minimapMarkerTextByImgPtr.Count > 256)
                            _minimapMarkerTextByImgPtr.Clear();
                        txtCache = new MiniMapMarkerTextCache { Txt = txtResolved, Go = goResolved };
                        _minimapMarkerTextByImgPtr[imgPtr] = txtCache;
                    }

                    UnityEngine.UI.Text txt = txtCache.Txt;
                    GameObject txtGo = txtCache.Go;
                    _lastUnitMarkerTextComp = txt;
                    _lastUnitMarkerTextGo = txtGo;
                    if (txt != null && !ReferenceEquals(txtGo, null) && txtGo != null)
                    {
                        bool hasText = !string.IsNullOrEmpty(__0);
                        if (txtGo.activeSelf != hasText)
                            txtGo.SetActive(hasText);
                        if (hasText && txt.text != __0)
                            txt.text = __0;
                        txt.color = Color.white;
                    }
                }
                catch { }
            }
        }

        private static byte ResolveLiveVehicleMarkerKind(Squad sq)
        {
            if (sq == null || sq.Pointer == IntPtr.Zero)
                return AiCore.VehicleMarkerKindNone;

            if (AiCore.TryGetSquadSnapshot(sq.Pointer, out AiCore.SquadSnapshot sqSnap))
                return sqSnap.ActiveVehicleMarkerKind;

            Soldier ldr = sq.Leader;
            if (ldr != null && ldr.IsAlive && ldr.IsOnVehicle())
            {
                Vehicle v = ldr.GetCurrentVehicle();
                if (v != null && v.Pointer != IntPtr.Zero && !v.IsDisabled())
                    return AiCore.GetVehicleMarkerKind(v);
            }
            return AiCore.VehicleMarkerKindNone;
        }

        private static Sprite GetOrLoadSquadIcon(ref Sprite cached, string squadCode, bool isFriendly)
        {
            if (!ReferenceEquals(cached, null) && cached != null)
                return cached;
            cached = SquadEditorScene.GetSquadIcon(squadCode, isFriendly);
            return cached;
        }

        private static Sprite GetCachedVehicleOverrideMarkerSprite(byte vehicleMarkerKind, bool isFriendly)
        {
            switch (vehicleMarkerKind)
            {
                case AiCore.VehicleMarkerKindPlane:
                    return isFriendly
                        ? GetOrLoadSquadIcon(ref _cachedMkrPlaneBlue, "pilot_crew", true)
                        : GetOrLoadSquadIcon(ref _cachedMkrPlaneRed, "pilot_crew", false);

                case AiCore.VehicleMarkerKindTank:
                    return isFriendly
                        ? GetOrLoadSquadIcon(ref _cachedMkrTankBlue, "tank_crew", true)
                        : GetOrLoadSquadIcon(ref _cachedMkrTankRed, "tank_crew", false);

                case AiCore.VehicleMarkerKindMechanized:
                    return isFriendly
                        ? GetOrLoadSquadIcon(ref _cachedMkrMechBlue, "mechanized_section", true)
                        : GetOrLoadSquadIcon(ref _cachedMkrMechRed, "mechanized_section", false);

                default:
                    return null;
            }
        }

        [HarmonyPatch(typeof(Squad), nameof(Squad.GetMarkerIcon))]
        public static class Squad_GetMarkerIcon_Patch
        {
            public static void Postfix(Squad __instance, bool __0, ref Sprite __result)
            {
                try
                {
                    UnityEngine.UI.Text txt = _lastUnitMarkerTextComp;
                    GameObject txtGo = _lastUnitMarkerTextGo;
                    _lastUnitMarkerTextComp = null;
                    _lastUnitMarkerTextGo = null;

                    if (VisionConfig.EnableMod.Value && __instance != null && __instance.Pointer != IntPtr.Zero)
                    {
                        byte liveVehKind = ResolveLiveVehicleMarkerKind(__instance);
                        if (liveVehKind != AiCore.VehicleMarkerKindNone)
                        {
                            string overrideCode = null;
                            if (liveVehKind == AiCore.VehicleMarkerKindPlane)
                                overrideCode = "pilot_crew";
                            else if (liveVehKind == AiCore.VehicleMarkerKindTank)
                                overrideCode = "tank_crew";
                            else if (liveVehKind == AiCore.VehicleMarkerKindMechanized)
                                overrideCode = "mechanized_section";

                            if (overrideCode != null && !string.Equals(__instance.squadCode, overrideCode, StringComparison.Ordinal))
                            {
                                Sprite vehSprite = GetCachedVehicleOverrideMarkerSprite(liveVehKind, __0);
                                if (!ReferenceEquals(vehSprite, null) && vehSprite != null)
                                    __result = vehSprite;
                            }
                        }
                    }

                    if (!_insideMiniMapUpdate ||
                        !__0 ||
                        !VisionConfig.EnableMod.Value ||
                        !VisionConfig.EnableMapSquadLabels.Value ||
                        __instance == null ||
                        !IsFriendlySquadToLocalPlayer(__instance) ||
                        txt == null ||
                        ReferenceEquals(txtGo, null) ||
                        txtGo == null)
                    {
                        return;
                    }

                    string sqName = __instance.squadName;
                    if (string.IsNullOrEmpty(sqName))
                        return;

                    string letter = sqName.Length == 1 ? sqName : sqName.Substring(0, 1);
                    if (!txtGo.activeSelf)
                        txtGo.SetActive(true);
                    if (txt.text != letter)
                        txt.text = letter;
                    txt.color = GetSquadLetterColor(__instance, 0.96f);
                }
                catch { }
            }
        }

        internal static bool IsFriendlySquadToLocalPlayer(Squad sq)
        {
            if (sq == null || sq.Pointer == IntPtr.Zero)
                return false;

            AiCore.LocalPlayerFastHandles lp = AiCore.GetCachedLocalPlayerHandles();
            if (!lp.HasPlayer || string.IsNullOrEmpty(lp.Faction))
                return true;

            string sqFac = null;
            if (AiCore.WorldSquadCount > 0 &&
                AiCore.TryGetSquadSnapshot(sq.Pointer, out AiCore.SquadSnapshot sqSnap) &&
                !string.IsNullOrEmpty(sqSnap.Faction))
            {
                sqFac = sqSnap.Faction;
            }
            else
            {
                Soldier leader = sq.Leader;
                if (leader != null && !string.IsNullOrEmpty(leader.faction))
                {
                    sqFac = leader.faction;
                }
                else if (sq.units != null)
                {
                    var units = sq.units;
                    int cnt = units.Count;
                    for (int i = 0; i < cnt; i++)
                    {
                        Soldier u = units[i];
                        if (u != null && !string.IsNullOrEmpty(u.faction))
                        {
                            sqFac = u.faction;
                            break;
                        }
                    }
                }
            }

            if (string.IsNullOrEmpty(sqFac))
                return false;

            return AiCore.AreSameSideFactions(lp.Faction, sqFac);
        }

        internal static Color GetSquadLetterColor(Squad sq, float alpha = 0.96f)
        {
            AiCore.LocalPlayerFastHandles lp = AiCore.GetCachedLocalPlayerHandles();
            bool isMySquad = lp.HasPlayer &&
                             lp.SquadPtr != IntPtr.Zero &&
                             sq != null &&
                             sq.Pointer != IntPtr.Zero &&
                             lp.SquadPtr == sq.Pointer;
            return isMySquad
                ? new Color(1.0f, 0.88f, 0.25f, alpha)
                : new Color(1.0f, 1.0f, 1.0f, alpha);
        }

        internal static Marker3DGUI.Entry _lastPooledMarker3DEntry;
        internal static readonly Dictionary<IntPtr, Marker3DGUI.Entry> _squad3DMarkerEntries =
            new Dictionary<IntPtr, Marker3DGUI.Entry>(32);
        internal static readonly Dictionary<IntPtr, IntPtr> _marker3DEntryOwnerByEntryPtr =
            new Dictionary<IntPtr, IntPtr>(64);
        internal static readonly Dictionary<IntPtr, MiniMapMarkerTextCache> _marker3DEntryTexts =
            new Dictionary<IntPtr, MiniMapMarkerTextCache>(64);
        internal static readonly Dictionary<IntPtr, ActiveSquad3DLabel> _activeSquad3DLabels =
            new Dictionary<IntPtr, ActiveSquad3DLabel>(32);
        private static Font _cachedSquad3DFont;
        private static IntPtr _lastLocalHudVehiclePtr;
        private static IntPtr _lastLocalHudVehicleTaskPtr;
        private static float _nextLocalHudVehicleCheckTime;

        private static bool HasActiveVehicleSquadObjectiveTask(Squad sq)
        {
            return sq != null &&
                   sq.currentTask != null &&
                   sq.currentTask.Pointer != IntPtr.Zero &&
                   sq.currentTask.TryCast<MissionTaskRetreat>() == null;
        }

        private static void RefreshVehicleSquadOrderIcon()
        {
            if (!VisionConfig.EnableMod.Value)
                return;

            PlayerGUI gui = PlayerGUI.instance;
            PlayerController pc = PlayerController.currentController;
            Squad sq = PlayerGUI.squad;
            if (gui == null ||
                gui.squadOrderPosIcon == null ||
                pc == null ||
                pc.ControlledCharacter == null ||
                pc.ControlledVehicle == null ||
                sq == null ||
                sq.Leader == null)
            {
                return;
            }

            if (sq.order == Order.follow && !sq.InChargeTime())
            {
                if (HasActiveVehicleSquadObjectiveTask(sq))
                {
                    if (!gui.squadOrderPosIcon.enabled)
                        gui.squadOrderPosIcon.enabled = true;

                    Sprite taskSprite = null;
                    try { taskSprite = sq.currentTask.GetTaskIcon(); } catch { }
                    if (taskSprite == null)
                    {
                        try { taskSprite = ItemsDatabase.LoadAndCacheSprite("order_move", "er2gui", null); } catch { }
                    }
                    if (taskSprite != null)
                        gui.squadOrderPosIcon.sprite = taskSprite;
                }
                else
                {
                    if (gui.squadOrderPosIcon.enabled)
                        gui.squadOrderPosIcon.enabled = false;
                }
            }
        }

        [HarmonyPatch(typeof(PlayerGUI), nameof(PlayerGUI.UpdateSquadGUI))]
        public static class PlayerGUI_UpdateSquadGUI_Patch
        {
            public static void Postfix()
            {
                try
                {
                    RefreshVehicleSquadOrderIcon();
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(PlayerGUI), nameof(PlayerGUI.GetSquadOrderPosition))]
        public static class PlayerGUI_GetSquadOrderPosition_Patch
        {
            public static void Postfix(ref Vector2 __0, ref bool __result)
            {
                try
                {
                    if (__result || !VisionConfig.EnableMod.Value)
                        return;

                    AiCore.LocalPlayerFastHandles lp = AiCore.GetCachedLocalPlayerHandles();
                    if (!lp.HasControlledCharacter || lp.ControlledVehicle == null)
                        return;

                    Squad sq = lp.Squad ?? PlayerGUI.squad;
                    if (sq == null || sq.Leader == null)
                        return;

                    Vector3 worldPos;
                    if (sq.order != Order.follow)
                    {
                        Soldier cc = lp.Soldier;
                        if (cc.HasDestinationAssigned &&
                            AiCore.GetRawTargetDestinationPtr(cc) != IntPtr.Zero &&
                            (!sq.Leader.IsPlayer() || cc.targetDestination.TryCast<DestinationWaypoint>() != null))
                        {
                            worldPos = cc.targetDestination.GetCoverPosition() + Vector3.up;
                        }
                        else
                        {
                            worldPos = sq.moveOrderPosition + (Vector3.up * 0.7f);
                        }
                    }
                    else if (HasActiveVehicleSquadObjectiveTask(sq))
                    {
                        worldPos = sq.currentTask.GetTaskDestination() + (Vector3.up * 1.5f);
                    }
                    else
                    {
                        return;
                    }

                    Camera cam = ResourcesManager.mainCamera;
                    if (cam == null || cam.transform == null)
                        return;

                    Vector3 localPos = cam.transform.InverseTransformPoint(worldPos);
                    if (localPos.z <= 0f)
                    {
                        __0 = new Vector2(-2000f, -2000f);
                        __result = true;
                        return;
                    }

                    __0 = ResourcesManager.GetCanvasPosition(worldPos);
                    __result = true;
                }
                catch { }
            }
        }

        private static Font GetSquad3DLabelFont()
        {
            if (_cachedSquad3DFont != null)
                return _cachedSquad3DFont;

            try
            {
                PlayerGUI pgui = PlayerGUI.instance;
                if (pgui != null && pgui.squadLabelPrefab != null)
                {
                    SquadSoldierLabel lbl = pgui.squadLabelPrefab.GetComponent<SquadSoldierLabel>();
                    if (lbl != null && lbl.name_surname != null && lbl.name_surname.font != null)
                    {
                        _cachedSquad3DFont = lbl.name_surname.font;
                        return _cachedSquad3DFont;
                    }
                }
            }
            catch { }

            try
            {
                _cachedSquad3DFont = Resources.GetBuiltinResource<Font>("Arial.ttf");
            }
            catch { }
            return _cachedSquad3DFont;
        }

        private static UnityEngine.UI.Text GetOrCreateMarker3DSquadText(
            Marker3DGUI.Entry entry,
            UnityEngine.UI.RawImage entryImg,
            out GameObject txtGo)
        {
            txtGo = null;
            if (entry == null || ReferenceEquals(entryImg, null) || entryImg == null)
                return null;

            IntPtr entryPtr = entry.Pointer;
            if (_marker3DEntryTexts.TryGetValue(entryPtr, out MiniMapMarkerTextCache cached) &&
                !ReferenceEquals(cached.Txt, null) &&
                cached.Txt != null &&
                !ReferenceEquals(cached.Go, null) &&
                cached.Go != null)
            {
                txtGo = cached.Go;
                return cached.Txt;
            }

            Transform parentTr = entryImg.transform;
            if (parentTr == null)
                return null;

            Transform existing = parentTr.Find("ER2AI_Squad3DLetter");
            UnityEngine.UI.Text txt = existing != null ? existing.GetComponent<UnityEngine.UI.Text>() : null;
            if (txt == null)
            {
                GameObject go = new GameObject("ER2AI_Squad3DLetter");
                RectTransform rt = go.AddComponent<RectTransform>();
                rt.SetParent(parentTr, false);
                rt.anchorMin = new Vector2(0.5f, 0.5f);
                rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.anchoredPosition = Vector2.zero;
                rt.sizeDelta = new Vector2(36f, 24f);
                rt.localScale = Vector3.one;

                txt = go.AddComponent<UnityEngine.UI.Text>();
                Font f = GetSquad3DLabelFont();
                if (f != null)
                    txt.font = f;
                txt.fontSize = 13;
                txt.fontStyle = FontStyle.Bold;
                txt.alignment = TextAnchor.MiddleCenter;
                txt.horizontalOverflow = HorizontalWrapMode.Overflow;
                txt.verticalOverflow = VerticalWrapMode.Overflow;
                txt.raycastTarget = false;
                txt.color = Color.white;

                UnityEngine.UI.Outline outline = go.AddComponent<UnityEngine.UI.Outline>();
                outline.effectColor = new Color(0f, 0f, 0f, 0.92f);
                outline.effectDistance = new Vector2(1.0f, -1.0f);
                txtGo = go;
            }
            else
            {
                txtGo = txt.gameObject;
            }

            if (_marker3DEntryTexts.Count > 128)
                _marker3DEntryTexts.Clear();
            _marker3DEntryTexts[entryPtr] = new MiniMapMarkerTextCache { Txt = txt, Go = txtGo };
            return txt;
        }

        [HarmonyPatch(typeof(Marker3DGUI), nameof(Marker3DGUI.GetPooled))]
        public static class Marker3DGUI_GetPooled_Patch
        {
            public static void Postfix(Marker3DGUI.Entry __result)
            {
                try
                {
                    _lastPooledMarker3DEntry = __result;
                    if (__result != null)
                    {
                        IntPtr entryPtr = __result.Pointer;
                        if (_marker3DEntryTexts.TryGetValue(entryPtr, out MiniMapMarkerTextCache existing) &&
                            !ReferenceEquals(existing.Txt, null) &&
                            existing.Txt != null &&
                            !ReferenceEquals(existing.Go, null) &&
                            existing.Go != null &&
                            existing.Go.activeSelf)
                        {
                            existing.Go.SetActive(false);
                        }
                        if (_marker3DEntryOwnerByEntryPtr.TryGetValue(entryPtr, out IntPtr prevOwner))
                        {
                            _marker3DEntryOwnerByEntryPtr.Remove(entryPtr);
                            if (_squad3DMarkerEntries.TryGetValue(prevOwner, out Marker3DGUI.Entry prevEntry) &&
                                prevEntry != null &&
                                prevEntry.Pointer == entryPtr)
                            {
                                _squad3DMarkerEntries.Remove(prevOwner);
                            }
                        }
                        _activeSquad3DLabels.Remove(entryPtr);
                    }
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(Marker3DGUI), nameof(Marker3DGUI.DrawOccluded))]
        public static class Marker3DGUI_DrawOccluded_Patch
        {
            public static void Prefix(int __1)
            {
                if (__1 == 0)
                    _lastPooledMarker3DEntry = null;
            }

            public static void Postfix(Il2CppSystem.Object __0, int __1)
            {
                try
                {
                    if (__1 != 0 || __0 == null || __0.Pointer == IntPtr.Zero)
                        return;

                    IntPtr keyPtr = __0.Pointer;
                    if (_lastPooledMarker3DEntry != null)
                    {
                        if (_squad3DMarkerEntries.Count > 128)
                        {
                            _squad3DMarkerEntries.Clear();
                            _marker3DEntryOwnerByEntryPtr.Clear();
                        }
                        IntPtr pooledPtr = _lastPooledMarker3DEntry.Pointer;
                        if (_marker3DEntryOwnerByEntryPtr.TryGetValue(pooledPtr, out IntPtr oldOwner) &&
                            oldOwner != keyPtr &&
                            _squad3DMarkerEntries.TryGetValue(oldOwner, out Marker3DGUI.Entry oldEntry) &&
                            oldEntry != null &&
                            oldEntry.Pointer == pooledPtr)
                        {
                            _squad3DMarkerEntries.Remove(oldOwner);
                        }
                        _squad3DMarkerEntries[keyPtr] = _lastPooledMarker3DEntry;
                        _marker3DEntryOwnerByEntryPtr[pooledPtr] = keyPtr;
                        _lastPooledMarker3DEntry = null;
                    }

                    if (!_squad3DMarkerEntries.TryGetValue(keyPtr, out Marker3DGUI.Entry entry) ||
                        entry == null ||
                        entry.lastFrame != Time.frameCount)
                    {
                        return;
                    }

                    IntPtr entryPtr = entry.Pointer;
                    if (!_marker3DEntryOwnerByEntryPtr.TryGetValue(entryPtr, out IntPtr ownerPtr) ||
                        ownerPtr != keyPtr)
                    {
                        return;
                    }

                    bool labelsEnabled = VisionConfig.EnableMod.Value && VisionConfig.EnableMapSquadLabels.Value;
                    float uNow = Time.unscaledTime;
                    if (_activeSquad3DLabels.TryGetValue(entryPtr, out ActiveSquad3DLabel cachedLbl) &&
                        cachedLbl != null &&
                        cachedLbl.OwnerKeyPtr == keyPtr)
                    {
                        if (cachedLbl.IsNonSquadKey)
                            return;

                        if (!labelsEnabled)
                        {
                            if (cachedLbl.LastActive)
                            {
                                cachedLbl.LastActive = false;
                                if (!ReferenceEquals(cachedLbl.TxtGo, null) && cachedLbl.TxtGo != null && cachedLbl.TxtGo.activeSelf)
                                    cachedLbl.TxtGo.SetActive(false);
                            }
                            return;
                        }

                        if (uNow < cachedLbl.NextRefreshTime && uNow >= cachedLbl.NextRefreshTime - 2.0f)
                        {
                            if (cachedLbl.IsSuppressedSquad)
                                return;

                            if (!ReferenceEquals(cachedLbl.ImgGo, null) && cachedLbl.ImgGo != null && cachedLbl.ImgGo.activeInHierarchy &&
                                !ReferenceEquals(cachedLbl.Txt, null) && cachedLbl.Txt != null &&
                                !ReferenceEquals(cachedLbl.TxtGo, null) && cachedLbl.TxtGo != null)
                            {
                                if (!cachedLbl.LastActive)
                                {
                                    cachedLbl.LastActive = true;
                                    if (!cachedLbl.TxtGo.activeSelf)
                                        cachedLbl.TxtGo.SetActive(true);
                                }

                                float fastAlpha = Mathf.Clamp01(entry.alpha * 1.15f);
                                if (Mathf.Abs(cachedLbl.LastAlpha - fastAlpha) > 0.01f)
                                {
                                    cachedLbl.LastAlpha = fastAlpha;
                                    Color fc = cachedLbl.BaseColor;
                                    fc.a = fastAlpha;
                                    cachedLbl.Txt.color = fc;
                                }
                                return;
                            }
                        }
                    }

                    Squad sq = __0.TryCast<Squad>();
                    if (sq == null)
                    {
                        if (_marker3DEntryTexts.TryGetValue(entryPtr, out MiniMapMarkerTextCache nonSqTxt) &&
                            !ReferenceEquals(nonSqTxt.Txt, null) &&
                            nonSqTxt.Txt != null &&
                            !ReferenceEquals(nonSqTxt.Go, null) &&
                            nonSqTxt.Go != null &&
                            nonSqTxt.Go.activeSelf)
                        {
                            nonSqTxt.Go.SetActive(false);
                        }
                        if (cachedLbl == null)
                        {
                            cachedLbl = new ActiveSquad3DLabel();
                            _activeSquad3DLabels[entryPtr] = cachedLbl;
                        }
                        cachedLbl.OwnerKeyPtr = keyPtr;
                        cachedLbl.IsNonSquadKey = true;
                        cachedLbl.IsSuppressedSquad = false;
                        cachedLbl.LastActive = false;
                        return;
                    }

                    if (!labelsEnabled || !IsFriendlySquadToLocalPlayer(sq))
                    {
                        if (_marker3DEntryTexts.TryGetValue(entryPtr, out MiniMapMarkerTextCache existing) &&
                            !ReferenceEquals(existing.Txt, null) &&
                            existing.Txt != null &&
                            !ReferenceEquals(existing.Go, null) &&
                            existing.Go != null &&
                            existing.Go.activeSelf)
                        {
                            existing.Go.SetActive(false);
                        }
                        if (!labelsEnabled)
                        {
                            _activeSquad3DLabels.Remove(entryPtr);
                        }
                        else
                        {
                            if (cachedLbl == null)
                            {
                                cachedLbl = new ActiveSquad3DLabel();
                                _activeSquad3DLabels[entryPtr] = cachedLbl;
                            }
                            cachedLbl.OwnerKeyPtr = keyPtr;
                            cachedLbl.IsNonSquadKey = false;
                            cachedLbl.IsSuppressedSquad = true;
                            cachedLbl.NextRefreshTime = uNow + 0.50f;
                            cachedLbl.LastActive = false;
                        }
                        return;
                    }

                    UnityEngine.UI.RawImage entryImg = entry.img;
                    GameObject entryImgGo = !ReferenceEquals(entryImg, null) && entryImg != null ? entryImg.gameObject : null;
                    if (ReferenceEquals(entryImgGo, null) || entryImgGo == null || !entryImgGo.activeInHierarchy)
                        return;

                    string sqName = sq.squadName;
                    if (string.IsNullOrEmpty(sqName))
                    {
                        _activeSquad3DLabels.Remove(entryPtr);
                        return;
                    }

                    UnityEngine.UI.Text txt = GetOrCreateMarker3DSquadText(entry, entryImg, out GameObject txtGo);
                    if (txt == null || ReferenceEquals(txtGo, null) || txtGo == null)
                        return;

                    if (cachedLbl == null)
                    {
                        cachedLbl = new ActiveSquad3DLabel();
                        _activeSquad3DLabels[entryPtr] = cachedLbl;
                    }

                    string letter = sqName.Length == 1 ? sqName : sqName.Substring(0, 1);
                    if (cachedLbl.CachedLetter != letter)
                    {
                        cachedLbl.CachedLetter = letter;
                        if (txt.text != letter)
                            txt.text = letter;
                    }

                    float alpha = Mathf.Clamp01(entry.alpha * 1.15f);
                    Color baseColor = GetSquadLetterColor(sq, 1.0f);
                    Color finalColor = baseColor;
                    finalColor.a = alpha;
                    txt.color = finalColor;
                    if (!txtGo.activeSelf)
                        txtGo.SetActive(true);

                    cachedLbl.OwnerKeyPtr = keyPtr;
                    cachedLbl.Entry = entry;
                    cachedLbl.Img = entryImg;
                    cachedLbl.ImgGo = entryImgGo;
                    cachedLbl.Txt = txt;
                    cachedLbl.TxtGo = txtGo;
                    cachedLbl.BaseColor = baseColor;
                    cachedLbl.LastAlpha = alpha;
                    cachedLbl.NextRefreshTime = uNow + 0.50f;
                    cachedLbl.LastActive = true;
                    cachedLbl.IsNonSquadKey = false;
                    cachedLbl.IsSuppressedSquad = false;
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(Marker3DGUI), nameof(Marker3DGUI.LateUpdate))]
        public static class Marker3DGUI_LateUpdate_Patch
        {
            public static void Postfix()
            {
                long profStart = HitchProfiler.BeginModScope();
                try
                {
                    if (VisionConfig.EnableMod.Value)
                    {
                        float uNow = Time.unscaledTime;
                        if (uNow >= _nextLocalHudVehicleCheckTime || uNow < _nextLocalHudVehicleCheckTime - 2.0f)
                        {
                            _nextLocalHudVehicleCheckTime = uNow + 0.15f;
                            PlayerController pcHud = PlayerController.currentController;
                            Vehicle ctrlVeh = !ReferenceEquals(pcHud, null) && pcHud != null ? pcHud.ControlledVehicle : null;
                            IntPtr curVehPtr = !ReferenceEquals(ctrlVeh, null) && ctrlVeh != null ? ctrlVeh.Pointer : IntPtr.Zero;
                            Squad hudSq = curVehPtr != IntPtr.Zero ? PlayerGUI.squad : null;
                            SquadTask curTask = !ReferenceEquals(hudSq, null) && hudSq != null ? hudSq.currentTask : null;
                            IntPtr curTaskPtr = !ReferenceEquals(curTask, null) && curTask != null ? curTask.Pointer : IntPtr.Zero;

                            if (curVehPtr != _lastLocalHudVehiclePtr ||
                                (curVehPtr != IntPtr.Zero && curTaskPtr != _lastLocalHudVehicleTaskPtr))
                            {
                                _lastLocalHudVehiclePtr = curVehPtr;
                                _lastLocalHudVehicleTaskPtr = curTaskPtr;
                                if (PlayerGUI.instance != null)
                                    PlayerGUI.UpdateSquadGUI();
                            }
                        }
                    }

                    if (_activeSquad3DLabels.Count == 0)
                        return;

                    bool show = VisionConfig.EnableMod.Value && VisionConfig.EnableMapSquadLabels.Value;
                    foreach (var kv in _activeSquad3DLabels)
                    {
                        ActiveSquad3DLabel lbl = kv.Value;
                        if (lbl == null || lbl.IsNonSquadKey || lbl.IsSuppressedSquad)
                            continue;

                        Marker3DGUI.Entry entry = lbl.Entry;
                        UnityEngine.UI.Text txt = lbl.Txt;
                        GameObject txtGo = lbl.TxtGo;
                        if (ReferenceEquals(txt, null) || txt == null || ReferenceEquals(txtGo, null) || txtGo == null)
                            continue;

                        GameObject imgGo = lbl.ImgGo;
                        bool entryVisible = show &&
                                            entry != null &&
                                            !ReferenceEquals(lbl.Img, null) &&
                                            lbl.Img != null &&
                                            !ReferenceEquals(imgGo, null) &&
                                            imgGo != null &&
                                            imgGo.activeSelf &&
                                            entry.alpha > 0.01f;
                        if (!entryVisible)
                        {
                            if (lbl.LastActive)
                            {
                                lbl.LastActive = false;
                                if (txtGo.activeSelf)
                                    txtGo.SetActive(false);
                            }
                            continue;
                        }

                        if (!lbl.LastActive)
                        {
                            lbl.LastActive = true;
                            if (!txtGo.activeSelf)
                                txtGo.SetActive(true);
                        }

                        float targetAlpha = Mathf.Clamp01(entry.alpha * 1.15f);
                        if (Mathf.Abs(lbl.LastAlpha - targetAlpha) > 0.01f)
                        {
                            lbl.LastAlpha = targetAlpha;
                            Color c = txt.color;
                            c.a = targetAlpha;
                            txt.color = c;
                        }
                    }
                }
                catch { }
                finally
                {
                    HitchProfiler.EndModScope(HitchBucket.UiMarkers, profStart);
                }
            }
        }

        [HarmonyPatch(typeof(MiniMapGUI), nameof(MiniMapGUI.Update))]
        public static class MiniMapGUI_Update_Patch
        {
            public static void Prefix()
            {
                _insideMiniMapUpdate = true;
                _lastUsedUnitMarkerIndex = -1;
                _lastUnitMarkerTextComp = null;
                _lastUnitMarkerTextGo = null;
            }

            public static void Postfix(MiniMapGUI __instance)
            {
                _insideMiniMapUpdate = false;
                _lastUnitMarkerTextComp = null;
                _lastUnitMarkerTextGo = null;
                long profStart = HitchProfiler.BeginModScope();
                try
                {
                    if (!VisionConfig.EnableMod.Value || __instance == null || !MiniMapGUI.MiniMapOpened)
                        return;

                    if (!TryGetActivePendingPlayerHqOrder(out PlayerPendingHqOrderState pending))
                        return;

                    // Note: MiniMapGUI.DisableRemainingUnitMarkers (0x1804A0820) is inlined at the end of
                    // MiniMapGUI.Update (0x1804A8BAC). Restore usedMarker_index to _lastUsedUnitMarkerIndex
                    // so GetNextUnitMarker claims the first unused slot while higher slots remain disabled!
                    __instance.usedMarker_index = _lastUsedUnitMarkerIndex;

                    string badge = string.IsNullOrEmpty(pending.MapBadgeText) ? "HQ" : pending.MapBadgeText;
                    UnityEngine.UI.Image img = __instance.GetNextUnitMarker(badge, true);
                    UnityEngine.UI.Text badgeTxt = _lastUnitMarkerTextComp;
                    _lastUnitMarkerTextComp = null;
                    _lastUnitMarkerTextGo = null;
                    if (img == null || img.rectTransform == null)
                        return;

                    Sprite orderSprite = null;
                    if (pending.TargetTask != null)
                    {
                        try { orderSprite = pending.TargetTask.GetTaskIcon(); } catch { }
                    }
                    if (orderSprite == null && PlayerGUI.instance != null && PlayerGUI.instance.squadOrderPosIcon != null)
                    {
                        orderSprite = PlayerGUI.instance.squadOrderPosIcon.sprite;
                    }
                    if (orderSprite != null)
                    {
                        img.sprite = orderSprite;
                    }

                    RectTransform rt = img.rectTransform;
                    rt.anchoredPosition = __instance.GetPositionInContainer(pending.WorldPos, true);
                    img.color = new Color(1.0f, 0.85f, 0.28f, 0.52f);
                    if (badgeTxt != null)
                    {
                        badgeTxt.color = new Color(1.0f, 0.92f, 0.55f, 0.72f);
                    }
                    rt.localEulerAngles = new Vector3(0f, 0f, -__instance.northShift);

                    // Match vanilla MiniMapGUI.Update (0x1804A6CBD / 0x1804A819D): actual map zoom divisor is
                    // miniMap.localScale (1 + 30 * zoomValue^2, or rectZoom scale), NOT raw zoomValue (which is 0 when unzoomed!).
                    float z = MiniMapGUI.zoomValue;
                    float mapScale = 1.0f + 30.0f * z * z;
                    if (__instance.miniMap != null)
                    {
                        Vector3 mScale = __instance.miniMap.localScale;
                        float liveMapScale = Mathf.Max(mScale.x, mScale.y);
                        if (liveMapScale > 0.1f)
                            mapScale = liveMapScale;
                    }

                    rt.localScale = Vector3.one * (0.78f / Mathf.Max(1.0f, mapScale));
                }
                catch { }
                finally
                {
                    HitchProfiler.EndModScope(HitchBucket.UiMarkers, profStart);
                }
            }
        }

        /// <summary>
        /// Injects <c>Accept HQ: ...</c> and <c>Balk HQ Order</c> wedges into the root <c>Q</c> Orders radial
        /// (<see cref="PlayerController"/> <c>OpenOrdersMenu</c>) while leaving sub-menus untouched.
        /// </summary>
        [HarmonyPatch(typeof(CircularMenuSelection.CircularMenu2), nameof(CircularMenuSelection.CircularMenu2.ShowCircle))]
        public static class CircularMenu2_ShowCircle_Patch
        {
            public static void Prefix(
                ref Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<CircularMenuSelection.CircularMenuData> __0,
                GameInput __1,
                ref Il2CppSystem.Action<CircularMenuSelection.CircularMenuData> __2,
                string __3)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value ||
                        __1 != GameInput.OrdersMenu ||
                        !string.IsNullOrEmpty(__3) ||
                        __0 == null ||
                        __0.Length == 0)
                    {
                        return;
                    }

                    CircularMenuSelection.CircularMenuData firstItem = __0[0];
                    if (firstItem == null || firstItem.returnData == null || firstItem.returnData.TryCast<Soldier>() != null)
                        return;

                    CircularMenuSelection.CircularMenuData lastItem = __0[__0.Length - 1];
                    if (lastItem != null &&
                        (string.Equals(lastItem.text, "Balk HQ Order", StringComparison.Ordinal) ||
                         (lastItem.returnData != null && string.Equals(lastItem.returnData.ToString(), HqBalkRadialSentinel, StringComparison.Ordinal))))
                    {
                        return;
                    }

                    Soldier lp = Soldier.CurrentControlledSoldierOrNull();
                    if (lp == null || !lp.IsAlive || lp.IsInjured || lp.HasSurrended() || !lp.IsSquadLeader())
                        return;

                    if (!TryGetActivePendingPlayerHqOrder(out PlayerPendingHqOrderState pending))
                        return;

                    Sprite acceptSprite = null;
                    if (pending.TargetTask != null)
                    {
                        try { acceptSprite = pending.TargetTask.GetTaskIcon(); } catch { }
                    }
                    if (acceptSprite == null)
                        acceptSprite = firstItem.image;

                    int origLen = __0.Length;
                    var expanded = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<CircularMenuSelection.CircularMenuData>(origLen + 2);
                    for (int i = 0; i < origLen; i++)
                        expanded[i] = __0[i];

                    expanded[origLen] = new CircularMenuSelection.CircularMenuData(
                        acceptSprite,
                        $"Accept HQ: {pending.ShortActionLabel}",
                        new Color(0.55f, 0.95f, 0.55f, 1.0f),
                        (Il2CppSystem.String)HqAcceptRadialSentinel);

                    expanded[origLen + 1] = new CircularMenuSelection.CircularMenuData(
                        firstItem.image,
                        "Balk HQ Order",
                        new Color(0.95f, 0.55f, 0.45f, 1.0f),
                        (Il2CppSystem.String)HqBalkRadialSentinel);

                    Il2CppSystem.Action<CircularMenuSelection.CircularMenuData> origCallback = __2;
                    __2 = Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<Il2CppSystem.Action<CircularMenuSelection.CircularMenuData>>(
                        (Action<CircularMenuSelection.CircularMenuData>)(selected =>
                        {
                            try
                            {
                                string marker = selected != null && selected.returnData != null
                                    ? selected.returnData.ToString()
                                    : null;
                                string itemText = selected != null ? selected.text : null;
                                if (string.Equals(marker, HqAcceptRadialSentinel, StringComparison.Ordinal) ||
                                    (!string.IsNullOrEmpty(itemText) && itemText.StartsWith("Accept HQ:", StringComparison.Ordinal)))
                                {
                                    AcceptPendingPlayerHqOrder();
                                    return;
                                }
                                if (string.Equals(marker, HqBalkRadialSentinel, StringComparison.Ordinal) ||
                                    string.Equals(itemText, "Balk HQ Order", StringComparison.Ordinal))
                                {
                                    BalkPendingPlayerHqOrder();
                                    return;
                                }
                            }
                            catch { }

                            origCallback?.Invoke(selected);
                        }));

                    __0 = expanded;
                }
                catch { }
            }
        }
    }
}
