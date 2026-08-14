using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.ViewModels;

internal sealed class ShutdownPreparationResult
{
    internal ShutdownPreparationResult(
        string reason,
        long elapsedMs,
        bool slowWaitLogged,
        int sqliteCloseFailureCount)
    {
        Reason = reason ?? "shutdown";
        ElapsedMs = elapsedMs;
        SlowWaitLogged = slowWaitLogged;
        SqliteCloseFailureCount = Math.Max(0, sqliteCloseFailureCount);
    }

    internal string Reason { get; }

    internal long ElapsedMs { get; }

    internal bool SlowWaitLogged { get; }

    internal int SqliteCloseFailureCount { get; }

    internal string ToLogFields()
    {
        return "reason=" + FormatForLog(Reason)
            + " elapsedMs=" + ElapsedMs
            + " slowWaitLogged=" + FormatBool(SlowWaitLogged)
            + " sqliteCloseFailureCount=" + SqliteCloseFailureCount;
    }

    private static string FormatBool(bool value)
    {
        return value.ToString().ToLowerInvariant();
    }

    private static string FormatForLog(string value)
    {
        return (value ?? string.Empty).Replace(Environment.NewLine, " | ");
    }
}

internal sealed class ShellShutdownWorkflowCompletionReceipt
{
    internal ShellShutdownWorkflowCompletionReceipt(
        string reason,
        bool preparationSucceeded,
        Exception exception = null)
    {
        Reason = reason ?? "window_close";
        PreparationSucceeded = preparationSucceeded;
        Exception = exception;
    }

    internal string Reason { get; }

    internal bool PreparationSucceeded { get; }

    internal Exception Exception { get; }

    internal bool CloseAllowed => true;
}

/// <summary>
/// Owns the shell close/update preparation arbitration while WPF remains the terminal shutdown host.
/// </summary>
internal sealed class ShellShutdownWorkflowOwner
{
    private static readonly TimeSpan ShutdownDrainWarningThreshold = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan ShutdownQueueDrainWarningThreshold = TimeSpan.FromSeconds(20);

    private static readonly TimeSpan PerformanceDiagnosticsDrainLimit = TimeSpan.FromSeconds(5);

    private readonly object syncRoot = new();

    private readonly TaskCompletionSource<Task> shutdownStartCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly StartupUpdateWorkflowOwner startupUpdateWorkflow;

    private readonly ElevatedProcessWarningWorkflowOwner elevatedProcessWarningWorkflow;

    private readonly StartupBackgroundTaskSchedulerOwner startupBackgroundTaskScheduler;

    private readonly RegularChartListOwner regularChartListOwner;

    private readonly PlaylistWorkspaceViewModel playlistWorkspace;

    private readonly PlaylistReferenceApplyWorkflowOwner playlistReferenceApplyWorkflow;

    private readonly PlayHistoryWorkflowOwner playHistoryWorkflowOwner;

    private readonly PackageInstallWorkflowOwner packageInstallWorkflow;

    private readonly MaintenanceRescanWorkflowOwner maintenanceRescanWorkflow;

    private readonly FolderAutoRenameWorkflowOwner folderAutoRenameWorkflow;

    private readonly PlaybackPanelViewModel playbackPanel;

    private readonly ISettingsEditSession settingsEditSession;

    private readonly SemaphoreSlim mainOperationSemaphore;

    private readonly StartupProgressWorkflowOwner startupProgressWorkflowOwner;

    private readonly Action<string> markCoordinatedShutdownStarted;

    private readonly Action requestApplicationShutdown;

    private readonly Func<Task> stopPerformanceDiagnostics;

    private readonly Func<Func<Task>, Task> dispatchToUi;

    private readonly Action<string> logShutdown;

    private readonly Action<string> logShutdownWarning;

    private readonly Func<string, string> formatTextForLog;

    private BMSLibrary files;

    private BMSPlaylist tables;

    private bool libraryAttached;

    private bool playlistAttached;

    private int shutdownRequested;

    private Task<ShutdownPreparationResult> preparationTask;

    private Task<ShellShutdownWorkflowCompletionReceipt> windowCloseTask;

    private bool closingOrClosed;

    private bool preparationStarted;

    private bool preparationRunning;

    private bool preparationCompleted;

    private bool preparationWasUpdate;

    private bool closeAllowed;

    private bool updatePreparationFailurePending;

    private bool terminalResourcesClosed;

    private int terminalApplicationShutdownRequested;

    private Task failureDrainTask;

    /// <summary>
    /// Creates the owner that coordinates shell shutdown preparation, terminal cleanup, and the final application-lifetime request.
    /// </summary>
    /// <param name="startupUpdateWorkflow">Startup update workflow that shares shutdown preparation.</param>
    /// <param name="elevatedProcessWarningWorkflow">Elevated-process warning workflow notified during shutdown.</param>
    /// <param name="startupBackgroundTaskScheduler">Owner of startup background tasks that must drain.</param>
    /// <param name="regularChartListOwner">Regular chart list owner that must stop background work.</param>
    /// <param name="playlistWorkspace">Playlist workspace whose operations must drain.</param>
    /// <param name="playHistoryWorkflowOwner">Play-history workflow owner whose refresh must drain.</param>
    /// <param name="packageInstallWorkflow">Package-install workflow cancelled during shutdown.</param>
    /// <param name="maintenanceRescanWorkflow">Maintenance rescan workflow cancelled during shutdown.</param>
    /// <param name="folderAutoRenameWorkflow">Folder rename workflow cancelled during shutdown.</param>
    /// <param name="playbackPanel">Playback owner closed during terminal cleanup.</param>
    /// <param name="settingsEditSession">Settings session saved during terminal cleanup.</param>
    /// <param name="mainOperationSemaphore">Semaphore used to wait for the main operation boundary.</param>
    /// <param name="startupProgressWorkflowOwner">Startup progress owner used to block and unblock interaction.</param>
    /// <param name="markCoordinatedShutdownStarted">Marks the process lifetime as coordinated before preparation.</param>
    /// <param name="requestApplicationShutdown">Requests final application termination after terminal cleanup.</param>
    /// <param name="stopPerformanceDiagnostics">Stops performance diagnostics during preparation.</param>
    /// <param name="dispatchToUi">Dispatches terminal UI work to the shell thread.</param>
    /// <param name="logShutdown">Writes normal shutdown diagnostics.</param>
    /// <param name="logShutdownWarning">Writes shutdown warning diagnostics.</param>
    /// <param name="formatTextForLog">Formats untrusted values for shutdown diagnostics.</param>
    internal ShellShutdownWorkflowOwner(
        StartupUpdateWorkflowOwner startupUpdateWorkflow,
        ElevatedProcessWarningWorkflowOwner elevatedProcessWarningWorkflow,
        StartupBackgroundTaskSchedulerOwner startupBackgroundTaskScheduler,
        RegularChartListOwner regularChartListOwner,
        PlaylistWorkspaceViewModel playlistWorkspace,
        PlayHistoryWorkflowOwner playHistoryWorkflowOwner,
        PackageInstallWorkflowOwner packageInstallWorkflow,
        MaintenanceRescanWorkflowOwner maintenanceRescanWorkflow,
        FolderAutoRenameWorkflowOwner folderAutoRenameWorkflow,
        PlaybackPanelViewModel playbackPanel,
        ISettingsEditSession settingsEditSession,
        SemaphoreSlim mainOperationSemaphore,
        StartupProgressWorkflowOwner startupProgressWorkflowOwner,
        Action<string> markCoordinatedShutdownStarted,
        Action requestApplicationShutdown,
        Func<Task> stopPerformanceDiagnostics,
        Func<Func<Task>, Task> dispatchToUi,
        Action<string> logShutdown,
        Action<string> logShutdownWarning,
        Func<string, string> formatTextForLog)
    {
        this.startupUpdateWorkflow = startupUpdateWorkflow ?? throw new ArgumentNullException(nameof(startupUpdateWorkflow));
        this.elevatedProcessWarningWorkflow = elevatedProcessWarningWorkflow ?? throw new ArgumentNullException(nameof(elevatedProcessWarningWorkflow));
        this.startupBackgroundTaskScheduler = startupBackgroundTaskScheduler ?? throw new ArgumentNullException(nameof(startupBackgroundTaskScheduler));
        this.regularChartListOwner = regularChartListOwner ?? throw new ArgumentNullException(nameof(regularChartListOwner));
        this.playlistWorkspace = playlistWorkspace ?? throw new ArgumentNullException(nameof(playlistWorkspace));
        playlistReferenceApplyWorkflow = playlistWorkspace.PlaylistReferenceApplyWorkflow;
        this.playHistoryWorkflowOwner = playHistoryWorkflowOwner ?? throw new ArgumentNullException(nameof(playHistoryWorkflowOwner));
        this.packageInstallWorkflow = packageInstallWorkflow ?? throw new ArgumentNullException(nameof(packageInstallWorkflow));
        this.maintenanceRescanWorkflow = maintenanceRescanWorkflow ?? throw new ArgumentNullException(nameof(maintenanceRescanWorkflow));
        this.folderAutoRenameWorkflow = folderAutoRenameWorkflow ?? throw new ArgumentNullException(nameof(folderAutoRenameWorkflow));
        this.playbackPanel = playbackPanel ?? throw new ArgumentNullException(nameof(playbackPanel));
        this.settingsEditSession = settingsEditSession ?? throw new ArgumentNullException(nameof(settingsEditSession));
        this.mainOperationSemaphore = mainOperationSemaphore ?? throw new ArgumentNullException(nameof(mainOperationSemaphore));
        this.startupProgressWorkflowOwner = startupProgressWorkflowOwner ?? throw new ArgumentNullException(nameof(startupProgressWorkflowOwner));
        this.markCoordinatedShutdownStarted = markCoordinatedShutdownStarted ?? throw new ArgumentNullException(nameof(markCoordinatedShutdownStarted));
        this.requestApplicationShutdown = requestApplicationShutdown ?? throw new ArgumentNullException(nameof(requestApplicationShutdown));
        this.stopPerformanceDiagnostics = stopPerformanceDiagnostics ?? throw new ArgumentNullException(nameof(stopPerformanceDiagnostics));
        this.dispatchToUi = dispatchToUi ?? throw new ArgumentNullException(nameof(dispatchToUi));
        this.logShutdown = logShutdown ?? throw new ArgumentNullException(nameof(logShutdown));
        this.logShutdownWarning = logShutdownWarning ?? throw new ArgumentNullException(nameof(logShutdownWarning));
        this.formatTextForLog = formatTextForLog ?? throw new ArgumentNullException(nameof(formatTextForLog));
        startupUpdateWorkflow.BindShutdownPreparation(PrepareForStartupUpdateAsync);
    }

    internal bool IsShutdownRequested => Volatile.Read(ref shutdownRequested) != 0;

    internal void AttachLibrary(BMSLibrary library)
    {
        if (library == null)
        {
            throw new ArgumentNullException(nameof(library));
        }
        bool requestShutdown;
        lock (syncRoot)
        {
            files = library;
            Volatile.Write(ref libraryAttached, true);
            requestShutdown = IsShutdownRequested;
        }
        if (requestShutdown)
        {
            TryShutdownStep("library_late_attach", () => library.RequestShutdown("late_attach"));
        }
    }

    internal void AttachPlaylist(BMSPlaylist playlist)
    {
        if (playlist == null)
        {
            throw new ArgumentNullException(nameof(playlist));
        }
        bool requestShutdown;
        lock (syncRoot)
        {
            tables = playlist;
            Volatile.Write(ref playlistAttached, true);
            requestShutdown = IsShutdownRequested;
        }
        if (requestShutdown)
        {
            TryShutdownStep("playlist_late_attach", () => playlist.RequestShutdown("late_attach"));
        }
    }

    internal void CompleteTerminalShutdown()
    {
        lock (syncRoot)
        {
            if (terminalResourcesClosed)
            {
                return;
            }
            terminalResourcesClosed = true;
        }
        TryShutdownStep("set_ui_unblocked", () => startupProgressWorkflowOwner.SetStartupUiInteractionBlocked(false));
        try
        {
            settingsEditSession.Save();
        }
        catch (Exception exception)
        {
            LogWarningSafely(exception, "settings_save_failed");
        }
        Task regularChartListStop = BeginShutdownRequested("terminal_close");
        try
        {
            regularChartListStop.GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            logShutdown("regularChartListStop_final_failed message=" + exception.Message);
        }
        TryShutdownStep("player_close", playbackPanel.CloseProcess);
        TryShutdownStep("audio_native_runtime", Ribbit.Media.Audio.BassAudioRuntime.Shutdown);
        try
        {
            WaitForLr2DbProcessLocksAsync(new ShutdownWaitTracker()).GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            logShutdown("lr2DbProcessLocksFinal_failed message=" + exception.Message);
        }
        try
        {
            TempDirectoryPublisher.RemoveAll((path, exception) =>
                logShutdown("temp_remove_failed path=" + path + " message=" + exception.Message));
        }
        catch (Exception exception)
        {
            logShutdown("temp_remove_failed message=" + exception.Message);
        }
    }

    /// <summary>
    /// Requests process termination through the composed application-lifetime boundary exactly once.
    /// The shell calls this only after window-state capture and terminal resource cleanup complete.
    /// </summary>
    internal void RequestTerminalApplicationShutdown()
    {
        if (Interlocked.Exchange(ref terminalApplicationShutdownRequested, 1) != 0)
        {
            return;
        }

        requestApplicationShutdown();
    }

    internal bool IsClosingOrClosed
    {
        get
        {
            lock (syncRoot)
            {
                return closingOrClosed;
            }
        }
    }

    internal bool IsShutdownPreparationStarted
    {
        get
        {
            lock (syncRoot)
            {
                return preparationStarted;
            }
        }
    }

    internal bool IsShutdownPreparationRunning
    {
        get
        {
            lock (syncRoot)
            {
                return preparationRunning;
            }
        }
    }

    internal bool IsShutdownPrepared
    {
        get
        {
            lock (syncRoot)
            {
                return preparationCompleted;
            }
        }
    }

    internal bool IsCloseAllowed
    {
        get
        {
            lock (syncRoot)
            {
                return closeAllowed;
            }
        }
    }

    internal Task<ShutdownPreparationResult> PrepareForStartupUpdateAsync(string reason)
    {
        Task<ShutdownPreparationResult> preparation = EnsurePreparationAsync(reason, updatePreparation: true);
        bool preparationBelongsToUpdate;
        lock (syncRoot)
        {
            preparationBelongsToUpdate = preparationWasUpdate;
        }
        if (preparationBelongsToUpdate)
        {
            _ = AllowCloseAfterStartupUpdateTerminalAsync(preparation);
        }
        return preparation;
    }

    internal bool ConsumeUpdatePreparationFailure()
    {
        lock (syncRoot)
        {
            if (!updatePreparationFailurePending)
            {
                return false;
            }
            updatePreparationFailurePending = false;
            return true;
        }
    }

    internal Task<ShellShutdownWorkflowCompletionReceipt> RequestWindowCloseAsync()
    {
        TryBeginWindowCloseRequest(out Task<ShellShutdownWorkflowCompletionReceipt> request);
        return request;
    }

    internal bool TryBeginWindowCloseRequest(out Task<ShellShutdownWorkflowCompletionReceipt> request)
    {
        lock (syncRoot)
        {
            if (windowCloseTask != null)
            {
                request = windowCloseTask;
                return false;
            }

            closingOrClosed = true;
            var completion = new TaskCompletionSource<ShellShutdownWorkflowCompletionReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
            windowCloseTask = completion.Task;
            _ = CompleteWindowCloseAsync(completion);
            request = windowCloseTask;
            return true;
        }
    }

    private async Task CompleteWindowCloseAsync(TaskCompletionSource<ShellShutdownWorkflowCompletionReceipt> completion)
    {
        try
        {
            elevatedProcessWarningWorkflow.NotifyClosing();
            bool startupUpdateWasActive = startupUpdateWorkflow.NotifyClosing();
            Task startupUpdateTerminal = startupUpdateWorkflow.WaitForTerminalAsync();
            bool preparationAlreadyStarted = IsShutdownPreparationStarted;
            if (startupUpdateWasActive
                && startupUpdateWorkflow.IsShutdownPreparationStarted
                && !preparationAlreadyStarted)
            {
                await startupUpdateWorkflow.WaitForShutdownPreparationRequestAsync().ConfigureAwait(false);
                preparationAlreadyStarted = IsShutdownPreparationStarted;
            }
            if (startupUpdateWasActive && !preparationAlreadyStarted)
            {
                await startupUpdateWorkflow.WaitForIdleAsync().ConfigureAwait(false);
                await startupUpdateTerminal.ConfigureAwait(false);
            }

            Task<ShutdownPreparationResult> preparation = EnsurePreparationAsync("window_close", updatePreparation: false);
            try
            {
                await preparation.ConfigureAwait(false);
                if (startupUpdateWasActive && preparationAlreadyStarted)
                {
                    await startupUpdateWorkflow.WaitForIdleAsync().ConfigureAwait(false);
                }
                await startupUpdateTerminal.ConfigureAwait(false);
                MarkCloseAllowed();
                completion.TrySetResult(new ShellShutdownWorkflowCompletionReceipt("window_close", preparationSucceeded: true));
            }
            catch (Exception exception)
            {
                LogWarningSafely(exception, "shell_shutdown preparation failed");
                if (startupUpdateWasActive && preparationAlreadyStarted)
                {
                    try
                    {
                        await startupUpdateWorkflow.WaitForIdleAsync().ConfigureAwait(false);
                    }
                    catch (Exception drainException)
                    {
                        LogWarningSafely(drainException, "shell_shutdown startup update drain failed");
                    }
                    try
                    {
                        await startupUpdateTerminal.ConfigureAwait(false);
                    }
                    catch (Exception drainException)
                    {
                        LogWarningSafely(drainException, "shell_shutdown startup update terminal drain failed");
                    }
                }
                else
                {
                    try
                    {
                        await startupUpdateTerminal.ConfigureAwait(false);
                    }
                    catch (Exception drainException)
                    {
                        LogWarningSafely(drainException, "shell_shutdown startup update terminal drain failed");
                    }
                }
                MarkCloseAllowed();
                completion.TrySetResult(new ShellShutdownWorkflowCompletionReceipt("window_close", preparationSucceeded: false, exception));
            }
        }
        catch (Exception exception)
        {
            LogWarningSafely(exception, "shell_shutdown close arbitration failed");
            MarkPreparationCompleted();
            MarkCloseAllowed();
            completion.TrySetResult(new ShellShutdownWorkflowCompletionReceipt("window_close", preparationSucceeded: false, exception));
        }
    }

    private Task<ShutdownPreparationResult> EnsurePreparationAsync(string reason, bool updatePreparation)
    {
        TaskCompletionSource<ShutdownPreparationResult> completion;
        string normalizedReason = reason ?? (updatePreparation ? "update" : "window_close");
        lock (syncRoot)
        {
            if (preparationTask != null)
            {
                return preparationTask;
            }

            preparationStarted = true;
            preparationRunning = true;
            closingOrClosed = true;
            preparationWasUpdate = updatePreparation;
            completion = new TaskCompletionSource<ShutdownPreparationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            preparationTask = completion.Task;
        }

        try
        {
            Task dispatchTask = dispatchToUi(() => RunPreparationAsync(completion, normalizedReason, updatePreparation));
            if (dispatchTask == null)
            {
                throw new InvalidOperationException("Shutdown preparation UI dispatch returned no task.");
            }
            _ = ObservePreparationDispatchAsync(dispatchTask, completion, updatePreparation);
        }
        catch (Exception exception)
        {
            CompletePreparationFailure(completion, exception, updatePreparation);
        }
        return completion.Task;
    }

    private async Task ObservePreparationDispatchAsync(
        Task dispatchTask,
        TaskCompletionSource<ShutdownPreparationResult> completion,
        bool updatePreparation)
    {
        try
        {
            await dispatchTask.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            CompletePreparationFailure(completion, exception, updatePreparation);
        }
    }

    private async Task RunPreparationAsync(
        TaskCompletionSource<ShutdownPreparationResult> completion,
        string reason,
        bool updatePreparation)
    {
        try
        {
            markCoordinatedShutdownStarted(reason);
            ShutdownPreparationResult result = await PrepareShutdownCoreAsync(reason).ConfigureAwait(false);
            await StopPerformanceDiagnosticsSafelyAsync().ConfigureAwait(false);
            MarkPreparationCompleted();
            completion.TrySetResult(result);
        }
        catch (Exception exception)
        {
            CompletePreparationFailure(completion, exception, updatePreparation);
        }
    }

    private async Task StopPerformanceDiagnosticsSafelyAsync()
    {
        try
        {
            Task stopTask = stopPerformanceDiagnostics();
            if (stopTask == null)
            {
                throw new InvalidOperationException("Performance diagnostics stop returned no task.");
            }

            if (await Task.WhenAny(
                    stopTask,
                    Task.Delay(PerformanceDiagnosticsDrainLimit)).ConfigureAwait(false) != stopTask)
            {
                logShutdownWarning("performance_diagnostics_stop_timed_out");
                return;
            }

            await stopTask.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LogWarningSafely(exception, "performance_diagnostics_stop_failed");
        }
    }

    private void CompletePreparationFailure(
        TaskCompletionSource<ShutdownPreparationResult> completion,
        Exception exception,
        bool updatePreparation)
    {
        Task failureDrain = StartFailureDrain();
        _ = CompletePreparationFailureAfterDrainAsync(completion, exception, updatePreparation, failureDrain);
    }

    private async Task CompletePreparationFailureAfterDrainAsync(
        TaskCompletionSource<ShutdownPreparationResult> completion,
        Exception exception,
        bool updatePreparation,
        Task failureDrain)
    {
        try
        {
            await failureDrain.ConfigureAwait(false);
        }
        catch (Exception shutdownException)
        {
            LogWarningSafely(shutdownException, "shell_shutdown cancellation fallback failed");
        }
        await StopPerformanceDiagnosticsSafelyAsync().ConfigureAwait(false);
        lock (syncRoot)
        {
            preparationRunning = false;
            preparationCompleted = true;
            if (updatePreparation)
            {
                updatePreparationFailurePending = true;
            }
        }
        completion.TrySetException(exception);
    }

    private Task StartFailureDrain()
    {
        lock (syncRoot)
        {
            if (failureDrainTask == null)
            {
                failureDrainTask = DrainAfterPreparationFailureAsync();
            }
            return failureDrainTask;
        }
    }

    private async Task DrainAfterPreparationFailureAsync()
    {
        Task regularChartListStop = BeginShutdownRequested("preparation_failure");
        var stopwatch = Stopwatch.StartNew();
        int sqliteCloseFailureBaseline = ShutdownOperationTracker.SqliteCloseFailureCount;
        await CollectShutdownPreparationResultAsync(
            "preparation_failure",
            stopwatch,
            sqliteCloseFailureBaseline,
            regularChartListStop).ConfigureAwait(false);
    }

    private void MarkPreparationCompleted()
    {
        lock (syncRoot)
        {
            preparationRunning = false;
            preparationCompleted = true;
        }
    }

    private async Task AllowCloseAfterStartupUpdateTerminalAsync(Task<ShutdownPreparationResult> preparation)
    {
        await startupUpdateWorkflow.WaitForTerminalAsync().ConfigureAwait(false);
        try
        {
            await preparation.ConfigureAwait(false);
        }
        catch
        {
        }
        MarkCloseAllowed();
    }

    private void MarkCloseAllowed()
    {
        lock (syncRoot)
        {
            closeAllowed = true;
        }
    }

    private async Task<ShutdownPreparationResult> PrepareShutdownCoreAsync(string reason)
    {
        Task regularChartListStop = BeginShutdownRequested(reason);
        var stopwatch = Stopwatch.StartNew();
        logShutdown("prepare_start reason=" + formatTextForLog(reason));
        int sqliteCloseFailureBaseline = ShutdownOperationTracker.SqliteCloseFailureCount;
        TryShutdownStep("set_ui_blocked", () => startupProgressWorkflowOwner.SetStartupUiInteractionBlocked(true));

        ShutdownPreparationResult result = await CollectShutdownPreparationResultAsync(
            reason,
            stopwatch,
            sqliteCloseFailureBaseline,
            regularChartListStop).ConfigureAwait(false);
        logShutdown("prepare_done " + result.ToLogFields());
        return result;
    }

    private async Task<ShutdownPreparationResult> CollectShutdownPreparationResultAsync(
        string reason,
        Stopwatch stopwatch = null,
        int? sqliteCloseFailureBaseline = null,
        Task regularChartListStopTask = null)
    {
        stopwatch ??= Stopwatch.StartNew();
        sqliteCloseFailureBaseline ??= ShutdownOperationTracker.SqliteCloseFailureCount;
        var waitTracker = new ShutdownWaitTracker();
        await WaitForTaskCompletionAsync(
            "regularChartListWarmup",
            regularChartListStopTask,
            ShutdownDrainWarningThreshold,
            waitTracker,
            () => "running=" + FormatBool(regularChartListOwner.IsVirtualOrderPrewarmRunning)).ConfigureAwait(false);
        await (regularChartListStopTask ?? Task.CompletedTask).ConfigureAwait(false);
        await WaitForDropInstallQueueIdleAsync(waitTracker).ConfigureAwait(false);
        await WaitForMaintenanceRescanIdleAsync(waitTracker).ConfigureAwait(false);
        await WaitForFolderAutoRenameIdleAsync(waitTracker).ConfigureAwait(false);
        await WaitForPlaylistBuildIdleAsync(waitTracker).ConfigureAwait(false);
        await WaitForPlaylistSummaryDataBuildIdleAsync(waitTracker).ConfigureAwait(false);
        await WaitForStartupBackgroundTasksIdleAsync(waitTracker).ConfigureAwait(false);
        Task playlistLibraryIndexPrewarmTask = playlistWorkspace.GetPlaylistLibraryIndexPrewarmTask();
        await WaitForTaskCompletionAsync(
            "playlistLibraryIndexPrewarm",
            playlistLibraryIndexPrewarmTask,
            ShutdownQueueDrainWarningThreshold,
            waitTracker,
            () => "completed=" + FormatBool(playlistLibraryIndexPrewarmTask == null || playlistLibraryIndexPrewarmTask.IsCompleted)).ConfigureAwait(false);
        await WaitForPlayHistoryRefreshIdleAsync(waitTracker).ConfigureAwait(false);
        await WaitForMainOperationIdleAsync(waitTracker).ConfigureAwait(false);
        await WaitForLibraryShutdownBlockingWorkAsync(waitTracker).ConfigureAwait(false);
        await WaitForPlaylistShutdownBlockingWorkAsync(waitTracker).ConfigureAwait(false);
        await WaitForDeferredPlaylistWorkersIdleAsync(waitTracker).ConfigureAwait(false);
        await WaitForPlaylistReloadCleanupIdleAsync(waitTracker).ConfigureAwait(false);
        await WaitForLr2DbProcessLocksAsync(waitTracker).ConfigureAwait(false);
        await WaitForSqliteConnectionsIdleAsync(waitTracker).ConfigureAwait(false);
        int sqliteCloseFailureCount = Math.Max(0, ShutdownOperationTracker.SqliteCloseFailureCount - sqliteCloseFailureBaseline.Value);
        stopwatch.Stop();
        return new ShutdownPreparationResult(
            reason,
            stopwatch.ElapsedMilliseconds,
            waitTracker.SlowWaitLogged,
            sqliteCloseFailureCount);
    }

    private Task BeginShutdownRequested(string reason)
    {
        if (Interlocked.CompareExchange(ref shutdownRequested, 1, 0) != 0)
        {
            return shutdownStartCompletion.Task.Unwrap();
        }
        TryShutdownStep("playlist_index_shutdown", playlistWorkspace.MarkPlaylistLibraryIndexShutdownRequested);
        Task regularChartListStop = Task.CompletedTask;
        try
        {
            regularChartListStop = regularChartListOwner.StopAsync() ?? Task.CompletedTask;
        }
        catch (Exception exception)
        {
            logShutdown("regularChartListStop_failed message=" + exception.Message);
        }
        RequestShutdownCancellation(reason ?? "shutdown");
        shutdownStartCompletion.TrySetResult(regularChartListStop);
        return regularChartListStop;
    }

    private void RequestShutdownCancellation(string reason)
    {
        TryShutdownStep("library", () =>
        {
            if (Volatile.Read(ref libraryAttached))
            {
                files.RequestShutdown(reason);
            }
        });
        TryShutdownStep("playlist", () =>
        {
            if (Volatile.Read(ref playlistAttached))
            {
                tables.RequestShutdown(reason);
            }
        });
        TryShutdownStep("playlist_build", playlistWorkspace.CancelDetailBuilds);
        TryShutdownStep("playlist_summary", playlistWorkspace.StopPlaylistSummaryDataBuild);
        TryShutdownStep("external_table_catalog", playlistWorkspace.CancelExternalTableCollectionLoadForShutdown);
        TryShutdownStep("play_history", () =>
        {
            playHistoryWorkflowOwner.Deactivate();
            playHistoryWorkflowOwner.ClearQueuedRefreshes();
            playHistoryWorkflowOwner.CancelDisplayTargetCatalogRefreshesForShutdown();
        });
        TryShutdownStep("playlist_index_prewarm", playlistWorkspace.CancelPlaylistLibraryIndexPrewarmForShutdown);
        TryShutdownStep("playlist_reload_cleanup", playlistWorkspace.CancelPlaylistReloadCleanupForShutdown);
        TryShutdownStep("maintenance_rescan", maintenanceRescanWorkflow.RequestShutdown);
        TryShutdownStep("folder_auto_rename", folderAutoRenameWorkflow.RequestShutdown);
        TryShutdownStep("package_install", packageInstallWorkflow.RequestShutdown);
        TryShutdownStep("startup_background_queue", () => startupBackgroundTaskScheduler.RequestShutdown(reason));
    }

    private sealed class ShutdownWaitTracker
    {
        private int slowWaitLogged;

        internal bool SlowWaitLogged => Volatile.Read(ref slowWaitLogged) != 0;

        internal void MarkSlowWaitLogged()
        {
            Interlocked.Exchange(ref slowWaitLogged, 1);
        }
    }

    private async Task WaitForDropInstallQueueIdleAsync(ShutdownWaitTracker tracker)
    {
        await WaitForConditionAsync(
            "dropInstallQueue",
            () => packageInstallWorkflow.IsIdle,
            ShutdownQueueDrainWarningThreshold,
            tracker,
            () => "idle=" + FormatBool(packageInstallWorkflow.IsIdle)).ConfigureAwait(false);
    }

    private async Task WaitForMaintenanceRescanIdleAsync(ShutdownWaitTracker tracker)
    {
        await WaitForConditionAsync(
            "maintenanceRescan",
            () => maintenanceRescanWorkflow.IsIdle,
            ShutdownQueueDrainWarningThreshold,
            tracker,
            () => "idle=" + FormatBool(maintenanceRescanWorkflow.IsIdle)).ConfigureAwait(false);
    }

    private async Task WaitForFolderAutoRenameIdleAsync(ShutdownWaitTracker tracker)
    {
        await WaitForConditionAsync(
            "folderAutoRename",
            () => folderAutoRenameWorkflow.IsIdle,
            ShutdownQueueDrainWarningThreshold,
            tracker,
            () => "idle=" + FormatBool(folderAutoRenameWorkflow.IsIdle)).ConfigureAwait(false);
    }

    private async Task WaitForPlaylistBuildIdleAsync(ShutdownWaitTracker tracker)
    {
        Task detailBuildIdle = playlistWorkspace.WaitForDetailBuildIdleAsync();
        await WaitForTaskCompletionAsync(
            "playlistBuild",
            detailBuildIdle,
            ShutdownQueueDrainWarningThreshold,
            tracker,
            () => playlistWorkspace.DescribeDetailBuildState(FormatBool)).ConfigureAwait(false);
        await detailBuildIdle.ConfigureAwait(false);
    }

    private async Task WaitForPlaylistSummaryDataBuildIdleAsync(ShutdownWaitTracker tracker)
    {
        Task summaryBuildIdle = playlistWorkspace.WaitForPlaylistSummaryDataBuildIdleAsync();
        await WaitForTaskCompletionAsync(
            "playlistSummaryDataBuild",
            summaryBuildIdle,
            ShutdownQueueDrainWarningThreshold,
            tracker,
            () => "idle=" + FormatBool(playlistWorkspace.IsPlaylistSummaryDataBuildIdle)).ConfigureAwait(false);
        await summaryBuildIdle.ConfigureAwait(false);
    }

    private async Task WaitForStartupBackgroundTasksIdleAsync(ShutdownWaitTracker tracker)
    {
        await WaitForConditionAsync(
            "startupBackgroundTasks",
            () => startupBackgroundTaskScheduler.IsFullyIdle,
            ShutdownDrainWarningThreshold,
            tracker,
            startupBackgroundTaskScheduler.DescribeWaitState).ConfigureAwait(false);
    }

    private async Task WaitForPlayHistoryRefreshIdleAsync(ShutdownWaitTracker tracker)
    {
        Task refreshQueuesIdle = playHistoryWorkflowOwner.WaitForRefreshQueuesIdleAsync();
        await WaitForTaskCompletionAsync(
            "playHistoryRefresh",
            refreshQueuesIdle,
            ShutdownQueueDrainWarningThreshold,
            tracker,
            () => playHistoryWorkflowOwner.DescribeRefreshQueues()
                + " " + playHistoryWorkflowOwner.DescribeDisplayTargetCatalogRefresh()).ConfigureAwait(false);
        await refreshQueuesIdle.ConfigureAwait(false);
        await WaitForConditionAsync(
            "playHistoryRefresh",
            () => playHistoryWorkflowOwner.IsDisplayTargetCatalogRefreshIdle,
            ShutdownQueueDrainWarningThreshold,
            tracker,
            () => playHistoryWorkflowOwner.DescribeRefreshQueues()
                + " " + playHistoryWorkflowOwner.DescribeDisplayTargetCatalogRefresh()).ConfigureAwait(false);
    }

    private async Task WaitForDeferredPlaylistWorkersIdleAsync(ShutdownWaitTracker tracker)
    {
        await WaitForConditionAsync(
            "deferredPlaylistWorkers",
            () => playlistReferenceApplyWorkflow.IsIdle
                && playlistWorkspace.IsDeferredExternalPlaylistSyncIdle,
            ShutdownQueueDrainWarningThreshold,
            tracker,
            () => playlistReferenceApplyWorkflow.DescribeWaitState()
                + " " + playlistWorkspace.DescribeDeferredExternalPlaylistSyncWaitState()).ConfigureAwait(false);
    }

    private async Task WaitForPlaylistReloadCleanupIdleAsync(ShutdownWaitTracker tracker)
    {
        Task reloadCleanupIdle = playlistWorkspace.WaitForPlaylistReloadCleanupIdleAsync();
        await WaitForTaskCompletionAsync(
            "playlistReloadCleanup",
            reloadCleanupIdle,
            ShutdownQueueDrainWarningThreshold,
            tracker,
            playlistWorkspace.DescribePlaylistReloadCleanupWaitState).ConfigureAwait(false);
        await reloadCleanupIdle.ConfigureAwait(false);
    }

    private async Task WaitForLibraryShutdownBlockingWorkAsync(ShutdownWaitTracker tracker)
    {
        await WaitForConditionAsync(
            "libraryShutdownWork",
            () => !Volatile.Read(ref libraryAttached) || !files.HasShutdownBlockingWork,
            ShutdownDrainWarningThreshold,
            tracker,
            () => !Volatile.Read(ref libraryAttached) ? "library=unattached" : files.GetShutdownBlockingWorkLogFields()).ConfigureAwait(false);
    }

    private async Task WaitForPlaylistShutdownBlockingWorkAsync(ShutdownWaitTracker tracker)
    {
        await WaitForConditionAsync(
            "playlistShutdownWork",
            () => !Volatile.Read(ref playlistAttached) || !tables.HasShutdownBlockingWork,
            ShutdownDrainWarningThreshold,
            tracker,
            () => !Volatile.Read(ref playlistAttached) ? "playlist=unattached" : tables.GetShutdownBlockingWorkLogFields()).ConfigureAwait(false);
    }

    private async Task WaitForSqliteConnectionsIdleAsync(ShutdownWaitTracker tracker)
    {
        await WaitForConditionAsync(
            "sqliteConnections",
            () => ShutdownOperationTracker.ActiveSqliteConnectionCount == 0,
            ShutdownQueueDrainWarningThreshold,
            tracker,
            () => "activeSqliteConnectionCount=" + ShutdownOperationTracker.ActiveSqliteConnectionCount).ConfigureAwait(false);
    }

    private async Task WaitForTaskCompletionAsync(
        string target,
        Task task,
        TimeSpan warningThreshold,
        ShutdownWaitTracker tracker,
        Func<string> describeState)
    {
        if (task == null || task.IsCompleted)
        {
            return;
        }
        var stopwatch = Stopwatch.StartNew();
        bool warningLogged = false;
        Task warningDelay = Task.Delay(warningThreshold);
        if (await Task.WhenAny(task, warningDelay).ConfigureAwait(false) == warningDelay)
        {
            LogSlowWaitIfNeeded(target, stopwatch, warningThreshold, tracker, ref warningLogged, describeState);
            await Task.WhenAny(task).ConfigureAwait(false);
        }
    }

    private async Task WaitForConditionAsync(
        string target,
        Func<bool> isIdle,
        TimeSpan warningThreshold,
        ShutdownWaitTracker tracker,
        Func<string> describeState)
    {
        if (isIdle())
        {
            return;
        }
        var stopwatch = Stopwatch.StartNew();
        bool warningLogged = false;
        while (true)
        {
            await Task.Delay(100).ConfigureAwait(false);
            if (isIdle())
            {
                return;
            }
            LogSlowWaitIfNeeded(target, stopwatch, warningThreshold, tracker, ref warningLogged, describeState);
        }
    }

    private async Task WaitForMainOperationIdleAsync(ShutdownWaitTracker tracker)
    {
        Task waitTask = mainOperationSemaphore.WaitAsync();
        await WaitForTaskCompletionAsync(
            "mainOperationSemaphore",
            waitTask,
            ShutdownDrainWarningThreshold,
            tracker,
            () => "semaphoreAvailable=false").ConfigureAwait(false);
        await waitTask.ConfigureAwait(false);
        mainOperationSemaphore.Release();
    }

    private async Task WaitForLr2DbProcessLocksAsync(ShutdownWaitTracker tracker)
    {
        await Task.Run(() =>
        {
            var stopwatch = Stopwatch.StartNew();
            bool warningLogged = false;
            while (true)
            {
                bool songLockTaken = false;
                bool scoreLockTaken = false;
                try
                {
                    songLockTaken = LR2SongDBExtended.Lock(TimeSpan.FromMilliseconds(100));
                    if (songLockTaken)
                    {
                        scoreLockTaken = LR2ScoreDBExtended.Lock(TimeSpan.FromMilliseconds(100));
                    }
                    if (songLockTaken && scoreLockTaken)
                    {
                        return;
                    }
                }
                finally
                {
                    if (scoreLockTaken)
                    {
                        LR2ScoreDBExtended.Unlock();
                    }
                    if (songLockTaken)
                    {
                        LR2SongDBExtended.Unlock();
                    }
                }
                LogSlowWaitIfNeeded(
                    "lr2DbProcessLocks",
                    stopwatch,
                    ShutdownDrainWarningThreshold,
                    tracker,
                    ref warningLogged,
                    () => "songLockTaken=" + FormatBool(songLockTaken) + " scoreLockTaken=" + FormatBool(scoreLockTaken));
                Thread.Sleep(100);
            }
        }).ConfigureAwait(false);
    }

    private void LogSlowWaitIfNeeded(
        string target,
        Stopwatch stopwatch,
        TimeSpan warningThreshold,
        ShutdownWaitTracker tracker,
        ref bool warningLogged,
        Func<string> describeState)
    {
        if (warningLogged || stopwatch.Elapsed < warningThreshold)
        {
            return;
        }
        warningLogged = true;
        tracker?.MarkSlowWaitLogged();
        string state = string.Empty;
        if (describeState != null)
        {
            try
            {
                state = describeState();
            }
            catch (Exception exception)
            {
                state = "stateFailed=" + exception.GetType().Name;
            }
        }
        logShutdownWarning("wait_slow target=" + formatTextForLog(target)
            + " elapsedMs=" + stopwatch.ElapsedMilliseconds
            + (string.IsNullOrWhiteSpace(state) ? string.Empty : " " + state));
    }

    private void TryShutdownStep(string name, Action action)
    {
        try
        {
            action?.Invoke();
        }
        catch (Exception exception)
        {
            logShutdown((name ?? "step") + "_failed message=" + exception.Message);
        }
    }

    private void LogWarningSafely(Exception exception, string context)
    {
        try
        {
            logShutdownWarning(context + " message=" + exception.Message);
        }
        catch
        {
        }
    }

    private static string FormatBool(bool value)
    {
        return value.ToString().ToLowerInvariant();
    }
}
