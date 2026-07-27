using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
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
            "log",
            "logs",
            "update_backup",
            "update_work"
        };

        private const string ManagedFilesManifestName = "update-managed-files.txt";
        private const string UpdateFailureReceiptFileName = "update-failure.txt";
        private const string UpdateFailureReceiptTemporaryFileName = "update-failure.txt.tmp";
        private const string UpdaterReadyFileName = "updater-ready.txt";
        private const string UpdaterDecisionFileName = "updater-decision.txt";
        private const int DecisionWaitMilliseconds = 300000;
        private const string ImportedMetadataDirectoryName = "imported_metadata";
        private const string MetadataDbFileName = "chart-info-metadata.db";
        private const string MetadataArchiveFileName = "chart-info-metadata.7z";

        private static readonly string[] LegacyManagedFilePaths =
        [
            "libs/Bass.Net.dll",
            "libs/DynamicJson.dll",
            "libs/IniLibrary.dll",
            "libs/Livet.dll",
            "libs/Livet.Extensions.dll",
            "libs/MetroRadiance.Chrome.dll",
            "libs/MetroRadiance.Core.dll",
            "libs/MetroRadiance.dll",
            "libs/Microsoft.Expression.Drawing.dll",
            "libs/Microsoft.Expression.Effects.dll",
            "libs/Microsoft.Expression.Interactions.dll",
            "libs/Microsoft.WindowsAPICodePack.dll",
            "libs/Microsoft.WindowsAPICodePack.Shell.dll",
            "libs/Newtonsoft.Json.dll",
            "libs/NLog.Database.dll",
            "libs/NLog.dll",
            "libs/NLog.WindowsEventLog.dll",
            "libs/OggVorbis.NET64.dll",
            "libs/QuickConverter.dll",
            "libs/SgmlReaderDll.dll",
            "libs/SevenZipExtractor.dll",
            "libs/System.Collections.Immutable.dll",
            "libs/System.Resources.Extensions.dll",
            "libs/System.Memory.dll",
            "libs/System.Buffers.dll",
            "libs/System.Numerics.Vectors.dll",
            "libs/System.Runtime.CompilerServices.Unsafe.dll",
            "libs/System.Windows.Interactivity.dll",
            "libs/sqlite.net.dll",
            "libs/x64/OggVorbis.NET64.dll",
            "libs/x64/sqlite3.dll",
            "x64/sqlite3.dll",
            "x86/sqlite3.dll",
            "x86/7z.dll",
            "x86/bass.dll",
            "x86/bass_fx.dll",
            "x86/bassasio.dll",
            "x86/bassenc.dll",
            "x86/bassmix.dll",
            "x86/basswasapi.dll",
            "libs/x86/sqlite3.dll",
            "libs/x86/7z.dll",
            "libs/x86/bass.dll",
            "libs/x86/bass_fx.dll",
            "libs/x86/bassasio.dll",
            "libs/x86/bassenc.dll",
            "libs/x86/bassmix.dll",
            "libs/x86/basswasapi.dll",
            "x64/OggVorbis.NET64.dll",
            "x64/7z.dll",
            "x64/bass.dll",
            "x64/bass_fx.dll",
            "x64/bassasio.dll",
            "x64/bassenc.dll",
            "x64/bassmix.dll",
            "x64/basswasapi.dll",
            "BeMusicSeeker.exe.config",
            "SevenZipExtractor.dll",
            "OggVorbis.NET.dll",
            "OggVorbis.NET64.dll"
        ];

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
                PublishReadyHandshake(request);
                if (!WaitForLaunchDecision(request))
                {
                    return 0;
                }
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
            try
            {
                WaitForApplicationExit(request.ProcessId);
            }
            catch (Exception exception)
            {
                TryWriteFailureReceipt(request, exception);
                throw;
            }

            try
            {
                ApplyUpdateAfterApplicationExit(request);
            }
            catch (RollbackFailureException exception)
            {
                // Do not restart when the updater could not prove that the previous
                // application state was fully restored.
                TryWriteFailureReceipt(request, exception);
                throw;
            }
            catch (Exception exception)
            {
                TryWriteFailureReceipt(request, exception);
                TryRestartApplicationAfterFailure(request);
                throw;
            }
        }

        private static void PublishReadyHandshake(UpdateRequest request)
        {
            string appDirectory = NormalizeExistingDirectory(request.AppDirectory);
            string readyFilePath = NormalizeFilePath(request.ReadyFilePath);
            string expectedReadyFilePath = NormalizeFilePath(Path.Combine(appDirectory, "update_work", "current", UpdaterReadyFileName));
            if (!string.Equals(readyFilePath, expectedReadyFilePath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("ready-file must be the dedicated updater-ready.txt file under update_work/current.");
            }
            EnsureNoReparsePointAncestors(appDirectory, "update_work/current/updater-ready.txt");
            EnsureNoReparsePointIfPresent(readyFilePath);
            byte[] marker = Encoding.UTF8.GetBytes(ProtocolVersion.ToString());
            using FileStream stream = UpdaterFileSystem.Open(readyFilePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            stream.Write(marker, 0, marker.Length);
            stream.Flush(flushToDisk: true);
        }

        private static bool WaitForLaunchDecision(UpdateRequest request)
        {
            try
            {
                string appDirectory = NormalizeExistingDirectory(request.AppDirectory);
                string decisionFilePath = NormalizeFilePath(request.DecisionFilePath);
                string expectedDecisionFilePath = NormalizeFilePath(Path.Combine(appDirectory, "update_work", "current", UpdaterDecisionFileName));
                if (!string.Equals(decisionFilePath, expectedDecisionFilePath, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("decision-file must be the dedicated updater-decision.txt file under update_work/current.");
                }
                EnsureNoReparsePointAncestors(appDirectory, "update_work/current/updater-decision.txt");
                EnsureNoReparsePointIfPresent(decisionFilePath);

                DateTime deadline = DateTime.UtcNow.AddMilliseconds(DecisionWaitMilliseconds);
                while (true)
                {
                    if (UpdaterFileSystem.FileExists(decisionFilePath))
                    {
                        string decision;
                        using (FileStream stream = UpdaterFileSystem.OpenRead(decisionFilePath))
                        using (var reader = new StreamReader(stream, Encoding.UTF8))
                        {
                            decision = reader.ReadToEnd().Trim();
                        }
                        UpdaterFileSystem.DeleteFile(decisionFilePath);
                        if (string.Equals(decision, "proceed", StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                        if (string.Equals(decision, "cancel", StringComparison.OrdinalIgnoreCase))
                        {
                            return false;
                        }
                        throw new InvalidOperationException("The updater launch decision was not recognized: " + decision);
                    }

                    if (DateTime.UtcNow >= deadline)
                    {
                        if (!IsProcessRunning(request.ProcessId))
                        {
                            throw new TimeoutException("The application exited before publishing an updater launch decision.");
                        }
                        // Keep the decision channel alive while the still-running application
                        // drains its shutdown work; the updater must not start its 60-second
                        // process-exit window before explicit approval.
                        deadline = DateTime.UtcNow.AddMilliseconds(DecisionWaitMilliseconds);
                    }
                    Thread.Sleep(50);
                }
            }
            catch (Exception exception)
            {
                TryWriteFailureReceipt(request, exception);
                throw;
            }
        }

        private static void ApplyUpdateAfterApplicationExit(UpdateRequest request)
        {

            string appDirectory = NormalizeExistingDirectory(request.AppDirectory);
            string packagePath = NormalizeExistingFile(request.PackagePath);
            string backupDirectory = NormalizeDirectoryPath(request.BackupDirectory);
            string restartExePath = NormalizeExistingFile(request.RestartExePath);
            EnsurePathUnderDirectory(appDirectory, restartExePath, "restart executable");

            string expectedBackupDirectory = NormalizeDirectoryPath(Path.Combine(appDirectory, "update_backup"));
            if (!string.Equals(backupDirectory, expectedBackupDirectory, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("backup-dir must be the dedicated update_backup directory under app-dir.");
            }

            string workDirectory = Path.Combine(appDirectory, "update_work");
            string extractDirectory = Path.Combine(workDirectory, "extracted");
            EnsurePackagePathUnderDownloads(appDirectory, packagePath);
            EnsureNoReparsePointAncestors(appDirectory, "update_work/extracted");
            if (UpdaterFileSystem.DirectoryExists(extractDirectory))
            {
                EnsureNoReparsePoint(extractDirectory);
                UpdaterFileSystem.DeleteDirectory(extractDirectory, recursive: true);
            }
            UpdaterFileSystem.CreateDirectory(extractDirectory);

            ValidatePackage(packagePath);
            ExtractPackage(packagePath, extractDirectory);
            EnsureNoPreservedTopLevelEntries(extractDirectory);
            string restartRelativePath = GetRelativePath(appDirectory, restartExePath);
            if (!EnumerateRelativePackagePaths(extractDirectory).Contains(restartRelativePath, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The restart executable must be included in the updated package: " + restartRelativePath);
            }

            ValidateExtractedPackageBeforeBackupRotation(extractDirectory, appDirectory);

            if (UpdaterFileSystem.DirectoryExists(backupDirectory))
            {
                EnsureNoReparsePoint(backupDirectory);
                UpdaterFileSystem.DeleteDirectory(backupDirectory, recursive: true);
            }
            EnsureNoReparsePointAncestors(appDirectory, "update_backup/previous");
            UpdaterFileSystem.CreateDirectory(backupDirectory);

            string previousDirectory = Path.Combine(backupDirectory, "previous");
            UpdaterFileSystem.CreateDirectory(previousDirectory);

            bool restartStarted = false;
            var appliedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var backupEntries = new List<BackupEntry>();
            var createdDirectories = new List<string>();
            try
            {
                ApplyExtractedPackage(extractDirectory, appDirectory, previousDirectory, appliedPaths, backupEntries, createdDirectories);
                if (!UpdaterFileSystem.FileExists(restartExePath))
                {
                    throw new FileNotFoundException("The restart executable was not included in the updated application.", restartExePath);
                }

                try
                {
                    SafeDeleteFile(packagePath);
                    SafeDeleteDirectory(extractDirectory);
                }
                catch (Exception cleanupException)
                {
                    Console.Error.WriteLine("The update was applied, but temporary cleanup before restart failed; the restarted application will retry it: " + cleanupException);
                }

                Process restartProcess = Process.Start(new ProcessStartInfo(restartExePath)
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(restartExePath) ?? appDirectory
                });
                if (restartProcess == null)
                {
                    throw new InvalidOperationException("The updated application could not be restarted.");
                }
                restartStarted = true;
            }
            catch (Exception updateException)
            {
                if (!restartStarted)
                {
                    try
                    {
                        TryRollback(appDirectory, previousDirectory, appliedPaths, backupEntries, createdDirectories);
                        TryCleanupFailedUpdateArtifacts(packagePath, extractDirectory);
                    }
                    catch (Exception rollbackException)
                    {
                        throw new RollbackFailureException(updateException, rollbackException);
                    }
                }
                throw;
            }
        }

        private static void WaitForApplicationExit(int processId)
        {
            if (processId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(processId), "The application process id must be positive.");
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

        private static bool IsProcessRunning(int processId)
        {
            try
            {
                using Process process = Process.GetProcessById(processId);
                return !process.HasExited;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        private static void TryRestartApplicationAfterFailure(UpdateRequest request)
        {
            try
            {
                string appDirectory = NormalizeExistingDirectory(request.AppDirectory);
                string restartExePath = NormalizeExistingFile(request.RestartExePath);
                EnsurePathUnderDirectory(appDirectory, restartExePath, "restart executable");
                Process restartProcess = Process.Start(new ProcessStartInfo(restartExePath)
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(restartExePath) ?? appDirectory
                });
                if (restartProcess == null)
                {
                    throw new InvalidOperationException("The previous application could not be restarted after update failure.");
                }
            }
            catch (Exception restartException)
            {
                Console.Error.WriteLine("The update failed and the previous application could not be restarted: " + restartException);
            }
        }

        private static void TryWriteFailureReceipt(UpdateRequest request, Exception exception)
        {
            string temporaryReceiptPath = null;
            try
            {
                string appDirectory = NormalizeExistingDirectory(request.AppDirectory);
                string workDirectory = Path.Combine(appDirectory, "update_work");
                string receiptPath = Path.Combine(workDirectory, UpdateFailureReceiptFileName);
                temporaryReceiptPath = Path.Combine(workDirectory, UpdateFailureReceiptTemporaryFileName);
                EnsureNoReparsePointAncestors(appDirectory, "update_work/" + UpdateFailureReceiptFileName);
                EnsureNoReparsePointIfPresent(workDirectory);
                UpdaterFileSystem.CreateDirectory(workDirectory);
                EnsureNoReparsePointIfPresent(receiptPath);
                EnsureNoReparsePointIfPresent(temporaryReceiptPath);
                byte[] details = Encoding.UTF8.GetBytes(exception?.ToString() ?? "The updater failed without exception details.");
                using (FileStream stream = UpdaterFileSystem.Open(temporaryReceiptPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    stream.Write(details, 0, details.Length);
                    stream.Flush(flushToDisk: true);
                }
                UpdaterFileSystem.MoveFile(temporaryReceiptPath, receiptPath, overwrite: true);
            }
            catch (Exception receiptException)
            {
                Console.Error.WriteLine("The updater could not atomically persist its failure receipt; the temporary receipt will be retried on the next startup: " + receiptException);
                bool temporaryReceiptExists = false;
                try
                {
                    temporaryReceiptExists = temporaryReceiptPath != null && UpdaterFileSystem.FileExists(temporaryReceiptPath);
                }
                catch (Exception fallbackProbeException)
                {
                    Console.Error.WriteLine("The updater could not inspect its temporary failure receipt fallback: " + fallbackProbeException);
                }
                if (!temporaryReceiptExists)
                {
                    Console.Error.WriteLine("The updater has no durable failure receipt fallback: " + receiptException);
                }
            }
        }

        private static void TryCleanupFailedUpdateArtifacts(string packagePath, string extractDirectory)
        {
            try
            {
                SafeDeleteFile(packagePath);
                SafeDeleteDirectory(extractDirectory);
            }
            catch (Exception cleanupException)
            {
                Console.Error.WriteLine("The update was rolled back, but temporary cleanup failed; startup cleanup will retry it: " + cleanupException);
            }
        }

        private static void ValidatePackage(string packagePath)
        {
            using ZipArchive archive = OpenPackage(packagePath);
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                string entryName = entry.FullName.Replace('\\', '/');
                bool isDirectoryEntry = entryName.EndsWith("/", StringComparison.Ordinal);
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
                if (segments.Length > 0 && string.Equals(segments[0], ImportedMetadataDirectoryName, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Package must not contain app-managed metadata cache: " + segments[0]);
                }
                if (segments.Length > 0 && string.Equals(segments[0], MetadataDbFileName, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Package must not contain root metadata database: " + segments[0]);
                }
                if ((segments.Length > 1 || isDirectoryEntry) && string.Equals(segments[0], MetadataArchiveFileName, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Package must contain root metadata archive as a file: " + segments[0]);
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

        private static void ApplyExtractedPackage(
            string extractDirectory,
            string appDirectory,
            string previousDirectory,
            HashSet<string> appliedPaths,
            ICollection<BackupEntry> backupEntries,
            ICollection<string> createdDirectories)
        {
            HashSet<string> newPackagePaths = EnumerateRelativePackagePaths(extractDirectory);
            bool hasManagedFilesManifest = UpdaterFileSystem.FileExists(Path.Combine(appDirectory, ManagedFilesManifestName));
            HashSet<string> previousManagedPaths = ReadManagedFilesManifest(appDirectory, newPackagePaths);
            AddLegacyManagedPaths(previousManagedPaths, appDirectory);
            AddAppManagedMetadataArtifactPaths(previousManagedPaths, appDirectory);
            ValidateExistingPackagePathsBeforeMutation(appDirectory, previousManagedPaths, newPackagePaths, hasManagedFilesManifest);
            ValidateFileToDirectoryTransitions(appDirectory, previousManagedPaths, newPackagePaths);
            MoveExistingPathToBackup(appDirectory, previousDirectory, ManagedFilesManifestName, backupEntries);
            appliedPaths.Add(ManagedFilesManifestName);
            foreach (string relativePath in previousManagedPaths.Except(newPackagePaths, StringComparer.OrdinalIgnoreCase))
            {
                EnsureNoReparsePointAncestors(appDirectory, relativePath);
                MoveExistingPathToBackup(appDirectory, previousDirectory, relativePath, backupEntries);
            }

            foreach (string relativePath in newPackagePaths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                if (string.Equals(relativePath, ManagedFilesManifestName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                EnsureNoReparsePointAncestors(appDirectory, relativePath);
                MoveExistingPathToBackup(appDirectory, previousDirectory, relativePath, backupEntries);
                appliedPaths.Add(relativePath);
                string source = Path.Combine(extractDirectory, relativePath);
                string destination = Path.Combine(appDirectory, relativePath);
                CreateDestinationDirectory(appDirectory, destination, createdDirectories);
                UpdaterFileSystem.CopyFile(source, destination, overwrite: false);
            }

            appliedPaths.Add(ManagedFilesManifestName);
            WriteManagedFilesManifest(appDirectory, newPackagePaths);
            RemoveEmptyDirectories(appDirectory);
        }

        private static void ValidateExtractedPackageBeforeBackupRotation(string extractDirectory, string appDirectory)
        {
            HashSet<string> newPackagePaths = EnumerateRelativePackagePaths(extractDirectory);
            bool hasManagedFilesManifest = UpdaterFileSystem.FileExists(Path.Combine(appDirectory, ManagedFilesManifestName));
            HashSet<string> previousManagedPaths = ReadManagedFilesManifest(appDirectory, newPackagePaths);
            AddLegacyManagedPaths(previousManagedPaths, appDirectory);
            AddAppManagedMetadataArtifactPaths(previousManagedPaths, appDirectory);
            ValidateExistingPackagePathsBeforeMutation(appDirectory, previousManagedPaths, newPackagePaths, hasManagedFilesManifest);
            ValidateFileToDirectoryTransitions(appDirectory, previousManagedPaths, newPackagePaths);
        }

        private static void AddAppManagedMetadataArtifactPaths(HashSet<string> managedPaths, string appDirectory)
        {
            AddAppManagedMetadataDirectoryPath(managedPaths, appDirectory, ImportedMetadataDirectoryName);
            AddLegacyManagedFilePath(managedPaths, appDirectory, MetadataDbFileName);
            AddLegacyManagedFilePath(managedPaths, appDirectory, MetadataArchiveFileName);
        }

        private static void ValidateExistingPackagePathsBeforeMutation(
            string appDirectory,
            HashSet<string> previousManagedPaths,
            HashSet<string> newPackagePaths,
            bool hasManagedFilesManifest)
        {
            foreach (string relativePath in previousManagedPaths.Union(newPackagePaths, StringComparer.OrdinalIgnoreCase))
            {
                EnsureNoReparsePointAncestors(appDirectory, relativePath);
                string path = Path.Combine(appDirectory, relativePath);
                EnsureNoReparsePointIfPresent(path);
                if (UpdaterFileSystem.DirectoryExists(path))
                {
                    EnsureNoReparsePoint(path);
                }
            }

            foreach (string relativePath in previousManagedPaths)
            {
                string path = Path.Combine(appDirectory, relativePath);
                if (!UpdaterFileSystem.DirectoryExists(path))
                {
                    continue;
                }

                foreach (string existingFile in UpdaterFileSystem.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    string existingRelativePath = GetRelativePath(appDirectory, existingFile);
                    if (!IsManagedPath(existingRelativePath, appDirectory, previousManagedPaths))
                    {
                        throw new IOException("A managed file path contains an unmanaged descendant: " + path);
                    }
                }
            }

            foreach (string relativePath in newPackagePaths)
            {
                ValidateExistingFileAncestors(appDirectory, relativePath, previousManagedPaths);
                if (!hasManagedFilesManifest
                    || string.Equals(relativePath, ManagedFilesManifestName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string path = Path.Combine(appDirectory, relativePath);
                if (UpdaterFileSystem.FileExists(path)
                    && !IsManagedPath(relativePath, appDirectory, previousManagedPaths))
                {
                    throw new IOException("Cannot replace an unmanaged application file: " + path);
                }
            }
        }

        private static void ValidateExistingFileAncestors(
            string appDirectory,
            string relativePath,
            HashSet<string> previousManagedPaths)
        {
            string normalized = NormalizeRelativePackagePath(relativePath);
            string[] segments = normalized.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            string current = appDirectory;
            for (int i = 0; i < segments.Length - 1; i++)
            {
                current = Path.Combine(current, segments[i]);
                if (!UpdaterFileSystem.FileExists(current))
                {
                    continue;
                }

                string ancestorRelativePath = string.Join(Path.DirectorySeparatorChar.ToString(), segments[..(i + 1)]);
                if (!IsManagedPath(ancestorRelativePath, appDirectory, previousManagedPaths))
                {
                    throw new IOException("Cannot create a directory below an unmanaged file: " + current);
                }
            }
        }

        private static void ValidateFileToDirectoryTransitions(
            string appDirectory,
            HashSet<string> previousManagedPaths,
            HashSet<string> newPackagePaths)
        {
            foreach (string relativePath in newPackagePaths)
            {
                if (string.Equals(relativePath, ManagedFilesManifestName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string path = Path.Combine(appDirectory, relativePath);
                if (!UpdaterFileSystem.DirectoryExists(path))
                {
                    continue;
                }

                EnsureNoReparsePoint(path);
                foreach (string existingFile in UpdaterFileSystem.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    string existingRelativePath = GetRelativePath(appDirectory, existingFile);
                    if (!IsManagedPath(existingRelativePath, appDirectory, previousManagedPaths))
                    {
                        throw new IOException("Cannot replace a directory containing unmanaged files: " + path);
                    }
                }
            }
        }

        private static bool IsManagedPath(
            string relativePath,
            string appDirectory,
            HashSet<string> managedPaths)
        {
            string normalized = NormalizeRelativePackagePath(relativePath);
            if (managedPaths.Contains(normalized))
            {
                return true;
            }

            string current = normalized;
            while (current.Contains(Path.DirectorySeparatorChar))
            {
                current = current[..current.LastIndexOf(Path.DirectorySeparatorChar)];
                if (!managedPaths.Contains(current))
                {
                    continue;
                }

                string managedPath = Path.Combine(appDirectory, current);
                if (UpdaterFileSystem.DirectoryExists(managedPath)
                    && managedPaths.Any(candidate => IsRelativePathUnderDirectory(candidate, current)))
                {
                    return true;
                }
            }

            return false;
        }

        private static void EnsurePackagePathUnderDownloads(string appDirectory, string packagePath)
        {
            string downloadsDirectory = Path.Combine(appDirectory, "update_work", "downloads");
            EnsurePathUnderDirectory(downloadsDirectory, packagePath, "update package");

            EnsureNoReparsePointAncestors(appDirectory, "update_work/downloads");
            EnsureNoReparsePointIfPresent(downloadsDirectory);
            if (UpdaterFileSystem.DirectoryExists(downloadsDirectory))
            {
                EnsureNoReparsePoint(downloadsDirectory);
            }
        }

        private static void EnsurePathUnderDirectory(string rootDirectory, string candidatePath, string description)
        {
            string normalizedCandidatePath = UpdaterFileSystem.NormalizePath(candidatePath);
            string normalizedRootDirectory = UpdaterFileSystem.NormalizePath(rootDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(normalizedCandidatePath, normalizedRootDirectory, StringComparison.OrdinalIgnoreCase)
                || !normalizedCandidatePath.StartsWith(
                    normalizedRootDirectory + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The " + description + " must be under " + normalizedRootDirectory + ".");
            }
        }

        private static void AddAppManagedMetadataDirectoryPath(HashSet<string> managedPaths, string appDirectory, string relativePath)
        {
            string normalized = NormalizeRelativePackagePath(relativePath);
            string path = Path.Combine(appDirectory, normalized);
            if (UpdaterFileSystem.DirectoryExists(path))
            {
                EnsureNoReparsePoint(path);
                managedPaths.RemoveWhere(candidate => IsRelativePathUnderDirectory(candidate, normalized));
                foreach (string file in UpdaterFileSystem.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    managedPaths.Add(GetRelativePath(appDirectory, file));
                }
                return;
            }

            if (UpdaterFileSystem.FileExists(path))
            {
                managedPaths.Add(normalized);
            }
        }

        private static void TryRollback(
            string appDirectory,
            string previousDirectory,
            HashSet<string> appliedPaths,
            IEnumerable<BackupEntry> backupEntries,
            IEnumerable<string> createdDirectories)
        {
            var failures = new List<Exception>();
            foreach (string relativePath in appliedPaths.OrderByDescending(path => path.Length))
            {
                try
                {
                    SafeDeletePath(Path.Combine(appDirectory, relativePath));
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
            }

            try
            {
                RemoveCreatedDirectories(createdDirectories);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }

            foreach (BackupEntry backupEntry in backupEntries.OrderBy(entry => entry.RelativePath.Length))
            {
                try
                {
                    RestoreBackupPath(backupEntry, appDirectory, previousDirectory);
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
            }

            if (failures.Count > 0)
            {
                throw new AggregateException("The update failed and rollback could not restore every managed path.", failures);
            }
        }

        private static void CreateDestinationDirectory(
            string appDirectory,
            string destination,
            ICollection<string> createdDirectories)
        {
            string directory = Path.GetDirectoryName(destination);
            if (string.IsNullOrWhiteSpace(directory))
            {
                return;
            }

            string normalizedRoot = UpdaterFileSystem.NormalizePath(appDirectory);
            string current = UpdaterFileSystem.NormalizePath(directory);
            var missingDirectories = new List<string>();
            while (!string.Equals(current, normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                if (!current.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Destination directory escaped the application directory: " + destination);
                }

                EnsureNoReparsePointIfPresent(current);
                if (UpdaterFileSystem.DirectoryExists(current))
                {
                    break;
                }
                if (UpdaterFileSystem.FileExists(current))
                {
                    throw new IOException("A file blocks the destination directory: " + current);
                }

                missingDirectories.Add(current);
                current = Path.GetDirectoryName(current)
                    ?? throw new InvalidOperationException("Destination directory has no application ancestor: " + destination);
            }

            foreach (string missingDirectory in missingDirectories.AsEnumerable().Reverse())
            {
                UpdaterFileSystem.CreateDirectory(missingDirectory);
                createdDirectories.Add(missingDirectory);
            }
        }

        private static void RemoveCreatedDirectories(IEnumerable<string> createdDirectories)
        {
            foreach (string directory in createdDirectories
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(path => path.Length))
            {
                if (!UpdaterFileSystem.DirectoryExists(directory))
                {
                    continue;
                }

                EnsureNoReparsePointEntry(directory);
                if (!UpdaterFileSystem.EnumerateFileSystemEntries(directory).Any())
                {
                    UpdaterFileSystem.DeleteDirectory(directory, recursive: false);
                }
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

        private static void AddLegacyManagedPaths(HashSet<string> managedPaths, string appDirectory)
        {
            foreach (string relativePath in LegacyManagedFilePaths)
            {
                AddLegacyManagedFilePath(managedPaths, appDirectory, relativePath);
            }
        }

        private static void AddLegacyManagedFilePath(HashSet<string> managedPaths, string appDirectory, string relativePath)
        {
            string normalized = NormalizeRelativePackagePath(relativePath);
            if (UpdaterFileSystem.FileExists(Path.Combine(appDirectory, normalized)))
            {
                managedPaths.Add(normalized);
            }
        }

        private static bool IsRelativePathUnderDirectory(string relativePath, string directoryPath)
        {
            string normalizedRelativePath = NormalizeRelativePackagePath(relativePath);
            string normalizedDirectoryPath = NormalizeRelativePackagePath(directoryPath);
            string directoryPrefix = normalizedDirectoryPath + Path.DirectorySeparatorChar;
            return normalizedRelativePath.StartsWith(directoryPrefix, StringComparison.OrdinalIgnoreCase);
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

        private static void MoveExistingPathToBackup(
            string appDirectory,
            string previousDirectory,
            string relativePath,
            ICollection<BackupEntry> backupEntries)
        {
            relativePath = NormalizeRelativePackagePath(relativePath);
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                return;
            }

            string source = Path.Combine(appDirectory, relativePath);
            EnsureNoReparsePointIfPresent(source);
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
                backupEntries.Add(new BackupEntry(relativePath, isDirectory: false));
            }
            else if (UpdaterFileSystem.DirectoryExists(source))
            {
                RemoveEmptyDirectoriesUnderPath(source);
                if (UpdaterFileSystem.EntryExists(destination))
                {
                    if (!UpdaterFileSystem.DirectoryExists(destination)
                        || UpdaterFileSystem.EnumerateFileSystemEntries(source).Any())
                    {
                        throw new IOException("Cannot replace an existing backup path with a non-empty directory: " + source);
                    }

                    UpdaterFileSystem.DeleteDirectory(source, recursive: false);
                    return;
                }

                UpdaterFileSystem.MoveDirectory(source, destination);
                backupEntries.Add(new BackupEntry(relativePath, isDirectory: true));
            }
        }

        private static void RemoveEmptyDirectoriesUnderPath(string rootDirectory)
        {
            if (!UpdaterFileSystem.DirectoryExists(rootDirectory))
            {
                return;
            }

            var directories = new List<string>();
            var pending = new Stack<string>();
            pending.Push(rootDirectory);
            while (pending.Count > 0)
            {
                string parent = pending.Pop();
                foreach (string entry in UpdaterFileSystem.EnumerateFileSystemEntries(parent))
                {
                    if (!UpdaterFileSystem.DirectoryExists(entry))
                    {
                        continue;
                    }

                    EnsureNoReparsePointEntry(entry);
                    directories.Add(entry);
                    pending.Push(entry);
                }
            }

            foreach (string directory in directories.OrderByDescending(path => path.Length))
            {
                if (UpdaterFileSystem.DirectoryExists(directory)
                    && !UpdaterFileSystem.EnumerateFileSystemEntries(directory).Any())
                {
                    UpdaterFileSystem.DeleteDirectory(directory, recursive: false);
                }
            }
        }

        private static void RestoreBackupPath(BackupEntry backupEntry, string appDirectory, string previousDirectory)
        {
            string source = Path.Combine(previousDirectory, backupEntry.RelativePath);
            if (!UpdaterFileSystem.EntryExists(source))
            {
                return;
            }

            string destination = Path.Combine(appDirectory, backupEntry.RelativePath);
            EnsureNoReparsePointAncestors(appDirectory, backupEntry.RelativePath);
            if (backupEntry.IsDirectory)
            {
                if (UpdaterFileSystem.FileExists(destination))
                {
                    EnsureNoReparsePointEntry(destination);
                    UpdaterFileSystem.DeleteFile(destination);
                }
                else if (!UpdaterFileSystem.DirectoryExists(destination))
                {
                    UpdaterFileSystem.CreateDirectory(destination);
                }

                RestoreBackupDirectoryContents(source, destination);
                if (!UpdaterFileSystem.EnumerateFileSystemEntries(source).Any())
                {
                    UpdaterFileSystem.DeleteDirectory(source, recursive: false);
                }
                return;
            }

            UpdaterFileSystem.CreateDirectory(Path.GetDirectoryName(destination));
            if (UpdaterFileSystem.FileExists(destination))
            {
                EnsureNoReparsePointEntry(destination);
                UpdaterFileSystem.DeleteFile(destination);
            }
            else if (UpdaterFileSystem.DirectoryExists(destination))
            {
                EnsureNoReparsePointEntry(destination);
                if (UpdaterFileSystem.EnumerateFileSystemEntries(destination).Any())
                {
                    throw new IOException("Cannot restore a file over a non-empty directory: " + destination);
                }
                UpdaterFileSystem.DeleteDirectory(destination, recursive: false);
            }

            UpdaterFileSystem.MoveFile(source, destination);
        }

        private static void RestoreBackupDirectoryContents(string source, string destination)
        {
            foreach (string sourceChild in UpdaterFileSystem.EnumerateFileSystemEntries(source))
            {
                string childName = Path.GetFileName(sourceChild);
                string destinationChild = Path.Combine(destination, childName);
                if (UpdaterFileSystem.FileExists(sourceChild))
                {
                    if (UpdaterFileSystem.FileExists(destinationChild))
                    {
                        EnsureNoReparsePointEntry(destinationChild);
                        UpdaterFileSystem.DeleteFile(destinationChild);
                    }
                    else if (UpdaterFileSystem.DirectoryExists(destinationChild))
                    {
                        EnsureNoReparsePointEntry(destinationChild);
                        if (UpdaterFileSystem.EnumerateFileSystemEntries(destinationChild).Any())
                        {
                            throw new IOException("Cannot restore a file over a non-empty directory: " + destinationChild);
                        }
                        UpdaterFileSystem.DeleteDirectory(destinationChild, recursive: false);
                    }

                    UpdaterFileSystem.MoveFile(sourceChild, destinationChild);
                    continue;
                }

                if (!UpdaterFileSystem.DirectoryExists(sourceChild))
                {
                    continue;
                }

                if (UpdaterFileSystem.FileExists(destinationChild))
                {
                    EnsureNoReparsePointEntry(destinationChild);
                    UpdaterFileSystem.DeleteFile(destinationChild);
                }
                else if (UpdaterFileSystem.DirectoryExists(destinationChild))
                {
                    EnsureNoReparsePointEntry(destinationChild);
                }
                else
                {
                    UpdaterFileSystem.CreateDirectory(destinationChild);
                }

                RestoreBackupDirectoryContents(sourceChild, destinationChild);
                if (!UpdaterFileSystem.EnumerateFileSystemEntries(sourceChild).Any())
                {
                    UpdaterFileSystem.DeleteDirectory(sourceChild, recursive: false);
                }
            }
        }

        private static void RemoveEmptyDirectories(string appDirectory)
        {
            foreach (string directory in EnumerateDirectoriesWithoutReparsePoints(appDirectory).OrderByDescending(path => path.Length))
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

        private static IEnumerable<string> EnumerateDirectoriesWithoutReparsePoints(string rootDirectory)
        {
            var pending = new Stack<string>();
            pending.Push(rootDirectory);
            while (pending.Count > 0)
            {
                string parent = pending.Pop();
                foreach (string entry in UpdaterFileSystem.EnumerateFileSystemEntries(parent))
                {
                    if (!UpdaterFileSystem.DirectoryExists(entry))
                    {
                        continue;
                    }

                    if (string.Equals(parent, rootDirectory, StringComparison.OrdinalIgnoreCase)
                        && PreservedTopLevelNames.Contains(Path.GetFileName(entry)))
                    {
                        continue;
                    }

                    EnsureNoReparsePointEntry(entry);
                    yield return entry;
                    pending.Push(entry);
                }
            }
        }

        private static void EnsureNoReparsePoint(string path)
        {
            EnsureNoReparsePointEntry(path);

            if (UpdaterFileSystem.DirectoryExists(path))
            {
                foreach (string child in UpdaterFileSystem.EnumerateFileSystemEntries(path))
                {
                    EnsureNoReparsePoint(child);
                }
            }
        }

        private static void EnsureNoReparsePointEntry(string path)
        {
            FileAttributes attributes = UpdaterFileSystem.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException("Reparse points are not supported in the application directory: " + path);
            }
        }

        private static void EnsureNoReparsePointAncestors(string appDirectory, string relativePath)
        {
            string[] segments = NormalizeAncestorPath(relativePath)
                .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            string current = appDirectory;
            for (int i = 0; i < segments.Length - 1; i++)
            {
                current = Path.Combine(current, segments[i]);
                EnsureNoReparsePointIfPresent(current);
            }
        }

        private static string NormalizeAncestorPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            string normalized = path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar).Trim();
            if (Path.IsPathRooted(normalized) || normalized.Contains(":"))
            {
                throw new InvalidOperationException("Ancestor path must be relative: " + path);
            }

            string[] segments = normalized.Split(new[] { Path.DirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Any(segment => segment == "." || segment == ".."))
            {
                throw new InvalidOperationException("Ancestor path must not contain traversal: " + path);
            }

            return string.Join(Path.DirectorySeparatorChar.ToString(), segments);
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

        private sealed class BackupEntry
        {
            public BackupEntry(string relativePath, bool isDirectory)
            {
                RelativePath = relativePath;
                IsDirectory = isDirectory;
            }

            public string RelativePath { get; }

            public bool IsDirectory { get; }
        }

        private sealed class RollbackFailureException : Exception
        {
            public RollbackFailureException(Exception updateException, Exception rollbackException)
                : base(
                    "The update failed and rollback could not be completed.",
                    new AggregateException(updateException, rollbackException))
            {
            }
        }

        private sealed class UpdateRequest
        {
            public string AppDirectory { get; private set; }

            public string PackagePath { get; private set; }

            public string BackupDirectory { get; private set; }

            public string ReadyFilePath { get; private set; }

            public string DecisionFilePath { get; private set; }

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

                string processIdText = ReadRequired(values, "--pid");
                if (!int.TryParse(processIdText, out int processId) || processId <= 0)
                {
                    throw new ArgumentException("The --pid argument must be a positive process id.");
                }

                return new UpdateRequest
                {
                    AppDirectory = ReadRequired(values, "--app-dir"),
                    PackagePath = ReadRequired(values, "--package"),
                    BackupDirectory = ReadRequired(values, "--backup-dir"),
                    ReadyFilePath = ReadRequired(values, "--ready-file"),
                    DecisionFilePath = ReadRequired(values, "--decision-file"),
                    ProcessId = processId,
                    RestartExePath = ReadRequired(values, "--restart-exe")
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
