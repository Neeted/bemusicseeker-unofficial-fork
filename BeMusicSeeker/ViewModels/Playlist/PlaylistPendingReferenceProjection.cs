using System.Collections.Generic;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.ViewModels;

/// <summary>新hashの捕捉位置まで既存drainを待つ間だけ、元receiptの投影と一回の捕捉を保持します。</summary>
internal sealed class PlaylistPendingReferenceProjection
{
    /// <summary>同じ元receipt全体を一度に適用する差分です。</summary>
    internal readonly List<(PlaylistDetailSourceRow Before, PlaylistDetailSourceRow After)> Changes = [];
    /// <summary>新依存だけを一回捕捉した、挿入すべき正確な境界です。</summary>
    internal PlaylistReferenceReadSnapshot Snapshot;
    /// <summary>捕捉・挿入する新しい依存keyです。nullは未捕捉を表します。</summary>
    internal HashSet<PlaylistEntryLookupKey> Keys;
    /// <summary>詳細の新依存と同時に適用する詳細位置です。</summary>
    internal int? ChartInfoVersion;
}
