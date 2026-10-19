using System.Threading;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.ViewModels;

/// <summary>最初の投影前に捕捉したlookupと、元集合へ変更キーを反映済みの位置を保持します。lookup版を全値の不変snapshotとは扱いません。</summary>
internal sealed class ChartListScoreInput
{
    private BMSLibrary.ScoreSnapshot lookup;
    private int position;

    /// <summary>filter前の元集合全体に同じlookupを接続する前に基準位置を固定します。</summary>
    internal ChartListScoreInput(BMSLibrary library)
    {
        Library = library;
        lookup = library?.CaptureScoreSnapshotForView();
        position = lookup?.Version ?? 0;
    }

    /// <summary>同じ元集合入力の寿命に属するscore所有者です。</summary>
    internal BMSLibrary Library { get; }
    /// <summary>この入力の変更キーを反映済みの位置です。読取りlookupが先行しても飛び越しません。</summary>
    internal int Position => Volatile.Read(ref position);

    /// <summary>対象行の次の実lookupに現在の参照を使います。位置は変更キーの適用後だけ進めます。</summary>
    internal void PrepareChanges() => Volatile.Write(ref lookup, Library?.CaptureScoreSnapshotForView());

    /// <summary>対象範囲へ実際に反映したrangeだけを連結します。穴のあるrangeを受理しません。</summary>
    internal bool CompleteChange(ScoreSnapshotChange change)
    {
        int current = Position;
        while (change.Version > current)
        {
            if (change.BaseVersion > current) { return false; }
            int observed = Interlocked.CompareExchange(ref position, change.Version, current);
            if (observed == current) { return true; }
            current = observed;
        }
        return true;
    }

    /// <summary>捕捉lookupから既存のBMS/BMSON規則で一行の値だけを複写します。</summary>
    internal ChartScoreSnapshot Resolve(ChartListSourceRow row) => Library == null
        ? row.CapturedChart.Score
        : Library.ResolveChartScoreSnapshot(row.Kind, row.Path, row.Hash, row.Sha256, Volatile.Read(ref lookup));
}
