using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class PendingInstallDestinationSelectionResult
{
    /// <summary>検証時に解決した保留パッケージです。独立した所持譜面ではnullです。</summary>
    internal ChartPackage Package { get; set; }

    public bool Success { get; set; }

    public string ValidatedDestinationDirectory { get; set; }

    public List<PackageChartEntry> TargetEntries { get; } = [];

    public string WarningMessage { get; set; }
}
