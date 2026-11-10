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
            public int PromotedTier;
            public Rank BaseRank;
            public string BaseName;
            public SoldierPersonality Personality;
            public byte RoleCategory;
            public bool NameInitialized;
        }

        private static readonly Dictionary<IntPtr, SoldierRecord> _records = new Dictionary<IntPtr, SoldierRecord>(512);
        private static readonly Dictionary<IntPtr, float> _nextPromotionCheckTime = new Dictionary<IntPtr, float>(256);

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

            int roll100 = AiCore.GetDeterministicHash(v.Pointer, 73) % 100;
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
            public static void Postfix(TurretAI __instance, bool __result)
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

                    Vehicle veh = AiCore.GetCachedTurretVehicle(__instance.turret);
                    if (!AiCore.IsLocallyOwnedVehicle(veh)) return;

                    if (VisionConfig.EnableCombinedArmsPrioritySupport.Value)
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
            }
        }

        [HarmonyPatch(typeof(TurretAI), nameof(TurretAI.AutoPilot))]
        public static class TurretAI_AutoPilot_Patch
        {
            public static void Prefix(TurretAI __instance)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value ||
                        !VisionConfig.EnableSmartVehicleAmmoSelection.Value ||
                        __instance == null ||
                        __instance.turret == null)
                    {
                        return;
                    }

                    Spottable curTarget = __instance.CurrentVisibleTarget;
                    if (curTarget != null && curTarget.Pointer != IntPtr.Zero)
                    {
                        bool isVehOrAir = curTarget.IsWheeledVehicleOrTank() || curTarget.IsAirVehicle();
                        IntPtr turretPtr = __instance.turret.Pointer;
                        if (!_lastTurretTargetWasVehicle.TryGetValue(turretPtr, out bool prevWasVeh) || prevWasVeh != isVehOrAir)
                        {
                            _lastTurretTargetWasVehicle[turretPtr] = isVehOrAir;
                            __instance.nextSwitchAmmoCheck = 0f;
                            __instance.turret.SelectBestAmmo(curTarget);
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
                        VisionConfig.FriendlyFireConeDegrees.Value <= 0.1f ||
                        __instance == null ||
                        __instance.Pointer == IntPtr.Zero ||
                        !__instance.IsAI() ||
                        __instance.IsPlayer() ||
                        __instance.transform == null)
                    {
                        return true;
                    }

                    // Allow non-lethal smoke grenades to be thrown near friendlies
                    if (__0 == null || __0.TryCast<VirtualSmokeGrenade>() != null)
                    {
                        return true;
                    }

                    if (__1 == null || __1.Pointer == IntPtr.Zero)
                    {
                        __instance.throw_end_time = Time.time + 3.0f;
                        return false;
                    }

                    Vector3 targetPos = __1.GetCenterOfUnit();
                    float dist = Vector3.Distance(__instance.transform.position, targetPos);
                    if (!IsSafeToThrowGrenadeAt(__instance, targetPos, dist, 11.5f))
                    {
                        __instance.throw_end_time = Time.time + 3.0f;
                        if (VisionConfig.DebugLogging.Value)
                        {
                            AiCore.LogDebug(
                                "GrenadeFFBlocked",
                                $"Blocked danger-close lethal grenade throw by {__instance.faction} soldier ({dist:F0}m).");
                        }
                        return false;
                    }
                }
                catch { }
                return true;
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
                        __instance == null ||
                        !AiCore.IsLocallyOwnedVehicle(__instance.veh))
                    {
                        return;
                    }

                    // Easter Egg: Imperial Japanese Tokkōtai (Kamikaze) terminal dive when crippled or out of ordnance/ammo
                    TryExecuteJapaneseKamikazeDive(__instance, false);
                    if (IsJapaneseKamikazeActive(__instance))
                        return;

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

            public static void Postfix(AIPlane __instance)
            {
                try
                {
                    if (IsJapaneseKamikazeActive(__instance))
                    {
                        TryExecuteJapaneseKamikazeDive(__instance, true);
                    }
                }
                catch { }
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
