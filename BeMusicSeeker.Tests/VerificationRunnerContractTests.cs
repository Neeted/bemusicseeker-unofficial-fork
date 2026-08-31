using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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
    private const int FunctionalHardTimeoutSeconds = 300;
    private const int FunctionalReportingTargetSeconds = 180;
    private const string V216HappyPathResultName =
        "BeMusicSeeker.ReleaseAcceptance.V216FirstHopAcceptance.HappyPath";
    private const string V216ManagedFileLockResultName =
        "BeMusicSeeker.ReleaseAcceptance.V216FirstHopAcceptance.ManagedFileLockCharacterization";

    [TestMethod]
    public void ModeMappings_UseCanonicalFunctionalExactlyOnceForUnfilteredRoutes()
    {
        using JsonDocument contract = ReadRunnerContract();
        JsonElement mappings = GetProperty(contract.RootElement, "ModeMappings");

        Assert.AreEqual(4, mappings.GetArrayLength());
        AssertModeMapping(mappings, "Quick-no-filter", "Quick", "empty", 1, "canonical-functional");
        AssertModeMapping(mappings, "Functional", "Functional", "empty", 1, "canonical-functional");
        AssertModeMapping(mappings, "Full", "Full", "empty", 1, "full-with-canonical-functional");
        AssertModeMapping(mappings, "Quick-with-filter", "Quick", "provided", 0, "filtered-quick");
    }

    [TestMethod]
    public void CanonicalFunctional_PropagatesTimeoutAndCallerOwnedDiagnostics()
    {
        using JsonDocument contract = ReadRunnerContract();
        JsonElement canonical = GetProperty(contract.RootElement, "CanonicalFunctional");

        Assert.AreEqual("Invoke-CanonicalFunctionalVerification", GetProperty(canonical, "Owner").GetString());
        Assert.AreEqual(1, GetProperty(canonical, "InvocationCount").GetInt32());
        Assert.AreEqual("FunctionalTimeoutSeconds", GetProperty(canonical, "TimeoutArgument").GetString());
        Assert.AreEqual("DiagnosticsRoot", GetProperty(canonical, "DiagnosticsRootArgument").GetString());
        Assert.AreEqual("caller-owned", GetProperty(canonical, "DiagnosticsRootOwnership").GetString());
        Assert.AreEqual("run-root/{restore,build,functional}", GetProperty(canonical, "DiagnosticsLayout").GetString());
        Assert.AreEqual("portable-test-start+FunctionalTimeoutSeconds", GetProperty(canonical, "ExecutionDeadline").GetString());
        Assert.AreEqual("execution-deadline+10-seconds", GetProperty(canonical, "FailureCleanupDeadline").GetString());
        CollectionAssert.AreEqual(
            new[]
            {
                "locked-restore",
                "release-build",
                "built-output-validation",
                "functional-shards",
                "repository-whitespace"
            },
            ReadStringArray(GetProperty(canonical, "Stages")));
    }

    [TestMethod]
    public void FunctionalTimeout_Uses300SecondDefaultAndHardMaximumWithShorterSeam()
    {
        using JsonDocument result = ReadFunctionalTimeoutValidationProbe();

        Assert.AreEqual(FunctionalHardTimeoutSeconds, GetProperty(result.RootElement, "DefaultTimeoutSeconds").GetInt32());
        CollectionAssert.AreEqual(
            new[] { 1, FunctionalHardTimeoutSeconds },
            ReadIntegerArray(GetProperty(result.RootElement, "AcceptedTimeoutSeconds")));
        CollectionAssert.AreEqual(
            new[] { FunctionalHardTimeoutSeconds + 1 },
            ReadIntegerArray(GetProperty(result.RootElement, "RejectedTimeoutSeconds")));
        Assert.IsTrue(GetProperty(result.RootElement, "InternalFunctionRejected301").GetBoolean());
    }

    [TestMethod]
    public void FunctionalDeadlinePolicy_UsesAbsoluteExecutionAndFailureCleanupCutoffs()
    {
        using JsonDocument result = ReadFunctionalDeadlinePolicy();
        JsonElement policy = GetProperty(result.RootElement, "Policy");
        DateTime startUtc = GetProperty(policy, "StartUtc").GetDateTime();
        DateTime executionDeadlineUtc = GetProperty(policy, "ExecutionDeadlineUtc").GetDateTime();
        DateTime failureCleanupDeadlineUtc = GetProperty(policy, "FailureCleanupDeadlineUtc").GetDateTime();

        Assert.AreEqual(FunctionalHardTimeoutSeconds, GetProperty(policy, "TimeoutSeconds").GetInt32());
        Assert.AreEqual(startUtc.AddSeconds(FunctionalHardTimeoutSeconds), executionDeadlineUtc);
        Assert.AreEqual(executionDeadlineUtc.AddSeconds(10), failureCleanupDeadlineUtc);
        Assert.AreEqual(FunctionalHardTimeoutSeconds, GetProperty(result.RootElement, "RemainingAtStart").GetInt32());
        CollectionAssert.AreEqual(
            new[] { "StartUtc", "TimeoutSeconds", "ExecutionDeadlineUtc", "FailureCleanupDeadlineUtc" },
            policy.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.IsFalse(policy.TryGetProperty("CleanupReserveSeconds", out _));
        Assert.IsFalse(policy.TryGetProperty("ProcessDeadlineUtc", out _));
        Assert.IsFalse(policy.TryGetProperty("PreCompletionReserveSeconds", out _));
    }

    [TestMethod]
    public void CanonicalFunctional_ReportsRetainedExitTimePast180WithoutChangingSuccess()
    {
        using JsonDocument exactTarget = ReadFunctionalExitTimeReportProbe(FunctionalReportingTargetSeconds, FunctionalHardTimeoutSeconds);
        using JsonDocument overTarget = ReadFunctionalExitTimeReportProbe(181, FunctionalHardTimeoutSeconds);
        using JsonDocument exactDeadline = ReadFunctionalExitTimeReportProbe(FunctionalHardTimeoutSeconds, FunctionalHardTimeoutSeconds);

        AssertSuccessfulExitTimeReport(exactTarget, FunctionalReportingTargetSeconds);
        Assert.AreEqual(0, GetArrayLengthOrZero(GetProperty(exactTarget.RootElement, "Warnings")), exactTarget.RootElement.GetRawText());

        AssertSuccessfulExitTimeReport(overTarget, 181);
        AssertFunctionalReportingWarning(overTarget, 181);

        AssertSuccessfulExitTimeReport(exactDeadline, FunctionalHardTimeoutSeconds);
        AssertFunctionalReportingWarning(exactDeadline, FunctionalHardTimeoutSeconds);
    }

    [TestMethod]
    public void CanonicalFunctional_Over180NonzeroExitRemainsFailure()
    {
        using JsonDocument result = ReadFunctionalExitTimeReportProbe(181, FunctionalHardTimeoutSeconds, "serial-state-a");

        Assert.IsTrue(GetProperty(result.RootElement, "Caught").GetBoolean(), result.RootElement.GetRawText());
        Assert.IsFalse(GetProperty(result.RootElement, "TimedOut").GetBoolean(), result.RootElement.GetRawText());
        Assert.IsTrue(
            GetProperty(result.RootElement, "ExceptionMessage").GetString()!.Contains("serial-state-a", StringComparison.Ordinal),
            result.RootElement.GetRawText());
    }

    [TestMethod]
    public void CanonicalFunctional_ExitAfter300IsTimeoutEvenInsideCleanupWindow()
    {
        using JsonDocument result = ReadFunctionalExitTimeReportProbe(FunctionalHardTimeoutSeconds + 1, FunctionalHardTimeoutSeconds);

        Assert.IsTrue(GetProperty(result.RootElement, "Caught").GetBoolean(), result.RootElement.GetRawText());
        Assert.IsTrue(GetProperty(result.RootElement, "TimedOut").GetBoolean(), result.RootElement.GetRawText());
        Assert.AreEqual(
            FunctionalHardTimeoutSeconds,
            ReadReportedElapsedSeconds(result.RootElement),
            0.05,
            GetProperty(result.RootElement, "ElapsedLine").GetString());
        Assert.AreEqual(0, GetProperty(result.RootElement, "FanoutAttempts").GetInt32(), result.RootElement.GetRawText());
        DateTime startUtc = GetProperty(result.RootElement, "StartUtc").GetDateTime();
        DateTime cleanupDeadlineUtc = GetProperty(result.RootElement, "CleanupDeadlineUtc").GetDateTime();
        Assert.AreEqual(startUtc.AddSeconds(FunctionalHardTimeoutSeconds + 10), cleanupDeadlineUtc);
        Assert.IsTrue(GetProperty(result.RootElement, "StopRoots").GetBoolean(), result.RootElement.GetRawText());
    }

    [TestMethod]
    public void CanonicalFunctional_UsesOneDeadlineOnlyForPortableAndFanoutExecution()
    {
        using JsonDocument result = ReadFunctionalExecutionProbe();
        JsonElement events = result.RootElement;
        string[] names = events
            .EnumerateArray()
            .Select(item => GetProperty(item, "Name").GetString()!)
            .ToArray();

        CollectionAssert.AreEqual(
            new[]
            {
                "restore",
                "build",
                "built-output-validation",
                "portable-start",
                "deadline-check",
                "portable-exit-observed",
                "fanout-start-bass-collectible",
                "deadline-check",
                "fanout-start-serial-state-a",
                "deadline-check",
                "fanout-start-serial-state-b",
                "deadline-check",
                "fanout-start-remaining-bms-library",
                "deadline-check",
                "fanout-start-remaining",
                "execution-stop-observed",
                "cleanup-start",
                "cleanup-complete",
                "repository-whitespace"
            },
            names);

        JsonElement restore = events[0];
        JsonElement build = events[1];
        JsonElement portable = events[3];
        Assert.IsTrue(GetProperty(restore, "DeadlineUtc").ValueKind is JsonValueKind.Null);
        Assert.IsTrue(GetProperty(build, "DeadlineUtc").ValueKind is JsonValueKind.Null);

        DateTime portableStartedUtc = GetProperty(portable, "TimestampUtc").GetDateTime();
        JsonElement[] deadlineChecks = events
            .EnumerateArray()
            .Where(item => GetProperty(item, "Name").GetString() == "deadline-check")
            .ToArray();
        Assert.AreEqual(5, deadlineChecks.Length);
        DateTime executionDeadlineUtc = GetProperty(deadlineChecks[0], "DeadlineUtc").GetDateTime();
        TimeSpan remainingAtPortableStart = executionDeadlineUtc - portableStartedUtc;
        Assert.IsTrue(
            remainingAtPortableStart > TimeSpan.FromSeconds(25) &&
            remainingAtPortableStart <= TimeSpan.FromSeconds(30),
            $"The Functional deadline was not created at the portable test boundary: {remainingAtPortableStart}.");

        foreach (JsonElement deadlineCheck in deadlineChecks)
        {
            Assert.AreEqual(executionDeadlineUtc, GetProperty(deadlineCheck, "DeadlineUtc").GetDateTime());
        }

        JsonElement[] fanoutStarts = events
            .EnumerateArray()
            .Where(item => GetProperty(item, "Name").GetString()!.StartsWith("fanout-start-", StringComparison.Ordinal))
            .ToArray();
        Assert.AreEqual(5, fanoutStarts.Length);
        CollectionAssert.AreEqual(
            new[]
            {
                "fanout-start-bass-collectible",
                "fanout-start-serial-state-a",
                "fanout-start-serial-state-b",
                "fanout-start-remaining-bms-library",
                "fanout-start-remaining"
            },
            fanoutStarts.Select(item => GetProperty(item, "Name").GetString()).ToArray());
        JsonElement portableExit = events[5];
        Assert.IsTrue(
            fanoutStarts.All(item => GetProperty(item, "TimestampUtc").GetDateTime() >=
                GetProperty(portableExit, "TimestampUtc").GetDateTime()),
            "A fanout host started before portable process exit was observed.");

        JsonElement executionStop = events[15];
        Assert.IsTrue(GetProperty(executionStop, "AllExited").GetBoolean());
        Assert.IsTrue(GetProperty(executionStop, "ExecutionStopped").GetBoolean());
        JsonElement cleanup = events[16];
        JsonElement cleanupComplete = events[17];
        Assert.IsTrue(GetProperty(cleanup, "AllExited").GetBoolean());
        Assert.IsTrue(GetProperty(cleanupComplete, "AllExited").GetBoolean());
        Assert.AreEqual(executionDeadlineUtc.AddSeconds(10), GetProperty(cleanup, "DeadlineUtc").GetDateTime());
        JsonElement repositoryWhitespace = events[18];
        Assert.IsTrue(
            GetProperty(repositoryWhitespace, "TimestampUtc").GetDateTime() >=
            GetProperty(cleanupComplete, "TimestampUtc").GetDateTime(),
            "Repository postflight ran before Functional process cleanup completed.");
        Assert.IsTrue(GetProperty(repositoryWhitespace, "DeadlineUtc").ValueKind is JsonValueKind.Null);
    }

    [TestMethod]
    public void CanonicalFunctional_PortableNonzeroSuppressesFanoutAndCleansRawOwnership()
    {
        using JsonDocument result = ReadFunctionalRawFailureProbe("portable-nonzero");

        AssertRawFailureProbe(
            result,
            expectedFailure: "portable-settings",
            expectedFanoutAttempts: 0,
            expectedSuccessfulStarts: 1,
            expectedRawRecords: 1,
            expectedEntriesBeforeConvert: 1,
            expectedEntriesAfterConvert: 1);
        Assert.IsTrue(
            GetProperty(result.RootElement, "ExceptionMessage").GetString()!.Contains("exit code", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void CanonicalFunctional_PortablePostStartExceptionSuppressesFanoutAndCleansRawOwnership()
    {
        using JsonDocument result = ReadFunctionalRawFailureProbe("portable-post-start-exception");

        AssertRawFailureProbe(
            result,
            expectedFailure: "portable-settings",
            expectedFanoutAttempts: 0,
            expectedSuccessfulStarts: 0,
            expectedRawRecords: 1,
            expectedEntriesBeforeConvert: 0,
            expectedEntriesAfterConvert: 1);
        Assert.IsTrue(
            GetProperty(result.RootElement, "ExceptionMessage").GetString()!.Contains("post-start fault injection", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void CanonicalFunctional_PartialFanoutPostStartExceptionPreservesPrimaryAndCleansAllOwnedProcesses()
    {
        using JsonDocument result = ReadFunctionalRawFailureProbe("partial-fanout-post-start-exception");

        AssertRawFailureProbe(
            result,
            expectedFailure: "serial-state-a",
            expectedFanoutAttempts: 2,
            expectedSuccessfulStarts: 2,
            expectedRawRecords: 3,
            expectedEntriesBeforeConvert: 2,
            expectedEntriesAfterConvert: 3);
        Assert.IsTrue(
            GetProperty(result.RootElement, "ExceptionMessage").GetString()!.Contains("post-start fault injection", StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(
            GetProperty(result.RootElement, "ExceptionMessage").GetString()!.Contains("serial-state-a", StringComparison.Ordinal));
    }

    [TestMethod]
    public void CanonicalFunctional_UsesRetainedExitTimeAcrossFanoutDeadlineBoundary()
    {
        using JsonDocument beforeDeadline = ReadFunctionalDeadlineBoundaryProbe("before");
        using JsonDocument afterDeadline = ReadFunctionalDeadlineBoundaryProbe("after");

        Assert.AreEqual(5, GetProperty(beforeDeadline.RootElement, "FanoutAttempts").GetInt32());
        Assert.IsFalse(GetProperty(beforeDeadline.RootElement, "TimedOut").GetBoolean());
        Assert.IsTrue(GetProperty(beforeDeadline.RootElement, "ObservationAfterDeadline").GetBoolean());
        Assert.AreEqual(
            2.0,
            ReadReportedElapsedSeconds(beforeDeadline.RootElement),
            0.05,
            GetProperty(beforeDeadline.RootElement, "ElapsedLine").GetString());
        Assert.AreEqual(5, GetProperty(afterDeadline.RootElement, "FanoutAttempts").GetInt32());
        Assert.IsTrue(GetProperty(afterDeadline.RootElement, "TimedOut").GetBoolean());
        Assert.IsTrue(GetProperty(afterDeadline.RootElement, "ObservationAfterDeadline").GetBoolean());
        Assert.AreEqual(
            10.0,
            ReadReportedElapsedSeconds(afterDeadline.RootElement),
            0.05,
            GetProperty(afterDeadline.RootElement, "ElapsedLine").GetString());
    }

    [TestMethod]
    public void FunctionalShardPlan_UsesExecutablePlanForExactHostOwnership()
    {
        using JsonDocument plan = ReadFunctionalShardPlan();
        JsonElement shards = GetProperty(plan.RootElement, "Shards");

        Assert.AreEqual(6, shards.GetArrayLength());
        CollectionAssert.AreEqual(
            new[]
            {
                "portable-settings",
                "bass-collectible",
                "serial-state-a",
                "serial-state-b",
                "remaining-bms-library",
                "remaining"
            },
            ReadShardNames(shards));

        AssertShard(
            FindShard(shards, "portable-settings"),
            1,
            "ClassLevel",
            new[] { "BeMusicSeeker.Tests.PlayerPanelStateSettingsCompatibilityTests" });
        AssertShard(
            FindShard(shards, "bass-collectible"),
            1,
            "ClassLevel",
            new[] { "BeMusicSeeker.Tests.BassCollectibleLoadContextTests" });
        AssertShard(
            FindShard(shards, "serial-state-a"),
            1,
            "ClassLevel",
            new[]
            {
                "BeMusicSeeker.Tests.SettingsForegroundInteractionTests",
                "BeMusicSeeker.Tests.SettingDialogEditCompletionTests",
                "BeMusicSeeker.Tests.SettingsWindowPresentationTests",
                "BeMusicSeeker.Tests.ApplicationCompositionTests",
                "BeMusicSeeker.Tests.ApplicationSettingsLifecycleTests",
                "BeMusicSeeker.Tests.ApplicationUiSchedulerBoundaryTests",
                "BeMusicSeeker.Tests.BeatorajaBmtOptionsSnapshotTests",
                "BeMusicSeeker.Tests.BmsLibraryOptionsSnapshotTests",
                "BeMusicSeeker.Tests.CustomFolderOutputSettingsSnapshotTests",
                "BeMusicSeeker.Tests.MainWindowViewSettingsBoundaryTests",
                "BeMusicSeeker.Tests.PlayerSettingsGatewayTests",
                "BeMusicSeeker.Tests.PlaylistUrlCompletionOptionsSnapshotTests",
                "BeMusicSeeker.Tests.ResourceIconContractTests",
                "BeMusicSeeker.Tests.SettingDialogCustomFolderOutputBaseTests",
                "BeMusicSeeker.Tests.SettingDialogOpenCommandTests",
                "BeMusicSeeker.Tests.ShellShutdownWorkflowOwnerTests",
                "BeMusicSeeker.Tests.StartupSettingsSnapshotTests",
                "BeMusicSeeker.Tests.BmsPlaylistExternalReloadTests",
                "BeMusicSeeker.Tests.BmsPlaylistCustomFolderOutputTests",
                "BeMusicSeeker.Tests.BmsPlaylistPersistenceLifecycleTests",
                "BeMusicSeeker.Tests.BmsPlaylistMigrationAndRegistrationTests",
                "BeMusicSeeker.Tests.BassNativeRuntimeTests",
                "BeMusicSeeker.Tests.NLogWrapperTests"
            });
        AssertShard(
            FindShard(shards, "serial-state-b"),
            1,
            "ClassLevel",
            new[]
            {
                "BeMusicSeeker.Tests.BmsLibraryLr2SongDbSyncTests",
                "BeMusicSeeker.Tests.LoadPlaylistURIDialogTests",
                "BeMusicSeeker.Tests.MainWindowChartPresentationWpfTests",
                "BeMusicSeeker.Tests.MainWindowPackageMaintenanceWpfTests",
                "BeMusicSeeker.Tests.MainWindowPlaybackWpfTests",
                "BeMusicSeeker.Tests.MainWindowPlayHistoryWpfTests",
                "BeMusicSeeker.Tests.MainWindowPlaylistWorkspaceWpfTests",
                "BeMusicSeeker.Tests.MainWindowProgressStatusBarWpfTests",
                "BeMusicSeeker.Tests.MainWindowSelectedChartContextMenuWpfTests",
                "BeMusicSeeker.Tests.MainWindowTreePresentationWpfTests",
                "BeMusicSeeker.Tests.MainWindowViewHostTests",
                "BeMusicSeeker.Tests.SettingsWindowCompiledBehaviorTests",
                "BeMusicSeeker.Tests.UiDialogCoordinatorWpfTests",
                "BeMusicSeeker.Tests.PlaybackPanelViewModelTests",
                "BeMusicSeeker.Tests.InstalledOnlyResourceOverwriteValidationTests",
                "BeMusicSeeker.Tests.LibraryFileScanPipelineOwnerTests",
                "BeMusicSeeker.Tests.Lr2PlayHistorySchemaUiTests",
                "BeMusicSeeker.Tests.MainWindowExternalShellTests",
                "BeMusicSeeker.Tests.PlayHistoryReadModelTests",
                "BeMusicSeeker.Tests.ApplicationStartupCompositionOwnerTests"
            });

        int remainingWorkers = Math.Max(1, Environment.ProcessorCount);
        AssertShard(
            FindShard(shards, "remaining-bms-library"),
            remainingWorkers,
            "ClassLevel",
            Array.Empty<string>());
        AssertShard(
            FindShard(shards, "remaining"),
            remainingWorkers,
            "ClassLevel",
            Array.Empty<string>());

        JsonElement remainingBmsLibrary = FindShard(shards, "remaining-bms-library");
        JsonElement remaining = FindShard(shards, "remaining");
        string[] expectedExclusions =
        [
            "BeMusicSeeker.Tests.PlayerPanelStateSettingsCompatibilityTests",
            "BeMusicSeeker.Tests.BassCollectibleLoadContextTests",
            "BeMusicSeeker.Tests.SettingsForegroundInteractionTests",
            "BeMusicSeeker.Tests.SettingDialogEditCompletionTests",
            "BeMusicSeeker.Tests.SettingsWindowPresentationTests",
            "BeMusicSeeker.Tests.ApplicationCompositionTests",
            "BeMusicSeeker.Tests.ApplicationSettingsLifecycleTests",
            "BeMusicSeeker.Tests.ApplicationUiSchedulerBoundaryTests",
            "BeMusicSeeker.Tests.BeatorajaBmtOptionsSnapshotTests",
            "BeMusicSeeker.Tests.BmsLibraryOptionsSnapshotTests",
            "BeMusicSeeker.Tests.CustomFolderOutputSettingsSnapshotTests",
            "BeMusicSeeker.Tests.MainWindowViewSettingsBoundaryTests",
            "BeMusicSeeker.Tests.PlayerSettingsGatewayTests",
            "BeMusicSeeker.Tests.PlaylistUrlCompletionOptionsSnapshotTests",
            "BeMusicSeeker.Tests.ResourceIconContractTests",
            "BeMusicSeeker.Tests.SettingDialogCustomFolderOutputBaseTests",
            "BeMusicSeeker.Tests.SettingDialogOpenCommandTests",
            "BeMusicSeeker.Tests.ShellShutdownWorkflowOwnerTests",
            "BeMusicSeeker.Tests.StartupSettingsSnapshotTests",
            "BeMusicSeeker.Tests.BmsPlaylistExternalReloadTests",
            "BeMusicSeeker.Tests.BmsPlaylistCustomFolderOutputTests",
            "BeMusicSeeker.Tests.BmsPlaylistPersistenceLifecycleTests",
            "BeMusicSeeker.Tests.BmsPlaylistMigrationAndRegistrationTests",
            "BeMusicSeeker.Tests.BassNativeRuntimeTests",
            "BeMusicSeeker.Tests.NLogWrapperTests",
            "BeMusicSeeker.Tests.BmsLibraryLr2SongDbSyncTests",
            "BeMusicSeeker.Tests.LoadPlaylistURIDialogTests",
            "BeMusicSeeker.Tests.MainWindowChartPresentationWpfTests",
            "BeMusicSeeker.Tests.MainWindowPackageMaintenanceWpfTests",
            "BeMusicSeeker.Tests.MainWindowPlaybackWpfTests",
            "BeMusicSeeker.Tests.MainWindowPlayHistoryWpfTests",
            "BeMusicSeeker.Tests.MainWindowPlaylistWorkspaceWpfTests",
            "BeMusicSeeker.Tests.MainWindowProgressStatusBarWpfTests",
            "BeMusicSeeker.Tests.MainWindowSelectedChartContextMenuWpfTests",
            "BeMusicSeeker.Tests.MainWindowTreePresentationWpfTests",
            "BeMusicSeeker.Tests.MainWindowViewHostTests",
            "BeMusicSeeker.Tests.SettingsWindowCompiledBehaviorTests",
            "BeMusicSeeker.Tests.UiDialogCoordinatorWpfTests",
            "BeMusicSeeker.Tests.PlaybackPanelViewModelTests",
            "BeMusicSeeker.Tests.InstalledOnlyResourceOverwriteValidationTests",
            "BeMusicSeeker.Tests.LibraryFileScanPipelineOwnerTests",
            "BeMusicSeeker.Tests.Lr2PlayHistorySchemaUiTests",
            "BeMusicSeeker.Tests.MainWindowExternalShellTests",
            "BeMusicSeeker.Tests.PlayHistoryReadModelTests",
            "BeMusicSeeker.Tests.ApplicationStartupCompositionOwnerTests"
        ];
        Assert.AreEqual(45, expectedExclusions.Length);
        AssertRemainingBmsLibraryPartition(remainingBmsLibrary, remaining, expectedExclusions);
        CollectionAssert.AreEqual(
            expectedExclusions,
            ReadStringArray(GetProperty(remaining, "ExcludedClasses")));

        AssertForegroundInteractionContract(plan.RootElement, shards);
        AssertRetiredFixtureSelectorsAbsent(plan.RootElement);
        Assert.IsFalse(plan.RootElement.TryGetProperty("FanoutShards", out _));
        Assert.IsFalse(plan.RootElement.TryGetProperty("FanoutLaunchShards", out _));
        Assert.IsFalse(plan.RootElement.TryGetProperty("EarlyShards", out _));
    }

    [TestMethod]
    public void FullContract_PreservesCanonicalOrderAndExplicitPhaseBudgets()
    {
        using JsonDocument contract = ReadRunnerContract();
        JsonElement full = GetProperty(contract.RootElement, "Full");

        CollectionAssert.AreEqual(
            new[]
            {
                "tool-restore",
                "canonical-functional",
                "tool-smoke",
                "current-distribution-publish",
                "baseline-preparation",
                "existing-data",
                "update",
                "ProcessIntegration",
                "ReleaseAcceptance",
                "format",
                "analyzer"
            },
            ReadStringArray(GetProperty(full, "OrderedOperations")));

        var expectedBudgets = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["tool-restore"] = 120,
            ["tool-smoke"] = 60,
            ["current-distribution-publish"] = 180,
            ["baseline-preparation"] = 300,
            ["existing-data"] = 180,
            ["update"] = 240,
            ["ProcessIntegration"] = 180,
            ["ReleaseAcceptance"] = 180,
            ["format"] = 120,
            ["analyzer"] = 180
        };

        JsonElement descriptors = GetProperty(full, "PhaseDescriptors");
        Assert.AreEqual(expectedBudgets.Count, descriptors.GetArrayLength());
        foreach (JsonElement descriptor in descriptors.EnumerateArray())
        {
            string name = GetProperty(descriptor, "Name").GetString()!;
            Assert.IsTrue(expectedBudgets.TryGetValue(name, out int budget), $"Unexpected Full phase: {name}");
            Assert.AreEqual(budget, GetProperty(descriptor, "BudgetSeconds").GetInt32());
            Assert.IsFalse(string.IsNullOrWhiteSpace(GetProperty(descriptor, "DiagnosticsSegment").GetString()));
            Assert.IsTrue(GetProperty(descriptor, "Monitored").GetBoolean());
            Assert.AreEqual(
                "primary-failure-preserved;cleanup-failure-diagnostic;success-cleanup-failure",
                GetProperty(descriptor, "FailureContract").GetString());
        }

        Assert.AreEqual(0, GetProperty(full, "IndependentFunctionalRoutes").GetArrayLength());
        Assert.AreEqual(1, GetProperty(full, "CanonicalFunctionalInvocationCount").GetInt32());
        Assert.AreEqual("run-root/{restore,build,functional}", GetProperty(full, "CanonicalFunctionalDiagnosticsLayout").GetString());
        Assert.AreEqual("current-distribution-publish", GetProperty(full, "PostFunctionalStartsWith").GetString());
        Assert.IsTrue(GetProperty(GetProperty(contract.RootElement, "ModeMappings"), 2, "CanonicalFunctionalBeforePostFunctionalPhases").GetBoolean());

        JsonElement repositoryFormat = GetProperty(full, "RepositoryFormat");
        Assert.AreEqual("folder", GetProperty(repositoryFormat, "WorkspaceKind").GetString());
        Assert.AreEqual("none", GetProperty(repositoryFormat, "ProjectEvaluation").GetString());
        Assert.IsTrue(GetProperty(repositoryFormat, "VerifiesAllGenuineWorkspaceFiles").GetBoolean());
        CollectionAssert.AreEqual(
            new[] { "artifacts/verification", "bin", "obj" },
            ReadStringArray(GetProperty(repositoryFormat, "GeneratedRootExclusions")));

        JsonElement baselinePreparation = GetProperty(full, "BaselinePreparation");
        Assert.AreEqual("external-temp-root", GetProperty(baselinePreparation, "ExpandedSourceRoot").GetString());
        Assert.AreEqual("package-and-manifest-only", GetProperty(baselinePreparation, "RunRootContents").GetString());
        Assert.AreEqual("finally", GetProperty(baselinePreparation, "Cleanup").GetString());

        JsonElement artifact = GetProperty(full, "DistributionArtifact");
        Assert.AreEqual(1, GetProperty(artifact, "ManifestSchemaVersion").GetInt32());
        Assert.AreEqual("mandatory", GetProperty(artifact, "ManifestMode").GetString());
        Assert.AreEqual("run-root/distribution", GetProperty(artifact, "ArtifactRoot").GetString());
        Assert.AreEqual("exact-version", GetProperty(artifact, "Selection").GetString());
        Assert.IsTrue(GetProperty(artifact, "NoLatestScan").GetBoolean());
        Assert.IsTrue(GetProperty(artifact, "NoRepublishByConsumers").GetBoolean());
        CollectionAssert.AreEqual(
            new[] { "existing-data", "update", "ProcessIntegration", "ReleaseAcceptance" },
            ReadStringArray(GetProperty(artifact, "Consumers")));

        JsonElement identity = GetProperty(artifact, "CreationIdentity");
        Assert.AreEqual("baseline-preparation", GetProperty(identity, "Capture").GetString());
        CollectionAssert.AreEqual(
            new[] { "runId", "artifactId", "manifestSha256", "manifestSeal" },
            ReadStringArray(GetProperty(identity, "ExpectedFields")));
        Assert.IsTrue(GetProperty(identity, "ExactMatch").GetBoolean());
        Assert.AreEqual(
            "before-and-after-every-consumer-and-final",
            GetProperty(identity, "Revalidation").GetString());

        JsonElement v216 = GetProperty(contract.RootElement, "V216FirstHop");
        CollectionAssert.AreEqual(
            new[] { "UPD-V216-HAPPY", "UPD-V216-LOCK", "REL-V216-ID" },
            ReadStringArray(GetProperty(v216, "ContractIds")));
        Assert.AreEqual("devdocs/acceptance/v216-first-hop/artifact.json", GetProperty(v216, "ArtifactMetadataPath").GetString());
        Assert.AreEqual("scripts/accept-v216-first-hop.ps1", GetProperty(v216, "AcceptanceScript").GetString());
        Assert.AreEqual("2.1.6.0", GetProperty(v216, "ArtifactVersion").GetString());
        Assert.AreEqual(11260709, GetProperty(v216, "ArtifactSizeBytes").GetInt64());
        Assert.AreEqual(
            "C2C460B6757478816912A59FEA535209B2A960528C8996FFE12225EC7CED7BB2",
            GetProperty(v216, "ArtifactSha256").GetString());
        Assert.AreEqual("checked-in-single-path", GetProperty(v216, "ArtifactSelection").GetString());
        Assert.AreEqual("fail-closed", GetProperty(v216, "MissingOrMismatch").GetString());
        Assert.IsTrue(GetProperty(v216, "NoFallback").GetBoolean());
        Assert.AreEqual("protocol-1-legacy-arguments-and-close", GetProperty(v216, "LegacyUpdaterProtocol").GetString());
        Assert.AreEqual("bounded-exit-and-full-stream-pid-drain", GetProperty(v216, "StartupCompletion").GetString());
        Assert.AreEqual("ab9d97ed3f53dab80fb2894f20f44abdfb6fed32", GetProperty(v216, "CurrentUpdaterBaseline").GetString());

        JsonElement outcomeGate = GetProperty(contract.RootElement, "ReleaseOutcomeGate");
        Assert.AreEqual("Assert-VerificationTestOutcomes", GetProperty(outcomeGate, "Owner").GetString());
        CollectionAssert.AreEqual(
            new[] { "REL-CRITICAL-ROSTER", "REL-OPTIONAL-SKIP" },
            ReadStringArray(GetProperty(outcomeGate, "ContractIds")));
        Assert.AreEqual("devdocs/acceptance/v216-first-hop/artifact.json", GetProperty(outcomeGate, "RosterPath").GetString());
        Assert.AreEqual("exactly-once", GetProperty(outcomeGate, "RequiredFqnCardinality").GetString());
        Assert.AreEqual("Passed", GetProperty(outcomeGate, "RequiredOutcome").GetString());
        Assert.AreEqual("exact-fqn-allowlist-with-non-empty-reason", GetProperty(outcomeGate, "OptionalSkip").GetString());
        CollectionAssert.AreEqual(
            new[] { "Functional", "ProcessIntegration", "ReleaseAcceptance" },
            ReadStringArray(GetProperty(outcomeGate, "Inputs")));
        Assert.AreEqual("release-outcomes.json", GetProperty(outcomeGate, "ReceiptFileName").GetString());

        JsonElement receiptInputs = GetProperty(outcomeGate, "ReceiptInputs");
        Assert.AreEqual(1, receiptInputs.GetArrayLength());
        Assert.AreEqual("V216FirstHopAcceptance", GetProperty(receiptInputs, 0, "Name").GetString());
        Assert.AreEqual(
            "release-acceptance/v216-first-hop/v216-first-hop-acceptance.json",
            GetProperty(receiptInputs, 0, "RelativePath").GetString());
        Assert.AreEqual("json", GetProperty(receiptInputs, 0, "Format").GetString());

        JsonElement acceptanceReceipt = GetProperty(v216, "AcceptanceReceipt");
        Assert.AreEqual(
            "release-acceptance/v216-first-hop/v216-first-hop-acceptance.json",
            GetProperty(acceptanceReceipt, "RelativePath").GetString());
        Assert.AreEqual("json", GetProperty(acceptanceReceipt, "Format").GetString());
        Assert.AreEqual("exactly-once", GetProperty(acceptanceReceipt, "RequiredCardinality").GetString());
        Assert.AreEqual("Passed", GetProperty(acceptanceReceipt, "RequiredOutcome").GetString());
        Assert.AreEqual(
            GetProperty(acceptanceReceipt, "RelativePath").GetString(),
            GetProperty(receiptInputs, 0, "RelativePath").GetString());
        JsonElement requiredResults = GetProperty(acceptanceReceipt, "RequiredResults");
        Assert.AreEqual(2, requiredResults.GetArrayLength());
        AssertV216ResultContract(requiredResults[0], "UPD-V216-HAPPY", V216HappyPathResultName);
        AssertV216ResultContract(requiredResults[1], "UPD-V216-LOCK", V216ManagedFileLockResultName);
    }

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
            JsonDocument missing = ReadOutcomeGateProbe(emptyResults, requiredFqn);
            AssertGateFailure(missing, "missing");

            string duplicateResults = Path.Combine(root, "duplicate.trx");
            WriteSyntheticTrx(duplicateResults, (requiredFqn, "Passed"), (requiredFqn, "Passed"));
            JsonDocument duplicate = ReadOutcomeGateProbe(duplicateResults, requiredFqn);
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
    public void ReleaseOutcomeGateLoadsRequiredRosterFromMetadataPath()
    {
        const string requiredFqn = "BeMusicSeeker.Tests.Synthetic.MetadataRoster.ReleaseOutcome";
        string root = Path.Combine(
            Path.GetTempPath(),
            "BeMusicSeeker-VerificationMetadataRosterContractTests",
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

    private static void AssertV216ResultContract(
        JsonElement result,
        string expectedContractId,
        string expectedFullyQualifiedName)
    {
        Assert.AreEqual(expectedContractId, GetProperty(result, "ContractId").GetString());
        Assert.AreEqual(expectedFullyQualifiedName, GetProperty(result, "FullyQualifiedName").GetString());
    }

    private static void AssertModeMapping(
        JsonElement mappings,
        string name,
        string mode,
        string filterState,
        int canonicalInvocationCount,
        string route)
    {
        JsonElement mapping = default;
        bool found = false;
        foreach (JsonElement candidate in mappings.EnumerateArray())
        {
            if (GetProperty(candidate, "Name").GetString() == name)
            {
                mapping = candidate;
                found = true;
                break;
            }
        }

        Assert.IsTrue(found, $"Mode mapping was not found: {name}");
        Assert.AreEqual(mode, GetProperty(mapping, "Mode").GetString());
        Assert.AreEqual(filterState, GetProperty(mapping, "FilterState").GetString());
        Assert.AreEqual(canonicalInvocationCount, GetProperty(mapping, "CanonicalFunctionalInvocationCount").GetInt32());
        Assert.AreEqual("Invoke-CanonicalFunctionalVerification", GetProperty(mapping, "CanonicalFunctionalOwner").GetString());
        Assert.AreEqual("FunctionalTimeoutSeconds", GetProperty(mapping, "TimeoutArgument").GetString());
        Assert.AreEqual("caller-owned", GetProperty(mapping, "DiagnosticsRootOwnership").GetString());
        Assert.AreEqual(route, GetProperty(mapping, "Route").GetString());
    }

    private static void AssertShard(JsonElement shard, int workers, string scope, string[] classes)
    {
        Assert.AreEqual(workers, GetProperty(shard, "Workers").GetInt32());
        Assert.AreEqual(scope, GetProperty(shard, "Scope").GetString());
        CollectionAssert.AreEqual(classes, ReadStringArray(GetProperty(shard, "Classes")));
        Assert.IsFalse(string.IsNullOrWhiteSpace(GetProperty(shard, "Filter").GetString()));
    }

    private static void AssertForegroundInteractionContract(JsonElement planRoot, JsonElement shards)
    {
        CollectionAssert.AreEqual(
            new[]
            {
                "BeMusicSeeker.Tests.SettingsForegroundInteractionTests.SettingsWindow_NavigationSupportsKeyboardAutomationAndResetsPageScroll",
                "BeMusicSeeker.Tests.SettingsForegroundInteractionTests.SettingsComboBox_HitTestingPreservesWholeSurfaceAndEditableTextRoutes",
                "BeMusicSeeker.Tests.SettingsForegroundInteractionTests.SettingsControlDictionary_OverridesOuterImplicitStylesAndMaterializesClosedRoutes",
                "BeMusicSeeker.Tests.SettingsForegroundInteractionTests.SettingsWindow_ManualResyncClosesAndQueuesForcedWorkflow",
                "BeMusicSeeker.Tests.SettingsForegroundInteractionTests.Lr2AdvancedPathsDialog_EnterCommitsFocusedEditorBeforeAccepting",
                "BeMusicSeeker.Tests.SettingsForegroundInteractionTests.Lr2AdvancedPathsDialog_EnterKeepsDialogOpenWhenFocusedCandidateIsRejected",
                "BeMusicSeeker.Tests.SettingsForegroundInteractionTests.Lr2AdvancedPathsDialog_InitialInvalidTupleStaysOpenAndFocusesRejectedEditor"
            },
            ReadStringArray(GetProperty(planRoot, "ForegroundInteractionMethods")));

        JsonElement serialStateA = FindShard(shards, "serial-state-a");
        const string foregroundClass = "BeMusicSeeker.Tests.SettingsForegroundInteractionTests";
        Assert.IsTrue(ReadStringArray(GetProperty(serialStateA, "Classes")).Contains(foregroundClass));
        foreach (JsonElement shard in shards.EnumerateArray())
        {
            string name = GetProperty(shard, "Name").GetString()!;
            string[] classes = ReadStringArray(GetProperty(shard, "Classes"));
            if (!string.Equals(name, "serial-state-a", StringComparison.Ordinal))
            {
                Assert.IsFalse(classes.Contains(foregroundClass), $"Foreground fixture leaked into host: {name}");
            }
        }

        foreach (string method in ReadStringArray(GetProperty(planRoot, "ForegroundInteractionMethods")))
        {
            Assert.IsTrue(
                method.StartsWith(foregroundClass + ".", StringComparison.Ordinal),
                $"Foreground method is owned by another fixture: {method}");
            Assert.IsFalse(
                method.Contains("SettingDialogEditCompletionTests", StringComparison.Ordinal),
                $"Retired foreground owner remains in the allowlist: {method}");
        }
    }

    private static void AssertRemainingBmsLibraryPartition(
        JsonElement positive,
        JsonElement negative,
        string[] expectedExclusions)
    {
        const string selector = "FullyQualifiedName~BeMusicSeeker.Tests.BmsLibrary";
        string positiveBaseFilter = GetProperty(positive, "BaseFilter").GetString()!;
        string negativeBaseFilter = GetProperty(negative, "BaseFilter").GetString()!;
        Assert.AreEqual(positiveBaseFilter, negativeBaseFilter);
        Assert.AreEqual(selector, GetProperty(positive, "Selector").GetString());
        Assert.AreEqual(selector, GetProperty(negative, "Selector").GetString());
        Assert.AreEqual("Positive", GetProperty(positive, "SelectorPolarity").GetString());
        Assert.AreEqual("Negative", GetProperty(negative, "SelectorPolarity").GetString());
        Assert.AreEqual("logical-prefix", GetProperty(positive, "Routing").GetString());
        Assert.AreEqual("logical-prefix", GetProperty(negative, "Routing").GetString());
        Assert.AreEqual(selector, GetProperty(positive, "SelectorFilter").GetString());
        string negativeSelector = selector.Replace("~", "!~", StringComparison.Ordinal);
        Assert.AreEqual(negativeSelector, GetProperty(negative, "SelectorFilter").GetString());
        Assert.AreNotEqual(
            GetProperty(positive, "SelectorFilter").GetString(),
            GetProperty(negative, "SelectorFilter").GetString());

        Assert.AreEqual(
            $"({positiveBaseFilter})&({GetProperty(positive, "SelectorFilter").GetString()})",
            GetProperty(positive, "Filter").GetString());
        Assert.AreEqual(
            $"({positiveBaseFilter})&({GetProperty(negative, "SelectorFilter").GetString()})",
            GetProperty(negative, "Filter").GetString());
        CollectionAssert.AreEqual(
            expectedExclusions,
            ReadStringArray(GetProperty(positive, "ExcludedClasses")));
        CollectionAssert.AreEqual(
            expectedExclusions,
            ReadStringArray(GetProperty(negative, "ExcludedClasses")));
        foreach (string exclusion in expectedExclusions)
        {
            string exclusionPredicate = $"FullyQualifiedName!~{exclusion}";
            Assert.IsTrue(positiveBaseFilter.Contains(exclusionPredicate, StringComparison.Ordinal));
            Assert.IsTrue(negativeBaseFilter.Contains(exclusionPredicate, StringComparison.Ordinal));
        }
        Assert.AreEqual(0, GetProperty(positive, "Classes").GetArrayLength());
        Assert.AreEqual(0, GetProperty(negative, "Classes").GetArrayLength());
    }

    private static void AssertRetiredFixtureSelectorsAbsent(JsonElement planRoot)
    {
        string serializedPlan = planRoot.GetRawText();
        foreach (string retiredSelector in new[]
        {
            "BeMusicSeeker.Tests.BmsPlaylistUpdateTests",
            "BeMusicSeeker.Tests.PlaylistWorkspaceViewModelTests",
            "BeMusicSeeker.Tests.OwnedChartCollectionStateTests",
            "BeMusicSeeker.Tests.PlaylistSummaryAggregationTests",
            "BeMusicSeeker.Tests.RegularChartListOwnerTests",
            "BeMusicSeeker.Tests.ChartInfoMetadataTests",
            "BeMusicSeeker.Tests.ChartInfoMetadataOwnerTests",
            "BeMusicSeeker.Tests.BmsLibraryInitializationServiceTests",
            "BeMusicSeeker.Tests.StartupLibraryConstructionOwnerTests",
            "BeMusicSeeker.Tests.SettingsWindowPresentationTests.SettingsWindow_ManualResyncClosesAndQueuesForcedWorkflow",
            "BeMusicSeeker.Tests.SettingDialogEditCompletionTests.Lr2AdvancedPathsDialog_EnterCommitsFocusedEditorBeforeAccepting",
            "BeMusicSeeker.Tests.SettingDialogEditCompletionTests.Lr2AdvancedPathsDialog_EnterKeepsDialogOpenWhenFocusedCandidateIsRejected",
            "BeMusicSeeker.Tests.SettingDialogEditCompletionTests.Lr2AdvancedPathsDialog_InitialInvalidTupleStaysOpenAndFocusesRejectedEditor"
        })
        {
            Assert.IsFalse(
                serializedPlan.Contains(retiredSelector, StringComparison.Ordinal),
                $"Retired fixture selector remains in the actual launch plan: {retiredSelector}");
        }
    }

    private static JsonElement FindShard(JsonElement shards, string name)
    {
        foreach (JsonElement shard in shards.EnumerateArray())
        {
            if (GetProperty(shard, "Name").GetString() == name)
            {
                return shard;
            }
        }

        Assert.Fail($"Functional shard was not found: {name}");
        return default;
    }

    private static string[] ReadShardNames(JsonElement shards)
    {
        return shards.EnumerateArray()
            .Select(shard => GetProperty(shard, "Name").GetString()!)
            .ToArray();
    }

    private static JsonDocument ReadRunnerContract()
    {
        string scriptPath = Path.Combine(FindRepositoryRoot(), "scripts", "verification-runner-contract.ps1");
        return ReadPowerShellJson(new[] { "-File", scriptPath });
    }

    private static JsonDocument ReadFunctionalTimeoutValidationProbe()
    {
        string repositoryRoot = FindRepositoryRoot();
        string lifecyclePath = QuotePowerShellLiteral(
            Path.Combine(repositoryRoot, "scripts", "verification-process-lifecycle.ps1"));
        string verifyScriptPath = QuotePowerShellLiteral(
            Path.Combine(repositoryRoot, "scripts", "verify-refactor.ps1"));
        string command = string.Join(
            Environment.NewLine,
            "$ErrorActionPreference = 'Stop'",
            ". " + lifecyclePath,
            "$signal = [System.Threading.ManualResetEventSlim]::new($false)",
            "$guard = [VerificationPostStartFaultGuard]::new($signal)",
            ". " + verifyScriptPath + " -InternalTestGuard $guard",
            "$defaultTimeoutSeconds = $FunctionalTimeoutSeconds",
            "$acceptedTimeoutSeconds = [System.Collections.Generic.List[int]]::new()",
            "$rejectedTimeoutSeconds = [System.Collections.Generic.List[int]]::new()",
            // Each guarded script invocation still runs PowerShell parameter binding before
            // the probe-only early return, so these values exercise the executable CLI seam
            // without entering restore, build, or test execution.
            "foreach ($candidate in @(1, 300, 301)) { try { & " + verifyScriptPath + " -FunctionalTimeoutSeconds $candidate -InternalTestGuard $guard *> $null; [void]$acceptedTimeoutSeconds.Add($candidate) } catch { [void]$rejectedTimeoutSeconds.Add($candidate) } }",
            "$internalFunctionRejected301 = $false",
            "try { New-FunctionalDeadlinePolicy -StartUtc ([DateTime]::UtcNow) -TimeoutSeconds 301 | Out-Null } catch { $internalFunctionRejected301 = $true }",
            "[pscustomobject]@{ DefaultTimeoutSeconds = $defaultTimeoutSeconds; AcceptedTimeoutSeconds = @($acceptedTimeoutSeconds); RejectedTimeoutSeconds = @($rejectedTimeoutSeconds); InternalFunctionRejected301 = $internalFunctionRejected301 } | ConvertTo-Json -Depth 8 -Compress");
        return ReadPowerShellJson(new[] { "-Command", command });
    }

    private static JsonDocument ReadFunctionalShardPlan()
    {
        // The typed guard loads the actual runner definitions without entering a normal
        // verification route. Serialize the single executable plan object returned by
        // New-FunctionalShardPlan; do not maintain a metadata-only copy in this test.
        string repositoryRoot = FindRepositoryRoot();
        string lifecyclePath = QuotePowerShellLiteral(
            Path.Combine(repositoryRoot, "scripts", "verification-process-lifecycle.ps1"));
        string verifyScriptPath = QuotePowerShellLiteral(
            Path.Combine(repositoryRoot, "scripts", "verify-refactor.ps1"));
        string command = string.Join(
            Environment.NewLine,
            "$ErrorActionPreference = 'Stop'",
            $". {lifecyclePath}",
            "$signal = [System.Threading.ManualResetEventSlim]::new($true)",
            "$guard = [VerificationPostStartFaultGuard]::new($signal)",
            $". {verifyScriptPath} -InternalTestGuard $guard",
            "$plan = New-FunctionalShardPlan",
            "Assert-FunctionalShardConfiguration -Plan $plan",
            "$plan | ConvertTo-Json -Depth 16 -Compress");
        return ReadPowerShellJson(new[] { "-Command", command });
    }

    private static JsonDocument ReadFunctionalDeadlinePolicy()
    {
        string repositoryRoot = FindRepositoryRoot();
        string lifecyclePath = QuotePowerShellLiteral(
            Path.Combine(repositoryRoot, "scripts", "verification-process-lifecycle.ps1"));
        string verifyScriptPath = QuotePowerShellLiteral(
            Path.Combine(repositoryRoot, "scripts", "verify-refactor.ps1"));
        string command = string.Join(
            Environment.NewLine,
            "$ErrorActionPreference = 'Stop'",
            $". {lifecyclePath}",
            "$signal = [System.Threading.ManualResetEventSlim]::new($true)",
            "$guard = [VerificationPostStartFaultGuard]::new($signal)",
            $". {verifyScriptPath} -InternalTestGuard $guard",
            "$startUtc = [DateTime]::new(2026, 8, 25, 4, 0, 0, [DateTimeKind]::Utc)",
            $"$policy = New-FunctionalDeadlinePolicy -StartUtc $startUtc -TimeoutSeconds {FunctionalHardTimeoutSeconds}",
            "$remainingAtStart = Get-RemainingBudgetSeconds -DeadlineUtc $policy.ExecutionDeadlineUtc -NowUtc $startUtc",
            "[pscustomobject]@{ Policy = $policy; RemainingAtStart = $remainingAtStart } | ConvertTo-Json -Depth 8 -Compress");
        return ReadPowerShellJson(new[] { "-Command", command });
    }

    private static JsonDocument ReadFunctionalExecutionProbe()
    {
        string repositoryRoot = FindRepositoryRoot();
        string lifecyclePath = QuotePowerShellLiteral(
            Path.Combine(repositoryRoot, "scripts", "verification-process-lifecycle.ps1"));
        string verifyScriptPath = QuotePowerShellLiteral(
            Path.Combine(repositoryRoot, "scripts", "verify-refactor.ps1"));
        string testProjectPath = QuotePowerShellLiteral(
            Path.Combine(repositoryRoot, "BeMusicSeeker.Tests", "BeMusicSeeker.Tests.csproj"));
        string command = string.Join(
            Environment.NewLine,
            "$ErrorActionPreference = 'Stop'",
            $". {lifecyclePath}",
            "$signal = [System.Threading.ManualResetEventSlim]::new($false)",
            "$guard = [VerificationPostStartFaultGuard]::new($signal)",
            $". {verifyScriptPath} -InternalTestGuard $guard",
            $"$solution = {testProjectPath}",
            "$events = [System.Collections.Generic.List[object]]::new()",
            "$global:portableEntry = $null",
            "$global:portableExitObserved = $false",
            "function Add-ProbeEvent { param([string]$Name, [object]$DeadlineUtc, [bool]$AllExited = $false, [bool]$ExecutionStopped = $false) [void]$events.Add([pscustomobject]@{ Name = $Name; TimestampUtc = [DateTime]::UtcNow; DeadlineUtc = $DeadlineUtc; AllExited = $AllExited; ExecutionStopped = $ExecutionStopped }) }",
            "function Invoke-BudgetedCommand { param([System.Diagnostics.Stopwatch]$Stopwatch, [int]$BudgetSeconds, [string]$Label, [string]$CommandPath, [string[]]$Arguments, [string]$DiagnosticsDirectory, [object]$ProcessDeadlineUtc, [object]$PhaseDeadlineUtc, [object]$CleanupDeadlineUtc, [switch]$IsTestCommand) $stageName = if ($Label -eq 'Locked restore') { 'restore' } else { 'build' }; Add-ProbeEvent -Name $stageName -DeadlineUtc $null }",
            "function Assert-BuiltOutputs { Add-ProbeEvent -Name 'built-output-validation' -DeadlineUtc $null }",
            "$actualAssert = ${function:Assert-FunctionalExecutionDeadline}",
            "function Assert-FunctionalExecutionDeadline { param([object]$DeadlinePolicy, [string]$StageName) Add-ProbeEvent -Name 'deadline-check' -DeadlineUtc $DeadlinePolicy.ExecutionDeadlineUtc; & $actualAssert @PSBoundParameters }",
            "$actualStart = ${function:Start-FunctionalShardProcess}",
            "function Start-FunctionalShardProcess { param([pscustomobject]$Shard, [string]$DiagnosticsDirectory, [System.Collections.IList]$Entries, [System.Collections.IList]$OwnedProcessRecords, [object]$PostStartFaultGuard, [string]$RunSettingsPath) if ($Shard.Name -ceq 'portable-settings') { Add-ProbeEvent -Name 'portable-start' -DeadlineUtc $null } else { if ($null -eq $global:portableEntry -or -not $global:portableEntry.Process.HasExited) { throw 'Fanout started before portable exit was observed.' }; if (-not $global:portableExitObserved) { $global:portableExitObserved = $true; Add-ProbeEvent -Name 'portable-exit-observed' -DeadlineUtc $null }; Add-ProbeEvent -Name ('fanout-start-' + $Shard.Name) -DeadlineUtc $null }; $originalFilter = $Shard.Filter; try { $Shard.Filter = 'FullyQualifiedName~BeMusicSeeker.Tests.__NoSuchFunctionalProbe'; $entry = & $actualStart @PSBoundParameters; if ($Shard.Name -ceq 'portable-settings') { $global:portableEntry = $entry }; return $entry } finally { $Shard.Filter = $originalFilter } }",
            "$actualCleanup = ${function:Invoke-VerificationFunctionalCleanup}",
            "function Invoke-VerificationFunctionalCleanup { param([System.Collections.IList]$Entries, [DateTime]$CleanupDeadlineUtc, [switch]$StopRoots, [object]$PrimitiveObserver) $allExited = @($Entries | Where-Object { -not $_.Process.HasExited }).Count -eq 0; $executionStopped = $null -ne $testExecutionStopwatch -and -not $testExecutionStopwatch.IsRunning; Add-ProbeEvent -Name 'execution-stop-observed' -DeadlineUtc $null -AllExited:$allExited -ExecutionStopped:$executionStopped; Add-ProbeEvent -Name 'cleanup-start' -DeadlineUtc $CleanupDeadlineUtc -AllExited:$allExited; $result = & $actualCleanup @PSBoundParameters; Add-ProbeEvent -Name 'cleanup-complete' -DeadlineUtc $null -AllExited:$allExited; return $result }",
            "function Assert-RepositoryWhitespace { Add-ProbeEvent -Name 'repository-whitespace' -DeadlineUtc $null }",
            "$diagnostics = Join-Path ([IO.Path]::GetTempPath()) ('bms-verification-contract-' + [Guid]::NewGuid().ToString('N'))",
            "try { Invoke-CanonicalFunctionalVerification -DiagnosticsRoot $diagnostics -TimeoutSeconds 30 *> $null } finally { if (Test-Path -LiteralPath $diagnostics) { Remove-Item -LiteralPath $diagnostics -Recurse -Force } }",
            "$events | ConvertTo-Json -Depth 8 -Compress");
        return ReadPowerShellJson(new[] { "-Command", command });
    }

    private static JsonDocument ReadFunctionalRawFailureProbe(string scenario)
    {
        string repositoryRoot = FindRepositoryRoot();
        string lifecyclePath = QuotePowerShellLiteral(
            Path.Combine(repositoryRoot, "scripts", "verification-process-lifecycle.ps1"));
        string verifyScriptPath = QuotePowerShellLiteral(
            Path.Combine(repositoryRoot, "scripts", "verify-refactor.ps1"));
        string command = string.Join(
            Environment.NewLine,
            "$ErrorActionPreference = 'Stop'",
            $"$scenario = {QuotePowerShellLiteral(scenario)}",
            ". " + lifecyclePath,
            "$signal = [System.Threading.ManualResetEventSlim]::new($scenario -eq 'portable-post-start-exception')",
            "$guard = [VerificationPostStartFaultGuard]::new($signal)",
            ". " + verifyScriptPath + " -InternalTestGuard $guard",
            "$global:probeArgument = if ($scenario -eq 'portable-nonzero') { @('test', (Join-Path $repoRoot '__missing-functional-probe__.csproj'), '--no-restore') } else { @('--version') }",
            "$global:startAttempts = [System.Collections.Generic.List[string]]::new()",
            "$global:startReturns = [System.Collections.Generic.List[string]]::new()",
            "$global:rawProcessIds = [System.Collections.Generic.List[int]]::new()",
            "$global:rawCommandIdentities = [System.Collections.Generic.List[string]]::new()",
            "$global:convertBefore = $null",
            "$global:convertAfter = $null",
            "$global:cleanupSnapshot = $null",
            "$actualStart = ${function:Start-FunctionalShardProcess}",
            "$actualConvert = ${function:Convert-FunctionalRawOwnershipRecordsToEntries}",
            "$actualCleanup = ${function:Invoke-VerificationFunctionalCleanup}",
            "function Get-TestArguments { param([string]$Filter, [string]$DiagnosticsDirectory, [string]$RunSettingsPath, [switch]$NoBuild) return @($global:probeArgument) }",
            "function Invoke-BudgetedCommand { param([System.Diagnostics.Stopwatch]$Stopwatch, [int]$BudgetSeconds, [string]$Label, [string]$CommandPath, [string[]]$Arguments, [string]$DiagnosticsDirectory, [DateTime]$ProcessDeadlineUtc, [DateTime]$PhaseDeadlineUtc, [DateTime]$CleanupDeadlineUtc, [switch]$IsTestCommand) }",
            "function Assert-BuiltOutputs { }",
            "function Assert-RepositoryWhitespace { }",
            "function Start-FunctionalShardProcess { param([pscustomobject]$Shard, [string]$DiagnosticsDirectory, [System.Collections.IList]$Entries, [System.Collections.IList]$OwnedProcessRecords, [object]$PostStartFaultGuard, [string]$RunSettingsPath) [void]$global:startAttempts.Add($Shard.Name); try { $entry = & $actualStart @PSBoundParameters; [void]$global:startReturns.Add($Shard.Name); foreach ($record in @($OwnedProcessRecords)) { if (-not $global:rawProcessIds.Contains([int]$record.ProcessId)) { [void]$global:rawProcessIds.Add([int]$record.ProcessId) }; if (-not $global:rawCommandIdentities.Contains([string]$record.CommandIdentity)) { [void]$global:rawCommandIdentities.Add([string]$record.CommandIdentity) } }; if ($scenario -eq 'partial-fanout-post-start-exception' -and $Shard.Name -ceq 'bass-collectible') { [void]$signal.Set() }; return $entry } catch { foreach ($record in @($OwnedProcessRecords)) { if (-not $global:rawProcessIds.Contains([int]$record.ProcessId)) { [void]$global:rawProcessIds.Add([int]$record.ProcessId) }; if (-not $global:rawCommandIdentities.Contains([string]$record.CommandIdentity)) { [void]$global:rawCommandIdentities.Add([string]$record.CommandIdentity) } }; throw } }",
            "function Convert-FunctionalRawOwnershipRecordsToEntries { param([System.Collections.IList]$Entries, [System.Collections.IList]$OwnedProcessRecords, [object]$CleanupFailures) $global:convertBefore = [pscustomobject]@{ RawRecords = @($OwnedProcessRecords).Count; Entries = @($Entries).Count }; $result = & $actualConvert @PSBoundParameters; $global:convertAfter = [pscustomobject]@{ RawRecords = @($OwnedProcessRecords).Count; Entries = @($Entries).Count }; return $result }",
            "function Invoke-VerificationFunctionalCleanup { param([object[]]$Entries, [DateTime]$CleanupDeadlineUtc, [switch]$StopRoots, [object]$PrimitiveObserver) $result = & $actualCleanup @PSBoundParameters; $global:cleanupSnapshot = [pscustomobject]@{ Entries = @($result.EntryResults).Count; RemainingOwnedProcessIds = @($result.EntryResults | ForEach-Object { @($_.Result.RemainingOwnedProcessIds) }); ResultErrors = @($result.EntryResults | Where-Object { $null -ne $_.Error }).Count; FanoutFailures = @($result.FanoutFailures).Count }; return $result }",
            "$diagnostics = Join-Path ([IO.Path]::GetTempPath()) ('bms-verification-raw-' + [Guid]::NewGuid().ToString('N'))",
            "$caught = $null",
            "try { Invoke-CanonicalFunctionalVerification -DiagnosticsRoot $diagnostics -TimeoutSeconds 10 *> $null } catch { $caught = $_.Exception }",
            "$residualProcessIds = @($global:rawProcessIds | Sort-Object -Unique | Where-Object { $null -ne (Get-Process -Id $_ -ErrorAction SilentlyContinue) })",
            "$output = [pscustomobject]@{ Scenario = $scenario; Caught = $null -ne $caught; ExceptionMessage = if ($null -eq $caught) { [string]::Empty } else { $caught.Message }; StartAttempts = @($global:startAttempts); StartReturns = @($global:startReturns); RawProcessIds = @($global:rawProcessIds); RawCommandIdentities = @($global:rawCommandIdentities); RawRecordsBeforeConvert = if ($null -eq $global:convertBefore) { -1 } else { $global:convertBefore.RawRecords }; EntriesBeforeConvert = if ($null -eq $global:convertBefore) { -1 } else { $global:convertBefore.Entries }; EntriesAfterConvert = if ($null -eq $global:convertAfter) { -1 } else { $global:convertAfter.Entries }; CleanupEntries = if ($null -eq $global:cleanupSnapshot) { -1 } else { $global:cleanupSnapshot.Entries }; CleanupRemainingOwnedProcessIds = if ($null -eq $global:cleanupSnapshot) { @() } else { @($global:cleanupSnapshot.RemainingOwnedProcessIds) }; CleanupResultErrors = if ($null -eq $global:cleanupSnapshot) { -1 } else { $global:cleanupSnapshot.ResultErrors }; CleanupFanoutFailures = if ($null -eq $global:cleanupSnapshot) { -1 } else { $global:cleanupSnapshot.FanoutFailures }; ResidualProcessIds = $residualProcessIds }",
            "if (Test-Path -LiteralPath $diagnostics) { Remove-Item -LiteralPath $diagnostics -Recurse -Force -ErrorAction SilentlyContinue }",
            "$output | ConvertTo-Json -Depth 12 -Compress");
        return ReadPowerShellJson(new[] { "-Command", command });
    }

    private static JsonDocument ReadFunctionalDeadlineBoundaryProbe(string boundary)
    {
        string repositoryRoot = FindRepositoryRoot();
        string lifecyclePath = QuotePowerShellLiteral(
            Path.Combine(repositoryRoot, "scripts", "verification-process-lifecycle.ps1"));
        string verifyScriptPath = QuotePowerShellLiteral(
            Path.Combine(repositoryRoot, "scripts", "verify-refactor.ps1"));
        string command = string.Join(
            Environment.NewLine,
            "$ErrorActionPreference = 'Stop'",
            $"$boundary = {QuotePowerShellLiteral(boundary)}",
            ". " + lifecyclePath,
            "$signal = [System.Threading.ManualResetEventSlim]::new($false)",
            "$guard = [VerificationPostStartFaultGuard]::new($signal)",
            ". " + verifyScriptPath + " -InternalTestGuard $guard",
            "$global:reportedPortableExit = $false",
            "$global:reportedDeadlineUtc = $null",
            "$global:fanoutAttempts = 0",
            "$global:fanoutObservationAfterDeadline = $false",
            "$global:reportedStartUtc = $null",
            "$actualPolicy = ${function:New-FunctionalDeadlinePolicy}",
            "$actualExit = ${function:Get-FunctionalProcessExitTimeUtc}",
            "function New-FunctionalDeadlinePolicy { param([DateTime]$StartUtc, [int]$TimeoutSeconds) $policy = & $actualPolicy @PSBoundParameters; $global:reportedStartUtc = $policy.StartUtc; $global:reportedDeadlineUtc = $policy.ExecutionDeadlineUtc; return $policy }",
            "function Get-FunctionalProcessExitTimeUtc { param([System.Diagnostics.Process]$Process) $actual = & $actualExit @PSBoundParameters; if ($null -eq $actual) { return $null }; if ($Process.Id -eq $global:portableProcessId) { if (-not $global:reportedPortableExit) { $global:reportedPortableExit = $true; return $null }; return $global:reportedStartUtc.AddSeconds(1) }; if ([DateTime]::UtcNow -lt $global:reportedDeadlineUtc) { return $null }; $global:fanoutObservationAfterDeadline = $true; if ($boundary -eq 'before') { return $global:reportedStartUtc.AddSeconds(2) }; return $global:reportedDeadlineUtc.AddTicks(1) }",
            "$actualStart = ${function:Start-FunctionalShardProcess}",
            "function Get-TestArguments { param([string]$Filter, [string]$DiagnosticsDirectory, [string]$RunSettingsPath, [switch]$NoBuild) return @('--version') }",
            "function Invoke-BudgetedCommand { param([System.Diagnostics.Stopwatch]$Stopwatch, [int]$BudgetSeconds, [string]$Label, [string]$CommandPath, [string[]]$Arguments, [string]$DiagnosticsDirectory, [DateTime]$ProcessDeadlineUtc, [DateTime]$PhaseDeadlineUtc, [DateTime]$CleanupDeadlineUtc, [switch]$IsTestCommand) }",
            "function Assert-BuiltOutputs { }",
            "function Assert-RepositoryWhitespace { }",
            "function Start-FunctionalShardProcess { param([pscustomobject]$Shard, [string]$DiagnosticsDirectory, [System.Collections.IList]$Entries, [System.Collections.IList]$OwnedProcessRecords, [object]$PostStartFaultGuard, [string]$RunSettingsPath) if ($Shard.Name -cne 'portable-settings') { $global:fanoutAttempts++ }; $entry = & $actualStart @PSBoundParameters; if ($Shard.Name -ceq 'portable-settings') { $global:portableProcessId = $entry.Process.Id }; return $entry }",
            "$diagnostics = Join-Path ([IO.Path]::GetTempPath()) ('bms-verification-boundary-' + [Guid]::NewGuid().ToString('N'))",
            "$caught = $null",
            "$capturedOutput = [System.Collections.Generic.List[object]]::new()",
            "try { Invoke-CanonicalFunctionalVerification -DiagnosticsRoot $diagnostics -TimeoutSeconds 10 *>&1 | ForEach-Object { [void]$capturedOutput.Add($_) } } catch { $caught = $_.Exception }",
            "$elapsedLine = @($capturedOutput | ForEach-Object { [string]$_ } | Where-Object { $_ -like 'Functional test execution elapsed:*' } | Select-Object -Last 1)",
            "$output = [pscustomobject]@{ Boundary = $boundary; FanoutAttempts = $global:fanoutAttempts; ObservationAfterDeadline = $global:fanoutObservationAfterDeadline; ElapsedLine = if ($elapsedLine.Count -eq 0) { [string]::Empty } else { [string]$elapsedLine[0] }; TimedOut = $null -ne $caught -and $caught.Message.Contains('exceeded the configured execution deadline', [StringComparison]::Ordinal); ExceptionMessage = if ($null -eq $caught) { [string]::Empty } else { $caught.Message } }",
            "if (Test-Path -LiteralPath $diagnostics) { Remove-Item -LiteralPath $diagnostics -Recurse -Force -ErrorAction SilentlyContinue }",
            "$output | ConvertTo-Json -Depth 8 -Compress");
        return ReadPowerShellJson(new[] { "-Command", command });
    }

    private static JsonDocument ReadFunctionalExitTimeReportProbe(
        int retainedExitSeconds,
        int timeoutSeconds,
        string? failureHostName = null)
    {
        string repositoryRoot = FindRepositoryRoot();
        string lifecyclePath = QuotePowerShellLiteral(
            Path.Combine(repositoryRoot, "scripts", "verification-process-lifecycle.ps1"));
        string verifyScriptPath = QuotePowerShellLiteral(
            Path.Combine(repositoryRoot, "scripts", "verify-refactor.ps1"));
        string failureHostLiteral = failureHostName is null
            ? "[string]::Empty"
            : QuotePowerShellLiteral(failureHostName);
        string command = string.Join(
            Environment.NewLine,
            "$ErrorActionPreference = 'Stop'",
            $"$retainedExitSeconds = {retainedExitSeconds}",
            $"$timeoutSeconds = {timeoutSeconds}",
            $"$failureHostName = {failureHostLiteral}",
            ". " + lifecyclePath,
            "$signal = [System.Threading.ManualResetEventSlim]::new($false)",
            "$guard = [VerificationPostStartFaultGuard]::new($signal)",
            ". " + verifyScriptPath + " -InternalTestGuard $guard",
            "$global:reportedStartUtc = $null",
            "$global:reportedDeadlineUtc = $null",
            "$global:portableProcessId = 0",
            "$global:fanoutAttempts = 0",
            "$global:cleanupDeadlineUtc = $null",
            "$global:stopRoots = $false",
            "$global:actualExit = ${function:Get-FunctionalProcessExitTimeUtc}",
            "$global:actualPolicy = ${function:New-FunctionalDeadlinePolicy}",
            "function New-FunctionalDeadlinePolicy { param([DateTime]$StartUtc, [int]$TimeoutSeconds) $policy = & $global:actualPolicy @PSBoundParameters; $global:reportedStartUtc = $policy.StartUtc; $global:reportedDeadlineUtc = $policy.ExecutionDeadlineUtc; return $policy }",
            "function Get-FunctionalProcessExitTimeUtc { param([System.Diagnostics.Process]$Process) $actual = & $global:actualExit @PSBoundParameters; if ($null -eq $actual) { return $null }; return $global:reportedStartUtc.AddSeconds($retainedExitSeconds) }",
            "$actualStart = ${function:Start-FunctionalShardProcess}",
            "function Get-TestArguments { param([string]$Filter, [string]$DiagnosticsDirectory, [string]$RunSettingsPath, [switch]$NoBuild) if (-not [string]::IsNullOrWhiteSpace($failureHostName) -and $DiagnosticsDirectory -like ('*' + $failureHostName)) { return @('--unknown-functional-probe') }; return @('--version') }",
            "function Invoke-BudgetedCommand { param([System.Diagnostics.Stopwatch]$Stopwatch, [int]$BudgetSeconds, [string]$Label, [string]$CommandPath, [string[]]$Arguments, [string]$DiagnosticsDirectory, [DateTime]$ProcessDeadlineUtc, [DateTime]$PhaseDeadlineUtc, [DateTime]$CleanupDeadlineUtc, [switch]$IsTestCommand) }",
            "function Assert-BuiltOutputs { }",
            "function Assert-RepositoryWhitespace { }",
            "function Start-FunctionalShardProcess { param([pscustomobject]$Shard, [string]$DiagnosticsDirectory, [System.Collections.IList]$Entries, [System.Collections.IList]$OwnedProcessRecords, [object]$PostStartFaultGuard, [string]$RunSettingsPath) if ($Shard.Name -cne 'portable-settings') { $global:fanoutAttempts++ }; $entry = & $actualStart @PSBoundParameters; if ($Shard.Name -ceq 'portable-settings') { $global:portableProcessId = $entry.Process.Id }; return $entry }",
            "$actualCleanup = ${function:Invoke-VerificationFunctionalCleanup}",
            "function Invoke-VerificationFunctionalCleanup { param([System.Collections.IList]$Entries, [DateTime]$CleanupDeadlineUtc, [switch]$StopRoots, [object]$PrimitiveObserver) $global:cleanupDeadlineUtc = $CleanupDeadlineUtc; $global:stopRoots = $StopRoots.IsPresent; return & $actualCleanup @PSBoundParameters }",
            "$capturedOutput = [System.Collections.Generic.List[object]]::new()",
            "$caught = $null",
            "$diagnostics = Join-Path ([IO.Path]::GetTempPath()) ('bms-verification-report-' + [Guid]::NewGuid().ToString('N'))",
            "try { Invoke-CanonicalFunctionalVerification -DiagnosticsRoot $diagnostics -TimeoutSeconds $timeoutSeconds *>&1 | ForEach-Object { [void]$capturedOutput.Add($_) } } catch { $caught = $_.Exception }",
            "$elapsedLine = @($capturedOutput | ForEach-Object { [string]$_ } | Where-Object { $_ -like 'Functional test execution elapsed:*' } | Select-Object -Last 1)",
            "$warnings = @($capturedOutput | Where-Object { $_ -is [System.Management.Automation.WarningRecord] } | ForEach-Object { $_.Message } | ForEach-Object { [string]$_ })",
            "$outputLines = @($capturedOutput | ForEach-Object { [string]$_ })",
            "$output = [pscustomobject]@{ Caught = $null -ne $caught; TimedOut = $null -ne $caught -and $caught.Message.Contains('exceeded the configured execution deadline', [StringComparison]::Ordinal); ExceptionMessage = if ($null -eq $caught) { [string]::Empty } else { $caught.Message }; ElapsedLine = if ($elapsedLine.Count -eq 0) { [string]::Empty } else { [string]$elapsedLine[0] }; Warnings = $warnings; OutputLines = $outputLines; FanoutAttempts = $global:fanoutAttempts; StartUtc = $global:reportedStartUtc; CleanupDeadlineUtc = $global:cleanupDeadlineUtc; StopRoots = $global:stopRoots }",
            "if (Test-Path -LiteralPath $diagnostics) { Remove-Item -LiteralPath $diagnostics -Recurse -Force -ErrorAction SilentlyContinue }",
            "$output | ConvertTo-Json -Depth 12 -Compress");
        return ReadPowerShellJson(new[] { "-Command", command });
    }

    private static void AssertSuccessfulExitTimeReport(JsonDocument result, int expectedElapsedSeconds)
    {
        JsonElement root = result.RootElement;
        Assert.IsFalse(GetProperty(root, "Caught").GetBoolean(), root.GetRawText());
        Assert.IsFalse(GetProperty(root, "TimedOut").GetBoolean(), root.GetRawText());
        Assert.AreEqual(
            expectedElapsedSeconds,
            ReadReportedElapsedSeconds(root),
            0.05,
            GetProperty(root, "ElapsedLine").GetString());
    }

    private static void AssertFunctionalReportingWarning(JsonDocument result, int expectedElapsedSeconds)
    {
        string warningText = string.Join(
            Environment.NewLine,
            ReadStringArray(GetProperty(result.RootElement, "Warnings")));
        if (string.IsNullOrWhiteSpace(warningText))
        {
            warningText = string.Join(
                Environment.NewLine,
                ReadStringArray(GetProperty(result.RootElement, "OutputLines")));
        }

        StringAssert.Contains(warningText, FunctionalReportingTargetSeconds.ToString(CultureInfo.InvariantCulture));
        Assert.IsTrue(warningText.Contains("target", StringComparison.OrdinalIgnoreCase), warningText);
        Assert.IsTrue(
            warningText.Contains(expectedElapsedSeconds.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal),
            warningText);
        Assert.IsTrue(warningText.Contains("retained", StringComparison.OrdinalIgnoreCase), warningText);
        Assert.IsTrue(warningText.Contains("user-facing report", StringComparison.OrdinalIgnoreCase), warningText);
    }

    private static double ReadReportedElapsedSeconds(JsonElement result)
    {
        string line = GetProperty(result, "ElapsedLine").GetString()!;
        const string prefix = "Functional test execution elapsed: ";
        int valueStart = line.IndexOf(prefix, StringComparison.Ordinal);
        Assert.IsTrue(valueStart >= 0, line);
        valueStart += prefix.Length;
        int valueEnd = line.IndexOf('s', valueStart);
        Assert.IsTrue(valueEnd > valueStart, line);
        string value = line[valueStart..valueEnd];
        Assert.IsTrue(
            double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) ||
            double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out seconds),
            line);
        return seconds;
    }

    private static void AssertRawFailureProbe(
        JsonDocument result,
        string expectedFailure,
        int expectedFanoutAttempts,
        int expectedSuccessfulStarts,
        int expectedRawRecords,
        int expectedEntriesBeforeConvert,
        int expectedEntriesAfterConvert)
    {
        JsonElement root = result.RootElement;
        Assert.IsTrue(GetProperty(root, "Caught").GetBoolean(), root.GetRawText());
        Assert.IsTrue(
            GetProperty(root, "ExceptionMessage").GetString()!.Contains(expectedFailure, StringComparison.Ordinal),
            root.GetRawText());
        string[] attempts = ReadStringArray(GetProperty(root, "StartAttempts"));
        Assert.AreEqual(expectedFanoutAttempts + 1, attempts.Length);
        Assert.AreEqual(expectedFanoutAttempts, attempts.Count(name => !name.Equals("portable-settings", StringComparison.Ordinal)));
        Assert.AreEqual(expectedSuccessfulStarts, GetArrayLengthOrZero(GetProperty(root, "StartReturns")), root.GetRawText());
        Assert.AreEqual(expectedRawRecords, GetArrayLengthOrZero(GetProperty(root, "RawProcessIds")), root.GetRawText());
        Assert.AreEqual(expectedRawRecords, GetProperty(root, "RawRecordsBeforeConvert").GetInt32());
        Assert.AreEqual(expectedEntriesBeforeConvert, GetProperty(root, "EntriesBeforeConvert").GetInt32());
        Assert.AreEqual(expectedEntriesAfterConvert, GetProperty(root, "EntriesAfterConvert").GetInt32());
        Assert.AreEqual(expectedEntriesAfterConvert, GetProperty(root, "CleanupEntries").GetInt32());
        Assert.AreEqual(0, GetArrayLengthOrZero(GetProperty(root, "CleanupRemainingOwnedProcessIds")), root.GetRawText());
        Assert.AreEqual(0, GetProperty(root, "CleanupResultErrors").GetInt32());
        Assert.AreEqual(0, GetProperty(root, "CleanupFanoutFailures").GetInt32());
        Assert.AreEqual(0, GetArrayLengthOrZero(GetProperty(root, "ResidualProcessIds")), root.GetRawText());
    }

    private static int GetArrayLengthOrZero(JsonElement element)
    {
        return element.ValueKind is JsonValueKind.Null ? 0 : element.GetArrayLength();
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
        Assert.IsTrue(process.Start(), "The PowerShell contract process did not start.");
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
        Task<string> errorTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(processTimeoutMilliseconds))
        {
            string cleanup = StopProcessTree(process, cleanupTimeoutMilliseconds);
            Assert.Fail(
                $"The PowerShell contract process exceeded the {processTimeoutMilliseconds / 1000}-second timeout. " +
                $"Process cleanup: {cleanup}. stdout: {GetCompletedTaskValue(outputTask)} stderr: {GetCompletedTaskValue(errorTask)}");
            return default;
        }

        try
        {
            if (!Task.WaitAll(new Task[] { outputTask, errorTask }, streamTimeoutMilliseconds))
            {
                string cleanup = StopProcessTree(process, cleanupTimeoutMilliseconds);
                Assert.Fail(
                    $"The PowerShell contract output did not close within the {streamTimeoutMilliseconds / 1000}-second timeout. " +
                    $"Process cleanup: {cleanup}. stdout: {GetCompletedTaskValue(outputTask)} stderr: {GetCompletedTaskValue(errorTask)}");
                return default;
            }
        }
        catch (Exception exception) when (exception is not AssertFailedException)
        {
            string cleanup = StopProcessTree(process, cleanupTimeoutMilliseconds);
            Assert.Fail($"The PowerShell contract output failed: {exception}. Process cleanup: {cleanup}");
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

    private static JsonElement GetProperty(JsonElement element, string name)
    {
        Assert.IsTrue(element.TryGetProperty(name, out JsonElement value), $"Contract property was not found: {name}");
        return value;
    }

    private static JsonElement GetProperty(JsonElement array, int index, string name)
    {
        return GetProperty(array[index], name);
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

    private static int[] ReadIntegerArray(JsonElement array)
    {
        var values = new List<int>();
        foreach (JsonElement value in array.EnumerateArray())
        {
            values.Add(value.GetInt32());
        }
        return values.ToArray();
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
