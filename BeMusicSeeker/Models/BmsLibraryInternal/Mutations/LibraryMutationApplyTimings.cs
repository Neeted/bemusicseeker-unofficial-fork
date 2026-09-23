namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// ライブラリ変更処理の時間内訳を保持する診断用レポートです。
/// 操作の確定事実とこの診断レポートは意図的に分離します。
/// </summary>
internal sealed class LibraryMutationApplyTimings
{
    /// <summary>resource health変更区間の開始までに要した経過時間。</summary>
    internal long ResourceHealthBeginMs { get; set; }

    /// <summary>所持譜面変更結果の組み立てに要した経過時間。</summary>
    internal long BuildMutationMs { get; set; }

    /// <summary>所持譜面コレクション通知の公開に要した経過時間。</summary>
    internal long PublishNotificationMs { get; set; }

    /// <summary>state変更の適用に要した経過時間。</summary>
    internal long StateApplyMs { get; set; }

    /// <summary>folder DB処理に要した経過時間。</summary>
    internal long StateFolderDbMs { get; set; }

    /// <summary>pathのメモリ反映に要した経過時間。</summary>
    internal long StatePathMemoryApplyMs { get; set; }

    /// <summary>BMS path DB行の更新に要した経過時間。</summary>
    internal long StateBmsPathDbMs { get; set; }

    /// <summary>BMSON path DB行の更新に要した経過時間。</summary>
    internal long StateBmsonPathDbMs { get; set; }

    /// <summary>BMS DB行の削除に要した経過時間。</summary>
    internal long StateBmsRemovalDbMs { get; set; }

    /// <summary>BMSON DB行の削除に要した経過時間。</summary>
    internal long StateBmsonRemovalDbMs { get; set; }

    /// <summary>package stateの適用に要した経過時間。</summary>
    internal long StatePackageApplyMs { get; set; }

    /// <summary>playlist参照適用で評価したcatalog譜面数。</summary>
    internal int PlaylistReferenceAffectedCharts { get; set; }

    /// <summary>playlist参照適用で一致したcatalog譜面数。</summary>
    internal int PlaylistReferenceMatchedCharts { get; set; }

    /// <summary>resource health変更区間の破棄に要した経過時間。</summary>
    internal long ResourceHealthDisposeMs { get; set; }

    /// <summary>LR2通常folder同期に要した経過時間。</summary>
    internal long Lr2NormalFolderSyncMs { get; set; }

    /// <summary>所持譜面変更のdispatchに要した経過時間。</summary>
    internal long DispatchMs { get; set; }

    /// <summary>処理全体の経過時間。</summary>
    internal long ElapsedMs { get; set; }
}
