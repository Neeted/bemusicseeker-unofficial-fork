using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// 重複判定で使う chart の正規化済み行です。
/// BMS / bmson は storage row を正本にし、必要な時だけ <see cref="ChartFile"/> projection を作ります。
/// </summary>
internal sealed class DuplicateChartRow
{
    public string Path { get; private set; }

    public string DirectoryPath { get; private set; }

    public string LookupHash { get; private set; }

    public ChartLookupHashKind HashKind { get; private set; }

    public ChartFileKind ChartKind { get; private set; }

    public ChartFile Chart { get; private set; }

    internal BMSFile BmsFile { get; private set; }

    internal LR2SongDBExtended.bmson_song BmsonSong { get; private set; }

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
            return ChartFileProjection.FromBmsStorageOwnerIdentity(BmsFile);
        }
        if (BmsonSong != null)
        {
            return ChartFileProjection.FromBmsonStorageOwnerIdentity(BmsonSong);
        }
        return null;
    }

    internal DuplicateChartRow WithPath(string path)
    {
        return new DuplicateChartRow
        {
            Path = path,
            DirectoryPath = DirectoryExt.GetDirectoryNameSimple(path),
            LookupHash = LookupHash,
            HashKind = HashKind,
            ChartKind = ChartKind,
            Chart = Chart,
            BmsFile = BmsFile,
            BmsonSong = BmsonSong,
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
            Chart = Chart,
            BmsFile = BmsFile,
            BmsonSong = BmsonSong,
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
