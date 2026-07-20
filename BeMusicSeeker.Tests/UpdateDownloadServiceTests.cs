using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class UpdateDownloadServiceTests
{
    [TestMethod]
    public void UpdateDownloadService_PreparesLaunchWithoutStartingTheUpdater()
    {
        string serviceSource = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker",
            "Models",
            "Update",
            "UpdateDownloadService.cs");
        string ownerSource = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker",
            "ViewModels",
            "StartupUpdateWorkflowOwner.cs");

        StringAssert.Contains(serviceSource, "PreparedUpdaterLaunch PrepareUpdaterLaunch(string packagePath)");
        Assert.IsFalse(serviceSource.Contains("Process.Start("), "UpdateDownloadService must only prepare the updater launch.");
        Assert.IsFalse(serviceSource.Contains("StartUpdater("), "The old process-starting service seam must be removed.");
        Assert.IsFalse(serviceSource.Contains("CreateUpdaterStartInfo("), "The old ProcessStartInfo service seam must be removed.");
        StringAssert.Contains(ownerSource, "IPreparedUpdaterLaunch preparedLaunch = prepareUpdaterLaunch(packagePath)");
        StringAssert.Contains(ownerSource, "preparedLaunch.Start()");
        Assert.IsFalse(ownerSource.Contains("ProcessStartInfo"), "StartupUpdateWorkflowOwner must not own updater process configuration.");
    }

    [TestMethod]
    public void PreparedUpdaterLaunch_IsAnInternalProcessBoundary()
    {
        string launchSource = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker",
            "Models",
            "Update",
            "PreparedUpdaterLaunch.cs");

        StringAssert.Contains(launchSource, "internal interface IPreparedUpdaterLaunch");
        StringAssert.Contains(launchSource, "internal sealed class PreparedUpdaterLaunch : IPreparedUpdaterLaunch");
        StringAssert.Contains(launchSource, "public Process Start()");
        Assert.IsFalse(launchSource.Contains("public sealed class PreparedUpdaterLaunch"));
        Assert.IsTrue(File.Exists(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Models", "Update", "PreparedUpdaterLaunch.cs")));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "BeMusicSeeker.sln")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
