using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// 重複判定で使う chart の正規化済み行です。
/// BMS は storage row を保持しますが、bmson は <see cref="ChartFile"/> と bmson storage row を正本にして
/// compatibility adapter を作らないようにします。
/// </summary>
internal sealed class DuplicateChartRow
{
    public string Path { get; set; }

    public string DirectoryPath { get; set; }

    public string LookupHash { get; set; }

    public ChartLookupHashKind HashKind { get; set; }

    public ChartFileKind ChartKind { get; set; }

    public ChartFile Chart { get; set; }

    public BMSFile BmsFile => Chart?.BmsFile;

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
            LookupHash = chart.PrimaryLookupHash,
            HashKind = ChartLookupKey.GetPrimaryHashKind(chart),
            ChartKind = chart.Kind,
            Chart = chart,
        };
    }
}
