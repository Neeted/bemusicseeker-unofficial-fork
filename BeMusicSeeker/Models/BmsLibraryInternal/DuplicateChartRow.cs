using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Models.LR2;

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
            LookupHash = chart.PrimaryLookupHash,
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
            LookupHash = SelectPrimaryHash(file.hash, file.sha256),
            HashKind = SelectPrimaryHashKind(file.hash, file.sha256),
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
            LookupHash = SelectPrimaryHash(song.md5, song.sha256),
            HashKind = SelectPrimaryHashKind(song.md5, song.sha256),
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

    private static string SelectPrimaryHash(string md5, string sha256)
    {
        return !string.IsNullOrWhiteSpace(md5)
            ? md5.Trim()
            : string.IsNullOrWhiteSpace(sha256) ? null : sha256.Trim();
    }

    private static ChartLookupHashKind SelectPrimaryHashKind(string md5, string sha256)
    {
        if (!string.IsNullOrWhiteSpace(md5))
        {
            return ChartLookupHashKind.Md5;
        }
        return string.IsNullOrWhiteSpace(sha256) ? ChartLookupHashKind.None : ChartLookupHashKind.Sha256;
    }
}
