using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.Update;
using Microsoft.VisualStudio.TestTools.UnitTesting;

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
        string root = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_UpdateDownloadServiceTests", Guid.NewGuid().ToString("N"));
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
        var service = new UpdateDownloadService(
            ApplicationPathSnapshot.FromExecutablePath(applicationPath),
            gateway);

        try
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
    /// journal・receiptがない清掃でも解放済み同期ロックとcurrentの元内容を保持し、
    /// 古いdownloadsだけを回収する。保持中の削除例外だけを無視する誤りを検出する。
    /// </summary>
    [TestMethod]
    public void CleanupPreviousWorkDirectoryPreservesActiveUpdaterPayload()
    {
        string root = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_UpdateDownloadServiceTests", Guid.NewGuid().ToString("N"));
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
                new RecordingUpdaterProcessGateway());

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
            DeleteCleanupTestDirectory(root, primaryFailure);
        }
    }

    /// <summary>
    /// 未完了journalがあれば作業領域のツールへ復旧を委譲し、ツールを保持したまま古い復旧記録を処理して復旧経路の切断を検出する。
    /// </summary>
    [TestMethod]
    public void CleanupPreviousWorkDirectoryRecoversIncompleteTransactionThroughUpdaterGateway()
    {
        string root = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_UpdateDownloadServiceTests", Guid.NewGuid().ToString("N"));
        string applicationPath = Path.Combine(root, "BeMusicSeeker.exe");
        string workRoot = Path.Combine(root, "update_work");
        string currentDirectory = Path.Combine(workRoot, "current");
        string journalPath = Path.Combine(workRoot, "update-transaction.json");
        Directory.CreateDirectory(currentDirectory);
        File.WriteAllText(applicationPath, string.Empty);
        File.WriteAllText(Path.Combine(currentDirectory, "BeMusicSeeker.Updater.exe"), "recovery-updater");
        File.WriteAllText(journalPath, "durable-transaction");
        var gateway = new RecordingUpdaterProcessGateway();

        try
        {
            var service = new UpdateDownloadService(
                ApplicationPathSnapshot.FromExecutablePath(applicationPath),
                gateway);

            service.CleanupPreviousWorkDirectory();

            Assert.AreEqual(
                Path.Combine(currentDirectory, "BeMusicSeeker.Updater.exe"),
                gateway.RecoveryExecutablePath);
            Assert.AreEqual(root, gateway.RecoveryApplicationDirectory);
            Assert.IsTrue(File.Exists(Path.Combine(currentDirectory, "BeMusicSeeker.Updater.exe")));
            Assert.IsFalse(File.Exists(journalPath));
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
    /// journalなし・同期ロック排他保持中も本来の失敗receiptを通知し、lock/current/未確認receiptを保持して
    /// downloadsを回収する。ロック削除失敗で原因を覆う誤りと、確認前に記録を消す誤りを検出する。
    /// </summary>
    [TestMethod]
    public void CleanupPreviousWorkDirectoryPublishesAndConsumesUpdaterFailureReceipt()
    {
        string root = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_UpdateDownloadServiceTests", Guid.NewGuid().ToString("N"));
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
                new RecordingUpdaterProcessGateway());

            UpdateFailureReceiptException exception = Assert.ThrowsException<UpdateFailureReceiptException>(
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
            DeleteCleanupTestDirectory(root, primaryFailure);
        }
    }

    /// <summary>
    /// 一時ファイルだけの更新失敗receiptも通知と確認後の消費へ接続し、公開途中の失敗情報を取り落とす誤りを検出する。
    /// </summary>
    [TestMethod]
    public void CleanupPreviousWorkDirectoryReadsTemporaryUpdaterFailureReceipt()
    {
        string root = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_UpdateDownloadServiceTests", Guid.NewGuid().ToString("N"));
        string applicationPath = Path.Combine(root, "BeMusicSeeker.exe");
        string failureReceiptPath = Path.Combine(root, "update_work", "update-failure.txt.tmp");
        Directory.CreateDirectory(Path.GetDirectoryName(failureReceiptPath)!);
        File.WriteAllText(applicationPath, string.Empty);
        File.WriteAllText(failureReceiptPath, "temporary updater failure details");

        try
        {
            var service = new UpdateDownloadService(
                ApplicationPathSnapshot.FromExecutablePath(applicationPath),
                new RecordingUpdaterProcessGateway());

            UpdateFailureReceiptException exception = Assert.ThrowsException<UpdateFailureReceiptException>(
                () => service.CleanupPreviousWorkDirectory());

            StringAssert.Contains(exception.Message, "temporary updater failure details");
            Assert.IsTrue(File.Exists(failureReceiptPath));
            exception.Acknowledge();
            Assert.IsFalse(File.Exists(failureReceiptPath));
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
    /// 実配布ツールを準備先のcurrent領域から起動してprotocol応答と正常終端を得られ、配布payloadの起動不能を検出する。
    /// </summary>
    /// <remarks>本番の自己完結payloadと実プロセスを観測するReleaseAcceptance。公開出力の指定がない通常実行では既存のInconclusiveを維持する。</remarks>
    [TestMethod]
    [TestCategory("ReleaseAcceptance")]
    public async Task PreparedUpdaterPayloadStartsFromCurrentDirectory()
    {
        string? updaterPublishOutput = Environment.GetEnvironmentVariable("BMS_SCD_UPDATER_PUBLISH_ROOT");
        if (string.IsNullOrWhiteSpace(updaterPublishOutput))
        {
            Assert.Inconclusive("Self-contained updater verification requires BMS_SCD_UPDATER_PUBLISH_ROOT.");
        }
        string root = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_UpdateDownloadServiceTests", Guid.NewGuid().ToString("N"));
        string applicationPath = Path.Combine(root, "BeMusicSeeker.exe");
        string packagePath = Path.Combine(root, "update_work", "downloads", "app.zip");
        Process? process = null;
        Task? exitTask = null;
        Task<string>? outputTask = null;
        Task<string>? errorTask = null;
        Exception? primaryFailure = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(packagePath)!);
            File.WriteAllText(applicationPath, string.Empty);
            File.WriteAllText(packagePath, "package");
            string sourcePath = Path.Combine(updaterPublishOutput!, "BeMusicSeeker.Updater.exe");
            if (!File.Exists(sourcePath))
            {
                Assert.Inconclusive("Self-contained updater payload is missing: " + sourcePath);
            }
            File.Copy(sourcePath, Path.Combine(root, "BeMusicSeeker.Updater.exe"));

            var gateway = new RecordingUpdaterProcessGateway();
            var service = new UpdateDownloadService(
                ApplicationPathSnapshot.FromExecutablePath(applicationPath),
                gateway);
            service.PrepareUpdaterLaunch(packagePath);
            string expectedWorkingDirectory = Path.Combine(root, "update_work", "current");
            string expectedExecutablePath = Path.Combine(expectedWorkingDirectory, "BeMusicSeeker.Updater.exe");
            Assert.AreEqual(expectedExecutablePath, gateway.Request.ExecutablePath);
            Assert.AreEqual(expectedWorkingDirectory, gateway.Request.WorkingDirectory);
            var startInfo = new ProcessStartInfo(expectedExecutablePath)
            {
                UseShellExecute = false,
                WorkingDirectory = expectedWorkingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("--version");
            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("The prepared updater payload did not start.");
            exitTask = process.WaitForExitAsync();
            outputTask = process.StandardOutput.ReadToEndAsync();
            errorTask = process.StandardError.ReadToEndAsync();
            try
            {
                // 実rootと両pipeのEOFで一つの既存30秒期限を共有する。
                await Task.WhenAll(exitTask, outputTask, errorTask).WaitAsync(TimeSpan.FromSeconds(30));
            }
            catch (TimeoutException)
            {
                Assert.Fail("The prepared updater payload did not exit or drain within 30 seconds.");
            }
            Assert.AreEqual(0, process.ExitCode, await errorTask);
            StringAssert.Contains(await outputTask, "1");
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }
        finally
        {
            try
            {
                if (process is not null)
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    Task[] ownedTasks = new Task?[] { exitTask, outputTask, errorTask }
                        .Where(task => task is not null).Cast<Task>().ToArray();
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
                process?.Dispose();
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
    /// currentがシンボリックリンクならツール準備を拒否し、リンク先の実sentinelを保って作業領域外の書換えを検出する。
    /// </summary>
    [TestMethod]
    [TestCategory("EnvironmentIntegration")]
    public void PrepareUpdaterLaunchRejectsReparsePointInCurrentWorkDirectory()
    {
        string root = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_UpdateDownloadServiceTests", Guid.NewGuid().ToString("N"));
        string externalDirectory = Path.Combine(root, "external");
        Directory.CreateDirectory(externalDirectory);
        File.WriteAllText(Path.Combine(externalDirectory, "sentinel.txt"), "external-sentinel");
        File.WriteAllText(Path.Combine(root, "BeMusicSeeker.exe"), string.Empty);
        foreach (string fileName in new[]
        {
            "BeMusicSeeker.Updater.exe"
        })
        {
            File.WriteAllText(Path.Combine(root, fileName), "updater");
        }
        Directory.CreateDirectory(Path.Combine(root, "update_work"));
        string currentDirectory = Path.Combine(root, "update_work", "current");
        try
        {
            Directory.CreateSymbolicLink(currentDirectory, externalDirectory);
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException || exception is PlatformNotSupportedException)
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
            Assert.Fail("The test environment does not permit directory symbolic links: " + exception.Message);
            return;
        }

        try
        {
            var service = new UpdateDownloadService(
                ApplicationPathSnapshot.FromExecutablePath(Path.Combine(root, "BeMusicSeeker.exe")),
                new RecordingUpdaterProcessGateway());

            Assert.ThrowsException<InvalidOperationException>(() => service.PrepareUpdaterLaunch(Path.Combine(root, "update_work", "downloads", "app.zip")));
            Assert.AreEqual("external-sentinel", File.ReadAllText(Path.Combine(externalDirectory, "sentinel.txt")));
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
    /// 指定パッケージがdownloads外なら起動準備を拒否し、検証用領域外のZIPを更新対象にする誤りを検出する。
    /// </summary>
    [TestMethod]
    public void PrepareUpdaterLaunchRejectsPackageOutsideDownloadDirectory()
    {
        string root = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_UpdateDownloadServiceTests", Guid.NewGuid().ToString("N"));
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

        try
        {
            var service = new UpdateDownloadService(
                ApplicationPathSnapshot.FromExecutablePath(applicationPath),
                new RecordingUpdaterProcessGateway());

            Assert.ThrowsException<InvalidOperationException>(() => service.PrepareUpdaterLaunch(Path.Combine(root, "outside.zip")));
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
    /// downloadsがシンボリックリンクなら削除を警告付きで止め、リンク先のパッケージを消す誤りを検出する。
    /// </summary>
    [TestMethod]
    [TestCategory("EnvironmentIntegration")]
    public void TryDeleteDownloadedPackageRejectsReparsePointDownloadsDirectory()
    {
        string root = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_UpdateDownloadServiceTests", Guid.NewGuid().ToString("N"));
        string externalDirectory = Path.Combine(root, "external");
        Directory.CreateDirectory(externalDirectory);
        string externalPackagePath = Path.Combine(externalDirectory, "app.zip");
        File.WriteAllText(externalPackagePath, "external-package");
        File.WriteAllText(Path.Combine(root, "BeMusicSeeker.exe"), string.Empty);
        Directory.CreateDirectory(Path.Combine(root, "update_work"));
        string downloadsDirectory = Path.Combine(root, "update_work", "downloads");
        try
        {
            Directory.CreateSymbolicLink(downloadsDirectory, externalDirectory);
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException || exception is PlatformNotSupportedException)
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
            Assert.Fail("The test environment does not permit directory symbolic links: " + exception.Message);
            return;
        }

        try
        {
            var service = new UpdateDownloadService(
                ApplicationPathSnapshot.FromExecutablePath(Path.Combine(root, "BeMusicSeeker.exe")),
                new RecordingUpdaterProcessGateway());
            Exception? warning = null;

            service.TryDeleteDownloadedPackage(Path.Combine(downloadsDirectory, "app.zip"), exception => warning = exception);

            Assert.IsNotNull(warning);
            Assert.AreEqual("external-package", File.ReadAllText(externalPackagePath));
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
    /// 固有の清掃テスト領域を回収し、回収失敗と絶対パスを元の失敗とは分けて残します。
    /// </summary>
    private static void DeleteCleanupTestDirectory(string root, Exception? primaryFailure)
    {
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
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

    private sealed class RecordingUpdaterProcessGateway : IUpdaterProcessGateway
    {
        internal UpdaterProcessLaunchRequest Request { get; private set; } = null!;

        internal string RecoveryExecutablePath { get; private set; } = null!;

        internal string RecoveryApplicationDirectory { get; private set; } = null!;

        internal FakePreparedUpdaterLaunch PreparedLaunch { get; } = new();

        public IPreparedUpdaterLaunch Prepare(UpdaterProcessLaunchRequest request)
        {
            Request = request;
            return PreparedLaunch;
        }

        public bool RecoverIncompleteTransaction(string executablePath, string applicationDirectory)
        {
            RecoveryExecutablePath = executablePath;
            RecoveryApplicationDirectory = applicationDirectory;
            return true;
        }
    }

    private sealed class FakePreparedUpdaterLaunch : IPreparedUpdaterLaunch
    {
        internal int StartCount { get; private set; }

        public UpdaterLaunchReceipt Start()
        {
            StartCount++;
            return new();
        }
    }

}
