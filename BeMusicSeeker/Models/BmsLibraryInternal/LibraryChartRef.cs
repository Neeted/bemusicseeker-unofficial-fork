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
    private readonly BMSFile bmsFile;
    private readonly LR2SongDBExtended.bmson_song bmsonSong;
    private readonly ChartFile chartSnapshot;
    private readonly string path;
    private readonly string md5;
    private readonly string sha256;
    private string directory;

    public LibraryChartKind Kind { get; }

    public string Path => path;

    public string Directory => directory ??= string.IsNullOrWhiteSpace(Path) ? null : System.IO.Path.GetDirectoryName(Path);

    public string Md5
    {
        get
        {
            BMSFile currentBmsFile = GetBmsStorageOwner();
            if (currentBmsFile != null)
            {
                return NormalizeHash(currentBmsFile.hash);
            }

            LR2SongDBExtended.bmson_song currentBmsonSong = GetBmsonStorageOwner();
            return currentBmsonSong != null
                ? NormalizeHash(currentBmsonSong.md5)
                : md5;
        }
    }

    public string Sha256
    {
        get
        {
            BMSFile currentBmsFile = GetBmsStorageOwner();
            if (currentBmsFile != null)
            {
                return NormalizeHash(currentBmsFile.sha256);
            }

            LR2SongDBExtended.bmson_song currentBmsonSong = GetBmsonStorageOwner();
            return currentBmsonSong != null
                ? NormalizeHash(currentBmsonSong.sha256)
                : sha256;
        }
    }

    private LibraryChartRef(
        LibraryChartKind kind,
        string path,
        string md5,
        string sha256,
        BMSFile bmsFile,
        LR2SongDBExtended.bmson_song bmsonSong,
        ChartFile chartSnapshot = null)
    {
        Kind = kind;
        this.path = string.IsNullOrWhiteSpace(path) ? null : path;
        this.md5 = NormalizeHash(md5);
        this.sha256 = NormalizeHash(sha256);
        this.bmsFile = bmsFile;
        this.bmsonSong = bmsonSong;
        this.chartSnapshot = chartSnapshot;
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
            return new LibraryChartRef(
                LibraryChartKind.Bms,
                chart.Path,
                chart.Md5,
                chart.Sha256,
                bmsFile,
                null,
                chart);
        }
        LR2SongDBExtended.bmson_song bmsonSong = chart.GetBmsonStorageOwner();
        if (bmsonSong != null)
        {
            return new LibraryChartRef(
                LibraryChartKind.Bmson,
                chart.Path,
                chart.Md5,
                chart.Sha256,
                null,
                bmsonSong,
                chart);
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

    internal BMSFile GetBmsStorageOwner()
    {
        return Kind == LibraryChartKind.Bms ? bmsFile : null;
    }

    internal LR2SongDBExtended.bmson_song GetBmsonStorageOwner()
    {
        return Kind == LibraryChartKind.Bmson ? bmsonSong : null;
    }

    internal ChartFile GetChartSnapshot()
    {
        return chartSnapshot;
    }

    internal ChartFile ToChartFile()
    {
        if (chartSnapshot != null)
        {
            return chartSnapshot;
        }

        BMSFile bmsFile = GetBmsStorageOwner();
        if (bmsFile != null)
        {
            return ChartFileProjection.FromBmsFile(
                bmsFile,
                includeWarningSnapshot: false,
                includeResourceReferences: false);
        }

        LR2SongDBExtended.bmson_song bmsonSong = GetBmsonStorageOwner();
        if (bmsonSong != null)
        {
            return ChartFileProjection.FromBmsonSong(
                bmsonSong,
                includeWarningSnapshot: false,
                includeResourceReferences: false);
        }

        return null;
    }

    internal ChartFile ToChartFileIdentity()
    {
        if (chartSnapshot != null)
        {
            return ChartFileProjection.FromStorageOwnerListIdentity(chartSnapshot) ?? chartSnapshot;
        }

        BMSFile bmsFile = GetBmsStorageOwner();
        if (bmsFile != null)
        {
            return ChartFileProjection.FromBmsStorageOwnerIdentity(bmsFile);
        }

        LR2SongDBExtended.bmson_song bmsonSong = GetBmsonStorageOwner();
        if (bmsonSong != null)
        {
            return ChartFileProjection.FromBmsonStorageOwnerIdentity(bmsonSong);
        }

        return null;
    }

    private static string NormalizeHash(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

}
