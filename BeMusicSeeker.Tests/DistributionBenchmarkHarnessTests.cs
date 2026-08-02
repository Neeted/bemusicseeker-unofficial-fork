using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
[TestCategory("ProcessIntegration")]
public sealed class DistributionBenchmarkHarnessTests
{
    [TestMethod]
    public void HarnessDescribesTheFiniteOfficialCandidateMatrix()
    {
        string repositoryRoot = FindRepositoryRoot();
        string scriptPath = Path.Combine(repositoryRoot, "scripts", "benchmark-net10-distribution.ps1");
        Assert.IsTrue(File.Exists(scriptPath));

        var startInfo = new ProcessStartInfo
        {
            FileName = "pwsh",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add("-DescribeCandidates");

        using Process process = Process.Start(startInfo)
            ?? throw new AssertFailedException("pwsh could not start the distribution benchmark descriptor.");
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.AreEqual(0, process.ExitCode, output + Environment.NewLine + error);

        using JsonDocument document = JsonDocument.Parse(output);
        JsonElement root = document.RootElement;
        JsonElement candidates = root.GetProperty("candidates");
        Assert.AreEqual(6, candidates.GetArrayLength());
        CollectionAssert.AreEqual(
            new[]
            {
                "folder-il",
                "folder-r2r",
                "bundle-il",
                "bundle-r2r",
                "extract-il",
                "extract-r2r"
            },
            Array.ConvertAll(candidates.EnumerateArray().ToArray(), item => item.GetProperty("name").GetString()));
        Assert.AreEqual(1, root.GetProperty("warmupRuns").GetInt32());
        Assert.AreEqual(3, root.GetProperty("warmCacheMeasuredRuns").GetInt32());
        Assert.AreEqual(3, root.GetProperty("freshInstallMeasuredRuns").GetInt32());
        Assert.AreEqual(2, root.GetProperty("additionalRunsPerAmbiguousPhase").GetInt32());
        Assert.AreEqual("bundle-r2r", root.GetProperty("equivalentDefault").GetString());
        JsonElement smoke = root.GetProperty("smoke");
        Assert.AreEqual("extract-r2r", smoke.GetProperty("candidate").GetString());
        Assert.AreEqual(1, smoke.GetProperty("freshRuns").GetInt32());
        Assert.AreEqual(1, smoke.GetProperty("warmRuns").GetInt32());
    }

    [TestMethod]
    public void ApplicationOwnedNativeAssetsArePublishedForEveryOfficialCandidate()
    {
        string repositoryRoot = FindRepositoryRoot();
        XDocument project = XDocument.Load(Path.Combine(repositoryRoot, "BeMusicSeeker.csproj"));
        XElement target = project.Root!
            .Elements("Target")
            .Single(element => (string?)element.Attribute("Name") == "CopyPublishedNativeAssets");

        Assert.IsNull(target.Attribute("Condition"));
        string targetText = target.ToString(SaveOptions.DisableFormatting);
        StringAssert.Contains(targetText, @"$(PublishDir)native");
        StringAssert.Contains(targetText, @"$(PublishDir)libs\x64");
    }

    [TestMethod]
    public void AdditionalMeasurementIsLimitedToTopCandidatesNearThePracticalBoundary()
    {
        string repositoryRoot = FindRepositoryRoot();
        string scriptPath = Path.Combine(repositoryRoot, "scripts", "benchmark-net10-distribution.ps1")
            .Replace("'", "''", StringComparison.Ordinal);
        string command = $$"""
            . '{{scriptPath}}' -ImportFunctionsOnly
            $summaries = @(
                [ordered]@{
                    name = 'slow'
                    viable = $true
                    selectionMetricMs = 6600.0
                    phases = [ordered]@{
                        'warm-cache' = [ordered]@{ medianStartupReadyOperableMs = 5480.0 }
                        'fresh-install' = [ordered]@{ medianStartupReadyOperableMs = 6600.0 }
                    }
                }
                [ordered]@{
                    name = 'fast'
                    viable = $true
                    selectionMetricMs = 6000.0
                    phases = [ordered]@{
                        'warm-cache' = [ordered]@{ medianStartupReadyOperableMs = 5000.0 }
                        'fresh-install' = [ordered]@{ medianStartupReadyOperableMs = 6000.0 }
                    }
                }
            )
            Get-AdditionalMeasurementDecision -Summaries $summaries | ConvertTo-Json -Compress
            """;

        var startInfo = new ProcessStartInfo
        {
            FileName = "pwsh",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(command);

        using Process process = Process.Start(startInfo)
            ?? throw new AssertFailedException("pwsh could not start the distribution benchmark selection check.");
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.AreEqual(0, process.ExitCode, output + Environment.NewLine + error);

        using JsonDocument document = JsonDocument.Parse(output);
        JsonElement root = document.RootElement;
        Assert.IsTrue(root.GetProperty("required").GetBoolean());
        CollectionAssert.AreEqual(
            new[] { "fast", "slow" },
            Array.ConvertAll(
                root.GetProperty("candidates").EnumerateArray().ToArray(),
                item => item.GetString()));
        CollectionAssert.AreEqual(
            new[] { "warm-cache", "fresh-install" },
            Array.ConvertAll(
                root.GetProperty("phases").EnumerateArray().ToArray(),
                item => item.GetString()));
    }

    [TestMethod]
    public void WarmupFailureInvalidatesBenchmarkRunSet()
    {
        string scriptPath = Path.Combine(
                FindRepositoryRoot(),
                "scripts",
                "benchmark-net10-distribution.ps1")
            .Replace("'", "''", StringComparison.Ordinal);
        string command = $$"""
            . '{{scriptPath}}' -ImportFunctionsOnly
            $failureMessage = $null
            try {
                Assert-BenchmarkRunsSucceeded -Runs @(
                    [ordered]@{
                        candidate = 'folder-il'
                        phase = 'warmup'
                        round = 1
                        failure = 'startup failed'
                        semanticState = $null
                    }
                )
            }
            catch {
                $failureMessage = $_.Exception.Message
            }
            if ($failureMessage -notmatch 'candidate=folder-il phase=warmup round=1') {
                throw "Warmup failure was not rejected with run identity: $failureMessage"
            }
            Write-Output 'passed'
            """;

        ProcessResult result = RunProcess("pwsh", "-NoProfile", "-NonInteractive", "-Command", command);

        Assert.AreEqual(0, result.ExitCode, result.Output + Environment.NewLine + result.Error);
        StringAssert.Contains(result.Output, "passed");
    }

    [TestMethod]
    public void RecommendationUsesGeneralLayoutCharacteristicsWhenPerformanceIsEquivalent()
    {
        string scriptPath = Path.Combine(
                FindRepositoryRoot(),
                "scripts",
                "benchmark-net10-distribution.ps1")
            .Replace("'", "''", StringComparison.Ordinal);
        string command = $$"""
            . '{{scriptPath}}' -ImportFunctionsOnly
            function New-Summary(
                [string]$Name,
                [double]$Warm,
                [double]$Fresh,
                [double]$WorkingSet,
                [bool]$Extract,
                [string]$Family,
                [bool]$ReadyToRun) {
                return [ordered]@{
                    name = $Name
                    viable = $true
                    selectionMetricMs = [Math]::Max($Warm, $Fresh)
                    workingSetMetricBytes = $WorkingSet
                    nativeSelfExtract = $Extract
                    family = $Family
                    readyToRun = $ReadyToRun
                    phases = [ordered]@{
                        'warm-cache' = [ordered]@{ medianStartupReadyOperableMs = $Warm }
                        'fresh-install' = [ordered]@{ medianStartupReadyOperableMs = $Fresh }
                    }
                }
            }
            $decision = [ordered]@{ required = $false; candidates = @(); phases = @() }
            $recommendation = Get-Recommendation -AdditionalDecision $decision -Summaries @(
                (New-Summary 'extract-r2r' 3290 5450 205MB $true 'extract' $true)
                (New-Summary 'folder-r2r' 3300 4650 200MB $false 'folder' $true)
                (New-Summary 'bundle-il' 4000 4960 201MB $false 'bundle' $false)
                (New-Summary 'bundle-r2r' 3280 4665 207MB $false 'bundle' $true)
            )
            $recommendation | ConvertTo-Json -Compress
            """;

        ProcessResult result = RunProcess("pwsh", "-NoProfile", "-NonInteractive", "-Command", command);

        Assert.AreEqual(0, result.ExitCode, result.Output + Environment.NewLine + result.Error);
        using JsonDocument document = JsonDocument.Parse(result.Output);
        Assert.AreEqual("bundle-r2r", document.RootElement.GetProperty("selected").GetString());
        Assert.AreEqual(
            "practical-performance-equivalent-general-layout-preference",
            document.RootElement.GetProperty("reason").GetString());
    }

    [TestMethod]
    public void RecommendationKeepsAClearPracticalWinner()
    {
        string scriptPath = Path.Combine(
                FindRepositoryRoot(),
                "scripts",
                "benchmark-net10-distribution.ps1")
            .Replace("'", "''", StringComparison.Ordinal);
        string command = $$"""
            . '{{scriptPath}}' -ImportFunctionsOnly
            $decision = [ordered]@{ required = $false; candidates = @(); phases = @() }
            $recommendation = Get-Recommendation -AdditionalDecision $decision -Summaries @(
                [ordered]@{
                    name = 'bundle-r2r'; viable = $true; selectionMetricMs = 5000.0
                    workingSetMetricBytes = 205MB; nativeSelfExtract = $false
                    family = 'bundle'; readyToRun = $true
                    phases = [ordered]@{
                        'warm-cache' = [ordered]@{ medianStartupReadyOperableMs = 4000.0 }
                        'fresh-install' = [ordered]@{ medianStartupReadyOperableMs = 5000.0 }
                    }
                }
                [ordered]@{
                    name = 'folder-il'; viable = $true; selectionMetricMs = 5700.0
                    workingSetMetricBytes = 190MB; nativeSelfExtract = $false
                    family = 'folder'; readyToRun = $false
                    phases = [ordered]@{
                        'warm-cache' = [ordered]@{ medianStartupReadyOperableMs = 4800.0 }
                        'fresh-install' = [ordered]@{ medianStartupReadyOperableMs = 5700.0 }
                    }
                }
            )
            $recommendation | ConvertTo-Json -Compress
            """;

        ProcessResult result = RunProcess("pwsh", "-NoProfile", "-NonInteractive", "-Command", command);

        Assert.AreEqual(0, result.ExitCode, result.Output + Environment.NewLine + result.Error);
        using JsonDocument document = JsonDocument.Parse(result.Output);
        Assert.AreEqual("bundle-r2r", document.RootElement.GetProperty("selected").GetString());
        Assert.AreEqual(
            "clear-balanced-practical-advantage",
            document.RootElement.GetProperty("reason").GetString());
    }

    [TestMethod]
    public void RecommendationDoesNotTradeClearWarmRegressionForFewerFiles()
    {
        string scriptPath = Path.Combine(
                FindRepositoryRoot(),
                "scripts",
                "benchmark-net10-distribution.ps1")
            .Replace("'", "''", StringComparison.Ordinal);
        string command = $$"""
            . '{{scriptPath}}' -ImportFunctionsOnly
            $decision = [ordered]@{ required = $false; candidates = @(); phases = @() }
            $recommendation = Get-Recommendation -AdditionalDecision $decision -Summaries @(
                [ordered]@{
                    name = 'folder-r2r'; viable = $true; selectionMetricMs = 5000.0
                    workingSetMetricBytes = 200MB; nativeSelfExtract = $false
                    family = 'folder'; readyToRun = $true
                    phases = [ordered]@{
                        'warm-cache' = [ordered]@{ medianStartupReadyOperableMs = 3000.0 }
                        'fresh-install' = [ordered]@{ medianStartupReadyOperableMs = 5000.0 }
                    }
                }
                [ordered]@{
                    name = 'bundle-r2r'; viable = $true; selectionMetricMs = 5050.0
                    workingSetMetricBytes = 200MB; nativeSelfExtract = $false
                    family = 'bundle'; readyToRun = $true
                    phases = [ordered]@{
                        'warm-cache' = [ordered]@{ medianStartupReadyOperableMs = 4500.0 }
                        'fresh-install' = [ordered]@{ medianStartupReadyOperableMs = 5050.0 }
                    }
                }
            )
            $recommendation | ConvertTo-Json -Compress
            """;

        ProcessResult result = RunProcess("pwsh", "-NoProfile", "-NonInteractive", "-Command", command);

        Assert.AreEqual(0, result.ExitCode, result.Output + Environment.NewLine + result.Error);
        using JsonDocument document = JsonDocument.Parse(result.Output);
        Assert.AreEqual("folder-r2r", document.RootElement.GetProperty("selected").GetString());
        Assert.AreEqual(
            "clear-balanced-practical-advantage",
            document.RootElement.GetProperty("reason").GetString());
    }

    [TestMethod]
    public void RecommendationExposesInversePhaseTradeoffWithoutApplyingLayoutPreference()
    {
        string scriptPath = Path.Combine(
                FindRepositoryRoot(),
                "scripts",
                "benchmark-net10-distribution.ps1")
            .Replace("'", "''", StringComparison.Ordinal);
        string command = $$"""
            . '{{scriptPath}}' -ImportFunctionsOnly
            $decision = [ordered]@{ required = $false; candidates = @(); phases = @() }
            $recommendation = Get-Recommendation -AdditionalDecision $decision -Summaries @(
                [ordered]@{
                    name = 'folder-r2r'; viable = $true; selectionMetricMs = 6000.0
                    workingSetMetricBytes = 200MB; nativeSelfExtract = $false
                    family = 'folder'; readyToRun = $true
                    phases = [ordered]@{
                        'warm-cache' = [ordered]@{ medianStartupReadyOperableMs = 3000.0 }
                        'fresh-install' = [ordered]@{ medianStartupReadyOperableMs = 6000.0 }
                    }
                }
                [ordered]@{
                    name = 'bundle-r2r'; viable = $true; selectionMetricMs = 5000.0
                    workingSetMetricBytes = 200MB; nativeSelfExtract = $false
                    family = 'bundle'; readyToRun = $true
                    phases = [ordered]@{
                        'warm-cache' = [ordered]@{ medianStartupReadyOperableMs = 4500.0 }
                        'fresh-install' = [ordered]@{ medianStartupReadyOperableMs = 5000.0 }
                    }
                }
            )
            $recommendation | ConvertTo-Json -Compress
            """;

        ProcessResult result = RunProcess("pwsh", "-NoProfile", "-NonInteractive", "-Command", command);

        Assert.AreEqual(0, result.ExitCode, result.Output + Environment.NewLine + result.Error);
        using JsonDocument document = JsonDocument.Parse(result.Output);
        Assert.AreEqual(JsonValueKind.Null, document.RootElement.GetProperty("selected").ValueKind);
        Assert.AreEqual(
            "fresh-warm-practical-tradeoff-requires-product-decision",
            document.RootElement.GetProperty("reason").GetString());
        CollectionAssert.AreEquivalent(
            new[] { "folder-r2r", "bundle-r2r" },
            Array.ConvertAll(
                document.RootElement.GetProperty("practicalTier").EnumerateArray().ToArray(),
                item => item.GetString()));
    }

    [TestMethod]
    public void BenchmarkOutputRootMustBeADedicatedPerformanceArtifactChild()
    {
        string repositoryRoot = FindRepositoryRoot();
        string scriptPath = Path.Combine(
                repositoryRoot,
                "scripts",
                "benchmark-net10-distribution.ps1")
            .Replace("'", "''", StringComparison.Ordinal);
        string escapedRepositoryRoot = repositoryRoot.Replace("'", "''", StringComparison.Ordinal);
        string command = $$"""
            . '{{scriptPath}}' -ImportFunctionsOnly
            $failureMessage = $null
            try {
                Assert-BenchmarkOutputRootIsSafe -Path '{{escapedRepositoryRoot}}' -RepositoryRoot '{{escapedRepositoryRoot}}'
            }
            catch {
                $failureMessage = $_.Exception.Message
            }
            if ($failureMessage -notmatch 'dedicated child') {
                throw "Unsafe root was not rejected: $failureMessage"
            }
            Assert-BenchmarkOutputRootIsSafe `
                -Path '{{escapedRepositoryRoot}}\artifacts\performance\unit-test-output' `
                -RepositoryRoot '{{escapedRepositoryRoot}}'
            Write-Output 'passed'
            """;

        ProcessResult result = RunProcess("pwsh", "-NoProfile", "-NonInteractive", "-Command", command);

        Assert.AreEqual(0, result.ExitCode, result.Output + Environment.NewLine + result.Error);
        StringAssert.Contains(result.Output, "passed");
    }

    [TestMethod]
    public void MainWindowReadyUsesTheLaterHandleOrInputIdleObservation()
    {
        string scriptPath = Path.Combine(
                FindRepositoryRoot(),
                "scripts",
                "benchmark-net10-distribution.ps1")
            .Replace("'", "''", StringComparison.Ordinal);
        string command = $$"""
            . '{{scriptPath}}' -ImportFunctionsOnly
            [ordered]@{
                handleLater = Get-MainWindowReadyMilliseconds `
                    -MainWindowHandleMilliseconds 2631.693 `
                    -InputIdleMilliseconds 2600.642
                idleLater = Get-MainWindowReadyMilliseconds `
                    -MainWindowHandleMilliseconds 2500.0 `
                    -InputIdleMilliseconds 2700.0
                handleOnly = Get-MainWindowReadyMilliseconds `
                    -MainWindowHandleMilliseconds 2500.0 `
                    -InputIdleMilliseconds $null
            } | ConvertTo-Json -Compress
            """;

        ProcessResult result = RunProcess("pwsh", "-NoProfile", "-NonInteractive", "-Command", command);

        Assert.AreEqual(0, result.ExitCode, result.Output + Environment.NewLine + result.Error);
        using JsonDocument document = JsonDocument.Parse(result.Output);
        Assert.AreEqual(2631.693, document.RootElement.GetProperty("handleLater").GetDouble(), 0.001);
        Assert.AreEqual(2700.0, document.RootElement.GetProperty("idleLater").GetDouble(), 0.001);
        Assert.AreEqual(2500.0, document.RootElement.GetProperty("handleOnly").GetDouble(), 0.001);
    }

    private static ProcessResult RunProcess(string fileName, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo)
            ?? throw new AssertFailedException($"Unable to start {fileName}.");
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new ProcessResult(process.ExitCode, output.Trim(), error.Trim());
    }

    private static string FindRepositoryRoot()
    {
        string current = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(current))
        {
            if (File.Exists(Path.Combine(current, "BeMusicSeeker.sln")))
            {
                return current;
            }
            current = Directory.GetParent(current)?.FullName ?? string.Empty;
        }
        throw new AssertFailedException("Repository root was not found.");
    }

    private sealed record ProcessResult(int ExitCode, string Output, string Error);
}
