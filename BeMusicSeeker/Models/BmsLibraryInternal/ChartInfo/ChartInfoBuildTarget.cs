using System.Collections.Generic;
using System.Linq;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// 同じ内容の詳細解析を共有する捕捉済み共通譜面の集合です。現在値や保存行は変更しません。
/// </summary>
internal sealed class ChartInfoBuildTarget
{
    private readonly List<ChartFile> charts = [];

    private ChartInfoBuildTarget(ChartFileKind kind, string path, string md5, string sha256)
    {
        Kind = kind;
        Path = path;
        Md5 = md5;
        Sha256 = sha256;
    }

    internal ChartFileKind Kind { get; }

    internal string Path { get; }

    internal string Md5 { get; }

    internal string Sha256 { get; }

    internal bool NeedsDigest => charts.Any(HasMissingDigest);

    internal int MissingDigestOwnerCount => charts.Count(HasMissingDigest);

    internal int OwnerCount => charts.Count;

    internal static ChartInfoBuildTarget FromChart(ChartFile chart)
    {
        if (chart == null)
        {
            return null;
        }

        var target = new ChartInfoBuildTarget(
            chart.Kind,
            chart.Path,
            chart.Md5,
            chart.Sha256);
        target.AddChart(chart);
        return target;
    }

    internal void AddChart(ChartFile chart)
    {
        if (chart != null)
        {
            charts.Add(chart);
        }
    }

    private static bool HasMissingDigest(ChartFile chart)
        => chart?.Kind == ChartFileKind.Bms && string.IsNullOrWhiteSpace(chart.Sha256);

    /// <summary>確定したSHA256の旧新事実を返します。捕捉した譜面を変更しません。</summary>
    internal int CreateDigestChanges(string sha256, ICollection<LibraryChartDigestChange> changes)
    {
        if (string.IsNullOrWhiteSpace(sha256))
        {
            return 0;
        }

        int count = 0;
        foreach (ChartFile chart in charts.Where(HasMissingDigest))
        {
            changes?.Add(new LibraryChartDigestChange(ChartFileKind.Bms, chart.Path, chart.Md5, chart.Sha256, chart.Md5, sha256));
            count++;
        }
        return count;
    }

    /// <summary>既存BMS保存行のパスとMD5へ限定する詳細列の更新要求を返します。</summary>
    internal IReadOnlyList<Lr2ChartInfoSongProjection> CreateBmsChartInfoSongProjections(ChartDetails row)
        => [.. charts.Where(chart => chart.Kind == ChartFileKind.Bms)
            .Select(chart => Lr2ChartInfoSongProjection.Create(chart.Path, chart.Md5, row)).Where(value => value != null)];

    /// <summary>詳細を適用する共通値を返します。基本レベルはDBで同じパスとMD5が一致した項目だけへ適用します。</summary>
    internal IReadOnlyList<ChartFile> CreateCommittedChartInfoValues(ChartDetails row,
        IReadOnlySet<Lr2ChartInfoSongProjectionIdentity> matchedIdentities)
        => row == null ? [] : [.. charts.Select(chart =>
        {
            ChartFile next = ChartFileProjection.WithChartInfo(chart, row);
            Lr2ChartInfoSongProjection projection = chart.Kind == ChartFileKind.Bms
                ? Lr2ChartInfoSongProjection.Create(chart.Path, chart.Md5, row) : null;
            return projection != null && matchedIdentities?.Contains(projection.Identity) == true
                ? next with { Level = row.level, LevelText = row.level?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                    Difficulty = projection.Difficulty } : next;
        })];
}
