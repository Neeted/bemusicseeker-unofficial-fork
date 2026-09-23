namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// ChartFile から chart_info build target と grouping key を作ります。
/// BMS の md5-first digest backfill と bmson の sha256-first identity をここで分けます。
/// </summary>
internal static class ChartInfoBuildTargetMapper
{
    internal static ChartInfoBuildTarget Create(ChartFile chart)
    {
        if (chart == null)
        {
            return null;
        }

        return ChartInfoBuildTarget.FromChart(chart);
    }

    internal static string BuildKey(ChartFile chart)
    {
        if (chart?.Kind == ChartFileKind.Bms)
        {
            return BuildBmsKey(chart.Sha256, chart.Md5, chart.Path);
        }

        return BuildDefaultKey(chart?.Sha256, chart?.Md5, chart?.Path);
    }

    internal static bool ShouldSkipBackfillTarget(ChartFile chart)
    {
        return chart?.Kind == ChartFileKind.Bms
            && string.IsNullOrWhiteSpace(chart.Sha256)
            && string.IsNullOrWhiteSpace(chart.Md5);
    }

    internal static bool IsDigestBackfillTarget(ChartFile chart)
    {
        return ChartStorageOwnerMutator.HasMissingBmsSha256(chart);
    }

    internal static bool HasSingleStorageOwner(ChartFile chart)
    {
        return ChartStorageOwnerMutator.HasSingleStorageOwner(chart);
    }

    private static string BuildDefaultKey(string sha256, string md5, string path)
    {
        if (!string.IsNullOrWhiteSpace(sha256))
        {
            return "sha256:" + sha256;
        }

        if (!string.IsNullOrWhiteSpace(md5))
        {
            return "md5:" + md5;
        }

        return "path:" + (path ?? string.Empty);
    }

    private static string BuildBmsKey(string sha256, string md5, string path)
    {
        if (!string.IsNullOrWhiteSpace(md5))
        {
            return "md5:" + md5;
        }

        if (!string.IsNullOrWhiteSpace(sha256))
        {
            return "sha256:" + sha256;
        }

        return "path:" + (path ?? string.Empty);
    }
}
