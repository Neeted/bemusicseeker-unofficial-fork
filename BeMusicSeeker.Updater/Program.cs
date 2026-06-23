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
            if (UpdaterFileSystem.DirectoryExists(extractDirectory))
            {
                UpdaterFileSystem.DeleteDirectory(extractDirectory, recursive: true);
            }
            UpdaterFileSystem.CreateDirectory(extractDirectory);

            ValidatePackage(packagePath);
            ExtractPackage(packagePath, extractDirectory);
            EnsureNoPreservedTopLevelEntries(extractDirectory);

            if (UpdaterFileSystem.DirectoryExists(backupDirectory))
            {
                UpdaterFileSystem.DeleteDirectory(backupDirectory, recursive: true);
            }
            UpdaterFileSystem.CreateDirectory(backupDirectory);

            string previousDirectory = Path.Combine(backupDirectory, "previous");
            UpdaterFileSystem.CreateDirectory(previousDirectory);

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

            if (!string.IsNullOrWhiteSpace(restartExePath) && UpdaterFileSystem.FileExists(restartExePath))
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
            using ZipArchive archive = OpenPackage(packagePath);
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

        private static ZipArchive OpenPackage(string packagePath)
        {
            return new ZipArchive(UpdaterFileSystem.OpenRead(packagePath), ZipArchiveMode.Read);
        }

        private static void ExtractPackage(string packagePath, string extractDirectory)
        {
            using ZipArchive archive = OpenPackage(packagePath);
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                string relativePath = NormalizeRelativePackagePath(entry.FullName);
                string destination = Path.Combine(extractDirectory, relativePath);
                if (string.IsNullOrWhiteSpace(entry.Name))
                {
                    UpdaterFileSystem.CreateDirectory(destination);
                    continue;
                }

                string destinationDirectory = Path.GetDirectoryName(destination);
                if (!string.IsNullOrWhiteSpace(destinationDirectory))
                {
                    UpdaterFileSystem.CreateDirectory(destinationDirectory);
                }
                using Stream source = entry.Open();
                using FileStream destinationStream = UpdaterFileSystem.Open(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                source.CopyTo(destinationStream);
            }
        }

        private static void EnsureNoPreservedTopLevelEntries(string extractDirectory)
        {
            foreach (string path in UpdaterFileSystem.EnumerateFileSystemEntries(extractDirectory))
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
            HashSet<string> previousManagedPaths = ReadManagedFilesManifest(appDirectory, newPackagePaths);
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
                UpdaterFileSystem.CreateDirectory(Path.GetDirectoryName(destination));
                UpdaterFileSystem.CopyFile(source, destination, overwrite: false);
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

            foreach (string source in UpdaterFileSystem.EnumerateFileSystemEntries(previousDirectory))
            {
                RestoreBackupPath(source, appDirectory, previousDirectory);
            }
        }

        private static HashSet<string> EnumerateRelativePackagePaths(string extractDirectory)
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string sourceFile in UpdaterFileSystem.EnumerateFiles(extractDirectory, "*", SearchOption.AllDirectories))
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

        private static HashSet<string> ReadManagedFilesManifest(string appDirectory, HashSet<string> newPackagePaths)
        {
            string manifestPath = Path.Combine(appDirectory, ManagedFilesManifestName);
            if (!UpdaterFileSystem.FileExists(manifestPath))
            {
                return [.. newPackagePaths.Where(path => UpdaterFileSystem.EntryExists(Path.Combine(appDirectory, path)))];
            }

            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in UpdaterFileSystem.ReadAllLines(manifestPath))
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

        private static void WriteManagedFilesManifest(string appDirectory, HashSet<string> managedPaths)
        {
            string manifestPath = Path.Combine(appDirectory, ManagedFilesManifestName);
            string[] lines = [.. managedPaths
                .Where(path => !string.Equals(path, ManagedFilesManifestName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];
            UpdaterFileSystem.WriteAllLines(manifestPath, lines);
        }

        private static void MoveExistingPathToBackup(string appDirectory, string previousDirectory, string relativePath)
        {
            relativePath = NormalizeRelativePackagePath(relativePath);
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                return;
            }

            string source = Path.Combine(appDirectory, relativePath);
            if (!UpdaterFileSystem.EntryExists(source))
            {
                return;
            }

            EnsureNoReparsePoint(source);
            string destination = Path.Combine(previousDirectory, relativePath);
            UpdaterFileSystem.CreateDirectory(Path.GetDirectoryName(destination));
            if (UpdaterFileSystem.FileExists(source))
            {
                UpdaterFileSystem.MoveFile(source, destination);
            }
            else
            {
                UpdaterFileSystem.MoveDirectory(source, destination);
            }
        }

        private static void RestoreBackupPath(string source, string appDirectory, string previousDirectory)
        {
            string relativePath = GetRelativePath(previousDirectory, source);
            string destination = Path.Combine(appDirectory, relativePath);
            UpdaterFileSystem.CreateDirectory(Path.GetDirectoryName(destination));
            if (UpdaterFileSystem.FileExists(source))
            {
                if (UpdaterFileSystem.FileExists(destination))
                {
                    UpdaterFileSystem.DeleteFile(destination);
                }
                UpdaterFileSystem.MoveFile(source, destination);
            }
            else if (UpdaterFileSystem.DirectoryExists(source))
            {
                if (UpdaterFileSystem.DirectoryExists(destination))
                {
                    UpdaterFileSystem.DeleteDirectory(destination, recursive: true);
                }
                UpdaterFileSystem.MoveDirectory(source, destination);
            }
        }

        private static void RemoveEmptyDirectories(string appDirectory)
        {
            foreach (string directory in UpdaterFileSystem.EnumerateDirectories(appDirectory, "*", SearchOption.AllDirectories).OrderByDescending(path => path.Length))
            {
                string name = Path.GetFileName(directory);
                if (PreservedTopLevelNames.Contains(name))
                {
                    continue;
                }

                if (!UpdaterFileSystem.EnumerateFileSystemEntries(directory).Any())
                {
                    UpdaterFileSystem.DeleteDirectory(directory, recursive: false);
                }
            }
        }

        private static void EnsureNoReparsePoint(string path)
        {
            FileAttributes attributes = UpdaterFileSystem.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException("Reparse points are not supported in the application directory: " + path);
            }

            if (UpdaterFileSystem.DirectoryExists(path))
            {
                foreach (string child in UpdaterFileSystem.EnumerateFileSystemEntries(path))
                {
                    EnsureNoReparsePoint(child);
                }
            }
        }

        private static string NormalizeExistingDirectory(string path)
        {
            string normalized = NormalizeDirectoryPath(path);
            if (!UpdaterFileSystem.DirectoryExists(normalized))
            {
                throw new DirectoryNotFoundException(normalized);
            }
            return normalized;
        }

        private static string NormalizeExistingFile(string path)
        {
            string normalized = NormalizeFilePath(path);
            if (!UpdaterFileSystem.FileExists(normalized))
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
            return UpdaterFileSystem.NormalizePath(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }

        private static string NormalizeFilePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }
            return UpdaterFileSystem.NormalizePath(path);
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
            Uri pathUri = new(UpdaterFileSystem.NormalizePath(path));
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
            if (UpdaterFileSystem.FileExists(path))
            {
                UpdaterFileSystem.DeleteFile(path);
            }
        }

        private static void SafeDeleteDirectory(string path)
        {
            if (UpdaterFileSystem.DirectoryExists(path))
            {
                UpdaterFileSystem.DeleteDirectory(path, recursive: true);
            }
        }

        private static void SafeDeletePath(string path)
        {
            if (UpdaterFileSystem.FileExists(path))
            {
                UpdaterFileSystem.DeleteFile(path);
            }
            else if (UpdaterFileSystem.DirectoryExists(path))
            {
                UpdaterFileSystem.DeleteDirectory(path, recursive: true);
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
