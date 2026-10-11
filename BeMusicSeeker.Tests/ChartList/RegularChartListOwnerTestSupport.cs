using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

/// <summary>通常一覧の実ownerへ隔離した入力・実SQLite・通知gateを接続する既存fixtureをまとめます。</summary>
internal static class RegularChartListOwnerTestSupport
{
    /// <summary>画面更新を既定では直実行し、一覧の構築・受付を実ownerで検査するための既存依存を揃えます。</summary>
    internal static RegularChartListOwner CreateOwner(
        MainChartListViewModel table,
        PlaylistWorkspaceViewModel workspace)
    {
        return CreateOwner(table, workspace, action => action());
    }

    /// <summary>画面更新を既定では直実行し、一覧の構築・受付を実ownerで検査するための既存依存を揃えます。</summary>
    internal static RegularChartListOwner CreateOwner(
        MainChartListViewModel table,
        PlaylistWorkspaceViewModel workspace,
        Action<Action> dispatchToUi,
        IUiScheduler? normalLibraryRefreshUiScheduler = null,
        Action<string>? log = null,
        PendingPackageWorkflowOwner? pendingPackageWorkflow = null,
        ChartFileOperationSynchronizer? chartFileOperations = null,
        IUiDialogService? mutationDialogs = null,
        ChartMutationActivityOwner? chartMutationActivity = null,
        IChartMutationPlaybackPort? mutationPlayback = null)
    {
        return new RegularChartListOwner(
            table,
            workspace,
            log ?? (_ => { }),
            dispatchToUi,
            _ => { },
            pendingPackageWorkflow ?? CreatePendingPackageWorkflowOwner(),
            chartFileOperations ?? new ChartFileOperationSynchronizer(),
            chartMutationActivity ?? new ChartMutationActivityOwner(),
            mutationPlayback ?? new NoOpChartMutationPlaybackPort(),
            normalLibraryRefreshUiScheduler ?? new TestUiScheduler(() => null!),
            mutationDialogs);
    }

    /// <summary>実snapshot readerと同じcatalog writerを保持し、通知生産側のUI同期待ちを検出するために使います。</summary>
    internal static ReaderWriterLockSlimWrapper GetCatalogStorageRowsWriteGate(BMSLibrary library)
    {
        FieldInfo storageOwnerField = typeof(BMSLibrary).GetField(
            "catalogOwnedCollectionOwner",
            BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new InvalidOperationException("Owned collection field was not found.");
        CatalogOwnedCollectionOwner storageOwner = storageOwnerField.GetValue(library) as CatalogOwnedCollectionOwner
            ?? throw new InvalidOperationException("Owned collection was not initialized.");
        return storageOwner.WriteGate;
    }

    /// <summary>
    /// catalog writer を保持したまま、外部 replacement 相当の refresh 通知だけを発行します。
    /// </summary>
    /// <remarks>
    /// この fixture は別の mutation を実行すると通知前に catalog writer 待ちになるため、
    /// UI subscriber の非同期スケジューリングを確認するために通知だけを発行します。
    /// 現行の具象 <see cref="BMSLibrary" /> には typed attach source の注入経路がないため、
    /// owner フィールドだけを反射で取得し、既存の typed internal publication method を呼び出します。
    /// </remarks>
    internal static void PublishNormalLibraryRefreshResetNotification(
        BMSLibrary library,
        bool notifiesBmsFiles,
        bool notifiesBmsonSongs)
    {
        FieldInfo mutationOwnerField = typeof(BMSLibrary).GetField(
            "libraryMutationOwner",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var mutationOwner = (LibraryMutationOwner)mutationOwnerField.GetValue(library)!;
        mutationOwner.PublishExternalReplacementNormalLibraryRefreshNotification(
            notifiesBmsFiles,
            notifiesBmsonSongs);
    }

    /// <summary>一覧編集の接続先として実pending ownerを用意し、libraryに到達しないcaseでは空の入力窓口を渡します。</summary>
    internal static PendingPackageWorkflowOwner CreatePendingPackageWorkflowOwner()
    {
        return new PendingPackageWorkflowOwner(
              () => null!,
              new ChartFileOperationSynchronizer(),
              new ChartMutationActivityOwner(),
              new NoOpChartMutationPlaybackPort(),
              new TestUiDialogService(),
              () => new InstallDestinationWorkflowSettingsSnapshot(
                  showManualInstallConfirmation: false,
                  deletePendingPackageSourceAfterInstall: false),
              ExternalShellGatewayPolicy.Current);
    }

    /// <summary>通常一覧終端でdetail退役と関連列公開を実行するworkspaceを、外部通信を開始しない既存portで用意します。</summary>
    internal static PlaylistWorkspaceViewModel CreateWorkspaceForOwner(
        MainChartListViewModel? table = null,
        PlaylistDetailBuildState? buildState = null,
        PlaylistDetailViewState? viewState = null,
        Action<string>? detailRetentionLog = null)
    {
        return new PlaylistWorkspaceViewModel(
            action => action(),
            table ?? new MainChartListViewModel(action => action()),
            buildState ?? new PlaylistDetailBuildState(),
            viewState ?? new PlaylistDetailViewState(),
            _ => { },
            detailRetentionLog ?? (_ => { }),
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.KeywordSearchFavoritesSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
            PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
            () => null!,
            () => null!,
            _ => { },
            new ObservableCollection<BMSTable>(),
            (_, _) => false,
            () => true,
            () => MainViewUpdateMode.FolderFilterSelected,
            () => Task.CompletedTask,
            () => false,
            () => { },
            _ => { },
            (exception, message) => { },
            (_, _) => false,
            (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck);
    }

    /// <summary>実FSとDBへ接続するcaseに同じ譜面識別値を渡し、pathだけを各専用領域に置きます。</summary>
    internal static ChartFile CreateTestableBmsFile(string path)
    {
        ChartFile file = (ChartTestValues.Empty() with { Path = path });
        file = file with { Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" };
        return file;
    }

    /// <summary>所持library譜面を実rename要求へ変換し、テスト準備時の対象拒否を見逃さないようにします。</summary>
    internal static RenameChartFolderRequest CreateRenameRequest(ChartFile file)
    {
        ChartFile chart = (file);
        var target = new ChartOperationTarget(
            chart,
            playlistEntry: null,
            ChartOperationSourceScope.Library,
            isOwned: true,
            isPending: false,
            isPlaylistMissing: false,
            ChartOperationCapabilities.MoveInLibrary);
        Assert.IsTrue(RenameChartFolderRequest.TryCreate(target, out RenameChartFolderRequest request));
        return request;
    }

    /// <summary>専用SQLiteへsong・folder・maintenance表を作り、処理後は専用領域全体を回収します。</summary>
    internal static void WithTemporarySongDb(Action<string> testAction)
    {
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_RegularOwner_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        File.WriteAllBytes(songDbPath, []);
        try
        {
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDB.folder>();
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
            }
            testAction(songDbPath);
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }


    /// <summary>明示した画面・keyword・modeを一覧入口へ渡し、既定の外部設定を読まない要求を用意します。</summary>
    internal static RegularChartListEntryRequest CreateEntryRequest(
        MainViewUpdateMode mode,
        string keywordFilter = "",
        ChartModeFilter modeFilter = ChartModeFilter.All)
    {
        return new RegularChartListEntryRequest
        {
            Filters = new ChartListFilterSnapshot(keywordFilter, modeFilter),
            Mode = mode,
            RequestedMode = mode,
            CurrentTreeMode = mode,
            IncludeBmsonRows = false,
            Stopwatch = Stopwatch.StartNew()
        };
    }

    /// <summary>sortとfolder絞込みを識別できるpath・タイトル・modeを持つ保存行不要のsourceを用意します。</summary>
    internal static ChartListSourceRow CreateSourceRow(string folder, string fileName, int? mode = null)
    {
        string path = string.IsNullOrEmpty(folder)
            ? fileName
            : System.IO.Path.Combine(@"C:\Charts", folder, fileName);
        var chart = new ChartFile(ChartFileKind.Bms, path, md5: fileName, sha256: null, title: fileName, rawTitle: fileName, artist: string.Empty, genre: string.Empty, folder: folder, tag: string.Empty, levelText: string.Empty, level: null, mode: mode, chartInfo: null);
        return ChartListSourceRow.FromChartFile(chart);
    }

    /// <summary>detail退役前の参照元と表示行を組み立てるため、同じ譜面とplaylist項目を持つsourceを用意します。</summary>
    internal static PlaylistDetailSourceRow CreatePlaylistSourceRow(string md5)
    {
        ChartFile chart = CreateSourceRow("Playlist", md5).Chart;
        return new PlaylistDetailSourceRow(
            new BMSTableEntry(chart),
            chart);
    }

    /// <summary>sourceから実タイトル昇順orderを構築し、構築失敗をfixture段階で検出します。</summary>
    internal static ChartListOrder CreateOrder(params ChartListSourceRow[] sourceRows)
    {
        Assert.IsTrue(ChartListOrder.TryCreate(
            sourceRows,
            nameof(LibraryChartRow.Title),
            ListSortDirection.Ascending,
            out ChartListOrder order));
        return order;
    }

    /// <summary>指定した行を実体一覧の実構築へ渡し、終端とcache採用の検査に使う候補を得ます。</summary>
    internal static RegularChartListBuildResult Build(
        RegularChartListOwner owner,
        RegularChartListRequestLease lease,
        IEnumerable<LibraryChartRow> rows,
        MainViewUpdateMode mode = MainViewUpdateMode.UpdatedNone)
    {
        var request = new RegularChartListRefreshRequest(
            mode,
            MainViewUpdateMode.TreeViewFilterNotChanged,
            parameter: null,
            MainViewUpdateMode.FolderFilterSelected,
            treeParameter: null,
            includeBmsonRows: true,
            virtualSubsetRequiredFailure: false,
            keywordFilter: string.Empty,
            ChartModeFilter.All,
            default);
        return owner.Build(lease, request, new RegularChartListBuildInput
        {
            CurrentTreeMode = MainViewUpdateMode.FolderFilterSelected,
            HasFolderRowsOverride = true,
            FolderRowsOverride = rows,
            SortCacheGeneration = new NormalLibrarySortCacheGenerationSnapshot(1, 1, 0, 0, 0, 0, 0, 0),
            Stopwatch = Stopwatch.StartNew()
        });
    }

    /// <summary>実体行sortから通常一覧終端までの要求を、捕捉した画面modeと列選択で用意します。</summary>
    internal static RegularMaterializedChartListApplyRequest CreateMaterializedApplyRequest(
        IEnumerable<LibraryChartRow> rows,
        MainViewUpdateMode appliedMode = MainViewUpdateMode.FolderFilterSelected)
    {
        var refresh = new RegularChartListRefreshRequest(
            MainViewUpdateMode.SortUpdated,
            MainViewUpdateMode.TreeViewFilterNotChanged,
            parameter: null,
            appliedMode,
            treeParameter: null,
            includeBmsonRows: false,
            virtualSubsetRequiredFailure: false,
            keywordFilter: string.Empty,
            ChartModeFilter.All,
            ChartListSortSpecification.Create(nameof(LibraryChartRow.Title), ListSortDirection.Ascending, hasValue: true));
        var settings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD);
        return new RegularMaterializedChartListApplyRequest
        {
            RefreshRequest = refresh,
            HasFolderRowsOverride = true,
            FolderRowsOverride = rows,
            ExternalVersions = new RegularChartListExternalVersions(0, 0, 0),
            ColumnSelection = new MainChartListColumnSelection(
                settings,
                reused: false,
                elapsedMs: 0L,
                appliedMode,
                Visibility.Collapsed,
                new PlaylistSummaryColumnSettings()),
            Mode = MainViewUpdateMode.SortUpdated,
            Stopwatch = Stopwatch.StartNew()
        };
    }

    /// <summary>実構築で得た行だけを実体終端へ渡し、関連列とsummary公開の条件を揃えます。</summary>
    internal static RegularChartListPresentationResult CreateTerminalInput(
        RegularChartListBuildResult build,
        MainViewUpdateMode mode = MainViewUpdateMode.UpdatedNone,
        Visibility visibility = Visibility.Collapsed,
        PlaylistSummaryColumnSettings? summarySettings = null)
    {
        var stopwatch = Stopwatch.StartNew();
        var settings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD);
        return RegularChartListPresentationResult.ForMaterialized(
            build,
            new MainChartListRowsApplyRequest
            {
                Rows = build.Sort.RowsView,
                ColumnsSettings = settings,
                SelectionPolicy = MainChartListSelectionPolicy.Preserve,
                Summary = MainChartListSummaryUpdate.NormalRows(build.Sort.RowsView),
                Stopwatch = stopwatch
            },
            new MainChartListColumnSelection(
                settings,
                reused: false,
                elapsedMs: 0L,
                mode,
                visibility,
                summarySettings ?? new PlaylistSummaryColumnSettings()),
            mode,
            stopwatch);
    }

    /// <summary>仮想終端へ行参照を渡し、未知folder数のsummaryから非同期集計への遷移を用意します。</summary>
    internal static RegularChartListPresentationResult CreateVirtualTerminalInput(IList rows)
    {
        var stopwatch = Stopwatch.StartNew();
        var settings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD);
        return RegularChartListPresentationResult.ForVirtual(
            new MainChartListRowsApplyRequest
            {
                Rows = rows,
                ColumnsSettings = settings,
                SelectionPolicy = MainChartListSelectionPolicy.Preserve,
                Summary = MainChartListSummaryUpdate.NormalCounts(rows.Count, distinctFolderCount: -1),
                Stopwatch = stopwatch
            },
            new MainChartListColumnSelection(
                settings,
                reused: false,
                elapsedMs: 0L,
                MainViewUpdateMode.FolderFilterSelected,
                Visibility.Collapsed,
                new PlaylistSummaryColumnSettings()),
            MainViewUpdateMode.FolderFilterSelected,
            stopwatch);
    }

    /// <summary>lockとgateの競合をThreadPool枯渇に依存させず観測する専用workerを開始します。</summary>
    internal static Task StartLongRunning(Action action)
    {
        return Task.Factory.StartNew(
            action,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    /// <summary>停止収束を専用workerから開始し、内側の非同期終端まで一つのTaskで回収します。</summary>
    internal static Task StartLongRunningAsync(Func<Task> action)
    {
        return Task.Factory.StartNew(
                action,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default)
            .Unwrap();
    }

    /// <summary>受付したUI処理を手動実行待ちにし、予約中の停止と遅い公開を観測します。</summary>
    internal sealed class ActionQueueUiScheduler : IUiScheduler
    {
        private readonly Action<Action> enqueue;

        internal ActionQueueUiScheduler(Action<Action> enqueue)
        {
            this.enqueue = enqueue ?? throw new ArgumentNullException(nameof(enqueue));
        }

        public bool IsAvailable => true;

        public bool CanExecuteInline => false;

        public bool CheckAccess() => false;

        public IUiScheduledOperation Schedule(
            Action action,
            UiSchedulePriority priority = UiSchedulePriority.Normal)
        {
            var operation = new ActionQueueUiScheduledOperation();
            enqueue(() => operation.Execute(action));
            return operation;
        }

        public void Invoke(Action action, UiSchedulePriority priority = UiSchedulePriority.Normal)
        {
            action();
        }

        public T Invoke<T>(Func<T> action, UiSchedulePriority priority = UiSchedulePriority.Normal)
        {
            return action();
        }

        public async Task InvokeAsync(
            Action action,
            UiSchedulePriority priority = UiSchedulePriority.Normal)
        {
            await Schedule(action, priority).Completion.ConfigureAwait(false);
        }

        public async Task InvokeAsync(
            Func<Task> action,
            UiSchedulePriority priority = UiSchedulePriority.Normal)
        {
            await action().ConfigureAwait(false);
        }
    }

    /// <summary>手動実行の成功・失敗を実ownerが待つCompletionへ返します。</summary>
    internal sealed class ActionQueueUiScheduledOperation : IUiScheduledOperation
    {
        private readonly TaskCompletionSource<bool> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsAccepted => true;

        public bool IsCompleted => completion.Task.IsCompleted;

        public bool IsAborted => false;

        public string RejectionReason => string.Empty;

        public Task Completion => completion.Task;

        public void Abort()
        {
        }

        internal void Execute(Action action)
        {
            try
            {
                action();
                completion.TrySetResult(true);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
                throw;
            }
        }
    }

    /// <summary>処理を受付した後に取消済み終端を返し、未開始処理の停止回収を観測します。</summary>
    internal sealed class AbortingUiScheduler : IUiScheduler
    {
        public bool IsAvailable => true;

        public bool CanExecuteInline => false;

        public bool CheckAccess() => false;

        public IUiScheduledOperation Schedule(
            Action action,
            UiSchedulePriority priority = UiSchedulePriority.Normal)
        {
            return new AbortedUiScheduledOperation();
        }

        public void Invoke(Action action, UiSchedulePriority priority = UiSchedulePriority.Normal)
        {
            throw new InvalidOperationException("Synchronous invoke is not supported.");
        }

        public T Invoke<T>(Func<T> action, UiSchedulePriority priority = UiSchedulePriority.Normal)
        {
            throw new InvalidOperationException("Synchronous invoke is not supported.");
        }

        public Task InvokeAsync(
            Action action,
            UiSchedulePriority priority = UiSchedulePriority.Normal)
        {
            return Task.FromException(new InvalidOperationException("UI operation was aborted."));
        }

        public Task InvokeAsync(
            Func<Task> action,
            UiSchedulePriority priority = UiSchedulePriority.Normal)
        {
            return Task.FromException(new InvalidOperationException("UI operation was aborted."));
        }
    }

    /// <summary>受付済みかつ取消済みのUI終端を返すfixtureです。</summary>
    internal sealed class AbortedUiScheduledOperation : IUiScheduledOperation
    {
        private static readonly Task AbortedCompletion = Task.FromCanceled(
            new CancellationToken(canceled: true));

        public bool IsAccepted => true;

        public bool IsCompleted => true;

        public bool IsAborted => true;

        public string RejectionReason => "UI operation was aborted.";

        public Task Completion => AbortedCompletion;

        public void Abort()
        {
        }
    }

    /// <summary>実summary走査の添字読取りだけをgateで止め、共有走査回数と取消・停止の収束を観測します。</summary>
    internal sealed class BlockingIndexedSourceRows : IReadOnlyList<ChartListSourceRow>, IDisposable
    {
        private readonly IReadOnlyList<ChartListSourceRow> rows;
        private int indexReadCount;

        internal BlockingIndexedSourceRows(params ChartListSourceRow[] rows)
        {
            this.rows = rows;
        }

        internal ManualResetEventSlim IndexReadStarted { get; } = new();

        internal ManualResetEventSlim ReleaseIndexRead { get; } = new();

        internal int IndexReadCount => Volatile.Read(ref indexReadCount);

        public int Count => rows.Count;

        public ChartListSourceRow this[int index]
        {
            get
            {
                Interlocked.Increment(ref indexReadCount);
                IndexReadStarted.Set();
                ReleaseIndexRead.Wait();
                return rows[index];
            }
        }

        public IEnumerator<ChartListSourceRow> GetEnumerator()
        {
            return rows.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        public void Dispose()
        {
            IndexReadStarted.Dispose();
            ReleaseIndexRead.Dispose();
        }
    }
}
