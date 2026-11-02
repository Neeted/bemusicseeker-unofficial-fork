using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Components.Update;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Processes;
using BeMusicSeeker.TestSupport.Verification;
using static BeMusicSeeker.TestSupport.Components.Update.UpdaterPackageTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
[TestCategory("EnvironmentIntegration")]
[TestCategory("ProcessIntegration")]
public sealed class UpdaterPackageSyncTests
{
    private UpdaterPackageCommands? commands;
    private ProcessDeadline? operationDeadline;
    private Lazy<ProcessDeadline>? failureCleanupDeadline;
    private readonly List<string> cleanupDiagnostics = [];
    private UpdaterPackageCommands Commands => commands ?? throw new InvalidOperationException("caseが未初期化です。");

    /// <summary>
    /// 同じドライブの非root作業場所からroot配置を受付け、proceed前の旧構成保持と適用後の管理対象・長パス・実再起動を確認する。
    /// 管理外の空フォルダを保持し、保存済みSongsルートが更新後も通常preflightを通ることを確認する。
    /// </summary>
    /// <remarks>ケース専用の非ZドライブとJobを所有し、プロセス・回復登録・別名・実領域の順に回収する。UNC実共有の検証とは分担する。</remarks>
    [TestMethod]
    [TestCategory("Suite.DriveRoot")]
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

            ProcessStartInfo startInfo = Commands.CreateUpdaterStartInfo(appDirectoryPath, packagePath, backupDirectory);
            startInfo.WorkingDirectory = workingDirectory;
            OwnedProcess process = Commands.StartOwnedProcess(startInfo);
            Assert.IsTrue(Commands.WaitForReadyOrProcessExit(process, readyPath), "Root request failed before ready: " + process.ProcessId.ToString());
            Assert.AreEqual("1", File.ReadAllText(readyPath).Trim());
            Assert.IsFalse(Commands.WaitForExit(process, 250), "The exited parent must not bypass the proceed decision.");
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
            Commands.WaitForExit(process);
            Assert.AreEqual(0, process.RootExit.GetAwaiter().GetResult(), process.StandardOutput.GetAwaiter().GetResult() + Environment.NewLine + process.StandardError.GetAwaiter().GetResult());
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
    [TestCategory("Suite.DriveRoot")]
    public void WindowsGatewayAcceptsDriveRootReadyReceiptAndCancelsWithoutApplying()
    {
        WithTemporaryDirectory((_, appDirectoryPath) =>
        {
            WriteTextFile(appDirectoryPath, "BeMusicSeeker.exe", "old-app");
            CopyRestartExecutable(appDirectoryPath, "restart-marker.txt", "unexpected-restart");
            WriteTextFile(appDirectoryPath, "update_work/downloads/package.zip", "not-yet-applied");
            string canonicalBefore = ComputeCanonicalTreeHash(appDirectoryPath);
            string packagePath = Path.Combine(appDirectoryPath, "update_work", "downloads", "package.zip");
            ProcessDeadline deadline = operationDeadline ?? throw new InvalidOperationException("case期限がありません。");
            string requestPath = Path.Combine(appDirectoryPath, "update_work", "gateway-probe.json");
            var request = new UpdaterPackageProbeRequest(appDirectoryPath, FindUpdaterExecutable(), false, deadline.Remaining.Ticks);
            File.WriteAllText(requestPath, JsonSerializer.Serialize(request));
            try
            {
                WindowsProcessProbeRunner.RunAsync("update-package", nameof(WindowsGatewayAcceptsDriveRootReadyReceiptAndCancelsWithoutApplying),
                    CancellationToken.None, deadline, [requestPath], false,
                    failureCleanupDeadline ?? throw new InvalidOperationException("回収期限がありません。")).GetAwaiter().GetResult();
                // RunAsyncはroot・全Job子孫・両pipeの終端を確認してから返ります。
                Assert.AreEqual(canonicalBefore, ComputeCanonicalTreeHash(appDirectoryPath));
                Assert.IsTrue(File.Exists(packagePath));
                Assert.IsFalse(File.Exists(Path.Combine(appDirectoryPath, "restart-marker.txt")));
            }
            catch (Exception exception)
            {
                if (exception.Data.Contains("FreshProbeCleanupFailures"))
                { cleanupDiagnostics.Add("更新probeの全Job終端を確認できません: " + exception.Data["FreshProbeCleanupFailures"]); }
                throw;
            }
        }, useDriveRoot: true);
    }

    /// <summary>
    /// 同じドライブの子作業場所からドライブルートを回復し、記録されたアプリPIDが再利用されても旧版復元と実再起動を止める誤りを検出する。
    /// </summary>
    /// <remarks>独立Jobと固有パスを所有し、共有registryはこのアプリ領域の登録だけを回収する。</remarks>
    [TestMethod]
    [TestCategory("Suite.DriveRoot")]
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
            Commands.RunUpdaterRecovery(appDirectoryPath, workingDirectory);

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
    [TestCategory("Suite.DriveRoot")]
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

            Commands.ApplyUpdateExpectRestartFailure(appDirectoryPath, packagePath, backupDirectoryPath);

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
    [TestCategory("Suite.DirectorySymbolicLink")]
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

            Commands.RunUpdaterExpectFailure(appDirectoryPath, packagePath, backupDirectoryPath);

            Assert.AreEqual("external-sentinel", File.ReadAllText(Path.Combine(externalDirectoryPath, "old.dll")));
            Assert.AreEqual("old-app", File.ReadAllText(Path.Combine(appDirectoryPath, "BeMusicSeeker.exe")));
            Assert.IsTrue(File.Exists(packagePath));
        });
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
        var deadline = ProcessDeadline.FromSuiteEnvironment(TimeSpan.FromSeconds(180));
        operationDeadline = deadline;
        var cleanupDeadline = new Lazy<ProcessDeadline>(() => ProcessDeadline.FromTimeout(TimeSpan.FromSeconds(10)));
        failureCleanupDeadline = cleanupDeadline;
        var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string tempDirectoryPath = workspace.RootDirectory;
        cleanupDiagnostics.Clear();
        OwnedTestDrive? drive = null;
        string appDirectoryPath = Path.Combine(tempDirectoryPath, "app");
        bool ownsRecoveryRegistrations = false;
        Exception? primaryFailure = null;
        try
        {
            commands = new UpdaterPackageCommands(FindUpdaterExecutable(), deadline, cleanupDeadline);
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
            DistributionValidation.CheckDeadline(deadline, CancellationToken.None);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
            if (exception is OwnedProcessStartException { CleanupFailure: not null } startFailure)
            { cleanupDiagnostics.Add(startFailure.CleanupFailure.ToString()); }
            throw;
        }
        finally
        {
            if (commands is not null)
            {
                foreach (OwnedProcess process in commands.Processes.AsEnumerable().Reverse())
                {
                    try
                    {
                        ProcessStopResult stopped = process.StopAsync(null, primaryFailure is null ? deadline : cleanupDeadline.Value).GetAwaiter().GetResult();
                        if (stopped.CleanupFailure is not null) { cleanupDiagnostics.Add(stopped.CleanupFailure.ToString()); }
                        process.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    }
                    catch (Exception exception) { cleanupDiagnostics.Add(exception.ToString()); }
                }
            }
            commands = null;
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
            if (cleanupDiagnostics.Count > 0) { workspace.PreserveArtifacts(); }
            try { workspace.Dispose(); }
            catch (Exception exception) { cleanupDiagnostics.Add(exception.ToString()); }

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
