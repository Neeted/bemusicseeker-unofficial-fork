using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class Lr2FolderFileSyncItem
{
    public string FilePath { get; set; }

    public Lr2FolderFileDefinition Definition { get; set; }

    public DateTime? LastWriteTimeUtc { get; set; }

    public int FolderType { get; set; } = 2;

    public string ParentHash { get; set; }
}

internal sealed class Lr2FolderFileDbSyncRequest
{
    public IReadOnlyCollection<Lr2FolderFileSyncItem> Items { get; set; } = [];

    public IReadOnlyCollection<string> ScopeDirectories { get; set; } = [];

    public IReadOnlyCollection<string> ScopePaths { get; set; } = [];

    public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;

    public bool AllowPrune { get; set; }
}

internal sealed class Lr2FolderFileDbSyncResult(
    int itemCount,
    int generatedCount,
    int upsertedCount,
    int deletedCount,
    int skippedUnsupportedPathCount,
    int skippedMissingMetadataCount,
    long elapsedMs)
{
    public int ItemCount { get; } = itemCount;

    public int GeneratedCount { get; } = generatedCount;

    public int UpsertedCount { get; } = upsertedCount;

    public int DeletedCount { get; } = deletedCount;

    public int SkippedUnsupportedPathCount { get; } = skippedUnsupportedPathCount;

    public int SkippedMissingMetadataCount { get; } = skippedMissingMetadataCount;

    public long ElapsedMs { get; } = elapsedMs;

    public bool HasChanges => UpsertedCount > 0 || DeletedCount > 0;
}

internal static class Lr2FolderFileDbSyncService
{
    private static readonly IEqualityComparer<string> PathComparer = StringComparer.OrdinalIgnoreCase;

    internal static Lr2FolderFileDbSyncResult Sync(
        LR2SongDBExtended songDb,
        Lr2FolderFileDbSyncRequest request)
    {
        if (songDb == null)
        {
            throw new ArgumentNullException(nameof(songDb));
        }

        request ??= new Lr2FolderFileDbSyncRequest();
        var stopwatch = Stopwatch.StartNew();
        List<LR2SongDB.folder> existingRows = [.. songDb.Table<LR2SongDB.folder>()];
        Dictionary<string, LR2SongDB.folder> existingRowsByPath = CreateExistingRowMap(existingRows);
        var generatedPathsByKey = new Dictionary<string, string>(PathComparer);
        var upsertRows = new List<LR2SongDB.folder>();
        int itemCount = 0;
        int generatedCount = 0;
        int skippedUnsupportedPathCount = 0;
        int skippedMissingMetadataCount = 0;

        foreach (Lr2FolderFileSyncItem item in request.Items ?? [])
        {
            if (item == null)
            {
                continue;
            }

            itemCount++;
            string filePath = NormalizeFilePath(item.FilePath);
            if (string.IsNullOrWhiteSpace(filePath))
            {
                skippedUnsupportedPathCount++;
                continue;
            }
            existingRowsByPath.TryGetValue(filePath, out LR2SongDB.folder existingRow);
            if (item.FolderType == 1 || IsNormalDirectoryRow(existingRow))
            {
                skippedUnsupportedPathCount++;
                continue;
            }

            bool created = Lr2FolderFileProjection.TryCreateFolderRow(new Lr2FolderFileRowRequest
            {
                FilePath = filePath,
                Definition = item.Definition,
                ExistingRow = existingRow,
                LastWriteTimeUtc = item.LastWriteTimeUtc,
                GeneratedAtUtc = request.GeneratedAtUtc,
                FolderType = item.FolderType,
                ParentHash = item.ParentHash
            }, out LR2SongDB.folder row);
            if (!created)
            {
                if (item.LastWriteTimeUtc == null)
                {
                    skippedMissingMetadataCount++;
                }
                else
                {
                    skippedUnsupportedPathCount++;
                }
                continue;
            }

            generatedCount++;
            generatedPathsByKey[filePath] = filePath;
            if (!AreEquivalent(row, existingRow))
            {
                upsertRows.Add(row);
            }
        }

        List<string> deletePaths = request.AllowPrune
            ? CreateDeletePaths(existingRows, generatedPathsByKey, request.ScopeDirectories, request.ScopePaths)
            : [];

        Lr2FolderGenerationWriteResult writeResult = Lr2FolderDbWriter.ApplySyncPlan(
            songDb,
            new Lr2FolderGenerationSyncPlan(upsertRows, deletePaths));

        stopwatch.Stop();
        return new Lr2FolderFileDbSyncResult(
            itemCount,
            generatedCount,
            writeResult.UpsertedCount,
            writeResult.DeletedCount,
            skippedUnsupportedPathCount,
            skippedMissingMetadataCount,
            stopwatch.ElapsedMilliseconds);
    }

    private static Dictionary<string, LR2SongDB.folder> CreateExistingRowMap(IEnumerable<LR2SongDB.folder> existingRows)
    {
        var result = new Dictionary<string, LR2SongDB.folder>(PathComparer);
        foreach (LR2SongDB.folder row in existingRows ?? [])
        {
            string path = NormalizeFilePath(row?.path);
            if (string.IsNullOrWhiteSpace(path) || result.ContainsKey(path))
            {
                continue;
            }
            result.Add(path, row);
        }
        return result;
    }

    private static List<string> CreateDeletePaths(
        IEnumerable<LR2SongDB.folder> existingRows,
        IReadOnlyDictionary<string, string> generatedPathsByKey,
        IEnumerable<string> scopeDirectories,
        IEnumerable<string> scopePaths)
    {
        List<string> directories = [.. (scopeDirectories ?? [])
            .Select(Lr2FolderPath.NormalizeDirectoryPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(PathComparer)];
        var explicitPaths = new HashSet<string>(
            (scopePaths ?? []).Select(NormalizeFilePath).Where(path => !string.IsNullOrWhiteSpace(path)),
            PathComparer);
        var deletePaths = new List<string>();

        foreach (LR2SongDB.folder row in existingRows ?? [])
        {
            string path = NormalizeFilePath(row?.path);
            if (string.IsNullOrWhiteSpace(path)
                || !IsLr2FolderPath(path)
                || IsNormalDirectoryRow(row))
            {
                continue;
            }

            if (generatedPathsByKey != null
                && generatedPathsByKey.TryGetValue(path, out string generatedPath)
                && string.Equals(path, generatedPath, StringComparison.Ordinal))
            {
                continue;
            }

            if ((generatedPathsByKey != null
                    && generatedPathsByKey.ContainsKey(path))
                || explicitPaths.Contains(path)
                || directories.Any(directory => Lr2FolderPath.IsSameOrDescendant(path, directory)))
            {
                deletePaths.Add(row.path);
            }
        }

        return deletePaths;
    }

    private static bool AreEquivalent(LR2SongDB.folder expected, LR2SongDB.folder existing)
    {
        if (expected == null || existing == null)
        {
            return false;
        }

        return string.Equals(expected.path, existing.path, StringComparison.Ordinal)
            && string.Equals(expected.title, existing.title, StringComparison.Ordinal)
            && string.Equals(expected.subtitle, existing.subtitle, StringComparison.Ordinal)
            && string.Equals(expected.category, existing.category, StringComparison.Ordinal)
            && string.Equals(expected.info_a, existing.info_a, StringComparison.Ordinal)
            && string.Equals(expected.info_b, existing.info_b, StringComparison.Ordinal)
            && string.Equals(expected.command, existing.command, StringComparison.Ordinal)
            && expected.type == existing.type
            && string.Equals(expected.banner, existing.banner, StringComparison.Ordinal)
            && string.Equals(expected.parent, existing.parent, StringComparison.Ordinal)
            && expected.date == existing.date
            && expected.max == existing.max
            && expected.adddate == existing.adddate;
    }

    private static bool IsNormalDirectoryRow(LR2SongDB.folder row)
    {
        return row?.type == 1;
    }

    private static bool IsLr2FolderPath(string path)
    {
        return string.Equals(Path.GetExtension(path ?? string.Empty), ".lr2folder", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeFilePath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(filePath);
        }
        catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
        {
            return null;
        }
    }
}
