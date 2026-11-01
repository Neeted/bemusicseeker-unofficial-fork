using System;
using System.Collections.Generic;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Tests;

/// <summary>呼出し側のDB所有者で実LR2行同期を行い、試験ごとの観測と故障入力を受け付けます。</summary>
internal sealed class TestLr2PlaylistFolderSynchronizationPort : ILr2PlaylistFolderSynchronizationPort
{
    private readonly ApplicationDatabaseScope databaseScope;
    private readonly string songDbPath;

    /// <summary>同じfixtureが使う接続所有者と曲DBへ、実LR2行同期を接続します。</summary>
    internal TestLr2PlaylistFolderSynchronizationPort(ApplicationDatabaseScope databaseScope, string songDbPath)
    {
        this.databaseScope = databaseScope;
        this.songDbPath = songDbPath;
    }

    /// <summary>同期の受付順を観測するcase専有の記録です。</summary>
    internal List<string> Operations { get; } = [];

    /// <summary>指定時に現行の物理出力snapshotを供給し、未指定時は未発見として扱います。</summary>
    internal Func<CustomFolderOutputPhysicalSurface> PhysicalSurfaceFactory { get; set; } = null!;

    /// <summary>指定時はDBへ接続する前にこの故障を送出します。</summary>
    internal Exception Failure { get; set; } = null!;

    /// <summary>同期の開始時に、受け渡された実受付能力を観測します。</summary>
    internal Action<LibraryFileMutationCapability> SynchronizationStartProbe { get; set; } = null!;

    /// <summary>注入したsnapshotまたは未発見の空snapshotを返します。</summary>
    public CustomFolderOutputPhysicalSurface GetCurrentAppManagedCustomFolderOutputPhysicalSurface()
    {
        return PhysicalSurfaceFactory?.Invoke()
            ?? new CustomFolderOutputPhysicalSurface(
                new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase),
                discoveryComplete: false);
    }

    /// <summary>同じDB所有者の実接続で同期を確定し、失敗時はsavepointまで戻して元例外を伝播します。</summary>
    public Lr2FolderFileDbSyncResult SyncPlaylistLr2FolderFileRows(
        string operation,
        Lr2FolderFileDbSyncRequest request,
        LibraryFileMutationCapability mutationCapability)
    {
        if (mutationCapability == null)
        {
            throw new ArgumentNullException(nameof(mutationCapability));
        }
        SynchronizationStartProbe?.Invoke(mutationCapability);
        Operations.Add(operation);
        if (Failure != null)
        {
            throw Failure;
        }

        using LR2SongDBExtended songDb = databaseScope.OpenSongWrite(songDbPath);
        string savepoint = songDb.SaveTransactionPoint();
        try
        {
            Lr2FolderFileDbSyncResult result = Lr2FolderFileDbSyncService.Sync(songDb, request);
            songDb.Commit();
            return result;
        }
        catch
        {
            songDb.RollbackTo(savepoint);
            throw;
        }
    }
}
