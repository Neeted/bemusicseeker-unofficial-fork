using System;
using System.Collections.Generic;
using System.Linq;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Immutable LR2 path facts consumed by the normal-folder owner after a
/// catalog mutation has applied its durable and live state.
/// </summary>
internal sealed class Lr2NormalFolderCatalogMutationReceipt
{
    internal Lr2NormalFolderCatalogMutationReceipt(
        int ownedCollectionVersion,
        IEnumerable<string> addedBmsChartPaths,
        IEnumerable<string> removedBmsChartPaths,
        IEnumerable<Lr2NormalFolderPathChange> pathChanges,
        IEnumerable<string> currentBmsChartPaths)
    {
        OwnedCollectionVersion = ownedCollectionVersion;
        AddedBmsChartPaths = SnapshotPaths(addedBmsChartPaths);
        RemovedBmsChartPaths = SnapshotPaths(removedBmsChartPaths);
        PathChanges = Array.AsReadOnly([.. (pathChanges ?? []).Where(change => change != null)]);
        SnapshotAvailable = currentBmsChartPaths != null;
        CurrentBmsChartPaths = Array.AsReadOnly((currentBmsChartPaths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray());
    }

    internal int OwnedCollectionVersion { get; }

    internal IReadOnlyList<string> AddedBmsChartPaths { get; }

    internal IReadOnlyList<string> RemovedBmsChartPaths { get; }

    internal IReadOnlyList<Lr2NormalFolderPathChange> PathChanges { get; }

    internal IReadOnlyList<string> CurrentBmsChartPaths { get; }

    internal bool SnapshotAvailable { get; }

    internal bool HasBmsMutation => AddedBmsChartPaths.Count > 0
        || RemovedBmsChartPaths.Count > 0
        || PathChanges.Count > 0;

    internal bool RequiresCurrentBmsChartSnapshot => RemovedBmsChartPaths.Count > 0
        || PathChanges.Count > 0;

    internal Lr2NormalFolderCurrentBmsLookup CreateCurrentBmsLookup()
    {
        return Lr2NormalFolderCurrentBmsLookup.CreateFromChartPaths(CurrentBmsChartPaths);
    }

    private static IReadOnlyList<string> SnapshotPaths(IEnumerable<string> paths)
    {
        return Array.AsReadOnly((paths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray());
    }
}

/// <summary>
/// The LR2 owner needs only the old and new BMS paths for a relocation.
/// Catalog hashes and removal semantics stay in the catalog owner.
/// </summary>
internal sealed class Lr2NormalFolderPathChange(string oldPath, string newPath)
{
    internal string OldPath { get; } = oldPath;

    internal string NewPath { get; } = newPath;
}
