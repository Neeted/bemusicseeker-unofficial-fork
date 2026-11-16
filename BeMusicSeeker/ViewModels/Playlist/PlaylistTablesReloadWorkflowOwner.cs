using System;
using System.Threading.Tasks;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// 一回の表再読込みに必要な進捗識別と、親操作が保持する生存P権限です。
/// </summary>
internal sealed class PlaylistTablesReloadRequest
{
    /// <summary>親の進捗識別とP権限を捕捉します。権限の所有と終端解放は呼出元の責務です。</summary>
    /// <param name="operationToken">親が所有する起動・再読込みの進捗識別。</param>
    /// <param name="capability">同じownerの生存P権限。外側受付がない入口ではnullです。</param>
    internal PlaylistTablesReloadRequest(long operationToken, LibraryFileMutationCapability capability = null)
    {
        OperationToken = operationToken;
        Capability = capability;
    }

    /// <summary>再読込みと必要同期が共有する親の進捗識別です。</summary>
    internal long OperationToken { get; }

    /// <summary>呼出元が実処理終端まで保持し、内部継続が借用するP権限です。</summary>
    internal LibraryFileMutationCapability Capability { get; }
}

/// <summary>
/// 表再読込みから必要な外部同期へ同じ進捗識別と生存P権限を渡す要求です。
/// </summary>
internal sealed class PlaylistTablesExternalSyncRequest
{
    /// <summary>再読込み完了後の必要同期へ、同じ親の識別と権限を渡します。</summary>
    /// <param name="operationToken">親が所有する起動・再読込みの進捗識別。</param>
    /// <param name="capability">同じownerの生存P権限。所有・解放は親が行います。</param>
    internal PlaylistTablesExternalSyncRequest(long operationToken, LibraryFileMutationCapability capability)
    {
        OperationToken = operationToken;
        Capability = capability;
    }

    /// <summary>呼出元が保持し、必要同期と出力が借用する生存P権限です。</summary>
    internal LibraryFileMutationCapability Capability { get; }

    /// <summary>再読込み後の同期を識別する既存の診断理由です。</summary>
    internal string Reason => "ReloadTables";

    /// <summary>再読込みからの必須継続であることを示します。</summary>
    internal bool FromReloadTables => true;

    /// <summary>必要同期の終端で参照公開の完了通知を発行します。</summary>
    internal bool PublishReferenceReceipt => true;

    /// <summary>必要同期が親の再読込みと共有する進捗識別です。</summary>
    internal long OperationToken { get; }
}

/// <summary>
/// 表再読込みと必要外部同期の両Taskが成功終端した際の変更不能な結果です。
/// </summary>
internal sealed class PlaylistTablesReloadWorkflowResult
{
    /// <summary>再読込みと必要同期が成功終端した要求の組を保持します。</summary>
    /// <param name="reloadRequest">実終端した再読込み要求。</param>
    /// <param name="externalSyncRequest">同じ権限で実終端した必要同期要求。</param>
    internal PlaylistTablesReloadWorkflowResult(
        PlaylistTablesReloadRequest reloadRequest,
        PlaylistTablesExternalSyncRequest externalSyncRequest)
    {
        ReloadRequest = reloadRequest ?? throw new ArgumentNullException(nameof(reloadRequest));
        ExternalSyncRequest = externalSyncRequest
            ?? throw new ArgumentNullException(nameof(externalSyncRequest));
    }

    /// <summary>成功終端した再読込み要求です。</summary>
    internal PlaylistTablesReloadRequest ReloadRequest { get; }

    /// <summary>成功終端した必要同期要求です。</summary>
    internal PlaylistTablesExternalSyncRequest ExternalSyncRequest { get; }
}

/// <summary>
/// 同じ受理済みPの権限で表の再読込みと必要な外部同期を接続し、両Taskの実終端を待ちます。
/// 親操作の進捗と受付は呼出元が所有し、このownerは受付を取り直しません。
/// </summary>
internal sealed class PlaylistTablesReloadWorkflowOwner
{
    private readonly Func<PlaylistTablesReloadRequest, Task> reloadTablesAsync;

    private readonly Func<PlaylistTablesExternalSyncRequest, Task> syncExternalPlaylistsAsync;

    /// <summary>再読込みと同権限の必要同期を実終端まで待つ処理を接続します。</summary>
    /// <param name="reloadTablesAsync">指定要求のPを借用してDB・必要出力・公開を完了する処理。</param>
    /// <param name="syncExternalPlaylistsAsync">親の生存P権限を借用して必要同期と出力を待つ処理。</param>
    internal PlaylistTablesReloadWorkflowOwner(
        Func<PlaylistTablesReloadRequest, Task> reloadTablesAsync,
        Func<PlaylistTablesExternalSyncRequest, Task> syncExternalPlaylistsAsync)
    {
        this.reloadTablesAsync = reloadTablesAsync
            ?? throw new ArgumentNullException(nameof(reloadTablesAsync));
        this.syncExternalPlaylistsAsync = syncExternalPlaylistsAsync
            ?? throw new ArgumentNullException(nameof(syncExternalPlaylistsAsync));
    }

    /// <summary>同じ受理済みP内で再読込みから必要同期・出力まで直接待ちます。いずれかの例外は元のまま伝播します。</summary>
    /// <param name="request">呼出元が実処理終端まで所有するP権限を持つ再読込み要求。</param>
    /// <returns>両Taskの成功終端後に、各段階へ実際に渡した要求を返します。</returns>
    internal async Task<PlaylistTablesReloadWorkflowResult> ReloadAsync(
        PlaylistTablesReloadRequest request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        await reloadTablesAsync(request);
        PlaylistTablesExternalSyncRequest externalSyncRequest =
            new(request.OperationToken, request.Capability);
        await syncExternalPlaylistsAsync(externalSyncRequest);
        return new PlaylistTablesReloadWorkflowResult(request, externalSyncRequest);
    }
}
