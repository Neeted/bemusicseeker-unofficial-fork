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

            Assert.ThrowsExactly<InvalidOperationException>(() => service.PrepareUpdaterLaunch(Path.Combine(root, "update_work", "downloads", "app.zip")));
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



}
