using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
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
                row.UpdateSourceProjection(chart);
                return row;
            }
            row = LibraryChartRow.FromChartFile(chart);
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
            bmsonRow.UpdateSourceProjection(chart);
            return bmsonRow;
        }

        bmsonRow = LibraryChartRow.FromChartFile(chart);
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
        bool sourceIdentityChanged = membershipChanged;
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
                var nextSortKeys = BmsonLibrarySortKeySnapshot.Capture(song);
                if (!hasPreviousSortKeys || previousSortKeys.HasChanged(nextSortKeys))
                {
                    sortKeyChanged = true;
                    sourceIdentityChanged |= !hasPreviousSortKeys || previousSortKeys.HasSourceIdentityChanged(nextSortKeys);
                }
            }
            if (row != null)
            {
                nextByPath[song.path] = row;
                nextBySong[song] = row;
                nextSortKeysByPath[song.path] = BmsonLibrarySortKeySnapshot.Capture(song);
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
        return new BmsonLibraryRowCacheSyncResult(membershipChanged, sortKeyChanged, sourceIdentityChanged, sourceReferenceChanged);
    }

    internal int PruneBmsFiles(IEnumerable<BMSFile> currentFiles)
    {
        if (rowsByFile.Count == 0)
        {
            return 0;
        }
        var current = new HashSet<BMSFile>(
            (currentFiles ?? []).Where(file => file != null),
            BmsFileReferenceComparer.Instance);
        List<BMSFile> removed = [.. rowsByFile.Keys.Where(file => !current.Contains(file))];
        foreach (BMSFile file in removed)
        {
            rowsByFile.Remove(file);
        }
        return removed.Count;
    }

    internal int RemoveBmsFiles(IEnumerable<BMSFile> removedFiles)
    {
        if (rowsByFile.Count == 0)
        {
            return 0;
        }
        var removed = new HashSet<BMSFile>(
            (removedFiles ?? []).Where(file => file != null),
            BmsFileReferenceComparer.Instance);
        if (removed.Count == 0)
        {
            return 0;
        }

        var removedPaths = new HashSet<string>(
            removed.Select(file => file?.path).Where(path => !string.IsNullOrWhiteSpace(path)),
            StringComparer.OrdinalIgnoreCase);
        List<BMSFile> removedKeys = [.. rowsByFile.Keys.Where(file =>
            removed.Contains(file)
            || (!string.IsNullOrWhiteSpace(file?.path) && removedPaths.Contains(file.path)))];
        int count = 0;
        foreach (BMSFile file in removedKeys)
        {
            if (rowsByFile.Remove(file))
            {
                count++;
            }
        }
        return count;
    }

    internal BmsonLibraryRowCacheSyncResult RemoveBmsonSongs(IEnumerable<LR2SongDBExtended.bmson_song> removedSongs)
    {
        if (rowsByBmsonPath.Count == 0 && rowsByBmsonSong.Count == 0 && bmsonSortKeysByPath.Count == 0)
        {
            return default;
        }

        var removedSongRefs = new HashSet<LR2SongDBExtended.bmson_song>(
            (removedSongs ?? []).Where(song => song != null),
            BmsonSongReferenceComparer.Instance);
        if (removedSongRefs.Count == 0)
        {
            return default;
        }

        var removedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (LR2SongDBExtended.bmson_song song in removedSongRefs)
        {
            if (!string.IsNullOrWhiteSpace(song.path))
            {
                removedPaths.Add(song.path);
            }
            if (rowsByBmsonSong.TryGetValue(song, out LibraryChartRow row))
            {
                string rowPath = row?.GetBmsonStorageOwner()?.path;
                if (!string.IsNullOrWhiteSpace(rowPath))
                {
                    removedPaths.Add(rowPath);
                }
            }
        }

        int removedCount = 0;
        foreach (string path in removedPaths)
        {
            if (rowsByBmsonPath.Remove(path))
            {
                removedCount++;
            }
            bmsonSortKeysByPath.Remove(path);
        }

        List<LR2SongDBExtended.bmson_song> removedKeys = [.. rowsByBmsonSong.Keys.Where(song =>
            removedSongRefs.Contains(song)
            || (!string.IsNullOrWhiteSpace(song?.path) && removedPaths.Contains(song.path)))];
        foreach (LR2SongDBExtended.bmson_song song in removedKeys)
        {
            rowsByBmsonSong.Remove(song);
        }

        bool changed = removedCount > 0 || removedKeys.Count > 0;
        return new BmsonLibraryRowCacheSyncResult(
            membershipChanged: changed,
            sortKeyChanged: changed,
            sourceIdentityChanged: changed);
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
        var previousSortKeys = BmsonLibrarySortKeySnapshot.Capture(row.GetBmsonStorageOwner());
        row.UpdateFromBmsonSong(nextSong);
        return previousSortKeys.HasChanged(BmsonLibrarySortKeySnapshot.Capture(nextSong));
    }

    internal static bool HasBmsonLibrarySortKeyChangedForTest(LibraryChartRow row, Action<LR2SongDBExtended.bmson_song> mutateCurrentSong)
    {
        LR2SongDBExtended.bmson_song bmsonSong = row?.GetBmsonStorageOwner();
        if (bmsonSong == null || mutateCurrentSong == null)
        {
            return false;
        }
        var previousSortKeys = BmsonLibrarySortKeySnapshot.Capture(bmsonSong);
        mutateCurrentSong(bmsonSong);
        return previousSortKeys.HasChanged(BmsonLibrarySortKeySnapshot.Capture(bmsonSong));
    }

    internal static bool HasBmsonLibrarySourceIdentityChangedForTest(LibraryChartRow row, LR2SongDBExtended.bmson_song nextSong)
    {
        if (row == null)
        {
            return nextSong != null;
        }
        var previousSortKeys = BmsonLibrarySortKeySnapshot.Capture(row.GetBmsonStorageOwner());
        row.UpdateFromBmsonSong(nextSong);
        return previousSortKeys.HasSourceIdentityChanged(BmsonLibrarySortKeySnapshot.Capture(nextSong));
    }

    internal static bool HasBmsonLibrarySourceIdentityChangedForTest(LibraryChartRow row, Action<LR2SongDBExtended.bmson_song> mutateCurrentSong)
    {
        LR2SongDBExtended.bmson_song bmsonSong = row?.GetBmsonStorageOwner();
        if (bmsonSong == null || mutateCurrentSong == null)
        {
            return false;
        }
        var previousSortKeys = BmsonLibrarySortKeySnapshot.Capture(bmsonSong);
        mutateCurrentSong(bmsonSong);
        return previousSortKeys.HasSourceIdentityChanged(BmsonLibrarySortKeySnapshot.Capture(bmsonSong));
    }

    internal static IReadOnlyList<string> GetBmsonLibrarySortKeySnapshotColumnNamesForTest()
    {
        return BmsonLibrarySortKeySnapshot.ColumnNames;
    }

    internal static IReadOnlyList<string> GetBmsonLibrarySourceIdentitySnapshotColumnNamesForTest()
    {
        return BmsonLibrarySortKeySnapshot.SourceIdentityColumnNames;
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
            nameof(LibraryChartRow.Folder),
            nameof(LibraryChartRow.path),
            nameof(LibraryChartRow.tag),
            nameof(LibraryChartRow.hash),
            nameof(LibraryChartRow.sha256)
        ];

        internal static readonly IReadOnlyList<string> SourceIdentityColumnNames =
        [
            nameof(LibraryChartRow.Title),
            nameof(LibraryChartRow.Artist),
            nameof(LibraryChartRow.genre),
            nameof(LibraryChartRow.level),
            nameof(LibraryChartRow.mode),
            nameof(LibraryChartRow.Folder),
            nameof(LibraryChartRow.path),
            nameof(LibraryChartRow.tag),
            nameof(LibraryChartRow.hash),
            nameof(LibraryChartRow.sha256)
        ];

        private readonly string title;
        private readonly string artist;
        private readonly string genre;
        private readonly string levelText;
        private readonly double? levelValue;
        private readonly int? mode;
        private readonly string folder;
        private readonly string path;
        private readonly string tag;
        private readonly string hash;
        private readonly string sha256;

        private BmsonLibrarySortKeySnapshot(LR2SongDBExtended.bmson_song song)
        {
            title = song == null ? string.Empty : BmsonSongParser.ComposeDisplayTitle(song);
            artist = song?.artist ?? string.Empty;
            genre = song?.genre ?? string.Empty;
            levelText = song?.level.HasValue == true ? song.level.Value.ToString(CultureInfo.InvariantCulture) : string.Empty;
            levelValue = song?.level;
            mode = song == null ? null : BmsonSongParser.ResolvePlaylistMode(song.mode_hint);
            folder = song == null ? string.Empty : BmsonSongParser.ComposeDisplayFolder(song);
            path = song?.path ?? string.Empty;
            tag = string.Empty;
            hash = song?.md5 ?? string.Empty;
            sha256 = song?.sha256 ?? string.Empty;
        }

        internal static BmsonLibrarySortKeySnapshot Capture(LR2SongDBExtended.bmson_song song)
        {
            return new BmsonLibrarySortKeySnapshot(song);
        }

        internal bool HasChanged(BmsonLibrarySortKeySnapshot next)
        {
            return HasSourceIdentityChanged(next);
        }

        internal bool HasSourceIdentityChanged(BmsonLibrarySortKeySnapshot next)
        {
            return !string.Equals(title, next.title, StringComparison.Ordinal)
                || !string.Equals(artist, next.artist, StringComparison.Ordinal)
                || !string.Equals(genre, next.genre, StringComparison.Ordinal)
                || !string.Equals(levelText, next.levelText, StringComparison.Ordinal)
                || !object.Equals(levelValue, next.levelValue)
                || mode != next.mode
                || !string.Equals(folder, next.folder, StringComparison.Ordinal)
                || !string.Equals(path, next.path, StringComparison.Ordinal)
                || !string.Equals(tag, next.tag, StringComparison.Ordinal)
                || !string.Equals(hash, next.hash, StringComparison.Ordinal)
                || !string.Equals(sha256, next.sha256, StringComparison.Ordinal);
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
