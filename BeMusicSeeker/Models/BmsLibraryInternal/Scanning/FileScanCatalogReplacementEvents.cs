using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Immutable facts published after the scan pipeline has committed its catalog
/// storage replacement. Consumer-specific cache and presentation effects are
/// composed by the application-facing library facade.
/// </summary>
internal sealed class FileScanCatalogReplacementEvent
{
    internal FileScanCatalogReplacementEvent(
        CatalogFileScanStorageReplacementRequest request,
        CatalogFileScanStorageReplacementReceipt receipt,
        LibraryResourceIndex nextResourceIndex,
        bool resourceHealthIndexCurrentAtBase,
        string reason)
    {
        Request = request ?? throw new ArgumentNullException(nameof(request));
        Receipt = receipt ?? throw new ArgumentNullException(nameof(receipt));
        NextResourceIndex = nextResourceIndex;
        ResourceHealthIndexCurrentAtBase = resourceHealthIndexCurrentAtBase;
        Reason = reason ?? string.Empty;
    }

    internal CatalogFileScanStorageReplacementRequest Request { get; }

    internal CatalogFileScanStorageReplacementReceipt Receipt { get; }

    internal LibraryResourceIndex NextResourceIndex { get; }

    internal bool ResourceHealthIndexCurrentAtBase { get; }

    internal string Reason { get; }
}

/// <summary>
/// Immutable scan-only residual facts that are applied after the catalog
/// replacement. These facts intentionally exclude generic catalog, package,
/// playlist-reference, and LR2 mutations.
/// </summary>
internal sealed class FileScanCatalogResidualEvent
{
    private FileScanCatalogResidualEvent(
        IEnumerable<ChartFile> installDestinationChangedCharts,
        string reason)
    {
        InstallDestinationChangedCharts = new List<ChartFile>(
            (installDestinationChangedCharts ?? [])
                .Select(ChartFileProjection.ToImmutableSnapshot)
                .Where(chart => chart != null))
            .AsReadOnly();
        Reason = reason ?? string.Empty;
    }

    internal IReadOnlyList<ChartFile> InstallDestinationChangedCharts { get; }

    internal string Reason { get; }

    /// <summary>
    /// 走査結果から解除済み install destination の immutable facts を作成します。
    /// </summary>
    /// <param name="installDestinationChangedCharts">解除後の chart projection。</param>
    /// <param name="reason">走査理由。</param>
    /// <returns>共通反映へ渡す residual facts。</returns>
    internal static FileScanCatalogResidualEvent Create(
        IEnumerable<ChartFile> installDestinationChangedCharts,
        string reason)
    {
        if (installDestinationChangedCharts == null)
        {
            throw new ArgumentNullException(nameof(installDestinationChangedCharts));
        }

        return new FileScanCatalogResidualEvent(
            installDestinationChangedCharts,
            reason);
    }
}
