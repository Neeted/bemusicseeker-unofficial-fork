using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Owns canonical catalog storage-row and owned-collection mutations.
/// Consumer cache and presentation effects remain composed by <see cref="BMSLibrary"/>.
/// </summary>
internal sealed class CatalogMutationOwner
{
    private readonly CatalogStorageRowsOwner storageRowsOwner;

    private readonly CatalogOwnedCollectionOwner ownedCollectionOwner;

    internal CatalogMutationOwner(
        CatalogStorageRowsOwner storageRowsOwner,
        CatalogOwnedCollectionOwner ownedCollectionOwner)
    {
        this.storageRowsOwner = storageRowsOwner ?? throw new ArgumentNullException(nameof(storageRowsOwner));
        this.ownedCollectionOwner = ownedCollectionOwner ?? throw new ArgumentNullException(nameof(ownedCollectionOwner));
    }

    internal CatalogStorageRowsReplacementRequest CreateStorageRowsReplacementRequest(
        IEnumerable<BMSFile> bmsRows,
        IEnumerable<LR2SongDBExtended.bmson_song> bmsonRows,
        bool replaceBmsRows,
        bool replaceBmsonRows)
    {
        using (storageRowsOwner.WriteGate.GetWriterGuard())
        {
            CatalogStorageRowsSnapshot currentRows = storageRowsOwner.CaptureSnapshot();
            return new CatalogStorageRowsReplacementRequest(
                bmsRows,
                bmsonRows,
                replaceBmsRows,
                replaceBmsonRows,
                replaceBmsRows && !ReferenceEquals(currentRows.BmsRows, bmsRows),
                replaceBmsonRows && !ReferenceEquals(currentRows.BmsonRows, bmsonRows));
        }
    }

    internal CatalogStorageRowsReplacementReceipt ApplyStorageRowsReplacement(
        CatalogStorageRowsReplacementRequest request)
    {
        if (request == null)
        {
            return CatalogStorageRowsReplacementReceipt.NotApplied;
        }

        using (storageRowsOwner.WriteGate.GetWriterGuard())
        {
            StorageRowsVersionSnapshot previousVersions = storageRowsOwner.CaptureVersionSnapshot();
            if (request.ReplaceBmsRows && request.BmsRowsChanged)
            {
                storageRowsOwner.ReplaceBmsRows([.. request.BmsRows]);
            }
            if (request.ReplaceBmsonRows && request.BmsonRowsChanged)
            {
                storageRowsOwner.ReplaceBmsonRows([.. request.BmsonRows]);
            }
            bool applied = request.BmsRowsChanged || request.BmsonRowsChanged;
            if (applied)
            {
                ownedCollectionOwner.Invalidate();
            }
            StorageRowsVersionSnapshot currentVersions = storageRowsOwner.CaptureVersionSnapshot();
            return new CatalogStorageRowsReplacementReceipt(
                applied,
                request.BmsRowsChanged,
                request.BmsonRowsChanged,
                ownedCollectionInvalidated: applied,
                new StorageRowsVersionSnapshot(
                    previousVersions.BmsRowsVersion,
                    previousVersions.BmsonRowsVersion,
                    currentVersions.BmsRowsVersion,
                    currentVersions.BmsonRowsVersion));
        }
    }

    internal CatalogStorageRowsRemovalRequest CreateStorageRowsRemovalRequest(
        IEnumerable<OwnedChartRemoveRequest> removeRequests)
    {
        return new CatalogStorageRowsRemovalRequest(removeRequests);
    }

    internal CatalogStorageRowsRemovalReceipt ApplyStorageRowsRemoval(
        CatalogStorageRowsRemovalRequest request)
    {
        if (request == null)
        {
            return CatalogStorageRowsRemovalReceipt.NotApplied;
        }

        using (storageRowsOwner.WriteGate.GetWriterGuard())
        {
            StorageRowsVersionSnapshot previousVersions = storageRowsOwner.CaptureVersionSnapshot();
            if (!request.HasChanges)
            {
                return new CatalogStorageRowsRemovalReceipt(
                    applied: false,
                    bmsRowsChanged: false,
                    bmsonRowsChanged: false,
                    previousVersions);
            }

            StorageRowsVersionSnapshot storageRowsVersion = storageRowsOwner.RemoveRows(
                new HashSet<BMSFile>(request.RemovedBmsRows),
                new HashSet<string>(request.BmsPathCleanupKeys, StringComparer.OrdinalIgnoreCase),
                new HashSet<LR2SongDBExtended.bmson_song>(request.RemovedBmsonRows),
                new HashSet<string>(request.BmsonPathCleanupKeys, StringComparer.OrdinalIgnoreCase));
            return new CatalogStorageRowsRemovalReceipt(
                applied: true,
                storageRowsVersion.BmsRowsVersion != previousVersions.BmsRowsVersion,
                storageRowsVersion.BmsonRowsVersion != previousVersions.BmsonRowsVersion,
                storageRowsVersion);
        }
    }

    internal CatalogOwnedCollectionMutationRequest CreateOwnedCollectionMutationRequest(
        IEnumerable<OwnedChartRemoveRequest> removeRequests,
        IEnumerable<LibraryChartPathChange> pathChanges,
        IEnumerable<BMSFile> addedBmsFiles,
        IEnumerable<LR2SongDBExtended.bmson_song> addedBmsonSongs,
        StorageRowsVersionSnapshot storageRowsVersion)
    {
        return new CatalogOwnedCollectionMutationRequest(
            removeRequests,
            pathChanges,
            addedBmsFiles,
            addedBmsonSongs,
            storageRowsVersion);
    }

    internal CatalogOwnedCollectionMutationReceipt ApplyOwnedCollectionMutation(
        CatalogOwnedCollectionMutationRequest request)
    {
        if (request == null)
        {
            return CatalogOwnedCollectionMutationReceipt.NotApplied;
        }

        using (storageRowsOwner.WriteGate.GetWriterGuard())
        {
            bool ownerApplied = ownedCollectionOwner.ApplyMutation(
                request.RemoveRequests,
                request.PathChanges,
                request.AddedBmsFiles,
                request.AddedBmsonSongs,
                request.StorageRowsVersion);
            StorageRowsVersionSnapshot currentStorageRowsVersion = storageRowsOwner.CaptureVersionSnapshot();
            bool applied = request.HasChanges && ownerApplied;
            return new CatalogOwnedCollectionMutationReceipt(
                applied,
                ownedCollectionApplied: applied,
                ownedCollectionVersion: ownedCollectionOwner.CollectionVersion,
                applied
                    ? new StorageRowsVersionSnapshot(
                        request.StorageRowsVersion.PreviousBmsRowsVersion,
                        request.StorageRowsVersion.PreviousBmsonRowsVersion,
                        currentStorageRowsVersion.BmsRowsVersion,
                        currentStorageRowsVersion.BmsonRowsVersion)
                    : currentStorageRowsVersion);
        }
    }

    internal CatalogFileScanStorageReplacementRequest CreateFileScanStorageReplacementRequest(
        bool hasDbDiff,
        IEnumerable<BMSFile> nextBmsRows,
        IEnumerable<LR2SongDBExtended.bmson_song> nextBmsonRows,
        IEnumerable<string> deletedBmsPaths,
        IEnumerable<string> deletedBmsonPaths,
        IEnumerable<BMSFile> addedBmsFiles,
        IEnumerable<LR2SongDBExtended.bmson_song> addedBmsonSongs)
    {
        CatalogStorageRowsSnapshot currentRows = storageRowsOwner.CaptureSnapshot();
        bool removedPayloadAvailable = ownedCollectionOwner.TryCreateFileScanRemovedStorageOwnerIdentityCharts(
            [.. deletedBmsPaths ?? []],
            [.. deletedBmsonPaths ?? []],
            [.. nextBmsRows ?? []],
            [.. nextBmsonRows ?? []],
            currentRows.BmsRowsVersion,
            currentRows.BmsonRowsVersion,
            out List<ChartFile> removedCharts);
        return new CatalogFileScanStorageReplacementRequest(
            hasDbDiff,
            nextBmsRows,
            nextBmsonRows,
            deletedBmsPaths,
            deletedBmsonPaths,
            addedBmsFiles,
            addedBmsonSongs,
            currentRows.BmsRowsVersion,
            currentRows.BmsonRowsVersion,
            removedPayloadAvailable,
            removedCharts,
            currentRows);
    }

    internal CatalogFileScanStorageReplacementReceipt ApplyFileScanStorageReplacement(
        CatalogFileScanStorageReplacementRequest request,
        Action<CatalogStorageRowsSnapshot> applyStorageProjection = null)
    {
        if (request == null)
        {
            return CatalogFileScanStorageReplacementReceipt.NotApplied;
        }

        CatalogStorageRowsSnapshot storageRows = request.HasDbDiff
            ? storageRowsOwner.ReplaceRowsAndCaptureSnapshot(
                [.. request.NextBmsRows],
                [.. request.NextBmsonRows])
            : request.CurrentRows;
        applyStorageProjection?.Invoke(storageRows);
        CatalogOwnedCollectionReplacementResult ownedReplacement = request.HasDbDiff
            ? ownedCollectionOwner.ReplaceForFileScan(storageRows)
            : CatalogOwnedCollectionReplacementResult.NotApplied;
        StorageRowsVersionSnapshot versions = new(
            request.PreviousBmsRowsVersion,
            request.PreviousBmsonRowsVersion,
            storageRows.BmsRowsVersion,
            storageRows.BmsonRowsVersion);
        return new CatalogFileScanStorageReplacementReceipt(
            applied: request.HasDbDiff,
            ownedCollectionApplied: ownedReplacement.Applied,
            versions,
            ownedCollectionOwner.CollectionVersion,
            ownedReplacement.FilterSummary,
            request.AddedCharts,
            request.RemovedCharts,
            movedCharts: []);
    }

    internal CatalogInstalledTargetUpsertRequest CreateInstalledTargetUpsertRequest(
        IEnumerable<BMSFile> bmsRows,
        IEnumerable<LR2SongDBExtended.bmson_song> bmsonRows)
    {
        using (storageRowsOwner.WriteGate.GetWriterGuard())
        {
            return CreateInstalledTargetUpsertRequestUnsafe(bmsRows, bmsonRows);
        }
    }

    internal CatalogInstalledTargetUpsertReceipt ApplyInstalledTargetUpsert(
        IEnumerable<BMSFile> bmsRows,
        IEnumerable<LR2SongDBExtended.bmson_song> bmsonRows)
    {
        using (storageRowsOwner.WriteGate.GetWriterGuard())
        {
            return ApplyInstalledTargetUpsertUnsafe(
                CreateInstalledTargetUpsertRequestUnsafe(bmsRows, bmsonRows));
        }
    }

    internal CatalogInstalledTargetUpsertReceipt ApplyInstalledTargetUpsert(
        CatalogInstalledTargetUpsertRequest request)
    {
        if (request == null)
        {
            return CatalogInstalledTargetUpsertReceipt.NotApplied;
        }

        using (storageRowsOwner.WriteGate.GetWriterGuard())
        {
            return ApplyInstalledTargetUpsertUnsafe(request);
        }
    }

    private CatalogInstalledTargetUpsertRequest CreateInstalledTargetUpsertRequestUnsafe(
        IEnumerable<BMSFile> bmsRows,
        IEnumerable<LR2SongDBExtended.bmson_song> bmsonRows)
    {
        StorageRowsVersionSnapshot currentVersions = storageRowsOwner.CaptureVersionSnapshot();
        return new CatalogInstalledTargetUpsertRequest(
            bmsRows,
            bmsonRows,
            currentVersions.BmsRowsVersion,
            currentVersions.BmsonRowsVersion);
    }

    private CatalogInstalledTargetUpsertReceipt ApplyInstalledTargetUpsertUnsafe(
        CatalogInstalledTargetUpsertRequest request)
    {
        StorageRowsVersionSnapshot currentVersions = storageRowsOwner.CaptureVersionSnapshot();
        if (currentVersions.BmsRowsVersion != request.PreviousBmsRowsVersion
            || currentVersions.BmsonRowsVersion != request.PreviousBmsonRowsVersion)
        {
            throw new InvalidOperationException("Catalog storage rows changed before installed target upsert.");
        }

        ChartStorageTargetSet targets = ChartStorageTargetSet.FromRows(
            request.BmsRows,
            request.BmsonRows);
        StorageRowsVersionSnapshot versions = storageRowsOwner.ApplyInstalledTargets(targets);
        bool ownedCollectionApplied = ownedCollectionOwner.ApplyMutation(
            [],
            [],
            request.BmsRows,
            request.BmsonRows,
            versions);
        return new CatalogInstalledTargetUpsertReceipt(
            applied: request.BmsRows.Count > 0 || request.BmsonRows.Count > 0,
            ownedCollectionApplied,
            versions,
            ownedCollectionOwner.CollectionVersion,
            request.AddedCharts);
    }

    internal CatalogDigestMutationRequest CreateDigestMutationRequest(
        IEnumerable<LibraryChartDigestChange> digestChanges)
    {
        return new CatalogDigestMutationRequest(digestChanges);
    }

    internal CatalogDigestMutationReceipt ApplyDigestMutation(
        CatalogDigestMutationRequest request)
    {
        if (request == null)
        {
            return CatalogDigestMutationReceipt.NotApplied;
        }

        using (storageRowsOwner.WriteGate.GetWriterGuard())
        {
            bool ownedCollectionApplied = ownedCollectionOwner.ApplyDigestChanges(request.DigestChanges);
            return new CatalogDigestMutationReceipt(
                applied: request.DigestChanges.Count > 0,
                ownedCollectionApplied,
                storageRowsOwner.CaptureVersionSnapshot(),
                ownedCollectionOwner.CollectionVersion,
                request.DigestChanges);
        }
    }
}

internal enum CatalogMutationApplyKind
{
    NoOp,
    StorageRowsReplacement,
    StorageRowsRemoval,
    OwnedCollectionMutation,
    FileScanStorageReplacement,
    InstalledTargetUpsert,
    DigestMutation
}

/// <summary>
/// Immutable input snapshot for catalog storage-row removal.
/// </summary>
internal sealed class CatalogStorageRowsRemovalRequest
{
    internal CatalogStorageRowsRemovalRequest(IEnumerable<OwnedChartRemoveRequest> removeRequests)
    {
        List<OwnedChartRemoveRequest> requests = [.. (removeRequests ?? []).Where(request => request != null)];
        RemovedBmsRows = Snapshot(requests
            .Select(request => request.BmsOwner)
            .Where(file => file != null)
            .Distinct());
        BmsPathCleanupKeys = CreatePathCleanupKeys(requests, ChartFileKind.Bms);
        RemovedBmsonRows = Snapshot(requests
            .Select(request => request.BmsonOwner)
            .Where(song => song != null)
            .Distinct());
        BmsonPathCleanupKeys = CreatePathCleanupKeys(requests, ChartFileKind.Bmson);
    }

    internal IReadOnlyList<BMSFile> RemovedBmsRows { get; }

    internal IReadOnlyList<string> BmsPathCleanupKeys { get; }

    internal IReadOnlyList<LR2SongDBExtended.bmson_song> RemovedBmsonRows { get; }

    internal IReadOnlyList<string> BmsonPathCleanupKeys { get; }

    internal bool HasChanges => RemovedBmsRows.Count > 0
        || BmsPathCleanupKeys.Count > 0
        || RemovedBmsonRows.Count > 0
        || BmsonPathCleanupKeys.Count > 0;

    private static IReadOnlyList<string> CreatePathCleanupKeys(
        IEnumerable<OwnedChartRemoveRequest> requests,
        ChartFileKind kind)
    {
        return Snapshot(requests
            .Where(request => request.Mode == OwnedChartRemoveMode.PathCleanup && request.Kind == kind)
            .Select(request => OwnedChartCollectionState.CreateOwnedPathKey(request.Path))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<T> Snapshot<T>(IEnumerable<T> values)
    {
        return Array.AsReadOnly([.. values ?? []]);
    }
}

/// <summary>
/// Canonical facts emitted after catalog storage-row removal.
/// </summary>
internal sealed class CatalogStorageRowsRemovalReceipt
{
    internal static CatalogStorageRowsRemovalReceipt NotApplied { get; } =
        new(
            applied: false,
            bmsRowsChanged: false,
            bmsonRowsChanged: false,
            new StorageRowsVersionSnapshot(0, 0));

    internal CatalogStorageRowsRemovalReceipt(
        bool applied,
        bool bmsRowsChanged,
        bool bmsonRowsChanged,
        StorageRowsVersionSnapshot storageRowsVersion)
    {
        Applied = applied;
        Kind = applied
            ? CatalogMutationApplyKind.StorageRowsRemoval
            : CatalogMutationApplyKind.NoOp;
        BmsRowsChanged = bmsRowsChanged;
        BmsonRowsChanged = bmsonRowsChanged;
        StorageRowsVersion = storageRowsVersion;
    }

    internal bool Applied { get; }

    internal CatalogMutationApplyKind Kind { get; }

    internal bool BmsRowsChanged { get; }

    internal bool BmsonRowsChanged { get; }

    internal StorageRowsVersionSnapshot StorageRowsVersion { get; }
}

/// <summary>
/// Immutable input snapshot for an owned collection mutation after catalog storage rows are applied.
/// </summary>
internal sealed class CatalogOwnedCollectionMutationRequest
{
    internal CatalogOwnedCollectionMutationRequest(
        IEnumerable<OwnedChartRemoveRequest> removeRequests,
        IEnumerable<LibraryChartPathChange> pathChanges,
        IEnumerable<BMSFile> addedBmsFiles,
        IEnumerable<LR2SongDBExtended.bmson_song> addedBmsonSongs,
        StorageRowsVersionSnapshot storageRowsVersion)
    {
        RemoveRequests = Snapshot(removeRequests);
        PathChanges = Snapshot(pathChanges);
        AddedBmsFiles = Snapshot(addedBmsFiles);
        AddedBmsonSongs = Snapshot(addedBmsonSongs);
        StorageRowsVersion = storageRowsVersion;
    }

    internal IReadOnlyList<OwnedChartRemoveRequest> RemoveRequests { get; }

    internal IReadOnlyList<LibraryChartPathChange> PathChanges { get; }

    internal IReadOnlyList<BMSFile> AddedBmsFiles { get; }

    internal IReadOnlyList<LR2SongDBExtended.bmson_song> AddedBmsonSongs { get; }

    internal StorageRowsVersionSnapshot StorageRowsVersion { get; }

    internal bool HasChanges => RemoveRequests.Count > 0
        || PathChanges.Count > 0
        || AddedBmsFiles.Count > 0
        || AddedBmsonSongs.Count > 0;

    private static IReadOnlyList<T> Snapshot<T>(IEnumerable<T> values)
    {
        return Array.AsReadOnly([.. values ?? []]);
    }
}

/// <summary>
/// Canonical facts emitted after applying an owned collection mutation.
/// </summary>
internal sealed class CatalogOwnedCollectionMutationReceipt
{
    internal static CatalogOwnedCollectionMutationReceipt NotApplied { get; } =
        new(
            applied: false,
            ownedCollectionApplied: false,
            ownedCollectionVersion: 0,
            new StorageRowsVersionSnapshot(0, 0));

    internal CatalogOwnedCollectionMutationReceipt(
        bool applied,
        bool ownedCollectionApplied,
        int ownedCollectionVersion,
        StorageRowsVersionSnapshot storageRowsVersion)
    {
        Applied = applied;
        Kind = applied
            ? CatalogMutationApplyKind.OwnedCollectionMutation
            : CatalogMutationApplyKind.NoOp;
        OwnedCollectionApplied = ownedCollectionApplied;
        OwnedCollectionVersion = ownedCollectionVersion;
        StorageRowsVersion = storageRowsVersion;
    }

    internal bool Applied { get; }

    internal CatalogMutationApplyKind Kind { get; }

    internal bool OwnedCollectionApplied { get; }

    internal int OwnedCollectionVersion { get; }

    internal StorageRowsVersionSnapshot StorageRowsVersion { get; }
}

/// <summary>
/// Immutable input snapshot for a selected catalog storage-row replacement.
/// </summary>
internal sealed class CatalogStorageRowsReplacementRequest
{
    internal CatalogStorageRowsReplacementRequest(
        IEnumerable<BMSFile> bmsRows,
        IEnumerable<LR2SongDBExtended.bmson_song> bmsonRows,
        bool replaceBmsRows,
        bool replaceBmsonRows,
        bool bmsRowsChanged,
        bool bmsonRowsChanged)
    {
        BmsRows = Snapshot(bmsRows);
        BmsonRows = Snapshot(bmsonRows);
        ReplaceBmsRows = replaceBmsRows;
        ReplaceBmsonRows = replaceBmsonRows;
        BmsRowsChanged = bmsRowsChanged;
        BmsonRowsChanged = bmsonRowsChanged;
    }

    internal IReadOnlyList<BMSFile> BmsRows { get; }

    internal IReadOnlyList<LR2SongDBExtended.bmson_song> BmsonRows { get; }

    internal bool ReplaceBmsRows { get; }

    internal bool ReplaceBmsonRows { get; }

    internal bool BmsRowsChanged { get; }

    internal bool BmsonRowsChanged { get; }

    private static IReadOnlyList<T> Snapshot<T>(IEnumerable<T> values)
    {
        return Array.AsReadOnly([.. values ?? []]);
    }
}

/// <summary>
/// Canonical facts emitted after a catalog storage-row replacement.
/// </summary>
internal sealed class CatalogStorageRowsReplacementReceipt
{
    internal static CatalogStorageRowsReplacementReceipt NotApplied { get; } =
        new(
            applied: false,
            bmsRowsChanged: false,
            bmsonRowsChanged: false,
            ownedCollectionInvalidated: false,
            default);

    internal CatalogStorageRowsReplacementReceipt(
        bool applied,
        bool bmsRowsChanged,
        bool bmsonRowsChanged,
        bool ownedCollectionInvalidated,
        StorageRowsVersionSnapshot storageRowsVersion)
    {
        Applied = applied;
        Kind = applied
            ? CatalogMutationApplyKind.StorageRowsReplacement
            : CatalogMutationApplyKind.NoOp;
        BmsRowsChanged = bmsRowsChanged;
        BmsonRowsChanged = bmsonRowsChanged;
        OwnedCollectionInvalidated = ownedCollectionInvalidated;
        StorageRowsVersion = storageRowsVersion;
    }

    internal bool Applied { get; }

    internal CatalogMutationApplyKind Kind { get; }

    internal bool BmsRowsChanged { get; }

    internal bool BmsonRowsChanged { get; }

    internal bool OwnedCollectionInvalidated { get; }

    internal StorageRowsVersionSnapshot StorageRowsVersion { get; }
}

/// <summary>
/// Immutable input snapshot for an installed-target catalog upsert.
/// </summary>
internal sealed class CatalogInstalledTargetUpsertRequest
{
    internal CatalogInstalledTargetUpsertRequest(
        IEnumerable<BMSFile> bmsRows,
        IEnumerable<LR2SongDBExtended.bmson_song> bmsonRows,
        int previousBmsRowsVersion,
        int previousBmsonRowsVersion)
    {
        BmsRows = Snapshot(bmsRows);
        BmsonRows = Snapshot(bmsonRows);
        PreviousBmsRowsVersion = previousBmsRowsVersion;
        PreviousBmsonRowsVersion = previousBmsonRowsVersion;
        AddedCharts = CatalogChartMutationFact.CreateFacts(
            ChartFileProjection.FromStorageRows(
                BmsRows,
                BmsonRows,
                includeWarningSnapshot: false,
                requirePath: false,
                includeResourceReferences: false,
                includeScoreSnapshot: false));
    }

    internal IReadOnlyList<BMSFile> BmsRows { get; }

    internal IReadOnlyList<LR2SongDBExtended.bmson_song> BmsonRows { get; }

    internal int PreviousBmsRowsVersion { get; }

    internal int PreviousBmsonRowsVersion { get; }

    internal IReadOnlyList<CatalogChartMutationFact> AddedCharts { get; }

    private static IReadOnlyList<T> Snapshot<T>(IEnumerable<T> values)
    {
        return Array.AsReadOnly([.. values ?? []]);
    }
}

/// <summary>
/// Canonical facts emitted after an installed-target catalog upsert.
/// </summary>
internal sealed class CatalogInstalledTargetUpsertReceipt
{
    internal static CatalogInstalledTargetUpsertReceipt NotApplied { get; } =
        new(
            applied: false,
            ownedCollectionApplied: false,
            default,
            ownedCollectionVersion: 0,
            []);

    internal CatalogInstalledTargetUpsertReceipt(
        bool applied,
        bool ownedCollectionApplied,
        StorageRowsVersionSnapshot storageRowsVersion,
        int ownedCollectionVersion,
        IEnumerable<CatalogChartMutationFact> addedCharts)
    {
        Applied = applied;
        Kind = applied
            ? CatalogMutationApplyKind.InstalledTargetUpsert
            : CatalogMutationApplyKind.NoOp;
        OwnedCollectionApplied = ownedCollectionApplied;
        StorageRowsVersion = storageRowsVersion;
        OwnedCollectionVersion = ownedCollectionVersion;
        AddedCharts = Array.AsReadOnly([.. addedCharts ?? []]);
    }

    internal bool Applied { get; }

    internal CatalogMutationApplyKind Kind { get; }

    internal bool OwnedCollectionApplied { get; }

    internal StorageRowsVersionSnapshot StorageRowsVersion { get; }

    internal int OwnedCollectionVersion { get; }

    internal IReadOnlyList<CatalogChartMutationFact> AddedCharts { get; }
}

/// <summary>
/// Immutable input snapshot for a catalog digest mutation.
/// </summary>
internal sealed class CatalogDigestMutationRequest
{
    internal CatalogDigestMutationRequest(IEnumerable<LibraryChartDigestChange> digestChanges)
    {
        DigestChanges = Array.AsReadOnly((digestChanges ?? [])
            .Where(change => change?.HasDigestChange == true)
            .ToArray());
    }

    internal IReadOnlyList<LibraryChartDigestChange> DigestChanges { get; }
}

/// <summary>
/// Canonical facts emitted after a catalog digest mutation.
/// </summary>
internal sealed class CatalogDigestMutationReceipt
{
    internal static CatalogDigestMutationReceipt NotApplied { get; } =
        new(
            applied: false,
            ownedCollectionApplied: false,
            default,
            ownedCollectionVersion: 0,
            []);

    internal CatalogDigestMutationReceipt(
        bool applied,
        bool ownedCollectionApplied,
        StorageRowsVersionSnapshot storageRowsVersion,
        int ownedCollectionVersion,
        IEnumerable<LibraryChartDigestChange> digestChanges)
    {
        Applied = applied;
        Kind = applied
            ? CatalogMutationApplyKind.DigestMutation
            : CatalogMutationApplyKind.NoOp;
        OwnedCollectionApplied = ownedCollectionApplied;
        StorageRowsVersion = storageRowsVersion;
        OwnedCollectionVersion = ownedCollectionVersion;
        DigestChanges = Array.AsReadOnly([.. digestChanges ?? []]);
    }

    internal bool Applied { get; }

    internal CatalogMutationApplyKind Kind { get; }

    internal bool OwnedCollectionApplied { get; }

    internal StorageRowsVersionSnapshot StorageRowsVersion { get; }

    internal int OwnedCollectionVersion { get; }

    internal IReadOnlyList<LibraryChartDigestChange> DigestChanges { get; }
}

/// <summary>
/// Immutable input snapshot for a catalog file-scan replacement.
/// </summary>
internal sealed class CatalogFileScanStorageReplacementRequest
{
    internal CatalogFileScanStorageReplacementRequest(
        bool hasDbDiff,
        IEnumerable<BMSFile> nextBmsRows,
        IEnumerable<LR2SongDBExtended.bmson_song> nextBmsonRows,
        IEnumerable<string> deletedBmsPaths,
        IEnumerable<string> deletedBmsonPaths,
        IEnumerable<BMSFile> addedBmsFiles,
        IEnumerable<LR2SongDBExtended.bmson_song> addedBmsonSongs,
        int previousBmsRowsVersion,
        int previousBmsonRowsVersion,
        bool removedPayloadAvailable,
        IEnumerable<ChartFile> removedCharts,
        CatalogStorageRowsSnapshot currentRows)
    {
        HasDbDiff = hasDbDiff;
        NextBmsRows = Snapshot(nextBmsRows);
        NextBmsonRows = Snapshot(nextBmsonRows);
        DeletedBmsPaths = Snapshot(deletedBmsPaths);
        DeletedBmsonPaths = Snapshot(deletedBmsonPaths);
        AddedBmsFiles = Snapshot(addedBmsFiles);
        AddedBmsonSongs = Snapshot(addedBmsonSongs);
        AddedCharts = CatalogChartMutationFact.CreateFacts(
            ChartFileProjection.FromStorageRows(
                AddedBmsFiles,
                AddedBmsonSongs,
                includeWarningSnapshot: false,
                requirePath: false,
                includeResourceReferences: false,
                includeScoreSnapshot: false));
        PreviousBmsRowsVersion = previousBmsRowsVersion;
        PreviousBmsonRowsVersion = previousBmsonRowsVersion;
        RemovedPayloadAvailable = removedPayloadAvailable;
        RemovedCharts = Snapshot(removedCharts);
        CurrentRows = currentRows ?? throw new ArgumentNullException(nameof(currentRows));
    }

    internal bool HasDbDiff { get; }

    internal IReadOnlyList<BMSFile> NextBmsRows { get; }

    internal IReadOnlyList<LR2SongDBExtended.bmson_song> NextBmsonRows { get; }

    internal IReadOnlyList<string> DeletedBmsPaths { get; }

    internal IReadOnlyList<string> DeletedBmsonPaths { get; }

    internal IReadOnlyList<BMSFile> AddedBmsFiles { get; }

    internal IReadOnlyList<LR2SongDBExtended.bmson_song> AddedBmsonSongs { get; }

    internal IReadOnlyList<CatalogChartMutationFact> AddedCharts { get; }

    internal int PreviousBmsRowsVersion { get; }

    internal int PreviousBmsonRowsVersion { get; }

    internal bool RemovedPayloadAvailable { get; }

    internal IReadOnlyList<ChartFile> RemovedCharts { get; }

    internal CatalogStorageRowsSnapshot CurrentRows { get; }

    private static IReadOnlyList<T> Snapshot<T>(IEnumerable<T> values)
    {
        return Array.AsReadOnly([.. values ?? []]);
    }
}

/// <summary>
/// Canonical facts emitted after a successful catalog file-scan replacement.
/// </summary>
internal sealed class CatalogFileScanStorageReplacementReceipt
{
    internal static CatalogFileScanStorageReplacementReceipt NotApplied { get; } =
        new(
            applied: false,
            ownedCollectionApplied: false,
            default,
            ownedCollectionVersion: 0,
            new OwnedChartStorageRowFilterSummary(0, 0, 0, 0, 0, 0),
            [],
            [],
            []);

    internal CatalogFileScanStorageReplacementReceipt(
        bool applied,
        bool ownedCollectionApplied,
        StorageRowsVersionSnapshot versions,
        int ownedCollectionVersion,
        OwnedChartStorageRowFilterSummary filterSummary,
        IEnumerable<CatalogChartMutationFact> addedCharts,
        IEnumerable<ChartFile> removedCharts,
        IEnumerable<ChartFile> movedCharts)
    {
        Applied = applied;
        Kind = applied
            ? CatalogMutationApplyKind.FileScanStorageReplacement
            : CatalogMutationApplyKind.NoOp;
        OwnedCollectionApplied = ownedCollectionApplied;
        StorageRowsVersion = versions;
        OwnedCollectionVersion = ownedCollectionVersion;
        FilterSummary = filterSummary;
        AddedCharts = SnapshotFacts(addedCharts);
        RemovedCharts = CreateFacts(removedCharts);
        MovedCharts = CreateFacts(movedCharts);
    }

    internal bool Applied { get; }

    internal CatalogMutationApplyKind Kind { get; }

    internal bool OwnedCollectionApplied { get; }

    internal StorageRowsVersionSnapshot StorageRowsVersion { get; }

    internal int OwnedCollectionVersion { get; }

    internal OwnedChartStorageRowFilterSummary FilterSummary { get; }

    internal IReadOnlyList<CatalogChartMutationFact> AddedCharts { get; }

    internal IReadOnlyList<CatalogChartMutationFact> RemovedCharts { get; }

    internal IReadOnlyList<CatalogChartMutationFact> MovedCharts { get; }

    private static IReadOnlyList<CatalogChartMutationFact> CreateFacts(IEnumerable<ChartFile> charts)
    {
        return Array.AsReadOnly((charts ?? [])
            .Where(chart => chart != null)
            .Select(CatalogChartMutationFact.FromChart)
            .ToArray());
    }

    private static IReadOnlyList<CatalogChartMutationFact> SnapshotFacts(
        IEnumerable<CatalogChartMutationFact> facts)
    {
        return Array.AsReadOnly([.. facts ?? []]);
    }
}

internal sealed class CatalogChartMutationFact(
    ChartFileKind kind,
    string path,
    string md5,
    string sha256)
{
    internal ChartFileKind Kind { get; } = kind;

    internal string Path { get; } = path;

    internal string Md5 { get; } = md5;

    internal string Sha256 { get; } = sha256;

    internal static CatalogChartMutationFact FromChart(ChartFile chart)
    {
        return new CatalogChartMutationFact(chart.Kind, chart.Path, chart.Md5, chart.Sha256);
    }

    internal static IReadOnlyList<CatalogChartMutationFact> CreateFacts(IEnumerable<ChartFile> charts)
    {
        return Array.AsReadOnly((charts ?? [])
            .Where(chart => chart != null)
            .Select(FromChart)
            .ToArray());
    }
}
