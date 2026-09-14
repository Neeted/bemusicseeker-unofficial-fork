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

    /// <summary>Returns observed library deletion facts to the operation terminal.</summary>
    LibraryChartRemovalOutcome RemoveLibraryCharts(
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
    /// <summary>Renames all selected invalid-extension families in one operation-scoped session.</summary>
    LibraryMutationSessionReceipt RenameLibraryChartsWithReceipt(
        BMSLibrary library,
        IReadOnlyList<LibraryFileExtensionRenameBatch> batches);

    /// <summary>Moves selected library folders and returns operation-scoped terminal facts.</summary>
    LibraryMutationSessionReceipt MoveLibraryChartsWithReceipt(
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
        LibraryMutationSessionReceipt mutationReceipt = null,
        LibraryChartRemovalOutcome removalOutcome = null)
    {
        RemovalOutcome = removalOutcome;
        Succeeded = succeeded;
        Failure = failure;
        MutationReceipt = mutationReceipt;
    }

    /// <summary>Deletion facts retained independently of workflow cleanup failures.</summary>
    internal LibraryChartRemovalOutcome RemovalOutcome { get; }

    /// <summary>Catalog failure prevents success; filesystem-only partial failure preserves continuation.</summary>
    internal static SelectedChartMutationResult FromRemoval(LibraryChartRemovalOutcome outcome, Exception failure)
    {
        Exception primary = outcome?.CatalogFailure;
        Exception combined = primary == null ? failure : failure == null ? primary : new AggregateException(primary, failure);
        return new SelectedChartMutationResult(
            combined == null,
            combined,
            mutationReceipt: outcome?.SessionReceipt,
            removalOutcome: outcome);
    }

    internal bool Succeeded { get; }

    internal Exception Failure { get; }

    /// <summary>Gets operation-scoped terminal facts for the selected library mutation, when applicable.</summary>
    internal LibraryMutationSessionReceipt MutationReceipt { get; }

    /// <summary>Includes the catalog commit observed by library deletion.</summary>
    internal bool HasDurableCommit => RemovalOutcome?.CatalogDurable == true || MutationReceipt?.DurableCommit == true;

    /// <summary>Session-based selected library mutations do not expose executor compensation recovery.</summary>
    internal bool ManualRecoveryRequired => false;

    /// <summary>
    /// Gets whether selected-chart finalization failed after durable state.
    /// </summary>
    internal bool HasDurableFinalizationFailure => RemovalOutcome?.RequiredFinalizationFailed == true || MutationReceipt?.HasDurableFinalizationFailure == true;

    internal bool CompletedWithCleanupFailure => MutationReceipt?.CompletedWithCleanupFailure == true;

    /// <summary>Gets bounded manual-inspection candidates retained by the session.</summary>
    internal IReadOnlyList<string> RecoveryPaths => MutationReceipt?.CandidatePaths ?? [];

    internal static SelectedChartMutationResult Completed { get; } = new(true, null);

    /// <summary>Preserves operation-scoped mutation facts without synthesizing per-item receipts.</summary>
    internal static SelectedChartMutationResult FromReceipt(LibraryMutationSessionReceipt mutationReceipt)
    {
        Exception requiredFailure = mutationReceipt?.PhysicalFailure
            ?? mutationReceipt?.ApplyFailure
            ?? mutationReceipt?.FinalizationFailure;
        bool durableStateRequired = mutationReceipt?.ConfirmedChangeCount > 0;
        bool succeeded = requiredFailure == null
            && (!durableStateRequired || mutationReceipt.DurableCommit);
        if (!succeeded && requiredFailure == null)
        {
            requiredFailure = new InvalidOperationException(
                "Confirmed library mutation did not produce a durable session receipt.");
        }
        return new SelectedChartMutationResult(succeeded, requiredFailure, mutationReceipt);
    }

    /// <summary>Retains receipts when an additional workflow failure occurs after mutation.</summary>
    internal static SelectedChartMutationResult Failed(Exception failure, LibraryMutationSessionReceipt mutationReceipt = null)
    {
        return new SelectedChartMutationResult(
            false,
            failure ?? throw new ArgumentNullException(nameof(failure)), mutationReceipt);
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
        if (!TryEnterOperation(
            request.Section == MainViewOperationSection.InstallPending,
            out IDisposable operationGate))
        {
            return SelectedChartMutationResult.Failed(
                new InvalidOperationException("A chart-file operation is already active."));
        }
        bool operationGateTransferred = false;
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
                candidatePaths = store.GetLibraryWholeFolderDeleteConfirmationPaths(library, libraryCharts) ?? [];
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

            LibraryChartRemovalOutcome removalOutcome = null;
            IReadOnlyList<string> approvedFolderPaths = approvedWholeFolderDeletePaths.ToArray();
            SelectedChartMutationResult result = await Task.Run(() => ExecuteMutation(
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

                removalOutcome = store.RemoveLibraryCharts(
                    library,
                    libraryCharts,
                    approvedFolderPaths);
            },
            acquiredOperationGate: operationGate)).ConfigureAwait(false);
            operationGateTransferred = true;
            if (removalOutcome != null)
            {
                result = SelectedChartMutationResult.FromRemoval(removalOutcome, result.Failure);
                await LibraryChartRemovalReport.ShowAsync(dialogs, removalOutcome, result.Failure).ConfigureAwait(false);
            }
            return result;
        }
        catch (Exception ex)
        {
            return SelectedChartMutationResult.Failed(ex);
        }
        finally
        {
            if (!operationGateTransferred)
            {
                operationGate.Dispose();
            }
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
            if (!TryEnterOperation(request.IsPendingSelected, out IDisposable operationGate))
            {
                return Task.FromResult(SelectedChartMutationResult.Failed(
                    new InvalidOperationException("A chart-file operation is already active.")));
            }
            bool operationGateTransferred = false;
            try
            {
                if (!ConfirmMessage(
                    BeMusicSeeker.Properties.Resources.Msg_rename_to_invalid,
                    "Invalid chart extension rename confirmation"))
                {
                    operationGate.Dispose();
                    return Task.FromResult(SelectedChartMutationResult.Completed);
                }

                IReadOnlyList<ChartFile> bCharts = [.. charts.Where(chart =>
                (Path.GetExtension(chart.Path) ?? string.Empty).StartsWith(".b", StringComparison.OrdinalIgnoreCase))];
                IReadOnlyList<ChartFile> pCharts = [.. charts.Where(chart =>
                (Path.GetExtension(chart.Path) ?? string.Empty).StartsWith(".p", StringComparison.OrdinalIgnoreCase))];
                IReadOnlyList<ChartFile> allCharts = [.. bCharts.Concat(pCharts)];
                var libraryRenameBatches = new List<LibraryFileExtensionRenameBatch>(2);
                if (bCharts.Count > 0)
                {
                    libraryRenameBatches.Add(new LibraryFileExtensionRenameBatch(bCharts, ".bmx"));
                }
                if (pCharts.Count > 0)
                {
                    libraryRenameBatches.Add(new LibraryFileExtensionRenameBatch(pCharts, ".pmx"));
                }
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
                Task<SelectedChartMutationResult> task;
                if (!request.IsPendingSelected && store is ISelectedChartMutationTerminalStore terminalStore)
                {
                    task = Task.Run(() => ExecuteMutation(
                        SelectedChartMutationRefreshScope.Library,
                        stopPlayback,
                        mutation: null,
                        mutationWithReceipt: library => terminalStore.RenameLibraryChartsWithReceipt(
                            library,
                            libraryRenameBatches),
                        acquiredOperationGate: operationGate));
                }
                else
                {
                    task = Task.Run(() => ExecuteMutation(
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
                        },
                        acquiredOperationGate: operationGate));
                }
                operationGateTransferred = true;
                return !request.IsPendingSelected && store is ISelectedChartMutationTerminalStore
                    ? ReportInvalidExtensionRenameAsync(task)
                    : task;
            }
            finally
            {
                if (!operationGateTransferred)
                {
                    operationGate.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            return Task.FromResult(SelectedChartMutationResult.Failed(ex));
        }
    }

    private async Task<SelectedChartMutationResult> ReportInvalidExtensionRenameAsync(
        Task<SelectedChartMutationResult> mutationTask)
    {
        SelectedChartMutationResult result = await mutationTask.ConfigureAwait(false);
        await FileDbMutationReport.ShowAsync(
            dialogs,
            BeMusicSeeker.Properties.Resources.Rename_invalid_ext,
            result.MutationReceipt,
            result.Failure).ConfigureAwait(false);
        return result;
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
                    out ChartLibraryMoveRequest moveRequest))
            {
                return SelectedChartMutationResult.Completed;
            }

            if (!TryEnterOperation(pending: false, out IDisposable operationGate))
            {
                return SelectedChartMutationResult.Failed(
                    new InvalidOperationException("A chart-file operation is already active."));
            }
            bool operationGateTransferred = false;
            try
            {
                if (!await ConfirmMessageAsync(
                    BeMusicSeeker.Properties.Resources.Msg_move_to_other_root,
                    "Selected chart library move confirmation"))
                {
                    return SelectedChartMutationResult.Completed;
                }

                Task<SelectedChartMutationResult> task;
                if (store is ISelectedChartMutationTerminalStore terminalStore)
                {
                    task = Task.Run(() => ExecuteMutation(
                        SelectedChartMutationRefreshScope.Library,
                        () => playback.StopPlaybackForLibraryCharts(moveRequest.Charts),
                        mutation: null,
                        mutationWithReceipt: library => terminalStore.MoveLibraryChartsWithReceipt(library, moveRequest),
                        publishMutationApplied: true,
                        acquiredOperationGate: operationGate));
                }
                else
                {
                    task = Task.Run(() => ExecuteMutation(
                        SelectedChartMutationRefreshScope.Library,
                        () => playback.StopPlaybackForLibraryCharts(moveRequest.Charts),
                        library =>
                        {
                            store.MoveLibraryCharts(library, moveRequest);
                        },
                        publishMutationApplied: true,
                        acquiredOperationGate: operationGate));
                }
                operationGateTransferred = true;
                SelectedChartMutationResult result = await task.ConfigureAwait(false);
                if (store is ISelectedChartMutationTerminalStore)
                    await FileDbMutationReport.ShowAsync(dialogs, BeMusicSeeker.Properties.Resources.FileDbMutationReport_Move,
                        result.MutationReceipt, result.Failure).ConfigureAwait(false);
                return result;
            }
            finally
            {
                if (!operationGateTransferred)
                {
                    operationGate.Dispose();
                }
            }
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

        if (!TryEnterOperation(pending: false, out IDisposable operationGate))
        {
            return SelectedChartMutationResult.Failed(
                new InvalidOperationException("A chart-file operation is already active."));
        }
        SelectedChartMutationResult result;
        bool publishMutationApplied = false;
        try
        {
            store.SetBMSFilesEncoding(RequireLibrary(), request.BmsFiles, request.Encoding);
            publishMutationApplied = true;
            result = SelectedChartMutationResult.Completed;
        }
        catch (Exception ex)
        {
            result = SelectedChartMutationResult.Failed(ex);
        }
        finally
        {
            operationGate.Dispose();
        }
        if (publishMutationApplied)
        {
            try
            {
                PublishMutationApplied(encodingChanged: true);
            }
            catch (Exception ex)
            {
                return SelectedChartMutationResult.Failed(ex);
            }
        }
        return result;
    }

    private SelectedChartMutationResult ExecuteMutation(
        SelectedChartMutationRefreshScope refreshScope,
        Action stopPlayback,
        Action<BMSLibrary> mutation,
        Func<BMSLibrary, LibraryMutationSessionReceipt> mutationWithReceipt = null,
        bool publishMutationApplied = false,
        IDisposable acquiredOperationGate = null)
    {
        IDisposable operationGate = acquiredOperationGate;
        if (operationGate == null
            && !TryEnterOperation(
                refreshScope == SelectedChartMutationRefreshScope.Pending,
                out operationGate))
        {
            return SelectedChartMutationResult.Failed(
                new InvalidOperationException("A chart-file operation is already active."));
        }
        BMSLibrary library;
        try
        {
            library = RequireLibrary();
        }
        catch (Exception ex)
        {
            operationGate.Dispose();
            return SelectedChartMutationResult.Failed(ex);
        }

        BMSLibrary.OperationDialogScope dialogScope = null;
        IDisposable activityLease = null;
        bool suppressionStarted = false;
        LibraryMutationSessionReceipt mutationReceipt = null;
        bool publishMutationAppliedAfterRelease = false;
        var failures = new List<ExceptionDispatchInfo>();
        try
        {
            dialogScope = library.BeginOperationDialogScope();
            activityLease = chartMutationActivity.Enter();
            stopPlayback?.Invoke();
            suppressionStarted = true;
            PublishRefreshSuppressionChanged(isSuppressed: true, scope: refreshScope);
            mutationReceipt = mutationWithReceipt?.Invoke(library);
            if (mutationWithReceipt == null)
            {
                mutation(library);
            }
            if (publishMutationApplied
                && (mutationReceipt == null
                    || (mutationReceipt.DurableCommit
                        && !mutationReceipt.HasDurableFinalizationFailure)))
            {
                publishMutationAppliedAfterRelease = true;
            }
        }
        catch (Exception ex)
        {
            failures.Add(ExceptionDispatchInfo.Capture(ex));
        }
        finally
        {
            if (operationGate != null)
            {
                CaptureCleanupFailure(operationGate.Dispose, failures);
                operationGate = null;
            }
            if (failures.Count == 0 && publishMutationAppliedAfterRelease)
            {
                CaptureNotification(() => PublishMutationApplied(libraryPathChanged: true));
            }
            if (suppressionStarted)
            {
                CaptureNotification(() => PublishRefreshSuppressionChanged(isSuppressed: false, scope: null));
            }
            if (activityLease != null)
            {
                CaptureNotification(activityLease.Dispose);
            }
            if (dialogScope != null)
            {
                CaptureCleanupFailure(dialogScope.Dispose, failures);
                CaptureNotification(dialogScope.Flush);
            }
        }
        return failures.Count switch
        {
            0 => mutationReceipt == null
                ? SelectedChartMutationResult.Completed
                : SelectedChartMutationResult.FromReceipt(mutationReceipt),
            1 => SelectedChartMutationResult.Failed(CombineReceiptFailure(failures[0].SourceException), mutationReceipt),
            _ => SelectedChartMutationResult.Failed(
                CombineReceiptFailure(new AggregateException(failures.Select(failure => failure.SourceException))), mutationReceipt),
        };

        void CaptureNotification(Action notification)
        {
            if (mutationWithReceipt != null)
                FileDbMutationReport.NotifyBestEffort(notification);
            else
                CaptureCleanupFailure(notification, failures);
        }

        Exception CombineReceiptFailure(Exception failure)
        {
            Exception primary = mutationReceipt == null ? null : SelectedChartMutationResult.FromReceipt(mutationReceipt).Failure;
            return primary == null ? failure : new AggregateException(primary, failure);
        }
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

    private bool TryEnterOperation(bool pending, out IDisposable operationGate)
    {
        BMSLibrary library = libraryProvider();
        if (pending && library?.IsPendingOperationAdmissionReady == true)
        {
            return library.TryEnterPendingOperation(out operationGate);
        }
        return chartFileOperations.TryEnter(out operationGate);
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

    /// <summary>Preserves the model deletion outcome for terminal reporting.</summary>
    public LibraryChartRemovalOutcome RemoveLibraryCharts(
        BMSLibrary library,
        IReadOnlyList<LibraryChartRef> charts,
        IReadOnlyList<string> approvedWholeFolderDeletePaths)
    {
        return library.RemoveLibraryCharts(
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

    /// <summary>The selected library rename terminal owns one receipt for all extension families.</summary>
    public LibraryMutationSessionReceipt RenameLibraryChartsWithReceipt(
        BMSLibrary library,
        IReadOnlyList<LibraryFileExtensionRenameBatch> batches)
    {
        return library.RenameBMSFilesExtensionsWithReceipt(batches, unregister: true);
    }

    public void MoveLibraryCharts(BMSLibrary library, ChartLibraryMoveRequest request)
    {
        library.MoveLibraryRootFolder(request.Charts, request.NewParentDirectory, false);
    }

    /// <summary>The canonical selected move terminal owns aggregate receipt notification.</summary>
    public LibraryMutationSessionReceipt MoveLibraryChartsWithReceipt(
        BMSLibrary library,
        ChartLibraryMoveRequest request)
    {
        return library.MoveLibraryRootFolderWithReceipt(
            request.Charts,
            request.NewParentDirectory,
            false,
            reportAtTerminal: true);
    }

    public void SetBMSFilesEncoding(
        BMSLibrary library,
        IReadOnlyList<BMSFile> bmsFiles,
        string encoding)
    {
        library.SetBMSFilesEncoding(bmsFiles, encoding);
    }
}
