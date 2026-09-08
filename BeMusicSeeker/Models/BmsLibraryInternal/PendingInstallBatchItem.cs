using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class PendingInstallBatchItem
{
    /// <summary>確定対象の元 pending package です。</summary>
    public ChartPackage OriginalPackage { get; set; }

    /// <summary>現在の所持 hash を除外して作成した一件分の work package です。</summary>
    public ChartPackage InstallWorkPackage { get; set; }

    /// <summary>この work package の推定導入先です。</summary>
    public string DestinationDirectory { get; set; }

    /// <summary>resource 移動だけを行う候補かどうかです。</summary>
    public bool IsResourceOnlyInstall { get; set; }

    /// <summary>work package から除外する既存 chart/component の path です。</summary>
    public HashSet<string> ExcludedComponentPaths { get; set; }
}
