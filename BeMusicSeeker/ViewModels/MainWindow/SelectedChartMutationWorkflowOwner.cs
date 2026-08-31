using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Views.Dialogs;
using MessageBoxButton = BeMusicSeeker.Models.UiDialogButton;
using MessageBoxImage = BeMusicSeeker.Models.UiDialogIcon;
using MessageBoxResult = BeMusicSeeker.Models.UiDialogDefaultResult;

namespace BeMusicSeeker.ViewModels;

internal enum SelectedChartMutationRefreshScope
{
    Pending,
    Library
}

internal abstract class SelectedChartMutationWorkflowChangedEventArgs : EventArgs
{
}

internal sealed class SelectedChartMutationRefreshSuppressionChangedEventArgs : SelectedChartMutationWorkflowChangedEventArgs
{
    internal SelectedChartMutationRefreshSuppressionChangedEventArgs(
        bool isSuppressed,
        SelectedChartMutationRefreshScope? scope)
    {
        IsSuppressed = isSuppressed;
        Scope = scope;
    }

    internal bool IsSuppressed { get; }

    internal SelectedChartMutationRefreshScope? Scope { get; }
}

internal sealed class SelectedChartMutationAppliedEventArgs : SelectedChartMutationWorkflowChangedEventArgs
{
    internal SelectedChartMutationAppliedEventArgs(
        bool libraryPathChanged = false,
        bool encodingChanged = false)
    {
        LibraryPathChanged = libraryPathChanged;
        EncodingChanged = encodingChanged;
    }

    internal bool LibraryPathChanged { get; }

    internal bool EncodingChanged { get; }
}

internal interface ISelectedChartMutationPlaybackPort
{
    void StopPlaybackForPendingCharts(IReadOnlyList<ChartFile> charts);

    void StopPlaybackForLibraryCharts(IReadOnlyList<LibraryChartRef> charts);

    void StopPlaybackForChartDirectories(IReadOnlyList<string> directories);
}

internal interface IPendingDeleteConfirmationDialogPort
{
    Task<UiInteractionResult<bool>> ShowAsync();
}

internal interface ISelectedChartMutationStore
{
    IReadOnlyList<string> GetLibraryWholeFolderDeleteConfirmationPaths(
        BMSLibrary library,
        IReadOnlyList<LibraryChartRef> charts);

    void RemoveLibraryCharts(
        BMSLibrary library,
        IReadOnlyList<LibraryChartRef> charts,
        IReadOnlyList<string> approvedWholeFolderDeletePaths);

    void RemovePendingCharts(
        BMSLibrary library,
        IReadOnlyList<ChartFile> charts,
        bool sendToRecycleBin,
        bool deleteContainingPackageFoldersWhenNoBms);

    void RenameLibraryCharts(
        BMSLibrary library,
        IReadOnlyList<ChartFile> charts,
        string newExtension);

    void RenamePendingCharts(
        BMSLibrary library,
        IReadOnlyList<ChartFile> charts,
        string newExtension);

    void MoveLibraryCharts(BMSLibrary library, ChartLibraryMoveRequest request);

    void SetBMSFilesEncoding(
        BMSLibrary library,
        IReadOnlyList<BMSFile> bmsFiles,
        string encoding);
}

internal interface ISelectedChartMutationTerminalStore
{
    FileDbMutationBatchReceipt MoveLibraryChartsWithReceipt(
        BMSLibrary library,
        ChartLibraryMoveRequest request);
}

internal sealed class SelectedChartDeleteRequest
{
    internal SelectedChartDeleteRequest(
        IEnumerable<ChartOperationTarget> selectedTargets,
        ChartOperationTarget contextTarget,
        MainViewOperationSection section)
    {
        SelectedTargets = (selectedTargets ?? []).Where(target => target != null).ToArray();
        ContextTarget = contextTarget;
        Section = section;
    }

    internal IReadOnlyList<ChartOperationTarget> SelectedTargets { get; }

    internal ChartOperationTarget ContextTarget { get; }

    internal MainViewOperationSection Section { get; }
}

internal sealed class SelectedInvalidExtensionRenameRequest
{
    internal SelectedInvalidExtensionRenameRequest(
        IEnumerable<ChartOperationTarget> targets,
        bool isPendingSelected)
    {
        Targets = (targets ?? []).Where(target => target != null).ToArray();
        IsPendingSelected = isPendingSelected;
    }

    internal IReadOnlyList<ChartOperationTarget> Targets { get; }

    internal bool IsPendingSelected { get; }
}

internal sealed class SelectedChartMoveRequest
{
    internal SelectedChartMoveRequest(
        IEnumerable<ChartOperationTarget> targets,
        string newParentDirectory)
    {
        Targets = (targets ?? []).Where(target => target != null).ToArray();
        NewParentDirectory = newParentDirectory;
    }

    internal IReadOnlyList<ChartOperationTarget> Targets { get; }

    internal string NewParentDirectory { get; }
}

internal sealed class SelectedChartMutationResult
{
    private SelectedChartMutationResult(
        bool succeeded,
        Exception failure,
        FileDbMutationBatchReceipt mutationReceipt = null)
    {
        Succeeded = succeeded;
        Failure = failure;
        MutationReceipt = mutationReceipt;
    }

    internal bool Succeeded { get; }

    internal Exception Failure { get; }

    internal FileDbMutationBatchReceipt MutationReceipt { get; }

    internal bool HasDurableCommit => MutationReceipt?.HasDurableCommit == true;

    internal bool ManualRecoveryRequired => MutationReceipt?.ManualRecoveryRequired == true;

    internal bool CompletedWithCleanupFailure => MutationReceipt?.CompletedWithCleanupFailure == true;

    internal IReadOnlyList<string> RecoveryPaths => MutationReceipt?.RecoveryPaths ?? [];

    internal static SelectedChartMutationResult Completed { get; } = new(true, null);

    internal static SelectedChartMutationResult FromReceipt(FileDbMutationBatchReceipt mutationReceipt)
    {
        return new SelectedChartMutationResult(
            mutationReceipt?.ManualRecoveryRequired != true,
            null,
            mutationReceipt);
    }

    internal static SelectedChartMutationResult Failed(Exception failure)
    {
        return new SelectedChartMutationResult(
            false,
            failure ?? throw new ArgumentNullException(nameof(failure)));
    }
}

internal sealed class SelectedChartEncodingRequest
{
    internal SelectedChartEncodingRequest(
        IEnumerable<ChartOperationTarget> targets,
        string encoding)
    {
        BmsFiles = (targets ?? [])
            .Where(target => target?.HasCapability(ChartOperationCapabilities.RunBmsEncodingFix) == true
                && ChartFileKindResolver.IsBmsChartFile(target.Chart))
            .Select(target => target.Chart.GetBmsStorageOwner())
            .Where(ChartFileKindResolver.IsBmsChartFile)
            .ToArray();
        Encoding = encoding ?? string.Empty;
    }

    internal IReadOnlyList<BMSFile> BmsFiles { get; }

    internal string Encoding { get; }

    internal bool HasTargets => BmsFiles.Count > 0;
}

internal sealed class SelectedChartMutationWorkflowOwner
{
    private readonly Func<BMSLibrary> libraryProvider;
    private readonly ChartFileOperationSynchronizer chartFileOperations;
    private readonly ChartMutationActivityOwner chartMutationActivity;
    private readonly ISelectedChartMutationPlaybackPort playback;
    private readonly IUiDialogService dialogs;
    private readonly IPendingDeleteConfirmationDialogPort pendingDeleteDialog;
    private readonly ISelectedChartMutationStore store;

    internal SelectedChartMutationWorkflowOwner(
        Func<BMSLibrary> libraryProvider,
        ChartFileOperationSynchronizer chartFileOperations,
        ChartMutationActivityOwner chartMutationActivity,
        ISelectedChartMutationPlaybackPort playback,
        IUiDialogService dialogs,
        IPendingDeleteConfirmationDialogPort pendingDeleteDialog,
        ISelectedChartMutationStore store = null)
    {
        this.libraryProvider = libraryProvider ?? throw new ArgumentNullException(nameof(libraryProvider));
        this.chartFileOperations = chartFileOperations ?? throw new ArgumentNullException(nameof(chartFileOperations));
        this.chartMutationActivity = chartMutationActivity ?? throw new ArgumentNullException(nameof(chartMutationActivity));
        this.playback = playback ?? throw new ArgumentNullException(nameof(playback));
        this.dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        this.pendingDeleteDialog = pendingDeleteDialog ?? throw new ArgumentNullException(nameof(pendingDeleteDialog));
        this.store = store ?? new BmsLibrarySelectedChartMutationStore();
    }

    internal event EventHandler<SelectedChartMutationWorkflowChangedEventArgs> WorkflowChanged;

    internal async Task<SelectedChartMutationResult> DeleteAsync(SelectedChartDeleteRequest request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }
        try
        {
            ChartDeleteTargetResolution resolution = ChartDeleteTargetResolver.Resolve(
                request.SelectedTargets,
                request.ContextTarget,
                request.Section);
            if (resolution.Route == ChartDeleteRoute.None || resolution.Targets.Count == 0)
            {
                return SelectedChartMutationResult.Completed;
            }

            bool deleteContainingPackageFoldersWhenNoBms = false;
            if (resolution.Route == ChartDeleteRoute.Pending)
            {
                UiInteractionResult<bool> dialogResult = await pendingDeleteDialog.ShowAsync();
                if (dialogResult == null)
                {
                    return SelectedChartMutationResult.Failed(
                        new InvalidOperationException("Pending delete confirmation returned no result."));
                }
                if (!dialogResult.IsAccepted)
                {
                    return dialogResult.Status is UiInteractionStatus.CancelledByUser or UiInteractionStatus.ClosedByUser
                        ? SelectedChartMutationResult.Completed
                        : SelectedChartMutationResult.Failed(
                            dialogResult.Error ?? new InvalidOperationException(
                                "Pending delete confirmation could not be displayed (" + dialogResult.Status + ")."));
                }
                deleteContainingPackageFoldersWhenNoBms = dialogResult.Value;
            }
            else if (!await ConfirmMessageAsync(
                BeMusicSeeker.Properties.Resources.Msg_move_to_recycle,
                "Selected library chart deletion confirmation"))
            {
                return SelectedChartMutationResult.Completed;
            }

            List<LibraryChartRef> libraryCharts = resolution.Route == ChartDeleteRoute.Library
                ? [.. resolution.Targets
                    .Where(target => target != null && target.HasCapability(ChartOperationCapabilities.RemoveFromLibrary))
                    .Select(target => target.ToLibraryChartRef())
                    .Where(chart => chart != null)]
                : [];
            List<ChartFile> pendingCharts = resolution.Route == ChartDeleteRoute.Pending
                ? [.. resolution.Targets
                    .Where(target => target != null && target.HasCapability(ChartOperationCapabilities.UpdateInstallDestination))
                    .Select(target => target.Chart)
                    .Where(chart => chart != null)]
                : [];
            List<string> approvedWholeFolderDeletePaths = [];
            if (libraryCharts.Count > 0)
            {
                BMSLibrary library = RequireLibrary();
                IReadOnlyList<string> candidatePaths;
                using (chartFileOperations.Enter())
                {
                    candidatePaths = store.GetLibraryWholeFolderDeleteConfirmationPaths(library, libraryCharts) ?? [];
                }
                foreach (string folderPath in candidatePaths)
                {
                    bool approved = await ConfirmMessageAsync(
                        string.Format(BeMusicSeeker.Properties.Resources.Confirm_DeleteFolderWithNoBms, folderPath),
                        BeMusicSeeker.Properties.Resources.MessageBoxTitle_Confirm,
                        MessageBoxButton.YesNo,
                        MessageBoxResult.Yes);
                    if (approved)
                    {
                        approvedWholeFolderDeletePaths.Add(folderPath);
                    }
                }
            }

            IReadOnlyList<string> approvedFolderPaths = approvedWholeFolderDeletePaths.ToArray();
            return await Task.Run(() => ExecuteMutation(
                resolution.Route == ChartDeleteRoute.Pending
                ? SelectedChartMutationRefreshScope.Pending
                : SelectedChartMutationRefreshScope.Library,
            () =>
            {
                if (resolution.Route == ChartDeleteRoute.Pending)
                {
                    IReadOnlyList<ChartFile> playbackCharts = deleteContainingPackageFoldersWhenNoBms
                        ? pendingCharts
                        : [.. pendingCharts.Where(ChartFileKindResolver.IsBmsChartFile)];
                    playback.StopPlaybackForPendingCharts(playbackCharts);
                    return;
                }
                playback.StopPlaybackForLibraryCharts(
                    [.. libraryCharts.Where(chart => chart?.Kind == LibraryChartKind.Bms)]);
                playback.StopPlaybackForChartDirectories(approvedFolderPaths);
            },
            library =>
            {
                if (resolution.Route == ChartDeleteRoute.Pending)
                {
                    store.RemovePendingCharts(
                        library,
                        pendingCharts,
                        sendToRecycleBin: true,
                        deleteContainingPackageFoldersWhenNoBms);
                    return;
                }

                store.RemoveLibraryCharts(
                    library,
                    libraryCharts,
                    approvedFolderPaths);
            })).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return SelectedChartMutationResult.Failed(ex);
        }
    }

    internal Task<SelectedChartMutationResult> RenameInvalidExtensionsAsync(
        SelectedInvalidExtensionRenameRequest request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }
        try
        {
            List<ChartOperationTarget> targets = [.. request.Targets
                .Where(target => target?.HasCapability(ChartOperationCapabilities.RenameInvalidExtension) == true)
                .Where(target => ChartFileKindResolver.IsBmsChartFile(target.Chart))];
            if (targets.Count == 0)
            {
                return Task.FromResult(SelectedChartMutationResult.Completed);
            }
            if (targets.Any(target =>
                target.IsPending != request.IsPendingSelected
                || (target.SourceScope == ChartOperationSourceScope.PendingPackage)
                != request.IsPendingSelected))
            {
                return Task.FromResult(SelectedChartMutationResult.Failed(
                    new InvalidOperationException(
                        "Selected chart rename targets do not match the current operation section.")));
            }

            List<ChartFile> charts = [.. targets.Select(target => target.Chart)];
            if (!ConfirmMessage(
                BeMusicSeeker.Properties.Resources.Msg_rename_to_invalid,
                "Invalid chart extension rename confirmation"))
            {
                return Task.FromResult(SelectedChartMutationResult.Completed);
            }

            IReadOnlyList<ChartFile> bCharts = [.. charts.Where(chart =>
                (Path.GetExtension(chart.Path) ?? string.Empty).StartsWith(".b", StringComparison.OrdinalIgnoreCase))];
            IReadOnlyList<ChartFile> pCharts = [.. charts.Where(chart =>
                (Path.GetExtension(chart.Path) ?? string.Empty).StartsWith(".p", StringComparison.OrdinalIgnoreCase))];
            IReadOnlyList<ChartFile> allCharts = [.. bCharts.Concat(pCharts)];
            Action stopPlayback;
            if (request.IsPendingSelected)
            {
                stopPlayback = () => playback.StopPlaybackForPendingCharts(allCharts);
            }
            else
            {
                stopPlayback = () => playback.StopPlaybackForLibraryCharts(
                    [.. allCharts.Select(LibraryChartRef.FromChartFile).Where(chart => chart != null)]);
            }
            return Task.Run(() => ExecuteMutation(
                request.IsPendingSelected
                    ? SelectedChartMutationRefreshScope.Pending
                    : SelectedChartMutationRefreshScope.Library,
                stopPlayback,
                library =>
                {
                    if (request.IsPendingSelected)
                    {
                        if (bCharts.Count > 0)
                        {
                            store.RenamePendingCharts(library, bCharts, ".bmx");
                        }
                        if (pCharts.Count > 0)
                        {
                            store.RenamePendingCharts(library, pCharts, ".pmx");
                        }
                        return;
                    }
                    if (bCharts.Count > 0)
                    {
                        store.RenameLibraryCharts(library, bCharts, ".bmx");
                    }
                    if (pCharts.Count > 0)
                    {
                        store.RenameLibraryCharts(library, pCharts, ".pmx");
                    }
                }));
        }
        catch (Exception ex)
        {
            return Task.FromResult(SelectedChartMutationResult.Failed(ex));
        }
    }

    internal async Task<SelectedChartMutationResult> MoveAsync(SelectedChartMoveRequest request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }
        try
        {
            if (string.IsNullOrWhiteSpace(request.NewParentDirectory)
                || !ChartLibraryMoveRequest.TryCreate(
                    request.Targets,
                    request.NewParentDirectory,
                    out ChartLibraryMoveRequest moveRequest)
                || !await ConfirmMessageAsync(
                    BeMusicSeeker.Properties.Resources.Msg_move_to_other_root,
                    "Selected chart library move confirmation"))
            {
                return SelectedChartMutationResult.Completed;
            }

            if (store is ISelectedChartMutationTerminalStore terminalStore)
            {
                return await Task.Run(() => ExecuteMutation(
                    SelectedChartMutationRefreshScope.Library,
                    () => playback.StopPlaybackForLibraryCharts(moveRequest.Charts),
                    mutation: null,
                    mutationWithReceipt: library => terminalStore.MoveLibraryChartsWithReceipt(library, moveRequest),
                    publishMutationApplied: true)).ConfigureAwait(false);
            }
            return await Task.Run(() => ExecuteMutation(
                SelectedChartMutationRefreshScope.Library,
                () => playback.StopPlaybackForLibraryCharts(moveRequest.Charts),
                library =>
                {
                    store.MoveLibraryCharts(library, moveRequest);
                    PublishMutationApplied(libraryPathChanged: true);
                })).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return SelectedChartMutationResult.Failed(ex);
        }
    }

    internal SelectedChartMutationResult ApplyEncoding(SelectedChartEncodingRequest request)
    {
        if (request?.HasTargets != true)
        {
            return SelectedChartMutationResult.Completed;
        }

        try
        {
            store.SetBMSFilesEncoding(RequireLibrary(), request.BmsFiles, request.Encoding);
            PublishMutationApplied(encodingChanged: true);
            return SelectedChartMutationResult.Completed;
        }
        catch (Exception ex)
        {
            return SelectedChartMutationResult.Failed(ex);
        }
    }

    private SelectedChartMutationResult ExecuteMutation(
        SelectedChartMutationRefreshScope refreshScope,
        Action stopPlayback,
        Action<BMSLibrary> mutation,
        Func<BMSLibrary, FileDbMutationBatchReceipt> mutationWithReceipt = null,
        bool publishMutationApplied = false)
    {
        BMSLibrary library;
        try
        {
            library = RequireLibrary();
        }
        catch (Exception ex)
        {
            return SelectedChartMutationResult.Failed(ex);
        }

        BMSLibrary.OperationDialogScope dialogScope = null;
        IDisposable operationGate = null;
        IDisposable activityLease = null;
        bool suppressionStarted = false;
        FileDbMutationBatchReceipt mutationReceipt = null;
        var failures = new List<ExceptionDispatchInfo>();
        try
        {
            dialogScope = library.BeginOperationDialogScope();
            activityLease = chartMutationActivity.Enter();
            operationGate = chartFileOperations.Enter();
            stopPlayback?.Invoke();
            suppressionStarted = true;
            PublishRefreshSuppressionChanged(isSuppressed: true, scope: refreshScope);
            mutationReceipt = mutationWithReceipt?.Invoke(library);
            if (mutationWithReceipt == null)
            {
                mutation(library);
            }
            if (publishMutationApplied && mutationReceipt?.HasDurableCommit == true)
            {
                PublishMutationApplied(libraryPathChanged: true);
            }
        }
        catch (Exception ex)
        {
            failures.Add(ExceptionDispatchInfo.Capture(ex));
        }
        finally
        {
            if (suppressionStarted)
            {
                CaptureCleanupFailure(
                    () => PublishRefreshSuppressionChanged(isSuppressed: false, scope: null),
                    failures);
            }
            if (operationGate != null)
            {
                CaptureCleanupFailure(operationGate.Dispose, failures);
            }
            if (activityLease != null)
            {
                CaptureCleanupFailure(activityLease.Dispose, failures);
            }
            if (dialogScope != null)
            {
                CaptureCleanupFailure(dialogScope.Dispose, failures);
                CaptureCleanupFailure(dialogScope.Flush, failures);
            }
        }
        return failures.Count switch
        {
            0 => mutationReceipt == null
                ? SelectedChartMutationResult.Completed
                : SelectedChartMutationResult.FromReceipt(mutationReceipt),
            1 => SelectedChartMutationResult.Failed(failures[0].SourceException),
            _ => SelectedChartMutationResult.Failed(
                new AggregateException(failures.Select(failure => failure.SourceException))),
        };
    }

    private void PublishRefreshSuppressionChanged(
        bool isSuppressed,
        SelectedChartMutationRefreshScope? scope)
    {
        WorkflowChanged?.Invoke(
            this,
            new SelectedChartMutationRefreshSuppressionChangedEventArgs(isSuppressed, scope));
    }

    private void PublishMutationApplied(
        bool libraryPathChanged = false,
        bool encodingChanged = false)
    {
        WorkflowChanged?.Invoke(
            this,
            new SelectedChartMutationAppliedEventArgs(libraryPathChanged, encodingChanged));
    }

    private async Task<bool> ConfirmMessageAsync(
        string message,
        string routeName,
        MessageBoxButton button = MessageBoxButton.OKCancel,
        MessageBoxResult defaultResult = MessageBoxResult.Cancel)
    {
        UiDialogResult result = await dialogs.ConfirmAsync(new UiConfirmationRequest(
            message,
            BeMusicSeeker.Properties.Resources.Confirm,
            button,
            MessageBoxImage.Question,
            defaultResult));
        if (result == null)
        {
            throw new InvalidOperationException(routeName + " returned no result.");
        }
        return result.Status switch
        {
            UiDialogStatus.Accepted => true,
            UiDialogStatus.Rejected or UiDialogStatus.CancelledByUser => false,
            UiDialogStatus.ClosedByUser => result.IsPositive,
            _ => throw result.Exception ?? new InvalidOperationException(
                routeName + " could not be displayed (" + result.Status + ").")
        };
    }

    private bool ConfirmMessage(
        string message,
        string routeName,
        MessageBoxButton button = MessageBoxButton.OKCancel,
        MessageBoxResult defaultResult = MessageBoxResult.Cancel)
    {
        UiDialogResult result = dialogs.ConfirmAsync(new UiConfirmationRequest(
            message,
            BeMusicSeeker.Properties.Resources.Confirm,
            button,
            MessageBoxImage.Question,
            defaultResult))
            .GetAwaiter()
            .GetResult();
        if (result == null)
        {
            throw new InvalidOperationException(routeName + " returned no result.");
        }
        return result.Status switch
        {
            UiDialogStatus.Accepted => true,
            UiDialogStatus.Rejected or UiDialogStatus.CancelledByUser => false,
            UiDialogStatus.ClosedByUser => result.IsPositive,
            _ => throw result.Exception ?? new InvalidOperationException(
                routeName + " could not be displayed (" + result.Status + ").")
        };
    }

    private BMSLibrary RequireLibrary()
    {
        return libraryProvider()
            ?? throw new InvalidOperationException("Selected chart mutation library is not available.");
    }

    private static void CaptureCleanupFailure(Action action, ICollection<ExceptionDispatchInfo> failures)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            failures.Add(ExceptionDispatchInfo.Capture(ex));
        }
    }
}

internal sealed class BmsLibrarySelectedChartMutationStore : ISelectedChartMutationStore, ISelectedChartMutationTerminalStore
{
    public IReadOnlyList<string> GetLibraryWholeFolderDeleteConfirmationPaths(
        BMSLibrary library,
        IReadOnlyList<LibraryChartRef> charts)
    {
        return library.GetLibraryWholeFolderDeleteConfirmationPaths(charts);
    }

    public void RemoveLibraryCharts(
        BMSLibrary library,
        IReadOnlyList<LibraryChartRef> charts,
        IReadOnlyList<string> approvedWholeFolderDeletePaths)
    {
        library.RemoveLibraryCharts(
            charts,
            approvedWholeFolderDeletePaths: approvedWholeFolderDeletePaths);
    }

    public void RemovePendingCharts(
        BMSLibrary library,
        IReadOnlyList<ChartFile> charts,
        bool sendToRecycleBin,
        bool deleteContainingPackageFoldersWhenNoBms)
    {
        library.RemovePendingCharts(charts, sendToRecycleBin, deleteContainingPackageFoldersWhenNoBms);
    }

    public void RenameLibraryCharts(
        BMSLibrary library,
        IReadOnlyList<ChartFile> charts,
        string newExtension)
    {
        library.RenameBMSFilesExtensions(charts, newExtension, true);
    }

    public void RenamePendingCharts(
        BMSLibrary library,
        IReadOnlyList<ChartFile> charts,
        string newExtension)
    {
        library.RenamePendingBmsFormatChartFileExtensions(charts, newExtension);
    }

    public void MoveLibraryCharts(BMSLibrary library, ChartLibraryMoveRequest request)
    {
        _ = MoveLibraryChartsWithReceipt(library, request);
    }

    public FileDbMutationBatchReceipt MoveLibraryChartsWithReceipt(
        BMSLibrary library,
        ChartLibraryMoveRequest request)
    {
        return library.MoveLibraryRootFolderWithReceipt(
            request.Charts,
            request.NewParentDirectory,
            false);
    }

    public void SetBMSFilesEncoding(
        BMSLibrary library,
        IReadOnlyList<BMSFile> bmsFiles,
        string encoding)
    {
        library.SetBMSFilesEncoding(bmsFiles, encoding);
    }
}
