using System;
using System.Threading.Tasks;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// 一回の差分再読込みと必須LR2継続が共有する変更不能な要求です。
/// </summary>
internal sealed class FileDiffReloadRequest
{
    /// <summary>
    /// 各段階へ渡す理由・進捗番号と、呼出元が所有する生存中の共通変更権限を捕捉します。
    /// </summary>
    /// <param name="reason">必須LR2継続とプレイリスト参照更新へ同じ値で渡す理由。</param>
    /// <param name="operationToken">画面の起動進捗操作を識別する番号。</param>
    /// <param name="capability">同じownerが受理した操作の生存権限。実モデルを更新する呼出元は全必須継続の実終端まで所有します。</param>
    /// <param name="progressRequest">受理した親の表示世代と操作識別。必須LR2へ同じ発生元を直接渡します。</param>
    /// <param name="warningObserver">今回の走査警告を親解放後の通知へ持ち出す局所境界。</param>
    internal FileDiffReloadRequest(string reason, long operationToken, LibraryFileMutationCapability capability = null, OperationProgressRequest progressRequest = null, Action<LibraryScanWarning> warningObserver = null)
    {
        Reason = reason ?? string.Empty;
        OperationToken = operationToken;
        Capability = capability;
        ProgressRequest = progressRequest;
        WarningObserver = warningObserver;
    }

    /// <summary>この操作だけの走査警告を、親解放後に通知する呼出元へ渡します。</summary>
    internal Action<LibraryScanWarning> WarningObserver { get; }

    /// <summary>呼出元で捕捉した親の進捗識別です。</summary>
    internal OperationProgressRequest ProgressRequest { get; }

    /// <summary>受理済み差分と必須LR2継続に共通の生存権限。要求は所有元leaseを解放しません。</summary>
    internal LibraryFileMutationCapability Capability { get; }

    /// <summary>LR2継続とプレイリスト参照更新へ渡す共通の理由。</summary>
    internal string Reason { get; }

    /// <summary>
    /// 各段階へ渡す画面の進捗操作番号。
    /// </summary>
    internal long OperationToken { get; }
}

/// <summary>
/// 差分、必要LR2、保存済み表参照の実終端後に返す変更不能な結果です。
/// </summary>
internal sealed class FileDiffReloadWorkflowResult
{
    /// <summary>
    /// 直接待った差分・LR2・参照の今回結果を保持します。
    /// </summary>
    /// <param name="request">今回の差分要求。</param>
    /// <param name="lr2QueueResult">直接待ったLR2結果。</param>
    /// <param name="playlistReferenceQueueRequest">直接反映した参照要求。</param>
    /// <param name="fileResult">今回の差分確定結果。後続は受付解放後に明示登録します。</param>
    internal FileDiffReloadWorkflowResult(
        FileDiffReloadRequest request,
        Lr2SongDbSyncQueueResult lr2QueueResult,
        PlaylistReferenceApplyQueueRequest playlistReferenceQueueRequest, LibraryFileInitializationResult fileResult)
    {
        FileResult = fileResult ?? throw new ArgumentNullException(nameof(fileResult));
        Request = request ?? throw new ArgumentNullException(nameof(request));
        Lr2QueueResult = lr2QueueResult
            ?? throw new ArgumentNullException(nameof(lr2QueueResult));
        PlaylistReferenceQueueRequest = playlistReferenceQueueRequest
            ?? throw new ArgumentNullException(nameof(playlistReferenceQueueRequest));
    }

    /// <summary>
    /// 実終端した差分要求を取得します。
    /// </summary>
    internal FileDiffReloadRequest Request { get; }

    /// <summary>今回の必須ファイル変更の結果。受付外の明示後続へ渡します。</summary>
    internal LibraryFileInitializationResult FileResult { get; }

    /// <summary>
    /// 差分後に直接待ったLR2結果を取得します。
    /// </summary>
    internal Lr2SongDbSyncQueueResult Lr2QueueResult { get; }

    /// <summary>
    /// LR2後に直接反映した参照要求を取得します。
    /// </summary>
    internal PlaylistReferenceApplyQueueRequest PlaylistReferenceQueueRequest { get; }
}

/// <summary>
/// 差分と必要LR2、参照の実処理を同じ要求で直接待つ管理主体です。
/// </summary>
internal sealed class FileDiffReloadWorkflowOwner
{
    private readonly Func<FileDiffReloadRequest, Task<LibraryFileInitializationResult>> reloadFileDiff;

    private readonly Lr2SongDbSyncWorkflowOwner lr2SongDbSyncWorkflow;

    private readonly Func<PlaylistReferenceApplyQueueRequest, Task> queuePlaylistReference;

    /// <summary>
    /// 差分、必要LR2、参照の実Task境界を接続します。
    /// </summary>
    /// <param name="reloadFileDiff">差分と確定結果を返す実処理。</param>
    /// <param name="lr2SongDbSyncWorkflow">今回の差分結果を受け取るLR2管理主体。</param>
    /// <param name="queuePlaylistReference">同じ要求の参照を直接反映する境界。</param>
    internal FileDiffReloadWorkflowOwner(
        Func<FileDiffReloadRequest, Task<LibraryFileInitializationResult>> reloadFileDiff,
        Lr2SongDbSyncWorkflowOwner lr2SongDbSyncWorkflow,
        Func<PlaylistReferenceApplyQueueRequest, Task> queuePlaylistReference)
    {
        this.reloadFileDiff = reloadFileDiff
            ?? throw new ArgumentNullException(nameof(reloadFileDiff));
        this.lr2SongDbSyncWorkflow = lr2SongDbSyncWorkflow
            ?? throw new ArgumentNullException(nameof(lr2SongDbSyncWorkflow));
        this.queuePlaylistReference = queuePlaylistReference
            ?? throw new ArgumentNullException(nameof(queuePlaylistReference));
    }

    /// <summary>
    /// 受理済み差分更新と必須LR2継続を同じ生存権限で実終端まで待ち、その後プレイリスト参照の実終端を待ちます。
    /// LR2経路が利用不能の場合は型付きSkipped結果として後段へ進み、それ以外の例外・取消は後段を開始せず伝播します。
    /// 共通受付の再取得や呼出元leaseの解放は行いません。
    /// </summary>
    /// <param name="request">理由・進捗番号・同ownerの生存権限を共有する変更不能な要求。</param>
    /// <returns>全必須処理の実終端までのTask。元要求、今回ファイル結果・LR2結果・反映した参照要求を返します。</returns>
    internal async Task<FileDiffReloadWorkflowResult> ReloadAsync(FileDiffReloadRequest request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        LibraryFileInitializationResult fileResult = await reloadFileDiff(request).ConfigureAwait(false);
        Lr2SongDbSyncQueueResult lr2QueueResult =
            await lr2SongDbSyncWorkflow.QueueAfterReloadFileDiffAsync(request, request.Capability, fileResult).ConfigureAwait(false);
        PlaylistReferenceApplyQueueRequest playlistReferenceQueueRequest =
            new(request.Reason, request.OperationToken);
        await queuePlaylistReference(playlistReferenceQueueRequest).ConfigureAwait(false);
        return new FileDiffReloadWorkflowResult(
            request,
            lr2QueueResult,
            playlistReferenceQueueRequest, fileResult);
    }

}
