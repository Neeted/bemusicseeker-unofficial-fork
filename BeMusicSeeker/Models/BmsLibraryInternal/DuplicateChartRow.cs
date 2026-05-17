using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class DuplicateChartRow
{
    public string Path { get; set; }

    public string DirectoryPath { get; set; }

    public string LookupHash { get; set; }

    public PendingChartLookupHashKind HashKind { get; set; }

    public PendingChartKind ChartKind { get; set; }

    public BMSFile DisplayRow { get; set; }

    public static DuplicateChartRow CreateFromBmsFile(BMSFile file)
    {
        if (file == null || string.IsNullOrWhiteSpace(file.path))
        {
            return null;
        }
        return new DuplicateChartRow
        {
            Path = file.path,
            DirectoryPath = DirectoryExt.GetDirectoryNameSimple(file.path),
            LookupHash = PendingChartEntry.GetPrimaryLookupHash(file),
            HashKind = PendingChartEntry.GetPrimaryLookupHashKind(file),
            ChartKind = PendingChartKind.Bms,
            DisplayRow = file
        };
    }

    public static DuplicateChartRow CreateFromBmsonSong(LR2SongDBExtended.bmson_song song)
    {
        var displayRow = PendingChartEntry.CreateFromBmsonSong(song);
        if (displayRow == null)
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
            DisplayRow = displayRow
        };
    }
}
