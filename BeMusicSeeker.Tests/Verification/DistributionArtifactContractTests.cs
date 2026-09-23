using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
[TestCategory("ProcessIntegration")]
public sealed class DistributionArtifactContractTests
{
    [TestMethod]
    public void ManifestPassesCurrentPublishPathsToConsumers()
    {
        using ManifestFixture fixture = CreateFixture();
        PowerShellResult result = RunPowerShell(fixture.Root, @"
. $env:BMS_TEST_SCRIPT
New-DistributionArtifactManifest `
    -ArtifactRoot (Join-Path $env:BMS_TEST_ROOT 'distribution') `
    -RunId 'tests-full-run' `
    -ArtifactId 'artifact-001' `
    -CurrentAppRoot (Join-Path $env:BMS_TEST_ROOT 'distribution/current/app') `
    -CurrentUpdaterRoot (Join-Path $env:BMS_TEST_ROOT 'distribution/current/updater') `
    -CurrentPackagePath (Join-Path $env:BMS_TEST_ROOT 'distribution/current/dist/bemusicseeker-unofficial-fork-v1.2.3.zip') `
    -CurrentVersion '1.2.3' `
    -CurrentCommit 'current-commit' | Out-Null
$manifest = Read-DistributionArtifactManifest -ManifestPath $env:BMS_TEST_MANIFEST
[ordered]@{
    artifactId = $manifest.ArtifactId
    runId = $manifest.RunId
    appRoot = $manifest.Current.appRoot
    updaterRoot = $manifest.Current.updaterRoot
    packagePath = $manifest.Current.packagePath
} | ConvertTo-Json -Compress
");

        AssertPowerShellSuccess(result);
        using var document = JsonDocument.Parse(result.Output.Trim());
        Assert.AreEqual("artifact-001", document.RootElement.GetProperty("artifactId").GetString());
        Assert.AreEqual("tests-full-run", document.RootElement.GetProperty("runId").GetString());
        Assert.AreEqual(fixture.CurrentAppRoot, document.RootElement.GetProperty("appRoot").GetString());
        Assert.AreEqual(fixture.CurrentUpdaterRoot, document.RootElement.GetProperty("updaterRoot").GetString());
        Assert.AreEqual(fixture.CurrentPackagePath, document.RootElement.GetProperty("packagePath").GetString());
    }

    [TestMethod]
    public void V216ArtifactIdentityRequiresPinnedSizeAndHashWithoutFallback()
    {
        string repositoryRoot = FindRepositoryRoot();
        string metadataPath = Path.Combine(
            repositoryRoot,
            "devdocs",
            "acceptance",
            "v216-first-hop",
            "artifact.json");
        PowerShellResult result = RunPowerShell(repositoryRoot, $@"
. $env:BMS_TEST_SCRIPT
$artifact = Assert-V216ArtifactIdentity -MetadataPath {QuotePowerShellLiteral(metadataPath)} -RepositoryRoot {QuotePowerShellLiteral(repositoryRoot)}
[ordered]@{{ version = $artifact.Version; size = $artifact.ExpectedSizeBytes; sha256 = $artifact.ExpectedSha256; path = $artifact.ArtifactPath }} | ConvertTo-Json -Compress
");

        AssertPowerShellSuccess(result);
        using var document = JsonDocument.Parse(result.Output.Trim());
        Assert.AreEqual("2.1.6.0", document.RootElement.GetProperty("version").GetString());
        Assert.AreEqual(11260709, document.RootElement.GetProperty("size").GetInt64());
        Assert.AreEqual(
            "c2c460b6757478816912a59fea535209b2a960528c8996ffe12225ec7ced7bb2",
            document.RootElement.GetProperty("sha256").GetString());
        Assert.AreEqual(
            "published-bemusicseeker-unofficial-fork-v2.1.6.0.zip",
            Path.GetFileName(document.RootElement.GetProperty("path").GetString()));
    }

    [TestMethod]
    public void V216ArtifactIdentityFailsClosedForSizeHashAndMissingCandidate()
    {
        string repositoryRoot = FindRepositoryRoot();
        string metadataPath = Path.Combine(
            repositoryRoot,
            "devdocs",
            "acceptance",
            "v216-first-hop",
            "artifact.json");
        string tempRoot = Path.Combine(
            Path.GetTempPath(),
            "BeMusicSeeker-V216ArtifactContractTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            string exactArtifact = ReadMetadataArtifactPath(metadataPath);
            string sizeCandidate = Path.Combine(tempRoot, "size", "published-bemusicseeker-unofficial-fork-v2.1.6.0.zip");
            string hashCandidate = Path.Combine(tempRoot, "hash", "published-bemusicseeker-unofficial-fork-v2.1.6.0.zip");
            Directory.CreateDirectory(Path.GetDirectoryName(sizeCandidate)!);
            Directory.CreateDirectory(Path.GetDirectoryName(hashCandidate)!);
            File.Copy(exactArtifact, sizeCandidate);
            File.Copy(exactArtifact, hashCandidate);
            File.AppendAllText(sizeCandidate, "size mutation", Encoding.ASCII);
            byte[] hashMutation = File.ReadAllBytes(hashCandidate);
            hashMutation[^1] ^= 0x01;
            File.WriteAllBytes(hashCandidate, hashMutation);
            string sizeMismatchMetadata = Path.Combine(tempRoot, "size-mismatch.json");
            string hashMismatchMetadata = Path.Combine(tempRoot, "hash-mismatch.json");
            string missingMetadata = Path.Combine(tempRoot, "missing.json");
            WriteV216Metadata(sizeMismatchMetadata, sizeCandidate, 11260709, "C2C460B6757478816912A59FEA535209B2A960528C8996FFE12225EC7CED7BB2");
            WriteV216Metadata(hashMismatchMetadata, hashCandidate, 11260709, "C2C460B6757478816912A59FEA535209B2A960528C8996FFE12225EC7CED7BB2");
            WriteV216Metadata(
                missingMetadata,
                Path.Combine(tempRoot, "missing", "published-bemusicseeker-unofficial-fork-v2.1.6.0.zip"),
                11260709,
                "C2C460B6757478816912A59FEA535209B2A960528C8996FFE12225EC7CED7BB2");

            PowerShellResult result = RunPowerShell(tempRoot, $@"
. $env:BMS_TEST_SCRIPT
Assert-V216ArtifactIdentity -MetadataPath {QuotePowerShellLiteral(sizeMismatchMetadata)} -RepositoryRoot {QuotePowerShellLiteral(repositoryRoot)} | Out-Null
");
            AssertPowerShellFailure(result, "artifact size mismatch");

            result = RunPowerShell(tempRoot, $@"
. $env:BMS_TEST_SCRIPT
Assert-V216ArtifactIdentity -MetadataPath {QuotePowerShellLiteral(hashMismatchMetadata)} -RepositoryRoot {QuotePowerShellLiteral(repositoryRoot)} | Out-Null
");
            AssertPowerShellFailure(result, "artifact SHA-256 mismatch");

            result = RunPowerShell(tempRoot, $@"
. $env:BMS_TEST_SCRIPT
Assert-V216ArtifactIdentity -MetadataPath {QuotePowerShellLiteral(missingMetadata)} -RepositoryRoot {QuotePowerShellLiteral(repositoryRoot)} | Out-Null
");
            AssertPowerShellFailure(result, "artifact is missing");
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    private static string ReadMetadataArtifactPath(string metadataPath)
    {
        using var metadata = JsonDocument.Parse(File.ReadAllText(metadataPath));
        string relativePath = metadata.RootElement.GetProperty("artifactPath").GetString()!;
        return Path.GetFullPath(Path.Combine(FindRepositoryRoot(), relativePath));
    }

    private static void WriteV216Metadata(string path, string artifactPath, long sizeBytes, string sha256)
    {
        var metadata = new
        {
            schemaVersion = 1,
            manifestType = "BeMusicSeeker.V216Artifact",
            artifactId = "public-v2.1.6.0",
            version = "2.1.6.0",
            packageFormatVersion = 1,
            fileName = "published-bemusicseeker-unofficial-fork-v2.1.6.0.zip",
            artifactPath,
            downloadUrl = "https://github.com/Neeted/bemusicseeker-unofficial-fork/releases/download/v2.1.6.0/bemusicseeker-unofficial-fork-v2.1.6.0.zip",
            sizeBytes,
            sha256,
            sealedArtifact = true
        };
        string json = JsonSerializer.Serialize(new
        {
            schemaVersion = metadata.schemaVersion,
            manifestType = metadata.manifestType,
            artifactId = metadata.artifactId,
            version = metadata.version,
            packageFormatVersion = metadata.packageFormatVersion,
            fileName = metadata.fileName,
            artifactPath = metadata.artifactPath,
            downloadUrl = metadata.downloadUrl,
            sizeBytes = metadata.sizeBytes,
            sha256 = metadata.sha256,
            @sealed = metadata.sealedArtifact
        });
        File.WriteAllText(path, json);
    }

    private static ManifestFixture CreateFixture()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "BeMusicSeeker-DistributionArtifactContractTests",
            Guid.NewGuid().ToString("N"));
        string distributionRoot = Path.Combine(root, "distribution");
        string currentAppRoot = Path.Combine(distributionRoot, "current", "app");
        string currentUpdaterRoot = Path.Combine(distributionRoot, "current", "updater");
        string currentDistRoot = Path.Combine(distributionRoot, "current", "dist");
        Directory.CreateDirectory(Path.Combine(currentAppRoot, "nested"));
        Directory.CreateDirectory(currentUpdaterRoot);
        Directory.CreateDirectory(currentDistRoot);
        File.WriteAllText(Path.Combine(currentAppRoot, "nested", "z.txt"), "z");
        File.WriteAllText(Path.Combine(currentAppRoot, "A.txt"), "a");
        File.WriteAllText(Path.Combine(currentUpdaterRoot, "updater.exe"), "updater");
        string currentPackagePath = Path.Combine(currentDistRoot, "bemusicseeker-unofficial-fork-v1.2.3.zip");
        File.WriteAllText(currentPackagePath, "current-package");
        return new ManifestFixture(root, currentAppRoot, currentUpdaterRoot, currentPackagePath);
    }

    private static PowerShellResult RunPowerShell(string root, string command)
    {
        const int processTimeoutMilliseconds = 60_000;
        const int cleanupTimeoutMilliseconds = 5_000;
        const int streamTimeoutMilliseconds = 5_000;
        var startInfo = new ProcessStartInfo
        {
            FileName = "pwsh",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(command);
        startInfo.Environment["BMS_TEST_SCRIPT"] = Path.Combine(FindRepositoryRoot(), "scripts", "distribution-artifact.ps1");
        startInfo.Environment["BMS_TEST_ROOT"] = root;
        startInfo.Environment["BMS_TEST_MANIFEST"] = Path.Combine(root, "distribution", "distribution-manifest.json");

        using var process = new Process { StartInfo = startInfo };
        Assert.IsTrue(process.Start(), "The distribution artifact PowerShell process did not start.");
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
        Task<string> errorTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(processTimeoutMilliseconds))
        {
            string cleanup = StopProcessTree(process, cleanupTimeoutMilliseconds);
            Assert.Fail(
                $"The distribution artifact PowerShell process exceeded the {processTimeoutMilliseconds / 1000}-second timeout. " +
                $"Process cleanup: {cleanup}. stdout: {GetCompletedTaskValue(outputTask)} stderr: {GetCompletedTaskValue(errorTask)}");
            return default;
        }

        try
        {
            if (!Task.WaitAll(new Task[] { outputTask, errorTask }, streamTimeoutMilliseconds))
            {
                string cleanup = StopProcessTree(process, cleanupTimeoutMilliseconds);
                Assert.Fail(
                    $"The distribution artifact PowerShell output did not close within the {streamTimeoutMilliseconds / 1000}-second timeout. " +
                    $"Process cleanup: {cleanup}. stdout: {GetCompletedTaskValue(outputTask)} stderr: {GetCompletedTaskValue(errorTask)}");
                return default;
            }
        }
        catch (Exception exception) when (exception is not AssertFailedException)
        {
            string cleanup = StopProcessTree(process, cleanupTimeoutMilliseconds);
            Assert.Fail($"The distribution artifact PowerShell output failed: {exception}. Process cleanup: {cleanup}");
            return default;
        }

        string output = outputTask.GetAwaiter().GetResult();
        string error = errorTask.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
        {
            string cleanup = StopProcessTree(process, cleanupTimeoutMilliseconds);
            if (!string.IsNullOrWhiteSpace(cleanup))
            {
                error += Environment.NewLine + "Process cleanup: " + cleanup;
            }
        }
        return new PowerShellResult(process.ExitCode, output, error);
    }

    private static string StopProcessTree(Process process, int cleanupTimeoutMilliseconds)
    {
        var diagnostics = new List<string>();
        bool killFailed = false;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception)
        {
            killFailed = true;
            diagnostics.Add("managed tree termination failed: " + exception.Message);
        }

        try
        {
            if (!process.HasExited && !process.WaitForExit(cleanupTimeoutMilliseconds))
            {
                killFailed = true;
                diagnostics.Add($"PowerShell PID {process.Id} remained active after {cleanupTimeoutMilliseconds / 1000}s.");
            }
        }
        catch (Exception exception)
        {
            diagnostics.Add("process cleanup wait failed: " + exception.Message);
        }

        if (killFailed)
        {
            try
            {
                var taskkillInfo = new ProcessStartInfo
                {
                    FileName = "taskkill.exe",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                taskkillInfo.ArgumentList.Add("/PID");
                taskkillInfo.ArgumentList.Add(process.Id.ToString());
                taskkillInfo.ArgumentList.Add("/T");
                taskkillInfo.ArgumentList.Add("/F");
                using var taskkill = Process.Start(taskkillInfo);
                if (taskkill is null)
                {
                    diagnostics.Add("taskkill.exe did not start.");
                }
                else if (!taskkill.WaitForExit(cleanupTimeoutMilliseconds))
                {
                    diagnostics.Add("taskkill.exe did not finish within the cleanup timeout.");
                    try
                    {
                        taskkill.Kill(entireProcessTree: true);
                        if (!taskkill.WaitForExit(cleanupTimeoutMilliseconds))
                        {
                            diagnostics.Add("taskkill.exe remained active after forced cleanup.");
                        }
                    }
                    catch (Exception exception)
                    {
                        diagnostics.Add("taskkill.exe cleanup failed: " + exception.Message);
                    }
                }
                else if (taskkill.ExitCode != 0)
                {
                    diagnostics.Add($"taskkill.exe failed with exit code {taskkill.ExitCode}.");
                }
            }
            catch (Exception exception)
            {
                diagnostics.Add("taskkill fallback failed: " + exception.Message);
            }
        }

        return diagnostics.Count == 0 ? "completed" : string.Join("; ", diagnostics);
    }

    private static string GetCompletedTaskValue(Task<string> task)
    {
        return task.Status == TaskStatus.RanToCompletion ? task.Result : "<unavailable>";
    }

    private static void AssertPowerShellSuccess(PowerShellResult result)
    {
        Assert.AreEqual(0, result.ExitCode, result.Error + Environment.NewLine + result.Output);
    }

    private static void AssertPowerShellFailure(PowerShellResult result, string expectedMessage)
    {
        Assert.AreNotEqual(0, result.ExitCode, "PowerShell unexpectedly succeeded: " + result.Output);
        StringAssert.Contains(result.Error + result.Output, expectedMessage);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "BeMusicSeeker.sln")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private static string QuotePowerShellLiteral(string value)
    {
        return "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    }

    private sealed class ManifestFixture : IDisposable
    {
        public ManifestFixture(
            string root,
            string currentAppRoot,
            string currentUpdaterRoot,
            string currentPackagePath)
        {
            Root = root;
            CurrentAppRoot = currentAppRoot;
            CurrentUpdaterRoot = currentUpdaterRoot;
            CurrentPackagePath = currentPackagePath;
        }

        public string Root { get; }
        public string CurrentAppRoot { get; }
        public string CurrentUpdaterRoot { get; }
        public string CurrentPackagePath { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    private readonly record struct PowerShellResult(int ExitCode, string Output, string Error);
}
