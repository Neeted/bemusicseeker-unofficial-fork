namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class MaintenanceWorkflowResult
{
    public bool HasUpdates { get; set; }

    public int CheckedFileCount { get; set; }

    public int MaintenanceInfoUpsertCount { get; set; }

    public int SongUpsertCount { get; set; }

    public int ReloadedSongCount { get; set; }

    public int ZeroNoteChangedCount { get; set; }

    public long TotalMs { get; set; }
}
