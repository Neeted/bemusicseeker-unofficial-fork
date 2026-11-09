using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>複数の実行区分で同じ準備・観測を共有します。</summary>
internal static class PendingPackageWorkflowOwnerTestSupport
{
    /// <summary>各ケースが渡す依存を使って検証対象の管理主体を構成し、既存の通知接続を維持します。</summary>
    internal static PendingPackageWorkflowOwner CreateOwner(
        Func<BMSLibrary> libraryProvider,
        List<string> events,
        IPendingPackageStore store,
        IUiDialogService dialogs,
        Func<string, ExplorerOpenResult>? explorerOpener = null,
        Func<string, ExplorerOpenResult>? fileExplorerOpener = null,
        RecordingPresentation? presentation = null,
        IChartMutationPlaybackPort? playback = null,
        ChartFileOperationSynchronizer? chartFileOperations = null,
        Func<InstallDestinationWorkflowSettingsSnapshot>? settingsProvider = null)
    {
        presentation ??= new RecordingPresentation(events);
        ChartMutationActivityOwner activity = new();
        var owner = new PendingPackageWorkflowOwner(
            libraryProvider,
            chartFileOperations ?? new ChartFileOperationSynchronizer(),
            activity,
            playback ?? new RecordingPlayback(events),
            dialogs,
            settingsProvider ?? DefaultSettings,
            new TestExternalShellGateway(
                explorerOpener ?? (_ => new ExplorerOpenResult()),
                fileExplorerOpener ?? (_ => new ExplorerOpenResult())),
            store);
        owner.WorkflowChanged += presentation.OnWorkflowChanged;
        activity.ActivityChanged += presentation.OnActivityChanged;
        return owner;
    }

    /// <summary>各実行区分が同じ準備と観測を使うための共通補助です。</summary>
    internal sealed class TestExternalShellGateway : IExternalShellGateway
    {
        private readonly Func<string, ExplorerOpenResult> explorerOpener;
        private readonly Func<string, ExplorerOpenResult> fileExplorerOpener;

        internal TestExternalShellGateway(
            Func<string, ExplorerOpenResult> explorerOpener,
            Func<string, ExplorerOpenResult> fileExplorerOpener)
        {
            this.explorerOpener = explorerOpener;
            this.fileExplorerOpener = fileExplorerOpener;
        }

        public void Open(ExternalShellRequest request)
        {
        }

        public ExplorerOpenResult OpenFileAndSelect(string filePath) => fileExplorerOpener(filePath);

        public ExplorerOpenResult OpenDirectory(string directoryPath) => explorerOpener(directoryPath);

        public bool TryOpenDirectoryWithExplorerProcess(string directoryPath, out string failureReason)
        {
            failureReason = string.Empty;
            return true;
        }
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
    internal static InstallDestinationWorkflowSettingsSnapshot DefaultSettings()
    {
        return new InstallDestinationWorkflowSettingsSnapshot(
            showManualInstallConfirmation: false,
            deletePendingPackageSourceAfterInstall: false,
            showNewPackageInstallConfirmation: false);
    }

    /// <summary>指定された識別・配置条件から、各ケースが観測する譜面値を作ります。</summary>
    internal static ChartFile CreateChart(
        string path = @"C:\Charts\song.bms",
        string? installDestination = null)
    {
        return new ChartFile(ChartFileKind.Bms, path, md5: "0123456789abcdef0123456789abcdef", sha256: null, title: "Title", rawTitle: "Title", artist: "Artist", genre: "Genre", folder: "Charts", tag: null, levelText: null, level: null, mode: null, chartInfo: null, installDestination: installDestination);
    }

    /// <summary>指定された譜面と操作条件を、そのまま検証対象へ渡す操作対象にまとめます。</summary>
    internal static ChartOperationTarget CreateTarget(
        ChartFile chart,
        ChartOperationCapabilities capabilities = ChartOperationCapabilities.UpdateInstallDestination)
    {
        return new ChartOperationTarget(
            chart,
            null,
            ChartOperationSourceScope.PendingPackage,
            isOwned: false,
            isPending: true,
            isPlaylistMissing: false,
            capabilities,
            PackageChartEntry.FromChart(chart));
    }

    /// <summary>各実行区分が同じ準備と観測を使うための共通補助です。</summary>
    internal sealed class RecordingPresentation
    {
        private readonly List<string> events;

        internal RecordingPresentation(List<string> events)
        {
            this.events = events;
        }

        internal Exception? EndActivityFailure { get; set; }

        internal Exception? EndRefreshSuppressionFailure { get; set; }

        internal Exception? StartRefreshSuppressionFailure { get; set; }

        internal void OnActivityChanged(object? sender, EventArgs e)
        {
            var activity = (ChartMutationActivityOwner)sender!;
            events.Add(activity.IsActive ? "activity-start" : "activity-end");
            if (!activity.IsActive && EndActivityFailure != null)
            {
                throw EndActivityFailure;
            }
        }

        internal PendingPackageRefreshScope LastRefreshScope { get; private set; }

        internal void OnWorkflowChanged(
            object? sender,
            PendingPackageWorkflowChangedEventArgs e)
        {
            switch (e)
            {
                case PendingPackageRefreshSuppressionChangedEventArgs suppressionChanged:
                    if (suppressionChanged.IsSuppressed)
                    {
                        if (StartRefreshSuppressionFailure != null)
                        {
                            throw StartRefreshSuppressionFailure;
                        }
                        LastRefreshScope = suppressionChanged.Scope
                            ?? throw new InvalidOperationException("Refresh suppression scope was not published.");
                        events.Add("suppression-start");
                    }
                    else
                    {
                        events.Add("suppression-end");
                        if (EndRefreshSuppressionFailure != null)
                        {
                            throw EndRefreshSuppressionFailure;
                        }
                    }
                    break;
                case PendingPackageMutationAppliedEventArgs mutationApplied:
                    if (mutationApplied.ChangedCharts.Count > 0)
                    {
                        events.Add("transient");
                    }
                    if (mutationApplied.InstallDestinationStateChanged)
                    {
                        events.Add("invalidate");
                    }
                    if (mutationApplied.IdentitySortKeyChanged)
                    {
                        events.Add("identity-refresh");
                    }
                    if (mutationApplied.DisplayStateChanged)
                    {
                        events.Add("display-refresh");
                    }
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(e), e, "Unsupported pending-package workflow change.");
            }
        }
    }

    /// <summary>各実行区分が同じ準備と観測を使うための共通補助です。</summary>
    internal sealed class RecordingPlayback : IChartMutationPlaybackPort
    {
        private readonly List<string> events;

        internal RecordingPlayback(List<string> events)
        {
            this.events = events;
        }


        public Task StopPlaybackForMutationAsync()
        {
            events.Add("playback-stop");
            return Task.CompletedTask;
        }
    }

    /// <summary>各実行区分が同じ準備と観測を使うための共通補助です。</summary>
    internal sealed class FakeUiDialogService : IUiDialogService
    {
        private readonly Queue<UiDialogResult> confirmationResults = new();

        internal UiDialogResult? ConfirmationResult { get; set; }

        internal UiConfirmationRequest? ConfirmationRequest { get; private set; }

        internal Func<UiConfirmationRequest, Task<UiDialogResult>>? ConfirmationHandler { get; set; }

        internal UiMessageRequest? MessageRequest { get; private set; }

        internal List<UiMessageRequest> MessageRequests { get; } = [];

        internal UiDialogResult? MessageResult { get; set; }

        /// <summary>メッセージ表示の要求を受けた時点で呼びます。表示の要求が、どの受付の状態で届いたかを観測するために使います。</summary>
        internal Action? OnMessage { get; set; }

        internal UiProgressRequest? ProgressRequest { get; private set; }

        internal UiProgressResult? ProgressResult { get; set; }

        internal void EnqueueConfirmation(UiDialogResult result)
        {
            confirmationResults.Enqueue(result);
        }

        public Task<UiDialogResult> ShowMessageAsync(UiMessageRequest request, CancellationToken cancellationToken = default)
        {
            MessageRequest = request;
            MessageRequests.Add(request);
            OnMessage?.Invoke();
            return Task.FromResult(MessageResult ?? UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));
        }

        public Task<UiDialogResult> ConfirmAsync(UiConfirmationRequest request, CancellationToken cancellationToken = default)
        {
            ConfirmationRequest = request;
            if (ConfirmationHandler != null)
            {
                return ConfirmationHandler(request);
            }
            if (confirmationResults.Count > 0)
            {
                return Task.FromResult(confirmationResults.Dequeue());
            }
            return Task.FromResult(ConfirmationResult ?? throw new InvalidOperationException("Confirmation result was not configured."));
        }

        public Task<UiWindowDialogResult<TResult>> ShowWindowAsync<TWindow, TResult>(UiWindowDialogRequest<TWindow, TResult> request, CancellationToken cancellationToken = default)
            where TWindow : Window => throw new NotSupportedException();

        public Task<UiFilePickerResult> PickFileAsync(UiFilePickerRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<UiFolderPickerResult> PickFolderAsync(UiFolderPickerRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<UiSaveFilePickerResult> PickSaveFileAsync(UiSaveFilePickerRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<UiProgressResult> RunWithProgressAsync(UiProgressRequest request, Func<UiProgressContext, Task> operation, CancellationToken cancellationToken = default)
        {
            ProgressRequest = request;
            return Task.FromResult(ProgressResult ?? new UiProgressResult(UiDialogStatus.Accepted));
        }
    }

    /// <summary>各実行区分が同じ準備と観測を使うための共通補助です。</summary>
    internal sealed class RecordingLibraryDialogService : IBmsLibraryDialogService
    {
        internal int CallCount { get; private set; }

        public UiDialogDefaultResult Confirm(
            string messageBoxText,
            string caption,
            UiDialogButton button,
            UiDialogIcon icon,
            UiDialogDefaultResult defaultResult = UiDialogDefaultResult.None)
        {
            CallCount++;
            return defaultResult == UiDialogDefaultResult.None ? UiDialogDefaultResult.OK : defaultResult;
        }

        /// <summary>待たない投函で届いた通知の件数です。同期の表示の <see cref="CallCount"/> とは別に数えます。</summary>
        internal int PostCount { get; private set; }

        public void Post(IReadOnlyList<BMSLibrary.OperationDialogMessage> messages, Func<bool> shouldSuppress) => PostCount += messages.Count;
    }

}
