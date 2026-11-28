using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace EasyRed2HumanAI
{
    public static partial class VeteranAndTacticsPatches
    {
        internal struct DamageAttribution
        {
            public bool IsFriendlyFire;
            public bool IsSelfInflicted;
            public string WeaponOrSource;
            public string HitDetail;
            public int DistanceMeters;
            public float Timestamp;
            public bool CausedBleeding;
        }

        private const byte DeathAttributionPhotonEventCode = 197;
        private static readonly Dictionary<IntPtr, DamageAttribution> _lastDamageByVictim = new Dictionary<IntPtr, DamageAttribution>(32);
        private static readonly Dictionary<int, DamageAttribution> _pendingRemoteDeathByViewId = new Dictionary<int, DamageAttribution>(16);
        private static readonly Dictionary<IntPtr, float> _lastRemoteAttrSendTimeByPtr = new Dictionary<IntPtr, float>(16);
        private static readonly List<IntPtr> _reusableExpiredAttrKeys = new List<IntPtr>(32);
        private static readonly List<int> _reusableExpiredViewIds = new List<int>(16);

        internal static void PruneExpiredDamageAttributions(float now, bool forceClear = false)
        {
            if (forceClear)
            {
                _lastDamageByVictim.Clear();
                _pendingRemoteDeathByViewId.Clear();
                _lastRemoteAttrSendTimeByPtr.Clear();
                if (_expPreStateTop > 0)
                    Array.Clear(_expPreStates, 0, _expPreStateTop);
                _expPreStateTop = 0;
                _deathPanelVictimViewId = 0;
                _deathPanelVictimRef = null;
                _deathPanelBaseYearLine = null;
                _deathPanelVanillaReason = null;
                return;
            }

            if (_lastDamageByVictim.Count > 0)
            {
                _reusableExpiredAttrKeys.Clear();
                foreach (var kv in _lastDamageByVictim)
                {
                    if (now - kv.Value.Timestamp > 90.0f || now < kv.Value.Timestamp)
                        _reusableExpiredAttrKeys.Add(kv.Key);
                }

                for (int i = 0; i < _reusableExpiredAttrKeys.Count; i++)
                {
                    IntPtr expPtr = _reusableExpiredAttrKeys[i];
                    _lastDamageByVictim.Remove(expPtr);
                    _lastRemoteAttrSendTimeByPtr.Remove(expPtr);
                }
            }

            if (_pendingRemoteDeathByViewId.Count > 0)
            {
                _reusableExpiredViewIds.Clear();
                foreach (var kv in _pendingRemoteDeathByViewId)
                {
                    if (now - kv.Value.Timestamp > 90.0f || now < kv.Value.Timestamp)
                        _reusableExpiredViewIds.Add(kv.Key);
                }

                for (int i = 0; i < _reusableExpiredViewIds.Count; i++)
                {
                    _pendingRemoteDeathByViewId.Remove(_reusableExpiredViewIds[i]);
                }
            }
        }
        private static string _activeExplosionWeaponName;
        private static bool _activeExplosionIsArtillery;
        private static Vector3 _activeExplosionPos;
        private static float _activeExplosionRadius;
        private static Soldier _activeExplosionAttacker;
        private static bool _activeImpactHasContext;
        private static BulletData _activeImpactBulletData;
        private static bool _activeImpactIsMortar;
        private static Vector3 _activeImpactStartPos;
        private static Vector3 _activeImpactDirection;
        private static IntPtr _activeDamageAttackerPtr;

        private struct ExplosionPlayerPreState
        {
            public Soldier Player;
            public IntPtr Ptr;
            public int PreLife;
            public bool PreAlive;
            public bool PreInjured;
            public bool PreBleeding;
            public bool HitPartRecorded;
        }

        private static ExplosionPlayerPreState[] _expPreStates = new ExplosionPlayerPreState[32];
        private static int _expPreStateTop;

        public struct ExplosionPatchState
        {
            public IntPtr PrevAttackerPtr;
            public string PrevWeaponName;
            public bool PrevIsArtillery;
            public Vector3 PrevPos;
            public float PrevRadius;
            public Soldier PrevExpAttacker;
            public int SnapStart;
            public int SnapCount;
        }

        public static void BeginProjectileImpactContext(Corvostudio.Weapons.BulletInstance inst, BulletData bulletData, Vector3 hitDirection = default)
        {
            if (inst == null && bulletData == null)
            {
                _activeImpactHasContext = false;
                return;
            }

            _activeImpactHasContext = true;
            _activeImpactBulletData = bulletData ?? (inst != null ? inst.bulletData : null);
            _activeImpactIsMortar = inst != null && inst.isMortar;
            _activeImpactStartPos = inst != null ? inst.startPosition : Vector3.zero;
            _activeImpactDirection = hitDirection.sqrMagnitude > 0.0001f ? hitDirection.normalized : Vector3.zero;
        }

        public static void EndProjectileImpactContext()
        {
            _activeImpactHasContext = false;
            _activeImpactBulletData = null;
            _activeImpactIsMortar = false;
            _activeImpactDirection = Vector3.zero;
        }

        // Active Death Screen Obituary Banner state (rendered on both DeathPanel and OnGUI overlay)
        public static string ActiveDeathAttributionText;
        public static bool ActiveDeathAttributionIsFF;
        public static bool ActiveDeathAttributionIsSelf;
        public static float ActiveDeathAttributionStartTime;
        public static float ActiveDeathAttributionExpireTime;
        public static IntPtr ActiveDeathAttributionVictimPtr;

        // Instant DeathPanel visibility state (bypasses staggered legacy Animation fade-in)
        private static float _deathPanelShowStartUnscaledTime;
        private static float _deathPanelHoldUntilUnscaledTime;
        private static float _deathPanelCleanupUntilUnscaledTime;
        private static IntPtr _deathPanelVictimPtr;
        private static int _deathPanelVictimViewId;
        private static Soldier _deathPanelVictimRef;
        private static string _deathPanelBaseYearLine;
        private static int _deathPanelYearFontSize;
        private static string _deathPanelVanillaReason;

        // =====================================================================
        // 2. Quick-Tap Squad Orders Keybind (Tab) to Spot in Look Direction
        // =====================================================================
        public static void UpdateQuickTapSpot(bool menuOpen)
        {
            if (!VisionConfig.EnableMod.Value || !VisionConfig.EnableQuickTapSpot.Value || menuOpen)
                return;

            VirtualGamepad gpad = GamepadsAPI.GetGamepad(0);
            if (gpad == null || !gpad.GetButtonUp(GameInput.OrdersMenu, out float heldDuration, 0))
                return;

            float holdThreshold = PlayerController.holdButtonDuration > 0.05f ? PlayerController.holdButtonDuration : 0.25f;
            if (heldDuration <= 0f || heldDuration >= holdThreshold)
                return;

            PlayerController pc = PlayerController.currentController;
            Soldier player = pc != null ? pc.ControlledCharacter : null;
            if (pc != null &&
                !pc.lockRotationAndFiring &&
                player != null &&
                player.IsAlive &&
                !player.HasSurrended() &&
                player.joinedSquad != null)
            {
                PlayerController.TrySpotInLookDirection();
            }
        }

        // =====================================================================
        // 3. Death Screen Kill Attribution & Soldier Age Obituary
        // =====================================================================
        private static string CleanUnityObjectName(string rawName)
        {
            if (string.IsNullOrWhiteSpace(rawName))
                return "";

            string cleaned = rawName.Replace("(Clone)", "").Replace('_', ' ').Trim();
            while (cleaned.Contains("  "))
                cleaned = cleaned.Replace("  ", " ");
            return cleaned;
        }

        private static string FormatYearLineWithAge(string yearLine)
        {
            if (string.IsNullOrWhiteSpace(yearLine))
                return "";

            string trimmed = yearLine.Trim();
            if (trimmed.IndexOf("Age ", StringComparison.OrdinalIgnoreCase) >= 0)
                return trimmed;

            int firstYear = -1;
            int secondYear = -1;
            int i = 0;
            while (i <= trimmed.Length - 4)
            {
                if (char.IsDigit(trimmed[i]) &&
                    char.IsDigit(trimmed[i + 1]) &&
                    char.IsDigit(trimmed[i + 2]) &&
                    char.IsDigit(trimmed[i + 3]) &&
                    (i == 0 || !char.IsDigit(trimmed[i - 1])) &&
                    (i + 4 == trimmed.Length || !char.IsDigit(trimmed[i + 4])))
                {
                    int yr = (trimmed[i] - '0') * 1000
                           + (trimmed[i + 1] - '0') * 100
                           + (trimmed[i + 2] - '0') * 10
                           + (trimmed[i + 3] - '0');
                    if (yr >= 1850 && yr <= 2050)
                    {
                        if (firstYear < 0)
                        {
                            firstYear = yr;
                        }
                        else
                        {
                            secondYear = yr;
                            break;
                        }
                    }
                    i += 4;
                    continue;
                }
                i++;
            }

            if (firstYear > 0 && secondYear > firstYear)
            {
                int age = secondYear - firstYear;
                if (age >= 12 && age <= 95)
                {
                    return $"{firstYear} - {secondYear} (Age {age})";
                }
            }

            return trimmed;
        }

        private static string FormatCannonShellLabel(BulletData bulletData)
        {
            if (bulletData == null)
                return "Cannon";
            switch (bulletData.shellType)
            {
                case ShellType.HE:
                    return "HE Shell";
                case ShellType.APHE:
                    return "APHE Shell";
                case ShellType.HEAT:
                    return "HEAT Shell";
                case ShellType.AP:
                    return "AP Shell";
                case ShellType.Rocket:
                    return "Rocket";
                default:
                    if (bulletData.IsFlak())
                        return "Flak Shell";
                    return "Cannon";
            }
        }

        private static string FormatVehicleWeaponName(string vehName, Vehicle veh, BulletData bulletData)
        {
            if (bulletData != null)
            {
                float cal = bulletData.caliber;
                if (cal >= 30f)
                {
                    string shellType = FormatCannonShellLabel(bulletData);
                    return $"{vehName} ({Mathf.RoundToInt(cal)}mm {shellType})";
                }
                if (cal >= 18f)
                {
                    return $"{vehName} ({Mathf.RoundToInt(cal)}mm Autocannon)";
                }
                if (cal >= 12f)
                {
                    return $"{vehName} (Heavy MG)";
                }
                if (cal > 0f)
                {
                    return $"{vehName} (Machine Gun)";
                }
            }

            if (veh != null && veh.IsAirVehicle())
                return $"{vehName} (Bomb / Rocket)";

            return vehName;
        }

        private static string GetShooterWeaponOrVehicleName(Soldier shooter, BulletData bulletData = null)
        {
            if (shooter == null)
            {
                if (bulletData != null && bulletData.caliber >= 18f)
                {
                    return bulletData.caliber >= 30f
                        ? $"{Mathf.RoundToInt(bulletData.caliber)}mm {FormatCannonShellLabel(bulletData)}"
                        : $"{Mathf.RoundToInt(bulletData.caliber)}mm Autocannon";
                }
                return "";
            }

            if (shooter.IsOnVehicle())
            {
                Vehicle veh = shooter.GetCurrentVehicle();
                string vehName = (veh != null && !string.IsNullOrWhiteSpace(veh.name))
                    ? CleanUnityObjectName(veh.name)
                    : "Vehicle";

                Seats seat = shooter.GetCurrentVehicleSeat();
                bool usingVehicleWeapon = shooter.IsUsingSeatTurret
                    || !shooter.CanUseHandItem
                    || (seat != null && seat.hasTurret)
                    || (bulletData != null && bulletData.caliber >= 12f);

                // Vehicle crewmen still carry their personal sidearm (e.g. Colt 1911) in GetHeldItem()
                // while operating a turret or vehicle weapon — never report the holstered sidearm.
                if (usingVehicleWeapon)
                {
                    return FormatVehicleWeaponName(vehName, veh, bulletData);
                }

                ItemObject heldInVeh = shooter.GetHeldItem();
                if (heldInVeh != null && !string.IsNullOrWhiteSpace(heldInVeh.name))
                    return $"{CleanUnityObjectName(heldInVeh.name)} ({vehName})";

                return FormatVehicleWeaponName(vehName, veh, bulletData);
            }

            // If a tank/autocannon shell hits after the crewman bailed out on foot carrying a pistol
            if (bulletData != null && bulletData.caliber >= 18f && !shooter.IsATUnit() && !shooter.hasGrenadeLauncher)
            {
                return bulletData.caliber >= 30f
                    ? $"{Mathf.RoundToInt(bulletData.caliber)}mm {(bulletData.IsHe() ? "HE Shell" : (bulletData.IsAp() ? "AP Shell" : "Cannon Shell"))}"
                    : $"{Mathf.RoundToInt(bulletData.caliber)}mm Autocannon";
            }

            GenericGun curWep = shooter.GetHeldGun();
            if (curWep != null && !string.IsNullOrWhiteSpace(curWep.name))
                return CleanUnityObjectName(curWep.name);

            ItemObject held = shooter.GetHeldItem();
            if (held != null && !string.IsNullOrWhiteSpace(held.name) && !IsNonCombatUtilityItemName(held.name))
                return CleanUnityObjectName(held.name);

            return "";
        }

        private static bool IsNonCombatUtilityItemName(string itemName)
        {
            if (string.IsNullOrWhiteSpace(itemName))
                return true;

            return itemName.IndexOf("bandage", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   itemName.IndexOf("syringe", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   itemName.IndexOf("medkit", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   itemName.IndexOf("medic", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   itemName.IndexOf("binocular", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   itemName.IndexOf("repair", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   itemName.IndexOf("wrench", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   itemName.IndexOf("sandbag", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   itemName.IndexOf("shovel", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   itemName.IndexOf("radio", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   itemName.IndexOf("compass", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   itemName.IndexOf("whistle", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string CleanDisplayLabel(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return "";

            string cleaned = raw.Replace('_', ' ').Trim();
            while (cleaned.Contains("  "))
                cleaned = cleaned.Replace("  ", " ");
            return cleaned;
        }

        private static bool IsSevereKillDetail(string hitDetail)
        {
            if (string.IsNullOrEmpty(hitDetail))
                return false;

            return hitDetail.IndexOf("Cook-off", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   hitDetail.IndexOf("Destroyed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   hitDetail.IndexOf("Direct Blast", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   hitDetail.IndexOf("Crash", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   hitDetail.IndexOf("Run Over", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        internal static void RecordPlayerDamageAttribution(Soldier victim, DamageAttribution attr, bool onlyIfNoRecentDetailed = false, bool sendRemote = true)
        {
            if (victim == null || victim.Pointer == IntPtr.Zero)
                return;

            if (onlyIfNoRecentDetailed &&
                _lastDamageByVictim.TryGetValue(victim.Pointer, out DamageAttribution prev) &&
                (Time.time - prev.Timestamp) <= 0.35f)
            {
                if (attr.CausedBleeding && !prev.CausedBleeding)
                {
                    prev.CausedBleeding = true;
                    _lastDamageByVictim[victim.Pointer] = prev;
                    if (sendRemote && Photon.Pun.PhotonNetwork.InRoom)
                    {
                        TrySendRemotePlayerDeathAttribution(victim, in prev);
                    }
                }
                return;
            }

            _lastDamageByVictim[victim.Pointer] = attr;
            if (sendRemote && Photon.Pun.PhotonNetwork.InRoom)
            {
                TrySendRemotePlayerDeathAttribution(victim, in attr);
            }
        }

        private static void TrySendRemotePlayerDeathAttribution(Soldier victim, in DamageAttribution attr)
        {
            try
            {
                if (victim == null || victim.Pointer == IntPtr.Zero)
                    return;

                SyncSoldier syn = victim.soldierSyncher;
                if (syn == null || syn.OwnSyncher || syn.ViewID <= 0)
                    return;

                Photon.Realtime.Player owner = syn.Controller ?? syn.Owner;
                if (owner == null || owner.IsLocal || owner.ActorNumber <= 0)
                    return;

                float now = Time.time;
                bool bypassCooldown = !victim.IsAlive || victim.IsInjured || victim.life_total.Value <= 0 || IsSevereKillDetail(attr.HitDetail);
                if (!bypassCooldown &&
                    _lastRemoteAttrSendTimeByPtr.TryGetValue(victim.Pointer, out float lastSent) &&
                    (now - lastSent) < 0.25f)
                {
                    return;
                }

                _lastRemoteAttrSendTimeByPtr[victim.Pointer] = now;

                int flags = (attr.IsSelfInflicted ? 1 : 0) |
                            (attr.IsFriendlyFire ? 2 : 0) |
                            (attr.CausedBleeding ? 4 : 0);

                var payload = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStringArray(new string[]
                {
                    syn.ViewID.ToString(),
                    flags.ToString(),
                    attr.DistanceMeters.ToString(),
                    attr.WeaponOrSource ?? "",
                    attr.HitDetail ?? ""
                });

                var opts = new Photon.Realtime.RaiseEventOptions
                {
                    TargetActors = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int>(new int[] { owner.ActorNumber })
                };

                Photon.Pun.PhotonNetwork.RaiseEvent(
                    DeathAttributionPhotonEventCode,
                    payload.Cast<Il2CppSystem.Object>(),
                    opts,
                    ExitGames.Client.Photon.SendOptions.SendReliable);
            }
            catch { }
        }

        private static DamageAttribution StampExplosionPlayerAttribution(
            Soldier player,
            Vector3 expPos,
            float expRadius,
            Soldier attacker,
            bool isOffMapArtilleryOrAir,
            string expSource)
        {
            if (player == null || player.Pointer == IntPtr.Zero)
                return default;

            Vector3 playerPos = player.GetCenterOfUnit();
            float blastDist = Vector3.Distance(expPos, playerPos);

            int distMeters;
            if (isOffMapArtilleryOrAir || attacker == null || attacker.Pointer == player.Pointer)
            {
                // For off-map artillery/aerial bombs or self-damage, report distance from blast impact point
                distMeters = Mathf.RoundToInt(blastDist);
            }
            else if (_activeImpactHasContext && _activeImpactStartPos.sqrMagnitude > 1f)
            {
                distMeters = Mathf.RoundToInt(Vector3.Distance(_activeImpactStartPos, playerPos));
            }
            else
            {
                distMeters = Mathf.RoundToInt(Vector3.Distance(attacker.GetCenterOfUnit(), playerPos));
            }

            bool isSelf = attacker != null && attacker.Pointer == player.Pointer;
            bool isFF = attacker != null &&
                        !isSelf &&
                        !string.IsNullOrEmpty(attacker.faction) &&
                        !string.IsNullOrEmpty(player.faction) &&
                        AiCore.AreSameSideFactions(attacker.faction, player.faction);
            string blastDetail = blastDist <= Mathf.Max(2.5f, expRadius * 0.45f)
                ? "Direct Blast"
                : "Blast Wave";

            DamageAttribution attr = new DamageAttribution
            {
                IsSelfInflicted = isSelf,
                IsFriendlyFire = isFF,
                WeaponOrSource = expSource,
                HitDetail = blastDetail,
                DistanceMeters = Mathf.Max(0, distMeters),
                Timestamp = Time.time,
                CausedBleeding = player.isBleeding
            };

            RecordPlayerDamageAttribution(player, attr, onlyIfNoRecentDetailed: false);
            return attr;
        }

        internal static void RecordVehicleDisabledAttribution(Vehicle veh)
        {
            try
            {
                if (!VisionConfig.EnableMod.Value ||
                    !VisionConfig.EnableDeathScreenAttribution.Value ||
                    veh == null ||
                    veh.Pointer == IntPtr.Zero)
                {
                    return;
                }

                AiCore.EnsureWorldSnapshot();
                IntPtr vehPtr = veh.Pointer;
                bool isAir = veh.IsAirVehicle();
                string vehName = !string.IsNullOrWhiteSpace(veh.name) ? CleanUnityObjectName(veh.name) : (isAir ? "Aircraft" : "Vehicle");
                string cookedDetail = isAir ? "Aircraft Destroyed" : "Vehicle Destroyed (Cook-off)";
                string fallbackDetail = isAir ? "Aircraft Destroyed / Crash" : "Vehicle Destroyed (Cook-off)";
                float now = Time.time;

                int count = AiCore.WorldSoldierCount;
                var soldiers = AiCore.WorldSoldiers;
                for (int i = 0; i < count; i++)
                {
                    ref AiCore.SoldierSnapshot ss = ref soldiers[i];
                    if (ss.Ref == null || ss.Ref.Pointer == IntPtr.Zero || !IsDeathAttributionTarget(ss.Ref))
                        continue;

                    if (!ss.Ref.IsOnVehicle())
                        continue;

                    Vehicle curVeh = ss.Ref.GetCurrentVehicle();
                    if (curVeh == null || curVeh.Pointer != vehPtr)
                        continue;

                    if (_lastDamageByVictim.TryGetValue(ss.Ptr, out DamageAttribution prev) &&
                        (now - prev.Timestamp) <= 3.0f &&
                        !string.IsNullOrWhiteSpace(prev.WeaponOrSource))
                    {
                        prev.HitDetail = cookedDetail;
                        prev.Timestamp = now;
                        RecordPlayerDamageAttribution(ss.Ref, prev, onlyIfNoRecentDetailed: false);
                    }
                    else
                    {
                        RecordPlayerDamageAttribution(
                            ss.Ref,
                            new DamageAttribution
                            {
                                IsSelfInflicted = false,
                                IsFriendlyFire = false,
                                WeaponOrSource = vehName,
                                HitDetail = fallbackDetail,
                                DistanceMeters = 0,
                                Timestamp = now,
                                CausedBleeding = ss.Ref.isBleeding
                            },
                            onlyIfNoRecentDetailed: false);
                    }
                }
            }
            catch { }
        }

        [HarmonyPatch(typeof(Photon.Realtime.LoadBalancingClient), nameof(Photon.Realtime.LoadBalancingClient.OnEvent))]
        public static class LoadBalancingClient_OnEvent_Patch
        {
            public static void Postfix(ExitGames.Client.Photon.EventData __0)
            {
                try
                {
                    if (__0 == null || !VisionConfig.EnableMod.Value)
                    {
                        return;
                    }

                    if (__0.Code == GrenadeKickPhotonEventCode)
                    {
                        HandleRemoteGrenadeKickPhotonEvent(__0);
                        return;
                    }

                    if (__0.Code != DeathAttributionPhotonEventCode ||
                        !VisionConfig.EnableDeathScreenAttribution.Value)
                    {
                        return;
                    }

                    var arr = __0.CustomData != null
                        ? __0.CustomData.TryCast<Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStringArray>()
                        : null;
                    if (arr == null || arr.Length < 5)
                        return;

                    if (!int.TryParse(arr[0], out int viewId) || viewId <= 0)
                        return;

                    int.TryParse(arr[1], out int flags);
                    int.TryParse(arr[2], out int distMeters);
                    string weapon = arr[3] ?? "";
                    string hitDetail = arr[4] ?? "";

                    DamageAttribution attr = new DamageAttribution
                    {
                        IsSelfInflicted = (flags & 1) != 0,
                        IsFriendlyFire = (flags & 2) != 0,
                        CausedBleeding = (flags & 4) != 0,
                        WeaponOrSource = weapon,
                        HitDetail = hitDetail,
                        DistanceMeters = Mathf.Max(0, distMeters),
                        Timestamp = Time.time
                    };

                    _pendingRemoteDeathByViewId[viewId] = attr;

                    Soldier cur = Soldier.CurrentControlledSoldierOrNull();
                    if (cur != null && cur.Pointer != IntPtr.Zero)
                    {
                        SyncSoldier syn = cur.soldierSyncher;
                        if (syn != null && syn.ViewID == viewId)
                        {
                            _lastDamageByVictim[cur.Pointer] = attr;
                        }
                    }

                    // If SyncDeath RPC opened DeathPanel 1-5 frames before Event 197 arrived, refresh the displayed cause immediately.
                    if (viewId == _deathPanelVictimViewId &&
                        _deathPanelVictimRef != null &&
                        _deathPanelVictimRef.Pointer != IntPtr.Zero &&
                        _deathPanelCleanupUntilUnscaledTime > Time.unscaledTime &&
                        (Time.unscaledTime - _deathPanelShowStartUnscaledTime) <= 3.0f)
                    {
                        _pendingRemoteDeathByViewId.Remove(viewId);
                        FormatAndApplyDeathAttribution(
                            DeathPanel.instance,
                            _deathPanelVictimRef,
                            _deathPanelVanillaReason,
                            _deathPanelBaseYearLine ?? "",
                            _deathPanelYearFontSize > 0 ? _deathPanelYearFontSize : 38,
                            hasAttr: true,
                            in attr);
                    }
                }
                catch (Exception ex)
                {
                    AiCore.ReportPatchException("Death.PhotonEv", ex);
                }
            }
        }

        private static string FormatBodyPart(BodyPartType part)
        {
            switch (part)
            {
                case BodyPartType.head:
                    return "Headshot";
                case BodyPartType.chest:
                    return "Torso";
                case BodyPartType.arm_l:
                case BodyPartType.arm_r:
                    return "Arm";
                case BodyPartType.leg_l:
                case BodyPartType.leg_r:
                    return "Leg";
                default:
                    return "Torso";
            }
        }

        private static bool IsPlayerCharacter(Soldier s)
        {
            if (s == null || s.Pointer == IntPtr.Zero)
                return false;

            if (s.IsPlayer() || PlayerController.CharacterIsPlayer(s))
                return true;

            Soldier cur = Soldier.CurrentControlledSoldierOrNull();
            return cur != null && cur.Pointer == s.Pointer;
        }

        private static bool IsDeathAttributionTarget(Soldier s)
        {
            if (IsPlayerCharacter(s))
                return true;

            return Photon.Pun.PhotonNetwork.InRoom && AiCore.IsAnyHumanPlayerSoldier(s);
        }

        private static void TryCaptureExplosionPreState(Soldier s, Vector3 expPos, float maxDistSq, ref ExplosionPatchState state)
        {
            if (s == null || s.Pointer == IntPtr.Zero)
                return;

            if ((s.GetCenterOfUnit() - expPos).sqrMagnitude > maxDistSq)
                return;

            if (!AiCore.TryEnsureArrayCapacity(ref _expPreStates, _expPreStateTop + 1, 512))
                return;

            _expPreStates[_expPreStateTop++] = new ExplosionPlayerPreState
            {
                Player = s,
                Ptr = s.Pointer,
                PreLife = s.life_total.Value,
                PreAlive = s.IsAlive,
                PreInjured = s.IsInjured,
                PreBleeding = s.isBleeding,
                HitPartRecorded = false
            };
            state.SnapCount++;
        }

        [HarmonyPatch(typeof(Corvostudio.Weapons.Explosion), nameof(Corvostudio.Weapons.Explosion.CreateExplosion))]
        public static class Explosion_CreateExplosion_Patch
        {
            public static void Prefix(Vector3 __0, float __1, float __2, float __3, Soldier __4, GameObject __5, ref HitType __6, bool __7, out ExplosionPatchState __state)
            {
                __state = new ExplosionPatchState
                {
                    PrevAttackerPtr = _activeDamageAttackerPtr,
                    PrevWeaponName = _activeExplosionWeaponName,
                    PrevIsArtillery = _activeExplosionIsArtillery,
                    PrevPos = _activeExplosionPos,
                    PrevRadius = _activeExplosionRadius,
                    PrevExpAttacker = _activeExplosionAttacker,
                    SnapStart = _expPreStateTop,
                    SnapCount = 0
                };
                try
                {
                    // __7 is canDamage; ignore non-damaging visual explosions
                    if (!VisionConfig.EnableMod.Value || !__7)
                        return;

                    string srcVehFac = null;
                    if (__4 != null && __4.Pointer != IntPtr.Zero)
                    {
                        VisionPatches.RecordExplosionSound(__0, __3, __4);
                        _activeDamageAttackerPtr = __4.Pointer;
                    }
                    else
                    {
                        Vehicle srcVeh = __5 != null ? (__5.GetComponent<Vehicle>() ?? __5.GetComponentInParent<Vehicle>()) : null;
                        srcVehFac = (srcVeh != null && srcVeh.Pointer != IntPtr.Zero) ? srcVeh.GetVehicleFaction() : null;
                        VisionPatches.RecordExplosionSound(__0, __3, null, srcVehFac);

                        IntPtr resolvedVehicleKillerPtr = new IntPtr(-1);
                        if (srcVeh != null &&
                            srcVeh.Pointer != IntPtr.Zero &&
                            IsOnlineScoreboardFixActive() &&
                            _localPlayerDamagedVehicles.TryGetValue(srcVeh.Pointer, out float hitTime) &&
                            (Time.time - hitTime) <= 20.0f)
                        {
                            Soldier curLocal = Soldier.CurrentControlledSoldierOrNull();
                            if (curLocal != null && curLocal.Pointer != IntPtr.Zero)
                                resolvedVehicleKillerPtr = curLocal.Pointer;
                        }
                        _activeDamageAttackerPtr = resolvedVehicleKillerPtr;
                    }

                    // Reclassify Molotov / phosphorus / incendiary grenade shatters from HitType.Explosion -> HitType.Fire
                    // BEFORE native CreateExplosion runs so tank tracks (VehicleDamageableTrackPart) are 100% immune!
                    TryReclassifyIncendiaryGrenadeExplosion(__0, __5, ref __6);

                    if (__3 >= 4.5f && __6 == HitType.Explosion)
                    {
                        string shellFac = (__4 != null && __4.Pointer != IntPtr.Zero) ? AiCore.GetCachedFaction(__4) : srcVehFac;
                        RecordHeavyArtilleryShellingImpact(__0, __3, shellFac);
                    }

                    if (VisionConfig.EnableEmergentDecisionEngine != null && VisionConfig.EnableEmergentDecisionEngine.Value)
                    {
                        string expFac = (__4 != null && __4.Pointer != IntPtr.Zero) ? AiCore.GetCachedFaction(__4) : srcVehFac;
                        EmergentRuntimeBridge.NotifyExplosion(__0, __3, expFac, __3 >= 4.5f || __1 >= 45f);
                    }

                    if (!VisionConfig.EnableDeathScreenAttribution.Value)
                        return;

                    string expSource = "";
                    bool isOffMapArtilleryOrAir = false;

                    // 1. Check if this explosion was triggered by an impacting projectile (BulletInstance.OnHit)
                    if (_activeImpactHasContext)
                    {
                        BulletData bd = _activeImpactBulletData;
                        float cal = bd != null ? bd.caliber : 0f;
                        ShellType st = bd != null ? bd.shellType : ShellType.HE;

                        if (__4 != null && __4.IsOnVehicle())
                        {
                            Vehicle shooterVeh = __4.GetCurrentVehicle();
                            string vName = (shooterVeh != null && !string.IsNullOrWhiteSpace(shooterVeh.name))
                                ? CleanUnityObjectName(shooterVeh.name)
                                : "Vehicle";

                            if (shooterVeh != null && shooterVeh.IsAirVehicle())
                            {
                                isOffMapArtilleryOrAir = true;
                                expSource = st == ShellType.Rocket
                                    ? $"{vName} (Aircraft Rocket)"
                                    : (cal >= 45f ? $"{vName} (Aerial Bomb)" : $"{vName} ({Mathf.RoundToInt(cal)}mm Cannon)");
                            }
                            else
                            {
                                expSource = FormatVehicleWeaponName(vName, shooterVeh, bd);
                            }
                        }
                        else if (_activeImpactIsMortar)
                        {
                            if (cal >= 130f)
                            {
                                isOffMapArtilleryOrAir = true;
                                expSource = $"{Mathf.RoundToInt(cal)}mm Heavy Artillery Barrage";
                            }
                            else if (cal >= 95f)
                            {
                                isOffMapArtilleryOrAir = true;
                                expSource = $"{Mathf.RoundToInt(cal)}mm Artillery Barrage";
                            }
                            else if (cal >= 45f)
                            {
                                expSource = $"{Mathf.RoundToInt(cal)}mm Mortar Shell";
                            }
                            else
                            {
                                isOffMapArtilleryOrAir = true;
                                expSource = "Artillery / Mortar Barrage";
                            }
                        }
                        else if (__4 != null)
                        {
                            if (st == ShellType.Rocket || st == ShellType.HEAT || __4.IsATUnit())
                            {
                                string held = GetShooterWeaponOrVehicleName(__4, null);
                                string tag = st == ShellType.Rocket ? "AT Rocket" : "HEAT";
                                expSource = !string.IsNullOrEmpty(held) ? $"{held} ({tag})" : $"Anti-Tank {tag}";
                            }
                            else if (__4.hasGrenadeLauncher)
                            {
                                string held = GetShooterWeaponOrVehicleName(__4, null);
                                expSource = !string.IsNullOrEmpty(held) ? $"{held} (Rifle Grenade)" : "Rifle Grenade";
                            }
                            else if (cal >= 95f)
                            {
                                isOffMapArtilleryOrAir = true;
                                expSource = $"{Mathf.RoundToInt(cal)}mm Artillery Shell";
                            }
                            else if (cal >= 30f)
                            {
                                expSource = $"{Mathf.RoundToInt(cal)}mm HE Shell";
                            }
                        }
                        else if (cal >= 95f)
                        {
                            isOffMapArtilleryOrAir = true;
                            expSource = $"{Mathf.RoundToInt(cal)}mm Artillery Barrage";
                        }
                    }

                    // 2. Check ignoreHittable (__5) for thrown ItemGrenade or exploding Vehicle
                    if (string.IsNullOrEmpty(expSource) && __5 != null)
                    {
                        ItemGrenade gren = __5.GetComponent<ItemGrenade>();
                        if (gren == null)
                            gren = __5.GetComponentInParent<ItemGrenade>();

                        if (gren != null && !string.IsNullOrWhiteSpace(gren.name))
                        {
                            string gName = CleanUnityObjectName(gren.name);
                            string gType = gren.grenadeType == GrenadeType.ATGrenade
                                ? "AT Grenade"
                                : (gren.grenadeType == GrenadeType.phosporusGrenade ? "White Phosphorus" : "Frag Grenade");
                            expSource = gName.IndexOf("Grenade", StringComparison.OrdinalIgnoreCase) >= 0
                                ? gName
                                : $"{gName} ({gType})";
                        }
                        else
                        {
                            Vehicle expVeh = __5.GetComponentInParent<Vehicle>();
                            if (expVeh != null && !string.IsNullOrWhiteSpace(expVeh.name))
                                expSource = $"{CleanUnityObjectName(expVeh.name)} (Vehicle Detonation)";
                        }
                    }

                    // 3. Fallback classification by responsible unit & blast parameters
                    if (string.IsNullOrEmpty(expSource))
                    {
                        if (__4 != null && __4.IsOnVehicle())
                        {
                            Vehicle shooterVeh = __4.GetCurrentVehicle();
                            if (shooterVeh != null && !string.IsNullOrWhiteSpace(shooterVeh.name))
                            {
                                string vName = CleanUnityObjectName(shooterVeh.name);
                                if (shooterVeh.IsAirVehicle())
                                {
                                    isOffMapArtilleryOrAir = true;
                                    expSource = $"{vName} (Aerial Bomb / Rocket)";
                                }
                                else
                                {
                                    expSource = $"{vName} (HE Shell)";
                                }
                            }
                            else
                            {
                                expSource = "Vehicle HE Shell";
                            }
                        }
                        else if (__4 == null)
                        {
                            if (__3 >= 8f)
                            {
                                isOffMapArtilleryOrAir = true;
                                expSource = "Artillery Barrage";
                            }
                            else
                            {
                                expSource = "Landmine / Explosive";
                            }
                        }
                        else
                        {
                            // __2 is maxPenetration: >= 30f on a thrown explosive indicates an AT Grenade or Satchel Charge
                            if (__2 >= 30f)
                            {
                                expSource = "Anti-Tank Grenade / Satchel";
                            }
                            else
                            {
                                expSource = "Frag Grenade";
                            }
                        }
                    }

                    _activeExplosionWeaponName = expSource;
                    _activeExplosionIsArtillery = isOffMapArtilleryOrAir || (__4 == null);
                    _activeExplosionPos = __0;
                    _activeExplosionRadius = __3;
                    _activeExplosionAttacker = __4;

                    // Capture pre-blast HP/state instead of blindly stamping attribution before damage occurs.
                    // Actual attribution is written only when BodyPart.HitPart or Postfix confirms real damage.
                    float maxCapDist = Mathf.Max(22f, __3 * 4.5f);
                    float maxCapDistSq = maxCapDist * maxCapDist;

                    Soldier localPlayer = Soldier.CurrentControlledSoldierOrNull();
                    IntPtr localPlayerPtr = (localPlayer != null && localPlayer.Pointer != IntPtr.Zero) ? localPlayer.Pointer : IntPtr.Zero;
                    if (localPlayerPtr != IntPtr.Zero)
                    {
                        TryCaptureExplosionPreState(localPlayer, __0, maxCapDistSq, ref __state);
                    }

                    if (Photon.Pun.PhotonNetwork.InRoom)
                    {
                        AiCore.EnsureWorldSnapshot();
                        int count = AiCore.WorldSoldierCount;
                        var soldiers = AiCore.WorldSoldiers;
                        for (int i = 0; i < count; i++)
                        {
                            ref AiCore.SoldierSnapshot ss = ref soldiers[i];
                            if (ss.Ref != null && ss.Ptr != IntPtr.Zero && ss.Ptr != localPlayerPtr && IsDeathAttributionTarget(ss.Ref))
                            {
                                TryCaptureExplosionPreState(ss.Ref, __0, maxCapDistSq, ref __state);
                            }
                        }
                    }
                }
                catch { }
            }

            public static void Postfix(ExplosionPatchState __state)
            {
                try
                {
                    if (__state.SnapCount > 0 &&
                        VisionConfig.EnableMod.Value &&
                        VisionConfig.EnableDeathScreenAttribution.Value &&
                        !string.IsNullOrEmpty(_activeExplosionWeaponName))
                    {
                        int end = Mathf.Min(_expPreStateTop, __state.SnapStart + __state.SnapCount);
                        for (int i = __state.SnapStart; i < end; i++)
                        {
                            ref ExplosionPlayerPreState snap = ref _expPreStates[i];
                            if (snap.HitPartRecorded || snap.Player == null || snap.Player.Pointer == IntPtr.Zero)
                                continue;

                            Soldier p = snap.Player;
                            bool tookRealDamage = (snap.PreAlive && !p.IsAlive) ||
                                                  (!snap.PreInjured && p.IsInjured) ||
                                                  (p.life_total.Value < snap.PreLife) ||
                                                  (!snap.PreBleeding && p.isBleeding);
                            if (!tookRealDamage)
                                continue;

                            DamageAttribution stamped = StampExplosionPlayerAttribution(
                                p,
                                _activeExplosionPos,
                                _activeExplosionRadius,
                                _activeExplosionAttacker,
                                _activeExplosionIsArtillery,
                                _activeExplosionWeaponName);

                            if (!p.IsAlive &&
                                _deathPanelVictimRef != null &&
                                _deathPanelVictimRef.Pointer == p.Pointer &&
                                _deathPanelCleanupUntilUnscaledTime > Time.unscaledTime &&
                                (Time.unscaledTime - _deathPanelShowStartUnscaledTime) <= 1.0f)
                            {
                                _lastDamageByVictim.Remove(p.Pointer);
                                FormatAndApplyDeathAttribution(
                                    DeathPanel.instance,
                                    p,
                                    _deathPanelVanillaReason,
                                    _deathPanelBaseYearLine ?? "",
                                    _deathPanelYearFontSize > 0 ? _deathPanelYearFontSize : 38,
                                    hasAttr: true,
                                    in stamped);
                            }
                        }
                    }
                }
                catch { }
                finally
                {
                    if (_expPreStateTop > __state.SnapStart)
                        Array.Clear(_expPreStates, __state.SnapStart, _expPreStateTop - __state.SnapStart);
                    _expPreStateTop = __state.SnapStart;
                    _activeExplosionWeaponName = __state.PrevWeaponName;
                    _activeExplosionIsArtillery = __state.PrevIsArtillery;
                    _activeExplosionPos = __state.PrevPos;
                    _activeExplosionRadius = __state.PrevRadius;
                    _activeExplosionAttacker = __state.PrevExpAttacker;
                    _activeDamageAttackerPtr = __state.PrevAttackerPtr;
                }
            }
        }

        public struct BulletDamagePatchState
        {
            public Soldier BotCalloutVictim;
            public Soldier PlayerAttrVictim;
            public IntPtr PlayerAttrVictimPtr;
            public IntPtr PrevAttackerPtr;
            public bool IsDirectBodyOrHelmet;
            public int PreLife;
            public bool PreAlive;
            public bool PreInjured;
            public bool PreBleeding;
            public bool HadPrevAttr;
            internal DamageAttribution PrevAttr;
            internal DamageAttribution WrittenAttr;
        }

        [HarmonyPatch(typeof(Bullet), nameof(Bullet.BulletDamage))]
        public static class Bullet_BulletDamage_Patch
        {
            public static bool Prefix(
                ImpactSpecifier __0,
                BulletData __1,
                Vector3 __2,
                int __3,
                Soldier __4,
                ref Creature __result,
                out BulletDamagePatchState __state)
            {
                __state = new BulletDamagePatchState { PrevAttackerPtr = _activeDamageAttackerPtr };
                try
                {
                    if (!VisionConfig.EnableMod.Value || __0 == null)
                        return true;

                    if (__4 != null && __4.Pointer != IntPtr.Zero)
                        _activeDamageAttackerPtr = __4.Pointer;
                    else if (__state.PrevAttackerPtr == IntPtr.Zero)
                        _activeDamageAttackerPtr = IntPtr.Zero;

                    Soldier victim = null;
                    VehicleDamagablePart hitVehiclePart = null;
                    string hitDetail = "Torso";
                    bool isDirectBodyOrHelmetHit = false;

                    BodyPart bp = __0.TryCast<BodyPart>();
                    if (bp != null && bp.unit != null)
                    {
                        victim = bp.unit.TryCast<Soldier>();
                        hitDetail = FormatBodyPart(bp.bodyPart);
                        isDirectBodyOrHelmetHit = true;
                    }
                    else
                    {
                        ItemHelmet helmet = __0.TryCast<ItemHelmet>();
                        if (helmet != null && helmet.User != null)
                        {
                            victim = helmet.User;
                            hitDetail = "Headshot (Helmet)";
                            isDirectBodyOrHelmetHit = true;
                        }
                        else
                        {
                            hitVehiclePart = __0.TryCast<VehicleDamagablePart>();
                            if (hitVehiclePart != null && hitVehiclePart.rootVeh != null)
                            {
                                Vehicle targetVeh = hitVehiclePart.rootVeh;
                                Soldier vehOccupant = null;
                                Soldier curPlayer = Soldier.CurrentControlledSoldierOrNull();
                                if (curPlayer != null &&
                                    curPlayer.IsOnVehicle() &&
                                    curPlayer.GetCurrentVehicle() != null &&
                                    curPlayer.GetCurrentVehicle().Pointer == targetVeh.Pointer)
                                {
                                    vehOccupant = curPlayer;
                                }
                                else if (Photon.Pun.PhotonNetwork.InRoom && targetVeh.seats != null)
                                {
                                    var seats = targetVeh.seats;
                                    for (int si = 0; si < seats.Length; si++)
                                    {
                                        Seats seat = seats[si];
                                        if (seat != null && seat.hasUnitSet && seat.unitSet != null && seat.unitSet.IsAlive && IsDeathAttributionTarget(seat.unitSet))
                                        {
                                            vehOccupant = seat.unitSet;
                                            break;
                                        }
                                    }
                                }

                                if (vehOccupant != null)
                                {
                                    // Ignore harmless non-penetrating small-arms ricochets and non-explosive friendly hull hits
                                    // so stray SMG/rifle pings on tank armor never overwrite _lastDamageByVictim with "Vehicle Hit".
                                    bool fromExp = !string.IsNullOrEmpty(_activeExplosionWeaponName);
                                    bool sameFactionBullet = !fromExp &&
                                                             __4 != null &&
                                                             __4.Pointer != IntPtr.Zero &&
                                                             AiCore.AreSameSideFactions(AiCore.GetCachedFaction(__4), AiCore.GetCachedFaction(vehOccupant));
                                    bool canThreatenArmor = fromExp ||
                                                            __1 == null ||
                                                            __1.IsHe() ||
                                                            __1.IsFire() ||
                                                            __1.explosion_penetration > 2.0f ||
                                                            __1.mmPenetration > hitVehiclePart.armorThickness;
                                    if (!sameFactionBullet && canThreatenArmor)
                                    {
                                        victim = vehOccupant;
                                        hitDetail = "Vehicle Hit";
                                    }
                                }
                            }
                        }
                    }

                    // Enclosed Armored Ground Vehicle Crew Protection:
                    // Stop small-arms bullets (<12mm / non-AP / non-HE / non-Fire) from killing buttoned-up crewmen
                    // (!seat.showUnitSet) inside intact armored hulls via BodyPart/Helmet collider clipping or
                    // point-blank friendly/enemy muzzles poking inside the outer hull collider.
                    if (VisionConfig.EnableRealisticVehicleArmorAndTopAttack.Value &&
                        isDirectBodyOrHelmetHit &&
                        victim != null &&
                        victim.Pointer != IntPtr.Zero &&
                        victim.IsOnVehicle() &&
                        string.IsNullOrEmpty(_activeExplosionWeaponName) &&
                        !ActiveCrewSpallContext)
                    {
                        Vehicle groundVeh = victim.GetCurrentVehicle();
                        if (groundVeh != null &&
                            groundVeh.Pointer != IntPtr.Zero &&
                            !groundVeh.IsAirVehicle() &&
                            !groundVeh.IsCoverDestroyed())
                        {
                            float effArmor = GetEffectiveVehicleExplosionArmor(groundVeh);
                            if (effArmor >= 8.0f &&
                                (__1 == null ||
                                 (!__1.IsHe() &&
                                  !__1.IsFlak() &&
                                  !__1.IsFire() &&
                                  __1.explosion_penetration < 5.0f &&
                                  __1.mmPenetration < Mathf.Max(12.0f, effArmor))) &&
                                IsSoldierInEnclosedVehicleSeat(groundVeh, victim))
                            {
                                EnclosedCrewBulletClipsBlockedCount++;
                                __result = null;
                                return false;
                            }
                        }
                    }

                    // Realistic Airplane Canopy vs. Armored Belly/Seat Pan Protection:
                    // Allow direct canopy/window BodyPart & Helmet hits from level/above (dogfighting aircraft,
                    // open cockpits) and from heavy .50 cal+ rounds (>= 12.5mm), while stopping rifle-caliber
                    // ground fire (< 12.5mm, non-HE, non-Flak) coming up from steep below through the floor/engine.
                    if (isDirectBodyOrHelmetHit &&
                        victim != null &&
                        victim.Pointer != IntPtr.Zero &&
                        victim.IsOnVehicle() &&
                        __1 != null &&
                        !__1.IsHe() &&
                        !__1.IsFlak() &&
                        __1.caliber < 12.5f &&
                        string.IsNullOrEmpty(_activeExplosionWeaponName))
                    {
                        Vehicle victimVeh = victim.GetCurrentVehicle();
                        if (victimVeh != null &&
                            victimVeh.Pointer != IntPtr.Zero &&
                            victimVeh.IsAirVehicle() &&
                            !victimVeh.IsCoverDestroyed() &&
                            !victimVeh.IsDisabled())
                        {
                            bool fromSteepBelow = _activeImpactHasContext &&
                                                  _activeImpactDirection.sqrMagnitude > 0.001f &&
                                                  _activeImpactDirection.y > 0.30f;

                            if (!fromSteepBelow && !_activeImpactHasContext &&
                                __4 != null && __4.Pointer != IntPtr.Zero &&
                                __4.transform != null && victim.transform != null)
                            {
                                Vehicle shooterVeh = __4.IsOnVehicle() ? __4.GetCurrentVehicle() : null;
                                bool shooterAirborne = shooterVeh != null && shooterVeh.Pointer != IntPtr.Zero && shooterVeh.IsAirVehicle();
                                if (!shooterAirborne && (victim.transform.position.y - __4.transform.position.y) >= 15.0f)
                                    fromSteepBelow = true;
                            }

                            if (fromSteepBelow)
                            {
                                AirplanePilotMgHitsBlockedCount++;
                                __result = null;
                                return false;
                            }
                        }
                    }

                    // Attribute direct local-player hits & vehicle hits (runs only after armor/airplane blocks pass)
                    if (IsOnlineScoreboardFixActive() && __4 != null && __4.Pointer != IntPtr.Zero && IsPlayerCharacter(__4))
                    {
                        if (isDirectBodyOrHelmetHit && victim != null && victim.Pointer != IntPtr.Zero)
                        {
                            TagVictimDamagedByLocalPlayer(__4, victim, __1 != null && __1.IsFire());
                        }
                        else if (hitVehiclePart != null && hitVehiclePart.rootVeh != null && hitVehiclePart.rootVeh.Pointer != IntPtr.Zero)
                        {
                            RecordLocalPlayerDamagedVehicle(hitVehiclePart.rootVeh.Pointer);
                        }
                    }

                    // Track pre-hit alive enemy victim for AI Bot Kill Callouts (VoiceClip.enemyDown — "I got him!")
                    if (isDirectBodyOrHelmetHit &&
                        VisionConfig.EnableTacticalVoiceCallouts.Value &&
                        victim != null &&
                        victim.Pointer != IntPtr.Zero &&
                        victim.IsAlive &&
                        __4 != null &&
                        __4.Pointer != IntPtr.Zero &&
                        AiCore.IsLocallyOwnedBot(__4) &&
                        !AiCore.AreSameSideFactions(AiCore.GetCachedFaction(__4), AiCore.GetCachedFaction(victim)))
                    {
                        __state.BotCalloutVictim = victim;
                    }

                    // Trigger nearby cross-squad witness suppression & cover replanning whenever any friendly soldier takes a hit
                    if (isDirectBodyOrHelmetHit &&
                        victim != null &&
                        victim.Pointer != IntPtr.Zero &&
                        victim.IsAlive &&
                        __3 > 0)
                    {
                        bool hasOrigin = __4 != null && __4.Pointer != IntPtr.Zero && __4.transform != null;
                        Vector3 origin = hasOrigin ? __4.transform.position : __2;
                        byte hitKind = 1;
                        if (__4 != null && __4.Pointer != IntPtr.Zero && __4.IsOnVehicle())
                            hitKind = 2;
                        else if (!string.IsNullOrEmpty(_activeExplosionWeaponName) ||
                                 (__1 != null && (__1.IsHe() || __1.IsFlak() || __1.IsFire() || __1.caliber >= 20f)))
                            hitKind = 3;
                        float hitDist = (hasOrigin && victim.transform != null)
                            ? Vector3.Distance(victim.transform.position, origin)
                            : -1f;
                        EmergentRuntimeBridge.RecordLastHitContext(victim, hitKind, hitDist, Time.time);
                        NotifyFriendlyHitOrCasualty(victim, origin, hasOrigin, isFatalOrDown: false);
                    }

                    if (!VisionConfig.EnableDeathScreenAttribution.Value ||
                        victim == null ||
                        victim.Pointer == IntPtr.Zero ||
                        !victim.IsAlive ||
                        !IsDeathAttributionTarget(victim))
                    {
                        return true;
                    }

                    if (isDirectBodyOrHelmetHit &&
                        __4 != null &&
                        __4.Pointer != IntPtr.Zero &&
                        !BodyPart.AllowDamage(AiCore.GetCachedFaction(__4), victim.faction))
                    {
                        return true;
                    }

                    bool fromExplosion = !string.IsNullOrEmpty(_activeExplosionWeaponName);
                    string expWeaponName = _activeExplosionWeaponName;
                    int preservedDistMeters = -1;

                    // If a flying shrapnel BulletInstance from an explosion hits a few frames after CreateExplosion returned,
                    // preserve the explosion's WeaponOrSource instead of overwriting it with the shooter's holstered sidearm.
                    if (!fromExplosion &&
                        (__1 == null || __1.IsHe() || __1.caliber <= 0f) &&
                        _lastDamageByVictim.TryGetValue(victim.Pointer, out DamageAttribution recentAttr) &&
                        (Time.time - recentAttr.Timestamp) <= 0.45f &&
                        (string.Equals(recentAttr.HitDetail, "Direct Blast", StringComparison.Ordinal) ||
                         string.Equals(recentAttr.HitDetail, "Blast Wave", StringComparison.Ordinal) ||
                         recentAttr.HitDetail.StartsWith("Shrapnel", StringComparison.Ordinal)))
                    {
                        fromExplosion = true;
                        expWeaponName = recentAttr.WeaponOrSource;
                        preservedDistMeters = recentAttr.DistanceMeters;
                    }

                    string weaponOrSource = fromExplosion
                        ? expWeaponName
                        : GetShooterWeaponOrVehicleName(__4, __1);

                    if (fromExplosion)
                    {
                        hitDetail = hitDetail.StartsWith("Headshot", StringComparison.Ordinal)
                            ? $"Shrapnel {hitDetail}"
                            : $"Shrapnel ({hitDetail})";
                    }

                    bool isSelf = __4 != null && __4.Pointer == victim.Pointer;
                    bool isFF = __4 != null &&
                                !isSelf &&
                                !string.IsNullOrEmpty(AiCore.GetCachedFaction(__4)) &&
                                !string.IsNullOrEmpty(victim.faction) &&
                                AiCore.AreSameSideFactions(AiCore.GetCachedFaction(__4), victim.faction);

                    // Note: __3 in Bullet.BulletDamage is int damage, NOT distance; always compute distance in meters
                    int distMeters;
                    if (preservedDistMeters >= 0)
                    {
                        distMeters = preservedDistMeters;
                    }
                    else if (_activeExplosionIsArtillery)
                    {
                        distMeters = Mathf.RoundToInt(Vector3.Distance(__2, victim.GetCenterOfUnit()));
                    }
                    else if (__4 != null && !isSelf)
                    {
                        distMeters = Mathf.RoundToInt(Vector3.Distance(__4.GetCenterOfUnit(), victim.GetCenterOfUnit()));
                    }
                    else
                    {
                        distMeters = Mathf.RoundToInt(Vector3.Distance(__2, victim.GetCenterOfUnit()));
                    }

                    DamageAttribution bulletAttr = new DamageAttribution
                    {
                        IsSelfInflicted = isSelf,
                        IsFriendlyFire = isFF,
                        WeaponOrSource = !string.IsNullOrEmpty(weaponOrSource) ? weaponOrSource : (_activeExplosionIsArtillery ? "Artillery / Mortar Barrage" : "Enemy Fire"),
                        HitDetail = hitDetail,
                        DistanceMeters = Mathf.Max(0, distMeters),
                        Timestamp = Time.time,
                        CausedBleeding = victim.isBleeding
                    };

                    __state.PlayerAttrVictim = victim;
                    __state.PlayerAttrVictimPtr = victim.Pointer;
                    __state.IsDirectBodyOrHelmet = isDirectBodyOrHelmetHit;
                    __state.PreLife = victim.life_total.Value;
                    __state.PreAlive = victim.IsAlive;
                    __state.PreInjured = victim.IsInjured;
                    __state.PreBleeding = victim.isBleeding;
                    __state.HadPrevAttr = _lastDamageByVictim.TryGetValue(victim.Pointer, out __state.PrevAttr);
                    __state.WrittenAttr = bulletAttr;

                    RecordPlayerDamageAttribution(victim, bulletAttr, onlyIfNoRecentDetailed: false, sendRemote: false);

                    // Also stamp any additional human crewmates sharing a struck vehicle hull in Co-op.
                    if (!isDirectBodyOrHelmetHit &&
                        hitVehiclePart != null &&
                        hitVehiclePart.rootVeh != null &&
                        Photon.Pun.PhotonNetwork.InRoom &&
                        hitVehiclePart.rootVeh.seats != null)
                    {
                        var seats = hitVehiclePart.rootVeh.seats;
                        for (int si = 0; si < seats.Length; si++)
                        {
                            Seats seat = seats[si];
                            if (seat != null &&
                                seat.hasUnitSet &&
                                seat.unitSet != null &&
                                seat.unitSet.Pointer != victim.Pointer &&
                                seat.unitSet.IsAlive &&
                                IsDeathAttributionTarget(seat.unitSet))
                            {
                                RecordPlayerDamageAttribution(seat.unitSet, bulletAttr, onlyIfNoRecentDetailed: false);
                            }
                        }
                    }
                }
                catch { }

                return true;
            }

            public static void Postfix(Soldier __4, BulletDamagePatchState __state)
            {
                _activeDamageAttackerPtr = __state.PrevAttackerPtr;

                try
                {
                    Soldier attrVictim = __state.PlayerAttrVictim;
                    if (__state.PlayerAttrVictimPtr != IntPtr.Zero && attrVictim != null && attrVictim.Pointer != IntPtr.Zero)
                    {
                        bool confirmed = !__state.IsDirectBodyOrHelmet ||
                                         (__state.PreAlive && !attrVictim.IsAlive) ||
                                         (!__state.PreInjured && attrVictim.IsInjured) ||
                                         (attrVictim.life_total.Value < __state.PreLife) ||
                                         (!__state.PreBleeding && attrVictim.isBleeding);

                        if (!confirmed)
                        {
                            if (__state.HadPrevAttr)
                                _lastDamageByVictim[__state.PlayerAttrVictimPtr] = __state.PrevAttr;
                            else
                                _lastDamageByVictim.Remove(__state.PlayerAttrVictimPtr);
                        }
                        else
                        {
                            DamageAttribution finalAttr = __state.WrittenAttr;
                            if (attrVictim.isBleeding && !finalAttr.CausedBleeding)
                            {
                                finalAttr.CausedBleeding = true;
                                if (attrVictim.IsAlive || !IsPlayerCharacter(attrVictim))
                                    _lastDamageByVictim[__state.PlayerAttrVictimPtr] = finalAttr;
                            }
                            if (Photon.Pun.PhotonNetwork.InRoom)
                            {
                                TrySendRemotePlayerDeathAttribution(attrVictim, in finalAttr);
                            }
                        }
                    }
                }
                catch { }

                Soldier botVictim = __state.BotCalloutVictim;
                if (botVictim == null)
                    return;

                try
                {
                    // Confirmed lethal hit by an AI bot: match vanilla DamageFeedback.OnSoldierHit (~25% roll)
                    // routed through unified Spectator-safe TrySayTacticalVoice + Co-op SyncSay!
                    if (!botVictim.IsAlive &&
                        __4 != null &&
                        __4.IsAlive &&
                        !__4.isBleeding &&
                        __4.GetSuppressionValue() < 55)
                    {
                        int chance = __4.IsMarksman() ? 35 : 25;
                        if (UnityEngine.Random.Range(0, 100) < chance)
                        {
                            BotKillCalloutsCount++;
                            TrySayTacticalVoice(__4, __4.joinedSquad, VoiceManager.VoiceClip.enemyDown, 0.25f);
                        }
                    }
                }
                catch { }
            }
        }

        public struct BodyPartHitPatchState
        {
            public Soldier Victim;
            public IntPtr VictimPtr;
            public bool DidWrite;
            public bool HadPrev;
            internal DamageAttribution PrevAttr;
            internal DamageAttribution WrittenAttr;
            public int PreLife;
            public bool PreAlive;
            public bool PreInjured;
            public bool PreBleeding;
        }

        [HarmonyPatch(typeof(BodyPart), nameof(BodyPart.HitPart))]
        public static class BodyPart_HitPart_Patch
        {
            public static void Prefix(BodyPart __instance, float __0, float __1, Vector3 __2, string __3, HitType __4, out BodyPartHitPatchState __state)
            {
                __state = default;
                long profStart = HitchProfiler.BeginModScope();
                try
                {
                    if (!VisionConfig.EnableMod.Value ||
                        !VisionConfig.EnableDeathScreenAttribution.Value ||
                        __instance == null ||
                        __instance.unit == null ||
                        __0 <= 0f)
                    {
                        return;
                    }

                    Soldier victim = __instance.unit.TryCast<Soldier>();
                    if (victim == null ||
                        victim.Pointer == IntPtr.Zero ||
                        !victim.IsAlive ||
                        !IsDeathAttributionTarget(victim))
                    {
                        return;
                    }

                    if (!__instance.CanBeDamaged() || !BodyPart.AllowDamage(__3, victim.faction))
                        return;

                    bool insideExplosion = !string.IsNullOrEmpty(_activeExplosionWeaponName) &&
                                           (__4 == HitType.Explosion || __4 == HitType.Fire);

                    string weaponOrSource;
                    string hitDetail;
                    bool onlyIfNoRecentDetailed;
                    int distMeters;
                    bool isSelf;
                    bool isFF;

                    if (insideExplosion)
                    {
                        for (int i = 0; i < _expPreStateTop; i++)
                        {
                            if (_expPreStates[i].Ptr == victim.Pointer)
                                _expPreStates[i].HitPartRecorded = true;
                        }

                        Soldier expAttacker = _activeExplosionAttacker;
                        isSelf = expAttacker != null && expAttacker.Pointer == victim.Pointer;
                        isFF = expAttacker != null
                            ? (!isSelf &&
                               !string.IsNullOrEmpty(expAttacker.faction) &&
                               !string.IsNullOrEmpty(victim.faction) &&
                               AiCore.AreSameSideFactions(expAttacker.faction, victim.faction))
                            : (!string.IsNullOrEmpty(__3) &&
                               !string.IsNullOrEmpty(victim.faction) &&
                               AiCore.AreSameSideFactions(__3, victim.faction));

                        Vector3 vPos = victim.GetCenterOfUnit();
                        float blastDist = Vector3.Distance(_activeExplosionPos, vPos);
                        if (_activeExplosionIsArtillery || expAttacker == null || isSelf)
                        {
                            distMeters = Mathf.RoundToInt(blastDist);
                        }
                        else if (_activeImpactHasContext && _activeImpactStartPos.sqrMagnitude > 1f)
                        {
                            distMeters = Mathf.RoundToInt(Vector3.Distance(_activeImpactStartPos, vPos));
                        }
                        else
                        {
                            distMeters = Mathf.RoundToInt(Vector3.Distance(expAttacker.GetCenterOfUnit(), vPos));
                        }

                        weaponOrSource = _activeExplosionWeaponName;
                        hitDetail = __4 == HitType.Fire
                            ? "Burns"
                            : (blastDist <= Mathf.Max(2.5f, _activeExplosionRadius * 0.45f)
                                ? "Direct Blast"
                                : $"Shrapnel ({FormatBodyPart(__instance.bodyPart)})");
                        onlyIfNoRecentDetailed = false;
                    }
                    else
                    {
                        switch (__4)
                        {
                            case HitType.Fire:
                                weaponOrSource = !string.IsNullOrEmpty(_activeExplosionWeaponName) ? _activeExplosionWeaponName : "Fire / Incendiary";
                                hitDetail = "Burns";
                                onlyIfNoRecentDetailed = false;
                                break;
                            case HitType.Melee:
                                weaponOrSource = "Melee / Bayonet";
                                hitDetail = FormatBodyPart(__instance.bodyPart);
                                onlyIfNoRecentDetailed = false;
                                break;
                            case HitType.VehicleCollision:
                                weaponOrSource = "Vehicle";
                                hitDetail = "Run Over / Crushed";
                                onlyIfNoRecentDetailed = false;
                                break;
                            case HitType.Explosion:
                                weaponOrSource = !string.IsNullOrEmpty(_activeExplosionWeaponName) ? _activeExplosionWeaponName : "Artillery / Explosion";
                                hitDetail = $"Shrapnel ({FormatBodyPart(__instance.bodyPart)})";
                                onlyIfNoRecentDetailed = true;
                                break;
                            default:
                                weaponOrSource = !string.IsNullOrEmpty(__3) ? $"{__3} Fire" : "Enemy Fire";
                                hitDetail = FormatBodyPart(__instance.bodyPart);
                                onlyIfNoRecentDetailed = true;
                                break;
                        }

                        Vector3 vPos = victim.GetCenterOfUnit();
                        distMeters = (__4 == HitType.Melee || __4 == HitType.VehicleCollision)
                            ? Mathf.RoundToInt(Vector3.Distance(__2, vPos))
                            : 0;
                        isSelf = false;
                        isFF = !string.IsNullOrEmpty(__3) &&
                               !string.IsNullOrEmpty(victim.faction) &&
                               AiCore.AreSameSideFactions(__3, victim.faction);
                    }

                    if (onlyIfNoRecentDetailed &&
                        _lastDamageByVictim.TryGetValue(victim.Pointer, out DamageAttribution recentPrev) &&
                        (Time.time - recentPrev.Timestamp) <= 0.35f)
                    {
                        if (victim.isBleeding && !recentPrev.CausedBleeding)
                        {
                            recentPrev.CausedBleeding = true;
                            _lastDamageByVictim[victim.Pointer] = recentPrev;
                            if (Photon.Pun.PhotonNetwork.InRoom)
                                TrySendRemotePlayerDeathAttribution(victim, in recentPrev);
                        }
                        return;
                    }

                    DamageAttribution hitAttr = new DamageAttribution
                    {
                        IsFriendlyFire = isFF,
                        IsSelfInflicted = isSelf,
                        WeaponOrSource = weaponOrSource,
                        HitDetail = hitDetail,
                        DistanceMeters = Mathf.Max(0, distMeters),
                        Timestamp = Time.time,
                        CausedBleeding = victim.isBleeding
                    };

                    __state.Victim = victim;
                    __state.VictimPtr = victim.Pointer;
                    __state.PreLife = victim.life_total.Value;
                    __state.PreAlive = victim.IsAlive;
                    __state.PreInjured = victim.IsInjured;
                    __state.PreBleeding = victim.isBleeding;
                    __state.HadPrev = _lastDamageByVictim.TryGetValue(victim.Pointer, out __state.PrevAttr);
                    __state.WrittenAttr = hitAttr;
                    __state.DidWrite = true;

                    RecordPlayerDamageAttribution(victim, hitAttr, onlyIfNoRecentDetailed: false, sendRemote: false);
                }
                catch (Exception ex)
                {
                    AiCore.ReportPatchException("Death.HitPart", ex);
                }
                finally
                {
                    HitchProfiler.EndModScope(HitchBucket.DeathHitAttr, profStart);
                }
            }

            public static void Postfix(bool __result, BodyPartHitPatchState __state)
            {
                if (!__state.DidWrite || __state.VictimPtr == IntPtr.Zero)
                    return;

                try
                {
                    Soldier v = __state.Victim;
                    bool validObj = v != null && v.Pointer != IntPtr.Zero;
                    bool confirmed = validObj &&
                                     ((__state.PreAlive && !v.IsAlive) ||
                                      (!__state.PreInjured && v.IsInjured) ||
                                      (v.life_total.Value < __state.PreLife) ||
                                      (!__state.PreBleeding && v.isBleeding));

                    if (!confirmed)
                    {
                        if (__state.HadPrev)
                            _lastDamageByVictim[__state.VictimPtr] = __state.PrevAttr;
                        else
                            _lastDamageByVictim.Remove(__state.VictimPtr);
                        return;
                    }

                    DamageAttribution finalAttr = __state.WrittenAttr;
                    if (v.isBleeding && !finalAttr.CausedBleeding)
                    {
                        finalAttr.CausedBleeding = true;
                        if (v.IsAlive || !IsPlayerCharacter(v))
                            _lastDamageByVictim[__state.VictimPtr] = finalAttr;
                    }

                    if (Photon.Pun.PhotonNetwork.InRoom)
                    {
                        TrySendRemotePlayerDeathAttribution(v, in finalAttr);
                    }
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(Soldier), nameof(Soldier.DamageWithAnimation))]
        public static class Soldier_DamageWithAnimation_Patch
        {
            public static void Postfix(Soldier __instance, float __0, BodyPartType __1, HitType __2)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value ||
                        !VisionConfig.EnableDeathScreenAttribution.Value ||
                        __0 <= 0f ||
                        __instance == null ||
                        __instance.Pointer == IntPtr.Zero ||
                        !IsDeathAttributionTarget(__instance))
                    {
                        return;
                    }

                    if (_lastDamageByVictim.TryGetValue(__instance.Pointer, out DamageAttribution prev) &&
                        (Time.time - prev.Timestamp) <= 0.40f)
                    {
                        if (__instance.isBleeding && !prev.CausedBleeding)
                        {
                            prev.CausedBleeding = true;
                            RecordPlayerDamageAttribution(__instance, prev, onlyIfNoRecentDetailed: false);
                        }
                        return;
                    }

                    string weaponOrSource = __2 == HitType.Fire ? "Fire / Incendiary"
                        : __2 == HitType.Melee ? "Melee / Bayonet"
                        : __2 == HitType.VehicleCollision ? "Vehicle"
                        : __2 == HitType.Explosion ? (!string.IsNullOrEmpty(_activeExplosionWeaponName) ? _activeExplosionWeaponName : "Explosion")
                        : "Enemy Fire";
                    string hitDetail = __2 == HitType.VehicleCollision ? "Run Over / Crushed" : FormatBodyPart(__1);

                    RecordPlayerDamageAttribution(
                        __instance,
                        new DamageAttribution
                        {
                            IsFriendlyFire = false,
                            IsSelfInflicted = false,
                            WeaponOrSource = weaponOrSource,
                            HitDetail = hitDetail,
                            DistanceMeters = 0,
                            Timestamp = Time.time,
                            CausedBleeding = __instance.isBleeding
                        },
                        onlyIfNoRecentDetailed: true);
                }
                catch (Exception ex)
                {
                    AiCore.ReportPatchException("Death.DmgAnim", ex);
                }
            }
        }

        private static void FormatAndApplyDeathAttribution(
            DeathPanel dp,
            Soldier victim,
            string vanillaReason,
            string yearLineWithAge,
            int yearFontSize,
            bool hasAttr,
            in DamageAttribution attr)
        {
            if (victim == null || victim.Pointer == IntPtr.Zero)
                return;

            bool staleVehicleHit = hasAttr &&
                                   string.Equals(attr.HitDetail, "Vehicle Hit", StringComparison.Ordinal) &&
                                   (Time.time - attr.Timestamp) > 8.0f;
            float hitAge = hasAttr ? Mathf.Max(0f, Time.time - attr.Timestamp) : 999f;
            bool isBleedAttribution = hasAttr && (victim.isBleeding || attr.CausedBleeding);
            bool isIncapOrSevere = hasAttr && (victim.IsInjured || IsSevereKillDetail(attr.HitDetail));
            float maxAttrAge = isBleedAttribution ? 90.0f : (isIncapOrSevere ? 20.0f : 8.0f);

            string causeLine;
            string subColor = "#D8D2C2";
            bool isFriendlyFire = false;
            bool isSelfInflicted = false;

            if (!hasAttr || hitAge > maxAttrAge || staleVehicleHit)
            {
                string cleanedReason = CleanDisplayLabel(vanillaReason);
                if (!string.IsNullOrEmpty(cleanedReason))
                {
                    causeLine = cleanedReason;
                }
                else if (victim.IsOnVehicle() && victim.GetCurrentVehicle() != null)
                {
                    Vehicle deadVeh = victim.GetCurrentVehicle();
                    string vName = !string.IsNullOrWhiteSpace(deadVeh.name)
                        ? CleanUnityObjectName(deadVeh.name)
                        : (deadVeh.IsAirVehicle() ? "Aircraft" : "Vehicle");
                    string vDetail = deadVeh.IsAirVehicle() ? "Aircraft Crash" : "Vehicle Destroyed";
                    causeLine = $"{vName}  \u2022  {vDetail}";
                }
                else if (victim.isBleeding)
                {
                    causeLine = "Bled out from Wounds";
                }
                else if (AiCore.TryGetActiveWaterLevel(out float wl) &&
                         victim.transform != null &&
                         victim.transform.position.y < wl - 0.5f)
                {
                    causeLine = "Drowned";
                }
                else
                {
                    causeLine = "Killed in Action";
                }
            }
            else
            {
                isFriendlyFire = attr.IsFriendlyFire;
                isSelfInflicted = attr.IsSelfInflicted;
                bool delayedBleedOut = hitAge > 3.0f && (victim.isBleeding || attr.CausedBleeding);

                string prefixTag = "";
                if (isSelfInflicted)
                {
                    prefixTag = "[SELF-INFLICTED] ";
                    subColor = "#FFB347";
                }
                else if (isFriendlyFire)
                {
                    prefixTag = "[FRIENDLY FIRE] ";
                    subColor = "#FF6666";
                }

                List<string> detailParts = new List<string>(4);
                if (delayedBleedOut)
                {
                    detailParts.Add("Bled out");
                }

                string cleanWep = CleanDisplayLabel(attr.WeaponOrSource);
                if (!string.IsNullOrEmpty(cleanWep))
                    detailParts.Add(cleanWep);

                string cleanHit = CleanDisplayLabel(attr.HitDetail);
                if (!string.IsNullOrEmpty(cleanHit) &&
                    !string.Equals(cleanWep, cleanHit, StringComparison.OrdinalIgnoreCase))
                {
                    detailParts.Add(cleanHit);
                }

                if (!attr.IsSelfInflicted || attr.DistanceMeters > 0)
                {
                    detailParts.Add($"{attr.DistanceMeters}m");
                }

                causeLine = detailParts.Count > 0
                    ? prefixTag + string.Join("  \u2022  ", detailParts)
                    : (prefixTag + "Killed in Action").Trim();
            }

            bool shownOnNativeDeathPanel = false;
            Text yearLabel = dp != null ? dp.text_year : null;
            if (yearLabel != null)
            {
                yearLabel.text = !string.IsNullOrEmpty(causeLine)
                    ? $"<size={yearFontSize}>{yearLineWithAge}</size>\n<size=26><color={subColor}>{causeLine}</color></size>"
                    : $"<size={yearFontSize}>{yearLineWithAge}</size>";
                shownOnNativeDeathPanel = true;
            }

            if (shownOnNativeDeathPanel)
            {
                ActiveDeathAttributionExpireTime = 0f;
                ActiveDeathAttributionText = null;
            }
            else
            {
                ActiveDeathAttributionText = causeLine;
                ActiveDeathAttributionIsFF = isFriendlyFire;
                ActiveDeathAttributionIsSelf = isSelfInflicted;
                ActiveDeathAttributionStartTime = Time.unscaledTime;
                ActiveDeathAttributionExpireTime = Time.unscaledTime + 8.5f;
                ActiveDeathAttributionVictimPtr = victim.Pointer;
            }
        }

        [HarmonyPatch(typeof(DeathPanel), nameof(DeathPanel.ShowDeath))]
        public static class DeathPanel_ShowDeath_Patch
        {
            public static void Postfix(Soldier __0, string __1, bool __2)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value || !VisionConfig.EnableDeathScreenAttribution.Value || __0 == null)
                        return;

                    DeathPanel dp = DeathPanel.instance;

                    // If the soldier surrendered or is still alive (showDeath == false), do NOT display cause-of-death attribution
                    if (!__2 || __0.HasSurrended() || __0.IsAlive)
                    {
                        _lastDamageByVictim.Remove(__0.Pointer);
                        _deathPanelVictimViewId = 0;
                        _deathPanelVictimRef = null;
                        ActiveDeathAttributionExpireTime = 0f;
                        ActiveDeathAttributionText = null;

                        if (dp != null)
                            ActivateInstantDeathPanel(dp, __0.Pointer);

                        return;
                    }

                    Text yearLabel = dp != null ? dp.text_year : null;
                    string yearLineWithAge = "";
                    int yearFontSize = 38;

                    if (yearLabel != null)
                    {
                        yearFontSize = yearLabel.fontSize;
                        if (yearLabel.resizeTextForBestFit && yearLabel.resizeTextMaxSize > yearFontSize)
                            yearFontSize = yearLabel.resizeTextMaxSize;
                        if (yearFontSize < 38)
                            yearFontSize = 38;

                        yearLabel.resizeTextForBestFit = false;
                        yearLabel.fontSize = yearFontSize;
                        yearLabel.supportRichText = true;
                        yearLabel.horizontalOverflow = HorizontalWrapMode.Overflow;
                        yearLabel.verticalOverflow = VerticalWrapMode.Overflow;

                        string curYearText = yearLabel.text ?? "";
                        int nlIdx = curYearText.IndexOf('\n');
                        string baseYearLine = nlIdx >= 0 ? curYearText.Substring(0, nlIdx) : curYearText;
                        yearLineWithAge = FormatYearLineWithAge(baseYearLine);
                    }

                    bool hasAttr = _lastDamageByVictim.TryGetValue(__0.Pointer, out DamageAttribution attr);
                    _lastDamageByVictim.Remove(__0.Pointer);

                    SyncSoldier syn = __0.soldierSyncher;
                    int viewId = syn != null ? syn.ViewID : 0;
                    if (viewId > 0 && _pendingRemoteDeathByViewId.TryGetValue(viewId, out DamageAttribution remoteAttr))
                    {
                        _pendingRemoteDeathByViewId.Remove(viewId);
                        if (!hasAttr || remoteAttr.Timestamp > attr.Timestamp || (Time.time - attr.Timestamp) > 20f)
                        {
                            attr = remoteAttr;
                            hasAttr = true;
                        }
                    }

                    _deathPanelVictimViewId = viewId;
                    _deathPanelVictimRef = __0;
                    _deathPanelBaseYearLine = yearLineWithAge;
                    _deathPanelYearFontSize = yearFontSize;
                    _deathPanelVanillaReason = __1;

                    if (dp != null)
                        ActivateInstantDeathPanel(dp, __0.Pointer);

                    FormatAndApplyDeathAttribution(dp, __0, __1, yearLineWithAge, yearFontSize, hasAttr, in attr);
                }
                catch { }
            }
        }

        private static void ActivateInstantDeathPanel(DeathPanel dp, IntPtr victimPtr)
        {
            if (dp == null)
                return;

            DisableDeathPanelRaycasts(dp);

            Animation anim = dp.GetComponent<Animation>();
            if (anim != null)
            {
                if (anim.GetStateCount() > 0)
                {
                    AnimationState state = anim.GetStateAtIndex(0);
                    if (state != null && state.length > 0f)
                    {
                        state.enabled = true;
                        state.weight = 1f;
                        state.time = state.length * 0.72f;
                        anim.Sample();
                    }
                }
                anim.Stop();
            }

            _deathPanelShowStartUnscaledTime = Time.unscaledTime;
            _deathPanelHoldUntilUnscaledTime = Time.unscaledTime + 5.5f;
            _deathPanelCleanupUntilUnscaledTime = Time.unscaledTime + 15.0f;
            _deathPanelVictimPtr = victimPtr;

            ApplyDeathPanelAlpha(dp, 1.0f);
        }

        private static void DisableDeathPanelRaycasts(DeathPanel dp)
        {
            if (dp == null)
                return;

            if (dp.text_reason != null)
                dp.text_reason.raycastTarget = false;
            if (dp.text_name != null)
                dp.text_name.raycastTarget = false;
            if (dp.text_year != null)
                dp.text_year.raycastTarget = false;

            Graphic rootGraphic = dp.GetComponent<Graphic>();
            if (rootGraphic != null)
                rootGraphic.raycastTarget = false;

            CanvasGroup cg = dp.GetComponent<CanvasGroup>();
            if (cg != null)
            {
                cg.blocksRaycasts = false;
                cg.interactable = false;
            }
        }

        private static void FinishInstantDeathPanel(DeathPanel dp)
        {
            _deathPanelCleanupUntilUnscaledTime = 0f;
            _deathPanelVictimViewId = 0;
            _deathPanelVictimRef = null;
            if (dp == null)
                return;

            Animation anim = dp.GetComponent<Animation>();
            if (anim != null)
            {
                if (anim.GetStateCount() > 0)
                {
                    AnimationState state = anim.GetStateAtIndex(0);
                    if (state != null)
                    {
                        state.enabled = true;
                        state.weight = 1f;
                        state.normalizedTime = 1.0f;
                        anim.Sample();
                    }
                }
                anim.Stop();
            }

            ApplyDeathPanelAlpha(dp, 0.0f);
            DisableDeathPanelRaycasts(dp);
        }

        public static void MaintainInstantDeathPanel()
        {
            if (_deathPanelCleanupUntilUnscaledTime <= 0f)
                return;

            float now = Time.unscaledTime;
            DeathPanel dp = DeathPanel.instance;
            if (dp == null || now > _deathPanelCleanupUntilUnscaledTime)
            {
                FinishInstantDeathPanel(dp);
                return;
            }

            DisableDeathPanelRaycasts(dp);

            Animation anim = dp.GetComponent<Animation>();
            if (anim != null && anim.isPlaying)
                anim.Stop();

            if (now < _deathPanelHoldUntilUnscaledTime)
            {
                float elapsed = now - _deathPanelShowStartUnscaledTime;

                if (elapsed > 0.35f)
                {
                    Soldier cur = Soldier.CurrentControlledSoldierOrNull();
                    if (cur != null && cur.IsAlive && cur.Pointer != _deathPanelVictimPtr)
                    {
                        // Player has taken control of a new living soldier -> dismiss immediately
                        _deathPanelHoldUntilUnscaledTime = now;
                    }
                    else if (elapsed > 1.5f && DeathPanel.IsSkipping())
                    {
                        // Player explicitly held Skip after 1.5s -> fade out smoothly
                        _deathPanelHoldUntilUnscaledTime = Mathf.Min(_deathPanelHoldUntilUnscaledTime, now + 0.35f);
                    }
                }
            }

            if (now < _deathPanelHoldUntilUnscaledTime)
            {
                float remaining = _deathPanelHoldUntilUnscaledTime - now;
                float alpha = remaining < 0.45f ? Mathf.Clamp01(remaining / 0.45f) : 1.0f;
                ApplyDeathPanelAlpha(dp, alpha);
            }
            else
            {
                FinishInstantDeathPanel(dp);
            }
        }

        private static void ApplyDeathPanelAlpha(DeathPanel dp, float alpha)
        {
            if (dp == null)
                return;

            CanvasGroup cg = dp.GetComponent<CanvasGroup>();
            if (cg != null)
                cg.alpha = alpha;

            SetTextAlpha(dp.text_reason, alpha);
            SetTextAlpha(dp.text_name, alpha);
            SetTextAlpha(dp.text_year, alpha);
        }

        private static void SetTextAlpha(Text txt, float alpha)
        {
            if (txt == null)
                return;

            Color c = txt.color;
            if (Mathf.Abs(c.a - alpha) > 0.002f)
            {
                c.a = alpha;
                txt.color = c;
            }
        }
    }
}
