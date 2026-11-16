using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>生存する親受付内で所持パスと保守値を含む楽曲行を捕捉します。後続処理はモデル行を再参照せず、確定文字コードは実読取りの同一path・内容hashへだけ適用します。</summary>
internal sealed class Lr2SongDbSyncInputRowSnapshot(IReadOnlyList<string> chartPaths, IReadOnlyList<ChartFile> songRows)
{
    public IReadOnlyList<string> ChartPaths { get; } = chartPaths ?? [];
    public IReadOnlyList<ChartFile> SongRows { get; } = songRows ?? [];
}
