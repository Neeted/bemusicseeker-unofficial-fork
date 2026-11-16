using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using static BeMusicSeeker.TestSupport.Components.BmsLibraryInstallEstimationServiceTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
[TestCategory("Suite.DriveRoot")]
public sealed class BmsLibraryInstallEstimationServiceTests
{
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
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string tempRoot = workspace.RootDirectory;
        Settings testSettings = TestSettingsFactory.OpenSettings(workspace.GetPath("user.config"));
        string originalDirectory = Directory.GetCurrentDirectory();
        OwnedTestDrive? drive = null;
        Exception? primaryFailure = null;
        try
        {
            drive = OwnedTestDrive.Create(tempRoot, 'Z');
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
            if (cleanupFailures.Count == 0)
            {
                try { drive?.AssertOwned(); drive?.Dispose(); }
                catch (Exception failure) { cleanupFailures.Add(failure); }
            }
            if (cleanupFailures.Count != 0) { workspace.PreserveArtifacts(); }
            if (cleanupFailures.Count != 0)
            {
                if (primaryFailure != null) { cleanupFailures.Insert(0, primaryFailure); }
                throw new AggregateException("未回収のdrive fixtureを保持します: " + tempRoot + "; " + drive, cleanupFailures);
            }
        }
    }

}
