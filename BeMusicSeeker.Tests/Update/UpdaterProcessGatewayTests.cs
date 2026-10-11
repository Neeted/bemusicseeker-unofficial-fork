using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models.Update;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class UpdaterProcessGatewayTests
{
    /// <summary>
    /// 空白付きの要求パスと現在PIDを引用してツール起動へ渡し、作業ディレクトリと非表示起動を保って外部引数の分割・欠落を検出する。
    /// </summary>
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

    /// <summary>
    /// プロセス起動の元失敗を受付handshake失敗へ包んで伝え、正常receiptへ読み替えたり元原因を失う誤りを検出する。
    /// </summary>
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

    /// <summary>
    /// 取消決定ファイルを公開できないと失敗を通知して入力待ちの実fixtureの終端を確認し、取消失敗後に稼働を残す誤りを検出する。
    /// </summary>
    /// <remarks>既存GUI fixtureをREADY後もstdinで保持し、未releaseの実rootと両pipeの終端を観測する。gateway用handleと所有handleを分けて生成identityを確認し、失敗時も所有processを回収する。</remarks>
    [TestMethod]
    public async Task WindowsGatewayAbortStopsUpdaterWhenDecisionPublicationFails()
    {
        string root = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_UpdaterProcessGatewayTests", Guid.NewGuid().ToString("N"));
        string executablePath = Path.Combine(root, "restart.exe");
        Process? ownedProcess = null;
        Process? gatewayProcess = null;
        Task? exitTask = null;
        Task<string?>? readyTask = null;
        Task<string>? outputTask = null;
        Task<string>? errorTask = null;
        Exception? primaryFailure = null;
        try
        {
            Directory.CreateDirectory(root);
            File.Copy(
                Path.Combine(AppContext.BaseDirectory, "updater-restart-application", "UpdaterRestartApplication.exe"),
                executablePath);
            File.WriteAllText(Path.ChangeExtension(executablePath, ".json"), JsonSerializer.Serialize(new
            {
                WaitForRelease = true,
                MarkerFile = (string?)null,
                MarkerContents = (string?)null
            }));
            ownedProcess = new Process
            {
                StartInfo = new ProcessStartInfo(executablePath)
                {
                    WorkingDirectory = root,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            Assert.IsTrue(ownedProcess.Start(), "The held updater fixture did not start.");
            exitTask = ownedProcess.WaitForExitAsync();
            errorTask = ownedProcess.StandardError.ReadToEndAsync();
            readyTask = ownedProcess.StandardOutput.ReadLineAsync();
            await TestUiDispatcherHost.AwaitNotificationAsync(readyTask, exitTask, "updater-fixture.ready");
            Assert.AreEqual("BMS_TEST_FIXTURE_READY", await readyTask);
            outputTask = ownedProcess.StandardOutput.ReadToEndAsync();
            Assert.IsFalse(ownedProcess.HasExited, "The fixture must remain alive while its stdin release is withheld.");
            int processId = ownedProcess.Id;
            long creationIdentity = ownedProcess.StartTime.ToUniversalTime().Ticks;
            string readyFilePath = Path.Combine(root, "updater-ready.txt");
            string decisionPath = Path.Combine(root, "updater-decision.txt");
            Directory.CreateDirectory(decisionPath);
            var gateway = new WindowsUpdaterProcessGateway(_ =>
            {
                gatewayProcess = Process.GetProcessById(processId);
                Assert.AreEqual(creationIdentity, gatewayProcess.StartTime.ToUniversalTime().Ticks);
                Assert.IsFalse(ownedProcess.HasExited, "The held fixture must still be alive at gateway handoff.");
                File.WriteAllText(readyFilePath, "1");
                return gatewayProcess;
            });
            var request = UpdaterProcessLaunchRequest.Create(
                executablePath, root, root, Path.Combine(root, "app.zip"), Path.Combine(root, "update_backup"),
                readyFilePath, decisionPath, Path.Combine(root, "BeMusicSeeker.exe"));
            UpdaterLaunchReceipt receipt = gateway.Prepare(request).Start();
            UpdaterLaunchFailureException exception =
                Assert.ThrowsException<UpdaterLaunchFailureException>(receipt.Abort);
            StringAssert.Contains(exception.Message, "cancel handshake");
            Assert.IsNotNull(exception.InnerException, "The original decision publication failure must remain visible.");
            // releaseは送信しない。gatewayが停止させた実rootと両pipeのEOFを同じ5秒以内で観測する。
            await Task.WhenAll(exitTask, outputTask, errorTask).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }
        finally
        {
            try
            {
                if (ownedProcess is not null)
                {
                    if (!ownedProcess.HasExited)
                    {
                        ownedProcess.Kill(entireProcessTree: true);
                    }
                    Task[] ownedTasks = new Task?[] { exitTask, readyTask, outputTask, errorTask }
                        .OfType<Task>().ToArray();
                    await Task.WhenAll(ownedTasks).WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
            catch (Exception cleanupFailure)
            {
                if (primaryFailure is null)
                {
                    primaryFailure = cleanupFailure;
                }
                else
                {
                    primaryFailure.Data["ProcessSecondaryDiagnostics"] = cleanupFailure.ToString();
                }
            }
            finally
            {
                gatewayProcess?.Dispose();
                ownedProcess?.Dispose();
                try
                {
                    if (Directory.Exists(root))
                    {
                        Directory.Delete(root, recursive: true);
                    }
                }
                catch (Exception cleanupFailure)
                {
                    if (primaryFailure is null)
                    {
                        primaryFailure = cleanupFailure;
                    }
                    else
                    {
                        primaryFailure.Data["DirectorySecondaryDiagnostics"] = cleanupFailure.ToString();
                    }
                }
            }
        }
        if (primaryFailure is not null)
        {
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        }
    }

    /// <summary>
    /// 続行と取消の決定公開が両方失敗すると両原因を保持して入力待ちの実fixtureの終端を確認し、失敗を一つに潰す誤りを検出する。
    /// </summary>
    /// <remarks>既存GUI fixtureをREADY後もstdinで保持し、未releaseの実rootと両pipeの終端を観測する。gateway用handleと所有handleを分けて生成identityを確認し、失敗時も所有processを回収する。</remarks>
    [TestMethod]
    public async Task WindowsGatewayProceedNormalizesDecisionPublicationFailureAndStopsUpdater()
    {
        string root = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_UpdaterProcessGatewayTests", Guid.NewGuid().ToString("N"));
        string executablePath = Path.Combine(root, "restart.exe");
        Process? ownedProcess = null;
        Process? gatewayProcess = null;
        Task? exitTask = null;
        Task<string?>? readyTask = null;
        Task<string>? outputTask = null;
        Task<string>? errorTask = null;
        Exception? primaryFailure = null;
        try
        {
            Directory.CreateDirectory(root);
            File.Copy(
                Path.Combine(AppContext.BaseDirectory, "updater-restart-application", "UpdaterRestartApplication.exe"),
                executablePath);
            File.WriteAllText(Path.ChangeExtension(executablePath, ".json"), JsonSerializer.Serialize(new
            {
                WaitForRelease = true,
                MarkerFile = (string?)null,
                MarkerContents = (string?)null
            }));
            ownedProcess = new Process
            {
                StartInfo = new ProcessStartInfo(executablePath)
                {
                    WorkingDirectory = root,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            Assert.IsTrue(ownedProcess.Start(), "The held updater fixture did not start.");
            exitTask = ownedProcess.WaitForExitAsync();
            errorTask = ownedProcess.StandardError.ReadToEndAsync();
            readyTask = ownedProcess.StandardOutput.ReadLineAsync();
            await TestUiDispatcherHost.AwaitNotificationAsync(readyTask, exitTask, "updater-fixture.ready");
            Assert.AreEqual("BMS_TEST_FIXTURE_READY", await readyTask);
            outputTask = ownedProcess.StandardOutput.ReadToEndAsync();
            Assert.IsFalse(ownedProcess.HasExited, "The fixture must remain alive while its stdin release is withheld.");
            int processId = ownedProcess.Id;
            long creationIdentity = ownedProcess.StartTime.ToUniversalTime().Ticks;
            string readyFilePath = Path.Combine(root, "updater-ready.txt");
            string decisionPath = Path.Combine(root, "updater-decision.txt");
            Directory.CreateDirectory(decisionPath);
            var gateway = new WindowsUpdaterProcessGateway(_ =>
            {
                gatewayProcess = Process.GetProcessById(processId);
                Assert.AreEqual(creationIdentity, gatewayProcess.StartTime.ToUniversalTime().Ticks);
                Assert.IsFalse(ownedProcess.HasExited, "The held fixture must still be alive at gateway handoff.");
                File.WriteAllText(readyFilePath, "1");
                return gatewayProcess;
            });
            var request = UpdaterProcessLaunchRequest.Create(
                executablePath, root, root, Path.Combine(root, "app.zip"), Path.Combine(root, "update_backup"),
                readyFilePath, decisionPath, Path.Combine(root, "BeMusicSeeker.exe"));
            UpdaterLaunchReceipt receipt = gateway.Prepare(request).Start();
            AggregateException exception = Assert.ThrowsException<AggregateException>(() => receipt.Proceed());
            Assert.AreEqual(2, exception.InnerExceptions.Count);
            Assert.IsTrue(exception.InnerExceptions.Any(failure => failure.Message.Contains("proceed handshake", StringComparison.Ordinal)));
            Assert.IsTrue(exception.InnerExceptions.Any(failure => failure.Message.Contains("cancel handshake", StringComparison.Ordinal)));
            foreach (Exception failure in exception.InnerExceptions)
            {
                Assert.IsNotNull(failure.InnerException, "Both original publication failures must remain visible.");
            }
            // release未送信の入力待ちprocessが止まり、両pipeも閉じた実終端を観測する。
            await Task.WhenAll(exitTask, outputTask, errorTask).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }
        finally
        {
            try
            {
                if (ownedProcess is not null)
                {
                    if (!ownedProcess.HasExited)
                    {
                        ownedProcess.Kill(entireProcessTree: true);
                    }
                    Task[] ownedTasks = new Task?[] { exitTask, readyTask, outputTask, errorTask }
                        .OfType<Task>().ToArray();
                    await Task.WhenAll(ownedTasks).WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
            catch (Exception cleanupFailure)
            {
                if (primaryFailure is null)
                {
                    primaryFailure = cleanupFailure;
                }
                else
                {
                    primaryFailure.Data["ProcessSecondaryDiagnostics"] = cleanupFailure.ToString();
                }
            }
            finally
            {
                gatewayProcess?.Dispose();
                ownedProcess?.Dispose();
                try
                {
                    if (Directory.Exists(root))
                    {
                        Directory.Delete(root, recursive: true);
                    }
                }
                catch (Exception cleanupFailure)
                {
                    if (primaryFailure is null)
                    {
                        primaryFailure = cleanupFailure;
                    }
                    else
                    {
                        primaryFailure.Data["DirectorySecondaryDiagnostics"] = cleanupFailure.ToString();
                    }
                }
            }
        }
        if (primaryFailure is not null)
        {
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        }
    }

    /// <summary>
    /// 取消callbackの最初の失敗後も再試行を受理し、二度目の取消まで受付状態を消費してしまう誤りを検出する。
    /// </summary>
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

    /// <summary>
    /// 続行callbackが保持される間に別workerの取消が完了でき、外部callback中も決定guardを握って競合を止める誤りを検出する。
    /// </summary>
    [TestMethod]
    public async Task UpdaterLaunchReceipt_DoesNotHoldDecisionGuardAcrossProceedCallback()
    {
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var abortAttempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseCallback = new ManualResetEventSlim();
        var receipt = new UpdaterLaunchReceipt(
            abort: () => { },
            proceed: () =>
            {
                callbackEntered.TrySetResult();
                releaseCallback.Wait();
            });
        Task proceedTask = Task.Factory.StartNew(
            receipt.Proceed, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Task? competingAbort = null;
        Exception? primaryFailure = null;
        try
        {
            await TestUiDispatcherHost.AwaitNotificationAsync(callbackEntered.Task, proceedTask, "updater-receipt.proceed-callback");
            Assert.IsFalse(proceedTask.IsCompleted, "Proceed must still be held inside its callback.");
            competingAbort = Task.Factory.StartNew(
                () =>
                {
                    abortAttempted.TrySetResult();
                    receipt.Abort();
                },
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            await TestUiDispatcherHost.AwaitNotificationAsync(abortAttempted.Task, competingAbort, "updater-receipt.abort-attempt");
            Assert.IsFalse(proceedTask.IsCompleted, "Proceed must remain held before the competing decision.");
            // callback保持中の排他不成立を観測する既存1秒。通常到達・解放後の完了待ちには使わない。
            var cutoff = Task.Delay(TimeSpan.FromSeconds(1));
            Task first = await Task.WhenAny(competingAbort, proceedTask, cutoff);
            Assert.AreSame(competingAbort, first, "A competing decision must complete while the external callback remains held.");
            await competingAbort;
            Assert.IsFalse(proceedTask.IsCompleted, "A completed abort must not end or fault the held proceed worker.");
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }
        finally
        {
            releaseCallback.Set();
            try
            {
                await Task.WhenAll(competingAbort is null ? new[] { proceedTask } : new[] { proceedTask, competingAbort });
            }
            catch (Exception cleanupFailure)
            {
                if (primaryFailure is null)
                {
                    primaryFailure = cleanupFailure;
                }
                else
                {
                    primaryFailure.Data["WorkerSecondaryDiagnostics"] = cleanupFailure.ToString();
                }
            }
        }
        if (primaryFailure is not null)
        {
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        }
    }

    /// <summary>
    /// 指定復旧期限を超えた入力待ちの実fixtureは停止して可視失敗を返し、復旧故障を正常扱いしたりプロセスを残す誤りを検出する。
    /// </summary>
    /// <remarks>既存GUI fixtureをREADY後もstdinで保持し、未releaseの実rootと両pipeの終端を観測する。gateway用handleと所有handleを分けて生成identityを確認し、失敗時も所有processを回収する。</remarks>
    [TestMethod]
    public async Task WindowsGatewayRecoveryTerminatesHungProcessAndReportsVisibleFailure()
    {
        string root = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_UpdaterProcessGatewayTests", Guid.NewGuid().ToString("N"));
        string executablePath = Path.Combine(root, "restart.exe");
        Process? ownedProcess = null;
        Process? gatewayProcess = null;
        Task? exitTask = null;
        Task<string?>? readyTask = null;
        Task<string>? outputTask = null;
        Task<string>? errorTask = null;
        Exception? primaryFailure = null;
        try
        {
            Directory.CreateDirectory(root);
            File.Copy(
                Path.Combine(AppContext.BaseDirectory, "updater-restart-application", "UpdaterRestartApplication.exe"),
                executablePath);
            File.WriteAllText(Path.ChangeExtension(executablePath, ".json"), JsonSerializer.Serialize(new
            {
                WaitForRelease = true,
                MarkerFile = (string?)null,
                MarkerContents = (string?)null
            }));
            ownedProcess = new Process
            {
                StartInfo = new ProcessStartInfo(executablePath)
                {
                    WorkingDirectory = root,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            Assert.IsTrue(ownedProcess.Start(), "The held updater fixture did not start.");
            exitTask = ownedProcess.WaitForExitAsync();
            errorTask = ownedProcess.StandardError.ReadToEndAsync();
            readyTask = ownedProcess.StandardOutput.ReadLineAsync();
            await TestUiDispatcherHost.AwaitNotificationAsync(readyTask, exitTask, "updater-fixture.ready");
            Assert.AreEqual("BMS_TEST_FIXTURE_READY", await readyTask);
            outputTask = ownedProcess.StandardOutput.ReadToEndAsync();
            Assert.IsFalse(ownedProcess.HasExited, "The fixture must remain alive while its stdin release is withheld.");
            int processId = ownedProcess.Id;
            long creationIdentity = ownedProcess.StartTime.ToUniversalTime().Ticks;
            var gateway = new WindowsUpdaterProcessGateway(
                _ =>
                {
                    gatewayProcess = Process.GetProcessById(processId);
                    Assert.AreEqual(creationIdentity, gatewayProcess.StartTime.ToUniversalTime().Ticks);
                    Assert.IsFalse(ownedProcess.HasExited, "Recovery must receive the live held fixture.");
                    return gatewayProcess;
                },
                recoveryTimeout: TimeSpan.FromMilliseconds(50));
            UpdaterLaunchFailureException exception =
                Assert.ThrowsException<UpdaterLaunchFailureException>(() =>
                    gateway.RecoverIncompleteTransaction(executablePath, root));
            StringAssert.Contains(exception.Message, "did not finish");
            // PIDの再取得失敗ではなく、所有した実processのExitと両EOFで期限超過後の停止を確認する。
            await Task.WhenAll(exitTask, outputTask, errorTask).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }
        finally
        {
            try
            {
                if (ownedProcess is not null)
                {
                    if (!ownedProcess.HasExited)
                    {
                        ownedProcess.Kill(entireProcessTree: true);
                    }
                    Task[] ownedTasks = new Task?[] { exitTask, readyTask, outputTask, errorTask }
                        .OfType<Task>().ToArray();
                    await Task.WhenAll(ownedTasks).WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
            catch (Exception cleanupFailure)
            {
                if (primaryFailure is null)
                {
                    primaryFailure = cleanupFailure;
                }
                else
                {
                    primaryFailure.Data["ProcessSecondaryDiagnostics"] = cleanupFailure.ToString();
                }
            }
            finally
            {
                gatewayProcess?.Dispose();
                ownedProcess?.Dispose();
                try
                {
                    if (Directory.Exists(root))
                    {
                        Directory.Delete(root, recursive: true);
                    }
                }
                catch (Exception cleanupFailure)
                {
                    if (primaryFailure is null)
                    {
                        primaryFailure = cleanupFailure;
                    }
                    else
                    {
                        primaryFailure.Data["DirectorySecondaryDiagnostics"] = cleanupFailure.ToString();
                    }
                }
            }
        }
        if (primaryFailure is not null)
        {
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        }
    }

}
