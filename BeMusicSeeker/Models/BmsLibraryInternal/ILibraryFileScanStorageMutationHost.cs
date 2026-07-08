using System;
using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Bridges the file scan storage mutation workflow to BMSLibrary state that must remain private.
/// </summary>
internal interface ILibraryFileScanStorageMutationHost
{
    /// <summary>
    /// Enters the owned storage write lock used by file scan storage mutation.
    /// </summary>
    /// <returns>The active write-lock scope.</returns>
    IDisposable EnterOwnedStorageWriteLock();

    /// <summary>
    /// Creates removed-chart payloads from the current owned collection when available.
    /// </summary>
    /// <param name="fileCheckResult">The file scan diff result.</param>
    /// <param name="removedCharts">Removed chart payloads when the current owned collection is available.</param>
    /// <returns><see langword="true"/> when removed payloads are available; otherwise <see langword="false"/>.</returns>
    bool TryCreateRemovedStorageOwnerIdentityCharts(
        SongTableFileCheckResult fileCheckResult,
        out List<ChartFile> removedCharts);

    /// <summary>
    /// Begins the resource-health input mutation window.
    /// </summary>
    /// <returns>The active resource-health input mutation scope.</returns>
    IResourceHealthInputMutationScope BeginResourceHealthInputMutation();

    /// <summary>
    /// Builds and stores the owned-chart collection mutation result for the file scan diff.
    /// </summary>
    /// <param name="fileCheckResult">The file scan diff result.</param>
    /// <param name="removedCharts">Removed chart payloads created from the current owned collection.</param>
    /// <param name="removedPayloadAvailable">Whether removed chart payloads were available.</param>
    /// <param name="baseIndexCurrent">Whether the resource-health index was current at the base version.</param>
    void BuildMutationResult(
        SongTableFileCheckResult fileCheckResult,
        List<ChartFile> removedCharts,
        bool removedPayloadAvailable,
        bool baseIndexCurrent);

    /// <summary>
    /// Publishes the owned collection change notification required before storage state is mutated.
    /// </summary>
    void PublishOwnedCollectionChangeNotification();

    /// <summary>
    /// Suppresses resource-health invalidation while storage rows are mutated when the current result requires it.
    /// </summary>
    /// <returns>A suppression scope, or <see langword="null"/> when suppression is unnecessary.</returns>
    IDisposable SuppressResourceHealthIndexInvalidationIfNeeded();

    /// <summary>
    /// Applies storage rows, resource index state, and owned collection replacement for the file scan diff.
    /// </summary>
    /// <param name="fileCheckResult">The file scan diff result.</param>
    void ApplyStorageRowsResourceIndexAndOwnedCollectionReplacement(SongTableFileCheckResult fileCheckResult);

    /// <summary>
    /// Applies the failure fallback for the current mutation result and invalidates dependent caches.
    /// </summary>
    void ApplyFailureFallback();

    /// <summary>
    /// Dispatches the completed file scan storage mutation.
    /// </summary>
    /// <param name="reason">The original file scan reason.</param>
    void DispatchOwnedChartCollectionMutation(string reason);
}
