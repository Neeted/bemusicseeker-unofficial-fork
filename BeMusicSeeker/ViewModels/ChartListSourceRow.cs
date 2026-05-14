using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.ViewModels;

internal sealed class ChartListSourceRow
{
    private readonly string bmsonTitle;

    private readonly string bmsonFolder;

    private readonly string bmsonPath;

    private readonly int? bmsonMode;

    private ChartListSourceRow(BMSFile bmsFile, LR2SongDBExtended.bmson_song bmsonSong)
    {
        BmsFile = bmsFile;
        BmsonSong = bmsonSong;
        bmsonTitle = BmsonSongParser.ComposeDisplayTitle(bmsonSong);
        bmsonFolder = BmsonSongParser.ComposeDisplayFolder(bmsonSong);
        bmsonPath = bmsonSong?.path ?? string.Empty;
        bmsonMode = BmsonSongParser.ResolvePlaylistMode(bmsonSong?.mode_hint);
    }

    internal BMSFile BmsFile { get; }

    internal LR2SongDBExtended.bmson_song BmsonSong { get; }

    internal string Title => BmsFile?.Title ?? bmsonTitle;

    internal string Artist => BmsFile?.Artist ?? BmsonSong?.artist ?? string.Empty;

    internal string Genre => BmsFile?.genre ?? BmsonSong?.genre ?? string.Empty;

    internal string Folder => BmsFile?.Folder ?? bmsonFolder;

    internal string Path => BmsFile?.path ?? bmsonPath;

    internal int? Mode => BmsFile?.mode ?? bmsonMode;

    internal string Tag => BmsFile?.tag ?? string.Empty;

    internal string Hash => BmsFile?.hash ?? BmsonSong?.md5 ?? string.Empty;

    internal string Sha256 => BmsFile?.sha256 ?? BmsonSong?.sha256 ?? string.Empty;

    internal string InstallDestination => BmsFile?.instl_dst ?? string.Empty;

    internal string InstallDestinationTitle => BmsFile?.InstallDestinationTitle ?? string.Empty;

    internal string InstallDestinationArtist => BmsFile?.InstallDestinationArtist ?? string.Empty;

    internal string RefTablesSymbols => BmsFile?.RefTablesSymbols ?? string.Empty;

    internal BMSFile CreateFilterFile()
    {
        return BmsFile ?? PendingChartEntry.CreateFromBmsonSong(BmsonSong);
    }

    internal static ChartListSourceRow FromBmsFile(BMSFile file)
    {
        return file == null ? null : new ChartListSourceRow(file, null);
    }

    internal static ChartListSourceRow FromBmsonSong(LR2SongDBExtended.bmson_song song)
    {
        return song == null ? null : new ChartListSourceRow(null, song);
    }

    internal static List<ChartListSourceRow> BuildStandardLibraryRows(
        IEnumerable<BMSFile> bmsFiles,
        IEnumerable<LR2SongDBExtended.bmson_song> bmsonSongs)
    {
        List<ChartListSourceRow> rows = new List<ChartListSourceRow>();
        rows.AddRange((bmsFiles ?? Enumerable.Empty<BMSFile>())
            .Select(FromBmsFile)
            .Where(row => row != null));
        rows.AddRange((bmsonSongs ?? Enumerable.Empty<LR2SongDBExtended.bmson_song>())
            .Where(song => song != null && !string.IsNullOrWhiteSpace(song.path))
            .OrderBy(song => song.path, StringComparer.OrdinalIgnoreCase)
            .Select(FromBmsonSong)
            .Where(row => row != null));
        return rows;
    }
}
