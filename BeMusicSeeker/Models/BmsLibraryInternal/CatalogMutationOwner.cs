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
    FileScanStorageReplacement,
    InstalledTargetUpsert,
    DigestMutation
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
