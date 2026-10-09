using System;
using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class AutoInstallWorkflowResult
{
    /// <summary>受理操作が一回捕捉した設定。準備・物理実行・推定で同じ値を使います。</summary>
    internal BmsLibraryOptionsSnapshot OptionsSnapshot { get; set; }
    /// <summary>準備時の保留集合と所持索引。短いモデル保護から切り離して再利用します。</summary>
    internal List<ChartPackage> PendingPackageSnapshot { get; set; }
    internal InstalledChartLookupIndexSnapshot InstalledChartLookup { get; set; }
    /// <summary>展開後の入力。受理操作が使用・回収の実終端まで所有します。</summary>
    internal List<string> ExpandedInstallPaths { get; set; }
    internal List<Action> PreparationDiagnostics { get; } = [];
    /// <summary>副作用前に固定した実宛先。名前決定と交差判断を実行時に別入力でやり直しません。</summary>
    internal Dictionary<ChartPackage, string> PreparedDestinations { get; } = [];
    /// <summary>P競合で物理変更を開始しない対象。登録済み事実と他対象の成功を維持します。</summary>
    internal HashSet<ChartPackage> BusyPackages { get; } = [];

    public List<ChartPackage> DiscoveredPackages { get; } = [];

    public List<string> RegroupEligibleSourceDirectories { get; } = [];

    public List<ChartPackage> PendingPackagesToRemove { get; } = [];

    public List<ChartPackage> PendingPackagesToAdd { get; } = [];

    public List<ChartPackage> AutoInstallCandidates { get; } = [];

    public List<string> ExtractedTempDirectories { get; } = [];

    public long DiscoveryMs { get; set; }

    public long ClassificationMs { get; set; }

    public long InstalledCheckMs { get; set; }

    public long WarningClassificationMs { get; set; }

    public long TotalMs { get; set; }
}
