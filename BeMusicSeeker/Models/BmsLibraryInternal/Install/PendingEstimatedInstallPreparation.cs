using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>同じ操作のP判定と推定先導入へ渡す、設定・開始時所持・同梱物の準備結果です。</summary>
internal sealed class PendingEstimatedInstallPreparation
{
    /// <summary>取り込み入口または手動受付で捕捉した設定です。</summary>
    internal BmsLibraryOptionsSnapshot Options { get; init; }
    /// <summary>開始時所持と順序付き候補を保持する、一回の導入バッチ計画です。</summary>
    internal PendingInstallBatchPlan Plan { get; init; }
    /// <summary>モデルguardを解放して一回列挙した元の同梱物です。</summary>
    internal IReadOnlyDictionary<string, IReadOnlyList<string>> ComponentFiles { get; init; }
    /// <summary>既存の導入分類で物理変更が必要な候補だけの実src/dstです。</summary>
    internal IReadOnlyDictionary<ChartPackage, IReadOnlyList<string>> PhysicalScopes { get; init; }
}
