using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class PendingExtensionRenameResult
{
    public List<BMSFile> FilesToRemove { get; } = new List<BMSFile>();

    public List<PendingExtensionRenameFailure> Failures { get; } = new List<PendingExtensionRenameFailure>();

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
