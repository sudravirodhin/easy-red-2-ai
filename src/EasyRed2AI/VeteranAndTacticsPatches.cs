using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace EasyRed2HumanAI
{
    public static class VeteranAndTacticsPatches
    {
        public enum SoldierPersonality : byte
        {
            None = 0,
            // 1. Squad Leader / Officer
            Leader_Spearhead = 1,
            Leader_Tactician = 2,
            Leader_OldGuard = 3,
            // 2. Assaulter / Pointman (SMG, Assault Rifle, Shotgun, Flamethrower)
            Assaulter_Shock = 4,
            Assaulter_Breacher = 5,
            Assaulter_Skirmisher = 6,
            // 3. Standard Rifleman
            Rifleman_Regular = 7,
            Rifleman_Cautious = 8,
            Rifleman_Grenadier = 9,
            // 4. Machine Gunner
            Gunner_Anchor = 10,
            Gunner_AssaultMG = 11,
            Gunner_Overwatch = 12,
            // 5. Marksman / Sniper
            Marksman_Deadeye = 13,
            Marksman_Stalker = 14,
            Marksman_Spotter = 15,
            // 6. Anti-Tank / Sapper
            AT_TankHunter = 16,
            AT_Ambusher = 17,
            AT_Demolitionist = 18,
            // 7. Field Medic
            Medic_Heroic = 19,
            Medic_Combat = 20,
            Medic_Triage = 21,
            // 8. Tank & Armored Vehicle Crew
            TankCrew_Brawler = 22,
            TankCrew_Sniper = 23,
            TankCrew_Support = 24,
            // 9. Light Vehicle / Transport / Halftrack / AA Crew
            LightCrew_Outrider = 25,
            LightCrew_Escort = 26,
            LightCrew_FlakWatch = 27,
            // 10. Aircraft Pilot
            Pilot_Ace = 28,
            Pilot_Daredevil = 29,
            Pilot_Disciplined = 30
        }

        private struct SoldierRecord
        {
            public float SpawnTime;
            public float BonusSeconds;
            public int PromotedTier;
            public Rank BaseRank;
            public string BaseName;
            public SoldierPersonality Personality;
            public byte RoleCategory;
            public bool NameInitialized;
        }

        // Per-soldier state keyed by IL2CPP IntPtr
        private static readonly Dictionary<IntPtr, SoldierRecord> _records = new Dictionary<IntPtr, SoldierRecord>(512);
        private static readonly Dictionary<IntPtr, float> _stanceDropHoldFireUntil = new Dictionary<IntPtr, float>(256);
        private static readonly Dictionary<IntPtr, float> _nextStanceDropAllowed = new Dictionary<IntPtr, float>(256);
        private static readonly Dictionary<IntPtr, float> _combatStanceHoldUntil = new Dictionary<IntPtr, float>(256);
        private static readonly Dictionary<IntPtr, float> _combatCrouchLockUntil = new Dictionary<IntPtr, float>(256);
        private static readonly Dictionary<IntPtr, float> _noProneUntil = new Dictionary<IntPtr, float>(256);
        private static readonly Dictionary<IntPtr, float> _nextWallCheckTime = new Dictionary<IntPtr, float>(256);
        private static readonly Dictionary<IntPtr, float> _wallBlockedUntil = new Dictionary<IntPtr, float>(256);
        private static readonly Dictionary<IntPtr, float> _peekRestoreTime = new Dictionary<IntPtr, float>(256);
        private static readonly Dictionary<IntPtr, float> _nextPeekAllowedTime = new Dictionary<IntPtr, float>(256);
        private static readonly Dictionary<IntPtr, float> _nextFFCheckTime = new Dictionary<IntPtr, float>(256);
        private static readonly Dictionary<IntPtr, float> _ffBlockedUntil = new Dictionary<IntPtr, float>(256);
        private static readonly Dictionary<IntPtr, float> _nextAntiStackTime = new Dictionary<IntPtr, float>(256);
        private static readonly Dictionary<IntPtr, float> _nextVaultCheckTime = new Dictionary<IntPtr, float>(256);
        private static readonly Dictionary<IntPtr, float> _nextLiberateCheckTime = new Dictionary<IntPtr, float>(256);
        private static readonly Dictionary<IntPtr, float> _bleedCoverDeadline = new Dictionary<IntPtr, float>(128);
        private static readonly Dictionary<IntPtr, float> _nextBipodSeekTime = new Dictionary<IntPtr, float>(128);
        private static readonly Dictionary<IntPtr, float> _nextBipodDeployCheckTime = new Dictionary<IntPtr, float>(128);
        private static readonly Dictionary<IntPtr, float> _bipodStandUntil = new Dictionary<IntPtr, float>(128);
        private static readonly Dictionary<IntPtr, float> _nextWallSideCheckTime = new Dictionary<IntPtr, float>(256);
        private static readonly Dictionary<IntPtr, float> _nextWallSidestepTime = new Dictionary<IntPtr, float>(256);
        private static readonly Dictionary<IntPtr, float> _nextTankDisciplineCountTime = new Dictionary<IntPtr, float>(256);
        private static readonly Dictionary<IntPtr, float> _poseLockUntil = new Dictionary<IntPtr, float>(256);
        private static readonly Dictionary<IntPtr, float> _nextBayonetCheckTime = new Dictionary<IntPtr, float>(256);
        private static readonly Dictionary<IntPtr, float> _nextMeleeAttemptTime = new Dictionary<IntPtr, float>(256);
        private static readonly Dictionary<IntPtr, float> _nextMeleeStepTime = new Dictionary<IntPtr, float>(256);
        private static readonly Dictionary<IntPtr, float> _nextPromotionCheckTime = new Dictionary<IntPtr, float>(256);
        private static readonly Dictionary<IntPtr, float> _repairTaskStartTime = new Dictionary<IntPtr, float>(64);
        private static readonly HashSet<IntPtr> _countedRepairTasks = new HashSet<IntPtr>();
        private static readonly HashSet<IntPtr> _bayonetCheckedSoldiers = new HashSet<IntPtr>();
        private static readonly Dictionary<IntPtr, Vehicle> _turretVehicleCache = new Dictionary<IntPtr, Vehicle>(64);
        private static readonly Dictionary<long, bool> _coverExposureCache = new Dictionary<long, bool>(1024);

        private struct WatchWindowCache
        {
            public Vector3 Pos;
            public float BaseAngleDeg;
            public Vector3 ResultDir;
            public float ExpireTime;
        }

        private static readonly Dictionary<IntPtr, WatchWindowCache> _watchWindowCache = new Dictionary<IntPtr, WatchWindowCache>(256);
        private static int _cachedSolidEnvMask;
        private static float _nextAllowedBotRpcTime;
        private static float _nextCoverCacheClearTime;

        private struct VehicleAimCache
        {
            public IntPtr VehiclePtr;
            public float ExpireTime;
            public bool CanEngage;
            public bool HasCustomAimPoint;
            public Vector3 CustomAimPoint;
        }

        private static readonly Dictionary<IntPtr, VehicleAimCache> _vehicleAimCache = new Dictionary<IntPtr, VehicleAimCache>(128);

        private struct ThreatSectorCache
        {
            public float ExpireTime;
            public Vector3 WatchDir;
            public bool HasActiveContact;
        }

        private static readonly Dictionary<IntPtr, ThreatSectorCache> _threatSectorCache = new Dictionary<IntPtr, ThreatSectorCache>(256);
        private static readonly HashSet<IntPtr> _isTurningToWatchSector = new HashSet<IntPtr>();

        internal struct DamageAttribution
        {
            public string AttackerName;
            public bool IsFriendlyFire;
            public bool IsSelfInflicted;
            public string WeaponOrSource;
            public string HitDetail;
            public int DistanceMeters;
            public float Timestamp;
        }

        private static readonly Dictionary<IntPtr, DamageAttribution> _lastDamageByVictim = new Dictionary<IntPtr, DamageAttribution>(32);
        private static string _activeExplosionWeaponName;
        private static bool _activeExplosionIsArtillery;

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

        private static float _nextStateCleanupTime;

        // Procedural 32x32 chevron textures for Tier 1 (^), Tier 2 (^^), Tier 3 (^^^ ★)
        private static Texture2D _texVet1;
        private static Texture2D _texVet2;
        private static Texture2D _texVet3;

        // Telemetry counters for debug / toast feedback
        public static int ActiveVeteransCount;
        public static int FieldPromotionsCount;
        public static int SelfPreservationDrops;
        public static int BipodsDeployed;
        public static int WallsBlocked;
        public static int AutoPeeksTriggered;
        public static int FriendlyFirePrevented;
        public static int AntiStackNudges;
        public static int WallsVaulted;
        public static int ComradesLiberated;
        public static int ShelteredRepairsCount;
        public static int WallSideRepositionsCount;
        public static int ThreatSectorOrientationsCount;
        public static int UselessTankFirePrevented;
        public static int ExposedCrewTargeted;
        public static int MeleeAttacksTriggered;

        // =====================================================================
        // Veteran Progression & Role Personality System (3 Archetypes per Role)
        // =====================================================================
        private static int GetDeterministicHash(IntPtr ptr, int seed)
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

        private static byte DetermineRoleCategory(Soldier s)
        {
            if (s == null)
                return 0;

            if (s.IsOnVehicle())
            {
                Vehicle v = s.GetCurrentVehicle();
                if (v != null)
                {
                    if (v.TryCast<VehiclePlane>() != null)
                        return 10; // Aircraft Pilot / Aircrew
                    if (v.TryCast<VehicleTank>() != null)
                        return 8;  // Armored Tank / Tracked Vehicle Crew
                }
                return 9;          // Light / Wheeled / Halftrack / AA Crew
            }

            if (s.joinedSquad != null && s.joinedSquad.Leader != null && s.joinedSquad.Leader.Pointer == s.Pointer)
                return 1; // Squad Leader / Officer

            if (s.hasSyringe)
                return 7; // Field Medic

            if (s.isAtUnit || s.isSapper)
                return 6; // Anti-Tank / Sapper

            if (s.isMarksMan)
                return 5; // Marksman / Sniper

            if (s.hasMg)
                return 4; // Machine Gunner

            GenericGun gun = s.GetHeldGun();
            if (gun != null)
            {
                float dmgRange = gun.DamageRange();
                if (!s.hasMg && dmgRange > 1f && dmgRange < 185f)
                    return 2; // Assaulter / Pointman (SMG, Assault Rifle, Shotgun, Flamethrower)
            }

            return 3; // Standard Rifleman
        }

        private static SoldierPersonality RollPersonalityForRole(Soldier s, byte roleCategory)
        {
            int h = GetDeterministicHash(s.Pointer, s._randomSeed_k__BackingField);
            int roll100 = h % 100;

            switch (roleCategory)
            {
                case 1: // Squad Leader
                    if (roll100 < 35) return SoldierPersonality.Leader_Spearhead;
                    if (roll100 < 75) return SoldierPersonality.Leader_Tactician;
                    return SoldierPersonality.Leader_OldGuard;

                case 2: // Assaulter / Pointman
                    if (roll100 < 40) return SoldierPersonality.Assaulter_Shock;
                    if (roll100 < 75) return SoldierPersonality.Assaulter_Breacher;
                    return SoldierPersonality.Assaulter_Skirmisher;

                case 3: // Standard Rifleman
                    if (s.hasGrenadeLauncher && roll100 < 65)
                        return SoldierPersonality.Rifleman_Grenadier;
                    if (roll100 < 40) return SoldierPersonality.Rifleman_Regular;
                    if (roll100 < 75) return SoldierPersonality.Rifleman_Cautious;
                    return SoldierPersonality.Rifleman_Grenadier;

                case 4: // Machine Gunner
                    if (roll100 < 45) return SoldierPersonality.Gunner_Anchor;
                    if (roll100 < 75) return SoldierPersonality.Gunner_AssaultMG;
                    return SoldierPersonality.Gunner_Overwatch;

                case 5: // Marksman / Sniper
                    if (roll100 < 40) return SoldierPersonality.Marksman_Deadeye;
                    if (roll100 < 75) return SoldierPersonality.Marksman_Stalker;
                    return SoldierPersonality.Marksman_Spotter;

                case 6: // Anti-Tank / Sapper
                    if (roll100 < 40) return SoldierPersonality.AT_TankHunter;
                    if (roll100 < 75) return SoldierPersonality.AT_Ambusher;
                    return SoldierPersonality.AT_Demolitionist;

                case 7: // Field Medic
                    if (roll100 < 35) return SoldierPersonality.Medic_Heroic;
                    if (roll100 < 75) return SoldierPersonality.Medic_Combat;
                    return SoldierPersonality.Medic_Triage;

                case 8: // Tank & Armored Vehicle Crew
                    if (roll100 < 35) return SoldierPersonality.TankCrew_Brawler;
                    if (roll100 < 70) return SoldierPersonality.TankCrew_Sniper;
                    return SoldierPersonality.TankCrew_Support;

                case 9: // Light Vehicle / Halftrack / AA Crew
                    if (roll100 < 35) return SoldierPersonality.LightCrew_Outrider;
                    if (roll100 < 75) return SoldierPersonality.LightCrew_Escort;
                    return SoldierPersonality.LightCrew_FlakWatch;

                case 10: // Aircraft Pilot
                    if (roll100 < 35) return SoldierPersonality.Pilot_Ace;
                    if (roll100 < 70) return SoldierPersonality.Pilot_Daredevil;
                    return SoldierPersonality.Pilot_Disciplined;

                default:
                    return SoldierPersonality.Rifleman_Regular;
            }
        }

        private static SoldierRecord GetOrCreateRecord(Soldier s)
        {
            IntPtr ptr = s.Pointer;
            if (!_records.TryGetValue(ptr, out SoldierRecord rec))
            {
                string rawName = s.name_surname ?? "";
                int bracketIdx = rawName.IndexOf(" [", StringComparison.Ordinal);
                if (bracketIdx >= 0)
                    rawName = rawName.Substring(0, bracketIdx);

                byte roleCat = DetermineRoleCategory(s);
                rec = new SoldierRecord
                {
                    SpawnTime = Time.time,
                    BonusSeconds = 0f,
                    PromotedTier = 0,
                    BaseRank = s.rank,
                    BaseName = rawName,
                    RoleCategory = roleCat,
                    Personality = RollPersonalityForRole(s, roleCat)
                };
                _records[ptr] = rec;
            }
                else
                {
                    byte currentRoleCat = DetermineRoleCategory(s);
                    if (currentRoleCat != rec.RoleCategory)
                    {
                        rec.RoleCategory = currentRoleCat;
                        rec.Personality = RollPersonalityForRole(s, currentRoleCat);
                        rec.NameInitialized = false;
                        _records[ptr] = rec;
                    }
                }
                return rec;
            }

        public static SoldierPersonality GetPersonality(Soldier s)
        {
            if (s == null || s.Pointer == IntPtr.Zero || !VisionConfig.EnableMod.Value || !VisionConfig.EnableRolePersonalities.Value)
                return SoldierPersonality.None;

            SoldierRecord rec = GetOrCreateRecord(s);
            bool isVehOrAir = rec.RoleCategory >= 8;
            if (isVehOrAir && !VisionConfig.EnableVehicleAndPilotPersonalities.Value)
                return SoldierPersonality.None;
            if (!isVehOrAir && !VisionConfig.EnableInfantryPersonalities.Value)
                return SoldierPersonality.None;

            return rec.Personality;
        }

        public static SoldierPersonality GetVehicleCrewPersonality(Vehicle v)
        {
            if (v == null || v.Pointer == IntPtr.Zero || !VisionConfig.EnableMod.Value || !VisionConfig.EnableRolePersonalities.Value || !VisionConfig.EnableVehicleAndPilotPersonalities.Value)
                return SoldierPersonality.None;

            var seats = v.seats;
            if (seats != null)
            {
                int len = seats.Length;
                for (int i = 0; i < len; i++)
                {
                    Seats seat = seats[i];
                    if (seat != null && seat.unitSet != null)
                    {
                        Soldier crew = seat.unitSet.TryCast<Soldier>();
                        if (crew != null && crew.IsAlive)
                            return GetPersonality(crew);
                    }
                }
            }

            int roll100 = GetDeterministicHash(v.Pointer, 73) % 100;
            if (v.TryCast<VehiclePlane>() != null)
            {
                if (roll100 < 35) return SoldierPersonality.Pilot_Ace;
                if (roll100 < 70) return SoldierPersonality.Pilot_Daredevil;
                return SoldierPersonality.Pilot_Disciplined;
            }
            if (v.TryCast<VehicleTank>() != null)
            {
                if (roll100 < 35) return SoldierPersonality.TankCrew_Brawler;
                if (roll100 < 70) return SoldierPersonality.TankCrew_Sniper;
                return SoldierPersonality.TankCrew_Support;
            }
            if (roll100 < 35) return SoldierPersonality.LightCrew_Outrider;
            if (roll100 < 75) return SoldierPersonality.LightCrew_Escort;
            return SoldierPersonality.LightCrew_FlakWatch;
        }

        public static string GetPersonalityTag(SoldierPersonality p)
        {
            switch (p)
            {
                case SoldierPersonality.Leader_Spearhead: return "Spearhead";
                case SoldierPersonality.Leader_Tactician: return "Tactician";
                case SoldierPersonality.Leader_OldGuard: return "Old Guard";
                case SoldierPersonality.Assaulter_Shock: return "Shock";
                case SoldierPersonality.Assaulter_Breacher: return "Breacher";
                case SoldierPersonality.Assaulter_Skirmisher: return "Skirmisher";
                case SoldierPersonality.Rifleman_Regular: return "Regular";
                case SoldierPersonality.Rifleman_Cautious: return "Cautious";
                case SoldierPersonality.Rifleman_Grenadier: return "Grenadier";
                case SoldierPersonality.Gunner_Anchor: return "Anchor";
                case SoldierPersonality.Gunner_AssaultMG: return "Assault MG";
                case SoldierPersonality.Gunner_Overwatch: return "Overwatch";
                case SoldierPersonality.Marksman_Deadeye: return "Deadeye";
                case SoldierPersonality.Marksman_Stalker: return "Stalker";
                case SoldierPersonality.Marksman_Spotter: return "Spotter";
                case SoldierPersonality.AT_TankHunter: return "Tank Hunter";
                case SoldierPersonality.AT_Ambusher: return "Ambusher";
                case SoldierPersonality.AT_Demolitionist: return "Demo";
                case SoldierPersonality.Medic_Heroic: return "Heroic";
                case SoldierPersonality.Medic_Combat: return "Combat Medic";
                case SoldierPersonality.Medic_Triage: return "Triage";
                case SoldierPersonality.TankCrew_Brawler: return "Brawler Crew";
                case SoldierPersonality.TankCrew_Sniper: return "Sniper Crew";
                case SoldierPersonality.TankCrew_Support: return "Support Crew";
                case SoldierPersonality.LightCrew_Outrider: return "Outrider";
                case SoldierPersonality.LightCrew_Escort: return "Escort";
                case SoldierPersonality.LightCrew_FlakWatch: return "Flak Watch";
                case SoldierPersonality.Pilot_Ace: return "Ace";
                case SoldierPersonality.Pilot_Daredevil: return "Daredevil";
                case SoldierPersonality.Pilot_Disciplined: return "Disciplined";
                default: return "";
            }
        }

        public static string BuildSoldierBracketTag(Soldier s, bool includeVet, bool includePersonality, bool includeStarUnicode)
        {
            if (s == null) return "";
            int vetTier = includeVet ? GetVeteranTier(s) : 0;
            string vetStr = "";
            if (vetTier == 1) vetStr = "Vet I";
            else if (vetTier == 2) vetStr = "Vet II";
            else if (vetTier >= 3) vetStr = includeStarUnicode ? "Vet III \u2605" : "Vet III";

            string persStr = "";
            if (includePersonality && VisionConfig.EnableRolePersonalities.Value)
            {
                SoldierPersonality p = GetPersonality(s);
                if (p == SoldierPersonality.Rifleman_Cautious && VisionConfig.VeterancyCuresSkittishness.Value && vetTier >= 2)
                    persStr = "Composed";
                else
                    persStr = GetPersonalityTag(p);
            }

            if (!string.IsNullOrEmpty(vetStr) && !string.IsNullOrEmpty(persStr))
                return $" [{vetStr} \u2022 {persStr}]";
            if (!string.IsNullOrEmpty(vetStr))
                return $" [{vetStr}]";
            if (!string.IsNullOrEmpty(persStr))
                return $" [{persStr}]";
            return "";
        }

        private static float GetEffectiveProneSuppressionThreshold(Soldier s, float vet01)
        {
            float threshold = 35f;
            SoldierPersonality p = GetPersonality(s);
            switch (p)
            {
                case SoldierPersonality.Assaulter_Shock:
                    threshold = 68f;
                    break;
                case SoldierPersonality.AT_Demolitionist:
                case SoldierPersonality.Gunner_Anchor:
                    threshold = 60f;
                    break;
                case SoldierPersonality.Assaulter_Breacher:
                case SoldierPersonality.Medic_Heroic:
                case SoldierPersonality.Leader_Spearhead:
                    threshold = 50f;
                    break;
                case SoldierPersonality.Assaulter_Skirmisher:
                case SoldierPersonality.Gunner_AssaultMG:
                    threshold = 45f;
                    break;
                case SoldierPersonality.Medic_Triage:
                    threshold = 20f;
                    break;
                case SoldierPersonality.Rifleman_Cautious:
                    threshold = VisionConfig.VeterancyCuresSkittishness.Value
                        ? Mathf.Lerp(18f, 36f, vet01)
                        : 18f;
                    break;
            }

            // Squad Leader Spearhead Aura (+12 suppression grit when leader is alive within 22m)
            if (s != null && s.joinedSquad != null && s.joinedSquad.Leader != null &&
                s.joinedSquad.Leader.IsAlive && s.joinedSquad.Leader.Pointer != s.Pointer)
            {
                Soldier leader = s.joinedSquad.Leader;
                if (GetPersonality(leader) == SoldierPersonality.Leader_Spearhead &&
                    (leader.transform.position - s.transform.position).sqrMagnitude <= 22f * 22f)
                {
                    threshold += 12f;
                }
            }

            return threshold;
        }

        private static void GetEffectivePeekParameters(Soldier s, float vet01, out float maxPeekSuppression, out float duckBackSuppression, out float peekCooldown)
        {
            maxPeekSuppression = 25f;
            duckBackSuppression = 30f;
            peekCooldown = 3.0f;

            SoldierPersonality p = GetPersonality(s);
            switch (p)
            {
                case SoldierPersonality.Assaulter_Breacher:
                case SoldierPersonality.Assaulter_Shock:
                case SoldierPersonality.Leader_Spearhead:
                    maxPeekSuppression = 38f;
                    duckBackSuppression = 45f;
                    peekCooldown = 2.0f;
                    break;
                case SoldierPersonality.Rifleman_Cautious:
                case SoldierPersonality.Medic_Triage:
                    float cure = VisionConfig.VeterancyCuresSkittishness.Value ? vet01 : 0f;
                    maxPeekSuppression = Mathf.Lerp(15f, 25f, cure);
                    duckBackSuppression = Mathf.Lerp(19f, 30f, cure);
                    peekCooldown = Mathf.Lerp(6.2f, 3.2f, cure);
                    break;
                case SoldierPersonality.Marksman_Stalker:
                    peekCooldown = 4.2f;
                    break;
            }
        }

        public static float GetVeterancy01(Soldier s)
        {
            if (s == null || s.Pointer == IntPtr.Zero || !VisionConfig.EnableVeteranSystem.Value)
                return 0f;

            SoldierRecord rec = GetOrCreateRecord(s);
            float fullSeconds = Mathf.Max(60f, VisionConfig.VeteranFullMaturitySeconds.Value);
            float effectiveAlive = Mathf.Max(0f, Time.time - rec.SpawnTime) + rec.BonusSeconds;
            return Mathf.Clamp01(effectiveAlive / fullSeconds);
        }

        public static int GetVeteranTier(Soldier s)
        {
            float v = GetVeterancy01(s);
            if (v >= 0.80f) return 3; // 12+ mins (Vet III ★)
            if (v >= 0.50f) return 2; // 7.5+ mins (Vet II)
            if (v >= 0.20f) return 1; // 3.0+ mins (Vet I)
            return 0;
        }

        public static void AddCombatExperienceBonus(Soldier s, float bonusSeconds)
        {
            if (s == null || s.Pointer == IntPtr.Zero || !VisionConfig.EnableVeteranSystem.Value)
                return;

            IntPtr ptr = s.Pointer;
            if (_records.TryGetValue(ptr, out SoldierRecord rec))
            {
                // Cap combat bonus at 25% of full maturity so time survived remains the primary driver
                float maxBonus = Mathf.Max(60f, VisionConfig.VeteranFullMaturitySeconds.Value) * 0.25f;
                rec.BonusSeconds = Mathf.Min(maxBonus, rec.BonusSeconds + bonusSeconds);
                _records[ptr] = rec;
            }
        }

        private static void CheckAndApplyFieldPromotion(Soldier s)
        {
            if (s == null || s.Pointer == IntPtr.Zero)
                return;

            IntPtr ptr = s.Pointer;
            float now = Time.time;
            if (_nextPromotionCheckTime.TryGetValue(ptr, out float nextPromo) && now < nextPromo)
                return;

            float jitter = (GetDeterministicHash(ptr, 19) % 100) * 0.01f;
            _nextPromotionCheckTime[ptr] = now + 4.0f + jitter;

            SoldierRecord rec = GetOrCreateRecord(s);
            int tier = VisionConfig.EnableVeteranSystem.Value ? GetVeteranTier(s) : 0;
            bool tierPromoted = false;

            if (tier > rec.PromotedTier)
            {
                rec.PromotedTier = tier;
                tierPromoted = true;

                if (VisionConfig.EnableVeteranSystem.Value &&
                    VisionConfig.PromoteVeteranRanks.Value &&
                    IsLocallyOwnedBot(s))
                {
                    Rank targetRank = s.rank;
                    if (tier == 1 && targetRank < Rank.corporal)
                        targetRank = Rank.corporal;
                    else if (tier == 2 && targetRank < Rank.seargent)
                        targetRank = Rank.seargent;
                    else if (tier >= 3 && targetRank < Rank.first_seargent)
                        targetRank = Rank.first_seargent;

                    if (targetRank != s.rank)
                    {
                        s.SetRank(targetRank);
                        s.UpdateRanksDecal();
                        FieldPromotionsCount++;
                    }
                }
            }

            if (tierPromoted || !rec.NameInitialized)
            {
                rec.NameInitialized = true;
                _records[ptr] = rec;

                // Keep soldier's name_surname synced with their Veteran and/or Personality tag for 3D hover / UI
                bool showVetTag = VisionConfig.EnableVeteranSystem.Value && (VisionConfig.ShowVeteranSquadRosterTags.Value || VisionConfig.ShowVeteran3DIcons.Value);
                bool showPersTag = VisionConfig.EnableRolePersonalities.Value && VisionConfig.ShowPersonalityInSquadRoster.Value;
                string baseName = string.IsNullOrEmpty(rec.BaseName) ? (s.name_surname ?? "") : rec.BaseName;
                int bracketIdx = baseName.IndexOf(" [", StringComparison.Ordinal);
                if (bracketIdx >= 0)
                    baseName = baseName.Substring(0, bracketIdx);

                string desiredName = baseName + BuildSoldierBracketTag(s, showVetTag, showPersTag, false);
                if (s.name_surname != desiredName)
                {
                    s.name_surname = desiredName;
                }
            }
            else if (tierPromoted)
            {
                _records[ptr] = rec;
            }
        }

        // =====================================================================
        // Procedural Veteran Chevron Badge Textures (32x32 RGBA)
        // =====================================================================
        private static void EnsureBadgeTextures()
        {
            if (_texVet1 != null && _texVet2 != null && _texVet3 != null)
                return;

            _texVet1 = CreateChevronTexture(1, new Color(0.90f, 0.72f, 0.38f, 0.95f), false); // Bronze / Corporal
            _texVet2 = CreateChevronTexture(2, new Color(0.88f, 0.93f, 1.00f, 0.95f), false); // Silver / Sergeant
            _texVet3 = CreateChevronTexture(3, new Color(1.00f, 0.84f, 0.15f, 1.00f), true);  // Gold + Star / First Sgt
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

            // Draw chevrons (^ shapes pointing up in Unity texture space where y=31 is top)
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

            // Draw a small diamond/star pip at the bottom center for Tier 3
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

            // Periodic dictionary cleanup & active veteran count every 3 seconds
            if (Time.time > _nextStateCleanupTime)
            {
                _nextStateCleanupTime = Time.time + 3f;
                CleanupDictionaries();
            }

            if (!VisionConfig.EnableVeteranSystem.Value || !VisionConfig.ShowVeteran3DIcons.Value)
                return;

            // Honor the exact native PlayerController.OnGUI (0x1807c7d95 - 0x1807c8c68) name display rules:
            // 1. Must have an alive human-controlled soldier
            PlayerController pc = PlayerController.currentController;
            if (pc == null) return;
            Soldier player = pc.ControlledCharacter;
            if (player == null || !player.IsAlive)
                return;

            // 2. Must have GUI enabled and 3D markers enabled (hidden in Realistic / No-HUD mode)
            if (SavableData.Settings == null ||
                SavableData.Settings.system == null ||
                !SavableData.Settings.system.enableGUI ||
                SavableData.Settings.system.disable3DMarkers)
            {
                return;
            }

            // 3. Native PlayerController.OnGUI hides friendly names when aiming in FPS (unless limited aiming)
            if (pc.IsAiming && !pc.IsLimitedAiming)
                return;

            // 4. Only show the 3D veteran icon for the single unoccluded friendly soldier currently hovered
            //    by the player's crosshair in pc.cursorSoldiers (matching the exact name fade calculation)
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

                // Never show on enemies
                if (s.faction != player.faction)
                    continue;

                // Skip soldiers inside a vehicle when player is also in a vehicle
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

            PruneDict(_records, alivePtrs);
            PruneDict(_stanceDropHoldFireUntil, alivePtrs);
            PruneDict(_nextStanceDropAllowed, alivePtrs);
            PruneDict(_combatStanceHoldUntil, alivePtrs);
            PruneDict(_combatCrouchLockUntil, alivePtrs);
            PruneDict(_noProneUntil, alivePtrs);
            PruneDict(_nextWallCheckTime, alivePtrs);
            PruneDict(_wallBlockedUntil, alivePtrs);
            PruneDict(_peekRestoreTime, alivePtrs);
            PruneDict(_nextPeekAllowedTime, alivePtrs);
            PruneDict(_nextFFCheckTime, alivePtrs);
            PruneDict(_ffBlockedUntil, alivePtrs);
            PruneDict(_nextAntiStackTime, alivePtrs);
            PruneDict(_nextVaultCheckTime, alivePtrs);
            PruneDict(_nextLiberateCheckTime, alivePtrs);
            PruneDict(_bleedCoverDeadline, alivePtrs);
            PruneDict(_nextBipodSeekTime, alivePtrs);
            PruneDict(_nextBipodDeployCheckTime, alivePtrs);
            PruneDict(_bipodStandUntil, alivePtrs);
            PruneDict(_nextWallSideCheckTime, alivePtrs);
            PruneDict(_nextWallSidestepTime, alivePtrs);
            PruneDict(_nextTankDisciplineCountTime, alivePtrs);
            PruneDict(_poseLockUntil, alivePtrs);
            PruneDict(_nextBayonetCheckTime, alivePtrs);
            PruneDict(_nextMeleeAttemptTime, alivePtrs);
            PruneDict(_nextMeleeStepTime, alivePtrs);
            PruneDict(_nextPromotionCheckTime, alivePtrs);
            PruneDict(_vehicleAimCache, alivePtrs);
            PruneDict(_threatSectorCache, alivePtrs);
            PruneDict(_watchWindowCache, alivePtrs);
            PruneDict(_lastDamageByVictim, alivePtrs);

            if (_bayonetCheckedSoldiers.Count > 0)
            {
                var deadBayo = new List<IntPtr>();
                foreach (var k in _bayonetCheckedSoldiers)
                {
                    if (!alivePtrs.Contains(k))
                        deadBayo.Add(k);
                }
                for (int i = 0; i < deadBayo.Count; i++)
                {
                    _bayonetCheckedSoldiers.Remove(deadBayo[i]);
                }
            }

            if (Time.time > _nextCoverCacheClearTime)
            {
                _nextCoverCacheClearTime = Time.time + 60f;
                _coverExposureCache.Clear();
                _turretVehicleCache.Clear();
            }

            if (_isTurningToWatchSector.Count > 0)
            {
                var deadTurns = new List<IntPtr>();
                foreach (var k in _isTurningToWatchSector)
                {
                    if (!alivePtrs.Contains(k))
                        deadTurns.Add(k);
                }
                for (int i = 0; i < deadTurns.Count; i++)
                {
                    _isTurningToWatchSector.Remove(deadTurns[i]);
                }
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

        private static void PruneDict<T>(Dictionary<IntPtr, T> dict, HashSet<IntPtr> alive)
        {
            if (dict.Count == 0) return;
            var remove = new List<IntPtr>();
            foreach (var k in dict.Keys)
            {
                if (!alive.Contains(k)) remove.Add(k);
            }
            for (int i = 0; i < remove.Count; i++)
            {
                dict.Remove(remove[i]);
            }
        }

        // =====================================================================
        // Helper: Co-op Ownership, RPC Rate-Limiting & Solid Environment Raycast
        // =====================================================================
        public static bool IsLocallyOwnedBot(Soldier s)
        {
            if (s == null || s.Pointer == IntPtr.Zero || !s.IsAlive || s.IsPlayer())
                return false;

            SyncSoldier syncher = s.soldierSyncher;
            if (syncher != null && (!syncher.OwnSyncher || syncher.controlled_by_player))
                return false;

            return true;
        }

        public static bool IsLocallyOwnedVehicle(Vehicle v)
        {
            if (v == null || v.Pointer == IntPtr.Zero)
                return false;

            SyncVehicle syncher = v.vehicleSyncher;
            return syncher == null || syncher.OwnSyncher;
        }

        private static bool TryConsumeBotRpcBudget(float minInterval = 0.35f)
        {
            float now = Time.time;
            if (now < _nextAllowedBotRpcTime)
                return false;

            _nextAllowedBotRpcTime = now + minInterval;
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

        private static bool RaycastEnvironment(Vector3 origin, Vector3 dir, float maxDist, Transform ignoreRoot, out RaycastHit envHit)
        {
            // ResourcesManager.groundMask (Default | Terrain) hits only solid environment geometry (walls, buildings,
            // sandbags, terrain) and ignores soldier BodyPart colliders, ragdolls, items, foliage, and triggers.
            // This eliminates multi-hop raycast loops and IL2CPP GetComponentInParent<Creature>() hierarchy walks!
            return Physics.Raycast(origin, dir, out envHit, maxDist, GetSolidEnvironmentMask(), QueryTriggerInteraction.Ignore);
        }

        private static float GetBarrelHeightForPose(SoldierPose pose)
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

        private static bool IsMuzzleBlockedByWall(Soldier shooter, Vector3 targetPos, float distToTarget)
        {
            if (shooter == null || distToTarget <= 1.2f)
                return false;

            // Only evaluate wall obstruction once the soldier has rotated within firing/aiming sector
            // (Vanilla RotateAndFireToward pulls the trigger whenever AngleToDirection <= 30 deg)
            if (shooter.AngleToDirection(targetPos - shooter.transform.position) > 35f)
                return false;

            // Check native TPS/FPS wall proximity flag first
            if (shooter.IsTowardWall())
                return true;

            Transform myRoot = shooter.transform.root;
            Vector3 soldierPos = shooter.transform.position;
            float barrelH = GetBarrelHeightForPose(shooter.m_pose);

            // 1. Steady Stance Barrel Corridor from Spine:
            //    - Starts 0.10m behind the soldier's spine at actual stance barrel height (0.25m Prone / 0.68m Crouch / 1.24m Stand).
            //    - Catches walls/windowsills/sandbags even when the rifle barrel tip (GunPosition) is poking into or through the wall!
            //    - Immune to procedural hand/body animation barrel dip, so it safely blocks wall shots during bolt-cycling & transitions.
            Vector3 spineAtBarrel = soldierPos + Vector3.up * barrelH;
            Vector3 toTargetFromSpine = targetPos - spineAtBarrel;
            if (toTargetFromSpine.sqrMagnitude > 0.04f)
            {
                Vector3 spineAimDir = toTargetFromSpine.normalized;
                float spineMaxCheck = Mathf.Min(VisionConfig.MuzzleObstructionCheckDist.Value + 0.65f, distToTarget - 0.35f);
                if (spineMaxCheck > 0.25f)
                {
                    Vector3 spineStart = spineAtBarrel - spineAimDir * 0.10f;
                    if (RaycastEnvironment(spineStart, spineAimDir, spineMaxCheck, myRoot, out _))
                        return true;

                    // Lower barrel lip-clearance check (-0.06m) so barrels grazing a stone parapet or windowsill lip are caught
                    if (RaycastEnvironment(spineStart - Vector3.up * 0.06f, spineAimDir, Mathf.Min(spineMaxCheck, 2.0f), myRoot, out _))
                        return true;
                }
            }

            // 2. Live Weapon Receiver & Muzzle Ray (when not mid-body-transition or reloading):
            //    In SoldierAI.RotateAndFireToward (0x18055E50C), vanilla computes ai.shootDirection from heldGun.transform.position
            //    (the weapon receiver in the soldier's hands, offset to the right shoulder and behind GunPosition).
            if (!shooter.isInBodyAnimation && !shooter.IsReloading)
            {
                Vector3 gunPos = shooter.GunPosition();
                GenericGun heldGun = shooter.GetHeldGun();
                Vector3 receiverPos = (heldGun != null && heldGun.transform != null) ? heldGun.transform.position : gunPos;

                // Clamp against downward bolt-cycle / recoil hand dips
                float minLiveY = soldierPos.y + barrelH - 0.12f;
                if (receiverPos.y < minLiveY) receiverPos.y = minLiveY;
                if (gunPos.y < minLiveY) gunPos.y = minLiveY;

                Vector3 toTarget = targetPos - receiverPos;
                Vector3 aimDir = toTarget.sqrMagnitude > 0.01f ? toTarget.normalized : shooter.GetFireDir();

                float maxCheck = Mathf.Min(VisionConfig.MuzzleObstructionCheckDist.Value + 0.35f, distToTarget - 0.35f);
                if (maxCheck > 0.25f)
                {
                    if (RaycastEnvironment(receiverPos - aimDir * 0.10f, aimDir, maxCheck, myRoot, out _))
                        return true;

                    if (RaycastEnvironment(gunPos - aimDir * 0.15f, aimDir, maxCheck, myRoot, out _))
                        return true;

                    if (RaycastEnvironment(gunPos - Vector3.up * 0.06f - aimDir * 0.10f, aimDir, Mathf.Min(maxCheck, 1.85f), myRoot, out _))
                        return true;
                }
            }

            return false;
        }

        private static bool IsActualFireDirectionBlockedByWall(Soldier shooter, float distToTarget)
        {
            if (shooter == null || distToTarget <= 1.2f)
                return false;

            Vector3 fireDir = shooter.GetFireDir();
            if (fireDir.sqrMagnitude < 0.01f)
                return false;
            fireDir.Normalize();

            Transform myRoot = shooter.transform.root;
            Vector3 soldierPos = shooter.transform.position;
            float barrelH = GetBarrelHeightForPose(shooter.m_pose);
            float minLiveY = soldierPos.y + barrelH - 0.12f;

            GenericGun heldGun = shooter.GetHeldGun();
            Vector3 receiverPos = (heldGun != null && heldGun.transform != null) ? heldGun.transform.position : shooter.GunPosition();
            if (receiverPos.y < minLiveY)
                receiverPos.y = minLiveY;

            float maxCheck = Mathf.Min(VisionConfig.MuzzleObstructionCheckDist.Value + 0.35f, distToTarget - 0.35f);
            if (maxCheck <= 0.25f)
                return false;

            return RaycastEnvironment(receiverPos - fireDir * 0.10f, fireDir, maxCheck, myRoot, out _);
        }

        private static bool IsFriendlyInLineOfFire(Soldier shooter, Vector3 targetPos, float distToTarget, float vet01, out Vector3 sidestepDir)
        {
            sidestepDir = Vector3.zero;
            if (distToTarget <= 1.8f)
                return false;

            Vector3 gunPos = shooter.GunPosition();
            Vector3 toTarget = targetPos - gunPos;
            Vector3 fireDir = toTarget.sqrMagnitude > 0.001f ? toTarget.normalized : shooter.GetFireDir();

            float maxCheckDist = Mathf.Min(distToTarget - 0.5f, Mathf.Lerp(40f, 60f, vet01));
            if (maxCheckDist <= 0.5f)
                return false;

            float allowedConeDeg = VisionConfig.FriendlyFireConeDegrees.Value + (vet01 * 1.5f);
            float corridorWidth = VisionConfig.FriendlyFireCorridorWidth.Value;
            string myFaction = shooter.faction;
            IntPtr myPtr = shooter.Pointer;

            // 1. Always check the human player first if on the same faction
            Soldier player = Soldier.CurrentControlledSoldierOrNull();
            if (player != null && player.IsAlive && player.Pointer != myPtr && player.faction == myFaction)
            {
                if (CheckSingleFriendlyInCone(gunPos, fireDir, maxCheckDist, allowedConeDeg, corridorWidth, shooter, player, out sidestepDir))
                    return true;
            }

            // 2. Check shooter's own squadmates (most likely to be right in front of the muzzle)
            Squad mySquad = shooter.joinedSquad;
            if (mySquad != null && mySquad.units != null)
            {
                var units = mySquad.units;
                int c = units.Count;
                for (int i = 0; i < c; i++)
                {
                    Soldier f = units[i];
                    if (f == null || !f.IsAlive || f.Pointer == myPtr) continue;
                    if (CheckSingleFriendlyInCone(gunPos, fireDir, maxCheckDist, allowedConeDeg, corridorWidth, shooter, f, out sidestepDir))
                        return true;
                }
            }

            // 3. Check other nearby friendly squads
            var allSquads = Squad.AllSquads;
            if (allSquads != null)
            {
                foreach (var kvp in allSquads)
                {
                    Squad sq = kvp.Value;
                    if (sq == null || sq == mySquad || sq.units == null) continue;
                    Soldier leader = sq.Leader;
                    if (leader == null || leader.faction != myFaction) continue;

                    // Quick squad distance cull (skip squads > 75m away from shooter)
                    if ((leader.transform.position - gunPos).sqrMagnitude > 75f * 75f)
                        continue;

                    var units = sq.units;
                    int c = units.Count;
                    for (int i = 0; i < c; i++)
                    {
                        Soldier f = units[i];
                        if (f == null || !f.IsAlive || f.Pointer == myPtr) continue;
                        if (CheckSingleFriendlyInCone(gunPos, fireDir, maxCheckDist, allowedConeDeg, corridorWidth, shooter, f, out sidestepDir))
                            return true;
                    }
                }
            }

            return false;
        }

        private static bool CheckSingleFriendlyInCone(
            Vector3 gunPos,
            Vector3 fireDir,
            float maxCheckDist,
            float allowedConeDeg,
            float corridorWidth,
            Soldier shooter,
            Soldier friend,
            out Vector3 sidestepDir)
        {
            sidestepDir = Vector3.zero;
            Vector3 friendPos = friend.GetCenterOfUnit();
            Vector3 toFriend = friendPos - gunPos;
            float distSq = toFriend.sqrMagnitude;
            if (distSq > maxCheckDist * maxCheckDist || distSq < 0.04f)
                return false;

            float forwardDist = Vector3.Dot(toFriend, fireDir);
            if (forwardDist <= 0.1f || forwardDist >= maxCheckDist)
                return false;

            float angle = Vector3.Angle(toFriend, fireDir);
            float lateralDist = Vector3.Cross(fireDir, toFriend).magnitude;

            // Block shot if friendly is within the 5-degree cone OR within the close-range (8m x 0.65m) torso corridor
            if (angle <= allowedConeDeg || (forwardDist <= 8.0f && lateralDist <= corridorWidth))
            {
                Vector3 right = shooter.transform.right;
                float side = Vector3.Dot(toFriend, right);
                sidestepDir = side >= 0f ? -right : right;
                return true;
            }
            return false;
        }

        private static bool TryGetAntiStackPush(Soldier shooter, out Vector3 pushDir)
        {
            pushDir = Vector3.zero;
            float configuredSpace = VisionConfig.MinFriendlySpacingMeters.Value;
            if (configuredSpace <= 0.2f) return false;

            // Only trigger a physical reposition step when two stationary soldiers are genuinely overlapping (< 1.25m)
            // so stationary squads hold their firing line calmly without constant shuffling.
            float minSpace = Mathf.Min(configuredSpace, 1.25f);
            if (GetPersonality(shooter) == SoldierPersonality.Assaulter_Skirmisher)
                minSpace = Mathf.Min(configuredSpace * 1.25f, 1.65f);

            float minSpaceSq = minSpace * minSpace;
            Vector3 myPos = shooter.transform.position;
            IntPtr myPtr = shooter.Pointer;
            string myFaction = shooter.faction;

            // Check own squad first (only the higher-pointer soldier steps aside so both don't move at once)
            Squad mySquad = shooter.joinedSquad;
            if (mySquad != null && mySquad.units != null)
            {
                var units = mySquad.units;
                int c = units.Count;
                for (int i = 0; i < c; i++)
                {
                    Soldier f = units[i];
                    if (f == null || !f.IsAlive || f.Pointer == myPtr || f.IsOnVehicle()) continue;
                    if (myPtr.ToInt64() < f.Pointer.ToInt64()) continue;

                    Vector3 diff = myPos - f.transform.position;
                    diff.y = 0f;
                    float dSq = diff.sqrMagnitude;
                    if (dSq < minSpaceSq)
                    {
                        if (dSq > 0.01f)
                            pushDir = diff.normalized;
                        else
                            pushDir = (myPtr.ToInt64() & 1) == 0 ? shooter.transform.right : -shooter.transform.right;
                        return true;
                    }
                }
            }

            // Check human player if nearby (AI always yields personal space to the human player)
            Soldier player = Soldier.CurrentControlledSoldierOrNull();
            if (player != null && player.IsAlive && player.Pointer != myPtr && player.faction == myFaction && !player.IsOnVehicle())
            {
                Vector3 diff = myPos - player.transform.position;
                diff.y = 0f;
                float dSq = diff.sqrMagnitude;
                if (dSq < minSpaceSq)
                {
                    pushDir = dSq > 0.01f ? diff.normalized : shooter.transform.right;
                    return true;
                }
            }

            return false;
        }

        // =====================================================================
        // Helper: Synchronized Bot Stance, Cover Seeking & Gunner Bipod Wall Mount
        // =====================================================================
        private static void SetBotPose(SoldierAI ai, Soldier soldier, SoldierPose pose, bool forceImmediate = false)
        {
            if (ai == null || soldier == null)
                return;

            if (soldier.UsingBipod() || soldier.IsBipodTransitioning())
            {
                ai.actualPose = soldier.m_pose;
                return;
            }

            // Native Soldier.SetPose (0x180767f81 - 0x180767fb2) forcibly rewrites Idle (0) -> Crouch (1)
            // (or aborts if Prone) whenever Time.time <= stopHandsAnim_end (isInHandsAnimation / IsReloading).
            // Setting ai.actualPose = Idle while m_pose != Idle during a hands animation causes Move/StopMove
            // to call SetPose(Idle) and get snapped back to Crouch on every single frame!
            if (pose == SoldierPose.Idle &&
                soldier.m_pose != SoldierPose.Idle &&
                (soldier.isInHandsAnimation || soldier.IsReloading || soldier.isInBodyAnimation))
            {
                ai.actualPose = soldier.m_pose;
                return;
            }

            IntPtr ptr = soldier.Pointer;

            if (pose >= SoldierPose.Crouch && (ai.HasEnemyAndCanFireAtHim || soldier.GetSuppressionValue() >= 8f))
            {
                if (ptr != IntPtr.Zero)
                {
                    float lockUntil = Time.time + 3.5f;
                    if (!_combatCrouchLockUntil.TryGetValue(ptr, out float cur) || lockUntil > cur)
                    {
                        _combatCrouchLockUntil[ptr] = lockUntil;
                    }
                }
            }

            // If already in the requested stance, simply keep ai.actualPose synchronized so MovementRoutine's
            // per-frame StopMove(ai.actualPose) / MoveOptimized(ai.actualPose) never fights soldier.m_pose!
            if (soldier.m_pose == pose)
            {
                ai.actualPose = pose;
                return;
            }

            // Prevent rapid stance oscillation (Stand <-> Crouch <-> Prone jitter) by enforcing a minimum
            // stance dwell lock (_poseLockUntil) and waiting for any active 1.2s body transition animation
            // to finish, except for urgent transitions (lifting a Prone mover or dropping from Standing under fire).
            bool isUrgentTransition = forceImmediate ||
                                      (soldier.m_pose == SoldierPose.Prone && ai.IsMoving()) ||
                                      (soldier.m_pose == SoldierPose.Idle && pose >= SoldierPose.Crouch && !ai.IsMoving() &&
                                       (ai.HasEnemyAndCanFireAtHim || soldier.GetSuppressionValue() >= 15f));

            if (!isUrgentTransition && ptr != IntPtr.Zero)
            {
                if (soldier.isInBodyAnimation ||
                    (_poseLockUntil.TryGetValue(ptr, out float poseLock) && Time.time < poseLock))
                {
                    ai.actualPose = soldier.m_pose;
                    return;
                }
            }

            if (ptr != IntPtr.Zero)
            {
                _poseLockUntil[ptr] = Time.time + 2.8f;
            }

            // MovementRoutine calls StopMove(ai.actualPose) every frame when stationary
            // and MoveOptimized(ai.actualPose) when moving, so BOTH must be updated!
            ai.actualPose = pose;
            soldier.SetPose(pose, true, false, 0.2f);
        }

        private static bool IsMachineGunnerOrHasBipod(Soldier soldier, out AttachmentBipod bipod)
        {
            bipod = null;
            if (soldier == null)
                return false;

            bipod = soldier.GetHeldFunctionalBipod();
            return bipod != null || soldier.hasMg || soldier.IsGunner();
        }

        private static bool TrySeekNearbyCover(SoldierAI ai, Soldier soldier)
        {
            if (ai == null || soldier == null || soldier.IsOnVehicle())
                return false;

            if (soldier.IsOnCover())
                return true;

            // IMPORTANT (Fix for Wall-Hugging Bug):
            // Never call soldier.GetDestinationInPosition(soldier.transform.position, ...) + soldier.CoverPosition(cover),
            // because CoverPosition overwrites soldier.targetDestination with a local wall cover node and erases the
            // squad's objective/advance order! Instead, use native TryToGetToActualCover(), which upgrades an
            // existing DestinationWithoutCover to a CombatCover strictly inside the squad's moveOrderPosition area.
            if (soldier.HasDestinationAssigned)
            {
                soldier.TryToGetToActualCover();
                return soldier.IsOnCover() || ai.IsMoving();
            }

            return false;
        }

        private static bool HasClearLineOfFireAtHeight(Soldier soldier, Vector3 targetPos, float dist, float lowerBarrelHeight, float upperBarrelHeight, float maxRayDist)
        {
            if (soldier == null || dist <= 1.5f)
                return false;

            return HasClearLineOfFireFromPosition(soldier.transform.position, soldier.transform.root, targetPos, dist, lowerBarrelHeight, upperBarrelHeight, maxRayDist);
        }

        private static bool HasClearLineOfFireFromPosition(
            Vector3 basePos,
            Transform ignoreRoot,
            Vector3 targetPos,
            float dist,
            float lowerBarrelHeight,
            float upperBarrelHeight,
            float maxRayDist)
        {
            if (dist <= 1.5f)
                return false;

            // Check both the lower barrel-clearance line and the upper barrel/sight line toward targetPos
            // (starting 0.10m behind the spine axis and stepping past self-colliders via RaycastEnvironment)
            // so a wall/parapet between 0.60m and 1.15m tall never passes an overly high eye-level ray!
            Vector3 lowerOrigin = basePos + Vector3.up * lowerBarrelHeight;
            Vector3 toTargetLower = targetPos - lowerOrigin;
            float lenLower = toTargetLower.magnitude;
            if (lenLower <= 1.0f)
                return true;

            Vector3 dirLower = toTargetLower / lenLower;
            float checkDistLower = Mathf.Min(lenLower - 0.50f, maxRayDist);
            if (checkDistLower > 0.35f)
            {
                if (RaycastEnvironment(lowerOrigin - dirLower * 0.10f, dirLower, checkDistLower + 0.10f, ignoreRoot, out _))
                    return false;
            }

            Vector3 upperOrigin = basePos + Vector3.up * upperBarrelHeight;
            Vector3 toTargetUpper = targetPos - upperOrigin;
            float lenUpper = toTargetUpper.magnitude;
            if (lenUpper > 1.0f)
            {
                Vector3 dirUpper = toTargetUpper / lenUpper;
                float checkDistUpper = Mathf.Min(lenUpper - 0.50f, maxRayDist);
                if (checkDistUpper > 0.35f)
                {
                    if (RaycastEnvironment(upperOrigin - dirUpper * 0.10f, dirUpper, checkDistUpper + 0.10f, ignoreRoot, out _))
                        return false;
                }
            }

            return true;
        }

        private static bool HasClearLineOfFireFromProne(Soldier soldier, Vector3 targetPos, float dist)
        {
            return HasClearLineOfFireAtHeight(soldier, targetPos, dist, 0.22f, 0.32f, 15.0f);
        }

        private static bool HasClearLineOfFireFromCrouch(Soldier soldier, Vector3 targetPos, float dist)
        {
            return HasClearLineOfFireAtHeight(soldier, targetPos, dist, 0.64f, 0.78f, 18.0f);
        }

        private static bool HasClearLineOfFireFromStanding(Soldier soldier, Vector3 targetPos, float dist)
        {
            return HasClearLineOfFireAtHeight(soldier, targetPos, dist, 1.18f, 1.32f, 18.0f);
        }

        private static bool TrySidestepWallObstruction(SoldierAI ai, Soldier soldier, Vector3 targetPos, float dist)
        {
            if (ai == null || soldier == null || soldier.Pointer == IntPtr.Zero || soldier.transform == null)
                return false;

            IntPtr ptr = soldier.Pointer;
            if (_nextWallSidestepTime.TryGetValue(ptr, out float nextSide) && Time.time < nextSide)
                return false;

            _nextWallSidestepTime[ptr] = Time.time + 3.5f;

            Vector3 curPos = soldier.transform.position;
            Vector3 toTargetXZ = targetPos - curPos;
            toTargetXZ.y = 0f;
            if (toTargetXZ.sqrMagnitude < 0.25f)
                return false;
            toTargetXZ.Normalize();

            Vector3 right = new Vector3(toTargetXZ.z, 0f, -toTargetXZ.x);
            Transform myRoot = soldier.transform.root;
            bool preferRight = (GetDeterministicHash(ptr, 61) & 1) == 0;

            float[] stepDistances = { 1.25f, 1.80f };
            for (int dIdx = 0; dIdx < stepDistances.Length; dIdx++)
            {
                float stepDist = stepDistances[dIdx];
                for (int side = 0; side < 2; side++)
                {
                    Vector3 sideDir = ((side == 0) == preferRight) ? right : -right;
                    if (IsDirectionBlockedByEnvironment(curPos + Vector3.up * 0.75f, sideDir, stepDist + 0.30f, myRoot))
                        continue;

                    Vector3 candidateXZ = curPos + sideDir * stepDist;
                    Vector3 groundProbe = new Vector3(candidateXZ.x, curPos.y + 1.25f, candidateXZ.z);
                    if (!Physics.Raycast(groundProbe, Vector3.down, out RaycastHit groundHit, 2.6f))
                        continue;

                    if (groundHit.collider == null || groundHit.collider.isTrigger || groundHit.normal.y < 0.65f)
                        continue;

                    Vector3 stepPos = groundHit.point;
                    if (Mathf.Abs(stepPos.y - curPos.y) > 1.20f)
                        continue;

                    // Check if stepping to stepPos clears the vertical wall frame / corner / pillar in Standing or Crouch
                    if (HasClearLineOfFireFromPosition(stepPos, myRoot, targetPos, dist, 1.18f, 1.32f, 18.0f) ||
                        HasClearLineOfFireFromPosition(stepPos, myRoot, targetPos, dist, 0.64f, 0.78f, 18.0f))
                    {
                        if ((ai.MoveDestination - curPos).sqrMagnitude <= 2.5f * 2.5f)
                        {
                            ai.MoveDestination = stepPos;
                        }
                        ai.MoveDirectlyToward(stepPos, 0.45f);
                        _nextWallSidestepTime[ptr] = Time.time + 4.5f;
                        AntiStackNudges++;
                        return true;
                    }
                }
            }

            return false;
        }

        // =====================================================================
        // Helper: Sheltered Tank / Vehicle Repair Position (Leeward Hull Cover)
        // =====================================================================
        private static Vector3 GetShelteredRepairPosition(Vehicle veh, Soldier mechanic, out Vector3 shelterDir)
        {
            shelterDir = Vector3.back;
            if (veh == null || veh.transform == null)
                return Vector3.zero;

            Vector3 vehPos = veh.transform.position;
            Vector3 vehFwd = veh.transform.forward;
            vehFwd.y = 0f;
            vehFwd = vehFwd.sqrMagnitude > 0.001f ? vehFwd.normalized : Vector3.forward;

            Vector3 threatDir = Vector3.zero;

            // 1. Check the mechanic's own visible target first
            if (mechanic != null && mechanic.aiController != null && mechanic.aiController.visibleTarget != null)
            {
                Vector3 toEnemy = mechanic.aiController.visibleTarget.GetCenterOfUnit() - vehPos;
                toEnemy.y = 0f;
                if (toEnemy.sqrMagnitude > 0.25f)
                    threatDir = toEnemy.normalized;
            }

            // 2. Check the vehicle's main turret target
            Turret mainTurret = veh.GetMainTurret(false);
            if (threatDir.sqrMagnitude < 0.01f && mainTurret != null && mainTurret.autoPilot != null)
            {
                Spottable turretTarget = mainTurret.autoPilot.CurrentVisibleTarget;
                if (turretTarget != null)
                {
                    Vector3 toEnemy = turretTarget.GetCenterOfUnit() - vehPos;
                    toEnemy.y = 0f;
                    if (toEnemy.sqrMagnitude > 0.25f)
                        threatDir = toEnemy.normalized;
                }
            }

            // 3. Check secondary seat turrets or seated crew members' targets
            if (threatDir.sqrMagnitude < 0.01f && veh.seats != null)
            {
                var seats = veh.seats;
                int seatCount = seats.Length;
                for (int i = 0; i < seatCount; i++)
                {
                    Seats seat = seats[i];
                    if (seat == null) continue;

                    if (seat.connectedTurret != null && seat.connectedTurret.autoPilot != null)
                    {
                        Spottable t = seat.connectedTurret.autoPilot.CurrentVisibleTarget;
                        if (t != null)
                        {
                            Vector3 toEnemy = t.GetCenterOfUnit() - vehPos;
                            toEnemy.y = 0f;
                            if (toEnemy.sqrMagnitude > 0.25f)
                            {
                                threatDir = toEnemy.normalized;
                                break;
                            }
                        }
                    }

                    if (seat.unitSet != null && seat.unitSet.aiController != null && seat.unitSet.aiController.visibleTarget != null)
                    {
                        Vector3 toEnemy = seat.unitSet.aiController.visibleTarget.GetCenterOfUnit() - vehPos;
                        toEnemy.y = 0f;
                        if (toEnemy.sqrMagnitude > 0.25f)
                        {
                            threatDir = toEnemy.normalized;
                            break;
                        }
                    }
                }
            }

            // 4. Use the main turret's aim / fire direction (points toward the last known threat sector)
            if (threatDir.sqrMagnitude < 0.01f && mainTurret != null)
            {
                Vector3 fireDir = mainTurret.GetFireDir();
                fireDir.y = 0f;
                if (fireDir.sqrMagnitude > 0.01f)
                    threatDir = fireDir.normalized;
            }

            // 5. Fallback to the tank hull's forward vector (enemies are typically ahead of the frontal glacis)
            if (threatDir.sqrMagnitude < 0.01f)
            {
                threatDir = vehFwd;
            }

            // Sheltered side is directly opposite the threat so the tank hull acts as solid armor cover
            shelterDir = -threatDir;

            float absFwdDot = Mathf.Abs(Vector3.Dot(shelterDir, vehFwd));
            float hullOffset = Mathf.Lerp(2.7f, 4.1f, absFwdDot);

            // Check if the direct leeward spot is blocked by a solid wall/building right behind the tank
            Transform vehRoot = veh.transform.root;
            Vector3 rayOrigin = vehPos + Vector3.up * 1.1f;
            if (IsDirectionBlockedByEnvironment(rayOrigin, shelterDir, hullOffset + 0.5f, vehRoot))
            {
                Vector3 altLeft = Quaternion.Euler(0f, 55f, 0f) * shelterDir;
                float altLeftOffset = Mathf.Lerp(2.7f, 4.1f, Mathf.Abs(Vector3.Dot(altLeft, vehFwd)));
                if (!IsDirectionBlockedByEnvironment(rayOrigin, altLeft, altLeftOffset + 0.5f, vehRoot))
                {
                    shelterDir = altLeft;
                    hullOffset = altLeftOffset;
                }
                else
                {
                    Vector3 altRight = Quaternion.Euler(0f, -55f, 0f) * shelterDir;
                    float altRightOffset = Mathf.Lerp(2.7f, 4.1f, Mathf.Abs(Vector3.Dot(altRight, vehFwd)));
                    if (!IsDirectionBlockedByEnvironment(rayOrigin, altRight, altRightOffset + 0.5f, vehRoot))
                    {
                        shelterDir = altRight;
                        hullOffset = altRightOffset;
                    }
                }
            }

            return vehPos + shelterDir * hullOffset;
        }

        private static bool IsDirectionBlockedByEnvironment(Vector3 origin, Vector3 dir, float dist, Transform ignoreRoot)
        {
            return RaycastEnvironment(origin, dir, dist, ignoreRoot, out _);
        }

        // =====================================================================
        // Helper: Threat-Sector Gaze Concentration & Flanking Blind-Spots
        // - Stationary defenders concentrate on directions of KNOWN enemy squads
        //   (squadmate targets, IsEnemyAwareOfThisSquad, spotted/marked, or firing within 4.5s)
        // - Unspotted, non-firing flanking squads are ignored so flanking maneuvers are rewarded!
        // =====================================================================
        public static bool HasActiveThreatSectorContact(Soldier soldier)
        {
            if (soldier == null || soldier.Pointer == IntPtr.Zero)
                return false;

            // Pure O(1) dictionary lookup — NEVER trigger Squad.AllSquads iteration or 18-angle window raycast
            // sweeps inside Creature.CanSee! ThreatSectorCache is populated by SequentialUpdate & MovementRoutine.
            return _threatSectorCache.TryGetValue(soldier.Pointer, out ThreatSectorCache cached) &&
                   Time.time < cached.ExpireTime &&
                   cached.HasActiveContact;
        }

        private static readonly float[] _watchSweepOffsets = new float[]
        {
            0f, -18f, 18f, -36f, 36f, -54f, 54f, -72f, 72f, -90f, 90f, -112f, 112f, -135f, 135f, -158f, 158f, 180f
        };

        private static Vector3 CenterGazeInWindowAperture(Vector3 upperOrigin, Vector3 dir, Transform ignoreRoot, float fanT, float maxFanDeg)
        {
            // Probe left (-8, -16, -24, -32 deg) and right (+8, +16, +24, +32 deg) at window height (1.28m)
            // to find the angular bounds of the window opening so the soldier's vision cone is centered out the window
            // rather than staring perpendicular to the window wall or clipping the window jamb.
            float minOpenDeg = 0f;
            for (int step = 1; step <= 4; step++)
            {
                float deg = -8f * step;
                Vector3 probe = Quaternion.Euler(0f, deg, 0f) * dir;
                if (!IsDirectionBlockedByEnvironment(upperOrigin, probe, 5.5f, ignoreRoot))
                    minOpenDeg = deg;
                else
                    break;
            }

            float maxOpenDeg = 0f;
            for (int step = 1; step <= 4; step++)
            {
                float deg = 8f * step;
                Vector3 probe = Quaternion.Euler(0f, deg, 0f) * dir;
                if (!IsDirectionBlockedByEnvironment(upperOrigin, probe, 5.5f, ignoreRoot))
                    maxOpenDeg = deg;
                else
                    break;
            }

            float centerDeg = 0.5f * (minOpenDeg + maxOpenDeg);
            float halfWidth = 0.5f * (maxOpenDeg - minOpenDeg);
            float safeMargin = Mathf.Max(0f, halfWidth - 6f);
            float safeFan = Mathf.Clamp(fanT * maxFanDeg, -safeMargin, safeMargin);
            return (Quaternion.Euler(0f, centerDeg + safeFan, 0f) * dir).normalized;
        }

        private static Vector3 PickClearWatchDirection(Soldier soldier, Vector3 myPos, Vector3 baseDir, float maxFanDeg)
        {
            baseDir.y = 0f;
            if (baseDir.sqrMagnitude < 0.001f)
                return Vector3.zero;
            baseDir.Normalize();

            IntPtr ptr = soldier.Pointer;
            float baseAngleDeg = Mathf.Atan2(baseDir.x, baseDir.z) * Mathf.Rad2Deg;
            if (_watchWindowCache.TryGetValue(ptr, out WatchWindowCache wCache) &&
                Time.time < wCache.ExpireTime &&
                (myPos - wCache.Pos).sqrMagnitude < 0.25f &&
                Mathf.Abs(Mathf.DeltaAngle(baseAngleDeg, wCache.BaseAngleDeg)) < 10.0f)
            {
                return wCache.ResultDir;
            }

            int h = GetDeterministicHash(ptr, 97);
            float t = ((h % 1001) / 500.0f) - 1.0f; // -1.0 .. +1.0
            float fanAngle = t * maxFanDeg;
            Vector3 fannedDir = Quaternion.Euler(0f, fanAngle, 0f) * baseDir;

            Transform root = soldier.transform.root;
            Vector3 upperOrigin = myPos + Vector3.up * 1.28f;
            Vector3 sillOrigin = myPos + Vector3.up * 0.52f;

            // 1. Check if fannedDir is blocked by an indoor/nearby wall (< 4.5m) or already looks out a window/parapet
            //    (sill/wall <= 2.35m at 0.52m height, open exterior view >= 7.5m at 1.28m height).
            bool fannedBlocked = RaycastEnvironment(upperOrigin, fannedDir, 4.5f, root, out _);
            bool fannedHasWindowSill = !fannedBlocked &&
                                       !RaycastEnvironment(upperOrigin, fannedDir, 7.5f, root, out _) &&
                                       RaycastEnvironment(sillOrigin, fannedDir, 2.35f, root, out _);

            Vector3 finalDir;
            if (fannedHasWindowSill)
            {
                finalDir = CenterGazeInWindowAperture(upperOrigin, fannedDir, root, t, maxFanDeg);
                _watchWindowCache[ptr] = new WatchWindowCache
                {
                    Pos = myPos,
                    BaseAngleDeg = baseAngleDeg,
                    ResultDir = finalDir,
                    ExpireTime = Time.time + 8.0f
                };
                return finalDir;
            }

            // Detect when a bot is sitting right next to a window (45-90 deg to the side) but fannedDir points
            // perpendicular to the window along an indoor hallway/room!
            bool nearbyWindowDetected = false;
            if (!fannedBlocked)
            {
                Vector3 left90 = new Vector3(-fannedDir.z, 0f, fannedDir.x);
                Vector3 right90 = new Vector3(fannedDir.z, 0f, -fannedDir.x);
                if ((RaycastEnvironment(sillOrigin, left90, 2.35f, root, out _) && !RaycastEnvironment(upperOrigin, left90, 7.5f, root, out _)) ||
                    (RaycastEnvironment(sillOrigin, right90, 2.35f, root, out _) && !RaycastEnvironment(upperOrigin, right90, 7.5f, root, out _)))
                {
                    nearbyWindowDetected = true;
                }
            }

            if (!fannedBlocked && !nearbyWindowDetected)
            {
                _watchWindowCache[ptr] = new WatchWindowCache
                {
                    Pos = myPos,
                    BaseAngleDeg = baseAngleDeg,
                    ResultDir = fannedDir,
                    ExpireTime = Time.time + 8.0f
                };
                return fannedDir;
            }

            // 2. Sweep 360 deg around baseDir to find the best window aperture or longest unobstructed exterior sightline
            Vector3 bestDir = fannedDir;
            float bestScore = -999f;
            bool bestIsWindow = false;

            for (int i = 0; i < _watchSweepOffsets.Length; i++)
            {
                float angleOffset = _watchSweepOffsets[i];
                Vector3 cand = i == 0 ? baseDir : (Quaternion.Euler(0f, angleOffset, 0f) * baseDir);

                float clearDist = 18.0f;
                if (RaycastEnvironment(upperOrigin, cand, 18.0f, root, out RaycastHit upperHit))
                {
                    clearDist = upperHit.distance;
                }

                if (clearDist < 2.0f)
                    continue;

                bool isWindowAperture = clearDist >= 7.0f &&
                                        RaycastEnvironment(sillOrigin, cand, 2.35f, root, out RaycastHit sillHit) &&
                                        sillHit.distance < clearDist - 3.0f;

                float alignDot = Vector3.Dot(cand, baseDir); // -1.0 .. +1.0
                float score = clearDist + (alignDot * 5.5f);
                if (isWindowAperture)
                {
                    score += 18.0f; // Strong preference for looking out a window opening rather than perpendicular into the room
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    bestDir = cand;
                    bestIsWindow = isWindowAperture;
                }
            }

            finalDir = (bestIsWindow || fannedBlocked)
                ? CenterGazeInWindowAperture(upperOrigin, bestDir, root, t, maxFanDeg)
                : bestDir;

            _watchWindowCache[ptr] = new WatchWindowCache
            {
                Pos = myPos,
                BaseAngleDeg = baseAngleDeg,
                ResultDir = finalDir,
                ExpireTime = Time.time + 8.0f
            };
            return finalDir;
        }

        private static void InsertClosestContact(
            Vector3 dir,
            float distSq,
            ref int count,
            ref Vector3 c0, ref float d0,
            ref Vector3 c1, ref float d1,
            ref Vector3 c2, ref float d2,
            ref Vector3 c3, ref float d3)
        {
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.25f)
                return;
            Vector3 norm = dir.normalized;

            // Deduplicate contacts within ~18 degrees of an existing closer contact
            if (count > 0 && Vector3.Dot(norm, c0) > 0.95f)
            {
                if (distSq < d0) { c0 = norm; d0 = distSq; }
                return;
            }
            if (count > 1 && Vector3.Dot(norm, c1) > 0.95f)
            {
                if (distSq < d1) { c1 = norm; d1 = distSq; }
                return;
            }
            if (count > 2 && Vector3.Dot(norm, c2) > 0.95f)
            {
                if (distSq < d2) { c2 = norm; d2 = distSq; }
                return;
            }
            if (count > 3 && Vector3.Dot(norm, c3) > 0.95f)
            {
                if (distSq < d3) { c3 = norm; d3 = distSq; }
                return;
            }

            if (count == 0 || distSq < d0)
            {
                c3 = c2; d3 = d2;
                c2 = c1; d2 = d1;
                c1 = c0; d1 = d0;
                c0 = norm; d0 = distSq;
                if (count < 4) count++;
            }
            else if (count == 1 || distSq < d1)
            {
                c3 = c2; d3 = d2;
                c2 = c1; d2 = d1;
                c1 = norm; d1 = distSq;
                if (count < 4) count++;
            }
            else if (count == 2 || distSq < d2)
            {
                c3 = c2; d3 = d2;
                c2 = norm; d2 = distSq;
                if (count < 4) count++;
            }
            else if (count == 3 || distSq < d3)
            {
                c3 = norm; d3 = distSq;
                if (count < 4) count++;
            }
        }

        private static bool TryGetKnownThreatSectorDirection(Soldier soldier, out Vector3 watchDir, out bool hasActiveContact)
        {
            watchDir = Vector3.zero;
            hasActiveContact = false;
            if (soldier == null || soldier.Pointer == IntPtr.Zero || soldier.transform == null)
                return false;

            IntPtr ptr = soldier.Pointer;
            if (_threatSectorCache.TryGetValue(ptr, out ThreatSectorCache cached) && Time.time < cached.ExpireTime)
            {
                watchDir = cached.WatchDir;
                hasActiveContact = cached.HasActiveContact;
                return watchDir.sqrMagnitude > 0.01f;
            }

            Vector3 myPos = soldier.transform.position;
            string myFaction = soldier.faction;
            Squad mySquad = soldier.joinedSquad;
            MissionObjective myObj = (mySquad != null && mySquad.currentTask != null)
                ? mySquad.currentTask.TryCast<MissionObjective>()
                : null;

            int contactCount = 0;
            Vector3 c0 = Vector3.zero, c1 = Vector3.zero, c2 = Vector3.zero, c3 = Vector3.zero;
            float d0 = float.MaxValue, d1 = float.MaxValue, d2 = float.MaxValue, d3 = float.MaxValue;

            // 1. Highest priority: Enemies currently seen by any member of our own squad
            if (mySquad != null && mySquad.units != null)
            {
                var units = mySquad.units;
                int uCount = units.Count;
                for (int i = 0; i < uCount; i++)
                {
                    Soldier mate = units[i];
                    if (mate == null || !mate.IsAlive || mate.aiController == null) continue;
                    Spottable mateTarget = mate.aiController.visibleTarget;
                    if (mateTarget == null || mateTarget.IsAirVehicle()) continue;

                    Vector3 toEnemy = mateTarget.GetCenterOfUnit() - myPos;
                    toEnemy.y = 0f;
                    float dSq = toEnemy.sqrMagnitude;
                    if (dSq > 0.25f)
                    {
                        // Weight active squadmate targets slightly closer so our squad prioritizes its immediate fight
                        InsertClosestContact(toEnemy, dSq * 0.55f, ref contactCount, ref c0, ref d0, ref c1, ref d1, ref c2, ref d2, ref c3, ref d3);
                    }
                }
            }

            // 2. Known / Spotted / Recently-Firing Enemy Squads within 175m
            //    CRITICAL FLANKING RULE: Quiet, unspotted enemy squads (!IsEnemyAwareOfThisSquad, !isMarked, !HasFiredRecently)
            //    are completely ignored so defenders concentrate on known contacts and leave quiet flanks open!
            var allSquads = Squad.AllSquads;
            if (allSquads != null)
            {
                foreach (var kvp in allSquads)
                {
                    Squad sq = kvp.Value;
                    if (sq == null || sq == mySquad || sq.units == null) continue;

                    Soldier enemyRef = sq.Leader;
                    if (enemyRef == null || !enemyRef.IsAlive)
                    {
                        var units = sq.units;
                        int uCount = units.Count;
                        for (int i = 0; i < uCount; i++)
                        {
                            Soldier u = units[i];
                            if (u != null && u.IsAlive)
                            {
                                enemyRef = u;
                                break;
                            }
                        }
                    }

                    if (enemyRef == null || enemyRef.faction == myFaction)
                        continue;

                    if (enemyRef.IsOnVehicle())
                    {
                        Vehicle v = enemyRef.GetCurrentVehicle();
                        if (v != null && v.TryCast<VehiclePlane>() != null)
                            continue;
                    }

                    Vector3 toSquad = enemyRef.transform.position - myPos;
                    toSquad.y = 0f;
                    float dSq = toSquad.sqrMagnitude;
                    if (dSq > 175f * 175f || dSq < 0.25f)
                        continue;

                    bool isKnownContact = sq.IsEnemyAwareOfThisSquad ||
                                          sq.isMarked ||
                                          sq.isHalfMarked ||
                                          sq.isMarkedPinPoint ||
                                          enemyRef.IsSpotted;

                    Vector3 contactVector = toSquad;

                    if (!isKnownContact && dSq <= 150f * 150f)
                    {
                        var units = sq.units;
                        int uCount = units.Count;
                        for (int i = 0; i < uCount; i++)
                        {
                            Soldier u = units[i];
                            if (u == null || !u.IsAlive) continue;
                            if (u.IsSpotted || VisionPatches.HasFiredRecently(u, 4.5f))
                            {
                                isKnownContact = true;
                                contactVector = u.transform.position - myPos;
                                contactVector.y = 0f;
                                dSq = contactVector.sqrMagnitude;
                                break;
                            }
                        }
                    }

                    if (!isKnownContact && myObj != null && dSq <= 55f * 55f)
                    {
                        if (enemyRef.PositionIsInsideValidConquerZone(enemyRef.transform.position, myObj))
                        {
                            isKnownContact = true;
                        }
                    }

                    if (isKnownContact)
                    {
                        InsertClosestContact(contactVector, dSq, ref contactCount, ref c0, ref d0, ref c1, ref d1, ref c2, ref d2, ref c3, ref d3);
                    }
                }
            }

            // 3. Check the Human Player if on the enemy team and within 175m (ONLY if spotted, firing recently, or in a known squad!)
            Soldier player = Soldier.CurrentControlledSoldierOrNull();
            if (player != null && player.IsAlive && player.faction != myFaction)
            {
                Vector3 toPlayer = player.transform.position - myPos;
                toPlayer.y = 0f;
                float pDistSq = toPlayer.sqrMagnitude;
                if (pDistSq <= 175f * 175f && pDistSq > 0.25f)
                {
                    bool playerIsKnown = player.IsSpotted ||
                                         VisionPatches.HasFiredRecently(player, 4.5f) ||
                                         (player.joinedSquad != null &&
                                          (player.joinedSquad.IsEnemyAwareOfThisSquad ||
                                           player.joinedSquad.isMarked ||
                                           player.joinedSquad.isHalfMarked ||
                                           player.joinedSquad.isMarkedPinPoint));

                    if (playerIsKnown)
                    {
                        InsertClosestContact(toPlayer, pDistSq, ref contactCount, ref c0, ref d0, ref c1, ref d1, ref c2, ref d2, ref c3, ref d3);
                    }
                }
            }

            if (contactCount > 0)
            {
                hasActiveContact = true;

                // Deterministically assign each squad member to one of the active known contacts
                // so a squad divides its attention across multiple known threats without averaging
                // two separate directions into empty space between them!
                int pickIdx = GetDeterministicHash(ptr, 41) % contactCount;
                Vector3 chosenBase = pickIdx == 0 ? c0 : (pickIdx == 1 ? c1 : (pickIdx == 2 ? c2 : c3));
                float maxFan = (soldier.hasMg || soldier.isMarksMan) ? 8f : 14f;
                watchDir = PickClearWatchDirection(soldier, myPos, chosenBase, maxFan);
            }
            else
            {
                // Pre-contact fallback: no enemy squads are currently spotted/aware/loud nearby,
                // so orient toward the general enemy approach vector with a wider 24-deg overwatch fan
                hasActiveContact = false;
                Vector3 fallbackBase = soldier.transform.forward;
                BattleManager bm = BattleManager.instance;
                BattlePhase phase = bm != null ? bm.currentPhase : null;
                if (phase != null && !string.IsNullOrEmpty(myFaction) && phase.EnemyFactionHasSpawn(myFaction))
                {
                    Vector3 toEnemySpawn = phase.GetOppositeFactionTerritoryPosition(myFaction) - myPos;
                    toEnemySpawn.y = 0f;
                    if (toEnemySpawn.sqrMagnitude > 1.0f)
                    {
                        fallbackBase = toEnemySpawn;
                    }
                }
                watchDir = PickClearWatchDirection(soldier, myPos, fallbackBase, 24f);
            }

            float sectorJitter = (GetDeterministicHash(ptr, 53) % 35) * 0.01f;
            _threatSectorCache[ptr] = new ThreatSectorCache
            {
                ExpireTime = Time.time + 1.50f + sectorJitter,
                WatchDir = watchDir,
                HasActiveContact = hasActiveContact
            };

            return watchDir.sqrMagnitude > 0.01f;
        }

        // =====================================================================
        // Helper: Smart Wall-Side Cover & Threat Direction (Stand BEHIND Walls)
        // =====================================================================
        private static Vector3 GetEffectiveThreatDirection(Soldier soldier, Squad squad, Vector3 referencePos, Vector3 fallbackDir)
        {
            // 1. Direct visible target of this soldier (live incoming/outgoing fire)
            if (soldier != null && soldier.aiController != null && soldier.aiController.visibleTarget != null)
            {
                Vector3 toTarget = soldier.aiController.visibleTarget.GetCenterOfUnit() - referencePos;
                toTarget.y = 0f;
                if (toTarget.sqrMagnitude > 0.25f)
                    return toTarget.normalized;
            }

            // 2. Closest visible target engaged by any squadmate (squad's active combat sector)
            if (squad != null && squad.units != null)
            {
                var units = squad.units;
                int count = units.Count;
                float bestDistSq = float.MaxValue;
                Vector3 bestToEnemy = Vector3.zero;

                for (int i = 0; i < count; i++)
                {
                    Soldier mate = units[i];
                    if (mate == null || !mate.IsAlive || mate.aiController == null) continue;
                    Spottable mateTarget = mate.aiController.visibleTarget;
                    if (mateTarget == null) continue;

                    Vector3 enemyPos = mateTarget.GetCenterOfUnit();
                    Vector3 toEnemy = enemyPos - referencePos;
                    toEnemy.y = 0f;
                    float dSq = toEnemy.sqrMagnitude;
                    if (dSq > 0.25f && dSq < bestDistSq)
                    {
                        bestDistSq = dSq;
                        bestToEnemy = toEnemy;
                    }
                }

                if (bestToEnemy.sqrMagnitude > 0.25f)
                    return bestToEnemy.normalized;
            }

            // 2B. Known threat sector (aware/spotted/loud enemy squads within 175m, ignoring quiet flankers)
            if (VisionConfig.EnableThreatSectorConcentration.Value &&
                soldier != null &&
                TryGetKnownThreatSectorDirection(soldier, out Vector3 sectorDir, out bool hasActiveContact) &&
                hasActiveContact &&
                sectorDir.sqrMagnitude > 0.01f)
            {
                return sectorDir;
            }

            // 3. Opposite faction territory / spawn direction from BattleManager.instance.currentPhase
            string faction = soldier != null ? soldier.faction : (squad != null && squad.Leader != null ? squad.Leader.faction : null);
            BattleManager bm = BattleManager.instance;
            BattlePhase phase = bm != null ? bm.currentPhase : null;
            if (phase != null && !string.IsNullOrEmpty(faction) && phase.EnemyFactionHasSpawn(faction))
            {
                Vector3 enemyTerritory = phase.GetOppositeFactionTerritoryPosition(faction);
                Vector3 toTerritory = enemyTerritory - referencePos;
                toTerritory.y = 0f;
                if (toTerritory.sqrMagnitude > 1.0f)
                    return toTerritory.normalized;
            }

            // 4. Fallback direction
            fallbackDir.y = 0f;
            return fallbackDir.sqrMagnitude > 0.001f ? fallbackDir.normalized : Vector3.forward;
        }

        private static bool IsPositionShelteredByFrontCover(Vector3 pos, Vector3 threatDir, Transform ignoreRoot)
        {
            if (threatDir.sqrMagnitude < 0.001f)
                return false;

            // Check if there is low/waist-height (0.50m) or chest-height (0.88m) cover within 1.85m toward +threatDir
            return IsDirectionBlockedByEnvironment(pos + Vector3.up * 0.50f, threatDir, 1.85f, ignoreRoot) ||
                   IsDirectionBlockedByEnvironment(pos + Vector3.up * 0.88f, threatDir, 1.85f, ignoreRoot);
        }

        private static bool IsPositionOnExposedSideOfWall(Vector3 pos, Vector3 threatDir, Transform ignoreRoot, out RaycastHit rearWallHit)
        {
            rearWallHit = default;
            if (threatDir.sqrMagnitude < 0.001f)
                return false;

            // 1. If there is already cover in front (+threatDir within 1.85m) at waist or chest height,
            //    the position is sheltered (e.g. behind a sandbag, inside a trench, or behind a windowsill).
            if (IsPositionShelteredByFrontCover(pos, threatDir, ignoreRoot))
                return false;

            // 2. No cover in front toward +threatDir! Now check if there is a vertical wall directly behind
            //    the soldier's back (-threatDir within 1.95m at waist/chest height 0.75m).
            Vector3 rearOrigin = pos + Vector3.up * 0.75f;
            if (Physics.Raycast(rearOrigin, -threatDir, out rearWallHit, 1.95f, GetSolidEnvironmentMask(), QueryTriggerInteraction.Ignore))
            {
                if (Mathf.Abs(rearWallHit.normal.y) < 0.55f)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryFindShelteredSideOfWall(Vector3 currentPos, Vector3 threatDir, Transform ignoreRoot, out Vector3 shelteredPos)
        {
            shelteredPos = Vector3.zero;
            if (!IsPositionOnExposedSideOfWall(currentPos, threatDir, ignoreRoot, out RaycastHit rearWallHit))
                return false;

            // Probe from 2.30m behind the front face of the wall back toward +threatDir to locate the back face
            // and verify the wall is a freestanding wall/sandbag/fence/hedgerow (thickness <= 1.65m), not a hill or solid building
            int envMask = GetSolidEnvironmentMask();
            Vector3 probeOrigin = rearWallHit.point - threatDir * 2.30f;
            if (!Physics.Raycast(probeOrigin, threatDir, out RaycastHit backFaceHit, 2.25f, envMask, QueryTriggerInteraction.Ignore))
                return false;

            float wallThickness = Vector3.Distance(rearWallHit.point, backFaceHit.point);
            if (wallThickness < 0.04f || wallThickness > 1.65f)
                return false;

            // Ideal sheltered position is 0.85m behind the back face of the wall (so the wall is now in +threatDir)
            Vector3 candidateXZ = backFaceHit.point - threatDir * 0.85f;
            Vector3 groundRayStart = new Vector3(candidateXZ.x, currentPos.y + 1.35f, candidateXZ.z);
            if (!Physics.Raycast(groundRayStart, Vector3.down, out RaycastHit groundHit, 3.0f, envMask, QueryTriggerInteraction.Ignore))
                return false;

            if (groundHit.normal.y < 0.65f)
                return false;

            Vector3 candidatePos = groundHit.point;
            if (Mathf.Abs(candidatePos.y - currentPos.y) > 1.50f)
                return false;

            // Ensure the space between the back face of the wall and candidatePos is open (not inside another collider)
            Vector3 backSurfaceCheck = backFaceHit.point - threatDir * 0.08f;
            Vector3 toCandidate = (candidatePos + Vector3.up * 0.75f) - backSurfaceCheck;
            if (toCandidate.sqrMagnitude > 0.01f &&
                IsDirectionBlockedByEnvironment(backSurfaceCheck, toCandidate.normalized, toCandidate.magnitude, ignoreRoot))
            {
                return false;
            }

            // Ensure lateral shoulder clearance at candidatePos
            Vector3 right = new Vector3(threatDir.z, 0f, -threatDir.x);
            Vector3 chestPos = candidatePos + Vector3.up * 0.80f;
            if (IsDirectionBlockedByEnvironment(chestPos, right, 0.35f, ignoreRoot) &&
                IsDirectionBlockedByEnvironment(chestPos, -right, 0.35f, ignoreRoot))
            {
                return false;
            }

            // Outdoor sky check: if currentPos is outdoors (no roof within 6m above), require candidatePos
            // to also have open sky above it so soldiers never try to step through the outer shell of a closed house prefab!
            bool currentIsOutdoor = !IsDirectionBlockedByEnvironment(currentPos + Vector3.up * 0.85f, Vector3.up, 6.0f, ignoreRoot);
            if (currentIsOutdoor && IsDirectionBlockedByEnvironment(candidatePos + Vector3.up * 0.85f, Vector3.up, 6.0f, ignoreRoot))
            {
                return false;
            }

            shelteredPos = candidatePos;
            return true;
        }

        private static void HandleGunnerBipodAndWallMounting(SoldierAI ai, Soldier soldier, AttachmentBipod bip, Vector3 targetPos, float dist)
        {
            if (!VisionConfig.EnableGunnerBipodWallMounting.Value || ai == null || soldier == null || soldier.IsOnVehicle())
                return;

            IntPtr ptr = soldier.Pointer;

            // 1. If already deployed on a bipod, maintain stance or undeploy if moving / flanked > 60 deg
            if (soldier.UsingBipod())
            {
                ai.actualPose = soldier.m_pose;
                if (ai.IsMoving() || soldier.IsCharging())
                {
                    soldier.PutAwayBipod(0.30f, false, false, false);
                    return;
                }

                if (ai.HasEnemyAndCanFireAtHim && dist > 2f)
                {
                    Vector3 toTarget = targetPos - soldier.transform.position;
                    if (soldier.AngleToDirection(toTarget) > 60f)
                    {
                        soldier.PutAwayBipod(0.35f, false, false, false);
                    }
                }
                return;
            }

            if (soldier.IsBipodTransitioning())
            {
                ai.actualPose = soldier.m_pose;
                return;
            }

            // 2. When stationary and aiming at an enemy with a functional bipod, scan for wall/ledge or ground mount
            if (bip == null || ai.IsMoving() || !ai.HasEnemyAndCanFireAtHim || dist <= 5.0f)
                return;

            if (_nextBipodDeployCheckTime.TryGetValue(ptr, out float nextCheck) && Time.time < nextCheck)
                return;

            _nextBipodDeployCheckTime[ptr] = Time.time + 1.25f;

            // Must be roughly facing the target and not mid-transition before locking into a bipod mount
            if (soldier.isInBodyAnimation || soldier.AngleToDirection(targetPos - soldier.transform.position) > 28f)
                return;

            if (!soldier.CanStartBipodDeploy())
                return;

            // Try A: Scan for wall/sandbag/windowsill surface at current stance (Crouch or Standing)
            // Note: Calling ScanBipodSurface directly bypasses vanilla BipodCheck's overly strict IsTowardWall() abort
            if (soldier.m_pose != SoldierPose.Prone)
            {
                if (soldier.ScanBipodSurface(bip, out Vector3 curAnchor, out Collider curSupport) && curSupport != null)
                {
                    SetBotPose(ai, soldier, soldier.m_pose);
                    if (soldier.m_pose == SoldierPose.Idle)
                    {
                        _bipodStandUntil[ptr] = Time.time + 4.5f;
                    }
                    soldier.OperateBipod(bip, curAnchor, curSupport, true, false);
                    BipodsDeployed++;
                    return;
                }
            }

            // Compute horizontal forward & right vectors for multi-height wall surface scanning
            Vector3 fwd = soldier.transform.forward;
            fwd.y = 0f;
            fwd = fwd.sqrMagnitude > 0.001f ? fwd.normalized : Vector3.forward;
            Vector3 right = new Vector3(fwd.z, 0f, -fwd.x);
            Vector3 scanBase = soldier.transform.position + fwd * 0.35f;
            float feetY = soldier.transform.position.y;

            // Try B: If Prone behind a waist-high wall/sandbag (~0.85m), step up to Crouch and mount the wall
            if (soldier.m_pose == SoldierPose.Prone)
            {
                if (soldier.ScanBipodSurfaceAt(bip, scanBase + Vector3.up * 0.85f, right, out Vector3 crouchAnchor, out Collider crouchSupport) &&
                    crouchSupport != null &&
                    crouchAnchor.y > feetY + 0.45f)
                {
                    SetBotPose(ai, soldier, SoldierPose.Crouch, true);
                    _bipodStandUntil[ptr] = Time.time + 3.0f;
                    _noProneUntil[ptr] = Time.time + 10.0f;
                    soldier.OperateBipod(bip, crouchAnchor, crouchSupport, true, false);
                    BipodsDeployed++;
                    return;
                }
            }

            // Try C: If Crouch/Prone behind a chest-high wall/windowsill (~1.30m), stand up and mount (AI equivalent of TryStandForHighBipod)
            if (soldier.m_pose != SoldierPose.Idle && (soldier.IsOnCover() || soldier.IsTowardWall() || IsMuzzleBlockedByWall(soldier, targetPos, dist)))
            {
                if (soldier.ScanBipodSurfaceAt(bip, scanBase + Vector3.up * 1.30f, right, out Vector3 standAnchor, out Collider standSupport) &&
                    standSupport != null &&
                    standAnchor.y > feetY + 0.92f)
                {
                    SetBotPose(ai, soldier, SoldierPose.Idle, true);
                    _bipodStandUntil[ptr] = Time.time + 4.5f;
                    _nextStanceDropAllowed[ptr] = Time.time + 4.5f;
                    _noProneUntil[ptr] = Time.time + 10.0f;
                    soldier.StartCoroutine(soldier.RetryBipodDeployAfterStanding());
                    BipodsDeployed++;
                    return;
                }
            }

            // Try D: No wall/ledge surface in front — deploy ground bipod when Prone if ground LOF is clear.
            // Never flip Prone -> BipodCheck -> Crouch in the same tick!
            bool canTryProneBipod = (!_noProneUntil.TryGetValue(ptr, out float noProne) || Time.time >= noProne) &&
                                    !soldier.IsOnCover() &&
                                    !soldier.IsTowardWall();

            if (dist > 8.5f && canTryProneBipod && HasClearLineOfFireFromProne(soldier, targetPos, dist))
            {
                if (soldier.m_pose == SoldierPose.Prone)
                {
                    soldier.BipodCheck(true);
                    if (soldier.UsingBipod())
                    {
                        BipodsDeployed++;
                    }
                    else
                    {
                        // Ground slope/uneven terrain rejected bipod deploy — don't retry ground bipod for 12s
                        _noProneUntil[ptr] = Time.time + 12.0f;
                    }
                }
                else
                {
                    SetBotPose(ai, soldier, SoldierPose.Prone);
                }
            }
        }

        // =====================================================================
        // Priority 3D: Smart Anti-Armor Target & Fire Discipline
        // - Stops infantry with small arms from shooting uselessly at armored tanks
        // - Redirects aim to exposed crew members (open-topped TDs, halftracks, jeeps, pintle gunners)
        // - Allows shooting vulnerable rubber wheels on wheeled vehicles or penetrable armor/tracks
        // =====================================================================
        private static bool TryEvaluateVehicleTarget(
            Soldier shooter,
            Vehicle veh,
            float dist,
            out Soldier exposedCrewTarget,
            out Vector3 customAimPoint,
            out bool hasCustomAimPoint)
        {
            exposedCrewTarget = null;
            customAimPoint = Vector3.zero;
            hasCustomAimPoint = false;

            if (shooter == null || veh == null)
                return false;

            GenericGun heldGun = shooter.GetHeldGun();
            if (heldGun == null)
                return false;

            float maxShootDist = heldGun.DamageRange();
            if (dist > maxShootDist + 5f)
                return false;

            bool isAtWeapon = heldGun.IsAtWeapon();
            bool isExplosiveOrFlame = heldGun.isFlamethrower || heldGun.IsGrenadeLauncher();
            float bulletPenMm = 0f;

            AmmoItem ammoItem = heldGun.GetAmmoItem();
            if (ammoItem == null)
                ammoItem = heldGun.usedAmmoItem;

            if (ammoItem != null)
            {
                BulletData bd = ammoItem.GetBulletData();
                if (bd != null)
                {
                    if (bd.IsHe() || bd.IsFire() || bd.explosion_penetration > 10f || bd.mmPenetration > 25)
                    {
                        isExplosiveOrFlame = true;
                    }
                    bulletPenMm = bd.CalculateMmPenetrationWithFallof(dist);
                }
            }

            if (bulletPenMm <= 0.01f)
            {
                bulletPenMm = isAtWeapon ? 35f : 6f;
            }

            Vector3 shooterPos = shooter.transform.position;

            // 1. Check for exposed crew members / passengers (Seats.showUnitSet == true)
            //    If the shooter is NOT using a dedicated AT rocket/HE weapon, shooting an exposed
            //    crewman/gunner/driver is always the most effective way small arms can help!
            var seats = veh.seats;
            if (seats != null)
            {
                float bestCrewDistSq = float.MaxValue;
                int seatCount = seats.Length;
                for (int i = 0; i < seatCount; i++)
                {
                    Seats seat = seats[i];
                    if (seat == null || !seat.showUnitSet)
                        continue;

                    Soldier crew = seat.unitSet;
                    if (crew == null || !crew.IsAlive || crew.faction == shooter.faction)
                        continue;

                    Spottable crewSpottable = crew.Cast<Spottable>();
                    if (shooter.CanSee(crewSpottable))
                    {
                        float dSq = (crew.transform.position - shooterPos).sqrMagnitude;
                        if (dSq < bestCrewDistSq)
                        {
                            bestCrewDistSq = dSq;
                            exposedCrewTarget = crew;
                        }
                    }
                }

                if (exposedCrewTarget != null && !isAtWeapon && !isExplosiveOrFlame)
                {
                    customAimPoint = exposedCrewTarget.GetCenterOfUnit();
                    hasCustomAimPoint = true;
                    return true;
                }
            }

            // 2. Dedicated explosive / HEAT / Flamethrower / heavy AT weapons (>40mm pen) can engage vehicle hulls directly
            if (isExplosiveOrFlame || bulletPenMm >= 40f)
            {
                return true;
            }

            bool isTank = veh.TryCast<VehicleTank>() != null;

            // Standard small arms (<12mm penetration) cannot penetrate tank hulls or tank tracks
            if (isTank && !isAtWeapon && bulletPenMm < 12f)
            {
                if (exposedCrewTarget != null)
                {
                    customAimPoint = exposedCrewTarget.GetCenterOfUnit();
                    hasCustomAimPoint = true;
                    return true;
                }
                return false;
            }

            // 3. Inspect vehicle damageable parts for penetrable hull armor, vulnerable wheels, or breakable tracks
            var parts = veh.damageableParts;
            if (parts != null)
            {
                bool canPenetrateMainHull = false;
                Vector3 bestPartPos = Vector3.zero;
                float bestPartDistSq = float.MaxValue;

                Vector3 bestWheelOrTrackPos = Vector3.zero;
                float bestWheelOrTrackDistSq = float.MaxValue;

                int partCount = parts.Count;
                for (int i = 0; i < partCount; i++)
                {
                    VehicleDamagablePart part = parts[i];
                    if (part == null || part.transform == null)
                        continue;

                    // Cosmetic detachable parts (fenders/skirts) do 0 damage to vehicle health
                    if (part.TryCast<VehicleDamagableDetachablePart>() != null)
                        continue;

                    VehicleDamageableWheelPart wheelPart = part.TryCast<VehicleDamageableWheelPart>();
                    if (wheelPart != null)
                    {
                        // Small arms can shoot out un-flattened rubber tires on wheeled vehicles within 60m
                        if (!isTank && !wheelPart.WheelIsFlat && dist <= 60f && bulletPenMm > wheelPart.armorThickness + 0.5f)
                        {
                            Vector3 wPos = wheelPart.transform.position;
                            float dSq = (wPos - shooterPos).sqrMagnitude;
                            if (dSq < bestWheelOrTrackDistSq)
                            {
                                bestWheelOrTrackDistSq = dSq;
                                bestWheelOrTrackPos = wPos;
                            }
                        }
                        continue;
                    }

                    VehicleDamageableTrackPart trackPart = part.TryCast<VehicleDamageableTrackPart>();
                    if (trackPart != null)
                    {
                        // AT rifles / heavy guns that can penetrate track armor can shoot out unbroken tracks
                        if (!trackPart.TrackIsBroken && bulletPenMm > trackPart.armorThickness + 0.5f)
                        {
                            Vector3 tPos = trackPart.transform.position;
                            float dSq = (tPos - shooterPos).sqrMagnitude;
                            if (dSq < bestWheelOrTrackDistSq)
                            {
                                bestWheelOrTrackDistSq = dSq;
                                bestWheelOrTrackPos = tPos;
                            }
                        }
                        continue;
                    }

                    // Outer hull / superstructure / cab armor plate
                    float effectiveArmor = part.considerAngledArmor ? (part.armorThickness * 1.15f) : part.armorThickness;
                    if (bulletPenMm > effectiveArmor + 0.5f)
                    {
                        canPenetrateMainHull = true;
                        Vector3 pPos = part.transform.position;
                        float dSq = (pPos - shooterPos).sqrMagnitude;
                        if (dSq < bestPartDistSq)
                        {
                            bestPartDistSq = dSq;
                            bestPartPos = pPos;
                        }
                    }
                }

                if (canPenetrateMainHull)
                {
                    if (isTank && bestPartDistSq < float.MaxValue)
                    {
                        // Aim AT rifle at the specific thinner armor plate it can penetrate
                        customAimPoint = bestPartPos;
                        hasCustomAimPoint = true;
                    }
                    return true;
                }

                if (bestWheelOrTrackDistSq < float.MaxValue)
                {
                    // Hull is too thick, but the shooter can flatten a wheel or break a track!
                    customAimPoint = bestWheelOrTrackPos;
                    hasCustomAimPoint = true;
                    return true;
                }
            }

            // Fallback: if an AT rifle couldn't penetrate the tank's armor or tracks, but an exposed crewman is visible
            if (exposedCrewTarget != null)
            {
                customAimPoint = exposedCrewTarget.GetCenterOfUnit();
                hasCustomAimPoint = true;
                return true;
            }

            return false;
        }

        private static void TryThrowNormalGrenadeAtVehicle(Soldier soldier, Vehicle targetVeh, float dist)
        {
            if (!IsLocallyOwnedBot(soldier) || targetVeh == null || soldier.inventory == null)
                return;

            if (dist <= 7.5f || dist > 32f || soldier.isInBodyAnimation || soldier.isInHandsAnimation)
                return;

            // Vanilla SequentialUpdate (0x18055f91e) only checks FindItemOfType<VirtualATGrenade>() against vehicles.
            // Allow infantry within throwing range to also throw normal grenades (VirtualGrenade) at tanks/vehicles!
            VirtualItem grenadeItem = soldier.inventory.FindItemOfType<VirtualGrenade>();
            if (grenadeItem != null)
            {
                float maxThrowRange = soldier.GetGrenadeThrowRange(grenadeItem);
                if (dist <= maxThrowRange && UnityEngine.Random.Range(0, 100) > 65 && TryConsumeBotRpcBudget(0.45f))
                {
                    soldier.Throw(grenadeItem, targetVeh.Cast<Spottable>());
                }
            }
        }

        // =====================================================================
        // Helper: Point-Blank Bayonet, Melee & Butt-Stroke CQB Combat
        // - Automatically unfolds/mounts integrated bayonets (e.g. Mosin M44, Carcano M38, Type 44)
        //   once per soldier without calling InstallAttachment (which triggers a heavy SyncSoldierEquipment
        //   Photon RPC across all clients and causes Co-op desyncs when squads alert!)
        // - Overrides vanilla's tiny 1.6m SequentialUpdate-only melee trigger and prevents
        //   bots from crouching/hip-firing bullets into each other at 1-2.5m instead of
        //   lunging with bayonets, knives/shovels, or rifle butt-strokes
        // =====================================================================
        private static void TryEnsureBayonetMounted(Soldier soldier)
        {
            if (!IsLocallyOwnedBot(soldier) ||
                soldier.IsOnVehicle() ||
                soldier.HasBayonetReady ||
                soldier.isInHandsAnimation ||
                soldier.IsReloading)
            {
                return;
            }

            IntPtr ptr = soldier.Pointer;
            if (!_bayonetCheckedSoldiers.Add(ptr))
                return;

            GenericGun heldGun = soldier.GetHeldGun();
            if (heldGun == null)
                return;

            // Unfold integrated / foldable bayonet on rifle (starts folded in AttachmentBayonet.Awake).
            // Never call soldier.inventory.InstallAttachment(bayoItem, true) for AI bots because InstallAttachment
            // broadcasts SyncSoldier.SyncSoldierEquipment over Photon for every soldier in an alerted squad!
            if (heldGun.integratedBayonet != null && !heldGun.integratedBayonet.IsMounted)
            {
                heldGun.integratedBayonet.SetMounted(true);
            }
        }

        private static bool IsEngagedInPointBlankMelee(
            SoldierAI ai,
            Soldier soldier,
            float dist,
            out Soldier enemySoldier,
            out float lungeRange,
            out float ceaseFireRange,
            out float strikeRange)
        {
            enemySoldier = null;
            lungeRange = 0f;
            ceaseFireRange = 0f;
            strikeRange = 0f;

            if (!VisionConfig.EnablePointBlankMelee.Value ||
                ai == null ||
                soldier == null ||
                dist > 6.2f ||
                ai.visibleTarget == null ||
                ai.HeldMortar != null ||
                soldier.IsOnVehicle() ||
                soldier.UsingBipod() ||
                soldier.IsBipodTransitioning() ||
                soldier.HasSurrended() ||
                soldier.IsCarryingBody() ||
                soldier.IsCarried())
            {
                return false;
            }

            enemySoldier = ai.visibleTarget.TryCast<Soldier>();
            if (enemySoldier == null ||
                !enemySoldier.IsAlive ||
                enemySoldier.IsOnVehicle() ||
                enemySoldier.HasSurrended())
            {
                return false;
            }

            Vector3 myPos = soldier.transform.position;
            Vector3 enemyPos = enemySoldier.transform.position;
            if (Mathf.Abs(enemyPos.y - myPos.y) > 1.65f)
                return false;

            bool hasBayonet = soldier.HasBayonetReady;
            bool holdsMelee = soldier.HoldsMeleeWeapon;
            GenericGun heldGun = soldier.GetHeldGun();
            bool gunEmptyOrReloading = soldier.IsReloading ||
                                       soldier.MustReload() ||
                                       (heldGun != null && heldGun.GetCurrentAmmoCount() <= 0);
            bool boltCyclingClose = soldier.CantAimDueToBoltActionUsage ||
                                    (heldGun != null && (heldGun.bolt_required || heldGun.IsReloadingOrUsingBolt()));

            if (holdsMelee)
            {
                lungeRange = 6.0f;
                ceaseFireRange = 6.0f;
                strikeRange = 1.90f;
            }
            else if (hasBayonet)
            {
                lungeRange = (soldier.IsCharging() || gunEmptyOrReloading || boltCyclingClose) ? 5.2f : 3.8f;
                ceaseFireRange = (gunEmptyOrReloading || boltCyclingClose) ? 4.5f : 2.75f;
                strikeRange = 2.25f;
            }
            else
            {
                lungeRange = gunEmptyOrReloading ? 4.8f : (boltCyclingClose ? 3.2f : 2.5f);
                ceaseFireRange = (gunEmptyOrReloading || boltCyclingClose) ? 3.8f : 2.15f;
                strikeRange = 1.65f;
            }

            if (dist > lungeRange)
                return false;

            Vector3 toEnemy = (enemyPos + Vector3.up * 1.05f) - (myPos + Vector3.up * 1.05f);
            float checkDist = Mathf.Min(dist - 0.25f, 3.5f);
            if (checkDist > 0.30f &&
                IsDirectionBlockedByEnvironment(myPos + Vector3.up * 1.05f, toEnemy.normalized, checkDist, soldier.transform.root))
            {
                return false;
            }

            return true;
        }

        private static void TryExecuteMeleeAttack(SoldierAI ai, Soldier soldier, Soldier enemy)
        {
            if (ai == null || !IsLocallyOwnedBot(soldier) || enemy == null)
                return;

            Vector3 toEnemy = enemy.transform.position - soldier.transform.position;
            toEnemy.y = 0f;
            if (toEnemy.sqrMagnitude > 0.01f && soldier.AngleToDirection(toEnemy) > 42f)
                return;

            ai.MeleeBlockWindow();
            if (soldier.IsBlocking || soldier.isInHandsAnimation || soldier.isInBodyAnimation)
                return;

            IntPtr ptr = soldier.Pointer;
            if (_nextMeleeAttemptTime.TryGetValue(ptr, out float nextMelee) && Time.time < nextMelee)
                return;

            if (!TryConsumeBotRpcBudget(0.25f))
                return;

            _nextMeleeAttemptTime[ptr] = Time.time + 0.45f;
            soldier.SetGunTriggerPressed(false);
            soldier.Melee();
            MeleeAttacksTriggered++;
        }

        // =====================================================================
        // PATCH 1: SoldierAI.SequentialUpdate (shared=1, len=1250)
        // - Veteran survival tracking & dynamic rank promotion
        // - Priority 1A: Seek cover FIRST when bleeding, then bandage in cover
        // - Priority 1B: Redirect vanilla's "Favourite Fighting Pose" (uncrouched unless needed; always crouch/prone when shooting)
        // - Priority 1C: Gunner wall-seeking & bipod mounting
        // - Priority 3: Auto-peek stance recovery when reloading/suppressed
        // - Priority 4: Search & liberate nearby surrendered comrades
        // =====================================================================
        [HarmonyPatch(typeof(SoldierAI), nameof(SoldierAI.SequentialUpdate))]
        public static class SoldierAI_SequentialUpdate_Patch
        {
            public static void Prefix(SoldierAI __instance, out bool __state)
            {
                __state = false;
                try
                {
                    if (!VisionConfig.EnableMod.Value || __instance == null)
                        return;

                    Soldier soldier = __instance.character;
                    if (!IsLocallyOwnedBot(soldier) || soldier.IsOnVehicle())
                        return;

                    // 1. Threat-Sector Alert Propagation & Faster Target Lock-On:
                    //    In vanilla Soldier.GetBestVisibleEnemy (0x18074B4A8 - 0x18074B4C6), spotting an enemy > 30m
                    //    calls SetAlertedFor(20f) and then forces a full 2.5-second freeze (Time.time - alertStartTime <= 2.5f)
                    //    before GetBestVisibleEnemy will return any target!
                    //    When a soldier's squad is aware of an attacking squad (hasActiveContact) or already in combat,
                    //    pre-alert the soldier and shorten that 2.5s reaction freeze to 0.55s (Rookie) -> 0.15s (Veteran)!
                    if (VisionConfig.EnableThreatSectorConcentration.Value)
                    {
                        TryGetKnownThreatSectorDirection(soldier, out _, out bool hasActiveContact);
                        bool isSquadAware = soldier.joinedSquad != null && soldier.joinedSquad.IsEnemyAwareOfThisSquad;
                        if (hasActiveContact || isSquadAware || soldier.isAlerted || soldier.GetSuppressionValue() >= 3f)
                        {
                            soldier.SetAlertedFor(20f);
                            float vet01 = GetVeterancy01(soldier);
                            float minAlertElapsed = (!__instance.IsMoving() && hasActiveContact)
                                ? Mathf.Lerp(1.95f, 2.35f, vet01)  // 0.55s -> 0.15s lock-on delay for stationary overwatch
                                : Mathf.Lerp(1.65f, 2.15f, vet01); // 0.85s -> 0.35s lock-on delay when moving/alerted
                            float maxStartTime = Time.time - minAlertElapsed;
                            if (soldier.alertStartTime > maxStartTime)
                            {
                                soldier.alertStartTime = maxStartTime;
                            }
                        }
                    }

                    // 2. If bleeding and actively moving toward a destination/cover, give up to 3.5s to reach cover
                    //    before stopping to bandage, without overwriting the squad's destination!
                    if (VisionConfig.BandageOnlyAfterFindingCover.Value &&
                        (soldier.isBleeding || soldier.GetBleedValue() > 0.01f))
                    {
                        IntPtr ptr = soldier.Pointer;
                        if (!_bleedCoverDeadline.TryGetValue(ptr, out float deadline))
                        {
                            SoldierPersonality bleedPers = GetPersonality(soldier);
                            float seekWindow = (bleedPers == SoldierPersonality.Rifleman_Cautious || bleedPers == SoldierPersonality.Medic_Triage)
                                ? 4.5f
                                : (bleedPers == SoldierPersonality.Assaulter_Shock ? 2.2f : 3.5f);
                            deadline = Time.time + seekWindow;
                            _bleedCoverDeadline[ptr] = deadline;
                        }

                        if (!soldier.IsOnCover() && Time.time < deadline)
                        {
                            bool movingToCover = TrySeekNearbyCover(__instance, soldier);
                            if (movingToCover && __instance.IsMoving() && soldier.isBleeding)
                            {
                                // Temporarily mask isBleeding during SequentialUpdate so vanilla doesn't interrupt movement to cover
                                soldier.isBleeding = false;
                                __state = true;
                            }
                        }
                    }
                }
                catch { }
            }

            public static void Postfix(SoldierAI __instance, bool __state)
            {
                try
                {
                    if (__instance == null)
                        return;

                    Soldier soldier = __instance.character;
                    if (__state && soldier != null)
                    {
                        soldier.isBleeding = true;
                    }

                    if (!VisionConfig.EnableMod.Value || soldier == null || !soldier.IsAlive || soldier.IsPlayer())
                        return;

                    IntPtr ptr = soldier.Pointer;

                    // 1. Update Veteran progression & field rank promotions (throttled per soldier)
                    CheckAndApplyFieldPromotion(soldier);

                    if (!IsLocallyOwnedBot(soldier) || soldier.IsOnVehicle())
                        return;

                    // 2. Priority 1A (Don't Die): Bandage AFTER finding cover (or if stationary / deadline expired)
                    bool isBleeding = soldier.isBleeding || soldier.GetBleedValue() > 0.01f;
                    if (isBleeding)
                    {
                        if (!VisionConfig.BandageOnlyAfterFindingCover.Value)
                        {
                            if (!soldier.isInBodyAnimation && !soldier.isInHandsAnimation)
                            {
                                SetBotPose(__instance, soldier, SoldierPose.Crouch);
                                __instance.TryStopBleeding();
                            }
                            return;
                        }

                        if (!_bleedCoverDeadline.TryGetValue(ptr, out float deadline))
                        {
                            SoldierPersonality bleedPers = GetPersonality(soldier);
                            float seekWindow = (bleedPers == SoldierPersonality.Rifleman_Cautious || bleedPers == SoldierPersonality.Medic_Triage)
                                ? 4.5f
                                : (bleedPers == SoldierPersonality.Assaulter_Shock ? 2.2f : 3.5f);
                            deadline = Time.time + seekWindow;
                            _bleedCoverDeadline[ptr] = deadline;
                        }

                        bool readyToBandage = soldier.IsOnCover() ||
                                              Time.time >= deadline ||
                                              !__instance.IsMoving();

                        if (readyToBandage)
                        {
                            SoldierPose healPose = soldier.IsOnCover() ? SoldierPose.Crouch : SoldierPose.Prone;
                            SetBotPose(__instance, soldier, healPose);
                            if (!soldier.isInBodyAnimation && !soldier.isInHandsAnimation)
                            {
                                __instance.TryStopBleeding();
                            }
                            return;
                        }
                        else
                        {
                            // Sprinting to cover while bleeding: stay standing so they reach cover quickly!
                            SetBotPose(__instance, soldier, SoldierPose.Idle);
                            return;
                        }
                    }
                    else
                    {
                        _bleedCoverDeadline.Remove(ptr);
                    }

                    // 3. Auto-Peek Recovery: Duck back down after peeking if reloading, suppressed, or target lost
                    float vet01 = GetVeterancy01(soldier);
                    SoldierPersonality personality = GetPersonality(soldier);
                    GetEffectivePeekParameters(soldier, vet01, out float maxPeekSupp, out float duckBackSupp, out float peekCooldown);

                    bool isPeeking = false;
                    if (_peekRestoreTime.TryGetValue(ptr, out float restoreAt))
                    {
                        bool urgentDuck = soldier.IsReloading ||
                                          soldier.GetSuppressionValue() > duckBackSupp ||
                                          !__instance.HasEnemyAndCanFireAtHim;

                        bool timerExpiredAndCanCrouch = false;
                        if (!urgentDuck && Time.time > restoreAt)
                        {
                            Vector3 peekTargetPos = __instance.visibleTarget != null
                                ? __instance.visibleTarget.GetCenterOfUnit()
                                : (soldier.transform.position + soldier.transform.forward * Mathf.Max(__instance.distFromTarget, 15f));
                            if (HasClearLineOfFireFromCrouch(soldier, peekTargetPos, __instance.distFromTarget))
                            {
                                timerExpiredAndCanCrouch = true;
                            }
                            else
                            {
                                // Still actively engaging over a waist-high wall that blocks Crouch — keep peeking until reload/suppression/target-loss!
                                _peekRestoreTime[ptr] = Time.time + 2.5f;
                            }
                        }

                        if (urgentDuck || timerExpiredAndCanCrouch)
                        {
                            _peekRestoreTime.Remove(ptr);
                            _nextPeekAllowedTime[ptr] = Time.time + peekCooldown;
                            if (!__instance.IsMoving() && soldier.m_pose == SoldierPose.Idle)
                            {
                                SetBotPose(__instance, soldier, SoldierPose.Crouch);
                                _combatStanceHoldUntil[ptr] = Time.time + 5.0f;
                                _combatCrouchLockUntil[ptr] = Time.time + 3.5f;
                            }
                        }
                        else
                        {
                            isPeeking = true;
                        }
                    }

                    // 3A. Role Personality Active Combat Hooks (Tactician Support, Skirmisher Strafe, Breacher/Grenadier Grenades, Ambusher AT)
                    if (personality != SoldierPersonality.None)
                    {
                        if (personality == SoldierPersonality.Leader_Tactician &&
                            SoldierAI.next_requestingSupport > Time.time + 6.0f)
                        {
                            SoldierAI.next_requestingSupport = Time.time + 6.0f;
                        }
                        else if (personality == SoldierPersonality.Assaulter_Skirmisher &&
                                 __instance.HasEnemyAndCanFireAtHim &&
                                 __instance.distFromTarget < 55f)
                        {
                            __instance.moveLookingTarget = true;
                        }
                        else if ((personality == SoldierPersonality.Assaulter_Breacher ||
                                  personality == SoldierPersonality.Rifleman_Grenadier ||
                                  personality == SoldierPersonality.AT_Demolitionist) &&
                                 __instance.visibleTarget != null &&
                                 !__instance.visibleTarget.IsWheeledVehicleOrTank() &&
                                 __instance.distFromTarget >= 9.0f &&
                                 __instance.distFromTarget <= 34.0f &&
                                 !soldier.isInHandsAnimation &&
                                 !soldier.IsReloading &&
                                 soldier.inventory != null &&
                                 Time.time > soldier.throw_end_time + 5.0f)
                        {
                            VirtualItem fragItem = soldier.inventory.FindItemOfType<VirtualGrenade>();
                            if (fragItem != null &&
                                __instance.distFromTarget <= soldier.GetGrenadeThrowRange(fragItem) &&
                                UnityEngine.Random.Range(0, 100) > 60 &&
                                TryConsumeBotRpcBudget(0.45f))
                            {
                                soldier.Throw(fragItem, __instance.visibleTarget);
                            }
                        }
                    }

                    // 3B. Priority 3D: Smart Anti-Armor Target & Fire Discipline
                    //     Stop infantry with small arms from shooting armored tanks unless they can shoot
                    //     a visible exposed crew member or penetrate a vehicle part/wheel/track!
                    if (VisionConfig.PreventSmallArmsFireAtTanks.Value &&
                        __instance.visibleTarget != null &&
                        __instance.visibleTarget.IsWheeledVehicleOrTank())
                    {
                        Vehicle targetVeh = __instance.visibleTarget.TryCast<Vehicle>();
                        if (targetVeh != null)
                        {
                            // Allow throwing normal grenades (in addition to vanilla's AT grenades) at tanks/vehicles in throw range
                            TryThrowNormalGrenadeAtVehicle(soldier, targetVeh, __instance.distFromTarget);

                            if (TryEvaluateVehicleTarget(
                                soldier,
                                targetVeh,
                                __instance.distFromTarget,
                                out Soldier exposedCrew,
                                out _,
                                out _))
                            {
                                if (exposedCrew != null)
                                {
                                    // Redirect target directly to the exposed crew member so the AI aims at the crewman, not the steel hull!
                                    Spottable crewSpottable = exposedCrew.Cast<Spottable>();
                                    __instance.visibleTarget = crewSpottable;
                                    soldier.CurrentVisibleTarget = crewSpottable;
                                    __instance.distFromTarget = Vector3.Distance(soldier.transform.position, exposedCrew.transform.position);
                                    if (!_nextTankDisciplineCountTime.TryGetValue(ptr, out float nextCount) || Time.time >= nextCount)
                                    {
                                        _nextTankDisciplineCountTime[ptr] = Time.time + 3.0f;
                                        ExposedCrewTargeted++;
                                    }
                                }
                            }
                            else
                            {
                                // Held weapon cannot penetrate any part of this tank/vehicle and no exposed crew member is visible!
                                // If this soldier is flagged isAtUnit (e.g., expended AT ammo and switched to rifle/pistol),
                                // check if there is a visible enemy foot soldier they should engage instead.
                                bool switchedToFootEnemy = false;
                                if (soldier.isAtUnit)
                                {
                                    soldier.isAtUnit = false;
                                    Spottable altEnemy = soldier.GetBestVisibleEnemy(out float altDist);
                                    soldier.isAtUnit = true;
                                    if (altEnemy != null && !altEnemy.IsWheeledVehicleOrTank())
                                    {
                                        __instance.visibleTarget = altEnemy;
                                        soldier.CurrentVisibleTarget = altEnemy;
                                        __instance.distFromTarget = altDist;
                                        GenericGun heldGun = soldier.GetHeldGun();
                                        __instance.targetInWeaponRange = heldGun != null && heldGun.DamageRange() >= altDist;
                                        switchedToFootEnemy = true;
                                    }
                                }

                                if (!switchedToFootEnemy)
                                {
                                    // Stop shooting small arms at the tank and keep head down in cover!
                                    __instance.targetInWeaponRange = false;
                                    soldier.SetGunTriggerPressed(false);
                                    _combatStanceHoldUntil[ptr] = Time.time + 6.0f;
                                    _combatCrouchLockUntil[ptr] = Time.time + 3.5f;
                                    if (!_nextTankDisciplineCountTime.TryGetValue(ptr, out float nextCount) || Time.time >= nextCount)
                                    {
                                        _nextTankDisciplineCountTime[ptr] = Time.time + 3.0f;
                                        UselessTankFirePrevented++;
                                    }
                                }
                            }
                        }
                    }

                    // 4. Redirect Vanilla's "Favourite Fighting Pose" & Handle Gunner Bipod Wall Mounting
                    bool isGunner = IsMachineGunnerOrHasBipod(soldier, out AttachmentBipod bipod);
                    bool isHighBipodStanding = _bipodStandUntil.TryGetValue(ptr, out float standUntil) && Time.time < standUntil;
                    float suppression = soldier.GetSuppressionValue();
                    float dist = __instance.distFromTarget;

                    if (VisionConfig.EnablePointBlankMelee.Value &&
                        ((__instance.HasEnemyAndCanFireAtHim && dist <= 45f) || soldier.IsCharging() || soldier.isAlerted))
                    {
                        TryEnsureBayonetMounted(soldier);
                    }

                    bool inPointBlankMelee = IsEngagedInPointBlankMelee(
                        __instance,
                        soldier,
                        dist,
                        out Soldier meleeEnemy,
                        out _,
                        out _,
                        out float seqStrikeRange);

                    if (inPointBlankMelee && meleeEnemy != null)
                    {
                        __instance.knifeCharge = soldier.HoldsMeleeWeapon || soldier.HasBayonetReady;
                        __instance.moveLookingTarget = true;
                        SetBotPose(__instance, soldier, SoldierPose.Idle, true);

                        // If no bayonet is ready and the gun is empty/reloading (or enemy is < 2.2m), draw inventory knife/shovel
                        if (!soldier.HasBayonetReady &&
                            !soldier.HoldsMeleeWeapon &&
                            soldier.InventoryKnife != null &&
                            !soldier.isInHandsAnimation)
                        {
                            GenericGun heldGun = soldier.GetHeldGun();
                            bool gunEmpty = soldier.IsReloading ||
                                            soldier.MustReload() ||
                                            (heldGun != null && heldGun.GetCurrentAmmoCount() <= 0);
                            if ((gunEmpty || dist <= 2.15f) && TryConsumeBotRpcBudget(0.35f))
                            {
                                soldier.ToggleInventoryKnife();
                            }
                        }

                        if (dist > 1.15f && !soldier.isInBodyAnimation)
                        {
                            __instance.MoveDirectlyToward(meleeEnemy.transform.position, 0.35f);
                        }

                        if (dist <= seqStrikeRange)
                        {
                            TryExecuteMeleeAttack(__instance, soldier, meleeEnemy);
                        }
                    }

                    if (__instance.HasEnemyAndCanFireAtHim || suppression >= 8f)
                    {
                        // Hold combat stance for at least 6.0s after losing LOS so bots never rapidly stand/prone in grass!
                        _combatStanceHoldUntil[ptr] = Time.time + 6.0f;
                    }

                    // Check if this soldier is a mechanic actively assigned to repair a vehicle
                    VehicleRepairTask repairTask = null;
                    bool isRepairingVehicle = false;
                    if (VisionConfig.EnableSmartTankCrewRepair.Value && soldier.targetDestination != null)
                    {
                        repairTask = soldier.targetDestination.TryCast<VehicleRepairTask>();
                        if (repairTask != null && repairTask.VehicleStillneedsRepair && repairTask.vehToRepair != null)
                        {
                            isRepairingVehicle = true;
                            Vector3 shelterPos = GetShelteredRepairPosition(repairTask.vehToRepair, soldier, out _);
                            repairTask.targetPos = shelterPos;
                            repairTask.pose = SoldierPose.Crouch;
                            if ((__instance.MoveDestination - shelterPos).sqrMagnitude > 0.64f)
                            {
                                __instance.MoveDestination = shelterPos;
                            }
                        }
                    }

                    // Priority 1D: Smart Wall-Side Cover (Stand BEHIND walls relative to incoming fire, never IN FRONT with wall at back)
                    if (VisionConfig.EnableSmartWallSideCover.Value &&
                        !isRepairingVehicle &&
                        !soldier.IsCharging() &&
                        !soldier.UsingBipod() &&
                        !soldier.IsBipodTransitioning())
                    {
                        if (!_nextWallSideCheckTime.TryGetValue(ptr, out float nextWallSide) || Time.time >= nextWallSide)
                        {
                            _nextWallSideCheckTime[ptr] = Time.time + 0.90f;
                            Vector3 threatDir = GetEffectiveThreatDirection(soldier, soldier.joinedSquad, soldier.transform.position, soldier.transform.forward);
                            if (threatDir.sqrMagnitude > 0.01f)
                            {
                                DestinationWithoutCover noCoverDest = soldier.targetDestination != null
                                    ? soldier.targetDestination.TryCast<DestinationWithoutCover>()
                                    : null;

                                if (noCoverDest != null &&
                                    TryFindShelteredSideOfWall(noCoverDest.targetPos, threatDir, soldier.transform.root, out Vector3 shelteredDestPos))
                                {
                                    noCoverDest.targetPos = shelteredDestPos;
                                    noCoverDest.pose = SoldierPose.Crouch;
                                    if ((__instance.MoveDestination - shelteredDestPos).sqrMagnitude > 0.49f)
                                    {
                                        __instance.MoveDestination = shelteredDestPos;
                                    }
                                    _nextWallSideCheckTime[ptr] = Time.time + 3.5f;
                                    WallSideRepositionsCount++;
                                }
                                else if (!__instance.IsMoving() &&
                                         IsPositionOnExposedSideOfWall(soldier.transform.position, threatDir, soldier.transform.root, out _))
                                {
                                    // Soldier is stationary in front of a wall with the wall at their back and open air toward the threat!
                                    if (soldier.joinedSquad != null)
                                    {
                                        soldier.joinedSquad.formationDirection = threatDir;
                                    }

                                    if (TryFindShelteredSideOfWall(soldier.transform.position, threatDir, soldier.transform.root, out Vector3 shelteredPos))
                                    {
                                        if (noCoverDest != null)
                                        {
                                            noCoverDest.targetPos = shelteredPos;
                                            noCoverDest.pose = SoldierPose.Crouch;
                                        }
                                        else
                                        {
                                            // Try upgrading to a sheltered CombatCover first; if still exposed, assign shelteredPos behind the wall
                                            soldier.TryToGetToActualCover();
                                            if (soldier.targetDestination == null ||
                                                IsPositionOnExposedSideOfWall(soldier.targetDestination.GetCoverPosition(), threatDir, soldier.transform.root, out _))
                                            {
                                                DestinationWithoutCover newDest = new DestinationWithoutCover(shelteredPos, SoldierPose.Crouch);
                                                soldier.CoverPosition(newDest.Cast<AiDestination>());
                                            }
                                        }

                                        if ((__instance.MoveDestination - shelteredPos).sqrMagnitude > 0.49f)
                                        {
                                            __instance.MoveDestination = shelteredPos;
                                        }
                                        _nextWallSideCheckTime[ptr] = Time.time + 4.0f;
                                        WallSideRepositionsCount++;
                                    }
                                    else if (soldier.HasDestinationAssigned &&
                                             (__instance.HasEnemyAndCanFireAtHim || suppression >= 5f || soldier.isAlerted))
                                    {
                                        // Back is against a thick/closed building facing incoming fire — seek a sheltered CombatCover node
                                        soldier.TryToGetToActualCover();
                                        _nextWallSideCheckTime[ptr] = Time.time + 3.0f;
                                    }
                                }
                            }
                        }
                    }

                    float proneSuppThreshold = GetEffectiveProneSuppressionThreshold(soldier, vet01);

                    if (VisionConfig.EnforceStandingWhenOutOfCombat.Value && !soldier.IsStealth)
                    {
                        // Clear any sticky default crouch/prone pose field unless stealth-following
                        __instance.pose = SoldierPose.Idle;

                        // Vanilla GetDestinationInsideArea sets DestinationWithoutCover.pose to Random.Range(1, 3) (50% Prone!).
                        // Redirect open-ground destination poses so bots never lie Prone in open fields without reason.
                        if (soldier.targetDestination != null)
                        {
                            DestinationWithoutCover noCoverDest = soldier.targetDestination.TryCast<DestinationWithoutCover>();
                            if (noCoverDest != null && noCoverDest.pose == SoldierPose.Prone && suppression < proneSuppThreshold)
                            {
                                noCoverDest.pose = SoldierPose.Crouch;
                            }
                        }
                    }

                    if (soldier.UsingBipod() || soldier.IsBipodTransitioning())
                    {
                        // Keep current bipod stance locked
                        __instance.actualPose = soldier.m_pose;
                    }
                    else if (isRepairingVehicle)
                    {
                        // Tank crew repairing or skirting around the hull to the sheltered leeward side:
                        // Stay crouched so the tank's armor shields their head/torso!
                        SetBotPose(__instance, soldier, SoldierPose.Crouch);
                    }
                    else if (__instance.IsMoving())
                    {
                        // Moving soldier stance handling:
                        // Note: In vanilla SoldierAI.get_IsMoveTime (0x180562080), when HasEnemyAndCanFireAtHim is true,
                        // IsMoveTime toggles true (2.5s) <-> false (4.5s) on a 7.0s square wave!
                        // Forcing Idle whenever IsMoving() is true while HasEnemyAndCanFireAtHim is true (or right after
                        // crouching when lower eye height loses LOS) caused bots to oscillate Stand <-> Crouch!
                        bool inCombatCrouchLock = _combatCrouchLockUntil.TryGetValue(ptr, out float crouchLockUntil) && Time.time < crouchLockUntil;
                        bool stealthCrouch = soldier.IsStealth &&
                                             soldier.joinedSquad != null &&
                                             soldier.joinedSquad.Leader != null &&
                                             soldier.joinedSquad.Leader.m_pose > SoldierPose.Idle;

                        if (soldier.IsCharging() || inPointBlankMelee)
                        {
                            SetBotPose(__instance, soldier, SoldierPose.Idle, true);
                        }
                        else if (__instance.HasEnemyAndCanFireAtHim && dist > 4.5f)
                        {
                            // Moving while actively engaging a visible enemy (2.5s walk-and-shoot / combat-step phase):
                            // Never crawl Prone while moving; maintain Crouch if LOF from Crouch is clear so stance
                            // stays consistent across both phases of vanilla's 7.0s IsMoveTime cycle!
                            Vector3 targetPos = __instance.visibleTarget != null
                                ? __instance.visibleTarget.GetCenterOfUnit()
                                : (soldier.transform.position + soldier.transform.forward * Mathf.Max(dist, 15f));

                            bool crouchClear = HasClearLineOfFireFromCrouch(soldier, targetPos, dist);
                            bool standClear = !crouchClear && HasClearLineOfFireFromStanding(soldier, targetPos, dist);

                            if (soldier.m_pose == SoldierPose.Prone || __instance.actualPose == SoldierPose.Prone)
                            {
                                SetBotPose(__instance, soldier, standClear ? SoldierPose.Idle : SoldierPose.Crouch);
                            }
                            else if (VisionConfig.NeverShootStandingInOpen.Value &&
                                     !isPeeking &&
                                     !isHighBipodStanding &&
                                     (crouchClear || (!standClear && soldier.m_pose == SoldierPose.Crouch)))
                            {
                                SetBotPose(__instance, soldier, SoldierPose.Crouch);
                            }
                            else
                            {
                                SetBotPose(__instance, soldier, SoldierPose.Idle);
                            }
                        }
                        else if (VisionConfig.EnforceStandingWhenOutOfCombat.Value)
                        {
                            // Moving without an active fireable enemy:
                            // Always lift Prone movers immediately so they never crawl along paths,
                            // but respect _combatCrouchLockUntil before standing from Crouch -> Idle so brief LOS
                            // flickers behind ridges/bushes never cause rapid Stand <-> Crouch loops!
                            if (soldier.m_pose == SoldierPose.Prone || __instance.actualPose == SoldierPose.Prone)
                            {
                                SoldierPose liftPose = (inCombatCrouchLock || suppression >= 28f || stealthCrouch)
                                    ? SoldierPose.Crouch
                                    : SoldierPose.Idle;
                                SetBotPose(__instance, soldier, liftPose);
                            }
                            else if (!inCombatCrouchLock && suppression < 28f && !stealthCrouch)
                            {
                                SetBotPose(__instance, soldier, SoldierPose.Idle);
                            }
                            else if (soldier.m_pose == SoldierPose.Crouch)
                            {
                                __instance.actualPose = SoldierPose.Crouch;
                            }
                        }
                    }
                    else
                    {
                        // Stationary soldier stance redirection
                        if (inPointBlankMelee)
                        {
                            SetBotPose(__instance, soldier, SoldierPose.Idle, true);
                        }
                        else if (isPeeking || isHighBipodStanding)
                        {
                            // Preserve active auto-peek or high-wall bipod stance against vanilla SequentialUpdate overwrites
                            __instance.actualPose = soldier.m_pose;
                        }
                        else if (__instance.HasEnemyAndCanFireAtHim && !soldier.IsCharging())
                        {
                            // Engaging a visible enemy while stationary: NEVER shoot from standing in the open
                            // (unless crouching is blocked by a low wall/hill crest directly in front!)
                            if (VisionConfig.NeverShootStandingInOpen.Value && dist > 4.5f)
                            {
                                Vector3 targetPos = __instance.visibleTarget != null
                                    ? __instance.visibleTarget.GetCenterOfUnit()
                                    : (soldier.transform.position + soldier.transform.forward * Mathf.Max(dist, 15f));

                                bool crouchClear = HasClearLineOfFireFromCrouch(soldier, targetPos, dist);
                                bool standClear = !crouchClear && HasClearLineOfFireFromStanding(soldier, targetPos, dist);
                                bool mustStandToClearWall = !crouchClear &&
                                                            standClear &&
                                                            suppression < maxPeekSupp &&
                                                            !soldier.IsReloading;

                                if (mustStandToClearWall)
                                {
                                    // Crouching/Proning behind this waist-high wall/parapet puts the wall in front of the barrel,
                                    // while Standing clears it — stand up (or stay standing) so the weapon barrel clears the wall!
                                    if (__instance.actualPose != SoldierPose.Idle || soldier.m_pose != SoldierPose.Idle)
                                    {
                                        SetBotPose(__instance, soldier, SoldierPose.Idle);
                                        _noProneUntil[ptr] = Time.time + 10.0f;
                                        _peekRestoreTime[ptr] = Time.time + 4.5f;
                                    }
                                }
                                else if (!crouchClear && soldier.m_pose == SoldierPose.Idle && suppression < 32f)
                                {
                                    // Keep standing if already standing and crouching is obstructed
                                    __instance.actualPose = SoldierPose.Idle;
                                }
                                else
                                {
                                    bool canGoProne = (!_noProneUntil.TryGetValue(ptr, out float noProne) || Time.time >= noProne) &&
                                                      !soldier.IsOnCover() &&
                                                      !soldier.IsTowardWall();

                                    // Hysteresis: if already Prone, stay Prone down to 45% of proneSuppThreshold so stance never
                                    // bounces between Prone and Crouch as suppression decays slightly.
                                    bool shouldBeProne = suppression >= proneSuppThreshold ||
                                                         isGunner ||
                                                         (soldier.m_pose == SoldierPose.Prone && suppression >= proneSuppThreshold * 0.45f);

                                    SoldierPose combatPose = SoldierPose.Crouch;
                                    if (shouldBeProne &&
                                        canGoProne &&
                                        HasClearLineOfFireFromProne(soldier, targetPos, dist))
                                    {
                                        combatPose = SoldierPose.Prone;
                                    }

                                    // Always override __instance.actualPose (which vanilla SequentialUpdate may have just set to Prone!)
                                    if (__instance.actualPose != combatPose || soldier.m_pose != combatPose)
                                    {
                                        SetBotPose(__instance, soldier, combatPose);
                                        if (!_nextStanceDropAllowed.TryGetValue(ptr, out float nextAllowed) || Time.time >= nextAllowed)
                                        {
                                            _nextStanceDropAllowed[ptr] = Time.time + 3.5f;
                                            SelfPreservationDrops++;
                                        }
                                    }
                                }
                            }
                            else
                            {
                                __instance.actualPose = soldier.m_pose;
                            }
                        }
                        else if (VisionConfig.EnforceStandingWhenOutOfCombat.Value)
                        {
                            // Stationary with NO fireable enemy:
                            // Keep defenders crouched when occupying a CombatCover, holding inside an objective capture zone,
                            // sheltered behind front cover/walls, or when they/their squad are alerted!
                            bool inCombatHold = _combatStanceHoldUntil.TryGetValue(ptr, out float holdUntil) && Time.time < holdUntil;
                            bool stealthCrouch = soldier.IsStealth &&
                                                 soldier.joinedSquad != null &&
                                                 soldier.joinedSquad.Leader != null &&
                                                 soldier.joinedSquad.Leader.m_pose > SoldierPose.Idle;
                            CombatCover activeCover = soldier.targetDestination != null
                                ? soldier.targetDestination.TryCast<CombatCover>()
                                : null;
                            bool onActualCombatCover = soldier.IsOnCover() ||
                                                       (activeCover != null && (soldier.transform.position - activeCover.GetCoverPosition()).sqrMagnitude <= 3.2f * 3.2f);
                            MissionObjective currentObj = (soldier.joinedSquad != null && soldier.joinedSquad.currentTask != null)
                                ? soldier.joinedSquad.currentTask.TryCast<MissionObjective>()
                                : null;
                            bool inObjectiveZone = currentObj != null && soldier.PositionIsInsideValidConquerZone(soldier.transform.position, currentObj);
                            bool isSquadAlerted = soldier.isAlerted || (soldier.joinedSquad != null && soldier.joinedSquad.IsEnemyAwareOfThisSquad);
                            Vector3 threatDir = GetEffectiveThreatDirection(soldier, soldier.joinedSquad, soldier.transform.position, soldier.transform.forward);
                            bool isShelteredByWall = VisionConfig.EnableSmartWallSideCover.Value &&
                                                     IsPositionShelteredByFrontCover(soldier.transform.position, threatDir, soldier.transform.root);
                            bool hasMortar = __instance.HeldMortar != null;

                            bool holdDefensiveCrouch = onActualCombatCover || inObjectiveZone || isShelteredByWall || isSquadAlerted;
                            bool coverRequiresStanding = onActualCombatCover &&
                                                         activeCover != null &&
                                                         activeCover.GetCoverPose() == SoldierPose.Idle &&
                                                         !inCombatHold &&
                                                         suppression < 8f &&
                                                         !isSquadAlerted;

                            if ((!holdDefensiveCrouch || coverRequiresStanding) && !inCombatHold && suppression < 8f && !stealthCrouch && !hasMortar)
                            {
                                SetBotPose(__instance, soldier, SoldierPose.Idle);
                            }
                            else if (soldier.m_pose == SoldierPose.Prone &&
                                     (inCombatHold || isGunner || suppression >= proneSuppThreshold * 0.45f) &&
                                     (!_noProneUntil.TryGetValue(ptr, out float noProneHold) || Time.time >= noProneHold))
                            {
                                // Keep Prone soldiers and gunners Prone during combat hold so brief LOS flickers never bounce them to Crouch!
                                __instance.actualPose = SoldierPose.Prone;
                            }
                            else if (suppression < 35f && !stealthCrouch && !hasMortar)
                            {
                                // Hold Crouch in cover / trenches / objective zones (and lift any vanilla 50% Prone pose to Crouch when not heavily suppressed)
                                if (__instance.actualPose != SoldierPose.Crouch || soldier.m_pose != SoldierPose.Crouch)
                                {
                                    SetBotPose(__instance, soldier, SoldierPose.Crouch);
                                }
                            }
                        }
                    }

                    // Gunner bipod surface deployment (only when stationary)
                    if (isGunner && !__instance.IsMoving())
                    {
                        Vector3 targetPos = __instance.visibleTarget != null
                            ? __instance.visibleTarget.GetCenterOfUnit()
                            : (soldier.transform.position + soldier.transform.forward * 25f);
                        HandleGunnerBipodAndWallMounting(__instance, soldier, bipod, targetPos, dist);
                    }

                    // 5. Priority 4 (Rescue & Liberate): Free nearby surrendered friendly comrades
                    float maxLiberateSupp = (personality == SoldierPersonality.Medic_Heroic || personality == SoldierPersonality.Leader_OldGuard)
                        ? 52f
                        : ((personality == SoldierPersonality.Rifleman_Cautious || personality == SoldierPersonality.Medic_Triage) ? 16f : 35f);

                    if (VisionConfig.EnableComradeLiberation.Value &&
                        suppression < maxLiberateSupp &&
                        !soldier.UsingBipod() &&
                        (!__instance.HasEnemyAndCanFireAtHim || dist > 28f || personality == SoldierPersonality.Medic_Heroic))
                    {
                        if (!_nextLiberateCheckTime.TryGetValue(ptr, out float nextLib) || Time.time >= nextLib)
                        {
                            _nextLiberateCheckTime[ptr] = Time.time + 1.5f;
                            TryLiberateNearbyComrade(__instance, soldier, personality);
                        }
                    }
                }
                catch { }
            }

            private static void TryLiberateNearbyComrade(SoldierAI ai, Soldier rescuer, SoldierPersonality personality)
            {
                var allSquads = Squad.AllSquads;
                if (allSquads == null) return;

                string myFaction = rescuer.faction;
                Vector3 myPos = rescuer.transform.position;
                float searchRadius = VisionConfig.LiberateSearchRadius.Value;
                if (personality == SoldierPersonality.Medic_Heroic || personality == SoldierPersonality.Leader_OldGuard)
                    searchRadius *= 1.50f;
                else if (personality == SoldierPersonality.Rifleman_Cautious || personality == SoldierPersonality.Medic_Triage)
                    searchRadius *= 0.60f;

                float bestDistSq = searchRadius * searchRadius;
                Soldier bestSurrendered = null;

                foreach (var kvp in allSquads)
                {
                    Squad sq = kvp.Value;
                    if (sq == null || sq.units == null) continue;
                    var units = sq.units;
                    int c = units.Count;
                    for (int i = 0; i < c; i++)
                    {
                        Soldier f = units[i];
                        if (f == null || !f.IsAlive || f.Pointer == rescuer.Pointer) continue;
                        if (f.faction != myFaction || !f.HasSurrended()) continue;

                        float dSq = (f.transform.position - myPos).sqrMagnitude;
                        if (dSq < bestDistSq)
                        {
                            bestDistSq = dSq;
                            bestSurrendered = f;
                        }
                    }
                }

                if (bestSurrendered != null)
                {
                    if (bestDistSq <= 3.8f * 3.8f)
                    {
                        if (TryConsumeBotRpcBudget(0.45f))
                        {
                            string sqId = rescuer.joinedSquad != null ? rescuer.joinedSquad.InstanceID.ToString() : "";
                            bestSurrendered.UnSurrenderSynched(sqId);
                            ComradesLiberated++;
                            AddCombatExperienceBonus(rescuer, 30f);
                        }
                    }
                    else if (!rescuer.IsSquadLeader())
                    {
                        ai.MoveDirectlyToward(bestSurrendered.transform.position, 2.2f);
                    }
                }
            }
        }

        // =====================================================================
        // PATCH 2: SoldierAI.ProcessAiAccuracy (shared=1, len=1102)
        // Signature: public static float ProcessAiAccuracy(Soldier user, SoldierAI.AiAccuracyMode mode)
        // - Linearly scales AI inaccuracy/spread down by up to 20% over 15 minutes
        // =====================================================================
        [HarmonyPatch(typeof(SoldierAI), nameof(SoldierAI.ProcessAiAccuracy))]
        public static class SoldierAI_ProcessAiAccuracy_Patch
        {
            public static void Postfix(Soldier __0, ref float __result)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value || __0 == null || __result <= 0f)
                        return;

                    float vet01 = GetVeterancy01(__0);
                    float spreadMult = 1.0f;

                    if (VisionConfig.EnableVeteranSystem.Value && vet01 > 0.02f)
                    {
                        float maxReduction = Mathf.Clamp(VisionConfig.VeteranMaxSpreadReduction.Value, 0f, 0.40f);
                        spreadMult *= (1.0f - (vet01 * maxReduction));
                    }

                    SoldierPersonality pers = GetPersonality(__0);
                    if (pers == SoldierPersonality.Marksman_Deadeye && __0.m_pose >= SoldierPose.Crouch)
                    {
                        spreadMult *= 0.88f; // Deadeye marksman 12% tighter crouch/prone precision
                    }
                    else if (pers == SoldierPersonality.Rifleman_Cautious && __0.GetSuppressionValue() > 28f)
                    {
                        float cure = VisionConfig.VeterancyCuresSkittishness.Value ? vet01 : 0f;
                        spreadMult *= Mathf.Lerp(1.12f, 1.00f, cure); // Rookie nerve jitter under heavy suppression, cured by Veterancy
                    }

                    __result *= spreadMult;
                }
                catch { }
            }
        }

        // =====================================================================
        // PATCH 3: SoldierAI.OnSuppressed (shared=1, len=455)
        // - Reduces vanilla's 8.0s cover check cooldown down to 4.8s -> 2.2s
        // - Forces suppressed troops in the open to drop Prone/Crouch & upgrade path to cover
        // =====================================================================
        [HarmonyPatch(typeof(SoldierAI), nameof(SoldierAI.OnSuppressed))]
        public static class SoldierAI_OnSuppressed_Patch
        {
            public static void Postfix(SoldierAI __instance)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value || __instance == null)
                        return;

                    Soldier soldier = __instance.character;
                    if (!IsLocallyOwnedBot(soldier))
                        return;

                    IntPtr ptr = soldier.Pointer;
                    _combatStanceHoldUntil[ptr] = Time.time + 6.0f;

                    // Surviving under suppression builds veteran experience slightly
                    AddCombatExperienceBonus(soldier, 1.5f);

                    float vet01 = GetVeterancy01(soldier);
                    float desiredCooldown = Mathf.Lerp(4.8f, 2.2f, vet01);
                    if (__instance.nextSuppressionCheckTime > Time.time + desiredCooldown)
                    {
                        __instance.nextSuppressionCheckTime = Time.time + desiredCooldown;
                    }

                    if (VisionConfig.EnableSelfPreservationStance.Value &&
                        !soldier.IsOnVehicle() &&
                        !soldier.IsCharging())
                    {
                        TrySeekNearbyCover(__instance, soldier);

                        if (!__instance.IsMoving())
                        {
                            bool canGoProne = (!_noProneUntil.TryGetValue(ptr, out float noProne) || Time.time >= noProne) &&
                                              !soldier.IsOnCover() &&
                                              !soldier.IsTowardWall();

                            float proneThresh = GetEffectiveProneSuppressionThreshold(soldier, vet01);
                            SoldierPose targetPose = (soldier.GetSuppressionValue() >= proneThresh && canGoProne)
                                ? SoldierPose.Prone
                                : SoldierPose.Crouch;

                            if (soldier.m_pose < targetPose)
                            {
                                SetBotPose(__instance, soldier, targetPose);
                                SelfPreservationDrops++;
                            }
                        }
                    }
                }
                catch { }
            }
        }

        // =====================================================================
        // PATCH 4: SoldierAI.UpdateMoveDirection (shared=1, len=712)
        // - Priority 2: Teaches AI soldiers to vault low walls, fences & sandbags
        // =====================================================================
        [HarmonyPatch(typeof(SoldierAI), nameof(SoldierAI.UpdateMoveDirection))]
        public static class SoldierAI_UpdateMoveDirection_Patch
        {
            public static void Postfix(SoldierAI __instance)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value || !VisionConfig.EnableWallVaulting.Value || __instance == null)
                        return;

                    Soldier soldier = __instance.character;
                    if (!IsLocallyOwnedBot(soldier) || soldier.IsOnVehicle())
                        return;

                    IntPtr ptr = soldier.Pointer;
                    if (_nextVaultCheckTime.TryGetValue(ptr, out float nextVault) && Time.time < nextVault)
                        return;

                    _nextVaultCheckTime[ptr] = Time.time + 0.50f;

                    if (!__instance.IsMoving() ||
                        soldier.isInBodyAnimation ||
                        soldier.IsJumping() ||
                        soldier.IsCarryingBody() ||
                        soldier.IsCarried() ||
                        soldier.HasSurrended())
                    {
                        return;
                    }

                    if (soldier.CanVault(out Vector3 vaultPos) && TryConsumeBotRpcBudget(0.35f))
                    {
                        soldier.Vault(vaultPos);
                        _nextVaultCheckTime[ptr] = Time.time + 2.20f;
                        WallsVaulted++;
                    }
                }
                catch { }
            }
        }

        // =====================================================================
        // PATCH 5: SoldierAI.RotateAndFireToward (shared=1, len=1136)
        // - Priority 1: Always at least Crouch (or Prone for MGs) BEFORE shooting when stationary; Gunner wall bipod deployment
        // - Priority 3A: Stop shooting walls + Auto-Peek over low cover/windowsills
        // - Priority 3B: Friendly Fire Avoidance (5-deg cone + 0.65m corridor)
        // - Priority 3C: Anti-stacking personal space (~2.4m firing line spacing)
        // =====================================================================
        [HarmonyPatch(typeof(SoldierAI), nameof(SoldierAI.RotateAndFireToward))]
        public static class SoldierAI_RotateAndFireToward_Patch
        {
            public static bool Prefix(SoldierAI __instance, ref Vector3 __0, float __1, out bool __state)
            {
                __state = false;
                try
                {
                    if (!VisionConfig.EnableMod.Value || __instance == null)
                        return true;

                    __state = __instance.allowFireAtEnemy;
                    Soldier soldier = __instance.character;
                    if (!IsLocallyOwnedBot(soldier))
                        return true;

                    IntPtr ptr = soldier.Pointer;
                    _combatStanceHoldUntil[ptr] = Time.time + 6.0f;

                    float dist = __instance.distFromTarget;
                    if (dist <= 0.1f)
                        dist = Vector3.Distance(soldier.transform.position, __0);

                    // Priority 3D: Smart Anti-Armor Target & Fire Discipline
                    // Stop infantry with small arms from rotating/firing at armored tanks unless they can
                    // hit a visible exposed crew member or penetrate a vehicle part/wheel/track!
                    if (VisionConfig.PreventSmallArmsFireAtTanks.Value &&
                        __instance.visibleTarget != null &&
                        __instance.visibleTarget.IsWheeledVehicleOrTank() &&
                        !soldier.IsOnVehicle())
                    {
                        Vehicle targetVeh = __instance.visibleTarget.TryCast<Vehicle>();
                        if (targetVeh != null)
                        {
                            IntPtr vehPtr = targetVeh.Pointer;
                            if (!_vehicleAimCache.TryGetValue(ptr, out VehicleAimCache aimCache) ||
                                aimCache.VehiclePtr != vehPtr ||
                                Time.time >= aimCache.ExpireTime)
                            {
                                bool canEngage = TryEvaluateVehicleTarget(
                                    soldier,
                                    targetVeh,
                                    dist,
                                    out _,
                                    out Vector3 evalAimPoint,
                                    out bool evalHasCustomAim);

                                aimCache = new VehicleAimCache
                                {
                                    VehiclePtr = vehPtr,
                                    ExpireTime = Time.time + 0.40f,
                                    CanEngage = canEngage,
                                    HasCustomAimPoint = evalHasCustomAim,
                                    CustomAimPoint = evalAimPoint
                                };
                                _vehicleAimCache[ptr] = aimCache;
                            }

                            if (aimCache.CanEngage)
                            {
                                if (aimCache.HasCustomAimPoint)
                                {
                                    // Aim directly at the exposed crewman, un-flattened rubber tire, or penetrable armor plate/track!
                                    __0 = aimCache.CustomAimPoint;
                                    dist = Vector3.Distance(soldier.transform.position, __0);
                                }
                            }
                            else
                            {
                                __instance.targetInWeaponRange = false;
                                __instance.allowFireAtEnemy = false;
                                soldier.SetGunTriggerPressed(false);
                                if (!_nextTankDisciplineCountTime.TryGetValue(ptr, out float nextCount) || Time.time >= nextCount)
                                {
                                    _nextTankDisciplineCountTime[ptr] = Time.time + 3.0f;
                                    UselessTankFirePrevented++;
                                }
                                return false;
                            }
                        }
                    }

                    float vet01 = GetVeterancy01(soldier);
                    bool blockFire = false;
                    bool isMoving = __instance.IsMoving();
                    bool isPeeking = _peekRestoreTime.TryGetValue(ptr, out float peekEnd) && Time.time < peekEnd;
                    bool isHighBipodStanding = _bipodStandUntil.TryGetValue(ptr, out float standUntil) && Time.time < standUntil;
                    bool isGunner = IsMachineGunnerOrHasBipod(soldier, out AttachmentBipod bipod);

                    // Keep actualPose synchronized with m_pose while stationary so MovementRoutine's StopMove(actualPose)
                    // never fights the soldier's current stance between SequentialUpdate ticks.
                    if (!isMoving && !soldier.IsOnVehicle())
                    {
                        __instance.actualPose = soldier.m_pose;
                    }

                    // 0. Priority 0 (CQB): Point-Blank Melee, Bayonet & Butt-Stroke Engagement
                    // When an enemy foot soldier is right in front of us in CQB, stand up, close distance,
                    // suppress point-blank gunfire within ceaseFireRange, and execute bayonet/melee strikes!
                    bool inPointBlankMelee = false;
                    if (dist <= 6.2f &&
                        IsEngagedInPointBlankMelee(
                            __instance,
                            soldier,
                            dist,
                            out Soldier meleeEnemy,
                            out _,
                            out float ceaseFireRange,
                            out float strikeRange) &&
                        meleeEnemy != null)
                    {
                        inPointBlankMelee = true;
                        if (soldier.m_pose != SoldierPose.Idle)
                        {
                            SetBotPose(__instance, soldier, SoldierPose.Idle, true);
                        }

                        Vector3 enemyPos = meleeEnemy.transform.position;
                        if (dist > 1.15f && !soldier.isInBodyAnimation)
                        {
                            if (!_nextMeleeStepTime.TryGetValue(ptr, out float nextStep) || Time.time >= nextStep)
                            {
                                _nextMeleeStepTime[ptr] = Time.time + 0.20f;
                                __instance.MoveDirectlyToward(enemyPos, 0.30f);
                                __instance.moveLookingTarget = true;
                            }
                        }

                        if (dist <= ceaseFireRange)
                        {
                            blockFire = true;
                            Vector3 dirToEnemy = enemyPos - soldier.transform.position;
                            dirToEnemy.y = 0f;
                            if (dirToEnemy.sqrMagnitude > 0.01f)
                            {
                                soldier.RotateToward(dirToEnemy.normalized, __1 * 2.5f);
                            }
                        }

                        if (dist <= strikeRange)
                        {
                            TryExecuteMeleeAttack(__instance, soldier, meleeEnemy);
                        }
                    }

                    // 1. Priority 1: Never Shoot Standing in the Open — Crouch (or Prone for Gunners) BEFORE Shooting
                    // CRITICAL FIX: Only drop from standing to crouch if HasClearLineOfFireFromCrouch is true (or heavily suppressed)
                    // so bots standing behind waist-high walls/sandbags/ridges don't drop into a blocked crouch and oscillate!
                    if (!isMoving &&
                        !inPointBlankMelee &&
                        (VisionConfig.EnableSelfPreservationStance.Value || VisionConfig.NeverShootStandingInOpen.Value) &&
                        !isPeeking &&
                        !isHighBipodStanding &&
                        !soldier.IsOnVehicle() &&
                        !soldier.IsCharging() &&
                        !soldier.UsingBipod())
                    {
                        if (soldier.m_pose == SoldierPose.Idle && dist > 4.5f && dist < 220f)
                        {
                            float curSupp = soldier.GetSuppressionValue();
                            if (curSupp >= 32f || HasClearLineOfFireFromCrouch(soldier, __0, dist))
                            {
                                bool canGoProne = (!_noProneUntil.TryGetValue(ptr, out float noProne) || Time.time >= noProne) &&
                                                  !soldier.IsOnCover() &&
                                                  !soldier.IsTowardWall();

                                SoldierPose fightPose = SoldierPose.Crouch;
                                float proneThresh = GetEffectiveProneSuppressionThreshold(soldier, vet01);
                                if ((curSupp >= proneThresh || isGunner) &&
                                    canGoProne &&
                                    HasClearLineOfFireFromProne(soldier, __0, dist))
                                {
                                    fightPose = SoldierPose.Prone;
                                }

                                SetBotPose(__instance, soldier, fightPose);

                                if (!_nextStanceDropAllowed.TryGetValue(ptr, out float nextAllowed) || Time.time >= nextAllowed)
                                {
                                    // Brief 0.30s pause when dropping from standing so they lower their silhouette before firing
                                    _stanceDropHoldFireUntil[ptr] = Time.time + 0.30f;
                                    _nextStanceDropAllowed[ptr] = Time.time + 3.5f;
                                    SelfPreservationDrops++;
                                }
                            }
                            else
                            {
                                // Standing is required to clear the cover/ridge in front; keep actualPose synced to Idle
                                __instance.actualPose = SoldierPose.Idle;
                            }
                        }

                        if (_stanceDropHoldFireUntil.TryGetValue(ptr, out float holdUntil) && Time.time < holdUntil)
                        {
                            blockFire = true;
                        }
                    }

                    // 2. Priority 3A: Stop Shooting Walls & Auto-Peek Over Cover (or High-Wall Bipod Mount / Corner Sidestep)
                    if (VisionConfig.EnableWallShootingPrevention.Value && !soldier.IsOnVehicle())
                    {
                        float angleToTarget = soldier.AngleToDirection(__0 - soldier.transform.position);
                        if (angleToTarget <= 35f &&
                            (!_nextWallCheckTime.TryGetValue(ptr, out float nextWall) || Time.time >= nextWall))
                        {
                            _nextWallCheckTime[ptr] = Time.time + 0.25f;
                            if (IsMuzzleBlockedByWall(soldier, __0, dist))
                            {
                                _wallBlockedUntil[ptr] = Time.time + 0.28f;
                                WallsBlocked++;

                                // Auto-Peek up one (or two) stances if crouched/prone behind a low wall/sandbag/windowsill:
                                // - Respect _nextPeekAllowedTime cooldown and _poseLockUntil dwell time so bots never bounce
                                // - Require soldier to already be facing the target (<= 25 deg) and not mid-body-animation
                                // - Verify current stance LOF is genuinely blocked AND the higher stance has clear LOF
                                bool canPeekNow = (!_nextPeekAllowedTime.TryGetValue(ptr, out float nextPeek) || Time.time >= nextPeek) &&
                                                  (!_poseLockUntil.TryGetValue(ptr, out float poseLock) || Time.time >= poseLock);
                                GetEffectivePeekParameters(soldier, vet01, out float maxPeekSupp, out _, out _);
                                if (!isMoving &&
                                    canPeekNow &&
                                    VisionConfig.EnableAutoPeekOverCover.Value &&
                                    soldier.GetSuppressionValue() < maxPeekSupp &&
                                    !soldier.IsReloading &&
                                    !soldier.isInBodyAnimation &&
                                    !soldier.UsingBipod() &&
                                    angleToTarget <= 25f)
                                {
                                    bool crouchClear = HasClearLineOfFireFromCrouch(soldier, __0, dist);
                                    bool standClear = !crouchClear && HasClearLineOfFireFromStanding(soldier, __0, dist);

                                    if (soldier.m_pose == SoldierPose.Prone &&
                                        !HasClearLineOfFireFromProne(soldier, __0, dist))
                                    {
                                        if (crouchClear)
                                        {
                                            SetBotPose(__instance, soldier, SoldierPose.Crouch, true);
                                            _noProneUntil[ptr] = Time.time + 10.0f;
                                            _combatStanceHoldUntil[ptr] = Time.time + 6.0f;
                                            _peekRestoreTime[ptr] = Time.time + 5.0f;
                                            _nextStanceDropAllowed[ptr] = Time.time + 5.0f;
                                            AutoPeeksTriggered++;
                                        }
                                        else if (standClear)
                                        {
                                            // Both Prone and Crouch are blocked by a waist-high wall, but Standing clears it!
                                            SetBotPose(__instance, soldier, SoldierPose.Idle, true);
                                            _noProneUntil[ptr] = Time.time + 10.0f;
                                            _combatStanceHoldUntil[ptr] = Time.time + 6.0f;
                                            _peekRestoreTime[ptr] = Time.time + 4.5f;
                                            _nextStanceDropAllowed[ptr] = Time.time + 4.5f;
                                            if (isGunner && bipod != null)
                                            {
                                                _bipodStandUntil[ptr] = Time.time + 4.5f;
                                                soldier.StartCoroutine(soldier.RetryBipodDeployAfterStanding());
                                            }
                                            AutoPeeksTriggered++;
                                        }
                                        else if (!soldier.IsCharging())
                                        {
                                            TrySidestepWallObstruction(__instance, soldier, __0, dist);
                                        }
                                    }
                                    else if (soldier.m_pose == SoldierPose.Crouch && !crouchClear)
                                    {
                                        if (standClear)
                                        {
                                            SetBotPose(__instance, soldier, SoldierPose.Idle, true);
                                            _noProneUntil[ptr] = Time.time + 10.0f;
                                            _combatStanceHoldUntil[ptr] = Time.time + 6.0f;
                                            _peekRestoreTime[ptr] = Time.time + 4.5f;
                                            _nextStanceDropAllowed[ptr] = Time.time + 4.5f;
                                            if (isGunner && bipod != null)
                                            {
                                                _bipodStandUntil[ptr] = Time.time + 4.5f;
                                                soldier.StartCoroutine(soldier.RetryBipodDeployAfterStanding());
                                            }
                                            AutoPeeksTriggered++;
                                        }
                                        else if (!soldier.IsCharging())
                                        {
                                            // Both Crouch and Standing are blocked (vertical doorframe / pillar / tall wall edge) — step sideways!
                                            TrySidestepWallObstruction(__instance, soldier, __0, dist);
                                        }
                                    }
                                    else if (soldier.m_pose == SoldierPose.Idle && !soldier.IsCharging())
                                    {
                                        // Already Standing and muzzle is still blocked by a vertical wall edge/pillar — step sideways!
                                        TrySidestepWallObstruction(__instance, soldier, __0, dist);
                                    }
                                }
                            }
                        }

                        if (_wallBlockedUntil.TryGetValue(ptr, out float wallUntil) && Time.time < wallUntil)
                        {
                            blockFire = true;
                        }
                    }

                    // 3. Priority 3B: Friendly Fire Avoidance (5-deg Cone + 0.65m Corridor)
                    // Hold fire cleanly without calling MoveFPS against StopMove (which caused upper-body aim/spine snapping!)
                    if (VisionConfig.EnableFriendlyFireAvoidance.Value)
                    {
                        if (!_nextFFCheckTime.TryGetValue(ptr, out float nextFF) || Time.time >= nextFF)
                        {
                            _nextFFCheckTime[ptr] = Time.time + 0.25f;
                            if (IsFriendlyInLineOfFire(soldier, __0, dist, vet01, out _))
                            {
                                _ffBlockedUntil[ptr] = Time.time + 0.30f;
                                FriendlyFirePrevented++;
                            }
                        }

                        if (_ffBlockedUntil.TryGetValue(ptr, out float ffUntil) && Time.time < ffUntil)
                        {
                            blockFire = true;
                        }
                    }

                    // 4. Priority 3C: Anti-Stacking Personal Space (Smooth Step Aside When Overlapping)
                    // Never call MoveFPS on a stationary soldier (fighting MovementRoutine's StopMove caused severe jitter).
                    // Instead, if two non-prone, non-cover soldiers are genuinely overlapping (< 1.25m), issue a single
                    // natural MoveDirectlyToward step on a 6.0s cooldown.
                    if (VisionConfig.EnableAntiStacking.Value &&
                        !isMoving &&
                        soldier.m_pose != SoldierPose.Prone &&
                        !soldier.IsOnVehicle() &&
                        !soldier.IsOnCover() &&
                        !soldier.IsTowardWall() &&
                        !soldier.UsingBipod() &&
                        !soldier.isInBodyAnimation)
                    {
                        if (!_nextAntiStackTime.TryGetValue(ptr, out float nextAS) || Time.time >= nextAS)
                        {
                            _nextAntiStackTime[ptr] = Time.time + 2.5f;
                            if (TryGetAntiStackPush(soldier, out Vector3 pushDir))
                            {
                                Vector3 curPos = soldier.transform.position;
                                if (!IsDirectionBlockedByEnvironment(curPos + Vector3.up * 0.75f, pushDir, 1.50f, soldier.transform.root))
                                {
                                    __instance.MoveDirectlyToward(curPos + pushDir * 1.40f, 0.45f);
                                    _nextAntiStackTime[ptr] = Time.time + 6.0f;
                                    AntiStackNudges++;
                                }
                            }
                        }
                    }

                    // Apply trigger block before RotateAndFireToward executes so the bot still aims/rotates
                    // toward the enemy without firing into a wall or a squadmate's back
                    if (blockFire)
                    {
                        __instance.allowFireAtEnemy = false;
                        soldier.SetGunTriggerPressed(false);
                    }
                }
                catch { }
                return true;
            }

            public static void Postfix(SoldierAI __instance, bool __state)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value || __instance == null)
                        return;

                    // Restore original allowFireAtEnemy state for next tick without running unthrottled per-frame raycasts
                    __instance.allowFireAtEnemy = __state;
                }
                catch { }
            }
        }

        // =====================================================================
        // PATCH 5B: SoldierAI.MovementRoutine (shared=1, len=930)
        // - In vanilla MovementRoutine (0x18055D486 - 0x18055D706), when a soldier is stationary
        //   and not actively firing at a visible target, vanilla calls StopMove() and LookForward(),
        //   which NEVER rotates soldier.transform.forward! As a result, stationary defenders keep
        //   staring in whatever random direction their last path step pointed (often sideways or
        //   at an empty edge of the map).
        // - Smoothly turns stationary, un-engaged soldiers to face their Known Threat Sector
        //   (using 18 deg -> 5 deg angular hysteresis so they hold a rock-steady overwatch posture
        //   once aligned, while leaving quiet unspotted flanks unwatched).
        // =====================================================================
        [HarmonyPatch(typeof(SoldierAI), nameof(SoldierAI.MovementRoutine))]
        public static class SoldierAI_MovementRoutine_Patch
        {
            public static void Postfix(SoldierAI __instance, float __0)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value ||
                        !VisionConfig.EnableThreatSectorConcentration.Value ||
                        __instance == null)
                    {
                        return;
                    }

                    Soldier soldier = __instance.character;
                    if (!IsLocallyOwnedBot(soldier))
                        return;

                    IntPtr ptr = soldier.Pointer;

                    if (__instance.IsMoving() ||
                        __instance.HasEnemyAndCanFireAtHim ||
                        __instance.HeldMortar != null ||
                        soldier.IsOnVehicle() ||
                        soldier.UsingBipod() ||
                        soldier.IsBipodTransitioning() ||
                        soldier.isInBodyAnimation ||
                        soldier.isInHandsAnimation ||
                        soldier.HasSurrended() ||
                        soldier.IsCarried() ||
                        soldier.IsCarryingBody())
                    {
                        _isTurningToWatchSector.Remove(ptr);
                        return;
                    }

                    if (TryGetKnownThreatSectorDirection(soldier, out Vector3 watchDir, out _) &&
                        watchDir.sqrMagnitude > 0.01f)
                    {
                        float angle = soldier.AngleToDirection(watchDir);
                        if (_isTurningToWatchSector.Contains(ptr))
                        {
                            if (angle <= 4.0f)
                            {
                                _isTurningToWatchSector.Remove(ptr);
                            }
                            else
                            {
                                soldier.RotateToward(watchDir, __0);
                            }
                        }
                        else if (angle > 14.0f)
                        {
                            _isTurningToWatchSector.Add(ptr);
                            ThreatSectorOrientationsCount++;
                            soldier.RotateToward(watchDir, __0);
                        }
                    }
                }
                catch { }
            }
        }

        // =====================================================================
        // PATCH 6: SquadSoldierLabel.RefreshVisual (shared=1, len=212)
        // - Appends [Vet I], [Vet II], [Vet III ★] & rank color in Squad Roster UI
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
                        nameLabel.color = new Color(0.95f, 0.82f, 0.55f, 1f); // Warm Bronze
                    else if (tier == 2)
                        nameLabel.color = new Color(0.85f, 0.95f, 1.00f, 1f); // Crisp Silver
                    else if (tier >= 3)
                        nameLabel.color = new Color(1.00f, 0.86f, 0.22f, 1f); // Veteran Gold
                }
                catch { }
            }
        }

        // =====================================================================
        // PATCH 7: VehicleRepairTask.GetCoverPosition (shared=1, len=177)
        // - Vanilla returns vehToRepair.transform.position (the center of the tank!).
        // - Redirects the repair destination to the sheltered leeward/rear side of the
        //   tank hull (opposite the enemy threat / turret direction) so the crewman
        //   walks around the hull and uses the tank's armor as solid cover.
        // =====================================================================
        [HarmonyPatch(typeof(VehicleRepairTask), nameof(VehicleRepairTask.GetCoverPosition))]
        public static class VehicleRepairTask_GetCoverPosition_Patch
        {
            public static void Postfix(VehicleRepairTask __instance, ref Vector3 __result)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value ||
                        !VisionConfig.EnableSmartTankCrewRepair.Value ||
                        __instance == null)
                    {
                        return;
                    }

                    Vehicle veh = __instance.vehToRepair;
                    if (!IsLocallyOwnedVehicle(veh) || veh.transform == null)
                        return;

                    Vector3 shelterPos = GetShelteredRepairPosition(veh, null, out _);
                    __instance.targetPos = shelterPos;
                    __instance.pose = SoldierPose.Crouch;
                    __result = shelterPos;
                }
                catch { }
            }
        }

        // =====================================================================
        // PATCH 8: VehicleRepairTask.IsReached (shared=1, len=328)
        // - Vanilla returns true whenever the mechanic is within 5.5m of the tank's center,
        //   which is immediately true the instant he pops out of his hatch on the exposed
        //   front or side in the enemy firing line!
        // - Requires the mechanic to actually reach the sheltered leeward hemisphere/point
        //   behind the tank hull before starting the vulnerable repair animation (with a
        //   6.5s safety fallback if the rear of the tank is wedged against an obstacle).
        // =====================================================================
        [HarmonyPatch(typeof(VehicleRepairTask), nameof(VehicleRepairTask.IsReached))]
        public static class VehicleRepairTask_IsReached_Patch
        {
            public static void Postfix(VehicleRepairTask __instance, Vector3 __0, ref bool __result)
            {
                try
                {
                    if (!__result ||
                        !VisionConfig.EnableMod.Value ||
                        !VisionConfig.EnableSmartTankCrewRepair.Value ||
                        __instance == null)
                    {
                        return;
                    }

                    // If the vehicle is already fully repaired, let OnCoverReached run immediately so the crewman re-mounts
                    if (!__instance.VehicleStillneedsRepair)
                        return;

                    Vehicle veh = __instance.vehToRepair;
                    if (!IsLocallyOwnedVehicle(veh) || veh.transform == null)
                        return;

                    IntPtr taskPtr = __instance.Pointer;
                    if (!_repairTaskStartTime.TryGetValue(taskPtr, out float startTime))
                    {
                        startTime = Time.time;
                        _repairTaskStartTime[taskPtr] = startTime;
                    }

                    // Safety timeout: if the crewman has been trying to get around the tank for > 6.5s
                    // (e.g., tank is wedged against a building or cliff), allow repair from current < 5.5m spot
                    if (Time.time - startTime > 6.5f)
                        return;

                    Vector3 shelterPos = GetShelteredRepairPosition(veh, null, out Vector3 shelterDir);
                    Vector3 vehPos = veh.transform.position;

                    Vector3 toShelter = __0 - shelterPos;
                    toShelter.y = 0f;
                    if (toShelter.sqrMagnitude <= 2.25f * 2.25f)
                        return;

                    Vector3 toSoldier = __0 - vehPos;
                    toSoldier.y = 0f;
                    if (toSoldier.sqrMagnitude > 0.04f && Vector3.Dot(toSoldier.normalized, shelterDir) >= 0.30f)
                        return;

                    // Mechanic is still on the exposed side / front in the firing line — keep moving around the hull!
                    __result = false;
                }
                catch { }
            }
        }

        // =====================================================================
        // PATCH 9: VehicleRepairTask.OnCoverReached (shared=1, len=720)
        // - Forces the mechanic into Crouch behind the tank hull while repairing
        // - Holds off starting the locked repair animation if an enemy has flanked
        //   within 35m with direct LOS and heavy suppression (> 55) on the mechanic
        // =====================================================================
        [HarmonyPatch(typeof(VehicleRepairTask), nameof(VehicleRepairTask.OnCoverReached))]
        public static class VehicleRepairTask_OnCoverReached_Patch
        {
            public static bool Prefix(VehicleRepairTask __instance, Soldier __0)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value ||
                        !VisionConfig.EnableSmartTankCrewRepair.Value ||
                        __instance == null ||
                        __0 == null)
                    {
                        return true;
                    }

                    if (!__instance.VehicleStillneedsRepair || !__0.IsAI() || !IsLocallyOwnedBot(__0))
                        return true;

                    // Keep the crewman crouched behind the tank's armor
                    if (__0.aiController != null)
                    {
                        SetBotPose(__0.aiController, __0, SoldierPose.Crouch);

                        // If an enemy has flanked around the hull at close range (< 35m) and is heavily suppressing
                        // the mechanic directly, pause starting the locked repair animation so he can defend himself first
                        if (__0.GetSuppressionValue() > 55f &&
                            __0.aiController.HasEnemyAndCanFireAtHim &&
                            __0.aiController.distFromTarget > 0.1f &&
                            __0.aiController.distFromTarget < 35f)
                        {
                            return false;
                        }
                    }
                    else
                    {
                        __0.SetPose(SoldierPose.Crouch, true, false, 0.2f);
                    }

                    if (_countedRepairTasks.Add(__instance.Pointer))
                    {
                        ShelteredRepairsCount++;
                    }
                }
                catch { }
                return true;
            }
        }

        // =====================================================================
        // PATCH 10: CombatCover.IsCoverAvailable (shared=0, len=330)
        // - Vanilla only checks Vector3.Angle(coverForward, shootDirection) < localLookAngleWideness.
        // - Many walls/houses have omnidirectional (180/360 deg) or two-sided CombatCover nodes,
        //   causing defenders to pick cover nodes on the EXPOSED front side of a wall (with the
        //   wall at their back and open air toward the enemy).
        // - Rejects any CombatCover whose position has open air toward +shootDirection and a
        //   vertical wall directly behind it (-shootDirection within 1.95m).
        // =====================================================================
        [HarmonyPatch(typeof(CombatCover), nameof(CombatCover.IsCoverAvailable))]
        public static class CombatCover_IsCoverAvailable_Patch
        {
            public static void Postfix(CombatCover __instance, Vector3 __0, ref bool __result)
            {
                try
                {
                    if (!__result ||
                        !VisionConfig.EnableMod.Value ||
                        !VisionConfig.EnableSmartWallSideCover.Value ||
                        __instance == null)
                    {
                        return;
                    }

                    Vector3 threatDir = __0;
                    threatDir.y = 0f;
                    if (threatDir.sqrMagnitude < 0.001f)
                        return;

                    threatDir.Normalize();

                    // For directional wall cover nodes, cap allowed look angle at 78 deg so soldiers don't
                    // pick side/back-facing wall cover nodes that leave them exposed to the threat vector
                    if (__instance.localLookAngleWideness < 175f)
                    {
                        Vector3 coverFwd = __instance.GetLookRotation() * Vector3.forward;
                        coverFwd.y = 0f;
                        if (coverFwd.sqrMagnitude > 0.001f && Vector3.Angle(coverFwd, threatDir) > 78f)
                        {
                            __result = false;
                            return;
                        }
                    }

                    // Reject cover nodes on the exposed front face of a wall (no cover in +threatDir, wall in -threatDir)
                    // Cache per (cover.Pointer, 8-way compass octant) so CoverManager.GetCovers never runs hundreds of
                    // uncached raycasts across the same cover nodes in a single frame!
                    float angleDeg = Mathf.Atan2(threatDir.x, threatDir.z) * Mathf.Rad2Deg;
                    if (angleDeg < 0f) angleDeg += 360f;
                    int octant = Mathf.FloorToInt((angleDeg + 22.5f) / 45f) & 7;
                    long cacheKey = ((long)__instance.Pointer << 4) | (uint)octant;

                    if (!_coverExposureCache.TryGetValue(cacheKey, out bool isExposed))
                    {
                        Vector3 coverPos = __instance.GetCoverPosition();
                        isExposed = IsPositionOnExposedSideOfWall(coverPos, threatDir, null, out _);
                        _coverExposureCache[cacheKey] = isExposed;
                    }

                    if (isExposed)
                    {
                        __result = false;
                    }
                }
                catch { }
            }
        }

        // =====================================================================
        // PATCH 11: Squad.SendUnitsToCovers & Squad.GetCoveredPositionInArea
        // - In vanilla, when a defending squad reaches its objective (HoldArea -> SendUnitsToCovers),
        //   formationDirection can still be set to the squad's travel vector toward the objective
        //   center rather than toward the enemy faction's territory or active visible targets!
        // - Ensures formationDirection points toward the actual enemy threat before CoverManager.GetCovers runs.
        // =====================================================================
        [HarmonyPatch(typeof(Squad), nameof(Squad.SendUnitsToCovers))]
        public static class Squad_SendUnitsToCovers_Patch
        {
            public static void Prefix(Squad __instance, Vector3 __0)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value ||
                        !VisionConfig.EnableSmartWallSideCover.Value ||
                        __instance == null)
                    {
                        return;
                    }

                    if (__instance.Leader != null && !IsLocallyOwnedBot(__instance.Leader))
                        return;

                    Vector3 threatDir = GetEffectiveThreatDirection(__instance.Leader, __instance, __0, __instance.formationDirection);
                    if (threatDir.sqrMagnitude > 0.01f)
                    {
                        __instance.formationDirection = threatDir;
                    }
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(Squad), nameof(Squad.GetCoveredPositionInArea))]
        public static class Squad_GetCoveredPositionInArea_Patch
        {
            public static void Prefix(Squad __instance)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value ||
                        !VisionConfig.EnableSmartWallSideCover.Value ||
                        __instance == null)
                    {
                        return;
                    }

                    if (__instance.Leader != null && !IsLocallyOwnedBot(__instance.Leader))
                        return;

                    Vector3 refPos = (__instance.Leader != null && __instance.Leader.transform != null)
                        ? __instance.Leader.transform.position
                        : Vector3.zero;
                    Vector3 threatDir = GetEffectiveThreatDirection(__instance.Leader, __instance, refPos, __instance.formationDirection);
                    if (threatDir.sqrMagnitude > 0.01f)
                    {
                        __instance.formationDirection = threatDir;
                    }
                }
                catch { }
            }
        }

        // =====================================================================
        // Helper & Patches 12-14: Death Screen Kill Attribution (Who, What, Friendly Fire, Hit & Distance)
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

        private static string FormatSoldierIdentity(Soldier s)
        {
            if (s == null)
                return "";

            string rankPrefix;
            if (s.rank == Rank.corporal)
                rankPrefix = "Cpl. ";
            else if (s.rank == Rank.seargent)
                rankPrefix = "Sgt. ";
            else if (s.rank == Rank.first_seargent)
                rankPrefix = "1st Sgt. ";
            else if ((int)s.rank > (int)Rank.first_seargent)
                rankPrefix = "Lt. ";
            else
                rankPrefix = "Pvt. ";

            string name = string.IsNullOrWhiteSpace(s.name_surname) ? "Unknown Soldier" : s.name_surname.Replace('_', ' ').Trim();
            while (name.Contains("  "))
                name = name.Replace("  ", " ");

            int bracketIdx = name.IndexOf(" [", StringComparison.Ordinal);
            if (bracketIdx >= 0)
                name = name.Substring(0, bracketIdx);

            return rankPrefix + name;
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

        private static string GetShooterWeaponOrVehicleName(Soldier shooter)
        {
            if (shooter == null)
                return "";

            if (shooter.IsOnVehicle())
            {
                Vehicle veh = shooter.GetCurrentVehicle();
                string vehName = (veh != null && !string.IsNullOrWhiteSpace(veh.name))
                    ? CleanUnityObjectName(veh.name)
                    : "Vehicle";

                ItemObject heldInVeh = shooter.GetHeldItem();
                if (heldInVeh != null && !string.IsNullOrWhiteSpace(heldInVeh.name))
                    return $"{CleanUnityObjectName(heldInVeh.name)} ({vehName})";

                return vehName;
            }

            ItemObject held = shooter.GetHeldItem();
            if (held != null && !string.IsNullOrWhiteSpace(held.name))
                return CleanUnityObjectName(held.name);

            GenericGun curWep = shooter.GetHeldGun();
            if (curWep != null && !string.IsNullOrWhiteSpace(curWep.name))
                return CleanUnityObjectName(curWep.name);

            return "";
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

        [HarmonyPatch(typeof(Corvostudio.Weapons.Explosion), nameof(Corvostudio.Weapons.Explosion.CreateExplosion))]
        public static class Explosion_CreateExplosion_Patch
        {
            public static void Prefix(Vector3 __0, float __1, float __2, float __3, Soldier __4, GameObject __5, HitType __6, bool __7)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value || !VisionConfig.EnableDeathScreenAttribution.Value)
                        return;

                    string expSource = "";
                    if (__5 != null)
                    {
                        ItemGrenade gren = __5.GetComponent<ItemGrenade>();
                        if (gren == null)
                            gren = __5.GetComponentInParent<ItemGrenade>();

                        if (gren != null && !string.IsNullOrWhiteSpace(gren.name))
                        {
                            expSource = CleanUnityObjectName(gren.name);
                        }
                        else if (!__7)
                        {
                            Vehicle expVeh = __5.GetComponentInParent<Vehicle>();
                            if (expVeh != null && !string.IsNullOrWhiteSpace(expVeh.name))
                                expSource = CleanUnityObjectName(expVeh.name);
                        }
                    }

                    if (string.IsNullOrEmpty(expSource))
                    {
                        if (__7)
                        {
                            expSource = "Artillery / Mortar Barrage";
                        }
                        else if (__4 != null && __4.IsOnVehicle())
                        {
                            Vehicle shooterVeh = __4.GetCurrentVehicle();
                            expSource = (shooterVeh != null && !string.IsNullOrWhiteSpace(shooterVeh.name))
                                ? $"{CleanUnityObjectName(shooterVeh.name)} (HE Shell)"
                                : "Vehicle HE Shell";
                        }
                        else if (__4 != null)
                        {
                            string heldName = GetShooterWeaponOrVehicleName(__4);
                            expSource = !string.IsNullOrEmpty(heldName) ? heldName : "Grenade / Explosive";
                        }
                        else
                        {
                            expSource = "Explosion";
                        }
                    }

                    _activeExplosionWeaponName = expSource;
                    _activeExplosionIsArtillery = __7;

                    // Also record blast attribution if the player is within blast radius in case direct blast kills them
                    Soldier player = Soldier.CurrentControlledSoldierOrNull();
                    if (player != null && player.Pointer != IntPtr.Zero)
                    {
                        float blastDist = Vector3.Distance(__0, player.GetCenterOfUnit());
                        if (blastDist <= Mathf.Max(14f, __1 * 3.5f))
                        {
                            int distMeters = (__4 != null && __4.Pointer != player.Pointer)
                                ? Mathf.RoundToInt(Vector3.Distance(__4.GetCenterOfUnit(), player.GetCenterOfUnit()))
                                : Mathf.RoundToInt(blastDist);

                            bool isSelf = __4 != null && __4.Pointer == player.Pointer;
                            bool isFF = __4 != null && !isSelf && !string.IsNullOrEmpty(__4.faction) && __4.faction == player.faction;

                            _lastDamageByVictim[player.Pointer] = new DamageAttribution
                            {
                                AttackerName = __4 != null ? FormatSoldierIdentity(__4) : "",
                                IsSelfInflicted = isSelf,
                                IsFriendlyFire = isFF,
                                WeaponOrSource = expSource,
                                HitDetail = "Explosion",
                                DistanceMeters = Mathf.Max(0, distMeters),
                                Timestamp = Time.time
                            };
                        }
                    }
                }
                catch { }
            }

            public static void Postfix()
            {
                _activeExplosionWeaponName = null;
                _activeExplosionIsArtillery = false;
            }
        }

        [HarmonyPatch(typeof(Bullet), nameof(Bullet.BulletDamage))]
        public static class Bullet_BulletDamage_Patch
        {
            public static void Prefix(ImpactSpecifier __0, BulletData __1, Vector3 __2, int __3, Soldier __4)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value || !VisionConfig.EnableDeathScreenAttribution.Value || __0 == null)
                        return;

                    Soldier victim = null;
                    string hitDetail = "Torso";

                    BodyPart bp = __0.TryCast<BodyPart>();
                    if (bp != null && bp.unit != null)
                    {
                        victim = bp.unit.TryCast<Soldier>();
                        hitDetail = FormatBodyPart(bp.bodyPart);
                    }
                    else
                    {
                        ItemHelmet helmet = __0.TryCast<ItemHelmet>();
                        if (helmet != null && helmet.User != null)
                        {
                            victim = helmet.User;
                            hitDetail = "Headshot (Helmet)";
                        }
                        else
                        {
                            VehicleDamagablePart vPart = __0.TryCast<VehicleDamagablePart>();
                            if (vPart != null && vPart.rootVeh != null)
                            {
                                Soldier curPlayer = Soldier.CurrentControlledSoldierOrNull();
                                if (curPlayer != null &&
                                    curPlayer.IsOnVehicle() &&
                                    curPlayer.GetCurrentVehicle() != null &&
                                    curPlayer.GetCurrentVehicle().Pointer == vPart.rootVeh.Pointer)
                                {
                                    victim = curPlayer;
                                    hitDetail = "Vehicle Hit";
                                }
                            }
                        }
                    }

                    if (victim == null || !IsPlayerCharacter(victim))
                        return;

                    bool fromExplosion = !string.IsNullOrEmpty(_activeExplosionWeaponName);
                    string weaponOrSource = fromExplosion
                        ? _activeExplosionWeaponName
                        : GetShooterWeaponOrVehicleName(__4);

                    if (fromExplosion)
                    {
                        hitDetail = hitDetail.StartsWith("Headshot", StringComparison.Ordinal)
                            ? $"Shrapnel {hitDetail}"
                            : $"Shrapnel ({hitDetail})";
                    }

                    bool isSelf = __4 != null && __4.Pointer == victim.Pointer;
                    bool isFF = __4 != null && !isSelf && !string.IsNullOrEmpty(__4.faction) && __4.faction == victim.faction;

                    int distMeters = __3;
                    if ((fromExplosion || distMeters <= 0) && __4 != null && !isSelf)
                    {
                        distMeters = Mathf.RoundToInt(Vector3.Distance(__4.GetCenterOfUnit(), victim.GetCenterOfUnit()));
                    }

                    _lastDamageByVictim[victim.Pointer] = new DamageAttribution
                    {
                        AttackerName = __4 != null ? FormatSoldierIdentity(__4) : (_activeExplosionIsArtillery ? "Artillery Support" : ""),
                        IsSelfInflicted = isSelf,
                        IsFriendlyFire = isFF,
                        WeaponOrSource = weaponOrSource,
                        HitDetail = hitDetail,
                        DistanceMeters = Mathf.Max(0, distMeters),
                        Timestamp = Time.time
                    };
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(DeathPanel), nameof(DeathPanel.ShowDeath))]
        public static class DeathPanel_ShowDeath_Patch
        {
            public static void Postfix(Soldier __0)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value || !VisionConfig.EnableDeathScreenAttribution.Value || __0 == null)
                        return;

                    DeathPanel dp = DeathPanel.instance;
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

                        // Disable Best-Fit auto-shrinking so adding "(Age XX)" and multi-line attribution
                        // never causes Unity to shrink the birth/death years text to fit the original box.
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

                    if (!_lastDamageByVictim.TryGetValue(__0.Pointer, out DamageAttribution attr) ||
                        Time.time - attr.Timestamp > 120f)
                    {
                        if (yearLabel != null && !string.IsNullOrEmpty(yearLineWithAge))
                            yearLabel.text = $"<size={yearFontSize}>{yearLineWithAge}</size>";

                        if (dp != null)
                            ActivateInstantDeathPanel(dp, __0.Pointer);

                        ActiveDeathAttributionExpireTime = 0f;
                        ActiveDeathAttributionText = null;
                        return;
                    }

                    bool bledOut = (Time.time - attr.Timestamp) > 1.5f;
                    string actionVerb = bledOut ? "Bled out from wounds by" : "Killed by";

                    string prefixTag = "";
                    string richColor = "#E8E2D0";
                    string subColor = "#CFC8B8";
                    if (attr.IsSelfInflicted)
                    {
                        prefixTag = "[SELF-INFLICTED] ";
                        richColor = "#FFB347";
                        subColor = "#FFD08A";
                    }
                    else if (attr.IsFriendlyFire)
                    {
                        prefixTag = "[FRIENDLY FIRE] ";
                        richColor = "#FF5555";
                        subColor = "#FF8888";
                    }

                    string whoLine;
                    if (attr.IsSelfInflicted)
                    {
                        string ownSource = !string.IsNullOrEmpty(attr.WeaponOrSource) ? attr.WeaponOrSource : "Explosion";
                        whoLine = $"{prefixTag}Killed by own {ownSource}";
                    }
                    else
                    {
                        string attacker = !string.IsNullOrEmpty(attr.AttackerName)
                            ? attr.AttackerName
                            : (!string.IsNullOrEmpty(attr.WeaponOrSource) ? attr.WeaponOrSource : "Enemy Fire");
                        whoLine = $"{prefixTag}{actionVerb} {attacker}";
                    }

                    whoLine = whoLine.Replace('_', ' ').Trim();
                    while (whoLine.Contains("  "))
                        whoLine = whoLine.Replace("  ", " ");

                    List<string> detailParts = new List<string>(3);
                    if (!attr.IsSelfInflicted && !string.IsNullOrEmpty(attr.WeaponOrSource))
                    {
                        string cleanWep = attr.WeaponOrSource.Replace('_', ' ').Trim();
                        while (cleanWep.Contains("  "))
                            cleanWep = cleanWep.Replace("  ", " ");
                        if (!string.IsNullOrEmpty(cleanWep) &&
                            !whoLine.EndsWith(cleanWep, StringComparison.OrdinalIgnoreCase))
                        {
                            detailParts.Add(cleanWep);
                        }
                    }

                    if (!string.IsNullOrEmpty(attr.HitDetail))
                    {
                        string cleanHit = attr.HitDetail.Replace('_', ' ').Trim();
                        while (cleanHit.Contains("  "))
                            cleanHit = cleanHit.Replace("  ", " ");
                        if (!string.IsNullOrEmpty(cleanHit) &&
                            !whoLine.EndsWith(cleanHit, StringComparison.OrdinalIgnoreCase) &&
                            (detailParts.Count == 0 || !string.Equals(detailParts[0], cleanHit, StringComparison.OrdinalIgnoreCase)))
                        {
                            detailParts.Add(cleanHit);
                        }
                    }

                    if (!attr.IsSelfInflicted || attr.DistanceMeters > 0)
                    {
                        detailParts.Add($"{attr.DistanceMeters}m");
                    }

                    string howLine = detailParts.Count > 0 ? string.Join("  \u2022  ", detailParts) : "";

                    // 1. Update native DeathPanel UI text as a centered multi-line obituary stack beneath birth/death years + age
                    bool shownOnNativeDeathPanel = false;
                    if (yearLabel != null)
                    {
                        yearLabel.text = !string.IsNullOrEmpty(howLine)
                            ? $"<size={yearFontSize}>{yearLineWithAge}</size>\n<size=27><color={richColor}>{whoLine}</color></size>\n<size=23><color={subColor}>{howLine}</color></size>"
                            : $"<size={yearFontSize}>{yearLineWithAge}</size>\n<size=27><color={richColor}>{whoLine}</color></size>";
                        shownOnNativeDeathPanel = true;
                    }

                    if (dp != null)
                        ActivateInstantDeathPanel(dp, __0.Pointer);

                    // 2. Only activate the OnGUI fallback box if native DeathPanel is missing or hidden by a third-party mod
                    if (shownOnNativeDeathPanel)
                    {
                        ActiveDeathAttributionExpireTime = 0f;
                        ActiveDeathAttributionText = null;
                    }
                    else
                    {
                        ActiveDeathAttributionText = !string.IsNullOrEmpty(howLine)
                            ? $"{whoLine}\n{howLine}"
                            : whoLine;
                        ActiveDeathAttributionIsFF = attr.IsFriendlyFire;
                        ActiveDeathAttributionIsSelf = attr.IsSelfInflicted;
                        ActiveDeathAttributionStartTime = Time.unscaledTime;
                        ActiveDeathAttributionExpireTime = Time.unscaledTime + 8.5f;
                        ActiveDeathAttributionVictimPtr = __0.Pointer;
                    }
                }
                catch { }
            }
        }

        private static void ActivateInstantDeathPanel(DeathPanel dp, IntPtr victimPtr)
        {
            if (dp == null)
                return;

            DisableDeathPanelRaycasts(dp);

            // Sample the legacy Animation at 72% (peak full-page visibility) and stop it so
            // text_reason, text_name, and text_year appear immediately all at once instead of staggering.
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
            _deathPanelHoldUntilUnscaledTime = Time.unscaledTime + 3.85f;
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
            if (dp == null)
                return;

            // Advance the legacy Animation to its exact end-of-clip state (100%) and sample it once
            // so all animated components on DeathPanel return to their resting post-death state.
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

                // Dismiss immediately as soon as squad/teammate selection or respawn view opens,
                // or if the player presses the skip key, or already controls a new living soldier.
                if (PlayerGUI.IsSelectingSquad() ||
                    RespawnPanel.InRespawningView ||
                    (elapsed > 0.35f && DeathPanel.IsSkipping()))
                {
                    _deathPanelHoldUntilUnscaledTime = now;
                }
                else if (elapsed > 0.45f)
                {
                    Soldier cur = Soldier.CurrentControlledSoldierOrNull();
                    if (cur != null && cur.IsAlive && cur.Pointer != _deathPanelVictimPtr)
                    {
                        _deathPanelHoldUntilUnscaledTime = now;
                    }
                }
            }

            if (now < _deathPanelHoldUntilUnscaledTime)
            {
                float remaining = _deathPanelHoldUntilUnscaledTime - now;
                float alpha = remaining < 0.30f ? Mathf.Clamp01(remaining / 0.30f) : 1.0f;
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

        // =====================================================================
        // PATCH 15 & 16: Vehicle & Aircraft Role Personalities (TurretAI & AIPlane)
        // =====================================================================
        [HarmonyPatch(typeof(TurretAI), nameof(TurretAI.RefreshTargets))]
        public static class TurretAI_RefreshTargets_Patch
        {
            public static void Postfix(TurretAI __instance)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value ||
                        !VisionConfig.EnableRolePersonalities.Value ||
                        !VisionConfig.EnableVehicleAndPilotPersonalities.Value ||
                        __instance == null ||
                        __instance.turret == null)
                    {
                        return;
                    }

                    Vehicle veh = GetCachedTurretVehicle(__instance.turret);
                    if (!IsLocallyOwnedVehicle(veh)) return;

                    SoldierPersonality crewPers = GetVehicleCrewPersonality(veh);
                    if (crewPers == SoldierPersonality.TankCrew_Brawler || crewPers == SoldierPersonality.LightCrew_Outrider)
                    {
                        // Brawler / Outrider crews acquire and switch targets 20% faster at close-mid range
                        float remaining = __instance.nextRefreshTime - Time.time;
                        if (remaining > 0.25f)
                        {
                            __instance.nextRefreshTime = Time.time + remaining * 0.80f;
                        }
                    }
                    else if (crewPers == SoldierPersonality.TankCrew_Support && __instance.HasRadioSupportRequest)
                    {
                        // Infantry Support crews respond more rapidly to friendly squad radio requests
                        float remaining = __instance.nextRefreshTime - Time.time;
                        if (remaining > 0.20f)
                        {
                            __instance.nextRefreshTime = Time.time + remaining * 0.75f;
                        }
                    }
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(AIPlane), nameof(AIPlane.Update))]
        public static class AIPlane_Update_Patch
        {
            public static void Prefix(AIPlane __instance)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value ||
                        !VisionConfig.EnableRolePersonalities.Value ||
                        !VisionConfig.EnableVehicleAndPilotPersonalities.Value ||
                        __instance == null ||
                        !IsLocallyOwnedVehicle(__instance.veh))
                    {
                        return;
                    }

                    SoldierPersonality pilotPers = GetVehicleCrewPersonality(__instance.veh);
                    if (pilotPers == SoldierPersonality.Pilot_Ace)
                    {
                        // Ace Dogfighter: faster target re-evaluation when hunting enemy aircraft
                        float rem = __instance.nextUpdateCheck - Time.time;
                        if (rem > 0.20f)
                            __instance.nextUpdateCheck = Time.time + rem * 0.78f;
                    }
                    else if (pilotPers == SoldierPersonality.Pilot_Daredevil)
                    {
                        // Daredevil CAS: commits slightly longer on dive bombing runs before pulling up
                        if (__instance.exitDiveEndTime > Time.time && __instance.exitDiveEndTime < Time.time + 1.2f)
                            __instance.exitDiveEndTime += 0.15f;
                    }
                    else if (pilotPers == SoldierPersonality.Pilot_Disciplined)
                    {
                        // Disciplined Bomber: pulls out of dives slightly earlier for safer high-altitude release
                        if (__instance.exitDiveEndTime > Time.time + 0.5f)
                            __instance.exitDiveEndTime -= 0.12f;
                    }
                }
                catch { }
            }
        }
    }
}
