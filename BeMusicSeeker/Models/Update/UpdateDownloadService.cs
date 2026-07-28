using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.Update;

internal sealed class UpdateDownloadService
{
    private const int DownloadTimeoutMilliseconds = 300000;
    private const string UpdateFailureReceiptFileName = "update-failure.txt";
    private const string UpdateFailureReceiptTemporaryFileName = "update-failure.txt.tmp";
    private const string TransactionJournalFileName = "update-transaction.json";
    private const string UpdaterReadyFileName = "updater-ready.txt";
    private const string UpdaterDecisionFileName = "updater-decision.txt";
    private const string UpdaterExecutableFileName = "BeMusicSeeker.Updater.exe";
    private static readonly string[] UpdaterPayloadFileNames =
    [
        "BeMusicSeeker.Updater.exe"
    ];

    private static readonly string[] LegacyUpdaterPayloadFileNames =
    [
        "BeMusicSeeker.Updater.dll",
        "BeMusicSeeker.Updater.deps.json",
        "BeMusicSeeker.Updater.runtimeconfig.json"
    ];

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
        EnsureNoReparsePointAncestors(applicationPathSnapshot.BaseDirectory, "update_work/downloads");
        EnsureNoReparsePointIfPresent(workRoot);
        if (LongPathFileSystem.DirectoryExists(workRoot))
        {
            EnsureNoReparsePointTree(workRoot);
        }
        EnsureNoReparsePointIfPresent(downloadsDirectory);
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
        EnsureDownloadedPackagePath(packagePath);
        string currentUpdaterDirectory = Path.Combine(GetUpdateWorkRoot(), "current");
        EnsureNoReparsePointAncestors(appDirectory, "update_work/current");
        EnsureNoReparsePointIfPresent(currentUpdaterDirectory);
        if (LongPathFileSystem.DirectoryExists(currentUpdaterDirectory))
        {
            EnsureNoReparsePointTree(currentUpdaterDirectory);
        }
        LongPathFileSystem.CreateDirectory(currentUpdaterDirectory);
        string readyFilePath = Path.Combine(currentUpdaterDirectory, UpdaterReadyFileName);
        string decisionFilePath = Path.Combine(currentUpdaterDirectory, UpdaterDecisionFileName);
        EnsureNoReparsePointIfPresent(readyFilePath);
        if (LongPathFileSystem.FileExists(readyFilePath))
        {
            LongPathFileSystem.DeleteFile(readyFilePath);
        }
        EnsureNoReparsePointIfPresent(decisionFilePath);
        if (LongPathFileSystem.FileExists(decisionFilePath))
        {
            LongPathFileSystem.DeleteFile(decisionFilePath);
        }
        foreach (string legacyPayloadFileName in LegacyUpdaterPayloadFileNames)
        {
            string legacyPayloadPath = Path.Combine(currentUpdaterDirectory, legacyPayloadFileName);
            EnsureNoReparsePointIfPresent(legacyPayloadPath);
            if (LongPathFileSystem.FileExists(legacyPayloadPath))
            {
                LongPathFileSystem.DeleteFile(legacyPayloadPath);
            }
        }
        string updaterPath = null;
        foreach (string payloadFileName in UpdaterPayloadFileNames)
        {
            string sourcePath = Path.Combine(appDirectory, payloadFileName);
            if (!LongPathFileSystem.FileExists(sourcePath))
            {
                throw new FileNotFoundException("Updater payload file was not found.", sourcePath);
            }

            string destinationPath = Path.Combine(currentUpdaterDirectory, payloadFileName);
            EnsureNoReparsePointIfPresent(destinationPath);
            LongPathFileSystem.CopyFile(sourcePath, destinationPath, overwrite: true);
            if (string.Equals(payloadFileName, "BeMusicSeeker.Updater.exe", StringComparison.OrdinalIgnoreCase))
            {
                updaterPath = destinationPath;
            }
        }

        return updaterProcessGateway.Prepare(
            UpdaterProcessLaunchRequest.Create(
                updaterPath,
                currentUpdaterDirectory,
                appDirectory,
                packagePath,
                Path.Combine(appDirectory, "update_backup"),
                readyFilePath,
                decisionFilePath,
                applicationPathSnapshot.ExecutablePath));
    }

    internal string GetUpdateWorkRoot()
    {
        return Path.Combine(applicationPathSnapshot.BaseDirectory, "update_work");
    }

    internal void CleanupPreviousWorkDirectory()
    {
        string workRoot = GetUpdateWorkRoot();
        EnsureNoReparsePointIfPresent(workRoot);
        if (!LongPathFileSystem.DirectoryExists(workRoot))
        {
            return;
        }

        EnsureNoReparsePointTree(workRoot);
        if (!RecoverIncompleteTransaction(workRoot))
        {
            return;
        }
        UpdateFailureReceiptException failureReceipt = ReadFailureReceipt(workRoot);

        foreach (string child in LongPathFileSystem.EnumerateFileSystemEntries(workRoot))
        {
            string childName = Path.GetFileName(child.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (LongPathFileSystem.DirectoryExists(child)
                && string.Equals(childName, "current", StringComparison.OrdinalIgnoreCase))
            {
                // The updater process is launched from this directory and may still hold
                // its payload open while the restarted application is initializing.
                // Leave the payload in place; it is overwritten on the next launch.
                continue;
            }
            if (string.Equals(childName, UpdateFailureReceiptFileName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(childName, UpdateFailureReceiptTemporaryFileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            EnsureNoReparsePointTree(child);
            if (LongPathFileSystem.DirectoryExists(child))
            {
                LongPathFileSystem.DeleteDirectory(child, recursive: true);
            }
            else if (LongPathFileSystem.FileExists(child))
            {
                LongPathFileSystem.DeleteFile(child);
            }
        }

        if (failureReceipt != null)
        {
            throw failureReceipt;
        }
    }

    private bool RecoverIncompleteTransaction(string workRoot)
    {
        string journalPath = Path.Combine(workRoot, TransactionJournalFileName);
        EnsureNoReparsePointIfPresent(journalPath);
        if (!LongPathFileSystem.FileExists(journalPath))
        {
            return true;
        }

        string currentUpdaterPath = Path.Combine(workRoot, "current", UpdaterExecutableFileName);
        EnsureNoReparsePointIfPresent(currentUpdaterPath);
        string updaterPath = currentUpdaterPath;
        if (!LongPathFileSystem.FileExists(updaterPath))
        {
            updaterPath = Path.Combine(applicationPathSnapshot.BaseDirectory, UpdaterExecutableFileName);
            EnsureNoReparsePointIfPresent(updaterPath);
        }
        if (!LongPathFileSystem.FileExists(updaterPath))
        {
            throw new FileNotFoundException(
                "An updater executable is required to recover an incomplete update transaction.",
                updaterPath);
        }

        return updaterProcessGateway.RecoverIncompleteTransaction(
            updaterPath,
            applicationPathSnapshot.BaseDirectory);
    }

    private UpdateFailureReceiptException ReadFailureReceipt(string workRoot)
    {
        string receiptPath = Path.Combine(workRoot, UpdateFailureReceiptFileName);
        if (!LongPathFileSystem.FileExists(receiptPath))
        {
            receiptPath = Path.Combine(workRoot, UpdateFailureReceiptTemporaryFileName);
            if (!LongPathFileSystem.FileExists(receiptPath))
            {
                return null;
            }
        }

        EnsureNoReparsePointIfPresent(receiptPath);
        string failureDetails = Encoding.UTF8.GetString(LongPathFileSystem.ReadAllBytes(receiptPath)).Trim();
        return new UpdateFailureReceiptException(
            "The previous update failed before the application could restart. "
            + (string.IsNullOrWhiteSpace(failureDetails) ? "No failure details were recorded." : failureDetails),
            () => AcknowledgeFailureReceipt(workRoot));
    }

    private void AcknowledgeFailureReceipt(string workRoot)
    {
        EnsureNoReparsePointAncestors(applicationPathSnapshot.BaseDirectory, "update_work/update-failure.txt");
        EnsureNoReparsePointIfPresent(workRoot);
        foreach (string fileName in new[] { UpdateFailureReceiptFileName, UpdateFailureReceiptTemporaryFileName })
        {
            string path = Path.Combine(workRoot, fileName);
            EnsureNoReparsePointIfPresent(path);
            if (LongPathFileSystem.FileExists(path))
            {
                LongPathFileSystem.DeleteFile(path);
            }
        }
    }

    private static void EnsureNoReparsePointTree(string path)
    {
        EnsureNoReparsePointEntry(path);
        if (!LongPathFileSystem.DirectoryExists(path))
        {
            return;
        }

        foreach (string child in LongPathFileSystem.EnumerateFileSystemEntries(path))
        {
            EnsureNoReparsePointTree(child);
        }
    }

    private static void EnsureNoReparsePointAncestors(string rootDirectory, string relativePath)
    {
        string normalized = relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        string[] segments = normalized.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        string current = rootDirectory;
        for (int i = 0; i < segments.Length - 1; i++)
        {
            current = Path.Combine(current, segments[i]);
            EnsureNoReparsePointIfPresent(current);
        }
    }

    private static void EnsureNoReparsePointIfPresent(string path)
    {
        try
        {
            EnsureNoReparsePointEntry(path);
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private static void EnsureNoReparsePointEntry(string path)
    {
        FileAttributes attributes = LongPathFileSystem.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("Reparse points are not supported in the update work directory: " + path);
        }
    }

    private void EnsureDownloadedPackagePath(string packagePath)
    {
        string downloadsDirectory = Path.Combine(GetUpdateWorkRoot(), "downloads");
        string normalizedPackagePath = LongPathFileSystem.NormalizePathForStorage(packagePath);
        string normalizedDownloadsDirectory = LongPathFileSystem.TrimTrailingDirectorySeparators(
            LongPathFileSystem.NormalizePathForStorage(downloadsDirectory));
        if (string.Equals(normalizedPackagePath, normalizedDownloadsDirectory, StringComparison.OrdinalIgnoreCase)
            || !LongPathFileSystem.IsSameOrDescendantDirectoryPath(normalizedPackagePath, normalizedDownloadsDirectory))
        {
            throw new InvalidOperationException("The update package must be under update_work/downloads.");
        }

        EnsureNoReparsePointAncestors(applicationPathSnapshot.BaseDirectory, "update_work/downloads");
        EnsureNoReparsePointIfPresent(downloadsDirectory);
        if (LongPathFileSystem.DirectoryExists(downloadsDirectory))
        {
            EnsureNoReparsePointTree(downloadsDirectory);
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
            EnsureDownloadedPackagePath(packagePath);
            EnsureNoReparsePointIfPresent(packagePath);
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
