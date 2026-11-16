using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx;
using Corvostudio.EasyRed2.Characters;
using HarmonyLib;
using UnityEngine;

namespace EasyRed2HumanAI
{
    public static partial class VeteranAndTacticsPatches
    {
        public enum DogTagAllegiance : byte
        {
            EnemyTrophy = 0,
            FriendlyBroughtHome = 1,
            LegacyUnlocked = 2
        }

        public enum DogTagRarityTier : byte
        {
            Common = 0,
            Rare = 1,
            Unique = 2
        }

        private enum ArchiveSideFilter : byte
        {
            All = 0,
            BroughtHome = 1,
            Trophies = 2,
            MyKills = 3
        }

        private enum ArchiveRarityFilter : byte
        {
            All = 0,
            Unique = 1,
            Rare = 2,
            Common = 3
        }

        private enum ArchiveSortMode : byte
        {
            Newest = 0,
            Oldest = 1,
            Youngest = 2,
            OldestAge = 3,
            Rarity = 4,
            NameAZ = 5
        }

        private sealed class DogTagRecord
        {
            public string Name;
            public int RankValue;
            public string RawFaction;
            public int BornYear;
            public int DeathYear;
            public int Age;
            public string BattleName;
            public DogTagRarityTier Rarity;
            public string UniqueId;
            public DogTagAllegiance Side;
            public bool KilledByPlayer;
            public long CollectedEpoch;

            // Precomputed once on load/capture so OnGUI renders with zero per-frame string allocations
            public string DisplayRankAndName;
            public string DisplayLifespan;
            public string DisplayNationAndBattle;
            public string DisplayRecoveredAt;
            public string SearchHaystackLower;
        }

        private const string ArchiveFileName = "BattleHardened_DogTags.jsonl";
        private const int ArchiveRowsPerPage = 11;

        private static readonly List<DogTagRecord> _dogTagRecords = new List<DogTagRecord>(256);
        private static readonly List<int> _filteredDogTagIndices = new List<int>(256);
        private static readonly HashSet<string> _archivedUniqueIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static bool _dogTagArchiveLoaded;
        private static bool _dogTagViewDirty = true;
        private static string _dogTagArchiveFilePath;

        // Cached summary counters
        private static int _countBroughtHome;
        private static int _countTrophies;
        private static int _countMyKills;
        private static int _countCommon;
        private static int _countRare;
        private static int _countUnique;
        private static int _youngestAge;
        private static int _oldestAge;
        private static string _cachedSummaryLine = "No dogtags archived yet — open a fallen soldier's inventory to recover their tag.";

        // UI state
        private static string _archiveSearchQuery = string.Empty;
        private static ArchiveSideFilter _archiveSideFilter = ArchiveSideFilter.All;
        private static ArchiveRarityFilter _archiveRarityFilter = ArchiveRarityFilter.All;
        private static ArchiveSortMode _archiveSortMode = ArchiveSortMode.Newest;
        private static int _archiveCurrentPage;

        public static int TotalArchivedDogTagsCount
        {
            get
            {
                EnsureDogTagArchiveLoaded();
                return _dogTagRecords.Count;
            }
        }

        // =====================================================================
        // Native Capture Hook: Soldier.ExtractDogTag (0x180748BA0)
        // Atomically pops soldier.dogTag (+0x238) and sets it to null, so it
        // returns non-null strictly once per corpse and only on deliberate full
        // inventory open (BodyPart.OpenInventoryAndCollectDogTag).
        // =====================================================================
        [HarmonyPatch(typeof(Soldier), nameof(Soldier.ExtractDogTag))]
        public static class Soldier_ExtractDogTag_Patch
        {
            public static void Postfix(Soldier __instance, DogTags.DogTag __result)
            {
                try
                {
                    if (__result == null ||
                        __instance == null ||
                        __instance.Pointer == IntPtr.Zero ||
                        !VisionConfig.EnableMod.Value ||
                        VisionConfig.EnableDogTagArchive == null ||
                        !VisionConfig.EnableDogTagArchive.Value)
                    {
                        return;
                    }

                    RecordExtractedDogTag(__instance, __result);
                }
                catch (Exception ex)
                {
                    AiCore.LogDebug("DogTagArchiveErr", ex.Message, 5.0f);
                }
            }
        }

        private static void EnsureDogTagArchiveLoaded()
        {
            if (_dogTagArchiveLoaded)
                return;

            _dogTagArchiveLoaded = true;
            _dogTagRecords.Clear();
            _archivedUniqueIds.Clear();
            ResetSummaryCounters();

            try
            {
                _dogTagArchiveFilePath = Path.Combine(Paths.ConfigPath, ArchiveFileName);
                if (!File.Exists(_dogTagArchiveFilePath))
                {
                    RefreshSummaryLine();
                    _dogTagViewDirty = true;
                    return;
                }

                using (var fs = new FileStream(_dogTagArchiveFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs, Encoding.UTF8))
                {
                    string line;
                    while ((line = sr.ReadLine()) != null)
                    {
                        if (string.IsNullOrWhiteSpace(line))
                            continue;

                        if (TryParseJsonLine(line, out DogTagRecord rec))
                        {
                            FinalizeAndIndexRecord(rec);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[DogTagArchive] Failed reading {ArchiveFileName}: {ex.Message}");
            }

            RefreshSummaryLine();
            _dogTagViewDirty = true;
        }

        private static void RecordExtractedDogTag(Soldier corpse, DogTags.DogTag tag)
        {
            EnsureDogTagArchiveLoaded();

            Soldier localPlayer = Soldier.CurrentControlledSoldierOrNull();
            string localFac = localPlayer != null ? AiCore.GetCachedFaction(localPlayer) : string.Empty;
            string corpseFac = !string.IsNullOrWhiteSpace(tag.faction) ? tag.faction : AiCore.GetCachedFaction(corpse);

            bool isFriendly = !string.IsNullOrEmpty(localFac) &&
                              !string.IsNullOrEmpty(corpseFac) &&
                              AiCore.AreSameSideFactions(localFac, corpseFac);

            DogTagAllegiance side = isFriendly
                ? DogTagAllegiance.FriendlyBroughtHome
                : DogTagAllegiance.EnemyTrophy;

            bool killedByPlayer = !isFriendly && WasVictimKilledByLocalPlayer(corpse.Pointer);
            DogTagRarityTier rarity = MapRarity(tag.type);

            string uniqueId = string.Empty;
            if (rarity == DogTagRarityTier.Unique && !string.IsNullOrEmpty(tag.special_loadout))
            {
                try { uniqueId = DogTags.DogTag.FindDogTagIDByLoadoutId(tag.special_loadout) ?? tag.special_loadout; }
                catch { uniqueId = tag.special_loadout; }
            }

            int bornYear = tag.bornYear;
            int deathYear = ResolveBattleHistoricalYear(bornYear);
            string battleName = ResolveActiveBattleDisplayName(tag.available_map);
            long epoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            var rec = new DogTagRecord
            {
                Name = CleanTagName(tag.name_surname),
                RankValue = (int)tag.rank,
                RawFaction = corpseFac ?? string.Empty,
                BornYear = bornYear,
                DeathYear = deathYear,
                BattleName = battleName,
                Rarity = rarity,
                UniqueId = uniqueId ?? string.Empty,
                Side = side,
                KilledByPlayer = killedByPlayer,
                CollectedEpoch = epoch
            };

            FinalizeAndIndexRecord(rec);
            AppendRecordToDisk(rec);
            RefreshSummaryLine();
            _dogTagViewDirty = true;
        }

        public static int SyncPreExistingUniqueDogTags()
        {
            EnsureDogTagArchiveLoaded();
            int imported = 0;

            try
            {
                GameStatistics stats = SavableData.Statistics;
                var ownedUniques = stats != null ? stats.uniqueDogTags : null;
                var allArchive = DogTags.DogTagArchive.all;
                if (ownedUniques == null || allArchive == null || ownedUniques.Length == 0)
                    return 0;

                for (int i = 0; i < ownedUniques.Length; i++)
                {
                    string uid = ownedUniques[i];
                    if (string.IsNullOrWhiteSpace(uid) || _archivedUniqueIds.Contains(uid))
                        continue;

                    if (!allArchive.ContainsKey(uid))
                        continue;

                    DogTags.DogTag heroTag = allArchive[uid];
                    if (heroTag == null)
                        continue;

                    int bornYear = heroTag.bornYear;
                    int estYear = EstimateMapYear(heroTag.available_map, bornYear);

                    var rec = new DogTagRecord
                    {
                        Name = CleanTagName(heroTag.name_surname),
                        RankValue = (int)heroTag.rank,
                        RawFaction = heroTag.faction ?? string.Empty,
                        BornYear = bornYear,
                        DeathYear = estYear,
                        BattleName = FormatGamemapName(heroTag.available_map),
                        Rarity = DogTagRarityTier.Unique,
                        UniqueId = uid,
                        Side = DogTagAllegiance.LegacyUnlocked,
                        KilledByPlayer = false,
                        CollectedEpoch = 0L
                    };

                    FinalizeAndIndexRecord(rec);
                    AppendRecordToDisk(rec);
                    imported++;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[DogTagArchive] Unique hero sync warning: {ex.Message}");
            }

            if (imported > 0)
            {
                RefreshSummaryLine();
                _dogTagViewDirty = true;
            }
            return imported;
        }

        private static void ResetSummaryCounters()
        {
            _countBroughtHome = 0;
            _countTrophies = 0;
            _countMyKills = 0;
            _countCommon = 0;
            _countRare = 0;
            _countUnique = 0;
            _youngestAge = 0;
            _oldestAge = 0;
        }

        private static void FinalizeAndIndexRecord(DogTagRecord rec)
        {
            if (string.IsNullOrWhiteSpace(rec.Name))
                rec.Name = "Unknown Soldier";

            rec.Age = (rec.BornYear >= 1860 && rec.DeathYear >= rec.BornYear && (rec.DeathYear - rec.BornYear) <= 95)
                ? (rec.DeathYear - rec.BornYear)
                : 0;

            string rankShort = FormatRankShort((Rank)rec.RankValue);
            rec.DisplayRankAndName = string.IsNullOrEmpty(rankShort)
                ? rec.Name
                : $"{rankShort} {rec.Name}";

            if (rec.BornYear >= 1860 && rec.DeathYear >= rec.BornYear && rec.Age > 0)
                rec.DisplayLifespan = $"{rec.BornYear}\u2013{rec.DeathYear} (Age {rec.Age})";
            else if (rec.BornYear >= 1860)
                rec.DisplayLifespan = $"b. {rec.BornYear}";
            else
                rec.DisplayLifespan = "Unknown Years";

            string prettyNation = AiCore.FormatFactionWithAlliance(rec.RawFaction);
            if (string.IsNullOrEmpty(prettyNation))
                prettyNation = "Unknown Nation";

            rec.DisplayNationAndBattle = !string.IsNullOrWhiteSpace(rec.BattleName)
                ? $"{prettyNation}  \u2022  {rec.BattleName}"
                : prettyNation;

            if (rec.CollectedEpoch > 0)
            {
                DateTime localDt = DateTimeOffset.FromUnixTimeSeconds(rec.CollectedEpoch).LocalDateTime;
                rec.DisplayRecoveredAt = localDt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            }
            else
            {
                rec.DisplayRecoveredAt = "Legacy Save";
            }

            string sideToken = rec.Side == DogTagAllegiance.FriendlyBroughtHome
                ? "brought home friend friendly ally allied comrade"
                : (rec.Side == DogTagAllegiance.EnemyTrophy
                    ? (rec.KilledByPlayer ? "trophy enemy my kill killed" : "trophy enemy")
                    : "legacy unlocked hero");

            string rarityToken = rec.Rarity == DogTagRarityTier.Unique
                ? "unique hero"
                : (rec.Rarity == DogTagRarityTier.Rare ? "rare" : "common");

            rec.SearchHaystackLower = (
                rec.DisplayRankAndName + " " +
                FormatRankFull((Rank)rec.RankValue) + " " +
                rec.DisplayLifespan + " " +
                rec.DisplayNationAndBattle + " " +
                rec.DisplayRecoveredAt + " " +
                sideToken + " " +
                rarityToken + " " +
                rec.CollectedEpoch.ToString(CultureInfo.InvariantCulture)
            ).ToLowerInvariant();

            _dogTagRecords.Add(rec);
            if (!string.IsNullOrEmpty(rec.UniqueId))
                _archivedUniqueIds.Add(rec.UniqueId);

            if (rec.Side == DogTagAllegiance.FriendlyBroughtHome)
                _countBroughtHome++;
            else if (rec.Side == DogTagAllegiance.EnemyTrophy)
                _countTrophies++;

            if (rec.KilledByPlayer)
                _countMyKills++;

            switch (rec.Rarity)
            {
                case DogTagRarityTier.Unique: _countUnique++; break;
                case DogTagRarityTier.Rare: _countRare++; break;
                default: _countCommon++; break;
            }

            if (rec.Age > 0)
            {
                if (_youngestAge == 0 || rec.Age < _youngestAge)
                    _youngestAge = rec.Age;
                if (rec.Age > _oldestAge)
                    _oldestAge = rec.Age;
            }
        }

        private static void RefreshSummaryLine()
        {
            int total = _dogTagRecords.Count;
            if (total == 0)
            {
                _cachedSummaryLine = "No dogtags archived yet — open a fallen soldier's inventory to recover their tag.";
                return;
            }

            string ageSpan = _youngestAge > 0
                ? $"  |  Ages: {_youngestAge}\u2013{_oldestAge}"
                : string.Empty;

            _cachedSummaryLine =
                $"Total: {total}  |  Brought Home: {_countBroughtHome}  |  Trophies: {_countTrophies} (Your Kills: {_countMyKills})  |  " +
                $"Unique: {_countUnique}  Rare: {_countRare}  Common: {_countCommon}{ageSpan}";
        }

        private static DogTagRarityTier MapRarity(DogTags.DogTagType nativeType)
        {
            if (nativeType == DogTags.DogTagType.Unique)
                return DogTagRarityTier.Unique;
            if (nativeType == DogTags.DogTagType.Rare)
                return DogTagRarityTier.Rare;
            return DogTagRarityTier.Common;
        }

        private static int ResolveBattleHistoricalYear(int bornYear)
        {
            int year = 0;
            try { year = SoldierIdentityManager.GetCurrentYear(); } catch { }

            if (year < 1900 || year > 2050)
            {
                try
                {
                    BattleData bd = BattleManager.GetCurrentBattleData();
                    if (bd != null && bd.year >= 1900 && bd.year <= 2050)
                        year = bd.year;
                }
                catch { }
            }

            if (year < 1900 || year > 2050)
                year = 1944;

            if (bornYear >= 1860 && year < bornYear + 14)
                year = bornYear + 18;

            return year;
        }

        private static int EstimateMapYear(Gamemap map, int bornYear)
        {
            string m = map.ToString();
            int year = 1944;
            if (m.IndexOf("Stalingrad", StringComparison.OrdinalIgnoreCase) >= 0 ||
                m.IndexOf("Tunisia", StringComparison.OrdinalIgnoreCase) >= 0)
                year = 1942;
            else if (m.IndexOf("Kos", StringComparison.OrdinalIgnoreCase) >= 0)
                year = 1943;
            else if (m.IndexOf("France1940", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     m.IndexOf("Stonne", StringComparison.OrdinalIgnoreCase) >= 0)
                year = 1940;
            else if (m.IndexOf("Shanghai", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     m.IndexOf("Nanking", StringComparison.OrdinalIgnoreCase) >= 0)
                year = 1937;
            else if (m.IndexOf("Budapest", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     m.IndexOf("Iwo", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     m.IndexOf("Okinawa", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     m.IndexOf("Berlin", StringComparison.OrdinalIgnoreCase) >= 0)
                year = 1945;

            if (bornYear >= 1860 && year < bornYear + 14)
                year = bornYear + 18;
            return year;
        }

        private static string ResolveActiveBattleDisplayName(Gamemap fallbackMap)
        {
            try
            {
                BattleData bd = BattleManager.GetCurrentBattleData();
                if (bd != null && !string.IsNullOrWhiteSpace(bd.location_name))
                {
                    return bd.location_name.Replace('_', ' ').Trim();
                }
            }
            catch { }

            return FormatGamemapName(fallbackMap);
        }

        private static string FormatGamemapName(Gamemap map)
        {
            string raw = map.ToString();
            if (string.IsNullOrWhiteSpace(raw) || string.Equals(raw, "None", StringComparison.OrdinalIgnoreCase))
                return string.Empty;

            var sb = new StringBuilder(raw.Length + 6);
            bool prevSpace = true;
            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];
                if (c == '_')
                {
                    if (!prevSpace && sb.Length > 0)
                    {
                        sb.Append(' ');
                        prevSpace = true;
                    }
                    continue;
                }
                if (i > 0 && !prevSpace && char.IsUpper(c) && char.IsLower(raw[i - 1]))
                {
                    sb.Append(' ');
                }
                sb.Append(c);
                prevSpace = (c == ' ');
            }
            return sb.ToString().Trim();
        }

        private static string CleanTagName(string rawName)
        {
            if (string.IsNullOrWhiteSpace(rawName))
                return "Unknown Soldier";
            return rawName.Replace('\n', ' ').Replace('\r', ' ').Trim();
        }

        private static string FormatRankShort(Rank rank)
        {
            switch (rank)
            {
                case Rank.pvt: return "Pvt.";
                case Rank.pre_corporal: return "PFC";
                case Rank.corporal: return "Cpl.";
                case Rank.pre_seargent: return "Jr. Sgt.";
                case Rank.seargent: return "Sgt.";
                case Rank.staff_seargent: return "SSgt.";
                case Rank.technical_seargent: return "TSgt.";
                case Rank.first_seargent: return "1st Sgt.";
                case Rank.leutenant: return "Lt.";
                case Rank.captain: return "Capt.";
                case Rank.major: return "Maj.";
                case Rank.general: return "Gen.";
                default: return string.Empty;
            }
        }

        private static string FormatRankFull(Rank rank)
        {
            switch (rank)
            {
                case Rank.pvt: return "Private";
                case Rank.pre_corporal: return "Private First Class";
                case Rank.corporal: return "Corporal";
                case Rank.pre_seargent: return "Junior Sergeant";
                case Rank.seargent: return "Sergeant";
                case Rank.staff_seargent: return "Staff Sergeant";
                case Rank.technical_seargent: return "Technical Sergeant";
                case Rank.first_seargent: return "First Sergeant";
                case Rank.leutenant: return "Lieutenant";
                case Rank.captain: return "Captain";
                case Rank.major: return "Major";
                case Rank.general: return "General";
                default: return string.Empty;
            }
        }

        // =====================================================================
        // Dependency-Free Compact JSONL Persistence
        // =====================================================================
        private static void AppendRecordToDisk(DogTagRecord rec)
        {
            try
            {
                if (string.IsNullOrEmpty(_dogTagArchiveFilePath))
                    _dogTagArchiveFilePath = Path.Combine(Paths.ConfigPath, ArchiveFileName);

                string sideCode = rec.Side == DogTagAllegiance.FriendlyBroughtHome
                    ? "F"
                    : (rec.Side == DogTagAllegiance.LegacyUnlocked ? "U" : "E");

                var sb = new StringBuilder(220);
                sb.Append("{\"n\":\""); AppendEscapedJson(sb, rec.Name);
                sb.Append("\",\"rk\":").Append(rec.RankValue);
                sb.Append(",\"fac\":\""); AppendEscapedJson(sb, rec.RawFaction);
                sb.Append("\",\"by\":").Append(rec.BornYear);
                sb.Append(",\"dy\":").Append(rec.DeathYear);
                sb.Append(",\"bat\":\""); AppendEscapedJson(sb, rec.BattleName);
                sb.Append("\",\"rar\":").Append((int)rec.Rarity);
                sb.Append(",\"uid\":\""); AppendEscapedJson(sb, rec.UniqueId);
                sb.Append("\",\"side\":\"").Append(sideCode);
                sb.Append("\",\"myk\":").Append(rec.KilledByPlayer ? 1 : 0);
                sb.Append(",\"ts\":").Append(rec.CollectedEpoch).AppendLine("}");

                using (var fs = new FileStream(_dogTagArchiveFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                using (var sw = new StreamWriter(fs, new UTF8Encoding(false)))
                {
                    sw.Write(sb.ToString());
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[DogTagArchive] Failed appending dogtag: {ex.Message}");
            }
        }

        private static void AppendEscapedJson(StringBuilder sb, string val)
        {
            if (string.IsNullOrEmpty(val))
                return;

            for (int i = 0; i < val.Length; i++)
            {
                char c = val[i];
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append(' '); break;
                    case '\r': break;
                    case '\t': sb.Append(' '); break;
                    default:
                        if (!char.IsControl(c))
                            sb.Append(c);
                        break;
                }
            }
        }

        private static bool TryParseJsonLine(string line, out DogTagRecord rec)
        {
            rec = null;
            if (string.IsNullOrWhiteSpace(line) || line[0] != '{')
                return false;

            string name = ReadJsonString(line, "\"n\":\"");
            if (string.IsNullOrEmpty(name))
                return false;

            int rk = (int)ReadJsonLong(line, "\"rk\":", 0);
            string fac = ReadJsonString(line, "\"fac\":\"");
            int by = (int)ReadJsonLong(line, "\"by\":", 0);
            int dy = (int)ReadJsonLong(line, "\"dy\":", 0);
            string bat = ReadJsonString(line, "\"bat\":\"");
            int rarInt = Mathf.Clamp((int)ReadJsonLong(line, "\"rar\":", 0), 0, 2);
            string uid = ReadJsonString(line, "\"uid\":\"");
            string sideStr = ReadJsonString(line, "\"side\":\"");
            long myk = ReadJsonLong(line, "\"myk\":", 0);
            long ts = ReadJsonLong(line, "\"ts\":", 0);

            DogTagAllegiance side = DogTagAllegiance.EnemyTrophy;
            if (string.Equals(sideStr, "F", StringComparison.OrdinalIgnoreCase))
                side = DogTagAllegiance.FriendlyBroughtHome;
            else if (string.Equals(sideStr, "U", StringComparison.OrdinalIgnoreCase))
                side = DogTagAllegiance.LegacyUnlocked;

            rec = new DogTagRecord
            {
                Name = name,
                RankValue = rk,
                RawFaction = fac ?? string.Empty,
                BornYear = by,
                DeathYear = dy,
                BattleName = bat ?? string.Empty,
                Rarity = (DogTagRarityTier)rarInt,
                UniqueId = uid ?? string.Empty,
                Side = side,
                KilledByPlayer = myk != 0,
                CollectedEpoch = ts
            };
            return true;
        }

        private static string ReadJsonString(string json, string keyPrefix)
        {
            int idx = json.IndexOf(keyPrefix, StringComparison.Ordinal);
            if (idx < 0)
                return string.Empty;

            int start = idx + keyPrefix.Length;
            var sb = new StringBuilder(32);
            for (int i = start; i < json.Length; i++)
            {
                char c = json[i];
                if (c == '\\' && i + 1 < json.Length)
                {
                    i++;
                    sb.Append(json[i]);
                    continue;
                }
                if (c == '"')
                    break;
                sb.Append(c);
            }
            return sb.ToString();
        }

        private static long ReadJsonLong(string json, string keyPrefix, long fallback)
        {
            int idx = json.IndexOf(keyPrefix, StringComparison.Ordinal);
            if (idx < 0)
                return fallback;

            int start = idx + keyPrefix.Length;
            while (start < json.Length && json[start] == ' ')
                start++;

            int end = start;
            if (end < json.Length && json[end] == '-')
                end++;
            while (end < json.Length && char.IsDigit(json[end]))
                end++;

            if (end <= start)
                return fallback;

            return long.TryParse(json.Substring(start, end - start), NumberStyles.Integer, CultureInfo.InvariantCulture, out long val)
                ? val
                : fallback;
        }

        // =====================================================================
        // Cached Filter / Sort Engine + IMGUI Archive Window
        // =====================================================================
        private static void RebuildFilteredViewIfNeeded()
        {
            if (!_dogTagViewDirty)
                return;

            _dogTagViewDirty = false;
            _filteredDogTagIndices.Clear();

            string needle = !string.IsNullOrWhiteSpace(_archiveSearchQuery)
                ? _archiveSearchQuery.Trim().ToLowerInvariant()
                : string.Empty;

            string[] tokens = !string.IsNullOrEmpty(needle)
                ? needle.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                : null;

            int count = _dogTagRecords.Count;
            for (int i = 0; i < count; i++)
            {
                DogTagRecord r = _dogTagRecords[i];

                switch (_archiveSideFilter)
                {
                    case ArchiveSideFilter.BroughtHome:
                        if (r.Side != DogTagAllegiance.FriendlyBroughtHome) continue;
                        break;
                    case ArchiveSideFilter.Trophies:
                        if (r.Side != DogTagAllegiance.EnemyTrophy) continue;
                        break;
                    case ArchiveSideFilter.MyKills:
                        if (!r.KilledByPlayer) continue;
                        break;
                }

                switch (_archiveRarityFilter)
                {
                    case ArchiveRarityFilter.Unique:
                        if (r.Rarity != DogTagRarityTier.Unique) continue;
                        break;
                    case ArchiveRarityFilter.Rare:
                        if (r.Rarity != DogTagRarityTier.Rare) continue;
                        break;
                    case ArchiveRarityFilter.Common:
                        if (r.Rarity != DogTagRarityTier.Common) continue;
                        break;
                }

                if (tokens != null && tokens.Length > 0)
                {
                    bool allMatched = true;
                    for (int t = 0; t < tokens.Length; t++)
                    {
                        if (r.SearchHaystackLower.IndexOf(tokens[t], StringComparison.Ordinal) < 0)
                        {
                            allMatched = false;
                            break;
                        }
                    }
                    if (!allMatched)
                        continue;
                }

                _filteredDogTagIndices.Add(i);
            }

            _filteredDogTagIndices.Sort(CompareRecordIndices);

            int maxPage = Mathf.Max(0, (_filteredDogTagIndices.Count - 1) / ArchiveRowsPerPage);
            if (_archiveCurrentPage > maxPage)
                _archiveCurrentPage = maxPage;
        }

        private static int CompareRecordIndices(int idxA, int idxB)
        {
            DogTagRecord a = _dogTagRecords[idxA];
            DogTagRecord b = _dogTagRecords[idxB];
            int cmp;

            switch (_archiveSortMode)
            {
                case ArchiveSortMode.Oldest:
                    cmp = a.CollectedEpoch.CompareTo(b.CollectedEpoch);
                    return cmp != 0 ? cmp : idxA.CompareTo(idxB);

                case ArchiveSortMode.Youngest:
                {
                    int ageA = a.Age > 0 ? a.Age : 999;
                    int ageB = b.Age > 0 ? b.Age : 999;
                    cmp = ageA.CompareTo(ageB);
                    return cmp != 0 ? cmp : b.CollectedEpoch.CompareTo(a.CollectedEpoch);
                }

                case ArchiveSortMode.OldestAge:
                {
                    int ageA = a.Age > 0 ? a.Age : -1;
                    int ageB = b.Age > 0 ? b.Age : -1;
                    cmp = ageB.CompareTo(ageA);
                    return cmp != 0 ? cmp : b.CollectedEpoch.CompareTo(a.CollectedEpoch);
                }

                case ArchiveSortMode.Rarity:
                    cmp = ((int)b.Rarity).CompareTo((int)a.Rarity);
                    if (cmp != 0) return cmp;
                    return b.CollectedEpoch.CompareTo(a.CollectedEpoch);

                case ArchiveSortMode.NameAZ:
                    cmp = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                    return cmp != 0 ? cmp : b.CollectedEpoch.CompareTo(a.CollectedEpoch);

                case ArchiveSortMode.Newest:
                default:
                    cmp = b.CollectedEpoch.CompareTo(a.CollectedEpoch);
                    return cmp != 0 ? cmp : idxB.CompareTo(idxA);
            }
        }

        private static KeyCode _cachedArchiveHeaderHotkey = (KeyCode)(-999);
        private static string _cachedArchiveHeaderText = "Battle Hardened  |  Dogtag Collection Archive (F7)";
        private static string _cachedArchiveCloseBtnText = "Close Archive (F7)";
        private static int _cachedPillAllCount = -1;
        private static int _cachedPillBroughtCount = -1;
        private static int _cachedPillTrophyCount = -1;
        private static int _cachedPillMyKillsCount = -1;
        private static string _cachedPillAllLabel = "All Tags (0)";
        private static string _cachedPillBroughtLabel = "Brought Home (0)";
        private static string _cachedPillTrophyLabel = "Enemy Trophies (0)";
        private static string _cachedPillMyKillsLabel = "My Kills (0)";
        private static int _cachedPageNum = -1;
        private static int _cachedTotalPages = -1;
        private static int _cachedTotalFiltered = -1;
        private static string _cachedPaginationLabel = "Page 1 / 1  (0 matching)";

        public static void DrawDogTagArchiveWindow(float virtualW, float virtualH, Action onClose, Action onOpenConfigMenu)
        {
            EnsureDogTagArchiveLoaded();
            RebuildFilteredViewIfNeeded();

            float panelW = Mathf.Min(920f, virtualW - 24f);
            float panelH = 684f;
            float panelX = (virtualW - panelW) * 0.5f;
            float panelY = (virtualH - panelH) * 0.5f;

            GUI.color = new Color(0.05f, 0.06f, 0.08f, 0.96f);
            GUI.Box(new Rect(panelX, panelY, panelW, panelH), "");

            KeyCode hk = VisionConfig.DogTagArchiveHotkey != null ? VisionConfig.DogTagArchiveHotkey.Value : KeyCode.F7;
            if (hk != _cachedArchiveHeaderHotkey)
            {
                _cachedArchiveHeaderHotkey = hk;
                _cachedArchiveHeaderText = $"Battle Hardened  |  Dogtag Collection Archive ({hk})";
                _cachedArchiveCloseBtnText = $"Close Archive ({hk})";
            }

            GUI.color = new Color(1.0f, 0.88f, 0.35f, 1.0f);
            GUI.Label(new Rect(panelX + 20f, panelY + 10f, panelW - 260f, 22f), _cachedArchiveHeaderText);

            GUI.color = new Color(0.70f, 0.88f, 1.0f, 1.0f);
            if (GUI.Button(new Rect(panelX + panelW - 210f, panelY + 8f, 190f, 24f), "Back to Config Menu (F6)"))
            {
                onOpenConfigMenu?.Invoke();
                return;
            }

            // Summary strip
            GUI.color = new Color(0.80f, 0.88f, 0.95f, 0.95f);
            GUI.Label(new Rect(panelX + 20f, panelY + 36f, panelW - 40f, 20f), _cachedSummaryLine);

            // Search bar
            float searchY = panelY + 60f;
            GUI.color = new Color(0.75f, 0.84f, 0.92f, 1.0f);
            GUI.Label(new Rect(panelX + 20f, searchY + 2f, 58f, 22f), "Search:");

            GUI.color = Color.white;
            string nextSearch = GUI.TextField(new Rect(panelX + 80f, searchY, panelW - 206f, 24f), _archiveSearchQuery ?? string.Empty, 80);
            if (!string.Equals(nextSearch, _archiveSearchQuery, StringComparison.Ordinal))
            {
                _archiveSearchQuery = nextSearch;
                _archiveCurrentPage = 0;
                _dogTagViewDirty = true;
                RebuildFilteredViewIfNeeded();
            }

            GUI.color = new Color(0.90f, 0.78f, 0.78f, 0.95f);
            if (GUI.Button(new Rect(panelX + panelW - 118f, searchY, 98f, 24f), "Clear Search"))
            {
                if (!string.IsNullOrEmpty(_archiveSearchQuery))
                {
                    _archiveSearchQuery = string.Empty;
                    _archiveCurrentPage = 0;
                    _dogTagViewDirty = true;
                    RebuildFilteredViewIfNeeded();
                }
            }

            // Allegiance filter pills: All | Brought Home (Friends) | Trophies (Enemies) | My Kills
            if (_dogTagRecords.Count != _cachedPillAllCount ||
                _countBroughtHome != _cachedPillBroughtCount ||
                _countTrophies != _cachedPillTrophyCount ||
                _countMyKills != _cachedPillMyKillsCount)
            {
                _cachedPillAllCount = _dogTagRecords.Count;
                _cachedPillBroughtCount = _countBroughtHome;
                _cachedPillTrophyCount = _countTrophies;
                _cachedPillMyKillsCount = _countMyKills;
                _cachedPillAllLabel = $"All Tags ({_cachedPillAllCount})";
                _cachedPillBroughtLabel = $"Brought Home ({_cachedPillBroughtCount})";
                _cachedPillTrophyLabel = $"Enemy Trophies ({_cachedPillTrophyCount})";
                _cachedPillMyKillsLabel = $"My Kills ({_cachedPillMyKillsCount})";
            }

            float sideY = panelY + 90f;
            float sidePillW = (panelW - 40f - 12f) / 4f;
            DrawSideFilterPill(panelX + 20f + 0f * (sidePillW + 4f), sideY, sidePillW, ArchiveSideFilter.All,
                _cachedPillAllLabel, new Color(1.0f, 0.88f, 0.36f, 1.0f));
            DrawSideFilterPill(panelX + 20f + 1f * (sidePillW + 4f), sideY, sidePillW, ArchiveSideFilter.BroughtHome,
                _cachedPillBroughtLabel, new Color(0.50f, 0.96f, 0.68f, 1.0f));
            DrawSideFilterPill(panelX + 20f + 2f * (sidePillW + 4f), sideY, sidePillW, ArchiveSideFilter.Trophies,
                _cachedPillTrophyLabel, new Color(1.0f, 0.66f, 0.42f, 1.0f));
            DrawSideFilterPill(panelX + 20f + 3f * (sidePillW + 4f), sideY, sidePillW, ArchiveSideFilter.MyKills,
                _cachedPillMyKillsLabel, new Color(1.0f, 0.52f, 0.52f, 1.0f));

            // Rarity + Sort row
            float subFilterY = panelY + 120f;
            float rarW = 86f;
            DrawRarityPill(panelX + 20f + 0f * (rarW + 4f), subFilterY, rarW, ArchiveRarityFilter.All, "All Rarities");
            DrawRarityPill(panelX + 20f + 1f * (rarW + 4f), subFilterY, rarW, ArchiveRarityFilter.Unique, "Unique");
            DrawRarityPill(panelX + 20f + 2f * (rarW + 4f), subFilterY, rarW, ArchiveRarityFilter.Rare, "Rare");
            DrawRarityPill(panelX + 20f + 3f * (rarW + 4f), subFilterY, rarW, ArchiveRarityFilter.Common, "Common");

            float sortStartX = panelX + 20f + 4f * (rarW + 4f) + 14f;
            float sortAvailW = (panelX + panelW - 20f) - sortStartX;
            float sortPillW = (sortAvailW - 20f) / 6f;
            DrawSortPill(sortStartX + 0f * (sortPillW + 4f), subFilterY, sortPillW, ArchiveSortMode.Newest, "Newest");
            DrawSortPill(sortStartX + 1f * (sortPillW + 4f), subFilterY, sortPillW, ArchiveSortMode.Oldest, "Oldest");
            DrawSortPill(sortStartX + 2f * (sortPillW + 4f), subFilterY, sortPillW, ArchiveSortMode.Youngest, "Youngest");
            DrawSortPill(sortStartX + 3f * (sortPillW + 4f), subFilterY, sortPillW, ArchiveSortMode.OldestAge, "Oldest Age");
            DrawSortPill(sortStartX + 4f * (sortPillW + 4f), subFilterY, sortPillW, ArchiveSortMode.Rarity, "Rarity");
            DrawSortPill(sortStartX + 5f * (sortPillW + 4f), subFilterY, sortPillW, ArchiveSortMode.NameAZ, "Name A-Z");

            // Column headers
            float headerY = panelY + 152f;
            float xOrigin = panelX + 20f;
            float colStatusW = 148f;
            float colRarityW = 70f;
            float colNameW = 204f;
            float colYearsW = 146f;
            float colTimeW = 122f;
            float colBattleW = (panelW - 40f) - (colStatusW + colRarityW + colNameW + colYearsW + colTimeW + 20f);

            GUI.color = new Color(0.55f, 0.82f, 1.0f, 1.0f);
            GUI.Label(new Rect(xOrigin, headerY, colStatusW, 20f), "Recovery");
            GUI.Label(new Rect(xOrigin + colStatusW + 4f, headerY, colRarityW, 20f), "Rarity");
            GUI.Label(new Rect(xOrigin + colStatusW + colRarityW + 8f, headerY, colNameW, 20f), "Rank & Name");
            GUI.Label(new Rect(xOrigin + colStatusW + colRarityW + colNameW + 12f, headerY, colYearsW, 20f), "Years & Age");
            GUI.Label(new Rect(xOrigin + colStatusW + colRarityW + colNameW + colYearsW + 16f, headerY, colBattleW, 20f), "Nation & Battle");
            GUI.Label(new Rect(panelX + panelW - 20f - colTimeW, headerY, colTimeW, 20f), "Collected");

            // Rows
            float rowStartY = panelY + 176f;
            float rowStride = 39f;
            int totalFiltered = _filteredDogTagIndices.Count;

            if (totalFiltered == 0)
            {
                GUI.color = new Color(0.75f, 0.78f, 0.84f, 0.85f);
                string emptyMsg = _dogTagRecords.Count == 0
                    ? "Your dogtag collection is empty. Open a fallen friendly or enemy soldier's inventory in battle to collect their tag,\nor click 'Sync Vanilla Unique Tags' below to import any Unique hero tags already unlocked in your save."
                    : "No archived dogtags match your current search or filter.";
                GUI.Label(new Rect(panelX + 28f, rowStartY + 30f, panelW - 56f, 60f), emptyMsg);
            }
            else
            {
                int startIdx = _archiveCurrentPage * ArchiveRowsPerPage;
                int endIdx = Mathf.Min(totalFiltered, startIdx + ArchiveRowsPerPage);

                for (int idx = startIdx; idx < endIdx; idx++)
                {
                    DogTagRecord rec = _dogTagRecords[_filteredDogTagIndices[idx]];
                    float ry = rowStartY + (idx - startIdx) * rowStride;

                    GUI.color = ((idx - startIdx) & 1) == 0
                        ? new Color(0.11f, 0.13f, 0.17f, 0.78f)
                        : new Color(0.08f, 0.10f, 0.13f, 0.62f);
                    GUI.Box(new Rect(xOrigin - 4f, ry - 2f, panelW - 32f, 35f), "");

                    // 1. Recovery / Side badge
                    string statusLabel;
                    if (rec.Side == DogTagAllegiance.FriendlyBroughtHome)
                    {
                        GUI.color = new Color(0.48f, 0.95f, 0.66f, 1.0f);
                        statusLabel = "[BROUGHT HOME]";
                    }
                    else if (rec.Side == DogTagAllegiance.LegacyUnlocked)
                    {
                        GUI.color = new Color(0.82f, 0.78f, 1.0f, 1.0f);
                        statusLabel = "[HERO ARCHIVE]";
                    }
                    else if (rec.KilledByPlayer)
                    {
                        GUI.color = new Color(1.0f, 0.56f, 0.46f, 1.0f);
                        statusLabel = "[TROPHY \u2022 MY KILL]";
                    }
                    else
                    {
                        GUI.color = new Color(0.98f, 0.74f, 0.44f, 1.0f);
                        statusLabel = "[ENEMY TROPHY]";
                    }
                    GUI.Label(new Rect(xOrigin, ry + 5f, colStatusW, 24f), statusLabel);

                    // 2. Rarity
                    string rarLabel;
                    switch (rec.Rarity)
                    {
                        case DogTagRarityTier.Unique:
                            GUI.color = new Color(1.0f, 0.86f, 0.28f, 1.0f);
                            rarLabel = "UNIQUE";
                            break;
                        case DogTagRarityTier.Rare:
                            GUI.color = new Color(0.45f, 0.86f, 1.0f, 1.0f);
                            rarLabel = "RARE";
                            break;
                        default:
                            GUI.color = new Color(0.76f, 0.80f, 0.85f, 0.95f);
                            rarLabel = "Common";
                            break;
                    }
                    GUI.Label(new Rect(xOrigin + colStatusW + 4f, ry + 5f, colRarityW, 24f), rarLabel);

                    // 3. Rank & Name
                    GUI.color = Color.white;
                    GUI.Label(new Rect(xOrigin + colStatusW + colRarityW + 8f, ry + 5f, colNameW, 24f), rec.DisplayRankAndName);

                    // 4. Lifespan & Age
                    GUI.color = new Color(0.92f, 0.90f, 0.82f, 1.0f);
                    GUI.Label(new Rect(xOrigin + colStatusW + colRarityW + colNameW + 12f, ry + 5f, colYearsW, 24f), rec.DisplayLifespan);

                    // 5. Nation & Battle
                    GUI.color = new Color(0.78f, 0.86f, 0.95f, 0.96f);
                    GUI.Label(new Rect(xOrigin + colStatusW + colRarityW + colNameW + colYearsW + 16f, ry + 5f, colBattleW, 24f), rec.DisplayNationAndBattle);

                    // 6. Collected Date/Time
                    GUI.color = new Color(0.68f, 0.74f, 0.82f, 0.92f);
                    GUI.Label(new Rect(panelX + panelW - 20f - colTimeW, ry + 5f, colTimeW, 24f), rec.DisplayRecoveredAt);
                }
            }

            // Pagination & Footer actions
            int totalPages = Mathf.Max(1, (totalFiltered + ArchiveRowsPerPage - 1) / ArchiveRowsPerPage);
            float pageY = panelY + panelH - 70f;

            GUI.color = _archiveCurrentPage > 0 ? new Color(0.80f, 0.90f, 1.0f, 1.0f) : new Color(0.5f, 0.5f, 0.5f, 0.55f);
            if (GUI.Button(new Rect(panelX + 20f, pageY, 110f, 24f), "< Prev Page") && _archiveCurrentPage > 0)
            {
                _archiveCurrentPage--;
            }

            if (_archiveCurrentPage != _cachedPageNum ||
                totalPages != _cachedTotalPages ||
                totalFiltered != _cachedTotalFiltered)
            {
                _cachedPageNum = _archiveCurrentPage;
                _cachedTotalPages = totalPages;
                _cachedTotalFiltered = totalFiltered;
                _cachedPaginationLabel = $"Page {_archiveCurrentPage + 1} / {totalPages}  ({totalFiltered} matching)";
            }

            GUI.color = new Color(0.90f, 0.92f, 0.96f, 1.0f);
            GUI.Label(new Rect(panelX + 142f, pageY + 2f, 250f, 22f), _cachedPaginationLabel);

            GUI.color = (_archiveCurrentPage + 1) < totalPages ? new Color(0.80f, 0.90f, 1.0f, 1.0f) : new Color(0.5f, 0.5f, 0.5f, 0.55f);
            if (GUI.Button(new Rect(panelX + 360f, pageY, 110f, 24f), "Next Page >") && (_archiveCurrentPage + 1) < totalPages)
            {
                _archiveCurrentPage++;
            }

            float bottomY = panelY + panelH - 38f;
            GUI.color = new Color(1.0f, 0.88f, 0.42f, 1.0f);
            if (GUI.Button(new Rect(panelX + 20f, bottomY, 270f, 26f), "Sync Vanilla Unique Hero Tags"))
            {
                int added = SyncPreExistingUniqueDogTags();
                RebuildFilteredViewIfNeeded();
                VisionPatches.ShowToast(added > 0
                    ? $"[Dogtag Archive] Imported {added} unlocked Unique hero dogtag(s) from your save!"
                    : "[Dogtag Archive] All unlocked Unique hero dogtags are already in your archive.");
            }

            GUI.color = new Color(0.75f, 0.90f, 1.0f, 1.0f);
            if (GUI.Button(new Rect(panelX + 306f, bottomY, 240f, 26f), "Reload Archive from Disk"))
            {
                _dogTagArchiveLoaded = false;
                EnsureDogTagArchiveLoaded();
                RebuildFilteredViewIfNeeded();
                VisionPatches.ShowToast($"[Dogtag Archive] Reloaded {_dogTagRecords.Count} dogtag(s) from {ArchiveFileName}.");
            }

            GUI.color = new Color(1.0f, 0.80f, 0.80f, 1.0f);
            if (GUI.Button(new Rect(panelX + panelW - 220f, bottomY, 200f, 26f), _cachedArchiveCloseBtnText))
            {
                onClose?.Invoke();
            }

            GUI.color = Color.white;
        }

        private static void DrawSideFilterPill(float x, float y, float w, ArchiveSideFilter mode, string label, Color activeColor)
        {
            bool active = _archiveSideFilter == mode;
            GUI.color = active ? activeColor : new Color(0.66f, 0.72f, 0.80f, 0.90f);
            if (GUI.Button(new Rect(x, y, w, 25f), label))
            {
                _archiveSideFilter = mode;
                _archiveCurrentPage = 0;
                _dogTagViewDirty = true;
                RebuildFilteredViewIfNeeded();
            }
        }

        private static void DrawRarityPill(float x, float y, float w, ArchiveRarityFilter mode, string label)
        {
            bool active = _archiveRarityFilter == mode;
            GUI.color = active ? new Color(0.52f, 0.92f, 1.0f, 1.0f) : new Color(0.66f, 0.72f, 0.80f, 0.88f);
            if (GUI.Button(new Rect(x, y, w, 23f), label))
            {
                _archiveRarityFilter = mode;
                _archiveCurrentPage = 0;
                _dogTagViewDirty = true;
                RebuildFilteredViewIfNeeded();
            }
        }

        private static void DrawSortPill(float x, float y, float w, ArchiveSortMode mode, string label)
        {
            bool active = _archiveSortMode == mode;
            GUI.color = active ? new Color(1.0f, 0.86f, 0.36f, 1.0f) : new Color(0.66f, 0.72f, 0.80f, 0.88f);
            if (GUI.Button(new Rect(x, y, w, 23f), label))
            {
                _archiveSortMode = mode;
                _archiveCurrentPage = 0;
                _dogTagViewDirty = true;
                RebuildFilteredViewIfNeeded();
            }
        }
    }
}
