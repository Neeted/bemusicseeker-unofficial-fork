using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class PendingZeroNoteRenameResult
{
    public int Total { get; set; }

    public int Processed { get; set; }

    public int ZeroNote { get; set; }

    public int Renamed { get; set; }

    public int DuplicateDeleted { get; set; }

    public int Skipped { get; set; }

    public int Failed { get; set; }

    public bool Canceled { get; set; }

    public List<string> ChartPathsToRemove { get; } = [];

    public List<PendingZeroNoteRenameFailure> Failures { get; } = [];
}
