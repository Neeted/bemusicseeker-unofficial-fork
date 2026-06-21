using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading.Tasks;

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
        Directory.CreateDirectory(downloadsDirectory);

        string fileName = Path.GetFileName(asset.FileName);
        if (string.IsNullOrWhiteSpace(fileName) || fileName != asset.FileName)
        {
            throw new UpdateManifestValidationException("Invalid update package file name.");
        }

        string packagePath = Path.Combine(downloadsDirectory, fileName);
        string temporaryPath = packagePath + ".download";
        if (File.Exists(temporaryPath))
        {
            File.Delete(temporaryPath);
        }

        using (var httpClient = new HttpClient { Timeout = TimeSpan.FromMilliseconds(DownloadTimeoutMilliseconds) })
        using (HttpResponseMessage response = await httpClient.GetAsync(asset.Url).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            using Stream source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using FileStream destination = File.Create(temporaryPath);
            await source.CopyToAsync(destination).ConfigureAwait(false);
        }

        FileInfo downloaded = new(temporaryPath);
        if (downloaded.Length != asset.SizeBytes)
        {
            File.Delete(temporaryPath);
            throw new InvalidOperationException("Downloaded update package size does not match update.json.");
        }

        string actualSha256 = ComputeSha256(temporaryPath);
        if (!string.Equals(actualSha256, asset.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(temporaryPath);
            throw new InvalidOperationException("Downloaded update package SHA-256 does not match update.json.");
        }

        if (File.Exists(packagePath))
        {
            File.Delete(packagePath);
        }
        File.Move(temporaryPath, packagePath);
        return packagePath;
    }

    public Process StartUpdater(string packagePath)
    {
        string appDirectory = AppDomain.CurrentDomain.BaseDirectory;
        string sourceUpdaterPath = Path.Combine(appDirectory, "BeMusicSeeker.Updater.exe");
        if (!File.Exists(sourceUpdaterPath))
        {
            throw new FileNotFoundException("Updater executable was not found.", sourceUpdaterPath);
        }

        string currentUpdaterDirectory = Path.Combine(GetUpdateWorkRoot(), "current");
        Directory.CreateDirectory(currentUpdaterDirectory);
        string updaterPath = Path.Combine(currentUpdaterDirectory, "BeMusicSeeker.Updater.exe");
        File.Copy(sourceUpdaterPath, updaterPath, overwrite: true);

        string appExePath = Assembly.GetExecutingAssembly().Location;
        var startInfo = new ProcessStartInfo(updaterPath)
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

        return Process.Start(startInfo);
    }

    internal static string GetUpdateWorkRoot()
    {
        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "update_work");
    }

    internal static void CleanupPreviousWorkDirectory()
    {
        string workRoot = GetUpdateWorkRoot();
        if (Directory.Exists(workRoot))
        {
            Directory.Delete(workRoot, recursive: true);
        }
    }

    private static string ComputeSha256(string path)
    {
        using SHA256 sha256 = SHA256.Create();
        using FileStream stream = File.OpenRead(path);
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
