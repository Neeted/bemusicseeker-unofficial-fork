using System.ComponentModel;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using Parago.Windows;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>複数の実行区分で同じ準備・観測を共有します。</summary>
internal static class DuplicateMaintenanceWorkflowOwnerTestSupport
{
    /// <summary>指定された識別・配置条件から、各ケースが観測する譜面値を作ります。</summary>
    internal static ChartFile CreateChart(string path, string hash)
    {
        return new ChartFile(ChartFileKind.Bms, path, hash, null, "Duplicate", "Duplicate", "Artist", "Genre", "Folder", string.Empty, string.Empty, null, null, null);
    }

    /// <summary>各実行区分が同じ準備と観測を使うための共通補助です。</summary>
    internal static FakeUiDialogService AcceptedDialogs()
    {
        return new FakeUiDialogService
        {
            ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)
        };
    }

    /// <summary>各実行区分が同じ準備と観測を使うための共通補助です。</summary>
    internal sealed class RecordingPresentation : IChartMutationPlaybackPort
    {
        private readonly List<string> events;

        internal RecordingPresentation(List<string> events)
        {
            this.events = events;
        }

        internal Exception? EndActivityFailure { get; set; }

        /// <summary>指定すると、再生の停止が（記録の後に）この失敗で終わります。停止の失敗で物理変更へ進まないことの観測に使います。</summary>
        internal Exception? StopFailure { get; set; }

        internal void OnActivityChanged(object? sender, EventArgs e)
        {
            var activity = (ChartMutationActivityOwner)sender!;
            events.Add(activity.IsActive ? "activity-start" : "activity-end");
            if (!activity.IsActive && EndActivityFailure != null)
            {
                throw EndActivityFailure;
            }
        }

        internal void OnWorkflowChanged(
            object? sender,
            DuplicateMaintenanceWorkflowChangedEventArgs e)
        {
            switch (e)
            {
                case DuplicateMaintenanceRefreshSuppressionChangedEventArgs suppressionChanged:
                    events.Add(suppressionChanged.IsSuppressed ? "suppression-start" : "suppression-end");
                    break;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(e),
                        e,
                        "Unsupported duplicate-maintenance workflow change.");
            }
        }

        public Task StopPlaybackForMutationAsync()
        {
            events.Add("playback-stop");
            return StopFailure == null ? Task.CompletedTask : Task.FromException(StopFailure);
        }
    }

    /// <summary>各実行区分が同じ準備と観測を使うための共通補助です。</summary>
    internal class RecordingStore : IDuplicateMaintenanceStore
    {
        private readonly List<string> events;

        internal RecordingStore(List<string> events)
        {
            this.events = events;
        }

        internal string SourceDirectory { get; private set; } = string.Empty;

        internal string DestinationDirectory { get; private set; } = string.Empty;

        internal IReadOnlyList<ChartFile> Charts { get; private set; } = [];

        protected void AddEvent(string value) => events.Add(value);

        public virtual DuplicateMergeMaintenanceReceipt MergeFolderWithReceipt(
            BMSLibrary library, string sourceDirectory, string destinationDirectory, long operationId,
            LibraryOperationNotices notices)
        {
            events.Add("store-merge");
            SourceDirectory = sourceDirectory;
            DestinationDirectory = destinationDirectory;
            return new DuplicateMergeMaintenanceReceipt(
                true, ResourceHealthIndexUpdateMode.DeferOnUpdates,
                MaintenanceWorkflowResultFacts.From(null), false, false, false, false, false,
                LibraryMutationSessionReceipt.Empty);
        }

        /// <summary>一括整理の要求を記録し、全グループを未処理（操作全体の拒否）として返します。一括整理の結果を観測するケースは、継承して差し替えます。</summary>
        public virtual DuplicateBatchOrganizeResult OrganizeDuplicateGroups(
            BMSLibrary library, DuplicateBatchOrganizeRequest request, long operationId,
            LibraryOperationNotices notices, Action<DuplicateBatchOrganizeProgress>? progress, CancellationToken cancellationToken)
        {
            events.Add("store-organize");
            return DuplicateBatchOrganizeResult.NotAdmitted(request);
        }

        /// <summary>分裂配置の探索し直しの呼出しと渡された探索条件を記録し、実ライブラリの探索へ渡します。失敗を注入するケースは、継承して差し替えます。</summary>
        public virtual void SearchSplitPlacementGroups(BMSLibrary library, SplitPlacementSearchCriteria criteria)
        {
            events.Add("store-search-split");
            SearchedSplitPlacementCriteria.Add(criteria);
            library.SearchSplitPlacementGroups(criteria, null, CancellationToken.None);
        }

        /// <summary>探索し直しに渡された探索条件を、呼出しの順に記録します。</summary>
        internal List<SplitPlacementSearchCriteria> SearchedSplitPlacementCriteria { get; } = [];

        internal LibraryChartRemovalOutcome RemovalOutcome { get; set; } = null!;
        internal IReadOnlyList<string> WholeFolderCandidates { get; set; } = [];
        internal IReadOnlyList<string> RegisteredUserRoots { get; set; } = [];
        internal IReadOnlyList<string> ApprovedPaths { get; private set; } = [];
        internal int PhysicalMutationStarts { get; private set; }

        /// <summary>対象別受付Pの取得結果です。falseにすると、確認後に対象別受付が使用中だった場合を再現します。</summary>
        internal bool PhysicalMutationAdmitted { get; set; } = true;

        public bool TryBeginPhysicalMutation(BMSLibrary library, IEnumerable<string> paths, bool recursive, out LibraryFileMutationLease? lease)
        { PhysicalMutationStarts++; lease = null; return PhysicalMutationAdmitted; }

        /// <summary>削除の事前準備を呼ばれた回数です。準備の失敗で止める経路へ届いたことの観測に使います。</summary>
        internal int PrepareChartRemovalCalls { get; private set; }

        /// <summary>派生の保存窓口が準備を差し替える場合に、呼出しを記録へ加えます。</summary>
        protected void CountPrepareChartRemoval() => PrepareChartRemovalCalls++;

        public virtual LibraryChartRemovalPreflight PrepareChartRemoval(BMSLibrary library, IReadOnlyList<ChartFile> charts)
            => PrepareCounted(charts);

        private LibraryChartRemovalPreflight PrepareCounted(IReadOnlyList<ChartFile> charts)
        {
            PrepareChartRemovalCalls++;
            return new(charts, WholeFolderCandidates, [], charts.Count, 0, registeredUserRoots: RegisteredUserRoots);
        }

        public virtual LibraryChartRemovalOutcome RemoveCharts(BMSLibrary library, LibraryChartRemovalPreflight prepared, IReadOnlyList<string> approvedWholeFolderPaths, LibraryOperationNotices notices, out Action? publication)
        {
            publication = null;
            IReadOnlyList<ChartFile> charts = prepared.Targets;
            events.Add("store-remove");
            ApprovedPaths = approvedWholeFolderPaths;
            Charts = charts;
            return RemovalOutcome ?? new LibraryChartRemovalOutcome(charts.Select(chart => new LibraryChartRemovalTarget(chart.Path, LibraryChartRemovalState.Confirmed)), true, true);
        }
    }

    /// <summary>各実行区分が同じ準備と観測を使うための共通補助です。</summary>
    internal sealed class TerminalRecordingStore : RecordingStore
    {
        private readonly DuplicateMergeMaintenanceReceipt receipt;

        internal TerminalRecordingStore(
            List<string> events,
            DuplicateMergeMaintenanceReceipt receipt)
            : base(events)
        {
            this.receipt = receipt ?? throw new ArgumentNullException(nameof(receipt));
        }

        internal int MergeWithReceiptCallCount { get; private set; }

        public override DuplicateMergeMaintenanceReceipt MergeFolderWithReceipt(
            BMSLibrary library,
            string sourceDirectory,
            string destinationDirectory,
            long operationId,
            LibraryOperationNotices notices)
        {
            MergeWithReceiptCallCount++;
            AddEvent("store-merge-with-receipt");
            return receipt;
        }
    }

    /// <summary>
    /// 進捗ハブへ本番と同じ接続で重複保守の実行中を入力します。確認を表示しない統合を保存処理で止め、
    /// <see cref="Complete"/> または破棄で解放して操作の終端まで回収します。
    /// </summary>
    internal sealed class BlockedDuplicateMergeProgress : IDisposable
    {
        private readonly ManualResetEventSlim release = new(false);
        private readonly Task<DuplicateMaintenanceMutationResult> operation;

        private BlockedDuplicateMergeProgress(OperationProgressHubViewModel hub, string sourceDirectory)
        {
            SourceDirectory = sourceDirectory;
            var events = new List<string>();
            var owner = new DuplicateMaintenanceWorkflowOwner(
                UninitializedBmsLibrary.Create,
                new ChartFileOperationSynchronizer(),
                new ChartMutationActivityOwner(),
                new NoOpChartMutationPlaybackPort(),
                AcceptedDialogs(),
                () => false,
                _ => true,
                _ => new ExplorerOpenResult(),
                _ => null,
                new BlockingStore(events, release));
            hub.AttachDuplicateMaintenanceProgressSource(owner);
            string destination = sourceDirectory + "_destination";
            operation = owner.RunFolderMergeAsync(sourceDirectory, destination, new DuplicateGroup([], [sourceDirectory, destination]));
        }

        /// <summary>行の詳細に表示される統合元フォルダです。</summary>
        internal string SourceDirectory { get; }

        /// <summary>統合を開始し、進捗の開始通知が届いた状態で返します。</summary>
        internal static BlockedDuplicateMergeProgress Start(OperationProgressHubViewModel hub, string sourceDirectory)
            => new(hub, sourceDirectory);

        /// <summary>保存処理を解放し、進捗の終了通知を含む操作の終端まで待ちます。</summary>
        internal void Complete()
        {
            release.Set();
            operation.GetAwaiter().GetResult();
        }

        public void Dispose()
        {
            try { Complete(); }
            finally { release.Dispose(); }
        }

        private sealed class BlockingStore(List<string> events, ManualResetEventSlim release) : RecordingStore(events)
        {
            public override DuplicateMergeMaintenanceReceipt MergeFolderWithReceipt(
                BMSLibrary library, string sourceDirectory, string destinationDirectory, long operationId, LibraryOperationNotices notices)
            {
                release.Wait();
                return base.MergeFolderWithReceipt(library, sourceDirectory, destinationDirectory, operationId, notices);
            }
        }
    }

    /// <summary>各実行区分が同じ準備と観測を使うための共通補助です。</summary>
    internal sealed class FakeUiDialogService : IUiDialogService
    {
        internal UiDialogResult? ConfirmationResult { get; set; }
        internal Func<UiConfirmationRequest, Task<UiDialogResult>>? ConfirmationHandler { get; set; }

        internal TaskCompletionSource<UiDialogResult>? PendingConfirmation { get; set; }
        internal TaskCompletionSource<bool>? ConfirmationReached { get; set; }

        internal UiConfirmationRequest? ConfirmationRequest { get; private set; }

        internal List<UiMessageRequest> Messages { get; } = [];
        internal Action? OnMessage { get; set; }

        /// <summary>指定すると、確認・案内・進捗の開始と終了をこの記録へ「confirm」「message」「progress-start」「progress-end」で加えます。</summary>
        internal List<string>? Events { get; set; }

        /// <summary>進捗画面の代替です。実際の背景実行（BackgroundWorker）で操作を実行し、報告・取消の要求・取消ボタンの無効化を観測します。</summary>
        internal BackgroundProgressRunner Progress { get; } = new();

        public Task<UiDialogResult> ShowMessageAsync(UiMessageRequest request, CancellationToken cancellationToken = default)
        {
            Events?.Add("message");
            Messages.Add(request);
            OnMessage?.Invoke();
            return Task.FromResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));
        }

        public Task<UiDialogResult> ConfirmAsync(UiConfirmationRequest request, CancellationToken cancellationToken = default)
        {
            Events?.Add("confirm");
            ConfirmationRequest = request;
            if (ConfirmationHandler != null) { return ConfirmationHandler(request); }
            ConfirmationReached?.TrySetResult(true);
            if (PendingConfirmation != null)
            {
                return PendingConfirmation.Task;
            }
            return Task.FromResult(ConfirmationResult ?? throw new InvalidOperationException("Confirmation result was not configured."));
        }

        public Task<UiWindowDialogResult<TResult>> ShowWindowAsync<TWindow, TResult>(UiWindowDialogRequest<TWindow, TResult> request, CancellationToken cancellationToken = default)
            where TWindow : Window => throw new NotSupportedException();

        public Task<UiFilePickerResult> PickFileAsync(UiFilePickerRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<UiFolderPickerResult> PickFolderAsync(UiFolderPickerRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<UiSaveFilePickerResult> PickSaveFileAsync(UiSaveFilePickerRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async Task<UiProgressResult> RunWithProgressAsync(UiProgressRequest request, Func<UiProgressContext, Task> operation, CancellationToken cancellationToken = default)
        {
            Events?.Add("progress-start");
            try { return await Progress.RunAsync(request, operation); }
            finally { Events?.Add("progress-end"); }
        }
    }

    /// <summary>
    /// 進捗画面の代替です。本番と同じく、操作を実際の背景実行（<see cref="BackgroundWorker"/>）で実行し、画面の完了（取消の要求・報告の受取り）を待たずに操作を進めます。
    /// 報告は進捗の受け手（指定した同期の文脈、既定ではスレッドプール）で記録し、取消ボタンの押下と無効化を観測できます。
    /// </summary>
    internal sealed class BackgroundProgressRunner
    {
        private readonly object gate = new();
        private readonly List<(int Percent, string Message)> reports = [];
        private BackgroundWorker? worker;
        private int cancellationDisabledCount;
        private int runs;

        /// <summary>直近の進捗画面の要求です。題名・ラベル・設定・所有者を観測します。</summary>
        internal UiProgressRequest? Request { get; private set; }

        /// <summary>進捗画面を開いた回数です。</summary>
        internal int Runs => Volatile.Read(ref runs);

        /// <summary>報告された割合と文言を、報告順に保持します。</summary>
        internal IReadOnlyList<(int Percent, string Message)> Reports
        {
            get { lock (gate) { return reports.ToArray(); } }
        }

        /// <summary>報告を記録した直後に呼ぶ通知です。進捗の受け手のスレッドで呼びます。</summary>
        internal Action<int, string>? Reported { get; set; }

        /// <summary>取消ボタンを無効にした回数です。</summary>
        internal int CancellationDisabledCount => Volatile.Read(ref cancellationDisabledCount);

        /// <summary>
        /// 進捗の報告と画面の完了を受け取る同期の文脈（UIスレッドの代替）です。省略時はスレッドプールで受け取ります。
        /// 開始時に捕捉するため、操作の開始前に設定します。
        /// </summary>
        internal SynchronizationContext? ReceiverContext { get; set; }

        /// <summary>指定すると、操作の終端後にこの状態で画面の結果を返します（進捗の仕組みが取消を返す場合の再現）。</summary>
        internal UiDialogStatus? ForcedStatus { get; set; }

        /// <summary>指定すると、操作を呼ばずにこの状態（表示の失敗など）で画面の結果を返します。進捗画面が開けない経路の再現です。</summary>
        internal UiDialogStatus? FailWithoutRunning { get; set; }

        /// <summary>取消が要求されたかです。</summary>
        internal bool CancellationRequested => Volatile.Read(ref worker)?.CancellationPending == true;

        /// <summary>利用者が進捗画面の取消ボタンを押した状態にします。</summary>
        internal void RequestCancel()
            => (Volatile.Read(ref worker) ?? throw new InvalidOperationException("The progress operation has not started.")).CancelAsync();

        internal Task<UiProgressResult> RunAsync(UiProgressRequest request, Func<UiProgressContext, Task> operation)
        {
            Request = request;
            Interlocked.Increment(ref runs);
            if (FailWithoutRunning is { } failure)
            {
                return Task.FromResult(new UiProgressResult(failure, error: new InvalidOperationException("progress route could not run the operation")));
            }
            var completion = new TaskCompletionSource<UiProgressResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var started = new BackgroundWorker { WorkerReportsProgress = true, WorkerSupportsCancellation = true };
            Volatile.Write(ref worker, started);
            started.DoWork += (_, e) =>
            {
                var context = new UiProgressContext(new ProgressDialogContext(started, e, () => Interlocked.Increment(ref cancellationDisabledCount)));
                operation(context).GetAwaiter().GetResult();
            };
            started.ProgressChanged += (_, e) =>
            {
                (int Percent, string Message) report = (e.ProgressPercentage, e.UserState?.ToString() ?? string.Empty);
                lock (gate) { reports.Add(report); }
                Reported?.Invoke(report.Percent, report.Message);
            };
            started.RunWorkerCompleted += (_, e) => completion.TrySetResult(
                ForcedStatus is { } status ? new UiProgressResult(status, error: e.Error)
                : e.Error != null ? new UiProgressResult(UiDialogStatus.Failed, error: e.Error)
                : new UiProgressResult(UiDialogStatus.Accepted));
            SynchronizationContext? previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(ReceiverContext);
            try { started.RunWorkerAsync(); }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
            return completion.Task;
        }
    }

}
