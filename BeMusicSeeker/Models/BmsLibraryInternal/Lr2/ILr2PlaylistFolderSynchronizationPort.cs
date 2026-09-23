namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Narrow capability used by playlist output materialization to synchronize
/// LR2 folder rows. The caller supplies the live capability issued by the
/// owning mutation lease; the port never performs direct admission itself.
/// </summary>
internal interface ILr2PlaylistFolderSynchronizationPort
{
    CustomFolderOutputPhysicalSurface GetCurrentAppManagedCustomFolderOutputPhysicalSurface();

    Lr2FolderFileDbSyncResult SyncPlaylistLr2FolderFileRows(
        string operation,
        Lr2FolderFileDbSyncRequest request,
        LibraryFileMutationCapability mutationCapability);
}
