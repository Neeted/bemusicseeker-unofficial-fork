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
public sealed class VerificationRunnerContractTests
{
    private const string V216HappyPathResultName =
        "BeMusicSeeker.ReleaseAcceptance.V216FirstHopAcceptance.HappyPath";
    private const string V216ManagedFileLockResultName =
        "BeMusicSeeker.ReleaseAcceptance.V216FirstHopAcceptance.ManagedFileLockCharacterization";

    [TestMethod]
    public void V216FirstHopReceiptGateRequiresBothExactPassedResultsAndReceiptInput()
    {
        const string happyContractId = "UPD-V216-HAPPY";
        const string lockContractId = "UPD-V216-LOCK";
        string root = Path.Combine(
            Path.GetTempPath(),
            "BeMusicSeeker-V216FirstHopReceiptContractTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string rosterPath = Path.Combine(root, "roster.json");
            WriteSyntheticRoster(
                rosterPath,
                (happyContractId, V216HappyPathResultName),
                (lockContractId, V216ManagedFileLockResultName));

            string passedReceiptPath = Path.Combine(root, "passed.json");
            WriteSyntheticJson(
                passedReceiptPath,
                (V216HappyPathResultName, "Passed"),
                (V216ManagedFileLockResultName, "Passed"));
            using JsonDocument passed = ReadOutcomeRosterGateProbe(passedReceiptPath, rosterPath);
            Assert.IsTrue(GetProperty(passed.RootElement, "Passed").GetBoolean(), passed.RootElement.GetRawText());

            string missingResultPath = Path.Combine(root, "missing-result.json");
            WriteSyntheticJson(missingResultPath, (V216HappyPathResultName, "Passed"));
            using JsonDocument missingResult = ReadOutcomeRosterGateProbe(missingResultPath, rosterPath);
            AssertGateFailure(missingResult, V216ManagedFileLockResultName);

            string duplicateResultPath = Path.Combine(root, "duplicate-result.json");
            WriteSyntheticJson(
                duplicateResultPath,
                (V216HappyPathResultName, "Passed"),
                (V216HappyPathResultName, "Passed"),
                (V216ManagedFileLockResultName, "Passed"));
            using JsonDocument duplicateResult = ReadOutcomeRosterGateProbe(duplicateResultPath, rosterPath);
            AssertGateFailure(duplicateResult, "duplicate");

            string nonPassedResultPath = Path.Combine(root, "non-passed-result.json");
            WriteSyntheticJson(
                nonPassedResultPath,
                (V216HappyPathResultName, "Passed"),
                (V216ManagedFileLockResultName, "Failed"));
            using JsonDocument nonPassedResult = ReadOutcomeRosterGateProbe(nonPassedResultPath, rosterPath);
            AssertGateFailure(nonPassedResult, "not Passed");

            string detailedOnlyReceiptPath = Path.Combine(root, "detailed-only.json");
            File.WriteAllText(
                detailedOnlyReceiptPath,
                "{\"status\":\"passed\",\"happy\":{},\"locked\":{}}",
                Encoding.UTF8);
            using JsonDocument detailedOnly = ReadOutcomeRosterGateProbe(detailedOnlyReceiptPath, rosterPath);
            AssertGateFailure(detailedOnly, "fullyQualifiedName and outcome");

            string missingReceiptPath = Path.Combine(root, "missing-receipt.json");
            using JsonDocument missingReceipt = ReadOutcomeRosterGateProbe(missingReceiptPath, rosterPath);
            AssertGateFailure(missingReceipt, "missing");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public void ReleaseOutcomeGateRequiresExactPassedCardinality()
    {
        const string requiredFqn = "BeMusicSeeker.Tests.Synthetic.Required.ReleaseOutcome";
        string root = Path.Combine(
            Path.GetTempPath(),
            "BeMusicSeeker-VerificationOutcomeContractTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string emptyResults = Path.Combine(root, "empty.trx");
            WriteSyntheticTrx(emptyResults);
            using JsonDocument missing = ReadOutcomeGateProbe(emptyResults, requiredFqn);
            AssertGateFailure(missing, "missing");

            string duplicateResults = Path.Combine(root, "duplicate.trx");
            WriteSyntheticTrx(duplicateResults, (requiredFqn, "Passed"), (requiredFqn, "Passed"));
            using JsonDocument duplicate = ReadOutcomeGateProbe(duplicateResults, requiredFqn);
            AssertGateFailure(duplicate, "duplicate");

            string caseVariantResults = Path.Combine(root, "case-variant.trx");
            WriteSyntheticTrx(caseVariantResults, (requiredFqn.ToLowerInvariant(), "Passed"));
            using JsonDocument caseVariant = ReadOutcomeGateProbe(caseVariantResults, requiredFqn);
            AssertGateFailure(caseVariant, "missing");

            foreach (string outcome in new[] { "Skipped", "Inconclusive", "NotExecuted", "Failed" })
            {
                string nonPassedResults = Path.Combine(root, outcome + ".trx");
                WriteSyntheticTrx(nonPassedResults, (requiredFqn, outcome));
                using JsonDocument nonPassed = ReadOutcomeGateProbe(nonPassedResults, requiredFqn);
                AssertGateFailure(nonPassed, outcome);
            }

            string passedResults = Path.Combine(root, "passed.trx");
            WriteSyntheticTrx(passedResults, (requiredFqn, "Passed"));
            using JsonDocument passed = ReadOutcomeGateProbe(passedResults, requiredFqn);
            Assert.IsTrue(GetProperty(passed.RootElement, "Passed").GetBoolean(), passed.RootElement.GetRawText());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public void ReleaseOutcomeGateAllowsOnlyExactOptionalSkipWithReason()
    {
        const string requiredFqn = "BeMusicSeeker.Tests.Synthetic.Required.ReleaseOutcome";
        const string optionalFqn = "BeMusicSeeker.Tests.Synthetic.Optional.ReleaseOutcome";
        const string optionalReason = "provisioned acceptance dependency";
        string root = Path.Combine(
            Path.GetTempPath(),
            "BeMusicSeeker-VerificationOptionalOutcomeContractTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string allowedResults = Path.Combine(root, "allowed.trx");
            WriteSyntheticTrx(allowedResults, (requiredFqn, "Passed"), (optionalFqn, "Skipped"));
            using JsonDocument allowed = ReadOutcomeGateProbe(
                allowedResults,
                requiredFqn,
                optionalFqn,
                optionalReason);
            AssertOptionalReceipt(allowed, optionalFqn, "Skipped", optionalReason);

            string notExecutedResults = Path.Combine(root, "not-executed.trx");
            WriteSyntheticTrx(notExecutedResults, (requiredFqn, "Passed"), (optionalFqn, "NotExecuted"));
            using JsonDocument notExecuted = ReadOutcomeGateProbe(
                notExecutedResults,
                requiredFqn,
                optionalFqn,
                optionalReason);
            AssertOptionalReceipt(notExecuted, optionalFqn, "NotExecuted", optionalReason);

            string inconclusiveResults = Path.Combine(root, "inconclusive.json");
            WriteSyntheticJson(inconclusiveResults, (requiredFqn, "Passed"), (optionalFqn, "Inconclusive"));
            using JsonDocument inconclusive = ReadOutcomeGateProbe(
                inconclusiveResults,
                requiredFqn,
                optionalFqn,
                optionalReason);
            AssertOptionalReceipt(inconclusive, optionalFqn, "Inconclusive", optionalReason);

            string unknownResults = Path.Combine(root, "unknown.trx");
            WriteSyntheticTrx(
                unknownResults,
                (requiredFqn, "Passed"),
                ("BeMusicSeeker.Tests.Synthetic.Unknown.ReleaseOutcome", "Skipped"));
            using JsonDocument unknown = ReadOutcomeGateProbe(
                unknownResults,
                requiredFqn,
                optionalFqn,
                optionalReason);
            AssertGateFailure(unknown, "Unknown");

            string unknownNotExecutedResults = Path.Combine(root, "unknown-not-executed.trx");
            WriteSyntheticTrx(
                unknownNotExecutedResults,
                (requiredFqn, "Passed"),
                ("BeMusicSeeker.Tests.Synthetic.Unknown.NotExecuted.ReleaseOutcome", "NotExecuted"));
            using JsonDocument unknownNotExecuted = ReadOutcomeGateProbe(
                unknownNotExecutedResults,
                requiredFqn,
                optionalFqn,
                optionalReason);
            AssertGateFailure(unknownNotExecuted);

            string emptyReasonResults = Path.Combine(root, "empty-reason.trx");
            WriteSyntheticTrx(emptyReasonResults, (requiredFqn, "Passed"), (optionalFqn, "Skipped"));
            using JsonDocument emptyReason = ReadOutcomeGateProbe(emptyReasonResults, requiredFqn, optionalFqn, "");
            AssertGateFailure(emptyReason, "reason");

            foreach (string outcome in new[] { "Failed", "Error", "Aborted" })
            {
                string failedResults = Path.Combine(root, "allowlisted-" + outcome + ".trx");
                WriteSyntheticTrx(failedResults, (requiredFqn, "Passed"), (optionalFqn, outcome));
                using JsonDocument failed = ReadOutcomeGateProbe(
                    failedResults,
                    requiredFqn,
                    optionalFqn,
                    optionalReason);
                AssertGateFailure(failed);
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public void ReleaseOutcomeGateLoadsRequiredRosterFromSuppliedPath()
    {
        const string requiredFqn = "BeMusicSeeker.Tests.Synthetic.Roster.ReleaseOutcome";
        string root = Path.Combine(
            Path.GetTempPath(),
            "BeMusicSeeker-VerificationRosterContractTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string resultsPath = Path.Combine(root, "passed.trx");
            string rosterPath = Path.Combine(root, "roster.json");
            WriteSyntheticTrx(resultsPath, (requiredFqn, "Passed"));
            WriteSyntheticRoster(rosterPath, ("SYNTHETIC-REQUIRED", requiredFqn));

            using JsonDocument result = ReadOutcomeRosterGateProbe(resultsPath, rosterPath);
            Assert.IsTrue(GetProperty(result.RootElement, "Passed").GetBoolean(), result.RootElement.GetRawText());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static JsonDocument ReadOutcomeGateProbe(
        string resultPath,
        string requiredFqn,
        string? optionalFqn = null,
        string? optionalReason = null)
    {
        string repositoryRoot = FindRepositoryRoot();
        string helperPath = QuotePowerShellLiteral(Path.Combine(repositoryRoot, "scripts", "verification-test-outcomes.ps1"));
        string resultLiteral = QuotePowerShellLiteral(resultPath);
        string requiredLiteral = QuotePowerShellLiteral(requiredFqn);
        string optionalExpression = optionalFqn is null
            ? "@()"
            : "@([pscustomobject]@{ fullyQualifiedName = "
              + QuotePowerShellLiteral(optionalFqn)
              + "; reason = "
              + QuotePowerShellLiteral(optionalReason ?? string.Empty)
              + " })";
        string command = string.Join(
            Environment.NewLine,
            "Set-StrictMode -Version Latest",
            "$ErrorActionPreference = 'Stop'",
            ". " + helperPath,
            "$caught = $null",
            "try {",
            "  $gate = Assert-VerificationTestOutcomes -ResultPaths @(" + resultLiteral + ") -RequiredFqns @(" + requiredLiteral + ") -OptionalSkipAllowlist " + optionalExpression,
            "} catch { $caught = $_.Exception.Message }",
            "[pscustomobject]@{ Passed = $null -eq $caught; Message = if ($null -eq $caught) { [string]::Empty } else { [string]$caught }; Receipt = if ($null -eq $caught) { $gate.Receipt } else { $null } } | ConvertTo-Json -Depth 8 -Compress");
        return ReadPowerShellJson(new[] { "-Command", command });
    }

    private static JsonDocument ReadOutcomeRosterGateProbe(string resultPath, string rosterPath)
    {
        string repositoryRoot = FindRepositoryRoot();
        string helperPath = QuotePowerShellLiteral(Path.Combine(repositoryRoot, "scripts", "verification-test-outcomes.ps1"));
        string resultLiteral = QuotePowerShellLiteral(resultPath);
        string rosterLiteral = QuotePowerShellLiteral(rosterPath);
        string command = string.Join(
            Environment.NewLine,
            "Set-StrictMode -Version Latest",
            "$ErrorActionPreference = 'Stop'",
            ". " + helperPath,
            "$caught = $null",
            "try {",
            "  $gate = Assert-VerificationTestOutcomes -ResultPaths @(" + resultLiteral + ") -RosterPath " + rosterLiteral,
            "} catch { $caught = $_.Exception.Message }",
            "[pscustomobject]@{ Passed = $null -eq $caught; Message = if ($null -eq $caught) { [string]::Empty } else { [string]$caught } } | ConvertTo-Json -Depth 8 -Compress");
        return ReadPowerShellJson(new[] { "-Command", command });
    }

    private static void AssertGateFailure(JsonDocument result, string expectedMessagePart)
    {
        AssertGateFailure(result);
        StringAssert.Contains(
            GetProperty(result.RootElement, "Message").GetString()!,
            expectedMessagePart,
            result.RootElement.GetRawText());
    }

    private static void AssertGateFailure(JsonDocument result)
    {
        Assert.IsFalse(GetProperty(result.RootElement, "Passed").GetBoolean(), result.RootElement.GetRawText());
    }

    private static void AssertOptionalReceipt(
        JsonDocument result,
        string expectedFullyQualifiedName,
        string expectedOutcome,
        string expectedReason)
    {
        Assert.IsTrue(GetProperty(result.RootElement, "Passed").GetBoolean(), result.RootElement.GetRawText());
        JsonElement receipt = GetProperty(result.RootElement, "Receipt");
        JsonElement optionalSkips = GetProperty(receipt, "optionalSkips");
        Assert.AreEqual(1, optionalSkips.GetArrayLength(), result.RootElement.GetRawText());
        JsonElement optional = optionalSkips[0];
        Assert.AreEqual(expectedFullyQualifiedName, GetProperty(optional, "fullyQualifiedName").GetString());
        Assert.AreEqual(expectedOutcome, GetProperty(optional, "outcome").GetString());
        Assert.AreEqual(expectedReason, GetProperty(optional, "reason").GetString());
    }

    private static void WriteSyntheticTrx(string path, params (string FullyQualifiedName, string Outcome)[] results)
    {
        var builder = new StringBuilder(
            "<?xml version=\"1.0\" encoding=\"utf-8\"?><TestRun><Results>",
            256);
        foreach ((string fullyQualifiedName, string outcome) in results)
        {
            builder.Append("<UnitTestResult fullyQualifiedName=\"")
                .Append(System.Security.SecurityElement.Escape(fullyQualifiedName))
                .Append("\" outcome=\"")
                .Append(System.Security.SecurityElement.Escape(outcome))
                .Append("\" />");
        }
        builder.Append("</Results></TestRun>");
        File.WriteAllText(path, builder.ToString(), Encoding.UTF8);
    }

    private static void WriteSyntheticJson(string path, params (string FullyQualifiedName, string Outcome)[] results)
    {
        string json = JsonSerializer.Serialize(new
        {
            results = results
                .Select(result => new
                {
                    fullyQualifiedName = result.FullyQualifiedName,
                    outcome = result.Outcome
                })
                .ToArray()
        });
        File.WriteAllText(path, json, Encoding.UTF8);
    }

    private static void WriteSyntheticRoster(string path, params (string ContractId, string FullyQualifiedName)[] requiredEntries)
    {
        string json = JsonSerializer.Serialize(new
        {
            releaseOutcome = new
            {
                schemaVersion = 1,
                required = requiredEntries
                    .Select(entry => new
                    {
                        contractId = entry.ContractId,
                        fullyQualifiedName = entry.FullyQualifiedName
                    })
                    .ToArray(),
                optionalSkipAllowlist = Array.Empty<object>()
            }
        });
        File.WriteAllText(path, json, Encoding.UTF8);
    }

    private static JsonDocument ReadPowerShellJson(IReadOnlyList<string> arguments)
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
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        Assert.IsTrue(process.Start(), "The PowerShell outcome-gate process did not start.");
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
        Task<string> errorTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(processTimeoutMilliseconds))
        {
            string cleanup = StopProcessTree(process, cleanupTimeoutMilliseconds);
            Assert.Fail(
                $"The PowerShell outcome-gate process exceeded the {processTimeoutMilliseconds / 1000}-second timeout. " +
                $"Process cleanup: {cleanup}. stdout: {GetCompletedTaskValue(outputTask)} stderr: {GetCompletedTaskValue(errorTask)}");
            return default!;
        }

        try
        {
            if (!Task.WaitAll(new Task[] { outputTask, errorTask }, streamTimeoutMilliseconds))
            {
                string cleanup = StopProcessTree(process, cleanupTimeoutMilliseconds);
                Assert.Fail(
                    $"The PowerShell outcome-gate output did not close within the {streamTimeoutMilliseconds / 1000}-second timeout. " +
                    $"Process cleanup: {cleanup}. stdout: {GetCompletedTaskValue(outputTask)} stderr: {GetCompletedTaskValue(errorTask)}");
                return default!;
            }
        }
        catch (Exception exception) when (exception is not AssertFailedException)
        {
            string cleanup = StopProcessTree(process, cleanupTimeoutMilliseconds);
            Assert.Fail($"The PowerShell outcome-gate output failed: {exception}. Process cleanup: {cleanup}");
            return default!;
        }

        string output = outputTask.GetAwaiter().GetResult();
        string error = errorTask.GetAwaiter().GetResult();
        Assert.AreEqual(0, process.ExitCode, error);
        Assert.IsTrue(string.IsNullOrWhiteSpace(error), error);
        return JsonDocument.Parse(output);
    }

    private static string QuotePowerShellLiteral(string value)
    {
        return "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    }

    private static string StopProcessTree(Process process, int cleanupTimeoutMilliseconds)
    {
        var diagnostics = new List<string>();
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception)
        {
            diagnostics.Add("managed tree termination failed: " + exception.Message);
        }

        try
        {
            if (!process.HasExited && !process.WaitForExit(cleanupTimeoutMilliseconds))
            {
                diagnostics.Add($"PowerShell PID {process.Id} remained active after {cleanupTimeoutMilliseconds / 1000}s.");
            }
        }
        catch (Exception exception)
        {
            diagnostics.Add("process cleanup wait failed: " + exception.Message);
        }

        return diagnostics.Count == 0 ? "completed" : string.Join("; ", diagnostics);
    }

    private static string GetCompletedTaskValue(Task<string> task)
    {
        return task.Status == TaskStatus.RanToCompletion ? task.Result : "<unavailable>";
    }

    private static JsonElement GetProperty(JsonElement element, string name)
    {
        Assert.IsTrue(element.TryGetProperty(name, out JsonElement value), $"Outcome property was not found: {name}");
        return value;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
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
}
