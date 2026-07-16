using System;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Bridges the extracted library-mutation workflow to BMSLibrary state that must remain private.
/// </summary>
internal interface ILibraryMutationDeltaApplyHost
{
    /// <summary>
    /// Throws when LR2 song DB synchronization currently owns the library mutation surface.
    /// </summary>
    /// <param name="operationName">The guarded operation name used for diagnostics.</param>
    void ThrowIfLr2SongDbSyncMutationBlocked(string operationName);

    /// <summary>
    /// Begins the resource-health input mutation window for the library delta.
    /// </summary>
    /// <returns>The active resource-health input mutation scope.</returns>
    IResourceHealthInputMutationScope BeginResourceHealthInputMutation();

    /// <summary>
    /// Builds and stores the owned-chart collection mutation for the current library delta.
    /// </summary>
    /// <param name="delta">The library mutation delta being applied.</param>
    /// <param name="baseInputVersion">The resource-health input version captured before mutation.</param>
    /// <param name="baseIndexCurrent">Whether the resource-health index was current at the base version.</param>
    void BuildMutationResult(LibraryMutationDelta delta, int baseInputVersion, bool baseIndexCurrent);

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
    /// Applies catalog storage-row removal for the current mutation result.
    /// </summary>
    /// <returns>The storage-row version snapshot after removal processing.</returns>
    StorageRowsVersionSnapshot ApplyCatalogStorageRowsRemoval();

    /// <summary>
    /// Applies the library delta to the broader BMSLibrary state.
    /// </summary>
    /// <param name="delta">The library mutation delta being applied.</param>
    /// <returns>Timing information reported by the state applier.</returns>
    BmsLibraryStateApplyResult ApplyLibraryMutationDeltaToState(LibraryMutationDelta delta);

    /// <summary>
    /// Applies the current owned-chart collection mutation after storage rows and state have been updated.
    /// </summary>
    /// <param name="storageRowsVersion">The storage-row version snapshot returned by unregister processing.</param>
    void ApplyOwnedChartCollectionMutation(StorageRowsVersionSnapshot storageRowsVersion);

    /// <summary>
    /// Completes resource-health mutation metadata after the input mutation scope is disposed.
    /// </summary>
    /// <param name="targetInputVersion">The target input version observed after the mutation window.</param>
    void CompleteResourceHealthMutation(int targetInputVersion);

    /// <summary>
    /// Synchronizes LR2 normal folder output for the current owned mutation.
    /// </summary>
    /// <param name="reason">The reason string used for sync diagnostics.</param>
    void SyncLr2NormalFoldersForOwnedMutation(string reason);

    /// <summary>
    /// Applies the failure fallback for the current mutation result and invalidates dependent caches.
    /// </summary>
    void ApplyFailureFallback();

    /// <summary>
    /// Dispatches the completed owned-chart collection mutation.
    /// </summary>
    /// <param name="reason">The dispatch reason.</param>
    void DispatchOwnedChartCollectionMutation(string reason);

    /// <summary>
    /// Writes the install-performance entry for a timed library mutation delta.
    /// </summary>
    /// <param name="delta">The library mutation delta that was applied.</param>
    /// <param name="performanceLogContext">The non-empty performance log context.</param>
    /// <param name="timings">The timings captured by the coordinator.</param>
    void LogLibraryMutationDeltaPerformance(
        LibraryMutationDelta delta,
        string performanceLogContext,
        LibraryMutationDeltaApplyTimings timings);
}
