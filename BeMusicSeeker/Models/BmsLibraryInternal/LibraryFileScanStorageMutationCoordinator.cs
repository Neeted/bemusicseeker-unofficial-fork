using System;
using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Coordinates applying file scan storage mutations without owning BMSLibrary storage state.
/// </summary>
internal sealed class LibraryFileScanStorageMutationCoordinator
{
    private readonly ILibraryFileScanStorageMutationHost host;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryFileScanStorageMutationCoordinator"/> class.
    /// </summary>
    /// <param name="host">The host that owns BMSLibrary private state.</param>
    internal LibraryFileScanStorageMutationCoordinator(ILibraryFileScanStorageMutationHost host)
    {
        this.host = host ?? throw new ArgumentNullException(nameof(host));
    }

    /// <summary>
    /// Applies file scan storage mutations while preserving the original lock, mutation, and fallback ordering.
    /// </summary>
    /// <param name="fileCheckResult">The file scan diff result.</param>
    /// <param name="reason">The file scan dispatch reason.</param>
    internal void Apply(SongTableFileCheckResult fileCheckResult, string reason)
    {
        if (fileCheckResult == null)
        {
            return;
        }

        using (host.EnterOwnedStorageWriteLock())
        {
            bool removedPayloadAvailable = host.TryCreateRemovedStorageOwnerIdentityCharts(
                fileCheckResult,
                out List<ChartFile> removedCharts);
            IResourceHealthInputMutationScope resourceHealthMutation = host.BeginResourceHealthInputMutation();
            try
            {
                host.BuildMutationResult(
                    fileCheckResult,
                    removedCharts,
                    removedPayloadAvailable,
                    resourceHealthMutation.BaseIndexCurrent);
                host.PublishOwnedCollectionChangeNotification();
                try
                {
                    using (host.SuppressResourceHealthIndexInvalidationIfNeeded())
                    {
                        host.ApplyStorageRowsResourceIndexAndOwnedCollectionReplacement(fileCheckResult);
                    }
                }
                catch
                {
                    host.ApplyFailureFallback();
                    throw;
                }
            }
            finally
            {
                resourceHealthMutation.Dispose();
            }
        }

        host.DispatchOwnedChartCollectionMutation(reason);
    }
}
