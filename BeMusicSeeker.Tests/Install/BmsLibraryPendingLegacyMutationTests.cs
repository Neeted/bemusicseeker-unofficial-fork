using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using Microsoft.VisualBasic.FileIO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BmsLibraryPendingLegacyMutationTests
{
    /// <summary>存在しない選択譜面を失敗として集計し、ファイルや親ディレクトリの削除へ進まず元パスと失敗型を返す。</summary>
    /// <remarks>実ファイルで物理変更と残存を観測する。ライブラリ経由のケースでは実SQLite保存行も確認し、ごみ箱そのものは局所portで代替する。</remarks>
    [TestMethod]
    public void DeletePendingCharts_MissingSourceIsFailureWithoutFileMutation()
    {
        WithTemporaryDirectory(tempDirectoryPath =>
        {
            string packageDirectoryPath = Path.Combine(tempDirectoryPath, "target");
            Directory.CreateDirectory(packageDirectoryPath);
            ChartFile chartFile = ChartTestValues.Empty() with
            {
                Path = Path.Combine(packageDirectoryPath, "missing.bms")
            };
            chartFile = chartFile with { Md5 = new string('a', 32) };
            ChartPackage package = ChartPackageTestExtensions.CreatePackage([chartFile]);
            package.path = chartFile.Path;
            var fileMutationService = new RecordingDeleteFileMutationService();
            var service = new BmsLibraryPackageInstallService();

            PendingFileDeletionResult result = service.DeletePendingCharts(
                [(chartFile)],
                [package],
                sendToRecycleBin: false,
                deleteContainingPackageFoldersWhenNoBms: true,
                fileMutationService,
                null,
                null);

            Assert.AreEqual(1, result.Requested);
            Assert.AreEqual(1, result.Processed);
            Assert.AreEqual(0, result.Removed);
            Assert.AreEqual(1, result.Failed);
            Assert.AreEqual(0, fileMutationService.DeleteFileShellPaths.Count);
            Assert.AreEqual(0, fileMutationService.DeleteDirectoryDirectPaths.Count);
            Assert.AreEqual(0, fileMutationService.DeleteDirectoryShellPaths.Count);
            Assert.AreEqual(chartFile.Path, result.Failures[0].Path);
            Assert.IsInstanceOfType(result.Failures[0].Exception, typeof(FileNotFoundException));
        });
    }

    /// <summary>譜面パスがディレクトリになっている入力を失敗にし、削除portを呼ばず元ディレクトリを残す。</summary>
    /// <remarks>実ファイルで物理変更と残存を観測する。ライブラリ経由のケースでは実SQLite保存行も確認し、ごみ箱そのものは局所portで代替する。</remarks>
    [TestMethod]
    public void DeletePendingCharts_WrongKindSourceIsFailureWithoutMutation()
    {
        WithTemporaryDirectory(tempDirectoryPath =>
        {
            string packageDirectoryPath = Path.Combine(tempDirectoryPath, "target");
            Directory.CreateDirectory(packageDirectoryPath);
            ChartFile chartFile = ChartTestValues.Empty() with
            {
                Path = packageDirectoryPath
            };
            chartFile = chartFile with { Md5 = new string('a', 32) };
            ChartPackage package = ChartPackageTestExtensions.CreatePackage([chartFile]);
            package.path = chartFile.Path;
            var fileMutationService = new RecordingDeleteFileMutationService();
            var service = new BmsLibraryPackageInstallService();

            PendingFileDeletionResult result = service.DeletePendingCharts(
                [(chartFile)],
                [package],
                sendToRecycleBin: false,
                deleteContainingPackageFoldersWhenNoBms: true,
                fileMutationService,
                null,
                null);

            Assert.AreEqual(1, result.Requested);
            Assert.AreEqual(1, result.Processed);
            Assert.AreEqual(0, result.Removed);
            Assert.AreEqual(1, result.Failed);
            Assert.IsInstanceOfType(result.Failures[0].Exception, typeof(IOException));
            Assert.AreEqual(0, fileMutationService.DeleteFileShellPaths.Count);
            Assert.AreEqual(0, fileMutationService.DeleteDirectoryDirectPaths.Count);
            Assert.IsTrue(Directory.Exists(packageDirectoryPath));
        });
    }

    /// <summary>実symlinkの選択譜面を拒否し、外部のリンク先を残してファイルや親ディレクトリの削除へ進まない。</summary>
    /// <remarks>実ファイルで物理変更と残存を観測する。ライブラリ経由のケースでは実SQLite保存行も確認し、ごみ箱そのものは局所portで代替する。</remarks>
    [TestMethod]
    public void DeletePendingCharts_ReparseSourceIsFailureWithoutMutation()
    {
        WithTemporaryDirectory(tempDirectoryPath =>
        {
            string packageDirectoryPath = Path.Combine(tempDirectoryPath, "target");
            Directory.CreateDirectory(packageDirectoryPath);
            string targetPath = Path.Combine(tempDirectoryPath, "external.bms");
            File.WriteAllText(targetPath, "#PLAYER 1\r\n");
            string linkPath = Path.Combine(packageDirectoryPath, "linked.bms");
            try
            {
                File.CreateSymbolicLink(linkPath, targetPath);
            }
            catch (Exception exception) when (exception is IOException
                || exception is UnauthorizedAccessException
                || exception is PlatformNotSupportedException)
            {
                Assert.Inconclusive("実ファイルsymlinkを作成できない環境のため、この安全境界の実入力検証は未実施: " + exception.Message);
                return;
            }
            ChartFile chartFile = ChartTestValues.Empty() with
            {
                Path = linkPath
            };
            chartFile = chartFile with { Md5 = new string('a', 32) };
            ChartPackage package = ChartPackageTestExtensions.CreatePackage([chartFile]);
            package.path = chartFile.Path;
            var fileMutationService = new RecordingDeleteFileMutationService();
            var service = new BmsLibraryPackageInstallService();

            PendingFileDeletionResult result = service.DeletePendingCharts(
                [(chartFile)],
                [package],
                sendToRecycleBin: false,
                deleteContainingPackageFoldersWhenNoBms: true,
                fileMutationService,
                null,
                null);

            Assert.AreEqual(1, result.Requested);
            Assert.AreEqual(1, result.Processed);
            Assert.AreEqual(0, result.Removed);
            Assert.AreEqual(1, result.Failed);
            Assert.IsInstanceOfType(result.Failures[0].Exception, typeof(IOException));
            Assert.AreEqual(0, fileMutationService.DeleteFileShellPaths.Count);
            Assert.AreEqual(0, fileMutationService.DeleteDirectoryDirectPaths.Count);
            Assert.IsTrue(File.Exists(targetPath));
        });
    }

    /// <summary>譜面単位の削除が一件失敗しても成功した兄弟だけを除き、失敗譜面・パッケージ・保存行を残して親の削除へ昇格しない。</summary>
    /// <remarks>実ファイルで物理変更と残存を観測する。ライブラリ経由のケースでは実SQLite保存行も確認し、ごみ箱そのものは局所portで代替する。</remarks>
    [TestMethod]
    public void DeletePendingCharts_ChartOnlyChildFailureKeepsFailedRowAndPackage()
    {
        WithTemporaryDirectory(tempDirectoryPath =>
        {
            string songDbPath = Path.Combine(tempDirectoryPath, "song.db");
            File.WriteAllBytes(songDbPath, []);
            string packageDirectoryPath = Path.Combine(tempDirectoryPath, "target");
            Directory.CreateDirectory(packageDirectoryPath);
            ChartFile failedFile = CreateChart(packageDirectoryPath, "failed.bms", "a");
            ChartFile successfulFile = CreateChart(packageDirectoryPath, "successful.bms", "b");
            ChartPackage package = ChartPackageTestExtensions.CreatePackage([failedFile, successfulFile]);
            package.path = packageDirectoryPath;
            foreach (PackageChartEntry entry in package.ChartEntries)
            {
                BmsLibraryPackageInstallService.InitializePendingResourceHealth(entry);
                entry.SetInstallDestinationPathOnly(Path.Combine(tempDirectoryPath, "destination"));
            }
            var fileMutationService = new RecordingDeleteFileMutationService(failedFile.Path);
            BmsLibraryInitializationTestSupport.ExecuteSongDbFixtureTransaction(songDbPath, songDb =>
            {
                songDb.CreateTable<LR2SongDBExtended.install>();
                songDb.InsertOrReplace(package, typeof(LR2SongDBExtended.install));
            });
            var library = new TestBmsLibrary(
                songDbPath,
                null,
                null,
                fileMutationService,
                new BmsLibraryInitializationTestSupport.RecordingDialogService())
            {
                ChartPackagesPending = new System.Collections.ObjectModel.ObservableCollection<ChartPackage>([package])
            };

            library.RemovePendingCharts(
                [(failedFile), (successfulFile)],
                sendToRecycleBin: false,
                deleteContainingPackageFoldersWhenNoBms: false);

            Assert.AreEqual(1, fileMutationService.DeleteFileDirectPaths.Count);
            CollectionAssert.AreEqual(new[] { successfulFile.Path }, fileMutationService.DeleteFileDirectPaths);
            Assert.IsTrue(File.Exists(failedFile.Path));
            Assert.IsFalse(File.Exists(successfulFile.Path));
            Assert.AreEqual(0, fileMutationService.DeleteDirectoryShellPaths.Count);
            Assert.AreEqual(0, fileMutationService.DeleteDirectoryDirectPaths.Count);
            Assert.IsTrue(Directory.Exists(packageDirectoryPath));
            Assert.AreSame(package, library.ChartPackagesPending.Single());
            Assert.AreEqual(1, package.ChartEntries.Count);
            Assert.AreEqual(failedFile.Path, package.ChartEntries.Single().Chart.Path);
            BmsLibraryPackageInstallService.ClearPendingInstallDestination(package.ChartEntries.Single());
            Assert.AreEqual(0, package.ChartEntries.Single().Chart.ResourceHealthMaintenanceSnapshot.WavFilesDefined);
            Assert.AreEqual(string.Empty, package.ChartEntries.Single().Chart.InstallDestination);
            using var verifySongDb = new LR2SongDBExtended(songDbPath, SQLite.SQLiteOpenFlags.ReadOnly, acquireProcessLock: false);
            Assert.AreEqual(1, verifySongDb.Table<LR2SongDBExtended.install>().Count());
            Assert.AreEqual(package.path, verifySongDb.Table<LR2SongDBExtended.install>().Single().path);
        });
    }

    /// <summary>パッケージ全体の削除は指定されたごみ箱方針と再帰正規化で行い、失敗したパッケージの譜面・リソース・保存行を保持する。</summary>
    /// <remarks>実ファイルで物理変更と残存を観測する。ライブラリ経由のケースでは実SQLite保存行も確認し、ごみ箱そのものは局所portで代替する。</remarks>
    // falseは完全削除、trueはごみ箱指定。どちらも失敗したパッケージの部分削除を禁止する。
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RemovePendingCharts_WholePackagesHonorRecyclePolicyAndKeepFailedPackage(bool sendToRecycleBin)
    {

        WithTemporaryDirectory(tempDirectoryPath =>
        {
            string failedDirectoryPath = Path.Combine(tempDirectoryPath, "FailedPackage");
            string successfulDirectoryPath = Path.Combine(tempDirectoryPath, "SuccessfulPackage");
            string failedNestedDirectoryPath = Path.Combine(failedDirectoryPath, "sub");
            string successfulNestedDirectoryPath = Path.Combine(successfulDirectoryPath, "sub");
            Directory.CreateDirectory(failedNestedDirectoryPath);
            Directory.CreateDirectory(successfulNestedDirectoryPath);
            ChartFile failedA = CreateChart(failedDirectoryPath, "a.bms", "a");
            ChartFile failedB = CreateChart(failedNestedDirectoryPath, "b.bms", "b");
            CreateChart(successfulDirectoryPath, "a.bms", "c");
            CreateChart(successfulNestedDirectoryPath, "b.bms", "d");
            string failedResourcePath = Path.Combine(failedDirectoryPath, "sound.wav");
            File.WriteAllText(failedResourcePath, "keep resource");
            string nestedResourceDirectoryPath = Path.Combine(successfulDirectoryPath, "resources");
            Directory.CreateDirectory(nestedResourceDirectoryPath);
            File.WriteAllText(Path.Combine(nestedResourceDirectoryPath, "sound.wav"), "remove resource");
            string unrelatedPath = Path.Combine(tempDirectoryPath, "unrelated.txt");
            File.WriteAllText(unrelatedPath, "keep parent content");
            var service = new BmsLibraryPackageInstallService();
            List<ChartPackage> packages = service.SearchChartPackagesRecursivelyWithMetadata(tempDirectoryPath, 0.6).Packages;
            ChartPackage failedPackage = packages.Single(package => package.path == failedDirectoryPath);
            ChartPackage successfulPackage = packages.Single(package => package.path == successfulDirectoryPath);
            Assert.AreEqual(2, failedPackage.ChartEntries.Count);
            Assert.AreEqual(2, successfulPackage.ChartEntries.Count);
            var fileMutationService = new RecordingDeleteFileMutationService(directoryFailurePath: failedDirectoryPath);
            var dialogService = new BmsLibraryInitializationTestSupport.RecordingDialogService();
            string songDbPath = Path.Combine(tempDirectoryPath, "song.db");
            BmsLibraryInitializationTestSupport.ExecuteSongDbFixtureTransaction(songDbPath, songDb =>
            {
                songDb.CreateTable<LR2SongDBExtended.install>();
                songDb.InsertOrReplace(failedPackage, typeof(LR2SongDBExtended.install));
                songDb.InsertOrReplace(successfulPackage, typeof(LR2SongDBExtended.install));
            });
            var library = new TestBmsLibrary(songDbPath, null, null, fileMutationService, dialogService)
            {
                ChartPackagesPending = new System.Collections.ObjectModel.ObservableCollection<ChartPackage>([failedPackage, successfulPackage])
            };

            library.RemovePendingCharts(
                packages.SelectMany(package => package.ChartEntries).Select(entry => entry.Chart),
                sendToRecycleBin,
                deleteContainingPackageFoldersWhenNoBms: true);

            CollectionAssert.AreEquivalent(
                new[] { failedDirectoryPath, successfulDirectoryPath },
                fileMutationService.DeleteDirectoryShellPaths);
            RecycleOption expectedPolicy = sendToRecycleBin ? RecycleOption.SendToRecycleBin : RecycleOption.DeletePermanently;
            CollectionAssert.AreEqual(new[] { expectedPolicy, expectedPolicy }, fileMutationService.DeleteDirectoryRecycleOptions);
            CollectionAssert.AreEqual(
                new[] { ReadOnlyNormalizationScope.RecursiveDirectoryTree, ReadOnlyNormalizationScope.RecursiveDirectoryTree },
                fileMutationService.DeleteDirectoryNormalizationScopes);
            Assert.AreEqual(0, fileMutationService.DeleteFileShellPaths.Count);
            Assert.AreEqual(0, fileMutationService.DeleteFileDirectPaths.Count);
            Assert.IsTrue(File.Exists(failedA.Path));
            Assert.IsTrue(File.Exists(failedB.Path));
            Assert.AreEqual("keep resource", File.ReadAllText(failedResourcePath));
            Assert.IsFalse(Directory.Exists(successfulDirectoryPath));
            Assert.AreEqual("keep parent content", File.ReadAllText(unrelatedPath));
            Assert.AreSame(failedPackage, library.ChartPackagesPending.Single());
            CollectionAssert.AreEquivalent(
                new[] { failedA.Path, failedB.Path },
                failedPackage.ChartEntries.Select(entry => entry.Chart.Path).ToArray());
            Assert.AreEqual(1, dialogService.Calls.Count);
            using var verifySongDb = new LR2SongDBExtended(songDbPath, SQLite.SQLiteOpenFlags.ReadOnly, acquireProcessLock: false);
            Assert.AreEqual(failedDirectoryPath, verifySongDb.Table<LR2SongDBExtended.install>().Single().path);
        });
    }

    /// <summary>一部の譜面削除ではパッケージとリソースを残し、最後の譜面ではフォルダ全体削除の指定に応じてリソースの保持を分ける。</summary>
    /// <remarks>実ファイルで物理変更と残存を観測する。ライブラリ経由のケースでは実SQLite保存行も確認し、ごみ箱そのものは局所portで代替する。</remarks>
    // falseは最後の譜面も単体削除、trueは最後の選択時だけパッケージ全体を削除する。
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RemovePendingCharts_PartialThenLastSelectionHonorsWholePackageOption(bool deleteWholePackage)
    {

        WithTemporaryDirectory(tempDirectoryPath =>
        {
            string packageDirectoryPath = Path.Combine(tempDirectoryPath, "Package");
            string nestedDirectoryPath = Path.Combine(packageDirectoryPath, "sub");
            Directory.CreateDirectory(nestedDirectoryPath);
            string rootChartPath = CreateChart(packageDirectoryPath, "root.bms", "a").Path;
            string nestedChartPath = CreateChart(nestedDirectoryPath, "nested.bms", "b").Path;
            string resourcePath = Path.Combine(nestedDirectoryPath, "sound.wav");
            File.WriteAllText(resourcePath, "keep until package deletion");
            var service = new BmsLibraryPackageInstallService();
            ChartPackage package = service.SearchChartPackagesRecursivelyWithMetadata(packageDirectoryPath, 0.6).Packages.Single();
            Assert.AreEqual(packageDirectoryPath, package.path);
            Assert.AreEqual(2, package.ChartEntries.Count);
            ChartFile rootChart = package.ChartEntries.Single(entry => entry.Chart.Path == rootChartPath).Chart;
            ChartFile nestedChart = package.ChartEntries.Single(entry => entry.Chart.Path == nestedChartPath).Chart;
            var fileMutationService = new RecordingDeleteFileMutationService();
            var dialogService = new BmsLibraryInitializationTestSupport.RecordingDialogService();
            string songDbPath = Path.Combine(tempDirectoryPath, "song.db");
            BmsLibraryInitializationTestSupport.ExecuteSongDbFixtureTransaction(songDbPath, songDb =>
            {
                songDb.CreateTable<LR2SongDBExtended.install>();
                songDb.InsertOrReplace(package, typeof(LR2SongDBExtended.install));
            });
            var library = new TestBmsLibrary(songDbPath, null, null, fileMutationService, dialogService)
            {
                ChartPackagesPending = new System.Collections.ObjectModel.ObservableCollection<ChartPackage>([package])
            };

            library.RemovePendingCharts([rootChart], sendToRecycleBin: true, deleteContainingPackageFoldersWhenNoBms: deleteWholePackage);

            Assert.IsFalse(File.Exists(rootChartPath));
            Assert.IsTrue(File.Exists(nestedChartPath));
            Assert.IsTrue(File.Exists(resourcePath));
            Assert.AreEqual(0, fileMutationService.DeleteDirectoryShellPaths.Count);
            Assert.AreSame(package, library.ChartPackagesPending.Single());
            Assert.AreEqual(nestedChartPath, package.ChartEntries.Single().Chart.Path);
            using (var verifyPartialDb = new LR2SongDBExtended(songDbPath, SQLite.SQLiteOpenFlags.ReadOnly, acquireProcessLock: false))
            {
                Assert.AreEqual(packageDirectoryPath, verifyPartialDb.Table<LR2SongDBExtended.install>().Single().path);
            }

            library.RemovePendingCharts([nestedChart], sendToRecycleBin: true, deleteContainingPackageFoldersWhenNoBms: deleteWholePackage);

            Assert.IsFalse(File.Exists(nestedChartPath));
            Assert.AreEqual(!deleteWholePackage, Directory.Exists(packageDirectoryPath));
            Assert.AreEqual(!deleteWholePackage, File.Exists(resourcePath));
            if (deleteWholePackage)
            {
                CollectionAssert.AreEqual(new[] { packageDirectoryPath }, fileMutationService.DeleteDirectoryShellPaths);
                CollectionAssert.AreEqual(new[] { rootChartPath }, fileMutationService.DeleteFileShellPaths);
            }
            else
            {
                Assert.AreEqual(0, fileMutationService.DeleteDirectoryShellPaths.Count);
                CollectionAssert.AreEqual(new[] { rootChartPath, nestedChartPath }, fileMutationService.DeleteFileShellPaths);
            }
            Assert.AreEqual(0, library.ChartPackagesPending.Count);
            Assert.AreEqual(0, dialogService.Calls.Count);
            using var verifySongDb = new LR2SongDBExtended(songDbPath, SQLite.SQLiteOpenFlags.ReadOnly, acquireProcessLock: false);
            Assert.AreEqual(0, verifySongDb.Table<LR2SongDBExtended.install>().Count());
        });
    }

    /// <summary>再帰探索の有無にかかわらず独立した単一譜面パッケージをすべて選択しても、親フォルダや未選択の説明ファイルを削除しない。</summary>
    /// <remarks>実ファイルで物理変更と残存を観測する。ライブラリ経由のケースでは実SQLite保存行も確認し、ごみ箱そのものは局所portで代替する。</remarks>
    // falseは直下探索、trueは再帰探索。探索方法によって親を削除する権限を広げない。
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RemovePendingCharts_SingleFilePackagesKeepParentEvenWhenAllSelected(bool recursiveDiscovery)
    {

        WithTemporaryDirectory(tempDirectoryPath =>
        {
            string sourceDirectoryPath = Path.Combine(tempDirectoryPath, "IndependentCharts");
            Directory.CreateDirectory(sourceDirectoryPath);
            string firstPath = Path.Combine(sourceDirectoryPath, "a.bms");
            string secondPath = Path.Combine(sourceDirectoryPath, "b.bms");
            File.WriteAllText(firstPath, "#PLAYER 1\r\n#TITLE A\r\n#WAVAA absent_a.wav\r\n#00111:AA\r\n");
            File.WriteAllText(secondPath, "#PLAYER 1\r\n#TITLE B\r\n#WAVAA absent_b.wav\r\n#00111:AA\r\n");
            string readmePath = Path.Combine(sourceDirectoryPath, "readme.txt");
            File.WriteAllText(readmePath, "keep independent files' parent");
            var service = new BmsLibraryPackageInstallService();
            List<ChartPackage> packages = service.SearchChartPackagesRecursivelyWithMetadata(
                sourceDirectoryPath, 0.6, recursive: recursiveDiscovery).Packages;
            CollectionAssert.AreEquivalent(new[] { firstPath, secondPath }, packages.Select(package => package.path).ToArray());
            Assert.IsTrue(packages.All(package => package.delete_parent == recursiveDiscovery));
            List<ChartFile> charts = [.. packages.SelectMany(package => package.ChartEntries).Select(entry => entry.Chart)];
            var fileMutationService = new RecordingDeleteFileMutationService();
            var dialogService = new BmsLibraryInitializationTestSupport.RecordingDialogService();
            string songDbPath = Path.Combine(tempDirectoryPath, "song.db");
            BmsLibraryInitializationTestSupport.ExecuteSongDbFixtureTransaction(songDbPath, songDb =>
            {
                songDb.CreateTable<LR2SongDBExtended.install>();
                foreach (ChartPackage package in packages)
                {
                    songDb.InsertOrReplace(package, typeof(LR2SongDBExtended.install));
                }
            });
            var library = new TestBmsLibrary(songDbPath, null, null, fileMutationService, dialogService)
            {
                ChartPackagesPending = new System.Collections.ObjectModel.ObservableCollection<ChartPackage>(packages)
            };

            library.RemovePendingCharts(charts, sendToRecycleBin: true, deleteContainingPackageFoldersWhenNoBms: true);

            Assert.IsFalse(File.Exists(firstPath));
            Assert.IsFalse(File.Exists(secondPath));
            Assert.IsTrue(Directory.Exists(sourceDirectoryPath));
            Assert.AreEqual("keep independent files' parent", File.ReadAllText(readmePath));
            CollectionAssert.AreEquivalent(new[] { firstPath, secondPath }, fileMutationService.DeleteFileShellPaths);
            Assert.AreEqual(0, fileMutationService.DeleteDirectoryShellPaths.Count);
            Assert.AreEqual(0, fileMutationService.DeleteDirectoryDirectPaths.Count);
            Assert.AreEqual(0, library.ChartPackagesPending.Count);
            Assert.AreEqual(0, dialogService.Calls.Count);
            using var verifySongDb = new LR2SongDBExtended(songDbPath, SQLite.SQLiteOpenFlags.ReadOnly, acquireProcessLock: false);
            Assert.AreEqual(0, verifySongDb.Table<LR2SongDBExtended.install>().Count());
        });
    }

    /// <summary>拡張子変更の準備後に入力が消えた場合、失敗として集計して移動・重複削除へ進まず元のファイル不在を伝える。</summary>
    /// <remarks>実ファイルで物理変更と残存を観測する。ライブラリ経由のケースでは実SQLite保存行も確認し、ごみ箱そのものは局所portで代替する。</remarks>
    [TestMethod]
    public void InvalidExtensionPlan_LateMissingSourceIsFailureWithoutMutation()
    {
        WithTemporaryDirectory(tempDirectoryPath =>
        {
            string sourcePath = Path.Combine(tempDirectoryPath, "chart.bms");
            File.WriteAllText(sourcePath, "#PLAYER 1\r\n");
            ChartFile sourceFile = ChartTestValues.Empty() with
            {
                Path = sourcePath
            };
            sourceFile = sourceFile with { Md5 = new string('a', 32) };
            var service = new BmsLibraryLibraryFileOperationsService();
            var snapshot = LibraryFileOperationTargetSnapshot.FromChart(
                (sourceFile),
                captureSourceFileExistence: true);
            LegacyInvalidExtensionRenamePlan plan = service.BuildInvalidExtensionRenamePlan(
                [snapshot],
                ".bmx",
                rejectUnsafeSource: true);
            var fileMutationService = new RecordingDeleteFileMutationService();
            File.Delete(sourcePath);

            LegacyInvalidExtensionRenameExecutionResult result = service.ExecuteInvalidExtensionRenamePlan(
                plan,
                fileMutationService,
                null);

            Assert.AreEqual(1, result.SkippedCount);
            Assert.AreEqual(1, result.FailedCount);
            Assert.AreEqual(0, result.RenamedCount);
            Assert.AreEqual(0, result.DuplicateDeletedCount);
            Assert.AreEqual(0, fileMutationService.DeleteFileDirectPaths.Count);
            Assert.AreEqual(0, fileMutationService.MoveFilePaths.Count);
            Assert.IsInstanceOfType(result.Items[0].Outcome.FailureException, typeof(FileNotFoundException));
        });
    }

    /// <summary>拡張子変更で実symlinkを拒否し、移動・重複削除を行わず外部のリンク先を保持する。</summary>
    /// <remarks>実ファイルで物理変更と残存を観測する。ライブラリ経由のケースでは実SQLite保存行も確認し、ごみ箱そのものは局所portで代替する。</remarks>
    [TestMethod]
    public void InvalidExtensionPlan_ReparseSourceIsFailureWithoutMutation()
    {
        WithTemporaryDirectory(tempDirectoryPath =>
        {
            string targetPath = Path.Combine(tempDirectoryPath, "external.bms");
            File.WriteAllText(targetPath, "#PLAYER 1\r\n");
            string sourcePath = Path.Combine(tempDirectoryPath, "linked.bms");
            try
            {
                File.CreateSymbolicLink(sourcePath, targetPath);
            }
            catch (Exception exception) when (exception is IOException
                || exception is UnauthorizedAccessException
                || exception is PlatformNotSupportedException)
            {
                Assert.Inconclusive("実ファイルsymlinkを作成できない環境のため、この安全境界の実入力検証は未実施: " + exception.Message);
                return;
            }
            ChartFile sourceFile = ChartTestValues.Empty() with
            {
                Path = sourcePath
            };
            sourceFile = sourceFile with { Md5 = new string('a', 32) };
            var service = new BmsLibraryLibraryFileOperationsService();
            var snapshot = LibraryFileOperationTargetSnapshot.FromChart(
                (sourceFile),
                captureSourceFileExistence: true);
            LegacyInvalidExtensionRenamePlan plan = service.BuildInvalidExtensionRenamePlan(
                [snapshot],
                ".bmx",
                rejectUnsafeSource: true);
            var fileMutationService = new RecordingDeleteFileMutationService();

            LegacyInvalidExtensionRenameExecutionResult result = service.ExecuteInvalidExtensionRenamePlan(
                plan,
                fileMutationService,
                null);

            Assert.AreEqual(1, result.SkippedCount);
            Assert.AreEqual(1, result.FailedCount);
            Assert.AreEqual(0, result.RenamedCount);
            Assert.AreEqual(0, result.DuplicateDeletedCount);
            Assert.AreEqual(0, fileMutationService.DeleteFileDirectPaths.Count);
            Assert.AreEqual(0, fileMutationService.MoveFilePaths.Count);
            Assert.IsInstanceOfType(result.Items[0].Outcome.FailureException, typeof(IOException));
            Assert.IsTrue(File.Exists(targetPath));
        });
    }

    private static ChartFile CreateChart(string directoryPath, string fileName, string hashSeed)
    {
        string filePath = Path.Combine(directoryPath, fileName);
        File.WriteAllText(filePath, "#PLAYER 1\r\n#TITLE " + fileName + "\r\n");
        ChartFile file = ChartTestValues.Empty() with
        {
            Path = filePath
        };
        file = file with { Md5 = hashSeed.PadRight(32, hashSeed[0]) };
        return file;
    }

    private static void WithTemporaryDirectory(Action<string> action)
    {
        string directoryPath = Path.Combine(
            Path.GetTempPath(),
            "BeMusicSeeker_PendingLegacyTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directoryPath);
        try
        {
            action(directoryPath);
        }
        finally
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
    }


    private sealed class RecordingDeleteFileMutationService : IFileMutationService
    {
        private readonly string? failurePath;

        private readonly string? directoryFailurePath;

        /// <summary>実変更を記録し、指定された一つのファイルまたはディレクトリを変更前に失敗させる。</summary>
        internal RecordingDeleteFileMutationService(string? failurePath = null, string? directoryFailurePath = null)
        {
            this.failurePath = failurePath;
            this.directoryFailurePath = directoryFailurePath;
        }

        internal List<string> DeleteFileDirectPaths { get; } = [];

        internal List<string> DeleteFileShellPaths { get; } = [];

        internal List<string> DeleteDirectoryDirectPaths { get; } = [];

        internal List<string> DeleteDirectoryShellPaths { get; } = [];

        /// <summary>実機のごみ箱を使わず、要求された削除方針を記録する。</summary>
        internal List<RecycleOption> DeleteDirectoryRecycleOptions { get; } = [];

        /// <summary>ディレクトリ操作へ渡された属性正規化の範囲を記録する。</summary>
        internal List<ReadOnlyNormalizationScope> DeleteDirectoryNormalizationScopes { get; } = [];

        internal List<string> MoveFilePaths { get; } = [];

        public void EnsureDirectory(string directoryPath, FileMutationOptions? options = null)
        {
            if (!string.IsNullOrWhiteSpace(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }
        }

        public void MoveFile(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions? options = null)
        {
            MoveFilePaths.Add(sourcePath);
            throw new NotSupportedException();
        }

        public void MoveDirectory(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions? options = null)
        {
            throw new NotSupportedException();
        }

        public void CopyFile(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions? options = null)
        {
            throw new NotSupportedException();
        }

        public void CopyDirectory(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions? options = null)
        {
            throw new NotSupportedException();
        }

        public void DeleteFileDirect(string filePath, FileMutationOptions? options = null)
        {
            DeleteFileDirectPaths.Add(filePath);
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }

        public void DeleteFileShell(string filePath, UIOption uiOption, RecycleOption recycleOption, FileMutationOptions? options = null)
        {
            DeleteFileShellPaths.Add(filePath);
            if (string.Equals(filePath, failurePath, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("injected child delete failure");
            }
            DeleteFileDirect(filePath, options);
        }

        public void DeleteDirectoryDirect(string directoryPath, bool recursive, FileMutationOptions? options = null)
        {
            DeleteDirectoryDirectPaths.Add(directoryPath);
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive);
            }
        }

        public void DeleteDirectoryShell(string directoryPath, UIOption uiOption, RecycleOption recycleOption, FileMutationOptions? options = null)
        {
            DeleteDirectoryShellPaths.Add(directoryPath);
            DeleteDirectoryRecycleOptions.Add(recycleOption);
            DeleteDirectoryNormalizationScopes.Add(options?.ReadOnlyNormalizationScope ?? ReadOnlyNormalizationScope.None);
            if (string.Equals(directoryPath, directoryFailurePath, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("injected package delete failure");
            }
            DeleteDirectoryDirect(directoryPath, recursive: true, options);
        }

        public void SetTimestamps(string path, bool isDirectory, DateTime? creationTime, DateTime? lastWriteTime, FileMutationOptions? options = null)
        {
        }
    }
}
