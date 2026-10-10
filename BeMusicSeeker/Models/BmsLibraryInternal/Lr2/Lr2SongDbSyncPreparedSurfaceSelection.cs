namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>同じ操作で生成・検証した完全な面を最終入力へ直接渡します。</summary>
internal sealed class Lr2SongDbSyncPreparedSurfaceSelection(Lr2SongDbSyncPreparedDataSurface surface)
{
    public Lr2SongDbSyncPreparedDataSurface Surface { get; } = surface;
    public bool HasPreparedSurface => Surface?.HasPreparedDataSurface == true;
    public bool HasLr2FolderSurface => Surface?.HasLr2FolderSurface == true;
}
