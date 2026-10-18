using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>BMSの親フォルダ直下にあるtxtを、LR2のテキスト付属状態へ変換します。</summary>
internal static class Lr2TextGroupResolver
{
    /// <summary>譜面の親フォルダ直下のtxtを判定します。親フォルダを取得できない場合だけ指定値を0か1に正規化して返します。</summary>
    internal static int ResolveFlag(string chartPath, int fallback = 0)
    {
        string directory = GetChartDirectory(chartPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return NormalizeFlag(fallback);
        }
        return HasDirectTextFile(directory) ? 1 : 0;
    }

    /// <summary>
    /// 物理配置成功後の保存対象について、BMSのTxtを現在の配置から確定します。
    /// 同じ保存境界の同一フォルダは一回だけ判定し、BMSONの値は変更しません。
    /// </summary>
    /// <param name="charts">物理処理が成功した保存対象。セッションでは全成功パッケージをまとめて渡します。</param>
    /// <returns>入力の順序と譜面情報を維持し、BMSのTxtだけを反映した保存値。</returns>
    internal static IReadOnlyList<ChartFile> ResolveCurrentChartFlags(IEnumerable<ChartFile> charts)
    {
        ArgumentNullException.ThrowIfNull(charts);
        var flagsByDirectory = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var resolved = new List<ChartFile>();
        foreach (ChartFile chart in charts)
        {
            if (chart?.Kind != ChartFileKind.Bms)
            {
                resolved.Add(chart);
                continue;
            }
            string directory = GetChartDirectory(chart.Path);
            if (directory == null || !flagsByDirectory.TryGetValue(directory, out int flag))
            {
                flag = ResolveFlag(chart.Path);
                if (directory != null)
                {
                    flagsByDirectory.Add(directory, flag);
                }
            }
            resolved.Add(chart with { Txt = flag });
        }
        return resolved;
    }

    private static string GetChartDirectory(string chartPath)
    {
        if (string.IsNullOrWhiteSpace(chartPath))
        {
            return null;
        }
        try
        {
            string directory = Path.GetDirectoryName(chartPath);
            return string.IsNullOrWhiteSpace(directory)
                ? null
                : LongPathFileSystem.NormalizePathForStorage(directory);
        }
        catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
        {
            return null;
        }
    }

    private static bool HasDirectTextFile(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !LongPathFileSystem.DirectoryExists(directory))
        {
            return false;
        }
        try
        {
            return LongPathFileSystem.EnumerateFiles(directory, "*.txt", SearchOption.TopDirectoryOnly).Any();
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
        {
            return false;
        }
    }

    private static int NormalizeFlag(int value)
    {
        return value == 0 ? 0 : 1;
    }
}
