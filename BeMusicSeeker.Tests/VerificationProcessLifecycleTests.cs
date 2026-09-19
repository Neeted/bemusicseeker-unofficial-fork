using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
[TestCategory("ProcessIntegration")]
public sealed class VerificationProcessLifecycleTests
{
    private sealed record LedgerEntry(int ProcessId, long CreationIdentity);
    private sealed record PrimitiveEvent(int Sequence, string Operation, int RootProcessId, string Context, long UtcTicks);

    [TestMethod]
    public void NormalExitPreservesCompletedStandardOutputAndError()
    {
        using JsonDocument result = RunProbe("normal");
        Assert.AreEqual(0, result.RootElement.GetProperty("exitCode").GetInt32());
        Assert.AreEqual("stdout-complete", result.RootElement.GetProperty("stdout").GetString());
        Assert.AreEqual("stderr-complete", result.RootElement.GetProperty("stderr").GetString());
        Assert.AreEqual("stdout-complete", result.RootElement.GetProperty("stdoutArtifact").GetString());
        Assert.AreEqual("stderr-complete", result.RootElement.GetProperty("stderrArtifact").GetString());
        Assert.AreEqual(JsonValueKind.Null, result.RootElement.GetProperty("lifecycleArtifact").ValueKind);
        Assert.AreEqual(1, result.RootElement.GetProperty("cleanupTransitionCount").GetInt32());
        PrimitiveEvent[] events = ReadPrimitiveEvents(result.RootElement.GetProperty("primitiveEvents"));
        AssertNoPrimitiveStartedAfterDeadline(result.RootElement, events);
        AssertCleanupTransitionPrecedesPersistenceAndDispose(events);
        CollectionAssert.AreEqual(
            Array.Empty<string>(),
            ReadStringArray(result.RootElement.GetProperty("secondaryDiagnostics")));
        CollectionAssert.AreEqual(
            Array.Empty<int>(),
            ReadIntArray(result.RootElement.GetProperty("remainingOwnedProcessIds")));
    }

    [TestMethod]
    public void RedirectedUtf8OutputPreservesBothPipesAndArtifactsWithoutChangingParent()
    {
        using JsonDocument result = RunProbe("utf8-output");
        Assert.AreEqual(0, result.RootElement.GetProperty("exitCode").GetInt32());
        Assert.IsFalse(result.RootElement.GetProperty("processTimedOut").GetBoolean());
        Assert.AreEqual("stdout-日本語-✓-😀", result.RootElement.GetProperty("stdout").GetString());
        Assert.AreEqual("stderr-失敗-✓-😀", result.RootElement.GetProperty("stderr").GetString());
        Assert.AreEqual("stdout-日本語-✓-😀", result.RootElement.GetProperty("stdoutArtifact").GetString());
        Assert.AreEqual("stderr-失敗-✓-😀", result.RootElement.GetProperty("stderrArtifact").GetString());
        Assert.IsTrue(result.RootElement.GetProperty("parentEncodingUnchanged").GetBoolean());
        Assert.IsTrue(result.RootElement.GetProperty("parentEnvironmentUnchanged").GetBoolean());
        CollectionAssert.AreEqual(Array.Empty<string>(), ReadStringArray(result.RootElement.GetProperty("secondaryDiagnostics")));
        CollectionAssert.AreEqual(Array.Empty<int>(), ReadIntArray(result.RootElement.GetProperty("remainingOwnedProcessIds")));
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
    public void NonzeroExitPreservesPrimaryFailureAndStopsOwnedDescendant()
    {
        using JsonDocument result = RunProbe("nonzero-descendant");
        Assert.AreEqual(7, result.RootElement.GetProperty("exitCode").GetInt32());
        Assert.AreEqual("nonzero-exit", result.RootElement.GetProperty("primaryFailureKind").GetString());
        Assert.AreEqual("primary-stderr", result.RootElement.GetProperty("stderr").GetString());
        CollectionAssert.AreEqual(
            Array.Empty<string>(),
            ReadStringArray(result.RootElement.GetProperty("secondaryDiagnostics")));
        PrimitiveEvent[] events = ReadPrimitiveEvents(result.RootElement.GetProperty("primitiveEvents"));
        Assert.IsTrue(
            ContainsPrimitiveEvent(events, "descendant-stop"),
            "The failure cleanup owner must stop the owned descendant through its exact stop seam.");
        CollectionAssert.AreEqual(
            Array.Empty<int>(),
            ReadIntArray(result.RootElement.GetProperty("remainingOwnedProcessIds")));
    }

    [TestMethod]
    public void LateExitDuringCleanupGraceRemainsExecutionTimeout()
    {
        using JsonDocument result = RunProbe("late-success");

        Assert.IsTrue(result.RootElement.GetProperty("processExited").GetBoolean(), result.RootElement.GetRawText());
        Assert.IsTrue(result.RootElement.GetProperty("processTimedOut").GetBoolean(), result.RootElement.GetRawText());
        Assert.AreEqual("timeout", result.RootElement.GetProperty("primaryFailureKind").GetString());
        Assert.AreEqual("late-success-stdout", result.RootElement.GetProperty("stdout").GetString());
        DateTime cleanupDeadlineUtc = result.RootElement.GetProperty("cleanupDeadlineUtc").GetDateTime();
        Assert.AreEqual(
            cleanupDeadlineUtc.Ticks,
            result.RootElement.GetProperty("cleanupCutoffUtcTicks").GetInt64(),
            "Failure cleanup must carry the phase cleanup deadline without resetting it to a new relative timeout.");
    }

    [TestMethod]
    public void StreamDrainTimeoutDiagnosticIncludesContextAndOwnedCleanupCompletes()
    {
        using JsonDocument result = RunProbe("stream-timeout");
        Assert.IsTrue(
            result.RootElement.GetProperty("processExited").GetBoolean(),
            "The lifecycle root must be observed as exited before stream-drain cleanup is asserted.");
        Assert.IsFalse(result.RootElement.GetProperty("processTimedOut").GetBoolean());
        Assert.AreEqual(JsonValueKind.Null, result.RootElement.GetProperty("primaryFailureKind").ValueKind);
        Assert.AreEqual(0, result.RootElement.GetProperty("exitCode").GetInt32());
        string[] diagnostics = ReadStringArray(result.RootElement.GetProperty("secondaryDiagnostics"));
        Assert.IsTrue(
            AssertStreamTimeoutDiagnosticsIncludeContext(
                diagnostics,
                result.RootElement.GetProperty("rootProcessId").GetInt32()) > 0,
            "The deterministic inherited-handle probe must exercise the bounded stream-drain timeout path.");
        Assert.AreEqual(1, result.RootElement.GetProperty("cleanupTransitionCount").GetInt32());
        Assert.IsTrue(ContainsDiagnostic(diagnostics, "stream-reader-close: skipped after cleanup deadline"));
        Assert.IsTrue(ContainsDiagnostic(diagnostics, "process-lineage-residual-check: skipped after cleanup deadline"));
        PrimitiveEvent[] events = ReadPrimitiveEvents(result.RootElement.GetProperty("primitiveEvents"));
        Assert.IsTrue(
            CountPrimitiveEvents(events, "late-task-fault") >= 2,
            "Both deterministic late stream faults must be observed through the production seam.");
        Assert.IsTrue(
            CountPrimitiveEvents(events, "cleanup-task-wait") > 0,
            "The production seam did not observe a bounded cleanup task wait.");
        AssertNoPrimitiveStartedAfterDeadline(result.RootElement, events);
        Assert.IsFalse(ContainsPrimitiveEvent(events, "reader-close"));
        Assert.IsFalse(
            ContainsPrimitiveEvent(events, "persistence"),
            "A successful process cannot spend the failure cleanup grace on diagnostic persistence.");
        Assert.AreEqual(JsonValueKind.Null, result.RootElement.GetProperty("lifecycleArtifact").ValueKind);
        CollectionAssert.AreEqual(
            Array.Empty<int>(),
            ReadIntArray(result.RootElement.GetProperty("remainingOwnedProcessIds")));
    }

    [TestMethod]
    public void CompletedStdoutDoesNotExtendExecutionDeadlineForPendingStderr()
    {
        using JsonDocument result = RunProbe("asymmetric-stdout-complete");
        Assert.AreEqual("stdout-asymmetric-complete", result.RootElement.GetProperty("stdout").GetString());
        Assert.AreEqual(JsonValueKind.Null, result.RootElement.GetProperty("stdoutArtifact").ValueKind);
        Assert.AreEqual(JsonValueKind.Null, result.RootElement.GetProperty("stderrArtifact").ValueKind);
        string[] diagnostics = ReadStringArray(result.RootElement.GetProperty("secondaryDiagnostics"));
        Assert.IsTrue(ContainsDiagnostic(diagnostics, "stream-drain-timeout: stderr"));
        Assert.IsFalse(ContainsDiagnostic(diagnostics, "stream-drain-timeout: stdout"));
        Assert.AreEqual(JsonValueKind.Null, result.RootElement.GetProperty("lifecycleArtifact").ValueKind);
    }

    [TestMethod]
    public void CompletedStderrDoesNotExtendExecutionDeadlineForPendingStdout()
    {
        using JsonDocument result = RunProbe("asymmetric-stderr-complete");
        Assert.AreEqual("stderr-asymmetric-complete", result.RootElement.GetProperty("stderr").GetString());
        Assert.AreEqual(JsonValueKind.Null, result.RootElement.GetProperty("stdoutArtifact").ValueKind);
        Assert.AreEqual(JsonValueKind.Null, result.RootElement.GetProperty("stderrArtifact").ValueKind);
        string[] diagnostics = ReadStringArray(result.RootElement.GetProperty("secondaryDiagnostics"));
        Assert.IsTrue(ContainsDiagnostic(diagnostics, "stream-drain-timeout: stdout"));
        Assert.IsFalse(ContainsDiagnostic(diagnostics, "stream-drain-timeout: stderr"));
        Assert.AreEqual(JsonValueKind.Null, result.RootElement.GetProperty("lifecycleArtifact").ValueKind);
    }

    [TestMethod]
    public void LateFaultFromLifecycleAIsObservedWithoutContaminatingLifecycleB()
    {
        using JsonDocument result = RunProbe("same-pwsh-late-fault");
        string[] lifecycleADiagnostics = ReadStringArray(
            result.RootElement.GetProperty("lifecycleASecondaryDiagnostics"));
        string[] lifecycleBDiagnostics = ReadStringArray(
            result.RootElement.GetProperty("lifecycleBSecondaryDiagnostics"));
        Assert.IsTrue(ContainsDiagnostic(lifecycleADiagnostics, "stream-drain-timeout: stderr"));
        Assert.IsTrue(ContainsDiagnostic(lifecycleBDiagnostics, "lifecycle-B owned fault"));
        Assert.IsFalse(result.RootElement.GetProperty("lifecycleBContainsLifecycleADiagnostic").GetBoolean());
        Assert.IsTrue(
            result.RootElement.GetProperty("lifecycleAPostSealFaultObservationCount").GetInt32() > 0,
            "The post-seal task fault must still be observed through the lifecycle-local continuation.");
        Assert.AreEqual("A-complete", result.RootElement.GetProperty("lifecycleAStdout").GetString());
        Assert.AreEqual("B-complete", result.RootElement.GetProperty("lifecycleBStdout").GetString());
    }

    [TestMethod]
    public void ActualMonitoredCallerPreservesPostStartPrimaryAndExactOwnership()
    {
        using JsonDocument result = RunProbe("post-start-exception");
        Assert.IsTrue(
            result.RootElement.GetProperty("primaryMessage").GetString()!.Contains(
                "Internal post-start fault injection",
                StringComparison.Ordinal));
        Assert.IsTrue(result.RootElement.GetProperty("rootProcessId").GetInt32() > 0);
        Assert.IsFalse(result.RootElement.GetProperty("rootResidual").GetBoolean());
        CollectionAssert.AreEqual(
            Array.Empty<int>(),
            ReadIntArray(result.RootElement.GetProperty("remainingOwnedProcessIds")));
        CollectionAssert.AreEqual(
            Array.Empty<int>(),
            ReadIntArray(result.RootElement.GetProperty("ledgerResidualProcessIds")));
        Assert.AreEqual(
            "existing-stderr-artifact",
            result.RootElement.GetProperty("stderrArtifact").GetString());
        Assert.IsTrue(
            ContainsDiagnostic(
                ReadStringArray(result.RootElement.GetProperty("lifecycleSecondaryDiagnostics")),
                "post-start-exception"));
    }

    [TestMethod]
    public void TerminalDiagnosticIsIncludedInFinalLifecycleArtifact()
    {
        using JsonDocument result = RunProbe("terminal-diagnostic");
        string[] diagnostics = ReadStringArray(result.RootElement.GetProperty("secondaryDiagnostics"));
        Assert.IsTrue(ContainsDiagnostic(diagnostics, "artifact replace failed"));
        Assert.IsTrue(
            result.RootElement.GetProperty("lifecycleArtifact").GetString()!.Contains(
                "artifact replace failed",
                StringComparison.Ordinal));
    }

    [TestMethod]
    public void FinalDiagnosticFlushFailureRemainsSecondaryAndPreservesSinkArtifact()
    {
        using JsonDocument result = RunProbe("terminal-flush-failure");
        string[] diagnostics = ReadStringArray(result.RootElement.GetProperty("secondaryDiagnostics"));
        Assert.IsTrue(ContainsDiagnostic(diagnostics, "terminal-diagnostic-flush"));
        Assert.AreEqual(JsonValueKind.Null, result.RootElement.GetProperty("lifecycleArtifact").ValueKind);
        Assert.IsTrue(ContainsDiagnostic(diagnostics, "artifact replace failed"));
    }

    [TestMethod]
    public void FunctionalCleanupUsesOneSharedCutoffAndSkipsLaterPrimitiveWork()
    {
        using JsonDocument result = RunProbe("functional-shared-deadline");
        CollectionAssert.AreEqual(
            new[] { "shared-deadline-first", "shared-deadline-second" },
            ReadStringArray(result.RootElement.GetProperty("entryNames")));
        CollectionAssert.AreEqual(
            new[] { false, true },
            ReadBooleanArray(result.RootElement.GetProperty("skippedAfterDeadline")));
        PrimitiveEvent[] events = ReadPrimitiveEvents(result.RootElement.GetProperty("primitiveEvents"));
        long sharedDeadline = result.RootElement.GetProperty("sharedCleanupDeadlineUtcTicks").GetInt64();
        foreach (PrimitiveEvent primitiveEvent in events)
        {
            Assert.IsTrue(
                primitiveEvent.UtcTicks <= sharedDeadline,
                $"Primitive {primitiveEvent.Operation} started after the shared cleanup deadline.");
            Assert.AreNotEqual(
                "shared-deadline-second",
                primitiveEvent.Context,
                "The later entry must not start a filesystem/native/wait primitive after the shared cutoff.");
        }
        Assert.IsTrue(
            ContainsDiagnostic(
                ReadStringArray(result.RootElement.GetProperty("entrySecondaryDiagnostics")),
                "skipped after shared cleanup deadline"));
    }

    [TestMethod]
    public void FunctionalCleanupPreservesCompletedSuccessWithinOriginalExecutionDeadline()
    {
        using JsonDocument result = RunProbe("functional-completed-success");

        Assert.AreEqual("functional-completed-success", result.RootElement.GetProperty("entryName").GetString());
        Assert.IsFalse(result.RootElement.GetProperty("skippedAfterDeadline").GetBoolean());
        Assert.IsTrue(result.RootElement.GetProperty("processExited").GetBoolean());
        Assert.IsFalse(result.RootElement.GetProperty("processTimedOut").GetBoolean());
        Assert.AreEqual(0, result.RootElement.GetProperty("exitCode").GetInt32());
        Assert.AreEqual(JsonValueKind.Null, result.RootElement.GetProperty("primaryFailureKind").ValueKind);
        Assert.AreEqual("late-success-stdout", result.RootElement.GetProperty("stdout").GetString());
        Assert.AreEqual("late-success-stdout", result.RootElement.GetProperty("stdoutArtifact").GetString());
        Assert.AreEqual(
            result.RootElement.GetProperty("executionDeadlineUtcTicks").GetInt64(),
            result.RootElement.GetProperty("cleanupCutoffUtcTicks").GetInt64(),
            "A completed-success wrapper must carry the original execution deadline into the shared lifecycle.");
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
        PrimitiveEvent[] events = ReadPrimitiveEvents(result.RootElement.GetProperty("primitiveEvents"));
        int[] fanoutRootIds = ReadIntArray(result.RootElement.GetProperty("fanoutRootIds"));
        AssertRootFanoutPrecedesLineage(events, fanoutRootIds);
        AssertNoPrimitiveStartedAfterDeadline(result.RootElement, events);
    }

    [TestMethod]
    public void ExpiredDeadlineReportsExactResidualAndOuterLedgerCleanupRemovesIt()
    {
        ProbeRun run = StartProbe("expired-residual");
        try
        {
            Assert.IsTrue(run.Process.WaitForExit(30_000), "The expired residual probe did not terminate.");
            Assert.AreEqual(0, run.Process.ExitCode, "The expired residual probe failed.");
            Assert.IsTrue(File.Exists(run.ResultPath));
            using var result = JsonDocument.Parse(File.ReadAllText(run.ResultPath));
            int[] residualIds = ReadIntArray(result.RootElement.GetProperty("remainingOwnedProcessIds"));
            Assert.IsTrue(residualIds.Length > 0, "The expired deadline must report a nonempty exact residual PID set.");
            LedgerEntry[] ledger = ReadLedger(run.LedgerPath);
            Dictionary<int, LedgerEntry> ledgerByPid = ledger.ToDictionary(entry => entry.ProcessId);
            var residualPidSet = new HashSet<int>();
            foreach (int residualId in residualIds)
            {
                Assert.IsTrue(
                    residualPidSet.Add(residualId),
                    $"The lifecycle returned duplicate residual PID {residualId}.");
                Assert.IsTrue(
                    ledgerByPid.TryGetValue(residualId, out LedgerEntry? entry),
                    $"Residual PID {residualId} did not map to an exact ownership ledger entry.");
                Assert.IsTrue(
                    IsExactIdentityAlive(entry!),
                    $"Exact residual PID {residualId} was not alive before outer ledger cleanup.");
            }
            Assert.AreEqual(residualIds.Length, residualPidSet.Count);
            PrimitiveEvent[] events = ReadPrimitiveEvents(result.RootElement.GetProperty("primitiveEvents"));
            AssertNoPrimitiveStartedAfterDeadline(result.RootElement, events);
            string cleanup = CleanupLedger(run.LedgerPath);
            Assert.IsTrue(string.IsNullOrWhiteSpace(cleanup), cleanup);
            foreach (LedgerEntry entry in ledger)
            {
                Assert.IsFalse(IsExactIdentityAlive(entry), $"Ledger PID {entry.ProcessId} remained after exact cleanup.");
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
            using var document = JsonDocument.Parse(line);
            entries.Add(new LedgerEntry(
                document.RootElement.GetProperty("pid").GetInt32(),
                document.RootElement.GetProperty("creationIdentity").GetInt64()));
        }
        return entries.ToArray();
    }

    private static PrimitiveEvent[] ReadPrimitiveEvents(JsonElement array)
    {
        var events = new List<PrimitiveEvent>();
        foreach (JsonElement value in array.EnumerateArray())
        {
            events.Add(new PrimitiveEvent(
                value.GetProperty("Sequence").GetInt32(),
                value.GetProperty("Operation").GetString()!,
                value.GetProperty("RootProcessId").GetInt32(),
                value.GetProperty("Context").GetString()!,
                value.GetProperty("UtcTicks").GetInt64()));
        }
        return events.ToArray();
    }

    private static void AssertNoPrimitiveStartedAfterDeadline(JsonElement result, IEnumerable<PrimitiveEvent> events)
    {
        long deadlineTicks = result.GetProperty("cleanupDeadlineUtcTicks").GetInt64();
        foreach (PrimitiveEvent primitiveEvent in events)
        {
            if (primitiveEvent.Operation.Equals("late-task-fault", StringComparison.Ordinal))
            {
                continue;
            }
            Assert.IsTrue(
                primitiveEvent.UtcTicks <= deadlineTicks,
                $"Primitive {primitiveEvent.Operation} for PID {primitiveEvent.RootProcessId} started after the cleanup deadline.");
        }
    }

    private static void AssertCleanupTransitionPrecedesPersistenceAndDispose(IEnumerable<PrimitiveEvent> events)
    {
        int transitionSequence = int.MaxValue;
        bool hasTransition = false;
        foreach (PrimitiveEvent primitiveEvent in events)
        {
            if (primitiveEvent.Operation.Equals("cleanup-transition", StringComparison.Ordinal))
            {
                transitionSequence = Math.Min(transitionSequence, primitiveEvent.Sequence);
                hasTransition = true;
            }
        }
        Assert.IsTrue(hasTransition, "The production seam did not observe cleanup transition.");
        foreach (PrimitiveEvent primitiveEvent in events)
        {
            if (primitiveEvent.Operation is "persistence" or "dispose")
            {
                Assert.IsTrue(
                    transitionSequence < primitiveEvent.Sequence,
                    $"Cleanup transition must precede actual {primitiveEvent.Operation} primitive.");
            }
        }
    }

    private static void AssertRootFanoutPrecedesLineage(
        IEnumerable<PrimitiveEvent> events,
        IEnumerable<int> rootProcessIds)
    {
        var roots = new HashSet<int>(rootProcessIds);
        int maximumRootStopSequence = 0;
        int firstLineageOrCreationSequence = int.MaxValue;
        var stoppedRoots = new HashSet<int>();
        foreach (PrimitiveEvent primitiveEvent in events)
        {
            if (primitiveEvent.Operation.Equals("root-stop", StringComparison.Ordinal) &&
                roots.Contains(primitiveEvent.RootProcessId))
            {
                stoppedRoots.Add(primitiveEvent.RootProcessId);
                maximumRootStopSequence = Math.Max(maximumRootStopSequence, primitiveEvent.Sequence);
            }
            if (primitiveEvent.Operation is "lineage-snapshot" or "creation-query")
            {
                firstLineageOrCreationSequence = Math.Min(firstLineageOrCreationSequence, primitiveEvent.Sequence);
            }
        }
        Assert.AreEqual(roots.Count, stoppedRoots.Count, "Every retained root must receive a root-stop primitive.");
        foreach (int rootProcessId in roots)
        {
            Assert.IsTrue(stoppedRoots.Contains(rootProcessId), $"Root PID {rootProcessId} did not receive a root-stop primitive.");
        }
        Assert.AreNotEqual(int.MaxValue, firstLineageOrCreationSequence, "The production seam did not observe a lineage or creation query.");
        Assert.IsTrue(
            firstLineageOrCreationSequence > maximumRootStopSequence,
            "Lineage and creation queries must begin after every root-stop primitive.");
    }

    private static int CountPrimitiveEvents(IEnumerable<PrimitiveEvent> events, string operation)
    {
        int count = 0;
        foreach (PrimitiveEvent primitiveEvent in events)
        {
            if (primitiveEvent.Operation.Equals(operation, StringComparison.Ordinal))
            {
                count++;
            }
        }
        return count;
    }

    private static bool ContainsPrimitiveEvent(IEnumerable<PrimitiveEvent> events, string operation)
    {
        foreach (PrimitiveEvent primitiveEvent in events)
        {
            if (primitiveEvent.Operation.Equals(operation, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
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
            using var process = Process.GetProcessById(entry.ProcessId);
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

    private static bool[] ReadBooleanArray(JsonElement array)
    {
        var values = new List<bool>();
        foreach (JsonElement value in array.EnumerateArray())
        {
            values.Add(value.GetBoolean());
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
