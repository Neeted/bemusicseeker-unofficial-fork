using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class InstallTableLoadResult
{
    public List<ChartPackage> PendingPackages { get; } = [];

    /// <summary>この復元試行の初期DBsnapshotが保持する解除元の全生キーです。新登録と同じtransactionでexact削除します。</summary>
    internal List<string> InstallPathsToDelete { get; } = [];

    /// <summary>一度の探索で得た登録置換先です。自動推定や導入は開始しません。</summary>
    internal List<ChartPackage> InstallRowsToUpsert { get; } = [];

    public int InstalledWarningCount { get; set; }

    public int SingleFileWarningCount { get; set; }

    public int StrictWarningCount { get; set; }

    public long LoadMs { get; set; }

    public long WarningInitMs { get; set; }

    public long TotalMs { get; set; }
}
