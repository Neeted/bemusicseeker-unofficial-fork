using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// 一覧の表示投影と提供元の接続を所有します。scoreの位置は各元集合入力が管理します。
/// </summary>
internal sealed class MainChartRowProjectionOwner
{
    private readonly Dictionary<string, ChartFileTransientState> transientStatesByKey = new(StringComparer.Ordinal);
    private Func<ChartFile, ChartFileStatus> playbackStatusProvider;
    /// <summary>現在の再生状態の読取り口を、表示行の生成時へ直接渡します。</summary>
    internal void SetPlaybackStatusProvider(Func<ChartFile, ChartFileStatus> provider) => playbackStatusProvider = provider;

    private int chartInfoVersion;

    /// <summary>通常一覧入力の実訪問・投影・確保件数を観測します。未指定時は計測状態を持ちません。</summary>
    internal Action<string, int> NormalInputWorkObserver { get; set; }

    internal int ChartInfoVersion => Volatile.Read(ref chartInfoVersion);

    /// <summary>独立したchart-info提供元の版を捕捉します。score位置は配送rangeでのみ進めます。</summary>
    internal void CaptureVersions(BMSLibrary library)
    {
        Volatile.Write(ref chartInfoVersion, library?.ChartInfoIndexVersion ?? 0);
    }

    /// <summary>表示行へ専門提供元を接続します。元集合入力がある場合はその同じscore投影を共有します。</summary>
    internal void ConfigureLibraryRow(BMSLibrary library, LibraryChartRow row, ChartListSourceRow scoreSource = null)
    {
        if (row == null)
        {
            return;
        }
        row.SetChartTransientStateProvider(GetTransientState);
        row.SetScoreProjectionProvider(scoreSource == null
            ? chart => library?.ResolveChartScoreSnapshot(chart.Kind, chart.Path, chart.Md5, chart.Sha256)
            : _ => scoreSource.ScoreValue, () => 0);
        row.SetPlaybackStatusProvider(playbackStatusProvider);
        row.SetChartInfoProjectionProvider(chart => ResolveChartInfo(library, chart));
        row.SetResourceHealthProjectionProvider(candidate => ResolveResourceHealth(library, candidate));
        row.SetPlaylistReferenceDisplayProvider(candidate => ResolvePlaylistReferenceDisplay(library, candidate?.Chart));
    }

    internal void ConfigureLibraryRows(BMSLibrary library, IEnumerable<LibraryChartRow> rows)
    {
        foreach (LibraryChartRow row in rows ?? [])
        {
            ConfigureLibraryRow(library, row);
        }
    }

    internal LibraryChartRow CreateLibraryRow(BMSLibrary library, ChartFile chart)
    {
        var row = LibraryChartRow.FromChartFile(chart);
        ConfigureLibraryRow(library, row);
        return row;
    }

    internal LibraryChartRow CreatePackageRow(BMSLibrary library, PackageChartEntry entry)
    {
        var row = LibraryChartRow.FromPackageChartEntry(entry);
        ConfigureLibraryRow(library, row);
        return row;
    }

    /// <summary>
    /// 対象集合の表示行を入力行の現在値へ接続します。
    /// 導入先の再投影とパッケージ項目の変更通知を、生成時の値で固定しません。
    /// </summary>
    internal LibraryChartRow CreateSubsetRow(
        BMSLibrary library,
        ChartListSourceRow sourceRow,
        bool includeResourceHealth)
    {
        if (sourceRow == null)
        {
            return null;
        }
        var row = LibraryChartRow.FromSourceRow(sourceRow);
        if (includeResourceHealth)
        {
            row?.SetResourceHealthProjectionProvider(candidate => ResolveResourceHealth(library, candidate));
        }
        row?.SetPlaybackStatusProvider(playbackStatusProvider);
        row?.SetChartInfoProjectionProvider(chart => ResolveChartInfo(library, chart));
        row?.SetPlaylistReferenceDisplayProvider(candidate => ResolvePlaylistReferenceDisplay(library, candidate?.Chart));
        return row;
    }

    /// <summary>専用package元集合全体を、最初のscore投影前に捕捉した同じlookupへ接続します。</summary>
    internal List<ChartListSourceRow> BuildPackageSourceRows(
        BMSLibrary library,
        IEnumerable<PackageChartEntry> entries,
        bool includeResourceHealth, ChartListScoreInput scoreInput = null)
    {
        scoreInput ??= new(library);
        return ChartListSourceRow.BuildPackageRows(
            entries,
            includeResourceHealth ? row => ResolveResourceHealth(library, row) : null,
            row => ResolvePlaylistReferenceDisplay(library, row),
            GetTransientState,
            chart => ResolveChartInfo(library, chart),
            row => ResolveChartInfo(library, row),
            () => ChartInfoVersion,
            () => 0, scoreInput.Resolve);
    }

    /// <summary>専用譜面元集合全体を、表示filter前から同じscore入力へ接続します。</summary>
    internal List<ChartListSourceRow> BuildStandardSourceRows(
        BMSLibrary library,
        IEnumerable<ChartFile> charts,
        ChartListSourceProjectionMode projectionMode,
        bool includeResourceHealth, ChartListScoreInput scoreInput = null)
    {
        scoreInput ??= new(library);
        return ChartListSourceRow.BuildStandardLibraryRows(
            charts,
            projectionMode,
            includeResourceHealth ? row => ResolveResourceHealth(library, row) : null,
            row => ResolvePlaylistReferenceDisplay(library, row),
            GetTransientState,
            chart => ResolveChartInfo(library, chart),
            row => ResolveChartInfo(library, row),
            () => ChartInfoVersion,
            () => 0,
            scoreInput.Resolve);
    }

    /// <summary>捕捉したカタログを直接読む通常一覧入力を作ります。投影のためだけに所属を複製しません。</summary>
    internal NormalLibrarySourceRows BuildNormalSourceRows(
        BMSLibrary library, CatalogChartCollectionSnapshot source, bool includeBmsonRows)
    {
        source ??= new CatalogChartCollectionSnapshot([], [], 0);
        // 全行で同じ提供元を共有する。行factoryの内側で作ると、全件分の同一delegateを保持して後続処理のGC負荷を増やす。
        Func<ChartListSourceRow, ResourceHealthWarningProjection> resourceHealth = candidate => ResolveResourceHealth(library, candidate);
        Func<ChartListSourceRow, PlaylistReferenceDisplay> references = candidate => ResolvePlaylistReferenceDisplay(library, candidate);
        Func<ChartFile, bool, ChartFileTransientState> transientState = GetTransientState;
        Func<ChartFile, BeMusicSeeker.Models.ChartDetails> chartInfo = value => ResolveChartInfo(library, value);
        Func<ChartListSourceRow, BeMusicSeeker.Models.ChartDetails> rowChartInfo = candidate => ResolveChartInfo(library, candidate);
        Func<int> chartInfoVersion = () => ChartInfoVersion;
        var scoreInput = new ChartListScoreInput(library);
        Func<int> scoreVersion = () => 0;
        Func<ChartListSourceRow, ChartScoreSnapshot> score = scoreInput.Resolve;
        return new NormalLibrarySourceRows(library, source, includeBmsonRows,
            chart => ChartListSourceRow.FromChartFile(chart, ChartListSourceProjectionMode.OwnerBacked,
                resourceHealth, references, transientState, chartInfo, rowChartInfo, chartInfoVersion, scoreVersion, score), null,
            (kind, count) => NormalInputWorkObserver?.Invoke(kind, count), scoreInput);
    }

    /// <summary>同じ読取り境界で捕捉したentry値・順序と所持currentを、一回の詳細投影へ渡します。</summary>
    internal PlaylistDetailSourceRow CreatePlaylistDetailSourceRow(
        BMSLibrary library,
        BMSTableEntry entry,
        ChartFile resolvedChart,
        BMSScore scoreSnapshot,
        BeMusicSeeker.Models.ChartDetails entryChartInfo,
        LibraryChartRef resolvedChartRef,
        PlaylistEntryValue entryValue = null,
        long entryOrder = 0)
    {
        var row = new PlaylistDetailSourceRow(
            entry,
            resolvedChart,
            scoreSnapshot,
            entryChartInfo,
            chart => ResolvePlaylistReferenceDisplay(library, chart),
            GetTransientState,
            chart => ResolveChartInfo(library, chart),
            resolvedChartRef,
            entryValue,
            entryOrder);
        row.PlaybackStatusProvider = playbackStatusProvider;
        return row;
    }

    internal ChartFileTransientState GetTransientState(ChartFile chart, bool includeWarningSnapshot)
    {
        string key = ChartFileRuntimeStateKey.Create(chart);
        ChartFileTransientState state = !string.IsNullOrWhiteSpace(key) && transientStatesByKey.TryGetValue(key, out ChartFileTransientState found) && found != null
            ? (includeWarningSnapshot ? found : found.WithoutWarnings()) : ChartFileTransientState.Empty;
        return state;
    }

    internal void UpdateTransientStates(IEnumerable<ChartFile> charts, bool forceInstallDestinationProjection = false)
    {
        foreach (ChartFile chart in charts ?? [])
        {
            string key = ChartFileRuntimeStateKey.Create(chart);
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }
            var state = ChartFileTransientState.FromInstallDestinationState(
                chart,
                includeWarningSnapshot: true,
                forceInstallDestinationProjection: forceInstallDestinationProjection,
                forceWarningProjection: forceInstallDestinationProjection);
            if (state.HasState)
            {
                transientStatesByKey[key] = state;
            }
            else
            {
                transientStatesByKey.Remove(key);
            }
        }
    }

    internal void PruneTransientStates(IEnumerable<ChartFile> currentCharts)
    {
        var currentKeys = new HashSet<string>(
            (currentCharts ?? [])
                .Select(ChartFileRuntimeStateKey.Create)
                .Where(key => !string.IsNullOrWhiteSpace(key)),
            StringComparer.Ordinal);
        PruneTransientStates(currentKeys);
    }

    internal void PruneTransientStatesToOwnedCharts(BMSLibrary library)
    {
        if (transientStatesByKey.Count == 0)
        {
            return;
        }
        PruneTransientStates(
            library?.CreateOwnedChartRuntimeStatePrimaryKeySnapshot()
            ?? new HashSet<string>(StringComparer.Ordinal));
    }

    internal void ClearTransientStates()
    {
        transientStatesByKey.Clear();
    }

    internal ResourceHealthWarningProjection ResolveResourceHealth(BMSLibrary library, ChartListSourceRow row)
    {
        return library == null || row == null
            ? ResourceHealthWarningProjection.Empty
            : library.TryGetCurrentResourceHealthWarningProjection(row.Kind, row.Path, row.Hash);
    }

    internal BeMusicSeeker.Models.ChartDetails ResolveChartInfo(BMSLibrary library, ChartFile chart)
    {
        return chart == null ? null : library?.ResolveChartInfo(chart.Sha256, chart.Md5);
    }

    internal BeMusicSeeker.Models.ChartDetails ResolveChartInfo(BMSLibrary library, ChartListSourceRow row)
    {
        return row == null ? null : library?.ResolveChartInfo(row.Sha256, row.Hash);
    }

    internal PlaylistReferenceDisplay ResolvePlaylistReferenceDisplay(BMSLibrary library, ChartFile chart)
    {
        return library?.GetPlaylistReferenceDisplay(chart) ?? PlaylistReferenceDisplay.Empty;
    }

    internal PlaylistReferenceDisplay ResolvePlaylistReferenceDisplay(BMSLibrary library, ChartListSourceRow row)
    {
        return row == null
            ? PlaylistReferenceDisplay.Empty
            : library?.GetPlaylistReferenceDisplay(row.Hash, row.Sha256) ?? PlaylistReferenceDisplay.Empty;
    }

    private ResourceHealthWarningProjection ResolveResourceHealth(BMSLibrary library, LibraryChartRow row)
    {
        ChartFile chart = row?.Chart;
        return library == null || chart == null
            ? ResourceHealthWarningProjection.Empty
            : library.TryGetCurrentResourceHealthWarningProjection(chart);
    }

    private void PruneTransientStates(ISet<string> currentKeys)
    {
        foreach (string key in transientStatesByKey.Keys.ToList())
        {
            if (currentKeys?.Contains(key) != true)
            {
                transientStatesByKey.Remove(key);
            }
        }
    }
}
