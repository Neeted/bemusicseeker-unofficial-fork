using System;
using System.IO;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.ViewModels;

internal enum OwnedChartKind
{
    Bms,
    Bmson
}

internal enum ChartOperationSourceScope
{
    Library,
    PendingPackage,
    NewlyInstalledPackage,
    PlaylistOwned,
    PlaylistMissing
}

[Flags]
internal enum ChartOperationCapabilities
{
    None = 0,
    OpenFile = 1 << 0,
    OpenFolder = 1 << 1,
    OpenRepositoryBySha256 = 1 << 2,
    UseLr2Ir = 1 << 3,
    UseScoreViewer = 1 << 4,
    UpdateRanking = 1 << 5,
    RunResourceHealthCheck = 1 << 6,
    RunBmsEncodingCheck = 1 << 7,
    RunBmsEncodingFix = 1 << 8,
    RunZeroNoteCheck = 1 << 9,
    MoveInLibrary = 1 << 10,
    RemoveFromLibrary = 1 << 11,
    UpdateInstallDestination = 1 << 12,
    RenameInvalidExtension = 1 << 13,
    ConvertToAudio = 1 << 14,
    OpenPlaylistUrls = 1 << 15,
    RepairInstalledLocation = 1 << 16
}

internal sealed class OwnedChartRef
{
    internal OwnedChartKind Kind { get; }

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

    internal LR2SongDBExtended.bmson_song BmsonSong { get; }

    internal OwnedChartRef(
        OwnedChartKind kind,
        string path,
        string md5,
        string sha256,
        string title,
        string artist,
        double? level,
        int? mode,
        LR2SongDBExtended.chart_info chartInfo,
        BMSFile bmsFile,
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
        BmsonSong = bmsonSong;
    }
}

internal sealed class ChartOperationTarget
{
    internal OwnedChartRef Chart { get; }

    internal BMSTableEntry PlaylistEntry { get; }

    internal ChartOperationSourceScope SourceScope { get; }

    internal bool IsOwned { get; }

    internal bool IsPending { get; }

    internal bool IsPlaylistMissing { get; }

    internal ChartOperationCapabilities Capabilities { get; }

    internal ChartOperationTarget(
        OwnedChartRef chart,
        BMSTableEntry playlistEntry,
        ChartOperationSourceScope sourceScope,
        bool isOwned,
        bool isPending,
        bool isPlaylistMissing,
        ChartOperationCapabilities capabilities)
    {
        Chart = chart ?? throw new ArgumentNullException(nameof(chart));
        PlaylistEntry = playlistEntry;
        SourceScope = sourceScope;
        IsOwned = isOwned;
        IsPending = isPending;
        IsPlaylistMissing = isPlaylistMissing;
        Capabilities = capabilities;
    }

    internal bool HasCapability(ChartOperationCapabilities capability)
    {
        return (Capabilities & capability) == capability;
    }
}
