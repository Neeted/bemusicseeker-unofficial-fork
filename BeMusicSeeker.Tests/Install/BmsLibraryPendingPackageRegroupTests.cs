using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BmsLibraryPendingPackageRegroupTests
{
    private readonly BeMusicSeeker.Properties.Settings testSettings = MainWindowViewModelTestFactory.CreateIsolatedSettings();

    /// <summary>分割した二つの保留に同じ導入先があっても、単一パッケージの再推定が公開集合と保存行を勝手に結合しない。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void SearchEstimatedInstallationDirectory_DoesNotRegroupSplitPackagesWhenPendingDestinationsMatch()
    {

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageA");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Installed", "PackageA");
            ChartPackage firstPackage = CreatePendingSingleFilePackage(CreateBmsFile(sourceDirectoryPath, "a.bms", "Same A"), destinationDirectoryPath);
            ChartPackage secondPackage = CreatePendingSingleFilePackage(CreateBmsFile(sourceDirectoryPath, "b.bms", "Same B"), destinationDirectoryPath);

            library.BmsCharts = [];
            SeedPendingPackages(library, songDbPath, firstPackage, secondPackage);

            library.SearchEstimatedInstallationDirectory(firstPackage);

            AssertPendingPackagePaths(library, firstPackage.path, secondPackage.path);
            CollectionAssert.AreEquivalent(new[] { firstPackage.path, secondPackage.path }, LoadInstallPaths(songDbPath));
        });
    }

    /// <summary>譜面指定の再推定でも、同じ導入先を持つ別の保留パッケージを公開集合や保存行から失わせない。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void SearchEstimatedInstallationDirectoryByFile_DoesNotRegroupSplitPackagesWhenPendingDestinationsMatch()
    {

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageB");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Installed", "PackageB");
            ChartPackage firstPackage = CreatePendingSingleFilePackage(CreateBmsFile(sourceDirectoryPath, "a.bms", "Same A"), destinationDirectoryPath);
            ChartPackage secondPackage = CreatePendingSingleFilePackage(CreateBmsFile(sourceDirectoryPath, "b.bms", "Same B"), destinationDirectoryPath);

            library.BmsCharts = [];
            SeedPendingPackages(library, songDbPath, firstPackage, secondPackage);

            library.SearchEstimatedInstallationDirectory(PackageChartEntry.FromChart((firstPackage.GetBmsChartsForTest().Single())), asParallel: false, fixMode: false);

            AssertPendingPackagePaths(library, firstPackage.path, secondPackage.path);
            CollectionAssert.AreEquivalent(new[] { firstPackage.path, secondPackage.path }, LoadInstallPaths(songDbPath));
        });
    }

    /// <summary>別entryから同じBmsonパスを指定すると、保留パッケージを解決して保留理由を解除し、対象パスを保持する。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void SearchEstimatedInstallationDirectoryByFiles_MatchesAdapterlessBmsonPackageByChartPath()
    {

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageBmsonAdapterless");
            string chartPath = CreateBmsonFile(sourceDirectoryPath, "chart.bmson", "Adapterless", "Test");
            ChartFile bmsonSong = ChartTestValues.ReadBmson(chartPath);
            var pendingPackage = ChartPackage.FromChartEntries(
            [
                PackageChartEntry.FromChart((bmsonSong))
            ]);
            pendingPackage.path = sourceDirectoryPath;
            pendingPackage.delete_parent = false;
            pendingPackage.DeferredEstimateReason = PendingEstimateDeferredReason.HealthySourceBaseline;
            var selectedChart = PackageChartEntry.FromChart((bmsonSong));

            library.BmsCharts = [];
            SeedPendingPackages(library, songDbPath, pendingPackage);


            library.SearchEstimatedInstallationDirectory([selectedChart], asParallel: false, fixMode: false);

            PackageChartEntry entry = pendingPackage.ChartEntries.Single();
            Assert.AreEqual(PendingEstimateDeferredReason.None, pendingPackage.DeferredEstimateReason);

            Assert.AreEqual(chartPath, entry.Chart.Path);
        });
    }



    /// <summary>二件の一括推定が外側並列・内側単一の契約で両対象を処理し、対象名付きの進捗を全件適用から非稼働へ終える。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void SearchEstimatedInstallationDirectory_MultiPackageBatchReportsExecutionPolicyAndProgress()
    {

        const int packageCount = 2;
        int expectedWorkItemDegree = Math.Max(1, Environment.ProcessorCount - 1);
        int expectedMaxActive = Math.Min(packageCount, expectedWorkItemDegree);
        using var observer = new RecordingInstallEstimationExecutionObserver(expectedMaxActive);

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string firstSourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "ObserverFirst");
            string secondSourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "ObserverSecond");
            string firstPendingPath = CreateBmsFile(firstSourceDirectoryPath, "first.bms", "Observer First");
            string secondPendingPath = CreateBmsFile(secondSourceDirectoryPath, "second.bms", "Observer Second");
            ChartPackage firstPackage = CreatePendingSingleFilePackage(firstPendingPath);
            ChartPackage secondPackage = CreatePendingSingleFilePackage(secondPendingPath);

            // 未所持の対応譜面を一件ずつ持つ二つのパッケージから、二件の評価要求を実受付する。
            library.BmsCharts = [];
            SeedPendingPackages(library, songDbPath, firstPackage, secondPackage);

            library.SearchEstimatedInstallationDirectory([firstPackage, secondPackage]);
        }, observer);

        Assert.AreEqual(packageCount, observer.WorkItems.Count);
        Assert.AreEqual(expectedMaxActive, observer.MaxActive);
        CollectionAssert.AreEqual(
            Enumerable.Range(0, packageCount).ToArray(),
            observer.WorkItems.Select(item => item.OrderIndex).OrderBy(order => order).ToArray());
        Assert.IsTrue(observer.WorkItems.All(item => item.Source == PendingInstallEstimateBatchSource.ManualReestimate));
        Assert.IsTrue(observer.WorkItems.All(item => item.WorkItemDegree == expectedWorkItemDegree));
        Assert.IsTrue(observer.WorkItems.All(item => item.CandidateEvaluationDegree == 1));
        CollectionAssert.AreEquivalent(
            new[] { "first.bms", "second.bms" },
            observer.WorkItems.Select(item => item.DisplayName).ToArray());

        List<InstallEstimationProgressObservation> activeProgress =
            [.. observer.Progress.Where(progress => progress.IsActive)];
        Assert.IsTrue(activeProgress.Count > 0);
        Assert.IsTrue(activeProgress.All(progress =>
            progress.Source == InstallEstimationProgressSource.ManualReestimate
            && progress.TotalWorkCount == packageCount
            && progress.CompletedWorkCount >= 0
            && progress.CompletedWorkCount <= packageCount));
        CollectionAssert.AreEqual(
            Enumerable.Range(0, packageCount + 1).ToArray(),
            activeProgress.Select(progress => progress.CompletedWorkCount).Distinct().ToArray());
        InstallEstimationProgressObservation inactiveProgress = observer.Progress.Last();
        Assert.IsFalse(inactiveProgress.IsActive);
        Assert.AreEqual(InstallEstimationProgressSource.None, inactiveProgress.Source);
        Assert.AreEqual(0, inactiveProgress.TotalWorkCount);
        Assert.AreEqual(0, inactiveProgress.CompletedWorkCount);

        Assert.AreEqual(packageCount, observer.Attempts.Count);
        Assert.IsTrue(observer.Attempts.All(attempt =>
            attempt.Source == PendingInstallEstimateBatchSource.ManualReestimate
            && !string.IsNullOrWhiteSpace(attempt.DisplayName)));
        CollectionAssert.AreEqual(
            Enumerable.Range(0, packageCount).ToArray(),
            observer.Attempts.Select(attempt => attempt.OrderIndex).OrderBy(order => order).ToArray());
    }

    /// <summary>一括推定の観測先が失敗したとき、元例外をそのまま呼出し元へ伝え、正常終了に置き換えない。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void SearchEstimatedInstallationDirectory_ObserverFailureIsPropagated()
    {

        var observer = new ThrowingInstallEstimationExecutionObserver();
        InvalidOperationException failure = Assert.ThrowsException<InvalidOperationException>(() =>
            WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
            {
                ChartPackage firstPackage = CreatePendingSingleFilePackage(
                    CreateBmsFile(Path.Combine(tempRootPath, "Pending", "FailureFirst"), "first.bms", "Failure First"));
                ChartPackage secondPackage = CreatePendingSingleFilePackage(
                    CreateBmsFile(Path.Combine(tempRootPath, "Pending", "FailureSecond"), "second.bms", "Failure Second"));
                library.BmsCharts = [];
                SeedPendingPackages(library, songDbPath, firstPackage, secondPackage);

                library.SearchEstimatedInstallationDirectory([firstPackage, secondPackage]);
            }, observer));
        Assert.AreSame(observer.Failure, failure);
    }

    /// <summary>単一譜面の相対リソースから入力元を除く導入先を選び、無関係な下位フォルダの走査警告を付けない。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void SearchEstimatedInstallationDirectory_SingleFileRelativeResourceSelectsExternalCandidateWithoutSourceScanWarning()
    {

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "RelativeSingleFile");
            string candidateDirectoryPath = Path.Combine(tempRootPath, "Installed", "RelativeCandidate");
            string sourceSoundDirectoryPath = Path.Combine(sourceDirectoryPath, "sound");
            string candidateSoundDirectoryPath = Path.Combine(candidateDirectoryPath, "sound");
            string unrelatedNestedDirectoryPath = Path.Combine(sourceDirectoryPath, "Unrelated", "Nested");
            Directory.CreateDirectory(sourceSoundDirectoryPath);
            Directory.CreateDirectory(candidateSoundDirectoryPath);
            Directory.CreateDirectory(unrelatedNestedDirectoryPath);
            string pendingPath = CreateBmsFileWithContents(
                sourceDirectoryPath,
                "pending.bms",
                "#PLAYER 1\r\n#TITLE Relative\r\n#ARTIST Test\r\n#WAVAA sound/00.wav\r\n#00111:AA\r\n");
            string candidatePath = CreateBmsFileWithContents(
                candidateDirectoryPath,
                "candidate.bms",
                "#PLAYER 1\r\n#TITLE Relative\r\n#ARTIST Test\r\n");
            File.WriteAllText(Path.Combine(sourceSoundDirectoryPath, "00.wav"), "source");
            File.WriteAllText(Path.Combine(candidateSoundDirectoryPath, "00.wav"), "candidate");
            File.WriteAllText(Path.Combine(unrelatedNestedDirectoryPath, "noise.bin"), "noise");
            ChartPackage pendingPackage = CreatePendingSingleFilePackage(pendingPath);
            library.BmsCharts = [BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(candidatePath))];
            SeedPendingPackages(library, songDbPath, pendingPackage);
            var cache = new DirectoryResourceLookupCache();
            cache.AddDir(sourceDirectoryPath, ["pending.bms", Path.Combine("sound", "00.wav")]);
            cache.AddDir(candidateDirectoryPath, ["candidate.bms", Path.Combine("sound", "00.wav")]);
            SetLibraryResourceIndex(library, cache);

            library.SearchEstimatedInstallationDirectory(pendingPackage);

            PackageChartEntry entry = GetOnlyEntry(pendingPackage);
            Assert.AreEqual(candidateDirectoryPath, entry.Chart.InstallDestination);
            Assert.AreNotEqual(sourceDirectoryPath, entry.Chart.InstallDestination);
            Assert.IsTrue(entry.ResourceSnapshot.AudioReferences.Single().IsPathAware);
            Assert.IsFalse(entry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.SourceSurfaceScanLimitExceeded));
        });
    }

    /// <summary>明示された適格ディレクトリの分割保留を二譜面の一パッケージに再編成し、保存行もディレクトリ単位へ揃える。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void TryRegroupPendingPackagesForSourceDirectories_RegroupsSplitPackagesWhenEligibleDirectoryIsSupplied()
    {

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageC");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Installed", "PackageC");
            ChartPackage firstPackage = CreatePendingSingleFilePackage(CreateBmsFile(sourceDirectoryPath, "a.bms", "Same A"), destinationDirectoryPath);
            ChartPackage secondPackage = CreatePendingSingleFilePackage(CreateBmsFile(sourceDirectoryPath, "b.bms", "Same B"), destinationDirectoryPath);

            library.BmsCharts = [];
            SeedPendingPackages(library, songDbPath, firstPackage, secondPackage);

            InvokeRegroupForSourceDirectories(library, sourceDirectoryPath);

            AssertRegroupedPendingPackage(library, sourceDirectoryPath, destinationDirectoryPath, expectedFileCount: 2);
            CollectionAssert.AreEqual(new[] { sourceDirectoryPath }, LoadInstallPaths(songDbPath));
        });
    }

    /// <summary>BMSとBmsonの分割保留を同じディレクトリへ再編成しても、所持Bmsonに未所持BMSの導入先を転用しない。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void TryRegroupPendingPackagesForSourceDirectories_UsesAdapterlessBmsonEntriesForEligibility()
    {

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageBmsonRegroup");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Installed", "PackageBmsonRegroup");
            string pendingBmsonPath = CreateBmsonFile(sourceDirectoryPath, "pending.bmson", "Regroup Bmson", "Bmson Artist");
            ChartFile pendingSong = ChartTestValues.ReadBmson(pendingBmsonPath);
            var adapterlessPackage = ChartPackage.FromChartEntries(
            [
                PackageChartEntry.FromChart((pendingSong))
            ]);
            adapterlessPackage.path = pendingBmsonPath;
            adapterlessPackage.delete_parent = true;
            ChartPackage bmsPackage = CreatePendingSingleFilePackage(CreateBmsFile(sourceDirectoryPath, "chart.bms", "Regroup Bms"), destinationDirectoryPath);
            string installedBmsonPath = CreateBmsonFile(destinationDirectoryPath, "installed.bmson", "Regroup Bmson", "Bmson Artist");
            ChartFile installedSong = ChartTestValues.ReadBmson(installedBmsonPath);


            library.BmsCharts = [];
            library.BmsonCharts = [installedSong];
            SeedPendingPackages(library, songDbPath, adapterlessPackage, bmsPackage);

            InvokeRegroupForSourceDirectories(library, sourceDirectoryPath);

            ChartPackage regroupedPackage = AssertRegroupedPendingPackage(library, sourceDirectoryPath, expectedFileCount: 2);
            PackageChartEntry regroupedBmson = regroupedPackage.ChartEntries
                .Single(entry => entry.Chart.Kind == ChartFileKind.Bmson);
            PackageChartEntry regroupedBms = regroupedPackage.ChartEntries
                .Single(entry => entry.Chart.Kind == ChartFileKind.Bms);
            Assert.AreEqual(pendingBmsonPath, regroupedBmson.Chart.Path);

            Assert.IsTrue(string.IsNullOrWhiteSpace(regroupedBmson.Chart.InstallDestination));
            Assert.AreEqual(destinationDirectoryPath, regroupedBms.Chart.InstallDestination);
            CollectionAssert.AreEqual(new[] { sourceDirectoryPath }, LoadInstallPaths(songDbPath));
        });
    }

    /// <summary>古い捕捉パスを持つentryも現在のBmson対象で重複排除し、再編成した公開集合と保存行に一件だけ残す。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void TryRegroupPendingPackagesForSourceDirectories_DeduplicatesAdapterlessBmsonByChartTarget()
    {

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageBmsonDuplicateRegroup");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Installed", "PackageBmsonDuplicateRegroup");
            string pendingBmsonPath = CreateHealthyBmsonFile(sourceDirectoryPath, "pending.bmson", "Duplicate Bmson", "Bmson Artist");
            ChartFile pendingSong = ChartTestValues.ReadBmson(pendingBmsonPath);
            var firstEntry = PackageChartEntry.FromChart((pendingSong));
            string staleSnapshotPath = Path.Combine(sourceDirectoryPath, "pending-stale-snapshot.bmson");
            ChartFile staleCapture = WithChartPath(pendingSong, staleSnapshotPath);
            var secondEntry = PackageChartEntry.FromChart(staleCapture);
            secondEntry.ApplyCurrentChart(pendingSong);
            var firstPackage = ChartPackage.FromChartEntries([firstEntry]);
            firstPackage.path = pendingBmsonPath;
            firstPackage.delete_parent = true;
            var secondPackage = ChartPackage.FromChartEntries([secondEntry]);
            secondPackage.path = staleSnapshotPath;
            secondPackage.delete_parent = true;
            string installedBmsonPath = CreateHealthyBmsonFile(destinationDirectoryPath, "installed.bmson", "Duplicate Bmson", "Bmson Artist");
            ChartFile installedSong = ChartTestValues.ReadBmson(installedBmsonPath);



            library.BmsCharts = [];
            library.BmsonCharts = [installedSong];
            SeedPendingPackages(library, songDbPath, firstPackage, secondPackage);

            InvokeRegroupForSourceDirectories(library, sourceDirectoryPath);

            ChartPackage regroupedPackage = library.ChartPackagesPending.Single();
            Assert.AreEqual(sourceDirectoryPath, regroupedPackage.path);
            Assert.AreEqual(1, regroupedPackage.ChartEntries.Count);

            Assert.AreEqual(pendingBmsonPath, regroupedPackage.ChartEntries.Single().Chart.Path);
            Assert.IsTrue(string.IsNullOrWhiteSpace(regroupedPackage.ChartEntries.Single().Chart.InstallDestination));
            CollectionAssert.AreEqual(new[] { sourceDirectoryPath }, LoadInstallPaths(songDbPath));
        });
    }

    /// <summary>全譜面が所持済みの再編成では、導入先を付けず所持済み警告を各entryに残して保存行を結合する。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void TryRegroupPendingPackagesForSourceDirectories_DoesNotApplyInstallDestinationToInstalledOnlyEntries()
    {

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "InstalledOnlyRegroup");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Installed", "InstalledOnlyRegroup");
            string firstContents = "#PLAYER 1\r\n#TITLE Installed A\r\n#ARTIST Test\r\n";
            string secondContents = "#PLAYER 1\r\n#TITLE Installed B\r\n#ARTIST Test\r\n";
            string pendingAPath = CreateBmsFileWithContents(sourceDirectoryPath, "a.bms", firstContents);
            string pendingBPath = CreateBmsFileWithContents(sourceDirectoryPath, "b.bms", secondContents);
            string installedAPath = CreateBmsFileWithContents(destinationDirectoryPath, "installed-a.bms", firstContents);
            string installedBPath = CreateBmsFileWithContents(destinationDirectoryPath, "installed-b.bms", secondContents);
            ChartPackage firstPackage = CreatePendingSingleFilePackage(pendingAPath);
            ChartPackage secondPackage = CreatePendingSingleFilePackage(pendingBPath);

            library.BmsCharts =
            [
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(installedAPath)),
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(installedBPath))
            ];
            SeedPendingPackages(library, songDbPath, firstPackage, secondPackage);

            InvokeRegroupForSourceDirectories(library, sourceDirectoryPath);

            ChartPackage regroupedPackage = AssertRegroupedPendingPackage(library, sourceDirectoryPath, expectedFileCount: 2);
            Assert.IsTrue(regroupedPackage.ChartEntries.All(entry => string.IsNullOrWhiteSpace(entry.Chart.InstallDestination)));
            Assert.IsTrue(regroupedPackage.ChartEntries.All(entry => entry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.AlreadyInstalled)));
            CollectionAssert.AreEqual(new[] { sourceDirectoryPath }, LoadInstallPaths(songDbPath));
        });
    }

    /// <summary>所持・未所持の混在を再編成すると、未所持entryだけへ導入先を付け、所持entryの警告と空の導入先を保つ。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void TryRegroupPendingPackagesForSourceDirectories_AppliesInstallDestinationOnlyToMissingEntries()
    {

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "MixedRegroup");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Installed", "MixedRegroup");
            string installedContents = "#PLAYER 1\r\n#TITLE Installed\r\n#ARTIST Test\r\n";
            string pendingInstalledPath = CreateBmsFileWithContents(sourceDirectoryPath, "installed.bms", installedContents);
            string pendingMissingPath = CreateBmsFile(sourceDirectoryPath, "missing.bms", "Missing");
            string installedPath = CreateBmsFileWithContents(destinationDirectoryPath, "installed.bms", installedContents);
            ChartPackage installedPackage = CreatePendingSingleFilePackage(pendingInstalledPath);
            ChartPackage missingPackage = CreatePendingSingleFilePackage(pendingMissingPath, destinationDirectoryPath);

            library.BmsCharts =
            [
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(installedPath))
            ];
            SeedPendingPackages(library, songDbPath, installedPackage, missingPackage);

            InvokeRegroupForSourceDirectories(library, sourceDirectoryPath);

            ChartPackage regroupedPackage = AssertRegroupedPendingPackage(library, sourceDirectoryPath, expectedFileCount: 2);
            PackageChartEntry installedEntry = GetEntryByFileName(regroupedPackage, "installed.bms");
            PackageChartEntry missingEntry = GetEntryByFileName(regroupedPackage, "missing.bms");
            Assert.IsTrue(string.IsNullOrWhiteSpace(installedEntry.Chart.InstallDestination));
            Assert.IsTrue(installedEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.AlreadyInstalled));
            Assert.AreEqual(destinationDirectoryPath, missingEntry.Chart.InstallDestination);
            Assert.IsFalse(missingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.AlreadyInstalled));
            CollectionAssert.AreEqual(new[] { sourceDirectoryPath }, LoadInstallPaths(songDbPath));
        });
    }

    /// <summary>再編成後は単一譜面警告を除き、欠落WAVの警告を再評価して正常な隣接entryへ波及させない。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void TryRegroupPendingPackagesForSourceDirectories_ReinitializesWarningsWhenEligibleDirectoryIsSupplied()
    {

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageD");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Installed", "PackageD");
            ChartPackage strictWarningPackage = CreatePendingSingleFilePackage(
                CreateBmsFileWithContents(
                    sourceDirectoryPath,
                    "strict.bms",
                    "#PLAYER 1\r\n#TITLE Strict\r\n#ARTIST Test\r\n#WAVAA sound_missing.wav\r\n#00111:AA\r\n"),
                destinationDirectoryPath);
            ChartPackage normalPackage = CreatePendingSingleFilePackage(CreateBmsFile(sourceDirectoryPath, "normal.bms", "Normal"), destinationDirectoryPath);

            library.BmsCharts = [];
            ApplySingleFileWarnings(strictWarningPackage, normalPackage);
            SeedPendingPackages(library, songDbPath, strictWarningPackage, normalPackage);

            InvokeRegroupForSourceDirectories(library, sourceDirectoryPath);

            ChartPackage regroupedPackage = AssertRegroupedPendingPackage(library, sourceDirectoryPath, destinationDirectoryPath, expectedFileCount: 2);
            PackageChartEntry strictWarningEntry = GetEntryByFileName(regroupedPackage, "strict.bms");
            PackageChartEntry normalEntry = GetEntryByFileName(regroupedPackage, "normal.bms");
            Assert.IsTrue(strictWarningEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
            StringAssert.Contains(ChartWarningTestHelpers.BuildTooltipText(strictWarningEntry), "WAV");
            Assert.IsFalse(strictWarningEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.SingleBmsFile));
            Assert.AreEqual(string.Empty, ChartWarningTestHelpers.BuildDigestText(normalEntry));
        });
    }

    /// <summary>再編成の導入先から代表タイトルと作者を全entryへ反映しても、元entryの候補一覧を消さない。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void TryRegroupPendingPackagesForSourceDirectories_SynchronizesRepresentativeMetadataWithoutClearingSuggestions()
    {

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageRegroupMetadata");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Installed", "PackageRegroupMetadata");
            string minorityPath = CreateBmsFileWithContents(destinationDirectoryPath, "a.bms", "#TITLE Minority\r\n#ARTIST Other\r\n");
            string installedFilePath = CreateBmsFileWithContents(destinationDirectoryPath, "installed.bms", "#PLAYER 1\r\n#TITLE Installed Title\r\n#SUBTITLE Alternate Name\r\n#ARTIST Installed Artist OBJ: Diff\r\n");
            string majorityPath = CreateBmsFileWithContents(destinationDirectoryPath, "z.bms", "#TITLE Installed Title\r\n#ARTIST Installed Artist\r\n");
            ChartPackage firstPackage = CreatePendingSingleFilePackage(CreateBmsFile(sourceDirectoryPath, "a.bms", "Same A"), destinationDirectoryPath);
            ChartPackage secondPackage = CreatePendingSingleFilePackage(CreateBmsFile(sourceDirectoryPath, "b.bms", "Same B"), destinationDirectoryPath);
            PackageChartEntry firstPendingEntry = GetOnlyEntry(firstPackage);
            firstPendingEntry.RestoreInstallDestinationState(new PackageChartInstallDestinationState(destinationDirectoryPath, string.Empty, string.Empty, [destinationDirectoryPath]));
            firstPendingEntry.SetWarning(ChartWarningKind.InstallEstimationLowConfidence, BeMusicSeeker.Properties.Resources.WarningDigest_InstallEstimationLowConfidence);

            library.BmsCharts = [.. new[] { minorityPath, installedFilePath, majorityPath }
                .Select(path => BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(path)))];
            SeedPendingPackages(library, songDbPath, firstPackage, secondPackage);

            InvokeRegroupForSourceDirectories(library, sourceDirectoryPath);

            ChartPackage regroupedPackage = AssertRegroupedPendingPackage(library, sourceDirectoryPath, destinationDirectoryPath, expectedFileCount: 2);
            foreach (PackageChartEntry regroupedEntry in regroupedPackage.ChartEntries)
            {
                Assert.AreEqual("Installed Title", regroupedEntry.Chart.InstallDestinationTitle);
                Assert.AreEqual("Installed Artist", regroupedEntry.Chart.InstallDestinationArtist);
            }
            Assert.AreEqual(1, firstPendingEntry.Chart.InstallDestinationSuggestions.Count);
            Assert.AreEqual(destinationDirectoryPath, firstPendingEntry.Chart.InstallDestinationSuggestions.Single());
        });
    }

    /// <summary>再編成のDB保存を失敗させると、元entryの導入先・候補・警告と二つの公開パッケージを変更しない。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void TryRegroupPendingPackagesForSourceDirectories_DurableFailureLeavesSourceEntryStateUnchanged()
    {

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageRegroupDbFailure");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Installed", "PackageRegroupDbFailure");
            string installedFilePath = CreateBmsFileWithContents(
                destinationDirectoryPath,
                "representative.bms",
                "#PLAYER 1\r\n#TITLE Resolved Title\r\n#ARTIST Resolved Artist\r\n");
            ChartPackage firstPackage = CreatePendingSingleFilePackage(CreateBmsFile(sourceDirectoryPath, "a.bms", "Same A"), destinationDirectoryPath);
            ChartPackage secondPackage = CreatePendingSingleFilePackage(CreateBmsFile(sourceDirectoryPath, "b.bms", "Same B"), destinationDirectoryPath);
            PackageChartEntry firstPendingEntry = GetOnlyEntry(firstPackage);
            firstPendingEntry.RestoreInstallDestinationState(new PackageChartInstallDestinationState(
                destinationDirectoryPath,
                "Original Title",
                "Original Artist",
                [Path.Combine(tempRootPath, "Suggested")]));
            firstPendingEntry.SetWarning(ChartWarningKind.InstallEstimationLowConfidence, BeMusicSeeker.Properties.Resources.WarningDigest_InstallEstimationLowConfidence);
            PackageChartInstallDestinationState expectedDestinationState = firstPendingEntry.CaptureInstallDestinationState();
            string[] expectedWarnings = [.. firstPendingEntry.Chart.Warnings.Select(warning => warning.Kind + "|" + warning.Message)];

            library.BmsCharts = [BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(installedFilePath))];
            SeedPendingPackages(library, songDbPath, firstPackage, secondPackage);

            // 再編成結果の準備後、最初の永続反映をDBパスのディレクトリで失敗させ、元entryへの先行適用を検出する。
            File.Delete(songDbPath);
            Directory.CreateDirectory(songDbPath);

            TargetInvocationException exception = Assert.ThrowsException<TargetInvocationException>(
                () => InvokeRegroupForSourceDirectories(library, sourceDirectoryPath));
            Assert.IsInstanceOfType<SQLite.SQLiteException>(exception.InnerException);

            PackageChartInstallDestinationState actualDestinationState = firstPendingEntry.CaptureInstallDestinationState();
            Assert.AreEqual(expectedDestinationState.Destination, actualDestinationState.Destination);
            Assert.AreEqual(expectedDestinationState.Title, actualDestinationState.Title);
            Assert.AreEqual(expectedDestinationState.Artist, actualDestinationState.Artist);
            CollectionAssert.AreEqual(expectedDestinationState.Suggestions.ToArray(), actualDestinationState.Suggestions.ToArray());
            CollectionAssert.AreEqual(
                expectedWarnings,
                firstPendingEntry.Chart.Warnings.Select(warning => warning.Kind + "|" + warning.Message).ToArray());
            CollectionAssert.AreEqual(new[] { firstPackage, secondPackage }, library.ChartPackagesPending.ToArray());
        });
    }

    /// <summary>同じ入力元のディレクトリパッケージが既存なら、分割保留を追加結合せず公開集合と保存行を保つ。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void TryRegroupPendingPackagesForSourceDirectories_DoesNotRegroupWhenDirectoryPackageAlreadyExists()
    {

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageE");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Installed", "PackageE");
            CreateBmsFile(sourceDirectoryPath, "a.bms", "Same A");
            CreateBmsFile(sourceDirectoryPath, "b.bms", "Same B");
            var directoryPackage = new ChartPackage
            {
                path = sourceDirectoryPath,
                delete_parent = false
            };
            ChartPackage splitPackage = CreatePendingSingleFilePackage(Path.Combine(sourceDirectoryPath, "a.bms"), destinationDirectoryPath);

            library.BmsCharts = [];
            SeedPendingPackages(library, songDbPath, directoryPackage, splitPackage);

            InvokeRegroupForSourceDirectories(library, sourceDirectoryPath);

            AssertPendingPackagePaths(library, sourceDirectoryPath, splitPackage.path);
            CollectionAssert.AreEquivalent(new[] { sourceDirectoryPath, splitPackage.path }, LoadInstallPaths(songDbPath));
        });
    }

    /// <summary>健全な入力元として推定保留になったパッケージが混在すると、明示ディレクトリでも分割状態と保存行を維持する。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void TryRegroupPendingPackagesForSourceDirectories_SkipsDeferredEstimatePackages()
    {

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageDeferred");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Installed", "PackageDeferred");
            ChartPackage firstPackage = CreatePendingSingleFilePackage(CreateBmsFile(sourceDirectoryPath, "a.bms", "Same A"), destinationDirectoryPath);
            ChartPackage secondPackage = CreatePendingSingleFilePackage(CreateBmsFile(sourceDirectoryPath, "b.bms", "Same B"), destinationDirectoryPath);
            firstPackage.DeferredEstimateReason = PendingEstimateDeferredReason.HealthySourceBaseline;

            library.BmsCharts = [];
            SeedPendingPackages(library, songDbPath, firstPackage, secondPackage);

            InvokeRegroupForSourceDirectories(library, sourceDirectoryPath);

            AssertPendingPackagePaths(library, firstPackage.path, secondPackage.path);
            CollectionAssert.AreEquivalent(new[] { firstPackage.path, secondPackage.path }, LoadInstallPaths(songDbPath));
        });
    }

    /// <summary>Strongが成立しない二候補では導入先を未確定にし、代表メタデータ・両候補・不一致理由を表示用警告へ残す。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void SearchEstimatedInstallationDirectory_LowConfidenceLeavesInstallDestinationEmptyAndShowsRepresentativeMetadata()
    {

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageLowConfidence");
            string candidateADirectoryPath = Path.Combine(tempRootPath, "Installed", "A");
            string candidateBDirectoryPath = Path.Combine(tempRootPath, "Installed", "B");
            string pendingFilePath = CreateBmsFileWithContents(sourceDirectoryPath, "pending.bms", "#PLAYER 1\r\n#TITLE Pending\r\n#ARTIST Test\r\n#WAVAA sound.wav\r\n#00111:AA\r\n");
            CreateBmsFileWithContents(candidateADirectoryPath, "candidateA.bms", "#PLAYER 1\r\n#TITLE Candidate A\r\n#ARTIST Artist A\r\n");
            CreateBmsFileWithContents(candidateBDirectoryPath, "candidateB.bms", "#PLAYER 1\r\n#TITLE Candidate B\r\n#ARTIST Artist B\r\n");
            File.WriteAllText(Path.Combine(candidateADirectoryPath, "sound.wav"), "a");
            File.WriteAllText(Path.Combine(candidateBDirectoryPath, "sound.wav"), "b");

            ChartFile pendingFile = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pendingFilePath));
            ChartPackage pendingPackage = ChartPackageTestExtensions.CreatePackage([pendingFile]);
            pendingPackage.path = pendingFilePath;
            pendingPackage.delete_parent = true;
            library.BmsCharts =
            [
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(Path.Combine(candidateADirectoryPath, "candidateA.bms"))),
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(Path.Combine(candidateBDirectoryPath, "candidateB.bms")))
            ];
            SeedPendingPackages(library, songDbPath, pendingPackage);
            SetLibraryResourceIndex(library, BuildDirectoryLookupCache(sourceDirectoryPath, candidateADirectoryPath, candidateBDirectoryPath));

            library.SearchEstimatedInstallationDirectory(pendingPackage);

            PackageChartEntry pendingEntry = GetOnlyEntry(pendingPackage);
            Assert.IsTrue(string.IsNullOrWhiteSpace(pendingEntry.Chart.InstallDestination));
            Assert.AreEqual("Candidate A", pendingEntry.Chart.InstallDestinationTitle);
            Assert.AreEqual("Artist A", pendingEntry.Chart.InstallDestinationArtist);
            CollectionAssert.AreEquivalent(new[] { candidateADirectoryPath, candidateBDirectoryPath }, pendingEntry.Chart.InstallDestinationSuggestions.ToArray());
            Assert.IsTrue(ChartWarningTestHelpers.ContainsLowConfidenceInstallEstimationWarning(pendingEntry));
            Assert.IsTrue(pendingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationMetadataMismatch));
            StringAssert.Contains(ChartWarningTestHelpers.BuildTooltipText(pendingEntry), BeMusicSeeker.Properties.Resources.Warning_InstallEstimationMetadataMismatchPrefix);
            StringAssert.Contains(ChartWarningTestHelpers.BuildDigestText(pendingEntry), BeMusicSeeker.Properties.Resources.WarningDigest_InstallEstimationMetadataMismatch);
            StringAssert.Contains(ChartWarningTestHelpers.BuildTooltipText(pendingEntry), candidateADirectoryPath);
        });
    }

    /// <summary>BmsonのStrong不成立の二候補も自動確定せず、同じentryへ代表メタデータと不一致候補を反映する。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void SearchEstimatedInstallationDirectory_AdapterlessBmsonLowConfidenceStaysOnChartEntry()
    {

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageBmsonLowConfidence");
            string candidateADirectoryPath = Path.Combine(tempRootPath, "Installed", "A");
            string candidateBDirectoryPath = Path.Combine(tempRootPath, "Installed", "B");
            string pendingBmsonPath = CreateBmsonFile(sourceDirectoryPath, "pending.bmson", "Pending Bmson", "Pending Artist");
            CreateBmsFileWithContents(candidateADirectoryPath, "candidateA.bms", "#PLAYER 1\r\n#TITLE Candidate A\r\n#ARTIST Artist A\r\n");
            CreateBmsFileWithContents(candidateBDirectoryPath, "candidateB.bms", "#PLAYER 1\r\n#TITLE Candidate B\r\n#ARTIST Artist B\r\n");
            File.WriteAllText(Path.Combine(candidateADirectoryPath, "sound.wav"), "a");
            File.WriteAllText(Path.Combine(candidateBDirectoryPath, "sound.wav"), "b");

            var entry = PackageChartEntry.FromChart((ChartTestValues.ReadBmson(pendingBmsonPath)));
            var pendingPackage = ChartPackage.FromChartEntries([entry]);
            pendingPackage.path = sourceDirectoryPath;
            pendingPackage.delete_parent = false;
            library.BmsCharts =
            [
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(Path.Combine(candidateADirectoryPath, "candidateA.bms"))),
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(Path.Combine(candidateBDirectoryPath, "candidateB.bms")))
            ];
            SeedPendingPackages(library, songDbPath, pendingPackage);
            SetLibraryResourceIndex(library, BuildDirectoryLookupCache(sourceDirectoryPath, candidateADirectoryPath, candidateBDirectoryPath));

            library.SearchEstimatedInstallationDirectory(pendingPackage);

            Assert.IsTrue(string.IsNullOrWhiteSpace(entry.Chart.InstallDestination));
            Assert.AreEqual("Candidate A", entry.Chart.InstallDestinationTitle);
            Assert.AreEqual("Artist A", entry.Chart.InstallDestinationArtist);
            CollectionAssert.AreEquivalent(new[] { candidateADirectoryPath, candidateBDirectoryPath }, entry.Chart.InstallDestinationSuggestions.ToArray());
            Assert.IsTrue(entry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationMetadataMismatch));

        });
    }

    /// <summary>複数Bmsonの一括推定でも、Strong不成立の対象に二候補と不一致警告を残し、先頭候補へ確定しない。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void SearchEstimatedInstallationDirectory_BatchAdapterlessBmsonLowConfidenceStaysOnChartEntry()
    {

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string firstSourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageBmsonBatchLowConfidence");
            string secondSourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageBmsonBatchOther");
            string candidateADirectoryPath = Path.Combine(tempRootPath, "Installed", "A");
            string candidateBDirectoryPath = Path.Combine(tempRootPath, "Installed", "B");
            string firstPendingBmsonPath = CreateBmsonFile(firstSourceDirectoryPath, "pending.bmson", "Pending Bmson", "Pending Artist");
            string secondPendingBmsonPath = CreateBmsonFile(secondSourceDirectoryPath, "other.bmson", "Other Bmson", "Other Artist");
            CreateBmsFileWithContents(candidateADirectoryPath, "candidateA.bms", "#PLAYER 1\r\n#TITLE Candidate A\r\n#ARTIST Artist A\r\n");
            CreateBmsFileWithContents(candidateBDirectoryPath, "candidateB.bms", "#PLAYER 1\r\n#TITLE Candidate B\r\n#ARTIST Artist B\r\n");
            File.WriteAllText(Path.Combine(candidateADirectoryPath, "sound.wav"), "a");
            File.WriteAllText(Path.Combine(candidateBDirectoryPath, "sound.wav"), "b");

            var firstEntry = PackageChartEntry.FromChart((ChartTestValues.ReadBmson(firstPendingBmsonPath)));
            var firstPackage = ChartPackage.FromChartEntries([firstEntry]);
            firstPackage.path = firstSourceDirectoryPath;
            firstPackage.delete_parent = false;
            var secondEntry = PackageChartEntry.FromChart((ChartTestValues.ReadBmson(secondPendingBmsonPath)));
            var secondPackage = ChartPackage.FromChartEntries([secondEntry]);
            secondPackage.path = secondSourceDirectoryPath;
            secondPackage.delete_parent = false;
            library.BmsCharts =
            [
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(Path.Combine(candidateADirectoryPath, "candidateA.bms"))),
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(Path.Combine(candidateBDirectoryPath, "candidateB.bms")))
            ];
            SeedPendingPackages(library, songDbPath, firstPackage, secondPackage);
            SetLibraryResourceIndex(library, BuildDirectoryLookupCache(firstSourceDirectoryPath, secondSourceDirectoryPath, candidateADirectoryPath, candidateBDirectoryPath));

            library.SearchEstimatedInstallationDirectory([firstPackage, secondPackage]);

            Assert.IsTrue(string.IsNullOrWhiteSpace(firstEntry.Chart.InstallDestination));
            Assert.AreEqual("Candidate A", firstEntry.Chart.InstallDestinationTitle);
            CollectionAssert.AreEquivalent(new[] { candidateADirectoryPath, candidateBDirectoryPath }, firstEntry.Chart.InstallDestinationSuggestions.ToArray());
            Assert.IsTrue(firstEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationMetadataMismatch));
        });
    }

    /// <summary>混在パッケージの所持先を解決できないと、未所持entryの一般推定へ迂回せず専用の保留理由と警告を付ける。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void SearchEstimatedInstallationDirectory_MixedPackageUnresolvedInstalledDestination_DefersWithoutFallbackEstimation()
    {

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageMixedUnresolved");
            string installedADirectoryPath = Path.Combine(tempRootPath, "Installed", "A");
            string installedBDirectoryPath = Path.Combine(tempRootPath, "Installed", "B");
            string pendingInstalledAPath = CreateBmsFileWithContents(sourceDirectoryPath, "installedA.bms", "#PLAYER 1\r\n#TITLE Installed A\r\n#ARTIST Test\r\n");
            string pendingInstalledBPath = CreateBmsFileWithContents(sourceDirectoryPath, "installedB.bms", "#PLAYER 1\r\n#TITLE Installed B\r\n#ARTIST Test\r\n");
            string pendingMissingPath = CreateBmsFileWithContents(sourceDirectoryPath, "missing.bms", "#PLAYER 1\r\n#TITLE Missing\r\n#ARTIST Test\r\n#WAVAA missing.wav\r\n#00111:AA\r\n");
            string installedAPath = CreateBmsFileWithContents(installedADirectoryPath, "installedA.bms", "#PLAYER 1\r\n#TITLE Installed A\r\n#ARTIST Test\r\n");
            string installedBPath = CreateBmsFileWithContents(installedBDirectoryPath, "installedB.bms", "#PLAYER 1\r\n#TITLE Installed B\r\n#ARTIST Test\r\n");

            ChartFile pendingInstalledA = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pendingInstalledAPath));
            ChartFile pendingInstalledB = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pendingInstalledBPath));
            ChartFile pendingMissing = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pendingMissingPath));
            ChartPackage pendingPackage = ChartPackageTestExtensions.CreatePackage([pendingInstalledA, pendingInstalledB, pendingMissing]);
            pendingPackage.path = sourceDirectoryPath;
            pendingPackage.delete_parent = false;
            library.BmsCharts =
            [
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(installedAPath)),
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(installedBPath))
            ];
            SeedPendingPackages(library, songDbPath, pendingPackage);

            library.SearchEstimatedInstallationDirectory(pendingPackage);

            Assert.AreEqual(PendingEstimateDeferredReason.InstalledDestinationResolveFailed, pendingPackage.DeferredEstimateReason);
            Assert.IsTrue(GetEntryByFileName(pendingPackage, "installedA.bms").Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.AlreadyInstalled));
            Assert.IsTrue(GetEntryByFileName(pendingPackage, "installedB.bms").Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.AlreadyInstalled));
            PackageChartEntry pendingMissingEntry = GetEntryByFileName(pendingPackage, "missing.bms");
            Assert.IsTrue(string.IsNullOrWhiteSpace(pendingMissingEntry.Chart.InstallDestination));
            Assert.IsTrue(string.IsNullOrWhiteSpace(pendingMissingEntry.Chart.InstallDestinationTitle));
            Assert.AreEqual(0, pendingMissingEntry.Chart.InstallDestinationSuggestions.Count);
            Assert.IsFalse(ChartWarningTestHelpers.ContainsLowConfidenceInstallEstimationWarning(pendingMissingEntry));
            Assert.IsTrue(pendingMissingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstalledDestinationResolveFailed));
            StringAssert.Contains(ChartWarningTestHelpers.BuildTooltipText(pendingMissingEntry), BeMusicSeeker.Properties.Resources.Warning_InstalledDestinationResolveFailed);
        });
    }

    /// <summary>親ディレクトリ参照を持つ入力は推定を保留し、導入先や低確度候補を捏造せず未対応理由を通知用警告へ残す。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void SearchEstimatedInstallationDirectory_UnsupportedParentResourcePath_WarnsAndSkipsEstimation()
    {

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageUnsupportedResourcePath");
            string candidateDirectoryPath = Path.Combine(tempRootPath, "Installed", "Candidate");
            string pendingFilePath = CreateBmsFileWithContents(
                sourceDirectoryPath,
                "unsupported.bms",
                "#PLAYER 1\r\n"
                + "#TITLE Unsupported Resource Path\r\n"
                + "#ARTIST Test\r\n"
                + "#WAVAA ..\\Base\\sound.wav\r\n"
                + "#00111:AA\r\n");
            string candidateFilePath = CreateBmsFileWithContents(candidateDirectoryPath, "candidate.bms", "#PLAYER 1\r\n#TITLE Current Directory Resource Path\r\n#ARTIST Test\r\n");
            File.WriteAllText(Path.Combine(candidateDirectoryPath, "sound.wav"), "audio");
            ChartPackage pendingPackage = ChartPackageTestExtensions.CreatePackage([BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pendingFilePath))]);
            pendingPackage.path = sourceDirectoryPath;
            pendingPackage.delete_parent = false;

            library.BmsCharts = [BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(candidateFilePath))];
            SeedPendingPackages(library, songDbPath, pendingPackage);
            SetLibraryResourceIndex(library, BuildDirectoryLookupCache(sourceDirectoryPath, candidateDirectoryPath));

            library.SearchEstimatedInstallationDirectory(pendingPackage);

            Assert.AreEqual(PendingEstimateDeferredReason.UnsupportedResourcePath, pendingPackage.DeferredEstimateReason);
            PackageChartEntry pendingEntry = GetEntryByFileName(pendingPackage, "unsupported.bms");
            Assert.IsTrue(string.IsNullOrWhiteSpace(pendingEntry.Chart.InstallDestination));
            Assert.IsFalse(ChartWarningTestHelpers.ContainsLowConfidenceInstallEstimationWarning(pendingEntry));
            Assert.IsTrue(pendingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.UnsupportedResourcePath));
            StringAssert.Contains(ChartWarningTestHelpers.BuildTooltipText(pendingEntry), BeMusicSeeker.Properties.Resources.Warning_UnsupportedResourcePath);
        });
    }

    /// <summary>現在ディレクトリ付きのリソース参照を正規化して候補を選び、拡張子だけの記述を未対応パスと誤分類しない。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void SearchEstimatedInstallationDirectory_CurrentDirectoryResourcePath_NormalizesAndEstimates()
    {

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageCurrentDirectoryResourcePath");
            string candidateDirectoryPath = Path.Combine(tempRootPath, "Installed", "Candidate");
            string pendingFilePath = CreateBmsFileWithContents(
                sourceDirectoryPath,
                "current-dir.bms",
                "#PLAYER 1\r\n"
                + "#TITLE Current Directory Resource Path\r\n"
                + "#ARTIST Test\r\n"
                + "#WAVAA .\\sound.wav\r\n"
                + "#WAVAB .wav\r\n#WAVAC .ogg\r\n#BMPAA .png\r\n#BMPAB mystery.xyz\r\n"
                + "#00111:AA\r\n");
            string candidateFilePath = CreateBmsFileWithContents(candidateDirectoryPath, "candidate.bms", "#PLAYER 1\r\n#TITLE Current Directory Resource Path\r\n#ARTIST Test\r\n");
            File.WriteAllText(Path.Combine(candidateDirectoryPath, "sound.wav"), "audio");
            ChartPackage pendingPackage = ChartPackageTestExtensions.CreatePackage([BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pendingFilePath))]);
            pendingPackage.path = sourceDirectoryPath;
            pendingPackage.delete_parent = false;

            library.BmsCharts = [BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(candidateFilePath))];
            SeedPendingPackages(library, songDbPath, pendingPackage);
            SetLibraryResourceIndex(library, BuildDirectoryLookupCache(sourceDirectoryPath, candidateDirectoryPath));

            library.SearchEstimatedInstallationDirectory(pendingPackage);

            Assert.AreEqual(PendingEstimateDeferredReason.None, pendingPackage.DeferredEstimateReason);
            PackageChartEntry pendingEntry = GetEntryByFileName(pendingPackage, "current-dir.bms");
            Assert.AreEqual(candidateDirectoryPath, pendingEntry.Chart.InstallDestination);
            Assert.IsFalse(pendingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.UnsupportedResourcePath));
        });
    }

    /// <summary>混在パッケージでは所持ハッシュの候補順だけで確定せず、未所持譜面の最終評価に合う導入先を選ぶ。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void SearchEstimatedInstallationDirectory_MixedPackageMultipleCandidates_UsesFinalEvaluationWinner()
    {

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageMixedWinner");
            string installedADirectoryPath = Path.Combine(tempRootPath, "Installed", "A");
            string installedBDirectoryPath = Path.Combine(tempRootPath, "Installed", "B");
            string pendingInstalledAPath = CreateBmsFileWithContents(sourceDirectoryPath, "installedA.bms", "#PLAYER 1\r\n#TITLE Installed A\r\n#ARTIST Test\r\n");
            string pendingInstalledBPath = CreateBmsFileWithContents(sourceDirectoryPath, "installedB.bms", "#PLAYER 1\r\n#TITLE Installed B\r\n#ARTIST Test\r\n");
            string pendingMissingPath = CreateBmsFileWithContents(sourceDirectoryPath, "missing.bms", "#PLAYER 1\r\n#TITLE Installed B\r\n#ARTIST Test\r\n#WAVAA sound.wav\r\n#00111:AA\r\n");
            string installedAPath = CreateBmsFileWithContents(installedADirectoryPath, "installedA.bms", "#PLAYER 1\r\n#TITLE Installed A\r\n#ARTIST Test\r\n");
            string installedBPath = CreateBmsFileWithContents(installedBDirectoryPath, "installedB.bms", "#PLAYER 1\r\n#TITLE Installed B\r\n#ARTIST Test\r\n");
            File.WriteAllText(Path.Combine(installedBDirectoryPath, "sound.wav"), "b");

            ChartFile pendingInstalledA = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pendingInstalledAPath));
            ChartFile pendingInstalledB = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pendingInstalledBPath));
            ChartFile pendingMissing = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pendingMissingPath));
            ChartPackage pendingPackage = ChartPackageTestExtensions.CreatePackage([pendingInstalledA, pendingInstalledB, pendingMissing]);
            pendingPackage.path = sourceDirectoryPath;
            pendingPackage.delete_parent = false;
            library.BmsCharts =
            [
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(installedAPath)),
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(installedBPath))
            ];
            SeedPendingPackages(library, songDbPath, pendingPackage);
            SetLibraryResourceIndex(library, BuildDirectoryLookupCache(sourceDirectoryPath, installedADirectoryPath, installedBDirectoryPath));

            library.SearchEstimatedInstallationDirectory(pendingPackage);

            Assert.AreEqual(PendingEstimateDeferredReason.None, pendingPackage.DeferredEstimateReason);
            PackageChartEntry pendingMissingEntry = GetEntryByFileName(pendingPackage, "missing.bms");
            Assert.AreEqual(installedBDirectoryPath, pendingMissingEntry.Chart.InstallDestination);
            Assert.AreEqual(0, pendingMissingEntry.Chart.InstallDestinationSuggestions.Count);
            Assert.IsFalse(pendingMissingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstalledDestinationAmbiguous));
            Assert.IsFalse(pendingMissingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstalledDestinationResolveFailed));
        });
    }

    /// <summary>混在パッケージの実在ペアと資源評価が同等なら、BMSとBmsonを通したディレクトリ固有ハッシュ数で一意の候補を選ぶ。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void SearchEstimatedInstallationDirectory_MixedPackageEquivalentCandidates_UsesDirectoryUniquePrimaryHashCount()
    {

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageMixedCountTieBreak");
            string installedADirectoryPath = Path.Combine(tempRootPath, "Installed", "A");
            string installedBDirectoryPath = Path.Combine(tempRootPath, "Installed", "B");
            string pendingInstalledAPath = CreateBmsFileWithContents(sourceDirectoryPath, "installedA.bms", "#PLAYER 1\r\n#TITLE Song\r\n#ARTIST Test\r\n#GENRE A\r\n");
            string pendingInstalledBPath = CreateBmsFileWithContents(sourceDirectoryPath, "installedB.bms", "#PLAYER 1\r\n#TITLE Song\r\n#ARTIST Test\r\n#GENRE B\r\n");
            string pendingMissingPath = CreateBmsFileWithContents(sourceDirectoryPath, "missing.bms", "#PLAYER 1\r\n#TITLE Song\r\n#ARTIST Test\r\n#WAVAA sound.wav\r\n#00111:AA\r\n");
            string installedAPath = CreateBmsFileWithContents(installedADirectoryPath, "installedA.bms", "#PLAYER 1\r\n#TITLE Song\r\n#ARTIST Test\r\n#GENRE A\r\n");
            string installedBPath = CreateBmsFileWithContents(installedBDirectoryPath, "installedB.bms", "#PLAYER 1\r\n#TITLE Song\r\n#ARTIST Test\r\n#GENRE B\r\n");
            // 追加bmsonは基本情報を持たせず、両候補の支持数を同等にして一意ハッシュ数だけの差を作ります。
            string extraABmsonPath = CreateBmsonFile(installedADirectoryPath, "extraA.bmson", "", "");
            File.WriteAllText(Path.Combine(installedADirectoryPath, "sound.wav"), "a");
            File.WriteAllText(Path.Combine(installedBDirectoryPath, "sound.wav"), "b");

            ChartFile pendingInstalledA = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pendingInstalledAPath));
            ChartFile pendingInstalledB = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pendingInstalledBPath));
            ChartFile pendingMissing = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pendingMissingPath));
            ChartPackage pendingPackage = ChartPackageTestExtensions.CreatePackage([pendingInstalledA, pendingInstalledB, pendingMissing]);
            pendingPackage.path = sourceDirectoryPath;
            pendingPackage.delete_parent = false;
            library.BmsCharts =
            [
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(installedAPath)),
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(installedBPath))
            ];
            library.BmsonCharts =
            [
                ChartTestValues.ReadBmson(extraABmsonPath)
            ];
            SeedPendingPackages(library, songDbPath, pendingPackage);
            SetLibraryResourceIndex(library, BuildDirectoryLookupCache(sourceDirectoryPath, installedADirectoryPath, installedBDirectoryPath));

            library.SearchEstimatedInstallationDirectory(pendingPackage);

            Assert.AreEqual(PendingEstimateDeferredReason.None, pendingPackage.DeferredEstimateReason);
            PackageChartEntry pendingMissingEntry = GetEntryByFileName(pendingPackage, "missing.bms");
            Assert.AreEqual(installedADirectoryPath, pendingMissingEntry.Chart.InstallDestination);
            Assert.AreEqual(0, pendingMissingEntry.Chart.InstallDestinationSuggestions.Count);
            Assert.IsFalse(ChartWarningTestHelpers.ContainsLowConfidenceInstallEstimationWarning(pendingMissingEntry));
            Assert.IsFalse(pendingMissingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstalledDestinationAmbiguous));
            Assert.IsFalse(pendingMissingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstalledDestinationAutoAppliedAmbiguous));
        });
    }

    /// <summary>混在パッケージの複数有力候補を未確定で残し、通常の推定曖昧ではなく所持先曖昧の警告と両パスを示す。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void SearchEstimatedInstallationDirectory_MixedPackageMultipleViableCandidates_ShowsInstalledDestinationAmbiguousWarning()
    {

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageMixedAmbiguous");
            string installedADirectoryPath = Path.Combine(tempRootPath, "Installed", "A");
            string installedBDirectoryPath = Path.Combine(tempRootPath, "Installed", "B");
            string pendingInstalledAPath = CreateBmsFileWithContents(sourceDirectoryPath, "installedA.bms", "#PLAYER 1\r\n#TITLE Target Song (A)\r\n#ARTIST Test\r\n");
            string pendingInstalledBPath = CreateBmsFileWithContents(sourceDirectoryPath, "installedB.bms", "#PLAYER 1\r\n#TITLE Target Song (B)\r\n#ARTIST Test\r\n");
            string pendingMissingPath = CreateBmsFileWithContents(sourceDirectoryPath, "missing.bms", "#PLAYER 1\r\n#TITLE Target Song (Missing)\r\n#ARTIST Test\r\n#WAVAA sound.wav\r\n#00111:AA\r\n");
            string installedAPath = CreateBmsFileWithContents(installedADirectoryPath, "installedA.bms", "#PLAYER 1\r\n#TITLE Target Song (A)\r\n#ARTIST Test\r\n");
            string installedBPath = CreateBmsFileWithContents(installedBDirectoryPath, "installedB.bms", "#PLAYER 1\r\n#TITLE Target Song (B)\r\n#ARTIST Test\r\n");
            File.WriteAllText(Path.Combine(installedADirectoryPath, "sound.wav"), "a");
            File.WriteAllText(Path.Combine(installedBDirectoryPath, "sound.wav"), "b");

            ChartFile pendingInstalledA = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pendingInstalledAPath));
            ChartFile pendingInstalledB = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pendingInstalledBPath));
            ChartFile pendingMissing = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pendingMissingPath));
            ChartPackage pendingPackage = ChartPackageTestExtensions.CreatePackage([pendingInstalledA, pendingInstalledB, pendingMissing]);
            pendingPackage.path = sourceDirectoryPath;
            pendingPackage.delete_parent = false;
            library.BmsCharts =
            [
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(installedAPath)),
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(installedBPath))
            ];
            SeedPendingPackages(library, songDbPath, pendingPackage);
            SetLibraryResourceIndex(library, BuildDirectoryLookupCache(sourceDirectoryPath, installedADirectoryPath, installedBDirectoryPath));

            library.SearchEstimatedInstallationDirectory(pendingPackage);

            Assert.AreEqual(PendingEstimateDeferredReason.None, pendingPackage.DeferredEstimateReason);
            PackageChartEntry pendingMissingEntry = GetEntryByFileName(pendingPackage, "missing.bms");
            Assert.IsTrue(string.IsNullOrWhiteSpace(pendingMissingEntry.Chart.InstallDestination));
            CollectionAssert.AreEquivalent(new[] { installedADirectoryPath, installedBDirectoryPath }, pendingMissingEntry.Chart.InstallDestinationSuggestions.ToArray());
            Assert.IsTrue(ChartWarningTestHelpers.ContainsLowConfidenceInstallEstimationWarning(pendingMissingEntry));
            Assert.IsTrue(pendingMissingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstalledDestinationAmbiguous));
            Assert.IsFalse(pendingMissingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationAmbiguous));
            StringAssert.Contains(ChartWarningTestHelpers.BuildDigestText(pendingMissingEntry), BeMusicSeeker.Properties.Resources.WarningDigest_InstalledDestinationAmbiguous);
            StringAssert.Contains(ChartWarningTestHelpers.BuildTooltipText(pendingMissingEntry), BeMusicSeeker.Properties.Resources.Warning_InstalledDestinationAmbiguous.Split('\n')[0]);
            StringAssert.Contains(ChartWarningTestHelpers.BuildTooltipText(pendingMissingEntry), installedADirectoryPath);
            StringAssert.Contains(ChartWarningTestHelpers.BuildTooltipText(pendingMissingEntry), installedBDirectoryPath);
        });
    }

    /// <summary>強いメタデータ一致と自動適用設定がある混在パッケージは先頭候補へ確定しても、全候補と自動適用の曖昧警告を保持する。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void SearchEstimatedInstallationDirectory_MixedPackageStrongMetadataWithSetting_AutoAppliesFirstCandidateAndKeepsAutoAppliedWarning()
    {

        WithAutoApplyAmbiguousInstallDestination(true, delegate
        {
            WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
            {
                string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageMixedAutoApplyStrong");
                string installedADirectoryPath = Path.Combine(tempRootPath, "Installed", "A");
                string installedBDirectoryPath = Path.Combine(tempRootPath, "Installed", "B");
                string installedAContents = "#PLAYER 1\r\n#TITLE Target Song\r\n#ARTIST Artist\r\n#GENRE A\r\n";
                string installedBContents = "#PLAYER 1\r\n#TITLE Target Song\r\n#ARTIST Artist\r\n#GENRE B\r\n";
                string pendingInstalledAPath = CreateBmsFileWithContents(sourceDirectoryPath, "installedA.bms", installedAContents);
                string pendingInstalledBPath = CreateBmsFileWithContents(sourceDirectoryPath, "installedB.bms", installedBContents);
                string pendingMissingPath = CreateBmsFileWithContents(sourceDirectoryPath, "missing.bms", "#PLAYER 1\r\n#TITLE Target Song (Another)\r\n#ARTIST Artist / Diff\r\n#WAVAA sound.wav\r\n#00111:AA\r\n");
                string installedAPath = CreateBmsFileWithContents(installedADirectoryPath, "installedA.bms", installedAContents);
                string installedBPath = CreateBmsFileWithContents(installedBDirectoryPath, "installedB.bms", installedBContents);
                File.WriteAllText(Path.Combine(installedADirectoryPath, "sound.wav"), "a");
                File.WriteAllText(Path.Combine(installedBDirectoryPath, "sound.wav"), "b");

                ChartFile pendingInstalledA = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pendingInstalledAPath));
                ChartFile pendingInstalledB = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pendingInstalledBPath));
                ChartFile pendingMissing = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pendingMissingPath));
                ChartPackage pendingPackage = ChartPackageTestExtensions.CreatePackage([pendingInstalledA, pendingInstalledB, pendingMissing]);
                pendingPackage.path = sourceDirectoryPath;
                pendingPackage.delete_parent = false;
                library.BmsCharts =
                [
                    BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(installedAPath)),
                    BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(installedBPath))
                ];
                SeedPendingPackages(library, songDbPath, pendingPackage);
                SetLibraryResourceIndex(library, BuildDirectoryLookupCache(sourceDirectoryPath, installedADirectoryPath, installedBDirectoryPath));

                library.SearchEstimatedInstallationDirectory(pendingPackage);

                PackageChartEntry pendingMissingEntry = GetEntryByFileName(pendingPackage, "missing.bms");
                Assert.AreEqual(installedADirectoryPath, pendingMissingEntry.Chart.InstallDestination);
                Assert.AreEqual("Target Song", pendingMissingEntry.Chart.InstallDestinationTitle);
                Assert.AreEqual("Artist", pendingMissingEntry.Chart.InstallDestinationArtist);
                CollectionAssert.AreEqual(new[] { installedADirectoryPath, installedBDirectoryPath }, pendingMissingEntry.Chart.InstallDestinationSuggestions.ToArray());
                Assert.IsTrue(ChartWarningTestHelpers.ContainsLowConfidenceInstallEstimationWarning(pendingMissingEntry));
                Assert.IsTrue(pendingMissingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstalledDestinationAutoAppliedAmbiguous));
                Assert.IsFalse(pendingMissingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstalledDestinationAmbiguous));
                Assert.IsFalse(pendingMissingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationAmbiguous));
                StringAssert.Contains(ChartWarningTestHelpers.BuildDigestText(pendingMissingEntry), BeMusicSeeker.Properties.Resources.WarningDigest_InstalledDestinationAutoAppliedAmbiguous);
                StringAssert.Contains(ChartWarningTestHelpers.BuildTooltipText(pendingMissingEntry), BeMusicSeeker.Properties.Resources.Warning_InstalledDestinationAutoAppliedAmbiguous.Split('\n')[0]);
                StringAssert.Contains(ChartWarningTestHelpers.BuildTooltipText(pendingMissingEntry), installedADirectoryPath);
                StringAssert.Contains(ChartWarningTestHelpers.BuildTooltipText(pendingMissingEntry), installedBDirectoryPath);
            });
        });
    }

    /// <summary>自動適用設定が無効なら、強いメタデータ一致があっても混在パッケージを確定せず両候補と所持先曖昧警告を残す。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void SearchEstimatedInstallationDirectory_MixedPackageStrongMetadataWithSettingOff_LeavesInstallDestinationEmpty()
    {

        WithAutoApplyAmbiguousInstallDestination(false, delegate
        {
            WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
            {
                string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageMixedAutoApplyStrongOff");
                string installedADirectoryPath = Path.Combine(tempRootPath, "Installed", "A");
                string installedBDirectoryPath = Path.Combine(tempRootPath, "Installed", "B");
                string installedAContents = "#PLAYER 1\r\n#TITLE Target Song\r\n#ARTIST Artist\r\n#GENRE A\r\n";
                string installedBContents = "#PLAYER 1\r\n#TITLE Target Song\r\n#ARTIST Artist\r\n#GENRE B\r\n";
                string pendingInstalledAPath = CreateBmsFileWithContents(sourceDirectoryPath, "installedA.bms", installedAContents);
                string pendingInstalledBPath = CreateBmsFileWithContents(sourceDirectoryPath, "installedB.bms", installedBContents);
                string pendingMissingPath = CreateBmsFileWithContents(sourceDirectoryPath, "missing.bms", "#PLAYER 1\r\n#TITLE Target Song (Another)\r\n#ARTIST Artist / Diff\r\n#WAVAA sound.wav\r\n#00111:AA\r\n");
                string installedAPath = CreateBmsFileWithContents(installedADirectoryPath, "installedA.bms", installedAContents);
                string installedBPath = CreateBmsFileWithContents(installedBDirectoryPath, "installedB.bms", installedBContents);
                File.WriteAllText(Path.Combine(installedADirectoryPath, "sound.wav"), "a");
                File.WriteAllText(Path.Combine(installedBDirectoryPath, "sound.wav"), "b");

                ChartFile pendingInstalledA = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pendingInstalledAPath));
                ChartFile pendingInstalledB = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pendingInstalledBPath));
                ChartFile pendingMissing = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pendingMissingPath));
                ChartPackage pendingPackage = ChartPackageTestExtensions.CreatePackage([pendingInstalledA, pendingInstalledB, pendingMissing]);
                pendingPackage.path = sourceDirectoryPath;
                pendingPackage.delete_parent = false;
                library.BmsCharts =
                [
                    BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(installedAPath)),
                    BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(installedBPath))
                ];
                SeedPendingPackages(library, songDbPath, pendingPackage);
                SetLibraryResourceIndex(library, BuildDirectoryLookupCache(sourceDirectoryPath, installedADirectoryPath, installedBDirectoryPath));

                library.SearchEstimatedInstallationDirectory(pendingPackage);

                PackageChartEntry pendingMissingEntry = GetEntryByFileName(pendingPackage, "missing.bms");
                Assert.IsTrue(string.IsNullOrWhiteSpace(pendingMissingEntry.Chart.InstallDestination));
                Assert.AreEqual("Target Song", pendingMissingEntry.Chart.InstallDestinationTitle);
                Assert.AreEqual("Artist", pendingMissingEntry.Chart.InstallDestinationArtist);
                CollectionAssert.AreEqual(new[] { installedADirectoryPath, installedBDirectoryPath }, pendingMissingEntry.Chart.InstallDestinationSuggestions.ToArray());
                Assert.IsTrue(ChartWarningTestHelpers.ContainsLowConfidenceInstallEstimationWarning(pendingMissingEntry));
                Assert.IsTrue(pendingMissingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstalledDestinationAmbiguous));
                Assert.IsFalse(pendingMissingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstalledDestinationAutoAppliedAmbiguous));
                Assert.IsFalse(pendingMissingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationAmbiguous));
            });
        });
    }

    /// <summary>自動適用設定が有効でもStrong不成立の混在パッケージは未確定にし、不一致警告を残して自動適用済みとしない。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void SearchEstimatedInstallationDirectory_MixedPackageWeakMetadataWithSetting_LeavesInstallDestinationEmpty()
    {

        WithAutoApplyAmbiguousInstallDestination(true, delegate
        {
            WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
            {
                string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageMixedAutoApplyWeak");
                string installedADirectoryPath = Path.Combine(tempRootPath, "Installed", "A");
                string installedBDirectoryPath = Path.Combine(tempRootPath, "Installed", "B");
                string pendingInstalledAPath = CreateBmsFileWithContents(sourceDirectoryPath, "installedA.bms", "#PLAYER 1\r\n#TITLE Installed A\r\n#ARTIST Test\r\n");
                string pendingInstalledBPath = CreateBmsFileWithContents(sourceDirectoryPath, "installedB.bms", "#PLAYER 1\r\n#TITLE Installed B\r\n#ARTIST Test\r\n");
                string pendingMissingPath = CreateBmsFileWithContents(sourceDirectoryPath, "missing.bms", "#PLAYER 1\r\n#TITLE Missing\r\n#ARTIST Test\r\n#WAVAA sound.wav\r\n#00111:AA\r\n");
                string installedAPath = CreateBmsFileWithContents(installedADirectoryPath, "installedA.bms", "#PLAYER 1\r\n#TITLE Installed A\r\n#ARTIST Test\r\n");
                string installedBPath = CreateBmsFileWithContents(installedBDirectoryPath, "installedB.bms", "#PLAYER 1\r\n#TITLE Installed B\r\n#ARTIST Test\r\n");
                File.WriteAllText(Path.Combine(installedADirectoryPath, "sound.wav"), "a");
                File.WriteAllText(Path.Combine(installedBDirectoryPath, "sound.wav"), "b");

                ChartFile pendingInstalledA = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pendingInstalledAPath));
                ChartFile pendingInstalledB = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pendingInstalledBPath));
                ChartFile pendingMissing = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pendingMissingPath));
                ChartPackage pendingPackage = ChartPackageTestExtensions.CreatePackage([pendingInstalledA, pendingInstalledB, pendingMissing]);
                pendingPackage.path = sourceDirectoryPath;
                pendingPackage.delete_parent = false;
                library.BmsCharts =
                [
                    BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(installedAPath)),
                    BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(installedBPath))
                ];
                SeedPendingPackages(library, songDbPath, pendingPackage);
                SetLibraryResourceIndex(library, BuildDirectoryLookupCache(sourceDirectoryPath, installedADirectoryPath, installedBDirectoryPath));

                library.SearchEstimatedInstallationDirectory(pendingPackage);

                PackageChartEntry pendingMissingEntry = GetEntryByFileName(pendingPackage, "missing.bms");
                Assert.IsTrue(string.IsNullOrWhiteSpace(pendingMissingEntry.Chart.InstallDestination));
                CollectionAssert.AreEquivalent(new[] { installedADirectoryPath, installedBDirectoryPath }, pendingMissingEntry.Chart.InstallDestinationSuggestions.ToArray());
                Assert.IsTrue(ChartWarningTestHelpers.ContainsLowConfidenceInstallEstimationWarning(pendingMissingEntry));
                Assert.IsTrue(pendingMissingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationMetadataMismatch));
                Assert.IsFalse(pendingMissingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstalledDestinationAutoAppliedAmbiguous));
                Assert.IsFalse(pendingMissingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationAmbiguous));
            });
        });
    }

    /// <summary>リソース同順位からStrong候補を適用し、所持現在値の変更後はキャッシュの失効で照合とMIX切断表示を同時に更新する。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void SearchEstimatedInstallationDirectory_MetadataTieBreakSelectsMatchingCandidate()
    {

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageMetadataTieBreak");
            string candidateADirectoryPath = Path.Combine(tempRootPath, "Installed", "A");
            string candidateBDirectoryPath = Path.Combine(tempRootPath, "Installed", "B");
            string pendingFilePath = CreateBmsFileWithContents(sourceDirectoryPath, "pending.bms", "#PLAYER 1\r\n#TITLE Target Song (Another)\r\n#ARTIST Artist / Diff\r\n#WAVAA sound.wav\r\n#00111:AA\r\n");
            CreateBmsFileWithContents(candidateADirectoryPath, "candidateA1.bms", "#PLAYER 1\r\n#TITLE Another Song\r\n#ARTIST Someone\r\n");
            CreateBmsFileWithContents(candidateADirectoryPath, "candidateA2.bms", "#PLAYER 1\r\n#TITLE Another Song [7K]\r\n#ARTIST Someone\r\n");
            CreateBmsFileWithContents(candidateBDirectoryPath, "candidateB1.bms", "#PLAYER 1\r\n#TITLE Target Song\r\n#ARTIST Artist\r\n");
            CreateBmsFileWithContents(candidateBDirectoryPath, "candidateB2.bms", "#PLAYER 1\r\n#TITLE Target Song (Hyper)\r\n#ARTIST Artist / obj: diff\r\n");
            File.WriteAllText(Path.Combine(candidateADirectoryPath, "sound.wav"), "a");
            File.WriteAllText(Path.Combine(candidateBDirectoryPath, "sound.wav"), "b");

            ChartFile pendingFile = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pendingFilePath));
            ChartPackage pendingPackage = ChartPackageTestExtensions.CreatePackage([pendingFile]);
            pendingPackage.path = pendingFilePath;
            pendingPackage.delete_parent = true;
            library.BmsCharts =
            [
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(Path.Combine(candidateADirectoryPath, "candidateA1.bms"))),
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(Path.Combine(candidateADirectoryPath, "candidateA2.bms"))),
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(Path.Combine(candidateBDirectoryPath, "candidateB1.bms"))),
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(Path.Combine(candidateBDirectoryPath, "candidateB2.bms")))
            ];
            SeedPendingPackages(library, songDbPath, pendingPackage);
            SetLibraryResourceIndex(library, BuildDirectoryLookupCache(sourceDirectoryPath, candidateADirectoryPath, candidateBDirectoryPath));

            library.SearchEstimatedInstallationDirectory(pendingPackage);

            PackageChartEntry pendingEntry = GetOnlyEntry(pendingPackage);
            Assert.AreEqual(candidateBDirectoryPath, pendingEntry.Chart.InstallDestination);
            Assert.AreEqual("Target Song", pendingEntry.Chart.InstallDestinationTitle);
            Assert.AreEqual("Artist", pendingEntry.Chart.InstallDestinationArtist);
            Assert.IsFalse(ChartWarningTestHelpers.ContainsLowConfidenceInstallEstimationWarning(pendingEntry));
            Assert.AreEqual(0, pendingEntry.Chart.InstallDestinationSuggestions.Count);

            // 同じ実パスの所持現在値を変更し、既存キャッシュの失効でStrongと表示が同時に更新されることを確認します。
            library.RemoveInstallDestination([pendingEntry]);
            library.BmsCharts = [.. library.BmsCharts.Select(chart => chart with { RawTitle = "Ark (remix/5keys)", Artist = string.Empty })];
            library.SearchEstimatedInstallationDirectory(pendingPackage);
            Assert.IsTrue(string.IsNullOrWhiteSpace(pendingEntry.Chart.InstallDestination));
            Assert.AreEqual("Ark (remix", pendingEntry.Chart.InstallDestinationTitle);
            Assert.AreEqual(string.Empty, pendingEntry.Chart.InstallDestinationArtist);
            CollectionAssert.AreEquivalent(new[] { candidateADirectoryPath, candidateBDirectoryPath }, pendingEntry.Chart.InstallDestinationSuggestions.ToArray());
            Assert.IsTrue(pendingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationMetadataMismatch));

        });
    }

    /// <summary>通常推定の強いメタデータ一致を自動適用する設定では先頭候補を選びつつ、曖昧警告と全候補を維持する。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void SearchEstimatedInstallationDirectory_AmbiguousStrongMetadataWithSetting_AppliesDestinationAndKeepsWarning()
    {

        WithAutoApplyAmbiguousInstallDestination(true, delegate
        {
            WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
            {
                string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageAmbiguousStrongMetadata");
                string candidateADirectoryPath = Path.Combine(tempRootPath, "Installed", "A");
                string candidateBDirectoryPath = Path.Combine(tempRootPath, "Installed", "B");
                string pendingFilePath = CreateBmsFileWithContents(sourceDirectoryPath, "pending.bms", "#PLAYER 1\r\n#TITLE Target Song (Another)\r\n#ARTIST Artist / Diff\r\n#WAVAA sound.wav\r\n#00111:AA\r\n");
                CreateBmsFileWithContents(candidateADirectoryPath, "candidateA.bms", "#PLAYER 1\r\n#TITLE Target Song\r\n#ARTIST Artist\r\n");
                CreateBmsFileWithContents(candidateBDirectoryPath, "candidateB.bms", "#PLAYER 1\r\n#TITLE Target Song\r\n#ARTIST Artist\r\n");
                File.WriteAllText(Path.Combine(candidateADirectoryPath, "sound.wav"), "a");
                File.WriteAllText(Path.Combine(candidateBDirectoryPath, "sound.wav"), "b");

                ChartFile pendingFile = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pendingFilePath));
                ChartPackage pendingPackage = ChartPackageTestExtensions.CreatePackage([pendingFile]);
                pendingPackage.path = pendingFilePath;
                pendingPackage.delete_parent = true;
                library.BmsCharts =
                [
                    BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(Path.Combine(candidateADirectoryPath, "candidateA.bms"))),
                    BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(Path.Combine(candidateBDirectoryPath, "candidateB.bms")))
                ];
                SeedPendingPackages(library, songDbPath, pendingPackage);
                SetLibraryResourceIndex(library, BuildDirectoryLookupCache(sourceDirectoryPath, candidateADirectoryPath, candidateBDirectoryPath));

                library.SearchEstimatedInstallationDirectory(pendingPackage);

                PackageChartEntry pendingEntry = GetOnlyEntry(pendingPackage);
                Assert.AreEqual(candidateADirectoryPath, pendingEntry.Chart.InstallDestination);
                Assert.AreEqual("Target Song", pendingEntry.Chart.InstallDestinationTitle);
                Assert.AreEqual("Artist", pendingEntry.Chart.InstallDestinationArtist);
                Assert.IsTrue(ChartWarningTestHelpers.ContainsLowConfidenceInstallEstimationWarning(pendingEntry));
                CollectionAssert.AreEqual(new[] { candidateADirectoryPath, candidateBDirectoryPath }, pendingEntry.Chart.InstallDestinationSuggestions.ToArray());
                Assert.IsTrue(pendingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationAmbiguous));
                StringAssert.Contains(ChartWarningTestHelpers.BuildTooltipText(pendingEntry), candidateADirectoryPath);
                StringAssert.Contains(ChartWarningTestHelpers.BuildTooltipText(pendingEntry), candidateBDirectoryPath);
            });
        });
    }

    /// <summary>リソースの単一候補でも別題・別作者または片側空作者なら確定せず、空作者の候補もTITLEを表示して不一致専用警告を渡す。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SearchEstimatedInstallationDirectory_MetadataMismatchLeavesInstallDestinationEmptyAndShowsSingleSuggestion(bool emptyArtist)
    {

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageMetadataMismatch");
            string candidateDirectoryPath = Path.Combine(tempRootPath, "Installed", "OnlyCandidate");
            string pendingFilePath = CreateBmsFileWithContents(sourceDirectoryPath, "pending.bms", "#PLAYER 1\r\n#TITLE Target Song\r\n#ARTIST Base Artist obj: Diff\r\n#WAVAA sound.wav\r\n#00111:AA\r\n");
            CreateBmsFileWithContents(candidateDirectoryPath, "candidate.bms", emptyArtist ? "#TITLE Target Song\r\n#ARTIST OBJ: Diff\r\n" : "#PLAYER 1\r\n#TITLE Completely Different\r\n#ARTIST Another Artist\r\n");
            File.WriteAllText(Path.Combine(candidateDirectoryPath, "sound.wav"), "dst");

            ChartFile pendingFile = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pendingFilePath));
            ChartPackage pendingPackage = ChartPackageTestExtensions.CreatePackage([pendingFile]);
            pendingPackage.path = pendingFilePath;
            pendingPackage.delete_parent = true;
            library.BmsCharts =
            [
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(Path.Combine(candidateDirectoryPath, "candidate.bms")))
            ];
            SeedPendingPackages(library, songDbPath, pendingPackage);
            SetLibraryResourceIndex(library, BuildDirectoryLookupCache(sourceDirectoryPath, candidateDirectoryPath));

            library.SearchEstimatedInstallationDirectory(pendingPackage);

            PackageChartEntry pendingEntry = GetOnlyEntry(pendingPackage);
            Assert.IsTrue(string.IsNullOrWhiteSpace(pendingEntry.Chart.InstallDestination));
            Assert.AreEqual(emptyArtist ? "Target Song" : "Completely Different", pendingEntry.Chart.InstallDestinationTitle);
            Assert.AreEqual(emptyArtist ? string.Empty : "Another Artist", pendingEntry.Chart.InstallDestinationArtist);
            CollectionAssert.AreEqual(new[] { candidateDirectoryPath }, pendingEntry.Chart.InstallDestinationSuggestions.ToArray());
            Assert.IsTrue(ChartWarningTestHelpers.ContainsLowConfidenceInstallEstimationWarning(pendingEntry));
            Assert.IsTrue(pendingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationMetadataMismatch));
            StringAssert.Contains(ChartWarningTestHelpers.BuildTooltipText(pendingEntry), BeMusicSeeker.Properties.Resources.Warning_InstallEstimationMetadataMismatchPrefix);
            StringAssert.Contains(ChartWarningTestHelpers.BuildDigestText(pendingEntry), BeMusicSeeker.Properties.Resources.WarningDigest_InstallEstimationMetadataMismatch);
            StringAssert.Contains(ChartWarningTestHelpers.BuildTooltipText(pendingEntry), candidateDirectoryPath);
        });
    }

    /// <summary>候補が一件もないと、空の導入先・代表情報・候補一覧を保ち、曖昧さや不一致の警告を誤って付けない。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void SearchEstimatedInstallationDirectory_HighConfidenceWithoutCandidate_DoesNotSetLowConfidenceState()
    {

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageNoCandidate");
            string pendingFilePath = CreateBmsFileWithContents(sourceDirectoryPath, "pending.bms", "#PLAYER 1\r\n#TITLE Pending\r\n#ARTIST Test\r\n#WAVAA sound.wav\r\n#00111:AA\r\n");

            ChartFile pendingFile = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pendingFilePath));
            ChartPackage pendingPackage = ChartPackageTestExtensions.CreatePackage([pendingFile]);
            pendingPackage.path = pendingFilePath;
            pendingPackage.delete_parent = true;
            library.BmsCharts = [];
            SeedPendingPackages(library, songDbPath, pendingPackage);
            SetLibraryResourceIndex(library, BuildDirectoryLookupCache(sourceDirectoryPath));

            library.SearchEstimatedInstallationDirectory(pendingPackage);

            PackageChartEntry pendingEntry = GetOnlyEntry(pendingPackage);
            Assert.IsTrue(string.IsNullOrWhiteSpace(pendingEntry.Chart.InstallDestination));
            Assert.IsTrue(string.IsNullOrWhiteSpace(pendingEntry.Chart.InstallDestinationTitle));
            Assert.IsTrue(string.IsNullOrWhiteSpace(pendingEntry.Chart.InstallDestinationArtist));
            Assert.AreEqual(0, pendingEntry.Chart.InstallDestinationSuggestions.Count);
            Assert.IsFalse(ChartWarningTestHelpers.ContainsLowConfidenceInstallEstimationWarning(pendingEntry));
            Assert.AreEqual(string.Empty, ChartWarningTestHelpers.BuildDigestText(pendingEntry));
        });
    }

    /// <summary>所持ハッシュで結合先が解決したBMSパッケージでは、全entryの導入先と代表タイトル・作者を揃える。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void SearchMergeDestinationForPendingPackage_ResolvedPathUpdatesRepresentativeMetadata()
    {

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageMergeResolved");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Installed", "PackageMergeResolved");
            string pendingFileAPath = CreateBmsFileWithContents(sourceDirectoryPath, "pendingA.bms", "#PLAYER 1\r\n#TITLE Installed Title\r\n#ARTIST Installed Artist\r\n");
            string pendingFileBPath = CreateBmsFileWithContents(sourceDirectoryPath, "pendingB.bms", "#PLAYER 1\r\n#TITLE Installed Title\r\n#ARTIST Installed Artist\r\n");
            string installedFileAPath = CreateBmsFileWithContents(destinationDirectoryPath, "installedA.bms", "#PLAYER 1\r\n#TITLE Installed Title\r\n#ARTIST Installed Artist\r\n");
            string installedFileBPath = CreateBmsFileWithContents(destinationDirectoryPath, "installedB.bms", "#PLAYER 1\r\n#TITLE Installed Title\r\n#ARTIST Installed Artist\r\n");

            string minorityPath = CreateBmsFileWithContents(destinationDirectoryPath, "a.bms", "#TITLE Minority\r\n#ARTIST Other\r\n");
            ChartFile pendingFileA = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pendingFileAPath));
            ChartFile pendingFileB = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pendingFileBPath));
            ChartPackage pendingPackage = ChartPackageTestExtensions.CreatePackage([pendingFileA, pendingFileB]);
            pendingPackage.path = sourceDirectoryPath;
            pendingPackage.delete_parent = false;
            library.BmsCharts =
            [
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(minorityPath)),
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(installedFileAPath)),
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(installedFileBPath))
            ];
            SeedPendingPackages(library, songDbPath, pendingPackage);

            library.SearchMergeDestinationForPendingPackage(pendingPackage);

            foreach (PackageChartEntry pendingEntry in pendingPackage.ChartEntries)
            {
                Assert.AreEqual(destinationDirectoryPath, pendingEntry.Chart.InstallDestination);
                Assert.AreEqual("Installed Title", pendingEntry.Chart.InstallDestinationTitle);
                Assert.AreEqual("Installed Artist", pendingEntry.Chart.InstallDestinationArtist);
            }
        });
    }

    /// <summary>Bmsonの所持ハッシュから結合先を直接解決し、BMSが無く作者が空でも導入先と代表TITLEを反映する。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SearchMergeDestinationForPendingPackage_ResolvesBmsonFromInstalledBmsonHash(bool emptyArtist)
    {

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "BmsonMergeResolved");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Installed", "BmsonMergeResolved");
            string pendingBmsonPath = CreateBmsonFile(sourceDirectoryPath, "pending.bmson", "Installed Bmson", emptyArtist ? "" : "Bmson Artist");
            string installedBmsonPath = CreateBmsonFile(destinationDirectoryPath, "installed.bmson", "Installed Bmson", emptyArtist ? "" : "Bmson Artist");
            var pendingBmsonEntry = PackageChartEntry.FromChart((ChartTestValues.ReadBmson(pendingBmsonPath)));
            var pendingPackage = ChartPackage.FromChartEntries([pendingBmsonEntry]);
            pendingPackage.path = sourceDirectoryPath;
            pendingPackage.delete_parent = false;
            library.BmsCharts = [];
            library.BmsonCharts =
            [
                ChartTestValues.ReadBmson(installedBmsonPath)
            ];
            SeedPendingPackages(library, songDbPath, pendingPackage);

            library.SearchMergeDestinationForPendingPackage(pendingPackage);

            PackageChartEntry entry = pendingPackage.ChartEntries.Single();
            Assert.AreEqual(destinationDirectoryPath, entry.Chart.InstallDestination);
            Assert.AreEqual("Installed Bmson", entry.Chart.InstallDestinationTitle);
            Assert.AreEqual(emptyArtist ? string.Empty : "Bmson Artist", entry.Chart.InstallDestinationArtist);

        });
    }

    /// <summary>所持Bmsonの修復推定は現在パスの自己一致を候補から外し、リソースを満たす別ディレクトリを選ぶ。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void SearchCorrectInstallationDirectoryCharts_BmsonOwnedChartIgnoresCurrentInstalledIndexSelfMatch()
    {

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Installed", "BmsonWrong");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Installed", "BmsonCorrect");
            string sourceBmsonPath = CreateBmsonFile(sourceDirectoryPath, "source.bmson", "Repair Bmson", "Bmson Artist");
            string destinationBmsPath = CreateBmsFileWithContents(destinationDirectoryPath, "destination.bms", "#PLAYER 1\r\n#TITLE Repair Bmson\r\n#ARTIST Bmson Artist\r\n#WAVAA sound.wav\r\n#00111:AA\r\n");
            Directory.CreateDirectory(destinationDirectoryPath);
            File.WriteAllText(Path.Combine(destinationDirectoryPath, "sound.wav"), "sound");
            ChartFile sourceSong = ChartTestValues.ReadBmson(sourceBmsonPath);
            library.BmsCharts = [BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(destinationBmsPath))];
            library.BmsonCharts = [sourceSong];
            SetLibraryResourceIndex(library, BuildDirectoryLookupCache(sourceDirectoryPath, destinationDirectoryPath));

            var entry = PackageChartEntry.FromChart((sourceSong));
            library.SearchCorrectInstallationDirectoryCharts([entry]);

            Assert.AreEqual(destinationDirectoryPath, entry.Chart.InstallDestination);

        });
    }

    /// <summary>BMSとBmsonが混在しても両種の所持ハッシュを用いて、同じ結合先を全entryへ反映する。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void SearchMergeDestinationForPendingPackage_ResolvesMixedBmsAndBmsonPackage()
    {

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "MixedMergeResolved");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Installed", "MixedMergeResolved");
            string pendingBmsPath = CreateBmsFileWithContents(sourceDirectoryPath, "pending.bms", "#PLAYER 1\r\n#TITLE Mixed BMS\r\n#ARTIST Mixed Artist\r\n");
            string installedBmsPath = CreateBmsFileWithContents(destinationDirectoryPath, "installed.bms", "#PLAYER 1\r\n#TITLE Mixed BMS\r\n#ARTIST Mixed Artist\r\n");
            string pendingBmsonPath = CreateBmsonFile(sourceDirectoryPath, "pending.bmson", "Mixed Bmson", "Mixed Artist");
            string installedBmsonPath = CreateBmsonFile(destinationDirectoryPath, "installed.bmson", "Mixed Bmson", "Mixed Artist");
            ChartFile pendingBms = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pendingBmsPath));
            var pendingBmsonEntry = PackageChartEntry.FromChart((ChartTestValues.ReadBmson(pendingBmsonPath)));
            var pendingPackage = ChartPackage.FromChartEntries([PackageChartEntry.FromChart((pendingBms)), pendingBmsonEntry]);
            pendingPackage.path = sourceDirectoryPath;
            pendingPackage.delete_parent = false;
            library.BmsCharts =
            [
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(installedBmsPath))
            ];
            library.BmsonCharts =
            [
                ChartTestValues.ReadBmson(installedBmsonPath)
            ];
            SeedPendingPackages(library, songDbPath, pendingPackage);

            library.SearchMergeDestinationForPendingPackage(pendingPackage);

            foreach (PackageChartEntry pendingEntry in pendingPackage.ChartEntries)
            {
                Assert.AreEqual(destinationDirectoryPath, pendingEntry.Chart.InstallDestination);
            }

        });
    }

    /// <summary>混在譜面の所持先が分割されている場合、一致譜面数が多いディレクトリをBmsonを含む全entryの結合先にする。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void SearchMergeDestinationForPendingPackage_MixedSplitInstalledDirectoriesUsesMostMatchingDirectory()
    {

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "MixedMergeSplit");
            string primaryDestinationDirectoryPath = Path.Combine(tempRootPath, "Installed", "MixedMergePrimary");
            string secondaryDestinationDirectoryPath = Path.Combine(tempRootPath, "Installed", "MixedMergeSecondary");
            string pendingBmsAPath = CreateBmsFileWithContents(sourceDirectoryPath, "pendingA.bms", "#PLAYER 1\r\n#TITLE Majority BMS A\r\n#ARTIST Mixed Artist\r\n");
            string pendingBmsBPath = CreateBmsFileWithContents(sourceDirectoryPath, "pendingB.bms", "#PLAYER 1\r\n#TITLE Majority BMS B\r\n#ARTIST Mixed Artist\r\n");
            string installedBmsAPath = CreateBmsFileWithContents(primaryDestinationDirectoryPath, "installedA.bms", "#PLAYER 1\r\n#TITLE Majority BMS A\r\n#ARTIST Mixed Artist\r\n");
            string installedBmsBPath = CreateBmsFileWithContents(primaryDestinationDirectoryPath, "installedB.bms", "#PLAYER 1\r\n#TITLE Majority BMS B\r\n#ARTIST Mixed Artist\r\n");
            string pendingBmsonPath = CreateBmsonFile(sourceDirectoryPath, "pending.bmson", "Minority Bmson", "Mixed Artist");
            string installedBmsonPath = CreateBmsonFile(secondaryDestinationDirectoryPath, "installed.bmson", "Minority Bmson", "Mixed Artist");
            ChartFile pendingBmsA = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pendingBmsAPath));
            ChartFile pendingBmsB = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pendingBmsBPath));
            var pendingBmsonEntry = PackageChartEntry.FromChart((ChartTestValues.ReadBmson(pendingBmsonPath)));
            var pendingPackage = ChartPackage.FromChartEntries([PackageChartEntry.FromChart((pendingBmsA)), PackageChartEntry.FromChart((pendingBmsB)), pendingBmsonEntry]);
            pendingPackage.path = sourceDirectoryPath;
            pendingPackage.delete_parent = false;
            library.BmsCharts =
            [
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(installedBmsAPath)),
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(installedBmsBPath))
            ];
            library.BmsonCharts =
            [
                ChartTestValues.ReadBmson(installedBmsonPath)
            ];
            SeedPendingPackages(library, songDbPath, pendingPackage);

            library.SearchMergeDestinationForPendingPackage(pendingPackage);

            foreach (PackageChartEntry pendingEntry in pendingPackage.ChartEntries)
            {
                Assert.AreEqual(primaryDestinationDirectoryPath, pendingEntry.Chart.InstallDestination);
            }

        });
    }

    /// <summary>混在譜面の所持ハッシュ数が同数なら、実リソースを満たす候補へ評価を進め、ハッシュだけの候補を選ばない。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void SearchMergeDestinationForPendingPackage_MixedSplitTieFallsBackToResourceCandidate()
    {

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "MixedMergeTie");
            string resourceDestinationDirectoryPath = Path.Combine(tempRootPath, "Installed", "ResourceWinner");
            string hashOnlyDestinationDirectoryPath = Path.Combine(tempRootPath, "Installed", "HashOnlyTie");
            string pendingBmsPath = CreateBmsFileWithContents(sourceDirectoryPath, "pending.bms", "#PLAYER 1\r\n#TITLE Tie BMS\r\n#ARTIST Mixed Artist\r\n#WAVAA sound.wav\r\n#00111:AA\r\n");
            string installedBmsPath = CreateBmsFileWithContents(resourceDestinationDirectoryPath, "installed.bms", "#PLAYER 1\r\n#TITLE Tie BMS\r\n#ARTIST Mixed Artist\r\n#WAVAA sound.wav\r\n#00111:AA\r\n");
            string pendingBmsonPath = CreateBmsonFile(sourceDirectoryPath, "pending.bmson", "Tie Bmson", "Mixed Artist");
            string installedBmsonPath = CreateBmsonFile(hashOnlyDestinationDirectoryPath, "installed.bmson", "Tie Bmson", "Mixed Artist");
            File.WriteAllText(Path.Combine(resourceDestinationDirectoryPath, "sound.wav"), "resource");
            ChartFile pendingBms = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pendingBmsPath));
            var pendingBmsonEntry = PackageChartEntry.FromChart((ChartTestValues.ReadBmson(pendingBmsonPath)));
            var pendingPackage = ChartPackage.FromChartEntries([PackageChartEntry.FromChart((pendingBms)), pendingBmsonEntry]);
            pendingPackage.path = sourceDirectoryPath;
            pendingPackage.delete_parent = false;
            library.BmsCharts =
            [
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(installedBmsPath))
            ];
            library.BmsonCharts =
            [
                ChartTestValues.ReadBmson(installedBmsonPath)
            ];
            SeedPendingPackages(library, songDbPath, pendingPackage);
            SetLibraryResourceIndex(library, BuildDirectoryLookupCache(sourceDirectoryPath, resourceDestinationDirectoryPath, hashOnlyDestinationDirectoryPath));

            library.SearchMergeDestinationForPendingPackage(pendingPackage);

            foreach (PackageChartEntry pendingEntry in pendingPackage.ChartEntries)
            {
                Assert.AreEqual(resourceDestinationDirectoryPath, pendingEntry.Chart.InstallDestination);
            }

        });
    }

    /// <summary>譜面一覧からの結合先探索では入力元を候補から外し、外部候補の導入先・代表情報を反映する。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void SearchMergeDestinationForPendingCharts_UsesExternalCandidateOnly()
    {

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageMergeByFile");
            string candidateDirectoryPath = Path.Combine(tempRootPath, "Installed", "MergeCandidate");
            string pendingFilePath = CreateBmsFileWithContents(
                sourceDirectoryPath,
                "pending.bms",
                "#PLAYER 1\r\n#TITLE Installed Title\r\n#ARTIST Installed Artist obj: Diff\r\n#WAVAA 00.wav\r\n#WAVAB 01.wav\r\n#00111:AAAB\r\n");
            File.WriteAllText(Path.Combine(sourceDirectoryPath, "00.wav"), "src");
            File.WriteAllText(Path.Combine(sourceDirectoryPath, "01.wav"), "src");
            CreateBmsFileWithContents(candidateDirectoryPath, "installed.bms", "#PLAYER 1\r\n#TITLE Installed Title\r\n#ARTIST Installed Artist\r\n");
            File.WriteAllText(Path.Combine(candidateDirectoryPath, "00.wav"), "dst");
            File.WriteAllText(Path.Combine(candidateDirectoryPath, "01.wav"), "dst");

            ChartFile pendingFile = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pendingFilePath));
            ChartPackage pendingPackage = ChartPackageTestExtensions.CreatePackage([pendingFile]);
            pendingPackage.path = pendingFilePath;
            pendingPackage.delete_parent = true;
            library.BmsCharts = [BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(Path.Combine(candidateDirectoryPath, "installed.bms")))];
            SeedPendingPackages(library, songDbPath, pendingPackage);
            SetLibraryResourceIndex(library, BuildDirectoryLookupCache(sourceDirectoryPath, candidateDirectoryPath));

            var pendingEntry = PackageChartEntry.FromChart((pendingFile));
            library.SearchMergeDestinationForPendingCharts([pendingEntry]);

            Assert.AreEqual(candidateDirectoryPath, pendingEntry.Chart.InstallDestination);
            Assert.AreEqual("Installed Title", pendingEntry.Chart.InstallDestinationTitle);
            Assert.AreEqual("Installed Artist", pendingEntry.Chart.InstallDestinationArtist);
            Assert.AreEqual(0, pendingEntry.Chart.InstallDestinationSuggestions.Count);
        });
    }

    /// <summary>保留entryへ手動導入先を指定すると、代表タイトル・作者も更新し、低確度候補を新しく作らない。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void SetPendingInstallDestination_UpdatesRepresentativeMetadata()
    {

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string pendingDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageManual");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Installed", "Manual");
            string pendingFilePath = CreateBmsFileWithContents(pendingDirectoryPath, "pending.bms", "#PLAYER 1\r\n#TITLE Pending\r\n#ARTIST Test\r\n");
            string minorityPath = CreateBmsFileWithContents(destinationDirectoryPath, "a.bms", "#TITLE Minority\r\n#ARTIST Other\r\n");
            string installedFilePath = CreateBmsFileWithContents(destinationDirectoryPath, "installed.bms", "#PLAYER 1\r\n#TITLE Installed Title\r\n#SUBTITLE Alternate Name\r\n#ARTIST Installed Artist OBJ: Diff\r\n");
            string majorityPath = CreateBmsFileWithContents(destinationDirectoryPath, "z.bms", "#TITLE Installed Title\r\n#ARTIST Installed Artist\r\n");

            ChartFile pendingFile = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pendingFilePath));
            ChartPackage pendingPackage = ChartPackageTestExtensions.CreatePackage([pendingFile]);
            pendingPackage.path = pendingFilePath;
            pendingPackage.delete_parent = true;
            library.BmsCharts = [.. new[] { minorityPath, installedFilePath, majorityPath }
                .Select(path => BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(path)))];
            SeedPendingPackages(library, songDbPath, pendingPackage);

            PackageChartEntry pendingEntry = GetOnlyEntry(pendingPackage);
            bool succeeded = library.SetPendingInstallDestination(pendingEntry, destinationDirectoryPath);

            Assert.IsTrue(succeeded);
            Assert.AreEqual(destinationDirectoryPath, pendingEntry.Chart.InstallDestination);
            Assert.AreEqual("Installed Title", pendingEntry.Chart.InstallDestinationTitle);
            Assert.AreEqual("Installed Artist", pendingEntry.Chart.InstallDestinationArtist);
            Assert.AreEqual(0, pendingEntry.Chart.InstallDestinationSuggestions.Count);
            Assert.IsFalse(ChartWarningTestHelpers.ContainsLowConfidenceInstallEstimationWarning(pendingEntry));

            // 同じ所持パスの現在値を入れ替え、比較と表示の共通キャッシュが同じ失効で更新されることを確認します。
            library.BmsCharts = [.. library.BmsCharts.Select(chart => chart with { RawTitle = "Updated", Artist = "Updated Artist" })];
            Assert.IsTrue(library.SetPendingInstallDestination(pendingEntry, destinationDirectoryPath));
            Assert.AreEqual("Updated", pendingEntry.Chart.InstallDestinationTitle);
            Assert.AreEqual("Updated Artist", pendingEntry.Chart.InstallDestinationArtist);
            library.BmsCharts = [.. library.BmsCharts.Select(chart => chart with { RawTitle = string.Empty })];
            Assert.IsTrue(library.SetPendingInstallDestination(pendingEntry, destinationDirectoryPath));
            Assert.AreEqual(destinationDirectoryPath, pendingEntry.Chart.InstallDestination);
            Assert.AreEqual(string.Empty, pendingEntry.Chart.InstallDestinationTitle);
            Assert.AreEqual(string.Empty, pendingEntry.Chart.InstallDestinationArtist);
        });
    }

    /// <summary>Bmson保留へ親ディレクトリを手動指定すると、下位ディレクトリの所持譜面から代表情報を解決する。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void SetPendingInstallDestination_UpdatesAdapterlessBmsonEntryWithoutMaterializingAdapter()
    {

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string pendingDirectoryPath = Path.Combine(tempRootPath, "Pending", "BmsonManual");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Installed", "BmsonManual");
            string pendingBmsonPath = CreateBmsonFile(pendingDirectoryPath, "pending.bmson", "Pending Bmson", "Pending Artist");
            string nestedDestinationDirectoryPath = Path.Combine(destinationDirectoryPath, "Nested");
            string installedBmsonPath = CreateBmsonFile(nestedDestinationDirectoryPath, "installed.bmson", "Installed Bmson", "Installed Artist");
            File.WriteAllText(installedBmsonPath, """
                {"version":"1.0.0","info":{"title":"Installed Bmson","subtitle":"Alternate Name","chart_name":"ANOTHER","artist":"Installed Artist"},"sound_channels":[]}
                """);
            ChartFile pendingSong = ChartTestValues.ReadBmson(pendingBmsonPath);
            var entry = PackageChartEntry.FromChart((pendingSong));
            var pendingPackage = ChartPackage.FromChartEntries([entry]);
            pendingPackage.path = pendingDirectoryPath;
            pendingPackage.delete_parent = false;
            library.BmsCharts = [];
            library.BmsonCharts = [ChartTestValues.ReadBmson(installedBmsonPath)];
            SetLibraryResourceIndex(library, BuildDirectoryLookupCache(destinationDirectoryPath, nestedDestinationDirectoryPath));
            SeedPendingPackages(library, songDbPath, pendingPackage);
            bool succeeded = library.SetPendingInstallDestination(entry, destinationDirectoryPath);

            Assert.IsTrue(succeeded);

            Assert.AreEqual(destinationDirectoryPath, entry.Chart.InstallDestination);
            Assert.AreEqual("Installed Bmson", entry.Chart.InstallDestinationTitle);
            Assert.AreEqual("Installed Artist", entry.Chart.InstallDestinationArtist);
        });
    }

    /// <summary>パッケージに属さない所持BMSも手動指定を受理し、所持候補の導入先と代表情報を反映する。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void SetPendingInstallDestination_AllowsStandaloneLibraryFileForFullScan()
    {

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Library", "Source");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Library", "Destination");
            string sourceFilePath = CreateBmsFileWithContents(sourceDirectoryPath, "source.bms", "#PLAYER 1\r\n#TITLE Source\r\n#ARTIST Test\r\n");
            string destinationFilePath = CreateBmsFileWithContents(destinationDirectoryPath, "destination.bms", "#PLAYER 1\r\n#TITLE Destination Title\r\n#ARTIST Destination Artist\r\n");

            ChartFile sourceFile = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(sourceFilePath));
            library.BmsCharts =
            [
                sourceFile,
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(destinationFilePath))
            ];

            var sourceEntry = PackageChartEntry.FromChart((sourceFile));
            bool succeeded = library.SetPendingInstallDestination(sourceEntry, destinationDirectoryPath);

            Assert.IsTrue(succeeded);
            Assert.AreEqual(destinationDirectoryPath, sourceEntry.Chart.InstallDestination);
            Assert.AreEqual("Destination Title", sourceEntry.Chart.InstallDestinationTitle);
            Assert.AreEqual("Destination Artist", sourceEntry.Chart.InstallDestinationArtist);
            Assert.AreEqual(0, sourceEntry.Chart.InstallDestinationSuggestions.Count);
            Assert.IsFalse(ChartWarningTestHelpers.ContainsLowConfidenceInstallEstimationWarning(sourceEntry));
        });
    }

    /// <summary>パッケージに属さない所持Bmsonも手動指定を受理し、導入先・代表情報を更新して推定警告を残さない。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void SetPendingInstallDestination_AllowsStandaloneLibraryBmsonForFullScanWithoutAdapter()
    {

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Library", "BmsonSource");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Library", "BmsonDestination");
            string sourceBmsonPath = CreateBmsonFile(sourceDirectoryPath, "source.bmson", "Source Bmson", "Source Artist");
            string destinationBmsonPath = CreateBmsonFile(destinationDirectoryPath, "destination.bmson", "Destination Bmson", "Destination Artist");
            ChartFile sourceSong = ChartTestValues.ReadBmson(sourceBmsonPath);
            ChartFile destinationSong = ChartTestValues.ReadBmson(destinationBmsonPath);
            var sourceEntry = PackageChartEntry.FromChart((sourceSong));
            library.BmsCharts = [];
            library.BmsonCharts = [sourceSong, destinationSong];

            bool succeeded = library.SetPendingInstallDestination(sourceEntry, destinationDirectoryPath);

            Assert.IsTrue(succeeded);

            Assert.AreEqual(destinationDirectoryPath, sourceEntry.Chart.InstallDestination);
            Assert.AreEqual("Destination Bmson", sourceEntry.Chart.InstallDestinationTitle);
            Assert.AreEqual("Destination Artist", sourceEntry.Chart.InstallDestinationArtist);
            Assert.AreEqual(0, sourceEntry.Chart.InstallDestinationSuggestions.Count);
            Assert.IsFalse(sourceEntry.Chart.Warnings.Any(warning => warning.Category == ChartWarningCategory.InstallEstimation));
        });
    }

    /// <summary>所持集合にも保留にも属さない外部譜面の手動導入先指定を拒否し、管理対象として受理しない。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void SetPendingInstallDestination_RejectsStandaloneFileThatIsNotInLibrary()
    {

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "External", "Source");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Library", "Destination");
            string sourceFilePath = CreateBmsFileWithContents(sourceDirectoryPath, "source.bms", "#PLAYER 1\r\n#TITLE Source\r\n#ARTIST Test\r\n");
            string destinationFilePath = CreateBmsFileWithContents(destinationDirectoryPath, "destination.bms", "#PLAYER 1\r\n#TITLE Destination Title\r\n#ARTIST Destination Artist\r\n");

            ChartFile sourceFile = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(sourceFilePath));
            library.BmsCharts =
            [
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(destinationFilePath))
            ];

            bool succeeded = library.SetPendingInstallDestination(PackageChartEntry.FromChart((sourceFile)), destinationDirectoryPath);

            Assert.IsFalse(succeeded);
        });
    }

    /// <summary>不一致候補からの手動選択は同じパッケージの全entryへ反映し、候補と理由を保ちながら別パッケージを変更しない。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void SetPendingInstallDestination_FromLowConfidenceCandidates_PreservesWarningAndSuggestions()
    {

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageLowConfidenceManual");
            string candidateADirectoryPath = Path.Combine(tempRootPath, "Installed", "A");
            string candidateBDirectoryPath = Path.Combine(tempRootPath, "Installed", "B");
            string pendingFilePath = CreateBmsFileWithContents(sourceDirectoryPath, "pending.bms", "#PLAYER 1\r\n#TITLE Pending\r\n#ARTIST Test\r\n#WAVAA sound.wav\r\n#00111:AA\r\n");
            CreateBmsFileWithContents(candidateADirectoryPath, "candidateA.bms", "#PLAYER 1\r\n#TITLE Candidate A\r\n#ARTIST Artist A\r\n");
            CreateBmsFileWithContents(candidateBDirectoryPath, "candidateB.bms", "#PLAYER 1\r\n#TITLE Candidate B\r\n#ARTIST Artist B\r\n");
            string minorityPath = CreateBmsFileWithContents(candidateBDirectoryPath, "a-minority.bms", "#TITLE Minority\r\n#ARTIST Other\r\n");
            string majorityPath = CreateBmsFileWithContents(candidateBDirectoryPath, "z-majority.bms", "#TITLE Candidate B\r\n#ARTIST Artist B\r\n");
            File.WriteAllText(Path.Combine(candidateADirectoryPath, "sound.wav"), "a");
            File.WriteAllText(Path.Combine(candidateBDirectoryPath, "sound.wav"), "b");

            ChartFile pendingFile = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pendingFilePath));
            string secondFilePath = CreateBmsFileWithContents(sourceDirectoryPath, "second.bms", "#PLAYER 1\r\n#TITLE Second\r\n#ARTIST Test\r\n#WAVAA sound.wav\r\n#00111:AA\r\n");
            ChartPackage pendingPackage = ChartPackageTestExtensions.CreatePackage([pendingFile, BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(secondFilePath))]);
            pendingPackage.path = sourceDirectoryPath;
            pendingPackage.delete_parent = false;
            library.BmsCharts =
            [
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(minorityPath)),
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(majorityPath)),
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(Path.Combine(candidateADirectoryPath, "candidateA.bms"))),
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(Path.Combine(candidateBDirectoryPath, "candidateB.bms")))
            ];
            string otherFilePath = CreateBmsFileWithContents(Path.Combine(tempRootPath, "Other"), "other.bms", "#PLAYER 1\r\n#TITLE Other\r\n");
            ChartPackage otherPackage = ChartPackageTestExtensions.CreatePackage([BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(otherFilePath))]);
            otherPackage.path = otherFilePath;
            otherPackage.delete_parent = true;
            PackageChartEntry otherEntry = GetOnlyEntry(otherPackage);
            otherEntry.ApplyInstallDestination(candidateADirectoryPath, "Other title", "Other artist");
            SeedPendingPackages(library, songDbPath, pendingPackage, otherPackage);
            SetLibraryResourceIndex(library, BuildDirectoryLookupCache(sourceDirectoryPath, candidateADirectoryPath, candidateBDirectoryPath));

            library.SearchEstimatedInstallationDirectory(pendingPackage);

            PackageChartEntry pendingEntry = pendingPackage.ChartEntries.First();
            bool succeeded = library.SetPendingInstallDestination(pendingEntry, candidateBDirectoryPath);

            Assert.IsTrue(succeeded);
            foreach (PackageChartEntry entry in pendingPackage.ChartEntries)
            {
                Assert.AreEqual(candidateBDirectoryPath, entry.Chart.InstallDestination);
                Assert.AreEqual("Candidate B", entry.Chart.InstallDestinationTitle);
                Assert.AreEqual("Artist B", entry.Chart.InstallDestinationArtist);
                CollectionAssert.AreEquivalent(new[] { candidateADirectoryPath, candidateBDirectoryPath }, entry.Chart.InstallDestinationSuggestions.ToArray());
                Assert.IsTrue(entry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationMetadataMismatch));
            }
            Assert.AreEqual(candidateADirectoryPath, otherEntry.Chart.InstallDestination);
            Assert.AreEqual("Other title", otherEntry.Chart.InstallDestinationTitle);
            Assert.AreEqual("Other artist", otherEntry.Chart.InstallDestinationArtist);
            Assert.AreEqual(0, otherEntry.Chart.InstallDestinationSuggestions.Count);
            Assert.IsTrue(ChartWarningTestHelpers.ContainsLowConfidenceInstallEstimationWarning(pendingEntry));
            Assert.IsTrue(pendingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationMetadataMismatch));
            StringAssert.Contains(ChartWarningTestHelpers.BuildTooltipText(pendingEntry), BeMusicSeeker.Properties.Resources.Warning_InstallEstimationMetadataMismatchPrefix);
            StringAssert.Contains(ChartWarningTestHelpers.BuildTooltipText(pendingEntry), candidateADirectoryPath);
        });
    }

    /// <summary>Bmsonの不一致候補を手動選択しても、選択先の代表情報と元の両候補・不一致警告を同じentryに保持する。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void SetPendingInstallDestination_FromAdapterlessBmsonLowConfidenceCandidate_PreservesWarningAndSuggestions()
    {

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageBmsonLowConfidenceManual");
            string candidateADirectoryPath = Path.Combine(tempRootPath, "Installed", "A");
            string candidateBDirectoryPath = Path.Combine(tempRootPath, "Installed", "B");
            string pendingBmsonPath = CreateBmsonFile(sourceDirectoryPath, "pending.bmson", "Pending Bmson", "Pending Artist");
            CreateBmsFileWithContents(candidateADirectoryPath, "candidateA.bms", "#PLAYER 1\r\n#TITLE Candidate A\r\n#ARTIST Artist A\r\n");
            CreateBmsFileWithContents(candidateBDirectoryPath, "candidateB.bms", "#PLAYER 1\r\n#TITLE Candidate B\r\n#ARTIST Artist B\r\n");
            File.WriteAllText(Path.Combine(candidateADirectoryPath, "sound.wav"), "a");
            File.WriteAllText(Path.Combine(candidateBDirectoryPath, "sound.wav"), "b");

            ChartFile pendingSong = ChartTestValues.ReadBmson(pendingBmsonPath);
            var entry = PackageChartEntry.FromChart((pendingSong));
            var pendingPackage = ChartPackage.FromChartEntries([entry]);
            pendingPackage.path = sourceDirectoryPath;
            pendingPackage.delete_parent = false;
            library.BmsCharts =
            [
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(Path.Combine(candidateADirectoryPath, "candidateA.bms"))),
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(Path.Combine(candidateBDirectoryPath, "candidateB.bms")))
            ];
            SeedPendingPackages(library, songDbPath, pendingPackage);
            SetLibraryResourceIndex(library, BuildDirectoryLookupCache(sourceDirectoryPath, candidateADirectoryPath, candidateBDirectoryPath));

            library.SearchEstimatedInstallationDirectory(pendingPackage);

            bool succeeded = library.SetPendingInstallDestination(entry, candidateBDirectoryPath);

            Assert.IsTrue(succeeded);

            Assert.AreEqual(candidateBDirectoryPath, entry.Chart.InstallDestination);
            Assert.AreEqual("Candidate B", entry.Chart.InstallDestinationTitle);
            Assert.AreEqual("Artist B", entry.Chart.InstallDestinationArtist);
            CollectionAssert.AreEquivalent(new[] { candidateADirectoryPath, candidateBDirectoryPath }, entry.Chart.InstallDestinationSuggestions.ToArray());
            Assert.IsTrue(entry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationMetadataMismatch));
        });
    }

    /// <summary>手動解除と任意の実ディレクトリ指定は不一致候補を保ち、存在しない後続指定を拒否して確定済み情報を戻さない。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void SetPendingInstallDestination_WithManualDirectory_PreservesLowConfidenceState()
    {

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageLowConfidenceManualFree");
            string candidateADirectoryPath = Path.Combine(tempRootPath, "Installed", "A");
            string candidateBDirectoryPath = Path.Combine(tempRootPath, "Installed", "B");
            string manualDirectoryPath = Path.Combine(tempRootPath, "Installed", "Manual");
            string pendingFilePath = CreateBmsFileWithContents(sourceDirectoryPath, "pending.bms", "#PLAYER 1\r\n#TITLE Pending\r\n#ARTIST Test\r\n#WAVAA sound.wav\r\n#00111:AA\r\n");
            CreateBmsFileWithContents(candidateADirectoryPath, "candidateA.bms", "#PLAYER 1\r\n#TITLE Candidate A\r\n#ARTIST Artist A\r\n");
            CreateBmsFileWithContents(candidateBDirectoryPath, "candidateB.bms", "#PLAYER 1\r\n#TITLE Candidate B\r\n#ARTIST Artist B\r\n");
            string manualInstalledFilePath = CreateBmsFileWithContents(manualDirectoryPath, "manual.bms", "#PLAYER 1\r\n#TITLE Manual Title\r\n#ARTIST Manual Artist\r\n");
            File.WriteAllText(Path.Combine(candidateADirectoryPath, "sound.wav"), "a");
            File.WriteAllText(Path.Combine(candidateBDirectoryPath, "sound.wav"), "b");

            ChartFile pendingFile = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pendingFilePath));
            ChartPackage pendingPackage = ChartPackageTestExtensions.CreatePackage([pendingFile]);
            pendingPackage.path = pendingFilePath;
            pendingPackage.delete_parent = true;
            library.BmsCharts =
            [
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(Path.Combine(candidateADirectoryPath, "candidateA.bms"))),
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(Path.Combine(candidateBDirectoryPath, "candidateB.bms"))),
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(manualInstalledFilePath))
            ];
            SeedPendingPackages(library, songDbPath, pendingPackage);
            SetLibraryResourceIndex(library, BuildDirectoryLookupCache(sourceDirectoryPath, candidateADirectoryPath, candidateBDirectoryPath, manualDirectoryPath));

            library.SearchEstimatedInstallationDirectory(pendingPackage);

            PackageChartEntry pendingEntry = GetOnlyEntry(pendingPackage);
            Assert.IsTrue(library.SetPendingInstallDestination(pendingEntry, string.Empty));
            Assert.IsTrue(string.IsNullOrEmpty(pendingEntry.Chart.InstallDestination));
            Assert.AreEqual(string.Empty, pendingEntry.Chart.InstallDestinationTitle);
            Assert.AreEqual(string.Empty, pendingEntry.Chart.InstallDestinationArtist);
            CollectionAssert.AreEquivalent(new[] { candidateADirectoryPath, candidateBDirectoryPath }, pendingEntry.Chart.InstallDestinationSuggestions.ToArray());
            Assert.IsTrue(pendingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationMetadataMismatch));
            bool succeeded = library.SetPendingInstallDestination(pendingEntry, manualDirectoryPath);

            Assert.IsTrue(succeeded);
            Assert.AreEqual(manualDirectoryPath, pendingEntry.Chart.InstallDestination);
            Assert.AreEqual("Manual Title", pendingEntry.Chart.InstallDestinationTitle);
            Assert.AreEqual("Manual Artist", pendingEntry.Chart.InstallDestinationArtist);
            CollectionAssert.AreEquivalent(new[] { candidateADirectoryPath, candidateBDirectoryPath }, pendingEntry.Chart.InstallDestinationSuggestions.ToArray());
            Assert.IsTrue(ChartWarningTestHelpers.ContainsLowConfidenceInstallEstimationWarning(pendingEntry));
            Assert.IsTrue(pendingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationMetadataMismatch));
            ChartWarning[] warningsBeforeRejection = pendingEntry.Chart.Warnings.ToArray();
            Assert.IsFalse(library.SetPendingInstallDestination(pendingEntry, Path.Combine(tempRootPath, "Missing")));
            Assert.AreEqual(manualDirectoryPath, pendingEntry.Chart.InstallDestination);
            Assert.AreEqual("Manual Title", pendingEntry.Chart.InstallDestinationTitle);
            Assert.AreEqual("Manual Artist", pendingEntry.Chart.InstallDestinationArtist);
            CollectionAssert.AreEquivalent(new[] { candidateADirectoryPath, candidateBDirectoryPath }, pendingEntry.Chart.InstallDestinationSuggestions.ToArray());
            CollectionAssert.AreEqual(warningsBeforeRejection, pendingEntry.Chart.Warnings.ToArray());
        });
    }

    /// <summary>導入先の明示消去は曖昧推定の代表情報・候補・表示警告をすべて除き、古い警告行を残さない。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void RemoveInstallDestination_ClearsAllAmbiguousWarningLinesAndRepresentativeMetadata()
    {

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageLowConfidenceClear");
            string candidateADirectoryPath = Path.Combine(tempRootPath, "Installed", "A");
            string candidateBDirectoryPath = Path.Combine(tempRootPath, "Installed", "B");
            string pendingFilePath = CreateBmsFileWithContents(sourceDirectoryPath, "pending.bms", "#PLAYER 1\r\n#TITLE Pending\r\n#ARTIST Test\r\n#WAVAA sound.wav\r\n#00111:AA\r\n");
            CreateBmsFileWithContents(candidateADirectoryPath, "candidateA.bms", "#PLAYER 1\r\n#TITLE Pending\r\n#ARTIST Test\r\n");
            CreateBmsFileWithContents(candidateBDirectoryPath, "candidateB.bms", "#PLAYER 1\r\n#TITLE Pending\r\n#ARTIST Test\r\n");
            File.WriteAllText(Path.Combine(candidateADirectoryPath, "sound.wav"), "a");
            File.WriteAllText(Path.Combine(candidateBDirectoryPath, "sound.wav"), "b");

            ChartFile pendingFile = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pendingFilePath));
            ChartPackage pendingPackage = ChartPackageTestExtensions.CreatePackage([pendingFile]);
            pendingPackage.path = pendingFilePath;
            pendingPackage.delete_parent = true;
            library.BmsCharts =
            [
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(Path.Combine(candidateADirectoryPath, "candidateA.bms"))),
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(Path.Combine(candidateBDirectoryPath, "candidateB.bms")))
            ];
            SeedPendingPackages(library, songDbPath, pendingPackage);
            SetLibraryResourceIndex(library, BuildDirectoryLookupCache(sourceDirectoryPath, candidateADirectoryPath, candidateBDirectoryPath));

            library.SearchEstimatedInstallationDirectory(pendingPackage);
            PackageChartEntry pendingEntry = GetOnlyEntry(pendingPackage);
            Assert.IsTrue(pendingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationAmbiguous));

            library.RemoveInstallDestination([pendingEntry]);

            Assert.IsTrue(string.IsNullOrWhiteSpace(pendingEntry.Chart.InstallDestination));
            Assert.IsTrue(string.IsNullOrWhiteSpace(pendingEntry.Chart.InstallDestinationTitle));
            Assert.IsTrue(string.IsNullOrWhiteSpace(pendingEntry.Chart.InstallDestinationArtist));
            Assert.AreEqual(0, pendingEntry.Chart.InstallDestinationSuggestions.Count);
            Assert.IsFalse(ChartWarningTestHelpers.ContainsLowConfidenceInstallEstimationWarning(pendingEntry));
            Assert.IsFalse(pendingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationAmbiguous));
            Assert.AreEqual(string.Empty, ChartWarningTestHelpers.BuildDigestText(pendingEntry));
        });
    }

    /// <summary>メタデータ不一致からの手動変更や空指定は不一致の文脈を保持し、明示消去時だけ推定警告を除いて他の警告を保つ。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void SetPendingInstallDestination_MetadataMismatchEditsPreserveContextUntilExplicitClear()
    {

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageMetadataMismatchClear");
            string candidateDirectoryPath = Path.Combine(tempRootPath, "Installed", "OnlyCandidate");
            string pendingFilePath = CreateBmsFileWithContents(sourceDirectoryPath, "pending.bms", "#PLAYER 1\r\n#TITLE Target Song\r\n#ARTIST Base Artist obj: Diff\r\n#WAVAA sound.wav\r\n#00111:AA\r\n");
            CreateBmsFileWithContents(candidateDirectoryPath, "candidate.bms", "#PLAYER 1\r\n#TITLE Completely Different\r\n#ARTIST Another Artist\r\n");
            File.WriteAllText(Path.Combine(candidateDirectoryPath, "sound.wav"), "dst");
            string manualDirectoryPath = Path.Combine(tempRootPath, "Installed", "Manual");
            string manualFilePath = CreateBmsFileWithContents(manualDirectoryPath, "manual.bms", "#PLAYER 1\r\n#TITLE Manual Title\r\n#ARTIST Manual Artist\r\n");

            ChartFile pendingFile = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pendingFilePath));
            ChartPackage pendingPackage = ChartPackageTestExtensions.CreatePackage([pendingFile]);
            pendingPackage.path = pendingFilePath;
            pendingPackage.delete_parent = true;
            library.BmsCharts =
            [
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(Path.Combine(candidateDirectoryPath, "candidate.bms"))),
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(manualFilePath))
            ];
            SeedPendingPackages(library, songDbPath, pendingPackage);
            SetLibraryResourceIndex(library, BuildDirectoryLookupCache(sourceDirectoryPath, candidateDirectoryPath));

            pendingPackage.ChartEntries.Single().SetWarning(ChartWarningKind.SingleBmsFile, BeMusicSeeker.Properties.Resources.Warning_SingleBmsFile);
            library.SearchEstimatedInstallationDirectory(pendingPackage);
            PackageChartEntry pendingEntry = GetOnlyEntry(pendingPackage);
            Assert.IsTrue(pendingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationMetadataMismatch));

            ChartWarning[] estimationWarnings = pendingEntry.Chart.Warnings.Where(warning => warning.Category == ChartWarningCategory.InstallEstimation).ToArray();
            Assert.IsTrue(library.SetPendingInstallDestination(pendingEntry, candidateDirectoryPath));
            Assert.AreEqual(candidateDirectoryPath, pendingEntry.Chart.InstallDestination);
            Assert.AreEqual("Completely Different", pendingEntry.Chart.InstallDestinationTitle);
            Assert.AreEqual("Another Artist", pendingEntry.Chart.InstallDestinationArtist);
            Assert.IsTrue(library.SetPendingInstallDestination(pendingEntry, string.Empty));
            Assert.IsTrue(string.IsNullOrEmpty(pendingEntry.Chart.InstallDestination));
            Assert.AreEqual(string.Empty, pendingEntry.Chart.InstallDestinationTitle);
            Assert.AreEqual(string.Empty, pendingEntry.Chart.InstallDestinationArtist);
            CollectionAssert.AreEqual(new[] { candidateDirectoryPath }, pendingEntry.Chart.InstallDestinationSuggestions.ToArray());
            CollectionAssert.AreEqual(estimationWarnings, pendingEntry.Chart.Warnings.Where(warning => warning.Category == ChartWarningCategory.InstallEstimation).ToArray());
            Assert.IsTrue(library.SetPendingInstallDestination(pendingEntry, manualDirectoryPath));
            Assert.AreEqual(manualDirectoryPath, pendingEntry.Chart.InstallDestination);
            Assert.AreEqual("Manual Title", pendingEntry.Chart.InstallDestinationTitle);
            Assert.AreEqual("Manual Artist", pendingEntry.Chart.InstallDestinationArtist);
            CollectionAssert.AreEqual(new[] { candidateDirectoryPath }, pendingEntry.Chart.InstallDestinationSuggestions.ToArray());
            CollectionAssert.AreEqual(estimationWarnings, pendingEntry.Chart.Warnings.Where(warning => warning.Category == ChartWarningCategory.InstallEstimation).ToArray());
            ChartWarning[] otherWarnings = pendingEntry.Chart.Warnings.Where(warning => warning.Category != ChartWarningCategory.InstallEstimation).ToArray();
            Assert.IsTrue(otherWarnings.Length > 0);
            library.RemoveInstallDestination([pendingEntry]);
            CollectionAssert.AreEqual(otherWarnings, pendingEntry.Chart.Warnings.ToArray());

            Assert.IsTrue(string.IsNullOrWhiteSpace(pendingEntry.Chart.InstallDestination));
            Assert.IsTrue(string.IsNullOrWhiteSpace(pendingEntry.Chart.InstallDestinationTitle));
            Assert.IsTrue(string.IsNullOrWhiteSpace(pendingEntry.Chart.InstallDestinationArtist));
            Assert.AreEqual(0, pendingEntry.Chart.InstallDestinationSuggestions.Count);
            Assert.IsFalse(ChartWarningTestHelpers.ContainsLowConfidenceInstallEstimationWarning(pendingEntry));
            Assert.IsFalse(pendingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationMetadataMismatch));
        });
    }

    private static void InvokeRegroupForSourceDirectories(BMSLibrary library, params string[] sourceDirectoryPaths)
    {
        MethodInfo? regroupMethod = typeof(BMSLibrary).GetMethod("TryRegroupPendingPackagesForSourceDirectoriesUnsafe", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(regroupMethod);
        regroupMethod!.Invoke(library, [sourceDirectoryPaths]);
    }

    private static ChartPackage AssertRegroupedPendingPackage(BMSLibrary library, string expectedPackagePath, string expectedDestinationDirectory, int expectedFileCount)
    {
        ChartPackage regroupedPackage = AssertRegroupedPendingPackage(library, expectedPackagePath, expectedFileCount);
        Assert.IsTrue(regroupedPackage.ChartEntries.All(entry => string.Equals(entry.Chart.InstallDestination, expectedDestinationDirectory, StringComparison.OrdinalIgnoreCase)));
        return regroupedPackage;
    }

    private static ChartPackage AssertRegroupedPendingPackage(BMSLibrary library, string expectedPackagePath, int expectedFileCount)
    {
        Assert.AreEqual(1, library.ChartPackagesPending.Count);
        ChartPackage regroupedPackage = library.ChartPackagesPending.Single();
        Assert.AreEqual(expectedPackagePath, regroupedPackage.path);
        Assert.IsFalse(regroupedPackage.delete_parent);
        Assert.AreEqual(expectedFileCount, regroupedPackage.ChartEntries.Count);
        return regroupedPackage;
    }

    private static void AssertPendingPackagePaths(BMSLibrary library, params string[] expectedPaths)
    {
        Assert.AreEqual(expectedPaths.Length, library.ChartPackagesPending.Count);
        CollectionAssert.AreEquivalent(
            expectedPaths,
            library.ChartPackagesPending.Select(package => package.path).ToArray());
    }

    private static void SeedPendingPackages(BMSLibrary library, string songDbPath, params ChartPackage[] packages)
    {
        library.ChartPackagesPending = CreatePackageCollection(packages);
        // pending packageのschemaとseedだけを一つのtransactionへまとめ、本番の保存契約は変更せずfixtureのautocommit反復を減らす。
        BmsLibraryInitializationTestSupport.ExecuteSongDbFixtureTransaction(songDbPath, songDb =>
        {
            songDb.CreateTable<LR2SongDBExtended.install>();
            foreach (ChartPackage package in packages)
            {
                songDb.InsertOrReplace(package, typeof(LR2SongDBExtended.install));
            }
        });
    }

    private static DirectoryResourceLookupCache BuildDirectoryLookupCache(params string[] directories)
    {
        var cache = new DirectoryResourceLookupCache();
        foreach (string directoryPath in directories.Where(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path)))
        {
            cache.AddDir(directoryPath, Directory.GetFiles(directoryPath, "*", SearchOption.TopDirectoryOnly).Select(Path.GetFileName));
        }
        return cache;
    }

    private static void SetLibraryResourceIndex(BMSLibrary library, DirectoryResourceLookupCache cache)
    {
        var index = new LibraryResourceIndex();
        typeof(LibraryResourceIndex)
            .GetProperty(nameof(LibraryResourceIndex.DirectoryLookupCache), BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .SetValue(index, cache);
        FieldInfo ownerField = typeof(BMSLibrary).GetField(
            "libraryResourceIndexOwner",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var owner = (LibraryResourceIndexOwner)ownerField.GetValue(library)!;
        owner.Replace(index);
    }

    private static string[] LoadInstallPaths(string songDbPath)
    {
        // 全呼出し元はSeedPendingPackagesでinstall表を準備済みなので、SELECT専用read-only接続で観測する。
        using LR2SongDBExtended songDb = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
        return [.. songDb.Query<InstallRowRecord>("SELECT path FROM install")
            .Select(row => row.path)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];
    }

    private static ChartPackage CreatePendingSingleFilePackage(string filePath, string installDestination = "")
    {
        ChartFile file = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(filePath));
        var entry = PackageChartEntry.FromChart((file));
        if (!string.IsNullOrWhiteSpace(installDestination))
        {
            entry.SetInstallDestinationPathOnly(installDestination);
        }
        var package = ChartPackage.FromChartEntries([entry]);
        package.path = filePath;
        package.delete_parent = true;
        return package;
    }

    private static PackageChartEntry GetOnlyEntry(ChartPackage package)
    {
        Assert.IsNotNull(package);
        return package.ChartEntries.Single();
    }

    private static PackageChartEntry GetEntryByFileName(ChartPackage package, string fileName)
    {
        Assert.IsNotNull(package);
        return package.ChartEntries.Single(entry => Path.GetFileName(entry.Chart.Path).Equals(fileName, StringComparison.OrdinalIgnoreCase));
    }

    private static string CreateBmsFile(string directoryPath, string fileName, string titleSuffix)
    {
        return CreateBmsFileWithContents(directoryPath, fileName, "#PLAYER 1\r\n#TITLE " + titleSuffix + "\r\n#ARTIST Test\r\n");
    }

    private static string CreateBmsFileWithContents(string directoryPath, string fileName, string contents)
    {
        Directory.CreateDirectory(directoryPath);
        string filePath = Path.Combine(directoryPath, fileName);
        File.WriteAllText(filePath, contents);
        return filePath;
    }

    private static string CreateBmsonFile(string directoryPath, string fileName, string title, string artist)
    {
        Directory.CreateDirectory(directoryPath);
        string filePath = Path.Combine(directoryPath, fileName);
        File.WriteAllText(filePath, "{"
            + "\"version\":\"1.0.0\","
            + "\"info\":{\"title\":\"" + title + "\",\"artist\":\"" + artist + "\",\"mode_hint\":\"beat-7k\"},"
            + "\"sound_channels\":[{\"name\":\"sound.wav\",\"notes\":[{\"x\":1,\"y\":0,\"l\":0}]}]"
            + "}");
        return filePath;
    }

    private static string CreateHealthyBmsonFile(string directoryPath, string fileName, string title, string artist)
    {
        Directory.CreateDirectory(directoryPath);
        string filePath = Path.Combine(directoryPath, fileName);
        File.WriteAllText(filePath, "{"
            + "\"version\":\"1.0.0\","
            + "\"info\":{\"title\":\"" + title + "\",\"artist\":\"" + artist + "\",\"mode_hint\":\"beat-7k\"},"
            + "\"sound_channels\":[]"
            + "}");
        return filePath;
    }

    private static ChartFile WithChartPath(ChartFile source, string path)
    {
        return new ChartFile(source.Kind, path, source.Md5, source.Sha256, source.Title, source.RawTitle, source.Artist, source.Genre, source.Folder, source.Tag, source.LevelText, source.Level, source.Mode, source.ChartInfo, source.Subtitle, source.Resources, source.Stagefile, source.Backbmp, source.Banner, source.InstallDestination, source.InstallDestinationTitle, source.InstallDestinationArtist, source.InstallDestinationSuggestions, source.Warnings, source.WAVHealth, source.BGAHealth, source.MovieHealth, source.StagefileHealth, source.BannerHealth, source.BackbmpHealth, source.EncodingName);
    }

    private static void ApplySingleFileWarnings(params ChartPackage[] packages)
    {
        foreach (PackageChartEntry entry in (packages ?? []).Where(package => package != null).SelectMany(package => package.ChartEntries).Where(entry => entry.Chart.Kind == ChartFileKind.Bms))
        {
            entry.SetWarning(ChartWarningKind.SingleBmsFile, BeMusicSeeker.Properties.Resources.Warning_SingleBmsFile);
        }
    }

    private static ObservableCollection<ChartPackage> CreatePackageCollection(IEnumerable<ChartPackage> packages)
    {
        return new ObservableCollection<ChartPackage>([.. (packages ?? [])]);
    }

    private void WithTemporaryLibrary(
        Action<string, string, BMSLibrary> testAction,
        IInstallEstimationExecutionObserver? installEstimationExecutionObserver = null)
    {
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_PendingRegroupTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        File.WriteAllBytes(songDbPath, []);
        try
        {
            TestBmsLibrary library = installEstimationExecutionObserver == null
                ? new TestBmsLibrary(songDbPath, null!, null, null!, new RecordingDialogService(), settings: testSettings)
                : new TestBmsLibrary(songDbPath, null!, null, null!, new RecordingDialogService(), installEstimationExecutionObserver, settings: testSettings);
            testAction(tempRootPath, songDbPath, library);
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }

    private void WithAutoApplyAmbiguousInstallDestination(bool enabled, Action action)
    {
        bool original = testSettings.AutoApplyAmbiguousInstallDestination;
        try
        {
            testSettings.AutoApplyAmbiguousInstallDestination = enabled;
            action();
        }
        finally
        {
            testSettings.AutoApplyAmbiguousInstallDestination = original;
        }
    }

    private sealed class RecordingInstallEstimationExecutionObserver : IInstallEstimationExecutionObserver, IDisposable
    {
        private readonly object gate = new();
        private readonly Barrier? workItemBarrier;
        private readonly Action<InstallEstimationAppliedObservation>? attemptObserver;
        private int activeWorkItems;
        private int maxActive;

        internal RecordingInstallEstimationExecutionObserver(
            int expectedMaxActive = 0,
            Action<InstallEstimationAppliedObservation>? attemptObserver = null)
        {
            this.attemptObserver = attemptObserver;
            if (expectedMaxActive > 1)
            {
                workItemBarrier = new Barrier(expectedMaxActive);
            }
        }

        internal List<InstallEstimationWorkItemObservation> WorkItems { get; } = [];

        internal List<InstallEstimationProgressObservation> Progress { get; } = [];

        internal List<InstallEstimationAppliedObservation> Attempts { get; } = [];

        internal int MaxActive => Volatile.Read(ref maxActive);

        public IDisposable BeginWorkItem(InstallEstimationWorkItemObservation observation)
        {
            lock (gate)
            {
                WorkItems.Add(observation);
            }
            int currentActive = Interlocked.Increment(ref activeWorkItems);
            UpdateMaxActive(currentActive);
            workItemBarrier?.SignalAndWait();
            return new WorkItemScope(this);
        }

        public void ObserveProgress(InstallEstimationProgressObservation observation)
        {
            lock (gate)
            {
                Progress.Add(observation);
            }
        }

        public void ObserveResultApplied(InstallEstimationAppliedObservation observation)
        {
            lock (gate)
            {
                Attempts.Add(observation);
            }
            attemptObserver?.Invoke(observation);
        }

        /// <summary>実一括処理の終端後に、同時到達を観測する局所barrierを回収する。</summary>
        public void Dispose()
        {
            workItemBarrier?.Dispose();
        }

        private void UpdateMaxActive(int currentActive)
        {
            while (true)
            {
                int previousMax = Volatile.Read(ref maxActive);
                if (currentActive <= previousMax
                    || Interlocked.CompareExchange(ref maxActive, currentActive, previousMax) == previousMax)
                {
                    return;
                }
            }
        }

        private void EndWorkItem()
        {
            Interlocked.Decrement(ref activeWorkItems);
        }

        private sealed class WorkItemScope(RecordingInstallEstimationExecutionObserver owner) : IDisposable
        {
            private RecordingInstallEstimationExecutionObserver? owner = owner;

            public void Dispose()
            {
                Interlocked.Exchange(ref owner, null)?.EndWorkItem();
            }
        }
    }

    private sealed class ThrowingInstallEstimationExecutionObserver : IInstallEstimationExecutionObserver
    {
        internal readonly InvalidOperationException Failure = new("install-estimation observer failed");

        public IDisposable BeginWorkItem(InstallEstimationWorkItemObservation observation)
        {
            throw Failure;
        }

        public void ObserveProgress(InstallEstimationProgressObservation observation)
        {
        }

        public void ObserveResultApplied(InstallEstimationAppliedObservation observation)
        {
        }
    }

    private sealed class RecordingDialogService : IBmsLibraryDialogService
    {
        public UiDialogDefaultResult Show(string messageBoxText, string caption, UiDialogButton button, UiDialogIcon icon, UiDialogDefaultResult defaultResult = UiDialogDefaultResult.None)
        {
            return UiDialogDefaultResult.OK;
        }
    }

    private sealed class InstallRowRecord
    {
        public string path { get; set; } = string.Empty;
    }
}
