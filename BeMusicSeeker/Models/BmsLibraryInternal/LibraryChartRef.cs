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

    internal static LibraryChartRef FromChartFile(ChartFile chart)
    {
        if (chart == null)
        {
            return null;
        }
        BMSFile bmsFile = chart.GetBmsStorageOwner();
        if (bmsFile != null)
        {
            return FromBmsFile(bmsFile);
        }
        LR2SongDBExtended.bmson_song bmsonSong = chart.GetBmsonStorageOwner();
        if (bmsonSong != null)
        {
            return FromBmsonSong(bmsonSong);
        }
        return FromPath(
            chart.Kind == ChartFileKind.Bmson ? LibraryChartKind.Bmson : LibraryChartKind.Bms,
            chart.Path,
            chart.Md5,
            chart.Sha256);
    }

    internal static LibraryChartRef FromPath(LibraryChartKind kind, string path, string md5, string sha256)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }
        return new LibraryChartRef(
            kind,
            path,
            md5,
            sha256,
            null,
            null);
    }

}
