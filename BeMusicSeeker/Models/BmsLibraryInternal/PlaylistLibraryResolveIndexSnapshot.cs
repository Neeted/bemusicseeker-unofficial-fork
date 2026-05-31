using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// playlist detail の entry hash から、現在の所持 chart 代表を解決するための snapshot です。
/// </summary>
internal sealed class PlaylistLibraryResolveIndexSnapshot
{
    private static readonly PlaylistLibraryResolveIndexSnapshot empty = new(
        new Dictionary<string, LibraryChartRef>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, LibraryChartRef>(StringComparer.OrdinalIgnoreCase));

    private PlaylistLibraryResolveIndexSnapshot(
        Dictionary<string, LibraryChartRef> chartsByMd5,
        Dictionary<string, LibraryChartRef> chartsBySha256)
    {
        ChartsByMd5 = chartsByMd5 ?? new Dictionary<string, LibraryChartRef>(StringComparer.OrdinalIgnoreCase);
        ChartsBySha256 = chartsBySha256 ?? new Dictionary<string, LibraryChartRef>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 空の resolve index です。
    /// </summary>
    internal static PlaylistLibraryResolveIndexSnapshot Empty => empty;

    /// <summary>
    /// MD5 から代表 chart を解決する index です。
    /// </summary>
    internal IReadOnlyDictionary<string, LibraryChartRef> ChartsByMd5 { get; }

    /// <summary>
    /// SHA256 から代表 chart を解決する index です。
    /// </summary>
    internal IReadOnlyDictionary<string, LibraryChartRef> ChartsBySha256 { get; }

    /// <summary>
    /// storage owner identity から playlist detail 用 resolve index を構築します。
    /// </summary>
    /// <param name="charts">owned collection の storage owner identity chart。</param>
    /// <param name="cancellationCheck">構築中に呼び出す cancellation callback。</param>
    /// <returns>playlist detail 用 resolve index。</returns>
    internal static PlaylistLibraryResolveIndexSnapshot FromStorageOwnerCharts(
        IEnumerable<ChartFile> charts,
        Action cancellationCheck = null)
    {
        return FromLibraryChartRefs(
            (charts ?? [])
                .Select(CreateStorageOwnerRef)
                .Where(chart => chart != null),
            cancellationCheck);
    }

    /// <summary>
    /// chart ref 列挙から playlist detail 用 resolve index を構築します。
    /// </summary>
    /// <param name="charts">登録対象の chart ref。</param>
    /// <param name="cancellationCheck">構築中に呼び出す cancellation callback。</param>
    /// <returns>playlist detail 用 resolve index。</returns>
    internal static PlaylistLibraryResolveIndexSnapshot FromLibraryChartRefs(
        IEnumerable<LibraryChartRef> charts,
        Action cancellationCheck = null)
    {
        var chartsByMd5 = new Dictionary<string, LibraryChartRef>(StringComparer.OrdinalIgnoreCase);
        var chartsBySha256 = new Dictionary<string, LibraryChartRef>(StringComparer.OrdinalIgnoreCase);
        foreach (LibraryChartRef chart in charts ?? [])
        {
            cancellationCheck?.Invoke();
            AddChart(chartsByMd5, chartsBySha256, chart);
        }
        return new PlaylistLibraryResolveIndexSnapshot(chartsByMd5, chartsBySha256);
    }

    /// <summary>
    /// playlist entry の md5 / sha256 から、現在ライブラリに存在する chart を解決します。
    /// </summary>
    /// <param name="entry">解決対象の playlist entry。</param>
    /// <returns>一致した chart。見つからない場合は null。</returns>
    internal LibraryChartRef ResolveChartForPlaylistEntry(BMSTableEntry entry)
    {
        PlaylistEntryLookupKey lookupKey = PlaylistEntryLookupKey.FromEntry(entry);
        if (!lookupKey.HasValue)
        {
            return null;
        }
        if (lookupKey.Kind == PlaylistEntryLookupKeyKind.Md5 && ChartsByMd5.TryGetValue(lookupKey.Hash, out LibraryChartRef resolvedByMd5))
        {
            return resolvedByMd5;
        }
        if (lookupKey.Kind == PlaylistEntryLookupKeyKind.Sha256 && ChartsBySha256.TryGetValue(lookupKey.Hash, out LibraryChartRef resolvedBySha256))
        {
            return resolvedBySha256;
        }
        return null;
    }

    /// <summary>
    /// 同じ hash に一致した chart の代表を deterministic に選びます。
    /// </summary>
    /// <param name="existing">現在の代表 chart。</param>
    /// <param name="candidate">新しい候補 chart。</param>
    /// <returns>採用する代表 chart。</returns>
    internal static LibraryChartRef ChoosePreferredRepresentative(LibraryChartRef existing, LibraryChartRef candidate)
    {
        if (existing == null)
        {
            return candidate;
        }
        if (candidate == null)
        {
            return existing;
        }
        return string.Compare(candidate.Path ?? string.Empty, existing.Path ?? string.Empty, StringComparison.OrdinalIgnoreCase) < 0 ? candidate : existing;
    }

    private static void AddChart(
        Dictionary<string, LibraryChartRef> chartsByMd5,
        Dictionary<string, LibraryChartRef> chartsBySha256,
        LibraryChartRef chart)
    {
        if (chart == null || string.IsNullOrWhiteSpace(chart.Path) || string.IsNullOrWhiteSpace(chart.Md5))
        {
            return;
        }
        chartsByMd5[chart.Md5] = ChoosePreferredRepresentative(
            chartsByMd5.TryGetValue(chart.Md5, out LibraryChartRef existingByMd5) ? existingByMd5 : null,
            chart);
        if (!string.IsNullOrWhiteSpace(chart.Sha256))
        {
            chartsBySha256[chart.Sha256] = ChoosePreferredRepresentative(
                chartsBySha256.TryGetValue(chart.Sha256, out LibraryChartRef existingBySha256) ? existingBySha256 : null,
                chart);
        }
    }

    private static LibraryChartRef CreateStorageOwnerRef(ChartFile chart)
    {
        if (chart == null)
        {
            return null;
        }
        BMSFile bmsOwner = chart.GetBmsStorageOwner();
        if (bmsOwner != null)
        {
            return LibraryChartRef.FromBmsFile(bmsOwner);
        }
        LR2SongDBExtended.bmson_song bmsonOwner = chart.GetBmsonStorageOwner();
        return bmsonOwner != null ? LibraryChartRef.FromBmsonSong(bmsonOwner) : LibraryChartRef.FromChartFile(chart);
    }
}
