using System;
using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

internal static class RegularChartListOwnerTestSupport
{
    internal static RegularChartListOwner CreateOwner(
        MainChartListViewModel table,
        PlaylistWorkspaceViewModel workspace)
    {
        return CreateOwner(table, workspace, action => action());
    }

    internal static RegularChartListOwner CreateOwner(
        MainChartListViewModel table,
        PlaylistWorkspaceViewModel workspace,
        Action<Action> dispatchToUi,
        IUiScheduler? normalLibraryRefreshUiScheduler = null,
        Action<string>? log = null,
        PendingPackageWorkflowOwner? pendingPackageWorkflow = null,
        ChartFileOperationSynchronizer? chartFileOperations = null,
        IUiDialogService? mutationDialogs = null,
        ChartMutationActivityOwner? chartMutationActivity = null)
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
            new NoOpFolderAutoRenamePlaybackPort(),
            normalLibraryRefreshUiScheduler ?? new TestUiScheduler(() => null!),
            mutationDialogs);
    }

    internal static ReaderWriterLockSlimWrapper GetCatalogStorageRowsWriteGate(BMSLibrary library)
    {
        FieldInfo storageOwnerField = typeof(BMSLibrary).GetField(
            "catalogStorageRowsOwner",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var storageOwner = (CatalogStorageRowsOwner)storageOwnerField.GetValue(library)!;
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
    /// production 側の typed attach source が注入をサポートした時点で、この helper は退役させます。
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

    internal static PendingPackageWorkflowOwner CreatePendingPackageWorkflowOwner()
    {
        return new PendingPackageWorkflowOwner(
              () => null!,
              new ChartFileOperationSynchronizer(),
              new ChartMutationActivityOwner(),
              new NoOpPendingPackageMutationPlaybackPort(),
              new TestUiDialogService(),
              () => new InstallDestinationWorkflowSettingsSnapshot(
                  showManualInstallConfirmation: false,
                  deletePendingPackageSourceAfterInstall: false),
              ExternalShellGatewayPolicy.Current);
    }

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
            PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
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

    internal static TestableBmsFile CreateTestableBmsFile(string path)
    {
        var file = new TestableBmsFile { path = path };
        file.SetHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        return file;
    }

    internal static RenameChartFolderRequest CreateRenameRequest(BMSFile file)
    {
        ChartFile chart = ChartFileProjection.FromBmsFile(file);
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

    internal sealed class NoOpFolderAutoRenamePlaybackPort : IFolderAutoRenamePlaybackPort
    {
        public void StopPlaybackForCharts(IReadOnlyList<ChartFile> charts)
        {
        }

        public void StopPlaybackForFolderMutation()
        {
        }
    }

    internal sealed class TestableBmsFile : BMSFile
    {
        internal void SetHash(string value)
        {
            hash = value;
        }

        internal void SetMaintenanceInfo(BMSFileMaintenanceInfo value)
        {
            SetMaintenanceInfo(value, suppressPropertyChanged: true);
        }
    }

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

    internal static ChartListSourceRow CreateSourceRow(string folder, string fileName, int? mode = null)
    {
        string path = string.IsNullOrEmpty(folder)
            ? fileName
            : System.IO.Path.Combine(@"C:\Charts", folder, fileName);
        var chart = new ChartFile(
            ChartFileKind.Bms,
            path,
            md5: fileName,
            sha256: null,
            title: fileName,
            rawTitle: fileName,
            artist: string.Empty,
            genre: string.Empty,
            folder: folder,
            tag: string.Empty,
            levelText: string.Empty,
            level: null,
            mode: mode,
            chartInfo: null,
            bmsFile: null,
            bmsonSong: null);
        return ChartListSourceRow.FromChartFile(chart);
    }

    internal static PlaylistDetailSourceRow CreatePlaylistSourceRow(string md5)
    {
        ChartFile chart = CreateSourceRow("Playlist", md5).Chart;
        return new PlaylistDetailSourceRow(
            new BMSTableEntry(chart),
            chart);
    }

    internal static ChartListOrder CreateOrder(params ChartListSourceRow[] sourceRows)
    {
        Assert.IsTrue(ChartListOrder.TryCreate(
            sourceRows,
            nameof(LibraryChartRow.Title),
            ListSortDirection.Ascending,
            out ChartListOrder order));
        return order;
    }

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

    internal static Task StartLongRunning(Action action)
    {
        return Task.Factory.StartNew(
            action,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    internal static Task StartLongRunningAsync(Func<Task> action)
    {
        return Task.Factory.StartNew(
                action,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default)
            .Unwrap();
    }

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
                if (!ReleaseIndexRead.Wait(TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException("Summary index read was not released.");
                }
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
