namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class Lr2SongDbSyncPreparedSurfaceSelection(
    Lr2SongDbSyncPreparedDataSurface pendingSurface,
    Lr2SongDbSyncPreparedDataSurface activeSurface,
    int appliedScanGeneration,
    bool alreadyAppliedToScanSurface)
{
    public Lr2SongDbSyncPreparedDataSurface PendingSurface { get; } = pendingSurface;

    public Lr2SongDbSyncPreparedDataSurface ActiveSurface { get; } = activeSurface;

    public int AppliedScanGeneration { get; } = appliedScanGeneration;

    public bool AlreadyAppliedToScanSurface { get; } = alreadyAppliedToScanSurface;

    public bool HasActivePreparedSurface => ActiveSurface?.HasPreparedDataSurface == true;

    public bool HasActiveLr2FolderSurface => ActiveSurface?.HasLr2FolderSurface == true;
}
