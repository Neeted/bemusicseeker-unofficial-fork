using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class PendingInstallBatchPlan
{
    /// <summary>
    /// 保留照合と入力順重複排除、DST 適格性確認を通過した候補です。
    /// </summary>
    public List<ChartPackage> SelectedPendingPackages { get; } = [];

    /// <summary>
    /// 所有 chart が既存で、resource 移動も発生しない候補です。
    /// 実行時に component snapshot を確認して追加されます。
    /// </summary>
    public List<ChartPackage> CleanupOnlyCandidates { get; } = [];

    /// <summary>
    /// 開始時所持 hash と durable commit 済み hash を逐次保持する lookup です。
    /// hash の追記は低層の確定処理からのみ行います。
    /// </summary>
    public IMutablePrimaryHashLookup MoveGuardLookup { get; set; } = new PrimaryHashSetLookup();

    /// <summary>保留照合、入力順 dedup、DST 適格性確認に要した時間です。</summary>
    public long FilterMs { get; set; }

    /// <summary>候補 plan の構築全体に要した時間です。</summary>
    public long PlanBuildMs { get; set; }

    /// <summary>実行段階へ渡した pending package 数です。</summary>
    public int SelectedPendingCount { get; set; }

    /// <summary>実行時に resource 移動なしと判定された cleanup-only 候補数です。</summary>
    public int CleanupOnlyCandidateCount { get; set; }

    /// <summary>DST が未確定のまま保留した候補数です。</summary>
    public int DeferredManualHoldCount { get; set; }
}
