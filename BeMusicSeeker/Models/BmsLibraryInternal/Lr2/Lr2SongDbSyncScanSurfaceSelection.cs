namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class Lr2SongDbSyncScanSurfaceSelection(
    Lr2SongDbSyncScanSurfaceSnapshot surface,
    string missReason)
{
    public Lr2SongDbSyncScanSurfaceSnapshot Surface { get; } = surface;

    public string MissReason { get; } = missReason ?? string.Empty;

    public bool ReusedScanSurface => Surface != null;

    public int Generation => Surface?.Generation ?? 0;

    public bool ReusedLr2FolderSurface => Surface?.Lr2FolderFileDiscoveryComplete == true;
}
