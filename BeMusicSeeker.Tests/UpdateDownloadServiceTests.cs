using System;
using System.Diagnostics;
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
        string packagePath = Path.Combine(root, "update_work", "downloads", "app.zip");
        File.WriteAllText(applicationPath, string.Empty);
        Directory.CreateDirectory(Path.Combine(root, "update_work", "current"));
        foreach (string fileName in new[]
        {
            "BeMusicSeeker.Updater.exe"
        })
        {
            File.WriteAllText(Path.Combine(root, fileName), "updater");
        }
        foreach (string legacyPayloadFileName in new[]
        {
            "BeMusicSeeker.Updater.dll",
            "BeMusicSeeker.Updater.deps.json",
            "BeMusicSeeker.Updater.runtimeconfig.json"
        })
        {
            File.WriteAllText(Path.Combine(root, "update_work", "current", legacyPayloadFileName), "legacy-updater");
        }
        var gateway = new RecordingUpdaterProcessGateway();
        var service = new UpdateDownloadService(
            ApplicationPathSnapshot.FromExecutablePath(applicationPath),
            gateway);

        try
        {
            IPreparedUpdaterLaunch preparedLaunch = service.PrepareUpdaterLaunch(packagePath);

            Assert.AreSame(gateway.PreparedLaunch, preparedLaunch);
            Assert.IsTrue(File.Exists(gateway.Request.ExecutablePath));
            Assert.IsTrue(File.Exists(Path.Combine(gateway.Request.WorkingDirectory, "BeMusicSeeker.Updater.exe")));
            Assert.AreEqual(
                1,
                Directory.GetFiles(gateway.Request.WorkingDirectory, "BeMusicSeeker.Updater*", SearchOption.TopDirectoryOnly).Length);
            Assert.AreEqual(Path.Combine(root, "update_work", "current"), gateway.Request.WorkingDirectory);
            Assert.AreEqual(root, gateway.Request.ApplicationDirectory);
            Assert.AreEqual(packagePath, gateway.Request.PackagePath);
            Assert.AreEqual(Path.Combine(root, "update_backup"), gateway.Request.BackupDirectory);
            Assert.AreEqual(Path.Combine(root, "update_work", "current", "updater-ready.txt"), gateway.Request.ReadyFilePath);
            Assert.AreEqual(Path.Combine(root, "update_work", "current", "updater-decision.txt"), gateway.Request.DecisionFilePath);
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

    [TestMethod]
    public void CleanupPreviousWorkDirectoryPreservesActiveUpdaterPayload()
    {
        string root = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_UpdateDownloadServiceTests", Guid.NewGuid().ToString("N"));
        string applicationPath = Path.Combine(root, "BeMusicSeeker.exe");
        string workRoot = Path.Combine(root, "update_work");
        string currentDirectory = Path.Combine(workRoot, "current");
        Directory.CreateDirectory(currentDirectory);
        Directory.CreateDirectory(Path.Combine(workRoot, "downloads"));
        File.WriteAllText(applicationPath, string.Empty);
        File.WriteAllText(Path.Combine(currentDirectory, "BeMusicSeeker.Updater.exe"), "running-updater");
        File.WriteAllText(Path.Combine(workRoot, "downloads", "stale.zip"), "stale-package");

        try
        {
            var service = new UpdateDownloadService(
                ApplicationPathSnapshot.FromExecutablePath(applicationPath),
                new RecordingUpdaterProcessGateway());

            service.CleanupPreviousWorkDirectory();

            Assert.IsTrue(File.Exists(Path.Combine(currentDirectory, "BeMusicSeeker.Updater.exe")));
            Assert.IsFalse(Directory.Exists(Path.Combine(workRoot, "downloads")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public void CleanupPreviousWorkDirectoryRecoversIncompleteTransactionThroughUpdaterGateway()
    {
        string root = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_UpdateDownloadServiceTests", Guid.NewGuid().ToString("N"));
        string applicationPath = Path.Combine(root, "BeMusicSeeker.exe");
        string workRoot = Path.Combine(root, "update_work");
        string currentDirectory = Path.Combine(workRoot, "current");
        string journalPath = Path.Combine(workRoot, "update-transaction.json");
        Directory.CreateDirectory(currentDirectory);
        File.WriteAllText(applicationPath, string.Empty);
        File.WriteAllText(Path.Combine(currentDirectory, "BeMusicSeeker.Updater.exe"), "recovery-updater");
        File.WriteAllText(journalPath, "durable-transaction");
        var gateway = new RecordingUpdaterProcessGateway();

        try
        {
            var service = new UpdateDownloadService(
                ApplicationPathSnapshot.FromExecutablePath(applicationPath),
                gateway);

            service.CleanupPreviousWorkDirectory();

            Assert.AreEqual(
                Path.Combine(currentDirectory, "BeMusicSeeker.Updater.exe"),
                gateway.RecoveryExecutablePath);
            Assert.AreEqual(root, gateway.RecoveryApplicationDirectory);
            Assert.IsTrue(File.Exists(Path.Combine(currentDirectory, "BeMusicSeeker.Updater.exe")));
            Assert.IsFalse(File.Exists(journalPath));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public void CleanupPreviousWorkDirectoryPublishesAndConsumesUpdaterFailureReceipt()
    {
        string root = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_UpdateDownloadServiceTests", Guid.NewGuid().ToString("N"));
        string applicationPath = Path.Combine(root, "BeMusicSeeker.exe");
        string failureReceiptPath = Path.Combine(root, "update_work", "update-failure.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(failureReceiptPath)!);
        File.WriteAllText(applicationPath, string.Empty);
        File.WriteAllText(failureReceiptPath, "updater failure details");

        try
        {
            var service = new UpdateDownloadService(
                ApplicationPathSnapshot.FromExecutablePath(applicationPath),
                new RecordingUpdaterProcessGateway());

            UpdateFailureReceiptException exception = Assert.ThrowsException<UpdateFailureReceiptException>(
                () => service.CleanupPreviousWorkDirectory());

            StringAssert.Contains(exception.Message, "updater failure details");
            Assert.IsTrue(File.Exists(failureReceiptPath));
            exception.Acknowledge();
            Assert.IsFalse(File.Exists(failureReceiptPath));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public void CleanupPreviousWorkDirectoryReadsTemporaryUpdaterFailureReceipt()
    {
        string root = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_UpdateDownloadServiceTests", Guid.NewGuid().ToString("N"));
        string applicationPath = Path.Combine(root, "BeMusicSeeker.exe");
        string failureReceiptPath = Path.Combine(root, "update_work", "update-failure.txt.tmp");
        Directory.CreateDirectory(Path.GetDirectoryName(failureReceiptPath)!);
        File.WriteAllText(applicationPath, string.Empty);
        File.WriteAllText(failureReceiptPath, "temporary updater failure details");

        try
        {
            var service = new UpdateDownloadService(
                ApplicationPathSnapshot.FromExecutablePath(applicationPath),
                new RecordingUpdaterProcessGateway());

            UpdateFailureReceiptException exception = Assert.ThrowsException<UpdateFailureReceiptException>(
                () => service.CleanupPreviousWorkDirectory());

            StringAssert.Contains(exception.Message, "temporary updater failure details");
            Assert.IsTrue(File.Exists(failureReceiptPath));
            exception.Acknowledge();
            Assert.IsFalse(File.Exists(failureReceiptPath));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("ReleaseAcceptance")]
    public void PreparedUpdaterPayloadStartsFromCurrentDirectory()
    {
        string updaterPublishOutput = Environment.GetEnvironmentVariable("BMS_SCD_UPDATER_PUBLISH_ROOT");
        if (string.IsNullOrWhiteSpace(updaterPublishOutput))
        {
            Assert.Inconclusive("Self-contained updater verification requires BMS_SCD_UPDATER_PUBLISH_ROOT.");
        }
        string root = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_UpdateDownloadServiceTests", Guid.NewGuid().ToString("N"));
        string applicationPath = Path.Combine(root, "BeMusicSeeker.exe");
        string packagePath = Path.Combine(root, "update_work", "downloads", "app.zip");
        string[] payloadFileNames =
        {
            "BeMusicSeeker.Updater.exe"
        };

        Directory.CreateDirectory(Path.GetDirectoryName(packagePath)!);
        File.WriteAllText(applicationPath, string.Empty);
        File.WriteAllText(packagePath, "package");
        foreach (string fileName in payloadFileNames)
        {
            string sourcePath = Path.Combine(updaterPublishOutput, fileName);
            if (!File.Exists(sourcePath))
            {
                Assert.Inconclusive("Self-contained updater payload is missing: " + sourcePath);
            }
            File.Copy(sourcePath, Path.Combine(root, fileName));
        }

        try
        {
            var gateway = new RecordingUpdaterProcessGateway();
            var service = new UpdateDownloadService(
                ApplicationPathSnapshot.FromExecutablePath(applicationPath),
                gateway);
            service.PrepareUpdaterLaunch(packagePath);

            var startInfo = new ProcessStartInfo(gateway.Request.ExecutablePath)
            {
                UseShellExecute = false,
                WorkingDirectory = gateway.Request.WorkingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("--version");
            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("The prepared updater payload did not start.");
            Assert.IsTrue(process.WaitForExit(30000), "The prepared updater payload did not exit.");
            string standardOutput = process.StandardOutput.ReadToEnd();
            string standardError = process.StandardError.ReadToEnd();
            Assert.AreEqual(0, process.ExitCode, standardError);
            StringAssert.Contains(standardOutput, "1");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public void PrepareUpdaterLaunchRejectsReparsePointInCurrentWorkDirectory()
    {
        string root = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_UpdateDownloadServiceTests", Guid.NewGuid().ToString("N"));
        string externalDirectory = Path.Combine(root, "external");
        Directory.CreateDirectory(externalDirectory);
        File.WriteAllText(Path.Combine(externalDirectory, "sentinel.txt"), "external-sentinel");
        File.WriteAllText(Path.Combine(root, "BeMusicSeeker.exe"), string.Empty);
        foreach (string fileName in new[]
        {
            "BeMusicSeeker.Updater.exe"
        })
        {
            File.WriteAllText(Path.Combine(root, fileName), "updater");
        }
        Directory.CreateDirectory(Path.Combine(root, "update_work"));
        string currentDirectory = Path.Combine(root, "update_work", "current");
        try
        {
            Directory.CreateSymbolicLink(currentDirectory, externalDirectory);
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException || exception is PlatformNotSupportedException)
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
            Assert.Inconclusive("The test environment does not permit directory symbolic links: " + exception.Message);
            return;
        }

        try
        {
            var service = new UpdateDownloadService(
                ApplicationPathSnapshot.FromExecutablePath(Path.Combine(root, "BeMusicSeeker.exe")),
                new RecordingUpdaterProcessGateway());

            Assert.ThrowsException<InvalidOperationException>(() => service.PrepareUpdaterLaunch(Path.Combine(root, "update_work", "downloads", "app.zip")));
            Assert.AreEqual("external-sentinel", File.ReadAllText(Path.Combine(externalDirectory, "sentinel.txt")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public void PrepareUpdaterLaunchRejectsPackageOutsideDownloadDirectory()
    {
        string root = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_UpdateDownloadServiceTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string applicationPath = Path.Combine(root, "BeMusicSeeker.exe");
        File.WriteAllText(applicationPath, string.Empty);
        foreach (string fileName in new[]
        {
            "BeMusicSeeker.Updater.exe"
        })
        {
            File.WriteAllText(Path.Combine(root, fileName), "updater");
        }

        try
        {
            var service = new UpdateDownloadService(
                ApplicationPathSnapshot.FromExecutablePath(applicationPath),
                new RecordingUpdaterProcessGateway());

            Assert.ThrowsException<InvalidOperationException>(() => service.PrepareUpdaterLaunch(Path.Combine(root, "outside.zip")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public void TryDeleteDownloadedPackageRejectsReparsePointDownloadsDirectory()
    {
        string root = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_UpdateDownloadServiceTests", Guid.NewGuid().ToString("N"));
        string externalDirectory = Path.Combine(root, "external");
        Directory.CreateDirectory(externalDirectory);
        string externalPackagePath = Path.Combine(externalDirectory, "app.zip");
        File.WriteAllText(externalPackagePath, "external-package");
        File.WriteAllText(Path.Combine(root, "BeMusicSeeker.exe"), string.Empty);
        Directory.CreateDirectory(Path.Combine(root, "update_work"));
        string downloadsDirectory = Path.Combine(root, "update_work", "downloads");
        try
        {
            Directory.CreateSymbolicLink(downloadsDirectory, externalDirectory);
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException || exception is PlatformNotSupportedException)
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
            Assert.Inconclusive("The test environment does not permit directory symbolic links: " + exception.Message);
            return;
        }

        try
        {
            var service = new UpdateDownloadService(
                ApplicationPathSnapshot.FromExecutablePath(Path.Combine(root, "BeMusicSeeker.exe")),
                new RecordingUpdaterProcessGateway());
            Exception warning = null;

            service.TryDeleteDownloadedPackage(Path.Combine(downloadsDirectory, "app.zip"), exception => warning = exception);

            Assert.IsNotNull(warning);
            Assert.AreEqual("external-package", File.ReadAllText(externalPackagePath));
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

        internal string RecoveryExecutablePath { get; private set; }

        internal string RecoveryApplicationDirectory { get; private set; }

        internal IPreparedUpdaterLaunch PreparedLaunch { get; } = new FakePreparedUpdaterLaunch();

        public IPreparedUpdaterLaunch Prepare(UpdaterProcessLaunchRequest request)
        {
            Request = request;
            return PreparedLaunch;
        }

        public bool RecoverIncompleteTransaction(string executablePath, string applicationDirectory)
        {
            RecoveryExecutablePath = executablePath;
            RecoveryApplicationDirectory = applicationDirectory;
            return true;
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
