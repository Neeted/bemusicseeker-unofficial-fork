using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using BeMusicSeeker.Models.LR2;
using Livet;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Applies precomputed mutation results to the live BMSLibrary state.
/// The facade must acquire every required lock before invoking this applier.
/// This type never acquires locks and never shows UI.
/// </summary>
internal sealed class BmsLibraryStateApplier(
    BmsLibraryDbGateway dbGateway,
    Func<DispatcherCollection<ChartPackage>> getPendingPackages,
    Action<DispatcherCollection<ChartPackage>> setPendingPackages,
    Func<DispatcherCollection<ChartPackage>> getInstalledPackages,
    Action<DispatcherCollection<ChartPackage>> setInstalledPackages,
    Action raiseInstalledPackagesChanged)
{
    private readonly BmsLibraryDbGateway dbGateway = dbGateway ?? throw new ArgumentNullException(nameof(dbGateway));

    private readonly Func<DispatcherCollection<ChartPackage>> getPendingPackages = getPendingPackages ?? throw new ArgumentNullException(nameof(getPendingPackages));

    private readonly Action<DispatcherCollection<ChartPackage>> setPendingPackages = setPendingPackages ?? throw new ArgumentNullException(nameof(setPendingPackages));

    private readonly Func<DispatcherCollection<ChartPackage>> getInstalledPackages = getInstalledPackages ?? throw new ArgumentNullException(nameof(getInstalledPackages));

    private readonly Action<DispatcherCollection<ChartPackage>> setInstalledPackages = setInstalledPackages ?? throw new ArgumentNullException(nameof(setInstalledPackages));

    private readonly Action raiseInstalledPackagesChanged = raiseInstalledPackagesChanged ?? throw new ArgumentNullException(nameof(raiseInstalledPackagesChanged));

    public void ApplyPendingPackageMutationDelta(PendingPackageMutationDelta delta)
    {
        if (delta == null || !delta.HasChanges)
        {
            return;
        }

        setPendingPackages(new DispatcherCollection<ChartPackage>(
            new ObservableCollection<ChartPackage>(delta.RemainingPackages ?? []),
            DispatcherHelper.UIDispatcher));

        List<string> installPathsToDelete = [.. (delta.InstallPathsToDelete ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        if (installPathsToDelete.Count == 0)
        {
            return;
        }

        dbGateway.DeleteInstallRows(installPathsToDelete);
    }

    public BmsLibraryStateApplyResult ApplyLibraryMutationDelta(
        LibraryMutationDelta delta,
        IEnumerable<CatalogMutationRemovalFact> committedRemovalFacts = null,
        IEnumerable<CatalogRelocationPathFact> protectedPathFacts = null)
    {
        var result = new BmsLibraryStateApplyResult();
        if (delta == null)
        {
            return result;
        }

        Stopwatch stopwatch = Stopwatch.StartNew();
        foreach (LibraryInstallDestinationChange installDestinationChange in delta.UpdatedInstallDestinations)
        {
            if (installDestinationChange?.Entry != null)
            {
                if (installDestinationChange.ClearInstallDestinationState)
                {
                    installDestinationChange.Entry.ClearInstallDestination();
                }
                else
                {
                    installDestinationChange.Entry.SetInstallDestinationPathOnly(installDestinationChange.NewInstallDestination);
                }
            }
        }

        foreach (LibraryInstalledPackagePathChange installedPackagePathChange in delta.UpdatedInstalledPackagePaths)
        {
            if (installedPackagePathChange?.Package != null)
            {
                installedPackagePathChange.Package.path = installedPackagePathChange.NewPath;
            }
        }
        stopwatch.Stop();
        result.PackageApplyMs = stopwatch.ElapsedMilliseconds;

        if (committedRemovalFacts != null)
        {
            PruneInstalledPackagesForRemovedCharts(committedRemovalFacts, protectedPathFacts);
        }

        if (delta.RaiseInstalledPackagesChanged)
        {
            raiseInstalledPackagesChanged();
        }
        return result;
    }

    private void PruneInstalledPackagesForRemovedCharts(
        IEnumerable<CatalogMutationRemovalFact> removalFacts,
        IEnumerable<CatalogRelocationPathFact> protectedPathFacts)
    {
        if (removalFacts == null)
        {
            throw new ArgumentNullException(nameof(removalFacts));
        }

        List<CatalogMutationRemovalFact> factList = [.. removalFacts.Where(fact => fact != null)];
        if (factList.Count == 0)
        {
            return;
        }

        List<BMSFile> bmsFilesToUnregister = [.. factList
            .Select(fact => fact.BmsOwner)
            .Where(owner => owner != null)
            .Distinct()];
        HashSet<string> protectedBmsPathKeys = CreateProtectedPathKeys(
            protectedPathFacts,
            ChartFileKind.Bms);
        List<string> bmsPathCleanupPaths = [.. factList
            .Where(fact => fact.Mode == OwnedChartRemoveMode.PathCleanup && fact.Kind == ChartFileKind.Bms)
            .Select(fact => fact.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Where(path => !protectedBmsPathKeys.Contains(OwnedChartCollectionState.CreateOwnedPathKey(path)))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        if (bmsFilesToUnregister.Count > 0 || bmsPathCleanupPaths.Count > 0)
        {
            PruneInstalledPackagesForBmsFiles(bmsFilesToUnregister, bmsPathCleanupPaths);
        }

        List<LR2SongDBExtended.bmson_song> bmsonSongsToUnregister = [.. factList
            .Select(fact => fact.BmsonOwner)
            .Where(owner => owner != null)
            .Distinct()];
        HashSet<string> protectedBmsonPathKeys = CreateProtectedPathKeys(
            protectedPathFacts,
            ChartFileKind.Bmson);
        List<string> bmsonPathCleanupPaths = [.. factList
            .Where(fact => fact.Mode == OwnedChartRemoveMode.PathCleanup && fact.Kind == ChartFileKind.Bmson)
            .Select(fact => fact.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Where(path => !protectedBmsonPathKeys.Contains(OwnedChartCollectionState.CreateOwnedPathKey(path)))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        if (bmsonSongsToUnregister.Count > 0 || bmsonPathCleanupPaths.Count > 0)
        {
            PruneInstalledPackagesForBmsonSongs(bmsonSongsToUnregister, bmsonPathCleanupPaths);
        }
    }

    private static HashSet<string> CreateProtectedPathKeys(
        IEnumerable<CatalogRelocationPathFact> protectedPathFacts,
        ChartFileKind kind)
    {
        return new HashSet<string>(
            (protectedPathFacts ?? [])
                .Where(fact => fact?.Kind == kind)
                .Select(fact => OwnedChartCollectionState.CreateOwnedPathKey(fact.NewPath))
                .Where(path => !string.IsNullOrWhiteSpace(path)),
            StringComparer.OrdinalIgnoreCase);
    }

    private void PruneInstalledPackagesForBmsFiles(IEnumerable<BMSFile> bmsFiles, IEnumerable<string> pathCleanupPaths)
    {
        if (bmsFiles == null && pathCleanupPaths == null)
        {
            throw new ArgumentNullException(nameof(bmsFiles));
        }

        List<BMSFile> removedFilesList = [.. bmsFiles.Where(file => file != null)];
        List<string> pathCleanupList = [.. (pathCleanupPaths ?? []).Where(path => !string.IsNullOrWhiteSpace(path))];
        if (removedFilesList.Count == 0 && pathCleanupList.Count == 0)
        {
            return;
        }

        var pathCleanupSet = new HashSet<string>(
            pathCleanupList.Select(OwnedChartCollectionState.CreateOwnedPathKey).Where(path => !string.IsNullOrWhiteSpace(path)),
            StringComparer.OrdinalIgnoreCase);
        var removedFileRefs = new HashSet<BMSFile>(removedFilesList);
        DispatcherCollection<ChartPackage> installedPackages = getInstalledPackages();
        bool installedPackagesChanged = false;
        List<ChartPackage> emptyInstalledPackages = [];

        foreach (ChartPackage installedPackage in installedPackages.Where(package => package != null).ToList())
        {
            if (installedPackage.RemoveChartEntries(entry => IsMatchedRemovedFile(entry, removedFileRefs, pathCleanupSet)))
            {
                installedPackagesChanged = true;
                if (installedPackage.ChartEntries.Count == 0)
                {
                    emptyInstalledPackages.Add(installedPackage);
                }
            }
        }

        if (emptyInstalledPackages.Count > 0)
        {
            foreach (ChartPackage emptyInstalledPackage in emptyInstalledPackages)
            {
                installedPackages.Remove(emptyInstalledPackage);
            }
            setInstalledPackages(installedPackages);
        }

        if (installedPackagesChanged)
        {
            raiseInstalledPackagesChanged();
        }
    }

    private void PruneInstalledPackagesForBmsonSongs(IEnumerable<LR2SongDBExtended.bmson_song> bmsonSongs, IEnumerable<string> pathCleanupPaths)
    {
        if (bmsonSongs == null && pathCleanupPaths == null)
        {
            throw new ArgumentNullException(nameof(bmsonSongs));
        }

        List<LR2SongDBExtended.bmson_song> removedSongsList = [.. bmsonSongs.Where(song => song != null && !string.IsNullOrWhiteSpace(song.path))];
        List<string> pathCleanupList = [.. (pathCleanupPaths ?? []).Where(path => !string.IsNullOrWhiteSpace(path))];
        if (removedSongsList.Count == 0 && pathCleanupList.Count == 0)
        {
            return;
        }

        var pathCleanupSet = new HashSet<string>(
            pathCleanupList.Select(OwnedChartCollectionState.CreateOwnedPathKey).Where(path => !string.IsNullOrWhiteSpace(path)),
            StringComparer.OrdinalIgnoreCase);
        var removedSongRefs = new HashSet<LR2SongDBExtended.bmson_song>(removedSongsList);
        DispatcherCollection<ChartPackage> installedPackages = getInstalledPackages();
        bool installedPackagesChanged = false;
        List<ChartPackage> emptyInstalledPackages = [];
        foreach (ChartPackage installedPackage in installedPackages.Where(package => package != null).ToList())
        {
            if (installedPackage.RemoveChartEntries(entry => IsMatchedRemovedBmsonFile(entry, removedSongRefs, pathCleanupSet)))
            {
                installedPackagesChanged = true;
                if (installedPackage.ChartEntries.Count == 0)
                {
                    emptyInstalledPackages.Add(installedPackage);
                }
            }
        }
        if (emptyInstalledPackages.Count > 0)
        {
            foreach (ChartPackage emptyInstalledPackage in emptyInstalledPackages)
            {
                installedPackages.Remove(emptyInstalledPackage);
            }
            setInstalledPackages(installedPackages);
        }
        if (installedPackagesChanged)
        {
            raiseInstalledPackagesChanged();
        }
    }

    private static bool IsMatchedRemovedFile(PackageChartEntry entry, HashSet<BMSFile> removedFiles, HashSet<string> pathCleanupPaths)
    {
        if (entry?.Chart == null)
        {
            return false;
        }

        BMSFile bmsFile = entry.Chart.GetBmsStorageOwner();
        if (bmsFile != null && removedFiles.Contains(bmsFile))
        {
            return true;
        }

        string pathKey = OwnedChartCollectionState.CreateOwnedPathKey(entry.Chart.Path);
        return !string.IsNullOrWhiteSpace(pathKey) && pathCleanupPaths.Contains(pathKey);
    }

    private static bool IsMatchedRemovedBmsonFile(PackageChartEntry entry, HashSet<LR2SongDBExtended.bmson_song> removedSongs, HashSet<string> pathCleanupPaths)
    {
        if (entry?.Chart == null)
        {
            return false;
        }

        LR2SongDBExtended.bmson_song bmsonSong = entry.Chart.GetBmsonStorageOwner();
        if (bmsonSong != null && removedSongs.Contains(bmsonSong))
        {
            return true;
        }

        string pathKey = OwnedChartCollectionState.CreateOwnedPathKey(entry.Chart.Path);
        return !string.IsNullOrWhiteSpace(pathKey) && pathCleanupPaths.Contains(pathKey);
    }
}
