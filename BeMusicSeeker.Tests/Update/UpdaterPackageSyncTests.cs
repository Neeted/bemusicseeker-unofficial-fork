using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Update;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Components.Update;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static BeMusicSeeker.TestSupport.Components.Update.UpdaterPackageTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
[TestCategory("EnvironmentIntegration")]
[TestCategory("ProcessIntegration")]
public sealed class UpdaterPackageSyncTests
{
    private CaseProcessScope? activeProcessScope;

    /// <summary>
    /// 同じドライブの非root作業場所からroot配置を受付け、proceed前の旧構成保持と適用後の管理対象・長パス・実再起動を確認する。
    /// 管理外の空フォルダを保持し、保存済みSongsルートが更新後も通常preflightを通ることを確認する。
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
            string songsDirectory = Directory.CreateDirectory(Path.Combine(appDirectoryPath, "Songs")).FullName;
            string otherEmptyDirectory = Directory.CreateDirectory(Path.Combine(appDirectoryPath, "user-empty", "nested-empty")).FullName;
            string settingsPath = Path.Combine(appDirectoryPath, "config", "user.config");
            Settings settings = TestSettingsFactory.OpenSettings(settingsPath);
            settings.Commit(
                (nameof(Settings.OperationModeLR2DB), false),
                (nameof(Settings.StandaloneBmsRootPaths), StandaloneBmsRootPathSettings.Serialize([songsDirectory])));
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
            Assert.IsTrue(Directory.Exists(songsDirectory));
            Assert.IsTrue(Directory.Exists(otherEmptyDirectory));
            Assert.AreEqual(0, Directory.GetFileSystemEntries(songsDirectory).Length);
            Assert.AreEqual(0, Directory.GetFileSystemEntries(otherEmptyDirectory).Length);
            Settings reloadedSettings = TestSettingsFactory.OpenSettings(settingsPath);
            IReadOnlyList<string> savedRoots = new BmsRootRegistrationStore(reloadedSettings, () => null).CaptureUserRoots();
            CollectionAssert.AreEqual(new[] { songsDirectory }, savedRoots.ToArray());
            LibraryDirectoryPreflightService preflight = new();
            LibraryDirectoryPreflightRequest request = preflight.CreateRequest(
                savedRoots, savedRoots, new BmsLibraryOptionsSnapshot { OperationModeLR2DB = reloadedSettings.OperationModeLR2DB });
            preflight.EnsureAvailable(request, probeOutputBases: true);
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
                Assert.Fail("The test environment does not permit directory symbolic links: " + exception.Message);
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

    private (Type Program, object Request) PrepareManagedUpdate(string appDirectoryPath, string packagePath, string backupDirectoryPath)
    {
        return UpdaterPackageTestSupport.PrepareManagedUpdate(FindBuiltUpdaterFile("BeMusicSeeker.Updater.dll"), GetExitedProcessId(), appDirectoryPath, packagePath, backupDirectoryPath);
    }

    /// <summary>既存managed準備と同じ失敗callbackで、巻き戻しと元例外の保持を照合します。</summary>
    private void ApplyUpdateExpectRestartFailure(string appDirectoryPath, string packagePath, string backupDirectoryPath)
    {
        (Type program, object request) = PrepareManagedUpdate(appDirectoryPath, packagePath, backupDirectoryPath);
        AssertRestartFailure(program, request, Path.Combine(appDirectoryPath, "restart.exe"));
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

    /// <summary>このcaseが選んだupdaterとPIDを、共用CLI要求へ渡します。</summary>
    private ProcessStartInfo CreateUpdaterStartInfo(string appDirectoryPath, string packagePath, string backupDirectoryPath, string? restartExecutablePath = null, string? processId = null)
    {
        return UpdaterPackageTestSupport.CreateUpdaterStartInfo(FindUpdaterExecutable(), appDirectoryPath, packagePath, backupDirectoryPath, restartExecutablePath, processId ?? GetExitedProcessId().ToString());
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
