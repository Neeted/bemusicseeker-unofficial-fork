using System;
using System.Threading.Tasks;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Immutable input for one file-diff reload operation.
/// </summary>
internal sealed class FileDiffReloadRequest
{
    /// <summary>
    /// Initializes a file-diff request with the reason and shell operation token that own every stage.
    /// </summary>
    /// <param name="reason">The stable reason recorded by downstream workflow owners.</param>
    /// <param name="operationToken">The shell startup-progress operation token.</param>
    internal FileDiffReloadRequest(string reason, long operationToken)
    {
        Reason = reason ?? string.Empty;
        OperationToken = operationToken;
    }

    /// <summary>
    /// Gets the stable reason propagated to LR2 synchronization and playlist-reference queueing.
    /// </summary>
    internal string Reason { get; }

    /// <summary>
    /// Gets the shell operation token propagated to every downstream stage.
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
    /// Runs reload, LR2 queue, and playlist-reference queue in that exact order.
    /// An unavailable LR2 route is represented by the typed skipped result and does not block
    /// the playlist-reference stage; every exception otherwise stops later stages unchanged.
    /// </summary>
    /// <param name="request">The immutable request shared by all stages.</param>
    /// <returns>The exact downstream requests and LR2 queue disposition.</returns>
    internal async Task<FileDiffReloadWorkflowResult> ReloadAsync(FileDiffReloadRequest request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        await reloadFileDiff(request).ConfigureAwait(false);
        Lr2SongDbSyncQueueResult lr2QueueResult =
            lr2SongDbSyncWorkflow.QueueAfterReloadFileDiff(request);
        PlaylistReferenceApplyQueueRequest playlistReferenceQueueRequest =
            new(request.Reason, request.OperationToken);
        queuePlaylistReference(playlistReferenceQueueRequest);
        return new FileDiffReloadWorkflowResult(
            request,
            lr2QueueResult,
            playlistReferenceQueueRequest);
    }

}
