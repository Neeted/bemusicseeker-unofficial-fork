using System;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Views.Dialogs;

namespace BeMusicSeeker.ViewModels;

internal interface ILr2SongDbSyncWorkflowRuntime
{
    bool IsLr2ModeEnabled { get; }

    bool IsLibraryAvailable { get; }

    void Queue(
        string reason,
        bool force,
        Func<Lr2SongDbSyncPreparedDataSurface> prepareGeneratedData = null,
        bool allowIncompleteToQueue = true);

    bool TryRunDataPreparation(
        string reason,
        Func<Lr2SongDbSyncPreparedDataSurface> prepareGeneratedData,
        Action queueAfterPreparation = null);

    Lr2SongDbSyncPreparedDataSurface PreparePlaylistGeneratedData(string reason);

    Lr2SongDbSyncPreparedDataSurface PrepareBuiltinGeneratedData(string reason);

    void SyncExternalFolderRowsForCustomFolderOutputBaseChange(string reason);

    bool Cancel(string reason);

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
        Func<Lr2SongDbSyncPreparedDataSurface> prepareGeneratedData = null,
        bool allowIncompleteToQueue = true)
    {
        libraryProvider()?.QueueLr2SongDbSync(reason, force, prepareGeneratedData, allowIncompleteToQueue);
    }

    public bool TryRunDataPreparation(
        string reason,
        Func<Lr2SongDbSyncPreparedDataSurface> prepareGeneratedData,
        Action queueAfterPreparation = null)
    {
        return libraryProvider()?.TryRunLr2SongDbSyncDataPreparation(reason, prepareGeneratedData, queueAfterPreparation) == true;
    }

    public Lr2SongDbSyncPreparedDataSurface PreparePlaylistGeneratedData(string reason)
    {
        BMSPlaylist playlists = playlistProvider();
        if (playlists == null)
        {
            return Lr2SongDbSyncPreparedDataSurface.Empty;
        }

        return playlists.ReOutputAllCustomFoldersForLr2SongDbSync(
            reason,
            (processed, total, tableName) => libraryProvider()?.PublishLr2SongDbSyncExternalStageProgress(
                "playlist_materialization",
                processed,
                total,
                tableName));
    }

    public Lr2SongDbSyncPreparedDataSurface PrepareBuiltinGeneratedData(string reason)
    {
        return libraryProvider()?.Lr2Synchronization.SyncLr2BuiltinCustomFolderRows(reason)
            ?? Lr2SongDbSyncPreparedDataSurface.Empty;
    }

    public void SyncExternalFolderRowsForCustomFolderOutputBaseChange(string reason)
    {
        libraryProvider()?.Lr2Synchronization.SyncExternalLr2FolderRowsForCustomFolderOutputBaseChange(reason);
    }

    public bool Cancel(string reason)
    {
        return libraryProvider()?.CancelLr2SongDbSync(reason) == true;
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

    internal void QueueAfterReloadFileDiff(string reason)
    {
        if (!CanRun())
        {
            return;
        }

        QueueCore(reason, force: false);
    }

    internal void SchedulePostStartupSync(string reason, Action queued = null)
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
            });
    }

    internal void SyncFolderDataAfterSettingsChange(string reason)
    {
        if (!CanRun())
        {
            return;
        }

        ScheduleBackground(
            "SyncLr2SongDbSyncFolderDataAfterSettingsChange",
            () => runtime.TryRunDataPreparation(
                reason,
                () => Lr2SongDbSyncPreparedDataSurface.Merge(
                    runtime.PreparePlaylistGeneratedData(reason),
                    runtime.PrepareBuiltinGeneratedData(reason)),
                () => runtime.Queue(reason, force: false, allowIncompleteToQueue: false)));
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

    internal void CancelStatusBarSync()
    {
        if (!CanRun())
        {
            return;
        }

        runtime.Cancel("status_bar_cancel");
    }

    internal void CleanupStartupScanBlockersAndRetry()
    {
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
            () => runtime.PreparePlaylistGeneratedData(reason));
    }

    private bool CanRun()
    {
        return runtime.IsLr2ModeEnabled && runtime.IsLibraryAvailable;
    }

    private void ScheduleBackground(string routeName, Action work)
    {
        Task task = backgroundScheduler(work);
        taskLogger(task, routeName);
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
            UiDialogStatus.ClosedByUser => result.MessageBoxResult is MessageBoxResult.OK or MessageBoxResult.Yes,
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
