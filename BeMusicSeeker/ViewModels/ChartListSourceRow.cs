using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.ViewModels;

internal sealed class ChartListSourceRow
{
    private readonly Func<ChartListSourceRow, ResourceHealthWarningProjection> resourceHealthProjectionProvider;

    private readonly Func<ChartListSourceRow, PlaylistReferenceDisplay> playlistReferenceDisplayProvider;

    private readonly Func<LR2SongDBExtended.bmson_song, bool, ChartFileTransientState> bmsonTransientStateProvider;

    private readonly ChartFile identityChart;

    private readonly ChartFile sourceChart;

    private readonly bool hasSourceChartProjection;

    private ChartListSourceRow(
        ChartFile sourceChart,
        bool hasSourceChartProjection,
        Func<ChartListSourceRow, ResourceHealthWarningProjection> resourceHealthProjectionProvider,
        Func<ChartListSourceRow, PlaylistReferenceDisplay> playlistReferenceDisplayProvider,
        Func<LR2SongDBExtended.bmson_song, bool, ChartFileTransientState> bmsonTransientStateProvider)
    {
        this.sourceChart = sourceChart ?? throw new ArgumentNullException(nameof(sourceChart));
        this.hasSourceChartProjection = hasSourceChartProjection;
        BmsFile = sourceChart.BmsFile;
        BmsonSong = sourceChart.BmsonSong;
        this.resourceHealthProjectionProvider = resourceHealthProjectionProvider;
        this.playlistReferenceDisplayProvider = playlistReferenceDisplayProvider;
        this.bmsonTransientStateProvider = bmsonTransientStateProvider;
        identityChart = CreateChartFile(includeWarningSnapshot: false);
    }

    internal BMSFile BmsFile { get; }

    internal LR2SongDBExtended.bmson_song BmsonSong { get; }

    internal ChartFile Chart => CreateChartFile();

    internal bool HasSourceChartProjection => hasSourceChartProjection;

    internal string Title => identityChart?.Title ?? string.Empty;

    internal string Artist => identityChart?.Artist ?? string.Empty;

    internal string Genre => identityChart?.Genre ?? string.Empty;

    internal string Folder => identityChart?.Folder ?? string.Empty;

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

    internal LR2SongDBExtended.chart_info ChartInfo => BmsFile?.ChartInfo ?? BmsonSong?.ChartInfo ?? sourceChart?.ChartInfo;

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
        if (sourceChart != null)
        {
            if (BmsFile != null)
            {
                ChartFile currentChart = ChartFileProjection.FromBmsFile(BmsFile, includeWarningSnapshot: includeWarningSnapshot);
                return (sourceChart.Warnings?.Count ?? 0) > 0
                    ? ChartFileProjection.WithWarnings(currentChart, includeWarningSnapshot ? sourceChart.Warnings : [])
                    : currentChart;
            }
            if (BmsonSong != null)
            {
                return ChartFileProjection.FromBmsonSong(
                    BmsonSong,
                    GetBmsonTransientState(includeWarningSnapshot),
                    includeWarningSnapshot);
            }
            return includeWarningSnapshot ? sourceChart : ChartFileProjection.WithWarnings(sourceChart, []);
        }
        return null;
    }

    private ChartFileTransientState GetBmsonTransientState(bool includeWarningSnapshot)
    {
        if (BmsonSong == null)
        {
            return ChartFileTransientState.Empty;
        }
        ChartFileTransientState providerState = bmsonTransientStateProvider?.Invoke(BmsonSong, includeWarningSnapshot);
        if (providerState?.HasState == true)
        {
            if (includeWarningSnapshot
                && (providerState.Warnings?.Count ?? 0) == 0
                && (sourceChart?.Warnings?.Count ?? 0) > 0)
            {
                return ChartFileTransientState.FromChartFile(
                    ChartFileProjection.WithPackageState(
                        sourceChart,
                        providerState.InstallDestination,
                        providerState.InstallDestinationTitle,
                        providerState.InstallDestinationArtist,
                        providerState.InstallDestinationSuggestions,
                        sourceChart.Warnings));
            }
            return providerState;
        }
        return sourceChart == null ? ChartFileTransientState.Empty : ChartFileTransientState.FromChartFile(sourceChart, includeWarningSnapshot);
    }

    internal static ChartListSourceRow FromBmsFile(
        BMSFile file,
        Func<ChartListSourceRow, ResourceHealthWarningProjection> resourceHealthProjectionProvider = null,
        Func<ChartListSourceRow, PlaylistReferenceDisplay> playlistReferenceDisplayProvider = null,
        Func<LR2SongDBExtended.bmson_song, bool, ChartFileTransientState> bmsonTransientStateProvider = null)
    {
        return file == null
            ? null
            : new ChartListSourceRow(
                ChartFileProjection.FromBmsFile(file, includeWarningSnapshot: false),
                hasSourceChartProjection: false,
                resourceHealthProjectionProvider,
                playlistReferenceDisplayProvider,
                bmsonTransientStateProvider);
    }

    internal static ChartListSourceRow FromBmsonSong(
        LR2SongDBExtended.bmson_song song,
        Func<ChartListSourceRow, ResourceHealthWarningProjection> resourceHealthProjectionProvider = null,
        Func<ChartListSourceRow, PlaylistReferenceDisplay> playlistReferenceDisplayProvider = null,
        Func<LR2SongDBExtended.bmson_song, bool, ChartFileTransientState> bmsonTransientStateProvider = null)
    {
        return song == null
            ? null
            : new ChartListSourceRow(
                ChartFileProjection.FromBmsonSong(song, includeWarningSnapshot: false),
                hasSourceChartProjection: false,
                resourceHealthProjectionProvider,
                playlistReferenceDisplayProvider,
                bmsonTransientStateProvider);
    }

    internal static ChartListSourceRow FromChartFile(
        ChartFile chart,
        Func<ChartListSourceRow, ResourceHealthWarningProjection> resourceHealthProjectionProvider = null,
        Func<ChartListSourceRow, PlaylistReferenceDisplay> playlistReferenceDisplayProvider = null,
        Func<LR2SongDBExtended.bmson_song, bool, ChartFileTransientState> bmsonTransientStateProvider = null)
    {
        return chart == null ? null : new ChartListSourceRow(chart, hasSourceChartProjection: true, resourceHealthProjectionProvider, playlistReferenceDisplayProvider, bmsonTransientStateProvider);
    }

    internal static List<ChartListSourceRow> BuildStandardLibraryRows(
        IEnumerable<BMSFile> bmsFiles,
        IEnumerable<LR2SongDBExtended.bmson_song> bmsonSongs,
        Func<ChartListSourceRow, ResourceHealthWarningProjection> resourceHealthProjectionProvider = null,
        Func<ChartListSourceRow, PlaylistReferenceDisplay> playlistReferenceDisplayProvider = null,
        Func<LR2SongDBExtended.bmson_song, bool, ChartFileTransientState> bmsonTransientStateProvider = null)
    {
        List<ChartListSourceRow> rows =
        [
            .. (bmsFiles ?? [])
                .Select(file => FromBmsFile(file, resourceHealthProjectionProvider, playlistReferenceDisplayProvider, bmsonTransientStateProvider))
                .Where(row => row != null),
            .. (bmsonSongs ?? [])
                .Where(song => song != null && !string.IsNullOrWhiteSpace(song.path))
                .OrderBy(song => song.path, StringComparer.OrdinalIgnoreCase)
                .Select(song => FromBmsonSong(song, resourceHealthProjectionProvider, playlistReferenceDisplayProvider, bmsonTransientStateProvider))
                .Where(row => row != null),
        ];
        return rows;
    }

    internal static List<ChartListSourceRow> BuildStandardLibraryRows(
        IEnumerable<ChartFile> charts,
        Func<ChartListSourceRow, ResourceHealthWarningProjection> resourceHealthProjectionProvider = null,
        Func<ChartListSourceRow, PlaylistReferenceDisplay> playlistReferenceDisplayProvider = null,
        Func<LR2SongDBExtended.bmson_song, bool, ChartFileTransientState> bmsonTransientStateProvider = null)
    {
        return [.. (charts ?? [])
            .Select(chart => FromChartFile(chart, resourceHealthProjectionProvider, playlistReferenceDisplayProvider, bmsonTransientStateProvider))
            .Where(row => row != null)];
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
