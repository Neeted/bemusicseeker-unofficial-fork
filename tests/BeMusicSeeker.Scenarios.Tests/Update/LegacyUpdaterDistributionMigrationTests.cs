using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Processes;
using BeMusicSeeker.TestSupport.Verification;

namespace BeMusicSeeker.Tests;

[TestClass]
[TestCategory("ReleaseAcceptance")]
public sealed class LegacyUpdaterDistributionMigrationTests
{
    /// <summary>固定v2.1.6.0実updaterがCurrent ZIPを適用し、全管理bytes・集合・旧不要file退役・利用者データ保持を確認します。</summary>
    [TestMethod]
    public async Task PublishedLegacyUpdater_MigratesManagedFilesAndPreservesUserFiles()
    {
        var deadline = ProcessDeadline.FromTimeout(TimeSpan.FromSeconds(180));
        var cleanupDeadline = new Lazy<ProcessDeadline>(() => ProcessDeadline.FromTimeout(TimeSpan.FromSeconds(10)));
        var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string appRoot = workspace.GetPath("app");
        var processes = new List<OwnedProcess>();
        var cleanupFailures = new List<Exception>();
        Exception? primaryFailure = null;
        try
        {
            string repositoryRoot = UpdateDeploymentTestSupport.FindRepositoryRoot();
            DistributionCatalog catalog = await DistributionCatalog.ReadFromEnvironmentAsync(repositoryRoot, deadline);
            string legacyPackagePath = ValidatePinnedArtifact(repositoryRoot, catalog.Legacy.PackagePath);
            string currentPackagePath = catalog.Current.PackagePath;
            ValidateCurrentPackageName(currentPackagePath);
            string updaterPackagePath = workspace.GetPath(Path.GetFileName(currentPackagePath));
            ArchiveFile currentPackageIdentity = ReadFile(currentPackagePath);
            File.Copy(currentPackagePath, updaterPackagePath);
            AssertArchiveFileMatches(currentPackageIdentity, ReadFile(updaterPackagePath), Path.GetFileName(currentPackagePath));

            await catalog.Legacy.ExtractInstallationAsync(workspace, "app", deadline);

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

            DistributionValidation.CheckDeadline(deadline, CancellationToken.None);
            var process = OwnedProcess.Start(new ProcessStartRequest(startInfo.FileName, startInfo.ArgumentList.ToArray(), appRoot), () => cleanupDeadline.Value);
            processes.Add(process);
            ProcessCompletion updaterResult = await process.WaitForExitAsync(deadline);
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
            DistributionValidation.CheckDeadline(deadline, CancellationToken.None);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
            if (exception is OwnedProcessStartException { CleanupFailure: not null } failed) { cleanupFailures.Add(failed.CleanupFailure); }
        }
        finally
        {
            foreach (OwnedProcess owned in processes.AsEnumerable().Reverse())
            {
                try
                {
                    ProcessStopResult stopped = await owned.StopAsync(null, cleanupDeadline.Value);
                    if (stopped.CleanupFailure is not null) { cleanupFailures.Add(stopped.CleanupFailure); }
                    await owned.DisposeAsync();
                }
                catch (Exception exception) { cleanupFailures.Add(exception); }
            }
            if (primaryFailure is not null || cleanupFailures.Count > 0) { workspace.PreserveArtifacts(); }
            try { workspace.Dispose(); }
            catch (Exception exception) { cleanupFailures.Add(exception); }
        }
        if (primaryFailure is not null)
        {
            if (cleanupFailures.Count > 0) { primaryFailure.Data["UpdaterCleanupSecondaryDiagnostics"] = new AggregateException(cleanupFailures); }
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        }
        if (cleanupFailures.Count > 0) { throw new AggregateException(cleanupFailures); }
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

    private sealed record ArchiveFile(long Length, string Sha256);

    private sealed record FileSnapshot(HashSet<string> Directories, Dictionary<string, ArchiveFile> Files);

}
