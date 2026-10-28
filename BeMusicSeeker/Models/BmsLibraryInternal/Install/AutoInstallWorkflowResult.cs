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
    /// <summary>展開後の実入力。適用直前の存在確認に使い、正常消費された元アーカイブは含めません。</summary>
    internal List<string> ExpandedInstallPaths { get; set; }
    /// <summary>副作用前に固定した実宛先。名前決定と交差判断を実行時に別入力でやり直しません。</summary>
    internal Dictionary<ChartPackage, string> PreparedDestinations { get; } = [];
    /// <summary>P競合で物理変更を開始しない対象。登録済み事実と他対象の成功を維持します。</summary>
    internal HashSet<ChartPackage> BusyPackages { get; } = [];

    public List<ChartPackage> DiscoveredPackages { get; } = [];

    /// <summary>不完全scopeの元原因です。正常な独立対象を処理してから既存の終端報告へ渡します。</summary>
    internal List<Exception> DiscoveryFailures { get; } = [];

    public List<ChartPackage> PendingPackagesToRemove { get; } = [];

    /// <summary>完全に読めた新directoryと旧保留群です。採用時だけ同じsessionで登録を置換します。</summary>
    internal Dictionary<ChartPackage, List<ChartPackage>> PendingPackageReplacements { get; } = [];

    public List<ChartPackage> PendingPackagesToAdd { get; } = [];

    public List<ChartPackage> AutoInstallCandidates { get; } = [];

    public long DiscoveryMs { get; set; }

    public long ClassificationMs { get; set; }

    public long InstalledCheckMs { get; set; }

    public long WarningClassificationMs { get; set; }

    public long TotalMs { get; set; }
}
