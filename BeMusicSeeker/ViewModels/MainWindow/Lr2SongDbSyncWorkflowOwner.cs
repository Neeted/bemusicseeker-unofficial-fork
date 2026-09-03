using System;
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

/// <summary>
/// Describes the outcome of a typed LR2 song DB synchronization queue request.
/// </summary>
internal enum Lr2SongDbSyncQueueStatus
{
    /// <summary>
    /// The synchronization request was accepted by the LR2 runtime.
    /// </summary>
    Queued,

    /// <summary>
    /// The normal no-op path was selected because LR2 mode or the library was unavailable.
    /// </summary>
    SkippedUnavailable
}

/// <summary>
/// Immutable result for one LR2 queue handoff after a file-diff reload.
/// </summary>
internal sealed class Lr2SongDbSyncQueueResult
{
    /// <summary>
    /// Initializes a queue result with the exact file-diff request and disposition.
    /// </summary>
    /// <param name="request">The request whose reason and operation token were queued.</param>
    /// <param name="status">The queue disposition.</param>
    internal Lr2SongDbSyncQueueResult(
        FileDiffReloadRequest request,
        Lr2SongDbSyncQueueStatus status)
    {
        Request = request ?? throw new ArgumentNullException(nameof(request));
        Status = status;
    }

    /// <summary>
    /// Gets the exact file-diff request associated with this result.
    /// </summary>
    internal FileDiffReloadRequest Request { get; }

    /// <summary>
    /// Gets the queue disposition.
    /// </summary>
    internal Lr2SongDbSyncQueueStatus Status { get; }

    /// <summary>
    /// Gets whether the request was submitted to the LR2 runtime.
    /// </summary>
    internal bool WasQueued => Status == Lr2SongDbSyncQueueStatus.Queued;

    /// <summary>
    /// Gets whether the request was explicitly skipped because its runtime was unavailable.
    /// </summary>
    internal bool WasSkippedUnavailable =>
        Status == Lr2SongDbSyncQueueStatus.SkippedUnavailable;
}

internal interface ILr2SongDbSyncWorkflowRuntime
{
    bool IsLr2ModeEnabled { get; }

    bool IsLibraryAvailable { get; }

    void Queue(
        string reason,
        bool force,
        bool prepareGeneratedData = false,
        bool allowIncompleteToQueue = true);

    bool TryRunDataPreparation(
        string reason,
        bool includeBuiltinGeneratedData = false,
        Action queueAfterPreparation = null);

    void SyncExternalFolderRowsForCustomFolderOutputBaseChange(string reason);

    Lr2StartupScanBlockerCleanupResult CleanupStartupScanBlockerFolderRows(string reason);
}

internal sealed class BmsLr2SongDbSyncWorkflowRuntime : ILr2SongDbSyncWorkflowRuntime
{
    private readonly Func<BMSLibrary> libraryProvider;

    private readonly Func<BMSPlaylist> playlistProvider;

    private readonly Func<bool> lr2ModeProvider;

    internal BmsLr2SongDbSyncWorkflowRuntime(
        Func<BMSLibrary> libraryProvider,
        Func<BMSPlaylist> playlistProvider,
        Func<bool> lr2ModeProvider)
    {
        this.libraryProvider = libraryProvider ?? throw new ArgumentNullException(nameof(libraryProvider));
        this.playlistProvider = playlistProvider ?? throw new ArgumentNullException(nameof(playlistProvider));
        this.lr2ModeProvider = lr2ModeProvider ?? throw new ArgumentNullException(nameof(lr2ModeProvider));
    }

    public bool IsLr2ModeEnabled => lr2ModeProvider();

    public bool IsLibraryAvailable => libraryProvider() != null;

    public void Queue(
        string reason,
        bool force,
        bool prepareGeneratedData = false,
        bool allowIncompleteToQueue = true)
    {
        BMSLibrary library = libraryProvider();
        if (library == null)
        {
            return;
        }

        Func<LibraryFileMutationLease, Lr2SongDbSyncPreparedDataSurface> prepareWithLease = null;
        if (prepareGeneratedData)
        {
            prepareWithLease = preparationLease =>
                PrepareGeneratedDataUnderLease(
                    library,
                    reason,
                    preparationLease,
                    includeBuiltinGeneratedData: false);
        }
        library.QueueLr2SongDbSync(reason, force, prepareWithLease, allowIncompleteToQueue);
    }

    public bool TryRunDataPreparation(
        string reason,
        bool includeBuiltinGeneratedData = false,
        Action queueAfterPreparation = null)
    {
        BMSLibrary library = libraryProvider();
        if (library == null)
        {
            return false;
        }

        Func<LibraryFileMutationLease, Lr2SongDbSyncPreparedDataSurface> prepareWithLease =
            preparationLease => PrepareGeneratedDataUnderLease(
                library,
                reason,
                preparationLease,
                includeBuiltinGeneratedData);
        return library.TryRunLr2SongDbSyncDataPreparation(reason, prepareWithLease, queueAfterPreparation);
    }

    private Lr2SongDbSyncPreparedDataSurface PrepareGeneratedDataUnderLease(
        BMSLibrary library,
        string reason,
        LibraryFileMutationLease preparationLease,
        bool includeBuiltinGeneratedData)
    {
        ArgumentNullException.ThrowIfNull(preparationLease);
        using LibraryFileMutationCapability mutationCapability = preparationLease.CreateMutationCapability();
        mutationCapability.Validate(library.Lr2Synchronization);

        BMSPlaylist playlists = playlistProvider()
            ?? throw new InvalidOperationException("LR2 playlist preparation is unavailable.");

        Lr2SongDbSyncPreparedDataSurface playlistSurface =
            playlists.ReOutputAllCustomFoldersForLr2SongDbSyncUnderExistingReservation(
            reason,
            mutationCapability,
            (processed, total, tableName) => library.PublishLr2SongDbSyncExternalStageProgress(
                "playlist_materialization",
                processed,
                total,
                tableName));

        if (!includeBuiltinGeneratedData)
        {
            return playlistSurface;
        }

        return Lr2SongDbSyncPreparedDataSurface.Merge(
            playlistSurface,
            library.Lr2Synchronization.SyncLr2BuiltinCustomFolderRows(
                reason,
                mutationCapability));
    }

    public void SyncExternalFolderRowsForCustomFolderOutputBaseChange(string reason)
    {
        libraryProvider()?.Lr2Synchronization.SyncExternalLr2FolderRowsForCustomFolderOutputBaseChange(reason);
    }

    public Lr2StartupScanBlockerCleanupResult CleanupStartupScanBlockerFolderRows(string reason)
    {
        return libraryProvider()?.CleanupLr2SongDbSyncStartupScanBlockerFolderRows(reason);
    }
}

internal sealed class Lr2SongDbSyncWorkflowOwner
{
    private readonly ILr2SongDbSyncWorkflowRuntime runtime;

    private readonly IUiDialogService dialogs;

    private readonly Func<Action, Task> backgroundScheduler;

    private readonly Action<Task, string> taskLogger;

    /// <summary>
    /// Reports that the LR2 sync owner received a status-bar retry request.
    /// </summary>
    internal event Action StatusBarRetryRequested;

    /// <summary>
    /// Reports that the LR2 sync owner received a startup-blocker cleanup request.
    /// </summary>
    internal event Action StartupScanBlockerCleanupRequested;

    internal Lr2SongDbSyncWorkflowOwner(
        ILr2SongDbSyncWorkflowRuntime runtime,
        IUiDialogService dialogs,
        Func<Action, Task> backgroundScheduler = null,
        Action<Task, string> taskLogger = null)
    {
        this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        this.dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        this.backgroundScheduler = backgroundScheduler ?? (action => Task.Run(action));
        this.taskLogger = taskLogger ?? new Action<Task, string>((task, routeName) => task.Logging(routeName));
    }

    internal void RequestStatusBarRetry()
    {
        NotifyStatusBarActionReceived(StatusBarRetryRequested, "Lr2StatusBarRetryRequestNotification");
        if (!CanRun())
        {
            return;
        }

        ScheduleBackground(
            "RequestLr2SongDbSync",
            () => QueueCore("status_bar_retry", force: false));
    }

    internal async Task RequestManualResyncAsync()
    {
        if (!CanRun())
        {
            return;
        }

        const string reason = "setting_dialog_manual_resync";
        await Task.Run(() => QueueCore(reason, force: true)).ConfigureAwait(false);
    }

    /// <summary>
    /// Queues the LR2 synchronization associated with a completed file-diff reload.
    /// Unavailable LR2 mode is an explicit no-op result; runtime exceptions remain failures.
    /// </summary>
    /// <param name="request">The immutable file-diff request to propagate.</param>
    /// <returns>The exact request and queue disposition.</returns>
    internal Lr2SongDbSyncQueueResult QueueAfterReloadFileDiff(FileDiffReloadRequest request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        if (!CanRun())
        {
            return new Lr2SongDbSyncQueueResult(
                request,
                Lr2SongDbSyncQueueStatus.SkippedUnavailable);
        }

        QueueCore(request.Reason, force: false);
        return new Lr2SongDbSyncQueueResult(
            request,
            Lr2SongDbSyncQueueStatus.Queued);
    }

    internal void SchedulePostStartupSync(
        string reason,
        Action queued = null,
        Func<Action, Task> scheduler = null)
    {
        if (!CanRun())
        {
            queued?.Invoke();
            return;
        }

        string fullGenerationReason = "post_startup_" + (reason ?? string.Empty);
        ScheduleBackground(
            "PostStartupLr2SongDbSync",
            () =>
            {
                ExceptionDispatchInfo failure = null;
                try
                {
                    QueueCore(fullGenerationReason, force: false);
                }
                catch (Exception exception)
                {
                    failure = ExceptionDispatchInfo.Capture(exception);
                }
                try
                {
                    queued?.Invoke();
                }
                finally
                {
                    failure?.Throw();
                }
            },
            scheduler);
    }

    internal void SyncFolderDataAfterSettingsChange(string reason)
    {
        if (!CanRun())
        {
            return;
        }

        ScheduleBackground(
            "SyncLr2SongDbSyncFolderDataAfterSettingsChange",
            () =>
            {
                runtime.TryRunDataPreparation(
                    reason,
                    includeBuiltinGeneratedData: true,
                    () => runtime.Queue(reason, force: false, allowIncompleteToQueue: false));
            });
    }

    internal void SyncExternalFolderRowsAfterCustomFolderOutputBaseSettingsChange(string reason)
    {
        if (!CanRun())
        {
            return;
        }

        ScheduleBackground(
            "SyncExternalLr2FolderRowsAfterCustomFolderOutputBaseSettingsChange",
            () => runtime.SyncExternalFolderRowsForCustomFolderOutputBaseChange(reason));
    }

    internal void CleanupStartupScanBlockersAndRetry()
    {
        NotifyStatusBarActionReceived(
            StartupScanBlockerCleanupRequested,
            "Lr2StatusBarStartupBlockerCleanupRequestNotification");
        if (!CanRun())
        {
            return;
        }

        UiDialogResult confirmation = dialogs.ConfirmAsync(new UiConfirmationRequest(
            BeMusicSeeker.Properties.Resources.Msg_confirm_lr2_song_db_sync_startup_scan_blocker_cleanup,
            BeMusicSeeker.Properties.Resources.Warning,
            MessageBoxButton.OKCancel,
            MessageBoxImage.Exclamation,
            MessageBoxResult.Cancel)).GetAwaiter().GetResult();
        if (!ToConfirmationDecision(confirmation, "LR2 song DB sync startup blocker cleanup confirmation"))
        {
            return;
        }

        try
        {
            runtime.CleanupStartupScanBlockerFolderRows("status_bar_cleanup");
            ScheduleBackground(
                "Lr2SongDbSyncStartupScanBlockerCleanupRetry",
                () => QueueCore("status_bar_cleanup_retry", force: false));
        }
        catch (Exception ex)
        {
            ShowCleanupFailure(ex);
        }
    }

    private void QueueCore(string reason, bool force)
    {
        runtime.Queue(
            reason,
            force,
            prepareGeneratedData: true);
    }

    private bool CanRun()
    {
        return runtime.IsLr2ModeEnabled && runtime.IsLibraryAvailable;
    }

    private void ScheduleBackground(
        string routeName,
        Action work,
        Func<Action, Task> scheduler = null)
    {
        Task task = (scheduler ?? backgroundScheduler)(work);
        taskLogger(task, routeName);
    }

    private void NotifyStatusBarActionReceived(Action notification, string routeName)
    {
        try
        {
            notification?.Invoke();
        }
        catch (Exception exception)
        {
            try
            {
                taskLogger(Task.FromException(exception), routeName);
            }
            catch
            {
            }
        }
    }

    private void ShowCleanupFailure(Exception exception)
    {
        UiDialogResult result = dialogs.ShowMessageAsync(new UiMessageRequest(
            BeMusicSeeker.Properties.Resources.Msg_error_unexpected + Environment.NewLine + exception.Message,
            BeMusicSeeker.Properties.Resources.Error,
            MessageBoxButton.OK,
            MessageBoxImage.Hand,
            MessageBoxResult.OK)).GetAwaiter().GetResult();
        ThrowIfDialogNotShown(result, "LR2 song DB sync startup blocker cleanup failure notification");
    }

    private static bool ToConfirmationDecision(UiDialogResult result, string routeName)
    {
        if (result == null)
        {
            throw new InvalidOperationException(routeName + " returned no dialog result.");
        }

        return result.Status switch
        {
            UiDialogStatus.Accepted => true,
            UiDialogStatus.Rejected or UiDialogStatus.CancelledByUser => false,
            UiDialogStatus.ClosedByUser => result.IsPositive,
            _ => throw CreateDialogFailure(routeName, result)
        };
    }

    private static void ThrowIfDialogNotShown(UiDialogResult result, string routeName)
    {
        if (result?.Status is UiDialogStatus.Accepted or UiDialogStatus.CancelledByUser or UiDialogStatus.ClosedByUser)
        {
            return;
        }

        throw CreateDialogFailure(routeName, result);
    }

    private static InvalidOperationException CreateDialogFailure(string routeName, UiDialogResult result)
    {
        string suffix = result == null ? " returned no dialog result." : " was not shown: " + result.Status;
        return new InvalidOperationException(routeName + suffix, result?.Exception);
    }
}
