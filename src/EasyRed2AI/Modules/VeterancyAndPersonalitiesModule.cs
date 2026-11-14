using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace EasyRed2HumanAI
{
    public static partial class VeteranAndTacticsPatches
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
            public float NextRoleCheckTime;
            public int PromotedTier;
            public Rank BaseRank;
            public string BaseName;
            public SoldierPersonality Personality;
            public byte RoleCategory;
            public bool NameInitialized;
        }

        private static readonly Dictionary<IntPtr, SoldierRecord> _records = new Dictionary<IntPtr, SoldierRecord>(512);
        private static readonly Dictionary<IntPtr, float> _nextPromotionCheckTime = new Dictionary<IntPtr, float>(256);
        internal static readonly Dictionary<IntPtr, (SoldierPersonality Pers, float ExpireTime)> _cachedVehiclePersonalities =
            new Dictionary<IntPtr, (SoldierPersonality Pers, float ExpireTime)>(64);

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
        public static int IndoorPronePreventedCount;
        public static int LocalCalloutsSharedCount;
        public static int SuppressiveEventsCount;
        public static int BoundingOverwatchHoldsCount;
        public static int MacroGarrisonsAssignedCount;
        public static int MacroObjectivePivotsCount;
        public static int StaticGunsStaffedCount;
        public static int SmartApcDismountsCount;
        public static int TankStandoffHoldsCount;
        public static int FriendlyRoadkillsPreventedCount;
        public static int BridgeCrossingsAssistedCount;
        public static int ShellShockSurrendersCount;

        private static byte DetermineRoleCategory(Soldier s)
        {
            if (s == null)
                return 0;

            if (s.IsOnVehicle())
            {
                AiCore.VehicleFastHandles vh = AiCore.GetOrCreateVehicleHandles(s.GetCurrentVehicle());
                if (vh != null)
                {
                    if (vh.IsPlane)
                        return 10; // Aircraft Pilot / Aircrew
                    if (vh.IsTank)
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
            int h = AiCore.GetDeterministicHash(s.Pointer, s._randomSeed_k__BackingField);
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
            float now = Time.time;
            if (!_records.TryGetValue(ptr, out SoldierRecord rec))
            {
                string rawName = s.name_surname ?? "";
                int bracketIdx = rawName.IndexOf(" [", StringComparison.Ordinal);
                if (bracketIdx >= 0)
                    rawName = rawName.Substring(0, bracketIdx);

                byte roleCat = DetermineRoleCategory(s);
                float jitter = (AiCore.GetDeterministicHash(ptr, 43) & 63) * 0.04f;
                rec = new SoldierRecord
                {
                    SpawnTime = now,
                    BonusSeconds = 0f,
                    NextRoleCheckTime = now + 4.0f + jitter,
                    PromotedTier = 0,
                    BaseRank = s.rank,
                    BaseName = rawName,
                    RoleCategory = roleCat,
                    Personality = RollPersonalityForRole(s, roleCat)
                };
                _records[ptr] = rec;
            }
            else if (now >= rec.NextRoleCheckTime)
            {
                rec.NextRoleCheckTime = now + 4.0f;
                byte currentRoleCat = DetermineRoleCategory(s);
                if (currentRoleCat != rec.RoleCategory)
                {
                    rec.RoleCategory = currentRoleCat;
                    rec.Personality = RollPersonalityForRole(s, currentRoleCat);
                    rec.NameInitialized = false;
                }
                _records[ptr] = rec;
            }
            return rec;
        }

        public static SoldierPersonality GetPersonality(Soldier s)
        {
            if (ReferenceEquals(s, null) || s.Pointer == IntPtr.Zero || !VisionConfig.EnableMod.Value || !VisionConfig.EnableRolePersonalities.Value)
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
            if (ReferenceEquals(v, null) || v.Pointer == IntPtr.Zero || !VisionConfig.EnableMod.Value || !VisionConfig.EnableRolePersonalities.Value || !VisionConfig.EnableVehicleAndPilotPersonalities.Value)
                return SoldierPersonality.None;

            IntPtr vPtr = v.Pointer;
            float now = Time.time;
            if (_cachedVehiclePersonalities.TryGetValue(vPtr, out var cached) && now < cached.ExpireTime)
                return cached.Pers;

            SoldierPersonality resolved = SoldierPersonality.None;
            var seats = v.seats;
            if (seats != null)
            {
                int len = seats.Length;
                for (int i = 0; i < len; i++)
                {
                    Seats seat = seats[i];
                    if (seat != null && seat.unitSet != null)
                    {
                        Soldier crew = seat.unitSet;
                        if (crew != null && crew.IsAlive)
                        {
                            resolved = GetPersonality(crew);
                            break;
                        }
                    }
                }
            }

            if (resolved == SoldierPersonality.None)
            {
                int roll100 = AiCore.GetDeterministicHash(vPtr, 73) % 100;
                AiCore.VehicleFastHandles vh = AiCore.GetOrCreateVehicleHandles(v);
                if (vh != null && vh.IsPlane)
                {
                    resolved = roll100 < 35 ? SoldierPersonality.Pilot_Ace : (roll100 < 70 ? SoldierPersonality.Pilot_Daredevil : SoldierPersonality.Pilot_Disciplined);
                }
                else if (vh != null && vh.IsTank)
                {
                    resolved = roll100 < 35 ? SoldierPersonality.TankCrew_Brawler : (roll100 < 70 ? SoldierPersonality.TankCrew_Sniper : SoldierPersonality.TankCrew_Support);
                }
                else
                {
                    resolved = roll100 < 35 ? SoldierPersonality.LightCrew_Outrider : (roll100 < 75 ? SoldierPersonality.LightCrew_Escort : SoldierPersonality.LightCrew_FlakWatch);
                }
            }

            _cachedVehiclePersonalities[vPtr] = (resolved, now + 3.0f);
            return resolved;
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

        public static string GetPersonalityShortLabel(SoldierPersonality p)
        {
            string tag = GetPersonalityTag(p);
            return string.IsNullOrEmpty(tag) ? "Infantry" : tag;
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
            if (!ReferenceEquals(s, null) && s != null && s.joinedSquad != null)
            {
                Soldier leader = s.joinedSquad.Leader;
                if (leader != null && leader.IsAlive && leader.Pointer != s.Pointer &&
                    GetPersonality(leader) == SoldierPersonality.Leader_Spearhead)
                {
                    AiCore.SoldierFastHandles sh = AiCore.GetOrCreateSoldierHandles(s);
                    AiCore.SoldierFastHandles lh = AiCore.GetOrCreateSoldierHandles(leader);
                    if (sh != null && lh != null && sh.Tr != null && lh.Tr != null &&
                        (lh.Tr.position - sh.Tr.position).sqrMagnitude <= 22f * 22f)
                    {
                        threshold += 12f;
                    }
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
            if (ReferenceEquals(s, null) || s.Pointer == IntPtr.Zero || !VisionConfig.EnableVeteranSystem.Value)
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

        public static int GetVeteranTierByPtr(IntPtr ptr)
        {
            if (ptr == IntPtr.Zero || !VisionConfig.EnableVeteranSystem.Value || !_records.TryGetValue(ptr, out SoldierRecord rec))
                return 0;

            float fullSeconds = Mathf.Max(60f, VisionConfig.VeteranFullMaturitySeconds.Value);
            float effectiveAlive = Mathf.Max(0f, Time.time - rec.SpawnTime) + rec.BonusSeconds;
            float v = Mathf.Clamp01(effectiveAlive / fullSeconds);
            if (v >= 0.80f) return 3;
            if (v >= 0.50f) return 2;
            if (v >= 0.20f) return 1;
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

            float jitter = (AiCore.GetDeterministicHash(ptr, 19) % 100) * 0.01f;
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
                    AiCore.IsLocallyOwnedBot(s))
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
        // Vehicle & Aircraft Role Personalities & Combined Arms Support (TurretAI & AIPlane)
        // =====================================================================
        [HarmonyPatch(typeof(TurretAI), nameof(TurretAI.RefreshTargets))]
        public static class TurretAI_RefreshTargets_Patch
        {
            public static void Prefix(out long __state)
            {
                __state = HitchProfiler.Begin();
            }

            public static void Postfix(TurretAI __instance, Soldier __0, bool __result, long __state)
            {
                try
                {
                    if (!__result ||
                        !VisionConfig.EnableMod.Value ||
                        __instance == null ||
                        __instance.turret == null)
                    {
                        return;
                    }

                    _turretAiByTurretPtr[__instance.turret.Pointer] = __instance;

                    AiCore.TurretFastTraits traits = AiCore.GetCachedTurretTraits(__instance.turret);
                    Vehicle veh = traits.VehHandles != null ? traits.VehHandles.Ref : AiCore.GetCachedTurretVehicle(__instance.turret);
                    bool ownedByGunner = __0 != null && AiCore.IsLocallyOwnedBot(__0);
                    if (!ownedByGunner && !AiCore.IsLocallyOwnedVehicle(veh))
                        return;

                    // Tank Commander Cupola / Periscope: stamp fresh spotted target onto VehHandles for intra-crew intercom handoff!
                    if (traits.IsCommanderTurret)
                    {
                        if (traits.VehHandles != null && AiCore.IsValidAliveSpottable(__instance.CurrentVisibleTarget))
                        {
                            traits.VehHandles.CommanderSpottedTarget = __instance.CurrentVisibleTarget;
                            traits.VehHandles.CommanderSpotTime = Time.time;
                        }
                        return;
                    }

                    // Always run danger-close indirect-fire target sanitization & combined-arms / intercom priority
                    if (VisionConfig.EnableCombinedArmsPrioritySupport.Value ||
                        VisionConfig.EnableDynamicHumanReactionTime.Value ||
                        VisionConfig.FriendlyFireConeDegrees.Value > 0.1f)
                    {
                        TryPrioritizeKnownTargetForTurret(__instance, veh);
                    }

                    if (!VisionConfig.EnableRolePersonalities.Value ||
                        !VisionConfig.EnableVehicleAndPilotPersonalities.Value)
                    {
                        return;
                    }

                    SoldierPersonality crewPers = GetVehicleCrewPersonality(veh);
                    if (crewPers == SoldierPersonality.TankCrew_Brawler || crewPers == SoldierPersonality.LightCrew_Outrider)
                    {
                        float remaining = __instance.nextRefreshTime - Time.time;
                        if (remaining > 0.25f)
                        {
                            __instance.nextRefreshTime = Time.time + remaining * 0.80f;
                        }
                    }
                    else if (crewPers == SoldierPersonality.TankCrew_Support && __instance.HasRadioSupportRequest)
                    {
                        float remaining = __instance.nextRefreshTime - Time.time;
                        if (remaining > 0.20f)
                        {
                            __instance.nextRefreshTime = Time.time + remaining * 0.75f;
                        }
                    }
                }
                catch { }
                finally
                {
                    HitchProfiler.End(HitchBucket.TurretRefresh, __state);
                }
            }
        }

        [HarmonyPatch(typeof(TurretAI), nameof(TurretAI.AutoPilot))]
        public static class TurretAI_AutoPilot_Patch
        {
            public static void Prefix(TurretAI __instance, Soldier __0)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value ||
                        __instance == null ||
                        __instance.turret == null)
                    {
                        return;
                    }

                    Turret turret = __instance.turret;
                    IntPtr turretPtr = turret.Pointer;
                    _turretAiByTurretPtr[turretPtr] = __instance;

                    float now = Time.time;
                    Spottable curTarget = __instance.CurrentVisibleTarget;

                    // Dynamic Human Turret / Emplacement Sight-Lay & Trail-Shift Reaction Time:
                    // Native TurretAI.AutoPilot rotates toward CurrentVisibleTarget while holding trigger pull until
                    // Time.time > nextFireCheckTime (0x180830E26). Pre-sighted lanes react quickly (0.22s-0.38s),
                    // whereas flanked static AT/HMG emplacements and off-bore tank turrets require human sight-lay settle!
                    if (VisionConfig.EnableDynamicHumanReactionTime.Value)
                    {
                        AiCore.TurretFastTraits traits = AiCore.GetCachedTurretTraits(turret);
                        Vehicle veh = traits.VehHandles != null ? traits.VehHandles.Ref : AiCore.GetCachedTurretVehicle(turret);
                        bool owned = (__0 != null && AiCore.IsLocallyOwnedBot(__0)) || AiCore.IsLocallyOwnedVehicle(veh);
                        if (owned && !traits.IsCommanderTurret && curTarget != null && curTarget.Pointer != IntPtr.Zero)
                        {
                            _turretSightReaction.TryGetValue(turretPtr, out TurretSightReactionState rState);
                            if (curTarget.Pointer != rState.LastTargetPtr || (now - rState.LastSeenTime) > 2.4f)
                            {
                                float offBoreDeg = Vector3.Angle(turret.GetFireDir(), curTarget.GetCenterOfUnit() - turret.GetFirePos());
                                float vet01 = __0 != null ? GetVeterancy01(__0) : 0f;
                                float jitter = (AiCore.GetDeterministicHash(turretPtr, curTarget.Pointer.GetHashCode()) & 31) * 0.0055f;

                                float delay;
                                if (offBoreDeg <= 12.0f)
                                {
                                    // Pre-sighted lane: sights already laid on approach sector
                                    delay = 0.22f + jitter;
                                }
                                else if (traits.VehHandles != null && traits.VehHandles.IsStaticEmplacement && !traits.IsAA && offBoreDeg > 28.0f)
                                {
                                    // Flanked static AT gun / tripod HMG: heavy manual trail / traverse shift
                                    delay = Mathf.Lerp(0.78f, 1.25f, Mathf.Clamp01((offBoreDeg - 28.0f) / 90.0f)) + jitter;
                                }
                                else if (traits.IsAA || (traits.VehHandles != null && traits.VehHandles.IsPlane))
                                {
                                    // AA mount or defensive air gunner tracking fast lead-ring target
                                    delay = 0.28f + Mathf.Clamp01(offBoreDeg / 160.0f) * 0.22f + jitter * 0.8f;
                                }
                                else
                                {
                                    // Tank / armored turret traverse + optical reticle settle
                                    delay = 0.34f + Mathf.Clamp01(offBoreDeg / 120.0f) * 0.34f + jitter;
                                }

                                delay *= Mathf.Lerp(1.0f, 0.78f, vet01);
                                __instance.nextFireCheckTime = Mathf.Max(__instance.nextFireCheckTime, now + delay);
                                __instance.nextFireStopTime = 0f;
                            }

                            rState.LastTargetPtr = curTarget.Pointer;
                            rState.LastSeenTime = now;
                            _turretSightReaction[turretPtr] = rState;

                            if (now < __instance.nextFireCheckTime)
                            {
                                turret.SetGunTriggerPressed(false, __0);
                            }
                        }
                    }

                    // Pre-empt friendly-fire BEFORE AutoPilot calls SetGunTriggerPressed(true) (which otherwise queues
                    // TurretGun.FireOnReloadDone even if Postfix sets triggerPressed = false!).
                    if (VisionConfig.FriendlyFireConeDegrees.Value > 0.1f &&
                        ShouldBlockTurretFriendlyFire(__instance, turret, __0))
                    {
                        turret.SetGunTriggerPressed(false, __0);
                        __instance.nextFireCheckTime = now + 0.30f;
                    }

                    if (!VisionConfig.EnableSmartVehicleAmmoSelection.Value)
                        return;

                    if (curTarget != null && curTarget.Pointer != IntPtr.Zero)
                    {
                        bool isVehOrAir = curTarget.IsWheeledVehicleOrTank() || curTarget.IsAirVehicle();
                        if (!_lastTurretTargetWasVehicle.TryGetValue(turretPtr, out bool prevWasVeh) || prevWasVeh != isVehOrAir)
                        {
                            _lastTurretTargetWasVehicle[turretPtr] = isVehOrAir;
                            __instance.nextSwitchAmmoCheck = 0f;
                            turret.SelectBestAmmo(curTarget);
                        }
                    }
                }
                catch { }
            }

            public static void Postfix(TurretAI __instance, Soldier __0)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value ||
                        VisionConfig.FriendlyFireConeDegrees.Value <= 0.1f ||
                        __instance == null)
                    {
                        return;
                    }

                    Turret turret = __instance.turret;
                    if (turret == null || turret.Pointer == IntPtr.Zero || !turret.IsFiring())
                        return;

                    if (ShouldBlockTurretFriendlyFire(__instance, turret, __0))
                    {
                        turret.SetGunTriggerPressed(false, __0);
                    }
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(Soldier), nameof(Soldier.Throw))]
        public static class Soldier_Throw_Patch
        {
            public static bool Prefix(Soldier __instance, VirtualItem __0, Spottable __1)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value ||
                        ReferenceEquals(__instance, null) ||
                        __instance.Pointer == IntPtr.Zero ||
                        !__instance.IsAI() ||
                        __instance.IsPlayer())
                    {
                        return true;
                    }

                    AiCore.SoldierFastHandles h = AiCore.GetOrCreateSoldierHandles(__instance);
                    if (h == null || h.Tr == null)
                        return true;

                    // Allow non-lethal smoke grenades to be thrown near friendlies and for screening armor
                    if (__0 == null || __0.TryCast<VirtualSmokeGrenade>() != null)
                    {
                        return true;
                    }

                    if (__1 == null || __1.Pointer == IntPtr.Zero)
                    {
                        __instance.throw_end_time = Time.time + 3.0f;
                        return false;
                    }

                    // Prevent AI soldiers from wasting standard frag grenades on buttoned-up tanks/armored vehicles
                    if (VisionConfig.PreventSmallArmsFireAtTanks.Value && __1.IsWheeledVehicleOrTank())
                    {
                        Vehicle targetVeh = __1.TryCast<Vehicle>();
                        if (targetVeh != null && !CanThrowableDamageOrFlushVehicle(__instance, __0, targetVeh))
                        {
                            __instance.throw_end_time = Time.time + 4.0f;
                            UselessTankFragsBlockedCount++;
                            if (VisionConfig.DebugLogging.Value)
                            {
                                AiCore.LogDebug(
                                    "TankFragBlocked",
                                    $"Blocked ineffective frag throw ({__0.item_id}) by {AiCore.GetCachedFaction(h, Time.time)} soldier at buttoned-up {targetVeh.name}.",
                                    3.0f);
                            }
                            return false;
                        }
                    }

                    if (VisionConfig.FriendlyFireConeDegrees.Value > 0.1f)
                    {
                        Vector3 targetPos = __1.GetCenterOfUnit();
                        float dist = Vector3.Distance(h.Tr.position, targetPos);
                        if (!IsSafeToThrowGrenadeAt(__instance, targetPos, dist, 11.5f))
                        {
                            __instance.throw_end_time = Time.time + 3.0f;
                            if (VisionConfig.DebugLogging.Value)
                            {
                                AiCore.LogDebug(
                                    "GrenadeFFBlocked",
                                    $"Blocked danger-close lethal grenade throw by {AiCore.GetCachedFaction(h, Time.time)} soldier ({dist:F0}m).");
                            }
                            return false;
                        }
                    }
                }
                catch { }
                return true;
            }
        }

        [HarmonyPatch(typeof(AIPlane), nameof(AIPlane.Update))]
        public static class AIPlane_Update_Patch
        {
            public static void Prefix(AIPlane __instance, out long __state)
            {
                __state = HitchProfiler.BeginTopLevel();
                try
                {
                    if (!VisionConfig.EnableMod.Value ||
                        __instance == null ||
                        !AiCore.IsLocallyOwnedVehicle(__instance.veh))
                    {
                        return;
                    }

                    // Easter Egg: Imperial Japanese Tokkōtai (Kamikaze) terminal dive when crippled or out of ordnance/ammo
                    TryExecuteJapaneseKamikazeDive(__instance, false);
                    if (IsJapaneseKamikazeActive(__instance))
                        return;

                    // Prevent head-on mid-air dogfight collisions (vanilla holds nose-on down to 70m)
                    TryExecuteDogfightAntiChickenBreakOff(__instance, false);

                    if (VisionConfig.EnableCombinedArmsPrioritySupport.Value)
                    {
                        TrySteerAircraftOrbitTowardKnownThreat(__instance);
                    }

                    if (!VisionConfig.EnableRolePersonalities.Value ||
                        !VisionConfig.EnableVehicleAndPilotPersonalities.Value)
                    {
                        return;
                    }

                    SoldierPersonality pilotPers = GetVehicleCrewPersonality(__instance.veh);
                    if (pilotPers == SoldierPersonality.Pilot_Ace)
                    {
                        float rem = __instance.nextUpdateCheck - Time.time;
                        if (rem > 0.20f)
                            __instance.nextUpdateCheck = Time.time + rem * 0.78f;
                    }
                    else if (pilotPers == SoldierPersonality.Pilot_Daredevil)
                    {
                        if (__instance.exitDiveEndTime > Time.time && __instance.exitDiveEndTime < Time.time + 1.2f)
                            __instance.exitDiveEndTime += 0.15f;
                    }
                    else if (pilotPers == SoldierPersonality.Pilot_Disciplined)
                    {
                        if (__instance.exitDiveEndTime > Time.time + 0.5f)
                            __instance.exitDiveEndTime -= 0.12f;
                    }
                }
                catch { }
            }

            public static void Postfix(AIPlane __instance, long __state)
            {
                try
                {
                    if (IsJapaneseKamikazeActive(__instance))
                    {
                        TryExecuteJapaneseKamikazeDive(__instance, true);
                    }
                    else
                    {
                        TryExecuteDogfightAntiChickenBreakOff(__instance, true);

                        // Pilot Attack-Run / Dogfight Sight Alignment Reaction:
                        // Prevent frame-zero laser bursts the instant targetToAttack switches; hold pilot gun trigger
                        // for 0.42s-0.78s while the pilot aligns the reflector gunsight onto the new target!
                        if (VisionConfig.EnableMod.Value &&
                            VisionConfig.EnableDynamicHumanReactionTime.Value &&
                            __instance != null &&
                            AiCore.IsLocallyOwnedVehicle(__instance.veh))
                        {
                            IntPtr planePtr = __instance.Pointer;
                            Spottable tgt = __instance.targetToAttack;
                            _planeAttackReaction.TryGetValue(planePtr, out PlaneAttackReactionState pState);

                            if (tgt != null && tgt.Pointer != IntPtr.Zero)
                            {
                                float now = Time.time;
                                if (tgt.Pointer != pState.LastTargetPtr)
                                {
                                    SoldierPersonality pilotPers = GetVehicleCrewPersonality(__instance.veh);
                                    float jitter = (AiCore.GetDeterministicHash(planePtr, tgt.Pointer.GetHashCode()) & 31) * 0.009f;
                                    float delay = 0.48f + jitter;
                                    if (pilotPers == SoldierPersonality.Pilot_Ace)
                                        delay *= 0.74f;
                                    else if (pilotPers == SoldierPersonality.Pilot_Disciplined)
                                        delay *= 0.90f;

                                    pState.LastTargetPtr = tgt.Pointer;
                                    pState.HoldFireUntil = now + delay;
                                    _planeAttackReaction[planePtr] = pState;
                                }

                                if (now < pState.HoldFireUntil)
                                {
                                    Turret pilotGun = __instance.GetPilotGun();
                                    if (pilotGun != null)
                                        pilotGun.SetGunTriggerPressed(false, null);
                                }
                            }
                            else if (pState.LastTargetPtr != IntPtr.Zero)
                            {
                                pState.LastTargetPtr = IntPtr.Zero;
                                pState.HoldFireUntil = 0f;
                                _planeAttackReaction[planePtr] = pState;
                            }
                        }
                    }
                }
                catch { }
                finally
                {
                    HitchProfiler.End(HitchBucket.Aircraft, __state);
                }
            }
        }

        [HarmonyPatch(typeof(AIPlane), nameof(AIPlane.ExitDive))]
        public static class AIPlane_ExitDive_Patch
        {
            public static bool Prefix(AIPlane __instance)
            {
                try
                {
                    if (IsJapaneseKamikazeActive(__instance) &&
                        IsSpottableTargetValid(__instance.targetToAttack))
                    {
                        return false;
                    }
                }
                catch { }
                return true;
            }
        }

        [HarmonyPatch(typeof(AIPlane), nameof(AIPlane.NeedRefill))]
        public static class AIPlane_NeedRefill_Patch
        {
            public static void Postfix(AIPlane __instance, ref bool __result)
            {
                try
                {
                    if (__result && IsJapaneseKamikazeActive(__instance))
                    {
                        __result = false;
                    }
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(AIPlane), nameof(AIPlane.RetreatCrew))]
        public static class AIPlane_RetreatCrew_Patch
        {
            public static bool Prefix(AIPlane __instance)
            {
                try
                {
                    // Prevent Japanese Kamikaze pilots from bailing out with a parachute mid-air when their plane catches fire
                    if (IsJapaneseKamikazeActive(__instance))
                    {
                        return false;
                    }
                }
                catch { }
                return true;
            }
        }
    }
}
