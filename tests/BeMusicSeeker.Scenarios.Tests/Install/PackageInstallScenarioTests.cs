using System.IO;
using BeMusicSeeker.Runtime;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Processes;
using BeMusicSeeker.TestSupport.Scenarios;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Scenarios.Tests.Install;

/// <summary>通常の入力確保・保留操作・導入と次の祖先推定を実製品で検証します。</summary>
[TestClass]
public sealed class PackageInstallScenarioTests
{
    /// <summary>保証: SC-02。ZIPと手動差分導入が実FS/DB/表示を更新し、次Dropは再走査せず更新済み祖先資源から親を推定します。</summary>
    /// <remarks>手動先保持の条件表と削除設定trueの残存入力削除は下位Componentへ分担します。管理temp保留の再起動復元は期待しません。</remarks>
    [TestMethod(DisplayName = "[SC-02] ZIPと手動差分導入が保存され次の親候補推定へ反映する")]
    [TestCategory("Daily")]
    public async Task Drop_ManualInstallAndNextEstimate_PreserveFilesAndUpdateAncestor()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        try
        {
            await using var fixture = Sc02InstallFixture.Create(workspace);
            var deadline = ProcessDeadline.FromTimeout(TimeSpan.FromMinutes(3));
            await RunAsync(fixture, workspace, deadline, async app =>
            {
                AssertStartup(await app.WaitForStartupAsync(deadline));
                Sc02InstallOracle.AssertState(fixture, Sc02InstallPhase.Startup);
                ScenarioDropCompleted initial = await app.DropAsync([fixture.InitialArchivePath], deadline);
                AssertDrop(initial);
                ScenarioPackageViewCompleted initialPending = await app.NavigatePackagesAsync(true, deadline);
                string[] archiveHashes = fixture.InitialInstalledCharts.Where(chart => chart != fixture.ExistingChart).Select(chart => chart.Md5).ToArray();
                ScenarioPackageChart[] initialRows = initialPending.Rows.Where(row => archiveHashes.Contains(row.Chart.Md5)).ToArray();
                Assert.AreEqual(2, initialRows.Length);
                Assert.IsTrue(initialRows.Any(row => row.WarningKinds.Contains("NestedChartFileInPackage")));
                Assert.IsFalse(initialRows.Any(row => row.WarningKinds.Contains("ResourceBearingNestedCharts")));
                CollectionAssert.AreEquivalent(new[] { Path.GetFullPath(Path.Combine(fixture.LibraryRoot, fixture.ExistingChart.RelativePath)) },
                    (await app.SnapshotAsync(deadline)).Snapshot.Catalog.Select(chart => chart.Path).ToArray());
                ScenarioPendingCompleted initialInstalled = await app.ApplyPendingAsync(ScenarioPendingAction.InstallAsNew,
                    initialRows.Select(row => row.Chart.Path), deadline);
                Assert.IsNotNull(initialInstalled.Mutation);
                Assert.IsTrue(initialInstalled.Mutation.Succeeded);
                Assert.IsFalse(initialInstalled.Mutation.HasInstallRefusal);
                Assert.IsNull(initialInstalled.Mutation.Failure);
                Assert.IsNotNull(initialInstalled.Mutation.Session);
                AssertReceipt(initialInstalled.Mutation.Session);
                Assert.IsTrue(initialInstalled.Mutation.Session.DurableCommit);
                Sc02InstallOracle.AssertState(fixture, Sc02InstallPhase.InitialInstalled);
                Sc02InstallOracle.AssertFileMoves(fixture, Sc02InstallPhase.InitialInstalled,
                    initialInstalled.Mutation.Session.ConfirmedTargets.Select(target => new Sc02FileMoveObservation(target.SourcePath, target.DestinationPath)).ToArray());
                ScenarioPackageViewCompleted newly = await app.NavigatePackagesAsync(false, deadline);
                CollectionAssert.AreEquivalent(fixture.InitialInstalledCharts.Where(chart => chart != fixture.ExistingChart)
                    .Select(chart => Path.GetFullPath(Path.Combine(fixture.LibraryRoot, chart.RelativePath))).ToArray(),
                    newly.Rows.Select(row => row.Chart.Path).ToArray());

                AssertDrop(await app.DropAsync([fixture.DifferenceSourcePath], deadline));
                Sc02InstallOracle.AssertState(fixture, Sc02InstallPhase.DifferencePending);
                string[] paths = fixture.DifferenceCharts.Select(chart => Path.GetFullPath(Path.Combine(fixture.DifferenceSourcePath, chart.RelativePath))).ToArray();
                ScenarioPendingCompleted cleared = await app.ApplyPendingAsync(ScenarioPendingAction.Clear, paths, deadline);
                AssertDestinations(cleared.View, paths, null);
                ScenarioPendingCompleted searched = await app.ApplyPendingAsync(ScenarioPendingAction.Search, paths, deadline);
                AssertDestinations(searched.View, paths, fixture.ChildDirectory);
                // 推定値を残したまま同じ値を指定せず、手動指定自身の配送と変更を確認します。
                AssertDestinations((await app.ApplyPendingAsync(ScenarioPendingAction.Clear, paths, deadline)).View, paths, null);
                ScenarioPendingCompleted set = await app.ApplyPendingAsync(ScenarioPendingAction.Set, [paths[0]], deadline, fixture.ChildDirectory);
                AssertDestinations(set.View, paths, fixture.ChildDirectory);
                ScenarioPendingCompleted installed = await app.ApplyPendingAsync(ScenarioPendingAction.Install, paths, deadline);
                Assert.IsNotNull(installed.Mutation);
                Assert.IsTrue(installed.Mutation.Succeeded);
                Assert.IsFalse(installed.Mutation.HasInstallRefusal);
                Assert.IsNull(installed.Mutation.Failure);
                Assert.IsNotNull(installed.Mutation.Session);
                AssertReceipt(installed.Mutation.Session);
                Assert.IsTrue(installed.Mutation.Session.DurableCommit);
                Assert.IsFalse(installed.View.Rows.Any(row => paths.Contains(row.Chart.Path, StringComparer.OrdinalIgnoreCase)));
                Sc02InstallOracle.AssertState(fixture, Sc02InstallPhase.DifferenceInstalled);
                Sc02InstallOracle.AssertFileMoves(fixture, Sc02InstallPhase.DifferenceInstalled,
                    installed.Mutation.Session.ConfirmedTargets.Select(target => new Sc02FileMoveObservation(target.SourcePath, target.DestinationPath)).ToArray());

                // Reloadや構成file再scanを挟まず、直前の導入が祖先へ反映した資源を次の実Dropで使います。
                AssertDrop(await app.DropAsync([fixture.NextDifferencePath], deadline));
                ScenarioPackageViewCompleted next = await app.NavigatePackagesAsync(true, deadline);
                AssertDestinations(next, [fixture.NextDifferencePath], fixture.InstalledWorkDirectory);
                Sc02InstallOracle.AssertState(fixture, Sc02InstallPhase.NextDifferencePending);
                AssertCatalog((await app.SnapshotAsync(deadline)).Snapshot, fixture);
                await app.CloseAsync(deadline);
            });
            await RunAsync(fixture, workspace, deadline, async app =>
            {
                AssertStartup(await app.WaitForStartupAsync(deadline));
                Sc02InstallOracle.AssertState(fixture, Sc02InstallPhase.Restarted);
                AssertCatalog((await app.SnapshotAsync(deadline)).Snapshot, fixture);
                await app.CloseAsync(deadline);
            });
        }
        catch { workspace.PreserveArtifacts(); throw; }
    }

    private static async Task RunAsync(Sc02InstallFixture fixture, TestWorkspace workspace, ProcessDeadline deadline,
        Func<ApplicationScenarioDriver, Task> body)
    {
        string executable = ScenarioApplicationPaths.ExecutablePath;
        ApplicationScenarioDriver app = await ApplicationScenarioDriver.StartAsync(executable, fixture.ProfileDirectory,
            workspace.GetPath("session.json"), workspace.CaseId, fixture.UpdateManifestUrl, deadline);
        Exception? primary = null;
        try { await body(app); }
        catch (Exception failure) { primary = failure; throw; }
        finally
        {
            try { await app.DisposeAsync(); }
            catch (Exception cleanup) when (primary is not null) { primary.Data["ScenarioCleanupFailure"] = cleanup; }
        }
    }

    private static void AssertStartup(ScenarioInitializationResult result)
    {
        Assert.AreEqual(StartupInitializationOutcome.Succeeded, result.Outcome);
        Assert.IsTrue(result.CompletionPublished && result.IsInitializationCompleted);
        Assert.IsTrue(result.OperationToken > 0);
        Assert.IsNull(result.Failure);
    }

    private static void AssertDrop(ScenarioDropCompleted result)
    {
        Assert.AreNotEqual(Guid.Empty, result.BatchId);
        Assert.IsTrue(result.Generation > 0);
        Assert.IsNull(result.Failure, result.Failure?.StackTrace);
        Assert.IsNotNull(result.Result);
        Assert.IsFalse(result.Result.HasRequiredFailure);
        Assert.IsFalse(result.Result.SynchronizationIncomplete);
        Assert.AreEqual(0, result.Result.UnprocessedPackageCount);
        Assert.AreEqual(0, result.Result.Failures.Length);
        AssertReceipt(result.Result.Initial);
        if (result.Result.Estimated is not null) { AssertReceipt(result.Result.Estimated); }
    }

    private static void AssertReceipt(ScenarioMutationReceipt receipt)
    {
        Assert.IsFalse(receipt.HasRequiredFailure || receipt.HasUnrestoredExistingDestination || receipt.ManualRecoveryRequired || receipt.CompletedWithCleanupFailure);
        Assert.AreEqual(0, receipt.DestinationConflictCount);
        Assert.AreEqual(0, receipt.UnprocessedTargets.Length);
        Assert.AreEqual(0, receipt.Failures.Length);
    }

    private static void AssertDestinations(ScenarioPackageViewCompleted view, IReadOnlyList<string> paths, string? destination)
    {
        Assert.IsTrue(view.Pending);
        Assert.IsTrue(view.RowsGeneration > 0);
        foreach (string path in paths)
        {
            ScenarioPackageChart row = view.Rows.Single(row => string.Equals(row.Chart.Path, path, StringComparison.OrdinalIgnoreCase));
            if (destination is null) { Assert.IsTrue(string.IsNullOrEmpty(row.Destination), path); }
            else { Assert.AreEqual(Path.GetFullPath(destination), Path.GetFullPath(row.Destination ?? string.Empty), true, path); }
        }
    }

    private static void AssertCatalog(ScenarioLibrarySnapshot snapshot, Sc02InstallFixture fixture)
    {
        CollectionAssert.AreEquivalent(fixture.DifferenceInstalledCharts.Select(chart => Path.GetFullPath(Path.Combine(fixture.LibraryRoot, chart.RelativePath))).ToArray(),
            snapshot.Catalog.Select(chart => chart.Path).ToArray());
    }
}
