using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;

namespace BeMusicSeeker.Updater
{
    internal static class Program
    {
        internal const int ProtocolVersion = 1;

        private static readonly HashSet<string> PreservedTopLevelNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "config",
            "data",
            "logs",
            "update_backup",
            "update_work"
        };

        private const string ManagedFilesManifestName = "update-managed-files.txt";

        private static readonly HashSet<string> LegacyManagedRootFiles = new(StringComparer.OrdinalIgnoreCase)
        {
            "BeMusicSeeker.exe",
            "BeMusicSeeker.exe.config",
            "BeMusicSeeker.Updater.exe",
            "LaunchWithInfoLog.bat",
            "README.md",
            "README.ja.md",
            "LICENSE",
            "ThirdPartyNotices.txt",
            "ThirdPartyNotices.ja.txt",
            "chart-info-metadata.7z",
            "chart-info-metadata.db",
            ManagedFilesManifestName
        };

        private static readonly HashSet<string> LegacyManagedRootDirectories = new(StringComparer.OrdinalIgnoreCase)
        {
            "libs",
            "native",
            "lang",
            "third_party",
            "docs"
        };

        private static int Main(string[] args)
        {
            if (args.Length == 1 && string.Equals(args[0], "--version", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine(ProtocolVersion);
                return 0;
            }

            try
            {
                UpdateRequest request = UpdateRequest.Parse(args);
                ApplyUpdate(request);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                return 1;
            }
        }

        private static void ApplyUpdate(UpdateRequest request)
        {
            WaitForApplicationExit(request.ProcessId);

            string appDirectory = NormalizeExistingDirectory(request.AppDirectory);
            string packagePath = NormalizeExistingFile(request.PackagePath);
            string backupDirectory = NormalizeDirectoryPath(request.BackupDirectory);
            string restartExePath = NormalizeFilePath(request.RestartExePath);

            if (!IsPathUnderDirectory(backupDirectory, appDirectory))
            {
                throw new InvalidOperationException("backup-dir must be under app-dir.");
            }

            string workDirectory = Path.Combine(appDirectory, "update_work");
            string extractDirectory = Path.Combine(workDirectory, "extracted");
            if (Directory.Exists(extractDirectory))
            {
                Directory.Delete(extractDirectory, recursive: true);
            }
            Directory.CreateDirectory(extractDirectory);

            ValidatePackage(packagePath);
            ZipFile.ExtractToDirectory(packagePath, extractDirectory);
            EnsureNoPreservedTopLevelEntries(extractDirectory);

            if (Directory.Exists(backupDirectory))
            {
                Directory.Delete(backupDirectory, recursive: true);
            }
            Directory.CreateDirectory(backupDirectory);

            string previousDirectory = Path.Combine(backupDirectory, "previous");
            Directory.CreateDirectory(previousDirectory);

            try
            {
            ApplyExtractedPackage(extractDirectory, appDirectory, previousDirectory);
            SafeDeleteFile(packagePath);
            SafeDeleteDirectory(extractDirectory);
            }
            catch
            {
                TryRollback(appDirectory, previousDirectory, extractDirectory);
                throw;
            }

            if (!string.IsNullOrWhiteSpace(restartExePath) && File.Exists(restartExePath))
            {
                Process.Start(new ProcessStartInfo(restartExePath)
                {
                    UseShellExecute = true,
                    WorkingDirectory = appDirectory
                });
            }
        }

        private static void WaitForApplicationExit(int processId)
        {
            if (processId <= 0)
            {
                return;
            }

            try
            {
                using Process process = Process.GetProcessById(processId);
                process.WaitForExit(60000);
                if (!process.HasExited)
                {
                    throw new TimeoutException("Application process did not exit within 60 seconds.");
                }
            }
            catch (ArgumentException)
            {
            }
        }

        private static void ValidatePackage(string packagePath)
        {
            using ZipArchive archive = ZipFile.OpenRead(packagePath);
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                string entryName = entry.FullName.Replace('\\', '/');
                if (string.IsNullOrWhiteSpace(entryName))
                {
                    continue;
                }

                if (Path.IsPathRooted(entryName) || entryName.Contains(":"))
                {
                    throw new InvalidOperationException("Package contains rooted entry: " + entry.FullName);
                }

                string[] segments = entryName.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                if (segments.Any(segment => segment == "." || segment == ".."))
                {
                    throw new InvalidOperationException("Package contains traversal entry: " + entry.FullName);
                }

                if (segments.Length > 0 && PreservedTopLevelNames.Contains(segments[0]))
                {
                    throw new InvalidOperationException("Package must not contain preserved directory: " + segments[0]);
                }
            }
        }

        private static void EnsureNoPreservedTopLevelEntries(string extractDirectory)
        {
            foreach (string path in Directory.EnumerateFileSystemEntries(extractDirectory))
            {
                string name = Path.GetFileName(path);
                if (PreservedTopLevelNames.Contains(name))
                {
                    throw new InvalidOperationException("Extracted package contains preserved entry: " + name);
                }
            }
        }

        private static void ApplyExtractedPackage(string extractDirectory, string appDirectory, string previousDirectory)
        {
            HashSet<string> newPackagePaths = EnumerateRelativePackagePaths(extractDirectory);
            HashSet<string> previousManagedPaths = ReadManagedFilesManifest(appDirectory);
            foreach (string relativePath in previousManagedPaths.Except(newPackagePaths, StringComparer.OrdinalIgnoreCase))
            {
                MoveExistingPathToBackup(appDirectory, previousDirectory, relativePath);
            }

            foreach (string relativePath in newPackagePaths)
            {
                if (string.Equals(relativePath, ManagedFilesManifestName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                MoveExistingPathToBackup(appDirectory, previousDirectory, relativePath);
                string source = Path.Combine(extractDirectory, relativePath);
                string destination = Path.Combine(appDirectory, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                File.Copy(source, destination, overwrite: false);
            }

            WriteManagedFilesManifest(appDirectory, newPackagePaths);
            RemoveEmptyDirectories(appDirectory);
        }

        private static void TryRollback(string appDirectory, string previousDirectory, string extractDirectory)
        {
            foreach (string relativePath in EnumerateRelativePackagePaths(extractDirectory))
            {
                SafeDeletePath(Path.Combine(appDirectory, relativePath));
            }

            foreach (string source in Directory.EnumerateFileSystemEntries(previousDirectory))
            {
                RestoreBackupPath(source, appDirectory, previousDirectory);
            }
        }

        private static HashSet<string> EnumerateRelativePackagePaths(string extractDirectory)
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string sourceFile in Directory.EnumerateFiles(extractDirectory, "*", SearchOption.AllDirectories))
            {
                string relativePath = GetRelativePath(extractDirectory, sourceFile);
                if (!string.Equals(relativePath, ManagedFilesManifestName, StringComparison.OrdinalIgnoreCase))
                {
                    paths.Add(relativePath);
                }
            }
            paths.Add(ManagedFilesManifestName);
            return paths;
        }

        private static HashSet<string> ReadManagedFilesManifest(string appDirectory)
        {
            string manifestPath = Path.Combine(appDirectory, ManagedFilesManifestName);
            if (!File.Exists(manifestPath))
            {
                return EnumerateLegacyManagedPaths(appDirectory);
            }

            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in File.ReadAllLines(manifestPath))
            {
                string normalized = NormalizeRelativePackagePath(line);
                if (!string.IsNullOrWhiteSpace(normalized))
                {
                    paths.Add(normalized);
                }
            }
            paths.Add(ManagedFilesManifestName);
            return paths;
        }

        private static HashSet<string> EnumerateLegacyManagedPaths(string appDirectory)
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string file in Directory.EnumerateFiles(appDirectory))
            {
                string name = Path.GetFileName(file);
                if (LegacyManagedRootFiles.Contains(name))
                {
                    paths.Add(name);
                }
            }

            foreach (string directory in Directory.EnumerateDirectories(appDirectory))
            {
                string name = Path.GetFileName(directory);
                if (!LegacyManagedRootDirectories.Contains(name))
                {
                    continue;
                }

                foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                {
                    paths.Add(GetRelativePath(appDirectory, file));
                }
            }

            paths.Add(ManagedFilesManifestName);
            return paths;
        }

        private static void WriteManagedFilesManifest(string appDirectory, HashSet<string> managedPaths)
        {
            string manifestPath = Path.Combine(appDirectory, ManagedFilesManifestName);
            string[] lines = [.. managedPaths
                .Where(path => !string.Equals(path, ManagedFilesManifestName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];
            File.WriteAllLines(manifestPath, lines);
        }

        private static void MoveExistingPathToBackup(string appDirectory, string previousDirectory, string relativePath)
        {
            relativePath = NormalizeRelativePackagePath(relativePath);
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                return;
            }

            string source = Path.Combine(appDirectory, relativePath);
            if (!File.Exists(source) && !Directory.Exists(source))
            {
                return;
            }

            EnsureNoReparsePoint(source);
            string destination = Path.Combine(previousDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            if (File.Exists(source))
            {
                File.Move(source, destination);
            }
            else
            {
                Directory.Move(source, destination);
            }
        }

        private static void RestoreBackupPath(string source, string appDirectory, string previousDirectory)
        {
            string relativePath = GetRelativePath(previousDirectory, source);
            string destination = Path.Combine(appDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            if (File.Exists(source))
            {
                if (File.Exists(destination))
                {
                    File.Delete(destination);
                }
                File.Move(source, destination);
            }
            else if (Directory.Exists(source))
            {
                if (Directory.Exists(destination))
                {
                    Directory.Delete(destination, recursive: true);
                }
                Directory.Move(source, destination);
            }
        }

        private static void RemoveEmptyDirectories(string appDirectory)
        {
            foreach (string directory in Directory.EnumerateDirectories(appDirectory, "*", SearchOption.AllDirectories).OrderByDescending(path => path.Length))
            {
                string name = Path.GetFileName(directory);
                if (PreservedTopLevelNames.Contains(name))
                {
                    continue;
                }

                if (!Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory);
                }
            }
        }

        private static void EnsureNoReparsePoint(string path)
        {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException("Reparse points are not supported in the application directory: " + path);
            }

            if (Directory.Exists(path))
            {
                foreach (string child in Directory.EnumerateFileSystemEntries(path))
                {
                    EnsureNoReparsePoint(child);
                }
            }
        }

        private static string NormalizeExistingDirectory(string path)
        {
            string normalized = NormalizeDirectoryPath(path);
            if (!Directory.Exists(normalized))
            {
                throw new DirectoryNotFoundException(normalized);
            }
            return normalized;
        }

        private static string NormalizeExistingFile(string path)
        {
            string normalized = NormalizeFilePath(path);
            if (!File.Exists(normalized))
            {
                throw new FileNotFoundException("File not found.", normalized);
            }
            return normalized;
        }

        private static string NormalizeDirectoryPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("Directory path is required.");
            }
            return Path.GetFullPath(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }

        private static string NormalizeFilePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }
            return Path.GetFullPath(path);
        }

        private static bool IsPathUnderDirectory(string path, string directory)
        {
            string normalizedPath = NormalizeDirectoryPath(path) + Path.DirectorySeparatorChar;
            string normalizedDirectory = NormalizeDirectoryPath(directory) + Path.DirectorySeparatorChar;
            return normalizedPath.StartsWith(normalizedDirectory, StringComparison.OrdinalIgnoreCase);
        }

        private static string GetRelativePath(string rootDirectory, string path)
        {
            Uri rootUri = new(NormalizeDirectoryPath(rootDirectory) + Path.DirectorySeparatorChar);
            Uri pathUri = new(Path.GetFullPath(path));
            string relative = Uri.UnescapeDataString(rootUri.MakeRelativeUri(pathUri).ToString()).Replace('/', Path.DirectorySeparatorChar);
            return NormalizeRelativePackagePath(relative);
        }

        private static string NormalizeRelativePackagePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            string normalized = path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar).Trim();
            if (Path.IsPathRooted(normalized) || normalized.Contains(":"))
            {
                throw new InvalidOperationException("Managed package path must be relative: " + path);
            }

            string[] segments = normalized.Split(new[] { Path.DirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Any(segment => segment == "." || segment == ".."))
            {
                throw new InvalidOperationException("Managed package path must not contain traversal: " + path);
            }

            if (segments.Length > 0 && PreservedTopLevelNames.Contains(segments[0]))
            {
                throw new InvalidOperationException("Managed package path must not target preserved directory: " + path);
            }

            return string.Join(Path.DirectorySeparatorChar.ToString(), segments);
        }

        private static void SafeDeleteFile(string path)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        private static void SafeDeleteDirectory(string path)
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }

        private static void SafeDeletePath(string path)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            else if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }

        private sealed class UpdateRequest
        {
            public string AppDirectory { get; private set; }

            public string PackagePath { get; private set; }

            public string BackupDirectory { get; private set; }

            public int ProcessId { get; private set; }

            public string RestartExePath { get; private set; }

            public static UpdateRequest Parse(string[] args)
            {
                Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < args.Length; i++)
                {
                    string key = args[i];
                    if (!key.StartsWith("--", StringComparison.Ordinal))
                    {
                        throw new ArgumentException("Unexpected argument: " + key);
                    }
                    if (i + 1 >= args.Length)
                    {
                        throw new ArgumentException("Missing value for argument: " + key);
                    }
                    values[key] = args[++i];
                }

                return new UpdateRequest
                {
                    AppDirectory = ReadRequired(values, "--app-dir"),
                    PackagePath = ReadRequired(values, "--package"),
                    BackupDirectory = ReadRequired(values, "--backup-dir"),
                    ProcessId = int.TryParse(ReadRequired(values, "--pid"), out int processId) ? processId : 0,
                    RestartExePath = values.TryGetValue("--restart-exe", out string restartExePath) ? restartExePath : string.Empty
                };
            }

            private static string ReadRequired(Dictionary<string, string> values, string key)
            {
                if (!values.TryGetValue(key, out string value) || string.IsNullOrWhiteSpace(value))
                {
                    throw new ArgumentException("Missing required argument: " + key);
                }
                return value;
            }
        }
    }
}
