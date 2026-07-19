using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// custom-folder output の再出力、移行、削除、修復、および search-root 同期を所有します。
/// <para>
/// <see cref="BMSPlaylist"/> はテーブルの生存期間と DB transaction を保持し、
/// この owner は出力の prepare、ファイル materialization、LR2 row 同期、旧出力の prune を
/// 一つの lifecycle として閉じます。
/// </para>
/// </summary>
internal sealed class PlaylistCustomFolderOutputMaintenanceOwner
{
    private readonly PlaylistCustomFolderOutputOwner outputOwner;

    private readonly Func<CustomFolderOutputSettingsSnapshot> settingsProvider;

    private readonly Func<IReadOnlyList<BMSTable>> tablesSnapshotProvider;

    private readonly Action<BMSTable, string> ensureEntriesLoaded;

    private readonly Func<LR2Config> lr2ConfigProvider;

    private readonly Func<CustomFolderBatchMaterializationRequest, Lr2FolderFileDbSyncResult> syncMaterialization;

    private readonly Action<IReadOnlyList<PlaylistCustomFolderOutputOwner.CustomFolderOutputProjection>, CustomFolderOutputPhysicalSurface> persistStatuses;

    private readonly Action<BMSTable> deleteStatus;

    private readonly Func<BMSTable, CustomFolderOutputSettingsSnapshot, string> outputDirectoryResolver;

    private readonly Action<string> logPerformance;

    private readonly PlaylistOperationNotificationOwner notificationOwner;

    internal PlaylistCustomFolderOutputMaintenanceOwner(
        PlaylistCustomFolderOutputOwner outputOwner,
        Func<CustomFolderOutputSettingsSnapshot> settingsProvider,
        Func<IReadOnlyList<BMSTable>> tablesSnapshotProvider,
        Action<BMSTable, string> ensureEntriesLoaded,
        Func<LR2Config> lr2ConfigProvider,
        Func<CustomFolderBatchMaterializationRequest, Lr2FolderFileDbSyncResult> syncMaterialization,
        Action<IReadOnlyList<PlaylistCustomFolderOutputOwner.CustomFolderOutputProjection>, CustomFolderOutputPhysicalSurface> persistStatuses,
        Action<BMSTable> deleteStatus,
        Func<BMSTable, CustomFolderOutputSettingsSnapshot, string> outputDirectoryResolver,
        Action<string> logPerformance,
        PlaylistOperationNotificationOwner notificationOwner)
    {
        this.outputOwner = outputOwner ?? throw new ArgumentNullException(nameof(outputOwner));
        this.settingsProvider = settingsProvider ?? throw new ArgumentNullException(nameof(settingsProvider));
        this.tablesSnapshotProvider = tablesSnapshotProvider ?? throw new ArgumentNullException(nameof(tablesSnapshotProvider));
        this.ensureEntriesLoaded = ensureEntriesLoaded ?? throw new ArgumentNullException(nameof(ensureEntriesLoaded));
        this.lr2ConfigProvider = lr2ConfigProvider ?? throw new ArgumentNullException(nameof(lr2ConfigProvider));
        this.syncMaterialization = syncMaterialization ?? throw new ArgumentNullException(nameof(syncMaterialization));
        this.persistStatuses = persistStatuses ?? throw new ArgumentNullException(nameof(persistStatuses));
        this.deleteStatus = deleteStatus ?? throw new ArgumentNullException(nameof(deleteStatus));
        this.outputDirectoryResolver = outputDirectoryResolver ?? throw new ArgumentNullException(nameof(outputDirectoryResolver));
        this.logPerformance = logPerformance;
        this.notificationOwner = notificationOwner ?? throw new ArgumentNullException(nameof(notificationOwner));
    }

    internal bool SyncRootFolderOutputDirectoriesToLr2Config(CustomFolderOutputSettingsSnapshot settings)
    {
        if (settings == null || !settings.OperationModeLR2DB)
        {
            return false;
        }

        LR2Config config = lr2ConfigProvider();
        if (config == null)
        {
            throw new InvalidOperationException("LR2 configuration is required for custom-folder search-root synchronization.");
        }

        IReadOnlyList<string> expectedDirectories = [.. (tablesSnapshotProvider() ?? [])
            .Where(table => table?.is_root_folder == true && !string.IsNullOrWhiteSpace(table.Output_dir))
            .Select(table => Path.Combine(settings.LR2CustomFolderOutputBaseDirRootType, table.Output_dir))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        if (expectedDirectories.Count == 0)
        {
            return false;
        }

        foreach (string expectedDirectory in expectedDirectories)
        {
            LongPathFileSystem.CreateDirectory(expectedDirectory);
        }

        List<string> beforeDirectories = config.GetBMSSearchDirectoriesForChangeTracking();
        List<string> missingDirectories = [.. expectedDirectories
            .Where(path => !beforeDirectories.Contains(path, StringComparer.OrdinalIgnoreCase))];
        if (missingDirectories.Count == 0)
        {
            return false;
        }

        config.SetBMSSearchDirectories(beforeDirectories
            .Concat(expectedDirectories)
            .Distinct(StringComparer.OrdinalIgnoreCase));
        config.Save();
        return true;
    }

    internal bool SyncCustomFolderOutputSearchRootsAfterSettingsChange(
        string previousRootOutputBaseDirectory,
        LR2Config configOverride,
        CustomFolderOutputSettingsSnapshot settings)
    {
        if (settings == null || !settings.OperationModeLR2DB)
        {
            return false;
        }

        LR2Config config = configOverride ?? lr2ConfigProvider();
        if (config == null)
        {
            throw new InvalidOperationException("LR2 configuration is required for custom-folder search-root synchronization.");
        }

        IReadOnlyList<string> previousRootDirectories = CreateRootCustomFolderOutputDirectories(previousRootOutputBaseDirectory);
        IReadOnlyList<string> currentRootDirectories = CustomFolderOutputBaseRegistry.NormalizeBaseDirectories(
            CreateRootCustomFolderOutputDirectories(settings.LR2CustomFolderOutputBaseDirRootType));
        foreach (string currentRootDirectory in currentRootDirectories)
        {
            LongPathFileSystem.CreateDirectory(currentRootDirectory);
        }

        List<string> beforeDirectories = config.GetBMSSearchDirectoriesForChangeTracking();
        IReadOnlyList<string> explicitRemoveDirectories = CustomFolderOutputBaseRegistry.NormalizeBaseDirectories(
            previousRootDirectories.Concat([settings.LR2CustomFolderOutputBaseDirRootType]));
        IEnumerable<string> removeDirectories = beforeDirectories.Where(registeredPath =>
            explicitRemoveDirectories.Contains(registeredPath, StringComparer.OrdinalIgnoreCase)
            || IsRootCustomFolderOutputSearchRootAdoptionRemovalTarget(
                registeredPath,
                settings.LR2CustomFolderOutputBaseDirRootType,
                currentRootDirectories));
        List<string> nextDirectories = [.. beforeDirectories
            .Except(removeDirectories, StringComparer.OrdinalIgnoreCase)
            .Concat(currentRootDirectories)
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        EnsureCustomFolderOutputBaseSearchRoots(
            nextDirectories,
            settings.LR2CustomFolderOutputBaseDir,
            settings.LR2CustomFolderAdditionalOutputBaseDirs);
        if (nextDirectories.SequenceEqual(beforeDirectories, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        config.SetBMSSearchDirectories(nextDirectories);
        config.Save();
        return true;
    }

    internal IReadOnlyCollection<string> CreateMigrationProtectedOutputDirectories(
        IEnumerable<string> additionalDirectories,
        CustomFolderOutputSettingsSnapshot settings,
        IEnumerable<string> excludedDirectories = null)
    {
        var excluded = new HashSet<string>(
            (excludedDirectories ?? [])
                .Select(Lr2FolderPath.NormalizeDirectoryPath)
                .Where(path => !string.IsNullOrWhiteSpace(path)),
            StringComparer.OrdinalIgnoreCase);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string directory in CreateKnownOutputDirectories(settings).Concat(additionalDirectories ?? []))
        {
            if (!excluded.Contains(Lr2FolderPath.NormalizeDirectoryPath(directory)))
            {
                AddOutputDirectory(directories, directory);
            }
        }
        return [.. directories];
    }

    internal bool TryReOutputCustomFolder(BMSTable table, CustomFolderOutputSettingsSnapshot settings)
    {
        settings ??= GetSettings();
        try
        {
            PlaylistCustomFolderOutputOwner.CustomFolderOutputProjection projection = CreateProjection(table, settings);
            PlaylistCustomFolderOutputOwner.MarkAllFilesForWrite(projection);
            ReOutputProjectionsAsync(
                [projection],
                tableCount: 1,
                reason: "ReOutputCustomFolder",
                operation: "playlist_custom_folder_output_single",
                buildPreparedDataSurface: false,
                yieldBetweenTables: false,
                settings: settings)
                .GetAwaiter()
                .GetResult();
            DeleteEmptyOutputDirectory(projection.OutputDirectory);
            return true;
        }
        catch
        {
            notificationOwner.QueueWarning(string.Format(Resources.Warn_CustomFolderOutputFailed, table?.name, ResolveOutputDirectory(table, settings)));
            return false;
        }
    }

    internal bool TryMigrateCustomFolderOutputDirectory(
        BMSTable table,
        string outputDirectoryBefore,
        string outputDirectoryAfter,
        bool wasRootFolderBefore,
        string rootOutputBaseDirectoryBefore,
        string outputBaseDirectoryBefore,
        bool inferOutputBaseDirectoryBeforeWhenMissing,
        CustomFolderOutputSettingsSnapshot settings)
    {
        settings ??= GetSettings();
        string resolvedOutputDirectoryAfter = string.IsNullOrWhiteSpace(outputDirectoryAfter)
            ? ResolveOutputDirectory(table, settings)
            : outputDirectoryAfter;
        bool sameDirectory = IsSameDirectory(outputDirectoryBefore, resolvedOutputDirectoryAfter);
        try
        {
            PlaylistCustomFolderOutputOwner.CustomFolderOutputProjection projection = CreateProjection(
                table,
                settings,
                resolvedOutputDirectoryAfter);
            if (sameDirectory)
            {
                PlaylistCustomFolderOutputOwner.MarkAllFilesForWrite(projection);
            }
            ReOutputProjectionsAsync(
                [projection],
                tableCount: 1,
                reason: "MigrateCustomFolderOutputDirectory",
                operation: "playlist_custom_folder_output_migrate",
                buildPreparedDataSurface: false,
                yieldBetweenTables: false,
                settings: settings)
                .GetAwaiter()
                .GetResult();

            if (sameDirectory)
            {
                DeleteEmptyOutputDirectory(resolvedOutputDirectoryAfter);
                return true;
            }

            IReadOnlyCollection<string> managedBases = CreateManagedOutputBaseScopes(
                table,
                wasRootFolderBefore,
                rootOutputBaseDirectoryBefore,
                outputBaseDirectoryBefore,
                inferOutputBaseDirectoryBeforeWhenMissing,
                settings);
            bool deletionFailure;
            if (!DeleteOutputDirectoryTree(outputDirectoryBefore, managedBases, CreateMigrationProtectedOutputDirectories(
                [resolvedOutputDirectoryAfter],
                settings,
                [outputDirectoryBefore]),
                out deletionFailure))
            {
                if (deletionFailure)
                {
                    notificationOwner.QueueWarning(string.Format(Resources.Warn_FileOrDirDeleteFailed, outputDirectoryBefore));
                }
                return false;
            }

            SyncPrunedRows(
                CreateDirectoryPruneScopes(outputDirectoryBefore, wasRootFolderBefore, rootOutputBaseDirectoryBefore, settings),
                CreatePruneExcludedDirectories(
                    CreateMigrationProtectedOutputDirectories([resolvedOutputDirectoryAfter], settings, [outputDirectoryBefore]),
                    [outputDirectoryBefore],
                    [table],
                    settings),
                CreatePruneExcludedPaths(
                    CreateMigrationProtectedOutputDirectories([resolvedOutputDirectoryAfter], settings, [outputDirectoryBefore]),
                    [outputDirectoryBefore],
                    [table],
                    settings));
            return true;
        }
        catch
        {
            notificationOwner.QueueWarning(string.Format(Resources.Warn_CustomFolderOutputFailed, table?.name, resolvedOutputDirectoryAfter));
            return false;
        }
    }

    internal bool TryRemoveCustomFolder(
        BMSTable table,
        string outputDirectory,
        bool wasRootFolder,
        string rootOutputBaseDirectory,
        string outputBaseDirectory,
        CustomFolderOutputSettingsSnapshot settings)
    {
        settings ??= GetSettings();
        bool deletionFailure;
        if (!DeleteOutputDirectoryTree(outputDirectory, [outputBaseDirectory], null, out deletionFailure))
        {
            if (deletionFailure)
            {
                notificationOwner.QueueWarning(string.Format(Resources.Warn_FileOrDirDeleteFailed, outputDirectory));
            }
            return false;
        }

        try
        {
            SyncPrunedRows(
                CreateDirectoryPruneScopes(outputDirectory, wasRootFolder, rootOutputBaseDirectory, settings));
            deleteStatus(table);
            return true;
        }
        catch
        {
            notificationOwner.QueueWarning(string.Format(Resources.Warn_FileOrDirDeleteFailed, outputDirectory));
            return false;
        }
    }

    internal void MigrateCustomFolderOutputDirectories(
        IReadOnlyList<BMSTable> tables,
        IReadOnlyDictionary<BMSTable, string> outputDirectoriesBefore,
        string reason,
        Action<int, int, string> progressCallback,
        IReadOnlyDictionary<BMSTable, bool> wasRootFolderBeforeByTable,
        string rootOutputBaseDirectoryBefore,
        IReadOnlyDictionary<BMSTable, string> outputBaseDirectoryBeforeByTable,
        CustomFolderOutputSettingsSnapshot settings)
    {
        settings ??= GetSettings();
        var plans = new List<MigrationPlan>();
        foreach (BMSTable table in tables ?? [])
        {
            if (table == null
                || string.IsNullOrWhiteSpace(table.Output_dir)
                || outputDirectoriesBefore?.TryGetValue(table, out string beforeDirectory) != true
                || string.IsNullOrWhiteSpace(beforeDirectory))
            {
                continue;
            }

            string afterDirectory = ResolveOutputDirectory(table, settings);
            if (string.IsNullOrWhiteSpace(afterDirectory) || IsSameDirectory(beforeDirectory, afterDirectory))
            {
                continue;
            }
            plans.Add(new MigrationPlan
            {
                Table = table,
                OutputDirectoryBefore = beforeDirectory,
                OutputDirectoryAfter = afterDirectory,
                WasRootFolderBefore = wasRootFolderBeforeByTable?.TryGetValue(table, out bool wasRoot) == true
                    ? wasRoot
                    : table.is_root_folder,
                RootOutputBaseDirectoryBefore = rootOutputBaseDirectoryBefore,
                OutputBaseDirectoryBefore = outputBaseDirectoryBeforeByTable?.TryGetValue(table, out string outputBase) == true
                    ? outputBase
                    : null,
                InferOutputBaseDirectoryBeforeWhenMissing = outputBaseDirectoryBeforeByTable == null
            });
        }
        if (plans.Count == 0)
        {
            return;
        }

        var projections = new List<PlaylistCustomFolderOutputOwner.CustomFolderOutputProjection>();
        foreach (MigrationPlan plan in plans)
        {
            ensureEntriesLoaded(plan.Table, "MigrateCustomFolderOutputDirectories");
            PlaylistCustomFolderOutputOwner.CustomFolderOutputProjection projection = CreateProjection(
                plan.Table,
                settings,
                plan.OutputDirectoryAfter);
            PlaylistCustomFolderOutputOwner.MarkAllFilesForWrite(projection);
            projections.Add(projection);
        }

        ReOutputProjectionsAsync(
            projections,
            plans.Count,
            reason,
            "playlist_custom_folder_output_migrate_bulk",
            buildPreparedDataSurface: false,
            yieldBetweenTables: false,
            progressCallback,
            settings,
            stopwatch: Stopwatch.StartNew())
            .GetAwaiter()
            .GetResult();

        IReadOnlyCollection<string> protectedDirectories = CreateMigrationProtectedOutputDirectories(
            plans.Select(plan => plan.OutputDirectoryAfter),
            settings,
            plans.Select(plan => plan.OutputDirectoryBefore));
        var pruneScopes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int index = 0;
        foreach (MigrationPlan plan in plans)
        {
            index++;
            progressCallback?.Invoke(index - 1, plans.Count, plan.Table?.name ?? string.Empty);
            IReadOnlyCollection<string> managedBases = CreateManagedOutputBaseScopes(
                plan.Table,
                plan.WasRootFolderBefore,
                plan.RootOutputBaseDirectoryBefore,
                plan.OutputBaseDirectoryBefore,
                plan.InferOutputBaseDirectoryBeforeWhenMissing,
                settings);
            bool deletionFailure;
            if (DeleteOutputDirectoryTree(plan.OutputDirectoryBefore, managedBases, protectedDirectories, out deletionFailure))
            {
                foreach (string scope in CreateDirectoryPruneScopes(
                    plan.OutputDirectoryBefore,
                    plan.WasRootFolderBefore,
                    plan.RootOutputBaseDirectoryBefore,
                    settings))
                {
                    pruneScopes.Add(scope);
                }
            }
            else if (deletionFailure)
            {
                notificationOwner.QueueWarning(string.Format(Resources.Warn_FileOrDirDeleteFailed, plan.OutputDirectoryBefore));
            }
            progressCallback?.Invoke(index, plans.Count, plan.Table?.name ?? string.Empty);
        }

        if (pruneScopes.Count > 0)
        {
            SyncPrunedRows(
                [.. pruneScopes],
                CreatePruneExcludedDirectories(
                    protectedDirectories,
                    plans.Select(plan => plan.OutputDirectoryBefore),
                    plans.Select(plan => plan.Table),
                    settings),
                CreatePruneExcludedPaths(
                    protectedDirectories,
                    plans.Select(plan => plan.OutputDirectoryBefore),
                    plans.Select(plan => plan.Table),
                    settings));
        }
    }

    internal async Task<CustomFolderBatchOutputResult> ReOutputTablesAsync(
        IReadOnlyList<BMSTable> tables,
        string reason,
        string operation,
        bool forceWriteAllFiles,
        bool throwOnProjectionFailure,
        bool buildPreparedDataSurface,
        bool yieldBetweenTables,
        Action<int, int, string> progressCallback = null,
        CustomFolderOutputSettingsSnapshot settings = null)
    {
        settings ??= GetSettings();
        tables ??= [];
        var projections = new List<PlaylistCustomFolderOutputOwner.CustomFolderOutputProjection>();
        int failedCount = 0;
        int total = GetProgressTotal(tables.Count, progressCallback);
        var stopwatch = Stopwatch.StartNew();
        for (int index = 0; index < tables.Count; index++)
        {
            if (yieldBetweenTables)
            {
                await Task.Yield();
            }

            BMSTable table = tables[index];
            if (table == null || string.IsNullOrWhiteSpace(table.Output_dir))
            {
                progressCallback?.Invoke(index + 1, total, table?.name ?? string.Empty);
                continue;
            }

            try
            {
                ensureEntriesLoaded(table, operation ?? "ReOutputCustomFolders");
                PlaylistCustomFolderOutputOwner.CustomFolderOutputProjection projection = CreateProjection(table, settings);
                if (forceWriteAllFiles)
                {
                    PlaylistCustomFolderOutputOwner.MarkAllFilesForWrite(projection);
                }
                projections.Add(projection);
                progressCallback?.Invoke(index + 1, total, table.name ?? string.Empty);
            }
            catch (Exception ex) when (IsProjectionFailure(ex))
            {
                failedCount++;
                progressCallback?.Invoke(index + 1, total, table.name ?? string.Empty);
                if (throwOnProjectionFailure)
                {
                    ExceptionDispatchInfo.Capture(ex).Throw();
                }
            }
        }

        return await ReOutputProjectionsAsync(
            projections,
            tables.Count,
            reason,
            operation,
            buildPreparedDataSurface,
            yieldBetweenTables,
            progressCallback,
            settings,
            failedCount,
            stopwatch).ConfigureAwait(false);
    }

    internal Task<CustomFolderBatchOutputResult> ReOutputProjectionsAsync(
        IReadOnlyList<PlaylistCustomFolderOutputOwner.CustomFolderOutputProjection> projections,
        int tableCount,
        string reason,
        string operation,
        bool buildPreparedDataSurface,
        bool yieldBetweenTables,
        Action<int, int, string> progressCallback = null,
        CustomFolderOutputSettingsSnapshot settings = null,
        int projectionFailedCount = 0,
        Stopwatch stopwatch = null)
    {
        return ReOutputProjectionsCoreAsync(
            projections,
            tableCount,
            reason,
            operation,
            buildPreparedDataSurface,
            progressCallback,
            settings,
            projectionFailedCount,
            stopwatch ?? Stopwatch.StartNew());
    }

    private Task<CustomFolderBatchOutputResult> ReOutputProjectionsCoreAsync(
        IReadOnlyList<PlaylistCustomFolderOutputOwner.CustomFolderOutputProjection> projections,
        int tableCount,
        string reason,
        string operation,
        bool buildPreparedDataSurface,
        Action<int, int, string> progressCallback,
        CustomFolderOutputSettingsSnapshot settings,
        int projectionFailedCount,
        Stopwatch stopwatch)
    {
        projections ??= [];
        settings ??= GetSettings();
        int total = GetProgressTotal(tableCount, progressCallback);
        progressCallback?.Invoke(Math.Min(tableCount, total), total, Resources.Custom_folder_output_progress_single_label);
        PlaylistCustomFolderOutputOwner.CustomFolderBatchMaterializationResult materialization = outputOwner.MaterializeBatch(
            projections,
            (completed, count, tableName) => progressCallback?.Invoke(
                Math.Min(tableCount + completed, total),
                total,
                tableName ?? Resources.Custom_folder_output_progress_single_label),
            operation,
            reason,
            settings);
        Lr2FolderFileDbSyncResult syncResult = syncMaterialization(new CustomFolderBatchMaterializationRequest(materialization));
        progressCallback?.Invoke(total, total, Resources.Custom_folder_db_sync_progress_single_label);
        persistStatuses(
            [.. projections],
            PlaylistCustomFolderOutputOwner.CreatePhysicalSurfaceFromSyncItems(materialization.SyncItems));
        stopwatch.Stop();

        Lr2SongDbSyncPreparedDataSurface preparedDataSurface = buildPreparedDataSurface
            ? Lr2SongDbSyncPreparedDataSurface.FromSyncItems(
                materialization.Lr2FolderSurfaceScopeDirectories,
                materialization.SyncItems,
                directoryEntries: materialization.DirectoryEntries,
                discoveryComplete: projectionFailedCount == 0)
            : Lr2SongDbSyncPreparedDataSurface.Empty;
        logPerformance?.Invoke((operation ?? "playlist_custom_folder_output")
            + " done reason=" + (reason ?? "unknown")
            + " tableCount=" + tableCount
            + " projectionFailedCount=" + projectionFailedCount
            + " reOutputCount=" + projections.Count
            + " syncUpserted=" + (syncResult?.UpsertedCount ?? 0)
            + " syncDeleted=" + (syncResult?.DeletedCount ?? 0)
            + " elapsedMs=" + stopwatch.ElapsedMilliseconds);
        return Task.FromResult(new CustomFolderBatchOutputResult
        {
            ReOutputCount = projections.Count,
            Materialization = materialization,
            SyncResult = syncResult,
            PreparedDataSurface = preparedDataSurface,
            ElapsedMs = stopwatch.ElapsedMilliseconds
        });
    }

    private PlaylistCustomFolderOutputOwner.CustomFolderOutputProjection CreateProjection(
        BMSTable table,
        CustomFolderOutputSettingsSnapshot settings,
        string outputDirectoryOverride = null)
    {
        if (table?.ReaderWriterLock?.IsWriteLockHeld == true || table?.ReaderWriterLock?.IsReadLockHeld == true)
        {
            return outputOwner.CreateProjection(table, outputDirectoryOverride: outputDirectoryOverride, settings: settings);
        }

        using (table.ReaderWriterLock.GetReaderGuard())
        {
            return outputOwner.CreateProjection(table, outputDirectoryOverride: outputDirectoryOverride, settings: settings);
        }
    }

    private CustomFolderOutputSettingsSnapshot GetSettings()
    {
        return settingsProvider()
            ?? throw new InvalidOperationException("Custom-folder output settings provider returned null.");
    }

    private string ResolveOutputDirectory(BMSTable table, CustomFolderOutputSettingsSnapshot settings)
    {
        return outputDirectoryResolver(table, settings);
    }

    private IReadOnlyCollection<string> CreateKnownOutputDirectories(CustomFolderOutputSettingsSnapshot settings)
    {
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (BMSTable table in tablesSnapshotProvider() ?? [])
        {
            if (table == null || string.IsNullOrWhiteSpace(table.Output_dir))
            {
                continue;
            }
            try
            {
                AddOutputDirectory(directories, ResolveOutputDirectory(table, settings));
            }
            catch (Exception ex) when (ex is ArgumentNullException || ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                // Invalid legacy output paths are not a reason to block an unrelated migration.
            }
        }
        return [.. directories];
    }

    private IReadOnlyList<string> CreateRootCustomFolderOutputDirectories(string rootOutputBaseDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootOutputBaseDirectory))
        {
            return [];
        }
        return [.. (tablesSnapshotProvider() ?? [])
            .Where(table => table?.is_root_folder == true && !string.IsNullOrWhiteSpace(table.Output_dir))
            .Select(table => Path.Combine(rootOutputBaseDirectory, table.Output_dir))];
    }

    private static void EnsureCustomFolderOutputBaseSearchRoots(
        ICollection<string> directories,
        string normalOutputBase,
        string serializedAdditionalOutputBases)
    {
        EnsureCustomFolderOutputBaseSearchRoot(directories, normalOutputBase);
        foreach (string additionalOutputBase in CustomFolderOutputBaseRegistry.DeserializeBaseDirectories(serializedAdditionalOutputBases))
        {
            EnsureCustomFolderOutputBaseSearchRoot(directories, additionalOutputBase);
        }
    }

    private static void EnsureCustomFolderOutputBaseSearchRoot(ICollection<string> directories, string outputBase)
    {
        string normalizedOutputBase = CustomFolderOutputBaseRegistry.NormalizeDirectoryPath(outputBase);
        if (directories == null
            || string.IsNullOrWhiteSpace(normalizedOutputBase)
            || directories.Any(directory => CustomFolderOutputBaseSearchRootSyncService.IsSameOrNestedDirectory(
                normalizedOutputBase,
                CustomFolderOutputBaseRegistry.NormalizeDirectoryPath(directory))))
        {
            return;
        }

        LongPathFileSystem.CreateDirectory(normalizedOutputBase);
        directories.Add(normalizedOutputBase);
    }

    private static bool IsRootCustomFolderOutputSearchRootAdoptionRemovalTarget(
        string registeredPath,
        string currentRootBase,
        IReadOnlyList<string> currentRootDirectories)
    {
        if (string.IsNullOrWhiteSpace(registeredPath)
            || currentRootDirectories.Contains(registeredPath, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(currentRootBase)
            && CustomFolderOutputBaseSearchRootSyncService.IsSameOrNestedDirectory(registeredPath, currentRootBase))
        {
            return true;
        }

        return currentRootDirectories.Any(currentRootDirectory =>
            !CustomFolderOutputBaseSearchRootSyncService.IsSameDirectory(registeredPath, currentRootDirectory)
            && CustomFolderOutputBaseSearchRootSyncService.IsSameOrNestedDirectory(registeredPath, currentRootDirectory));
    }

    private static void AddOutputDirectory(ISet<string> directories, string directory)
    {
        string normalized = Lr2FolderPath.NormalizeDirectoryPath(directory);
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            directories?.Add(normalized);
        }
    }

    private static bool IsSameDirectory(string left, string right)
    {
        string normalizedLeft = Lr2FolderPath.NormalizeDirectoryPath(left);
        string normalizedRight = Lr2FolderPath.NormalizeDirectoryPath(right);
        return !string.IsNullOrWhiteSpace(normalizedLeft)
            && !string.IsNullOrWhiteSpace(normalizedRight)
            && string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyCollection<string> CreateManagedOutputBaseScopes(
        BMSTable table,
        bool wasRootFolder,
        string rootOutputBaseDirectoryBefore,
        string outputBaseDirectoryBefore,
        bool inferOutputBaseDirectoryBeforeWhenMissing,
        CustomFolderOutputSettingsSnapshot settings)
    {
        string outputBaseDirectory = !string.IsNullOrWhiteSpace(outputBaseDirectoryBefore)
            ? outputBaseDirectoryBefore
            : inferOutputBaseDirectoryBeforeWhenMissing
                ? wasRootFolder
                    ? rootOutputBaseDirectoryBefore ?? settings.LR2CustomFolderOutputBaseDirRootType
                    : CustomFolderOutputBaseRegistry.TryResolveNormalOutputBaseDirectory(
                        table?.custom_folder_output_base_name,
                        settings.LR2CustomFolderOutputBaseDir,
                        settings.LR2CustomFolderAdditionalOutputBaseDirs,
                        out string resolvedBase)
                            ? resolvedBase
                            : null
                : null;
        return string.IsNullOrWhiteSpace(outputBaseDirectory) ? [] : [outputBaseDirectory];
    }

    private static IReadOnlyCollection<string> CreateDirectoryPruneScopes(
        string physicalDirectory,
        bool wasRootFolder,
        string rootCustomFolderOutputBaseDirectory,
        CustomFolderOutputSettingsSnapshot settings)
    {
        var scopes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddPruneScope(scopes, physicalDirectory);
        if (wasRootFolder)
        {
            Lr2FolderFileSourceClassification classification = Lr2FolderFileSourceClassifier.Classify(new Lr2FolderFileSourceClassificationRequest
            {
                FilePath = physicalDirectory,
                Lr2RootPath = settings.LR2RootPath,
                RootCustomFolderOutputBaseDir = rootCustomFolderOutputBaseDirectory ?? settings.LR2CustomFolderOutputBaseDirRootType
            });
            AddPruneScope(scopes, classification.DatabasePath);
        }
        return [.. scopes];
    }

    private static void AddPruneScope(ISet<string> scopes, string directory)
    {
        string normalized = Lr2FolderFileProjection.NormalizeDatabasePath(directory);
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            scopes?.Add(normalized);
        }
    }

    private void SyncPrunedRows(
        IReadOnlyCollection<string> directoryScopes,
        IReadOnlyCollection<string> excludedDirectories = null,
        IReadOnlyCollection<string> excludedPaths = null)
    {
        if (directoryScopes == null || directoryScopes.Count == 0)
        {
            return;
        }
        syncMaterialization(new CustomFolderBatchMaterializationRequest
        {
            OutputRowScopeDirectories = directoryScopes,
            PruneExcludedDirectories = excludedDirectories ?? [],
            PruneExcludedPaths = excludedPaths ?? []
        });
    }

    private static IReadOnlyCollection<string> CreatePruneExcludedDirectories(
        IEnumerable<string> protectedOutputDirectories,
        IEnumerable<string> protectedScopeDirectories,
        IEnumerable<BMSTable> classificationTables,
        CustomFolderOutputSettingsSnapshot settings)
    {
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string directory in protectedOutputDirectories ?? [])
        {
            if (protectedScopeDirectories != null
                && !protectedScopeDirectories.Any(scope => !string.IsNullOrWhiteSpace(scope)
                    && Lr2FolderPath.IsSameOrDescendant(
                        Lr2FolderPath.NormalizeDirectoryPath(directory),
                        Lr2FolderPath.NormalizeDirectoryPath(scope))))
            {
                continue;
            }
            AddPruneExcludedVariants(excluded, directory, classificationTables, settings);
        }
        return [.. excluded];
    }

    private static IReadOnlyCollection<string> CreatePruneExcludedPaths(
        IEnumerable<string> protectedOutputDirectories,
        IEnumerable<string> scopeDirectories,
        IEnumerable<BMSTable> classificationTables,
        CustomFolderOutputSettingsSnapshot settings)
    {
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string scope in scopeDirectories ?? [])
        {
            string normalizedScope = Lr2FolderPath.NormalizeDirectoryPath(scope);
            if (string.IsNullOrWhiteSpace(normalizedScope))
            {
                continue;
            }
            foreach (string protectedDirectory in protectedOutputDirectories ?? [])
            {
                string normalizedProtected = Lr2FolderPath.NormalizeDirectoryPath(protectedDirectory);
                if (string.IsNullOrWhiteSpace(normalizedProtected)
                    || string.Equals(normalizedProtected, normalizedScope, StringComparison.OrdinalIgnoreCase)
                    || !Lr2FolderPath.IsSameOrDescendant(normalizedProtected, normalizedScope))
                {
                    continue;
                }
                string current = normalizedProtected;
                while (!string.IsNullOrWhiteSpace(current)
                    && Lr2FolderPath.IsSameOrDescendant(current, normalizedScope))
                {
                    AddPruneExcludedVariants(excluded, current, classificationTables, settings);
                    if (string.Equals(current, normalizedScope, StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }
                    current = Lr2FolderPath.SafeGetDirectoryName(current);
                }
            }
        }
        return [.. excluded];
    }

    private static void AddPruneExcludedVariants(
        ISet<string> paths,
        string directory,
        IEnumerable<BMSTable> classificationTables,
        CustomFolderOutputSettingsSnapshot settings)
    {
        paths?.Add(directory);
        string rowPath = Lr2FolderPath.ToFolderPath(directory);
        paths?.Add(rowPath);
        foreach (BMSTable table in classificationTables ?? [])
        {
            Lr2FolderFileSourceClassification classification = Lr2FolderFileSourceClassifier.Classify(new Lr2FolderFileSourceClassificationRequest
            {
                FilePath = rowPath ?? directory,
                Lr2RootPath = settings.LR2RootPath,
                RootCustomFolderOutputBaseDir = settings.LR2CustomFolderOutputBaseDirRootType
            });
            paths?.Add(classification.DatabasePath);
        }
    }

    private static bool DeleteOutputDirectoryTree(
        string targetDirectory,
        IReadOnlyCollection<string> managedOutputBaseDirectories,
        IReadOnlyCollection<string> protectedOutputDirectories = null)
    {
        return DeleteOutputDirectoryTree(
            targetDirectory,
            managedOutputBaseDirectories,
            protectedOutputDirectories,
            out _);
    }

    private static bool DeleteOutputDirectoryTree(
        string targetDirectory,
        IReadOnlyCollection<string> managedOutputBaseDirectories,
        IReadOnlyCollection<string> protectedOutputDirectories,
        out bool deletionFailure)
    {
        deletionFailure = false;
        string normalizedTarget = Lr2FolderPath.NormalizeDirectoryPath(targetDirectory);
        if (string.IsNullOrWhiteSpace(normalizedTarget)
            || !(managedOutputBaseDirectories ?? []).Any(baseDirectory =>
                !string.IsNullOrWhiteSpace(baseDirectory)
                && !IsSameDirectory(normalizedTarget, baseDirectory)
                && Lr2FolderPath.IsSameOrDescendant(normalizedTarget, Lr2FolderPath.NormalizeDirectoryPath(baseDirectory))))
        {
            return false;
        }
        if (!LongPathFileSystem.DirectoryExists(targetDirectory))
        {
            return true;
        }

        try
        {
            IReadOnlyCollection<string> protectedDirectories = [.. (protectedOutputDirectories ?? [])
                .Select(Lr2FolderPath.NormalizeDirectoryPath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)];
            if (protectedDirectories.Any(path => string.Equals(path, normalizedTarget, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            IReadOnlyCollection<string> protectedDescendants = [.. protectedDirectories
                .Where(path => Lr2FolderPath.IsSameOrDescendant(path, normalizedTarget)
                    && !string.Equals(path, normalizedTarget, StringComparison.OrdinalIgnoreCase))];
            if (protectedDescendants.Count == 0)
            {
                LongPathFileSystem.DeleteDirectory(targetDirectory, recursive: true);
                return true;
            }

            foreach (string filePath in LongPathFileSystem.EnumerateFiles(targetDirectory, "*", SearchOption.AllDirectories).OrderByDescending(path => path.Length))
            {
                if (!IsPathUnderAnyDirectory(filePath, protectedDescendants))
                {
                    LongPathFileSystem.DeleteFile(filePath);
                }
            }
            foreach (string directoryPath in LongPathFileSystem.EnumerateDirectories(targetDirectory, "*", SearchOption.AllDirectories).OrderByDescending(path => path.Length))
            {
                if (IsPathUnderAnyDirectory(directoryPath, protectedDescendants)
                    || LongPathFileSystem.EnumerateFileSystemEntries(directoryPath).Any())
                {
                    continue;
                }
                LongPathFileSystem.DeleteDirectory(directoryPath, recursive: false);
            }

            IReadOnlyList<string> remainingEntries = [.. LongPathFileSystem.EnumerateFileSystemEntries(targetDirectory)];
            if (remainingEntries.Count == 0)
            {
                LongPathFileSystem.DeleteDirectory(targetDirectory, recursive: false);
                return true;
            }
            bool onlyProtectedEntriesRemain = remainingEntries.All(path => IsPathUnderAnyDirectory(path, protectedDescendants));
            if (!onlyProtectedEntriesRemain)
            {
                deletionFailure = true;
            }
            return onlyProtectedEntriesRemain;
        }
        catch
        {
            deletionFailure = true;
            return false;
        }
    }

    private static bool IsPathUnderAnyDirectory(string path, IEnumerable<string> directories)
    {
        string normalizedPath = Lr2FolderPath.NormalizeDirectoryPath(path);
        return !string.IsNullOrWhiteSpace(normalizedPath)
            && (directories ?? []).Any(directory =>
                !string.IsNullOrWhiteSpace(directory)
                && Lr2FolderPath.IsSameOrDescendant(normalizedPath, Lr2FolderPath.NormalizeDirectoryPath(directory)));
    }

    private static void DeleteEmptyOutputDirectory(string outputDirectory)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory)
            || !LongPathFileSystem.DirectoryExists(outputDirectory)
            || LongPathFileSystem.EnumerateFileSystemEntries(outputDirectory).Any())
        {
            return;
        }
        LongPathFileSystem.DeleteDirectory(outputDirectory, recursive: false);
    }

    private static int GetProgressTotal(int tableCount, Action<int, int, string> progressCallback)
    {
        return progressCallback == null ? tableCount : Math.Max(tableCount, 0) * 2 + 1;
    }

    private static bool IsProjectionFailure(Exception exception)
    {
        return exception is IOException
            || exception is UnauthorizedAccessException
            || exception is ArgumentException
            || exception is NotSupportedException
            || exception is PathTooLongException
            || exception is SQLite.SQLiteException;
    }

    private sealed class MigrationPlan
    {
        internal BMSTable Table { get; init; }

        internal string OutputDirectoryBefore { get; init; }

        internal string OutputDirectoryAfter { get; init; }

        internal bool WasRootFolderBefore { get; init; }

        internal string RootOutputBaseDirectoryBefore { get; init; }

        internal string OutputBaseDirectoryBefore { get; init; }

        internal bool InferOutputBaseDirectoryBeforeWhenMissing { get; init; }
    }

    internal sealed class CustomFolderBatchMaterializationRequest
    {
        internal CustomFolderBatchMaterializationRequest()
        {
        }

        internal CustomFolderBatchMaterializationRequest(PlaylistCustomFolderOutputOwner.CustomFolderBatchMaterializationResult result)
        {
            OutputDirectories = result?.OutputDirectories ?? [];
            OutputRowScopeDirectories = result?.OutputRowScopeDirectories ?? [];
            Items = result?.SyncItems ?? [];
            DirectoryRowGenerationScopeDirectories = result?.DirectoryRowGenerationScopeDirectories ?? [];
            DirectoryEntries = result?.DirectoryEntries;
            PruneScopePaths = result?.PruneScopePaths ?? [];
            PruneExcludedDirectories = result?.PruneExcludedDirectories ?? [];
            EmptyOutputDirectories = result?.EmptyOutputDirectories ?? [];
        }

        internal IReadOnlyCollection<string> OutputDirectories { get; init; } = [];

        internal IReadOnlyCollection<string> OutputRowScopeDirectories { get; init; } = [];

        internal IReadOnlyCollection<Lr2FolderFileSyncItem> Items { get; init; } = [];

        internal IReadOnlyCollection<string> DirectoryRowGenerationScopeDirectories { get; init; } = [];

        internal IReadOnlyDictionary<string, RootFileEnumerationEntry> DirectoryEntries { get; init; }

        internal IReadOnlyCollection<string> PruneScopePaths { get; init; } = [];

        internal IReadOnlyCollection<string> PruneExcludedDirectories { get; init; } = [];

        internal IReadOnlyCollection<string> PruneExcludedPaths { get; init; } = [];

        internal IReadOnlyCollection<string> EmptyOutputDirectories { get; init; } = [];
    }

    internal sealed class CustomFolderBatchOutputResult
    {
        internal int ReOutputCount { get; init; }

        internal PlaylistCustomFolderOutputOwner.CustomFolderBatchMaterializationResult Materialization { get; init; }

        internal Lr2FolderFileDbSyncResult SyncResult { get; init; }

        internal Lr2SongDbSyncPreparedDataSurface PreparedDataSurface { get; init; }

        internal long ElapsedMs { get; init; }
    }
}
