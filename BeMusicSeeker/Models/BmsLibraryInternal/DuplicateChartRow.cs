using System;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class DuplicateChartRow
{
    private readonly Func<BMSFile> displayRowProvider;

    private BMSFile displayRow;

    public string Path { get; set; }

    public string DirectoryPath { get; set; }

    public string LookupHash { get; set; }

    public PendingChartLookupHashKind HashKind { get; set; }

    public PendingChartKind ChartKind { get; set; }

    public ChartFile Chart { get; set; }

    public BMSFile GetOrCreateDisplayRow()
    {
        if (displayRow == null)
        {
            displayRow = displayRowProvider?.Invoke();
        }
        return displayRow;
    }

    private DuplicateChartRow(BMSFile displayRow, Func<BMSFile> displayRowProvider)
    {
        this.displayRow = displayRow;
        this.displayRowProvider = displayRowProvider;
    }

    public static DuplicateChartRow CreateFromBmsFile(BMSFile file)
    {
        if (file == null || string.IsNullOrWhiteSpace(file.path))
        {
            return null;
        }
        return new DuplicateChartRow(file, null)
        {
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
        return new DuplicateChartRow(null, () => PendingChartEntry.CreateFromBmsonSong(song))
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
