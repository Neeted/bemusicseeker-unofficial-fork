using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class PendingFileDeletionResult
{
    public int Requested { get; set; }

    public int Processed { get; set; }

    public int Removed { get; set; }

    public int Failed { get; set; }

    public int Skipped { get; set; }

    public List<BMSFile> FilesToRemove { get; } = new List<BMSFile>();

    public List<PendingFileDeletionFailure> Failures { get; } = new List<PendingFileDeletionFailure>();
}
