using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading.Tasks;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.Update;

internal sealed class UpdateDownloadService
{
    private const int DownloadTimeoutMilliseconds = 300000;

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

    public PreparedUpdaterLaunch PrepareUpdaterLaunch(string packagePath)
    {
        string appDirectory = AppDomain.CurrentDomain.BaseDirectory;
        string sourceUpdaterPath = Path.Combine(appDirectory, "BeMusicSeeker.Updater.exe");
        if (!LongPathFileSystem.FileExists(sourceUpdaterPath))
        {
            throw new FileNotFoundException("Updater executable was not found.", sourceUpdaterPath);
        }

        string currentUpdaterDirectory = Path.Combine(GetUpdateWorkRoot(), "current");
        LongPathFileSystem.CreateDirectory(currentUpdaterDirectory);
        string updaterPath = Path.Combine(currentUpdaterDirectory, "BeMusicSeeker.Updater.exe");
        LongPathFileSystem.CopyFile(sourceUpdaterPath, updaterPath, overwrite: true);

        string appExePath = Assembly.GetExecutingAssembly().Location;
        ProcessStartInfo startInfo = new ProcessStartInfo(updaterPath)
        {
            UseShellExecute = false,
            WorkingDirectory = currentUpdaterDirectory,
            Arguments = JoinArguments(
                "--app-dir", appDirectory,
                "--package", packagePath,
                "--backup-dir", Path.Combine(appDirectory, "update_backup"),
                "--pid", Process.GetCurrentProcess().Id.ToString(),
                "--restart-exe", appExePath)
        };
        return new PreparedUpdaterLaunch(startInfo);
    }

    internal static string GetUpdateWorkRoot()
    {
        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "update_work");
    }

    internal static void CleanupPreviousWorkDirectory()
    {
        string workRoot = GetUpdateWorkRoot();
        if (LongPathFileSystem.DirectoryExists(workRoot))
        {
            LongPathFileSystem.DeleteDirectory(workRoot, recursive: true);
        }
    }

    internal static void TryDeleteDownloadedPackage(string packagePath, Action<Exception> warningReporter = null)
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

    private static string JoinArguments(params string[] values)
    {
        string[] arguments = new string[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            arguments[i] = QuoteArgument(values[i]);
        }
        return string.Join(" ", arguments);
    }

    private static string QuoteArgument(string value)
    {
        value ??= string.Empty;
        if (value.Length == 0)
        {
            return "\"\"";
        }

        var quoted = new System.Text.StringBuilder();
        quoted.Append('"');
        int backslashCount = 0;
        foreach (char c in value)
        {
            if (c == '\\')
            {
                backslashCount++;
                continue;
            }

            if (c == '"')
            {
                quoted.Append('\\', backslashCount * 2 + 1);
                quoted.Append('"');
                backslashCount = 0;
                continue;
            }

            quoted.Append('\\', backslashCount);
            backslashCount = 0;
            quoted.Append(c);
        }

        quoted.Append('\\', backslashCount * 2);
        quoted.Append('"');
        return quoted.ToString();
    }
}
