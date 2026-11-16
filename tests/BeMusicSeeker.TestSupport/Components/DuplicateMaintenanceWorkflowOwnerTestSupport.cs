using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;

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
            return Task.CompletedTask;
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

        internal LibraryChartRemovalOutcome RemovalOutcome { get; set; } = null!;
        internal IReadOnlyList<string> WholeFolderCandidates { get; set; } = [];
        internal IReadOnlyList<string> RegisteredUserRoots { get; set; } = [];
        internal IReadOnlyList<string> ApprovedPaths { get; private set; } = [];
        internal int PhysicalMutationStarts { get; private set; }

        /// <summary>対象別受付Pの取得結果です。falseにすると、確認後に対象別受付が使用中だった場合を再現します。</summary>
        internal bool PhysicalMutationAdmitted { get; set; } = true;

        public bool TryBeginPhysicalMutation(BMSLibrary library, IEnumerable<string> paths, bool recursive, out LibraryFileMutationLease? lease)
        { PhysicalMutationStarts++; lease = null; return PhysicalMutationAdmitted; }

        public LibraryChartRemovalPreflight PrepareChartRemoval(BMSLibrary library, IReadOnlyList<ChartFile> charts)
            => new(charts, WholeFolderCandidates, [], charts.Count, 0, registeredUserRoots: RegisteredUserRoots);

        public LibraryChartRemovalOutcome RemoveCharts(BMSLibrary library, LibraryChartRemovalPreflight prepared, IReadOnlyList<string> approvedWholeFolderPaths, LibraryOperationNotices notices, out Action? publication)
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
        public Task<UiDialogResult> ShowMessageAsync(UiMessageRequest request, CancellationToken cancellationToken = default)
        {
            Messages.Add(request);
            OnMessage?.Invoke();
            return Task.FromResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));
        }

        public Task<UiDialogResult> ConfirmAsync(UiConfirmationRequest request, CancellationToken cancellationToken = default)
        {
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

        public Task<UiProgressResult> RunWithProgressAsync(UiProgressRequest request, Func<UiProgressContext, Task> operation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

}
