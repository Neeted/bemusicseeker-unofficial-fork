using System;
using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class LibraryFixInstallationResult
{
    /// <summary>
    /// Gets or sets the single operation-scoped terminal receipt for repair moves and approved
    /// duplicate removals.
    /// </summary>
    public LibraryMutationSessionReceipt SessionReceipt { get; set; }

    /// <summary>Gets or sets the terminal failure retained by the operation receipt.</summary>
    public Exception Failure { get; set; }

    public List<LibraryDeleteFailure> Failures { get; } = [];

    public int RequestedCount { get; set; }

    public int MovedCount { get; set; }

    public int DuplicateSkippedCount { get; set; }

    /// <summary>Gets or sets the count of approved duplicate chart targets confirmed removed by this repair.</summary>
    public int ApprovedRemovedCount { get; set; }

    public long TotalMs { get; set; }

}
