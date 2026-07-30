using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using BeMusicSeeker.Diagnostics;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Owns the resource-health index state and the versioned commands that update it.
/// Catalog, database, and UI composition remain outside this owner and are supplied
/// as immutable target snapshots or narrow version/target providers.
/// </summary>
internal sealed class ResourceHealthIndexOwner
{
    private readonly object stateLock = new();

    private readonly BmsLibraryMaintenanceService maintenanceService;

    private readonly Action<string> logPerformance;

    // Composition supplies only the version facts needed to validate a publish.
    private readonly Func<ResourceHealthIndexCurrentVersion> currentVersionProvider;

    private ResourceHealthIndexSnapshotState state = new(
        ResourceHealthIndexSnapshot.Empty,
        -1,
        new StorageRowsVersionSnapshot(-1, -1),
        -1);

    private bool invalidated = true;

    private int suppressionDepth;

    private int snapshotVersionSeed;

    private int inputVersion;

    private int inputMutationDepth;

    internal ResourceHealthIndexOwner(
        BmsLibraryMaintenanceService maintenanceService,
        Action<string> logPerformance,
        Func<ResourceHealthIndexCurrentVersion> currentVersionProvider)
    {
        this.maintenanceService = maintenanceService ?? throw new ArgumentNullException(nameof(maintenanceService));
        this.logPerformance = logPerformance ?? throw new ArgumentNullException(nameof(logPerformance));
        this.currentVersionProvider = currentVersionProvider ?? throw new ArgumentNullException(nameof(currentVersionProvider));
    }

    internal int CurrentInputVersion => Volatile.Read(ref inputVersion);

    internal bool IsCurrent()
    {
        return IsCurrentState(Volatile.Read(ref state));
    }

    internal ResourceHealthInputMutation BeginInputMutation()
    {
        lock (stateLock)
        {
            int baseVersion = inputVersion;
            bool baseIndexCurrent = inputMutationDepth == 0
                && IsCurrentState(state);
            if (inputMutationDepth == 0)
            {
                IncrementInputVersionUnsafe();
            }
            inputMutationDepth++;
            return new ResourceHealthInputMutation(this, baseVersion, baseIndexCurrent);
        }
    }

    internal IDisposable SuppressInvalidation()
    {
        lock (stateLock)
        {
            suppressionDepth++;
        }
        return new InvalidationSuppression(this);
    }

    internal void Invalidate(string reason)
    {
        InvalidateCore(reason, ignoreSuppression: false);
    }

    internal void ForceInvalidate(string reason)
    {
        InvalidateCore(reason, ignoreSuppression: true);
    }

    internal ResourceHealthIndexSnapshot GetPublishedSnapshotOrEmpty()
    {
        return Volatile.Read(ref state)?.Snapshot ?? ResourceHealthIndexSnapshot.Empty;
    }

    internal ResourceHealthIndexSnapshot EnsureCurrent(
        string reason,
        ResourceMaintenanceTargetSet fullOwnedTargetSnapshot,
        ResourceHealthIndexCurrentVersion currentVersion,
        bool forceRebuild = false)
    {
        ResourceHealthIndexSnapshotState currentState = Volatile.Read(ref state);
        if (!forceRebuild && IsCurrentState(currentState))
        {
            return currentState.Snapshot;
        }

        ResourceMaintenanceTargetSet targetSet = fullOwnedTargetSnapshot;
        if (!targetSet.HasFullOwnedVersion)
        {
            Invalidate(reason);
            return ResourceHealthIndexSnapshot.Empty;
        }
        if (!ResourceHealthFullOwnedTargetFreshness.IsCurrent(
            targetSet,
            currentVersion.StorageRowsVersion,
            currentVersion.OwnedCollectionVersion,
            currentVersion.InputVersion,
            currentVersion.IsInputVersionStable))
        {
            Invalidate(reason);
            return ResourceHealthIndexSnapshot.Empty;
        }
        ResourceHealthIndexSnapshot snapshot = ResourceHealthIndexSnapshot.Build(
            targetSet.Charts,
            maintenanceService,
            Interlocked.Increment(ref snapshotVersionSeed));
        if (!PublishFullOwnedSnapshot(reason, targetSet, snapshot, currentVersion))
        {
            return ResourceHealthIndexSnapshot.Empty;
        }

        LogBuild(reason, snapshot);
        if (Net10PerformanceLog.IsEnabled)
        {
            Net10PerformanceLog.Write(
                PerformanceInteraction.Existing(
                    "resource_health",
                    snapshot.Version,
                    currentVersion.InputVersion),
                "snapshot_projection_applied",
                "targets=" + snapshot.TargetCount
                + " needFix=" + snapshot.NeedFixCount
                + " ignored=" + snapshot.IgnoredCount
                + " buildMs=" + snapshot.BuildMs);
        }
        return snapshot;
    }

    internal ResourceHealthIndexDispatchResult Apply(
        ResourceHealthIndexMutationFacts mutation,
        string reason,
        ResourceHealthIndexCurrentVersion currentVersion)
    {
        var result = new ResourceHealthIndexDispatchResult
        {
            Snapshot = TryGetCurrentSnapshot()
        };
        if (mutation == null || !mutation.HasChanges)
        {
            return result;
        }
        if (mutation.Invalidate)
        {
            Invalidate(reason);
            result.Snapshot = ResourceHealthIndexSnapshot.Empty;
            return result;
        }
        if (mutation.Defer)
        {
            result.Deferred = true;
            result.Snapshot = TryGetCurrentSnapshot();
            LogDeferred(reason, result.Snapshot, mutation.UpdateTargetCount);
            return result;
        }
        if (!mutation.RebuildFull
            && mutation.HasDeltaTargets
            && TryApplyDelta(
                reason,
                mutation.UpdatedTargets,
                mutation.RemovedTargets,
                mutation.DeltaBaseResourceHealthInputVersion,
                mutation.DeltaTargetResourceHealthInputVersion,
                currentVersion,
                out ResourceHealthIndexSnapshot deltaSnapshot))
        {
            result.Snapshot = deltaSnapshot;
            result.DeltaApplied = true;
            result.IndexMs = deltaSnapshot.BuildMs;
            return result;
        }
        if (!mutation.RebuildFull && mutation.HasDeltaTargets && mutation.InvalidateIfDeltaFails)
        {
            Invalidate(reason);
            result.Snapshot = ResourceHealthIndexSnapshot.Empty;
            return result;
        }
        if (mutation.RebuildFull || mutation.HasDeltaTargets)
        {
            ResourceHealthIndexSnapshot previousSnapshot = GetPublishedSnapshotOrEmpty();
            ResourceHealthIndexSnapshot rebuiltSnapshot = EnsureCurrent(
                reason,
                mutation.FullOwnedTargetSet,
                currentVersion,
                forceRebuild: true);
            result.Snapshot = rebuiltSnapshot;
            result.FullRebuilt = IsCurrent()
                && !ReferenceEquals(previousSnapshot, rebuiltSnapshot)
                && ReferenceEquals(rebuiltSnapshot, GetPublishedSnapshotOrEmpty());
            result.IndexMs = result.FullRebuilt ? rebuiltSnapshot.BuildMs : 0L;
        }
        return result;
    }

    internal ResourceHealthWarningProjection TryGetCurrentProjection(ChartFile chart)
    {
        ResourceHealthIndexSnapshot currentSnapshot = TryGetCurrentSnapshot();
        return currentSnapshot == ResourceHealthIndexSnapshot.Empty
            ? ResourceHealthWarningProjection.Empty
            : currentSnapshot.GetProjection(chart);
    }

    internal ResourceHealthWarningProjection TryGetCurrentProjection(ChartFileKind kind, string path, string md5)
    {
        ResourceHealthIndexSnapshot currentSnapshot = TryGetCurrentSnapshot();
        return currentSnapshot == ResourceHealthIndexSnapshot.Empty
            ? ResourceHealthWarningProjection.Empty
            : currentSnapshot.GetProjection(kind, path, md5);
    }

    internal ResourceHealthIndexSnapshot TryGetCurrentSnapshot()
    {
        ResourceHealthIndexSnapshotState currentState = Volatile.Read(ref state);
        return IsCurrentState(currentState)
            ? currentState.Snapshot
            : ResourceHealthIndexSnapshot.Empty;
    }

    internal void RebaseCurrentVersion(ResourceHealthIndexCurrentVersion currentVersion)
    {
        lock (stateLock)
        {
            ResourceHealthIndexCurrentVersion observedVersion = currentVersionProvider();
            if (state?.Snapshot == null
                || Volatile.Read(ref invalidated)
                || state.InputVersion != currentVersion.InputVersion
                || !currentVersion.IsInputVersionStable
                || observedVersion.InputVersion != currentVersion.InputVersion
                || observedVersion.StorageRowsVersion.BmsRowsVersion != currentVersion.StorageRowsVersion.BmsRowsVersion
                || observedVersion.StorageRowsVersion.BmsonRowsVersion != currentVersion.StorageRowsVersion.BmsonRowsVersion
                || observedVersion.OwnedCollectionVersion != currentVersion.OwnedCollectionVersion)
            {
                return;
            }
            state = new ResourceHealthIndexSnapshotState(
                state.Snapshot,
                state.InputVersion,
                currentVersion.StorageRowsVersion,
                currentVersion.OwnedCollectionVersion);
        }
    }

    internal void RebaseAfterInputMutation(
        ResourceHealthInputMutation mutation,
        ResourceHealthIndexCurrentVersion currentVersion)
    {
        if (mutation == null || !mutation.BaseIndexCurrent || mutation.TargetInputVersion < 0)
        {
            return;
        }

        lock (stateLock)
        {
            ResourceHealthIndexCurrentVersion observedVersion = currentVersionProvider();
            if (state?.Snapshot == null
                || Volatile.Read(ref invalidated)
                || state.InputVersion != mutation.BaseInputVersion
                || currentVersion.InputVersion != mutation.TargetInputVersion
                || !currentVersion.IsInputVersionStable
                || observedVersion.InputVersion != currentVersion.InputVersion
                || observedVersion.StorageRowsVersion.BmsRowsVersion != currentVersion.StorageRowsVersion.BmsRowsVersion
                || observedVersion.StorageRowsVersion.BmsonRowsVersion != currentVersion.StorageRowsVersion.BmsonRowsVersion
                || observedVersion.OwnedCollectionVersion != currentVersion.OwnedCollectionVersion)
            {
                return;
            }
            state = new ResourceHealthIndexSnapshotState(
                state.Snapshot,
                currentVersion.InputVersion,
                currentVersion.StorageRowsVersion,
                currentVersion.OwnedCollectionVersion);
        }
    }

    internal List<ChartFile> FilterTargetsByWarningState(
        IReadOnlyList<ChartFile> targets,
        bool isIgnored)
    {
        if (targets == null || targets.Count == 0)
        {
            return [];
        }

        ResourceHealthIndexSnapshot snapshot = TryGetCurrentSnapshot();
        if (snapshot == ResourceHealthIndexSnapshot.Empty)
        {
            snapshot = ResourceHealthIndexSnapshot.Build(
                targets,
                maintenanceService,
                version: 0);
        }
        return [.. targets.Where(chart =>
        {
            ResourceHealthWarningProjection projection = snapshot.GetProjection(chart);
            return projection.HasIssues && projection.IsIgnored == isIgnored;
        })];
    }

    private bool PublishFullOwnedSnapshot(
        string reason,
        ResourceMaintenanceTargetSet targetSet,
        ResourceHealthIndexSnapshot snapshot,
        ResourceHealthIndexCurrentVersion currentVersion)
    {
        lock (stateLock)
        {
            // Re-read the narrow composition version facts after the potentially
            // expensive build. A target captured before a concurrent catalog
            // mutation must never be published as current.
            ResourceHealthIndexCurrentVersion publishVersion = currentVersionProvider();
            if (publishVersion.InputVersion != inputVersion
                || !ResourceHealthFullOwnedTargetFreshness.IsCurrent(
                    targetSet,
                    publishVersion.StorageRowsVersion,
                    publishVersion.OwnedCollectionVersion,
                    publishVersion.InputVersion,
                    publishVersion.IsInputVersionStable))
            {
                Volatile.Write(ref invalidated, true);
                logPerformance("resource_health_index_full_target_stale reason=" + (reason ?? "unknown")
                    + " targetCount=" + targetSet.Count);
                return false;
            }
            PublishSnapshotUnsafe(snapshot, publishVersion);
            return true;
        }
    }

    private bool TryApplyDelta(
        string reason,
        IEnumerable<ChartFile> updatedTargets,
        IEnumerable<ChartFile> removedTargets,
        int? deltaBaseInputVersion,
        int? deltaTargetInputVersion,
        ResourceHealthIndexCurrentVersion currentVersion,
        out ResourceHealthIndexSnapshot snapshot)
    {
        snapshot = null;
        ResourceHealthIndexSnapshotState currentState = Volatile.Read(ref state);
        ResourceHealthIndexSnapshot currentSnapshot = currentState?.Snapshot;
        int baseInputVersion = deltaBaseInputVersion ?? currentState?.InputVersion ?? -1;
        if (!deltaTargetInputVersion.HasValue)
        {
            return false;
        }
        int targetInputVersion = deltaTargetInputVersion.Value;
        if (currentState == null
            || currentSnapshot == null
            || Volatile.Read(ref invalidated)
            || currentState.InputVersion != baseInputVersion
            || !IsStableInputVersion(baseInputVersion)
            || !IsStableInputVersion(targetInputVersion))
        {
            return false;
        }

        List<ChartFile> updatedTargetList = NormalizeTargets(updatedTargets);
        List<ChartFile> removedTargetList = NormalizeTargets(removedTargets);
        if (updatedTargetList.Count == 0 && removedTargetList.Count == 0)
        {
            return false;
        }
        snapshot = currentSnapshot.ApplyDelta(
            updatedTargetList,
            removedTargetList,
            maintenanceService,
            Interlocked.Increment(ref snapshotVersionSeed));
        lock (stateLock)
        {
            ResourceHealthIndexCurrentVersion publishVersion = currentVersionProvider();
            if (Volatile.Read(ref invalidated)
                || !ReferenceEquals(state, currentState)
                || inputVersion != targetInputVersion
                || !IsStableInputVersion(targetInputVersion)
                || publishVersion.InputVersion != targetInputVersion
                || publishVersion.StorageRowsVersion.BmsRowsVersion != currentVersion.StorageRowsVersion.BmsRowsVersion
                || publishVersion.StorageRowsVersion.BmsonRowsVersion != currentVersion.StorageRowsVersion.BmsonRowsVersion
                || publishVersion.OwnedCollectionVersion != currentVersion.OwnedCollectionVersion)
            {
                snapshot = null;
                return false;
            }
            state = new ResourceHealthIndexSnapshotState(
                snapshot,
                targetInputVersion,
                publishVersion.StorageRowsVersion,
                publishVersion.OwnedCollectionVersion);
            Volatile.Write(ref invalidated, false);
        }
        logPerformance("resource_health_index_delta reason=" + (reason ?? "unknown")
            + " version=" + snapshot.Version
            + " targetCount=" + snapshot.TargetCount
            + " updated=" + updatedTargetList.Count
            + " removed=" + removedTargetList.Count
            + " needFix=" + snapshot.NeedFixCount
            + " ignored=" + snapshot.IgnoredCount
            + " buildMs=" + snapshot.BuildMs);
        return true;
    }

    private bool IsCurrentState(ResourceHealthIndexSnapshotState currentState)
    {
        ResourceHealthIndexCurrentVersion currentVersion = currentVersionProvider();
        return currentState?.Snapshot != null
            && !Volatile.Read(ref invalidated)
            && currentState.InputVersion == currentVersion.InputVersion
            && currentState.StorageRowsVersion.BmsRowsVersion == currentVersion.StorageRowsVersion.BmsRowsVersion
            && currentState.StorageRowsVersion.BmsonRowsVersion == currentVersion.StorageRowsVersion.BmsonRowsVersion
            && currentState.OwnedCollectionVersion == currentVersion.OwnedCollectionVersion
            && currentVersion.IsInputVersionStable;
    }

    private int EndInputMutationVersion()
    {
        lock (stateLock)
        {
            inputMutationDepth = Math.Max(0, inputMutationDepth - 1);
            if (inputMutationDepth == 0)
            {
                IncrementInputVersionUnsafe();
            }
            return inputVersion;
        }
    }

    private void InvalidateCore(string reason, bool ignoreSuppression)
    {
        _ = reason;
        lock (stateLock)
        {
            BumpInputVersionUnsafe();
            if (!ignoreSuppression && suppressionDepth > 0)
            {
                return;
            }
            Volatile.Write(ref invalidated, true);
        }
    }

    private void EndSuppression()
    {
        lock (stateLock)
        {
            suppressionDepth = Math.Max(0, suppressionDepth - 1);
        }
    }

    private void PublishSnapshotUnsafe(
        ResourceHealthIndexSnapshot snapshot,
        ResourceHealthIndexCurrentVersion currentVersion)
    {
        state = new ResourceHealthIndexSnapshotState(
            snapshot,
            currentVersion.InputVersion,
            currentVersion.StorageRowsVersion,
            currentVersion.OwnedCollectionVersion);
        Volatile.Write(ref invalidated, false);
    }

    private void IncrementInputVersionUnsafe()
    {
        unchecked
        {
            inputVersion++;
        }
    }

    private void BumpInputVersionUnsafe()
    {
        unchecked
        {
            inputVersion += 2;
        }
    }

    private static bool IsStableInputVersion(int version)
    {
        return (version & 1) == 0;
    }

    private static List<ChartFile> NormalizeTargets(IEnumerable<ChartFile> targets)
    {
        return [.. (targets ?? []).Where(chart => chart != null)];
    }

    private void LogBuild(string reason, ResourceHealthIndexSnapshot snapshot)
    {
        logPerformance("resource_health_index_build reason=" + (reason ?? "unknown")
            + " version=" + snapshot.Version
            + " targetCount=" + snapshot.TargetCount
            + " needFix=" + snapshot.NeedFixCount
            + " ignored=" + snapshot.IgnoredCount
            + " buildMs=" + snapshot.BuildMs);
    }

    private void LogDeferred(string reason, ResourceHealthIndexSnapshot snapshot, int updateTargetCount)
    {
        logPerformance("resource_health_index_deferred reason=" + (reason ?? "unknown")
            + " targetCount=" + snapshot.TargetCount
            + " updateTargets=" + updateTargetCount
            + " invalidated=" + Volatile.Read(ref invalidated).ToString().ToLowerInvariant());
    }

    private sealed class ResourceHealthIndexSnapshotState(
        ResourceHealthIndexSnapshot snapshot,
        int inputVersion,
        StorageRowsVersionSnapshot storageRowsVersion,
        int ownedCollectionVersion)
    {
        internal ResourceHealthIndexSnapshot Snapshot { get; } = snapshot ?? ResourceHealthIndexSnapshot.Empty;

        internal int InputVersion { get; } = inputVersion;

        internal StorageRowsVersionSnapshot StorageRowsVersion { get; } = storageRowsVersion;

        internal int OwnedCollectionVersion { get; } = ownedCollectionVersion;
    }

    private sealed class InvalidationSuppression(ResourceHealthIndexOwner owner) : IDisposable
    {
        private ResourceHealthIndexOwner owner = owner;

        public void Dispose()
        {
            if (owner == null)
            {
                return;
            }
            owner.EndSuppression();
            owner = null;
        }
    }

    internal sealed class ResourceHealthInputMutation : IDisposable
    {
        private ResourceHealthIndexOwner owner;

        internal ResourceHealthInputMutation(ResourceHealthIndexOwner owner, int baseInputVersion, bool baseIndexCurrent)
        {
            this.owner = owner;
            BaseInputVersion = baseInputVersion;
            BaseIndexCurrent = baseIndexCurrent;
        }

        internal int BaseInputVersion { get; }

        internal bool BaseIndexCurrent { get; }

        internal int TargetInputVersion { get; private set; } = -1;

        public void Dispose()
        {
            ResourceHealthIndexOwner currentOwner = Interlocked.Exchange(ref owner, null);
            if (currentOwner != null)
            {
                TargetInputVersion = currentOwner.EndInputMutationVersion();
            }
        }
    }
}

internal readonly struct ResourceHealthIndexCurrentVersion
{
    internal ResourceHealthIndexCurrentVersion(
        StorageRowsVersionSnapshot storageRowsVersion,
        int ownedCollectionVersion,
        int inputVersion)
    {
        StorageRowsVersion = storageRowsVersion;
        OwnedCollectionVersion = ownedCollectionVersion;
        InputVersion = inputVersion;
    }

    internal StorageRowsVersionSnapshot StorageRowsVersion { get; }

    internal int OwnedCollectionVersion { get; }

    internal int InputVersion { get; }

    internal bool IsInputVersionStable => (InputVersion & 1) == 0;
}
