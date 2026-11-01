using System;
using System.IO;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.Update;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class UpdateDownloadServiceTests
{

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
