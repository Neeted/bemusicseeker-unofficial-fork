using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class LibraryFixInstallationResult
{
    public LibraryMutationDelta MutationDelta { get; } = new LibraryMutationDelta();

    public List<BMSFile> FilesToRemove { get; } = [];

    public List<BMSFile> MaintenanceTargets { get; } = [];

    public int RequestedCount { get; set; }

    public int MovedCount { get; set; }

    public int DuplicateSkippedCount { get; set; }

    public long TotalMs { get; set; }
}
