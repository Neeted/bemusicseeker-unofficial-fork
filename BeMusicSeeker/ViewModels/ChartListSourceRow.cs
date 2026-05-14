using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.ViewModels;

internal sealed class ChartListSourceRow
{
    private ChartListSourceRow(BMSFile bmsFile, LR2SongDBExtended.bmson_song bmsonSong)
    {
        BmsFile = bmsFile;
        BmsonSong = bmsonSong;
        Title = bmsFile?.Title ?? BmsonSongParser.ComposeDisplayTitle(bmsonSong);
        Folder = bmsFile?.Folder ?? BmsonSongParser.ComposeDisplayFolder(bmsonSong);
        Path = bmsFile?.path ?? bmsonSong?.path ?? string.Empty;
        Mode = bmsFile?.mode ?? BmsonSongParser.ResolvePlaylistMode(bmsonSong?.mode_hint);
    }

    internal BMSFile BmsFile { get; }

    internal LR2SongDBExtended.bmson_song BmsonSong { get; }

    internal string Title { get; }

    internal string Folder { get; }

    internal string Path { get; }

    internal int? Mode { get; }

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
