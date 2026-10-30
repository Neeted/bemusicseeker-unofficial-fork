using System;
using System.Collections.Frozen;
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

    private readonly Lr2ConfigOwner lr2ConfigOwner;

    private readonly PlaylistCustomFolderOutputStatusOwner statusOwner;

    private readonly Func<BMSTable, CustomFolderOutputSettingsSnapshot, string> outputDirectoryResolver;

    private readonly Action<string> logPerformance;

    private readonly PlaylistOperationNotificationOwner notificationOwner;

    private readonly Func<IEnumerable<string>, string, CustomFolderOutputPhysicalSurface> physicalSurfaceResolver;

    private readonly Func<IReadOnlyCollection<BMSTable>, bool> areCurrentTables;

    internal PlaylistCustomFolderOutputMaintenanceOwner(
        PlaylistCustomFolderOutputOwner outputOwner,
        Func<CustomFolderOutputSettingsSnapshot> settingsProvider,
        Func<IReadOnlyList<BMSTable>> tablesSnapshotProvider,
        Action<BMSTable, string> ensureEntriesLoaded,
        Lr2ConfigOwner lr2ConfigOwner,
        PlaylistCustomFolderOutputStatusOwner statusOwner,
        Func<BMSTable, CustomFolderOutputSettingsSnapshot, string> outputDirectoryResolver,
        Action<string> logPerformance,
        PlaylistOperationNotificationOwner notificationOwner,
        Func<IEnumerable<string>, string, CustomFolderOutputPhysicalSurface> physicalSurfaceResolver,
        Func<IReadOnlyCollection<BMSTable>, bool> areCurrentTables)
    {
        this.outputOwner = outputOwner ?? throw new ArgumentNullException(nameof(outputOwner));
        this.settingsProvider = settingsProvider ?? throw new ArgumentNullException(nameof(settingsProvider));
        this.tablesSnapshotProvider = tablesSnapshotProvider ?? throw new ArgumentNullException(nameof(tablesSnapshotProvider));
        this.ensureEntriesLoaded = ensureEntriesLoaded ?? throw new ArgumentNullException(nameof(ensureEntriesLoaded));
        this.lr2ConfigOwner = lr2ConfigOwner;
        this.statusOwner = statusOwner ?? throw new ArgumentNullException(nameof(statusOwner));
        this.outputDirectoryResolver = outputDirectoryResolver ?? throw new ArgumentNullException(nameof(outputDirectoryResolver));
        this.logPerformance = logPerformance;
        this.notificationOwner = notificationOwner ?? throw new ArgumentNullException(nameof(notificationOwner));
        this.physicalSurfaceResolver = physicalSurfaceResolver;
        this.areCurrentTables = areCurrentTables ?? throw new ArgumentNullException(nameof(areCurrentTables));
    }

    /// <summary>
    /// 表の読込み時に不足するルート出力ディレクトリを補います。外側のBMS親登録は設定不備として拒否し、
    /// 復元用の基点・旧子登録の完全な整理は表読込み後の同期へ委ねます。
    /// 書込みは共有所有者の現在正本へ当該登録差分だけを適用し、別操作の未公開値を先行公開しません。
    /// </summary>
    internal bool SyncRootFolderOutputDirectoriesToLr2Config(CustomFolderOutputSettingsSnapshot settings)
    {
        if (settings == null || !settings.OperationModeLR2DB)
        {
            return false;
        }

        ValidateOutputLayout(settings);
        Lr2ConfigOwner currentConfig = lr2ConfigOwner;
        if (currentConfig == null)
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

        // 正本の登録から差分を準備し、他の成立済み通常変更を保ちます。
        IReadOnlyList<string> beforeDirectories = currentConfig.CaptureSaved().RegisteredRoots;
        CustomFolderOutputBaseSearchRootSyncService.ValidateOutputBaseAgainstSearchRoots(
            settings.LR2CustomFolderOutputBaseDirRootType, beforeDirectories,
            Resources.Label_RootOutputBase, allowRegisteredChildren: true);
        foreach (string expectedDirectory in expectedDirectories)
        {
            LongPathFileSystem.CreateDirectory(expectedDirectory);
        }

        List<string> missingDirectories = [.. expectedDirectories
            .Where(path => !beforeDirectories.Contains(path, StringComparer.OrdinalIgnoreCase))];
        if (missingDirectories.Count == 0)
        {
            return false;
        }

        currentConfig.SaveRoots(new Lr2RootChange([], expectedDirectories));
        return true;
    }

    /// <summary>
    /// ルート出力先の基点・旧子登録を現在の表ディレクトリへ揃えます。
    /// 基点が既存BMS登録の子になる設定は、ファイル作成や登録変更の前に拒否します。
    /// </summary>
    internal bool SyncCustomFolderOutputSearchRootsAfterSettingsChange(
        string previousRootOutputBaseDirectory,
        CustomFolderOutputSettingsSnapshot settings)
    {
        if (settings == null || !settings.OperationModeLR2DB) { return false; }
        Lr2ConfigOwner owner = lr2ConfigOwner ?? throw new InvalidOperationException("LR2 configuration is required for custom-folder search-root synchronization.");
        IReadOnlyList<string> before = owner.CaptureSaved().RegisteredRoots;
        IReadOnlyList<string> next = PrepareCustomFolderOutputSearchRoots(previousRootOutputBaseDirectory, before, settings);
        if (next.SequenceEqual(before, StringComparer.OrdinalIgnoreCase)) { return false; }
        owner.SaveRoots(new Lr2RootChange(before, next));
        return true;
    }

    /// <summary>当該XMLの確定済み登録からルート型の登録変更を準備します。呼出元は通常出力の変更と同じ文書へまとめて一回保存できます。</summary>
    internal IReadOnlyList<string> PrepareCustomFolderOutputSearchRoots(string previousRootOutputBaseDirectory,
        IReadOnlyList<string> currentDirectories, CustomFolderOutputSettingsSnapshot settings,
        IReadOnlyDictionary<string, string> renames = null)
    {
        if (settings == null || !settings.OperationModeLR2DB) { return currentDirectories; }
        ManagedPlaylistOutputPlacement.Validate((tablesSnapshotProvider() ?? []).Where(table => !string.IsNullOrWhiteSpace(table.Output_dir))
            .Select(table => ManagedPlaylistOutputPlacement.Resolve(table, settings, renames)));
        List<string> beforeDirectories = [.. currentDirectories];
        CustomFolderOutputBaseSearchRootSyncService.ValidateOutputBaseAgainstSearchRoots(
            settings.LR2CustomFolderOutputBaseDirRootType, beforeDirectories,
            Resources.Label_RootOutputBase, allowRegisteredChildren: true);

        IReadOnlyList<string> previousRootDirectories = CreateRootCustomFolderOutputDirectories(previousRootOutputBaseDirectory);
        IReadOnlyList<string> currentRootDirectories = CustomFolderOutputBaseRegistry.NormalizeBaseDirectories(
            CreateRootCustomFolderOutputDirectories(settings.LR2CustomFolderOutputBaseDirRootType));
        foreach (string currentRootDirectory in currentRootDirectories)
        {
            LongPathFileSystem.CreateDirectory(currentRootDirectory);
        }

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
        return nextDirectories;
    }

    /// <summary>保存済み配置も、ファイル・DB・検索ルートの変更に先立って同じ条件で検査します。</summary>
    private void ValidateOutputLayout(CustomFolderOutputSettingsSnapshot settings,
        IReadOnlyDictionary<BMSTable, string> overrides = null, IReadOnlyDictionary<string, string> outputBaseRenames = null)
    {
        if (settings?.OperationModeLR2DB != true) { return; }
        ManagedPlaylistOutputPlacement.Validate((tablesSnapshotProvider() ?? []).Where(table => !string.IsNullOrWhiteSpace(table.Output_dir)).Select(table =>
            overrides?.TryGetValue(table, out string directory) == true ? directory
                : ManagedPlaylistOutputPlacement.Resolve(table, settings, outputBaseRenames)));
    }

    /// <summary>移行後の実効先と明示新先を旧先整理から保全します。同一表の旧親から新子への移行と旧path再利用に必要です。</summary>
    private IReadOnlyCollection<string> CreateMigrationDestinationDirectories(
        IReadOnlyDictionary<BMSTable, string> destinations, CustomFolderOutputSettingsSnapshot settings,
        IReadOnlyDictionary<string, string> outputBaseRenames = null)
    {
        return (tablesSnapshotProvider() ?? []).Where(table => !string.IsNullOrWhiteSpace(table.Output_dir)).Select(table => destinations?.TryGetValue(table, out string path) == true
                ? path : ManagedPlaylistOutputPlacement.Resolve(table, settings, outputBaseRenames))
            .Select(Lr2FolderPath.NormalizeDirectoryPath).Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
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

        if (NotifyUnregisteredOutputBase(table, settings)) { return new CustomFolderOutputPreparation([], [], settings, 0); }
        ValidateOutputLayout(settings);
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
        ValidateOutputLayout(preparation.Settings);
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
    /// 単一表の移行投影を準備します。未登録追加先の通常表は通知して対象から外し、正本保存や独立したBMT処理は妨げません。
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

        if (NotifyUnregisteredOutputBase(table, settings)) { return new CustomFolderMigrationPreparation([], settings, []); }
        ValidateOutputLayout(settings);
        if (!string.IsNullOrWhiteSpace(outputDirectoryAfter))
        {
            ValidateOutputLayout(settings, new Dictionary<BMSTable, string> { [table] = outputDirectoryAfter });
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
        IReadOnlyCollection<string> protectedDirectories = CreateMigrationDestinationDirectories(
            new Dictionary<BMSTable, string> { [table] = resolvedOutputDirectoryAfter }, settings);
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
        ValidateOutputLayout(preparation.Settings);
        MigrationPlan plan = preparation.Plans.FirstOrDefault();
        if (plan == null)
        {
            return new CustomFolderBatchOutputResult { Succeeded = true };
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
        ValidateOutputLayout(settings);
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
    /// 承認済み登録対応から全移行投影・配置検査・整理時の保全先を準備します。
    /// 未改名の他表も同じ最終配置で解決し、存在しない旧登録名を既定先へ読み替えません。
    /// </summary>
    internal CustomFolderMigrationPreparation PrepareCustomFolderOutputMigrations(
        IReadOnlyList<BMSTable> tables,
        IReadOnlyDictionary<BMSTable, string> outputDirectoriesBefore,
        IReadOnlyDictionary<BMSTable, bool> wasRootFolderBeforeByTable,
        string rootOutputBaseDirectoryBefore,
        IReadOnlyDictionary<BMSTable, string> outputBaseDirectoryBeforeByTable,
        CustomFolderOutputSettingsSnapshot settings = null, IReadOnlyDictionary<string, string> outputBaseRenames = null)
    {
        settings ??= GetSettings();
        ValidateOutputLayout(settings, outputBaseRenames: outputBaseRenames);
        ValidateOutputLayout(settings, outputDirectoriesBefore, outputBaseRenames);
        var plans = new List<MigrationPlan>();
        foreach (BMSTable table in tables ?? [])
        {
            if (table == null || string.IsNullOrWhiteSpace(table.Output_dir)) { continue; }
            string beforeDirectory = outputDirectoriesBefore?.TryGetValue(table, out string previous) == true ? previous : null;
            string afterDirectory = ManagedPlaylistOutputPlacement.Resolve(table, settings, outputBaseRenames);
            if (string.IsNullOrWhiteSpace(afterDirectory))
            {
                NotifyUnregisteredOutputBase(table, settings);
                continue;
            }
            ensureEntriesLoaded(table, "MigrateCustomFolderOutputDirectories");
            if (IsSameDirectory(beforeDirectory, afterDirectory))
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
            "MigrateCustomFolderOutputDirectories", outputBaseRenames);
        IReadOnlyCollection<string> protectedDirectories = CreateMigrationDestinationDirectories(
            plans.ToDictionary(plan => plan.Table, plan => plan.OutputDirectoryAfter), settings, outputBaseRenames);
        return new CustomFolderMigrationPreparation(plans, settings, protectedDirectories, outputBaseRenames);
    }

    /// <summary>
    /// 準備済みの新先投影とDB同期を成立させます。呼出元がこの成立を記録してから旧先整理へ進みます。
    /// </summary>
    internal CustomFolderBatchOutputResult MaterializePreparedCustomFolderMigration(
        CustomFolderMigrationPreparation preparation,
        string reason,
        Func<CustomFolderBatchMaterializationRequest, Lr2FolderFileDbSyncResult> syncMaterialization)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        ArgumentNullException.ThrowIfNull(syncMaterialization);
        ValidateCurrentTables(preparation.Tables);
        ValidateOutputLayout(preparation.Settings, outputBaseRenames: preparation.OutputBaseRenames);
        if (preparation.Plans.Count == 0)
        {
            return new CustomFolderBatchOutputResult { Succeeded = true };
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

        return result;
    }

    /// <summary>今回の変更前だけを表す不変な整理入力です。後続操作へ保持しません。</summary>
    internal sealed record PreviousOutputLocation(BMSTable Table, string Directory, string BaseDirectory, bool IsRoot);

    /// <summary>SQL置換前のLR2実効絶対配置・基点・ROOT種別を捕捉します。standaloneでは既存出力へ触れる入力を作りません。</summary>
    internal IReadOnlyList<PreviousOutputLocation> CapturePreviousOutputLocations(
        IReadOnlyList<BMSTable> tables, CustomFolderOutputSettingsSnapshot settings)
    {
        if (!settings.OperationModeLR2DB) { return []; }
        var locations = new List<PreviousOutputLocation>();
        foreach (BMSTable table in tables ?? [])
        {
            if (table == null || string.IsNullOrWhiteSpace(table.Output_dir)) { continue; }
            string directory = ResolveOutputDirectory(table, settings);
            string baseDirectory = table.is_root_folder ? settings.LR2CustomFolderOutputBaseDirRootType
                : CustomFolderOutputBaseRegistry.ResolveNormalOutputBaseDirectory(table.custom_folder_output_base_name,
                    settings.LR2CustomFolderOutputBaseDir, settings.LR2CustomFolderAdditionalOutputBaseDirs);
            if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(baseDirectory)) { continue; }
            locations.Add(new(table, Path.GetFullPath(directory), Path.GetFullPath(baseDirectory), table.is_root_folder));
        }
        return locations.AsReadOnly();
    }

    /// <summary>新正本の必須生成成功後だけ旧全配置を整理します。旧表のIDや現在所属に依存せず新配置集合を保全し、整理失敗は残存警告で終えます。</summary>
    internal void CleanupReplacedCustomFolderOutputs(IReadOnlyList<PreviousOutputLocation> previousLocations,
        CustomFolderOutputSettingsSnapshot settings,
        Func<CustomFolderBatchMaterializationRequest, Lr2FolderFileDbSyncResult> syncMaterialization)
    {
        if (!settings.OperationModeLR2DB || previousLocations == null || previousLocations.Count == 0) { return; }
        var plans = previousLocations.Select(previous => new MigrationPlan
        {
            Table = previous.Table,
            OutputDirectoryBefore = previous.Directory,
            WasRootFolderBefore = previous.IsRoot,
            RootOutputBaseDirectoryBefore = previous.BaseDirectory,
            OutputBaseDirectoryBefore = previous.BaseDirectory
        }).ToList();
        try
        {
            // 復元前後で表IDや所有表が変わっても、今回生成した全配置を保全します。
            IReadOnlyCollection<string> protectedDirectories = CreateMigrationDestinationDirectories(null, settings);
            CustomFolderBatchOutputResult cleanup = CompletePreparedMigrationCleanup(new(plans, settings, protectedDirectories), syncMaterialization);
            PublishPostLeaseResult(cleanup, null);
        }
        catch (Exception failure)
        {
            notificationOwner.QueueWarning(string.Format(Resources.Warn_CustomFolderCleanupFailed,
                string.Join(Environment.NewLine, plans.Select(plan => plan.OutputDirectoryBefore).Distinct(StringComparer.OrdinalIgnoreCase)), failure.Message));
        }
    }

    /// <summary>現在正本から生成し、成立した生成の後だけ今回の旧配置を整理します。必要生成の失敗は伝播し、旧先整理の失敗は対象と原因を通知して終端します。</summary>
    internal void RegenerateCustomFolderOutputs(IReadOnlyList<BMSTable> tables,
        IReadOnlyList<PreviousOutputLocation> previousLocations, CustomFolderOutputSettingsSnapshot settings,
        Func<CustomFolderBatchMaterializationRequest, Lr2FolderFileDbSyncResult> syncMaterialization)
    {
        if (!settings.OperationModeLR2DB) { return; }
        CustomFolderOutputPreparation preparation = PrepareTables(tables, "settings output", true, true, settings);
        CustomFolderBatchOutputResult result = ReOutputPreparedTablesAsync(preparation, "settings output",
            "settings_output_regenerate", false, false, syncMaterialization).GetAwaiter().GetResult();
        PublishPostLeaseResult(result, null);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(string.Format(Resources.Warn_CustomFolderOutputFailed,
                string.Join(", ", preparation.Tables.Select(table => table.name)),
                string.Join(", ", result.Materialization?.UnverifiedFilePaths ?? [])));
        }
        string[] protectedDirectories = preparation.Projections.Select(projection => projection.OutputDirectory).ToArray();
        var plans = new List<MigrationPlan>();
        foreach (PreviousOutputLocation previous in previousLocations ?? [])
        {
            string current = ResolveOutputDirectory(previous.Table, settings);
            if (string.IsNullOrWhiteSpace(current) || IsSameDirectory(current, previous.Directory)) { continue; }
            plans.Add(new MigrationPlan
            {
                Table = previous.Table,
                OutputDirectoryBefore = previous.Directory,
                OutputDirectoryAfter = current,
                WasRootFolderBefore = previous.IsRoot,
                RootOutputBaseDirectoryBefore = previous.BaseDirectory,
                OutputBaseDirectoryBefore = previous.BaseDirectory
            });
        }
        try
        {
            CustomFolderBatchOutputResult cleanup = CompletePreparedMigrationCleanup(new(plans, settings, protectedDirectories), syncMaterialization);
            PublishPostLeaseResult(cleanup, null);
        }
        catch (Exception failure)
        {
            // 設定操作は新先への生成まで成立済みです。旧先整理の残存だけを通知し、後続の必要同期へ進みます。
            // 表プロパティ保存の未完了継続は別契約のため、共有の整理処理では例外を変換しません。
            notificationOwner.QueueWarning(string.Format(Resources.Warn_CustomFolderCleanupFailed,
                string.Join(Environment.NewLine, plans.Select(plan => plan.OutputDirectoryBefore).Distinct(StringComparer.OrdinalIgnoreCase)), failure.Message));
        }
    }

    /// <summary>今回生成した出力を保全し、既知の旧表管理ディレクトリと対応行を整理します。ファイル削除失敗は結果へ残し、DB整理の例外は呼出元へ返します。</summary>
    internal CustomFolderBatchOutputResult CompletePreparedMigrationCleanup(CustomFolderMigrationPreparation preparation,
        Func<CustomFolderBatchMaterializationRequest, Lr2FolderFileDbSyncResult> syncMaterialization,
        CustomFolderBatchOutputResult result = null)
    {
        result ??= new CustomFolderBatchOutputResult { Succeeded = true };
        var pruneScopes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (MigrationPlan plan in preparation.Plans)
        {
            if (string.IsNullOrWhiteSpace(plan.OutputDirectoryBefore)) { continue; }
            IReadOnlyCollection<string> managedBases = CreateManagedOutputBaseScopes(plan.Table, plan.WasRootFolderBefore,
                plan.RootOutputBaseDirectoryBefore, plan.OutputBaseDirectoryBefore,
                plan.InferOutputBaseDirectoryBeforeWhenMissing, preparation.Settings);
            if (DeleteOutputDirectoryTree(plan.OutputDirectoryBefore, managedBases, preparation.ProtectedDirectories, out bool deletionFailure))
            {
                foreach (string scope in CreateDirectoryPruneScopes(plan.OutputDirectoryBefore, plan.WasRootFolderBefore,
                    plan.RootOutputBaseDirectoryBefore, preparation.Settings)) { pruneScopes.Add(scope); }
            }
            else if (deletionFailure)
            {
                result.WarningMessage = string.Format(Resources.Warn_FileOrDirDeleteFailed, plan.OutputDirectoryBefore);
                result.Succeeded = false;
            }
        }
        if (pruneScopes.Count > 0)
        {
            SyncPrunedRows(syncMaterialization, pruneScopes.ToArray(),
                CreateDestinationRowScopes(preparation.ProtectedDirectories, preparation.Plans.Select(plan => plan.OutputDirectoryBefore), preparation.Settings),
                CreateDestinationAncestorRows(preparation.ProtectedDirectories, preparation.Plans.Select(plan => plan.OutputDirectoryBefore), preparation.Settings));
        }
        return result;
    }

    /// <summary>未登録名を使う通常表だけを停止し、元の対象名と保存先名を通知します。ROOTでは追加名を使いません。</summary>
    internal bool NotifyUnregisteredOutputBase(BMSTable table, CustomFolderOutputSettingsSnapshot settings)
    {
        bool unknown = table != null && !table.is_root_folder && !string.IsNullOrWhiteSpace(table.custom_folder_output_base_name)
            && !CustomFolderOutputBaseRegistry.ContainsBaseName(table.custom_folder_output_base_name, settings.LR2CustomFolderAdditionalOutputBaseDirs);
        if (unknown) { notificationOwner.QueueWarning(string.Format(Resources.Warn_UnregisteredCustomFolderOutputBase, table.name, table.custom_folder_output_base_name)); }
        return unknown;
    }

    /// <summary>
    /// 対象表の項目を読み、表投影と物理入力を準備します。LR2専用の任意通知は表の実対象件数と探索段階を渡します。
    /// 専用通知の失敗は投影の結果・失敗分類を変更しません。
    /// </summary>
    internal CustomFolderOutputPreparation PrepareTables(
        IReadOnlyList<BMSTable> tables,
        string operation,
        bool forceWriteAllFiles,
        bool throwOnProjectionFailure,
        CustomFolderOutputSettingsSnapshot settings = null,
        Action<string, int, int> stageProgressReporter = null)
    {
        settings ??= GetSettings();
        tables ??= [];
        ValidateOutputLayout(settings);
        var progress = new Lr2SongDbSyncStageProgressReporter(stageProgressReporter);
        progress.Begin("playlist_projection", tables.Count);
        int processed = 0;
        var projections = new List<PlaylistCustomFolderOutputOwner.CustomFolderOutputProjection>();
        var preparedTables = new List<BMSTable>();
        int failedCount = 0;
        foreach (BMSTable table in tables)
        {
            if (table == null || string.IsNullOrWhiteSpace(table.Output_dir))
            {
                progress.Advance(++processed, tables.Count);
                continue;
            }

            if (NotifyUnregisteredOutputBase(table, settings)) { progress.Advance(++processed, tables.Count); continue; }
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
            progress.Advance(++processed, tables.Count);
        }

        progress.Begin("playlist_output_discovery");
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
    /// 受理済み予約の下で準備済み表投影を物理出力します。
    /// buildPreparedDataSurfaceが有効ならsyncMaterializationなしでfolder表を保存せず入力を構成できます。
    /// LR2専用の任意通知は実ファイル数と保存・結果構成の段階を渡し、既存の複合通知の契約を変更しません。
    /// </summary>
    internal Task<CustomFolderBatchOutputResult> ReOutputPreparedTablesAsync(
        CustomFolderOutputPreparation preparation,
        string reason,
        string operation,
        bool buildPreparedDataSurface,
        bool yieldBetweenTables,
        Func<CustomFolderBatchMaterializationRequest, Lr2FolderFileDbSyncResult> syncMaterialization,
        Action<int, int, string> progressCallback = null,
        Action<string, int, int> stageProgressReporter = null)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        if (!buildPreparedDataSurface)
        {
            ArgumentNullException.ThrowIfNull(syncMaterialization);
        }
        ValidateCurrentTables(preparation.Tables);
        ValidateOutputLayout(preparation.Settings);
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
            Stopwatch.StartNew(),
            stageProgressReporter);
    }

    /// <summary>
    /// 準備済み投影を共通の物理出力・保存経路へ渡します。任意のLR2段階通知は既存の複合通知から独立します。
    /// </summary>
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
        Stopwatch stopwatch = null,
        Action<string, int, int> stageProgressReporter = null)
    {
        if (!buildPreparedDataSurface)
        {
            ArgumentNullException.ThrowIfNull(syncMaterialization);
        }
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
            syncMaterialization,
            stageProgressReporter);
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
        Func<CustomFolderBatchMaterializationRequest, Lr2FolderFileDbSyncResult> syncMaterialization,
        Action<string, int, int> stageProgressReporter)
    {
        projections ??= [];
        settings ??= GetSettings();
        var progress = new Lr2SongDbSyncStageProgressReporter(stageProgressReporter);
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
            settingsOverride: settings,
            stageProgressReporter: stageProgressReporter);
        bool materializationSyncCompleted = !materialization.HasUnverifiedFiles
            && syncMaterialization != null;
        Lr2FolderFileDbSyncResult syncResult = materializationSyncCompleted
            ? syncMaterialization(new CustomFolderBatchMaterializationRequest(materialization))
            : null;
        string completionLabel = materializationSyncCompleted
            ? Resources.Custom_folder_db_sync_progress_single_label
            : Resources.Custom_folder_output_progress_single_label;
        if (!materialization.HasUnverifiedFiles)
        {
            progressCallback?.Invoke(
                total,
                total,
                completionLabel);
            progress.Begin("playlist_state_saving");
            statusOwner.PersistStatuses(
                [.. projections],
                PlaylistCustomFolderOutputOwner.CreatePhysicalSurfaceFromSyncItems(materialization.SyncItems));
        }
        stopwatch.Stop();

        progress.Begin("playlist_result_preparation");
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
                completionLabel),
            PerformanceMessage = performanceMessage,
            ElapsedMs = stopwatch.ElapsedMilliseconds
        });
    }

    private void PrepareProjectionSurfaceAndScopes(
        IReadOnlyCollection<PlaylistCustomFolderOutputOwner.CustomFolderOutputProjection> projections,
        CustomFolderOutputSettingsSnapshot settings,
        string reason, IReadOnlyDictionary<string, string> outputBaseRenames = null)
    {
        if (projections == null || projections.Count == 0)
        {
            return;
        }

        var destinations = projections.ToDictionary(projection => projection.Table, projection => projection.OutputDirectory);
        ValidateOutputLayout(settings, destinations, outputBaseRenames);
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
            CreateDestinationRowScopes(
                preparation.ProtectedDirectories,
                [plan.OutputDirectoryBefore],
                preparation.Settings),
            CreateDestinationAncestorRows(
                preparation.ProtectedDirectories,
                [plan.OutputDirectoryBefore],
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
    /// 内部の移行・生成の保護区間を終えた後、結果の進捗・警告・性能計測を公開します。
    /// 呼出元が保持する外側のPは、この公開時にも保持できます。
    /// 既定では公開後に元の失敗を伝播し、呼出元が結果を採用する場合は伝播を抑止します。
    /// </summary>
    /// <param name="result">公開する進捗・警告・性能計測と、伝播対象の元の失敗を保持する結果です。</param>
    /// <param name="progressCallback">結果に保持された進捗の通知先です。nullの場合は進捗を通知しません。</param>
    /// <param name="propagatePrimaryFailure">既定のtrueでは元の失敗を伝播します。保存サービスが結果を採用する場合はfalseとし、元の失敗を結果から受け取ります。</param>
    internal void PublishPostLeaseResult(
        CustomFolderBatchOutputResult result,
        Action<int, int, string> progressCallback, bool propagatePrimaryFailure = true)
    {
        InvokeBestEffort(() => PublishProgressFact(result, progressCallback), "custom_folder_progress_publication_failed");
        InvokeBestEffort(() => PublishWarning(result), "custom_folder_warning_publication_failed");
        InvokeBestEffort(() => PublishPerformance(result), "custom_folder_performance_publication_failed");
        if (propagatePrimaryFailure && result?.PrimaryException != null)
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
        if (directories == null || string.IsNullOrWhiteSpace(normalizedOutputBase))
        {
            return;
        }
        CustomFolderOutputBaseSearchRootSyncService.ValidateOutputBaseAgainstSearchRoots(
            normalizedOutputBase, directories, Resources.Label_CustomFolderOutputBase);
        if (directories.Any(directory => CustomFolderOutputBaseSearchRootSyncService.IsSameDirectory(normalizedOutputBase, directory)))
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
        // 基点自身と配下の旧登録だけを整理します。外側のBMS検索ルートは採用対象ではありません。
        return !currentRootDirectories.Contains(registeredPath, StringComparer.OrdinalIgnoreCase)
            && CustomFolderOutputBaseSearchRootSyncService.IsSameOrChildDirectory(registeredPath, currentRootBase);
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

    private static IReadOnlyCollection<string> CreateDestinationRowScopes(
        IEnumerable<string> protectedOutputDirectories,
        IEnumerable<string> protectedScopeDirectories,
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
            AddDestinationRowVariants(excluded, directory, settings);
        }
        return [.. excluded];
    }

    private static IReadOnlyCollection<string> CreateDestinationAncestorRows(
        IEnumerable<string> protectedOutputDirectories,
        IEnumerable<string> scopeDirectories,
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
                    AddDestinationRowVariants(excluded, current, settings);
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

    private static void AddDestinationRowVariants(
        ISet<string> paths,
        string directory,
        CustomFolderOutputSettingsSnapshot settings)
    {
        paths?.Add(directory);
        string rowPath = Lr2FolderPath.ToFolderPath(directory);
        paths?.Add(rowPath);
        Lr2FolderFileSourceClassification classification = Lr2FolderFileSourceClassifier.Classify(new Lr2FolderFileSourceClassificationRequest
        {
            FilePath = rowPath ?? directory,
            Lr2RootPath = settings.LR2RootPath,
            RootCustomFolderOutputBaseDir = settings.LR2CustomFolderOutputBaseDirRootType
        });
        paths?.Add(classification.DatabasePath);
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
            bool onlyProtectedEntriesRemain = remainingEntries.All(path => protectedDescendants.Any(destination =>
                Lr2FolderPath.IsSameOrDescendant(path, destination) || Lr2FolderPath.IsSameOrDescendant(destination, path)));
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
            IEnumerable<string> protectedDirectories, IReadOnlyDictionary<string, string> outputBaseRenames = null)
        {
            OutputBaseRenames = outputBaseRenames?.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
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

        /// <summary>未改名ヘッダーを含む操作中の承認済み最終登録対応。設定・正本の状態として公開しません。</summary>
        internal IReadOnlyDictionary<string, string> OutputBaseRenames { get; }
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
            DirectoryRowRootDirectories = result?.DirectoryRowRootDirectories ?? [];
            DirectoryEntries = result?.DirectoryEntries;
            PruneScopePaths = result?.PruneScopePaths ?? [];
            EmptyOutputDirectories = result?.EmptyOutputDirectories ?? [];
            SuppressLog = true;
        }

        internal IReadOnlyCollection<string> OutputDirectories { get; init; } = [];

        internal IReadOnlyCollection<string> OutputRowScopeDirectories { get; init; } = [];

        internal IReadOnlyCollection<Lr2FolderFileSyncItem> Items { get; init; } = [];

        internal IReadOnlyCollection<string> DirectoryRowGenerationScopeDirectories { get; init; } = [];

        internal IReadOnlyCollection<string> DirectoryRowRootDirectories { get; init; } = [];

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
