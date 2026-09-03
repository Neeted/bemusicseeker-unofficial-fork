using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Applies precomputed mutation results to the live BMSLibrary state.
/// The facade must acquire every required lock before invoking this applier.
/// This type never acquires locks and never shows UI.
/// </summary>
internal sealed partial class PackageLifecycleOwner
{
    internal sealed class PackageStateMutationApplier(
        BmsLibraryDbGateway dbGateway,
        Func<ObservableCollection<ChartPackage>> getPendingPackages,
        Action<ObservableCollection<ChartPackage>> setPendingPackages,
        Func<ObservableCollection<ChartPackage>> getInstalledPackages,
        Action<ObservableCollection<ChartPackage>> setInstalledPackages,
        Action raiseInstalledPackagesChanged,
        Func<IEnumerable<ChartPackage>, ObservableCollection<ChartPackage>> packageCollectionFactory,
        IUiScheduler uiScheduler,
        Func<Action, bool> tryDeferCollectionMutation,
        Action<Action> runPackageCollectionStateMutation)
    {
        private readonly BmsLibraryDbGateway dbGateway = dbGateway ?? throw new ArgumentNullException(nameof(dbGateway));

        private readonly Func<ObservableCollection<ChartPackage>> getPendingPackages = getPendingPackages ?? throw new ArgumentNullException(nameof(getPendingPackages));

        private readonly Action<ObservableCollection<ChartPackage>> setPendingPackages = setPendingPackages ?? throw new ArgumentNullException(nameof(setPendingPackages));

        private readonly Func<ObservableCollection<ChartPackage>> getInstalledPackages = getInstalledPackages ?? throw new ArgumentNullException(nameof(getInstalledPackages));

        private readonly Action<ObservableCollection<ChartPackage>> setInstalledPackages = setInstalledPackages ?? throw new ArgumentNullException(nameof(setInstalledPackages));

        private readonly Action raiseInstalledPackagesChanged = raiseInstalledPackagesChanged ?? throw new ArgumentNullException(nameof(raiseInstalledPackagesChanged));

        private readonly Func<IEnumerable<ChartPackage>, ObservableCollection<ChartPackage>> packageCollectionFactory = packageCollectionFactory ?? throw new ArgumentNullException(nameof(packageCollectionFactory));

        private readonly IUiScheduler uiScheduler = uiScheduler ?? throw new ArgumentNullException(nameof(uiScheduler));

        private readonly Func<Action, bool> tryDeferCollectionMutation = tryDeferCollectionMutation ?? throw new ArgumentNullException(nameof(tryDeferCollectionMutation));

        private readonly Action<Action> runPackageCollectionStateMutation = runPackageCollectionStateMutation ?? throw new ArgumentNullException(nameof(runPackageCollectionStateMutation));

        public void ApplyPendingPackageMutationDelta(
            PendingPackageMutationDelta delta,
            IEnumerable<ChartPackage> packagesToAdd = null,
            IEnumerable<ChartPackage> installRowsToUpsert = null)
        {
            List<ChartPackage> packagesToAddList = [.. (packagesToAdd ?? []).Where(package => package != null)];
            List<ChartPackage> installRowsToUpsertList = [.. (installRowsToUpsert ?? []).Where(package => package != null)];
            List<string> installPathsToDelete = [.. (delta?.InstallPathsToDelete ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.Ordinal)];
            bool hasChanges = delta != null
                && (delta.HasChanges
                    || delta.EntryMutations.Count > 0
                    || installPathsToDelete.Count > 0
                    || packagesToAddList.Count > 0
                    || installRowsToUpsertList.Count > 0);
            if (!hasChanges)
            {
                return;
            }

            IEnumerable<ChartPackage> pendingPackages = delta?.RemainingPackages;
            if (pendingPackages == null)
            {
                pendingPackages = getPendingPackages();
            }
            List<ChartPackage> nextPendingPackages = [.. (pendingPackages ?? [])
            .Where(package => package != null)];
            nextPendingPackages.AddRange(packagesToAddList);
            if (packagesToAddList.Count > 0 || installRowsToUpsertList.Count > 0)
            {
                PackageLifecycleOwner.ValidatePendingPackagePathUniqueness(
                    nextPendingPackages,
                    installRowsToUpsertList);
            }
            ObservableCollection<ChartPackage> remainingPackages = packageCollectionFactory(nextPendingPackages);
            if (remainingPackages == null)
            {
                throw new InvalidOperationException("Package collection factory returned null.");
            }

            dbGateway.ApplyInstallTableMutation(installPathsToDelete, installRowsToUpsertList);
            foreach (PendingPackageEntryMutation entryMutation in delta.EntryMutations ?? [])
            {
                entryMutation?.Package?.ReplaceChartEntries(entryMutation.RemainingEntries);
            }
            setPendingPackages(remainingPackages);
        }

        public BmsLibraryStateApplyResult ApplyLibraryMutationDelta(
            LibraryMutationDelta delta,
            IEnumerable<CatalogChartMutationFact> committedRemovalFacts = null,
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
                InvokeOnUi(raiseInstalledPackagesChanged);
            }
            return result;
        }

        private void PruneInstalledPackagesForRemovedCharts(
            IEnumerable<CatalogChartMutationFact> removalFacts,
            IEnumerable<CatalogRelocationPathFact> protectedPathFacts)
        {
            if (removalFacts == null)
            {
                throw new ArgumentNullException(nameof(removalFacts));
            }

            List<CatalogChartMutationFact> factList = [.. removalFacts.Where(fact => fact != null)];
            if (factList.Count == 0)
            {
                return;
            }

            HashSet<string> bmsOwnerIdentityKeys = CreateOwnerIdentityKeys(
                factList,
                protectedPathFacts,
                ChartFileKind.Bms);
            HashSet<string> protectedBmsPathKeys = CreateProtectedPathKeys(
                protectedPathFacts,
                factList,
                ChartFileKind.Bms);
            List<string> bmsPathCleanupPaths = [.. factList
            .Where(fact => fact.RemovalMode == OwnedChartRemoveMode.PathCleanup && fact.Kind == ChartFileKind.Bms)
            .Select(fact => fact.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Where(path => !protectedBmsPathKeys.Contains(OwnedChartCollectionState.CreateOwnedPathKey(path)))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
            if (bmsOwnerIdentityKeys.Count > 0 || bmsPathCleanupPaths.Count > 0)
            {
                PruneInstalledPackagesForBmsFiles(bmsOwnerIdentityKeys, bmsPathCleanupPaths);
            }

            HashSet<string> bmsonOwnerIdentityKeys = CreateOwnerIdentityKeys(
                factList,
                protectedPathFacts,
                ChartFileKind.Bmson);
            HashSet<string> protectedBmsonPathKeys = CreateProtectedPathKeys(
                protectedPathFacts,
                factList,
                ChartFileKind.Bmson);
            List<string> bmsonPathCleanupPaths = [.. factList
            .Where(fact => fact.RemovalMode == OwnedChartRemoveMode.PathCleanup && fact.Kind == ChartFileKind.Bmson)
            .Select(fact => fact.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Where(path => !protectedBmsonPathKeys.Contains(OwnedChartCollectionState.CreateOwnedPathKey(path)))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
            if (bmsonOwnerIdentityKeys.Count > 0 || bmsonPathCleanupPaths.Count > 0)
            {
                PruneInstalledPackagesForBmsonSongs(bmsonOwnerIdentityKeys, bmsonPathCleanupPaths);
            }
        }

        private static HashSet<string> CreateOwnerIdentityKeys(
            IEnumerable<CatalogChartMutationFact> removalFacts,
            IEnumerable<CatalogRelocationPathFact> pathFacts,
            ChartFileKind kind)
        {
            List<CatalogChartMutationFact> ownerFacts = [..
            (removalFacts ?? [])
                .Where(fact => fact?.Kind == kind
                    && fact.RemovalMode != OwnedChartRemoveMode.PathCleanup)];
            var identityKeys = new HashSet<string>(
                ownerFacts
                    .Select(fact => CreateChartIdentityKey(
                        fact.Kind,
                        fact.Path,
                        fact.Md5,
                        fact.Sha256))
                    .Where(key => !string.IsNullOrWhiteSpace(key)),
                StringComparer.OrdinalIgnoreCase);
            foreach (CatalogRelocationPathFact pathFact in (pathFacts ?? [])
                .Where(fact => fact?.Kind == kind))
            {
                if (!ownerFacts.Any(fact => IsSameOwner(fact, pathFact)))
                {
                    continue;
                }

                string relocatedIdentityKey = CreateChartIdentityKey(
                    pathFact.Kind,
                    pathFact.NewPath,
                    pathFact.Md5,
                    pathFact.Sha256);
                if (!string.IsNullOrWhiteSpace(relocatedIdentityKey))
                {
                    identityKeys.Add(relocatedIdentityKey);
                }
            }
            return identityKeys;
        }

        private static string CreateChartIdentityKey(
            ChartFileKind kind,
            string path,
            string md5,
            string sha256)
        {
            string pathKey = OwnedChartCollectionState.CreateOwnedPathKey(path) ?? string.Empty;
            string md5Key = md5?.Trim() ?? string.Empty;
            string sha256Key = sha256?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(pathKey)
                && string.IsNullOrWhiteSpace(md5Key)
                && string.IsNullOrWhiteSpace(sha256Key))
            {
                return null;
            }
            return (int)kind + "|" + md5Key + "|" + sha256Key + "|" + pathKey;
        }

        private static bool IsSameOwner(
            CatalogChartMutationFact removedFact,
            CatalogRelocationPathFact pathFact)
        {
            if (removedFact?.Kind != pathFact?.Kind
                || removedFact.RemovalMode == OwnedChartRemoveMode.PathCleanup)
            {
                return false;
            }

            string removedPathKey = OwnedChartCollectionState.CreateOwnedPathKey(removedFact.Path);
            string relocationOldPathKey = OwnedChartCollectionState.CreateOwnedPathKey(pathFact.OldPath);
            bool samePath = !string.IsNullOrWhiteSpace(removedPathKey)
                && string.Equals(removedPathKey, relocationOldPathKey, StringComparison.OrdinalIgnoreCase);
            bool relocationHasDigest = !string.IsNullOrWhiteSpace(pathFact.Md5)
                || !string.IsNullOrWhiteSpace(pathFact.Sha256);
            bool sameDigest = (!relocationHasDigest
                    || (string.IsNullOrWhiteSpace(removedFact.Md5)
                        || string.Equals(removedFact.Md5, pathFact.Md5, StringComparison.OrdinalIgnoreCase))
                    && (string.IsNullOrWhiteSpace(removedFact.Sha256)
                        || string.Equals(removedFact.Sha256, pathFact.Sha256, StringComparison.OrdinalIgnoreCase)));
            return samePath && sameDigest;
        }

        private static HashSet<string> CreateProtectedPathKeys(
            IEnumerable<CatalogRelocationPathFact> protectedPathFacts,
            IEnumerable<CatalogChartMutationFact> removalFacts,
            ChartFileKind kind)
        {
            return new HashSet<string>(
                (protectedPathFacts ?? [])
                    .Where(fact => fact?.Kind == kind)
                    .Where(fact => !(removalFacts ?? []).Any(removedFact => IsSameOwner(removedFact, fact)))
                    .Select(fact => OwnedChartCollectionState.CreateOwnedPathKey(fact.NewPath))
                    .Where(path => !string.IsNullOrWhiteSpace(path)),
                StringComparer.OrdinalIgnoreCase);
        }

        private void PruneInstalledPackagesForBmsFiles(IEnumerable<string> removedIdentityKeys, IEnumerable<string> pathCleanupPaths)
        {
            if (removedIdentityKeys == null && pathCleanupPaths == null)
            {
                throw new ArgumentNullException(nameof(removedIdentityKeys));
            }

            HashSet<string> removedIdentityKeySet = new(
                (removedIdentityKeys ?? []).Where(key => !string.IsNullOrWhiteSpace(key)),
                StringComparer.OrdinalIgnoreCase);
            List<string> pathCleanupList = [.. (pathCleanupPaths ?? []).Where(path => !string.IsNullOrWhiteSpace(path))];
            if (removedIdentityKeySet.Count == 0 && pathCleanupList.Count == 0)
            {
                return;
            }

            var pathCleanupSet = new HashSet<string>(
                pathCleanupList.Select(OwnedChartCollectionState.CreateOwnedPathKey).Where(path => !string.IsNullOrWhiteSpace(path)),
                StringComparer.OrdinalIgnoreCase);
            bool installedPackagesChanged = false;
            runPackageCollectionStateMutation(() =>
            {
                ObservableCollection<ChartPackage> installedPackages = getInstalledPackages();
                List<ChartPackage> emptyInstalledPackages = [];
                foreach (ChartPackage installedPackage in installedPackages.Where(package => package != null).ToList())
                {
                    if (installedPackage.RemoveChartEntries(entry => IsMatchedRemovedFile(entry, removedIdentityKeySet, pathCleanupSet)))
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
                    HashSet<ChartPackage> emptyPackageSet = [.. emptyInstalledPackages];
                    setInstalledPackages(packageCollectionFactory(
                        installedPackages.Where(package => !emptyPackageSet.Contains(package))));
                }
            });

            if (installedPackagesChanged)
            {
                InvokeOnUi(raiseInstalledPackagesChanged);
            }
        }

        private void PruneInstalledPackagesForBmsonSongs(IEnumerable<string> removedIdentityKeys, IEnumerable<string> pathCleanupPaths)
        {
            if (removedIdentityKeys == null && pathCleanupPaths == null)
            {
                throw new ArgumentNullException(nameof(removedIdentityKeys));
            }

            HashSet<string> removedIdentityKeySet = new(
                (removedIdentityKeys ?? []).Where(key => !string.IsNullOrWhiteSpace(key)),
                StringComparer.OrdinalIgnoreCase);
            List<string> pathCleanupList = [.. (pathCleanupPaths ?? []).Where(path => !string.IsNullOrWhiteSpace(path))];
            if (removedIdentityKeySet.Count == 0 && pathCleanupList.Count == 0)
            {
                return;
            }

            var pathCleanupSet = new HashSet<string>(
                pathCleanupList.Select(OwnedChartCollectionState.CreateOwnedPathKey).Where(path => !string.IsNullOrWhiteSpace(path)),
                StringComparer.OrdinalIgnoreCase);
            bool installedPackagesChanged = false;
            runPackageCollectionStateMutation(() =>
            {
                ObservableCollection<ChartPackage> installedPackages = getInstalledPackages();
                List<ChartPackage> emptyInstalledPackages = [];
                foreach (ChartPackage installedPackage in installedPackages.Where(package => package != null).ToList())
                {
                    if (installedPackage.RemoveChartEntries(entry => IsMatchedRemovedBmsonFile(entry, removedIdentityKeySet, pathCleanupSet)))
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
                    HashSet<ChartPackage> emptyPackageSet = [.. emptyInstalledPackages];
                    setInstalledPackages(packageCollectionFactory(
                        installedPackages.Where(package => !emptyPackageSet.Contains(package))));
                }
            });
            if (installedPackagesChanged)
            {
                InvokeOnUi(raiseInstalledPackagesChanged);
            }
        }

        private void InvokeOnUi(Action mutation)
        {
            if (mutation == null)
            {
                throw new ArgumentNullException(nameof(mutation));
            }
            if (tryDeferCollectionMutation(mutation))
            {
                return;
            }
            uiScheduler.Invoke(mutation);
        }

        private static bool IsMatchedRemovedFile(PackageChartEntry entry, HashSet<string> removedIdentityKeys, HashSet<string> pathCleanupPaths)
        {
            ChartFile chart = entry?.Chart;
            if (chart?.Kind != ChartFileKind.Bms)
            {
                return false;
            }

            string identityKey = CreateChartIdentityKey(
                chart.Kind,
                chart.Path,
                chart.Md5,
                chart.Sha256);
            if (!string.IsNullOrWhiteSpace(identityKey) && removedIdentityKeys.Contains(identityKey))
            {
                return true;
            }

            string pathKey = OwnedChartCollectionState.CreateOwnedPathKey(chart.Path);
            return !string.IsNullOrWhiteSpace(pathKey) && pathCleanupPaths.Contains(pathKey);
        }

        private static bool IsMatchedRemovedBmsonFile(PackageChartEntry entry, HashSet<string> removedIdentityKeys, HashSet<string> pathCleanupPaths)
        {
            ChartFile chart = entry?.Chart;
            if (chart?.Kind != ChartFileKind.Bmson)
            {
                return false;
            }

            string identityKey = CreateChartIdentityKey(
                chart.Kind,
                chart.Path,
                chart.Md5,
                chart.Sha256);
            if (!string.IsNullOrWhiteSpace(identityKey) && removedIdentityKeys.Contains(identityKey))
            {
                return true;
            }

            string pathKey = OwnedChartCollectionState.CreateOwnedPathKey(chart.Path);
            return !string.IsNullOrWhiteSpace(pathKey) && pathCleanupPaths.Contains(pathKey);
        }
    }
}
