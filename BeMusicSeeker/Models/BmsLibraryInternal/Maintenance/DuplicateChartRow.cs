using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// 重複判定で使う chart の正規化済み行です。
/// 捕捉した共通値と所持識別を保持し、保存行へ遡りません。
/// </summary>
internal sealed class DuplicateChartRow
{
    public string Path { get; private set; }

    public string DirectoryPath { get; private set; }

    public string LookupHash { get; private set; }

    public ChartLookupHashKind HashKind { get; private set; }

    public ChartFileKind ChartKind { get; private set; }

    public ChartFile Chart { get; private set; }

    internal bool HasChartSource => Chart != null;

    public static DuplicateChartRow CreateFromChart(ChartFile chart)
    {
        if (chart == null || string.IsNullOrWhiteSpace(chart.Path))
        {
            return null;
        }
        return new DuplicateChartRow
        {
            Path = chart.Path,
            DirectoryPath = DirectoryExt.GetDirectoryNameSimple(chart.Path),
            LookupHash = ChartLookupKey.GetPrimaryHash(chart),
            HashKind = ChartLookupKey.GetPrimaryHashKind(chart),
            ChartKind = chart.Kind,
            Chart = chart,


        };
    }

    internal ChartFile CreateChart() => Chart;

    internal DuplicateChartRow WithPath(string path)
    {
        return new DuplicateChartRow
        {
            Path = path,
            DirectoryPath = DirectoryExt.GetDirectoryNameSimple(path),
            LookupHash = LookupHash,
            HashKind = HashKind,
            ChartKind = ChartKind,
            Chart = Chart with { Path = path },
        };
    }

    internal DuplicateChartRow WithMd5(string md5)
    {
        return new DuplicateChartRow
        {
            Path = Path,
            DirectoryPath = DirectoryPath,
            LookupHash = SelectPrimaryHash(md5),
            HashKind = SelectPrimaryHashKind(md5),
            ChartKind = ChartKind,
            Chart = Chart with { Md5 = md5 },
        };
    }

    private static string SelectPrimaryHash(string md5)
    {
        return string.IsNullOrWhiteSpace(md5) ? null : md5.Trim();
    }

    private static ChartLookupHashKind SelectPrimaryHashKind(string md5)
    {
        if (!string.IsNullOrWhiteSpace(md5))
        {
            return ChartLookupHashKind.Md5;
        }
        return ChartLookupHashKind.None;
    }
}
