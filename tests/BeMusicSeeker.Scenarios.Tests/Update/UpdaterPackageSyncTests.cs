using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
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
    private UpdaterPackageCommands Commands => commands ?? throw new InvalidOperationException("更新caseを開始していません。");
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
            string applicationProcessId = Commands.GetExitedProcessId().ToString();
            string decisionFilePath = Path.Combine(appDirectoryPath, "update_work", "current", "updater-decision.txt");

            OwnedProcess process = Commands.StartUpdater(
                appDirectoryPath,
                packagePath,
                backupDirectoryPath,
                processId: applicationProcessId,
                publishProceed: false,
                restartArgumentsJson: JsonSerializer.Serialize(HardRestartArguments));
            try
            {
                Assert.IsFalse(Commands.WaitForExit(process, 250));
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
                // Versionは1のまま、再起動引数は任意項目として難集合をそのまま保持する。
                Assert.AreEqual(JsonValueKind.Array, journal.GetProperty("RestartArguments").ValueKind);
                CollectionAssert.AreEqual(
                    HardRestartArguments,
                    JsonSerializer.Deserialize<string[]>(journal.GetProperty("RestartArguments").GetRawText()));
            }
            finally
            {
                if (!process.RootExit.IsCompleted)
                {
                    // cancel公開と終端待機も回収であり、外側finallyと同じ期限を先に確定します。
                    ProcessDeadline cleanup = (failureCleanupDeadline ?? throw new InvalidOperationException("回収期限がありません。")).Value;
                    bool decisionPublished = false;
                    try
                    {
                        PublishUpdaterDecision(decisionFilePath, "cancel");
                        decisionPublished = true;
                    }
                    catch (Exception exception)
                    {
                        cleanupDiagnostics.Add(
                            "updater decision cleanup failed: " + exception.Message);
                    }

                    try
                    {
                        if (decisionPublished)
                        {
                            process.WaitForExitAsync(cleanup).GetAwaiter().GetResult();
                        }
                        else
                        {
                            Commands.StopAndWait(process);
                        }
                    }
                    catch (Exception exception)
                    {
                        cleanupDiagnostics.Add(
                            "updater process cleanup failed: " + exception.Message);
                    }
                }
            }

            Assert.IsTrue(process.RootExit.IsCompleted);
            Assert.AreEqual(0, process.RootExit.GetAwaiter().GetResult());
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

            Commands.RunUpdater(appDirectoryPath, packagePath, backupDirectoryPath);

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

            Commands.RunUpdaterRecovery(appDirectoryPath);

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
            CopyRestartExecutable(appDirectoryPath, "restart-marker.txt", "restarted" + Environment.NewLine, argumentsFile: "restart-arguments.json");
            WriteTextFile(appDirectoryPath, "update_work/downloads/package.zip", "package");
            WriteTextFile(previousDirectoryPath, "BeMusicSeeker.exe", "old-app");
            // 項目なしの旧形式journalは空配列として読み、--watchからの復旧でも引数なしで再起動する。
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

            Commands.RunUpdaterWatchdogRecovery(appDirectoryPath);

            Assert.AreEqual("old-app", File.ReadAllText(Path.Combine(appDirectoryPath, "BeMusicSeeker.exe")));
            AssertRestartFileExists(Path.Combine(appDirectoryPath, "restart-marker.txt"));
            AssertRecordedRestartArguments(appDirectoryPath, []);
            Assert.IsFalse(File.Exists(journalPath));
            Assert.IsFalse(Directory.Exists(backupDirectoryPath));
        });
    }

    /// <summary>
    /// 明示復旧が旧版を復元した後、journalに記録された再起動引数を欠落・順序違い・引用崩れなく再起動先へ渡すことを保証する。
    /// </summary>
    /// <remarks>独立Jobと固有パスを所有し、共有registryはこのアプリ領域の登録だけを回収する。</remarks>
    [TestMethod]
    public void RecoverCommandRestartsRestoredApplicationWithJournalRestartArguments()
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
            CopyRestartExecutable(appDirectoryPath, "restart-marker.txt", "restarted" + Environment.NewLine, argumentsFile: "restart-arguments.json");
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
                    NewPackagePaths = new[] { "BeMusicSeeker.exe" },
                    RestartArguments = HardRestartArguments
                }));

            Commands.RunUpdaterRecovery(appDirectoryPath);

            Assert.AreEqual("old-app", File.ReadAllText(Path.Combine(appDirectoryPath, "BeMusicSeeker.exe")));
            AssertRestartFileExists(Path.Combine(appDirectoryPath, "restart-marker.txt"));
            AssertRecordedRestartArguments(appDirectoryPath, HardRestartArguments);
            Assert.IsFalse(File.Exists(journalPath));
            Assert.IsFalse(Directory.Exists(backupDirectoryPath));
            Assert.AreEqual(0, GetRecoveryRunOnceValues(appDirectoryPath).Count, "Recovery must clear the recovery handoff.");
        });
    }

    /// <summary>
    /// journalに項目はあるが値がnull・配列でない・文字列でない要素・NULを含む場合は、再起動せず失敗記録と回復登録を残すことを保証する。
    /// </summary>
    /// <remarks>独立Jobと固有パスを所有し、共有registryはこのアプリ領域の登録だけを回収する。</remarks>
    [TestMethod]
    [DataRow("null")]
    [DataRow("\"a\"")]
    [DataRow("[1]")]
    [DataRow("[null]")]
    [DataRow("[\"a\\u0000b\"]")]
    public void RecoverCommandRejectsInvalidJournalRestartArgumentsWithoutRestarting(string restartArgumentsJson)
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
            CopyRestartExecutable(appDirectoryPath, "restart-marker.txt", "restarted" + Environment.NewLine, argumentsFile: "restart-arguments.json");
            WriteTextFile(previousDirectoryPath, "BeMusicSeeker.exe", "old-app");
            string journalJson = JsonSerializer.Serialize(new
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
            });
            // 末尾の閉じ括弧の前へ、検査対象の生JSONを項目として足す。
            journalJson = journalJson[..^1] + ",\"RestartArguments\":" + restartArgumentsJson + "}";
            WriteTextFile(appDirectoryPath, "update_work/update-transaction.json", journalJson);

            OwnedProcess process = Commands.StartUpdaterRecoveryProcess(appDirectoryPath);
            Commands.WaitForExit(process);

            Assert.AreNotEqual(0, process.RootExit.GetAwaiter().GetResult());
            Assert.AreEqual("partial-new", File.ReadAllText(Path.Combine(appDirectoryPath, "BeMusicSeeker.exe")));
            Assert.IsFalse(File.Exists(Path.Combine(appDirectoryPath, "restart-marker.txt")), "An invalid journal must not restart the application.");
            Assert.IsFalse(File.Exists(Path.Combine(appDirectoryPath, "restart-arguments.json")));
            Assert.IsTrue(File.Exists(journalPath), "An invalid journal must remain for diagnosis.");
            Assert.IsTrue(File.Exists(Path.Combine(appDirectoryPath, "update_work", "update-failure.txt")));
            Assert.AreEqual(1, GetRecoveryRunOnceValues(appDirectoryPath).Count, "The recovery handoff must be rearmed.");
        });
    }

    /// <summary>
    /// 難集合の再起動引数を渡した更新が成功すると、再起動先のMainが同じ列を受け取り、未指定・空配列では引数なしで再起動することを保証する。
    /// </summary>
    /// <remarks>独立Jobと固有パスを所有し、共有registryはこのアプリ領域の登録だけを回収する。</remarks>
    [TestMethod]
    [DataRow("inherited")]
    [DataRow("omitted")]
    [DataRow("empty")]
    public void UpdaterRestartsAppliedApplicationWithRestartArgumentsUnchanged(string mode)
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string appDirectoryPath = Path.Combine(tempDirectoryPath, "app");
            string packageSourceDirectoryPath = Path.Combine(tempDirectoryPath, "package-source");
            string packagePath = Path.Combine(appDirectoryPath, "update_work", "downloads", "package.zip");
            string backupDirectoryPath = Path.Combine(appDirectoryPath, "update_backup");
            string journalPath = Path.Combine(appDirectoryPath, "update_work", "update-transaction.json");
            const string managedPaths = "BeMusicSeeker.exe\nrestart.exe\nrestart.json";

            WriteTextFile(appDirectoryPath, "BeMusicSeeker.exe", "old-app");
            CopyRestartExecutable(appDirectoryPath);
            WriteTextFile(appDirectoryPath, "update-managed-files.txt", managedPaths);
            WriteTextFile(packageSourceDirectoryPath, "BeMusicSeeker.exe", "new-app");
            CopyRestartExecutable(packageSourceDirectoryPath, "restart-marker.txt", "restarted", argumentsFile: "restart-arguments.json");
            WriteTextFile(packageSourceDirectoryPath, "update-managed-files.txt", managedPaths);
            ZipFile.CreateFromDirectory(packageSourceDirectoryPath, packagePath);

            string? restartArgumentsJson = mode switch
            {
                "inherited" => JsonSerializer.Serialize(HardRestartArguments),
                "empty" => "[]",
                _ => null
            };
            Commands.RunUpdater(appDirectoryPath, packagePath, backupDirectoryPath, restartArgumentsJson: restartArgumentsJson);

            Assert.AreEqual("new-app", File.ReadAllText(Path.Combine(appDirectoryPath, "BeMusicSeeker.exe")));
            AssertRestartFileExists(Path.Combine(appDirectoryPath, "restart-marker.txt"));
            AssertRecordedRestartArguments(appDirectoryPath, mode == "inherited" ? HardRestartArguments : []);
            Assert.IsFalse(File.Exists(journalPath), "A committed transaction must remove its durable journal.");
            Assert.IsFalse(Directory.Exists(backupDirectoryPath));
            Assert.AreEqual(0, GetRecoveryRunOnceValues(appDirectoryPath).Count);
        });
    }

    /// <summary>
    /// 不正な再起動引数は受付通知より前に拒否し、journal・回復登録・本体・ZIPの変更や再起動を一切起こさないことを保証する。
    /// </summary>
    /// <remarks>独立Jobと固有パスを所有し、共有registryはこのアプリ領域の登録だけを回収する。</remarks>
    [TestMethod]
    [DataRow("[\"a\\u0000b\"]")]
    [DataRow("[\"a\"")]
    [DataRow("{\"0\":\"a\"}")]
    [DataRow("\"a\"")]
    [DataRow("null")]
    [DataRow("[\"a\",1]")]
    [DataRow("[\"a\",null]")]
    [DataRow("[[\"a\"]]")]
    public void UpdaterRejectsInvalidRestartArgumentsJsonBeforeReadyWithoutMutation(string restartArgumentsJson)
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string appDirectoryPath = Path.Combine(tempDirectoryPath, "app");
            string packageSourceDirectoryPath = Path.Combine(tempDirectoryPath, "package-source");
            string packagePath = Path.Combine(appDirectoryPath, "update_work", "downloads", "package.zip");
            string backupDirectoryPath = Path.Combine(appDirectoryPath, "update_backup");
            string journalPath = Path.Combine(appDirectoryPath, "update_work", "update-transaction.json");
            string readyPath = Path.Combine(appDirectoryPath, "update_work", "current", "updater-ready.txt");
            const string managedPaths = "BeMusicSeeker.exe\nrestart.exe\nrestart.json";

            WriteTextFile(appDirectoryPath, "BeMusicSeeker.exe", "old-app");
            CopyRestartExecutable(appDirectoryPath, "restart-marker.txt", "restarted", argumentsFile: "restart-arguments.json");
            WriteTextFile(appDirectoryPath, "update-managed-files.txt", managedPaths);
            WriteTextFile(packageSourceDirectoryPath, "BeMusicSeeker.exe", "new-app");
            CopyRestartExecutable(packageSourceDirectoryPath, "restart-marker.txt", "restarted", argumentsFile: "restart-arguments.json");
            WriteTextFile(packageSourceDirectoryPath, "update-managed-files.txt", managedPaths);
            ZipFile.CreateFromDirectory(packageSourceDirectoryPath, packagePath);
            string treeBefore = ComputeCanonicalTreeHash(appDirectoryPath);

            Commands.RunUpdaterExpectFailure(appDirectoryPath, packagePath, backupDirectoryPath, restartArgumentsJson: restartArgumentsJson);

            Assert.IsFalse(File.Exists(readyPath), "Invalid arguments must be rejected before the ready handshake.");
            Assert.IsFalse(File.Exists(journalPath));
            Assert.AreEqual(0, GetRecoveryRunOnceValues(appDirectoryPath).Count);
            Assert.AreEqual(0, GetRecoverySupervisorValues(appDirectoryPath).Count);
            Assert.AreEqual(treeBefore, ComputeCanonicalTreeHash(appDirectoryPath));
            Assert.AreEqual("old-app", File.ReadAllText(Path.Combine(appDirectoryPath, "BeMusicSeeker.exe")));
            Assert.IsTrue(File.Exists(packagePath));
            Assert.IsFalse(Directory.Exists(backupDirectoryPath));
            Assert.IsFalse(File.Exists(Path.Combine(appDirectoryPath, "restart-marker.txt")));
            Assert.IsFalse(File.Exists(Path.Combine(appDirectoryPath, "restart-arguments.json")));
        });
    }

    /// <summary>再起動先が記録したargvがJSON文字列配列として期待と完全一致することを、全プロセス終端後に照合します。</summary>
    private static void AssertRecordedRestartArguments(string appDirectoryPath, IReadOnlyList<string> expected)
    {
        string recordedPath = Path.Combine(appDirectoryPath, "restart-arguments.json");
        Assert.IsTrue(File.Exists(recordedPath), "The restarted application did not record its arguments.");
        string commandLinePath = recordedPath + ".commandline.txt";
        string commandLine = File.Exists(commandLinePath) ? File.ReadAllText(commandLinePath) : "(not recorded)";
        CollectionAssert.AreEqual(
            expected.ToArray(),
            JsonSerializer.Deserialize<string[]>(File.ReadAllText(recordedPath)),
            "Raw command line: " + commandLine);
    }

    /// <summary>
    /// 生存アプリを保持し、preparedは両入口で回収し、backing-up/applyingは延期する。旧世代や起動数を変更する誤りを検出する。
    /// </summary>
    /// <remarks>独立Jobと固有パスを所有し、共有registryはこのアプリ領域の登録だけを回収する。</remarks>
    [TestMethod]
    [DataRow("applying", false)]
    [DataRow("backing-up", false)]
    [DataRow("prepared", false)]
    [DataRow("prepared", true)]
    public void RecoverCommandDefersWhenTheApplicationExecutableIsStillRunning(string phase, bool preflight)
    {
        WithTemporaryDirectory(tempDirectoryPath => RunProbe(nameof(RecoverCommandDefersWhenTheApplicationExecutableIsStillRunning), tempDirectoryPath, false, phase, preflight));
    }

    /// <summary>実Gatewayのcancelでjournal・回復登録・watchdogが終端し、currentを再配置して次のreadyへ進めることを保証します。</summary>
    [TestMethod]
    public void WindowsGatewayCancelsAndPreparesNextUpdate()
    {
        WithTemporaryDirectory(tempDirectoryPath => RunProbe(nameof(WindowsGatewayCancelsAndPreparesNextUpdate), tempDirectoryPath, false));
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

            Commands.RunUpdaterRecovery(appDirectoryPath);

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

            OwnedProcess firstUpdater = Commands.StartUpdater(
                appDirectoryPath,
                packagePath,
                backupDirectoryPath,
                publishProceed: false);
            OwnedProcess secondUpdater = Commands.StartOwnedProcess(
                Commands.CreateUpdaterStartInfo(
                    appDirectoryPath,
                    packagePath,
                    backupDirectoryPath));

            Commands.WaitForExit(secondUpdater);
            Assert.AreNotEqual(0, secondUpdater.RootExit.GetAwaiter().GetResult());
            PublishUpdaterDecision(
                Path.Combine(appDirectoryPath, "update_work", "current", "updater-decision.txt"),
                "cancel");
            Commands.WaitForExit(firstUpdater);
            Assert.AreEqual(0, firstUpdater.RootExit.GetAwaiter().GetResult());
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

            Commands.RunUpdaterExpectFailure(
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
                OwnedProcess recovery = Commands.StartUpdaterRecoveryProcess(appDirectoryPath);
                Commands.WaitForExit(recovery);
                Assert.AreNotEqual(0, recovery.RootExit.GetAwaiter().GetResult(), "Rollback second fault must not be reported as success.");
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

            Commands.RunUpdaterRecovery(appDirectoryPath);

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

            OwnedProcess firstUpdater = Commands.StartUpdater(
                appDirectoryPath,
                packagePath,
                backupDirectoryPath,
                publishProceed: false);
            Assert.AreEqual(1, GetRecoveryRunOnceValues(appDirectoryPath).Count);
            DeleteRecoveryRunOnceValues(appDirectoryPath);

            OwnedProcess recovery = Commands.StartUpdaterRecoveryProcess(appDirectoryPath);
            // リース所有者は判断待ちなので、解放前の短い否定観測で競合側が
            // 完了していないことを確認する。
            Assert.IsFalse(Commands.WaitForExit(recovery, 250), "A competing recovery must wait for the transaction owner.");

            PublishUpdaterDecision(
                Path.Combine(appDirectoryPath, "update_work", "current", "updater-decision.txt"),
                "cancel");
            Commands.WaitForExit(firstUpdater);
            Assert.AreEqual(0, firstUpdater.RootExit.GetAwaiter().GetResult());
            Commands.WaitForExit(recovery);
            Assert.AreEqual(0, recovery.RootExit.GetAwaiter().GetResult());
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

            Commands.RunUpdater(appDirectoryPath, packagePath, backupDirectoryPath);

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
    /// 旧取込archiveの削除を確認し、空になった親フォルダの有無は固定しない。
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

            Commands.RunUpdater(appDirectoryPath, packagePath, backupDirectoryPath);

            Assert.AreEqual("new-app", File.ReadAllText(Path.Combine(appDirectoryPath, "BeMusicSeeker.exe")));
            Assert.IsFalse(File.Exists(Path.Combine(appDirectoryPath, "chart-info-metadata.7z")));
            Assert.IsFalse(File.Exists(Path.Combine(appDirectoryPath, "chart-info-metadata.db")));
            Assert.IsFalse(File.Exists(Path.Combine(appDirectoryPath, "imported_metadata", "chart-info-metadata.7z")));
            Assert.IsFalse(File.Exists(Path.Combine(appDirectoryPath, "imported_metadata", "chart-info-metadata.aaaaaaaaaaaa.7z")));
        });
    }

    /// <summary>
    /// 同梱版更新は新しいmetadata archiveへ置換して旧DB・取込履歴を整理し、古い解析情報を残す誤りを検出する。
    /// 旧取込archiveの削除を確認し、空になった親フォルダの有無は固定しない。
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

            Commands.RunUpdater(appDirectoryPath, packagePath, backupDirectoryPath);

            Assert.AreEqual("new-app", File.ReadAllText(Path.Combine(appDirectoryPath, "BeMusicSeeker.exe")));
            Assert.AreEqual("new-bundled-archive", File.ReadAllText(Path.Combine(appDirectoryPath, "chart-info-metadata.7z")));
            Assert.IsFalse(File.Exists(Path.Combine(appDirectoryPath, "chart-info-metadata.db")));
            Assert.IsFalse(File.Exists(Path.Combine(appDirectoryPath, "imported_metadata", "chart-info-metadata.7z")));
            Assert.IsFalse(File.Exists(Path.Combine(appDirectoryPath, "imported_metadata", "chart-info-metadata.bbbbbbbbbbbb.7z")));
        });
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

            Commands.RunUpdaterExpectFailure(appDirectoryPath, packagePath, appDirectoryPath);

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

            Commands.RunUpdaterExpectFailure(appDirectoryPath, packagePath, backupDirectoryPath, missingRestartPath);

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

            Commands.RunUpdaterExpectFailure(appDirectoryPath, packagePath, backupDirectoryPath, restartExecutablePath);

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

            Commands.RunUpdaterExpectFailure(appDirectoryPath, packagePath, backupDirectoryPath, processId: "0");

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

            Commands.RunUpdaterExpectFailure(appDirectoryPath, packagePath, backupDirectoryPath);

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
            Commands.RunUpdater(appDirectoryPath, packagePath, backupDirectoryPath, restartExecutablePath);

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
            (Type program, object request) = Commands.PrepareManagedUpdate(appDirectory, packagePath, backupDirectory);
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
                failure = Assert.ThrowsExactly<TargetInvocationException>(() =>
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
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [DoNotParallelize]
    public void ApplyUpdate_PostRestartDiagnosticFailureKeepsCommittedApplication(bool restarting)
    {
        WithTemporaryDirectory(tempDirectoryPath => RunProbe(nameof(ApplyUpdate_PostRestartDiagnosticFailureKeepsCommittedApplication), tempDirectoryPath, restarting));
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

            Commands.ApplyUpdateExpectRestartFailure(appDirectoryPath, packagePath, backupDirectoryPath);

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

            Commands.RunUpdater(appDirectoryPath, packagePath, backupDirectoryPath);

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

            Commands.RunUpdaterExpectFailure(appDirectoryPath, packagePath, backupDirectoryPath);

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

            Commands.RunUpdaterExpectFailure(appDirectoryPath, packagePath, backupDirectoryPath);

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

            Commands.RunUpdaterExpectFailure(appDirectoryPath, packagePath, backupDirectoryPath);

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

            Commands.ApplyUpdateExpectRestartFailure(appDirectoryPath, packagePath, backupDirectoryPath);

            Assert.AreEqual("old-managed", File.ReadAllText(Path.Combine(appDirectoryPath, "docs", "a.txt")));
            Assert.AreEqual("user-file", File.ReadAllText(Path.Combine(appDirectoryPath, "docs", "user.txt")));
            Assert.AreEqual("blocking-file", File.ReadAllText(Path.Combine(appDirectoryPath, "zz")));
        });
    }
    private void RunProbe(string caseName, string rootDirectory, bool restarting, string phase = "applying", bool preflight = false)
    {
        ProcessDeadline deadline = operationDeadline ?? throw new InvalidOperationException("case期限がありません。");
        string requestPath = Path.Combine(rootDirectory, "update-probe.json");
        var request = new UpdaterPackageProbeRequest(rootDirectory, Path.Combine(rootDirectory, "published-updater", "BeMusicSeeker.Updater.exe"), restarting, deadline.Remaining.Ticks, phase, preflight);
        File.WriteAllText(requestPath, JsonSerializer.Serialize(request));
        try
        {
            WindowsProcessProbeRunner.RunAsync("update-package", caseName, CancellationToken.None, deadline, [requestPath], restarting, failureCleanupDeadline ?? throw new InvalidOperationException("回収期限がありません。")).GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            if (exception.Data.Contains("FreshProbeCleanupFailures"))
            {
                cleanupDiagnostics.Add("更新probeの全Job終端を確認できません: " + exception.Data["FreshProbeCleanupFailures"]);
            }
            throw;
        }
    }

    private void WithTemporaryDirectory(Action<string> action)
    {
        operationDeadline = ProcessDeadline.FromTimeout(TimeSpan.FromSeconds(180));
        var cleanupDeadline = new Lazy<ProcessDeadline>(() => ProcessDeadline.FromTimeout(TimeSpan.FromSeconds(10)));
        failureCleanupDeadline = cleanupDeadline;
        var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string appDirectoryPath = workspace.GetPath("app");
        bool ownsRecoveryRegistrations = false;
        Exception? primaryFailure = null;
        cleanupDiagnostics.Clear();
        try
        {
            DistributionCatalog catalog = DistributionCatalog.ReadFromEnvironmentAsync(UpdateDeploymentTestSupport.FindRepositoryRoot(), operationDeadline).GetAwaiter().GetResult();
            string updaterDirectory = workspace.GetPath("published-updater");
            Directory.CreateDirectory(updaterDirectory);
            string publishedRoot = catalog.Current.UpdaterPublishRoot ?? throw new InvalidDataException("Current updater rootがありません。");
            string updaterPath = Path.Combine(updaterDirectory, "BeMusicSeeker.Updater.exe");
            File.Copy(Path.Combine(publishedRoot, "BeMusicSeeker.Updater.exe"), updaterPath);
            DistributionValidation.CheckDeadline(operationDeadline, CancellationToken.None);
            commands = new UpdaterPackageCommands(updaterPath, operationDeadline, cleanupDeadline);
            Assert.AreEqual(0, GetRecoveryRunOnceValues(appDirectoryPath).Count, "Existing recovery registration makes this fixture unavailable: " + appDirectoryPath);
            Assert.AreEqual(0, GetRecoverySupervisorValues(appDirectoryPath).Count, "Existing recovery supervisor makes this fixture unavailable: " + appDirectoryPath);
            ownsRecoveryRegistrations = true;
            Directory.CreateDirectory(Path.Combine(appDirectoryPath, "update_work", "downloads"));
            action(workspace.RootDirectory);
            DistributionValidation.CheckDeadline(operationDeadline, CancellationToken.None);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
            if (exception is OwnedProcessStartException { CleanupFailure: not null } startFailure)
            {
                cleanupDiagnostics.Add(startFailure.CleanupFailure.ToString());
            }
            throw;
        }
        finally
        {
            // 起動失敗とfinallyが同じLazy期限を共有し、操作の失敗・取消でも回収を省略しない。
            if (commands is not null)
            {
                foreach (OwnedProcess process in commands.Processes.AsEnumerable().Reverse())
                {
                    try
                    {
                        ProcessStopResult stopped = process.StopAsync(null, cleanupDeadline.Value).GetAwaiter().GetResult();
                        if (stopped.CleanupFailure is not null) { cleanupDiagnostics.Add(stopped.CleanupFailure.ToString()); }
                        process.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    }
                    catch (Exception exception) { cleanupDiagnostics.Add(exception.ToString()); }
                }
            }
            commands = null;
            if (cleanupDiagnostics.Count == 0 && ownsRecoveryRegistrations)
            {
                try { DeleteRecoveryRunOnceValues(appDirectoryPath); }
                catch (Exception exception) { cleanupDiagnostics.Add(exception.ToString()); }
            }
            if (primaryFailure is not null || cleanupDiagnostics.Count > 0) { workspace.PreserveArtifacts(); }
            try { workspace.Dispose(); }
            catch (Exception exception) { cleanupDiagnostics.Add(exception.ToString()); }
            if (cleanupDiagnostics.Count > 0)
            {
                string message = string.Join(Environment.NewLine, cleanupDiagnostics) + Environment.NewLine + "Preserved updater fixture: " + workspace.RootDirectory;
                if (primaryFailure is not null) { primaryFailure.Data["UpdaterCleanupSecondaryDiagnostics"] = message; }
                else { Assert.Fail(message); }
            }
        }
    }
}
