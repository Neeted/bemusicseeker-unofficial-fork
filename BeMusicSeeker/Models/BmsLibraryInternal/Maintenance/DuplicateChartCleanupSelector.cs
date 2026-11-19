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
    /// 残すのは更新日時が古い譜面で、同日時ならファイル名が短い譜面です。更新日時を読めない譜面は最も新しいものとして扱います。
    /// 同点（同日時かつ同じ名前の長さ）は列挙順に従い、順序を契約として保証しません。
    /// 主ハッシュを持たない譜面と、同じ主ハッシュが一件だけの譜面は対象に含めません。
    /// 主ハッシュごとに必ず一件を残すため、全件を削除対象にすることはありません。
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
            ChartFile keeper = grouped
                .OrderBy(chart =>
                {
                    try
                    {
                        return LongPathFileSystem.GetLastWriteTime(chart.Path, isDirectory: false);
                    }
                    catch
                    {
                        return DateTime.MaxValue;
                    }
                })
                .ThenBy(chart => Path.GetFileName(chart.Path).Length)
                .First();
            deletionList.AddRange(grouped.Where(chart => !ReferenceEquals(chart, keeper)));
        }
        return deletionList;
    }
}
