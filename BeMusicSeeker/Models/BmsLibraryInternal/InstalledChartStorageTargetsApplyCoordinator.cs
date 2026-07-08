using System;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Coordinates applying installed chart storage targets without owning BMSLibrary storage state.
/// </summary>
internal sealed class InstalledChartStorageTargetsApplyCoordinator
{
    private readonly IInstalledChartStorageTargetsApplyHost host;

    /// <summary>
    /// Initializes a new instance of the <see cref="InstalledChartStorageTargetsApplyCoordinator"/> class.
    /// </summary>
    /// <param name="host">The host that owns BMSLibrary private state.</param>
    internal InstalledChartStorageTargetsApplyCoordinator(IInstalledChartStorageTargetsApplyHost host)
    {
        this.host = host ?? throw new ArgumentNullException(nameof(host));
    }

    /// <summary>
    /// Applies installed chart storage targets while preserving the original mutation ordering and failure fallback.
    /// </summary>
    /// <param name="addedTargets">The storage targets being added.</param>
    /// <param name="lookupReason">The dispatch and lookup reason.</param>
    internal void Apply(ChartStorageTargetSet addedTargets, string lookupReason)
    {
        if (addedTargets == null)
        {
            return;
        }

        host.ThrowIfLr2SongDbSyncMutationBlocked("ApplyInstalledChartStorageTargets");
        try
        {
            IResourceHealthInputMutationScope resourceHealthMutation = host.BeginResourceHealthInputMutation();
            try
            {
                host.BuildMutationResult(
                    addedTargets,
                    resourceHealthMutation.BaseInputVersion,
                    resourceHealthMutation.BaseIndexCurrent);
                host.PublishOwnedCollectionChangeNotification();
                using (host.SuppressResourceHealthIndexInvalidationIfNeeded())
                {
                    StorageRowsVersionSnapshot storageRowsVersion = host.ApplyInstalledChartStorageRowsUnsafe(addedTargets);
                    host.ApplyOwnedChartCollectionMutation(storageRowsVersion);
                }
            }
            finally
            {
                resourceHealthMutation.Dispose();
            }

            host.CompleteResourceHealthMutation(resourceHealthMutation.TargetInputVersion);
            host.SyncLr2NormalFoldersForOwnedMutation(lookupReason ?? "install_package");
            host.DispatchOwnedChartCollectionMutation(lookupReason);
        }
        catch
        {
            host.ApplyFailureFallback();
            throw;
        }
    }
}
