using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Tests;

/// <summary>各試験が必要な基本値を明示して構成するための不変fixtureです。</summary>
internal static class ChartTestValues
{
    internal static ChartFile Empty(ChartFileKind kind = ChartFileKind.Bms)
        => new(kind, null, null, null, string.Empty, string.Empty, string.Empty, string.Empty,
            null, null, string.Empty, null, null, null);

    /// <summary>表示・検索の試験が指定したスコアだけを不変値として捕捉します。</summary>
    internal static ChartScoreSnapshot Score(ClearType clear, RankType rank, int perfect, int great,
        int totalnotes, int maxcombo, int minbp, int opHistory = 0, bool useLr2ScoreValue = false)
    {
        var score = new BMSScore
        {
            clear = clear,
            rank = rank,
            perfect = perfect,
            great = great,
            totalnotes = totalnotes,
            maxcombo = maxcombo,
            minbp = minbp,
            op_history = opHistory
        };
        if (useLr2ScoreValue)
        {
            score.clearValue = ClearTypeStorageConverter.ToLr2Value(clear);
        }
        return ChartScoreSnapshot.FromBmsScore(score, "fixture.bms");
    }

    internal static IEnumerable<ChartFile> Combine(IEnumerable<ChartFile> bms, IEnumerable<ChartFile> bmson)
        => (bms ?? []).Concat(bmson ?? []);

    internal static ChartFile ReadBmson(string path)
        => BmsonChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(path));

    /// <summary>DB境界の試験で、既存の保存行に残るparent列を明示して準備します。</summary>
    internal static LR2SongDB.song CreateBmsStorageRow(ChartFile chart, string parent)
    {
        LR2SongDB.song row = ChartSongStorageMapping.ToBmsRow(chart);
        row.parent = parent;
        return row;
    }

    /// <summary>DB境界を検証する試験だけに、ファイル解析から生成列の保存行を渡します。</summary>
    internal static LR2SongDB.song ReadBmsRow(string path)
        => ChartSongStorageMapping.ToBmsRow(BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(path)));
}
