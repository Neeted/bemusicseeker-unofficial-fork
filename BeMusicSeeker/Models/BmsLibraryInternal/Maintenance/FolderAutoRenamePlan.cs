using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>正規化済みの移動元・先と計画時の失敗を保持します。適用時には宛先を再計算しません。</summary>
internal sealed class FolderAutoRenamePlan
{
    /// <summary>計画時に確定した移動元です。</summary>
    public string SourceDirectory { get; init; }

    /// <summary>正規化と衝突回避を終えた移動先です。変更不要では未設定です。</summary>
    public string DestinationDirectory { get; init; }

    /// <summary>実移動へ渡さず診断として公開する計画時の失敗です。</summary>
    public Exception FailureException { get; init; }
}

/// <summary>診断・進捗用の全計画と実行可能な移動を一回確定します。同じ実移動だけをPの範囲、停止の判断、物理作用へ渡します。</summary>
internal sealed class FolderMoveBatch
{
    private readonly HashSet<FolderAutoRenamePlan> physicalMoves;

    /// <summary>現在の受付内で実FSの事前条件を確認します。除外した計画を適用時に実移動へ戻しません。</summary>
    internal FolderMoveBatch(IEnumerable<FolderAutoRenamePlan> plans)
    {
        Plans = Array.AsReadOnly([.. (plans ?? []).Where(plan => plan != null)]);
        Moves = Array.AsReadOnly([.. Plans.Where(plan => plan.FailureException == null
            && !string.IsNullOrWhiteSpace(plan.SourceDirectory)
            && !string.IsNullOrWhiteSpace(plan.DestinationDirectory)
            && !string.Equals(plan.SourceDirectory, plan.DestinationDirectory, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(Path.GetPathRoot(plan.SourceDirectory), plan.SourceDirectory, StringComparison.OrdinalIgnoreCase)
            && LongPathFileSystem.DirectoryExists(plan.SourceDirectory)
            && (string.IsNullOrEmpty(Path.GetDirectoryName(plan.DestinationDirectory))
                || LongPathFileSystem.DirectoryExists(Path.GetDirectoryName(plan.DestinationDirectory)))
            && !LongPathFileSystem.EntryExists(plan.DestinationDirectory))]);
        physicalMoves = [.. Moves];
    }

    /// <summary>失敗・変更不要を含む、進捗と診断の対象です。</summary>
    internal IReadOnlyList<FolderAutoRenamePlan> Plans { get; }

    /// <summary>停止前に確定した実移動です。宛先と対象を適用側で再計算しません。</summary>
    internal IReadOnlyList<FolderAutoRenamePlan> Moves { get; }

    /// <summary>全計画の反復中に、診断対象を実移動へ戻さず定数時間で判定します。</summary>
    internal bool ContainsPhysicalMove(FolderAutoRenamePlan plan) => physicalMoves.Contains(plan);
}
