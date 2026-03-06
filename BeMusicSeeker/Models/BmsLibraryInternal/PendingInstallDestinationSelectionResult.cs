using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class PendingInstallDestinationSelectionResult
{
    public bool Success { get; set; }

    public string ValidatedDestinationDirectory { get; set; }

    public List<BMSFile> TargetFiles { get; } = new List<BMSFile>();

    public string WarningMessage { get; set; }
}
