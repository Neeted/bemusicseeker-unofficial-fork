using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Owns live projection state and provider wiring shared by every workflow presented in the main chart table.
/// </summary>
internal sealed class MainChartRowProjectionOwner
{
    private readonly Dictionary<string, ChartFileTransientState> transientStatesByKey = new(StringComparer.Ordinal);
    private Func<ChartFile, ChartFileStatus> playbackStatusProvider;
    /// <summary>現在の再生状態の読取り口を、表示行の生成時へ直接渡します。</summary>
    internal void SetPlaybackStatusProvider(Func<ChartFile, ChartFileStatus> provider) => playbackStatusProvider = provider;

    private int chartInfoVersion;
    private int scoreSnapshotVersion;

    internal int ChartInfoVersion => Volatile.Read(ref chartInfoVersion);

    internal int ScoreSnapshotVersion => Volatile.Read(ref scoreSnapshotVersion);

    internal void CaptureVersions(BMSLibrary library)
    {
        Volatile.Write(ref chartInfoVersion, library?.ChartInfoIndexVersion ?? 0);
        Volatile.Write(ref scoreSnapshotVersion, library?.ScoreSnapshotVersion ?? 0);
    }

    internal void ConfigureLibraryRow(BMSLibrary library, LibraryChartRow row)
    {
        if (row == null)
        {
            return;
        }
        row.SetChartTransientStateProvider(GetTransientState);
        row.SetScoreProjectionProvider(
            chart => library?.ResolveChartScoreSnapshot(chart.Kind, chart.Path, chart.Md5, chart.Sha256),
            () => ScoreSnapshotVersion);
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

    internal List<ChartListSourceRow> BuildPackageSourceRows(
        BMSLibrary library,
        IEnumerable<PackageChartEntry> entries,
        bool includeResourceHealth)
    {
        return ChartListSourceRow.BuildPackageRows(
            entries,
            includeResourceHealth ? row => ResolveResourceHealth(library, row) : null,
            row => ResolvePlaylistReferenceDisplay(library, row),
            GetTransientState,
            chart => ResolveChartInfo(library, chart),
            row => ResolveChartInfo(library, row),
            () => ChartInfoVersion,
            () => ScoreSnapshotVersion);
    }

    internal List<ChartListSourceRow> BuildStandardSourceRows(
        BMSLibrary library,
        IEnumerable<ChartFile> charts,
        ChartListSourceProjectionMode projectionMode,
        bool includeResourceHealth)
    {
        return ChartListSourceRow.BuildStandardLibraryRows(
            charts,
            projectionMode,
            includeResourceHealth ? row => ResolveResourceHealth(library, row) : null,
            row => ResolvePlaylistReferenceDisplay(library, row),
            GetTransientState,
            chart => ResolveChartInfo(library, chart),
            row => ResolveChartInfo(library, row),
            () => ChartInfoVersion,
            () => ScoreSnapshotVersion,
            row => ResolveScoreSnapshot(library, row));
    }

    /// <summary>捕捉したカタログを直接読む通常一覧入力を作ります。投影のためだけに所属を複製しません。</summary>
    internal NormalLibrarySourceRows BuildNormalSourceRows(
        BMSLibrary library, CatalogChartCollectionSnapshot source, bool includeBmsonRows, NormalLibrarySourceRows previous = null)
    {
        source ??= new CatalogChartCollectionSnapshot([], [], 0);
        // 全行で同じ提供元を共有する。行factoryの内側で作ると、全件分の同一delegateを保持して後続処理のGC負荷を増やす。
        Func<ChartListSourceRow, ResourceHealthWarningProjection> resourceHealth = candidate => ResolveResourceHealth(library, candidate);
        Func<ChartListSourceRow, PlaylistReferenceDisplay> references = candidate => ResolvePlaylistReferenceDisplay(library, candidate);
        Func<ChartFile, bool, ChartFileTransientState> transientState = GetTransientState;
        Func<ChartFile, BeMusicSeeker.Models.ChartDetails> chartInfo = value => ResolveChartInfo(library, value);
        Func<ChartListSourceRow, BeMusicSeeker.Models.ChartDetails> rowChartInfo = candidate => ResolveChartInfo(library, candidate);
        Func<int> chartInfoVersion = () => ChartInfoVersion;
        Func<int> scoreVersion = () => ScoreSnapshotVersion;
        Func<ChartListSourceRow, ChartScoreSnapshot> score = candidate => ResolveScoreSnapshot(library, candidate);
        return new NormalLibrarySourceRows(library, source, includeBmsonRows,
            chart => ChartListSourceRow.FromChartFile(chart, ChartListSourceProjectionMode.OwnerBacked,
                resourceHealth, references, transientState, chartInfo, rowChartInfo, chartInfoVersion, scoreVersion, score), previous);
    }

    internal PlaylistDetailSourceRow CreatePlaylistDetailSourceRow(
        BMSLibrary library,
        BMSTableEntry entry,
        ChartFile resolvedChart,
        BMSScore scoreSnapshot,
        BeMusicSeeker.Models.ChartDetails entryChartInfo,
        LibraryChartRef resolvedChartRef)
    {
        var row = new PlaylistDetailSourceRow(
            entry,
            resolvedChart,
            scoreSnapshot,
            entryChartInfo,
            chart => ResolvePlaylistReferenceDisplay(library, chart),
            GetTransientState,
            chart => ResolveChartInfo(library, chart),
            resolvedChartRef);
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

    internal ChartScoreSnapshot ResolveScoreSnapshot(BMSLibrary library, ChartListSourceRow row)
    {
        return library == null || row == null
            ? ChartScoreSnapshot.MissingChart
            : library.ResolveChartScoreSnapshot(row.Kind, row.Path, row.Hash, row.Sha256);
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
