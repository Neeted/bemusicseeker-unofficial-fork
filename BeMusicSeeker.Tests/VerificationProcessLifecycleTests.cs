using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
[TestCategory("ProcessIntegration")]
public sealed class VerificationProcessLifecycleTests
{
    private sealed record LedgerEntry(int ProcessId, long CreationIdentity);

    [TestMethod]
    public void NormalExitPreservesCompletedStandardOutputAndError()
    {
        using JsonDocument result = RunProbe("normal");
        Assert.AreEqual(0, result.RootElement.GetProperty("exitCode").GetInt32());
        Assert.AreEqual("stdout-complete", result.RootElement.GetProperty("stdout").GetString());
        Assert.AreEqual("stderr-complete", result.RootElement.GetProperty("stderr").GetString());
        Assert.AreEqual(1, result.RootElement.GetProperty("cleanupTransitionCount").GetInt32());
        CollectionAssert.AreEqual(
            Array.Empty<string>(),
            ReadStringArray(result.RootElement.GetProperty("secondaryDiagnostics")));
        CollectionAssert.AreEqual(
            Array.Empty<int>(),
            ReadIntArray(result.RootElement.GetProperty("remainingOwnedProcessIds")));
    }

    [TestMethod]
    public void RootExitWithOwnedDescendantReturnsBoundedCleanupDiagnostic()
    {
        using JsonDocument result = RunProbe("descendant-root");
        Assert.AreEqual(0, result.RootElement.GetProperty("exitCode").GetInt32());
        Assert.IsFalse(result.RootElement.GetProperty("processTimedOut").GetBoolean());
        string[] diagnostics = ReadStringArray(result.RootElement.GetProperty("secondaryDiagnostics"));
        AssertStreamTimeoutDiagnosticsIncludeContext(
            diagnostics,
            result.RootElement.GetProperty("rootProcessId").GetInt32());
        Assert.IsTrue(
            ContainsDiagnostic(diagnostics, "owned-descendant-cleanup")
            || ContainsDiagnostic(diagnostics, "stream-drain-timeout"),
            "The inherited-handle probe must retain a bounded stream or owned-descendant diagnostic.");
        CollectionAssert.AreEqual(
            Array.Empty<int>(),
            ReadIntArray(result.RootElement.GetProperty("remainingOwnedProcessIds")));
    }

    [TestMethod]
    public void NonzeroExitPreservesPrimaryFailureAlongsideCleanupDiagnostic()
    {
        using JsonDocument result = RunProbe("nonzero-descendant");
        Assert.AreEqual(7, result.RootElement.GetProperty("exitCode").GetInt32());
        Assert.AreEqual("nonzero-exit", result.RootElement.GetProperty("primaryFailureKind").GetString());
        Assert.AreEqual("primary-stderr", result.RootElement.GetProperty("stderr").GetString());
        string[] diagnostics = ReadStringArray(result.RootElement.GetProperty("secondaryDiagnostics"));
        AssertStreamTimeoutDiagnosticsIncludeContext(
            diagnostics,
            result.RootElement.GetProperty("rootProcessId").GetInt32());
        Assert.IsTrue(ContainsDiagnostic(diagnostics, "owned-descendant-cleanup"));
        CollectionAssert.AreEqual(
            Array.Empty<int>(),
            ReadIntArray(result.RootElement.GetProperty("remainingOwnedProcessIds")));
    }

    [TestMethod]
    public void StreamDrainTimeoutDiagnosticIncludesContextAndOwnedCleanupCompletes()
    {
        using JsonDocument result = RunProbe("stream-timeout");
        Assert.AreEqual(0, result.RootElement.GetProperty("exitCode").GetInt32());
        Assert.IsFalse(result.RootElement.GetProperty("processTimedOut").GetBoolean());
        string[] diagnostics = ReadStringArray(result.RootElement.GetProperty("secondaryDiagnostics"));
        Assert.IsTrue(
            AssertStreamTimeoutDiagnosticsIncludeContext(
                diagnostics,
                result.RootElement.GetProperty("rootProcessId").GetInt32()) > 0,
            "The deterministic inherited-handle probe must exercise the bounded stream-drain timeout path.");
        Assert.AreEqual(1, result.RootElement.GetProperty("cleanupTransitionCount").GetInt32());
        Assert.IsTrue(ContainsDiagnostic(diagnostics, "stream-reader-close: skipped after cleanup deadline"));
        Assert.IsTrue(ContainsDiagnostic(diagnostics, "process-lineage-residual-check: skipped after cleanup deadline"));
        CollectionAssert.AreEqual(
            Array.Empty<int>(),
            ReadIntArray(result.RootElement.GetProperty("remainingOwnedProcessIds")));
    }

    [TestMethod]
    public void ProbeFailureCleanupUsesExactLedgerWhenResultIsMissing()
    {
        ProbeRun run = StartProbe("missing-result");
        try
        {
            Assert.IsTrue(run.Process.WaitForExit(30_000), "The failing lifecycle probe did not terminate.");
            Assert.AreNotEqual(0, run.Process.ExitCode, "The missing-result probe must fail before writing result JSON.");
            Assert.IsFalse(File.Exists(run.ResultPath), "The failing probe unexpectedly wrote result JSON.");

            LedgerEntry[] ledger = ReadLedger(run.LedgerPath);
            Assert.IsTrue(ledger.Length >= 2, "The failing probe must ledger both its root and child before result persistence.");
            string ledgerCleanup = CleanupLedger(run.LedgerPath);
            if (!run.Process.HasExited)
            {
                StopProcessTree(run.Process);
            }
            string residualCleanup = CleanupLedger(run.LedgerPath);
            Assert.IsTrue(
                string.IsNullOrWhiteSpace(ledgerCleanup) && string.IsNullOrWhiteSpace(residualCleanup),
                $"Exact lifecycle ledger cleanup failed: {ledgerCleanup}; {residualCleanup}");

            foreach (LedgerEntry entry in ledger)
            {
                Assert.IsFalse(
                    IsExactIdentityAlive(entry),
                    $"Ledger PID {entry.ProcessId} remained active after outer harness cleanup.");
            }
        }
        finally
        {
            if (!run.Process.HasExited)
            {
                StopProcessTree(run.Process);
            }
            CleanupLedger(run.LedgerPath);
            run.Process.Dispose();
            TryDeleteDirectory(run.DiagnosticsDirectory);
        }
    }

    [TestMethod]
    public void FunctionalRootFanoutStopsEveryRootBeforeLineageCollection()
    {
        using JsonDocument result = RunProbe("fanout-order");
        Assert.AreEqual(2, result.RootElement.GetProperty("fanoutRootCount").GetInt32());
        Assert.IsTrue(result.RootElement.GetProperty("fanoutRootsExitedBeforeLineage").GetBoolean());
        CollectionAssert.AreEqual(
            Array.Empty<string>(),
            ReadStringArray(result.RootElement.GetProperty("fanoutFailures")));
        CollectionAssert.AreEqual(
            new[] { 1, 1 },
            ReadIntArray(result.RootElement.GetProperty("lifecycleTransitionCounts")));
        CollectionAssert.AreEqual(
            Array.Empty<int>(),
            ReadIntArray(result.RootElement.GetProperty("remainingOwnedProcessIds")));
    }

    private static JsonDocument RunProbe(string scenario)
    {
        ProbeRun run = StartProbe(scenario);
        using Process process = run.Process;
        const int processTimeoutMilliseconds = 30_000;
        try
        {
            if (!process.WaitForExit(processTimeoutMilliseconds))
            {
                string cleanup = StopProcessTree(process);
                Assert.Fail(
                    $"The lifecycle probe exceeded {processTimeoutMilliseconds / 1000}s. "
                    + $"Cleanup: {cleanup}; diagnostics: {run.DiagnosticsDirectory}");
            }

            Assert.AreEqual(
                0,
                process.ExitCode,
                $"The lifecycle probe failed. Diagnostics: {run.DiagnosticsDirectory}");
            Assert.IsTrue(
                File.Exists(run.ResultPath),
                $"The lifecycle probe did not persist its structured result: {run.ResultPath}");
            return JsonDocument.Parse(File.ReadAllText(run.ResultPath));
        }
        finally
        {
            string ledgerCleanup = CleanupLedger(run.LedgerPath);
            if (!process.HasExited)
            {
                StopProcessTree(process);
            }
            string residualCleanup = CleanupLedger(run.LedgerPath);
            Assert.IsTrue(
                string.IsNullOrWhiteSpace(ledgerCleanup) && string.IsNullOrWhiteSpace(residualCleanup),
                $"Exact lifecycle ledger cleanup failed: {ledgerCleanup}; {residualCleanup}");
            TryDeleteDirectory(run.DiagnosticsDirectory);
        }
    }

    private sealed record ProbeRun(
        Process Process,
        string DiagnosticsDirectory,
        string ResultPath,
        string LedgerPath);

    private static ProbeRun StartProbe(string scenario)
    {
        string repositoryRoot = FindRepositoryRoot();
        string diagnosticsDirectory = Path.Combine(
            Path.GetTempPath(),
            "BeMusicSeekerVerificationProcessLifecycle",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(diagnosticsDirectory);
        string probePath = Path.Combine(
            repositoryRoot,
            "scripts",
            "test-fixtures",
            "verification-process-lifecycle-probe.ps1");
        string resultPath = Path.Combine(diagnosticsDirectory, "probe-result.json");
        var startInfo = new ProcessStartInfo
        {
            FileName = "pwsh",
            WorkingDirectory = repositoryRoot,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(probePath);
        startInfo.ArgumentList.Add("-Scenario");
        startInfo.ArgumentList.Add(scenario);
        startInfo.ArgumentList.Add("-DiagnosticsDirectory");
        startInfo.ArgumentList.Add(diagnosticsDirectory);
        startInfo.ArgumentList.Add("-ResultPath");
        startInfo.ArgumentList.Add(resultPath);

        var process = new Process { StartInfo = startInfo };
        Assert.IsTrue(process.Start(), "The lifecycle probe process did not start.");
        return new ProbeRun(
            process,
            diagnosticsDirectory,
            resultPath,
            Path.Combine(diagnosticsDirectory, "ownership-ledger.jsonl"));
    }

    private static LedgerEntry[] ReadLedger(string ledgerPath)
    {
        Assert.IsTrue(File.Exists(ledgerPath), $"The lifecycle probe did not persist its ownership ledger: {ledgerPath}");
        var entries = new List<LedgerEntry>();
        foreach (string line in File.ReadLines(ledgerPath))
        {
            using JsonDocument document = JsonDocument.Parse(line);
            entries.Add(new LedgerEntry(
                document.RootElement.GetProperty("pid").GetInt32(),
                document.RootElement.GetProperty("creationIdentity").GetInt64()));
        }
        return entries.ToArray();
    }

    private static string CleanupLedger(string ledgerPath)
    {
        if (!File.Exists(ledgerPath))
        {
            return string.Empty;
        }

        var diagnostics = new List<string>();
        foreach (LedgerEntry entry in ReadLedger(ledgerPath))
        {
            Process? owned = null;
            try
            {
                owned = Process.GetProcessById(entry.ProcessId);
                long observedIdentity = owned.StartTime.ToUniversalTime().Ticks;
                if (observedIdentity != entry.CreationIdentity)
                {
                    diagnostics.Add($"PID {entry.ProcessId} creation identity mismatch; not stopped.");
                    continue;
                }
                if (!owned.HasExited)
                {
                    owned.Kill(entireProcessTree: false);
                }
                if (!owned.HasExited && !owned.WaitForExit(5_000))
                {
                    diagnostics.Add($"PID {entry.ProcessId} remained active after exact cleanup.");
                }
            }
            catch (ArgumentException)
            {
                // Exact PID is already absent; no process-table or name scan is needed.
            }
            catch (Exception exception)
            {
                diagnostics.Add($"PID {entry.ProcessId} exact cleanup failed: {exception.Message}");
            }
            finally
            {
                owned?.Dispose();
            }
        }
        return string.Join("; ", diagnostics);
    }

    private static bool IsExactIdentityAlive(LedgerEntry entry)
    {
        try
        {
            using Process process = Process.GetProcessById(entry.ProcessId);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == entry.CreationIdentity;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string StopProcessTree(Process process)
    {
        var diagnostics = new List<string>();
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            if (!process.HasExited && !process.WaitForExit(5_000))
            {
                diagnostics.Add($"probe PID {process.Id} remained active after cleanup.");
            }
        }
        catch (Exception exception)
        {
            diagnostics.Add("probe cleanup failed: " + exception.Message);
        }
        return diagnostics.Count == 0 ? "completed" : string.Join("; ", diagnostics);
    }

    private static string[] ReadStringArray(JsonElement array)
    {
        var values = new List<string>();
        foreach (JsonElement value in array.EnumerateArray())
        {
            values.Add(value.GetString()!);
        }
        return values.ToArray();
    }

    private static int[] ReadIntArray(JsonElement array)
    {
        var values = new List<int>();
        foreach (JsonElement value in array.EnumerateArray())
        {
            values.Add(value.GetInt32());
        }
        return values.ToArray();
    }

    private static bool ContainsDiagnostic(IEnumerable<string> diagnostics, string value)
    {
        foreach (string diagnostic in diagnostics)
        {
            if (diagnostic.Contains(value, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private static int AssertStreamTimeoutDiagnosticsIncludeContext(
        IEnumerable<string> diagnostics,
        int rootProcessId)
    {
        int matchedDiagnostics = 0;
        foreach (string diagnostic in diagnostics)
        {
            if (!diagnostic.Contains("stream-drain-timeout", StringComparison.Ordinal))
            {
                continue;
            }

            matchedDiagnostics++;

            Assert.IsTrue(
                diagnostic.Contains($"root PID {rootProcessId};", StringComparison.Ordinal),
                $"Stream timeout diagnostic did not identify root PID {rootProcessId}: {diagnostic}");
            Assert.IsTrue(
                Regex.IsMatch(
                    diagnostic,
                    @"^stream-drain-timeout: (stdout|stderr); root PID \d+; elapsed \d+ms;"),
                $"Stream timeout diagnostic did not include stream and elapsed context: {diagnostic}");
        }
        return matchedDiagnostics;
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch
        {
            // Probe assertions remain authoritative; temporary diagnostics are best-effort.
        }
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
}
