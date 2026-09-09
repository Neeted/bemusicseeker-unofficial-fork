using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Views.Dialogs;
using MessageBoxButton = BeMusicSeeker.Models.UiDialogButton;
using MessageBoxImage = BeMusicSeeker.Models.UiDialogIcon;
using MessageBoxResult = BeMusicSeeker.Models.UiDialogDefaultResult;

namespace BeMusicSeeker.ViewModels;

internal abstract class DuplicateMaintenanceWorkflowChangedEventArgs : EventArgs
{
}

internal sealed class DuplicateMaintenanceRefreshSuppressionChangedEventArgs : DuplicateMaintenanceWorkflowChangedEventArgs
{
    internal DuplicateMaintenanceRefreshSuppressionChangedEventArgs(bool isSuppressed)
    {
        IsSuppressed = isSuppressed;
    }

    internal bool IsSuppressed { get; }
}

internal sealed class DuplicateMaintenanceRefreshPriorityWindowChangedEventArgs : DuplicateMaintenanceWorkflowChangedEventArgs
{
    internal DuplicateMaintenanceRefreshPriorityWindowChangedEventArgs(bool isActive, string reason)
    {
        IsActive = isActive;
        Reason = reason;
    }

    internal bool IsActive { get; }

    internal string Reason { get; }
}

internal enum DuplicateFolderKeyboardActionKind
{
    None,
    Merge,
    Cleanup,
    OpenContextMenu
}

internal sealed class DuplicateFolderKeyboardAction
{
    internal DuplicateFolderKeyboardAction(
        DuplicateFolderKeyboardActionKind kind,
        string destinationPath = null)
    {
        Kind = kind;
        DestinationPath = destinationPath;
    }

    internal DuplicateFolderKeyboardActionKind Kind { get; }

    internal string DestinationPath { get; }
}

internal interface IDuplicateMaintenancePlaybackPort
{

    void StopPlaybackForMerge();

    void StopPlaybackForCharts(IReadOnlyList<ChartFile> charts);
}

internal interface IDuplicateMaintenanceStore
{
    void MergeFolder(BMSLibrary library, string sourceDirectory, string destinationDirectory, long operationId);

    /// <summary>Returns observed library deletion facts to the operation terminal.</summary>
    LibraryChartRemovalOutcome RemoveCharts(BMSLibrary library, IReadOnlyList<ChartFile> charts);
}

internal interface IDuplicateMaintenanceTerminalStore
{
    DuplicateMergeMaintenanceReceipt MergeFolderWithReceipt(
        BMSLibrary library,
        string sourceDirectory,
        string destinationDirectory,
        long operationId);
}

internal sealed class DuplicateMaintenanceMutationResult
{
    private DuplicateMaintenanceMutationResult(
        bool succeeded,
        string selectionHeader,
        int removedChartCount,
        Exception failure,
        DuplicateMergeMaintenanceReceipt mutationReceipt = null,
        LibraryChartRemovalOutcome removalOutcome = null)
    {
        RemovalOutcome = removalOutcome;
        Succeeded = succeeded;
        SelectionHeader = selectionHeader;
        RemovedChartCount = removedChartCount;
        Failure = failure;
        MutationReceipt = mutationReceipt;
    }

    /// <summary>Confirmed deletion and independent catalog failure facts.</summary>
    internal LibraryChartRemovalOutcome RemovalOutcome { get; }

    /// <summary>Uses confirmed filesystem targets, never the cleanup plan size.</summary>
    internal static DuplicateMaintenanceMutationResult FromRemoval(string header,
        LibraryChartRemovalOutcome outcome, Exception failure)
    {
        Exception primary = outcome?.CatalogFailure;
        Exception combined = primary == null ? failure : failure == null ? primary : new AggregateException(primary, failure);
        return new DuplicateMaintenanceMutationResult(combined == null, header,
            outcome?.ConfirmedChartCount ?? 0, combined, removalOutcome: outcome);
    }

    internal bool Succeeded { get; }

    internal string SelectionHeader { get; }

    internal int RemovedChartCount { get; }

    internal Exception Failure { get; }

    internal DuplicateMergeMaintenanceReceipt MutationReceipt { get; }

    /// <summary>マージ変更前に見つかった immutable な宛先型衝突を取得します。</summary>
    internal IReadOnlyList<FileDbMutationDestinationTypeConflict> DestinationTypeConflicts =>
        MutationReceipt?.DestinationTypeConflicts ?? [];

    /// <summary>Includes the catalog commit observed by library deletion.</summary>
    internal bool HasDurableCommit => RemovalOutcome?.CatalogDurable == true || MutationReceipt?.HasDurableCommit == true;

    /// <summary>
    /// Gets whether merge finalization failed after durable file/catalog state.
    /// </summary>
    internal bool HasDurableFinalizationFailure => RemovalOutcome?.RequiredFinalizationFailed == true || MutationReceipt?.HasDurableFinalizationFailure == true;

    internal bool ManualRecoveryRequired => MutationReceipt?.ManualRecoveryRequired == true;

    internal bool CompletedWithCleanupFailure => MutationReceipt?.CompletedWithCleanupFailure == true;

    internal IReadOnlyList<string> RecoveryPaths => MutationReceipt?.RecoveryPaths ?? [];

    internal static DuplicateMaintenanceMutationResult Completed(
        string selectionHeader,
        int removedChartCount = 0,
        DuplicateMergeMaintenanceReceipt mutationReceipt = null)
    {
        return new DuplicateMaintenanceMutationResult(true, selectionHeader, removedChartCount, null, mutationReceipt);
    }

    internal static DuplicateMaintenanceMutationResult Rejected(string selectionHeader)
    {
        return new DuplicateMaintenanceMutationResult(false, selectionHeader, 0, null);
    }

    internal static DuplicateMaintenanceMutationResult NoWork(string selectionHeader)
    {
        return new DuplicateMaintenanceMutationResult(false, selectionHeader, 0, null);
    }

    internal static DuplicateMaintenanceMutationResult Failed(
        string selectionHeader,
        Exception failure,
        int removedChartCount = 0,
        DuplicateMergeMaintenanceReceipt mutationReceipt = null)
    {
        return new DuplicateMaintenanceMutationResult(
            false,
            selectionHeader,
            removedChartCount,
            failure ?? throw new ArgumentNullException(nameof(failure)),
            mutationReceipt);
    }

    /// <summary>Keeps unsuccessful and durable partial merge facts available to the terminal.</summary>
    internal static DuplicateMaintenanceMutationResult FromMergeReceipt(
        string selectionHeader,
        DuplicateMergeMaintenanceReceipt mutationReceipt)
    {
        if (mutationReceipt?.MutationReceipt?.DurableCommit == false
            || mutationReceipt?.ManualRecoveryRequired == true
            || mutationReceipt?.HasDurableFinalizationFailure == true)
        {
            return new DuplicateMaintenanceMutationResult(
                false,
                selectionHeader,
                0,
                mutationReceipt?.MutationReceipt?.Failure,
                mutationReceipt);
        }
        if (mutationReceipt?.MergeApplied != true)
        {
            return new DuplicateMaintenanceMutationResult(false, selectionHeader, 0, null, mutationReceipt);
        }
        return Completed(selectionHeader, mutationReceipt: mutationReceipt);
    }
}

internal sealed class DuplicateMaintenanceWorkflowOwner
{
    private readonly Func<BMSLibrary> libraryProvider;
    private readonly ChartFileOperationSynchronizer chartFileOperations;
    private readonly ChartMutationActivityOwner chartMutationActivity;
    private readonly IDuplicateMaintenancePlaybackPort playback;
    private readonly IUiDialogService dialogs;
    private readonly Func<bool> showConfirmationProvider;

    private readonly Func<string, bool> duplicateFolderDirectoryExists;

    private readonly Func<string, ExplorerOpenResult> duplicateFolderExplorerOpen;

    private readonly Func<DuplicateGroup, string> duplicateGroupNextHeaderProvider;

    private readonly IDuplicateMaintenanceStore store;

    internal DuplicateMaintenanceWorkflowOwner(
        Func<BMSLibrary> libraryProvider,
        ChartFileOperationSynchronizer chartFileOperations,
        ChartMutationActivityOwner chartMutationActivity,
        IDuplicateMaintenancePlaybackPort playback,
        IUiDialogService dialogs,
        Func<bool> showConfirmationProvider,
        Func<string, bool> duplicateFolderDirectoryExists,
        Func<string, ExplorerOpenResult> duplicateFolderExplorerOpen,
        Func<DuplicateGroup, string> duplicateGroupNextHeaderProvider,
        IDuplicateMaintenanceStore store = null)
    {
        this.libraryProvider = libraryProvider ?? throw new ArgumentNullException(nameof(libraryProvider));
        this.chartFileOperations = chartFileOperations ?? throw new ArgumentNullException(nameof(chartFileOperations));
        this.chartMutationActivity = chartMutationActivity ?? throw new ArgumentNullException(nameof(chartMutationActivity));
        this.playback = playback ?? throw new ArgumentNullException(nameof(playback));
        this.dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        this.showConfirmationProvider = showConfirmationProvider ?? throw new ArgumentNullException(nameof(showConfirmationProvider));
        this.duplicateFolderDirectoryExists = duplicateFolderDirectoryExists
            ?? throw new ArgumentNullException(nameof(duplicateFolderDirectoryExists));
        this.duplicateFolderExplorerOpen = duplicateFolderExplorerOpen
            ?? throw new ArgumentNullException(nameof(duplicateFolderExplorerOpen));
        this.duplicateGroupNextHeaderProvider = duplicateGroupNextHeaderProvider
            ?? throw new ArgumentNullException(nameof(duplicateGroupNextHeaderProvider));
        this.store = store ?? new BmsLibraryDuplicateMaintenanceStore();
    }

    internal event EventHandler<DuplicateMaintenanceWorkflowChangedEventArgs> WorkflowChanged;

    private sealed class FolderMergeRequest
    {
        internal FolderMergeRequest(
            string sourceDirectory,
            string destinationDirectory,
            IEnumerable<string> folders,
            string selectionHeader)
        {
            SourceDirectory = sourceDirectory;
            DestinationDirectory = destinationDirectory;
            Folders = (folders ?? []).Where(folder => !string.IsNullOrWhiteSpace(folder)).ToArray();
            SelectionHeader = selectionHeader;
        }

        internal string SourceDirectory { get; }

        internal string DestinationDirectory { get; }

        internal IReadOnlyList<string> Folders { get; }

        internal string SelectionHeader { get; }
    }

    private sealed class HashCleanupRequest
    {
        internal HashCleanupRequest(
            string folderPath,
            IEnumerable<ChartFile> charts,
            string selectionHeader)
        {
            FolderPath = folderPath;
            Charts = (charts ?? []).Where(chart => chart != null).ToArray();
            SelectionHeader = selectionHeader;
        }

        internal string FolderPath { get; }

        internal IReadOnlyList<ChartFile> Charts { get; }

        internal string SelectionHeader { get; }
    }

    private sealed class HashCleanupPlan
    {
        internal HashCleanupPlan(
            string folderPath,
            IEnumerable<ChartFile> chartsToRemove,
            string selectionHeader)
        {
            FolderPath = folderPath;
            ChartsToRemove = (chartsToRemove ?? []).Where(chart => chart != null).ToArray();
            SelectionHeader = selectionHeader;
        }

        internal string FolderPath { get; }

        internal IReadOnlyList<ChartFile> ChartsToRemove { get; }

        internal string SelectionHeader { get; }
    }

    private sealed class ConfirmationDecision
    {
        private ConfirmationDecision(bool accepted, Exception failure)
        {
            Accepted = accepted;
            Failure = failure;
        }

        internal bool Accepted { get; }

        internal Exception Failure { get; }

        internal static ConfirmationDecision AcceptedResult { get; } = new(true, null);

        internal static ConfirmationDecision Rejected { get; } = new(false, null);

        internal static ConfirmationDecision Failed(Exception failure)
        {
            return new ConfirmationDecision(false, failure ?? throw new ArgumentNullException(nameof(failure)));
        }
    }

    internal IReadOnlyList<string> CaptureDuplicateFolderMergeDestinations(
        DuplicateGroup duplicateGroup,
        string sourcePath)
    {
        if (duplicateGroup == null || string.IsNullOrWhiteSpace(sourcePath))
        {
            return [];
        }

        return [.. (duplicateGroup.Folders ?? [])
            .Except([sourcePath], StringComparer.OrdinalIgnoreCase)];
    }

    internal DuplicateFolderKeyboardAction CaptureDuplicateFolderKeyboardAction(
        DuplicateGroup duplicateGroup,
        string sourcePath)
    {
        if (duplicateGroup == null || string.IsNullOrWhiteSpace(sourcePath))
        {
            return new DuplicateFolderKeyboardAction(DuplicateFolderKeyboardActionKind.None);
        }

        int folderCount = duplicateGroup.Folders?.Count ?? 0;
        if (folderCount == 1)
        {
            return new DuplicateFolderKeyboardAction(DuplicateFolderKeyboardActionKind.Cleanup);
        }
        if (folderCount == 2)
        {
            string destinationPath = CaptureDuplicateFolderMergeDestinations(duplicateGroup, sourcePath)
                .FirstOrDefault();
            return string.IsNullOrWhiteSpace(destinationPath)
                ? new DuplicateFolderKeyboardAction(DuplicateFolderKeyboardActionKind.None)
                : new DuplicateFolderKeyboardAction(
                    DuplicateFolderKeyboardActionKind.Merge,
                    destinationPath);
        }
        if (folderCount >= 3)
        {
            return new DuplicateFolderKeyboardAction(DuplicateFolderKeyboardActionKind.OpenContextMenu);
        }
        return new DuplicateFolderKeyboardAction(DuplicateFolderKeyboardActionKind.None);
    }

    internal void OpenDuplicateFolderInExplorer(string path)
    {
        if (!duplicateFolderDirectoryExists(path))
        {
            return;
        }

        duplicateFolderExplorerOpen(path);
    }

    private FolderMergeRequest CreateFolderMergeRequest(
        string sourceDirectory,
        string destinationDirectory,
        DuplicateGroup duplicateGroup)
    {
        if (duplicateGroup == null)
        {
            throw new ArgumentNullException(nameof(duplicateGroup));
        }
        int folderCount = duplicateGroup.Folders?.Count ?? 0;
        string selectionHeader = folderCount >= 3
            ? duplicateGroup.Header
            : duplicateGroupNextHeaderProvider(duplicateGroup);
        return new FolderMergeRequest(
            sourceDirectory,
            destinationDirectory,
            duplicateGroup.Folders,
            selectionHeader);
    }

    private HashCleanupRequest CreateHashCleanupRequest(
        DuplicateGroup duplicateGroup,
        string folderPath)
    {
        if (duplicateGroup == null)
        {
            throw new ArgumentNullException(nameof(duplicateGroup));
        }
        return new HashCleanupRequest(
            folderPath,
            duplicateGroup.ChartFiles,
            duplicateGroupNextHeaderProvider(duplicateGroup));
    }

    internal Task<DuplicateMaintenanceMutationResult> RunFolderMergeAsync(
        string sourceDirectory,
        string destinationDirectory,
        DuplicateGroup duplicateGroup)
    {
        FolderMergeRequest request = CreateFolderMergeRequest(
            sourceDirectory,
            destinationDirectory,
            duplicateGroup);
        ValidateFolderMergeRequest(request);
        return RunFolderMergeCoreAsync(request);
    }

    internal Task<DuplicateMaintenanceMutationResult> RunHashCleanupAsync(
        DuplicateGroup duplicateGroup,
        string folderPath)
    {
        HashCleanupRequest request = CreateHashCleanupRequest(duplicateGroup, folderPath);
        ValidateHashCleanupRequest(request);
        List<ChartFile> chartsInFolder = [.. request.Charts
            .Where(chart => IsChartInFolder(chart, request.FolderPath))];
        List<ChartFile> deletionList;
        try
        {
            deletionList = BuildHashCleanupDeletionList(chartsInFolder);
        }
        catch (Exception exception)
        {
            return Task.FromResult(
                DuplicateMaintenanceMutationResult.Failed(request.SelectionHeader, exception));
        }
        if (deletionList.Count == 0)
        {
            return Task.FromResult(
                DuplicateMaintenanceMutationResult.NoWork(request.SelectionHeader));
        }
        return RunHashCleanupCoreAsync(
            new HashCleanupPlan(request.FolderPath, deletionList, request.SelectionHeader));
    }

    private async Task<DuplicateMaintenanceMutationResult> RunFolderMergeCoreAsync(
        FolderMergeRequest request)
    {
        if (!chartFileOperations.TryEnter(out IDisposable operationGate))
        {
            return DuplicateMaintenanceMutationResult.Rejected(request.SelectionHeader);
        }
        bool operationGateTransferred = false;
        string message = BeMusicSeeker.Properties.Resources.Msg_merge_bms_folder
            + Environment.NewLine
            + Environment.NewLine
            + BeMusicSeeker.Properties.Resources.Msg_merge_bms_target
            + ": "
            + request.SourceDirectory
            + Environment.NewLine
            + BeMusicSeeker.Properties.Resources.Msg_merge_bms_destination
            + ": "
            + request.DestinationDirectory;
        try
        {
            ConfirmationDecision confirmation = await ConfirmIfNeededAsync(
                message,
                "Duplicate folder merge confirmation");
            if (confirmation.Failure != null)
            {
                return DuplicateMaintenanceMutationResult.Failed(
                    request.SelectionHeader,
                    confirmation.Failure);
            }
            if (!confirmation.Accepted)
            {
                return DuplicateMaintenanceMutationResult.Rejected(request.SelectionHeader);
            }

            if (store is IDuplicateMaintenanceTerminalStore terminalStore)
            {
                Task<DuplicateMaintenanceMutationResult> mutationTask = Task.Run(() => ExecuteMutation(
                    request.SelectionHeader,
                    mutation: null,
                    stopPlayback: playback.StopPlaybackForMerge,
                    refreshPriorityReason: "merge_folder",
                    mutationWithReceipt: library => terminalStore.MergeFolderWithReceipt(
                        library,
                        request.SourceDirectory,
                        request.DestinationDirectory,
                        Stopwatch.GetTimestamp()),
                    acquiredOperationGate: operationGate));
                operationGateTransferred = true;
                DuplicateMaintenanceMutationResult result = await mutationTask;
                await FileDbMutationReport.ShowAsync(dialogs, BeMusicSeeker.Properties.Resources.FileDbMutationReport_Merge,
                    result.MutationReceipt?.MutationReceipt is { } receipt ? new FileDbMutationBatchReceipt([receipt]) : null,
                    result.Failure,
                    mergeOperation: true);
                return result;
            }
            Task<DuplicateMaintenanceMutationResult> regularMutationTask = Task.Run(() => ExecuteMutation(
                request.SelectionHeader,
                library => store.MergeFolder(
                    library,
                    request.SourceDirectory,
                request.DestinationDirectory,
                Stopwatch.GetTimestamp()),
                playback.StopPlaybackForMerge,
                "merge_folder",
                acquiredOperationGate: operationGate));
            operationGateTransferred = true;
            return await regularMutationTask;
        }
        finally
        {
            if (!operationGateTransferred)
            {
                operationGate.Dispose();
            }
        }
    }

    private async Task<DuplicateMaintenanceMutationResult> RunHashCleanupCoreAsync(
        HashCleanupPlan plan)
    {
        if (!chartFileOperations.TryEnter(out IDisposable operationGate))
        {
            return DuplicateMaintenanceMutationResult.Rejected(plan.SelectionHeader);
        }
        bool operationGateTransferred = false;
        try
        {
            ConfirmationDecision confirmation = await ConfirmIfNeededAsync(
                string.Format(BeMusicSeeker.Properties.Resources.Msg_cleanup_duplicate_hash, plan.ChartsToRemove.Count),
                "Duplicate hash cleanup confirmation");
            if (confirmation.Failure != null)
            {
                return DuplicateMaintenanceMutationResult.Failed(
                    plan.SelectionHeader,
                    confirmation.Failure,
                    0);
            }
            if (!confirmation.Accepted)
            {
                return DuplicateMaintenanceMutationResult.Rejected(plan.SelectionHeader);
            }

            LibraryChartRemovalOutcome removalOutcome = null;
            Task<DuplicateMaintenanceMutationResult> mutationTask = Task.Run(() => ExecuteMutation(
                plan.SelectionHeader,
                library => removalOutcome = store.RemoveCharts(library, plan.ChartsToRemove),
                () => playback.StopPlaybackForCharts(plan.ChartsToRemove),
                refreshPriorityReason: null,
                removedChartCount: 0,
                acquiredOperationGate: operationGate));
            operationGateTransferred = true;
            DuplicateMaintenanceMutationResult result = await mutationTask;
            result = DuplicateMaintenanceMutationResult.FromRemoval(plan.SelectionHeader, removalOutcome, result.Failure);
            await LibraryChartRemovalReport.ShowAsync(dialogs, removalOutcome, result.Failure);
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

    private async Task<ConfirmationDecision> ConfirmIfNeededAsync(string message, string routeName)
    {
        try
        {
            if (!showConfirmationProvider())
            {
                return ConfirmationDecision.AcceptedResult;
            }
            UiDialogResult result = await dialogs.ConfirmAsync(new UiConfirmationRequest(
                message,
                BeMusicSeeker.Properties.Resources.Confirm,
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question,
                MessageBoxResult.Cancel));
            if (result == null)
            {
                return ConfirmationDecision.Failed(
                    new InvalidOperationException(routeName + " returned no result."));
            }
            return result.Status switch
            {
                UiDialogStatus.Accepted => ConfirmationDecision.AcceptedResult,
                UiDialogStatus.Rejected or UiDialogStatus.CancelledByUser => ConfirmationDecision.Rejected,
                UiDialogStatus.ClosedByUser => result.IsPositive
                    ? ConfirmationDecision.AcceptedResult
                    : ConfirmationDecision.Rejected,
                _ => ConfirmationDecision.Failed(
                    new InvalidOperationException(
                        routeName + " could not be displayed (" + result.Status + ").",
                        result.Exception))
            };
        }
        catch (Exception exception)
        {
            return ConfirmationDecision.Failed(exception);
        }
    }

    private DuplicateMaintenanceMutationResult ExecuteMutation(
        string selectionHeader,
        Action<BMSLibrary> mutation,
        Action stopPlayback,
        string refreshPriorityReason,
        int removedChartCount = 0,
        Func<BMSLibrary, DuplicateMergeMaintenanceReceipt> mutationWithReceipt = null,
        IDisposable acquiredOperationGate = null)
    {
        var failures = new List<ExceptionDispatchInfo>();
        BMSLibrary library = null;
        DuplicateMergeMaintenanceReceipt mutationReceipt = null;
        BMSLibrary.OperationDialogScope dialogScope = null;
        IDisposable operationGate = null;
        IDisposable activityLease = null;
        bool suppressionStarted = false;
        bool refreshPriorityStarted = false;
        try
        {
            operationGate = acquiredOperationGate;
            if (operationGate == null && !chartFileOperations.TryEnter(out operationGate))
            {
                return DuplicateMaintenanceMutationResult.Rejected(selectionHeader);
            }
            library = libraryProvider();
            if (library == null)
            {
                return DuplicateMaintenanceMutationResult.Failed(
                    selectionHeader,
                    new InvalidOperationException("Duplicate maintenance library is not available."));
            }
            dialogScope = library.BeginOperationDialogScope();
            activityLease = chartMutationActivity.Enter();
            stopPlayback();
            PublishRefreshSuppressionChanged(isSuppressed: true);
            suppressionStarted = true;
            if (!string.IsNullOrWhiteSpace(refreshPriorityReason))
            {
                PublishRefreshPriorityWindowChanged(isActive: true, reason: refreshPriorityReason);
                refreshPriorityStarted = true;
            }
            mutationReceipt = mutationWithReceipt?.Invoke(library);
            if (mutationWithReceipt == null)
            {
                mutation(library);
            }
        }
        catch (Exception exception)
        {
            failures.Add(ExceptionDispatchInfo.Capture(exception));
        }
        finally
        {
            if (suppressionStarted)
            {
                CaptureNotification(() => PublishRefreshSuppressionChanged(isSuppressed: false));
            }
            if (refreshPriorityStarted)
            {
                CaptureNotification(() => PublishRefreshPriorityWindowChanged(isActive: false, reason: refreshPriorityReason));
            }
            if (operationGate != null)
            {
                CaptureCleanupFailure(operationGate.Dispose, failures);
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
        if (failures.Count == 0)
        {
            return mutationReceipt == null
                ? DuplicateMaintenanceMutationResult.Completed(selectionHeader, removedChartCount)
                : DuplicateMaintenanceMutationResult.FromMergeReceipt(selectionHeader, mutationReceipt);
        }
        Exception additionalFailure = failures.Count == 1
            ? failures[0].SourceException
            : new AggregateException(failures.Select(failure => failure.SourceException));
        Exception primary = mutationReceipt?.MutationReceipt?.Failure;
        return DuplicateMaintenanceMutationResult.Failed(
            selectionHeader,
            primary == null ? additionalFailure : new AggregateException(primary, additionalFailure),
            removedChartCount,
            mutationReceipt);

        void CaptureNotification(Action notification)
        {
            if (mutationWithReceipt != null)
                FileDbMutationReport.NotifyBestEffort(notification);
            else
                CaptureCleanupFailure(notification, failures);
        }
    }

    private void PublishRefreshSuppressionChanged(bool isSuppressed)
    {
        WorkflowChanged?.Invoke(
            this,
            new DuplicateMaintenanceRefreshSuppressionChangedEventArgs(isSuppressed));
    }

    private void PublishRefreshPriorityWindowChanged(bool isActive, string reason)
    {
        WorkflowChanged?.Invoke(
            this,
            new DuplicateMaintenanceRefreshPriorityWindowChangedEventArgs(isActive, reason));
    }

    private static List<ChartFile> BuildHashCleanupDeletionList(IEnumerable<ChartFile> charts)
    {
        var deletionList = new List<ChartFile>();
        foreach (var hashGroup in (charts ?? [])
            .Select(chart => new { Chart = chart, LookupHash = ChartLookupKey.GetPrimaryHash(chart) })
            .Where(item => !string.IsNullOrWhiteSpace(item.LookupHash))
            .GroupBy(item => item.LookupHash, StringComparer.OrdinalIgnoreCase))
        {
            List<ChartFile> grouped = [.. hashGroup.Select(item => item.Chart)];
            if (grouped.Count <= 1)
            {
                continue;
            }
            ChartFile keeper = grouped
                .OrderBy(chart =>
                {
                    try
                    {
                        return LongPathFileSystem.GetLastWriteTime(chart.Path, isDirectory: false);
                    }
                    catch
                    {
                        return DateTime.MaxValue;
                    }
                })
                .ThenBy(chart => Path.GetFileName(chart.Path).Length)
                .First();
            deletionList.AddRange(grouped.Where(chart => !ReferenceEquals(chart, keeper)));
        }
        return deletionList;
    }

    private static bool IsChartInFolder(ChartFile chart, string folderPath)
    {
        if (chart == null || string.IsNullOrWhiteSpace(chart.Path) || string.IsNullOrWhiteSpace(folderPath))
        {
            return false;
        }
        string normalizedFolderPath = folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return chart.Path.StartsWith(normalizedFolderPath, StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateFolderMergeRequest(FolderMergeRequest request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }
        if (string.IsNullOrWhiteSpace(request.SourceDirectory)
            || string.IsNullOrWhiteSpace(request.DestinationDirectory)
            || string.Equals(request.SourceDirectory, request.DestinationDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Source and destination directories must be distinct.", nameof(request));
        }
        if (request.Folders.Count < 2
            || !request.Folders.Any(folder => string.Equals(folder, request.SourceDirectory, StringComparison.OrdinalIgnoreCase))
            || !request.Folders.Any(folder => string.Equals(folder, request.DestinationDirectory, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("The merge request must identify both folders in one duplicate group.", nameof(request));
        }
    }

    private static void ValidateHashCleanupRequest(HashCleanupRequest request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }
        if (string.IsNullOrWhiteSpace(request.FolderPath))
        {
            throw new ArgumentException("A folder path is required.", nameof(request));
        }
    }

    private static void CaptureCleanupFailure(Action cleanup, List<ExceptionDispatchInfo> failures)
    {
        try
        {
            cleanup();
        }
        catch (Exception exception)
        {
            failures.Add(ExceptionDispatchInfo.Capture(exception));
        }
    }

}

internal sealed class BmsLibraryDuplicateMaintenanceStore : IDuplicateMaintenanceStore, IDuplicateMaintenanceTerminalStore
{
    public void MergeFolder(
        BMSLibrary library,
        string sourceDirectory,
        string destinationDirectory,
        long operationId)
    {
        library.MergeChartDirectory(sourceDirectory, destinationDirectory, operationId);
    }

    /// <summary>The canonical merge terminal owns the aggregate receipt notification.</summary>
    public DuplicateMergeMaintenanceReceipt MergeFolderWithReceipt(
        BMSLibrary library,
        string sourceDirectory,
        string destinationDirectory,
        long operationId)
    {
        return library.MergeChartDirectory(
            sourceDirectory,
            destinationDirectory,
            operationId,
            reportAtTerminal: true);
    }

    /// <summary>Preserves the model deletion outcome for terminal reporting.</summary>
    public LibraryChartRemovalOutcome RemoveCharts(BMSLibrary library, IReadOnlyList<ChartFile> charts)
    {
        List<LibraryChartRef> chartRefs = [.. (charts ?? [])
            .Select(LibraryChartRef.FromChartFile)
            .Where(chart => chart != null)];
        if (chartRefs.Count > 0)
        {
            return library.RemoveLibraryCharts(chartRefs);
        }
        return null;
    }
}
