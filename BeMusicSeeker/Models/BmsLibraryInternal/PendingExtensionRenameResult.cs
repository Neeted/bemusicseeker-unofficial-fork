using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class PendingExtensionRenameResult
{
    public List<string> ChartPathsToRemove { get; } = [];

    public List<PendingExtensionRenameFailure> Failures { get; } = [];

    public int Total { get; set; }

    public int Renamed { get; set; }

    public int DuplicateDeleted { get; set; }

    public int Skipped { get; set; }

    public int Failed { get; set; }

    public long TotalMs { get; set; }
}

internal sealed class PendingExtensionRenameFailure
{
    public BMSFile File { get; set; }

    public RenameInvalidExtensionOutcome Outcome { get; set; }
}
