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
    public void FunctionalShardPlan_UsesExecutablePlanForExactHostOwnership()
    {
        using JsonDocument plan = ReadFunctionalShardPlan();
        JsonElement shards = GetProperty(plan.RootElement, "Shards");

        Assert.AreEqual(5, shards.GetArrayLength());
        CollectionAssert.AreEqual(
            new[]
            {
                "portable-settings",
                "bass-collectible",
                "serial-state-a",
                "serial-state-b",
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
        CollectionAssert.AreEqual(
            expectedExclusions,
            ReadStringArray(GetProperty(remaining, "ExcludedClasses")));
        foreach (string selector in expectedExclusions)
        {
            Assert.IsTrue(
                GetProperty(remaining, "Filter").GetString()!.Contains(
                    $"FullyQualifiedName!~{selector}",
                    StringComparison.Ordinal));
        }

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
