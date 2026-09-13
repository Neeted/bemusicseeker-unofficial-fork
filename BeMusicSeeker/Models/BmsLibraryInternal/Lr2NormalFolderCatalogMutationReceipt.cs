using System;
using System.Collections.Generic;
using System.Linq;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// catalog mutation の durable / live state 適用後に normal-folder owner が
/// 消費する不変な LR2 path facts です。
/// </summary>
internal sealed class Lr2NormalFolderCatalogMutationReceipt
{
    /// <summary>
    /// catalog mutation の結果と、捕捉済み current BMS facts を receipt として固定します。
    /// </summary>
    /// <param name="ownedCollectionVersion">facts と対応する owned collection 世代。</param>
    /// <param name="addedBmsChartPaths">追加された BMS の exact path。</param>
    /// <param name="removedBmsChartPaths">削除された BMS の exact path。</param>
    /// <param name="pathChanges">移動前後の BMS path facts。</param>
    /// <param name="currentBmsFacts">捕捉済み current BMS の局所 facts。</param>
    internal Lr2NormalFolderCatalogMutationReceipt(
        int ownedCollectionVersion,
        IEnumerable<string> addedBmsChartPaths,
        IEnumerable<string> removedBmsChartPaths,
        IEnumerable<Lr2NormalFolderPathChange> pathChanges,
        Lr2NormalFolderCurrentBmsLookup currentBmsFacts)
    {
        OwnedCollectionVersion = ownedCollectionVersion;
        AddedBmsChartPaths = SnapshotPaths(addedBmsChartPaths);
        RemovedBmsChartPaths = SnapshotPaths(removedBmsChartPaths);
        PathChanges = Array.AsReadOnly([.. (pathChanges ?? []).Where(change => change != null)]);
        CurrentBmsFacts = currentBmsFacts;
    }

    internal int OwnedCollectionVersion { get; }

    internal IReadOnlyList<string> AddedBmsChartPaths { get; }

    internal IReadOnlyList<string> RemovedBmsChartPaths { get; }

    internal IReadOnlyList<Lr2NormalFolderPathChange> PathChanges { get; }

    /// <summary>
    /// 捕捉時にコピーされた現在 BMS の局所 facts です。
    /// </summary>
    internal Lr2NormalFolderCurrentBmsLookup CurrentBmsFacts { get; }

    /// <summary>current BMS facts が利用可能かを返します。</summary>
    internal bool SnapshotAvailable => CurrentBmsFacts != null;

    internal bool HasBmsMutation => AddedBmsChartPaths.Count > 0
        || RemovedBmsChartPaths.Count > 0
        || PathChanges.Count > 0;

    internal bool RequiresCurrentBmsChartSnapshot => RemovedBmsChartPaths.Count > 0
        || PathChanges.Count > 0;

    internal Lr2NormalFolderCurrentBmsLookup CreateCurrentBmsLookup()
    {
        return CurrentBmsFacts ?? Lr2NormalFolderCurrentBmsLookup.Empty;
    }

    private static IReadOnlyList<string> SnapshotPaths(IEnumerable<string> paths)
    {
        return Array.AsReadOnly((paths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.Ordinal)
            .ToArray());
    }
}

/// <summary>
/// LR2 owner が relocation に必要とする old/new BMS path です。
/// catalog hash と削除の意味は catalog owner が保持します。
/// </summary>
internal sealed class Lr2NormalFolderPathChange(string oldPath, string newPath)
{
    internal string OldPath { get; } = oldPath;

    internal string NewPath { get; } = newPath;
}
