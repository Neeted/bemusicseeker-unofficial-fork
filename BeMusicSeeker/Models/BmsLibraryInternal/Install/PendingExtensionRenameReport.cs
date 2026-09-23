using System;
using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Facade-independent outcome of pending extension renames.  The report keeps
/// the storage owner out of the application-facing file-operation port while
/// retaining the failure information needed by the workflow owner.
/// </summary>
internal sealed class PendingExtensionRenameReport
{
    public List<string> ChartPathsToRemove { get; } = [];

    public List<PendingExtensionRenameFailureReport> Failures { get; } = [];

    public int Total { get; set; }

    public int Renamed { get; set; }

    public int DuplicateDeleted { get; set; }

    public int Skipped { get; set; }

    public int Failed { get; set; }

    public long TotalMs { get; set; }
}

internal sealed class PendingExtensionRenameFailureReport
{
    public string FilePath { get; set; }

    public RenameInvalidExtensionOutcome Outcome { get; set; }
}
