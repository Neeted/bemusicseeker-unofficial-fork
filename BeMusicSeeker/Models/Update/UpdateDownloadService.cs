using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.Update;

internal sealed class UpdateDownloadService
{
    private const int DownloadTimeoutMilliseconds = 300000;

    private readonly ApplicationPathSnapshot applicationPathSnapshot;

    private readonly IUpdaterProcessGateway updaterProcessGateway;

    internal UpdateDownloadService(
        ApplicationPathSnapshot applicationPathSnapshot,
        IUpdaterProcessGateway updaterProcessGateway)
    {
        this.applicationPathSnapshot = applicationPathSnapshot
            ?? throw new ArgumentNullException(nameof(applicationPathSnapshot));
        this.updaterProcessGateway = updaterProcessGateway
            ?? throw new ArgumentNullException(nameof(updaterProcessGateway));
    }

    public async Task<string> DownloadAndVerifyAsync(UpdateAssetInfo asset)
    {
        if (asset == null)
        {
            throw new ArgumentNullException(nameof(asset));
        }

        string workRoot = GetUpdateWorkRoot();
        string downloadsDirectory = Path.Combine(workRoot, "downloads");
        LongPathFileSystem.CreateDirectory(downloadsDirectory);

        string fileName = Path.GetFileName(asset.FileName);
        if (string.IsNullOrWhiteSpace(fileName) || fileName != asset.FileName)
        {
            throw new UpdateManifestValidationException("Invalid update package file name.");
        }

        string packagePath = Path.Combine(downloadsDirectory, fileName);
        string temporaryPath = packagePath + ".download";
        if (LongPathFileSystem.FileExists(temporaryPath))
        {
            LongPathFileSystem.DeleteFile(temporaryPath);
        }

        using (var httpClient = new HttpClient { Timeout = TimeSpan.FromMilliseconds(DownloadTimeoutMilliseconds) })
        using (HttpResponseMessage response = await httpClient.GetAsync(asset.Url).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            using Stream source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using FileStream destination = LongPathFileSystem.Open(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await source.CopyToAsync(destination).ConfigureAwait(false);
        }

        if (LongPathFileSystem.GetFileMetadata(temporaryPath).Length != asset.SizeBytes)
        {
            LongPathFileSystem.DeleteFile(temporaryPath);
            throw new InvalidOperationException("Downloaded update package size does not match update.json.");
        }

        string actualSha256 = ComputeSha256(temporaryPath);
        if (!string.Equals(actualSha256, asset.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            LongPathFileSystem.DeleteFile(temporaryPath);
            throw new InvalidOperationException("Downloaded update package SHA-256 does not match update.json.");
        }

        LongPathFileSystem.MoveFile(temporaryPath, packagePath, overwrite: true);
        return packagePath;
    }

    public IPreparedUpdaterLaunch PrepareUpdaterLaunch(string packagePath)
    {
        if (string.IsNullOrWhiteSpace(packagePath))
        {
            throw new ArgumentException("An update package path is required.", nameof(packagePath));
        }

        string appDirectory = applicationPathSnapshot.BaseDirectory;
        string sourceUpdaterPath = Path.Combine(appDirectory, "BeMusicSeeker.Updater.exe");
        if (!LongPathFileSystem.FileExists(sourceUpdaterPath))
        {
            throw new FileNotFoundException("Updater executable was not found.", sourceUpdaterPath);
        }

        string currentUpdaterDirectory = Path.Combine(GetUpdateWorkRoot(), "current");
        LongPathFileSystem.CreateDirectory(currentUpdaterDirectory);
        string updaterPath = Path.Combine(currentUpdaterDirectory, "BeMusicSeeker.Updater.exe");
        LongPathFileSystem.CopyFile(sourceUpdaterPath, updaterPath, overwrite: true);

        return updaterProcessGateway.Prepare(
            UpdaterProcessLaunchRequest.Create(
                updaterPath,
                currentUpdaterDirectory,
                appDirectory,
                packagePath,
                Path.Combine(appDirectory, "update_backup"),
                applicationPathSnapshot.ExecutablePath));
    }

    internal string GetUpdateWorkRoot()
    {
        return Path.Combine(applicationPathSnapshot.BaseDirectory, "update_work");
    }

    internal void CleanupPreviousWorkDirectory()
    {
        string workRoot = GetUpdateWorkRoot();
        if (LongPathFileSystem.DirectoryExists(workRoot))
        {
            LongPathFileSystem.DeleteDirectory(workRoot, recursive: true);
        }
    }

    internal void TryDeleteDownloadedPackage(string packagePath, Action<Exception> warningReporter = null)
    {
        if (string.IsNullOrWhiteSpace(packagePath))
        {
            return;
        }
        try
        {
            if (LongPathFileSystem.FileExists(packagePath))
            {
                LongPathFileSystem.DeleteFile(packagePath);
            }
        }
        catch (Exception exception)
        {
            warningReporter?.Invoke(exception);
        }
    }

    private static string ComputeSha256(string path)
    {
        using SHA256 sha256 = SHA256.Create();
        using FileStream stream = LongPathFileSystem.OpenRead(path);
        return BitConverter.ToString(sha256.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
    }

}
