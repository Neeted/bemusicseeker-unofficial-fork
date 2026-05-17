using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models;

internal enum ChartFileKind
{
    Bms,
    Bmson
}

internal sealed class ChartFile
{
    internal ChartFileKind Kind { get; }

    internal string Path { get; }

    internal string Directory { get; }

    internal string Md5 { get; }

    internal string Sha256 { get; }

    internal string Title { get; }

    internal string Artist { get; }

    internal double? Level { get; }

    internal int? Mode { get; }

    internal LR2SongDBExtended.chart_info ChartInfo { get; }

    internal BMSFile BmsFile { get; }

    // Existing BMSFile-based APIs use this for both real BMS files and bmson adapters.
    internal BMSFile CompatibilityBmsFile { get; }

    internal LR2SongDBExtended.bmson_song BmsonSong { get; }

    internal ChartFile(
        ChartFileKind kind,
        string path,
        string md5,
        string sha256,
        string title,
        string artist,
        double? level,
        int? mode,
        LR2SongDBExtended.chart_info chartInfo,
        BMSFile bmsFile,
        BMSFile compatibilityBmsFile,
        LR2SongDBExtended.bmson_song bmsonSong)
    {
        Kind = kind;
        Path = string.IsNullOrWhiteSpace(path) ? null : path;
        Directory = string.IsNullOrWhiteSpace(Path) ? null : System.IO.Path.GetDirectoryName(Path);
        Md5 = string.IsNullOrWhiteSpace(md5) ? null : md5.Trim();
        Sha256 = string.IsNullOrWhiteSpace(sha256) ? null : sha256.Trim();
        Title = title ?? string.Empty;
        Artist = artist ?? string.Empty;
        Level = level;
        Mode = mode;
        ChartInfo = chartInfo;
        BmsFile = bmsFile;
        CompatibilityBmsFile = compatibilityBmsFile;
        BmsonSong = bmsonSong;
    }
}
