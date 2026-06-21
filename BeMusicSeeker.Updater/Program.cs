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
                MoveCurrentApplicationToBackup(appDirectory, previousDirectory);
                CopyExtractedPackage(extractDirectory, appDirectory);
                SafeDeleteFile(packagePath);
                SafeDeleteDirectory(extractDirectory);
            }
            catch
            {
                TryRollback(appDirectory, previousDirectory);
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

        private static void MoveCurrentApplicationToBackup(string appDirectory, string previousDirectory)
        {
            foreach (string path in Directory.EnumerateFileSystemEntries(appDirectory))
            {
                string name = Path.GetFileName(path);
                if (PreservedTopLevelNames.Contains(name))
                {
                    continue;
                }

                EnsureNoReparsePoint(path);
                string destination = Path.Combine(previousDirectory, name);
                if (File.Exists(path))
                {
                    File.Move(path, destination);
                }
                else if (Directory.Exists(path))
                {
                    Directory.Move(path, destination);
                }
            }
        }

        private static void CopyExtractedPackage(string extractDirectory, string appDirectory)
        {
            foreach (string source in Directory.EnumerateFileSystemEntries(extractDirectory))
            {
                string destination = Path.Combine(appDirectory, Path.GetFileName(source));
                if (File.Exists(source))
                {
                    File.Copy(source, destination, overwrite: false);
                }
                else if (Directory.Exists(source))
                {
                    CopyDirectory(source, destination);
                }
            }
        }

        private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
        {
            Directory.CreateDirectory(destinationDirectory);
            foreach (string sourceFile in Directory.EnumerateFiles(sourceDirectory))
            {
                File.Copy(sourceFile, Path.Combine(destinationDirectory, Path.GetFileName(sourceFile)), overwrite: false);
            }
            foreach (string sourceSubDirectory in Directory.EnumerateDirectories(sourceDirectory))
            {
                CopyDirectory(sourceSubDirectory, Path.Combine(destinationDirectory, Path.GetFileName(sourceSubDirectory)));
            }
        }

        private static void TryRollback(string appDirectory, string previousDirectory)
        {
            foreach (string path in Directory.EnumerateFileSystemEntries(appDirectory).ToArray())
            {
                string name = Path.GetFileName(path);
                if (PreservedTopLevelNames.Contains(name))
                {
                    continue;
                }
                SafeDeletePath(path);
            }

            foreach (string source in Directory.EnumerateFileSystemEntries(previousDirectory))
            {
                string destination = Path.Combine(appDirectory, Path.GetFileName(source));
                if (File.Exists(source))
                {
                    File.Move(source, destination);
                }
                else if (Directory.Exists(source))
                {
                    Directory.Move(source, destination);
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
