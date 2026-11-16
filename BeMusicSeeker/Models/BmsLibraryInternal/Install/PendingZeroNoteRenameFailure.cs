namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class PendingZeroNoteRenameFailure
{
    public ChartFile File { get; set; }

    public RenameInvalidExtensionOutcome Outcome { get; set; }
}
