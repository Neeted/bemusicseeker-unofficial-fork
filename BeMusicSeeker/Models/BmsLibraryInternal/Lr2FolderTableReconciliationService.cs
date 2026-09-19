using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using Ribbit.Util.Extensions;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal enum Lr2FolderTableProjectionSourceKind
{
    NormalDirectory = 1,
    FolderInfoDirectory = 2,
    Lr2FolderFile = 3,
    BuiltinCustomFolder = 4
}

/// <summary>
/// Describes the complete physical/input surface used by one LR2 folder
/// reconciliation.  The request is deliberately independent of the database;
/// all source parsing and validation happens before an existing row is read.
/// </summary>
internal sealed class Lr2FolderTableProjection
{
    internal Lr2FolderTableProjection(
        IReadOnlyList<LR2SongDB.folder> rows,
        IReadOnlyDictionary<string, Lr2FolderTableProjectionSourceKind> sourceKinds)
    {
        Rows = rows ?? [];
        SourceKinds = sourceKinds ?? new Dictionary<string, Lr2FolderTableProjectionSourceKind>(StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<LR2SongDB.folder> Rows { get; }

    public IReadOnlyDictionary<string, Lr2FolderTableProjectionSourceKind> SourceKinds { get; }

    public bool IsComplete => true;
}

/// <summary>
/// Reports the single whole-table folder write performed after projection
/// preflight.  No folder-row mutation occurs when projection construction
/// throws.
/// </summary>
internal sealed class Lr2FolderTableReconciliationResult
{
    internal Lr2FolderTableReconciliationResult(
        Lr2FolderTableProjection projection,
        int existingRowCount,
        Lr2FolderGenerationWriteResult writeResult)
    {
        Projection = projection ?? throw new ArgumentNullException(nameof(projection));
        ExistingRowCount = existingRowCount;
        WriteResult = writeResult;
    }

    public Lr2FolderTableProjection Projection { get; }

    public int ExistingRowCount { get; }

    public int GeneratedCount => Projection.Rows.Count;

    public int UpsertedCount => WriteResult.UpsertedCount;

    public int DeletedCount => WriteResult.DeletedCount;

    public bool HasChanges => WriteResult.HasChanges;

    internal Lr2FolderGenerationWriteResult WriteResult { get; }
}

[Serializable]
internal sealed class Lr2FolderTableProjectionConflictException : InvalidOperationException
{
    internal Lr2FolderTableProjectionConflictException()
    {
    }

    internal Lr2FolderTableProjectionConflictException(string message)
        : base(message)
    {
    }

    internal Lr2FolderTableProjectionConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    [System.Obsolete(DiagnosticId = "SYSLIB0051")]
    private Lr2FolderTableProjectionConflictException(
        System.Runtime.Serialization.SerializationInfo info,
        System.Runtime.Serialization.StreamingContext context)
        : base(info, context)
    {
    }
}

[Serializable]
internal sealed class Lr2FolderTableProjectionIncompleteException : InvalidOperationException
{
    internal Lr2FolderTableProjectionIncompleteException()
    {
    }

    internal Lr2FolderTableProjectionIncompleteException(string message)
        : base(message)
    {
    }

    internal Lr2FolderTableProjectionIncompleteException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    [System.Obsolete(DiagnosticId = "SYSLIB0051")]
    private Lr2FolderTableProjectionIncompleteException(
        System.Runtime.Serialization.SerializationInfo info,
        System.Runtime.Serialization.StreamingContext context)
        : base(info, context)
    {
    }
}

/// <summary>
/// Builds and applies the app-owned LR2 folder cache as one deterministic
/// projection.  Incremental playlist/settings synchronization deliberately
/// continues to use <see cref="Lr2FolderFileDbSyncService"/>.
/// </summary>
internal static class Lr2FolderTableReconciliationService
{
    private const int NormalPriority = 1;
    private const int FolderInfoPriority = 2;
    private const int Lr2FolderPriority = 3;
    private const int BuiltinPriority = 4;

    private static readonly IEqualityComparer<string> PathComparer = StringComparer.OrdinalIgnoreCase;

    /// <summary>
    /// Creates the complete folder projection without opening or mutating the
    /// database.  This is the preflight boundary for the full sync route.
    /// </summary>
    internal static Lr2FolderTableProjection BuildProjection(Lr2SongDbSyncRequest request)
    {
        request ??= new Lr2SongDbSyncRequest();
        if (!request.Lr2FolderFileDiscoveryComplete)
        {
            throw new Lr2FolderTableProjectionIncompleteException(
                "LR2 folder file discovery did not produce a complete surface.");
        }

        List<string> roots = NormalizeDirectories(request.RootDirectories);
        List<string> chartPaths = NormalizePaths(request.ChartPaths);
        List<string> normalDirectoryPaths = NormalizeDirectories(request.NormalFolderDirectoryPaths);
        if (normalDirectoryPaths.Count == 0 && roots.Count > 0)
        {
            normalDirectoryPaths = [.. Lr2NormalFolderDbSyncService.CreateDirectoryMetadataTargets(roots, chartPaths)];
        }

        List<string> lr2FolderPaths = NormalizePaths(
            (request.Lr2FolderFilePaths ?? [])
                .Concat(request.Lr2FolderFileEntries?.Keys ?? []));
        Lr2SongDbSyncService.Lr2FolderFileSyncItemsResult fileItemsResult =
            Lr2SongDbSyncService.CreateLr2FolderFileSyncItems(
                lr2FolderPaths,
                request,
                request.Lr2FolderFileEntries);
        List<Lr2FolderFileSyncItem> fileItems = [.. fileItemsResult.Items];
        if (fileItemsResult.HasReadFailures)
        {
            throw new Lr2FolderTableProjectionIncompleteException(
                "One or more .lr2folder files could not be read during full preflight.");
        }

        foreach (Lr2FolderFileSyncItem item in fileItems)
        {
            if (item?.LastWriteTimeUtc == null)
            {
                throw new Lr2FolderTableProjectionIncompleteException(
                    "A .lr2folder candidate has no committed timestamp: " + (item?.FilePath ?? "<unknown>"));
            }
        }

        IReadOnlyCollection<ParentTarget> parentTargets = CreateParentTargets(fileItems, request);
        var metadataTargets = new HashSet<string>(PathComparer);
        foreach (string root in roots)
        {
            metadataTargets.Add(root);
        }
        foreach (string directory in normalDirectoryPaths)
        {
            if (roots.Count == 0 || IsUnderAnyRoot(directory, roots))
            {
                metadataTargets.Add(directory);
            }
        }
        foreach (string folderInfoPath in NormalizePaths(request.FolderInfoFilePaths))
        {
            string directory = NormalizeDirectoryPath(Lr2FolderPath.SafeGetDirectoryName(folderInfoPath));
            if (!string.IsNullOrWhiteSpace(directory)
                && (roots.Count == 0 || IsUnderAnyRoot(directory, roots)))
            {
                metadataTargets.Add(directory);
            }
        }
        foreach (ParentTarget target in parentTargets)
        {
            if (!string.IsNullOrWhiteSpace(target.PhysicalDirectory))
            {
                metadataTargets.Add(target.PhysicalDirectory);
            }
        }

        IReadOnlyDictionary<string, RootFileEnumerationEntry> directoryEntries =
            NormalizeDirectoryEntries(request.DirectoryEntries);
        Lr2FolderDirectoryMetadataSnapshot metadata = Lr2FolderDirectoryMetadataBuilder.Build(
            new Lr2FolderDirectoryMetadataBuildRequest
            {
                DirectoryPaths = [.. metadataTargets.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)],
                FolderInfoFilePaths = request.FolderInfoFilePaths,
                FolderInfoFileEntries = request.FolderInfoFileEntries,
                DirectoryLastWriteTimeUtcResolver = directory => ResolveDirectoryTimestamp(
                    directory,
                    directoryEntries,
                    request.DirectoryLastWriteTimeUtcResolver),
                FolderInfoLinesReader = request.FolderInfoLinesReader
            });
        if (metadata.MissingDirectoryCount > 0)
        {
            throw new Lr2FolderTableProjectionIncompleteException(
                "Full folder preflight could not resolve " + metadata.MissingDirectoryCount + " directory timestamp(s).");
        }
        if (metadata.FolderInfoReadFailureCount > 0)
        {
            throw new Lr2FolderTableProjectionIncompleteException(
                "Full folder preflight could not read " + metadata.FolderInfoReadFailureCount + " folderinfo file(s).");
        }

        Lr2FolderGenerationResult normalGeneration = Lr2FolderRowGenerator.GenerateNormalDirectoryRows(
            new Lr2FolderGenerationRequest
            {
                RootDirectories = roots,
                ChartPaths = chartPaths,
                DirectoryPaths = [.. metadataTargets
                    .Where(path => roots.Count == 0 || IsUnderAnyRoot(path, roots))
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)],
                DirectoryMetadataResolver = metadata.Resolve,
                GeneratedAtUtc = request.StartedAtUtc
            });
        if (normalGeneration.SkippedUnsupportedPathCount > 0
            || normalGeneration.SkippedMissingMetadataCount > 0)
        {
            throw new Lr2FolderTableProjectionIncompleteException(
                "Full folder preflight skipped a normal directory projection.");
        }

        var candidates = new List<ProjectionCandidate>();
        foreach (LR2SongDB.folder row in normalGeneration.Rows)
        {
            if (!normalGeneration.SourceKinds.TryGetValue(row.path, out Lr2FolderRowSourceKind sourceKind))
            {
                sourceKind = Lr2FolderRowSourceKind.NormalDirectory;
            }
            candidates.Add(new ProjectionCandidate(
                row,
                sourceKind == Lr2FolderRowSourceKind.FolderInfoDirectory
                    ? FolderInfoPriority
                    : NormalPriority,
                sourceKind == Lr2FolderRowSourceKind.FolderInfoDirectory
                    ? Lr2FolderTableProjectionSourceKind.FolderInfoDirectory
                    : Lr2FolderTableProjectionSourceKind.NormalDirectory,
                "directory:" + row.path));
        }

        foreach (Lr2FolderFileSyncItem item in fileItems)
        {
            if (!Lr2FolderFileProjection.TryCreateFolderRow(
                    new Lr2FolderFileRowRequest
                    {
                        FilePath = item.FilePath,
                        DatabasePath = item.DatabasePath,
                        Definition = item.Definition,
                        LastWriteTimeUtc = item.LastWriteTimeUtc,
                        GeneratedAtUtc = request.StartedAtUtc,
                        FolderType = item.FolderType,
                        ParentHash = item.ParentHash
                    },
                    out LR2SongDB.folder row))
            {
                throw new Lr2FolderTableProjectionIncompleteException(
                    "Full folder preflight could not project .lr2folder: " + (item.FilePath ?? "<unknown>"));
            }

            Lr2FolderFileSourceClassification classification = Lr2FolderFileSourceClassifier.Classify(
                new Lr2FolderFileSourceClassificationRequest
                {
                    FilePath = item.FilePath,
                    Lr2RootPath = request.Lr2RootPath,
                    RootCustomFolderOutputBaseDir = request.Lr2RootCustomFolderOutputBaseDir,
                    BuiltinSourceDirectories = request.Lr2BuiltinFolderSourceDirectories
                });
            bool isBuiltin = classification.IsBuiltinSource;
            Lr2FolderTableProjectionSourceKind sourceKind = isBuiltin
                ? Lr2FolderTableProjectionSourceKind.BuiltinCustomFolder
                : Lr2FolderTableProjectionSourceKind.Lr2FolderFile;
            candidates.Add(new ProjectionCandidate(
                row,
                isBuiltin ? BuiltinPriority : Lr2FolderPriority,
                sourceKind,
                (isBuiltin ? "builtin:" : "lr2folder:") + (item.FilePath ?? string.Empty)));
        }

        foreach (ParentTarget target in parentTargets)
        {
            if (!TryCreateParentRow(target, metadata, request, out LR2SongDB.folder row))
            {
                throw new Lr2FolderTableProjectionIncompleteException(
                    "Full folder preflight could not project parent directory: " + target.PhysicalDirectory);
            }

            candidates.Add(new ProjectionCandidate(
                row,
                target.SourceKind == Lr2FolderTableProjectionSourceKind.BuiltinCustomFolder
                    ? BuiltinPriority
                    : Lr2FolderPriority,
                target.SourceKind,
                "parent:" + target.DatabaseDirectory));
        }

        Dictionary<string, ProjectionCandidate> selected = SelectCandidates(candidates);
        List<LR2SongDB.folder> rows = [.. selected.Values
            .OrderBy(candidate => candidate.Row.path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.Row.path, StringComparer.Ordinal)
            .Select(candidate => candidate.Row)];
        var sourceKinds = selected.Values.ToDictionary(
            candidate => candidate.Row.path,
            candidate => candidate.SourceKind,
            StringComparer.OrdinalIgnoreCase);
        return new Lr2FolderTableProjection(rows, sourceKinds);
    }

    /// <summary>
    /// Runs complete preflight, reads existing folder rows once, then applies
    /// one delete/upsert transaction for the app-generated folder table.
    /// The optional reporter is invoked as each projection row is merged with
    /// existing user columns, after the full projection count is known and
    /// before that transaction starts.
    /// </summary>
    internal static Lr2FolderTableReconciliationResult Reconcile(
        LR2SongDBExtended songDb,
        Lr2SongDbSyncRequest request,
        bool commitTransaction = true,
        Action<int, int, string> progressReporter = null)
    {
        if (songDb == null)
        {
            throw new ArgumentNullException(nameof(songDb));
        }

        Lr2FolderTableProjection projection = BuildProjection(request);
        // The full route owns the app-generated folder table.  Direct
        // callers may be synchronizing a newly-created song database where
        // this table has not yet been materialized.
        songDb.CreateTable<LR2SongDB.folder>();
        List<LR2SongDB.folder> existingRows = [.. songDb.Table<LR2SongDB.folder>()];
        var existingByPath = existingRows
            .Where(row => !string.IsNullOrWhiteSpace(row?.path))
            .GroupBy(row => row.path, PathComparer)
            .ToDictionary(group => group.Key, group => group
                .OrderBy(row => row.path, StringComparer.Ordinal)
                .First(), PathComparer);
        int total = projection.Rows.Count;
        var rows = new List<LR2SongDB.folder>(total);
        for (int index = 0; index < total; index++)
        {
            LR2SongDB.folder projectedRow = projection.Rows[index];
            LR2SongDB.folder preparedRow = existingByPath.TryGetValue(
                projectedRow.path,
                out LR2SongDB.folder existingRow)
                ? CopyWithAddDate(projectedRow, existingRow.adddate)
                : projectedRow;
            rows.Add(preparedRow);
            ReportProgress(progressReporter, index + 1, total, preparedRow?.path);
        }
        Lr2FolderGenerationWriteResult writeResult = Lr2FolderDbWriter.ReplaceAllRows(
            songDb,
            existingRows,
            rows,
            commitTransaction,
            request?.IsShutdownRequested);
        return new Lr2FolderTableReconciliationResult(
            new Lr2FolderTableProjection(rows, projection.SourceKinds),
            existingRows.Count,
            writeResult);
    }

    private static void ReportProgress(
        Action<int, int, string> progressReporter,
        int processed,
        int total,
        string currentPath)
    {
        if (progressReporter == null || total <= 0)
        {
            return;
        }

        try
        {
            progressReporter(processed, total, currentPath);
        }
        catch
        {
            // Progress observation must not affect the atomic folder apply.
        }
    }

    private static Dictionary<string, ProjectionCandidate> SelectCandidates(
        IEnumerable<ProjectionCandidate> candidates)
    {
        var groups = new Dictionary<string, List<ProjectionCandidate>>(PathComparer);
        foreach (ProjectionCandidate candidate in candidates ?? [])
        {
            string key = NormalizeRowKey(candidate?.Row?.path);
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new Lr2FolderTableProjectionIncompleteException(
                    "Full folder preflight produced a row without a database path.");
            }
            if (!groups.TryGetValue(key, out List<ProjectionCandidate> group))
            {
                group = [];
                groups.Add(key, group);
            }
            group.Add(candidate);
        }

        var selected = new Dictionary<string, ProjectionCandidate>(PathComparer);
        foreach (KeyValuePair<string, List<ProjectionCandidate>> group in groups)
        {
            int highestPriority = group.Value.Max(candidate => candidate.Priority);
            List<ProjectionCandidate> top = [.. group.Value.Where(candidate => candidate.Priority == highestPriority)];
            ProjectionCandidate first = top
                .OrderBy(candidate => candidate.Row.path, StringComparer.Ordinal)
                .ThenBy(candidate => candidate.Source, StringComparer.Ordinal)
                .First();
            foreach (ProjectionCandidate candidate in top)
            {
                if (!AreEquivalent(first.Row, candidate.Row))
                {
                    throw new Lr2FolderTableProjectionConflictException(
                        "Conflicting full folder projections for " + group.Key
                        + " at priority " + highestPriority + ".");
                }
            }
            selected[group.Key] = first;
        }
        return selected;
    }

    private static IReadOnlyCollection<ParentTarget> CreateParentTargets(
        IEnumerable<Lr2FolderFileSyncItem> items,
        Lr2SongDbSyncRequest request)
    {
        List<string> scopes = [.. Lr2SongDbSyncService.CreateLr2FolderDirectoryRowGenerationScopeDirectories(request)
            .Select(NormalizeScopePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))];
        var targets = new Dictionary<string, ParentTarget>(PathComparer);
        foreach (Lr2FolderFileSyncItem item in items ?? [])
        {
            string databasePath = NormalizeFilePath(item?.DatabasePath ?? item?.FilePath);
            string physicalDirectory = NormalizeDirectoryPath(Lr2FolderPath.SafeGetDirectoryName(item?.FilePath));
            string databaseDirectory = NormalizeParentDirectoryPath(databasePath);
            while (!string.IsNullOrWhiteSpace(databaseDirectory)
                && !string.IsNullOrWhiteSpace(physicalDirectory)
                && ContainsScope(databaseDirectory, scopes)
                && !ContainsScopeRoot(databaseDirectory, scopes))
            {
                bool knownRelative = IsKnownRelativeDirectory(databaseDirectory);
                if (!(knownRelative && string.Equals(
                        NormalizeScopePath(databaseDirectory),
                        @"LR2files\CustomFolder",
                        StringComparison.OrdinalIgnoreCase)))
                {
                    Lr2FolderFileSourceClassification classification = Lr2FolderFileSourceClassifier.Classify(
                        new Lr2FolderFileSourceClassificationRequest
                        {
                            FilePath = item.FilePath,
                            Lr2RootPath = request.Lr2RootPath,
                            RootCustomFolderOutputBaseDir = request.Lr2RootCustomFolderOutputBaseDir,
                            BuiltinSourceDirectories = request.Lr2BuiltinFolderSourceDirectories
                        });
                    targets[NormalizeScopePath(databaseDirectory)] = new ParentTarget(
                        databaseDirectory,
                        physicalDirectory,
                        classification.IsBuiltinSource
                            ? Lr2FolderTableProjectionSourceKind.BuiltinCustomFolder
                            : Lr2FolderTableProjectionSourceKind.Lr2FolderFile);
                }
                databaseDirectory = NormalizeParentDirectoryPath(databaseDirectory);
                physicalDirectory = NormalizeDirectoryPath(Lr2FolderPath.SafeGetDirectoryName(physicalDirectory));
            }
        }
        return [.. targets.Values
            .OrderBy(target => target.DatabaseDirectory, StringComparer.OrdinalIgnoreCase)
            .ThenBy(target => target.DatabaseDirectory, StringComparer.Ordinal)];
    }

    private static bool TryCreateParentRow(
        ParentTarget target,
        Lr2FolderDirectoryMetadataSnapshot metadata,
        Lr2SongDbSyncRequest request,
        out LR2SongDB.folder row)
    {
        row = null;
        if (target == null
            || string.IsNullOrWhiteSpace(target.DatabaseDirectory)
            || string.IsNullOrWhiteSpace(target.PhysicalDirectory))
        {
            return false;
        }

        bool knownRelative = IsKnownRelativeDirectory(target.DatabaseDirectory);
        if (knownRelative
            && string.Equals(
                NormalizeScopePath(target.DatabaseDirectory),
                @"LR2files\CustomFolder",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        string rowPath = knownRelative
            ? NormalizeScopePath(target.DatabaseDirectory) + Path.DirectorySeparatorChar
            : Lr2FolderPath.ToFolderPath(target.DatabaseDirectory);
        if (string.IsNullOrWhiteSpace(rowPath)
            || !Lr2CompatibilityEvaluator.TryGetCp932ByteCount(rowPath, out _)
            || !metadata.TryGetMetadata(target.PhysicalDirectory, out Lr2FolderDirectoryMetadata directoryMetadata)
            || directoryMetadata.LastWriteTimeUtc == null)
        {
            return false;
        }

        string parentHash = TryComputeParentHash(target.DatabaseDirectory, knownRelative);
        if (string.IsNullOrWhiteSpace(parentHash))
        {
            return false;
        }
        string title = directoryMetadata.HasFolderInfoTitle
            ? directoryMetadata.FolderInfoTitle
            : ResolveDirectoryTitle(target.DatabaseDirectory);
        // Custom-folder output bases are LR2 roots for the folder-table
        // hierarchy even when they are outside the configured BMS roots.  A
        // root-output child directory is likewise the first directory under
        // that LR2 root.  Keep this decision in the full projection so the
        // one-shot route does not fall back to hashing the physical parent.
        bool isRoot = !knownRelative
            && (IsRootEquivalentDirectory(target.DatabaseDirectory, request)
                || IsRootOutputBaseChild(target.DatabaseDirectory, request));
        row = new LR2SongDB.folder
        {
            title = title,
            path = rowPath,
            type = knownRelative ? 2 : 1,
            parent = isRoot ? Lr2SongFolderParentNormalizer.RootParentHash : parentHash,
            date = directoryMetadata.LastWriteTimeUtc.Value.ToUnixtime(),
            adddate = request.StartedAtUtc.ToUnixtime()
        };
        return true;
    }

    private static bool IsRootEquivalentDirectory(
        string directory,
        Lr2SongDbSyncRequest request)
    {
        string normalizedDirectory = NormalizeDirectoryPath(directory);
        if (string.IsNullOrWhiteSpace(normalizedDirectory))
        {
            return false;
        }

        if ((request?.RootDirectories ?? [])
            .Concat(request?.Lr2NormalCustomFolderOutputBaseDir == null
                ? []
                : [request.Lr2NormalCustomFolderOutputBaseDir])
            .Concat(request?.Lr2AdditionalNormalCustomFolderOutputBaseDirs ?? [])
            .Concat(request?.Lr2RootCustomFolderOutputBaseDir == null
                ? []
                : [request.Lr2RootCustomFolderOutputBaseDir])
            .Any(candidate => string.Equals(
                normalizedDirectory,
                NormalizeDirectoryPath(candidate),
                StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return false;
    }

    private static bool IsRootOutputBaseChild(
        string directory,
        Lr2SongDbSyncRequest request)
    {
        string rootOutputBase = NormalizeDirectoryPath(request?.Lr2RootCustomFolderOutputBaseDir);
        if (string.IsNullOrWhiteSpace(rootOutputBase))
        {
            return false;
        }

        string normalizedDirectory = NormalizeDirectoryPath(directory);
        string parentDirectory = NormalizeDirectoryPath(
            Lr2FolderPath.SafeGetDirectoryName(normalizedDirectory));
        return !string.IsNullOrWhiteSpace(normalizedDirectory)
            && string.Equals(parentDirectory, rootOutputBase, StringComparison.OrdinalIgnoreCase);
    }

    private static string TryComputeParentHash(string databaseDirectory, bool knownRelative)
    {
        try
        {
            string parentDirectory = Path.GetDirectoryName(
                databaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(parentDirectory))
            {
                return null;
            }
            if (knownRelative
                && string.Equals(
                    NormalizeScopePath(parentDirectory),
                    @"LR2files\CustomFolder",
                    StringComparison.OrdinalIgnoreCase))
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

    private static Lr2FolderDirectoryMetadata ResolveDirectoryMetadata(
        string directory,
        IReadOnlyDictionary<string, RootFileEnumerationEntry> entries,
        Func<string, DateTime?> resolver)
    {
        DateTime? timestamp = ResolveDirectoryTimestamp(directory, entries, resolver);
        return timestamp.HasValue ? new Lr2FolderDirectoryMetadata(timestamp) : null;
    }

    private static DateTime? ResolveDirectoryTimestamp(
        string directory,
        IReadOnlyDictionary<string, RootFileEnumerationEntry> entries,
        Func<string, DateTime?> resolver)
    {
        string normalized = NormalizeDirectoryPath(directory);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }
        if (entries?.TryGetValue(normalized, out RootFileEnumerationEntry entry) == true)
        {
            return entry?.LastWriteTimeUtc;
        }
        if (resolver != null)
        {
            try
            {
                return resolver(normalized);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                return null;
            }
        }
        try
        {
            return LongPathFileSystem.DirectoryExists(normalized)
                ? LongPathFileSystem.GetLastWriteTimeUtc(normalized, isDirectory: true)
                : null;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
        {
            return null;
        }
    }

    private static IReadOnlyDictionary<string, RootFileEnumerationEntry> NormalizeDirectoryEntries(
        IReadOnlyDictionary<string, RootFileEnumerationEntry> entries)
    {
        var result = new Dictionary<string, RootFileEnumerationEntry>(PathComparer);
        foreach (KeyValuePair<string, RootFileEnumerationEntry> pair in entries ?? new Dictionary<string, RootFileEnumerationEntry>(PathComparer))
        {
            string path = !string.IsNullOrWhiteSpace(pair.Value?.Path) ? pair.Value.Path : pair.Key;
            string normalized = NormalizeDirectoryPath(path);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }
            if (!result.TryGetValue(normalized, out RootFileEnumerationEntry existing)
                || existing?.LastWriteTimeUtc == null && pair.Value?.LastWriteTimeUtc != null)
            {
                result[normalized] = new RootFileEnumerationEntry(
                    normalized,
                    pair.Value?.LastWriteTimeUtc,
                    pair.Value?.FileSize);
            }
        }
        return result;
    }

    private static List<string> NormalizeDirectories(IEnumerable<string> paths)
    {
        return [.. (paths ?? [])
            .Select(NormalizeDirectoryPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(PathComparer)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(path => path, StringComparer.Ordinal)];
    }

    private static List<string> NormalizePaths(IEnumerable<string> paths)
    {
        return [.. (paths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.Trim())
            .Distinct(PathComparer)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(path => path, StringComparer.Ordinal)];
    }

    private static string NormalizeDirectoryPath(string path)
    {
        return Lr2FolderPath.NormalizeDirectoryPath(path);
    }

    private static string NormalizeFilePath(string path)
    {
        return Lr2FolderFileProjection.NormalizeDatabasePath(path);
    }

    private static string NormalizeRowKey(string path)
    {
        return NormalizeFilePath(path);
    }

    private static string NormalizeParentDirectoryPath(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            return null;
        }
        try
        {
            string normalized = databasePath.Trim()
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
                .TrimEnd(Path.DirectorySeparatorChar);
            string directory = Path.GetDirectoryName(normalized);
            if (string.IsNullOrWhiteSpace(directory))
            {
                return null;
            }
            return IsKnownRelativeDirectory(directory)
                ? NormalizeScopePath(directory)
                : NormalizeDirectoryPath(directory);
        }
        catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
        {
            return null;
        }
    }

    private static string NormalizeScopePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }
        string normalized = path.Trim()
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Path.IsPathRooted(normalized)
            ? NormalizeDirectoryPath(normalized)
            : normalized;
    }

    private static bool IsKnownRelativeDirectory(string path)
    {
        string normalized = NormalizeScopePath(path);
        return !string.IsNullOrWhiteSpace(normalized)
            && (string.Equals(normalized, @"LR2files\CustomFolder", StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith(@"LR2files\CustomFolder\", StringComparison.OrdinalIgnoreCase));
    }

    private static bool ContainsScope(string path, IEnumerable<string> scopes)
    {
        string normalizedPath = NormalizeScopePath(path);
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return false;
        }
        foreach (string scope in scopes ?? [])
        {
            string normalizedScope = NormalizeScopePath(scope);
            if (string.IsNullOrWhiteSpace(normalizedScope))
            {
                continue;
            }
            if (Path.IsPathRooted(normalizedPath) && Path.IsPathRooted(normalizedScope))
            {
                if (Lr2FolderPath.IsSameOrDescendantNormalized(normalizedPath, normalizedScope))
                {
                    return true;
                }
            }
            else if (string.Equals(normalizedPath, normalizedScope, StringComparison.OrdinalIgnoreCase)
                || normalizedPath.StartsWith(normalizedScope + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static bool ContainsScopeRoot(string path, IEnumerable<string> scopes)
    {
        string normalizedPath = NormalizeScopePath(path);
        return !string.IsNullOrWhiteSpace(normalizedPath)
            && (scopes ?? []).Any(scope => string.Equals(
                normalizedPath,
                NormalizeScopePath(scope),
                StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsUnderAnyRoot(string path, IEnumerable<string> roots)
    {
        return (roots ?? []).Any(root => Lr2FolderPath.IsSameOrDescendant(path, root));
    }

    private static string ResolveDirectoryTitle(string directory)
    {
        string normalized = NormalizeScopePath(directory);
        string trimmed = normalized?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) ?? directory;
        string title = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(title) ? trimmed : title;
    }

    private static LR2SongDB.folder CopyWithAddDate(LR2SongDB.folder row, int? adddate)
    {
        return new LR2SongDB.folder
        {
            title = row.title,
            subtitle = row.subtitle,
            category = row.category,
            info_a = row.info_a,
            info_b = row.info_b,
            command = row.command,
            path = row.path,
            type = row.type,
            banner = row.banner,
            parent = row.parent,
            date = row.date,
            max = row.max,
            adddate = adddate
        };
    }

    private static bool AreEquivalent(LR2SongDB.folder expected, LR2SongDB.folder actual)
    {
        return expected != null
            && actual != null
            && string.Equals(expected.path, actual.path, StringComparison.OrdinalIgnoreCase)
            && string.Equals(expected.title, actual.title, StringComparison.Ordinal)
            && string.Equals(expected.subtitle, actual.subtitle, StringComparison.Ordinal)
            && string.Equals(expected.category, actual.category, StringComparison.Ordinal)
            && string.Equals(expected.info_a, actual.info_a, StringComparison.Ordinal)
            && string.Equals(expected.info_b, actual.info_b, StringComparison.Ordinal)
            && string.Equals(expected.command, actual.command, StringComparison.Ordinal)
            && expected.type == actual.type
            && string.Equals(expected.banner, actual.banner, StringComparison.Ordinal)
            && string.Equals(expected.parent, actual.parent, StringComparison.Ordinal)
            && expected.date == actual.date
            && expected.max == actual.max
            && expected.adddate == actual.adddate;
    }

    private sealed class ProjectionCandidate(
        LR2SongDB.folder row,
        int priority,
        Lr2FolderTableProjectionSourceKind sourceKind,
        string source)
    {
        public LR2SongDB.folder Row { get; } = row;

        public int Priority { get; } = priority;

        public Lr2FolderTableProjectionSourceKind SourceKind { get; } = sourceKind;

        public string Source { get; } = source ?? string.Empty;
    }

    private sealed class ParentTarget(
        string databaseDirectory,
        string physicalDirectory,
        Lr2FolderTableProjectionSourceKind sourceKind)
    {
        public string DatabaseDirectory { get; } = databaseDirectory;

        public string PhysicalDirectory { get; } = physicalDirectory;

        public Lr2FolderTableProjectionSourceKind SourceKind { get; } = sourceKind;
    }
}
