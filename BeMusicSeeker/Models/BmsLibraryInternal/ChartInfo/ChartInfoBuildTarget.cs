using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// chart_info build が処理する chart storage owner の集合です。
/// build service 本体から BMS / bmson owner mutation の分岐を切り離す境界です。
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

    internal bool NeedsDigest => charts.Any(ChartStorageOwnerMutator.HasMissingBmsSha256);

    internal int MissingDigestOwnerCount => charts.Count(ChartStorageOwnerMutator.HasMissingBmsSha256);

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

    internal int ApplyDigest(
        string sha256,
        ICollection<BMSFile> completedDigestFiles,
        ICollection<LibraryChartDigestChange> digestChanges = null)
    {
        if (string.IsNullOrWhiteSpace(sha256))
        {
            return 0;
        }

        int applied = 0;
        foreach (ChartFile chart in charts)
        {
            applied += ChartStorageOwnerMutator.ApplyMissingBmsSha256(chart, sha256, completedDigestFiles, digestChanges);
        }

        return applied;
    }

    /// <summary>
    /// Creates update-only chart-info song projections for every BMS owner represented by this target.
    /// BMSON owners deliberately do not materialize LR2 compatibility rows.
    /// </summary>
    internal IReadOnlyList<Lr2ChartInfoSongProjection> CreateBmsChartInfoSongProjections(
        LR2SongDBExtended.chart_info row)
    {
        var result = new List<Lr2ChartInfoSongProjection>();
        foreach (ChartFile chart in charts)
        {
            Lr2ChartInfoSongProjection projection =
                ChartStorageOwnerMutator.CreateBmsChartInfoSongProjection(chart, row);
            if (projection == null)
            {
                continue;
            }
            result.Add(projection);
        }
        return result;
    }

    /// <summary>
    /// Projects durable chart-info generated columns to every canonical BMS owner.
    /// This is called only after the catalog transaction returns a successful receipt.
    /// </summary>
    internal int ApplyCommittedChartInfo(
        LR2SongDBExtended.chart_info row,
        IReadOnlySet<Lr2ChartInfoSongProjectionIdentity> matchedIdentities)
    {
        if (row == null)
        {
            return 0;
        }

        int applied = 0;
        foreach (ChartFile chart in charts)
        {
            applied += ChartStorageOwnerMutator.ApplyCommittedBmsChartInfoProjection(
                chart,
                row,
                matchedIdentities);
        }
        return applied;
    }

}
