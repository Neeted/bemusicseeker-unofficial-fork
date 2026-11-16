using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using BeMusicSeeker.Models.Update;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Wpf;

namespace BeMusicSeeker.WindowsProcessProbe;

/// <summary>親Job内でstdinを保持した実更新fixtureと製品Gatewayを接続します。</summary>
internal sealed class UpdaterProcessGatewayProbe
{
    /// <summary>実producerの部分出力を保持している間は完成名を見せず、通常Gatewayを併走させて完成後の受付・内容・排他openでclose済みを確認する。</summary>
    /// <remarks>Gatewayの開始通知は読取りループ内の存在確認の観測には使わない。完成前の実FS境界と通常readerの接続を分担し、最後にreceipt取消と実fixture・両pipe・全Taskを回収する。</remarks>
    internal Task WindowsGatewayAcceptsReadyOnlyAfterMarkerPublicationCompletes()
        => RunHeldReadyPublicationAsync(keepUnpublishedUntilDeadline: false);

    /// <summary>実producerを未公開のまま保持すると、Gatewayの受付期限で受付が失敗し、stdin待ち実fixtureを停止する。最後にproducerも解放して全資源を回収する。</summary>
    /// <remarks>
    /// 期限切れの経路を確かめるため、受付の期限だけを1秒へ注入する。製品の構築が既定の10秒を使うことはCoreの <c>UpdaterProcessGatewayTests</c> が確認する。
    /// process停止後のwriter終端は試験所有のgate解放で別に回収する。受付期限の厳密な時刻精度は保証しない。
    /// </remarks>
    internal Task WindowsGatewayStopsUpdaterWhenReadyPublicationRemainsIncomplete()
        => RunHeldReadyPublicationAsync(keepUnpublishedUntilDeadline: true);

    /// <summary>受付の期限切れを待つ時間です。未公開を保持したまま期限へ到達する経路だけに使います。</summary>
    private static readonly TimeSpan ShortReadyTimeout = TimeSpan.FromSeconds(1);

    /// <summary>cancelを読まないfixtureの正常終了を待つ時間と、強制停止後の終了確認の期限です。負荷下の強制停止の確認に余裕を残します。</summary>
    private static readonly TimeSpan AbortGraceForUnresponsiveFixture = TimeSpan.FromSeconds(2);

    private static async Task RunHeldReadyPublicationAsync(bool keepUnpublishedUntilDeadline)
    {
        var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string root = workspace.RootDirectory;
        string executablePath = Path.Combine(root, "restart.exe");
        string readyFilePath = Path.Combine(root, "updater-ready.txt");
        string decisionPath = Path.Combine(root, "updater-decision.txt");
        Process? ownedProcess = null;
        Process? gatewayProcess = null;
        Task? exitTask = null;
        Task<string?>? fixtureReadyTask = null;
        Task<string>? outputTask = null;
        Task<string>? errorTask = null;
        Task? writerTask = null;
        Task<UpdaterLaunchReceipt>? launchTask = null;
        UpdaterLaunchReceipt? receipt = null;
        bool processStarted = false;
        bool launchFailureObserved = false;
        Exception? primaryFailure = null;
        using var releaseWriter = new ManualResetEventSlim();
        var writerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processHandedOver = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
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
            processStarted = ownedProcess.Start();
            Assert.IsTrue(processStarted, "The held updater fixture did not start.");
            exitTask = ownedProcess.WaitForExitAsync();
            errorTask = ownedProcess.StandardError.ReadToEndAsync();
            fixtureReadyTask = ownedProcess.StandardOutput.ReadLineAsync();
            await TestUiDispatcherHost.AwaitNotificationAsync(fixtureReadyTask, exitTask, "updater-fixture.ready");
            Assert.AreEqual("BMS_TEST_FIXTURE_READY", await fixtureReadyTask);
            outputTask = ownedProcess.StandardOutput.ReadToEndAsync();
            int processId = ownedProcess.Id;
            long creationIdentity = ownedProcess.StartTime.ToUniversalTime().Ticks;

            writerTask = Task.Factory.StartNew(() =>
                UpdateMarkerPublisher.Publish(readyFilePath, overwrite: false, stream =>
                {
                    stream.WriteByte((byte)'1');
                    writerEntered.TrySetResult();
                    releaseWriter.Wait();
                }), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            await TestUiDispatcherHost.AwaitNotificationAsync(writerEntered.Task, writerTask, "updater-marker.partial-write");
            Assert.IsFalse(File.Exists(readyFilePath), "The final name must remain absent while the producer is held.");
            Assert.AreEqual(1, Directory.GetFiles(root, "updater-ready.txt.*.tmp").Length);
            Assert.IsFalse(writerTask.IsCompleted);
            var gateway = new WindowsUpdaterProcessGateway(_ =>
            {
                gatewayProcess = Process.GetProcessById(processId);
                Assert.AreEqual(creationIdentity, gatewayProcess.StartTime.ToUniversalTime().Ticks);
                Assert.IsFalse(ownedProcess.HasExited, "Gateway must receive the live stdin-held fixture.");
                processHandedOver.TrySetResult();
                return gatewayProcess;
            },
            // 受付の失敗では期限切れの経路だけを短くし、強制停止後の終了確認は製品既定の期限で待つ。
            // 受付の成功では、cancelを読まないfixtureの正常終了待ちを短くし、強制停止へ進む経路を確かめる。
            terminationTimeout: keepUnpublishedUntilDeadline ? null : AbortGraceForUnresponsiveFixture,
            readyTimeout: keepUnpublishedUntilDeadline ? ShortReadyTimeout : null);
            var request = UpdaterProcessLaunchRequest.Create(
                executablePath, root, root, Path.Combine(root, "app.zip"), Path.Combine(root, "update_backup"),
                readyFilePath, decisionPath, Path.Combine(root, "BeMusicSeeker.exe"), []);
            launchTask = Task.Factory.StartNew(
                () => gateway.Prepare(request).Start(), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            await TestUiDispatcherHost.AwaitNotificationAsync(processHandedOver.Task, launchTask, "updater-gateway.process-handoff");
            Assert.IsFalse(File.Exists(readyFilePath));
            Assert.IsFalse(writerTask.IsCompleted);

            if (keepUnpublishedUntilDeadline)
            {
                UpdaterLaunchFailureException failure = await Assert.ThrowsExactlyAsync<UpdaterLaunchFailureException>(async () =>
                {
                    receipt = await launchTask;
                });
                launchFailureObserved = true;
                StringAssert.Contains(failure.Message, "within 1 seconds");
                Assert.IsNull(receipt);
                Assert.IsFalse(File.Exists(readyFilePath));
                Assert.IsFalse(writerTask.IsCompleted, "Stopping the process must not be mistaken for ending the test-owned writer.");
                await Task.WhenAll(exitTask, outputTask, errorTask);
                Assert.IsTrue(ownedProcess.HasExited);
            }
            else
            {
                releaseWriter.Set();
                receipt = await launchTask;
                await writerTask;
                CollectionAssert.AreEqual(new byte[] { (byte)'1' }, File.ReadAllBytes(readyFilePath));
                using FileStream exclusiveReader = new(readyFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                Assert.AreEqual(1L, exclusiveReader.Length, "Publication must close the writer before the normal reader can accept it.");
                receipt.Abort();
                await Task.WhenAll(exitTask, outputTask, errorTask);
                Assert.IsTrue(ownedProcess.HasExited, "Gateway Abort must terminate the unresponsive updater before fixture cleanup.");
                Assert.AreEqual("cancel", File.ReadAllText(decisionPath).Trim());
            }
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }
        finally
        {
            releaseWriter.Set();
            if (writerTask is not null)
            {
                try
                {
                    await writerTask;
                }
                catch (Exception cleanupFailure)
                {
                    RecordCleanupFailure("WriterSecondaryDiagnostics", cleanupFailure);
                }
            }
            if (launchTask is not null)
            {
                try
                {
                    receipt ??= await launchTask;
                }
                catch (Exception cleanupFailure)
                {
                    // 既に主処理で観測済みの受付失敗は二次故障へ重複登録しません。
                    if (!launchFailureObserved && !ReferenceEquals(primaryFailure, cleanupFailure))
                    {
                        RecordCleanupFailure("LaunchSecondaryDiagnostics", cleanupFailure);
                    }
                }
            }
            try
            {
                receipt?.Abort();
            }
            catch (Exception cleanupFailure)
            {
                RecordCleanupFailure("AbortSecondaryDiagnostics", cleanupFailure);
            }
            try
            {
                if (processStarted && ownedProcess is not null && !ownedProcess.HasExited)
                {
                    ownedProcess.Kill(entireProcessTree: true);
                }
            }
            catch (Exception cleanupFailure)
            {
                RecordCleanupFailure("ProcessSecondaryDiagnostics", cleanupFailure);
            }
            try
            {
                await Task.WhenAll(new Task?[] { exitTask, fixtureReadyTask, outputTask, errorTask }.OfType<Task>());
            }
            catch (Exception cleanupFailure)
            {
                RecordCleanupFailure("PipeSecondaryDiagnostics", cleanupFailure);
            }
            gatewayProcess?.Dispose();
            ownedProcess?.Dispose();
            try
            {
                if (Directory.Exists(root))
                {
                    workspace.Dispose();
                }
            }
            catch (Exception cleanupFailure)
            {
                RecordCleanupFailure("DirectorySecondaryDiagnostics: " + root, cleanupFailure);
            }
        }
        if (primaryFailure is not null)
        {
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        }

        void RecordCleanupFailure(string phase, Exception cleanupFailure)
        {
            if (primaryFailure is null)
            {
                primaryFailure = cleanupFailure;
            }
            else
            {
                primaryFailure.Data[phase] = cleanupFailure.ToString();
            }
        }
    }





    /// <summary>
    /// 取消決定ファイルを公開できないと元原因を保持して入力待ちの実fixtureを停止し、既存一時file・完成名のdirectoryを保護して失敗tempを残さない。
    /// </summary>
    /// <remarks>既存GUI fixtureをREADY後もstdinで保持し、未releaseの実rootと両pipeの終端を観測する。gateway用handleと所有handleを分けて生成identityを確認し、失敗時も所有processを回収する。</remarks>
    internal async Task WindowsGatewayAbortStopsUpdaterWhenDecisionPublicationFails()
    {
        var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string root = workspace.RootDirectory;
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
            string previousTemporaryPath = decisionPath + ".tmp";
            File.WriteAllText(previousTemporaryPath, "other writer");
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
                readyFilePath, decisionPath, Path.Combine(root, "BeMusicSeeker.exe"), []);
            UpdaterLaunchReceipt receipt = gateway.Prepare(request).Start();
            string[] filesBeforePublication = Directory.GetFiles(root);
            UpdaterLaunchFailureException exception =
                Assert.ThrowsExactly<UpdaterLaunchFailureException>(receipt.Abort);
            StringAssert.Contains(exception.Message, "cancel handshake");
            Assert.IsNotNull(exception.InnerException, "The original decision publication failure must remain visible.");
            CollectionAssert.AreEquivalent(filesBeforePublication, Directory.GetFiles(root));
            Assert.AreEqual("other writer", File.ReadAllText(previousTemporaryPath));
            Assert.IsTrue(Directory.Exists(decisionPath));
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
                        workspace.Dispose();
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
    /// 続行と取消の決定公開が両方失敗すると両原因を保持して実fixtureを停止し、既存一時file・完成名のdirectoryを保護して両試行の失敗tempを残さない。
    /// </summary>
    /// <remarks>既存GUI fixtureをREADY後もstdinで保持し、未releaseの実rootと両pipeの終端を観測する。gateway用handleと所有handleを分けて生成identityを確認し、失敗時も所有processを回収する。</remarks>
    internal async Task WindowsGatewayProceedNormalizesDecisionPublicationFailureAndStopsUpdater()
    {
        var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string root = workspace.RootDirectory;
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
            string previousTemporaryPath = decisionPath + ".tmp";
            File.WriteAllText(previousTemporaryPath, "other writer");
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
                readyFilePath, decisionPath, Path.Combine(root, "BeMusicSeeker.exe"), []);
            UpdaterLaunchReceipt receipt = gateway.Prepare(request).Start();
            string[] filesBeforePublication = Directory.GetFiles(root);
            AggregateException exception = Assert.ThrowsExactly<AggregateException>(() => receipt.Proceed());
            Assert.AreEqual(2, exception.InnerExceptions.Count);
            Assert.IsTrue(exception.InnerExceptions.Any(failure => failure.Message.Contains("proceed handshake", StringComparison.Ordinal)));
            Assert.IsTrue(exception.InnerExceptions.Any(failure => failure.Message.Contains("cancel handshake", StringComparison.Ordinal)));
            foreach (Exception failure in exception.InnerExceptions)
            {
                Assert.IsNotNull(failure.InnerException, "Both original publication failures must remain visible.");
            }
            CollectionAssert.AreEquivalent(filesBeforePublication, Directory.GetFiles(root));
            Assert.AreEqual("other writer", File.ReadAllText(previousTemporaryPath));
            Assert.IsTrue(Directory.Exists(decisionPath));
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
                        workspace.Dispose();
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
    /// 指定復旧期限を超えた入力待ちの実fixtureは停止して可視失敗を返し、復旧故障を正常扱いしたりプロセスを残す誤りを検出する。
    /// </summary>
    /// <remarks>既存GUI fixtureをREADY後もstdinで保持し、未releaseの実rootと両pipeの終端を観測する。gateway用handleと所有handleを分けて生成identityを確認し、失敗時も所有processを回収する。</remarks>
    internal async Task WindowsGatewayRecoveryTerminatesHungProcessAndReportsVisibleFailure()
    {
        var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string root = workspace.RootDirectory;
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
                Assert.ThrowsExactly<UpdaterLaunchFailureException>(() =>
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
                        workspace.Dispose();
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
