using System;
using System.IO;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.Update;
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

        StringAssert.Contains(serviceSource, "IPreparedUpdaterLaunch PrepareUpdaterLaunch(string packagePath)");
        Assert.IsFalse(serviceSource.Contains("Process.Start("), "UpdateDownloadService must only prepare the updater launch.");
        Assert.IsFalse(serviceSource.Contains("ProcessStartInfo"), "UpdateDownloadService must not own updater process configuration.");
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
        StringAssert.Contains(launchSource, "public UpdaterLaunchReceipt Start()");
        Assert.IsFalse(launchSource.Contains("public sealed class PreparedUpdaterLaunch"));
        Assert.IsTrue(File.Exists(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Models", "Update", "PreparedUpdaterLaunch.cs")));
    }

    [TestMethod]
    public void PrepareUpdaterLaunchCopiesUpdaterAndPublishesTypedRequest()
    {
        string root = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_UpdateDownloadServiceTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string applicationPath = Path.Combine(root, "BeMusicSeeker.exe");
        string sourceUpdaterPath = Path.Combine(root, "BeMusicSeeker.Updater.exe");
        string packagePath = Path.Combine(root, "downloads", "app.zip");
        File.WriteAllText(applicationPath, string.Empty);
        File.WriteAllText(sourceUpdaterPath, "updater");
        var gateway = new RecordingUpdaterProcessGateway();
        var service = new UpdateDownloadService(
            ApplicationPathSnapshot.FromExecutablePath(applicationPath),
            gateway);

        try
        {
            IPreparedUpdaterLaunch preparedLaunch = service.PrepareUpdaterLaunch(packagePath);

            Assert.AreSame(gateway.PreparedLaunch, preparedLaunch);
            Assert.IsTrue(File.Exists(gateway.Request.ExecutablePath));
            Assert.AreEqual(Path.Combine(root, "update_work", "current"), gateway.Request.WorkingDirectory);
            Assert.AreEqual(root, gateway.Request.ApplicationDirectory);
            Assert.AreEqual(packagePath, gateway.Request.PackagePath);
            Assert.AreEqual(Path.Combine(root, "update_backup"), gateway.Request.BackupDirectory);
            Assert.AreEqual(applicationPath, gateway.Request.RestartExecutablePath);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed class RecordingUpdaterProcessGateway : IUpdaterProcessGateway
    {
        internal UpdaterProcessLaunchRequest Request { get; private set; } = null!;

        internal IPreparedUpdaterLaunch PreparedLaunch { get; } = new FakePreparedUpdaterLaunch();

        public IPreparedUpdaterLaunch Prepare(UpdaterProcessLaunchRequest request)
        {
            Request = request;
            return PreparedLaunch;
        }
    }

    private sealed class FakePreparedUpdaterLaunch : IPreparedUpdaterLaunch
    {
        public UpdaterLaunchReceipt Start() => new();
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
