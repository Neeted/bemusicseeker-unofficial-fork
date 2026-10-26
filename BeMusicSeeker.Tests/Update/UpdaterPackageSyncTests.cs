using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models.Update;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;

namespace BeMusicSeeker.Tests;

[TestClass]
[TestCategory("ProcessIntegration")]
public sealed class UpdaterPackageSyncTests
{
    private CaseProcessScope? activeProcessScope;

    /// <summary>
    /// 同じドライブの非root作業場所からroot配置を受付け、proceed前の旧構成保持と適用後の管理対象・長パス・実再起動を確認する。
    /// </summary>
    /// <remarks>ケース専用の非ZドライブとJobを所有し、プロセス・回復登録・別名・実領域の順に回収する。UNC実共有の検証とは分担する。</remarks>
    [TestMethod]
    public void UpdaterAppliesDriveRootPackageOnlyAfterProceedFromSameDriveChildDirectory()
    {
        WithTemporaryDirectory((tempDirectoryPath, appDirectoryPath) =>
        {
            string sourceDirectory = Path.Combine(tempDirectoryPath, "package-source");
            string packagePath = Path.Combine(appDirectoryPath, "update_work", "downloads", "package.zip");
            string backupDirectory = Path.Combine(appDirectoryPath, "update_backup");
            string readyPath = Path.Combine(appDirectoryPath, "update_work", "current", "updater-ready.txt");
            string decisionPath = Path.Combine(appDirectoryPath, "update_work", "current", "updater-decision.txt");
            string workingDirectory = Directory.CreateDirectory(Path.Combine(appDirectoryPath, "update_work", "current")).FullName;
            string longRelativePath = string.Join('/', Enumerable.Repeat(new string('a', 40), 7)) + "/managed.txt";
            WriteTextFile(appDirectoryPath, "BeMusicSeeker.exe", "old-app");
            CopyRestartExecutable(appDirectoryPath, "restart-marker.txt", "old-version");
            WriteTextFile(appDirectoryPath, "obsolete.dll", "old-managed");
            WriteTextFile(appDirectoryPath, "update-managed-files.txt", "BeMusicSeeker.exe\nrestart.exe\nrestart.json\nobsolete.dll");
            WriteTextFile(appDirectoryPath, "config/user.config", "preserved-config");
            WriteTextFile(appDirectoryPath, "data/settings.db", "preserved-data");
            WriteTextFile(appDirectoryPath, "logs/user.log", "preserved-log");
            WriteTextFile(appDirectoryPath, "unmanaged.txt", "user-content");
            string canonicalBefore = ComputeCanonicalTreeHash(appDirectoryPath);
            string preservedBefore = ComputePreservedTreeHash(appDirectoryPath);
            WriteTextFile(sourceDirectory, "BeMusicSeeker.exe", "new-app");
            CopyRestartExecutable(sourceDirectory, "restart-marker.txt", "new-version");
            WriteTextFile(sourceDirectory, longRelativePath, "long-managed");
            WriteTextFile(sourceDirectory, "update-managed-files.txt", "BeMusicSeeker.exe\nrestart.exe\nrestart.json\n" + longRelativePath);
            ZipFile.CreateFromDirectory(sourceDirectory, packagePath);

            ProcessStartInfo startInfo = CreateUpdaterStartInfo(appDirectoryPath, packagePath, backupDirectory);
            startInfo.WorkingDirectory = workingDirectory;
            OwnedProcess process = StartOwnedProcess(startInfo, useExecutionGate: true);
            Assert.IsTrue(WaitForReadyOrProcessExit(process, readyPath), "Root request failed before ready: " + process.Description);
            Assert.AreEqual("1", File.ReadAllText(readyPath).Trim());
            Assert.IsFalse(process.WaitForExit(250), "The exited parent must not bypass the proceed decision.");
            Assert.AreEqual(canonicalBefore, ComputeCanonicalTreeHash(appDirectoryPath));
            Assert.AreEqual(preservedBefore, ComputePreservedTreeHash(appDirectoryPath));
            Assert.IsTrue(File.Exists(packagePath));
            Assert.IsFalse(File.Exists(Path.Combine(appDirectoryPath, "restart-marker.txt")));
            using (var journal = JsonDocument.Parse(File.ReadAllText(Path.Combine(appDirectoryPath, "update_work", "update-transaction.json"))))
            {
                Assert.AreEqual(appDirectoryPath, journal.RootElement.GetProperty("AppDirectory").GetString());
                Assert.AreEqual("prepared", journal.RootElement.GetProperty("Phase").GetString());
            }

            PublishUpdaterDecision(decisionPath, "proceed");
            process.WaitForExitAndChildren();
            Assert.AreEqual(0, process.ExitCode, process.GetStandardOutput() + Environment.NewLine + process.GetStandardError());
            Assert.AreEqual("new-app", File.ReadAllText(Path.Combine(appDirectoryPath, "BeMusicSeeker.exe")));
            Assert.AreEqual("long-managed", File.ReadAllText(Path.Combine(appDirectoryPath, longRelativePath)));
            Assert.IsFalse(File.Exists(Path.Combine(appDirectoryPath, "obsolete.dll")));
            Assert.AreEqual("new-version", File.ReadAllText(Path.Combine(appDirectoryPath, "restart-marker.txt")));
            Assert.AreEqual(preservedBefore, ComputePreservedTreeHash(appDirectoryPath));
            Assert.AreEqual("preserved-log", File.ReadAllText(Path.Combine(appDirectoryPath, "logs", "user.log")));
            Assert.AreEqual("user-content", File.ReadAllText(Path.Combine(appDirectoryPath, "unmanaged.txt")));
            Assert.IsFalse(File.Exists(packagePath));
            Assert.IsFalse(Directory.Exists(backupDirectory));
            Assert.IsFalse(File.Exists(Path.Combine(appDirectoryPath, "update_work", "update-transaction.json")));
            Assert.AreEqual(0, GetRecoveryRunOnceValues(appDirectoryPath).Count);
            Assert.AreEqual(0, GetRecoverySupervisorValues(appDirectoryPath).Count);
        }, useDriveRoot: true);
    }

    /// <summary>
    /// gatewayの完成済み引用引数を実Updaterへ渡し、root末尾backslashと非root作業場所を保ってready receiptを受け取り、取消後の旧構成と所有プロセス終端を確認する。
    /// </summary>
    /// <remarks>gatewayの親PIDを変更せず、成功適用は別の実CLI代表に分担する。既存Jobが全子・両pipeと登録を回収してから非Z別名を解除する。</remarks>
    [TestMethod]
    public void WindowsGatewayAcceptsDriveRootReadyReceiptAndCancelsWithoutApplying()
    {
        WithTemporaryDirectory((_, appDirectoryPath) =>
        {
            string packagePath = Path.Combine(appDirectoryPath, "update_work", "downloads", "package.zip");
            string readyPath = Path.Combine(appDirectoryPath, "update_work", "current", "updater-ready.txt");
            string decisionPath = Path.Combine(appDirectoryPath, "update_work", "current", "updater-decision.txt");
            string workingDirectory = Directory.CreateDirectory(Path.Combine(appDirectoryPath, "update_work", "current")).FullName;
            WriteTextFile(appDirectoryPath, "BeMusicSeeker.exe", "old-app");
            CopyRestartExecutable(appDirectoryPath, "restart-marker.txt", "unexpected-restart");
            WriteTextFile(appDirectoryPath, "update_work/downloads/package.zip", "not-yet-applied");
            string canonicalBefore = ComputeCanonicalTreeHash(appDirectoryPath);
            OwnedProcess? process = null;
            var gateway = new WindowsUpdaterProcessGateway(startInfo =>
            {
                Assert.IsTrue(startInfo.CreateNoWindow);
                Assert.AreEqual(workingDirectory, startInfo.WorkingDirectory);
                startInfo.RedirectStandardOutput = true;
                startInfo.RedirectStandardError = true;
                process = StartOwnedProcess(startInfo, useExecutionGate: true);
                return process.BorrowedProcess;
            });
            var request = UpdaterProcessLaunchRequest.Create(
                FindUpdaterExecutable(), workingDirectory, appDirectoryPath, packagePath,
                Path.Combine(appDirectoryPath, "update_backup"), readyPath, decisionPath,
                Path.Combine(appDirectoryPath, "restart.exe"));
            UpdaterLaunchReceipt receipt = gateway.Prepare(request).Start();
            Assert.IsNotNull(process);
            Assert.AreEqual("1", File.ReadAllText(readyPath).Trim());
            Assert.IsFalse(process.HasExited);
            Assert.AreEqual(canonicalBefore, ComputeCanonicalTreeHash(appDirectoryPath));
            receipt.Abort();
            process.WaitForExitAndChildren();
            // 決定ファイルはUpdaterが読み取った直後に消費する。取消後の保証は全所有プロセスの終端と旧構成で観測する。
            Assert.IsTrue(process.HasExited);
            Assert.AreEqual(canonicalBefore, ComputeCanonicalTreeHash(appDirectoryPath));
            Assert.IsTrue(File.Exists(packagePath));
            Assert.IsFalse(File.Exists(Path.Combine(appDirectoryPath, "restart-marker.txt")));
        }, useDriveRoot: true);
    }

    /// <summary>
    /// 受付確認後もproceed前は更新を適用せず、prepared保存情報と独立復旧登録を保ち、取消で旧ファイルを維持して登録を片付ける順序誤りを検出する。
    /// </summary>
    /// <remarks>独立Jobと固有パスを所有し、共有registryはこのアプリ領域の登録だけを回収する。</remarks>
    [TestMethod]
    public void UpdaterWaitsForProceedDecisionBeforeApplyingOrWaitingForApplicationExit()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string appDirectoryPath = Path.Combine(tempDirectoryPath, "app");
            string packagePath = Path.Combine(appDirectoryPath, "update_work", "downloads", "package.zip");
            string backupDirectoryPath = Path.Combine(appDirectoryPath, "update_backup");
            WriteTextFile(appDirectoryPath, "BeMusicSeeker.exe", "old-app");
            CopyRestartExecutable(appDirectoryPath);
            WriteTextFile(appDirectoryPath, "update_work/downloads/package.zip", "not-yet-applied");
            string applicationProcessId = GetExitedProcessId().ToString();
            string decisionFilePath = Path.Combine(appDirectoryPath, "update_work", "current", "updater-decision.txt");

            OwnedProcess process = StartUpdater(
                appDirectoryPath,
                packagePath,
                backupDirectoryPath,
                processId: applicationProcessId,
                publishProceed: false);
            try
            {
                Assert.IsFalse(process.WaitForExit(250));
                Assert.AreEqual(1, GetRecoveryRunOnceValues(appDirectoryPath).Count, "The transaction must arm an independent recovery handoff.");
                Assert.AreEqual(1, GetRecoverySupervisorValues(appDirectoryPath).Count, "The transaction must arm a persistent recovery supervisor.");

                string journalPath = Path.Combine(appDirectoryPath, "update_work", "update-transaction.json");
                string journalJson = File.ReadAllText(journalPath);
                using var journalDocument = JsonDocument.Parse(journalJson);
                JsonElement journal = journalDocument.RootElement;
                Assert.AreEqual(JsonValueKind.Number, journal.GetProperty("Version").ValueKind);
                Assert.AreEqual(1, journal.GetProperty("Version").GetInt32());
                Assert.AreEqual(JsonValueKind.String, journal.GetProperty("Phase").ValueKind);
                Assert.AreEqual("prepared", journal.GetProperty("Phase").GetString());
                Assert.AreEqual(JsonValueKind.String, journal.GetProperty("AppDirectory").ValueKind);
                Assert.AreEqual(appDirectoryPath, journal.GetProperty("AppDirectory").GetString());
                Assert.AreEqual(JsonValueKind.String, journal.GetProperty("PackagePath").ValueKind);
                Assert.AreEqual(packagePath, journal.GetProperty("PackagePath").GetString());
                Assert.AreEqual(JsonValueKind.Number, journal.GetProperty("ApplicationProcessId").ValueKind);
                Assert.AreEqual(int.Parse(applicationProcessId), journal.GetProperty("ApplicationProcessId").GetInt32());
                Assert.AreEqual(JsonValueKind.String, journal.GetProperty("BackupDirectory").ValueKind);
                Assert.AreEqual(backupDirectoryPath, journal.GetProperty("BackupDirectory").GetString());
                Assert.AreEqual(JsonValueKind.String, journal.GetProperty("PreviousDirectory").ValueKind);
                Assert.AreEqual(Path.Combine(backupDirectoryPath, "previous"), journal.GetProperty("PreviousDirectory").GetString());
                Assert.AreEqual(JsonValueKind.String, journal.GetProperty("ExtractDirectory").ValueKind);
                Assert.AreEqual(Path.Combine(appDirectoryPath, "update_work", "extracted"), journal.GetProperty("ExtractDirectory").GetString());
                Assert.AreEqual(JsonValueKind.String, journal.GetProperty("RestartExecutablePath").ValueKind);
                Assert.AreEqual(Path.Combine(appDirectoryPath, "restart.exe"), journal.GetProperty("RestartExecutablePath").GetString());
                Assert.AreEqual(JsonValueKind.Number, journal.GetProperty("RestartProcessId").ValueKind);
                Assert.AreEqual(0, journal.GetProperty("RestartProcessId").GetInt32());
                Assert.AreEqual(JsonValueKind.True, journal.GetProperty("Preflight").ValueKind);
                Assert.IsTrue(journal.GetProperty("Preflight").GetBoolean());
                Assert.AreEqual(JsonValueKind.False, journal.GetProperty("BackupComplete").ValueKind);
                Assert.IsFalse(journal.GetProperty("BackupComplete").GetBoolean());
                Assert.AreEqual(JsonValueKind.Array, journal.GetProperty("NewPackagePaths").ValueKind);
                Assert.AreEqual(0, journal.GetProperty("NewPackagePaths").GetArrayLength());
            }
            finally
            {
                if (!process.HasExited)
                {
                    bool decisionPublished = false;
                    try
                    {
                        PublishUpdaterDecision(decisionFilePath, "cancel");
                        decisionPublished = true;
                    }
                    catch (Exception exception)
                    {
                        activeProcessScope?.RecordDiagnostic(
                            "updater decision cleanup failed: " + exception.Message);
                    }

                    try
                    {
                        if (decisionPublished)
                        {
                            process.WaitForExitAndChildren();
                        }
                        else
                        {
                            process.StopAndWait();
                        }
                    }
                    catch (Exception exception)
                    {
                        activeProcessScope?.RecordDiagnostic(
                            "updater process cleanup failed: " + exception.Message);
                    }
                }
            }

            Assert.IsTrue(process.HasExited);
            Assert.AreEqual(0, process.ExitCode);
            Assert.AreEqual("old-app", File.ReadAllText(Path.Combine(appDirectoryPath, "BeMusicSeeker.exe")));
            Assert.AreEqual(0, GetRecoveryRunOnceValues(appDirectoryPath).Count, "Cancellation must clear the recovery handoff.");
            Assert.AreEqual(0, GetRecoverySupervisorValues(appDirectoryPath).Count, "Cancellation must clear the persistent recovery supervisor.");
        });
    }

    /// <summary>
    /// 新しい更新の前にapplyingの未完了journalを回復し、新版の適用後だけjournalと旧世代を片付けて未回復状態への上書きを検出する。
    /// </summary>
    /// <remarks>独立Jobと固有パスを所有し、共有registryはこのアプリ領域の登録だけを回収する。</remarks>
    [TestMethod]
    public void ApplyUpdateRecoversIncompleteDurableJournalBeforeNewMutation()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string appDirectoryPath = Path.Combine(tempDirectoryPath, "app");
            string packageSourceDirectoryPath = Path.Combine(tempDirectoryPath, "package-source");
            string packagePath = Path.Combine(appDirectoryPath, "update_work", "downloads", "package.zip");
            string backupDirectoryPath = Path.Combine(appDirectoryPath, "update_backup");
            string previousDirectoryPath = Path.Combine(backupDirectoryPath, "previous");
            string extractDirectoryPath = Path.Combine(appDirectoryPath, "update_work", "extracted");
            string journalPath = Path.Combine(appDirectoryPath, "update_work", "update-transaction.json");

            WriteTextFile(appDirectoryPath, "BeMusicSeeker.exe", "partial-new");
            CopyRestartExecutable(appDirectoryPath);
            WriteTextFile(appDirectoryPath, "update-managed-files.txt", "BeMusicSeeker.exe\nrestart.exe\nrestart.json");
            WriteTextFile(appDirectoryPath, "update_backup/previous/BeMusicSeeker.exe", "old-app");
            CopyRestartExecutable(previousDirectoryPath);
            WriteTextFile(previousDirectoryPath, "update-managed-files.txt", "BeMusicSeeker.exe\nrestart.exe\nrestart.json");
            WriteTextFile(
                appDirectoryPath,
                "update_work/update-transaction.json",
                JsonSerializer.Serialize(new
                {
                    Version = 1,
                    Phase = "applying",
                    AppDirectory = appDirectoryPath,
                    PackagePath = packagePath,
                    BackupDirectory = backupDirectoryPath,
                    PreviousDirectory = previousDirectoryPath,
                    ExtractDirectory = extractDirectoryPath,
                    RestartExecutablePath = Path.Combine(appDirectoryPath, "restart.exe"),
                    BackupComplete = true,
                    NewPackagePaths = new[] { "BeMusicSeeker.exe", "restart.exe", "restart.json", "update-managed-files.txt" }
                }));

            WriteTextFile(packageSourceDirectoryPath, "BeMusicSeeker.exe", "new-app");
            CopyRestartExecutable(packageSourceDirectoryPath);
            WriteTextFile(packageSourceDirectoryPath, "update-managed-files.txt", "BeMusicSeeker.exe\nrestart.exe\nrestart.json");
            ZipFile.CreateFromDirectory(packageSourceDirectoryPath, packagePath);

            RunUpdater(appDirectoryPath, packagePath, backupDirectoryPath);

            Assert.AreEqual("new-app", File.ReadAllText(Path.Combine(appDirectoryPath, "BeMusicSeeker.exe")));
            Assert.IsFalse(File.Exists(journalPath), "A committed transaction must remove its durable journal.");
            Assert.IsFalse(Directory.Exists(backupDirectoryPath), "A committed transaction must remove its previous-generation backup.");
        });
    }

    /// <summary>
    /// バックアップ途中の回復は未移動の現行ファイルを消さず、部分的なコピーを現行へ昇格する誤りを検出する。
    /// </summary>
    /// <remarks>独立Jobと固有パスを所有し、共有registryはこのアプリ領域の登録だけを回収する。</remarks>
    [TestMethod]
    public void RecoverCommandRestoresPartialBackupWithoutDeletingUnmovedApplicationFiles()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string appDirectoryPath = Path.Combine(tempDirectoryPath, "app");
            string packagePath = Path.Combine(appDirectoryPath, "update_work", "downloads", "package.zip");
            string backupDirectoryPath = Path.Combine(appDirectoryPath, "update_backup");
            string previousDirectoryPath = Path.Combine(backupDirectoryPath, "previous");
            string extractDirectoryPath = Path.Combine(appDirectoryPath, "update_work", "extracted");
            string journalPath = Path.Combine(appDirectoryPath, "update_work", "update-transaction.json");

            WriteTextFile(appDirectoryPath, "BeMusicSeeker.exe", "old-app");
            WriteTextFile(appDirectoryPath, "docs/unmoved.txt", "still-old");
            CopyRestartExecutable(appDirectoryPath);
            WriteTextFile(previousDirectoryPath, "BeMusicSeeker.exe", "partial-copy");
            WriteTextFile(
                appDirectoryPath,
                "update_work/update-transaction.json",
                JsonSerializer.Serialize(new
                {
                    Version = 1,
                    Phase = "backing-up",
                    AppDirectory = appDirectoryPath,
                    PackagePath = packagePath,
                    BackupDirectory = backupDirectoryPath,
                    PreviousDirectory = previousDirectoryPath,
                    ExtractDirectory = extractDirectoryPath,
                    RestartExecutablePath = Path.Combine(appDirectoryPath, "restart.exe"),
                    NewPackagePaths = new[] { "BeMusicSeeker.exe", "docs/unmoved.txt" }
                }));

            RunUpdaterRecovery(appDirectoryPath);

            Assert.AreEqual("old-app", File.ReadAllText(Path.Combine(appDirectoryPath, "BeMusicSeeker.exe")));
            Assert.AreEqual("still-old", File.ReadAllText(Path.Combine(appDirectoryPath, "docs", "unmoved.txt")));
            Assert.IsFalse(File.Exists(journalPath));
            Assert.IsFalse(Directory.Exists(backupDirectoryPath));
        });
    }

    /// <summary>
    /// watchdog回復で旧版を復元して実再起動markerを得てからjournalとbackupを退役し、復旧と再起動の接続欠落を検出する。
    /// </summary>
    /// <remarks>独立Jobと固有パスを所有し、共有registryはこのアプリ領域の登録だけを回収する。</remarks>
    [TestMethod]
    public void WatchdogRecoversApplyingTransactionAndRestartsPreviousApplication()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string appDirectoryPath = Path.Combine(tempDirectoryPath, "app");
            string packagePath = Path.Combine(appDirectoryPath, "update_work", "downloads", "package.zip");
            string backupDirectoryPath = Path.Combine(appDirectoryPath, "update_backup");
            string previousDirectoryPath = Path.Combine(backupDirectoryPath, "previous");
            string extractDirectoryPath = Path.Combine(appDirectoryPath, "update_work", "extracted");
            string journalPath = Path.Combine(appDirectoryPath, "update_work", "update-transaction.json");

            WriteTextFile(appDirectoryPath, "BeMusicSeeker.exe", "partial-new");
            CopyRestartExecutable(appDirectoryPath, "restart-marker.txt", "restarted" + Environment.NewLine);
            WriteTextFile(appDirectoryPath, "update_work/downloads/package.zip", "package");
            WriteTextFile(previousDirectoryPath, "BeMusicSeeker.exe", "old-app");
            WriteTextFile(
                appDirectoryPath,
                "update_work/update-transaction.json",
                JsonSerializer.Serialize(new
                {
                    Version = 1,
                    Phase = "applying",
                    AppDirectory = appDirectoryPath,
                    PackagePath = packagePath,
                    ApplicationProcessId = 0,
                    BackupDirectory = backupDirectoryPath,
                    PreviousDirectory = previousDirectoryPath,
                    ExtractDirectory = extractDirectoryPath,
                    RestartExecutablePath = Path.Combine(appDirectoryPath, "restart.exe"),
                    BackupComplete = true,
                    NewPackagePaths = new[] { "BeMusicSeeker.exe" }
                }));

            RunUpdaterWatchdogRecovery(appDirectoryPath);

            Assert.AreEqual("old-app", File.ReadAllText(Path.Combine(appDirectoryPath, "BeMusicSeeker.exe")));
            AssertRestartFileExists(Path.Combine(appDirectoryPath, "restart-marker.txt"));
            Assert.IsFalse(File.Exists(journalPath));
            Assert.IsFalse(Directory.Exists(backupDirectoryPath));
        });
    }

    /// <summary>
    /// 同じドライブの子作業場所からドライブルートを回復し、記録されたアプリPIDが再利用されても旧版復元と実再起動を止める誤りを検出する。
    /// </summary>
    /// <remarks>独立Jobと固有パスを所有し、共有registryはこのアプリ領域の登録だけを回収する。</remarks>
    [TestMethod]
    public void RecoverCommandDoesNotTrustReusedApplicationPidAfterRestart()
    {
        WithTemporaryDirectory((_, appDirectoryPath) =>
        {
            string packagePath = Path.Combine(appDirectoryPath, "update_work", "downloads", "package.zip");
            string backupDirectoryPath = Path.Combine(appDirectoryPath, "update_backup");
            string previousDirectoryPath = Path.Combine(backupDirectoryPath, "previous");
            string extractDirectoryPath = Path.Combine(appDirectoryPath, "update_work", "extracted");
            string journalPath = Path.Combine(appDirectoryPath, "update_work", "update-transaction.json");

            WriteTextFile(appDirectoryPath, "BeMusicSeeker.exe", "partial-new");
            CopyRestartExecutable(appDirectoryPath, "restart-marker.txt", "restarted" + Environment.NewLine);
            WriteTextFile(previousDirectoryPath, "BeMusicSeeker.exe", "old-app");
            WriteTextFile(
                appDirectoryPath,
                "update_work/update-transaction.json",
                JsonSerializer.Serialize(new
                {
                    Version = 1,
                    Phase = "applying",
                    AppDirectory = appDirectoryPath,
                    PackagePath = packagePath,
                    ApplicationProcessId = Environment.ProcessId,
                    BackupDirectory = backupDirectoryPath,
                    PreviousDirectory = previousDirectoryPath,
                    ExtractDirectory = extractDirectoryPath,
                    RestartExecutablePath = Path.Combine(appDirectoryPath, "restart.exe"),
                    BackupComplete = true,
                    NewPackagePaths = new[] { "BeMusicSeeker.exe" }
                }));

            string workingDirectory = Directory.CreateDirectory(Path.Combine(appDirectoryPath, "update_work", "current")).FullName;
            RunUpdaterRecovery(appDirectoryPath, workingDirectory);

            Assert.AreEqual("old-app", File.ReadAllText(Path.Combine(appDirectoryPath, "BeMusicSeeker.exe")));
            AssertRestartFileExists(Path.Combine(appDirectoryPath, "restart-marker.txt"));
            Assert.IsFalse(File.Exists(journalPath));
        }, useDriveRoot: true);
    }

    /// <summary>
    /// 同じ実行ファイルが実際に生存すると更新・復旧を延期して旧世代と新しい復旧登録を保ち、稼働中のアプリを上書きする誤りを検出する。
    /// </summary>
    /// <remarks>独立Jobと固有パスを所有し、共有registryはこのアプリ領域の登録だけを回収する。</remarks>
    [TestMethod]
    public void RecoverCommandDefersWhenTheApplicationExecutableIsStillRunning()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
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
                    Phase = "applying",
                    AppDirectory = appDirectoryPath,
                    PackagePath = packagePath,
                    ApplicationProcessId = Environment.ProcessId,
                    BackupDirectory = backupDirectoryPath,
                    PreviousDirectory = previousDirectoryPath,
                    ExtractDirectory = extractDirectoryPath,
                    RestartExecutablePath = restartExecutablePath,
                    BackupComplete = true,
                    NewPackagePaths = new[] { "BeMusicSeeker.exe" }
                }));

            OwnedProcess liveApplication = StartLiveApplicationFixture(
                appDirectoryPath,
                restartExecutablePath);
            try
            {
                OwnedProcess normalUpdater = StartUpdater(
                    appDirectoryPath,
                    packagePath,
                    backupDirectoryPath,
                    restartExecutablePath,
                    Environment.ProcessId.ToString());
                normalUpdater.WaitForExitAndChildren();
                Assert.AreEqual(2, normalUpdater.ExitCode);
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

                OwnedProcess recovery = StartUpdaterRecoveryProcess(appDirectoryPath);
                recovery.WaitForExitAndChildren();
                Assert.AreEqual(2, recovery.ExitCode);
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
                try
                {
                    if (!liveApplication.HasExited)
                    {
                        liveApplication.StandardInput.WriteLine("release");
                    }

                    liveApplication.WaitForExitAndChildren();
                }
                catch (Exception exception)
                {
                    activeProcessScope?.RecordDiagnostic(
                        "live application fixture cleanup failed: " + exception.Message);
                    try
                    {
                        liveApplication.StopAndWait();
                    }
                    catch (Exception stopException)
                    {
                        activeProcessScope?.RecordDiagnostic(
                            "live application fixture stop failed: " + stopException.Message);
                    }
                }
            }
        });
    }

    /// <summary>
    /// 復元済みjournalだけが残りbackupがない状態で再回復しても現行ファイルを保ち、後片付けの再実行で復元を繰り返す誤りを検出する。
    /// </summary>
    /// <remarks>独立Jobと固有パスを所有し、共有registryはこのアプリ領域の登録だけを回収する。</remarks>
    [TestMethod]
    public void RolledBackJournalCleanupDoesNotReplayRestoreAfterBackupWasRemoved()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string appDirectoryPath = Path.Combine(tempDirectoryPath, "app");
            string packagePath = Path.Combine(appDirectoryPath, "update_work", "downloads", "package.zip");
            string backupDirectoryPath = Path.Combine(appDirectoryPath, "update_backup");
            string previousDirectoryPath = Path.Combine(backupDirectoryPath, "previous");
            string extractDirectoryPath = Path.Combine(appDirectoryPath, "update_work", "extracted");
            string journalPath = Path.Combine(appDirectoryPath, "update_work", "update-transaction.json");

            WriteTextFile(appDirectoryPath, "BeMusicSeeker.exe", "restored-old");
            CopyRestartExecutable(appDirectoryPath);
            WriteTextFile(
                appDirectoryPath,
                "update_work/update-transaction.json",
                JsonSerializer.Serialize(new
                {
                    Version = 1,
                    Phase = "rolled-back",
                    AppDirectory = appDirectoryPath,
                    PackagePath = packagePath,
                    BackupDirectory = backupDirectoryPath,
                    PreviousDirectory = previousDirectoryPath,
                    ExtractDirectory = extractDirectoryPath,
                    RestartExecutablePath = Path.Combine(appDirectoryPath, "restart.exe"),
                    NewPackagePaths = new[] { "BeMusicSeeker.exe" }
                }));

            RunUpdaterRecovery(appDirectoryPath);

            Assert.AreEqual("restored-old", File.ReadAllText(Path.Combine(appDirectoryPath, "BeMusicSeeker.exe")));
            Assert.IsFalse(File.Exists(journalPath));
        });
    }

    /// <summary>
    /// 判断待ちの実ツールがリースを保持すると競合更新を失敗にし、先着取消後も旧ファイルを保って二重適用の受付漏れを検出する。
    /// </summary>
    /// <remarks>独立Jobと固有パスを所有し、共有registryはこのアプリ領域の登録だけを回収する。</remarks>
    [TestMethod]
    public void UpdaterRejectsConcurrentTransactionLeaseWithoutMutation()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string appDirectoryPath = Path.Combine(tempDirectoryPath, "app");
            string packagePath = Path.Combine(appDirectoryPath, "update_work", "downloads", "package.zip");
            string backupDirectoryPath = Path.Combine(appDirectoryPath, "update_backup");
            WriteTextFile(appDirectoryPath, "BeMusicSeeker.exe", "old-app");
            CopyRestartExecutable(appDirectoryPath);
            WriteTextFile(appDirectoryPath, "update_work/downloads/package.zip", "not-used");

            OwnedProcess firstUpdater = StartUpdater(
                appDirectoryPath,
                packagePath,
                backupDirectoryPath,
                publishProceed: false);
            OwnedProcess secondUpdater = StartOwnedProcess(
                CreateUpdaterStartInfo(
                    appDirectoryPath,
                    packagePath,
                    backupDirectoryPath),
                useExecutionGate: true);

            secondUpdater.WaitForExitAndChildren();
            Assert.AreNotEqual(0, secondUpdater.ExitCode);
            PublishUpdaterDecision(
                Path.Combine(appDirectoryPath, "update_work", "current", "updater-decision.txt"),
                "cancel");
            firstUpdater.WaitForExitAndChildren();
            Assert.AreEqual(0, firstUpdater.ExitCode);
            Assert.AreEqual("old-app", File.ReadAllText(Path.Combine(appDirectoryPath, "BeMusicSeeker.exe")));
        });
    }

    /// <summary>
    /// 管理ファイルの排他失敗では本体と利用者ツリー・ZIPを維持して元理由を保存し、backup移動や再起動を始める誤りを検出する。
    /// </summary>
    /// <remarks>独立Jobと固有パスを所有し、共有registryはこのアプリ領域の登録だけを回収する。</remarks>
    [TestMethod]
    public void ApplyUpdate_PreflightManagedLockLeavesCanonicalAndPreservedTreesUnchanged()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string appDirectoryPath = Path.Combine(tempDirectoryPath, "app");
            string packageSourceDirectoryPath = Path.Combine(tempDirectoryPath, "package-source");
            string packagePath = Path.Combine(appDirectoryPath, "update_work", "downloads", "package.zip");
            string backupDirectoryPath = Path.Combine(appDirectoryPath, "update_backup");
            string restartExecutablePath = Path.Combine(appDirectoryPath, "restart.exe");
            string restartMarkerPath = Path.Combine(appDirectoryPath, "restart-marker.txt");

            WriteTextFile(appDirectoryPath, "BeMusicSeeker.exe", "old-app");
            CopyRestartExecutable(appDirectoryPath, "restart-marker.txt", "restarted" + Environment.NewLine);
            WriteTextFile(appDirectoryPath, "update-managed-files.txt", "BeMusicSeeker.exe\nrestart.exe\nrestart.json");
            WriteTextFile(appDirectoryPath, "data/settings.db", "preserved-data");
            WriteTextFile(appDirectoryPath, "config/user.config", "preserved-config");
            WriteTextFile(appDirectoryPath, "unmanaged.txt", "user-content");

            WriteTextFile(packageSourceDirectoryPath, "BeMusicSeeker.exe", "new-app");
            CopyRestartExecutable(packageSourceDirectoryPath, "restart-marker.txt", "updated" + Environment.NewLine);
            WriteTextFile(packageSourceDirectoryPath, "update-managed-files.txt", "BeMusicSeeker.exe\nrestart.exe\nrestart.json");
            ZipFile.CreateFromDirectory(packageSourceDirectoryPath, packagePath);

            string canonicalBefore = ComputeCanonicalTreeHash(appDirectoryPath);
            string preservedBefore = ComputePreservedTreeHash(appDirectoryPath);
            using FileStream managedLock = new(
                Path.Combine(appDirectoryPath, "BeMusicSeeker.exe"),
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);

            RunUpdaterExpectFailure(
                appDirectoryPath,
                packagePath,
                backupDirectoryPath,
                restartExecutablePath);

            Assert.AreEqual(canonicalBefore, ComputeCanonicalTreeHash(appDirectoryPath));
            Assert.AreEqual(preservedBefore, ComputePreservedTreeHash(appDirectoryPath));
            Assert.IsTrue(File.Exists(packagePath), "A preflight failure must leave the verified package available.");
            Assert.IsFalse(Directory.Exists(backupDirectoryPath), "Preflight must fail before backup rotation.");
            Assert.IsFalse(
                File.Exists(Path.Combine(appDirectoryPath, "update_work", "update-transaction.json")),
                "Preflight failure must not leave a mutation journal.");
            string failureReceiptPath = Path.Combine(appDirectoryPath, "update_work", "update-failure.txt");
            Assert.IsTrue(File.Exists(failureReceiptPath), "The preflight failure must be durably receipted.");
            StringAssert.Contains(File.ReadAllText(failureReceiptPath), "exclusive access");
            Assert.IsFalse(File.Exists(restartMarkerPath), "A preflight failure must not restart the application.");
        });
    }

    /// <summary>
    /// 復元途中にも故障したら元失敗receiptと唯一のbackup・journalを保持し、故障解消後の回復で収束して回復材料を消す誤りを検出する。
    /// </summary>
    /// <remarks>独立Jobと固有パスを所有し、共有registryはこのアプリ領域の登録だけを回収する。</remarks>
    [TestMethod]
    public void RecoverCommand_RollbackSecondFaultRetainsPrimaryReceiptAndRecoveryMaterial()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string appDirectoryPath = Path.Combine(tempDirectoryPath, "app");
            string packagePath = Path.Combine(appDirectoryPath, "update_work", "downloads", "package.zip");
            string backupDirectoryPath = Path.Combine(appDirectoryPath, "update_backup");
            string previousDirectoryPath = Path.Combine(backupDirectoryPath, "previous");
            string extractDirectoryPath = Path.Combine(appDirectoryPath, "update_work", "extracted");
            string journalPath = Path.Combine(appDirectoryPath, "update_work", "update-transaction.json");
            string failureReceiptPath = Path.Combine(appDirectoryPath, "update_work", "update-failure.txt");
            string managedPath = Path.Combine(appDirectoryPath, "BeMusicSeeker.exe");
            string rollbackFaultPath = Path.Combine(appDirectoryPath, "restart.exe");

            WriteTextFile(appDirectoryPath, "BeMusicSeeker.exe", "new-app");
            CopyRestartExecutable(appDirectoryPath);
            WriteTextFile(appDirectoryPath, "data/settings.db", "preserved-data");
            WriteTextFile(appDirectoryPath, "config/user.config", "preserved-config");
            WriteTextFile(previousDirectoryPath, "BeMusicSeeker.exe", "old-app");
            CopyRestartExecutable(previousDirectoryPath);
            WriteTextFile(appDirectoryPath, "update_work/downloads/package.zip", "package");
            WriteTextFile(appDirectoryPath, "update_work/update-failure.txt", "primary-failure-sentinel");
            WriteTextFile(
                appDirectoryPath,
                "update_work/update-transaction.json",
                JsonSerializer.Serialize(new
                {
                    Version = 1,
                    Phase = "applying",
                    AppDirectory = appDirectoryPath,
                    PackagePath = packagePath,
                    BackupDirectory = backupDirectoryPath,
                    PreviousDirectory = previousDirectoryPath,
                    ExtractDirectory = extractDirectoryPath,
                    RestartExecutablePath = Path.Combine(appDirectoryPath, "restart.exe"),
                    BackupComplete = true,
                    NewPackagePaths = new[] { "BeMusicSeeker.exe", "restart.exe", "restart.json" }
                }));

            using (FileStream managedLock = new(rollbackFaultPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                OwnedProcess recovery = StartUpdaterRecoveryProcess(appDirectoryPath);
                recovery.WaitForExitAndChildren();
                Assert.AreNotEqual(0, recovery.ExitCode, "Rollback second fault must not be reported as success.");
            }

            Assert.IsTrue(File.Exists(journalPath), "Rollback failure must retain the transaction journal.");
            Assert.IsTrue(Directory.Exists(backupDirectoryPath), "Rollback failure must retain the only backup.");
            Assert.AreEqual(
                "old-app",
                File.ReadAllText(Path.Combine(appDirectoryPath, "BeMusicSeeker.exe")),
                "The deterministic second fault must occur after an earlier managed path promotion.");
            string failureReceipt = File.ReadAllText(failureReceiptPath);
            StringAssert.Contains(failureReceipt, "primary-failure-sentinel");
            StringAssert.Contains(failureReceipt, "rollback");

            RunUpdaterRecovery(appDirectoryPath);

            Assert.AreEqual("old-app", File.ReadAllText(managedPath));
            Assert.AreEqual("preserved-data", File.ReadAllText(Path.Combine(appDirectoryPath, "data", "settings.db")));
            Assert.AreEqual("preserved-config", File.ReadAllText(Path.Combine(appDirectoryPath, "config", "user.config")));
            Assert.IsFalse(File.Exists(journalPath), "Recovery after the fault is removed must converge and retire the journal.");
            Assert.IsFalse(Directory.Exists(backupDirectoryPath), "Recovery after the fault is removed must retire the backup.");
        });
    }

    /// <summary>
    /// 更新リース保持中の回復は短い否定観測で終端せず、取消による解放後に直列回復してRunOnceと監視登録を片付ける誤りを検出する。
    /// </summary>
    /// <remarks>独立Jobと固有パスを所有し、共有registryはこのアプリ領域の登録だけを回収する。</remarks>
    [TestMethod]
    public void RecoverCommandSerializesRunOnceConsumersWhenTransactionLeaseIsUnavailable()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string appDirectoryPath = Path.Combine(tempDirectoryPath, "app");
            string packagePath = Path.Combine(appDirectoryPath, "update_work", "downloads", "package.zip");
            string backupDirectoryPath = Path.Combine(appDirectoryPath, "update_backup");
            WriteTextFile(appDirectoryPath, "BeMusicSeeker.exe", "old-app");
            CopyRestartExecutable(appDirectoryPath);
            WriteTextFile(appDirectoryPath, "update_work/downloads/package.zip", "not-used");

            OwnedProcess firstUpdater = StartUpdater(
                appDirectoryPath,
                packagePath,
                backupDirectoryPath,
                publishProceed: false);
            Assert.AreEqual(1, GetRecoveryRunOnceValues(appDirectoryPath).Count);
            DeleteRecoveryRunOnceValues(appDirectoryPath);

            OwnedProcess recovery = StartUpdaterRecoveryProcess(appDirectoryPath);
            // リース所有者は判断待ちなので、解放前の短い否定観測で競合側が
            // 完了していないことを確認する。
            Assert.IsFalse(recovery.WaitForExit(250), "A competing recovery must wait for the transaction owner.");

            PublishUpdaterDecision(
                Path.Combine(appDirectoryPath, "update_work", "current", "updater-decision.txt"),
                "cancel");
            firstUpdater.WaitForExitAndChildren();
            Assert.AreEqual(0, firstUpdater.ExitCode);
            recovery.WaitForExitAndChildren();
            Assert.AreEqual(0, recovery.ExitCode);
            Assert.AreEqual(0, GetRecoveryRunOnceValues(appDirectoryPath).Count);
            Assert.AreEqual(0, GetRecoverySupervisorValues(appDirectoryPath).Count);
        });
    }

    /// <summary>
    /// 旧manifestがない配布から現layoutへ更新し、旧管理DLLだけを整理して利用者追加DLLと設定を保つことで移行時の過剰削除を検出する。
    /// </summary>
    /// <remarks>独立Jobと固有パスを所有し、共有registryはこのアプリ領域の登録だけを回収する。</remarks>
    [TestMethod]
    public void ApplyUpdate_RemovesLegacyDllLayoutWithoutPreviousManifest()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string appDirectoryPath = Path.Combine(tempDirectoryPath, "app");
            string packageSourceDirectoryPath = Path.Combine(tempDirectoryPath, "package-source");
            string packagePath = Path.Combine(tempDirectoryPath, "app", "update_work", "downloads", "package.zip");
            string backupDirectoryPath = Path.Combine(appDirectoryPath, "update_backup");

            WriteTextFile(appDirectoryPath, "BeMusicSeeker.exe", "old-app");
            WriteTextFile(appDirectoryPath, "BeMusicSeeker.exe.config", "old-config");
            CopyRestartExecutable(appDirectoryPath);
            WriteTextFile(appDirectoryPath, "libs/SevenZipExtractor.dll", "old-sevenzip");
            WriteTextFile(appDirectoryPath, "libs/x64/7z.dll", "old-7z-native");
            WriteTextFile(appDirectoryPath, "libs/x64/OggVorbis.NET64.dll", "old-wrong-ogv");
            WriteTextFile(appDirectoryPath, "libs/x86/7z.dll", "old-7z-x86");
            WriteTextFile(appDirectoryPath, "libs/x64/sqlite3.dll", "old-libs-x64-sqlite");
            WriteTextFile(appDirectoryPath, "x86/sqlite3.dll", "old-root-x86");
            WriteTextFile(appDirectoryPath, "libs/x86/user.dll", "user-libs-x86");
            WriteTextFile(appDirectoryPath, "x86/user.dll", "user-root-x86");
            WriteTextFile(appDirectoryPath, "x64/sqlite3.dll", "old-root-x64");
            WriteTextFile(appDirectoryPath, "runtimes/win-x64/native/e_sqlite3.dll", "old-rid-e-sqlite3");
            foreach (string legacyManagedLibraryName in new[]
            {
                "Bass.Net.dll",
                "DynamicJson.dll",
                "IniLibrary.dll",
                "Livet.dll",
                "Livet.Extensions.dll",
                "MetroRadiance.Chrome.dll",
                "MetroRadiance.Core.dll",
                "MetroRadiance.dll",
                "Microsoft.Expression.Drawing.dll",
                "Microsoft.Expression.Effects.dll",
                "Microsoft.Expression.Interactions.dll",
                "Microsoft.WindowsAPICodePack.dll",
                "Microsoft.WindowsAPICodePack.Shell.dll",
                "Newtonsoft.Json.dll",
                "NLog.Database.dll",
                "NLog.dll",
                "NLog.WindowsEventLog.dll",
                "QuickConverter.dll",
                "SgmlReaderDll.dll",
                "System.Collections.Immutable.dll",
                "System.Resources.Extensions.dll",
                "System.Memory.dll",
                "System.Buffers.dll",
                "System.Numerics.Vectors.dll",
                "System.Runtime.CompilerServices.Unsafe.dll",
                "System.Windows.Interactivity.dll",
                "sqlite.net.dll"
            })
            {
                WriteTextFile(appDirectoryPath, "libs/" + legacyManagedLibraryName, "legacy-managed-library");
            }
            foreach (string legacyRootX64File in new[]
            {
                "OggVorbis.NET.dll",
                "x64/OggVorbis.NET64.dll",
                "x64/7z.dll",
                "x64/bass.dll",
                "x64/bass_fx.dll",
                "x64/bassasio.dll",
                "x64/bassenc.dll",
                "x64/bassmix.dll",
                "x64/basswasapi.dll"
            })
            {
                WriteTextFile(appDirectoryPath, legacyRootX64File, "legacy-root-x64");
            }
            WriteTextFile(appDirectoryPath, "x64/user.dll", "user-x64");
            WriteTextFile(appDirectoryPath, "config/user.config", "user-config");

            WriteTextFile(packageSourceDirectoryPath, "BeMusicSeeker.exe", "new-app");
            CopyRestartExecutable(packageSourceDirectoryPath);
            WriteTextFile(packageSourceDirectoryPath, "BeMusicSeeker.Updater.exe", "new-updater");
            WriteTextFile(packageSourceDirectoryPath, "SevenZipExtractor.dll", "new-sevenzip");
            WriteTextFile(packageSourceDirectoryPath, "OggVorbis.NET64.dll", "new-ogv");
            WriteTextFile(packageSourceDirectoryPath, "libs/x64/7z.dll", "new-7z-native");
            WriteTextFile(packageSourceDirectoryPath, "e_sqlite3.dll", "new-e-sqlite3");
            WriteTextFile(packageSourceDirectoryPath, "native/EverythingBridge_x64.dll", "new-bridge");
            WriteTextFile(packageSourceDirectoryPath, "lang/ja-JP.json", "{}");
            WriteTextFile(packageSourceDirectoryPath, "update-managed-files.txt", string.Join(Environment.NewLine, new[]
            {
                "BeMusicSeeker.exe",
                "restart.json",
                "restart.exe",
                "BeMusicSeeker.Updater.exe",
                "SevenZipExtractor.dll",
                "OggVorbis.NET64.dll",
                "libs/x64/7z.dll",
                "e_sqlite3.dll",
                "native/EverythingBridge_x64.dll",
                "lang/ja-JP.json"
            }));
            ZipFile.CreateFromDirectory(packageSourceDirectoryPath, packagePath);

            RunUpdater(appDirectoryPath, packagePath, backupDirectoryPath);

            Assert.AreEqual("new-app", File.ReadAllText(Path.Combine(appDirectoryPath, "BeMusicSeeker.exe")));
            Assert.AreEqual("new-ogv", File.ReadAllText(Path.Combine(appDirectoryPath, "OggVorbis.NET64.dll")));
            Assert.AreEqual("new-7z-native", File.ReadAllText(Path.Combine(appDirectoryPath, "libs", "x64", "7z.dll")));
            Assert.AreEqual("new-e-sqlite3", File.ReadAllText(Path.Combine(appDirectoryPath, "e_sqlite3.dll")));
            Assert.IsFalse(File.Exists(Path.Combine(appDirectoryPath, "runtimes", "win-x64", "native", "e_sqlite3.dll")));
            Assert.IsFalse(File.Exists(Path.Combine(appDirectoryPath, "x64", "sqlite3.dll")));
            Assert.AreEqual("user-x64", File.ReadAllText(Path.Combine(appDirectoryPath, "x64", "user.dll")));
            Assert.IsFalse(File.Exists(Path.Combine(appDirectoryPath, "libs", "SevenZipExtractor.dll")));
            Assert.IsFalse(File.Exists(Path.Combine(appDirectoryPath, "libs", "x64", "OggVorbis.NET64.dll")));
            Assert.IsFalse(File.Exists(Path.Combine(appDirectoryPath, "libs", "x64", "sqlite3.dll")));
            Assert.IsFalse(File.Exists(Path.Combine(appDirectoryPath, "BeMusicSeeker.exe.config")));
            foreach (string legacyManagedLibraryName in new[]
            {
                "Bass.Net.dll",
                "DynamicJson.dll",
                "IniLibrary.dll",
                "Livet.dll",
                "Livet.Extensions.dll",
                "MetroRadiance.Chrome.dll",
                "MetroRadiance.Core.dll",
                "MetroRadiance.dll",
                "Microsoft.Expression.Drawing.dll",
                "Microsoft.Expression.Effects.dll",
                "Microsoft.Expression.Interactions.dll",
                "Microsoft.WindowsAPICodePack.dll",
                "Microsoft.WindowsAPICodePack.Shell.dll",
                "Newtonsoft.Json.dll",
                "NLog.Database.dll",
                "NLog.dll",
                "NLog.WindowsEventLog.dll",
                "QuickConverter.dll",
                "SgmlReaderDll.dll",
                "System.Collections.Immutable.dll",
                "System.Resources.Extensions.dll",
                "System.Memory.dll",
                "System.Buffers.dll",
                "System.Numerics.Vectors.dll",
                "System.Runtime.CompilerServices.Unsafe.dll",
                "System.Windows.Interactivity.dll",
                "sqlite.net.dll"
            })
            {
                Assert.IsFalse(File.Exists(Path.Combine(appDirectoryPath, "libs", legacyManagedLibraryName)));
            }
            foreach (string legacyRootX64File in new[]
            {
                "OggVorbis.NET.dll",
                "x64/OggVorbis.NET64.dll",
                "x64/7z.dll",
                "x64/bass.dll",
                "x64/bass_fx.dll",
                "x64/bassasio.dll",
                "x64/bassenc.dll",
                "x64/bassmix.dll",
                "x64/basswasapi.dll"
            })
            {
                Assert.IsFalse(File.Exists(Path.Combine(appDirectoryPath, legacyRootX64File.Replace('/', Path.DirectorySeparatorChar))));
            }
            Assert.AreEqual("user-libs-x86", File.ReadAllText(Path.Combine(appDirectoryPath, "libs", "x86", "user.dll")));
            Assert.AreEqual("user-root-x86", File.ReadAllText(Path.Combine(appDirectoryPath, "x86", "user.dll")));
            Assert.AreEqual("user-config", File.ReadAllText(Path.Combine(appDirectoryPath, "config", "user.config")));

            string[] managedManifestLines = File.ReadAllLines(Path.Combine(appDirectoryPath, "update-managed-files.txt"));
            CollectionAssert.Contains(managedManifestLines, "OggVorbis.NET64.dll");
            CollectionAssert.Contains(managedManifestLines, "libs\\x64\\7z.dll");
            CollectionAssert.DoesNotContain(managedManifestLines, "libs\\x64\\OggVorbis.NET64.dll");
        });
    }

    /// <summary>
    /// メタデータを含まない更新では旧アプリ管理archive・DB・取込履歴を整理し、新版と古いメタデータを混在させる誤りを検出する。
    /// </summary>
    /// <remarks>独立Jobと固有パスを所有し、共有registryはこのアプリ領域の登録だけを回収する。</remarks>
    [TestMethod]
    public void ApplyUpdate_RemovesAppManagedMetadataArtifactsWhenPackageHasNoMetadata()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string appDirectoryPath = Path.Combine(tempDirectoryPath, "app");
            string packageSourceDirectoryPath = Path.Combine(tempDirectoryPath, "package-source");
            string packagePath = Path.Combine(tempDirectoryPath, "app", "update_work", "downloads", "package.zip");
            string backupDirectoryPath = Path.Combine(appDirectoryPath, "update_backup");

            WriteTextFile(appDirectoryPath, "BeMusicSeeker.exe", "old-app");
            CopyRestartExecutable(appDirectoryPath);
            WriteTextFile(appDirectoryPath, "chart-info-metadata.7z", "old-root-archive");
            WriteTextFile(appDirectoryPath, "chart-info-metadata.db", "old-root-db");
            WriteTextFile(appDirectoryPath, "imported_metadata/chart-info-metadata.7z", "old-imported-archive");
            WriteTextFile(appDirectoryPath, "imported_metadata/chart-info-metadata.aaaaaaaaaaaa.7z", "old-history-archive");

            WriteTextFile(packageSourceDirectoryPath, "BeMusicSeeker.exe", "new-app");
            CopyRestartExecutable(packageSourceDirectoryPath);
            WriteTextFile(packageSourceDirectoryPath, "update-managed-files.txt", string.Join(Environment.NewLine, new[]
            {
                "BeMusicSeeker.exe",
                "restart.json",
                "restart.exe"
            }));
            ZipFile.CreateFromDirectory(packageSourceDirectoryPath, packagePath);

            RunUpdater(appDirectoryPath, packagePath, backupDirectoryPath);

            Assert.AreEqual("new-app", File.ReadAllText(Path.Combine(appDirectoryPath, "BeMusicSeeker.exe")));
            Assert.IsFalse(File.Exists(Path.Combine(appDirectoryPath, "chart-info-metadata.7z")));
            Assert.IsFalse(File.Exists(Path.Combine(appDirectoryPath, "chart-info-metadata.db")));
            Assert.IsFalse(Directory.Exists(Path.Combine(appDirectoryPath, "imported_metadata")));
        });
    }

    /// <summary>
    /// 同梱版更新は新しいmetadata archiveへ置換して旧DB・取込履歴を整理し、古い解析情報を残す誤りを検出する。
    /// </summary>
    /// <remarks>独立Jobと固有パスを所有し、共有registryはこのアプリ領域の登録だけを回収する。</remarks>
    [TestMethod]
    public void ApplyUpdate_ReplacesAppManagedMetadataArtifactsWithBundledArchive()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string appDirectoryPath = Path.Combine(tempDirectoryPath, "app");
            string packageSourceDirectoryPath = Path.Combine(tempDirectoryPath, "package-source");
            string packagePath = Path.Combine(tempDirectoryPath, "app", "update_work", "downloads", "package.zip");
            string backupDirectoryPath = Path.Combine(appDirectoryPath, "update_backup");

            WriteTextFile(appDirectoryPath, "BeMusicSeeker.exe", "old-app");
            CopyRestartExecutable(appDirectoryPath);
            WriteTextFile(appDirectoryPath, "chart-info-metadata.7z", "old-root-archive");
            WriteTextFile(appDirectoryPath, "chart-info-metadata.db", "old-root-db");
            WriteTextFile(appDirectoryPath, "imported_metadata/chart-info-metadata.7z", "old-imported-archive");
            WriteTextFile(appDirectoryPath, "imported_metadata/chart-info-metadata.bbbbbbbbbbbb.7z", "old-history-archive");

            WriteTextFile(packageSourceDirectoryPath, "BeMusicSeeker.exe", "new-app");
            CopyRestartExecutable(packageSourceDirectoryPath);
            WriteTextFile(packageSourceDirectoryPath, "chart-info-metadata.7z", "new-bundled-archive");
            WriteTextFile(packageSourceDirectoryPath, "update-managed-files.txt", string.Join(Environment.NewLine, new[]
            {
                "BeMusicSeeker.exe",
                "restart.json",
                "restart.exe",
                "chart-info-metadata.7z"
            }));
            ZipFile.CreateFromDirectory(packageSourceDirectoryPath, packagePath);

            RunUpdater(appDirectoryPath, packagePath, backupDirectoryPath);

            Assert.AreEqual("new-app", File.ReadAllText(Path.Combine(appDirectoryPath, "BeMusicSeeker.exe")));
            Assert.AreEqual("new-bundled-archive", File.ReadAllText(Path.Combine(appDirectoryPath, "chart-info-metadata.7z")));
            Assert.IsFalse(File.Exists(Path.Combine(appDirectoryPath, "chart-info-metadata.db")));
            Assert.IsFalse(Directory.Exists(Path.Combine(appDirectoryPath, "imported_metadata")));
        });
    }

    /// <summary>
    /// ドライブルート配置で適用後の実再起動callbackが失敗すると旧metadataと本体・利用者ツリーを復元し、元失敗を残して新版を誤確定する誤りを検出する。
    /// </summary>
    /// <remarks>実ファイルの確定・復元と元の再起動失敗を観測するため、managed buildの既存callback境界を使用する。ほかのツール起動ケースが実配布プロセスを分担する。</remarks>
    [TestMethod]
    public void ApplyUpdate_RollbackRestoresAppManagedMetadataArtifactsAfterFailure()
    {
        WithTemporaryDirectory((tempDirectoryPath, appDirectoryPath) =>
        {
            string packageSourceDirectoryPath = Path.Combine(tempDirectoryPath, "package-source");
            string packagePath = Path.Combine(appDirectoryPath, "update_work", "downloads", "package.zip");
            string backupDirectoryPath = Path.Combine(appDirectoryPath, "update_backup");

            WriteTextFile(appDirectoryPath, "BeMusicSeeker.exe", "old-app");
            CopyRestartExecutable(appDirectoryPath);
            WriteTextFile(appDirectoryPath, "chart-info-metadata.7z", "old-root-archive");
            WriteTextFile(appDirectoryPath, "chart-info-metadata.db", "old-root-db");
            WriteTextFile(appDirectoryPath, "imported_metadata/chart-info-metadata.7z", "old-imported-archive");
            WriteTextFile(appDirectoryPath, "data/settings.db", "preserved-data");
            WriteTextFile(appDirectoryPath, "config/user.config", "preserved-config");
            WriteTextFile(appDirectoryPath, "unmanaged.txt", "user-content");
            string canonicalBefore = ComputeCanonicalTreeHash(appDirectoryPath);
            string preservedBefore = ComputePreservedTreeHash(appDirectoryPath);

            WriteTextFile(packageSourceDirectoryPath, "BeMusicSeeker.exe", "new-app");
            CopyRestartExecutable(packageSourceDirectoryPath);
            WriteTextFile(packageSourceDirectoryPath, "update-managed-files.txt", string.Join(Environment.NewLine, new[]
            {
                "BeMusicSeeker.exe",
                "restart.json",
                "restart.exe"
            }));
            ZipFile.CreateFromDirectory(packageSourceDirectoryPath, packagePath);

            ApplyUpdateExpectRestartFailure(appDirectoryPath, packagePath, backupDirectoryPath);

            Assert.AreEqual(canonicalBefore, ComputeCanonicalTreeHash(appDirectoryPath));
            Assert.AreEqual(preservedBefore, ComputePreservedTreeHash(appDirectoryPath));
            Assert.AreEqual("old-app", File.ReadAllText(Path.Combine(appDirectoryPath, "BeMusicSeeker.exe")));
            Assert.AreEqual("old-root-archive", File.ReadAllText(Path.Combine(appDirectoryPath, "chart-info-metadata.7z")));
            Assert.AreEqual("old-root-db", File.ReadAllText(Path.Combine(appDirectoryPath, "chart-info-metadata.db")));
            Assert.AreEqual("old-imported-archive", File.ReadAllText(Path.Combine(appDirectoryPath, "imported_metadata", "chart-info-metadata.7z")));
            Assert.IsFalse(File.Exists(packagePath));
            Assert.IsFalse(Directory.Exists(Path.Combine(appDirectoryPath, "update_work", "extracted")));
            Assert.IsFalse(Directory.Exists(backupDirectoryPath), "A successful rollback must retire the previous-generation backup.");
            Assert.IsFalse(
                File.Exists(Path.Combine(appDirectoryPath, "update_work", "update-transaction.json")),
                "A successful rollback must retire the transaction journal without committing the update.");
            Assert.IsTrue(File.Exists(Path.Combine(appDirectoryPath, "update_work", "update-failure.txt")));
        }, useDriveRoot: true);
    }

    /// <summary>
    /// backup先が専用の子領域でない更新要求は本体変更前に拒否し、本体を退避先として扱う誤りを検出する。
    /// </summary>
    /// <remarks>独立Jobと固有パスを所有し、共有registryはこのアプリ領域の登録だけを回収する。</remarks>
    [TestMethod]
    public void ApplyUpdate_RejectsBackupDirectoryOutsideDedicatedChildWithoutMutation()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string appDirectoryPath = Path.Combine(tempDirectoryPath, "app");
            string packageSourceDirectoryPath = Path.Combine(tempDirectoryPath, "package-source");
            string packagePath = Path.Combine(tempDirectoryPath, "app", "update_work", "downloads", "package.zip");

            WriteTextFile(appDirectoryPath, "BeMusicSeeker.exe", "old-app");
            CopyRestartExecutable(appDirectoryPath);
            WriteTextFile(packageSourceDirectoryPath, "BeMusicSeeker.exe", "new-app");
            CopyRestartExecutable(packageSourceDirectoryPath);
            WriteTextFile(packageSourceDirectoryPath, "update-managed-files.txt", string.Join(Environment.NewLine, new[]
            {
                "BeMusicSeeker.exe",
                "restart.json",
                "restart.exe"
            }));
            ZipFile.CreateFromDirectory(packageSourceDirectoryPath, packagePath);

            RunUpdaterExpectFailure(appDirectoryPath, packagePath, appDirectoryPath);

            Assert.AreEqual("old-app", File.ReadAllText(Path.Combine(appDirectoryPath, "BeMusicSeeker.exe")));
            Assert.IsTrue(File.Exists(packagePath));
        });
    }

    /// <summary>
    /// 再起動先が存在しない更新要求は本体変更前に拒否し、再起動不能な配布を適用する誤りを検出する。
    /// </summary>
    /// <remarks>独立Jobと固有パスを所有し、共有registryはこのアプリ領域の登録だけを回収する。</remarks>
    [TestMethod]
    public void ApplyUpdate_RejectsMissingRestartTargetBeforeMutation()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string appDirectoryPath = Path.Combine(tempDirectoryPath, "app");
            string packageSourceDirectoryPath = Path.Combine(tempDirectoryPath, "package-source");
            string packagePath = Path.Combine(tempDirectoryPath, "app", "update_work", "downloads", "package.zip");
            string backupDirectoryPath = Path.Combine(appDirectoryPath, "update_backup");
            string missingRestartPath = Path.Combine(appDirectoryPath, "missing.exe");

            WriteTextFile(appDirectoryPath, "BeMusicSeeker.exe", "old-app");
            WriteTextFile(packageSourceDirectoryPath, "BeMusicSeeker.exe", "new-app");
            WriteTextFile(packageSourceDirectoryPath, "update-managed-files.txt", "BeMusicSeeker.exe");
            ZipFile.CreateFromDirectory(packageSourceDirectoryPath, packagePath);

            RunUpdaterExpectFailure(appDirectoryPath, packagePath, backupDirectoryPath, missingRestartPath);

            Assert.AreEqual("old-app", File.ReadAllText(Path.Combine(appDirectoryPath, "BeMusicSeeker.exe")));
            Assert.IsTrue(File.Exists(packagePath));
        });
    }

    /// <summary>
    /// 新packageの管理対象に再起動先がないと旧本体・manifest・ZIPを保って拒否し、起動先のない更新を始める誤りを検出する。
    /// </summary>
    /// <remarks>独立Jobと固有パスを所有し、共有registryはこのアプリ領域の登録だけを回収する。</remarks>
    [TestMethod]
    public void ApplyUpdate_RejectsPackageWithoutManagedRestartTargetBeforeMutation()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string appDirectoryPath = Path.Combine(tempDirectoryPath, "app");
            string packageSourceDirectoryPath = Path.Combine(tempDirectoryPath, "package-source");
            string packagePath = Path.Combine(tempDirectoryPath, "app", "update_work", "downloads", "package.zip");
            string backupDirectoryPath = Path.Combine(appDirectoryPath, "update_backup");
            string restartExecutablePath = Path.Combine(appDirectoryPath, "BeMusicSeeker.exe");

            WriteTextFile(appDirectoryPath, "BeMusicSeeker.exe", "old-app");
            WriteTextFile(appDirectoryPath, "update-managed-files.txt", "BeMusicSeeker.exe");
            WriteTextFile(packageSourceDirectoryPath, "replacement.txt", "new-file");
            WriteTextFile(packageSourceDirectoryPath, "update-managed-files.txt", "replacement.txt");
            ZipFile.CreateFromDirectory(packageSourceDirectoryPath, packagePath);

            RunUpdaterExpectFailure(appDirectoryPath, packagePath, backupDirectoryPath, restartExecutablePath);

            Assert.AreEqual("old-app", File.ReadAllText(restartExecutablePath));
            Assert.AreEqual("BeMusicSeeker.exe", File.ReadAllText(Path.Combine(appDirectoryPath, "update-managed-files.txt")));
            Assert.IsTrue(File.Exists(packagePath));
            Assert.IsFalse(Directory.Exists(backupDirectoryPath));
        });
    }

    /// <summary>
    /// 正でないアプリPIDの要求は旧本体とZIPを保ってbackup作成前に拒否し、プロセス同一性なしに変更を始める誤りを検出する。
    /// </summary>
    /// <remarks>独立Jobと固有パスを所有し、共有registryはこのアプリ領域の登録だけを回収する。</remarks>
    [TestMethod]
    public void ApplyUpdate_RejectsInvalidProcessIdBeforeMutation()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string appDirectoryPath = Path.Combine(tempDirectoryPath, "app");
            string packageSourceDirectoryPath = Path.Combine(tempDirectoryPath, "package-source");
            string packagePath = Path.Combine(tempDirectoryPath, "app", "update_work", "downloads", "package.zip");
            string backupDirectoryPath = Path.Combine(appDirectoryPath, "update_backup");

            WriteTextFile(appDirectoryPath, "BeMusicSeeker.exe", "old-app");
            WriteTextFile(packageSourceDirectoryPath, "BeMusicSeeker.exe", "new-app");
            WriteTextFile(packageSourceDirectoryPath, "update-managed-files.txt", "BeMusicSeeker.exe");
            ZipFile.CreateFromDirectory(packageSourceDirectoryPath, packagePath);

            RunUpdaterExpectFailure(appDirectoryPath, packagePath, backupDirectoryPath, processId: "0");

            Assert.AreEqual("old-app", File.ReadAllText(Path.Combine(appDirectoryPath, "BeMusicSeeker.exe")));
            Assert.IsTrue(File.Exists(packagePath));
            Assert.IsFalse(Directory.Exists(backupDirectoryPath));
        });
    }

    /// <summary>
    /// downloads外のpackageは旧本体とZIPを保ってbackup作成前に拒否し、更新領域外のファイルを消費する誤りを検出する。
    /// </summary>
    /// <remarks>独立Jobと固有パスを所有し、共有registryはこのアプリ領域の登録だけを回収する。</remarks>
    [TestMethod]
    public void ApplyUpdate_RejectsPackageOutsideDownloadsBeforeMutation()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string appDirectoryPath = Path.Combine(tempDirectoryPath, "app");
            string packageSourceDirectoryPath = Path.Combine(tempDirectoryPath, "package-source");
            string packagePath = Path.Combine(tempDirectoryPath, "outside.zip");
            string backupDirectoryPath = Path.Combine(appDirectoryPath, "update_backup");

            WriteTextFile(appDirectoryPath, "BeMusicSeeker.exe", "old-app");
            CopyRestartExecutable(appDirectoryPath);
            WriteTextFile(packageSourceDirectoryPath, "BeMusicSeeker.exe", "new-app");
            CopyRestartExecutable(packageSourceDirectoryPath);
            WriteTextFile(packageSourceDirectoryPath, "update-managed-files.txt", string.Join(Environment.NewLine, new[]
            {
                "BeMusicSeeker.exe",
                "restart.json",
                "restart.exe"
            }));
            ZipFile.CreateFromDirectory(packageSourceDirectoryPath, packagePath);

            RunUpdaterExpectFailure(appDirectoryPath, packagePath, backupDirectoryPath);

            Assert.AreEqual("old-app", File.ReadAllText(Path.Combine(appDirectoryPath, "BeMusicSeeker.exe")));
            Assert.IsTrue(File.Exists(packagePath));
            Assert.IsFalse(Directory.Exists(backupDirectoryPath));
        });
    }

    /// <summary>
    /// 新版再起動後にZIP削除が失敗しても新版と実markerを維持し、確定後の後片付け失敗で旧版へ戻す誤りを検出する。
    /// </summary>
    /// <remarks>独立Jobと固有パスを所有し、共有registryはこのアプリ領域の登録だけを回収する。</remarks>
    [TestMethod]
    public void ApplyUpdate_DoesNotRollbackAfterRestartWhenCleanupFails()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string appDirectoryPath = Path.Combine(tempDirectoryPath, "app");
            string packageSourceDirectoryPath = Path.Combine(tempDirectoryPath, "package-source");
            string packagePath = Path.Combine(tempDirectoryPath, "app", "update_work", "downloads", "package.zip");
            string backupDirectoryPath = Path.Combine(appDirectoryPath, "update_backup");
            string restartExecutablePath = Path.Combine(appDirectoryPath, "restart.exe");
            string restartMarkerPath = Path.Combine(appDirectoryPath, "restart.marker");

            WriteTextFile(appDirectoryPath, "BeMusicSeeker.exe", "old-app");
            CopyRestartExecutable(appDirectoryPath, "old.marker", "old" + Environment.NewLine);
            WriteTextFile(appDirectoryPath, "update-managed-files.txt", string.Join(Environment.NewLine, new[]
            {
                "BeMusicSeeker.exe",
                "restart.exe",
                "restart.json"
            }));
            WriteTextFile(packageSourceDirectoryPath, "BeMusicSeeker.exe", "new-app");
            CopyRestartExecutable(packageSourceDirectoryPath, "restart.marker", "restarted" + Environment.NewLine);
            WriteTextFile(packageSourceDirectoryPath, "update-managed-files.txt", string.Join(Environment.NewLine, new[]
            {
                "BeMusicSeeker.exe",
                "restart.exe",
                "restart.json"
            }));
            ZipFile.CreateFromDirectory(packageSourceDirectoryPath, packagePath);

            using FileStream packageLock = new(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            RunUpdater(appDirectoryPath, packagePath, backupDirectoryPath, restartExecutablePath);

            AssertRestartFileExists(restartMarkerPath);
            Assert.AreEqual("new-app", File.ReadAllText(Path.Combine(appDirectoryPath, "BeMusicSeeker.exe")));
            Assert.IsTrue(File.Exists(packagePath), "The package remains available when post-restart cleanup cannot delete it.");
            Assert.IsTrue(File.Exists(restartMarkerPath), "The updated restart target must be started before cleanup.");
            Assert.IsFalse(Directory.Exists(Path.Combine(appDirectoryPath, "update_work", "extracted")));
            using var journal = JsonDocument.Parse(File.ReadAllText(Path.Combine(appDirectoryPath, "update_work", "update-transaction.json")));
            Assert.AreEqual("committed", journal.RootElement.GetProperty("Phase").GetString());
        });
    }

    /// <summary>ZIP削除と任意stderr診断が失敗しても展開先を再起動前に回収し、実起動失敗の原因と旧版への復元を保持します。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [DoNotParallelize]
    public void ApplyUpdate_TemporaryCleanupAndDiagnosticFailurePreserveRestartFailure(bool diagnosticFails)
    {
        WithTemporaryDirectory(tempDirectoryPath =>
        {
            (string appDirectory, string packagePath, string backupDirectory) = PrepareCleanupFailurePackage(tempDirectoryPath);
            (Type program, object request) = PrepareManagedUpdate(appDirectory, packagePath, backupDirectory);
            string extractDirectory = Path.Combine(appDirectory, "update_work", "extracted");
            string? observedRestartPath = null;
            string? observedAppliedContents = null;
            bool extractRemovedBeforeRestart = false;
            bool packageRetainedBeforeRestart = false;
            Func<ProcessStartInfo, Process> startApplication = startInfo =>
            {
                observedRestartPath = startInfo.FileName;
                observedAppliedContents = File.ReadAllText(Path.Combine(appDirectory, "BeMusicSeeker.exe"));
                extractRemovedBeforeRestart = !Directory.Exists(extractDirectory);
                packageRetainedBeforeRestart = File.Exists(packagePath);
                return ThrowRestartLaunchFailure();
            };
            using FileStream packageLock = new(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            TextWriter originalError = Console.Error;
            var diagnosticWriter = new ThrowingDiagnosticWriter();
            TargetInvocationException failure;
            try
            {
                if (diagnosticFails)
                {
                    Console.SetError(diagnosticWriter);
                }
                failure = Assert.ThrowsException<TargetInvocationException>(() =>
                    program.GetMethod("ApplyUpdate", BindingFlags.NonPublic | BindingFlags.Static)!
                        .Invoke(null, [request, startApplication]));
            }
            finally
            {
                Console.SetError(originalError);
            }

            Assert.AreEqual(Path.Combine(appDirectory, "restart.exe"), observedRestartPath);
            Assert.AreEqual("new-app", observedAppliedContents);
            Assert.IsTrue(extractRemovedBeforeRestart, "The second cleanup must run before attempting restart even when ZIP deletion fails.");
            Assert.IsTrue(packageRetainedBeforeRestart);
            Assert.IsInstanceOfType<Win32Exception>(failure.InnerException);
            Assert.AreEqual("Injected restart launch failure.", failure.InnerException.Message);
            StringAssert.Contains(failure.InnerException.StackTrace, nameof(ThrowRestartLaunchFailure));
            Assert.AreEqual("old-app", File.ReadAllText(Path.Combine(appDirectory, "BeMusicSeeker.exe")));
            Assert.IsFalse(Directory.Exists(extractDirectory));
            Assert.IsTrue(File.Exists(packagePath));
            if (diagnosticFails)
            {
                Assert.IsTrue(diagnosticWriter.WriteAttempts > 0);
            }
        });
    }

    /// <summary>実再起動後の診断故障や監視者の起動でも新版と回復資料を保ち、次回の明示回復だけが残留一時物を退役します。</summary>
    /// <remarks>実再起動のPIDを持つ確定済み・確定保存直前の両journalを使い、全processとアプリ固有登録を所有scopeで回収します。</remarks>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [DoNotParallelize]
    public void ApplyUpdate_PostRestartDiagnosticFailureKeepsCommittedApplication(bool restarting)
    {
        WithTemporaryDirectory(tempDirectoryPath =>
        {
            (string appDirectory, string packagePath, string backupDirectory) = PrepareCleanupFailurePackage(tempDirectoryPath);
            (Type program, object request) = PrepareManagedUpdate(appDirectory, packagePath, backupDirectory);
            string extractDirectory = Path.Combine(appDirectory, "update_work", "extracted");
            bool extractRemovedBeforeRestart = false;
            bool packageRetainedBeforeRestart = false;
            int diagnosticAttemptsBeforeRestart = 0;
            var diagnosticWriter = new ThrowingDiagnosticWriter();
            using FileStream packageLock = new(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            string? observedRestartPath = null;
            OwnedProcess? restartedProcess = null;
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
                restartedProcess = StartOwnedProcess(startInfo, useExecutionGate: true);
                restartedProcessId = restartedProcess.BorrowedProcess.Id;
                return restartedProcess.BorrowedProcess;
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
            restartedProcess.WaitForExitAndChildren();
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
            RunUpdaterWatchdogRecovery(appDirectory);
            Assert.AreEqual("new-app", File.ReadAllText(Path.Combine(appDirectory, "BeMusicSeeker.exe")));
            Assert.IsTrue(File.Exists(packagePath));
            Assert.IsTrue(Directory.Exists(backupDirectory));
            using var retainedJournal = JsonDocument.Parse(File.ReadAllText(journalPath));
            Assert.AreEqual("committed", retainedJournal.RootElement.GetProperty("Phase").GetString());
            Assert.AreEqual(1, GetRecoveryRunOnceValues(appDirectory).Count);
            Assert.AreEqual(1, GetRecoverySupervisorValues(appDirectory).Count);
            Assert.IsFalse(File.Exists(Path.Combine(appDirectory, "update_work", "update-failure.txt")));

            RunUpdaterRecovery(appDirectory);
            Assert.IsFalse(File.Exists(packagePath));
            Assert.IsFalse(Directory.Exists(extractDirectory));
            Assert.IsFalse(Directory.Exists(backupDirectory));
            Assert.IsFalse(File.Exists(journalPath));
            Assert.AreEqual(0, GetRecoveryRunOnceValues(appDirectory).Count);
            Assert.AreEqual(0, GetRecoverySupervisorValues(appDirectory).Count);
            Assert.AreEqual("new-app", File.ReadAllText(Path.Combine(appDirectory, "BeMusicSeeker.exe")));

        });
    }

    /// <summary>一時物回収の実障害を再起動前後で観測する小さな実packageを作成します。</summary>
    private static (string AppDirectory, string PackagePath, string BackupDirectory) PrepareCleanupFailurePackage(string tempDirectory)
    {
        string appDirectory = Path.Combine(tempDirectory, "app");
        string sourceDirectory = Path.Combine(tempDirectory, "package-source");
        string packagePath = Path.Combine(appDirectory, "update_work", "downloads", "package.zip");
        string backupDirectory = Path.Combine(appDirectory, "update_backup");
        string managedPaths = string.Join(Environment.NewLine, new[] { "BeMusicSeeker.exe", "restart.exe", "restart.json" });
        WriteTextFile(appDirectory, "BeMusicSeeker.exe", "old-app");
        CopyRestartExecutable(appDirectory, "old.marker", "old");
        WriteTextFile(appDirectory, "update-managed-files.txt", managedPaths);
        WriteTextFile(sourceDirectory, "BeMusicSeeker.exe", "new-app");
        CopyRestartExecutable(sourceDirectory, "restart.marker", "restarted");
        WriteTextFile(sourceDirectory, "update-managed-files.txt", managedPaths);
        ZipFile.CreateFromDirectory(sourceDirectory, packagePath);
        return (appDirectory, packagePath, backupDirectory);
    }

    private static Process ThrowRestartLaunchFailure()
    {
        throw new Win32Exception(5, "Injected restart launch failure.");
    }

    /// <summary>診断先だけを故障させ、更新や復元の実処理は置換しません。</summary>
    private sealed class ThrowingDiagnosticWriter : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;

        internal int WriteAttempts { get; private set; }

        public override void WriteLine(string? value)
        {
            WriteAttempts++;
            throw new IOException("Injected stderr failure.");
        }
    }

    /// <summary>
    /// ファイルからディレクトリへ置換した後の再起動失敗では元ファイルと無関係な兄弟を復元し、種類変更の巻き戻し欠落を検出する。
    /// </summary>
    /// <remarks>実ファイルの確定・復元と元の再起動失敗を観測するため、managed buildの既存callback境界を使用する。ほかのツール起動ケースが実配布プロセスを分担する。</remarks>
    [TestMethod]
    public void ApplyUpdate_RollbackRestoresFileDirectoryTransition()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string appDirectoryPath = Path.Combine(tempDirectoryPath, "app");
            string packageSourceDirectoryPath = Path.Combine(tempDirectoryPath, "package-source");
            string packagePath = Path.Combine(tempDirectoryPath, "app", "update_work", "downloads", "package.zip");
            string backupDirectoryPath = Path.Combine(appDirectoryPath, "update_backup");

            WriteTextFile(appDirectoryPath, "a", "old-file");
            WriteTextFile(appDirectoryPath, "z", "blocking-file");
            CopyRestartExecutable(appDirectoryPath);
            WriteTextFile(appDirectoryPath, "update-managed-files.txt", string.Join(Environment.NewLine, new[]
            {
                "a",
                "restart.json",
                "restart.exe"
            }));
            WriteTextFile(packageSourceDirectoryPath, "a/b/c.dll", "new-child");
            CopyRestartExecutable(packageSourceDirectoryPath);
            WriteTextFile(packageSourceDirectoryPath, "update-managed-files.txt", string.Join(Environment.NewLine, new[]
            {
                "a/b/c.dll",
                "restart.json",
                "restart.exe"
            }));
            ZipFile.CreateFromDirectory(packageSourceDirectoryPath, packagePath);

            ApplyUpdateExpectRestartFailure(appDirectoryPath, packagePath, backupDirectoryPath);

            Assert.AreEqual("old-file", File.ReadAllText(Path.Combine(appDirectoryPath, "a")));
            Assert.IsTrue(File.Exists(Path.Combine(appDirectoryPath, "z")));
            Assert.IsFalse(Directory.Exists(Path.Combine(appDirectoryPath, "a")) && File.Exists(Path.Combine(appDirectoryPath, "a", "b", "c.dll")));
        });
    }

    /// <summary>
    /// 管理された子ファイルだけのディレクトリを新版ファイルへ置換し、旧子ファイルを残して適用できなくなる誤りを検出する。
    /// </summary>
    /// <remarks>独立Jobと固有パスを所有し、共有registryはこのアプリ領域の登録だけを回収する。</remarks>
    [TestMethod]
    public void ApplyUpdate_ReplacesManagedDirectoryWithFile()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string appDirectoryPath = Path.Combine(tempDirectoryPath, "app");
            string packageSourceDirectoryPath = Path.Combine(tempDirectoryPath, "package-source");
            string packagePath = Path.Combine(tempDirectoryPath, "app", "update_work", "downloads", "package.zip");
            string backupDirectoryPath = Path.Combine(appDirectoryPath, "update_backup");

            WriteTextFile(appDirectoryPath, "a/b/old.dll", "old-child");
            CopyRestartExecutable(appDirectoryPath);
            WriteTextFile(appDirectoryPath, "update-managed-files.txt", string.Join(Environment.NewLine, new[]
            {
                "a/b/old.dll",
                "restart.json",
                "restart.exe"
            }));
            WriteTextFile(packageSourceDirectoryPath, "a", "new-file");
            CopyRestartExecutable(packageSourceDirectoryPath);
            WriteTextFile(packageSourceDirectoryPath, "update-managed-files.txt", string.Join(Environment.NewLine, new[]
            {
                "a",
                "restart.json",
                "restart.exe"
            }));
            ZipFile.CreateFromDirectory(packageSourceDirectoryPath, packagePath);

            RunUpdater(appDirectoryPath, packagePath, backupDirectoryPath);

            Assert.IsTrue(File.Exists(Path.Combine(appDirectoryPath, "a")));
            Assert.AreEqual("new-file", File.ReadAllText(Path.Combine(appDirectoryPath, "a")));
            Assert.IsFalse(File.Exists(Path.Combine(appDirectoryPath, "a", "b", "old.dll")));
        });
    }

    /// <summary>
    /// 未管理の子を含むディレクトリのファイル化は変更前に拒否し、利用者の子・旧世代・ZIPと理由を保って過剰削除を検出する。
    /// </summary>
    /// <remarks>独立Jobと固有パスを所有し、共有registryはこのアプリ領域の登録だけを回収する。</remarks>
    [TestMethod]
    public void ApplyUpdate_RejectsDirectoryToFileTransitionWithUnmanagedChildBeforeMutation()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string appDirectoryPath = Path.Combine(tempDirectoryPath, "app");
            string packageSourceDirectoryPath = Path.Combine(tempDirectoryPath, "package-source");
            string packagePath = Path.Combine(tempDirectoryPath, "app", "update_work", "downloads", "package.zip");
            string backupDirectoryPath = Path.Combine(appDirectoryPath, "update_backup");

            WriteTextFile(appDirectoryPath, "a/old.dll", "old-child");
            WriteTextFile(appDirectoryPath, "a/user.dll", "user-child");
            CopyRestartExecutable(appDirectoryPath);
            WriteTextFile(appDirectoryPath, "update-managed-files.txt", string.Join(Environment.NewLine, new[]
            {
                "a/old.dll",
                "restart.json",
                "restart.exe"
            }));
            WriteTextFile(packageSourceDirectoryPath, "a", "new-file");
            CopyRestartExecutable(packageSourceDirectoryPath);
            WriteTextFile(packageSourceDirectoryPath, "update-managed-files.txt", string.Join(Environment.NewLine, new[]
            {
                "a",
                "restart.json",
                "restart.exe"
            }));
            ZipFile.CreateFromDirectory(packageSourceDirectoryPath, packagePath);
            WriteTextFile(backupDirectoryPath, "previous/keep.txt", "previous-generation");

            RunUpdaterExpectFailure(appDirectoryPath, packagePath, backupDirectoryPath);

            Assert.IsTrue(Directory.Exists(Path.Combine(appDirectoryPath, "a")));
            Assert.AreEqual("old-child", File.ReadAllText(Path.Combine(appDirectoryPath, "a", "old.dll")));
            Assert.AreEqual("user-child", File.ReadAllText(Path.Combine(appDirectoryPath, "a", "user.dll")));
            Assert.AreEqual("previous-generation", File.ReadAllText(Path.Combine(backupDirectoryPath, "previous", "keep.txt")));
            Assert.IsTrue(File.Exists(packagePath));
            string failureReceiptPath = Path.Combine(appDirectoryPath, "update_work", "update-failure.txt");
            Assert.IsTrue(File.Exists(failureReceiptPath));
            StringAssert.Contains(File.ReadAllText(failureReceiptPath), "unmanaged");
        });
    }

    /// <summary>
    /// manifest管理外の既存ファイルを上書きするpackageは変更前に拒否し、旧本体・利用者内容・manifest・ZIPを保って境界越えを検出する。
    /// </summary>
    /// <remarks>独立Jobと固有パスを所有し、共有registryはこのアプリ領域の登録だけを回収する。</remarks>
    [TestMethod]
    public void ApplyUpdate_RejectsReplacingUnmanagedFileWhenManifestExistsBeforeMutation()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string appDirectoryPath = Path.Combine(tempDirectoryPath, "app");
            string packageSourceDirectoryPath = Path.Combine(tempDirectoryPath, "package-source");
            string packagePath = Path.Combine(tempDirectoryPath, "app", "update_work", "downloads", "package.zip");
            string backupDirectoryPath = Path.Combine(appDirectoryPath, "update_backup");

            WriteTextFile(appDirectoryPath, "BeMusicSeeker.exe", "old-app");
            CopyRestartExecutable(appDirectoryPath);
            WriteTextFile(appDirectoryPath, "user-added.dll", "user-content");
            WriteTextFile(appDirectoryPath, "update-managed-files.txt", string.Join(Environment.NewLine, new[]
            {
                "BeMusicSeeker.exe",
                "restart.json",
                "restart.exe"
            }));
            WriteTextFile(packageSourceDirectoryPath, "BeMusicSeeker.exe", "new-app");
            CopyRestartExecutable(packageSourceDirectoryPath);
            WriteTextFile(packageSourceDirectoryPath, "user-added.dll", "package-content");
            WriteTextFile(packageSourceDirectoryPath, "update-managed-files.txt", string.Join(Environment.NewLine, new[]
            {
                "BeMusicSeeker.exe",
                "restart.json",
                "restart.exe",
                "user-added.dll"
            }));
            ZipFile.CreateFromDirectory(packageSourceDirectoryPath, packagePath);

            RunUpdaterExpectFailure(appDirectoryPath, packagePath, backupDirectoryPath);

            Assert.AreEqual("old-app", File.ReadAllText(Path.Combine(appDirectoryPath, "BeMusicSeeker.exe")));
            Assert.AreEqual("user-content", File.ReadAllText(Path.Combine(appDirectoryPath, "user-added.dll")));
            Assert.AreEqual(
                string.Join(Environment.NewLine, new[] { "BeMusicSeeker.exe", "restart.json", "restart.exe" }),
                File.ReadAllText(Path.Combine(appDirectoryPath, "update-managed-files.txt")));
            Assert.IsTrue(File.Exists(packagePath));
            Assert.IsFalse(Directory.Exists(backupDirectoryPath));
        });
    }

    /// <summary>
    /// 管理ファイルのパスが利用者の子を持つディレクトリへ変わっていれば拒否し、manifestだけを信頼した再帰削除を検出する。
    /// </summary>
    /// <remarks>独立Jobと固有パスを所有し、共有registryはこのアプリ領域の登録だけを回収する。</remarks>
    [TestMethod]
    public void ApplyUpdate_RejectsManagedFilePathThatBecameDirectoryWithUserChild()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string appDirectoryPath = Path.Combine(tempDirectoryPath, "app");
            string packageSourceDirectoryPath = Path.Combine(tempDirectoryPath, "package-source");
            string packagePath = Path.Combine(tempDirectoryPath, "app", "update_work", "downloads", "package.zip");
            string backupDirectoryPath = Path.Combine(appDirectoryPath, "update_backup");

            WriteTextFile(appDirectoryPath, "BeMusicSeeker.exe", "old-app");
            CopyRestartExecutable(appDirectoryPath);
            WriteTextFile(appDirectoryPath, "a/user.txt", "user-content");
            WriteTextFile(appDirectoryPath, "update-managed-files.txt", string.Join(Environment.NewLine, new[]
            {
                "BeMusicSeeker.exe",
                "a",
                "restart.json",
                "restart.exe"
            }));
            WriteTextFile(packageSourceDirectoryPath, "BeMusicSeeker.exe", "new-app");
            CopyRestartExecutable(packageSourceDirectoryPath);
            WriteTextFile(packageSourceDirectoryPath, "a/new.dll", "package-content");
            WriteTextFile(packageSourceDirectoryPath, "update-managed-files.txt", string.Join(Environment.NewLine, new[]
            {
                "BeMusicSeeker.exe",
                "a/new.dll",
                "restart.json",
                "restart.exe"
            }));
            ZipFile.CreateFromDirectory(packageSourceDirectoryPath, packagePath);

            RunUpdaterExpectFailure(appDirectoryPath, packagePath, backupDirectoryPath);

            Assert.AreEqual("user-content", File.ReadAllText(Path.Combine(appDirectoryPath, "a", "user.txt")));
            Assert.AreEqual(
                string.Join(Environment.NewLine, new[] { "BeMusicSeeker.exe", "a", "restart.json", "restart.exe" }),
                File.ReadAllText(Path.Combine(appDirectoryPath, "update-managed-files.txt")));
            Assert.IsTrue(File.Exists(packagePath));
            Assert.IsFalse(Directory.Exists(backupDirectoryPath));
        });
    }

    /// <summary>
    /// 共有ディレクトリの管理ファイル適用後に再起動が失敗しても未管理の兄弟を残して復元し、復元範囲の過剰な削除を検出する。
    /// </summary>
    /// <remarks>実ファイルの確定・復元と元の再起動失敗を観測するため、managed buildの既存callback境界を使用する。ほかのツール起動ケースが実配布プロセスを分担する。</remarks>
    [TestMethod]
    public void ApplyUpdate_RollbackPreservesUnmanagedSiblingInSharedDirectory()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string appDirectoryPath = Path.Combine(tempDirectoryPath, "app");
            string packageSourceDirectoryPath = Path.Combine(tempDirectoryPath, "package-source");
            string packagePath = Path.Combine(tempDirectoryPath, "app", "update_work", "downloads", "package.zip");
            string backupDirectoryPath = Path.Combine(appDirectoryPath, "update_backup");

            WriteTextFile(appDirectoryPath, "docs/a.txt", "old-managed");
            WriteTextFile(appDirectoryPath, "docs/user.txt", "user-file");
            WriteTextFile(appDirectoryPath, "zz", "blocking-file");
            CopyRestartExecutable(appDirectoryPath);
            WriteTextFile(appDirectoryPath, "update-managed-files.txt", string.Join(Environment.NewLine, new[]
            {
                "docs/a.txt",
                "restart.json",
                "restart.exe"
            }));
            WriteTextFile(packageSourceDirectoryPath, "docs/a.txt", "new-managed");
            CopyRestartExecutable(packageSourceDirectoryPath);
            WriteTextFile(packageSourceDirectoryPath, "update-managed-files.txt", string.Join(Environment.NewLine, new[]
            {
                "docs/a.txt",
                "restart.json",
                "restart.exe"
            }));
            ZipFile.CreateFromDirectory(packageSourceDirectoryPath, packagePath);

            ApplyUpdateExpectRestartFailure(appDirectoryPath, packagePath, backupDirectoryPath);

            Assert.AreEqual("old-managed", File.ReadAllText(Path.Combine(appDirectoryPath, "docs", "a.txt")));
            Assert.AreEqual("user-file", File.ReadAllText(Path.Combine(appDirectoryPath, "docs", "user.txt")));
            Assert.AreEqual("blocking-file", File.ReadAllText(Path.Combine(appDirectoryPath, "zz")));
        });
    }

    /// <summary>
    /// 管理パスの祖先がシンボリックリンクなら適用を拒否し、外部sentinel・旧本体・ZIPを保ってリンク先を変更する誤りを検出する。
    /// </summary>
    /// <remarks>独立Jobと固有パスを所有し、共有registryはこのアプリ領域の登録だけを回収する。</remarks>
    [TestMethod]
    public void ApplyUpdate_RejectsReparseAncestorBeforeMovingManagedPath()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string appDirectoryPath = Path.Combine(tempDirectoryPath, "app");
            string packageSourceDirectoryPath = Path.Combine(tempDirectoryPath, "package-source");
            string externalDirectoryPath = Path.Combine(tempDirectoryPath, "external");
            string packagePath = Path.Combine(tempDirectoryPath, "app", "update_work", "downloads", "package.zip");
            string backupDirectoryPath = Path.Combine(appDirectoryPath, "update_backup");

            WriteTextFile(appDirectoryPath, "BeMusicSeeker.exe", "old-app");
            CopyRestartExecutable(appDirectoryPath);
            WriteTextFile(appDirectoryPath, "update-managed-files.txt", string.Join(Environment.NewLine, new[]
            {
                "plugins/old.dll",
                "restart.json",
                "restart.exe"
            }));
            WriteTextFile(externalDirectoryPath, "old.dll", "external-sentinel");
            string linkPath = Path.Combine(appDirectoryPath, "plugins");
            try
            {
                Directory.CreateSymbolicLink(linkPath, externalDirectoryPath);
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException || exception is PlatformNotSupportedException)
            {
                Assert.Inconclusive("The test environment does not permit directory symbolic links: " + exception.Message);
                return;
            }

            WriteTextFile(packageSourceDirectoryPath, "BeMusicSeeker.exe", "new-app");
            CopyRestartExecutable(packageSourceDirectoryPath);
            WriteTextFile(packageSourceDirectoryPath, "update-managed-files.txt", string.Join(Environment.NewLine, new[]
            {
                "BeMusicSeeker.exe",
                "restart.json",
                "restart.exe"
            }));
            ZipFile.CreateFromDirectory(packageSourceDirectoryPath, packagePath);

            RunUpdaterExpectFailure(appDirectoryPath, packagePath, backupDirectoryPath);

            Assert.AreEqual("external-sentinel", File.ReadAllText(Path.Combine(externalDirectoryPath, "old.dll")));
            Assert.AreEqual("old-app", File.ReadAllText(Path.Combine(appDirectoryPath, "BeMusicSeeker.exe")));
            Assert.IsTrue(File.Exists(packagePath));
        });
    }

    /// <summary>managed更新境界へ実要求と本番準備済みjournalを渡します。</summary>
    private (Type Program, object Request) PrepareManagedUpdate(string appDirectoryPath, string packagePath, string backupDirectoryPath)
    {
        // この呼出し単位の再起動失敗境界はmanaged buildを使う。
        // 他のプロセス検証は明示した配布exe、または未指定時の通常buildのexeを使う。
        var assembly = Assembly.LoadFrom(FindBuiltUpdaterFile("BeMusicSeeker.Updater.dll"));
        Type program = assembly.GetType("BeMusicSeeker.Updater.Program", throwOnError: true)!;
        Type requestType = program.GetNestedType("UpdateRequest", BindingFlags.NonPublic)!;
        string restartPath = Path.Combine(appDirectoryPath, "restart.exe");
        string[] arguments =
        [
            "--app-dir", appDirectoryPath,
            "--package", packagePath,
            "--backup-dir", backupDirectoryPath,
            "--restart-exe", restartPath,
            "--pid", GetExitedProcessId().ToString(),
            "--ready-file", Path.Combine(appDirectoryPath, "update_work", "current", "updater-ready.txt"),
            "--decision-file", Path.Combine(appDirectoryPath, "update_work", "current", "updater-decision.txt")
        ];
        object request = requestType.GetMethod("Parse")!.Invoke(null, [arguments])!;
        // ケース所有のアプリ領域へ本番の準備処理を通し、journal生成を複製しない。
        program.GetMethod("WritePreparedTransactionJournal", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [request]);
        program.GetMethod("PrepareFullTransactionJournal", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [request]);

        return (program, request);
    }

    private void ApplyUpdateExpectRestartFailure(string appDirectoryPath, string packagePath, string backupDirectoryPath)
    {
        (Type program, object request) = PrepareManagedUpdate(appDirectoryPath, packagePath, backupDirectoryPath);
        string restartPath = Path.Combine(appDirectoryPath, "restart.exe");
        bool restartAttempted = false;
        var startFailure = new Win32Exception(5, "Injected restart launch failure.");
        Func<ProcessStartInfo, Process> startApplication = startInfo =>
        {
            Assert.AreEqual(restartPath, startInfo.FileName);
            restartAttempted = true;
            throw startFailure;
        };
        TargetInvocationException failure = Assert.ThrowsException<TargetInvocationException>(() =>
            program.GetMethod("ApplyUpdate", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [request, startApplication]));
        Assert.IsTrue(restartAttempted, "The test must reach restart after applying the package, not fail during preflight.");
        Assert.AreSame(startFailure, failure.InnerException, "Successful rollback must preserve the original launch failure.");
    }

    private void RunUpdater(string appDirectoryPath, string packagePath, string backupDirectoryPath, string? restartExecutablePath = null, string? processId = null)
    {
        OwnedProcess process = StartUpdater(appDirectoryPath, packagePath, backupDirectoryPath, restartExecutablePath, processId);
        process.WaitForExitAndChildren();

        string standardOutput = process.GetStandardOutput();
        string standardError = process.GetStandardError();
        if (process.ExitCode != 0)
        {
            Assert.Fail("Updater failed with exit code " + process.ExitCode + Environment.NewLine + standardOutput + Environment.NewLine + standardError);
        }
    }

    private void RunUpdaterRecovery(string appDirectoryPath, string? workingDirectory = null)
    {
        OwnedProcess process = StartUpdaterRecoveryProcess(appDirectoryPath, workingDirectory);
        process.WaitForExitAndChildren();

        if (process.ExitCode != 0)
        {
            Assert.Fail(
                "Updater recovery failed with exit code "
                + process.ExitCode
                + Environment.NewLine
                + process.GetStandardOutput()
                + Environment.NewLine
                + process.GetStandardError());
        }
    }

    private OwnedProcess StartUpdaterRecoveryProcess(string appDirectoryPath, string? workingDirectory = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = FindUpdaterExecutable(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(FindUpdaterExecutable()) ?? Environment.CurrentDirectory
        };
        startInfo.ArgumentList.Add("--recover");
        startInfo.ArgumentList.Add("--app-dir");
        startInfo.ArgumentList.Add(appDirectoryPath);
        return StartOwnedProcess(startInfo, useExecutionGate: true);
    }

    private void RunUpdaterWatchdogRecovery(string appDirectoryPath)
    {
        string updaterPath = FindUpdaterExecutable();
        var startInfo = new ProcessStartInfo
        {
            FileName = updaterPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(updaterPath) ?? Environment.CurrentDirectory
        };
        startInfo.ArgumentList.Add("--watch");
        startInfo.ArgumentList.Add("--app-dir");
        startInfo.ArgumentList.Add(appDirectoryPath);
        startInfo.ArgumentList.Add("--pid");
        startInfo.ArgumentList.Add(GetExitedProcessId().ToString());
        OwnedProcess process = StartOwnedProcess(startInfo, useExecutionGate: true);
        process.WaitForExitAndChildren();

        if (process.ExitCode != 0)
        {
            Assert.Fail(
                "Updater watchdog failed with exit code "
                + process.ExitCode
                + Environment.NewLine
                + process.GetStandardOutput()
                + Environment.NewLine
                + process.GetStandardError());
        }
    }

    private void RunUpdaterExpectFailure(string appDirectoryPath, string packagePath, string backupDirectoryPath, string? restartExecutablePath = null, string? processId = null)
    {
        OwnedProcess process = StartUpdater(appDirectoryPath, packagePath, backupDirectoryPath, restartExecutablePath, processId);
        process.WaitForExitAndChildren();

        string output = process.GetStandardOutput();
        string error = process.GetStandardError();

        if (process.ExitCode == 0)
        {
            Assert.Fail("Updater unexpectedly succeeded." + Environment.NewLine + output + Environment.NewLine + error);
        }
    }

    private OwnedProcess StartUpdater(string appDirectoryPath, string packagePath, string backupDirectoryPath, string? restartExecutablePath = null, string? processId = null, bool publishProceed = true)
    {
        restartExecutablePath ??= Path.Combine(appDirectoryPath, "restart.exe");
        processId ??= GetExitedProcessId().ToString();
        string readyFilePath = Path.Combine(appDirectoryPath, "update_work", "current", "updater-ready.txt");
        string decisionFilePath = Path.Combine(appDirectoryPath, "update_work", "current", "updater-decision.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(readyFilePath)!);
        if (File.Exists(readyFilePath))
        {
            File.Delete(readyFilePath);
        }
        if (File.Exists(decisionFilePath))
        {
            File.Delete(decisionFilePath);
        }

        OwnedProcess process = StartOwnedProcess(CreateUpdaterStartInfo(
            appDirectoryPath,
            packagePath,
            backupDirectoryPath,
            restartExecutablePath,
            processId),
            useExecutionGate: true);
        bool validProcessId = int.TryParse(processId, out int parsedProcessId) && parsedProcessId > 0;
        if (validProcessId && WaitForReadyOrProcessExit(process, readyFilePath) && publishProceed)
        {
            PublishUpdaterDecision(decisionFilePath, "proceed");
        }

        return process;
    }

    private OwnedProcess StartLiveApplicationFixture(string appDirectoryPath, string restartExecutablePath)
    {
        OwnedProcess process = StartOwnedProcess(new ProcessStartInfo
        {
            FileName = restartExecutablePath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = appDirectoryPath
        }, "BMS_TEST_FIXTURE_READY", useExecutionGate: false);
        process.WaitForOutputReady();
        return process;
    }

    private OwnedProcess StartOwnedProcess(
        ProcessStartInfo startInfo,
        string? readyOutputLine = null,
        bool useExecutionGate = false)
    {
        if (activeProcessScope is null)
        {
            throw new InvalidOperationException("The updater test process scope was not initialized.");
        }

        return activeProcessScope.Start(startInfo, readyOutputLine, useExecutionGate);
    }

    private static void PublishUpdaterDecision(string decisionFilePath, string decision)
    {
        string temporaryPath = decisionFilePath + ".tmp";
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(decision);
        using (FileStream stream = new(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporaryPath, decisionFilePath, overwrite: true);
    }

    private ProcessStartInfo CreateUpdaterStartInfo(
        string appDirectoryPath,
        string packagePath,
        string backupDirectoryPath,
        string? restartExecutablePath = null,
        string? processId = null)
    {
        string updaterPath = FindUpdaterExecutable();
        restartExecutablePath ??= Path.Combine(appDirectoryPath, "restart.exe");
        processId ??= GetExitedProcessId().ToString();
        string readyFilePath = Path.Combine(appDirectoryPath, "update_work", "current", "updater-ready.txt");
        string decisionFilePath = Path.Combine(appDirectoryPath, "update_work", "current", "updater-decision.txt");
        var startInfo = new ProcessStartInfo
        {
            FileName = updaterPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(updaterPath) ?? Environment.CurrentDirectory
        };
        startInfo.ArgumentList.Add("--app-dir");
        startInfo.ArgumentList.Add(appDirectoryPath);
        startInfo.ArgumentList.Add("--package");
        startInfo.ArgumentList.Add(packagePath);
        startInfo.ArgumentList.Add("--backup-dir");
        startInfo.ArgumentList.Add(backupDirectoryPath);
        startInfo.ArgumentList.Add("--ready-file");
        startInfo.ArgumentList.Add(readyFilePath);
        startInfo.ArgumentList.Add("--decision-file");
        startInfo.ArgumentList.Add(decisionFilePath);
        startInfo.ArgumentList.Add("--pid");
        startInfo.ArgumentList.Add(processId);
        startInfo.ArgumentList.Add("--restart-exe");
        startInfo.ArgumentList.Add(restartExecutablePath);
        return startInfo;
    }

    private int GetExitedProcessId()
    {
        OwnedProcess process = StartOwnedProcess(new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            Arguments = "/d /q /c \"set /p _bmsExit= >nul & exit /b 0\"",
            UseShellExecute = false,
            RedirectStandardInput = true,
            CreateNoWindow = true
        });
        int processId = process.Id;
        process.StandardInput.WriteLine("exit");
        process.WaitForExitAndChildren();

        return processId;
    }

    private static void AssertRestartFileExists(string filePath)
    {
        Assert.IsTrue(File.Exists(filePath), "Expected file was not created by the completed restart process: " + filePath);
    }

    private static bool WaitForReadyOrProcessExit(OwnedProcess process, string filePath)
    {
        while (!File.Exists(filePath))
        {
            if (process.HasExited)
            {
                return false;
            }

            Thread.Sleep(50);
        }

        return true;
    }

    private sealed class CaseProcessScope
    {
        private readonly List<OwnedProcess> processes = new();
        private readonly List<string> diagnostics = new();

        internal OwnedProcess Start(
            ProcessStartInfo startInfo,
            string? readyOutputLine,
            bool useExecutionGate)
        {
            var process = new OwnedProcess(
                startInfo,
                readyOutputLine,
                useExecutionGate);
            processes.Add(process);
            process.Start();
            return process;
        }

        internal void RecordDiagnostic(string diagnostic)
        {
            diagnostics.Add(diagnostic);
        }

        internal IReadOnlyList<string> DisposeAll()
        {
            var result = new List<string>(diagnostics);
            for (int index = processes.Count - 1; index >= 0; index--)
            {
                OwnedProcess process = processes[index];
                try
                {
                    process.Dispose();
                }
                catch (Exception exception)
                {
                    result.Add(
                        "Updater test process cleanup failed for "
                        + process.Description
                        + ": "
                        + exception.Message);
                }
            }

            processes.Clear();
            return result;
        }
    }

    private sealed class OwnedProcess
    {
        private const int JobObjectBasicAccountingInformationClass = 1;
        private const int JobObjectExtendedLimitInformationClass = 9;
        private const uint JobObjectLimitKillOnJobClose = 0x2000;

        private readonly Process process;
        private readonly TaskCompletionSource<bool>? readyNotification;
        private readonly bool useExecutionGate;
        private Task<string> standardOutput = Task.FromResult(string.Empty);
        private Task<string> standardError = Task.FromResult(string.Empty);
        private IntPtr jobHandle;
        private bool started;
        private bool assignedToJob;

        internal OwnedProcess(ProcessStartInfo startInfo, string? readyOutputLine, bool useExecutionGate)
        {
            process = new Process { StartInfo = useExecutionGate ? CreateExecutionGateStartInfo(startInfo) : startInfo };
            this.useExecutionGate = useExecutionGate;
            if (readyOutputLine is not null)
            {
                readyNotification = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        internal string Description => process.StartInfo.FileName;
        /// <summary>既存の開始callbackへ借用し、所有と回収はこのscopeに残す実プロセスです。</summary>
        internal Process BorrowedProcess => process;

        internal int Id => process.Id;
        internal bool HasExited => process.HasExited;
        internal int ExitCode => process.ExitCode;
        internal StreamWriter StandardInput => process.StandardInput;
        internal bool WaitForExit(int milliseconds) => process.WaitForExit(milliseconds);

        internal void Start()
        {
            jobHandle = CreateProcessJob();
            started = process.Start();
            if (!started)
            {
                throw new InvalidOperationException("The owned test process was not started.");
            }

            // 割当失敗の経路でも、開始済みの両パイプを回収できるよう直ちに読む。
            if (process.StartInfo.RedirectStandardOutput)
            {
                standardOutput = ReadOutputAsync();
            }
            if (process.StartInfo.RedirectStandardError)
            {
                standardError = process.StandardError.ReadToEndAsync();
            }
            if (!AssignProcessToJobObject(jobHandle, process.Handle))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "The test process could not be assigned to its Job.");
            }
            assignedToJob = true;
            if (useExecutionGate)
            {
                process.StandardInput.WriteLine("go");
                process.StandardInput.Flush();
            }
        }

        private async Task<string> ReadOutputAsync()
        {
            if (readyNotification is null)
            {
                return await process.StandardOutput.ReadToEndAsync();
            }

            // 入力待ちfixtureだけが準備通知を出す。EOFと読取り失敗も待機側へ渡す。
            try
            {
                string? firstLine = await process.StandardOutput.ReadLineAsync();
                if (firstLine?.Trim() != "BMS_TEST_FIXTURE_READY")
                {
                    throw new InvalidOperationException("The fixture exited or produced unexpected readiness output: " + firstLine);
                }
                readyNotification.SetResult(true);
                return firstLine + Environment.NewLine + await process.StandardOutput.ReadToEndAsync();
            }
            catch (Exception exception)
            {
                readyNotification.TrySetException(exception);
                throw;
            }
        }

        internal void WaitForOutputReady() => readyNotification!.Task.GetAwaiter().GetResult();

        internal void WaitForExitAndChildren()
        {
            process.WaitForExit();
            Task.WhenAll(standardOutput, standardError).GetAwaiter().GetResult();
            if (assignedToJob)
            {
                while (QueryActiveProcessCount(jobHandle) != 0)
                {
                    Thread.Sleep(20);
                }
            }
        }

        internal void StopAndWait()
        {
            if (!started)
            {
                return;
            }
            if (assignedToJob)
            {
                if (!TerminateJobObject(jobHandle, 1))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "The test process Job could not be terminated.");
                }
            }
            else if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            WaitForExitAndChildren();
        }

        internal string GetStandardOutput() => standardOutput.GetAwaiter().GetResult();
        internal string GetStandardError() => standardError.GetAwaiter().GetResult();

        internal void Dispose()
        {
            try
            {
                StopAndWait();
            }
            finally
            {
                if (jobHandle != IntPtr.Zero)
                {
                    CloseHandle(jobHandle);
                    jobHandle = IntPtr.Zero;
                }
                process.Dispose();
            }
        }

        private static ProcessStartInfo CreateExecutionGateStartInfo(ProcessStartInfo target)
        {
            // updaterがwatchdogを作る前にJobへ所属させる。gatewayの完成済みArgumentsは
            // 再解析・再引用せず、ArgumentListだけWindows argvの末尾backslashも含めて引用する。
            var gate = new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = target.WorkingDirectory
            };
            gate.Environment["BMS_TEST_EXECUTABLE"] = target.FileName;
            gate.Environment["BMS_TEST_ARGUMENTS"] = target.ArgumentList.Count == 0
                ? target.Arguments
                : string.Join(" ", target.ArgumentList.Select(argument => "\""
                    + System.Text.RegularExpressions.Regex.Replace(argument, @"(\\*)(""|$)", match =>
                        match.Groups[1].Value + match.Groups[1].Value
                        + (match.Groups[2].Value.Length == 0 ? string.Empty : "\\\""))
                    + "\""));
            // cmdの環境展開は一度に限定し、展開後の%を再解釈するCALL等を使わない。
            gate.Arguments = "/d /q /v:off /s /c \"set /p _bmsStart= >nul && \"%BMS_TEST_EXECUTABLE%\" %BMS_TEST_ARGUMENTS%\"";
            return gate;
        }

        private static int QueryActiveProcessCount(IntPtr job)
        {
            var information = new JobObjectBasicAccountingInformation();
            if (!QueryInformationJobObject(
                    job,
                    JobObjectBasicAccountingInformationClass,
                    ref information,
                    (uint)Marshal.SizeOf<JobObjectBasicAccountingInformation>(),
                    IntPtr.Zero))
            {
                int error = Marshal.GetLastWin32Error();
                throw new Win32Exception(
                    error,
                    "The owned test process Job could not be queried (error "
                    + error
                    + ").");
            }

            return checked((int)information.ActiveProcesses);
        }

        private static IntPtr CreateProcessJob()
        {
            IntPtr job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "The updater test process Job could not be created.");
            }

            var limits = new JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation
                {
                    LimitFlags = JobObjectLimitKillOnJobClose
                }
            };
            if (!SetInformationJobObject(
                    job,
                    JobObjectExtendedLimitInformationClass,
                    ref limits,
                    (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
            {
                int error = Marshal.GetLastWin32Error();
                CloseHandle(job);
                throw new Win32Exception(error, "The updater test process Job could not be configured.");
            }

            return job;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr jobAttributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(
            IntPtr job,
            int informationClass,
            ref JobObjectExtendedLimitInformation information,
            uint informationLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateJobObject(IntPtr job, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool QueryInformationJobObject(
            IntPtr job,
            int informationClass,
            ref JobObjectBasicAccountingInformation information,
            uint informationLength,
            IntPtr returnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectBasicAccountingInformation
        {
            internal long TotalUserTime;
            internal long TotalKernelTime;
            internal long ThisPeriodTotalUserTime;
            internal long ThisPeriodTotalKernelTime;
            internal uint TotalPageFaultCount;
            internal uint TotalProcesses;
            internal uint ActiveProcesses;
            internal uint TotalTerminatedProcesses;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectBasicLimitInformation
        {
            internal long PerProcessUserTimeLimit;
            internal long PerJobUserTimeLimit;
            internal uint LimitFlags;
            internal UIntPtr MinimumWorkingSetSize;
            internal UIntPtr MaximumWorkingSetSize;
            internal uint ActiveProcessLimit;
            internal UIntPtr Affinity;
            internal uint PriorityClass;
            internal uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            internal ulong ReadOperationCount;
            internal ulong WriteOperationCount;
            internal ulong OtherOperationCount;
            internal ulong ReadTransferCount;
            internal ulong WriteTransferCount;
            internal ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectExtendedLimitInformation
        {
            internal JobObjectBasicLimitInformation BasicLimitInformation;
            internal IoCounters IoInfo;
            internal UIntPtr ProcessMemoryLimit;
            internal UIntPtr JobMemoryLimit;
            internal UIntPtr PeakProcessMemoryUsed;
            internal UIntPtr PeakJobMemoryUsed;
        }
    }
    private static string FindUpdaterExecutable()
    {
        string? publishedRoot = Environment.GetEnvironmentVariable("BMS_SCD_UPDATER_PUBLISH_ROOT");
        if (!string.IsNullOrWhiteSpace(publishedRoot))
        {
            string publishedExecutable = Path.Combine(
                Path.GetFullPath(publishedRoot),
                "BeMusicSeeker.Updater.exe");
            if (!File.Exists(publishedExecutable))
            {
                throw new FileNotFoundException(
                    "The explicitly selected published updater executable was not found.",
                    publishedExecutable);
            }
            return publishedExecutable;
        }

        return FindBuiltUpdaterFile("BeMusicSeeker.Updater.exe");
    }

    private static string FindBuiltUpdaterFile(string fileName)
    {
        string repositoryRoot = FindRepositoryRoot();
        var frameworkDirectory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        string targetFramework = frameworkDirectory.Name;
        string configuration = frameworkDirectory.Parent?.Name ?? "Debug";
        string platform = frameworkDirectory.Parent?.Parent?.Name ?? "x64";

        string[] candidates =
        [
            Path.Combine(repositoryRoot, "BeMusicSeeker.Updater", "bin", platform, configuration, targetFramework, fileName),
            Path.Combine(repositoryRoot, "BeMusicSeeker.Updater", "bin", configuration, targetFramework, fileName)
        ];
        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("Updater build output was not found.", candidates[0]);
    }

    private static IReadOnlyList<string> GetRecoveryRunOnceValues(string appDirectoryPath)
    {
        using RegistryKey? runOnce = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\RunOnce",
            writable: false);
        if (runOnce == null)
        {
            return Array.Empty<string>();
        }

        return runOnce.GetValueNames()
            .Where(valueName => valueName.StartsWith("!BeMusicSeeker.UpdateRecovery-", StringComparison.Ordinal)
                || valueName.StartsWith("BeMusicSeeker.UpdateRecovery-", StringComparison.Ordinal))
            .Where(valueName => (runOnce.GetValue(valueName) as string)?.Contains(
                appDirectoryPath,
                StringComparison.OrdinalIgnoreCase) == true)
            .ToArray();
    }

    private static void DeleteRecoveryRunOnceValues(string appDirectoryPath)
    {
        using RegistryKey? runOnce = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\RunOnce",
            writable: true);
        if (runOnce != null)
        {
            foreach (string valueName in GetRecoveryRunOnceValues(appDirectoryPath))
            {
                runOnce.DeleteValue(valueName, throwOnMissingValue: false);
            }

            runOnce.Flush();
        }

        using RegistryKey? run = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Run",
            writable: true);
        if (run == null)
        {
            return;
        }

        foreach (string valueName in run.GetValueNames()
            .Where(valueName => valueName.StartsWith("BeMusicSeeker.UpdateRecoverySupervisor-", StringComparison.Ordinal))
            .Where(valueName => (run.GetValue(valueName) as string)?.Contains(
                appDirectoryPath,
                StringComparison.OrdinalIgnoreCase) == true)
            .ToArray())
        {
            run.DeleteValue(valueName, throwOnMissingValue: false);
        }

        run.Flush();
    }

    private static IReadOnlyList<string> GetRecoverySupervisorValues(string appDirectoryPath)
    {
        using RegistryKey? run = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Run",
            writable: false);
        if (run == null)
        {
            return Array.Empty<string>();
        }

        return run.GetValueNames()
            .Where(valueName => valueName.StartsWith("BeMusicSeeker.UpdateRecoverySupervisor-", StringComparison.Ordinal))
            .Where(valueName => (run.GetValue(valueName) as string)?.Contains(
                appDirectoryPath,
                StringComparison.OrdinalIgnoreCase) == true)
            .ToArray();
    }

    private static string FindRepositoryRoot()
    {
        string? directoryPath = AppDomain.CurrentDomain.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(directoryPath))
        {
            if (File.Exists(Path.Combine(directoryPath, "BeMusicSeeker", "BeMusicSeeker.csproj")))
            {
                return directoryPath!;
            }

            DirectoryInfo? parent = Directory.GetParent(directoryPath);
            directoryPath = parent?.FullName;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private static void WriteTextFile(string rootDirectoryPath, string relativePath, string contents)
    {
        string filePath = Path.Combine(rootDirectoryPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
        string? directoryPath = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
        }

        File.WriteAllText(filePath, contents);
    }

    private static string ComputeCanonicalTreeHash(string appDirectoryPath)
    {
        return ComputeTreeHash(appDirectoryPath, includePreservedTopLevel: false);
    }

    private static string ComputePreservedTreeHash(string appDirectoryPath)
    {
        return ComputeTreeHash(appDirectoryPath, includePreservedTopLevel: true);
    }

    private static string ComputeTreeHash(string rootDirectoryPath, bool includePreservedTopLevel)
    {
        string[] preservedNames = ["config", "data", "log", "logs", "update_backup", "update_work"];
        var entries = new List<string>();
        foreach (string path in Directory.EnumerateFileSystemEntries(rootDirectoryPath, "*", SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(rootDirectoryPath, path)
                .Replace(Path.DirectorySeparatorChar, '/');
            string topLevelName = relativePath.Split('/')[0];
            bool isPreserved = preservedNames.Contains(topLevelName, StringComparer.OrdinalIgnoreCase);
            if (isPreserved != includePreservedTopLevel
                || (includePreservedTopLevel
                    && !string.Equals(topLevelName, "config", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(topLevelName, "data", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (Directory.Exists(path))
            {
                entries.Add("D|" + relativePath);
            }
            else
            {
                entries.Add(
                    "F|"
                    + relativePath
                    + "|"
                    + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
            }
        }

        entries.Sort(StringComparer.Ordinal);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", entries))));
    }

    /// <summary>本番と同じGUI起動先を配置し、marker内容や生存gateの入力をその版とともに移動させます。</summary>
    /// <param name="rootDirectoryPath">アプリ、packageまたはbackupの配置先です。</param>
    /// <param name="markerFile">起動した版が生成するmarker名です。nullなら生成しません。</param>
    /// <param name="markerContents">版ごとのmarker内容です。</param>
    /// <param name="waitForRelease">準備通知と標準入力のreleaseで生存期間を制御する場合にtrueです。</param>
    private static void CopyRestartExecutable(string rootDirectoryPath, string? markerFile = null, string? markerContents = null, bool waitForRelease = false)
    {
        Directory.CreateDirectory(rootDirectoryPath);
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "updater-restart-application", "UpdaterRestartApplication.exe"),
            Path.Combine(rootDirectoryPath, "restart.exe"),
            overwrite: true);
        WriteTextFile(rootDirectoryPath, "restart.json", JsonSerializer.Serialize(new
        {
            WaitForRelease = waitForRelease,
            MarkerFile = markerFile,
            MarkerContents = markerContents
        }));
    }

    private void WithTemporaryDirectory(Action<string> action)
    {
        WithTemporaryDirectory((tempDirectoryPath, _) => action(tempDirectoryPath), useDriveRoot: false);
    }

    /// <summary>実領域と実際のアプリルートを渡し、全プロセス終端後に所有した登録・別名・実領域を順に回収します。</summary>
    private void WithTemporaryDirectory(Action<string, string> action, bool useDriveRoot)
    {
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerUpdaterTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        var processScope = new CaseProcessScope();
        activeProcessScope = processScope;
        OwnedTestDrive? drive = null;
        string appDirectoryPath = Path.Combine(tempDirectoryPath, "app");
        bool ownsRecoveryRegistrations = false;
        Exception? primaryFailure = null;
        try
        {
            if (useDriveRoot)
            {
                // Zは保全中の別名であり、選択・予約・照会より前に必ず除外する。
                drive = OwnedTestDrive.Create(tempDirectoryPath, 'Z');
                drive.AssertOwned();
                appDirectoryPath = drive.Root;
            }
            Assert.AreEqual(0, GetRecoveryRunOnceValues(appDirectoryPath).Count, "Existing recovery registration makes this fixture unavailable: " + appDirectoryPath);
            Assert.AreEqual(0, GetRecoverySupervisorValues(appDirectoryPath).Count, "Existing recovery supervisor makes this fixture unavailable: " + appDirectoryPath);
            ownsRecoveryRegistrations = true;
            Directory.CreateDirectory(Path.Combine(appDirectoryPath, "update_work", "downloads"));
            action(tempDirectoryPath, appDirectoryPath);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
            throw;
        }
        finally
        {
            activeProcessScope = null;
            var cleanupDiagnostics = new List<string>(processScope.DisposeAll());
            if (cleanupDiagnostics.Count == 0)
            {
                try
                {
                    drive?.AssertOwned();
                    // root文字の再利用によるforeign登録は削除しない。開始後に所有した登録だけを回収する。
                    if (ownsRecoveryRegistrations)
                    {
                        DeleteRecoveryRunOnceValues(appDirectoryPath);
                    }
                }
                catch (Exception exception)
                {
                    cleanupDiagnostics.Add("recovery registration cleanup failed: " + exception.Message);
                }
            }

            if (cleanupDiagnostics.Count == 0)
            {
                try
                {
                    // 全Jobと両pipeの終端後、解除の直前にも同じ実領域への唯一の定義を照合する。
                    drive?.AssertOwned();
                    drive?.Dispose();
                    drive = null;
                }
                catch (Exception exception)
                {
                    cleanupDiagnostics.Add("owned updater drive cleanup failed: " + exception.Message);
                }
            }
            if (cleanupDiagnostics.Count == 0)
            {
                try
                {
                    if (Directory.Exists(tempDirectoryPath))
                    {
                        Directory.Delete(tempDirectoryPath, recursive: true);
                    }
                }
                catch (Exception exception)
                {
                    cleanupDiagnostics.Add("temporary updater test directory cleanup failed: " + exception.Message);
                }
            }

            if (cleanupDiagnostics.Count > 0)
            {
                cleanupDiagnostics.Add("Preserved updater fixture: app=" + appDirectoryPath + ", physical=" + tempDirectoryPath);
                string message = string.Join(
                    Environment.NewLine,
                    cleanupDiagnostics.Distinct(StringComparer.Ordinal));
                if (primaryFailure is null)
                {
                    Assert.Fail(message);
                }

                // 主処理の失敗を清掃診断で置き換えず、二次診断として残す。
                primaryFailure.Data["UpdaterCleanupSecondaryDiagnostics"] = message;
                Console.Error.WriteLine("Updater test cleanup diagnostics:" + Environment.NewLine + message);
            }
        }
    }
}
