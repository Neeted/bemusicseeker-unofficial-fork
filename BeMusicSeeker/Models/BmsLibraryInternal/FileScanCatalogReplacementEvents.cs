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
/// Immutable facts published when the catalog storage replacement could not be
/// applied. The original exception remains owned by the scan pipeline and is
/// rethrown after consumers invalidate their derived state.
/// </summary>
internal sealed class FileScanCatalogReplacementFailureEvent
{
    internal FileScanCatalogReplacementFailureEvent(
        CatalogFileScanStorageReplacementRequest request,
        bool resourceHealthIndexCurrentAtBase,
        string reason)
    {
        Request = request ?? throw new ArgumentNullException(nameof(request));
        ResourceHealthIndexCurrentAtBase = resourceHealthIndexCurrentAtBase;
        Reason = reason ?? string.Empty;
    }

    internal CatalogFileScanStorageReplacementRequest Request { get; }

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
        IReadOnlyList<ChartFile> installDestinationChangedCharts,
        bool invalidateInstalledDirectoryIndex,
        bool clearDuplicatedCache,
        string reason)
    {
        InstallDestinationChangedCharts = new List<ChartFile>(
            (installDestinationChangedCharts ?? [])
                .Select(ChartFileProjection.ToImmutableSnapshot)
                .Where(chart => chart != null))
            .AsReadOnly();
        InvalidateInstalledDirectoryIndex = invalidateInstalledDirectoryIndex;
        ClearDuplicatedCache = clearDuplicatedCache;
        Reason = reason ?? string.Empty;
    }

    internal IReadOnlyList<ChartFile> InstallDestinationChangedCharts { get; }

    internal bool InvalidateInstalledDirectoryIndex { get; }

    internal bool ClearDuplicatedCache { get; }

    internal string Reason { get; }

    internal static FileScanCatalogResidualEvent Create(LibraryMutationDelta delta, string reason)
    {
        if (delta == null)
        {
            throw new ArgumentNullException(nameof(delta));
        }

        List<string> unsupportedFields = [];
        if (delta.ChartRemoveRequests.Count > 0)
        {
            unsupportedFields.Add(nameof(delta.ChartRemoveRequests));
        }
        if (delta.AddedBmsFiles.Count > 0)
        {
            unsupportedFields.Add(nameof(delta.AddedBmsFiles));
        }
        if (delta.AddedBmsonSongs.Count > 0)
        {
            unsupportedFields.Add(nameof(delta.AddedBmsonSongs));
        }
        if (delta.ChartPathChanges.Count > 0)
        {
            unsupportedFields.Add(nameof(delta.ChartPathChanges));
        }
        if (delta.FolderPathChanges.Count > 0)
        {
            unsupportedFields.Add(nameof(delta.FolderPathChanges));
        }
        if (delta.UpdatedInstalledPackagePaths.Count > 0)
        {
            unsupportedFields.Add(nameof(delta.UpdatedInstalledPackagePaths));
        }
        if (delta.UpdatedInstallDestinations.Any(change => change?.Entry != null))
        {
            unsupportedFields.Add(nameof(delta.UpdatedInstallDestinations) + ".Entry");
        }
        if (delta.Failures.Count > 0)
        {
            unsupportedFields.Add(nameof(delta.Failures));
        }
        if (delta.NotifyStorageRowPathChanges)
        {
            unsupportedFields.Add(nameof(delta.NotifyStorageRowPathChanges));
        }
        if (delta.RaiseInstalledPackagesChanged)
        {
            unsupportedFields.Add(nameof(delta.RaiseInstalledPackagesChanged));
        }
        if (delta.InvalidateParentFolderCache)
        {
            unsupportedFields.Add(nameof(delta.InvalidateParentFolderCache));
        }
        if (delta.RenamedCount != 0)
        {
            unsupportedFields.Add(nameof(delta.RenamedCount));
        }
        if (delta.DuplicateDeletedCount != 0)
        {
            unsupportedFields.Add(nameof(delta.DuplicateDeletedCount));
        }
        if (delta.SkippedCount != 0)
        {
            unsupportedFields.Add(nameof(delta.SkippedCount));
        }
        if (delta.TotalMs != 0L)
        {
            unsupportedFields.Add(nameof(delta.TotalMs));
        }
        if (unsupportedFields.Count > 0)
        {
            throw new InvalidOperationException(
                "File scan residual contains unsupported mutation fields: "
                + string.Join(", ", unsupportedFields));
        }

        return new FileScanCatalogResidualEvent(
            delta.CreateAppliedInstallDestinationChartSnapshots(),
            delta.InvalidateInstalledDirectoryIndex,
            delta.ClearDuplicatedCache,
            reason);
    }
}
