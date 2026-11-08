using System.Collections.Concurrent;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>CoreとWindowsの再生パネル検証で同じ設定・通知・再生準備を接続します。</summary>
internal static class PlaybackPanelTestSupport
{
    /// <summary>
    /// 保持の長さそのものを検査しないケースが使う一時試聴の保持時間です。開始境界・保持・回収の順序は変えず、製品既定2秒の実時間だけを短くします。
    /// 保持中の読取りと所有を観測するケースは製品既定の値を明示し、製品の構成が既定値を使うことは構成経由のケースで確認します。
    /// </summary>
    internal static readonly TimeSpan ShortTemporaryPreviewHold = TimeSpan.FromMilliseconds(100);

    /// <summary>専有領域のダミー実行ファイルと記録用境界でBMIIDXプレイヤーを作ります。</summary>
    internal static BMIIDXView2015 CreateBmiPlayer(string directory, IExternalPlayerProcessGateway? processGateway = null)
    {
        string executable = Path.Combine(directory, "BMIIDXView2015.exe");
        File.WriteAllBytes(executable, []);
        return new BMIIDXView2015(executable, new RecordingPlayerSettingsGateway(),
            processGateway ?? new RecordingExternalPlayerProcessGateway());
    }

    /// <summary>ケースが所有する設定と代替UI境界を接続し、実パネルの再生判断を確認します。</summary>
    internal static PlaybackPanelViewModel CreatePanel(Settings testSettings, IBMSPlayer player)
    {
        return CreatePanel(testSettings, player, new FakePlaybackDialogService());
    }

    /// <summary>ケースが所有する設定と代替UI境界を接続し、実パネルの再生判断を確認します。</summary>
    internal static PlaybackPanelViewModel CreatePanel(Settings testSettings,
        IBMSPlayer player,
        FakePlaybackDialogService dialogs)
    {
        return CreatePanel(testSettings, player, new SettingsPlaybackSettingsStore(() => testSettings), dialogs);
    }

    /// <summary>ケースが所有する設定と代替UI境界を接続し、実パネルの再生判断を確認します。</summary>
    internal static PlaybackPanelViewModel CreatePanel(Settings testSettings,
        IBMSPlayer player,
        IPlaybackSettingsStore playbackSettings)
    {
        return CreatePanel(testSettings, player, playbackSettings, new FakePlaybackDialogService());
    }

    /// <summary>ケースが所有する設定と代替UI境界を接続し、実パネルの再生判断を確認します。</summary>
    internal static PlaybackPanelViewModel CreatePanel(Settings testSettings,
        IBMSPlayer player,
        IPlaybackSettingsStore playbackSettings,
        FakePlaybackDialogService dialogs)
    {
        return new PlaybackPanelViewModel(
            player,
            new ImmediatePlaybackUiDispatcher(),
            new MainChartListPlaybackQueue(new MainChartListViewModel()),
            playbackSettings,
            dialogs,
            _ => { },
            new ChartFileOperationSynchronizer(), temporaryPreviewHold: ShortTemporaryPreviewHold);
    }

    /// <summary>ケース専用設定で一時導入パネルを作り、指定時だけ明示UI schedulerを使います。</summary>
    internal static PlaybackPanelViewModel CreateTemporaryInstallPanel(Settings testSettings,
        string chartPath,
        string installDirectory,
        string songDbPath,
        IBMSPlayer player,
        IPlaybackDialogService dialogs,
        IUiScheduler? uiScheduler = null)
    {
        return CreateTemporaryInstallPanel(testSettings,
            chartPath,
            installDirectory,
            songDbPath,
            player,
            dialogs,
            out _, uiScheduler: uiScheduler);
    }

    /// <summary>専用設定・DB・受付を接続します。発見済みパッケージを渡した場合は実エントリーに導入先を設定し、その行投影を開始入力へ渡します。UI scheduler省略時は既存のDispatcher依存を維持し、Coreでは明示されたschedulerを使います。</summary>
    internal static PlaybackPanelViewModel CreateTemporaryInstallPanel(Settings testSettings,
        string chartPath,
        string installDirectory,
        string songDbPath,
        IBMSPlayer player,
        IPlaybackDialogService dialogs,
        out LibraryChartRow activationRow,
        ChartFileOperationSynchronizer? operations = null,
        string? packageRoot = null,
        ChartPackage? discoveredPackage = null,
        IUiScheduler? uiScheduler = null,
        TimeSpan? temporaryPreviewHold = null)
    {
        var databaseScope = new ApplicationDatabaseScope();
        PackageChartEntry entry = discoveredPackage?.ChartEntries.Single(item => item.Chart.Path == chartPath)
            ?? ChartPackageTestExtensions.CreateEntry(ChartTestValues.Empty() with { Path = chartPath });
        entry.SetInstallDestinationPathOnly(installDirectory);
        activationRow = discoveredPackage != null
            ? LibraryChartRow.FromPackageChartEntry(entry)
            : LibraryChartRow.FromChartFile(entry.Chart);
        var chartList = new MainChartListViewModel
        {
            Rows = new List<object> { activationRow },
            SelectedIndex = 0
        };
        var panel = new PlaybackPanelViewModel(
            player,
            new ImmediatePlaybackUiDispatcher(),
            new MainChartListPlaybackQueue(chartList),
            new SettingsPlaybackSettingsStore(() => testSettings),
            dialogs,
            _ => { },
            operations ?? new ChartFileOperationSynchronizer(), temporaryPreviewHold: temporaryPreviewHold ?? ShortTemporaryPreviewHold);
        TestBmsLibrary library = uiScheduler == null
            ? new TestBmsLibrary(databaseScope, songDbPath, settings: testSettings)
            : new TestBmsLibrary(databaseScope, songDbPath, null, null, null, new UnexpectedBmsLibraryDialogService(), uiScheduler, settings: testSettings);
        if (discoveredPackage != null)
        {
            library.ChartPackagesPending = [discoveredPackage];
        }
        else if (packageRoot != null)
        {
            ChartPackage package = ChartPackageTestExtensions.CreatePackage([entry]);
            package.path = packageRoot;
            library.ChartPackagesPending = [package];
        }
        panel.AttachLibrary(library);
        return panel;
    }

    /// <summary>保存を伴わないパネル状態をケース単位で所有します。</summary>
    internal sealed class InMemoryPlaybackSettingsStore : IPlaybackSettingsStore
    {
        /// <summary>保存先の代わりに保持するパネル表示状態です。</summary>
        public PlayerPanelState PlayerPanelState { get; set; }

        /// <summary>ケースが指定する繰返し設定です。</summary>
        public bool RepeatPlay { get; set; }

        /// <summary>ケースが指定するフォルダスキップ設定です。</summary>
        public bool FolderSkipPlay { get; set; }

        /// <summary>ケースが指定する単曲再生設定です。</summary>
        public bool SinglePlay { get; set; }

        /// <summary>外部パネル画像を使う条件です。</summary>
        public bool UseExternalPanelImage { get; set; }

        /// <summary>ケースが所有するパネル画像のパスです。</summary>
        public string StagefilePath { get; set; } = string.Empty;

        /// <summary>previewと保存判断へ渡すケース専用音量です。</summary>
        public int PlayerVolume { get; set; }

        /// <summary>永続化を持たないstoreの音量確定を受け付けます。</summary>
        public void CommitPlayerVolume() { }

        /// <summary>LR2 DBを使う場合の条件を保持します。</summary>
        public bool UsesLr2Database { get; set; }
    }

    /// <summary>確認・通知の結果と故障をケースから制御し、配送回数を観測します。</summary>
    internal sealed class FakePlaybackDialogService : IPlaybackDialogService
    {
        /// <summary>この代替通知で想定しない設定保存失敗を表明失敗として返します。</summary>
        public void NotifySettingsSaveFailure(Exception exception) => throw new AssertFailedException("Unexpected volume save failure.", exception);
        /// <summary>一時ファイル残留通知の完了・故障を指定します。</summary>
        internal Func<string, Task>? TemporaryFilesRemainNotification { get; set; }
        /// <summary>一時ファイル残留通知の呼出し回数です。</summary>
        internal int TemporaryFilesRemainNotificationCount { get; private set; }

        /// <summary>残留通知を数え、指定した非同期通知へ接続します。</summary>
        public Task NotifyTemporaryFilesRemainAsync(string directory)
        {
            TemporaryFilesRemainNotificationCount++;
            return TemporaryFilesRemainNotification?.Invoke(directory) ?? Task.CompletedTask;
        }

        /// <summary>一時導入再生の確認に返す選択です。</summary>
        internal bool TemporaryInstallConfirmationResult { get; set; } = true;

        /// <summary>確認を失敗させる元例外です。</summary>
        internal Exception? TemporaryInstallConfirmationException { get; set; }

        /// <summary>一時導入確認の呼出し回数です。</summary>
        internal int TemporaryInstallConfirmationCount { get; private set; }

        /// <summary>直近の再生失敗通知で受け取った元例外です。</summary>
        internal Exception? LastPlaybackFailure { get; private set; }

        /// <summary>再生失敗通知の呼出し回数です。</summary>
        internal int PlaybackFailureNotificationCount { get; private set; }

        private readonly TaskCompletionSource<object?> playbackFailureNotification =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>再生失敗通知が実際に届くまで待ちます。</summary>
        internal Task WaitForPlaybackFailureAsync() => playbackFailureNotification.Task;

        /// <summary>通知直前の再入・故障をケースから指定します。</summary>
        internal Action? BeforePlaybackFailureNotification { get; set; }

        /// <summary>通知自身を失敗させる元例外です。</summary>
        internal Exception? PlaybackFailureException { get; set; }

        /// <summary>確認を数え、指定した選択または元例外を返します。</summary>
        public bool ConfirmTemporaryInstallPlayback()
        {
            TemporaryInstallConfirmationCount++;
            if (TemporaryInstallConfirmationException != null)
            {
                throw TemporaryInstallConfirmationException;
            }
            return TemporaryInstallConfirmationResult;
        }

        /// <summary>元例外と回数を記録し、待機を終端してから指定した通知故障を返します。</summary>
        public void NotifyPlaybackFailure(Exception exception)
        {
            BeforePlaybackFailureNotification?.Invoke();
            LastPlaybackFailure = exception;
            PlaybackFailureNotificationCount++;
            playbackFailureNotification.TrySetResult(null);
            if (PlaybackFailureException != null)
            {
                throw PlaybackFailureException;
            }
        }
    }

    /// <summary>画面を作らず呼出し元でUI通知を完了させます。</summary>
    internal sealed class ImmediatePlaybackUiDispatcher : IPlaybackUiDispatcher
    {
        /// <summary>同期通知をこの代替dispatcherの配送方式で受け付けます。</summary>
        public void Dispatch(Action action)
        {
            action();
        }

        /// <summary>配送した通知の成功・故障に結び付くTaskを返します。</summary>
        public Task DispatchAsync(Action action)
        {
            action();
            return Task.CompletedTask;
        }
    }

    /// <summary>配送待ちのUI通知をケースから明示的に進め、実Taskの終端を観測します。</summary>
    internal sealed class QueuedPlaybackUiDispatcher : IPlaybackUiDispatcher
    {
        private readonly ConcurrentQueue<Action> actions = new();
        private readonly object pendingWaiterGate = new();
        private readonly List<(int MinimumCount, TaskCompletionSource<object?> Completion)> pendingWaiters = [];

        /// <summary>まだ配送していない通知数です。</summary>
        internal int PendingCount => actions.Count;

        /// <summary>指定数の未配送通知が揃ったことを待ちます。</summary>
        internal Task WaitForPendingActionsAsync(int minimumCount)
        {
            if (minimumCount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(minimumCount));
            }
            if (PendingCount >= minimumCount)
            {
                return Task.CompletedTask;
            }

            var completion = new TaskCompletionSource<object?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            lock (pendingWaiterGate)
            {
                if (PendingCount >= minimumCount)
                {
                    completion.TrySetResult(null);
                }
                else
                {
                    pendingWaiters.Add((minimumCount, completion));
                }
            }
            return completion.Task;
        }

        /// <summary>任意の面要求投稿の故障を、次の一回だけ再現します。</summary>
        internal bool FailNextDispatch { get; set; }

        /// <summary>同期通知をこの代替dispatcherの配送方式で受け付けます。</summary>
        public void Dispatch(Action action)
        {
            if (FailNextDispatch)
            {
                FailNextDispatch = false;
                throw new IOException("UI投稿故障");
            }
            actions.Enqueue(action);
            CompletePendingWaiters();
        }

        /// <summary>配送した通知の成功・故障に結び付くTaskを返します。</summary>
        public Task DispatchAsync(Action action)
        {
            var completion = new TaskCompletionSource<object?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            actions.Enqueue(() =>
            {
                try
                {
                    action();
                    completion.TrySetResult(null);
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            });
            CompletePendingWaiters();
            return completion.Task;
        }

        /// <summary>保留通知をFIFO順で全て実行します。</summary>
        internal void RunAll()
        {
            while (actions.TryDequeue(out Action? action))
            {
                action!();
            }
        }

        private void CompletePendingWaiters()
        {
            List<TaskCompletionSource<object?>>? completed = null;
            lock (pendingWaiterGate)
            {
                int pendingCount = PendingCount;
                for (int index = pendingWaiters.Count - 1; index >= 0; index--)
                {
                    (int minimumCount, TaskCompletionSource<object?> completion) = pendingWaiters[index];
                    if (pendingCount < minimumCount)
                    {
                        continue;
                    }

                    completed ??= [];
                    completed.Add(completion);
                    pendingWaiters.RemoveAt(index);
                }
            }

            if (completed == null)
            {
                return;
            }

            foreach (TaskCompletionSource<object?> completion in completed)
            {
                completion.TrySetResult(null);
            }
        }
    }



    /// <summary>削除時の再生停止を検証する際に、不要な保留削除確認へ到達しないことを保証します。</summary>
    internal sealed class UnusedPendingDeletionDialog : IPendingDeleteConfirmationDialogPort
    {
        /// <summary>このfixtureで想定しない保留削除の確認要求を拒否します。</summary>
        public Task<UiInteractionResult<bool>> ShowAsync() => throw new NotSupportedException();
    }

    /// <summary>CoreとWindowsの削除フローで、停止後の実ファイル変更と到達回数を共通に観測します。</summary>
    internal sealed class PhysicalChartDeletionStore : ISelectedChartMutationStore
    {
        /// <summary>実際にファイル削除へ到達した回数です。</summary>
        internal int Mutations { get; private set; }
        /// <summary>選択したライブラリ譜面を同一対象の削除準備へ渡します。</summary>
        public LibraryChartRemovalPreflight PrepareLibraryChartRemoval(BMSLibrary library, IReadOnlyList<LibraryChartRef> charts)
            => new(charts.Select(chart => chart.ToChartFileIdentity()), [], [], charts.Count, 0);
        /// <summary>このfixtureで対象外の拡張子変更準備を拒否します。</summary>
        public LibraryFileExtensionRenameBatch PrepareLibraryFileExtensionRenameBatch(
            BMSLibrary library, IReadOnlyList<ChartFile> charts, string newExtension) => throw new NotSupportedException();
        /// <summary>準備対象と許可されたフォルダを実削除し、確定結果を返します。</summary>
        public LibraryChartRemovalOutcome RemoveLibraryCharts(BMSLibrary library, LibraryChartRemovalPreflight prepared,
            IReadOnlyList<string> approvedWholeFolderDeletePaths, out Action? publication)
        {
            publication = null;
            IReadOnlyList<LibraryChartRef> charts = prepared.Targets.Select(chart => LibraryChartRef.FromChartFile(chart)).ToArray();
            Mutations++;
            foreach (LibraryChartRef chart in charts) { File.Delete(chart.Path); }
            foreach (string folder in approvedWholeFolderDeletePaths) { Directory.Delete(folder, recursive: true); }
            return new LibraryChartRemovalOutcome(charts.Select(chart => new LibraryChartRemovalTarget(chart.Path, LibraryChartRemovalState.Confirmed)), true, true);
        }
        /// <summary>このfixtureで対象外の保留譜面削除を拒否します。</summary>
        public void RemovePendingCharts(BMSLibrary library, IReadOnlyList<ChartFile> charts, bool sendToRecycleBin, bool deleteContainingPackageFoldersWhenNoBms) => throw new NotSupportedException();
        /// <summary>このfixtureで対象外のライブラリ譜面改名を拒否します。</summary>
        public LibraryMutationSessionReceipt RenameLibraryChartsWithReceipt(BMSLibrary library, IReadOnlyList<LibraryFileExtensionRenameBatch> batches) => throw new NotSupportedException();
        /// <summary>このfixtureで対象外の保留譜面改名を拒否します。</summary>
        public void RenamePendingCharts(BMSLibrary library, IReadOnlyList<ChartFile> charts, string newExtension) => throw new NotSupportedException();
        /// <summary>このfixtureで対象外のライブラリ譜面移動を拒否します。</summary>
        public LibraryMutationSessionReceipt MoveLibraryChartsWithReceipt(BMSLibrary library, ChartLibraryMoveRequest request) => throw new NotSupportedException();
        /// <summary>このfixtureで対象外の文字コード変更を拒否します。</summary>
        public void SetBMSFilesEncoding(BMSLibrary library, IReadOnlyList<ChartFile> bmsFiles, string encoding, IReadOnlyList<PackageChartEntry> pendingEntries) => throw new NotSupportedException();
    }
}
