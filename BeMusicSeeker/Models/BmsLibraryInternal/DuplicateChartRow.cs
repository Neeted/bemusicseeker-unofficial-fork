using BeMusicSeeker.Models.LR2;
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

    internal BMSFile BmsFile { get; set; }

    internal LR2SongDBExtended.bmson_song BmsonSong { get; set; }

    internal bool HasChartSource => Chart != null || BmsFile != null || BmsonSong != null;

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
            BmsFile = chart.GetBmsStorageOwner(),
            BmsonSong = chart.GetBmsonStorageOwner(),
        };
    }

    internal static DuplicateChartRow CreateFromBmsFile(BMSFile file)
    {
        if (file == null || string.IsNullOrWhiteSpace(file.path))
        {
            return null;
        }

        return new DuplicateChartRow
        {
            Path = file.path,
            DirectoryPath = DirectoryExt.GetDirectoryNameSimple(file.path),
            LookupHash = SelectPrimaryHash(file.hash),
            HashKind = SelectPrimaryHashKind(file.hash),
            ChartKind = ChartFileKind.Bms,
            BmsFile = file,
        };
    }

    internal static DuplicateChartRow CreateFromBmsonSong(LR2SongDBExtended.bmson_song song)
    {
        if (song == null || string.IsNullOrWhiteSpace(song.path))
        {
            return null;
        }

        return new DuplicateChartRow
        {
            Path = song.path,
            DirectoryPath = DirectoryExt.GetDirectoryNameSimple(song.path),
            LookupHash = SelectPrimaryHash(song.md5),
            HashKind = SelectPrimaryHashKind(song.md5),
            ChartKind = ChartFileKind.Bmson,
            BmsonSong = song,
        };
    }

    internal ChartFile CreateChart()
    {
        if (Chart != null)
        {
            return Chart;
        }
        if (BmsFile != null)
        {
            Chart = ChartFileProjection.FromBmsStorageOwnerIdentity(BmsFile);
        }
        else if (BmsonSong != null)
        {
            Chart = ChartFileProjection.FromBmsonStorageOwnerIdentity(BmsonSong);
        }
        return Chart;
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
