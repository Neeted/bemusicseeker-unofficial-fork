using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>親受付内で捕捉した楽曲行とパスを後段へ渡します。因果を推測する全体版を保持しません。</summary>
/// <summary>生存する親受付内で捕捉した所持パスと楽曲行の値です。後続処理はモデル行を再参照しません。</summary>
internal sealed class Lr2SongDbSyncInputRowSnapshot(IReadOnlyList<string> chartPaths, IReadOnlyList<ChartFile> songRows)
{
    public IReadOnlyList<string> ChartPaths { get; } = chartPaths ?? [];
    public IReadOnlyList<ChartFile> SongRows { get; } = songRows ?? [];
}
