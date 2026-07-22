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

namespace BeMusicSeeker.ViewModels;

internal interface IDuplicateMaintenanceActivityPort
{
    void BeginActivity();

    void EndActivity();
}

internal interface IDuplicateMaintenanceRefreshPort
{
    void BeginRefreshSuppression();

    void EndRefreshSuppression();

    void BeginRefreshPriorityWindow(string reason);

    void ScheduleRefreshPriorityWindowRelease(string reason);
}

internal interface IDuplicateMaintenancePlaybackPort
{

    void StopPlaybackForMerge();

    void StopPlaybackForCharts(IReadOnlyList<ChartFile> charts);
}

internal interface IDuplicateMaintenanceStore
{
    void MergeFolder(BMSLibrary library, string sourceDirectory, string destinationDirectory, long operationId);

    void RemoveCharts(BMSLibrary library, IReadOnlyList<ChartFile> charts);
}

internal interface IDuplicateMaintenanceOperation
{
    string SelectionHeader { get; }
}

internal sealed class DuplicateFolderMergeRequest
{
    internal DuplicateFolderMergeRequest(
        string sourceDirectory,
        string destinationDirectory,
        IEnumerable<string> folders,
        string groupHeader,
        string selectionHeader)
    {
        SourceDirectory = sourceDirectory;
        DestinationDirectory = destinationDirectory;
        Folders = (folders ?? []).Where(folder => !string.IsNullOrWhiteSpace(folder)).ToArray();
        GroupHeader = groupHeader;
        SelectionHeader = selectionHeader;
    }

    internal string SourceDirectory { get; }

    internal string DestinationDirectory { get; }

    internal IReadOnlyList<string> Folders { get; }

    internal string GroupHeader { get; }

    internal string SelectionHeader { get; }
}

internal sealed class DuplicateHashCleanupRequest
{
    internal DuplicateHashCleanupRequest(
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

internal sealed class DuplicateMaintenanceConfirmationResult
{
    private DuplicateMaintenanceConfirmationResult(
        bool accepted,
        DuplicateFolderMergeOperation operation,
        Exception failure)
    {
        Accepted = accepted;
        Operation = operation;
        Failure = failure;
    }

    internal bool Accepted { get; }

    internal DuplicateFolderMergeOperation Operation { get; }

    internal Exception Failure { get; }

    internal static DuplicateMaintenanceConfirmationResult AcceptedResult { get; } = new(true, null, null);

    internal static DuplicateMaintenanceConfirmationResult Rejected { get; } = new(false, null, null);

    internal static DuplicateMaintenanceConfirmationResult AcceptedOperation(DuplicateFolderMergeOperation operation)
    {
        return new DuplicateMaintenanceConfirmationResult(
            true,
            operation ?? throw new ArgumentNullException(nameof(operation)),
            null);
    }

    internal static DuplicateMaintenanceConfirmationResult Failed(Exception failure)
    {
        return new DuplicateMaintenanceConfirmationResult(
            false,
            null,
            failure ?? throw new ArgumentNullException(nameof(failure)));
    }
}

internal sealed class DuplicateFolderMergeOperation : IDuplicateMaintenanceOperation
{
    internal DuplicateFolderMergeOperation(DuplicateFolderMergeRequest request)
    {
        Request = request ?? throw new ArgumentNullException(nameof(request));
    }

    internal DuplicateFolderMergeRequest Request { get; }

    public string SelectionHeader => Request.SelectionHeader;
}

internal sealed class DuplicateHashCleanupPlan
{
    internal DuplicateHashCleanupPlan(
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

internal sealed class DuplicateHashCleanupOperation : IDuplicateMaintenanceOperation
{
    internal DuplicateHashCleanupOperation(DuplicateHashCleanupPlan plan)
    {
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
    }

    internal DuplicateHashCleanupPlan Plan { get; }

    public string SelectionHeader => Plan.SelectionHeader;
}

internal sealed class DuplicateHashCleanupConfirmationResult
{
    private DuplicateHashCleanupConfirmationResult(
        bool accepted,
        bool hasWork,
        DuplicateHashCleanupPlan plan,
        DuplicateHashCleanupOperation operation,
        Exception failure)
    {
        Accepted = accepted;
        HasWork = hasWork;
        Plan = plan;
        Operation = operation;
        Failure = failure;
    }

    internal bool Accepted { get; }

    internal bool HasWork { get; }

    internal DuplicateHashCleanupPlan Plan { get; }

    internal DuplicateHashCleanupOperation Operation { get; }

    internal Exception Failure { get; }

    internal static DuplicateHashCleanupConfirmationResult NoWork(string selectionHeader)
    {
        return new DuplicateHashCleanupConfirmationResult(
            accepted: true,
            hasWork: false,
            plan: new DuplicateHashCleanupPlan(null, [], selectionHeader),
            operation: null,
            failure: null);
    }

    internal static DuplicateHashCleanupConfirmationResult AcceptedResult(DuplicateHashCleanupPlan plan)
    {
        return new DuplicateHashCleanupConfirmationResult(
            accepted: true,
            hasWork: true,
            plan ?? throw new ArgumentNullException(nameof(plan)),
            operation: new DuplicateHashCleanupOperation(plan),
            failure: null);
    }

    internal static DuplicateHashCleanupConfirmationResult Rejected(DuplicateHashCleanupPlan plan)
    {
        return new DuplicateHashCleanupConfirmationResult(
            accepted: false,
            hasWork: true,
            plan,
            operation: null,
            failure: null);
    }

    internal static DuplicateHashCleanupConfirmationResult Failed(Exception failure)
    {
        return new DuplicateHashCleanupConfirmationResult(
            accepted: false,
            hasWork: false,
            plan: null,
            operation: null,
            failure ?? throw new ArgumentNullException(nameof(failure)));
    }
}

internal sealed class DuplicateMaintenanceMutationResult
{
    private DuplicateMaintenanceMutationResult(
        bool succeeded,
        string selectionHeader,
        Exception failure)
    {
        Succeeded = succeeded;
        SelectionHeader = selectionHeader;
        Failure = failure;
    }

    internal bool Succeeded { get; }

    internal string SelectionHeader { get; }

    internal Exception Failure { get; }

    internal static DuplicateMaintenanceMutationResult Completed(string selectionHeader)
    {
        return new DuplicateMaintenanceMutationResult(true, selectionHeader, null);
    }

    internal static DuplicateMaintenanceMutationResult Failed(string selectionHeader, Exception failure)
    {
        return new DuplicateMaintenanceMutationResult(
            false,
            selectionHeader,
            failure ?? throw new ArgumentNullException(nameof(failure)));
    }
}

internal sealed class DuplicateMaintenanceWorkflowOwner
{
    private readonly Func<BMSLibrary> libraryProvider;
    private readonly ChartFileOperationSynchronizer chartFileOperations;
    private readonly IDuplicateMaintenanceActivityPort activity;
    private readonly IDuplicateMaintenanceRefreshPort refresh;
    private readonly IDuplicateMaintenancePlaybackPort playback;
    private readonly IUiDialogService dialogs;
    private readonly Func<bool> showConfirmationProvider;
    private readonly IDuplicateMaintenanceStore store;
    private readonly object operationLock = new();
    private readonly HashSet<IDuplicateMaintenanceOperation> issuedOperations = [];

    internal DuplicateMaintenanceWorkflowOwner(
        Func<BMSLibrary> libraryProvider,
        ChartFileOperationSynchronizer chartFileOperations,
        IDuplicateMaintenanceActivityPort activity,
        IDuplicateMaintenanceRefreshPort refresh,
        IDuplicateMaintenancePlaybackPort playback,
        IUiDialogService dialogs,
        Func<bool> showConfirmationProvider,
        IDuplicateMaintenanceStore store = null)
    {
        this.libraryProvider = libraryProvider ?? throw new ArgumentNullException(nameof(libraryProvider));
        this.chartFileOperations = chartFileOperations ?? throw new ArgumentNullException(nameof(chartFileOperations));
        this.activity = activity ?? throw new ArgumentNullException(nameof(activity));
        this.refresh = refresh ?? throw new ArgumentNullException(nameof(refresh));
        this.playback = playback ?? throw new ArgumentNullException(nameof(playback));
        this.dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        this.showConfirmationProvider = showConfirmationProvider ?? throw new ArgumentNullException(nameof(showConfirmationProvider));
        this.store = store ?? new BmsLibraryDuplicateMaintenanceStore();
    }

    internal DuplicateMaintenanceConfirmationResult ConfirmFolderMerge(DuplicateFolderMergeRequest request)
    {
        ValidateFolderMergeRequest(request);
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
        DuplicateMaintenanceConfirmationResult confirmation = ConfirmIfNeeded(
            message,
            "Duplicate folder merge confirmation");
        if (!confirmation.Accepted)
        {
            return confirmation;
        }
        var operation = new DuplicateFolderMergeOperation(request);
        RegisterOperation(operation);
        return DuplicateMaintenanceConfirmationResult.AcceptedOperation(operation);
    }

    internal Task<DuplicateMaintenanceMutationResult> MergeFolderAsync(DuplicateFolderMergeOperation operation)
    {
        if (operation == null)
        {
            throw new ArgumentNullException(nameof(operation));
        }
        ConsumeOperation(operation);
        DuplicateFolderMergeRequest request = operation.Request;
        ValidateFolderMergeRequest(request);
        return Task.Run(() => ExecuteMutation(
            operation,
            library => store.MergeFolder(
                library,
                request.SourceDirectory,
                request.DestinationDirectory,
                Stopwatch.GetTimestamp()),
            playback.StopPlaybackForMerge,
            "merge_folder"));
    }

    internal DuplicateHashCleanupConfirmationResult ConfirmHashCleanup(DuplicateHashCleanupRequest request)
    {
        ValidateHashCleanupRequest(request);
        try
        {
            List<ChartFile> chartsInFolder = [.. request.Charts.Where(chart => IsChartInFolder(chart, request.FolderPath))];
            List<ChartFile> deletionList = BuildHashCleanupDeletionList(chartsInFolder);
            if (deletionList.Count == 0)
            {
                return DuplicateHashCleanupConfirmationResult.NoWork(request.SelectionHeader);
            }

            var plan = new DuplicateHashCleanupPlan(
                request.FolderPath,
                deletionList,
                request.SelectionHeader);
            if (!showConfirmationProvider())
            {
                DuplicateHashCleanupConfirmationResult accepted = DuplicateHashCleanupConfirmationResult.AcceptedResult(plan);
                RegisterOperation(accepted.Operation);
                return accepted;
            }

            DuplicateMaintenanceConfirmationResult confirmation = ConfirmIfNeeded(
                string.Format(BeMusicSeeker.Properties.Resources.Msg_cleanup_duplicate_hash, deletionList.Count),
                "Duplicate hash cleanup confirmation");
            if (confirmation.Failure != null)
            {
                return DuplicateHashCleanupConfirmationResult.Failed(confirmation.Failure);
            }
            if (!confirmation.Accepted)
            {
                return DuplicateHashCleanupConfirmationResult.Rejected(plan);
            }
            DuplicateHashCleanupConfirmationResult acceptedAfterPrompt = DuplicateHashCleanupConfirmationResult.AcceptedResult(plan);
            RegisterOperation(acceptedAfterPrompt.Operation);
            return acceptedAfterPrompt;
        }
        catch (Exception exception)
        {
            return DuplicateHashCleanupConfirmationResult.Failed(exception);
        }
    }

    internal Task<DuplicateMaintenanceMutationResult> CleanupHashAsync(DuplicateHashCleanupOperation operation)
    {
        if (operation == null)
        {
            throw new ArgumentNullException(nameof(operation));
        }
        ConsumeOperation(operation);
        DuplicateHashCleanupPlan plan = operation.Plan;
        if (plan.ChartsToRemove.Count == 0)
        {
            return Task.FromResult(DuplicateMaintenanceMutationResult.Completed(plan.SelectionHeader));
        }
        return Task.Run(() => ExecuteMutation(
            operation,
            library => store.RemoveCharts(library, plan.ChartsToRemove),
            () => playback.StopPlaybackForCharts(plan.ChartsToRemove),
            refreshPriorityReason: null));
    }

    private DuplicateMaintenanceConfirmationResult ConfirmIfNeeded(string message, string routeName)
    {
        try
        {
            if (!showConfirmationProvider())
            {
                return DuplicateMaintenanceConfirmationResult.AcceptedResult;
            }
            UiDialogResult result = dialogs.ConfirmAsync(new UiConfirmationRequest(
                message,
                BeMusicSeeker.Properties.Resources.Confirm,
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question,
                MessageBoxResult.Cancel)).GetAwaiter().GetResult();
            if (result == null)
            {
                return DuplicateMaintenanceConfirmationResult.Failed(
                    new InvalidOperationException(routeName + " returned no result."));
            }
            return result.Status switch
            {
                UiDialogStatus.Accepted => DuplicateMaintenanceConfirmationResult.AcceptedResult,
                UiDialogStatus.Rejected or UiDialogStatus.CancelledByUser => DuplicateMaintenanceConfirmationResult.Rejected,
                UiDialogStatus.ClosedByUser => result.MessageBoxResult is MessageBoxResult.OK or MessageBoxResult.Yes
                    ? DuplicateMaintenanceConfirmationResult.AcceptedResult
                    : DuplicateMaintenanceConfirmationResult.Rejected,
                _ => DuplicateMaintenanceConfirmationResult.Failed(
                    new InvalidOperationException(
                        routeName + " could not be displayed (" + result.Status + ").",
                        result.Exception)),
            };
        }
        catch (Exception exception)
        {
            return DuplicateMaintenanceConfirmationResult.Failed(exception);
        }
    }

    private DuplicateMaintenanceMutationResult ExecuteMutation(
        IDuplicateMaintenanceOperation operation,
        Action<BMSLibrary> mutation,
        Action stopPlayback,
        string refreshPriorityReason)
    {
        var failures = new List<ExceptionDispatchInfo>();
        BMSLibrary library = null;
        BMSLibrary.OperationDialogScope dialogScope = null;
        IDisposable operationGate = null;
        bool activityStarted = false;
        bool suppressionStarted = false;
        bool refreshPriorityStarted = false;
        try
        {
            library = libraryProvider();
            if (library == null)
            {
                return DuplicateMaintenanceMutationResult.Failed(
                    operation.SelectionHeader,
                    new InvalidOperationException("Duplicate maintenance library is not available."));
            }
            dialogScope = library.BeginOperationDialogScope();
            activity.BeginActivity();
            activityStarted = true;
            operationGate = chartFileOperations.Enter();
            stopPlayback();
            refresh.BeginRefreshSuppression();
            suppressionStarted = true;
            if (!string.IsNullOrWhiteSpace(refreshPriorityReason))
            {
                refresh.BeginRefreshPriorityWindow(refreshPriorityReason);
                refreshPriorityStarted = true;
            }
            mutation(library);
        }
        catch (Exception exception)
        {
            failures.Add(ExceptionDispatchInfo.Capture(exception));
        }
        finally
        {
            if (suppressionStarted)
            {
                CaptureCleanupFailure(refresh.EndRefreshSuppression, failures);
            }
            if (refreshPriorityStarted)
            {
                CaptureCleanupFailure(
                    () => refresh.ScheduleRefreshPriorityWindowRelease(refreshPriorityReason),
                    failures);
            }
            if (operationGate != null)
            {
                CaptureCleanupFailure(operationGate.Dispose, failures);
            }
            if (activityStarted)
            {
                CaptureCleanupFailure(activity.EndActivity, failures);
            }
            if (dialogScope != null)
            {
                CaptureCleanupFailure(dialogScope.Dispose, failures);
                CaptureCleanupFailure(dialogScope.Flush, failures);
            }
        }
        if (failures.Count == 0)
        {
            return DuplicateMaintenanceMutationResult.Completed(operation.SelectionHeader);
        }
        return DuplicateMaintenanceMutationResult.Failed(
            operation.SelectionHeader,
            failures.Count == 1
                ? failures[0].SourceException
                : new AggregateException(failures.Select(failure => failure.SourceException)));
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

    private static void ValidateFolderMergeRequest(DuplicateFolderMergeRequest request)
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

    private static void ValidateHashCleanupRequest(DuplicateHashCleanupRequest request)
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

    private void RegisterOperation(IDuplicateMaintenanceOperation operation)
    {
        lock (operationLock)
        {
            issuedOperations.Add(operation);
        }
    }

    private void ConsumeOperation(IDuplicateMaintenanceOperation operation)
    {
        lock (operationLock)
        {
            if (!issuedOperations.Remove(operation))
            {
                throw new InvalidOperationException("Duplicate maintenance operation was not confirmed by this owner or was already consumed.");
            }
        }
    }
}

internal sealed class BmsLibraryDuplicateMaintenanceStore : IDuplicateMaintenanceStore
{
    public void MergeFolder(
        BMSLibrary library,
        string sourceDirectory,
        string destinationDirectory,
        long operationId)
    {
        library.MergeChartDirectory(sourceDirectory, destinationDirectory, operationId);
    }

    public void RemoveCharts(BMSLibrary library, IReadOnlyList<ChartFile> charts)
    {
        List<LibraryChartRef> chartRefs = [.. (charts ?? [])
            .Select(LibraryChartRef.FromChartFile)
            .Where(chart => chart != null)];
        if (chartRefs.Count > 0)
        {
            library.RemoveLibraryCharts(chartRefs);
        }
    }
}

internal sealed class NoOpDuplicateMaintenanceActivityPort : IDuplicateMaintenanceActivityPort
{
    public void BeginActivity()
    {
    }

    public void EndActivity()
    {
    }
}

internal sealed class NoOpDuplicateMaintenanceRefreshPort : IDuplicateMaintenanceRefreshPort
{
    public void BeginRefreshSuppression()
    {
    }

    public void EndRefreshSuppression()
    {
    }

    public void BeginRefreshPriorityWindow(string reason)
    {
    }

    public void ScheduleRefreshPriorityWindowRelease(string reason)
    {
    }
}
