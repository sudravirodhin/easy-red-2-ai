using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace EasyRed2HumanAI
{
    public static partial class VeteranAndTacticsPatches
    {
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
            public IntPtr ClaimedTargetPtr;
            public IntPtr ClaimingSquadPtr;
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
            public bool IsEnemyRadioCapture;
            public bool IsHelmetPickup;
            public bool IsMedicSyringePickup;
            public bool IsAtMinePickup;
            public bool IsDeployableTripodPickup;
            public bool IsShellAmmoPickup;
            public bool IsToolboxPickup;
            public bool IsRecoverLifePickup;
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
            Syringe = 12,
            AtMine = 13,
            DeployableTripod = 14,
            ShellAmmo = 15,
            Toolbox = 16,
            RecoverLife = 17
        }

        private struct FreeFloorItemEntry
        {
            public ItemObject Item;
            public DroppableAmmoBox Box;
            public GenericGun Gun;
            public Magazine Mag;
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
            public Magazine Mag;
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
        private static readonly Dictionary<string, string> _cachedMagazineSocketsByItemId = new Dictionary<string, string>(256, StringComparer.Ordinal);
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
            Magazine mag = null;
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
                        if (ig.fuseType == FuseType.InfantryPressionFuse)
                        {
                            // Vanilla ItemGrenade.FixedUpdate has zero detonation code for InfantryPressionFuse (1); skip inert duds!
                            kind = FloorItemKind.Ignore;
                        }
                        else if (ig.fuseType == FuseType.VehiclePressionFuse)
                        {
                            // Unarmed (!ig.activated) AT mines on bunker/trench floors can be scavenged on armor maps!
                            kind = FloorItemKind.AtMine;
                        }
                        else
                        {
                            GrenadeType gt = ig.grenadeType;
                            if (gt == GrenadeType.ATGrenade)
                                kind = FloorItemKind.AtGrenade;
                            else if (gt == GrenadeType.smokeGrenade)
                                kind = FloorItemKind.SmokeGrenade;
                            else
                                kind = FloorItemKind.FragGrenade;
                        }
                    }
                    else if ((mag = item.TryCast<Magazine>()) != null)
                    {
                        kind = FloorItemKind.Magazine;
                        if (!string.IsNullOrEmpty(item.item_id) && !string.IsNullOrEmpty(mag.socket))
                        {
                            if (_cachedMagazineSocketsByItemId.Count > 1024)
                                _cachedMagazineSocketsByItemId.Clear();
                            _cachedMagazineSocketsByItemId[item.item_id] = mag.socket;
                        }
                    }
                    else if (item.TryCast<ShellAmmoItem>() != null)
                    {
                        kind = FloorItemKind.ShellAmmo;
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
                    else if (item.TryCast<ItemObjectDeployableVehicle>() != null)
                    {
                        kind = FloorItemKind.DeployableTripod;
                    }
                    else if (item.TryCast<ItemObjectToolbox>() != null)
                    {
                        kind = FloorItemKind.Toolbox;
                    }
                    else if (item.TryCast<ItemObjectRecoverLife>() != null)
                    {
                        kind = FloorItemKind.RecoverLife;
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
                Mag = mag,
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
                        Mag = meta.Mag,
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
            if (targetPtr == IntPtr.Zero)
                return false;

            AiCore.EnsureWorldSnapshot();
            var soldiers = AiCore.WorldSoldiers;
            int count = AiCore.WorldSoldierCount;
            for (int i = 0; i < count; i++)
            {
                ref AiCore.SoldierSnapshot sSnap = ref soldiers[i];
                if (sSnap.Ptr == IntPtr.Zero || sSnap.Ptr == selfPtr || sSnap.Handles == null)
                    continue;

                ref BotScavengeCache other = ref sSnap.Handles.Goap.Scavenge;
                if (now >= other.NextScanTime)
                    continue;

                if (other.ClaimedTargetPtr == targetPtr)
                    return true;
            }
            return false;
        }

        private static bool IsRadioPackAlreadyClaimedForSquad(IntPtr selfPtr, IntPtr sqPtr, float now)
        {
            if (sqPtr == IntPtr.Zero)
                return false;

            AiCore.EnsureWorldSnapshot();
            var soldiers = AiCore.WorldSoldiers;
            int count = AiCore.WorldSoldierCount;
            for (int i = 0; i < count; i++)
            {
                ref AiCore.SoldierSnapshot sSnap = ref soldiers[i];
                if (sSnap.Ptr == IntPtr.Zero || sSnap.Ptr == selfPtr || sSnap.Handles == null)
                    continue;

                ref BotScavengeCache other = ref sSnap.Handles.Goap.Scavenge;
                if (now >= other.NextScanTime)
                    continue;

                if (other.IsRadioPackPickup && other.ClaimingSquadPtr == sqPtr)
                    return true;
            }
            return false;
        }

        private static bool CanSoldierAcceptExtraMass(Soldier soldier, float extraMass, float bufferKg = 0.20f)
        {
            if (soldier == null || soldier.Pointer == IntPtr.Zero || soldier.inventory == null)
                return false;

            soldier.inventory.GetWeightAndMaxWeight(out float curW, out float maxW);
            if (maxW <= 0f)
                return true;

            return (curW + Mathf.Max(0f, extraMass)) <= (maxW - bufferKg);
        }

        private static bool TryResolveMagazineSocketCached(string itemId, out string socket)
        {
            socket = null;
            if (string.IsNullOrEmpty(itemId))
                return false;

            if (_cachedMagazineSocketsByItemId.TryGetValue(itemId, out socket))
                return !string.IsNullOrEmpty(socket);

            GameObject prefabGo = ItemsDatabase.GetPropPrefabCached(itemId);
            if (prefabGo != null)
            {
                Magazine magComp = prefabGo.GetComponent<Magazine>();
                if (magComp != null && !string.IsNullOrEmpty(magComp.socket))
                {
                    if (_cachedMagazineSocketsByItemId.Count > 1024)
                        _cachedMagazineSocketsByItemId.Clear();
                    socket = magComp.socket;
                    _cachedMagazineSocketsByItemId[itemId] = socket;
                    return true;
                }
            }

            return false;
        }

        private static bool DoesGunSupportAmmoOrMag(
            GenericGun gun,
            string itemId,
            bool isMagazine,
            string knownMagSocket,
            out bool compatibilityKnown)
        {
            compatibilityKnown = true;
            if (gun == null || gun.Pointer == IntPtr.Zero || string.IsNullOrEmpty(itemId))
                return false;

            if (!string.IsNullOrEmpty(gun.compatibleAmmo) &&
                string.Equals(gun.compatibleAmmo, itemId, StringComparison.Ordinal))
            {
                return true;
            }

            if (!isMagazine)
                return false;

            if (string.IsNullOrEmpty(gun.magazineSocket))
                return false;

            string sock = !string.IsNullOrEmpty(knownMagSocket) ? knownMagSocket : null;
            if (sock == null && !TryResolveMagazineSocketCached(itemId, out sock))
            {
                var compatMap = WeaponSocketCompatibilityChecker.socketCompatibilityMap;
                if (compatMap != null && compatMap.ContainsKey(gun.magazineSocket))
                {
                    var magIds = compatMap[gun.magazineSocket];
                    if (magIds != null && magIds.Contains(itemId))
                        return true;
                }

                compatibilityKnown = false;
                return false;
            }

            return string.Equals(gun.magazineSocket, sock, StringComparison.Ordinal);
        }

        private static bool IsAmmoOrMagCompatibleWithAnyHeldGun(
            Soldier soldier,
            string itemId,
            bool isMagazine,
            string knownMagSocket = null)
        {
            if (soldier == null || soldier.Pointer == IntPtr.Zero || string.IsNullOrEmpty(itemId))
                return false;

            for (int slot = 0; slot < 3; slot++)
            {
                GenericGun g = soldier.GetHeldGun(slot);
                if (g != null && DoesGunSupportAmmoOrMag(g, itemId, isMagazine, knownMagSocket, out _))
                    return true;
            }

            return false;
        }

        private static void ScheduleBotInventoryWeightAudit(Soldier soldier, float delaySeconds = 0.70f)
        {
            AiCore.SoldierFastHandles h = AiCore.GetOrCreateSoldierHandles(soldier);
            if (h == null)
                return;

            float target = Time.time + delaySeconds;
            if (h.NextInventoryWeightAuditTime <= 0f || target < h.NextInventoryWeightAuditTime)
                h.NextInventoryWeightAuditTime = target;
        }

        private static void ScheduleDelayedPickedWeaponRefill(Soldier soldier, int slot, float now)
        {
            AiCore.SoldierFastHandles h = AiCore.GetOrCreateSoldierHandles(soldier);
            if (h == null)
                return;

            h.PendingPickedWeaponSlot = slot;
            h.PendingPickedWeaponRefillTime = now + 0.60f;
            h.NextInventoryWeightAuditTime = now + 1.25f;
        }

        private static void PruneSurplusReserveAmmoPass(
            Inventory rawInv,
            Il2CppSystem.Collections.Generic.List<VirtualItem> items,
            GenericGun primaryGun,
            float targetMaxWeight,
            int minPrimaryKeepCount,
            int minStackRoundKeep,
            bool allowShedAllIfLoaded,
            bool primaryLoaded,
            ref int primaryReserves,
            ref float curW)
        {
            for (int i = items.Count - 1; i >= 0 && curW > targetMaxWeight; i--)
            {
                VirtualItem vi = items[i];
                if (vi == null || vi.Pointer == IntPtr.Zero || vi.IsWearedItem() || vi.IsInstance())
                    continue;

                VirtualMagazineItem vm = vi.TryCast<VirtualMagazineItem>();
                VirtualAmmo va = vm == null ? vi.TryCast<VirtualAmmo>() : null;
                if (vm == null && va == null)
                    continue;

                bool isMag = vm != null;
                bool matchesG0 = primaryGun != null && DoesGunSupportAmmoOrMag(primaryGun, vi.item_id, isMag, null, out _);
                float mass = Mathf.Max(0f, vi.GetMass());

                bool canRemoveWhole = !matchesG0 ||
                                      primaryReserves > minPrimaryKeepCount ||
                                      (allowShedAllIfLoaded && primaryLoaded);
                if (canRemoveWhole)
                {
                    rawInv.RemoveVirtualItem(vi);
                    curW -= mass;
                    if (matchesG0)
                        primaryReserves--;
                    continue;
                }

                if (va != null && minStackRoundKeep > 0)
                {
                    int cnt = va.GetAmmoCount();
                    int minKeep = primaryReserves <= 1 ? Mathf.Min(cnt, minStackRoundKeep) : 1;
                    if (cnt > minKeep && mass > 0.001f)
                    {
                        float unitMass = mass / cnt;
                        float excess = curW - targetMaxWeight + 0.05f;
                        int dropCnt = Mathf.Clamp(Mathf.CeilToInt(excess / unitMass), 1, cnt - minKeep);
                        va.SetAmmoCount(cnt - dropCnt);
                        curW -= dropCnt * unitMass;
                    }
                }
            }
        }

        private static void PruneBotInventoryForSprintWeight(Soldier soldier, bool purgeOrphanedCalibers)
        {
            if (!AiCore.IsLocallyOwnedBot(soldier) ||
                soldier.inventory == null ||
                soldier.inventory.inventory == null)
            {
                return;
            }

            Inventory rawInv = soldier.inventory.inventory;
            var items = rawInv.items;
            if (items == null)
                return;

            soldier.inventory.RemoveEmptyAmmoAndMagazines();
            soldier.inventory.GetWeightAndMaxWeight(out float curW, out float maxW);
            if (!purgeOrphanedCalibers && (maxW <= 0f || curW <= maxW - 0.05f))
                return;

            GenericGun g0 = soldier.GetHeldGun(0);
            GenericGun g1 = soldier.GetHeldGun(1);
            GenericGun g2 = soldier.GetHeldGun(2);

            bool anyRemoved = false;
            for (int i = items.Count - 1; i >= 0; i--)
            {
                VirtualItem vi = items[i];
                if (vi == null || vi.Pointer == IntPtr.Zero || vi.IsWearedItem() || vi.IsInstance())
                    continue;

                VirtualMagazineItem vMag = vi.TryCast<VirtualMagazineItem>();
                VirtualAmmo vAmmo = vMag == null ? vi.TryCast<VirtualAmmo>() : null;
                if (vMag == null && vAmmo == null)
                    continue;

                bool isMag = vMag != null;
                int cnt = isMag ? vMag.GetAmmoCount() : vAmmo.GetAmmoCount();
                if (cnt <= 0)
                {
                    rawInv.RemoveVirtualItem(vi);
                    anyRemoved = true;
                    continue;
                }

                if (!purgeOrphanedCalibers && curW <= maxW - 0.05f)
                    continue;

                string itemId = vi.item_id;
                bool compat = false;
                bool allKnown = true;
                GenericGun[] held = { g0, g1, g2 };
                for (int s = 0; s < 3; s++)
                {
                    GenericGun g = held[s];
                    if (g == null)
                        continue;
                    if (DoesGunSupportAmmoOrMag(g, itemId, isMag, null, out bool known))
                    {
                        compat = true;
                        break;
                    }
                    if (!known)
                        allKnown = false;
                }

                if (!compat && allKnown && (g0 != null || g1 != null || g2 != null))
                {
                    rawInv.RemoveVirtualItem(vi);
                    anyRemoved = true;
                }
            }

            if (anyRemoved)
                soldier.inventory.GetWeightAndMaxWeight(out curW, out maxW);
            if (maxW <= 0f || curW <= maxW - 0.05f)
                return;

            int primaryReserves = 0;
            int bandageStacks = 0;
            int throwableCount = 0;
            for (int i = 0; i < items.Count; i++)
            {
                VirtualItem vi = items[i];
                if (vi == null || vi.Pointer == IntPtr.Zero || vi.IsWearedItem() || vi.IsInstance())
                    continue;

                VirtualMagazineItem vm = vi.TryCast<VirtualMagazineItem>();
                VirtualAmmo va = vm == null ? vi.TryCast<VirtualAmmo>() : null;
                if (vm != null || va != null)
                {
                    if (g0 != null && DoesGunSupportAmmoOrMag(g0, vi.item_id, vm != null, null, out _))
                        primaryReserves++;
                    continue;
                }

                if (vi.TryCast<VirtualBandages>() != null)
                    bandageStacks++;
                else if (vi.TryCast<VirtualThrowable>() != null && !AiCore.IsPressureMineItem(vi, out _))
                    throwableCount++;
            }

            float targetSafeW = maxW - 0.05f;
            bool g0Loaded = g0 == null || g0.GetCurrentAmmoCount() > 0;

            // Pass C1: shed secondary/unknown reserve mags/ammo first, trim VirtualAmmo stacks, and trim >2 primary reserves
            PruneSurplusReserveAmmoPass(rawInv, items, g0, targetSafeW, 2, 5, false, g0Loaded, ref primaryReserves, ref curW);
            if (curW > targetSafeW)
                soldier.inventory.GetWeightAndMaxWeight(out curW, out maxW);

            // Pass C2: trim primary reserves down to 1 and shed duplicate (>1) bandages/throwables
            if (curW > targetSafeW)
            {
                PruneSurplusReserveAmmoPass(rawInv, items, g0, targetSafeW, 1, 0, false, g0Loaded, ref primaryReserves, ref curW);

                for (int i = items.Count - 1; i >= 0 && curW > targetSafeW; i--)
                {
                    VirtualItem vi = items[i];
                    if (vi == null || vi.Pointer == IntPtr.Zero || vi.IsWearedItem() || vi.IsInstance())
                        continue;

                    VirtualBandages vb = vi.TryCast<VirtualBandages>();
                    if (vb != null)
                    {
                        int bCnt = vb.GetStackCount();
                        float bMass = Mathf.Max(0f, vb.GetMass());
                        if (bandageStacks > 1)
                        {
                            rawInv.RemoveVirtualItem(vi);
                            curW -= bMass;
                            bandageStacks--;
                        }
                        else if (bCnt > 1 && bMass > 0.001f)
                        {
                            vb.SetStackCount(1);
                            curW -= (bCnt - 1) * (bMass / bCnt);
                        }
                        continue;
                    }

                    if (throwableCount > 1 && vi.TryCast<VirtualThrowable>() != null && !AiCore.IsPressureMineItem(vi, out _))
                    {
                        float tMass = Mathf.Max(0f, vi.GetMass());
                        rawInv.RemoveVirtualItem(vi);
                        curW -= tMass;
                        throwableCount--;
                    }
                }
            }

            if (curW > targetSafeW)
                soldier.inventory.GetWeightAndMaxWeight(out curW, out maxW);

            // Pass C3: final guarantee so curW <= maxW never locks player/bot sprinting
            if (curW > maxW)
            {
                PruneSurplusReserveAmmoPass(rawInv, items, g0, maxW - 0.02f, 1, 1, true, g0Loaded, ref primaryReserves, ref curW);
                for (int i = items.Count - 1; i >= 0 && curW > maxW - 0.02f && throwableCount > 0; i--)
                {
                    VirtualItem vi = items[i];
                    if (vi == null || vi.Pointer == IntPtr.Zero || vi.IsWearedItem() || vi.IsInstance())
                        continue;
                    if (vi.TryCast<VirtualThrowable>() != null && !AiCore.IsPressureMineItem(vi, out _))
                    {
                        float tMass = Mathf.Max(0f, vi.GetMass());
                        rawInv.RemoveVirtualItem(vi);
                        curW -= tMass;
                        throwableCount--;
                    }
                }
            }
        }

        internal static void TickBotInventoryWeightAndDelayedWeaponRefill(
            AiCore.SoldierFastHandles h,
            Soldier soldier,
            float now)
        {
            if (h == null || soldier == null || soldier.Pointer == IntPtr.Zero)
                return;

            if (h.NextInventoryWeightAuditTime <= 0f)
            {
                h.NextInventoryWeightAuditTime = now + 1.5f + (AiCore.GetDeterministicHash(h.Ptr, 937) & 31) * 0.20f;
            }

            if (h.PendingPickedWeaponSlot < 0 && now < h.NextInventoryWeightAuditTime)
                return;

            if (h.PendingPickedWeaponSlot >= 0 && now >= h.PendingPickedWeaponRefillTime)
            {
                if ((soldier.isInHandsAnimation || soldier.isInBodyAnimation) &&
                    now < h.PendingPickedWeaponRefillTime + 1.6f)
                {
                    h.NextInventoryWeightAuditTime = Mathf.Min(h.NextInventoryWeightAuditTime, now + 0.20f);
                    return;
                }

                int slot = h.PendingPickedWeaponSlot;
                h.PendingPickedWeaponSlot = -1;
                h.PendingPickedWeaponRefillTime = 0f;

                PruneBotInventoryForSprintWeight(soldier, purgeOrphanedCalibers: true);

                GenericGun pickedGun = soldier.GetHeldGun(slot);
                if (pickedGun != null &&
                    soldier.IsOutOfAmmosFor(pickedGun) &&
                    CanSoldierAcceptExtraMass(soldier, 0.35f, 0.15f))
                {
                    int qnt = pickedGun.IsAtWeapon() ? 1 : 2;
                    soldier.RefillAmmoForWearedWeapon(slot, true, null, true, qnt);
                }

                h.NextInventoryWeightAuditTime = now + 0.75f;
                return;
            }

            if (now >= h.NextInventoryWeightAuditTime)
            {
                if (soldier.IsAmmoRefillPending())
                {
                    h.NextInventoryWeightAuditTime = now + 0.45f;
                    return;
                }

                h.NextInventoryWeightAuditTime = now + 9.5f + (AiCore.GetDeterministicHash(h.Ptr, 941) & 15) * 0.25f;

                if (soldier.inventory != null)
                {
                    soldier.inventory.GetWeightAndMaxWeight(out float curW, out float maxW);
                    if (maxW > 0f && curW > maxW - 0.02f)
                    {
                        PruneBotInventoryForSprintWeight(soldier, purgeOrphanedCalibers: true);
                    }
                }
            }
        }

        private static void RefillSoldierPrimaryAndStowedWeapons(Soldier soldier, int minRefillQnt)
        {
            if (soldier == null || soldier.Pointer == IntPtr.Zero || soldier.inventory == null)
                return;

            soldier.inventory.RemoveEmptyAmmoAndMagazines();
            soldier.inventory.GetWeightAndMaxWeight(out float curW, out float maxW);
            if (maxW > 0f && curW >= maxW - 0.30f)
            {
                ScheduleBotInventoryWeightAudit(soldier, 0.25f);
                return;
            }

            float headroomKg = maxW > 0f ? (maxW - curW) : 99.0f;
            bool startedAny = false;

            GenericGun g0 = soldier.GetHeldGun(0);
            if (g0 != null && soldier.IsOutOfAmmosFor(g0))
            {
                int q0 = (g0.IsAtWeapon() || headroomKg < 2.2f)
                    ? 1
                    : Mathf.Min(minRefillQnt, (g0.integratedBipod != null || g0.IsHeavyMg()) ? 2 : minRefillQnt);
                soldier.RefillAmmoForWearedWeapon(0, true, null, true, q0);
                startedAny = true;
            }

            // In ER2 (Soldier.SwitchTo @ 0x18076AD20), when a soldier switches to their sidearm (Slot 2),
            // their primary weapon is swapped into Slot 2 (or Slot 1). Only refill stowed slots that are genuinely dry!
            GenericGun g1 = soldier.GetHeldGun(1);
            if (g1 != null && !g1.IsPistol() && soldier.IsOutOfAmmosFor(g1) && headroomKg >= 1.0f)
            {
                int q1 = g1.IsAtWeapon() ? 1 : Mathf.Min(minRefillQnt, headroomKg < 2.5f ? 1 : 2);
                soldier.RefillAmmoForWearedWeapon(1, true, null, true, q1);
                startedAny = true;
            }

            GenericGun g2 = soldier.GetHeldGun(2);
            if (g2 != null && !g2.IsPistol())
            {
                if (soldier.IsOutOfAmmosFor(g2) && headroomKg >= 0.8f)
                {
                    int q2 = g2.IsAtWeapon() ? 1 : Mathf.Min(minRefillQnt, headroomKg < 2.2f ? 1 : 2);
                    soldier.RefillAmmoForWearedWeapon(2, true, null, true, q2);
                    startedAny = true;
                }

                // If currently holding a pistol in Slot 0 because the primary in Slot 2 was dry, switch back to primary!
                if (g0 != null &&
                    g0.IsPistol() &&
                    (!soldier.IsOutOfAmmosFor(g2) || g2.GetCurrentAmmoCount() > 0) &&
                    !soldier.isInHandsAnimation &&
                    !soldier.isInBodyAnimation &&
                    soldier.CanSwitchHeldItem)
                {
                    soldier.SwitchTo(2);
                }
            }

            if (startedAny)
            {
                ScheduleBotInventoryWeightAudit(soldier, 0.70f);
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

            AiCore.SoldierFastHandles boxH = AiCore.GetOrCreateSoldierHandles(soldier);
            bool anyRefilled = false;

            // Bot-side cooldown keeps human player's nextAvailableUse (30s UI lockout) and stackCount untouched!
            if (ovInfo.IsDefaultWeaponAmmo || overrideContent == null || overrideContent.Length == 0)
            {
                RefillSoldierPrimaryAndStowedWeapons(soldier, 2);
                if (boxH != null)
                    boxH.Goap.NextBotBoxOrCrateResupplyTime = now + 35.0f;
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
                        (soldier.GetHeldGun(1) != null || CanSoldierAcceptExtraMass(soldier, Mathf.Max(0f, gunPrefab.mass) * 0.65f, 0.0f)) &&
                        (!Photon.Pun.PhotonNetwork.InRoom || AiCore.TryConsumeBotRpcBudget(0.35f)))
                    {
                        GameObject instGo = UnityEngine.Object.Instantiate(gunPrefab.gameObject);
                        if (instGo != null)
                        {
                            soldier.SetGunTriggerPressed(false);
                            soldier.PickUpItem(instGo, 1);
                            ScheduleDelayedPickedWeaponRefill(soldier, 1, now);
                            _nextGlobalBotWeaponPickupTime = now + 2.20f;
                            if (boxH != null)
                                boxH.Goap.NextWeaponUpgradeAllowedTime = now + 20.0f;
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
                    soldier.inventory.FindItemOfType<VirtualATGrenade>() == null &&
                    CanSoldierAcceptExtraMass(soldier, vi.GetMass(), 0.25f))
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
                         soldier.inventory.FindItemOfType<VirtualGrenade>() == null &&
                         CanSoldierAcceptExtraMass(soldier, vi.GetMass(), 0.25f))
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
                         soldier.inventory.FindItemOfType<VirtualSmokeGrenade>() == null &&
                         CanSoldierAcceptExtraMass(soldier, vi.GetMass(), 0.25f))
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
                         soldier.inventory.FindItemOfType<VirtualBandages>() == null &&
                         CanSoldierAcceptExtraMass(soldier, vi.GetMass(), soldier.isBleeding ? 0.0f : 0.25f))
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
                if (boxH != null)
                    boxH.Goap.NextBotBoxOrCrateResupplyTime = now + 35.0f;
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

            AiCore.SoldierFastHandles upgH = AiCore.GetOrCreateSoldierHandles(soldier);
            if (upgH != null && now < upgH.Goap.NextWeaponUpgradeAllowedTime)
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

        internal static bool HasPressureAtMineInInventory(Soldier s)
        {
            if (s == null || s.inventory == null || s.inventory.inventory == null)
                return false;

            var items = s.inventory.inventory.items;
            if (items == null)
                return false;

            int count = items.Count;
            for (int i = 0; i < count; i++)
            {
                VirtualItem vi = items[i];
                if (vi != null && AiCore.IsPressureMineItem(vi, out bool isAt) && isAt)
                    return true;
            }
            return false;
        }

        private static bool IsScavengePositionSafeFromThreat(
            Vector3 myPos,
            Vector3 targetPos,
            bool hasBoard,
            ref SquadBlackboard board,
            float now)
        {
            if (!hasBoard)
                return true;

            // 1. Fresh Casualty Kill-Zone Guard: never walk onto a spot where a squadmate was killed in the last 16s!
            if (board.LastCasualtyTime > 0f &&
                (now - board.LastCasualtyTime) <= 16.0f &&
                (targetPos - board.LastCasualtyPos).sqrMagnitude <= 14.0f * 14.0f)
            {
                return false;
            }

            // 2. Active Threat Standoff & Exposure Guard: never walk within 28m of the enemy threat or forward in front of our cover line!
            if (board.HasActiveThreat)
            {
                if ((targetPos - board.ThreatPosition).sqrMagnitude < 28.0f * 28.0f)
                    return false;

                float dFromMeSq = (targetPos - myPos).sqrMagnitude;
                if (dFromMeSq > 3.3f * 3.3f && Vector3.Dot(targetPos - myPos, board.ThreatDirection) > 1.5f)
                    return false;
            }

            return true;
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
            bool wantToolbox,
            bool wantAtMine,
            bool wantDeployableTripod,
            bool wantShellAmmo,
            bool wantRecoverLife,
            float now,
            out BotScavengeCache result)
        {
            IntPtr ptr = soldier.Pointer;
            AiCore.SoldierFastHandles scH = AiCore.GetOrCreateSoldierHandles(soldier);
            IntPtr mySqPtrRadio = (soldier.joinedSquad != null) ? soldier.joinedSquad.Pointer : IntPtr.Zero;
            SquadBlackboard scBoard = default;
            bool hasBoard = mySqPtrRadio != IntPtr.Zero && _squadBlackboards.TryGetValue(mySqPtrRadio, out scBoard);
            bool onlyUrgentAt = needAtWeapon && !needAmmo && !needBandage && !wantFragGrenade && !wantSmokeGrenade && !needUrgentPrimary && !wantSquadMg && !wantRoleUpgrade && !wantRadioPack && !wantMedicSyringe && !wantHelmet && !canFeedNeedyAlly;
            bool onlyUrgentBandage = needBandage && !needAmmo && !wantFragGrenade && !wantSmokeGrenade && !needAtWeapon && !needUrgentPrimary && !wantSquadMg && !wantRoleUpgrade && !wantRadioPack && !wantMedicSyringe && !wantHelmet && !canFeedNeedyAlly;
            result = scH != null ? scH.Goap.Scavenge : default;
            if (scH != null &&
                now < result.NextScanTime &&
                (!onlyUrgentAt || result.IsAtWeaponPickup) &&
                (!onlyUrgentBandage || result.IsBuddyBandageRefill || result.NeedBandage) &&
                (!result.IsRadioPackPickup || wantRadioPack))
            {
                if ((result.IsBuddyBandageRefill || result.IsBuddyAmmoBorrow) &&
                    result.TargetBuddyDonor != null &&
                    result.TargetBuddyDonor.Pointer != IntPtr.Zero &&
                    result.TargetBuddyDonor.IsAlive &&
                    !result.TargetBuddyDonor.IsInjured &&
                    result.TargetBuddyDonor.transform != null)
                {
                    Vector3 donorPos = result.TargetBuddyDonor.transform.position;
                    if (IsScavengePositionSafeFromThreat(myPos, donorPos, hasBoard, ref scBoard, now))
                    {
                        result.TargetPosition = donorPos;
                        return true;
                    }
                }
                else if (result.IsFeedingNeedyAlly &&
                         result.TargetNeedyAllyToFeed != null &&
                         result.TargetNeedyAllyToFeed.Pointer != IntPtr.Zero &&
                         result.TargetNeedyAllyToFeed.IsAlive &&
                         !result.TargetNeedyAllyToFeed.IsInjured &&
                         result.TargetNeedyAllyToFeed.transform != null)
                {
                    Vector3 allyPos = result.TargetNeedyAllyToFeed.transform.position;
                    if (IsScavengePositionSafeFromThreat(myPos, allyPos, hasBoard, ref scBoard, now))
                    {
                        result.TargetPosition = allyPos;
                        return true;
                    }
                }
                else if (result.TargetFloorItem != null)
                {
                    if (IsFloorItemFreeToPickUp(result.TargetFloorItem) &&
                        !IsScavengeTargetClaimedByOtherBot(ptr, result.TargetFloorItem.Pointer, now))
                    {
                        Vector3 fPos = result.TargetFloorItem.transform.position;
                        if (IsScavengePositionSafeFromThreat(myPos, fPos, hasBoard, ref scBoard, now))
                        {
                            result.TargetPosition = fPos;
                            return true;
                        }
                    }
                    // Someone else already picked up/claimed the floor item or it is now in a kill-zone; rescan!
                }
                else if (result.TargetRefillCrate != null)
                {
                    if (result.TargetRefillCrate.Pointer != IntPtr.Zero &&
                        result.TargetRefillCrate.gameObject != null &&
                        result.TargetRefillCrate.gameObject.activeInHierarchy &&
                        result.TargetRefillCrate.transform != null &&
                        !IsScavengeTargetClaimedByOtherBot(ptr, result.TargetRefillCrate.Pointer, now))
                    {
                        Vector3 crPos = result.TargetRefillCrate.transform.position;
                        if (IsScavengePositionSafeFromThreat(myPos, crPos, hasBoard, ref scBoard, now))
                        {
                            result.TargetPosition = crPos;
                            return true;
                        }
                    }
                }
                else if (result.TargetInventory != null &&
                         IsInventoryValidCorpseOrCrate(result.TargetInventory, soldier.faction, result.NeedAmmo || result.NeedFragGrenade || result.NeedSmokeGrenade, result.NeedBandage || result.IsMedicSyringePickup, out _, out _) &&
                         !IsScavengeTargetClaimedByOtherBot(ptr, result.TargetInventory.Pointer, now))
                {
                    Vector3 invPos = result.TargetInventory.InventoryPosition;
                    if (IsScavengePositionSafeFromThreat(myPos, invPos, hasBoard, ref scBoard, now))
                    {
                        result.TargetPosition = invPos;
                        return true;
                    }
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
            if (soldier.IsSquadLeader() && !soldier.isBleeding && soldier.joinedSquad != null &&
                soldier.joinedSquad.units != null && soldier.joinedSquad.units.Count >= 2)
            {
                maxSearchRadius = Mathf.Min(maxSearchRadius, needAmmo ? 12.0f : 10.0f);
            }
            float bestDistSq = maxSearchRadius * maxSearchRadius;
            float lastRadLostTime = 0f;
            float soldierSuppForRadio = 0f;
            if (wantRadioPack)
            {
                if (hasBoard)
                    lastRadLostTime = scBoard.LastRadiomanLostTime;
                soldierSuppForRadio = soldier.GetSuppressionValue();
            }
            float nextWepT = scH != null ? scH.Goap.NextWeaponUpgradeAllowedTime : 0f;
            float nextBoxT = scH != null ? scH.Goap.NextBotBoxOrCrateResupplyTime : 0f;
            bool canPickUpNewWeapon = (now >= nextWepT || nextWepT - now > 120.0f);
            bool canUseBoxOrCrate = (now >= nextBoxT || nextBoxT - now > 45.0f);

            // 0. Scan living friendly buddies in AiCore.WorldSoldiers for Buddy Resupply (Bandage from Medic, Ammo from Carrier, or Feeding a needy MG/AT squadmate)
            if (VisionConfig.EnableBuddyResupply.Value && (needBandage || needAmmo || canFeedNeedyAlly))
            {
                AiCore.EnsureWorldSnapshot();
                var wSoldiers = AiCore.WorldSoldiers;
                int wCount = AiCore.WorldSoldierCount;
                string myFaction = soldier.faction;
                IntPtr mySqPtr = soldier.joinedSquad != null ? soldier.joinedSquad.Pointer : IntPtr.Zero;
                bool canRefillBandageFromMedic = needBandage &&
                                                 (scH == null || now >= scH.Goap.NextBuddyBandageRefillTime);
                bool canResupplyBuddyAmmo = (needAmmo || canFeedNeedyAlly) &&
                                            (scH == null || now >= scH.Goap.NextBuddyAmmoResupplyTime);

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

                        if (!IsScavengePositionSafeFromThreat(myPos, sSnap.Pos, hasBoard, ref scBoard, now) ||
                            IsScavengeTargetClaimedByOtherBot(ptr, sSnap.Ptr, now))
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
                            if (sSnap.Handles != null && now < sSnap.Handles.Goap.NextBuddyAmmoResupplyTime)
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
                if (!IsScavengePositionSafeFromThreat(myPos, itemPos, hasBoard, ref scBoard, now) ||
                    !IsFloorItemFreeToPickUp(item) ||
                    IsScavengeTargetClaimedByOtherBot(ptr, item.Pointer, now))
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
                            if (needAtWeapon &&
                                fEntry.Gun.IsAtWeapon() &&
                                fEntry.Gun.GetCurrentAmmoCount() > 0 &&
                                (soldier.GetHeldGun(1) != null || CanSoldierAcceptExtraMass(soldier, Mathf.Max(0f, item.mass) * 0.5f, 0.0f)))
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
                        if (wantRadioPack && dSq <= 12.0f * 12.0f)
                        {
                            bestDistSq = dSq;
                            result = new BotScavengeCache
                            {
                                NextScanTime = now + 2.8f,
                                NeedAmmo = needAmmo,
                                NeedBandage = needBandage,
                                TargetFloorItem = item,
                                TargetPosition = itemPos,
                                IsRadioPackPickup = true,
                                ClaimingSquadPtr = mySqPtrRadio
                            };
                        }
                        break;

                    case FloorItemKind.Ammo:
                        if (needAmmo &&
                            CanSoldierAcceptExtraMass(soldier, item.mass, 0.20f) &&
                            IsAmmoOrMagCompatibleWithAnyHeldGun(soldier, item.item_id, isMagazine: false))
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

                    case FloorItemKind.Magazine:
                        if (needAmmo &&
                            CanSoldierAcceptExtraMass(soldier, item.mass, 0.20f) &&
                            fEntry.Mag != null &&
                            fEntry.Mag.GetCurrentAmmoCount() > 0 &&
                            IsAmmoOrMagCompatibleWithAnyHeldGun(soldier, item.item_id, isMagazine: true, fEntry.Mag.socket))
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
                        if (needBandage && (soldier.isBleeding || CanSoldierAcceptExtraMass(soldier, item.mass, 0.20f)))
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
                        if (needAtWeapon && CanSoldierAcceptExtraMass(soldier, item.mass, 0.20f))
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
                        if (wantFragGrenade && CanSoldierAcceptExtraMass(soldier, item.mass, 0.20f))
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
                        if (wantSmokeGrenade && CanSoldierAcceptExtraMass(soldier, item.mass, 0.20f))
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
                        if (wantMedicSyringe &&
                            !IsFriendlyPlayerWithinDistance(soldier, itemPos, 25.0f) &&
                            (soldier.inventory == null ||
                             soldier.inventory.inventory == null ||
                             string.IsNullOrEmpty(item.item_id) ||
                             soldier.inventory.inventory.FindItemWithID(item.item_id) == null))
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

                    case FloorItemKind.AtMine:
                        if ((wantAtMine || (needAtWeapon && AiCore.AnyEnemyGroundVehiclesPresent(soldier.faction))) &&
                            CanSoldierAcceptExtraMass(soldier, item.mass, 0.20f))
                        {
                            bestDistSq = dSq;
                            result = new BotScavengeCache
                            {
                                NextScanTime = now + 2.8f,
                                NeedAmmo = needAmmo,
                                NeedBandage = needBandage,
                                TargetFloorItem = item,
                                TargetPosition = itemPos,
                                IsAtMinePickup = true
                            };
                        }
                        break;

                    case FloorItemKind.DeployableTripod:
                        if (wantDeployableTripod &&
                            !IsFriendlyPlayerWithinDistance(soldier, itemPos, 20.0f) &&
                            CanSoldierAcceptExtraMass(soldier, item.mass, 0.15f))
                        {
                            bestDistSq = dSq;
                            result = new BotScavengeCache
                            {
                                NextScanTime = now + 2.8f,
                                NeedAmmo = needAmmo,
                                NeedBandage = needBandage,
                                TargetFloorItem = item,
                                TargetPosition = itemPos,
                                IsDeployableTripodPickup = true
                            };
                        }
                        break;

                    case FloorItemKind.ShellAmmo:
                        if (wantShellAmmo && CanSoldierAcceptExtraMass(soldier, item.mass, 0.20f))
                        {
                            bestDistSq = dSq;
                            result = new BotScavengeCache
                            {
                                NextScanTime = now + 2.8f,
                                NeedAmmo = needAmmo,
                                NeedBandage = needBandage,
                                TargetFloorItem = item,
                                TargetPosition = itemPos,
                                IsShellAmmoPickup = true
                            };
                        }
                        break;

                    case FloorItemKind.Toolbox:
                        if (wantToolbox &&
                            !IsFriendlyPlayerWithinDistance(soldier, itemPos, 22.0f) &&
                            CanSoldierAcceptExtraMass(soldier, item.mass, 0.10f))
                        {
                            bestDistSq = dSq;
                            result = new BotScavengeCache
                            {
                                NextScanTime = now + 2.8f,
                                NeedAmmo = needAmmo,
                                NeedBandage = needBandage,
                                TargetFloorItem = item,
                                TargetPosition = itemPos,
                                IsToolboxPickup = true
                            };
                        }
                        break;

                    case FloorItemKind.RecoverLife:
                        if (wantRecoverLife && dSq <= 10.5f * 10.5f && !IsFriendlyPlayerWithinDistance(soldier, itemPos, 16.0f))
                        {
                            bestDistSq = dSq;
                            result = new BotScavengeCache
                            {
                                NextScanTime = now + 2.8f,
                                NeedAmmo = needAmmo,
                                NeedBandage = needBandage,
                                TargetFloorItem = item,
                                TargetPosition = itemPos,
                                IsRecoverLifePickup = true
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
                    if (dSq >= bestDistSq ||
                        !IsScavengePositionSafeFromThreat(myPos, cPos, hasBoard, ref scBoard, now) ||
                        IsScavengeTargetClaimedByOtherBot(ptr, crate.Pointer, now))
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

                    if (!IsScavengePositionSafeFromThreat(myPos, invPos, hasBoard, ref scBoard, now) ||
                        IsScavengeTargetClaimedByOtherBot(ptr, sEntry.Ptr, now))
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
                            bool isFriendlyCorpse = owner == null ||
                                (!string.IsNullOrEmpty(sEntry.OwnerFaction) && AiCore.AreSameSideFactions(myFac, sEntry.OwnerFaction));
                            bool canClaimThisRadio = isFriendlyCorpse
                                ? (dSq <= 14.0f * 14.0f)
                                : (dSq <= 10.0f * 10.0f &&
                                   soldierSuppForRadio < 8.0f &&
                                   (lastRadLostTime <= 0f || (now - lastRadLostTime) >= 20.0f));
                            if (canClaimThisRadio)
                            {
                                // Uniforms (isRadio == false) sit at index 0 in VirtualClothing lists; iterate all items to find the radio pack!
                                VirtualItem candRadio = null;
                                var clothItems = inv.inventory != null ? inv.inventory.items : null;
                                if (clothItems != null)
                                {
                                    int cLim = Mathf.Min(clothItems.Count, 24);
                                    for (int k = 0; k < cLim; k++)
                                    {
                                        VirtualItem ci = clothItems[k];
                                        if (ci != null &&
                                            ci.TryCast<VirtualClothing>() != null &&
                                            TryGetVirtualWearableInfo(ci, out CachedWearableInfo wInfo) &&
                                            wInfo.IsRadio)
                                        {
                                            candRadio = ci;
                                            break;
                                        }
                                    }
                                }

                                if (candRadio != null)
                                {
                                    bestDistSq = dSq;
                                    result = new BotScavengeCache
                                    {
                                        NextScanTime = now + 2.8f,
                                        NeedAmmo = needAmmo,
                                        NeedBandage = needBandage,
                                        TargetInventory = inv,
                                        TargetInventoryWearable = candRadio,
                                        TargetPosition = invPos,
                                        IsRadioPackPickup = true,
                                        IsEnemyRadioCapture = (owner != null && !isFriendlyCorpse),
                                        ClaimingSquadPtr = mySqPtrRadio
                                    };
                                    continue;
                                }
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
                            if (atGren != null && !AiCore.IsPressureMineItem(atGren, out _))
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

                    if (wantMedicSyringe &&
                        isDeadBodyOrCrate &&
                        !IsFriendlyPlayerWithinDistance(soldier, invPos, 25.0f) &&
                        CanTransferFullMedicKit(soldier.inventory, inv, out _, out _, out _))
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

                    VirtualItem fgCand = wantFragGrenade ? inv.FindItemOfType<VirtualGrenade>() : null;
                    bool hasFragInInv = fgCand != null && !AiCore.IsPressureMineItem(fgCand, out _);
                    bool hasSmokeInInv = wantSmokeGrenade && inv.FindItemOfType<VirtualSmokeGrenade>() != null;
                    bool hasBandageInInv = needBandage && inv.FindItemOfType<VirtualBandages>() != null;
                    bool hasAmmoInInv = needAmmo &&
                                        (owner != null ||
                                         inv.FindItemOfType<VirtualAmmo>() != null ||
                                         inv.FindItemOfType<VirtualMagazineItem>() != null);

                    bool hasAtMineInInv = false;
                    if (wantAtMine)
                    {
                        VirtualItem candMine = inv.FindItemOfType<VirtualATGrenade>();
                        if (candMine != null && AiCore.IsPressureMineItem(candMine, out bool atMineFlag) && atMineFlag)
                            hasAtMineInInv = true;
                    }
                    bool hasTripodInInv = wantDeployableTripod &&
                                          isDeadBodyOrCrate &&
                                          !IsFriendlyPlayerWithinDistance(soldier, invPos, 20.0f) &&
                                          inv.FindItemOfType<VirtualDeployableVehicle>() != null;
                    bool hasShellInInv = wantShellAmmo && inv.FindItemOfType<VirtualShellAmmo>() != null;
                    bool hasToolboxInInv = wantToolbox &&
                                           isDeadBodyOrCrate &&
                                           !IsFriendlyPlayerWithinDistance(soldier, invPos, 22.0f) &&
                                           inv.FindItemOfType<VirtualToolbox>() != null;
                    bool hasRecoverLifeInInv = wantRecoverLife &&
                                               dSq <= 10.5f * 10.5f &&
                                               !IsFriendlyPlayerWithinDistance(soldier, invPos, 16.0f) &&
                                               inv.FindItemOfType<VirtualRecoverLife>() != null;

                    if (!hasAmmoInInv && !hasBandageInInv && !hasFragInInv && !hasSmokeInInv &&
                        !hasAtMineInInv && !hasTripodInInv && !hasShellInInv && !hasToolboxInInv && !hasRecoverLifeInInv)
                        continue;

                    bestDistSq = dSq;
                    result = new BotScavengeCache
                    {
                        NextScanTime = now + 2.8f,
                        NeedAmmo = hasAmmoInInv,
                        NeedBandage = hasBandageInInv,
                        NeedFragGrenade = hasFragInInv,
                        NeedSmokeGrenade = hasSmokeInInv,
                        IsAtMinePickup = hasAtMineInInv,
                        IsDeployableTripodPickup = hasTripodInInv,
                        IsShellAmmoPickup = hasShellInInv,
                        IsToolboxPickup = hasToolboxInInv,
                        IsRecoverLifePickup = hasRecoverLifeInInv,
                        TargetInventory = inv,
                        TargetPosition = invPos
                    };
                }
            }

            result.ClaimedTargetPtr =
                result.TargetFloorItem != null ? result.TargetFloorItem.Pointer :
                result.TargetRefillCrate != null ? result.TargetRefillCrate.Pointer :
                result.TargetInventory != null ? result.TargetInventory.Pointer :
                result.TargetBuddyDonor != null ? result.TargetBuddyDonor.Pointer :
                result.TargetNeedyAllyToFeed != null ? result.TargetNeedyAllyToFeed.Pointer :
                IntPtr.Zero;
            if (scH != null)
                scH.Goap.Scavenge = result;
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
            string myFaction = AiCore.GetCachedFaction(self);

            for (int i = 0; i < wCount; i++)
            {
                ref AiCore.SoldierSnapshot sSnap = ref wSoldiers[i];
                if (!sSnap.IsPlayer || !AiCore.AreSameSideFactions(myFaction, sSnap.Faction))
                    continue;
                if ((sSnap.Pos - myPos).sqrMagnitude <= maxSq)
                    return true;
            }
            return false;
        }

        private static int CountUniqueSyringesInInventory(
            InventoryManager invMgr,
            out VirtualItem s0,
            out VirtualItem s1,
            out VirtualItem s2)
        {
            s0 = null;
            s1 = null;
            s2 = null;

            Inventory inv = invMgr != null ? invMgr.inventory : null;
            var items = inv != null ? inv.items : null;
            if (items == null)
                return 0;

            int uniqueCount = 0;
            string id0 = null;
            string id1 = null;
            int total = items.Count;
            for (int i = 0; i < total; i++)
            {
                VirtualItem vi = items[i];
                if (vi == null || vi.Pointer == IntPtr.Zero || vi.TryCast<VirtualSyringe>() == null)
                    continue;

                string id = vi.item_id;
                if (string.IsNullOrEmpty(id))
                    continue;

                if (uniqueCount == 0)
                {
                    s0 = vi;
                    id0 = id;
                    uniqueCount = 1;
                }
                else if (uniqueCount == 1)
                {
                    if (string.Equals(id, id0, StringComparison.Ordinal))
                        continue;
                    s1 = vi;
                    id1 = id;
                    uniqueCount = 2;
                }
                else
                {
                    if (string.Equals(id, id0, StringComparison.Ordinal) ||
                        string.Equals(id, id1, StringComparison.Ordinal))
                    {
                        continue;
                    }
                    s2 = vi;
                    uniqueCount = 3;
                    break;
                }
            }

            return uniqueCount;
        }

        private static bool CanTransferFullMedicKit(
            InventoryManager dstInv,
            InventoryManager srcInv,
            out VirtualItem s0,
            out VirtualItem s1,
            out VirtualItem s2)
        {
            s0 = null;
            s1 = null;
            s2 = null;

            if (dstInv == null || srcInv == null || dstInv.inventory == null)
                return false;

            // Every Medic carries 3 distinct VirtualSyringe tools (InjureType 0, 1, 2).
            // Never scavenge from an incomplete 1-of-3 or 2-of-3 corpse kit!
            if (CountUniqueSyringesInInventory(srcInv, out s0, out s1, out s2) < 3)
                return false;

            float neededMass = 0f;
            bool needAny = false;
            if (dstInv.inventory.FindItemWithID(s0.item_id) == null)
            {
                neededMass += Mathf.Max(0f, s0.GetMass());
                needAny = true;
            }
            if (dstInv.inventory.FindItemWithID(s1.item_id) == null)
            {
                neededMass += Mathf.Max(0f, s1.GetMass());
                needAny = true;
            }
            if (dstInv.inventory.FindItemWithID(s2.item_id) == null)
            {
                neededMass += Mathf.Max(0f, s2.GetMass());
                needAny = true;
            }

            if (!needAny)
                return false;

            dstInv.GetWeightAndMaxWeight(out float curW, out float maxW);
            if (maxW > 0f && curW + neededMass > maxW - 0.01f)
                return false;

            return dstInv.HasSpaceFor(s0);
        }

        private static bool TryTransferFullMedicKitAtomically(Soldier soldier, InventoryManager srcInv)
        {
            InventoryManager dstInv = soldier != null ? soldier.inventory : null;
            if (!CanTransferFullMedicKit(dstInv, srcInv, out VirtualItem s0, out VirtualItem s1, out VirtualItem s2))
                return false;

            if (Photon.Pun.PhotonNetwork.InRoom && !AiCore.TryConsumeBotRpcBudget(0.35f))
                return false;

            string pre0 = dstInv.inventory.FindItemWithID(s0.item_id) != null ? s0.item_id : null;
            string pre1 = dstInv.inventory.FindItemWithID(s1.item_id) != null ? s1.item_id : null;
            string pre2 = dstInv.inventory.FindItemWithID(s2.item_id) != null ? s2.item_id : null;

            var batch = new Il2CppSystem.Collections.Generic.List<VirtualItem>();
            if (pre0 == null) batch.Add(s0);
            if (pre1 == null) batch.Add(s1);
            if (pre2 == null) batch.Add(s2);

            if (batch.Count == 0)
                return false;

            dstInv.TakeIntoInventoryBatch(batch.Cast<Il2CppSystem.Collections.Generic.IList<VirtualItem>>(), srcInv);

            if (CountUniqueSyringesInInventory(dstInv, out _, out _, out _) >= 3)
            {
                soldier.hasSyringe = true;
                return true;
            }

            // Safety rollback: never leave a dead Medic with a partial 1-of-3 or 2-of-3 tool kit!
            var rollback = new Il2CppSystem.Collections.Generic.List<VirtualItem>();
            if (pre0 == null)
            {
                VirtualItem moved0 = dstInv.inventory.FindItemWithID(s0.item_id);
                if (moved0 != null) rollback.Add(moved0);
            }
            if (pre1 == null)
            {
                VirtualItem moved1 = dstInv.inventory.FindItemWithID(s1.item_id);
                if (moved1 != null) rollback.Add(moved1);
            }
            if (pre2 == null)
            {
                VirtualItem moved2 = dstInv.inventory.FindItemWithID(s2.item_id);
                if (moved2 != null) rollback.Add(moved2);
            }
            if (rollback.Count > 0)
            {
                srcInv.TakeIntoInventoryBatch(rollback.Cast<Il2CppSystem.Collections.Generic.IList<VirtualItem>>(), dstInv);
            }
            soldier.hasSyringe = CountUniqueSyringesInInventory(dstInv, out _, out _, out _) >= 3;
            return false;
        }
    }
}
