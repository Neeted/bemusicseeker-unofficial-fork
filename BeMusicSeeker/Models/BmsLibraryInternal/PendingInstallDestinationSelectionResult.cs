using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class PendingInstallDestinationSelectionResult
{
    public bool Success { get; set; }

    public string ValidatedDestinationDirectory { get; set; }

    public List<PackageChartEntry> TargetEntries { get; } = [];

    public List<BMSFile> TargetFiles { get; } = [];

    public string WarningMessage { get; set; }
}
