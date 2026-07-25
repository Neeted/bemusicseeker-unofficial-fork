using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Views.Dialogs;

namespace BeMusicSeeker.ViewModels;

internal interface IFolderAutoRenameMutationPort
{
    bool HasTargets(BMSLibrary library, string parentDirectory);

    FolderAutoRenameExecutionResult RenameSelected(
        BMSLibrary library,
        ChartFolderAutoRenameRequest request,
        Action<int, int, string> progressReporter);

    bool RenameAll(
        BMSLibrary library,
        string parentDirectory,
        Action<int, int, string> progressReporter);
}

internal sealed class BmsLibraryFolderAutoRenameMutationPort : IFolderAutoRenameMutationPort
{
    public bool HasTargets(BMSLibrary library, string parentDirectory)
    {
        return library?.HasAutoRenameAllChartFolderTargets(parentDirectory) == true;
    }

    public FolderAutoRenameExecutionResult RenameSelected(
        BMSLibrary library,
        ChartFolderAutoRenameRequest request,
        Action<int, int, string> progressReporter)
    {
        library?.AutoRenameChartFolders(request?.Charts ?? [], progressReporter: progressReporter);
        return new FolderAutoRenameExecutionResult { RefreshRequired = true };
    }

    public bool RenameAll(
        BMSLibrary library,
        string parentDirectory,
        Action<int, int, string> progressReporter)
    {
        return library?.AutoRenameAllChartFolders(parentDirectory, progressReporter) == true;
    }
}

internal interface IFolderAutoRenamePlaybackPort
{
    void StopPlaybackForCharts(IReadOnlyList<ChartFile> charts);

    void StopPlaybackForFolderMutation();
}

internal sealed class FolderAutoRenameRefreshSuppressionChangedEventArgs : EventArgs
{
    internal FolderAutoRenameRefreshSuppressionChangedEventArgs(bool isSuppressed)
    {
        IsSuppressed = isSuppressed;
    }

    internal bool IsSuppressed { get; }
}

internal sealed class FolderAutoRenameProgressSnapshot
{
    internal int TotalCount { get; init; }

    internal int ProcessedCount { get; init; }

    internal string CurrentPath { get; init; } = string.Empty;

    internal bool IsCompleted { get; init; }
}

internal sealed class FolderAutoRenameExecutionResult
{
    internal bool RefreshRequired { get; init; }
}

internal sealed class FolderAutoRenameCompletionReceipt
{
    internal FolderAutoRenameCompletionReceipt(long generation, bool allFolders, FolderAutoRenameExecutionResult result)
    {
        Generation = generation;
        AllFolders = allFolders;
        RefreshRequired = result?.RefreshRequired == true;
    }

    internal long Generation { get; }

    internal bool AllFolders { get; }

    internal bool RefreshRequired { get; }
}

internal sealed class FolderAutoRenameFailure
{
    internal FolderAutoRenameFailure(long generation, bool allFolders, Exception exception)
    {
        Generation = generation;
        AllFolders = allFolders;
        Exception = exception ?? throw new ArgumentNullException(nameof(exception));
    }

    internal long Generation { get; }

    internal bool AllFolders { get; }

    internal Exception Exception { get; }
}

/// <summary>
/// Owns the scheduled shell lifecycle for selected and all-folder auto-rename
/// requests while the library remains responsible for filesystem/catalog mutation.
/// </summary>
internal sealed class FolderAutoRenameWorkflowOwner
{
    private readonly object syncRoot = new();

    private readonly IFolderAutoRenameMutationPort mutationPort;

    private readonly ChartFileOperationSynchronizer chartFileOperations;

    private readonly ChartMutationActivityOwner chartMutationActivity;

    private readonly IFolderAutoRenamePlaybackPort playback;

    private readonly Func<Action, Task> schedule;

    private readonly Action<Action> dispatchToUi;

    private readonly IUiDialogService dialogs;

    private readonly Action<string> logInfo;

    private readonly Action<Exception> reportWorkflowFailure;

    private readonly Action<Exception> reportNotificationFailure;

    private BMSLibrary library;

    private long generation;

    private long statusVersion;

    private RunContext activeRun;

    private bool shutdownRequested;

    internal FolderAutoRenameWorkflowOwner(
        ChartFileOperationSynchronizer chartFileOperations,
        ChartMutationActivityOwner chartMutationActivity,
        IFolderAutoRenameMutationPort mutationPort,
        IFolderAutoRenamePlaybackPort playback,
        Func<Action, Task> schedule,
        Action<Action> dispatchToUi,
        IUiDialogService dialogs,
        Action<string> logInfo = null,
        Action<Exception> reportNotificationFailure = null,
        Action<Exception> reportWorkflowFailure = null)
    {
        this.chartFileOperations = chartFileOperations ?? throw new ArgumentNullException(nameof(chartFileOperations));
        this.chartMutationActivity = chartMutationActivity ?? throw new ArgumentNullException(nameof(chartMutationActivity));
        this.mutationPort = mutationPort ?? throw new ArgumentNullException(nameof(mutationPort));
        this.playback = playback ?? throw new ArgumentNullException(nameof(playback));
        this.schedule = schedule ?? throw new ArgumentNullException(nameof(schedule));
        this.dispatchToUi = dispatchToUi ?? throw new ArgumentNullException(nameof(dispatchToUi));
        this.dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        this.logInfo = logInfo;
        this.reportNotificationFailure = reportNotificationFailure;
        this.reportWorkflowFailure = reportWorkflowFailure;
    }

    internal event Action<FolderAutoRenameProgressSnapshot> ProgressChanged;

    internal event Action<FolderAutoRenameCompletionReceipt> CompletionPublished;

    internal event Action<FolderAutoRenameFailure> FailurePublished;

    internal event Action TerminalPublished;

    internal event EventHandler<FolderAutoRenameRefreshSuppressionChangedEventArgs> RefreshSuppressionChanged;

    internal bool IsActive
    {
        get
        {
            lock (syncRoot)
            {
                return activeRun != null;
            }
        }
    }

    internal bool IsIdle => !IsActive;

    internal void AttachLibrary(BMSLibrary nextLibrary)
    {
        RunContext run;
        long resetGeneration;
        lock (syncRoot)
        {
            if (ReferenceEquals(library, nextLibrary))
            {
                return;
            }
            library = nextLibrary;
            generation++;
            statusVersion++;
            run = activeRun;
            resetGeneration = generation;
        }
        if (run != null)
        {
            DispatchNotification(() =>
            {
                lock (syncRoot)
                {
                    if (generation != resetGeneration || !ReferenceEquals(library, nextLibrary))
                    {
                        return;
                    }
                }
                ProgressChanged?.Invoke(CreateResetProgress());
            });
        }
    }

    internal bool RequestStartSelected(IReadOnlyList<ChartOperationTarget> targets)
    {
        if (!ChartFolderAutoRenameRequest.TryCreate(targets, out ChartFolderAutoRenameRequest request))
        {
            return false;
        }
        RunContext run;
        lock (syncRoot)
        {
            if (!TryCreateRunUnsafe(allFolders: false, request, parentDirectory: null, out run))
            {
                return false;
            }
        }
        return Schedule(run);
    }

    internal async Task RequestStartAllAsync(string parentDirectory)
    {
        if (!TryCaptureAllRequest(parentDirectory, out BMSLibrary requestedLibrary))
        {
            return;
        }

        UiDialogResult confirmation = await dialogs.ConfirmAsync(new UiConfirmationRequest(
            BeMusicSeeker.Properties.Resources.Msg_rename_folders,
            BeMusicSeeker.Properties.Resources.Confirm,
            System.Windows.MessageBoxButton.OKCancel,
            System.Windows.MessageBoxImage.Question,
            System.Windows.MessageBoxResult.Cancel));
        UiDialogRoute.ThrowIfNotShown(confirmation, "folderAutoRenameAllConfirmation");
        if (!confirmation.IsAccepted)
        {
            return;
        }

        if (!LongPathFileSystem.DirectoryExists(parentDirectory))
        {
            return;
        }

        RunContext run;
        lock (syncRoot)
        {
            if (!ReferenceEquals(library, requestedLibrary)
                || !TryCreateRunUnsafe(allFolders: true, request: null, parentDirectory, out run))
            {
                return;
            }
        }
        Schedule(run);
    }

    internal void RequestShutdown()
    {
        lock (syncRoot)
        {
            shutdownRequested = true;
            generation++;
            statusVersion++;
        }
        DispatchNotification(() => ProgressChanged?.Invoke(CreateResetProgress()));
    }

    private bool TryCreateRunUnsafe(
        bool allFolders,
        ChartFolderAutoRenameRequest request,
        string parentDirectory,
        out RunContext run)
    {
        if (shutdownRequested || library == null || activeRun != null)
        {
            run = null;
            return false;
        }
        generation++;
        run = new RunContext(generation, library, allFolders, request, parentDirectory);
        activeRun = run;
        return true;
    }

    private bool TryCaptureAllRequest(string parentDirectory, out BMSLibrary requestedLibrary)
    {
        if (string.IsNullOrWhiteSpace(parentDirectory) || !LongPathFileSystem.DirectoryExists(parentDirectory))
        {
            requestedLibrary = null;
            return false;
        }
        lock (syncRoot)
        {
            requestedLibrary = library;
            return !shutdownRequested && requestedLibrary != null && activeRun == null;
        }
    }

    private bool Schedule(RunContext run)
    {
        try
        {
            Task scheduled = schedule(() => Execute(run));
            if (scheduled == null)
            {
                throw new InvalidOperationException("Folder auto-rename scheduler returned no task.");
            }
            scheduled.ContinueWith(
                task =>
                {
                    if (task.IsFaulted)
                    {
                        CompleteFailure(run, task.Exception?.GetBaseException() ?? new InvalidOperationException("Folder auto-rename scheduler failed."));
                    }
                    else if (task.IsCanceled)
                    {
                        CompleteFailure(run, new InvalidOperationException("Folder auto-rename scheduler canceled."));
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return true;
        }
        catch (Exception exception)
        {
            CompleteFailure(run, exception);
            return true;
        }
    }

    private void Execute(RunContext run)
    {
        try
        {
            if (!IsCurrentGeneration(run))
            {
                CompleteStale(run);
                return;
            }
            if (run.AllFolders)
            {
                bool hasTargets;
                try
                {
                    hasTargets = false;
                    if (!ExecuteMutation(
                        run,
                        stopPlayback: null,
                        mutation: () => hasTargets = mutationPort.HasTargets(run.Library, run.ParentDirectory),
                        refreshSuppression: false))
                    {
                        CompleteStale(run);
                        return;
                    }
                }
                catch (Exception exception)
                {
                    LogInfoSafely("folder_auto_rename preflight_failed message=" + FormatExceptionMessage(exception));
                    CompleteFailure(run, exception);
                    return;
                }
                if (!hasTargets)
                {
                    CompleteSuccess(run, new FolderAutoRenameExecutionResult());
                    return;
                }
            }
            PublishProgress(run, new FolderAutoRenameProgressSnapshot
            {
                TotalCount = 1,
                ProcessedCount = 0,
                CurrentPath = string.Empty
            });
            LogInfoSafely("folder_auto_rename start scope=" + (run.AllFolders ? "all" : "selected"));
            Action<int, int, string> progressReporter = (total, processed, currentPath) => PublishProgress(run, new FolderAutoRenameProgressSnapshot
            {
                TotalCount = total,
                ProcessedCount = processed,
                CurrentPath = currentPath ?? string.Empty
            });
            FolderAutoRenameExecutionResult result = null;
            if (run.AllFolders)
            {
                bool changed = false;
                if (!ExecuteMutation(
                    run,
                    playback.StopPlaybackForFolderMutation,
                    () => changed = mutationPort.RenameAll(run.Library, run.ParentDirectory, progressReporter),
                    refreshSuppression: true))
                {
                    CompleteStale(run);
                    return;
                }
                result = new FolderAutoRenameExecutionResult { RefreshRequired = changed };
            }
            else
            {
                if (!ExecuteMutation(
                    run,
                    () => playback.StopPlaybackForCharts(run.SelectedRequest.Charts),
                    () => result = mutationPort.RenameSelected(run.Library, run.SelectedRequest, progressReporter),
                    refreshSuppression: true))
                {
                    CompleteStale(run);
                    return;
                }
            }
            if (result == null)
            {
                throw new InvalidOperationException("Folder auto-rename executor returned no result.");
            }
            LogInfoSafely("folder_auto_rename done scope=" + (run.AllFolders ? "all" : "selected") + " refreshRequired=" + result.RefreshRequired);
            CompleteSuccess(run, result);
        }
        catch (Exception exception)
        {
            LogInfoSafely("folder_auto_rename failed scope=" + (run.AllFolders ? "all" : "selected") + " message=" + FormatExceptionMessage(exception));
            CompleteFailure(run, exception);
        }
    }

    private bool ExecuteMutation(
        RunContext run,
        Action stopPlayback,
        Action mutation,
        bool refreshSuppression)
    {
        BMSLibrary library = run.Library;
        BMSLibrary.OperationDialogScope dialogScope = null;
        IDisposable activityLease = null;
        IDisposable operationGate = null;
        bool suppressionStarted = false;
        bool mutationAllowed = true;
        var failures = new List<ExceptionDispatchInfo>();
        try
        {
            dialogScope = library.BeginOperationDialogScope();
            activityLease = chartMutationActivity.Enter();
            operationGate = chartFileOperations.Enter();
            if (!IsCurrentGeneration(run))
            {
                mutationAllowed = false;
            }
            else
            {
                stopPlayback?.Invoke();
                suppressionStarted = refreshSuppression;
                if (suppressionStarted)
                {
                    PublishRefreshSuppressionChanged(isSuppressed: true);
                }
                mutation();
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
                CaptureCleanupFailure(() => PublishRefreshSuppressionChanged(isSuppressed: false), failures);
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
        switch (failures.Count)
        {
            case 0:
                return mutationAllowed;
            case 1:
                failures[0].Throw();
                return false;
            default:
                throw new AggregateException(failures.Select(failure => failure.SourceException));
        }
    }

    private void PublishRefreshSuppressionChanged(bool isSuppressed)
    {
        RefreshSuppressionChanged?.Invoke(
            this,
            new FolderAutoRenameRefreshSuppressionChangedEventArgs(isSuppressed));
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

    private void PublishProgress(RunContext run, FolderAutoRenameProgressSnapshot progress)
    {
        if (progress == null)
        {
            return;
        }
        FolderAutoRenameProgressSnapshot snapshot;
        long snapshotStatusVersion;
        lock (syncRoot)
        {
            if (!ReferenceEquals(activeRun, run) || !IsCurrentGenerationUnsafe(run))
            {
                return;
            }
            snapshot = CloneProgress(progress);
            run.LastProgress = snapshot;
            run.ProgressStarted = true;
            statusVersion++;
            snapshotStatusVersion = statusVersion;
        }
        DispatchNotification(() =>
        {
            lock (syncRoot)
            {
                if (!IsCurrentGenerationUnsafe(run) || statusVersion != snapshotStatusVersion)
                {
                    return;
                }
            }
            InvokeObserverSafely(() => ProgressChanged?.Invoke(CloneProgress(snapshot)));
        });
    }

    private void CompleteSuccess(RunContext run, FolderAutoRenameExecutionResult result)
    {
        long terminalStatusVersion;
        bool publish;
        lock (syncRoot)
        {
            if (!ReferenceEquals(activeRun, run))
            {
                return;
            }
            activeRun = null;
            statusVersion++;
            terminalStatusVersion = statusVersion;
            publish = IsCurrentGenerationUnsafe(run);
            if (publish)
            {
                activeRun = run;
                run.TerminalPending = true;
            }
        }
        if (!publish)
        {
            return;
        }
        FolderAutoRenameProgressSnapshot terminalProgress = CreateTerminalProgress(run);
        var receipt = new FolderAutoRenameCompletionReceipt(run.Generation, run.AllFolders, result);
        bool dispatched = DispatchNotification(() =>
        {
            lock (syncRoot)
            {
                if (!IsCurrentGenerationUnsafe(run) || statusVersion != terminalStatusVersion)
                {
                    FinishTerminalPublication(run);
                    return;
                }
            }
            if (run.ProgressStarted)
            {
                InvokeObserverSafely(() => ProgressChanged?.Invoke(CloneProgress(terminalProgress)));
            }
            InvokeObserverSafely(() => CompletionPublished?.Invoke(receipt));
            InvokeObserverSafely(() => TerminalPublished?.Invoke());
            FinishTerminalPublication(run);
        });
        if (!dispatched)
        {
            FinishTerminalPublication(run);
        }
    }

    private void CompleteFailure(RunContext run, Exception exception)
    {
        long terminalStatusVersion;
        bool publish;
        lock (syncRoot)
        {
            if (!ReferenceEquals(activeRun, run))
            {
                return;
            }
            activeRun = null;
            statusVersion++;
            terminalStatusVersion = statusVersion;
            publish = IsCurrentGenerationUnsafe(run);
            if (publish)
            {
                activeRun = run;
                run.TerminalPending = true;
            }
        }
        ReportWorkflowFailure(exception);
        if (!publish)
        {
            return;
        }
        FolderAutoRenameProgressSnapshot terminalProgress = CreateTerminalProgress(run);
        var failure = new FolderAutoRenameFailure(run.Generation, run.AllFolders, exception);
        bool dispatched = DispatchNotification(() =>
        {
            lock (syncRoot)
            {
                if (!IsCurrentGenerationUnsafe(run) || statusVersion != terminalStatusVersion)
                {
                    FinishTerminalPublication(run);
                    return;
                }
            }
            if (run.ProgressStarted)
            {
                InvokeObserverSafely(() => ProgressChanged?.Invoke(CloneProgress(terminalProgress)));
            }
            InvokeObserverSafely(() => FailurePublished?.Invoke(failure));
            InvokeObserverSafely(() => TerminalPublished?.Invoke());
            FinishTerminalPublication(run);
        });
        if (!dispatched)
        {
            FinishTerminalPublication(run);
        }
    }

    private void FinishTerminalPublication(RunContext run)
    {
        lock (syncRoot)
        {
            if (ReferenceEquals(activeRun, run) && run.TerminalPending)
            {
                activeRun = null;
                run.TerminalPending = false;
                statusVersion++;
            }
        }
    }

    private void CompleteStale(RunContext run)
    {
        lock (syncRoot)
        {
            if (ReferenceEquals(activeRun, run))
            {
                activeRun = null;
                statusVersion++;
            }
        }
    }

    private bool IsCurrentGeneration(RunContext run)
    {
        lock (syncRoot)
        {
            return IsCurrentGenerationUnsafe(run);
        }
    }

    private bool IsCurrentGenerationUnsafe(RunContext run)
    {
        return !shutdownRequested
            && generation == run.Generation
            && ReferenceEquals(library, run.Library);
    }

    private bool DispatchNotification(Action notification)
    {
        try
        {
            dispatchToUi(() =>
            {
                try
                {
                    notification();
                }
                catch (Exception exception)
                {
                    ReportNotificationFailure(exception);
                }
            });
            return true;
        }
        catch (Exception exception)
        {
            ReportNotificationFailure(exception);
            return false;
        }
    }

    private void ReportWorkflowFailure(Exception exception)
    {
        try
        {
            reportWorkflowFailure?.Invoke(exception);
        }
        catch
        {
        }
    }

    private void ReportNotificationFailure(Exception exception)
    {
        try
        {
            reportNotificationFailure?.Invoke(exception);
        }
        catch
        {
        }
    }

    private void InvokeObserverSafely(Action observer)
    {
        try
        {
            observer?.Invoke();
        }
        catch (Exception exception)
        {
            ReportNotificationFailure(exception);
        }
    }

    private void LogInfoSafely(string message)
    {
        try
        {
            logInfo?.Invoke(message);
        }
        catch
        {
        }
    }

    private static string FormatExceptionMessage(Exception exception)
    {
        return (exception?.Message ?? string.Empty).Replace(Environment.NewLine, " ");
    }

    private static FolderAutoRenameProgressSnapshot CloneProgress(FolderAutoRenameProgressSnapshot source)
    {
        return new FolderAutoRenameProgressSnapshot
        {
            TotalCount = source?.TotalCount ?? 0,
            ProcessedCount = source?.ProcessedCount ?? 0,
            CurrentPath = source?.CurrentPath ?? string.Empty,
            IsCompleted = source?.IsCompleted == true
        };
    }

    private static FolderAutoRenameProgressSnapshot CreateTerminalProgress(RunContext run)
    {
        FolderAutoRenameProgressSnapshot progress = CloneProgress(run.LastProgress ?? new FolderAutoRenameProgressSnapshot { TotalCount = 1 });
        return new FolderAutoRenameProgressSnapshot
        {
            TotalCount = progress.TotalCount,
            ProcessedCount = progress.ProcessedCount,
            CurrentPath = string.Empty,
            IsCompleted = true
        };
    }

    private static FolderAutoRenameProgressSnapshot CreateResetProgress()
    {
        return new FolderAutoRenameProgressSnapshot
        {
            TotalCount = 1,
            ProcessedCount = 0,
            CurrentPath = string.Empty,
            IsCompleted = true
        };
    }

    private sealed class RunContext
    {
        internal RunContext(
            long generation,
            BMSLibrary library,
            bool allFolders,
            ChartFolderAutoRenameRequest selectedRequest,
            string parentDirectory)
        {
            Generation = generation;
            Library = library;
            AllFolders = allFolders;
            SelectedRequest = selectedRequest;
            ParentDirectory = parentDirectory;
        }

        internal long Generation { get; }

        internal BMSLibrary Library { get; }

        internal bool AllFolders { get; }

        internal ChartFolderAutoRenameRequest SelectedRequest { get; }

        internal string ParentDirectory { get; }

        internal FolderAutoRenameProgressSnapshot LastProgress { get; set; }

        internal bool ProgressStarted { get; set; }

        internal bool TerminalPending { get; set; }
    }
}
