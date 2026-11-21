namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>分裂配置の探索の段階です。</summary>
internal enum SplitPlacementSearchStage
{
    /// <summary>入力の整理、プレビュー名と汎用名の除外、索引の構築。</summary>
    Prepare,

    /// <summary>候補フォルダごとの共有数の集計と関係の判定。</summary>
    Match,
}

/// <summary>
/// 探索の進捗です。<see cref="SplitPlacementSearchStage.Match"/> では、照合を終えた候補フォルダの数と候補フォルダの総数を表します。
/// <see cref="SplitPlacementSearchStage.Prepare"/> の数は、対象フォルダの整理済み数と対象フォルダの総数です。
/// </summary>
internal readonly record struct SplitPlacementSearchProgress(SplitPlacementSearchStage Stage, int Completed, int Total);
