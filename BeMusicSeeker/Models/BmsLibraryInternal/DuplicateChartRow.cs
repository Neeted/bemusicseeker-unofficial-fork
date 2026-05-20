using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// 重複判定で使う chart の正規化済み行です。
/// BMS は storage row を保持しますが、bmson は <see cref="ChartFile"/> と <see cref="LR2SongDBExtended.bmson_song"/> を正本にして
/// compatibility adapter を作らないようにします。
/// </summary>
internal sealed class DuplicateChartRow
{
    public BMSFile BmsFile { get; set; }

    public string Path { get; set; }

    public string DirectoryPath { get; set; }

    public string LookupHash { get; set; }

    public PendingChartLookupHashKind HashKind { get; set; }

    public PendingChartKind ChartKind { get; set; }

    public ChartFile Chart { get; set; }

    public static DuplicateChartRow CreateFromBmsFile(BMSFile file)
    {
        if (file == null || string.IsNullOrWhiteSpace(file.path))
        {
            return null;
        }
        return new DuplicateChartRow
        {
            BmsFile = file,
            Path = file.path,
            DirectoryPath = DirectoryExt.GetDirectoryNameSimple(file.path),
            LookupHash = PendingChartEntry.GetPrimaryLookupHash(file),
            HashKind = PendingChartEntry.GetPrimaryLookupHashKind(file),
            ChartKind = PendingChartKind.Bms,
            Chart = ChartFileProjection.FromBmsFile(file),
        };
    }

    public static DuplicateChartRow CreateFromBmsonSong(LR2SongDBExtended.bmson_song song)
    {
        if (song == null || string.IsNullOrWhiteSpace(song.path))
        {
            return null;
        }
        ChartFile chart = ChartFileProjection.FromBmsonSong(song);
        if (chart == null)
        {
            return null;
        }
        return new DuplicateChartRow
        {
            Path = song.path,
            DirectoryPath = DirectoryExt.GetDirectoryNameSimple(song.path),
            LookupHash = PendingChartEntry.GetPrimaryLookupHash(song),
            HashKind = PendingChartEntry.GetPrimaryLookupHashKind(song),
            ChartKind = PendingChartKind.Bmson,
            Chart = chart
        };
    }
}
