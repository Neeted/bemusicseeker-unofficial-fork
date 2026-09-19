using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models.Update;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class UpdaterProcessGatewayTests
{
    [TestMethod]
    public void WindowsGatewayMapsTypedRequestToUpdaterProcessStartInfo()
    {
        ProcessStartInfo? captured = null;
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
        var request = UpdaterProcessLaunchRequest.Create(
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
            ProcessStartInfo capturedStartInfo = captured!;
            Assert.AreEqual(request.ExecutablePath, capturedStartInfo.FileName);
            Assert.AreEqual(request.WorkingDirectory, capturedStartInfo.WorkingDirectory);
            Assert.IsFalse(capturedStartInfo.UseShellExecute);
            Assert.IsTrue(capturedStartInfo.CreateNoWindow);
            int processId = Process.GetCurrentProcess().Id;
            Assert.AreEqual(
                "\"--app-dir\" \"" + applicationDirectory + "\" "
                    + "\"--package\" \"" + packagePath + "\" "
                    + "\"--backup-dir\" \"" + backupDirectory + "\" "
                    + "\"--ready-file\" \"" + readyFilePath + "\" "
                    + "\"--decision-file\" \"" + decisionFilePath + "\" "
                    + "\"--pid\" \"" + processId + "\" "
                    + "\"--restart-exe\" \"" + restartExecutablePath + "\"",
                capturedStartInfo.Arguments);
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
        var request = UpdaterProcessLaunchRequest.Create(
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
        Process? updaterProcess = null;
        var gateway = new WindowsUpdaterProcessGateway(_ =>
        {
            updaterProcess = Process.Start(new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                Arguments = "/c timeout /t 30 /nobreak > nul",
                UseShellExecute = false,
                CreateNoWindow = true
            })!;
            File.WriteAllText(readyFilePath, "1");
            return updaterProcess;
        });
        var request = UpdaterProcessLaunchRequest.Create(
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

            UpdaterLaunchFailureException exception =
                Assert.ThrowsException<UpdaterLaunchFailureException>(receipt.Abort);

            StringAssert.Contains(exception.Message, "cancel handshake");
            Assert.IsTrue(updaterProcess!.WaitForExit(5000), "Abort must stop the updater even when decision publication fails.");
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
        Process? updaterProcess = null;
        var gateway = new WindowsUpdaterProcessGateway(_ =>
        {
            updaterProcess = Process.Start(new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                Arguments = "/c timeout /t 30 /nobreak > nul",
                UseShellExecute = false,
                CreateNoWindow = true
            })!;
            File.WriteAllText(readyFilePath, "1");
            return updaterProcess;
        });
        var request = UpdaterProcessLaunchRequest.Create(
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

            AggregateException exception = Assert.ThrowsException<AggregateException>(
                () => receipt.Proceed());

            Assert.AreEqual(2, exception.InnerExceptions.Count);
            Assert.IsTrue(exception.InnerExceptions.Any(failure => failure.Message.Contains("proceed handshake", StringComparison.Ordinal)));
            Assert.IsTrue(exception.InnerExceptions.Any(failure => failure.Message.Contains("cancel handshake", StringComparison.Ordinal)));
            Assert.IsTrue(updaterProcess!.WaitForExit(5000), "Proceed failure must stop the updater after aborting.");
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

        Assert.ThrowsException<IOException>(receipt.Abort);
        receipt.Abort();

        Assert.AreEqual(2, abortAttempts);
    }

    [TestMethod]
    public void UpdaterLaunchReceipt_DoesNotHoldDecisionGuardAcrossProceedCallback()
    {
        using var callbackEntered = new ManualResetEventSlim();
        using var releaseCallback = new ManualResetEventSlim();
        using var competingAbortStarted = new ManualResetEventSlim();
        var receipt = new UpdaterLaunchReceipt(
            abort: () => { },
            proceed: () =>
            {
                callbackEntered.Set();
                releaseCallback.Wait();
            });
        Task proceedTask = Task.Factory.StartNew(
            receipt.Proceed,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        Task? competingAbort = null;
        bool callbackEnteredWhileProceeding = callbackEntered.Wait(TimeSpan.FromSeconds(5));
        bool competingAbortStartedWhileCallbackBlocked = false;
        bool competingAbortCompletedWhileCallbackBlocked = false;
        bool competingAbortEventuallyCompleted;
        bool proceedCompleted;

        if (callbackEnteredWhileProceeding)
        {
            competingAbort = Task.Factory.StartNew(
                () =>
                {
                    competingAbortStarted.Set();
                    receipt.Abort();
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            competingAbortStartedWhileCallbackBlocked = competingAbortStarted.Wait(TimeSpan.FromSeconds(5));
            if (competingAbortStartedWhileCallbackBlocked)
            {
                competingAbortCompletedWhileCallbackBlocked = competingAbort.Wait(TimeSpan.FromSeconds(1));
            }
        }

        releaseCallback.Set();
        competingAbortEventuallyCompleted = competingAbort?.Wait(TimeSpan.FromSeconds(5)) ?? true;
        proceedCompleted = proceedTask.Wait(TimeSpan.FromSeconds(5));

        Assert.IsTrue(callbackEnteredWhileProceeding, "The proceed callback did not start.");
        Assert.IsTrue(competingAbortStartedWhileCallbackBlocked, "The competing decision thread did not start.");
        Assert.IsTrue(
            competingAbortCompletedWhileCallbackBlocked,
            "A competing decision must not wait for an external callback under the receipt guard.");
        Assert.IsTrue(competingAbortEventuallyCompleted, "The competing decision thread did not stop.");
        Assert.IsTrue(proceedCompleted, "The proceed callback thread did not stop.");
    }

    [TestMethod]
    public void WindowsGatewayRecoveryTerminatesHungProcessAndReportsVisibleFailure()
    {
        Process? recoveryProcess = null;
        int recoveryProcessId = 0;
        var gateway = new WindowsUpdaterProcessGateway(
            _ =>
            {
                recoveryProcess = Process.Start(new ProcessStartInfo
                {
                    FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                    Arguments = "/c timeout /t 30 /nobreak > nul",
                    UseShellExecute = false,
                    CreateNoWindow = true
                })!;
                recoveryProcessId = recoveryProcess!.Id;
                return recoveryProcess;
            },
            recoveryTimeout: TimeSpan.FromMilliseconds(50));

        try
        {
            UpdaterLaunchFailureException exception =
                Assert.ThrowsException<UpdaterLaunchFailureException>(() =>
                    gateway.RecoverIncompleteTransaction(
                        @"C:\Be Music Seeker\BeMusicSeeker.Updater.exe",
                        @"C:\Be Music Seeker"));

            StringAssert.Contains(exception.Message, "did not finish");
            Assert.ThrowsException<ArgumentException>(
                () => Process.GetProcessById(recoveryProcessId),
                "A hung recovery helper must be terminated before startup recovery fails.");
        }
        finally
        {
            try
            {
                using var survivingProcess = Process.GetProcessById(recoveryProcessId);
                survivingProcess.Kill(entireProcessTree: true);
                survivingProcess.WaitForExit(5000);
            }
            catch (ArgumentException)
            {
            }
            recoveryProcess?.Dispose();
        }
    }

}
