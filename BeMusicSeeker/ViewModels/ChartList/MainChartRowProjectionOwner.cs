using System;
using System.Collections.Concurrent;
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
    // UIの導入先更新とpackage workerの投影が重なる。値は不変なので、全件複製せず一キーの読書きだけを同期する。
    private readonly ConcurrentDictionary<string, ChartFileTransientState> transientStatesByKey = new(StringComparer.Ordinal);
    private Func<ChartFile, ChartFileStatus> playbackStatusProvider;
    /// <summary>現在の再生状態の読取り口を、表示行の生成時へ直接渡します。</summary>
    internal void SetPlaybackStatusProvider(Func<ChartFile, ChartFileStatus> provider) => playbackStatusProvider = provider;

    private int chartInfoVersion;

    /// <summary>一覧入力の実訪問・投影・確保件数を観測します。投影はBMS/BMSONの形式別も通知し、未指定時は計測状態を持ちません。</summary>
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
        PlaylistReferenceDisplay referenceDisplay = scoreSource == null ? CaptureReferenceDisplay(library, row.Chart) : null;
        row.SetPlaylistReferenceDisplayProvider(scoreSource == null ? _ => referenceDisplay : _ => scoreSource.ReferenceDisplay);
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
        row?.SetPlaylistReferenceDisplayProvider(_ => sourceRow.ReferenceDisplay);
        return row;
    }

    /// <summary>専用package元集合全体を同じscore入力へ接続し、既存の投影一巡でMD5/SHAを登録して参照を捕捉してから返します。</summary>
    internal List<ChartListSourceRow> BuildPackageSourceRows(
        BMSLibrary library,
        IEnumerable<PackageChartEntry> entries,
        bool includeResourceHealth, ChartListScoreInput scoreInput = null, ChartListReferenceInput referenceInput = null)
    {
        scoreInput ??= new(library);
        referenceInput ??= new(library);
        var keys = new HashSet<PlaylistEntryLookupKey>(CatalogReadSnapshot.LookupKeyComparer);
        List<ChartListSourceRow> rows = ChartListSourceRow.BuildPackageRows(
            (entries ?? []).Select(entry => { NormalInputWorkObserver?.Invoke("package_projection", 1); RegisterReferenceKeys(keys, entry?.Chart); return entry; }),
            includeResourceHealth ? row => ResolveResourceHealth(library, row) : null,
            row => referenceInput.Resolve(row.CapturedChart.Md5, row.CapturedChart.Sha256),
            GetTransientState,
            chart => ResolveChartInfo(library, chart),
            row => ResolveChartInfo(library, row),
            () => ChartInfoVersion,
            () => 0, scoreInput.Resolve);
        referenceInput.CaptureInitial(keys);
        NormalInputWorkObserver?.Invoke("reference_capture", 1);
        return rows;
    }

    /// <summary>専用譜面元集合全体を表示filter前から同じscore入力へ接続し、同じ投影列挙で登録した両hashの参照値を捕捉します。</summary>
    internal List<ChartListSourceRow> BuildStandardSourceRows(
        BMSLibrary library,
        IEnumerable<ChartFile> charts,
        ChartListSourceProjectionMode projectionMode,
        bool includeResourceHealth, ChartListScoreInput scoreInput = null, ChartListReferenceInput referenceInput = null)
    {
        scoreInput ??= new(library);
        referenceInput ??= new(library);
        var keys = new HashSet<PlaylistEntryLookupKey>(CatalogReadSnapshot.LookupKeyComparer);
        List<ChartListSourceRow> rows = ChartListSourceRow.BuildStandardLibraryRows(
            (charts ?? []).Select(chart => { RegisterReferenceKeys(keys, chart); return chart; }),
            projectionMode,
            includeResourceHealth ? row => ResolveResourceHealth(library, row) : null,
            row => referenceInput.Resolve(row.CapturedChart.Md5, row.CapturedChart.Sha256),
            GetTransientState,
            chart => ResolveChartInfo(library, chart),
            row => ResolveChartInfo(library, row),
            () => ChartInfoVersion,
            () => 0,
            scoreInput.Resolve);
        referenceInput.CaptureInitial(keys);
        NormalInputWorkObserver?.Invoke("reference_capture", 1);
        return rows;
    }

    private static void RegisterReferenceKeys(HashSet<PlaylistEntryLookupKey> keys, ChartFile chart)
    {
        if (!string.IsNullOrWhiteSpace(chart?.Md5)) { keys.Add(new(PlaylistEntryLookupKeyKind.Md5, chart.Md5)); }
        if (!string.IsNullOrWhiteSpace(chart?.Sha256)) { keys.Add(new(PlaylistEntryLookupKeyKind.Sha256, chart.Sha256)); }
    }

    /// <summary>捕捉したカタログを直接読む通常一覧入力を作ります。投影のためだけに所属を複製しません。</summary>
    internal NormalLibrarySourceRows BuildNormalSourceRows(
        BMSLibrary library, CatalogChartCollectionSnapshot source, bool includeBmsonRows)
    {
        source ??= new CatalogChartCollectionSnapshot([], [], 0);
        // 全行で同じ提供元を共有する。行factoryの内側で作ると、全件分の同一delegateを保持して後続処理のGC負荷を増やす。
        Func<ChartListSourceRow, ResourceHealthWarningProjection> resourceHealth = candidate => ResolveResourceHealth(library, candidate);
        var referenceInput = new ChartListReferenceInput(library);
        Func<ChartListSourceRow, PlaylistReferenceDisplay> references = candidate => referenceInput.Resolve(candidate.Hash, candidate.Sha256);
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
            (kind, count) => NormalInputWorkObserver?.Invoke(kind, count), scoreInput, referenceInput);
    }

    /// <summary>同じ読取り境界で捕捉したentry値・順序と所持currentを一回の詳細投影へ渡します。参照入力がある場合はその不変値を共有します。</summary>
    internal PlaylistDetailSourceRow CreatePlaylistDetailSourceRow(
        BMSLibrary library,
        BMSTableEntry entry,
        ChartFile resolvedChart,
        BMSScore scoreSnapshot,
        BeMusicSeeker.Models.ChartDetails entryChartInfo,
        LibraryChartRef resolvedChartRef,
        PlaylistEntryValue entryValue = null,
        long entryOrder = 0,
        ChartListReferenceInput referenceInput = null)
    {
        PlaylistReferenceDisplay referenceDisplay = referenceInput == null ? CaptureReferenceDisplay(library, resolvedChart) : null;
        var row = new PlaylistDetailSourceRow(
            entry,
            resolvedChart,
            scoreSnapshot,
            entryChartInfo,
            referenceInput == null ? _ => referenceDisplay
                : chart => referenceInput.Resolve(chart?.Md5, chart?.Sha256),
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
                transientStatesByKey.TryRemove(key, out _);
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

    private static PlaylistReferenceDisplay CaptureReferenceDisplay(BMSLibrary library, ChartFile chart)
    {
        if (chart == null) { return PlaylistReferenceDisplay.Empty; }
        var input = new ChartListReferenceInput(library);
        var keys = new HashSet<PlaylistEntryLookupKey>(CatalogReadSnapshot.LookupKeyComparer);
        RegisterReferenceKeys(keys, chart);
        input.CaptureInitial(keys);
        return input.Resolve(chart.Md5, chart.Sha256);
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
        foreach (string key in transientStatesByKey.Keys)
        {
            if (currentKeys?.Contains(key) != true)
            {
                transientStatesByKey.TryRemove(key, out _);
            }
        }
    }
}
