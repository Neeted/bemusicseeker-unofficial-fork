namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Narrow capability used by playlist output materialization to synchronize
/// LR2 folder rows. It owns the database transaction and LR2 mutation lease.
/// </summary>
internal interface ILr2PlaylistFolderSynchronizationPort
{
    CustomFolderOutputPhysicalSurface GetCurrentAppManagedCustomFolderOutputPhysicalSurface();

    Lr2FolderFileDbSyncResult SyncPlaylistLr2FolderFileRows(
        string operation,
        Lr2FolderFileDbSyncRequest request);
}
