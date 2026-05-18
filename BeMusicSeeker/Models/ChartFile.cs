using System.Collections.Generic;
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

    internal string PrimaryLookupHash
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Md5))
            {
                return Md5;
            }
            return string.IsNullOrWhiteSpace(Sha256) ? null : Sha256;
        }
    }

    internal string Title { get; }

    internal string RawTitle { get; }

    internal string Artist { get; }

    internal string Genre { get; }

    internal string Folder { get; }

    internal string Tag { get; }

    internal string LevelText { get; }

    internal double? Level { get; }

    internal int? Mode { get; }

    internal LR2SongDBExtended.chart_info ChartInfo { get; }

    internal string Subtitle { get; }

    internal string InstallDestination { get; }

    internal string InstallDestinationTitle { get; }

    internal string InstallDestinationArtist { get; }

    internal IReadOnlyList<ChartWarning> Warnings { get; }

    internal int? WAVHealth { get; }

    internal int? BGAHealth { get; }

    internal int? MovieHealth { get; }

    internal bool? StagefileHealth { get; }

    internal bool? BannerHealth { get; }

    internal bool? BackbmpHealth { get; }

    internal string EncodingName { get; }

    internal BMSFile BmsFile { get; }

    internal LR2SongDBExtended.bmson_song BmsonSong { get; }

    internal ChartFile(
        ChartFileKind kind,
        string path,
        string md5,
        string sha256,
        string title,
        string rawTitle,
        string artist,
        string genre,
        string folder,
        string tag,
        string levelText,
        double? level,
        int? mode,
        LR2SongDBExtended.chart_info chartInfo,
        BMSFile bmsFile,
        LR2SongDBExtended.bmson_song bmsonSong,
        string subtitle = null,
        string installDestination = null,
        string installDestinationTitle = null,
        string installDestinationArtist = null,
        IReadOnlyList<ChartWarning> warnings = null,
        int? wavHealth = null,
        int? bgaHealth = null,
        int? movieHealth = null,
        bool? stagefileHealth = null,
        bool? bannerHealth = null,
        bool? backbmpHealth = null,
        string encodingName = null)
    {
        Kind = kind;
        Path = string.IsNullOrWhiteSpace(path) ? null : path;
        Directory = string.IsNullOrWhiteSpace(Path) ? null : System.IO.Path.GetDirectoryName(Path);
        Md5 = string.IsNullOrWhiteSpace(md5) ? null : md5.Trim();
        Sha256 = string.IsNullOrWhiteSpace(sha256) ? null : sha256.Trim();
        Title = title ?? string.Empty;
        RawTitle = rawTitle ?? Title;
        Artist = artist ?? string.Empty;
        Genre = genre ?? string.Empty;
        Folder = string.IsNullOrWhiteSpace(folder) ? System.IO.Path.GetFileName(Directory) ?? string.Empty : folder;
        Tag = tag ?? string.Empty;
        LevelText = levelText ?? string.Empty;
        Level = level;
        Mode = mode;
        ChartInfo = chartInfo;
        Subtitle = subtitle ?? string.Empty;
        InstallDestination = installDestination ?? string.Empty;
        InstallDestinationTitle = installDestinationTitle ?? string.Empty;
        InstallDestinationArtist = installDestinationArtist ?? string.Empty;
        Warnings = warnings ?? [];
        WAVHealth = wavHealth;
        BGAHealth = bgaHealth;
        MovieHealth = movieHealth;
        StagefileHealth = stagefileHealth;
        BannerHealth = bannerHealth;
        BackbmpHealth = backbmpHealth;
        EncodingName = encodingName ?? string.Empty;
        BmsFile = bmsFile;
        BmsonSong = bmsonSong;
    }
}
