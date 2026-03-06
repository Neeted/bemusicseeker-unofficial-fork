namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class ZeroNoteRecheckResult
{
    public int Total { get; set; }

    public int MismatchCount { get; set; }

    public int ClearedCount { get; set; }

    public int SkippedCount { get; set; }
}
