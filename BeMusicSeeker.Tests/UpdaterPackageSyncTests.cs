using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class UpdaterPackageSyncTests
{
    [TestMethod]
    public void ApplyUpdate_RemovesLegacyDllLayoutWithoutPreviousManifest()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string appDirectoryPath = Path.Combine(tempDirectoryPath, "app");
            string packageSourceDirectoryPath = Path.Combine(tempDirectoryPath, "package-source");
            string packagePath = Path.Combine(tempDirectoryPath, "package.zip");
            string backupDirectoryPath = Path.Combine(appDirectoryPath, "update_backup");

            WriteTextFile(appDirectoryPath, "BeMusicSeeker.exe", "old-app");
            WriteTextFile(appDirectoryPath, "libs/SevenZipExtractor.dll", "old-sevenzip");
            WriteTextFile(appDirectoryPath, "libs/x64/7z.dll", "old-7z-native");
            WriteTextFile(appDirectoryPath, "libs/x64/OggVorbis.NET64.dll", "old-wrong-ogv");
            WriteTextFile(appDirectoryPath, "libs/x86/7z.dll", "old-7z-x86");
            WriteTextFile(appDirectoryPath, "x86/sqlite3.dll", "old-root-x86");
            WriteTextFile(appDirectoryPath, "config/user.config", "user-config");

            WriteTextFile(packageSourceDirectoryPath, "BeMusicSeeker.exe", "new-app");
            WriteTextFile(packageSourceDirectoryPath, "BeMusicSeeker.exe.config", "new-config");
            WriteTextFile(packageSourceDirectoryPath, "BeMusicSeeker.Updater.exe", "new-updater");
            WriteTextFile(packageSourceDirectoryPath, "libs/SevenZipExtractor.dll", "new-sevenzip");
            WriteTextFile(packageSourceDirectoryPath, "libs/OggVorbis.NET64.dll", "new-ogv");
            WriteTextFile(packageSourceDirectoryPath, "libs/x64/7z.dll", "new-7z-native");
            WriteTextFile(packageSourceDirectoryPath, "native/EverythingBridge_x64.dll", "new-bridge");
            WriteTextFile(packageSourceDirectoryPath, "lang/ja-JP.json", "{}");
            WriteTextFile(packageSourceDirectoryPath, "update-managed-files.txt", string.Join(Environment.NewLine, new[]
            {
                "BeMusicSeeker.exe",
                "BeMusicSeeker.exe.config",
                "BeMusicSeeker.Updater.exe",
                "libs/SevenZipExtractor.dll",
                "libs/OggVorbis.NET64.dll",
                "libs/x64/7z.dll",
                "native/EverythingBridge_x64.dll",
                "lang/ja-JP.json"
            }));
            ZipFile.CreateFromDirectory(packageSourceDirectoryPath, packagePath);

            RunUpdater(appDirectoryPath, packagePath, backupDirectoryPath);

            Assert.AreEqual("new-app", File.ReadAllText(Path.Combine(appDirectoryPath, "BeMusicSeeker.exe")));
            Assert.AreEqual("new-ogv", File.ReadAllText(Path.Combine(appDirectoryPath, "libs", "OggVorbis.NET64.dll")));
            Assert.AreEqual("new-7z-native", File.ReadAllText(Path.Combine(appDirectoryPath, "libs", "x64", "7z.dll")));
            Assert.IsFalse(File.Exists(Path.Combine(appDirectoryPath, "libs", "x64", "OggVorbis.NET64.dll")));
            Assert.IsFalse(Directory.Exists(Path.Combine(appDirectoryPath, "libs", "x86")));
            Assert.IsFalse(Directory.Exists(Path.Combine(appDirectoryPath, "x86")));
            Assert.AreEqual("user-config", File.ReadAllText(Path.Combine(appDirectoryPath, "config", "user.config")));

            string[] managedManifestLines = File.ReadAllLines(Path.Combine(appDirectoryPath, "update-managed-files.txt"));
            CollectionAssert.Contains(managedManifestLines, "libs\\OggVorbis.NET64.dll");
            CollectionAssert.Contains(managedManifestLines, "libs\\x64\\7z.dll");
            CollectionAssert.DoesNotContain(managedManifestLines, "libs\\x64\\OggVorbis.NET64.dll");
        });
    }

    [TestMethod]
    public void ApplyUpdate_RemovesAppManagedMetadataArtifactsWhenPackageHasNoMetadata()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string appDirectoryPath = Path.Combine(tempDirectoryPath, "app");
            string packageSourceDirectoryPath = Path.Combine(tempDirectoryPath, "package-source");
            string packagePath = Path.Combine(tempDirectoryPath, "package.zip");
            string backupDirectoryPath = Path.Combine(appDirectoryPath, "update_backup");

            WriteTextFile(appDirectoryPath, "BeMusicSeeker.exe", "old-app");
            WriteTextFile(appDirectoryPath, "chart-info-metadata.7z", "old-root-archive");
            WriteTextFile(appDirectoryPath, "chart-info-metadata.db", "old-root-db");
            WriteTextFile(appDirectoryPath, "imported_metadata/chart-info-metadata.7z", "old-imported-archive");
            WriteTextFile(appDirectoryPath, "imported_metadata/chart-info-metadata.aaaaaaaaaaaa.7z", "old-history-archive");

            WriteTextFile(packageSourceDirectoryPath, "BeMusicSeeker.exe", "new-app");
            WriteTextFile(packageSourceDirectoryPath, "update-managed-files.txt", "BeMusicSeeker.exe");
            ZipFile.CreateFromDirectory(packageSourceDirectoryPath, packagePath);

            RunUpdater(appDirectoryPath, packagePath, backupDirectoryPath);

            Assert.AreEqual("new-app", File.ReadAllText(Path.Combine(appDirectoryPath, "BeMusicSeeker.exe")));
            Assert.IsFalse(File.Exists(Path.Combine(appDirectoryPath, "chart-info-metadata.7z")));
            Assert.IsFalse(File.Exists(Path.Combine(appDirectoryPath, "chart-info-metadata.db")));
            Assert.IsFalse(Directory.Exists(Path.Combine(appDirectoryPath, "imported_metadata")));
        });
    }

    [TestMethod]
    public void ApplyUpdate_ReplacesAppManagedMetadataArtifactsWithBundledArchive()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string appDirectoryPath = Path.Combine(tempDirectoryPath, "app");
            string packageSourceDirectoryPath = Path.Combine(tempDirectoryPath, "package-source");
            string packagePath = Path.Combine(tempDirectoryPath, "package.zip");
            string backupDirectoryPath = Path.Combine(appDirectoryPath, "update_backup");

            WriteTextFile(appDirectoryPath, "BeMusicSeeker.exe", "old-app");
            WriteTextFile(appDirectoryPath, "chart-info-metadata.7z", "old-root-archive");
            WriteTextFile(appDirectoryPath, "chart-info-metadata.db", "old-root-db");
            WriteTextFile(appDirectoryPath, "imported_metadata/chart-info-metadata.7z", "old-imported-archive");
            WriteTextFile(appDirectoryPath, "imported_metadata/chart-info-metadata.bbbbbbbbbbbb.7z", "old-history-archive");

            WriteTextFile(packageSourceDirectoryPath, "BeMusicSeeker.exe", "new-app");
            WriteTextFile(packageSourceDirectoryPath, "chart-info-metadata.7z", "new-bundled-archive");
            WriteTextFile(packageSourceDirectoryPath, "update-managed-files.txt", string.Join(Environment.NewLine, new[]
            {
                "BeMusicSeeker.exe",
                "chart-info-metadata.7z"
            }));
            ZipFile.CreateFromDirectory(packageSourceDirectoryPath, packagePath);

            RunUpdater(appDirectoryPath, packagePath, backupDirectoryPath);

            Assert.AreEqual("new-app", File.ReadAllText(Path.Combine(appDirectoryPath, "BeMusicSeeker.exe")));
            Assert.AreEqual("new-bundled-archive", File.ReadAllText(Path.Combine(appDirectoryPath, "chart-info-metadata.7z")));
            Assert.IsFalse(File.Exists(Path.Combine(appDirectoryPath, "chart-info-metadata.db")));
            Assert.IsFalse(Directory.Exists(Path.Combine(appDirectoryPath, "imported_metadata")));
        });
    }

    [TestMethod]
    public void ApplyUpdate_RollbackRestoresAppManagedMetadataArtifactsAfterFailure()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string appDirectoryPath = Path.Combine(tempDirectoryPath, "app");
            string packageSourceDirectoryPath = Path.Combine(tempDirectoryPath, "package-source");
            string packagePath = Path.Combine(tempDirectoryPath, "package.zip");
            string backupDirectoryPath = Path.Combine(appDirectoryPath, "update_backup");

            WriteTextFile(appDirectoryPath, "BeMusicSeeker.exe", "old-app");
            WriteTextFile(appDirectoryPath, "chart-info-metadata.7z", "old-root-archive");
            WriteTextFile(appDirectoryPath, "chart-info-metadata.db", "old-root-db");
            WriteTextFile(appDirectoryPath, "imported_metadata/chart-info-metadata.7z", "old-imported-archive");
            WriteTextFile(appDirectoryPath, "blocked", "existing-file");

            WriteTextFile(packageSourceDirectoryPath, "BeMusicSeeker.exe", "new-app");
            WriteTextFile(packageSourceDirectoryPath, "blocked/file.txt", "copy-should-fail");
            WriteTextFile(packageSourceDirectoryPath, "update-managed-files.txt", string.Join(Environment.NewLine, new[]
            {
                "BeMusicSeeker.exe",
                "blocked/file.txt"
            }));
            ZipFile.CreateFromDirectory(packageSourceDirectoryPath, packagePath);

            RunUpdaterExpectFailure(appDirectoryPath, packagePath, backupDirectoryPath);

            Assert.AreEqual("old-app", File.ReadAllText(Path.Combine(appDirectoryPath, "BeMusicSeeker.exe")));
            Assert.AreEqual("old-root-archive", File.ReadAllText(Path.Combine(appDirectoryPath, "chart-info-metadata.7z")));
            Assert.AreEqual("old-root-db", File.ReadAllText(Path.Combine(appDirectoryPath, "chart-info-metadata.db")));
            Assert.AreEqual("old-imported-archive", File.ReadAllText(Path.Combine(appDirectoryPath, "imported_metadata", "chart-info-metadata.7z")));
            Assert.AreEqual("existing-file", File.ReadAllText(Path.Combine(appDirectoryPath, "blocked")));
        });
    }

    private static void RunUpdater(string appDirectoryPath, string packagePath, string backupDirectoryPath)
    {
        using Process process = StartUpdater(appDirectoryPath, packagePath, backupDirectoryPath);
        if (!process.WaitForExit(30000))
        {
            process.Kill();
            Assert.Fail("Updater process timed out.");
        }

        string standardOutput = process.StandardOutput.ReadToEnd();
        string standardError = process.StandardError.ReadToEnd();
        if (process.ExitCode != 0)
        {
            Assert.Fail("Updater failed with exit code " + process.ExitCode + Environment.NewLine + standardOutput + Environment.NewLine + standardError);
        }
    }

    private static void RunUpdaterExpectFailure(string appDirectoryPath, string packagePath, string backupDirectoryPath)
    {
        using Process process = StartUpdater(appDirectoryPath, packagePath, backupDirectoryPath);
        if (!process.WaitForExit(30000))
        {
            process.Kill();
            Assert.Fail("Updater process timed out.");
        }

        if (process.ExitCode == 0)
        {
            string standardOutput = process.StandardOutput.ReadToEnd();
            string standardError = process.StandardError.ReadToEnd();
            Assert.Fail("Updater unexpectedly succeeded." + Environment.NewLine + standardOutput + Environment.NewLine + standardError);
        }
    }

    private static Process StartUpdater(string appDirectoryPath, string packagePath, string backupDirectoryPath)
    {
        string updaterPath = FindUpdaterExecutable();
        var processStartInfo = new ProcessStartInfo
        {
            FileName = updaterPath,
            Arguments = string.Join(" ", new[]
            {
                "--app-dir",
                appDirectoryPath,
                "--package",
                packagePath,
                "--backup-dir",
                backupDirectoryPath,
                "--pid",
                "0"
            }.Select(QuoteArgument)),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(updaterPath) ?? Environment.CurrentDirectory
        };

        return Process.Start(processStartInfo) ?? throw new InvalidOperationException("Updater process was not started.");
    }

    private static string FindUpdaterExecutable()
    {
        string repositoryRoot = FindRepositoryRoot();
        var frameworkDirectory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        string targetFramework = frameworkDirectory.Name;
        string configuration = frameworkDirectory.Parent?.Name ?? "Debug";
        string platform = frameworkDirectory.Parent?.Parent?.Name ?? "x64";

        string[] candidates =
        [
            Path.Combine(repositoryRoot, "BeMusicSeeker.Updater", "bin", platform, configuration, targetFramework, "BeMusicSeeker.Updater.exe"),
            Path.Combine(repositoryRoot, "BeMusicSeeker.Updater", "bin", configuration, targetFramework, "BeMusicSeeker.Updater.exe")
        ];
        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("Updater executable was not found.", candidates[0]);
    }

    private static string FindRepositoryRoot()
    {
        string? directoryPath = AppDomain.CurrentDomain.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(directoryPath))
        {
            if (File.Exists(Path.Combine(directoryPath, "BeMusicSeeker.csproj")))
            {
                return directoryPath!;
            }

            DirectoryInfo? parent = Directory.GetParent(directoryPath);
            directoryPath = parent?.FullName;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private static void WriteTextFile(string rootDirectoryPath, string relativePath, string contents)
    {
        string filePath = Path.Combine(rootDirectoryPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
        string? directoryPath = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
        }

        File.WriteAllText(filePath, contents);
    }

    private static string QuoteArgument(string argument)
    {
        if (string.IsNullOrEmpty(argument))
        {
            return "\"\"";
        }

        if (!argument.Any(char.IsWhiteSpace) && !argument.Contains("\""))
        {
            return argument;
        }

        var builder = new System.Text.StringBuilder();
        builder.Append('"');
        int backslashCount = 0;
        foreach (char c in argument)
        {
            if (c == '\\')
            {
                backslashCount++;
                continue;
            }

            if (c == '"')
            {
                builder.Append('\\', backslashCount * 2 + 1);
                builder.Append('"');
                backslashCount = 0;
                continue;
            }

            builder.Append('\\', backslashCount);
            backslashCount = 0;
            builder.Append(c);
        }

        builder.Append('\\', backslashCount * 2);
        builder.Append('"');
        return builder.ToString();
    }

    private static void WithTemporaryDirectory(Action<string> action)
    {
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerUpdaterTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        try
        {
            action(tempDirectoryPath);
        }
        finally
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
    }
}
