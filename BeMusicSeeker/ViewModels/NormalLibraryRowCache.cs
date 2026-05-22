using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.ViewModels;

internal sealed class LibraryRowCacheBuildStats
{
    internal int HitCount { get; set; }

    internal int MissCount { get; set; }

    internal int PrunedCount { get; set; }
}

internal sealed class NormalLibraryRowCache
{
    private readonly Dictionary<BMSFile, LibraryChartRow> rowsByFile = new(BmsFileReferenceComparer.Instance);

    private readonly Dictionary<string, LibraryChartRow> rowsByBmsonPath = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<LR2SongDBExtended.bmson_song, LibraryChartRow> rowsByBmsonSong = new(BmsonSongReferenceComparer.Instance);

    private readonly Dictionary<string, BmsonLibrarySortKeySnapshot> bmsonSortKeysByPath = new(StringComparer.OrdinalIgnoreCase);

    internal NormalLibraryRowCache()
    {
    }

    internal int Count => rowsByFile.Count + rowsByBmsonPath.Count;

    internal List<LibraryChartRow> SnapshotRows()
    {
        return
        [
            .. rowsByFile.Values.Where(row => row != null),
            .. rowsByBmsonPath.Values.Where(row => row != null)
        ];
    }

    internal LibraryChartRow GetOrCreate(ChartFile chart, LibraryRowCacheBuildStats stats)
    {
        BMSFile file = chart?.GetBmsStorageOwner();
        if (file != null)
        {
            if (rowsByFile.TryGetValue(file, out LibraryChartRow row))
            {
                if (stats != null)
                {
                    stats.HitCount++;
                }
                return row;
            }
            row = LibraryChartRow.FromBmsFile(file);
            if (row == null)
            {
                return null;
            }
            rowsByFile[file] = row;
            if (stats != null)
            {
                stats.MissCount++;
            }
            return row;
        }

        LR2SongDBExtended.bmson_song bmsonSong = chart?.GetBmsonStorageOwner();
        if (bmsonSong == null)
        {
            return null;
        }
        LibraryChartRow bmsonRow = null;
        if (!rowsByBmsonSong.TryGetValue(bmsonSong, out bmsonRow)
            && !string.IsNullOrWhiteSpace(bmsonSong.path))
        {
            rowsByBmsonPath.TryGetValue(bmsonSong.path, out bmsonRow);
        }
        if (bmsonRow != null)
        {
            return bmsonRow;
        }

        bmsonRow = LibraryChartRow.FromBmsonSong(bmsonSong);
        return bmsonRow;
    }

    internal BmsonLibraryRowCacheSyncResult SyncBmsonRows(
        IEnumerable<LR2SongDBExtended.bmson_song> bmsonSongs,
        Action<LibraryChartRow> configureRow)
    {
        List<LR2SongDBExtended.bmson_song> snapshot =
        [
            .. (bmsonSongs ?? [])
                .Where(song => song != null && !string.IsNullOrWhiteSpace(song.path))
                .OrderBy(song => song.path, StringComparer.OrdinalIgnoreCase)
        ];
        var nextPaths = new HashSet<string>(snapshot.Select(song => song.path), StringComparer.OrdinalIgnoreCase);
        bool membershipChanged = rowsByBmsonPath.Count != nextPaths.Count || rowsByBmsonPath.Keys.Any(path => !nextPaths.Contains(path));
        bool sourceReferenceChanged = false;
        bool sortKeyChanged = membershipChanged;
        var nextByPath = new Dictionary<string, LibraryChartRow>(StringComparer.OrdinalIgnoreCase);
        var nextBySong = new Dictionary<LR2SongDBExtended.bmson_song, LibraryChartRow>(BmsonSongReferenceComparer.Instance);
        var nextSortKeysByPath = new Dictionary<string, BmsonLibrarySortKeySnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (LR2SongDBExtended.bmson_song song in snapshot)
        {
            bool foundBySameReference = rowsByBmsonSong.TryGetValue(song, out LibraryChartRow row);
            if (!foundBySameReference)
            {
                rowsByBmsonPath.TryGetValue(song.path, out row);
                if (row != null)
                {
                    sourceReferenceChanged = true;
                }
            }
            if (row == null)
            {
                row = LibraryChartRow.FromBmsonSong(song);
                configureRow?.Invoke(row);
                membershipChanged = true;
                sortKeyChanged = true;
            }
            else
            {
                if (!row.ReferencesBmsonStorageOwner(song))
                {
                    sourceReferenceChanged = true;
                }
                bool hasPreviousSortKeys = bmsonSortKeysByPath.TryGetValue(song.path, out BmsonLibrarySortKeySnapshot previousSortKeys);
                row.UpdateFromBmsonSong(song);
                configureRow?.Invoke(row);
                if (!hasPreviousSortKeys || previousSortKeys.HasChanged(row))
                {
                    sortKeyChanged = true;
                }
            }
            if (row != null)
            {
                nextByPath[song.path] = row;
                nextBySong[song] = row;
                nextSortKeysByPath[song.path] = BmsonLibrarySortKeySnapshot.Capture(row);
            }
        }
        rowsByBmsonPath.Clear();
        foreach (KeyValuePair<string, LibraryChartRow> item in nextByPath)
        {
            rowsByBmsonPath[item.Key] = item.Value;
        }
        rowsByBmsonSong.Clear();
        foreach (KeyValuePair<LR2SongDBExtended.bmson_song, LibraryChartRow> item in nextBySong)
        {
            rowsByBmsonSong[item.Key] = item.Value;
        }
        bmsonSortKeysByPath.Clear();
        foreach (KeyValuePair<string, BmsonLibrarySortKeySnapshot> item in nextSortKeysByPath)
        {
            bmsonSortKeysByPath[item.Key] = item.Value;
        }
        return new BmsonLibraryRowCacheSyncResult(membershipChanged, sortKeyChanged, sourceReferenceChanged);
    }

    internal int Prune(IEnumerable<ChartFile> currentCharts)
    {
        var current = new HashSet<BMSFile>(
            GetBmsStorageOwners(currentCharts),
            BmsFileReferenceComparer.Instance);
        List<BMSFile> removed = [.. rowsByFile.Keys.Where(file => !current.Contains(file))];
        foreach (BMSFile file in removed)
        {
            rowsByFile.Remove(file);
        }
        return removed.Count;
    }

    internal void Clear()
    {
        rowsByFile.Clear();
        rowsByBmsonPath.Clear();
        rowsByBmsonSong.Clear();
        bmsonSortKeysByPath.Clear();
    }

    internal static bool HasBmsonLibrarySortKeyChangedForTest(LibraryChartRow row, LR2SongDBExtended.bmson_song nextSong)
    {
        if (row == null)
        {
            return nextSong != null;
        }
        var previousSortKeys = BmsonLibrarySortKeySnapshot.Capture(row);
        row.UpdateFromBmsonSong(nextSong);
        return previousSortKeys.HasChanged(row);
    }

    internal static bool HasBmsonLibrarySortKeyChangedForTest(LibraryChartRow row, Action<LR2SongDBExtended.bmson_song> mutateCurrentSong)
    {
        LR2SongDBExtended.bmson_song bmsonSong = row?.GetBmsonStorageOwner();
        if (bmsonSong == null || mutateCurrentSong == null)
        {
            return false;
        }
        var previousSortKeys = BmsonLibrarySortKeySnapshot.Capture(row);
        mutateCurrentSong(bmsonSong);
        return previousSortKeys.HasChanged(row);
    }

    internal static IReadOnlyList<string> GetBmsonLibrarySortKeySnapshotColumnNamesForTest()
    {
        return BmsonLibrarySortKeySnapshot.ColumnNames;
    }

    private static IEnumerable<BMSFile> GetBmsStorageOwners(IEnumerable<ChartFile> charts)
    {
        return (charts ?? [])
            .Select(chart => chart?.GetBmsStorageOwner())
            .Where(file => file != null);
    }

    private readonly struct BmsonLibrarySortKeySnapshot
    {
        internal static readonly IReadOnlyList<string> ColumnNames =
        [
            nameof(LibraryChartRow.Title),
            nameof(LibraryChartRow.Artist),
            nameof(LibraryChartRow.genre),
            nameof(LibraryChartRow.level),
            nameof(LibraryChartRow.mode),
            nameof(LibraryChartRow.WarningDigestText),
            nameof(LibraryChartRow.Folder),
            nameof(LibraryChartRow.path),
            nameof(LibraryChartRow.tag),
            nameof(LibraryChartRow.hash),
            nameof(LibraryChartRow.sha256),
            nameof(LibraryChartRow.instl_dst),
            nameof(LibraryChartRow.InstallDestinationTitle),
            nameof(LibraryChartRow.InstallDestinationArtist),
            nameof(LibraryChartRow.RefTablesSymbols),
            nameof(LibraryChartRow.WAVHealth),
            nameof(LibraryChartRow.BGAHealth),
            nameof(LibraryChartRow.MovieHealth),
            nameof(LibraryChartRow.encoding),
            nameof(LibraryChartRow.clear),
            nameof(LibraryChartRow.rateDouble),
            nameof(LibraryChartRow.score),
            nameof(LibraryChartRow.maxcombo),
            nameof(LibraryChartRow.minbp),
            nameof(LibraryChartRow.rankingString),
            nameof(LibraryChartRow.rankingLastupdate),
            nameof(LibraryChartRow.stddevVal),
            nameof(LibraryChartRow.scoreDifficulty),
            nameof(LibraryChartRow.ChartLevelSortKey),
            nameof(LibraryChartRow.ChartDifficultySortKey),
            nameof(LibraryChartRow.ChartMainBpmSortKey),
            nameof(LibraryChartRow.ChartMaxBpmSortKey),
            nameof(LibraryChartRow.ChartMinBpmSortKey),
            nameof(LibraryChartRow.ChartDurationSortKey),
            nameof(LibraryChartRow.ChartJudgeSortKey),
            nameof(LibraryChartRow.ChartFeatureSortKey),
            nameof(LibraryChartRow.ChartNotes),
            nameof(LibraryChartRow.ChartLongNotes),
            nameof(LibraryChartRow.ChartScratchNotes),
            nameof(LibraryChartRow.ChartTotalSortKey),
            nameof(LibraryChartRow.ChartTotalPerNoteSortKey),
            nameof(LibraryChartRow.ChartDensitySortKey),
            nameof(LibraryChartRow.ChartPeakDensitySortKey),
            nameof(LibraryChartRow.ChartEndDensitySortKey),
            nameof(LibraryChartRow.ChartSoflanCount)
        ];

        private readonly string title;
        private readonly string artist;
        private readonly string genre;
        private readonly string levelText;
        private readonly double? levelValue;
        private readonly int? mode;
        private readonly string warningDigestText;
        private readonly string folder;
        private readonly string path;
        private readonly string tag;
        private readonly string hash;
        private readonly string sha256;
        private readonly string installDestination;
        private readonly string installDestinationTitle;
        private readonly string installDestinationArtist;
        private readonly string refTablesSymbols;
        private readonly int? wavHealth;
        private readonly int? bgaHealth;
        private readonly int? movieHealth;
        private readonly string encoding;
        private readonly ClearType clear;
        private readonly double? rateDouble;
        private readonly int? score;
        private readonly int? maxCombo;
        private readonly int? minBp;
        private readonly string rankingString;
        private readonly DateTime? rankingLastUpdate;
        private readonly double? stdDevVal;
        private readonly double? scoreDifficulty;
        private readonly double? chartLevelSortKey;
        private readonly int? chartDifficultySortKey;
        private readonly double? chartMainBpmSortKey;
        private readonly double? chartMaxBpmSortKey;
        private readonly double? chartMinBpmSortKey;
        private readonly int? chartDurationSortKey;
        private readonly int? chartJudgeSortKey;
        private readonly int? chartFeatureSortKey;
        private readonly int? chartNotes;
        private readonly int? chartLongNotes;
        private readonly int? chartScratchNotes;
        private readonly double? chartTotalSortKey;
        private readonly double? chartTotalPerNoteSortKey;
        private readonly double? chartDensitySortKey;
        private readonly double? chartPeakDensitySortKey;
        private readonly double? chartEndDensitySortKey;
        private readonly int? chartSoflanCount;

        private BmsonLibrarySortKeySnapshot(LibraryChartRow row)
        {
            title = row?.Title ?? string.Empty;
            artist = row?.Artist ?? string.Empty;
            genre = row?.genre ?? string.Empty;
            levelText = row?.Level ?? string.Empty;
            levelValue = row?.level;
            mode = row?.mode;
            warningDigestText = row?.WarningDigestText ?? string.Empty;
            folder = row?.Folder ?? string.Empty;
            path = row?.path ?? string.Empty;
            tag = row?.tag ?? string.Empty;
            hash = row?.hash ?? string.Empty;
            sha256 = row?.sha256 ?? string.Empty;
            installDestination = row?.instl_dst ?? string.Empty;
            installDestinationTitle = row?.InstallDestinationTitle ?? string.Empty;
            installDestinationArtist = row?.InstallDestinationArtist ?? string.Empty;
            refTablesSymbols = row?.RefTablesSymbols ?? string.Empty;
            wavHealth = row?.WAVHealth;
            bgaHealth = row?.BGAHealth;
            movieHealth = row?.MovieHealth;
            encoding = row?.encoding ?? string.Empty;
            clear = row?.clear ?? ClearType.NO_SONG;
            rateDouble = row?.rateDouble;
            score = row?.score;
            maxCombo = row?.maxcombo;
            minBp = row?.minbp;
            rankingString = row?.rankingString ?? string.Empty;
            rankingLastUpdate = row?.rankingLastupdate;
            stdDevVal = row?.stddevVal;
            scoreDifficulty = row?.scoreDifficulty;
            chartLevelSortKey = row?.ChartLevelSortKey;
            chartDifficultySortKey = row?.ChartDifficultySortKey;
            chartMainBpmSortKey = row?.ChartMainBpmSortKey;
            chartMaxBpmSortKey = row?.ChartMaxBpmSortKey;
            chartMinBpmSortKey = row?.ChartMinBpmSortKey;
            chartDurationSortKey = row?.ChartDurationSortKey;
            chartJudgeSortKey = row?.ChartJudgeSortKey;
            chartFeatureSortKey = row?.ChartFeatureSortKey;
            chartNotes = row?.ChartNotes;
            chartLongNotes = row?.ChartLongNotes;
            chartScratchNotes = row?.ChartScratchNotes;
            chartTotalSortKey = row?.ChartTotalSortKey;
            chartTotalPerNoteSortKey = row?.ChartTotalPerNoteSortKey;
            chartDensitySortKey = row?.ChartDensitySortKey;
            chartPeakDensitySortKey = row?.ChartPeakDensitySortKey;
            chartEndDensitySortKey = row?.ChartEndDensitySortKey;
            chartSoflanCount = row?.ChartSoflanCount;
        }

        internal static BmsonLibrarySortKeySnapshot Capture(LibraryChartRow row)
        {
            return new BmsonLibrarySortKeySnapshot(row);
        }

        internal bool HasChanged(LibraryChartRow row)
        {
            return !string.Equals(title, row?.Title ?? string.Empty, StringComparison.Ordinal)
                || !string.Equals(artist, row?.Artist ?? string.Empty, StringComparison.Ordinal)
                || !string.Equals(genre, row?.genre ?? string.Empty, StringComparison.Ordinal)
                || !string.Equals(levelText, row?.Level ?? string.Empty, StringComparison.Ordinal)
                || !object.Equals(levelValue, row?.level)
                || mode != row?.mode
                || !string.Equals(warningDigestText, row?.WarningDigestText ?? string.Empty, StringComparison.Ordinal)
                || !string.Equals(folder, row?.Folder ?? string.Empty, StringComparison.Ordinal)
                || !string.Equals(path, row?.path ?? string.Empty, StringComparison.Ordinal)
                || !string.Equals(tag, row?.tag ?? string.Empty, StringComparison.Ordinal)
                || !string.Equals(hash, row?.hash ?? string.Empty, StringComparison.Ordinal)
                || !string.Equals(sha256, row?.sha256 ?? string.Empty, StringComparison.Ordinal)
                || !string.Equals(installDestination, row?.instl_dst ?? string.Empty, StringComparison.Ordinal)
                || !string.Equals(installDestinationTitle, row?.InstallDestinationTitle ?? string.Empty, StringComparison.Ordinal)
                || !string.Equals(installDestinationArtist, row?.InstallDestinationArtist ?? string.Empty, StringComparison.Ordinal)
                || !string.Equals(refTablesSymbols, row?.RefTablesSymbols ?? string.Empty, StringComparison.Ordinal)
                || wavHealth != row?.WAVHealth
                || bgaHealth != row?.BGAHealth
                || movieHealth != row?.MovieHealth
                || !string.Equals(encoding, row?.encoding ?? string.Empty, StringComparison.Ordinal)
                || clear != (row?.clear ?? ClearType.NO_SONG)
                || !object.Equals(rateDouble, row?.rateDouble)
                || score != row?.score
                || maxCombo != row?.maxcombo
                || minBp != row?.minbp
                || !string.Equals(rankingString, row?.rankingString ?? string.Empty, StringComparison.Ordinal)
                || !object.Equals(rankingLastUpdate, row?.rankingLastupdate)
                || !object.Equals(stdDevVal, row?.stddevVal)
                || !object.Equals(scoreDifficulty, row?.scoreDifficulty)
                || !object.Equals(chartLevelSortKey, row?.ChartLevelSortKey)
                || chartDifficultySortKey != row?.ChartDifficultySortKey
                || !object.Equals(chartMainBpmSortKey, row?.ChartMainBpmSortKey)
                || !object.Equals(chartMaxBpmSortKey, row?.ChartMaxBpmSortKey)
                || !object.Equals(chartMinBpmSortKey, row?.ChartMinBpmSortKey)
                || chartDurationSortKey != row?.ChartDurationSortKey
                || chartJudgeSortKey != row?.ChartJudgeSortKey
                || chartFeatureSortKey != row?.ChartFeatureSortKey
                || chartNotes != row?.ChartNotes
                || chartLongNotes != row?.ChartLongNotes
                || chartScratchNotes != row?.ChartScratchNotes
                || !object.Equals(chartTotalSortKey, row?.ChartTotalSortKey)
                || !object.Equals(chartTotalPerNoteSortKey, row?.ChartTotalPerNoteSortKey)
                || !object.Equals(chartDensitySortKey, row?.ChartDensitySortKey)
                || !object.Equals(chartPeakDensitySortKey, row?.ChartPeakDensitySortKey)
                || !object.Equals(chartEndDensitySortKey, row?.ChartEndDensitySortKey)
                || chartSoflanCount != row?.ChartSoflanCount;
        }
    }

    private sealed class BmsFileReferenceComparer : IEqualityComparer<BMSFile>
    {
        internal static readonly BmsFileReferenceComparer Instance = new();

        public bool Equals(BMSFile x, BMSFile y)
        {
            return ReferenceEquals(x, y);
        }

        public int GetHashCode(BMSFile obj)
        {
            return RuntimeHelpers.GetHashCode(obj);
        }
    }

    private sealed class BmsonSongReferenceComparer : IEqualityComparer<LR2SongDBExtended.bmson_song>
    {
        internal static readonly BmsonSongReferenceComparer Instance = new();

        public bool Equals(LR2SongDBExtended.bmson_song x, LR2SongDBExtended.bmson_song y)
        {
            return ReferenceEquals(x, y);
        }

        public int GetHashCode(LR2SongDBExtended.bmson_song obj)
        {
            return RuntimeHelpers.GetHashCode(obj);
        }
    }
}
