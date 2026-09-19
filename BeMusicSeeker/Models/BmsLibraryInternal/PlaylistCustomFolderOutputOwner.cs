using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// プレイリストの custom-folder projection とファイル materialization を所有します。
/// <para>
/// プレイリスト aggregate は定義生成、出力先 registry、LR2 row 同期を提供し、
/// この owner は immutable な projection から物理ファイルと同期項目を生成します。
/// </para>
/// </summary>
internal sealed class PlaylistCustomFolderOutputOwner
{
    private readonly Func<CustomFolderOutputSettingsSnapshot> settingsProvider;

    private readonly Func<BMSTable, CustomFolderOutputSettingsSnapshot, IReadOnlyList<CustomFolderDefinition>> definitionsProvider;

    private readonly Func<BMSTable, CustomFolderOutputSettingsSnapshot, string> outputDirectoryResolver;

    private readonly Func<IEnumerable<string>, string, CustomFolderOutputPhysicalSurface> physicalSurfaceResolver;

    private readonly Func<IReadOnlyCollection<CustomFolderOutputProjection>, CustomFolderOutputSettingsSnapshot, IReadOnlyCollection<string>> knownOutputDirectoriesProvider;

    private readonly Func<string, BMSTable, CustomFolderOutputSettingsSnapshot, IReadOnlyCollection<string>> directoryRowGenerationScopesProvider;

    private readonly Action<string> logPerformance;

    internal PlaylistCustomFolderOutputOwner(
        Func<CustomFolderOutputSettingsSnapshot> settingsProvider,
        Func<BMSTable, CustomFolderOutputSettingsSnapshot, IReadOnlyList<CustomFolderDefinition>> definitionsProvider,
        Func<BMSTable, CustomFolderOutputSettingsSnapshot, string> outputDirectoryResolver,
        Func<IEnumerable<string>, string, CustomFolderOutputPhysicalSurface> physicalSurfaceResolver,
        Func<IReadOnlyCollection<CustomFolderOutputProjection>, CustomFolderOutputSettingsSnapshot, IReadOnlyCollection<string>> knownOutputDirectoriesProvider,
        Func<string, BMSTable, CustomFolderOutputSettingsSnapshot, IReadOnlyCollection<string>> directoryRowGenerationScopesProvider,
        Action<string> logPerformance)
    {
        this.settingsProvider = settingsProvider ?? throw new ArgumentNullException(nameof(settingsProvider));
        this.definitionsProvider = definitionsProvider ?? throw new ArgumentNullException(nameof(definitionsProvider));
        this.outputDirectoryResolver = outputDirectoryResolver ?? throw new ArgumentNullException(nameof(outputDirectoryResolver));
        this.physicalSurfaceResolver = physicalSurfaceResolver ?? throw new ArgumentNullException(nameof(physicalSurfaceResolver));
        this.knownOutputDirectoriesProvider = knownOutputDirectoriesProvider ?? throw new ArgumentNullException(nameof(knownOutputDirectoriesProvider));
        this.directoryRowGenerationScopesProvider = directoryRowGenerationScopesProvider ?? throw new ArgumentNullException(nameof(directoryRowGenerationScopesProvider));
        this.logPerformance = logPerformance;
    }

    internal CustomFolderOutputProjection CreateLayoutProjection(
        BMSTable table,
        string outputDirectoryOverride = null,
        CustomFolderOutputSettingsSnapshot settings = null)
    {
        settings ??= GetSettings();
        string outputDirectory = string.IsNullOrWhiteSpace(outputDirectoryOverride)
            ? outputDirectoryResolver(table, settings)
            : outputDirectoryOverride;
        IReadOnlyList<string> relativeFilePaths = Lr2ManagedCustomFolderOutputLayout.CreateRelativeFilePaths(
            table,
            Lr2ManagedCustomFolderOutputLayout.CreateCountsFromLoadedTable(table),
            settings.EnableDownloadLr2IrScoreAndDetectUnsent);
        var files = new List<CustomFolderOutputFileProjection>();
        foreach (string relativeFilePath in relativeFilePaths ?? [])
        {
            if (string.IsNullOrWhiteSpace(relativeFilePath))
            {
                continue;
            }

            string relativeDirectory = NormalizeRelativeDirectory(Path.GetDirectoryName(relativeFilePath));
            string filePath = string.IsNullOrWhiteSpace(relativeDirectory)
                ? Path.Combine(outputDirectory, Path.GetFileName(relativeFilePath))
                : Path.Combine(outputDirectory, relativeDirectory, Path.GetFileName(relativeFilePath));
            files.Add(new CustomFolderOutputFileProjection
            {
                RelativeDirectory = relativeDirectory,
                FilePath = filePath,
                DatabasePath = ResolveDatabasePath(table, filePath, settings)
            });
        }

        return new CustomFolderOutputProjection
        {
            Table = table,
            Settings = settings,
            OutputDirectory = outputDirectory,
            OutputRowScopePaths = CreateOutputRowScopePaths(table, outputDirectory, settings),
            Files = files
        };
    }

    internal CustomFolderOutputProjection CreateProjection(
        BMSTable table,
        bool includeText = true,
        string outputDirectoryOverride = null,
        CustomFolderOutputSettingsSnapshot settings = null)
    {
        settings ??= GetSettings();
        string outputDirectory = string.IsNullOrWhiteSpace(outputDirectoryOverride)
            ? outputDirectoryResolver(table, settings)
            : outputDirectoryOverride;
        IReadOnlyList<CustomFolderDefinition> definitions = OrderDefinitions(definitionsProvider(table, settings));
        var files = new List<CustomFolderOutputFileProjection>();
        var nextFileIndexByDirectory = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (CustomFolderDefinition definition in definitions)
        {
            string relativeDirectory = NormalizeRelativeDirectory(definition.RelativeDirectory);
            nextFileIndexByDirectory.TryGetValue(relativeDirectory, out int index);
            nextFileIndexByDirectory[relativeDirectory] = index + 1;
            string filePath = Path.Combine(outputDirectory, relativeDirectory, $"{index:D4}.lr2folder");
            files.Add(new CustomFolderOutputFileProjection
            {
                Index = index,
                RelativeDirectory = relativeDirectory,
                Text = includeText ? definition.Text ?? string.Empty : null,
                Definition = definition.ParsedDefinition,
                FilePath = filePath,
                DatabasePath = ResolveDatabasePath(table, filePath, settings)
            });
        }

        return new CustomFolderOutputProjection
        {
            Table = table,
            Settings = settings,
            OutputDirectory = outputDirectory,
            OutputRowScopePaths = CreateOutputRowScopePaths(table, outputDirectory, settings),
            Files = files
        };
    }

    internal CustomFolderBatchMaterializationResult MaterializeBatch(
        IReadOnlyList<CustomFolderOutputProjection> projections,
        Action<int, int, string> progressCallback = null,
        string operation = null,
        string reason = null,
        CustomFolderOutputSettingsSnapshot settingsOverride = null)
    {
        var result = new CustomFolderBatchMaterializationResult();
        var shiftJis = Encoding.GetEncoding("shift_jis");
        IReadOnlyList<CustomFolderOutputProjection> projectionList = [.. (projections ?? [])
            .Where(projection => projection != null && !string.IsNullOrWhiteSpace(projection.OutputDirectory))];
        CustomFolderOutputSettingsSnapshot settings = settingsOverride
            ?? projectionList.Select(projection => projection.Settings).FirstOrDefault(snapshot => snapshot != null)
            ?? GetSettings();
        result.Settings = settings;
        AssignProtectedOutputDirectories(projectionList, settings);
        if (projectionList.Any(projection => projection.PhysicalSurface == null))
        {
            CustomFolderOutputPhysicalSurface physicalSurface = physicalSurfaceResolver(
                projectionList.Select(projection => projection.OutputDirectory),
                reason);
            foreach (CustomFolderOutputProjection projection in projectionList.Where(projection => projection.PhysicalSurface == null))
            {
                projection.PhysicalSurface = physicalSurface;
                if (physicalSurface?.DiscoveryComplete != true)
                {
                    MarkAllFilesForWrite(projection);
                }
            }
        }

        var batchExpectedFilePaths = new HashSet<string>(
            projectionList.SelectMany(projection => projection.Files ?? [])
                .Select(file => Lr2FolderPath.NormalizeDirectoryPath(file?.FilePath))
                .Where(path => !string.IsNullOrWhiteSpace(path)),
            StringComparer.OrdinalIgnoreCase);
        var batchOutputDirectories = new HashSet<string>(
            projectionList.Select(projection => Lr2FolderPath.NormalizeDirectoryPath(projection.OutputDirectory))
                .Where(directory => !string.IsNullOrWhiteSpace(directory)),
            StringComparer.OrdinalIgnoreCase);
        var batchOutputRowScopeDirectories = new HashSet<string>(
            projectionList.SelectMany(projection => projection.OutputRowScopePaths ?? [])
                .Where(directory => !string.IsNullOrWhiteSpace(directory)),
            StringComparer.OrdinalIgnoreCase);
        result.PruneExcludedDirectories.AddRange(projectionList
            .SelectMany(CreateProtectedOutputRowScopePaths)
            .Where(directory => !string.IsNullOrWhiteSpace(directory)
                && !batchOutputDirectories.Contains(directory)
                && !batchOutputRowScopeDirectories.Contains(directory))
            .Distinct(StringComparer.OrdinalIgnoreCase));

        for (int projectionIndex = 0; projectionIndex < projectionList.Count; projectionIndex++)
        {
            CustomFolderOutputProjection projection = projectionList[projectionIndex];
            int writtenBefore = result.WrittenFileCount;
            int unchangedBefore = result.UnchangedFileCount;
            int deletedBefore = result.DeletedFileCount;
            var projectionStopwatch = Stopwatch.StartNew();
            Log((operation ?? "playlist_custom_folder_output") + " materialize_projection_start"
                + " reason=" + (reason ?? "unknown")
                + " index=" + (projectionIndex + 1)
                + " total=" + projectionList.Count
                + " name=" + Quote(projection.Table?.name)
                + " outputDir=" + Quote(projection.OutputDirectory)
                + " fileCount=" + (projection.Files?.Count ?? 0));
            try
            {
                result.OutputDirectories.Add(projection.OutputDirectory);
                result.OutputRowScopeDirectories.AddRange(projection.OutputRowScopePaths ?? []);
                result.DirectoryRowGenerationScopeDirectories.AddRange(
                    directoryRowGenerationScopesProvider(
                        projection.OutputDirectory,
                        projection.Table,
                        projection.Settings ?? settings) ?? []);
                IReadOnlyList<CustomFolderOutputFileProjection> files = projection.Files ?? [];
                if (files.Count > 0)
                {
                    LongPathFileSystem.CreateDirectory(projection.OutputDirectory);
                }

                foreach (CustomFolderOutputFileProjection file in files)
                {
                    if (file == null || string.IsNullOrWhiteSpace(file.FilePath))
                    {
                        continue;
                    }

                    string text = file.Text ?? string.Empty;
                    RootFileEnumerationEntry physicalEntry = projection.PhysicalSurface?.DiscoveryComplete == true
                        ? projection.PhysicalSurface.Resolve(file.FilePath)
                        : null;
                    string fileDirectory = Path.GetDirectoryName(file.FilePath);
                    if (!string.IsNullOrWhiteSpace(fileDirectory))
                    {
                        LongPathFileSystem.CreateDirectory(fileDirectory);
                    }
                    bool forceWrite = projection.ForceWriteFilePaths.Contains(file.FilePath);
                    ExistingFileState existingFileState = ExistingFileState.Missing;
                    if (!forceWrite && physicalEntry?.LastWriteTimeUtc == null)
                    {
                        existingFileState = InspectExistingFile(file.FilePath, text, shiftJis);
                        if (existingFileState == ExistingFileState.Unverified)
                        {
                            result.UnverifiedFilePaths.Add(file.FilePath);
                            continue;
                        }
                    }

                    bool writeRequired = forceWrite
                        || (physicalEntry?.LastWriteTimeUtc == null
                            && existingFileState != ExistingFileState.Current);
                    if (writeRequired)
                    {
                        WriteAllText(file.FilePath, text, shiftJis);
                        physicalEntry = null;
                        result.WrittenFileCount++;
                    }
                    else
                    {
                        result.UnchangedFileCount++;
                    }

                    var syncItem = new Lr2FolderFileSyncItem
                    {
                        FilePath = file.FilePath,
                        DatabasePath = file.DatabasePath,
                        Definition = file.Definition ?? Lr2FolderFileProjection.ParseDefinition(ReadLines(text)),
                        LastWriteTimeUtc = physicalEntry?.LastWriteTimeUtc ?? LongPathFileSystem.GetLastWriteTimeUtc(file.FilePath, isDirectory: false)
                    };
                    ApplySourceClassification(syncItem, projection.Table, projection.Settings ?? settings);
                    result.SyncItems.Add(syncItem);
                }
                RemoveStaleManagedFiles(projection, batchExpectedFilePaths, result);
                if (files.Count == 0)
                {
                    result.EmptyOutputDirectories.Add(projection.OutputDirectory);
                    if (TryDeleteEmptyDirectory(projection.OutputDirectory, projection.ProtectedOutputDirectories, out string deletedOutputDirectory))
                    {
                        AddPruneScopePath(result, projection.Table, deletedOutputDirectory, directoryPath: true);
                    }
                }
                projectionStopwatch.Stop();
                Log((operation ?? "playlist_custom_folder_output") + " materialize_projection_done"
                    + " reason=" + (reason ?? "unknown")
                    + " index=" + (projectionIndex + 1)
                    + " total=" + projectionList.Count
                    + " name=" + Quote(projection.Table?.name)
                    + " writtenFiles=" + (result.WrittenFileCount - writtenBefore)
                    + " unchangedFiles=" + (result.UnchangedFileCount - unchangedBefore)
                    + " deletedFiles=" + (result.DeletedFileCount - deletedBefore)
                    + " elapsedMs=" + projectionStopwatch.ElapsedMilliseconds);
                progressCallback?.Invoke(projectionIndex + 1, projectionList.Count, projection.Table?.name ?? string.Empty);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                projectionStopwatch.Stop();
                Log((operation ?? "playlist_custom_folder_output") + " materialize_projection_failed"
                    + " reason=" + (reason ?? "unknown")
                    + " index=" + (projectionIndex + 1)
                    + " total=" + projectionList.Count
                    + " name=" + Quote(projection.Table?.name)
                    + " outputDir=" + Quote(projection.OutputDirectory)
                    + " exception=" + Quote(ex.GetType().Name)
                    + " message=" + Quote(ex.Message)
                    + " elapsedMs=" + projectionStopwatch.ElapsedMilliseconds);
                throw;
            }
        }

        foreach (CustomFolderOutputProjection projection in projectionList
            .OrderByDescending(projection => projection.OutputDirectory.Length))
        {
            if (TryDeleteEmptyDirectory(projection.OutputDirectory, projection.ProtectedOutputDirectories, out string deletedOutputDirectory))
            {
                result.EmptyOutputDirectories.Add(deletedOutputDirectory);
                AddPruneScopePath(result, projection.Table, deletedOutputDirectory, directoryPath: true);
            }
        }

        result.OutputDirectories.RemoveAll(string.IsNullOrWhiteSpace);
        AddOwnedDirectoryEntries(result.DirectoryEntries, result.SyncItems, result.DirectoryRowGenerationScopeDirectories);
        return result;
    }

    internal CustomFolderOutputPhysicalMtimeSignatureIndex CreatePhysicalMtimeSignatureIndex(
        IEnumerable<string> outputDirectories,
        CustomFolderOutputPhysicalSurface physicalSurface,
        IEnumerable<string> ownerBoundaryDirectories = null)
    {
        if (physicalSurface?.DiscoveryComplete != true)
        {
            return CustomFolderOutputPhysicalMtimeSignatureIndex.Incomplete;
        }

        var builders = new Dictionary<string, CustomFolderOutputPhysicalMtimeSignatureBuilder>(StringComparer.OrdinalIgnoreCase);
        foreach (string outputDirectory in outputDirectories ?? [])
        {
            string normalizedDirectory = Lr2FolderPath.NormalizeDirectoryPath(outputDirectory);
            if (!string.IsNullOrWhiteSpace(normalizedDirectory) && !builders.ContainsKey(normalizedDirectory))
            {
                builders[normalizedDirectory] = new CustomFolderOutputPhysicalMtimeSignatureBuilder(normalizedDirectory);
            }
        }
        if (builders.Count == 0)
        {
            return new CustomFolderOutputPhysicalMtimeSignatureIndex(
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                discoveryComplete: true);
        }

        var ownerResolver = new CustomFolderOutputOwnerResolver(ownerBoundaryDirectories ?? builders.Keys);
        foreach (RootFileEnumerationEntry entry in (physicalSurface.FileEntries?.Values ?? [])
            .Where(entry => entry != null && !string.IsNullOrWhiteSpace(entry.Path))
            .OrderBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase))
        {
            string normalizedFile = CustomFolderOutputPhysicalSurface.NormalizeFilePath(entry.Path);
            if (string.IsNullOrWhiteSpace(normalizedFile)
                || !ownerResolver.TryFindOwner(normalizedFile, out string ownerDirectory)
                || !builders.TryGetValue(ownerDirectory, out CustomFolderOutputPhysicalMtimeSignatureBuilder builder))
            {
                continue;
            }
            builder.AddFile(normalizedFile, entry.LastWriteTimeUtc);
        }

        var signatures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (CustomFolderOutputPhysicalMtimeSignatureBuilder builder in builders.Values)
        {
            if (builder.TryBuild(out string signature))
            {
                signatures[builder.OutputDirectory] = signature;
            }
        }
        return new CustomFolderOutputPhysicalMtimeSignatureIndex(signatures, discoveryComplete: true);
    }

    internal static void MarkAllFilesForWrite(CustomFolderOutputProjection projection)
    {
        if (projection == null)
        {
            return;
        }
        foreach (CustomFolderOutputFileProjection file in projection.Files ?? [])
        {
            if (!string.IsNullOrWhiteSpace(file?.FilePath))
            {
                projection.ForceWriteFilePaths.Add(file.FilePath);
            }
        }
    }

    internal static CustomFolderOutputPhysicalSurface CreatePhysicalSurfaceFromSyncItems(IEnumerable<Lr2FolderFileSyncItem> syncItems)
    {
        return CustomFolderOutputPhysicalSurface.FromEntries(
            (syncItems ?? [])
                .Where(item => !string.IsNullOrWhiteSpace(item?.FilePath))
                .Select(item => new RootFileEnumerationEntry(item.FilePath, item.LastWriteTimeUtc)),
            discoveryComplete: true);
    }

    private void AssignProtectedOutputDirectories(
        IReadOnlyCollection<CustomFolderOutputProjection> projections,
        CustomFolderOutputSettingsSnapshot settings)
    {
        IReadOnlyCollection<string> knownOutputDirectories = knownOutputDirectoriesProvider(projections, settings) ?? [];
        foreach (CustomFolderOutputProjection projection in projections ?? [])
        {
            string outputDirectory = Lr2FolderPath.NormalizeDirectoryPath(projection?.OutputDirectory);
            if (string.IsNullOrWhiteSpace(outputDirectory))
            {
                continue;
            }
            projection.ProtectedOutputDirectories = [.. knownOutputDirectories
                .Where(directory => !string.Equals(directory, outputDirectory, StringComparison.OrdinalIgnoreCase)
                    && Lr2FolderPath.IsSameOrDescendant(directory, outputDirectory))
                .Distinct(StringComparer.OrdinalIgnoreCase)];
        }
    }

    private static IReadOnlyCollection<string> CreateProtectedOutputRowScopePaths(CustomFolderOutputProjection projection)
    {
        if (projection == null || projection.ProtectedOutputDirectories == null)
        {
            return [];
        }
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string protectedDirectory in projection.ProtectedOutputDirectories)
        {
            foreach (string scopePath in CreateOutputRowScopePaths(projection.Table, protectedDirectory, projection.Settings))
            {
                if (!string.IsNullOrWhiteSpace(scopePath))
                {
                    result.Add(scopePath);
                }
            }
        }
        return [.. result];
    }

    private static IReadOnlyCollection<string> CreateOutputRowScopePaths(
        BMSTable table,
        string outputDirectory,
        CustomFolderOutputSettingsSnapshot settings)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            return [];
        }
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string physicalPath = Lr2FolderPath.ToFolderPath(outputDirectory);
        if (!string.IsNullOrWhiteSpace(physicalPath))
        {
            result.Add(physicalPath);
        }
        string databasePath = ResolveDatabasePath(table, physicalPath ?? outputDirectory, settings);
        if (!string.IsNullOrWhiteSpace(databasePath))
        {
            result.Add(databasePath);
        }
        return [.. result];
    }

    private static string ResolveDatabasePath(BMSTable table, string filePath, CustomFolderOutputSettingsSnapshot settings)
    {
        var item = new Lr2FolderFileSyncItem { FilePath = filePath };
        ApplySourceClassification(item, table, settings);
        return NormalizeRowPath(item.DatabasePath ?? item.FilePath);
    }

    private static void ApplySourceClassification(
        Lr2FolderFileSyncItem item,
        BMSTable table,
        CustomFolderOutputSettingsSnapshot settings)
    {
        if (item == null || table?.is_root_folder != true)
        {
            return;
        }
        if (settings == null)
        {
            throw new System.InvalidOperationException("Custom-folder output settings snapshot was not provided.");
        }
        Lr2FolderFileSourceClassification classification = Lr2FolderFileSourceClassifier.Classify(new Lr2FolderFileSourceClassificationRequest
        {
            FilePath = item.FilePath,
            Lr2RootPath = settings.LR2RootPath,
            RootCustomFolderOutputBaseDir = settings.LR2CustomFolderOutputBaseDirRootType
        });
        item.DatabasePath = classification.DatabasePath;
        item.FolderType = classification.FolderType;
        item.ParentHash = classification.ParentHash;
    }

    private static IReadOnlyList<CustomFolderDefinition> OrderDefinitions(IEnumerable<CustomFolderDefinition> definitions)
    {
        var indexedDefinitions = (definitions ?? [])
            .Select((definition, index) => new
            {
                Definition = definition,
                Index = index,
                RelativeDirectory = NormalizeRelativeDirectory(definition?.RelativeDirectory)
            })
            .Where(item => item.Definition != null)
            .ToList();
        var directoryOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in indexedDefinitions)
        {
            if (!directoryOrder.ContainsKey(item.RelativeDirectory))
            {
                directoryOrder[item.RelativeDirectory] = item.Index;
            }
        }
        return [.. indexedDefinitions
            .OrderBy(item => directoryOrder[item.RelativeDirectory])
            .ThenBy(item => item.Definition.IsRandomVariant ? 1 : 0)
            .ThenBy(item => item.Index)
            .Select(item => item.Definition)];
    }

    private static string NormalizeRelativeDirectory(string relativeDirectory)
    {
        if (string.IsNullOrWhiteSpace(relativeDirectory))
        {
            return string.Empty;
        }
        return relativeDirectory.Trim()
            .Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
    }

    private static string NormalizeRowPath(string path)
    {
        return Lr2FolderFileProjection.NormalizeDatabasePath(path);
    }

    private static void RemoveStaleManagedFiles(
        CustomFolderOutputProjection projection,
        ISet<string> expectedFilePaths,
        CustomFolderBatchMaterializationResult result)
    {
        if (projection == null || string.IsNullOrWhiteSpace(projection.OutputDirectory) || result == null
            || !LongPathFileSystem.DirectoryExists(projection.OutputDirectory))
        {
            return;
        }
        foreach (string filePath in EnumerateManagedFiles(projection.OutputDirectory))
        {
            string normalizedFilePath = Lr2FolderPath.NormalizeDirectoryPath(filePath);
            if (!string.IsNullOrWhiteSpace(normalizedFilePath)
                && expectedFilePaths != null
                && expectedFilePaths.Contains(normalizedFilePath))
            {
                continue;
            }
            if (IsPathUnderAnyDirectory(normalizedFilePath, projection.ProtectedOutputDirectories))
            {
                continue;
            }
            if (TryDeleteManagedFile(filePath, out bool deleted))
            {
                if (deleted)
                {
                    result.DeletedFileCount++;
                }
                AddPruneScopePath(result, projection.Table, filePath, directoryPath: false);
            }
        }
        foreach (string deletedDirectory in RemoveEmptyDirectories(projection.OutputDirectory, projection.ProtectedOutputDirectories))
        {
            result.EmptyOutputDirectories.Add(deletedDirectory);
            AddPruneScopePath(result, projection.Table, deletedDirectory, directoryPath: true);
        }
    }

    private static IReadOnlyList<string> EnumerateManagedFiles(string outputDirectory)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory) || !LongPathFileSystem.DirectoryExists(outputDirectory))
        {
            return [];
        }
        try
        {
            return [.. LongPathFileSystem.EnumerateFiles(outputDirectory, "*.lr2folder", SearchOption.AllDirectories)];
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException || ex is FileNotFoundException)
        {
            return [];
        }
    }

    private static bool TryDeleteManagedFile(string filePath, out bool deleted)
    {
        deleted = false;
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }
        try
        {
            bool existed = LongPathFileSystem.FileExists(filePath);
            LongPathFileSystem.DeleteFile(filePath);
            deleted = existed;
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException || ex is DirectoryNotFoundException)
        {
            return true;
        }
    }

    private static void AddPruneScopePath(CustomFolderBatchMaterializationResult result, BMSTable table, string path, bool directoryPath)
    {
        if (result == null || string.IsNullOrWhiteSpace(path))
        {
            return;
        }
        result.PruneScopePaths.Add(path);
        string databasePath = ResolveDatabasePath(table, directoryPath ? Lr2FolderPath.ToFolderPath(path) : path, result.Settings);
        if (!string.IsNullOrWhiteSpace(databasePath))
        {
            result.PruneScopePaths.Add(databasePath);
        }
    }

    private static List<string> RemoveEmptyDirectories(string outputDirectory, IReadOnlyCollection<string> protectedDirectories)
    {
        var deletedDirectories = new List<string>();
        if (string.IsNullOrWhiteSpace(outputDirectory) || !LongPathFileSystem.DirectoryExists(outputDirectory))
        {
            return deletedDirectories;
        }
        IReadOnlyList<string> directories;
        try
        {
            directories = [.. LongPathFileSystem.EnumerateDirectories(outputDirectory, "*", SearchOption.AllDirectories)
                .OrderByDescending(path => path.Length)];
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException || ex is FileNotFoundException)
        {
            return deletedDirectories;
        }
        foreach (string directory in directories)
        {
            if (IsPathUnderAnyDirectory(directory, protectedDirectories))
            {
                continue;
            }
            if (TryDeleteEmptyDirectory(directory, protectedDirectories, out string deletedDirectory))
            {
                deletedDirectories.Add(deletedDirectory);
            }
        }
        return deletedDirectories;
    }

    private static bool TryDeleteEmptyDirectory(string directory, IReadOnlyCollection<string> protectedDirectories, out string deletedDirectory)
    {
        deletedDirectory = null;
        if (string.IsNullOrWhiteSpace(directory) || IsPathUnderAnyDirectory(directory, protectedDirectories))
        {
            return false;
        }
        try
        {
            if (!LongPathFileSystem.DirectoryExists(directory) || LongPathFileSystem.EnumerateFileSystemEntries(directory).Any())
            {
                return false;
            }
            LongPathFileSystem.DeleteDirectory(directory, recursive: false);
            deletedDirectory = directory;
            return true;
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException || ex is FileNotFoundException)
        {
            return false;
        }
    }

    private static bool IsPathUnderAnyDirectory(string path, IEnumerable<string> directories)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }
        string normalizedPath = Lr2FolderPath.NormalizeDirectoryPath(path);
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return false;
        }
        return (directories ?? [])
            .Select(Lr2FolderPath.NormalizeDirectoryPath)
            .Any(directory => !string.IsNullOrWhiteSpace(directory)
                && Lr2FolderPath.IsSameOrDescendant(normalizedPath, directory));
    }

    private static void WriteAllText(string path, string text, Encoding encoding)
    {
        using FileStream stream = LongPathFileSystem.Open(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, encoding);
        writer.Write(text);
    }

    private static ExistingFileState InspectExistingFile(string path, string text, Encoding encoding)
    {
        if (!LongPathFileSystem.FileExists(path))
        {
            return ExistingFileState.Missing;
        }

        try
        {
            byte[] expected = encoding.GetBytes(text ?? string.Empty);
            byte[] actual = LongPathFileSystem.ReadAllBytes(path);
            return expected.SequenceEqual(actual)
                ? ExistingFileState.Current
                : ExistingFileState.Outdated;
        }
        catch (Exception ex) when (ex is FileNotFoundException || ex is DirectoryNotFoundException)
        {
            return ExistingFileState.Missing;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            return ExistingFileState.Unverified;
        }
    }

    private static IEnumerable<string> ReadLines(string text)
    {
        using var reader = new StringReader(text ?? string.Empty);
        string line;
        while ((line = reader.ReadLine()) != null)
        {
            yield return line;
        }
    }

    private static void AddOwnedDirectoryEntries(
        IDictionary<string, RootFileEnumerationEntry> result,
        IReadOnlyCollection<Lr2FolderFileSyncItem> items,
        IReadOnlyCollection<string> directoryRowGenerationScopeDirectories)
    {
        IReadOnlyCollection<string> metadataTargets = Lr2FolderFileDbSyncService.CreateParentDirectoryMetadataTargets(
            items,
            directoryRowGenerationScopeDirectories);
        foreach (string target in metadataTargets ?? [])
        {
            var entry = RootFileEnumerationEntry.FromDirectoryInfo(target);
            string key = Lr2FolderPath.NormalizeDirectoryPath(entry?.Path);
            if (!string.IsNullOrWhiteSpace(key))
            {
                result[key] = new RootFileEnumerationEntry(key, entry.LastWriteTimeUtc, entry.FileSize);
            }
        }
    }

    private void Log(string message)
    {
        logPerformance?.Invoke(message);
    }

    private static string Quote(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "\"\"";
        }
        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    private CustomFolderOutputSettingsSnapshot GetSettings()
    {
        return settingsProvider() ?? throw new InvalidOperationException("Custom-folder output settings provider returned null.");
    }

    internal sealed class CustomFolderDefinition
    {
        internal string RelativeDirectory { get; set; }

        internal string Text { get; set; }

        internal Lr2FolderFileDefinition ParsedDefinition { get; set; }

        internal bool IsRandomVariant { get; set; }
    }

    internal sealed class CustomFolderOutputProjection
    {
        internal BMSTable Table { get; set; }

        internal CustomFolderOutputSettingsSnapshot Settings { get; set; }

        internal string OutputDirectory { get; set; }

        internal IReadOnlyCollection<string> OutputRowScopePaths { get; set; } = [];

        internal IReadOnlyCollection<string> ProtectedOutputDirectories { get; set; } = [];

        internal IReadOnlyList<CustomFolderOutputFileProjection> Files { get; set; } = [];

        internal HashSet<string> ForceWriteFilePaths { get; } = new(StringComparer.OrdinalIgnoreCase);

        internal CustomFolderOutputPhysicalSurface PhysicalSurface { get; set; }
    }

    internal sealed class CustomFolderOutputFileProjection
    {
        internal int Index { get; set; }

        internal string RelativeDirectory { get; set; }

        internal string Text { get; set; }

        internal Lr2FolderFileDefinition Definition { get; set; }

        internal string FilePath { get; set; }

        internal string DatabasePath { get; set; }
    }

    internal sealed class CustomFolderBatchMaterializationResult
    {
        internal CustomFolderOutputSettingsSnapshot Settings { get; set; }

        internal List<string> OutputDirectories { get; } = [];

        internal List<string> OutputRowScopeDirectories { get; } = [];

        internal List<string> DirectoryRowGenerationScopeDirectories { get; } = [];

        internal List<string> Lr2FolderSurfaceScopeDirectories { get; } = [];

        internal List<Lr2FolderFileSyncItem> SyncItems { get; } = [];

        internal List<string> UnverifiedFilePaths { get; } = [];

        internal bool HasUnverifiedFiles => UnverifiedFilePaths.Count > 0;

        internal Dictionary<string, RootFileEnumerationEntry> DirectoryEntries { get; } = new(StringComparer.OrdinalIgnoreCase);

        internal int WrittenFileCount { get; set; }

        internal int UnchangedFileCount { get; set; }

        internal int DeletedFileCount { get; set; }

        internal List<string> PruneScopePaths { get; } = [];

        internal List<string> PruneExcludedDirectories { get; } = [];

        internal List<string> EmptyOutputDirectories { get; } = [];
    }

    private enum ExistingFileState
    {
        Missing,
        Current,
        Outdated,
        Unverified
    }

    internal sealed class CustomFolderOutputPhysicalMtimeSignatureIndex(
        IReadOnlyDictionary<string, string> signatures,
        bool discoveryComplete)
    {
        internal static CustomFolderOutputPhysicalMtimeSignatureIndex Incomplete { get; } = new(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            discoveryComplete: false);

        private IReadOnlyDictionary<string, string> Signatures { get; } =
            signatures ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        internal bool DiscoveryComplete { get; } = discoveryComplete;

        internal int SignatureCount => Signatures.Count;

        internal bool TryGetSignature(string outputDirectory, out string signature)
        {
            signature = null;
            string normalizedDirectory = Lr2FolderPath.NormalizeDirectoryPath(outputDirectory);
            return DiscoveryComplete
                && !string.IsNullOrWhiteSpace(normalizedDirectory)
                && Signatures.TryGetValue(normalizedDirectory, out signature);
        }
    }

    private sealed class CustomFolderOutputOwnerResolver
    {
        private const string NoOwner = "";

        private readonly HashSet<string> outputDirectories;

        private readonly Dictionary<string, string> ownerByDirectory = new(StringComparer.OrdinalIgnoreCase);

        internal CustomFolderOutputOwnerResolver(IEnumerable<string> outputDirectories)
        {
            this.outputDirectories = new HashSet<string>(
                (outputDirectories ?? [])
                    .Select(Lr2FolderPath.NormalizeDirectoryPath)
                    .Where(directory => !string.IsNullOrWhiteSpace(directory)),
                StringComparer.OrdinalIgnoreCase);
        }

        internal bool TryFindOwner(string filePath, out string ownerDirectory)
        {
            ownerDirectory = null;
            string directory = Lr2FolderPath.NormalizeDirectoryPath(Lr2FolderPath.SafeGetDirectoryName(filePath));
            if (string.IsNullOrWhiteSpace(directory) || outputDirectories.Count == 0)
            {
                return false;
            }
            if (ownerByDirectory.TryGetValue(directory, out string cachedOwner))
            {
                ownerDirectory = string.Equals(cachedOwner, NoOwner, StringComparison.Ordinal) ? null : cachedOwner;
                return ownerDirectory != null;
            }

            var visitedDirectories = new List<string>();
            string current = directory;
            string resolvedOwner = null;
            while (!string.IsNullOrWhiteSpace(current))
            {
                if (ownerByDirectory.TryGetValue(current, out cachedOwner))
                {
                    resolvedOwner = string.Equals(cachedOwner, NoOwner, StringComparison.Ordinal) ? null : cachedOwner;
                    break;
                }
                visitedDirectories.Add(current);
                if (outputDirectories.Contains(current))
                {
                    resolvedOwner = current;
                    break;
                }
                string parent = Lr2FolderPath.SafeGetParentNormalizedDirectory(current);
                if (string.IsNullOrWhiteSpace(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
                current = parent;
            }

            string cacheValue = resolvedOwner ?? NoOwner;
            foreach (string visitedDirectory in visitedDirectories)
            {
                ownerByDirectory[visitedDirectory] = cacheValue;
            }
            ownerDirectory = resolvedOwner;
            return ownerDirectory != null;
        }
    }

    private sealed class CustomFolderOutputPhysicalMtimeSignatureBuilder(string outputDirectory)
    {
        private readonly StringBuilder builder = new();

        private readonly HashSet<string> parentDirectories = new(StringComparer.OrdinalIgnoreCase);

        internal string OutputDirectory { get; } = outputDirectory;

        internal void AddFile(string filePath, DateTime? lastWriteTimeUtc)
        {
            string normalizedFile = CustomFolderOutputPhysicalSurface.NormalizeFilePath(filePath);
            if (string.IsNullOrWhiteSpace(normalizedFile))
            {
                return;
            }
            builder.Append("F\t").Append(normalizedFile).Append('\t')
                .Append(lastWriteTimeUtc?.Ticks.ToString(CultureInfo.InvariantCulture) ?? "missing").Append('\n');
            string parent = Lr2FolderPath.NormalizeDirectoryPath(Lr2FolderPath.SafeGetDirectoryName(normalizedFile));
            while (!string.IsNullOrWhiteSpace(parent))
            {
                parentDirectories.Add(parent);
                if (string.Equals(parent, OutputDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
                string next = Lr2FolderPath.SafeGetParentNormalizedDirectory(parent);
                if (string.IsNullOrWhiteSpace(next) || string.Equals(next, parent, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
                parent = next;
            }
        }

        internal bool TryBuild(out string signature)
        {
            signature = null;
            string normalizedOutputDirectory = Lr2FolderPath.NormalizeDirectoryPath(OutputDirectory);
            if (string.IsNullOrWhiteSpace(normalizedOutputDirectory))
            {
                return false;
            }
            AppendDirectoryLine(builder, "O", normalizedOutputDirectory);
            foreach (string directory in parentDirectories.OrderBy(directory => directory, StringComparer.OrdinalIgnoreCase))
            {
                if (!string.Equals(directory, normalizedOutputDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    AppendDirectoryLine(builder, "D", directory);
                }
            }
            signature = BMSTable.ComputeSha256Hex(builder.ToString());
            return true;
        }

        private static void AppendDirectoryLine(StringBuilder builder, string kind, string directory)
        {
            var entry = RootFileEnumerationEntry.FromDirectoryInfo(directory);
            string normalizedDirectory = Lr2FolderPath.NormalizeDirectoryPath(entry?.Path ?? directory);
            if (string.IsNullOrWhiteSpace(normalizedDirectory))
            {
                return;
            }
            builder.Append(kind).Append('\t').Append(normalizedDirectory).Append('\t')
                .Append(entry?.LastWriteTimeUtc?.Ticks.ToString(CultureInfo.InvariantCulture) ?? "missing").Append('\n');
        }
    }
}
