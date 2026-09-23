using System;
using System.Threading.Tasks;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Immutable input for one initialized playlist-table reload operation.
/// </summary>
internal sealed class PlaylistTablesReloadRequest
{
    /// <summary>
    /// Initializes the reload request for one shell operation token.
    /// </summary>
    /// <param name="operationToken">The startup-progress token that owns the request.</param>
    internal PlaylistTablesReloadRequest(long operationToken)
    {
        OperationToken = operationToken;
    }

    /// <summary>
    /// Gets the startup-progress token associated with this reload.
    /// </summary>
    internal long OperationToken { get; }

    /// <summary>
    /// Gets whether the table reload should queue a BMT export after hydration.
    /// ReloadTables keeps this disabled; the existing deferred external-sync route owns that work.
    /// </summary>
    internal bool QueueBeatorajaBmtExportAfterHydration => false;
}

/// <summary>
/// Immutable request passed from the table-reload owner to playlist external synchronization.
/// </summary>
internal sealed class PlaylistTablesExternalSyncRequest
{
    /// <summary>
    /// Initializes the exact external-sync request used after a successful table reload.
    /// </summary>
    /// <param name="operationToken">The startup-progress token that owns the request.</param>
    internal PlaylistTablesExternalSyncRequest(long operationToken)
    {
        OperationToken = operationToken;
    }

    /// <summary>
    /// Gets the existing reason used by the playlist external-sync route.
    /// </summary>
    internal string Reason => "ReloadTables";

    /// <summary>
    /// Gets whether the request originated from a table reload.
    /// </summary>
    internal bool FromReloadTables => true;

    /// <summary>
    /// Gets whether the sync should publish the playlist-reference completion receipt.
    /// </summary>
    internal bool PublishReferenceReceipt => true;

    /// <summary>
    /// Gets the startup-progress token associated with this external sync.
    /// </summary>
    internal long OperationToken { get; }
}

/// <summary>
/// Immutable receipt produced after table reload and external-sync queueing both succeed.
/// </summary>
internal sealed class PlaylistTablesReloadWorkflowResult
{
    /// <summary>
    /// Initializes a successful table-reload receipt.
    /// </summary>
    /// <param name="reloadRequest">The reload request that completed.</param>
    /// <param name="externalSyncRequest">The exact sync request that was queued.</param>
    internal PlaylistTablesReloadWorkflowResult(
        PlaylistTablesReloadRequest reloadRequest,
        PlaylistTablesExternalSyncRequest externalSyncRequest)
    {
        ReloadRequest = reloadRequest ?? throw new ArgumentNullException(nameof(reloadRequest));
        ExternalSyncRequest = externalSyncRequest
            ?? throw new ArgumentNullException(nameof(externalSyncRequest));
    }

    /// <summary>
    /// Gets the completed reload request.
    /// </summary>
    internal PlaylistTablesReloadRequest ReloadRequest { get; }

    /// <summary>
    /// Gets the exact external-sync request submitted after reload completion.
    /// </summary>
    internal PlaylistTablesExternalSyncRequest ExternalSyncRequest { get; }
}

/// <summary>
/// Owns the playlist-table reload to deferred external-sync queue sequence.
/// Root startup progress and the shared operation gate remain in <see cref="MainWindowViewModel"/>
/// because those resources coordinate every library operation.
/// </summary>
internal sealed class PlaylistTablesReloadWorkflowOwner
{
    private readonly Func<PlaylistTablesReloadRequest, Task> reloadTablesAsync;

    private readonly Action<PlaylistTablesExternalSyncRequest> queueExternalPlaylistSync;

    /// <summary>
    /// Initializes the owner with the two narrow operations in its sequence.
    /// </summary>
    /// <param name="reloadTablesAsync">Reloads table storage for the supplied immutable request.</param>
    /// <param name="queueExternalPlaylistSync">Queues deferred playlist synchronization after reload.</param>
    internal PlaylistTablesReloadWorkflowOwner(
        Func<PlaylistTablesReloadRequest, Task> reloadTablesAsync,
        Action<PlaylistTablesExternalSyncRequest> queueExternalPlaylistSync)
    {
        this.reloadTablesAsync = reloadTablesAsync
            ?? throw new ArgumentNullException(nameof(reloadTablesAsync));
        this.queueExternalPlaylistSync = queueExternalPlaylistSync
            ?? throw new ArgumentNullException(nameof(queueExternalPlaylistSync));
    }

    /// <summary>
    /// Reloads playlist tables and queues external synchronization only after reload succeeds.
    /// Exceptions from either operation are intentionally propagated to the root progress owner.
    /// </summary>
    /// <param name="request">The initialized operation request.</param>
    /// <returns>An immutable receipt containing the exact requests used by both stages.</returns>
    internal async Task<PlaylistTablesReloadWorkflowResult> ReloadAsync(
        PlaylistTablesReloadRequest request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        await reloadTablesAsync(request);
        PlaylistTablesExternalSyncRequest externalSyncRequest =
            new(request.OperationToken);
        queueExternalPlaylistSync(externalSyncRequest);
        return new PlaylistTablesReloadWorkflowResult(request, externalSyncRequest);
    }
}
