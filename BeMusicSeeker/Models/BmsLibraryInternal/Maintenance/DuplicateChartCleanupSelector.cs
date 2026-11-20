using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// 同じフォルダ内に同じ主ハッシュの譜面が複数あるとき、削除する譜面を選ぶ規則です。
/// 単体の同一フォルダ内の重複整理と、重複グループの一括整理が同じ実装を使い、選定を分岐させません。
/// </summary>
internal static class DuplicateChartCleanupSelector
{
    /// <summary>
    /// 主ハッシュごとに一件だけを残し、残りを削除対象として返します。
    /// 同じ主ハッシュの譜面が二件以上あるときだけ、各譜面の実ファイルの実在と更新日時を確認します。
    /// 実在を確認できない譜面（ファイルの欠落、属性取得の失敗、パスがファイルではないもの）は、残す一件にも削除対象にも選びません。
    /// 確認できた譜面のうち更新日時が古い譜面を残し、同日時ならファイル名が短い譜面を残します。残す一件以外の確認できた譜面だけを削除対象にします。
    /// 確認できた譜面が二件に満たない主ハッシュは削除対象を持ちません。実在する唯一のコピーを削除対象にしないためです。
    /// 同点（同日時かつ同じ名前の長さ）は列挙順に従い、順序を契約として保証しません。
    /// 主ハッシュを持たない譜面と、同じ主ハッシュが一件だけの譜面は、ファイルを確認せず対象に含めません。
    /// 主ハッシュごとの判定は独立しており、確認できた譜面から必ず一件を残すため、確認できた譜面を全件削除対象にすることはありません。
    /// </summary>
    /// <param name="charts">同じフォルダ直下の譜面。</param>
    /// <returns>削除する譜面。入力の同一インスタンスだけを返します。</returns>
    internal static List<ChartFile> SelectDeletions(IEnumerable<ChartFile> charts)
    {
        var deletionList = new List<ChartFile>();
        foreach (var hashGroup in (charts ?? [])
            .Select(chart => new { Chart = chart, LookupHash = ChartLookupKey.GetPrimaryHash(chart) })
            .Where(item => !string.IsNullOrWhiteSpace(item.LookupHash))
            .GroupBy(item => item.LookupHash, StringComparer.OrdinalIgnoreCase))
        {
            List<ChartFile> grouped = [.. hashGroup.Select(item => item.Chart)];
            if (grouped.Count <= 1)
            {
                continue;
            }
            List<(ChartFile Chart, DateTime LastWriteTimeUtc)> confirmed = [];
            foreach (ChartFile chart in grouped)
            {
                if (TryConfirmFile(chart.Path, out DateTime lastWriteTimeUtc))
                {
                    confirmed.Add((chart, lastWriteTimeUtc));
                }
            }
            if (confirmed.Count <= 1)
            {
                continue;
            }
            ChartFile keeper = confirmed
                .OrderBy(item => item.LastWriteTimeUtc)
                .ThenBy(item => Path.GetFileName(item.Chart.Path).Length)
                .First().Chart;
            deletionList.AddRange(confirmed.Where(item => !ReferenceEquals(item.Chart, keeper)).Select(item => item.Chart));
        }
        return deletionList;
    }

    /// <summary>
    /// 実ファイルの実在と更新日時を一回の属性取得で確認します。
    /// 欠落した場合に例外にならず古い日時を返す取得は使いません。取得に失敗した場合は、欠落と区別せず確認できなかったものとして扱います。
    /// </summary>
    private static bool TryConfirmFile(string path, out DateTime lastWriteTimeUtc)
    {
        try
        {
            lastWriteTimeUtc = LongPathFileSystem.GetFileMetadata(path).LastWriteTimeUtc;
            return true;
        }
        catch
        {
            lastWriteTimeUtc = default;
            return false;
        }
    }
}
