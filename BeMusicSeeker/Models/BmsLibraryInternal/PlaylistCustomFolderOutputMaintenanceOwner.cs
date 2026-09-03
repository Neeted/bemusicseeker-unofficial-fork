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

    private readonly PlaylistCustomFolderOutputStatusOwner statusOwner;

    private readonly Func<BMSTable, CustomFolderOutputSettingsSnapshot, string> outputDirectoryResolver;

    private readonly Action<string> logPerformance;

    private readonly PlaylistOperationNotificationOwner notificationOwner;

    private readonly Func<IEnumerable<string>, string, CustomFolderOutputPhysicalSurface> physicalSurfaceResolver;

    private readonly Action<IReadOnlyCollection<PlaylistCustomFolderOutputOwner.CustomFolderOutputProjection>, CustomFolderOutputSettingsSnapshot>
        prepareOutputScopes;

    private readonly Func<IReadOnlyCollection<BMSTable>, bool> areCurrentTables;

    internal PlaylistCustomFolderOutputMaintenanceOwner(
        PlaylistCustomFolderOutputOwner outputOwner,
        Func<CustomFolderOutputSettingsSnapshot> settingsProvider,
        Func<IReadOnlyList<BMSTable>> tablesSnapshotProvider,
        Action<BMSTable, string> ensureEntriesLoaded,
        Func<LR2Config> lr2ConfigProvider,
        PlaylistCustomFolderOutputStatusOwner statusOwner,
        Func<BMSTable, CustomFolderOutputSettingsSnapshot, string> outputDirectoryResolver,
        Action<string> logPerformance,
        PlaylistOperationNotificationOwner notificationOwner,
        Func<IEnumerable<string>, string, CustomFolderOutputPhysicalSurface> physicalSurfaceResolver,
        Action<IReadOnlyCollection<PlaylistCustomFolderOutputOwner.CustomFolderOutputProjection>, CustomFolderOutputSettingsSnapshot> prepareOutputScopes,
        Func<IReadOnlyCollection<BMSTable>, bool> areCurrentTables)
    {
        this.outputOwner = outputOwner ?? throw new ArgumentNullException(nameof(outputOwner));
        this.settingsProvider = settingsProvider ?? throw new ArgumentNullException(nameof(settingsProvider));
        this.tablesSnapshotProvider = tablesSnapshotProvider ?? throw new ArgumentNullException(nameof(tablesSnapshotProvider));
        this.ensureEntriesLoaded = ensureEntriesLoaded ?? throw new ArgumentNullException(nameof(ensureEntriesLoaded));
        this.lr2ConfigProvider = lr2ConfigProvider ?? throw new ArgumentNullException(nameof(lr2ConfigProvider));
        this.statusOwner = statusOwner ?? throw new ArgumentNullException(nameof(statusOwner));
        this.outputDirectoryResolver = outputDirectoryResolver ?? throw new ArgumentNullException(nameof(outputDirectoryResolver));
        this.logPerformance = logPerformance;
        this.notificationOwner = notificationOwner ?? throw new ArgumentNullException(nameof(notificationOwner));
        this.physicalSurfaceResolver = physicalSurfaceResolver;
        this.prepareOutputScopes = prepareOutputScopes;
        this.areCurrentTables = areCurrentTables ?? throw new ArgumentNullException(nameof(areCurrentTables));
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

    /// <summary>
    /// Hydrates the target and builds the immutable custom-folder projection before
    /// the file-mutation lease is acquired.
    /// </summary>
    internal CustomFolderOutputPreparation PrepareCustomFolderOutput(
        BMSTable table,
        CustomFolderOutputSettingsSnapshot settings = null)
    {
        settings ??= GetSettings();
        if (table == null)
        {
            return new CustomFolderOutputPreparation([], [], settings, 0);
        }

        ensureEntriesLoaded(table, "ReOutputCustomFolder");
        PlaylistCustomFolderOutputOwner.CustomFolderOutputProjection projection = CreateProjection(table, settings);
        PlaylistCustomFolderOutputOwner.MarkAllFilesForWrite(projection);
        PrepareProjectionSurfaceAndScopes([projection], settings, "ReOutputCustomFolder");
        return new CustomFolderOutputPreparation([table], [projection], settings, 0);
    }

    /// <summary>
    /// Executes a projection prepared before admission and delegates the nested
    /// LR2 row synchronization to the active lease owner.
    /// </summary>
    internal CustomFolderBatchOutputResult TryReOutputPreparedCustomFolder(
        CustomFolderOutputPreparation preparation,
        Func<CustomFolderBatchMaterializationRequest, Lr2FolderFileDbSyncResult> syncMaterialization)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        ArgumentNullException.ThrowIfNull(syncMaterialization);
        ValidateCurrentTables(preparation.Tables);
        try
        {
            CustomFolderBatchOutputResult result = ReOutputProjectionsAsync(
                preparation.Projections,
                tableCount: preparation.TableCount,
                reason: "ReOutputCustomFolder",
                operation: "playlist_custom_folder_output_single",
                buildPreparedDataSurface: false,
                yieldBetweenTables: false,
                settings: preparation.Settings,
                syncMaterialization: syncMaterialization)
                .GetAwaiter()
                .GetResult();
            if (result.HasUnverifiedFiles)
            {
                result.WarningMessage = string.Format(
                    Resources.Warn_CustomFolderOutputFailed,
                    preparation.Tables.FirstOrDefault()?.name,
                    preparation.Projections.FirstOrDefault()?.OutputDirectory);
                result.Succeeded = false;
                return result;
            }
            DeleteEmptyOutputDirectory(preparation.Projections.FirstOrDefault()?.OutputDirectory);
            return result;
        }
        catch (Exception ex)
        {
            return new CustomFolderBatchOutputResult
            {
                WarningMessage = string.Format(
                    Resources.Warn_CustomFolderOutputFailed,
                    preparation.Tables.FirstOrDefault()?.name,
                    preparation.Projections.FirstOrDefault()?.OutputDirectory),
                PrimaryException = ex
            };
        }
    }

    /// <summary>
    /// Hydrates a single table and builds the destination projection before
    /// admission.  No filesystem or LR2 row mutation is performed here.
    /// </summary>
    internal CustomFolderMigrationPreparation PrepareCustomFolderOutputMigration(
        BMSTable table,
        string outputDirectoryBefore,
        string outputDirectoryAfter,
        bool wasRootFolderBefore,
        string rootOutputBaseDirectoryBefore,
        string outputBaseDirectoryBefore,
        bool inferOutputBaseDirectoryBeforeWhenMissing,
        CustomFolderOutputSettingsSnapshot settings = null)
    {
        settings ??= GetSettings();
        if (table == null)
        {
            return new CustomFolderMigrationPreparation([], settings, []);
        }

        ensureEntriesLoaded(table, "MigrateCustomFolderOutputDirectory");
        string resolvedOutputDirectoryAfter = string.IsNullOrWhiteSpace(outputDirectoryAfter)
            ? ResolveOutputDirectory(table, settings)
            : outputDirectoryAfter;
        var plan = new MigrationPlan
        {
            Table = table,
            OutputDirectoryBefore = outputDirectoryBefore,
            OutputDirectoryAfter = resolvedOutputDirectoryAfter,
            WasRootFolderBefore = wasRootFolderBefore,
            RootOutputBaseDirectoryBefore = rootOutputBaseDirectoryBefore,
            OutputBaseDirectoryBefore = outputBaseDirectoryBefore,
            InferOutputBaseDirectoryBeforeWhenMissing = inferOutputBaseDirectoryBeforeWhenMissing
        };
        plan.SameDirectory = IsSameDirectory(outputDirectoryBefore, resolvedOutputDirectoryAfter);
        plan.Projection = CreateProjection(table, settings, resolvedOutputDirectoryAfter);
        if (plan.SameDirectory)
        {
            PlaylistCustomFolderOutputOwner.MarkAllFilesForWrite(plan.Projection);
        }
        PrepareProjectionSurfaceAndScopes([plan.Projection], settings, "MigrateCustomFolderOutputDirectory");
        IReadOnlyCollection<string> protectedDirectories = CreateMigrationProtectedOutputDirectories(
            [resolvedOutputDirectoryAfter],
            settings,
            [outputDirectoryBefore]);
        return new CustomFolderMigrationPreparation([plan], settings, protectedDirectories);
    }

    /// <summary>
    /// Executes a prepared single-table migration under the active capability.
    /// Current membership is checked immediately before any filesystem work.
    /// </summary>
    internal CustomFolderBatchOutputResult TryMigratePreparedCustomFolderOutputDirectory(
        CustomFolderMigrationPreparation preparation,
        Func<CustomFolderBatchMaterializationRequest, Lr2FolderFileDbSyncResult> syncMaterialization)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        ArgumentNullException.ThrowIfNull(syncMaterialization);
        ValidateCurrentTables(preparation.Tables);
        MigrationPlan plan = preparation.Plans.FirstOrDefault();
        if (plan == null)
        {
            return new CustomFolderBatchOutputResult();
        }

        try
        {
            CustomFolderBatchOutputResult result = ReOutputProjectionsAsync(
                [plan.Projection],
                tableCount: 1,
                reason: "MigrateCustomFolderOutputDirectory",
                operation: "playlist_custom_folder_output_migrate",
                buildPreparedDataSurface: false,
                yieldBetweenTables: false,
                settings: preparation.Settings,
                syncMaterialization: syncMaterialization)
                .GetAwaiter()
                .GetResult();
            if (result.HasUnverifiedFiles)
            {
                result.WarningMessage = string.Format(
                    Resources.Warn_CustomFolderOutputFailed,
                    plan.Table?.name,
                    plan.OutputDirectoryAfter);
                result.Succeeded = false;
                return result;
            }

            if (plan.SameDirectory)
            {
                DeleteEmptyOutputDirectory(plan.OutputDirectoryAfter);
                return result;
            }

            return CompletePreparedMigration(preparation, result, syncMaterialization);
        }
        catch (Exception ex)
        {
            return new CustomFolderBatchOutputResult
            {
                WarningMessage = string.Format(
                    Resources.Warn_CustomFolderOutputFailed,
                    plan.Table?.name,
                    plan.OutputDirectoryAfter),
                PrimaryException = ex
            };
        }
    }

    internal CustomFolderBatchOutputResult TryRemoveCustomFolder(
        BMSTable table,
        string outputDirectory,
        bool wasRootFolder,
        string rootOutputBaseDirectory,
        string outputBaseDirectory,
        CustomFolderOutputSettingsSnapshot settings,
        Func<CustomFolderBatchMaterializationRequest, Lr2FolderFileDbSyncResult> syncMaterialization)
    {
        ArgumentNullException.ThrowIfNull(syncMaterialization);
        settings ??= GetSettings();
        ValidateCurrentTables([table]);
        bool deletionFailure;
        if (!DeleteOutputDirectoryTree(outputDirectory, [outputBaseDirectory], null, out deletionFailure))
        {
            if (deletionFailure)
            {
                return new CustomFolderBatchOutputResult
                {
                    WarningMessage = string.Format(Resources.Warn_FileOrDirDeleteFailed, outputDirectory),
                    Succeeded = false
                };
            }
            return new CustomFolderBatchOutputResult { Succeeded = false };
        }

        try
        {
            SyncPrunedRows(
                syncMaterialization,
                CreateDirectoryPruneScopes(outputDirectory, wasRootFolder, rootOutputBaseDirectory, settings));
            statusOwner.DeleteStatus(table);
            return new CustomFolderBatchOutputResult { ReOutputCount = 1, Succeeded = true };
        }
        catch (Exception ex)
        {
            return new CustomFolderBatchOutputResult
            {
                WarningMessage = string.Format(Resources.Warn_FileOrDirDeleteFailed, outputDirectory),
                PrimaryException = ex,
                Succeeded = false
            };
        }
    }

    /// <summary>
    /// Builds all migration projections and the protected-directory snapshot before
    /// the file-mutation lease is acquired.
    /// </summary>
    internal CustomFolderMigrationPreparation PrepareCustomFolderOutputMigrations(
        IReadOnlyList<BMSTable> tables,
        IReadOnlyDictionary<BMSTable, string> outputDirectoriesBefore,
        IReadOnlyDictionary<BMSTable, bool> wasRootFolderBeforeByTable,
        string rootOutputBaseDirectoryBefore,
        IReadOnlyDictionary<BMSTable, string> outputBaseDirectoryBeforeByTable,
        CustomFolderOutputSettingsSnapshot settings = null)
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

            ensureEntriesLoaded(table, "MigrateCustomFolderOutputDirectories");
            string afterDirectory = ResolveOutputDirectory(table, settings);
            if (string.IsNullOrWhiteSpace(afterDirectory) || IsSameDirectory(beforeDirectory, afterDirectory))
            {
                continue;
            }

            var plan = new MigrationPlan
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
                InferOutputBaseDirectoryBeforeWhenMissing = outputBaseDirectoryBeforeByTable == null,
                SameDirectory = false
            };
            plan.Projection = CreateProjection(table, settings, afterDirectory);
            PlaylistCustomFolderOutputOwner.MarkAllFilesForWrite(plan.Projection);
            plans.Add(plan);
        }
        if (plans.Count == 0)
        {
            return new CustomFolderMigrationPreparation([], settings, []);
        }

        PrepareProjectionSurfaceAndScopes(
            plans.Select(plan => plan.Projection).ToArray(),
            settings,
            "MigrateCustomFolderOutputDirectories");
        IReadOnlyCollection<string> protectedDirectories = CreateMigrationProtectedOutputDirectories(
            plans.Select(plan => plan.OutputDirectoryAfter),
            settings,
            plans.Select(plan => plan.OutputDirectoryBefore));
        return new CustomFolderMigrationPreparation(plans, settings, protectedDirectories);
    }

    /// <summary>
    /// Applies prepared migration projections and prunes the old output tree while
    /// holding the explicit mutation capability.  Progress and warnings are facts
    /// returned to the caller for post-lease publication.
    /// </summary>
    internal CustomFolderBatchOutputResult MigratePreparedCustomFolderOutputDirectories(
        CustomFolderMigrationPreparation preparation,
        string reason,
        Func<CustomFolderBatchMaterializationRequest, Lr2FolderFileDbSyncResult> syncMaterialization)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        ArgumentNullException.ThrowIfNull(syncMaterialization);
        ValidateCurrentTables(preparation.Tables);
        if (preparation.Plans.Count == 0)
        {
            return new CustomFolderBatchOutputResult();
        }

        CustomFolderBatchOutputResult result = ReOutputProjectionsAsync(
            preparation.Plans.Select(plan => plan.Projection).ToArray(),
            preparation.Plans.Count,
            reason,
            "playlist_custom_folder_output_migrate_bulk",
            buildPreparedDataSurface: false,
            yieldBetweenTables: false,
            syncMaterialization: syncMaterialization,
            progressCallback: null,
            settings: preparation.Settings,
            stopwatch: Stopwatch.StartNew())
            .GetAwaiter()
            .GetResult();
        if (result.HasUnverifiedFiles)
        {
            throw new InvalidOperationException(
                "Custom-folder output could not verify one or more existing files before migration.");
        }

        var pruneScopes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (MigrationPlan plan in preparation.Plans)
        {
            IReadOnlyCollection<string> managedBases = CreateManagedOutputBaseScopes(
                plan.Table,
                plan.WasRootFolderBefore,
                plan.RootOutputBaseDirectoryBefore,
                plan.OutputBaseDirectoryBefore,
                plan.InferOutputBaseDirectoryBeforeWhenMissing,
                preparation.Settings);
            bool deletionFailure;
            if (DeleteOutputDirectoryTree(
                plan.OutputDirectoryBefore,
                managedBases,
                preparation.ProtectedDirectories,
                out deletionFailure))
            {
                foreach (string scope in CreateDirectoryPruneScopes(
                    plan.OutputDirectoryBefore,
                    plan.WasRootFolderBefore,
                    plan.RootOutputBaseDirectoryBefore,
                    preparation.Settings))
                {
                    pruneScopes.Add(scope);
                }
            }
            else
            {
                result.Succeeded = false;
                if (deletionFailure)
                {
                    result.WarningMessage = string.Format(
                        Resources.Warn_FileOrDirDeleteFailed,
                        plan.OutputDirectoryBefore);
                }
            }
        }

        if (pruneScopes.Count > 0)
        {
            SyncPrunedRows(
                syncMaterialization,
                [.. pruneScopes],
                CreatePruneExcludedDirectories(
                    preparation.ProtectedDirectories,
                    preparation.Plans.Select(plan => plan.OutputDirectoryBefore),
                    preparation.Plans.Select(plan => plan.Table),
                    preparation.Settings),
                CreatePruneExcludedPaths(
                    preparation.ProtectedDirectories,
                    preparation.Plans.Select(plan => plan.OutputDirectoryBefore),
                    preparation.Plans.Select(plan => plan.Table),
                    preparation.Settings));
        }
        return result;
    }

    /// <summary>
    /// Builds a target-table projection and hydrates entries before lease admission.
    /// </summary>
    internal CustomFolderOutputPreparation PrepareTables(
        IReadOnlyList<BMSTable> tables,
        string operation,
        bool forceWriteAllFiles,
        bool throwOnProjectionFailure,
        CustomFolderOutputSettingsSnapshot settings = null)
    {
        settings ??= GetSettings();
        tables ??= [];
        var projections = new List<PlaylistCustomFolderOutputOwner.CustomFolderOutputProjection>();
        var preparedTables = new List<BMSTable>();
        int failedCount = 0;
        foreach (BMSTable table in tables)
        {
            if (table == null || string.IsNullOrWhiteSpace(table.Output_dir))
            {
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
                preparedTables.Add(table);
                projections.Add(projection);
            }
            catch (Exception ex) when (IsProjectionFailure(ex))
            {
                failedCount++;
                if (throwOnProjectionFailure)
                {
                    ExceptionDispatchInfo.Capture(ex).Throw();
                }
            }
        }

        PrepareProjectionSurfaceAndScopes(projections, settings, operation ?? "ReOutputCustomFolders");
        return new CustomFolderOutputPreparation(preparedTables, projections, settings, failedCount);
    }

    /// <summary>
    /// Completes the physical-surface and protected-scope portion of an existing
    /// projection before the mutation lease is acquired.  Repair projections are
    /// assembled by a separate planner, so they cannot use <see cref="PrepareTables"/>.
    /// </summary>
    internal void PrepareOutputProjections(
        IReadOnlyCollection<PlaylistCustomFolderOutputOwner.CustomFolderOutputProjection> projections,
        CustomFolderOutputSettingsSnapshot settings,
        string reason)
    {
        PrepareProjectionSurfaceAndScopes(projections, settings ?? GetSettings(), reason);
    }

    /// <summary>
    /// Materializes a previously prepared table projection under the active lease.
    /// </summary>
    internal Task<CustomFolderBatchOutputResult> ReOutputPreparedTablesAsync(
        CustomFolderOutputPreparation preparation,
        string reason,
        string operation,
        bool buildPreparedDataSurface,
        bool yieldBetweenTables,
        Func<CustomFolderBatchMaterializationRequest, Lr2FolderFileDbSyncResult> syncMaterialization,
        Action<int, int, string> progressCallback = null)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        ArgumentNullException.ThrowIfNull(syncMaterialization);
        ValidateCurrentTables(preparation.Tables);
        return ReOutputProjectionsAsync(
            preparation.Projections,
            preparation.TableCount,
            reason,
            operation,
            buildPreparedDataSurface,
            yieldBetweenTables,
            syncMaterialization,
            progressCallback,
            preparation.Settings,
            preparation.ProjectionFailedCount,
            Stopwatch.StartNew());
    }

    internal Task<CustomFolderBatchOutputResult> ReOutputProjectionsAsync(
        IReadOnlyList<PlaylistCustomFolderOutputOwner.CustomFolderOutputProjection> projections,
        int tableCount,
        string reason,
        string operation,
        bool buildPreparedDataSurface,
        bool yieldBetweenTables,
        Func<CustomFolderBatchMaterializationRequest, Lr2FolderFileDbSyncResult> syncMaterialization,
        Action<int, int, string> progressCallback = null,
        CustomFolderOutputSettingsSnapshot settings = null,
        int projectionFailedCount = 0,
        Stopwatch stopwatch = null)
    {
        ArgumentNullException.ThrowIfNull(syncMaterialization);
        return ReOutputProjectionsCoreAsync(
            projections,
            tableCount,
            reason,
            operation,
            buildPreparedDataSurface,
            progressCallback,
            settings,
            projectionFailedCount,
            stopwatch ?? Stopwatch.StartNew(),
            syncMaterialization);
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
        Stopwatch stopwatch,
        Func<CustomFolderBatchMaterializationRequest, Lr2FolderFileDbSyncResult> syncMaterialization)
    {
        projections ??= [];
        settings ??= GetSettings();
        ArgumentNullException.ThrowIfNull(syncMaterialization);
        int total = GetProgressTotal(tableCount, progressCallback);
        progressCallback?.Invoke(
            Math.Min(tableCount, total),
            total,
            Resources.Custom_folder_output_progress_single_label);
        PlaylistCustomFolderOutputOwner.CustomFolderBatchMaterializationResult materialization = outputOwner.MaterializeBatch(
            projections,
            progressCallback: (completed, count, tableName) => progressCallback?.Invoke(
                Math.Min(tableCount + completed, total),
                total,
                tableName ?? Resources.Custom_folder_output_progress_single_label),
            operation: operation,
            reason: reason,
            settingsOverride: settings);
        Lr2FolderFileDbSyncResult syncResult = materialization.HasUnverifiedFiles
            ? null
            : syncMaterialization(new CustomFolderBatchMaterializationRequest(materialization));
        if (!materialization.HasUnverifiedFiles)
        {
            progressCallback?.Invoke(
                total,
                total,
                Resources.Custom_folder_db_sync_progress_single_label);
            statusOwner.PersistStatuses(
                [.. projections],
                PlaylistCustomFolderOutputOwner.CreatePhysicalSurfaceFromSyncItems(materialization.SyncItems));
        }
        stopwatch.Stop();

        Lr2SongDbSyncPreparedDataSurface preparedDataSurface = buildPreparedDataSurface
            ? Lr2SongDbSyncPreparedDataSurface.FromSyncItems(
                materialization.Lr2FolderSurfaceScopeDirectories,
                materialization.SyncItems,
                directoryEntries: materialization.DirectoryEntries,
                discoveryComplete: projectionFailedCount == 0 && !materialization.HasUnverifiedFiles)
            : Lr2SongDbSyncPreparedDataSurface.Empty;
        string performanceMessage = (operation ?? "playlist_custom_folder_output")
            + " done reason=" + (reason ?? "unknown")
            + " tableCount=" + tableCount
            + " projectionFailedCount=" + projectionFailedCount
            + " reOutputCount=" + (materialization.HasUnverifiedFiles ? 0 : projections.Count)
            + " syncUpserted=" + (syncResult?.UpsertedCount ?? 0)
            + " syncDeleted=" + (syncResult?.DeletedCount ?? 0)
            + " unverifiedFileCount=" + materialization.UnverifiedFilePaths.Count
            + " elapsedMs=" + stopwatch.ElapsedMilliseconds;
        return Task.FromResult(new CustomFolderBatchOutputResult
        {
            ReOutputCount = materialization.HasUnverifiedFiles ? 0 : projections.Count,
            Succeeded = !materialization.HasUnverifiedFiles,
            Materialization = materialization,
            SyncResult = syncResult,
            PreparedDataSurface = preparedDataSurface,
            ProgressFact = new CustomFolderProgressFact(
                total,
                total,
                materialization.HasUnverifiedFiles
                    ? Resources.Custom_folder_output_progress_single_label
                    : Resources.Custom_folder_db_sync_progress_single_label),
            PerformanceMessage = performanceMessage,
            ElapsedMs = stopwatch.ElapsedMilliseconds
        });
    }

    private void PrepareProjectionSurfaceAndScopes(
        IReadOnlyCollection<PlaylistCustomFolderOutputOwner.CustomFolderOutputProjection> projections,
        CustomFolderOutputSettingsSnapshot settings,
        string reason)
    {
        if (projections == null || projections.Count == 0)
        {
            return;
        }

        prepareOutputScopes?.Invoke(projections, settings);
        if (physicalSurfaceResolver == null)
        {
            return;
        }

        CustomFolderOutputPhysicalSurface physicalSurface = physicalSurfaceResolver(
            projections.Select(projection => projection?.OutputDirectory),
            reason);
        foreach (PlaylistCustomFolderOutputOwner.CustomFolderOutputProjection projection in projections)
        {
            if (projection == null || projection.PhysicalSurface != null)
            {
                continue;
            }

            projection.PhysicalSurface = physicalSurface;
            if (physicalSurface?.DiscoveryComplete != true)
            {
                PlaylistCustomFolderOutputOwner.MarkAllFilesForWrite(projection);
            }
        }
    }

    private void ValidateCurrentTables(IReadOnlyCollection<BMSTable> tables)
    {
        if (tables == null || tables.Count == 0)
        {
            return;
        }
        if (!areCurrentTables(tables))
        {
            throw new InvalidOperationException(
                "The playlist output target changed before mutation execution.");
        }
    }

    private CustomFolderBatchOutputResult CompletePreparedMigration(
        CustomFolderMigrationPreparation preparation,
        CustomFolderBatchOutputResult result,
        Func<CustomFolderBatchMaterializationRequest, Lr2FolderFileDbSyncResult> syncMaterialization)
    {
        if (preparation == null || result == null)
        {
            return result;
        }

        MigrationPlan plan = preparation.Plans.FirstOrDefault();
        if (plan == null)
        {
            return result;
        }

        IReadOnlyCollection<string> managedBases = CreateManagedOutputBaseScopes(
            plan.Table,
            plan.WasRootFolderBefore,
            plan.RootOutputBaseDirectoryBefore,
            plan.OutputBaseDirectoryBefore,
            plan.InferOutputBaseDirectoryBeforeWhenMissing,
            preparation.Settings);
        bool deletionFailure;
        if (!DeleteOutputDirectoryTree(
            plan.OutputDirectoryBefore,
            managedBases,
            preparation.ProtectedDirectories,
            out deletionFailure))
        {
            result.Succeeded = false;
            if (deletionFailure)
            {
                result.WarningMessage = string.Format(
                    Resources.Warn_FileOrDirDeleteFailed,
                    plan.OutputDirectoryBefore);
            }
            return result;
        }

        SyncPrunedRows(
            syncMaterialization,
            CreateDirectoryPruneScopes(
                plan.OutputDirectoryBefore,
                plan.WasRootFolderBefore,
                plan.RootOutputBaseDirectoryBefore,
                preparation.Settings),
            CreatePruneExcludedDirectories(
                preparation.ProtectedDirectories,
                [plan.OutputDirectoryBefore],
                [plan.Table],
                preparation.Settings),
            CreatePruneExcludedPaths(
                preparation.ProtectedDirectories,
                [plan.OutputDirectoryBefore],
                [plan.Table],
                preparation.Settings));
        return result;
    }

    private static void PublishProgressFact(
        CustomFolderBatchOutputResult result,
        Action<int, int, string> progressCallback)
    {
        if (result?.ProgressFact == null || progressCallback == null)
        {
            return;
        }
        progressCallback(
            result.ProgressFact.ProcessedCount,
            result.ProgressFact.TotalCount,
            result.ProgressFact.Label);
    }

    private void PublishWarning(CustomFolderBatchOutputResult result)
    {
        if (!string.IsNullOrWhiteSpace(result?.WarningMessage))
        {
            notificationOwner.QueueWarning(result.WarningMessage);
        }
    }

    private void PublishPerformance(CustomFolderBatchOutputResult result)
    {
        if (!string.IsNullOrWhiteSpace(result?.PerformanceMessage))
        {
            logPerformance?.Invoke(result.PerformanceMessage);
        }
    }

    /// <summary>
    /// Publishes bounded progress, warning, and performance facts after the
    /// caller has released its file-mutation lease.
    /// </summary>
    internal void PublishPostLeaseResult(
        CustomFolderBatchOutputResult result,
        Action<int, int, string> progressCallback)
    {
        InvokeBestEffort(() => PublishProgressFact(result, progressCallback), "custom_folder_progress_publication_failed");
        InvokeBestEffort(() => PublishWarning(result), "custom_folder_warning_publication_failed");
        InvokeBestEffort(() => PublishPerformance(result), "custom_folder_performance_publication_failed");
        if (result?.PrimaryException != null)
        {
            ExceptionDispatchInfo.Capture(result.PrimaryException).Throw();
        }
    }

    private static void InvokeBestEffort(Action action, string diagnostic)
    {
        if (action == null)
        {
            return;
        }
        try
        {
            action();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(diagnostic + ": " + exception.GetType().Name);
        }
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
        Func<CustomFolderBatchMaterializationRequest, Lr2FolderFileDbSyncResult> syncMaterialization,
        IReadOnlyCollection<string> directoryScopes,
        IReadOnlyCollection<string> excludedDirectories = null,
        IReadOnlyCollection<string> excludedPaths = null)
    {
        ArgumentNullException.ThrowIfNull(syncMaterialization);
        if (directoryScopes == null || directoryScopes.Count == 0)
        {
            return;
        }
        syncMaterialization(new CustomFolderBatchMaterializationRequest
        {
            OutputRowScopeDirectories = directoryScopes,
            PruneExcludedDirectories = excludedDirectories ?? [],
            PruneExcludedPaths = excludedPaths ?? [],
            SuppressLog = true
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

    internal sealed class CustomFolderOutputPreparation
    {
        internal CustomFolderOutputPreparation(
            IEnumerable<BMSTable> tables,
            IEnumerable<PlaylistCustomFolderOutputOwner.CustomFolderOutputProjection> projections,
            CustomFolderOutputSettingsSnapshot settings,
            int projectionFailedCount)
        {
            Tables = [.. (tables ?? []).Where(table => table != null)];
            Projections = [.. (projections ?? []).Where(projection => projection != null)];
            Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            ProjectionFailedCount = Math.Max(0, projectionFailedCount);
        }

        internal IReadOnlyList<BMSTable> Tables { get; }

        internal IReadOnlyList<PlaylistCustomFolderOutputOwner.CustomFolderOutputProjection> Projections { get; }

        internal CustomFolderOutputSettingsSnapshot Settings { get; }

        internal int TableCount => Tables.Count;

        internal int ProjectionFailedCount { get; }
    }

    internal sealed class CustomFolderMigrationPreparation
    {
        internal CustomFolderMigrationPreparation(
            IEnumerable<MigrationPlan> plans,
            CustomFolderOutputSettingsSnapshot settings,
            IEnumerable<string> protectedDirectories)
        {
            Plans = [.. (plans ?? []).Where(plan => plan != null)];
            Tables = [.. Plans.Select(plan => plan.Table).Where(table => table != null)];
            Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            ProtectedDirectories = [.. (protectedDirectories ?? [])
                .Where(directory => !string.IsNullOrWhiteSpace(directory))
                .Distinct(StringComparer.OrdinalIgnoreCase)];
        }

        internal IReadOnlyList<MigrationPlan> Plans { get; }

        internal IReadOnlyList<BMSTable> Tables { get; }

        internal CustomFolderOutputSettingsSnapshot Settings { get; }

        internal IReadOnlyCollection<string> ProtectedDirectories { get; }
    }

    internal sealed class CustomFolderProgressFact
    {
        internal CustomFolderProgressFact(int processedCount, int totalCount, string label)
        {
            ProcessedCount = Math.Max(0, processedCount);
            TotalCount = Math.Max(0, totalCount);
            Label = label ?? string.Empty;
        }

        internal int ProcessedCount { get; }

        internal int TotalCount { get; }

        internal string Label { get; }
    }

    internal sealed class MigrationPlan
    {
        internal BMSTable Table { get; init; }

        internal string OutputDirectoryBefore { get; init; }

        internal string OutputDirectoryAfter { get; init; }

        internal bool WasRootFolderBefore { get; init; }

        internal string RootOutputBaseDirectoryBefore { get; init; }

        internal string OutputBaseDirectoryBefore { get; init; }

        internal bool InferOutputBaseDirectoryBeforeWhenMissing { get; init; }

        internal bool SameDirectory { get; set; }

        internal PlaylistCustomFolderOutputOwner.CustomFolderOutputProjection Projection { get; set; }
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
            SuppressLog = true;
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

        internal bool SuppressLog { get; init; }
    }

    internal sealed class CustomFolderBatchOutputResult
    {
        internal int ReOutputCount { get; init; }

        internal bool Succeeded { get; set; }

        internal bool HasUnverifiedFiles => Materialization?.HasUnverifiedFiles == true;

        internal PlaylistCustomFolderOutputOwner.CustomFolderBatchMaterializationResult Materialization { get; init; }

        internal Lr2FolderFileDbSyncResult SyncResult { get; init; }

        internal Lr2SongDbSyncPreparedDataSurface PreparedDataSurface { get; init; }

        internal CustomFolderProgressFact ProgressFact { get; init; }

        internal string PerformanceMessage { get; init; }

        internal string WarningMessage { get; set; }

        internal Exception PrimaryException { get; init; }

        internal long ElapsedMs { get; init; }
    }
}
