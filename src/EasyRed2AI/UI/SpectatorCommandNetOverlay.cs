using System;
using System.Collections.Generic;
using System.Text;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace EasyRed2HumanAI
{
    public static partial class VeteranAndTacticsPatches
    {
        // =====================================================================
        // Spectator Mode 4: Command Net Overhead View + Vanilla A/B Observer
        //  - Bright Blue   = Your Team (or Invaders if no controlled side yet)
        //  - Bright Red    = Enemy Team
        //  - Bright Cyan   = Squad Objective/Flank Routes + Hovered A* Paths
        //  - Works both with BH AI ON (Team Net) and F8 OFF (Vanilla A/B)
        // =====================================================================
        private struct CmdNetObjTally
        {
            public IntPtr TaskPtr;
            public string ShortLabel;
            public int YellowCount;
            public int RedCount;
        }

        private static bool _specCommandNetActive;
        private static bool _specCommandNetIsFreeCam;
        private static IntPtr _cmdNetCachedBdPtr;
        private static string _specLastLocalPlayerFaction;

        private static IntPtr _cmdNetHoveredSquadPtr;
        private static float _cmdNetHoverHoldUntil;
        private static bool _cmdNetHasHoveredSquad;
        private static bool _cmdNetHoverIsYellow;

        private static float _nextCmdNetFormatTime;
        private static IntPtr _lastFormattedHoveredSqPtr;
        private static bool _lastFormattedModEnabled;
        private static string _cmdNetCardLine0 = "";
        private static string _cmdNetCardLine1 = "";
        private static string _cmdNetCardLine2 = "";
        private static readonly StringBuilder _cmdNetSb = new StringBuilder(512);

        private static Mesh _cmdNetBeaconMesh;
        private static Mesh _cmdNetVehicleMesh;
        private static Material _cmdNetMatYellow;
        private static Material _cmdNetMatRed;
        private static Material _cmdNetMatYellowLeader;
        private static Material _cmdNetMatRedLeader;
        private static Material _cmdNetMatCyan;
        private static Material _cmdNetMatHoverMember;
        private static bool _cmdNetAssetsInitialized;

        private static readonly List<MissionObjective> _cmdNetObjBuf = new List<MissionObjective>(16);
        private static readonly CmdNetObjTally[] _cmdNetTallies = new CmdNetObjTally[12];
        private static readonly Vector2[] _cmdNetRingPts = new Vector2[16];
        private static readonly bool[] _cmdNetRingOk = new bool[16];

        private static readonly Color CmdNetColorCyan = new Color(1.00f, 0.18f, 0.86f, 0.98f);
        private static readonly Color CmdNetColorHoverMember = new Color(0.26f, 1.00f, 0.62f, 0.98f);
        private static readonly Color CmdNetColorAmber = new Color(1.00f, 0.60f, 0.15f, 0.85f);

        private static Color GetGameFriendlyBlue()
        {
            Color c = MissionTask.BLUE_MISSION_TEXT;
            c.a = 0.96f;
            return c;
        }

        private static Color GetGameFriendlyBlueSl()
        {
            Color c = Color.Lerp(MissionTask.BLUE_MISSION_TEXT, Color.white, 0.25f);
            c.a = 1.00f;
            return c;
        }

        private static Color GetGameEnemyRed()
        {
            Color c = MissionTask.RED_MISSION_TEXT;
            c.a = 0.96f;
            return c;
        }

        private static Color GetGameEnemyRedSl()
        {
            Color c = Color.Lerp(MissionTask.RED_MISSION_TEXT, Color.white, 0.25f);
            c.a = 1.00f;
            return c;
        }

        private static void EnsureCommandNetRenderAssets()
        {
            if (_cmdNetAssetsInitialized &&
                _cmdNetBeaconMesh != null &&
                _cmdNetVehicleMesh != null &&
                _cmdNetMatYellow != null &&
                _cmdNetMatRed != null &&
                _cmdNetMatHoverMember != null)
            {
                return;
            }

            _cmdNetAssetsInitialized = true;

            try
            {
                if (_cmdNetBeaconMesh == null)
                {
                    _cmdNetBeaconMesh = CreateOctaBeaconMesh(0.46f, 0.42f, 1.00f);
                }
                if (_cmdNetVehicleMesh == null)
                {
                    _cmdNetVehicleMesh = CreateVehicleChevronMesh();
                }

                Shader sh = Shader.Find("Universal Render Pipeline/Unlit") ??
                            Shader.Find("Sprites/Default") ??
                            Shader.Find("Unlit/Color") ??
                            Shader.Find("Hidden/Internal-Colored");
                if (sh != null)
                {
                    if (_cmdNetMatYellow == null)
                        _cmdNetMatYellow = CreateCommandNetMaterial(sh, GetGameFriendlyBlue());
                    if (_cmdNetMatRed == null)
                        _cmdNetMatRed = CreateCommandNetMaterial(sh, GetGameEnemyRed());
                    if (_cmdNetMatYellowLeader == null)
                        _cmdNetMatYellowLeader = CreateCommandNetMaterial(sh, GetGameFriendlyBlueSl());
                    if (_cmdNetMatRedLeader == null)
                        _cmdNetMatRedLeader = CreateCommandNetMaterial(sh, GetGameEnemyRedSl());
                    if (_cmdNetMatCyan == null)
                        _cmdNetMatCyan = CreateCommandNetMaterial(sh, CmdNetColorCyan);
                    if (_cmdNetMatHoverMember == null)
                        _cmdNetMatHoverMember = CreateCommandNetMaterial(sh, CmdNetColorHoverMember);
                }
            }
            catch { }
        }

        private static Mesh CreateOctaBeaconMesh(float radius, float midY, float topY)
        {
            Mesh m = new Mesh { hideFlags = HideFlags.HideAndDontSave };
            var verts = new Il2CppStructArray<Vector3>(6);
            verts[0] = new Vector3(0f, topY, 0f);
            verts[1] = new Vector3(0f, 0f, 0f);
            verts[2] = new Vector3(0f, midY, radius);
            verts[3] = new Vector3(radius, midY, 0f);
            verts[4] = new Vector3(0f, midY, -radius);
            verts[5] = new Vector3(-radius, midY, 0f);

            var tris = new Il2CppStructArray<int>(24);
            // Upper 4 faces
            tris[0] = 0; tris[1] = 2; tris[2] = 3;
            tris[3] = 0; tris[4] = 3; tris[5] = 4;
            tris[6] = 0; tris[7] = 4; tris[8] = 5;
            tris[9] = 0; tris[10] = 5; tris[11] = 2;
            // Lower 4 faces
            tris[12] = 1; tris[13] = 3; tris[14] = 2;
            tris[15] = 1; tris[16] = 4; tris[17] = 3;
            tris[18] = 1; tris[19] = 5; tris[20] = 4;
            tris[21] = 1; tris[22] = 2; tris[23] = 5;

            m.SetVertices(verts);
            m.SetTriangles(tris, 0);
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }

        private static Mesh CreateVehicleChevronMesh()
        {
            Mesh m = new Mesh { hideFlags = HideFlags.HideAndDontSave };
            var verts = new Il2CppStructArray<Vector3>(6);
            verts[0] = new Vector3(0f, 0.35f, 1.25f);
            verts[1] = new Vector3(-0.78f, 0.22f, -0.75f);
            verts[2] = new Vector3(0.78f, 0.22f, -0.75f);
            verts[3] = new Vector3(0f, 0.22f, -0.35f);
            verts[4] = new Vector3(0f, 0.85f, 0f);
            verts[5] = new Vector3(0f, 0f, 0f);

            var tris = new Il2CppStructArray<int>(24);
            // Top hull diamond
            tris[0] = 4; tris[1] = 0; tris[2] = 2;
            tris[3] = 4; tris[4] = 2; tris[5] = 3;
            tris[6] = 4; tris[7] = 3; tris[8] = 1;
            tris[9] = 4; tris[10] = 1; tris[11] = 0;
            // Bottom hull
            tris[12] = 5; tris[13] = 2; tris[14] = 0;
            tris[15] = 5; tris[16] = 3; tris[17] = 2;
            tris[18] = 5; tris[19] = 1; tris[20] = 3;
            tris[21] = 5; tris[22] = 0; tris[23] = 1;

            m.SetVertices(verts);
            m.SetTriangles(tris, 0);
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }

        private static Material CreateCommandNetMaterial(Shader shader, Color col)
        {
            Material mat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            mat.color = col;
            if (mat.HasProperty("_BaseColor"))
                mat.SetColor("_BaseColor", col);
            if (mat.HasProperty("_ZTest"))
                mat.SetInt("_ZTest", 8);
            if (mat.HasProperty("_Cull"))
                mat.SetInt("_Cull", 0);
            if (mat.HasProperty("_ZWrite"))
                mat.SetInt("_ZWrite", 0);
            mat.renderQueue = 3950;
            return mat;
        }

        private static string ResolveYellowSideFaction(BattleData bd, out string redSideSampleFac)
        {
            IntPtr curBdPtr = bd != null ? bd.Pointer : IntPtr.Zero;
            if (curBdPtr != _cmdNetCachedBdPtr || Time.timeSinceLevelLoad < 1.5f)
            {
                _cmdNetCachedBdPtr = curBdPtr;
                _specLastLocalPlayerFaction = null;
                _cmdNetHoveredSquadPtr = IntPtr.Zero;
                _cmdNetHasHoveredSquad = false;
                _lastFormattedHoveredSqPtr = IntPtr.Zero;
            }

            Soldier local = Soldier.CurrentControlledSoldierOrNull();
            if (!ReferenceEquals(local, null) && local != null && !string.IsNullOrEmpty(local.faction))
            {
                _specLastLocalPlayerFaction = local.faction;
            }

            string yellowFac = _specLastLocalPlayerFaction;
            if (string.IsNullOrEmpty(yellowFac))
            {
                string playerFac = BattleManager.PlayerFaction;
                if (!string.IsNullOrEmpty(playerFac))
                    yellowFac = playerFac;
            }
            if (string.IsNullOrEmpty(yellowFac) && bd != null)
            {
                try { yellowFac = bd.GetInvadersFaction(); } catch { }
            }

            redSideSampleFac = null;
            int sqCount = AiCore.WorldSquadCount;
            var squads = AiCore.WorldSquads;
            for (int i = 0; i < sqCount; i++)
            {
                string f = squads[i].Faction;
                if (string.IsNullOrEmpty(f))
                    continue;

                if (string.IsNullOrEmpty(yellowFac))
                    yellowFac = f;
                else if (redSideSampleFac == null && !AiCore.AreSameSideFactions(yellowFac, f))
                    redSideSampleFac = f;

                if (!string.IsNullOrEmpty(yellowFac) && !string.IsNullOrEmpty(redSideSampleFac))
                    break;
            }

            if (string.IsNullOrEmpty(redSideSampleFac) && bd != null)
            {
                try
                {
                    string def = bd.GetDefendersFaction();
                    if (!string.IsNullOrEmpty(def) && (string.IsNullOrEmpty(yellowFac) || !AiCore.AreSameSideFactions(yellowFac, def)))
                        redSideSampleFac = def;
                    else
                    {
                        string inv = bd.GetInvadersFaction();
                        if (!string.IsNullOrEmpty(inv) && (string.IsNullOrEmpty(yellowFac) || !AiCore.AreSameSideFactions(yellowFac, inv)))
                            redSideSampleFac = inv;
                    }
                }
                catch { }
            }

            return yellowFac;
        }

        internal static void UpdateAndRenderSpectatorCommandNet3D(bool isFreeCamera, bool canShowOverlays)
        {
            try
            {
                if (ActiveSpectatorHudMode != SpectatorTacticalHudMode.CommandNetOverview ||
                    !canShowOverlays ||
                    !IsCommandNetOverviewAllowed())
                {
                    _specCommandNetActive = false;
                    _cmdNetHasHoveredSquad = false;
                    return;
                }

                Camera cam = ResourcesManager.mainCamera;
                if (cam == null)
                    cam = Camera.main;
                if (cam == null || cam.transform == null)
                {
                    _specCommandNetActive = false;
                    _cmdNetHasHoveredSquad = false;
                    return;
                }

                // Build/refresh the read-only world snapshot even if AI Mod is toggled OFF via F8!
                AiCore.EnsureWorldSnapshot(0.22f);

                _specCommandNetActive = true;
                _specCommandNetIsFreeCam = isFreeCamera;

                BattleData bd = null;
                try { bd = BattleManager.GetCurrentBattleData(); } catch { }
                string yellowRefFac = ResolveYellowSideFaction(bd, out string redRefFac);

                Vector3 camPos = cam.transform.position;
                float unscaledNow = Time.unscaledTime;

                // 1. Sky-friendly Squad / SL Hover Detection
                int sqCount = AiCore.WorldSquadCount;
                var squads = AiCore.WorldSquads;
                IntPtr bestSqPtr = IntPtr.Zero;
                int bestSqSnapIdx = -1;

                if (isFreeCamera && sqCount > 0)
                {
                    Vector2 screenCenter = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
                    float maxHoverPx = Mathf.Max(42f, Screen.height * 0.14f);
                    float stickyHoverPx = maxHoverPx * 1.35f;
                    float bestScorePx = float.MaxValue;

                    for (int i = 0; i < sqCount; i++)
                    {
                        ref AiCore.SquadSnapshot sqSnap = ref squads[i];
                        if (sqSnap.Ptr == IntPtr.Zero || sqSnap.UnitCount <= 0 || sqSnap.Ref == null)
                            continue;

                        if ((sqSnap.RefPos - camPos).sqrMagnitude > 480f * 480f)
                            continue;

                        Vector3 sp = cam.WorldToScreenPoint(sqSnap.RefPos + new Vector3(0f, 1.6f, 0f));
                        if (sp.z <= 0.5f || sp.z > 480f)
                            continue;

                        float dPx = Vector2.Distance(new Vector2(sp.x, sp.y), screenCenter);
                        bool isCur = _cmdNetHoveredSquadPtr != IntPtr.Zero && sqSnap.Ptr == _cmdNetHoveredSquadPtr;
                        if (dPx > (isCur ? stickyHoverPx : maxHoverPx))
                            continue;

                        float score = (isCur ? dPx * 0.72f : dPx) + sp.z * 0.08f;
                        if (score < bestScorePx)
                        {
                            bestScorePx = score;
                            bestSqPtr = sqSnap.Ptr;
                            bestSqSnapIdx = i;
                        }
                    }
                }

                if (bestSqPtr != IntPtr.Zero)
                {
                    _cmdNetHoveredSquadPtr = bestSqPtr;
                    _cmdNetHoverHoldUntil = unscaledNow + 0.65f;
                    _cmdNetHasHoveredSquad = true;
                }
                else if (_cmdNetHoveredSquadPtr != IntPtr.Zero && unscaledNow < _cmdNetHoverHoldUntil)
                {
                    bestSqSnapIdx = -1;
                    for (int i = 0; i < sqCount; i++)
                    {
                        if (squads[i].Ptr == _cmdNetHoveredSquadPtr && squads[i].UnitCount > 0)
                        {
                            bestSqSnapIdx = i;
                            bestSqPtr = _cmdNetHoveredSquadPtr;
                            break;
                        }
                    }
                    _cmdNetHasHoveredSquad = bestSqSnapIdx >= 0;
                    if (!_cmdNetHasHoveredSquad)
                        _cmdNetHoveredSquadPtr = IntPtr.Zero;
                }
                else
                {
                    _cmdNetHasHoveredSquad = false;
                    _cmdNetHoveredSquadPtr = IntPtr.Zero;
                }

                // 2. Render Bright Yellow / Red Unlit 3D Meshes over Soldiers and Vehicles
                EnsureCommandNetRenderAssets();
                float hoverPulse = 1.0f + 0.14f * Mathf.Sin(unscaledNow * 6.5f);

                if (_cmdNetBeaconMesh != null && _cmdNetMatYellow != null && _cmdNetMatRed != null)
                {
                    int wCount = AiCore.WorldSoldierCount;
                    var soldiers = AiCore.WorldSoldiers;
                    for (int i = 0; i < wCount; i++)
                    {
                        ref AiCore.SoldierSnapshot sSnap = ref soldiers[i];
                        if (sSnap.Ptr == IntPtr.Zero || sSnap.IsOnVehicle || sSnap.HasSurrendered || sSnap.IsInjured)
                            continue;

                        float dist = Vector3.Distance(camPos, sSnap.Pos);
                        if (dist > 650f)
                            continue;

                        float s = Mathf.Clamp(dist * 0.0115f, 0.75f, 3.6f);
                        bool isYellow = !string.IsNullOrEmpty(yellowRefFac) && AiCore.AreSameSideFactions(sSnap.Faction, yellowRefFac);
                        bool isHovered = _cmdNetHasHoveredSquad && sSnap.SquadPtr == _cmdNetHoveredSquadPtr;

                        Material mat;
                        if (isHovered && sSnap.IsSquadLeader && _cmdNetMatCyan != null)
                            mat = _cmdNetMatCyan;
                        else if (isHovered && _cmdNetMatHoverMember != null)
                            mat = _cmdNetMatHoverMember;
                        else if (isYellow)
                            mat = (sSnap.IsSquadLeader && _cmdNetMatYellowLeader != null) ? _cmdNetMatYellowLeader : _cmdNetMatYellow;
                        else
                            mat = (sSnap.IsSquadLeader && _cmdNetMatRedLeader != null) ? _cmdNetMatRedLeader : _cmdNetMatRed;

                        float pulse = isHovered ? hoverPulse : 1.0f;
                        Vector3 scale = sSnap.IsSquadLeader
                            ? new Vector3(s * 0.95f * pulse, s * 2.25f * pulse, s * 0.95f * pulse)
                            : new Vector3(s * 0.65f * pulse, s * 1.45f * pulse, s * 0.65f * pulse);
                        Vector3 drawPos = sSnap.Pos + new Vector3(0f, sSnap.IsSquadLeader ? (1.35f + s * 0.55f) : (0.95f + s * 0.25f), 0f);

                        Graphics.DrawMesh(_cmdNetBeaconMesh, Matrix4x4.TRS(drawPos, Quaternion.identity, scale), mat, 0, cam, 0, null, false, false);
                    }
                }

                if (_cmdNetVehicleMesh != null && _cmdNetMatYellow != null && _cmdNetMatRed != null)
                {
                    try
                    {
                        var allVeh = Vehicle.allVehicles;
                        if (allVeh != null)
                        {
                            int vLen = allVeh.Count;
                            for (int i = 0; i < vLen; i++)
                            {
                                Vehicle v = allVeh[i];
                                if (v == null || v.Pointer == IntPtr.Zero || v.IsDisabled() || v.transform == null)
                                    continue;

                                string vFac = v.GetVehicleFaction();
                                if (string.IsNullOrEmpty(vFac) || vFac == "Civilian")
                                    continue;

                                Vector3 vPos = v.transform.position;
                                float dist = Vector3.Distance(camPos, vPos);
                                if (dist > 850f)
                                    continue;

                                bool isYellow = !string.IsNullOrEmpty(yellowRefFac) && AiCore.AreSameSideFactions(vFac, yellowRefFac);
                                Material vMat = isYellow ? (_cmdNetMatYellowLeader ?? _cmdNetMatYellow) : (_cmdNetMatRedLeader ?? _cmdNetMatRed);
                                float vs = Mathf.Clamp(dist * 0.016f, 1.5f, 5.5f);
                                Vector3 drawPos = vPos + new Vector3(0f, 2.0f + vs * 0.35f, 0f);

                                Graphics.DrawMesh(_cmdNetVehicleMesh, Matrix4x4.TRS(drawPos, v.transform.rotation, new Vector3(vs, vs, vs)), vMat, 0, cam, 0, null, false, false);
                            }
                        }
                    }
                    catch { }
                }

                // 3. Throttled Card Readout Formatting (0.18s cadence)
                bool modEnabled = VisionConfig.EnableMod.Value;
                if (unscaledNow < _nextCmdNetFormatTime &&
                    _lastFormattedHoveredSqPtr == _cmdNetHoveredSquadPtr &&
                    _lastFormattedModEnabled == modEnabled &&
                    _nextCmdNetFormatTime - unscaledNow <= 2.0f)
                {
                    return;
                }

                _nextCmdNetFormatTime = unscaledNow + 0.18f;
                _lastFormattedHoveredSqPtr = _cmdNetHoveredSquadPtr;
                _lastFormattedModEnabled = modEnabled;

                if (bestSqSnapIdx >= 0 && bestSqSnapIdx < sqCount)
                {
                    ref AiCore.SquadSnapshot hoveredSnap = ref squads[bestSqSnapIdx];
                    _cmdNetHoverIsYellow = !string.IsNullOrEmpty(yellowRefFac) && AiCore.AreSameSideFactions(hoveredSnap.Faction, yellowRefFac);
                    FormatCommandNetSquadLeaderCard(ref hoveredSnap, _cmdNetHoverIsYellow, modEnabled, camPos);
                }
                else
                {
                    FormatCommandNetMacroSummaryCard(yellowRefFac, redRefFac, modEnabled);
                }
            }
            catch (Exception ex)
            {
                AiCore.ReportPatchException("SpecCmdNet.Update", ex);
            }
        }

        private static string GetShortObjectiveCode(MissionObjective obj, int idx)
        {
            if (obj == null)
                return $"#{idx + 1}";

            string full = AiCore.GetVanillaObjectiveDisplayName(obj, null, out string shortLetter);
            string code = !string.IsNullOrWhiteSpace(shortLetter) ? shortLetter.Trim() : full;
            if (string.IsNullOrWhiteSpace(code))
                return $"#{idx + 1}";

            if (code.StartsWith("Point ", StringComparison.OrdinalIgnoreCase) && code.Length > 6)
                code = code.Substring(6).Trim();
            else if (code.StartsWith("Objective ", StringComparison.OrdinalIgnoreCase) && code.Length > 10)
                code = code.Substring(10).Trim();

            return code.Length > 10 ? code.Substring(0, 10) : code;
        }

        private static string GetCommandNetDirectiveLabel(IntPtr sqPtr, bool modEnabled, float now, out bool smokeUp, out bool underBarrageHold, out bool postBarrageSurge)
        {
            smokeUp = false;
            underBarrageHold = false;
            postBarrageSurge = false;
            if (!modEnabled)
                return "VANILLA";

            if (_garrisonAssignedObjectiveBySquad.ContainsKey(sqPtr))
                return "GARRISON";

            if (_squadBlackboards.TryGetValue(sqPtr, out SquadBlackboard b))
            {
                smokeUp = now < b.SmokeActiveUntil;

                switch (b.NetDirective)
                {
                    case SquadNetDirective.Hammer_WideFlankEnvelopment: return "HAMMER FLANK";
                    case SquadNetDirective.Anvil_PinAndCallSupport: return "ANVIL PIN";
                    case SquadNetDirective.Regroup_SectorWithdrawal: return "REGROUP";
                    case SquadNetDirective.Connected_StandardObjective: return "STANDARD PUSH";
                    default: return "AUTONOMOUS";
                }
            }

            return "STANDARD";
        }

        private static void FormatCommandNetSquadLeaderCard(
            ref AiCore.SquadSnapshot sqSnap,
            bool isYellow,
            bool modEnabled,
            Vector3 camPos)
        {
            Squad sq = sqSnap.Ref;
            IntPtr sqPtr = sqSnap.Ptr;
            float now = Time.time;

            string modeTag = modEnabled ? $"BH v{Plugin.PluginVersion} NET" : "VANILLA A/B (F8 OFF)";
            string sideTag = isYellow ? "BLUE" : "RED";
            string sqName = !string.IsNullOrEmpty(sq.squadName) ? sq.squadName : $"Squad #{AiCore.GetSquadDiagId(sqPtr)}";
            if (sqName.Length > 26)
                sqName = sqName.Substring(0, 26);

            string radioStr = sqSnap.HasIntrinsicRadio
                ? "HQ-NET"
                : (sqSnap.HasRadioOrRelay ? "RELAY" : "CUT OFF");

            string slClass = sqSnap.Leader != null
                ? AiCore.GetSoldierClassLabel(sqSnap.Leader)
                : "Squad Leader";
            string slPers = (modEnabled && sqSnap.Leader != null)
                ? GetPersonalityShortLabel(GetPersonality(sqSnap.Leader))
                : "Vanilla";
            int camDist = Mathf.RoundToInt(Vector3.Distance(camPos, sqSnap.RefPos));

            // Line 1
            _cmdNetSb.Length = 0;
            _cmdNetSb.Append('[').Append(modeTag).Append("]  \u2022  [").Append(sideTag).Append("] '")
                     .Append(sqName).Append("' (").Append(sqSnap.UnitCount).Append(" alive)  |  SL: ")
                     .Append(slClass).Append(" (").Append(slPers).Append(")  |  Radio: ").Append(radioStr)
                     .Append("  |  ").Append(camDist).Append('m');
            _cmdNetCardLine0 = _cmdNetSb.ToString();

            // Line 2
            string dirLabel = GetCommandNetDirectiveLabel(sqPtr, modEnabled, now, out bool smokeUp, out _, out _);
            string objStr = "None";
            int objDist = -1;
            if (sq.currentTask != null && sq.currentTask.Pointer != IntPtr.Zero)
            {
                MissionObjective mObj = FindActiveMissionObjectiveForTaskPtr(sq.currentTask.Pointer);
                objStr = mObj != null ? AiCore.GetVanillaObjectiveDisplayName(mObj) : "OrderPos";
                if (objStr.Length > 24)
                    objStr = objStr.Substring(0, 24);
                objDist = Mathf.RoundToInt(Vector3.Distance(sqSnap.RefPos, sq.currentTask.GetTaskDestination()));
            }

            _cmdNetSb.Length = 0;
            _cmdNetSb.Append("Directive: ").Append(dirLabel)
                     .Append("  |  Order: ").Append(sq.order.ToString())
                     .Append("  |  Obj: ").Append(objStr);
            if (objDist >= 0)
                _cmdNetSb.Append(" (").Append(objDist).Append("m)");
            if (smokeUp)
                _cmdNetSb.Append("  [SMOKE]");
            if (modEnabled && _squadBlackboards.TryGetValue(sqPtr, out SquadBlackboard cardB))
            {
                if (cardB.LeaderRoseToOccasion)
                    _cmdNetSb.Append("  [RALLIED]");
                if (now < cardB.LeaderShockUntil)
                    _cmdNetSb.Append("  [SL SHOCK]");
                else if (cardB.LeaderBraveryAura >= 0.06f)
                    _cmdNetSb.Append("  [BRAVERY +").Append(Mathf.RoundToInt(cardB.LeaderBraveryAura * 100f)).Append("%]");
            }
            _cmdNetCardLine1 = _cmdNetSb.ToString();

            // Line 3: Aggregate squad suppression + live A* paths + threat/casualties
            float suppSum = 0f;
            int activePaths = 0;
            var soldiers = AiCore.WorldSoldiers;
            int endIdx = Mathf.Min(AiCore.WorldSoldierCount, sqSnap.UnitStartIndex + sqSnap.UnitCount);
            for (int i = sqSnap.UnitStartIndex; i < endIdx; i++)
            {
                ref AiCore.SoldierSnapshot ss = ref soldiers[i];
                if (ss.Ref != null)
                    suppSum += ss.Ref.GetSuppressionValue();

                if (ss.Handles != null && ss.Handles.Ai != null && !ss.IsOnVehicle)
                {
                    A.d p = AiCore.GetCachedAiPath(ss.Handles, ss.Handles.Ai);
                    if (!ReferenceEquals(p, null) && p.Pointer != IntPtr.Zero && p.IsOk)
                        activePaths++;
                }
            }

            float avgSupp = sqSnap.UnitCount > 0 ? suppSum / sqSnap.UnitCount : 0f;
            float decayedLosses = 0f;
            bool hasThreat = false;
            bool threatArmor = false;
            int threatDist = 0;
            if (modEnabled && _squadBlackboards.TryGetValue(sqPtr, out SquadBlackboard b))
            {
                decayedLosses = b.DecayedRecentDeaths;
                hasThreat = b.HasActiveThreat;
                threatArmor = b.ThreatIsArmor;
                if (hasThreat)
                    threatDist = Mathf.RoundToInt(Vector3.Distance(sqSnap.RefPos, b.ThreatPosition));
            }
            else if (sqSnap.Leader != null && sqSnap.Leader.aiController != null && sqSnap.Leader.aiController.visibleTarget != null)
            {
                hasThreat = true;
                threatDist = Mathf.RoundToInt(sqSnap.Leader.aiController.distFromTarget);
            }

            _cmdNetSb.Length = 0;
            _cmdNetSb.Append("Losses: ").Append(decayedLosses.ToString("F1"))
                     .Append("  |  AvgSupp: ").Append(Mathf.RoundToInt(avgSupp))
                     .Append("  |  Threat: ");
            if (!hasThreat)
                _cmdNetSb.Append("None");
            else
                _cmdNetSb.Append(threatArmor ? "ARMOR " : "").Append(threatDist).Append('m');

            _cmdNetSb.Append("  |  Live Cyan A* Paths: ").Append(activePaths).Append('/').Append(sqSnap.UnitCount);
            _cmdNetCardLine2 = _cmdNetSb.ToString();
        }

        private static void FormatCommandNetMacroSummaryCard(string yellowRefFac, string redRefFac, bool modEnabled)
        {
            string yName = string.IsNullOrEmpty(yellowRefFac) ? "Team 1" : AiCore.FormatFactionWithAlliance(yellowRefFac);
            string rName = string.IsNullOrEmpty(redRefFac) ? "Opposing Force" : AiCore.FormatFactionWithAlliance(redRefFac);

            BattleManager bm = BattleManager.instance;
            BattlePhase phase = AiCore.TryGetCurrentBattlePhase(bm);
            _cmdNetObjBuf.Clear();
            AiCore.CollectActiveMissionObjectives(phase, _cmdNetObjBuf);

            int tallyCount = Mathf.Min(_cmdNetObjBuf.Count, _cmdNetTallies.Length);
            for (int i = 0; i < tallyCount; i++)
            {
                MissionObjective obj = _cmdNetObjBuf[i];
                _cmdNetTallies[i] = new CmdNetObjTally
                {
                    TaskPtr = (obj != null && obj.missionTask != null) ? obj.missionTask.Pointer : IntPtr.Zero,
                    ShortLabel = GetShortObjectiveCode(obj, i),
                    YellowCount = 0,
                    RedCount = 0
                };
            }

            int yTotal = 0, yHq = 0, yCut = 0, yGar = 0, yFlank = 0;
            int rTotal = 0, rHq = 0, rCut = 0, rGar = 0, rFlank = 0;
            float now = Time.time;

            int sqCount = AiCore.WorldSquadCount;
            var squads = AiCore.WorldSquads;
            for (int s = 0; s < sqCount; s++)
            {
                ref AiCore.SquadSnapshot sqSnap = ref squads[s];
                if (sqSnap.UnitCount <= 0 || sqSnap.Ref == null || sqSnap.RefOnPlane)
                    continue;

                bool isY = !string.IsNullOrEmpty(yellowRefFac) && AiCore.AreSameSideFactions(sqSnap.Faction, yellowRefFac);
                bool linked = sqSnap.HasRadioOrRelay;
                bool isGar = modEnabled && _garrisonAssignedObjectiveBySquad.ContainsKey(sqSnap.Ptr);
                bool isFlank = modEnabled &&
                               _squadBlackboards.TryGetValue(sqSnap.Ptr, out SquadBlackboard b) &&
                               b.NetDirective == SquadNetDirective.Hammer_WideFlankEnvelopment &&
                               now < b.NetDirectiveExpireTime;

                if (isY)
                {
                    yTotal++;
                    if (linked) yHq++; else yCut++;
                    if (isGar) yGar++;
                    if (isFlank) yFlank++;
                }
                else
                {
                    rTotal++;
                    if (linked) rHq++; else rCut++;
                    if (isGar) rGar++;
                    if (isFlank) rFlank++;
                }

                IntPtr tPtr = sqSnap.Ref.currentTask != null ? sqSnap.Ref.currentTask.Pointer : IntPtr.Zero;
                if (tPtr != IntPtr.Zero)
                {
                    for (int i = 0; i < tallyCount; i++)
                    {
                        if (_cmdNetTallies[i].TaskPtr == tPtr)
                        {
                            if (isY) _cmdNetTallies[i].YellowCount++;
                            else _cmdNetTallies[i].RedCount++;
                            break;
                        }
                    }
                }
            }

            int artyZones = 0;
            if (modEnabled)
            {
                int fCount = FriendlyArtilleryZoneCount;
                for (int i = 0; i < fCount; i++)
                {
                    if (now < FriendlyArtilleryZones[i].ExpireTime)
                        artyZones++;
                }
                var pockets = ActiveShellingPockets;
                for (int i = 0; i < pockets.Length; i++)
                {
                    if (now < pockets[i].ExpireTime)
                        artyZones++;
                }
            }

            _cmdNetSb.Length = 0;
            _cmdNetSb.Append(modEnabled ? $"[BATTLE HARDENED v{Plugin.PluginVersion}  |  COMMAND NET]" : "[VANILLA ER2 AI  |  A/B OBSERVER (F8 OFF)]")
                     .Append("   \u2022   [BLUE] ").Append(yName)
                     .Append("   vs   [RED] ").Append(rName)
                     .Append("   \u2022   [MAGENTA] Hover/Flank");
            _cmdNetCardLine0 = _cmdNetSb.ToString();

            _cmdNetSb.Length = 0;
            _cmdNetSb.Append("[BLUE] ").Append(yTotal).Append(" sq (").Append(yHq).Append(" HQ, ").Append(yCut).Append(" Cut)  |  Flags:");
            AppendFlagTally(_cmdNetSb, tallyCount, countYellow: true);
            _cmdNetSb.Append("  |  Gar:").Append(yGar).Append("  Flank:").Append(yFlank);
            if (modEnabled)
                _cmdNetSb.Append("  Pivots:").Append(MacroObjectivePivotsCount);
            _cmdNetCardLine1 = _cmdNetSb.ToString();

            _cmdNetSb.Length = 0;
            _cmdNetSb.Append("[RED]    ").Append(rTotal).Append(" sq (").Append(rHq).Append(" HQ, ").Append(rCut).Append(" Cut)  |  Flags:");
            AppendFlagTally(_cmdNetSb, tallyCount, countYellow: false);
            _cmdNetSb.Append("  |  Gar:").Append(rGar).Append("  Flank:").Append(rFlank)
                     .Append("  |  Arty:").Append(artyZones).Append("  (Aim near squad for SL+A*)");
            _cmdNetCardLine2 = _cmdNetSb.ToString();
        }

        private static void AppendFlagTally(StringBuilder sb, int tallyCount, bool countYellow)
        {
            if (tallyCount <= 0)
            {
                sb.Append(" --");
                return;
            }

            bool any = false;
            for (int i = 0; i < tallyCount; i++)
            {
                int c = countYellow ? _cmdNetTallies[i].YellowCount : _cmdNetTallies[i].RedCount;
                sb.Append(' ').Append(_cmdNetTallies[i].ShortLabel).Append('(').Append(c).Append(')');
                any = true;
            }
            if (!any)
                sb.Append(" --");
        }

        private static bool TryProjectSegmentToGui(
            Camera cam,
            Vector3 camPos,
            Vector3 camFwd,
            Vector3 worldA,
            Vector3 worldB,
            out Vector2 guiA,
            out Vector2 guiB)
        {
            float dA = Vector3.Dot(worldA - camPos, camFwd);
            float dB = Vector3.Dot(worldB - camPos, camFwd);
            const float nearZ = 0.75f;
            if (dA < nearZ && dB < nearZ)
            {
                guiA = default;
                guiB = default;
                return false;
            }

            if (dA < nearZ)
            {
                float t = (nearZ - dA) / (dB - dA);
                worldA = Vector3.Lerp(worldA, worldB, Mathf.Clamp01(t));
            }
            else if (dB < nearZ)
            {
                float t = (nearZ - dB) / (dA - dB);
                worldB = Vector3.Lerp(worldB, worldA, Mathf.Clamp01(t));
            }

            Vector3 sA = cam.WorldToScreenPoint(worldA);
            Vector3 sB = cam.WorldToScreenPoint(worldB);
            float sh = Screen.height;
            guiA = new Vector2(sA.x, sh - sA.y);
            guiB = new Vector2(sB.x, sh - sB.y);
            return true;
        }

        private static void DrawScreenLine(Vector2 a, Vector2 b, float width, Color color)
        {
            Vector2 d = b - a;
            float len = d.magnitude;
            if (len < 1.5f)
                return;

            float sw = Screen.width;
            float sh = Screen.height;
            if ((a.x < -90f && b.x < -90f) || (a.x > sw + 90f && b.x > sw + 90f) ||
                (a.y < -90f && b.y < -90f) || (a.y > sh + 90f && b.y > sh + 90f))
            {
                return;
            }

            float angle = Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg;
            Matrix4x4 prevMatrix = GUI.matrix;
            Color prevColor = GUI.color;
            GUI.color = color;
            GUIUtility.RotateAroundPivot(angle, a);
            GUI.DrawTexture(new Rect(a.x, a.y - width * 0.5f, len, width), Texture2D.whiteTexture);
            GUI.matrix = prevMatrix;
            GUI.color = prevColor;
        }

        private static void DrawScreenArrow(Vector2 from, Vector2 to, float width, Color color)
        {
            Vector2 d = to - from;
            float len = d.magnitude;
            if (len < 2.0f)
                return;

            DrawScreenLine(from, to, width, color);

            if (len >= 12.0f)
            {
                Vector2 dir = d / len;
                float headLen = Mathf.Clamp(len * 0.18f, 6.0f, 11.0f);
                Vector2 back = -dir * headLen;
                Vector2 perp = new Vector2(-dir.y, dir.x) * (headLen * 0.45f);
                DrawScreenLine(to, to + back + perp, width, color);
                DrawScreenLine(to, to + back - perp, width, color);
            }
        }

        private static void DrawProjectedGroundCircle(Camera cam, Vector3 camPos, Vector3 camFwd, Vector3 center, float radius, float width, Color color)
        {
            const int segs = 16;
            float y = center.y + 0.6f;
            float sh = Screen.height;

            for (int i = 0; i < segs; i++)
            {
                float ang = i * (Mathf.PI * 2f / segs);
                Vector3 wp = new Vector3(center.x + Mathf.Cos(ang) * radius, y, center.z + Mathf.Sin(ang) * radius);
                float d = Vector3.Dot(wp - camPos, camFwd);
                if (d > 0.75f)
                {
                    Vector3 sp = cam.WorldToScreenPoint(wp);
                    _cmdNetRingPts[i] = new Vector2(sp.x, sh - sp.y);
                    _cmdNetRingOk[i] = true;
                }
                else
                {
                    _cmdNetRingOk[i] = false;
                }
            }

            for (int i = 0; i < segs; i++)
            {
                int next = (i + 1) % segs;
                if (_cmdNetRingOk[i] && _cmdNetRingOk[next])
                {
                    DrawScreenLine(_cmdNetRingPts[i], _cmdNetRingPts[next], width, color);
                }
            }
        }

        internal static void DrawSpectatorCommandNetWorldOverlay()
        {
            long profStart = HitchProfiler.Begin();
            try
            {
                if (!_specCommandNetActive || Event.current == null || Event.current.type != EventType.Repaint)
                    return;

                if (ActiveSpectatorHudMode != SpectatorTacticalHudMode.CommandNetOverview || !IsCommandNetOverviewAllowed())
                    return;

                Camera cam = ResourcesManager.mainCamera;
                if (cam == null)
                    cam = Camera.main;
                if (cam == null || cam.transform == null)
                    return;

                Vector3 camPos = cam.transform.position;
                Vector3 camFwd = cam.transform.forward;
                float now = Time.time;
                float unscaledNow = Time.unscaledTime;
                bool modEnabled = VisionConfig.EnableMod.Value;

                BattleData bd = null;
                try { bd = BattleManager.GetCurrentBattleData(); } catch { }
                string yellowRefFac = ResolveYellowSideFaction(bd, out _);

                // 1. Artillery Barrage Zones (pulsing Amber/Orange rings)
                if (modEnabled)
                {
                    float pulseAlpha = 0.55f + 0.28f * Mathf.Sin(unscaledNow * 4.5f);
                    Color artyCol = new Color(CmdNetColorAmber.r, CmdNetColorAmber.g, CmdNetColorAmber.b, pulseAlpha);
                    int fCount = FriendlyArtilleryZoneCount;
                    for (int i = 0; i < fCount; i++)
                    {
                        ref ActiveBarragePocket z = ref FriendlyArtilleryZones[i];
                        if (now < z.ExpireTime)
                            DrawProjectedGroundCircle(cam, camPos, camFwd, z.Position, z.Radius, 2.0f, artyCol);
                    }

                    Color shellCol = new Color(1.0f, 0.36f, 0.14f, pulseAlpha * 0.85f);
                    var pockets = ActiveShellingPockets;
                    for (int i = 0; i < pockets.Length; i++)
                    {
                        ref ActiveBarragePocket p = ref pockets[i];
                        if (now < p.ExpireTime)
                            DrawProjectedGroundCircle(cam, camPos, camFwd, p.Position, p.Radius, 1.8f, shellCol);
                    }
                }

                // 2. Macro Squad Cyan Routes
                int sqCount = AiCore.WorldSquadCount;
                var squads = AiCore.WorldSquads;
                IntPtr hoveredLeaderPtr = IntPtr.Zero;
                Vector3 hoveredLeaderPos = Vector3.zero;

                for (int s = 0; s < sqCount; s++)
                {
                    ref AiCore.SquadSnapshot sqSnap = ref squads[s];
                    if (sqSnap.UnitCount <= 0 || sqSnap.Ref == null || sqSnap.RefOnPlane)
                        continue;

                    if (_cmdNetHasHoveredSquad && sqSnap.Ptr == _cmdNetHoveredSquadPtr)
                    {
                        hoveredLeaderPtr = sqSnap.Leader != null ? sqSnap.Leader.Pointer : IntPtr.Zero;
                        hoveredLeaderPos = sqSnap.RefPos + new Vector3(0f, 1.4f, 0f);
                    }

                    if (sqSnap.LeaderOnVehicle && sqSnap.Leader != null)
                    {
                        Vehicle lv = sqSnap.Leader.GetCurrentVehicle();
                        AiCore.VehicleFastHandles vh = lv != null ? AiCore.GetOrCreateVehicleHandles(lv) : null;
                        if (vh != null && (vh.IsStaticEmplacement || vh.IsParachute))
                            continue;
                    }

                    Vector3 startPos = sqSnap.RefPos + new Vector3(0f, 1.4f, 0f);
                    Vector3 destPos = Vector3.zero;
                    bool hasDest = false;
                    float lineWidth = 1.4f;
                    Color routeCol = new Color(1.00f, 0.84f, 0.22f, 0.44f);

                    if (modEnabled &&
                        _squadBlackboards.TryGetValue(sqSnap.Ptr, out SquadBlackboard b) &&
                        b.NetDirective == SquadNetDirective.Hammer_WideFlankEnvelopment &&
                        now < b.NetDirectiveExpireTime &&
                        b.NetDirectiveTargetPos.sqrMagnitude > 1f)
                    {
                        destPos = b.NetDirectiveTargetPos + new Vector3(0f, 1.4f, 0f);
                        hasDest = true;
                        lineWidth = 2.4f;
                        routeCol = new Color(1.00f, 0.18f, 0.86f, 0.92f);
                    }
                    else if (sqSnap.Ref.moveOrderPosition.sqrMagnitude > 1f &&
                             (sqSnap.Ref.moveOrderPosition - sqSnap.RefPos).sqrMagnitude > 36f)
                    {
                        destPos = sqSnap.Ref.moveOrderPosition + new Vector3(0f, 1.4f, 0f);
                        hasDest = true;
                        lineWidth = 1.8f;
                        routeCol = new Color(1.00f, 0.86f, 0.22f, 0.74f);
                    }
                    else if (sqSnap.Ref.currentTask != null && sqSnap.Ref.currentTask.Pointer != IntPtr.Zero)
                    {
                        destPos = sqSnap.Ref.currentTask.GetTaskDestination() + new Vector3(0f, 1.4f, 0f);
                        hasDest = (destPos - startPos).sqrMagnitude > 25f;
                    }

                    if (hasDest &&
                        sqSnap.LeaderOnVehicle &&
                        !sqSnap.LeaderOnGroundVehicle &&
                        (destPos - startPos).sqrMagnitude > 260f * 260f)
                    {
                        hasDest = false;
                    }

                    if (hasDest && TryProjectSegmentToGui(cam, camPos, camFwd, startPos, destPos, out Vector2 guiA, out Vector2 guiB))
                    {
                        DrawScreenArrow(guiA, guiB, lineWidth, routeCol);
                    }
                }

                // 3. Hovered Squad Live A* Polyline Paths (Bright Cyan) + Cohesion Tethers
                var soldiers = AiCore.WorldSoldiers;
                int wCount = AiCore.WorldSoldierCount;

                if (_cmdNetHasHoveredSquad && _cmdNetHoveredSquadPtr != IntPtr.Zero)
                {
                    Color tetherCol = new Color(0.32f, 1.00f, 0.68f, 0.68f);
                    Color pathCol = new Color(1.00f, 0.22f, 0.88f, 0.98f);

                    for (int i = 0; i < wCount; i++)
                    {
                        ref AiCore.SoldierSnapshot ss = ref soldiers[i];
                        if (ss.SquadPtr != _cmdNetHoveredSquadPtr || ss.IsOnVehicle || ss.HasSurrendered || ss.IsInjured)
                            continue;

                        Vector3 myWPos = ss.Pos + new Vector3(0f, 1.1f, 0f);
                        if (hoveredLeaderPtr != IntPtr.Zero &&
                            ss.Ptr != hoveredLeaderPtr &&
                            TryProjectSegmentToGui(cam, camPos, camFwd, myWPos, hoveredLeaderPos, out Vector2 gSol, out Vector2 gLdr))
                        {
                            DrawScreenLine(gSol, gLdr, 1.2f, tetherCol);
                        }

                        SoldierAI ai = ss.Handles != null ? ss.Handles.Ai : (ss.Ref != null ? ss.Ref.aiController : null);
                        if (ReferenceEquals(ai, null) || ai == null)
                            continue;

                        bool drewPoly = false;
                        try
                        {
                            A.d path = ss.Handles != null ? AiCore.GetCachedAiPath(ss.Handles, ai) : ai.path;
                            if (!ReferenceEquals(path, null) && path.Pointer != IntPtr.Zero && path.IsOk && path.NodesCount > 0)
                            {
                                var nodes = path.B();
                                if (nodes != null && nodes.Length > 0)
                                {
                                    Vector3 prevW = myWPos;
                                    int maxNodes = Mathf.Min(nodes.Length, 14);
                                    for (int k = 0; k < maxNodes; k++)
                                    {
                                        Vector3 nextW = nodes[k].point + new Vector3(0f, 0.8f, 0f);
                                        if (TryProjectSegmentToGui(cam, camPos, camFwd, prevW, nextW, out Vector2 g0, out Vector2 g1))
                                            DrawScreenLine(g0, g1, 2.3f, pathCol);
                                        prevW = nextW;
                                    }
                                    Vector3 endW = path.Destination + new Vector3(0f, 0.8f, 0f);
                                    if (TryProjectSegmentToGui(cam, camPos, camFwd, prevW, endW, out Vector2 gl0, out Vector2 gl1))
                                        DrawScreenArrow(gl0, gl1, 2.3f, pathCol);
                                    drewPoly = true;
                                }
                            }
                        }
                        catch { }

                        if (!drewPoly && ai.MoveDestination.sqrMagnitude > 1f)
                        {
                            Vector3 mdW = ai.MoveDestination + new Vector3(0f, 0.8f, 0f);
                            if ((mdW - myWPos).sqrMagnitude > 4.0f &&
                                TryProjectSegmentToGui(cam, camPos, camFwd, myWPos, mdW, out Vector2 gm0, out Vector2 gm1))
                            {
                                DrawScreenArrow(gm0, gm1, 1.8f, new Color(1.00f, 0.22f, 0.88f, 0.78f));
                            }
                        }
                    }
                }

                // 4. Crisp Overhead Screen Pips (Through-Canopy Visibility)
                Color borderCol = new Color(0.02f, 0.02f, 0.03f, 0.90f);
                float sh = Screen.height;
                float sw = Screen.width;

                for (int i = 0; i < wCount; i++)
                {
                    ref AiCore.SoldierSnapshot ss = ref soldiers[i];
                    if (ss.IsOnVehicle || ss.IsInjured)
                        continue;

                    Vector3 worldPip = ss.Pos + new Vector3(0f, 1.6f, 0f);
                    if (Vector3.Dot(worldPip - camPos, camFwd) <= 0.75f)
                        continue;

                    Vector3 sp = cam.WorldToScreenPoint(worldPip);
                    float gx = sp.x;
                    float gy = sh - sp.y;
                    if (gx < -12f || gx > sw + 12f || gy < -12f || gy > sh + 12f)
                        continue;

                    if (ss.HasSurrendered)
                    {
                        float psz = 4f;
                        float phalf = psz * 0.5f;
                        GUI.color = borderCol;
                        GUI.DrawTexture(new Rect(gx - phalf - 1f, gy - phalf - 1f, psz + 2f, psz + 2f), Texture2D.whiteTexture);
                        GUI.color = Color.white;
                        GUI.DrawTexture(new Rect(gx - phalf, gy - phalf, psz, psz), Texture2D.whiteTexture);
                        continue;
                    }

                    bool isY = !string.IsNullOrEmpty(yellowRefFac) && AiCore.AreSameSideFactions(ss.Faction, yellowRefFac);
                    bool isHovSq = _cmdNetHasHoveredSquad && ss.SquadPtr == _cmdNetHoveredSquadPtr;
                    bool isHovSl = isHovSq && ss.IsSquadLeader;
                    bool isHovMember = isHovSq && !ss.IsSquadLeader;
                    float sz = isHovSl ? 9f : (isHovMember ? 6f : (ss.IsSquadLeader ? 7f : 4f));
                    float half = sz * 0.5f;

                    GUI.color = (isHovSl || isHovMember) ? Color.white : borderCol;
                    GUI.DrawTexture(new Rect(gx - half - 1f, gy - half - 1f, sz + 2f, sz + 2f), Texture2D.whiteTexture);

                    GUI.color = isHovSl ? CmdNetColorCyan : (isHovMember ? CmdNetColorHoverMember : (isY ? GetGameFriendlyBlue() : GetGameEnemyRed()));
                    GUI.DrawTexture(new Rect(gx - half, gy - half, sz, sz), Texture2D.whiteTexture);
                }

                GUI.color = Color.white;
            }
            catch (Exception ex)
            {
                AiCore.ReportPatchException("SpecCmdNet.World", ex);
            }
            finally
            {
                HitchProfiler.End(HitchBucket.SpecCmdNet, profStart);
            }
        }

        internal static void DrawSpectatorCenterReticle(float cx, float cy, Color tint, bool hovered)
        {
            Color prev = GUI.color;
            GUI.color = new Color(0.02f, 0.03f, 0.04f, hovered ? 0.85f : 0.55f);
            GUI.DrawTexture(new Rect(cx - 2f, cy - 2f, 4f, 4f), Texture2D.whiteTexture);

            GUI.color = tint;
            GUI.DrawTexture(new Rect(cx - 1f, cy - 1f, 2f, 2f), Texture2D.whiteTexture);

            float tickGap = hovered ? 3.5f : 3.0f;
            float tickLen = hovered ? 4.0f : 3.0f;
            GUI.color = new Color(tint.r, tint.g, tint.b, tint.a * 0.72f);
            GUI.DrawTexture(new Rect(cx - tickGap - tickLen, cy - 0.5f, tickLen, 1f), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(cx + tickGap, cy - 0.5f, tickLen, 1f), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(cx - 0.5f, cy - tickGap - tickLen, 1f, tickLen), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(cx - 0.5f, cy + tickGap, 1f, tickLen), Texture2D.whiteTexture);
            GUI.color = prev;
        }

        internal static void DrawSpectatorCommandNetHudCard(float virtualW, float virtualH, float toastBottomY)
        {
            long profStart = HitchProfiler.Begin();
            try
            {
                if (!_specCommandNetActive)
                    return;

                // Minimal crisp center reticle in Free Camera
                if (_specCommandNetIsFreeCam)
                {
                    DrawSpectatorCenterReticle(
                        virtualW * 0.5f,
                        virtualH * 0.5f,
                        _cmdNetHasHoveredSquad
                            ? CmdNetColorCyan
                            : new Color(0.92f, 0.96f, 1.0f, 0.48f),
                        _cmdNetHasHoveredSquad);
                }

                if (string.IsNullOrEmpty(_cmdNetCardLine0))
                {
                    GUI.color = Color.white;
                    return;
                }

                float cardW = Mathf.Min(virtualW - 44f, 980f);
                float cardH = 82f;
                float cardX = (virtualW - cardW) * 0.5f;
                float cardY = toastBottomY > 0f ? Mathf.Max(68f, toastBottomY + 8f) : 68f;

                GUI.color = new Color(0.03f, 0.04f, 0.06f, 0.92f);
                GUI.Box(new Rect(cardX, cardY, cardW, cardH), "");
                GUI.Box(new Rect(cardX + 2f, cardY + 2f, cardW - 4f, cardH - 4f), "");

                bool modEnabled = VisionConfig.EnableMod.Value;
                GUI.color = !modEnabled
                    ? CmdNetColorAmber
                    : (_cmdNetHasHoveredSquad ? CmdNetColorCyan : GetGameFriendlyBlue());
                GUI.DrawTexture(new Rect(cardX + 2f, cardY + 2f, cardW - 4f, 3f), Texture2D.whiteTexture);

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
                GUI.color = !modEnabled
                    ? new Color(1.0f, 0.74f, 0.30f, 1.0f)
                    : (_cmdNetHasHoveredSquad ? (_cmdNetHoverIsYellow ? GetGameFriendlyBlueSl() : GetGameEnemyRedSl()) : GetGameFriendlyBlue());
                GUI.Label(new Rect(cardX + 14f, cardY + 7f, cardW - 28f, 22f), _cmdNetCardLine0);

                if (lbl != null)
                {
                    lbl.fontSize = 13;
                    lbl.fontStyle = FontStyle.Normal;
                }
                GUI.color = new Color(0.62f, 0.84f, 1.0f, 1.0f);
                GUI.Label(new Rect(cardX + 14f, cardY + 31f, cardW - 28f, 21f), _cmdNetCardLine1);

                GUI.color = !_cmdNetHasHoveredSquad ? new Color(1.0f, 0.70f, 0.66f, 0.98f) : new Color(0.90f, 0.93f, 0.97f, 0.98f);
                GUI.Label(new Rect(cardX + 14f, cardY + 54f, cardW - 28f, 21f), _cmdNetCardLine2);

                if (lbl != null)
                {
                    lbl.fontSize = oldSize;
                    lbl.fontStyle = oldStyle;
                    lbl.alignment = oldAlign;
                    lbl.wordWrap = oldWrap;
                }
                GUI.color = Color.white;
            }
            catch (Exception ex)
            {
                AiCore.ReportPatchException("SpecCmdNet.Hud", ex);
            }
            finally
            {
                HitchProfiler.End(HitchBucket.SpecCmdNet, profStart);
            }
        }
    }
}
