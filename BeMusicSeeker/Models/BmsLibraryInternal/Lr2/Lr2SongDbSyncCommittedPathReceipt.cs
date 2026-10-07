using System;
using System.Collections.Generic;
using System.Linq;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>差分DB確定済みのBMS項目を捕捉し、直後のLR2同期一回だけで消費する短命な証票です。</summary>
internal sealed class Lr2SongDbSyncCommittedPathReceipt
{
    /// <summary>確定した対象の不変値を捕捉します。保存行・管理主体・独立した版は保持しません。</summary>
    internal Lr2SongDbSyncCommittedPathReceipt(IEnumerable<ChartFile> committedCharts)
    {
        CommittedCharts = [.. (committedCharts ?? []).Where(chart => chart?.Kind == ChartFileKind.Bms && !string.IsNullOrWhiteSpace(chart.Path))];
        CommittedBmsPaths = new HashSet<string>(CommittedCharts.Select(chart => chart.Path), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>確定対象の捕捉値です。現在値への逆参照は所有者の排他境界で行います。</summary>
    internal IReadOnlyList<ChartFile> CommittedCharts { get; }

    /// <summary>ファイル読取りを省略する確定済み物理パスです。永続化・DBからの復元はしません。</summary>
    internal IReadOnlySet<string> CommittedBmsPaths { get; }

    /// <summary>対象数に比例して、所持識別と確定時の形式・DB exact path・ハッシュだけを照合します。投影の差替えや無関係な集合変更では無効化しません。</summary>
    internal bool MatchesTargets(IReadOnlyList<ChartFile> currentCharts)
    {
        if (currentCharts == null || currentCharts.Count != CommittedCharts.Count)
        {
            return false;
        }
        for (int index = 0; index < CommittedCharts.Count; index++)
        {
            ChartFile captured = CommittedCharts[index];
            ChartFile current = currentCharts[index];
            if (captured.Token == null || current?.Token != captured.Token || current.Kind != captured.Kind
                || !string.Equals(current.Path, captured.Path, StringComparison.Ordinal)
                || !string.Equals(current.Md5, captured.Md5, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(current.Sha256, captured.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        return true;
    }
}
