using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using BeMusicSeeker.Models.LR2;
using Ribbit.Util.Extensions;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class Lr2FolderFileSyncItem
{
    public string FilePath { get; set; }

    public string DatabasePath { get; set; }

    public Lr2FolderFileDefinition Definition { get; set; }

    public DateTime? LastWriteTimeUtc { get; set; }

    public int FolderType { get; set; } = 2;

    public string ParentHash { get; set; }

    public bool PreserveExistingRowOnly { get; set; }
}

internal sealed class Lr2FolderFileDbSyncRequest
{
    public IReadOnlyCollection<Lr2FolderFileSyncItem> Items { get; set; } = [];

    public IReadOnlyCollection<string> ScopeDirectories { get; set; } = [];

    public IReadOnlyCollection<string> DirectoryRowScopeDirectories { get; set; } = [];

    public IReadOnlyCollection<string> DirectoryRowGenerationScopeDirectories { get; set; } = [];

    public IReadOnlyCollection<string> ScopePaths { get; set; } = [];

    public IReadOnlyCollection<string> PruneExcludedDirectories { get; set; } = [];

    public Func<string, Lr2FolderDirectoryMetadata> DirectoryMetadataResolver { get; set; }

    public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;

    public bool AllowPrune { get; set; }

    public bool ScopeReadLr2FolderRowsOnly { get; set; }

    public bool UpdateParentDirectoryRowsForPreservedItems { get; set; } = true;
}

internal sealed class Lr2FolderFileDbSyncResult(
    int itemCount,
    int existingReadCount,
    int generatedCount,
    int upsertedCount,
    int deletedCount,
    int preservedCount,
    int skippedUnsupportedPathCount,
    int skippedMissingMetadataCount,
    long elapsedMs)
{
    public int ItemCount { get; } = itemCount;

    public int ExistingReadCount { get; } = existingReadCount;

    public int GeneratedCount { get; } = generatedCount;

    public int UpsertedCount { get; } = upsertedCount;

    public int DeletedCount { get; } = deletedCount;

    public int PreservedCount { get; } = preservedCount;

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
        List<LR2SongDB.folder> existingRows = ReadExistingRows(songDb, request);
        Dictionary<string, LR2SongDB.folder> existingRowsByPath = CreateExistingRowMap(existingRows);
        var generatedPathsByKey = new Dictionary<string, string>(PathComparer);
        var generatedParentDirectoryKeys = new HashSet<string>(PathComparer);
        var upsertRows = new List<LR2SongDB.folder>();
        var replaceDeletePaths = new List<string>();
        int itemCount = 0;
        int generatedCount = 0;
        int preservedCount = 0;
        int skippedUnsupportedPathCount = 0;
        int skippedMissingMetadataCount = 0;
        IReadOnlyCollection<string> directoryRowGenerationScopeDirectories =
            request.DirectoryRowGenerationScopeDirectories?.Count > 0
                ? request.DirectoryRowGenerationScopeDirectories
                : request.DirectoryRowScopeDirectories;
        DirectoryScopeMatcher directoryRowGenerationScopeMatcher =
            DirectoryScopeMatcher.Create(directoryRowGenerationScopeDirectories);

        foreach (Lr2FolderFileSyncItem item in request.Items ?? [])
        {
            if (item == null)
            {
                continue;
            }

            itemCount++;
            string databasePath = NormalizeFilePath(item.DatabasePath ?? item.FilePath);
            if (string.IsNullOrWhiteSpace(databasePath))
            {
                skippedUnsupportedPathCount++;
                continue;
            }
            LR2SongDB.folder existingRow = ResolveExistingRow(item, existingRowsByPath, databasePath, out string replaceDeletePath);
            if (item.FolderType == 1 || IsNormalDirectoryRow(existingRow))
            {
                skippedUnsupportedPathCount++;
                continue;
            }
            if (item.PreserveExistingRowOnly && existingRow != null)
            {
                preservedCount++;
                generatedPathsByKey[databasePath] = databasePath;
                if (request.UpdateParentDirectoryRowsForPreservedItems)
                {
                    UpsertParentDirectoryRows(
                        item,
                        databasePath,
                        directoryRowGenerationScopeMatcher,
                        existingRowsByPath,
                        request.GeneratedAtUtc,
                        request.DirectoryMetadataResolver,
                        generatedPathsByKey,
                        generatedParentDirectoryKeys,
                        upsertRows);
                }
                continue;
            }

            bool created = Lr2FolderFileProjection.TryCreateFolderRow(new Lr2FolderFileRowRequest
            {
                FilePath = item.FilePath,
                DatabasePath = databasePath,
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
            generatedPathsByKey[databasePath] = databasePath;
            if (!string.IsNullOrWhiteSpace(replaceDeletePath)
                && !string.Equals(replaceDeletePath, row.path, StringComparison.Ordinal))
            {
                replaceDeletePaths.Add(replaceDeletePath);
            }
            if (!AreEquivalent(row, existingRow))
            {
                upsertRows.Add(row);
            }

            UpsertParentDirectoryRows(
                item,
                databasePath,
                directoryRowGenerationScopeMatcher,
                existingRowsByPath,
                request.GeneratedAtUtc,
                request.DirectoryMetadataResolver,
                generatedPathsByKey,
                generatedParentDirectoryKeys,
                upsertRows);
        }

        List<string> deletePaths = [.. replaceDeletePaths];
        if (request.AllowPrune)
        {
            deletePaths.AddRange(CreateDeletePaths(
                existingRows,
                generatedPathsByKey,
                request.ScopeDirectories,
                request.DirectoryRowScopeDirectories,
                request.ScopePaths,
                request.PruneExcludedDirectories));
        }
        deletePaths = [.. deletePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(PathComparer)];

        Lr2FolderGenerationWriteResult writeResult = Lr2FolderDbWriter.ApplySyncPlan(
            songDb,
            new Lr2FolderGenerationSyncPlan(upsertRows, deletePaths));

        stopwatch.Stop();
        return new Lr2FolderFileDbSyncResult(
            itemCount,
            existingRows.Count,
            generatedCount,
            writeResult.UpsertedCount,
            writeResult.DeletedCount,
            preservedCount,
            skippedUnsupportedPathCount,
            skippedMissingMetadataCount,
            stopwatch.ElapsedMilliseconds);
    }

    private static List<LR2SongDB.folder> ReadExistingRows(
        LR2SongDBExtended songDb,
        Lr2FolderFileDbSyncRequest request)
    {
        var rowsByPath = new Dictionary<string, LR2SongDB.folder>(StringComparer.Ordinal);
        foreach (LR2SongDB.folder row in QueryExistingRowsByExactPaths(
            songDb,
            CreateExistingRowExactPathScope(request)))
        {
            AddExistingRow(rowsByPath, row);
        }

        if (request?.AllowPrune == true)
        {
            foreach (LR2SongDB.folder row in QueryExistingRowsByScopeDirectories(
                songDb,
                CreateExistingRowScopeDirectories(request),
                request.PruneExcludedDirectories,
                request.ScopeReadLr2FolderRowsOnly))
            {
                AddExistingRow(rowsByPath, row);
            }
        }

        return [.. rowsByPath.Values];
    }

    private static IReadOnlyCollection<string> CreateExistingRowExactPathScope(Lr2FolderFileDbSyncRequest request)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (request == null)
        {
            return result;
        }

        DirectoryScopeMatcher directoryRowGenerationScopeMatcher =
            DirectoryScopeMatcher.Create(request.DirectoryRowGenerationScopeDirectories?.Count > 0
                ? request.DirectoryRowGenerationScopeDirectories
                : request.DirectoryRowScopeDirectories);
        foreach (Lr2FolderFileSyncItem item in request.Items ?? [])
        {
            AddExactFilePath(result, item?.DatabasePath);
            AddExactFilePath(result, item?.FilePath);
            string databasePath = NormalizeFilePath(item?.DatabasePath ?? item?.FilePath);
            if (string.IsNullOrWhiteSpace(databasePath))
            {
                continue;
            }
            if (item.PreserveExistingRowOnly && !request.UpdateParentDirectoryRowsForPreservedItems)
            {
                continue;
            }

            foreach (ParentDirectoryRowTarget parentTarget in CreateParentDirectoryRowTargets(
                item,
                databasePath,
                directoryRowGenerationScopeMatcher))
            {
                if (TryCreateDirectoryRowPath(parentTarget.DatabaseDirectory, out string rowPath, out _)
                    && !string.IsNullOrWhiteSpace(rowPath))
                {
                    result.Add(rowPath);
                }
            }
        }

        foreach (string path in request.ScopePaths ?? [])
        {
            AddExactFilePath(result, path);
        }

        return result;
    }

    private static IReadOnlyCollection<string> CreateExistingRowScopeDirectories(Lr2FolderFileDbSyncRequest request)
    {
        var result = new HashSet<string>(PathComparer);
        foreach (string directory in request?.ScopeDirectories ?? [])
        {
            AddScopeDirectory(result, directory);
        }
        foreach (string directory in request?.DirectoryRowScopeDirectories ?? [])
        {
            AddScopeDirectory(result, directory);
        }
        return result;
    }

    private static void AddExactFilePath(ISet<string> result, string path)
    {
        string normalized = NormalizeFilePath(path);
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            result?.Add(normalized);
        }
    }

    private static void AddScopeDirectory(ISet<string> result, string directory)
    {
        string normalized = NormalizeScopeDirectory(directory);
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            result?.Add(normalized);
        }
    }

    private static void UpsertParentDirectoryRows(
        Lr2FolderFileSyncItem item,
        string databasePath,
        DirectoryScopeMatcher directoryRowScopeMatcher,
        IReadOnlyDictionary<string, LR2SongDB.folder> existingRowsByPath,
        DateTime generatedAtUtc,
        Func<string, Lr2FolderDirectoryMetadata> directoryMetadataResolver,
        IDictionary<string, string> generatedPathsByKey,
        ISet<string> generatedParentDirectoryKeys,
        IList<LR2SongDB.folder> upsertRows)
    {
        foreach (ParentDirectoryRowTarget parentTarget in CreateParentDirectoryRowTargets(
            item,
            databasePath,
            directoryRowScopeMatcher))
        {
            if (!generatedParentDirectoryKeys.Add(parentTarget.DatabaseDirectory)
                || !TryCreateParentDirectoryRow(
                    parentTarget.DatabaseDirectory,
                    parentTarget.PhysicalDirectory,
                    directoryRowScopeMatcher,
                    existingRowsByPath,
                    generatedAtUtc,
                    directoryMetadataResolver,
                    out LR2SongDB.folder parentRow))
            {
                continue;
            }

            generatedPathsByKey[parentRow.path] = parentRow.path;
            LR2SongDB.folder existingParentRow = existingRowsByPath.TryGetValue(parentRow.path, out LR2SongDB.folder existingParent)
                ? existingParent
                : null;
            if (!AreEquivalent(parentRow, existingParentRow))
            {
                upsertRows.Add(parentRow);
            }
        }
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

    private static void AddExistingRow(IDictionary<string, LR2SongDB.folder> rowsByPath, LR2SongDB.folder row)
    {
        if (rowsByPath == null || string.IsNullOrWhiteSpace(row?.path))
        {
            return;
        }

        rowsByPath[row.path] = row;
    }

    private static IEnumerable<LR2SongDB.folder> QueryExistingRowsByExactPaths(
        LR2SongDBExtended songDb,
        IReadOnlyCollection<string> exactPaths)
    {
        if (songDb == null || exactPaths == null || exactPaths.Count == 0)
        {
            return [];
        }

        string[] paths = [.. exactPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.Ordinal)];
        return Lr2FolderExistingRowLookup.QueryExactPaths(songDb, paths);
    }

    private static IEnumerable<LR2SongDB.folder> QueryExistingRowsByScopeDirectories(
        LR2SongDBExtended songDb,
        IReadOnlyCollection<string> scopeDirectories,
        IReadOnlyCollection<string> excludedScopeDirectories,
        bool lr2FolderRowsOnly)
    {
        if (songDb == null || scopeDirectories == null || scopeDirectories.Count == 0)
        {
            return [];
        }

        var scopePaths = new List<string>();
        foreach (string scopeDirectory in scopeDirectories)
        {
            if (!TryCreateDirectoryRowPath(scopeDirectory, out string scopePath, out _)
                || string.IsNullOrWhiteSpace(scopePath))
            {
                continue;
            }

            scopePaths.Add(scopePath);
        }
        var excludedScopePaths = new List<string>();
        foreach (string excludedScopeDirectory in excludedScopeDirectories ?? [])
        {
            if (!TryCreateDirectoryRowPath(excludedScopeDirectory, out string excludedScopePath, out _)
                || string.IsNullOrWhiteSpace(excludedScopePath))
            {
                continue;
            }

            excludedScopePaths.Add(excludedScopePath);
        }
        return lr2FolderRowsOnly
            ? Lr2FolderExistingRowLookup.QueryLr2FolderPathPrefixScopes(songDb, scopePaths, excludedScopePaths)
            : Lr2FolderExistingRowLookup.QueryPathPrefixScopes(songDb, scopePaths, excludedScopePaths);
    }

    private static LR2SongDB.folder ResolveExistingRow(
        Lr2FolderFileSyncItem item,
        IReadOnlyDictionary<string, LR2SongDB.folder> existingRowsByPath,
        string databasePath,
        out string replaceDeletePath)
    {
        replaceDeletePath = null;
        if (existingRowsByPath == null || string.IsNullOrWhiteSpace(databasePath))
        {
            return null;
        }

        if (existingRowsByPath.TryGetValue(databasePath, out LR2SongDB.folder row))
        {
            return row;
        }

        string physicalPath = NormalizeFilePath(item?.FilePath);
        if (string.IsNullOrWhiteSpace(physicalPath)
            || string.Equals(physicalPath, databasePath, StringComparison.OrdinalIgnoreCase)
            || !existingRowsByPath.TryGetValue(physicalPath, out row))
        {
            return null;
        }

        replaceDeletePath = row.path;
        return row;
    }

    private static List<string> CreateDeletePaths(
        IEnumerable<LR2SongDB.folder> existingRows,
        IReadOnlyDictionary<string, string> generatedPathsByKey,
        IEnumerable<string> scopeDirectories,
        IEnumerable<string> directoryRowScopeDirectories,
        IEnumerable<string> scopePaths,
        IEnumerable<string> pruneExcludedDirectories)
    {
        DirectoryScopeMatcher scopeMatcher = DirectoryScopeMatcher.Create(scopeDirectories);
        DirectoryScopeMatcher directoryRowScopeMatcher = DirectoryScopeMatcher.Create(directoryRowScopeDirectories);
        DirectoryScopeMatcher pruneExcludeMatcher = DirectoryScopeMatcher.Create(pruneExcludedDirectories);
        var explicitPaths = new HashSet<string>(
            (scopePaths ?? []).Select(NormalizeFilePath).Where(path => !string.IsNullOrWhiteSpace(path)),
            PathComparer);
        var deletePaths = new List<string>();

        foreach (LR2SongDB.folder row in existingRows ?? [])
        {
            string path = NormalizeFilePath(row?.path);
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }
            if (pruneExcludeMatcher.Contains(path))
            {
                continue;
            }

            bool isLr2FolderPath = IsLr2FolderPath(path);
            bool isDirectoryRowScopePath = directoryRowScopeMatcher.Contains(path);
            if (!isLr2FolderPath && !isDirectoryRowScopePath)
            {
                continue;
            }
            if (IsNormalDirectoryRow(row) && !isDirectoryRowScopePath)
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
                || scopeMatcher.Contains(path)
                || isDirectoryRowScopePath)
            {
                deletePaths.Add(row.path);
            }
        }

        return deletePaths;
    }

    internal static IReadOnlyCollection<string> CreateParentDirectoryMetadataTargets(
        IEnumerable<Lr2FolderFileSyncItem> items,
        IEnumerable<string> directoryRowGenerationScopeDirectories)
    {
        DirectoryScopeMatcher directoryRowGenerationScopeMatcher =
            DirectoryScopeMatcher.Create(directoryRowGenerationScopeDirectories);
        var result = new HashSet<string>(PathComparer);
        foreach (Lr2FolderFileSyncItem item in items ?? [])
        {
            if (item == null)
            {
                continue;
            }

            string databasePath = NormalizeFilePath(item.DatabasePath ?? item.FilePath);
            if (string.IsNullOrWhiteSpace(databasePath))
            {
                continue;
            }

            foreach (ParentDirectoryRowTarget target in CreateParentDirectoryRowTargets(
                item,
                databasePath,
                directoryRowGenerationScopeMatcher))
            {
                if (!string.IsNullOrWhiteSpace(target.PhysicalDirectory))
                {
                    result.Add(target.PhysicalDirectory);
                }
            }
        }

        return [.. result.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];
    }

    private static bool TryCreateParentDirectoryRow(
        string databaseDirectory,
        string physicalDirectory,
        DirectoryScopeMatcher directoryRowScopeMatcher,
        IReadOnlyDictionary<string, LR2SongDB.folder> existingRowsByPath,
        DateTime generatedAtUtc,
        Func<string, Lr2FolderDirectoryMetadata> directoryMetadataResolver,
        out LR2SongDB.folder row)
    {
        row = null;
        if (string.IsNullOrWhiteSpace(databaseDirectory)
            || string.IsNullOrWhiteSpace(physicalDirectory))
        {
            return false;
        }

        if (!TryCreateDirectoryRowPath(databaseDirectory, out string rowPath, out bool isKnownRelativeLr2Directory)
            || string.IsNullOrWhiteSpace(rowPath))
        {
            return false;
        }

        if (isKnownRelativeLr2Directory
            && IsKnownRelativeLr2FolderDirectoryRoot(databaseDirectory))
        {
            return false;
        }

        Lr2FolderDirectoryMetadata metadata = ResolveDirectoryMetadata(physicalDirectory, directoryMetadataResolver);
        DateTime? lastWriteTimeUtc = metadata?.LastWriteTimeUtc;
        if (!lastWriteTimeUtc.HasValue)
        {
            return false;
        }

        string parentHash = TryComputeParentDirectoryHash(databaseDirectory, isKnownRelativeLr2Directory, directoryRowScopeMatcher);
        if (string.IsNullOrWhiteSpace(parentHash))
        {
            return false;
        }

        if (!Lr2CompatibilityEvaluator.TryGetCp932ByteCount(rowPath, out _))
        {
            return false;
        }

        LR2SongDB.folder existingRow = null;
        existingRowsByPath?.TryGetValue(rowPath, out existingRow);
        row = new LR2SongDB.folder
        {
            title = metadata?.HasFolderInfoTitle == true
                ? metadata.FolderInfoTitle
                : ResolveDirectoryTitle(databaseDirectory),
            path = rowPath,
            type = isKnownRelativeLr2Directory ? 2 : 1,
            parent = parentHash,
            date = lastWriteTimeUtc.Value.ToUnixtime(),
            adddate = existingRow?.adddate ?? generatedAtUtc.ToUnixtime()
        };
        return true;
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
            return Lr2FolderFileProjection.NormalizeDatabasePath(filePath);
        }
        catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
        {
            return null;
        }
    }

    private static string NormalizeScopeDirectory(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return null;
        }

        string trimmed = directoryPath.Trim();
        if (Path.IsPathRooted(trimmed))
        {
            return Lr2FolderPath.NormalizeDirectoryPath(directoryPath);
        }

        string normalized = trimmed
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .TrimEnd(Path.DirectorySeparatorChar);
        if (IsKnownRelativeLr2FolderDirectory(normalized))
        {
            return normalized;
        }
        return null;
    }

    private sealed class DirectoryScopeMatcher
    {
        private static readonly DirectoryScopeMatcher Empty = new([]);

        private readonly List<string> scopes;

        private readonly HashSet<string> roots;

        private DirectoryScopeMatcher(List<string> scopes)
        {
            this.scopes = scopes ?? [];
            roots = new HashSet<string>(this.scopes, PathComparer);
        }

        public static DirectoryScopeMatcher Create(IEnumerable<string> scopeDirectories)
        {
            List<string> normalizedScopes = [.. (scopeDirectories ?? [])
                .Select(NormalizeScopeDirectory)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(PathComparer)
                .OrderBy(path => path.Length)];
            return normalizedScopes.Count == 0
                ? Empty
                : new DirectoryScopeMatcher(normalizedScopes);
        }

        public bool Contains(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || scopes.Count == 0)
            {
                return false;
            }

            string normalizedPath = NormalizePathForScopeComparison(path);
            if (string.IsNullOrWhiteSpace(normalizedPath))
            {
                return false;
            }

            string current = normalizedPath;
            while (!string.IsNullOrWhiteSpace(current))
            {
                if (roots.Contains(current))
                {
                    return true;
                }
                string parent = GetParentPathForScopeComparison(current);
                if (string.IsNullOrWhiteSpace(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
                current = parent;
            }
            return false;
        }

        public bool ContainsRoot(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory) || roots.Count == 0)
            {
                return false;
            }

            string normalizedDirectory = NormalizeScopeDirectory(directory);
            return !string.IsNullOrWhiteSpace(normalizedDirectory) && roots.Contains(normalizedDirectory);
        }

        private static string NormalizePathForScopeComparison(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    return null;
                }
                if (Path.IsPathRooted(path))
                {
                    return Lr2FolderPath.NormalizeDirectoryPath(path);
                }
                return path.Trim()
                    .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
                    .TrimEnd(Path.DirectorySeparatorChar);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                return null;
            }
        }

        private static string GetParentPathForScopeComparison(string normalizedPath)
        {
            if (string.IsNullOrWhiteSpace(normalizedPath))
            {
                return null;
            }

            try
            {
                if (Path.IsPathRooted(normalizedPath))
                {
                    return Lr2FolderPath.NormalizeDirectoryPath(Path.GetDirectoryName(normalizedPath));
                }

                int separatorIndex = normalizedPath.LastIndexOf(Path.DirectorySeparatorChar);
                return separatorIndex <= 0
                    ? null
                    : normalizedPath.Substring(0, separatorIndex);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                return null;
            }
        }
    }

    private static bool IsKnownRelativeLr2FolderDirectory(string directoryPath)
    {
        return string.Equals(directoryPath, @"LR2files\CustomFolder", StringComparison.OrdinalIgnoreCase)
            || directoryPath.StartsWith(@"LR2files\CustomFolder\", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsKnownRelativeLr2FolderDirectoryRoot(string directoryPath)
    {
        string normalized = NormalizeKnownRelativeDirectory(directoryPath);
        return string.Equals(normalized, @"LR2files\CustomFolder", StringComparison.OrdinalIgnoreCase);
    }

    private static List<ParentDirectoryRowTarget> CreateParentDirectoryRowTargets(
        Lr2FolderFileSyncItem item,
        string databasePath,
        DirectoryScopeMatcher directoryRowScopeMatcher)
    {
        var targets = new List<ParentDirectoryRowTarget>();
        string databaseDirectory = NormalizeParentDirectoryPath(databasePath);
        string physicalDirectory = NormalizePhysicalParentDirectoryPath(item?.FilePath);
        while (!string.IsNullOrWhiteSpace(databaseDirectory)
            && !string.IsNullOrWhiteSpace(physicalDirectory)
            && directoryRowScopeMatcher.Contains(databaseDirectory)
            && !directoryRowScopeMatcher.ContainsRoot(databaseDirectory))
        {
            targets.Add(new ParentDirectoryRowTarget(databaseDirectory, physicalDirectory));
            databaseDirectory = NormalizeParentDirectoryPath(databaseDirectory);
            physicalDirectory = NormalizeParentDirectoryPath(physicalDirectory);
        }
        targets.Reverse();
        return targets;
    }

    private sealed class ParentDirectoryRowTarget(string databaseDirectory, string physicalDirectory)
    {
        public string DatabaseDirectory { get; } = databaseDirectory;

        public string PhysicalDirectory { get; } = physicalDirectory;
    }

    private static string NormalizeParentDirectoryPath(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            return null;
        }

        try
        {
            string normalizedPath = databasePath.Trim()
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
                .TrimEnd(Path.DirectorySeparatorChar);
            string directory = Path.GetDirectoryName(normalizedPath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                return null;
            }

            string relativeDirectory = NormalizeKnownRelativeDirectory(directory);
            return relativeDirectory ?? Lr2FolderPath.NormalizeDirectoryPath(directory);
        }
        catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
        {
            return null;
        }
    }

    private static string NormalizePhysicalParentDirectoryPath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        try
        {
            string directory = Path.GetDirectoryName(filePath);
            return Lr2FolderPath.NormalizeDirectoryPath(directory);
        }
        catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
        {
            return null;
        }
    }

    private static bool TryCreateDirectoryRowPath(
        string directory,
        out string rowPath,
        out bool isKnownRelativeLr2Directory)
    {
        rowPath = null;
        isKnownRelativeLr2Directory = false;
        string relativeDirectory = NormalizeKnownRelativeDirectory(directory);
        if (!string.IsNullOrWhiteSpace(relativeDirectory))
        {
            rowPath = relativeDirectory + Path.DirectorySeparatorChar;
            isKnownRelativeLr2Directory = true;
            return true;
        }

        rowPath = Lr2FolderPath.ToFolderPath(directory);
        return !string.IsNullOrWhiteSpace(rowPath);
    }

    private static string NormalizeKnownRelativeDirectory(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return null;
        }

        string normalized = directoryPath.Trim()
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .TrimEnd(Path.DirectorySeparatorChar);
        return IsKnownRelativeLr2FolderDirectory(normalized)
            ? normalized
            : null;
    }

    private static Lr2FolderDirectoryMetadata ResolveDirectoryMetadata(
        string directoryPath,
        Func<string, Lr2FolderDirectoryMetadata> resolver)
    {
        if (resolver == null || string.IsNullOrWhiteSpace(directoryPath))
        {
            return null;
        }

        try
        {
            return resolver(directoryPath);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
        {
            return null;
        }
    }

    private static string TryComputeParentDirectoryHash(
        string directory,
        bool isKnownRelativeLr2Directory,
        DirectoryScopeMatcher directoryRowScopeMatcher)
    {
        try
        {
            string parentDirectory = Path.GetDirectoryName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(parentDirectory))
            {
                return null;
            }

            if (directoryRowScopeMatcher.ContainsRoot(parentDirectory)
                || (isKnownRelativeLr2Directory && IsKnownRelativeLr2FolderDirectoryRoot(parentDirectory)))
            {
                return Lr2SongFolderParentNormalizer.RootParentHash;
            }

            return Lr2SongFolderParentNormalizer.ComputeDirectoryHash(parentDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException || ex is EncoderFallbackException)
        {
            return null;
        }
    }

    private static string ResolveDirectoryTitle(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        string trimmed = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string title = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(title) ? trimmed : title;
    }
}
