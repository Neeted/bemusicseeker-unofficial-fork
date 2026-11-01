using System;
using System.Collections.Generic;
using System.IO;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static BeMusicSeeker.TestSupport.Components.BmsLibraryInstallEstimationServiceTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BmsLibraryInstallEstimationServiceTests
{
    // 値の変更だけを試すfixtureなので、他のtesthostの実設定ファイルは読み込まない。
    private readonly BeMusicSeeker.Properties.Settings testSettings =
        TestSettingsFactory.OpenSettings(Path.Combine(
            Path.GetTempPath(),
            "InstallEstimationSettings-" + Guid.NewGuid().ToString("N"),
            "user.config"));

    /// <summary>所持rootのdirectoryと直下fileは同じ絶対rootへ解決し、非所持rootは拒否します。drive相対先の別directoryへの化けも区別します。</summary>
    /// <remarks>実在検査と既知集合を実validatorへ渡し、期待値は確保した文字と「:\」から作ります。process current directoryの変更から復元までをこのケースだけが所有します。</remarks>
    [TestMethod]
    [DataRow(false, true)]
    [DataRow(true, true)]
    [DataRow(false, false)]
    [DoNotParallelize]
    [TestCategory("EnvironmentIntegration")]
    public void ValidateInstallDestination_PreservesOwnedDriveRootAndRejectsUnknownRoot(bool fileInput, bool knownRoot)
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_ManualRoot_" + Guid.NewGuid().ToString("N"));
        string originalDirectory = Directory.GetCurrentDirectory();
        OwnedTestDrive? drive = null;
        Exception? primaryFailure = null;
        try
        {
            drive = OwnedTestDrive.Create(tempRoot);
            drive.AssertOwned();
            string expectedRoot = drive.Root[0] + @":\";
            string notRoot = Directory.CreateDirectory(Path.Combine(expectedRoot, "NotRoot")).FullName;
            string rootFile = Path.Combine(expectedRoot, "owned.bms");
            File.WriteAllText(rootFile, "#TITLE Owned");
            Directory.SetCurrentDirectory(notRoot);
            Assert.AreEqual(notRoot, Path.GetFullPath(drive.Root[..2]));
            Assert.AreNotEqual(expectedRoot, Path.GetFullPath(drive.Root[..2]));
            ChartFile pendingFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(tempRoot, "pending", "chart.bms"));
            ChartPackage pendingPackage = ChartPackageTestExtensions.CreatePackage([pendingFile]);
            string[] knownDirectories = knownRoot ? [expectedRoot, notRoot] : [notRoot];

            PendingInstallDestinationSelectionResult result = CreateService(testSettings).ValidateInstallDestination(
                PackageChartEntry.FromChart(pendingFile), [pendingPackage], knownDirectories,
                fileInput ? rootFile : expectedRoot);

            Assert.AreEqual(knownRoot, result.Success);
            if (knownRoot) { Assert.AreEqual(expectedRoot, result.ValidatedDestinationDirectory); }
            else { Assert.IsTrue(string.IsNullOrEmpty(result.ValidatedDestinationDirectory)); }
            Assert.AreEqual(1, result.TargetEntries.Count);
            Assert.AreEqual(pendingFile.Path, result.TargetEntries[0].Chart.Path);
        }
        catch (Exception failure) { primaryFailure = failure; throw; }
        finally
        {
            var cleanupFailures = new List<Exception>();
            try { Directory.SetCurrentDirectory(originalDirectory); }
            catch (Exception failure) { cleanupFailures.Add(failure); }
            try { drive?.Dispose(); }
            catch (Exception failure) { cleanupFailures.Add(failure); }
            try { DeleteDirectoryIfExists(tempRoot); }
            catch (Exception failure) { cleanupFailures.Add(failure); }
            if (cleanupFailures.Count != 0)
            {
                if (primaryFailure != null) { cleanupFailures.Insert(0, primaryFailure); }
                throw new AggregateException(cleanupFailures);
            }
        }
    }

}
