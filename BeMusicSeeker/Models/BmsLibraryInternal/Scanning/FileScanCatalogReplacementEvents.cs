using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// 走査の正本確定後にcatalogと索引へ渡す変更不能な事実です。
/// 完全反映の索引置換と、0件保護付き解除の範囲外exact entry除去を区別します。
/// </summary>
internal sealed class FileScanCatalogReplacementEvent
{
    /// <summary>正本置換のreceiptと、完全索引または保護する検索範囲を同じ反映へ捕捉します。</summary>
    internal FileScanCatalogReplacementEvent(
        CatalogFileScanStorageReplacementRequest request,
        CatalogFileScanStorageReplacementReceipt receipt,
        LibraryResourceIndex nextResourceIndex,
        bool resourceHealthIndexCurrentAtBase,
        string reason, IReadOnlyList<string> protectedSearchRoots = null)
    {
        Request = request ?? throw new ArgumentNullException(nameof(request));
        Receipt = receipt ?? throw new ArgumentNullException(nameof(receipt));
        NextResourceIndex = nextResourceIndex;
        ProtectedSearchRoots = Array.AsReadOnly((protectedSearchRoots ?? []).ToArray());
        ResourceHealthIndexCurrentAtBase = resourceHealthIndexCurrentAtBase;
        Reason = reason ?? string.Empty;
    }

    internal CatalogFileScanStorageReplacementRequest Request { get; }

    internal CatalogFileScanStorageReplacementReceipt Receipt { get; }

    /// <summary>完全走査の置換索引。保護範囲がある場合は使用せず、現索引の範囲外entryだけを除きます。</summary>
    internal LibraryResourceIndex NextResourceIndex { get; }

    /// <summary>0件保護で索引を維持する実検索範囲。空の場合だけ全索引を置換します。</summary>
    internal IReadOnlyList<string> ProtectedSearchRoots { get; }

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
