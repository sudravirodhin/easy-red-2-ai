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

        internal struct SoldierRecord
        {
            public bool Initialized;
            public float SpawnTime;
            public float BonusSeconds;
            public float NextRoleCheckTime;
            public int PromotedTier;
            public Rank BaseRank;
            public string BaseName;
            public SoldierPersonality Personality;
            public byte RoleCategory;
            public bool NameInitialized;
            public bool LeaderPromotionEvaluated;
            public bool RoseToTheOccasion;
        }

        internal static readonly Dictionary<IntPtr, (SoldierPersonality Pers, float ExpireTime)> _cachedVehiclePersonalities =
            new Dictionary<IntPtr, (SoldierPersonality Pers, float ExpireTime)>(64);

        // Telemetry counters for debug / toast feedback
        public static int ActiveVeteransCount;
        public static int FieldPromotionsCount;
        public static int RisenLeadersCount;
        public static int SelfPreservationDrops;
        public static int BipodsDeployed;
        public static int WallsBlocked;
        public static int AutoPeeksTriggered;
        public static int FriendlyFirePrevented;
        public static int AntiStackNudges;
        public static int WallsVaulted;
        public static int ComradesLiberated;
        public static int ShelteredRepairsCount;
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
        public static int FriendlyRoadkillsPreventedCount;
        public static int BridgeCrossingsAssistedCount;
        public static int GrenadesKickedCount;
        public static int GrenadesEvadedCount;
        public static int ShellShockSurrendersCount;

        private static byte DetermineVehicleRoleCategory(Vehicle veh)
        {
            AiCore.VehicleFastHandles vh = AiCore.GetOrCreateVehicleHandles(veh);
            if (vh != null)
            {
                if (vh.IsPlane)
                    return 10; // Aircraft Pilot / Aircrew
                if (vh.IsTank)
                    return 8;  // Armored Tank / Tracked Vehicle Crew
            }
            return 9;          // Light / Wheeled / Halftrack / AA Crew
        }

        private static byte DetermineRoleCategory(Soldier s)
        {
            if (s == null)
                return 0;

            if (s.IsOnVehicle())
                return DetermineVehicleRoleCategory(s.GetCurrentVehicle());

            Squad sq = s.joinedSquad;
            if (sq != null && !sq.isOpenForJoiners)
            {
                Vehicle opVeh = s.VehicleToOperate();
                if (opVeh != null && opVeh.Pointer != IntPtr.Zero && !opVeh.IsTransportVehicle())
                    return DetermineVehicleRoleCategory(opVeh);

                if (AiCore.IsConfirmedPlaneCrewSquad(sq))
                    return 10; // Bailed Aircraft Pilot / Aircrew

                if (AiCore.IsTrueVehicleCrewSquad(sq))
                    return 8;  // Bailed Armored Tank / Vehicle Crew
            }

            if (sq != null && sq.Leader != null && sq.Leader.Pointer == s.Pointer)
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
            AiCore.SoldierFastHandles h = AiCore.GetOrCreateSoldierHandles(s);
            if (h == null)
                return default;

            float now = Time.time;
            ref SoldierRecord rec = ref h.Veterancy.Record;
            if (!rec.Initialized)
            {
                string rawName = s.name_surname ?? "";
                int bracketIdx = rawName.IndexOf(" [", StringComparison.Ordinal);
                if (bracketIdx >= 0)
                    rawName = rawName.Substring(0, bracketIdx);

                byte roleCat = DetermineRoleCategory(s);
                SoldierPersonality rolledPers = RollPersonalityForRole(s, roleCat);
                bool spawnedAsLeader = roleCat == 1 ||
                                       s.IsSquadLeader() ||
                                       (s.joinedSquad != null && s.joinedSquad.Leader != null && s.joinedSquad.Leader.Pointer == s.Pointer);
                float jitter = (AiCore.GetDeterministicHash(h.Ptr, 43) & 63) * 0.04f;
                rec = new SoldierRecord
                {
                    Initialized = true,
                    SpawnTime = now,
                    BonusSeconds = 0f,
                    NextRoleCheckTime = now + 4.0f + jitter,
                    PromotedTier = 0,
                    BaseRank = s.rank,
                    BaseName = rawName,
                    RoleCategory = roleCat,
                    Personality = rolledPers,
                    LeaderPromotionEvaluated = spawnedAsLeader || IsLeaderPersonality(rolledPers),
                    RoseToTheOccasion = false
                };
            }
            return rec;
        }

        internal static bool IsLeaderPersonality(SoldierPersonality p)
        {
            return p == SoldierPersonality.Leader_Spearhead ||
                   p == SoldierPersonality.Leader_Tactician ||
                   p == SoldierPersonality.Leader_OldGuard;
        }

        internal static bool HasSoldierRisenToOccasion(Soldier s)
        {
            return !ReferenceEquals(s, null) &&
                   s != null &&
                   s.Pointer != IntPtr.Zero &&
                   AiCore.TryGetSoldierHandles(s.Pointer, out AiCore.SoldierFastHandles h) &&
                   h.Veterancy.Record.Initialized &&
                   h.Veterancy.Record.RoseToTheOccasion;
        }

        private static SoldierPersonality ResolveRiseToOccasionPersonality(Soldier s, SoldierPersonality originalPers)
        {
            switch (originalPers)
            {
                case SoldierPersonality.Assaulter_Shock:
                case SoldierPersonality.Assaulter_Breacher:
                case SoldierPersonality.Assaulter_Skirmisher:
                case SoldierPersonality.Gunner_AssaultMG:
                case SoldierPersonality.AT_TankHunter:
                case SoldierPersonality.AT_Demolitionist:
                case SoldierPersonality.Medic_Heroic:
                case SoldierPersonality.TankCrew_Brawler:
                case SoldierPersonality.LightCrew_Outrider:
                case SoldierPersonality.Pilot_Daredevil:
                    return SoldierPersonality.Leader_Spearhead;

                case SoldierPersonality.Rifleman_Regular:
                case SoldierPersonality.Rifleman_Grenadier:
                case SoldierPersonality.Gunner_Overwatch:
                case SoldierPersonality.Marksman_Spotter:
                case SoldierPersonality.Medic_Combat:
                case SoldierPersonality.TankCrew_Support:
                case SoldierPersonality.LightCrew_Escort:
                case SoldierPersonality.Pilot_Ace:
                    return SoldierPersonality.Leader_Tactician;

                default:
                {
                    int surgeRoll = s != null ? (AiCore.GetDeterministicHash(s.Pointer, 947) % 100) : 50;
                    return surgeRoll < 20 ? SoldierPersonality.Leader_Spearhead : SoldierPersonality.Leader_OldGuard;
                }
            }
        }

        internal static float ComputeLeaderBraveryAura(Soldier leader)
        {
            if (ReferenceEquals(leader, null) ||
                leader == null ||
                leader.Pointer == IntPtr.Zero ||
                !leader.IsAlive ||
                leader.IsInjured ||
                leader.HasSurrended() ||
                !VisionConfig.EnableMod.Value ||
                !VisionConfig.EnableRolePersonalities.Value ||
                !VisionConfig.EnableInfantryPersonalities.Value)
            {
                return 0f;
            }

            float vet01 = GetVeterancy01(leader);
            SoldierPersonality pers = GetPersonality(leader);
            float baseAura;
            switch (pers)
            {
                case SoldierPersonality.Leader_Spearhead:
                    baseAura = 0.09f + 0.07f * vet01;
                    break;
                case SoldierPersonality.Leader_Tactician:
                    baseAura = 0.05f + 0.05f * vet01;
                    break;
                case SoldierPersonality.Leader_OldGuard:
                    baseAura = 0.04f + 0.05f * vet01;
                    break;
                case SoldierPersonality.Assaulter_Shock:
                case SoldierPersonality.Assaulter_Breacher:
                case SoldierPersonality.Assaulter_Skirmisher:
                case SoldierPersonality.Gunner_AssaultMG:
                case SoldierPersonality.AT_TankHunter:
                case SoldierPersonality.AT_Demolitionist:
                case SoldierPersonality.Medic_Heroic:
                case SoldierPersonality.TankCrew_Brawler:
                case SoldierPersonality.LightCrew_Outrider:
                case SoldierPersonality.Pilot_Daredevil:
                    baseAura = 0.04f + 0.04f * vet01;
                    break;
                case SoldierPersonality.Rifleman_Regular:
                case SoldierPersonality.Rifleman_Grenadier:
                case SoldierPersonality.Gunner_Overwatch:
                case SoldierPersonality.Marksman_Spotter:
                case SoldierPersonality.Medic_Combat:
                case SoldierPersonality.TankCrew_Support:
                case SoldierPersonality.LightCrew_Escort:
                case SoldierPersonality.Pilot_Ace:
                    baseAura = 0.01f + 0.03f * vet01;
                    break;
                case SoldierPersonality.None:
                    return 0f;
                default:
                    return -0.03f + 0.04f * vet01;
            }

            if (baseAura > 0f)
            {
                float auraMult = GetFactionCommandProfile(AiCore.GetCachedFaction(leader)).LeaderAuraMult;
                if (auraMult > 0.01f)
                    baseAura = Mathf.Min(0.22f, baseAura * auraMult);
            }
            return baseAura;
        }

        internal static bool TryEvaluateRiseToTheOccasion(Soldier s, Squad sq, ref SquadBlackboard board, float now, bool hasBoard)
        {
            if (ReferenceEquals(s, null) ||
                s == null ||
                s.Pointer == IntPtr.Zero ||
                !s.IsAlive ||
                s.IsInjured ||
                s.HasSurrended() ||
                ReferenceEquals(sq, null) ||
                sq == null ||
                sq.Pointer == IntPtr.Zero ||
                sq.CountMembers < 2)
            {
                return false;
            }

            Soldier sqLdr = sq.Leader;
            if (sqLdr == null || sqLdr.Pointer != s.Pointer)
                return false;

            GetOrCreateRecord(s);
            AiCore.SoldierFastHandles h = AiCore.GetOrCreateSoldierHandles(s);
            if (h == null)
                return false;

            ref SoldierRecord rec = ref h.Veterancy.Record;
            if (!rec.Initialized || rec.LeaderPromotionEvaluated)
                return false;

            rec.LeaderPromotionEvaluated = true;

            if (IsLeaderPersonality(rec.Personality) ||
                !VisionConfig.EnableMod.Value ||
                !VisionConfig.EnableRolePersonalities.Value ||
                !VisionConfig.EnableInfantryPersonalities.Value)
            {
                return false;
            }

            int tier = VisionConfig.EnableVeteranSystem.Value ? GetVeteranTier(s) : 0;
            int chancePct = tier >= 3 ? 60 : (tier == 2 ? 45 : (tier == 1 ? 30 : 20));
            int roll100 = AiCore.GetDeterministicHash(s.Pointer, s._randomSeed_k__BackingField ^ 0x5A3C) % 100;
            if (roll100 >= chancePct)
                return false;

            SoldierPersonality promoted = ResolveRiseToOccasionPersonality(s, rec.Personality);
            rec.Personality = promoted;
            rec.RoseToTheOccasion = true;
            rec.NameInitialized = false;
            h.Emergent.WeightsInitialized = false;
            RisenLeadersCount++;

            RefreshSoldierBracketNameAndRoster(s, ref rec);
            RefreshSquadSoldierLabelIfShown(s);

            if (hasBoard && board.LeaderShockUntil > now)
            {
                board.LeaderShockUntil = now + (board.LeaderShockUntil - now) * 0.50f;
                board.LeaderShockPeak *= 0.60f;
            }

            string archLabel = GetPersonalityTag(promoted);
            Soldier localPlayer = Soldier.CurrentControlledSoldierOrNull();
            if (localPlayer != null && localPlayer.Pointer == s.Pointer)
            {
                string msg = $"Rise to the Occasion! Command forged you into a {archLabel} leader.";
                try { PlayerController.HintToPlayer(msg, false); } catch { }
                VisionPatches.ShowToast($"[Battle Hardened] {msg}", 5.0f);
            }
            else if (sq.IsPlayerInSquad())
            {
                string cleanName = string.IsNullOrEmpty(rec.BaseName) ? "Your new Squad Leader" : rec.BaseName;
                AiCore.TryShowLocalPlayerRadioHint(
                    sq,
                    $"Chain of Command: {cleanName} rose to the occasion ({archLabel})!",
                    requireLeader: false,
                    minInterval: 4.0f);
            }

            if (!s.IsPlayer())
            {
                TrySayTacticalVoice(s, sq, VoiceManager.VoiceClip.followMe, 0.0f);
            }

            return true;
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
            if (!VisionConfig.EnableVeteranSystem.Value ||
                !AiCore.TryGetSoldierHandles(ptr, out AiCore.SoldierFastHandles h) ||
                !h.Veterancy.Record.Initialized)
                return 0;

            SoldierRecord rec = h.Veterancy.Record;
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
            if (!VisionConfig.EnableVeteranSystem.Value)
                return;

            AiCore.SoldierFastHandles h = AiCore.GetOrCreateSoldierHandles(s);
            if (h != null && h.Veterancy.Record.Initialized)
            {
                // Cap combat bonus at 25% of full maturity so time survived remains the primary driver
                float maxBonus = Mathf.Max(60f, VisionConfig.VeteranFullMaturitySeconds.Value) * 0.25f;
                h.Veterancy.Record.BonusSeconds = Mathf.Min(maxBonus, h.Veterancy.Record.BonusSeconds + bonusSeconds);
            }
        }

        private static void RefreshSoldierBracketNameAndRoster(Soldier s, ref SoldierRecord rec)
        {
            if (ReferenceEquals(s, null) || s == null || s.Pointer == IntPtr.Zero)
                return;

            rec.NameInitialized = true;

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

            Soldier controlled = Soldier.CurrentControlledSoldierOrNull();
            if (controlled != null &&
                controlled.joinedSquad != null &&
                s.joinedSquad != null &&
                controlled.joinedSquad.Pointer == s.joinedSquad.Pointer)
            {
                RefreshBottomLeftSquadRosterVisuals();
            }
        }

        private static void CheckAndApplyFieldPromotion(Soldier s)
        {
            AiCore.SoldierFastHandles h = AiCore.GetOrCreateSoldierHandles(s);
            if (h == null)
                return;

            IntPtr ptr = h.Ptr;
            float now = Time.time;
            if (now < h.Veterancy.NextPromotionCheckTime)
                return;

            float jitter = (AiCore.GetDeterministicHash(ptr, 19) % 100) * 0.01f;
            h.Veterancy.NextPromotionCheckTime = now + 4.0f + jitter;

            GetOrCreateRecord(s);
            ref SoldierRecord rec = ref h.Veterancy.Record;
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
                RefreshSoldierBracketNameAndRoster(s, ref rec);
            }
        }

        // =====================================================================
        // Vehicle & Aircraft Role Personalities & Combined Arms Support (TurretAI & AIPlane)
        // =====================================================================
        [HarmonyPatch(typeof(TurretAI), nameof(TurretAI.RefreshTargets))]
        public static class TurretAI_RefreshTargets_Patch
        {
            public struct TurretRefreshState
            {
                public long ProfStart;
                public Spottable PrevTarget;
                public IntPtr PrevTargetPtr;
            }

            public static void Prefix(TurretAI __instance, out TurretRefreshState __state)
            {
                __state = new TurretRefreshState
                {
                    ProfStart = HitchProfiler.Begin()
                };
                try
                {
                    if (__instance != null &&
                        !ReferenceEquals(__instance.CurrentVisibleTarget, null) &&
                        __instance.CurrentVisibleTarget != null)
                    {
                        __state.PrevTarget = __instance.CurrentVisibleTarget;
                        __state.PrevTargetPtr = __instance.CurrentVisibleTarget.Pointer;
                    }
                }
                catch { }
            }

            public static void Postfix(TurretAI __instance, Soldier __0, bool __result, TurretRefreshState __state)
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

                    // Turret Infantry Target Commitment Hysteresis:
                    // Prevent vanilla TurretAI.FindBestTarget (0x180831A80) from oscillating between equally distant
                    // infantrymen in dense firefights (never blocks upgrading onto vehicles/aircraft or TryPrioritizeKnownTargetForTurret).
                    Spottable newTurretTarget = __instance.CurrentVisibleTarget;
                    if (__state.PrevTargetPtr != IntPtr.Zero &&
                        !ReferenceEquals(newTurretTarget, null) &&
                        newTurretTarget != null &&
                        newTurretTarget.Pointer != __state.PrevTargetPtr &&
                        AiCore.IsValidAliveSpottable(__state.PrevTarget))
                    {
                        Spottable prevT = __state.PrevTarget;
                        bool prevIsInfantry = !prevT.IsWheeledVehicleOrTank() && !prevT.IsAirVehicle();
                        bool newIsInfantry = !newTurretTarget.IsWheeledVehicleOrTank() && !newTurretTarget.IsAirVehicle();
                        if (prevIsInfantry && newIsInfantry)
                        {
                            float nowT = Time.time;
                            Turret turretObj = __instance.turret;
                            Vector3 tFirePos = turretObj.GetFirePos();
                            Vector3 prevPos = prevT.GetCenterOfUnit();
                            Vector3 toPrev = prevPos - tFirePos;
                            float prevDist = toPrev.magnitude;
                            float newDist = Vector3.Distance(tFirePos, newTurretTarget.GetCenterOfUnit());
                            float prevOffBore = prevDist > 0.5f ? Vector3.Angle(turretObj.GetFireDir(), toPrev) : 0f;

                            _turretSightReaction.TryGetValue(turretObj.Pointer, out TurretSightReactionState tReact);
                            bool turretCommitted = nowT < tReact.LockedUntil || prevOffBore <= 15.0f;
                            bool newIsCloseEmergency = newDist <= 22.0f && newDist < prevDist - 6.0f;
                            float tMargin = turretCommitted ? Mathf.Max(18.0f, prevDist * 0.30f) : Mathf.Max(8.0f, prevDist * 0.15f);

                            if (!newIsCloseEmergency &&
                                prevDist >= 4.0f &&
                                newDist >= prevDist - tMargin &&
                                turretObj.CanShootInDirection(toPrev / prevDist, 1.0f))
                            {
                                float prevAngleAdj = Corvostudio.Weapons.BulletInstance.CalculateTrajectory(
                                    prevDist, turretObj.GetBulletSpeed(), turretObj.GetBulletGravity(), out bool prevReach);
                                if (prevReach && __instance.CanSee(prevT))
                                {
                                    __instance.CurrentVisibleTarget = prevT;
                                    __instance.targetAngleAdjust = prevAngleAdj;
                                    __instance.targetInWeaponRange = true;
                                }
                            }
                        }
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
                    HitchProfiler.End(HitchBucket.TurretRefresh, __state.ProfStart);
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
                    if (!ReferenceEquals(curTarget, null) && curTarget != null && !AiCore.IsValidAliveSpottable(curTarget))
                    {
                        __instance.CurrentVisibleTarget = null;
                        __instance.targetInWeaponRange = false;
                        curTarget = null;
                        turret.SetGunTriggerPressed(false, __0);
                    }

                    AiCore.TurretFastTraits traits = AiCore.GetCachedTurretTraits(turret);
                    Vehicle veh = traits.VehHandles != null ? traits.VehHandles.Ref : AiCore.GetCachedTurretVehicle(turret);
                    bool owned = (__0 != null && AiCore.IsLocallyOwnedBot(__0)) || AiCore.IsLocallyOwnedVehicle(veh);

                    if (owned &&
                        !traits.IsCommanderTurret &&
                        traits.VehHandles != null &&
                        traits.VehHandles.IsStaticEmplacement &&
                        veh != null &&
                        now >= traits.VehHandles.NextStaticSelfPreservationCheckTime)
                    {
                        traits.VehHandles.NextStaticSelfPreservationCheckTime = now + 0.45f;
                        AiCore.EnsureWorldSnapshot();
                        Vector3 vehPos = traits.VehHandles.Tr != null ? traits.VehHandles.Tr.position : veh.transform.position;
                        bool hasAmmo = HasAnyEmplacementTurretAmmo(veh, turret);
                        TryReleaseStaleStaticEmplacementCrew(veh, turret, vehPos, hasAmmo, now, AiCore.WorldSquads, AiCore.WorldSquadCount);
                        if (__0 != null && !__0.IsOnVehicle())
                            return;
                    }

                    // Active 360-Degree Tank Commander Periscope / Cupola Horizon Scan & Under-Fire Slewing:
                    // Native TurretAI.AutoPilot (0x180830802) returns immediately when CurrentVisibleTarget == null without
                    // ever calling TurretCommander.RotateToward or slewing main/coax/AA turrets toward incoming fire!
                    // Priority #0: slew toward active incoming attacker bearing when idle; otherwise commander scans gunfire / 360.
                    if (owned && traits.IsCommanderTurret && (curTarget == null || curTarget.Pointer == IntPtr.Zero))
                    {
                        string cmdFac = (__0 != null && !string.IsNullOrEmpty(__0.faction))
                            ? __0.faction
                            : GetVehicleCrewFaction(veh, turret);
                        Vector3 scanDir;
                        if (traits.VehHandles != null &&
                            now < traits.VehHandles.UnderFireUntil)
                        {
                            Vector3 ufTargetPos = AiCore.TryGetSpottableCenter(traits.VehHandles.UnderFireAttacker, out Vector3 liveUfPos)
                                ? liveUfPos
                                : traits.VehHandles.UnderFireFromPos;
                            Vector3 toUf = ufTargetPos - turret.GetFirePos();
                            scanDir = toUf.sqrMagnitude > 0.25f ? toUf.normalized : Vector3.forward;
                        }
                        else if (!string.IsNullOrEmpty(cmdFac) &&
                                 VisionPatches.TryGetDominantEnemyGunfireDirection(turret.GetFirePos(), cmdFac, 185f, 3.5f, out Vector3 gunDir, out _, out _))
                        {
                            scanDir = gunDir;
                        }
                        else
                        {
                            float phaseRad = (now * 55.0f + ((turretPtr.ToInt64() & 15L) * 24.0f)) * Mathf.Deg2Rad;
                            scanDir = new Vector3(Mathf.Sin(phaseRad), 0f, Mathf.Cos(phaseRad));
                        }
                        turret.RotateToward(scanDir, 0f);
                    }
                    else if (owned &&
                             !traits.IsCommanderTurret &&
                             (__0 == null || !__0.IsPlayer()) &&
                             (curTarget == null || curTarget.Pointer == IntPtr.Zero) &&
                             !__instance.HasRadioSupportRequest &&
                             traits.VehHandles != null &&
                             now < traits.VehHandles.UnderFireUntil)
                    {
                        Vector3 firePos = turret.GetFirePos();
                        Vector3 threatPos = AiCore.TryGetSpottableCenter(traits.VehHandles.UnderFireAttacker, out Vector3 livePos)
                            ? livePos
                            : traits.VehHandles.UnderFireFromPos;
                        Vector3 toThreat = threatPos - firePos;
                        float d = toThreat.magnitude;
                        if (d >= 4.0f)
                        {
                            Vector3 threatDir = toThreat / d;
                            if (turret.CanShootInDirection(threatDir, 1.0f))
                            {
                                turret.RotateToward(threatDir, 0f);
                            }
                        }
                    }

                    // Dynamic Human Turret / Emplacement Sight-Lay & Trail-Shift Reaction Time:
                    // Native TurretAI.AutoPilot rotates toward CurrentVisibleTarget while holding trigger pull until
                    // Time.time > nextFireCheckTime (0x180830E26). Pre-sighted lanes react quickly (0.22s-0.38s),
                    // whereas flanked static AT/HMG emplacements and off-bore tank turrets require human sight-lay settle!
                    if (VisionConfig.EnableDynamicHumanReactionTime.Value)
                    {
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

                                if (rState.LastSeenTime > 0f && (now - rState.LastSeenTime) <= 2.0f && offBoreDeg <= 16.0f)
                                {
                                    delay = Mathf.Min(delay, 0.18f);
                                }

                                delay *= Mathf.Lerp(1.0f, 0.78f, vet01);
                                __instance.nextFireCheckTime = Mathf.Max(__instance.nextFireCheckTime, now + delay);
                                __instance.nextFireStopTime = 0f;
                                rState.LockedUntil = now + 3.2f;
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
            public static bool Prefix(Soldier __instance, VirtualItem __0, ref Spottable __1, out float __state)
            {
                __state = (!ReferenceEquals(__instance, null) && __instance.Pointer != IntPtr.Zero)
                    ? __instance.stopHandsAnim_end
                    : -1f;
                try
                {
                    TryCancelLocalPlayerReloadOnThrow(__instance, __0);

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
                    if (__0 == null)
                        return true;

                    if (__0.TryCast<VirtualSmokeGrenade>() != null)
                        return TryHandleBotSmokeThrowAim(__instance, __0, ref __1);

                    // AT/AP pressure mines are planted onto the ground (target == null), never thrown as hand grenades at targets
                    if (AiCore.IsPressureMineItem(__0, out bool isAtMine))
                    {
                        if (__1 == null || __1.Pointer == IntPtr.Zero)
                        {
                            if (__instance.transform != null)
                            {
                                AiCore.RegisterPlacedMine(
                                    __instance.transform.position + __instance.transform.forward * 1.15f,
                                    null,
                                    isAtMine);
                            }
                            return true;
                        }

                        __instance.throw_end_time = Time.time + 3.0f;
                        return false;
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

            public static void Postfix(Soldier __instance, float __state)
            {
                try
                {
                    if (!VisionConfig.EnableMod.Value ||
                        ReferenceEquals(__instance, null) ||
                        __instance.Pointer == IntPtr.Zero ||
                        __state < 0f)
                    {
                        return;
                    }

                    // Only act if vanilla Soldier.Throw actually succeeded (consumed item and set stopHandsAnim_end = now + 3.0f).
                    // Blocked throws (Prefix returning false, CanPlaySpecialAnim false, vehicle seat, or failed item removal) leave stopHandsAnim_end unchanged.
                    float now = Time.time;
                    if (__instance.stopHandsAnim_end <= __state + 0.5f || __instance.stopHandsAnim_end < now + 1.5f)
                        return;

                    if (!AiCore.IsLocallyOwnedSoldier(__instance))
                        return;

                    // Immediately cut any active full-auto burst on Frame 0 and push SyncStopFire to Co-op clients.
                    __instance.SetGunTriggerPressed(false);
                    if (__instance.triggerState == Soldier.TriggerState.Firing)
                        __instance.StopFire();

                    // Hold AI bot fire through the full throw animation. In Co-op MP, vanilla delays SynchItemThrow
                    // by 1.5s (sent at release), so remote clients play ThrowItem from t=1.5s to t=4.5s; holding bot
                    // gunfire until 4.55s keeps vanilla clients from seeing full-auto gunfire mid-throw without delaying human players.
                    AiCore.SoldierFastHandles h = AiCore.GetOrCreateSoldierHandles(__instance);
                    if (AiCore.IsLocallyOwnedBot(h))
                    {
                        float holdUntil = now + (AiCore.IsInMultiplayerRoom() ? 4.55f : 3.15f);
                        if (holdUntil > h.Combat.ThrowFireHoldUntil)
                            h.Combat.ThrowFireHoldUntil = holdUntil;
                        h.Combat.RotateFireFrame.Frame = -1;
                        if (h.Ai != null)
                        {
                            h.Ai.shotFiresEnd = 0f;
                            if (h.Ai.nextAvailableShotFires < holdUntil)
                                h.Ai.nextAvailableShotFires = holdUntil;
                        }
                    }
                }
                catch { }
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

                    // Easter Egg: Imperial Japanese Tokkōtai (Kamikaze) terminal dive when crippled
                    TryExecuteJapaneseKamikazeDive(__instance, false);
                    if (IsJapaneseKamikazeActive(__instance))
                        return;

                    if (VisionConfig.EnableCombinedArmsPrioritySupport.Value)
                    {
                        TrySteerAircraftOrbitTowardKnownThreat(__instance);
                    }

                    UpdateAircraftDiveTerrainSafetyAndPersonality(__instance);
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
                    if (__instance == null || !VisionConfig.EnableMod.Value)
                        return;

                    if (IsJapaneseKamikazeActive(__instance))
                    {
                        __result = false;
                        return;
                    }

                    VehiclePlane vp = __instance.veh;
                    if (vp == null || vp.Pointer == IntPtr.Zero || vp.planeType != PlaneType.Fighter || vp.PlayerIsInside())
                        return;

                    AiCore.VehicleFastHandles vh = AiCore.GetOrCreateVehicleHandles(vp);
                    float now = Time.time;

                    // Check whether a friendly AircraftRepairStation actually exists via vanilla's exact lookup.
                    // In vanilla AIPlane.Update (0x18057644C), if NeedRefill() is true when GetNearestRepairStation()
                    // is null, SetGoToRefill() forces SetFlyAroundObjectiveArea() (planeState = 7) and skips
                    // GetBestAircraftVisibleEnemy() forever — locking the plane into a permanent 200m vulture circle!
                    bool hasFriendlyStation = __instance.GetNearestRepairStation() != null;
                    if (!hasFriendlyStation)
                    {
                        __result = false;
                        if (vh != null && AiCore.IsLocallyOwnedVehicle(vp))
                        {
                            bool hasAnyOrdnance = __instance.PilotGunHasAnyAmmo() || __instance.HasBombsToDeliver();
                            if (hasAnyOrdnance)
                            {
                                vh.OffMapRearmReadyTime = 0f;
                            }
                            else if (vh.OffMapRearmReadyTime <= 0f)
                            {
                                vh.OffMapRearmReadyTime = now + 30.0f;
                            }
                            else if (now >= vh.OffMapRearmReadyTime)
                            {
                                vh.OffMapRearmReadyTime = 0f;
                                vh.BombsDepletedAt = 0f;
                                if (vp.CanRefillAmmmo())
                                    vp.RefillAllTurrets();
                                if (vp.CanRefillBombBay())
                                    vp.RefillBombBaySynched();
                            }
                        }
                        return;
                    }

                    if (__result)
                        return;

                    // Dive-bomber / fighter-bomber ("Winchester" — all bombs/rockets expended):
                    // Vanilla AIPlane.NeedRefill (0x180572D95) skips CountBombs() whenever GetPilotTurret() != null.
                    // However, if the aircraft still has forward pilot gun ammo, let it strafe ground targets for 40s
                    // after bomb release before heading back to the repair ring to rearm bombs!
                    var bays = vp.bombBay;
                    if (bays != null && bays.Length > 0)
                    {
                        if (vp.CountBombs() > 0)
                        {
                            if (vh != null)
                                vh.BombsDepletedAt = 0f;
                        }
                        else if (__instance.targetToAttack == null || !__instance.targetToAttack.IsAirVehicle())
                        {
                            if (!__instance.PilotGunHasAnyAmmo())
                            {
                                __result = true;
                            }
                            else if (vh != null)
                            {
                                if (vh.BombsDepletedAt <= 0f)
                                    vh.BombsDepletedAt = now;
                                else if (now - vh.BombsDepletedAt >= 40.0f)
                                    __result = true;
                            }
                        }
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

                    if (VisionConfig.EnableMod.Value)
                    {
                        return HandlePlaneRetreatCrew(__instance);
                    }
                }
                catch { }
                return true;
            }
        }
    }
}
