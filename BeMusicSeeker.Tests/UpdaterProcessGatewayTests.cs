using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using BeMusicSeeker.Models.Update;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class UpdaterProcessGatewayTests
{
    [TestMethod]
    public void WindowsGatewayMapsTypedRequestToUpdaterProcessStartInfo()
    {
        ProcessStartInfo captured = null!;
        string root = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_UpdaterProcessGatewayTests", Guid.NewGuid().ToString("N"));
        string readyFilePath = Path.Combine(root, "updater-ready.txt");
        string decisionFilePath = Path.Combine(root, "updater-decision.txt");
        Directory.CreateDirectory(root);
        using Process fakeProcess = new();
        var gateway = new WindowsUpdaterProcessGateway(startInfo =>
        {
            captured = startInfo;
            File.WriteAllText(readyFilePath, "1");
            return fakeProcess;
        });
        string applicationDirectory = @"C:\Be Music Seeker";
        string packagePath = @"C:\downloads\app package.zip";
        string backupDirectory = @"C:\Be Music Seeker\update_backup";
        string restartExecutablePath = @"C:\Be Music Seeker\BeMusicSeeker.exe";
        UpdaterProcessLaunchRequest request = UpdaterProcessLaunchRequest.Create(
            @"C:\Be Music Seeker\update_work\BeMusicSeeker.Updater.exe",
            @"C:\Be Music Seeker\update_work",
            applicationDirectory,
            packagePath,
            backupDirectory,
            readyFilePath,
            decisionFilePath,
            restartExecutablePath);

        try
        {
            UpdaterLaunchReceipt receipt = gateway.Prepare(request).Start();

            Assert.IsNotNull(receipt);
            Assert.AreEqual(request.ExecutablePath, captured.FileName);
            Assert.AreEqual(request.WorkingDirectory, captured.WorkingDirectory);
            Assert.IsFalse(captured.UseShellExecute);
            int processId = Process.GetCurrentProcess().Id;
            Assert.AreEqual(
                "\"--app-dir\" \"" + applicationDirectory + "\" "
                    + "\"--package\" \"" + packagePath + "\" "
                    + "\"--backup-dir\" \"" + backupDirectory + "\" "
                    + "\"--ready-file\" \"" + readyFilePath + "\" "
                    + "\"--decision-file\" \"" + decisionFilePath + "\" "
                    + "\"--pid\" \"" + processId + "\" "
                    + "\"--restart-exe\" \"" + restartExecutablePath + "\"",
                captured.Arguments);
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
    public void WindowsGatewayNormalizesProcessStartFailure()
    {
        string root = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_UpdaterProcessGatewayTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string readyFilePath = Path.Combine(root, "updater-ready.txt");
        string decisionFilePath = Path.Combine(root, "updater-decision.txt");
        var gateway = new WindowsUpdaterProcessGateway(_ => throw new InvalidOperationException("start failed"));
        UpdaterProcessLaunchRequest request = UpdaterProcessLaunchRequest.Create(
            @"C:\Be Music Seeker\update_work\BeMusicSeeker.Updater.exe",
            @"C:\Be Music Seeker\update_work",
            @"C:\Be Music Seeker",
            @"C:\downloads\app.zip",
            @"C:\Be Music Seeker\update_backup",
            readyFilePath,
            decisionFilePath,
            @"C:\Be Music Seeker\BeMusicSeeker.exe");

        try
        {
            UpdaterLaunchFailureException exception = Assert.ThrowsException<UpdaterLaunchFailureException>(
                () => gateway.Prepare(request).Start());
            StringAssert.Contains(exception.Message, "ready handshake");
            Assert.IsInstanceOfType(exception.InnerException, typeof(InvalidOperationException));
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
    public void WindowsGatewayAbortStopsUpdaterWhenDecisionPublicationFails()
    {
        string root = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_UpdaterProcessGatewayTests", Guid.NewGuid().ToString("N"));
        string readyFilePath = Path.Combine(root, "updater-ready.txt");
        string decisionPath = Path.Combine(root, "updater-decision.txt");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(decisionPath);
        Process updaterProcess = null!;
        var gateway = new WindowsUpdaterProcessGateway(_ =>
        {
            updaterProcess = Process.Start(new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                Arguments = "/c timeout /t 30 /nobreak > nul",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            File.WriteAllText(readyFilePath, "1");
            return updaterProcess;
        });
        UpdaterProcessLaunchRequest request = UpdaterProcessLaunchRequest.Create(
            @"C:\Be Music Seeker\update_work\BeMusicSeeker.Updater.exe",
            @"C:\Be Music Seeker\update_work",
            @"C:\Be Music Seeker",
            @"C:\downloads\app.zip",
            @"C:\Be Music Seeker\update_backup",
            readyFilePath,
            decisionPath,
            @"C:\Be Music Seeker\BeMusicSeeker.exe");

        try
        {
            UpdaterLaunchReceipt receipt = gateway.Prepare(request).Start();

            receipt.Abort();

            Assert.IsTrue(updaterProcess.WaitForExit(5000), "Abort must stop the updater even when decision publication fails.");
        }
        finally
        {
            if (updaterProcess != null && !updaterProcess.HasExited)
            {
                updaterProcess.Kill(entireProcessTree: true);
                updaterProcess.WaitForExit(5000);
            }
            updaterProcess?.Dispose();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public void WindowsGatewayProceedNormalizesDecisionPublicationFailureAndStopsUpdater()
    {
        string root = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_UpdaterProcessGatewayTests", Guid.NewGuid().ToString("N"));
        string readyFilePath = Path.Combine(root, "updater-ready.txt");
        string decisionPath = Path.Combine(root, "updater-decision.txt");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(decisionPath);
        Process updaterProcess = null!;
        var gateway = new WindowsUpdaterProcessGateway(_ =>
        {
            updaterProcess = Process.Start(new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                Arguments = "/c timeout /t 30 /nobreak > nul",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            File.WriteAllText(readyFilePath, "1");
            return updaterProcess;
        });
        UpdaterProcessLaunchRequest request = UpdaterProcessLaunchRequest.Create(
            @"C:\Be Music Seeker\update_work\BeMusicSeeker.Updater.exe",
            @"C:\Be Music Seeker\update_work",
            @"C:\Be Music Seeker",
            @"C:\downloads\app.zip",
            @"C:\Be Music Seeker\update_backup",
            readyFilePath,
            decisionPath,
            @"C:\Be Music Seeker\BeMusicSeeker.exe");

        try
        {
            UpdaterLaunchReceipt receipt = gateway.Prepare(request).Start();

            UpdaterLaunchFailureException exception = Assert.ThrowsException<UpdaterLaunchFailureException>(
                () => receipt.Proceed());

            StringAssert.Contains(exception.Message, "proceed handshake");
            Assert.IsTrue(updaterProcess.WaitForExit(5000), "Proceed failure must stop the updater after aborting.");
        }
        finally
        {
            if (updaterProcess != null && !updaterProcess.HasExited)
            {
                updaterProcess.Kill(entireProcessTree: true);
                updaterProcess.WaitForExit(5000);
            }
            updaterProcess?.Dispose();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public void UpdaterLaunchReceiptRetriesAbortAfterAnAbortActionFailure()
    {
        int abortAttempts = 0;
        var receipt = new UpdaterLaunchReceipt(() =>
        {
            if (Interlocked.Increment(ref abortAttempts) == 1)
            {
                throw new IOException("cancel publication failed");
            }
        });

        receipt.Abort();
        receipt.Abort();

        Assert.AreEqual(2, abortAttempts);
    }
}
