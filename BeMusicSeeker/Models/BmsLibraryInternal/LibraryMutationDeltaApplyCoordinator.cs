using System;
using System.Diagnostics;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Coordinates the ordered application of a library mutation delta without owning BMSLibrary storage state.
/// </summary>
internal sealed class LibraryMutationDeltaApplyCoordinator
{
    private readonly ILibraryMutationDeltaApplyHost host;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryMutationDeltaApplyCoordinator"/> class.
    /// </summary>
    /// <param name="host">The host that owns BMSLibrary private state.</param>
    internal LibraryMutationDeltaApplyCoordinator(ILibraryMutationDeltaApplyHost host)
    {
        this.host = host ?? throw new ArgumentNullException(nameof(host));
    }

    /// <summary>
    /// Applies a library mutation delta using the default dispatch reason and no performance context.
    /// </summary>
    /// <param name="delta">The library mutation delta to apply.</param>
    internal void Apply(LibraryMutationDelta delta)
    {
        Apply(delta, performanceLogContext: null);
    }

    /// <summary>
    /// Applies a library mutation delta while preserving the original operation ordering and failure fallback.
    /// </summary>
    /// <param name="delta">The library mutation delta to apply.</param>
    /// <param name="performanceLogContext">The optional performance log context.</param>
    internal void Apply(LibraryMutationDelta delta, string performanceLogContext)
    {
        const string defaultReason = "library_delta";
        host.ThrowIfLr2SongDbSyncMutationBlocked("ApplyLibraryMutationDelta");
        bool collectPerformanceLog = !string.IsNullOrWhiteSpace(performanceLogContext);
        Stopwatch totalStopwatch = StartPerformanceStepStopwatch(collectPerformanceLog);
        var timings = new LibraryMutationDeltaApplyTimings();
        ResourceHealthIndexOwner.ResourceHealthInputMutation resourceHealthMutation = null;
        try
        {
            Stopwatch resourceHealthBeginStopwatch = StartPerformanceStepStopwatch(collectPerformanceLog);
            resourceHealthMutation = host.BeginResourceHealthInputMutation();
            timings.ResourceHealthBeginMs = StopPerformanceStepStopwatch(resourceHealthBeginStopwatch);
            try
            {
                Stopwatch buildMutationStopwatch = StartPerformanceStepStopwatch(collectPerformanceLog);
                host.BuildMutationResult(
                    delta,
                    resourceHealthMutation.BaseInputVersion,
                    resourceHealthMutation.BaseIndexCurrent);
                timings.BuildMutationMs = StopPerformanceStepStopwatch(buildMutationStopwatch);

                using (host.SuppressResourceHealthIndexInvalidationIfNeeded())
                {
                    Stopwatch stateApplyStopwatch = StartPerformanceStepStopwatch(collectPerformanceLog);
                    BmsLibraryStateApplyResult catalogStateApplyResult = host.ApplyCatalogMutationToState(delta);
                    timings.StateApplyMs = StopPerformanceStepStopwatch(stateApplyStopwatch);
                    timings.StateFolderDbMs = catalogStateApplyResult?.FolderDbMs ?? 0;
                    timings.StatePathMemoryApplyMs = catalogStateApplyResult?.PathMemoryApplyMs ?? 0;
                    timings.StateBmsPathDbMs = catalogStateApplyResult?.BmsPathDbMs ?? 0;
                    timings.StateBmsonPathDbMs = catalogStateApplyResult?.BmsonPathDbMs ?? 0;
                    timings.StateBmsRemovalDbMs = catalogStateApplyResult?.BmsRemovalDbMs ?? 0;
                    timings.StateBmsonRemovalDbMs = catalogStateApplyResult?.BmsonRemovalDbMs ?? 0;
                }
                Stopwatch publishNotificationStopwatch = StartPerformanceStepStopwatch(collectPerformanceLog);
                host.PublishOwnedCollectionChangeNotification();
                timings.PublishNotificationMs = StopPerformanceStepStopwatch(publishNotificationStopwatch);
                Stopwatch residualApplyStopwatch = StartPerformanceStepStopwatch(collectPerformanceLog);
                BmsLibraryStateApplyResult residualStateApplyResult = host.ApplyConsumerResidualState(delta);
                timings.StateApplyMs += StopPerformanceStepStopwatch(residualApplyStopwatch);
                timings.StatePackageApplyMs = residualStateApplyResult?.PackageApplyMs ?? 0;
            }
            finally
            {
                Stopwatch resourceHealthDisposeStopwatch = StartPerformanceStepStopwatch(collectPerformanceLog);
                resourceHealthMutation.Dispose();
                timings.ResourceHealthDisposeMs = StopPerformanceStepStopwatch(resourceHealthDisposeStopwatch);
            }
            host.CompleteResourceHealthMutation(resourceHealthMutation.TargetInputVersion);
            Stopwatch lr2NormalFolderSyncStopwatch = StartPerformanceStepStopwatch(collectPerformanceLog);
            host.SyncLr2NormalFoldersForOwnedMutation(performanceLogContext ?? defaultReason);
            timings.Lr2NormalFolderSyncMs = StopPerformanceStepStopwatch(lr2NormalFolderSyncStopwatch);
            Stopwatch dispatchStopwatch = StartPerformanceStepStopwatch(collectPerformanceLog);
            host.DispatchOwnedChartCollectionMutation(defaultReason);
            timings.DispatchMs = StopPerformanceStepStopwatch(dispatchStopwatch);
            if (collectPerformanceLog)
            {
                timings.ElapsedMs = StopPerformanceStepStopwatch(totalStopwatch);
                host.LogLibraryMutationDeltaPerformance(delta, performanceLogContext, timings);
            }
        }
        catch
        {
            host.RebaseResourceHealthAfterFailure(resourceHealthMutation);
            host.ApplyFailureFallback();
            throw;
        }
    }

    private static Stopwatch StartPerformanceStepStopwatch(bool enabled)
    {
        return enabled ? Stopwatch.StartNew() : null;
    }

    private static long StopPerformanceStepStopwatch(Stopwatch stopwatch)
    {
        if (stopwatch == null)
        {
            return 0;
        }
        stopwatch.Stop();
        return stopwatch.ElapsedMilliseconds;
    }
}
