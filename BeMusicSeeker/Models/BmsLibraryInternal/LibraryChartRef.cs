using System;
using System.IO;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal enum LibraryChartKind
{
    Bms,
    Bmson
}

internal sealed class LibraryChartRef
{
    public LibraryChartKind Kind { get; }

    public string Path { get; }

    public string Directory { get; }

    public string Md5 { get; }

    public string Sha256 { get; }

    public BMSFile BmsFile { get; }

    public LR2SongDBExtended.bmson_song BmsonSong { get; }

    private LibraryChartRef(
        LibraryChartKind kind,
        string path,
        string md5,
        string sha256,
        BMSFile bmsFile,
        LR2SongDBExtended.bmson_song bmsonSong)
    {
        Kind = kind;
        Path = string.IsNullOrWhiteSpace(path) ? null : path;
        Directory = string.IsNullOrWhiteSpace(Path) ? null : System.IO.Path.GetDirectoryName(Path);
        Md5 = string.IsNullOrWhiteSpace(md5) ? null : md5.Trim();
        Sha256 = string.IsNullOrWhiteSpace(sha256) ? null : sha256.Trim();
        BmsFile = bmsFile;
        BmsonSong = bmsonSong;
    }

    public static LibraryChartRef FromBmsFile(BMSFile file)
    {
        if (file == null)
        {
            return null;
        }
        if (file is PendingChartEntry pending && pending.IsBmsonChart)
        {
            return FromBmsonSong(pending.BmsonSong);
        }
        return new LibraryChartRef(
            LibraryChartKind.Bms,
            file.path,
            file.hash,
            file.sha256,
            file,
            null);
    }

    public static LibraryChartRef FromBmsonSong(LR2SongDBExtended.bmson_song song)
    {
        if (song == null)
        {
            return null;
        }
        return new LibraryChartRef(
            LibraryChartKind.Bmson,
            song.path,
            song.md5,
            song.sha256,
            null,
            song);
    }

    public BMSFile ToCompatibilityBmsFile()
    {
        if (Kind == LibraryChartKind.Bms)
        {
            return BmsFile;
        }
        return PendingChartEntry.CreateFromBmsonSong(BmsonSong);
    }
}
