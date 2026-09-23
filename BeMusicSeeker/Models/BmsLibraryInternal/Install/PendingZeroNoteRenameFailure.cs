namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class PendingZeroNoteRenameFailure
{
    public BMSFile File { get; set; }

    public RenameInvalidExtensionOutcome Outcome { get; set; }
}
