using BeMusicSeeker.Models;
using BeMusicSeeker.Models.Update;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Components.Update;
using BeMusicSeeker.TestSupport.Files;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class UpdateDownloadServiceTests
{
    /// <summary>
    /// 更新ツールだけを作業領域へコピーして旧payloadを除き、未起動の型付き要求に指定パスを渡して配置・起動順の誤りを検出する。
    /// </summary>
    [TestMethod]
    public void PrepareUpdaterLaunchCopiesUpdaterAndPublishesTypedRequest()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string root = workspace.RootDirectory;
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
        // 動作モード変更の再起動と同じ捕捉値（検証起動の絶対manifestパスや--test-sessionを含む）を件数・順序・値で引き継ぐ。
        string[] restartArguments =
        [
            .. UpdaterPackageTestSupport.HardRestartArguments,
            "--test-session",
            Path.Combine(root, "manifest dir", "manifest.json")
        ];
        var service = new UpdateDownloadService(
            ApplicationPathSnapshot.FromExecutablePath(applicationPath),
            gateway,
            restartArguments);

        {
            IPreparedUpdaterLaunch preparedLaunch = service.PrepareUpdaterLaunch(packagePath);

            Assert.AreSame(gateway.PreparedLaunch, preparedLaunch);
            Assert.AreEqual(0, gateway.PreparedLaunch.StartCount);
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
            CollectionAssert.AreEqual(restartArguments, gateway.Request.RestartArguments.ToArray());
        }

    }

    /// <summary>
    /// journal・receiptがない清掃でも解放済み同期ロックとcurrentの元内容を保持し、
    /// 古いdownloadsだけを回収する。保持中の削除例外だけを無視する誤りを検出する。
    /// </summary>
    [TestMethod]
    public void CleanupPreviousWorkDirectoryPreservesActiveUpdaterPayload()
    {
        var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string root = workspace.RootDirectory;
        string applicationPath = Path.Combine(root, "BeMusicSeeker.exe");
        string workRoot = Path.Combine(root, "update_work");
        string currentDirectory = Path.Combine(workRoot, "current");
        string updaterPath = Path.Combine(currentDirectory, "BeMusicSeeker.Updater.exe");
        string lockPath = Path.Combine(workRoot, "update-transaction.lock");
        byte[] lockBytes = [1, 2, 3];
        byte[] updaterBytes = [4, 5, 6];
        Exception? primaryFailure = null;

        try
        {
            Directory.CreateDirectory(currentDirectory);
            Directory.CreateDirectory(Path.Combine(workRoot, "downloads"));
            File.WriteAllText(applicationPath, string.Empty);
            File.WriteAllBytes(updaterPath, updaterBytes);
            File.WriteAllBytes(lockPath, lockBytes);
            File.WriteAllText(Path.Combine(workRoot, "downloads", "stale.zip"), "stale-package");
            var service = new UpdateDownloadService(
                ApplicationPathSnapshot.FromExecutablePath(applicationPath),
                new RecordingUpdaterProcessGateway(),
                []);

            service.CleanupPreviousWorkDirectory();

            CollectionAssert.AreEqual(lockBytes, File.ReadAllBytes(lockPath));
            CollectionAssert.AreEqual(updaterBytes, File.ReadAllBytes(updaterPath));
            Assert.IsFalse(Directory.Exists(Path.Combine(workRoot, "downloads")));
            using FileStream reusedLock = File.Open(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            Assert.AreEqual((long)lockBytes.Length, reusedLock.Length);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
            throw;
        }
        finally
        {
            DeleteCleanupTestDirectory(workspace, primaryFailure);
        }
    }

    /// <summary>
    /// 未完了journalがあれば作業領域のツールへ復旧を委譲し、ツールを保持したまま古い復旧記録を処理して復旧経路の切断を検出する。
    /// </summary>
    [TestMethod]
    public void CleanupPreviousWorkDirectoryRecoversIncompleteTransactionThroughUpdaterGateway()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string root = workspace.RootDirectory;
        string applicationPath = Path.Combine(root, "BeMusicSeeker.exe");
        string workRoot = Path.Combine(root, "update_work");
        string currentDirectory = Path.Combine(workRoot, "current");
        string journalPath = Path.Combine(workRoot, "update-transaction.json");
        Directory.CreateDirectory(currentDirectory);
        File.WriteAllText(applicationPath, string.Empty);
        File.WriteAllText(Path.Combine(currentDirectory, "BeMusicSeeker.Updater.exe"), "recovery-updater");
        File.WriteAllText(journalPath, "durable-transaction");
        var gateway = new RecordingUpdaterProcessGateway();

        {
            var service = new UpdateDownloadService(
                ApplicationPathSnapshot.FromExecutablePath(applicationPath),
                gateway,
                []);

            service.CleanupPreviousWorkDirectory();

            Assert.AreEqual(
                Path.Combine(currentDirectory, "BeMusicSeeker.Updater.exe"),
                gateway.RecoveryExecutablePath);
            Assert.AreEqual(root, gateway.RecoveryApplicationDirectory);
            Assert.IsTrue(File.Exists(Path.Combine(currentDirectory, "BeMusicSeeker.Updater.exe")));
            Assert.IsFalse(File.Exists(journalPath));
        }

    }

    /// <summary>
    /// journalなし・同期ロック排他保持中も本来の失敗receiptを通知し、lock/current/未確認receiptを保持して
    /// downloadsを回収する。ロック削除失敗で原因を覆う誤りと、確認前に記録を消す誤りを検出する。
    /// </summary>
    [TestMethod]
    public void CleanupPreviousWorkDirectoryPublishesAndConsumesUpdaterFailureReceipt()
    {
        var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string root = workspace.RootDirectory;
        string applicationPath = Path.Combine(root, "BeMusicSeeker.exe");
        string workRoot = Path.Combine(root, "update_work");
        string currentDirectory = Path.Combine(workRoot, "current");
        string updaterPath = Path.Combine(currentDirectory, "BeMusicSeeker.Updater.exe");
        string lockPath = Path.Combine(workRoot, "update-transaction.lock");
        string failureReceiptPath = Path.Combine(workRoot, "update-failure.txt");
        Exception? primaryFailure = null;

        try
        {
            Directory.CreateDirectory(currentDirectory);
            Directory.CreateDirectory(Path.Combine(workRoot, "downloads"));
            File.WriteAllText(applicationPath, string.Empty);
            File.WriteAllText(updaterPath, "running-updater");
            File.WriteAllText(failureReceiptPath, "updater failure details");
            File.WriteAllText(Path.Combine(workRoot, "downloads", "stale.zip"), "stale-package");
            using FileStream heldLock = File.Open(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var service = new UpdateDownloadService(
                ApplicationPathSnapshot.FromExecutablePath(applicationPath),
                new RecordingUpdaterProcessGateway(),
                []);

            UpdateFailureReceiptException exception = Assert.ThrowsExactly<UpdateFailureReceiptException>(
                () => service.CleanupPreviousWorkDirectory());

            StringAssert.Contains(exception.Message, "updater failure details");
            Assert.IsTrue(File.Exists(lockPath));
            Assert.AreEqual("running-updater", File.ReadAllText(updaterPath));
            Assert.AreEqual("updater failure details", File.ReadAllText(failureReceiptPath));
            Assert.IsFalse(Directory.Exists(Path.Combine(workRoot, "downloads")));
            exception.Acknowledge();
            Assert.IsFalse(File.Exists(failureReceiptPath));
            Assert.IsTrue(File.Exists(lockPath));
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
            throw;
        }
        finally
        {
            DeleteCleanupTestDirectory(workspace, primaryFailure);
        }
    }

    /// <summary>
    /// 一時ファイルだけの更新失敗receiptも通知と確認後の消費へ接続し、公開途中の失敗情報を取り落とす誤りを検出する。
    /// </summary>
    [TestMethod]
    public void CleanupPreviousWorkDirectoryReadsTemporaryUpdaterFailureReceipt()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string root = workspace.RootDirectory;
        string applicationPath = Path.Combine(root, "BeMusicSeeker.exe");
        string failureReceiptPath = Path.Combine(root, "update_work", "update-failure.txt.tmp");
        Directory.CreateDirectory(Path.GetDirectoryName(failureReceiptPath)!);
        File.WriteAllText(applicationPath, string.Empty);
        File.WriteAllText(failureReceiptPath, "temporary updater failure details");

        {
            var service = new UpdateDownloadService(
                ApplicationPathSnapshot.FromExecutablePath(applicationPath),
                new RecordingUpdaterProcessGateway(),
                []);

            UpdateFailureReceiptException exception = Assert.ThrowsExactly<UpdateFailureReceiptException>(
                () => service.CleanupPreviousWorkDirectory());

            StringAssert.Contains(exception.Message, "temporary updater failure details");
            Assert.IsTrue(File.Exists(failureReceiptPath));
            exception.Acknowledge();
            Assert.IsFalse(File.Exists(failureReceiptPath));
        }

    }

    /// <summary>
    /// 指定パッケージがdownloads外なら起動準備を拒否し、検証用領域外のZIPを更新対象にする誤りを検出する。
    /// </summary>
    [TestMethod]
    public void PrepareUpdaterLaunchRejectsPackageOutsideDownloadDirectory()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string root = workspace.RootDirectory;
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

        {
            var service = new UpdateDownloadService(
                ApplicationPathSnapshot.FromExecutablePath(applicationPath),
                new RecordingUpdaterProcessGateway(),
                []);

            Assert.ThrowsExactly<InvalidOperationException>(() => service.PrepareUpdaterLaunch(Path.Combine(root, "outside.zip")));
        }

    }

    /// <summary>
    /// 固有の清掃テスト領域を回収し、回収失敗と絶対パスを元の失敗とは分けて残します。
    /// </summary>
    private static void DeleteCleanupTestDirectory(TestWorkspace workspace, Exception? primaryFailure)
    {
        string root = workspace.RootDirectory;
        try
        {
            if (Directory.Exists(root))
            {
                workspace.Dispose();
            }
        }
        catch (Exception cleanupFailure)
        {
            var diagnostic = new IOException("一時領域を回収できません: " + root, cleanupFailure);
            if (primaryFailure is null)
            {
                throw diagnostic;
            }
            primaryFailure.Data["DirectorySecondaryDiagnostics"] = diagnostic.ToString();
        }
    }
}
