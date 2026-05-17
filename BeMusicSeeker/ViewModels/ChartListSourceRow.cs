using System;
using System.Collections.Generic;
using System.Globalization;
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

    private readonly Func<ChartListSourceRow, ResourceHealthWarningProjection> resourceHealthProjectionProvider;

    private readonly Func<ChartListSourceRow, PlaylistReferenceDisplay> playlistReferenceDisplayProvider;

    private readonly Func<LR2SongDBExtended.bmson_song, PendingChartEntry> bmsonChartAdapterProvider;

    private PendingChartEntry bmsonChartAdapter;

    private ChartListSourceRow(
        BMSFile bmsFile,
        LR2SongDBExtended.bmson_song bmsonSong,
        Func<ChartListSourceRow, ResourceHealthWarningProjection> resourceHealthProjectionProvider,
        Func<ChartListSourceRow, PlaylistReferenceDisplay> playlistReferenceDisplayProvider,
        Func<LR2SongDBExtended.bmson_song, PendingChartEntry> bmsonChartAdapterProvider)
    {
        BmsFile = bmsFile;
        BmsonSong = bmsonSong;
        this.resourceHealthProjectionProvider = resourceHealthProjectionProvider;
        this.playlistReferenceDisplayProvider = playlistReferenceDisplayProvider;
        this.bmsonChartAdapterProvider = bmsonChartAdapterProvider;
        bmsonTitle = BmsonSongParser.ComposeDisplayTitle(bmsonSong);
        bmsonFolder = BmsonSongParser.ComposeDisplayFolder(bmsonSong);
        bmsonPath = bmsonSong?.path ?? string.Empty;
        bmsonMode = BmsonSongParser.ResolvePlaylistMode(bmsonSong?.mode_hint);
    }

    internal BMSFile BmsFile { get; }

    internal LR2SongDBExtended.bmson_song BmsonSong { get; }

    internal BMSFile CompatibilityBmsFile => BmsFile ?? GetOrCreateBmsonChartAdapter();

    internal string Title => BmsFile?.Title ?? bmsonTitle;

    internal string Artist => BmsFile?.Artist ?? BmsonSong?.artist ?? string.Empty;

    internal string Genre => BmsFile?.genre ?? BmsonSong?.genre ?? string.Empty;

    internal string Folder => BmsFile?.Folder ?? bmsonFolder;

    internal string Path => BmsFile?.path ?? bmsonPath;

    internal int? Mode => BmsFile?.mode ?? bmsonMode;

    internal string WarningDigestText => ChartWarningProjectionFormatter.BuildDigestText(
        CompatibilityBmsFile,
        GetResourceHealthProjection(),
        resourceHealthProjectionProvider != null,
        InstallDestination);

    internal string Tag => BmsFile?.tag ?? string.Empty;

    internal string Level => BmsFile?.Level ?? (BmsonSong?.level.HasValue == true ? BmsonSong.level.Value.ToString(CultureInfo.InvariantCulture) : string.Empty);

    internal double? LevelValue => BmsFile?.level ?? BmsonSong?.level;

    internal string Hash => BmsFile?.hash ?? BmsonSong?.md5 ?? string.Empty;

    internal string Sha256 => BmsFile?.sha256 ?? BmsonSong?.sha256 ?? string.Empty;

    internal string InstallDestination => CompatibilityBmsFile?.instl_dst ?? string.Empty;

    internal string InstallDestinationTitle => CompatibilityBmsFile?.InstallDestinationTitle ?? string.Empty;

    internal string InstallDestinationArtist => CompatibilityBmsFile?.InstallDestinationArtist ?? string.Empty;

    internal string RefTablesSymbols => BmsFile?.RefTablesSymbols ?? GetPlaylistReferenceDisplay().Symbols;

    internal string RefTablesNames => BmsFile?.RefTablesNames ?? GetPlaylistReferenceDisplay().Names;

    internal int? WAVHealth => BmsFile?.WAVHealth ?? BmsonSong?.MaintenanceInfo?.WAVHealth;

    internal int? BGAHealth => BmsFile?.BGAHealth ?? BmsonSong?.MaintenanceInfo?.BGAHealth;

    internal int? MovieHealth => BmsFile?.MovieHealth ?? BmsonSong?.MaintenanceInfo?.MovieHealth;

    internal string EncodingName => BmsFile?.encoding ?? BmsonSong?.MaintenanceInfo?.encoding ?? string.Empty;

    internal ClearType Clear => BmsFile?.clear ?? (string.IsNullOrWhiteSpace(Path) ? ClearType.NO_SONG : ClearType.NO_PLAY);

    internal RankType Rank => BmsFile?.rank ?? RankType.INVALID;

    internal double? RateDouble => BmsFile?.rateDouble;

    internal int? Rate => BmsFile?.rate;

    internal int? Score => BmsFile?.score;

    internal int? TotalNotes => BmsFile?.totalnotes;

    internal int? MaxCombo => BmsFile?.maxcombo;

    internal int? MinBp => BmsFile?.minbp;

    internal int? Ranking => BmsFile?.ranking;

    internal int? RankingNum => BmsFile?.rankingNum;

    internal string RankingString => BmsFile?.rankingString ?? string.Empty;

    internal DateTime? RankingLastUpdate => BmsFile?.rankingLastupdate;

    internal double? StdDevVal => BmsFile?.stddevVal;

    internal double? ScoreDifficulty => BmsFile?.scoreDifficulty;

    internal LR2SongDBExtended.chart_info ChartInfo => BmsFile?.ChartInfo ?? BmsonSong?.ChartInfo;

    internal double? ChartLevelSortKey => ChartInfo?.level ?? 0;

    internal int? ChartDifficultySortKey => ChartInfo?.difficulty;

    internal double? ChartMainBpmSortKey => ChartInfo?.mainbpm;

    internal double? ChartMaxBpmSortKey => ChartInfo?.maxbpm;

    internal double? ChartMinBpmSortKey => ChartInfo?.minbpm;

    internal int? ChartDurationSortKey => ChartInfo?.length;

    internal int? ChartJudgeSortKey => ChartInfo?.judge;

    internal int? ChartFeatureSortKey => ChartInfo?.feature;

    internal int? ChartNotes => ChartInfo?.notes;

    internal int? ChartLongNotes => ChartInfo?.ln;

    internal int? ChartScratchNotes => ChartInfoDisplayFormatter.GetScratchNotes(ChartInfo);

    internal double? ChartTotalSortKey => ChartInfo?.total;

    internal double? ChartTotalPerNoteSortKey => ChartInfoDisplayFormatter.GetTotalPerNote(ChartInfo);

    internal double? ChartDensitySortKey => ChartInfo?.density;

    internal double? ChartPeakDensitySortKey => ChartInfo?.peakdensity;

    internal double? ChartEndDensitySortKey => ChartInfo?.enddensity;

    internal int? ChartSoflanCount => ChartInfo?.speedchange_count;

    internal BMSFile CreateFilterFile()
    {
        return CompatibilityBmsFile;
    }

    private PendingChartEntry GetOrCreateBmsonChartAdapter()
    {
        if (BmsonSong == null)
        {
            return null;
        }
        PendingChartEntry provided = bmsonChartAdapterProvider?.Invoke(BmsonSong);
        if (provided != null)
        {
            bmsonChartAdapter = provided;
            return bmsonChartAdapter;
        }
        if (bmsonChartAdapter == null || !ReferenceEquals(bmsonChartAdapter.BmsonSong, BmsonSong))
        {
            bmsonChartAdapter = PendingChartEntry.CreateFromBmsonSong(BmsonSong);
        }
        else if (!string.Equals(bmsonChartAdapter.path, BmsonSong.path, StringComparison.OrdinalIgnoreCase))
        {
            bmsonChartAdapter.UpdateFromBmsonSong(BmsonSong);
        }
        return bmsonChartAdapter;
    }

    internal static ChartListSourceRow FromBmsFile(
        BMSFile file,
        Func<ChartListSourceRow, ResourceHealthWarningProjection> resourceHealthProjectionProvider = null,
        Func<ChartListSourceRow, PlaylistReferenceDisplay> playlistReferenceDisplayProvider = null,
        Func<LR2SongDBExtended.bmson_song, PendingChartEntry> bmsonChartAdapterProvider = null)
    {
        return file == null ? null : new ChartListSourceRow(file, null, resourceHealthProjectionProvider, playlistReferenceDisplayProvider, bmsonChartAdapterProvider);
    }

    internal static ChartListSourceRow FromBmsonSong(
        LR2SongDBExtended.bmson_song song,
        Func<ChartListSourceRow, ResourceHealthWarningProjection> resourceHealthProjectionProvider = null,
        Func<ChartListSourceRow, PlaylistReferenceDisplay> playlistReferenceDisplayProvider = null,
        Func<LR2SongDBExtended.bmson_song, PendingChartEntry> bmsonChartAdapterProvider = null)
    {
        return song == null ? null : new ChartListSourceRow(null, song, resourceHealthProjectionProvider, playlistReferenceDisplayProvider, bmsonChartAdapterProvider);
    }

    internal static List<ChartListSourceRow> BuildStandardLibraryRows(
        IEnumerable<BMSFile> bmsFiles,
        IEnumerable<LR2SongDBExtended.bmson_song> bmsonSongs,
        Func<ChartListSourceRow, ResourceHealthWarningProjection> resourceHealthProjectionProvider = null,
        Func<ChartListSourceRow, PlaylistReferenceDisplay> playlistReferenceDisplayProvider = null,
        Func<LR2SongDBExtended.bmson_song, PendingChartEntry> bmsonChartAdapterProvider = null)
    {
        List<ChartListSourceRow> rows = new List<ChartListSourceRow>();
        rows.AddRange((bmsFiles ?? Enumerable.Empty<BMSFile>())
            .Select(file => FromBmsFile(file, resourceHealthProjectionProvider, playlistReferenceDisplayProvider, bmsonChartAdapterProvider))
            .Where(row => row != null));
        rows.AddRange((bmsonSongs ?? Enumerable.Empty<LR2SongDBExtended.bmson_song>())
            .Where(song => song != null && !string.IsNullOrWhiteSpace(song.path))
            .OrderBy(song => song.path, StringComparer.OrdinalIgnoreCase)
            .Select(song => FromBmsonSong(song, resourceHealthProjectionProvider, playlistReferenceDisplayProvider, bmsonChartAdapterProvider))
            .Where(row => row != null));
        return rows;
    }

    private ResourceHealthWarningProjection GetResourceHealthProjection()
    {
        return resourceHealthProjectionProvider == null
            ? ResourceHealthWarningProjection.Empty
            : resourceHealthProjectionProvider.Invoke(this) ?? ResourceHealthWarningProjection.Empty;
    }

    private PlaylistReferenceDisplay GetPlaylistReferenceDisplay()
    {
        return playlistReferenceDisplayProvider == null
            ? PlaylistReferenceDisplay.Empty
            : playlistReferenceDisplayProvider.Invoke(this) ?? PlaylistReferenceDisplay.Empty;
    }
}
