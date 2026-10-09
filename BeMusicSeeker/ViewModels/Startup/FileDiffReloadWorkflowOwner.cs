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
    internal FileDiffReloadRequest(string reason, long operationToken, LibraryFileMutationCapability capability = null)
    {
        Reason = reason ?? string.Empty;
        OperationToken = operationToken;
        Capability = capability;
    }

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
/// Immutable receipt produced after a file-diff reload and its two downstream queue stages succeed.
/// </summary>
internal sealed class FileDiffReloadWorkflowResult
{
    /// <summary>
    /// Initializes the completed file-diff workflow receipt.
    /// </summary>
    /// <param name="request">The exact request that started the workflow.</param>
    /// <param name="lr2QueueResult">The LR2 queue result for the request.</param>
    /// <param name="playlistReferenceQueueRequest">The exact playlist-reference queue request.</param>
    internal FileDiffReloadWorkflowResult(
        FileDiffReloadRequest request,
        Lr2SongDbSyncQueueResult lr2QueueResult,
        PlaylistReferenceApplyQueueRequest playlistReferenceQueueRequest)
    {
        Request = request ?? throw new ArgumentNullException(nameof(request));
        Lr2QueueResult = lr2QueueResult
            ?? throw new ArgumentNullException(nameof(lr2QueueResult));
        PlaylistReferenceQueueRequest = playlistReferenceQueueRequest
            ?? throw new ArgumentNullException(nameof(playlistReferenceQueueRequest));
    }

    /// <summary>
    /// Gets the exact file-diff request that completed.
    /// </summary>
    internal FileDiffReloadRequest Request { get; }

    /// <summary>
    /// Gets the LR2 synchronization queue result produced after file-diff reload.
    /// </summary>
    internal Lr2SongDbSyncQueueResult Lr2QueueResult { get; }

    /// <summary>
    /// Gets the exact playlist-reference queue request submitted after LR2 synchronization.
    /// </summary>
    internal PlaylistReferenceApplyQueueRequest PlaylistReferenceQueueRequest { get; }
}

/// <summary>
/// Owns the file-diff reload sequence and its narrow post-reload queue handoffs.
/// </summary>
internal sealed class FileDiffReloadWorkflowOwner
{
    private readonly Func<FileDiffReloadRequest, Task> reloadFileDiff;

    private readonly Lr2SongDbSyncWorkflowOwner lr2SongDbSyncWorkflow;

    private readonly Action<PlaylistReferenceApplyQueueRequest> queuePlaylistReference;

    /// <summary>
    /// Initializes the canonical reload owner with all three narrow workflow stages.
    /// </summary>
    /// <param name="reloadFileDiff">The operation that performs the durable file-diff reload.</param>
    /// <param name="lr2SongDbSyncWorkflow">The owner that receives the post-reload LR2 request.</param>
    /// <param name="queuePlaylistReference">The typed playlist-reference queue port.</param>
    internal FileDiffReloadWorkflowOwner(
        Func<FileDiffReloadRequest, Task> reloadFileDiff,
        Lr2SongDbSyncWorkflowOwner lr2SongDbSyncWorkflow,
        Action<PlaylistReferenceApplyQueueRequest> queuePlaylistReference)
    {
        this.reloadFileDiff = reloadFileDiff
            ?? throw new ArgumentNullException(nameof(reloadFileDiff));
        this.lr2SongDbSyncWorkflow = lr2SongDbSyncWorkflow
            ?? throw new ArgumentNullException(nameof(lr2SongDbSyncWorkflow));
        this.queuePlaylistReference = queuePlaylistReference
            ?? throw new ArgumentNullException(nameof(queuePlaylistReference));
    }

    /// <summary>
    /// 受理済み差分更新と必須LR2継続を同じ生存権限で実終端まで待ち、その後プレイリスト参照更新を登録します。
    /// LR2経路が利用不能の場合は型付きSkipped結果として後段へ進み、それ以外の例外・取消は後段を開始せず伝播します。
    /// 共通受付の再取得や呼出元leaseの解放は行いません。
    /// </summary>
    /// <param name="request">理由・進捗番号・同ownerの生存権限を共有する変更不能な要求。</param>
    /// <returns>必須処理の実終端と後段登録までのTask。元要求、LR2結果と登録した参照更新要求を返します。</returns>
    internal async Task<FileDiffReloadWorkflowResult> ReloadAsync(FileDiffReloadRequest request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        await reloadFileDiff(request).ConfigureAwait(false);
        Lr2SongDbSyncQueueResult lr2QueueResult =
            await lr2SongDbSyncWorkflow.QueueAfterReloadFileDiffAsync(request, request.Capability).ConfigureAwait(false);
        PlaylistReferenceApplyQueueRequest playlistReferenceQueueRequest =
            new(request.Reason, request.OperationToken);
        queuePlaylistReference(playlistReferenceQueueRequest);
        return new FileDiffReloadWorkflowResult(
            request,
            lr2QueueResult,
            playlistReferenceQueueRequest);
    }

}
