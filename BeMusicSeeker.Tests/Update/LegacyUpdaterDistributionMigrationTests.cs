using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
[TestCategory("ReleaseAcceptance")]
public sealed class LegacyUpdaterDistributionMigrationTests
{
    private const string ArtifactPathEnvironmentVariable = "BMS_LEGACY_UPDATER_ARTIFACT_PATH";
    private const string CurrentPackagePathEnvironmentVariable = "BMS_LEGACY_UPDATER_CURRENT_PACKAGE_PATH";
    private const string ExecutionDeadlineEnvironmentVariable = "BMS_LEGACY_UPDATER_EXECUTION_DEADLINE_UTC";
    private const string CleanupDeadlineEnvironmentVariable = "BMS_LEGACY_UPDATER_CLEANUP_DEADLINE_UTC";

    [TestMethod]
    public async Task PublishedLegacyUpdater_MigratesManagedFilesAndPreservesUserFiles()
    {
        string repositoryRoot = FindRepositoryRoot();
        string legacyPackagePath = ValidatePinnedArtifact(repositoryRoot, GetRequiredEnvironmentPath(ArtifactPathEnvironmentVariable));
        string currentPackagePath = GetRequiredEnvironmentPath(CurrentPackagePathEnvironmentVariable);
        ValidateCurrentPackageName(currentPackagePath);
        (DateTime executionDeadlineUtc, DateTime cleanupDeadlineUtc) = ReadDeadlines();

        string sandboxRoot = Path.Combine(
            Path.GetTempPath(),
            "BeMusicSeeker-LegacyUpdaterDistributionMigration",
            Guid.NewGuid().ToString("N"));
        string appRoot = Path.Combine(sandboxRoot, "app");
        Directory.CreateDirectory(appRoot);
        string updaterPackagePath = Path.Combine(sandboxRoot, Path.GetFileName(currentPackagePath));

        ExceptionDispatchInfo? primaryFailure = null;
        string? cleanupFailure = null;
        try
        {
            ArchiveFile currentPackageIdentity = ReadFile(currentPackagePath);
            File.Copy(currentPackagePath, updaterPackagePath);
            AssertArchiveFileMatches(currentPackageIdentity, ReadFile(updaterPackagePath), Path.GetFileName(currentPackagePath));

            ZipFile.ExtractToDirectory(legacyPackagePath, appRoot);

            string updaterPath = Path.Combine(appRoot, "BeMusicSeeker.Updater.exe");
            Assert.IsTrue(File.Exists(updaterPath), "The pinned v2.1.6.0 distribution does not contain its released updater.");

            string installedManifestPath = Path.Combine(appRoot, "update-managed-files.txt");
            Assert.IsTrue(File.Exists(installedManifestPath), "The pinned distribution does not contain its managed-file list.");
            HashSet<string> oldManagedPaths = ReadManagedPaths(File.OpenRead(installedManifestPath));
            Assert.IsTrue(oldManagedPaths.Count > 0, "The pinned distribution's managed-file list is empty.");
            foreach (string relativePath in oldManagedPaths)
            {
                Assert.IsTrue(
                    File.Exists(ResolvePackagePath(appRoot, relativePath)),
                    "The pinned distribution is missing a file from its managed-file list: " + relativePath);
            }

            Dictionary<string, ArchiveFile> currentManagedFiles;
            using (ZipArchive currentArchive = ZipFile.OpenRead(updaterPackagePath))
            {
                Dictionary<string, ZipArchiveEntry> currentEntries = ReadArchiveEntries(currentArchive);
                ZipArchiveEntry currentManifestEntry = GetEntry(currentEntries, "update-managed-files.txt");
                currentManagedFiles = ReadManagedFilesFromArchive(currentEntries, currentManifestEntry);

                HashSet<string> packagePayloadPaths = new(currentEntries.Keys, StringComparer.OrdinalIgnoreCase);
                packagePayloadPaths.Remove("update-managed-files.txt");
                CollectionAssert.AreEquivalent(
                    packagePayloadPaths.OrderBy(static path => path, StringComparer.OrdinalIgnoreCase).ToArray(),
                    currentManagedFiles.Keys.OrderBy(static path => path, StringComparer.OrdinalIgnoreCase).ToArray(),
                    "The current archive's managed-file list must name every package payload exactly once.");
            }

            CreatePreservedFixtures(appRoot);
            FileSnapshot preservedBefore = CapturePreservedFiles(appRoot);

            var startInfo = new ProcessStartInfo
            {
                FileName = updaterPath,
                WorkingDirectory = appRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
            };
            startInfo.ArgumentList.Add("--app-dir");
            startInfo.ArgumentList.Add(appRoot);
            startInfo.ArgumentList.Add("--package");
            startInfo.ArgumentList.Add(updaterPackagePath);
            startInfo.ArgumentList.Add("--backup-dir");
            startInfo.ArgumentList.Add(Path.Combine(appRoot, "update_backup"));
            startInfo.ArgumentList.Add("--pid");
            startInfo.ArgumentList.Add("0");

            UpdaterProcessResult updaterResult = await RunOwnedUpdaterAsync(
                startInfo,
                executionDeadlineUtc,
                cleanupDeadlineUtc);
            Assert.AreEqual(
                0,
                updaterResult.ExitCode,
                "The released v2.1.6.0 updater failed."
                + Environment.NewLine
                + "stdout: " + updaterResult.StandardOutput
                + Environment.NewLine
                + "stderr: " + updaterResult.StandardError);

            foreach ((string relativePath, ArchiveFile expected) in currentManagedFiles)
            {
                string installedPath = ResolvePackagePath(appRoot, relativePath);
                Assert.IsTrue(File.Exists(installedPath), "Current managed file is missing after migration: " + relativePath);
                AssertArchiveFileMatches(expected, ReadFile(installedPath), relativePath);
            }

            HashSet<string> installedManagedPaths = ReadManagedPaths(File.OpenRead(Path.Combine(appRoot, "update-managed-files.txt")));
            CollectionAssert.AreEquivalent(
                currentManagedFiles.Keys.OrderBy(static path => path, StringComparer.OrdinalIgnoreCase).ToArray(),
                installedManagedPaths.OrderBy(static path => path, StringComparer.OrdinalIgnoreCase).ToArray(),
                "The updater did not install the current managed-file list.");

            foreach (string obsoletePath in oldManagedPaths.Except(currentManagedFiles.Keys, StringComparer.OrdinalIgnoreCase))
            {
                Assert.IsFalse(
                    File.Exists(ResolvePackagePath(appRoot, obsoletePath)),
                    "A file managed by the old distribution but omitted from the current package remains: " + obsoletePath);
            }

            FileSnapshot preservedAfter = CapturePreservedFiles(appRoot);
            AssertSnapshotsEqual(preservedBefore, preservedAfter);
        }
        catch (Exception exception)
        {
            primaryFailure = ExceptionDispatchInfo.Capture(exception);
        }
        finally
        {
            try
            {
                if (Directory.Exists(sandboxRoot))
                {
                    Directory.Delete(sandboxRoot, recursive: true);
                }
            }
            catch (Exception exception)
            {
                cleanupFailure = $"Acceptance sandbox cleanup failed for '{sandboxRoot}': {exception.GetType().Name}: {exception.Message}";
            }
        }

        if (primaryFailure is not null)
        {
            if (!string.IsNullOrWhiteSpace(cleanupFailure))
            {
                primaryFailure.SourceException.Data["Secondary sandbox cleanup failure"] = cleanupFailure;
            }

            primaryFailure.Throw();
        }

        if (!string.IsNullOrWhiteSpace(cleanupFailure))
        {
            Assert.Fail(cleanupFailure);
        }
    }

    private static async Task<UpdaterProcessResult> RunOwnedUpdaterAsync(
        ProcessStartInfo startInfo,
        DateTime executionDeadlineUtc,
        DateTime cleanupDeadlineUtc)
    {
        using var process = new Process { StartInfo = startInfo };
        Task<string>? standardOutputTask = null;
        Task<string>? standardErrorTask = null;
        int? processId = null;
        bool started = false;
        ExceptionDispatchInfo? primaryFailure = null;
        var cleanupFailures = new List<string>();
        UpdaterProcessResult? result = null;

        try
        {
            if (!process.Start())
            {
                throw new AssertFailedException("The released v2.1.6.0 updater did not start.");
            }

            started = true;
            processId = process.Id;
            standardOutputTask = process.StandardOutput.ReadToEndAsync();
            standardErrorTask = process.StandardError.ReadToEndAsync();

            TimeSpan processWait = executionDeadlineUtc - DateTime.UtcNow;
            if (processWait <= TimeSpan.Zero)
            {
                throw new TimeoutException("The ReleaseAcceptance execution deadline elapsed before the legacy updater could finish.");
            }

            try
            {
                await process.WaitForExitAsync().WaitAsync(processWait);
            }
            catch (TimeoutException exception)
            {
                throw new AssertFailedException(
                    $"The released v2.1.6.0 updater exceeded its remaining {processWait.TotalSeconds:F1}-second ReleaseAcceptance deadline (PID {processId}).",
                    exception);
            }

            TimeSpan streamWait = GetRemaining(executionDeadlineUtc, "legacy updater redirected-stream completion");
            try
            {
                await Task.WhenAll(standardOutputTask, standardErrorTask).WaitAsync(streamWait);
            }
            catch (TimeoutException exception)
            {
                throw new AssertFailedException(
                    $"The released v2.1.6.0 updater output did not close before the execution deadline (PID {processId}).",
                    exception);
            }

            result = new UpdaterProcessResult(
                process.ExitCode,
                standardOutputTask.GetAwaiter().GetResult(),
                standardErrorTask.GetAwaiter().GetResult());
        }
        catch (Exception exception)
        {
            primaryFailure = ExceptionDispatchInfo.Capture(exception);
        }
        finally
        {
            if (started)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch (Exception exception)
                {
                    cleanupFailures.Add($"Could not stop owned updater PID {processId}: {exception.GetType().Name}: {exception.Message}");
                }

                try
                {
                    if (!process.HasExited)
                    {
                        await process.WaitForExitAsync().WaitAsync(GetRemaining(cleanupDeadlineUtc, "owned updater process cleanup"));
                    }
                }
                catch (Exception exception)
                {
                    cleanupFailures.Add($"Could not reap owned updater PID {processId}: {exception.GetType().Name}: {exception.Message}");
                }

                await DrainOutputAsync(standardOutputTask, "stdout", processId, cleanupDeadlineUtc, cleanupFailures);
                await DrainOutputAsync(standardErrorTask, "stderr", processId, cleanupDeadlineUtc, cleanupFailures);
            }
        }

        if (primaryFailure is not null)
        {
            if (cleanupFailures.Count > 0)
            {
                primaryFailure.SourceException.Data["Secondary legacy updater cleanup failure"] = string.Join("; ", cleanupFailures);
            }

            primaryFailure.Throw();
        }

        if (cleanupFailures.Count > 0)
        {
            throw new AssertFailedException("Legacy updater process cleanup failed: " + string.Join("; ", cleanupFailures));
        }

        return result ?? throw new AssertFailedException("The legacy updater produced no process result.");
    }

    private static async Task DrainOutputAsync(
        Task<string>? outputTask,
        string streamName,
        int? processId,
        DateTime cleanupDeadlineUtc,
        List<string> failures)
    {
        if (outputTask is null)
        {
            return;
        }

        try
        {
            await outputTask.WaitAsync(GetRemaining(cleanupDeadlineUtc, $"legacy updater {streamName} drain"));
        }
        catch (Exception exception)
        {
            failures.Add($"Could not drain legacy updater {streamName} for PID {processId}: {exception.GetType().Name}: {exception.Message}");
        }
    }

    private static TimeSpan GetRemaining(DateTime deadlineUtc, string operation)
    {
        TimeSpan remaining = deadlineUtc - DateTime.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            throw new TimeoutException($"The ReleaseAcceptance cleanup deadline elapsed during {operation}.");
        }

        return remaining;
    }

    private static string ValidatePinnedArtifact(string repositoryRoot, string artifactPath)
    {
        string metadataPath = Path.Combine(repositoryRoot, "devdocs", "acceptance", "v216-first-hop", "artifact.json");
        using var metadata = JsonDocument.Parse(File.ReadAllText(metadataPath));
        JsonElement root = metadata.RootElement;

        Assert.AreEqual(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.AreEqual("BeMusicSeeker.V216Artifact", root.GetProperty("manifestType").GetString());
        Assert.AreEqual("public-v2.1.6.0", root.GetProperty("artifactId").GetString());
        Assert.AreEqual("2.1.6.0", root.GetProperty("version").GetString());
        Assert.AreEqual(1, root.GetProperty("packageFormatVersion").GetInt32());
        Assert.AreEqual("published-bemusicseeker-unofficial-fork-v2.1.6.0.zip", root.GetProperty("fileName").GetString());
        Assert.IsTrue(root.GetProperty("sealed").GetBoolean(), "The pinned legacy artifact must remain sealed.");

        string metadataArtifactPath = root.GetProperty("artifactPath").GetString()
            ?? throw new AssertFailedException("The pinned legacy artifact metadata has no artifactPath.");
        string expectedPath = Path.GetFullPath(Path.Combine(repositoryRoot, metadataArtifactPath.Replace('/', Path.DirectorySeparatorChar)));
        Assert.AreEqual(expectedPath, artifactPath, ignoreCase: true, "The updater source must be the pinned v2.1.6.0 ZIP.");
        Assert.AreEqual(root.GetProperty("fileName").GetString(), Path.GetFileName(artifactPath));

        var artifactFile = new FileInfo(artifactPath);
        Assert.IsTrue(artifactFile.Exists, "The pinned v2.1.6.0 ZIP is missing: " + artifactPath);
        Assert.AreEqual(root.GetProperty("sizeBytes").GetInt64(), artifactFile.Length, "The pinned ZIP size changed.");
        string actualHash;
        using (FileStream stream = File.OpenRead(artifactPath))
        {
            actualHash = Convert.ToHexString(SHA256.HashData(stream));
        }

        Assert.AreEqual(root.GetProperty("sha256").GetString(), actualHash, ignoreCase: true, "The pinned ZIP hash changed.");
        return artifactPath;
    }

    private static void ValidateCurrentPackageName(string packagePath)
    {
        Assert.IsTrue(File.Exists(packagePath), "The current Full distribution ZIP is missing: " + packagePath);
        string fileName = Path.GetFileName(packagePath);
        Assert.IsTrue(
            System.Text.RegularExpressions.Regex.IsMatch(
                fileName,
                @"^bemusicseeker-unofficial-fork-v(?<version>\d+\.\d+\.\d+\.\d+)\.zip$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant),
            "The current distribution package has an invalid versioned name: " + fileName);
        Assert.AreNotEqual("2.1.6.0", System.Text.RegularExpressions.Regex.Match(fileName, @"v(?<version>\d+\.\d+\.\d+\.\d+)").Groups["version"].Value);
    }

    private static (DateTime ExecutionDeadlineUtc, DateTime CleanupDeadlineUtc) ReadDeadlines()
    {
        DateTime executionDeadlineUtc = ReadDeadline(ExecutionDeadlineEnvironmentVariable);
        DateTime cleanupDeadlineUtc = ReadDeadline(CleanupDeadlineEnvironmentVariable);
        Assert.IsTrue(cleanupDeadlineUtc >= executionDeadlineUtc, "ReleaseAcceptance cleanup deadline precedes its execution deadline.");
        Assert.IsTrue(executionDeadlineUtc > DateTime.UtcNow, "The ReleaseAcceptance execution deadline has elapsed.");
        return (executionDeadlineUtc, cleanupDeadlineUtc);
    }

    private static DateTime ReadDeadline(string environmentVariableName)
    {
        string? value = Environment.GetEnvironmentVariable(environmentVariableName);
        Assert.IsFalse(string.IsNullOrWhiteSpace(value), "Missing ReleaseAcceptance deadline: " + environmentVariableName);
        Assert.IsTrue(
            DateTime.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out DateTime deadlineUtc),
            "Invalid ReleaseAcceptance deadline: " + environmentVariableName);
        return deadlineUtc;
    }

    private static string GetRequiredEnvironmentPath(string environmentVariableName)
    {
        string? value = Environment.GetEnvironmentVariable(environmentVariableName);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new AssertFailedException("Missing ReleaseAcceptance input: " + environmentVariableName);
        }

        string path = Path.GetFullPath(value);
        Assert.IsTrue(File.Exists(path), "ReleaseAcceptance input file is missing: " + path);
        return path;
    }

    private static Dictionary<string, ZipArchiveEntry> ReadArchiveEntries(ZipArchive archive)
    {
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (entry.FullName.EndsWith("/", StringComparison.Ordinal) || entry.FullName.EndsWith("\\", StringComparison.Ordinal))
            {
                continue;
            }

            string relativePath = NormalizeRelativePath(entry.FullName);
            Assert.IsTrue(entries.TryAdd(relativePath, entry), "The current package contains duplicate paths: " + relativePath);
        }

        return entries;
    }

    private static Dictionary<string, ArchiveFile> ReadManagedFilesFromArchive(
        Dictionary<string, ZipArchiveEntry> archiveEntries,
        ZipArchiveEntry manifestEntry)
    {
        var result = new Dictionary<string, ArchiveFile>(StringComparer.OrdinalIgnoreCase);
        using Stream manifestStream = manifestEntry.Open();
        using var reader = new StreamReader(manifestStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            string relativePath = NormalizeRelativePath(line);
            Assert.IsFalse(
                string.Equals(relativePath, "update-managed-files.txt", StringComparison.OrdinalIgnoreCase),
                "The managed-file list cannot list itself.");
            if (!archiveEntries.TryGetValue(relativePath, out ZipArchiveEntry? entry) || entry is null)
            {
                throw new AssertFailedException("The managed file is missing from the current package: " + relativePath);
            }

            Assert.IsTrue(result.TryAdd(relativePath, ReadArchiveFile(entry)), "The current managed-file list contains a duplicate path: " + relativePath);
        }

        Assert.IsTrue(result.Count > 0, "The current package's managed-file list is empty.");
        return result;
    }

    private static HashSet<string> ReadManagedPaths(Stream manifestStream)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (manifestStream)
        using (var reader = new StreamReader(manifestStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
        {
            while (reader.ReadLine() is { } line)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    string relativePath = NormalizeRelativePath(line);
                    Assert.IsTrue(result.Add(relativePath), "The pinned distribution's managed-file list contains a duplicate path: " + relativePath);
                }
            }
        }

        return result;
    }

    private static ZipArchiveEntry GetEntry(Dictionary<string, ZipArchiveEntry> entries, string relativePath)
    {
        if (!entries.TryGetValue(relativePath, out ZipArchiveEntry? entry) || entry is null)
        {
            throw new AssertFailedException("Required package entry is missing: " + relativePath);
        }

        return entry;
    }

    private static string NormalizeRelativePath(string relativePath)
    {
        string normalized = relativePath.Trim().Replace('\\', '/');
        Assert.IsFalse(string.IsNullOrWhiteSpace(normalized), "Package paths cannot be empty.");
        Assert.IsFalse(Path.IsPathRooted(normalized), "Package paths must be relative: " + relativePath);
        string[] segments = normalized.Split('/');
        Assert.IsFalse(segments.Any(static segment => segment.Length == 0 || segment == "." || segment == ".."), "Package path escapes or aliases its root: " + relativePath);
        return string.Join('/', segments);
    }

    private static string ResolvePackagePath(string root, string relativePath)
    {
        string normalized = NormalizeRelativePath(relativePath);
        string rootPath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string fullPath = Path.GetFullPath(Path.Combine(rootPath, normalized.Replace('/', Path.DirectorySeparatorChar)));
        Assert.IsTrue(
            fullPath.StartsWith(rootPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            "Package path escapes its installation root: " + relativePath);
        return fullPath;
    }

    private static ArchiveFile ReadArchiveFile(ZipArchiveEntry entry)
    {
        using Stream stream = entry.Open();
        return new ArchiveFile(entry.Length, Convert.ToHexString(SHA256.HashData(stream)));
    }

    private static ArchiveFile ReadFile(string path)
    {
        var file = new FileInfo(path);
        using FileStream stream = File.OpenRead(path);
        return new ArchiveFile(file.Length, Convert.ToHexString(SHA256.HashData(stream)));
    }

    private static void AssertArchiveFileMatches(ArchiveFile expected, ArchiveFile actual, string relativePath)
    {
        Assert.AreEqual(expected.Length, actual.Length, "File size differs from the current distribution package: " + relativePath);
        Assert.AreEqual(expected.Sha256, actual.Sha256, true, "File content differs from the current distribution package: " + relativePath);
    }

    private static void CreatePreservedFixtures(string appRoot)
    {
        WriteFixture(appRoot, "config/user-options.fixture", "legacy portable config\n");
        WriteFixture(appRoot, "config/nested/user-settings.fixture", "legacy nested config\n");
        WriteFixture(appRoot, "data/user-library.fixture", "legacy library bytes\n");
        WriteFixture(appRoot, "data/nested/user-database.fixture", "legacy database bytes\n");
        WriteFixture(appRoot, "user-added/custom-content/notes.fixture", "user added content\n");
    }

    private static void WriteFixture(string root, string relativePath, string content)
    {
        string path = ResolvePackagePath(root, relativePath);
        string? directory = Path.GetDirectoryName(path);
        if (directory is null)
        {
            throw new AssertFailedException("Could not determine the fixture's parent directory: " + path);
        }

        Directory.CreateDirectory(directory);
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static FileSnapshot CapturePreservedFiles(string appRoot)
    {
        string[] roots = { "config", "data", "user-added" };
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new Dictionary<string, ArchiveFile>(StringComparer.OrdinalIgnoreCase);
        foreach (string relativeRoot in roots)
        {
            string absoluteRoot = ResolvePackagePath(appRoot, relativeRoot);
            Assert.IsTrue(Directory.Exists(absoluteRoot), "A preserved fixture directory is missing: " + relativeRoot);
            directories.Add(relativeRoot);
            foreach (string directory in Directory.EnumerateDirectories(absoluteRoot, "*", SearchOption.AllDirectories))
            {
                directories.Add(Path.GetRelativePath(appRoot, directory).Replace('\\', '/'));
            }

            foreach (string file in Directory.EnumerateFiles(absoluteRoot, "*", SearchOption.AllDirectories))
            {
                string relativePath = Path.GetRelativePath(appRoot, file).Replace('\\', '/');
                files.Add(relativePath, ReadFile(file));
            }
        }

        return new FileSnapshot(directories, files);
    }

    private static void AssertSnapshotsEqual(FileSnapshot before, FileSnapshot after)
    {
        CollectionAssert.AreEquivalent(
            before.Directories.OrderBy(static path => path, StringComparer.OrdinalIgnoreCase).ToArray(),
            after.Directories.OrderBy(static path => path, StringComparer.OrdinalIgnoreCase).ToArray(),
            "The updater changed the preserved config, data, or user-added directory set.");
        CollectionAssert.AreEquivalent(
            before.Files.Keys.OrderBy(static path => path, StringComparer.OrdinalIgnoreCase).ToArray(),
            after.Files.Keys.OrderBy(static path => path, StringComparer.OrdinalIgnoreCase).ToArray(),
            "The updater changed the preserved config, data, or user-added file path set.");
        foreach ((string relativePath, ArchiveFile expected) in before.Files)
        {
            AssertArchiveFileMatches(expected, after.Files[relativePath], relativePath);
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "BeMusicSeeker.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("The repository root was not found from the ReleaseAcceptance test output directory.");
    }

    private sealed record ArchiveFile(long Length, string Sha256);

    private sealed record FileSnapshot(HashSet<string> Directories, Dictionary<string, ArchiveFile> Files);

    private sealed record UpdaterProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
