using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
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
    public void Manifest_UsesExactPackageAndSealsCanonicalArtifact()
    {
        using ManifestFixture fixture = CreateFixture();
        PowerShellResult result = RunPowerShell(fixture.Root, $@"
. $env:BMS_TEST_SCRIPT
$manifest = New-DistributionArtifactManifest `
    -ArtifactRoot (Join-Path $env:BMS_TEST_ROOT 'distribution') `
    -RunId 'tests-full-run' `
    -ArtifactId 'artifact-001' `
    -CurrentAppRoot (Join-Path $env:BMS_TEST_ROOT 'distribution/current/app') `
    -CurrentUpdaterRoot (Join-Path $env:BMS_TEST_ROOT 'distribution/current/updater') `
    -CurrentPackagePath (Join-Path $env:BMS_TEST_ROOT 'distribution/current/dist/bemusicseeker-unofficial-fork-v1.2.3.zip') `
    -CurrentVersion '1.2.3' `
    -CurrentCommit 'current-commit' `
    -BaselinePackagePath (Join-Path $env:BMS_TEST_ROOT 'distribution/baseline/package/bemusicseeker-unofficial-fork-v0.9.0.zip') `
    -BaselineVersion '0.9.0' `
    -BaselineCommit 'baseline-commit'
Assert-DistributionArtifactManifest -ArtifactManifest $manifest | Out-Null
[ordered]@{{
    artifactId = $manifest.ArtifactId
    runId = $manifest.RunId
    manifestPath = $manifest.ManifestPath
    manifestSha256 = $manifest.ManifestSha256
    currentPackagePath = $manifest.Current.packagePath
    currentPackageSha256 = $manifest.Current.packageSha256
    currentTreeSha256 = $manifest.Current.appTreeSha256
}} | ConvertTo-Json -Compress
");

        AssertPowerShellSuccess(result);
        using JsonDocument document = JsonDocument.Parse(result.Output.Trim());
        Assert.AreEqual("artifact-001", document.RootElement.GetProperty("artifactId").GetString());
        Assert.AreEqual("tests-full-run", document.RootElement.GetProperty("runId").GetString());
        Assert.AreEqual(
            fixture.CurrentPackagePath,
            document.RootElement.GetProperty("currentPackagePath").GetString());
        Assert.IsTrue(File.Exists(fixture.ManifestPath));
        Assert.IsTrue(File.Exists(fixture.ManifestHashPath));
        Assert.AreEqual(
            document.RootElement.GetProperty("manifestSha256").GetString(),
            File.ReadAllText(fixture.ManifestHashPath).Trim());
        Assert.AreEqual(
            "bemusicseeker-unofficial-fork-v1.2.3.zip",
            Path.GetFileName(document.RootElement.GetProperty("currentPackagePath").GetString()));
        Assert.AreNotEqual(
            fixture.DecoyPackagePath,
            document.RootElement.GetProperty("currentPackagePath").GetString());
        using JsonDocument manifestDocument = JsonDocument.Parse(File.ReadAllText(fixture.ManifestPath));
        CollectionAssert.AreEqual(
            new[]
            {
                "schemaVersion",
                "manifestType",
                "runId",
                "artifactId",
                "artifactRoot",
                "manifestPath",
                "manifestHashPath",
                "generatedUtc",
                "current",
                "baseline"
            },
            manifestDocument.RootElement.EnumerateObject().Select(property => property.Name).ToArray());
    }

    [TestMethod]
    public void ManifestTreeHash_UsesOrdinalNormalizedRelativePathsAndDetectsMutation()
    {
        using ManifestFixture fixture = CreateFixture();
        PowerShellResult result = RunPowerShell(fixture.Root, $@"
. $env:BMS_TEST_SCRIPT
$manifest = New-DistributionArtifactManifest `
    -ArtifactRoot (Join-Path $env:BMS_TEST_ROOT 'distribution') `
    -RunId 'run-hash' `
    -ArtifactId 'artifact-hash' `
    -CurrentAppRoot (Join-Path $env:BMS_TEST_ROOT 'distribution/current/app') `
    -CurrentUpdaterRoot (Join-Path $env:BMS_TEST_ROOT 'distribution/current/updater') `
    -CurrentPackagePath (Join-Path $env:BMS_TEST_ROOT 'distribution/current/dist/bemusicseeker-unofficial-fork-v1.2.3.zip') `
    -CurrentVersion '1.2.3' `
    -CurrentCommit 'current-commit' `
    -BaselinePackagePath (Join-Path $env:BMS_TEST_ROOT 'distribution/baseline/package/bemusicseeker-unofficial-fork-v0.9.0.zip') `
    -BaselineVersion '0.9.0' `
    -BaselineCommit 'baseline-commit'
[ordered]@{{
    appTree = $manifest.Current.appTreeSha256
    updaterTree = $manifest.Current.updaterTreeSha256
    package = $manifest.Current.packageSha256
}} | ConvertTo-Json -Compress
");

        AssertPowerShellSuccess(result);
        using JsonDocument document = JsonDocument.Parse(result.Output.Trim());
        Assert.AreEqual(
            ComputeTreeHash(fixture.CurrentAppRoot),
            document.RootElement.GetProperty("appTree").GetString());
        Assert.AreEqual(
            ComputeTreeHash(fixture.CurrentUpdaterRoot),
            document.RootElement.GetProperty("updaterTree").GetString());
        Assert.AreEqual(
            ComputeFileHash(fixture.CurrentPackagePath),
            document.RootElement.GetProperty("package").GetString());

        File.AppendAllText(Path.Combine(fixture.CurrentAppRoot, "nested", "z.txt"), "-mutated");
        result = RunPowerShell(fixture.Root, $@"
. $env:BMS_TEST_SCRIPT
$manifest = Read-DistributionArtifactManifest -ManifestPath $env:BMS_TEST_MANIFEST
Assert-DistributionArtifactManifest -ArtifactManifest $manifest | Out-Null
");
        AssertPowerShellFailure(result, "Current app artifact is missing or tampered");
    }

    [TestMethod]
    public void ManifestFailuresAreExplicitForMissingTamperedPathIdentityAndVersion()
    {
        using ManifestFixture fixture = CreateFixture();
        PowerShellResult result = CreateManifest(fixture, "artifact-valid", "run-valid");
        AssertPowerShellSuccess(result);

        result = RunPowerShell(fixture.Root, $@"
. $env:BMS_TEST_SCRIPT
$manifest = Read-DistributionArtifactManifest -ManifestPath $env:BMS_TEST_MANIFEST
Assert-DistributionArtifactManifest -ArtifactManifest $manifest -ExpectedArtifactId 'artifact-wrong' | Out-Null
");
        AssertPowerShellFailure(result, "Distribution artifact ID mismatch");

        File.Delete(fixture.BaselinePackagePath);
        result = RunPowerShell(fixture.Root, $@"
. $env:BMS_TEST_SCRIPT
$manifest = Read-DistributionArtifactManifest -ManifestPath $env:BMS_TEST_MANIFEST
Assert-DistributionArtifactManifest -ArtifactManifest $manifest | Out-Null
");
        AssertPowerShellFailure(result, "Baseline package is missing");

        result = RunPowerShell(fixture.Root, $@"
. $env:BMS_TEST_SCRIPT
New-DistributionArtifactManifest `
    -ArtifactRoot (Join-Path $env:BMS_TEST_ROOT 'distribution') `
    -RunId 'run-path' `
    -ArtifactId 'artifact-path' `
    -CurrentAppRoot (Join-Path $env:BMS_TEST_ROOT 'outside/app') `
    -CurrentUpdaterRoot (Join-Path $env:BMS_TEST_ROOT 'distribution/current/updater') `
    -CurrentPackagePath (Join-Path $env:BMS_TEST_ROOT 'distribution/current/dist/bemusicseeker-unofficial-fork-v1.2.3.zip') `
    -CurrentVersion '1.2.3' `
    -CurrentCommit 'current-commit' `
    -BaselinePackagePath (Join-Path $env:BMS_TEST_ROOT 'distribution/baseline/package/bemusicseeker-unofficial-fork-v0.9.0.zip') `
    -BaselineVersion '0.9.0' `
    -BaselineCommit 'baseline-commit' | Out-Null
");
        AssertPowerShellFailure(result, "must remain under the distribution artifact root");

        File.WriteAllText(fixture.BaselinePackagePath, "restored");
        result = RunPowerShell(fixture.Root, $@"
. $env:BMS_TEST_SCRIPT
New-DistributionArtifactManifest `
    -ArtifactRoot (Join-Path $env:BMS_TEST_ROOT 'distribution') `
    -RunId 'run-version' `
    -ArtifactId 'artifact-version' `
    -CurrentAppRoot (Join-Path $env:BMS_TEST_ROOT 'distribution/current/app') `
    -CurrentUpdaterRoot (Join-Path $env:BMS_TEST_ROOT 'distribution/current/updater') `
    -CurrentPackagePath (Join-Path $env:BMS_TEST_ROOT 'distribution/current/dist/bemusicseeker-unofficial-fork-v1.2.3.zip') `
    -CurrentVersion '9.9.9' `
    -CurrentCommit 'current-commit' `
    -BaselinePackagePath (Join-Path $env:BMS_TEST_ROOT 'distribution/baseline/package/bemusicseeker-unofficial-fork-v0.9.0.zip') `
    -BaselineVersion '0.9.0' `
    -BaselineCommit 'baseline-commit' | Out-Null
");
        AssertPowerShellFailure(result, "package name does not match version");
    }

    [TestMethod]
    public void MissingManifestDoesNotFallBackToAnotherArtifactRoot()
    {
        using ManifestFixture fixture = CreateFixture();
        PowerShellResult result = RunPowerShell(fixture.Root, $@"
. $env:BMS_TEST_SCRIPT
Read-DistributionArtifactManifest -ManifestPath (Join-Path $env:BMS_TEST_ROOT 'missing/distribution-manifest.json') | Out-Null
");

        AssertPowerShellFailure(result, "Distribution manifest is missing");
        Assert.IsFalse(File.Exists(fixture.ManifestPath));
    }

    [TestMethod]
    public void ManifestIdentityRejectsSelfConsistentResealAfterCreation()
    {
        using ManifestFixture fixture = CreateFixture();
        PowerShellResult result = CreateManifest(fixture, "artifact-identity", "run-identity");
        AssertPowerShellSuccess(result);

        result = RunPowerShell(fixture.Root, $@"
. $env:BMS_TEST_SCRIPT
$created = Read-DistributionArtifactManifest -ManifestPath $env:BMS_TEST_MANIFEST
Assert-DistributionArtifactIdentity `
    -ArtifactManifest $created `
    -ExpectedRunId 'run-identity' `
    -ExpectedArtifactId 'artifact-identity' `
    -ExpectedManifestSha256 $created.ManifestSha256 `
    -ExpectedManifestSeal $created.ManifestSha256 | Out-Null
$replacement = Get-Content -LiteralPath $env:BMS_TEST_MANIFEST -Raw | ConvertFrom-Json
$replacement.generatedUtc = '2000-01-01T00:00:00.0000000Z'
[IO.File]::WriteAllText(
    $env:BMS_TEST_MANIFEST,
    ($replacement | ConvertTo-Json -Depth 16),
    [Text.UTF8Encoding]::new($false))
$replacementHash = (Get-FileHash -LiteralPath $env:BMS_TEST_MANIFEST -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText(
    $created.ManifestHashPath,
    $replacementHash + [Environment]::NewLine,
    [Text.UTF8Encoding]::new($false))
$resealed = Read-DistributionArtifactManifest -ManifestPath $env:BMS_TEST_MANIFEST
Assert-DistributionArtifactIdentity `
    -ArtifactManifest $resealed `
    -ExpectedRunId 'run-identity' `
    -ExpectedArtifactId 'artifact-identity' `
    -ExpectedManifestSha256 $created.ManifestSha256 `
    -ExpectedManifestSeal $created.ManifestSha256 | Out-Null
");

        AssertPowerShellFailure(result, "Distribution manifest SHA-256 mismatch");
    }

    private static PowerShellResult CreateManifest(ManifestFixture fixture, string artifactId, string runId)
    {
        return RunPowerShell(fixture.Root, $@"
. $env:BMS_TEST_SCRIPT
$manifest = New-DistributionArtifactManifest `
    -ArtifactRoot (Join-Path $env:BMS_TEST_ROOT 'distribution') `
    -RunId '{runId}' `
    -ArtifactId '{artifactId}' `
    -CurrentAppRoot (Join-Path $env:BMS_TEST_ROOT 'distribution/current/app') `
    -CurrentUpdaterRoot (Join-Path $env:BMS_TEST_ROOT 'distribution/current/updater') `
    -CurrentPackagePath (Join-Path $env:BMS_TEST_ROOT 'distribution/current/dist/bemusicseeker-unofficial-fork-v1.2.3.zip') `
    -CurrentVersion '1.2.3' `
    -CurrentCommit 'current-commit' `
    -BaselinePackagePath (Join-Path $env:BMS_TEST_ROOT 'distribution/baseline/package/bemusicseeker-unofficial-fork-v0.9.0.zip') `
    -BaselineVersion '0.9.0' `
    -BaselineCommit 'baseline-commit'
$manifest.ManifestPath
");
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
        string baselinePackageRoot = Path.Combine(distributionRoot, "baseline", "package");
        Directory.CreateDirectory(Path.Combine(currentAppRoot, "nested"));
        Directory.CreateDirectory(currentUpdaterRoot);
        Directory.CreateDirectory(currentDistRoot);
        Directory.CreateDirectory(baselinePackageRoot);
        File.WriteAllText(Path.Combine(currentAppRoot, "nested", "z.txt"), "z");
        File.WriteAllText(Path.Combine(currentAppRoot, "A.txt"), "a");
        File.WriteAllText(Path.Combine(currentUpdaterRoot, "updater.exe"), "updater");
        string currentPackagePath = Path.Combine(currentDistRoot, "bemusicseeker-unofficial-fork-v1.2.3.zip");
        string decoyPackagePath = Path.Combine(currentDistRoot, "bemusicseeker-unofficial-fork-v9.9.9.zip");
        string baselinePackagePath = Path.Combine(baselinePackageRoot, "bemusicseeker-unofficial-fork-v0.9.0.zip");
        File.WriteAllText(currentPackagePath, "current-package");
        File.WriteAllText(decoyPackagePath, "newer-decoy-package");
        File.WriteAllText(baselinePackagePath, "baseline-package");
        return new ManifestFixture(root, currentAppRoot, currentUpdaterRoot, currentPackagePath, decoyPackagePath, baselinePackagePath);
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
                using Process? taskkill = Process.Start(taskkillInfo);
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

    private static string ComputeTreeHash(string root)
    {
        string[] entries = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => new
            {
                RelativePath = Path.GetRelativePath(root, path).Replace('\\', '/'),
                Hash = ComputeFileHash(path)
            })
            .OrderBy(entry => entry.RelativePath, StringComparer.Ordinal)
            .Select(entry => entry.RelativePath + "\t" + entry.Hash)
            .ToArray();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", entries)))).ToLowerInvariant();
    }

    private static string ComputeFileHash(string path)
    {
        return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
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

    private sealed class ManifestFixture : IDisposable
    {
        public ManifestFixture(
            string root,
            string currentAppRoot,
            string currentUpdaterRoot,
            string currentPackagePath,
            string decoyPackagePath,
            string baselinePackagePath)
        {
            Root = root;
            CurrentAppRoot = currentAppRoot;
            CurrentUpdaterRoot = currentUpdaterRoot;
            CurrentPackagePath = currentPackagePath;
            DecoyPackagePath = decoyPackagePath;
            BaselinePackagePath = baselinePackagePath;
        }

        public string Root { get; }
        public string CurrentAppRoot { get; }
        public string CurrentUpdaterRoot { get; }
        public string CurrentPackagePath { get; }
        public string DecoyPackagePath { get; }
        public string BaselinePackagePath { get; }
        public string ManifestPath => Path.Combine(Root, "distribution", "distribution-manifest.json");
        public string ManifestHashPath => Path.Combine(Root, "distribution", "distribution-manifest.sha256");

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
