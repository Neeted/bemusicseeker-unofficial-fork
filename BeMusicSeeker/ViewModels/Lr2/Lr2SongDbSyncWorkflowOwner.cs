using System;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;

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
    SkippedUnavailable,

    /// <summary>LまたはPが競合し、準備前に見送った要求。予約や自動再実行を行いません。</summary>
    SkippedCompeting
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

    void DiscardCommittedPathReceipt(string reason);

    /// <summary>全体同期はLとPを準備前に非待機取得し、実worker・状態保存・cleanupの終端まで保持します。</summary>
    /// <param name="capability">受理済み必須継続の生存権限。nullは新しい要求です。</param>
    /// <param name="acceptedBackground">外側起動登録の識別。新しい全体同期は背景登録でも競合時に待ちません。</param>
    /// <returns>取得できた要求は実終端後にtrue、開始競合や未接続は副作用なしでfalse。元失敗と取消を伝播します。</returns>
    Task<bool> QueueAsync(
        string reason,
        bool force,
        bool prepareGeneratedData = false,
        bool allowIncompleteToQueue = true,
        bool allowCommittedPathReceipt = false, LibraryFileMutationCapability capability = null,
        bool acceptedBackground = false, bool includeBuiltinGeneratedData = false, LibraryFileMutationCapability playlistCapability = null);

    /// <summary>受理済み設定後更新の実処理とcleanupを非同期で追跡します。</summary>
    Task SyncExternalFolderRowsForCustomFolderOutputBaseChangeAsync(string reason, LibraryFileMutationCapability capability = null, LibraryFileMutationCapability playlistCapability = null);

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

    public void DiscardCommittedPathReceipt(string reason)
    {
        libraryProvider()?.Lr2Synchronization.DiscardLr2SongDbSyncCommittedPathReceipt(reason);
    }

    /// <inheritdoc/>
    public async Task<bool> QueueAsync(
        string reason,
        bool force,
        bool prepareGeneratedData = false,
        bool allowIncompleteToQueue = true,
        bool allowCommittedPathReceipt = false, LibraryFileMutationCapability capability = null,
        bool acceptedBackground = false, bool includeBuiltinGeneratedData = false, LibraryFileMutationCapability playlistCapability = null)
    {
        BMSLibrary library = libraryProvider();
        if (library == null)
        {
            return false;
        }

        Func<LibraryFileMutationLease, BmsLibraryOptionsSnapshot, Lr2SongDbSyncPreparedDataSurface> prepareWithLease = null;
        CustomFolderOutputSettingsSnapshot outputSettings = null;
        Action<LibraryFileMutationCapability> capturePreparationInputs = null;
        LibraryFileMutationCapability playlistAuthority = null;
        if (prepareGeneratedData)
        {
            capturePreparationInputs = authority =>
            {
                playlistAuthority = authority;
                outputSettings = playlistProvider()?.GetCustomFolderOutputSettings();
            };
            prepareWithLease = (preparationLease, options) =>
                PrepareGeneratedDataUnderLease(
                    library,
                    reason,
                    preparationLease,
                    includeBuiltinGeneratedData, options, outputSettings, playlistAuthority);
        }
        bool admitted = false;
        await library.QueueLr2SongDbSyncAsync(
            reason,
            force,
            prepareWithLease,
            allowIncompleteToQueue,
            allowCommittedPathReceipt, capability, acceptedBackground, capturePreparationInputs: capturePreparationInputs, playlistCapability: playlistCapability, admissionResult: result => admitted = result).ConfigureAwait(false);
        return admitted;
    }

    /// <summary>呼出元の生存中の共通権限で生成面を準備し、保存前の入力結果を返します。</summary>
    internal Lr2SongDbSyncPreparedDataSurface PrepareGeneratedDataUnderLease(
        BMSLibrary library,
        string reason,
        LibraryFileMutationLease preparationLease,
        bool includeBuiltinGeneratedData, BmsLibraryOptionsSnapshot options = null,
        CustomFolderOutputSettingsSnapshot outputSettings = null, LibraryFileMutationCapability playlistCapability = null)
    {
        ArgumentNullException.ThrowIfNull(preparationLease);
        using LibraryFileMutationCapability mutationCapability = preparationLease.CreateMutationCapability();
        mutationCapability.Validate(library.OperationAdmission);

        BMSPlaylist playlists = playlistProvider()
            ?? throw new InvalidOperationException("LR2 playlist preparation is unavailable.");

        var progress = new Lr2SongDbSyncStageProgressReporter((stage, processed, total) =>
            library.PublishLr2SongDbSyncExternalStageProgress(stage, processed, total));
        progress.Begin("playlist_preparation");
        Lr2SongDbSyncPreparedDataSurface playlistSurface =
            playlists.ReOutputAllCustomFoldersForLr2SongDbSyncUnderExistingReservation(
            reason,
            playlistCapability ?? throw new InvalidOperationException("Playlist preparation requires its accepted authority."),
            stageProgressReporter: (stage, processed, total) => library.PublishLr2SongDbSyncExternalStageProgress(stage, processed, total),
            settings: outputSettings);

        if (!includeBuiltinGeneratedData)
        {
            return playlistSurface;
        }

        progress.Begin("builtin_folder_preparation");
        return Lr2SongDbSyncPreparedDataSurface.Merge(
            playlistSurface,
            library.Lr2Synchronization.SyncLr2BuiltinCustomFolderRows(
                reason,
                mutationCapability, options));
    }

    /// <summary>接続済みライブラリへ受理済みの設定後更新を渡し、実終端を待ちます。</summary>
    public Task SyncExternalFolderRowsForCustomFolderOutputBaseChangeAsync(string reason, LibraryFileMutationCapability capability = null, LibraryFileMutationCapability playlistCapability = null)
        => libraryProvider()?.Lr2Synchronization.SyncExternalLr2FolderRowsForCustomFolderOutputBaseChangeAsync(reason, capability, playlistCapability) ?? Task.CompletedTask;

}

internal sealed class Lr2SongDbSyncWorkflowOwner
{
    private readonly ILr2SongDbSyncWorkflowRuntime runtime;

    private readonly Func<Action, Task> backgroundScheduler;

    private readonly Action<Task, string> taskLogger;

    /// <summary>
    /// Reports that the LR2 sync owner received a status-bar retry request.
    /// </summary>
    internal event Action StatusBarRetryRequested;

    internal Lr2SongDbSyncWorkflowOwner(
        ILr2SongDbSyncWorkflowRuntime runtime,
        Func<Action, Task> backgroundScheduler = null,
        Action<Task, string> taskLogger = null)
    {
        this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        this.backgroundScheduler = backgroundScheduler ?? (action => Task.Run(action));
        this.taskLogger = taskLogger ?? new Action<Task, string>((task, routeName) => task.ObserveFault(routeName));
    }

    internal void RequestStatusBarRetry()
    {
        NotifyStatusBarActionReceived(StatusBarRetryRequested, "Lr2StatusBarRetryRequestNotification");
        if (!CanRun())
        {
            runtime.DiscardCommittedPathReceipt("workflow_unavailable");
            return;
        }

        taskLogger(QueueCoreAsync("status_bar_retry", force: false), "RequestLr2SongDbSync");
    }

    /// <summary>新しい明示同期を非待機で受付し、準備・保存・cleanupの実終端を待ちます。</summary>
    internal async Task RequestManualResyncAsync()
    {
        if (!CanRun()) { runtime.DiscardCommittedPathReceipt("workflow_unavailable"); return; }
        await QueueCoreAsync("setting_dialog_manual_resync", force: true).ConfigureAwait(false);
    }

    /// <summary>受理済み差分の共通権限でLR2の実終端を待ち、失敗時は後段へ進みません。</summary>
    internal async Task<Lr2SongDbSyncQueueResult> QueueAfterReloadFileDiffAsync(FileDiffReloadRequest request,
        LibraryFileMutationCapability capability = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!CanRun())
        {
            runtime.DiscardCommittedPathReceipt("reload_queue_unavailable");
            return new Lr2SongDbSyncQueueResult(request, Lr2SongDbSyncQueueStatus.SkippedUnavailable);
        }
        bool admitted = await QueueCoreAsync(request.Reason, force: false, allowCommittedPathReceipt: true, capability: capability).ConfigureAwait(false);
        return new Lr2SongDbSyncQueueResult(request, admitted ? Lr2SongDbSyncQueueStatus.Queued : Lr2SongDbSyncQueueStatus.SkippedCompeting);
    }

    /// <summary>独立した受理済み起動継続を既存依存先へ投入し、LR2実終端後に後段を呼びます。</summary>
    internal void SchedulePostStartupSync(string reason, Action queued = null, Func<Func<Task>, Task> scheduler = null)
    {
        if (!CanRun()) { runtime.DiscardCommittedPathReceipt("startup_queue_unavailable"); queued?.Invoke(); return; }
        ScheduleBackgroundAsync("PostStartupLr2SongDbSync", async () =>
        {
            ExceptionDispatchInfo failure = null;
            try { await QueueCoreAsync("post_startup_" + (reason ?? string.Empty), false, true, acceptedBackground: true).ConfigureAwait(false); }
            catch (Exception exception) { failure = ExceptionDispatchInfo.Capture(exception); }
            try { queued?.Invoke(); }
            finally { failure?.Throw(); }
        }, scheduler);
    }

    /// <summary>受理済み設定の権限で生成データ準備とDB同期の実終端まで待ちます。</summary>
    internal Task SyncFolderDataAfterSettingsChangeAsync(string reason, LibraryFileMutationCapability capability, LibraryFileMutationCapability playlistCapability = null)
    {
        if (!CanRun()) { runtime.DiscardCommittedPathReceipt("settings_queue_unavailable"); return Task.CompletedTask; }
        return runtime.QueueAsync(reason, false, prepareGeneratedData: true, allowIncompleteToQueue: false,
            capability: capability, includeBuiltinGeneratedData: true, playlistCapability: playlistCapability);
    }

    /// <summary>受理済み設定の権限で外部folder更新の実終端まで待ちます。</summary>
    internal Task SyncExternalFolderRowsAfterCustomFolderOutputBaseSettingsChangeAsync(string reason, LibraryFileMutationCapability capability, LibraryFileMutationCapability playlistCapability = null)
    {
        if (!CanRun()) { runtime.DiscardCommittedPathReceipt("external_queue_unavailable"); return Task.CompletedTask; }
        return runtime.SyncExternalFolderRowsForCustomFolderOutputBaseChangeAsync(reason, capability, playlistCapability);
    }

    private Task<bool> QueueCoreAsync(string reason, bool force, bool allowCommittedPathReceipt = false,
        LibraryFileMutationCapability capability = null, bool acceptedBackground = false)
        => runtime.QueueAsync(reason, force, prepareGeneratedData: true,
            allowCommittedPathReceipt: allowCommittedPathReceipt, capability: capability, acceptedBackground: acceptedBackground);

    private bool CanRun()
    {
        return runtime.IsLr2ModeEnabled && runtime.IsLibraryAvailable;
    }

    /// <summary>既存の背景投入先で非同期処理の実終端を追跡します。起動先は登録と実処理を一つのTaskで返します。</summary>
    private void ScheduleBackgroundAsync(string routeName, Func<Task> work, Func<Func<Task>, Task> scheduler = null)
    {
        async Task RunAsync()
        {
            Task operation = null;
            await backgroundScheduler(() => operation = work()).ConfigureAwait(false);
            if (operation == null) { throw new InvalidOperationException("Background scheduler completed without starting its operation."); }
            await operation.ConfigureAwait(false);
        }
        taskLogger(scheduler == null ? RunAsync() : scheduler(work), routeName);
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

}
