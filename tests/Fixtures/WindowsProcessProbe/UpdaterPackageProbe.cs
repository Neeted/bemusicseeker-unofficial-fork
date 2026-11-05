using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using BeMusicSeeker.Models.Update;
using BeMusicSeeker.TestSupport.Components.Update;
using BeMusicSeeker.TestSupport.Processes;
using BeMusicSeeker.TestSupport.Verification;
using static BeMusicSeeker.TestSupport.Components.Update.UpdaterPackageTestSupport;

namespace BeMusicSeeker.WindowsProcessProbe;

/// <summary>実Processを返す既存callbackとstdin生存fixtureだけを、親Job内の固定payloadで実行します。</summary>
internal sealed class UpdaterPackageProbe
{
    private readonly UpdaterPackageProbeRequest request;
    private readonly ProcessDeadline deadline;
    private readonly Lazy<ProcessDeadline> cleanupDeadline = new(() => ProcessDeadline.FromTimeout(TimeSpan.FromSeconds(10)));
    private readonly List<string> cleanupDiagnostics = [];
    private readonly List<(Process Process, Task<string> Output, Task<string> Error)> directProcesses = [];
    private readonly UpdaterPackageCommands Commands;

    private UpdaterPackageProbe(UpdaterPackageProbeRequest request)
    {
        this.request = request;
        deadline = ProcessDeadline.FromSuiteEnvironment(TimeSpan.FromTicks(request.RemainingTicks));
        Commands = new UpdaterPackageCommands(request.UpdaterPath, deadline, cleanupDeadline);
    }

    /// <summary>固定caseとbool入力を実行し、全直接processとpipeの終端後にだけ結果を返します。</summary>
    internal static Task<int> RunAsync(string caseName, string requestPath)
    {
        try
        {
            UpdaterPackageProbeRequest request = JsonSerializer.Deserialize<UpdaterPackageProbeRequest>(File.ReadAllText(requestPath))
                ?? throw new InvalidDataException("更新probe要求が空です。");
            var probe = new UpdaterPackageProbe(request);
            probe.Run(caseName);
            Program.WriteResult(new { Case = caseName, Passed = true, Restarting = request.Restarting });
            return Task.FromResult(0);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            foreach (System.Collections.DictionaryEntry diagnostic in exception.Data) { Console.Error.WriteLine($"{diagnostic.Key}: {diagnostic.Value}"); }
            return Task.FromResult(2);
        }
    }

    private void Run(string caseName)
    {
        Exception? primary = null;
        try
        {
            switch (caseName)
            {
                case nameof(WindowsGatewayAcceptsDriveRootReadyReceiptAndCancelsWithoutApplying):
                    WindowsGatewayAcceptsDriveRootReadyReceiptAndCancelsWithoutApplying();
                    break;
                case nameof(WindowsGatewayCancelsAndPreparesNextUpdate):
                    WindowsGatewayCancelsAndPreparesNextUpdate();
                    break;
                case nameof(RecoverCommandDefersWhenTheApplicationExecutableIsStillRunning):
                    Assert.IsFalse(request.Restarting);
                    RecoverCommandDefersWhenTheApplicationExecutableIsStillRunning();
                    break;
                case nameof(ApplyUpdate_PostRestartDiagnosticFailureKeepsCommittedApplication):
                    ApplyUpdate_PostRestartDiagnosticFailureKeepsCommittedApplication(request.Restarting);
                    break;
                default: throw new ArgumentException("未知の更新probe caseです: " + caseName);
            }
            DistributionValidation.CheckDeadline(deadline, CancellationToken.None);
        }
        catch (Exception exception) { primary = exception; throw; }
        finally
        {
            // 強制停止そのものから回収時間へ含め、内側finallyで確定済みなら同じ期限を引き継ぎます。
            ProcessDeadline cleanup = primary is null ? deadline : cleanupDeadline.Value;
            foreach ((Process Process, Task<string> Output, Task<string> Error) owned in directProcesses.AsEnumerable().Reverse())
            {
                try
                {
                    if (!owned.Process.HasExited) { owned.Process.Kill(entireProcessTree: true); }
                    Task.WhenAll(owned.Process.WaitForExitAsync(), owned.Output, owned.Error).WaitAsync(cleanup.Remaining).GetAwaiter().GetResult();
                    owned.Process.Dispose();
                }
                catch (Exception exception) { cleanupDiagnostics.Add(exception.ToString()); }
            }
            foreach (OwnedProcess process in Commands.Processes.AsEnumerable().Reverse())
            {
                try
                {
                    ProcessStopResult stopped = process.StopAsync(null, cleanup).GetAwaiter().GetResult();
                    if (stopped.CleanupFailure is not null) { cleanupDiagnostics.Add(stopped.CleanupFailure.ToString()); }
                    process.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
                catch (Exception exception) { cleanupDiagnostics.Add(exception.ToString()); }
            }
            // HKCUとtempは親Jobの全終端後に親caseが回収する。
            if (cleanupDiagnostics.Count > 0)
            {
                string message = string.Join(Environment.NewLine, cleanupDiagnostics);
                if (primary is not null) { primary.Data["UpdaterCleanupSecondaryDiagnostics"] = message; }
                else { Assert.Fail(message); }
            }
        }
    }

    /// <summary>親が所有するdrive rootで実gatewayのready receipt・Abort・旧tree保持を確認します。</summary>
    private void WindowsGatewayAcceptsDriveRootReadyReceiptAndCancelsWithoutApplying()
    {
        string appDirectoryPath = request.RootDirectory;
        string packagePath = Path.Combine(appDirectoryPath, "update_work", "downloads", "package.zip");
        string readyPath = Path.Combine(appDirectoryPath, "update_work", "current", "updater-ready.txt");
        string decisionPath = Path.Combine(appDirectoryPath, "update_work", "current", "updater-decision.txt");
        string workingDirectory = Directory.CreateDirectory(Path.Combine(appDirectoryPath, "update_work", "current")).FullName;
        string canonicalBefore = ComputeCanonicalTreeHash(appDirectoryPath);
        Process? process = null;
        var gateway = new WindowsUpdaterProcessGateway(startInfo =>
        {
            Assert.IsTrue(startInfo.CreateNoWindow);
            Assert.AreEqual(workingDirectory, startInfo.WorkingDirectory);
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            process = StartDirectProcess(startInfo);
            return process;
        });
        var launchRequest = UpdaterProcessLaunchRequest.Create(
            request.UpdaterPath, workingDirectory, appDirectoryPath, packagePath,
            Path.Combine(appDirectoryPath, "update_backup"), readyPath, decisionPath,
            Path.Combine(appDirectoryPath, "restart.exe"), []);
        UpdaterLaunchReceipt receipt = gateway.Prepare(launchRequest).Start();
        Assert.IsNotNull(process);
        Assert.AreEqual("1", File.ReadAllText(readyPath).Trim());
        Assert.IsFalse(process.HasExited);
        Assert.AreEqual(canonicalBefore, ComputeCanonicalTreeHash(appDirectoryPath));
        receipt.Abort();
        WaitForDirectExit(process, deadline);
        // 全子孫終端後の最終treeとZIP・再起動markerは、親Jobを回収した親caseで確認します。
        Assert.IsTrue(process.HasExited);
    }


    private void WindowsGatewayCancelsAndPreparesNextUpdate()
    {
        Directory.CreateDirectory(Path.Combine(request.RootDirectory, "app", "update_work", "downloads"));
        (string app, string package, string backup) = PrepareCleanupFailurePackage(request.RootDirectory);
        WriteTextFile(app, "config/user.config", "user-settings");
        WriteTextFile(backup, "previous/retained.txt", "previous-generation");
        string current = Directory.CreateDirectory(Path.Combine(app, "update_work", "current")).FullName;
        string updater = Path.Combine(current, "BeMusicSeeker.Updater.exe");
        string ready = Path.Combine(current, "updater-ready.txt");
        string decision = Path.Combine(current, "updater-decision.txt");
        string canonical = ComputeCanonicalTreeHash(app);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            // 再配置が成功すること自体も、前回のwatchdogが同じ実行体を保持していない独立した確認です。
            File.Copy(request.UpdaterPath, updater, overwrite: true);
            File.Delete(ready);
            File.Delete(decision);
            Process? process = null;
            var gateway = new WindowsUpdaterProcessGateway(info =>
            {
                info.RedirectStandardOutput = true;
                info.RedirectStandardError = true;
                process = StartDirectProcess(info);
                return process;
            });
            UpdaterLaunchReceipt receipt = gateway.Prepare(UpdaterProcessLaunchRequest.Create(updater, current,
                app, package, backup, ready, decision, Path.Combine(app, "restart.exe"), ["--simple-restart-argument"])).Start();
            Assert.IsNotNull(process);
            Assert.IsTrue(File.Exists(Path.Combine(app, "update_work", "update-transaction.json")));
            receipt.Abort();
            WaitForDirectExit(process, deadline);
            Assert.AreEqual(0, process.ExitCode);
            Assert.IsFalse(File.Exists(Path.Combine(app, "update_work", "update-transaction.json")));
            Assert.AreEqual(0, GetRecoveryRunOnceValues(app).Count);
            Assert.AreEqual(0, GetRecoverySupervisorValues(app).Count);
            Assert.AreEqual(canonical, ComputeCanonicalTreeHash(app));
            Assert.IsTrue(File.Exists(package));
            Assert.AreEqual("user-settings", File.ReadAllText(Path.Combine(app, "config", "user.config")));
            Assert.AreEqual("previous-generation", File.ReadAllText(Path.Combine(backup, "previous", "retained.txt")));
            Assert.IsFalse(File.Exists(Path.Combine(app, "old.marker")));
            using FileStream releasedImage = new(updater, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.IsTrue(releasedImage.Length > 0, "所有watchdogを含む実行体の解放はfixtureのKillより前に確認します。");
        }
    }

    private Process StartDirectProcess(ProcessStartInfo info, bool waitForReady = false)
    {
        DistributionValidation.CheckDeadline(deadline, CancellationToken.None);
        Process process = Process.Start(info) ?? throw new InvalidOperationException("再起動fixtureを開始できません。");
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<string> output = ReadOutputAsync(process, waitForReady, ready);
        Task<string> error = process.StandardError.ReadToEndAsync();
        directProcesses.Add((process, output, error));
        if (waitForReady) { ready.Task.WaitAsync(deadline.Remaining).GetAwaiter().GetResult(); }
        return process;
    }

    private static async Task<string> ReadOutputAsync(Process process, bool waitForReady, TaskCompletionSource ready)
    {
        try
        {
            string prefix = string.Empty;
            if (waitForReady)
            {
                string? line = await process.StandardOutput.ReadLineAsync();
                Assert.AreEqual("BMS_TEST_FIXTURE_READY", line?.Trim());
                prefix = line + Environment.NewLine;
                ready.SetResult();
            }
            return prefix + await process.StandardOutput.ReadToEndAsync();
        }
        catch (Exception exception) { ready.TrySetException(exception); throw; }
    }

    private Process StartLiveApplicationFixture(string appDirectoryPath, string restartExecutablePath)
        => StartDirectProcess(new ProcessStartInfo(restartExecutablePath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = appDirectoryPath
        }, waitForReady: true);

    private void WaitForDirectExit(Process process, ProcessDeadline waitDeadline)
    {
        (Process Process, Task<string> Output, Task<string> Error) owned = directProcesses.Single(entry => ReferenceEquals(entry.Process, process));
        Task.WhenAll(process.WaitForExitAsync(), owned.Output, owned.Error).WaitAsync(waitDeadline.Remaining).GetAwaiter().GetResult();
    }

    private void StopDirectProcess(Process process)
    {
        ProcessDeadline cleanup = cleanupDeadline.Value;
        if (!process.HasExited) { process.Kill(entireProcessTree: true); }
        WaitForDirectExit(process, cleanup);
    }
    /// <summary>
    /// 同じ実行ファイルが実際に生存すると更新・復旧を延期して旧世代と新しい復旧登録を保ち、稼働中のアプリを上書きする誤りを検出する。
    /// </summary>
    /// <remarks>独立Jobと固有パスを所有し、共有registryはこのアプリ領域の登録だけを回収する。</remarks>
    private void RecoverCommandDefersWhenTheApplicationExecutableIsStillRunning()
    {
        {
            string tempDirectoryPath = request.RootDirectory;
            string appDirectoryPath = Path.Combine(tempDirectoryPath, "app");
            string packagePath = Path.Combine(appDirectoryPath, "update_work", "downloads", "package.zip");
            string backupDirectoryPath = Path.Combine(appDirectoryPath, "update_backup");
            string previousDirectoryPath = Path.Combine(backupDirectoryPath, "previous");
            string extractDirectoryPath = Path.Combine(appDirectoryPath, "update_work", "extracted");
            string journalPath = Path.Combine(appDirectoryPath, "update_work", "update-transaction.json");
            string restartExecutablePath = Path.Combine(appDirectoryPath, "restart.exe");

            WriteTextFile(appDirectoryPath, "BeMusicSeeker.exe", "partial-new");
            CopyRestartExecutable(appDirectoryPath, waitForRelease: true);
            WriteTextFile(previousDirectoryPath, "BeMusicSeeker.exe", "old-app");
            WriteTextFile(
                appDirectoryPath,
                "update_work/update-transaction.json",
                JsonSerializer.Serialize(new
                {
                    Version = 1,
                    Phase = request.Phase,
                    Preflight = request.Preflight,
                    AppDirectory = appDirectoryPath,
                    PackagePath = packagePath,
                    ApplicationProcessId = Environment.ProcessId,
                    BackupDirectory = backupDirectoryPath,
                    PreviousDirectory = previousDirectoryPath,
                    ExtractDirectory = extractDirectoryPath,
                    RestartExecutablePath = restartExecutablePath,
                    BackupComplete = request.Phase == "applying",
                    NewPackagePaths = new[] { "BeMusicSeeker.exe" }
                }));

            Process liveApplication = StartLiveApplicationFixture(
                appDirectoryPath,
                restartExecutablePath);
            try
            {
                if (request.Phase == "prepared")
                {
                    string source = Path.Combine(tempDirectoryPath, "package-source");
                    WriteTextFile(source, "BeMusicSeeker.exe", "next-app");
                    Directory.CreateDirectory(Path.GetDirectoryName(packagePath) ?? throw new InvalidOperationException());
                    System.IO.Compression.ZipFile.CreateFromDirectory(source, packagePath);
                    string journal = File.ReadAllText(journalPath);
                    string canonical = ComputeCanonicalTreeHash(appDirectoryPath);
                    Commands.RunUpdaterRecovery(appDirectoryPath);
                    Assert.IsFalse(File.Exists(journalPath));
                    Assert.AreEqual(0, GetRecoveryRunOnceValues(appDirectoryPath).Count);
                    Assert.AreEqual(0, GetRecoverySupervisorValues(appDirectoryPath).Count);
                    Assert.IsFalse(liveApplication.HasExited);
                    Assert.AreEqual(canonical, ComputeCanonicalTreeHash(appDirectoryPath));
                    Assert.AreEqual("old-app", File.ReadAllText(Path.Combine(previousDirectoryPath, "BeMusicSeeker.exe")));
                    File.WriteAllText(journalPath, journal);
                    OwnedProcess prepared = Commands.StartUpdater(appDirectoryPath, packagePath, backupDirectoryPath,
                        restartExecutablePath, liveApplication.Id.ToString(), publishProceed: false);
                    Assert.IsFalse(prepared.RootExit.IsCompleted, "通常入口は古いpreparedを回収して次のreadyへ進みます。");
                    PublishUpdaterDecision(Path.Combine(appDirectoryPath, "update_work", "current", "updater-decision.txt"), "cancel");
                    Commands.WaitForExit(prepared);
                    Assert.AreEqual(0, prepared.RootExit.GetAwaiter().GetResult());
                    Assert.IsFalse(File.Exists(journalPath));
                    Assert.AreEqual(0, GetRecoveryRunOnceValues(appDirectoryPath).Count);
                    Assert.AreEqual(0, GetRecoverySupervisorValues(appDirectoryPath).Count);
                    Assert.AreEqual(canonical, ComputeCanonicalTreeHash(appDirectoryPath));
                    Assert.AreEqual("old-app", File.ReadAllText(Path.Combine(previousDirectoryPath, "BeMusicSeeker.exe")));
                    Assert.IsFalse(liveApplication.HasExited);
                    return;
                }
                OwnedProcess normalUpdater = Commands.StartUpdater(
                    appDirectoryPath,
                    packagePath,
                    backupDirectoryPath,
                    restartExecutablePath,
                    Environment.ProcessId.ToString());
                Commands.WaitForExit(normalUpdater);
                Assert.AreEqual(2, normalUpdater.RootExit.GetAwaiter().GetResult());
                Assert.AreEqual("partial-new", File.ReadAllText(Path.Combine(appDirectoryPath, "BeMusicSeeker.exe")));
                Assert.AreEqual(
                    1,
                    Directory.EnumerateFiles(previousDirectoryPath, "*", SearchOption.AllDirectories).Count(),
                    "The deferred update must retain only the recorded previous-generation file.");
                Assert.AreEqual("old-app", File.ReadAllText(Path.Combine(previousDirectoryPath, "BeMusicSeeker.exe")));
                Assert.AreEqual(1, GetRecoveryRunOnceValues(appDirectoryPath).Count);
                Assert.AreEqual(1, GetRecoverySupervisorValues(appDirectoryPath).Count);
                Assert.IsFalse(liveApplication.HasExited, "The application fixture must remain alive after normal updater deferral.");
                IReadOnlyList<string> runOnceBeforeRecovery = GetRecoveryRunOnceValues(appDirectoryPath);

                OwnedProcess recovery = Commands.StartUpdaterRecoveryProcess(appDirectoryPath);
                Commands.WaitForExit(recovery);
                Assert.AreEqual(2, recovery.RootExit.GetAwaiter().GetResult());
                Assert.AreEqual("partial-new", File.ReadAllText(Path.Combine(appDirectoryPath, "BeMusicSeeker.exe")));
                Assert.IsTrue(File.Exists(journalPath));
                Assert.AreEqual(
                    1,
                    Directory.EnumerateFiles(previousDirectoryPath, "*", SearchOption.AllDirectories).Count(),
                    "Recovery deferral must retain only the recorded previous-generation file.");
                Assert.AreEqual("old-app", File.ReadAllText(Path.Combine(previousDirectoryPath, "BeMusicSeeker.exe")));
                Assert.AreEqual(1, GetRecoveryRunOnceValues(appDirectoryPath).Count);
                Assert.AreEqual(1, GetRecoverySupervisorValues(appDirectoryPath).Count);
                Assert.IsFalse(
                    runOnceBeforeRecovery.SequenceEqual(GetRecoveryRunOnceValues(appDirectoryPath)),
                    "Recovery must publish a fresh RunOnce generation before deferring.");
                Assert.IsFalse(liveApplication.HasExited, "The application fixture must remain alive after recovery deferral.");
            }
            finally
            {
                ProcessDeadline cleanup = cleanupDeadline.Value;
                try
                {
                    if (!liveApplication.HasExited)
                    {
                        liveApplication.StandardInput.WriteLine("release");
                    }

                    WaitForDirectExit(liveApplication, cleanup);
                }
                catch (Exception exception)
                {
                    cleanupDiagnostics.Add(
                        "live application fixture cleanup failed: " + exception.Message);
                    try
                    {
                        StopDirectProcess(liveApplication);
                    }
                    catch (Exception stopException)
                    {
                        cleanupDiagnostics.Add(
                            "live application fixture stop failed: " + stopException.Message);
                    }
                }
            }
        }
    }

    /// <summary>実再起動後の診断故障や監視者の起動でも新版と回復資料を保ち、次回の明示回復だけが残留一時物を退役します。</summary>
    /// <remarks>実再起動のPIDを持つ確定済み・確定保存直前の両journalを使い、全processとアプリ固有登録を所有scopeで回収します。</remarks>
    private void ApplyUpdate_PostRestartDiagnosticFailureKeepsCommittedApplication(bool restarting)
    {
        {
            string tempDirectoryPath = this.request.RootDirectory;
            (string appDirectory, string packagePath, string backupDirectory) = PrepareCleanupFailurePackage(tempDirectoryPath);
            (Type program, object request) = Commands.PrepareManagedUpdate(appDirectory, packagePath, backupDirectory);
            string extractDirectory = Path.Combine(appDirectory, "update_work", "extracted");
            bool extractRemovedBeforeRestart = false;
            bool packageRetainedBeforeRestart = false;
            int diagnosticAttemptsBeforeRestart = 0;
            var diagnosticWriter = new ThrowingDiagnosticWriter();
            using FileStream packageLock = new(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            string? observedRestartPath = null;
            Process? restartedProcess = null;
            int restartedProcessId = 0;
            Func<ProcessStartInfo, Process> startApplication = startInfo =>
            {
                observedRestartPath = startInfo.FileName;
                extractRemovedBeforeRestart = !Directory.Exists(extractDirectory);
                packageRetainedBeforeRestart = File.Exists(packagePath);
                diagnosticAttemptsBeforeRestart = diagnosticWriter.WriteAttempts;
                packageLock.Dispose();
                startInfo.UseShellExecute = false;
                startInfo.RedirectStandardOutput = true;
                startInfo.RedirectStandardError = true;
                startInfo.CreateNoWindow = true;
                restartedProcess = StartDirectProcess(startInfo);
                restartedProcessId = restartedProcess.Id;
                return restartedProcess;
            };
            TextWriter originalError = Console.Error;
            try
            {
                Console.SetError(diagnosticWriter);
                program.GetMethod("ApplyUpdate", BindingFlags.NonPublic | BindingFlags.Static)!
                    .Invoke(null, [request, startApplication]);
            }
            finally
            {
                Console.SetError(originalError);
            }

            Assert.IsNotNull(restartedProcess);
            WaitForDirectExit(restartedProcess, deadline);
            Assert.AreEqual(0, restartedProcess.ExitCode);
            Assert.AreEqual(Path.Combine(appDirectory, "restart.exe"), observedRestartPath);
            Assert.IsTrue(extractRemovedBeforeRestart);
            Assert.IsTrue(packageRetainedBeforeRestart);
            Assert.AreEqual("restarted", File.ReadAllText(Path.Combine(appDirectory, "restart.marker")));
            Assert.AreEqual("new-app", File.ReadAllText(Path.Combine(appDirectory, "BeMusicSeeker.exe")));
            Assert.IsTrue(File.Exists(packagePath));
            Assert.IsFalse(Directory.Exists(extractDirectory));
            using var journal = JsonDocument.Parse(File.ReadAllText(Path.Combine(appDirectory, "update_work", "update-transaction.json")));
            Assert.AreEqual("committed", journal.RootElement.GetProperty("Phase").GetString());
            Assert.IsTrue(Directory.Exists(backupDirectory), "Residual temporary artifacts must keep the backup together with the committed journal.");
            Assert.IsTrue(diagnosticWriter.WriteAttempts > diagnosticAttemptsBeforeRestart,
                "The test must reach the post-restart diagnostic failure, not only the earlier ZIP cleanup warning.");
            Assert.AreEqual(restartedProcessId, journal.RootElement.GetProperty("RestartProcessId").GetInt32());
            string journalPath = Path.Combine(appDirectory, "update_work", "update-transaction.json");
            if (restarting)
            {
                JsonNode interruptedJournal = JsonNode.Parse(File.ReadAllText(journalPath))!;
                interruptedJournal["Phase"] = "restarting";
                File.WriteAllText(journalPath, interruptedJournal.ToJsonString());
            }
            using (File.Open(packagePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                // 監視者が削除できる実状態で後追い削除を検出します。
            }
            Commands.RunUpdaterWatchdogRecovery(appDirectory);
            Assert.AreEqual("new-app", File.ReadAllText(Path.Combine(appDirectory, "BeMusicSeeker.exe")));
            Assert.IsTrue(File.Exists(packagePath));
            Assert.IsTrue(Directory.Exists(backupDirectory));
            using var retainedJournal = JsonDocument.Parse(File.ReadAllText(journalPath));
            Assert.AreEqual("committed", retainedJournal.RootElement.GetProperty("Phase").GetString());
            Assert.AreEqual(1, GetRecoveryRunOnceValues(appDirectory).Count);
            Assert.AreEqual(1, GetRecoverySupervisorValues(appDirectory).Count);
            Assert.IsFalse(File.Exists(Path.Combine(appDirectory, "update_work", "update-failure.txt")));

            Commands.RunUpdaterRecovery(appDirectory);
            Assert.IsFalse(File.Exists(packagePath));
            Assert.IsFalse(Directory.Exists(extractDirectory));
            Assert.IsFalse(Directory.Exists(backupDirectory));
            Assert.IsFalse(File.Exists(journalPath));
            Assert.AreEqual(0, GetRecoveryRunOnceValues(appDirectory).Count);
            Assert.AreEqual(0, GetRecoverySupervisorValues(appDirectory).Count);
            Assert.AreEqual("new-app", File.ReadAllText(Path.Combine(appDirectory, "BeMusicSeeker.exe")));

        }
    }
}
