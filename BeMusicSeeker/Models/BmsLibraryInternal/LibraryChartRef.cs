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

    public BMSFile CompatibilityChartFile { get; }

    public LR2SongDBExtended.bmson_song BmsonSong { get; }

    private LibraryChartRef(
        LibraryChartKind kind,
        string path,
        string md5,
        string sha256,
        BMSFile compatibilityChartFile,
        LR2SongDBExtended.bmson_song bmsonSong)
    {
        Kind = kind;
        Path = string.IsNullOrWhiteSpace(path) ? null : path;
        Directory = string.IsNullOrWhiteSpace(Path) ? null : System.IO.Path.GetDirectoryName(Path);
        Md5 = string.IsNullOrWhiteSpace(md5) ? null : md5.Trim();
        Sha256 = string.IsNullOrWhiteSpace(sha256) ? null : sha256.Trim();
        CompatibilityChartFile = compatibilityChartFile;
        BmsonSong = bmsonSong;
    }

    public static LibraryChartRef FromCompatibilityChartFile(BMSFile file)
    {
        if (file == null)
        {
            return null;
        }
        if (file is PendingChartEntry pending && pending.IsBmsonChart)
        {
            return new LibraryChartRef(
                LibraryChartKind.Bmson,
                pending.path,
                pending.hash,
                pending.sha256,
                pending,
                pending.BmsonSong);
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

    public BMSFile ToCompatibilityChartFile()
    {
        if (Kind == LibraryChartKind.Bms)
        {
            return CompatibilityChartFile;
        }
        return CompatibilityChartFile ?? PendingChartEntry.CreateFromBmsonSong(BmsonSong);
    }
}
