using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>複数の実行区分で同じ準備・観測を共有します。</summary>
internal static class SelectedChartMutationWorkflowOwnerTestSupport
{
    /// <summary>各ケースが渡す依存を使って検証対象の管理主体を構成し、既存の通知接続を維持します。</summary>
    internal static SelectedChartMutationWorkflowOwner CreateOwner(
        RecordingPresentation presentation,
        FakeUiDialogService dialogs,
        RecordingStore store,
        ChartFileOperationSynchronizer? gate = null,
        ChartMutationActivityOwner? activity = null)
    {
        activity ??= new();
        var owner = new SelectedChartMutationWorkflowOwner(
            () => (BMSLibrary)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(BMSLibrary)),
            gate ?? new ChartFileOperationSynchronizer(),
            activity,
            presentation,
            dialogs,
            dialogs,
            store);
        owner.WorkflowChanged += presentation.OnWorkflowChanged;
        activity.ActivityChanged += presentation.OnActivityChanged;
        return owner;
    }

    /// <summary>各実行区分が同じ準備と観測を使うための共通補助です。</summary>
    internal static FakeUiDialogService AcceptedMessageDialogs()
    {
        return new FakeUiDialogService
        {
            ConfirmationResults = new Queue<UiDialogResult>([
                UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)
            ])
        };
    }

    /// <summary>指定された譜面と操作条件を、そのまま検証対象へ渡す操作対象にまとめます。</summary>
    internal static ChartOperationTarget CreateTarget(
        string fileName,
        ChartOperationSourceScope sourceScope,
        bool isPending,
        ChartOperationCapabilities capabilities)
    {
        return new ChartOperationTarget(
            CreateChart(Path.Combine(@"C:\Songs", fileName)),
            null,
            sourceScope,
            isOwned: true,
            isPending,
            isPlaylistMissing: false,
            capabilities);
    }

    /// <summary>指定された識別・配置条件から、各ケースが観測する譜面値を作ります。</summary>
    internal static ChartFile CreateChart(string path)
    {
        return new ChartFile(ChartFileKind.Bms, path, "hash-" + Path.GetFileNameWithoutExtension(path), null, "Title", "Title", "Artist", "Genre", "Folder", string.Empty, string.Empty, null, null, null);
    }

    /// <summary>各実行区分が同じ準備と観測を使うための共通補助です。</summary>
    internal sealed class RecordingPresentation : IChartMutationPlaybackPort
    {
        internal List<string> Events { get; }

        internal RecordingPresentation(List<string>? events = null)
        {
            Events = events ?? [];
        }

        internal int PathRefreshCalls { get; private set; }

        internal int EncodingRefreshCalls { get; private set; }

        /// <summary>本番store接続試験で停止の実終端だけを保留し、準備済み対象とFSの条件差を観測します。</summary>
        internal Func<Task>? BeforeStop { get; set; }

        internal void OnActivityChanged(object? sender, EventArgs e)
        {
            var activity = (ChartMutationActivityOwner)sender!;
            Events.Add(activity.IsActive ? "activity-start" : "activity-end");
        }

        internal void OnWorkflowChanged(
            object? sender,
            SelectedChartMutationWorkflowChangedEventArgs e)
        {
            switch (e)
            {
                case SelectedChartMutationRefreshSuppressionChangedEventArgs suppressionChanged:
                    if (suppressionChanged.IsSuppressed)
                    {
                        Events.Add(
                            suppressionChanged.Scope == SelectedChartMutationRefreshScope.Pending
                                ? "pending-refresh-start"
                                : "library-refresh-start");
                    }
                    else
                    {
                        Events.Add(
                            Events.Contains("pending-refresh-start") && !Events.Contains("library-refresh-end")
                                ? "pending-refresh-end"
                                : "library-refresh-end");
                    }
                    break;
                case SelectedChartMutationAppliedEventArgs mutationApplied:
                    if (mutationApplied.LibraryPathChanged)
                    {
                        PathRefreshCalls++;
                        Events.Add("path-refresh");
                    }
                    if (mutationApplied.EncodingChanged)
                    {
                        EncodingRefreshCalls++;
                        Events.Add("encoding-refresh");
                    }
                    break;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(e),
                        e,
                        "Unsupported selected chart mutation workflow change.");
            }
        }

        public Task StopPlaybackForMutationAsync()
        {
            Events.Add("playback-stop");
            return BeforeStop?.Invoke() ?? Task.CompletedTask;
        }
    }

    /// <summary>各実行区分が同じ準備と観測を使うための共通補助です。</summary>
    internal class RecordingStore : ISelectedChartMutationStore
    {
        private readonly List<string>? events;

        internal RecordingStore(List<string>? events = null)
        {
            this.events = events;
        }

        internal IReadOnlyList<string> WholeFolderDeletePaths { get; set; } = [];

        internal IReadOnlyList<string> ApprovedWholeFolderDeletePaths { get; private set; } = [];

        internal IReadOnlyList<LibraryChartRef> LibraryCharts { get; private set; } = [];

        internal int LibraryDeleteCalls { get; private set; }

        internal int PhysicalMutationStarts { get; private set; }

        public bool TryBeginPhysicalMutation(BMSLibrary library, IEnumerable<string> paths, bool recursive, out LibraryFileMutationLease? lease)
        { PhysicalMutationStarts++; lease = null; return true; }

        internal int PendingDeleteCalls { get; private set; }

        internal bool DeleteContainingPackageFoldersWhenNoBms { get; private set; }

        internal List<string> RenameOperations { get; } = [];

        internal int RenameCallCount { get; private set; }

        internal string MovedDirectory { get; private set; } = string.Empty;

        internal int EncodingCallCount { get; private set; }

        internal Exception? Failure { get; set; }

        public virtual LibraryChartRemovalPreflight PrepareLibraryChartRemoval(BMSLibrary library, IReadOnlyList<LibraryChartRef> charts)
            => new(charts.Select(chart => chart.ToChartFileIdentity()),
                WholeFolderDeletePaths, [], charts.Count, charts.Count(chart => chart.Token == null));

        public LibraryFileExtensionRenameBatch PrepareLibraryFileExtensionRenameBatch(
            BMSLibrary library, IReadOnlyList<ChartFile> charts, string newExtension)
            => new(charts, newExtension);

        internal LibraryChartRemovalOutcome RemovalOutcome { get; set; } = null!;

        public LibraryChartRemovalOutcome RemoveLibraryCharts(BMSLibrary library, LibraryChartRemovalPreflight prepared, IReadOnlyList<string> approvedWholeFolderDeletePaths, out Action? publication)
        {
            publication = null;
            IReadOnlyList<LibraryChartRef> charts = prepared.Targets.Select(chart => LibraryChartRef.FromChartFile(chart)).ToArray();
            ThrowIfConfigured();
            LibraryDeleteCalls++;
            events?.Add("store-library-delete");
            LibraryCharts = charts;
            ApprovedWholeFolderDeletePaths = approvedWholeFolderDeletePaths;
            return RemovalOutcome ?? new LibraryChartRemovalOutcome(charts.Select(chart => new LibraryChartRemovalTarget(chart.Path, LibraryChartRemovalState.Confirmed)), true, true);
        }

        public void RemovePendingCharts(BMSLibrary library, IReadOnlyList<ChartFile> charts, bool sendToRecycleBin, bool deleteContainingPackageFoldersWhenNoBms)
        {
            ThrowIfConfigured();
            PendingDeleteCalls++;
            events?.Add("store-pending-delete");
            DeleteContainingPackageFoldersWhenNoBms = deleteContainingPackageFoldersWhenNoBms;
        }

        public virtual LibraryMutationSessionReceipt RenameLibraryChartsWithReceipt(
            BMSLibrary library,
            IReadOnlyList<LibraryFileExtensionRenameBatch> batches)
        {
            ThrowIfConfigured();
            events?.Add("store-library-rename");
            RenameOperations.AddRange(batches.Select(batch => "library:" + batch.NewExtension));
            RenameCallCount++;
            return LibraryMutationSessionReceipt.Empty;
        }

        public void RenamePendingCharts(BMSLibrary library, IReadOnlyList<ChartFile> charts, string newExtension)
        {
            ThrowIfConfigured();
            events?.Add("store-pending-rename");
            RenameOperations.Add("pending:" + newExtension);
            RenameCallCount++;
        }

        public virtual LibraryMutationSessionReceipt MoveLibraryChartsWithReceipt(
            BMSLibrary library, ChartLibraryMoveRequest request)
        {
            ThrowIfConfigured();
            events?.Add("store-move");
            MovedDirectory = request.NewParentDirectory;
            return new LibraryMutationSessionReceipt(
                [new LibraryMutationSessionTarget(request.Charts[0].Path, request.NewParentDirectory)],
                durableCommit: true,
                catalogChartPathChangeCount: 1,
                folderReferenceMoveCount: 1);
        }

        public virtual void SetBMSFilesEncoding(
            BMSLibrary library,
            IReadOnlyList<ChartFile> bmsFiles,
            string encoding, IReadOnlyList<PackageChartEntry> pendingEntries)
        {
            EncodingCallCount++;
            ThrowIfConfigured();
            events?.Add("store-encoding");
            EncodingFiles = bmsFiles;
            Encoding = encoding;
        }

        internal IReadOnlyList<ChartFile> EncodingFiles { get; private set; } = [];

        internal string Encoding { get; private set; } = string.Empty;

        private void ThrowIfConfigured()
        {
            if (Failure != null)
            {
                throw Failure;
            }
        }
    }

    /// <summary>各実行区分が同じ準備と観測を使うための共通補助です。</summary>
    internal sealed class FakeUiDialogService : IUiDialogService, IPendingDeleteConfirmationDialogPort
    {
        internal Queue<UiDialogResult> ConfirmationResults { get; set; } = new();

        internal UiInteractionResult<bool> PendingDeleteResult { get; set; } = null!;

        internal List<UiMessageRequest> Messages { get; } = [];
        internal Action? OnMessage { get; set; }
        internal Action? OnConfirmation { get; set; }
        internal Action<UiConfirmationRequest>? ConfirmationObserved { get; set; }
        internal Func<UiConfirmationRequest, Task<UiDialogResult>>? ConfirmationHandler { get; set; }
        internal TaskCompletionSource<UiDialogResult>? FirstConfirmation { get; set; }
        internal TaskCompletionSource<bool>? ConfirmationReached { get; set; }
        private int confirmationCount;

        public Task<UiDialogResult> ShowMessageAsync(UiMessageRequest request, CancellationToken cancellationToken = default)
        {
            Messages.Add(request);
            OnMessage?.Invoke();
            return Task.FromResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));
        }

        public Task<UiDialogResult> ConfirmAsync(UiConfirmationRequest request, CancellationToken cancellationToken = default)
        {
            ConfirmationObserved?.Invoke(request);
            if (ConfirmationHandler != null) { return ConfirmationHandler(request); }
            confirmationCount++;
            if (confirmationCount == 1)
            {
                OnConfirmation?.Invoke();
                ConfirmationReached?.TrySetResult(true);
                if (FirstConfirmation != null) { return FirstConfirmation.Task; }
            }
            if (ConfirmationResults.Count == 0)
            {
                throw new InvalidOperationException("No confirmation result configured.");
            }
            return Task.FromResult(ConfirmationResults.Dequeue());
        }

        public Task<UiWindowDialogResult<TResult>> ShowWindowAsync<TWindow, TResult>(UiWindowDialogRequest<TWindow, TResult> request, CancellationToken cancellationToken = default)
            where TWindow : Window
        {
            throw new NotSupportedException();
        }

        public Task<UiInteractionResult<bool>> ShowAsync()
        {
            return Task.FromResult(PendingDeleteResult);
        }

        public Task<UiFilePickerResult> PickFileAsync(UiFilePickerRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<UiFolderPickerResult> PickFolderAsync(UiFolderPickerRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<UiSaveFilePickerResult> PickSaveFileAsync(UiSaveFilePickerRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<UiProgressResult> RunWithProgressAsync(UiProgressRequest request, Func<UiProgressContext, Task> operation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

}
