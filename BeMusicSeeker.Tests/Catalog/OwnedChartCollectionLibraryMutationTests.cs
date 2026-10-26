using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using Microsoft.VisualBasic.FileIO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using static BeMusicSeeker.Tests.OwnedChartCollectionTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class OwnedChartCollectionLibraryMutationTests
{
    /// <summary>物理成功scope内の確認済み親子だけを解除し、catalogと登録保存の4終端を独立して回収します。失敗・未処理・scope外の登録を保持します。</summary>
    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void RemoveLibraryCharts_RegistrationAndCatalogFinalizeIndependently(bool failCatalog, bool failRegistration)
    {
        WithTemporarySongDb(songDbPath =>
        {
            string root = Path.GetDirectoryName(songDbPath)!;
            string success = Directory.CreateDirectory(Path.Combine(root, "Success")).FullName;
            string child = Directory.CreateDirectory(Path.Combine(success, "RegisteredChild")).FullName;
            string failed = Directory.CreateDirectory(Path.Combine(root, "Failed")).FullName;
            string missing = Path.Combine(root, "Missing");
            string retained = Directory.CreateDirectory(Path.Combine(root, "Retained")).FullName;
            string configPath = Path.Combine(root, "user.config");
            Settings settings = PortableSettingsPersistenceTests.OpenSettings(configPath);
            string[] registered = [success, child, failed, missing, retained];
            settings.OperationModeLR2DB = false;
            settings.StandaloneBmsRootPaths = StandaloneBmsRootPathSettings.Serialize(registered);
            settings.BMSRootPath = success; settings.BMSInstallDir = retained; settings.Save();
            ChartFile[] charts = new[] { success, failed, missing }.Select((folder, index) =>
                CreateFile(new string((char)('a' + index), 32), Path.Combine(folder, "chart.bms"))).ToArray();
            foreach (ChartFile chart in charts.Take(2)) { File.WriteAllText(chart.Path, "#TITLE Root"); }
            var physicalFailure = new IOException("root-delete-physical-marker");
            var filesystem = new TestFileMutationService
            {
                BeforeDirectoryDelete = path =>
                {
                    if (path != failed) { return; }
                    // APIの返却前に物理削除だけが起きた部分失敗も、事後の不在から成功へ昇格しません。
                    Directory.Delete(path, recursive: true);
                    throw physicalFailure;
                }
            };
            var library = new TestBmsLibrary(songDbPath, null, null, filesystem, settings: settings) { BmsCharts = charts, BmsonCharts = [] };
            try
            {
                using (var db = new LR2SongDBExtended(songDbPath))
                {
                    foreach (ChartFile chart in charts) { db.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(chart), typeof(LR2SongDB.song)); }
                    if (failCatalog) { db.Execute("CREATE TRIGGER root_catalog_failure BEFORE DELETE ON song BEGIN SELECT RAISE(ABORT, 'root-catalog-marker'); END;"); }
                }
                LibraryChartRemovalPreflight prepared = library.PrepareLibraryChartRemoval(charts.Select(chart => LibraryChartRef.FromChartFile(chart)));
                StringAssert.Contains(prepared.GetWholeFolderConfirmationMessage(success), child);
                LibraryChartRemovalOutcome result;
                using (failRegistration ? File.Open(configPath, FileMode.Open, FileAccess.Read, FileShare.Read) : null)
                { result = library.RemoveLibraryCharts(prepared, false, [success, failed, missing]); }
                Assert.AreEqual(1, result.ConfirmedChartCount);
                Assert.IsFalse(Directory.Exists(success));
                Assert.IsFalse(Directory.Exists(failed));
                Assert.AreSame(physicalFailure, result.Targets.Single(target => target.Path == charts[1].Path).Failure);
                Assert.AreEqual(LibraryChartRemovalState.NotExecuted, result.Targets.Single(target => target.Path == charts[2].Path).State);
                Assert.AreEqual(failCatalog, result.CatalogFailure != null);
                Assert.AreEqual(failRegistration, result.RegistrationFailure != null);
                Assert.AreEqual(!failCatalog, result.CatalogDurable);
                CollectionAssert.AreEqual(new[] { success }, result.DeletedFolderPaths.ToArray());
                string[] expected = failRegistration ? registered : [failed, missing, retained];
                Settings fresh = PortableSettingsPersistenceTests.OpenSettings(configPath);
                CollectionAssert.AreEqual(expected, StandaloneBmsRootPathSettings.Deserialize(fresh.StandaloneBmsRootPaths, fresh.BMSRootPath).ToArray());
                CollectionAssert.AreEqual(expected, new BmsRootRegistrationStore(settings, () => null).CaptureUserRoots().ToArray());
                Assert.AreEqual(retained, fresh.BMSInstallDir);
                using LR2SongDBExtended read = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
                Assert.AreEqual(failCatalog ? 3 : 2, read.Table<LR2SongDB.song>().Count());
                if (failCatalog && failRegistration)
                {
                    ReadOnlyCollection<Exception> causes = ((AggregateException)result.RequiredFailure).Flatten().InnerExceptions;
                    Assert.IsTrue(causes.Any(failure => failure.ToString().Contains("root-catalog-marker", StringComparison.Ordinal)));
                    Assert.IsTrue(causes.Any(failure => failure is BeMusicSeeker.Properties.PortableSettingsException));
                }
            }
            finally { library.RequestStop("registered-root-terminal-test"); }
        });
    }

    /// <summary>最後・現在導入先のYesは譜面削除へ読み替えず保護し、現在導入先は登録0でも保護します。混在する独立folderだけを続行し、明示Noは従来の譜面のみ削除を保ちます。</summary>
    [DataTestMethod]
    [DataRow("last")]
    [DataRow("install")]
    [DataRow("install-without-roots")] // モデル境界の登録0条件であり、実UIの稼働profileへの到達は保証しません。
    [DataRow("mixed")]
    [DataRow("no")]
    public void RemoveLibraryCharts_ProtectedWholeScopePreservesRootsAndContinuesIndependentTargets(string condition)
    {
        WithTemporarySongDb(songDbPath =>
        {
            string root = Path.GetDirectoryName(songDbPath)!;
            string first = Directory.CreateDirectory(Path.Combine(root, "Registered")).FullName;
            string second = Directory.CreateDirectory(Path.Combine(root, "Other")).FullName;
            string scope = first;
            string install = first;
            string free = Directory.CreateDirectory(Path.Combine(second, "Free")).FullName;
            string[] roots = condition == "install-without-roots" ? [] : condition == "last" ? [first] : [first, second];
            Settings settings = TestBmsFactory.CreateRootSettings(roots);
            settings.BMSInstallDir = install;
            ChartFile chart = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(scope, "chart.bms"));
            ChartFile independent = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine(free, "chart.bms"));
            File.WriteAllText(chart.Path, "#TITLE Protected"); File.WriteAllText(independent.Path, "#TITLE Independent");
            string resource = Path.Combine(scope, "resource.wav"); File.WriteAllText(resource, "keep");
            ChartFile[] charts = condition == "mixed" ? [chart, independent] : [chart];
            var filesystem = new TestFileMutationService();
            var library = new TestBmsLibrary(songDbPath, null, null, filesystem, settings: settings) { BmsCharts = charts, BmsonCharts = [] };
            try
            {
                using (var db = new LR2SongDBExtended(songDbPath))
                { foreach (ChartFile file in charts) { db.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(file), typeof(LR2SongDB.song)); } }
                LibraryChartRemovalPreflight prepared = library.PrepareLibraryChartRemoval(charts.Select(chart => LibraryChartRef.FromChartFile(chart)));
                LibraryChartRemovalOutcome result = library.RemoveLibraryCharts(prepared, false,
                    condition == "no" ? [] : condition == "mixed" ? [scope, free] : [scope]);
                Assert.AreEqual(condition == "no" || condition == "mixed" ? 1 : 0, result.ConfirmedChartCount);
                Assert.AreEqual(condition != "no", File.Exists(chart.Path));
                Assert.IsTrue(Directory.Exists(scope)); Assert.IsTrue(Directory.Exists(install));
                Assert.IsTrue(File.Exists(resource));
                Assert.AreEqual(condition != "mixed", File.Exists(independent.Path));
                CollectionAssert.AreEqual(roots, new BmsRootRegistrationStore(settings, () => null).CaptureUserRoots().ToArray());
                Assert.AreEqual(install, settings.BMSInstallDir);
                using LR2SongDBExtended read = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
                Assert.AreEqual(condition == "no" ? 0 : 1, read.Table<LR2SongDB.song>().Count());
                if (condition != "no")
                {
                    Assert.IsTrue(result.HasError);
                    Assert.AreEqual(LibraryChartRemovalState.NotExecuted, result.Targets.Single(target => target.Path == chart.Path).State);
                    Assert.IsNotNull(result.Targets.Single(target => target.Path == chart.Path).Failure);
                }
                Assert.IsNull(result.RegistrationFailure);
                Assert.IsFalse(library.OperationAdmission.IsActive);
            }
            finally { library.RequestStop("registered-root-protection-test"); }
        });
    }

    /// <summary>保護祖先Aは直下groupだけ拒否し、明示選択の安全子Freeは自身のYes/Noどおり処理して、祖先・兄弟の管理外資源を保持します。</summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RemoveLibraryCharts_ProtectedAncestorPreservesExplicitChildDecision(bool wholeChild)
    {
        WithTemporarySongDb(songDbPath =>
        {
            string root = Path.GetDirectoryName(songDbPath)!;
            string ancestor = Directory.CreateDirectory(Path.Combine(root, "A")).FullName;
            string free = Directory.CreateDirectory(Path.Combine(ancestor, "Free")).FullName;
            string keep = Directory.CreateDirectory(Path.Combine(ancestor, "Keep")).FullName;
            string retained = Directory.CreateDirectory(Path.Combine(root, "Retained")).FullName;
            ChartFile parent = CreateFile(new string('a', 32), Path.Combine(ancestor, "root.bms"));
            ChartFile child = CreateFile(new string('b', 32), Path.Combine(free, "selected.bms"));
            File.WriteAllText(parent.Path, "parent chart"); File.WriteAllText(child.Path, "child chart");
            File.WriteAllText(Path.Combine(ancestor, "user.dat"), "ancestor resource");
            File.WriteAllText(Path.Combine(keep, "user.dat"), "unselected sibling resource");
            File.WriteAllText(Path.Combine(free, "user.dat"), "child resource");
            string unmanagedChart = Path.Combine(free, "unregistered.bms"); File.WriteAllText(unmanagedChart, "unregistered chart");
            string configPath = Path.Combine(root, "user.config");
            Settings settings = PortableSettingsPersistenceTests.OpenSettings(configPath);
            settings.OperationModeLR2DB = false;
            settings.StandaloneBmsRootPaths = StandaloneBmsRootPathSettings.Serialize([ancestor, retained]);
            settings.BMSRootPath = ancestor; settings.BMSInstallDir = ancestor; settings.Save();
            byte[] savedSettings = File.ReadAllBytes(configPath);
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), settings: settings)
            { BmsCharts = [parent, child], BmsonCharts = [] };
            new BmsLibraryDbGateway(songDbPath).UpsertSongs(library.BmsCharts);
            try
            {
                LibraryChartRemovalPreflight prepared = library.PrepareLibraryChartRemoval(library.BmsCharts.Select(chart => LibraryChartRef.FromChartFile(chart)));
                CollectionAssert.AreEquivalent(new[] { ancestor, free }, prepared.WholeFolderCandidatePaths.ToArray());
                LibraryChartRemovalOutcome result = library.RemoveLibraryCharts(prepared, false, wholeChild ? [ancestor, free] : [ancestor]);
                Assert.AreEqual(1, result.ConfirmedChartCount);
                Assert.AreEqual(LibraryChartRemovalState.NotExecuted, result.Targets.Single(target => target.Path == parent.Path).State);
                Assert.IsNotNull(result.Targets.Single(target => target.Path == parent.Path).Failure);
                Assert.AreEqual(LibraryChartRemovalState.Confirmed, result.Targets.Single(target => target.Path == child.Path).State);
                Assert.IsTrue(result.CatalogDurable);
                Assert.IsNull(result.RegistrationFailure);
                CollectionAssert.AreEqual(wholeChild ? new[] { free } : [], result.DeletedFolderPaths.ToArray());
                Assert.AreEqual("parent chart", File.ReadAllText(parent.Path));
                Assert.AreEqual("ancestor resource", File.ReadAllText(Path.Combine(ancestor, "user.dat")));
                Assert.AreEqual("unselected sibling resource", File.ReadAllText(Path.Combine(keep, "user.dat")));
                Assert.IsFalse(File.Exists(child.Path));
                Assert.AreEqual(!wholeChild, Directory.Exists(free));
                if (!wholeChild)
                {
                    Assert.AreEqual("child resource", File.ReadAllText(Path.Combine(free, "user.dat")));
                    Assert.AreEqual("unregistered chart", File.ReadAllText(unmanagedChart));
                }
                Assert.AreEqual(parent.Path, library.BmsCharts.Single().Path);
                using LR2SongDBExtended read = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
                Assert.AreEqual(parent.Path, read.Table<LR2SongDB.song>().Single().path);
                CollectionAssert.AreEqual(new[] { ancestor, retained }, new BmsRootRegistrationStore(settings, () => null).CaptureUserRoots().ToArray());
                CollectionAssert.AreEqual(savedSettings, File.ReadAllBytes(configPath));
                Settings fresh = PortableSettingsPersistenceTests.OpenSettings(configPath);
                CollectionAssert.AreEqual(new[] { ancestor, retained }, StandaloneBmsRootPathSettings.Deserialize(fresh.StandaloneBmsRootPaths).ToArray());
                Assert.AreEqual(ancestor, fresh.BMSInstallDir);
            }
            finally { library.RequestShutdown("protected-ancestor-explicit-child-test"); }
        });
    }

    /// <summary>空の保護rootを含む祖先と危険子の両groupを拒否し、安全な子だけを同じ一要求で削除して、旧祖先保護の全観測を維持します。</summary>
    [TestMethod]
    public void RemoveLibraryCharts_EmptyProtectedChildPreservesContainingGroupsAndContinuesFree()
    {
        WithTemporarySongDb(songDbPath =>
        {
            string root = Path.GetDirectoryName(songDbPath)!;
            string outer = Directory.CreateDirectory(Path.Combine(root, "Outer")).FullName;
            string middle = Directory.CreateDirectory(Path.Combine(outer, "Middle")).FullName;
            string guarded = Directory.CreateDirectory(Path.Combine(middle, "Guarded")).FullName;
            string install = Directory.CreateDirectory(Path.Combine(guarded, "EmptyInstall")).FullName;
            string free = Directory.CreateDirectory(Path.Combine(middle, "Free")).FullName;
            string keep = Directory.CreateDirectory(Path.Combine(middle, "Keep")).FullName;
            string retained = Directory.CreateDirectory(Path.Combine(root, "Retained")).FullName;
            ChartFile[] charts = [CreateFile(new string('a', 32), Path.Combine(middle, "parent.bms")),
                CreateFile(new string('b', 32), Path.Combine(guarded, "guarded.bms")),
                CreateFile(new string('c', 32), Path.Combine(free, "child.bms"))];
            foreach (ChartFile chart in charts)
            {
                File.WriteAllText(chart.Path, chart.Md5);
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(chart.Path)!, "user.dat"), chart.Md5);
            }
            File.WriteAllText(Path.Combine(keep, "user.dat"), "unselected sibling");
            string configPath = Path.Combine(root, "user.config");
            Settings settings = PortableSettingsPersistenceTests.OpenSettings(configPath);
            settings.OperationModeLR2DB = false;
            string[] roots = [outer, install, retained];
            settings.StandaloneBmsRootPaths = StandaloneBmsRootPathSettings.Serialize(roots);
            settings.BMSRootPath = outer; settings.BMSInstallDir = install; settings.Save();
            byte[] savedSettings = File.ReadAllBytes(configPath);
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), settings: settings)
            { BmsCharts = charts, BmsonCharts = [] };
            new BmsLibraryDbGateway(songDbPath).UpsertSongs(library.BmsCharts);
            try
            {
                LibraryChartRemovalPreflight prepared = library.PrepareLibraryChartRemoval(library.BmsCharts.Select(chart => LibraryChartRef.FromChartFile(chart)));
                CollectionAssert.AreEquivalent(new[] { middle, guarded, free }, prepared.WholeFolderCandidatePaths.ToArray());
                LibraryChartRemovalOutcome result = library.RemoveLibraryCharts(prepared, false, [middle, guarded, free]);
                Assert.AreEqual(1, result.ConfirmedChartCount);
                foreach (ChartFile kept in charts.Take(2))
                {
                    Assert.AreEqual(kept.Md5, File.ReadAllText(kept.Path));
                    Assert.AreEqual(kept.Md5, File.ReadAllText(Path.Combine(Path.GetDirectoryName(kept.Path)!, "user.dat")));
                    LibraryChartRemovalTarget rejected = result.Targets.Single(target => target.Path == kept.Path);
                    Assert.AreEqual(LibraryChartRemovalState.NotExecuted, rejected.State);
                    Assert.IsNotNull(rejected.Failure);
                }
                Assert.IsTrue(Directory.Exists(install));
                Assert.AreEqual(0, Directory.GetFileSystemEntries(install).Length);
                Assert.IsFalse(Directory.Exists(free));
                Assert.AreEqual("unselected sibling", File.ReadAllText(Path.Combine(keep, "user.dat")));
                Assert.AreEqual(LibraryChartRemovalState.Confirmed, result.Targets.Single(target => target.Path == charts[2].Path).State);
                CollectionAssert.AreEqual(new[] { free }, result.DeletedFolderPaths.ToArray());
                Assert.IsTrue(result.CatalogDurable);
                Assert.IsNull(result.RegistrationFailure);
                string[] expectedPaths = charts.Take(2).Select(chart => chart.Path).ToArray();
                CollectionAssert.AreEquivalent(expectedPaths, library.BmsCharts.Select(chart => chart.Path).ToArray());
                using LR2SongDBExtended read = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
                CollectionAssert.AreEquivalent(expectedPaths, read.Table<LR2SongDB.song>().Select(row => row.path).ToArray());
                CollectionAssert.AreEqual(roots, new BmsRootRegistrationStore(settings, () => null).CaptureUserRoots().ToArray());
                CollectionAssert.AreEqual(savedSettings, File.ReadAllBytes(configPath));
                Settings fresh = PortableSettingsPersistenceTests.OpenSettings(configPath);
                CollectionAssert.AreEqual(roots, StandaloneBmsRootPathSettings.Deserialize(fresh.StandaloneBmsRootPaths).ToArray());
                Assert.AreEqual(install, fresh.BMSInstallDir);
            }
            finally { library.RequestShutdown("empty-protected-child-test"); }
        });
    }

    /// <summary>登録rootの物理削除成功を実媒体の登録解除へ接続し、独立した導入先と残存rootを保持します。</summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RemoveLibraryCharts_WholeRegisteredRootUnregistersOnlyAfterPhysicalSuccess(bool lr2)
    {
        WithTemporarySongDb(songDbPath =>
        {
            string root = Path.GetDirectoryName(songDbPath)!;
            string removed = Directory.CreateDirectory(Path.Combine(root, "Removed")).FullName;
            string retained = Directory.CreateDirectory(Path.Combine(root, "Retained")).FullName;
            string configPath = Path.Combine(root, "user.config");
            BeMusicSeeker.Properties.Settings settings = PortableSettingsPersistenceTests.OpenSettings(configPath);
            settings.OperationModeLR2DB = false;
            settings.StandaloneBmsRootPaths = StandaloneBmsRootPathSettings.Serialize([removed, retained]);
            settings.BMSRootPath = removed;
            settings.BMSInstallDir = retained;
            string managed = Directory.CreateDirectory(Path.Combine(root, "ManagedOutput")).FullName;
            if (lr2)
            {
                settings.OperationModeLR2DB = true;
                settings.LR2RootPath = Path.Combine(root, "LR2");
                settings.LR2CustomFolderOutputBaseDir = managed;
                settings.LR2ConfigXmlPath = BmsPlaylistTestSupport.CreateLr2Config(settings.LR2RootPath, removed, retained, managed).ConfigFilePath;
            }
            settings.Save();
            string chartPath = Path.Combine(removed, "chart.bms");
            File.WriteAllText(chartPath, "#TITLE Removed");
            ChartFile chart = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", chartPath);
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), settings: settings)
            { BmsCharts = [chart], BmsonCharts = [] };
            try
            {
                using (var db = new LR2SongDBExtended(songDbPath))
                { db.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(chart), typeof(LR2SongDB.song)); }
                LibraryChartRemovalOutcome result = library.RemoveLibraryCharts(
                    library.PrepareLibraryChartRemoval([LibraryChartRef.FromChartFile(chart)]), false, [removed]);
                Assert.AreEqual(1, result.ConfirmedChartCount);
                Assert.IsFalse(Directory.Exists(removed));
                Settings fresh = PortableSettingsPersistenceTests.OpenSettings(configPath);
                if (lr2)
                {
                    CollectionAssert.AreEquivalent(new[] { retained, managed }, new LR2Config(settings.LR2ConfigXmlPath).GetBMSSearchDirectoriesForChangeTracking().ToArray());
                    CollectionAssert.AreEqual(new[] { retained }, new BmsRootRegistrationStore(settings, () => library.FixtureLr2Owner).CaptureUserRoots().ToArray());
                    Assert.IsTrue(Directory.Exists(managed));
                }
                else { CollectionAssert.AreEqual(new[] { retained }, StandaloneBmsRootPathSettings.Deserialize(fresh.StandaloneBmsRootPaths, fresh.BMSRootPath).ToArray()); }
                Assert.AreEqual(retained, fresh.BMSInstallDir);
            }
            finally { library.RequestStop("registered-root-test"); }
        });
    }

    /// <summary>
    /// 永久削除・ごみ箱指定の両条件で、確認済みフォルダだけを物理削除・保存確定し、一回のリソース世代公開から失敗対象を除く。
    /// </summary>
    /// <remarks>実ライブラリの変更入口を所持カタログへ接続し、入口で対象を解決してから反映する境界を観測するため実構成を使う。</remarks>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RemoveLibraryCharts_PublishesOneResourceGenerationForConfirmedFoldersOnly(bool recycle)
    {

        WithTemporarySongDb(songDbPath =>
        {
            string root = Path.GetDirectoryName(songDbPath)!;
            string[] folders = [Path.Combine(root, "A"), Path.Combine(root, "B"), Path.Combine(root, "C")];
            var files = new List<ChartFile>();
            for (int i = 0; i < folders.Length; i++)
            {
                Directory.CreateDirectory(folders[i]);
                string path = Path.Combine(folders[i], "chart.bms");
                File.WriteAllText(path, "#PLAYER 1\r\n#TITLE Removal\r\n#BPM 120\r\n");
                File.WriteAllText(Path.Combine(folders[i], "shared.wav"), "scan-only resource");
                files.Add(CreateFile(new string((char)('a' + i), 32), path));
            }
            var deletionFailure = new IOException("B deletion failed before changing files");
            var filesystem = new TestFileMutationService
            {
                BeforeDirectoryDelete = path =>
                {
                    if (path == folders[1])
                    {
                        throw deletionFailure;
                    }
                }
            };
            var library = new TestBmsLibrary(songDbPath, null, null, filesystem,
                new FileDbReportRecordingDialogs(), new TestUiScheduler(() => null!),
                () => new BmsLibraryOptionsSnapshot { OperationModeLR2DB = false })
            {
                BmsCharts = files,
                BmsonCharts = []
            };
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                foreach (ChartFile file in files)
                {
                    db.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(file), typeof(LR2SongDB.song));
                }
            }
            uint shared = ChartResourceKeyHash.GetLookupHash("shared");
            LibraryResourceIndexOwner owner = LibraryResourceIndexTestSupport.GetOwner(library);
            owner.Replace(LibraryResourceIndex.CreateFromNativeCanonicalArrays(
                folders,
                folders.Select(_ => new[] { shared }).ToArray(),
                [[], [], []], [[], [], []],
                folders.Select(_ => new[] { shared }).ToArray(),
                [[], [], []], [[], [], []],
                new Dictionary<uint, string[]> { [shared] = folders.ToArray() }, new Dictionary<uint, string[]>(), new Dictionary<uint, string[]>()));
            LibraryResourceIndexSnapshot before = owner.CaptureSnapshot();
            string pendingDirectory = Path.Combine(root, "pending");
            Directory.CreateDirectory(pendingDirectory);
            string pendingPath = Path.Combine(pendingDirectory, "pending.bms");
            File.WriteAllText(pendingPath, "#TITLE Pending\n#WAV01 shared.wav\n#00111:01\n");
            var pending = PackageChartEntry.FromPath(pendingPath);
            BmsLibraryPackageInstallService.InitializePendingResourceHealth(pending);
            var package = ChartPackage.FromChartEntries([pending]);
            package.path = pendingDirectory;
            library.ChartPackagesPending = new[] { package };
            pending.SetInstallDestinationPathOnly(folders[0]);
            BmsLibraryPackageInstallService.RefreshPendingResourceHealth([pending], package, before.DirectoryLookupCache);
            Assert.AreEqual(100, pending.Chart.WAVHealth);
            File.WriteAllText(Path.Combine(pendingDirectory, "shared.wav"), "added after registration");
            var entryMutations = new List<string>();
            before.DirectoryLookupCache.EntryStoreMutationObserver = path => entryMutations.Add(path);

            LibraryChartRemovalOutcome result = library.RemoveLibraryCharts(
                library.PrepareLibraryChartRemoval(files.Select(chart => LibraryChartRef.FromChartFile((chart)))), recycle, folders);

            Assert.AreEqual(2, result.ConfirmedChartCount);
            Assert.IsTrue(result.CatalogDurable);
            Assert.IsNotNull(result.SessionReceipt);
            Assert.AreEqual(2, result.SessionReceipt.ResourceDirectoryRemovalCount);
            Assert.AreSame(deletionFailure, result.Targets.Single(target => target.Path == files[1].Path).Failure);
            Assert.IsFalse(Directory.Exists(folders[0]));
            Assert.IsTrue(Directory.Exists(folders[1]));
            Assert.IsFalse(Directory.Exists(folders[2]));
            Assert.IsTrue(File.Exists(Path.Combine(folders[1], "shared.wav")));
            Assert.AreEqual(string.Empty, pending.Chart.InstallDestination);
            Assert.AreEqual(0, pending.Chart.WAVHealth);
            Assert.IsTrue(pending.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
            Assert.AreEqual(ChartResourcePlacementBasis.Current, pending.ResourcePlacement.Basis);
            Assert.IsTrue(filesystem.DirectoryRecycleOptions.All(option => option ==
                (recycle ? RecycleOption.SendToRecycleBin : RecycleOption.DeletePermanently)));
            LibraryResourceIndexSnapshot after = owner.CaptureSnapshot();
            Assert.AreEqual(before.Generation + 1, after.Generation);
            CollectionAssert.AreEqual(new[] { folders[0], folders[2] }, entryMutations);
            CollectionAssert.AreEqual(new[] { folders[1] }, after.DirectoryLookupCache.GetDirectoriesByAudioRelativeHash(shared).ToArray());
            CollectionAssert.AreEqual(folders, before.DirectoryLookupCache.GetDirectoriesByAudioRelativeHash(shared).ToArray());
            using LR2SongDBExtended readback = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            CollectionAssert.AreEqual(new[] { files[1].Path }, readback.Table<LR2SongDB.song>().Select(row => row.path).ToArray());
        });
    }

    /// <summary>
    /// DB確定前の削除失敗と確定後の必須最終化失敗を分け、物理削除済み対象の確認状態・永続結果・未公開リソース索引を保持する。
    /// </summary>
    /// <remarks>実ライブラリの変更入口を所持カタログへ接続し、入口で対象を解決してから反映する境界を観測するため実構成を使う。</remarks>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RemoveLibraryCharts_CatalogFailureReturnsAfterConfirmedFilesystemDeletion(bool afterCommit)
    {

        WithTemporarySongDb(songDbPath =>
        {
            string root = Path.GetDirectoryName(songDbPath)!;
            string folder = Path.Combine(root, "Pack");
            Directory.CreateDirectory(folder);
            string chartPath = Path.Combine(folder, "delete.bms");
            File.WriteAllText(chartPath, "#PLAYER 1");
            ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", chartPath);
            string lr2Root = Path.Combine(root, "LR2");
            LR2Config config = BmsPlaylistTestSupport.CreateLr2Config(lr2Root, root);
            var configOwner = new BeMusicSeeker.Models.LR2.Lr2ConfigOwner(config);
            var filesystem = new TestFileMutationService();
            var library = new TestBmsLibrary(songDbPath, configOwner, null, filesystem, null,
                new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                () => new BmsLibraryOptionsSnapshot { OperationModeLR2DB = afterCommit, LR2RootPath = lr2Root })
            {
                BmsCharts = [file],
                BmsonCharts = []
            };
            string folderRowPath = Lr2FolderPath.ToFolderPath(folder);
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(file), typeof(LR2SongDB.song));
                db.InsertOrReplace(new LR2SongDB.folder { path = folderRowPath, title = "Pack", type = 1 }, typeof(LR2SongDB.folder));
                db.Execute(afterCommit
                    ? "CREATE TRIGGER fail_delete BEFORE DELETE ON folder BEGIN SELECT RAISE(ABORT, 'required-folder-prune-fault'); END;"
                    : "CREATE TRIGGER fail_delete BEFORE DELETE ON song BEGIN SELECT RAISE(ABORT, 'catalog-delete-fault'); END;");
            }
            uint shared = ChartResourceKeyHash.GetLookupHash("shared");
            LibraryResourceIndexOwner resourceOwner = LibraryResourceIndexTestSupport.GetOwner(library);
            resourceOwner.Replace(LibraryResourceIndex.CreateFromNativeCanonicalArrays(
                [folder],
                [new[] { shared }],
                [[]], [[]],
                [new[] { shared }],
                [[]], [[]],
                new Dictionary<uint, string[]> { [shared] = [folder] },
                new Dictionary<uint, string[]>(),
                new Dictionary<uint, string[]>()));
            LibraryResourceIndexSnapshot resourceBefore = resourceOwner.CaptureSnapshot();

            LibraryChartRemovalOutcome outcome = library.RemoveLibraryCharts(
                library.PrepareLibraryChartRemoval([LibraryChartRef.FromChartFile((file))]),
                false,
                [folder]);

            Assert.IsFalse(File.Exists(chartPath));
            Assert.IsFalse(Directory.Exists(folder));
            Assert.AreEqual(0, filesystem.FileDeleteCalls);
            Assert.AreEqual(1, filesystem.DirectoryDeleteCalls);
            Assert.AreEqual(1, outcome.ConfirmedChartCount);
            Assert.AreEqual(chartPath, outcome.Targets.Single().Path);
            Assert.IsTrue(outcome.CatalogApplyAttempted);
            Assert.AreEqual(afterCommit, outcome.CatalogDurable);
            Assert.AreEqual(afterCommit, outcome.RequiredFinalizationFailed);
            Assert.IsNotNull(outcome.CatalogFailure);
            Assert.IsNotNull(outcome.SessionReceipt);
            Assert.AreEqual(1, outcome.SessionReceipt.ResourceDirectoryRemovalCount);
            LibraryResourceIndexSnapshot resourceAfter = resourceOwner.CaptureSnapshot();
            Assert.AreEqual(resourceBefore.Generation, resourceAfter.Generation);
            CollectionAssert.AreEqual(
                new[] { folder },
                resourceAfter.DirectoryLookupCache.GetDirectoriesByAudioRelativeHash(shared).ToArray());
            using LR2SongDBExtended readback = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(afterCommit ? 0 : 1, readback.Table<LR2SongDB.song>().Count());
            Assert.AreEqual(1, readback.Table<LR2SongDB.folder>().Count(row => row.path == folderRowPath));
        });
    }

    /// <summary>
    /// 削除成功・元ファイル欠落・変更後のAPI失敗を区別し、成功した対象だけを確定して未確認対象のDB行を保持する。
    /// </summary>
    /// <remarks>実ライブラリの変更入口を所持カタログへ接続し、入口で対象を解決してから反映する境界を観測するため実構成を使う。</remarks>
    [TestMethod]
    public void RemoveLibraryCharts_OnlySuccessfulApiTargetsAreConfirmed()
    {

        WithTemporarySongDb(songDbPath =>
        {
            string root = Path.GetDirectoryName(songDbPath)!;
            string partialDirectory = Path.Combine(root, "Partial");
            Directory.CreateDirectory(partialDirectory);
            string success = Path.Combine(root, "success.bms");
            string missing = Path.Combine(root, "missing.bms");
            string partial = Path.Combine(partialDirectory, "partial.bms");
            File.WriteAllText(success, "#PLAYER 1");
            File.WriteAllText(partial, "#PLAYER 1");
            ChartFile[] files = new[] { CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", success),
                CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", missing),
                CreateFile("cccccccccccccccccccccccccccccccc", partial) };
            var partialFailure = new IOException("directory changed before failure");
            var filesystem = new TestFileMutationService
            {
                BeforeDirectoryDelete = path =>
            {
                Assert.AreEqual(partialDirectory, path);
                File.Delete(partial);
                throw partialFailure;
            }
            };
            var dialogs = new FileDbReportRecordingDialogs();
            var library = new TestBmsLibrary(songDbPath, null, null, filesystem, dialogs)
            { BmsCharts = files, BmsonCharts = [] };
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                foreach (ChartFile? file in files)
                {
                    db.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(file), typeof(LR2SongDB.song));
                }
            }

            LibraryChartRemovalOutcome outcome = library.RemoveLibraryCharts(
                library.PrepareLibraryChartRemoval(files.Select(chart => LibraryChartRef.FromChartFile((chart)))), false, [partialDirectory]);

            Assert.AreEqual(1, outcome.ConfirmedChartCount);
            Assert.AreEqual(LibraryChartRemovalState.Confirmed, outcome.Targets.Single(target => target.Path == success).State);
            Assert.AreEqual(LibraryChartRemovalState.NotExecuted, outcome.Targets.Single(target => target.Path == missing).State);
            Assert.AreEqual(LibraryChartRemovalState.Unconfirmed, outcome.Targets.Single(target => target.Path == partial).State);
            Assert.AreSame(partialFailure, outcome.Targets.Single(target => target.Path == partial).Failure);
            Assert.IsFalse(File.Exists(success));
            Assert.IsFalse(File.Exists(partial));
            Assert.AreEqual(1, filesystem.FileDeleteCalls);
            Assert.AreEqual(1, filesystem.DirectoryDeleteCalls);
            Assert.AreEqual(0, dialogs.ModelMessages);
            using LR2SongDBExtended readback = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            CollectionAssert.AreEquivalent(new[] { missing, partial }, readback.Table<LR2SongDB.song>().Select(row => row.path).ToArray());
        });
    }

    /// <summary>
    /// 子の成功・フォルダ失敗・欠落・譜面だけの削除失敗に応じて承認済み親の再帰削除を制限し、失敗対象とリソースを保つ。
    /// </summary>
    /// <remarks>実ライブラリの変更入口を所持カタログへ接続し、入口で対象を解決してから反映する境界を観測するため実構成を使う。</remarks>
    [DataTestMethod]
    [DataRow(0, false)] // 子の全体削除成功、永久削除。
    [DataRow(1, true)] // 子フォルダの削除失敗、ごみ箱指定。
    [DataRow(2, false)] // 子フォルダが既に欠落、永久削除。
    [DataRow(3, true)] // 子の全体削除を拒否し個別削除が失敗、ごみ箱指定。
    public void RemoveLibraryCharts_ParentDeletionDependsOnObservedChildResult(int childState, bool recycle)
    {

        WithTemporarySongDb(songDbPath =>
        {
            string root = Path.GetDirectoryName(songDbPath)!;
            string parent = Path.Combine(root, "Song");
            string child = Path.Combine(parent, "Child");
            string independent = Path.Combine(root, "SongOther");
            string sibling = Path.Combine(parent, "Sibling");
            Directory.CreateDirectory(sibling);
            Directory.CreateDirectory(child);
            Directory.CreateDirectory(independent);
            string parentChartPath = Path.Combine(parent, "parent.bms");
            string childChartPath = Path.Combine(child, "child.bmson");
            string independentChartPath = Path.Combine(independent, "independent.bms");
            string siblingChartPath = Path.Combine(sibling, "sibling.bms");
            string parentResource = Path.Combine(parent, "parent.wav");
            string childResource = Path.Combine(child, "child.wav");
            File.WriteAllText(parentChartPath, "#PLAYER 1");
            File.WriteAllText(childChartPath, "{}");
            File.WriteAllText(independentChartPath, "#PLAYER 1");
            File.WriteAllText(siblingChartPath, "#PLAYER 1");
            File.WriteAllText(parentResource, "parent resource");
            File.WriteAllText(childResource, "child resource");
            ChartFile parentChart = CreateFile(new string('a', 32), parentChartPath);
            ChartFile independentChart = CreateFile(new string('c', 32), independentChartPath);
            ChartFile siblingChart = CreateFile(new string('d', 32), siblingChartPath);
            ChartFile childChart = ChartTestValues.Empty(ChartFileKind.Bmson) with
            {
                Token = new OwnedChartToken(),
                Path = childChartPath,
                Folder = child,
                Md5 = new string('b', 32),
                Sha256 = new string('b', 64)
            };
            var childFailure = new IOException("selected child deletion failed");
            var filesystem = new TestFileMutationService
            {
                BeforeDirectoryDelete = path =>
                {
                    if (childState == 1 && path == child)
                    {
                        throw childFailure;
                    }
                },
                BeforeFileDelete = path =>
                {
                    if (childState == 3 && path == childChartPath)
                    {
                        throw childFailure;
                    }
                }
            };
            var library = new TestBmsLibrary(songDbPath, null, null, filesystem, new FileDbReportRecordingDialogs())
            {
                BmsCharts = [parentChart, independentChart, siblingChart],
                BmsonCharts = [childChart]
            };
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(parentChart), typeof(LR2SongDB.song));
                db.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(independentChart), typeof(LR2SongDB.song));
                db.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(siblingChart), typeof(LR2SongDB.song));
                db.InsertOrReplace(ChartSongStorageMapping.ToBmsonRow(childChart), typeof(LR2SongDBExtended.bmson_song));
            }
            if (childState == 2)
            {
                Directory.Delete(child, recursive: true);
            }

            LibraryChartRef[] selected = [LibraryChartRef.FromChartFile((parentChart)),
                LibraryChartRef.FromChartFile((childChart)), LibraryChartRef.FromChartFile((independentChart)),
                LibraryChartRef.FromChartFile((siblingChart))];
            var approvedFolders = library.PrepareLibraryChartRemoval(selected).WholeFolderCandidatePaths.ToList();
            CollectionAssert.AreEquivalent(new[] { parent, child, independent, sibling }, approvedFolders);
            if (childState == 3)
            {
                approvedFolders.Remove(child); // 利用者はこの子の譜面だけを削除する。
            }

            LibraryChartRemovalOutcome outcome = library.RemoveLibraryCharts(library.PrepareLibraryChartRemoval(selected), recycle, approvedFolders);

            Assert.IsNotNull(outcome);
            Assert.IsTrue(outcome.CatalogDurable);
            Assert.IsNull(outcome.CatalogFailure);
            Assert.AreEqual(childState == 0 ? 4 : 3, outcome.ConfirmedChartCount);
            Assert.AreEqual(childState != 0, outcome.HasError);
            Assert.IsFalse(File.Exists(parentChartPath));
            Assert.IsFalse(Directory.Exists(independent));
            Assert.IsFalse(Directory.Exists(sibling));
            Assert.AreEqual(childState != 0, Directory.Exists(parent));
            Assert.AreEqual(childState != 0, File.Exists(parentResource));
            Assert.AreEqual(childState == 1 || childState == 3, File.Exists(childChartPath));
            Assert.AreEqual(childState == 1 || childState == 3, File.Exists(childResource));
            Assert.AreEqual(childState == 0, filesystem.DirectoryDeletePaths.Contains(parent));
            Assert.IsTrue(filesystem.DirectoryDeletePaths.Contains(independent));
            Assert.IsTrue(filesystem.DirectoryRecycleOptions.All(option => option ==
                (recycle ? RecycleOption.SendToRecycleBin : RecycleOption.DeletePermanently)));
            LibraryChartRemovalTarget childResult = outcome.Targets.Single(target => target.Path == childChartPath);
            Assert.AreEqual(childState == 0 ? LibraryChartRemovalState.Confirmed
                : childState == 2 ? LibraryChartRemovalState.NotExecuted : LibraryChartRemovalState.Unconfirmed, childResult.State);
            if (childState == 1 || childState == 3)
            {
                Assert.AreSame(childFailure, childResult.Failure);
            }

            Assert.AreEqual(0, library.BmsCharts.Count);
            Assert.AreEqual(childState == 0 ? 0 : 1, library.BmsonCharts.Count);
            using LR2SongDBExtended readback = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(0, readback.Table<LR2SongDB.song>().Count());
            string[] retained = readback.Table<LR2SongDBExtended.bmson_song>().Select(row => row.path).ToArray();
            CollectionAssert.AreEqual(childState == 0 ? Array.Empty<string>() : new[] { childChartPath }, retained);
            using LibraryFileMutationLease probe = library.TryBeginLibraryFileMutation("deletion_completion_probe");
            Assert.IsNotNull(probe);
        });
    }

    /// <summary>
    /// 未選択の子孫がある親は全体削除の候補にせず、選択譜面だけを削除して子孫の実ファイル・リソース・DB行を保持する。
    /// </summary>
    /// <remarks>実ライブラリの変更入口を所持カタログへ接続し、入口で対象を解決してから反映する境界を観測するため実構成を使う。</remarks>
    [TestMethod]
    public void RemoveLibraryCharts_UnselectedDescendantKeepsResourcesAndRows()
    {

        WithTemporarySongDb(songDbPath =>
        {
            string folder = Path.Combine(Path.GetDirectoryName(songDbPath)!, "Pack");
            string child = Path.Combine(folder, "Child");
            Directory.CreateDirectory(child);
            ChartFile selected = CreateFile(new string('a', 32), Path.Combine(folder, "selected.bms"));
            ChartFile kept = CreateFile(new string('b', 32), Path.Combine(child, "kept.bms"));
            File.WriteAllText(selected.Path, "#PLAYER 1");
            File.WriteAllText(kept.Path, "#PLAYER 1");
            string resource = Path.Combine(folder, "sound.wav");
            File.WriteAllText(resource, "keep");
            var filesystem = new TestFileMutationService();
            var library = new TestBmsLibrary(songDbPath, null, null, filesystem, new FileDbReportRecordingDialogs())
            { BmsCharts = [selected, kept], BmsonCharts = [] };
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                foreach (ChartFile? file in new[] { selected, kept })
                {
                    db.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(file), typeof(LR2SongDB.song));
                }
            }

            Assert.AreEqual(0, library.PrepareLibraryChartRemoval([LibraryChartRef.FromChartFile((selected))]).WholeFolderCandidatePaths.ToList().Count);

            LibraryChartRemovalOutcome outcome = library.RemoveLibraryCharts(library.PrepareLibraryChartRemoval([LibraryChartRef.FromChartFile((selected))]), false, []);

            Assert.IsFalse(outcome.HasError);
            Assert.AreEqual(1, outcome.ConfirmedChartCount);
            Assert.AreEqual(0, filesystem.DirectoryDeleteCalls);
            Assert.IsFalse(File.Exists(selected.Path));
            Assert.IsTrue(File.Exists(kept.Path));
            Assert.AreEqual("keep", File.ReadAllText(resource));
            Assert.AreSame(kept, library.BmsCharts.Single());
            using LR2SongDBExtended readback = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(kept.Path, readback.Table<LR2SongDB.song>().Single().path);
        });
    }

    /// <summary>
    /// パスだけの選択と同tokenの捕捉値を本番の現在所有へ解決し、指定の削除方針で物理フォルダとDB行を除く。
    /// </summary>
    /// <remarks>実ライブラリの変更入口を所持カタログへ接続し、入口で対象を解決してから反映する境界を観測するため実構成を使う。</remarks>
    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void RemoveLibraryCharts_ResolvesSelectionThroughProductionOwner(bool pathOnly)
    {

        WithTemporarySongDb(songDbPath =>
        {
            string folder = Path.Combine(Path.GetDirectoryName(songDbPath)!, "Pack");
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "chart.bms");
            File.WriteAllText(path, "#PLAYER 1");
            ChartFile canonical = CreateFile(new string('a', 32), path);
            var filesystem = new TestFileMutationService();
            var library = new TestBmsLibrary(songDbPath, null, null, filesystem, new FileDbReportRecordingDialogs())
            { BmsCharts = [canonical], BmsonCharts = [] };
            ChartFile owned = library.BmsCharts.Single();
            LibraryChartRef selected = pathOnly
                ? LibraryChartRef.FromPath(ChartFileKind.Bms, path, owned.Md5, owned.Sha256)
                : LibraryChartRef.FromChartFile(owned with { Title = "captured selection" });
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(canonical), typeof(LR2SongDB.song));
            }

            LibraryChartRemovalOutcome outcome = library.RemoveLibraryCharts(library.PrepareLibraryChartRemoval([selected]), pathOnly, [folder]);

            Assert.IsFalse(outcome.HasError);
            Assert.AreEqual(1, outcome.ConfirmedChartCount);
            Assert.AreEqual(path, outcome.Targets.Single().Path);
            Assert.IsFalse(Directory.Exists(folder));
            Assert.AreEqual(pathOnly ? RecycleOption.SendToRecycleBin : RecycleOption.DeletePermanently,
                filesystem.DirectoryRecycleOptions.Single());
            Assert.AreEqual(0, library.BmsCharts.Count);
            using LR2SongDBExtended readback = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(0, readback.Table<LR2SongDB.song>().Count());
        });
    }

    /// <summary>
    /// 大小文字だけ違う完全一致行を同時選択したとき、一回の物理フォルダ削除で両方のカタログ対象を確定削除する。
    /// </summary>
    /// <remarks>実ライブラリの変更入口を所持カタログへ接続し、入口で対象を解決してから反映する境界を観測するため実構成を使う。</remarks>
    [TestMethod]
    public void RemoveLibraryCharts_CaseOnlyExactRowsAreBothRemoved()
    {

        WithTemporarySongDb(songDbPath =>
        {
            string folder = Path.Combine(Path.GetDirectoryName(songDbPath)!, "Pack");
            Directory.CreateDirectory(folder);
            string lowerPath = Path.Combine(folder, "chart.bms");
            string upperPath = Path.Combine(folder, "CHART.bms");
            File.WriteAllText(lowerPath, "#PLAYER 1");
            ChartFile lower = CreateFile(new string('a', 32), lowerPath);
            ChartFile upper = CreateFile(new string('b', 32), upperPath);
            var filesystem = new TestFileMutationService();
            var library = new TestBmsLibrary(songDbPath, null, null, filesystem, new FileDbReportRecordingDialogs())
            {
                BmsCharts = [lower, upper],
                BmsonCharts = []
            };
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(lower), typeof(LR2SongDB.song));
                db.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(upper), typeof(LR2SongDB.song));
            }

            LibraryChartRemovalOutcome outcome = library.RemoveLibraryCharts(
                library.PrepareLibraryChartRemoval([LibraryChartRef.FromChartFile((lower)), LibraryChartRef.FromChartFile((upper))]),
                false,
                [folder]);

            Assert.IsFalse(outcome.HasError);
            Assert.AreEqual(2, outcome.ConfirmedChartCount);
            CollectionAssert.AreEquivalent(
                new[] { lowerPath, upperPath },
                outcome.Targets.Where(target => target.State == LibraryChartRemovalState.Confirmed)
                    .Select(target => target.Path)
                    .ToArray());
            Assert.AreEqual(1, filesystem.DirectoryDeleteCalls);
            Assert.IsFalse(Directory.Exists(folder));
            Assert.AreEqual(0, library.BmsCharts.Count);
            using LR2SongDBExtended readback = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(0, readback.Table<LR2SongDB.song>().Count());
        });
    }

    /// <summary>
    /// 大小文字またはドット別表記で同じ物理譜面を指す選択を個別削除するとき、物理削除は一回だけ行い両完全一致行を除く。
    /// </summary>
    /// <remarks>実ライブラリの変更入口を所持カタログへ接続し、入口で対象を解決してから反映する境界を観測するため実構成を使う。</remarks>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RemoveLibraryCharts_PhysicalAliasExactRowsAreBothRemovedWithoutWholeFolderDelete(bool useDotAlias)
    {

        WithTemporarySongDb(songDbPath =>
        {
            string folder = Path.Combine(Path.GetDirectoryName(songDbPath)!, "Pack");
            Directory.CreateDirectory(folder);
            string canonicalPath = Path.Combine(folder, "chart.bms");
            string aliasPath = useDotAlias
                ? Path.Combine(folder, ".", "chart.bms")
                : Path.Combine(folder, "CHART.bms");
            File.WriteAllText(canonicalPath, "#PLAYER 1");
            ChartFile canonical = CreateFile(new string('a', 32), canonicalPath);
            ChartFile alias = CreateFile(new string('b', 32), aliasPath);
            var filesystem = new TestFileMutationService();
            var library = new TestBmsLibrary(songDbPath, null, null, filesystem, new FileDbReportRecordingDialogs())
            {
                BmsCharts = [canonical, alias],
                BmsonCharts = []
            };
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(canonical), typeof(LR2SongDB.song));
                db.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(alias), typeof(LR2SongDB.song));
            }

            LibraryChartRemovalOutcome outcome = library.RemoveLibraryCharts(
                library.PrepareLibraryChartRemoval([LibraryChartRef.FromChartFile((canonical)), LibraryChartRef.FromChartFile((alias))]),
                false,
                []);

            Assert.IsFalse(outcome.HasError);
            Assert.AreEqual(2, outcome.ConfirmedChartCount);
            CollectionAssert.AreEquivalent(
                new[] { canonicalPath, aliasPath },
                outcome.Targets.Where(target => target.State == LibraryChartRemovalState.Confirmed)
                    .Select(target => target.Path)
                    .ToArray());
            Assert.AreEqual(1, filesystem.FileDeleteCalls);
            Assert.AreEqual(0, filesystem.DirectoryDeleteCalls);
            Assert.IsFalse(File.Exists(canonicalPath));
            Assert.AreEqual(0, library.BmsCharts.Count);
            using LR2SongDBExtended readback = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(0, readback.Table<LR2SongDB.song>().Count());
        });
    }

    /// <summary>
    /// 同じ物理譜面の個別削除が失敗しても別表記で自動再試行せず、両行と元例外を未確認結果として保持する。
    /// </summary>
    /// <remarks>実ライブラリの変更入口を所持カタログへ接続し、入口で対象を解決してから反映する境界を観測するため実構成を使う。</remarks>
    [TestMethod]
    public void RemoveLibraryCharts_PhysicalAliasDeleteFailureIsNotRetried()
    {

        WithTemporarySongDb(songDbPath =>
        {
            string folder = Path.Combine(Path.GetDirectoryName(songDbPath)!, "Pack");
            Directory.CreateDirectory(folder);
            string canonicalPath = Path.Combine(folder, "chart.bms");
            string aliasPath = Path.Combine(folder, ".", "chart.bms");
            File.WriteAllText(canonicalPath, "#PLAYER 1");
            ChartFile canonical = CreateFile(new string('a', 32), canonicalPath);
            ChartFile alias = CreateFile(new string('b', 32), aliasPath);
            var failure = new IOException("selected physical file deletion failed");
            var filesystem = new TestFileMutationService
            {
                BeforeFileDelete = _ => throw failure
            };
            var library = new TestBmsLibrary(songDbPath, null, null, filesystem, new FileDbReportRecordingDialogs())
            {
                BmsCharts = [canonical, alias],
                BmsonCharts = []
            };
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(canonical), typeof(LR2SongDB.song));
                db.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(alias), typeof(LR2SongDB.song));
            }

            LibraryChartRemovalOutcome outcome = library.RemoveLibraryCharts(
                library.PrepareLibraryChartRemoval([LibraryChartRef.FromChartFile((canonical)), LibraryChartRef.FromChartFile((alias))]),
                false,
                []);

            Assert.IsTrue(outcome.HasError);
            Assert.AreEqual(0, outcome.ConfirmedChartCount);
            Assert.AreEqual(1, filesystem.FileDeleteCalls);
            Assert.IsTrue(File.Exists(canonicalPath));
            Assert.AreEqual(2, library.BmsCharts.Count);
            Assert.IsTrue(outcome.Targets.All(target => target.State == LibraryChartRemovalState.Unconfirmed));
            Assert.IsTrue(outcome.Targets.All(target => ReferenceEquals(failure, target.Failure)));
            using LR2SongDBExtended readback = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            CollectionAssert.AreEquivalent(
                new[] { canonicalPath, aliasPath },
                readback.Table<LR2SongDB.song>().Select(row => row.path).ToArray());
        });
    }

    /// <summary>
    /// 親と大小文字だけ違う子の完全一致行をすべて選択したとき、選択を畳まず子と親の全体削除候補を返す。
    /// </summary>
    /// <remarks>実ライブラリの所持集合を参照する確認path入口を使い、物理操作前の選択解決を確認する。</remarks>
    [TestMethod]
    public void GetLibraryWholeFolderDeleteConfirmationPaths_PreservesCaseOnlyNestedSelections()
    {

        WithTemporarySongDb(songDbPath =>
        {
            string parent = Path.Combine(Path.GetDirectoryName(songDbPath)!, "Pack");
            string child = Path.Combine(parent, "Child");
            Directory.CreateDirectory(child);
            string parentPath = Path.Combine(parent, "parent.bms");
            string lowerPath = Path.Combine(child, "chart.bms");
            string upperPath = Path.Combine(child, "CHART.bms");
            File.WriteAllText(parentPath, "#PLAYER 1");
            File.WriteAllText(lowerPath, "#PLAYER 1");
            ChartFile parentChart = CreateFile(new string('a', 32), parentPath);
            ChartFile lower = CreateFile(new string('b', 32), lowerPath);
            ChartFile upper = CreateFile(new string('c', 32), upperPath);
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(),
                new FileDbReportRecordingDialogs())
            {
                BmsCharts = [parentChart, lower, upper],
                BmsonCharts = []
            };

            var confirmationPaths = library.PrepareLibraryChartRemoval(
                [LibraryChartRef.FromChartFile((parentChart)), LibraryChartRef.FromChartFile((lower)), LibraryChartRef.FromChartFile((upper))]).WholeFolderCandidatePaths.ToList();

            CollectionAssert.AreEquivalent(new[] { child, parent }, confirmationPaths);
        });
    }

    /// <summary>
    /// 未登録・同hash別パス・パス喪失・未登録のドットと大小文字別表記は解決不能とし、実ファイルとDB行を削除しない。
    /// </summary>
    /// <remarks>実ライブラリの変更入口を所持カタログへ接続し、入口で対象を解決してから反映する境界を観測するため実構成を使う。</remarks>
    [DataTestMethod]
    [DataRow(0)] // 未登録の別パス。
    [DataRow(1)] // 同じhashだが未登録の別パス。
    [DataRow(2)] // 捕捉後にパスを失った所有。
    [DataRow(3)] // 未登録のドット別表記。
    [DataRow(4)] // 未登録の大小文字別表記。
    public void RemoveLibraryCharts_UnresolvedSelectionKeepsFilesystemAndDatabase(int selectionKind)
    {

        WithTemporarySongDb(songDbPath =>
        {
            string folder = Path.Combine(Path.GetDirectoryName(songDbPath)!, "Pack");
            Directory.CreateDirectory(folder);
            string catalogPath = Path.Combine(folder, "catalog.bms");
            string selectedPath = selectionKind switch
            {
                2 => catalogPath,
                3 => Path.Combine(folder, ".", "catalog.bms"),
                4 => Path.Combine(folder, "CATALOG.bms"),
                _ => Path.Combine(folder, "outside-catalog.bms")
            };
            File.WriteAllText(catalogPath, "#PLAYER 1");
            File.WriteAllText(selectedPath, "#PLAYER 1");
            ChartFile canonical = CreateFile(new string('a', 32), catalogPath);
            LibraryChartRef selected = selectionKind == 2
                ? LibraryChartRef.FromChartFile((canonical))
                : LibraryChartRef.FromPath(ChartFileKind.Bms, selectedPath,
                    selectionKind is 1 or 3 or 4 ? canonical.Md5 : null, null);
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(canonical), typeof(LR2SongDB.song));
            }

            if (selectionKind == 2)
            {
                canonical = canonical with { Path = null };
            }

            var filesystem = new TestFileMutationService();
            var library = new TestBmsLibrary(songDbPath, null, null, filesystem, new FileDbReportRecordingDialogs())
            { BmsCharts = [canonical], BmsonCharts = [] };

            LibraryChartRemovalOutcome outcome = library.RemoveLibraryCharts(library.PrepareLibraryChartRemoval([selected]), false, [folder]);

            Assert.AreEqual(0, outcome.ConfirmedChartCount);
            Assert.IsTrue(outcome.HasError);
            Assert.AreEqual(LibraryChartRemovalState.Unresolved, outcome.Targets.Single().State);
            Assert.AreEqual(selectedPath, outcome.Targets.Single().Path);
            Assert.AreEqual(0, filesystem.FileDeleteCalls);
            Assert.AreEqual(0, filesystem.DirectoryDeleteCalls);
            Assert.IsTrue(File.Exists(catalogPath));
            Assert.IsTrue(File.Exists(selectedPath));
            using LR2SongDBExtended readback = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(catalogPath, readback.Table<LR2SongDB.song>().Single().path);
        });
    }

    /// <summary>
    /// 古いhashの選択は現在値へ解決する一方、移転・形式不一致・退役token・大小文字の別パスを救済せず物理処理を拒否する。
    /// </summary>
    /// <remarks>実ライブラリの変更入口を所持カタログへ接続し、入口で対象を解決してから反映する境界を観測するため実構成を使う。</remarks>
    [DataTestMethod]
    [DataRow(0)] // hashだけが更新済み。
    [DataRow(1)] // 実ファイルと現在値が移転済み。
    [DataRow(2)] // 同tokenでも選択の種別が不一致。
    [DataRow(3)] // 同パスへ置換され選択tokenが退役済み。
    [DataRow(4)] // 同tokenでも選択パスの大小文字が不一致。
    public void RemoveLibraryCharts_PreflightChecksCapturedKindAndExactPathButAllowsOldHash(int change)
    {

        WithTemporarySongDb(songDbPath =>
        {
            string folder = Path.Combine(Path.GetDirectoryName(songDbPath) ?? throw new InvalidOperationException(), "Pack");
            Directory.CreateDirectory(folder);
            string originalPath = Path.Combine(folder, "chart.bms");
            File.WriteAllText(originalPath, "#PLAYER 1");
            File.WriteAllText(Path.Combine(folder, "resource.wav"), "retain folder");
            var filesystem = new TestFileMutationService();
            var dialogs = new RecordingRemovalDialogs();
            var library = new TestBmsLibrary(songDbPath, null, null, filesystem, dialogs)
            { BmsCharts = [CreateFile(new string('a', 32), originalPath)], BmsonCharts = [] };
            try
            {
                ChartFile captured = library.BmsCharts.Single();
                var selected = LibraryChartRef.FromChartFile(captured);
                CatalogOwnedCollectionOwner owner = GetOwnedCollectionOwner(library);
                using (owner.WriteGate.GetWriterGuard())
                {
                    lock (owner.Gate)
                    {
                        if (change == 3)
                        {
                            owner.Collection.UpsertCharts([captured]);
                        }
                        else if (change != 2 && change != 4)
                        {
                            string currentPath = change == 1 ? Path.Combine(folder, "moved.bms") : originalPath;
                            if (change == 1)
                            {
                                File.Move(originalPath, currentPath);
                            }
                            ApplyCapturedCurrentValues(owner.Collection, captured with { Path = currentPath, Md5 = new string('b', 32) });
                        }
                    }
                }
                if (change == 2)
                {
                    selected = LibraryChartRef.FromChartFile(ChartTestValues.Empty(ChartFileKind.Bmson) with
                    { Token = captured.Token, Path = captured.Path, Md5 = captured.Md5 });
                }
                if (change == 4)
                {
                    selected = LibraryChartRef.FromChartFile(captured with { Path = captured.Path.ToUpperInvariant() });
                }
                ChartFile current = library.BmsCharts.Single();
                new BmsLibraryDbGateway(songDbPath).UpsertSongs([current]);
                Assert.AreEqual(change == 0 ? 1 : 0, library.PrepareLibraryChartRemoval([selected]).WholeFolderCandidatePaths.ToList().Count);
                LibraryChartRemovalOutcome outcome = library.RemoveLibraryCharts(library.PrepareLibraryChartRemoval([selected]), false, []);
                Assert.AreEqual(0, dialogs.CallCount);
                Assert.AreEqual(change == 0 ? 1 : 0, outcome.ConfirmedChartCount);
                Assert.AreEqual(0, filesystem.DirectoryDeleteCalls);
                Assert.AreEqual(change == 0 ? 1 : 0, filesystem.FileDeleteCalls);
                Assert.AreEqual(change != 0, File.Exists(current.Path));
                Assert.AreEqual(new string('a', 32), captured.Md5);
                Assert.AreEqual(change == 4 ? originalPath.ToUpperInvariant() : originalPath, selected.Path);
                using LR2SongDBExtended readback = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
                if (change == 0)
                {
                    Assert.AreEqual(0, readback.Table<LR2SongDB.song>().Count());
                    Assert.AreEqual(0, library.BmsCharts.Count);
                }
                else
                {
                    Assert.AreEqual(LibraryChartRemovalState.Unresolved, outcome.Targets.Single().State);
                    LR2SongDB.song row = readback.Table<LR2SongDB.song>().Single();
                    Assert.AreEqual(current.Path, row.path);
                    Assert.AreEqual(current.Md5, row.hash);
                    Assert.AreSame(current.Token, library.BmsCharts.Single().Token);
                }
            }
            finally
            {
                library.RequestShutdown("removal-preflight-test");
            }
        });
    }

    /// <summary>
    /// 準備済み要求の後方対象に退役tokenがあれば全対象を拒否し、先行する削除・拡張子変更・DB・索引・通知を開始しない。
    /// </summary>
    /// <remarks>実ライブラリの変更入口を所持カタログへ接続し、入口で対象を解決してから反映する境界を観測するため実構成を使う。</remarks>
    [TestMethod]
    public void PreparedRequests_RejectRetiredTargetBeforeAnyPhysicalOrCatalogMutation()
    {

        WithTemporarySongDb(songDbPath =>
        {
            string folder = Path.Combine(Path.GetDirectoryName(songDbPath) ?? throw new InvalidOperationException(), "Prepared");
            Directory.CreateDirectory(folder);
            string firstPath = Path.Combine(folder, "first.bms");
            string lastPath = Path.Combine(folder, "last.pms");
            File.WriteAllText(firstPath, "#PLAYER 1");
            File.WriteAllText(lastPath, "#PLAYER 1");
            var filesystem = new TestFileMutationService();
            var library = new TestBmsLibrary(songDbPath, null, null, filesystem, new FileDbReportRecordingDialogs())
            { BmsCharts = [CreateFile(new string('a', 32), firstPath), CreateFile(new string('b', 32), lastPath)], BmsonCharts = [] };
            try
            {
                ChartFile first = library.BmsCharts.Single(chart => chart.Path == firstPath);
                ChartFile retired = library.BmsCharts.Single(chart => chart.Path == lastPath);
                CatalogOwnedCollectionOwner owner = GetOwnedCollectionOwner(library);
                using (owner.WriteGate.GetWriterGuard())
                {
                    lock (owner.Gate) { owner.Collection.UpsertCharts([retired]); }
                }
                ChartFile[] before = library.BmsCharts.ToArray();
                ChartFile replacement = before.Single(chart => chart.Path == lastPath);
                Assert.AreNotSame(retired.Token, replacement.Token);
                Assert.AreEqual(retired.Md5, replacement.Md5);
                new BmsLibraryDbGateway(songDbPath).UpsertSongs(before);
                int version = owner.CatalogRevision;
                int notifications = library.NormalLibraryRefreshNotificationVersion;
                OwnedChartHashIndexSnapshot hashes = owner.Collection.CreateOwnedHashIndexSnapshot();
                var prepared = new LibraryChartRemovalPreflight([first, retired], [], [], 2, 0);

                ArgumentException deleteFailure = Assert.ThrowsException<ArgumentException>(() => library.RemoveLibraryCharts(prepared, false, []));
                ArgumentException renameFailure = Assert.ThrowsException<ArgumentException>(() => library.RenameBMSFilesExtensionsWithReceipt(
                    [new LibraryFileExtensionRenameBatch([first], ".bmx"), new LibraryFileExtensionRenameBatch([retired], ".pmx")]));

                StringAssert.Contains(deleteFailure.Message, BeMusicSeeker.Properties.Resources.Error_PreparedChartTargetMismatch);
                StringAssert.Contains(renameFailure.Message, BeMusicSeeker.Properties.Resources.Error_PreparedChartTargetMismatch);
                StringAssert.Contains(deleteFailure.Message, lastPath);
                StringAssert.Contains(renameFailure.Message, lastPath);
                Assert.AreEqual("targets", deleteFailure.ParamName);
                Assert.AreEqual("targets", renameFailure.ParamName);
                Assert.AreEqual(0, filesystem.FileDeleteCalls);
                Assert.AreEqual(0, filesystem.DirectoryDeleteCalls);
                Assert.AreEqual(0, filesystem.FileMoveCalls);
                Assert.IsTrue(File.Exists(firstPath));
                Assert.IsTrue(File.Exists(lastPath));
                AssertChartSnapshotParity(before, library.BmsCharts);
                Assert.AreEqual(version, owner.CatalogRevision);
                Assert.AreEqual(notifications, library.NormalLibraryRefreshNotificationVersion);
                CollectionAssert.AreEquivalent(hashes.Md5Hashes.ToArray(), owner.Collection.CreateOwnedHashIndexSnapshot().Md5Hashes.ToArray());
                using LR2SongDBExtended readback = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
                CollectionAssert.AreEquivalent(before.Select(chart => chart.Path + ":" + chart.Md5).ToArray(),
                    readback.Table<LR2SongDB.song>().Select(row => row.path + ":" + row.hash).ToArray());
                using LibraryFileMutationLease probe = library.TryBeginLibraryFileMutation("prepared_failure_released", showMessage: false);
                Assert.IsNotNull(probe);
            }
            finally { library.RequestShutdown("prepared_binding_test"); }
        });
    }

    /// <summary>
    /// 古いdigestで準備された拡張子変更でも捕捉した元パスから移転し、現在の両hashを実DB・現在値・索引に保存する。
    /// </summary>
    /// <remarks>実ライブラリの変更入口を所持カタログへ接続し、入口で対象を解決してから反映する境界を観測するため実構成を使う。</remarks>
    [TestMethod]
    public void PreparedRename_UsesCurrentDigestAndFixedSourceWithoutHashHistoryComparison()
    {

        WithTemporarySongDb(songDbPath =>
        {
            string folder = Path.Combine(Path.GetDirectoryName(songDbPath) ?? throw new InvalidOperationException(), "PreparedDigest");
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "chart.bms");
            File.WriteAllText(path, "#PLAYER 1");
            var filesystem = new TestFileMutationService();
            var library = new TestBmsLibrary(songDbPath, null, null, filesystem, new FileDbReportRecordingDialogs())
            { BmsCharts = [CreateFile(new string('b', 32), path) with { Sha256 = new string('c', 64) }], BmsonCharts = [] };
            try
            {
                ChartFile current = library.BmsCharts.Single();
                new BmsLibraryDbGateway(songDbPath).UpsertSongs([current]);
                ChartFile oldDigest = current with { Md5 = new string('a', 32), Sha256 = new string('d', 64) };
                var prepared = new LibraryFileExtensionRenameBatch([oldDigest], ".bmx");

                LibraryMutationSessionReceipt receipt = library.RenameBMSFilesExtensionsWithReceipt([prepared], unregister: false);

                Assert.IsTrue(receipt.DurableCommit);
                Assert.AreEqual(1, receipt.ConfirmedChangeCount);
                Assert.AreEqual(path, filesystem.FileMoveSourcePaths.Single());
                Assert.AreEqual(1, filesystem.FileMoveCalls);
                Assert.AreEqual(0, filesystem.FileDeleteCalls);
                string destination = Path.ChangeExtension(path, ".bmx");
                Assert.IsFalse(File.Exists(path));
                Assert.IsTrue(File.Exists(destination));
                ChartFile moved = library.BmsCharts.Single();
                Assert.AreSame(current.Token, moved.Token);
                Assert.AreEqual(destination, moved.Path);
                Assert.AreEqual(current.Md5, moved.Md5);
                Assert.AreEqual(current.Sha256, moved.Sha256);
                Assert.AreEqual(new string('a', 32), prepared.Targets.Single().Md5);
                OwnedChartHashIndexSnapshot hashes = GetOwnedCollectionOwner(library).Collection.CreateOwnedHashIndexSnapshot();
                Assert.IsTrue(hashes.Md5Hashes.Contains(current.Md5, StringComparer.OrdinalIgnoreCase));
                Assert.IsTrue(hashes.Sha256Hashes.Contains(current.Sha256, StringComparer.OrdinalIgnoreCase));
                Assert.IsFalse(hashes.Md5Hashes.Contains(oldDigest.Md5, StringComparer.OrdinalIgnoreCase));
                Assert.IsFalse(hashes.Sha256Hashes.Contains(oldDigest.Sha256, StringComparer.OrdinalIgnoreCase));
                using LR2SongDBExtended readback = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
                LR2SongDB.song row = readback.Table<LR2SongDB.song>().Single();
                Assert.AreEqual(destination, row.path);
                Assert.AreEqual(current.Md5, row.hash);
                using LibraryFileMutationLease probe = library.TryBeginLibraryFileMutation("prepared_digest_released", showMessage: false);
                Assert.IsNotNull(probe);
            }
            finally { library.RequestShutdown("prepared_digest_test"); }
        });
    }

    /// <summary>
    /// 最後の譜面削除で全体削除の確認を一度行い、拒否ならリソースを残し承認ならフォルダごと削除してDBから譜面を除く。
    /// </summary>
    /// <remarks>実ライブラリの変更入口を所持カタログへ接続し、入口で対象を解決してから反映する境界を観測するため実構成を使う。</remarks>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RemoveLibraryCharts_LastChartHonorsExplicitWholeFolderDecision(bool deleteWholeFolder)
    {

        WithTemporarySongDb(songDbPath =>
        {
            string folder = Path.Combine(Path.GetDirectoryName(songDbPath)!, "Pack");
            Directory.CreateDirectory(folder);
            ChartFile chart = CreateFile(new string('a', 32), Path.Combine(folder, "chart.bms"));
            File.WriteAllText(chart.Path, "#PLAYER 1");
            File.WriteAllText(Path.Combine(folder, "resource.wav"), "remove with the folder");
            var filesystem = new TestFileMutationService();
            var dialogs = new RecordingRemovalDialogs();
            var library = new TestBmsLibrary(songDbPath, null, null, filesystem, dialogs)
            { BmsCharts = [chart], BmsonCharts = [] };
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(chart), typeof(LR2SongDB.song));
            }

            CollectionAssert.AreEqual(new[] { folder }, library.PrepareLibraryChartRemoval([LibraryChartRef.FromChartFile((chart))]).WholeFolderCandidatePaths.ToList());

            LibraryChartRemovalOutcome outcome = library.RemoveLibraryCharts(library.PrepareLibraryChartRemoval([LibraryChartRef.FromChartFile(chart)]), false, deleteWholeFolder ? [folder] : []);

            Assert.AreEqual(0, dialogs.CallCount);
            Assert.AreEqual(1, outcome.ConfirmedChartCount);
            Assert.IsFalse(outcome.HasError);
            Assert.AreEqual(deleteWholeFolder ? 1 : 0, filesystem.DirectoryDeleteCalls);
            Assert.AreEqual(!deleteWholeFolder, Directory.Exists(folder));
            Assert.AreEqual(!deleteWholeFolder, File.Exists(Path.Combine(folder, "resource.wav")));
            Assert.AreEqual(0, library.BmsCharts.Count);
            using LR2SongDBExtended readback = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(0, readback.Table<LR2SongDB.song>().Count());
        });
    }

    /// <summary>
    /// 実削除後に所持集合を再初期化せず残る所有へ同期し、親cache・重複cacheの失効と削除tokenを一度通知する。
    /// </summary>
    /// <remarks>実ライブラリの変更入口を所持カタログへ接続し、入口で対象を解決してから反映する境界を観測するため実構成を使う。</remarks>
    [TestMethod]
    public void RemoveLibraryCharts_UnregisterKeepsOwnedCollectionInitializedAndSynced()
    {

        WithTemporarySongDb(delegate (string songDbPath)
        {
            string rootPath = Path.Combine(Path.GetDirectoryName(songDbPath)!, "Installed");
            string firstDirectoryPath = Path.Combine(rootPath, "First");
            string secondDirectoryPath = Path.Combine(rootPath, "Second");
            Directory.CreateDirectory(firstDirectoryPath);
            Directory.CreateDirectory(secondDirectoryPath);
            string firstPath = Path.Combine(firstDirectoryPath, "chart.bms");
            string secondPath = Path.Combine(secondDirectoryPath, "chart.bms");
            File.WriteAllText(firstPath, "#PLAYER 1");
            File.WriteAllText(secondPath, "#PLAYER 1");
            ChartFile first = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", firstPath);
            ChartFile second = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", secondPath);
            var library = new TestBmsLibrary(songDbPath);
            using var initialBmsFilesNotification = new ManualResetEventSlim(false);
            System.ComponentModel.PropertyChangedEventHandler initialHandler = delegate (object? _, System.ComponentModel.PropertyChangedEventArgs args)
            {
                if (args.PropertyName == nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion))
                {
                    initialBmsFilesNotification.Set();
                }
            };
            library.PropertyChanged += initialHandler;
            library.BmsCharts = [first, second];
            library.BmsonCharts = [];
            library.DuplicateChartGroups = [];
            // 初期通知が終わってから解除後の通知を観測し、workerの実時間遅延は判定しない。
            initialBmsFilesNotification.Wait();
            library.PropertyChanged -= initialHandler;
            List<ChartFile> initialSnapshot = InvokeCreateOwnedChartInfoFullBackfillTargetSnapshot(library);
            Assert.AreEqual(2, initialSnapshot.Count);
            int baselineParentFolderVersion = library.BMSParentFolderListCacheVersion;
            int baselineDuplicateInvalidationVersion = library.DuplicateChartGroupsInvalidationVersion;
            int handledNotificationVersion = library.NormalLibraryRefreshNotificationVersion;
            int parentFolderVersionChanged = 0;
            int bmsFilesChanged = 0;
            library.PropertyChanged += delegate (object? _, System.ComponentModel.PropertyChangedEventArgs args)
            {
                if (args.PropertyName == "BMSParentFolderListCacheVersion")
                {
                    parentFolderVersionChanged++;
                }
                if (args.PropertyName == nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion))
                {
                    bmsFilesChanged++;
                }
            };
            LibraryChartRemovalOutcome removal = library.RemoveLibraryCharts(
                library.PrepareLibraryChartRemoval([LibraryChartRef.FromChartFile(initialSnapshot[0])]),
                sendToRecycleBin: false,
                approvedWholeFolderDeletePaths: []);
            Assert.IsFalse(removal.HasError);
            NormalLibraryRefreshNotificationBatch batch = library.GetNormalLibraryRefreshNotificationsAfter(handledNotificationVersion);

            Assert.AreEqual(baselineParentFolderVersion + 1, library.BMSParentFolderListCacheVersion);
            Assert.AreEqual(1, parentFolderVersionChanged);
            Assert.AreEqual(1, bmsFilesChanged);
            Assert.IsTrue((batch.ChangedCharts.Count > 0 || batch.DeletedTokens.Count > 0 || batch.HasEffect(LibraryChartRefreshEffects.SourceChanged)));
            Assert.IsTrue(batch.DeletedTokens.Contains(first.Token));
            Assert.IsFalse(batch.ChangedCharts.Any(chart => chart.Kind == ChartFileKind.Bmson));
            Assert.IsNull(library.DuplicateChartGroups);
            Assert.AreEqual(baselineDuplicateInvalidationVersion + 1, library.DuplicateChartGroupsInvalidationVersion);
            Assert.AreEqual(1, library.BmsCharts.Count);
            Assert.AreSame(second, library.BmsCharts[0]);
            List<ChartFile> afterSnapshot = InvokeCreateOwnedChartInfoFullBackfillTargetSnapshot(library);
            Assert.AreEqual(1, afterSnapshot.Count);
            Assert.AreSame(second.Token, afterSnapshot[0].Token);
        });
    }

    /// <summary>
    /// 実ライブラリのBMS集合setterで置換した後、次の一覧取得に古い所有を残さず新tokenを反映する。
    /// </summary>
    /// <remarks>実ライブラリのBMSFiles交換を所持集合のversionと接続し、次回viewでの再構築を確認する。</remarks>
    [TestMethod]
    public void BMSFilesReplacement_InvalidatesCatalogRevisionAndRebuildsOnNextView()
    {

        WithTemporarySongDb(delegate (string songDbPath)
        {
            ChartFile first = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "First", "chart.bms"));
            ChartFile replacement = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine("C:\\Installed", "Replacement", "chart.bms"));
            var library = new TestBmsLibrary(songDbPath)
            {
                BmsCharts = [first],
                BmsonCharts = []
            };
            List<ChartFile> initialSnapshot = InvokeCreateOwnedChartInfoFullBackfillTargetSnapshot(library);
            Assert.AreEqual(1, initialSnapshot.Count);
            library.BmsCharts = [replacement];
            List<ChartFile> rebuiltSnapshot = InvokeCreateOwnedChartInfoFullBackfillTargetSnapshot(library);

            Assert.AreEqual(1, rebuiltSnapshot.Count);
            Assert.AreSame(replacement.Token, rebuiltSnapshot[0].Token);
        });
    }

    /// <summary>
    /// BMS・bmsonの公開一覧への追加・解除を拒否し、元入力リストを後から空にしても捕捉済み所有を変更させない。
    /// </summary>
    /// <remarks>実ライブラリの公開storage viewを使い、呼出し側から所持集合を変更できない境界を確認する。</remarks>
    [TestMethod]
    public void StorageRowPropertiesExposeReadOnlyViews()
    {

        WithTemporarySongDb(delegate (string songDbPath)
        {
            ChartFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Bms", "chart.bms"));
            ChartFile bmsonSong = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "chart.bmson"), "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            List<ChartFile> inputBmsFiles = [bmsFile];
            List<ChartFile> inputBmsonSongs = [bmsonSong];
            var library = new TestBmsLibrary(songDbPath)
            {
                BmsCharts = inputBmsFiles,
                BmsonCharts = inputBmsonSongs
            };

            Assert.AreEqual(2, InvokeCreateOwnedChartInfoFullBackfillTargetSnapshot(library).Count);
            Assert.ThrowsException<NotSupportedException>(() => ((IList<ChartFile>)library.BmsCharts).Add(CreateFile("cccccccccccccccccccccccccccccccc", Path.Combine("C:\\Installed", "Other", "chart.bms"))));
            Assert.ThrowsException<NotSupportedException>(() => ((IList<ChartFile>)library.BmsonCharts).Clear());
            Assert.AreEqual(1, library.BmsCharts.Count);
            Assert.AreEqual(1, library.BmsonCharts.Count);
            List<ChartFile> initialSnapshot = InvokeCreateOwnedChartInfoFullBackfillTargetSnapshot(library);
            Assert.AreEqual(2, initialSnapshot.Count);

            inputBmsFiles.Clear();
            inputBmsonSongs.Clear();
            List<ChartFile> afterInputMutationSnapshot = InvokeCreateOwnedChartInfoFullBackfillTargetSnapshot(library);

            Assert.AreEqual(1, library.BmsCharts.Count);
            Assert.AreEqual(1, library.BmsonCharts.Count);
            Assert.AreEqual(2, afterInputMutationSnapshot.Count);
            Assert.AreSame(bmsFile.Token, afterInputMutationSnapshot.Single(chart => chart.Kind == ChartFileKind.Bms).Token);
            Assert.AreSame(bmsonSong.Token, afterInputMutationSnapshot.Single(chart => chart.Kind == ChartFileKind.Bmson).Token);
        });
    }

    /// <summary>
    /// 実ライブラリの配下判定はBMS・bmsonと祖先を認識し、接頭辞だけ同じ兄弟・欠落先・nullを所有ありにしない。
    /// </summary>
    /// <remarks>実ライブラリの公開path判定をBMSとbmsonの所持集合へ接続し、両形式の存在判定を確認する。</remarks>
    [TestMethod]
    public void HasOwnedChartUnderRealPath_UsesOwnedCollectionForBmsAndBmson()
    {

        WithTemporarySongDb(delegate (string songDbPath)
        {
            ChartFile bmsFile = CreateFile(
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                Path.Combine("C:\\Installed", "Bms", "chart.bms"));
            ChartFile bmsonSong = CreateBmsonSong(
                Path.Combine("C:\\Installed", "Bmson", "chart.bmson"),
                "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            var library = new TestBmsLibrary(songDbPath)
            {
                BmsCharts = [bmsFile],
                BmsonCharts = [bmsonSong]
            };

            Assert.IsTrue(library.HasOwnedChartUnderRealPath(Path.Combine("C:\\Installed", "Bms")));
            Assert.IsTrue(library.HasOwnedChartUnderRealPath(Path.Combine("C:\\Installed", "Bmson")));
            Assert.IsTrue(library.HasOwnedChartUnderRealPath("C:\\Installed"));
            Assert.IsFalse(library.HasOwnedChartUnderRealPath("C:\\Install"));
            Assert.IsFalse(library.HasOwnedChartUnderRealPath(Path.Combine("C:\\Installed", "Missing")));
            Assert.IsFalse(library.HasOwnedChartUnderRealPath(null));
        });
    }

    /// <summary>
    /// bmsonの実削除を所持集合へ反映し、一回の通常一覧通知に削除tokenを含めてBMS変更を混入させない。
    /// </summary>
    /// <remarks>実ライブラリの変更入口を所持カタログへ接続し、入口で対象を解決してから反映する境界を観測するため実構成を使う。</remarks>
    [TestMethod]
    public void RemoveLibraryCharts_UnregistersBmsonStorageRowsInLibraryBoundary()
    {

        WithTemporarySongDb(delegate (string songDbPath)
        {
            string rootPath = Path.Combine(Path.GetDirectoryName(songDbPath)!, "Installed");
            string firstDirectoryPath = Path.Combine(rootPath, "First");
            string secondDirectoryPath = Path.Combine(rootPath, "Second");
            Directory.CreateDirectory(firstDirectoryPath);
            Directory.CreateDirectory(secondDirectoryPath);
            string firstPath = Path.Combine(firstDirectoryPath, "chart.bmson");
            string secondPath = Path.Combine(secondDirectoryPath, "chart.bmson");
            File.WriteAllText(firstPath, "{}");
            File.WriteAllText(secondPath, "{}");
            ChartFile first = CreateBmsonSong(firstPath, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            ChartFile second = CreateBmsonSong(secondPath, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            var library = new TestBmsLibrary(songDbPath);
            SetLibraryFilesWithoutNotification(library, []);
            SetLibraryBmsonSongsWithoutNotification(library, [first, second]);
            List<ChartFile> initialSnapshot = InvokeCreateOwnedChartInfoFullBackfillTargetSnapshot(library);
            Assert.AreEqual(2, initialSnapshot.Count);
            int handledNotificationVersion = library.NormalLibraryRefreshNotificationVersion;
            int bmsonSongsChanged = 0;
            library.PropertyChanged += delegate (object? _, System.ComponentModel.PropertyChangedEventArgs args)
            {
                if (args.PropertyName == nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion))
                {
                    bmsonSongsChanged++;
                }
            };
            LibraryChartRemovalOutcome removal = library.RemoveLibraryCharts(
                library.PrepareLibraryChartRemoval([LibraryChartRef.FromChartFile((first))]),
                sendToRecycleBin: false,
                approvedWholeFolderDeletePaths: []);
            Assert.IsFalse(removal.HasError);
            NormalLibraryRefreshNotificationBatch batch = library.GetNormalLibraryRefreshNotificationsAfter(handledNotificationVersion);

            Assert.AreEqual(1, library.BmsonCharts.Count);
            Assert.AreSame(second, library.BmsonCharts.Single());
            Assert.AreEqual(1, bmsonSongsChanged);
            Assert.IsTrue((batch.ChangedCharts.Count > 0 || batch.DeletedTokens.Count > 0 || batch.HasEffect(LibraryChartRefreshEffects.SourceChanged)));
            Assert.IsFalse(batch.ChangedCharts.Any(chart => chart.Kind == ChartFileKind.Bms));
            Assert.IsTrue(batch.DeletedTokens.Contains(first.Token));
            List<ChartFile> afterSnapshot = InvokeCreateOwnedChartInfoFullBackfillTargetSnapshot(library);
            Assert.AreEqual(1, afterSnapshot.Count);
            Assert.AreSame(second.Token, afterSnapshot[0].Token);
        });
    }

    /// <summary>
    /// 背景16譜面を残して同じライブラリで二回実削除しても、各hash索引を全件再構築せず共有hashの残存所有と旧snapshotを保持する。
    /// </summary>
    /// <remarks>実ライブラリの変更入口を所持カタログへ接続し、入口で対象を解決してから反映する境界を観測するため実構成を使う。</remarks>
    [DataTestMethod]
    [DataRow(16)]
    public void RemoveLibraryCharts_TwoWarmOperationsPreserveRemainingOwnersWithoutFullRebuild(int backgroundCount)
    {

        WithTemporarySongDb(songDbPath =>
        {
            string root = Path.GetDirectoryName(songDbPath)!;
            string sharedHash = new string('a', 32);
            string uniqueHash = new string('b', 32);
            List<ChartFile> files = [];
            for (int index = 0; index < backgroundCount; index++)
            {
                files.Add(CreateFile(
                    (index + 100).ToString("x32"),
                    Path.Combine(root, "Background", index.ToString("D3") + ".bms")));
            }
            string remainingPath = Path.Combine(root, "Remaining", "shared.bms");
            string firstFolder = Path.Combine(root, "Remove1");
            string secondFolder = Path.Combine(root, "Remove2");
            string firstPath = Path.Combine(firstFolder, "shared.bms");
            string secondPath = Path.Combine(secondFolder, "unique.bms");
            Directory.CreateDirectory(Path.GetDirectoryName(remainingPath)!);
            Directory.CreateDirectory(firstFolder);
            Directory.CreateDirectory(secondFolder);
            File.WriteAllText(remainingPath, "#PLAYER 1");
            File.WriteAllText(firstPath, "#PLAYER 1");
            File.WriteAllText(secondPath, "#PLAYER 1");
            ChartFile remaining = CreateFile(sharedHash, remainingPath);
            ChartFile first = CreateFile(sharedHash, firstPath);
            ChartFile second = CreateFile(uniqueHash, secondPath);
            files.Add(remaining);
            files.Add(first);
            files.Add(second);
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new FileDbReportRecordingDialogs())
            {
                BmsCharts = files,
                BmsonCharts = [],
                DuplicateChartGroups = []
            };
            // warm操作前のfixture seedは本番保存契約を検証しないため、一つのtransactionにまとめて共通DBロックの保持時間を短縮する。
            BmsLibraryInitializationTestSupport.ExecuteSongDbFixtureTransaction(songDbPath, db =>
            {
                foreach (ChartFile file in files)
                {
                    db.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(file), typeof(LR2SongDB.song));
                }
            });

            BMSLibrary.InstalledPrimaryHashWarmupResult initialPrimary = library.WarmInstalledPrimaryHashLookup("u1_warm_remove");
            Assert.IsFalse(initialPrimary.FullDirectoryLookupInitialized);
            OwnedChartHashIndexVersionedSnapshot initialHash = library.GetOwnedChartHashIndexSnapshot();
            InstalledChartLookupIndexSnapshot initialInstalled = InvokeCreateInstalledChartLookupSnapshot(library);
            PlaylistLibraryResolveIndexSnapshot initialPlaylist = library.GetPlaylistLibraryResolveIndexSnapshot(
                CancellationToken.None,
                out bool initialPlaylistCacheHit,
                out int initialPlaylistStaleRetries);
            Assert.IsFalse(initialPlaylistCacheHit);
            Assert.AreEqual(0, initialPlaylistStaleRetries);
            Assert.IsTrue(initialHash.ContainsMd5(sharedHash));
            Assert.IsTrue(initialHash.ContainsMd5(uniqueHash));
            (ChartFileKind Kind, string Path, string Md5, string Sha256)[] initialSharedCandidates =
                CapturePlaylistCandidateFacts(initialPlaylist.GetMd5Candidates(sharedHash));
            (ChartFileKind Kind, string Path, string Md5, string Sha256)[] initialUniqueCandidates =
                CapturePlaylistCandidateFacts(initialPlaylist.GetMd5Candidates(uniqueHash));
            Assert.IsTrue(initialSharedCandidates.Length > 0);
            Assert.IsTrue(initialUniqueCandidates.Length > 0);
            Assert.IsTrue(initialSharedCandidates.Any(candidate =>
                string.Equals(candidate.Path, remainingPath, StringComparison.OrdinalIgnoreCase)
                && string.Equals(candidate.Md5, sharedHash, StringComparison.OrdinalIgnoreCase)));
            Assert.IsTrue(initialSharedCandidates.Any(candidate =>
                string.Equals(candidate.Path, firstPath, StringComparison.OrdinalIgnoreCase)
                && string.Equals(candidate.Md5, sharedHash, StringComparison.OrdinalIgnoreCase)));
            Assert.IsTrue(initialUniqueCandidates.Any(candidate =>
                string.Equals(candidate.Path, secondPath, StringComparison.OrdinalIgnoreCase)
                && string.Equals(candidate.Md5, uniqueHash, StringComparison.OrdinalIgnoreCase)));
            LibraryChartRef initialSharedRepresentative = initialPlaylist.ResolveChartForPlaylistHash(sharedHash, null);
            LibraryChartRef initialUniqueRepresentative = initialPlaylist.ResolveChartForPlaylistHash(uniqueHash, null);
            Assert.IsNotNull(initialSharedRepresentative);
            Assert.IsNotNull(initialUniqueRepresentative);
            string initialSharedRepresentativePath = initialSharedRepresentative!.Path;
            string initialSharedRepresentativeMd5 = initialSharedRepresentative.Md5;
            string initialSharedRepresentativeSha256 = initialSharedRepresentative.Sha256;
            string initialUniqueRepresentativePath = initialUniqueRepresentative!.Path;
            string initialUniqueRepresentativeMd5 = initialUniqueRepresentative.Md5;
            string initialUniqueRepresentativeSha256 = initialUniqueRepresentative.Sha256;

            List<string> hashWork = [];
            List<string> playlistWork = [];
            List<string> installedWork = [];
            library.OwnedChartHashIndexStoreWorkObserver = hashWork.Add;
            library.PlaylistLibraryResolveIndexStoreWorkObserver = playlistWork.Add;
            library.InstalledChartLookupStoreWorkObserver = installedWork.Add;
            (ChartFile File, string Folder)[] removals =
            [
                (first, firstFolder),
                (second, secondFolder)
            ];
            for (int removalIndex = 0; removalIndex < removals.Length; removalIndex++)
            {
                (ChartFile file, string folder) = removals[removalIndex];
                string oldPath = file.Path;
                string oldHash = file.Md5;
                LibraryChartRemovalOutcome outcome = library.RemoveLibraryCharts(
                    library.PrepareLibraryChartRemoval([LibraryChartRef.FromChartFile((file))]),
                    sendToRecycleBin: false,
                    approvedWholeFolderDeletePaths: [folder]);

                Assert.IsFalse(outcome.HasError);
                Assert.IsTrue(outcome.CatalogDurable);
                Assert.IsFalse(File.Exists(oldPath));
                Assert.IsFalse(Directory.Exists(folder));
                Assert.IsNull(library.DuplicateChartGroups);
                Assert.IsFalse(InvokeCreateOwnedChartInfoFullBackfillTargetSnapshot(library)
                    .Any(chart => string.Equals(chart.Path, oldPath, StringComparison.OrdinalIgnoreCase)));
                // ここはSELECT専用の観測なので、writer接続を保持せずread-only入口を使う。
                using (LR2SongDBExtended verifyDb = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly())
                {
                    string[] dbPaths = verifyDb.Table<LR2SongDB.song>().Select(row => row.path).ToArray();
                    Assert.IsFalse(dbPaths.Contains(oldPath, StringComparer.OrdinalIgnoreCase));
                    Assert.IsTrue(dbPaths.Contains(remainingPath, StringComparer.OrdinalIgnoreCase));
                    Assert.AreEqual(
                        removalIndex == 0,
                        dbPaths.Contains(secondPath, StringComparer.OrdinalIgnoreCase));
                    foreach (ChartFile backgroundFile in files.Take(backgroundCount))
                    {
                        Assert.IsTrue(
                            dbPaths.Contains(backgroundFile.Path, StringComparer.OrdinalIgnoreCase),
                            "削除後も未対象のbackground rowを保持します。");
                    }
                }
                OwnedChartHashIndexVersionedSnapshot updatedHash = library.GetOwnedChartHashIndexSnapshot();
                OwnedChartHashIndexVersionedSnapshot cachedHash = library.GetOwnedChartHashIndexSnapshot();
                InstalledChartLookupIndexSnapshot updatedInstalled = InvokeCreateInstalledChartLookupSnapshot(library);
                InstalledChartLookupIndexSnapshot cachedInstalled = InvokeCreateInstalledChartLookupSnapshot(library);
                BMSLibrary.InstalledPrimaryHashWarmupResult updatedPrimary = library.WarmInstalledPrimaryHashLookup("u1_warm_remove");
                PlaylistLibraryResolveIndexSnapshot updatedPlaylist = library.GetPlaylistLibraryResolveIndexSnapshot(
                    CancellationToken.None,
                    out bool updatedPlaylistCacheHit,
                    out int updatedPlaylistStaleRetries);
                PlaylistLibraryResolveIndexSnapshot cachedPlaylist = library.GetPlaylistLibraryResolveIndexSnapshot(
                    CancellationToken.None,
                    out bool cachedPlaylistCacheHit,
                    out int cachedPlaylistStaleRetries);

                Assert.AreSame(updatedHash, cachedHash);
                Assert.AreSame(updatedInstalled, cachedInstalled);
                Assert.AreEqual("cached", updatedPrimary.Status);
                Assert.AreEqual(0L, updatedPrimary.BuildMs);
                Assert.IsTrue(updatedPlaylistCacheHit);
                Assert.AreEqual(0, updatedPlaylistStaleRetries);
                Assert.IsTrue(cachedPlaylistCacheHit);
                Assert.AreEqual(0, cachedPlaylistStaleRetries);
                Assert.AreSame(updatedPlaylist, cachedPlaylist);
                Assert.IsFalse(updatedPlaylist.ContainsCandidate(ChartFileKind.Bms, oldPath));
                Assert.IsTrue(CapturePlaylistCandidateFacts(updatedPlaylist.GetMd5Candidates(sharedHash))
                    .Any(candidate =>
                        candidate.Kind == ChartFileKind.Bms
                        && string.Equals(candidate.Path, remainingPath, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(candidate.Md5, sharedHash, StringComparison.OrdinalIgnoreCase)));
                Assert.AreEqual(
                    removalIndex == 0,
                    CapturePlaylistCandidateFacts(updatedPlaylist.GetMd5Candidates(uniqueHash)).Any(candidate =>
                        string.Equals(candidate.Path, secondPath, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(candidate.Md5, uniqueHash, StringComparison.OrdinalIgnoreCase)));
                Assert.IsTrue(updatedHash.ContainsMd5(sharedHash));
                Assert.AreEqual(removalIndex == 0, updatedHash.ContainsMd5(uniqueHash));
                Assert.IsTrue(initialHash.ContainsMd5(oldHash));
                Assert.IsNotNull(initialPlaylist.ResolveChartForPlaylistHash(oldHash, null));
                Assert.AreEqual(
                    initialSharedRepresentativePath,
                    initialPlaylist.ResolveChartForPlaylistHash(sharedHash, null)!.Path);
                Assert.AreEqual(
                    initialSharedRepresentativeMd5,
                    initialPlaylist.ResolveChartForPlaylistHash(sharedHash, null)!.Md5);
                Assert.AreEqual(
                    initialSharedRepresentativeSha256,
                    initialPlaylist.ResolveChartForPlaylistHash(sharedHash, null)!.Sha256);
                Assert.AreEqual(
                    initialUniqueRepresentativePath,
                    initialPlaylist.ResolveChartForPlaylistHash(uniqueHash, null)!.Path);
                Assert.AreEqual(
                    initialUniqueRepresentativeMd5,
                    initialPlaylist.ResolveChartForPlaylistHash(uniqueHash, null)!.Md5);
                Assert.AreEqual(
                    initialUniqueRepresentativeSha256,
                    initialPlaylist.ResolveChartForPlaylistHash(uniqueHash, null)!.Sha256);
                CollectionAssert.AreEqual(
                    initialSharedCandidates,
                    CapturePlaylistCandidateFacts(initialPlaylist.GetMd5Candidates(sharedHash)));
                CollectionAssert.AreEqual(
                    initialUniqueCandidates,
                    CapturePlaylistCandidateFacts(initialPlaylist.GetMd5Candidates(uniqueHash)));
            }

            Assert.IsTrue(library.BmsCharts.Contains(remaining));
            Assert.IsTrue(library.GetOwnedChartHashIndexSnapshot().ContainsMd5(sharedHash));
            Assert.IsFalse(library.GetOwnedChartHashIndexSnapshot().ContainsMd5(uniqueHash));
            Assert.AreEqual(0, hashWork.Count(operation => operation == "owned_hash_source_enumeration"));
            Assert.AreEqual(
                0,
                playlistWork.Count(operation => operation == "playlist_resolve_source_enumeration" || operation == "playlist_resolve_full_root_enumeration"));
            Assert.IsTrue(
                installedWork.Count(operation => operation == "installed_primary_hash_count_update") <= 4,
                "warm削除後にinstalled lookupを全件再構築しました。");
            Assert.IsTrue(
                installedWork.Count(operation => operation == "installed_primary_hash_count_update") > 0,
                "実削除のinstalled lookup差分更新を観測できませんでした。");
        });
    }

    /// <summary>
    /// リソース欠落警告を持つ譜面の実削除は最新健全性索引へ差分反映し、以前のsnapshotを保ちながら現在の警告対象を除く。
    /// </summary>
    /// <remarks>実ライブラリの変更入口を所持カタログへ接続し、入口で対象を解決してから反映する境界を観測するため実構成を使う。</remarks>
    [TestMethod]
    public void RemoveLibraryCharts_UnregisterAppliesCurrentResourceHealthIndexDelta()
    {

        WithTemporarySongDb(delegate (string songDbPath)
        {
            string resourceDirectoryPath = Path.Combine(Path.GetDirectoryName(songDbPath)!, "Installed", "Resource");
            Directory.CreateDirectory(resourceDirectoryPath);
            string resourcePath = Path.Combine(resourceDirectoryPath, "chart.bms");
            File.WriteAllText(resourcePath, "#PLAYER 1");
            ChartFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", resourcePath);
            bmsFile = ChartFileProjection.WithMaintenance(bmsFile, MaintenanceStorageMapping.ToCommon(new LR2SongDBExtended.maintenance { path = bmsFile.Path, hash = bmsFile.Md5, wav_files_defined = 2, wav_files_existing = 1 }));
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), null);
            SetLibraryFilesWithoutNotification(library, [bmsFile]);
            SetLibraryBmsonSongsWithoutNotification(library, []);
            EnsureCurrentResourceHealthIndex(library);
            ResourceHealthIndexSnapshot beforeSnapshot = library.TryGetCurrentResourceHealthIndexSnapshotForView();
            Assert.AreEqual(1, beforeSnapshot.TargetCount);
            Assert.IsTrue(beforeSnapshot.GetProjection(
                ChartFileKind.Bms,
                bmsFile.Path,
                bmsFile.Md5).HasIssues);
            LibraryChartRemovalOutcome removal = library.RemoveLibraryCharts(
                library.PrepareLibraryChartRemoval([LibraryChartRef.FromChartFile((bmsFile))]),
                sendToRecycleBin: false,
                approvedWholeFolderDeletePaths: []);
            Assert.IsFalse(removal.HasError);

            ResourceHealthIndexSnapshot afterSnapshot = library.TryGetCurrentResourceHealthIndexSnapshotForView();
            Assert.AreEqual(0, afterSnapshot.TargetCount);
            Assert.AreEqual(1, beforeSnapshot.TargetCount);
            Assert.IsFalse(HasNoCurrentResourceHealthIndex(library));
        });
    }

    /// <summary>
    /// 実フォルダ改名の保存確定後は古いパスの健全性索引を失効し、現在の表示へ以前の警告対象を返さない。
    /// </summary>
    /// <remarks>実ライブラリの変更入口を所持カタログへ接続し、入口で対象を解決してから反映する境界を観測するため実構成を使う。</remarks>
    [TestMethod]
    public void RenameChartFolder_InvalidatesCurrentResourceHealthIndex()
    {

        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_ResourceMutation_" + Guid.NewGuid().ToString("N"));
            string oldDirectoryPath = Path.Combine(tempRootPath, "Old");
            string newDirectoryPath = Path.Combine(tempRootPath, "New");
            Directory.CreateDirectory(oldDirectoryPath);
            string oldBmsPath = Path.Combine(oldDirectoryPath, "chart.bms");
            string newBmsPath = Path.Combine(newDirectoryPath, "chart.bms");
            File.WriteAllText(oldBmsPath, "#PLAYER 1");
            try
            {
                ChartFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", oldBmsPath);
                bmsFile = ChartFileProjection.WithMaintenance(bmsFile, MaintenanceStorageMapping.ToCommon(new LR2SongDBExtended.maintenance { path = bmsFile.Path, hash = bmsFile.Md5, wav_files_defined = 2, wav_files_existing = 1 }));
                var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), null);
                SetLibraryFilesWithoutNotification(library, [bmsFile]);
                SetLibraryBmsonSongsWithoutNotification(library, []);
                EnsureCurrentResourceHealthIndex(library);
                Assert.AreEqual(1, library.TryGetCurrentResourceHealthIndexSnapshotForView().TargetCount);
                LibraryMutationSessionReceipt receipt = library.RenameChartFolderWithReceipt(
                    oldDirectoryPath,
                    "New",
                    unregister: false,
                    renameRootFolder: false);
                Assert.IsTrue(receipt.DurableCommit, receipt.PrimaryFailure?.ToString());

                Assert.AreEqual(0, library.TryGetCurrentResourceHealthIndexSnapshotForView().TargetCount);
                Assert.IsTrue(HasNoCurrentResourceHealthIndex(library));
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

    /// <summary>
    /// BMS・bmsonを含む実フォルダ改名は通知前に新パスを公開し、集合・親cache・重複cacheと通常一覧通知を一度だけ更新する。
    /// </summary>
    /// <remarks>実ライブラリの変更入口を所持カタログへ接続し、入口で対象を解決してから反映する境界を観測するため実構成を使う。</remarks>
    [TestMethod]
    public void RenameChartFolder_DispatchesParentFolderOnceAndClearsDuplicateCache()
    {

        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_OwnedMutationDispatch_" + Guid.NewGuid().ToString("N"));
            string oldDirectoryPath = Path.Combine(tempRootPath, "Old");
            string newDirectoryPath = Path.Combine(tempRootPath, "New");
            Directory.CreateDirectory(oldDirectoryPath);
            string oldBmsPath = Path.Combine(oldDirectoryPath, "chart.bms");
            string newBmsPath = Path.Combine(newDirectoryPath, "chart.bms");
            string oldBmsonPath = Path.Combine(oldDirectoryPath, "chart.bmson");
            string newBmsonPath = Path.Combine(newDirectoryPath, "chart.bmson");
            File.WriteAllText(oldBmsPath, "#PLAYER 1");
            File.WriteAllText(oldBmsonPath, "{}");
            try
            {
                ChartFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", oldBmsPath);
                ChartFile bmsonSong = CreateBmsonSong(oldBmsonPath, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
                var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), null);
                SetLibraryFilesWithoutNotification(library, [bmsFile]);
                SetLibraryBmsonSongsWithoutNotification(library, [bmsonSong]);
                SetDuplicateChartGroupsWithoutNotification(library, []);
                int baselineCatalogRevision = library.CatalogRevision;
                int handledNotificationVersion = library.NormalLibraryRefreshNotificationVersion;
                int catalogRevisionChanged = 0;
                int bmsFilesChanged = 0;
                int bmsonSongsChanged = 0;
                bool bmsonPathAvailableAtNotification = false;
                int baselineParentFolderVersion = library.BMSParentFolderListCacheVersion;
                int baselineDuplicateInvalidationVersion = library.DuplicateChartGroupsInvalidationVersion;
                int parentFolderVersionChanged = 0;
                library.PropertyChanged += delegate (object? _, System.ComponentModel.PropertyChangedEventArgs args)
                {
                    if (args.PropertyName == "CatalogRevision")
                    {
                        catalogRevisionChanged++;
                    }
                    if (args.PropertyName == nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion))
                    {
                        bmsFilesChanged++;
                    }
                    if (args.PropertyName == nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion))
                    {
                        bmsonSongsChanged++;
                    }
                    if (args.PropertyName == "CatalogRevision")
                    {
                        bmsonPathAvailableAtNotification = library.BmsonCharts.Any(song => song.Path == newBmsonPath);
                    }
                    if (args.PropertyName == "BMSParentFolderListCacheVersion")
                    {
                        parentFolderVersionChanged++;
                    }
                };
                LibraryMutationSessionReceipt receipt = library.RenameChartFolderWithReceipt(
                    oldDirectoryPath,
                    "New",
                    unregister: false,
                    renameRootFolder: false);
                Assert.IsTrue(receipt.DurableCommit, receipt.PrimaryFailure?.ToString());
                NormalLibraryRefreshNotificationBatch batch = library.GetNormalLibraryRefreshNotificationsAfter(handledNotificationVersion);

                Assert.AreEqual(baselineCatalogRevision + 1, library.CatalogRevision);
                Assert.AreEqual(1, catalogRevisionChanged);
                Assert.AreEqual(1, bmsFilesChanged);
                Assert.AreEqual(1, bmsonSongsChanged);
                Assert.AreEqual(0, batch.DeletedTokens.Count);
                Assert.IsTrue(batch.ChangedCharts.Any(chart => chart.Kind == ChartFileKind.Bms && chart.Path == newBmsPath));
                Assert.IsTrue(batch.ChangedCharts.Any(chart => chart.Kind == ChartFileKind.Bmson && chart.Path == newBmsonPath));
                Assert.IsTrue(bmsonPathAvailableAtNotification);
                Assert.AreEqual(baselineParentFolderVersion + 1, library.BMSParentFolderListCacheVersion);
                Assert.AreEqual(1, parentFolderVersionChanged);
                Assert.IsNull(library.DuplicateChartGroups);
                Assert.AreEqual(baselineDuplicateInvalidationVersion + 1, library.DuplicateChartGroupsInvalidationVersion);
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

    private static (ChartFileKind Kind, string Path, string Md5, string Sha256)[] CapturePlaylistCandidateFacts(
        IEnumerable<LibraryChartRef> candidates)
    {
        return [.. (candidates ?? [])
            .Where(candidate => candidate != null)
            .Select(candidate => (candidate.Kind, candidate.Path, candidate.Md5, candidate.Sha256))];
    }

    /// <summary>モデルが確認を再表示せず、明示された承認集合で実行することを観測します。</summary>
    private sealed class RecordingRemovalDialogs : IBmsLibraryDialogService
    {
        internal int CallCount { get; private set; }

        public UiDialogDefaultResult Show(string messageBoxText, string caption, UiDialogButton button,
            UiDialogIcon icon, UiDialogDefaultResult defaultResult = UiDialogDefaultResult.None)
        {
            CallCount++;
            return UiDialogDefaultResult.OK;
        }
    }
}
