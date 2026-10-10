using System;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;

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


    /// <summary>全体同期はLとPを準備前に非待機取得し、実worker・状態保存・cleanupの終端まで保持します。</summary>
    /// <param name="capability">受理済み必須継続の生存権限。nullは新しい要求です。</param>
    /// <param name="acceptedBackground">外側起動登録の識別。新しい全体同期は背景登録でも競合時に待ちません。</param>
    /// <returns>取得できた要求は実終端後にtrue、開始競合や未接続は副作用なしでfalse。元失敗と取消を伝播します。</returns>
    Task<bool> QueueAsync(
        string reason,
        bool force,
        bool prepareGeneratedData = false,
        bool allowIncompleteToQueue = true,
        LibraryFileInitializationResult initializationResult = null, LibraryFileMutationCapability capability = null,
        bool acceptedBackground = false, bool includeBuiltinGeneratedData = false, LibraryFileMutationCapability playlistCapability = null, Lr2SongDbSyncPreparedDataSurface preparedSurface = null, OperationProgressRequest originatingRequest = null, BmsLibraryOptionsSnapshot optionsSnapshot = null);

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

    /// <inheritdoc/>
    public async Task<bool> QueueAsync(
        string reason,
        bool force,
        bool prepareGeneratedData = false,
        bool allowIncompleteToQueue = true,
        LibraryFileInitializationResult initializationResult = null, LibraryFileMutationCapability capability = null,
        bool acceptedBackground = false, bool includeBuiltinGeneratedData = false, LibraryFileMutationCapability playlistCapability = null, Lr2SongDbSyncPreparedDataSurface preparedSurface = null, OperationProgressRequest originatingRequest = null, BmsLibraryOptionsSnapshot optionsSnapshot = null)
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
            initializationResult, capability, acceptedBackground, capturePreparationInputs: capturePreparationInputs, playlistCapability: playlistCapability, optionsSnapshot: optionsSnapshot, admissionResult: result => admitted = result, preparedSurface: preparedSurface, originatingRequest: originatingRequest).ConfigureAwait(false);
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

    internal Lr2SongDbSyncWorkflowOwner(ILr2SongDbSyncWorkflowRuntime runtime)
    {
        this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    /// <summary>新しい明示同期を非待機で受付し、準備・保存・cleanupの実終端を待ちます。</summary>
    internal async Task RequestManualResyncAsync()
    {
        if (!CanRun()) { return; }
        await QueueCoreAsync("setting_dialog_manual_resync", force: true).ConfigureAwait(false);
    }

    /// <summary>受理済み差分の共通権限でLR2の実終端を待ち、失敗時は後段へ進みません。</summary>
    internal async Task<Lr2SongDbSyncQueueResult> QueueAfterReloadFileDiffAsync(FileDiffReloadRequest request,
        LibraryFileMutationCapability capability = null, LibraryFileInitializationResult initializationResult = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!CanRun())
        {
            return new Lr2SongDbSyncQueueResult(request, Lr2SongDbSyncQueueStatus.SkippedUnavailable);
        }
        bool admitted = await QueueCoreAsync(request.Reason, force: false, initializationResult: initializationResult, capability: capability, originatingRequest: request.ProgressRequest).ConfigureAwait(false);
        return new Lr2SongDbSyncQueueResult(request, admitted ? Lr2SongDbSyncQueueStatus.Queued : Lr2SongDbSyncQueueStatus.SkippedCompeting);
    }

    /// <summary>ローカル確定後の必要同期を同じL/Pで直接待ちます。準備済みの完全な生成面を再出力しません。</summary>
    internal Task<bool> SynchronizeRequiredAsync(string reason, LibraryFileInitializationResult initializationResult,
        Lr2SongDbSyncPreparedDataSurface preparedSurface, LibraryFileMutationCapability capability, OperationProgressRequest originatingRequest = null, BmsLibraryOptionsSnapshot optionsSnapshot = null)
        => CanRun() ? runtime.QueueAsync(reason, false, initializationResult: initializationResult,
            capability: capability, preparedSurface: preparedSurface, originatingRequest: originatingRequest, optionsSnapshot: optionsSnapshot) : Task.FromResult(false);

    /// <summary>受理済み設定の権限で生成データ準備とDB同期の実終端まで待ちます。</summary>
    internal Task SyncFolderDataAfterSettingsChangeAsync(string reason, LibraryFileMutationCapability capability, LibraryFileMutationCapability playlistCapability = null)
    {
        if (!CanRun()) { return Task.CompletedTask; }
        return runtime.QueueAsync(reason, false, prepareGeneratedData: true, allowIncompleteToQueue: false,
            capability: capability, includeBuiltinGeneratedData: true, playlistCapability: playlistCapability);
    }

    /// <summary>受理済み設定の権限で外部folder更新の実終端まで待ちます。</summary>
    internal Task SyncExternalFolderRowsAfterCustomFolderOutputBaseSettingsChangeAsync(string reason, LibraryFileMutationCapability capability, LibraryFileMutationCapability playlistCapability = null)
    {
        if (!CanRun()) { return Task.CompletedTask; }
        return runtime.SyncExternalFolderRowsForCustomFolderOutputBaseChangeAsync(reason, capability, playlistCapability);
    }

    private Task<bool> QueueCoreAsync(string reason, bool force, LibraryFileInitializationResult initializationResult = null,
        LibraryFileMutationCapability capability = null, bool acceptedBackground = false, OperationProgressRequest originatingRequest = null)
        => runtime.QueueAsync(reason, force, prepareGeneratedData: true,
            initializationResult: initializationResult, capability: capability, acceptedBackground: acceptedBackground, originatingRequest: originatingRequest);

    private bool CanRun()
    {
        return runtime.IsLr2ModeEnabled && runtime.IsLibraryAvailable;
    }

}
