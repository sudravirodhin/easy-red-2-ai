using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace EasyRed2HumanAI
{
    public static partial class VeteranAndTacticsPatches
    {
        // =====================================================================
        // 3-TIER HIERARCHICAL GOAP, SQUAD BLACKBOARD & RADIOMAN INTEL NET (v0.6.1)
        // Tier 3 (Team Net GOAP): Cross-Squad Anvil & Hammer + Combined-Arms Support
        //         (Strictly gated by alive Radioman / Vehicle Radio relay!)
        // Tier 2 (Squad Leader GOAP): Translates Net Directive or Local Objective
        //         into Squad Directives & Role Slots (Suppress / Flank / Breach / Radio)
        // Tier 1 (Individual Bot GOAP): 32-bit WorldState + Role/Personality +
        //         Radioman Link Preservation + Lone-Wolf Survival/Link-Up
        // =====================================================================

        [Flags]
        internal enum GoapStateBits : ulong
        {
            None                      = 0UL,
            IsBleeding                = 1UL << 0,
            InCover                   = 1UL << 1,
            IsSuppressed              = 1UL << 2,
            IsHeavilySuppressed       = 1UL << 3,
            HasVisibleEnemy           = 1UL << 4,
            CanFireAtVisibleEnemy     = 1UL << 5,
            TargetLostBehindCover     = 1UL << 6,
            SquadHasActiveThreat      = 1UL << 7,
            AllyPinningThreat         = 1UL << 8,
            ArmorThreatClose          = 1UL << 9,
            HasFragGrenade            = 1UL << 10,
            HasSmokeGrenade           = 1UL << 11,
            InGrenadeRange            = 1UL << 12,
            InPointBlankRange         = 1UL << 13,
            FlankCoverAvailable       = 1UL << 14,
            ExposedSideOfWall         = 1UL << 15,
            ComradeNeedsLiberation    = 1UL << 16,
            IsMachineGunner           = 1UL << 17,
            IsAssaulterOrSkirmisher   = 1UL << 18,
            IsRadioman                = 1UL << 19,
            RadioLinkDrifting         = 1UL << 20,
            IsEntirelyAlone           = 1UL << 21,
            OnBridgeOrNarrow          = 1UL << 22,
            CanProvideOverwatch       = 1UL << 23,
            NeedAmmoOrMedScavenge     = 1UL << 24,
            ObjectiveCapContestUrgent = 1UL << 25,

            // Goal & Chain Effect Bits
            SurvivedCrisis            = 1UL << 26,
            RadioLinkPreserved        = 1UL << 27,
            TargetPinned              = 1UL << 28,
            TargetEliminated          = 1UL << 29,
            SquadSupported            = 1UL << 30,
            HasBandage                = 1UL << 31,
            OverwhelmedNeedFallback   = 1UL << 32,
            NearFreshCasualty         = 1UL << 33,
            BandageUnsafeUnderFire    = 1UL << 34
        }

        internal enum SquadNetDirective : byte
        {
            Disconnected_Autonomous = 0,
            Connected_StandardObjective = 1,
            Anvil_PinAndCallSupport = 2,
            Hammer_WideFlankEnvelopment = 3,
            Regroup_SectorWithdrawal = 4
        }

        internal enum GoapActionId : byte
        {
            None = 0,
            SeekEmergencyCover = 1,
            BandageInCover = 2,
            RepositionBehindWall = 3,
            MaintainRadioLinkWithLeader = 4,
            ThrowSmokeScreen = 5,
            SuppressThreatPosition = 6,
            MoveToFlankCover = 7,
            FlushThreatWithFrag = 8,
            HoldOverwatchFire = 9,
            BoundWithOverwatch = 10,
            PointBlankBayonetBreach = 11,
            RescueSurrenderedComrade = 12,
            LoneWolfLinkUpOrAmbush = 13,
            DirectFireEngagement = 14,
            ScavengeSupplyFromCorpse = 15,
            TacticalFallbackRetreat = 16
        }

        private struct GoapActionDef
        {
            public GoapActionId Id;
            public GoapStateBits Required;
            public GoapStateBits Forbidden;
            public GoapStateBits Add;
            public GoapStateBits Remove;
            public float BaseCost;
        }

        private static readonly GoapActionDef[] _goapActions = new GoapActionDef[]
        {
            new GoapActionDef
            {
                Id = GoapActionId.SeekEmergencyCover,
                Required = GoapStateBits.None,
                Forbidden = GoapStateBits.InCover,
                Add = GoapStateBits.InCover | GoapStateBits.SurvivedCrisis,
                Remove = GoapStateBits.ExposedSideOfWall,
                BaseCost = 1.2f
            },
            new GoapActionDef
            {
                Id = GoapActionId.BandageInCover,
                Required = GoapStateBits.IsBleeding | GoapStateBits.HasBandage | GoapStateBits.InCover,
                Forbidden = GoapStateBits.InPointBlankRange | GoapStateBits.ExposedSideOfWall | GoapStateBits.ArmorThreatClose | GoapStateBits.BandageUnsafeUnderFire,
                Add = GoapStateBits.SurvivedCrisis,
                Remove = GoapStateBits.IsBleeding,
                BaseCost = 0.7f
            },
            new GoapActionDef
            {
                Id = GoapActionId.RepositionBehindWall,
                Required = GoapStateBits.ExposedSideOfWall,
                Forbidden = GoapStateBits.OnBridgeOrNarrow | GoapStateBits.InPointBlankRange,
                Add = GoapStateBits.InCover | GoapStateBits.SurvivedCrisis,
                Remove = GoapStateBits.ExposedSideOfWall,
                BaseCost = 1.1f
            },
            // Radioman Lifeline Action: keep radio link alive with Squad Leader & confirm radio support calls
            new GoapActionDef
            {
                Id = GoapActionId.MaintainRadioLinkWithLeader,
                Required = GoapStateBits.IsRadioman | GoapStateBits.RadioLinkDrifting,
                Forbidden = GoapStateBits.IsBleeding | GoapStateBits.InPointBlankRange,
                Add = GoapStateBits.RadioLinkPreserved | GoapStateBits.SquadSupported | GoapStateBits.SurvivedCrisis,
                Remove = GoapStateBits.RadioLinkDrifting,
                BaseCost = 0.85f
            },
            new GoapActionDef
            {
                Id = GoapActionId.ThrowSmokeScreen,
                Required = GoapStateBits.HasSmokeGrenade | GoapStateBits.SquadHasActiveThreat,
                Forbidden = GoapStateBits.InPointBlankRange | GoapStateBits.AllyPinningThreat,
                Add = GoapStateBits.AllyPinningThreat | GoapStateBits.SurvivedCrisis | GoapStateBits.SquadSupported,
                Remove = GoapStateBits.HasSmokeGrenade,
                BaseCost = 1.45f
            },
            new GoapActionDef
            {
                Id = GoapActionId.SuppressThreatPosition,
                Required = GoapStateBits.SquadHasActiveThreat,
                Forbidden = GoapStateBits.IsBleeding | GoapStateBits.InPointBlankRange,
                Add = GoapStateBits.AllyPinningThreat | GoapStateBits.TargetPinned | GoapStateBits.SquadSupported,
                Remove = GoapStateBits.None,
                BaseCost = 1.8f
            },
            // Radioman is forbidden from volunteering for wide exposed flanking bounds so he preserves the radio link!
            new GoapActionDef
            {
                Id = GoapActionId.MoveToFlankCover,
                Required = GoapStateBits.SquadHasActiveThreat | GoapStateBits.AllyPinningThreat | GoapStateBits.FlankCoverAvailable,
                Forbidden = GoapStateBits.IsBleeding | GoapStateBits.IsMachineGunner | GoapStateBits.IsRadioman | GoapStateBits.OnBridgeOrNarrow,
                Add = GoapStateBits.InCover | GoapStateBits.InGrenadeRange | GoapStateBits.TargetLostBehindCover | GoapStateBits.TargetEliminated,
                Remove = GoapStateBits.None,
                BaseCost = 1.6f
            },
            new GoapActionDef
            {
                Id = GoapActionId.FlushThreatWithFrag,
                Required = GoapStateBits.HasFragGrenade | GoapStateBits.InGrenadeRange | GoapStateBits.TargetLostBehindCover,
                Forbidden = GoapStateBits.IsBleeding | GoapStateBits.IsRadioman | GoapStateBits.InPointBlankRange | GoapStateBits.CanFireAtVisibleEnemy,
                Add = GoapStateBits.TargetEliminated | GoapStateBits.SquadSupported,
                Remove = GoapStateBits.HasFragGrenade,
                BaseCost = 1.4f
            },
            new GoapActionDef
            {
                Id = GoapActionId.HoldOverwatchFire,
                Required = GoapStateBits.CanProvideOverwatch | GoapStateBits.SquadHasActiveThreat,
                Forbidden = GoapStateBits.IsBleeding | GoapStateBits.InPointBlankRange,
                Add = GoapStateBits.AllyPinningThreat | GoapStateBits.TargetPinned | GoapStateBits.SquadSupported,
                Remove = GoapStateBits.None,
                BaseCost = 1.85f
            },
            new GoapActionDef
            {
                Id = GoapActionId.BoundWithOverwatch,
                Required = GoapStateBits.SquadHasActiveThreat | GoapStateBits.AllyPinningThreat,
                Forbidden = GoapStateBits.IsBleeding | GoapStateBits.IsHeavilySuppressed | GoapStateBits.IsMachineGunner | GoapStateBits.IsRadioman | GoapStateBits.InPointBlankRange,
                Add = GoapStateBits.InCover | GoapStateBits.CanFireAtVisibleEnemy | GoapStateBits.TargetEliminated | GoapStateBits.SquadSupported,
                Remove = GoapStateBits.None,
                BaseCost = 2.05f
            },
            new GoapActionDef
            {
                Id = GoapActionId.PointBlankBayonetBreach,
                Required = GoapStateBits.InPointBlankRange | GoapStateBits.HasVisibleEnemy,
                Forbidden = GoapStateBits.None,
                Add = GoapStateBits.TargetEliminated | GoapStateBits.SurvivedCrisis,
                Remove = GoapStateBits.None,
                BaseCost = 0.5f
            },
            new GoapActionDef
            {
                Id = GoapActionId.RescueSurrenderedComrade,
                Required = GoapStateBits.ComradeNeedsLiberation,
                Forbidden = GoapStateBits.IsBleeding | GoapStateBits.IsHeavilySuppressed | GoapStateBits.IsRadioman | GoapStateBits.InPointBlankRange,
                Add = GoapStateBits.SquadSupported,
                Remove = GoapStateBits.ComradeNeedsLiberation,
                BaseCost = 2.1f
            },
            new GoapActionDef
            {
                Id = GoapActionId.LoneWolfLinkUpOrAmbush,
                Required = GoapStateBits.IsEntirelyAlone,
                Forbidden = GoapStateBits.IsBleeding | GoapStateBits.InPointBlankRange | GoapStateBits.CanFireAtVisibleEnemy,
                Add = GoapStateBits.InCover | GoapStateBits.SurvivedCrisis | GoapStateBits.SquadSupported,
                Remove = GoapStateBits.IsEntirelyAlone,
                BaseCost = 1.35f
            },
            new GoapActionDef
            {
                Id = GoapActionId.DirectFireEngagement,
                Required = GoapStateBits.CanFireAtVisibleEnemy,
                Forbidden = GoapStateBits.InPointBlankRange,
                Add = GoapStateBits.TargetEliminated,
                Remove = GoapStateBits.None,
                BaseCost = 1.7f
            },
            new GoapActionDef
            {
                Id = GoapActionId.ScavengeSupplyFromCorpse,
                Required = GoapStateBits.NeedAmmoOrMedScavenge,
                Forbidden = GoapStateBits.IsHeavilySuppressed | GoapStateBits.InPointBlankRange | GoapStateBits.OnBridgeOrNarrow,
                Add = GoapStateBits.HasBandage | GoapStateBits.SurvivedCrisis | GoapStateBits.SquadSupported,
                Remove = GoapStateBits.NeedAmmoOrMedScavenge,
                BaseCost = 1.15f
            },
            new GoapActionDef
            {
                Id = GoapActionId.TacticalFallbackRetreat,
                Required = GoapStateBits.OverwhelmedNeedFallback,
                Forbidden = GoapStateBits.IsBleeding | GoapStateBits.InPointBlankRange | GoapStateBits.OnBridgeOrNarrow,
                Add = GoapStateBits.InCover | GoapStateBits.SurvivedCrisis | GoapStateBits.SquadSupported,
                Remove = GoapStateBits.OverwhelmedNeedFallback | GoapStateBits.ArmorThreatClose | GoapStateBits.ExposedSideOfWall,
                BaseCost = 0.88f
            }
        };

        internal struct BotScavengeCache
        {
            public float NextScanTime;
            public InventoryManager TargetInventory;
            public ItemObject TargetFloorItem;
            public AmmoRefillCrate TargetRefillCrate;
            public VirtualItem TargetInventoryAtWeapon;
            public VirtualItem TargetInventoryPrimaryWeapon;
            public VirtualItem TargetInventoryWearable;
            public Soldier TargetBuddyDonor;
            public Soldier TargetNeedyAllyToFeed;
            public Vector3 TargetPosition;
            public bool NeedAmmo;
            public bool NeedBandage;
            public bool NeedFragGrenade;
            public bool NeedSmokeGrenade;
            public bool IsDroppableAmmoBox;
            public bool IsAtWeaponPickup;
            public bool IsRoleWeaponUpgrade;
            public bool IsBuddyBandageRefill;
            public bool IsBuddyAmmoBorrow;
            public bool IsFeedingNeedyAlly;
            public bool IsRadioPackPickup;
            public bool IsHelmetPickup;
            public bool IsMedicSyringePickup;
        }

        private enum FloorItemKind : byte
        {
            Unknown = 0,
            Ignore = 1,
            DroppableBox = 2,
            Gun = 3,
            Helmet = 4,
            RadioPack = 5,
            Ammo = 6,
            Bandage = 7,
            FragGrenade = 8,
            SmokeGrenade = 9,
            AtGrenade = 10,
            Magazine = 11,
            Syringe = 12
        }

        private struct FreeFloorItemEntry
        {
            public ItemObject Item;
            public DroppableAmmoBox Box;
            public GenericGun Gun;
            public Vector3 Pos;
            public FloorItemKind Kind;
        }

        private struct CachedBoxOverrideInfo
        {
            public bool Valid;
            public bool IsDefaultWeaponAmmo;
            public bool HasAmmo;
            public bool HasFragGrenade;
            public bool HasSmokeGrenade;
            public bool HasAtGrenade;
            public bool HasAtWeapon;
            public bool HasBandage;
        }

        private struct FloorItemStaticMeta
        {
            public FloorItemKind Kind;
            public GameObject Go;
            public Transform Tr;
            public DroppableAmmoBox Box;
            public GenericGun Gun;
            public ItemGrenade Grenade;
        }

        private struct ScavengeInventoryEntry
        {
            public InventoryManager Inv;
            public IntPtr Ptr;
            public Soldier DeadSoldierOwner;
            public Vehicle VehOwner;
            public string OwnerFaction;
            public Vector3 Pos;
            public bool IsDeadBodyOrDisabledCrate;
        }

        private static List<FreeFloorItemEntry> _freeFloorItemsSnapshot = new List<FreeFloorItemEntry>(64);
        private static List<FreeFloorItemEntry> _stagingFreeFloorItems = new List<FreeFloorItemEntry>(64);
        private static readonly Dictionary<IntPtr, FloorItemStaticMeta> _cachedFloorItemMeta = new Dictionary<IntPtr, FloorItemStaticMeta>(2048);
        private static List<ScavengeInventoryEntry> _activeScavengeInventories = new List<ScavengeInventoryEntry>(64);
        private static List<ScavengeInventoryEntry> _stagingScavengeInventories = new List<ScavengeInventoryEntry>(64);
        private static readonly List<AmmoRefillCrate> _activeAmmoRefillCrates = new List<AmmoRefillCrate>(32);
        private static readonly Dictionary<IntPtr, CachedBoxOverrideInfo> _cachedBoxOverrides = new Dictionary<IntPtr, CachedBoxOverrideInfo>(64);
        private static float _nextFreeFloorItemsSnapshotTime;
        private static int _floorScanCursor;
        private static int _lastFloorScanFrame;
        private static float _nextScavengeInvSnapshotTime;
        private static int _scavengeInvScanCursor;
        private static int _lastScavengeInvScanFrame;

        [HarmonyPatch(typeof(AmmoRefillCrate), nameof(AmmoRefillCrate.Start))]
        public static class AmmoRefillCrate_Start_Patch
        {
            public static void Postfix(AmmoRefillCrate __instance)
            {
                try
                {
                    if (__instance == null || __instance.Pointer == IntPtr.Zero)
                        return;

                    IntPtr ptr = __instance.Pointer;
                    for (int i = _activeAmmoRefillCrates.Count - 1; i >= 0; i--)
                    {
                        AmmoRefillCrate existing = _activeAmmoRefillCrates[i];
                        if (existing == null || existing.Pointer == IntPtr.Zero)
                        {
                            _activeAmmoRefillCrates.RemoveAt(i);
                        }
                        else if (existing.Pointer == ptr)
                        {
                            return;
                        }
                    }

                    _activeAmmoRefillCrates.Add(__instance);
                }
                catch { }
            }
        }

        private static CachedBoxOverrideInfo GetCachedBoxOverrideInfo(
            IntPtr boxPtr,
            Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStringArray overrideContent,
            bool allowTakeWeapon)
        {
            if (boxPtr != IntPtr.Zero && _cachedBoxOverrides.TryGetValue(boxPtr, out CachedBoxOverrideInfo cached))
                return cached;

            CachedBoxOverrideInfo info = new CachedBoxOverrideInfo { Valid = true };
            if (overrideContent == null || overrideContent.Length == 0)
            {
                info.IsDefaultWeaponAmmo = true;
                info.HasAmmo = true;
            }
            else
            {
                int len = overrideContent.Length;
                for (int i = 0; i < len; i++)
                {
                    string itemId = overrideContent[i];
                    if (string.IsNullOrEmpty(itemId))
                        continue;

                    ItemObject prefab = ItemsDatabase.GetItemObject(itemId);
                    if (prefab == null || prefab.Pointer == IntPtr.Zero)
                        continue;

                    GenericGun gunPrefab = prefab.TryCast<GenericGun>();
                    if (gunPrefab != null)
                    {
                        if (allowTakeWeapon && gunPrefab.IsAtWeapon())
                            info.HasAtWeapon = true;
                        continue;
                    }

                    if (prefab.TryCast<AmmoItem>() != null)
                    {
                        info.HasAmmo = true;
                        continue;
                    }

                    VirtualItem vi = prefab.ToVirtualItem();
                    if (vi == null)
                        continue;

                    if (vi.TryCast<VirtualATGrenade>() != null)
                        info.HasAtGrenade = true;
                    else if (vi.TryCast<VirtualGrenade>() != null)
                        info.HasFragGrenade = true;
                    else if (vi.TryCast<VirtualSmokeGrenade>() != null)
                        info.HasSmokeGrenade = true;
                    else if (vi.TryCast<VirtualBandages>() != null)
                        info.HasBandage = true;
                    else if (vi.TryCast<VirtualMagazineItem>() != null || vi.TryCast<VirtualAmmo>() != null)
                        info.HasAmmo = true;
                }
            }

            if (boxPtr != IntPtr.Zero)
            {
                if (_cachedBoxOverrides.Count > 256)
                    _cachedBoxOverrides.Clear();
                _cachedBoxOverrides[boxPtr] = info;
            }

            return info;
        }

        private static bool BoxOverrideMatchesNeeds(
            CachedBoxOverrideInfo ov,
            bool needAmmo,
            bool needAtWeapon,
            bool wantFragGrenade,
            bool wantSmokeGrenade,
            bool needBandage)
        {
            return (needAmmo && ov.HasAmmo) ||
                   (needAtWeapon && (ov.HasAtWeapon || ov.HasAtGrenade)) ||
                   (wantFragGrenade && ov.HasFragGrenade) ||
                   (wantSmokeGrenade && ov.HasSmokeGrenade) ||
                   (needBandage && ov.HasBandage);
        }

        private static bool TryGetOrCreateFloorItemMeta(ItemObject item, out FloorItemStaticMeta meta)
        {
            IntPtr ptr = item.Pointer;
            if (_cachedFloorItemMeta.TryGetValue(ptr, out meta))
            {
                if (meta.Kind == FloorItemKind.Ignore)
                    return false;
                if (!ReferenceEquals(meta.Go, null) && meta.Go != null && !ReferenceEquals(meta.Tr, null) && meta.Tr != null)
                    return true;
                _cachedFloorItemMeta.Remove(ptr);
            }

            GameObject go = item.gameObject;
            Transform tr = item.transform;
            if (go == null || tr == null)
            {
                meta = default;
                return false;
            }

            DroppableAmmoBox box = item.TryCast<DroppableAmmoBox>();
            GenericGun gun = null;
            ItemGrenade ig = null;
            FloorItemKind kind = FloorItemKind.Ignore;

            if (box != null)
            {
                kind = FloorItemKind.DroppableBox;
            }
            else
            {
                gun = item.TryCast<GenericGun>();
                if (gun != null)
                {
                    kind = FloorItemKind.Gun;
                }
                else
                {
                    ig = item.TryCast<ItemGrenade>();
                    if (ig != null)
                    {
                        GrenadeType gt = ig.grenadeType;
                        if (gt == GrenadeType.ATGrenade)
                            kind = FloorItemKind.AtGrenade;
                        else if (gt == GrenadeType.smokeGrenade)
                            kind = FloorItemKind.SmokeGrenade;
                        else
                            kind = FloorItemKind.FragGrenade;
                    }
                    else if (item.TryCast<AmmoItem>() != null)
                    {
                        kind = FloorItemKind.Ammo;
                    }
                    else if (item.TryCast<ItemObjectBandages>() != null)
                    {
                        kind = FloorItemKind.Bandage;
                    }
                    else if (item.TryCast<ItemHelmet>() != null)
                    {
                        kind = FloorItemKind.Helmet;
                    }
                    else
                    {
                        ItemClothing cloth = item.TryCast<ItemClothing>();
                        if (cloth != null)
                        {
                            kind = cloth.isRadio ? FloorItemKind.RadioPack : FloorItemKind.Ignore;
                        }
                        else
                        {
                            VirtualItem vi = item.ToVirtualItem();
                            if (vi != null)
                            {
                                if (vi.TryCast<VirtualMagazineItem>() != null)
                                    kind = FloorItemKind.Magazine;
                                else if (vi.TryCast<VirtualSyringe>() != null)
                                    kind = FloorItemKind.Syringe;
                                else if (vi.TryCast<VirtualBandages>() != null)
                                    kind = FloorItemKind.Bandage;
                            }
                        }
                    }
                }
            }

            meta = new FloorItemStaticMeta
            {
                Kind = kind,
                Go = go,
                Tr = tr,
                Box = box,
                Gun = gun,
                Grenade = ig
            };

            if (_cachedFloorItemMeta.Count > 4096)
                _cachedFloorItemMeta.Clear();
            _cachedFloorItemMeta[ptr] = meta;
            return kind != FloorItemKind.Ignore && kind != FloorItemKind.Unknown;
        }

        private static void EnsureFreeFloorItemsSnapshot(float now)
        {
            int frame = Time.frameCount;
            if (_lastFloorScanFrame == frame)
                return;

            bool midPass = _floorScanCursor > 0;
            if (!midPass && now < _nextFreeFloorItemsSnapshotTime && _nextFreeFloorItemsSnapshotTime - now <= 5.0f)
                return;

            // Defer non-urgent floor-item scan chunk by 1 frame if SquadLeaderRoutine / WorldSnapshot / MacroDirector already ran
            if (AiCore.IsHeavyWorkFrame() && _nextFreeFloorItemsSnapshotTime > 0f && now < _nextFreeFloorItemsSnapshotTime + 1.5f)
                return;

            _lastFloorScanFrame = frame;
            long profFloorStart = HitchProfiler.Begin();
            try
            {
                var floorItems = ItemObject.spawnedItems;
                if (floorItems == null)
                {
                    _freeFloorItemsSnapshot.Clear();
                    _stagingFreeFloorItems.Clear();
                    _floorScanCursor = 0;
                    _nextFreeFloorItemsSnapshotTime = now + 1.00f;
                    HitchProfiler.RecordFloorSnapshotRebuilt(0);
                    return;
                }

                int fCount = floorItems.Count;
                if (fCount == 0)
                {
                    _freeFloorItemsSnapshot.Clear();
                    _stagingFreeFloorItems.Clear();
                    _floorScanCursor = 0;
                    _nextFreeFloorItemsSnapshotTime = now + 1.00f;
                    HitchProfiler.RecordFloorSnapshotRebuilt(0);
                    return;
                }

                if (_floorScanCursor < 0 || _floorScanCursor >= fCount)
                {
                    _floorScanCursor = 0;
                    _stagingFreeFloorItems.Clear();
                }

                // Scan at most 96 spawned items per frame so 500+ scene items never cause a single-frame wrapper/JNI spike!
                int end = Mathf.Min(_floorScanCursor + 96, fCount);
                for (int i = _floorScanCursor; i < end; i++)
                {
                    ItemObject item = floorItems[i];
                    if (ReferenceEquals(item, null) || item == null || item.Pointer == IntPtr.Zero)
                        continue;

                    if (!item.IsPhysicEnabled() || !item.CanInteract())
                        continue;

                    if (!TryGetOrCreateFloorItemMeta(item, out FloorItemStaticMeta meta))
                        continue;

                    if (!meta.Go.activeInHierarchy || meta.Tr.parent != null)
                        continue;

                    if (!ReferenceEquals(meta.Grenade, null) && meta.Grenade != null && (meta.Grenade.activated || meta.Grenade.exploded))
                        continue;

                    _stagingFreeFloorItems.Add(new FreeFloorItemEntry
                    {
                        Item = item,
                        Box = meta.Box,
                        Gun = meta.Gun,
                        Pos = meta.Tr.position,
                        Kind = meta.Kind
                    });
                }

                if (end >= fCount)
                {
                    var prevActive = _freeFloorItemsSnapshot;
                    _freeFloorItemsSnapshot = _stagingFreeFloorItems;
                    _stagingFreeFloorItems = prevActive;
                    _stagingFreeFloorItems.Clear();
                    _floorScanCursor = 0;
                    _nextFreeFloorItemsSnapshotTime = now + 1.00f;
                    HitchProfiler.RecordFloorSnapshotRebuilt(fCount);
                }
                else
                {
                    _floorScanCursor = end;
                }
            }
            finally
            {
                HitchProfiler.End(HitchBucket.GoapFloorSnap, profFloorStart);
            }
        }

        internal static readonly Dictionary<IntPtr, BotScavengeCache> _botScavengeCaches = new Dictionary<IntPtr, BotScavengeCache>(128);
        internal static readonly Dictionary<IntPtr, float> _nextWeaponUpgradeAllowedTime = new Dictionary<IntPtr, float>(128);
        internal static readonly Dictionary<IntPtr, float> _nextBuddyBandageRefillTime = new Dictionary<IntPtr, float>(128);
        internal static readonly Dictionary<IntPtr, float> _nextBuddyAmmoResupplyTime = new Dictionary<IntPtr, float>(128);
        internal static readonly Dictionary<IntPtr, float> _nextBotBoxOrCrateResupplyTime = new Dictionary<IntPtr, float>(128);
        internal static readonly Dictionary<IntPtr, float> _nextSmokeGrenadeTime = new Dictionary<IntPtr, float>(128);
        private static float _nextGlobalBotWeaponPickupTime;

        private struct CachedWearableInfo
        {
            public bool Valid;
            public bool IsRadio;
            public bool IsHelmet;
        }

        private static readonly Dictionary<string, CachedWearableInfo> _cachedWearableInfo = new Dictionary<string, CachedWearableInfo>(64);

        private static bool TryGetVirtualWearableInfo(VirtualItem vi, out CachedWearableInfo info)
        {
            info = default;
            if (vi == null || vi.Pointer == IntPtr.Zero)
                return false;

            string itemId = vi.item_id;
            if (!string.IsNullOrEmpty(itemId) && _cachedWearableInfo.TryGetValue(itemId, out info))
                return info.Valid;

            if (vi.TryCast<VirtualHelmet>() != null)
            {
                info = new CachedWearableInfo { Valid = true, IsRadio = false, IsHelmet = true };
            }
            else if (vi.TryCast<VirtualClothing>() != null)
            {
                ItemObject prefab = vi.GetItemPrefab();
                ItemClothing clothing = prefab != null ? prefab.TryCast<ItemClothing>() : null;
                if (clothing != null && clothing.isRadio)
                {
                    info = new CachedWearableInfo { Valid = true, IsRadio = true, IsHelmet = false };
                }
            }

            if (!string.IsNullOrEmpty(itemId))
            {
                if (_cachedWearableInfo.Count > 256)
                    _cachedWearableInfo.Clear();
                _cachedWearableInfo[itemId] = info;
            }
            return info.Valid;
        }

        private struct CachedInventoryOwner
        {
            public Soldier SoldierOwner;
            public Vehicle VehicleOwner;
        }

        private struct CachedWeaponArchetype
        {
            public bool Valid;
            public bool IsPistol;
            public bool IsAtWeapon;
            public bool IsMg;
            public bool IsScopedOrSniper;
            public bool IsManualBoltOnly;
        }

        private static readonly Dictionary<IntPtr, CachedInventoryOwner> _cachedInventoryOwners = new Dictionary<IntPtr, CachedInventoryOwner>(256);
        private static readonly Dictionary<string, CachedWeaponArchetype> _cachedWeaponArchetypes = new Dictionary<string, CachedWeaponArchetype>(128);

        private static bool IsFloorItemFreeToPickUp(ItemObject item)
        {
            // CRITICAL: ItemObject.Awake (0x180513F41) adds EVERY ItemObject in the scene—including equipped
            // firearms/grenades/syringes in live soldiers' hands—into ItemObject.spawnedItems!
            // Vanilla InventoryManager.GetNearItems (0x1807E2468) checks ItemObject.IsPhysicEnabled() (rb != null).
            // Require IsPhysicEnabled(), CanInteract(), activeInHierarchy, and parent == null so bots NEVER steal
            // an equipped weapon or item out of a live soldier's or player's hands!
            if (ReferenceEquals(item, null) ||
                item == null ||
                item.Pointer == IntPtr.Zero ||
                !item.IsPhysicEnabled() ||
                !item.CanInteract())
            {
                return false;
            }

            if (TryGetOrCreateFloorItemMeta(item, out FloorItemStaticMeta meta))
            {
                if (!meta.Go.activeInHierarchy || meta.Tr.parent != null)
                    return false;
                if (!ReferenceEquals(meta.Grenade, null) && meta.Grenade != null && (meta.Grenade.activated || meta.Grenade.exploded))
                    return false;
                return true;
            }

            if (item.gameObject == null ||
                !item.gameObject.activeInHierarchy ||
                item.transform == null ||
                item.transform.parent != null)
            {
                return false;
            }

            // Never attempt to pick up a live/thrown armed grenade (ItemGrenade.GetInteractions @ 0x1805121B5 checks !activated)
            ItemGrenade ig = item.TryCast<ItemGrenade>();
            if (ig != null && (ig.activated || ig.exploded))
            {
                return false;
            }

            return true;
        }

        private static CachedInventoryOwner GetCachedInventoryOwner(InventoryManager inv)
        {
            IntPtr invPtr = inv.Pointer;
            if (_cachedInventoryOwners.TryGetValue(invPtr, out CachedInventoryOwner cached))
                return cached;

            if (_cachedInventoryOwners.Count > 2048)
                _cachedInventoryOwners.Clear();

            cached = new CachedInventoryOwner
            {
                SoldierOwner = inv.GetComponent<Soldier>(),
                VehicleOwner = inv.GetComponent<Vehicle>()
            };
            _cachedInventoryOwners[invPtr] = cached;
            return cached;
        }

        private static bool IsInventoryValidCorpseOrCrate(
            InventoryManager inv,
            string soldierFaction,
            bool needAmmo,
            bool needBandage,
            out Soldier deadSoldierOwner,
            out bool isDeadBodyOrDisabledCrate)
        {
            deadSoldierOwner = null;
            isDeadBodyOrDisabledCrate = false;

            if (ReferenceEquals(inv, null) || inv == null || inv.Pointer == IntPtr.Zero || inv.IsPlayersInventory())
                return false;

            // Check cached Soldier/Vehicle owner BEFORE wrapping inv.inventory / inv.inventory.items!
            // Previously touching inv.inventory.items first allocated 3 IL2CPP wrappers on all 120 LIVING soldiers!
            CachedInventoryOwner invOwner = GetCachedInventoryOwner(inv);
            Soldier owner = invOwner.SoldierOwner;
            Vehicle vehOwner = invOwner.VehicleOwner;

            // CRITICAL: In Easy Red 2 (Creature.get_IsAlive @ 0x180A55260, get_IsInjured @ 0x180A55390, get_IsDead @ 0x180A552F0),
            // an incapacitated/downed soldier waiting for a medic has IsAlive == false (life_total == 0) AND IsInjured == true,
            // while their weapons, helmet, and radio pack remain inside owner.inventory so they keep their gear when revived!
            // Never target living or downed/injured soldiers via InventoryManager (living buddy resupply uses TargetBuddyDonor).
            if (!ReferenceEquals(owner, null) && owner != null)
            {
                if (owner.Pointer == IntPtr.Zero || !owner.IsDead || owner.IsInjured || owner.IsAlive)
                    return false;
                deadSoldierOwner = owner;
            }

            bool vehIsDisabled = false;
            if (!ReferenceEquals(vehOwner, null) && vehOwner != null && vehOwner.Pointer != IntPtr.Zero)
            {
                string vf = AiCore.InternFaction(vehOwner.GetVehicleFaction());
                if (!string.IsNullOrEmpty(vf) && AiCore.AreEnemyFactions(vf, soldierFaction))
                    return false;
                vehIsDisabled = vehOwner.IsDisabled();
                if (!needAmmo && !needBandage && !vehIsDisabled)
                    return false;
            }

            if (inv.inventory == null || inv.inventory.items == null || inv.inventory.items.Count == 0)
                return false;

            isDeadBodyOrDisabledCrate = (ReferenceEquals(vehOwner, null) || vehOwner == null || vehOwner.Pointer == IntPtr.Zero)
                ? true
                : vehIsDisabled;
            return true;
        }

        private static void EnsureScavengeInventoriesSnapshot(float now)
        {
            int frame = Time.frameCount;
            if (_lastScavengeInvScanFrame == frame)
                return;

            bool midPass = _scavengeInvScanCursor > 0;
            if (!midPass && now < _nextScavengeInvSnapshotTime && _nextScavengeInvSnapshotTime - now <= 5.0f)
                return;

            if (AiCore.IsHeavyWorkFrame() && _nextScavengeInvSnapshotTime > 0f && now < _nextScavengeInvSnapshotTime + 1.2f)
                return;

            _lastScavengeInvScanFrame = frame;
            var activeList = InventoryManager.activeInventories;
            if (activeList == null)
            {
                _activeScavengeInventories.Clear();
                _stagingScavengeInventories.Clear();
                _scavengeInvScanCursor = 0;
                _nextScavengeInvSnapshotTime = now + 0.90f;
                return;
            }

            int count = activeList.Count;
            if (count == 0)
            {
                _activeScavengeInventories.Clear();
                _stagingScavengeInventories.Clear();
                _scavengeInvScanCursor = 0;
                _nextScavengeInvSnapshotTime = now + 0.90f;
                return;
            }

            if (_scavengeInvScanCursor < 0 || _scavengeInvScanCursor >= count)
            {
                _scavengeInvScanCursor = 0;
                _stagingScavengeInventories.Clear();
            }

            // Scan at most 48 inventories per frame; check dead-owner BEFORE wrapping inv.inventory!
            int end = Mathf.Min(_scavengeInvScanCursor + 48, count);
            HitchProfiler.RecordScavengeInventoriesScanned(end - _scavengeInvScanCursor);

            for (int i = _scavengeInvScanCursor; i < end; i++)
            {
                InventoryManager inv = activeList[i];
                if (ReferenceEquals(inv, null) || inv == null || inv.Pointer == IntPtr.Zero || inv.IsPlayersInventory())
                    continue;

                CachedInventoryOwner invOwner = GetCachedInventoryOwner(inv);
                Soldier owner = invOwner.SoldierOwner;
                Vehicle vehOwner = invOwner.VehicleOwner;
                Soldier deadOwner = null;
                string ownerFac = null;

                if (!ReferenceEquals(owner, null) && owner != null)
                {
                    if (owner.Pointer == IntPtr.Zero || !owner.IsDead || owner.IsInjured || owner.IsAlive)
                        continue;
                    deadOwner = owner;
                    ownerFac = AiCore.GetCachedFaction(owner);
                }

                bool isDeadOrDisabled = true;
                if (!ReferenceEquals(vehOwner, null) && vehOwner != null && vehOwner.Pointer != IntPtr.Zero)
                {
                    ownerFac = AiCore.InternFaction(vehOwner.GetVehicleFaction());
                    isDeadOrDisabled = vehOwner.IsDisabled();
                }

                if (inv.inventory == null || inv.inventory.items == null || inv.inventory.items.Count == 0)
                    continue;

                _stagingScavengeInventories.Add(new ScavengeInventoryEntry
                {
                    Inv = inv,
                    Ptr = inv.Pointer,
                    DeadSoldierOwner = deadOwner,
                    VehOwner = vehOwner,
                    OwnerFaction = ownerFac,
                    Pos = inv.InventoryPosition,
                    IsDeadBodyOrDisabledCrate = isDeadOrDisabled
                });
            }

            if (end >= count)
            {
                var prevActive = _activeScavengeInventories;
                _activeScavengeInventories = _stagingScavengeInventories;
                _stagingScavengeInventories = prevActive;
                _stagingScavengeInventories.Clear();
                _scavengeInvScanCursor = 0;
                _nextScavengeInvSnapshotTime = now + 0.90f;
            }
            else
            {
                _scavengeInvScanCursor = end;
            }
        }

        private static bool IsScavengeTargetClaimedByOtherBot(IntPtr selfPtr, IntPtr targetPtr, float now)
        {
            if (targetPtr == IntPtr.Zero || _botScavengeCaches.Count <= 1)
                return false;

            foreach (var kvp in _botScavengeCaches)
            {
                if (kvp.Key == selfPtr)
                    continue;
                BotScavengeCache other = kvp.Value;
                if (now >= other.NextScanTime)
                    continue;

                if ((other.TargetFloorItem != null && other.TargetFloorItem.Pointer == targetPtr) ||
                    (other.TargetRefillCrate != null && other.TargetRefillCrate.Pointer == targetPtr) ||
                    (other.TargetInventory != null && other.TargetInventory.Pointer == targetPtr) ||
                    (other.TargetBuddyDonor != null && other.TargetBuddyDonor.Pointer == targetPtr) ||
                    (other.TargetNeedyAllyToFeed != null && other.TargetNeedyAllyToFeed.Pointer == targetPtr))
                {
                    return true;
                }
            }
            return false;
        }

        private static void RefillSoldierPrimaryAndStowedWeapons(Soldier soldier, int minRefillQnt)
        {
            if (soldier == null)
                return;

            soldier.RefillAmmoForWearedWeapon(0, true, null, false, minRefillQnt);

            // In ER2 (Soldier.SwitchTo @ 0x18076AD20), when a soldier switches to their sidearm (Slot 2),
            // their primary weapon is swapped into Slot 2 (or Slot 1). Refill any stowed non-pistol weapon too!
            GenericGun g1 = soldier.GetHeldGun(1);
            if (g1 != null && !g1.IsPistol())
                soldier.RefillAmmoForWearedWeapon(1, true, null, false, minRefillQnt);

            GenericGun g2 = soldier.GetHeldGun(2);
            if (g2 != null && !g2.IsPistol())
            {
                soldier.RefillAmmoForWearedWeapon(2, true, null, false, minRefillQnt);
                // If currently holding a pistol in Slot 0 because the primary in Slot 2 was dry, switch back to primary!
                GenericGun g0 = soldier.GetHeldGun(0);
                if (g0 != null && g0.IsPistol() && !soldier.isInHandsAnimation && !soldier.isInBodyAnimation && soldier.CanSwitchHeldItem)
                {
                    soldier.SwitchTo(2);
                }
            }
        }

        private static bool TryResupplyFromBoxOrCrate(
            Soldier soldier,
            Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStringArray overrideContent,
            bool allowWeaponPickup,
            CachedBoxOverrideInfo ovInfo,
            bool needAmmo,
            bool needFrag,
            bool needSmoke,
            bool needAtWeapon,
            bool needBandage,
            float now,
            ref BotGoapPlan plan,
            out string lootedDesc)
        {
            lootedDesc = "supply crate";
            if (soldier == null || soldier.Pointer == IntPtr.Zero)
                return false;

            IntPtr ptr = soldier.Pointer;
            bool anyRefilled = false;

            // Bot-side cooldown keeps human player's nextAvailableUse (30s UI lockout) and stackCount untouched!
            if (ovInfo.IsDefaultWeaponAmmo || overrideContent == null || overrideContent.Length == 0)
            {
                RefillSoldierPrimaryAndStowedWeapons(soldier, 2);
                _nextBotBoxOrCrateResupplyTime[ptr] = now + 35.0f;
                lootedDesc = "ammo box/crate";
                return true;
            }

            Inventory rawInv = soldier.inventory != null ? soldier.inventory.inventory : null;
            bool canSwitchWeaponNow = allowWeaponPickup &&
                                      !soldier.IsPlayer() &&
                                      !soldier.isInHandsAnimation &&
                                      !soldier.isInBodyAnimation &&
                                      soldier.CanSwitchHeldItem &&
                                      (now >= _nextGlobalBotWeaponPickupTime || now + 5.0f < _nextGlobalBotWeaponPickupTime);

            int len = overrideContent.Length;
            for (int i = 0; i < len; i++)
            {
                string itemId = overrideContent[i];
                if (string.IsNullOrEmpty(itemId))
                    continue;

                ItemObject prefab = ItemsDatabase.GetItemObject(itemId);
                if (prefab == null || prefab.Pointer == IntPtr.Zero)
                    continue;

                GenericGun gunPrefab = prefab.TryCast<GenericGun>();
                if (gunPrefab != null)
                {
                    if (needAtWeapon &&
                        canSwitchWeaponNow &&
                        gunPrefab.IsAtWeapon() &&
                        gunPrefab.gameObject != null &&
                        (!Photon.Pun.PhotonNetwork.InRoom || AiCore.TryConsumeBotRpcBudget(0.35f)))
                    {
                        GameObject instGo = UnityEngine.Object.Instantiate(gunPrefab.gameObject);
                        if (instGo != null)
                        {
                            soldier.SetGunTriggerPressed(false);
                            soldier.PickUpItem(instGo, 1);
                            soldier.RefillAmmoForWearedWeapon(1, true, null, false, 2);
                            _nextGlobalBotWeaponPickupTime = now + 2.20f;
                            _nextWeaponUpgradeAllowedTime[ptr] = now + 20.0f;
                            plan.NextReplanTime = Mathf.Max(plan.NextReplanTime, now + 2.50f);
                            needAtWeapon = false;
                            canSwitchWeaponNow = false;
                            anyRefilled = true;
                            lootedDesc = "AT launcher from supply crate";
                        }
                    }
                    continue;
                }

                if (rawInv == null)
                    continue;

                VirtualItem vi = prefab.ToVirtualItem();
                if (vi == null)
                    continue;

                if (needAtWeapon &&
                    vi.TryCast<VirtualATGrenade>() != null &&
                    soldier.inventory.FindItemOfType<VirtualATGrenade>() == null)
                {
                    if (rawInv.AddVirtualItem(vi))
                    {
                        needAtWeapon = false;
                        anyRefilled = true;
                        lootedDesc = "AT grenade from supply box/crate";
                    }
                }
                else if (needFrag &&
                         vi.TryCast<VirtualGrenade>() != null &&
                         soldier.inventory.FindItemOfType<VirtualGrenade>() == null)
                {
                    if (rawInv.AddVirtualItem(vi))
                    {
                        needFrag = false;
                        anyRefilled = true;
                        if (lootedDesc == "supply crate")
                            lootedDesc = "frag grenade from supply box/crate";
                    }
                }
                else if (needSmoke &&
                         vi.TryCast<VirtualSmokeGrenade>() != null &&
                         soldier.inventory.FindItemOfType<VirtualSmokeGrenade>() == null)
                {
                    if (rawInv.AddVirtualItem(vi))
                    {
                        needSmoke = false;
                        anyRefilled = true;
                        if (lootedDesc == "supply crate")
                            lootedDesc = "smoke grenade from supply box/crate";
                    }
                }
                else if (needBandage &&
                         vi.TryCast<VirtualBandages>() != null &&
                         soldier.inventory.FindItemOfType<VirtualBandages>() == null)
                {
                    if (rawInv.AddVirtualItem(vi))
                    {
                        needBandage = false;
                        anyRefilled = true;
                        if (lootedDesc == "supply crate")
                            lootedDesc = "bandage from supply box/crate";
                    }
                }
            }

            if (needAmmo && ovInfo.HasAmmo)
            {
                RefillSoldierPrimaryAndStowedWeapons(soldier, 2);
                anyRefilled = true;
                if (lootedDesc == "supply crate")
                    lootedDesc = "ammo from supply box/crate";
            }

            if (anyRefilled)
            {
                _nextBotBoxOrCrateResupplyTime[ptr] = now + 35.0f;
            }
            return anyRefilled;
        }

        private static bool TryGetVirtualWeaponArchetype(VirtualItem vw, out CachedWeaponArchetype arch)
        {
            arch = default;
            if (vw == null || vw.Pointer == IntPtr.Zero)
                return false;

            string itemId = vw.item_id;
            if (!string.IsNullOrEmpty(itemId) && _cachedWeaponArchetypes.TryGetValue(itemId, out arch))
                return arch.Valid;

            ItemObject prefab = vw.GetItemPrefab();
            GenericGun gun = prefab != null ? prefab.TryCast<GenericGun>() : null;
            if (gun != null)
            {
                arch = new CachedWeaponArchetype
                {
                    Valid = true,
                    IsPistol = gun.IsPistol(),
                    IsAtWeapon = gun.IsAtWeapon(),
                    IsMg = gun.integratedBipod != null || gun.IsHeavyMg(),
                    IsScopedOrSniper = gun.IsSniperRifle() || gun.IsUsingScope || gun.HasScopeWithMagnification(),
                    IsManualBoltOnly = gun.isManualBoltOperated && !gun.IsFullAuto()
                };
            }

            if (!string.IsNullOrEmpty(itemId))
            {
                if (_cachedWeaponArchetypes.Count > 512)
                    _cachedWeaponArchetypes.Clear();
                _cachedWeaponArchetypes[itemId] = arch;
            }
            return arch.Valid;
        }

        internal struct SquadBlackboard
        {
            public float NextRefreshTime;
            public bool IsConnectedToRadioNet;
            public bool PreviouslyHadAliveRadioman;
            public bool HasActiveThreat;
            public bool ThreatIsArmor;
            public bool HasAliveMachineGunner;
            public bool HasAliveMedic;
            public bool SquadHasAntiTank;
            public int AliveMemberCount;
            public int NearbyEnemyCount42m;
            public int NearbyFriendlyCount42m;
            public int FriendlyUnitsInsideObjective;
            public int EnemyUnitsInsideObjective;
            public Vector3 SquadCenter;
            public Vector3 ThreatPosition;
            public Vector3 ThreatDirection;
            public Spottable PrimaryTarget;

            // Macro Objective & Ticket / Attrition Context
            public bool IsInvaderFaction;
            public float TicketRatio01;
            public bool HasActiveObjectiveTask;
            public bool IsDefendingObjective;
            public Vector3 ObjectivePosition;
            public float ObjectiveCaptureProgress;

            // Tier 3 Team Net Directive assigned to this Squad Leader (plus HQ Advisory Suggestion)
            public SquadNetDirective NetDirective;
            public float NetDirectiveExpireTime;
            public Vector3 NetDirectiveTargetPos;

            public SquadNetDirective SuggestedNetDirective;
            public float SuggestedDirectiveExpireTime;
            public Vector3 SuggestedDirectiveTargetPos;
            public Vector3 SuggestedPincerFlankDir;
            public IntPtr SuggestedPincerTaskPtr;

            // Zero-Raycast Casualty / Fatal-Funnel Hazard Memory
            public Vector3 LastCasualtyPos;
            public float LastCasualtyTime;
            public float DecayedRecentDeaths;
            public float LastDeathDecayUpdateTime;

            // Tier 2 Coordinated Squad Role Slots
            public IntPtr Suppressor0Ptr;
            public IntPtr Suppressor1Ptr;
            public float SuppressHoldUntil;

            public IntPtr FlankerLeftPtr;
            public IntPtr FlankerRightPtr;
            public float FlankUntil;
            public float NextFlankCoverScanTime;
            public CombatCover FlankLeftCover;
            public CombatCover FlankRightCover;

            public IntPtr BreacherPtr;
            public float BreachUntil;

            public float SmokeActiveUntil;
            public Vector3 LastSmokeTargetPos;

            public Soldier SurrenderedComrade;
            public IntPtr RescuerPtr;
            public float RescueUntil;

            public IntPtr DragRescuerPtr;
            public IntPtr DragPatientPtr;
            public float DragClaimUntil;

            public bool IsThreatBeingPinned(float now)
            {
                return now < SuppressHoldUntil && (Suppressor0Ptr != IntPtr.Zero || Suppressor1Ptr != IntPtr.Zero);
            }
        }

        internal static readonly Dictionary<IntPtr, float> _nextSquadVoiceTime = new Dictionary<IntPtr, float>(64);
        internal static readonly Dictionary<IntPtr, float> _nextSoldierVoiceTime = new Dictionary<IntPtr, float>(256);
        internal static readonly Dictionary<IntPtr, float> _nextFlankCoverAssignTime = new Dictionary<IntPtr, float>(256);
        internal static readonly Dictionary<IntPtr, float> _medicHealStartTime = new Dictionary<IntPtr, float>(64);
        internal static readonly Dictionary<IntPtr, IntPtr> _medicHealTargetPtr = new Dictionary<IntPtr, IntPtr>(64);
        internal static readonly Dictionary<IntPtr, IntPtr> _pincerOrderedTaskBySquad = new Dictionary<IntPtr, IntPtr>(64);
        private static readonly Dictionary<IntPtr, Squad> _reusablePrimarySquadByTask = new Dictionary<IntPtr, Squad>(8);
        private static readonly Dictionary<IntPtr, int> _reusablePincerCountByTask = new Dictionary<IntPtr, int>(8);

        internal struct BotGoapPlan
        {
            public GoapStateBits LastGoalBit;
            public GoapActionId Step0;
            public GoapActionId Step1;
            public byte StepIndex;
            public byte StepCount;
            public float PlanExpireTime;
            public float NextReplanTime;
            public SoldierPose DesiredPose;
            public bool LockUnitCoverChange;
            public bool HasMoveOverride;
            public bool IsEvacuatingBailedCrew;
            public Vector3 MoveOverrideDest;
        }

        internal static bool TryGetBotGoapSummary(
            Soldier soldier,
            out SquadNetDirective netDir,
            out GoapStateBits goalBit,
            out GoapActionId step0,
            out GoapActionId step1,
            out byte stepIdx,
            out bool isEvacCrew)
        {
            netDir = SquadNetDirective.Disconnected_Autonomous;
            goalBit = GoapStateBits.None;
            step0 = GoapActionId.None;
            step1 = GoapActionId.None;
            stepIdx = 0;
            isEvacCrew = false;

            if (ReferenceEquals(soldier, null) || soldier == null || soldier.Pointer == IntPtr.Zero)
                return false;

            Squad sq = soldier.joinedSquad;
            if (sq != null && sq.Pointer != IntPtr.Zero && _squadBlackboards.TryGetValue(sq.Pointer, out SquadBlackboard b))
            {
                netDir = b.NetDirective;
            }

            if (_botGoapPlans.TryGetValue(soldier.Pointer, out BotGoapPlan p) && Time.time < p.PlanExpireTime)
            {
                goalBit = p.LastGoalBit;
                step0 = p.Step0;
                step1 = p.Step1;
                stepIdx = p.StepIndex;
                isEvacCrew = p.IsEvacuatingBailedCrew;
                return true;
            }
            return false;
        }

        internal static string GetNetDirectiveShortName(SquadNetDirective d)
        {
            switch (d)
            {
                case SquadNetDirective.Connected_StandardObjective: return "Connected";
                case SquadNetDirective.Anvil_PinAndCallSupport: return "AnvilPin";
                case SquadNetDirective.Hammer_WideFlankEnvelopment: return "HammerFlank";
                case SquadNetDirective.Regroup_SectorWithdrawal: return "SectorWithdraw";
                default: return "Autonomous";
            }
        }

        internal static string GetGoapGoalShortName(GoapStateBits g)
        {
            if ((g & GoapStateBits.SurvivedCrisis) != 0) return "SurviveCrisis";
            if ((g & GoapStateBits.RadioLinkPreserved) != 0) return "PreserveRadio";
            if ((g & GoapStateBits.TargetPinned) != 0) return "PinTarget";
            if ((g & GoapStateBits.TargetEliminated) != 0) return "EliminateTarget";
            if ((g & GoapStateBits.SquadSupported) != 0) return "SupportSquad";
            return "None";
        }

        internal static string GetGoapActionShortName(GoapActionId a)
        {
            switch (a)
            {
                case GoapActionId.SeekEmergencyCover: return "SeekCover";
                case GoapActionId.BandageInCover: return "BandageInCover";
                case GoapActionId.RepositionBehindWall: return "ShelterWall";
                case GoapActionId.MaintainRadioLinkWithLeader: return "KeepRadioLink";
                case GoapActionId.ThrowSmokeScreen: return "ThrowSmoke";
                case GoapActionId.SuppressThreatPosition: return "SuppressThreat";
                case GoapActionId.MoveToFlankCover: return "FlankCover";
                case GoapActionId.FlushThreatWithFrag: return "FlushFrag";
                case GoapActionId.HoldOverwatchFire: return "HoldOverwatch";
                case GoapActionId.BoundWithOverwatch: return "BoundForward";
                case GoapActionId.PointBlankBayonetBreach: return "PointBlankCQB";
                case GoapActionId.RescueSurrenderedComrade: return "LiberateComrade";
                case GoapActionId.LoneWolfLinkUpOrAmbush: return "LoneLinkOrEvac";
                case GoapActionId.DirectFireEngagement: return "DirectFire";
                case GoapActionId.ScavengeSupplyFromCorpse: return "ScavengeSupply";
                case GoapActionId.TacticalFallbackRetreat: return "FallbackRetreat";
                default: return "None";
            }
        }

        internal struct BotMicroVariance
        {
            public float AggressionCostMult;      // 0.85f .. 1.15f (lower = bolder flanking, grenade flushing, smoke pushes, CQB breaching)
            public float CautionCostMult;         // 0.85f .. 1.15f (lower = faster emergency cover seeking, wall shelter, suppression pinning, radio discipline)
            public float ReactionJitterSeconds;   // 0.00f .. 0.38f (desynchronizes squadmate replan intervals so bots never move in robotic lockstep)
            public float SuppressionJitter;       // -2.2f .. +2.2f (individual nerve variance on suppression thresholds)

            public static BotMicroVariance FromSoldier(IntPtr soldierPtr)
            {
                // Zero-allocation deterministic bit-mixing via MurmurHash3 (AiCore.GetDeterministicHash)
                int h0 = AiCore.GetDeterministicHash(soldierPtr, 901);
                int h1 = AiCore.GetDeterministicHash(soldierPtr, 902);

                float u0 = (h0 & 0xFFFF) * (1f / 65535f);
                float u1 = ((h0 >> 16) & 0x7FFF) * (1f / 32767f);
                float u2 = (h1 & 0xFFFF) * (1f / 65535f);
                float u3 = ((h1 >> 16) & 0x7FFF) * (1f / 32767f);

                return new BotMicroVariance
                {
                    AggressionCostMult = 0.85f + u0 * 0.30f,
                    CautionCostMult = 0.85f + u1 * 0.30f,
                    ReactionJitterSeconds = u2 * 0.38f,
                    SuppressionJitter = (u3 - 0.5f) * 4.4f
                };
            }
        }

        internal static readonly Dictionary<IntPtr, SquadBlackboard> _squadBlackboards = new Dictionary<IntPtr, SquadBlackboard>(64);
        internal static readonly Dictionary<IntPtr, BotGoapPlan> _botGoapPlans = new Dictionary<IntPtr, BotGoapPlan>(256);
        internal static readonly HashSet<IntPtr> _initialHqSpawnBriefingConsumed = new HashSet<IntPtr>();
        internal static readonly Dictionary<string, FactionCommandProfile> _factionProfileCache = new Dictionary<string, FactionCommandProfile>(32);

        internal struct FactionCommandProfile
        {
            public float RadioRelayRadius;            // Default 45m (USA: 55m, UK/Canada: 50m, USSR/France: 38m, Mongolia/China: 42m)
            public bool AllowDisconnectedLocalFlank;  // True for decentralized doctrines (Germany, Finland, USA, Mongolia, Australia, NZ, Gurkha, Poland, Greece, China, Japan)
            public bool PreferAmbushWhenAlone;        // True for Japan, Finland, Gurkha, China (hold local ambush cover when alone vs retreating to merge)
            public float FlankLateralBias;            // 0.78f (tight) to 0.90f (wide encirclement like Finland/Mongolia/Japan; default 0.83f)
            public float GarrisonSquadRatio;          // Default 0.22f (UK/France/Italy: 0.28f; USSR/Japan/Mongolia: 0.16f)
            public float EmplacementSearchMult;       // Default 1.0f (Italy/France/Romania/Hungary/Greece/India: 1.18f-1.30f)
            public float SuppressionThresholdBase;    // Default 18f (USA/Manchukuo: 15f; Italy: 16f; Germany: 19f; Poland/NZ: 22f; Finland: 23f; Gurkha: 24f; Japan: 25f)
            public float SuppressCostMult;            // Germany: 0.72f, UK/India: 0.80f, France: 0.82f
            public float SmokeCostMult;               // USA: 0.70f, Canada/UK: 0.75f, Japan: 0.76f
            public float FlankMoveCostMult;           // Finland: 0.72f, Mongolia: 0.74f, USSR/Australia/Canada: 0.76f, Germany/NZ: 0.78f, USA: 0.82f
            public float FragFlushCostMult;           // China: 0.74f, USSR/Canada: 0.78f, USA_Afrikan: 0.80f, Hungary: 0.84f
            public float OverwatchHoldCostMult;       // UK: 0.78f, Germany/USA/India: 0.82f, Italy/France: 0.84f
            public float BayonetBreachCostMult;       // Japan: 0.62f, Gurkha: 0.65f, NZ: 0.75f, Greece: 0.80f, Australia: 0.82f
            public float EmergencyCoverCostMult;      // USA: 0.82f, Manchukuo: 0.78f, China: 0.84f, Italy: 0.85f
            public float ScavengeAndResupplyMult;     // Finland: 0.72f, China: 0.74f, Poland: 0.76f, Gurkha/Greece: 0.78f, Germany/USSR: 0.80f, USA/Aus/NZ: 0.82f
            public float ComradeAidAndDragMult;       // USA/USA_Afrikan: 0.74f, Aus/NZ: 0.75f, Finland: 0.76f, Canada/Poland/Gurkha: 0.78f; USSR: 1.08f, Manchukuo: 1.18f, Japan: 1.22f
            public float AttritionPivotThreshold;     // Decayed casualties on a stalled (<20%) objective before Team Net pivots squads to a softer objective (Finland: 5.5, USA: 6.0, Ger/Can/Aus/NZ: 6.5, Default: 7.5, USSR: 10.5, Japan: 12.0)

            public static readonly FactionCommandProfile Default = new FactionCommandProfile
            {
                RadioRelayRadius = 45f,
                AllowDisconnectedLocalFlank = false,
                PreferAmbushWhenAlone = false,
                FlankLateralBias = 0.83f,
                GarrisonSquadRatio = 0.22f,
                EmplacementSearchMult = 1.0f,
                SuppressionThresholdBase = 18f,
                SuppressCostMult = 1.0f,
                SmokeCostMult = 1.0f,
                FlankMoveCostMult = 1.0f,
                FragFlushCostMult = 1.0f,
                OverwatchHoldCostMult = 1.0f,
                BayonetBreachCostMult = 1.0f,
                EmergencyCoverCostMult = 1.0f,
                ScavengeAndResupplyMult = 1.0f,
                ComradeAidAndDragMult = 1.0f,
                AttritionPivotThreshold = 7.5f
            };
        }

        internal static FactionCommandProfile GetFactionCommandProfile(string faction)
        {
            if (!VisionConfig.EnableFactionNetDoctrines.Value || string.IsNullOrEmpty(faction))
                return FactionCommandProfile.Default;

            if (_factionProfileCache.TryGetValue(faction, out FactionCommandProfile cached))
                return cached;

            FactionCommandProfile p = FactionCommandProfile.Default;
            string f = faction;

            // Normalize Workshop/sub-faction prefixes (e.g. "Germany_Winter") to canonical switch keys
            if (f.StartsWith("UnitedStates_Afrikan", StringComparison.OrdinalIgnoreCase)) f = "UnitedStates_Afrikan";
            else if (f.StartsWith("UnitedStates", StringComparison.OrdinalIgnoreCase) || f.StartsWith("USA", StringComparison.OrdinalIgnoreCase)) f = "UnitedStates";
            else if (f.StartsWith("Germany", StringComparison.OrdinalIgnoreCase)) f = "Germany";
            else if (f.StartsWith("Finland", StringComparison.OrdinalIgnoreCase)) f = "Finland";
            else if (f.StartsWith("England", StringComparison.OrdinalIgnoreCase) || f.StartsWith("UK", StringComparison.OrdinalIgnoreCase)) f = "England";
            else if (f.StartsWith("Canada", StringComparison.OrdinalIgnoreCase)) f = "Canada";
            else if (f.StartsWith("Australia", StringComparison.OrdinalIgnoreCase)) f = "Australia";
            else if (f.StartsWith("NewZealand", StringComparison.OrdinalIgnoreCase)) f = "NewZealand";
            else if (f.StartsWith("India", StringComparison.OrdinalIgnoreCase)) f = "India";
            else if (f.StartsWith("Gurkha", StringComparison.OrdinalIgnoreCase)) f = "Gurkha";
            else if (f.StartsWith("France", StringComparison.OrdinalIgnoreCase)) f = "France";
            else if (f.StartsWith("Poland", StringComparison.OrdinalIgnoreCase)) f = "Poland";
            else if (f.StartsWith("Greece", StringComparison.OrdinalIgnoreCase)) f = "Greece";
            else if (f.StartsWith("Ussr", StringComparison.OrdinalIgnoreCase)) f = "Ussr";
            else if (f.StartsWith("Mongolia", StringComparison.OrdinalIgnoreCase)) f = "Mongolia";
            else if (f.StartsWith("China", StringComparison.OrdinalIgnoreCase)) f = "China";
            else if (f.StartsWith("Japan", StringComparison.OrdinalIgnoreCase)) f = "Japan";
            else if (f.StartsWith("Manchukuo", StringComparison.OrdinalIgnoreCase)) f = "Manchukuo";
            else if (f.StartsWith("Italy", StringComparison.OrdinalIgnoreCase)) f = "Italy";
            else if (f.StartsWith("Romania", StringComparison.OrdinalIgnoreCase)) f = "Romania";
            else if (f.StartsWith("Hungary", StringComparison.OrdinalIgnoreCase)) f = "Hungary";

            switch (f)
            {
                case "Germany":
                    // Wehrmacht / Fallschirmjager: Auftragstaktik (local initiative when cut off) + MG34/42 Base-of-Fire + Beutewaffen scavenging
                    p.RadioRelayRadius = 48f;
                    p.AllowDisconnectedLocalFlank = true;
                    p.FlankLateralBias = 0.82f;
                    p.SuppressionThresholdBase = 19.5f;
                    p.SuppressCostMult = 0.72f;
                    p.FlankMoveCostMult = 0.78f;
                    p.OverwatchHoldCostMult = 0.82f;
                    p.ScavengeAndResupplyMult = 0.80f;
                    p.ComradeAidAndDragMult = 0.86f;
                    p.AttritionPivotThreshold = 6.5f;
                    break;

                case "Finland":
                    // Finnish Army: Motti wide encirclement, autonomous forest ambush, master field scavengers & "Kaveria ei jateta" comrade rescue
                    p.RadioRelayRadius = 42f;
                    p.AllowDisconnectedLocalFlank = true;
                    p.PreferAmbushWhenAlone = true;
                    p.FlankLateralBias = 0.90f;
                    p.GarrisonSquadRatio = 0.18f;
                    p.SuppressionThresholdBase = 23f;
                    p.FlankMoveCostMult = 0.72f;
                    p.FragFlushCostMult = 0.82f;
                    p.ScavengeAndResupplyMult = 0.72f;
                    p.ComradeAidAndDragMult = 0.76f;
                    p.AttritionPivotThreshold = 5.5f;
                    break;

                case "UnitedStates_Afrikan":
                    // US 92nd Infantry (Italy): strong radio net + close-range flanking, grenade grit, and high buddy aid
                    p.RadioRelayRadius = 52f;
                    p.AllowDisconnectedLocalFlank = true;
                    p.SuppressionThresholdBase = 20f;
                    p.SmokeCostMult = 0.75f;
                    p.FlankMoveCostMult = 0.80f;
                    p.FragFlushCostMult = 0.80f;
                    p.OverwatchHoldCostMult = 0.84f;
                    p.ScavengeAndResupplyMult = 0.82f;
                    p.ComradeAidAndDragMult = 0.74f;
                    p.AttritionPivotThreshold = 6.0f;
                    break;

                case "UnitedStates":
                    // US Army / Marines: Superior radio net, NCO initiative, smoke screens, buddy resupply & casualty care
                    p.RadioRelayRadius = 55f;
                    p.AllowDisconnectedLocalFlank = true;
                    p.SuppressionThresholdBase = 15.5f;
                    p.SmokeCostMult = 0.70f;
                    p.OverwatchHoldCostMult = 0.82f;
                    p.FlankMoveCostMult = 0.82f;
                    p.EmergencyCoverCostMult = 0.82f;
                    p.ScavengeAndResupplyMult = 0.82f;
                    p.ComradeAidAndDragMult = 0.74f;
                    p.AttritionPivotThreshold = 6.0f;
                    break;

                case "England":
                    // British Army: Methodical "Bite & Hold" set-piece overwatch, smoke, strong garrisoning, and disciplined casualty aid
                    p.RadioRelayRadius = 50f;
                    p.FlankLateralBias = 0.80f;
                    p.GarrisonSquadRatio = 0.28f;
                    p.EmplacementSearchMult = 1.15f;
                    p.SuppressCostMult = 0.80f;
                    p.SmokeCostMult = 0.76f;
                    p.OverwatchHoldCostMult = 0.78f;
                    p.ScavengeAndResupplyMult = 0.86f;
                    p.ComradeAidAndDragMult = 0.82f;
                    p.AttritionPivotThreshold = 7.0f;
                    break;

                case "Canada":
                    // Canadian Army (Juno / Ortona / Scheldt): Commonwealth radio net + aggressive urban/beach shock breaching & comrade rescue
                    p.RadioRelayRadius = 48f;
                    p.AllowDisconnectedLocalFlank = true;
                    p.SuppressionThresholdBase = 20f;
                    p.SmokeCostMult = 0.75f;
                    p.FlankMoveCostMult = 0.76f;
                    p.FragFlushCostMult = 0.78f;
                    p.ScavengeAndResupplyMult = 0.84f;
                    p.ComradeAidAndDragMult = 0.78f;
                    p.AttritionPivotThreshold = 6.5f;
                    break;

                case "Australia":
                    // Australian AIF (Tobruk / Kokoda): Autonomous bush/desert patrol flanking, close-quarters grit, and "mateship" casualty care
                    p.RadioRelayRadius = 46f;
                    p.AllowDisconnectedLocalFlank = true;
                    p.FlankLateralBias = 0.86f;
                    p.SuppressionThresholdBase = 21.5f;
                    p.FlankMoveCostMult = 0.76f;
                    p.BayonetBreachCostMult = 0.82f;
                    p.ScavengeAndResupplyMult = 0.82f;
                    p.ComradeAidAndDragMult = 0.75f;
                    p.AttritionPivotThreshold = 6.5f;
                    break;

                case "NewZealand":
                    // 2nd NZ Division / Maori Battalion (Crete / Cassino): Fierce close-assault, counter-attack shock & strong mate recovery
                    p.RadioRelayRadius = 46f;
                    p.AllowDisconnectedLocalFlank = true;
                    p.FlankLateralBias = 0.85f;
                    p.SuppressionThresholdBase = 22f;
                    p.FlankMoveCostMult = 0.78f;
                    p.FragFlushCostMult = 0.82f;
                    p.BayonetBreachCostMult = 0.75f;
                    p.ScavengeAndResupplyMult = 0.82f;
                    p.ComradeAidAndDragMult = 0.75f;
                    p.AttritionPivotThreshold = 6.5f;
                    break;

                case "India":
                    // Indian Army: Disciplined mountain/desert overwatch, proactive emplacement defense & regimental casualty care
                    p.RadioRelayRadius = 46f;
                    p.GarrisonSquadRatio = 0.25f;
                    p.EmplacementSearchMult = 1.18f;
                    p.SuppressCostMult = 0.80f;
                    p.OverwatchHoldCostMult = 0.82f;
                    p.ScavengeAndResupplyMult = 0.88f;
                    p.ComradeAidAndDragMult = 0.82f;
                    p.AttritionPivotThreshold = 7.0f;
                    break;

                case "Gurkha":
                    // Gurkhas: Fearless close-quarters Kukri/melee shock, wide infiltration, field scavenging & fierce unit brotherhood
                    p.RadioRelayRadius = 44f;
                    p.AllowDisconnectedLocalFlank = true;
                    p.PreferAmbushWhenAlone = true;
                    p.FlankLateralBias = 0.88f;
                    p.SuppressionThresholdBase = 24f;
                    p.FlankMoveCostMult = 0.75f;
                    p.BayonetBreachCostMult = 0.65f;
                    p.ScavengeAndResupplyMult = 0.78f;
                    p.ComradeAidAndDragMult = 0.78f;
                    p.AttritionPivotThreshold = 8.5f;
                    break;

                case "France":
                    // 1940 French Army: Bataille Conduite (Methodical top-down battle, strong emplacements, urgent radio recovery if cut)
                    p.RadioRelayRadius = 38f;
                    p.AllowDisconnectedLocalFlank = false;
                    p.FlankLateralBias = 0.78f;
                    p.GarrisonSquadRatio = 0.28f;
                    p.EmplacementSearchMult = 1.25f;
                    p.SuppressCostMult = 0.82f;
                    p.OverwatchHoldCostMult = 0.84f;
                    p.ScavengeAndResupplyMult = 0.94f;
                    p.ComradeAidAndDragMult = 0.88f;
                    break;

                case "Poland":
                    // Polish Army: Tenacious mobile defense, high cohesion when cut off, aggressive weapon scavenging & comrade liberation
                    p.RadioRelayRadius = 42f;
                    p.AllowDisconnectedLocalFlank = true;
                    p.EmplacementSearchMult = 1.15f;
                    p.SuppressionThresholdBase = 22f;
                    p.FlankMoveCostMult = 0.82f;
                    p.FragFlushCostMult = 0.84f;
                    p.ScavengeAndResupplyMult = 0.76f;
                    p.ComradeAidAndDragMult = 0.78f;
                    p.AttritionPivotThreshold = 7.0f;
                    break;

                case "Greece":
                    // Greek Army: Mountain fortification defense, counter-charge grit, captured supply scavenging & comrade aid
                    p.RadioRelayRadius = 40f;
                    p.AllowDisconnectedLocalFlank = true;
                    p.GarrisonSquadRatio = 0.26f;
                    p.EmplacementSearchMult = 1.22f;
                    p.SuppressionThresholdBase = 21f;
                    p.BayonetBreachCostMult = 0.80f;
                    p.ScavengeAndResupplyMult = 0.78f;
                    p.ComradeAidAndDragMult = 0.80f;
                    break;

                case "Ussr":
                    // Red Army: Deep Battle tank-infantry pincers & close SMG/grenade shock, active battlefield re-arming, relentless assault wave
                    p.RadioRelayRadius = 38f;
                    p.AllowDisconnectedLocalFlank = false;
                    p.FlankLateralBias = 0.86f;
                    p.GarrisonSquadRatio = 0.16f;
                    p.SuppressionThresholdBase = 20.5f;
                    p.FlankMoveCostMult = 0.76f;
                    p.FragFlushCostMult = 0.78f;
                    p.BayonetBreachCostMult = 0.84f;
                    p.ScavengeAndResupplyMult = 0.80f;
                    p.ComradeAidAndDragMult = 1.08f;
                    p.AttritionPivotThreshold = 10.5f;
                    break;

                case "Mongolia":
                    // Mongolian People's Army (Khalkhin Gol): Wide steppe mobility, autonomous recon flanking without radios
                    p.RadioRelayRadius = 40f;
                    p.AllowDisconnectedLocalFlank = true;
                    p.FlankLateralBias = 0.89f;
                    p.GarrisonSquadRatio = 0.16f;
                    p.SuppressionThresholdBase = 19.5f;
                    p.FlankMoveCostMult = 0.74f;
                    p.OverwatchHoldCostMult = 0.85f;
                    p.ScavengeAndResupplyMult = 0.84f;
                    p.ComradeAidAndDragMult = 0.90f;
                    p.AttritionPivotThreshold = 7.0f;
                    break;

                case "China":
                    // National Revolutionary Army (Shanghai / Nanking): Low radio reliance, close grenade ambushes, aggressive battlefield scavenging
                    p.RadioRelayRadius = 38f;
                    p.AllowDisconnectedLocalFlank = true;
                    p.PreferAmbushWhenAlone = true;
                    p.FlankLateralBias = 0.85f;
                    p.SuppressionThresholdBase = 21f;
                    p.FragFlushCostMult = 0.74f;
                    p.BayonetBreachCostMult = 0.78f;
                    p.EmergencyCoverCostMult = 0.84f;
                    p.ScavengeAndResupplyMult = 0.74f;
                    p.ComradeAidAndDragMult = 0.92f;
                    p.AttritionPivotThreshold = 9.5f;
                    break;

                case "Japan":
                    // Imperial Japanese Army / SNLF: Wide infiltration, smoke + bayonet shock, lone ambush to the last man, relentless assault over casualty dragging
                    p.RadioRelayRadius = 42f;
                    p.AllowDisconnectedLocalFlank = true;
                    p.PreferAmbushWhenAlone = true;
                    p.FlankLateralBias = 0.88f;
                    p.GarrisonSquadRatio = 0.16f;
                    p.SuppressionThresholdBase = 25f;
                    p.SmokeCostMult = 0.76f;
                    p.FlankMoveCostMult = 0.78f;
                    p.BayonetBreachCostMult = 0.62f;
                    p.ScavengeAndResupplyMult = 0.84f;
                    p.ComradeAidAndDragMult = 1.22f;
                    p.AttritionPivotThreshold = 12.0f;
                    break;

                case "Manchukuo":
                    // Manchukuo Imperial Army (Khalkhin Gol): Conscript auxiliaries dependent on officers/radio, cautious under fire
                    p.RadioRelayRadius = 36f;
                    p.AllowDisconnectedLocalFlank = false;
                    p.PreferAmbushWhenAlone = false;
                    p.GarrisonSquadRatio = 0.26f;
                    p.SuppressionThresholdBase = 14.5f;
                    p.EmergencyCoverCostMult = 0.78f;
                    p.OverwatchHoldCostMult = 0.88f;
                    p.ScavengeAndResupplyMult = 0.95f;
                    p.ComradeAidAndDragMult = 1.18f;
                    p.AttritionPivotThreshold = 6.5f;
                    break;

                case "Italy":
                    // Regio Esercito: Strongest emplacement/artillery staffing & prepared defense, officer-dependent when isolated
                    p.RadioRelayRadius = 40f;
                    p.AllowDisconnectedLocalFlank = false;
                    p.GarrisonSquadRatio = 0.28f;
                    p.EmplacementSearchMult = 1.30f;
                    p.SuppressionThresholdBase = 16f;
                    p.OverwatchHoldCostMult = 0.84f;
                    p.EmergencyCoverCostMult = 0.85f;
                    p.ScavengeAndResupplyMult = 0.92f;
                    p.ComradeAidAndDragMult = 0.90f;
                    p.AttritionPivotThreshold = 7.0f;
                    break;

                case "Romania":
                    // Royal Romanian Army: Strong static AT/artillery gun staffing and defensive overwatch
                    p.RadioRelayRadius = 40f;
                    p.AllowDisconnectedLocalFlank = false;
                    p.GarrisonSquadRatio = 0.25f;
                    p.EmplacementSearchMult = 1.24f;
                    p.SuppressionThresholdBase = 16.5f;
                    p.SuppressCostMult = 0.84f;
                    p.EmergencyCoverCostMult = 0.86f;
                    p.ScavengeAndResupplyMult = 0.90f;
                    p.ComradeAidAndDragMult = 0.92f;
                    break;

                case "Hungary":
                    // Royal Hungarian Honvedseg: Tenacious emplacement defense + close grenade counter-punches
                    p.RadioRelayRadius = 42f;
                    p.AllowDisconnectedLocalFlank = false;
                    p.GarrisonSquadRatio = 0.24f;
                    p.EmplacementSearchMult = 1.20f;
                    p.SuppressionThresholdBase = 18.5f;
                    p.SuppressCostMult = 0.84f;
                    p.FragFlushCostMult = 0.84f;
                    p.ScavengeAndResupplyMult = 0.86f;
                    p.ComradeAidAndDragMult = 0.88f;
                    break;
            }

            _factionProfileCache[faction] = p;
            return p;
        }

        public static int GoapPlansExecutedCount { get; private set; }
        public static int GoapPinAndFlankCount { get; private set; }
        public static int GoapSmokeScreensCount { get; private set; }
        public static int RadioNetCutoffsCount { get; private set; }
        public static int TeamNetAnvilHammerCount { get; private set; }
        public static int RoleWeaponUpgradesCount { get; private set; }
        public static int BuddyResuppliesCount { get; private set; }
        public static int FieldRadiosInheritedCount { get; private set; }
        public static int HelmetsRecoveredCount { get; private set; }
        public static int CasualtiesDraggedCount { get; private set; }
        public static int BotKillCalloutsCount { get; set; }
        public static int SquadChargesOrderedCount { get; private set; }
        public static int SquadCovertOrdersCount { get; private set; }
        public static int SquadFollowOrdersCount { get; private set; }
        public static int TacticalRetreatsCount { get; private set; }
        public static int SquadLeaderBalksCount { get; private set; }
        public static int IndividualSelfPreservationBalksCount { get; private set; }

        internal static readonly HashSet<IntPtr> _aiCovertHoldFireSquads = new HashSet<IntPtr>();
        internal static readonly HashSet<IntPtr> _aiOrderedChargeSquads = new HashSet<IntPtr>();
        internal static readonly Dictionary<IntPtr, float> _aiChargeBaselineDeaths = new Dictionary<IntPtr, float>(32);
        internal static readonly Dictionary<IntPtr, float> _aiCovertHoldFireExpireTime = new Dictionary<IntPtr, float>(32);
        internal static readonly Dictionary<IntPtr, float> _aiFollowColumnUntil = new Dictionary<IntPtr, float>(32);
        internal static readonly Dictionary<IntPtr, float> _nextAiSquadChargeTime = new Dictionary<IntPtr, float>(64);
        internal static readonly Dictionary<IntPtr, float> _nextAiCovertOrderTime = new Dictionary<IntPtr, float>(64);
        internal static readonly Dictionary<IntPtr, float> _nextAiFollowOrderTime = new Dictionary<IntPtr, float>(64);
        internal static readonly Dictionary<IntPtr, float> _nextAiSquadRetreatTime = new Dictionary<IntPtr, float>(64);
        internal static readonly Dictionary<IntPtr, float> _aiSquadRetreatUntil = new Dictionary<IntPtr, float>(32);
        internal static readonly Dictionary<IntPtr, float> _nextIndividualFallbackTime = new Dictionary<IntPtr, float>(128);
        internal static readonly Dictionary<IntPtr, float> _individualFallbackUntil = new Dictionary<IntPtr, float>(64);
        internal static readonly Dictionary<IntPtr, Vector3> _individualFallbackDest = new Dictionary<IntPtr, Vector3>(64);

        internal struct CasualtyDragState
        {
            public Soldier Patient;
            public Vector3 DropSpot;
            public float DragEndTime;
            public float NextScanTime;
        }

        internal struct FootStuckState
        {
            public Vector3 AnchorPos;
            public float AnchorTime;
            public float NextUnstickTime;
            public float UnstickOverrideUntil;
            public Vector3 UnstickDest;
        }

        internal static readonly Dictionary<IntPtr, CasualtyDragState> _casualtyDragStates = new Dictionary<IntPtr, CasualtyDragState>(32);
        internal static readonly Dictionary<IntPtr, FootStuckState> _footStuckStates = new Dictionary<IntPtr, FootStuckState>(128);

        public static void ClearGoapCaches()
        {
            _squadBlackboards.Clear();
            _botGoapPlans.Clear();
            _botScavengeCaches.Clear();
            _freeFloorItemsSnapshot.Clear();
            _stagingFreeFloorItems.Clear();
            _nextFreeFloorItemsSnapshotTime = 0f;
            _floorScanCursor = 0;
            _lastFloorScanFrame = 0;
            _activeScavengeInventories.Clear();
            _stagingScavengeInventories.Clear();
            _nextScavengeInvSnapshotTime = 0f;
            _scavengeInvScanCursor = 0;
            _lastScavengeInvScanFrame = 0;
            _nextWeaponUpgradeAllowedTime.Clear();
            _nextBuddyBandageRefillTime.Clear();
            _nextBuddyAmmoResupplyTime.Clear();
            _nextBotBoxOrCrateResupplyTime.Clear();
            _nextSmokeGrenadeTime.Clear();
            _cachedBoxOverrides.Clear();
            _cachedFloorItemMeta.Clear();
            _casualtyDragStates.Clear();
            _footStuckStates.Clear();
            _cachedInventoryOwners.Clear();
            _initialHqSpawnBriefingConsumed.Clear();
            _nextSquadVoiceTime.Clear();
            _nextSoldierVoiceTime.Clear();
            _nextFlankCoverAssignTime.Clear();
            _medicHealStartTime.Clear();
            _medicHealTargetPtr.Clear();
            _pincerOrderedTaskBySquad.Clear();
            _aiCovertHoldFireSquads.Clear();
            _aiOrderedChargeSquads.Clear();
            _aiChargeBaselineDeaths.Clear();
            _aiCovertHoldFireExpireTime.Clear();
            _aiFollowColumnUntil.Clear();
            _nextAiSquadChargeTime.Clear();
            _nextAiCovertOrderTime.Clear();
            _nextAiFollowOrderTime.Clear();
            _nextAiSquadRetreatTime.Clear();
            _aiSquadRetreatUntil.Clear();
            _nextIndividualFallbackTime.Clear();
            _individualFallbackUntil.Clear();
            _individualFallbackDest.Clear();
            _nextGlobalBotWeaponPickupTime = 0f;
        }

        internal static bool IsActivelyHealingTeammate(Soldier soldier, out MedicHealingDestination healDest)
        {
            healDest = null;
            if (soldier == null || soldier.targetDestination == null || soldier.targetDestination.Pointer == IntPtr.Zero)
                return false;

            healDest = soldier.targetDestination.TryCast<MedicHealingDestination>();
            if (healDest == null)
                return false;

            IntPtr medicPtr = soldier.Pointer;
            Soldier patient = healDest.soldierToHealUp;
            if (patient == null ||
                patient.Pointer == IntPtr.Zero ||
                patient.IsDead ||
                patient.IsOnVehicle() ||
                patient.HasSurrended() ||
                healDest.coverReached ||
                !(patient.IsInjured || patient.isBleeding || patient.GetBleedValue() > 0.01f))
            {
                _medicHealStartTime.Remove(medicPtr);
                _medicHealTargetPtr.Remove(medicPtr);
                soldier.CoverPosition(null);
                healDest = null;
                return false;
            }

            // Guard against unreachable patients (e.g. different building floor/roof or 12s pathing timeout)
            float now = Time.time;
            if (!_medicHealTargetPtr.TryGetValue(medicPtr, out IntPtr prevPatientPtr) || prevPatientPtr != patient.Pointer)
            {
                _medicHealTargetPtr[medicPtr] = patient.Pointer;
                _medicHealStartTime[medicPtr] = now;
            }
            else if (_medicHealStartTime.TryGetValue(medicPtr, out float startT) && now - startT > 12.0f)
            {
                _medicHealStartTime.Remove(medicPtr);
                _medicHealTargetPtr.Remove(medicPtr);
                soldier.CoverPosition(null);
                healDest = null;
                return false;
            }

            if (soldier.transform != null && patient.transform != null)
            {
                Vector3 myPos = soldier.transform.position;
                Vector3 patPos = patient.transform.position;
                float vertDelta = Mathf.Abs(patPos.y - myPos.y);
                Vector2 flatDelta = new Vector2(patPos.x - myPos.x, patPos.z - myPos.z);
                if (vertDelta > 4.5f && flatDelta.sqrMagnitude < 12f * 12f)
                {
                    _medicHealStartTime.Remove(medicPtr);
                    _medicHealTargetPtr.Remove(medicPtr);
                    soldier.CoverPosition(null);
                    healDest = null;
                    return false;
                }
            }

            return true;
        }

        // =====================================================================
        // Audible Native Faction Voice Callouts (F.E.A.R.-Style Plan Telegraphing)
        // =====================================================================
        internal static void TrySayTacticalVoice(Soldier soldier, Squad squad, VoiceManager.VoiceClip clip, float delay = 0.1f)
        {
            if (!VisionConfig.EnableTacticalVoiceCallouts.Value ||
                soldier == null ||
                !soldier.IsAlive ||
                soldier.IsPlayer() ||
                soldier.HasSurrended() ||
                soldier.IsVoiceLocked ||
                soldier.IsTalking())
            {
                return;
            }

            float now = Time.time;
            if (now < soldier.skip_say_time || now < soldier.sayDelayedEnd)
                return;

            IntPtr soldPtr = soldier.Pointer;
            if (soldPtr != IntPtr.Zero)
            {
                if (_nextSoldierVoiceTime.TryGetValue(soldPtr, out float nextSoldVoice) && now < nextSoldVoice)
                    return;
            }

            if (squad != null && squad.Pointer != IntPtr.Zero)
            {
                IntPtr sqPtr = squad.Pointer;
                if (_nextSquadVoiceTime.TryGetValue(sqPtr, out float nextSqVoice) && now < nextSqVoice)
                    return;
                _nextSquadVoiceTime[sqPtr] = now + 5.5f;
            }

            if (soldPtr != IntPtr.Zero)
            {
                _nextSoldierVoiceTime[soldPtr] = now + 9.0f;
            }

            try
            {
                // Local 3D spatialized audio: native Soldier.Say (0x18076144C) automatically checks Creature.IsLodded2()
                // against the active rendering camera—so first-person, vehicle, and Free/Spectator Camera (F) all hear
                // nearby bots naturally anywhere on the map with zero RPC cost!
                soldier.Say(clip, delay, -1, null);

                // In Multiplayer Co-op, only spend a Photon SyncSay packet when a remote human player's soldier is within earshot (<= 75m)!
                if (Photon.Pun.PhotonNetwork.InRoom &&
                    soldier.transform != null &&
                    AiCore.IsNearAnyRemotePlayer(soldier.transform.position, 75.0f) &&
                    AiCore.TryConsumeBotRpcBudget(0.50f))
                {
                    soldier.SyncSay(clip, -1);
                }
            }
            catch { }
        }

        // =====================================================================
        // Tier 3: Radioman-Gated Team Intel & Multi-Squad Net GOAP Planner
        // =====================================================================
        internal static bool IsMountedOnGroundVehicle(Soldier s)
        {
            if (s == null || !s.IsOnVehicle())
                return false;

            Vehicle veh = s.GetCurrentVehicle();
            if (veh == null || veh.Pointer == IntPtr.Zero || veh.IsAirVehicle() || veh.TryCast<VehicleParachute>() != null)
                return false;

            return true;
        }

        internal static bool HasLiveRadioOrRelayLink(Squad sq)
        {
            if (sq == null || sq.Pointer == IntPtr.Zero)
                return false;

            if (!VisionConfig.EnableRadiomanIntelNet.Value)
                return true;

            // O(1) lookup from AiCore's flat 0.35s WorldSnapshot (eliminates nested O(N^2)/O(N^3) Squad.AllSquads scans!)
            if (AiCore.TryGetSquadSnapshot(sq.Pointer, out AiCore.SquadSnapshot snap))
                return snap.HasRadioOrRelay;

            if (sq.RadioRequestActiveAndConfirmed())
                return true;

            Soldier leader = sq.Leader;
            if (leader == null || !leader.IsAlive || leader.transform == null)
                return false;

            if (leader.IsOnVehicle())
            {
                Vehicle veh = leader.GetCurrentVehicle();
                if (veh != null && veh.TryCast<VehicleParachute>() == null)
                    return true;
            }

            Soldier rad = sq.GetRadioman();
            return rad != null &&
                   rad.IsAlive &&
                   !rad.HasSurrended() &&
                   rad.transform != null &&
                   (rad.transform.position - leader.transform.position).sqrMagnitude <= 32f * 32f;
        }

        internal static bool IsSquadConnectedToRadioNet(Squad sq)
        {
            if (sq == null || sq.Pointer == IntPtr.Zero)
                return false;

            if (HasLiveRadioOrRelayLink(sq))
                return true;

            // Newly spawned squads without a Radioman (e.g. AT squads, Sniper/Mortar/Engineer teams)
            // receive ONE initial command from the Team Net on spawn before going off-grid until they
            // physically link up within 45m of a Radio-Connected squad or friendly vehicle!
            return !_initialHqSpawnBriefingConsumed.Contains(sq.Pointer);
        }

        internal static void ConsumeInitialHqSpawnBriefingIfNoRadio(Squad sq)
        {
            if (sq != null && sq.Pointer != IntPtr.Zero && !HasLiveRadioOrRelayLink(sq))
            {
                _initialHqSpawnBriefingConsumed.Add(sq.Pointer);
            }
        }

        internal static bool CanBroadcastEnemyContactToTeamNet(Squad enemySquad, Vector3 enemyPos, string enemyFaction, bool isEnemyVehicle = false)
        {
            if (!VisionConfig.EnableRadiomanIntelNet.Value)
                return true;

            if (enemySquad != null && (enemySquad.isMarked || enemySquad.isMarkedPinPoint || enemySquad.isHalfMarked))
                return true;

            // Aerial Recon Broadcast: Friendly aircraft flying overhead have cockpit radios and can report
            // spotted enemy tanks/vehicles across the map to the Team Net!
            if (isEnemyVehicle && _airborneAircraft.Count > 0)
            {
                for (int a = 0; a < _airborneAircraft.Count; a++)
                {
                    AirborneAircraftInfo planeInfo = _airborneAircraft[a];
                    if (planeInfo.Faction != enemyFaction &&
                        planeInfo.Plane != null &&
                        planeInfo.Plane.Pointer != IntPtr.Zero &&
                        !planeInfo.Plane.isGrounded &&
                        planeInfo.Plane.CanFight() &&
                        (planeInfo.Position - enemyPos).sqrMagnitude <= 650f * 650f)
                    {
                        return true;
                    }
                }
            }

            AiCore.EnsureWorldSnapshot();
            int sqCount = AiCore.WorldSquadCount;
            if (sqCount == 0)
                return true;

            var squads = AiCore.WorldSquads;
            for (int i = 0; i < sqCount; i++)
            {
                ref AiCore.SquadSnapshot fSnap = ref squads[i];
                if (!fSnap.LeaderIsAlive || string.IsNullOrEmpty(fSnap.Faction) || fSnap.Faction == enemyFaction)
                    continue;

                if ((fSnap.RefPos - enemyPos).sqrMagnitude > 190f * 190f)
                    continue;

                if (fSnap.HasRadioOrRelay)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Tier 3 Team Net GOAP Planner: Treats each Faction's Radio Net as a strategic commander playing
        /// the match with whatever Infantry Squads and Combat Vehicles can hear it:
        ///   1. Multi-Squad / Combined-Arms "Anvil & Hammer": An engaged Infantry Squad OR Tank/Vehicle Anvil
        ///      pins the enemy + calls radio support while a nearby Radio-Connected Infantry Squad flanks wide (`AttackFromSide`).
        ///   2. Single-Squad "Pin & Envelop": When a connected squad's own MG gunners pin the threat and no neighbor
        ///      squad is nearby, the Squad Leader orders a local flank toward covered terrain.
        ///   3. Multi-Squad Objective Pincers: When 2+ connected squads advance on the same objective, additional
        ///      squads swing left/right (`AttackFromSide`) rather than funneling down the same center line.
        /// Disconnected squads (no Radioman and >45m from friendly forces) cannot hear the Team Net and fight on their own.
        /// </summary>
        private static readonly List<Squad> _reusableConnectedInfantrySquads = new List<Squad>(32);
        private static readonly List<Squad> _reusableActiveVehicleAnvils = new List<Squad>(16);
        private static readonly List<Squad> _reusableAiFootSquads = new List<Squad>(48);

        private static void ReleaseAiSquadExecutionStateIfActive(Squad sq)
        {
            if (sq == null || sq.Pointer == IntPtr.Zero)
                return;

            IntPtr sqPtr = sq.Pointer;
            _aiFollowColumnUntil.Remove(sqPtr);
            _aiSquadRetreatUntil.Remove(sqPtr);
            if (_aiCovertHoldFireSquads.Remove(sqPtr))
            {
                _aiCovertHoldFireExpireTime.Remove(sqPtr);
                if (sq.HoldFire)
                {
                    Soldier leader = sq.Leader;
                    bool canSync = !Photon.Pun.PhotonNetwork.InRoom ||
                                   (leader != null && (leader.soldierSyncher == null || leader.soldierSyncher.OwnSyncher));
                    if (canSync)
                    {
                        try { sq.SetHoldFireOrder(false, false, false, Photon.Pun.PhotonNetwork.InRoom); } catch { }
                    }
                }
            }
        }

        internal static void RunTeamNetGoapCoordinator(float now)
        {
            if (!VisionConfig.EnableTacticalGoapPlanner.Value)
                return;

            AiCore.EnsureWorldSnapshot();
            int sqCount = AiCore.WorldSquadCount;
            if (sqCount < 1)
                return;

            var connectedInfantrySquads = _reusableConnectedInfantrySquads;
            var activeVehicleAnvils = _reusableActiveVehicleAnvils;
            var aiFootSquads = _reusableAiFootSquads;
            connectedInfantrySquads.Clear();
            activeVehicleAnvils.Clear();
            aiFootSquads.Clear();
            bool issuedSquadCoverOrderThisTick = false;

            var squads = AiCore.WorldSquads;
            for (int sIdx = 0; sIdx < sqCount; sIdx++)
            {
                ref AiCore.SquadSnapshot sqSnap = ref squads[sIdx];
                if (!sqSnap.LeaderIsAlive)
                    continue;

                Squad sq = sqSnap.Ref;
                Soldier leader = sqSnap.Leader;
                if (sq == null || sq.Pointer == IntPtr.Zero || leader == null || !leader.IsAlive || leader.transform == null)
                    continue;

                // If the Human Player is the Squad Leader, never issue movement orders to their squad,
                // BUT if the Player's Squad is connected to the Radio Net and engaged in a firefight,
                // allow the Team Net to treat the Player's Squad as an Anvil and send a nearby AI Hammer squad to flank!
                if (leader.IsPlayer())
                {
                    ReleaseAiSquadExecutionStateIfActive(sq);
                    SquadBlackboard playerBoard = GetOrUpdateSquadBlackboard(sq, leader);
                    if (playerBoard.IsConnectedToRadioNet && playerBoard.HasActiveThreat)
                    {
                        activeVehicleAnvils.Add(sq);
                    }
                    continue;
                }

                if (!AiCore.IsLocallyOwnedBot(leader))
                    continue;

                // Mounted Ground Combat Vehicles have onboard radios and can act as Heavy Anvils for nearby infantry!
                if (sqSnap.LeaderOnVehicle)
                {
                    ReleaseAiSquadExecutionStateIfActive(sq);
                    if (IsMountedOnGroundVehicle(leader))
                    {
                        SquadBlackboard vehBoard = GetOrUpdateSquadBlackboard(sq, leader);
                        if (vehBoard.HasActiveThreat)
                        {
                            activeVehicleAnvils.Add(sq);
                        }
                    }
                    continue;
                }

                aiFootSquads.Add(sq);
                SquadBlackboard board = GetOrUpdateSquadBlackboard(sq, leader);
                if (!board.IsConnectedToRadioNet)
                {
                    // Allow a squad that received a one-time spawn command (or a 45m relay command) to finish executing
                    // that directive before reverting to Disconnected_Autonomous!
                    if (now >= board.NetDirectiveExpireTime)
                    {
                        FactionCommandProfile disProfile = GetFactionCommandProfile(leader.faction);

                        // Decentralized Command Doctrines (e.g. Germany Auftragstaktik, Finland Motti, Mongolia, Australia/NZ/Gurkha,
                        // Poland, Greece, China, Japan): Squad Leader retains local tactical initiative even when cut off from HQ radio!
                        if (disProfile.AllowDisconnectedLocalFlank &&
                            board.HasActiveThreat &&
                            board.AliveMemberCount >= 4 &&
                            board.IsThreatBeingPinned(now) &&
                            (board.FlankLeftCover != null || board.FlankRightCover != null) &&
                            leader.Params != null && leader.Params.canGiveOrders && leader.Params.canMove)
                        {
                            Vector3 toThreat = board.ThreatPosition - leader.transform.position;
                            toThreat.y = 0f;
                            float dThreat = toThreat.magnitude;
                            if (dThreat >= 25f && dThreat <= 135f)
                            {
                                Vector3 axDir = toThreat / dThreat;
                                Vector3 pRight = new Vector3(axDir.z, 0f, -axDir.x);
                                float sSign = board.FlankRightCover != null ? 1f : -1f;
                                float lat = disProfile.FlankLateralBias;
                                float fwd = Mathf.Sqrt(Mathf.Max(0.1f, 1f - lat * lat));
                                Vector3 localFlankDir = (axDir * fwd - pRight * (sSign * lat)).normalized;

                                board.NetDirective = SquadNetDirective.Hammer_WideFlankEnvelopment;
                                board.NetDirectiveExpireTime = now + 15.0f;
                                board.NetDirectiveTargetPos = board.ThreatPosition;
                                _squadBlackboards[sq.Pointer] = board;

                                if (!issuedSquadCoverOrderThisTick &&
                                    (!Photon.Pun.PhotonNetwork.InRoom || AiCore.TryConsumeBotRpcBudget(0.45f)))
                                {
                                    issuedSquadCoverOrderThisTick = true;
                                    try
                                    {
                                        sq.AttackFromSide(localFlankDir, board.ThreatPosition, 18.0f);
                                    }
                                    catch { }
                                }

                                TrySayTacticalVoice(leader, sq, VoiceManager.VoiceClip.attackThere, 0.2f);
                                TeamNetAnvilHammerCount++;
                                GoapPlansExecutedCount++;
                                continue;
                            }
                        }

                        board.NetDirective = SquadNetDirective.Disconnected_Autonomous;
                        _squadBlackboards[sq.Pointer] = board;

                        // If an unengaged disconnected squad (e.g. an AT squad after finishing its initial order)
                        // is within 95m of a friendly Radio-Connected squad/vehicle, move toward them to reconnect!
                        if (!issuedSquadCoverOrderThisTick &&
                            !board.HasActiveThreat &&
                            leader.Params != null && leader.Params.canGiveOrders && leader.Params.canMove &&
                            TryFindNearestRadioHubPosition(sq, leader.transform.position, leader.faction, 95f, out Vector3 hubPos) &&
                            (!Photon.Pun.PhotonNetwork.InRoom || AiCore.TryConsumeBotRpcBudget(0.45f)))
                        {
                            issuedSquadCoverOrderThisTick = true;
                            try
                            {
                                sq.SendUnitsToCovers(hubPos, 18.0f);
                            }
                            catch { }
                        }
                    }
                    continue;
                }

                if (now >= board.NetDirectiveExpireTime)
                {
                    board.NetDirective = SquadNetDirective.Connected_StandardObjective;
                    _squadBlackboards[sq.Pointer] = board;
                }

                // Tier 3 TeamNet Sector Withdrawal: if a Radio-Connected frontline squad is shattered & heavily outnumbered
                // or facing close enemy armor with zero AT capability (outside desperate cap saves), authorize a sector fallback
                // and call radio artillery/tank support onto the enemy position!
                if (VisionConfig.EnableTacticalRetreatsAndRegroup.Value &&
                    board.HasActiveThreat &&
                    board.NetDirective != SquadNetDirective.Regroup_SectorWithdrawal &&
                    !IsSquadOnRetreatTask(sq) &&
                    !(board.IsDefendingObjective && board.ObjectiveCaptureProgress >= 0.55f) &&
                    !(board.IsInvaderFaction && board.TicketRatio01 <= 0.18f) &&
                    (!_nextAiSquadRetreatTime.TryGetValue(sq.Pointer, out float nextNetRetT) || now >= nextNetRetT))
                {
                    float dNetThreat = Vector3.Distance(leader.transform.position, board.ThreatPosition);
                    bool uncounterableCloseArmor = board.ThreatIsArmor && !board.SquadHasAntiTank && dNetThreat <= 44.0f;
                    bool shatteredOutnumbered = board.AliveMemberCount <= 3 &&
                                                dNetThreat <= 68.0f &&
                                                board.NearbyEnemyCount42m >= (board.NearbyFriendlyCount42m * 2 + 3);
                    if (uncounterableCloseArmor || shatteredOutnumbered)
                    {
                        board.NetDirective = SquadNetDirective.Regroup_SectorWithdrawal;
                        board.NetDirectiveExpireTime = now + 18.0f;
                        board.NetDirectiveTargetPos = board.ThreatPosition;
                        _squadBlackboards[sq.Pointer] = board;
                        TryTriggerSquadRadioSupport(sq, leader);
                        if (VisionConfig.DebugLogging.Value)
                        {
                            AiCore.LogDebug(
                                "TeamNetRetreat",
                                $"{leader.faction} Radio Net authorized Sector Withdrawal for '{sq.squadName}' (alive:{board.AliveMemberCount}, armor:{uncounterableCloseArmor}, enemies42m:{board.NearbyEnemyCount42m}).",
                                3.0f);
                        }
                    }
                }

                connectedInfantrySquads.Add(sq);
            }

            int infCount = connectedInfantrySquads.Count;
            if (infCount > 0)
            {
                // =================================================================
                // 1A. Vehicle/Tank Anvil + Infantry Hammer (Combined-Arms Flank)
                // =================================================================
                for (int v = 0; v < activeVehicleAnvils.Count; v++)
                {
                    Squad vehSq = activeVehicleAnvils[v];
                    if (!_squadBlackboards.TryGetValue(vehSq.Pointer, out SquadBlackboard vehBoard) || !vehBoard.HasActiveThreat)
                        continue;

                    Soldier vehLeader = vehSq.Leader;
                    if (vehLeader == null || vehLeader.transform == null)
                        continue;

                    Vector3 vehPos = vehLeader.transform.position;
                    Vector3 threatPos = vehBoard.ThreatPosition;
                    Vector3 vehToThreat = threatPos - vehPos;
                    vehToThreat.y = 0f;
                    float vehDist = vehToThreat.magnitude;
                    if (vehDist < 18f || vehDist > 180f)
                        continue;

                    Vector3 axisDir = vehToThreat / vehDist;
                    Vector3 perpRight = new Vector3(axisDir.z, 0f, -axisDir.x);
                    IntPtr vehTaskPtr = vehSq.currentTask != null ? vehSq.currentTask.Pointer : IntPtr.Zero;

                    Squad bestHammerSq = FindBestAvailableHammerSquad(connectedInfantrySquads, -1, vehSq.Pointer, vehLeader.faction, vehTaskPtr, threatPos, 115f, now);
                    if (bestHammerSq != null)
                    {
                        IssueHammerFlankDirective(bestHammerSq, vehPos, threatPos, axisDir, perpRight, now, $"Tank/Vehicle Anvil ('{vehSq.squadName}')");
                    }
                }

                // =================================================================
                // 1B. Infantry Anvil + Infantry Hammer (or Single-Squad Pin & Flank)
                // =================================================================
                for (int i = 0; i < infCount; i++)
                {
                    Squad anvilSq = connectedInfantrySquads[i];
                    if (IsSquadActivelyRetreating(anvilSq.Pointer, now))
                        continue;

                    if (!_squadBlackboards.TryGetValue(anvilSq.Pointer, out SquadBlackboard anvilBoard))
                        continue;

                    if (!anvilBoard.HasActiveThreat || now < anvilBoard.NetDirectiveExpireTime)
                        continue;

                    Soldier anvilLeader = anvilSq.Leader;
                    if (anvilLeader == null || anvilLeader.transform == null)
                        continue;

                    Vector3 anvilPos = anvilLeader.transform.position;
                    Vector3 threatPos = anvilBoard.ThreatPosition;
                    Vector3 anvilToThreat = threatPos - anvilPos;
                    anvilToThreat.y = 0f;
                    float anvilDist = anvilToThreat.magnitude;
                    if (anvilDist < 18f || anvilDist > 160f)
                        continue;

                    Vector3 axisDir = anvilToThreat / anvilDist;
                    Vector3 perpRight = new Vector3(axisDir.z, 0f, -axisDir.x);
                    IntPtr anvilTaskPtr = anvilSq.currentTask != null ? anvilSq.currentTask.Pointer : IntPtr.Zero;

                    // Find best nearby Radio-Connected friendly squad within 115m on the same objective/sector to act as the HAMMER
                    Squad bestHammerSq = FindBestAvailableHammerSquad(connectedInfantrySquads, i, anvilSq.Pointer, anvilLeader.faction, anvilTaskPtr, threatPos, 115f, now);

                    if (bestHammerSq != null)
                    {
                        // Post ANVIL advisory suggestion on Squad A (evaluated by Squad A's Leader)
                        anvilBoard.SuggestedNetDirective = SquadNetDirective.Anvil_PinAndCallSupport;
                        anvilBoard.SuggestedDirectiveExpireTime = now + 24.0f;
                        anvilBoard.SuggestedDirectiveTargetPos = threatPos;
                        anvilBoard.SuggestedPincerTaskPtr = IntPtr.Zero;
                        _squadBlackboards[anvilSq.Pointer] = anvilBoard;
                        ConsumeInitialHqSpawnBriefingIfNoRadio(anvilSq);

                        // Post HAMMER advisory suggestion on Squad B (evaluated by Squad B's Leader)
                        IssueHammerFlankDirective(bestHammerSq, anvilPos, threatPos, axisDir, perpRight, now, $"Infantry Anvil ('{anvilSq.squadName}')");
                    }
                    else if (anvilBoard.AliveMemberCount >= 4 &&
                             anvilBoard.IsThreatBeingPinned(now) &&
                             (anvilBoard.FlankLeftCover != null || anvilBoard.FlankRightCover != null) &&
                             anvilLeader.Params != null && anvilLeader.Params.canGiveOrders && anvilLeader.Params.canMove)
                    {
                        // Single-Squad Self-Pin & Flank suggestion: MG gunner has the enemy pinned and lateral cover exists.
                        anvilBoard.SuggestedNetDirective = SquadNetDirective.Hammer_WideFlankEnvelopment;
                        anvilBoard.SuggestedDirectiveExpireTime = now + 22.0f;
                        anvilBoard.SuggestedDirectiveTargetPos = threatPos;
                        anvilBoard.SuggestedPincerTaskPtr = IntPtr.Zero;
                        _squadBlackboards[anvilSq.Pointer] = anvilBoard;
                        ConsumeInitialHqSpawnBriefingIfNoRadio(anvilSq);
                    }
                }

                // =================================================================
                // 2. Multi-Squad Objective Pincer (Advisory HQ Suggestion -> NCO Decides Execution!)
                // =================================================================
                _reusablePrimarySquadByTask.Clear();
                _reusablePincerCountByTask.Clear();
                var primarySquadByTask = _reusablePrimarySquadByTask;
                var pincerCountByTask = _reusablePincerCountByTask;

                for (int i = 0; i < infCount; i++)
                {
                    Squad sq = connectedInfantrySquads[i];
                    if (sq.currentTask == null || sq.currentTask.Pointer == IntPtr.Zero || IsSquadActivelyRetreating(sq.Pointer, now))
                        continue;

                    if (_squadBlackboards.TryGetValue(sq.Pointer, out SquadBlackboard board) &&
                        (board.HasActiveThreat || now < board.NetDirectiveExpireTime || now < board.SuggestedDirectiveExpireTime))
                    {
                        continue;
                    }

                    Soldier leader = sq.Leader;
                    if (leader == null || leader.transform == null ||
                        (leader.Params != null && (!leader.Params.canGiveOrders || !leader.Params.canMove)))
                    {
                        continue;
                    }

                    IntPtr taskPtr = sq.currentTask.Pointer;
                    Vector3 taskPos = sq.currentTask.GetTaskDestination();
                    Vector3 toTask = taskPos - leader.transform.position;
                    toTask.y = 0f;
                    float distToTask = toTask.magnitude;
                    if (distToTask < 55f || distToTask > 175f)
                        continue;

                    if (!primarySquadByTask.ContainsKey(taskPtr))
                    {
                        primarySquadByTask[taskPtr] = sq;
                        pincerCountByTask[taskPtr] = 0;
                        ConsumeInitialHqSpawnBriefingIfNoRadio(sq);
                        continue;
                    }

                    int pincerIndex = pincerCountByTask[taskPtr] + 1;
                    pincerCountByTask[taskPtr] = pincerIndex;

                    FactionCommandProfile pincerProfile = GetFactionCommandProfile(leader.faction);
                    Vector3 axisDir = toTask / distToTask;
                    Vector3 perpRight = new Vector3(axisDir.z, 0f, -axisDir.x);
                    float sideSign = (pincerIndex % 2 == 1) ? 1f : -1f;
                    float latBias = pincerProfile.FlankLateralBias;
                    float fwdBias = Mathf.Sqrt(Mathf.Max(0.1f, 1f - latBias * latBias));
                    Vector3 pincerFlankDir = (axisDir * fwdBias - perpRight * (sideSign * latBias)).normalized;

                    // Post advisory pincer maneuver to Squad Leader instead of HQ directly snapping sq.AttackFromSide!
                    board.SuggestedNetDirective = SquadNetDirective.Hammer_WideFlankEnvelopment;
                    board.SuggestedDirectiveExpireTime = now + 28.0f;
                    board.SuggestedDirectiveTargetPos = taskPos;
                    board.SuggestedPincerFlankDir = pincerFlankDir;
                    board.SuggestedPincerTaskPtr = taskPtr;
                    _squadBlackboards[sq.Pointer] = board;
                    ConsumeInitialHqSpawnBriefingIfNoRadio(sq);
                }
                _reusablePrimarySquadByTask.Clear();
                _reusablePincerCountByTask.Clear();
            }

            // =================================================================
            // 3. Tier 2 Squad Leader Execution GOAP (Charge! / Covert Hold-Fire / Follow Column)
            //    Evaluated for all AI foot Squad Leaders (both Radio-Connected and Disconnected)
            //    so each Squad Leader translates TeamNet directives & local battlefield state into
            //    native squad orders based on personality, doctrine, cap urgency, tickets & smoke.
            // =================================================================
            int footCount = aiFootSquads.Count;
            for (int i = 0; i < footCount; i++)
            {
                Squad footSq = aiFootSquads[i];
                if (footSq == null || footSq.Pointer == IntPtr.Zero)
                    continue;

                Soldier footLeader = footSq.Leader;
                if (footLeader == null || !footLeader.IsAlive || footLeader.transform == null)
                    continue;

                if (!_squadBlackboards.TryGetValue(footSq.Pointer, out SquadBlackboard footBoard))
                    footBoard = GetOrUpdateSquadBlackboard(footSq, footLeader);

                EvaluateSquadLeaderExecutionOrders(footSq, footLeader, ref footBoard, now, ref issuedSquadCoverOrderThisTick);
            }
            aiFootSquads.Clear();
        }

        private static void EvaluateSquadLeaderExecutionOrders(
            Squad sq,
            Soldier leader,
            ref SquadBlackboard board,
            float now,
            ref bool issuedSquadCoverOrderThisTick)
        {
            IntPtr sqPtr = sq.Pointer;
            Vector3 leaderPos = leader.transform.position;
            float leaderSupp = leader.GetSuppressionValue();
            float distToThreat = board.HasActiveThreat ? Vector3.Distance(leaderPos, board.ThreatPosition) : 9999f;
            float distToObj = board.HasActiveObjectiveTask ? Vector3.Distance(leaderPos, board.ObjectivePosition) : 9999f;
            bool syncOnline = Photon.Pun.PhotonNetwork.InRoom;

            // A. Auto-Release AI Covert Hold-Fire immediately when discovered, suppressed, or entering close assault range.
            //    Strictly scoped to _aiCovertHoldFireSquads so mission-scripted Lua holdFire orders are NEVER touched!
            if (_aiCovertHoldFireSquads.Contains(sqPtr))
            {
                if (!sq.HoldFire)
                {
                    _aiCovertHoldFireSquads.Remove(sqPtr);
                    _aiCovertHoldFireExpireTime.Remove(sqPtr);
                }
                else
                {
                    bool expired = !_aiCovertHoldFireExpireTime.TryGetValue(sqPtr, out float expT) || now >= expT;
                    bool compromised = sq.IsEnemyAwareOfThisSquad ||
                                       leaderSupp >= 8.0f ||
                                       leader.isBleeding ||
                                       distToThreat <= 36.0f ||
                                       distToObj <= 36.0f ||
                                       sq.InChargeTime() ||
                                       (board.IsDefendingObjective && board.ObjectiveCaptureProgress >= 0.35f);

                    if ((expired || compromised) && (!syncOnline || AiCore.TryConsumeBotRpcBudget(0.20f)))
                    {
                        _aiCovertHoldFireSquads.Remove(sqPtr);
                        _aiCovertHoldFireExpireTime.Remove(sqPtr);
                        try
                        {
                            sq.SetHoldFireOrder(false, false, compromised && !expired, syncOnline);
                        }
                        catch { }
                    }
                }
            }

            // B. Auto-Release AI Follow Column March as soon as the squad nears the frontline (<=78m) or takes contact
            //    so vanilla SquadLeaderRoutine deploys the squad into tactical cover immediately.
            if (_aiFollowColumnUntil.TryGetValue(sqPtr, out float followUntil))
            {
                if (sq.order != Order.follow)
                {
                    _aiFollowColumnUntil.Remove(sqPtr);
                }
                else if (now >= followUntil ||
                         board.HasActiveThreat ||
                         sq.IsEnemyAwareOfThisSquad ||
                         leaderSupp >= 4.0f ||
                         leader.isBleeding ||
                         distToObj <= 78.0f)
                {
                    _aiFollowColumnUntil.Remove(sqPtr);
                    if (sq.timed_order_end > now)
                        sq.timed_order_end = 0f;
                }
            }

            // B2. Auto-Release AI Fighting Withdrawal once rally window completes or squad is out of immediate danger
            if (_aiSquadRetreatUntil.TryGetValue(sqPtr, out float sqRetUntil))
            {
                if (now >= sqRetUntil || (!board.HasActiveThreat && leaderSupp < 5.0f))
                {
                    _aiSquadRetreatUntil.Remove(sqPtr);
                }
                else
                {
                    return;
                }
            }

            FactionCommandProfile profile = GetFactionCommandProfile(leader.faction);
            SoldierPersonality leaderPers = GetPersonality(leader);
            bool isFanaticalDoctrine = profile.BayonetBreachCostMult <= 0.68f;

            // B3. Abort Suicidal AI Charge Early if 2+ Squadmates Are Cut Down Mid-Charge!
            if (_aiOrderedChargeSquads.Contains(sqPtr))
            {
                if (!sq.InChargeTime())
                {
                    _aiOrderedChargeSquads.Remove(sqPtr);
                    _aiChargeBaselineDeaths.Remove(sqPtr);
                }
                else if (!sq.isChargeSquad && !sq.IsPlayerInSquad() && !isFanaticalDoctrine)
                {
                    float baseDeaths = _aiChargeBaselineDeaths.TryGetValue(sqPtr, out float bd0) ? bd0 : 0f;
                    float chargeDeaths = board.DecayedRecentDeaths - baseDeaths;
                    bool mowedDown = chargeDeaths >= 1.65f ||
                                     (leaderSupp >= 68.0f && distToObj > 18.0f && distToThreat > 18.0f);
                    if (mowedDown && (!syncOnline || AiCore.TryConsumeBotRpcBudget(0.45f)))
                    {
                        _aiOrderedChargeSquads.Remove(sqPtr);
                        _aiChargeBaselineDeaths.Remove(sqPtr);
                        sq.timed_order_end = 0f;
                        try { sq.Leader_OrderAttackCurrentTask(leader); } catch { }
                        TrySayTacticalVoice(leader, sq, VoiceManager.VoiceClip.keepYourHeadDown, 0.08f);
                        SquadLeaderBalksCount++;
                        BotActionTrace.RecordEvent(leader, BotTraceEventKind.LeaderBalk, "abort_charge_casualties", distToThreat);
                        issuedSquadCoverOrderThisTick = true;
                        if (VisionConfig.DebugLogging.Value)
                        {
                            AiCore.LogDebug(
                                "SquadChargeAbort",
                                $"{leader.faction} {leaderPers} aborted Charge! on '{sq.squadName}' after heavy casualties (deaths:{chargeDeaths:F1}, supp:{leaderSupp:F0}).",
                                3.0f);
                        }
                        return;
                    }
                }
            }

            // B4. Autonomous NCO Evaluation of HQ Advisory Suggestion (Obey, Adapt, or Balk/Refuse Suicidal Push!)
            if (board.IsConnectedToRadioNet &&
                now < board.SuggestedDirectiveExpireTime &&
                board.SuggestedNetDirective != SquadNetDirective.Disconnected_Autonomous)
            {
                SquadNetDirective suggested = board.SuggestedNetDirective;
                float sugExpire = board.SuggestedDirectiveExpireTime;
                Vector3 sugTarget = board.SuggestedDirectiveTargetPos;
                Vector3 sugPincerDir = board.SuggestedPincerFlankDir;
                IntPtr sugTaskPtr = board.SuggestedPincerTaskPtr;
                board.SuggestedDirectiveExpireTime = 0f;

                if (suggested == SquadNetDirective.Hammer_WideFlankEnvelopment)
                {
                    float balkDeathThresh = leaderPers == SoldierPersonality.Leader_Spearhead
                        ? 2.60f
                        : (leaderPers == SoldierPersonality.Leader_OldGuard ? 1.35f : 1.85f);
                    float balkSuppThresh = leaderPers == SoldierPersonality.Leader_Spearhead
                        ? 58.0f
                        : (leaderPers == SoldierPersonality.Leader_OldGuard ? 34.0f : 44.0f);
                    bool hasSmokeUp = now < board.SmokeActiveUntil;

                    bool ncoBalks = !isFanaticalDoctrine &&
                                    (board.AliveMemberCount <= 2 ||
                                     board.DecayedRecentDeaths >= balkDeathThresh ||
                                     (leaderSupp >= balkSuppThresh && !hasSmokeUp));

                    if (ncoBalks)
                    {
                        // NCO refuses HQ's flank/push order to save his men: converts locally into Anvil base-of-fire & requests fire support!
                        board.NetDirective = SquadNetDirective.Anvil_PinAndCallSupport;
                        board.NetDirectiveExpireTime = now + 18.0f;
                        board.NetDirectiveTargetPos = board.HasActiveThreat ? board.ThreatPosition : sugTarget;
                        _squadBlackboards[sqPtr] = board;

                        TrySayTacticalVoice(leader, sq, VoiceManager.VoiceClip.keepYourHeadDown, 0.15f);
                        TryTriggerSquadRadioSupport(sq, leader);
                        SquadLeaderBalksCount++;
                        BotActionTrace.RecordEvent(leader, BotTraceEventKind.LeaderBalk, "refused_hq_flank", distToThreat);
                        if (VisionConfig.DebugLogging.Value)
                        {
                            AiCore.LogDebug(
                                "NcoBalked",
                                $"{leader.faction} {leaderPers} ('{sq.squadName}') refused HQ flank suggestion (deaths:{board.DecayedRecentDeaths:F1}, supp:{leaderSupp:F0}) -> holding cover & calling support.",
                                3.0f);
                        }
                    }
                    else
                    {
                        // NCO accepts HQ's flank/pincer suggestion and chooses HOW to execute (smoke, stealth, or AttackFromSide)
                        board.NetDirective = SquadNetDirective.Hammer_WideFlankEnvelopment;
                        board.NetDirectiveExpireTime = sugExpire;
                        board.NetDirectiveTargetPos = sugTarget;
                        _squadBlackboards[sqPtr] = board;

                        if (sugTaskPtr != IntPtr.Zero &&
                            sq.currentTask != null &&
                            sq.currentTask.Pointer == sugTaskPtr &&
                            (!_pincerOrderedTaskBySquad.TryGetValue(sqPtr, out IntPtr prevPincerTask) || prevPincerTask != sugTaskPtr) &&
                            !issuedSquadCoverOrderThisTick &&
                            (!syncOnline || AiCore.TryConsumeBotRpcBudget(0.45f)))
                        {
                            issuedSquadCoverOrderThisTick = true;
                            _pincerOrderedTaskBySquad[sqPtr] = sugTaskPtr;
                            try
                            {
                                sq.AttackFromSide(sugPincerDir, sugTarget, 22.0f);
                                if (sq.timed_order_end < sugExpire)
                                    sq.timed_order_end = sugExpire;
                            }
                            catch { }
                        }

                        if (board.HasActiveThreat && distToThreat <= 42.0f && !hasSmokeUp && leaderPers != SoldierPersonality.Leader_Spearhead)
                        {
                            TryPopSquadRetreatSmoke(sq, leader, ref board, now);
                            _squadBlackboards[sqPtr] = board;
                        }

                        TrySayTacticalVoice(leader, sq, VoiceManager.VoiceClip.attackThere, 0.22f);
                        TeamNetAnvilHammerCount++;
                        GoapPlansExecutedCount++;
                    }
                }
                else
                {
                    board.NetDirective = suggested;
                    board.NetDirectiveExpireTime = sugExpire;
                    board.NetDirectiveTargetPos = sugTarget;
                    _squadBlackboards[sqPtr] = board;

                    if (suggested == SquadNetDirective.Anvil_PinAndCallSupport)
                    {
                        TrySayTacticalVoice(leader, sq, VoiceManager.VoiceClip.coveringFire, 0.15f);
                        TryTriggerSquadRadioSupport(sq, leader);
                    }
                }
            }

            // C. Evaluate New Tier 2 Squad Leader Execution Order (Baseline 1.00f = Standard Cover-to-Cover Assault)
            if (issuedSquadCoverOrderThisTick)
                return;

            if (leader.Params != null && (!leader.Params.canGiveOrders || !leader.Params.canMove))
                return;

            if (leader.HasSurrended() || leader.IsInjured || IsSquadOnRetreatTask(sq) || _squadOrderedToVehicle.ContainsKey(sqPtr))
                return;

            float bestCost = 0.98f;
            int bestAction = 0; // 1 = Charge, 2 = Covert HoldFire, 3 = Follow Column, 4 = Fighting Withdrawal / Regroup
            Vector3 chargeTarget = Vector3.zero;
            Vector3 retreatRallyPos = Vector3.zero;

            // 1. Evaluate Charge! (sq.Charge) — Short-Range Shock Breach (<= 26m non-fanatic, requires tactical advantage or desperate cap)
            if (!sq.InChargeTime() &&
                board.AliveMemberCount >= 3 &&
                (!_nextAiSquadChargeTime.TryGetValue(sqPtr, out float nextChargeT) || now >= nextChargeT) &&
                !AiCore.IsOnBridgeOrNarrowCrossing(leaderPos))
            {
                bool isDesperateOvertime = (board.HasActiveObjectiveTask && board.IsDefendingObjective && board.ObjectiveCaptureProgress >= 0.62f) ||
                                           (board.IsInvaderFaction && board.TicketRatio01 <= 0.16f && board.HasActiveObjectiveTask && !board.IsDefendingObjective);
                float maxObjChargeDist = isFanaticalDoctrine ? 44.0f : (isDesperateOvertime ? 34.0f : 26.0f);
                float maxThreatChargeDist = isFanaticalDoctrine ? 38.0f : 24.0f;

                bool hasChargeTarget = false;
                if (board.HasActiveObjectiveTask && distToObj >= 12.0f && distToObj <= maxObjChargeDist)
                {
                    chargeTarget = board.ObjectivePosition;
                    hasChargeTarget = !board.ThreatIsArmor || distToObj <= 24.0f;
                }
                else if (board.HasActiveThreat && !board.ThreatIsArmor && distToThreat >= 12.0f && distToThreat <= maxThreatChargeDist)
                {
                    chargeTarget = board.ThreatPosition;
                    hasChargeTarget = true;
                }

                // Non-fanatical NCOs refuse to order a fresh Charge! right after losing men into a meatgrinder
                if (hasChargeTarget && !isFanaticalDoctrine && !isDesperateOvertime && board.DecayedRecentDeaths >= 1.40f)
                {
                    hasChargeTarget = false;
                }

                if (hasChargeTarget)
                {
                    float chargeCost = 1.72f * profile.BayonetBreachCostMult;
                    if (leaderPers == SoldierPersonality.Leader_Spearhead)
                        chargeCost *= 0.78f;
                    else if (leaderPers == SoldierPersonality.Leader_Tactician)
                        chargeCost *= 1.02f;
                    else if (leaderPers == SoldierPersonality.Leader_OldGuard)
                        chargeCost *= 1.38f;

                    bool isDesperateCap = false;
                    if (board.HasActiveObjectiveTask &&
                        board.IsDefendingObjective &&
                        board.ObjectiveCaptureProgress >= 0.55f &&
                        distToObj <= maxObjChargeDist)
                    {
                        isDesperateCap = true;
                        float urg01 = Mathf.InverseLerp(0.55f, 0.92f, board.ObjectiveCaptureProgress);
                        chargeCost *= Mathf.Lerp(0.76f, 0.44f, urg01);
                    }
                    else if (board.HasActiveObjectiveTask &&
                             !board.IsDefendingObjective &&
                             distToObj <= maxObjChargeDist)
                    {
                        if (board.ObjectiveCaptureProgress >= 0.60f)
                        {
                            chargeCost *= 0.72f;
                        }
                        else if (board.FriendlyUnitsInsideObjective == 0 && board.AliveMemberCount >= 5 && distToObj <= 24.0f)
                        {
                            chargeCost *= 0.84f;
                        }
                    }

                    if (board.IsInvaderFaction &&
                        board.TicketRatio01 <= 0.18f &&
                        board.HasActiveObjectiveTask &&
                        !board.IsDefendingObjective &&
                        distToObj <= maxObjChargeDist)
                    {
                        isDesperateCap = true;
                        chargeCost *= 0.68f;
                    }

                    bool hasSmokeCover = (now < board.SmokeActiveUntil &&
                                          (board.LastSmokeTargetPos - chargeTarget).sqrMagnitude <= 28.0f * 28.0f) ||
                                         Physics.Linecast(
                                             leaderPos + Vector3.up * 1.2f,
                                             chargeTarget + Vector3.up * 1.2f,
                                             AiCore.GetSmokeOcclusionMask());
                    if (hasSmokeCover)
                        chargeCost *= 0.68f;

                    bool enemyPinned = board.HasActiveThreat && board.IsThreatBeingPinned(now);
                    if (enemyPinned)
                        chargeCost *= 0.80f;

                    bool localTwoToOneOdds = board.NearbyFriendlyCount42m >= Mathf.Max(4, board.NearbyEnemyCount42m * 2);
                    bool hasTacticalJustification = isFanaticalDoctrine || isDesperateCap || hasSmokeCover || enemyPinned || localTwoToOneOdds;

                    if (board.NetDirective == SquadNetDirective.Hammer_WideFlankEnvelopment &&
                        Vector3.Distance(leaderPos, chargeTarget) <= 26.0f)
                    {
                        chargeCost *= 0.86f;
                    }

                    if (!isDesperateCap && leaderSupp >= 42.0f && !isFanaticalDoctrine)
                        chargeCost *= 1.55f;

                    if (hasTacticalJustification && chargeCost < bestCost)
                    {
                        bestCost = chargeCost;
                        bestAction = 1;
                    }
                }
            }

            // 2. Evaluate Covert Hold-Fire (sq.SetHoldFireOrder) — Stealth Hammer Envelopments, Tactician Infiltration, Ambush Doctrines
            if (!sq.HoldFire &&
                !sq.InChargeTime() &&
                !sq.IsEnemyAwareOfThisSquad &&
                leaderSupp < 5.0f &&
                !leader.isBleeding &&
                board.AliveMemberCount >= 3 &&
                distToThreat > 42.0f &&
                distToObj > 40.0f &&
                (distToThreat <= 135.0f || distToObj <= 135.0f) &&
                (!_nextAiCovertOrderTime.TryGetValue(sqPtr, out float nextCovT) || now >= nextCovT))
            {
                bool wantStealthFlank = board.NetDirective == SquadNetDirective.Hammer_WideFlankEnvelopment;
                bool wantTacticianInfil = leaderPers == SoldierPersonality.Leader_Tactician;
                bool wantDoctrineAmbush = profile.PreferAmbushWhenAlone && (!board.IsConnectedToRadioNet || board.IsDefendingObjective);

                if (wantStealthFlank || wantTacticianInfil || wantDoctrineAmbush)
                {
                    float covertCost = 1.26f * profile.FlankMoveCostMult;
                    if (wantStealthFlank)
                        covertCost *= 0.72f;
                    if (wantTacticianInfil)
                        covertCost *= 0.76f;
                    if (wantDoctrineAmbush)
                        covertCost *= 0.80f;
                    if (leaderPers == SoldierPersonality.Leader_Spearhead)
                        covertCost *= 1.20f;
                    if (board.IsDefendingObjective && board.ObjectiveCaptureProgress >= 0.25f)
                        covertCost *= 2.50f;

                    if (covertCost < bestCost)
                    {
                        bestCost = covertCost;
                        bestAction = 2;
                    }
                }
            }

            // 3. Evaluate Follow Column (sq.FollowLeader) — Fast Unengaged Rear-Echelon Approach (>105m Out of Contact)
            if (sq.order == Order.defend &&
                !sq.InChargeTime() &&
                !board.HasActiveThreat &&
                !sq.IsEnemyAwareOfThisSquad &&
                !board.IsDefendingObjective &&
                !_garrisonAssignedObjectiveBySquad.ContainsKey(sqPtr) &&
                distToObj >= 105.0f &&
                distToObj < 9000.0f &&
                leaderSupp < 2.0f &&
                (!_nextAiFollowOrderTime.TryGetValue(sqPtr, out float nextFolT) || now >= nextFolT))
            {
                const float followCost = 0.86f;
                if (followCost < bestCost)
                {
                    bestCost = followCost;
                    bestAction = 3;
                }
            }

            // 4. Evaluate Fighting Withdrawal / Regroup (sq.HoldArea + Smoke + 'Retreat!') — Mauled Squads, Outnumbered Pinned Squads, Uncounterable Close Armor
            if (VisionConfig.EnableTacticalRetreatsAndRegroup.Value &&
                !sq.InChargeTime() &&
                board.HasActiveThreat &&
                distToThreat <= 68.0f &&
                !AiCore.IsOnBridgeOrNarrowCrossing(leaderPos) &&
                !(board.IsDefendingObjective && board.ObjectiveCaptureProgress >= 0.55f) &&
                !(board.IsInvaderFaction && board.TicketRatio01 <= 0.18f) &&
                (!_nextAiSquadRetreatTime.TryGetValue(sqPtr, out float nextRetT) || now >= nextRetT))
            {
                bool noAtAgainstCloseTank = board.ThreatIsArmor && !board.SquadHasAntiTank && distToThreat <= 42.0f;
                bool fanaticalNoRetreat = profile.PreferAmbushWhenAlone && profile.BayonetBreachCostMult <= 0.66f && !noAtAgainstCloseTank;

                if (!fanaticalNoRetreat)
                {
                    bool netWithdrawal = board.NetDirective == SquadNetDirective.Regroup_SectorWithdrawal;
                    bool mauledRemnant = board.AliveMemberCount <= 2 &&
                                         leaderSupp >= 22.0f &&
                                         board.NearbyEnemyCount42m > board.NearbyFriendlyCount42m;
                    bool pinnedAndOverwhelmed = board.AliveMemberCount <= 3 &&
                                                leaderSupp >= 38.0f &&
                                                board.NearbyEnemyCount42m >= board.NearbyFriendlyCount42m + 4;

                    if ((netWithdrawal || noAtAgainstCloseTank || mauledRemnant || pinnedAndOverwhelmed) &&
                        TryComputeTacticalFallbackPosition(leader, sq, leaderPos, board.ThreatPosition, 24.0f, 44.0f, out retreatRallyPos))
                    {
                        float retreatCost = (netWithdrawal || noAtAgainstCloseTank) ? 0.52f : 0.72f;
                        if (leaderPers == SoldierPersonality.Leader_OldGuard)
                            retreatCost *= 0.80f;
                        else if (leaderPers == SoldierPersonality.Leader_Tactician)
                            retreatCost *= 0.88f;
                        else if (leaderPers == SoldierPersonality.Leader_Spearhead)
                            retreatCost *= 1.18f;

                        if (retreatCost < bestCost)
                        {
                            bestCost = retreatCost;
                            bestAction = 4;
                        }
                    }
                }
            }

            if (bestAction == 1)
            {
                if (!syncOnline || AiCore.TryConsumeBotRpcBudget(0.45f))
                {
                    if (_aiCovertHoldFireSquads.Remove(sqPtr))
                    {
                        _aiCovertHoldFireExpireTime.Remove(sqPtr);
                        try { sq.SetHoldFireOrder(false, false, false, syncOnline); } catch { }
                    }
                    _aiFollowColumnUntil.Remove(sqPtr);

                    try
                    {
                        sq.Charge(chargeTarget, 15.0f);
                        if (sq.timed_order_end < now + 14.0f)
                            sq.timed_order_end = now + 14.0f;
                    }
                    catch { }

                    _aiOrderedChargeSquads.Add(sqPtr);
                    _aiChargeBaselineDeaths[sqPtr] = board.DecayedRecentDeaths;
                    _nextAiSquadChargeTime[sqPtr] = now + 62.0f;
                    issuedSquadCoverOrderThisTick = true;
                    SquadChargesOrderedCount++;
                    GoapPlansExecutedCount++;

                    if (VisionConfig.DebugLogging.Value)
                    {
                        AiCore.LogDebug(
                            "SquadCharge",
                            $"{leader.faction} {leaderPers} ordered Charge! on '{sq.squadName}' (cost:{bestCost:F2}, capProg:{board.ObjectiveCaptureProgress:F2}, dist:{Vector3.Distance(leaderPos, chargeTarget):F0}m).",
                            3.0f);
                    }
                }
            }
            else if (bestAction == 2)
            {
                if (!syncOnline || AiCore.TryConsumeBotRpcBudget(0.40f))
                {
                    try
                    {
                        sq.SetHoldFireOrder(true, false, true, syncOnline);
                        _aiCovertHoldFireSquads.Add(sqPtr);
                        _aiCovertHoldFireExpireTime[sqPtr] = now + 20.0f;
                    }
                    catch { }

                    _nextAiCovertOrderTime[sqPtr] = now + 38.0f;
                    issuedSquadCoverOrderThisTick = true;
                    SquadCovertOrdersCount++;
                    GoapPlansExecutedCount++;

                    if (VisionConfig.DebugLogging.Value)
                    {
                        AiCore.LogDebug(
                            "SquadCovert",
                            $"{leader.faction} {leaderPers} ordered Covert Hold-Fire on '{sq.squadName}' (cost:{bestCost:F2}, directive:{board.NetDirective}).",
                            3.0f);
                    }
                }
            }
            else if (bestAction == 3)
            {
                if (!syncOnline || AiCore.TryConsumeBotRpcBudget(0.40f))
                {
                    try
                    {
                        sq.FollowLeader(false);
                        sq.timed_order_end = now + 12.0f;
                        _aiFollowColumnUntil[sqPtr] = now + 12.0f;
                    }
                    catch { }

                    _nextAiFollowOrderTime[sqPtr] = now + 32.0f;
                    issuedSquadCoverOrderThisTick = true;
                    SquadFollowOrdersCount++;
                    GoapPlansExecutedCount++;
                }
            }
            else if (bestAction == 4)
            {
                if (!syncOnline || AiCore.TryConsumeBotRpcBudget(0.45f))
                {
                    if (_aiCovertHoldFireSquads.Remove(sqPtr))
                    {
                        _aiCovertHoldFireExpireTime.Remove(sqPtr);
                        try { sq.SetHoldFireOrder(false, false, false, syncOnline); } catch { }
                    }
                    _aiFollowColumnUntil.Remove(sqPtr);

                    // Pop screening smoke before breaking contact if the leader or a nearby squadmate carries smoke
                    TryPopSquadRetreatSmoke(sq, leader, ref board, now);
                    TrySayTacticalVoice(leader, sq, VoiceManager.VoiceClip.timeToRetreat, 0.05f);

                    try
                    {
                        sq.HoldArea(retreatRallyPos, 14.0f, false);
                        sq.timed_order_end = now + 17.0f;
                    }
                    catch { }

                    _aiSquadRetreatUntil[sqPtr] = now + 17.0f;
                    _nextAiSquadRetreatTime[sqPtr] = now + 48.0f;
                    issuedSquadCoverOrderThisTick = true;
                    TacticalRetreatsCount++;
                    GoapPlansExecutedCount++;
                    BotActionTrace.RecordEvent(leader, BotTraceEventKind.SquadRetreat, board.ThreatIsArmor ? "squad_withdraw_armor" : "squad_withdraw_mauled", distToThreat);

                    if (VisionConfig.DebugLogging.Value)
                    {
                        AiCore.LogDebug(
                            "SquadRetreat",
                            $"{leader.faction} {leaderPers} ordered Fighting Withdrawal / Regroup on '{sq.squadName}' (alive:{board.AliveMemberCount}, armor:{board.ThreatIsArmor}, rallyDist:{Vector3.Distance(leaderPos, retreatRallyPos):F0}m).",
                            3.0f);
                    }
                }
            }
        }

        internal static bool HasUsableAntiTankCapability(Soldier u)
        {
            if (u == null || !u.IsAlive)
                return false;

            try
            {
                for (int slot = 0; slot <= 2; slot++)
                {
                    GenericGun g = u.GetHeldGun(slot);
                    if (g != null && g.IsAtWeapon() && (g.GetCurrentAmmoCount() > 0 || !u.IsOutOfAmmosFor(g)))
                        return true;
                }

                InventoryManager inv = u.inventory;
                if (inv != null)
                {
                    if (inv.FindItemOfType<VirtualATGrenade>() != null)
                        return true;

                    VirtualItem vGren = inv.FindItemOfType<VirtualGrenade>();
                    if (vGren != null && !string.IsNullOrEmpty(vGren.item_id))
                    {
                        string id = vGren.item_id;
                        if (id.IndexOf("molotov", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            id.IndexOf("satchel", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            id.IndexOf("geballte", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            id.IndexOf("hafthohl", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            id.IndexOf("gammon", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            return true;
                        }
                    }
                }
            }
            catch { }

            return false;
        }

        private static void TryPopSquadRetreatSmoke(Squad sq, Soldier leader, ref SquadBlackboard board, float now)
        {
            if (!board.HasActiveThreat || board.PrimaryTarget == null || now < board.SmokeActiveUntil)
                return;

            var units = sq.units;
            int count = units != null ? units.Count : 0;
            for (int i = -1; i < count; i++)
            {
                Soldier cand = (i < 0) ? leader : units[i];
                if (cand == null || !cand.IsAlive || cand.IsPlayer() || cand.HasSurrended() || cand.inventory == null || cand.transform == null)
                    continue;
                if (cand.isInHandsAnimation || cand.isInBodyAnimation || now <= cand.throw_end_time + 4.0f)
                    continue;

                VirtualItem smoke = cand.inventory.FindItemOfType<VirtualSmokeGrenade>();
                if (smoke == null)
                    continue;

                Vector3 cPos = cand.transform.position;
                float d = Vector3.Distance(cPos, board.ThreatPosition);
                if (d >= 8.0f && d <= cand.GetGrenadeThrowRange(smoke) + 8.0f &&
                    !AiCore.HasLowCeilingOverhead(cPos, 3.2f) &&
                    AiCore.TryConsumeBotRpcBudget(0.45f))
                {
                    _nextSmokeGrenadeTime[cand.Pointer] = now + 32.0f;
                    board.SmokeActiveUntil = now + 22.0f;
                    board.LastSmokeTargetPos = board.ThreatPosition;
                    cand.Throw(smoke, board.PrimaryTarget);
                    GoapSmokeScreensCount++;
                    return;
                }
            }
        }

        private static bool TryComputeTacticalFallbackPosition(
            Soldier soldier,
            Squad excludeSquad,
            Vector3 fromPos,
            Vector3 threatPos,
            float minDist,
            float maxDist,
            out Vector3 fallbackPos)
        {
            fallbackPos = fromPos;
            if (soldier == null || string.IsNullOrEmpty(soldier.faction))
                return false;

            Vector3 awayDir = fromPos - threatPos;
            awayDir.y = 0f;
            if (awayDir.sqrMagnitude < 0.05f)
            {
                awayDir = soldier.transform != null ? -soldier.transform.forward : Vector3.back;
                awayDir.y = 0f;
            }
            awayDir = awayDir.sqrMagnitude > 0.001f ? awayDir.normalized : Vector3.back;
            Vector3 threatDir = -awayDir;

            // 1. Prefer rallying onto a nearby intact friendly squad or friendly ground vehicle in the rear hemisphere!
            AiCore.EnsureWorldSnapshot();
            var squads = AiCore.WorldSquads;
            int sqCount = AiCore.WorldSquadCount;
            IntPtr exclPtr = excludeSquad != null ? excludeSquad.Pointer : IntPtr.Zero;
            float bestAllyScore = float.MaxValue;
            Vector3 bestAllyRally = Vector3.zero;
            bool foundAllyRally = false;

            string myFac = AiCore.GetCachedFaction(soldier);
            for (int i = 0; i < sqCount; i++)
            {
                ref AiCore.SquadSnapshot fSnap = ref squads[i];
                if (fSnap.Ptr == exclPtr || fSnap.UnitCount < 2 || fSnap.RefOnPlane || string.IsNullOrEmpty(fSnap.Faction))
                    continue;

                if (!AiCore.AreSameSideFactions(myFac, fSnap.Faction))
                    continue;

                Vector3 toAlly = fSnap.RefPos - fromPos;
                float d = new Vector2(toAlly.x, toAlly.z).magnitude;
                if (d < minDist * 0.80f || d > 90.0f || Mathf.Abs(toAlly.y) > 12.0f)
                    continue;

                Vector3 toAllyDir = new Vector3(toAlly.x, 0f, toAlly.z) / d;
                if (Vector3.Dot(toAllyDir, awayDir) < 0.15f)
                    continue;

                float score = d - (fSnap.LeaderOnVehicle ? 14.0f : 0f) - Mathf.Min(fSnap.UnitCount, 8) * 1.5f;
                if (score < bestAllyScore)
                {
                    bestAllyScore = score;
                    bestAllyRally = fSnap.RefPos + awayDir * 4.0f;
                    foundAllyRally = true;
                }
            }

            int envMask = AiCore.GetSolidEnvironmentMask();
            Vector3 searchAnchor = foundAllyRally
                ? Vector3.Lerp(fromPos, bestAllyRally, Mathf.Clamp01(maxDist / Mathf.Max(1f, Vector3.Distance(fromPos, bestAllyRally))))
                : (fromPos + awayDir * Mathf.Lerp(minDist, maxDist, 0.65f));

            // 2. Check CoverManager.staticCovers around the fallback anchor for sheltered rear cover
            var octree = CoverManager.staticCovers;
            if (octree != null)
            {
                var nearby = octree.GetNearby(searchAnchor, 14.0f);
                if (nearby != null && nearby.Length > 0)
                {
                    float bestCovDistSq = float.MaxValue;
                    for (int i = 0; i < nearby.Length; i++)
                    {
                        AiDestination d = nearby[i];
                        CombatCover cc = d != null ? d.TryCast<CombatCover>() : null;
                        if (cc == null)
                            continue;

                        Vector3 cPos = cc.GetCoverPosition();
                        if (Mathf.Abs(cPos.y - fromPos.y) > 6.5f)
                            continue;

                        Vector3 stepVec = cPos - fromPos;
                        stepVec.y = 0f;
                        if (stepVec.sqrMagnitude < (minDist * 0.65f) * (minDist * 0.65f) || Vector3.Dot(stepVec.normalized, awayDir) < 0.15f)
                            continue;

                        if (!cc.IsCoverAvailable(threatDir, soldier.faction))
                            continue;

                        float dSq = (cPos - searchAnchor).sqrMagnitude;
                        if (dSq < bestCovDistSq)
                        {
                            bestCovDistSq = dSq;
                            fallbackPos = cPos;
                        }
                    }
                    if (bestCovDistSq < float.MaxValue)
                        return true;
                }
            }

            // 3. Snap fallback anchor onto solid non-water ground
            Vector3 rayTop = new Vector3(searchAnchor.x, fromPos.y + 8.0f, searchAnchor.z);
            if (Physics.Raycast(rayTop, Vector3.down, out RaycastHit gHit, 20.0f, envMask, QueryTriggerInteraction.Ignore) &&
                gHit.normal.y >= 0.55f &&
                gHit.collider != null &&
                gHit.collider.gameObject.layer != 4)
            {
                try
                {
                    FollowingWater w = FollowingWater.instance;
                    if (w != null && w.Pointer != IntPtr.Zero && w.isActiveAndEnabled && gHit.point.y <= FollowingWater.WaterLevel + 0.15f)
                        return false;
                }
                catch { }

                fallbackPos = gHit.point;
                return true;
            }

            return false;
        }

        internal static bool TryTriggerImmediateIndividualFallback(Soldier soldier, Vector3 myPos)
        {
            if (soldier == null || soldier.Pointer == IntPtr.Zero || soldier.aiController == null || !AiCore.CanBotMoveAutonomously(soldier))
                return false;

            if (AiCore.IsOnBridgeOrNarrowCrossing(myPos))
                return false;

            IntPtr ptr = soldier.Pointer;
            float now = Time.time;
            if (_nextIndividualFallbackTime.TryGetValue(ptr, out float nextAllowed) && now < nextAllowed)
                return false;

            Squad sq = soldier.joinedSquad;
            Vector3 threatPos = myPos + (soldier.transform != null ? soldier.transform.forward * 15.0f : Vector3.forward * 15.0f);
            if (soldier.aiController.visibleTarget != null &&
                AiCore.TryGetSpottableCenter(soldier.aiController.visibleTarget, out Vector3 visPos))
            {
                threatPos = visPos;
            }
            else if (sq != null && _squadBlackboards.TryGetValue(sq.Pointer, out SquadBlackboard b) && b.HasActiveThreat)
            {
                threatPos = b.ThreatPosition;
            }

            if (!TryComputeTacticalFallbackPosition(soldier, sq, myPos, threatPos, 20.0f, 34.0f, out Vector3 fbDest))
                return false;

            // Pop personal smoke grenade if carried before sprinting back!
            if (soldier.inventory != null &&
                !soldier.isInHandsAnimation &&
                !soldier.isInBodyAnimation &&
                now > soldier.throw_end_time + 4.0f &&
                soldier.aiController.visibleTarget != null)
            {
                VirtualItem smoke = soldier.inventory.FindItemOfType<VirtualSmokeGrenade>();
                if (smoke != null &&
                    !AiCore.HasLowCeilingOverhead(myPos, 3.2f) &&
                    AiCore.TryConsumeBotRpcBudget(0.45f))
                {
                    _nextSmokeGrenadeTime[ptr] = now + 32.0f;
                    soldier.Throw(smoke, soldier.aiController.visibleTarget);
                    GoapSmokeScreensCount++;
                }
            }

            _individualFallbackDest[ptr] = fbDest;
            _individualFallbackUntil[ptr] = now + 8.5f;
            _nextIndividualFallbackTime[ptr] = now + 36.0f;
            _localCoverProtectedUntil[ptr] = now + 8.5f;

            SetBotPose(soldier.aiController, soldier, SoldierPose.Idle, true);
            AiCore.MoveBotTowardPosition(soldier.aiController, soldier, fbDest, 2.2f, 3.0f);
            TrySayTacticalVoice(soldier, sq, VoiceManager.VoiceClip.timeToRetreat, 0.05f);
            TacticalRetreatsCount++;
            GoapPlansExecutedCount++;
            BotActionTrace.RecordEvent(soldier, BotTraceEventKind.FallbackRetreat, "immediate_fallback");
            return true;
        }

        private static Squad FindBestAvailableHammerSquad(
            List<Squad> connectedInfantrySquads,
            int excludeIndex,
            IntPtr anvilSqPtr,
            string faction,
            IntPtr anvilTaskPtr,
            Vector3 threatPos,
            float maxDist,
            float now)
        {
            Squad bestHammerSq = null;
            float bestHammerDistSq = maxDist * maxDist;
            int count = connectedInfantrySquads.Count;

            for (int j = 0; j < count; j++)
            {
                if (j == excludeIndex)
                    continue;

                Squad candSq = connectedInfantrySquads[j];
                if (candSq == null || candSq.Pointer == anvilSqPtr || IsSquadActivelyRetreating(candSq.Pointer, now))
                    continue;
                Soldier candLeader = candSq.Leader;
                if (candLeader == null || candLeader.faction != faction || candLeader.transform == null)
                    continue;

                if (candLeader.Params != null && (!candLeader.Params.canGiveOrders || !candLeader.Params.canMove))
                    continue;

                if (_squadBlackboards.TryGetValue(candSq.Pointer, out SquadBlackboard candBoard) &&
                    now < candBoard.NetDirectiveExpireTime)
                {
                    continue;
                }

                float dToThreatSq = (candLeader.transform.position - threatPos).sqrMagnitude;
                if (dToThreatSq >= bestHammerDistSq || dToThreatSq <= 15f * 15f)
                    continue;

                // Never pull a squad away from an unrelated objective across the map to flank a distant skirmish
                if (candSq.currentTask != null && candSq.currentTask.Pointer != IntPtr.Zero)
                {
                    bool sameObjective = anvilTaskPtr != IntPtr.Zero && candSq.currentTask.Pointer == anvilTaskPtr;
                    if (!sameObjective && dToThreatSq > 60f * 60f)
                    {
                        float taskToThreatSq = (candSq.currentTask.GetTaskDestination() - threatPos).sqrMagnitude;
                        if (taskToThreatSq > 85f * 85f)
                            continue;
                    }
                }

                bestHammerDistSq = dToThreatSq;
                bestHammerSq = candSq;
            }

            return bestHammerSq;
        }

        private static void TryTriggerSquadRadioSupport(Squad sq, Soldier leader)
        {
            if (sq != null &&
                leader != null &&
                (leader.Params == null || leader.Params.canUseRadio) &&
                sq.HasRadioman() &&
                !sq.RadioRequestActive() &&
                sq.SomeTimePassedSinceLastRadioRequest() &&
                leader.aiController != null)
            {
                try
                {
                    sq.TryAssignRadioOrder(leader.aiController, leader);
                }
                catch { }
            }
        }

        private static void IssueHammerFlankDirective(
            Squad hammerSq,
            Vector3 anvilPos,
            Vector3 threatPos,
            Vector3 axisDir,
            Vector3 perpRight,
            float now,
            string anvilLabel)
        {
            Soldier hammerLeader = hammerSq.Leader;
            if (hammerLeader == null || hammerLeader.transform == null)
                return;

            SquadBlackboard hammerBoard = GetOrUpdateSquadBlackboard(hammerSq, hammerLeader);
            hammerBoard.SuggestedNetDirective = SquadNetDirective.Hammer_WideFlankEnvelopment;
            hammerBoard.SuggestedDirectiveExpireTime = now + 24.0f;
            hammerBoard.SuggestedDirectiveTargetPos = threatPos;
            hammerBoard.SuggestedPincerTaskPtr = IntPtr.Zero;
            _squadBlackboards[hammerSq.Pointer] = hammerBoard;
            ConsumeInitialHqSpawnBriefingIfNoRadio(hammerSq);

            if (VisionConfig.DebugLogging.Value)
            {
                AiCore.LogDebug(
                    "TeamNetGOAP",
                    $"{hammerLeader.faction} Radio Net suggested {anvilLabel} + Hammer wide flank to '{hammerSq.squadName}'.");
            }
        }

        internal static void NotifySquadMemberKilled(Soldier victim)
        {
            if (victim == null || victim.Pointer == IntPtr.Zero)
                return;

            Squad sq = victim.joinedSquad;
            if (sq == null || sq.Pointer == IntPtr.Zero || AiCore.IsTrueVehicleCrewSquad(sq))
                return;

            Transform vTr = victim.transform;
            if (vTr == null)
                return;

            IntPtr sqPtr = sq.Pointer;
            Vector3 deathPos = vTr.position;
            float now = Time.time;

            _squadBlackboards.TryGetValue(sqPtr, out SquadBlackboard board);
            float dt = board.LastDeathDecayUpdateTime > 0f ? Mathf.Max(0f, now - board.LastDeathDecayUpdateTime) : 0f;
            board.DecayedRecentDeaths = board.DecayedRecentDeaths * Mathf.Exp(-dt * 0.05f) + 1.0f;
            board.LastDeathDecayUpdateTime = now;
            board.LastCasualtyPos = deathPos;
            board.LastCasualtyTime = now;
            _squadBlackboards[sqPtr] = board;

            // Nearby locally owned AI squadmates (<= 14m) witness their comrade drop and immediately re-evaluate their plan!
            var units = sq.units;
            if (units == null)
                return;

            IntPtr victimPtr = victim.Pointer;
            int count = units.Count;
            for (int i = 0; i < count; i++)
            {
                Soldier ally = units[i];
                if (ReferenceEquals(ally, null) || ally == null || ally.Pointer == victimPtr || !ally.IsAlive || !ally.IsAI() || !AiCore.IsLocallyOwnedBot(ally))
                    continue;

                AiCore.SoldierFastHandles ah = AiCore.GetOrCreateSoldierHandles(ally);
                if (ah == null || ah.Tr == null)
                    continue;

                if ((ah.Tr.position - deathPos).sqrMagnitude > 14.0f * 14.0f)
                    continue;

                float startle = 0.14f + (AiCore.GetDeterministicHash(ah.Ptr, victimPtr.GetHashCode()) & 15) * 0.018f;
                if (_botGoapPlans.TryGetValue(ah.Ptr, out BotGoapPlan plan))
                {
                    plan.NextReplanTime = Mathf.Min(plan.NextReplanTime, now + startle);
                    _botGoapPlans[ah.Ptr] = plan;
                }

                if (!ally.IsOnCover() && _fireAndMovementStates.TryGetValue(ah.Ptr, out FireAndMovementState fm) && !fm.InFiringHalt)
                {
                    fm.InFiringHalt = true;
                    fm.PhaseStartTime = now;
                    fm.PhaseEndTime = now + 1.65f;
                    fm.HaltLostSightTime = 0f;
                    _fireAndMovementStates[ah.Ptr] = fm;
                }
            }
        }

        // =====================================================================
        // Tier 2: Squad Tactical Blackboard (Computed 1x per Squad per 0.6s)
        // =====================================================================
        internal static SquadBlackboard GetOrUpdateSquadBlackboard(Squad sq, Soldier fallbackSoldier)
        {
            if (sq == null || sq.Pointer == IntPtr.Zero)
            {
                return default;
            }

            IntPtr sqPtr = sq.Pointer;
            float now = Time.time;
            if (_squadBlackboards.TryGetValue(sqPtr, out SquadBlackboard board) && now < board.NextRefreshTime)
            {
                return board;
            }

            long profBoardStart = HitchProfiler.Begin();
            try
            {
            board.NextRefreshTime = now + 0.60f;
            if (board.LastDeathDecayUpdateTime > 0f)
            {
                float deathDt = Mathf.Max(0f, now - board.LastDeathDecayUpdateTime);
                board.DecayedRecentDeaths *= Mathf.Exp(-deathDt * 0.05f);
                if (board.DecayedRecentDeaths < 0.05f)
                    board.DecayedRecentDeaths = 0f;
            }
            board.LastDeathDecayUpdateTime = now;

            Soldier leader = sq.Leader != null && sq.Leader.IsAlive ? sq.Leader : fallbackSoldier;
            if (leader == null || leader.transform == null)
            {
                _squadBlackboards[sqPtr] = board;
                return board;
            }

            Vector3 sqCenter = leader.transform.position;
            board.SquadCenter = sqCenter;

            // Check Radioman status & detect sudden loss of squad Radioman
            Soldier rad = sq.GetRadioman();
            bool hasAliveRadNow = rad != null && rad.IsAlive && !rad.HasSurrended();
            if (board.PreviouslyHadAliveRadioman && !hasAliveRadNow)
            {
                board.PreviouslyHadAliveRadioman = false;
                board.NetDirective = SquadNetDirective.Disconnected_Autonomous;
                RadioNetCutoffsCount++;
                TrySayTacticalVoice(leader, sq, VoiceManager.VoiceClip.radiomanIsDead, 0.35f);
                if (VisionConfig.DebugLogging.Value)
                {
                    AiCore.LogDebug("RadioNet", $"{leader.faction} squad '{sq.squadName}' lost its Radioman! Cut off from distant Team Net.");
                }
            }
            else if (hasAliveRadNow)
            {
                board.PreviouslyHadAliveRadioman = true;
            }

            board.IsConnectedToRadioNet = IsSquadConnectedToRadioNet(sq);
            if (!board.IsConnectedToRadioNet)
            {
                board.NetDirective = SquadNetDirective.Disconnected_Autonomous;
            }

            // Populate Macro Objective & Dynamic Operations Ticket / Attrition state
            board.IsInvaderFaction = false;
            board.TicketRatio01 = 1.0f;
            board.HasActiveObjectiveTask = false;
            board.IsDefendingObjective = false;
            board.ObjectiveCaptureProgress = 0f;

            try
            {
                BattleData bd = BattleManager.GetCurrentBattleData();
                if (ConquestEconomy.IsActive && !string.IsNullOrEmpty(leader.faction))
                {
                    int budget = ConquestEconomy.GetBudget(leader.faction);
                    board.TicketRatio01 = Mathf.Clamp01(budget / 350.0f);
                }
                else if (bd != null && !string.IsNullOrEmpty(leader.faction))
                {
                    board.IsInvaderFaction = bd.IsInvaderFaction(leader.faction);
                    if (board.IsInvaderFaction && bd.invadersTickets > 0)
                    {
                        short remTickets = bd.GetRemainingInvaderTickets();
                        board.TicketRatio01 = Mathf.Clamp01(remTickets / Mathf.Max(40f, (float)bd.invadersTickets));
                    }
                }

                SquadTask curTask = sq.currentTask;
                if (curTask != null && curTask.Pointer != IntPtr.Zero)
                {
                    board.HasActiveObjectiveTask = true;
                    board.ObjectivePosition = curTask.GetTaskDestination();
                    AiCore.EvaluateSquadTaskForFaction(
                        curTask,
                        leader.faction,
                        bd,
                        _garrisonAssignedObjectiveBySquad.ContainsKey(sqPtr),
                        out board.IsDefendingObjective,
                        out board.ObjectiveCaptureProgress);
                    if (ConquestEconomy.IsActive)
                    {
                        board.IsInvaderFaction = !board.IsDefendingObjective;
                    }
                }
            }
            catch { }

            // Scan squad members once to find active threat, alive count, MG/Medic/AT presence & surrendered comrades
            board.HasActiveThreat = false;
            board.ThreatIsArmor = false;
            board.HasAliveMachineGunner = false;
            board.HasAliveMedic = false;
            board.SquadHasAntiTank = false;
            board.PrimaryTarget = null;
            board.SurrenderedComrade = null;
            board.AliveMemberCount = 0;
            board.NearbyEnemyCount42m = 0;
            board.NearbyFriendlyCount42m = 0;
            board.FriendlyUnitsInsideObjective = 0;
            board.EnemyUnitsInsideObjective = 0;

            float bestThreatScore = -999f;
            var units = sq.units;
            if (units != null)
            {
                int uCount = units.Count;
                for (int i = 0; i < uCount; i++)
                {
                    Soldier u = units[i];
                    if (u == null || !u.IsAlive || u.transform == null)
                        continue;

                    if ( u.HasSurrended())
                    {
                        if (board.SurrenderedComrade == null && Mathf.Abs(u.transform.position.y - sqCenter.y) <= 2.4f)
                        {
                            board.SurrenderedComrade = u;
                        }
                        continue;
                    }

                    board.AliveMemberCount++;
                    if (u.hasMg || u.IsGunner() || u.GetHeldFunctionalBipod() != null)
                    {
                        board.HasAliveMachineGunner = true;
                    }
                    if (u.hasSyringe)
                    {
                        board.HasAliveMedic = true;
                    }
                    if (!board.SquadHasAntiTank && HasUsableAntiTankCapability(u))
                    {
                        board.SquadHasAntiTank = true;
                    }

                    SoldierAI uAi = u.aiController;
                    Spottable vt = null;
                    Vector3 tPos = Vector3.zero;
                    if (uAi != null && AiCore.TryGetSpottableCenter(uAi.visibleTarget, out tPos))
                    {
                        vt = uAi.visibleTarget;
                    }
                    else if (u.IsOnVehicle())
                    {
                        // Seated vehicle crew disable SoldierAI and aim via TurretAI.CurrentVisibleTarget
                        Vehicle curVeh = u.GetCurrentVehicle();
                        Turret mt = curVeh != null ? curVeh.GetMainTurret(false) : null;
                        Spottable turretVt = (mt != null && mt.autoPilot != null) ? mt.autoPilot.CurrentVisibleTarget : null;
                        if (turretVt != null && AiCore.TryGetSpottableCenter(turretVt, out tPos))
                        {
                            vt = turretVt;
                        }
                    }

                    if (vt != null)
                    {
                        float dist = Vector3.Distance(sqCenter, tPos);
                        bool isArmor = vt.TryCast<VehicleTank>() != null;
                        float score = 200f - dist + (isArmor ? 35f : 0f);
                        if (score > bestThreatScore)
                        {
                            bestThreatScore = score;
                            board.HasActiveThreat = true;
                            board.ThreatIsArmor = isArmor;
                            board.PrimaryTarget = vt;
                            board.ThreatPosition = tPos;
                        }
                    }
                    else if (_lastKnownTargets.TryGetValue(u.Pointer, out LastKnownTargetInfo lk) &&
                             now - lk.LastSeenTime <= 6.5f)
                    {
                        float dist = Vector3.Distance(sqCenter, lk.LastSeenPos);
                        float score = 120f - dist;
                        if (score > bestThreatScore)
                        {
                            bestThreatScore = score;
                            board.HasActiveThreat = true;
                            board.ThreatIsArmor = false;
                            board.PrimaryTarget = lk.Target;
                            board.ThreatPosition = lk.LastSeenPos;
                        }
                    }

                    // If a Machine Gunner in the squad is actively firing at a visible enemy, mark threat as pinned
                    if (uAi != null && uAi.HasEnemyAndCanFireAtHim && (u.hasMg || u.IsGunner() || u.UsingBipod()))
                    {
                        board.Suppressor0Ptr = u.Pointer;
                        if (board.SuppressHoldUntil < now + 1.5f)
                            board.SuppressHoldUntil = now + 1.5f;
                    }
                }
            }

            // Single flat-array pass over AiCore.WorldSquads/WorldSoldiers (0.60s per squad) to compute:
            //  1) Local 42m force ratio (NearbyFriendlyCount42m vs NearbyEnemyCount42m) for Tactical Retreat decisions
            //  2) Active capture-zone headcount (FriendlyUnitsInsideObjective vs EnemyUnitsInsideObjective) so squads
            //     know whether they actually have capture majority inside the circle!
            if (!string.IsNullOrEmpty(leader.faction))
            {
                AiCore.EnsureWorldSnapshot();
                var wSquads = AiCore.WorldSquads;
                var wSoldiers = AiCore.WorldSoldiers;
                int wSqCount = AiCore.WorldSquadCount;

                bool countObjCircle = board.HasActiveObjectiveTask &&
                                      sq.currentTask != null &&
                                      (sqCenter - board.ObjectivePosition).sqrMagnitude <= 130.0f * 130.0f;
                float objRad = countObjCircle ? Mathf.Max(10.0f, sq.currentTask.GetTaskAreaRadius()) : 0f;
                float objRadSq = objRad * objRad;
                Vector3 objCenter = board.ObjectivePosition;
                const float localR42Sq = 42.0f * 42.0f;
                const float coarseSqRadius = 90.0f * 90.0f;

                string leaderFac = AiCore.GetCachedFaction(leader);
                for (int s = 0; s < wSqCount; s++)
                {
                    ref AiCore.SquadSnapshot sSnap = ref wSquads[s];
                    if (sSnap.UnitCount == 0 || sSnap.RefOnPlane || string.IsNullOrEmpty(sSnap.Faction))
                        continue;

                    bool nearSquad = (sSnap.RefPos - sqCenter).sqrMagnitude <= coarseSqRadius;
                    bool nearObj = countObjCircle && (sSnap.RefPos - objCenter).sqrMagnitude <= (objRad + 55.0f) * (objRad + 55.0f);
                    if (!nearSquad && !nearObj)
                        continue;

                    bool sameSide = AiCore.AreSameSideFactions(leaderFac, sSnap.Faction);
                    int end = sSnap.UnitStartIndex + sSnap.UnitCount;
                    for (int u = sSnap.UnitStartIndex; u < end; u++)
                    {
                        Vector3 uPos = wSoldiers[u].Pos;
                        if (nearSquad)
                        {
                            Vector3 dL = uPos - sqCenter;
                            if (dL.x * dL.x + dL.z * dL.z <= localR42Sq && Mathf.Abs(dL.y) <= 14.0f)
                            {
                                if (sameSide) board.NearbyFriendlyCount42m++;
                                else board.NearbyEnemyCount42m++;
                            }
                        }

                        if (nearObj)
                        {
                            Vector3 dO = uPos - objCenter;
                            if (dO.x * dO.x + dO.z * dO.z <= objRadSq && Mathf.Abs(dO.y) <= 16.0f)
                            {
                                if (sameSide) board.FriendlyUnitsInsideObjective++;
                                else board.EnemyUnitsInsideObjective++;
                            }
                        }
                    }
                }
            }

            // Vanilla Soldier.Surrender() detaches the soldier from sq.units (joinedSquad = null), so scan
            // AiCore.WorldSoldiers (O(N) flat struct array) for any surrendered friendly comrade within LiberateSearchRadius!
            if (board.SurrenderedComrade == null &&
                VisionConfig.EnableComradeLiberation.Value &&
                !string.IsNullOrEmpty(leader.faction))
            {
                AiCore.EnsureWorldSnapshot();
                int solCount = AiCore.WorldSoldierCount;
                var soldiers = AiCore.WorldSoldiers;
                float maxLibR = Mathf.Max(16.0f, VisionConfig.LiberateSearchRadius.Value);
                float bestSurrDistSq = maxLibR * maxLibR;

                for (int i = 0; i < solCount; i++)
                {
                    ref AiCore.SoldierSnapshot sSnap = ref soldiers[i];
                    if (!sSnap.HasSurrendered || sSnap.Faction != leader.faction || sSnap.Ref == null)
                        continue;

                    if (Mathf.Abs(sSnap.Pos.y - sqCenter.y) > 2.8f)
                        continue;

                    float dSq = (sSnap.Pos - sqCenter).sqrMagnitude;
                    if (dSq < bestSurrDistSq)
                    {
                        bestSurrDistSq = dSq;
                        board.SurrenderedComrade = sSnap.Ref;
                    }
                }
            }

            if (board.HasActiveThreat)
            {
                Vector3 toThreat = board.ThreatPosition - sqCenter;
                toThreat.y = 0f;
                board.ThreatDirection = toThreat.sqrMagnitude > 0.01f ? toThreat.normalized : leader.transform.forward;

                if (!leader.IsOnVehicle() &&
                    (now >= board.NextFlankCoverScanTime ||
                     (now >= board.FlankUntil && board.FlankLeftCover == null && board.FlankRightCover == null)))
                {
                    // Cap flank octree scans to at most 1 per frame globally (and never on a SquadLeaderRoutine/snapshot frame)
                    if (AiCore.IsHeavyWorkFrame() || _lastFlankCoverOctreeFrame == Time.frameCount)
                    {
                        board.NextFlankCoverScanTime = now + 0.25f;
                    }
                    else
                    {
                        _lastFlankCoverOctreeFrame = Time.frameCount;
                        board.NextFlankCoverScanTime = now + 4.5f;
                        CombatCover prevLeft = (now < board.FlankUntil && board.FlankerLeftPtr != IntPtr.Zero) ? board.FlankLeftCover : null;
                        CombatCover prevRight = (now < board.FlankUntil && board.FlankerRightPtr != IntPtr.Zero) ? board.FlankRightCover : null;
                        FindSquadFlankCovers(sqCenter, board.ThreatPosition, board.ThreatDirection, leader.faction, out CombatCover newLeft, out CombatCover newRight);
                        board.FlankLeftCover = prevLeft != null ? prevLeft : newLeft;
                        board.FlankRightCover = prevRight != null ? prevRight : newRight;
                    }
                }
            }
            else
            {
                board.ThreatDirection = GetEffectiveThreatDirection(leader, sq, sqCenter, leader.transform.forward);
                board.FlankLeftCover = null;
                board.FlankRightCover = null;
            }

            if (now >= board.SuppressHoldUntil)
            {
                board.Suppressor0Ptr = IntPtr.Zero;
                board.Suppressor1Ptr = IntPtr.Zero;
            }
            if (now >= board.FlankUntil)
            {
                board.FlankerLeftPtr = IntPtr.Zero;
                board.FlankerRightPtr = IntPtr.Zero;
            }
            if (now >= board.BreachUntil)
            {
                board.BreacherPtr = IntPtr.Zero;
            }
            if (now >= board.RescueUntil || board.SurrenderedComrade == null || !board.SurrenderedComrade.HasSurrended())
            {
                board.RescuerPtr = IntPtr.Zero;
            }
            if (now >= board.DragClaimUntil)
            {
                board.DragRescuerPtr = IntPtr.Zero;
                board.DragPatientPtr = IntPtr.Zero;
            }

            _squadBlackboards[sqPtr] = board;
            return board;
            }
            finally
            {
                HitchProfiler.End(HitchBucket.GoapSquadBoard, profBoardStart);
            }
        }

        private static int _lastFlankCoverOctreeFrame = -1;

        private static void FindSquadFlankCovers(
            Vector3 squadCenter,
            Vector3 threatPos,
            Vector3 threatDir,
            string faction,
            out CombatCover leftFlankCover,
            out CombatCover rightFlankCover)
        {
            long profFlankStart = HitchProfiler.Begin();
            try
            {
            leftFlankCover = null;
            rightFlankCover = null;

            var octree = CoverManager.staticCovers;
            if (octree == null)
                return;

            Vector3 midPoint = Vector3.Lerp(squadCenter, threatPos, 0.45f);
            var nearby = octree.GetNearby(midPoint, 22.0f);
            if (nearby == null || nearby.Length == 0)
                return;

            Vector3 rightPerp = new Vector3(threatDir.z, 0f, -threatDir.x);
            float bestLeftScore = -999f;
            float bestRightScore = -999f;

            int count = nearby.Length;
            for (int i = 0; i < count; i++)
            {
                AiDestination dest = nearby[i];
                if (dest == null || dest.Pointer == IntPtr.Zero)
                    continue;

                CombatCover cc = dest.TryCast<CombatCover>();
                if (cc == null)
                    continue;

                Vector3 covPos = cc.GetCoverPosition();
                if (Mathf.Abs(covPos.y - squadCenter.y) > 3.2f)
                    continue;

                Vector3 toThreatFromCov = threatPos - covPos;
                toThreatFromCov.y = 0f;
                float distToThreat = toThreatFromCov.magnitude;
                if (distToThreat < 6.5f || distToThreat > 45.0f)
                    continue;

                Vector3 covToThreatDir = toThreatFromCov / distToThreat;
                float angleOffAxis = Vector3.Angle(threatDir, covToThreatDir);
                if (angleOffAxis < 18f || angleOffAxis > 75f)
                    continue;

                if (!cc.IsCoverAvailable(covToThreatDir, faction))
                    continue;

                float side = Vector3.Dot(covPos - squadCenter, rightPerp);
                float score = angleOffAxis * 0.5f - Mathf.Abs(distToThreat - 18f) * 0.4f;
                if (side < -2.5f && score > bestLeftScore)
                {
                    bestLeftScore = score;
                    leftFlankCover = cc;
                }
                else if (side > 2.5f && score > bestRightScore)
                {
                    bestRightScore = score;
                    rightFlankCover = cc;
                }
            }
            }
            finally
            {
                HitchProfiler.End(HitchBucket.GoapFlankCovers, profFlankStart);
            }
        }

        // =====================================================================
        // Tier 1: Individual Bot 32-Bit WorldState & Bounded 2-Step GOAP Planner
        // =====================================================================
        internal static bool HasActiveGoapCoverLock(IntPtr soldierPtr, float now)
        {
            return _botGoapPlans.TryGetValue(soldierPtr, out BotGoapPlan plan) &&
                   plan.LockUnitCoverChange &&
                   now < plan.PlanExpireTime;
        }

        internal static bool IsSquadActivelyRetreating(IntPtr sqPtr, float now)
        {
            return sqPtr != IntPtr.Zero &&
                   _aiSquadRetreatUntil.TryGetValue(sqPtr, out float retUntil) &&
                   now < retUntil;
        }

        internal static bool IsInIndividualOrGoapFallback(IntPtr soldierPtr, float now)
        {
            if (soldierPtr == IntPtr.Zero)
                return false;

            if (_individualFallbackUntil.TryGetValue(soldierPtr, out float fbUntil) && now < fbUntil)
                return true;

            if (_botGoapPlans.TryGetValue(soldierPtr, out BotGoapPlan plan) && now < plan.PlanExpireTime)
            {
                if (plan.IsEvacuatingBailedCrew)
                    return true;

                GoapActionId cur = plan.StepIndex == 0 ? plan.Step0 : plan.Step1;
                if (cur == GoapActionId.TacticalFallbackRetreat ||
                    (cur == GoapActionId.ThrowSmokeScreen && plan.StepCount > 1 && plan.Step1 == GoapActionId.TacticalFallbackRetreat))
                {
                    return true;
                }
            }

            return false;
        }

        internal static bool HasPendingAtWeaponScavenge(IntPtr soldierPtr, Vector3 myPos, float maxDist = 24.0f)
        {
            return soldierPtr != IntPtr.Zero &&
                   _botScavengeCaches.TryGetValue(soldierPtr, out BotScavengeCache sc) &&
                   sc.IsAtWeaponPickup &&
                   (sc.TargetFloorItem != null || sc.TargetRefillCrate != null || sc.TargetInventory != null) &&
                   (sc.TargetPosition - myPos).sqrMagnitude <= maxDist * maxDist;
        }

        internal static bool TryGetProtectedBotMoveOverride(IntPtr soldierPtr, float now, out Vector3 overrideDest)
        {
            overrideDest = Vector3.zero;
            if (soldierPtr == IntPtr.Zero)
                return false;

            if (_individualFallbackUntil.TryGetValue(soldierPtr, out float fbUntil) &&
                now < fbUntil &&
                _individualFallbackDest.TryGetValue(soldierPtr, out overrideDest))
            {
                return true;
            }

            if (_casualtyDragStates.TryGetValue(soldierPtr, out CasualtyDragState drag) &&
                now < drag.DragEndTime &&
                drag.Patient != null &&
                !drag.Patient.IsDead)
            {
                overrideDest = drag.DropSpot;
                return true;
            }

            if (_botGoapPlans.TryGetValue(soldierPtr, out BotGoapPlan plan) &&
                now < plan.PlanExpireTime &&
                plan.HasMoveOverride)
            {
                overrideDest = plan.MoveOverrideDest;
                return true;
            }

            return false;
        }

        internal static void ExecuteSoldierGoapTick(
            SoldierAI ai,
            Soldier soldier,
            SoldierPersonality personality,
            float vet01,
            float suppression,
            float dist,
            bool isGunner,
            bool isOnBridge,
            bool inPointBlankMelee,
            bool isEvadingArmor,
            bool isStaffingEmplacement)
        {
            if (!VisionConfig.EnableTacticalGoapPlanner.Value ||
                ai == null ||
                soldier == null ||
                soldier.IsOnVehicle() ||
                soldier.HasSurrended() ||
                soldier.isInBodyAnimation ||
                isStaffingEmplacement)
            {
                return;
            }

            if (soldier.IsCharging())
            {
                Squad cSq = soldier.joinedSquad;
                bool scriptedCharge = cSq != null && cSq.isChargeSquad;
                FactionCommandProfile cProf = GetFactionCommandProfile(soldier.faction);
                BotMicroVariance cMicro = BotMicroVariance.FromSoldier(soldier.Pointer);
                bool fanatical = scriptedCharge || cProf.BayonetBreachCostMult <= 0.66f;

                float personalBalkSupp = Mathf.Lerp(36.0f, 46.0f, vet01) / Mathf.Max(0.75f, cMicro.CautionCostMult);
                if (personality == SoldierPersonality.Assaulter_Shock || personality == SoldierPersonality.Leader_Spearhead)
                    personalBalkSupp += 12.0f;
                else if (personality == SoldierPersonality.Rifleman_Cautious || personality == SoldierPersonality.Medic_Triage)
                    personalBalkSupp -= 8.0f;

                bool comradeDroppedNearby = cSq != null &&
                                            _squadBlackboards.TryGetValue(cSq.Pointer, out SquadBlackboard cb) &&
                                            cb.LastCasualtyTime > 0f &&
                                            (Time.time - cb.LastCasualtyTime) <= 4.5f &&
                                            soldier.transform != null &&
                                            (soldier.transform.position - cb.LastCasualtyPos).sqrMagnitude <= 11.5f * 11.5f;

                bool balkFromCharge = !fanatical &&
                                      dist > 12.0f &&
                                      (soldier.isBleeding ||
                                       suppression >= personalBalkSupp ||
                                       (comradeDroppedNearby && suppression >= 18.0f));
                if (!balkFromCharge)
                    return;

                if (!soldier.IsOnCover())
                {
                    if (soldier.HasDestinationAssigned &&
                        _localCoverProtectedUntil.TryGetValue(soldier.Pointer, out float balkCovUntil) &&
                        Time.time < balkCovUntil)
                    {
                        return;
                    }

                    if (AiCore.CanBotMoveAutonomously(soldier) && TrySeekNearbyCover(ai, soldier))
                    {
                        _localCoverProtectedUntil[soldier.Pointer] = Time.time + 5.5f;
                        IndividualSelfPreservationBalksCount++;
                        BotActionTrace.RecordEvent(soldier, BotTraceEventKind.SelfBalk, "charge_balk_cover", dist);
                        return;
                    }

                    if (AiCore.CanBotChangePoseAutonomously(soldier) && soldier.m_pose == SoldierPose.Idle)
                    {
                        SetBotPose(ai, soldier, suppression >= personalBalkSupp + 14.0f ? SoldierPose.Prone : SoldierPose.Crouch, true);
                        IndividualSelfPreservationBalksCount++;
                        BotActionTrace.RecordEvent(soldier, BotTraceEventKind.SelfBalk, "charge_balk_drop", dist);
                    }
                }
                // Once in cover (or if no cover exists nearby), fall through to GOAP so bleeding/pinned bots
                // can BandageInCover, ThrowSmokeScreen, Suppress, or TacticalFallbackRetreat instead of freezing!
            }

            bool canMoveAutonomously = AiCore.CanBotMoveAutonomously(soldier);
            if (!canMoveAutonomously && !AiCore.CanBotChangePoseAutonomously(soldier))
            {
                return;
            }

            long profGoapStart = HitchProfiler.Begin();
            try
            {
            IntPtr ptr = soldier.Pointer;
            float now = Time.time;
            Squad sq = soldier.joinedSquad;
            SquadBlackboard board = GetOrUpdateSquadBlackboard(sq, soldier);

            if (canMoveAutonomously &&
                !inPointBlankMelee &&
                !isOnBridge &&
                soldier.transform != null &&
                TryRecoverChronicallyStuckFootBot(ai, soldier, ptr, soldier.transform.position, suppression, now))
            {
                if (sq != null && sq.Pointer != IntPtr.Zero)
                    _squadBlackboards[sq.Pointer] = board;
                return;
            }

            // Emergent Buddy Casualty Dragging (Soldier.CarryBodySynched):
            // If this soldier is currently carrying a wounded buddy to cover/Medic (or spots an unclaimed wounded buddy
            // in the open nearby while not bleeding, evading armor, or in point-blank combat), handle the carry & drop-off cleanly!
            if (canMoveAutonomously &&
                VisionConfig.EnableBuddyCasualtyDragging.Value &&
                !inPointBlankMelee &&
                !isOnBridge &&
                !isEvadingArmor &&
                soldier.transform != null &&
                TryExecuteBuddyCasualtyDrag(ai, soldier, sq, ref board, soldier.transform.position, now))
            {
                if (sq != null && sq.Pointer != IntPtr.Zero)
                    _squadBlackboards[sq.Pointer] = board;
                return;
            }

            // Build 32-Bit WorldState in O(1)
            GoapStateBits ws = BuildSoldierWorldState(
                ai,
                soldier,
                sq,
                ref board,
                personality,
                suppression,
                dist,
                isGunner,
                isOnBridge,
                inPointBlankMelee,
                isEvadingArmor,
                now);

            bool hasActivePlan = _botGoapPlans.TryGetValue(ptr, out BotGoapPlan plan) && now < plan.PlanExpireTime;
            bool nonBleedingBailedCrew =
                IsSoldierBailedOutVehicleCrew(soldier, sq) &&
                (ws & (GoapStateBits.IsBleeding | GoapStateBits.InPointBlankRange | GoapStateBits.CanFireAtVisibleEnemy)) == 0;
            bool emergencyPreempt =
                ((ws & GoapStateBits.InPointBlankRange) != 0 && plan.Step0 != GoapActionId.PointBlankBayonetBreach) ||
                ((ws & GoapStateBits.IsBleeding) != 0 &&
                 (ws & GoapStateBits.BandageUnsafeUnderFire) == 0 &&
                 plan.Step0 != GoapActionId.SeekEmergencyCover &&
                 plan.Step0 != GoapActionId.BandageInCover &&
                 plan.Step0 != GoapActionId.ScavengeSupplyFromCorpse) ||
                ((ws & (GoapStateBits.IsBleeding | GoapStateBits.BandageUnsafeUnderFire)) == (GoapStateBits.IsBleeding | GoapStateBits.BandageUnsafeUnderFire) &&
                 plan.Step0 == GoapActionId.BandageInCover) ||
                ((ws & GoapStateBits.RadioLinkDrifting) != 0 && plan.Step0 != GoapActionId.MaintainRadioLinkWithLeader) ||
                (nonBleedingBailedCrew &&
                 plan.Step0 != GoapActionId.LoneWolfLinkUpOrAmbush &&
                 plan.Step0 != GoapActionId.ThrowSmokeScreen) ||
                (!nonBleedingBailedCrew &&
                 isEvadingArmor &&
                 plan.Step0 != GoapActionId.SeekEmergencyCover &&
                 plan.Step0 != GoapActionId.ScavengeSupplyFromCorpse &&
                 plan.Step0 != GoapActionId.TacticalFallbackRetreat &&
                 plan.Step0 != GoapActionId.ThrowSmokeScreen) ||
                (!nonBleedingBailedCrew &&
                 (ws & (GoapStateBits.IsSuppressed | GoapStateBits.IsHeavilySuppressed | GoapStateBits.NearFreshCasualty)) != 0 &&
                 (ws & GoapStateBits.InCover) == 0 &&
                 plan.Step0 != GoapActionId.SeekEmergencyCover &&
                 plan.Step0 != GoapActionId.TacticalFallbackRetreat &&
                 plan.Step0 != GoapActionId.ThrowSmokeScreen);

            if (!hasActivePlan || emergencyPreempt || now >= plan.NextReplanTime)
            {
                GoapActionId prevActiveAction = hasActivePlan && !emergencyPreempt
                    ? (plan.StepIndex == 0 ? plan.Step0 : plan.Step1)
                    : GoapActionId.None;
                plan = PlanBestGoapActionChain(ws, soldier, ref board, personality, vet01, now, prevActiveAction);
                _botGoapPlans[ptr] = plan;
            }

            GoapActionId currentAction = plan.StepIndex == 0 ? plan.Step0 : plan.Step1;
            if (currentAction == GoapActionId.None)
                return;

            ExecuteGoapActionStep(ai, soldier, sq, ref board, ref plan, currentAction, ws, suppression, dist, now);
            _botGoapPlans[ptr] = plan;
            BotActionTrace.RecordGoapState(
                ai,
                soldier,
                personality,
                board.NetDirective,
                plan.LastGoalBit,
                plan.Step0,
                plan.Step1,
                plan.IsEvacuatingBailedCrew,
                suppression,
                dist,
                now);
            if (sq != null && sq.Pointer != IntPtr.Zero)
            {
                _squadBlackboards[sq.Pointer] = board;
            }
            }
            finally
            {
                HitchProfiler.End(HitchBucket.GoapSoldier, profGoapStart);
            }
        }

        private static bool TryRecoverChronicallyStuckFootBot(
            SoldierAI ai,
            Soldier soldier,
            IntPtr ptr,
            Vector3 myPos,
            float suppression,
            float now)
        {
            _footStuckStates.TryGetValue(ptr, out FootStuckState st);

            if (now < st.UnstickOverrideUntil)
            {
                if ((st.UnstickDest - myPos).sqrMagnitude <= 1.5f * 1.5f)
                {
                    st.UnstickOverrideUntil = 0f;
                    st.AnchorPos = myPos;
                    st.AnchorTime = now;
                    _footStuckStates[ptr] = st;
                    return false;
                }

                SetBotPose(ai, soldier, suppression >= 35f ? SoldierPose.Crouch : SoldierPose.Idle, suppression < 35f);
                AiCore.MoveBotTowardPosition(ai, soldier, st.UnstickDest, 0.6f, 1.5f);
                return true;
            }

            bool intentionallyStationary =
                soldier.IsOnCover() ||
                soldier.UsingBipod() ||
                ai.visibleTarget != null ||
                soldier.isInHandsAnimation ||
                soldier.isInBodyAnimation ||
                soldier.IsCarryingBody();

            Vector3 desiredEnd = GetActualMoveEndpoint(ai, soldier);
            Vector2 toEndFlat = new Vector2(desiredEnd.x - myPos.x, desiredEnd.z - myPos.z);
            bool tryingToMoveFar = toEndFlat.sqrMagnitude > 4.2f * 4.2f;

            if (intentionallyStationary || !tryingToMoveFar || st.AnchorTime <= 0f || (myPos - st.AnchorPos).sqrMagnitude > 0.65f * 0.65f)
            {
                st.AnchorPos = myPos;
                st.AnchorTime = now;
                _footStuckStates[ptr] = st;
                return false;
            }

            if ((now - st.AnchorTime) < 11.0f || now < st.NextUnstickTime)
                return false;

            st.AnchorPos = myPos;
            st.AnchorTime = now;
            st.NextUnstickTime = now + 14.0f;

            Vector3 fwd = new Vector3(toEndFlat.x, 0f, toEndFlat.y).normalized;
            if (fwd.sqrMagnitude < 0.01f)
                fwd = soldier.transform.forward;
            Vector3 right = new Vector3(fwd.z, 0f, -fwd.x);
            int sign = (AiCore.GetDeterministicHash(ptr, (int)(now * 0.5f)) & 1) == 0 ? 1 : -1;

            int envMask = AiCore.GetSolidEnvironmentMask();
            Vector3 chestStart = myPos + new Vector3(0f, 0.85f, 0f);
            for (int attempt = 0; attempt < 4; attempt++)
            {
                Vector3 dir;
                switch (attempt)
                {
                    case 0: dir = (right * sign + fwd * 0.35f).normalized; break;
                    case 1: dir = (-right * sign + fwd * 0.35f).normalized; break;
                    case 2: dir = (right * sign - fwd * 0.55f).normalized; break;
                    default: dir = (-right * sign - fwd * 0.55f).normalized; break;
                }

                if (Physics.Raycast(chestStart, dir, 4.2f, envMask, QueryTriggerInteraction.Ignore))
                    continue;

                Vector3 cand = myPos + dir * 3.8f;
                float groundY = ResourcesManager.GetTerrainAltitude(cand, 0f);
                if (groundY < 0.05f || Mathf.Abs(groundY - myPos.y) > 2.4f)
                    continue;

                cand.y = groundY;
                _localCoverProtectedUntil.Remove(ptr);
                _botGoapPlans.Remove(ptr);
                soldier.CoverPosition(null);

                st.UnstickDest = cand;
                st.UnstickOverrideUntil = now + 2.4f;
                _footStuckStates[ptr] = st;

                SetBotPose(ai, soldier, suppression >= 35f ? SoldierPose.Crouch : SoldierPose.Idle, suppression < 35f);
                AiCore.MoveBotTowardPosition(ai, soldier, cand, 0.6f, 1.5f);
                return true;
            }

            _footStuckStates[ptr] = st;
            return false;
        }

        private static bool IsSoldierBailedOutVehicleCrew(Soldier soldier, Squad sq)
        {
            if (soldier == null || sq == null || !AiCore.IsTrueVehicleCrewSquad(sq) || soldier.IsOnVehicle() || sq.SomeoneIsRepairing())
                return false;

            if (soldier.IsReachingVehicleToMount() ||
                soldier.IsEnteringVehicle ||
                soldier.VehicleToOperate() != null)
            {
                return false;
            }

            AiDestination td = soldier.targetDestination;
            return td == null || td.TryCast<VehicleRepairTask>() == null;
        }

        private static GoapStateBits BuildSoldierWorldState(
            SoldierAI ai,
            Soldier soldier,
            Squad sq,
            ref SquadBlackboard board,
            SoldierPersonality personality,
            float suppression,
            float dist,
            bool isGunner,
            bool isOnBridge,
            bool inPointBlankMelee,
            bool isEvadingArmor,
            float now)
        {
            GoapStateBits ws = GoapStateBits.None;
            Vector3 myPos = soldier.transform.position;
            bool isRad = soldier.IsRadioman();

            // Never classify a crewman as "bailed out" while he is actively repairing OR walking to remount his vehicle!
            bool isBailedVehicleCrew = IsSoldierBailedOutVehicleCrew(soldier, sq);

            if (soldier.isBleeding)
            {
                ws |= GoapStateBits.IsBleeding;
                if (!CanSafelyBandageNow(ai, soldier, now, out _, out _))
                    ws |= GoapStateBits.BandageUnsafeUnderFire;
            }
            if (soldier.IsOnCover() || IsPositionShelteredByFrontCover(myPos, board.ThreatDirection, soldier.transform.root))
                ws |= GoapStateBits.InCover;

            // Radioman and bailed-out vehicle crews value self-preservation more highly (drop/seek cover at >=12 suppression),
            // while regular troops use their faction doctrine's SuppressionThresholdBase + individual BotMicroVariance!
            FactionCommandProfile factionProfile = GetFactionCommandProfile(soldier.faction);
            BotMicroVariance micro = BotMicroVariance.FromSoldier(soldier.Pointer);
            bool highSelfPreservation = isRad || isBailedVehicleCrew;
            float baseSuppThresh = highSelfPreservation
                ? 12f
                : Mathf.Max(8f, factionProfile.SuppressionThresholdBase + micro.SuppressionJitter);
            float heavySuppThresh = highSelfPreservation ? 30f : (baseSuppThresh * 2.55f);
            if (suppression >= baseSuppThresh)
                ws |= GoapStateBits.IsSuppressed;
            if (suppression >= heavySuppThresh)
                ws |= GoapStateBits.IsHeavilySuppressed;

            if (board.LastCasualtyTime > 0f &&
                (now - board.LastCasualtyTime) <= 5.5f &&
                (myPos - board.LastCasualtyPos).sqrMagnitude <= 11.5f * 11.5f &&
                (now >= board.SmokeActiveUntil || (board.LastSmokeTargetPos - myPos).sqrMagnitude > 18.0f * 18.0f))
            {
                ws |= GoapStateBits.NearFreshCasualty;
            }

            if (ai.visibleTarget != null)
                ws |= GoapStateBits.HasVisibleEnemy;
            // Bailed vehicle crews only block evacuation for close-quarters self-defense (<= 16m), never to plink at 60m riflemen with a sidearm!
            if (ai.HasEnemyAndCanFireAtHim && (!isBailedVehicleCrew || dist <= 16.0f))
                ws |= GoapStateBits.CanFireAtVisibleEnemy;
            if (isGunner)
                ws |= GoapStateBits.IsMachineGunner;
            if (isOnBridge)
                ws |= GoapStateBits.OnBridgeOrNarrow;
            if (inPointBlankMelee)
                ws |= GoapStateBits.InPointBlankRange;
            if (isEvadingArmor)
                ws |= GoapStateBits.ArmorThreatClose;

            if (isRad)
            {
                ws |= GoapStateBits.IsRadioman;
                Soldier leader = sq != null ? sq.Leader : null;
                if (leader != null && leader.IsAlive && leader.Pointer != soldier.Pointer && leader.transform != null)
                {
                    float dToLeaderSq = (leader.transform.position - myPos).sqrMagnitude;
                    // Hysteresis (20.5m enter / 12.5m exit) prevents Radioman from thrashing at the 18m boundary!
                    bool alreadyMaintainingLink =
                        _botGoapPlans.TryGetValue(soldier.Pointer, out BotGoapPlan curRadPlan) &&
                        now < curRadPlan.PlanExpireTime &&
                        (curRadPlan.StepIndex == 0 ? curRadPlan.Step0 : curRadPlan.Step1) == GoapActionId.MaintainRadioLinkWithLeader;
                    float driftThreshold = alreadyMaintainingLink ? 12.5f : 20.5f;
                    if (dToLeaderSq > driftThreshold * driftThreshold || (sq.RadioRequestWaiting() && dToLeaderSq > 5.0f * 5.0f))
                    {
                        ws |= GoapStateBits.RadioLinkDrifting;
                    }
                }
            }

            if (personality == SoldierPersonality.Assaulter_Shock ||
                personality == SoldierPersonality.Assaulter_Breacher ||
                personality == SoldierPersonality.Assaulter_Skirmisher)
            {
                ws |= GoapStateBits.IsAssaulterOrSkirmisher;
            }

            if (board.HasActiveThreat)
                ws |= GoapStateBits.SquadHasActiveThreat;

            if (board.IsThreatBeingPinned(now) &&
                board.Suppressor0Ptr != soldier.Pointer &&
                board.Suppressor1Ptr != soldier.Pointer)
            {
                ws |= GoapStateBits.AllyPinningThreat;
            }

            if ((_lastKnownTargets.TryGetValue(soldier.Pointer, out LastKnownTargetInfo lk) && now - lk.LastSeenTime <= 4.5f) ||
                (board.HasActiveThreat && !board.ThreatIsArmor && !ai.HasEnemyAndCanFireAtHim))
            {
                ws |= GoapStateBits.TargetLostBehindCover;
            }

            float dToThreatSq = board.HasActiveThreat ? (board.ThreatPosition - myPos).sqrMagnitude : 999999f;
            if (board.HasActiveThreat && dToThreatSq >= 10f * 10f && dToThreatSq <= 29f * 29f)
                ws |= GoapStateBits.InGrenadeRange;

            if (soldier.inventory != null && now > soldier.throw_end_time + 5.0f)
            {
                if (VisionConfig.EnableLastKnownSuppression.Value &&
                    (!_nextSuppressiveGrenadeTime.TryGetValue(soldier.Pointer, out float nextGren) || now >= nextGren || nextGren - now > 45.0f) &&
                    (board.BreacherPtr == IntPtr.Zero || board.BreacherPtr == soldier.Pointer || now >= board.BreachUntil) &&
                    soldier.inventory.FindItemOfType<VirtualGrenade>() != null)
                {
                    ws |= GoapStateBits.HasFragGrenade;
                }

                // Prevent smoke-screen spam: enforce both a 32s personal smoke cooldown and a 22s squad-wide smoke cooldown
                // within 18m of an already-smoked threat sector (while allowing smoke on a fresh threat > 18m away).
                bool personalSmokeReady = !_nextSmokeGrenadeTime.TryGetValue(soldier.Pointer, out float nextSmk) || now >= nextSmk || nextSmk - now > 60.0f;
                bool sectorNeedsSmoke = now >= board.SmokeActiveUntil ||
                                        board.SmokeActiveUntil - now > 60.0f ||
                                        (board.ThreatPosition - board.LastSmokeTargetPos).sqrMagnitude > 18f * 18f;
                if (board.HasActiveThreat &&
                    personalSmokeReady &&
                    sectorNeedsSmoke &&
                    dToThreatSq >= 12f * 12f &&
                    dToThreatSq <= 38f * 38f &&
                    !AiCore.HasLowCeilingOverhead(myPos, 3.2f) &&
                    soldier.inventory.FindItemOfType<VirtualSmokeGrenade>() != null)
                {
                    ws |= GoapStateBits.HasSmokeGrenade;
                }
            }

            if ((board.FlankLeftCover != null && (board.FlankerLeftPtr == IntPtr.Zero || board.FlankerLeftPtr == soldier.Pointer)) ||
                (board.FlankRightCover != null && (board.FlankerRightPtr == IntPtr.Zero || board.FlankerRightPtr == soldier.Pointer)))
            {
                ws |= GoapStateBits.FlankCoverAvailable;
            }

            if (VisionConfig.EnableSmartWallSideCover.Value &&
                !isBailedVehicleCrew &&
                !ai.IsMoving() &&
                !IsAsyncPathRefreshPending(ai, soldier, myPos, now) &&
                !IsInCommittedFiringHalt(soldier.Pointer, now) &&
                (suppression >= 10f || board.HasActiveThreat) &&
                IsPositionOnExposedSideOfWall(myPos, board.ThreatDirection, soldier.transform.root, out _))
            {
                ws |= GoapStateBits.ExposedSideOfWall;
            }

            if (VisionConfig.EnableComradeLiberation.Value &&
                board.SurrenderedComrade != null &&
                board.SurrenderedComrade.IsAlive &&
                board.SurrenderedComrade.HasSurrended() &&
                (board.RescuerPtr == IntPtr.Zero || board.RescuerPtr == soldier.Pointer) &&
                (board.SurrenderedComrade.transform.position - myPos).sqrMagnitude <= 22.0f * 22.0f)
            {
                ws |= GoapStateBits.ComradeNeedsLiberation;
            }

            if (ai.HasEnemyAndCanFireAtHim && dist >= 16f && dist <= 140f && suppression < 42f &&
                AiCore.TryGetSpottableCenter(ai.visibleTarget, out Vector3 tPos))
            {
                // Synchronize GOAP overwatch with SoldierAI_IsMoveTime_Patch's committed firing halt so
                // advancing bots never enter out-of-phase micro-halts or freeze on open bridges.
                bool canHoldOverwatchNow = true;
                if (isOnBridge && (ws & GoapStateBits.InCover) == 0 && !soldier.UsingBipod())
                {
                    canHoldOverwatchNow = false;
                }
                else if ((ai.HasPath || ai.HasPathRequest) && !soldier.UsingBipod() && !board.IsDefendingObjective)
                {
                    canHoldOverwatchNow = IsInCommittedFiringHalt(soldier.Pointer, now);
                }

                if (canHoldOverwatchNow &&
                    (HasClearLineOfFireFromCrouch(soldier, tPos, dist) || HasClearLineOfFireFromStanding(soldier, tPos, dist)))
                {
                    ws |= GoapStateBits.CanProvideOverwatch;
                }
            }

            // Detect when the soldier is "Entirely Alone" (last survivor of squad, Leader dead and no squadmates within 35m,
            // or any bailed-out Vehicle Crew on foot — even while executing MissionTaskRetreat so GOAP drives evacuation!)
            bool onRetreatTask = IsSquadOnRetreatTask(sq);
            if (isBailedVehicleCrew)
            {
                ws |= GoapStateBits.IsEntirelyAlone;
            }
            else if (!onRetreatTask)
            {
                if (sq == null || board.AliveMemberCount <= 1)
                {
                    ws |= GoapStateBits.IsEntirelyAlone;
                }
                else if (sq.Leader == null || !sq.Leader.IsAlive ||
                         (!soldier.IsSquadLeader() && (sq.Leader.transform.position - myPos).sqrMagnitude > 65f * 65f))
                {
                    Soldier closestAlly = sq.GetClosestSquadMember(soldier, out float closestAllyDist);
                    if (closestAlly == null || closestAllyDist > 35f)
                    {
                        ws |= GoapStateBits.IsEntirelyAlone;
                    }
                }
            }

            // Authentic Field Bandage Tracking: Soldier.hasSyringe (0x502) is strictly the Medic's revive syringe!
            // Personal self-bleed dressings live in soldier.inventory as VirtualBandages (consumed by Soldier.UseBandages).
            bool hasBandage = soldier.inventory != null && soldier.inventory.FindItemOfType<VirtualBandages>() != null;
            if (hasBandage)
                ws |= GoapStateBits.HasBandage;

            // Battlefield Scavenging, Buddy Resupply, Wearable Scavenging & Role-Smart Weapon Upgrades:
            // 1) Out of reserve ammo for primary/stowed weapon OR out of VirtualBandages when not fighting a visible foot enemy.
            // 2) Need an Anti-Tank weapon/AT grenade when enemy armor is threatening the squad (or bot is an AT trooper whose launcher is dry).
            // 3) Pistol-only on foot (merged tankers/pilots/officers), fallen Squad MG inheritance, or role-matched primary upgrade!
            // 4) Emergent Field Radio / Fallen Medic Syringe Inheritance & Knocked-Off / Player-Gifted Helmet Recovery!
            // 5) Support Ammo-Bearer proactively feeding a dry MG or AT squadmate!
            bool urgentBandageWhileBleeding = soldier.isBleeding && !hasBandage;
            if ((!soldier.isBleeding || urgentBandageWhileBleeding) && suppression < 28f && !isOnBridge)
            {
                GenericGun gun0 = soldier.GetHeldGun(0);
                GenericGun gun1 = soldier.GetHeldGun(1);
                GenericGun gun2 = soldier.GetHeldGun(2);
                bool recentFootContact = _lastKnownTargets.TryGetValue(soldier.Pointer, out LastKnownTargetInfo scavLk) &&
                                         (now - scavLk.LastSeenTime) < 4.5f;
                bool casualScavengeAllowed = !urgentBandageWhileBleeding && !isEvadingArmor && !isBailedVehicleCrew;
                bool nonUrgentScavengeAllowed = casualScavengeAllowed && !recentFootContact;
                bool outOfAmmo = casualScavengeAllowed &&
                                 ((gun0 != null && soldier.IsOutOfAmmosFor(gun0)) ||
                                  (gun1 != null && gun1.IsAtWeapon() && !string.IsNullOrEmpty(gun1.compatibleAmmo) && soldier.IsOutOfAmmosFor(gun1)) ||
                                  (gun2 != null && !gun2.IsPistol() && !gun2.IsAtWeapon() && soldier.IsOutOfAmmosFor(gun2)));
                bool outOfBandage = (urgentBandageWhileBleeding || !recentFootContact) &&
                                    !isEvadingArmor &&
                                    !isBailedVehicleCrew &&
                                    !hasBandage;

                bool armorThreatPresent = (ws & GoapStateBits.ArmorThreatClose) != 0 ||
                                          (board.HasActiveThreat && board.PrimaryTarget != null && board.PrimaryTarget.TryCast<Vehicle>() != null) ||
                                          (ai.visibleTarget != null && ai.visibleTarget.TryCast<Vehicle>() != null);
                SoldierPersonality pers = GetPersonality(soldier);
                bool isAtRole = pers == SoldierPersonality.AT_TankHunter ||
                                pers == SoldierPersonality.AT_Ambusher ||
                                pers == SoldierPersonality.AT_Demolitionist;
                bool isMedicRole = pers == SoldierPersonality.Medic_Heroic ||
                                   pers == SoldierPersonality.Medic_Combat ||
                                   pers == SoldierPersonality.Medic_Triage;
                bool needAtWeapon = false;
                if (!urgentBandageWhileBleeding &&
                    (armorThreatPresent || isAtRole) &&
                    !onRetreatTask && !soldier.IsSquadLeader() && !soldier.IsRadioman() && !isMedicRole)
                {
                    bool hasUsableAtGun = (gun0 != null && gun0.IsAtWeapon() && (gun0.GetCurrentAmmoCount() > 0 || !soldier.IsOutOfAmmosFor(gun0))) ||
                                          (gun1 != null && gun1.IsAtWeapon() && (gun1.GetCurrentAmmoCount() > 0 || !soldier.IsOutOfAmmosFor(gun1))) ||
                                          (gun2 != null && gun2.IsAtWeapon() && (gun2.GetCurrentAmmoCount() > 0 || !soldier.IsOutOfAmmosFor(gun2)));
                    bool hasAtGrenade = soldier.inventory != null && soldier.inventory.FindItemOfType<VirtualATGrenade>() != null;
                    needAtWeapon = !hasUsableAtGun && !hasAtGrenade;
                }

                bool needUrgentPrimary = false;
                bool wantSquadMg = false;
                bool wantRoleUpgrade = false;
                if (casualScavengeAllowed)
                {
                    EvaluateRoleWeaponUpgradeNeed(
                        soldier,
                        sq,
                        pers,
                        isMedicRole,
                        onRetreatTask,
                        ref board,
                        now,
                        out needUrgentPrimary,
                        out wantSquadMg,
                        out wantRoleUpgrade);
                    if (recentFootContact)
                    {
                        wantSquadMg = false;
                        wantRoleUpgrade = false;
                    }
                }
                bool wantAnyWeaponUpgrade = needUrgentPrimary || wantSquadMg || wantRoleUpgrade;

                bool wantRadioPack = nonUrgentScavengeAllowed &&
                                     VisionConfig.EnableEmergentWearableScavenge.Value &&
                                     !onRetreatTask &&
                                     sq != null &&
                                     !AiCore.IsTrueVehicleCrewSquad(sq) &&
                                     !board.PreviouslyHadAliveRadioman &&
                                     !isRad &&
                                     !isMedicRole &&
                                     (soldier.vest_Obj == null || !soldier.vest_Obj.isFuelTank);

                bool wantMedicSyringe = nonUrgentScavengeAllowed &&
                                        !onRetreatTask &&
                                        sq != null &&
                                        !AiCore.IsTrueVehicleCrewSquad(sq) &&
                                        !board.HasAliveMedic &&
                                        !soldier.hasSyringe &&
                                        !isRad &&
                                        !soldier.IsSquadLeader();

                bool wantHelmet = nonUrgentScavengeAllowed &&
                                  VisionConfig.EnableEmergentWearableScavenge.Value &&
                                  !onRetreatTask &&
                                  !soldier.hasHeadgear &&
                                  suppression < 12f;

                bool canFeedNeedyAlly = nonUrgentScavengeAllowed &&
                                        VisionConfig.EnableBuddyResupply.Value &&
                                        !onRetreatTask &&
                                        !outOfAmmo &&
                                        soldier.IsAmmoBoxCarrier();

                bool wantFragGrenade = nonUrgentScavengeAllowed &&
                                       !onRetreatTask &&
                                       !isMedicRole &&
                                       suppression < 16f &&
                                       soldier.inventory != null &&
                                       soldier.inventory.FindItemOfType<VirtualGrenade>() == null;

                // Do not immediately break off a smoke-covered push just to scavenge another smoke grenade right after throwing one!
                bool wantSmokeGrenade = nonUrgentScavengeAllowed &&
                                        !onRetreatTask &&
                                        suppression < 16f &&
                                        (!_nextSmokeGrenadeTime.TryGetValue(soldier.Pointer, out float nextSmkScav) || now >= nextSmkScav || nextSmkScav - now > 60.0f) &&
                                        (soldier.IsSquadLeader() ||
                                         isMedicRole ||
                                         pers == SoldierPersonality.Assaulter_Breacher ||
                                         pers == SoldierPersonality.Assaulter_Skirmisher) &&
                                        soldier.inventory != null &&
                                        soldier.inventory.FindItemOfType<VirtualSmokeGrenade>() == null;

                bool hasVisibleFootEnemy = ai.HasEnemyAndCanFireAtHim && (ai.visibleTarget == null || ai.visibleTarget.TryCast<Vehicle>() == null);
                if (!hasVisibleFootEnemy &&
                    (needAtWeapon || wantAnyWeaponUpgrade || outOfAmmo || outOfBandage || wantFragGrenade || wantSmokeGrenade || wantRadioPack || wantMedicSyringe || wantHelmet || canFeedNeedyAlly) &&
                    TryGetNearbyScavengeTarget(
                        soldier,
                        myPos,
                        pers,
                        outOfAmmo,
                        outOfBandage,
                        wantFragGrenade,
                        wantSmokeGrenade,
                        needAtWeapon,
                        needUrgentPrimary,
                        wantSquadMg,
                        wantRoleUpgrade,
                        wantRadioPack,
                        wantMedicSyringe,
                        wantHelmet,
                        canFeedNeedyAlly,
                        now,
                        out _))
                {
                    ws |= GoapStateBits.NeedAmmoOrMedScavenge;
                }
            }

            // Win-Condition Urgency: Defenders contest objectives being back-capped or outnumbered inside the circle,
            // and Attackers push into the cap circle whenever they lack capture majority inside the zone, capture is
            // >= 35% underway, or tickets are critically low (<= 25%)!
            bool desperateCapHold = false;
            if (board.HasActiveObjectiveTask && sq != null && sq.currentTask != null)
            {
                float dToObjSq = (board.ObjectivePosition - myPos).sqrMagnitude;
                bool insideCap = sq.currentTask.IsInsideTaskArea(myPos, 0.85f);
                desperateCapHold = insideCap &&
                                   ((board.IsDefendingObjective && board.ObjectiveCaptureProgress >= 0.40f) ||
                                    (!board.IsDefendingObjective && (board.ObjectiveCaptureProgress >= 0.50f || board.TicketRatio01 <= 0.22f)));

                if (dToObjSq <= 85f * 85f && !insideCap)
                {
                    bool defenderPointBeingCapped = board.IsDefendingObjective &&
                                                    (board.ObjectiveCaptureProgress >= 0.08f ||
                                                     board.EnemyUnitsInsideObjective > board.FriendlyUnitsInsideObjective);
                    bool urgentAttackerProgressOrTickets = !board.IsDefendingObjective &&
                                                           dToObjSq <= 75f * 75f &&
                                                           (board.ObjectiveCaptureProgress >= 0.35f ||
                                                            (board.IsInvaderFaction && board.TicketRatio01 <= 0.25f));
                    bool attackerOutnumberedInsideCircle = !board.IsDefendingObjective &&
                                                           board.EnemyUnitsInsideObjective > 0 &&
                                                           board.FriendlyUnitsInsideObjective <= board.EnemyUnitsInsideObjective &&
                                                           dToObjSq <= 65f * 65f;
                    bool attackerCloseEmptyCircleBreach = !board.IsDefendingObjective &&
                                                          board.FriendlyUnitsInsideObjective == 0 &&
                                                          dToObjSq <= 45f * 45f;
                    if (defenderPointBeingCapped ||
                        urgentAttackerProgressOrTickets ||
                        attackerOutnumberedInsideCircle ||
                        attackerCloseEmptyCircleBreach)
                    {
                        ws |= GoapStateBits.ObjectiveCapContestUrgent;
                    }
                }
            }

            // Tier 1 Individual "Oh Shit!" Break-Contact / Fallback State:
            // Triggered when an active individual fallback is underway, OR when caught close to an enemy tank (<= 34m)
            // with zero usable AT capability (and no nearby fallen AT launcher/grenade to grab!), OR when isolated/remnant
            // under heavy suppression and outnumbered!
            if (VisionConfig.EnableTacticalRetreatsAndRegroup.Value &&
                !isBailedVehicleCrew &&
                !soldier.isBleeding &&
                !isOnBridge &&
                !inPointBlankMelee &&
                !desperateCapHold)
            {
                IntPtr sPtr = soldier.Pointer;
                if (_individualFallbackUntil.TryGetValue(sPtr, out float fbUntil) && now < fbUntil)
                {
                    ws |= GoapStateBits.OverwhelmedNeedFallback;
                }
                else if (!_nextIndividualFallbackTime.TryGetValue(sPtr, out float nextFb) || now >= nextFb)
                {
                    bool canGrabNearbyAtWeapon = suppression < 62.0f && HasPendingAtWeaponScavenge(sPtr, myPos, 22.0f);
                    bool helplessAgainstCloseTank =
                        !canGrabNearbyAtWeapon &&
                        ((ws & GoapStateBits.ArmorThreatClose) != 0 ||
                         (board.HasActiveThreat && board.ThreatIsArmor && dToThreatSq <= 34.0f * 34.0f)) &&
                        !HasUsableAntiTankCapability(soldier);

                    bool isolatedAndOverwhelmed =
                        ((ws & GoapStateBits.IsEntirelyAlone) != 0 || board.AliveMemberCount <= 2) &&
                        suppression >= 34.0f &&
                        board.NearbyEnemyCount42m >= board.NearbyFriendlyCount42m + 3 &&
                        !(factionProfile.PreferAmbushWhenAlone && factionProfile.BayonetBreachCostMult <= 0.66f);

                    if (helplessAgainstCloseTank || isolatedAndOverwhelmed)
                    {
                        ws |= GoapStateBits.OverwhelmedNeedFallback;
                    }
                }
            }

            return ws;
        }

        private static BotGoapPlan PlanBestGoapActionChain(
            GoapStateBits ws,
            Soldier soldier,
            ref SquadBlackboard board,
            SoldierPersonality personality,
            float vet01,
            float now,
            GoapActionId prevActiveAction)
        {
            // Determine highest priority Goal bit based on 3-Tier Hierarchy:
            // 1. Immediate Crisis Survival (Bleeding, Armor close, Exposed wall side, Heavy open suppression)
            // 2. Radioman Lifeline Preservation (Keep radio link with Squad Leader alive!)
            // 3. Point-Blank CQB Self-Defense
            // 4. Lone-Wolf Survival/Link-Up (when Entirely Alone)
            // 5. Battlefield Ammo/Bandage Scavenging (when out of primary ammo or bandages out of direct LOS)
            // 6. Squad Leader & Team Net Directive (Anvil Pinning vs. Hammer Flanking vs. Local Fire & Maneuver)
            bool nonBleedingBailedCrew =
                IsSoldierBailedOutVehicleCrew(soldier, soldier.joinedSquad) &&
                (ws & (GoapStateBits.IsBleeding | GoapStateBits.InPointBlankRange | GoapStateBits.CanFireAtVisibleEnemy)) == 0 &&
                (ws & GoapStateBits.IsEntirelyAlone) != 0;

            bool bleedingInCoverMustFightFirst =
                (ws & (GoapStateBits.IsBleeding | GoapStateBits.BandageUnsafeUnderFire | GoapStateBits.InCover)) ==
                (GoapStateBits.IsBleeding | GoapStateBits.BandageUnsafeUnderFire | GoapStateBits.InCover) &&
                (ws & (GoapStateBits.CanFireAtVisibleEnemy | GoapStateBits.InPointBlankRange)) != 0;

            GoapStateBits desiredGoalBit;
            if (nonBleedingBailedCrew)
            {
                // Bailed-out vehicle crews bolt away from the burning wreck toward friendly lines / infantry
                // instead of huddling behind a 1-slot sapling next to the knocked-out hull!
                desiredGoalBit = GoapStateBits.SquadSupported;
            }
            else if (bleedingInCoverMustFightFirst)
            {
                // Bleeding soldier already in cover with an active shooter on top of him keeps his rifle up
                // to neutralize the attacker BEFORE holstering his weapon to bandage!
                desiredGoalBit = GoapStateBits.TargetEliminated;
            }
            else if ((ws & (GoapStateBits.IsBleeding | GoapStateBits.OverwhelmedNeedFallback | GoapStateBits.ArmorThreatClose | GoapStateBits.ExposedSideOfWall)) != 0 ||
                     ((ws & (GoapStateBits.IsSuppressed | GoapStateBits.IsHeavilySuppressed | GoapStateBits.NearFreshCasualty)) != 0 &&
                      (ws & GoapStateBits.InCover) == 0))
            {
                desiredGoalBit = GoapStateBits.SurvivedCrisis;
            }
            else if ((ws & GoapStateBits.RadioLinkDrifting) != 0)
            {
                desiredGoalBit = GoapStateBits.RadioLinkPreserved;
            }
            else if ((ws & GoapStateBits.InPointBlankRange) != 0)
            {
                desiredGoalBit = GoapStateBits.TargetEliminated;
            }
            else if ((ws & GoapStateBits.NeedAmmoOrMedScavenge) != 0)
            {
                desiredGoalBit = GoapStateBits.SquadSupported;
            }
            else if ((ws & GoapStateBits.IsEntirelyAlone) != 0 && (ws & GoapStateBits.CanFireAtVisibleEnemy) == 0)
            {
                desiredGoalBit = GoapStateBits.SquadSupported;
            }
            else if ((ws & GoapStateBits.ComradeNeedsLiberation) != 0 && (ws & GoapStateBits.HasVisibleEnemy) == 0)
            {
                desiredGoalBit = GoapStateBits.SquadSupported;
            }
            else if ((ws & GoapStateBits.SquadHasActiveThreat) != 0)
            {
                bool isMedic = soldier.IsMedic() ||
                               personality == SoldierPersonality.Medic_Heroic ||
                               personality == SoldierPersonality.Medic_Combat ||
                               personality == SoldierPersonality.Medic_Triage;
                // Keep Machine Gunners, Medics, Radiomen, and at least 1 Overwatch Suppressor (when AllyPinningThreat == 0
                // and >= 3 squadmates are alive) eligible for SquadSupported even during ObjectiveCapContestUrgent so the squad
                // ALWAYS establishes pinning fire to unlock MoveToFlankCover & BoundWithOverwatch into the capture circle!
                bool dedicatedSupportOrNeedInitialPin =
                    board.AliveMemberCount >= 3 &&
                    !(board.IsDefendingObjective && board.ObjectiveCaptureProgress >= 0.65f) &&
                    ((ws & (GoapStateBits.IsMachineGunner | GoapStateBits.IsRadioman)) != 0 ||
                     isMedic ||
                     personality == SoldierPersonality.Gunner_Anchor ||
                     personality == SoldierPersonality.Gunner_Overwatch ||
                     ((ws & GoapStateBits.AllyPinningThreat) == 0 && (ws & GoapStateBits.CanProvideOverwatch) != 0));

                bool preferSupportOrPin =
                    dedicatedSupportOrNeedInitialPin ||
                    ((ws & GoapStateBits.ObjectiveCapContestUrgent) == 0 &&
                     (board.NetDirective == SquadNetDirective.Anvil_PinAndCallSupport ||
                      personality == SoldierPersonality.Leader_Tactician));

                desiredGoalBit = preferSupportOrPin ? GoapStateBits.SquadSupported : GoapStateBits.TargetEliminated;
            }
            else
            {
                desiredGoalBit = GoapStateBits.TargetEliminated;
            }

            GoapActionId bestStep0 = GoapActionId.None;
            GoapActionId bestStep1 = GoapActionId.None;
            float bestCost = 999f;
            IntPtr soldierPtr = soldier.Pointer;
            bool canMove = AiCore.CanBotMoveAutonomously(soldier);
            FactionCommandProfile factionProfile = GetFactionCommandProfile(soldier.faction);
            BotMicroVariance micro = BotMicroVariance.FromSoldier(soldierPtr);

            int actionCount = _goapActions.Length;
            for (int i = 0; i < actionCount; i++)
            {
                GoapActionDef a0 = _goapActions[i];
                if (!canMove && IsMovementGoapAction(a0.Id))
                    continue;
                if ((ws & a0.Required) != a0.Required || (ws & a0.Forbidden) != 0)
                    continue;

                float cost0 = GetPersonalityAndDirectiveAdjustedCost(a0, ws, soldierPtr, ref board, personality, vet01, ref factionProfile, ref micro);
                // Plan Commitment Hysteresis (-0.35f inertia bonus) prevents stuttering/flip-flopping between nearly-tied actions
                if (a0.Id == prevActiveAction)
                    cost0 = Mathf.Max(0.05f, cost0 - 0.35f);

                GoapStateBits wsAfter0 = (ws | a0.Add) & ~a0.Remove;

                if ((wsAfter0 & desiredGoalBit) != 0)
                {
                    if (cost0 < bestCost)
                    {
                        bestCost = cost0;
                        bestStep0 = a0.Id;
                        bestStep1 = GoapActionId.None;
                    }
                    // Allow 2-step tactical chains to compete even when Step 0 partially satisfies the goal bit:
                    //  1) MoveToFlankCover -> FlushThreatWithFrag (when carrying frag grenade)
                    //  2) SeekEmergencyCover / RepositionBehindWall / ScavengeSupplyFromCorpse -> BandageInCover (when bleeding)
                    //  3) ThrowSmokeScreen -> MoveToFlankCover / BoundWithOverwatch (when smoking to mask an unpinned advance)
                    bool canChainFlankFrag = a0.Id == GoapActionId.MoveToFlankCover && (ws & GoapStateBits.HasFragGrenade) != 0;
                    bool canChainCoverBandage = (a0.Id == GoapActionId.SeekEmergencyCover ||
                                                 a0.Id == GoapActionId.RepositionBehindWall ||
                                                 a0.Id == GoapActionId.ScavengeSupplyFromCorpse) &&
                                                (ws & GoapStateBits.IsBleeding) != 0 &&
                                                (ws & GoapStateBits.BandageUnsafeUnderFire) == 0;
                    bool canChainSmokePush = a0.Id == GoapActionId.ThrowSmokeScreen &&
                                             (ws & GoapStateBits.HasSmokeGrenade) != 0 &&
                                             ((ws & GoapStateBits.AllyPinningThreat) == 0 || (ws & GoapStateBits.OverwhelmedNeedFallback) != 0);
                    if (!canChainFlankFrag && !canChainCoverBandage && !canChainSmokePush)
                        continue;
                }

                for (int j = 0; j < actionCount; j++)
                {
                    if (i == j)
                        continue;

                    GoapActionDef a1 = _goapActions[j];
                    if (!canMove && IsMovementGoapAction(a1.Id))
                        continue;
                    if ((wsAfter0 & a1.Required) != a1.Required || (wsAfter0 & a1.Forbidden) != 0)
                        continue;

                    GoapStateBits wsAfter1 = (wsAfter0 | a1.Add) & ~a1.Remove;
                    if ((wsAfter1 & desiredGoalBit) == 0)
                        continue;

                    float cost1 = GetPersonalityAndDirectiveAdjustedCost(a1, wsAfter0, soldierPtr, ref board, personality, vet01, ref factionProfile, ref micro);
                    // Synergy bonuses for intentional 2-step tactical chains:
                    if (a0.Id == GoapActionId.MoveToFlankCover && a1.Id == GoapActionId.FlushThreatWithFrag)
                        cost1 -= 1.45f;
                    else if ((a0.Id == GoapActionId.SeekEmergencyCover ||
                              a0.Id == GoapActionId.RepositionBehindWall ||
                              a0.Id == GoapActionId.ScavengeSupplyFromCorpse) &&
                             a1.Id == GoapActionId.BandageInCover)
                        cost1 -= 0.85f;
                    else if (a0.Id == GoapActionId.ThrowSmokeScreen &&
                             (a1.Id == GoapActionId.MoveToFlankCover ||
                              a1.Id == GoapActionId.BoundWithOverwatch ||
                              a1.Id == GoapActionId.TacticalFallbackRetreat ||
                              a1.Id == GoapActionId.LoneWolfLinkUpOrAmbush))
                        cost1 -= 1.15f;

                    float totalCost = cost0 + cost1;
                    if (totalCost < bestCost)
                    {
                        bestCost = totalCost;
                        bestStep0 = a0.Id;
                        bestStep1 = a1.Id;
                    }
                }
            }

            // Fallback Goal Bridge: if TargetEliminated/SurvivedCrisis had no valid path during an active firefight
            // (e.g. on a narrow bridge, pinned without flank cover, or bleeding in cover with a close shooter right around
            // the corner blocking bandaging), fall back to DirectFire / Overwatch / Suppress so a soldier never stalls!
            if (bestStep0 == GoapActionId.None)
            {
                if ((ws & GoapStateBits.CanFireAtVisibleEnemy) != 0)
                {
                    bestStep0 = GoapActionId.DirectFireEngagement;
                }
                else if ((ws & GoapStateBits.SquadHasActiveThreat) != 0 &&
                         ((ws & GoapStateBits.IsBleeding) == 0 || (ws & GoapStateBits.BandageUnsafeUnderFire) != 0))
                {
                    bestStep0 = (ws & GoapStateBits.CanProvideOverwatch) != 0
                        ? GoapActionId.HoldOverwatchFire
                        : GoapActionId.SuppressThreatPosition;
                }
            }

            return new BotGoapPlan
            {
                LastGoalBit = desiredGoalBit,
                Step0 = bestStep0,
                Step1 = bestStep1,
                StepIndex = 0,
                StepCount = (byte)(bestStep1 != GoapActionId.None ? 2 : (bestStep0 != GoapActionId.None ? 1 : 0)),
                PlanExpireTime = now + 4.3f + micro.ReactionJitterSeconds,
                NextReplanTime = now + 1.35f + micro.ReactionJitterSeconds,
                DesiredPose = SoldierPose.Crouch,
                LockUnitCoverChange = false
            };
        }

        private static float GetPersonalityAndDirectiveAdjustedCost(
            GoapActionDef def,
            GoapStateBits ws,
            IntPtr soldierPtr,
            ref SquadBlackboard board,
            SoldierPersonality personality,
            float vet01,
            ref FactionCommandProfile factionProfile,
            ref BotMicroVariance micro)
        {
            float cost = def.BaseCost;
            SquadNetDirective netDirective = board.NetDirective;
            bool urgentObjPush = (ws & GoapStateBits.ObjectiveCapContestUrgent) != 0;
            bool underFireOrNearCasualty = (ws & (GoapStateBits.IsSuppressed | GoapStateBits.IsHeavilySuppressed | GoapStateBits.NearFreshCasualty)) != 0;
            bool advanceMaskedOrPinned = (ws & GoapStateBits.AllyPinningThreat) != 0 || Time.time < board.SmokeActiveUntil;
            bool lowTicketConservation = board.IsInvaderFaction &&
                                         board.TicketRatio01 <= 0.30f &&
                                         board.ObjectiveCaptureProgress < 0.35f &&
                                         !urgentObjPush;

            switch (def.Id)
            {
                case GoapActionId.MoveToFlankCover:
                    cost *= factionProfile.FlankMoveCostMult * micro.AggressionCostMult;
                    if (netDirective == SquadNetDirective.Hammer_WideFlankEnvelopment)
                        cost -= 0.75f;
                    if (urgentObjPush && (!underFireOrNearCasualty || advanceMaskedOrPinned))
                        cost -= 0.55f;
                    else if (underFireOrNearCasualty && !advanceMaskedOrPinned)
                        cost += 0.55f;
                    if (personality == SoldierPersonality.Assaulter_Skirmisher ||
                        personality == SoldierPersonality.Assaulter_Shock ||
                        personality == SoldierPersonality.Assaulter_Breacher)
                        cost -= 0.65f;
                    else if (personality == SoldierPersonality.Medic_Heroic ||
                             personality == SoldierPersonality.Medic_Combat ||
                             personality == SoldierPersonality.Medic_Triage)
                        cost += 1.25f;
                    break;

                case GoapActionId.FlushThreatWithFrag:
                    cost *= factionProfile.FragFlushCostMult * micro.AggressionCostMult;
                    if (netDirective == SquadNetDirective.Hammer_WideFlankEnvelopment)
                        cost -= 0.45f;
                    if (urgentObjPush)
                        cost -= 0.50f;
                    if (personality == SoldierPersonality.Assaulter_Breacher ||
                        personality == SoldierPersonality.Rifleman_Grenadier ||
                        personality == SoldierPersonality.AT_Demolitionist)
                        cost -= 0.55f;
                    else if (personality == SoldierPersonality.Medic_Heroic ||
                             personality == SoldierPersonality.Medic_Combat ||
                             personality == SoldierPersonality.Medic_Triage)
                        cost += 0.90f;
                    break;

                case GoapActionId.ThrowSmokeScreen:
                    cost *= factionProfile.SmokeCostMult * micro.AggressionCostMult;
                    if (netDirective == SquadNetDirective.Hammer_WideFlankEnvelopment)
                        cost -= 0.45f;
                    if (urgentObjPush || lowTicketConservation || underFireOrNearCasualty)
                        cost -= 0.55f;
                    if (personality == SoldierPersonality.Assaulter_Breacher ||
                        personality == SoldierPersonality.Rifleman_Grenadier ||
                        personality == SoldierPersonality.AT_Demolitionist)
                        cost -= 0.55f;
                    break;

                case GoapActionId.SuppressThreatPosition:
                    cost *= factionProfile.SuppressCostMult * micro.CautionCostMult;
                    if (netDirective == SquadNetDirective.Anvil_PinAndCallSupport)
                        cost -= 0.80f;
                    if (lowTicketConservation || (underFireOrNearCasualty && !advanceMaskedOrPinned))
                        cost -= 0.35f;
                    if (personality == SoldierPersonality.Gunner_Anchor ||
                        personality == SoldierPersonality.Gunner_Overwatch ||
                        personality == SoldierPersonality.Gunner_AssaultMG)
                        cost -= 0.70f;
                    break;

                case GoapActionId.HoldOverwatchFire:
                    cost *= factionProfile.OverwatchHoldCostMult * micro.CautionCostMult;
                    if (netDirective == SquadNetDirective.Anvil_PinAndCallSupport)
                        cost -= 0.70f;
                    // When objective is being back-capped or needs an overtime cap push, penalize sitting outside the cap radius
                    // UNLESS incoming fire / fresh casualties require pinning fire first!
                    if (urgentObjPush && (!underFireOrNearCasualty || advanceMaskedOrPinned))
                        cost += 0.65f;
                    else if (lowTicketConservation || underFireOrNearCasualty)
                        cost -= 0.35f;
                    if (personality == SoldierPersonality.Gunner_Anchor ||
                        personality == SoldierPersonality.Gunner_Overwatch ||
                        personality == SoldierPersonality.Gunner_AssaultMG)
                        cost -= 0.65f;
                    break;

                case GoapActionId.BoundWithOverwatch:
                    cost *= factionProfile.FlankMoveCostMult * micro.AggressionCostMult;
                    if (netDirective == SquadNetDirective.Anvil_PinAndCallSupport ||
                        netDirective == SquadNetDirective.Hammer_WideFlankEnvelopment)
                        cost -= 0.45f;
                    // Self-Preservation Priority #1: never blindly jog into unpinned, unsmoked fire or over a fresh comrade's body!
                    if (underFireOrNearCasualty && !advanceMaskedOrPinned)
                    {
                        cost += 1.15f;
                        IndividualSelfPreservationBalksCount++;
                    }
                    else if (urgentObjPush)
                    {
                        cost -= 0.75f;
                    }
                    if (personality == SoldierPersonality.Assaulter_Shock ||
                        personality == SoldierPersonality.Assaulter_Breacher ||
                        personality == SoldierPersonality.Assaulter_Skirmisher ||
                        personality == SoldierPersonality.Rifleman_Grenadier ||
                        personality == SoldierPersonality.Gunner_AssaultMG)
                        cost -= 0.45f;
                    break;

                case GoapActionId.PointBlankBayonetBreach:
                    cost *= factionProfile.BayonetBreachCostMult * micro.AggressionCostMult;
                    if (urgentObjPush)
                        cost -= 0.30f;
                    if (personality == SoldierPersonality.Assaulter_Shock ||
                        personality == SoldierPersonality.Assaulter_Breacher)
                        cost -= 0.35f;
                    break;

                case GoapActionId.RescueSurrenderedComrade:
                    cost *= factionProfile.ComradeAidAndDragMult * micro.AggressionCostMult;
                    if (lowTicketConservation)
                        cost -= 0.50f;
                    if (personality == SoldierPersonality.Medic_Heroic ||
                        personality == SoldierPersonality.Leader_OldGuard)
                        cost -= 0.75f;
                    break;

                case GoapActionId.SeekEmergencyCover:
                    cost *= factionProfile.EmergencyCoverCostMult * micro.CautionCostMult;
                    if (lowTicketConservation)
                        cost -= 0.40f;
                    if (personality == SoldierPersonality.Rifleman_Cautious ||
                        personality == SoldierPersonality.Medic_Triage)
                        cost -= 0.45f;
                    break;

                case GoapActionId.RepositionBehindWall:
                case GoapActionId.BandageInCover:
                case GoapActionId.MaintainRadioLinkWithLeader:
                case GoapActionId.LoneWolfLinkUpOrAmbush:
                    cost *= micro.CautionCostMult;
                    if (lowTicketConservation)
                        cost -= 0.35f;
                    break;

                case GoapActionId.ScavengeSupplyFromCorpse:
                    cost *= factionProfile.ScavengeAndResupplyMult * micro.CautionCostMult;
                    if (lowTicketConservation)
                        cost -= 0.40f;
                    // Urgently prioritize grabbing a nearby fallen Bazooka/Panzerfaust/AT grenade when enemy armor is closing in,
                    // while heavily penalizing any non-AT scavenging until out of immediate armor danger!
                    if ((ws & GoapStateBits.ArmorThreatClose) != 0)
                    {
                        bool isAtPickup = _botScavengeCaches.TryGetValue(soldierPtr, out BotScavengeCache sc) && sc.IsAtWeaponPickup;
                        cost += isAtPickup ? -0.70f : 1.50f;
                    }
                    // Prioritize inheriting a fallen radio pack when the squad is cut off from its Radioman!
                    if (!board.PreviouslyHadAliveRadioman && !factionProfile.AllowDisconnectedLocalFlank)
                        cost -= 0.45f;
                    break;

                case GoapActionId.DirectFireEngagement:
                    cost *= micro.AggressionCostMult;
                    if ((ws & GoapStateBits.IsSuppressed) != 0)
                        cost += 0.45f;
                    if (netDirective == SquadNetDirective.Hammer_WideFlankEnvelopment)
                        cost += 0.50f;
                    if (urgentObjPush)
                        cost += 0.60f;
                    break;

                case GoapActionId.TacticalFallbackRetreat:
                    cost *= micro.CautionCostMult;
                    if (netDirective == SquadNetDirective.Regroup_SectorWithdrawal)
                        cost -= 0.45f;
                    if ((ws & GoapStateBits.ArmorThreatClose) != 0)
                        cost -= 0.40f;
                    if (personality == SoldierPersonality.Rifleman_Cautious ||
                        personality == SoldierPersonality.Medic_Triage ||
                        personality == SoldierPersonality.Leader_OldGuard)
                        cost -= 0.35f;
                    else if (personality == SoldierPersonality.Assaulter_Shock)
                        cost += 0.25f;
                    break;
            }

            return Mathf.Max(0.1f, cost - vet01 * 0.15f);
        }

        private static void ExecuteGoapActionStep(
            SoldierAI ai,
            Soldier soldier,
            Squad sq,
            ref SquadBlackboard board,
            ref BotGoapPlan plan,
            GoapActionId action,
            GoapStateBits ws,
            float suppression,
            float dist,
            float now)
        {
            IntPtr ptr = soldier.Pointer;
            Vector3 myPos = soldier.transform.position;
            plan.HasMoveOverride = false;
            plan.IsEvacuatingBailedCrew = false;

            switch (action)
            {
                case GoapActionId.SeekEmergencyCover:
                    if (TrySeekNearbyCover(ai, soldier))
                    {
                        plan.LockUnitCoverChange = true;
                        if (soldier.IsOnCover())
                        {
                            SetBotPose(ai, soldier, SoldierPose.Crouch);
                            if (plan.StepCount > 1)
                                plan.StepIndex = 1;
                        }
                        else
                        {
                            float covDistSq = (soldier.HasDestinationAssigned && soldier.targetDestination != null)
                                ? (soldier.targetDestination.GetCoverPosition() - myPos).sqrMagnitude
                                : (GetActualMoveEndpoint(ai, soldier) - myPos).sqrMagnitude;
                            ApplyDesiredMovingPose(ai, soldier, covDistSq, suppression);
                        }
                    }
                    break;

                case GoapActionId.BandageInCover:
                    plan.LockUnitCoverChange = true;
                    SetBotPose(ai, soldier, SoldierPose.Crouch);
                    if (soldier.isBleeding && !soldier.isInHandsAnimation && !soldier.isInBodyAnimation)
                    {
                        ai.TryStopBleeding();
                    }
                    break;

                case GoapActionId.RepositionBehindWall:
                    if (TryFindShelteredSideOfWall(myPos, board.ThreatDirection, soldier.transform.root, out Vector3 shelteredPos))
                    {
                        DestinationWithoutCover newDest = new DestinationWithoutCover(shelteredPos, SoldierPose.Crouch);
                        soldier.CoverPosition(newDest.Cast<AiDestination>());
                        ai.MoveDestination = shelteredPos;
                        if (soldier.m_pose == SoldierPose.Prone && (shelteredPos - myPos).sqrMagnitude > 3.5f * 3.5f)
                            SetBotPose(ai, soldier, SoldierPose.Crouch);
                        plan.LockUnitCoverChange = true;
                        plan.PlanExpireTime = now + 4.0f;
                        plan.NextReplanTime = Mathf.Max(plan.NextReplanTime, now + 3.2f);
                        WallSideRepositionsCount++;
                        GoapPlansExecutedCount++;
                        if ((shelteredPos - myPos).sqrMagnitude <= 1.8f * 1.8f && plan.StepCount > 1)
                        {
                            plan.StepIndex = 1;
                        }
                    }
                    break;

                case GoapActionId.MaintainRadioLinkWithLeader:
                    if (sq != null && sq.Leader != null && sq.Leader.IsAlive && sq.Leader.transform != null)
                    {
                        Vector3 leaderPos = sq.Leader.transform.position;
                        // Position the Radioman 3.5m behind the Squad Leader (away from the enemy threat line)
                        Vector3 safeRadioSpot = leaderPos - board.ThreatDirection * 3.5f;
                        plan.LockUnitCoverChange = true;
                        plan.HasMoveOverride = true;
                        plan.MoveOverrideDest = safeRadioSpot;
                        float radioDistSq = (myPos - safeRadioSpot).sqrMagnitude;
                        if (radioDistSq > 4.0f * 4.0f)
                        {
                            ApplyDesiredMovingPose(ai, soldier, radioDistSq, suppression);
                            AiCore.MoveBotTowardPosition(ai, soldier, safeRadioSpot, 0.5f, 2.8f);
                            GoapPlansExecutedCount++;
                        }
                        else if (!ai.IsMoving() && (board.HasActiveThreat || soldier.GetSuppressionValue() >= 5f || soldier.isAlerted))
                        {
                            SetBotPose(ai, soldier, SoldierPose.Crouch);
                        }
                    }
                    break;

                case GoapActionId.ThrowSmokeScreen:
                    {
                        bool smokeThrown = false;
                        if (soldier.inventory != null &&
                            now > soldier.throw_end_time + 5.0f &&
                            board.PrimaryTarget != null &&
                            (!_nextSmokeGrenadeTime.TryGetValue(ptr, out float nextSmkThrow) || now >= nextSmkThrow || nextSmkThrow - now > 60.0f) &&
                            (now >= board.SmokeActiveUntil || board.SmokeActiveUntil - now > 60.0f || (board.ThreatPosition - board.LastSmokeTargetPos).sqrMagnitude > 18f * 18f))
                        {
                            VirtualItem smokeItem = soldier.inventory.FindItemOfType<VirtualSmokeGrenade>();
                            if (smokeItem != null)
                            {
                                float dToThreat = Vector3.Distance(myPos, board.ThreatPosition);
                                if (dToThreat >= 12f && dToThreat <= soldier.GetGrenadeThrowRange(smokeItem) + 6f &&
                                    !AiCore.HasLowCeilingOverhead(myPos, 3.2f) &&
                                    AiCore.TryConsumeBotRpcBudget(0.45f))
                                {
                                    _nextSmokeGrenadeTime[ptr] = now + 32.0f;
                                    board.SmokeActiveUntil = now + 22.0f;
                                    board.LastSmokeTargetPos = board.ThreatPosition;
                                    soldier.Throw(smokeItem, board.PrimaryTarget);
                                    board.Suppressor0Ptr = ptr;
                                    board.SuppressHoldUntil = now + 6.5f;
                                    GoapSmokeScreensCount++;
                                    GoapPlansExecutedCount++;
                                    smokeThrown = true;
                                    TrySayTacticalVoice(soldier, sq, VoiceManager.VoiceClip.keepYourHeadDown, 0.1f);
                                    if (VisionConfig.DebugLogging.Value)
                                    {
                                        AiCore.LogDebug(
                                            "GoapSmoke",
                                            $"{soldier.faction} {GetPersonality(soldier)} threw GOAP smoke screen ({dToThreat:F0}m, nextStep:{plan.Step1}).",
                                            3.0f);
                                    }
                                }
                            }
                        }

                        // Never stall on Step 0 if smoke was just thrown OR if smoke couldn't be thrown right now
                        // (e.g., squadmate smoked 0.1s ago, range/ceiling changed, or RPC budget busy): immediately
                        // advance to Step 1 (and execute Step 1 right away if hands weren't used to throw!).
                        if (plan.StepCount > 1 && plan.StepIndex == 0 && plan.Step1 != GoapActionId.None)
                        {
                            plan.StepIndex = 1;
                            if (!smokeThrown && plan.Step1 != GoapActionId.ThrowSmokeScreen)
                            {
                                ExecuteGoapActionStep(ai, soldier, sq, ref board, ref plan, plan.Step1, ws, suppression, dist, now);
                            }
                        }
                        else if (!smokeThrown)
                        {
                            plan.PlanExpireTime = 0f;
                        }
                    }
                    break;

                case GoapActionId.SuppressThreatPosition:
                case GoapActionId.HoldOverwatchFire:
                    if (board.Suppressor0Ptr == IntPtr.Zero || board.Suppressor0Ptr == ptr)
                        board.Suppressor0Ptr = ptr;
                    else if (board.Suppressor1Ptr == IntPtr.Zero || board.Suppressor1Ptr == ptr)
                        board.Suppressor1Ptr = ptr;

                    board.SuppressHoldUntil = Mathf.Max(board.SuppressHoldUntil, now + 3.6f);

                    if (action == GoapActionId.HoldOverwatchFire &&
                        IsInCommittedFiringHalt(ptr, now) &&
                        AiCore.TryGetSpottableCenter(ai.visibleTarget, out Vector3 owTargetPos))
                    {
                        bool crouchClear = HasClearLineOfFireFromCrouch(soldier, owTargetPos, dist);
                        SetBotPose(ai, soldier, crouchClear ? SoldierPose.Crouch : SoldierPose.Idle, true);
                        if (!_nextBoundingHoldCountTime.TryGetValue(ptr, out float nextBoundCount) || now >= nextBoundCount)
                        {
                            _nextBoundingHoldCountTime[ptr] = now + 5.0f;
                            GoapPlansExecutedCount++;
                            TrySayTacticalVoice(soldier, sq, VoiceManager.VoiceClip.coveringFire, 0.15f);
                        }
                    }
                    break;

                case GoapActionId.MoveToFlankCover:
                    {
                        CombatCover chosenCover = null;
                        if (board.FlankLeftCover != null && (board.FlankerLeftPtr == IntPtr.Zero || board.FlankerLeftPtr == ptr))
                        {
                            board.FlankerLeftPtr = ptr;
                            chosenCover = board.FlankLeftCover;
                        }
                        else if (board.FlankRightCover != null && (board.FlankerRightPtr == IntPtr.Zero || board.FlankerRightPtr == ptr))
                        {
                            board.FlankerRightPtr = ptr;
                            chosenCover = board.FlankRightCover;
                        }

                        if (chosenCover != null)
                        {
                            board.FlankUntil = Mathf.Max(board.FlankUntil, now + 6.5f);
                            plan.LockUnitCoverChange = true;
                            plan.PlanExpireTime = now + 6.5f;
                            _localCoverProtectedUntil[ptr] = now + 6.5f;

                            if (soldier.targetDestination == null || soldier.targetDestination.Pointer != chosenCover.Pointer)
                            {
                                if (!_nextFlankCoverAssignTime.TryGetValue(ptr, out float nextAssign) || now >= nextAssign)
                                {
                                    _nextFlankCoverAssignTime[ptr] = now + 5.5f;
                                    soldier.CoverPosition(chosenCover.Cast<AiDestination>());
                                    ApplyDesiredMovingPose(ai, soldier, (chosenCover.GetCoverPosition() - myPos).sqrMagnitude, suppression);
                                    plan.NextReplanTime = Mathf.Max(plan.NextReplanTime, now + 4.5f);
                                    GoapPinAndFlankCount++;
                                    GoapPlansExecutedCount++;
                                    TrySayTacticalVoice(soldier, sq, VoiceManager.VoiceClip.imMoving, 0.25f);
                                    if (VisionConfig.DebugLogging.Value)
                                    {
                                        AiCore.LogDebug("GoapPinFlank", $"{soldier.faction} {GetPersonality(soldier)} flanking pinned enemy to lateral cover (nextStep:{plan.Step1}).", 3.0f);
                                    }
                                }
                            }
                            else if (soldier.IsOnCover() && plan.StepCount > 1)
                            {
                                plan.StepIndex = 1;
                            }
                        }
                    }
                    break;

                case GoapActionId.FlushThreatWithFrag:
                    if (VisionConfig.EnableLastKnownSuppression.Value)
                    {
                        board.BreacherPtr = ptr;
                        board.BreachUntil = now + 4.0f;
                        if (!TryFlushLastKnownEnemyWithGrenade(ai, soldier) &&
                            board.HasActiveThreat &&
                            board.PrimaryTarget != null &&
                            board.PrimaryTarget.Pointer != IntPtr.Zero &&
                            !ai.HasEnemyAndCanFireAtHim &&
                            !ai.IsMoving() &&
                            !soldier.isInHandsAnimation &&
                            !soldier.isInBodyAnimation &&
                            !soldier.IsReloading &&
                            soldier.inventory != null &&
                            now > soldier.throw_end_time + 6.0f &&
                            (!_nextSuppressiveGrenadeTime.TryGetValue(ptr, out float nextG) || now >= nextG))
                        {
                            Soldier targetSol = board.PrimaryTarget.TryCast<Soldier>();
                            if (targetSol != null && targetSol.IsAlive && targetSol.faction != soldier.faction)
                            {
                                Vector3 tPos = targetSol.GetCenterOfUnit();
                                float dFrag = Vector3.Distance(myPos, tPos);
                                VirtualItem fragItem = soldier.inventory.FindItemOfType<VirtualGrenade>();
                                if (fragItem != null &&
                                    dFrag >= 10.0f &&
                                    dFrag <= Mathf.Min(29.0f, soldier.GetGrenadeThrowRange(fragItem)) &&
                                    IsSafeToThrowGrenadeAt(soldier, tPos, dFrag, 11.5f) &&
                                    AiCore.TryConsumeBotRpcBudget(0.45f))
                                {
                                    _nextSuppressiveGrenadeTime[ptr] = now + 14.0f;
                                    soldier.Throw(fragItem, board.PrimaryTarget);
                                    GoapPlansExecutedCount++;
                                    TrySayTacticalVoice(soldier, sq, VoiceManager.VoiceClip.granade, 0.05f);
                                    if (VisionConfig.DebugLogging.Value)
                                    {
                                        AiCore.LogDebug(
                                            "GoapFrag",
                                            $"{soldier.faction} {GetPersonality(soldier)} lobbed GOAP frag grenade to flush cover ({dFrag:F0}m).",
                                            3.0f);
                                    }
                                }
                            }
                        }
                    }
                    break;

                case GoapActionId.BoundWithOverwatch:
                    if (ai.IsMoving())
                    {
                        ApplyDesiredMovingPose(ai, soldier, (GetActualMoveEndpoint(ai, soldier) - myPos).sqrMagnitude, suppression);
                    }
                    else if ((ws & GoapStateBits.ObjectiveCapContestUrgent) != 0 && board.HasActiveObjectiveTask)
                    {
                        Vector3 toObj = board.ObjectivePosition - myPos;
                        toObj.y = 0f;
                        Vector3 objDir = toObj.sqrMagnitude > 0.01f ? toObj.normalized : board.ThreatDirection;
                        TryAdvanceBoundTowardThreat(ai, soldier, sq, myPos, board.ObjectivePosition, objDir, now, ref plan);
                    }
                    else if (board.HasActiveThreat &&
                             board.IsThreatBeingPinned(now) &&
                             !ai.HasEnemyAndCanFireAtHim &&
                             (myPos - board.ThreatPosition).sqrMagnitude > 24f * 24f)
                    {
                        TryAdvanceBoundTowardThreat(ai, soldier, sq, myPos, board.ThreatPosition, board.ThreatDirection, now, ref plan);
                    }
                    break;

                case GoapActionId.ScavengeSupplyFromCorpse:
                    {
                        // Reuse the BotScavengeCache populated by BuildSoldierWorldState (eliminates duplicate 50-line evaluation per tick)
                        if (_botScavengeCaches.TryGetValue(ptr, out BotScavengeCache scavCache) &&
                            (scavCache.TargetFloorItem != null ||
                             scavCache.TargetRefillCrate != null ||
                             scavCache.TargetInventory != null ||
                             scavCache.TargetBuddyDonor != null ||
                             scavCache.TargetNeedyAllyToFeed != null))
                        {
                            if ((scavCache.IsBuddyBandageRefill || scavCache.IsBuddyAmmoBorrow) &&
                                scavCache.TargetBuddyDonor != null &&
                                scavCache.TargetBuddyDonor.Pointer != IntPtr.Zero &&
                                scavCache.TargetBuddyDonor.IsAlive &&
                                scavCache.TargetBuddyDonor.transform != null)
                            {
                                scavCache.TargetPosition = scavCache.TargetBuddyDonor.transform.position;
                            }
                            else if (scavCache.IsFeedingNeedyAlly &&
                                     scavCache.TargetNeedyAllyToFeed != null &&
                                     scavCache.TargetNeedyAllyToFeed.Pointer != IntPtr.Zero &&
                                     scavCache.TargetNeedyAllyToFeed.IsAlive &&
                                     scavCache.TargetNeedyAllyToFeed.transform != null)
                            {
                                scavCache.TargetPosition = scavCache.TargetNeedyAllyToFeed.transform.position;
                            }

                            bool isBuddyHandoff = scavCache.IsBuddyBandageRefill ||
                                                  scavCache.IsBuddyAmmoBorrow ||
                                                  scavCache.IsFeedingNeedyAlly;
                            bool outOfAmmo = scavCache.NeedAmmo;
                            bool outOfBandage = scavCache.NeedBandage;
                            Vector3 targetPos = scavCache.TargetPosition;
                            float dSq = (targetPos - myPos).sqrMagnitude;
                            float triggerRadius = isBuddyHandoff ? 3.8f : 3.3f;
                            ApplyDesiredMovingPose(ai, soldier, dSq, suppression);
                            if (dSq <= triggerRadius * triggerRadius)
                            {
                                bool looted = false;
                                string lootedDesc = "supply";

                                // 0A. Buddy Bandage Refill from living friendly Medic
                                if (scavCache.IsBuddyBandageRefill &&
                                    scavCache.TargetBuddyDonor != null &&
                                    scavCache.TargetBuddyDonor.Pointer != IntPtr.Zero &&
                                    scavCache.TargetBuddyDonor.IsAlive &&
                                    !scavCache.TargetBuddyDonor.IsInjured)
                                {
                                    if (!Photon.Pun.PhotonNetwork.InRoom || AiCore.TryConsumeBotRpcBudget(0.25f))
                                    {
                                        soldier.RefillBandages();
                                        _nextBuddyBandageRefillTime[ptr] = now + 45.0f;
                                        BuddyResuppliesCount++;
                                        looted = true;
                                        lootedDesc = "buddy bandage from medic";
                                        TrySayTacticalVoice(soldier, sq, VoiceManager.VoiceClip.thankYou, 0.25f);
                                    }
                                }
                                // 0B. Buddy Ammo Borrow from living friendly Ammo Carrier / Support rifleman
                                else if (scavCache.IsBuddyAmmoBorrow &&
                                         scavCache.TargetBuddyDonor != null &&
                                         scavCache.TargetBuddyDonor.Pointer != IntPtr.Zero &&
                                         scavCache.TargetBuddyDonor.IsAlive &&
                                         !scavCache.TargetBuddyDonor.IsInjured)
                                {
                                    if (!Photon.Pun.PhotonNetwork.InRoom || AiCore.TryConsumeBotRpcBudget(0.30f))
                                    {
                                        Soldier donor = scavCache.TargetBuddyDonor;
                                        donor.BorrowAmmos(soldier);
                                        // Creature.BorrowAmmos (0x180A51A60) only transfers ammo if donor.inventory contains a magazine/clip
                                        // matching soldier.GetHeldGun(0). Always refill primary/stowed weapons when resupplying from an
                                        // Ammo Box Carrier or if caliber mismatch left the weapon dry, and enforce a 35s cooldown!
                                        RefillSoldierPrimaryAndStowedWeapons(soldier, 2);
                                        _nextBuddyAmmoResupplyTime[ptr] = now + 35.0f;
                                        BuddyResuppliesCount++;
                                        looted = true;
                                        lootedDesc = "buddy ammo from carrier";
                                        TrySayTacticalVoice(soldier, sq, VoiceManager.VoiceClip.thankYou, 0.25f);
                                    }
                                }
                                // 0C. Support / Ammo Carrier proactively feeding a dry MG gunner or AT specialist
                                else if (scavCache.IsFeedingNeedyAlly &&
                                         scavCache.TargetNeedyAllyToFeed != null &&
                                         scavCache.TargetNeedyAllyToFeed.Pointer != IntPtr.Zero &&
                                         scavCache.TargetNeedyAllyToFeed.IsAlive &&
                                         !scavCache.TargetNeedyAllyToFeed.IsInjured)
                                {
                                    if (!Photon.Pun.PhotonNetwork.InRoom || AiCore.TryConsumeBotRpcBudget(0.30f))
                                    {
                                        Soldier ally = scavCache.TargetNeedyAllyToFeed;
                                        soldier.BorrowAmmos(ally);
                                        if (AiCore.IsLocallyOwnedBot(ally))
                                        {
                                            RefillSoldierPrimaryAndStowedWeapons(ally, 2);
                                        }
                                        _nextBuddyAmmoResupplyTime[ptr] = now + 35.0f;
                                        _nextBuddyAmmoResupplyTime[ally.Pointer] = now + 35.0f;
                                        BuddyResuppliesCount++;
                                        looted = true;
                                        lootedDesc = "fed ammo to needy MG/AT squadmate";
                                    }
                                }
                                // 0D. Placed Map AmmoRefillCrate (trench / bunker / strongpoint resupply crate)
                                else if (scavCache.TargetRefillCrate != null &&
                                         scavCache.TargetRefillCrate.Pointer != IntPtr.Zero &&
                                         scavCache.TargetRefillCrate.gameObject != null &&
                                         scavCache.TargetRefillCrate.gameObject.activeInHierarchy)
                                {
                                    AmmoRefillCrate crate = scavCache.TargetRefillCrate;
                                    CachedBoxOverrideInfo ov = GetCachedBoxOverrideInfo(
                                        crate.Pointer,
                                        crate.overrideContent,
                                        crate.allowTakeWeaponIntoInventory);
                                    looted = TryResupplyFromBoxOrCrate(
                                        soldier,
                                        crate.overrideContent,
                                        crate.allowTakeWeaponIntoInventory,
                                        ov,
                                        outOfAmmo,
                                        scavCache.NeedFragGrenade,
                                        scavCache.NeedSmokeGrenade,
                                        scavCache.IsAtWeaponPickup,
                                        outOfBandage,
                                        now,
                                        ref plan,
                                        out lootedDesc);
                                }
                                // 1. Pick up physical floor items (dropped AT launchers, primary rifles/SMGs/MGs, helmets, radios, DroppableAmmoBox, grenades, magazines, syringes)
                                else if (IsFloorItemFreeToPickUp(scavCache.TargetFloorItem))
                                {
                                    DroppableAmmoBox floorBox = scavCache.IsDroppableAmmoBox
                                        ? scavCache.TargetFloorItem.TryCast<DroppableAmmoBox>()
                                        : null;
                                    GenericGun floorGun = floorBox == null ? scavCache.TargetFloorItem.TryCast<GenericGun>() : null;

                                    if (floorBox != null)
                                    {
                                        // Resupply from floor-dropped ammo box without locking out the player's 30s UI prompt or deleting the box!
                                        CachedBoxOverrideInfo ov = GetCachedBoxOverrideInfo(floorBox.Pointer, floorBox.overrideContent, true);
                                        looted = TryResupplyFromBoxOrCrate(
                                            soldier,
                                            floorBox.overrideContent,
                                            true,
                                            ov,
                                            outOfAmmo,
                                            scavCache.NeedFragGrenade,
                                            scavCache.NeedSmokeGrenade,
                                            scavCache.IsAtWeaponPickup,
                                            outOfBandage,
                                            now,
                                            ref plan,
                                            out lootedDesc);
                                    }
                                    else if (floorGun != null)
                                    {
                                        bool canSwitchWeaponNow = !soldier.IsPlayer() &&
                                                                  !soldier.isInHandsAnimation &&
                                                                  !soldier.isInBodyAnimation &&
                                                                  soldier.CanSwitchHeldItem &&
                                                                  (now >= _nextGlobalBotWeaponPickupTime || now + 5.0f < _nextGlobalBotWeaponPickupTime);

                                        if (canSwitchWeaponNow && (!Photon.Pun.PhotonNetwork.InRoom || AiCore.TryConsumeBotRpcBudget(0.35f)))
                                        {
                                            int slot = floorGun.IsAtWeapon() ? 1 : 0;
                                            soldier.SetGunTriggerPressed(false);
                                            soldier.PickUpItem(scavCache.TargetFloorItem.gameObject, slot);
                                            soldier.RefillAmmoForWearedWeapon(slot, true, null, false, 3);
                                            _nextGlobalBotWeaponPickupTime = now + 2.20f;
                                            _nextWeaponUpgradeAllowedTime[ptr] = now + (scavCache.IsRoleWeaponUpgrade ? 90.0f : 20.0f);
                                            plan.NextReplanTime = Mathf.Max(plan.NextReplanTime, now + 2.50f);
                                            looted = true;
                                            if (scavCache.IsRoleWeaponUpgrade)
                                            {
                                                RoleWeaponUpgradesCount++;
                                                lootedDesc = $"upgraded primary ({floorGun.name})";
                                            }
                                            else
                                            {
                                                lootedDesc = floorGun.IsAtWeapon() ? "dropped AT weapon" : "dropped firearm";
                                            }
                                        }
                                        else
                                        {
                                            _nextWeaponUpgradeAllowedTime[ptr] = now + 8.0f;
                                        }
                                    }
                                    else if ((scavCache.IsHelmetPickup || scavCache.IsRadioPackPickup) && soldier.inventory != null)
                                    {
                                        if (!Photon.Pun.PhotonNetwork.InRoom || AiCore.TryConsumeBotRpcBudget(0.35f))
                                        {
                                            VirtualItem addedWearable = soldier.inventory.AddItemToInventory(scavCache.TargetFloorItem);
                                            if (addedWearable != null)
                                            {
                                                ResourcesManager.SynchDestroy(scavCache.TargetFloorItem.gameObject, true);
                                                soldier.SetWerable(addedWearable, true);
                                                looted = true;
                                                if (scavCache.IsRadioPackPickup)
                                                {
                                                    FieldRadiosInheritedCount++;
                                                    lootedDesc = "floor radio pack";
                                                }
                                                else
                                                {
                                                    HelmetsRecoveredCount++;
                                                    lootedDesc = "helmet from ground";
                                                    if (IsFriendlyPlayerWithinDistance(soldier, myPos, 7.5f))
                                                    {
                                                        TrySayTacticalVoice(soldier, sq, VoiceManager.VoiceClip.thankYou, 0.05f);
                                                    }
                                                }
                                            }
                                        }
                                    }
                                    else if (soldier.inventory != null && soldier.inventory.AddItemToInventoryAndDestroyInstance(scavCache.TargetFloorItem))
                                    {
                                        if (outOfAmmo)
                                            RefillSoldierPrimaryAndStowedWeapons(soldier, 2);
                                        if (scavCache.IsMedicSyringePickup && soldier.inventory.FindItemOfType<VirtualSyringe>() != null)
                                            soldier.hasSyringe = true;
                                        looted = true;
                                        if (scavCache.IsAtWeaponPickup)
                                            lootedDesc = "floor AT grenade";
                                        else if (scavCache.NeedFragGrenade)
                                            lootedDesc = "floor frag grenade";
                                        else if (scavCache.NeedSmokeGrenade)
                                            lootedDesc = "floor smoke grenade";
                                        else if (scavCache.IsMedicSyringePickup)
                                            lootedDesc = "fallen medic syringe";
                                        else
                                            lootedDesc = "floor ammo/bandage";
                                    }
                                }
                                // 2. Scavenge from verified dead corpse inventory or supply crate
                                else if (IsInventoryValidCorpseOrCrate(
                                             scavCache.TargetInventory,
                                             soldier.faction,
                                             outOfAmmo || scavCache.NeedFragGrenade || scavCache.NeedSmokeGrenade,
                                             outOfBandage || scavCache.IsMedicSyringePickup,
                                             out _,
                                             out bool ownerStillDeadOrCrate))
                                {
                                    InventoryManager targetInv = scavCache.TargetInventory;
                                    bool canSwitchWeaponNow = ownerStillDeadOrCrate &&
                                                              !soldier.IsPlayer() &&
                                                              !soldier.isInHandsAnimation &&
                                                              !soldier.isInBodyAnimation &&
                                                              soldier.CanSwitchHeldItem &&
                                                              (now >= _nextGlobalBotWeaponPickupTime || now + 5.0f < _nextGlobalBotWeaponPickupTime);

                                    if ((scavCache.IsRadioPackPickup || scavCache.IsHelmetPickup) &&
                                        ownerStillDeadOrCrate &&
                                        soldier.inventory != null &&
                                        scavCache.TargetInventoryWearable != null &&
                                        targetInv.Contains(scavCache.TargetInventoryWearable))
                                    {
                                        if (!Photon.Pun.PhotonNetwork.InRoom || AiCore.TryConsumeBotRpcBudget(0.35f))
                                        {
                                            VirtualItem wearable = scavCache.TargetInventoryWearable;
                                            // InventoryManager.TakeIntoInventory (0x1807E50E0) always returns false (xor al, al)
                                            // and automatically calls soldier.SetWerable(copy, true) for VirtualWearable items!
                                            soldier.inventory.TakeIntoInventory(wearable, targetInv);
                                            if (!targetInv.Contains(wearable))
                                            {
                                                looted = true;
                                                if (scavCache.IsRadioPackPickup)
                                                {
                                                    FieldRadiosInheritedCount++;
                                                    lootedDesc = "field radio pack from fallen soldier";
                                                }
                                                else
                                                {
                                                    HelmetsRecoveredCount++;
                                                    lootedDesc = "helmet from fallen comrade";
                                                }
                                            }
                                        }
                                    }
                                    else if (scavCache.IsAtWeaponPickup && ownerStillDeadOrCrate)
                                    {
                                        if (scavCache.TargetInventoryAtWeapon != null && targetInv.Contains(scavCache.TargetInventoryAtWeapon))
                                        {
                                            if (canSwitchWeaponNow && (!Photon.Pun.PhotonNetwork.InRoom || AiCore.TryConsumeBotRpcBudget(0.35f)))
                                            {
                                                soldier.SetGunTriggerPressed(false);
                                                soldier.PickUpItemFromInventory(scavCache.TargetInventoryAtWeapon, targetInv, 1);
                                                soldier.RefillAmmoForWearedWeapon(1, true, null, false, 2);
                                                _nextGlobalBotWeaponPickupTime = now + 2.20f;
                                                _nextWeaponUpgradeAllowedTime[ptr] = now + 20.0f;
                                                plan.NextReplanTime = Mathf.Max(plan.NextReplanTime, now + 2.50f);
                                                looted = true;
                                                lootedDesc = "AT launcher from body/crate";
                                            }
                                            else
                                            {
                                                _nextWeaponUpgradeAllowedTime[ptr] = now + 8.0f;
                                            }
                                        }
                                        else if (soldier.inventory != null)
                                        {
                                            VirtualItem atGren = targetInv.FindItemOfType<VirtualATGrenade>();
                                            if (atGren != null)
                                            {
                                                soldier.inventory.TakeIntoInventory(atGren, targetInv);
                                                if (!targetInv.Contains(atGren))
                                                {
                                                    looted = true;
                                                    lootedDesc = "AT grenade from body/crate";
                                                }
                                            }
                                        }
                                    }
                                    else if (scavCache.IsRoleWeaponUpgrade &&
                                             scavCache.TargetInventoryPrimaryWeapon != null &&
                                             targetInv.Contains(scavCache.TargetInventoryPrimaryWeapon))
                                    {
                                        if (canSwitchWeaponNow && (!Photon.Pun.PhotonNetwork.InRoom || AiCore.TryConsumeBotRpcBudget(0.35f)))
                                        {
                                            soldier.SetGunTriggerPressed(false);
                                            soldier.PickUpItemFromInventory(scavCache.TargetInventoryPrimaryWeapon, targetInv, 0);
                                            soldier.RefillAmmoForWearedWeapon(0, true, null, false, 3);
                                            _nextGlobalBotWeaponPickupTime = now + 2.20f;
                                            _nextWeaponUpgradeAllowedTime[ptr] = now + 90.0f;
                                            plan.NextReplanTime = Mathf.Max(plan.NextReplanTime, now + 2.50f);
                                            RoleWeaponUpgradesCount++;
                                            looted = true;
                                            lootedDesc = "role-matched primary from body/crate";
                                        }
                                        else
                                        {
                                            _nextWeaponUpgradeAllowedTime[ptr] = now + 8.0f;
                                        }
                                    }

                                    if (scavCache.NeedFragGrenade &&
                                        soldier.inventory != null &&
                                        soldier.inventory.FindItemOfType<VirtualGrenade>() == null)
                                    {
                                        VirtualItem fg = targetInv.FindItemOfType<VirtualGrenade>();
                                        if (fg != null)
                                        {
                                            soldier.inventory.TakeIntoInventory(fg, targetInv);
                                            if (!targetInv.Contains(fg))
                                            {
                                                looted = true;
                                                if (lootedDesc == "supply")
                                                    lootedDesc = "frag grenade from body/crate";
                                            }
                                        }
                                    }

                                    if (scavCache.NeedSmokeGrenade &&
                                        soldier.inventory != null &&
                                        soldier.inventory.FindItemOfType<VirtualSmokeGrenade>() == null)
                                    {
                                        VirtualItem sg = targetInv.FindItemOfType<VirtualSmokeGrenade>();
                                        if (sg != null)
                                        {
                                            soldier.inventory.TakeIntoInventory(sg, targetInv);
                                            if (!targetInv.Contains(sg))
                                            {
                                                looted = true;
                                                if (lootedDesc == "supply")
                                                    lootedDesc = "smoke grenade from body/crate";
                                            }
                                        }
                                    }

                                    if (scavCache.IsMedicSyringePickup && soldier.inventory != null)
                                    {
                                        VirtualItem syr = targetInv.FindItemOfType<VirtualSyringe>();
                                        if (syr != null)
                                        {
                                            soldier.inventory.TakeIntoInventory(syr, targetInv);
                                            if (!targetInv.Contains(syr))
                                            {
                                                soldier.hasSyringe = true;
                                                looted = true;
                                                if (lootedDesc == "supply")
                                                    lootedDesc = "fallen medic syringe";
                                            }
                                        }
                                    }

                                    if (outOfBandage && soldier.inventory != null)
                                    {
                                        VirtualItem bnd = targetInv.FindItemOfType<VirtualBandages>();
                                        if (bnd != null)
                                        {
                                            soldier.inventory.TakeIntoInventory(bnd, targetInv);
                                            if (soldier.inventory.FindItemOfType<VirtualBandages>() != null)
                                            {
                                                looted = true;
                                                if (lootedDesc == "supply")
                                                    lootedDesc = "bandage";
                                            }
                                        }
                                    }

                                    if (outOfAmmo)
                                    {
                                        RefillSoldierPrimaryAndStowedWeapons(soldier, 2);
                                        looted = true;
                                        if (lootedDesc == "supply")
                                            lootedDesc = "ammo";
                                    }
                                }

                                _botScavengeCaches.Remove(ptr);
                                if (looted)
                                {
                                    if (ai.moveDirectlyToward_end > now)
                                        ai.moveDirectlyToward_end = 0f;
                                    GoapPlansExecutedCount++;
                                    if (plan.StepCount > 1 && plan.StepIndex == 0)
                                        plan.StepIndex = 1;
                                    if (VisionConfig.DebugLogging.Value)
                                    {
                                        AiCore.LogDebug(
                                            "GoapScavenge",
                                            $"{soldier.faction} {GetPersonality(soldier)} scavenged {lootedDesc}.",
                                            3.0f);
                                    }
                                }
                            }
                            else
                            {
                                plan.LockUnitCoverChange = true;
                                plan.HasMoveOverride = true;
                                plan.MoveOverrideDest = targetPos;
                                // Stop at 2.35m arm's-length handoff distance for living buddies (1.15m for floor/corpse items)
                                // using overlap-safe duration / CPathFinding so bots never body-hug or drop into 0.5x slow-walk!
                                float standoff = isBuddyHandoff ? 2.35f : 1.15f;
                                AiCore.MoveBotTowardPosition(ai, soldier, targetPos, standoff, 2.8f);
                            }
                        }
                    }
                    break;

                case GoapActionId.RescueSurrenderedComrade:
                    if (board.SurrenderedComrade != null && board.SurrenderedComrade.IsAlive && board.SurrenderedComrade.HasSurrended())
                    {
                        board.RescuerPtr = ptr;
                        board.RescueUntil = now + 4.0f;
                        Soldier targetSurr = board.SurrenderedComrade;
                        float dSq = (targetSurr.transform.position - myPos).sqrMagnitude;
                        if (dSq <= 3.8f * 3.8f)
                        {
                            _liberateMoveStartTime.Remove(ptr);
                            if (ai.moveDirectlyToward_end > now)
                                ai.moveDirectlyToward_end = 0f;
                            if (AiCore.TryConsumeBotRpcBudget(0.45f))
                            {
                                string sqId = sq != null ? sq.InstanceID.ToString() : "";
                                targetSurr.UnSurrenderSynched(sqId);
                                ComradesLiberated++;
                                GoapPlansExecutedCount++;
                                AddCombatExperienceBonus(soldier, 30f);
                            }
                        }
                        else if (dSq <= 22.0f * 22.0f && !soldier.IsSquadLeader() && !ai.HasEnemyAndCanFireAtHim && soldier.GetSuppressionValue() < 35f)
                        {
                            if (!_liberateMoveCooldownUntil.TryGetValue(ptr, out float cdUntil) || now >= cdUntil)
                            {
                                if (!_liberateMoveStartTime.TryGetValue(ptr, out float startT))
                                {
                                    _liberateMoveStartTime[ptr] = now;
                                }
                                else if (now - startT > 8.5f)
                                {
                                    _liberateMoveStartTime.Remove(ptr);
                                    _liberateMoveCooldownUntil[ptr] = now + 20.0f;
                                    break;
                                }
                                plan.LockUnitCoverChange = true;
                                Vector3 surrPos = targetSurr.transform.position;
                                plan.HasMoveOverride = true;
                                plan.MoveOverrideDest = surrPos;
                                ApplyDesiredMovingPose(ai, soldier, dSq, suppression);
                                AiCore.MoveBotTowardPosition(ai, soldier, surrPos, 2.0f, 2.8f);
                            }
                        }
                    }
                    break;

                case GoapActionId.LoneWolfLinkUpOrAmbush:
                    {
                        // Bailed-out Pilots & Tank/Vehicle Crews follow a 3-step hierarchy:
                        // 1. Remount a nearby operable friendly crew vehicle with a free seat (e.g., just repaired tank!)
                        // 2. If unarmed/pistol-only or squad on retreat, fall back to friendly rear spawn
                        // 3. Otherwise, if armed with a primary firearm, link up and join the nearest friendly infantry squad!
                        SoldierPersonality lonePers = GetPersonality(soldier);
                        bool isBailedCrewOrPilot = AiCore.IsTrueVehicleCrewSquad(sq) || IsPilotOrVehicleCrewPersonality(lonePers);
                        if (isBailedCrewOrPilot)
                        {
                            if (TryRemountNearbyOperableCrewVehicle(ai, soldier, sq, myPos, 28f))
                            {
                                plan.LockUnitCoverChange = true;
                                ApplyDesiredMovingPose(ai, soldier, (GetActualMoveEndpoint(ai, soldier) - myPos).sqrMagnitude, suppression);
                                break;
                            }

                            if (ShouldBailedCrewRetreatOverJoinSquad(soldier, sq, myPos))
                            {
                                if (TryRetreatBailedCrewTowardFriendlySpawn(ai, soldier, sq, myPos, out Vector3 spawnOverride, out bool hasSpawnOverride))
                                {
                                    plan.LockUnitCoverChange = true;
                                    plan.HasMoveOverride = hasSpawnOverride;
                                    plan.MoveOverrideDest = spawnOverride;
                                    plan.IsEvacuatingBailedCrew = hasSpawnOverride;
                                    SetBotPose(ai, soldier, suppression >= 34f ? SoldierPose.Crouch : SoldierPose.Idle, suppression < 34f);
                                    BotActionTrace.RecordEvent(soldier, BotTraceEventKind.CrewEvac, "crew_retreat_spawn", dist);
                                    break;
                                }
                            }

                            if (TryLinkUpOrJoinFriendlySquad(ai, soldier, myPos, 95f, out Vector3 linkDest1))
                            {
                                DetachStaleFootCoverForEvacuation(soldier);
                                plan.LockUnitCoverChange = true;
                                plan.HasMoveOverride = true;
                                plan.MoveOverrideDest = linkDest1;
                                plan.IsEvacuatingBailedCrew = true;
                                SetBotPose(ai, soldier, suppression >= 34f ? SoldierPose.Crouch : SoldierPose.Idle, suppression < 34f);
                                BotActionTrace.RecordEvent(soldier, BotTraceEventKind.CrewEvac, "crew_link_squad", dist);
                                break;
                            }
                        }

                        bool preferAmbush = !isBailedCrewOrPilot &&
                                            (ws & GoapStateBits.OnBridgeOrNarrow) == 0 &&
                                            GetFactionCommandProfile(soldier.faction).PreferAmbushWhenAlone;
                        if (preferAmbush)
                        {
                            if (!soldier.IsOnCover() && TrySeekNearbyCover(ai, soldier))
                            {
                                plan.LockUnitCoverChange = true;
                                ApplyDesiredMovingPose(ai, soldier, (GetActualMoveEndpoint(ai, soldier) - myPos).sqrMagnitude, suppression);
                            }
                            else if (soldier.IsOnCover())
                            {
                                SetBotPose(ai, soldier, SoldierPose.Crouch);
                            }
                            else if (TryLinkUpOrJoinFriendlySquad(ai, soldier, myPos, 55f, out Vector3 linkDest2))
                            {
                                plan.LockUnitCoverChange = true;
                                plan.HasMoveOverride = true;
                                plan.MoveOverrideDest = linkDest2;
                                ApplyDesiredMovingPose(ai, soldier, (linkDest2 - myPos).sqrMagnitude, suppression);
                            }
                        }
                        else
                        {
                            if (TryLinkUpOrJoinFriendlySquad(ai, soldier, myPos, 75f, out Vector3 linkDest3))
                            {
                                plan.LockUnitCoverChange = true;
                                plan.HasMoveOverride = true;
                                plan.MoveOverrideDest = linkDest3;
                                ApplyDesiredMovingPose(ai, soldier, (linkDest3 - myPos).sqrMagnitude, suppression);
                            }
                            else if (!soldier.IsOnCover() && TrySeekNearbyCover(ai, soldier))
                            {
                                plan.LockUnitCoverChange = true;
                                ApplyDesiredMovingPose(ai, soldier, (GetActualMoveEndpoint(ai, soldier) - myPos).sqrMagnitude, suppression);
                            }
                        }
                    }
                    break;

                case GoapActionId.TacticalFallbackRetreat:
                    {
                        bool hasActiveDest = _individualFallbackUntil.TryGetValue(ptr, out float fbEnd) &&
                                             now < fbEnd &&
                                             _individualFallbackDest.TryGetValue(ptr, out Vector3 fbDest);

                        if (!hasActiveDest)
                        {
                            if (_nextIndividualFallbackTime.TryGetValue(ptr, out float nextFbAllowed) && now < nextFbAllowed)
                            {
                                _individualFallbackUntil.Remove(ptr);
                                _individualFallbackDest.Remove(ptr);
                                plan.PlanExpireTime = 0f;
                                plan.NextReplanTime = now;
                                break;
                            }

                            Vector3 threatRef = board.HasActiveThreat
                                ? board.ThreatPosition
                                : (myPos + (soldier.transform != null ? soldier.transform.forward * 16.0f : Vector3.forward * 16.0f));
                            if (ai.visibleTarget != null && AiCore.TryGetSpottableCenter(ai.visibleTarget, out Vector3 visThreat))
                                threatRef = visThreat;

                            if (!TryComputeTacticalFallbackPosition(soldier, sq, myPos, threatRef, 22.0f, 36.0f, out fbDest))
                            {
                                _nextIndividualFallbackTime[ptr] = now + 18.0f;
                                plan.PlanExpireTime = 0f;
                                plan.NextReplanTime = now;
                                break;
                            }

                            fbEnd = now + 8.5f;
                            _individualFallbackDest[ptr] = fbDest;
                            _individualFallbackUntil[ptr] = fbEnd;
                            _nextIndividualFallbackTime[ptr] = now + 38.0f;
                            TacticalRetreatsCount++;
                            GoapPlansExecutedCount++;
                            TrySayTacticalVoice(soldier, sq, VoiceManager.VoiceClip.timeToRetreat, 0.08f);
                            BotActionTrace.RecordEvent(soldier, BotTraceEventKind.FallbackRetreat, "goap_break_contact", dist);

                            if (VisionConfig.DebugLogging.Value)
                            {
                                AiCore.LogDebug(
                                    "IndivFallback",
                                    $"{soldier.faction} {GetPersonality(soldier)} executing break-contact fallback ({Vector3.Distance(myPos, fbDest):F0}m rearward).",
                                    3.0f);
                            }
                        }
                        else
                        {
                            fbDest = _individualFallbackDest[ptr];
                        }

                        float remSq = (fbDest - myPos).sqrMagnitude;
                        if (remSq <= 3.2f * 3.2f)
                        {
                            _individualFallbackUntil.Remove(ptr);
                            _individualFallbackDest.Remove(ptr);
                            SetBotPose(ai, soldier, SoldierPose.Crouch);
                            plan.PlanExpireTime = 0f;
                            plan.NextReplanTime = now;
                        }
                        else
                        {
                            plan.LockUnitCoverChange = true;
                            plan.PlanExpireTime = Mathf.Min(fbEnd, Mathf.Max(plan.PlanExpireTime, now + 4.5f));
                            _localCoverProtectedUntil[ptr] = now + 4.5f;
                            plan.HasMoveOverride = true;
                            plan.MoveOverrideDest = fbDest;
                            SetBotPose(ai, soldier, remSq <= 5.0f * 5.0f ? SoldierPose.Crouch : SoldierPose.Idle, remSq > 5.0f * 5.0f);
                            AiCore.MoveBotTowardPosition(ai, soldier, fbDest, 2.2f, 3.0f);
                        }
                    }
                    break;
            }
        }

        private static bool IsMovementGoapAction(GoapActionId id)
        {
            switch (id)
            {
                case GoapActionId.SeekEmergencyCover:
                case GoapActionId.RepositionBehindWall:
                case GoapActionId.MaintainRadioLinkWithLeader:
                case GoapActionId.MoveToFlankCover:
                case GoapActionId.BoundWithOverwatch:
                case GoapActionId.PointBlankBayonetBreach:
                case GoapActionId.RescueSurrenderedComrade:
                case GoapActionId.LoneWolfLinkUpOrAmbush:
                case GoapActionId.ScavengeSupplyFromCorpse:
                case GoapActionId.TacticalFallbackRetreat:
                    return true;
                default:
                    return false;
            }
        }

        internal static bool IsSquadOnRetreatTask(Squad sq)
        {
            if (sq == null || sq.currentTask == null)
                return false;

            if (sq.currentTask.TryCast<MissionTaskRetreat>() != null)
                return true;

            // Support Dynamic Lua Retreat Finales (SHARED/phase_4.lua on Budapest, Cassino, Shanghai, etc.)
            MissionTaskScripted mTask = sq.currentTask.TryCast<MissionTaskScripted>();
            if (mTask != null && mTask.customTextDefenders == "#retreat")
            {
                Soldier leader = sq.Leader;
                BattleData bd = BattleManager.GetCurrentBattleData();
                if (leader != null && bd != null && !string.IsNullOrEmpty(leader.faction) && !bd.IsInvaderFaction(leader.faction))
                    return true;
            }
            return false;
        }

        private static bool IsPilotOrVehicleCrewPersonality(SoldierPersonality p)
        {
            return p == SoldierPersonality.Pilot_Ace ||
                   p == SoldierPersonality.Pilot_Daredevil ||
                   p == SoldierPersonality.Pilot_Disciplined ||
                   p == SoldierPersonality.TankCrew_Brawler ||
                   p == SoldierPersonality.TankCrew_Sniper ||
                   p == SoldierPersonality.TankCrew_Support ||
                   p == SoldierPersonality.LightCrew_Outrider ||
                   p == SoldierPersonality.LightCrew_Escort ||
                   p == SoldierPersonality.LightCrew_FlakWatch;
        }

        private static void DetachStaleFootCoverForEvacuation(Soldier soldier)
        {
            if (soldier == null || soldier.Pointer == IntPtr.Zero)
                return;

            _localCoverProtectedUntil.Remove(soldier.Pointer);
            if (!soldier.HasDestinationAssigned)
                return;

            AiDestination td = soldier.targetDestination;
            if (td != null &&
                (td.TryCast<Vehicle>() != null ||
                 td.TryCast<VehicleRepairTask>() != null ||
                 td.TryCast<MedicHealingDestination>() != null))
            {
                return;
            }

            soldier.CoverPosition(null);
        }

        private static bool TryRetreatBailedCrewTowardFriendlySpawn(
            SoldierAI ai,
            Soldier soldier,
            Squad sq,
            Vector3 myPos,
            out Vector3 rawMoveDest,
            out bool hasRawMoveDest)
        {
            rawMoveDest = Vector3.zero;
            hasRawMoveDest = false;
            if (ai == null || soldier == null || string.IsNullOrEmpty(soldier.faction))
                return false;

            try
            {
                Vector3 baseDest;
                bool alreadyRetreating = IsSquadOnRetreatTask(sq);
                if (alreadyRetreating)
                {
                    baseDest = sq.currentTask.GetTaskDestination();
                    float groundY = ResourcesManager.GetTerrainAltitude(baseDest, 0f);
                    if (Mathf.Abs(baseDest.y - groundY) > 3.5f && groundY >= -0.35f)
                    {
                        baseDest.y = Mathf.Max(groundY, 0.05f);
                    }
                }
                else
                {
                    if (!AiCore.TryComputeCrewRetreatDestination(soldier.faction, myPos, out baseDest))
                        return false;

                    // Replace any null OR stale frontline tank objective with MissionTaskRetreat so SquadLeaderRoutine
                    // never re-orders bailed tankers forward toward an enemy objective!
                    if (sq != null && AiCore.IsTrueVehicleCrewSquad(sq) && !sq.IsPlayerInSquad() && BattleManager.BattleHasObjectives())
                    {
                        Soldier auth = (sq.Leader != null && sq.Leader.IsAlive) ? sq.Leader : soldier;
                        if (AiCore.IsLocallyOwnedBot(auth))
                        {
                            var retreatTask = new MissionTaskRetreat(baseDest, sq, false);
                            sq.currentTask = retreatTask.Cast<SquadTask>();
                            sq.HoldArea(baseDest, 15.0f, false);
                            if (ResourcesManager.instance != null)
                            {
                                ResourcesManager.instance.StartCoroutine(sq.UpdateSquadTaskUI());
                            }
                        }
                    }
                }

                // Deterministic 1.8m-4.0m personal offset well inside MissionTaskRetreat's 15m despawn sphere
                // prevents 3-4 crewmen from body-stacking while ensuring every survivor triggers Soldier.OnRetreat!
                int h = AiCore.GetDeterministicHash(soldier.Pointer, 733);
                float angRad = ((h & 0xFF) * (1f / 255f)) * (Mathf.PI * 2f);
                float rad = 1.8f + (((h >> 8) & 0xFF) * (1f / 255f)) * 2.2f;
                Vector3 spreadDest = new Vector3(
                    baseDest.x + Mathf.Cos(angRad) * rad,
                    baseDest.y,
                    baseDest.z + Mathf.Sin(angRad) * rad);
                float spreadAlt = ResourcesManager.GetTerrainAltitude(spreadDest, 0f);
                if (spreadAlt >= 0.05f && Mathf.Abs(spreadAlt - baseDest.y) <= 4.0f)
                {
                    spreadDest.y = spreadAlt;
                }
                else
                {
                    spreadDest = baseDest;
                }

                DetachStaleFootCoverForEvacuation(soldier);
                rawMoveDest = spreadDest;
                hasRawMoveDest = true;
                AiCore.MoveBotTowardPosition(ai, soldier, spreadDest, 0f, 3.2f);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void EvaluateRoleWeaponUpgradeNeed(
            Soldier soldier,
            Squad sq,
            SoldierPersonality pers,
            bool isMedicRole,
            bool onRetreatTask,
            ref SquadBlackboard board,
            float now,
            out bool needUrgentPrimary,
            out bool wantSquadMg,
            out bool wantRoleUpgrade)
        {
            needUrgentPrimary = false;
            wantSquadMg = false;
            wantRoleUpgrade = false;

            if (soldier.isInHandsAnimation || soldier.isInBodyAnimation || !soldier.CanSwitchHeldItem || soldier.HoldsInventoryItem)
                return;

            IntPtr ptr = soldier.Pointer;
            if (_nextWeaponUpgradeAllowedTime.TryGetValue(ptr, out float nextAllowed) && now < nextAllowed)
                return;

            // CRITICAL: In Easy Red 2 (Soldier.SetItem @ 0x180765D10, Soldier.SwitchTo @ 0x18076AD20),
            // wearedItems has 3 slots: Slot 0 (hands), Slot 1 (AT/special), and Slot 2 (holster/stowed weapon).
            // When a Squad Leader or Rifleman switches to their sidearm (Slot 2), Slot 0 and Slot 2 swap—putting
            // the pistol in Slot 0 (GetHeldGun(0)) and stowing their primary rifle/SMG in Slot 2 (GetHeldGun(2))!
            // Always inspect all 3 slots (plus GetHeldItem_inventory(0..2)) so a soldier holding a sidearm
            // NEVER falsely triggers needUrgentPrimary and swaps their pistol for a random bolt-action rifle!
            GenericGun g0 = soldier.GetHeldGun(0);
            GenericGun g1 = soldier.GetHeldGun(1);
            GenericGun g2 = soldier.GetHeldGun(2);

            bool g0RealPrimary = g0 != null && !g0.IsPistol() && !g0.IsAtWeapon();
            bool g1RealPrimary = g1 != null && !g1.IsPistol() && !g1.IsAtWeapon();
            bool g2RealPrimary = g2 != null && !g2.IsPistol() && !g2.IsAtWeapon();

            bool hasInventoryRealPrimary = false;
            bool hasInventoryMg = false;
            if (!g0RealPrimary && !g1RealPrimary && !g2RealPrimary)
            {
                for (int slotIdx = 0; slotIdx <= 2; slotIdx++)
                {
                    VirtualItem vSlot = soldier.GetHeldItem_inventory(slotIdx);
                    if (vSlot != null && TryGetVirtualWeaponArchetype(vSlot, out CachedWeaponArchetype slotArch))
                    {
                        if (!slotArch.IsPistol && !slotArch.IsAtWeapon)
                            hasInventoryRealPrimary = true;
                        if (slotArch.IsMg)
                            hasInventoryMg = true;
                    }
                }
            }

            if (!g0RealPrimary && !g1RealPrimary && !g2RealPrimary && !hasInventoryRealPrimary)
            {
                bool isRetreatingCrew = onRetreatTask && sq != null && AiCore.IsTrueVehicleCrewSquad(sq);
                if (!isRetreatingCrew && (!onRetreatTask || board.HasActiveThreat || soldier.GetSuppressionValue() >= 8))
                    needUrgentPrimary = true;
                return;
            }

            // Never perform a Slot 0 primary-weapon replacement while a primary weapon is stowed in Slot 2
            // (which would overwrite the sidearm in Slot 0 and leave the soldier carrying two primaries!).
            if (onRetreatTask || !g0RealPrimary)
                return;

            bool holdingMg = soldier.hasMg ||
                             hasInventoryMg ||
                             (g0 != null && (g0.integratedBipod != null || g0.IsHeavyMg())) ||
                             (g1 != null && (g1.integratedBipod != null || g1.IsHeavyMg())) ||
                             (g2 != null && (g2.integratedBipod != null || g2.IsHeavyMg()));

            // 2. Fallen Squad MG Inheritance: if the squad has 0 alive MG gunners, a rifleman inherits a nearby MG!
            if (!holdingMg &&
                !board.HasAliveMachineGunner &&
                sq != null && !AiCore.IsTrueVehicleCrewSquad(sq) &&
                !soldier.IsSquadLeader() && !soldier.IsRadioman() && !isMedicRole)
            {
                wantSquadMg = true;
            }

            // 3. Role-Matched Primary Upgrades (Gunner missing MG, Marksman missing scope, Assaulter stuck with bolt-action):
            if ((pers == SoldierPersonality.Gunner_Anchor ||
                 pers == SoldierPersonality.Gunner_AssaultMG ||
                 pers == SoldierPersonality.Gunner_Overwatch) && !holdingMg)
            {
                wantRoleUpgrade = true;
            }
            else if ((pers == SoldierPersonality.Marksman_Deadeye ||
                      pers == SoldierPersonality.Marksman_Stalker ||
                      pers == SoldierPersonality.Marksman_Spotter) &&
                     !g0.IsSniperRifle() && !g0.IsUsingScope && !g0.HasScopeWithMagnification())
            {
                wantRoleUpgrade = true;
            }
            else if ((pers == SoldierPersonality.Assaulter_Shock ||
                      pers == SoldierPersonality.Assaulter_Breacher ||
                      pers == SoldierPersonality.Assaulter_Skirmisher) &&
                     g0.isManualBoltOperated && !g0.IsFullAuto())
            {
                wantRoleUpgrade = true;
            }
        }

        private static bool IsWeaponArchetypeValidUpgrade(
            bool isPistol,
            bool isAtWeapon,
            bool isMg,
            bool isScopedOrSniper,
            bool isManualBoltOnly,
            SoldierPersonality pers,
            bool needUrgentPrimary,
            bool wantSquadMg,
            bool wantRoleUpgrade)
        {
            if (isPistol || isAtWeapon)
                return false;

            if (needUrgentPrimary)
                return true;

            if (wantSquadMg && isMg)
                return true;

            if (!wantRoleUpgrade)
                return false;

            if (pers == SoldierPersonality.Gunner_Anchor ||
                pers == SoldierPersonality.Gunner_AssaultMG ||
                pers == SoldierPersonality.Gunner_Overwatch)
            {
                return isMg;
            }

            if (pers == SoldierPersonality.Marksman_Deadeye ||
                pers == SoldierPersonality.Marksman_Stalker ||
                pers == SoldierPersonality.Marksman_Spotter)
            {
                return isScopedOrSniper;
            }

            if (pers == SoldierPersonality.Assaulter_Shock ||
                pers == SoldierPersonality.Assaulter_Breacher ||
                pers == SoldierPersonality.Assaulter_Skirmisher)
            {
                return !isManualBoltOnly && !isMg;
            }

            return false;
        }

        private static bool IsCandidateGunValidUpgrade(
            GenericGun cand,
            SoldierPersonality pers,
            bool needUrgentPrimary,
            bool wantSquadMg,
            bool wantRoleUpgrade,
            bool requireLoadedChamberOrMag)
        {
            if (cand == null)
                return false;

            if (requireLoadedChamberOrMag && cand.GetCurrentAmmoCount() <= 0)
                return false;

            return IsWeaponArchetypeValidUpgrade(
                cand.IsPistol(),
                cand.IsAtWeapon(),
                cand.integratedBipod != null || cand.IsHeavyMg(),
                cand.IsSniperRifle() || cand.IsUsingScope || cand.HasScopeWithMagnification(),
                cand.isManualBoltOperated && !cand.IsFullAuto(),
                pers,
                needUrgentPrimary,
                wantSquadMg,
                wantRoleUpgrade);
        }

        private static void TryAdvanceBoundTowardThreat(
            SoldierAI ai,
            Soldier soldier,
            Squad sq,
            Vector3 myPos,
            Vector3 threatPos,
            Vector3 threatDir,
            float now,
            ref BotGoapPlan plan)
        {
            IntPtr ptr = soldier.Pointer;
            if (_nextLocalCoverSeekTime.TryGetValue(ptr, out float nextSeek) && now < nextSeek)
                return;

            _nextLocalCoverSeekTime[ptr] = now + 3.0f;

            bool isOnBridge = VisionConfig.EnableBridgeCrossingDiscipline.Value &&
                              AiCore.IsOnBridgeOrNarrowCrossing(myPos);

            // Walking-Pace Friendly Tank Escort: if a friendly tank nearby (<= 17.5m) is rolling forward
            // toward the threat, advance in its sheltered rear-quarter wake using its hull as mobile armor cover!
            if (TryFindTankHullCoverOrEscortSpot(soldier, myPos, threatDir, true, out Vector3 tankSpot, out bool isMovingEscort) &&
                isMovingEscort &&
                (!isOnBridge || AiCore.IsSafeBridgeDeckStep(myPos, tankSpot, out tankSpot)))
            {
                SetBotPose(ai, soldier, SoldierPose.Crouch);
                ai.MoveDirectlyToward(tankSpot, 2.8f);
                plan.LockUnitCoverChange = true;
                plan.PlanExpireTime = now + 3.8f;
                plan.NextReplanTime = Mathf.Max(plan.NextReplanTime, now + 2.4f);
                _localCoverProtectedUntil[ptr] = now + 3.8f;
                TankHullCoverUsesCount++;
                GoapPlansExecutedCount++;
                return;
            }

            var octree = CoverManager.staticCovers;
            CombatCover bestAdvanceCover = null;
            if (octree != null)
            {
                Vector3 searchCenter = myPos + threatDir * 12.0f;
                var nearby = octree.GetNearby(searchCenter, 14.0f);
                if (nearby != null && nearby.Length > 0)
                {
                    float myDistToThreatSq = (threatPos - myPos).sqrMagnitude;
                    var unreachable = soldier.nonReachableCovers;
                    float bestScore = -999f;

                    int count = nearby.Length;
                    for (int i = 0; i < count; i++)
                    {
                        AiDestination dest = nearby[i];
                        if (dest == null || dest.Pointer == IntPtr.Zero)
                            continue;
                        if (unreachable != null && unreachable.Contains(dest))
                            continue;

                        CombatCover cc = dest.TryCast<CombatCover>();
                        if (cc == null || cc.destroyed)
                            continue;

                        Vector3 covPos = cc.GetCoverPosition();
                        if (Mathf.Abs(covPos.y - myPos.y) > (isOnBridge ? 1.15f : 2.8f))
                            continue;

                        float dFromMeSq = (covPos - myPos).sqrMagnitude;
                        if (dFromMeSq < 5.5f * 5.5f || dFromMeSq > 22.0f * 22.0f)
                            continue;

                        float covDistToThreatSq = (threatPos - covPos).sqrMagnitude;
                        if (covDistToThreatSq >= myDistToThreatSq - 25.0f || covDistToThreatSq < 8.0f * 8.0f)
                            continue;

                        float advanceGain = Vector3.Dot(covPos - myPos, threatDir);
                        if (advanceGain < 4.5f)
                            continue;

                        float score = advanceGain - Mathf.Sqrt(dFromMeSq) * 0.25f;
                        if (score <= bestScore)
                            continue;

                        if (isOnBridge && !AiCore.IsSafeBridgeDeckStep(myPos, covPos, out _))
                            continue;

                        if (!cc.IsCoverAvailable(threatDir, AiCore.GetCachedFaction(soldier)))
                            continue;

                        bestScore = score;
                        bestAdvanceCover = cc;
                    }
                }
            }

            if (bestAdvanceCover != null)
            {
                soldier.CoverPosition(bestAdvanceCover.Cast<AiDestination>());
                ApplyDesiredMovingPose(ai, soldier, (bestAdvanceCover.GetCoverPosition() - myPos).sqrMagnitude, soldier.GetSuppressionValue());
                plan.LockUnitCoverChange = true;
                plan.PlanExpireTime = now + 5.5f;
                plan.NextReplanTime = Mathf.Max(plan.NextReplanTime, now + 3.6f);
                _localCoverProtectedUntil[ptr] = now + 5.5f;
                GoapPlansExecutedCount++;
                TrySayTacticalVoice(soldier, sq, VoiceManager.VoiceClip.imMoving, 0.20f);
                return;
            }

            // Bridge Parapet-to-Parapet Forward Bound when staticCovers nodes aren't baked along the bridge span
            if (isOnBridge)
            {
                Vector3 probeAhead = myPos + threatDir * 10.5f;
                if (AiCore.IsSafeBridgeDeckStep(myPos, probeAhead, out Vector3 safeAheadDeck))
                {
                    Vector3 bridgeBoundSpot = safeAheadDeck;
                    if (TryFindBridgeParapetOrPillarSpot(soldier, safeAheadDeck, threatDir, out Vector3 aheadParapet) &&
                        AiCore.IsSafeBridgeDeckStep(myPos, aheadParapet, out Vector3 verifiedParapet))
                    {
                        bridgeBoundSpot = verifiedParapet;
                    }

                    if ((bridgeBoundSpot - myPos).sqrMagnitude >= 4.5f * 4.5f &&
                        Vector3.Dot(bridgeBoundSpot - myPos, threatDir) >= 3.5f)
                    {
                        DestinationWithoutCover bridgeBoundDest = new DestinationWithoutCover(bridgeBoundSpot, SoldierPose.Crouch);
                        soldier.CoverPosition(bridgeBoundDest.Cast<AiDestination>());
                        ApplyDesiredMovingPose(ai, soldier, (bridgeBoundSpot - myPos).sqrMagnitude, soldier.GetSuppressionValue());
                        plan.LockUnitCoverChange = true;
                        plan.PlanExpireTime = now + 4.8f;
                        plan.NextReplanTime = Mathf.Max(plan.NextReplanTime, now + 3.2f);
                        _localCoverProtectedUntil[ptr] = now + 4.8f;
                        GoapPlansExecutedCount++;
                        TrySayTacticalVoice(soldier, sq, VoiceManager.VoiceClip.imMoving, 0.20f);
                    }
                }
            }
        }

        private static bool TryGetNearbyScavengeTarget(
            Soldier soldier,
            Vector3 myPos,
            SoldierPersonality pers,
            bool needAmmo,
            bool needBandage,
            bool wantFragGrenade,
            bool wantSmokeGrenade,
            bool needAtWeapon,
            bool needUrgentPrimary,
            bool wantSquadMg,
            bool wantRoleUpgrade,
            bool wantRadioPack,
            bool wantMedicSyringe,
            bool wantHelmet,
            bool canFeedNeedyAlly,
            float now,
            out BotScavengeCache result)
        {
            IntPtr ptr = soldier.Pointer;
            bool onlyUrgentAt = needAtWeapon && !needAmmo && !needBandage && !wantFragGrenade && !wantSmokeGrenade && !needUrgentPrimary && !wantSquadMg && !wantRoleUpgrade && !wantRadioPack && !wantMedicSyringe && !wantHelmet && !canFeedNeedyAlly;
            bool onlyUrgentBandage = needBandage && !needAmmo && !wantFragGrenade && !wantSmokeGrenade && !needAtWeapon && !needUrgentPrimary && !wantSquadMg && !wantRoleUpgrade && !wantRadioPack && !wantMedicSyringe && !wantHelmet && !canFeedNeedyAlly;
            if (_botScavengeCaches.TryGetValue(ptr, out result) &&
                now < result.NextScanTime &&
                (!onlyUrgentAt || result.IsAtWeaponPickup) &&
                (!onlyUrgentBandage || result.IsBuddyBandageRefill || result.NeedBandage))
            {
                if ((result.IsBuddyBandageRefill || result.IsBuddyAmmoBorrow) &&
                    result.TargetBuddyDonor != null &&
                    result.TargetBuddyDonor.Pointer != IntPtr.Zero &&
                    result.TargetBuddyDonor.IsAlive &&
                    !result.TargetBuddyDonor.IsInjured &&
                    result.TargetBuddyDonor.transform != null)
                {
                    result.TargetPosition = result.TargetBuddyDonor.transform.position;
                    return true;
                }
                if (result.IsFeedingNeedyAlly &&
                    result.TargetNeedyAllyToFeed != null &&
                    result.TargetNeedyAllyToFeed.Pointer != IntPtr.Zero &&
                    result.TargetNeedyAllyToFeed.IsAlive &&
                    !result.TargetNeedyAllyToFeed.IsInjured &&
                    result.TargetNeedyAllyToFeed.transform != null)
                {
                    result.TargetPosition = result.TargetNeedyAllyToFeed.transform.position;
                    return true;
                }
                if (result.TargetFloorItem != null)
                {
                    if (IsFloorItemFreeToPickUp(result.TargetFloorItem) &&
                        !IsScavengeTargetClaimedByOtherBot(ptr, result.TargetFloorItem.Pointer, now))
                    {
                        result.TargetPosition = result.TargetFloorItem.transform.position;
                        return true;
                    }
                    // Someone else already picked up or claimed the floor item while we were walking toward it; rescan immediately!
                }
                else if (result.TargetRefillCrate != null)
                {
                    if (result.TargetRefillCrate.Pointer != IntPtr.Zero &&
                        result.TargetRefillCrate.gameObject != null &&
                        result.TargetRefillCrate.gameObject.activeInHierarchy &&
                        result.TargetRefillCrate.transform != null &&
                        !IsScavengeTargetClaimedByOtherBot(ptr, result.TargetRefillCrate.Pointer, now))
                    {
                        result.TargetPosition = result.TargetRefillCrate.transform.position;
                        return true;
                    }
                }
                else if (result.TargetInventory != null &&
                         IsInventoryValidCorpseOrCrate(result.TargetInventory, soldier.faction, result.NeedAmmo || result.NeedFragGrenade || result.NeedSmokeGrenade, result.NeedBandage || result.IsMedicSyringePickup, out _, out _) &&
                         !IsScavengeTargetClaimedByOtherBot(ptr, result.TargetInventory.Pointer, now))
                {
                    result.TargetPosition = result.TargetInventory.InventoryPosition;
                    return true;
                }
                else if (result.TargetBuddyDonor == null && result.TargetNeedyAllyToFeed == null)
                {
                    return false;
                }
            }

            long profScavStart = HitchProfiler.Begin();
            try
            {
            FactionCommandProfile factionProfile = GetFactionCommandProfile(soldier.faction);
            BotMicroVariance micro = BotMicroVariance.FromSoldier(ptr);
            float scanInterval = (needAtWeapon || needUrgentPrimary || needAmmo || wantRadioPack || onlyUrgentBandage)
                ? (2.8f + micro.ReactionJitterSeconds)
                : (5.2f + micro.ReactionJitterSeconds);
            result = new BotScavengeCache
            {
                NextScanTime = now + scanInterval,
                NeedAmmo = needAmmo,
                NeedBandage = needBandage,
                NeedFragGrenade = wantFragGrenade,
                NeedSmokeGrenade = wantSmokeGrenade
            };
            bool wantAnyUpgrade = needUrgentPrimary || wantSquadMg || wantRoleUpgrade;
            float doctrineSearchScale = Mathf.Clamp(1.0f / Mathf.Max(0.5f, factionProfile.ScavengeAndResupplyMult), 0.85f, 1.25f);
            float baseSearchRadius = (needAtWeapon || needUrgentPrimary) ? 20.0f : ((wantSquadMg || wantRadioPack) ? 16.0f : 12.5f);
            float maxSearchRadius = baseSearchRadius * doctrineSearchScale;
            float bestDistSq = maxSearchRadius * maxSearchRadius;
            bool canPickUpNewWeapon = (!_nextWeaponUpgradeAllowedTime.TryGetValue(ptr, out float nextWepT) || now >= nextWepT || nextWepT - now > 120.0f);
            bool canUseBoxOrCrate = (!_nextBotBoxOrCrateResupplyTime.TryGetValue(ptr, out float nextBoxT) || now >= nextBoxT || nextBoxT - now > 45.0f);

            // 0. Scan living friendly buddies in AiCore.WorldSoldiers for Buddy Resupply (Bandage from Medic, Ammo from Carrier, or Feeding a needy MG/AT squadmate)
            if (VisionConfig.EnableBuddyResupply.Value && (needBandage || needAmmo || canFeedNeedyAlly))
            {
                AiCore.EnsureWorldSnapshot();
                var wSoldiers = AiCore.WorldSoldiers;
                int wCount = AiCore.WorldSoldierCount;
                string myFaction = soldier.faction;
                IntPtr mySqPtr = soldier.joinedSquad != null ? soldier.joinedSquad.Pointer : IntPtr.Zero;
                bool canRefillBandageFromMedic = needBandage &&
                                                 (!_nextBuddyBandageRefillTime.TryGetValue(ptr, out float nextBndT) || now >= nextBndT);
                bool canResupplyBuddyAmmo = (needAmmo || canFeedNeedyAlly) &&
                                            (!_nextBuddyAmmoResupplyTime.TryGetValue(ptr, out float nextAmmoT) || now >= nextAmmoT);

                if (canRefillBandageFromMedic || canResupplyBuddyAmmo)
                {
                    for (int i = 0; i < wCount; i++)
                    {
                        ref AiCore.SoldierSnapshot sSnap = ref wSoldiers[i];
                        if (sSnap.Ptr == ptr || sSnap.Faction != myFaction || sSnap.IsOnVehicle || sSnap.HasSurrendered || sSnap.IsInjured || sSnap.Ref == null)
                            continue;

                        if (Mathf.Abs(sSnap.Pos.y - myPos.y) > 2.5f)
                            continue;

                        float dSq = (sSnap.Pos - myPos).sqrMagnitude;
                        if (dSq >= bestDistSq || dSq > 14.0f * 14.0f)
                            continue;

                        if (IsScavengeTargetClaimedByOtherBot(ptr, sSnap.Ptr, now))
                            continue;

                        if (canRefillBandageFromMedic && sSnap.IsMedic)
                        {
                            bestDistSq = dSq;
                            result = new BotScavengeCache
                            {
                                NextScanTime = now + 2.8f,
                                NeedAmmo = needAmmo,
                                NeedBandage = needBandage,
                                TargetBuddyDonor = sSnap.Ref,
                                TargetPosition = sSnap.Pos,
                                IsBuddyBandageRefill = true
                            };
                            continue;
                        }

                        if (canResupplyBuddyAmmo && needAmmo && (sSnap.IsAmmoCarrier || sSnap.CanBorrowAmmo))
                        {
                            bestDistSq = dSq;
                            result = new BotScavengeCache
                            {
                                NextScanTime = now + 2.8f,
                                NeedAmmo = needAmmo,
                                NeedBandage = needBandage,
                                TargetBuddyDonor = sSnap.Ref,
                                TargetPosition = sSnap.Pos,
                                IsBuddyAmmoBorrow = true
                            };
                            continue;
                        }

                        if (canResupplyBuddyAmmo && canFeedNeedyAlly && mySqPtr != IntPtr.Zero && sSnap.SquadPtr == mySqPtr)
                        {
                            if (_nextBuddyAmmoResupplyTime.TryGetValue(sSnap.Ptr, out float allyNextT) && now < allyNextT)
                                continue;

                            Soldier ally = sSnap.Ref;
                            GenericGun allyGun = ally.GetHeldGun();
                            if (allyGun != null && (ally.hasMg || allyGun.IsAtWeapon()) && ally.IsOutOfAmmosFor(allyGun))
                            {
                                bestDistSq = dSq;
                                result = new BotScavengeCache
                                {
                                    NextScanTime = now + 2.8f,
                                    NeedAmmo = needAmmo,
                                    NeedBandage = needBandage,
                                    TargetNeedyAllyToFeed = ally,
                                    TargetPosition = sSnap.Pos,
                                    IsFeedingNeedyAlly = true
                                };
                            }
                        }
                    }
                }
            }

            // 1. Scan ONLY genuinely dropped physical floor items via the shared 0.75s _freeFloorItemsSnapshot
            EnsureFreeFloorItemsSnapshot(now);
            int fCount = _freeFloorItemsSnapshot.Count;
            for (int i = 0; i < fCount; i++)
            {
                FreeFloorItemEntry fEntry = _freeFloorItemsSnapshot[i];
                Vector3 itemPos = fEntry.Pos;
                if (Mathf.Abs(itemPos.y - myPos.y) > 2.4f)
                    continue;

                float dSq = (itemPos - myPos).sqrMagnitude;
                if (dSq >= bestDistSq)
                    continue;

                ItemObject item = fEntry.Item;
                if (!IsFloorItemFreeToPickUp(item) || IsScavengeTargetClaimedByOtherBot(ptr, item.Pointer, now))
                    continue;

                switch (fEntry.Kind)
                {
                    case FloorItemKind.DroppableBox:
                        if (canUseBoxOrCrate && fEntry.Box != null)
                        {
                            CachedBoxOverrideInfo ov = GetCachedBoxOverrideInfo(fEntry.Box.Pointer, fEntry.Box.overrideContent, true);
                            if (BoxOverrideMatchesNeeds(ov, needAmmo, needAtWeapon && canPickUpNewWeapon, wantFragGrenade, wantSmokeGrenade, needBandage))
                            {
                                bestDistSq = dSq;
                                result = new BotScavengeCache
                                {
                                    NextScanTime = now + 2.8f,
                                    NeedAmmo = needAmmo,
                                    NeedBandage = needBandage,
                                    NeedFragGrenade = wantFragGrenade,
                                    NeedSmokeGrenade = wantSmokeGrenade,
                                    TargetFloorItem = item,
                                    TargetPosition = itemPos,
                                    IsDroppableAmmoBox = true,
                                    IsAtWeaponPickup = needAtWeapon && (ov.HasAtWeapon || ov.HasAtGrenade)
                                };
                            }
                        }
                        break;

                    case FloorItemKind.Gun:
                        if (canPickUpNewWeapon && fEntry.Gun != null)
                        {
                            if (needAtWeapon && fEntry.Gun.IsAtWeapon() && fEntry.Gun.GetCurrentAmmoCount() > 0)
                            {
                                bestDistSq = dSq;
                                result = new BotScavengeCache
                                {
                                    NextScanTime = now + 2.8f,
                                    NeedAmmo = needAmmo,
                                    NeedBandage = needBandage,
                                    TargetFloorItem = item,
                                    TargetPosition = itemPos,
                                    IsAtWeaponPickup = true
                                };
                            }
                            else if (wantAnyUpgrade && IsCandidateGunValidUpgrade(fEntry.Gun, pers, needUrgentPrimary, wantSquadMg, wantRoleUpgrade, true))
                            {
                                bestDistSq = dSq;
                                result = new BotScavengeCache
                                {
                                    NextScanTime = now + 2.8f,
                                    NeedAmmo = needAmmo,
                                    NeedBandage = needBandage,
                                    TargetFloorItem = item,
                                    TargetPosition = itemPos,
                                    IsRoleWeaponUpgrade = true
                                };
                            }
                        }
                        break;

                    case FloorItemKind.Helmet:
                        if (wantHelmet && dSq <= 9.0f * 9.0f)
                        {
                            bestDistSq = dSq;
                            result = new BotScavengeCache
                            {
                                NextScanTime = now + 2.8f,
                                NeedAmmo = needAmmo,
                                NeedBandage = needBandage,
                                TargetFloorItem = item,
                                TargetPosition = itemPos,
                                IsHelmetPickup = true
                            };
                        }
                        break;

                    case FloorItemKind.RadioPack:
                        if (wantRadioPack)
                        {
                            bestDistSq = dSq;
                            result = new BotScavengeCache
                            {
                                NextScanTime = now + 2.8f,
                                NeedAmmo = needAmmo,
                                NeedBandage = needBandage,
                                TargetFloorItem = item,
                                TargetPosition = itemPos,
                                IsRadioPackPickup = true
                            };
                        }
                        break;

                    case FloorItemKind.Ammo:
                    case FloorItemKind.Magazine:
                        if (needAmmo)
                        {
                            bestDistSq = dSq;
                            result = new BotScavengeCache
                            {
                                NextScanTime = now + 2.8f,
                                NeedAmmo = needAmmo,
                                NeedBandage = needBandage,
                                TargetFloorItem = item,
                                TargetPosition = itemPos
                            };
                        }
                        break;

                    case FloorItemKind.Bandage:
                        if (needBandage)
                        {
                            bestDistSq = dSq;
                            result = new BotScavengeCache
                            {
                                NextScanTime = now + 2.8f,
                                NeedAmmo = needAmmo,
                                NeedBandage = needBandage,
                                TargetFloorItem = item,
                                TargetPosition = itemPos
                            };
                        }
                        break;

                    case FloorItemKind.AtGrenade:
                        if (needAtWeapon)
                        {
                            bestDistSq = dSq;
                            result = new BotScavengeCache
                            {
                                NextScanTime = now + 2.8f,
                                NeedAmmo = needAmmo,
                                NeedBandage = needBandage,
                                TargetFloorItem = item,
                                TargetPosition = itemPos,
                                IsAtWeaponPickup = true
                            };
                        }
                        break;

                    case FloorItemKind.FragGrenade:
                        if (wantFragGrenade)
                        {
                            bestDistSq = dSq;
                            result = new BotScavengeCache
                            {
                                NextScanTime = now + 2.8f,
                                NeedAmmo = needAmmo,
                                NeedBandage = needBandage,
                                NeedFragGrenade = true,
                                TargetFloorItem = item,
                                TargetPosition = itemPos
                            };
                        }
                        break;

                    case FloorItemKind.SmokeGrenade:
                        if (wantSmokeGrenade)
                        {
                            bestDistSq = dSq;
                            result = new BotScavengeCache
                            {
                                NextScanTime = now + 2.8f,
                                NeedAmmo = needAmmo,
                                NeedBandage = needBandage,
                                NeedSmokeGrenade = true,
                                TargetFloorItem = item,
                                TargetPosition = itemPos
                            };
                        }
                        break;

                    case FloorItemKind.Syringe:
                        if (wantMedicSyringe)
                        {
                            bestDistSq = dSq;
                            result = new BotScavengeCache
                            {
                                NextScanTime = now + 2.8f,
                                NeedAmmo = needAmmo,
                                NeedBandage = needBandage,
                                TargetFloorItem = item,
                                TargetPosition = itemPos,
                                IsMedicSyringePickup = true
                            };
                        }
                        break;
                }
            }

            // 1B. Scan placed map AmmoRefillCrates (trench/bunker/strongpoint crates; lazily prune destroyed entries)
            if (canUseBoxOrCrate && _activeAmmoRefillCrates.Count > 0)
            {
                for (int i = _activeAmmoRefillCrates.Count - 1; i >= 0; i--)
                {
                    AmmoRefillCrate crate = _activeAmmoRefillCrates[i];
                    if (crate == null || crate.Pointer == IntPtr.Zero)
                    {
                        _activeAmmoRefillCrates.RemoveAt(i);
                        continue;
                    }

                    if (crate.gameObject == null || !crate.gameObject.activeInHierarchy || crate.transform == null)
                        continue;

                    Vector3 cPos = crate.transform.position;
                    if (Mathf.Abs(cPos.y - myPos.y) > 2.4f)
                        continue;

                    float dSq = (cPos - myPos).sqrMagnitude;
                    if (dSq >= bestDistSq || IsScavengeTargetClaimedByOtherBot(ptr, crate.Pointer, now))
                        continue;

                    CachedBoxOverrideInfo ov = GetCachedBoxOverrideInfo(crate.Pointer, crate.overrideContent, crate.allowTakeWeaponIntoInventory);
                    if (BoxOverrideMatchesNeeds(ov, needAmmo, needAtWeapon && canPickUpNewWeapon, wantFragGrenade, wantSmokeGrenade, needBandage))
                    {
                        bestDistSq = dSq;
                        result = new BotScavengeCache
                        {
                            NextScanTime = now + 2.8f,
                            NeedAmmo = needAmmo,
                            NeedBandage = needBandage,
                            NeedFragGrenade = wantFragGrenade,
                            NeedSmokeGrenade = wantSmokeGrenade,
                            TargetRefillCrate = crate,
                            TargetPosition = cPos,
                            IsAtWeaponPickup = needAtWeapon && (ov.HasAtWeapon || ov.HasAtGrenade)
                        };
                    }
                }
            }

            // 2. Scan nearby verified dead corpse inventories and supply crates via the chunked _activeScavengeInventories snapshot!
            EnsureScavengeInventoriesSnapshot(now);
            int invCount = _activeScavengeInventories.Count;
            if (invCount > 0)
            {
                IntPtr myInvPtr = (!ReferenceEquals(soldier.inventory, null) && soldier.inventory != null) ? soldier.inventory.Pointer : IntPtr.Zero;
                string myFac = AiCore.GetCachedFaction(soldier);
                bool needAmmoOrNade = needAmmo || wantFragGrenade || wantSmokeGrenade;
                bool needMedOrSyringe = needBandage || wantMedicSyringe;

                for (int i = 0; i < invCount; i++)
                {
                    ScavengeInventoryEntry sEntry = _activeScavengeInventories[i];
                    if (sEntry.Ptr == IntPtr.Zero || sEntry.Ptr == myInvPtr)
                        continue;

                    Vector3 invPos = sEntry.Pos;
                    if (Mathf.Abs(invPos.y - myPos.y) > 2.4f)
                        continue;

                    float dSq = (invPos - myPos).sqrMagnitude;
                    if (dSq >= bestDistSq)
                        continue;

                    if (IsScavengeTargetClaimedByOtherBot(ptr, sEntry.Ptr, now))
                        continue;

                    InventoryManager inv = sEntry.Inv;
                    if (!IsInventoryValidCorpseOrCrate(
                            inv,
                            myFac,
                            needAmmoOrNade,
                            needMedOrSyringe,
                            out Soldier owner,
                            out bool isDeadBodyOrCrate))
                    {
                        continue;
                    }

                    // 2A. Field Radio Pack Inheritance (from dead friendly OR enemy radioman) & Corpse Helmet Recovery (from dead friendly comrade)
                    if (isDeadBodyOrCrate && (wantRadioPack || wantHelmet))
                    {
                        if (wantRadioPack)
                        {
                            VirtualItem vCloth = inv.FindItemOfType<VirtualClothing>();
                            if (vCloth != null && TryGetVirtualWearableInfo(vCloth, out CachedWearableInfo wInfo) && wInfo.IsRadio)
                            {
                                bestDistSq = dSq;
                                result = new BotScavengeCache
                                {
                                    NextScanTime = now + 2.8f,
                                    NeedAmmo = needAmmo,
                                    NeedBandage = needBandage,
                                    TargetInventory = inv,
                                    TargetInventoryWearable = vCloth,
                                    TargetPosition = invPos,
                                    IsRadioPackPickup = true
                                };
                                continue;
                            }
                        }

                        if (wantHelmet && dSq <= 8.5f * 8.5f && owner != null && sEntry.OwnerFaction == myFac)
                        {
                            VirtualItem vHelm = inv.FindItemOfType<VirtualHelmet>();
                            if (vHelm != null)
                            {
                                bestDistSq = dSq;
                                result = new BotScavengeCache
                                {
                                    NextScanTime = now + 2.8f,
                                    NeedAmmo = needAmmo,
                                    NeedBandage = needBandage,
                                    TargetInventory = inv,
                                    TargetInventoryWearable = vHelm,
                                    TargetPosition = invPos,
                                    IsHelmetPickup = true
                                };
                                continue;
                            }
                        }
                    }

                    if (canPickUpNewWeapon && (needAtWeapon || wantAnyUpgrade) && isDeadBodyOrCrate)
                    {
                        if (needAtWeapon)
                        {
                            VirtualItem atGren = inv.FindItemOfType<VirtualATGrenade>();
                            if (atGren != null)
                            {
                                bestDistSq = dSq;
                                result = new BotScavengeCache
                                {
                                    NextScanTime = now + 2.8f,
                                    NeedAmmo = needAmmo,
                                    NeedBandage = needBandage,
                                    TargetInventory = inv,
                                    TargetPosition = invPos,
                                    IsAtWeaponPickup = true
                                };
                                continue;
                            }
                        }

                        // Scan all VirtualWeapons in inv.inventory.items (up to 32) so AT launchers & MGs inside mixed crates are never missed!
                        var invItems = inv.inventory != null ? inv.inventory.items : null;
                        if (invItems != null)
                        {
                            int itemLim = Mathf.Min(invItems.Count, 32);
                            VirtualItem matchedAtWep = null;
                            VirtualItem matchedUpgWep = null;
                            for (int k = 0; k < itemLim; k++)
                            {
                                VirtualItem candVi = invItems[k];
                                if (candVi == null || candVi.TryCast<VirtualWeapon>() == null)
                                    continue;
                                if (!TryGetVirtualWeaponArchetype(candVi, out CachedWeaponArchetype arch))
                                    continue;

                                if (needAtWeapon && arch.IsAtWeapon)
                                {
                                    matchedAtWep = candVi;
                                    break;
                                }
                                if (matchedUpgWep == null &&
                                    wantAnyUpgrade &&
                                    IsWeaponArchetypeValidUpgrade(
                                        arch.IsPistol,
                                        arch.IsAtWeapon,
                                        arch.IsMg,
                                        arch.IsScopedOrSniper,
                                        arch.IsManualBoltOnly,
                                        pers,
                                        needUrgentPrimary,
                                        wantSquadMg,
                                        wantRoleUpgrade))
                                {
                                    matchedUpgWep = candVi;
                                }
                            }

                            if (matchedAtWep != null)
                            {
                                bestDistSq = dSq;
                                result = new BotScavengeCache
                                {
                                    NextScanTime = now + 2.8f,
                                    NeedAmmo = needAmmo,
                                    NeedBandage = needBandage,
                                    TargetInventory = inv,
                                    TargetInventoryAtWeapon = matchedAtWep,
                                    TargetPosition = invPos,
                                    IsAtWeaponPickup = true
                                };
                                continue;
                            }
                            if (matchedUpgWep != null)
                            {
                                bestDistSq = dSq;
                                result = new BotScavengeCache
                                {
                                    NextScanTime = now + 2.8f,
                                    NeedAmmo = needAmmo,
                                    NeedBandage = needBandage,
                                    TargetInventory = inv,
                                    TargetInventoryPrimaryWeapon = matchedUpgWep,
                                    TargetPosition = invPos,
                                    IsRoleWeaponUpgrade = true
                                };
                                continue;
                            }
                        }
                    }

                    if (wantMedicSyringe && isDeadBodyOrCrate && inv.FindItemOfType<VirtualSyringe>() != null)
                    {
                        bestDistSq = dSq;
                        result = new BotScavengeCache
                        {
                            NextScanTime = now + 2.8f,
                            NeedAmmo = needAmmo,
                            NeedBandage = needBandage,
                            TargetInventory = inv,
                            TargetPosition = invPos,
                            IsMedicSyringePickup = true
                        };
                        continue;
                    }

                    bool hasFragInInv = wantFragGrenade && inv.FindItemOfType<VirtualGrenade>() != null;
                    bool hasSmokeInInv = wantSmokeGrenade && inv.FindItemOfType<VirtualSmokeGrenade>() != null;
                    bool hasBandageInInv = needBandage && inv.FindItemOfType<VirtualBandages>() != null;
                    bool hasAmmoInInv = needAmmo &&
                                        (owner != null ||
                                         inv.FindItemOfType<VirtualAmmo>() != null ||
                                         inv.FindItemOfType<VirtualMagazineItem>() != null);

                    if (!hasAmmoInInv && !hasBandageInInv && !hasFragInInv && !hasSmokeInInv)
                        continue;

                    bestDistSq = dSq;
                    result = new BotScavengeCache
                    {
                        NextScanTime = now + 2.8f,
                        NeedAmmo = hasAmmoInInv,
                        NeedBandage = hasBandageInInv,
                        NeedFragGrenade = hasFragInInv,
                        NeedSmokeGrenade = hasSmokeInInv,
                        TargetInventory = inv,
                        TargetPosition = invPos
                    };
                }
            }

            _botScavengeCaches[ptr] = result;
            return result.TargetFloorItem != null ||
                   result.TargetRefillCrate != null ||
                   result.TargetInventory != null ||
                   result.TargetBuddyDonor != null ||
                   result.TargetNeedyAllyToFeed != null;
            }
            finally
            {
                HitchProfiler.End(HitchBucket.GoapScavenge, profScavStart);
            }
        }

        private static bool IsFriendlyPlayerWithinDistance(Soldier self, Vector3 myPos, float maxDist)
        {
            if (self == null)
                return false;

            AiCore.EnsureWorldSnapshot();
            var wSoldiers = AiCore.WorldSoldiers;
            int wCount = AiCore.WorldSoldierCount;
            float maxSq = maxDist * maxDist;
            string myFaction = self.faction;

            for (int i = 0; i < wCount; i++)
            {
                ref AiCore.SoldierSnapshot sSnap = ref wSoldiers[i];
                if (!sSnap.IsPlayer || sSnap.Faction != myFaction)
                    continue;
                if ((sSnap.Pos - myPos).sqrMagnitude <= maxSq)
                    return true;
            }
            return false;
        }

        private static bool TryExecuteBuddyCasualtyDrag(
            SoldierAI ai,
            Soldier soldier,
            Squad sq,
            ref SquadBlackboard board,
            Vector3 myPos,
            float now)
        {
            if (ai == null || soldier == null)
                return false;

            IntPtr ptr = soldier.Pointer;

            // 1. If already carrying a casualty, drag them to the sheltered drop spot and release them!
            if (soldier.IsCarryingBody())
            {
                bool hasState = _casualtyDragStates.TryGetValue(ptr, out CasualtyDragState activeDrag);
                Soldier carried = soldier.carryData != null ? soldier.carryData.carriedSoldier : activeDrag.Patient;

                bool shouldDrop = !VisionConfig.EnableBuddyCasualtyDragging.Value ||
                                  !hasState ||
                                  carried == null ||
                                  carried.IsDead ||
                                  !carried.IsInjured ||
                                  now >= activeDrag.DragEndTime ||
                                  (myPos - activeDrag.DropSpot).sqrMagnitude <= 2.25f * 2.25f ||
                                  soldier.GetSuppressionValue() >= 55f;

                if (shouldDrop)
                {
                    if (!Photon.Pun.PhotonNetwork.InRoom || AiCore.TryConsumeBotRpcBudget(0.35f))
                    {
                        soldier.CarryBodySynched(null);
                        _casualtyDragStates.Remove(ptr);
                    }
                    return true;
                }

                SetBotPose(ai, soldier, SoldierPose.Crouch);
                ai.MoveDirectlyToward(activeDrag.DropSpot, 2.6f);
                return true;
            }

            if (!VisionConfig.EnableBuddyCasualtyDragging.Value || sq == null || sq.units == null)
                return false;

            if (soldier.isBleeding || soldier.IsSquadLeader() || soldier.IsRadioman() || soldier.IsMedic() || soldier.carryData != null)
                return false;

            SoldierPersonality pers = GetPersonality(soldier);
            if (pers == SoldierPersonality.Medic_Heroic ||
                pers == SoldierPersonality.Medic_Combat ||
                pers == SoldierPersonality.Medic_Triage)
                return false;

            FactionCommandProfile factionProfile = GetFactionCommandProfile(soldier.faction);
            float comradeMult = Mathf.Max(0.55f, factionProfile.ComradeAidAndDragMult);
            float maxDragSupp = Mathf.Clamp(28f / comradeMult, 18f, 36f);
            if (soldier.GetSuppressionValue() >= maxDragSupp || ai.HasEnemyAndCanFireAtHim)
                return false;

            // Prevent multiple squadmates from simultaneously walking over to drag a casualty in the same squad
            if (now < board.DragClaimUntil && board.DragRescuerPtr != IntPtr.Zero && board.DragRescuerPtr != ptr)
                return false;

            if (_casualtyDragStates.TryGetValue(ptr, out CasualtyDragState dragState) && now < dragState.NextScanTime)
                return false;

            dragState.NextScanTime = now + 3.2f;
            _casualtyDragStates[ptr] = dragState;

            if (!board.HasActiveThreat)
                return false;

            int uCount = sq.units.Count;
            Soldier bestPatient = null;
            Vector3 bestPatientPos = Vector3.zero;
            float maxDragRadius = Mathf.Clamp(9.5f / comradeMult, 7.0f, 12.5f);
            float bestDistSq = maxDragRadius * maxDragRadius;
            Soldier squadMedic = null;
            Vector3 squadMedicPos = Vector3.zero;

            for (int i = 0; i < uCount; i++)
            {
                Soldier u = sq.units[i];
                if (u == null || !u.IsAlive || u.transform == null)
                    continue;
                if (u.IsMedic() && !u.IsInjured)
                {
                    squadMedic = u;
                    squadMedicPos = u.transform.position;
                    break;
                }
            }

            for (int i = 0; i < uCount; i++)
            {
                Soldier cand = sq.units[i];
                if (cand == null || cand.Pointer == ptr || cand.IsDead || !cand.IsInjured || cand.IsOnVehicle())
                    continue;
                if (cand.carryData != null || cand.IsOnCover() || cand.transform == null)
                    continue;

                Vector3 cPos = cand.transform.position;
                if (Mathf.Abs(cPos.y - myPos.y) > 2.2f)
                    continue;

                // Skip if a squad medic is already right next to the casualty
                if (squadMedic != null && (squadMedicPos - cPos).sqrMagnitude <= 6.0f * 6.0f)
                    continue;

                float dSq = (cPos - myPos).sqrMagnitude;
                if (dSq < bestDistSq)
                {
                    bestDistSq = dSq;
                    bestPatient = cand;
                    bestPatientPos = cPos;
                }
            }

            if (bestPatient == null)
                return false;

            board.DragRescuerPtr = ptr;
            board.DragPatientPtr = bestPatient.Pointer;
            board.DragClaimUntil = now + 4.5f;

            Vector3 dropSpot;
            if (squadMedic != null && (squadMedicPos - bestPatientPos).sqrMagnitude <= 14.0f * 14.0f)
            {
                dropSpot = squadMedicPos - board.ThreatDirection * 1.8f;
            }
            else if (!TryFindShelteredSideOfWall(bestPatientPos, board.ThreatDirection, soldier.transform.root, out dropSpot))
            {
                dropSpot = bestPatientPos - board.ThreatDirection * 5.5f;
            }

            if (bestDistSq <= 2.35f * 2.35f)
            {
                if (!Photon.Pun.PhotonNetwork.InRoom || AiCore.TryConsumeBotRpcBudget(0.40f))
                {
                    soldier.SetGunTriggerPressed(false);
                    soldier.CarryBodySynched(bestPatient);
                    dragState.Patient = bestPatient;
                    dragState.DropSpot = dropSpot;
                    dragState.DragEndTime = now + 7.0f;
                    dragState.NextScanTime = now + 18.0f;
                    _casualtyDragStates[ptr] = dragState;
                    CasualtiesDraggedCount++;
                    GoapPlansExecutedCount++;
                    TrySayTacticalVoice(soldier, sq, VoiceManager.VoiceClip.imMoving, 0.25f);
                    if (VisionConfig.DebugLogging.Value)
                    {
                        AiCore.LogDebug(
                            "CasualtyDrag",
                            $"{soldier.faction} {pers} dragging wounded squadmate out of fire line to shelter.",
                            3.0f);
                    }
                    return true;
                }
                return false;
            }

            ApplyDesiredMovingPose(ai, soldier, bestDistSq, soldier.GetSuppressionValue());

            AiCore.MoveBotTowardPosition(ai, soldier, bestPatientPos, 1.15f, 2.6f);
            dragState.NextScanTime = now + 0.45f;
            _casualtyDragStates[ptr] = dragState;
            return true;
        }

        private static bool TryRemountNearbyOperableCrewVehicle(
            SoldierAI ai,
            Soldier soldier,
            Squad sq,
            Vector3 myPos,
            float maxRadius)
        {
            if (ai == null || soldier == null || soldier.IsOnVehicle())
                return false;

            if (soldier.targetDestination != null && soldier.targetDestination.TryCast<VehicleRepairTask>() != null)
                return false;

            var allVehicles = Vehicle.allVehicles;
            if (allVehicles == null)
                return false;

            int vCount = allVehicles.Count;
            if (vCount == 0)
                return false;

            float bestDistSq = maxRadius * maxRadius;
            Vehicle bestVeh = null;
            string myFaction = soldier.faction;

            for (int i = 0; i < vCount; i++)
            {
                Vehicle veh = allVehicles[i];
                if (veh == null || veh.Pointer == IntPtr.Zero || veh.transform == null)
                    continue;

                if (veh.IsLocked() ||
                    veh.IsDisabled() ||
                    veh.IsCoverDestroyed() ||
                    veh.IsAirVehicle() ||
                    veh.TryCast<MovableVehicle>() == null ||
                    _vehiclesCrewRetreatedFrom.Contains(veh.Pointer))
                {
                    continue;
                }

                if (veh.CanBeRepaired())
                {
                    Soldier mech = sq != null ? sq.GetMechanic() : null;
                    if (mech == null || !mech.IsAlive || mech.GetRepairTools() == null)
                        continue;
                }

                string vf = GetVehicleCrewFaction(veh);
                if (!string.IsNullOrEmpty(vf) &&
                    vf != "Civilian" &&
                    vf != myFaction)
                    continue;

                float dSq = (veh.transform.position - myPos).sqrMagnitude;
                if (dSq >= bestDistSq)
                    continue;

                bool hasFreeSeat = false;
                if (veh.seats != null)
                {
                    int sCount = veh.seats.Length;
                    for (int s = 0; s < sCount; s++)
                    {
                        Seats seat = veh.seats[s];
                        if (seat != null && seat.unitSet == null)
                        {
                            hasFreeSeat = true;
                            break;
                        }
                    }
                }
                if (!hasFreeSeat)
                    continue;

                bestDistSq = dSq;
                bestVeh = veh;
            }

            if (bestVeh == null)
                return false;

            if (soldier.targetDestination == null || soldier.targetDestination.Pointer != bestVeh.Pointer)
            {
                soldier.CoverPosition(bestVeh.Cast<AiDestination>());
            }
            ai.MoveDestination = bestVeh.transform.position;
            return true;
        }

        private static bool ShouldBailedCrewRetreatOverJoinSquad(Soldier soldier, Squad sq, Vector3 myPos)
        {
            if (IsSquadOnRetreatTask(sq))
                return true;

            SoldierPersonality pers = GetPersonality(soldier);
            bool isPilot = pers == SoldierPersonality.Pilot_Ace ||
                           pers == SoldierPersonality.Pilot_Daredevil ||
                           pers == SoldierPersonality.Pilot_Disciplined;

            GenericGun g0 = soldier.GetHeldGun(0);
            GenericGun g1 = soldier.GetHeldGun(1);
            GenericGun g2 = soldier.GetHeldGun(2);
            bool hasRealPrimary = (g0 != null && !g0.IsPistol() && !g0.IsAtWeapon() && !soldier.IsOutOfAmmosFor(g0)) ||
                                  (g1 != null && !g1.IsPistol() && !g1.IsAtWeapon() && !soldier.IsOutOfAmmosFor(g1)) ||
                                  (g2 != null && !g2.IsPistol() && !g2.IsAtWeapon() && !soldier.IsOutOfAmmosFor(g2));

            // Unarmed / pistol-only bailed crew or pilots retreat toward rear spawn to survive
            if (!hasRealPrimary || isPilot)
                return true;

            // Armed vehicle crewmen check if a friendly infantry squad is within 85m to link up with; otherwise retreat
            AiCore.EnsureWorldSnapshot();
            int sqCount = AiCore.WorldSquadCount;
            var squads = AiCore.WorldSquads;
            IntPtr mySqPtr = sq != null ? sq.Pointer : IntPtr.Zero;
            string myFaction = soldier.faction;

            for (int i = 0; i < sqCount; i++)
            {
                ref AiCore.SquadSnapshot other = ref squads[i];
                if (!other.LeaderIsAlive || other.Ptr == mySqPtr || other.IsVehicleCrew || other.IsPlayerInSquad || other.LeaderOnVehicle || other.Faction != myFaction)
                    continue;
                if ((other.RefPos - myPos).sqrMagnitude <= 85f * 85f)
                    return false;
            }

            return true;
        }

        private static bool TryLinkUpOrJoinFriendlySquad(
            SoldierAI ai,
            Soldier self,
            Vector3 myPos,
            float maxRadius,
            out Vector3 activeMoveDest)
        {
            activeMoveDest = myPos;
            AiCore.EnsureWorldSnapshot();
            int sqCount = AiCore.WorldSquadCount;
            if (sqCount == 0)
                return false;

            float bestSq = maxRadius * maxRadius;
            Squad bestSquad = null;
            Vector3 linkUpPos = Vector3.zero;
            string myFaction = self.faction;
            IntPtr mySqPtr = self.joinedSquad != null ? self.joinedSquad.Pointer : IntPtr.Zero;

            var squads = AiCore.WorldSquads;
            for (int i = 0; i < sqCount; i++)
            {
                ref AiCore.SquadSnapshot other = ref squads[i];
                if (!other.LeaderIsAlive || other.Ptr == mySqPtr || other.IsVehicleCrew || other.IsPlayerInSquad || other.LeaderOnVehicle || other.Faction != myFaction)
                    continue;

                float dSq = (other.RefPos - myPos).sqrMagnitude;
                if (dSq < bestSq)
                {
                    bestSq = dSq;
                    bestSquad = other.Ref;
                    linkUpPos = other.RefPos;
                }
            }

            if (bestSquad == null)
                return false;

            // If within 18m of the friendly Squad Leader, merge the lone soldier properly into the squad via Squad.Join!
            // Vanilla Squad.AddIfNotInSquad (0x1808001A0) only inserts into bestSquad.units without updating self.joinedSquad
            // or leaving the old dead squad, which stranded merged survivors on their dead squad's stale order.
            // Squad.Join (0x180806A00) leaves the old squad, updates self.joinedSquad, syncs RPC_JoinSquad online, and updates GUI.
            if (bestSq <= 18f * 18f)
            {
                try
                {
                    Soldier targetLeader = bestSquad.Leader;
                    if (bestSquad.units != null &&
                        bestSquad.units.Count < 10 &&
                        targetLeader != null &&
                        AiCore.IsLocallyOwnedBot(self) &&
                        AiCore.IsLocallyOwnedBot(targetLeader) &&
                        !bestSquad.InInside(self) &&
                        AiCore.TryConsumeBotRpcBudget())
                    {
                        bestSquad.Join(self);
                        if (self.joinedSquad != null && self.joinedSquad.Pointer == bestSquad.Pointer)
                        {
                            Vector3 targetOrderPos = (bestSquad.order != Order.follow && bestSquad.moveOrderPosition.sqrMagnitude > 0.1f)
                                ? bestSquad.moveOrderPosition
                                : linkUpPos;
                            activeMoveDest = targetOrderPos;
                            if (ai != null)
                            {
                                float targetRadius = bestSquad.order != Order.follow
                                    ? Mathf.Clamp(bestSquad.moveOrderRadius * 0.5f, 3.5f, 12.0f)
                                    : 3.5f;
                                AiCore.MoveBotTowardPosition(ai, self, targetOrderPos, targetRadius, 4.0f);
                            }

                            GoapPlansExecutedCount++;
                            if (VisionConfig.DebugLogging.Value)
                            {
                                AiCore.LogDebug(
                                    "GoapMerge",
                                    $"{myFaction} lone survivor / bailed crewman merged into friendly squad '{bestSquad.squadName}'.",
                                    3.0f);
                            }
                            return true;
                        }
                    }
                }
                catch { }
            }

            if (bestSq > 8f * 8f && ai != null)
            {
                activeMoveDest = linkUpPos;
                AiCore.MoveBotTowardPosition(ai, self, linkUpPos, 3.5f, 3.0f);
                return true;
            }

            return false;
        }

        private static bool TryFindNearestRadioHubPosition(
            Squad selfSq,
            Vector3 myPos,
            string myFaction,
            float maxRadius,
            out Vector3 hubPos)
        {
            hubPos = Vector3.zero;
            if (selfSq == null)
                return false;

            AiCore.EnsureWorldSnapshot();
            int sqCount = AiCore.WorldSquadCount;
            if (sqCount == 0)
                return false;

            float bestSq = maxRadius * maxRadius;
            bool found = false;
            IntPtr selfPtr = selfSq.Pointer;
            var squads = AiCore.WorldSquads;

            for (int i = 0; i < sqCount; i++)
            {
                ref AiCore.SquadSnapshot other = ref squads[i];
                if (!other.LeaderIsAlive || other.Ptr == selfPtr || other.Faction != myFaction)
                    continue;

                if (!other.HasIntrinsicRadio && !other.LeaderOnGroundVehicle && !other.IsPlayerInSquad)
                    continue;

                float dSq = (other.RefPos - myPos).sqrMagnitude;
                if (dSq < bestSq && dSq > 45f * 45f)
                {
                    bestSq = dSq;
                    hubPos = other.RefPos;
                    found = true;
                }
            }

            return found;
        }
    }
}
