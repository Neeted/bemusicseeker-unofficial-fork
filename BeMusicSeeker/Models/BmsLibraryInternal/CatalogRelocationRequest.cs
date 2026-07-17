using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Immutable catalog relocation command prepared from a library mutation delta.
/// </summary>
internal sealed class CatalogRelocationRequest
{
    internal CatalogRelocationRequest(
        IEnumerable<CatalogFolderPathReplacement> folderPathChanges,
        IEnumerable<BmsSongPathReplacement> bmsPathReplacements,
        IEnumerable<BmsonSongPathReplacement> bmsonPathReplacements)
    {
        FolderPathChanges = Snapshot(folderPathChanges);
        BmsPathReplacements = Snapshot(bmsPathReplacements);
        BmsonPathReplacements = Snapshot(bmsonPathReplacements);
    }

    internal IReadOnlyList<CatalogFolderPathReplacement> FolderPathChanges { get; }

    internal IReadOnlyList<BmsSongPathReplacement> BmsPathReplacements { get; }

    internal IReadOnlyList<BmsonSongPathReplacement> BmsonPathReplacements { get; }

    internal bool HasChanges => FolderPathChanges.Count > 0
        || BmsPathReplacements.Count > 0
        || BmsonPathReplacements.Count > 0;

    private static IReadOnlyList<T> Snapshot<T>(IEnumerable<T> values)
    {
        return Array.AsReadOnly([.. (values ?? []).Where(value => value != null)]);
    }
}

/// <summary>
/// Immutable folder-row replacement facts for a catalog relocation transaction.
/// </summary>
internal sealed class CatalogFolderPathReplacement
{
    internal CatalogFolderPathReplacement(string oldFolderPath, string newFolderPath)
    {
        OldFolderPath = oldFolderPath;
        NewFolderPath = newFolderPath;
    }

    internal string OldFolderPath { get; }

    internal string NewFolderPath { get; }
}

/// <summary>
/// Immutable BMS row replacement facts plus the live owner to update after commit.
/// </summary>
internal sealed class BmsSongPathReplacement
{
    internal BmsSongPathReplacement(
        BMSFile song,
        BMSFile liveOwner,
        string oldPath,
        BMSFileMaintenanceInfo maintenanceInfo)
    {
        Song = song ?? throw new ArgumentNullException(nameof(song));
        LiveOwner = liveOwner ?? throw new ArgumentNullException(nameof(liveOwner));
        OldPath = oldPath;
        MaintenanceInfo = maintenanceInfo;
    }

    internal BMSFile Song { get; }

    internal BMSFile LiveOwner { get; }

    internal string OldPath { get; }

    internal BMSFileMaintenanceInfo MaintenanceInfo { get; }
}

/// <summary>
/// Immutable bmson row replacement facts plus the live owner to update after commit.
/// </summary>
internal sealed class BmsonSongPathReplacement
{
    internal BmsonSongPathReplacement(
        LR2SongDBExtended.bmson_song song,
        LR2SongDBExtended.bmson_song liveOwner,
        string oldPath)
    {
        Song = song ?? throw new ArgumentNullException(nameof(song));
        LiveOwner = liveOwner ?? throw new ArgumentNullException(nameof(liveOwner));
        OldPath = oldPath;
    }

    internal LR2SongDBExtended.bmson_song Song { get; }

    internal LR2SongDBExtended.bmson_song LiveOwner { get; }

    internal string OldPath { get; }
}

/// <summary>
/// Timing facts returned by the durable catalog relocation transaction.
/// </summary>
internal sealed class CatalogRelocationDbReceipt
{
    internal long FolderDbMs { get; set; }

    internal long BmsPathDbMs { get; set; }

    internal long BmsonPathDbMs { get; set; }

    internal long BmsRemovalDbMs { get; set; }

    internal long BmsonRemovalDbMs { get; set; }
}

/// <summary>
/// Immutable facts emitted after one catalog relocation/removal command commits and
/// updates the live catalog owners.
/// </summary>
internal sealed class CatalogMutationReceipt
{
    internal static CatalogMutationReceipt NotApplied { get; } =
        new(
            applied: false,
            new StorageRowsVersionSnapshot(0, 0),
            folderDbMs: 0,
            bmsPathDbMs: 0,
            bmsonPathDbMs: 0,
            bmsRemovalDbMs: 0,
            bmsonRemovalDbMs: 0,
            liveApplyMs: 0,
            ownedCollectionApplied: false,
            ownedCollectionVersion: 0,
            addedCharts: [],
            pathFacts: [],
            removalRequests: []);

    internal CatalogMutationReceipt(
        bool applied,
        StorageRowsVersionSnapshot storageRowsVersion,
        long folderDbMs,
        long bmsPathDbMs,
        long bmsonPathDbMs,
        long bmsRemovalDbMs,
        long bmsonRemovalDbMs,
        long liveApplyMs,
        bool ownedCollectionApplied,
        int ownedCollectionVersion,
        IEnumerable<CatalogChartMutationFact> addedCharts,
        IEnumerable<CatalogRelocationPathFact> pathFacts,
        IEnumerable<OwnedChartRemoveRequest> removalRequests)
    {
        Applied = applied;
        StorageRowsVersion = storageRowsVersion;
        FolderDbMs = folderDbMs;
        BmsPathDbMs = bmsPathDbMs;
        BmsonPathDbMs = bmsonPathDbMs;
        BmsRemovalDbMs = bmsRemovalDbMs;
        BmsonRemovalDbMs = bmsonRemovalDbMs;
        LiveApplyMs = liveApplyMs;
        OwnedCollectionApplied = ownedCollectionApplied;
        OwnedCollectionVersion = ownedCollectionVersion;
        Kind = applied
            ? CatalogMutationApplyKind.GenericMutation
            : CatalogMutationApplyKind.NoOp;
        AddedCharts = Array.AsReadOnly([.. (addedCharts ?? []).Where(fact => fact != null)]);
        PathFacts = Array.AsReadOnly([.. (pathFacts ?? []).Where(fact => fact != null)]);
        MovedCharts = PathFacts;
        RemovedCharts = CatalogChartMutationFact.CreateRemovalFacts(removalRequests);
    }

    internal bool Applied { get; }

    internal StorageRowsVersionSnapshot StorageRowsVersion { get; }

    internal long FolderDbMs { get; }

    internal long BmsPathDbMs { get; }

    internal long BmsonPathDbMs { get; }

    internal long BmsRemovalDbMs { get; }

    internal long BmsonRemovalDbMs { get; }

    internal long LiveApplyMs { get; }

    internal bool OwnedCollectionApplied { get; }

    internal int OwnedCollectionVersion { get; }

    internal CatalogMutationApplyKind Kind { get; }

    internal IReadOnlyList<CatalogChartMutationFact> AddedCharts { get; }

    internal IReadOnlyList<CatalogChartMutationFact> RemovedCharts { get; }

    internal IReadOnlyList<CatalogRelocationPathFact> MovedCharts { get; }

    internal IReadOnlyList<CatalogRelocationPathFact> PathFacts { get; }

}

/// <summary>
/// Immutable path fact carried by a relocation receipt.
/// </summary>
internal sealed class CatalogRelocationPathFact
{
    internal CatalogRelocationPathFact(
        ChartFileKind kind,
        string oldPath,
        string newPath,
        string md5 = null,
        string sha256 = null)
    {
        Kind = kind;
        OldPath = oldPath;
        NewPath = newPath;
        Md5 = md5;
        Sha256 = sha256;
    }

    internal ChartFileKind Kind { get; }

    internal string OldPath { get; }

    internal string NewPath { get; }

    internal string Md5 { get; }

    internal string Sha256 { get; }
}
