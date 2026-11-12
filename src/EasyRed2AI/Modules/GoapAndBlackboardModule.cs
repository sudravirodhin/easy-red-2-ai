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
        internal enum GoapStateBits : uint
        {
            None                    = 0u,
            IsBleeding              = 1u << 0,
            InCover                 = 1u << 1,
            IsSuppressed            = 1u << 2,
            IsHeavilySuppressed     = 1u << 3,
            HasVisibleEnemy         = 1u << 4,
            CanFireAtVisibleEnemy   = 1u << 5,
            TargetLostBehindCover   = 1u << 6,
            SquadHasActiveThreat    = 1u << 7,
            AllyPinningThreat       = 1u << 8,
            ArmorThreatClose        = 1u << 9,
            HasFragGrenade          = 1u << 10,
            HasSmokeGrenade         = 1u << 11,
            InGrenadeRange          = 1u << 12,
            InPointBlankRange       = 1u << 13,
            FlankCoverAvailable     = 1u << 14,
            ExposedSideOfWall       = 1u << 15,
            ComradeNeedsLiberation  = 1u << 16,
            IsMachineGunner         = 1u << 17,
            IsAssaulterOrSkirmisher = 1u << 18,
            IsRadioman              = 1u << 19,
            RadioLinkDrifting       = 1u << 20,
            IsEntirelyAlone         = 1u << 21,
            OnBridgeOrNarrow        = 1u << 22,
            CanProvideOverwatch     = 1u << 23,
            NeedAmmoOrMedScavenge   = 1u << 24,
            ObjectiveCapContestUrgent = 1u << 25,

            // Goal & Chain Effect Bits
            SurvivedCrisis          = 1u << 26,
            RadioLinkPreserved      = 1u << 27,
            TargetPinned            = 1u << 28,
            TargetEliminated        = 1u << 29,
            SquadSupported          = 1u << 30,
            HasBandage              = 1u << 31
        }

        internal enum SquadNetDirective : byte
        {
            Disconnected_Autonomous = 0,
            Connected_StandardObjective = 1,
            Anvil_PinAndCallSupport = 2,
            Hammer_WideFlankEnvelopment = 3
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
            ScavengeSupplyFromCorpse = 15
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
                Forbidden = GoapStateBits.InCover | GoapStateBits.OnBridgeOrNarrow,
                Add = GoapStateBits.InCover | GoapStateBits.SurvivedCrisis,
                Remove = GoapStateBits.ExposedSideOfWall,
                BaseCost = 1.2f
            },
            new GoapActionDef
            {
                Id = GoapActionId.BandageInCover,
                Required = GoapStateBits.IsBleeding | GoapStateBits.HasBandage | GoapStateBits.InCover,
                Forbidden = GoapStateBits.InPointBlankRange | GoapStateBits.ExposedSideOfWall | GoapStateBits.ArmorThreatClose,
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
                Forbidden = GoapStateBits.IsBleeding | GoapStateBits.OnBridgeOrNarrow | GoapStateBits.InPointBlankRange,
                Add = GoapStateBits.AllyPinningThreat | GoapStateBits.TargetPinned | GoapStateBits.SquadSupported,
                Remove = GoapStateBits.None,
                BaseCost = 1.85f
            },
            new GoapActionDef
            {
                Id = GoapActionId.BoundWithOverwatch,
                Required = GoapStateBits.SquadHasActiveThreat | GoapStateBits.AllyPinningThreat,
                Forbidden = GoapStateBits.IsBleeding | GoapStateBits.IsHeavilySuppressed | GoapStateBits.IsMachineGunner | GoapStateBits.IsRadioman | GoapStateBits.OnBridgeOrNarrow | GoapStateBits.InPointBlankRange,
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
                Forbidden = GoapStateBits.IsBleeding | GoapStateBits.InPointBlankRange | GoapStateBits.OnBridgeOrNarrow | GoapStateBits.CanFireAtVisibleEnemy,
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

        private static readonly List<FreeFloorItemEntry> _freeFloorItemsSnapshot = new List<FreeFloorItemEntry>(64);
        private static readonly Dictionary<IntPtr, FloorItemKind> _cachedFloorItemKinds = new Dictionary<IntPtr, FloorItemKind>(128);
        private static readonly List<AmmoRefillCrate> _activeAmmoRefillCrates = new List<AmmoRefillCrate>(32);
        private static readonly Dictionary<IntPtr, CachedBoxOverrideInfo> _cachedBoxOverrides = new Dictionary<IntPtr, CachedBoxOverrideInfo>(64);
        private static float _nextFreeFloorItemsSnapshotTime;

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

        private static FloorItemKind ClassifyFloorItem(ItemObject item, out DroppableAmmoBox box, out GenericGun gun)
        {
            box = item.TryCast<DroppableAmmoBox>();
            if (box != null)
            {
                gun = null;
                return FloorItemKind.DroppableBox;
            }

            gun = item.TryCast<GenericGun>();
            if (gun != null)
                return FloorItemKind.Gun;

            IntPtr ptr = item.Pointer;
            if (_cachedFloorItemKinds.TryGetValue(ptr, out FloorItemKind cached))
                return cached;

            FloorItemKind kind = FloorItemKind.Ignore;
            ItemGrenade ig = item.TryCast<ItemGrenade>();
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

            if (_cachedFloorItemKinds.Count > 256)
                _cachedFloorItemKinds.Clear();
            _cachedFloorItemKinds[ptr] = kind;
            return kind;
        }

        private static void EnsureFreeFloorItemsSnapshot(float now)
        {
            if (now < _nextFreeFloorItemsSnapshotTime && _nextFreeFloorItemsSnapshotTime - now <= 5.0f)
                return;

            // Defer non-urgent floor-item scan by 1 tick if SquadLeaderRoutine / WorldSnapshot / MacroDirector already ran this frame
            if (AiCore.IsHeavyWorkFrame() && _nextFreeFloorItemsSnapshotTime > 0f && now < _nextFreeFloorItemsSnapshotTime + 1.5f)
                return;

            _nextFreeFloorItemsSnapshotTime = now + 1.20f;
            _freeFloorItemsSnapshot.Clear();

            var floorItems = ItemObject.spawnedItems;
            if (floorItems == null)
                return;

            int fCount = floorItems.Count;
            if (fCount >= 64)
                AiCore.NotifyDirectorWorkThisFrame();
            for (int i = 0; i < fCount; i++)
            {
                ItemObject item = floorItems[i];
                if (IsFloorItemFreeToPickUp(item))
                {
                    FloorItemKind kind = ClassifyFloorItem(item, out DroppableAmmoBox box, out GenericGun gun);
                    if (kind != FloorItemKind.Ignore && kind != FloorItemKind.Unknown)
                    {
                        _freeFloorItemsSnapshot.Add(new FreeFloorItemEntry
                        {
                            Item = item,
                            Box = box,
                            Gun = gun,
                            Pos = item.transform.position,
                            Kind = kind
                        });
                    }
                }
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
            if (item == null ||
                item.Pointer == IntPtr.Zero ||
                !item.IsPhysicEnabled() ||
                !item.CanInteract() ||
                item.gameObject == null ||
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

            if (_cachedInventoryOwners.Count > 1024)
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

            if (inv == null || inv.Pointer == IntPtr.Zero || inv.IsPlayersInventory())
                return false;

            if (inv.inventory == null || inv.inventory.items == null || inv.inventory.items.Count == 0)
                return false;

            CachedInventoryOwner invOwner = GetCachedInventoryOwner(inv);
            Soldier owner = invOwner.SoldierOwner;
            Vehicle vehOwner = invOwner.VehicleOwner;

            // CRITICAL: In Easy Red 2 (Creature.get_IsAlive @ 0x180A55260, get_IsInjured @ 0x180A55390, get_IsDead @ 0x180A552F0),
            // an incapacitated/downed soldier waiting for a medic has IsAlive == false (life_total == 0) AND IsInjured == true,
            // while their weapons, helmet, and radio pack remain inside owner.inventory so they keep their gear when revived!
            // Never target living or downed/injured soldiers via InventoryManager (living buddy resupply uses TargetBuddyDonor).
            if (owner != null)
            {
                if (owner.Pointer == IntPtr.Zero || !owner.IsDead || owner.IsInjured || owner.IsAlive)
                    return false;
                deadSoldierOwner = owner;
            }

            if (vehOwner != null && vehOwner.Pointer != IntPtr.Zero)
            {
                string vf = vehOwner.GetVehicleFaction();
                if (!string.IsNullOrEmpty(vf) && ResourcesManager.IsEnemyFaction(vf, soldierFaction))
                    return false;
                if (!needAmmo && !needBandage && !vehOwner.IsDisabled())
                    return false;
                isDeadBodyOrDisabledCrate = vehOwner.IsDisabled();
                return true;
            }

            isDeadBodyOrDisabledCrate = true;
            return true;
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
            public int AliveMemberCount;
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

            // Tier 3 Team Net Directive assigned to this Squad Leader
            public SquadNetDirective NetDirective;
            public float NetDirectiveExpireTime;
            public Vector3 NetDirectiveTargetPos;

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
            public GoapActionId Step0;
            public GoapActionId Step1;
            public byte StepIndex;
            public byte StepCount;
            public float PlanExpireTime;
            public float NextReplanTime;
            public SoldierPose DesiredPose;
            public bool LockUnitCoverChange;
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
                ComradeAidAndDragMult = 1.0f
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

        internal struct CasualtyDragState
        {
            public Soldier Patient;
            public Vector3 DropSpot;
            public float DragEndTime;
            public float NextScanTime;
        }

        internal static readonly Dictionary<IntPtr, CasualtyDragState> _casualtyDragStates = new Dictionary<IntPtr, CasualtyDragState>(32);

        public static void ClearGoapCaches()
        {
            _squadBlackboards.Clear();
            _botGoapPlans.Clear();
            _botScavengeCaches.Clear();
            _freeFloorItemsSnapshot.Clear();
            _nextFreeFloorItemsSnapshotTime = 0f;
            _nextWeaponUpgradeAllowedTime.Clear();
            _nextBuddyBandageRefillTime.Clear();
            _nextBuddyAmmoResupplyTime.Clear();
            _nextBotBoxOrCrateResupplyTime.Clear();
            _nextSmokeGrenadeTime.Clear();
            _cachedBoxOverrides.Clear();
            _cachedFloorItemKinds.Clear();
            _casualtyDragStates.Clear();
            _cachedInventoryOwners.Clear();
            _initialHqSpawnBriefingConsumed.Clear();
            _nextSquadVoiceTime.Clear();
            _nextSoldierVoiceTime.Clear();
            _nextFlankCoverAssignTime.Clear();
            _medicHealStartTime.Clear();
            _medicHealTargetPtr.Clear();
            _pincerOrderedTaskBySquad.Clear();
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
            connectedInfantrySquads.Clear();
            activeVehicleAnvils.Clear();
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

                connectedInfantrySquads.Add(sq);
            }

            int infCount = connectedInfantrySquads.Count;
            if (infCount == 0)
                return;

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
                    // Lock in ANVIL directive on Squad A (Pin & Call Radio Support if Radioman ready)
                    anvilBoard.NetDirective = SquadNetDirective.Anvil_PinAndCallSupport;
                    anvilBoard.NetDirectiveExpireTime = now + 24.0f;
                    anvilBoard.NetDirectiveTargetPos = threatPos;
                    _squadBlackboards[anvilSq.Pointer] = anvilBoard;
                    ConsumeInitialHqSpawnBriefingIfNoRadio(anvilSq);

                    TrySayTacticalVoice(anvilLeader, anvilSq, VoiceManager.VoiceClip.coveringFire, 0.1f);
                    TryTriggerSquadRadioSupport(anvilSq, anvilLeader);

                    // Lock in HAMMER directive on Squad B
                    IssueHammerFlankDirective(bestHammerSq, anvilPos, threatPos, axisDir, perpRight, now, $"Infantry Anvil ('{anvilSq.squadName}')");
                }
                else if (anvilBoard.AliveMemberCount >= 4 &&
                         anvilBoard.IsThreatBeingPinned(now) &&
                         (anvilBoard.FlankLeftCover != null || anvilBoard.FlankRightCover != null) &&
                         anvilLeader.Params != null && anvilLeader.Params.canGiveOrders && anvilLeader.Params.canMove)
                {
                    // Single-Squad Self-Pin & Flank: MG gunner has the enemy pinned and lateral cover exists.
                    // Note: Do NOT call anvilSq.AttackFromSide(..., threatPos, ...) because setting sq.orderPosition to threatPos
                    // (outside sq.currentTask) causes vanilla Squad.Leader_OrderAttackCurrentTask to immediately overwrite the order 250ms later!
                    anvilBoard.NetDirective = SquadNetDirective.Hammer_WideFlankEnvelopment;
                    anvilBoard.NetDirectiveExpireTime = now + 22.0f;
                    anvilBoard.NetDirectiveTargetPos = threatPos;
                    _squadBlackboards[anvilSq.Pointer] = anvilBoard;
                    ConsumeInitialHqSpawnBriefingIfNoRadio(anvilSq);

                    TrySayTacticalVoice(anvilLeader, anvilSq, VoiceManager.VoiceClip.attackThere, 0.2f);
                    TryTriggerSquadRadioSupport(anvilSq, anvilLeader);
                    TeamNetAnvilHammerCount++;
                    GoapPlansExecutedCount++;
                }
            }

            // =================================================================
            // 2. Multi-Squad Objective Pincer (When 2+ Connected Squads Push the Same Objective)
            // =================================================================
            _reusablePrimarySquadByTask.Clear();
            _reusablePincerCountByTask.Clear();
            var primarySquadByTask = _reusablePrimarySquadByTask;
            var pincerCountByTask = _reusablePincerCountByTask;

            for (int i = 0; i < infCount; i++)
            {
                Squad sq = connectedInfantrySquads[i];
                if (sq.currentTask == null || sq.currentTask.Pointer == IntPtr.Zero)
                    continue;

                if (_squadBlackboards.TryGetValue(sq.Pointer, out SquadBlackboard board) &&
                    (board.HasActiveThreat || now < board.NetDirectiveExpireTime))
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

                board.NetDirective = SquadNetDirective.Hammer_WideFlankEnvelopment;
                board.NetDirectiveExpireTime = now + 28.0f;
                board.NetDirectiveTargetPos = taskPos;
                _squadBlackboards[sq.Pointer] = board;
                ConsumeInitialHqSpawnBriefingIfNoRadio(sq);

                // Only issue the squad-level AttackFromSide waypoint ONCE per objective assignment, AND cap at 1 AttackFromSide
                // call per TeamNetGOAP tick (gated by TryConsumeBotRpcBudget) so multiple squads never stack CoverManager sweeps
                // or burst 12-20 reliable PhotonView.RPCs on a single frame!
                if (!_pincerOrderedTaskBySquad.TryGetValue(sq.Pointer, out IntPtr prevPincerTask) || prevPincerTask != taskPtr)
                {
                    if (!issuedSquadCoverOrderThisTick &&
                        (!Photon.Pun.PhotonNetwork.InRoom || AiCore.TryConsumeBotRpcBudget(0.45f)))
                    {
                        issuedSquadCoverOrderThisTick = true;
                        _pincerOrderedTaskBySquad[sq.Pointer] = taskPtr;
                        try
                        {
                            sq.AttackFromSide(pincerFlankDir, taskPos, 22.0f);
                            if (sq.timed_order_end < now + 28.0f)
                                sq.timed_order_end = now + 28.0f;
                        }
                        catch { }

                        TeamNetAnvilHammerCount++;
                        GoapPlansExecutedCount++;
                        if (VisionConfig.DebugLogging.Value)
                        {
                            AiCore.LogDebug(
                                "TeamNetPincer",
                                $"{leader.faction} squad '{sq.squadName}' executing objective pincer (latBias:{latBias:F2}, dist:{distToTask:F0}m).",
                                4.0f);
                        }
                    }
                }
            }
            _reusablePrimarySquadByTask.Clear();
            _reusablePincerCountByTask.Clear();
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
                if (candSq == null || candSq.Pointer == anvilSqPtr)
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
            hammerBoard.NetDirective = SquadNetDirective.Hammer_WideFlankEnvelopment;
            hammerBoard.NetDirectiveExpireTime = now + 24.0f;
            hammerBoard.NetDirectiveTargetPos = threatPos;
            _squadBlackboards[hammerSq.Pointer] = hammerBoard;
            ConsumeInitialHqSpawnBriefingIfNoRadio(hammerSq);

            // Note: Do NOT call hammerSq.AttackFromSide(flankDir, threatPos, 20f) here!
            // In vanilla Squad.Leader_OrderAttackCurrentTask (0x180806E8B), if sq.orderPosition is set to threatPos
            // (outside sq.currentTask), vanilla immediately overwrites the order 250ms later with a new voice callout.
            // Instead, Hammer_WideFlankEnvelopment drives individual soldier GOAP flanking & lateral cover selection!
            TrySayTacticalVoice(hammerLeader, hammerSq, VoiceManager.VoiceClip.attackThere, 0.25f);
            TeamNetAnvilHammerCount++;
            GoapPlansExecutedCount++;

            if (VisionConfig.DebugLogging.Value)
            {
                AiCore.LogDebug(
                    "TeamNetGOAP",
                    $"{hammerLeader.faction} Radio Net coordinated {anvilLabel} + Hammer wide flank ('{hammerSq.squadName}').");
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

            board.NextRefreshTime = now + 0.60f;

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

            // Scan squad members once to find active threat, alive count, MG gunner/Medic presence & surrendered comrades
            board.HasActiveThreat = false;
            board.ThreatIsArmor = false;
            board.HasAliveMachineGunner = false;
            board.HasAliveMedic = false;
            board.PrimaryTarget = null;
            board.SurrenderedComrade = null;
            board.AliveMemberCount = 0;

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

        private static int _lastFlankCoverOctreeFrame = -1;

        private static void FindSquadFlankCovers(
            Vector3 squadCenter,
            Vector3 threatPos,
            Vector3 threatDir,
            string faction,
            out CombatCover leftFlankCover,
            out CombatCover rightFlankCover)
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

        // =====================================================================
        // Tier 1: Individual Bot 32-Bit WorldState & Bounded 2-Step GOAP Planner
        // =====================================================================
        internal static bool HasActiveGoapCoverLock(IntPtr soldierPtr, float now)
        {
            return _botGoapPlans.TryGetValue(soldierPtr, out BotGoapPlan plan) &&
                   plan.LockUnitCoverChange &&
                   now < plan.PlanExpireTime;
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
                soldier.IsCharging() ||
                soldier.isInBodyAnimation ||
                isStaffingEmplacement)
            {
                return;
            }

            bool canMoveAutonomously = AiCore.CanBotMoveAutonomously(soldier);
            if (!canMoveAutonomously && !AiCore.CanBotChangePoseAutonomously(soldier))
            {
                return;
            }

            IntPtr ptr = soldier.Pointer;
            float now = Time.time;
            Squad sq = soldier.joinedSquad;
            SquadBlackboard board = GetOrUpdateSquadBlackboard(sq, soldier);

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
            bool emergencyPreempt =
                ((ws & GoapStateBits.InPointBlankRange) != 0 && plan.Step0 != GoapActionId.PointBlankBayonetBreach) ||
                ((ws & GoapStateBits.IsBleeding) != 0 &&
                 plan.Step0 != GoapActionId.SeekEmergencyCover &&
                 plan.Step0 != GoapActionId.BandageInCover &&
                 plan.Step0 != GoapActionId.ScavengeSupplyFromCorpse) ||
                ((ws & GoapStateBits.RadioLinkDrifting) != 0 && plan.Step0 != GoapActionId.MaintainRadioLinkWithLeader) ||
                (isEvadingArmor && plan.Step0 != GoapActionId.SeekEmergencyCover && plan.Step0 != GoapActionId.ScavengeSupplyFromCorpse) ||
                ((ws & GoapStateBits.IsHeavilySuppressed) != 0 && (ws & GoapStateBits.InCover) == 0 && plan.Step0 != GoapActionId.SeekEmergencyCover);

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
            if (sq != null && sq.Pointer != IntPtr.Zero)
            {
                _squadBlackboards[sq.Pointer] = board;
            }
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
            bool isRemountingOrRepairing =
                soldier.IsReachingVehicleToMount() ||
                soldier.IsEnteringVehicle ||
                soldier.VehicleToOperate() != null ||
                (soldier.targetDestination != null && soldier.targetDestination.TryCast<VehicleRepairTask>() != null);
            bool isBailedVehicleCrew = sq != null &&
                                       sq.IsVehicleCrew &&
                                       !soldier.IsOnVehicle() &&
                                       !sq.SomeoneIsRepairing() &&
                                       !isRemountingOrRepairing;

            if (soldier.isBleeding)
                ws |= GoapStateBits.IsBleeding;
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

            if (ai.visibleTarget != null)
                ws |= GoapStateBits.HasVisibleEnemy;
            if (ai.HasEnemyAndCanFireAtHim)
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
                    // If Radioman drifts > 18m from Leader OR Leader is waiting to confirm a radio request (> 5m), pull Radioman to Leader!
                    if (dToLeaderSq > 18f * 18f || (sq.RadioRequestWaiting() && dToLeaderSq > 5.0f * 5.0f))
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
                !ai.IsMoving() &&
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
                if (HasClearLineOfFireFromCrouch(soldier, tPos, dist) || HasClearLineOfFireFromStanding(soldier, tPos, dist))
                {
                    ws |= GoapStateBits.CanProvideOverwatch;
                }
            }

            // Detect when the soldier is "Entirely Alone" (last survivor of squad, Leader dead and no squadmates within 35m,
            // or a bailed-out Vehicle Crew on foot whose vehicle was destroyed and is NOT already executing MissionTaskRetreat!)
            bool onRetreatTask = IsSquadOnRetreatTask(sq);
            if (!onRetreatTask)
            {
                if (sq == null || board.AliveMemberCount <= 1 || isBailedVehicleCrew)
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
                bool casualScavengeAllowed = !urgentBandageWhileBleeding && !isEvadingArmor;
                bool outOfAmmo = casualScavengeAllowed &&
                                 ((gun0 != null && soldier.IsOutOfAmmosFor(gun0)) ||
                                  (gun1 != null && gun1.IsAtWeapon() && !string.IsNullOrEmpty(gun1.compatibleAmmo) && soldier.IsOutOfAmmosFor(gun1)) ||
                                  (gun2 != null && !gun2.IsPistol() && !gun2.IsAtWeapon() && soldier.IsOutOfAmmosFor(gun2)));
                bool outOfBandage = !isEvadingArmor && !hasBandage;

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
                }
                bool wantAnyWeaponUpgrade = needUrgentPrimary || wantSquadMg || wantRoleUpgrade;

                bool wantRadioPack = casualScavengeAllowed &&
                                     VisionConfig.EnableEmergentWearableScavenge.Value &&
                                     !onRetreatTask &&
                                     sq != null &&
                                     !sq.IsVehicleCrew &&
                                     !board.PreviouslyHadAliveRadioman &&
                                     !isRad &&
                                     !isMedicRole &&
                                     (soldier.vest_Obj == null || !soldier.vest_Obj.isFuelTank);

                bool wantMedicSyringe = casualScavengeAllowed &&
                                        !onRetreatTask &&
                                        sq != null &&
                                        !sq.IsVehicleCrew &&
                                        !board.HasAliveMedic &&
                                        !soldier.hasSyringe &&
                                        !isRad &&
                                        !soldier.IsSquadLeader();

                bool wantHelmet = casualScavengeAllowed &&
                                  VisionConfig.EnableEmergentWearableScavenge.Value &&
                                  !onRetreatTask &&
                                  !soldier.hasHeadgear &&
                                  suppression < 12f;

                bool canFeedNeedyAlly = casualScavengeAllowed &&
                                        VisionConfig.EnableBuddyResupply.Value &&
                                        !onRetreatTask &&
                                        !outOfAmmo &&
                                        soldier.IsAmmoBoxCarrier();

                bool wantFragGrenade = casualScavengeAllowed &&
                                       !onRetreatTask &&
                                       !isMedicRole &&
                                       suppression < 16f &&
                                       soldier.inventory != null &&
                                       soldier.inventory.FindItemOfType<VirtualGrenade>() == null;

                // Do not immediately break off a smoke-covered push just to scavenge another smoke grenade right after throwing one!
                bool wantSmokeGrenade = casualScavengeAllowed &&
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

            // Win-Condition Urgency: Defenders must contest objectives being back-capped/captured (capProgress >= 0.12f),
            // and Attackers push onto the cap circle when capture progress is >= 0.45f or tickets are critically low (<= 0.25f)!
            if (board.HasActiveObjectiveTask && sq != null && sq.currentTask != null)
            {
                float dToObjSq = (board.ObjectivePosition - myPos).sqrMagnitude;
                if (dToObjSq <= 85f * 85f && !sq.currentTask.IsInsideTaskArea(myPos, 0.85f))
                {
                    bool defenderPointBeingCapped = board.IsDefendingObjective && board.ObjectiveCaptureProgress >= 0.12f;
                    bool attackerMomentumOrLastStand = !board.IsDefendingObjective &&
                                                       (board.ObjectiveCaptureProgress >= 0.45f || board.TicketRatio01 <= 0.25f);
                    if (defenderPointBeingCapped || attackerMomentumOrLastStand)
                    {
                        ws |= GoapStateBits.ObjectiveCapContestUrgent;
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
            GoapStateBits desiredGoalBit;
            if ((ws & (GoapStateBits.IsBleeding | GoapStateBits.ArmorThreatClose | GoapStateBits.ExposedSideOfWall)) != 0 ||
                ((ws & GoapStateBits.IsHeavilySuppressed) != 0 && (ws & GoapStateBits.InCover) == 0))
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
            else if ((ws & GoapStateBits.IsEntirelyAlone) != 0 && (ws & GoapStateBits.InCover) == 0)
            {
                desiredGoalBit = GoapStateBits.SurvivedCrisis;
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
                bool preferSupportOrPin =
                    (ws & GoapStateBits.ObjectiveCapContestUrgent) == 0 &&
                    (board.NetDirective == SquadNetDirective.Anvil_PinAndCallSupport ||
                     (ws & (GoapStateBits.IsMachineGunner | GoapStateBits.IsRadioman)) != 0 ||
                     isMedic ||
                     personality == SoldierPersonality.Gunner_Anchor ||
                     personality == SoldierPersonality.Gunner_Overwatch ||
                     personality == SoldierPersonality.Leader_Tactician ||
                     ((ws & GoapStateBits.AllyPinningThreat) == 0 && (ws & GoapStateBits.CanProvideOverwatch) != 0));

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
                                                (ws & GoapStateBits.IsBleeding) != 0;
                    bool canChainSmokePush = a0.Id == GoapActionId.ThrowSmokeScreen &&
                                             (ws & GoapStateBits.HasSmokeGrenade) != 0 &&
                                             (ws & GoapStateBits.AllyPinningThreat) == 0;
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
                             (a1.Id == GoapActionId.MoveToFlankCover || a1.Id == GoapActionId.BoundWithOverwatch))
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

            // Fallback Goal Bridge: if TargetEliminated had no valid path during an active squad firefight
            // (e.g. on a narrow bridge or pinned without flank cover or smoke), fall back to SuppressThreatPosition
            // so a combat soldier never stalls with GoapActionId.None!
            if (bestStep0 == GoapActionId.None && (ws & GoapStateBits.SquadHasActiveThreat) != 0 && (ws & GoapStateBits.IsBleeding) == 0)
            {
                bestStep0 = (ws & GoapStateBits.CanProvideOverwatch) != 0
                    ? GoapActionId.HoldOverwatchFire
                    : GoapActionId.SuppressThreatPosition;
            }

            return new BotGoapPlan
            {
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
                    if (urgentObjPush)
                        cost -= 0.55f;
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
                    if (urgentObjPush || lowTicketConservation)
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
                    if (lowTicketConservation)
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
                    // When objective is being back-capped or needs an overtime cap push, penalize sitting outside the cap radius!
                    if (urgentObjPush)
                        cost += 0.65f;
                    else if (lowTicketConservation)
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
                    // Win-Condition Priority: strongly favor bounding into the cap zone when contesting or finishing an objective!
                    if (urgentObjPush)
                        cost -= 0.75f;
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

            switch (action)
            {
                case GoapActionId.SeekEmergencyCover:
                    if (TrySeekNearbyCover(ai, soldier))
                    {
                        plan.LockUnitCoverChange = true;
                        SetBotPose(ai, soldier, SoldierPose.Crouch);
                        if (soldier.IsOnCover() && plan.StepCount > 1)
                        {
                            plan.StepIndex = 1;
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
                        float radioDistSq = (myPos - safeRadioSpot).sqrMagnitude;
                        if (radioDistSq > 4.0f * 4.0f)
                        {
                            if (soldier.GetSuppressionValue() >= 12f)
                                SetBotPose(ai, soldier, SoldierPose.Crouch);
                            else if (soldier.m_pose != SoldierPose.Idle)
                                SetBotPose(ai, soldier, SoldierPose.Idle, true);

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
                                TrySayTacticalVoice(soldier, sq, VoiceManager.VoiceClip.keepYourHeadDown, 0.1f);
                                if (VisionConfig.DebugLogging.Value)
                                {
                                    AiCore.LogDebug(
                                        "GoapSmoke",
                                        $"{soldier.faction} {GetPersonality(soldier)} threw GOAP smoke screen ({dToThreat:F0}m, nextStep:{plan.Step1}).",
                                        3.0f);
                                }
                                if (plan.StepCount > 1)
                                    plan.StepIndex = 1;
                            }
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

                    if (action == GoapActionId.HoldOverwatchFire && ai.IsMoving() &&
                        AiCore.TryGetSpottableCenter(ai.visibleTarget, out Vector3 owTargetPos))
                    {
                        ai.moveCharacter = false;
                        bool crouchClear = HasClearLineOfFireFromCrouch(soldier, owTargetPos, dist);
                        SetBotPose(ai, soldier, crouchClear ? SoldierPose.Crouch : SoldierPose.Idle, true);
                        if (!_nextBoundingHoldCountTime.TryGetValue(ptr, out float nextBoundCount) || now >= nextBoundCount)
                        {
                            _nextBoundingHoldCountTime[ptr] = now + 5.0f;
                            BoundingOverwatchHoldsCount++;
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
                                    bool crouchOnFlank = soldier.GetSuppressionValue() >= 12f ||
                                                         (chosenCover.GetCoverPosition() - myPos).sqrMagnitude <= 4.5f * 4.5f;
                                    SetBotPose(ai, soldier, crouchOnFlank ? SoldierPose.Crouch : SoldierPose.Idle, !crouchOnFlank);
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
                        bool crouchBound = soldier.GetSuppressionValue() >= 12f ||
                                           (ai.MoveDestination - myPos).sqrMagnitude <= 4.5f * 4.5f;
                        if (crouchBound)
                            SetBotPose(ai, soldier, SoldierPose.Crouch);
                        else if (soldier.m_pose != SoldierPose.Idle)
                            SetBotPose(ai, soldier, SoldierPose.Idle, true);
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
                            if (dSq <= 4.5f * 4.5f || soldier.GetSuppressionValue() >= 12f)
                            {
                                SetBotPose(ai, soldier, SoldierPose.Crouch);
                            }
                            else if (soldier.m_pose != SoldierPose.Idle)
                            {
                                SetBotPose(ai, soldier, SoldierPose.Idle, true);
                            }
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
                                if (dSq <= 4.5f * 4.5f || soldier.GetSuppressionValue() >= 12f)
                                    SetBotPose(ai, soldier, SoldierPose.Crouch);
                                else if (soldier.m_pose != SoldierPose.Idle)
                                    SetBotPose(ai, soldier, SoldierPose.Idle, true);

                                AiCore.MoveBotTowardPosition(ai, soldier, targetSurr.transform.position, 2.0f, 2.8f);
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
                        bool loneSuppressed = soldier.GetSuppressionValue() >= 12f;
                        SoldierPersonality lonePers = GetPersonality(soldier);
                        bool isBailedCrewOrPilot = (sq != null && sq.IsVehicleCrew) || IsPilotOrVehicleCrewPersonality(lonePers);
                        if (isBailedCrewOrPilot)
                        {
                            if (TryRemountNearbyOperableCrewVehicle(ai, soldier, sq, myPos, 28f))
                            {
                                SetBotPose(ai, soldier, loneSuppressed ? SoldierPose.Crouch : SoldierPose.Idle, !loneSuppressed);
                                break;
                            }

                            if (ShouldBailedCrewRetreatOverJoinSquad(soldier, sq, myPos))
                            {
                                if (TryRetreatBailedCrewTowardFriendlySpawn(ai, soldier, sq, myPos))
                                {
                                    SetBotPose(ai, soldier, loneSuppressed ? SoldierPose.Crouch : SoldierPose.Idle, !loneSuppressed);
                                    break;
                                }
                            }
                            else if (TryLinkUpOrJoinFriendlySquad(ai, soldier, myPos, 85f))
                            {
                                SetBotPose(ai, soldier, loneSuppressed ? SoldierPose.Crouch : SoldierPose.Idle, !loneSuppressed);
                                break;
                            }
                        }

                        bool preferAmbush = !isBailedCrewOrPilot && GetFactionCommandProfile(soldier.faction).PreferAmbushWhenAlone;
                        if (preferAmbush)
                        {
                            if (!soldier.IsOnCover() && TrySeekNearbyCover(ai, soldier))
                            {
                                plan.LockUnitCoverChange = true;
                                if (loneSuppressed)
                                    SetBotPose(ai, soldier, SoldierPose.Crouch);
                            }
                            else if (soldier.IsOnCover())
                            {
                                SetBotPose(ai, soldier, SoldierPose.Crouch);
                            }
                            else if (TryLinkUpOrJoinFriendlySquad(ai, soldier, myPos, 55f))
                            {
                                SetBotPose(ai, soldier, loneSuppressed ? SoldierPose.Crouch : SoldierPose.Idle, !loneSuppressed);
                            }
                        }
                        else
                        {
                            if (TryLinkUpOrJoinFriendlySquad(ai, soldier, myPos, 75f))
                            {
                                SetBotPose(ai, soldier, loneSuppressed ? SoldierPose.Crouch : SoldierPose.Idle, !loneSuppressed);
                            }
                            else if (!soldier.IsOnCover() && TrySeekNearbyCover(ai, soldier))
                            {
                                plan.LockUnitCoverChange = true;
                            }
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

        private static bool TryRetreatBailedCrewTowardFriendlySpawn(SoldierAI ai, Soldier soldier, Squad sq, Vector3 myPos)
        {
            if (ai == null || soldier == null || string.IsNullOrEmpty(soldier.faction))
                return false;

            if (IsSquadOnRetreatTask(sq))
                return true;

            try
            {
                if (!AiCore.TryGetNearestFriendlySpawnPosition(soldier.faction, myPos, out Vector3 spawnPos))
                    return false;

                float distSq = (spawnPos - myPos).sqrMagnitude;
                if (distSq < 18f * 18f)
                    return false;

                if (sq != null && sq.IsVehicleCrew && sq.currentTask == null)
                {
                    var retreatTask = new MissionTaskRetreat(spawnPos, sq, false);
                    sq.currentTask = retreatTask.Cast<SquadTask>();
                    sq.HoldArea(spawnPos, 12.0f, false);
                    return true;
                }

                AiCore.MoveBotTowardPosition(ai, soldier, spawnPos, 4.0f, 3.2f);
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
                if (!onRetreatTask || board.HasActiveThreat || soldier.GetSuppressionValue() >= 8)
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
                sq != null && !sq.IsVehicleCrew &&
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

            // Walking-Pace Friendly Tank Escort: if a friendly tank nearby (<= 17.5m) is rolling forward
            // toward the threat, advance in its sheltered rear-quarter wake using its hull as mobile armor cover!
            if (TryFindTankHullCoverOrEscortSpot(soldier, myPos, threatDir, true, out Vector3 tankSpot, out bool isMovingEscort) &&
                isMovingEscort)
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
            if (octree == null)
                return;

            Vector3 searchCenter = myPos + threatDir * 12.0f;
            var nearby = octree.GetNearby(searchCenter, 14.0f);
            if (nearby == null || nearby.Length == 0)
                return;

            float myDistToThreatSq = (threatPos - myPos).sqrMagnitude;
            var unreachable = soldier.nonReachableCovers;
            CombatCover bestAdvanceCover = null;
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
                if (cc == null)
                    continue;

                Vector3 covPos = cc.GetCoverPosition();
                if (Mathf.Abs(covPos.y - myPos.y) > 2.8f)
                    continue;

                float dFromMeSq = (covPos - myPos).sqrMagnitude;
                if (dFromMeSq < 5.5f * 5.5f || dFromMeSq > 22.0f * 22.0f)
                    continue;

                float covDistToThreatSq = (threatPos - covPos).sqrMagnitude;
                if (covDistToThreatSq >= myDistToThreatSq - 25.0f || covDistToThreatSq < 8.0f * 8.0f)
                    continue;

                if (!cc.IsCoverAvailable(threatDir, soldier.faction))
                    continue;

                float advanceGain = Vector3.Dot(covPos - myPos, threatDir);
                if (advanceGain < 4.5f)
                    continue;

                float score = advanceGain - Mathf.Sqrt(dFromMeSq) * 0.25f;
                if (score > bestScore)
                {
                    bestScore = score;
                    bestAdvanceCover = cc;
                }
            }

            if (bestAdvanceCover != null)
            {
                soldier.CoverPosition(bestAdvanceCover.Cast<AiDestination>());
                SetBotPose(ai, soldier, SoldierPose.Crouch);
                plan.LockUnitCoverChange = true;
                plan.PlanExpireTime = now + 5.5f;
                plan.NextReplanTime = Mathf.Max(plan.NextReplanTime, now + 3.6f);
                _localCoverProtectedUntil[ptr] = now + 5.5f;
                GoapPlansExecutedCount++;
                TrySayTacticalVoice(soldier, sq, VoiceManager.VoiceClip.imMoving, 0.20f);
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

            // 2. Scan nearby verified dead corpse inventories and supply crates (InventoryManager.activeInventories)
            var activeList = InventoryManager.activeInventories;
            if (activeList != null)
            {
                IntPtr myInvPtr = soldier.inventory != null ? soldier.inventory.Pointer : IntPtr.Zero;
                int count = activeList.Count;

                for (int i = 0; i < count; i++)
                {
                    InventoryManager inv = activeList[i];
                    if (inv == null || inv.Pointer == IntPtr.Zero || inv.Pointer == myInvPtr)
                        continue;

                    if (!IsInventoryValidCorpseOrCrate(
                            inv,
                            soldier.faction,
                            needAmmo || wantFragGrenade || wantSmokeGrenade,
                            needBandage || wantMedicSyringe,
                            out Soldier owner,
                            out bool isDeadBodyOrCrate))
                    {
                        continue;
                    }

                    Vector3 invPos = inv.InventoryPosition;
                    if (Mathf.Abs(invPos.y - myPos.y) > 2.4f)
                        continue;

                    float dSq = (invPos - myPos).sqrMagnitude;
                    if (dSq >= bestDistSq)
                        continue;

                    if (IsScavengeTargetClaimedByOtherBot(ptr, inv.Pointer, now))
                        continue;

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

                        if (wantHelmet && dSq <= 8.5f * 8.5f && owner != null && owner.faction == soldier.faction)
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

            if (bestDistSq <= 4.0f * 4.0f || soldier.GetSuppressionValue() >= 12f)
                SetBotPose(ai, soldier, SoldierPose.Crouch);
            else if (soldier.m_pose != SoldierPose.Idle)
                SetBotPose(ai, soldier, SoldierPose.Idle, true);

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

                if (veh.IsDisabled() || veh.IsCoverDestroyed() || veh.IsAirVehicle())
                    continue;

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

        private static bool TryLinkUpOrJoinFriendlySquad(SoldierAI ai, Soldier self, Vector3 myPos, float maxRadius)
        {
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
                            if (ai != null)
                            {
                                Vector3 targetOrderPos = (bestSquad.order != Order.follow && bestSquad.moveOrderPosition.sqrMagnitude > 0.1f)
                                    ? bestSquad.moveOrderPosition
                                    : linkUpPos;
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
