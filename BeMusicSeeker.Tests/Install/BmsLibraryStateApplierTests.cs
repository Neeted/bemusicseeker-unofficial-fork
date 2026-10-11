using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static BeMusicSeeker.Tests.BmsLibraryStateApplierTestSupport;
using PackageStateMutationApplier = BeMusicSeeker.Models.BmsLibraryInternal.PackageLifecycleOwner.PackageStateMutationApplier;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BmsLibraryStateApplierTests
{
    /// <summary>二回の未公開進捗を一UI投稿へ合流し、最新の段階・件数・pathを一回通知することから古い値の公開や重複投稿を検出します。</summary>
    /// <remarks>窓口への進捗報告とUI投稿を接続します。DBの内容は検査せず、実Windowも使いません。</remarks>
    [TestMethod]
    public void LibraryInitializationProgress_CoalescesPendingReportsIntoOneTypedUiCommit()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var scheduler = new QueuedProgressUiScheduler();
            var library = new TestBmsLibrary(
                songDbPath,
                getLR2Config: null!,
                _lr2ScoreDB: null,
                fileMutationService: null!,
                dialogService: null!,
                scheduler);
            int versionNotifications = 0;
            library.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(BMSLibrary.LibraryInitializationProgressVersion))
                {
                    versionNotifications++;
                }
            };
            MethodInfo report = typeof(BMSLibrary).GetMethod(
                "ReportLibraryInitializationProgress",
                BindingFlags.Instance | BindingFlags.NonPublic)!;

            InvokeProgress(report, library, BMSLibrary.LibraryInitializationProgressStage.FileEnumeration, 12, 1, "first");
            InvokeProgress(report, library, BMSLibrary.LibraryInitializationProgressStage.FileDiff, 12, 12, "last");

            Assert.AreEqual(1, scheduler.PendingCount);

            scheduler.ExecuteNext();

            BMSLibrary.LibraryInitializationProgressSnapshot snapshot =
                library.GetLibraryInitializationProgressSnapshot();
            Assert.AreEqual(1, versionNotifications);
            Assert.AreEqual(2L, snapshot.Version);
            Assert.AreEqual(BMSLibrary.LibraryInitializationProgressStage.FileDiff, snapshot.Stage);
            Assert.AreEqual(12, snapshot.TotalCount);
            Assert.AreEqual(12, snapshot.ProcessedCount);
            Assert.AreEqual("last", snapshot.CurrentPath);
            Assert.AreEqual(0, scheduler.PendingCount);
        });
    }

    private static void InvokeProgress(
        MethodInfo report,
        BMSLibrary library,
        BMSLibrary.LibraryInitializationProgressStage stage,
        int total,
        int processed,
        string path)
    {
        report.Invoke(
            library,
            [stage, "managed", total, processed, path, true]);
    }


    /// <summary>宛先の全解除factsはpath・title・artist・候補・推定警告を消し、部分解除として古い推定を残す誤りを検出します。</summary>
    [TestMethod]
    public void CreateAppliedSnapshots_FullClearRemovesDestinationMetadataSuggestionsAndWarnings()
    {

        ChartFile file = ChartTestValues.Empty() with
        {
            Path = @"C:\Library\chart.bms"
        };
        file = file with { Warnings = [.. file.Warnings.Where(warning => warning.Kind != ChartWarningKind.InstallEstimationAmbiguous), ChartWarning.Create(ChartWarningKind.InstallEstimationAmbiguous, "ambiguous")] };
        LibraryInstallDestinationChange installDestinationChange = new()
        {
            Chart = ChartFileProjection.WithPackageState(
                (file),
                @"C:\Deleted",
                "Deleted title",
                "Deleted artist",
                [@"C:\Deleted", @"C:\Other"],
                file.Warnings),
            NewInstallDestination = null,
            ClearInstallDestinationState = true
        };
        LibraryPackageReferenceFacts packageFacts = new([installDestinationChange], []);


        ChartFile appliedChart = packageFacts.CreateAppliedInstallDestinationChartSnapshots().Single();
        Assert.AreEqual(string.Empty, appliedChart.InstallDestination);
        Assert.AreEqual(string.Empty, appliedChart.InstallDestinationTitle);
        Assert.AreEqual(string.Empty, appliedChart.InstallDestinationArtist);
        Assert.AreEqual(0, appliedChart.InstallDestinationSuggestions.Count);
        Assert.IsFalse(appliedChart.Warnings.Any(warning => warning.Category == ChartWarningCategory.InstallEstimation));
    }

    /// <summary>pathだけの空解除factsはtitle・artist・候補を保持し、全解除として推定情報を消す誤りを検出します。</summary>
    [TestMethod]
    public void CreateAppliedSnapshots_PathOnlyNullPreservesDestinationMetadataAndSuggestions()
    {

        ChartFile file = ChartTestValues.Empty() with
        {
            Path = @"C:\Library\chart.bms"
        };
        file = file with { Warnings = [.. file.Warnings.Where(warning => warning.Kind != ChartWarningKind.InstallEstimationAmbiguous), ChartWarning.Create(ChartWarningKind.InstallEstimationAmbiguous, "ambiguous")] };
        LibraryInstallDestinationChange installDestinationChange = new()
        {
            Chart = ChartFileProjection.WithPackageState(
                (file),
                @"C:\Installed",
                "Candidate title",
                "Candidate artist",
                [@"C:\Installed", @"C:\Other"],
                file.Warnings),
            NewInstallDestination = null,
            ClearInstallDestinationState = false
        };
        LibraryPackageReferenceFacts packageFacts = new([installDestinationChange], []);


        ChartFile appliedChart = packageFacts.CreateAppliedInstallDestinationChartSnapshots().Single();
        Assert.AreEqual(string.Empty, appliedChart.InstallDestination);
        Assert.AreEqual("Candidate title", appliedChart.InstallDestinationTitle);
        Assert.AreEqual("Candidate artist", appliedChart.InstallDestinationArtist);
        CollectionAssert.AreEqual(new[] { @"C:\Installed", @"C:\Other" }, appliedChart.InstallDestinationSuggestions.ToArray());
    }

    /// <summary>確定BMS削除factsから該当packageだけを除き、残存packageとUI threadへの一回通知を保つ剪定・公開漏れを検出します。</summary>
    /// <remarks>確定factsをpackage参照へ反映する境界です。DB・正本の実保存はExactRemovalとMoveAndRemoveのケースが担います。</remarks>
    [TestMethod]
    public void ApplyPackageReferenceFacts_UnregisterPrunesOnlyMatchingBmsPackageAndPublishesOnUiThread()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            ChartFile removedFile = ChartTestValues.Empty() with
            {
                Token = new OwnedChartToken(),
                Path = "C:\\Library\\remove.bms"
            };
            removedFile = removedFile with { Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" };
            ChartFile keptFile = ChartTestValues.Empty() with
            {
                Token = new OwnedChartToken(),
                Path = "C:\\Library\\keep.bms"
            };
            keptFile = keptFile with { Md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" };
            ChartPackage removedPackage = ChartPackageTestExtensions.CreatePackage([removedFile]);
            removedPackage.path = "C:\\Installed\\RemovePkg";
            removedPackage.delete_parent = false;
            ChartPackage keptPackage = ChartPackageTestExtensions.CreatePackage([keptFile]);
            keptPackage.path = "C:\\Installed\\KeepPkg";
            keptPackage.delete_parent = false;

            ObservableCollection<ChartPackage> pendingPackages = CreatePackageCollection([]);
            ObservableCollection<ChartPackage> installedPackages = CreatePackageCollection([removedPackage, keptPackage]);
            var callbacks = new TrackingCallbacks();
            PackageStateMutationApplier applier = CreateStateApplier(songDbPath, callbacks, () => pendingPackages, packages => pendingPackages = packages, () => installedPackages, packages => installedPackages = packages);

            ApplyCommittedMutation(
                applier,
                CreateUnregisterFacts([(removedFile)]));

            Assert.AreEqual(1, installedPackages.Count);
            Assert.AreSame(keptPackage, installedPackages.Single());
            Assert.AreEqual(1, callbacks.InstalledPackagesChangedCount);
            int uiThreadId = TestUiDispatcherHost.Dispatcher.Invoke(() => Environment.CurrentManagedThreadId);
            Assert.AreEqual(uiThreadId, callbacks.InstalledPackagesChangedThreadId);
        });
    }

    /// <summary>
    /// 確定したexact削除集合をDB・storage・owned・packageへ同じ意味で反映し、移動先と非対象行を保護します。
    /// </summary>
    /// <remarks>旧DBの完全一致keyと物理パスの別名、両形式・maintenance・ユーザー列を実SQLiteの確定後に照合します。</remarks>
    [DataTestMethod]
    // case-only・別名・dot別名、両対象削除、移転先保護、token指定削除を区別します。
    [DataRow("CHART", false, false, false)]
    [DataRow("other", false, false, false)]
    [DataRow(".\\chart", false, false, false)]
    [DataRow("CHART", false, true, false)]
    [DataRow("other", false, true, false)]
    [DataRow("CHART", true, false, false)]
    [DataRow("other", true, false, false)]
    [DataRow("CHART", false, false, true)]
    [DataRow("other", false, false, true)]
    public void ApplyPackageReferenceFacts_ExactRemovalKeepsOtherRowsAndPackages(
        string removedName, bool relocate, bool removeBoth, bool ownerReference)
    {
        WithTemporarySongDb(songDbPath =>
        {
            string root = Path.GetDirectoryName(songDbPath)!;
            string keptBmsPath = Path.Combine(root, "chart.bms");
            string keptBmsonPath = Path.Combine(root, "chart.bmson");
            ChartFile keptBms = ChartTestValues.Empty() with
            {
                Token = new OwnedChartToken(),
                Path = relocate ? Path.Combine(root, "old.bms") : keptBmsPath,
                Favorite = 7,
                Tag = "kept-user-tag",
                AddDate = 12345
            };
            keptBms = keptBms with { Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" };
            ChartFile removedBms = (ChartTestValues.Empty() with { Token = new OwnedChartToken(), Path = Path.Combine(root, removedName + ".bms") });
            removedBms = removedBms with { Md5 = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" };
            ChartFile keptBmson = (ChartTestValues.Empty(ChartFileKind.Bmson) with { Token = new OwnedChartToken() }) with
            {
                Path = relocate ? Path.Combine(root, "old.bmson") : keptBmsonPath,
                Md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                RawTitle = "kept-bmson"
            };
            ChartFile removedBmson = (ChartTestValues.Empty(ChartFileKind.Bmson) with { Token = new OwnedChartToken() }) with
            {
                Path = Path.Combine(root, removedName + ".bmson"),
                Md5 = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB",
                RawTitle = "removed-bmson"
            };
            keptBms = ChartFileProjection.WithMaintenance(keptBms, MaintenanceStorageMapping.ToCommon(new LR2SongDBExtended.maintenance { path = keptBms.Path, hash = keptBms.Md5 }));
            keptBmson = keptBmson with { ResourceHealthMaintenanceSnapshot = MaintenanceStorageMapping.ToCommon(new LR2SongDBExtended.maintenance { path = keptBmson.Path, hash = keptBmson.Md5 }) };
            File.WriteAllText(keptBmsPath, "#PLAYER 1\r\n#TITLE exact\r\n");
            File.WriteAllText(keptBmsonPath, "{}");
            string missingPath = Path.Combine(root, "missing.bms");
            string keptMissingPath = Path.Combine(root, removedName == "other" ? "other-missing.bms" : "MISSING.bms");
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.song>();
                db.CreateTable<LR2SongDBExtended.bmson_song>();
                db.CreateTable<LR2SongDBExtended.maintenance>();
                db.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(keptBms), typeof(LR2SongDB.song));
                db.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(removedBms), typeof(LR2SongDB.song));
                db.InsertOrReplace(ChartSongStorageMapping.ToBmsonRow(keptBmson), typeof(LR2SongDBExtended.bmson_song));
                db.InsertOrReplace(ChartSongStorageMapping.ToBmsonRow(removedBmson), typeof(LR2SongDBExtended.bmson_song));
                foreach (string path in new[] { keptBms.Path, removedBms.Path, keptBmson.Path, removedBmson.Path, missingPath, keptMissingPath })
                {
                    db.InsertOrReplace(new LR2SongDBExtended.maintenance { path = path }, typeof(LR2SongDBExtended.maintenance));
                }
            }

            var storage = new CatalogOwnedCollectionOwner();
            storage.ReplaceChartsAndCaptureSnapshot([keptBms, removedBms], [keptBmson, removedBmson]);
            CatalogOwnedCollectionOwner owned = storage;
            Assert.AreEqual(4, owned.Collection.CreatePathSnapshot().Count, "旧DBの別exact keyをloaded ownerで取り落とさない。");
            var keptPackage = ChartPackage.FromChartEntries(
            [
                PackageChartEntry.FromChart((keptBms)),
                PackageChartEntry.FromChart((keptBmson))
            ]);
            var removedPackage = ChartPackage.FromChartEntries(
            [
                PackageChartEntry.FromChart((removedBms)),
                PackageChartEntry.FromChart((removedBmson))
            ]);
            ObservableCollection<ChartPackage> installed = CreatePackageCollection([keptPackage, removedPackage]);
            PackageChartEntry[] stableEntries = [.. keptPackage.ChartEntries];
            var lifecycle = new PackageLifecycleOwner(new BmsLibraryDbGateway(songDbPath),
                new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher), _ => { },
                packages => new ObservableCollection<ChartPackage>(packages ?? []), () => { }, _ => { });
            lifecycle.SetInstalledPackages(installed);
            try
            {
                var removeRequests = new List<OwnedChartRemoveRequest>();
                removeRequests.Add(ownerReference
                    ? OwnedChartRemoveRequest.FromChart(removedBms)
                    : OwnedChartRemoveRequest.FromPathCleanup(ChartFileKind.Bms, removedBms.Path));
                removeRequests.Add(ownerReference
                    ? OwnedChartRemoveRequest.FromChart(removedBmson)
                    : OwnedChartRemoveRequest.FromPathCleanup(ChartFileKind.Bmson, removedBmson.Path));
                removeRequests.Add(OwnedChartRemoveRequest.FromPathCleanup(ChartFileKind.Bms, missingPath));
                if (removeBoth || relocate)
                {
                    removeRequests.Add(OwnedChartRemoveRequest.FromPathCleanup(ChartFileKind.Bms, keptBmsPath));
                    removeRequests.Add(OwnedChartRemoveRequest.FromPathCleanup(ChartFileKind.Bmson, keptBmsonPath));
                }
                var pathChanges = new List<LibraryChartPathChange>();
                if (relocate)
                {
                    pathChanges.Add(new LibraryChartPathChange
                    {
                        Chart = (keptBms),
                        OldPath = keptBms.Path,
                        NewPath = keptBmsPath
                    });
                    pathChanges.Add(new LibraryChartPathChange
                    {
                        Chart = (keptBmson),
                        OldPath = keptBmson.Path,
                        NewPath = keptBmsonPath
                    });
                }
                LibraryCatalogMutationFacts catalogFacts = new(removeRequests, pathChanges, []);

                CatalogMutationReceipt receipt = new CatalogMutationOwner(storage, new BmsLibraryDbGateway(songDbPath))
                    .ApplyCatalogMutation(catalogFacts);
                lifecycle.ApplyPackageReferenceFacts(
                    LibraryPackageReferenceFacts.Empty,
                    receipt.RemovedCharts,
                    receipt.PathFacts);
                lifecycle.PrepareCommittedChartApplication(storage.BmsRows.Concat(storage.BmsonRows))();
                installed = lifecycle.InstalledPackages;

                string[] expectedBms = removeBoth ? [] : [keptBmsPath];
                string[] expectedBmson = removeBoth ? [] : [keptBmsonPath];
                CollectionAssert.AreEquivalent(expectedBms, storage.BmsRows.Select(row => row.Path).ToArray());
                CollectionAssert.AreEquivalent(expectedBmson, storage.BmsonRows.Select(row => row.Path).ToArray());
                CollectionAssert.AreEquivalent(expectedBms.Concat(expectedBmson).ToArray(), owned.Collection.CreatePathSnapshot());
                CollectionAssert.AreEquivalent(expectedBms.Concat(expectedBmson).ToArray(),
                    installed.SelectMany(package => package.ChartEntries).Select(entry => entry.Chart.Path).ToArray());
                using LR2SongDBExtended readback = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
                CollectionAssert.AreEquivalent(expectedBms, readback.Table<LR2SongDB.song>().Select(row => row.path).ToArray());
                CollectionAssert.AreEquivalent(expectedBmson, readback.Table<LR2SongDBExtended.bmson_song>().Select(row => row.path).ToArray());
                CollectionAssert.AreEquivalent(expectedBms.Concat(expectedBmson).Append(keptMissingPath).ToArray(),
                    readback.Table<LR2SongDBExtended.maintenance>().Select(row => row.path).ToArray());
                if (!removeBoth)
                {
                    CollectionAssert.AreEqual(stableEntries, keptPackage.ChartEntries.ToArray(), "剪定した後も残存entryの同一性を維持します。");
                    Assert.AreSame(keptBms.Token, storage.BmsRows.Single().Token);
                    Assert.AreSame(keptBmson.Token, storage.BmsonRows.Single().Token);
                    Assert.AreEqual(relocate ? Path.Combine(root, "old.bms") : keptBmsPath, keptBms.Path);
                    Assert.AreEqual(relocate ? Path.Combine(root, "old.bmson") : keptBmsonPath, keptBmson.Path);
                    LR2SongDB.song kept = readback.Find<LR2SongDB.song>(keptBmsPath);
                    Assert.AreEqual(7, kept.favorite);
                    Assert.AreEqual(12345, kept.adddate);
                    Assert.AreEqual("kept-user-tag", kept.tag);
                    Assert.AreEqual("kept-bmson", readback.Find<LR2SongDBExtended.bmson_song>(keptBmsonPath).title);
                }
            }
            finally
            {
                lifecycle.ClearInstalledPackages();
            }
        });
    }

    /// <summary>完全一致のpath cleanup factsは該当BMS packageを除き、tokenのない削除事実を見落とす誤りを検出します。</summary>
    /// <remarks>確定factsをpackage参照へ反映する境界です。DB・正本の実保存はExactRemovalとMoveAndRemoveのケースが担います。</remarks>
    [TestMethod]
    public void ApplyPackageReferenceFacts_PathCleanupPrunesInstalledPackagesByPath()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            ChartFile canonicalFile = ChartTestValues.Empty() with
            {
                Token = new OwnedChartToken(),
                Path = "C:\\Library\\remove.bms"
            };
            canonicalFile = canonicalFile with { Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" };
            ChartFile keptFile = ChartTestValues.Empty() with
            {
                Token = new OwnedChartToken(),
                Path = "C:\\Library\\keep.bms"
            };
            keptFile = keptFile with { Md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" };
            ChartPackage removedPackage = ChartPackageTestExtensions.CreatePackage([canonicalFile]);
            removedPackage.path = "C:\\Installed\\RemovePkg";
            removedPackage.delete_parent = false;

            ObservableCollection<ChartPackage> pendingPackages = CreatePackageCollection([]);
            ObservableCollection<ChartPackage> installedPackages = CreatePackageCollection([removedPackage]);
            var callbacks = new TrackingCallbacks();
            PackageStateMutationApplier applier = CreateStateApplier(songDbPath, callbacks, () => pendingPackages, packages => pendingPackages = packages, () => installedPackages, packages => installedPackages = packages);

            LibraryCatalogMutationFacts catalogFacts = new(
                [OwnedChartRemoveRequest.FromPathCleanup(ChartFileKind.Bms, canonicalFile.Path)],
                [],
                []);
            ApplyCommittedMutation(applier, catalogFacts);

            Assert.AreEqual(0, installedPackages.Count);
        });
    }

    /// <summary>移転先保護factsを持つpath cleanupは移転済みpackageを残し、移動先を誤削除して通知する誤りを検出します。</summary>
    /// <remarks>確定factsをpackage参照へ反映する境界です。DB・正本の実保存はExactRemovalとMoveAndRemoveのケースが担います。</remarks>
    [TestMethod]
    public void ApplyPackageReferenceFacts_PathCleanupDoesNotPruneRelocatedDestination()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            ChartFile relocatedFile = ChartTestValues.Empty() with
            {
                Token = new OwnedChartToken(),
                Path = "C:\\Library\\new.bms"
            };
            relocatedFile = relocatedFile with { Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" };
            ChartPackage relocatedPackage = ChartPackageTestExtensions.CreatePackage([relocatedFile]);
            relocatedPackage.path = "C:\\Installed\\RelocatedPkg";
            relocatedPackage.delete_parent = false;

            ObservableCollection<ChartPackage> pendingPackages = CreatePackageCollection([]);
            ObservableCollection<ChartPackage> installedPackages = CreatePackageCollection([relocatedPackage]);
            var callbacks = new TrackingCallbacks();
            PackageStateMutationApplier applier = CreateStateApplier(
                songDbPath,
                callbacks,
                () => pendingPackages,
                packages => pendingPackages = packages,
                () => installedPackages,
                packages => installedPackages = packages);
            LibraryCatalogMutationFacts catalogFacts = new(
                [OwnedChartRemoveRequest.FromPathCleanup(
                    ChartFileKind.Bms,
                    relocatedFile.Path)],
                [],
                []);

            ApplyCommittedMutation(
                applier,
                catalogFacts,
                protectedPathFacts: [new CatalogRelocationPathFact(
                    ChartFileKind.Bms,
                    "C:\\Library\\old.bms",
                    relocatedFile.Path)]);

            Assert.AreEqual(1, installedPackages.Count);
            Assert.AreSame(relocatedPackage, installedPackages.Single());
            Assert.AreEqual(0, callbacks.InstalledPackagesChangedCount);
        });
    }

    /// <summary>混在packageの確定BMS削除では非対象BMSON entryを残し、形式を越えて剪定する誤りを検出します。</summary>
    /// <remarks>確定factsをpackage参照へ反映する境界です。DB・正本の実保存はExactRemovalとMoveAndRemoveのケースが担います。</remarks>
    [TestMethod]
    public void ApplyPackageReferenceFacts_UnregisterBmsRetainsUnmatchedBmsonEntry()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            ChartFile removedFile = ChartTestValues.Empty() with
            {
                Token = new OwnedChartToken(),
                Path = "C:\\Library\\remove.bms"
            };
            removedFile = removedFile with { Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" };
            var unmatchedBmsonEntry = PackageChartEntry.FromChart(((ChartTestValues.Empty(ChartFileKind.Bmson) with { Token = new OwnedChartToken() }) with
            {
                Path = "C:\\Library\\keep.bmson",
                Md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
            }));
            var mixedPackage = ChartPackage.FromChartEntries([PackageChartEntry.FromChart((removedFile)), unmatchedBmsonEntry]);
            mixedPackage.path = "C:\\Installed\\MixedPkg";

            ObservableCollection<ChartPackage> pendingPackages = CreatePackageCollection([]);
            ObservableCollection<ChartPackage> installedPackages = CreatePackageCollection([mixedPackage]);
            var callbacks = new TrackingCallbacks();
            PackageStateMutationApplier applier = CreateStateApplier(songDbPath, callbacks, () => pendingPackages, packages => pendingPackages = packages, () => installedPackages, packages => installedPackages = packages);

            ApplyCommittedMutation(
                applier,
                CreateUnregisterFacts([(removedFile)]));

            Assert.AreEqual(1, installedPackages.Count);
            Assert.AreSame(mixedPackage, installedPackages.Single());
            Assert.AreEqual(1, mixedPackage.ChartEntries.Count);
            Assert.AreEqual(unmatchedBmsonEntry.Chart.Path, mixedPackage.ChartEntries.Single().Chart.Path);
        });
    }

    /// <summary>確定BMSON削除factsは対応packageだけを除いて一回通知し、形式別の剪定漏れを検出します。</summary>
    /// <remarks>確定factsをpackage参照へ反映する境界です。DB・正本の実保存はExactRemovalとMoveAndRemoveのケースが担います。</remarks>
    [TestMethod]
    public void ApplyPackageReferenceFacts_UnregisterPrunesOnlyMatchingBmsonPackage()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            ChartFile removedSong = (ChartTestValues.Empty(ChartFileKind.Bmson) with { Token = new OwnedChartToken() }) with
            {
                Path = "C:\\Library\\remove.bmson",
                Folder = "C:\\Library",
                Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
            };
            ChartFile keptSong = (ChartTestValues.Empty(ChartFileKind.Bmson) with { Token = new OwnedChartToken() }) with
            {
                Path = "C:\\Library\\keep.bmson",
                Folder = "C:\\Library",
                Md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
            };
            var removedPackage = ChartPackage.FromChartEntries([PackageChartEntry.FromChart((removedSong))]);
            removedPackage.path = "C:\\Installed\\RemovePkg";
            removedPackage.delete_parent = false;
            var keptPackage = ChartPackage.FromChartEntries([PackageChartEntry.FromChart((keptSong))]);
            keptPackage.path = "C:\\Installed\\KeepPkg";
            keptPackage.delete_parent = false;

            ObservableCollection<ChartPackage> pendingPackages = CreatePackageCollection([]);
            ObservableCollection<ChartPackage> installedPackages = CreatePackageCollection([removedPackage, keptPackage]);
            var callbacks = new TrackingCallbacks();
            PackageStateMutationApplier applier = CreateStateApplier(songDbPath, callbacks, () => pendingPackages, packages => pendingPackages = packages, () => installedPackages, packages => installedPackages = packages);

            ApplyCommittedMutation(
                applier,
                CreateUnregisterFacts([(removedSong)]));

            Assert.AreEqual(1, installedPackages.Count);
            Assert.AreSame(keptPackage, installedPackages.Single());
            Assert.AreEqual(1, callbacks.InstalledPackagesSetCount);
            Assert.AreEqual(1, callbacks.InstalledPackagesChangedCount);
        });
    }

    /// <summary>一package内のBMSON削除は対象entryだけを剪定して残るentryと通知を保ち、package全体の誤削除を検出します。</summary>
    /// <remarks>確定factsをpackage参照へ反映する境界です。DB・正本の実保存はExactRemovalとMoveAndRemoveのケースが担います。</remarks>
    [TestMethod]
    public void ApplyPackageReferenceFacts_UnregisterPrunesOnlyMatchingBmsonEntry()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            ChartFile removedSong = (ChartTestValues.Empty(ChartFileKind.Bmson) with { Token = new OwnedChartToken() }) with
            {
                Path = "C:\\Library\\remove.bmson",
                Folder = "C:\\Library",
                Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
            };
            ChartFile keptSong = (ChartTestValues.Empty(ChartFileKind.Bmson) with { Token = new OwnedChartToken() }) with
            {
                Path = "C:\\Library\\keep.bmson",
                Folder = "C:\\Library",
                Md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
            };
            var removedEntry = PackageChartEntry.FromChart((removedSong));
            var keptEntry = PackageChartEntry.FromChart((keptSong));
            var package = ChartPackage.FromChartEntries([removedEntry, keptEntry]);
            package.path = "C:\\Installed\\MixedPkg";

            ObservableCollection<ChartPackage> pendingPackages = CreatePackageCollection([]);
            ObservableCollection<ChartPackage> installedPackages = CreatePackageCollection([package]);
            var callbacks = new TrackingCallbacks();
            PackageStateMutationApplier applier = CreateStateApplier(songDbPath, callbacks, () => pendingPackages, packages => pendingPackages = packages, () => installedPackages, packages => installedPackages = packages);

            ApplyCommittedMutation(
                applier,
                CreateUnregisterFacts([(removedSong)]));

            Assert.AreEqual(1, installedPackages.Count);
            Assert.AreSame(package, installedPackages.Single());
            Assert.AreEqual(1, package.ChartEntries.Count);
            Assert.AreEqual(keptSong.Path, package.ChartEntries.Single().Chart.Path);
            Assert.AreEqual(1, callbacks.InstalledPackagesChangedCount);
        });
    }

    /// <summary>同tokenを移転してから削除する実DB変更は両形式の削除・移転factsを返し、移動先保護が対象packageの剪定を妨げる誤りを検出します。</summary>
    /// <remarks>実SQLiteのcatalog確定結果を同じpackage applierへ渡す必要があり、移転と削除の同token接続を観測します。</remarks>
    [TestMethod]
    public void ApplyPackageReferenceFacts_MoveAndRemoveSameOwnerPrunesRelocatedBmsAndBmsonPackages()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_StateApplierMoveRemove_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRootPath);
            string oldBmsPath = Path.Combine(tempRootPath, "old.bms");
            string newBmsPath = Path.Combine(tempRootPath, "new.bms");
            string oldBmsonPath = Path.Combine(tempRootPath, "old.bmson");
            string newBmsonPath = Path.Combine(tempRootPath, "new.bmson");
            try
            {
                File.WriteAllText(oldBmsPath, "#PLAYER 1");
                File.WriteAllText(newBmsPath, "#PLAYER 1");
                File.WriteAllText(oldBmsonPath, "{}");
                File.WriteAllText(newBmsonPath, "{}");
                ChartFile bmsFile = (ChartTestValues.Empty() with { Token = new OwnedChartToken(), Path = oldBmsPath });
                bmsFile = bmsFile with { Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" };
                ChartFile bmsonSong = (ChartTestValues.Empty(ChartFileKind.Bmson) with { Token = new OwnedChartToken() }) with
                {
                    Path = oldBmsonPath,
                    Folder = tempRootPath,
                    Md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                    Sha256 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
                };
                using (var songDb = new LR2SongDBExtended(songDbPath))
                {
                    songDb.CreateTable<LR2SongDB.song>();
                    songDb.CreateTable<LR2SongDBExtended.bmson_song>();
                    songDb.CreateTable<LR2SongDBExtended.maintenance>();
                    songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(bmsFile), typeof(LR2SongDB.song));
                    songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsonRow(bmsonSong), typeof(LR2SongDBExtended.bmson_song));
                }

                ChartPackage bmsPackage = ChartPackageTestExtensions.CreatePackage(bmsFile);
                var bmsonPackage = ChartPackage.FromChartEntries(
                [
                    PackageChartEntry.FromChart((bmsonSong))
                ]);
                ObservableCollection<ChartPackage> pendingPackages = CreatePackageCollection([]);
                ObservableCollection<ChartPackage> installedPackages = CreatePackageCollection([bmsPackage, bmsonPackage]);
                var callbacks = new TrackingCallbacks();
                PackageStateMutationApplier applier = CreateStateApplier(
                    songDbPath,
                    callbacks,
                    () => pendingPackages,
                    packages => pendingPackages = packages,
                    () => installedPackages,
                    packages => installedPackages = packages);

                var pathChanges = new List<LibraryChartPathChange>();
                pathChanges.Add(new LibraryChartPathChange
                {
                    Chart = (bmsFile),
                    OldPath = oldBmsPath,
                    NewPath = newBmsPath
                });
                pathChanges.Add(new LibraryChartPathChange
                {
                    Chart = (bmsonSong),
                    OldPath = oldBmsonPath,
                    NewPath = newBmsonPath
                });
                var removeRequests = new List<OwnedChartRemoveRequest>
                {
                    OwnedChartRemoveRequest.FromChart(bmsFile),
                    OwnedChartRemoveRequest.FromChart(bmsonSong)
                };
                LibraryCatalogMutationFacts catalogFacts = new(removeRequests, pathChanges, []);

                var currentOwner = new CatalogOwnedCollectionOwner();
                currentOwner.ReplaceCharts([bmsFile], [bmsonSong]);
                var catalogOwner = new CatalogMutationOwner(currentOwner, new BmsLibraryDbGateway(songDbPath));
                CatalogMutationReceipt receipt = catalogOwner.ApplyCatalogMutation(
                    catalogFacts);

                Assert.IsTrue(receipt.Applied);
                Assert.AreEqual(2, receipt.RemovedCharts.Count);
                Assert.AreEqual(2, receipt.MovedCharts.Count);
                Assert.AreEqual(
                    bmsonSong.Sha256,
                    receipt.RemovedCharts.Single(fact => fact.Kind == ChartFileKind.Bmson).Sha256);

                applier.ApplyPackageReferenceFacts(
                    LibraryPackageReferenceFacts.Empty,
                    receipt.RemovedCharts,
                    receipt.PathFacts);

                Assert.AreEqual(0, installedPackages.Count);
                Assert.AreEqual(2, callbacks.InstalledPackagesChangedCount);
            }
            finally
            {
                if (Directory.Exists(tempRootPath))
                {
                    Directory.Delete(tempRootPath, recursive: true);
                }
            }
        });
    }

}

internal static class BmsLibraryStateApplierTestSupport
{
    internal static PackageStateMutationApplier CreateStateApplier(
        string songDbPath,
        TrackingCallbacks callbacks,
        Func<ObservableCollection<ChartPackage>> getPendingPackages,
        Action<ObservableCollection<ChartPackage>> setPendingPackages,
        Func<ObservableCollection<ChartPackage>> getInstalledPackages,
        Action<ObservableCollection<ChartPackage>> setInstalledPackages)
    {
        return new PackageStateMutationApplier(
            new BmsLibraryDbGateway(songDbPath),
            getPendingPackages,
            delegate (ObservableCollection<ChartPackage> packages)
            {
                callbacks.PendingPackagesSetCount++;
                setPendingPackages(packages);
            },
            getInstalledPackages,
            delegate (ObservableCollection<ChartPackage> packages)
            {
                callbacks.InstalledPackagesSetCount++;
                setInstalledPackages(packages);
            },
            () =>
            {
                callbacks.InstalledPackagesChangedCount++;
                callbacks.InstalledPackagesChangedThreadId = Environment.CurrentManagedThreadId;
            },
            packages => new ObservableCollection<ChartPackage>(packages),
            new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
            _ => false,
            mutation => mutation());
    }

    /// <summary>実DBと正式な共通現在値の適用を接続し、捕捉済み入力を変更せずに返します。</summary>
    internal static CatalogMutationReceipt ApplyCatalogRelocation(
        string songDbPath,
        LibraryCatalogMutationFacts catalogFacts,
        TrackingCallbacks callbacks)
        => ApplyCatalogRelocation(songDbPath, catalogFacts, callbacks, out _);

    /// <summary>確定後の共通現在値を観測するため、同じ局所カタログ所有者も返します。</summary>
    internal static CatalogMutationReceipt ApplyCatalogRelocation(
        string songDbPath,
        LibraryCatalogMutationFacts catalogFacts,
        TrackingCallbacks callbacks,
        out CatalogOwnedCollectionOwner currentOwner)
    {
        currentOwner = new CatalogOwnedCollectionOwner();
        currentOwner.ReplaceCharts(catalogFacts.ChartPathChanges.Select(change => change.Chart with
        { Path = change.OldPath, Token = change.Chart.Token ?? new OwnedChartToken() }), []);
        var normalizedChanges = new List<LibraryChartPathChange>();
        foreach (LibraryChartPathChange change in catalogFacts.ChartPathChanges)
        {
            ChartFile current = currentOwner.Collection.ResolveCurrentChart(LibraryChartRef.FromPath(
                change.Chart.Kind, change.OldPath, change.Chart.Md5, change.Chart.Sha256));
            normalizedChanges.Add(new LibraryChartPathChange
            { Chart = current, OldPath = change.OldPath, NewPath = change.NewPath });
        }
        var normalizedFacts = new LibraryCatalogMutationFacts(catalogFacts.ChartRemoveRequests, normalizedChanges, catalogFacts.FolderPathChanges);
        var owner = new CatalogMutationOwner(currentOwner, new BmsLibraryDbGateway(songDbPath));
        owner.CatalogWriteFailurePublished += delegate (object? sender, CatalogWriteFailureFact fact)
        {
            callbacks.SongDbWriteFailureCount++;
            callbacks.LastSongDbWriteFailureStage = fact.Stage;
            callbacks.LastSongDbWriteFailure = fact.Exception;
        };
        return owner.ApplyCatalogMutation(normalizedFacts);
    }

    internal static ObservableCollection<ChartPackage> CreatePackageCollection(IEnumerable<ChartPackage> packages)
    {
        return new ObservableCollection<ChartPackage>([.. (packages ?? [])]);
    }

    internal static LibraryCatalogMutationFacts CreateUnregisterFacts(IEnumerable<ChartFile> charts)
    {
        List<OwnedChartRemoveRequest> removeRequests = [.. (charts ?? [])
            .Select(OwnedChartRemoveRequest.FromChart)
            .Where(request => request != null)];
        return new LibraryCatalogMutationFacts(removeRequests, [], []);
    }

    internal static void ApplyCommittedMutation(
        PackageStateMutationApplier applier,
        LibraryCatalogMutationFacts catalogFacts,
        LibraryPackageReferenceFacts? packageReferenceFacts = null,
        IEnumerable<CatalogRelocationPathFact> protectedPathFacts = null!)
    {
        applier.ApplyPackageReferenceFacts(
            packageReferenceFacts ?? LibraryPackageReferenceFacts.Empty,
            CatalogChartMutationFact.CreateRemovalFacts(catalogFacts?.ChartRemoveRequests),
            protectedPathFacts ?? []);
    }

    internal static void WithTemporarySongDb(Action<string> testAction)
    {
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_StateApplierTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        File.WriteAllBytes(songDbPath, []);
        try
        {
            testAction(songDbPath);
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }

    internal sealed class TrackingCallbacks
    {
        public int PendingPackagesSetCount { get; set; }

        public int InstalledPackagesSetCount { get; set; }

        public int InstalledPackagesChangedCount { get; set; }

        public int InstalledPackagesChangedThreadId { get; set; }

        public int SongDbWriteFailureCount { get; set; }

        public string LastSongDbWriteFailureStage { get; set; } = string.Empty;

        public Exception LastSongDbWriteFailure { get; set; } = null!;
    }

    internal sealed class CanceledScheduleUiScheduler : IUiScheduler
    {
        public bool IsAvailable => true;

        public bool CanExecuteInline => false;

        public bool CheckAccess() => false;

        public IUiScheduledOperation Schedule(
            Action action,
            UiSchedulePriority priority = UiSchedulePriority.Normal)
            => CanceledUiScheduledOperation.Instance;

        public void Invoke(Action action, UiSchedulePriority priority = UiSchedulePriority.Normal)
            => throw new NotSupportedException();

        public T Invoke<T>(Func<T> action, UiSchedulePriority priority = UiSchedulePriority.Normal)
            => throw new NotSupportedException();

        public Task InvokeAsync(Action action, UiSchedulePriority priority = UiSchedulePriority.Normal)
            => throw new NotSupportedException();

        public Task InvokeAsync(Func<Task> action, UiSchedulePriority priority = UiSchedulePriority.Normal)
            => throw new NotSupportedException();
    }

    internal sealed class QueuedProgressUiScheduler : IUiScheduler
    {
        private readonly Queue<Action> actions = [];

        internal int PendingCount => actions.Count;

        public bool IsAvailable => true;

        public bool CanExecuteInline => false;

        public bool CheckAccess() => false;

        public IUiScheduledOperation Schedule(
            Action action,
            UiSchedulePriority priority = UiSchedulePriority.Normal)
        {
            actions.Enqueue(action);
            return new CompletedUiScheduledOperation();
        }

        public void Invoke(Action action, UiSchedulePriority priority = UiSchedulePriority.Normal) =>
            action();

        public T Invoke<T>(Func<T> action, UiSchedulePriority priority = UiSchedulePriority.Normal) =>
            action();

        public Task InvokeAsync(Action action, UiSchedulePriority priority = UiSchedulePriority.Normal)
        {
            action();
            return Task.CompletedTask;
        }

        public Task InvokeAsync(Func<Task> action, UiSchedulePriority priority = UiSchedulePriority.Normal) =>
            action();

        internal void ExecuteNext()
        {
            actions.Dequeue()();
        }
    }

    internal sealed class CompletedUiScheduledOperation : IUiScheduledOperation
    {
        public bool IsAccepted => true;

        public bool IsCompleted => true;

        public bool IsAborted => false;

        public string RejectionReason => string.Empty;

        public Task Completion => Task.CompletedTask;

        public void Abort()
        {
        }
    }

    internal sealed class CanceledUiScheduledOperation : IUiScheduledOperation
    {
        private static readonly CancellationToken CanceledToken = new(canceled: true);

        internal static CanceledUiScheduledOperation Instance { get; } = new();

        public bool IsAccepted => true;

        public bool IsCompleted => true;

        public bool IsAborted => true;

        public string RejectionReason => string.Empty;

        public Task Completion { get; } = Task.FromCanceled(CanceledToken);

        public void Abort()
        {
        }
    }

}
