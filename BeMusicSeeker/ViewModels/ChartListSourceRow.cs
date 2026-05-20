using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.ViewModels;

internal sealed class ChartListSourceRow
{
    private readonly string bmsonFolder;

    private readonly Func<ChartListSourceRow, ResourceHealthWarningProjection> resourceHealthProjectionProvider;

    private readonly Func<ChartListSourceRow, PlaylistReferenceDisplay> playlistReferenceDisplayProvider;

    private readonly Func<LR2SongDBExtended.bmson_song, PendingChartEntry> bmsonChartAdapterProvider;

    private readonly Func<LR2SongDBExtended.bmson_song, PendingChartEntry> existingBmsonChartAdapterProvider;

    private readonly bool materializeBmsonAdapterOnDemand;

    private PendingChartEntry bmsonChartAdapter;

    private readonly ChartFile identityChart;

    private ChartListSourceRow(
        BMSFile bmsFile,
        LR2SongDBExtended.bmson_song bmsonSong,
        Func<ChartListSourceRow, ResourceHealthWarningProjection> resourceHealthProjectionProvider,
        Func<ChartListSourceRow, PlaylistReferenceDisplay> playlistReferenceDisplayProvider,
        Func<LR2SongDBExtended.bmson_song, PendingChartEntry> bmsonChartAdapterProvider,
        Func<LR2SongDBExtended.bmson_song, PendingChartEntry> existingBmsonChartAdapterProvider,
        bool materializeBmsonAdapterOnDemand)
    {
        BmsFile = bmsFile;
        BmsonSong = bmsonSong;
        this.resourceHealthProjectionProvider = resourceHealthProjectionProvider;
        this.playlistReferenceDisplayProvider = playlistReferenceDisplayProvider;
        this.bmsonChartAdapterProvider = bmsonChartAdapterProvider;
        this.existingBmsonChartAdapterProvider = existingBmsonChartAdapterProvider;
        this.materializeBmsonAdapterOnDemand = materializeBmsonAdapterOnDemand;
        bmsonFolder = BmsonSongParser.ComposeDisplayFolder(bmsonSong);
        identityChart = bmsFile != null
            ? ChartFileProjection.FromBmsFile(bmsFile, includeWarningSnapshot: false)
            : ChartFileProjection.FromBmsonSong(bmsonSong, includeWarningSnapshot: false);
    }

    internal BMSFile BmsFile { get; }

    internal LR2SongDBExtended.bmson_song BmsonSong { get; }

    internal BMSFile CompatibilityBmsFile => BmsFile ?? GetBmsonChartAdapter();

    internal ChartFile Chart => CreateChartFile();

    internal string Title => identityChart?.Title ?? string.Empty;

    internal string Artist => identityChart?.Artist ?? string.Empty;

    internal string Genre => identityChart?.Genre ?? string.Empty;

    internal string Folder => identityChart?.Folder ?? bmsonFolder;

    internal string Path => identityChart?.Path ?? string.Empty;

    internal int? Mode => identityChart?.Mode;

    internal string WarningDigestText => ChartWarningProjectionFormatter.BuildDigestText(
        Chart,
        GetResourceHealthProjection(),
        resourceHealthProjectionProvider != null);

    internal string Tag => identityChart?.Tag ?? string.Empty;

    internal string Level => identityChart?.LevelText ?? string.Empty;

    internal double? LevelValue => identityChart?.Level;

    internal string Hash => identityChart?.Md5 ?? string.Empty;

    internal string Sha256 => identityChart?.Sha256 ?? string.Empty;

    internal string InstallDestination => CreateChartFile(includeWarningSnapshot: false)?.InstallDestination ?? string.Empty;

    internal string InstallDestinationTitle => CreateChartFile(includeWarningSnapshot: false)?.InstallDestinationTitle ?? string.Empty;

    internal string InstallDestinationArtist => CreateChartFile(includeWarningSnapshot: false)?.InstallDestinationArtist ?? string.Empty;

    internal string RefTablesSymbols => BmsFile?.RefTablesSymbols ?? GetPlaylistReferenceDisplay().Symbols;

    internal string RefTablesNames => BmsFile?.RefTablesNames ?? GetPlaylistReferenceDisplay().Names;

    internal int? WAVHealth => Chart?.WAVHealth;

    internal int? BGAHealth => Chart?.BGAHealth;

    internal int? MovieHealth => Chart?.MovieHealth;

    internal string EncodingName => Chart?.EncodingName ?? string.Empty;

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

    private ChartFile CreateChartFile(bool includeWarningSnapshot = true)
    {
        return BmsFile != null
            ? ChartFileProjection.FromBmsFile(BmsFile, includeWarningSnapshot: includeWarningSnapshot)
            : ChartFileProjection.FromBmsonSong(BmsonSong, GetExistingBmsonChartAdapter(), includeWarningSnapshot);
    }

    private PendingChartEntry GetExistingBmsonChartAdapter()
    {
        if (BmsonSong == null)
        {
            return null;
        }
        if (bmsonChartAdapter == null)
        {
            PendingChartEntry provided = existingBmsonChartAdapterProvider?.Invoke(BmsonSong);
            if (provided != null)
            {
                bmsonChartAdapter = provided;
                return bmsonChartAdapter;
            }
            return null;
        }
        if (!ReferenceEquals(bmsonChartAdapter.BmsonSong, BmsonSong))
        {
            PendingChartEntry provided = existingBmsonChartAdapterProvider?.Invoke(BmsonSong);
            if (provided != null)
            {
                bmsonChartAdapter = provided;
                return bmsonChartAdapter;
            }
            return null;
        }
        if (!string.Equals(bmsonChartAdapter.path, BmsonSong.path, StringComparison.OrdinalIgnoreCase))
        {
            bmsonChartAdapter.UpdateFromBmsonSong(BmsonSong);
        }
        return bmsonChartAdapter;
    }

    private PendingChartEntry GetBmsonChartAdapter()
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
        if (!materializeBmsonAdapterOnDemand)
        {
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
        Func<LR2SongDBExtended.bmson_song, PendingChartEntry> bmsonChartAdapterProvider = null,
        Func<LR2SongDBExtended.bmson_song, PendingChartEntry> existingBmsonChartAdapterProvider = null,
        bool materializeBmsonAdapterOnDemand = true)
    {
        return file == null ? null : new ChartListSourceRow(file, null, resourceHealthProjectionProvider, playlistReferenceDisplayProvider, bmsonChartAdapterProvider, existingBmsonChartAdapterProvider, materializeBmsonAdapterOnDemand);
    }

    internal static ChartListSourceRow FromBmsonSong(
        LR2SongDBExtended.bmson_song song,
        Func<ChartListSourceRow, ResourceHealthWarningProjection> resourceHealthProjectionProvider = null,
        Func<ChartListSourceRow, PlaylistReferenceDisplay> playlistReferenceDisplayProvider = null,
        Func<LR2SongDBExtended.bmson_song, PendingChartEntry> bmsonChartAdapterProvider = null,
        Func<LR2SongDBExtended.bmson_song, PendingChartEntry> existingBmsonChartAdapterProvider = null,
        bool materializeBmsonAdapterOnDemand = true)
    {
        return song == null ? null : new ChartListSourceRow(null, song, resourceHealthProjectionProvider, playlistReferenceDisplayProvider, bmsonChartAdapterProvider, existingBmsonChartAdapterProvider, materializeBmsonAdapterOnDemand);
    }

    internal static List<ChartListSourceRow> BuildStandardLibraryRows(
        IEnumerable<BMSFile> bmsFiles,
        IEnumerable<LR2SongDBExtended.bmson_song> bmsonSongs,
        Func<ChartListSourceRow, ResourceHealthWarningProjection> resourceHealthProjectionProvider = null,
        Func<ChartListSourceRow, PlaylistReferenceDisplay> playlistReferenceDisplayProvider = null,
        Func<LR2SongDBExtended.bmson_song, PendingChartEntry> bmsonChartAdapterProvider = null,
        Func<LR2SongDBExtended.bmson_song, PendingChartEntry> existingBmsonChartAdapterProvider = null,
        bool materializeBmsonAdapterOnDemand = true)
    {
        List<ChartListSourceRow> rows =
        [
            .. (bmsFiles ?? [])
                .Select(file => FromBmsFile(file, resourceHealthProjectionProvider, playlistReferenceDisplayProvider, bmsonChartAdapterProvider, existingBmsonChartAdapterProvider, materializeBmsonAdapterOnDemand))
                .Where(row => row != null),
            .. (bmsonSongs ?? [])
                .Where(song => song != null && !string.IsNullOrWhiteSpace(song.path))
                .OrderBy(song => song.path, StringComparer.OrdinalIgnoreCase)
                .Select(song => FromBmsonSong(song, resourceHealthProjectionProvider, playlistReferenceDisplayProvider, bmsonChartAdapterProvider, existingBmsonChartAdapterProvider, materializeBmsonAdapterOnDemand))
                .Where(row => row != null),
        ];
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
