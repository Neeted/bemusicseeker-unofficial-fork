using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

/// <summary>
/// installed-only resource overwrite の導入先確認が、既存配置の厳密確認として動作することを検証します。
/// </summary>
[TestClass]
public sealed class InstalledOnlyResourceOverwriteValidationTests
{
    private readonly BeMusicSeeker.Properties.Settings testSettings = MainWindowViewModelTestFactory.CreateIsolatedSettings();

    /// <summary>全譜面のhashが一つの所持folderへ解決する場合だけその宛先を選び、形式やhashの取り落としを検出します。</summary>
    [TestMethod]
    public void TryPrepareInstalledOnlyPackageDestination_AllChartsUnderOneDirectory_Succeeds()
    {
        WithWorkspace(delegate (BmsLibraryInstallEstimationService service, string tempRoot)
        {
            string installedDir = Path.Combine(tempRoot, "Installed", "PackageA");
            List<ChartFile> installedFiles =
            [
                CreateInstalledFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(installedDir, "a.bms")),
                CreateInstalledFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine(installedDir, "b.bme")),
                CreateInstalledFile("cccccccccccccccccccccccccccccccc", Path.Combine(installedDir, "c.pms"))
            ];

            ChartPackage package = CreatePendingPackage(
                Path.Combine(tempRoot, "Pending"),
                CreatePendingFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(tempRoot, "Pending", "a.bms")),
                CreatePendingFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine(tempRoot, "Pending", "b.bme")),
                CreatePendingFile("cccccccccccccccccccccccccccccccc", Path.Combine(tempRoot, "Pending", "c.pms")));

            InstalledChartLookupIndexSnapshot snapshot = CreateInstalledChartLookupIndexSnapshot(installedFiles);
            InstalledOnlyPackageResolutionResult resolution = service.TryPrepareInstalledOnlyPackageDestination(package, snapshot);

            Assert.IsTrue(resolution.Success);
            Assert.AreEqual(installedDir, resolution.DestinationDirectory);
            Assert.AreEqual(InstalledDirectoryResolveReason.None, resolution.Reason);
        });
    }

    /// <summary>異なる所持folderへ分かれるpackageを分割配置として拒否し、先頭譜面だけで宛先を決める誤りを検出します。</summary>
    [TestMethod]
    public void TryPrepareInstalledOnlyPackageDestination_PackageSplitAcrossDirectories_Skips()
    {
        WithWorkspace(delegate (BmsLibraryInstallEstimationService service, string tempRoot)
        {
            string installedDir1 = Path.Combine(tempRoot, "Installed", "Dir1");
            string installedDir2 = Path.Combine(tempRoot, "Installed", "Dir2");
            List<ChartFile> installedFiles =
            [
                CreateInstalledFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(installedDir1, "a.bms")),
                CreateInstalledFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine(installedDir2, "b.bme"))
            ];

            ChartPackage package = CreatePendingPackage(
                Path.Combine(tempRoot, "Pending"),
                CreatePendingFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(tempRoot, "Pending", "a.bms")),
                CreatePendingFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine(tempRoot, "Pending", "b.bme")));

            InstalledChartLookupIndexSnapshot snapshot = CreateInstalledChartLookupIndexSnapshot(installedFiles);
            InstalledOnlyPackageResolutionResult resolution = service.TryPrepareInstalledOnlyPackageDestination(package, snapshot);

            Assert.IsFalse(resolution.Success);
            Assert.IsNull(resolution.DestinationDirectory);
            Assert.AreEqual(InstalledDirectoryResolveReason.PackageHasSplitInstalledDirectories, resolution.Reason);
        });
    }

    /// <summary>一譜面のhashが複数folderにある場合は曖昧として拒否し、任意の一folderを選ぶ誤りを検出します。</summary>
    [TestMethod]
    public void TryPrepareInstalledOnlyPackageDestination_ChartInstalledInMultipleDirectories_Skips()
    {
        WithWorkspace(delegate (BmsLibraryInstallEstimationService service, string tempRoot)
        {
            string installedDir1 = Path.Combine(tempRoot, "Installed", "Dir1");
            string installedDir2 = Path.Combine(tempRoot, "Installed", "Dir2");
            List<ChartFile> installedFiles =
            [
                CreateInstalledFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(installedDir1, "a.bms")),
                CreateInstalledFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(installedDir2, "a.bms"))
            ];

            ChartPackage package = CreatePendingPackage(
                CreatePendingFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(tempRoot, "Pending", "a.bms")));

            InstalledChartLookupIndexSnapshot snapshot = CreateInstalledChartLookupIndexSnapshot(installedFiles);
            InstalledOnlyPackageResolutionResult resolution = service.TryPrepareInstalledOnlyPackageDestination(package, snapshot);

            Assert.IsFalse(resolution.Success);
            Assert.IsNull(resolution.DestinationDirectory);
            Assert.AreEqual(InstalledDirectoryResolveReason.ChartHasMultipleInstalledDirectories, resolution.Reason);
        });
    }

    /// <summary>package内の一譜面でも所持hashに対応するfolderがない場合は拒否し、部分一致だけで許可する誤りを検出します。</summary>
    [TestMethod]
    public void TryPrepareInstalledOnlyPackageDestination_MissingInstalledDirectory_Skips()
    {
        WithWorkspace(delegate (BmsLibraryInstallEstimationService service, string tempRoot)
        {
            string installedDir = Path.Combine(tempRoot, "Installed", "Dir1");
            List<ChartFile> installedFiles =
            [
                CreateInstalledFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(installedDir, "a.bms"))
            ];

            ChartPackage package = CreatePendingPackage(
                Path.Combine(tempRoot, "Pending"),
                CreatePendingFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(tempRoot, "Pending", "a.bms")),
                CreatePendingFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine(tempRoot, "Pending", "b.bme")));

            InstalledChartLookupIndexSnapshot snapshot = CreateInstalledChartLookupIndexSnapshot(installedFiles);
            InstalledOnlyPackageResolutionResult resolution = service.TryPrepareInstalledOnlyPackageDestination(package, snapshot);

            Assert.IsFalse(resolution.Success);
            Assert.IsNull(resolution.DestinationDirectory);
            Assert.AreEqual(InstalledDirectoryResolveReason.MissingInstallDestination, resolution.Reason);
        });
    }

    /// <summary>所持索引にない譜面をpathとprimary hash付きで返し、拒否の原因対象を取り違える誤りを検出します。</summary>
    [TestMethod]
    public void FindChartWithMissingInstalledDirectory_ReturnsChartFile()
    {
        WithWorkspace(delegate (BmsLibraryInstallEstimationService service, string tempRoot)
        {
            string installedDir = Path.Combine(tempRoot, "Installed", "Dir1");
            List<ChartFile> installedFiles =
            [
                CreateInstalledFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(installedDir, "a.bms"))
            ];
            ChartFile missingPendingFile = CreatePendingFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine(tempRoot, "Pending", "b.bme"));
            ChartPackage package = CreatePendingPackage(
                Path.Combine(tempRoot, "Pending"),
                CreatePendingFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(tempRoot, "Pending", "a.bms")),
                missingPendingFile);

            InstalledChartLookupIndexSnapshot snapshot = CreateInstalledChartLookupIndexSnapshot(installedFiles);
            ChartFile chart = BmsLibraryInstallEstimationService.FindChartWithMissingInstalledDirectory(package, snapshot);

            Assert.IsNotNull(chart);
            Assert.AreEqual(missingPendingFile.Path, chart.Path);
            Assert.AreEqual(missingPendingFile.Md5, chart.PrimaryLookupHash);
        });
    }

    /// <summary>複数folderに存在するhashの保留譜面を原因対象として返し、曖昧な対象の診断漏れを検出します。</summary>
    [TestMethod]
    public void FindChartWithMultipleInstalledDirectories_ReturnsChartFile()
    {
        WithWorkspace(delegate (BmsLibraryInstallEstimationService service, string tempRoot)
        {
            string installedDir1 = Path.Combine(tempRoot, "Installed", "Dir1");
            string installedDir2 = Path.Combine(tempRoot, "Installed", "Dir2");
            List<ChartFile> installedFiles =
            [
                CreateInstalledFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(installedDir1, "a.bms")),
                CreateInstalledFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(installedDir2, "a.bms"))
            ];
            ChartFile pendingFile = CreatePendingFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(tempRoot, "Pending", "a.bms"));
            ChartPackage package = CreatePendingPackage(pendingFile);

            InstalledChartLookupIndexSnapshot snapshot = CreateInstalledChartLookupIndexSnapshot(installedFiles);
            ChartFile chart = BmsLibraryInstallEstimationService.FindChartWithMultipleInstalledDirectories(package, snapshot);

            Assert.IsNotNull(chart);
            Assert.AreEqual(pendingFile.Path, chart.Path);
            Assert.AreEqual(pendingFile.Md5, chart.PrimaryLookupHash);
        });
    }

    /// <summary>保留宛先が同一でも実所持索引が分割配置なら拒否し、推定済み宛先で厳密確認を迂回する誤りを検出します。</summary>
    [TestMethod]
    public void TryPrepareInstalledOnlyPackageDestination_PendingInstlDstDoesNotBypassSplitDetection()
    {
        WithWorkspace(delegate (BmsLibraryInstallEstimationService service, string tempRoot)
        {
            string installedDir1 = Path.Combine(tempRoot, "Installed", "Dir1");
            string installedDir2 = Path.Combine(tempRoot, "Installed", "Dir2");
            List<ChartFile> installedFiles =
            [
                CreateInstalledFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(installedDir1, "a.bms")),
                CreateInstalledFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine(installedDir2, "b.bme"))
            ];

            ChartFile pendingA = CreatePendingFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(tempRoot, "Pending", "a.bms"));
            ChartFile pendingB = CreatePendingFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine(tempRoot, "Pending", "b.bme"));
            ChartPackage package = CreatePendingPackage(
                Path.Combine(tempRoot, "Pending"),
                ChartPackageTestExtensions.CreateEntryWithInstallDestination(pendingA, installedDir1),
                ChartPackageTestExtensions.CreateEntryWithInstallDestination(pendingB, installedDir1));

            InstalledChartLookupIndexSnapshot snapshot = CreateInstalledChartLookupIndexSnapshot(installedFiles);
            InstalledOnlyPackageResolutionResult resolution = service.TryPrepareInstalledOnlyPackageDestination(package, snapshot);

            Assert.IsFalse(resolution.Success);
            Assert.IsNull(resolution.DestinationDirectory);
            Assert.AreEqual(InstalledDirectoryResolveReason.PackageHasSplitInstalledDirectories, resolution.Reason);
        });
    }

    /// <summary>MD5が存在して不一致なら一致SHA256へ救済せず拒否し、照合hashの優先規則を変える誤りを検出します。</summary>
    [TestMethod]
    public void TryPrepareInstalledOnlyPackageDestination_Md5PresentDoesNotFallBackToSha256()
    {
        WithWorkspace(delegate (BmsLibraryInstallEstimationService service, string tempRoot)
        {
            string installedDir = Path.Combine(tempRoot, "Installed", "Dir1");
            ChartFile installedFile = CreateInstalledFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(installedDir, "a.bms"));
            installedFile = installedFile with { Sha256 = new string('b', 64) };

            ChartFile pendingFile = CreatePendingFile("cccccccccccccccccccccccccccccccc", Path.Combine(tempRoot, "Pending", "a.bms"));
            pendingFile = pendingFile with { Sha256 = new string('b', 64) };
            ChartPackage package = CreatePendingPackage(pendingFile);

            InstalledChartLookupIndexSnapshot snapshot = CreateInstalledChartLookupIndexSnapshot([installedFile]);
            InstalledOnlyPackageResolutionResult resolution = service.TryPrepareInstalledOnlyPackageDestination(package, snapshot);

            Assert.IsFalse(resolution.Success);
            Assert.IsNull(resolution.DestinationDirectory);
            Assert.AreEqual(InstalledDirectoryResolveReason.MissingInstallDestination, resolution.Reason);
        });
    }

    /// <summary>直下と子の全既所持譜面が同じ根へ戻るときだけ許可し、末尾構成不一致・変換後の別根を見送ります。所持directoryをそのまま比較する誤りを検出します。</summary>
    [TestMethod]
    [DataRow("Song", "sub", true, "None")]
    [DataRow("Song", "other", false, "MissingInstallDestination")]
    [DataRow("OtherSong", "sub", false, "PackageHasSplitInstalledDirectories")]
    public void TryPrepareInstalledOnlyPackageDestination_RequiresCommonConvertedRoot(string childRootName, string childDirectory, bool succeeds, string reason)
    {
        WithWorkspace((service, tempRoot) =>
        {
            string installedRoot = Path.Combine(tempRoot, "Installed", "Song");
            string inputRoot = Path.Combine(tempRoot, "Pending");
            ChartPackage package = CreatePendingPackage(inputRoot,
                CreatePendingFile("root", Path.Combine(inputRoot, "root.bms")),
                CreatePendingFile("child", Path.Combine(inputRoot, "sub", "old.bms")));
            InstalledOnlyPackageResolutionResult result = service.TryPrepareInstalledOnlyPackageDestination(package,
                CreateInstalledChartLookupIndexSnapshot([
                    CreateInstalledFile("root", Path.Combine(installedRoot, "root.bms")),
                    CreateInstalledFile("child", Path.Combine(tempRoot, "Installed", childRootName, childDirectory, "old.bms"))]));

            Assert.AreEqual(succeeds, result.Success);
            Assert.AreEqual(succeeds ? installedRoot : null, result.DestinationDirectory);
            Assert.AreEqual(Enum.Parse<InstalledDirectoryResolveReason>(reason), result.Reason);
        });
    }

    /// <summary>同じhashの二所持先のうち一方だけが入力相対配置と一致しても曖昧として拒否し、根変換で複数所持の拒否を解除する誤りを検出します。</summary>
    [TestMethod]
    public void TryPrepareInstalledOnlyPackageDestination_MultipleLocationsRemainAmbiguousBeforeRootConversion()
    {
        WithWorkspace((service, tempRoot) =>
        {
            string inputRoot = Path.Combine(tempRoot, "Pending");
            ChartPackage package = CreatePendingPackage(inputRoot,
                CreatePendingFile("owned", Path.Combine(inputRoot, "sub", "old.bms")));
            InstalledOnlyPackageResolutionResult result = service.TryPrepareInstalledOnlyPackageDestination(package,
                CreateInstalledChartLookupIndexSnapshot([
                    CreateInstalledFile("owned", Path.Combine(tempRoot, "A", "sub", "old.bms")),
                    CreateInstalledFile("owned", Path.Combine(tempRoot, "B", "other", "old.bms"))]));

            Assert.IsFalse(result.Success);
            Assert.IsNull(result.DestinationDirectory);
            Assert.AreEqual(InstalledDirectoryResolveReason.ChartHasMultipleInstalledDirectories, result.Reason);
        });
    }

    private void WithWorkspace(Action<BmsLibraryInstallEstimationService, string> testAction)
    {

        string tempRoot = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_InstalledDirIndexTests_" + Guid.NewGuid().ToString("N"));
        var service = new BmsLibraryInstallEstimationService(BmsLibraryOptionsSnapshot.CreateCurrent(testSettings));
        testAction(service, tempRoot);
    }

    private static InstalledChartLookupIndexSnapshot CreateInstalledChartLookupIndexSnapshot(IEnumerable<ChartFile> installedFiles)
    {
        var state = new InstalledChartLookupIndexState();
        foreach (ChartFile file in installedFiles ?? [])
        {
            if (file != null)
            {
                state.AddChart(file.Path, file.Md5, file.Sha256);
            }
        }
        return state.CreateSnapshot();
    }

    private static ChartFile CreateInstalledFile(string hash, string path)
    {
        ChartFile file = ChartTestValues.Empty() with
        {
            Path = path
        };
        file = file with { Md5 = hash };
        return file;
    }

    private static ChartFile CreatePendingFile(string hash, string path)
    {
        return CreateInstalledFile(hash, path);
    }

    private static ChartPackage CreatePendingPackage(params ChartFile[] files)
    {
        ChartPackage package = ChartPackageTestExtensions.CreatePackage(files);
        package.path = files.FirstOrDefault()?.Path ?? string.Empty;
        package.delete_parent = false;
        return package;
    }

    private static ChartPackage CreatePendingPackage(params PackageChartEntry[] entries)
    {
        ChartPackage package = ChartPackageTestExtensions.CreatePackage(entries);
        package.path = entries.FirstOrDefault()?.Chart?.Path ?? string.Empty;
        package.delete_parent = false;
        return package;
    }

    private static ChartPackage CreatePendingPackage(string packageRoot, params ChartFile[] files)
    {
        ChartPackage package = CreatePendingPackage(files);
        package.path = packageRoot;
        return package;
    }

    private static ChartPackage CreatePendingPackage(string packageRoot, params PackageChartEntry[] entries)
    {
        ChartPackage package = CreatePendingPackage(entries);
        package.path = packageRoot;
        return package;
    }

}
