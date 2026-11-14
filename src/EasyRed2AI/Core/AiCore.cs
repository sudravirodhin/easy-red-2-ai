using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Corvostudio.Weapons;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;

namespace EasyRed2HumanAI
{
    /// <summary>
    /// Shared core dependency layer for EasyRed2HumanAI.
    /// Centralizes Photon Co-op ownership checks, global bot RPC rate-limiting,
    /// solid-environment raycasts (ResourcesManager.groundMask), overhead ceiling caching,
    /// deterministic hashing, and dictionary/hashset memory hygiene.
    /// </summary>
    public static class AiCore
    {
        private static int _cachedSolidEnvMask;
        private static int _cachedSmokeOcclusionMask;
        private static IntPtr _cachedWaterInstancePtr;
        private static Renderer _cachedWaterRenderer;
        private static float _nextAllowedBotRpcTime;
        private static float _nextRoomCheckTime;
        private static int _cachedRoomPlayerCount = 1;
        private static IntPtr _lastPublishedRoomPtr;
        private static int _lastHeavyWorkFrame = -1;
        private static int _lastDirectorWorkFrame = -1;
        private static int _lastDebugLogWriteFrame = -1;
        [ThreadStatic]
        private static StringBuilder _sharedLogBuilder;
        private static readonly List<IntPtr> _sharedPruneBuffer = new List<IntPtr>(256);
        private static readonly Dictionary<IntPtr, Vehicle> _turretVehicleCache = new Dictionary<IntPtr, Vehicle>(64);
        private static readonly Dictionary<IntPtr, TurretFastTraits> _turretTraitsCache = new Dictionary<IntPtr, TurretFastTraits>(96);
        private static readonly Dictionary<long, bool> _ceilingCache = new Dictionary<long, bool>(512);
        private static readonly Dictionary<long, bool> _bridgeCellCache = new Dictionary<long, bool>(512);
        private static readonly Dictionary<string, float> _debugLogCooldowns = new Dictionary<string, float>(32);
        private static readonly Dictionary<int, (bool HasMod, float ExpireTime)> _playerModdedCache = new Dictionary<int, (bool HasMod, float ExpireTime)>(16);
        private static readonly List<Vector3> _cachedRemotePlayerPositions = new List<Vector3>(12);
        private static float _nextRemotePlayerPosScanTime;

        // =====================================================================
        // Zero-Allocation Faction Relation & Entity Fast-Handle Caches
        // Eliminates thousands of per-frame IL2CPP string marshaling calls
        // (ManagedStringToIl2Cpp / Il2CppStringToManaged) and finalizable
        // Il2CppObjectBase wrapper GCHandle allocations across 100+ bots.
        // =====================================================================
        public enum CachedDestKind : byte
        {
            None = 0,
            Other = 1,
            CombatCover = 2,
            VehicleRepair = 3,
            MedicHeal = 4
        }

        public sealed class SoldierFastHandles
        {
            public IntPtr Ptr;
            public IntPtr AiPtr;
            public int DiagId;
            public Soldier Ref;
            public SoldierAI Ai;
            public Transform Tr;
            public Transform RootTr;
            public SyncSoldier Syncher;
            public bool SyncherChecked;
            public string Faction = string.Empty;
            public float NextFactionRefreshTime;

            public float CachedOpticZoom = 1.0f;
            public float NextOpticCheckTime;

            public float CachedCamoMultiplier = 1.0f;
            public float NextCamoCheckTime;

            public AttachmentBipod CachedBipod;
            public bool CachedIsGunner;
            public float NextBipodCheckTime;

            public IntPtr CachedDestPtr;
            public CachedDestKind DestKind;
            public CombatCover CachedCover;
            public VehicleRepairTask CachedRepairTask;
            public MedicHealingDestination CachedHealDest;

            public Squad CachedSquad;
            public float NextSquadRefreshTime;

            public int LastMoveTimeFrame = -1;
            public bool CachedIsMoveTimeResult;
            public int LastEndpointFrame = -1;
            public Vector3 CachedEndpoint;
            public int CachedNodesCount;
        }

        public sealed class VehicleFastHandles
        {
            public IntPtr Ptr;
            public Vehicle Ref;
            public Transform Tr;
            public Transform RootTr;
            public SyncVehicle Syncher;
            public bool SyncherChecked;
            public bool IsAir;
            public bool IsWheeledOrTank;
            public bool IsTank;
            public bool IsPlane;
            public bool IsParachute;
            public bool IsStaticEmplacement;

            public bool CommanderSeatChecked;
            public int CommanderSeatIndex = -1;
            public bool HasDedicatedCommanderTurret;
            public float NextSpotterCheckTime;
            public bool CachedHasAliveCommanderOrExposedSpotter;
            public bool CachedHasCloseFriendlyEscort;
            public Spottable CommanderSpottedTarget;
            public float CommanderSpotTime;
        }

        public struct TurretFastTraits
        {
            public Vehicle RootVehicle;
            public VehicleFastHandles VehHandles;
            public bool IsCommanderTurret;
            public bool IsExposedCrewSeat;
            public bool IsAA;
            public bool IsIndirectOrArty;
            public float AimFov;
        }

        private struct EnemySpawnDirCacheEntry
        {
            public bool HasCentroid;
            public Vector3 Centroid;
            public float ExpireTime;
        }

        private static readonly Dictionary<string, string> _internedFactions = new Dictionary<string, string>(16, StringComparer.Ordinal);
        private static readonly Dictionary<(string, string), bool> _sameSideFactionCache = new Dictionary<(string, string), bool>(32);
        private static readonly Dictionary<(string, string), bool> _enemyFactionCache = new Dictionary<(string, string), bool>(32);
        private static readonly Dictionary<IntPtr, SoldierFastHandles> _soldierHandlesByPtr = new Dictionary<IntPtr, SoldierFastHandles>(256);
        private static readonly Dictionary<IntPtr, SoldierFastHandles> _soldierHandlesByAiPtr = new Dictionary<IntPtr, SoldierFastHandles>(256);
        private static readonly Dictionary<IntPtr, VehicleFastHandles> _vehicleHandlesByPtr = new Dictionary<IntPtr, VehicleFastHandles>(96);
        private static readonly HashSet<IntPtr> _nonSoldierCreaturePtrs = new HashSet<IntPtr>(64);
        private static readonly HashSet<IntPtr> _nonUnitSpottablePtrs = new HashSet<IntPtr>(64);
        private static readonly Dictionary<(IntPtr, string), EnemySpawnDirCacheEntry> _enemySpawnDirCache = new Dictionary<(IntPtr, string), EnemySpawnDirCacheEntry>(16);
        private static readonly List<SoldierFastHandles> _cachedDetachedSoldiers = new List<SoldierFastHandles>(32);
        private static float _nextDetachedCreatureScanTime;
        private static int _mpRoomStateFrame = -1;
        private static bool _mpInRoomCached;
        private static bool _mpIsMasterCached = true;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static string InternFaction(string raw)
        {
            if (string.IsNullOrEmpty(raw))
                return string.Empty;
            if (_internedFactions.TryGetValue(raw, out string canonical))
                return canonical;
            _internedFactions[raw] = raw;
            return raw;
        }

        public static bool AreSameSideFactions(string fA, string fB)
        {
            if (string.IsNullOrEmpty(fA) || string.IsNullOrEmpty(fB))
                return false;
            if (ReferenceEquals(fA, fB) || fA == fB)
                return true;

            var key = (fA, fB);
            if (_sameSideFactionCache.TryGetValue(key, out bool same))
                return same;

            same = ResourcesManager.IsSameFaction(fA, fB);
            if (_sameSideFactionCache.Count > 128)
                _sameSideFactionCache.Clear();
            _sameSideFactionCache[key] = same;
            _sameSideFactionCache[(fB, fA)] = same;
            return same;
        }

        public static bool AreEnemyFactions(string fA, string fB)
        {
            if (string.IsNullOrEmpty(fA) || string.IsNullOrEmpty(fB))
                return false;
            if (ReferenceEquals(fA, fB) || fA == fB)
                return false;

            var key = (fA, fB);
            if (_enemyFactionCache.TryGetValue(key, out bool enemy))
                return enemy;

            enemy = ResourcesManager.IsEnemyFaction(fA, fB);
            if (_enemyFactionCache.Count > 128)
                _enemyFactionCache.Clear();
            _enemyFactionCache[key] = enemy;
            _enemyFactionCache[(fB, fA)] = enemy;
            return enemy;
        }

        private static void EnsureMpRoomStateCached()
        {
            int frame = Time.frameCount;
            if (_mpRoomStateFrame == frame)
                return;

            _mpRoomStateFrame = frame;
            try
            {
                bool inRoom = PhotonNetwork.InRoom && !PhotonNetwork.OfflineMode;
                _mpInRoomCached = inRoom;
                _mpIsMasterCached = !inRoom || PhotonNetwork.IsMasterClient;
            }
            catch
            {
                _mpInRoomCached = false;
                _mpIsMasterCached = true;
            }
        }

        public static bool IsInMultiplayerRoom()
        {
            EnsureMpRoomStateCached();
            return _mpInRoomCached;
        }

        private static int _nextSoldierDiagId;
        private static int _nextSquadDiagId;
        private static readonly Dictionary<IntPtr, int> _squadDiagIds = new Dictionary<IntPtr, int>(96);
        private static readonly Dictionary<string, string> _formattedFactionDisplayCache = new Dictionary<string, string>(32, StringComparer.Ordinal);
        private static readonly HashSet<IntPtr> _confirmedVehicleCrewSquads = new HashSet<IntPtr>(64);
        private static readonly HashSet<IntPtr> _confirmedInfantrySquads = new HashSet<IntPtr>(128);

        public static void MarkConfirmedVehicleCrewSquad(Squad sq)
        {
            if (sq == null || sq.Pointer == IntPtr.Zero)
                return;
            _confirmedInfantrySquads.Remove(sq.Pointer);
            _confirmedVehicleCrewSquads.Add(sq.Pointer);
        }

        public static void PruneSquadClassificationCaches(HashSet<IntPtr> activeSquadPtrs)
        {
            if (activeSquadPtrs == null)
                return;
            PruneSet(_confirmedVehicleCrewSquads, activeSquadPtrs);
            PruneSet(_confirmedInfantrySquads, activeSquadPtrs);
        }

        /// <summary>
        /// Native Squad.get_IsVehicleCrew (0x18080D0A0) literally compiles to `!this.isOpenForJoiners`,
        /// which returns true for EVERY closed/scripted foot-infantry squad and Volga/beach landing-boat squad!
        /// This helper classifies genuine Tank, Aircraft, and Combat-Vehicle crews (and remembers them after bailout)
        /// without ever misclassifying closed infantry or troop-transport squads.
        /// </summary>
        public static bool IsTrueVehicleCrewSquad(Squad sq)
        {
            if (ReferenceEquals(sq, null) || sq == null || sq.Pointer == IntPtr.Zero)
                return false;

            if (sq.isOpenForJoiners)
                return false;

            IntPtr ptr = sq.Pointer;
            if (_confirmedVehicleCrewSquads.Contains(ptr))
                return true;
            if (_confirmedInfantrySquads.Contains(ptr))
                return false;

            try
            {
                var units = sq.units;
                int uCount = units != null ? units.Count : 0;
                if (uCount <= 0)
                    return false;

                if (uCount > 5 || sq.isChargeSquad)
                {
                    _confirmedInfantrySquads.Add(ptr);
                    return false;
                }

                for (int i = 0; i < uCount; i++)
                {
                    Soldier u = units[i];
                    if (ReferenceEquals(u, null) || u == null || u.Pointer == IntPtr.Zero)
                        continue;

                    if (u.IsMedic() || u.hasSyringe || u.hasMg || u.isMarksMan || u.IsRadioman() || u.IsAmmoBoxCarrier() || u.isAtUnit)
                    {
                        _confirmedInfantrySquads.Add(ptr);
                        return false;
                    }

                    if (u.IsOnVehicle())
                    {
                        Vehicle veh = u.GetCurrentVehicle();
                        VehicleFastHandles vh = GetOrCreateVehicleHandles(veh);
                        if (vh != null && !vh.IsParachute)
                        {
                            if (vh.IsTank || vh.IsPlane || vh.IsAir)
                            {
                                _confirmedVehicleCrewSquads.Add(ptr);
                                return true;
                            }

                            if (veh.IsTransportVehicle())
                            {
                                _confirmedInfantrySquads.Add(ptr);
                                return false;
                            }

                            VehicleWithWheels wheeled = veh.TryCast<VehicleWithWheels>();
                            if (wheeled != null)
                            {
                                if (wheeled.RetreatCrewOnDestroy() && !wheeled.IsTransportVehicle())
                                {
                                    _confirmedVehicleCrewSquads.Add(ptr);
                                    return true;
                                }
                                if (wheeled.IsTransportVehicle())
                                {
                                    _confirmedInfantrySquads.Add(ptr);
                                    return false;
                                }
                            }
                        }
                    }
                }

                Soldier mech = sq.GetMechanic();
                if (mech != null && mech.Pointer != IntPtr.Zero && mech.GetRepairTools() != null)
                {
                    _confirmedVehicleCrewSquads.Add(ptr);
                    return true;
                }

                if (sq.fullySpawned)
                    _confirmedInfantrySquads.Add(ptr);
            }
            catch { }

            return false;
        }

        public static string FormatFactionWithAlliance(string rawFaction)
        {
            if (string.IsNullOrWhiteSpace(rawFaction))
                return string.Empty;

            if (_formattedFactionDisplayCache.TryGetValue(rawFaction, out string cached))
                return cached;

            string trimmed = rawFaction.Trim();
            string alliance = null;
            string core = trimmed;

            if (core.EndsWith("_axis", StringComparison.OrdinalIgnoreCase))
            {
                alliance = "Axis";
                core = core.Substring(0, core.Length - 5);
            }
            else if (core.EndsWith("_allies", StringComparison.OrdinalIgnoreCase))
            {
                alliance = "Allies";
                core = core.Substring(0, core.Length - 7);
            }
            else
            {
                try
                {
                    if (AreSameSideFactions(trimmed, "Germany_axis") && !AreSameSideFactions(trimmed, "UnitedStates_allies"))
                        alliance = "Axis";
                    else if (AreSameSideFactions(trimmed, "UnitedStates_allies") && !AreSameSideFactions(trimmed, "Germany_axis"))
                        alliance = "Allies";
                }
                catch { }
            }

            string prettyNation;
            if (core.StartsWith("UnitedStates_Afrikan", StringComparison.OrdinalIgnoreCase) ||
                core.Equals("UnitedStates", StringComparison.OrdinalIgnoreCase) ||
                core.Equals("USA", StringComparison.OrdinalIgnoreCase))
            {
                prettyNation = "United States";
            }
            else if (core.StartsWith("Germany", StringComparison.OrdinalIgnoreCase))
            {
                prettyNation = "Germany";
            }
            else if (core.StartsWith("Ussr", StringComparison.OrdinalIgnoreCase) ||
                     core.Equals("SovietUnion", StringComparison.OrdinalIgnoreCase))
            {
                prettyNation = "USSR";
            }
            else if (core.StartsWith("England", StringComparison.OrdinalIgnoreCase) ||
                     core.Equals("UK", StringComparison.OrdinalIgnoreCase) ||
                     core.Equals("GreatBritain", StringComparison.OrdinalIgnoreCase) ||
                     core.Equals("Britain", StringComparison.OrdinalIgnoreCase))
            {
                prettyNation = "Britain";
            }
            else if (core.StartsWith("NewZealand", StringComparison.OrdinalIgnoreCase))
            {
                prettyNation = "New Zealand";
            }
            else if (core.StartsWith("SouthAfrica", StringComparison.OrdinalIgnoreCase))
            {
                prettyNation = "South Africa";
            }
            else
            {
                // Humanize underscores and PascalCase / camelCase for all other vanilla & Workshop nations
                var sb = new System.Text.StringBuilder(core.Length + 8);
                bool prevWasSpace = true;
                for (int i = 0; i < core.Length; i++)
                {
                    char c = core[i];
                    if (c == '_' || c == '-')
                    {
                        if (!prevWasSpace && sb.Length > 0)
                        {
                            sb.Append(' ');
                            prevWasSpace = true;
                        }
                        continue;
                    }

                    if (i > 0 &&
                        !prevWasSpace &&
                        char.IsUpper(c) &&
                        (char.IsLower(core[i - 1]) || (i + 1 < core.Length && char.IsLower(core[i + 1]))))
                    {
                        sb.Append(' ');
                        prevWasSpace = true;
                    }

                    sb.Append(prevWasSpace ? char.ToUpperInvariant(c) : c);
                    prevWasSpace = (c == ' ');
                }
                prettyNation = sb.ToString().Trim();
            }

            if (string.IsNullOrEmpty(prettyNation))
                prettyNation = trimmed;

            string result = !string.IsNullOrEmpty(alliance)
                ? $"{prettyNation} ({alliance})"
                : prettyNation;

            if (_formattedFactionDisplayCache.Count > 64)
                _formattedFactionDisplayCache.Clear();
            _formattedFactionDisplayCache[rawFaction] = result;
            return result;
        }

        public static int GetSquadDiagId(IntPtr sqPtr)
        {
            if (sqPtr == IntPtr.Zero)
                return 0;
            if (_squadDiagIds.TryGetValue(sqPtr, out int id))
                return id;
            if (_squadDiagIds.Count > 512)
                _squadDiagIds.Clear();
            id = ++_nextSquadDiagId;
            _squadDiagIds[sqPtr] = id;
            return id;
        }

        public static SoldierFastHandles GetOrCreateSoldierHandles(Soldier s)
        {
            if (ReferenceEquals(s, null))
                return null;
            IntPtr ptr = s.Pointer;
            if (ptr == IntPtr.Zero)
                return null;

            if (_soldierHandlesByPtr.TryGetValue(ptr, out SoldierFastHandles h) && !ReferenceEquals(h.Ref, null) && h.Ref != null)
            {
                return h;
            }

            if (s == null)
                return null;

            Transform tr = s.transform;
            SoldierAI ai = s.aiController;
            IntPtr aiPtr = (!ReferenceEquals(ai, null) && ai != null) ? ai.Pointer : IntPtr.Zero;
            float now = Time.time;
            float jitter = (GetDeterministicHash(ptr, 31) & 63) * 0.05f;

            h = new SoldierFastHandles
            {
                Ptr = ptr,
                AiPtr = aiPtr,
                DiagId = ++_nextSoldierDiagId,
                Ref = s,
                Ai = ai,
                Tr = tr,
                RootTr = tr != null ? tr.root : null,
                Syncher = s.soldierSyncher,
                SyncherChecked = true,
                Faction = InternFaction(s.faction),
                NextFactionRefreshTime = now + 5.0f + jitter
            };

            _soldierHandlesByPtr[ptr] = h;
            if (aiPtr != IntPtr.Zero)
                _soldierHandlesByAiPtr[aiPtr] = h;
            return h;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryGetSoldierHandles(IntPtr ptr, out SoldierFastHandles h)
        {
            if (ptr != IntPtr.Zero && _soldierHandlesByPtr.TryGetValue(ptr, out h) && !ReferenceEquals(h.Ref, null) && h.Ref != null)
                return true;
            h = null;
            return false;
        }

        public static SoldierFastHandles GetOrCreateSoldierHandlesByAi(SoldierAI ai)
        {
            if (ReferenceEquals(ai, null))
                return null;
            IntPtr aiPtr = ai.Pointer;
            if (aiPtr == IntPtr.Zero)
                return null;

            if (_soldierHandlesByAiPtr.TryGetValue(aiPtr, out SoldierFastHandles h) && !ReferenceEquals(h.Ref, null) && h.Ref != null)
                return h;

            if (ai == null)
                return null;

            Soldier s = ai.character;
            if (s == null || s.Pointer == IntPtr.Zero)
                return null;

            h = GetOrCreateSoldierHandles(s);
            if (h != null)
            {
                h.Ai = ai;
                h.AiPtr = aiPtr;
                _soldierHandlesByAiPtr[aiPtr] = h;
            }
            return h;
        }

        public static bool TryGetSoldierHandlesFromCreature(Creature c, out SoldierFastHandles h)
        {
            h = null;
            if (ReferenceEquals(c, null))
                return false;
            IntPtr ptr = c.Pointer;
            if (ptr == IntPtr.Zero)
                return false;

            if (_soldierHandlesByPtr.TryGetValue(ptr, out h))
            {
                if (!ReferenceEquals(h.Ref, null) && h.Ref != null)
                    return true;
                _soldierHandlesByPtr.Remove(ptr);
                h = null;
                return false;
            }

            if (_nonSoldierCreaturePtrs.Contains(ptr))
                return false;

            Soldier s = c.TryCast<Soldier>();
            if (s == null || s.Pointer == IntPtr.Zero)
            {
                if (_nonSoldierCreaturePtrs.Count > 128)
                    _nonSoldierCreaturePtrs.Clear();
                _nonSoldierCreaturePtrs.Add(ptr);
                return false;
            }

            h = GetOrCreateSoldierHandles(s);
            return h != null;
        }

        public static string GetCachedFaction(SoldierFastHandles h, float now)
        {
            if (h == null)
                return string.Empty;
            if (now >= h.NextFactionRefreshTime && h.Ref != null)
            {
                h.Faction = InternFaction(h.Ref.faction);
                h.NextFactionRefreshTime = now + 5.0f;
            }
            return h.Faction;
        }

        public static string GetCachedFaction(Soldier s)
        {
            SoldierFastHandles h = GetOrCreateSoldierHandles(s);
            return h != null ? GetCachedFaction(h, Time.time) : string.Empty;
        }

        public static float GetCachedOpticZoom(SoldierFastHandles h, float now)
        {
            if (h == null || h.Ref == null)
                return 1.0f;
            if (now < h.NextOpticCheckTime)
                return h.CachedOpticZoom;

            h.NextOpticCheckTime = now + 1.5f;
            float zoom = 1.0f;
            Soldier obs = h.Ref;
            if (obs.HasHeldGun(0))
            {
                GenericGun gun = obs.GetHeldGun(0);
                if (gun != null && (gun.HasScopeWithMagnification() || obs.IsMarksman()))
                {
                    float fovMult = gun.GetAimingFOVMultiplier();
                    if (fovMult > 0.01f && fovMult < 0.85f)
                        zoom = Mathf.Clamp(0.85f / fovMult, 1.0f, VisionConfig.MaxOpticZoomMultiplier.Value);
                    else if (obs.IsMarksman())
                        zoom = Mathf.Min(1.75f, VisionConfig.MaxOpticZoomMultiplier.Value);
                }
            }
            h.CachedOpticZoom = zoom;
            return zoom;
        }

        public static float GetCachedCamoMultiplier(SoldierFastHandles h, float now)
        {
            if (h == null || h.Ref == null)
                return 1.0f;
            if (now < h.NextCamoCheckTime)
                return h.CachedCamoMultiplier;

            h.NextCamoCheckTime = now + 4.0f;
            Soldier s = h.Ref;
            float camoSum = 0f;
            if (s.hasUniform)
            {
                var u = s.GetUniform;
                if (u != null) camoSum += u.CamoPercReduction;
            }
            if (s.hasVest)
            {
                var v = s.GetVest;
                if (v != null) camoSum += v.CamoPercReduction;
            }
            if (s.hasHeadgear)
            {
                var hg = s.GetHeadgear;
                if (hg != null) camoSum += hg.CamoPercReduction;
            }
            float mult = 1.0f - (camoSum / 3.0f) * 0.4f;
            h.CachedCamoMultiplier = mult;
            return mult;
        }

        public static bool GetCachedGunnerAndBipod(SoldierFastHandles h, float now, out AttachmentBipod bipod)
        {
            bipod = null;
            if (h == null || h.Ref == null)
                return false;

            if (now >= h.NextBipodCheckTime)
            {
                h.NextBipodCheckTime = now + 1.4f;
                Soldier s = h.Ref;
                h.CachedBipod = s.GetHeldFunctionalBipod();
                h.CachedIsGunner = h.CachedBipod != null || s.hasMg || s.IsGunner();
            }

            bipod = h.CachedBipod;
            return h.CachedIsGunner;
        }

        public static Squad GetCachedSquad(SoldierFastHandles h, float now)
        {
            if (h == null || h.Ref == null)
                return null;

            if (now >= h.NextSquadRefreshTime)
            {
                h.NextSquadRefreshTime = now + 0.5f;
                Squad sq = h.Ref.joinedSquad;
                h.CachedSquad = (!ReferenceEquals(sq, null) && sq != null) ? sq : null;
            }
            else if (!ReferenceEquals(h.CachedSquad, null) && h.CachedSquad == null)
            {
                h.CachedSquad = null;
            }

            return h.CachedSquad;
        }

        public static CachedDestKind ResolveTargetDestination(SoldierFastHandles h)
        {
            if (h == null || h.Ref == null)
                return CachedDestKind.None;

            AiDestination dest = h.Ref.targetDestination;
            IntPtr destPtr = (!ReferenceEquals(dest, null) && dest != null) ? dest.Pointer : IntPtr.Zero;
            if (destPtr == IntPtr.Zero)
            {
                if (h.CachedDestPtr != IntPtr.Zero)
                {
                    h.CachedDestPtr = IntPtr.Zero;
                    h.DestKind = CachedDestKind.None;
                    h.CachedCover = null;
                    h.CachedRepairTask = null;
                    h.CachedHealDest = null;
                }
                return CachedDestKind.None;
            }

            if (destPtr == h.CachedDestPtr)
                return h.DestKind;

            h.CachedDestPtr = destPtr;
            h.CachedCover = null;
            h.CachedRepairTask = null;
            h.CachedHealDest = null;

            CombatCover cc = dest.TryCast<CombatCover>();
            if (cc != null)
            {
                h.CachedCover = cc;
                h.DestKind = CachedDestKind.CombatCover;
                return CachedDestKind.CombatCover;
            }

            VehicleRepairTask rep = dest.TryCast<VehicleRepairTask>();
            if (rep != null)
            {
                h.CachedRepairTask = rep;
                h.DestKind = CachedDestKind.VehicleRepair;
                return CachedDestKind.VehicleRepair;
            }

            MedicHealingDestination heal = dest.TryCast<MedicHealingDestination>();
            if (heal != null)
            {
                h.CachedHealDest = heal;
                h.DestKind = CachedDestKind.MedicHeal;
                return CachedDestKind.MedicHeal;
            }

            h.DestKind = CachedDestKind.Other;
            return CachedDestKind.Other;
        }

        public static VehicleFastHandles GetOrCreateVehicleHandles(Vehicle v)
        {
            if (ReferenceEquals(v, null))
                return null;
            IntPtr ptr = v.Pointer;
            if (ptr == IntPtr.Zero)
                return null;

            if (_vehicleHandlesByPtr.TryGetValue(ptr, out VehicleFastHandles h) && !ReferenceEquals(h.Ref, null) && h.Ref != null)
                return h;

            if (v == null)
                return null;

            Transform tr = v.transform;
            bool isAir = v.IsAirVehicle();
            bool isWheeledOrTank = !isAir && v.IsWheeledVehicleOrTank();
            bool isPlane = isAir && v.TryCast<VehiclePlane>() != null;
            bool isTank = isWheeledOrTank && v.TryCast<VehicleTank>() != null;
            bool isPara = !isPlane && !isTank && v.TryCast<VehicleParachute>() != null;
            h = new VehicleFastHandles
            {
                Ptr = ptr,
                Ref = v,
                Tr = tr,
                RootTr = tr != null ? tr.root : null,
                Syncher = v.vehicleSyncher,
                SyncherChecked = true,
                IsAir = isAir,
                IsWheeledOrTank = isWheeledOrTank,
                IsPlane = isPlane,
                IsTank = isTank,
                IsParachute = isPara,
                IsStaticEmplacement = !isAir && !isWheeledOrTank && !isPara && v.TryCast<MovableVehicle>() == null
            };

            _vehicleHandlesByPtr[ptr] = h;
            return h;
        }

        public static bool TryResolveSpottableFast(
            Spottable s,
            out SoldierFastHandles solHandles,
            out VehicleFastHandles vehHandles)
        {
            solHandles = null;
            vehHandles = null;
            if (ReferenceEquals(s, null))
                return false;

            IntPtr ptr = s.Pointer;
            if (ptr == IntPtr.Zero)
                return false;

            if (_soldierHandlesByPtr.TryGetValue(ptr, out solHandles))
            {
                if (!ReferenceEquals(solHandles.Ref, null) && solHandles.Ref != null && solHandles.Tr != null && solHandles.Ref.IsAlive)
                    return true;
                solHandles = null;
                return false;
            }

            if (_vehicleHandlesByPtr.TryGetValue(ptr, out vehHandles))
            {
                if (!ReferenceEquals(vehHandles.Ref, null) && vehHandles.Ref != null && vehHandles.Tr != null)
                    return true;
                vehHandles = null;
                return false;
            }

            if (_nonUnitSpottablePtrs.Contains(ptr))
            {
                try
                {
                    Component comp = s.TryCast<Component>();
                    return comp != null && comp.transform != null;
                }
                catch
                {
                    return false;
                }
            }

            try
            {
                Soldier sol = s.TryCast<Soldier>();
                if (sol != null && sol.Pointer != IntPtr.Zero)
                {
                    solHandles = GetOrCreateSoldierHandles(sol);
                    if (solHandles != null && solHandles.Tr != null && sol.IsAlive)
                        return true;
                    solHandles = null;
                    return false;
                }

                Vehicle veh = s.TryCast<Vehicle>();
                if (veh != null && veh.Pointer != IntPtr.Zero)
                {
                    vehHandles = GetOrCreateVehicleHandles(veh);
                    if (vehHandles != null && vehHandles.Tr != null)
                        return true;
                    vehHandles = null;
                    return false;
                }

                Component comp = s.TryCast<Component>();
                if (comp == null || comp.transform == null)
                    return false;

                Creature c = s.TryCast<Creature>();
                if (c != null && !c.IsAlive)
                    return false;

                if (_nonUnitSpottablePtrs.Count > 128)
                    _nonUnitSpottablePtrs.Clear();
                _nonUnitSpottablePtrs.Add(ptr);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static void PruneFastHandleCaches(HashSet<IntPtr> activeSoldierPtrs, HashSet<IntPtr> activeVehiclePtrs)
        {
            if (activeSoldierPtrs != null && _soldierHandlesByPtr.Count > 0)
            {
                _sharedPruneBuffer.Clear();
                foreach (var kvp in _soldierHandlesByPtr)
                {
                    if (!activeSoldierPtrs.Contains(kvp.Key))
                    {
                        _sharedPruneBuffer.Add(kvp.Key);
                        if (kvp.Value.AiPtr != IntPtr.Zero)
                            _soldierHandlesByAiPtr.Remove(kvp.Value.AiPtr);
                    }
                }
                for (int i = 0; i < _sharedPruneBuffer.Count; i++)
                    _soldierHandlesByPtr.Remove(_sharedPruneBuffer[i]);
                _sharedPruneBuffer.Clear();
            }

            if (activeVehiclePtrs != null && _vehicleHandlesByPtr.Count > 0)
            {
                PruneDict(_vehicleHandlesByPtr, activeVehiclePtrs);
            }

            if (_nonSoldierCreaturePtrs.Count > 128)
                _nonSoldierCreaturePtrs.Clear();
            if (_nonUnitSpottablePtrs.Count > 128)
                _nonUnitSpottablePtrs.Clear();
        }

        // Rolling 15s Frame-Time & Hitch Telemetry
        private static float _currentWindowMaxFrameMs;
        private static float _previousWindowMaxFrameMs;
        private static float _nextFrameWindowRolloverTime;
        public static float RollingMaxFrameMs => Mathf.Max(_currentWindowMaxFrameMs, _previousWindowMaxFrameMs);
        public static int TotalHitchesOver28ms;

        public static int TotalPingGovernorSkippedCycles;

        public static void RecordFrameTiming()
        {
            float now = Time.unscaledTime;
            if (now >= _nextFrameWindowRolloverTime)
            {
                _previousWindowMaxFrameMs = _currentWindowMaxFrameMs;
                _currentWindowMaxFrameMs = 0f;
                _nextFrameWindowRolloverTime = now + 15.0f;
            }

            // Ignore scene load / alt-tab / pause frames so telemetry reflects true gameplay frame pacing
            if (Time.timeSinceLevelLoad < 3.0f || Time.timeScale <= 0.01f)
                return;

            float dt = Time.unscaledDeltaTime;
            if (dt <= 0f || dt > 0.50f)
                return;

            float dtMs = dt * 1000.0f;
            if (dtMs > _currentWindowMaxFrameMs)
                _currentWindowMaxFrameMs = dtMs;
            if (dtMs > 28.0f)
                TotalHitchesOver28ms++;
        }

        public static bool CanEmitDebugLog(string category, float minInterval = 4.0f)
        {
            if (!VisionConfig.DebugLogging.Value || Plugin.Log == null)
                return false;

            if (_lastDebugLogWriteFrame == Time.frameCount && category != "Heartbeat")
                return false;

            float now = Time.unscaledTime;
            if (_debugLogCooldowns.TryGetValue(category, out float nextAllowed) && now < nextAllowed)
                return false;

            return true;
        }

        [InterpolatedStringHandler]
        public ref struct DebugLogInterpolatedStringHandler
        {
            private StringBuilder _builder;

            public DebugLogInterpolatedStringHandler(
                int literalLength,
                int formattedCount,
                string category,
                out bool shouldAppend)
            {
                if (!CanEmitDebugLog(category))
                {
                    _builder = null;
                    shouldAppend = false;
                    return;
                }
                StringBuilder sb = _sharedLogBuilder;
                if (sb == null)
                {
                    sb = new StringBuilder(Math.Max(256, literalLength + formattedCount * 12));
                    _sharedLogBuilder = sb;
                }
                else
                {
                    sb.Clear();
                }
                _builder = sb;
                shouldAppend = true;
            }

            public void AppendLiteral(string s) => _builder?.Append(s);
            public void AppendFormatted<T>(T value) => _builder?.Append(value);
            public void AppendFormatted<T>(T value, string format)
            {
                if (_builder == null)
                    return;
                if (value is IFormattable formattable)
                    _builder.Append(formattable.ToString(format, null));
                else
                    _builder.Append(value);
            }

            internal bool IsActive => _builder != null;
            internal string GetFormattedText() => _builder != null ? _builder.ToString() : string.Empty;
        }

        public static void LogDebug(
            string category,
            [InterpolatedStringHandlerArgument("category")] DebugLogInterpolatedStringHandler message,
            float minInterval = 4.0f)
        {
            if (!message.IsActive || !CanEmitDebugLog(category, minInterval))
                return;

            _debugLogCooldowns[category] = Time.unscaledTime + minInterval;
            _lastDebugLogWriteFrame = Time.frameCount;
            Plugin.Log.LogInfo($"[Diag:{category}] {message.GetFormattedText()}");
        }

        public static void LogDebug(string category, string message, float minInterval = 4.0f)
        {
            if (!CanEmitDebugLog(category, minInterval))
                return;

            _debugLogCooldowns[category] = Time.unscaledTime + minInterval;
            _lastDebugLogWriteFrame = Time.frameCount;
            Plugin.Log.LogInfo($"[Diag:{category}] {message}");
        }

        public static int GetConnectedPlayerCount()
        {
            float now = Time.unscaledTime;
            if (now < _nextRoomCheckTime)
                return _cachedRoomPlayerCount;

            _nextRoomCheckTime = now + 1.0f;
            try
            {
                if (PhotonNetwork.InRoom && PhotonNetwork.CurrentRoom != null)
                {
                    _cachedRoomPlayerCount = Mathf.Max(1, (int)PhotonNetwork.CurrentRoom.PlayerCount);

                    IntPtr roomPtr = PhotonNetwork.CurrentRoom.Pointer;
                    if (roomPtr != IntPtr.Zero && roomPtr != _lastPublishedRoomPtr && PhotonNetwork.LocalPlayer != null)
                    {
                        _lastPublishedRoomPtr = roomPtr;
                        _playerModdedCache.Clear();
                        var props = new ExitGames.Client.Photon.Hashtable();
                        props[(Il2CppSystem.String)"bh_ver"] = (Il2CppSystem.String)Plugin.PluginVersion;
                        PhotonNetwork.LocalPlayer.SetCustomProperties(props);
                    }
                }
                else
                {
                    _cachedRoomPlayerCount = 1;
                    if (_lastPublishedRoomPtr != IntPtr.Zero)
                    {
                        _lastPublishedRoomPtr = IntPtr.Zero;
                        _playerModdedCache.Clear();
                    }
                }
            }
            catch
            {
                _cachedRoomPlayerCount = 1;
            }
            return _cachedRoomPlayerCount;
        }

        public static bool IsMultiplayerWithRemotePlayers()
        {
            return GetConnectedPlayerCount() > 1;
        }

        /// <summary>
        /// Returns true when the current frame time is elevated and bot brain cycles should
        /// yield/coast for a cycle so human player ping and Photon packet delivery never spike.
        /// </summary>
        public static bool ShouldSkipBotCycleForNetworkPing()
        {
            float dt = Time.unscaledDeltaTime;
            int players = GetConnectedPlayerCount();
            // Keep thresholds safely above normal 60Hz VSync jitter (16.67ms-17.4ms) so locked 60 FPS lobbies
            // never false-trigger PingSkip or starve global squad/macro directors.
            float threshold = players >= 6 ? 0.0208f : (players > 1 ? 0.0222f : 0.0265f);
            if (dt > threshold)
            {
                TotalPingGovernorSkippedCycles++;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Marks the current frame as having executed heavy spatial/octree/snapshot work
        /// (e.g., vanilla <c>Squad.SquadLeaderRoutine</c> or a full world snapshot rebuild)
        /// so secondary octree sweeps shift to subsequent frames.
        /// </summary>
        public static void NotifyHeavyWorkThisFrame()
        {
            _lastHeavyWorkFrame = Time.frameCount;
        }

        public static void NotifyDirectorWorkThisFrame()
        {
            int frame = Time.frameCount;
            _lastHeavyWorkFrame = frame;
            _lastDirectorWorkFrame = frame;
        }

        public static bool IsHeavyWorkFrame()
        {
            return _lastHeavyWorkFrame == Time.frameCount;
        }

        /// <summary>
        /// Ensures at most ONE global mod director pass (Macro Director, TeamNet GOAP Coordinator,
        /// Snapshot Rebuild, or Dictionary Pruning) executes on any single frame, without allowing
        /// frequent vanilla <c>Squad.SquadLeaderRoutine</c> coroutines (80+/s across 20 squads) to starve directors.
        /// </summary>
        public static bool TryClaimHeavyFrameWorkSlot(bool allowOnModerateFrame = false)
        {
            if (!allowOnModerateFrame && ShouldSkipBotCycleForNetworkPing())
                return false;

            int frame = Time.frameCount;
            if (_lastDirectorWorkFrame == frame)
                return false;

            _lastDirectorWorkFrame = frame;
            _lastHeavyWorkFrame = frame;
            return true;
        }

        public static bool CanBotMoveAutonomously(Soldier s)
        {
            if (s == null || s.Pointer == IntPtr.Zero)
                return false;

            AiParams p = s.Params;
            return p == null || p.canMove;
        }

        public static bool CanBotChangePoseAutonomously(Soldier s)
        {
            if (s == null || s.Pointer == IntPtr.Zero)
                return false;

            AiParams p = s.Params;
            return p == null || (p.changePoseAutomatically && !p.forcedPose.HasValue);
        }

        /// <summary>
        /// Universal game-mode objective classifier supporting Classic Invasion (<c>MissionTaskConquerArea</c>,
        /// <c>MissionTaskDestroyVehicles</c>, <c>MissionTaskClearArea</c>), <c>MissionTaskRetreat</c>,
        /// <c>MissionTaskPushFrontline</c>, Dynamic Conquest (<c>gamemode_conquest.lua</c>), Wave Defense
        /// (<c>gamemode_defend.lua</c>), Retreat Finales (<c>phase_4.lua</c>), and Multi-Phase Lua Operations.
        /// </summary>
        public static bool ClassifyMissionObjectiveForFaction(
            MissionObjective obj,
            string faction,
            BattleData bd,
            out bool isHeldByFaction,
            out bool canAttractFaction,
            out float contestOrProgress01)
        {
            isHeldByFaction = false;
            canAttractFaction = false;
            contestOrProgress01 = 0f;

            if (obj == null || obj.Pointer == IntPtr.Zero || string.IsNullOrEmpty(faction))
                return false;

            MissionTask task = obj.missionTask;
            if (task == null || task.Pointer == IntPtr.Zero || !task.IsTaskEnabled())
                return false;

            MissionObjectiveScripted sObj = obj.TryCast<MissionObjectiveScripted>();
            if (sObj != null)
            {
                if (!sObj.IsSettedUp() || sObj.IsSecured())
                    return false;

                // Dormant / staged / completed Lua objectives set both attractors false; ignore completely
                if (!sObj.canAttractAllies && !sObj.canAttractAxis)
                    return false;

                bool isInvader = bd != null && bd.IsInvaderFaction(faction);
                bool canAttract = sObj.CanAttractFaction(faction);
                MissionTaskScripted mTask = task.TryCast<MissionTaskScripted>();
                float rawPct = Mathf.Clamp01(task.GetTaskCompletationPercentage());

                // MissionTaskScripted stores icon/icon_defenders as (luaIcon + 1): 1 = Attack (#attack/#chase), 2 = Defend (#defend/#retreat)
                byte effIcon = mTask != null
                    ? ((!isInvader && mTask.icon_defenders > 0) ? mTask.icon_defenders : mTask.icon)
                    : (byte)0;

                // Retreat Finales (SHARED/phase_4.lua): Defenders get icon_defenders = 2 with "#retreat" text and canAttract = true.
                // Treat as an active evacuation rally objective (not a rear garrison) so all Defender squads withdraw into the zone!
                if (!isInvader && canAttract && mTask != null && mTask.customTextDefenders == "#retreat")
                {
                    canAttractFaction = true;
                    return true;
                }

                string sSec = sObj.securedFaction;
                bool ownedByFaction = (effIcon == 2) ||
                                      (!canAttract && !string.IsNullOrEmpty(sSec) && string.Equals(sSec, faction, StringComparison.OrdinalIgnoreCase));

                if (ownedByFaction)
                {
                    isHeldByFaction = true;
                    canAttractFaction = canAttract;
                    // Conquest & Invader-captured Wave Defense flags set canAttract = false for the owner and rest at 0.99f when uncontested,
                    // draining toward 0.0f when enemies capture! Defender-held Wave Defense / Lua Phase flags keep canAttract = true and rest at 0.0f.
                    if (!canAttract)
                        contestOrProgress01 = (rawPct > 0.01f && rawPct < 0.98f) ? Mathf.Clamp01(1.0f - rawPct) : 0f;
                    else
                        contestOrProgress01 = rawPct > 0.05f ? rawPct : 0f;
                    return true;
                }

                if (canAttract)
                {
                    canAttractFaction = true;
                    // On enemy-owned Conquest flags, rawPct starts at 0.99f under enemy ownership before neutralizing through 0.0f;
                    // only report positive friendly capture momentum once neutralized or under friendly securedFaction!
                    if (!string.IsNullOrEmpty(sSec) && !string.Equals(sSec, faction, StringComparison.OrdinalIgnoreCase))
                        contestOrProgress01 = rawPct < 0.65f ? (1.0f - rawPct) * 0.45f : 0f;
                    else if (mTask != null && mTask.icon == 2 && !isInvader && rawPct >= 0.95f)
                        contestOrProgress01 = 0f; // Lost Wave Defense flag resting at 0.99f before counter-capture begins
                    else
                        contestOrProgress01 = rawPct;
                    return true;
                }

                return false;
            }

            float capPct = Mathf.Clamp01(task.GetTaskCompletationPercentage());
            if (task.TryCast<MissionTaskPushFrontline>() != null)
            {
                string secFaction = obj.GetSecuredFaction();
                if (!string.IsNullOrEmpty(secFaction) && secFaction == faction)
                {
                    isHeldByFaction = true;
                    contestOrProgress01 = capPct > 0.05f ? capPct : 0f;
                    return true;
                }
                if (obj.CanAttractFaction(faction))
                {
                    canAttractFaction = true;
                    contestOrProgress01 = capPct;
                    return true;
                }
                return false;
            }

            if (obj.IsSecured())
                return false;

            if (task.TryCast<MissionTaskRetreat>() != null)
            {
                if (obj.CanAttractFaction(faction))
                {
                    canAttractFaction = true;
                    return true;
                }
                return false;
            }

            string invFaction = obj.GetSecuredFaction();
            if (!string.IsNullOrEmpty(invFaction) && invFaction != faction)
            {
                isHeldByFaction = true;
                canAttractFaction = obj.CanAttractFaction(faction);
                contestOrProgress01 = capPct > 0.05f ? capPct : 0f;
                return true;
            }

            if (obj.CanAttractFaction(faction))
            {
                canAttractFaction = true;
                contestOrProgress01 = capPct;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Evaluates a squad's active <see cref="SquadTask"/> across all vanilla and scripted/dynamic game modes
        /// so GOAP never misinterprets resting <c>0.99f</c> Conquest/WaveDefense bars as enemy back-caps.
        /// </summary>
        public static void EvaluateSquadTaskForFaction(
            SquadTask curTask,
            string faction,
            BattleData bd,
            bool isGarrisonAssigned,
            out bool isDefending,
            out float progressOrContest01)
        {
            isDefending = isGarrisonAssigned;
            progressOrContest01 = 0f;
            if (curTask == null || curTask.Pointer == IntPtr.Zero || string.IsNullOrEmpty(faction))
                return;

            var scriptedList = MissionObjectiveScripted.scriptedMissionObjectives;
            if (scriptedList != null)
            {
                IntPtr taskPtr = curTask.Pointer;
                int sCount = scriptedList.Count;
                for (int i = 0; i < sCount; i++)
                {
                    MissionObjectiveScripted sObj = scriptedList[i];
                    if (sObj != null && sObj.missionTask != null && sObj.missionTask.Pointer == taskPtr)
                    {
                        if (ClassifyMissionObjectiveForFaction(sObj, faction, bd, out bool held, out _, out float prog))
                        {
                            isDefending = isGarrisonAssigned || held;
                            progressOrContest01 = prog;
                        }
                        return;
                    }
                }
            }

            float rawPct = Mathf.Clamp01(curTask.GetTaskCompletationPercentage());
            if (curTask.TryCast<MissionTaskPushFrontline>() != null)
            {
                isDefending = isGarrisonAssigned;
                progressOrContest01 = rawPct;
                return;
            }

            if (curTask.TryCast<MissionTaskRetreat>() != null)
            {
                isDefending = false;
                progressOrContest01 = 1.0f;
                return;
            }

            bool isInvader = bd != null && bd.IsInvaderFaction(faction);
            isDefending = isGarrisonAssigned || !isInvader;
            progressOrContest01 = rawPct;
        }

        /// <summary>
        /// Validates that an IL2CPP <see cref="Spottable"/> interface wrapper still points to an
        /// alive, non-destroyed Unity <see cref="Component"/> with a valid native transform.
        /// Prevents native NullReferenceExceptions in <c>Soldier.GetCenterOfUnit</c> / <c>TurretAI.CanSee</c>.
        /// </summary>
        public static bool IsValidAliveSpottable(Spottable s)
        {
            return TryResolveSpottableFast(s, out _, out _);
        }

        public static bool TryGetSpottableCenter(Spottable s, out Vector3 center)
        {
            if (!TryResolveSpottableFast(s, out _, out _))
            {
                center = Vector3.zero;
                return false;
            }

            try
            {
                center = s.GetCenterOfUnit();
                return true;
            }
            catch
            {
                center = Vector3.zero;
                return false;
            }
        }

        public static bool IsMasterClient()
        {
            EnsureMpRoomStateCached();
            return _mpIsMasterCached;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsLocallyOwnedBot(SoldierFastHandles h)
        {
            if (h == null || h.Ref == null || !h.Ref.IsAlive || h.Ref.IsPlayer())
                return false;

            if (!IsInMultiplayerRoom())
                return true;

            if (h.Syncher == null)
                h.Syncher = h.Ref.soldierSyncher;

            return h.Syncher == null || (h.Syncher.OwnSyncher && !h.Syncher.controlled_by_player);
        }

        public static bool IsLocallyOwnedBot(Soldier s)
        {
            if (ReferenceEquals(s, null) || s.Pointer == IntPtr.Zero)
                return false;
            return IsLocallyOwnedBot(GetOrCreateSoldierHandles(s));
        }

        public static bool IsLocallyOwnedSoldier(Soldier s)
        {
            if (ReferenceEquals(s, null) || s.Pointer == IntPtr.Zero)
                return false;
            SoldierFastHandles h = GetOrCreateSoldierHandles(s);
            if (h == null || !h.Ref.IsAlive)
                return false;

            if (!IsInMultiplayerRoom())
                return true;

            if (h.Syncher == null)
                h.Syncher = h.Ref.soldierSyncher;

            return h.Syncher == null || h.Syncher.OwnSyncher;
        }

        public static bool IsLocallyOwnedVehicleSyncher(Vehicle v)
        {
            if (ReferenceEquals(v, null) || v.Pointer == IntPtr.Zero)
                return false;

            if (!IsInMultiplayerRoom())
                return true;

            VehicleFastHandles vh = GetOrCreateVehicleHandles(v);
            if (vh == null)
                return false;
            if (vh.Syncher == null)
                vh.Syncher = v.vehicleSyncher;
            return vh.Syncher == null || vh.Syncher.OwnSyncher;
        }

        public static bool IsLocallyOwnedVehicle(Vehicle v)
        {
            if (ReferenceEquals(v, null) || v.Pointer == IntPtr.Zero)
                return false;

            if (!IsInMultiplayerRoom())
                return v != null;

            VehicleFastHandles vh = GetOrCreateVehicleHandles(v);
            if (vh == null)
                return false;

            if (v.HasDriverAlive)
                return v.OwnDriverOrVehicle();

            if (vh.Syncher == null)
                vh.Syncher = v.vehicleSyncher;
            return vh.Syncher == null || vh.Syncher.OwnSyncher;
        }

        /// <summary>
        /// Safely returns the active <see cref="BattlePhase"/> without throwing when
        /// <c>BattleManager.phases</c> is null/empty (e.g., <c>sp_ME_...</c> Mission Editor V2 / reworked streaming battles).
        /// </summary>
        public static BattlePhase TryGetCurrentBattlePhase(BattleManager bm)
        {
            if (bm == null || bm.Pointer == IntPtr.Zero)
                return null;

            var phases = bm.phases;
            if (phases == null)
                return null;

            int idx = bm.currentPhaseNum;
            if ((uint)idx >= (uint)phases.Length)
                return null;

            return phases[idx];
        }

        /// <summary>
        /// Resolves the horizontal direction toward enemy spawn territory on both classic <see cref="BattlePhase"/>
        /// maps and <c>sp_ME_...</c> Mission Editor V2 streaming maps (falling back to <c>SpawnManager.activeSpawns</c>).
        /// </summary>
        public static bool TryGetEnemySpawnDirection(string myFaction, Vector3 fromPos, out Vector3 dir)
        {
            dir = Vector3.zero;
            if (string.IsNullOrEmpty(myFaction))
                return false;

            string canonFaction = InternFaction(myFaction);
            float now = Time.time;

            try
            {
                BattlePhase phase = TryGetCurrentBattlePhase(BattleManager.instance);
                IntPtr phasePtr = phase != null ? phase.Pointer : IntPtr.Zero;
                var cacheKey = (phasePtr, canonFaction);

                if (!_enemySpawnDirCache.TryGetValue(cacheKey, out EnemySpawnDirCacheEntry entry) || now >= entry.ExpireTime)
                {
                    entry = new EnemySpawnDirCacheEntry { ExpireTime = now + 5.0f };

                    if (phase != null && phase.EnemyFactionHasSpawn(canonFaction))
                    {
                        entry.HasCentroid = true;
                        entry.Centroid = phase.GetOppositeFactionTerritoryPosition(canonFaction);
                    }
                    else
                    {
                        var spawns = SpawnManager.activeSpawns;
                        if (spawns != null && spawns.Count > 0)
                        {
                            Vector3 sum = Vector3.zero;
                            int count = 0;
                            for (int i = 0; i < spawns.Count; i++)
                            {
                                SpawnManager sp = spawns[i];
                                if (sp == null || sp.Pointer == IntPtr.Zero || sp.gameObject == null || !sp.gameObject.activeInHierarchy)
                                    continue;

                                string spFact = sp.GetFaction();
                                if (string.IsNullOrEmpty(spFact) || spFact == canonFaction || spFact == "Civilian" || AreSameSideFactions(canonFaction, spFact))
                                    continue;

                                sum += sp.GetSpawnPosition();
                                count++;
                            }

                            if (count > 0)
                            {
                                entry.HasCentroid = true;
                                entry.Centroid = sum / count;
                            }
                        }
                    }

                    if (_enemySpawnDirCache.Count > 32)
                        _enemySpawnDirCache.Clear();
                    _enemySpawnDirCache[cacheKey] = entry;
                }

                if (entry.HasCentroid)
                {
                    Vector3 toEnemy = entry.Centroid - fromPos;
                    toEnemy.y = 0f;
                    if (toEnemy.sqrMagnitude > 1.0f)
                    {
                        dir = toEnemy.normalized;
                        return true;
                    }
                }
            }
            catch { }

            return false;
        }

        private static bool TryNormalizeFootSpawnPoint(Vector3 rawPos, out Vector3 groundedPos)
        {
            groundedPos = rawPos;
            float groundY = ResourcesManager.GetTerrainAltitude(rawPos, 0f);
            // Skip aircraft sky-spawns (> 14m above terrain) and deep-water boat/landing-craft spawns (< -0.35m)
            if (rawPos.y > groundY + 14.0f || groundY < -0.35f)
                return false;

            // Snap Y to terrain altitude so 3D distance checks (e.g. MissionTaskRetreat.IsInsideTaskArea) trigger on foot
            groundedPos = new Vector3(rawPos.x, Mathf.Max(groundY, 0.05f), rawPos.z);
            return true;
        }

        private static bool TrySampleDryRetreatPoint(Vector3 originPos, Vector3 horizDir, float dist, out Vector3 dryPos)
        {
            dryPos = Vector3.zero;
            horizDir.y = 0f;
            if (horizDir.sqrMagnitude < 0.001f)
                return false;
            horizDir.Normalize();

            for (int attempt = 0; attempt < 3; attempt++)
            {
                Vector3 dir = attempt == 0
                    ? horizDir
                    : Quaternion.Euler(0f, attempt == 1 ? 28f : -28f, 0f) * horizDir;
                Vector3 cand = originPos + dir * dist;
                float alt = ResourcesManager.GetTerrainAltitude(cand, 0f);
                if (alt >= 0.15f)
                {
                    dryPos = new Vector3(cand.x, alt, cand.z);
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Resolves the nearest dry, foot-reachable friendly spawn position on both classic <see cref="BattlePhase"/> maps
        /// and <c>sp_ME_...</c> Mission Editor V2 streaming maps (via <c>SpawnManager.activeSpawns</c>).
        /// Filters out sky aircraft spawns and deep-water boat spawns, and snaps Y to terrain altitude.
        /// </summary>
        public static bool TryGetNearestFriendlySpawnPosition(string myFaction, Vector3 fromPos, out Vector3 spawnPos)
        {
            spawnPos = Vector3.zero;
            if (string.IsNullOrEmpty(myFaction))
                return false;

            try
            {
                BattlePhase phase = TryGetCurrentBattlePhase(BattleManager.instance);
                if (phase != null)
                {
                    SpawnManager nearest = phase.GetNearestSpawn(myFaction, fromPos);
                    if (nearest != null &&
                        nearest.Pointer != IntPtr.Zero &&
                        TryNormalizeFootSpawnPoint(nearest.GetSpawnPosition(), out spawnPos))
                    {
                        return true;
                    }
                }

                var spawns = SpawnManager.activeSpawns;
                if (spawns != null && spawns.Count > 0)
                {
                    float bestExactSq = float.MaxValue;
                    Vector3 bestExactPos = Vector3.zero;
                    bool foundExact = false;

                    float bestAlliedSq = float.MaxValue;
                    Vector3 bestAlliedPos = Vector3.zero;
                    bool foundAllied = false;

                    for (int i = 0; i < spawns.Count; i++)
                    {
                        SpawnManager sp = spawns[i];
                        if (sp == null || sp.Pointer == IntPtr.Zero || sp.gameObject == null || !sp.gameObject.activeInHierarchy)
                            continue;

                        string spFaction = sp.GetFaction();
                        if (string.IsNullOrEmpty(spFaction) || spFaction == "Civilian")
                            continue;

                        bool exactMatch = spFaction == myFaction;
                        if (!exactMatch && !AreSameSideFactions(myFaction, spFaction))
                            continue;

                        if (!TryNormalizeFootSpawnPoint(sp.GetSpawnPosition(), out Vector3 cand))
                            continue;

                        float dSq = (cand - fromPos).sqrMagnitude;
                        if (exactMatch)
                        {
                            if (dSq < bestExactSq)
                            {
                                bestExactSq = dSq;
                                bestExactPos = cand;
                                foundExact = true;
                            }
                        }
                        else if (dSq < bestAlliedSq)
                        {
                            bestAlliedSq = dSq;
                            bestAlliedPos = cand;
                            foundAllied = true;
                        }
                    }

                    if (foundExact)
                    {
                        spawnPos = bestExactPos;
                        return true;
                    }
                    if (foundAllied)
                    {
                        spawnPos = bestAlliedPos;
                        return true;
                    }
                }
            }
            catch { }

            return false;
        }

        /// <summary>
        /// Computes a dry-land rear evacuation point for bailed-out vehicle crews across classic <see cref="BattlePhase"/> maps,
        /// <c>sp_ME_...</c> Mission Editor V2 maps (where <c>BattleManager.instance.currentPhase == null</c>), and pure Lua maps.
        /// Never fails on intermediate rivers/ponds or missing <see cref="BattlePhase"/> references.
        /// </summary>
        public static bool TryComputeCrewRetreatDestination(string myFaction, Vector3 originPos, out Vector3 retreatPos)
        {
            retreatPos = Vector3.zero;
            if (string.IsNullOrEmpty(myFaction))
                return false;

            try
            {
                if (TryGetNearestFriendlySpawnPosition(myFaction, originPos, out Vector3 spawnPos))
                {
                    Vector3 toSpawn = spawnPos - originPos;
                    toSpawn.y = 0f;
                    float dist = toSpawn.magnitude;

                    if (dist >= 18.0f && dist <= 110.0f)
                    {
                        retreatPos = spawnPos;
                        return true;
                    }

                    if (dist > 110.0f)
                    {
                        Vector3 dir = toSpawn / dist;
                        if (TrySampleDryRetreatPoint(originPos, dir, 95.0f, out retreatPos) ||
                            TrySampleDryRetreatPoint(originPos, dir, 125.0f, out retreatPos) ||
                            TrySampleDryRetreatPoint(originPos, dir, 68.0f, out retreatPos))
                        {
                            return true;
                        }

                        // Intermediate chord crossed a river/pond — use the verified dry friendly spawn directly!
                        retreatPos = spawnPos;
                        return true;
                    }

                    // Vehicle was knocked out right near friendly spawn (< 18m): step further rearward away from enemy
                    if (TryGetEnemySpawnDirection(myFaction, originPos, out Vector3 enemyDir) &&
                        TrySampleDryRetreatPoint(originPos, -enemyDir, 28.0f, out retreatPos))
                    {
                        return true;
                    }

                    if (dist >= 6.0f)
                    {
                        retreatPos = spawnPos;
                        return true;
                    }
                }

                // Pure Lua / SpawnAISquadGlobal fallback when no SpawnManager exists in scene:
                EnsureWorldSnapshot();
                int sqCount = WorldSquadCount;
                var squads = WorldSquads;

                float bestFriendlySq = 22.0f * 22.0f;
                Vector3 bestFriendlyPos = Vector3.zero;
                bool hasFriendly = false;

                float nearestEnemySq = float.MaxValue;
                Vector3 nearestEnemyPos = Vector3.zero;
                bool hasEnemy = false;

                for (int i = 0; i < sqCount; i++)
                {
                    ref SquadSnapshot snap = ref squads[i];
                    if (snap.UnitCount == 0 || snap.RefOnPlane || string.IsNullOrEmpty(snap.Faction))
                        continue;

                    Vector3 delta = snap.RefPos - originPos;
                    delta.y = 0f;
                    float dSq = delta.sqrMagnitude;
                    if (AreSameSideFactions(myFaction, snap.Faction))
                    {
                        if (!snap.IsVehicleCrew && dSq > bestFriendlySq)
                        {
                            float alt = ResourcesManager.GetTerrainAltitude(snap.RefPos, 0f);
                            if (alt >= 0.15f)
                            {
                                bestFriendlySq = dSq;
                                bestFriendlyPos = new Vector3(snap.RefPos.x, alt, snap.RefPos.z);
                                hasFriendly = true;
                            }
                        }
                    }
                    else if (AreEnemyFactions(myFaction, snap.Faction) && dSq < nearestEnemySq)
                    {
                        nearestEnemySq = dSq;
                        nearestEnemyPos = snap.RefPos;
                        hasEnemy = true;
                    }
                }

                if (hasEnemy)
                {
                    Vector3 away = originPos - nearestEnemyPos;
                    if (TrySampleDryRetreatPoint(originPos, away, 85.0f, out retreatPos) ||
                        TrySampleDryRetreatPoint(originPos, away, 55.0f, out retreatPos))
                    {
                        return true;
                    }
                }

                if (hasFriendly)
                {
                    retreatPos = bestFriendlyPos;
                    return true;
                }
            }
            catch { }

            return false;
        }

        /// <summary>
        /// Checks whether the human player controlling <paramref name="s"/> has published the
        /// <c>"bh_ver"</c> Photon custom property (cached for 5s per ActorNumber).
        /// Returns true for local players/bots or on error so Host-assist only activates for confirmed unmodded remote players.
        /// </summary>
        public static bool IsPlayerRunningBattleHardened(Soldier s)
        {
            if (s == null || s.Pointer == IntPtr.Zero)
                return true;

            try
            {
                SyncSoldier syncher = s.soldierSyncher;
                if (syncher == null || syncher.OwnSyncher)
                    return true;

                Photon.Realtime.Player controller = syncher.Controller ?? syncher.Owner;
                if (controller == null)
                    return true;

                int actorNum = controller.ActorNumber;
                float now = Time.unscaledTime;
                if (_playerModdedCache.TryGetValue(actorNum, out var cached) && now < cached.ExpireTime)
                    return cached.HasMod;

                bool hasMod = false;
                var props = controller.CustomProperties;
                if (props != null && props.ContainsKey((Il2CppSystem.String)"bh_ver"))
                    hasMod = true;

                if (_playerModdedCache.Count > 32)
                    _playerModdedCache.Clear();
                _playerModdedCache[actorNum] = (hasMod, now + 5.0f);
                return hasMod;
            }
            catch
            {
                return true;
            }
        }

        public static bool TryConsumeBotRpcBudget(float minInterval = 0.35f)
        {
            if (ShouldSkipBotCycleForNetworkPing())
                return false;

            float now = Time.time;
            if (_nextAllowedBotRpcTime > now + 2.5f)
            {
                _nextAllowedBotRpcTime = 0f;
            }

            if (now < _nextAllowedBotRpcTime)
                return false;

            int players = GetConnectedPlayerCount();
            if (players > 1)
            {
                minInterval *= (1.0f + Mathf.Min(players - 1, 12) * 0.25f);
            }

            _nextAllowedBotRpcTime = now + Mathf.Clamp(minInterval, 0.05f, 3.0f);
            return true;
        }

        public static Vehicle GetCachedTurretVehicle(Turret turret)
        {
            if (turret == null || turret.Pointer == IntPtr.Zero)
                return null;

            IntPtr ptr = turret.Pointer;
            if (_turretVehicleCache.TryGetValue(ptr, out Vehicle cachedVeh))
            {
                if (cachedVeh != null && cachedVeh.Pointer != IntPtr.Zero)
                    return cachedVeh;
            }

            Vehicle resolved = turret.GetComponentInParent<Vehicle>();
            _turretVehicleCache[ptr] = resolved;
            return resolved;
        }

        public static TurretFastTraits GetCachedTurretTraits(Turret turret)
        {
            if (ReferenceEquals(turret, null) || turret == null || turret.Pointer == IntPtr.Zero)
                return default;

            IntPtr ptr = turret.Pointer;
            if (_turretTraitsCache.TryGetValue(ptr, out TurretFastTraits traits))
            {
                if (traits.RootVehicle == null || traits.RootVehicle.Pointer != IntPtr.Zero)
                    return traits;
            }

            Vehicle rootVeh = GetCachedTurretVehicle(turret);
            VehicleFastHandles vh = rootVeh != null ? GetOrCreateVehicleHandles(rootVeh) : null;
            Seats seat = null;
            try { seat = turret.GetSeat(); } catch { }

            traits = new TurretFastTraits
            {
                RootVehicle = rootVeh,
                VehHandles = vh,
                IsCommanderTurret = turret.TryCast<TurretCommander>() != null,
                IsExposedCrewSeat = seat != null && seat.showUnitSet,
                IsAA = turret.isAA,
                IsIndirectOrArty = turret.IsUpIndirectFire() || turret.IsArtillery(),
                AimFov = turret.GetAimingFOV()
            };

            _turretTraitsCache[ptr] = traits;
            return traits;
        }

        /// <summary>
        /// Continuous human distance "squint" FOV curve: wide peripheral/ear awareness up close,
        /// tapering smoothly with distance so observers neither freeze blind at 21m nor keep 160-degree vision at 75m.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float EvaluateHumanSquintHalfFov(float dist, float closeOmniRange, float midMaxDist, float midHalfFov, float longHalfFov)
        {
            float omniRadius = Mathf.Clamp(closeOmniRange, 6.0f, 14.0f);
            if (dist <= omniRadius)
                return 180.0f;

            float nearBandEnd = Mathf.Max(omniRadius + 14.0f, 34.0f);
            if (dist <= nearBandEnd)
            {
                float tNear = (dist - omniRadius) / (nearBandEnd - omniRadius);
                return Mathf.Lerp(136.0f, Mathf.Max(midHalfFov, 90.0f), tNear);
            }

            float midEnd = Mathf.Max(nearBandEnd + 25.0f, midMaxDist);
            if (dist <= midEnd)
            {
                float tMid = (dist - nearBandEnd) / (midEnd - nearBandEnd);
                return Mathf.Lerp(Mathf.Max(midHalfFov, 90.0f), Mathf.Min(midHalfFov, 64.0f), tMid);
            }

            float farEnd = Mathf.Max(midEnd + 95.0f, 210.0f);
            float tFar = Mathf.Clamp01((dist - midEnd) / (farEnd - midEnd));
            return Mathf.Lerp(Mathf.Min(midHalfFov, 64.0f), Mathf.Clamp(longHalfFov, 26.0f, 52.0f), tFar);
        }

        /// <summary>
        /// Evaluates (throttled to 0.55s per vehicle) whether an enclosed vehicle/tank currently has an alive
        /// Commander cupola/periscope observer, an unbuttoned/open-top crewman, or friendly infantry within 18.5m
        /// capable of calling out flank targets over the intercom / tank-infantry phone.
        /// </summary>
        public static void GetCachedVehicleSpotterStatus(
            VehicleFastHandles vh,
            string myFaction,
            float now,
            out bool hasAliveCommanderOrExposedSpotter,
            out bool hasCloseFriendlyEscort)
        {
            hasAliveCommanderOrExposedSpotter = false;
            hasCloseFriendlyEscort = false;
            if (vh == null || vh.Ref == null || vh.Tr == null)
                return;

            if (now < vh.NextSpotterCheckTime)
            {
                hasAliveCommanderOrExposedSpotter = vh.CachedHasAliveCommanderOrExposedSpotter;
                hasCloseFriendlyEscort = vh.CachedHasCloseFriendlyEscort;
                return;
            }

            vh.NextSpotterCheckTime = now + 0.55f;
            Vehicle veh = vh.Ref;

            try
            {
                var seats = veh.seats;
                if (seats != null)
                {
                    int sLen = seats.Length;
                    if (!vh.CommanderSeatChecked)
                    {
                        vh.CommanderSeatChecked = true;
                        vh.CommanderSeatIndex = -1;
                        for (int i = 0; i < sLen; i++)
                        {
                            Seats s = seats[i];
                            if (s != null && s.connectedTurret != null && s.connectedTurret.TryCast<TurretCommander>() != null)
                            {
                                vh.CommanderSeatIndex = i;
                                vh.HasDedicatedCommanderTurret = true;
                                break;
                            }
                        }
                    }

                    if (vh.CommanderSeatIndex >= 0 && vh.CommanderSeatIndex < sLen)
                    {
                        Seats cmdSeat = seats[vh.CommanderSeatIndex];
                        if (cmdSeat != null && cmdSeat.unitSet != null && cmdSeat.unitSet.IsAlive)
                            hasAliveCommanderOrExposedSpotter = true;
                    }

                    if (!hasAliveCommanderOrExposedSpotter)
                    {
                        for (int i = 0; i < sLen; i++)
                        {
                            Seats s = seats[i];
                            if (s == null || s.unitSet == null || !s.unitSet.IsAlive)
                                continue;

                            // Exposed open-top / pintle gunner / unbuttoned passenger or dedicated periscope observer seat (non-driver, non-main-gunner)
                            if (s.showUnitSet || (!s.isDriver && !s.hasTurret && s.HasPeriscope()))
                            {
                                hasAliveCommanderOrExposedSpotter = true;
                                break;
                            }
                        }
                    }
                }
            }
            catch { }

            if (!string.IsNullOrEmpty(myFaction))
            {
                EnsureWorldSnapshot();
                Vector3 vPos = vh.Tr.position;
                var squads = WorldSquads;
                int sqCount = WorldSquadCount;
                for (int i = 0; i < sqCount; i++)
                {
                    ref SquadSnapshot sqSnap = ref squads[i];
                    if (sqSnap.UnitCount == 0 || sqSnap.RefOnPlane || sqSnap.LeaderOnVehicle || string.IsNullOrEmpty(sqSnap.Faction))
                        continue;
                    if (!AreSameSideFactions(myFaction, sqSnap.Faction))
                        continue;

                    if ((sqSnap.RefPos - vPos).sqrMagnitude <= 18.5f * 18.5f)
                    {
                        hasCloseFriendlyEscort = true;
                        break;
                    }
                }
            }

            vh.CachedHasAliveCommanderOrExposedSpotter = hasAliveCommanderOrExposedSpotter;
            vh.CachedHasCloseFriendlyEscort = hasCloseFriendlyEscort;
        }

        public static int GetSolidEnvironmentMask()
        {
            if (_cachedSolidEnvMask != 0)
                return _cachedSolidEnvMask;

            try
            {
                int gm = ResourcesManager.groundMask.value;
                if (gm != 0)
                {
                    _cachedSolidEnvMask = gm;
                    return gm;
                }
            }
            catch { }

            int terrainLayer = LayerMask.NameToLayer("Terrain");
            _cachedSolidEnvMask = terrainLayer >= 0 ? ((1 << 0) | (1 << terrainLayer)) : (1 << 0);
            return _cachedSolidEnvMask;
        }

        public static int GetSmokeOcclusionMask()
        {
            if (_cachedSmokeOcclusionMask != 0)
                return _cachedSmokeOcclusionMask;

            try
            {
                int vm = ResourcesManager.visionMask.value;
                int bm = ResourcesManager.bulletMask.value;
                int diff = vm & ~bm;
                if (diff != 0)
                {
                    _cachedSmokeOcclusionMask = diff;
                    return diff;
                }
            }
            catch { }

            int tfxLayer = LayerMask.NameToLayer("TransparentFX");
            _cachedSmokeOcclusionMask = tfxLayer >= 0 ? (1 << tfxLayer) : (1 << 1);
            return _cachedSmokeOcclusionMask;
        }

        public static bool TryGetActiveCameraPosition(out Vector3 camPos)
        {
            try
            {
                Camera cam = ResourcesManager.mainCamera;
                if (cam == null || cam.Pointer == IntPtr.Zero || !cam.isActiveAndEnabled)
                    cam = Camera.main;

                if (cam != null && cam.Pointer != IntPtr.Zero && cam.transform != null)
                {
                    camPos = cam.transform.position;
                    return true;
                }
            }
            catch { }

            camPos = Vector3.zero;
            return false;
        }

        /// <summary>
        /// Checks whether an impact point is submerged below or across an active <see cref="FollowingWater"/>
        /// or Layer 4 (<c>Water</c>) surface, or occluded from <paramref name="camPos"/> by a tactical smoke volume
        /// (<c>visionMask &amp; ~bulletMask</c>).
        /// </summary>
        public static bool IsPointOccludedByWaterOrSmoke(Vector3 camPos, Vector3 impactPos)
        {
            try
            {
                FollowingWater water = FollowingWater.instance;
                if (water != null && water.Pointer != IntPtr.Zero && water.isActiveAndEnabled)
                {
                    if (water.Pointer != _cachedWaterInstancePtr)
                    {
                        _cachedWaterInstancePtr = water.Pointer;
                        _cachedWaterRenderer = FollowingWater.GetRenderer();
                    }

                    if (_cachedWaterRenderer != null &&
                        _cachedWaterRenderer.Pointer != IntPtr.Zero &&
                        _cachedWaterRenderer.enabled)
                    {
                        float waterY = FollowingWater.WaterLevel;
                        // Submerged flesh hit or sightline crossing the rendered water plane
                        if (impactPos.y < waterY - 0.03f || (camPos.y - waterY) * (impactPos.y - waterY) < 0f)
                            return true;
                    }
                }
            }
            catch { }

            int smokeMask = GetSmokeOcclusionMask();
            int lineMask = smokeMask | (1 << 4); // Smoke volumes (TransparentFX) + local Water colliders (Layer 4)
            float distSq = (impactPos - camPos).sqrMagnitude;
            if (distSq > 0.25f && Physics.Linecast(camPos, impactPos, lineMask))
                return true;

            // PhysX Linecast starting inside a SphereCollider skips its own shell; CheckSphere catches inside-smoke hits
            if (distSq > 4.0f && Physics.CheckSphere(impactPos, 0.20f, smokeMask))
                return true;

            if (distSq > 6.25f && Physics.CheckSphere(camPos, 0.15f, smokeMask))
                return true;

            return false;
        }

        public static bool RaycastEnvironment(Vector3 origin, Vector3 dir, float maxDist, Transform ignoreRoot, out RaycastHit envHit)
        {
            // ResourcesManager.groundMask (Default | Terrain) hits only solid environment geometry (walls, buildings,
            // sandbags, terrain) and ignores soldier BodyPart colliders, ragdolls, items, foliage, and triggers.
            // This eliminates multi-hop raycast loops and IL2CPP GetComponentInParent<Creature>() hierarchy walks!
            return Physics.Raycast(origin, dir, out envHit, maxDist, GetSolidEnvironmentMask(), QueryTriggerInteraction.Ignore);
        }

        public static bool IsDirectionBlockedByEnvironment(Vector3 origin, Vector3 dir, float dist, Transform ignoreRoot)
        {
            return Physics.Raycast(origin, dir, dist, GetSolidEnvironmentMask(), QueryTriggerInteraction.Ignore);
        }

        public static bool HasLowCeilingOverhead(Vector3 feetPos, float maxHeight = 2.6f)
        {
            // Quantize to 2m x 1m x 2m spatial grid cell so indoor ceiling checks are O(1) after first probe
            int gx = Mathf.RoundToInt(feetPos.x * 0.5f) & 0xFFFF;
            int gy = Mathf.RoundToInt(feetPos.y) & 0xFFFF;
            int gz = Mathf.RoundToInt(feetPos.z * 0.5f) & 0xFFFF;
            long cellKey = ((long)gx << 32) | ((long)gy << 16) | (uint)gz;

            if (_ceilingCache.TryGetValue(cellKey, out bool cachedLowCeiling))
                return cachedLowCeiling;

            Vector3 origin = feetPos + Vector3.up * 0.65f;
            bool hasCeiling = Physics.Raycast(origin, Vector3.up, maxHeight, GetSolidEnvironmentMask(), QueryTriggerInteraction.Ignore);
            if (_ceilingCache.Count > 2048)
                _ceilingCache.Clear();
            _ceilingCache[cellKey] = hasCeiling;
            return hasCeiling;
        }

        public static bool IsOnBridgeOrNarrowCrossing(Vector3 feetPos)
        {
            // Quantize to 2m x 1m x 2m spatial grid cell so bridge/crossing checks are O(1) after first probe
            int gx = Mathf.RoundToInt(feetPos.x * 0.5f) & 0xFFFF;
            int gy = Mathf.RoundToInt(feetPos.y) & 0xFFFF;
            int gz = Mathf.RoundToInt(feetPos.z * 0.5f) & 0xFFFF;
            long cellKey = ((long)gx << 32) | ((long)gy << 16) | (uint)gz;

            if (_bridgeCellCache.TryGetValue(cellKey, out bool cachedBridge))
                return cachedBridge;

            bool isBridge = false;
            if (!HasLowCeilingOverhead(feetPos, 3.8f))
            {
                int mask = GetSolidEnvironmentMask();
                Vector3 baseOrigin = feetPos + Vector3.up * 0.85f;
                // A 2.65m downward probe from +0.85m means any ground > 1.80m below feetPos.y is a drop-off
                const float maxDropProbe = 2.65f;

                bool dropPosX = !Physics.Raycast(baseOrigin + new Vector3(3.8f, 0f, 0f), Vector3.down, maxDropProbe, mask, QueryTriggerInteraction.Ignore) ||
                                !Physics.Raycast(baseOrigin + new Vector3(6.2f, 0f, 0f), Vector3.down, maxDropProbe, mask, QueryTriggerInteraction.Ignore);
                bool dropNegX = false;
                if (dropPosX)
                {
                    dropNegX = !Physics.Raycast(baseOrigin + new Vector3(-3.8f, 0f, 0f), Vector3.down, maxDropProbe, mask, QueryTriggerInteraction.Ignore) ||
                               !Physics.Raycast(baseOrigin + new Vector3(-6.2f, 0f, 0f), Vector3.down, maxDropProbe, mask, QueryTriggerInteraction.Ignore);
                }

                if (dropPosX && dropNegX)
                {
                    isBridge = true;
                }
                else
                {
                    bool dropPosZ = !Physics.Raycast(baseOrigin + new Vector3(0f, 0f, 3.8f), Vector3.down, maxDropProbe, mask, QueryTriggerInteraction.Ignore) ||
                                    !Physics.Raycast(baseOrigin + new Vector3(0f, 0f, 6.2f), Vector3.down, maxDropProbe, mask, QueryTriggerInteraction.Ignore);
                    if (dropPosZ)
                    {
                        bool dropNegZ = !Physics.Raycast(baseOrigin + new Vector3(0f, 0f, -3.8f), Vector3.down, maxDropProbe, mask, QueryTriggerInteraction.Ignore) ||
                                        !Physics.Raycast(baseOrigin + new Vector3(0f, 0f, -6.2f), Vector3.down, maxDropProbe, mask, QueryTriggerInteraction.Ignore);
                        if (dropNegZ)
                            isBridge = true;
                    }
                }
            }

            if (_bridgeCellCache.Count > 2048)
                _bridgeCellCache.Clear();
            _bridgeCellCache[cellKey] = isBridge;
            return isBridge;
        }

        /// <summary>
        /// Verifies that a candidate cover/bounding position for a soldier currently on a bridge or narrow crossing
        /// lands squarely on walkable deck geometry at roughly the same elevation (not off an edge, into water, or inside a pillar).
        /// </summary>
        public static bool IsSafeBridgeDeckStep(Vector3 currentFeetPos, Vector3 candidatePos, out Vector3 snappedGroundPos)
        {
            snappedGroundPos = candidatePos;
            int mask = GetSolidEnvironmentMask();
            Vector3 probeOrigin = new Vector3(candidatePos.x, currentFeetPos.y + 1.35f, candidatePos.z);
            if (!Physics.Raycast(probeOrigin, Vector3.down, out RaycastHit hit, 2.50f, mask, QueryTriggerInteraction.Ignore))
                return false;

            if (hit.normal.y < 0.65f || Mathf.Abs(hit.point.y - currentFeetPos.y) > 1.15f)
                return false;

            if (hit.collider == null || hit.collider.gameObject.layer == 4)
                return false;

            try
            {
                FollowingWater water = FollowingWater.instance;
                if (water != null && water.Pointer != IntPtr.Zero && water.isActiveAndEnabled &&
                    hit.point.y <= FollowingWater.WaterLevel + 0.15f)
                {
                    return false;
                }
            }
            catch { }

            if (Physics.CheckSphere(hit.point + Vector3.up * 0.65f, 0.26f, mask, QueryTriggerInteraction.Ignore))
                return false;

            snappedGroundPos = hit.point;
            return true;
        }

        public static float GetBarrelHeightForPose(SoldierPose pose)
        {
            switch (pose)
            {
                case SoldierPose.Prone:
                    return 0.25f;
                case SoldierPose.Crouch:
                    return 0.68f;
                default:
                    return 1.24f;
            }
        }

        /// <summary>
        /// Extends a soldier's alertness window (<see cref="Soldier.SetAlertedFor"/>) without forcing
        /// <c>pointGunEndTime = Time.time + 3.0f</c> when the soldier does not currently have an
        /// engageable visible target (<c>HasEnemyAndCanFireAtHim</c>). This prevents stationary
        /// defenders behind cover/walls/windows from raising their rifle into the shooting posture
        /// every 250ms and oscillating against <c>CheckTowardWallTps(1.0f)</c>.
        /// </summary>
        public static void RefreshAlertnessWithoutRaisingGun(Soldier soldier, float duration)
        {
            SoldierFastHandles h = GetOrCreateSoldierHandles(soldier);
            if (h == null || h.Ref == null)
                return;

            float now = Time.time;
            float prevPointGunEnd = h.Ref.pointGunEndTime;
            h.Ref.SetAlertedFor(duration);

            SoldierAI ai = h.Ai;
            bool canFireAtVisibleEnemy = !ReferenceEquals(ai, null) && ai != null && ai.HasEnemyAndCanFireAtHim;
            if (!canFireAtVisibleEnemy)
            {
                h.Ref.pointGunEndTime = h.Ref.isTowardWallTps
                    ? Mathf.Min(prevPointGunEnd, now)
                    : prevPointGunEnd;
            }
        }

        /// <summary>
        /// Rotates a soldier's body horizontally toward <paramref name="flatDir"/> via
        /// <see cref="Soldier.RotateToward"/> while preventing native <c>RotateToward</c>
        /// (<c>0x18076060C</c>) from forcing <c>pointGunEndTime = Time.time + 1.5f</c> (which raises
        /// the rifle into the shooting posture) or twisting <c>lookRotation.leftRight</c> against
        /// <c>LookForward</c> when the soldier is not actively aiming at an enemy.
        /// </summary>
        public static void RotateBodyWithoutRaisingGun(Soldier soldier, Vector3 flatDir, float deltaTime)
        {
            SoldierFastHandles h = GetOrCreateSoldierHandles(soldier);
            if (h == null || h.Ref == null || deltaTime <= 0f)
                return;

            flatDir.y = 0f;
            if (flatDir.sqrMagnitude < 0.001f)
                return;

            Soldier s = h.Ref;
            float now = Time.time;
            float prevPointGunEnd = s.pointGunEndTime;
            SoldierAI ai = h.Ai;
            bool isActivelyAiming = now < prevPointGunEnd &&
                                    !s.isTowardWallTps &&
                                    !ReferenceEquals(ai, null) &&
                                    ai != null &&
                                    ai.HasEnemyAndCanFireAtHim;

            var lookRot = s.lookRotation;
            float prevLeftRight = lookRot != null ? lookRot.leftRight : 0f;
            float prevUpDown = lookRot != null ? lookRot.upDown : 0f;

            s.RotateToward(flatDir.normalized, deltaTime);

            if (!isActivelyAiming)
            {
                s.pointGunEndTime = s.isTowardWallTps
                    ? Mathf.Min(prevPointGunEnd, now)
                    : prevPointGunEnd;

                if (lookRot != null)
                {
                    lookRot.leftRight = prevLeftRight;
                    lookRot.upDown = prevUpDown;
                }
            }
        }

        /// <summary>
        /// Moves an AI bot smoothly toward <paramref name="targetPos"/> while stopping at
        /// <paramref name="standoffDist"/> meters so bots never body-hug squadmates/corpses.
        /// Uses multi-node <c>CPathFinding</c> (<see cref="SoldierAI.MoveDestination"/>) for long
        /// distances (&gt; 16m) or obstructed lines of walk, and uses an overlap-safe duration on
        /// <see cref="SoldierAI.MoveDirectlyToward"/> for short unobstructed hops so
        /// <c>moveDirectlyToward_end</c> never expires mid-stride into vanilla's 1-node
        /// <c>MoveFPSOptimized(0.5f)</c> slow-walk fallback.
        /// </summary>
        public static void MoveBotTowardPosition(
            SoldierAI ai,
            Soldier soldier,
            Vector3 targetPos,
            float standoffDist = 0f,
            float minDirectDuration = 2.8f)
        {
            if (ai == null || soldier == null || soldier.transform == null)
                return;

            Vector3 myPos = soldier.transform.position;
            Vector3 flatDelta = targetPos - myPos;
            flatDelta.y = 0f;
            float flatDistSq = flatDelta.sqrMagnitude;
            float stopThreshold = Mathf.Max(0f, standoffDist) + 0.25f;
            if (flatDistSq <= stopThreshold * stopThreshold)
                return;

            float flatDist = Mathf.Sqrt(flatDistSq);
            Vector3 dir = flatDelta / flatDist;
            Vector3 dest = standoffDist > 0.05f ? (targetPos - dir * standoffDist) : targetPos;
            dest.y = targetPos.y;
            float moveDist = Mathf.Max(0.25f, flatDist - standoffDist);

            // Long runs (> 16m) or wall-obstructed paths must use multi-node CPathFinding (NodesCount >= 2)
            // rather than a 1-node straight-line MoveDirectlyToward segment.
            if (moveDist > 16.0f ||
                IsDirectionBlockedByEnvironment(myPos + Vector3.up * 0.85f, dir, Mathf.Min(moveDist, 7.5f), soldier.transform.root))
            {
                if (ai.moveDirectlyToward_end > Time.time)
                    ai.moveDirectlyToward_end = 0f;

                if ((ai.MoveDestination - dest).sqrMagnitude > 0.64f)
                    ai.MoveDestination = dest;

                return;
            }

            float duration = Mathf.Clamp(moveDist / 2.6f + 1.45f, minDirectDuration, 5.5f);
            ai.MoveDirectlyToward(dest, duration);
        }

        /// <summary>
        /// Computes true horizontal (XZ-plane) unsigned angle in degrees between <paramref name="soldier"/>'s
        /// forward vector and <paramref name="direction"/>. Replaces vanilla <c>Soldier.AngleToDirection</c>
        /// (<c>0x18073D4E0</c>), which accidentally casts <see cref="Vector3"/> to <see cref="Vector2"/>
        /// and drops the North/South (<c>Z</c>) component completely!
        /// </summary>
        public static float HorizontalAngleToDirection(Soldier soldier, Vector3 direction)
        {
            SoldierFastHandles h = GetOrCreateSoldierHandles(soldier);
            if (h == null || h.Tr == null)
                return 180f;

            Vector3 fwd = h.Tr.forward;
            float fx = fwd.x, fz = fwd.z;
            float dx = direction.x, dz = direction.z;
            float lenSq = (fx * fx + fz * fz) * (dx * dx + dz * dz);
            if (lenSq < 1e-8f)
                return 0f;

            float cos = Mathf.Clamp((fx * dx + fz * dz) / Mathf.Sqrt(lenSq), -1f, 1f);
            return Mathf.Acos(cos) * Mathf.Rad2Deg;
        }

        public static float HorizontalSignedAngleToDirection(Soldier soldier, Vector3 direction)
        {
            SoldierFastHandles h = GetOrCreateSoldierHandles(soldier);
            if (h == null || h.Tr == null)
                return 0f;

            Vector3 fwd = h.Tr.forward;
            float fx = fwd.x, fz = fwd.z;
            float dx = direction.x, dz = direction.z;
            float lenSq = (fx * fx + fz * fz) * (dx * dx + dz * dz);
            if (lenSq < 1e-8f)
                return 0f;

            float cos = Mathf.Clamp((fx * dx + fz * dz) / Mathf.Sqrt(lenSq), -1f, 1f);
            float angle = Mathf.Acos(cos) * Mathf.Rad2Deg;
            // Match vanilla SignedAngleToDirection's intended 2D (x, z) cross sign: fx*dz - dx*fz
            return (fx * dz - dx * fz) < 0f ? -angle : angle;
        }

        /// <summary>
        /// Returns true if any remote multiplayer human client's controlled soldier is within
        /// <paramref name="maxRadius"/> of <paramref name="worldPos"/> (cached 2 Hz scan, O(1) per call).
        /// Used so <c>Soldier.SyncSay</c> Photon RPCs are only sent when a remote player can actually hear the bot.
        /// </summary>
        public static bool IsNearAnyRemotePlayer(Vector3 worldPos, float maxRadius)
        {
            if (!IsMultiplayerWithRemotePlayers())
                return false;

            float now = Time.unscaledTime;
            if (now >= _nextRemotePlayerPosScanTime)
            {
                _nextRemotePlayerPosScanTime = now + 0.50f;
                _cachedRemotePlayerPositions.Clear();
                try
                {
                    var list = Creature.allCreatures;
                    if (list != null)
                    {
                        int count = list.Count;
                        for (int i = 0; i < count; i++)
                        {
                            Creature c = list[i];
                            if (c == null || !c.IsAlive)
                                continue;

                            Soldier s = c.TryCast<Soldier>();
                            if (s == null || s.transform == null)
                                continue;

                            SyncSoldier syncher = s.soldierSyncher;
                            if (syncher != null && !syncher.OwnSyncher && (syncher.controlled_by_player || s.IsPlayer()))
                            {
                                _cachedRemotePlayerPositions.Add(s.transform.position);
                            }
                        }
                    }
                }
                catch { }
            }

            float maxSq = maxRadius * maxRadius;
            for (int i = 0; i < _cachedRemotePlayerPositions.Count; i++)
            {
                if ((_cachedRemotePlayerPositions[i] - worldPos).sqrMagnitude <= maxSq)
                    return true;
            }
            return false;
        }

        // =====================================================================
        // Zero-Allocation Human Player Aim-Lane Cache (SP + Co-op Crosshair Yield)
        // Refreshed at 12.5 Hz (0.08s) so friendly bots avoid claiming cover or
        // squad spacing inside a human player's sights and proactively duck/yield.
        // =====================================================================
        public struct PlayerAimLane
        {
            public IntPtr Ptr;
            public Soldier Ref;
            public Transform RootTr;
            public string Faction;
            public Vector3 FeetPos;
            public Vector3 AimOrigin;
            public Vector3 AimDir3D;
            public Vector3 AimDirFlat;
            public Vector3 RightFlat;
            public float MaxClearForward;
            public float HalfCorridorWidth;
            public bool IsAdsOrFiring;
            public bool IsProne;
            public bool IsCrouched;
        }

        public static readonly PlayerAimLane[] ActivePlayerAimLanes = new PlayerAimLane[12];
        public static int ActivePlayerAimLaneCount;
        private static float _nextPlayerAimLaneRefreshTime;
        private static Camera _cachedMainCamera;
        private static float _nextMainCameraLookupTime;

        private static Camera GetCachedMainCamera(float now)
        {
            if (!ReferenceEquals(_cachedMainCamera, null) && _cachedMainCamera != null && _cachedMainCamera.isActiveAndEnabled)
                return _cachedMainCamera;

            if (now < _nextMainCameraLookupTime)
                return null;

            _nextMainCameraLookupTime = now + 1.0f;
            _cachedMainCamera = Camera.main;
            return (!ReferenceEquals(_cachedMainCamera, null) && _cachedMainCamera != null && _cachedMainCamera.isActiveAndEnabled)
                ? _cachedMainCamera
                : null;
        }

        private static void TryAppendPlayerAimLane(Soldier p, SoldierFastHandles h, bool isLocalPlayer, float now)
        {
            if (ActivePlayerAimLaneCount >= ActivePlayerAimLanes.Length ||
                h == null ||
                h.Tr == null ||
                p == null ||
                !p.IsAlive ||
                p.HasSurrended())
            {
                return;
            }

            string faction = GetCachedFaction(h, now);
            if (string.IsNullOrEmpty(faction))
                return;

            bool onGroundVeh = false;
            Transform rootTr = h.RootTr;
            if (p.IsOnVehicle())
            {
                VehicleFastHandles vh = GetOrCreateVehicleHandles(p.GetCurrentVehicle());
                if (vh == null || vh.IsAir || vh.IsParachute)
                    return;

                onGroundVeh = true;
                if (vh.RootTr != null)
                    rootTr = vh.RootTr;
            }

            Vector3 feetPos = h.Tr.position;
            Vector3 aimOrigin;
            Vector3 aimDir3D;

            if (onGroundVeh && isLocalPlayer)
            {
                Camera cam = GetCachedMainCamera(now);
                if (cam != null && cam.transform != null)
                {
                    aimOrigin = cam.transform.position;
                    aimDir3D = cam.transform.forward;
                }
                else
                {
                    aimOrigin = p.GunPosition();
                    aimDir3D = p.GetFireDir();
                }
            }
            else
            {
                aimOrigin = p.GunPosition();
                aimDir3D = p.GetFireDir();
            }

            if (aimDir3D.sqrMagnitude < 0.001f)
                aimDir3D = h.Tr.forward;
            else
                aimDir3D.Normalize();

            Vector3 aimFlat = new Vector3(aimDir3D.x, 0f, aimDir3D.z);
            if (aimFlat.sqrMagnitude < 0.001f)
            {
                Vector3 trFwd = h.Tr.forward;
                aimFlat = new Vector3(trFwd.x, 0f, trFwd.z);
            }
            if (aimFlat.sqrMagnitude < 0.001f)
                return;

            aimFlat.Normalize();
            Vector3 rightFlat = new Vector3(aimFlat.z, 0f, -aimFlat.x);

            bool adsOrFiring = onGroundVeh ||
                               p.IsAiming ||
                               p.IsAutoCoverAimActive ||
                               p.pressTriggerRequest ||
                               VisionPatches.HasFiredRecently(p, 2.2f);
            SoldierPose pose = onGroundVeh ? SoldierPose.Crouch : p.m_pose;

            ActivePlayerAimLanes[ActivePlayerAimLaneCount++] = new PlayerAimLane
            {
                Ptr = h.Ptr,
                Ref = p,
                RootTr = rootTr,
                Faction = faction,
                FeetPos = feetPos,
                AimOrigin = aimOrigin,
                AimDir3D = aimDir3D,
                AimDirFlat = aimFlat,
                RightFlat = rightFlat,
                MaxClearForward = adsOrFiring ? 16.5f : 9.5f,
                HalfCorridorWidth = adsOrFiring ? 1.35f : 0.95f,
                IsAdsOrFiring = adsOrFiring,
                IsProne = pose == SoldierPose.Prone,
                IsCrouched = pose == SoldierPose.Crouch
            };
        }

        public static void EnsurePlayerAimLanesUpdated()
        {
            float now = Time.time;
            if (now >= _nextPlayerAimLaneRefreshTime || now < _nextPlayerAimLaneRefreshTime - 2.0f)
            {
                _nextPlayerAimLaneRefreshTime = now + 0.08f;
                int prevCount = ActivePlayerAimLaneCount;
                ActivePlayerAimLaneCount = 0;

                try
                {
                    Soldier localPlayer = Soldier.CurrentControlledSoldierOrNull();
                    IntPtr localPtr = IntPtr.Zero;
                    if (!ReferenceEquals(localPlayer, null) && localPlayer != null && localPlayer.IsAlive)
                    {
                        SoldierFastHandles lpH = GetOrCreateSoldierHandles(localPlayer);
                        if (lpH != null)
                        {
                            localPtr = lpH.Ptr;
                            TryAppendPlayerAimLane(localPlayer, lpH, isLocalPlayer: true, now);
                        }
                    }

                    if (IsInMultiplayerRoom())
                    {
                        EnsureWorldSnapshot();
                        int solCount = WorldSoldierCount;
                        var soldiers = WorldSoldiers;
                        for (int i = 0; i < solCount && ActivePlayerAimLaneCount < ActivePlayerAimLanes.Length; i++)
                        {
                            ref SoldierSnapshot snap = ref soldiers[i];
                            if (!snap.IsPlayer || snap.Ptr == localPtr || snap.Ref == null)
                                continue;

                            SoldierFastHandles rpH = GetOrCreateSoldierHandles(snap.Ref);
                            TryAppendPlayerAimLane(snap.Ref, rpH, isLocalPlayer: false, now);
                        }
                    }
                }
                catch { }

                if (prevCount > ActivePlayerAimLaneCount)
                    Array.Clear(ActivePlayerAimLanes, ActivePlayerAimLaneCount, prevCount - ActivePlayerAimLaneCount);
            }
        }

        /// <summary>
        /// Zero-allocation, zero-raycast cover filter (~15ns) used in CombatCover.IsCoverAvailable
        /// and local cover searches so friendly bots never claim cover spots inside a human player's sightline.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsCoverInPlayerAimLane(Vector3 coverPos, Vector3 coverShootDirFlat)
        {
            EnsurePlayerAimLanesUpdated();
            int count = ActivePlayerAimLaneCount;
            if (count == 0)
                return false;

            for (int i = 0; i < count; i++)
            {
                ref PlayerAimLane lane = ref ActivePlayerAimLanes[i];
                if (Mathf.Abs(coverPos.y - lane.FeetPos.y) > 2.2f)
                    continue;

                // Only reject cover searches facing the same general sector as the player (friendly cover);
                // opposing enemy cover searches have shootDirection pointing back toward the player (dot < 0).
                if (coverShootDirFlat.sqrMagnitude > 0.001f && Vector3.Dot(lane.AimDirFlat, coverShootDirFlat) < 0.05f)
                    continue;

                float dx = coverPos.x - lane.AimOrigin.x;
                float dz = coverPos.z - lane.AimOrigin.z;
                float fwd = dx * lane.AimDirFlat.x + dz * lane.AimDirFlat.z;
                if (fwd < 0.35f || fwd > lane.MaxClearForward)
                    continue;

                float rayY = lane.AimOrigin.y + lane.AimDir3D.y * fwd;
                if (rayY > coverPos.y + 2.35f || rayY < coverPos.y - 0.55f)
                    continue;

                float lat = Mathf.Abs(dx * lane.RightFlat.x + dz * lane.RightFlat.z);
                if (lat <= lane.HalfCorridorWidth + 0.20f + fwd * 0.03f)
                    return true;
            }

            return false;
        }

        public static bool TryGetBlockingFriendlyPlayerAimLane(
            IntPtr botPtr,
            string botFaction,
            Vector3 worldPoint,
            float extraWidth,
            out PlayerAimLane blockingLane,
            out float forwardDist,
            out float signedLateral,
            out Vector3 clearStepDir)
        {
            EnsurePlayerAimLanesUpdated();
            int count = ActivePlayerAimLaneCount;
            for (int i = 0; i < count; i++)
            {
                ref PlayerAimLane lane = ref ActivePlayerAimLanes[i];
                if (lane.Ptr == botPtr || !AreSameSideFactions(botFaction, lane.Faction))
                    continue;

                if (Mathf.Abs(worldPoint.y - lane.FeetPos.y) > 2.30f)
                    continue;

                float dx = worldPoint.x - lane.AimOrigin.x;
                float dz = worldPoint.z - lane.AimOrigin.z;
                float fwd = dx * lane.AimDirFlat.x + dz * lane.AimDirFlat.z;
                if (fwd < 0.30f || fwd > lane.MaxClearForward)
                    continue;

                // Ignore when the player is aiming steep overhead (e.g. AA fire at aircraft) or steep below
                float rayY = lane.AimOrigin.y + lane.AimDir3D.y * fwd;
                if (rayY > worldPoint.y + 2.35f || rayY < worldPoint.y - 0.55f)
                    continue;

                float sLat = dx * lane.RightFlat.x + dz * lane.RightFlat.z;
                float halfWidth = lane.HalfCorridorWidth + extraWidth + fwd * 0.035f;
                if (Mathf.Abs(sLat) <= halfWidth)
                {
                    blockingLane = lane;
                    forwardDist = fwd;
                    signedLateral = sLat;
                    if (Mathf.Abs(sLat) > 0.08f)
                        clearStepDir = sLat >= 0f ? lane.RightFlat : -lane.RightFlat;
                    else
                        clearStepDir = ((GetDeterministicHash(botPtr, 97) & 1) == 0) ? lane.RightFlat : -lane.RightFlat;
                    return true;
                }
            }

            blockingLane = default;
            forwardDist = 0f;
            signedLateral = 0f;
            clearStepDir = Vector3.zero;
            return false;
        }

        /// <summary>
        /// Zero-allocation danger-close blast + dispersion safety check for handheld knee mortars and static/on-map
        /// indirect-fire artillery/mortars. Checks the local player, remote human players, and all friendly squads.
        /// </summary>
        public static bool IsIndirectFireImpactDangerClose(
            string myFaction,
            Vector3 impactPos,
            IntPtr shooterPtr = default,
            float friendlySafeRadius = 29.0f,
            float playerSafeRadius = 35.0f)
        {
            if (string.IsNullOrEmpty(myFaction))
                return false;

            float playerSafeSq = playerSafeRadius * playerSafeRadius;
            float friendlySafeSq = friendlySafeRadius * friendlySafeRadius;

            Soldier localPlayer = Soldier.CurrentControlledSoldierOrNull();
            if (localPlayer != null &&
                localPlayer.IsAlive &&
                localPlayer.Pointer != shooterPtr)
            {
                SoldierFastHandles lpHandles = GetOrCreateSoldierHandles(localPlayer);
                string lpFaction = GetCachedFaction(lpHandles, Time.time);
                if (lpHandles != null &&
                    lpHandles.Tr != null &&
                    !string.IsNullOrEmpty(lpFaction) &&
                    AreSameSideFactions(myFaction, lpFaction))
                {
                    Vehicle pVeh = localPlayer.IsOnVehicle() ? localPlayer.GetCurrentVehicle() : null;
                    if (pVeh == null || !pVeh.IsAirVehicle())
                    {
                        Vector3 dP = lpHandles.Tr.position - impactPos;
                        if (dP.x * dP.x + dP.z * dP.z <= playerSafeSq && Mathf.Abs(dP.y) <= 22.0f)
                            return true;
                    }
                }
            }

            EnsureWorldSnapshot();
            int sqCount = WorldSquadCount;
            if (sqCount <= 0)
                return false;

            float maxSquadCheckRadius = Mathf.Max(friendlySafeRadius, playerSafeRadius) + 48.0f;
            float maxSquadCheckSq = maxSquadCheckRadius * maxSquadCheckRadius;
            var squads = WorldSquads;
            var soldiers = WorldSoldiers;

            for (int s = 0; s < sqCount; s++)
            {
                ref SquadSnapshot sqSnap = ref squads[s];
                if (sqSnap.UnitCount == 0 || sqSnap.RefOnPlane || string.IsNullOrEmpty(sqSnap.Faction))
                    continue;

                if (!AreSameSideFactions(myFaction, sqSnap.Faction))
                    continue;

                Vector3 sqDelta = sqSnap.RefPos - impactPos;
                if (sqDelta.x * sqDelta.x + sqDelta.z * sqDelta.z > maxSquadCheckSq)
                    continue;

                int end = sqSnap.UnitStartIndex + sqSnap.UnitCount;
                for (int i = sqSnap.UnitStartIndex; i < end; i++)
                {
                    ref SoldierSnapshot solSnap = ref soldiers[i];
                    if (solSnap.Ptr == shooterPtr || solSnap.Ref == null)
                        continue;

                    Vector3 d = solSnap.Pos - impactPos;
                    float rSq = solSnap.IsPlayer ? playerSafeSq : friendlySafeSq;
                    if (d.x * d.x + d.z * d.z <= rSq && Mathf.Abs(d.y) <= 22.0f)
                        return true;
                }
            }

            return false;
        }

        [HarmonyPatch(typeof(Soldier), nameof(Soldier.AngleToDirection))]
        public static class Soldier_AngleToDirection_Patch
        {
            public static bool Prefix(Soldier __instance, Vector3 __0, ref float __result)
            {
                if (!VisionConfig.EnableMod.Value || __instance == null)
                    return true;

                __result = HorizontalAngleToDirection(__instance, __0);
                return false;
            }
        }

        [HarmonyPatch(typeof(Soldier), nameof(Soldier.SignedAngleToDirection))]
        public static class Soldier_SignedAngleToDirection_Patch
        {
            public static bool Prefix(Soldier __instance, Vector3 __0, ref float __result)
            {
                if (!VisionConfig.EnableMod.Value || __instance == null)
                    return true;

                __result = HorizontalSignedAngleToDirection(__instance, __0);
                return false;
            }
        }

        public static int GetDeterministicHash(IntPtr ptr, int seed)
        {
            unchecked
            {
                long val = ptr.ToInt64();
                int h = (int)(val ^ (val >> 32)) ^ (seed * 397);
                h ^= (h >> 16);
                h *= 0x7feb352d;
                h ^= (h >> 15);
                return h & 0x7FFFFFFF;
            }
        }

        public static void PruneDict<T>(Dictionary<IntPtr, T> dict, HashSet<IntPtr> alive)
        {
            if (dict == null || dict.Count == 0)
                return;

            _sharedPruneBuffer.Clear();
            foreach (var k in dict.Keys)
            {
                if (!alive.Contains(k))
                    _sharedPruneBuffer.Add(k);
            }
            for (int i = 0; i < _sharedPruneBuffer.Count; i++)
            {
                dict.Remove(_sharedPruneBuffer[i]);
            }
            _sharedPruneBuffer.Clear();
        }

        public static void PruneSet(HashSet<IntPtr> set, HashSet<IntPtr> alive)
        {
            if (set == null || set.Count == 0)
                return;

            _sharedPruneBuffer.Clear();
            foreach (var k in set)
            {
                if (!alive.Contains(k))
                    _sharedPruneBuffer.Add(k);
            }
            for (int i = 0; i < _sharedPruneBuffer.Count; i++)
            {
                set.Remove(_sharedPruneBuffer[i]);
            }
            _sharedPruneBuffer.Clear();
        }

        public static int TotalAdaptiveAiExtraTicks;

        // =====================================================================
        // Zero-GC Flat Battlefield World Snapshot (Eliminates Per-Frame & O(N^2)/O(N^3) Squad.AllSquads Scans)
        // =====================================================================
        public struct SquadSnapshot
        {
            public IntPtr Ptr;
            public Squad Ref;
            public Soldier Leader;
            public Soldier RefSoldier;
            public Vector3 RefPos;
            public string Faction;
            public int UnitStartIndex;
            public int UnitCount;
            public bool LeaderIsAlive;
            public bool IsVehicleCrew;
            public bool IsPlayerInSquad;
            public bool LeaderOnVehicle;
            public bool LeaderOnGroundVehicle;
            public bool RefOnPlane;
            public bool HasIntrinsicRadio;
            public bool HasRadioOrRelay;
            public bool IsSpottedOrAware;
        }

        public struct SoldierSnapshot
        {
            public IntPtr Ptr;
            public Soldier Ref;
            public IntPtr SquadPtr;
            public Vector3 Pos;
            public string Faction;
            public bool IsPlayer;
            public bool IsOnVehicle;
            public bool IsSquadLeader;
            public bool HasSurrendered;
            public bool IsSpotted;
            public bool IsInjured;
            public bool IsMedic;
            public bool IsAmmoCarrier;
            public bool CanBorrowAmmo;
            public bool HasHeadgear;
        }

        public static SquadSnapshot[] WorldSquads = new SquadSnapshot[96];
        public static int WorldSquadCount;
        public static SoldierSnapshot[] WorldSoldiers = new SoldierSnapshot[384];
        public static int WorldSoldierCount;
        public static SoldierSnapshot[] WorldInjuredSoldiers = new SoldierSnapshot[64];
        public static int WorldInjuredSoldierCount;

        private static readonly Dictionary<IntPtr, int> _worldSquadIndexByPtr = new Dictionary<IntPtr, int>(96);
        private static readonly HashSet<IntPtr> _seenWorldSoldierPtrs = new HashSet<IntPtr>(384);
        private static float _lastWorldSnapshotTime = -999f;

        public static bool TryGetSquadSnapshot(IntPtr sqPtr, out SquadSnapshot snap)
        {
            EnsureWorldSnapshot();
            if (sqPtr != IntPtr.Zero && _worldSquadIndexByPtr.TryGetValue(sqPtr, out int idx) && idx >= 0 && idx < WorldSquadCount)
            {
                snap = WorldSquads[idx];
                return true;
            }
            snap = default;
            return false;
        }

        public static void EnsureWorldSnapshot(float maxAge = 0.35f)
        {
            float now = Time.time;
            if (now >= _lastWorldSnapshotTime)
            {
                float age = now - _lastWorldSnapshotTime;
                if (age < maxAge)
                    return;

                // If heavy spatial/octree work (e.g. SquadLeaderRoutine) already ran on this frame, reuse the slightly older
                // snapshot for 1 more frame so the O(N) snapshot rebuild lands on a clean frame!
                if (IsHeavyWorkFrame() && age < maxAge + 0.30f)
                    return;
            }

            NotifyDirectorWorkThisFrame();
            long profSnapStart = HitchProfiler.Begin();
            HitchProfiler.RecordWorldSnapshotRebuilt();
            _lastWorldSnapshotTime = now;
            int prevSqCount = WorldSquadCount;
            int prevSolCount = WorldSoldierCount;
            int prevInjCount = WorldInjuredSoldierCount;
            WorldSquadCount = 0;
            WorldSoldierCount = 0;
            WorldInjuredSoldierCount = 0;
            _worldSquadIndexByPtr.Clear();
            _seenWorldSoldierPtrs.Clear();

            try
            {
                var allSquads = Squad.AllSquads;
                if (allSquads != null && allSquads.Count > 0)
                {
                    bool radioNetEnabled = VisionConfig.EnableRadiomanIntelNet != null && VisionConfig.EnableRadiomanIntelNet.Value;

                    foreach (var kvp in allSquads)
                    {
                        Squad sq = kvp.Value;
                        if (sq == null || sq.Pointer == IntPtr.Zero)
                            continue;

                        var units = sq.units;
                        int uCount = units != null ? units.Count : 0;
                        Soldier leader = sq.Leader;
                        SoldierFastHandles leaderHandles = (!ReferenceEquals(leader, null) && leader != null) ? GetOrCreateSoldierHandles(leader) : null;
                        bool leaderAlive = leaderHandles != null && leader.IsAlive && leaderHandles.Tr != null;

                        Soldier refSoldier = leaderAlive ? leader : null;
                        SoldierFastHandles refHandles = leaderAlive ? leaderHandles : null;
                        int unitStart = WorldSoldierCount;
                        int addedUnits = 0;
                        bool hasAliveRadiomanNear = false;
                        Vector3 leaderPos = leaderAlive ? leaderHandles.Tr.position : Vector3.zero;

                        for (int i = 0; i < uCount; i++)
                        {
                            Soldier u = units[i];
                            if (ReferenceEquals(u, null) || u == null || u.Pointer == IntPtr.Zero)
                                continue;

                            SoldierFastHandles uHandles = GetOrCreateSoldierHandles(u);
                            if (uHandles == null || uHandles.Tr == null)
                                continue;

                            if (!u.IsAlive)
                            {
                                // In Easy Red 2, incapacitated casualties waiting for a medic have IsAlive == false (life_total == 0)
                                // and IsInjured == true (!IsDead). Track them in WorldInjuredSoldiers while keeping WorldSoldiers strictly IsAlive!
                                if (u.IsInjured && !u.IsDead && _seenWorldSoldierPtrs.Add(uHandles.Ptr))
                                {
                                    if (WorldInjuredSoldierCount >= WorldInjuredSoldiers.Length)
                                        Array.Resize(ref WorldInjuredSoldiers, WorldInjuredSoldiers.Length * 2);

                                    WorldInjuredSoldiers[WorldInjuredSoldierCount++] = new SoldierSnapshot
                                    {
                                        Ptr = uHandles.Ptr,
                                        Ref = u,
                                        SquadPtr = sq.Pointer,
                                        Pos = uHandles.Tr.position,
                                        Faction = GetCachedFaction(uHandles, now),
                                        IsPlayer = u.IsPlayer(),
                                        IsOnVehicle = u.IsOnVehicle(),
                                        IsInjured = true
                                    };
                                }
                                continue;
                            }

                            if (refSoldier == null)
                            {
                                refSoldier = u;
                                refHandles = uHandles;
                            }

                            Vector3 uPos = uHandles.Tr.position;
                            bool uSurr = u.HasSurrended();
                            if (leaderAlive && radioNetEnabled && !hasAliveRadiomanNear && u.IsRadioman() && !uSurr)
                            {
                                if ((uPos - leaderPos).sqrMagnitude <= 32f * 32f)
                                    hasAliveRadiomanNear = true;
                            }

                            if (WorldSoldierCount >= WorldSoldiers.Length)
                                Array.Resize(ref WorldSoldiers, WorldSoldiers.Length * 2);

                            _seenWorldSoldierPtrs.Add(uHandles.Ptr);
                            WorldSoldiers[WorldSoldierCount++] = new SoldierSnapshot
                            {
                                Ptr = uHandles.Ptr,
                                Ref = u,
                                SquadPtr = sq.Pointer,
                                Pos = uPos,
                                Faction = GetCachedFaction(uHandles, now),
                                IsPlayer = u.IsPlayer(),
                                IsOnVehicle = u.IsOnVehicle(),
                                IsSquadLeader = leaderAlive && uHandles.Ptr == leaderHandles.Ptr,
                                HasSurrendered = uSurr,
                                IsSpotted = u.IsSpotted,
                                IsInjured = u.IsInjured,
                                IsMedic = u.IsMedic(),
                                IsAmmoCarrier = u.IsAmmoBoxCarrier(),
                                CanBorrowAmmo = u.CanBorrowAmmo,
                                HasHeadgear = u.hasHeadgear
                            };
                            addedUnits++;
                        }

                        if (refSoldier == null || refHandles == null)
                            continue;

                        Vector3 refPos = leaderAlive ? leaderPos : refHandles.Tr.position;
                        string faction = GetCachedFaction(refHandles, now);
                        bool leaderOnVeh = leaderAlive && leader.IsOnVehicle();
                        bool leaderOnGroundVeh = false;
                        bool leaderHasOnboardRadio = false;
                        if (leaderOnVeh)
                        {
                            VehicleFastHandles lvh = GetOrCreateVehicleHandles(leader.GetCurrentVehicle());
                            if (lvh != null && !lvh.IsParachute)
                            {
                                leaderHasOnboardRadio = true;
                                if (!lvh.IsAir)
                                    leaderOnGroundVeh = true;
                            }
                        }

                        bool refOnPlane = false;
                        if (refSoldier.IsOnVehicle())
                        {
                            VehicleFastHandles rvh = GetOrCreateVehicleHandles(refSoldier.GetCurrentVehicle());
                            if (rvh != null && rvh.IsAir)
                                refOnPlane = true;
                        }

                        bool isPlayerInSq = sq.IsPlayerInSquad();
                        bool hasIntrinsicRadio = !radioNetEnabled ||
                                                 (leaderAlive && (leaderHasOnboardRadio || hasAliveRadiomanNear || sq.RadioRequestActiveAndConfirmed()));

                        bool spottedOrAware = sq.IsEnemyAwareOfThisSquad ||
                                              sq.isMarked ||
                                              sq.isHalfMarked ||
                                              sq.isMarkedPinPoint ||
                                              refSoldier.IsSpotted;

                        if (WorldSquadCount >= WorldSquads.Length)
                            Array.Resize(ref WorldSquads, WorldSquads.Length * 2);

                        int sqIdx = WorldSquadCount++;
                        _worldSquadIndexByPtr[sq.Pointer] = sqIdx;
                        WorldSquads[sqIdx] = new SquadSnapshot
                        {
                            Ptr = sq.Pointer,
                            Ref = sq,
                            Leader = leader,
                            RefSoldier = refSoldier,
                            RefPos = refPos,
                            Faction = faction,
                            UnitStartIndex = unitStart,
                            UnitCount = addedUnits,
                            LeaderIsAlive = leaderAlive,
                            IsVehicleCrew = IsTrueVehicleCrewSquad(sq),
                            IsPlayerInSquad = isPlayerInSq,
                            LeaderOnVehicle = leaderOnVeh,
                            LeaderOnGroundVehicle = leaderOnGroundVeh,
                            RefOnPlane = refOnPlane,
                            HasIntrinsicRadio = hasIntrinsicRadio,
                            HasRadioOrRelay = hasIntrinsicRadio,
                            IsSpottedOrAware = spottedOrAware
                        };
                    }

                    // Pass 2: Pure C# L1-cache Radio Relay propagation (< 2 microseconds, zero IL2CPP boundary crossings)
                    if (radioNetEnabled && WorldSquadCount > 1)
                    {
                        for (int i = 0; i < WorldSquadCount; i++)
                        {
                            if (WorldSquads[i].HasRadioOrRelay || !WorldSquads[i].LeaderIsAlive || string.IsNullOrEmpty(WorldSquads[i].Faction))
                                continue;

                            string myFaction = WorldSquads[i].Faction;
                            float relayR = VeteranAndTacticsPatches.GetFactionCommandProfile(myFaction).RadioRelayRadius;
                            float relayRSq = relayR * relayR;
                            Vector3 myPos = WorldSquads[i].RefPos;

                            for (int j = 0; j < WorldSquadCount; j++)
                            {
                                if (i == j || !WorldSquads[j].LeaderIsAlive || WorldSquads[j].Faction != myFaction)
                                    continue;

                                if (!WorldSquads[j].HasIntrinsicRadio && !WorldSquads[j].LeaderOnGroundVehicle)
                                    continue;

                                if ((WorldSquads[j].RefPos - myPos).sqrMagnitude <= relayRSq)
                                {
                                    WorldSquads[i].HasRadioOrRelay = true;
                                    break;
                                }
                            }
                        }
                    }
                    // Pass 3: Append any live soldiers detached from Squad.units (e.g. surrendered soldiers whose
                    // joinedSquad was cleared by vanilla Soldier.Surrender(), or lone players/pilots).
                    // Discover detached creatures every 1.05s and reuse cached handles on intermediate 0.35s snapshots
                    // to eliminate 130+ Creature.allCreatures[i] wrapper allocations per snapshot!
                    if (now >= _nextDetachedCreatureScanTime)
                    {
                        _nextDetachedCreatureScanTime = now + 1.05f;
                        _cachedDetachedSoldiers.Clear();
                        var allCreatures = Creature.allCreatures;
                        if (allCreatures != null)
                        {
                            int cCount = allCreatures.Count;
                            for (int cIdx = 0; cIdx < cCount; cIdx++)
                            {
                                Creature c = allCreatures[cIdx];
                                if (ReferenceEquals(c, null) || c == null || c.Pointer == IntPtr.Zero || _seenWorldSoldierPtrs.Contains(c.Pointer))
                                    continue;

                                if (TryGetSoldierHandlesFromCreature(c, out SoldierFastHandles dh) && dh.Tr != null)
                                    _cachedDetachedSoldiers.Add(dh);
                            }
                        }
                    }

                    for (int dIdx = 0; dIdx < _cachedDetachedSoldiers.Count; dIdx++)
                    {
                        SoldierFastHandles dh = _cachedDetachedSoldiers[dIdx];
                        if (dh == null || ReferenceEquals(dh.Ref, null) || dh.Ref == null || dh.Tr == null || _seenWorldSoldierPtrs.Contains(dh.Ptr))
                            continue;

                        Soldier u = dh.Ref;
                        if (!u.IsAlive)
                        {
                            if (u.IsInjured && !u.IsDead && _seenWorldSoldierPtrs.Add(dh.Ptr))
                            {
                                if (WorldInjuredSoldierCount >= WorldInjuredSoldiers.Length)
                                    Array.Resize(ref WorldInjuredSoldiers, WorldInjuredSoldiers.Length * 2);

                                WorldInjuredSoldiers[WorldInjuredSoldierCount++] = new SoldierSnapshot
                                {
                                    Ptr = dh.Ptr,
                                    Ref = u,
                                    SquadPtr = u.joinedSquad != null ? u.joinedSquad.Pointer : IntPtr.Zero,
                                    Pos = dh.Tr.position,
                                    Faction = GetCachedFaction(dh, now),
                                    IsPlayer = u.IsPlayer(),
                                    IsOnVehicle = u.IsOnVehicle(),
                                    IsInjured = true
                                };
                            }
                            continue;
                        }

                        if (!_seenWorldSoldierPtrs.Add(dh.Ptr))
                            continue;

                        if (WorldSoldierCount >= WorldSoldiers.Length)
                            Array.Resize(ref WorldSoldiers, WorldSoldiers.Length * 2);

                        WorldSoldiers[WorldSoldierCount++] = new SoldierSnapshot
                        {
                            Ptr = dh.Ptr,
                            Ref = u,
                            SquadPtr = u.joinedSquad != null ? u.joinedSquad.Pointer : IntPtr.Zero,
                            Pos = dh.Tr.position,
                            Faction = GetCachedFaction(dh, now),
                            IsPlayer = u.IsPlayer(),
                            IsOnVehicle = u.IsOnVehicle(),
                            IsSquadLeader = false,
                            HasSurrendered = u.HasSurrended(),
                            IsSpotted = u.IsSpotted,
                            IsInjured = u.IsInjured,
                            IsMedic = u.IsMedic(),
                            IsAmmoCarrier = u.IsAmmoBoxCarrier(),
                            CanBorrowAmmo = u.CanBorrowAmmo,
                            HasHeadgear = u.hasHeadgear
                        };
                    }
                }
            }
            catch
            {
            }

            if (prevSqCount > WorldSquadCount)
                Array.Clear(WorldSquads, WorldSquadCount, prevSqCount - WorldSquadCount);
            if (prevSolCount > WorldSoldierCount)
                Array.Clear(WorldSoldiers, WorldSoldierCount, prevSolCount - WorldSoldierCount);
            if (prevInjCount > WorldInjuredSoldierCount)
                Array.Clear(WorldInjuredSoldiers, WorldInjuredSoldierCount, prevInjCount - WorldInjuredSoldierCount);
            HitchProfiler.End(HitchBucket.WorldSnapshot, profSnapStart);
        }

        public static void ClearSpatialCaches()
        {
            _turretVehicleCache.Clear();
            _turretTraitsCache.Clear();
            _ceilingCache.Clear();
            _bridgeCellCache.Clear();
            _sameSideFactionCache.Clear();
            _enemyFactionCache.Clear();
            _enemySpawnDirCache.Clear();
            _soldierHandlesByPtr.Clear();
            _soldierHandlesByAiPtr.Clear();
            _squadDiagIds.Clear();
            _confirmedVehicleCrewSquads.Clear();
            _confirmedInfantrySquads.Clear();
            _formattedFactionDisplayCache.Clear();
            _nextSoldierDiagId = 0;
            _nextSquadDiagId = 0;
            _vehicleHandlesByPtr.Clear();
            _nonSoldierCreaturePtrs.Clear();
            _nonUnitSpottablePtrs.Clear();
            _cachedDetachedSoldiers.Clear();
            _nextDetachedCreatureScanTime = 0f;
            _worldSquadIndexByPtr.Clear();
            if (WorldSquadCount > 0)
                Array.Clear(WorldSquads, 0, WorldSquadCount);
            if (WorldSoldierCount > 0)
                Array.Clear(WorldSoldiers, 0, WorldSoldierCount);
            if (WorldInjuredSoldierCount > 0)
                Array.Clear(WorldInjuredSoldiers, 0, WorldInjuredSoldierCount);
            if (ActivePlayerAimLaneCount > 0)
                Array.Clear(ActivePlayerAimLanes, 0, ActivePlayerAimLaneCount);
            ActivePlayerAimLaneCount = 0;
            _nextPlayerAimLaneRefreshTime = 0f;
            _cachedMainCamera = null;
            _nextMainCameraLookupTime = 0f;
            WorldSquadCount = 0;
            WorldSoldierCount = 0;
            WorldInjuredSoldierCount = 0;
            _lastWorldSnapshotTime = -999f;
            _nextAllowedBotRpcTime = 0f;
            TotalAdaptiveAiExtraTicks = 0;
            TotalPingGovernorSkippedCycles = 0;
            TotalHitchesOver28ms = 0;
            _currentWindowMaxFrameMs = 0f;
            _previousWindowMaxFrameMs = 0f;
            _nextFrameWindowRolloverTime = 0f;
        }

        public static void PruneSpatialCachesIfOversized()
        {
            if (_turretVehicleCache.Count > 256)
                _turretVehicleCache.Clear();
            if (_turretTraitsCache.Count > 256)
                _turretTraitsCache.Clear();
            if (_ceilingCache.Count > 4096)
                _ceilingCache.Clear();
            if (_bridgeCellCache.Count > 4096)
                _bridgeCellCache.Clear();
        }

        // =====================================================================
        // Adaptive AI Brain Tick Scheduler (Creature.ProcessSequentialUpdates)
        // =====================================================================
        // Vanilla Creature.ProcessSequentialUpdates (0x180A53760) hardcodes `cmp ebp, 1`,
        // updating only 1 slot in Creature.allCreatures per frame. In a 120-140 bot match,
        // each soldier waits 2.0-2.5+ seconds between SoldierAI.SequentialUpdate brain ticks,
        // and any frame that lands on a Vehicle, dead soldier, or seated passenger wastes
        // the entire frame's AI budget.
        [HarmonyPatch(typeof(Creature), nameof(Creature.ProcessSequentialUpdates))]
        public static class Creature_ProcessSequentialUpdates_Patch
        {
            private static bool _isRunningExtraTicks;

            public static void Prefix(out long __state)
            {
                __state = _isRunningExtraTicks ? 0L : HitchProfiler.BeginTopLevel();
            }

            public static void Postfix(long __state)
            {
                if (__state > 0L)
                    HitchProfiler.End(HitchBucket.VanSeqTick, __state);

                if (_isRunningExtraTicks)
                    return;

                if (!VisionConfig.EnableMod.Value || !VisionConfig.EnableAdaptiveAiTickScaling.Value || Time.timeScale <= 0.01f)
                    return;

                // Prioritize multiplayer ping & frame-time stability: never add extra ticks on busy frames
                // or on frames where vanilla SquadLeaderRoutine / world snapshot rebuild already ran!
                if (IsHeavyWorkFrame() || ShouldSkipBotCycleForNetworkPing())
                    return;

                float dt = Time.unscaledDeltaTime;
                if (dt > 0.025f)
                    return;

                long extraProfStart = HitchProfiler.Begin();
                try
                {
                    var list = Creature.allCreatures;
                    if (list == null)
                        return;

                    int count = list.Count;
                    if (count <= 1)
                        return;

                    int lastIdx = Creature.sequentialUpdateIndex - 1;
                    if (lastIdx < 0 || lastIdx >= count)
                        lastIdx = count - 1;

                    int activeFootSoldiersTicked = 0;
                    bool squadLeaderTicked = false;
                    Creature lastCreature = list[lastIdx];
                    if (!ReferenceEquals(lastCreature, null) && lastCreature != null && lastCreature.IsAlive &&
                        TryGetSoldierHandlesFromCreature(lastCreature, out SoldierFastHandles lastHandles))
                    {
                        Soldier lastSoldier = lastHandles.Ref;
                        if (lastSoldier.isActiveAndEnabled && !lastSoldier.IsOnVehicle())
                        {
                            activeFootSoldiersTicked = 1;
                            if (lastSoldier.IsSquadLeader())
                                squadLeaderTicked = true;
                        }
                    }

                    int players = GetConnectedPlayerCount();
                    int targetFootTicks;
                    int maxScanSteps;

                    if (players >= 4)
                    {
                        // Large multiplayer lobby (4-10+ players): strictly 1 active foot tick per frame
                        // so Photon outgoing serialization queues never contend with player ping.
                        targetFootTicks = 1;
                        maxScanSteps = Mathf.Min(count - 1, 3);
                    }
                    else if (players > 1)
                    {
                        // Small Co-op session (2-3 players): at most 2 ticks and only when frame time < 14.5ms
                        targetFootTicks = (count > 60 && dt < 0.0145f) ? 2 : 1;
                        maxScanSteps = Mathf.Min(count - 1, 4);
                    }
                    else
                    {
                        // Singleplayer: cap at 2 active foot soldiers per frame (1 vanilla + at most 1 extra within 0.45ms CPU budget)
                        // so multiple SquadLeaderRoutine octree/pathfinding bursts NEVER stack onto the same frame!
                        targetFootTicks = (count > 55 && dt < 0.0175f) ? 2 : 1;
                        maxScanSteps = Mathf.Min(count - 1, 6);
                    }

                    if (activeFootSoldiersTicked >= targetFootTicks)
                        return;

                    _isRunningExtraTicks = true;
                    int scanned = 0;
                    long startStamp = Stopwatch.GetTimestamp();
                    long budgetTicks = (long)(Stopwatch.Frequency * 0.00045); // Strict 0.45ms ceiling for extra AI ticks

                    while (activeFootSoldiersTicked < targetFootTicks && scanned < maxScanSteps)
                    {
                        if ((Stopwatch.GetTimestamp() - startStamp) >= budgetTicks)
                            break;

                        int idx = Creature.sequentialUpdateIndex;
                        if (idx < 0 || idx >= count)
                            idx = 0;

                        Creature c = list[idx];
                        if (ReferenceEquals(c, null) || c == null)
                        {
                            Creature.sequentialUpdateIndex = idx + 1;
                            scanned++;
                            continue;
                        }

                        if (!TryGetSoldierHandlesFromCreature(c, out SoldierFastHandles sHandles) || !sHandles.Ref.isActiveAndEnabled)
                        {
                            Creature.sequentialUpdateIndex = idx + 1;
                            scanned++;
                            continue;
                        }

                        Soldier s = sHandles.Ref;
                        // Never run two Squad Leaders (SquadOrdersRoutine -> 8x CoverManager octree searches) in the same frame!
                        bool isLeader = s.IsAlive && !s.IsOnVehicle() && s.IsSquadLeader();
                        if (isLeader && squadLeaderTicked)
                            break;

                        Creature.sequentialUpdateIndex = idx + 1;
                        scanned++;

                        s.SequentialUpdate();

                        SoldierAI ai = sHandles.Ai;
                        if (!ReferenceEquals(ai, null) && ai != null && ai.enabled)
                        {
                            ai.SequentialUpdate();
                        }

                        if (s.IsAlive && !s.IsOnVehicle())
                        {
                            activeFootSoldiersTicked++;
                            TotalAdaptiveAiExtraTicks++;
                            if (isLeader)
                                squadLeaderTicked = true;
                        }
                    }
                }
                catch
                {
                }
                finally
                {
                    _isRunningExtraTicks = false;
                    HitchProfiler.End(HitchBucket.ModExtraSeqTicks, extraProfStart);
                }
            }
        }
    }
}
