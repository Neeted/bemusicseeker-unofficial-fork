using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Owns the catalog storage-row replacement phase of a file scan.
/// Consumer cache and presentation effects remain composed by <see cref="BMSLibrary"/>.
/// </summary>
internal sealed class CatalogFileScanStorageReplacementOwner
{
    private readonly CatalogStorageRowsOwner storageRowsOwner;

    private readonly CatalogOwnedCollectionOwner ownedCollectionOwner;

    internal CatalogFileScanStorageReplacementOwner(
        CatalogStorageRowsOwner storageRowsOwner,
        CatalogOwnedCollectionOwner ownedCollectionOwner)
    {
        this.storageRowsOwner = storageRowsOwner ?? throw new ArgumentNullException(nameof(storageRowsOwner));
        this.ownedCollectionOwner = ownedCollectionOwner ?? throw new ArgumentNullException(nameof(ownedCollectionOwner));
    }

    internal CatalogFileScanStorageReplacementRequest CreateRequest(
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

    internal CatalogFileScanStorageReplacementReceipt Apply(
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
            request.AddedTargets?.Charts,
            request.RemovedCharts,
            movedCharts: []);
    }
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
        CatalogStorageRowsSnapshot currentRows,
        ChartStorageTargetSet addedTargets = null)
    {
        HasDbDiff = hasDbDiff;
        NextBmsRows = Snapshot(nextBmsRows);
        NextBmsonRows = Snapshot(nextBmsonRows);
        DeletedBmsPaths = Snapshot(deletedBmsPaths);
        DeletedBmsonPaths = Snapshot(deletedBmsonPaths);
        AddedBmsFiles = Snapshot(addedBmsFiles);
        AddedBmsonSongs = Snapshot(addedBmsonSongs);
        PreviousBmsRowsVersion = previousBmsRowsVersion;
        PreviousBmsonRowsVersion = previousBmsonRowsVersion;
        RemovedPayloadAvailable = removedPayloadAvailable;
        RemovedCharts = Snapshot(removedCharts);
        CurrentRows = currentRows ?? throw new ArgumentNullException(nameof(currentRows));
        AddedTargets = addedTargets;
    }

    internal bool HasDbDiff { get; }

    internal IReadOnlyList<BMSFile> NextBmsRows { get; }

    internal IReadOnlyList<LR2SongDBExtended.bmson_song> NextBmsonRows { get; }

    internal IReadOnlyList<string> DeletedBmsPaths { get; }

    internal IReadOnlyList<string> DeletedBmsonPaths { get; }

    internal IReadOnlyList<BMSFile> AddedBmsFiles { get; }

    internal IReadOnlyList<LR2SongDBExtended.bmson_song> AddedBmsonSongs { get; }

    internal int PreviousBmsRowsVersion { get; }

    internal int PreviousBmsonRowsVersion { get; }

    internal bool RemovedPayloadAvailable { get; }

    internal IReadOnlyList<ChartFile> RemovedCharts { get; }

    internal CatalogStorageRowsSnapshot CurrentRows { get; }

    internal ChartStorageTargetSet AddedTargets { get; }

    internal CatalogFileScanStorageReplacementRequest WithAddedTargets(ChartStorageTargetSet addedTargets)
    {
        return new CatalogFileScanStorageReplacementRequest(
            HasDbDiff,
            NextBmsRows,
            NextBmsonRows,
            DeletedBmsPaths,
            DeletedBmsonPaths,
            AddedBmsFiles,
            AddedBmsonSongs,
            PreviousBmsRowsVersion,
            PreviousBmsonRowsVersion,
            RemovedPayloadAvailable,
            RemovedCharts,
            CurrentRows,
            addedTargets);
    }

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
        IEnumerable<ChartFile> addedCharts,
        IEnumerable<ChartFile> removedCharts,
        IEnumerable<ChartFile> movedCharts)
    {
        Applied = applied;
        OwnedCollectionApplied = ownedCollectionApplied;
        StorageRowsVersion = versions;
        OwnedCollectionVersion = ownedCollectionVersion;
        FilterSummary = filterSummary;
        AddedCharts = CreateFacts(addedCharts);
        RemovedCharts = CreateFacts(removedCharts);
        MovedCharts = CreateFacts(movedCharts);
    }

    internal bool Applied { get; }

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
}
