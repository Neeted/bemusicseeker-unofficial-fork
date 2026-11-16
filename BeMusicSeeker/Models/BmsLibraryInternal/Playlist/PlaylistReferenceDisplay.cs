using System;
using System.Collections.Generic;
using System.Linq;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>確定header順から生成した不変の参照表示です。空表示とbucket不在の区別は索引のkey存在が保持します。</summary>
internal sealed class PlaylistReferenceDisplay
{
    internal static readonly PlaylistReferenceDisplay Empty = new([]);

    /// <summary>同じ確定順のsymbol/nameを空値も含めて表示文字列へ投影します。</summary>
    internal PlaylistReferenceDisplay(IReadOnlyList<PlaylistReferenceTableDisplaySnapshot> snapshots)
    {
        IReadOnlyList<PlaylistReferenceTableDisplaySnapshot> sourceSnapshots = snapshots ?? [];
        Symbols = string.Join(" ", sourceSnapshots.Where(snapshot => snapshot != null).Select(snapshot => snapshot.Symbol));
        string names = string.Join(Environment.NewLine, sourceSnapshots.Where(snapshot => snapshot != null).Select(snapshot => snapshot.Name));
        Names = string.IsNullOrWhiteSpace(names) ? string.Empty : names;
    }

    internal string Symbols { get; }

    internal string Names { get; }
}
